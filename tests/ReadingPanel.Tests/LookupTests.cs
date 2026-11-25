using System.Net;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using ReadingPanel.Core;
using ReadingPanel.Integration;
using Xunit;

namespace ReadingPanel.Tests;

public class LookupTests
{
    private static Selection Pick(string text) => new(text, new(100, 100, 120));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token) => send(message, token);
    }
    private sealed class EmptyDictionary : IWordDictionary { public Entry? Find(string text) => null; }
    private sealed class DelayedDictionary : IWordLookup
    {
        public TaskCompletionSource<Entry?> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReceivedToken { get; private set; }
        public ValueTask<Entry?> FindAsync(string text, CancellationToken token)
        {
            if (text == "new") return ValueTask.FromResult<Entry?>(new("new", "new", "", "新"));
            ReceivedToken = token;
            return new(Reply.Task);
        }
    }
    private sealed class DelayedProvider(string name, bool requiresApiKey = true) : ITranslationProvider
    {
        public string Id => name;
        public string Name => name;
        public bool RequiresApiKey => requiresApiKey;
        public TaskCompletionSource<string> Answer { get; } = new();
        public CancellationToken ReceivedToken { get; private set; }
        public Task<string> TranslateAsync(Translation input, ProviderOptions options, CancellationToken token)
        {
            ReceivedToken = token;
            // Simulate a service that completes after cancellation.
            return Answer.Task;
        }
    }

    [Fact]
    public async Task ARegisteredKeylessServiceCanTranslateWithoutSetup()
    {
        var provider = new DelayedProvider("local", requiresApiKey: false);
        var lookup = new LookupController(new EmptyDictionary(), [provider], new(provider.Id, new()), _ => { });
        lookup.Open(Pick("unknown"));
        var translation = lookup.TranslateCurrentAsync();
        Assert.Equal(ResultKind.Translating, lookup.Current?.Kind);
        provider.Answer.SetResult("本地译文");
        await translation;
        Assert.Equal("本地译文", lookup.Current?.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASlowDictionaryMissCannotReopenOrTranslateAnOldSelection(bool replace)
    {
        var dictionary = new DelayedDictionary();
        var provider = new DelayedProvider("local", requiresApiKey: false);
        var lookup = new LookupController(dictionary, [provider], new(provider.Id, new()), _ => { });
        var old = lookup.OpenAsync(Pick("previous phrase"));
        Assert.Equal(ResultKind.LookingUp, lookup.Current?.Kind);
        if (replace) await lookup.OpenAsync(Pick("new")); else lookup.Close();
        Assert.True(dictionary.ReceivedToken.IsCancellationRequested);
        dictionary.Reply.SetResult(null);
        await old.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(replace ? "新" : null, lookup.Current?.Text);
    }

#if !ONLINE_EDITION
    [Theory]
    [InlineData("Yield,", "yield")]
    [InlineData("give up", "give up")]
    [InlineData("in the blink\r\nof an eye.", "in the blink of an eye")]
    [InlineData("e.g.", "e.g.")]
    [InlineData("actioning", "actioning")]
    [InlineData("egotripped", "egotrip")]
    public void OfflineIndexResolvesWordsPhrasesAndInflections(string text, string headword)
    {
        using var db = new WordDictionary(Path.Combine(AppContext.BaseDirectory, "assets", "ecdict.sqlite3"));
        var entry = Assert.IsType<Entry>(db.Find(text));
        Assert.Equal(headword, entry.Headword, ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(entry.Meaning));
    }

    [Theory]
    [InlineData("Circumstances,", "原形 circumstance", "状况", "circumstances", "[法]")]
    [InlineData("studied", null, "慎重的", "原形 study", "学习")]
    [InlineData("building", null, "建筑物", "原形 build", "建立")]
    [InlineData("glasses", null, "眼镜", "原形 glass", "玻璃杯")]
    public void OfflineCardKeepsTheSelectedSenseAndItsLemma(string text, string? firstHeading, string firstSense,
        string secondHeading, string secondSense)
    {
        using var db = new WordDictionary(Path.Combine(AppContext.BaseDirectory, "assets", "ecdict.sqlite3"));
        var entry = Assert.IsType<Entry>(db.Find(text));
        Assert.Equal(text.TrimEnd(','), entry.Word);
        Assert.Collection(entry.Definitions,
            first => { Assert.Equal(firstHeading, first.Heading); Assert.Contains(firstSense, first.Text); },
            second => { Assert.Equal(secondHeading, second.Heading); Assert.Contains(secondSense, second.Text); });
    }

    [Fact]
    public void OfflineInflectionsIncludeIrregularAndAmbiguousForms()
    {
        using var db = new WordDictionary(Path.Combine(AppContext.BaseDirectory, "assets", "ecdict.sqlite3"));
        var went = Assert.IsType<Entry>(db.Find("went"));
        Assert.Equal("went", went.Phonetic);
        Assert.Equal("go的过去式", Assert.Single(went.Definitions).Heading);
        Assert.Contains("去", went.Meaning);
        Assert.Contains("学习", db.Find("learnt")!.Meaning);
        Assert.Contains("是", db.Find("are")!.Definitions[0].Text);
        Assert.Contains("有", db.Find("has")!.Definitions[0].Text);
        Assert.Equal("原形 do", db.Find("does")!.Definitions[1].Heading);

        var leaves = Assert.IsType<Entry>(db.Find("leaves"));
        Assert.Equal(new[] { "原形 leave", "原形 leaf" }, leaves.Definitions.Skip(1).Select(s => s.Heading));
        Assert.Null(Assert.Single(db.Find("news")!.Definitions).Heading);
        Assert.Null(Assert.Single(db.Find("spring")!.Definitions).Heading);
        Assert.Null(Assert.Single(db.Find("gas")!.Definitions).Heading);
        Assert.Empty(db.Find("egotripped")!.Phonetic);
    }

    [Fact]
    public void LegacyIndexCanSupplementAnExactSpecialistEntry()
    {
        string file = Path.GetTempFileName();
        try
        {
            SQLitePCL.Batteries_V2.Init();
            using (var fixture = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
            {
                fixture.Open();
                using var sql = fixture.CreateCommand();
                sql.CommandText = """
                    CREATE TABLE words (word TEXT PRIMARY KEY COLLATE NOCASE, phonetic TEXT, meaning TEXT);
                    CREATE TABLE forms (form TEXT PRIMARY KEY COLLATE NOCASE, lemma TEXT);
                    INSERT INTO words VALUES ('circumstance', 'ipa', 'n. 状况'), ('circumstances', '', '[法] 事项');
                    INSERT INTO forms VALUES ('circumstances', 'circumstance');
                    """;
                sql.ExecuteNonQuery();
            }
            using var db = new WordDictionary(file);
            var entry = Assert.IsType<Entry>(db.Find("circumstances"));
            Assert.Equal("n. 状况", entry.Definitions[0].Text);
            Assert.Empty(entry.Phonetic);
            Assert.Empty(entry.ExamLabels);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task EnhancedOfflineLookupReturnsExamLabelsAndPhrasesWithoutTranslation()
    {
        using var db = new WordDictionary(Path.Combine(AppContext.BaseDirectory, "assets", "ecdict.sqlite3"));
        var provider = new DelayedProvider("unused");
        var lookup = new LookupController(db, [provider], new(provider.Id, new()), _ => { });
        await lookup.OpenAsync(Pick("abstract"));
        var tagged = Assert.IsType<Entry>(lookup.Current?.Entry);
        Assert.Equal(new[] { "高考", "CET-4", "CET-6", "考研", "TOEFL", "IELTS", "GRE" }, tagged.ExamLabels);

        await lookup.OpenAsync(Pick("double down on"));
        var phrase = Assert.IsType<Entry>(lookup.Current?.Entry);
        Assert.Contains("加倍下注", phrase.Meaning);
        Assert.Empty(phrase.ExamLabels);

        var englishOnly = Assert.IsType<Entry>(db.Find("'sarvo"));
        Assert.Contains("this afternoon", englishOnly.Meaning);
    }
#endif

    [Fact]
    public async Task SwitchingProviderRerunsSameSelectionAndDiscardsLateResults()
    {
        var first = new DelayedProvider("first");
        var second = new DelayedProvider("second");
        Settings? saved = null;
        var settings = new Settings(first.Id, new() { [first.Id] = new("test"), [second.Id] = new("test") });
        var lookup = new LookupController(new EmptyDictionary(), [first, second], settings, s => saved = s);
        var source = Pick("example");
        lookup.Open(source);
        Task stale = lookup.TranslateCurrentAsync();
        lookup.SelectProvider(second.Id);
        Assert.True(first.ReceivedToken.IsCancellationRequested);
        Assert.Equal(second.Id, saved?.Provider);
        Assert.Same(source, lookup.Current?.Source);
        first.Answer.SetResult("late");
        await stale;
        Assert.Equal(ResultKind.Translating, lookup.Current?.Kind);
        var finished = new TaskCompletionSource();
        lookup.Changed += () => { if (lookup.Current?.Kind == ResultKind.Translation) finished.TrySetResult(); };
        second.Answer.SetResult("current");
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("current", lookup.Current?.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DismissingOrReplacingCancelsTheOldSelection(bool replace)
    {
        var p = new DelayedProvider("example");
        var lookup = new LookupController(new EmptyDictionary(), [p], new(p.Id, new() { [p.Id] = new("test") }), _ => { });
        lookup.Open(Pick("unknown"));
        var pending = lookup.TranslateCurrentAsync();
        if (replace) lookup.Open(Pick("newword")); else lookup.Close();
        p.Answer.SetResult("late");
        await pending;
        Assert.True(p.ReceivedToken.IsCancellationRequested);
        Assert.Equal(replace ? "newword" : null, lookup.Current?.Source.Text);
    }

    [Fact]
    public async Task DeepSeekTranslatesTheSelectionUsingTheConfiguredModel()
    {
        const string input = "The river bends.";
        using var http = new HttpClient(new Handler(async (message, token) => {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://api.deepseek.com/chat/completions", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer sample", message.Headers.Authorization!.ToString());
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            var root = body.RootElement;
            Assert.Equal("custom-model", root.GetProperty("model").GetString());
            Assert.Equal(input, root.GetProperty("messages")[1].GetProperty("content").GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"河流转弯。"}}]}""") };
        }));
        var provider = new DeepSeekProvider(http);
        Assert.Equal("河流转弯。", await provider.TranslateAsync(new(input), new("sample", "custom-model"), default));
    }

    [Theory]
    [InlineData("sample-key", "speech-project")]
    [InlineData("", "")]
    [InlineData("", "shared-project")]
    public async Task GoogleCloudTranslatesSentencesWithAnApiKeyOrApplicationDefaultCredentials(string key, string project)
    {
        const string input = "The river bends near A & B.";
        int credentialRequests = 0;
        using var http = new HttpClient(new Handler(async (message, token) => {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://translation.googleapis.com/language/translate/v2", message.RequestUri!.AbsoluteUri);
            if (key.Length > 0)
            {
                Assert.Equal(key, Assert.Single(message.Headers.GetValues("x-goog-api-key")));
                Assert.Null(message.Headers.Authorization);
                Assert.False(message.Headers.Contains("x-goog-user-project"));
            }
            else
            {
                Assert.False(message.Headers.Contains("x-goog-api-key"));
                Assert.Equal("Bearer adc-token", message.Headers.Authorization?.ToString());
                Assert.Equal(project.Length > 0 ? project : "adc-project", Assert.Single(message.Headers.GetValues("x-goog-user-project")));
            }
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal(input, body.RootElement.GetProperty("q").GetString());
            Assert.Equal("en", body.RootElement.GetProperty("source").GetString());
            Assert.Equal("zh-CN", body.RootElement.GetProperty("target").GetString());
            Assert.Equal("text", body.RootElement.GetProperty("format").GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"data":{"translations":[{"translatedText":"河流在 A & B 附近转弯。"}]}}""") };
        }));
        var provider = new GoogleCloudProvider(http, (quotaProject, _) => {
            credentialRequests++;
            Assert.Equal(project, quotaProject);
            return Task.FromResult(GoogleCredential.FromAccessToken("adc-token").CreateWithQuotaProject(
                quotaProject.Length > 0 ? quotaProject : "adc-project"));
        });
        var settings = new Settings(provider.Id, new() { [provider.Id] = new(key) }) { Speech = new(GoogleProject: project) };
        var lookup = new LookupController(new EmptyDictionary(), [provider], settings, _ => { });
        await lookup.OpenAsync(Pick(input));
        Assert.Equal(ResultKind.Translation, lookup.Current?.Kind);
        Assert.Equal("河流在 A & B 附近转弯。", lookup.Current?.Text);
        Assert.Equal(key.Length == 0 ? 1 : 0, credentialRequests);
    }

    [Fact]
    public async Task GoogleCloudOffersSetupWhenCredentialsAreMissing()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP request.")));
        var provider = new GoogleCloudProvider(http, (_, _) => throw new InvalidOperationException("ADC unavailable."));
        var lookup = new LookupController(new EmptyDictionary(), [provider], new(provider.Id, new()), _ => { });
        await lookup.OpenAsync(Pick("Translate this sentence."));
        Assert.Equal(ResultKind.Setup, lookup.Current?.Kind);
        Assert.Contains("ADC", lookup.Current?.Text);
    }

    [Fact]
    public async Task NativeFramesHandleTruncationAndRetainUnicode()
    {
        using var stream = new MemoryStream();
        var message = new ReaderMessage("lookup", Guid.NewGuid().ToString(), "river 水", [-4, -7, 8, 12]);
        await NativeMessaging.WriteAsync(stream, message, default);
        stream.Position = 0;
        using var json = JsonDocument.Parse(await NativeMessaging.ReadAsync(stream, default));
        Assert.Equal(message.Text, json.RootElement.GetProperty("text").GetString());
        stream.SetLength(stream.Length - 1);
        stream.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeMessaging.ReadAsync(stream, default));
    }

    [Theory]
    [InlineData("sample:fx", "api-free.deepl.com")]
    [InlineData("sample", "api.deepl.com")]
    public async Task SentencesUseTheConfiguredDeepLAccount(string key, string host)
    {
        const string sentence = "The river bends.";
        int calls = 0;
        using var http = new HttpClient(new Handler(async (request, token) => {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"https://{host}/v2/translate", request.RequestUri?.AbsoluteUri);
            Assert.Equal($"DeepL-Auth-Key {key}", request.Headers.Authorization?.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(sentence, Assert.Single(body.RootElement.GetProperty("text").EnumerateArray()).GetString());
            Assert.Equal("EN", body.RootElement.GetProperty("source_lang").GetString());
            Assert.Equal("ZH-HANS", body.RootElement.GetProperty("target_lang").GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"translations":[{"text":"河流转弯。"}]}""") };
        }));
        var settings = Settings.Default with { Services = new() { ["deepl"] = new(key) } };
        Assert.Equal("deepl", settings.Provider);
        var lookup = new LookupController(new EmptyDictionary(), [new DeepLProvider(http)], settings, _ => { });
        await lookup.OpenAsync(Pick("word"));
        Assert.Equal(0, calls);
        await lookup.OpenAsync(Pick(sentence));
        Assert.Equal("河流转弯。", lookup.Current?.Text);
        lookup.Configure(settings with { TranslationEnabled = false });
        await lookup.OpenAsync(Pick("A second sentence."));
        await lookup.TranslateCurrentAsync();
        Assert.Equal(ResultKind.Setup, lookup.Current?.Kind);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DisablingTranslationCancelsAnInFlightSentence()
    {
        var provider = new DelayedProvider("deepl", requiresApiKey: false);
        var lookup = new LookupController(new EmptyDictionary(), [provider], Settings.Default, _ => { });
        var pending = lookup.OpenAsync(Pick("A sentence."));
        lookup.Configure(lookup.Settings with { TranslationEnabled = false });
        Assert.True(provider.ReceivedToken.IsCancellationRequested);
        provider.Answer.SetResult("late");
        await pending;
        Assert.Equal(ResultKind.Setup, lookup.Current?.Kind);
    }
}
