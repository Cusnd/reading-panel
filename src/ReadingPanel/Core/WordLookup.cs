namespace ReadingPanel.Core;

public sealed record DictionaryAttribution(string SourceName, Uri Article, string LicenseName, Uri License);

public sealed record DefinitionSection(string Text, string? Heading = null)
{
    public string? Lemma { get; init; }
}

public sealed record Entry(string Word, string Headword, string Phonetic, string Meaning)
{
    public IReadOnlyList<DefinitionSection> Definitions { get; private init; } = [new(Meaning)];
    public IReadOnlyList<string> ExamLabels { get; init; } = [];
    public DictionaryAttribution? Attribution { get; init; }

    internal Entry WithDefinitions(IReadOnlyList<DefinitionSection> sections) => this with {
        Definitions = sections,
        Meaning = string.Join("\n", sections.Select(s => s.Heading == null ? s.Text : $"{s.Heading}\n{s.Text}"))
    };
}

public interface IWordLookup
{
    bool ShowPending => true;
    ValueTask<Entry?> RetryAsync(string text, CancellationToken token) => FindAsync(text, token);
    string MissingText => "离线词典未收录这个词。";
    ValueTask<Entry?> FindAsync(string text, CancellationToken token);
}

public interface IWordDictionary : IWordLookup
{
    Entry? Find(string text);
    ValueTask<Entry?> IWordLookup.FindAsync(string text, CancellationToken token) => ValueTask.FromResult(Find(text));
}
