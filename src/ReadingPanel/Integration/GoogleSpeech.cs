using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Api.Gax.Grpc;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.TextToSpeech.V1;
using ReadingPanel.Core;

namespace ReadingPanel.Integration;

internal sealed class GoogleSpeech : IDisposable
{
    private TextToSpeechClient? client;
    private Grpc.Core.ChannelBase? ownedChannel;
    private string? configuredProject;
    private string project = "";
    internal async Task SpeakAsync(string input, SpeechOptions options, SpeechUsage usage, Action<byte[]> audio, CancellationToken ct)
    {
        if (client == null || configuredProject != options.GoogleProject)
        {
            await CloseClientAsync().ConfigureAwait(false);
            var credential = await GoogleCloudCredentials.GetAsync(options.GoogleProject, ct);
            project = credential.QuotaProject ?? (credential.UnderlyingCredential as ServiceAccountCredential)?.ProjectId ?? "";
            var builder = new TextToSpeechClientBuilder { GoogleCredential = credential };
            TextToSpeechClient next;
            try { next = await builder.BuildAsync(ct); }
            catch { await ShutdownChannelAsync(builder.LastCreatedChannel).ConfigureAwait(false); throw; }
            client = next;
            ownedChannel = builder.LastCreatedChannel;
            configuredProject = options.GoogleProject;
        }
        using var rpc = client.StreamingSynthesize(CallSettings.FromCancellationToken(ct));
        await rpc.WriteAsync(new StreamingSynthesizeRequest { StreamingConfig = new() {
            Voice = new() { Name = "en-US-Chirp3-HD-Charon", LanguageCode = "en-US" },
            StreamingAudioConfig = new() { AudioEncoding = AudioEncoding.Pcm, SampleRateHertz = 24000 }
        }});
        var receiver = ReceiveAsync();
        try
        {
            foreach (string chunk in Chunks(input))
            {
                ct.ThrowIfCancellationRequested();
                var sent = usage.Now;
                await Task.Run(() => usage.GoogleSent(sent, project, SpeechPolicy.Characters(chunk)), ct).ConfigureAwait(false);
                await rpc.WriteAsync(new StreamingSynthesizeRequest { Input = new() { Text = chunk } });
            }
            await rpc.WriteCompleteAsync();
            await receiver;
        }
        catch
        {
            rpc.Dispose();
            try { await receiver; } catch { }
            throw;
        }
        async Task ReceiveAsync()
        {
            await foreach (var response in rpc.GetResponseStream())
                if (!response.AudioContent.IsEmpty) audio(response.AudioContent.ToByteArray());
        }
    }

    private static Task ShutdownChannelAsync(Grpc.Core.ChannelBase? channel)
    {
        if (channel == null) return Task.CompletedTask;
        (channel as IDisposable)?.Dispose();
        return channel.ShutdownAsync();
    }

    private Task CloseClientAsync()
    {
        var channel = ownedChannel;
        ownedChannel = null;
        client = null;
        return ShutdownChannelAsync(channel);
    }

    public void Dispose() => CloseClientAsync().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();

    // Each streaming message must fit Google's UTF-8 byte limit. Keep words and sentence boundaries when possible.
    internal static IEnumerable<string> Chunks(string input)
    {
        int start = 0, i = 0, lastSpace = -1, bytes = 0;
        while (i < input.Length)
        {
            var rune = Rune.TryGetRuneAt(input, i, out var value) ? value : Rune.ReplacementChar;
            if (bytes + rune.Utf8SequenceLength > 4500)
            {
                int end = lastSpace > start ? lastSpace : i;
                yield return input[start..end];
                start = i = end; bytes = 0; lastSpace = -1;
                continue;
            }
            bytes += rune.Utf8SequenceLength;
            i += rune.Utf16SequenceLength;
            if (Rune.IsWhiteSpace(rune)) lastSpace = i;
        }
        if (start < i) yield return input[start..i];
    }
}

