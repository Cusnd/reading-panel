using ReadingPanel.Core;
using ReadingPanel.Integration;
using Xunit;

namespace ReadingPanel.Tests;

public class PerformanceTests
{
    [Fact]
    public async Task LastWaiterCancelsTheProducerAndAbandonedResultsCannotReplaceANewerValue()
    {
        var cache = new AsyncLookupCache<string, int>(2);
        var old = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken producer = default;
        int calls = 0;
        Task<int> Fetch(CancellationToken token) { calls++; producer = token; return old.Task; }
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var a = cache.GetAsync("a", Fetch, first.Token);
        var b = cache.GetAsync("a", Fetch, second.Token);
        first.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.False(producer.IsCancellationRequested);
        second.Cancel();
        Assert.True(producer.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        Assert.Equal(1, calls);
        Task<int> Get(string key, int value, bool refresh = false) =>
            cache.GetAsync(key, _ => Task.FromResult(value), default, refresh);
        Assert.Equal(2, await Get("a", 2));
        old.SetResult(1);
        Assert.Equal(2, await Get("a", 3));
        Assert.Equal(3, await Get("a", 3, true));
        await Get("b", 4);
        await Get("c", 5);
        Assert.Equal(6, await Get("a", 6));
    }

    private sealed class EmptyDictionary : IWordDictionary { public Entry? Find(string text) => null; }
    private sealed class Translator : ITranslationProvider
    {
        public string Id => "translator";
        public string Name => Id;
        public readonly List<TaskCompletionSource<string>> Replies = new();
        public Task<string> TranslateAsync(Translation input, ProviderOptions options, CancellationToken token)
        {
            var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Replies.Add(reply);
            return reply.Task;
        }
    }

    [Fact]
    public async Task IdenticalTranslationsShareWorkButKeepNewAnchorAndRespectChangedOptionsAndRetry()
    {
        var provider = new Translator();
        var settings = new Settings(provider.Id, new() { [provider.Id] = new("key", "model-one") });
        var lookup = new LookupController(new EmptyDictionary(), [provider], settings, _ => { });
        var first = new Selection("A full sentence.", new(10, 10, 20));
        var next = first with { Anchor = new(30, 30, 40) };
        var a = lookup.OpenAsync(first);
        var b = lookup.OpenAsync(next);
        Assert.Single(provider.Replies).SetResult("one");
        await Task.WhenAll(a, b);
        Assert.Same(next, lookup.Current!.Source);
        await lookup.OpenAsync(first);
        Assert.Single(provider.Replies);
        var retry = lookup.TranslateCurrentAsync();
        provider.Replies[1].SetResult("two");
        await retry;
        lookup.Close();
        lookup.Configure(settings with { Services = new() { [provider.Id] = new("key", "model-two") } });
        var refreshed = lookup.OpenAsync(next);
        provider.Replies[2].SetResult("three");
        await refreshed;
        Assert.Equal("three", lookup.Current?.Text);
    }

#if !ONLINE_EDITION
    [Fact]
    public async Task OfflineLookupReturnsWhileDatabaseIsLockedAndSkipsCancelledQueuedWork()
    {
        SQLitePCL.Batteries_V2.Init();
        string path = Path.GetTempFileName();
        try
        {
            using var fixture = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
            fixture.Open();
            using var sql = fixture.CreateCommand();
            sql.CommandText = """
                CREATE TABLE words(word TEXT PRIMARY KEY, phonetic TEXT, meaning TEXT);
                CREATE TABLE forms(form TEXT PRIMARY KEY, lemma TEXT);
                INSERT INTO words VALUES('first', '', 'one'), ('second', '', 'two');
                BEGIN EXCLUSIVE;
                """;
            sql.ExecuteNonQuery();
            using var db = new WordDictionary(path);
            IWordLookup asyncDictionary = db;
            var first = asyncDictionary.FindAsync("first", default).AsTask();
            using var stale = new CancellationTokenSource();
            var cancelled = asyncDictionary.FindAsync("second", stale.Token).AsTask();
            stale.Cancel();
            Assert.False(first.IsCompleted);
            sql.CommandText = "COMMIT";
            sql.ExecuteNonQuery();
            Assert.Equal("one", (await first.WaitAsync(TimeSpan.FromSeconds(5)))?.Meaning);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            var terms = Enumerable.Range(0, 24).Select(n => n % 2 == 0 ? "first" : "second").ToArray();
            var results = await Task.WhenAll(terms.Select(word => asyncDictionary.FindAsync(word, default).AsTask()));
            Assert.Equal(terms, results.Select(entry => entry!.Word));
        }
        finally { File.Delete(path); }
    }
#endif

    [Fact]
    public void ReplayPermissionDoesNotBlockInputOrTrustLateResultsFromAnotherTarget()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var returned = new ManualResetEventSlim();
        int checks = 0;
        var permission = new ReplayPermission(pid => {
            Interlocked.Increment(ref checks);
            if (pid == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); returned.Set(); return true; }
            return pid == 3;
        });
        Assert.False(permission.Verified(1));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(permission.Verified(1));
        Assert.False(permission.Verified(2));
        release.Set();
        Assert.True(returned.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(permission.Verified(2));
        Assert.False(permission.Verified(3));
        Assert.True(SpinWait.SpinUntil(() => permission.Verified(3), TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref checks) == 3, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void SlowReaderOnlyDelaysItsOwnProcessAndOrdinaryClicksHaveAShorterBudget()
    {
        var probes = new MouseProbes();
        Assert.True(probes.Begin(10));
        Assert.True(probes.Busy(10, 5000));
        Assert.False(probes.Busy(11, 5000));
        probes.Timeout(10, 5000);
        probes.End(10);
        Assert.True(probes.Busy(10, 5500));
        Assert.False(probes.Busy(10, 7000));
        Assert.True(DesktopMouse.LookupBudget(false, 0) < DesktopMouse.LookupBudget(true, 100));
        Assert.Equal(DesktopMouse.OrdinaryBudgetMs, DesktopMouse.LookupBudget(true, 60_000));
    }

    [Fact]
    public async Task ClipboardNotificationRetriesABusyOwnerAndChecksSequenceAtTheDeadline()
    {
        using var changed = new AutoResetEvent(false);
        using var armed = new ManualResetEventSlim();
        int published = 0, opens = 0;
        var wait = Task.Run(() => ClipboardSelection.OpenChangedClipboard(() => {
            armed.Set();
            return Volatile.Read(ref published) == 1;
        }, () => ++opens == 2, changed, true, 2000));
        Assert.True(armed.Wait(TimeSpan.FromSeconds(2)));
        Volatile.Write(ref published, 1);
        changed.Set();
        Assert.True(await wait);
        Assert.Equal(2, opens);
        int observations = 0;
        Assert.True(ClipboardSelection.OpenChangedClipboard(() => ++observations == 2, () => true, changed, true, 20));
        Assert.False(ClipboardSnapshot.CanSnapshotFormat(0x80));
        Assert.False(ClipboardSnapshot.CanSnapshotFormat(0x301));
        Assert.True(ClipboardSnapshot.CanSnapshotFormat(13));
    }

    [Fact]
    public void PiperChunksRespectPausesAndKeepUnicodeScalarsIntact()
    {
        string text = "Read this, " + new string('a', 57) + "🌎🌎，再读一句。 End.";
        var chunks = PiperSpeech.Chunks(text, 12).ToArray();
        Assert.Equal("Read this,", chunks[0]);
        Assert.All(chunks, part => {
            Assert.InRange(part.Length, 1, 12);
            Assert.False(char.IsLowSurrogate(part[0]) || char.IsHighSurrogate(part[^1]));
        });
        Assert.Equal(text.Replace(" ", ""), string.Concat(chunks).Replace(" ", ""));
    }

    private sealed class SlowDevice(Action write) : IPcmPlayback
    {
        public void Write(byte[] pcm) => write();
        public Task DrainAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

    [Fact]
    public void PlaybackBackpressureCannotExpireTheProducerTimerAndTimeoutResumesAfterWrite()
    {
        using var idle = new CancellationTokenSource();
        var budget = TimeSpan.FromMilliseconds(30);
        using var output = new SlowDevice(() => Assert.False(idle.Token.WaitHandle.WaitOne(budget * 3)));
        idle.CancelAfter(budget);
        Pronunciation.WriteAudio(output, [0, 0], idle, budget);
        Assert.True(idle.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SharedMonthlyLedgerMergesWritersAndServesSettingsFromMemory()
    {
        var dir = Directory.CreateTempSubdirectory("readingpanel-ledger-");
        try
        {
            var at = DateTimeOffset.Parse("2026-10-10T01:00:00Z");
            var first = new SpeechUsage(dir.FullName, () => at);
            var other = new SpeechUsage(dir.FullName, () => at);
            int notifications = 0;
            first.Changed += () => Interlocked.Increment(ref notifications);
            await Task.WhenAll(Task.Run(() => { for (int i = 0; i < 10; i++) first.GoogleSent(at, "a", 1); }),
                Task.Run(() => { for (int i = 0; i < 10; i++) other.GoogleSent(at, "b", 1); }));
            Assert.Equal(20, (await first.ReadAsync("")).Local);
            Assert.Equal(10, notifications);
            using var exclusive = File.Open(Assert.Single(dir.GetFiles()).FullName, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Equal(20, first.CachedTotals("")!.Local);
        }
        finally { dir.Delete(true); }
    }
}
