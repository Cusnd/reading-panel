using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace ReadingPanel.Integration;

internal sealed record StartupState(bool Enabled, bool MatchesCurrentExecutable, string? RegisteredPath, bool DisabledByWindows);

internal sealed class StartupRegistration(
    string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run",
    string approvalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
    Func<string?>? executablePath = null)
{
    private const string Name = "ReadingPanel";
    private string CurrentPath => Path.GetFullPath((executablePath == null ? Environment.ProcessPath : executablePath())
        ?? throw new InvalidOperationException("无法确定当前程序路径。"));

    public StartupState Read()
    {
        using var run = Registry.CurrentUser.OpenSubKey(runKey);
        using var approval = Registry.CurrentUser.OpenSubKey(approvalKey);
        string? path = ParsePath(run?.GetValue(Name) as string);
        bool disabled = approval?.GetValue(Name) is byte[] { Length: > 0 } bytes && (bytes[0] & 1) != 0;
        return new(path != null, string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase), path, disabled);
    }

    private static string? ParsePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var match = Regex.Match(command.Trim(),
            @"^(?:""(?<exe>[^""]+\.exe)""|(?<exe>[^""]+?\.exe))(?=\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string path = match.Groups["exe"].Value;
        try { return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    public void SetEnabled(bool enabled) => Apply(enabled, () => { });

    // Keep the previous command (including another edition's path) if saving the preferences fails.
    public void Apply(bool enabled, Action save)
    {
        using var run = enabled ? Registry.CurrentUser.CreateSubKey(runKey) : Registry.CurrentUser.OpenSubKey(runKey, true);
        var previous = run?.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        string? command = enabled ? $"\"{CurrentPath}\" --background" : null;
        if (Equals(previous, command)) { save(); return; }
        var kind = previous == null ? RegistryValueKind.None : run!.GetValueKind(Name);
        if (enabled) run!.SetValue(Name, command!, RegistryValueKind.String);
        else run!.DeleteValue(Name, false);
        try { save(); }
        catch
        {
            // Another edition may have replaced the entry while the settings file was being written.
            if (Equals(run!.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), command))
            {
                if (previous == null) run.DeleteValue(Name, false);
                else run.SetValue(Name, previous, kind);
            }
            throw;
        }
    }
}
