using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ReadingPanel.UI;

internal partial class SecretField : UserControl
{
    public event Action? Changed;
    public SecretField() => InitializeComponent();
    public string Password
    {
        get => Masked.Password;
        set { SetRevealed(false); Masked.Password = value; }
    }

    private void PasswordChanged(object sender, RoutedEventArgs e)
    {
        Placeholder.Visibility = Masked.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Changed?.Invoke();
    }

    private void PlainChanged(object sender, TextChangedEventArgs e)
    {
        if (Eye?.IsChecked == true) Masked.Password = Plain.Text;
    }

    private void ToggleMask(object sender, RoutedEventArgs e) => SetRevealed(Eye.IsChecked == true);

    private void SetRevealed(bool reveal)
    {
        Eye.IsChecked = reveal;
        Plain.Text = reveal ? Masked.Password : "";
        Plain.Visibility = Slash.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        Masked.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        var label = reveal ? "隐藏密钥" : "显示密钥";
        Eye.ToolTip = label;
        AutomationProperties.SetName(Eye, label);
    }
}
