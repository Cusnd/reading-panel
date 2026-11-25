using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ReadingPanel.UI;

internal sealed class ToggleSwitch : CheckBox
{
    public override void OnApplyTemplate() { base.OnApplyTemplate(); Slide(false); }
    protected override void OnChecked(RoutedEventArgs e) { base.OnChecked(e); Slide(IsLoaded); }
    protected override void OnUnchecked(RoutedEventArgs e) { base.OnUnchecked(e); Slide(IsLoaded); }

    private void Slide(bool animate)
    {
        if (GetTemplateChild("Thumb") is not FrameworkElement thumb) return;
        var slide = new TranslateTransform();
        double from = ((TranslateTransform)thumb.RenderTransform).X;
        thumb.RenderTransform = slide;
        slide.X = IsChecked == true ? 18 : 0;
        if (animate && SystemParameters.ClientAreaAnimation)
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, slide.X, TimeSpan.FromMilliseconds(140)) {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
            });
    }
}
