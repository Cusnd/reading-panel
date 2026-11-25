using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ReadingPanel.Core;
using ReadingPanel.Integration;
using SpeechMode = ReadingPanel.Core.SpeechMode;

namespace ReadingPanel.UI;

internal partial class PanelWindow : Window
{
    private static readonly Regex ChineseListSeparator = new(@"(?<=[\u3400-\u9fff])[,;] *(?=[\u3400-\u9fff])");
    private readonly LookupController controller;
    private readonly IntPtr hwnd;
    private Selection? selection;
    private Entry? displayedEntry;
    private bool expandLemmasByDefault;
    private (int X, int Bottom, double Scale)? aboveAnchor;
    private string? lastTranslation;
    private bool above;
    private (Selection Source, WindowsReader.Rect Work, double Scale)? disclosurePlacement;
    private readonly Dictionary<(double Scale, string Label), double> labelWidths = new();
    private int generation;
    public event Action? SpeakRequested;
    public event Action<SpeechMode>? SpeechModeChanged;
    public event Action? SpeechSettingsRequested;
    public event Action? SetupRequested;
    public event Action? DismissRequested;
    public event Action<string>? Error;

    public PanelWindow(LookupController controller)
    {
        InitializeComponent();
        this.controller = controller;
        hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(hwnd).AddHook(Message);
        foreach (var provider in controller.Providers)
        {
            var button = new RadioButton {
                Style = (Style)FindResource("ProviderChoice"), Content = provider.Name, Tag = provider.Id,
                GroupName = "TranslationService"
            };
            button.Checked += (_, _) => {
                try { controller.SelectProvider(provider.Id); }
                catch (Exception e) { Render(); Error?.Invoke(e.Message); }
            };
            ProviderButtons.Children.Add(button);
        }
        foreach (var speaker in new[] { WordSpeaker, TranslationSpeaker })
        {
            var menu = new ContextMenu();
            foreach (var mode in Enum.GetValues<SpeechMode>())
            {
                var item = new MenuItem { Header = SpeechPolicy.Label(mode), IsCheckable = true, Tag = mode };
                item.Click += (_, _) => { try { SpeechModeChanged?.Invoke(mode); } catch (Exception e) { Error?.Invoke(e.Message); } };
                menu.Items.Add(item);
            }
            menu.Opened += (_, _) => {
                foreach (var item in menu.Items.OfType<MenuItem>().Where(i => i.Tag is SpeechMode))
                    item.IsChecked = (SpeechMode)item.Tag == controller.Settings.Speech.Mode;
            };
            menu.Items.Add(new Separator());
            var settings = new MenuItem { Header = "用量与朗读设置…" };
            settings.Click += (_, _) => SpeechSettingsRequested?.Invoke();
            menu.Items.Add(settings);
            speaker.ContextMenu = menu;
        }
        ShowSpeech(false);
        SizeChanged += KeepBottomAnchored;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; DismissRequested?.Invoke(); } };
    }

    private IntPtr Message(IntPtr window, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message == 0x21 && selection?.Keyboard != true) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
        return IntPtr.Zero;
    }

    private void Styles(bool keyboard, bool closing)
    {
        const int noActivate = 0x08000000, transparent = 0x20;
        int style = WindowsReader.GetStyle(hwnd, -20) & ~(noActivate | transparent);
        WindowsReader.SetStyle(hwnd, -20, style | (keyboard ? 0 : noActivate) | (closing ? transparent : 0));
    }

    private static void Tween(Animatable target, DependencyProperty property, double value, int ms)
    {
        var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        target.BeginAnimation(property, animation);
    }

    public void Render()
    {
        var result = controller.Current;
        if (result == null && selection == null) return;
        int version = ++generation;
        if (result == null)
        {
            aboveAnchor = null;
            selection = null;
            lastTranslation = null;
            Styles(false, true);
            IsHitTestVisible = false;
            if (!SystemParameters.ClientAreaAnimation) { Hide(); return; }
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(90));
            fade.Completed += (_, _) => { if (generation == version) Hide(); };
            Surface.BeginAnimation(OpacityProperty, fade);
            Tween(Slide, TranslateTransform.YProperty, above ? 3 : -3, 90);
            return;
        }
        bool opening = !ReferenceEquals(selection, result.Source);
        bool wasHidden = !IsVisible;
        bool entering = opening || wasHidden;
        if (entering)
        {
            // Show and UpdateLayout can render synchronously before Place moves the window.
            Surface.Opacity = 0;
            Surface.BeginAnimation(OpacityProperty, null);
        }
        selection = result.Source;
        if (opening) { SpeechNotice.Visibility = Visibility.Collapsed; ShowSpeech(false); }
        Styles(selection.Keyboard, false);
        IsHitTestVisible = true;
        var monitor = GetMonitorArea(selection.Anchor);
        Present(result, opening, monitor.Scale);
        if (PendingStatus.Visibility != Visibility.Visible) BodyScroll.ScrollToTop();
        if (wasHidden) Show();
        Place(selection, monitor, entering);
        if (result.IsTranslation) PositionIndicator(!entering && SystemParameters.ClientAreaAnimation);
        if (entering)
        {
            Slide.BeginAnimation(TranslateTransform.YProperty, null);
            Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Scale.ScaleX = Scale.ScaleY = 1;
            Slide.Y = 0;
            if (SystemParameters.ClientAreaAnimation)
            {
                Surface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
                Slide.Y = above ? 5 : -5;
                Tween(Slide, TranslateTransform.YProperty, 0, 160);
                Scale.ScaleX = Scale.ScaleY = .985;
                Tween(Scale, ScaleTransform.ScaleXProperty, 1, 160);
                Tween(Scale, ScaleTransform.ScaleYProperty, 1, 160);
            }
            else Surface.Opacity = 1;
        }
        else if (result.Kind == ResultKind.Translation && SystemParameters.ClientAreaAnimation)
            TranslationText.BeginAnimation(OpacityProperty, new DoubleAnimation(.3, 1, TimeSpan.FromMilliseconds(135)));
        if (selection.Keyboard && (entering || !IsKeyboardFocusWithin))
        {
            Activate();
            (result.IsTranslation ? TranslationSpeaker : WordSpeaker).Focus();
        }
    }

    private void Present(LookupResult result, bool opening, double scale)
    {
        bool translation = result.IsTranslation;
        var entry = result.Entry;
        WordHeader.Visibility = translation ? Visibility.Collapsed : Visibility.Visible;
        TranslationText.Visibility = ProviderRow.Visibility = translation ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(Heading, translation ? "WordHeading" : "Heading");
        AutomationProperties.SetAutomationId(TranslationText, translation ? "Heading" : "TranslationText");
        AutomationProperties.SetAutomationId(WordSpeaker, translation ? "WordSpeaker" : "Speaker");
        AutomationProperties.SetAutomationId(TranslationSpeaker, translation ? "Speaker" : "TranslationSpeaker");
        Heading.Text = translation ? "" : entry?.Word ?? result.Source.Text.Trim();
        Heading.FontSize = Heading.Text.Length > 18 ? 24 : 30;
        Heading.MaxHeight = entry == null ? 96 : double.PositiveInfinity;
        DictionaryStatus.Text = !translation && entry == null ? result.Text : "";
        DictionaryStatus.Visibility = DictionaryStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Keep the previous paragraph in place while the other provider works.
        if (opening || result.Kind != ResultKind.Translating)
            lastTranslation = result.Kind == ResultKind.Translation ? result.Text.Replace('\uFFFC', ' ').Trim() : null;
        bool switching = result.Kind == ResultKind.Translating && lastTranslation != null;
        if (result.Kind == ResultKind.Translating) PendingStatus.Text = result.Text;
        PendingStatus.Visibility = switching ? Visibility.Visible : Visibility.Hidden;
        TranslationText.Text = translation ? lastTranslation ?? result.Text : "";
        TranslationText.Foreground = (Brush)FindResource(result.Kind == ResultKind.Translation ? "Ink" : "Muted");
        bool otherHeadword = entry != null && !entry.Headword.Equals(entry.Word, StringComparison.OrdinalIgnoreCase);
        HeadwordReference.Text = entry?.Definitions.FirstOrDefault()?.Heading
            ?? (otherHeadword ? "释义取自 " + entry!.Headword : "");
        HeadwordReference.Visibility = HeadwordReference.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Phonetic.Text = !otherHeadword && entry?.Phonetic is { Length: > 0 } ipa ? "/" + ipa + "/" : "";
        Phonetic.Visibility = Phonetic.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExamLabels.Text = entry == null ? "" : string.Join(" · ", entry.ExamLabels);
        ExamLabels.Visibility = ExamLabels.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (opening || !ReferenceEquals(entry, displayedEntry) || expandLemmasByDefault != controller.Settings.ExpandLemmasByDefault)
        {
            expandLemmasByDefault = controller.Settings.ExpandLemmasByDefault;
            disclosurePlacement = null;
            Surface.Width = double.NaN;
            SetDefinitions(entry, scale);
            displayedEntry = entry;
        }
        var attribution = entry?.Attribution;
        Article.NavigateUri = attribution?.Article;
        ArticleName.Text = attribution?.SourceName;
        LicenseLink.NavigateUri = attribution?.License;
        LicenseName.Text = attribution?.LicenseName;
        Credit.Visibility = attribution == null ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Content = result.Kind switch {
            ResultKind.Missing => "翻译所选内容", ResultKind.LookupError => "重试",
            ResultKind.Setup => "翻译设置", _ => "Retry"
        };
        ActionButton.Visibility = result.Kind is ResultKind.Setup or ResultKind.Error or ResultKind.LookupError or ResultKind.Missing
            ? Visibility.Visible : Visibility.Collapsed;
        foreach (RadioButton button in ProviderButtons.Children)
            button.IsChecked = (string)button.Tag == controller.Provider.Id;
    }

    private void SetDefinitions(Entry? entry, double scale)
    {
        RelatedSections.Children.Clear();
        var first = entry?.Definitions.FirstOrDefault();
        Meaning.Visibility = first == null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(Meaning, first?.Text ?? "");
        if (entry == null || first == null) { Meaning.Content = null; return; }
        // Folded labels need their final text metrics before the card moves to the selection's monitor.
        var labelMeasure = new TextBlock { Style = (Style)FindResource("PartOfSpeech"), Language = WordDefinitions.Language };
        TextOptions.SetTextFormattingMode(labelMeasure, TextOptions.GetTextFormattingMode(WordDefinitions));
        VisualTreeHelper.SetRootDpi(labelMeasure, new DpiScale(scale, scale));
        double labelWidth = 48;
        var naturalMeasure = new TextBlock { Style = labelMeasure.Style };
        Grid Section(DefinitionSection section, Expander? disclosure = null)
        {
            var lines = section.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(ReadDefinition).ToArray();
            foreach (var (label, _) in lines)
            {
                if (label.Length == 0) continue;
                if (!labelWidths.TryGetValue((scale, label), out double width))
                {
                    naturalMeasure.Text = labelMeasure.Text = label;
                    var space = new Size(double.PositiveInfinity, double.PositiveInfinity);
                    naturalMeasure.Measure(space);
                    labelMeasure.Measure(space);
                    labelWidths[(scale, label)] = width = Math.Max(naturalMeasure.DesiredSize.Width, labelMeasure.DesiredSize.Width);
                }
                labelWidth = Math.Max(labelWidth, width);
            }
            var rows = new Grid();
            if (disclosure == null || disclosure.IsExpanded) DefinitionRows(rows, lines);
            else disclosure.Expanded += Populate;

            void Populate(object sender, RoutedEventArgs args)
            {
                disclosure!.Expanded -= Populate;
                DefinitionRows(rows, lines);
            }
            return rows;
        }

        Meaning.Content = Section(first);
        foreach (var section in entry.Definitions.Skip(1))
        {
            if (section.Lemma is { } lemma)
            {
                var disclosure = new Expander {
                    Header = lemma, Style = (Style)FindResource("LemmaDisclosure"),
                    IsExpanded = expandLemmasByDefault
                };
                disclosure.Content = Section(section, disclosure);
                AutomationProperties.SetName(disclosure, section.Heading ?? lemma);
                RelatedSections.Children.Add(disclosure);
                continue;
            }
            if (section.Heading is { } heading)
            {
                RelatedSections.Children.Add(new TextBlock {
                    Text = heading, FontFamily = Heading.FontFamily, FontStyle = FontStyles.Italic, FontSize = 17,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 13, 0, 3)
                });
            }
            RelatedSections.Children.Add(Section(section));
        }
        WordDefinitions.Resources["WordLabelWidth"] = new GridLength(Math.Ceiling(labelWidth * scale) / scale);
    }

    private void DefinitionRows(Grid rows, (string Label, string Text)[] lines)
    {
        var labels = new ColumnDefinition();
        labels.SetResourceReference(ColumnDefinition.WidthProperty, "WordLabelWidth");
        rows.ColumnDefinitions.Add(labels);
        rows.ColumnDefinitions.Add(new ColumnDefinition());
        string? previousLabel = null;
        foreach (var (label, definition) in lines)
        {
            int row = rows.RowDefinitions.Count;
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bool newGroup = label != previousLabel;
            var margin = new Thickness(0, previousLabel == null ? 0 : newGroup ? 5 : 2, 0, 0);
            if (newGroup && label.Length > 0)
            {
                var caption = new TextBlock { Text = label, Style = (Style)FindResource("PartOfSpeech"), Margin = margin };
                Grid.SetRow(caption, row);
                rows.Children.Add(caption);
            }
            previousLabel = label;
            var text = new TextBlock {
                Text = ChineseListSeparator.Replace(definition, m => m.Value[0] == ',' ? "，" : "；"),
                Style = (Style)FindResource("Definition"), Margin = margin
            };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, label.Length > 0 ? 1 : 0);
            Grid.SetColumnSpan(text, label.Length > 0 ? 1 : 2);
            rows.Children.Add(text);
        }
    }

    private static (string Label, string Text) ReadDefinition(string line)
    {
        int end = line.StartsWith('[') ? line.IndexOf(']') + 1 : line.IndexOfAny([' ', '\t']);
        if (end <= 0 || end >= line.Length) return ("", line);
        string label = line[..end] switch {
            "n." => "名词", "v." => "动词", "vt." => "及物动词", "vi." => "不及物动词",
            "a." or "adj." => "形容词", "ad." or "adv." => "副词", "prep." => "介词",
            "pron." => "代词", "conj." => "连词", "art." => "冠词", "num." => "数词",
            "int." or "interj." => "感叹词", "det." => "限定词", "aux." => "助动词", "pl." => "复数",
            "pref." => "前缀", "suf." => "后缀", "abbr." => "缩写",
            "[计]" => "计算机", "[医]" => "医学", "[化]" => "化学", "[法]" => "法律", "[网络]" => "网络",
            _ => ""
        };
        return label.Length == 0 ? ("", line) : (label, line[end..].TrimStart());
    }

    private void PositionIndicator(bool animate)
    {
        var active = ProviderButtons.Children.OfType<RadioButton>().First(b => b.IsChecked == true);
        var position = active.TranslatePoint(new Point(), ProviderButtons);
        Indicator.Width = active.ActualWidth;
        Indicator.Height = active.ActualHeight;
        if (animate && IndicatorSlide.Y == position.Y && Math.Abs(IndicatorSlide.X - position.X) > 0.1)
            Tween(IndicatorSlide, TranslateTransform.XProperty, position.X, 155);
        else
        {
            IndicatorSlide.BeginAnimation(TranslateTransform.XProperty, null);
            IndicatorSlide.X = position.X;
            IndicatorSlide.Y = position.Y;
        }
    }

    private static (WindowsReader.Rect Work, double Scale) GetMonitorArea(Anchor anchor)
    {
        var monitor = WindowsReader.MonitorFromPoint(new() { X = anchor.X, Y = anchor.Top }, 2);
        var info = new WindowsReader.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<WindowsReader.MonitorInfo>() };
        WindowsReader.GetMonitorInfo(monitor, ref info);
        WindowsReader.GetDpiForMonitor(monitor, 0, out uint dpi, out _);
        return (info.Work, dpi > 0 ? dpi / 96d : 1);
    }

    private void Place(Selection source, (WindowsReader.Rect Work, double Scale)? monitor = null, bool entering = false)
    {
        aboveAnchor = null;
        var anchor = source.Anchor;
        var (work, scale) = monitor ?? GetMonitorArea(anchor);
        double outerX = Surface.Margin.Left + Surface.Margin.Right;
        double outerY = Surface.Margin.Top + Surface.Margin.Bottom;
        bool translation = ProviderRow.Visibility == Visibility.Visible;
        double available = Math.Max(1, (work.Right - work.Left) / scale - outerX);
        // A new word sizes to its senses. A sentence keeps the same width across providers.
        double readingWidth = source.Text.Length <= 60 ? 340 : 396;
        Surface.MaxWidth = Math.Min(translation ? readingWidth : 344, available);
        Surface.MinWidth = Math.Min(translation ? 190 : 226, Surface.MaxWidth);
        if (translation) Surface.Width = Surface.MaxWidth;
        double frameInsets = outerY + Card.Margin.Top + Card.Margin.Bottom + Card.BorderThickness.Top + Card.BorderThickness.Bottom;
        bool hasDisclosure = RelatedSections.Children.OfType<Expander>().Any();
        bool keepSide = hasDisclosure && disclosurePlacement is { } previous && ReferenceEquals(source, previous.Source) &&
            previous.Scale == scale && previous.Work.Equals(work);
        if (!keepSide) CardLayout.MaxHeight = Math.Max(1, (work.Bottom - work.Top) / scale - frameInsets);
        UpdateLayout();
        int below = anchor.Bottom - (int)((Surface.Margin.Top - 4) * scale);
        int aboveBottom = anchor.Top + (int)((Surface.Margin.Bottom - 4) * scale);
        int roomBelow = work.Bottom - Math.Max(work.Top, below);
        int roomAbove = Math.Min(work.Bottom, aboveBottom) - work.Top;
        double desiredHeight = Math.Ceiling((Surface.ActualHeight + outerY) * scale);
        if (!keepSide) above = desiredHeight > roomBelow && (desiredHeight <= roomAbove || hasDisclosure && roomAbove > roomBelow);
        if (hasDisclosure)
        {
            Surface.Width = Surface.ActualWidth;
            // Keep the chosen side of the selection. Extra senses scroll once that space is full.
            CardLayout.MaxHeight = Math.Max(1, (above ? roomAbove : roomBelow) / scale - frameInsets);
            // Pinning an already measured width needs no second synchronous pass unless the cap clips content.
            if (CardLayout.ActualHeight > CardLayout.MaxHeight) UpdateLayout();
        }
        int width = (int)Math.Ceiling((Surface.ActualWidth + outerX) * scale);
        int height = (int)Math.Ceiling((Surface.ActualHeight + outerY) * scale);
        int x = Math.Clamp(anchor.X - (int)((Surface.Margin.Left + 25) * scale), work.Left, Math.Max(work.Left, work.Right - width));
        int y = Math.Clamp(above ? aboveBottom - height : below, work.Top, Math.Max(work.Top, work.Bottom - height));
        double tip = Math.Clamp((anchor.X - x) / scale - Surface.Margin.Left - 6.5, 12, Math.Max(12, Surface.ActualWidth - 25));
        TipOffset.X = tip;
        Tip.VerticalAlignment = above ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Tip.LayoutTransform = new ScaleTransform(1, above ? -1 : 1);
        Tip.Fill = above && ProviderRow.IsVisible ? ProviderRow.Background :
            !above && WordHeader.IsVisible ? WordHeader.Background : Card.Background;
        Surface.RenderTransformOrigin = new Point((tip + 6.5) / Surface.ActualWidth, above ? 1 : 0);
        if (entering || !WindowsReader.GetWindowRect(hwnd, out var bounds) || bounds.Left != x || bounds.Top != y ||
            bounds.Right - x != width || bounds.Bottom - y != height)
            WindowsReader.SetWindowPos(hwnd, new IntPtr(-1), x, y, width, height, 0x10);
        disclosurePlacement = hasDisclosure ? (source, work, scale) : null;
        if (hasDisclosure && above) aboveAnchor = (x, y + height, scale);
    }

    private void KeepBottomAnchored(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged || aboveAnchor is not { } anchor) return;
        int top = anchor.Bottom - (int)Math.Ceiling(e.NewSize.Height * anchor.Scale);
        if (!WindowsReader.GetWindowRect(hwnd, out var bounds) || bounds.Top != top || bounds.Left != anchor.X)
            WindowsReader.SetWindowPos(hwnd, IntPtr.Zero, anchor.X, top, 0, 0, 0x15); // Move only, without activation or a Z-order change.
    }

    public void ShowSpeech(bool playing, string voice = "")
    {
        if (playing && SpeechNotice.IsVisible)
        {
            SpeechNotice.Visibility = Visibility.Collapsed;
            if (selection is { } source) Place(source);
        }
        var glyph = (Geometry)FindResource(playing ? "StopGlyph" : "VolumeGlyph");
        WordSpeaker.Content = TranslationSpeaker.Content = glyph;
        string wordLabel = playing ? "停止朗读" : "朗读所选词";
        string hint = playing && voice.Length > 0 ? voice : SpeechPolicy.Label(controller.Settings.Speech.Mode);
        WordSpeaker.ToolTip = $"{wordLabel} · {hint}（右键切换）";
        AutomationProperties.SetName(WordSpeaker, wordLabel);
        string translationLabel = playing ? "Stop pronunciation" : "Pronounce selection";
        TranslationSpeaker.ToolTip = $"{translationLabel} · {hint}（右键切换）";
        AutomationProperties.SetName(TranslationSpeaker, translationLabel);
    }
    public void ShowSpeechError(string message)
    {
        if (selection == null) return;
        SpeechMessage.Text = message;
        SpeechNotice.Visibility = Visibility.Visible;
        Place(selection);
        SpeechNotice.BringIntoView();
    }
    private void BodyScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        TopFade.Visibility = BodyScroll.VerticalOffset > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        BottomFade.Visibility = BodyScroll.ScrollableHeight - BodyScroll.VerticalOffset > 0.5 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void VoiceSettingsClick(object sender, RoutedEventArgs e) =>
        SpeechSettingsRequested?.Invoke();
    private void SpeakClick(object sender, RoutedEventArgs e) => SpeakRequested?.Invoke();
    private void CreditClick(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if (e.Uri.IsAbsoluteUri && e.Uri.Scheme == Uri.UriSchemeHttps)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
    private void ActionClick(object sender, RoutedEventArgs e)
    {
        if (controller.Current?.Kind == ResultKind.Setup) SetupRequested?.Invoke();
        else if (controller.Current is { Kind: ResultKind.LookupError } failed) _ = controller.RetryAsync(failed.Source);
        else _ = controller.TranslateCurrentAsync();
    }
}