internal sealed class SpeechBilling(HttpClient http, SpeechUsage usage, Func<string,CancellationToken,Task<GoogleCredential>>? credentials = null)
{
    private readonly SemaphoreSlim gate = new(1);
    internal async Task RefreshIfDueAsync(SpeechOptions options, CancellationToken ct, bool force = false)
    {
        if (options.BillingTable.Length == 0) return;
        await gate.WaitAsync(ct);
        try
        {
            if (!force && (await usage.ReadAsync(options.BillingTable).ConfigureAwait(false)).Fetched is { } time && usage.Now - time < TimeSpan.FromHours(1)) return;
            await Task.Run(() => RefreshAsync(options, ct), ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    internal static bool ValidTable(string table) =>
        Regex.IsMatch(table, @"\A[a-z][a-z0-9-]{4,61}[a-z0-9]\.[A-Za-z0-9_]+\.[A-Za-z0-9_]+\z", RegexOptions.CultureInvariant);

    internal async Task RefreshAsync(SpeechOptions options, CancellationToken ct)
    {
        string table = options.BillingTable;
        if (!ValidTable(table)) throw new ArgumentException("请输入 project.dataset.table 格式的 Cloud Billing 导出表。");
        var now = usage.Now;
        var localAtQuery = usage.LocalAt(now);
        var credential = await (credentials ?? GoogleCloudCredentials.GetAsync)(options.GoogleProject, ct);
        string token = await credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: ct);
        string jobProject = table.Split('.')[0];
        string root = "https://bigquery.googleapis.com/bigquery/v2/projects/" + jobProject + "/queries";
        string sql = $"""
            SELECT IFNULL(project.id, ''), DIV(UNIX_SECONDS(usage_start_time),3600), usage.unit,
                   CAST(ROUND(SUM(usage.amount)) AS INT64), UNIX_SECONDS(MAX(export_time))
            FROM `{table}`
            WHERE sku.id = 'F977-2280-6F1B'
              AND usage_start_time >= TIMESTAMP(@month, 'America/Los_Angeles')
              AND usage_start_time < TIMESTAMP(DATE_ADD(@month, INTERVAL 1 MONTH), 'America/Los_Angeles')
            GROUP BY 1,2,3
            """;
        using var query = new HttpRequestMessage(HttpMethod.Post, root) {
            Content = JsonContent.Create(new { query = sql, useLegacySql = false, parameterMode = "NAMED", timeoutMs = 10000,
                queryParameters = new[] { new { name = "month", parameterType = new { type = "DATE" },
                    parameterValue = new { value = SpeechPolicy.Month(now) + "-01" } } } })
        };
        JsonElement result = await GetAsync(query);
        var hours = new Dictionary<string,long>();
        DateTimeOffset? latest = null;
        for (;;)
        {
            bool complete = result.TryGetProperty("jobComplete", out var done) && done.GetBoolean();
            if (complete && result.TryGetProperty("rows", out var rows))
            {
                foreach (var row in rows.EnumerateArray())
                {
                    var f = row.GetProperty("f");
                    string Cell(int i) => f[i].GetProperty("v").GetString() ?? "";
                    if (Cell(2).ToLowerInvariant() is not ("count" or "character" or "characters"))
                        throw new InvalidDataException("账单表的用量单位无法换算为字符。");
                    string key = Cell(0) + "/" + Cell(1);
                    hours[key] = hours.GetValueOrDefault(key) + long.Parse(Cell(3), CultureInfo.InvariantCulture);
                    var exported = DateTimeOffset.FromUnixTimeSeconds(long.Parse(Cell(4), CultureInfo.InvariantCulture));
                    if (latest == null || exported > latest) latest = exported;
                }
            }
            string? page = result.TryGetProperty("pageToken", out var cursor) ? cursor.GetString() : null;
            if (complete && string.IsNullOrEmpty(page)) break;
            var job = result.GetProperty("jobReference");
            string location = job.TryGetProperty("location", out var region) ? region.GetString() ?? "US" : "US";
            string next = root + "/" + Uri.EscapeDataString(job.GetProperty("jobId").GetString()!)
                + "?location=" + Uri.EscapeDataString(location) + "&timeoutMs=10000";
            if (!string.IsNullOrEmpty(page)) next += "&pageToken=" + Uri.EscapeDataString(page);
            using var fetch = new HttpRequestMessage(HttpMethod.Get, next);
            result = await GetAsync(fetch);
        }
        usage.SetBilling(now, new(table, DateTimeOffset.UtcNow, hours, latest), localAtQuery);

        async Task<JsonElement> GetAsync(HttpRequestMessage request)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrEmpty(credential.QuotaProject)) request.Headers.Add("x-goog-user-project", credential.QuotaProject);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"账单查询失败（HTTP {(int)response.StatusCode}）。请检查表名和 BigQuery 读取、查询权限。");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            return json.RootElement.Clone();
        }
    }
}
