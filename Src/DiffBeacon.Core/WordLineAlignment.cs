namespace DiffBeacon.Core;

/// <summary>旧 raw ブロック WordDiff の一致量を使う、共有予算付きの行対応。</summary>
internal sealed class WordLineAlignment
{
    private const int MaxWork = 8_000_000;
    private const int MaxCandidates = 262_144;
    private const int MaxTextLength = 64 * 1024 * 1024;
    private const int MaxRows = 262_144;
    private const int MaxCells = 1_048_576;
    private readonly WordDiffOptions wordOptions;
    private readonly CancellationToken token;
    private readonly int initialWork;
    private int remaining;
    private int candidates;
    internal bool Fallback { get; private set; }
    internal int WorkUsed => initialWork - remaining;
    internal string? FallbackReason { get; private set; }

    internal WordLineAlignment(ComparisonOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.token = token;
        initialWork = remaining = Math.Clamp(options.MaxFallbackComparisons, 0, MaxWork);
        wordOptions = new()
        {
            CharacterLevel = options.InlineCharacterLevel,
            MatchCase = !options.IgnoreCase,
            IgnoreNumbers = options.IgnoreNumbers,
            Eol = options.CompareLineEndings ? WordEolMode.Strict : WordEolMode.Ignore,
            Whitespace = options.IgnoreWhitespace || options.Whitespace == WhitespaceMode.IgnoreAll ? WordWhitespaceMode.IgnoreAll :
                options.Whitespace == WhitespaceMode.IgnoreChanges ? WordWhitespaceMode.IgnoreChanges : WordWhitespaceMode.CompareAll
        };
    }

    internal IReadOnlyList<AlignedTableRow> Align(TableDocument[] documents, int[] starts, int[] ends,
        IReadOnlyList<(int A, int B)>[]? knownMatches = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(starts);
        ArgumentNullException.ThrowIfNull(ends);
        if (documents.Length is not (2 or 3) || starts.Length != documents.Length || ends.Length != documents.Length)
            throw new ArgumentException("行対応は同数の2/3文書とblock範囲が必要です。");
        if (knownMatches is not null && (documents.Length != 3 || knownMatches.Length != 3))
            throw new ArgumentException("既知の三者一致組は01/12/20の三つが必要です。");
        var docs = (TableDocument[])documents.Clone();
        var begin = (int[])starts.Clone();
        var end = (int[])ends.Clone();
        Validate(docs, begin, end);
        List<AlignedTableRow> rows;
        try
        {
            var blocks = new RawBlock[docs.Length];
            for (var side = 0; side < docs.Length; side++) blocks[side] = CreateBlock(docs[side], begin[side], end[side]);
            if (docs.Length == 2)
            {
                var map = BuildMap(blocks[0], blocks[1]);
                var pairs = VirtualPairs(map, blocks[1].Count);
                rows = new(pairs.Count);
                foreach (var pair in pairs)
                {
                    Spend(1, "output");
                    rows.Add(new(Global(pair.A, begin[0]), null, Global(pair.B, begin[1])));
                }
            }
            else rows = MergeThree(blocks, begin, knownMatches);
        }
        catch (LimitException limit)
        {
            MarkFallback(limit.Reason);
            rows = ZipBlock(begin, end);
        }
        if (!Complete(rows, begin, end))
        {
            // 原関数のassertだけに依存せず、誤ったmapから元行を落とさない。
            MarkFallback("map-invariant");
            rows = ZipBlock(begin, end);
            if (!Complete(rows, begin, end)) throw new InvalidOperationException("行対応の完全性を維持できません。");
        }
        token.ThrowIfCancellationRequested();
        return rows.AsReadOnly();
    }

    private void Spend(int amount, string phase)
    {
        token.ThrowIfCancellationRequested();
        if (amount < 0 || amount > remaining) throw new LimitException("work-limit:" + phase);
        remaining -= amount;
    }

    private void MarkFallback(string reason)
    {
        Fallback = true;
        FallbackReason ??= reason;
    }

    private sealed class LimitException(string reason) : Exception
    {
        internal string Reason { get; } = reason;
    }

