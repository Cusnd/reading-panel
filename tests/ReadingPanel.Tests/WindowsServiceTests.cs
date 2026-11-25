using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using ReadingPanel.Core;
using ReadingPanel.Integration;
using ReadingPanel.UI;
using Xunit;
using IToggleProvider = System.Windows.Automation.Provider.IToggleProvider;

namespace ReadingPanel.Tests;

public class WindowsServiceTests
{
#if !ONLINE_EDITION
    [Fact]
    public Task FirstShowKeepsTheCardTransparentUntilItIsPlaced() => WithOfflineCard((lookup, card) => {
        double? opacityAtShow = null;
        card.IsVisibleChanged += (_, _) => { if (card.IsVisible) opacityAtShow = card.Surface.Opacity; };

        OpenCard(lookup, new("river", new(420, 300, 320)));

        Assert.Equal(0d, opacityAtShow);
        WaitForAnimation(() => card.Surface.Opacity == 1);
    });

    [Fact]
    public Task WordCardDisplaysDefinitionsAndExamLabelsForTheCurrentEntry() => WithOfflineCard((lookup, card) => {
        OpenCard(lookup, new("abstract", new(100, 100, 120)));
        Assert.True(card.ExamLabels.IsVisible);
        Assert.Contains("CET-4 · CET-6", card.ExamLabels.Text);
        Assert.Contains("IELTS · GRE", card.ExamLabels.Text);
        Assert.True(card.ExamLabels.ActualHeight > 0);

        OpenCard(lookup, new("Circumstances,", new(100, 100, 120)));
        Assert.Equal("Circumstances", card.Heading.Text);
        Assert.Equal(Visibility.Collapsed, card.Phonetic.Visibility);
        Assert.Equal("原形 circumstance", card.HeadwordReference.Text);
        Assert.Contains("环境，状况，事件", ((Grid)card.Meaning.Content).Children.OfType<TextBlock>().Select(b => b.Text));
        var specific = Assert.Single(card.RelatedSections.Children.OfType<Grid>());
        Assert.Contains(specific.Children.OfType<TextBlock>(), b => b.Text == "法律" && b.IsVisible);
        Assert.Contains(specific.Children.OfType<TextBlock>(), b => b.Text.Contains("详细情节") && b.IsVisible);

        OpenCard(lookup, new("in the blink of an eye", new(100, 100, 120)));
        Assert.Equal(Visibility.Collapsed, card.ExamLabels.Visibility);
        Assert.Equal(Visibility.Collapsed, card.HeadwordReference.Visibility);
        Assert.Empty(card.RelatedSections.Children);
        Assert.Contains("一瞬间", AutomationProperties.GetName(card.Meaning));
    });

    [Fact]
    public Task SupplementaryLemmasExpandIndependentlyAndResetForANewLookup() => WithOfflineCard((lookup, card) => {
        OpenCard(lookup, new("Worse", new(100, 100, 120)));
        var lemma = Assert.Single(card.RelatedSections.Children.OfType<Expander>());
        var senses = (Grid)lemma.Content;
        Assert.Equal("原形 bad", AutomationProperties.GetName(lemma));
        Assert.Contains("更坏的事", AutomationProperties.GetName(card.Meaning));
        Assert.False(senses.IsVisible);
        Assert.Empty(senses.Children);
        double width = card.Surface.ActualWidth, height = card.Surface.ActualHeight;
        var header = (ToggleButton)lemma.Template.FindName("HeaderSite", lemma);
        var toggle = (IToggleProvider)new ToggleButtonAutomationPeer(header).GetPattern(PatternInterface.Toggle)!;
        toggle.Toggle();
        FinishReveal(lemma);
        Assert.True(senses.IsVisible);
        Assert.Contains(senses.Children.OfType<TextBlock>(), b => b.Text == "坏的");
        Assert.Equal(width, card.Surface.ActualWidth);
        Assert.True(card.Surface.ActualHeight > height);
        card.Render();
        Assert.Same(lemma, Assert.Single(card.RelatedSections.Children.OfType<Expander>()));
        Assert.True(lemma.IsExpanded);
        toggle.Toggle();
        FinishReveal(lemma);
        Assert.False(senses.IsVisible);
        Assert.Equal(height, card.Surface.ActualHeight);

        OpenCard(lookup, new("leaves", new(100, 100, 120)));
        var forms = card.RelatedSections.Children.OfType<Expander>().ToArray();
        Assert.Equal(new[] { "leave", "leaf" }, forms.Select(form => form.Header));
        forms[1].IsExpanded = true;
        Assert.False(forms[0].IsExpanded);
        OpenCard(lookup, new("leaves", new(150, 120, 140)));
        Assert.All(card.RelatedSections.Children.OfType<Expander>(), form => Assert.False(form.IsExpanded));

        OpenCard(lookup, new("went", new(100, 100, 120)));
        Assert.Equal("go的过去式", card.HeadwordReference.Text);
        Assert.Contains("去", AutomationProperties.GetName(card.Meaning));
        Assert.Empty(card.RelatedSections.Children);

        var work = SystemParameters.WorkArea;
        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(card).DpiScaleY;
        int bottom = (int)(work.Bottom * dpi);
        OpenCard(lookup, new("Worse", new(100, bottom - 30, bottom - 10)));
        double anchoredBottom = card.Top + card.ActualHeight;
        Assert.Equal(VerticalAlignment.Bottom, card.Tip.VerticalAlignment);
        var above = Assert.Single(card.RelatedSections.Children.OfType<Expander>());
        above.IsExpanded = true;
        FinishReveal(above);
        Assert.InRange(card.Top + card.ActualHeight, anchoredBottom - 1, anchoredBottom + 1);
        Assert.True(card.Top + card.ActualHeight <= work.Bottom + 1);
    });

