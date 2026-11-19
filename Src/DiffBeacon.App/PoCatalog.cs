namespace DiffBeacon.App;

/// <summary>
/// WinMerge の gettext カタログ（.po）を読む。アプリの表示言語の正本は、同梱の Translations ディレクトリ。
/// UI の固定日本語は Japanese.po の訳と揃えてあり、ここは他言語の msgstr を msgid から引く。
/// </summary>
internal sealed class PoCatalog
{
    private readonly Dictionary<string, string> _messages;

    private PoCatalog(string language, Dictionary<string, string> messages)
    {
        Language = language;
        _messages = messages;
    }

    /// <summary>ファイル名から取った言語名（Japanese など）。</summary>
    internal string Language { get; }
    /// <summary>訳の件数。</summary>
    internal int Count => _messages.Count;

    /// <summary>msgid の訳。無ければ msgid を返す。</summary>
    internal string Get(string id) => _messages.TryGetValue(id, out var text) && text.Length > 0 ? text : id;

    /// <summary>一つの .po を読む。複数形は msgstr[0] だけを採用する。</summary>
    internal static PoCatalog Load(string path)
    {
        var language = Path.GetFileNameWithoutExtension(path);
        var messages = new Dictionary<string, string>(StringComparer.Ordinal);
        string? id = null;
        var text = new System.Text.StringBuilder();
        var inText = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("msgid ", StringComparison.Ordinal))
            {
                Commit(messages, id, text, inText);
                id = ReadQuoted(line["msgid ".Length..]);
                text.Clear();
                inText = false;
            }
            else if (line.StartsWith("msgstr", StringComparison.Ordinal))
            {
                var quote = line.IndexOf('"');
                if (quote >= 0)
                {
                    text.Append(ReadQuoted(line[quote..]));
                    inText = true;
                }
            }
            else if (inText && line.StartsWith('"'))
                text.Append(ReadQuoted(line));
            else if (line.Length == 0 || line.StartsWith('#'))
            {
                Commit(messages, id, text, inText);
                id = null;
                text.Clear();
                inText = false;
            }
        }
        Commit(messages, id, text, inText);
        return new(language, messages);
    }

    /// <summary>ディレクトリ内の *.po を言語名で読む。ファイルが無ければ空。</summary>
    internal static IReadOnlyDictionary<string, PoCatalog> LoadDirectory(string directory)
    {
        var catalogs = new Dictionary<string, PoCatalog>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return catalogs;
        foreach (var path in Directory.EnumerateFiles(directory, "*.po"))
        {
            var catalog = Load(path);
            catalogs[catalog.Language] = catalog;
        }
        return catalogs;
    }

    /// <summary>実行ファイルの横、または開発時のリポジトリにある Translations/WinMerge。</summary>
    internal static string? FindDirectory()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Translations");
        if (Directory.Exists(beside)) return beside;
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Translations", "WinMerge");
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        return null;
    }

    private static void Commit(Dictionary<string, string> messages, string? id, System.Text.StringBuilder text, bool inText)
    {
        if (inText && !string.IsNullOrEmpty(id)) messages[id] = text.ToString();
    }

    /// <summary>gettext の引用符を外す。\\n、\\t、\\r、\\\\、\\" だけを戻す。</summary>
    private static string ReadQuoted(string text)
    {
        var start = text.IndexOf('"');
        var end = text.LastIndexOf('"');
        if (start < 0 || end <= start) return "";
        var body = text[(start + 1)..end];
        var result = new System.Text.StringBuilder(body.Length);
        for (var index = 0; index < body.Length; index++)
        {
            if (body[index] != '\\' || index + 1 >= body.Length) { result.Append(body[index]); continue; }
            result.Append(body[++index] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '\\' => '\\', '"' => '"', var other => other });
        }
        return result.ToString();
    }
}
