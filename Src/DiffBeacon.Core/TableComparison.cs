using System.Globalization;
using System.Text;

namespace DiffBeacon.Core;

public sealed record AlignedTableRow(int? LeftRow, int? BaseRow, int? RightRow);

public sealed class TableComparisonResult
{
    private readonly IReadOnlyDictionary<(int Row, int Column, int Side), DiffKind> differences;
    public IReadOnlyList<TableDocument> Documents { get; }
    public IReadOnlyList<AlignedTableRow> Rows { get; }
    public bool HasDifferences { get; }
    public bool AlignmentFallback { get; }
    public int ColumnCount { get; }

    internal TableComparisonResult(IReadOnlyList<TableDocument> documents, IReadOnlyList<AlignedTableRow> rows,
        IReadOnlyDictionary<(int Row, int Column, int Side), DiffKind> differences, bool alignmentFallback, bool hasDifferences)
    {
        Documents = documents;
        Rows = rows;
        this.differences = differences;
        AlignmentFallback = alignmentFallback;
        HasDifferences = hasDifferences;
        ColumnCount = documents.Max(document => document.ColumnCount);
    }

    public int? GetSourceRow(int side, int alignedRow)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(side);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(side, Documents.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(alignedRow);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(alignedRow, Rows.Count);
        var row = Rows[alignedRow];
        return side == 0 ? row.LeftRow : Documents.Count == 2 || side == 2 ? row.RightRow : row.BaseRow;
    }

    public TableCell? GetCell(int side, int alignedRow, int column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, ColumnCount);
        var source = GetSourceRow(side, alignedRow);
        if (source is null) return null;
        var cells = Documents[side].Rows[source.Value - 1].Cells;
        return column < cells.Count ? cells[column] : null;
    }

    public DiffKind GetKind(int side, int alignedRow, int column)
    {
        _ = GetCell(side, alignedRow, column);
        return differences.GetValueOrDefault((alignedRow, column, side), DiffKind.Equal);
    }
}

// 完全一致アンカーの間だけ類似行を探索する。旧共通文字量方式の完全再現ではない。
internal sealed class TableAlignment
{
    internal const int MaxTextLength = 64 * 1024 * 1024;
    internal const int MaxRows = 262_144;
    internal const int MaxCells = 1_048_576;
    private const int MaxGapComparisons = 262_144;
    private readonly ComparisonOptions options;
    private readonly CancellationToken token;
    private readonly TextPreprocessor preprocessor;
    private readonly System.Text.RegularExpressions.Regex? ignoredPattern;
    private readonly Dictionary<string, string> cellKeys = new(StringComparer.Ordinal);
    private long budget;
    private bool fallback;

    private TableAlignment(ComparisonOptions options, CancellationToken token)
    {
        this.options = options;
        this.token = token;
        budget = Math.Clamp(options.MaxFallbackComparisons, 0, 8_000_000);
        preprocessor = new(options, token);
        ignoredPattern = options.CreateIgnoredLineRegex();
    }

    internal static TableComparisonResult Compare(TableDocument[] documents, ComparisonOptions options, CancellationToken token)
        => new TableAlignment(options, token).Run(documents);

