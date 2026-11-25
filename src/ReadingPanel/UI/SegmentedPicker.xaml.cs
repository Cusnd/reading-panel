using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ReadingPanel.UI;

internal partial class SegmentedPicker : UserControl
{
    public int SelectedIndex { get; private set; } = -1;
    public event Action<int>? SelectionChanged;
    public SegmentedPicker() => InitializeComponent();

    public void SetItems(IEnumerable<string> labels)
    {
        foreach (string label in labels)
        {
            int index = Choices.Children.Count;
            var choice = new RadioButton { Content = label, FontSize = 13, MinHeight = 30,
                Style = (Style)FindResource("SegmentedChoice") };
            choice.Checked += (_, _) => {
                SelectedIndex = index;
                PositionIndicator(IsVisible);
                SelectionChanged?.Invoke(index);
            };
            Choices.Children.Add(choice);
        }
    }

    public void Select(int index) => ((RadioButton)Choices.Children[index]).IsChecked = true;

    private void SizeChangedHandler(object sender, SizeChangedEventArgs e) => PositionIndicator(false);

    private void PositionIndicator(bool animate)
    {
        if (SelectedIndex < 0 || Choices.ActualWidth <= 0) return;
        var choice = (RadioButton)Choices.Children[SelectedIndex];
        Indicator.Width = choice.ActualWidth;
        Indicator.Height = choice.ActualHeight;
        double from = Slide.X, to = choice.TranslatePoint(new Point(), Choices).X;
        Slide.BeginAnimation(TranslateTransform.XProperty, null);
        Slide.X = to;
        if (animate && SystemParameters.ClientAreaAnimation && from != to)
            Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(160)) {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
            });
    }
}
