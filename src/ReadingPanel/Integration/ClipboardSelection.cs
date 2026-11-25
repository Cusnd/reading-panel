using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ReadingPanel.Core;
using static ReadingPanel.Integration.ClipboardSnapshot;

namespace ReadingPanel.Integration;

internal sealed class ClipboardSelection : IDisposable
{
    private static readonly uint Metadata = RegisterClipboardFormat("GPUI internal metadata");
    private static readonly uint TextHash = RegisterClipboardFormat("GPUI internal text hash");
    private readonly object copyLock = new();
    private bool disposed;
    private readonly IntPtr clipboardWindow;
    private readonly AutoResetEvent changed = new(false);
    private readonly bool listening;

    internal ClipboardSelection(IntPtr window)
    {
        clipboardWindow = window;
        listening = AddClipboardFormatListener(window);
    }

    internal void ClipboardUpdated() => changed.Set();
    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr window);

    private static bool IsZed(uint processId)
    {
        if (processId == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals("Zed", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal static string? ZedText(string? metadata, string? text)
    {
        if (metadata == null || text == null || text.Length > 8000 || string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var json = JsonDocument.Parse(metadata);
            var entries = json.RootElement;
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != 1) return null;
            var item = entries[0];
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("is_entire_line", out var line) || line.ValueKind != JsonValueKind.False ||
                !item.TryGetProperty("len", out var length) || length.ValueKind != JsonValueKind.Number || !length.TryGetInt32(out int bytes)) return null;
            // Zed counts UTF-8 bytes before converting LF to Windows clipboard line endings.
            return Encoding.UTF8.GetByteCount(text.Replace("\r\n", "\n")) == bytes ? text.Trim() : null;
        }
        catch (JsonException) { return null; }
    }

    internal Selection? Read(IntPtr source, Func<bool> current, Func<Selection?> readAutomation, TextSelectionGesture? gesture,
        WindowsReader.Point? click = null)
    {
        if (source == IntPtr.Zero || !current()) return null;
        WindowsReader.GetWindowThreadProcessId(source, out uint process);
        bool zed = IsZed(process);
        bool highlighted = gesture?.Window == source;
        bool nativeEditor = highlighted && !zed && WindowsReader.IsNativeTextEditor(WindowsReader.HitWindow(gesture!.End));
        if (!highlighted || nativeEditor)
        {
            // Keyboard-created selections can be available without a preceding mouse gesture.
            try
            {
                var selection = readAutomation();
                if (selection != null) return selection;
            }
            catch (Exception) when (zed || nativeEditor) { /* Zed can still confirm a selection from its copy metadata. */ }
            if (!zed && !highlighted) return null;
        }
        if (!current() || WindowsReader.PasswordAtSelection(source, highlighted ? gesture!.End : null)) return null;
        uint clientProcess = 0;
        if (highlighted)
        {
            var client = WindowsReader.HitWindow(gesture!.End);
            if (WindowsReader.RootWindow(client) == source) WindowsReader.GetWindowThreadProcessId(client, out clientProcess);
        }
        string? text = ReadCopy(source, current, click == null, zed, clientProcess);
        if (text == null) return highlighted && !nativeEditor && current() ? readAutomation() : null;
        if (click is not { } point) WindowsReader.GetCursorPos(out point);
        return new(text, new(point.X, point.Y, point.Y), click == null);
    }

    private string? ReadCopy(IntPtr source, Func<bool> current, bool keyboard, bool zed, uint clientProcess)
    {
        if (!Monitor.TryEnter(copyLock)) return null;
        try
        {
            if (disposed) return null;
            if (keyboard)
                for (int i = 0; i < 60 && !ModifiersReleased() && current(); i++) Thread.Sleep(10);
            bool useInsert = zed || WindowsReader.IsTerminalWindow(source);
            return CopySelection(source, current, () => ModifiersReleased() && SendCopy(useInsert), zed, clientProcess);
        }
        finally { Monitor.Exit(copyLock); }
    }

    internal static string? PlainText(string? text) =>
        text is { Length: <= 8000 } && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    internal string? CopySelection(IntPtr source, Func<bool> current, Func<bool> sendCopy, bool zed = false, uint clientProcess = 0)
    {
        if (!current()) return null;
        long openUntil = Environment.TickCount64 + 70;
        while (!OpenClipboard(clipboardWindow))
        {
            if (!current() || Environment.TickCount64 >= openUntil) return null;
            changed.WaitOne(10);
        }
        ClipboardSnapshot? snapshot;
        uint before;
        try { snapshot = CaptureLocked(); before = GetClipboardSequenceNumber(); }
        finally { CloseClipboard(); }
        using (snapshot)
        {
            WindowsReader.GetWindowThreadProcessId(source, out uint sourcePid);
            if (snapshot == null || !current() || GetClipboardSequenceNumber() != before || !sendCopy()) return null;
            // Once Copy has been sent, finish restoring even if the user releases the button or changes windows.
            if (!OpenChangedClipboard(() => GetClipboardSequenceNumber() != before, () => OpenClipboard(clipboardWindow),
                    changed, listening, 600)) return null;
            try
            {
                var owner = GetClipboardOwner();
                WindowsReader.GetWindowThreadProcessId(owner, out uint ownerPid);
                if (owner != IntPtr.Zero && ownerPid != sourcePid && ownerPid != clientProcess) return null;
                bool zedCopy = zed && IsClipboardFormatAvailable(Metadata) && IsClipboardFormatAvailable(TextHash);
                // Some readers publish without an owner HWND. Ignore an unattributed change after switching apps.
                if (owner == IntPtr.Zero && !zedCopy && (zed || WindowsReader.GetForegroundWindow() != source)) return null;
                string? text = IsClipboardFormatAvailable(13) ? ReadStringLocked(13, 8000) : null;
                string? selected = zed
                    ? ZedText(zedCopy ? ReadStringLocked(Metadata, 16384) : null, text)
                    : PlainText(text);
                snapshot.RestoreLocked(); // Still holding the clipboard lock on the copy just inspected.
                return current() ? selected : null;
            }
            finally { CloseClipboard(); }
        }
    }

    internal static bool OpenChangedClipboard(Func<bool> published, Func<bool> open, WaitHandle changed, bool listening, int budgetMs)
    {
        long deadline = Environment.TickCount64 + budgetMs;
        while (true)
        {
            bool ready = published();
            if (ready && open()) return true;
            int remaining = (int)(deadline - Environment.TickCount64);
            if (remaining <= 0) return false;
            // Notification can precede release of the owner's clipboard lock.
            changed.WaitOne(ready || !listening ? Math.Min(20, remaining) : remaining);
        }
    }

    internal static bool ModifiersReleased() => GetAsyncKeyState(0x10) >= 0 && GetAsyncKeyState(0x11) >= 0 &&
        GetAsyncKeyState(0x12) >= 0 && GetAsyncKeyState(0x5B) >= 0 && GetAsyncKeyState(0x5C) >= 0;

    public void Dispose()
    {
        if (listening) RemoveClipboardFormatListener(clipboardWindow);
        lock (copyLock) { disposed = true; changed.Dispose(); }
    }

    private static bool SendCopy(bool useInsert)
    {
        // Ctrl+Insert avoids interrupting a command in terminal windows.
        ushort key = useInsert ? (ushort)0x2D : (ushort)'C';
        uint extended = useInsert ? 1u : 0u;
        Input[] chord = [Key(0x11, 0), Key(key, extended), Key(key, extended | 2), Key(0x11, 2)];
        int size = Marshal.SizeOf<Input>();
        uint sent = SendInput((uint)chord.Length, chord, size);
        if (sent > 0 && sent < chord.Length)
        {
            Input[] release = sent == 2 ? [chord[2], chord[3]] : [chord[3]];
            SendInput((uint)release.Length, release, size);
        }
        return sent >= 2;
    }

    private static Input Key(ushort code, uint flags) => new() { Type = 1, Data = new() { Key = new() { Code = code, Flags = flags } } };
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData {
        [FieldOffset(0)] public KeyInput Key;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort Code, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