    private TableComparisonResult Run(TableDocument[] documents)
    {
        var keys = new string[documents.Length][];
        for (var side = 0; side < documents.Length; side++)
        {
            keys[side] = new string[documents[side].Rows.Count];
            for (var row = 0; row < keys[side].Length; row++)
            {
                token.ThrowIfCancellationRequested();
                var key = new StringBuilder();
                foreach (var cell in documents[side].Rows[row].Cells)
                {
                    token.ThrowIfCancellationRequested();
                    AppendPart(key, CellKey(cell.Value));
                }
                keys[side][row] = key.ToString();
            }
        }
        List<AlignedTableRow> rows;
        if (documents.Length == 2)
        {
            rows = [];
            foreach (var pair in Align(keys[0], 0, keys[0].Length, keys[1], 0, keys[1].Length))
            {
                token.ThrowIfCancellationRequested();
                rows.Add(new(pair.A + 1, null, pair.B + 1));
            }
        }
        else rows = AlignThree(keys);
        var differences = new Dictionary<(int Row, int Column, int Side), DiffKind>();
        var different = false;
        for (var row = 0; row < rows.Count; row++)
        {
            token.ThrowIfCancellationRequested();
            var aligned = rows[row];
            var left = Cells(documents[0], aligned.LeftRow);
            var right = Cells(documents[^1], aligned.RightRow);
            var ancestor = documents.Length == 3 ? Cells(documents[1], aligned.BaseRow) : [];
            var columns = Math.Max(Math.Max(left.Count, right.Count), ancestor.Count);
            for (var column = 0; column < columns; column++)
            {
                if ((column & 255) == 0) token.ThrowIfCancellationRequested();
                var l = column < left.Count ? left[column] : null;
                var r = column < right.Count ? right[column] : null;
                var lr = Kind(l, r);
                different |= lr != DiffKind.Equal;
                if (documents.Length == 2)
                {
                    Store(0, lr);
                    Store(1, lr);
                }
                else
                {
                    var b = column < ancestor.Count ? ancestor[column] : null;
                    var bl = Kind(b, l);
                    var br = Kind(b, r);
                    different |= bl != DiffKind.Equal || br != DiffKind.Equal;
                    Store(0, bl);
                    Store(1, Combine(bl, br));
                    Store(2, br);
                }
                void Store(int side, DiffKind kind)
                {
                    if (kind != DiffKind.Equal) differences.Add((row, column, side), kind);
                }
            }
        }
        token.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(documents), rows.AsReadOnly(), differences, fallback, different);
    }

    private static IReadOnlyList<TableCell> Cells(TableDocument document, int? row)
        => row.HasValue ? document.Rows[row.Value - 1].Cells : [];

    private DiffKind Kind(TableCell? a, TableCell? b)
    {
        if (a is null) return b is null ? DiffKind.Equal : DiffKind.Added;
        if (b is null) return DiffKind.Deleted;
        token.ThrowIfCancellationRequested();
        if (a.Value == b.Value || CellKey(a.Value) == CellKey(b.Value)) return DiffKind.Equal;
        return TextDiffer.Compare(a.Value, b.Value, options, token).HasDifferences ? DiffKind.Modified : DiffKind.Equal;
    }

    private static DiffKind Combine(DiffKind a, DiffKind b)
        => a == b ? a : a == DiffKind.Equal ? b : b == DiffKind.Equal ? a : DiffKind.Modified;

    private static void AppendPart(StringBuilder builder, string text)
        => builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text);

    private string CellKey(string value)
    {
        if (cellKeys.TryGetValue(value, out var cached)) return cached;
        var lines = TextLines.Parse(value, token);
        var filtered = preprocessor.Process(lines);
        var key = new StringBuilder();
        for (var index = 0; index < lines.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var line = lines[index];
            if (ignoredPattern is not null) TextPreprocessor.CheckRegexInput(line.Content);
            if (filtered.CommentOnly[index] || options.IgnoreBlankLines && string.IsNullOrWhiteSpace(line.Content) ||
                (ignoredPattern?.IsMatch(line.Content) ?? false)) continue;
            AppendPart(key, filtered.Keys[index]);
            if (options.CompareLineEndings) AppendPart(key, line.Ending);
            if (!options.IgnoreFinalNewLine) key.Append(index == lines.Count - 1 && line.Ending.Length == 0 ? 'N' : 'E');
            key.Append(';');
        }
        var result = key.ToString();
        // 反復値だけを共有し、unique 長大セルの二重保持を制限する。
        if (cellKeys.Count < 4096 && value.Length <= 4096) cellKeys.Add(value, result);
        return result;
    }

    private List<AlignedTableRow> AlignThree(string[][] keys)
    {
        var left = Align(keys[1], 0, keys[1].Length, keys[0], 0, keys[0].Length);
        var right = Align(keys[1], 0, keys[1].Length, keys[2], 0, keys[2].Length);
        var result = new List<AlignedTableRow>();
        var li = 0;
        var ri = 0;
        for (var ancestor = 0; ancestor <= keys[1].Length; ancestor++)
        {
            token.ThrowIfCancellationRequested();
            var leftStart = li;
            var rightStart = ri;
            while (li < left.Count && left[li].A is null) { token.ThrowIfCancellationRequested(); li++; }
            while (ri < right.Count && right[ri].A is null) { token.ThrowIfCancellationRequested(); ri++; }
            if (li > leftStart || ri > rightStart)
            {
                var lStart = li > leftStart ? left[leftStart].B!.Value : 0;
                var rStart = ri > rightStart ? right[rightStart].B!.Value : 0;
                foreach (var insertion in Align(keys[0], lStart, lStart + li - leftStart,
                    keys[2], rStart, rStart + ri - rightStart))
                {
                    token.ThrowIfCancellationRequested();
                    result.Add(new(insertion.A + 1, null, insertion.B + 1));
                }
            }
            if (ancestor < keys[1].Length)
            {
                result.Add(new(left[li++].B + 1, ancestor + 1, right[ri++].B + 1));
            }
        }
        return result;
    }

    private readonly record struct Pair(int? A, int? B);

    private List<Pair> Align(string[] a, int aStart, int aEnd, string[] b, int bStart, int bEnd)
    {
        var result = new List<Pair>();
        while (aStart < aEnd && bStart < bEnd && a[aStart] == b[bStart])
        {
            token.ThrowIfCancellationRequested();
            result.Add(new(aStart++, bStart++));
        }
        var suffix = 0;
        while (aStart < aEnd - suffix && bStart < bEnd - suffix && a[aEnd - suffix - 1] == b[bEnd - suffix - 1])
        {
            token.ThrowIfCancellationRequested();
            suffix++;
        }
        aEnd -= suffix;
        bEnd -= suffix;
        var anchors = Anchors(a, aStart, aEnd, b, bStart, bEnd);
        foreach (var (ai, bi) in anchors)
        {
            token.ThrowIfCancellationRequested();
            Gap(a, aStart, ai, b, bStart, bi, result);
            result.Add(new(ai, bi));
            aStart = ai + 1;
            bStart = bi + 1;
        }
        Gap(a, aStart, aEnd, b, bStart, bEnd, result);
        for (var index = 0; index < suffix; index++)
        {
            token.ThrowIfCancellationRequested();
            result.Add(new(aEnd + index, bEnd + index));
        }
        return result;
    }

    private List<(int A, int B)> Anchors(string[] a, int aStart, int aEnd, string[] b, int bStart, int bEnd)
    {
        var occurrences = new Dictionary<string, (int A, int B)>(StringComparer.Ordinal);
        for (var index = aStart; index < aEnd; index++)
        {
            token.ThrowIfCancellationRequested();
            occurrences[a[index]] = occurrences.TryGetValue(a[index], out var entry) ? (-1, entry.B) : (index, -1);
        }
        for (var index = bStart; index < bEnd; index++)
        {
            token.ThrowIfCancellationRequested();
            if (occurrences.TryGetValue(b[index], out var entry))
                occurrences[b[index]] = (entry.A, entry.B == -1 ? index : -2);
        }
        var candidates = new List<(int A, int B)>();
        for (var index = aStart; index < aEnd; index++)
        {
            token.ThrowIfCancellationRequested();
            var entry = occurrences[a[index]];
            if (entry.A == index && entry.B >= 0) candidates.Add((index, entry.B));
        }
        var tails = new List<int>();
        var previous = new int[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var low = 0;
            var high = tails.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (candidates[tails[middle]].B < candidates[index].B) low = middle + 1;
                else high = middle;
            }
            previous[index] = low == 0 ? -1 : tails[low - 1];
            if (low == tails.Count) tails.Add(index);
            else tails[low] = index;
        }
        var result = new List<(int A, int B)>();
        for (var index = tails.Count == 0 ? -1 : tails[^1]; index >= 0; index = previous[index])
        {
            token.ThrowIfCancellationRequested();
            result.Add(candidates[index]);
        }
        result.Reverse();
        return result;
    }

    private void Gap(string[] a, int aStart, int aEnd, string[] b, int bStart, int bEnd, List<Pair> result)
    {
        var ac = aEnd - aStart;
        var bc = bEnd - bStart;
        if (ac == 0 || bc == 0) { Zip(); return; }
        var comparisons = (long)ac * bc;
        if (comparisons > MaxGapComparisons || comparisons > budget)
        {
            fallback = true;
            Zip();
            return;
        }
        // 配列は候補数から上限が決まる。行数の二乗に比例する無制限確保をしない。
        var width = bc + 1;
        var costs = new int[(ac + 1) * width];
        var steps = new byte[costs.Length];
        for (var ai = 1; ai <= ac; ai++) { token.ThrowIfCancellationRequested(); costs[ai * width] = ai * 1000; }
        for (var bi = 1; bi <= bc; bi++) { token.ThrowIfCancellationRequested(); costs[bi] = bi * 1000; }
        for (var ai = 1; ai <= ac; ai++)
        {
            token.ThrowIfCancellationRequested();
            for (var bi = 1; bi <= bc; bi++)
            {
                if (!TryCost(a[aStart + ai - 1], b[bStart + bi - 1], out var matchCost))
                {
                    fallback = true;
                    Zip();
                    return;
                }
                var at = ai * width + bi;
                var diagonal = costs[at - width - 1] + matchCost;
                var deletion = costs[at - width] + 1000;
                var insertion = costs[at - 1] + 1000;
                costs[at] = Math.Min(diagonal, Math.Min(deletion, insertion));
                steps[at] = costs[at] == diagonal ? (byte)0 : costs[at] == deletion ? (byte)1 : (byte)2;
            }
        }
        var reverse = new List<Pair>(Math.Max(ac, bc));
        var x = ac;
        var y = bc;
        while (x > 0 || y > 0)
        {
            token.ThrowIfCancellationRequested();
            if (x > 0 && y > 0 && steps[x * width + y] == 0) reverse.Add(new(aStart + --x, bStart + --y));
            else if (x > 0 && (y == 0 || steps[x * width + y] == 1)) reverse.Add(new(aStart + --x, null));
            else reverse.Add(new(null, bStart + --y));
        }
        reverse.Reverse();
        result.AddRange(reverse);
        void Zip()
        {
            for (var index = 0; index < Math.Max(ac, bc); index++)
            {
                token.ThrowIfCancellationRequested();
                result.Add(new(index < ac ? aStart + index : null, index < bc ? bStart + index : null));
            }
        }
    }

    private bool TryCost(string a, string b, out int cost)
    {
        token.ThrowIfCancellationRequested();
        cost = 1000;
        if (budget-- <= 0) return false;
        if (ReferenceEquals(a, b)) { cost = 0; return true; }
        if (a.Length == b.Length)
        {
            if (budget < a.Length) return false;
            budget -= a.Length;
            if (a == b) { cost = 0; return true; }
        }
        var common = 0;
        var length = Math.Min(a.Length, b.Length);
        // prefix/suffix の共通文字量も同じ全体予算へ課金する。
        while (common < length)
        {
            if ((common & 4095) == 0) token.ThrowIfCancellationRequested();
            if (budget-- <= 0) return false;
            if (a[common] != b[common]) break;
            common++;
        }
        var suffix = 0;
        while (suffix < length - common)
        {
            if ((suffix & 4095) == 0) token.ThrowIfCancellationRequested();
            if (budget-- <= 0) return false;
            if (a[a.Length - suffix - 1] != b[b.Length - suffix - 1]) break;
            suffix++;
        }
        cost = 1500 - (int)(1500L * (common + suffix) / Math.Max(1, Math.Max(a.Length, b.Length)));
        return true;
    }
}
