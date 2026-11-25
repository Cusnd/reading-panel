using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using ReadingPanel.Core;
using ReadingPanel.Integration;
using ReadingPanel.UI;
using Xunit;

namespace ReadingPanel.Tests;

public class SettingsTests
{
    private const string CurrentExe = @"C:\Reading Panel\ReadingPanel.exe";
    private const string OtherExe = @"D:\Online\ReadingPanel.exe";

    [Fact]
    public void StartupTracksTheExecutableAndPreservesWindowsApprovalAndPreviousCommands()
    {
        string root = TestRegistryPath();
        var startup = Registration(root);
        try
        {
            Assert.Equal(new StartupState(false, false, null, false), startup.Read());
            startup.SetEnabled(false);
            startup.SetEnabled(true);
            using var run = Registry.CurrentUser.OpenSubKey(root + @"\Run", true)!;
            Assert.Equal($"\"{CurrentExe}\" --background", run.GetValue("ReadingPanel"));
            Assert.Equal(new StartupState(true, true, CurrentExe, false), startup.Read());

            run.SetValue("ReadingPanel", CurrentExe.ToUpperInvariant() + " --background");
            Assert.True(startup.Read().MatchesCurrentExecutable);
            run.SetValue("ReadingPanel", "broken command");
            Assert.False(startup.Read().Enabled);
            string oldCommand = $"\"{OtherExe}\" --background";
            run.SetValue("ReadingPanel", oldCommand);
            Assert.Equal(new StartupState(true, false, OtherExe, false), startup.Read());
            Assert.Throws<IOException>(() => startup.Apply(true, () => throw new IOException("disk full")));
            Assert.Equal(oldCommand, run.GetValue("ReadingPanel"));
            Assert.Throws<IOException>(() => startup.Apply(false, () => throw new IOException("disk full")));
            Assert.Equal(oldCommand, run.GetValue("ReadingPanel"));
            const string replacement = @"""C:\New\ReadingPanel.exe"" --background";
            Assert.Throws<IOException>(() => startup.Apply(true, () => {
                run.SetValue("ReadingPanel", replacement);
                throw new IOException("disk full");
            }));
            Assert.Equal(replacement, run.GetValue("ReadingPanel"));

            using var approved = Registry.CurrentUser.CreateSubKey(root + @"\Approval");
            foreach (byte flag in new byte[] { 1, 2, 3, 6, 7 })
            {
                byte[] bytes = [flag, 0, 0, 0];
                approved.SetValue("ReadingPanel", bytes);
                startup.SetEnabled(true);
                Assert.Equal((flag & 1) != 0, startup.Read().DisabledByWindows);
                Assert.Equal(bytes, (byte[])approved.GetValue("ReadingPanel")!);
            }
            startup.SetEnabled(false);
            Assert.False(startup.Read().Enabled);
            Assert.Null(run.GetValue("ReadingPanel"));
            Assert.True(startup.Read().DisabledByWindows);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, false); }
    }

    [Fact]
    public void LegacyProtectedSettingsGainDefaultsAndPersistBothNewPreferences()
    {
        var dir = Directory.CreateTempSubdirectory("reading-panel-prefs-");
        try
        {
            var store = new SettingsStore(dir.FullName);
            var legacy = Encoding.UTF8.GetBytes("""{"Provider":"deepl","Services":{"deepl":{"ApiKey":"retained"}}}""");
            File.WriteAllBytes(Path.Combine(dir.FullName, "settings.dat"), ProtectedData.Protect(legacy, null, DataProtectionScope.CurrentUser));
            var previous = store.Load();
            Assert.Equal("Ctrl+Alt+L", previous.LookupShortcut);
            Assert.True(previous.ShowStatusNotifications);
            store.Save(previous with { LookupShortcut = "", ShowStatusNotifications = false });
            var saved = store.Load();
            Assert.Equal("", saved.LookupShortcut);
            Assert.False(saved.ShowStatusNotifications);
            Assert.Equal("retained", saved.For("deepl").ApiKey);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void BothBindingsSurviveDuplicateOccupiedAndFailedSavesAndCanBeSwapped()
    {
        using var lookup = new MiddleClickShortcut(IntPtr.Zero, 11, 15);
        using var toggle = new MiddleClickShortcut(IntPtr.Zero, 13, 14);
        using var other = new MiddleClickShortcut(IntPtr.Zero, 16, 17);
        const string a = "Ctrl+Shift+F14", b = "Ctrl+Shift+F15", c = "Ctrl+Shift+F16", d = "Ctrl+Shift+F17";
        void Apply(string l, string t, Action save) => MiddleClickShortcut.ApplyPair(lookup, l, toggle, t, save);
        void MustNotSave() => Assert.Fail("Invalid shortcut committed.");
        Apply(a, b, () => { });
        var duplicate = Assert.Throws<ArgumentException>(() => Apply(a, "Shift+Ctrl+F14", MustNotSave));
        Assert.Equal("键盘查词与中键切换快捷键不能相同。", duplicate.Message);
        other.Apply(d, () => { });
        Assert.Throws<InvalidOperationException>(() => Apply(c, d, MustNotSave));
        Assert.Throws<InvalidOperationException>(() => Apply(d, c, MustNotSave));
        other.Apply(c, () => { }); // The staged first binding was released.
        other.Dispose();
        Assert.Throws<IOException>(() => Apply(c, d, () => throw new IOException()));
        Assert.True(lookup.Matches(11));
        Assert.True(toggle.Matches(13));
        Assert.Throws<InvalidOperationException>(() => other.Apply(a, MustNotSave));
        Assert.Throws<InvalidOperationException>(() => other.Apply(b, MustNotSave));
        Apply(b, a, () => { });
        Assert.True(lookup.Matches(13));
        Assert.True(toggle.Matches(11));
        Apply(c, d, () => { });
        Apply("", "", () => { });
        Assert.False(lookup.Available);
        Assert.False(toggle.Available);
        other.Apply(c, () => { });
        other.Apply(d, () => { });
    }

    private static string TestRegistryPath() => @"Software\ReadingPanel-Test-" + Guid.NewGuid().ToString("N");
    private static StartupRegistration Registration(string root) => new(root + @"\Run", root + @"\Approval", () => CurrentExe);

    private sealed class EmptyDictionary : IWordDictionary
    {
        public Entry? Find(string text) => null;
    }

    private sealed class DraftProvider(string id, string name) : ITranslationProvider
    {
        public string Id => id;
        public string Name => name;
        public bool RequiresApiKey => id != "google";
        public string ApiKeyLabel => "API key";
        public string OptionLabel => id == "deepseek" ? "Model" : "";
        public (Translation Input, ProviderOptions Options, CancellationToken Token)? Last;
        public TaskCompletionSource<string>? Response;
        public Task<string> TranslateAsync(Translation input, ProviderOptions options, CancellationToken token)
        {
            Last = (input, options, token);
            return Response?.Task ?? Task.FromResult("你好");
        }
    }

    private sealed class WindowFixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("reading-panel-window-");
        private readonly string root = TestRegistryPath();
        private readonly HttpClient http = new();
        internal readonly DraftProvider Google = new("google", "Google Cloud"), DeepSeek = new("deepseek", "DeepSeek");
        internal readonly TaskCompletionSource<bool> Mouse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly StartupRegistration Startup;
        internal readonly SettingsStore Store;
        internal readonly LookupController Controller;
        internal readonly SettingsWindow Window;
        internal Task<CancellationToken>? PlaybackStarted;

        internal WindowFixture(bool showSpeech = false, SettingsSection section = SettingsSection.General)
        {
            Startup = Registration(root);
            Store = new(directory.FullName);
            var speech = new SpeechUsage(directory.FullName);
            var original = Settings.Default with { Services = new() { ["deepl"] = new("original") },
                Speech = new(OpenAIKey: "voice-secret"), MiddleClickEnabled = false };
            Controller = new(new EmptyDictionary(), [new DraftProvider("deepl", "DeepL"), Google, DeepSeek], original, Store.Save);
            Window = new SettingsWindow(Controller, Mouse.Task, true, false, speech, new(http, speech), showSpeech, section, Startup,
                createPreview: () => {
                    var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
                    PlaybackStarted = started.Task;
                    return new Pronunciation(speech, (_, token) => {
                        started.SetResult(token);
                        token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                        throw new OperationCanceledException(token);
                    });
                }) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            Window.Show();
        }

        public void Dispose()
        {
            Window.Close();
            Registry.CurrentUser.DeleteSubKeyTree(root, false);
            directory.Delete(true);
            http.Dispose();
        }
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(false, 2, 2)]
    [InlineData(true, 0, 3)]
    public Task SettingsOpensTheRequestedSectionAndRendersAllPages(bool showSpeech, int section, int initial) => OnSta(async () => {
        using var fixture = new WindowFixture(showSpeech, (SettingsSection)section);
        var window = fixture.Window;
        Assert.Equal(initial, window.Navigation.SelectedIndex);
        FrameworkElement[] controls = [window.Startup, window.LookupKeys.Input, window.Credential.Masked, window.VoiceMode];
        for (int i = 0; i < controls.Length; i++)
        {
            var tab = (RadioButton)window.Navigation.Choices.Children[i];
            tab.IsChecked = true;
            window.UpdateLayout();
            Assert.True(controls[i].IsVisible && controls[i].ActualHeight > 0 && controls[i].ActualWidth > 0);
            Assert.True(window.Navigation.Indicator.ActualWidth > 0);
            Assert.Equal(i, window.Navigation.SelectedIndex);
        }
        window.GoogleAccount.IsExpanded = true;
        await Task.Delay(200);
        window.BillingTable.BringIntoView();
        window.UpdateLayout();
        Assert.True(window.BillingTable.IsVisible && window.BillingTable.ActualHeight > 0);
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(window.Startup);
        Assert.NotNull(peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle));

        Assert.Contains("正在加载", window.MouseStatus.Text);
        fixture.Mouse.SetResult(true);
        var deadline = Environment.TickCount64 + 2000;
        while (!window.MouseStatus.Text.Contains("已就绪") && Environment.TickCount64 < deadline)
            await Task.Delay(15);
        Assert.Contains("已就绪", window.MouseStatus.Text);
        Assert.Equal("被占用", window.ToggleStatus.Text);
    });

    [Fact]
    public Task TranslationTestsUseDraftCredentialsAndDiscardCancelledResults() => OnSta(async () => {
        using var fixture = new WindowFixture(section: SettingsSection.Translation);
        var window = fixture.Window;
        window.Service.Select(2);
        window.Credential.Password = "test-deepseek";
        window.Option.Text = "draft-model";
        Click(window.TestConnection);
        Assert.Equal(new ProviderOptions("test-deepseek", "draft-model"), fixture.DeepSeek.Last!.Value.Options);
        Assert.Equal("Hello", fixture.DeepSeek.Last.Value.Input.Text);
        Assert.Contains("你好", window.ConnectionStatus.Text);

        window.QuotaProject.Text = "draft-project";
        window.Service.Select(1);
        Click(window.TestConnection);
        var google = fixture.Google;
        Assert.Equal(new ProviderOptions("", "draft-project"), google.Last!.Value.Options);
        google.Response = new();
        Click(window.TestConnection);
        Assert.False(window.TestConnection.IsEnabled);
        window.Service.Select(2);
        Assert.True(google.Last!.Value.Token.IsCancellationRequested);
        google.Response.SetResult("late");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Empty(window.ConnectionStatus.Text);
        Assert.Equal("test-deepseek", window.Credential.Password);
        Assert.Equal("draft-model", window.Option.Text);

        window.Service.Select(1);
        google.Response = new();
        Click(window.TestConnection);
        window.Close();
        Assert.True(google.Last!.Value.Token.IsCancellationRequested);
        google.Response.SetResult("late");
        Assert.Equal("deepl", fixture.Controller.Settings.Provider);
        Assert.False(fixture.Controller.Settings.Services.ContainsKey("deepseek"));
    });

    [Fact]
    public Task SaveValidatesBothShortcutsAndBillingBeforeCommittingTheDraft() => OnSta(() => {
        using var fixture = new WindowFixture();
        var window = fixture.Window;
        window.Service.Select(2);
        window.Option.Text = "draft-model";
        Assert.Equal(Visibility.Visible, window.Credential.Masked.Visibility);
        Assert.Equal(Visibility.Collapsed, window.Credential.Plain.Visibility);
        window.Credential.Eye.IsChecked = true;
        window.Credential.Eye.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        window.Credential.Plain.Text = "edited-key";
        Assert.Equal("edited-key", window.Credential.Password);

        window.LookupKeys.Record(Key.K, ModifierKeys.Shift);
        Assert.Equal("Shift+K", window.LookupKeys.Shortcut);
        window.LookupKeys.Record(Key.K, ModifierKeys.Control | ModifierKeys.Alt);
        window.LookupKeys.Record(Key.Tab, ModifierKeys.Control);
        Assert.Equal("Ctrl+Alt+K", window.LookupKeys.Shortcut);
        window.ToggleKeys.Shortcut = "Alt+Ctrl+K";
        window.Startup.IsChecked = true;
        window.Notifications.IsChecked = false;
        window.TranslationEnabled.IsChecked = false;
        window.LemmaDefault.Select(1);
        Click(window.SaveButton);
        Assert.Contains("不能相同", window.SaveError.Text);
        Assert.False(fixture.Startup.Read().Enabled);
        window.ToggleKeys.Record(Key.F24, ModifierKeys.None);
        window.BillingTable.Text = "invalid";
        Click(window.SaveButton);
        Assert.True(window.IsVisible);
        Assert.Contains("账单表名", window.SaveError.Text);
        Assert.False(fixture.Startup.Read().Enabled);
        window.BillingTable.Clear();
        Click(window.SaveButton);
        Assert.False(window.IsVisible);
        Assert.True(fixture.Startup.Read().MatchesCurrentExecutable);
        var saved = fixture.Store.Load();
        Assert.Equal("Ctrl+Alt+K", saved.LookupShortcut);
        Assert.Equal("F24", saved.MiddleClickShortcut);
        Assert.False(saved.ShowStatusNotifications);
        Assert.False(saved.TranslationEnabled);
        Assert.True(saved.ExpandLemmasByDefault);
        Assert.Equal("original", saved.For("deepl").ApiKey);
        Assert.Equal(new ProviderOptions("edited-key", "draft-model"), saved.For("deepseek"));
        Assert.Equal("voice-secret", saved.Speech.OpenAIKey);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ResetOnlyChangesDraftPreferencesAndClosingStopsTheAudition() => OnSta(async () => {
        using var fixture = new WindowFixture(showSpeech: true);
        var window = fixture.Window;
        window.Startup.IsChecked = true;
        window.ToggleKeys.Shortcut = "";
        window.Notifications.IsChecked = false;
        Click(window.ResetPreferences);
        Assert.True(window.Startup.IsChecked);
        Assert.Equal(Settings.Default.MiddleClickShortcut, window.ToggleKeys.Shortcut);
        Assert.True(window.Notifications.IsChecked);
        Assert.True(window.MiddleClickEnabled.IsChecked);
        Assert.Equal("original", window.Credential.Password);
        Assert.Equal("voice-secret", window.SpeechKey.Password);

        window.VoiceMode.SelectedValue = Core.SpeechMode.Piper;
        Click(window.Preview);
        Assert.Equal("停止", window.PreviewText.Text);
        var first = await fixture.PlaybackStarted!.WaitAsync(TimeSpan.FromSeconds(3));
        Click(window.Preview);
        Assert.Equal("试听", window.PreviewText.Text);
        Assert.True(first.IsCancellationRequested);
        Click(window.Preview);
        var second = await fixture.PlaybackStarted!.WaitAsync(TimeSpan.FromSeconds(3));
        window.Close();
        Assert.True(second.IsCancellationRequested);
        Assert.False(fixture.Startup.Read().Enabled);
        Assert.False(fixture.Controller.Settings.MiddleClickEnabled);
    });

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static Task OnSta(Func<Task> verify)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => {
                try { await verify(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