    [Theory]
    [InlineData("comforting", false)]
    [InlineData("leaves", true)]
    public Task TogglingALemmaKeepsThePrimaryTextInPlace(string word, bool scrolls) => WithOfflineCard((lookup, card) => {
        OpenCard(lookup, new(word, new(120, 200, 220)));
        var primary = (Grid)card.Meaning.Content;
        var sense = primary.Children.OfType<TextBlock>().First(t => Grid.GetColumn(t) == 1);
        var lemma = card.RelatedSections.Children.OfType<Expander>().First();
        Rect Bounds(FrameworkElement part)
        {
            var origin = part.TranslatePoint(new Point(), card.Surface);
            return new Rect(new Point(Math.Round(origin.X, 3), Math.Round(origin.Y, 3)), part.RenderSize);
        }
        var before = new[] { Bounds(card.Heading), Bounds(card.Phonetic), Bounds(card.WordSpeaker), Bounds(sense) };
        double width = card.Surface.ActualWidth;
        foreach (bool open in new[] { true, false })
        {
            lemma.IsExpanded = open;
            FinishReveal(lemma);
            Assert.Equal(before, new[] { Bounds(card.Heading), Bounds(card.Phonetic), Bounds(card.WordSpeaker), Bounds(sense) });
            Assert.Equal(width, card.Surface.ActualWidth);
            Assert.Equal(0, card.BodyScroll.VerticalOffset);
            Assert.Equal(open && scrolls, card.BodyScroll.ScrollableHeight > 0);
            if (open && word == "comforting")
            {
                var verb = ((Grid)lemma.Content).Children.OfType<TextBlock>().Single(t => t.Text == "及物动词");
                Assert.InRange(verb.ActualHeight, verb.LineHeight - 1, verb.LineHeight + 1);
            }
        }
    });

    [Fact]
    public Task TheDefaultOpensNewLemmasImmediatelyAndAllowsManualOverrides() => WithOfflineCard((lookup, card) => {
        OpenCard(lookup, new("comforting", new(120, 200, 220)));
        lookup.Configure(lookup.Settings with { ExpandLemmasByDefault = true });
        var lemma = Assert.Single(card.RelatedSections.Children.OfType<Expander>());
        Assert.True(lemma.IsExpanded);
        var reveal = (LemmaReveal)lemma.Template.FindName("Details", lemma);
        Assert.Equal(180, reveal.ChevronRotation.Angle);
        Assert.True(reveal.ActualHeight > 0);
        lemma.IsExpanded = false;
        FinishReveal(lemma);
        card.Render();
        Assert.False(lemma.IsExpanded);
        OpenCard(lookup, new("leaves", new(120, 200, 220)));
        Assert.All(card.RelatedSections.Children.OfType<Expander>(), form => Assert.True(form.IsExpanded));
        lookup.Configure(lookup.Settings with { ExpandLemmasByDefault = false });
        Assert.All(card.RelatedSections.Children.OfType<Expander>(), form => Assert.False(form.IsExpanded));
    });

