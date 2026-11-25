namespace ReadingPanel.Core;

// Reader adapters supply physical screen pixels; only the presentation layer uses DIPs.
public readonly record struct Anchor(int X, int Top, int Bottom);
public sealed record Selection(string Text, Anchor Anchor, bool Keyboard = false);
public enum ResultKind { Word, Missing, Translating, Translation, Setup, Error, LookingUp, LookupError }
public sealed record LookupResult(Selection Source, ResultKind Kind, string Text, Entry? Entry = null)
{
    public bool IsTranslation => Kind is ResultKind.Translating or ResultKind.Translation or ResultKind.Setup or ResultKind.Error;
}

// Owned by the UI dispatcher. Providers are asynchronous; only the current request may publish a result.
public sealed class LookupController
{
    private readonly IWordLookup dictionary;
    private readonly Dictionary<string, ITranslationProvider> services;
    private readonly Action<Settings> persist;
    // Replacing a provider's options drops its old cache, including references to the old credential.
    private readonly Dictionary<string, (ProviderOptions Options, AsyncLookupCache<Translation, string> Results)> translations = new();
    private CancellationTokenSource? pending;
    public Settings Settings { get; private set; }
    public IReadOnlyCollection<ITranslationProvider> Providers => services.Values;
    public ITranslationProvider Provider => services[Settings.Provider];
    public LookupResult? Current { get; private set; }
    public event Action? Changed;

    public LookupController(IWordLookup dictionary, IEnumerable<ITranslationProvider> providers, Settings settings, Action<Settings> persist)
    {
        this.dictionary = dictionary;
        services = providers.ToDictionary(p => p.Id);
        Settings = services.ContainsKey(settings.Provider) ? settings : settings with { Provider = services.Keys.First() };
        this.persist = persist;
    }

    public void Open(Selection selection) => _ = OpenAsync(selection);

    public Task OpenAsync(Selection selection) => OpenAsync(selection, false);
    internal Task RetryAsync(Selection selection) => OpenAsync(selection, true);

    private async Task OpenAsync(Selection selection, bool refresh)
    {
        if (!refresh && Current is { Kind: ResultKind.Translating } translating && translating.Source.Text == selection.Text)
        {
            await TranslateAsync(selection);
            return;
        }
        var previous = pending;
        using var request = new CancellationTokenSource();
        pending = request;
        try
        {
            var lookup = refresh ? dictionary.RetryAsync(selection.Text, request.Token) : dictionary.FindAsync(selection.Text, request.Token);
            previous?.Cancel();
            if (!lookup.IsCompleted)
            {
                var looking = new LookupResult(selection, ResultKind.LookingUp, "正在查词…");
                if (dictionary.ShowPending || Current != null) Publish(looking);
                else Current = looking; // Accept dismissal while the first local result is still on disk.
            }
            var entry = await lookup;
            if (pending != request) return;
            if (entry != null) Publish(new(selection, ResultKind.Word, entry.Meaning, entry));
            else if (selection.Text.All(c => !char.IsWhiteSpace(c))) Publish(new(selection, ResultKind.Missing, dictionary.MissingText));
            else { pending = null; await TranslateAsync(selection, refresh); }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (pending == request) Publish(new(selection, ResultKind.LookupError, e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }
                ? "词典请求过于频繁，请稍后再试。" : "未能读取词条，请重试。"));
        }
        finally { if (pending == request) pending = null; }
    }

    public Task TranslateCurrentAsync() => Current is { } result ? TranslateAsync(result.Source, refresh: true) : Task.CompletedTask;

    public void Configure(Settings settings)
    {
        if (!services.ContainsKey(settings.Provider)) throw new ArgumentException("Unknown provider.");
        bool translationChanged = settings.TranslationEnabled != Settings.TranslationEnabled ||
            settings.Provider != Settings.Provider || settings.For(settings.Provider) != Settings.For(Settings.Provider);
        bool lemmaPreferenceChanged = settings.ExpandLemmasByDefault != Settings.ExpandLemmasByDefault;
        persist(settings);
        Settings = settings;
        if (translationChanged && Current is { IsTranslation: true } result) _ = TranslateAsync(result.Source);
        else if (lemmaPreferenceChanged && Current?.Kind == ResultKind.Word) Changed?.Invoke();
    }

    public void SelectProvider(string id)
    {
        if (id != Settings.Provider) Configure(Settings with { Provider = id });
    }

    public void Close() { Cancel(); Publish(null); }
    private void Publish(LookupResult? result) { Current = result; Changed?.Invoke(); }
    private void Cancel()
    {
        var previous = pending;
        pending = null;
        previous?.Cancel();
    }

    private async Task TranslateAsync(Selection source, bool refresh = false)
    {
        var previous = pending;
        if (!Settings.TranslationEnabled)
        {
            Cancel();
            Publish(new(source, ResultKind.Setup, "句子翻译已关闭。"));
            return;
        }
        var provider = Provider;
        var options = Settings.For(provider.Id);
        if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(options.ApiKey))
        {
            Cancel();
            Publish(new(source, ResultKind.Setup, $"Add a {provider.Name} API key to translate this selection."));
            return;
        }
        using var request = new CancellationTokenSource();
        pending = request;
        try
        {
            var input = new Translation(source.Text);
            if (!translations.TryGetValue(provider.Id, out var cache) || cache.Options != options)
                translations[provider.Id] = cache = (options, new(32, text => text.Length <= 32_768));
            var task = cache.Results.GetAsync(input, ct => provider.TranslateAsync(input, options, ct), request.Token, refresh);
            previous?.Cancel();
            if (!task.IsCompletedSuccessfully) Publish(new(source, ResultKind.Translating, "翻译中…"));
            string translated = await task;
            if (pending == request) Publish(new(source, ResultKind.Translation, translated));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (TranslationSetupException ex)
        {
            if (pending == request) Publish(new(source, ResultKind.Setup, ex.Message));
        }
        catch (Exception ex)
        {
            if (pending == request) Publish(new(source, ResultKind.Error, ex is HttpRequestException { StatusCode: not null }
                ? ex.Message : $"Could not complete the translation with {provider.Name}."));
        }
        finally { if (pending == request) pending = null; }
    }
}