    private void Validate(TableDocument[] docs, int[] starts, int[] ends)
    {
        for (var side = 0; side < docs.Length; side++)
        {
            token.ThrowIfCancellationRequested();
            var doc = docs[side];
            ArgumentNullException.ThrowIfNull(doc);
            ArgumentNullException.ThrowIfNull(doc.SourceText);
            ArgumentNullException.ThrowIfNull(doc.Rows);
            if (doc.Rows.Count > MaxRows) throw new ArgumentException("表の行数上限を超えています。");
            if (starts[side] < 0 || ends[side] < starts[side] || ends[side] > doc.Rows.Count)
                throw new ArgumentOutOfRangeException(nameof(starts), "block範囲が文書外です。");
            long cells = 0;
            var previousEnd = -1;
            for (var rowIndex = starts[side]; rowIndex < ends[side]; rowIndex++)
            {
                token.ThrowIfCancellationRequested();
                var row = doc.Rows[rowIndex];
                ArgumentNullException.ThrowIfNull(row);
                ArgumentNullException.ThrowIfNull(row.Ending);
                ArgumentNullException.ThrowIfNull(row.Cells);
                if (row.SourceRow != rowIndex + 1 || row.RawStart < 0 || row.RawLength < 0 ||
                    (long)row.RawStart + row.RawLength + row.Ending.Length > doc.SourceText.Length ||
                    (previousEnd >= 0 && previousEnd != row.RawStart))
                    throw new ArgumentException("論理行の原文区間が不正または非連続です。");
                previousEnd = checked(row.RawStart + row.RawLength + row.Ending.Length);
                cells += row.Cells.Count;
                if (cells > MaxCells) throw new ArgumentException("表のセル数上限を超えています。");
            }
        }
    }

    private sealed record RawBlock(TableDocument Document, int First, int Count, string Text,
        int[] Starts, int[] ContentLengths, int[] FullLengths, long[] ContentTotals)
    {
        internal int TotalLineCount => Document.Rows.Count +
            (Document.Rows.Count > 0 && Document.Rows[^1].Ending.Length > 0 ? 1 : 0);
    }

