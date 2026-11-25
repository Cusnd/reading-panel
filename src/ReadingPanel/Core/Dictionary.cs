using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace ReadingPanel.Core;

public sealed class WordDictionary : IWordDictionary, IDisposable
{
    private sealed record Query(string Text, CancellationToken Token, TaskCompletionSource<Entry?> Result);
    private readonly System.Collections.Concurrent.BlockingCollection<Query> queue = new();
    private readonly Thread worker;
    private int disposed;
    private readonly AsyncLookupCache<string, Entry?> cache = new(128);
    public bool ShowPending => false;

    public WordDictionary(string file)
    {
        worker = new Thread(() => Run(file)) { IsBackground = true, Name = "Reading Panel dictionary" };
        worker.Start();
    }

    public ValueTask<Entry?> FindAsync(string text, CancellationToken token) => FindAsync(text, token, false);
    public ValueTask<Entry?> RetryAsync(string text, CancellationToken token) => FindAsync(text, token, true);
    private ValueTask<Entry?> FindAsync(string text, CancellationToken token, bool refresh)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return new(cache.GetAsync(text, ct => Enqueue(text, ct), token, refresh));
    }

    private Task<Entry?> Enqueue(string text, CancellationToken token)
    {
        var result = new TaskCompletionSource<Entry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(new(text, token, result));
        return result.Task;
    }

    public Entry? Find(string text) => FindAsync(text, default).AsTask().GetAwaiter().GetResult();

    private void Run(string file)
    {
        SqliteWordIndex? index = null;
        try
        {
            foreach (var query in queue.GetConsumingEnumerable())
            {
                if (query.Token.IsCancellationRequested) { query.Result.TrySetCanceled(query.Token); continue; }
                try
                {
                    index ??= new SqliteWordIndex(file);
                    query.Token.ThrowIfCancellationRequested();
                    query.Result.SetResult(index.Find(query.Text));
                }
                catch (OperationCanceledException) { query.Result.TrySetCanceled(query.Token); }
                catch (Exception error) { query.Result.TrySetException(error); }
            }
        }
        finally { index?.Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        queue.CompleteAdding();
        worker.Join();
        queue.Dispose();
    }
}

internal sealed class SqliteWordIndex : IDisposable
{
    private static readonly Regex FormNote = new(
        @"^(?:[a-z]+\.\s*)?([a-z]+(?:[-'][a-z]+)*)\s*的\s*" +
        @"(?:过去式|过去时|过去分词|现在式|现在时|现在分词|第[一二三]人称|单数|复数|比较级|最高级|ing|形式|和|与|或|及|、)+[。.]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, string> ExamNames = new() {
        ["zk"] = "中考", ["gk"] = "高考", ["cet4"] = "CET-4", ["cet6"] = "CET-6",
        ["ky"] = "考研", ["toefl"] = "TOEFL", ["ielts"] = "IELTS", ["gre"] = "GRE"
    };
    private readonly SqliteConnection connection;
    private readonly SqliteCommand exact;
    private readonly SqliteCommand inflection;

    public SqliteWordIndex(string file)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("The offline dictionary is missing. Keep the assets folder beside ReadingPanel.exe.", file);
        SQLitePCL.Batteries_V2.Init();
        connection = new(new SqliteConnectionStringBuilder {
            DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            // Older packages lack exam tags or store just one lemma per form.
            using var schema = connection.CreateCommand();
            schema.CommandText = "SELECT count(*) FROM pragma_table_info('words') WHERE name = 'tag'";
            string tags = (long)schema.ExecuteScalar()! == 0 ? "''" : "words.tag";
            schema.CommandText = "SELECT count(*) FROM pragma_table_info('forms') WHERE name = 'priority'";
            string order = (long)schema.ExecuteScalar()! == 0 ? "word" : "forms.priority, word";
            exact = Query($"SELECT word, phonetic, meaning, {tags} FROM words WHERE word = $term");
            inflection = Query($"""
                SELECT word, '', meaning, {tags} FROM forms JOIN words ON words.word = forms.lemma
                WHERE forms.form = $term AND words.word <> $term ORDER BY {order}
                """);
        }
        catch { connection.Dispose(); throw; }
    }

    private SqliteCommand Query(string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add("$term", SqliteType.Text);
        try { command.Prepare(); return command; }
        catch { command.Dispose(); throw; }
    }

    public Entry? Find(string text)
    {
        string term = Regex.Replace(text.Trim(), @"\s+", " ").Replace('’', '\'');
        if (term.Length is 0 or > 180) return null;
        var match = Read(exact, term).SingleOrDefault();
        if (match == null)
        {
            string bare = term.Trim('.', ',', ':', ';', '!', '?', '"', '“', '”', '(', ')').Trim();
            if (bare.Length == 0) return null;
            if (bare != term)
            {
                term = bare;
                match = Read(exact, term).SingleOrDefault();
            }
        }
        var lemmas = Read(inflection, term);
        if (match != null) lemmas.RemoveAll(lemma => lemma.Meaning == match.Meaning);
        if (lemmas.Count == 0) return match;
        if (match == null && lemmas.Count == 1) return lemmas[0];

        var sections = lemmas.Select(lemma => new DefinitionSection(lemma.Meaning, $"原形 {lemma.Headword}") {
            Lemma = lemma.Headword
        }).ToList();
        if (match != null)
        {
            string meaning = match.Meaning;
            var note = FormNote.Match(meaning);
            if (note.Success && note.Groups[1].Value.Equals(lemmas[0].Headword, StringComparison.OrdinalIgnoreCase))
                sections[0] = sections[0] with { Heading = meaning };
            else
            {
                // Bring the general definition above a specialist-only entry such as "circumstances".
                bool lemmaFirst = meaning.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .All(line => line.StartsWith('[') || FormNote.IsMatch(line));
                if (lemmaFirst) sections.Insert(1, new(meaning, match.Headword));
                else sections.Insert(0, new(meaning));
            }
        }
        var entry = match ?? (lemmas[0] with { ExamLabels = [] });
        return entry.WithDefinitions(sections);
    }

    private static List<Entry> Read(SqliteCommand command, string term)
    {
        command.Parameters[0].Value = term;
        using var reader = command.ExecuteReader();
        var entries = new List<Entry>();
        while (reader.Read())
        {
            var codes = reader.GetString(3).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            entries.Add(new Entry(term, reader.GetString(0), reader.GetString(1), reader.GetString(2)) {
                ExamLabels = codes.Where(ExamNames.ContainsKey).Select(code => ExamNames[code]).ToArray()
            });
        }
        return entries;
    }

    public void Dispose() { exact.Dispose(); inflection.Dispose(); connection.Dispose(); }
}