    private static void OpenCard(LookupController lookup, Selection selection)
    {
        var request = lookup.OpenAsync(selection);
        if (!request.IsCompleted) WaitForAnimation(() => request.IsCompleted);
        request.GetAwaiter().GetResult();
    }

    private static void FinishReveal(Expander disclosure)
    {
        var reveal = (LemmaReveal)disclosure.Template.FindName("Details", disclosure);
        double target = disclosure.IsExpanded ? 180 : 0;
        WaitForAnimation(() => reveal.ChevronRotation.Angle == target);
        disclosure.UpdateLayout();
    }

    private static void WaitForAnimation(Func<bool> finished)
    {
        var frame = new DispatcherFrame();
        var deadline = Environment.TickCount64 + 2000;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => {
            if (finished() || Environment.TickCount64 >= deadline) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(finished());
    }

    private sealed record CardService(string Name, Task<string> Reply) : ITranslationProvider
    {
        public string Id => Name;
        public bool RequiresApiKey => false;
        public Task<string> TranslateAsync(Translation input, ProviderOptions options, CancellationToken token) => Reply;
    }

    [Fact]
    public Task SwitchingAShortTranslationKeepsAllProvidersOnOneLine()
    {
        var ready = Task.FromResult("原译文");
        var pending = new TaskCompletionSource<string>();
        return WithOfflineCard((lookup, card) => {
            OpenCard(lookup, new("Check Windows ClearType settings and installed fonts", new(100, 100, 120)));
            Assert.Equal("原译文", card.TranslationText.Text);
            double height = card.ProviderRow.ActualHeight;
            var choices = card.ProviderButtons.Children.Cast<RadioButton>().ToArray();
            choices[^1].IsChecked = true;
            Assert.True(card.PendingStatus.IsVisible);
            Assert.Equal("原译文", card.TranslationText.Text);
            Assert.Equal(height, card.ProviderRow.ActualHeight);

            double right = card.PendingStatus.TranslatePoint(new Point(card.PendingStatus.ActualWidth, 0), card.ProviderRow).X;
            double top = choices[0].TranslatePoint(new Point(), card.ProviderRow).Y;
            foreach (var choice in choices)
            {
                var origin = choice.TranslatePoint(new Point(), card.ProviderRow);
                Assert.Equal(top, origin.Y);
                Assert.InRange(origin.X, right - 0.01, card.ProviderRow.ActualWidth - choice.ActualWidth + 0.01);
                right = origin.X + choice.ActualWidth;
            }

            pending.SetResult("新译文");
            WaitForAnimation(() => !card.PendingStatus.IsVisible);
            Assert.False(card.PendingStatus.IsVisible);
            Assert.Equal("新译文", card.TranslationText.Text);
        }, [new CardService("DeepL", ready), new CardService("Google Cloud", ready), new CardService("DeepSeek", pending.Task)]);
    }

    private static async Task WithOfflineCard(Action<LookupController, PanelWindow> verify, ITranslationProvider[]? services = null)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                using var db = new WordDictionary(Path.Combine(AppContext.BaseDirectory, "assets", "ecdict.sqlite3"));
                using var http = new HttpClient();
                var lookup = new LookupController(db, services ?? [new GoogleCloudProvider(http)], Settings.Default, _ => { });
                var card = new PanelWindow(lookup) { Opacity = 0 };
                try
                {
                    lookup.Changed += card.Render;
                    verify(lookup, card);
                }
                finally { card.Close(); }
                finished.SetResult();
            }
            catch (Exception e) { finished.SetException(e); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
#endif

    [Theory]
    [InlineData(-120, 100, 100)]
    [InlineData(-80, 121, 120)]
    [InlineData(-40, 104, 0)] // Right edge is outside.
    [InlineData(-85, 119, 0)] // Gap between highlighted lines.
    [InlineData(-50, 127, 0)] // Outside the shorter second line.
    public void MouseAnchorUsesLineBoundsWhenThePointerIntersectsThem(int x, int y, int expectedTop)
    {
        Rect[] lines = [new(-120,100,80,17), new(-120,120,50,15)];
        var line = WindowsReader.SelectedLine(lines, new WindowsReader.Point { X = x, Y = y });
        Assert.Equal(expectedTop, line.Top);
    }

    [Fact]
    public void MouseLookupRetriesASelectionThatTheReaderIsStillPublishing()
    {
        var expected = new Selection("river", new(10, 20, 30));
        int attempts = 0;
        var selected = WindowsReader.ReadWhenReady(() => ++attempts == 2 ? expected : null,
            () => true, Environment.TickCount64 + 1000);
        Assert.Same(expected, selected);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void MouseLookupDiscardsAResultIfTheGestureWasCancelledDuringTheRead()
    {
        bool current = true;
        var selected = WindowsReader.ReadWhenReady(() => {
            current = false;
            return new Selection("river", new(10, 20, 30));
        }, () => current, 0);
        Assert.Null(selected);
    }

    [Fact]
    public async Task WindowsReaderGetsOnlyTheHighlightedTextFromAnEditor()
    {
        var ready = new TaskCompletionSource<(IntPtr Window, Dispatcher Dispatcher)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try
            {
                var editor = new TextBox { Text = "The talks yielded little progress." };
                var window = new Window { Content = editor, Width = 300, Height = 60, Left = -30000, Top = -30000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                window.Show();
                editor.Select(10, 7);
                ready.SetResult((new WindowInteropHelper(window).Handle, window.Dispatcher));
                Dispatcher.Run();
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var fixture = await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
        try
        {
            var root = AutomationElement.FromHandle(fixture.Window);
            var edit = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            var result = WindowsReader.ReadControl(edit, fixture.Window);
            Assert.Equal("yielded", result?.Text);
            Assert.True(result?.Keyboard);
            var click = new WindowsReader.Point { X = 200, Y = 190 };
            var mouse = WindowsReader.ReadControl(edit, fixture.Window, click);
            Assert.Equal("yielded", mouse?.Text);
            Assert.False(mouse?.Keyboard);
            Assert.Equal(new Anchor(200, 190, 190), mouse?.Anchor);
            var pattern = (TextPattern)edit.GetCurrentPattern(TextPattern.Pattern);
            var caret = Assert.Single(pattern.GetSelection());
            caret.MoveEndpointByRange(TextPatternRangeEndpoint.End, caret, TextPatternRangeEndpoint.Start);
            caret.Select();
            Assert.Null(WindowsReader.ReadControl(edit, fixture.Window, click));
        }
        finally { fixture.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
    }

    [Fact]
    public void SettingsRoundTripProviderAndGesturePreferencesUsingWindowsProtection()
    {
        var directory = Directory.CreateTempSubdirectory("reading-panel-test-");
        try
        {
            var store = new SettingsStore(directory.FullName);
            var defaults = store.Load();
            Assert.Equal("deepl", defaults.Provider);
            Assert.True(defaults.TranslationEnabled);
            Assert.False(defaults.ExpandLemmasByDefault);
            Assert.True(File.Exists(Path.Combine(directory.FullName, "settings.dat")));
            var settings = new Settings("deepseek", new() {
                ["deepseek"] = new("example-private-key", "custom-model"), ["google"] = new("cloud-key"),
                ["deepl"] = new("sample:fx")
            }) { MiddleClickEnabled = false, MiddleClickShortcut = "Ctrl+Shift+J", TranslationEnabled = false, ExpandLemmasByDefault = true };
            store.Save(settings);
            var loaded = store.Load();
            Assert.Equal(settings.Provider, loaded.Provider);
            Assert.Equal(settings.For("deepseek"), loaded.For("deepseek"));
            Assert.Equal(settings.For("google"), loaded.For("google"));
            Assert.Equal(settings.For("deepl"), loaded.For("deepl"));
            Assert.False(loaded.MiddleClickEnabled);
            Assert.False(loaded.TranslationEnabled);
            Assert.True(loaded.ExpandLemmasByDefault);
            Assert.Equal(settings.MiddleClickShortcut, loaded.MiddleClickShortcut);
            Assert.DoesNotContain("example-private-key", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory.FullName, "settings.dat"))));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("microsoft", "google")]
    [InlineData("deepseek", "deepseek")]
    public void OlderSettingsMigrateTheProviderWithoutReusingItsCredentials(string previous, string expected)
    {
        var directory = Directory.CreateTempSubdirectory("reading-panel-upgrade-");
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new {
                Provider = previous,
                Services = new { microsoft = new ProviderOptions("old-azure-key", "eastus"), deepseek = new ProviderOptions("my-key", "my-model") }
            });
            File.WriteAllBytes(Path.Combine(directory.FullName, "settings.dat"),
                ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
            var loaded = new SettingsStore(directory.FullName).Load();
            Assert.Equal(expected, loaded.Provider);
            Assert.Equal(new ProviderOptions(), loaded.For("google"));
            Assert.Equal(new ProviderOptions("my-key", "my-model"), loaded.For("deepseek"));
            Assert.True(loaded.TranslationEnabled);
            Assert.True(loaded.MiddleClickEnabled);
            Assert.False(loaded.ExpandLemmasByDefault);
            Assert.Equal("Ctrl+Alt+M", loaded.MiddleClickShortcut);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("""[{"len":5,"is_entire_line":false}]""", "river", "river")]
    [InlineData("""[{"len":5,"is_entire_line":true}]""", "river", null)]
    [InlineData("""[{"len":5,"is_entire_line":false}]""", "水\r\nA", "水\r\nA")]
    [InlineData("""[{"len":2,"is_entire_line":false}]""", "river", null)]
    [InlineData("""[{"len":5,"is_entire_line":false},{"len":1,"is_entire_line":false}]""", "river\nx", null)]
    [InlineData(null, "old clipboard text", null)]
    [InlineData("{}", "river", null)]
    public void ZedNeedsOneConfirmedSelectionFromTheCurrentCopy(string? metadata, string text, string? expected) =>
        Assert.Equal(expected, ClipboardSelection.ZedText(metadata, text));

    [Theory]
    [InlineData(" river ", "river")]
    [InlineData("行\r\n第", "行\r\n第")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void CopiedTextPreservesTheSelectionAcrossReaders(string? copy, string? expected) =>
        Assert.Equal(expected, ClipboardSelection.PlainText(copy));

    [Fact]
    public async Task BrowserClientsReceiveTheCurrentMiddleClickPreferenceAndLiveChanges()
    {
        string name = "ReadingPanel-Test-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        var connect = server.WaitForConnectionAsync(stop.Token);
        await client.ConnectAsync(stop.Token);
        await connect;
        var state = new MiddleClickState(false);
        var serving = NativeMessaging.ServeAsync(server, state, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, stop.Token);
        foreach (bool expected in new[] { false, true, false })
        {
            state.Enabled = expected;
            using var message = System.Text.Json.JsonDocument.Parse(await NativeMessaging.ReadAsync(client, stop.Token));
            Assert.Equal("ready", message.RootElement.GetProperty("type").GetString());
            Assert.Equal(expected, message.RootElement.GetProperty("middleClickEnabled").GetBoolean());
        }
        client.Dispose();
        await serving.WaitAsync(stop.Token);
    }

    [Fact]
    public void ToggleShortcutRejectsConflictsAndKeepsOldBindingIfSavingFails()
    {
        using var original = new MiddleClickShortcut(IntPtr.Zero);
        const string first = "Ctrl+Alt+Shift+F21", second = "Ctrl+Alt+Shift+F22";
        original.Apply(first, () => { });
        var a = MiddleClickShortcut.Parse(first)!.Value;
        var b = MiddleClickShortcut.Parse(second)!.Value;
        Assert.True(WindowsReader.RegisterHotKey(IntPtr.Zero, 91, b.Modifiers, b.Key));
        try { Assert.Throws<InvalidOperationException>(() => original.Apply(second, () => Assert.Fail("Saved a conflicting shortcut."))); }
        finally { WindowsReader.UnregisterHotKey(IntPtr.Zero, 91); }
        Assert.Throws<IOException>(() => original.Apply(second, () => throw new IOException()));
        Assert.False(WindowsReader.RegisterHotKey(IntPtr.Zero, 91, a.Modifiers, a.Key));
        Assert.Throws<ArgumentException>(() => original.Apply("A", () => { }));
        original.Apply(second, () => { });
        original.Apply("", () => { });
        Assert.False(original.Available);
        Assert.True(WindowsReader.RegisterHotKey(IntPtr.Zero, 91, b.Modifiers, b.Key));
        WindowsReader.UnregisterHotKey(IntPtr.Zero, 91);
    }

}
