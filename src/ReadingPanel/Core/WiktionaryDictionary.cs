using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ReadingPanel.Core;

internal sealed class WiktionaryDictionary(HttpClient http) : IWordLookup
{
    public string MissingText => "未找到含中文释义的英语词条。";
    private long retryAfterUtc;
    private static readonly Regex Term = new(@"^[A-Za-z]+(?:['-][A-Za-z]+)*(?: [A-Za-z]+(?:['-][A-Za-z]+)*){0,2}$", RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, string> Parts = new() {
        ["名词"] = "n.", ["动词"] = "v.", ["形容词"] = "adj.", ["副词"] = "adv.", ["短语"] = "", ["成语"] = "",
        ["专有名词"] = "n.", ["专名"] = "n.", ["介词"] = "prep.", ["介系词"] = "prep.", ["连词"] = "conj.",
        ["连接词"] = "conj.", ["限定词"] = "det.", ["代词"] = "pron.", ["数词"] = "num.", ["感叹词"] = "int.",
        ["冠词"] = "art.", ["分词"] = "v.", ["缩写"] = "", ["缩略词"] = "", ["俗语"] = "", ["叹词"] = "int."
    };

    private readonly AsyncLookupCache<string, Entry?> pages = new(128, entry => entry == null || entry.Meaning.Length <= 32_768);
    public ValueTask<Entry?> FindAsync(string text, CancellationToken token) => FindAsync(text, token, false);
    public ValueTask<Entry?> RetryAsync(string text, CancellationToken token) => FindAsync(text, token, true);

    private async ValueTask<Entry?> FindAsync(string text, CancellationToken token, bool refresh)
    {
        string selection = text.Trim();
        if (selection.Length is 0 or > 80) return null;
        // A punctuated sentence belongs to the selected sentence translator.
        if (selection.Any(char.IsWhiteSpace) && ".!?".Contains(selection[^1])) return null;
        string word = selection.Trim('.', ',', ':', ';', '!', '?', '"', '“', '”', '(', ')').Replace('’', '\'').Trim();
        if (!Term.IsMatch(word)) return null;
        Task<Entry?> Fetch(string title) => pages.GetAsync(title, ct => FetchAsync(title, ct), token, refresh);
        var entry = await Fetch(word).ConfigureAwait(false);
        string lower = word.ToLowerInvariant();
        if (entry == null && word != lower) entry = await Fetch(lower).ConfigureAwait(false);
        return entry == null || entry.Word == word ? entry : entry with { Word = word };
    }

    private async Task<Entry?> FetchAsync(string title, CancellationToken token)
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < Interlocked.Read(ref retryAfterUtc))
            throw new HttpRequestException("Rate limited.", null, HttpStatusCode.TooManyRequests);
        const string endpoint = "https://zh.wiktionary.org/w/api.php?action=parse&format=json&formatversion=2&prop=text&uselang=zh-hans&variant=zh-hans&redirects=1&page=";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + Uri.EscapeDataString(title));
        request.Headers.UserAgent.ParseAdd("ReadingPanel/1.0 (interactive Windows dictionary)");
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter;
            var deadline = retry?.Date ?? DateTimeOffset.UtcNow.Add(retry?.Delta ?? TimeSpan.FromMinutes(1));
            Interlocked.Exchange(ref retryAfterUtc, deadline.ToUnixTimeSeconds());
        }
        response.EnsureSuccessStatusCode();
        using var content = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(content, cancellationToken: token).ConfigureAwait(false);
        var root = json.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            if (error.GetProperty("code").GetString() is "missingtitle" or "invalidtitle") return null;
            throw new InvalidDataException("Wiktionary could not return the entry.");
        }
        var page = root.GetProperty("parse");
        return ReadPage(title, page.GetProperty("title").GetString()!, page.GetProperty("text").GetString()!, token);
    }

    internal static Entry? ReadPage(string displayed, string title, string html, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var document = new HtmlDocument();
        document.LoadHtml(html);
        token.ThrowIfCancellationRequested();
        var body = document.DocumentNode.Descendants().FirstOrDefault(n => n.HasClass("mw-parser-output"));
        if (body == null) return null;
        bool english = false;
        string section = "", phonetic = "";
        var definitions = new List<string>();
        foreach (var node in body.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element))
        {
            token.ThrowIfCancellationRequested();
            if (definitions.Count == 24 && phonetic.Length != 0) break;
            var heading = node.HasClass("mw-heading") ? node.SelectSingleNode("h2|h3|h4|h5|h6") : node;
            if (heading?.Name is "h2" or "h3" or "h4" or "h5" or "h6")
            {
                string name = Text(heading);
                if (heading.Name == "h2")
                {
                    if (english) break;
                    english = name is "英语" or "英語";
                    section = "";
                }
                else section = Regex.Replace(name, @"\s*\d+$", "").Trim();
                continue;
            }
            if (!english) continue;
            if (phonetic.Length == 0 && section is "发音" or "發音")
            {
                var ipa = node.Descendants("span").FirstOrDefault(n => n.HasClass("IPA"));
                if (ipa != null) phonetic = Text(ipa).Trim('/','[',']');
            }
            if (definitions.Count == 24) continue;
            if (node.Name == "ol" && Parts.TryGetValue(section, out var part))
            {
                foreach (var item in node.Elements("li").Take(12))
                {
                    token.ThrowIfCancellationRequested();
                    if (definitions.Count == 24) break;
                    var clone = item.CloneNode(true);
                    foreach (var extra in clone.Descendants().Where(n => n.Name is "dl" or "ul" or "ol" or "sup" or "style").ToArray())
                    {
                        token.ThrowIfCancellationRequested();
                        extra.Remove();
                    }
                    AddDefinition(definitions, Text(clone), part);
                }
            }
            else if (section is "翻译" or "翻譯")
            {
                foreach (var item in node.Descendants("li"))
                {
                    token.ThrowIfCancellationRequested();
                    if (definitions.Count == 24) break;
                    var hit = Regex.Match(Text(item), @"^(?:汉语|漢語|中文|普通话|普通話)\s*[：:]\s*(.+)$");
                    if (hit.Success) AddDefinition(definitions, hit.Groups[1].Value, "");
                }
            }
            else if (node.Name == "p" && section is "" or "发音" or "發音" && definitions.Count == 0 &&
                     !node.Descendants("a").Any() && !node.Descendants().Any(n => n.HasClass("headword")))
                AddDefinition(definitions, Text(node), "");
        }
        if (definitions.Count == 0) return null;
        var source = new Uri("https://zh.wiktionary.org/zh-hans/" + Uri.EscapeDataString(title.Replace(' ', '_')));
        return new Entry(displayed, title, phonetic, string.Join("\n", definitions)) {
            Attribution = new("维基词典", source, "CC BY-SA 4.0", new Uri("https://creativecommons.org/licenses/by-sa/4.0/"))
        };
    }

    private static string Text(HtmlNode node) => Regex.Replace(HtmlEntity.DeEntitize(node.InnerText), @"\s+", " ").Trim();

    private static void AddDefinition(List<string> definitions, string text, string part)
    {
        if (definitions.Count >= 24 || !text.Any(c => c is >= '\u3400' and <= '\u9fff')) return;
        string line = part.Length == 0 ? text : part + " " + text;
        if (!definitions.Contains(line)) definitions.Add(line);
    }
}
