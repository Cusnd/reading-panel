using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using System.Net;
using ReadingPanel.Core;
using ReadingPanel.Integration;
using Xunit;

namespace ReadingPanel.Tests;

public class SpeechTests
{
    [Theory]
    [InlineData(SpeechMode.Auto, 50009, 8, false, SpeechMode.Google)]
    [InlineData(SpeechMode.Auto, 50008, 8, false, SpeechMode.OpenAI)]
    [InlineData(SpeechMode.Auto, 0, 8, true, SpeechMode.Google)]
    [InlineData(SpeechMode.Google, 8, 8, false, SpeechMode.Google)]
    [InlineData(SpeechMode.Google, 0, 8, true, SpeechMode.Google)]
    [InlineData(SpeechMode.OpenAI, 999999, 8, false, SpeechMode.OpenAI)]
    [InlineData(SpeechMode.Piper, 0, 8, false, SpeechMode.Piper)]
    public void RouteIncludesCurrentRequestAndHonorsPreference(SpeechMode preference, long remaining, int size, bool cached, SpeechMode expected) =>
        Assert.Equal(expected, SpeechPolicy.Resolve(preference, remaining, size, cached));

    [Fact]
    public void GoogleCountsTheActualSubmittedUnicodeTextAndKeepsEveryChunk()
    {
        Assert.Throws<InvalidOperationException>(() => SpeechPolicy.Resolve(SpeechMode.Google, 7, 8, false));
        Assert.Equal(8, SpeechPolicy.Characters(SpeechPolicy.Input("  yielded ")));
        string text = string.Concat(Enumerable.Repeat("𝕏 hello 🌎. ", 1200));
        var chunks = GoogleSpeech.Chunks(text).ToArray();
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, chunk => Assert.InRange(Encoding.UTF8.GetByteCount(chunk), 1, 4500));
        Assert.Equal(13200, chunks.Sum(SpeechPolicy.Characters));
    }

    [Fact]
    public void MonthlyLedgerReconcilesProjectsAndPreservesLateUsage()
    {
        var temp = Directory.CreateTempSubdirectory("speech-usage-test-");
        try
        {
            var clock = DateTimeOffset.Parse("2026-12-01T07:59:59Z");
            var november = clock;
            const string table = "account.dataset.billing";
            var ledger = new SpeechUsage(temp.FullName, () => clock);
            ledger.GoogleSent(clock, "project1", 80);
            ledger.OpenAIStarted(november);
            var export = new BillingSnapshot(table, clock, new() {
                [SpeechUsage.Bucket("project1", clock)] = 60, [SpeechUsage.Bucket("project2", clock)] = 950_000 });
            ledger.SetBilling(clock, export, ledger.LocalAt(clock));
            var totals = ledger.Read(table);
            Assert.Equal("2026-11", totals.Month);
            Assert.Equal(950060L, totals.Billed);
            Assert.Equal(20, totals.Pending);
            Assert.Equal(49920, totals.Remaining);
            ledger.GoogleSent(clock, "project2", 15);
            Assert.Equal(49905, ledger.Read(table).Remaining);
            ledger.SetBilling(clock, export, ledger.LocalAt(clock));
            Assert.Equal(49905, ledger.Read(table).Remaining);
            Assert.Null(ledger.Read("other.dataset.billing").Billed);
            clock = clock.AddSeconds(1);
            Assert.Equal("2026-12", ledger.Read(table).Month);
            Assert.Equal(SpeechPolicy.Allowance, ledger.Read(table).Remaining);
            ledger.OpenAIReported(november, new(15, 40, .0003m));
            Assert.Equal(0, ledger.Read(table).Reported);
            clock = november;
            Assert.Equal(1, new SpeechUsage(temp.FullName, () => clock).Read(table).Reported);
        }
        finally { temp.Delete(true); }
    }

    [Fact]
    public void OldSettingsAndRealtimeTokenBreakdown()
    {
        var old = JsonSerializer.Deserialize<Settings>("""{"Provider":"microsoft","Services":{}}""")!;
        Assert.Equal(SpeechMode.Auto, old.Speech.Mode);
        Assert.DoesNotContain("RealtimeKey", JsonSerializer.Serialize(old));
        using var packet = JsonDocument.Parse("""{"input_tokens":120,"input_token_details":{"cached_tokens":20},"output_tokens":100,"output_token_details":{"audio_tokens":80}}""");
        Assert.Equal(new TokenUsage(120,100,.0017092m), TokenUsage.Read(packet.RootElement));
    }

    [Fact]
    public async Task BillingExportUsesTheAccountSkuAndReadsEveryResultsPage()
    {
        var temp = Directory.CreateTempSubdirectory("speech-billing-test-");
        try
        {
            var ledger = new SpeechUsage(temp.FullName);
            var options = new SpeechOptions(BillingTable: "export-project.dataset.billing");
            int calls = 0;
            using var http = new HttpClient(new Handler(async (req, ct) => {
                calls++;
                Assert.Equal("Bearer sample", req.Headers.Authorization?.ToString());
                if (calls == 1)
                {
                    var body = await req.Content!.ReadAsStringAsync(ct);
                    using var parsed = JsonDocument.Parse(body);
                    string sql = parsed.RootElement.GetProperty("query").GetString()!;
                    Assert.Contains("F977-2280-6F1B", sql);
                    Assert.Contains(options.BillingTable, sql);
                    Assert.DoesNotContain("cost >", sql);
                }
                else Assert.Contains("pageToken=more", req.RequestUri!.Query);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(calls == 1
                    ? """{"jobComplete":true,"jobReference":{"jobId":"abc","location":"EU"},"pageToken":"more","rows":[{"f":[{"v":"project-a"},{"v":"12"},{"v":"count"},{"v":"950000"},{"v":"12345"}]}]}"""
                    : """{"jobComplete":true,"rows":[{"f":[{"v":"project-b"},{"v":"12"},{"v":"count"},{"v":"400"},{"v":"12500"}]}]}""") };
            }));
            var billing = new SpeechBilling(http, ledger, (_,_) => Task.FromResult(GoogleCredential.FromAccessToken("sample")));
            await billing.RefreshIfDueAsync(options, default);
            await billing.RefreshIfDueAsync(options, default);
            var result = ledger.Read(options.BillingTable);
            Assert.Equal(2, calls);
            Assert.Equal(950400, result.Billed);
            Assert.Equal(49600, result.Remaining);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(12500), result.LastExport);
        }
        finally { temp.Delete(true); }
    }

    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
