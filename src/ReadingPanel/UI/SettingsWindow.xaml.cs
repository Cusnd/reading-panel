using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ReadingPanel.Core;
using ReadingPanel.Integration;
using SpeechMode = ReadingPanel.Core.SpeechMode;

namespace ReadingPanel.UI;

internal enum SettingsSection { General, Lookup, Translation, Speech }

internal partial class SettingsWindow : Window
{
    private readonly LookupController lookup;
    private readonly Action<Settings> saveSettings;
    private readonly StartupRegistration startup;
    private StartupState? initialStartup;
    private readonly Dictionary<string, ProviderOptions> drafts;
    private readonly ITranslationProvider[] services;
    private ITranslationProvider? displayed;
    private readonly SpeechUsage usage;
    private readonly SpeechBilling billing;
    private readonly Func<Pronunciation> createPreview;
    private Pronunciation? preview;
    private bool refreshingUsage;
    private CancellationTokenSource? connection;
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) };

    public SettingsWindow(LookupController lookup, Task<bool> mouseReady, bool hotkeyAvailable, bool toggleAvailable,
        SpeechUsage usage, SpeechBilling billing, bool showSpeech = false, SettingsSection section = SettingsSection.General,
        StartupRegistration? startup = null, Action<Settings>? saveSettings = null, Func<Pronunciation>? createPreview = null)
    {
        this.usage = usage;
        this.billing = billing;
        this.createPreview = createPreview ?? (() => new Pronunciation(usage));
        this.lookup = lookup;
        this.startup = startup ?? new();
        this.saveSettings = saveSettings ?? (draft => {
            MiddleClickShortcut.ValidatePair(draft.LookupShortcut, draft.MiddleClickShortcut);
            lookup.Configure(draft);
        });
        var settings = lookup.Settings;
        drafts = new(settings.Services);
        services = lookup.Providers.ToArray();
        InitializeComponent();
        MaxHeight = Math.Max(1, SystemParameters.WorkArea.Height - 40);
        Title = $"Reading Panel {Program.Edition} 设置";
        EditionBadge.Text = Program.Edition;
#if ONLINE_EDITION
        EditionDetail.Text = "Online · 中文维基词典";
#else
        EditionDetail.Text = "Offline · 离线词典 (ECDICT)";
#endif
        DirectoryPath.Text = Program.DataDirectory;
        try
        {
            initialStartup = this.startup.Read();
            Startup.IsChecked = initialStartup.Enabled;
            Startup.Checked += StartupChanged;
            Startup.Unchecked += StartupChanged;
            UpdateStartupHint();
        }
        catch (Exception e)
        {
            Startup.IsEnabled = false;
            StartupHint.Text = "无法读取 Windows 启动设置。" + e.Message;
        }
        Navigation.SetItems(["常规", "查词", "翻译", "朗读"]);
        Navigation.SelectionChanged += ChangePage;
        Service.SetItems(services.Select(s => s.Name));
        Service.SelectionChanged += ServiceChanged;
        Service.Select(Array.IndexOf(services, lookup.Provider));
        LemmaDefault.SetItems(["默认收起", "默认展开"]);
        LemmaDefault.Select(settings.ExpandLemmasByDefault ? 1 : 0);
        Notifications.IsChecked = settings.ShowStatusNotifications;
        TranslationEnabled.IsChecked = settings.TranslationEnabled;
        ShowMiddleClickEnabled(settings.MiddleClickEnabled);
        LookupKeys.DefaultShortcut = Settings.Default.LookupShortcut;
        ToggleKeys.DefaultShortcut = Settings.Default.MiddleClickShortcut;
        void UpdateKeys()
        {
            ShowBinding(LookupBadge, LookupStatus, LookupKeys.Shortcut, settings.LookupShortcut, hotkeyAvailable);
            ShowBinding(ToggleBadge, ToggleStatus, ToggleKeys.Shortcut, settings.MiddleClickShortcut, toggleAvailable);
        }
        LookupKeys.Shortcut = settings.LookupShortcut;
        ToggleKeys.Shortcut = settings.MiddleClickShortcut;
        LookupKeys.Changed += UpdateKeys;
        ToggleKeys.Changed += UpdateKeys;
        UpdateKeys();
        _ = ShowMouseStatusAsync(mouseReady);
        VoiceMode.ItemsSource = Enum.GetValues<SpeechMode>().ToDictionary(m => m, m => m == SpeechMode.Auto
            ? "自动 · Google 优先，预留 5 万字符时切到 OpenAI" : SpeechPolicy.Label(m));
        VoiceMode.SelectedValue = settings.Speech.Mode;
        VoiceMode.SelectionChanged += (_, _) => { preview?.Stop(); VoiceStatus.Text = ""; };
        SpeechKey.Changed += UpdateEnvHint;
        SpeechKey.Password = settings.Speech.OpenAIKey;
        UpdateEnvHint();
        QuotaProject.Text = settings.Speech.GoogleProject;
        BillingTable.Text = settings.Speech.BillingTable;
        Credential.Changed += CancelConnection;
        Option.TextChanged += (_, _) => CancelConnection();
        QuotaProject.TextChanged += (_, _) => CancelConnection();
        SelectSection(showSpeech ? SettingsSection.Speech : section);
        usage.Changed += UsageChanged;
        RefreshUsage();
        UpdateVoice();
        timer.Tick += TimerTick;
        timer.Start();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2, edge = 0xEAEAEA;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34, ref edge, sizeof(int));
    }

    internal void SelectSection(SettingsSection section) => Navigation.Select((int)section);
    internal void ShowMiddleClickEnabled(bool enabled) => MiddleClickEnabled.IsChecked = enabled;

    private void ChangePage(int index)
    {
        FrameworkElement[] pages = [GeneralPage, LookupPage, TranslationPage, SpeechPage];
        for (int i = 0; i < pages.Length; i++) pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        Body.ScrollToTop();
        if (index != (int)SettingsSection.Speech) preview?.Stop();
        if (index != (int)SettingsSection.Translation) CancelConnection();
    }

    private void StartupChanged(object sender, RoutedEventArgs e) => UpdateStartupHint();
    private void UpdateStartupHint()
    {
        var hints = new List<string>();
        if (Startup.IsChecked == true && initialStartup is { } state)
        {
            if (state.Enabled && !state.MatchesCurrentExecutable) hints.Add("当前指向其他路径，保存后将更新为当前版本。");
            if (state.DisabledByWindows) hints.Add("已在 Windows 任务管理器的“启动应用”中禁用，请前往重新启用。");
        }
        StartupHint.Text = string.Join(" ", hints);
        StartupHint.ToolTip = initialStartup?.RegisteredPath;
    }

    private async Task ShowMouseStatusAsync(Task<bool> ready)
    {
        MouseStatus.Text = "鼠标监听 · 正在加载…";
        bool available;
        try { available = await ready.WaitAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception) { available = false; }
        if (lifetime.IsCancellationRequested) return;
        MouseStatus.Text = available ? "鼠标监听 · 已就绪" : "鼠标监听 · 不可用，请重启 Reading Panel";
        MouseStatus.Foreground = Brush(available ? "#2D6A4F" : "#9C3B30");
    }

    private static SolidColorBrush Brush(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;

    private static void ShowBinding(Border badge, TextBlock label, string draft, string saved, bool available)
    {
        bool changed = !string.Equals(draft, saved, StringComparison.OrdinalIgnoreCase);
        bool disabled = string.IsNullOrWhiteSpace(saved);
        label.Text = changed ? "待保存" : disabled ? "已禁用" : available ? "已注册" : "被占用";
        bool neutral = changed || disabled;
        label.Foreground = Brush(neutral ? "#666666" : available ? "#2D6A4F" : "#9C3B30");
        badge.Background = Brush(neutral ? "#F2F2F2" : available ? "#F1F8F4" : "#FDF3F2");
        badge.BorderBrush = Brush(neutral ? "#EAEAEA" : available ? "#CEE5D8" : "#F0D0CC");
    }

    // Windows sends already-registered combinations to the hotkey window instead of the focused editor.
    internal bool RecordRegisteredShortcut(IntPtr message)
    {
        if (!IsActive) return false;
        var recorder = LookupKeys.Input.IsKeyboardFocused ? LookupKeys : ToggleKeys.Input.IsKeyboardFocused ? ToggleKeys : null;
        if (recorder == null) return false;
        long data = message.ToInt64();
        recorder.Record(KeyInterop.KeyFromVirtualKey((int)(data >> 16) & 0xffff), (ModifierKeys)(data & 15));
        return true;
    }

    private void ResetPreferencesClick(object sender, RoutedEventArgs e)
    {
        var defaults = Settings.Default;
        Notifications.IsChecked = defaults.ShowStatusNotifications;
        MiddleClickEnabled.IsChecked = defaults.MiddleClickEnabled;
        TranslationEnabled.IsChecked = defaults.TranslationEnabled;
        LookupKeys.Shortcut = defaults.LookupShortcut;
        ToggleKeys.Shortcut = defaults.MiddleClickShortcut;
        LemmaDefault.Select(defaults.ExpandLemmasByDefault ? 1 : 0);
        SaveError.Foreground = Brush("#666666");
        SaveError.Text = "已恢复默认偏好，保存后生效。";
    }

    private void RememberFields()
    {
        if (displayed != null) drafts[displayed.Id] = new(Credential.Password.Trim(), Option.Text.Trim());
    }

    private void ServiceChanged(int index)
    {
        RememberFields();
        CancelConnection();
        displayed = services[index];
        var options = drafts.GetValueOrDefault(displayed.Id) ?? new();
        KeyLabel.Text = displayed.ApiKeyLabel;
        CredentialRow.Visibility = string.IsNullOrEmpty(displayed.ApiKeyLabel) ? Visibility.Collapsed : Visibility.Visible;
        OptionRow.Visibility = string.IsNullOrEmpty(displayed.OptionLabel) ? Visibility.Collapsed : Visibility.Visible;
        Credential.Password = options.ApiKey;
        OptionLabel.Text = displayed.OptionLabel == "Model" ? "模型" : displayed.OptionLabel;
        Option.Text = string.IsNullOrEmpty(options.Option) ? displayed.DefaultOption : options.Option;
    }

    private Settings Draft()
    {
        RememberFields();
        return lookup.Settings with {
            Provider = displayed!.Id, Services = new(drafts), Speech = SpeechDraft(),
            TranslationEnabled = TranslationEnabled.IsChecked == true, MiddleClickEnabled = MiddleClickEnabled.IsChecked == true,
            LookupShortcut = LookupKeys.Shortcut, MiddleClickShortcut = ToggleKeys.Shortcut,
            ExpandLemmasByDefault = LemmaDefault.SelectedIndex == 1, ShowStatusNotifications = Notifications.IsChecked == true
        };
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var draft = Draft();
            if (draft.Speech.BillingTable.Length > 0 && !SpeechBilling.ValidTable(draft.Speech.BillingTable))
                throw new ArgumentException("账单表名格式应为 project.dataset.table。");
            bool enabled = Startup.IsChecked == true;
            if (initialStartup is { } state && (state.Enabled || enabled))
                startup.Apply(enabled, () => saveSettings(draft));
            else saveSettings(draft);
            Close();
        }
        catch (Exception error)
        {
            if (error.Data[nameof(MiddleClickShortcut)] is string occupied)
            {
                if (LookupKeys.Shortcut == occupied) ShowBinding(LookupBadge, LookupStatus, occupied, occupied, false);
                if (ToggleKeys.Shortcut == occupied) ShowBinding(ToggleBadge, ToggleStatus, occupied, occupied, false);
            }
            ShowError("无法保存设置。" + error.Message);
        }
    }

    private void ShowError(string message)
    {
        SaveError.Foreground = Brush("#9C3B30");
        SaveError.Text = message;
    }

    private void OpenDirectoryClick(object sender, RoutedEventArgs e)
    {
        try { Program.OpenDataDirectory(); }
        catch (Exception error) { ShowError("无法打开数据目录。" + error.Message); }
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CancelClick(object sender, RoutedEventArgs e) => Close();

    private void CancelConnection()
    {
        connection?.Cancel();
        connection = null;
        TestConnection.IsEnabled = true;
        ConnectionStatus.Text = "";
    }

    private async void TestConnectionClick(object sender, RoutedEventArgs e)
    {
        if (displayed == null || connection != null) return;
        var provider = displayed;
        var options = Draft().For(provider.Id);
        ConnectionStatus.Foreground = Brush("#666666");
        ConnectionStatus.Text = "正在测试…";
        TestConnection.IsEnabled = false;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        connection = request;
        request.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(options.ApiKey))
                throw new ArgumentException("请先输入 API key。");
            var translated = await provider.TranslateAsync(new("Hello"), options, request.Token).WaitAsync(request.Token);
            if (connection == request)
            {
                ConnectionStatus.Foreground = Brush("#2D6A4F");
                ConnectionStatus.Text = "✓ 连接正常 · " + translated;
            }
        }
        catch (Exception error)
        {
            if (connection != request || lifetime.IsCancellationRequested) return;
            ConnectionStatus.Foreground = Brush("#9C3B30");
            ConnectionStatus.Text = error switch {
                OperationCanceledException => "连接超时，请重试。",
                TranslationSetupException or HttpRequestException { StatusCode: not null } => error.Message,
                ArgumentException when string.IsNullOrWhiteSpace(options.ApiKey) => "请先输入 API key。",
                _ => "连接失败，请检查网络与服务设置。"
            };
        }
        finally { if (connection == request) { connection = null; TestConnection.IsEnabled = true; } }
    }

    private void PreviewClick(object sender, RoutedEventArgs e)
    {
        if (preview != null) { preview.Stop(); return; }
        // Each audition uses the current credentials, even if this voice and sample were played before.
        preview = createPreview();
        preview.Changed += PreviewChanged;
        preview.Failed += PreviewFailed;
        VoiceStatus.Text = "";
        preview.Toggle("Reading Panel is ready.", SpeechDraft());
    }

    private void PreviewFailed(string message) => VoiceStatus.Text = message;
    private async void PreviewChanged()
    {
        if (preview == null) return;
        bool playing = preview.Playing;
        PreviewText.Text = playing ? "停止" : "试听";
        AutomationProperties.SetName(Preview, PreviewText.Text);
        PreviewIcon.Content = FindResource(playing ? "StopGlyph" : "VolumeGlyph");
        if (playing) return;
        var finished = preview;
        preview = null;
        finished.Changed -= PreviewChanged;
        finished.Failed -= PreviewFailed;
        try { await finished.ShutdownAsync(); }
        catch (Exception) { if (!lifetime.IsCancellationRequested) VoiceStatus.Text = "无法关闭语音设备。"; }
    }

    private void UpdateEnvHint() => EnvHint.Text = string.IsNullOrWhiteSpace(SpeechKey.Password)
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
        ? "已检测到 OPENAI_API_KEY 环境变量，留空将直接使用" : "";

    private void TimerTick(object? sender, EventArgs e)
    {
        if (Navigation.SelectedIndex == (int)SettingsSection.Speech) RefreshUsage();
    }

    protected override void OnClosed(EventArgs e)
    {
        timer.Stop();
        timer.Tick -= TimerTick;
        usage.Changed -= UsageChanged;
        CancelConnection();
        lifetime.Cancel();
        preview?.Stop();
        lifetime.Dispose();
        base.OnClosed(e);
    }

    private SpeechOptions SpeechDraft() => new((SpeechMode)(VoiceMode.SelectedValue ?? SpeechMode.Auto),
        SpeechKey.Password.Trim(), QuotaProject.Text.Trim(), BillingTable.Text.Trim());

    private void UsageChanged() => Dispatcher.BeginInvoke(() => {
        if (!lifetime.IsCancellationRequested) RenderUsage();
    });

    private async void RefreshUsage()
    {
        if (refreshingUsage || lifetime.IsCancellationRequested) return;
        refreshingUsage = true;
        try
        {
            await usage.ReadAsync(BillingTable.Text.Trim());
            if (!lifetime.IsCancellationRequested) RenderUsage();
        }
        catch (Exception) { if (!lifetime.IsCancellationRequested) GoogleLine.Text = "当前用量记录无法读取。"; }
        finally { refreshingUsage = false; }
    }

    private void RenderUsage()
    {
        try
        {
            var totals = usage.CachedTotals(BillingTable.Text.Trim());
            if (totals == null) { RefreshUsage(); return; }
            UsagePeriod.Text = totals.Month + " · 太平洋时间";
            UsageBar.Value = SpeechPolicy.Allowance - totals.Remaining;
            double fraction = UsageBar.Value / UsageBar.Maximum;
            UsageBar.Foreground = Brush(fraction >= .95 ? "#9C3B30" : fraction >= .85 ? "#B86B22" : "#252525");
            GoogleLine.Text = totals.Billed is { } billed
                ? $"Google 账户已入账 {billed:N0} 字符 · 预计剩余 {totals.Remaining:N0}"
                : $"Google 本机已发送 {totals.Local:N0} / {SpeechPolicy.Allowance:N0} 字符";
            QuotaDetail.Text = totals.Fetched is { } fetched
                ? $"本机待核 {totals.Pending:N0}。查询于 {fetched.ToLocalTime():MM-dd HH:mm}。"
                    + (totals.LastExport is { } last ? $" 当前 SKU 最近导出于 {last.ToLocalTime():MM-dd HH:mm}。" : " 本月未查到此 SKU 的记录。")
                : $"仅按本机估算剩余 {totals.Remaining:N0}。账户实际余量未知。";
            OpenAILine.Text = $"OpenAI 本机 · 输入 {totals.InputTokens:N0} / 输出 {totals.OutputTokens:N0} token · 约 US${totals.OpenAIUsd:0.00000}"
                + (totals.Started > totals.Reported ? $" · {totals.Started - totals.Reported} 笔用量未返回" : "");
        }
        catch (Exception) { GoogleLine.Text = "当前用量记录无法读取。"; QuotaDetail.Text = ""; OpenAILine.Text = ""; }
    }
    private void UpdateVoice()
    {
        bool ready = PiperPack.Find() != null;
        PiperState.Text = ready ? "Piper · John 已安装，离线可用" : "Piper · John 未安装";
        InstallVoice.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
    }
    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        InstallVoice.IsEnabled = false;
        InstallStatus.Text = "正在下载并校验 John 语音包…";
        try
        {
            var ct = lifetime.Token;
            await Task.Run(() => PiperPack.InstallAsync(Path.Combine(Program.DataDirectory, PiperPack.Folder), null, ct), ct);
            InstallStatus.Text = "";
            UpdateVoice();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { InstallStatus.Text = "安装失败。" + error.Message; }
        finally { InstallVoice.IsEnabled = true; }
    }
    private void BillingTableChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        RenderUsage();
    }
    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        var options = SpeechDraft();
        if (!SpeechBilling.ValidTable(options.BillingTable)) { SpeechStatus.Text = "请输入 project.dataset.table 格式的账单导出表。"; return; }
        RefreshBill.IsEnabled = false;
        SpeechStatus.Text = "正在读取账户账单…";
        try { await billing.RefreshIfDueAsync(options, lifetime.Token, true); RenderUsage(); SpeechStatus.Text = ""; }
        catch (OperationCanceledException) { }
        catch (Exception error) { SpeechStatus.Text = error is InvalidOperationException && error.Message.StartsWith("账单")
            ? error.Message : "无法读取账户账单。请检查 ADC、表名和 BigQuery 查询权限。"; }
        finally { RefreshBill.IsEnabled = true; }
    }
    private void GoogleHelp(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://cloud.google.com/docs/authentication/provide-credentials-adc") { UseShellExecute = true });
}
