using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DiffBeacon.Core;

// 原文を変えず、文書ごとの構文状態と行ごとの比較キーだけを生成する。
internal sealed class TextPreprocessor
{
    internal const int MaxRegexTextLength = 1_048_576;
    private const int MaxPatternLength = 16_384;
    private const int MaxReplacementLength = 65_536;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly ComparisonOptions options;
    private readonly CancellationToken token;
    private readonly (Regex Regex, string Replacement)[] substitutions;

    internal TextPreprocessor(ComparisonOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(options.CommentSyntax)) throw new ArgumentOutOfRangeException(nameof(options.CommentSyntax));
        ArgumentNullException.ThrowIfNull(options.SubstitutionRules);
        if (options.SubstitutionRules.Count > 256) throw new ArgumentException("置換規則は 256 個までです。", nameof(options));
        this.options = options;
        this.token = token;
        var rules = new List<(Regex, string)>();
        foreach (var rule in options.SubstitutionRules)
        {
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(rule);
            ArgumentNullException.ThrowIfNull(rule.Pattern);
            ArgumentNullException.ThrowIfNull(rule.Replacement);
            if (!rule.Enabled || rule.Pattern.Length == 0) continue;
            if (rule.Replacement.Length > MaxReplacementLength)
                throw new ArgumentException("置換文字列は 65,536 文字までです。", nameof(options));
            if (rule.Pattern.Length > MaxPatternLength)
                throw new ArgumentException("正規表現は 16,384 文字までです。", nameof(options));
            var pattern = rule.UseRegex ? rule.Pattern : Regex.Escape(rule.Pattern);
            if (!rule.UseRegex && rule.WholeWord) pattern = @"\b" + pattern + @"\b";
            rules.Add((CreateRegex(pattern, rule.MatchCase), DecodeReplacement(rule.Replacement)));
        }
        substitutions = rules.ToArray();
    }

    internal static Regex CreateRegex(string pattern, bool matchCase)
    {
        if (pattern.Length > MaxPatternLength * 2 + 4)
            throw new ArgumentException("正規表現が長すぎます。", nameof(pattern));
        return new(pattern, RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), RegexTimeout);
    }

    internal static void CheckRegexInput(string text)
    {
        if (text.Length > MaxRegexTextLength)
            throw new ArgumentException("正規表現の入力・出力は行ごとに 1,048,576 文字までです。", nameof(text));
    }

    internal (string[] Keys, bool[] CommentOnly) Process(IReadOnlyList<TextLine> lines)
    {
        var keys = new string[lines.Count];
        var commentOnly = new bool[lines.Count];
        var scanner = new CommentScanner(options.CommentSyntax, token);
        for (var index = 0; index < lines.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var filtered = scanner.Filter(lines[index].Content);
            commentOnly[index] = filtered.HadComment && filtered.Text.Length == 0;
            keys[index] = NormalizeFiltered(filtered.Text);
        }
        return (keys, commentOnly);
    }

    internal string NormalizeStandalone(string text)
    {
        // 公開ヘルパーにも文書内の改行を残す。TextDiffer は行ごとの状態を共有する。
        var scanner = new CommentScanner(options.CommentSyntax, token);
        var result = new StringBuilder(text.Length);
        foreach (var line in TextLines.Parse(text, token))
        {
            token.ThrowIfCancellationRequested();
            result.Append(Substitute(scanner.Filter(line.Content).Text)).Append(line.Ending);
        }
        return options.NormalizeKey(result.ToString(), token);
    }

    private string NormalizeFiltered(string text)
        => options.NormalizeKey(Substitute(text), token);

    private string Substitute(string text)
    {
        foreach (var (regex, replacement) in substitutions)
        {
            token.ThrowIfCancellationRequested();
            CheckRegexInput(text);
            var timer = Stopwatch.StartNew();
            var result = new StringBuilder(text.Length);
            var cursor = 0;
            for (var match = regex.Match(text); match.Success; match = match.NextMatch())
            {
                token.ThrowIfCancellationRequested();
                if (timer.Elapsed > RegexTimeout) throw new RegexMatchTimeoutException(text, regex.ToString(), RegexTimeout);
                AppendBounded(result, text.AsSpan(cursor, match.Index - cursor));
                AppendReplacement(result, match, replacement);
                cursor = match.Index + match.Length;
            }
            token.ThrowIfCancellationRequested();
            AppendBounded(result, text.AsSpan(cursor));
            text = result.ToString();
        }
        return text;
    }

    private void AppendReplacement(StringBuilder result, Match match, string replacement)
    {
        // 各参照を別々に展開し、$' 等の繰り返しで巨大な一時文字列を作らない。
        var index = 0;
        while (index < replacement.Length)
        {
            token.ThrowIfCancellationRequested();
            if (replacement[index] != '$')
            {
                var end = replacement.IndexOf('$', index);
                if (end < 0) end = replacement.Length;
                AppendBounded(result, replacement.AsSpan(index, end - index));
                index = end;
                continue;
            }
            var stop = index + 1;
            if (stop < replacement.Length)
            {
                if (replacement[stop] is >= '0' and <= '9')
                    while (stop < replacement.Length && replacement[stop] is >= '0' and <= '9') stop++;
                else if (replacement[stop] == '{' && replacement.IndexOf('}', stop) is var close && close >= 0) stop = close + 1;
                else stop++;
            }
            AppendBounded(result, match.Result(replacement[index..stop]).AsSpan());
            index = stop;
        }
    }

    private static void AppendBounded(StringBuilder result, ReadOnlySpan<char> text)
    {
        if (text.Length > MaxRegexTextLength - result.Length)
            throw new ArgumentException("置換後の行が 1,048,576 文字を超えます。");
        result.Append(text);
    }

    // SubstitutionList.cpp の制御文字エスケープと \1 キャプチャ参照を引き継ぐ。
    private static string DecodeReplacement(string replacement)
    {
        var result = new StringBuilder(replacement.Length);
        for (var index = 0; index < replacement.Length; index++)
        {
            if (replacement[index] != '\\') { result.Append(replacement[index]); continue; }
            if (++index == replacement.Length) break;
            var next = replacement[index];
            if (next is >= '0' and <= '9') { result.Append('$').Append(next); continue; }
            if (next == 'x' && index + 2 < replacement.Length &&
                byte.TryParse(replacement.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                result.Append((char)hex);
                index += 2;
                continue;
            }
            result.Append(next switch
            {
                'a' => '\a', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v', _ => next
            });
        }
        return result.ToString();
    }

    private sealed class CommentScanner(CommentSyntax syntax, CancellationToken token)
    {
        private string? commentEnd;
        private string? literalEnd;
        private char quote;
        private bool verbatim;
        private bool continuedLineComment;
        private bool xmlTag;

        internal (string Text, bool HadComment) Filter(string text)
        {
            if (syntax == CommentSyntax.None) return (text, false);
            var result = new StringBuilder(text.Length);
            var hadComment = commentEnd is not null;
            var index = 0;
            var nextCancellationCheck = 0;
            bool At(string value) => text.AsSpan(index).StartsWith(value, StringComparison.Ordinal);
            void Keep(int length) { result.Append(text.AsSpan(index, length)); index += length; }
            if (continuedLineComment)
            {
                continuedLineComment = text.EndsWith('\\');
                return ("", true);
            }
            while (index < text.Length)
            {
                if (index >= nextCancellationCheck)
                {
                    token.ThrowIfCancellationRequested();
                    nextCancellationCheck = index + 4096;
                }
                if (commentEnd is not null)
                {
                    hadComment = true;
                    if (At(commentEnd)) { index += commentEnd.Length; commentEnd = null; }
                    else index++;
                    continue;
                }
                if (literalEnd is not null)
                {
                    if (At(literalEnd)) { Keep(literalEnd.Length); literalEnd = null; }
                    else if (syntax == CommentSyntax.Python && text[index] == '\\') Keep(Math.Min(2, text.Length - index));
                    else Keep(1);
                    continue;
                }
                if (quote != '\0')
                {
                    var character = text[index];
                    Keep(1);
                    if (character == quote)
                    {
                        if (verbatim && index < text.Length && text[index] == quote) Keep(1);
                        else { quote = '\0'; verbatim = false; }
                    }
                    else if (!verbatim && syntax != CommentSyntax.Xml && character == '\\' && index < text.Length) Keep(1);
                    continue;
                }
                if (syntax == CommentSyntax.Xml)
                {
                    if (At("<![CDATA[")) { Keep(9); literalEnd = "]]>"; continue; }
                    if (At("<?")) { Keep(2); literalEnd = "?>"; continue; }
                    if (At("<!--")) { index += 4; commentEnd = "-->"; hadComment = true; continue; }
                    if (text[index] == '<') xmlTag = true;
                    if (xmlTag && text[index] is '\'' or '"') quote = text[index];
                    if (text[index] == '>') xmlTag = false;
                    Keep(1);
                    continue;
                }
                if (syntax == CommentSyntax.Python && text[index] == '#') { hadComment = true; break; }
                if (syntax is CommentSyntax.CStyle or CommentSyntax.CSharp)
                {
                    if (At("//"))
                    {
                        hadComment = true;
                        continuedLineComment = syntax == CommentSyntax.CStyle && text.EndsWith('\\');
                        break;
                    }
                    if (At("/*")) { index += 2; commentEnd = "*/"; hadComment = true; continue; }
                    if (syntax == CommentSyntax.CStyle && At("R\"") && IsRawStringStart(text, index))
                    {
                        var delimiterLength = Math.Min(17, text.Length - index - 2);
                        var relativeOpen = text.AsSpan(index + 2, delimiterLength).IndexOf('(');
                        var open = relativeOpen < 0 ? -1 : index + 2 + relativeOpen;
                        if (open >= index + 2 &&
                            !text.AsSpan(index + 2, open - index - 2).ContainsAny(' ', '\\', ')') &&
                            !text.AsSpan(index + 2, open - index - 2).ContainsAny('\t', '\r', '\n'))
                        {
                            literalEnd = ")" + text[(index + 2)..open] + "\"";
                            Keep(open - index + 1);
                            continue;
                        }
                    }
                }
                if (text[index] is '\'' or '"')
                {
                    quote = text[index];
                    if (syntax == CommentSyntax.Python && index + 2 < text.Length && text[index + 1] == quote && text[index + 2] == quote)
                    {
                        literalEnd = new string(quote, 3); quote = '\0'; Keep(3); continue;
                    }
                    if (syntax == CommentSyntax.CSharp && quote == '"')
                    {
                        var count = 1;
                        while (index + count < text.Length && text[index + count] == '"') count++;
                        if (count >= 3) { literalEnd = new string('"', count); quote = '\0'; Keep(count); continue; }
                        verbatim = index > 0 && text[index - 1] == '@' || index > 1 && text[index - 2] == '@' && text[index - 1] == '$';
                    }
                }
                Keep(1);
            }
            // 通常の引用符は行末で終了し、継続記号・複数行リテラルだけ状態を保持する。
            if (quote != '\0' && !verbatim && syntax != CommentSyntax.Xml && !text.EndsWith('\\')) quote = '\0';
            return (result.ToString(), hadComment);
        }

        private static bool IsRawStringStart(string text, int index)
        {
            // R の直前にある正式なエンコーディング接頭辞も含めて境界を判定する。
            var start = index;
            if (index >= 2 && text[index - 2] == 'u' && text[index - 1] == '8') start -= 2;
            else if (index > 0 && text[index - 1] is 'u' or 'U' or 'L') start--;
            return start == 0 || !IsIdentifier(text[start - 1]);
        }

        private static bool IsIdentifier(char character) => char.IsLetterOrDigit(character) || character == '_';
    }
}
