using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ReadingPanel.Integration;

// All Locked methods run between OpenClipboard and CloseClipboard. Materialize each format
// before sending Copy: an IDataObject backed by the live clipboard is not a snapshot.
internal sealed class ClipboardSnapshot : IDisposable
{
    private readonly Dictionary<uint, STGMEDIUM> formats = new();

    internal static ClipboardSnapshot? CaptureLocked()
    {
        var snapshot = new ClipboardSnapshot();
        bool complete = false;
        try
        {
            int count = CountClipboardFormats();
            var ids = new List<uint>(count);
            for (uint id = EnumClipboardFormats(0); id != 0; id = EnumClipboardFormats(id))
            {
                if (!CanSnapshotFormat(id)) return null;
                ids.Add(id);
            }
            if (ids.Count != count) return null;
            long bytes = 0;
            foreach (uint id in ids)
            {
                TYMED medium = id switch {
                    2 or 9 or 0x82 => TYMED.TYMED_GDI, 3 or 0x83 => TYMED.TYMED_MFPICT,
                    14 or 0x8E => TYMED.TYMED_ENHMF, _ => TYMED.TYMED_HGLOBAL
                };
                var handle = GetClipboardData(id);
                if (handle == IntPtr.Zero) return null;
                long size = StorageSize(id, medium, handle);
                if (size <= 0 || size > 64L * 1024 * 1024 - bytes) return null;
                bytes += size;
                var copy = OleDuplicateData(handle, (ushort)id, 0);
                if (copy == IntPtr.Zero) return null;
                snapshot.formats.Add(id, new STGMEDIUM { tymed = medium, unionmember = copy });
            }
            complete = snapshot.formats.Count == count;
            return complete ? snapshot : null;
        }
        finally { if (!complete) snapshot.Dispose(); }
    }

    internal static bool CanSnapshotFormat(uint id) => id != 0x80 && id is not (>= 0x200 and < 0x400);

    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap { public int Type, Width, Height, Stride; public ushort Planes, Depth; public IntPtr Bits; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MetafilePicture { public int Mode, X, Y; public IntPtr Metafile; }

    private static long StorageSize(uint id, TYMED medium, IntPtr handle)
    {
        if (medium == TYMED.TYMED_HGLOBAL) return (long)GlobalSize(handle);
        if (medium == TYMED.TYMED_ENHMF) return GetEnhMetaFileBits(handle, 0, IntPtr.Zero);
        if (medium == TYMED.TYMED_MFPICT)
        {
            var data = GlobalLock(handle);
            if (data == IntPtr.Zero) return 0;
            try { return GetMetaFileBitsEx(Marshal.PtrToStructure<MetafilePicture>(data).Metafile, 0, IntPtr.Zero); }
            finally { GlobalUnlock(handle); }
        }
        if (id == 9) return GetPaletteEntries(handle, 0, 0, IntPtr.Zero) * 4L;
        if (GetObject(handle, Marshal.SizeOf<Bitmap>(), out var bitmap) == 0) return 0;
        return checked((long)bitmap.Stride * Math.Abs((long)bitmap.Height) * bitmap.Planes);
    }

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr handle, int size, out Bitmap bitmap);
    [DllImport("gdi32.dll")] private static extern uint GetPaletteEntries(IntPtr handle, uint start, uint count, IntPtr entries);
    [DllImport("gdi32.dll")] private static extern uint GetEnhMetaFileBits(IntPtr handle, uint count, IntPtr data);
    [DllImport("gdi32.dll")] private static extern uint GetMetaFileBitsEx(IntPtr handle, uint count, IntPtr data);

    internal void RestoreLocked()
    {
        if (!EmptyClipboard()) throw new IOException("无法恢复剪贴板。");
        foreach (var (format, data) in formats.ToArray())
        {
            if (SetClipboardData(format, data.unionmember) == IntPtr.Zero) throw new IOException("无法恢复剪贴板格式。");
            formats.Remove(format); // Ownership of this handle passed to Windows.
        }
    }

    internal static string? ReadStringLocked(uint format, int limit)
    {
        var data = GetClipboardData(format);
        int chars = (int)Math.Min((ulong)GlobalSize(data) / 2, (ulong)limit + 1);
        if (chars == 0) return null;
        var pointer = GlobalLock(data);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            string raw = Marshal.PtrToStringUni(pointer, chars)!;
            int end = raw.IndexOf('\0');
            return end < 0 ? null : raw[..end];
        }
        finally { GlobalUnlock(data); }
    }

    public void Dispose()
    {
        foreach (var data in formats.Values)
        {
            var owned = data;
            ReleaseStgMedium(ref owned);
        }
        formats.Clear();
    }

    [DllImport("user32.dll")] internal static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] internal static extern bool CloseClipboard();
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] internal static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern int CountClipboardFormats();
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(IntPtr data);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr data);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr data);
    [DllImport("ole32.dll")] private static extern IntPtr OleDuplicateData(IntPtr data, ushort format, uint flags);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
