using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using ReadingPanel.Core;

namespace ReadingPanel.Integration;

internal static class WindowsReader
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public Rect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")] internal static extern IntPtr HitWindow(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr window, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder? name, int bytes, out int required);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr order, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetStyle(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] internal static extern int SetStyle(IntPtr window, int index, int value);

    internal static IntPtr RootWindow(IntPtr window) => GetAncestor(window, 2); // GA_ROOT
    internal static IntPtr WindowAt(Point point) => RootWindow(HitWindow(point));

    internal static bool IsClientPoint(IntPtr window, Point point) =>
        GetClientRect(window, out var rect) && ScreenToClient(window, ref point) &&
        point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    internal static int MouseKeys() =>
        (GetAsyncKeyState(1) < 0 ? 1 : 0) | (GetAsyncKeyState(2) < 0 ? 2 : 0) |
        (GetAsyncKeyState(0x10) < 0 ? 4 : 0) | (GetAsyncKeyState(0x11) < 0 ? 8 : 0) |
        (GetAsyncKeyState(5) < 0 ? 0x20 : 0) | (GetAsyncKeyState(6) < 0 ? 0x40 : 0);

    internal static void PostMiddle(IntPtr window, int kind, Point point, int keys)
    {
        if (ScreenToClient(window, ref point))
            PostMessage(window, kind, keys | (kind == DesktopMouse.MiddleDown ? 0x10 : 0), (point.Y << 16) | (point.X & 0xffff));
    }

    private static readonly nuint ReplayMarker = (nuint)(0x52500000u | (uint)Environment.ProcessId);
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public uint Type; public MouseData Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct HookData { public Point Point; public uint Data, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, MouseInput[] inputs, int bytes);

    internal static bool IsReplayedMouse(IntPtr data) => Marshal.PtrToStructure<HookData>(data).Extra == ReplayMarker;

    internal static bool ReplayClickAt(IntPtr source, IntPtr client, Point point, int keys)
    {
        if (!GetCursorPos(out var cursor) || cursor.X != point.X || cursor.Y != point.Y || MouseKeys() != keys ||
            GetAsyncKeyState(4) < 0 || GetForegroundWindow() != source || HitWindow(cursor) != client) return false;
        MouseInput down = new() { Mouse = new() { Flags = 0x20, Extra = ReplayMarker } };
        MouseInput up = new() { Mouse = new() { Flags = 0x40, Extra = ReplayMarker } };
        int size = Marshal.SizeOf<MouseInput>();
        uint sent = SendInput(2, [down, up], size);
        if (sent == 1 && SendInput(1, [up], size) == 0) PostMiddle(client, DesktopMouse.MiddleUp, point, keys);
        return sent != 0;
    }

    private static readonly TreeWalker WindowHandles = new(new NotCondition(
        new PropertyCondition(AutomationElement.NativeWindowHandleProperty, 0)));

    private static AutomationElement? FocusIn(IntPtr source)
    {
        var focus = AutomationElement.FocusedElement;
        if (focus == null) return null;
        var owner = WindowHandles.Normalize(focus);
        return owner != null && GetAncestor(new IntPtr(owner.Current.NativeWindowHandle), 2) == source ? focus : null;
    }

    internal static bool PasswordAtSelection(IntPtr source, Point? end)
    {
        var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (GetGUIThreadInfo(GetWindowThreadProcessId(source, out _), ref gui) && NativePassword(gui.Focus)) return true;
        var hit = end is { } p ? HitWindow(p) : IntPtr.Zero;
        if (hit != IntPtr.Zero && NativePassword(hit)) return true;
        try
        {
            if (AutomationElement.FocusedElement?.Current.IsPassword == true) return true;
            return end is { } point && RootWindow(hit) == source &&
                AutomationElement.FromPoint(new(point.X, point.Y))?.Current.IsPassword == true;
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or NotSupportedException or COMException)
        {
            // Custom-drawn readers can implement Copy without an accessibility provider.
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public int Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
    private static string WindowClass(IntPtr window)
    {
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }
    private static bool NativePassword(IntPtr window) => window != IntPtr.Zero && (GetStyle(window, -16) & 0x20) != 0 &&
        WindowClass(window).Contains("Edit", StringComparison.OrdinalIgnoreCase); // ES_PASSWORD
    internal static bool IsChromiumWindow(IntPtr window) => WindowClass(window).StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);

    internal static bool IsNativeTextEditor(IntPtr window)
    {
        string name = WindowClass(window);
        return name.Equals("Edit", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsTerminalWindow(IntPtr window) =>
        WindowClass(window) is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "mintty";

    internal static void PrepareLayout(IntPtr window, Point? location)
    {
        if (location == null)
        {
            if (!GetClientRect(window, out var client)) return;
            var center = new Point { X = client.Right / 2, Y = client.Bottom / 2 };
            if (!ClientToScreen(window, ref center)) return;
            location = center;
        }
        var pixel = location.Value;
        if (GetForegroundWindow() != window || WindowAt(pixel) != window) return;
        var point = new System.Windows.Point(pixel.X, pixel.Y);
        var provider = TextProviders(AutomationElement.FromPoint(point), window, true).FirstOrDefault();
        if (provider == null) return;
        // Request a character's layout only. No selection is changed and no text is retrieved.
        var character = provider.RangeFromPoint(point);
        character.ExpandToEnclosingUnit(TextUnit.Character);
        _ = character.GetBoundingRectangles();
    }

    internal static string DesktopSuffix()
    {
        static string Name(IntPtr handle)
        {
            GetUserObjectInformation(handle, 2, null, 0, out int bytes); // UOI_NAME
            if (bytes <= 0) throw new InvalidOperationException("Windows could not identify the current desktop.");
            var name = new StringBuilder(bytes / sizeof(char));
            if (!GetUserObjectInformation(handle, 2, name, bytes, out _))
                throw new InvalidOperationException("Windows could not read the current desktop name.");
            return name.ToString();
        }
        string station = Name(GetProcessWindowStation()), desktop = Name(GetThreadDesktop(GetCurrentThreadId()));
        // Keep the original host name for existing installations on the interactive desktop.
        if (station.Equals("WinSta0", StringComparison.OrdinalIgnoreCase) && desktop.Equals("Default", StringComparison.OrdinalIgnoreCase)) return "";
        return "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(station + "\\" + desktop)))[..16];
    }

    public static Anchor BrowserAnchor(int[] offsets, double[]? point)
    {
        GetCursorPos(out var cursor);
        // Chrome reports screen coordinates relative to the browser window's display. Capture the click
        // in that display's work area so moving the pointer during the native handoff doesn't shift the card.
        if (point is [>= 0 and <= 1, >= 0 and <= 1])
        {
            var monitor = MonitorFromWindow(GetForegroundWindow(), 0);
            if (monitor == IntPtr.Zero) monitor = MonitorFromPoint(cursor, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var work = info.Work;
                cursor.X = work.Left + (int)Math.Round(point[0] * (work.Right - work.Left));
                cursor.Y = work.Top + (int)Math.Round(point[1] * (work.Bottom - work.Top));
            }
        }
        return new(cursor.X, cursor.Y + offsets[1], cursor.Y + offsets[3]);
    }

    // Called on an MTA worker only. UI Automation can block in the source application.
    public static Selection? ReadSelection(IntPtr source)
    {
        if (source == IntPtr.Zero || GetForegroundWindow() != source) return null;
        var selection = ReadControl(FocusIn(source), source);
        return GetForegroundWindow() == source ? selection : null;
    }

    public static Selection? ReadAtPoint(IntPtr source, Point click, long retryUntil = 0, Func<bool>? current = null)
    {
        bool SameWindow() => source != IntPtr.Zero && (current == null || current()) && GetForegroundWindow() == source && WindowAt(click) == source;
        Selection? Probe()
        {
            var focus = FocusIn(source);
            if (focus?.Current.IsPassword == true) return null;
            var visited = new HashSet<AutomationElement>();
            var selection = ReadControl(focus, source, click, visited);
            if (selection != null) return selection;
            var hit = AutomationElement.FromPoint(new System.Windows.Point(click.X, click.Y));
            return hit != null && (focus == null || !Automation.Compare(focus, hit)) ? ReadControl(hit, source, click, visited) : null;
        }

        return ReadWhenReady(Probe, SameWindow, retryUntil);
    }

    internal static Selection? ReadWhenReady(Func<Selection?> probe, Func<bool> current, long retryUntil)
    {
        while (current())
        {
            var result = probe();
            if (result != null) return current() ? result : null;
            long remaining = retryUntil - Environment.TickCount64;
            if (remaining <= 0) break;
            Thread.Sleep((int)Math.Min(25, remaining));
        }
        return null;
    }

    internal static System.Windows.Rect SelectedLine(System.Windows.Rect[] bounds, Point? click) =>
        bounds.FirstOrDefault(r => r.Width > 0 && r.Height > 0 && (click is not { } p ||
            (p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom)));

    internal static Selection? ReadControl(AutomationElement? element, IntPtr source, Point? click = null, HashSet<AutomationElement>? visited = null)
    {
        foreach (var provider in TextProviders(element, source, false, visited))
        {
            TextPatternRange[] ranges;
            try { ranges = provider.GetSelection(); }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { continue; }
            if (ranges is [var range] && range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) != 0)
            {
                string raw = range.GetText(8001);
                if (raw.Length > 8000 || string.IsNullOrWhiteSpace(raw)) return null;
                System.Windows.Rect rect = default;
                try { rect = SelectedLine(range.GetBoundingRectangles(), click); }
                catch (Exception e) when (e is InvalidOperationException or NotSupportedException or COMException) { }
                if (rect.Width > 0)
                    return new(raw.Trim(), new(click?.X ?? (int)rect.Left, (int)rect.Top, (int)Math.Ceiling(rect.Bottom)), click == null);
                if (click is not { } cursor) GetCursorPos(out cursor);
                return new(raw.Trim(), new(cursor.X, cursor.Y, cursor.Y), click == null);
            }
        }
        return null;
    }

    private static IEnumerable<TextPattern> TextProviders(AutomationElement? element, IntPtr source, bool stopAtEditor, HashSet<AutomationElement>? visited = null)
    {
        for (int depth = 0; element != null && depth < 32; depth++)
        {
            if (visited != null && !visited.Add(element)) yield break;
            var current = element.Current;
            if (current.IsPassword) yield break;
            var handle = new IntPtr(current.NativeWindowHandle);
            if (handle != IntPtr.Zero && GetAncestor(handle, 2) != source) yield break;
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
            {
                yield return (TextPattern)pattern;
                if (current.ControlType == ControlType.Edit) yield break;
            }
            if (handle == source || (stopAtEditor && current.ControlType == ControlType.Edit)) yield break;
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
    }
}
