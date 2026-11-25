using System.Runtime.InteropServices;
using System.Windows.Threading;
using ReadingPanel.Core;

namespace ReadingPanel.Integration;

internal sealed record TextSelectionGesture(IntPtr Window, WindowsReader.Point End);

// Owned by the hook dispatcher. A late permission result updates only its original target.
internal sealed class ReplayPermission(Func<uint, bool> check)
{
    private sealed class Target(uint id) { internal readonly uint Id = id; internal volatile bool Allowed; }
    private Target? current;
    internal void Invalidate() => current = null;

    internal bool Verified(uint process)
    {
        if (current?.Id == process) return current.Allowed;
        var target = current = new Target(process);
        _ = Task.Run(() => {
            try { target.Allowed = check(process); }
            catch (Exception e) when (e is UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        });
        return false;
    }
}

internal sealed class MouseProbes
{
    private readonly HashSet<uint> running = new();
    private readonly Dictionary<uint, long> cooldowns = new();
    internal bool Busy(uint process, long now) => running.Contains(process) || cooldowns.GetValueOrDefault(process) > now;
    internal bool Begin(uint process) => running.Add(process);
    internal void End(uint process) => running.Remove(process);
    internal void Timeout(uint process, long now)
    {
        foreach (var expired in cooldowns.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray()) cooldowns.Remove(expired);
        cooldowns[process] = now + 1500;
    }
}

internal sealed class DesktopMouse : IDisposable
{
    internal const int MiddleDown = 0x0207, MiddleUp = 0x0208;
    private const int LeftDown = 0x0201, LeftUp = 0x0202, RightDown = 0x0204, Wheel = 0x020A, XDown = 0x020B, HorizontalWheel = 0x020E;
    internal const int GestureBudgetMs = 800, OrdinaryBudgetMs = 120;
    private const int SelectionSettleMs = 180;
    private readonly Dispatcher ui;
    private readonly MiddleClickState middleClick;
    private readonly ClipboardSelection clipboard;
    private readonly Action<Selection, IntPtr> open;
    private readonly Action dismiss;
    private readonly HookProc mouseCallback;
    private readonly WinEventProc foregroundCallback;
    private readonly Thread thread;
    private Dispatcher pump = null!; // Owned by the hook thread.
    private readonly TaskCompletionSource<bool> initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<bool> Initialization => initialized.Task;
    private readonly MouseProbes probing = new();
    private readonly ReplayPermission permission = new(InputIntegrity.CanSendTo);
    private IntPtr permissionWindow;
    private readonly Dictionary<IntPtr, (long Time, bool Gesture)> warmWindows = new();
    private readonly HashSet<IntPtr> warming = new();
    private IntPtr lastTarget;
    private WindowsReader.Point leftStart;
    private IntPtr leftWindow;
    private volatile TextSelectionGesture? lastGesture;
    internal TextSelectionGesture? LastGesture => lastGesture;
    private long selectionReleased;
    private long leftTime;
    private bool secondClick;
    private IntPtr trackedWindow;
    private bool claimed;
    private sealed record DeferredPress(IntPtr Window, IntPtr Client, uint ProcessId, WindowsReader.Point Point,
        int Keys, long RetryUntil, TextSelectionGesture? Gesture)
    {
        public WindowsReader.Point? Release { get; set; }
        public int ReleaseKeys { get; set; }
    }
    private volatile DeferredPress? deferred;
    private DeferredPress? passedDown;
    private DispatcherTimer? passThroughTimer;

