using System.Text;
using System.Text.RegularExpressions;

namespace DiffBeacon.App;

/// <summary>検索の条件（本家 LastSearchInfos の検索する文字列・単語単位・大文字小文字・正規表現）。</summary>
internal sealed record TextSearchQuery(string Pattern, bool MatchCase, bool WholeWord, bool Regex);

/// <summary>見つかった位置（本文の文字位置と長さ）。正規表現なら置換の \1 などに使う一致の結果も持つ。</summary>
internal sealed record TextSearchMatch(int Start, int Length, Match? RegexMatch);

/// <summary>
/// 本家WinMerge（Crystal Edit の CCrystalTextView::FindTextInBlock と FindTextHelper.cpp の FindStringHelper）と同じ規則で、
/// 本文を行ごとに検索する。画面の文書（ゴースト行を含む）ではなく、正本の本物の本文を対象にする純粋な処理で、UI には触れない。
/// - 行ごとに探し、一致は行をまたがない（本家は正規表現に改行を含むときだけ複数行を連ねるが、ここでは扱わない）。
/// - 前方検索は開始位置の行をその列から、以降の行を先頭から探す。後方検索は開始位置より前で終わる一致のうち最後のもの
///   （本家と同じく、その行を開始位置で切った文字列の中を探す）。
/// - 単語単位は、一致の前後の文字が英数字・下線でないこと（本家の xisalnum）。正規表現のときは使わない（本家も無効にする）。
/// - 大文字と小文字を区別しないときは、文字ごとに大文字へそろえて比べる（本家の CharUpper による比較に当たる OrdinalIgnoreCase）。
/// - 正規表現は .NET の正規表現で、本家の独自の正規表現とは文法が一部異なる（MIGRATION.md に記載）。一致に時間がかかりすぎたら中止する。
/// - 長さ0の一致（^ など）も一致として扱う。続けて探すときに同じ位置で止まらないよう、呼び出し側が skipEmptyAt を渡す。
/// </summary>
internal sealed class TextSearcher
{
    /// <summary>一回の一致に掛けてよい時間。病的な正規表現で画面が止まらないようにする。</summary>
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private readonly TextSearchQuery _query;
    private readonly Regex? _regex;

    /// <summary>検索の準備。検索する文字列が空、または正規表現が正しくなければ <see cref="FormatException"/>。</summary>
    internal TextSearcher(TextSearchQuery query)
    {
        _query = query;
        if (query.Pattern.Length == 0) throw new FormatException("検索する文字列を入力してください。");
        if (!query.Regex) return;
        // Compiled は Native AOT で使えない（解釈実行になる）ため指定しない。行ごとに当てるので Multiline も使わない（^ は行頭だけ）。
        var options = RegexOptions.CultureInvariant | (query.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try { _regex = new Regex(query.Pattern, options, MatchTimeout); }
        catch (ArgumentException error) { throw new FormatException("正規表現が正しくありません: " + error.Message, error); }
    }

    internal TextSearchQuery Query => _query;

    /// <summary>本文の各行の内容の範囲（改行を含まない）。改行で終わる本文・空の本文は、最後に空の行を持つ（本家の行の数え方）。</summary>
    internal static List<(int Start, int Length)> Lines(string text)
    {
        var lines = new List<(int, int)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                lines.Add((start, i - start));
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
            else if (text[i] == '\n') { lines.Add((start, i - start)); start = i + 1; }
        }
        lines.Add((start, text.Length - start));
        return lines;
    }

