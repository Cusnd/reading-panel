using System.Windows;
using ReadingPanel.Integration;

namespace ReadingPanel;

internal static class Program
{
#if ONLINE_EDITION
    internal const string Edition = "Online";
#else
    internal const string Edition = "Offline";
#endif
    internal static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReadingPanel");

    internal static void OpenDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DataDirectory) { UseShellExecute = true });
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--prepare-piper", _, ..] && args.Length is 2 or 3)
        {
            try { PiperPack.InstallAsync(Path.GetFullPath(args[1]), args.Length == 3 ? args[2] : null, CancellationToken.None).GetAwaiter().GetResult(); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        }
        bool background = args is ["--background"];
        bool browser = args.FirstOrDefault()?.StartsWith("chrome-extension://", StringComparison.Ordinal) == true;
        try
        {
            if (browser)
            {
                if (args[0] != NativeMessaging.Origin) return 1;
                NativeMessaging.RelayAsync().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length != 0 && !background) return 1;
            using var instance = new Mutex(true, @"Local\" + NativeMessaging.Pipe, out bool first);
            if (!first)
            {
                if (!background) NativeMessaging.ShowExistingAsync().GetAwaiter().GetResult();
                return 0;
            }
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
                using var host = new TrayHost(app);
                if (!background) host.OpenSettings();
                return app.Run();
            }
            finally { instance.ReleaseMutex(); }
        }
        catch (Exception e)
        {
            if (!browser && !background)
                MessageBox.Show(e.Message, "Reading Panel", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
