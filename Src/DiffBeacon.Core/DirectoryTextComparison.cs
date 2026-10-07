namespace DiffBeacon.Core;

public sealed record DirectoryTextThreeWayResult
{
    public bool MiddleLeftEqual { get; init; }
    public bool MiddleRightEqual { get; init; }
    public bool LeftRightEqual { get; init; }
    public bool HasSignificantDifferences { get; init; }
    public int SignificantCount { get; init; }
    public int LineWorkUsed { get; init; }
    public bool LineFallback { get; init; }
    public string? LineFallbackReason { get; init; }
}

internal sealed record PreparedText(IReadOnlyList<TextLine> Lines, bool[] Ignored,
    int[] Active, GnuLineKey[] Keys);
internal sealed record DirectoryTextRange(int[] Begin, int[] End, int Op);
internal sealed record DirectoryTextAnalysis(DirectoryTextThreeWayResult Result,
    IReadOnlyList<DirectoryTextRange>[] Pairs, IReadOnlyList<DirectoryTextRange> Ranges);

public static class DirectoryTextComparer
{
    public static DirectoryTextThreeWayResult CompareThreeWay(string left, string middle, string right,
        ComparisonOptions? options = null, CancellationToken cancellationToken = default)
        => CompareDetailed(left, middle, right, options, cancellationToken).Result;

    internal static DirectoryTextAnalysis CompareDetailed(string left, string middle, string right,
        ComparisonOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(middle);
        ArgumentNullException.ThrowIfNull(right);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        var lines = new[] { TextLines.Parse(left, cancellationToken), TextLines.Parse(middle, cancellationToken),
            TextLines.Parse(right, cancellationToken) };
        var preprocessor = new TextPreprocessor(options, cancellationToken);
        var ignoredPattern = options.CreateIgnoredLineRegex();
        var texts = lines.Select(value => TextDiffer.Prepare(value, options, preprocessor,
            ignoredPattern, cancellationToken)).ToArray();
        var budget = new GnuLineMatcher.LineBudget(options.MaxFallbackComparisons, cancellationToken);
        var pairs = new IReadOnlyList<DirectoryTextRange>[3];
        var equal = new bool[3];
        var order = new[] { (1, 0), (1, 2), (0, 2) };
        for (var index = 0; index < order.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (ai, bi) = order[index];
            var a = texts[ai];
            var b = texts[bi];
            var matches = GnuLineMatcher.Match(a.Keys, b.Keys, a.Lines, b.Lines,
                a.Active, b.Active, options, budget, cancellationToken);
            if (matches.Fallback)
            {
                // 未解決本文の退避はExact Full判定として公開しない。callerはfolder Errorにする。
                return new(new() { HasSignificantDifferences = true, SignificantCount = -1,
                    LineWorkUsed = budget.Used, LineFallback = true,
                    LineFallbackReason = matches.FallbackReason }, pairs, []);
            }
            pairs[index] = BuildRanges(a, b, matches.Pairs, cancellationToken);
            equal[index] = pairs[index].All(range => range.Op == 5);
        }
        var merged = Merge(pairs[0], pairs[1], texts[0], texts[2], options, cancellationToken);
        var significant = merged.Count(range => range.Op != 5);
        cancellationToken.ThrowIfCancellationRequested();
        return new(new() { MiddleLeftEqual = equal[0], MiddleRightEqual = equal[1], LeftRightEqual = equal[2],
            HasSignificantDifferences = significant != 0, SignificantCount = significant,
            LineWorkUsed = budget.Used }, pairs, merged);
    }

    // active座標を全原文へ戻す。無視行も空範囲もinclusiveの範囲として保持する。
    private static IReadOnlyList<DirectoryTextRange> BuildRanges(PreparedText a, PreparedText b,
        IReadOnlyList<(int A, int B)> matches, CancellationToken token)
    {
        var result = new List<DirectoryTextRange>();
        var nextA = 0;
        var nextB = 0;
        foreach (var (ai, bi) in matches)
        {
            Segment(a.Active[ai], b.Active[bi]);
            nextA = a.Active[ai] + 1;
            nextB = b.Active[bi] + 1;
        }
        Segment(a.Lines.Count, b.Lines.Count);
        return result.AsReadOnly();

        void Segment(int stopA, int stopB)
        {
            while (nextA < stopA || nextB < stopB)
            {
                token.ThrowIfCancellationRequested();
                var beginA = nextA;
                var beginB = nextB;
                var trivial = (nextA < stopA && a.Ignored[nextA]) || (nextB < stopB && b.Ignored[nextB]);
                do
                {
                    token.ThrowIfCancellationRequested();
                    if (trivial)
                    {
                        if (nextA < stopA && a.Ignored[nextA]) nextA++;
                        if (nextB < stopB && b.Ignored[nextB]) nextB++;
                    }
                    else
                    {
                        if (nextA < stopA) nextA++;
                        if (nextB < stopB) nextB++;
                    }
                } while ((nextA < stopA || nextB < stopB) &&
                    trivial == ((nextA < stopA && a.Ignored[nextA]) || (nextB < stopB && b.Ignored[nextB])));
                result.Add(new([beginA, beginB, -1], [nextA - 1, nextB - 1, -1], trivial ? 5 : 4));
            }
        }
    }

