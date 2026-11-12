namespace DiffBeacon.Core;

// パスを持たず、比較開始時に渡された原文だけを参照する不変の内容値。
internal sealed class LineTransformContent
{
    private readonly string source;
    private readonly Lazy<int[]> starts;
    private readonly string? sourcePath;
    private const string DisplayPrefix = "<FileContent:";
    private const string VirtualDisplay = "<Content>";
    internal bool IncludeFinalEofCandidate { get; }
    internal int LineCount => starts.Value.Length - 1;
    internal int DisplayLength => sourcePath is null ? VirtualDisplay.Length : checked(DisplayPrefix.Length + sourcePath.Length + 1);

    internal LineTransformContent(string source, LineTransformBudget budget, CancellationToken token, string? sourcePath = null,
        bool includeFinalEofCandidate = true)
    {
        this.source = source;
        IncludeFinalEofCandidate = includeFinalEofCandidate;
        this.sourcePath = TextComparisonSourcePaths.ValidatePath(sourcePath, nameof(sourcePath));
        budget.Charge(1, token);
        // 未選択枝のContentは参照だけ。物理行の走査は実際の内容関数まで遅延する。
        starts = new(() => ParseStarts(source, budget, token));
    }

    internal string Serialize(LineTransformBudget budget, CancellationToken token)
    {
        var length = DisplayLength;
        budget.Charge(length, token, length);
        var result = string.Create(length, this, static (destination, content) => content.WriteDisplay(destination));
        token.ThrowIfCancellationRequested();
        return result;
    }

    internal void WriteDisplay(Span<char> destination)
    {
        if (sourcePath is null) VirtualDisplay.AsSpan().CopyTo(destination);
        else
        {
            DisplayPrefix.AsSpan().CopyTo(destination);
            sourcePath.AsSpan().CopyTo(destination[DisplayPrefix.Length..]);
            destination[DisplayLength - 1] = '>';
        }
    }

    internal bool REContains(Func<string, bool> search, LineTransformBudget budget, CancellationToken token,
        bool includeFinalEofCandidate = true)
    {
        var offsets = starts.Value;
        for (var line = 0; line + 1 < offsets.Length; line++)
        {
            budget.Charge(1, token);
            var start = offsets[line];
            var end = offsets[line + 1];
            if (end > start && source[end - 1] == '\n')
            {
                end--;
                if (end > start && source[end - 1] == '\r') end--;
            }
            else if (end > start && source[end - 1] == '\r') end--;
            var length = end - start;
            // 行bodyの生成と検索は同じ共有予算。原文もpathも変更・再読込みしない。
            budget.Charge(length, token, length);
            if (search(source.Substring(start, length))) return true;
        }
        token.ThrowIfCancellationRequested();
        // Unicode readerは非EOL末尾の後もtrue、NONE readerはその行でfalseを返す。
        // 空入力と末尾EOLではどちらもcleared EOF bufferを一度検索する。
        return (includeFinalEofCandidate || source.Length == 0 || source[^1] is '\r' or '\n') && search("");
    }

    internal bool Contains(string needle, bool caseSensitive, LineTransformBudget budget, CancellationToken token)
    {
        if (needle.Length == 0) return false;
        var offsets = starts.Value;
        for (var line = 0; line + 1 < offsets.Length; line++)
        {
            budget.Charge(1, token);
            var start = offsets[line];
            var end = offsets[line + 1];
            if (end > start && source[end - 1] == '\n')
            {
                end--;
                if (end > start && source[end - 1] == '\r') end--;
            }
            else if (end > start && source[end - 1] == '\r') end--;
            if (end - start < needle.Length) continue;

            for (var candidate = start; candidate <= end - needle.Length; candidate++)
            {
                var matches = true;
                for (var index = 0; index < needle.Length; index++)
                {
                    budget.Charge(1, token);
                    var actual = source[candidate + index];
                    var expected = needle[index];
                    if (!caseSensitive)
                    {
                        actual = FoldAscii(actual);
                        expected = FoldAscii(expected);
                    }
                    if (actual == expected) continue;
                    matches = false;
                    break;
                }
                if (matches) return true;
            }
        }
        return false;
    }

    private static char FoldAscii(char value) => value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;

    private static int[] ParseStarts(string source, LineTransformBudget budget, CancellationToken token)
    {
        var offsets = new List<int>();
        var start = 0;
        for (var index = 0; index < source.Length; index++)
        {
            if ((index & 255) == 0) budget.Charge(Math.Min(256, source.Length - index), token);
            if (source[index] is not ('\r' or '\n')) continue;
            budget.Charge(1, token);
            offsets.Add(start);
            if (source[index] == '\r' && index + 1 < source.Length && source[index + 1] == '\n') index++;
            start = index + 1;
        }
        if (start < source.Length) { budget.Charge(1, token); offsets.Add(start); }
        budget.Charge(offsets.Count + 1L, token);
        offsets.Add(source.Length);
        return offsets.ToArray();
    }

    internal string Sublines(long start, long length, LineTransformBudget budget, CancellationToken token)
    {
        budget.Charge(1, token);
        // countはint範囲。加算前に比較し、long.MinValueも負のまま安全に扱う。
        var offsets = starts.Value;
        var count = (long)offsets.Length - 1;
        if (start < 0) start = start < -count ? 0 : count + start;
        if (start >= count) return "";
        var remaining = count - start;
        if (length < 0) length = remaining + length + 1;
        length = Math.Clamp(length, 0, remaining);
        var offset = offsets[(int)start];
        var characters = offsets[(int)(start + length)] - offset;
        budget.Charge(characters, token, characters);
        var result = source.Substring(offset, characters);
        token.ThrowIfCancellationRequested();
        return result;
    }
}
