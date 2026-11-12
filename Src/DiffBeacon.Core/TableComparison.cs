using System.Globalization;
using System.Text;

namespace DiffBeacon.Core;

public sealed record AlignedTableRow(int? LeftRow, int? BaseRow, int? RightRow);

public sealed class TableComparisonResult
{
    private readonly IReadOnlyDictionary<(int Row, int Column, int Side), DiffKind> differences;
    private readonly IReadOnlySet<(int Row, int Side)> ignoredRows;
    private readonly IReadOnlyDictionary<(int Row, int Side), DiffKind> rowKinds;
    public IReadOnlyList<TableDocument> Documents { get; }
    public IReadOnlyList<AlignedTableRow> Rows { get; }
    public bool HasDifferences { get; }
    public bool UsesLineTransforms { get; }
    public bool AlignmentFallback { get; }
    public int AlignmentWorkUsed { get; }
    public string? AlignmentFallbackReason { get; }
    public int ColumnCount { get; }

    internal TableComparisonResult(IReadOnlyList<TableDocument> documents, IReadOnlyList<AlignedTableRow> rows,
        IReadOnlyDictionary<(int Row, int Column, int Side), DiffKind> differences, WordLineAlignment alignment, bool hasDifferences,
        IReadOnlySet<(int Row, int Side)>? ignoredRows = null,
        IReadOnlyDictionary<(int Row, int Side), DiffKind>? rowKinds = null, bool usesLineTransforms = false)
    {
        Documents = documents;
        Rows = rows;
        this.differences = differences;
        this.ignoredRows = ignoredRows ?? new HashSet<(int Row, int Side)>();
        this.rowKinds = rowKinds ?? new Dictionary<(int Row, int Side), DiffKind>();
        AlignmentFallback = alignment.Fallback;
        AlignmentWorkUsed = alignment.WorkUsed;
        AlignmentFallbackReason = alignment.FallbackReason;
        HasDifferences = hasDifferences;
        UsesLineTransforms = usesLineTransforms;
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

    public bool IsIgnored(int side, int alignedRow)
    {
        _ = GetSourceRow(side, alignedRow);
        return ignoredRows.Contains((alignedRow, side));
    }

    public DiffKind GetRowKind(int side, int alignedRow)
    {
        _ = GetSourceRow(side, alignedRow);
        return rowKinds.GetValueOrDefault((alignedRow, side), DiffKind.Equal);
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
    private readonly Dictionary<int, TablePaneMask> ignoredColumns = [];
    private readonly bool hasColumnRanges;
    private readonly TableColumnRangePlan[] columnRangePlans;
    private bool hasLineTransforms;
    private readonly LineTransformBudget? lineTransformBudget;
    private readonly LineTransformPlan? lineTransformPlan;
    private readonly TextComparisonSourcePaths? sourcePaths;
    private readonly HashSet<(int SideA, int RowA, int SideB, int RowB)> transformedAnchors = [];

    private TableAlignment(ComparisonOptions options, CancellationToken token, TextComparisonSourcePaths? sourcePaths)
    {
        TableColumnIgnoreRule.ValidateRules(options.TableColumnIgnoreRules, token);
        foreach (var rule in options.TableColumnIgnoreRules)
        {
            token.ThrowIfCancellationRequested();
            ignoredColumns[rule.Column] = ignoredColumns.GetValueOrDefault(rule.Column) | rule.Panes;
        }
        TableColumnRangeRule.ValidateRules(options.TableColumnRangeRules, token);
        hasColumnRanges = options.TableColumnRangeRules.Count > 0;
        columnRangePlans = [
            TableColumnRangePlan.ForPane(options.TableColumnRangeRules, TablePaneMask.Left),
            TableColumnRangePlan.ForPane(options.TableColumnRangeRules, TablePaneMask.Base),
            TableColumnRangePlan.ForPane(options.TableColumnRangeRules, TablePaneMask.Right)
        ];
        this.options = options;
        this.token = token;
        this.sourcePaths = sourcePaths;
        ArgumentNullException.ThrowIfNull(options.LineTransformRules);
        if (options.LineTransformRules.Count > 0)
        {
            lineTransformBudget = new();
            lineTransformPlan = LineTransformPlan.Compile(options.LineTransformRules, lineTransformBudget, token,
                options.LineTransformTimestampEnvironment, options.LineTransformStringEnvironment, options.LineTransformLogEnvironment, options.LineTransformListEnvironment);
        }
        var cellOptions = options with { LineTransformRules = [] };
        alignment = new(cellOptions, token);
        preprocessor = new(cellOptions, token);
        ignoredPattern = options.CreateIgnoredLineRegex();
    }

    internal static TableComparisonResult Compare(TableDocument[] documents, ComparisonOptions options, CancellationToken token,
        TextComparisonSourcePaths? sourcePaths = null)
        => new TableAlignment(options, token, sourcePaths).Run(documents);

    private TableComparisonResult Run(TableDocument[] documents)
    {
        var activePanes = documents.Length == 2 ? TablePaneMask.Left | TablePaneMask.Right : TablePaneMask.All;
        hasLineTransforms = lineTransformPlan is not null && options.LineTransformRules.Any(rule => (rule.Panes & activePanes) != 0);
        var useColumnRanges = hasColumnRanges && options.TableColumnRangeRules.Any(rule => (rule.Panes & activePanes) != 0);
        var seenColumns = useColumnRanges ? new int[Math.Max(1, documents.Max(document => document.ColumnCount))] : [];
        var selectionGeneration = 0;
        var keys = new GnuLineKey[documents.Length][];
        for (var side = 0; side < documents.Length; side++)
        {
            if (hasLineTransforms && ignoredColumns.Count == 0 && !hasColumnRanges)
            { keys[side] = []; continue; }
            var pane = side == 0 ? TablePaneMask.Left : documents.Length == 2 || side == 2 ? TablePaneMask.Right : TablePaneMask.Base;
            keys[side] = new GnuLineKey[documents[side].Rows.Count];
            for (var row = 0; row < keys[side].Length; row++)
            {
                token.ThrowIfCancellationRequested();
                var key = new StringBuilder();
                var cells = documents[side].Rows[row].Cells;
                if (useColumnRanges)
                {
                    var selected = new StringBuilder();
                    columnRangePlans[side == 0 ? 0 : documents.Length == 2 ? 2 : side].AppendSelected(selected, cells, documents[side].Syntax.Delimiter, token,
                        ignoredColumns, pane, seenColumns, ++selectionGeneration);
                    AppendPart(key, CellKey(selected.ToString()));
                }
                else
                {
                    for (var column = 0; column < cells.Count; column++)
                    {
                        token.ThrowIfCancellationRequested();
                        // 除外は初期の行対応キーだけに適用し、原セルと変更区間のraw照合を保持する。
                        if (ignoredColumns.TryGetValue(column + 1, out var panes) && (panes & pane) != 0) continue;
                        AppendPart(key, CellKey(cells[column].Value));
                    }
                }
                // 外側行末はセル本文のキーから分離し、外側EOFの新しい比較条件を加えない。
                keys[side][row] = new(key.ToString(),
                    options.CompareLineEndings ? documents[side].Rows[row].Ending : "", false);
            }
        }
        var alignmentKeys = keys;
        if (hasLineTransforms)
        {
            var transformBudget = lineTransformBudget!;
            var plan = lineTransformPlan!;
            keys = new GnuLineKey[documents.Length][];
            for (var side = 0; side < documents.Length; side++)
            {
                token.ThrowIfCancellationRequested();
                var document = documents[side];
                var pane = side == 0 ? TablePaneMask.Left : documents.Length == 2 || side == 2 ? TablePaneMask.Right : TablePaneMask.Base;
                var raw = new List<TextLine>(document.Rows.Count);
                foreach (var row in document.Rows)
                {
                    token.ThrowIfCancellationRequested();
                    transformBudget.Charge(2L * row.RawLength, token);
                    var escapedLength = row.RawLength;
                    for (var offset = 0; offset < row.RawLength; offset++)
                    {
                        if ((offset & 4095) == 0) token.ThrowIfCancellationRequested();
                        if (document.SourceText[row.RawStart + offset] is '\u001b' or '\r' or '\n') escapedLength++;
                    }
                    transformBudget.Charge(escapedLength, token, escapedLength);
                    var escaped = new StringBuilder(escapedLength);
                    for (var offset = 0; offset < row.RawLength; offset++)
                    {
                        if ((offset & 4095) == 0) token.ThrowIfCancellationRequested();
                        var character = document.SourceText[row.RawStart + offset];
                        if (character is '\u001b' or '\r' or '\n')
                            escaped.Append('\u001b').Append(character == '\r' ? 'r' : character == '\n' ? 'n' : character);
                        else escaped.Append(character);
                    }
                    raw.Add(new(escaped.ToString(), row.Ending));
                }
                var transformed = plan.Transform(raw, pane, true, document.Syntax, token, originalContent: document.SourceText,
                    sourcePath: sourcePaths?.ForPane(pane),
                    includeFinalEofCandidate: sourcePaths?.IncludeFinalEofCandidateForPane(pane) ?? true);
                var processed = preprocessor.Process(raw, transformed);
                keys[side] = new GnuLineKey[raw.Count];
                for (var row = 0; row < raw.Count; row++)
                {
                    token.ThrowIfCancellationRequested();
                    if (ignoredPattern is not null) TextPreprocessor.CheckRegexInput(transformed[row]);
                    // 行変換後も通常の無視設定を共有し、原セルと元行は表示・保存に残す。
                    var ignored = processed.CommentOnly[row] ||
                        options.IgnoreBlankLines && string.IsNullOrWhiteSpace(transformed[row]) ||
                        (ignoredPattern?.IsMatch(transformed[row]) ?? false);
                    keys[side][row] = new(ignored ? "" : processed.Keys[row],
                        !ignored && options.CompareLineEndings ? raw[row].Ending : "", false);
                }
            }
        }
        List<AlignedTableRow> rows;
        if (documents.Length == 2)
        {
            rows = [];
            var aStart = 0;
            var bStart = 0;
            foreach (var (a, b) in ExactMatches(keys[0], 0, keys[0].Length, keys[1], 0, keys[1].Length, 0, 1))
            {
                token.ThrowIfCancellationRequested();
                AppendComparisonBlock(documents, alignmentKeys, [aStart, bStart], [a, b], rows);
                rows.Add(new(a + 1, null, b + 1));
                aStart = a + 1;
                bStart = b + 1;
            }
            AppendComparisonBlock(documents, alignmentKeys, [aStart, bStart], [keys[0].Length, keys[1].Length], rows);
        }
        else rows = AlignThree(documents, keys, alignmentKeys);
        var differences = new Dictionary<(int Row, int Column, int Side), DiffKind>();
        var ignoredRows = new HashSet<(int Row, int Side)>();
        var rowKinds = new Dictionary<(int Row, int Side), DiffKind>();
        var different = false;
        for (var row = 0; row < rows.Count; row++)
        {
            token.ThrowIfCancellationRequested();
            var aligned = rows[row];
            var left = Cells(documents[0], aligned.LeftRow);
            var right = Cells(documents[^1], aligned.RightRow);
            var ancestor = documents.Length == 3 ? Cells(documents[1], aligned.BaseRow) : [];
            if (hasLineTransforms)
            {
                if (documents.Length == 2)
                {
                    var kind = TransformRowKind(0, aligned.LeftRow, 1, aligned.RightRow);
                    StoreRow(0, kind); StoreRow(1, kind);
                }
                else
                {
                    var bl = TransformRowKind(1, aligned.BaseRow, 0, aligned.LeftRow);
                    var br = TransformRowKind(1, aligned.BaseRow, 2, aligned.RightRow);
                    StoreRow(0, bl); StoreRow(1, Combine(bl, br)); StoreRow(2, br);
                }
            }
            var lrIgnored = PairIgnored(0, aligned.LeftRow, documents.Length - 1, aligned.RightRow);
            var blIgnored = documents.Length == 3 && PairIgnored(1, aligned.BaseRow, 0, aligned.LeftRow);
            var brIgnored = documents.Length == 3 && PairIgnored(1, aligned.BaseRow, 2, aligned.RightRow);
            if (documents.Length == 2 && lrIgnored) { ignoredRows.Add((row, 0)); ignoredRows.Add((row, 1)); }
            if (documents.Length == 3)
            {
                if (blIgnored) ignoredRows.Add((row, 0));
                if (blIgnored || brIgnored) ignoredRows.Add((row, 1));
                if (brIgnored) ignoredRows.Add((row, 2));
            }
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
                    if (kind != DiffKind.Equal) { differences.Add((row, column, side), kind); StoreRow(side, kind); }
                }
                DiffKind RowKind(TableCell? a, TableCell? b, int sideA, int? sourceA, int sideB, int? sourceB)
                {
                    if (IsTransformAnchor(sideA, sourceA, sideB, sourceB)) return DiffKind.Equal;
                    var kind = Kind(a, b);
                    if (hasLineTransforms && kind == DiffKind.Equal && a is not null && b is not null &&
                        !documents[sideA].SourceText.AsSpan(a.RawStart, a.RawLength)
                            .SequenceEqual(documents[sideB].SourceText.AsSpan(b.RawStart, b.RawLength)))
                        return DiffKind.Modified;
                    return kind == DiffKind.Equal && a is not null && b is not null && options.CompareLineEndings &&
                        sourceA is int sa && sourceB is int sb && documents[sideA].Rows[sa - 1].Ending != documents[sideB].Rows[sb - 1].Ending
                        ? DiffKind.Modified : kind;
                }
            }

            void StoreRow(int side, DiffKind kind)
            {
                if (kind == DiffKind.Equal) return;
                different = true;
                rowKinds[(row, side)] = Combine(rowKinds.GetValueOrDefault((row, side), DiffKind.Equal), kind);
            }
        }
        token.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(documents), rows.AsReadOnly(), differences, alignment, different, ignoredRows, rowKinds, hasLineTransforms);