    // Diff3.h Make3wayDiff: 中央側の重なりを連鎖でまとめ、原文の各側へ投影する。
    private static IReadOnlyList<DirectoryTextRange> Merge(IReadOnlyList<DirectoryTextRange> diff10,
        IReadOnlyList<DirectoryTextRange> diff12, PreparedText left, PreparedText right,
        ComparisonOptions options, CancellationToken token)
    {
        var merged = new List<DirectoryTextRange>();
        var i10 = 0;
        var i12 = 0;
        var last = new int[3];
        while (i10 < diff10.Count || i12 < diff12.Count)
        {
            token.ThrowIfCancellationRequested();
            var first10 = i10 < diff10.Count ? diff10[i10] : null;
            var first12 = i12 < diff12.Count ? diff12[i12] : null;
            var last10 = first10;
            var last12 = first12;
            var firstIs12 = first10 is null || (first12 is not null && first12.Begin[0] <= first10.Begin[0]);
            var lastIs12 = firstIs12;
            var stop10 = i10;
            var stop12 = i12;
            while (stop10 < diff10.Count && stop12 < diff12.Count)
            {
                token.ThrowIfCancellationRequested();
                var dr10 = diff10[stop10];
                var dr12 = diff12[stop12];
                if (dr10.End[0] == dr12.End[0])
                {
                    stop10++;
                    lastIs12 = true;
                    last10 = dr10;
                    last12 = dr12;
                    break;
                }
                if (lastIs12 ? Math.Max(dr12.Begin[0], dr12.End[0]) < dr10.Begin[0]
                    : Math.Max(dr10.Begin[0], dr10.End[0]) < dr12.Begin[0]) break;
                if (dr12.End[0] > dr10.End[0]) { stop10++; lastIs12 = true; }
                else { stop12++; lastIs12 = false; }
                last10 = dr10;
                last12 = dr12;
            }
            if (lastIs12) stop12++;
            else stop10++;
            var begin = new int[3];
            var end = new int[3];
            if (firstIs12)
            {
                begin[1] = first12!.Begin[0];
                begin[2] = first12.Begin[1];
                begin[0] = stop10 == i10 ? begin[1] - last[1] + last[0]
                    : begin[1] - first10!.Begin[0] + first10.Begin[1];
            }
            else
            {
                begin[0] = first10!.Begin[1];
                begin[1] = first10.Begin[0];
                begin[2] = stop12 == i12 ? begin[1] - last[1] + last[2]
                    : begin[1] - first12!.Begin[0] + first12.Begin[1];
            }
            if (lastIs12)
            {
                end[1] = last12!.End[0];
                end[2] = last12.End[1];
                end[0] = stop10 == i10 ? end[1] - last[1] + last[0]
                    : end[1] - last10!.End[0] + last10.End[1];
            }
            else
            {
                end[0] = last10!.End[1];
                end[1] = last10.End[0];
                end[2] = stop12 == i12 ? end[1] - last[1] + last[2]
                    : end[1] - last12!.End[0] + last12.End[1];
            }
            for (var side = 0; side < 3; side++) last[side] = end[side] + 1;
            var op = stop10 == i10 ? 3 : stop12 == i12 ? 1
                : CompareOuter(begin, end, left, right, options, token) ? 2 : 4;
            var trivial = true;
            for (var index = i10; index < stop10; index++)
            { token.ThrowIfCancellationRequested(); trivial &= diff10[index].Op == 5; }
            for (var index = i12; index < stop12; index++)
            { token.ThrowIfCancellationRequested(); trivial &= diff12[index].Op == 5; }
            merged.Add(new(begin, end, trivial ? 5 : op));
            i10 = stop10;
            i12 = stop12;
        }
        // 原本と同じく分類後に次範囲との重複を切る。空範囲のend=begin-1を維持する。
        for (var index = 0; index + 1 < merged.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            for (var side = 0; side < 3; side++)
                if (merged[index].End[side] >= merged[index + 1].Begin[side])
                    merged[index].End[side] = merged[index + 1].Begin[side] - 1;
        }
        return merged.AsReadOnly();
    }

    // Comp02Functorの等長・各原文行比較。02全体の等価をこの範囲の等価へ代用しない。
    private static bool CompareOuter(int[] begin, int[] end, PreparedText left, PreparedText right,
        ComparisonOptions options, CancellationToken token)
    {
        if (end[0] - begin[0] != end[2] - begin[2]) return false;
        var leftIndex = Array.BinarySearch(left.Active, begin[0]);
        var rightIndex = Array.BinarySearch(right.Active, begin[2]);
        for (var offset = 0; offset <= end[0] - begin[0]; offset++)
        {
            token.ThrowIfCancellationRequested();
            var ai = begin[0] + offset;
            var bi = begin[2] + offset;
            if ((uint)ai >= (uint)left.Lines.Count || (uint)bi >= (uint)right.Lines.Count) return false;
            if (left.Ignored[ai] || right.Ignored[bi])
            {
                if (left.Ignored[ai] != right.Ignored[bi]) return false;
                continue;
            }
            if (leftIndex < 0 || leftIndex >= left.Active.Length || left.Active[leftIndex] != ai)
                leftIndex = Array.BinarySearch(left.Active, ai);
            if (rightIndex < 0 || rightIndex >= right.Active.Length || right.Active[rightIndex] != bi)
                rightIndex = Array.BinarySearch(right.Active, bi);
            if (leftIndex < 0 || rightIndex < 0 ||
                !GnuLineMatcher.EqualKey(left.Keys[leftIndex++], right.Keys[rightIndex++], token)) return false;
        }
        return true;
    }
}
