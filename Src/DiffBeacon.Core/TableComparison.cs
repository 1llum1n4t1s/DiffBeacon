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
    public int AlignmentWorkUsed { get; }
    public string? AlignmentFallbackReason { get; }
    public int ColumnCount { get; }

    internal TableComparisonResult(IReadOnlyList<TableDocument> documents, IReadOnlyList<AlignedTableRow> rows,
        IReadOnlyDictionary<(int Row, int Column, int Side), DiffKind> differences, WordLineAlignment alignment, bool hasDifferences)
    {
        Documents = documents;
        Rows = rows;
        this.differences = differences;
        AlignmentFallback = alignment.Fallback;
        AlignmentWorkUsed = alignment.WorkUsed;
        AlignmentFallbackReason = alignment.FallbackReason;
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

// 初期ブロックは復号セルキーのGNU一致で区切り、変更ブロック内は旧raw一致量で対応する。
internal sealed class TableAlignment
{
    internal const int MaxTextLength = 64 * 1024 * 1024;
    internal const int MaxRows = 262_144;
    internal const int MaxCells = 1_048_576;
    private readonly ComparisonOptions options;
    private readonly CancellationToken token;
    private readonly TextPreprocessor preprocessor;
    private readonly System.Text.RegularExpressions.Regex? ignoredPattern;
    private readonly Dictionary<string, string> cellKeys = new(StringComparer.Ordinal);
    private readonly WordLineAlignment alignment;

    private TableAlignment(ComparisonOptions options, CancellationToken token)
    {
        this.options = options;
        this.token = token;
        alignment = new(options, token);
        preprocessor = new(options, token);
        ignoredPattern = options.CreateIgnoredLineRegex();
    }

    internal static TableComparisonResult Compare(TableDocument[] documents, ComparisonOptions options, CancellationToken token)
        => new TableAlignment(options, token).Run(documents);

    private TableComparisonResult Run(TableDocument[] documents)
    {
        var keys = new GnuLineKey[documents.Length][];
        for (var side = 0; side < documents.Length; side++)
        {
            keys[side] = new GnuLineKey[documents[side].Rows.Count];
            for (var row = 0; row < keys[side].Length; row++)
            {
                token.ThrowIfCancellationRequested();
                var key = new StringBuilder();
                foreach (var cell in documents[side].Rows[row].Cells)
                {
                    token.ThrowIfCancellationRequested();
                    AppendPart(key, CellKey(cell.Value));
                }
                // 外側行末はセル本文のキーから分離し、外側EOFの新しい比較条件を加えない。
                keys[side][row] = new(key.ToString(),
                    options.CompareLineEndings ? documents[side].Rows[row].Ending : "", false);
            }
        }
        List<AlignedTableRow> rows;
        if (documents.Length == 2)
        {
            rows = [];
            var aStart = 0;
            var bStart = 0;
            foreach (var (a, b) in ExactMatches(keys[0], keys[1]))
            {
                token.ThrowIfCancellationRequested();
                AppendBlock(documents, [aStart, bStart], [a, b], rows);
                rows.Add(new(a + 1, null, b + 1));
                aStart = a + 1;
                bStart = b + 1;
            }
            AppendBlock(documents, [aStart, bStart], [keys[0].Length, keys[1].Length], rows);
        }
        else rows = AlignThree(documents, keys);
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
                var lr = RowKind(l, r, 0, aligned.LeftRow, documents.Length - 1, aligned.RightRow);
                different |= lr != DiffKind.Equal;
                if (documents.Length == 2)
                {
                    Store(0, lr);
                    Store(1, lr);
                }
                else
                {
                    var b = column < ancestor.Count ? ancestor[column] : null;
                    var bl = RowKind(b, l, 1, aligned.BaseRow, 0, aligned.LeftRow);
                    var br = RowKind(b, r, 1, aligned.BaseRow, 2, aligned.RightRow);
                    different |= bl != DiffKind.Equal || br != DiffKind.Equal;
                    Store(0, bl);
                    Store(1, Combine(bl, br));
                    Store(2, br);
                }
                void Store(int side, DiffKind kind)
                {
                    if (kind != DiffKind.Equal) differences.Add((row, column, side), kind);
                }
                DiffKind RowKind(TableCell? a, TableCell? b, int sideA, int? sourceA, int sideB, int? sourceB)
                {
                    var kind = Kind(a, b);
                    return kind == DiffKind.Equal && a is not null && b is not null && options.CompareLineEndings &&
                        sourceA is int sa && sourceB is int sb && documents[sideA].Rows[sa - 1].Ending != documents[sideB].Rows[sb - 1].Ending
                        ? DiffKind.Modified : kind;
                }
            }
        }
        token.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(documents), rows.AsReadOnly(), differences, alignment, different);
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

    private void AppendBlock(TableDocument[] documents, int[] starts, int[] ends, List<AlignedTableRow> result)
    {
        token.ThrowIfCancellationRequested();
        if (starts.Where((value, side) => value != ends[side]).Any())
            result.AddRange(alignment.Align(documents, starts, ends));
    }

    private readonly record struct Edit(int Side, int Begin, int End, int OtherCount);

    private List<AlignedTableRow> AlignThree(TableDocument[] documents, GnuLineKey[][] keys)
    {
        // Base上の変更区間を統合し、その区間ごとに01/12/20を作る。20の無効化を次blockへ持ち越さない。
        var edits = new List<Edit>();
        var matches = new IReadOnlyList<(int A, int B)>[3];
        var matchPositions = new int[3];
        foreach (var side in new[] { 0, 2 })
        {
            var baseStart = 0;
            var otherStart = 0;
            matches[side] = ExactMatches(keys[1], keys[side]);
            foreach (var (a, b) in matches[side])
            {
                if (a != baseStart || b != otherStart) edits.Add(new(side, baseStart, a, b - otherStart));
                baseStart = a + 1;
                otherStart = b + 1;
            }
            if (baseStart != keys[1].Length || otherStart != keys[side].Length)
                edits.Add(new(side, baseStart, keys[1].Length, keys[side].Length - otherStart));
        }
        token.ThrowIfCancellationRequested();
        edits.Sort((a, b) => a.Begin != b.Begin ? a.Begin.CompareTo(b.Begin) : a.End.CompareTo(b.End));
        var result = new List<AlignedTableRow>();
        var positions = new int[3];
        for (var index = 0; index < edits.Count;)
        {
            token.ThrowIfCancellationRequested();
            var begin = edits[index].Begin;
            var end = edits[index].End;
            var delta = new int[3];
            do
            {
                token.ThrowIfCancellationRequested();
                var edit = edits[index++];
                end = Math.Max(end, edit.End);
                delta[edit.Side] += edit.OtherCount - (edit.End - edit.Begin);
            } while (index < edits.Count && edits[index].Begin <= end);
            EqualUntil(begin);
            var ends = new[] { positions[0] + end - begin + delta[0], end, positions[2] + end - begin + delta[2] };
            // 他側の変更に合流しただけの同値行はraw引用表記などで再対応しない。
            IReadOnlyList<(int A, int B)>[] blockMatches =
            [
                BaseMatches(0, true), BaseMatches(2, false),
                ExactMatches(keys[2], positions[2], ends[2], keys[0], positions[0], ends[0])
                    .Select(pair => (pair.A - positions[2], pair.B - positions[0])).ToArray()
            ];
            result.AddRange(alignment.Align(documents, positions, ends, blockMatches));
            positions = ends;

            List<(int A, int B)> BaseMatches(int side, bool reverse)
            {
                var found = new List<(int A, int B)>();
                var sideMatches = matches[side];
                while (matchPositions[side] < sideMatches.Count && sideMatches[matchPositions[side]].A < begin)
                {
                    token.ThrowIfCancellationRequested();
                    matchPositions[side]++;
                }
                while (matchPositions[side] < sideMatches.Count && sideMatches[matchPositions[side]].A < end)
                {
                    token.ThrowIfCancellationRequested();
                    var pair = sideMatches[matchPositions[side]++];
                    var ancestor = pair.A - begin;
                    var other = pair.B - positions[side];
                    found.Add(reverse ? (other, ancestor) : (ancestor, other));
                }
                return found;
            }
        }
        EqualUntil(keys[1].Length);
        if (positions[0] != keys[0].Length || positions[2] != keys[2].Length)
            throw new InvalidOperationException("三者表比較の変更区間が元行を消費していません。");
        return result;

        void EqualUntil(int ancestor)
        {
            while (positions[1] < ancestor)
            {
                token.ThrowIfCancellationRequested();
                result.Add(new(++positions[0], ++positions[1], ++positions[2]));
            }
        }
    }

    private IReadOnlyList<(int A, int B)> ExactMatches(GnuLineKey[] a, GnuLineKey[] b)
        => ExactMatches(a, 0, a.Length, b, 0, b.Length);

    private IReadOnlyList<(int A, int B)> ExactMatches(GnuLineKey[] a, int aStart, int aEnd,
        GnuLineKey[] b, int bStart, int bEnd)
    {
        var matches = GnuLineMatcher.MatchSemantic(a, aStart, aEnd, b, bStart, bEnd,
            alignment.RemainingWork, token);
        alignment.AccountInitialMatches(matches);
        return matches.Pairs;
    }

}
