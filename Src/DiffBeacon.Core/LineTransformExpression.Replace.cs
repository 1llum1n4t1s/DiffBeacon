namespace DiffBeacon.Core;

internal sealed partial class LineTransformExpression
{
    private sealed record ReplacePattern(int[] Scalars, int[] Prefix);

    private static ReplacePattern PrepareReplace(string needle, string replacement, Context c)
    {
        // UTF-16不正は割当て前のscalar走査で拒否する。型とnoneの検査後だけ準備する。
        var count = checked((int)CharacterCount(needle, c));
        _ = CharacterCount(replacement, c);
        var allocation = checked(count * 8L);
        c.Budget.Charge(allocation + 1, c.Token, allocation);
        var scalars = new int[count];
        var prefix = new int[count];
        var position = 0;
        for (var index = 0; index < count; index++)
        {
            c.Budget.Charge(1, c.Token);
            var scalar = ReadScalar(needle, ref position);
            scalars[index] = c.CaseSensitive ? scalar : LineTransformCaseFold.Fold(scalar, c.Budget, c.Token);
        }
        for (var index = 1; index < count; index++)
        {
            c.Budget.Charge(1, c.Token);
            var matched = prefix[index - 1];
            while (matched > 0 && scalars[index] != scalars[matched])
            { c.Budget.Charge(1, c.Token); matched = prefix[matched - 1]; }
            if (scalars[index] == scalars[matched]) matched++;
            prefix[index] = matched;
        }
        return new(scalars, prefix);
    }

    private static string? ReplaceLiteral(string text, string replacement, ReplacePattern pattern, Context c)
    {
        var count = checked((int)CharacterCount(text, c));
        if (pattern.Scalars.Length == 0)
        {
            // Poco empty-matchはsubject末尾で停止し、一致ごとに元UTF-8を1byte進める。
            // 非ASCIIの残りbyteが次回matchで不正になる。原典regexSubstのnoneを保つ。
            for (var index = 0; index < text.Length; index++)
            { c.Budget.Charge(1, c.Token); if (text[index] > 127) return null; }
            var unit = ReplacementLength(replacement, 0, c);
            var length = checked(text.Length * unit);
            c.Budget.Charge(length, c.Token, length);
            return string.Create(checked((int)length), (text.Length, replacement, c), static (destination, state) =>
            {
                var position = 0;
                for (var index = 0; index < state.Length; index++)
                    WriteReplacement(state.replacement, ReadOnlySpan<char>.Empty, destination, ref position, state.c);
            });
        }
        // scalar列と元UTF-16境界を一度だけ予約。大小文字でbyte長が異なる$0も原文を保持。
        var allocation = checked(count * 8L + 4);
        c.Budget.Charge(allocation, c.Token, allocation);
        var scalars = new int[count];
        var boundaries = new int[count + 1];
        var position = 0;
        for (var index = 0; index < count; index++)
        {
            c.Budget.Charge(1, c.Token);
            boundaries[index] = position;
            var scalar = ReadScalar(text, ref position);
            scalars[index] = c.CaseSensitive ? scalar : LineTransformCaseFold.Fold(scalar, c.Budget, c.Token);
        }
        boundaries[count] = position;
        var resultLength = ReplacePass(text, replacement, pattern, scalars, boundaries, Span<char>.Empty, false, c, out var changed);
        if (!changed) return text;
        c.Budget.Charge(resultLength, c.Token, resultLength);
        return string.Create(checked((int)resultLength), (text, replacement, pattern, scalars, boundaries, c), static (destination, state) =>
            ReplacePass(state.text, state.replacement, state.pattern, state.scalars, state.boundaries, destination, true, state.c, out _));
    }

    private static long ReplacePass(string text, string replacement, ReplacePattern pattern, int[] scalars,
        int[] boundaries, Span<char> destination, bool write, Context c, out bool changed)
    {
        var matched = 0; var copied = 0; var position = 0; long length = 0; changed = false;
        for (var index = 0; index < scalars.Length; index++)
        {
            c.Budget.Charge(1, c.Token);
            while (matched > 0 && scalars[index] != pattern.Scalars[matched])
            { c.Budget.Charge(1, c.Token); matched = pattern.Prefix[matched - 1]; }
            if (scalars[index] == pattern.Scalars[matched]) matched++;
            if (matched != pattern.Scalars.Length) continue;
            changed = true;
            var start = boundaries[index + 1 - matched]; var end = boundaries[index + 1];
            length = checked(length + start - copied + ReplacementLength(replacement, end - start, c));
            if (write)
            {
                CopyReplaceSpan(text.AsSpan(copied, start - copied), destination, ref position, c);
                WriteReplacement(replacement, text.AsSpan(start, end - start), destination, ref position, c);
            }
            copied = end; matched = 0;
        }
        length = checked(length + text.Length - copied);
        if (write) CopyReplaceSpan(text.AsSpan(copied), destination, ref position, c);
        return length;
    }

    private static long ReplacementLength(string replacement, int originalLength, Context c)
    {
        long length = 0;
        for (var index = 0; index < replacement.Length; index++)
        {
            c.Budget.Charge(1, c.Token);
            var character = replacement[index];
            if (character == '$' && index + 1 < replacement.Length && replacement[index + 1] is >= '0' and <= '9')
            { if (replacement[++index] == '0') length = checked(length + originalLength); }
            else if (character == '$' && index + 1 < replacement.Length) { index++; length += 2; }
            else length++;
        }
        return length;
    }

    private static void WriteReplacement(string replacement, ReadOnlySpan<char> original, Span<char> destination, ref int position, Context c)
    {
        for (var index = 0; index < replacement.Length; index++)
        {
            c.Budget.Charge(1, c.Token);
            var character = replacement[index];
            if (character == '$' && index + 1 < replacement.Length && replacement[index + 1] is >= '0' and <= '9')
            { if (replacement[++index] == '0') CopyReplaceSpan(original, destination, ref position, c); }
            else if (character == '$' && index + 1 < replacement.Length)
            { destination[position++] = '$'; destination[position++] = replacement[++index]; }
            else destination[position++] = character;
        }
    }

    private static void CopyReplaceSpan(ReadOnlySpan<char> source, Span<char> destination, ref int position, Context c)
    {
        // 大きな$0/不一致部分も取消を定期観測し、出力の公開はstring.Create完了後だけ。
        for (var begin = 0; begin < source.Length; begin += 256)
        {
            var length = Math.Min(256, source.Length - begin);
            c.Budget.Charge(length, c.Token);
            source.Slice(begin, length).CopyTo(destination.Slice(position, length)); position += length;
        }
    }
}
