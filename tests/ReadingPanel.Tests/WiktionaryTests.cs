using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ReadingPanel.Core;
using Xunit;

namespace ReadingPanel.Tests;

public sealed class WiktionaryTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(reply(request));
        }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task ReadsOnlyEnglishMeaningsAndIpaWithArticleAttribution()
    {
        const string html = """
            <div class="mw-parser-output">
            <div class="mw-heading"><h2>英语</h2><span>[编辑]</span></div>
            <h3>发音</h3><ul><li><span class="IPA">/bæŋk/</span></li></ul>
            <h3>名词 1</h3><ol><li>银行<dl><dd>例句</dd></dl></li><li>河岸<sup>引文</sup></li></ol>
            <h4>近义词</h4><ol><li>忽略此义</li></ol>
            <h2>法语</h2><h3>名词</h3><ol><li>其他语言</li></ol></div>
            """;
        var handler = new Handler(request => {
            Assert.Equal("zh.wiktionary.org", request.RequestUri!.Host);
            Assert.Contains("page=bank", request.RequestUri.Query);
            Assert.Null(request.Headers.Authorization);
            Assert.NotEmpty(request.Headers.UserAgent);
            return Json(new { parse = new { title = "bank", text = html } });
        });
        using var http = new HttpClient(handler);
        var entry = Assert.IsType<Entry>(await new WiktionaryDictionary(http).FindAsync("bank,", default));
        Assert.Equal("n. 银行\nn. 河岸", entry.Meaning);
        Assert.Equal("bæŋk", entry.Phonetic);
        Assert.Equal("https://zh.wiktionary.org/zh-hans/bank", entry.Attribution?.Article.AbsoluteUri);
        Assert.Equal("https://creativecommons.org/licenses/by-sa/4.0/", entry.Attribution?.License.AbsoluteUri);
    }

    [Fact]
    public async Task CapitalisedPhraseFallsBackToTheLowercaseEntry()
    {
        const string html = """
            <div class="mw-parser-output"><h2>英语</h2><h3>翻译</h3>
            <ul><li>汉语：江畔</li><li>法语：autre</li></ul></div>
            """;
        var handler = new Handler(request => request.RequestUri!.Query.Contains("page=River%20bank")
            ? Json(new { error = new { code = "missingtitle" } })
            : Json(new { parse = new { title = "river bank", text = html } }));
        using var http = new HttpClient(handler);
        var entry = Assert.IsType<Entry>(await new WiktionaryDictionary(http).FindAsync("River bank", default));
        Assert.Equal(2, handler.Calls);
        Assert.Equal("River bank", entry.Word);
        Assert.Equal("江畔", entry.Meaning);
        Assert.EndsWith("/river_bank", entry.Attribution!.Article.AbsoluteUri);
    }

    [Fact]
    public async Task NormalizedTitlesAreCachedAndExplicitRetryFetchesAgain()
    {
        var handler = new Handler(_ => Json(new { parse = new { title = "bank",
            text = "<div class='mw-parser-output'><h2>英语</h2><h3>名词</h3><ol><li>银行</li></ol></div>" } }));
        using var http = new HttpClient(handler);
        var dictionary = new WiktionaryDictionary(http);
        await dictionary.FindAsync("bank,", default);
        await dictionary.FindAsync("bank", default);
        Assert.Equal(1, handler.Calls);
        Assert.NotNull(await dictionary.RetryAsync("bank", default));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void ReadPageHonorsCancellationAndStillCollectsLatePhoneticsAfterDefinitionLimit()
    {
        var section = "<h3>名词</h3><ol>" + string.Concat(Enumerable.Range(0, 12).Select(i => $"<li>词义{3*i}</li>")) + "</ol>";
        string html = "<div class='mw-parser-output'><h2>英语</h2>" + section + section.Replace("词义", "释义") +
            "<h3>发音</h3><span><span class='IPA'>/x/</span></span><h3>名词</h3><ol><li>其他</li></ol></div>";
        Assert.Throws<OperationCanceledException>(() => WiktionaryDictionary.ReadPage("x", "x", html, new CancellationToken(true)));
        var entry = WiktionaryDictionary.ReadPage("x", "x", html);
        Assert.Equal("x", entry?.Phonetic);
        Assert.Equal(24, entry!.Meaning.Split('\n').Length);
        Assert.DoesNotContain("其他", entry.Meaning);
    }

    [Fact]
    public async Task SentencesSkipTheDictionaryAndRateLimitsHonorRetryAfter()
    {
        var handler = new Handler(_ => {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(3));
            return response;
        });
        using var http = new HttpClient(handler);
        var dictionary = new WiktionaryDictionary(http);
        Assert.Null(await dictionary.FindAsync("The water rises.", default));
        Assert.Null(await dictionary.FindAsync("This takes more words", default));
        Assert.Equal(0, handler.Calls);
        foreach (var word in new[] { "water", "river" })
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => dictionary.FindAsync(word, default).AsTask());
            Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        }
        Assert.Equal(1, handler.Calls);
    }
}
