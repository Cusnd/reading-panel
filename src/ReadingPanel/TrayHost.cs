using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using ReadingPanel.Core;
using ReadingPanel.Integration;
using ReadingPanel.UI;
using Forms = System.Windows.Forms;

namespace ReadingPanel;

internal sealed class TrayHost : IDisposable
{
    private readonly Application app;
    private readonly IWordLookup dictionary;
    private readonly HttpClient http = new(new HttpClientHandler {
        AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(35), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
    private readonly SpeechUsage usage = new(Program.DataDirectory);
    private readonly SpeechBilling billing;
    private readonly Pronunciation speech;
    private readonly System.Windows.Threading.DispatcherTimer billingTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly LookupController lookup;
    private PanelWindow? createdPanel;
    private PanelWindow Panel => createdPanel ??= CreatePanel();
    private readonly Forms.NotifyIcon tray;
    private readonly HwndSource hotkeyWindow;
    private readonly MiddleClickShortcut lookupShortcut;
    private readonly MiddleClickShortcut toggleShortcut;
    private readonly Forms.ToolStripMenuItem middleClickItem = new("中键查词");
    private readonly MiddleClickState middleClick;
    private readonly ClipboardSelection clipboard;
    private readonly DesktopMouse mouse;
    private bool escapeRegistered;
    private IntPtr desktopSource;
    private SettingsWindow? settingsWindow;
    private Selection? editingSelection;
    private IntPtr keyboardSource;
    private (Guid Client, string Id)? browserLookup;
    private long generation;
    private readonly HashSet<IntPtr> keyboardReads = new();

    public TrayHost(Application app)
    {
        this.app = app;
#if ONLINE_EDITION
        dictionary = new WiktionaryDictionary(http);
#else
        dictionary = new WordDictionary(Path.Combine(AppContext.BaseDirectory, "assets", "ecdict.sqlite3"));
#endif
        var store = new SettingsStore(Program.DataDirectory);
        Settings settings;
        string? settingsError = null;
        try { settings = store.Load(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {
            settings = Settings.Default;
            settingsError = "The saved settings could not be read. Open Settings to enter your service details again.";
        }
        middleClick = new(settings.MiddleClickEnabled);
        _ = ListenAsync();
        hotkeyWindow = new HwndSource(new HwndSourceParameters("Reading Panel shortcut") {
            ParentWindow = new IntPtr(-3), WindowStyle = 0
        });
        hotkeyWindow.AddHook(HotkeyMessage);
        lookupShortcut = new(hotkeyWindow.Handle, 1, 5);
        toggleShortcut = new(hotkeyWindow.Handle);
        try { lookupShortcut.Apply(settings.LookupShortcut, () => { }); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { settingsError ??= e.Message; }
        try { toggleShortcut.Apply(settings.MiddleClickShortcut, () => { }); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { settingsError ??= e.Message; }
        clipboard = new(hotkeyWindow.Handle);
        ITranslationProvider[] services = [new DeepLProvider(http), new GoogleCloudProvider(http), new DeepSeekProvider(http)];
        lookup = new(dictionary, services, settings, next => {
            store.Save(next);
            middleClick.Enabled = next.MiddleClickEnabled;
            middleClickItem.Checked = middleClick.Enabled;
        });
        speech = new(usage);
        billing = new(http, usage);
        billingTimer.Tick += (_, _) => RefreshBilling();
        billingTimer.Start();
        RefreshBilling();
        lookup.Changed += () => {
            if (!shutdown.IsCancellationRequested && (createdPanel != null || lookup.Current != null) &&
                (editingSelection == null || lookup.Current?.Source != editingSelection)) Panel.Render();
        };
        speech.Changed += () => createdPanel?.ShowSpeech(speech.Playing, speech.Voice);
        speech.Failed += message => createdPanel?.ShowSpeechError(message);
        mouse = new DesktopMouse(app.Dispatcher, middleClick, clipboard, (selection, window) => {
            if (!shutdown.IsCancellationRequested && middleClick.Enabled && window == WindowsReader.GetForegroundWindow())
                OpenSelection(selection, window);
        }, () => {
            if (!shutdown.IsCancellationRequested && desktopSource != IntPtr.Zero && editingSelection == null) Dismiss();
        });
        var menu = new Forms.ContextMenuStrip();
        middleClickItem.Checked = middleClick.Enabled;
        middleClickItem.Click += (_, _) => ToggleMiddleClick();
        menu.Items.Add(middleClickItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("设置…", null, (_, _) => OpenSettings());
        menu.Items.Add("打开数据目录", null, (_, _) => {
            try { Program.OpenDataDirectory(); }
            catch (Exception e) { Notify(e.Message); }
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => app.Shutdown());
        tray = new Forms.NotifyIcon {
            Text = $"Reading Panel · {Program.Edition}", Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            ContextMenuStrip = menu, Visible = true
        };
        tray.DoubleClick += (_, _) => OpenSettings();
        initialized.SetResult();
        if (settingsError != null) Notify(settingsError);
    }

    private PanelWindow CreatePanel()
    {
        var panel = new PanelWindow(lookup);
        panel.SpeakRequested += Speak;
        panel.SpeechModeChanged += mode => {
            speech.Stop();
            lookup.Configure(lookup.Settings with { Speech = lookup.Settings.Speech with { Mode = mode } });
            panel.ShowSpeech(false);
        };
        panel.SpeechSettingsRequested += () => OpenSettings(fromCard: true, showSpeech: true);
        panel.DismissRequested += () => Dismiss(restoreFocus: true);
        panel.SetupRequested += () => OpenSettings(fromCard: true);
        panel.Error += Notify;
        panel.Deactivated += (_, _) => {
            if (lookup.Current?.Source.Keyboard == true && editingSelection == null) Dismiss();
        };
        return panel;
    }

    private async Task ListenAsync()
    {
        try
        {
            await Task.Run(() => NativeMessaging.ListenAsync(middleClick,
                async (client, message) => {
                    await initialized.Task.WaitAsync(shutdown.Token).ConfigureAwait(false);
                    await app.Dispatcher.InvokeAsync(() => HandleMessage(client, message));
                },
                client => app.Dispatcher.InvokeAsync(() => { if (browserLookup?.Client == client) Dismiss(); }).Task,
                shutdown.Token));
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (IOException) { if (!shutdown.IsCancellationRequested) Notify("The browser connection stopped. Restart Reading Panel to reconnect."); }
    }

    private void HandleMessage(Guid client, ReaderMessage message)
    {
        if (message.Type == "settings") { OpenSettings(); return; }
        if (message.Type == "lookup")
        {
            if (!middleClick.Enabled) return;
            OpenSelection(new(message.Text!.Trim(), WindowsReader.BrowserAnchor(message.Offsets!, message.Point)),
                browserOwner: (client, message.Id!));
        }
        else if (browserLookup == (client, message.Id) && (message.Type != "blur" || editingSelection == null)) Dismiss();
    }

    private IntPtr HotkeyMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message == 0x031D) { clipboard?.ClipboardUpdated(); return IntPtr.Zero; }
        if (message == 0x0312 && settingsWindow?.RecordRegisteredShortcut(l) == true)
        {
            handled = true;
            return IntPtr.Zero;
        }
        if (message == 0x0312 && toggleShortcut.Matches(w))
        {
            handled = true;
            ToggleMiddleClick();
            return IntPtr.Zero;
        }
        if (message == 0x0312 && w == 2 && escapeRegistered)
        {
            handled = true;
            Dismiss();
            return IntPtr.Zero;
        }
        if (message == 0x0312 && lookupShortcut.Matches(w))
        {
            handled = true;
            if (lookup.Current?.Source.Keyboard == true) Dismiss(restoreFocus: true);
            else _ = KeyboardLookupAsync();
        }
        return IntPtr.Zero;
    }

    private void ToggleMiddleClick()
    {
        try
        {
            bool enabled = !middleClick.Enabled;
            lookup.Configure(lookup.Settings with { MiddleClickEnabled = enabled });
            settingsWindow?.ShowMiddleClickEnabled(enabled);
            if (!enabled && editingSelection == null && (desktopSource != IntPtr.Zero || browserLookup != null)) Dismiss();
            if (lookup.Settings.ShowStatusNotifications) Notify(enabled ? "中键查词已开启。" : "中键查词已关闭。");
        }
        catch (Exception e) { Notify(e.Message); }
    }

    private async Task KeyboardLookupAsync()
    {
        var source = WindowsReader.GetForegroundWindow();
        if (source == IntPtr.Zero || !keyboardReads.Add(source)) return;
        long request = ++generation;
        try
        {
            var gesture = mouse.LastGesture;
            Selection? selected = await Task.Run(() => clipboard.Read(source,
                () => !shutdown.IsCancellationRequested && request == Interlocked.Read(ref generation) && source == WindowsReader.GetForegroundWindow(),
                () => WindowsReader.ReadSelection(source), gesture));
            if (request != generation || shutdown.IsCancellationRequested || source != WindowsReader.GetForegroundWindow()) return;
            if (selected == null) { Notify("Select text in a reader that supports Windows text selection."); return; }
            OpenSelection(selected, source);
        }
        catch (IOException e) { if (request == generation) Notify(e.Message); }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException)
        { if (request == generation) Notify("Windows could not read the selection in this app."); }
        finally { keyboardReads.Remove(source); }
    }

    internal void OpenSelection(Selection selection, IntPtr sourceWindow = default, (Guid Client, string Id)? browserOwner = null)
    {
        generation++;
        keyboardSource = selection.Keyboard ? sourceWindow : IntPtr.Zero;
        desktopSource = selection.Keyboard ? IntPtr.Zero : sourceWindow;
        browserLookup = browserOwner;
        editingSelection = null;
        UpdateMouseTracking();
        speech.Stop();
        lookup.Open(selection);
    }

    private void Dismiss(bool restoreFocus = false)
    {
        generation++;
        var source = keyboardSource;
        bool returnToSource = restoreFocus && source != IntPtr.Zero &&
            createdPanel != null && WindowsReader.GetForegroundWindow() == new WindowInteropHelper(createdPanel).Handle;
        keyboardSource = IntPtr.Zero;
        desktopSource = IntPtr.Zero;
        browserLookup = null;
        editingSelection = null;
        UpdateMouseTracking();
        speech.Stop();
        lookup.Close();
        if (returnToSource) WindowsReader.SetForegroundWindow(source);
    }

    private void UpdateMouseTracking()
    {
        var source = editingSelection == null ? desktopSource : IntPtr.Zero;
        mouse.Track(source);
        if (source != IntPtr.Zero && !escapeRegistered)
            escapeRegistered = WindowsReader.RegisterHotKey(hotkeyWindow.Handle, 2, 0x4000, 0x1B); // Escape while the card is open
        else if (source == IntPtr.Zero && escapeRegistered)
        {
            WindowsReader.UnregisterHotKey(hotkeyWindow.Handle, 2);
            escapeRegistered = false;
        }
    }

    private void Speak()
    {
        if (lookup.Current is not { } result) return;
        try { speech.Toggle(result.Source.Text, lookup.Settings.Speech); }
        catch (Exception e) when (e is InvalidOperationException or COMException or PlatformNotSupportedException)
        { createdPanel?.ShowSpeechError("请检查朗读设置和 Windows 音频输出。"); }
    }

    private void Notify(string message) => tray.ShowBalloonTip(3500, "Reading Panel", message, Forms.ToolTipIcon.Info);

    private async void RefreshBilling()
    {
        try { await billing.RefreshIfDueAsync(lookup.Settings.Speech, shutdown.Token); }
        catch (Exception) { /* Explicit refresh in Settings reports the failure. */ }
    }

    public void OpenSettings(bool fromCard = false, bool showSpeech = false)
    {
        if (fromCard && lookup.Current != null)
        {
            speech.Stop();
            editingSelection = lookup.Current.Source;
            UpdateMouseTracking();
            Panel.Hide();
        }
        else if (!fromCard) Dismiss();
        var section = showSpeech ? SettingsSection.Speech : fromCard ? SettingsSection.Translation : SettingsSection.General;
        if (settingsWindow != null)
        {
            settingsWindow.SelectSection(section);
            settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate();
            return;
        }
        settingsWindow = new SettingsWindow(lookup, mouse.Initialization, lookupShortcut.Available, toggleShortcut.Available,
            usage, billing, section: section, saveSettings: next => MiddleClickShortcut.ApplyPair(
                lookupShortcut, next.LookupShortcut, toggleShortcut, next.MiddleClickShortcut, () => lookup.Configure(next)));
        settingsWindow.Closed += (_, _) => {
            settingsWindow = null;
            if (!shutdown.IsCancellationRequested) RefreshBilling();
            if (editingSelection == null) return;
            editingSelection = null;
            if (!shutdown.IsCancellationRequested) { UpdateMouseTracking(); Panel.Render(); }
        };
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    public void Dispose()
    {
        shutdown.Cancel();
        var closingSpeech = speech.ShutdownAsync();
        billingTimer.Stop();
        mouse.Dispose();
        clipboard.Dispose();
        toggleShortcut.Dispose();
        if (escapeRegistered) WindowsReader.UnregisterHotKey(hotkeyWindow.Handle, 2);
        lookupShortcut.Dispose();
        hotkeyWindow.Dispose();
        tray.Visible = false;
        tray.ContextMenuStrip?.Dispose();
        tray.Icon?.Dispose();
        tray.Dispose();
        lookup.Close();
        try { closingSpeech.GetAwaiter().GetResult(); }
        catch (TimeoutException) { /* Exit cleanly if an audio driver or remote socket stalls past the shutdown budget. */ }
        finally
        {
            http.Dispose();
            (dictionary as IDisposable)?.Dispose();
            shutdown.Dispose();
        }
    }
}