    public DesktopMouse(Dispatcher ui, MiddleClickState middleClick, ClipboardSelection clipboard, Action<Selection, IntPtr> open, Action dismiss)
    {
        this.ui = ui;
        this.middleClick = middleClick;
        this.clipboard = clipboard;
        this.open = open;
        this.dismiss = dismiss;
        mouseCallback = MouseHook;
        foregroundCallback = ForegroundChanged;
        thread = new Thread(Listen) { IsBackground = true, Name = "Reading Panel mouse" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void Listen()
    {
        pump = Dispatcher.CurrentDispatcher;
        var mouse = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0); // WH_MOUSE_LL
        var foreground = mouse == IntPtr.Zero ? IntPtr.Zero :
            SetWinEventHook(3, 3, IntPtr.Zero, foregroundCallback, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND, out of context
        initialized.SetResult(mouse != IntPtr.Zero);
        if (mouse == IntPtr.Zero) return;
        var foregroundWindow = WindowsReader.GetForegroundWindow();
        VerifyWindow(foregroundWindow);
        Prepare(foregroundWindow, null);
        try { Dispatcher.Run(); }
        finally
        {
            if (foreground != IntPtr.Zero) UnhookWinEvent(foreground);
            UnhookWindowsHookEx(mouse);
        }
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code == 0)
        {
            int kind = (int)message;
            if (kind is RightDown or XDown or Wheel or HorizontalWheel) lastGesture = null;
            if (kind is MiddleDown or MiddleUp && WindowsReader.IsReplayedMouse(data))
                return CallNextHookEx(IntPtr.Zero, code, message, data);
            if (deferred is { } press)
            {
                var location = Marshal.PtrToStructure<WindowsReader.Point>(data);
                if (kind == MiddleUp)
                {
                    press.Release = location;
                    press.ReleaseKeys = WindowsReader.MouseKeys();
                    return new IntPtr(1);
                }
                // Moving after releasing a click doesn't turn it into a drag.
                if (kind != 0x0200 || (press.Release == null && !Near(press.Point, location)))
                    FinishDeferred(press, null);
            }
            if (kind == MiddleUp)
            {
                if (claimed) { claimed = false; return new IntPtr(1); }
                var passed = passedDown;
                passedDown = null;
                if (passed != null && passed.Window != WindowsReader.GetForegroundWindow())
                {
                    WindowsReader.GetWindowThreadProcessId(passed.Client, out uint process);
                    if (process == passed.ProcessId && WindowsReader.RootWindow(passed.Client) == passed.Window)
                        WindowsReader.PostMiddle(passed.Client, MiddleUp, Marshal.PtrToStructure<WindowsReader.Point>(data), WindowsReader.MouseKeys());
                    return new IntPtr(1);
                }
            }
            else if (kind is MiddleDown or LeftDown or LeftUp || (Volatile.Read(ref trackedWindow) != IntPtr.Zero &&
                     kind is RightDown or XDown or Wheel or HorizontalWheel))
            {
                try
                {
                    var point = Marshal.PtrToStructure<WindowsReader.Point>(data);
                    if (kind == LeftDown)
                    {
                        long now = Environment.TickCount64;
                        var window = WindowsReader.WindowAt(point);
                        secondClick = leftWindow == window && leftTime != 0 && now - leftTime <= GetDoubleClickTime() && Near(leftStart, point);
                        leftWindow = window;
                        lastGesture = null;
                        leftStart = point;
                        leftTime = now;
                    }
                    if (kind == LeftUp)
                    {
                        // Chromium can take a frame to expose its accessibility tree after the first UIA call.
                        // Wake the provider after a selection gesture without retrieving any text.
                        if (leftTime != 0 && leftWindow == WindowsReader.GetForegroundWindow() && (secondClick || !Near(leftStart, point)))
                        {
                            lastGesture = new(leftWindow, point);
                            selectionReleased = Environment.TickCount64;
                            Prepare(leftWindow, point);
                        }
                    }
                    else if ((kind == MiddleDown || Volatile.Read(ref trackedWindow) != IntPtr.Zero) && HandlePress(kind, point))
                        return new IntPtr(1);
                }
                catch (Exception) { /* An unavailable reader must leave the original input intact. */ }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private static bool Near(WindowsReader.Point a, WindowsReader.Point b) =>
        Math.Abs((long)a.X - b.X) <= 4 && Math.Abs((long)a.Y - b.Y) <= 4;

    private void Prepare(IntPtr source, WindowsReader.Point? point)
    {
        if (!middleClick.Enabled || source == IntPtr.Zero || warming.Count >= 2 || warming.Contains(source)) return;
        WindowsReader.GetWindowThreadProcessId(source, out uint process);
        if (process == Environment.ProcessId) return;
        long now = Environment.TickCount64;
        if (warmWindows.TryGetValue(source, out var last) && now - last.Time < 1000 && (point == null || last.Gesture)) return;
        // Foreground-only warmup is for Chromium's delayed accessibility tree. Gestures target the actual reader.
        if (warmWindows.Count >= 64) warmWindows.Clear();
        warmWindows[source] = (now, point != null);
        warming.Add(source);
        _ = Task.Run(() => {
            try { if (point != null || WindowsReader.IsChromiumWindow(source)) WindowsReader.PrepareLayout(source, point); }
            catch (Exception) { /* Availability is checked again on the explicit lookup. */ }
            finally { pump.BeginInvoke(() => warming.Remove(source)); }
        });
    }

    private bool HandlePress(int kind, WindowsReader.Point point)
    {
        if (kind == MiddleDown) { claimed = false; passedDown = null; }
        var client = WindowsReader.HitWindow(point);
        var target = WindowsReader.RootWindow(client);
        WindowsReader.GetWindowThreadProcessId(client, out uint process);
        if (process == Environment.ProcessId) return false; // The card and its controls keep their own input.
        if (kind == MiddleDown && middleClick.Enabled && target != IntPtr.Zero && target == WindowsReader.GetForegroundWindow() &&
            WindowsReader.IsClientPoint(client, point) && !probing.Busy(process, Environment.TickCount64) && permission.Verified(process))
        {
            long now = Environment.TickCount64;
            long retryUntil = target == lastTarget ? 0 : now + 30;
            var gesture = lastGesture;
            if (gesture?.Window == target) retryUntil = Math.Max(retryUntil, selectionReleased + SelectionSettleMs);
            lastTarget = target;
            var press = new DeferredPress(target, client, process, point, WindowsReader.MouseKeys(), retryUntil, gesture);
            deferred = press;
            pump.BeginInvoke(() => BeginLookup(press));
            return true;
        }
        DismissTracked();
        return false;
    }

    private void OpenMouse(Selection selection, IntPtr target)
    {
        Interlocked.Exchange(ref trackedWindow, target);
        ui.BeginInvoke(() => open(selection, target));
    }

    private void VerifyWindow(IntPtr window)
    {
        if (!middleClick.Enabled || permissionWindow == window) return;
        permissionWindow = window;
        permission.Invalidate();
        WindowsReader.GetWindowThreadProcessId(window, out uint process);
        if (process != 0) permission.Verified(process);
    }

    internal static int LookupBudget(bool sameWindowGesture, long ageMs) =>
        sameWindowGesture && ageMs <= 30_000 ? GestureBudgetMs : OrdinaryBudgetMs;

    private void BeginLookup(DeferredPress press)
    {
        if (!ReferenceEquals(deferred, press)) return;
        int budget = LookupBudget(press.Gesture?.Window == press.Window, Environment.TickCount64 - selectionReleased);
        passThroughTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(budget), DispatcherPriority.Input, (_, _) => {
            probing.Timeout(press.ProcessId, Environment.TickCount64);
            FinishDeferred(press, null, replayAtCursor: true);
        }, pump);
        Probe(press);
    }

    private void Probe(DeferredPress press)
    {
        if (!probing.Begin(press.ProcessId)) return;
        bool Current() => ReferenceEquals(deferred, press) && middleClick.Enabled && WindowsReader.GetForegroundWindow() == press.Window;
        _ = Task.Run(() => {
            Selection? selection = null;
            try
            {
                selection = clipboard.Read(press.Window, Current,
                    () => WindowsReader.ReadAtPoint(press.Window, press.Point, press.RetryUntil, Current), press.Gesture, press.Point);
            }
            catch (Exception) { /* An unavailable reader leaves the click with its original window. */ }
            pump.BeginInvoke(() => {
                probing.End(press.ProcessId);
                if (ReferenceEquals(deferred, press)) FinishDeferred(press, selection, replayAtCursor: true);
            });
        });
    }

    private void FinishDeferred(DeferredPress press, Selection? selection, bool replayAtCursor = false)
    {
        if (!ReferenceEquals(deferred, press)) return;
        deferred = null;
        passThroughTimer?.Stop();
        passThroughTimer = null;
        WindowsReader.GetWindowThreadProcessId(press.Client, out uint process);
        if (process != press.ProcessId || WindowsReader.RootWindow(press.Client) != press.Window) return;
        if (selection != null && middleClick.Enabled && WindowsReader.GetForegroundWindow() == press.Window)
        {
            claimed = press.Release == null;
            OpenMouse(selection, press.Window);
            return;
        }
        // A stationary, completed click can use normal input dispatch, including other mouse hooks.
        // A drag, a moved pointer or a window switch must deliver to the original client coordinates.
        if (!(replayAtCursor && press.Release is { } end && end.X == press.Point.X && end.Y == press.Point.Y &&
            press.Keys == press.ReleaseKeys && WindowsReader.ReplayClickAt(press.Window, press.Client, end, press.Keys)))
        {
            WindowsReader.PostMiddle(press.Client, MiddleDown, press.Point, press.Keys);
            if (press.Release is { } release) WindowsReader.PostMiddle(press.Client, MiddleUp, release, press.ReleaseKeys);
            else passedDown = press;
        }
        DismissTracked();
    }

    private void ForegroundChanged(IntPtr hook, uint evt, IntPtr window, int obj, int child, uint tid, uint time)
    {
        if (lastGesture is { } gesture && WindowsReader.GetForegroundWindow() != gesture.Window) lastGesture = null;
        if (deferred is { } press && WindowsReader.GetForegroundWindow() != press.Window) FinishDeferred(press, null);
        if (window != IntPtr.Zero && window == WindowsReader.GetForegroundWindow())
        {
            VerifyWindow(window);
            Prepare(window, null);
        }
        var source = Volatile.Read(ref trackedWindow);
        if (source != IntPtr.Zero && window != source && WindowsReader.GetForegroundWindow() != source) DismissTracked();
    }

    private void DismissTracked()
    {
        if (Interlocked.Exchange(ref trackedWindow, IntPtr.Zero) != IntPtr.Zero) ui.BeginInvoke(dismiss);
    }

    public void Track(IntPtr source) => Interlocked.Exchange(ref trackedWindow, source);

    public void Dispose()
    {
        Track(IntPtr.Zero);
        if (Initialization.GetAwaiter().GetResult())
        {
            pump.Invoke(() => { if (deferred is { } press) FinishDeferred(press, null); });
            pump.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        thread.Join();
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr window, int obj, int child, uint tid, uint time);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")] private static extern IntPtr SetWindowsHookEx(int kind, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