        bool PairIgnored(int sideA, int? rowA, int sideB, int? rowB)
            => IsTransformAnchor(sideA, rowA, sideB, rowB) && rowA is int a && rowB is int b && RawDifferent(sideA, a, sideB, b);
        bool RawDifferent(int sideA, int rowA, int sideB, int rowB)
        {
            var a = documents[sideA].Rows[rowA - 1]; var b = documents[sideB].Rows[rowB - 1];
            return !documents[sideA].SourceText.AsSpan(a.RawStart, a.RawLength)
                .SequenceEqual(documents[sideB].SourceText.AsSpan(b.RawStart, b.RawLength)) ||
                a.Ending != b.Ending;
        }
    }

    private bool IsTransformAnchor(int sideA, int? rowA, int sideB, int? rowB)
        => hasLineTransforms && rowA is int a && rowB is int b && transformedAnchors.Contains((sideA, a, sideB, b));

    private DiffKind TransformRowKind(int sideA, int? rowA, int sideB, int? rowB)
        => rowA is null ? rowB is null ? DiffKind.Equal : DiffKind.Added : rowB is null ? DiffKind.Deleted :
            IsTransformAnchor(sideA, rowA, sideB, rowB) ? DiffKind.Equal : DiffKind.Modified;

    private static IReadOnlyList<TableCell> Cells(TableDocument document, int? row)
        => row.HasValue ? document.Rows[row.Value - 1].Cells : [];

    private DiffKind Kind(TableCell? a, TableCell? b)
    {
        if (a is null) return b is null ? DiffKind.Equal : DiffKind.Added;
        if (b is null) return DiffKind.Deleted;
        token.ThrowIfCancellationRequested();
        if (a.Value == b.Value || CellKey(a.Value) == CellKey(b.Value)) return DiffKind.Equal;
        return TextDiffer.Compare(a.Value, b.Value, options with { LineTransformRules = [] }, token).HasDifferences ? DiffKind.Modified : DiffKind.Equal;
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

    private void AppendComparisonBlock(TableDocument[] documents, GnuLineKey[][] alignmentKeys,
        int[] starts, int[] ends, List<AlignedTableRow> result)
    {
        if (!hasLineTransforms || ignoredColumns.Count == 0 && !hasColumnRanges)
        { AppendBlock(documents, starts, ends, result); return; }
        // 静的列規則は一般変換の残差内だけで対応を補い、無視差分の根拠にはしない。
        var positions = (int[])starts.Clone();
        foreach (var (a, b) in ExactMatches(alignmentKeys[0], starts[0], ends[0], alignmentKeys[1], starts[1], ends[1]))
        {
            AppendBlock(documents, positions, [a, b], result);
            result.Add(new(a + 1, null, b + 1));
            positions = [a + 1, b + 1];
        }
        AppendBlock(documents, positions, ends, result);
    }

    private readonly record struct Edit(int Side, int Begin, int End, int OtherCount);

    private List<AlignedTableRow> AlignThree(TableDocument[] documents, GnuLineKey[][] keys, GnuLineKey[][] alignmentKeys)
    {
        // Base上の変更区間を統合し、その区間ごとに01/12/20を作る。20の無効化を次blockへ持ち越さない。
        var edits = new List<Edit>();
        var matches = new IReadOnlyList<(int A, int B)>[3];
        var matchPositions = new int[3];
        foreach (var side in new[] { 0, 2 })
        {
            var baseStart = 0;
            var otherStart = 0;
            matches[side] = ExactMatches(keys[1], 0, keys[1].Length, keys[side], 0, keys[side].Length, 1, side);
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
                ExactMatches(keys[2], positions[2], ends[2], keys[0], positions[0], ends[0], 2, 0)
                    .Select(pair => (pair.A - positions[2], pair.B - positions[0])).ToArray()
            ];
            if (hasLineTransforms && (ignoredColumns.Count > 0 || hasColumnRanges))
            {
                blockMatches[0] = AddStaticMatches(blockMatches[0], 0, 1);
                blockMatches[1] = AddStaticMatches(blockMatches[1], 1, 2);
                blockMatches[2] = AddStaticMatches(blockMatches[2], 2, 0);
            }
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

            IReadOnlyList<(int A, int B)> AddStaticMatches(IReadOnlyList<(int A, int B)> general, int sideA, int sideB)
            {
                var result = new List<(int A, int B)>();
                var startA = positions[sideA]; var startB = positions[sideB];
                foreach (var (relativeA, relativeB) in general)
                {
                    var stopA = positions[sideA] + relativeA; var stopB = positions[sideB] + relativeB;
                    AddUntil(stopA, stopB);
                    result.Add((relativeA, relativeB));
                    startA = stopA + 1; startB = stopB + 1;
                }
                AddUntil(ends[sideA], ends[sideB]);
                return result;

                void AddUntil(int stopA, int stopB)
                {
                    foreach (var (a, b) in ExactMatches(alignmentKeys[sideA], startA, stopA,
                        alignmentKeys[sideB], startB, stopB))
                        result.Add((a - positions[sideA], b - positions[sideB]));
                }
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
                if (hasLineTransforms)
                {
                    transformedAnchors.Add((0, positions[0], 2, positions[2]));
                    transformedAnchors.Add((2, positions[2], 0, positions[0]));
                }
            }
        }
    }

    private IReadOnlyList<(int A, int B)> ExactMatches(GnuLineKey[] a, GnuLineKey[] b)
        => ExactMatches(a, 0, a.Length, b, 0, b.Length);

    private IReadOnlyList<(int A, int B)> ExactMatches(GnuLineKey[] a, int aStart, int aEnd,
        GnuLineKey[] b, int bStart, int bEnd, int sideA = -1, int sideB = -1)
    {
        var matches = GnuLineMatcher.MatchSemantic(a, aStart, aEnd, b, bStart, bEnd,
            alignment.RemainingWork, token);
        alignment.AccountInitialMatches(matches);
        if (hasLineTransforms && sideA >= 0 && sideB >= 0)
            foreach (var (aRow, bRow) in matches.Pairs)
            {
                token.ThrowIfCancellationRequested();
                transformedAnchors.Add((sideA, aRow + 1, sideB, bRow + 1));
                transformedAnchors.Add((sideB, bRow + 1, sideA, aRow + 1));
            }
        return matches.Pairs;
    }

}
