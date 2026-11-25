using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ReadingPanel.UI;

internal sealed class LemmaReveal : Decorator
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(LemmaReveal),
        new PropertyMetadata(false, (d, e) => ((LemmaReveal)d).Reveal((bool)e.NewValue)));

    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(LemmaReveal),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => {
            var reveal = (LemmaReveal)d;
            double progress = (double)e.NewValue;
            reveal.ChevronRotation.Angle = 180 * progress;
            var visibility = progress > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (reveal.Visibility != visibility) reveal.Visibility = visibility;
        }));

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public RotateTransform ChevronRotation { get; } = new();
    private double Progress => (double)GetValue(ProgressProperty);

    public LemmaReveal()
    {
        ClipToBounds = true;
        Visibility = Visibility.Collapsed;
    }

    private void Reveal(bool expanded)
    {
        double from = Progress, to = expanded ? 1 : 0;
        DoubleAnimation? transition = null;
        if (IsLoaded && SystemParameters.ClientAreaAnimation && from != to)
        {
            var duration = TimeSpan.FromMilliseconds((expanded ? 160 : 130) * Math.Abs(to - from));
            transition = new DoubleAnimation(from, to, duration) {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
            };
        }
        BeginAnimation(ProgressProperty, transition);
        SetValue(ProgressProperty, to);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var full = base.MeasureOverride(new Size(constraint.Width, double.PositiveInfinity));
        return new Size(full.Width, full.Height * Progress);
    }

    protected override Size ArrangeOverride(Size size)
    {
        // Lay out the text at full height and reveal it through the changing viewport.
        if (Child is { } child) child.Arrange(new Rect(0, 0, size.Width, child.DesiredSize.Height));
        return size;
    }
}