    /// <summary>文字位置を含む行（改行の中を指すなら、その改行で終わる行）。</summary>
    internal static int LineOf(List<(int Start, int Length)> lines, int offset)
    {
        int low = 0, high = lines.Count - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (lines[mid].Start <= offset) low = mid; else high = mid - 1;
        }
        return low;
    }

    /// <summary>
    /// from から後ろへ探す（本家 FindTextInBlock の前方検索）。[blockStart, blockEnd) の外の一致は使わない。
    /// 末尾まで見つからなければ、wrap のときだけ blockStart から探し直す。skipEmptyAt の位置にある長さ0の一致は飛ばす。
    /// </summary>
    internal TextSearchMatch? FindForward(string text, int from, int blockStart, int blockEnd, bool wrap, int? skipEmptyAt, out bool wrapped)
    {
        wrapped = false;
        var lines = Lines(text);
        from = Math.Clamp(from, blockStart, blockEnd);
        for (var pass = 0; pass < 2; pass++)
        {
            var lineIndex = LineOf(lines, from);
            var column = from - lines[lineIndex].Start;
            for (; lineIndex < lines.Count; lineIndex++, column = 0)
            {
                var (start, length) = lines[lineIndex];
                if (start > blockEnd) break;
                var line = text.Substring(start, length);
                var position = Math.Min(column, length);
                while (true)
                {
                    var found = FindInLine(line, position);
                    if (found is null) break;
                    var (index, matchLength, match) = found.Value;
                    var absolute = start + index;
                    // 本家と同じく、ブロックの終わり（本文全体なら本文の末尾）の位置以降で始まる一致は見つからなかったものとする。
                    // その後ろの一致はさらに後ろで始まるので、この行はここで打ち切る。
                    if (absolute >= blockEnd || absolute + matchLength > blockEnd) break;
                    if (absolute < blockStart || matchLength == 0 && skipEmptyAt == absolute)
                    {
                        position = index + 1;
                        if (position > length) break;
                        continue;
                    }
                    return new(absolute, matchLength, match);
                }
            }
            if (!wrap || pass == 1) return null;
            wrapped = true; from = blockStart;
        }
        return null;
    }

    /// <summary>
    /// before より前を後ろから探す（本家の後方検索）。その行を before で切った文字列の中の最後の一致を返す。
    /// 先頭まで見つからなければ、wrap のときだけ本文の末尾から探し直す。
    /// </summary>
    internal TextSearchMatch? FindBackward(string text, int before, bool wrap, int? skipEmptyAt, out bool wrapped)
    {
        wrapped = false;
        var lines = Lines(text);
        before = Math.Clamp(before, 0, text.Length);
        for (var pass = 0; pass < 2; pass++)
        {
            var lineIndex = LineOf(lines, before);
            var limit = before - lines[lineIndex].Start;
            for (; lineIndex >= 0; lineIndex--, limit = -1)
            {
                var (start, length) = lines[lineIndex];
                var end = limit < 0 ? length : Math.Clamp(limit, 0, length);
                var line = text.Substring(start, end);
                (int Index, int Length, Match? Match)? last = null;
                var position = 0;
                while (position <= line.Length)
                {
                    var found = FindInLine(line, position);
                    if (found is null) break;
                    // 本文の末尾の位置で始まる一致は前方検索と同じく使わない。直前に見つけた長さ0の一致の位置でも止まらない。
                    var absolute = start + found.Value.Index;
                    if (absolute < text.Length && !(found.Value.Length == 0 && skipEmptyAt == absolute)) last = found;
                    position = found.Value.Index + Math.Max(1, found.Value.Length);
                }
                if (last is { } hit) return new(start + hit.Index, hit.Length, hit.Match);
            }
            if (!wrap || pass == 1) return null;
            wrapped = true; before = text.Length;
        }
        return null;
    }

    /// <summary>[start, start+length) がちょうど一つの一致か（置換する前に、選択している文字列が検索の一致かを確かめる）。</summary>
    internal TextSearchMatch? MatchAt(string text, int start, int length)
    {
        if (start < 0 || length < 0 || start + length > text.Length) return null;
        var lines = Lines(text);
        var lineIndex = LineOf(lines, start);
        var (lineStart, lineLength) = lines[lineIndex];
        if (start + length > lineStart + lineLength) return null;
        var line = text.Substring(lineStart, lineLength);
        var position = start - lineStart;
        var found = FindInLine(line, position);
        return found is { } hit && hit.Index == position && hit.Length == length ? new(start, length, hit.Match) : null;
    }

    /// <summary>
    /// [blockStart, blockEnd) の中の一致をすべて置き換える（本家の「すべて置換」）。元の本文の一致だけを置き換え、置き換えた文字列は
    /// 探し直さない（"here" を "there" にしても二重に置き換えない）。数えるのは、一致か置換後の文字列のどちらかが空でないもの（本家と同じ）。
    /// </summary>
    internal (string Text, int Count) ReplaceAll(string text, string replacement, int blockStart, int blockEnd)
    {
        var lines = Lines(text);
        var builder = new StringBuilder(text.Length);
        var copied = 0; var count = 0;
        foreach (var (start, length) in lines)
        {
            if (start + length < blockStart) continue;
            if (start > blockEnd) break;
            var line = text.Substring(start, length);
            var position = Math.Max(0, blockStart - start);
            while (position <= length)
            {
                var found = FindInLine(line, position);
                if (found is null) break;
                var (index, matchLength, match) = found.Value;
                var absolute = start + index;
                if (absolute >= blockEnd || absolute + matchLength > blockEnd) break;
                var inserted = ReplacementFor(new(absolute, matchLength, match), replacement);
                builder.Append(text, copied, absolute - copied).Append(inserted);
                copied = absolute + matchLength;
                if (matchLength != 0 || inserted.Length != 0) count++;
                position = index + Math.Max(1, matchLength);
            }
        }
        builder.Append(text, copied, text.Length - copied);
        return (builder.ToString(), count);
    }

    /// <summary>
    /// 置換後の文字列。正規表現でなければそのまま（$ や \ も文字として入れる）。正規表現なら本家 RxReplace と同じ書式:
    /// \0～\9 は一致・グループ、\r \n \t \a \b は制御文字、\U \L は以降を大文字／小文字、\u \l は次の文字だけ、\E \e で戻す、
    /// \xHH・\dDDD・\oOOO は文字コード、その他の \x は x そのもの。参加しなかったグループ・末尾の \ は誤りにする
    /// （本家はこのとき一致した文字列を消してしまうが、ここでは本文を変えずに理由を出す）。
    /// </summary>
    internal string ReplacementFor(TextSearchMatch found, string replacement)
    {
        if (!_query.Regex || found.RegexMatch is not { } match) return replacement;
        var builder = new StringBuilder(replacement.Length + found.Length);
        var upper = false; var lower = false; var upperNext = false; var lowerNext = false;
        void Add(string value)
        {
            if (value.Length == 0) return;
            var segment = upper ? value.ToUpperInvariant() : lower ? value.ToLowerInvariant() : value;
            if (upperNext) { segment = char.ToUpperInvariant(segment[0]) + segment[1..]; upperNext = false; }
            else if (lowerNext) { segment = char.ToLowerInvariant(segment[0]) + segment[1..]; lowerNext = false; }
            builder.Append(segment);
        }
        for (var i = 0; i < replacement.Length; i++)
        {
            var c = replacement[i];
            if (c != '\\') { Add(c.ToString()); continue; }
            if (++i >= replacement.Length) throw new FormatException("置換後の文字列の末尾の \\ の後に文字がありません。");
            var e = replacement[i];
            switch (e)
            {
                case >= '0' and <= '9':
                    var group = match.Groups[e - '0'];
                    if (e - '0' >= match.Groups.Count || !group.Success) throw new FormatException($"置換後の文字列の \\{e} に当たるグループが一致していません。");
                    Add(group.Value); break;
                case 'r': Add("\r"); break;
                case 'n': Add("\n"); break;
                case 't': Add("\t"); break;
                case 'a': Add("\a"); break;
                case 'b': Add("\b"); break;
                case 'U': upper = true; break;
                case 'L': lower = true; break;
                case 'u': upperNext = true; break;
                case 'l': lowerNext = true; break;
                case 'E' or 'e': upper = lower = false; break;
                case 'x': Add(((char)Code(replacement, ref i, 2, 16)).ToString()); break;
                case 'd': Add(((char)Code(replacement, ref i, 3, 10)).ToString()); break;
                case 'o': Add(((char)Code(replacement, ref i, 3, 8)).ToString()); break;
                default: Add(e.ToString()); break;
            }
        }
        return builder.ToString();
    }

    // \x・\d・\o の後の決まった桁数の数字を読む。
    private static int Code(string text, ref int index, int digits, int radix)
    {
        var value = 0;
        for (var n = 0; n < digits; n++)
        {
            if (index + 1 >= text.Length) throw new FormatException("置換後の文字列の文字コードの桁が足りません。");
            var c = char.ToUpperInvariant(text[++index]);
            var digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'A' and <= 'F' ? c - 'A' + 10 : int.MaxValue;
            if (digit >= radix) throw new FormatException("置換後の文字列の文字コードが正しくありません。");
            value = value * radix + digit;
        }
        return value;
    }

    /// <summary>一行（改行を含まない）の中で、position 以降に始まる最初の一致。</summary>
    private (int Index, int Length, Match? Match)? FindInLine(string line, int position)
    {
        if (position > line.Length) return null;
        if (_regex is { } regex)
        {
            var match = regex.Match(line, position);
            return match.Success ? (match.Index, match.Length, match) : null;
        }
        var comparison = _query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var pattern = _query.Pattern;
        while (position <= line.Length - pattern.Length)
        {
            var index = line.IndexOf(pattern, position, comparison);
            if (index < 0) return null;
            if (_query.WholeWord && (index > 0 && IsWordChar(line[index - 1]) || index + pattern.Length < line.Length && IsWordChar(line[index + pattern.Length])))
            { position = index + 1; continue; }
            return (index, pattern.Length, null);
        }
        return null;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
