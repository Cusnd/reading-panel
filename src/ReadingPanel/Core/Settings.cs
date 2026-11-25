using System.Security.Cryptography;
using System.Text.Json;

namespace ReadingPanel.Core;

public sealed record ProviderOptions(string ApiKey = "", string Option = "");
public sealed record Settings(string Provider, Dictionary<string, ProviderOptions> Services)
{
    public SpeechOptions Speech { get; init; } = new();
    public bool MiddleClickEnabled { get; init; } = true;
    public bool ExpandLemmasByDefault { get; init; }
    public string MiddleClickShortcut { get; init; } = "Ctrl+Alt+M";
    public string LookupShortcut { get; init; } = "Ctrl+Alt+L";
    public bool ShowStatusNotifications { get; init; } = true;
    public bool TranslationEnabled { get; init; } = true;
    public static Settings Default => new("deepl", new());
    public ProviderOptions For(string id)
    {
        var options = Services.GetValueOrDefault(id) ?? new();
        return id == GoogleCloudProvider.ServiceId && string.IsNullOrWhiteSpace(options.ApiKey)
            ? options with { Option = Speech.GoogleProject } : options;
    }
}

public sealed class SettingsStore(string directory)
{
    private readonly string file = Path.Combine(directory, "settings.dat");
    public Settings Load()
    {
        if (!File.Exists(file))
        {
            var defaults = Settings.Default;
            try { Write(defaults, overwrite: false); return defaults; }
            catch (IOException) when (File.Exists(file)) { } // Another desktop initialized the shared settings first.
        }
        byte[] data = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
        var settings = JsonSerializer.Deserialize<Settings>(data);
        if (settings?.Services is null) throw new InvalidDataException("Invalid Reading Panel settings.");
        return settings.Provider == "microsoft" ? settings with { Provider = GoogleCloudProvider.ServiceId } : settings;
    }

    public void Save(Settings settings) => Write(settings, overwrite: true);

    private void Write(Settings settings, bool overwrite)
    {
        Directory.CreateDirectory(directory);
        byte[] data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(settings), null, DataProtectionScope.CurrentUser);
        string temporary = Path.Combine(directory, Path.GetRandomFileName());
        try { File.WriteAllBytes(temporary, data); File.Move(temporary, file, overwrite); }
        finally { File.Delete(temporary); }
    }
}
