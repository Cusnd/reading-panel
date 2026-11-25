using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReadingPanel.Core;

[JsonConverter(typeof(JsonStringEnumConverter<SpeechMode>))]
public enum SpeechMode { Auto, Google, OpenAI, Piper }

public sealed record SpeechOptions(SpeechMode Mode = SpeechMode.Auto, string OpenAIKey = "", string GoogleProject = "", string BillingTable = "")
{
    [JsonIgnore]
    public string RealtimeKey => string.IsNullOrWhiteSpace(OpenAIKey)
        ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")?.Trim() ?? "" : OpenAIKey;
}

internal static class SpeechPolicy
{
    public const int Allowance = 1_000_000, Reserve = 50_000;
    public static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
    public static string Month(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, Pacific).ToString("yyyy-MM", CultureInfo.InvariantCulture);
    public static string Input(string text)
    {
        text = text.Trim();
        return text.Length > 0 && char.IsLetterOrDigit(text[^1]) ? text + "." : text;
    }
    public static int Characters(string input) => input.EnumerateRunes().Count();
    public static SpeechMode Resolve(SpeechMode mode, long remaining, int characters, bool googleCached)
    {
        if (mode == SpeechMode.Google && !googleCached && characters > remaining)
            throw new InvalidOperationException("Google 本月预计余量不足以朗读这段选区。请选择 OpenAI 或 Piper。");
        return mode == SpeechMode.Auto
            ? googleCached || remaining - characters > Reserve ? SpeechMode.Google : SpeechMode.OpenAI
            : mode;
    }
    public static string Label(SpeechMode mode) => mode switch {
        SpeechMode.Google => "Google · Charon", SpeechMode.OpenAI => "OpenAI · cedar",
        SpeechMode.Piper => "Piper · John", _ => "自动"
    };
}

internal sealed record TokenUsage(long Input, long Output, decimal Usd)
{
    internal static TokenUsage Read(JsonElement usage)
    {
        static long Count(JsonElement value, string key) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var n) && n.TryGetInt64(out var count) ? count : 0;
        long input = Count(usage, "input_tokens"), output = Count(usage, "output_tokens");
        usage.TryGetProperty("input_token_details", out var inputs);
        usage.TryGetProperty("output_token_details", out var outputs);
        // Reading Panel sends text only. Output includes text/reasoning and audio tokens.
        long cached = Math.Clamp(Count(inputs, "cached_tokens"), 0, input);
        long audio = Math.Clamp(Count(outputs, "audio_tokens"), 0, output);
        return new(input, output, ((input - cached) * 0.60m + cached * 0.06m + (output - audio) * 2.40m + audio * 20m) / 1_000_000m);
    }
}