    private RawBlock CreateBlock(TableDocument document, int first, int end)
    {
        if (document.SourceText.Length > MaxTextLength) throw new LimitException("text-limit");
        var count = end - first;
        Spend(count, "row-metadata");
        var starts = new int[count + 1];
        var lengths = new int[count];
        var fullLengths = new int[count];
        var totals = new long[count + 1];
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            var row = document.Rows[first + index];
            lengths[index] = row.RawLength;
            fullLengths[index] = checked(row.RawLength + row.Ending.Length);
            starts[index + 1] = checked(starts[index] + fullLengths[index]);
            totals[index + 1] = totals[index] + row.RawLength;
        }
        Spend(starts[count], "raw-copy");
        var text = count == 0 ? "" : document.SourceText.Substring(document.Rows[first].RawStart, starts[count]);
        return new(document, first, count, text, starts, lengths, fullLengths, totals);
    }

    private readonly record struct Point(int Row, int Column);
    private readonly record struct ProjectedDifference(Point LeftBegin, Point LeftEnd, Point RightBegin, Point RightEnd);
    private readonly record struct Pair(int? A, int? B);
    private readonly record struct Region(int AStart, int AEnd, int BStart, int BEnd);

    private Point Project(RawBlock block, int offset)
    {
        if (offset < 0 || offset > block.Text.Length || block.Count == 0)
            throw new LimitException("projection-range");
        var low = 0;
        var high = block.Count;
        while (low + 1 < high)
        {
            Spend(1, "projection");
            var middle = low + (high - low) / 2;
            if (block.Starts[middle] <= offset) low = middle;
            else high = middle;
        }
        var row = block.First + low;
        var column = offset - block.Starts[low];
        if (column > block.ContentLengths[low])
        {
            // 終端EOLの旧bufferは追加空行を持つ。候補には含めず投影のみ許容する。
            if (row < block.TotalLineCount - 1) return new(row + 1, 0);
            column = block.ContentLengths[low];
        }
        return new(row, column);
    }

    private ProjectedDifference[] ProjectDifferences(RawBlock a, RawBlock b, WordDiffResult differences)
    {
        Spend(differences.Differences.Count, "projection-storage");
        var result = new ProjectedDifference[differences.Differences.Count];
        for (var index = 0; index < result.Length; index++)
        {
            Spend(1, "projection");
            var difference = differences.Differences[index];
            if (difference.Left.Length < 0 || difference.Right.Length < 0)
                throw new LimitException("projection-range");
            result[index] = new(Project(a, difference.Left.Start), Project(a, checked(difference.Left.Start + difference.Left.Length)),
                Project(b, difference.Right.Start), Project(b, checked(difference.Right.Start + difference.Right.Length)));
        }
        return result;
    }

    private int Score(RawBlock a, RawBlock b, int rowA, int rowB, ProjectedDifference[] differences)
    {
        var leftRow = a.First + rowA;
        var rightRow = b.First + rowB;
        // 元dummyの固定pane0/1は異なる開始行で一致量を落とす。実測済みのpair起点を使う。
        var previousLeft = new Point(a.First, 0);
        var previousRight = new Point(b.First, 0);
        long matched = 0;
        foreach (var difference in differences)
        {
            Spend(1, "score-projection");
            if (previousLeft.Row <= leftRow && leftRow <= difference.LeftBegin.Row &&
                previousRight.Row <= rightRow && rightRow <= difference.RightBegin.Row &&
                leftRow - previousLeft.Row == rightRow - previousRight.Row)
            {
                matched += (leftRow == difference.LeftBegin.Row ? difference.LeftBegin.Column : a.FullLengths[rowA]) -
                    (previousLeft.Row == leftRow ? previousLeft.Column : 0);
            }
            previousLeft = difference.LeftEnd;
            previousRight = difference.RightEnd;
        }
        Spend(1, "score-projection");
        if (differences.Length == 0)
        {
            if (rowA == rowB) matched += a.FullLengths[rowA];
        }
        else if (previousLeft.Row <= leftRow && previousRight.Row <= rightRow &&
            leftRow - previousLeft.Row == rightRow - previousRight.Row)
            matched += a.FullLengths[rowA] - (previousLeft.Row == leftRow ? previousLeft.Column : 0);
        return checked((int)-matched);
    }

    private int?[] BuildMap(RawBlock a, RawBlock b, IReadOnlyList<(int A, int B)>? knownMatches = null)
    {
        Spend(a.Count, "map-storage");
        var map = new int?[a.Count];
        // ゼロ側は旧offset配列の未定義参照を実行しない。
        if (a.Count == 0 || b.Count == 0) return map;
        var pending = new Stack<Region>();
        var nextA = 0;
        var nextB = 0;
        foreach (var match in knownMatches ?? [])
        {
            Spend(1, "known-match");
            if (match.A < nextA || match.B < nextB || match.A >= a.Count || match.B >= b.Count)
                throw new LimitException("known-match-invariant");
            if (nextA < match.A && nextB < match.B) pending.Push(new(nextA, match.A, nextB, match.B));
            map[match.A] = match.B;
            nextA = match.A + 1;
            nextB = match.B + 1;
        }
        if (nextA < a.Count && nextB < b.Count) pending.Push(new(nextA, a.Count, nextB, b.Count));
        if (pending.Count == 0) return map;
        // 既知一致の間の領域も原文の前から処理し、共有予算の消費順を保つ。
        Spend(pending.Count, "known-regions");
        pending = new Stack<Region>(pending);
        token.ThrowIfCancellationRequested();
        var wordDiff = WordDiffer.Compare(a.Text, b.Text, wordOptions, remaining, token);
        Spend(wordDiff.WorkUsed, "word-diff");
        if (wordDiff.Fallback) throw new LimitException("word-diff:" + wordDiff.FallbackReason);
        var differences = ProjectDifferences(a, b, wordDiff);
        while (pending.Count > 0)
        {
            Spend(1, "best-pair-stack");
            var region = pending.Pop();
            var ac = region.AEnd - region.AStart;
            var bc = region.BEnd - region.BStart;
            if (ac == 0 || bc == 0) continue;
            if (ac == 1 && bc == 1) { map[region.AStart] = region.BStart; continue; }
            if (a.ContentTotals[region.AEnd] - a.ContentTotals[region.AStart] > 4096 ||
                b.ContentTotals[region.BEnd] - b.ContentTotals[region.BStart] > 4096)
            {
                MarkFallback("legacy-content-limit");
                for (var index = 0; index < ac; index++)
                {
                    Spend(1, "legacy-zip");
                    map[region.AStart + index] = index < bc ? region.BStart + index : null;
                }
                continue;
            }
            if ((long)ac * bc > MaxCandidates - candidates) throw new LimitException("candidate-limit");
            var bestA = -1;
            var bestB = -1;
            var bestScore = int.MaxValue;
            for (var x = region.AStart; x < region.AEnd; x++)
                for (var y = region.BStart; y < region.BEnd; y++)
                {
                    Spend(1, "candidate");
                    candidates++;
                    var score = Score(a, b, x, y, differences);
                    if (score < bestScore) { bestA = x; bestB = y; bestScore = score; }
                }
            map[bestA] = bestB;
            // Stack LIFOなので後半を先に積み、原本の前半→後半再帰順を保持する。
            if (bestA + 1 < region.AEnd && bestB + 1 < region.BEnd)
                pending.Push(new(bestA + 1, region.AEnd, bestB + 1, region.BEnd));
            if (region.AStart < bestA && region.BStart < bestB)
                pending.Push(new(region.AStart, bestA, region.BStart, bestB));
        }
        return map;
    }

    private List<Pair> VirtualPairs(int?[] map, int countB)
    {
        var result = new List<Pair>();
        var y = 0;
        for (var x = 0; x < map.Length; x++)
        {
            Spend(1, "virtual-map");
            if (map[x] is not int target) { result.Add(new(x, null)); continue; }
            if (target < y || target >= countB) throw new LimitException("pair-map-invariant");
            while (y < target) { Spend(1, "virtual-map"); result.Add(new(null, y++)); }
            result.Add(new(x, y++));
        }
        while (y < countB) { Spend(1, "virtual-map"); result.Add(new(null, y++)); }
        return result;
    }

    private List<AlignedTableRow> MergeThree(RawBlock[] blocks, int[] starts, IReadOnlyList<(int A, int B)>[]? knownMatches)
    {
        var v01 = VirtualPairs(BuildMap(blocks[0], blocks[1], knownMatches?[0]), blocks[1].Count);
        var v12 = VirtualPairs(BuildMap(blocks[1], blocks[2], knownMatches?[1]), blocks[2].Count);
        var v20 = VirtualPairs(BuildMap(blocks[2], blocks[0], knownMatches?[2]), blocks[0].Count);
        var result = new List<AlignedTableRow>();
        var i01 = 0;
        var i12 = 0;
        var i20 = 0;
        var usable20 = true;
        void Add(int? a, int? ancestor, int? b)
        {
            Spend(1, "three-way-output");
            result.Add(new(Global(a, starts[0]), Global(ancestor, starts[1]), Global(b, starts[2])));
        }
        void ZipInsertions(int begin01, int end01, int begin12, int end12)
        {
            while (begin01 < end01 || begin12 < end12)
            {
                Spend(1, "three-way-zip");
                var a = begin01 < end01 ? v01[begin01++].A : null;
                var b = begin12 < end12 ? v12[begin12++].B : null;
                Add(a, null, b);
            }
        }
        for (var ancestor = 0; ancestor < blocks[1].Count; ancestor++)
        {
            Spend(1, "three-way-map");
            var begin01 = i01;
            var begin12 = i12;
            while (i01 < v01.Count && v01[i01].B != ancestor) { Spend(1, "three-way-map"); i01++; }
            while (i12 < v12.Count && v12[i12].A != ancestor) { Spend(1, "three-way-map"); i12++; }
            if (i01 == v01.Count || i12 == v12.Count) throw new LimitException("three-way-map-invariant");
            var used20 = false;
            if (usable20)
            {
                if (v01[i01].A.HasValue && v12[i12].B.HasValue)
                {
                    var target = i20;
                    for (; target < v20.Count; target++)
                    {
                        Spend(1, "three-way-search");
                        if (v20[target].A == v12[i12].B && v20[target].B == v01[i01].A) break;
                    }
                    if (target < v20.Count)
                    {
                        for (; i20 < target; i20++) Add(v20[i20].B, null, v20[i20].A);
                        i20++;
                        used20 = true;
                    }
                    else usable20 = false;
                }
                else usable20 = false;
            }
            if (!used20) ZipInsertions(begin01, i01, begin12, i12);
            Add(v01[i01++].A, ancestor, v12[i12++].B);
        }
        if (i01 < v01.Count || i12 < v12.Count)
        {
            if (usable20)
                for (; i20 < v20.Count; i20++) Add(v20[i20].B, null, v20[i20].A);
            else ZipInsertions(i01, v01.Count, i12, v12.Count);
        }
        return result;
    }

    private static int? Global(int? row, int first) => row.HasValue ? checked(row.Value + first + 1) : null;

    private List<AlignedTableRow> ZipBlock(int[] starts, int[] ends)
    {
        var count = 0;
        for (var side = 0; side < starts.Length; side++) count = Math.Max(count, ends[side] - starts[side]);
        var result = new List<AlignedTableRow>(count);
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            int? a = index < ends[0] - starts[0] ? starts[0] + index + 1 : null;
            int? b = index < ends[^1] - starts[^1] ? starts[^1] + index + 1 : null;
            int? ancestor = starts.Length == 3 && index < ends[1] - starts[1] ? starts[1] + index + 1 : null;
            result.Add(new(a, ancestor, b));
        }
        return result;
    }

    private bool Complete(IReadOnlyList<AlignedTableRow> rows, int[] starts, int[] ends)
    {
        var next = (int[])starts.Clone();
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (!row.LeftRow.HasValue && !row.BaseRow.HasValue && !row.RightRow.HasValue) return false;
            if (starts.Length == 2 && row.BaseRow.HasValue) return false;
            for (var side = 0; side < starts.Length; side++)
            {
                var source = side == 0 ? row.LeftRow : starts.Length == 2 || side == 2 ? row.RightRow : row.BaseRow;
                if (source is not int number) continue;
                if (next[side] >= ends[side] || number != next[side] + 1) return false;
                next[side]++;
            }
        }
        for (var side = 0; side < next.Length; side++) if (next[side] != ends[side]) return false;
        return true;
    }
}
