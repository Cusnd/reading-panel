using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReadingPanel.Integration;

namespace ReadingPanel.UI;

internal partial class ShortcutRecorder : UserControl
{
    public string DefaultShortcut { get; set; } = "";
    public string Shortcut { get => Input.Text; set => Input.Text = value; }
    public event Action? Changed;
    public ShortcutRecorder()
    {
        InitializeComponent();
        Input.TextChanged += (_, _) => Changed?.Invoke();
    }

    private void ClearClick(object sender, RoutedEventArgs e) => Shortcut = "";
    private void ResetClick(object sender, RoutedEventArgs e) => Shortcut = DefaultShortcut;
    private void RecordKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab || key == Key.Escape) return;
        e.Handled = true;
        if (Keyboard.Modifiers == ModifierKeys.None && key is Key.Back or Key.Delete) Shortcut = "";
        else Record(key, Keyboard.Modifiers);
    }

    internal void Record(Key key, ModifierKeys modifiers)
    {
        if (key is Key.Tab or Key.Escape or Key.None or Key.System or Key.LeftCtrl or Key.RightCtrl or
            Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (modifiers == ModifierKeys.None && key is not (>= Key.F1 and <= Key.F24)) return;
        var text = (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "")
            + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "")
            + (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "")
            + (modifiers.HasFlag(ModifierKeys.Windows) ? "Win+" : "") + new KeyConverter().ConvertToInvariantString(key);
        try { MiddleClickShortcut.Parse(text); Shortcut = text; }
        catch (ArgumentException) { }
    }
}
