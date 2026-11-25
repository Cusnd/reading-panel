using System.Windows.Input;

namespace ReadingPanel.Integration;

internal sealed class MiddleClickShortcut(IntPtr window, int firstId = 3, int secondId = 4) : IDisposable
{
    private sealed record Registration(int Id, (uint Modifiers, uint Key) Binding);
    private Registration? active;
    private readonly int[] ids = [firstId, secondId];
    public bool Available => active != null;
    public bool Matches(IntPtr messageId) => active != null && messageId == active.Id;

    internal static (uint Modifiers, uint Key)? Parse(string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return null;
        try
        {
            // KeyGesture rejects Shift+letter for WPF commands, but it is a valid Win32 hotkey.
            string keys = shortcut.Split(',', 2)[0].Trim();
            int split = keys.LastIndexOf('+');
            var modifiers = split < 0 ? ModifierKeys.None :
                (ModifierKeys)new ModifierKeysConverter().ConvertFromInvariantString(keys[..split])!;
            if (new KeyConverter().ConvertFromInvariantString(keys[(split + 1)..].Trim()) is Key key &&
                key is not (Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
                    Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) &&
                (modifiers != ModifierKeys.None || key is >= Key.F1 and <= Key.F24))
            {
                int virtualKey = KeyInterop.VirtualKeyFromKey(key);
                if (virtualKey != 0) return ((uint)modifiers, (uint)virtualKey);
            }
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException or FormatException) { }
        throw new ArgumentException("请输入组合键（如 Ctrl+Alt+M）、功能键，或留空。");
    }

    public void Apply(string shortcut, Action save) => ApplyBindings([(this, shortcut)], [Parse(shortcut)], save);

    public static void ApplyPair(MiddleClickShortcut lookup, string lookupKeys, MiddleClickShortcut toggle, string toggleKeys, Action save)
        => ApplyBindings([(lookup, lookupKeys), (toggle, toggleKeys)], ValidatePair(lookupKeys, toggleKeys), save);

    internal static (uint Modifiers, uint Key)?[] ValidatePair(string lookupKeys, string toggleKeys)
    {
        var lookup = Parse(lookupKeys);
        var toggle = Parse(toggleKeys);
        if (lookup != null && lookup == toggle)
            throw new ArgumentException("键盘查词与中键切换快捷键不能相同。");
        return [lookup, toggle];
    }

    private static void ApplyBindings((MiddleClickShortcut Owner, string Keys)[] changes, (uint Modifiers, uint Key)?[] bindings, Action save)
    {
        // Reuse live registrations, including when the user swaps the two shortcuts.
        // Reserve all new keys before persisting or releasing either old key.
        var previous = changes.Select(c => c.Owner.active).OfType<Registration>().ToArray();
        var pending = new List<Registration>();
        var next = new Registration?[changes.Length];
        var window = changes[0].Owner.WindowHandle;
        var pool = changes.SelectMany(c => c.Owner.ids).ToArray();
        try
        {
            for (int i = 0; i < changes.Length; i++)
            {
                if (bindings[i] is not { } binding) continue;
                next[i] = previous.FirstOrDefault(r => r.Binding == binding);
                if (next[i] != null) continue;
                int id = changes[i].Owner.ids.Concat(pool).Except(previous.Concat(pending).Select(r => r.Id)).First();
                if (!WindowsReader.RegisterHotKey(window, id, binding.Modifiers | 0x4000, binding.Key))
                    throw new InvalidOperationException($"快捷键 {changes[i].Keys} 已被占用或由 Windows 保留，请选择其他组合。") {
                        Data = { [nameof(MiddleClickShortcut)] = changes[i].Keys }
                    };
                var registration = new Registration(id, binding);
                pending.Add(registration);
                next[i] = registration;
            }
            save();
        }
        catch
        {
            foreach (var registration in pending) WindowsReader.UnregisterHotKey(window, registration.Id);
            throw;
        }
        foreach (var registration in previous)
            if (!next.Contains(registration)) WindowsReader.UnregisterHotKey(window, registration.Id);
        for (int i = 0; i < changes.Length; i++) changes[i].Owner.active = next[i];
    }

    private IntPtr WindowHandle => window;

    public void Dispose()
    {
        if (active != null) WindowsReader.UnregisterHotKey(window, active.Id);
        active = null;
    }
}
