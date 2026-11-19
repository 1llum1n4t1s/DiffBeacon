using System.Globalization;
using System.Text;

namespace DiffBeacon.App;

/// <summary>
/// 画面の文言。既定は Japanese.po。利用者が選んだ言語、または OS の言語のカタログへ切り替える。
/// メニューの &amp; は Avalonia のアクセスキー _ にする。
/// </summary>
internal static class UiText
{
    private static readonly Dictionary<string, string> Cultures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ja"] = "Japanese", ["en"] = "English", ["de"] = "German", ["fr"] = "French", ["es"] = "Spanish",
        ["it"] = "Italian", ["ko"] = "Korean", ["pl"] = "Polish", ["pt"] = "Portuguese", ["ru"] = "Russian",
        ["zh"] = "ChineseSimplified", ["zh-CN"] = "ChineseSimplified", ["zh-TW"] = "ChineseTraditional",
        ["nl"] = "Dutch", ["sv"] = "Swedish", ["tr"] = "Turkish", ["uk"] = "Ukrainian", ["cs"] = "Czech",
    };

    private static IReadOnlyDictionary<string, PoCatalog> _catalogs = new Dictionary<string, PoCatalog>();
    private static PoCatalog? _active;

    /// <summary>いまの言語名。Japanese、English、カタログ名、または os。</summary>
    internal static string Language { get; private set; } = "Japanese";

    /// <summary>起動時と設定の読込みで呼ぶ。カタログが無い言語は日本語に戻す。</summary>
    internal static void Use(string language)
    {
        EnsureLoaded();
        Language = string.IsNullOrWhiteSpace(language) ? "Japanese" : language;
        var name = Language.Equals("os", StringComparison.OrdinalIgnoreCase) ? FromCulture(CultureInfo.CurrentUICulture) : Language;
        if (name.Equals("English", StringComparison.OrdinalIgnoreCase)) { _active = null; return; }
        _active = _catalogs.TryGetValue(name, out var catalog) ? catalog : _catalogs.GetValueOrDefault("Japanese");
        if (_active is null) Language = "Japanese";
    }

    /// <summary>メニュー見出し。アクセラレータの &amp; を _ にし、訳に含まれるキー表示は外す。</summary>
    internal static string Menu(string msgid)
    {
        EnsureLoaded();
        var text = _active?.Get(msgid) ?? msgid;
        var tab = text.IndexOf('\t');
        if (tab >= 0) text = text[..tab];
        return AccessKey(text);
    }

    /// <summary>ダイアログやステータスバーの文。アクセスキーは残さない。</summary>
    internal static string Phrase(string msgid)
    {
        EnsureLoaded();
        var text = _active?.Get(msgid) ?? msgid;
        var tab = text.IndexOf('\t');
        if (tab >= 0) text = text[..tab];
        return StripAccess(text);
    }

    private static void EnsureLoaded()
    {
        if (_catalogs.Count > 0) return;
        var directory = PoCatalog.FindDirectory();
        _catalogs = directory is null ? new Dictionary<string, PoCatalog>() : PoCatalog.LoadDirectory(directory);
        _active = _catalogs.GetValueOrDefault("Japanese");
    }

    private static string FromCulture(CultureInfo culture)
    {
        if (Cultures.TryGetValue(culture.Name, out var full)) return full;
        if (Cultures.TryGetValue(culture.TwoLetterISOLanguageName, out full)) return full;
        return "Japanese";
    }

    private static string AccessKey(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '&' && index + 1 < text.Length)
            {
                if (text[index + 1] == '&') { result.Append('&'); index++; }
                else result.Append('_');
            }
            else result.Append(text[index]);
        }
        return result.ToString();
    }

    private static string StripAccess(string text) => AccessKey(text).Replace("_", "", StringComparison.Ordinal);
}