internal sealed record BillingSnapshot(string Table, DateTimeOffset Fetched, Dictionary<string,long> Hours, DateTimeOffset? LastExport = null)
{
    public Dictionary<string,long> MatchedLocal { get; init; } = new();
}
internal sealed class SpeechMonth
{
    public Dictionary<string,long> Google { get; init; } = new();
    public long Started { get; set; }
    public long Reported { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal OpenAIUsd { get; set; }
    public BillingSnapshot? Billing { get; set; }
}
internal sealed record SpeechTotals(string Month, long Local, long? Billed, long Pending, long Remaining,
    DateTimeOffset? Fetched, DateTimeOffset? LastExport, long Started, long Reported, long InputTokens, long OutputTokens, decimal OpenAIUsd);

// Desktops and sessions share these files. Commit each mutation against the latest atomic replacement.
internal sealed class SpeechUsage(string directory, Func<DateTimeOffset>? clock = null)
{
    private readonly object gate = new();
    private readonly string writerName = @"Global\ReadingPanel-Speech-" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())));
    private sealed record CachedMonth(string Month, DateTime Stamp, long Length, SpeechMonth State);
    private volatile CachedMonth? cached;
    internal event Action? Changed;
    internal Task<SpeechTotals> ReadAsync(string table) => Task.Run(() => Read(table));
    internal DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;
    private string PathFor(string month) => Path.Combine(directory, "speech-" + month + ".json");
    internal static string Bucket(string project, DateTimeOffset time) => project + "/" + (time.ToUnixTimeSeconds() / 3600);
    internal SpeechTotals Read(string table)
    {
        lock (gate)
        {
            var month = SpeechPolicy.Month(Now);
            var state = Load(month);
            return Totals(month, state, table);
        }
    }

    internal SpeechTotals? CachedTotals(string table)
    {
        var month = SpeechPolicy.Month(Now);
        var hit = cached;
        return hit?.Month == month ? Totals(month, hit.State, table) : null;
    }

    private static SpeechTotals Totals(string month, SpeechMonth state, string table)
    {
        var cloud = !string.IsNullOrWhiteSpace(table) && state.Billing?.Table == table ? state.Billing : null;
        long local = state.Google.Values.Sum();
        // Within an hour/project, some or all of our requests can already be included in the export.
        long pending = cloud == null ? local : local - cloud.MatchedLocal.Values.Sum();
        long? billed = cloud == null ? null : Math.Max(0, cloud.Hours.Values.Sum());
        return new(month, local, billed, pending, Math.Max(0, SpeechPolicy.Allowance - (billed ?? 0) - pending),
            cloud?.Fetched, cloud?.LastExport, state.Started, state.Reported, state.InputTokens, state.OutputTokens, state.OpenAIUsd);
    }

    internal void GoogleSent(DateTimeOffset at, string project, int chars) => Update(at, month => {
        string key = Bucket(project, at);
        month.Google[key] = month.Google.GetValueOrDefault(key) + chars;
    });
    internal void OpenAIStarted(DateTimeOffset at) => Update(at, m => m.Started++);
    internal void OpenAIReported(DateTimeOffset at, TokenUsage usage) => Update(at, m => {
        m.Reported++; m.InputTokens += usage.Input; m.OutputTokens += usage.Output; m.OpenAIUsd += usage.Usd;
    });
    internal void SetBilling(DateTimeOffset at, BillingSnapshot snapshot, Dictionary<string,long> localAtQuery) => Update(at, m => {
        var previous = m.Billing?.Table == snapshot.Table ? m.Billing : null;
        var matched = new Dictionary<string,long>();
        foreach (var (bucket, local) in localAtQuery)
        {
            long billed = Math.Max(0, snapshot.Hours.GetValueOrDefault(bucket));
            long oldBilled = previous?.Hours.GetValueOrDefault(bucket) ?? 0;
            long oldMatched = previous?.MatchedLocal.GetValueOrDefault(bucket) ?? 0;
            // Re-reading an unchanged export cannot absorb a request sent since the previous query.
            matched[bucket] = Math.Min(local, Math.Min(billed, oldMatched + Math.Max(0, billed - oldBilled)));
        }
        m.Billing = snapshot with { MatchedLocal = matched };
    });
    internal Dictionary<string,long> LocalAt(DateTimeOffset at) { lock (gate) return new(Load(SpeechPolicy.Month(at)).Google); }

    private SpeechMonth Load(string month)
    {
        string file = PathFor(month);
        var info = new FileInfo(file);
        long length = info.Exists ? info.Length : -1;
        var stamp = info.LastWriteTimeUtc;
        if (cached is { } hit && hit.Month == month && hit.Stamp == stamp && hit.Length == length) return hit.State;
        var state = new SpeechMonth();
        if (length >= 0)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            stamp = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            length = stream.Length;
            state = JsonSerializer.Deserialize<SpeechMonth>(stream) ?? throw new InvalidDataException("无法读取本月朗读用量记录。");
        }
        cached = new(month, stamp, length, state);
        return state;
    }
    private void Update(DateTimeOffset at, Action<SpeechMonth> change)
    {
        using var writer = new Mutex(false, writerName);
        try { if (!writer.WaitOne(TimeSpan.FromSeconds(5))) throw new IOException("朗读用量文件正在使用。"); }
        catch (AbandonedMutexException) { /* A previous writer exited. Its atomic replacement is safe to read. */ }
        try
        {
            lock (gate)
            {
                string month = SpeechPolicy.Month(at);
                var previous = Load(month);
                var data = new SpeechMonth {
                    Google = new(previous.Google), Started = previous.Started, Reported = previous.Reported,
                    InputTokens = previous.InputTokens, OutputTokens = previous.OutputTokens, OpenAIUsd = previous.OpenAIUsd,
                    Billing = previous.Billing
                };
                change(data);
                Directory.CreateDirectory(directory);
                string temp = Path.Combine(directory, Path.GetRandomFileName());
                try {
                    File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(data));
                    // Give even equal-length commits a distinct stamp for readers in other sessions.
                    if (cached is { } old && File.GetLastWriteTimeUtc(temp) <= old.Stamp)
                        File.SetLastWriteTimeUtc(temp, old.Stamp.AddMilliseconds(1));
                    File.Move(temp, PathFor(month), true);
                    var info = new FileInfo(PathFor(month));
                    cached = new(month, info.LastWriteTimeUtc, info.Length, data);
                }
                catch { cached = null; throw; }
                finally { File.Delete(temp); }
            }
        }
        finally { writer.ReleaseMutex(); }
        Changed?.Invoke();
    }
}
