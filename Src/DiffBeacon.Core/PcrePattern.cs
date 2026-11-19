using System.Text;

namespace DiffBeacon.Core;

/// <summary>
/// PCRE の所有量指定（*+ ++ ?+ {m,n}+）を、.NET の原子グループへ書き換える。
/// 再帰、callout、\C、可変長の後読みは対象にしない。文字クラスとエスケープの中は変えない。
/// </summary>
public static class PcrePattern
{
    /// <summary>所有量指定を (?>atom量指定) にする。所有量指定が無ければ元の文字列を返す。</summary>
    /// <summary>
    /// .NET の正規表現に無い PCRE の再帰と呼出しと制御動詞を、黙って別の意味にせず拒否する。
    /// PCRE2 本体は RID ごとのネイティブライブラリになるため、この AOT ビルドには同梱していない。
    /// </summary>
    public static void RejectUnsupported(string source)
    {
        var depth = 0;
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\\') { index++; continue; }
            if (source[index] == '[')
            {
                var escaped = false;
                for (index++; index < source.Length; index++)
                {
                    if (!escaped && source[index] == ']') break;
                    escaped = !escaped && source[index] == '\\';
                }
                continue;
            }
            if (source[index] == '(') depth++;
            if (source[index] == '(' && index + 2 < source.Length && source[index + 1] == '?')
            {
                var mark = source[index + 2];
                if (mark is 'R' or '0' || (mark == 'C' && (index + 3 >= source.Length || source[index + 3] is ')' or '0' or '1' or '2' or '3' or '4' or '5' or '6' or '7' or '8' or '9')))
                    throw new FormatException("PCRE の再帰 (?R)/(?0) と呼出し (?C) は、.NET の正規表現に無く、このビルドは PCRE2 を同梱していないため使えません。");
            }
            if (source[index] == '(' && index + 1 < source.Length && source[index + 1] == '*')
                throw new FormatException("PCRE の制御動詞 (*ACCEPT) などは、.NET の正規表現に無く、このビルドは PCRE2 を同梱していないため使えません。");
        }
        if (depth < 0) return;
    }

    public static string RewritePossessive(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new StringBuilder(source.Length + 8);
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\\')
            {
                result.Append('\\');
                if (index + 1 < source.Length) result.Append(source[++index]);
                continue;
            }
            if (source[index] == '[')
            {
                var start = index;
                var escaped = false;
                for (index++; index < source.Length; index++)
                {
                    if (!escaped && source[index] == ']') break;
                    escaped = !escaped && source[index] == '\\';
                }
                result.Append(source.AsSpan(start, Math.Min(source.Length, index + 1) - start));
                continue;
            }
            if (index + 1 < source.Length && source[index + 1] == '+' && source[index] is '*' or '+' or '?' or '}')
            {
                var quantifierStart = QuantifierStart(source, index);
                var atomStart = AtomStart(source, quantifierStart);
                var atomLength = quantifierStart - atomStart;
                if (atomStart >= 0 && atomLength > 0 && result.Length >= atomLength)
                {
                    result.Length -= atomLength;
                    result.Append("(?>").Append(source.AsSpan(atomStart, atomLength)).Append(source.AsSpan(quantifierStart, index - quantifierStart + 1)).Append(')');
                    index++;
                    continue;
                }
            }
            result.Append(source[index]);
        }
        return result.ToString();
    }

    private static int QuantifierStart(string source, int mark)
    {
        if (source[mark] != '}') return mark;
        var start = source.LastIndexOf('{', mark);
        return start >= 0 ? start : mark;
    }

    private static int AtomStart(string source, int quantifierStart)
    {
        if (quantifierStart <= 0) return -1;
        var end = quantifierStart - 1;
        if (source[end] == ')')
        {
            var depth = 0;
            for (var index = end; index >= 0; index--)
            {
                if (source[index] == ')' && !Escaped(source, index)) depth++;
                else if (source[index] == '(' && !Escaped(source, index) && --depth == 0) return index;
            }
            return -1;
        }
        if (source[end] == ']')
        {
            for (var index = end - 1; index >= 0; index--)
                if (source[index] == '[' && !Escaped(source, index)) return index;
            return -1;
        }
        return Escaped(source, end) ? end - 1 : end;
    }

    private static bool Escaped(string source, int index)
    {
        var slashes = 0;
        for (var cursor = index - 1; cursor >= 0 && source[cursor] == '\\'; cursor--) slashes++;
        return slashes % 2 == 1;
    }
}
