using System.Globalization;
using System.Text;

namespace DiffBeacon.Core;

public sealed record TableColumnRangeRule(string Specification, TablePaneMask Panes)
{
    public const int MaximumRules = 256;
    public const int MaximumSpecificationLength = 65_536;

    public static void ValidateRules(IReadOnlyList<TableColumnRangeRule>? rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count > MaximumRules)
            throw new ArgumentException($"列範囲は {MaximumRules} 件まで指定できます。", nameof(rules));
        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rule is null || string.IsNullOrWhiteSpace(rule.Specification)
                || rule.Specification.Length > MaximumSpecificationLength
                || rule.Panes == 0 || (rule.Panes & ~TablePaneMask.All) != 0)
                throw new ArgumentException($"列範囲は1から{MaximumSpecificationLength}文字、対象は左・祖先・右の有効なペインで指定してください。", nameof(rules));
            _ = TableColumnRangePlan.Parse(rule.Specification);
        }
    }
}

internal readonly record struct ColumnRangeSegment(int Start, int? End, bool Excluded);

internal sealed class TableColumnRangePlan
{
    private readonly (int Start, int End)[] exclusions;
    private readonly ColumnRangeSegment[] inclusions;
    private readonly bool hasInclusions;

    private TableColumnRangePlan(ColumnRangeSegment[] segments)
    {
        inclusions = segments.Where(segment => !segment.Excluded).ToArray();
        hasInclusions = inclusions.Length > 0;
        exclusions = MergeExclusions(segments.Where(segment => segment.Excluded)
            .Select(segment => (segment.Start, segment.End ?? int.MaxValue)));
    }

    internal static TableColumnRangePlan ForPane(IReadOnlyList<TableColumnRangeRule> rules, TablePaneMask pane)
    {
        var combined = new List<ColumnRangeSegment>();
        foreach (var rule in rules)
        {
            if ((rule.Panes & pane) == 0) continue;
            combined.AddRange(Parse(rule.Specification));
        }
        return new(combined.ToArray());
    }

    internal static ColumnRangeSegment[] Parse(string specification)
    {
        if (string.IsNullOrWhiteSpace(specification) || specification.Length > TableColumnRangeRule.MaximumSpecificationLength)
            throw new ArgumentException("列範囲の指定が空か上限を超えています。", nameof(specification));

        var ranges = new List<ColumnRangeSegment>();
        var position = 0;
        while (true)
        {
            SkipWhitespace(specification, ref position);
            if (position >= specification.Length) throw InvalidSpecification();

            var excluded = specification[position] == '!';
            if (excluded)
            {
                position++;
                SkipWhitespace(specification, ref position);
            }

            var start = ParseColumnNumber(specification, ref position);
            SkipWhitespace(specification, ref position);
            int? end = start;
            if (position < specification.Length && specification[position] == '-')
            {
                position++;
                SkipWhitespace(specification, ref position);
                end = position == specification.Length || specification[position] == ','
                    ? null : ParseColumnNumber(specification, ref position);
                if (end is int last && last < start) throw InvalidSpecification();
            }
            ranges.Add(new(start, end, excluded));

            SkipWhitespace(specification, ref position);
            if (position == specification.Length) return ranges.ToArray();
            if (specification[position] != ',') throw InvalidSpecification();
            position++;
        }
    }

    internal void AppendSelected(StringBuilder output, IReadOnlyList<TableCell> cells, char delimiter,
        CancellationToken cancellationToken, IReadOnlyDictionary<int, TablePaneMask> ignoredColumns,
        TablePaneMask pane, int[] seenColumns, int generation)
    {
        var first = true;
        if (!hasInclusions)
        {
            var exclusionIndex = 0;
            for (var index = 0; index < cells.Count; index++)
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var column = index + 1;
                while (exclusionIndex < exclusions.Length && exclusions[exclusionIndex].End < column) exclusionIndex++;
                if (exclusionIndex < exclusions.Length && exclusions[exclusionIndex].Start <= column
                    && exclusions[exclusionIndex].End >= column) continue;
                AppendCell(output, cells, index, delimiter, ignoredColumns, pane, ref first);
            }
            return;
        }

        var work = 0;
        foreach (var range in inclusions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Math.Min(range.End ?? cells.Count, cells.Count);
            for (var column = range.Start; column <= end; column++)
            {
                if ((work++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var index = column - 1;
                if (seenColumns[index] == generation) continue;
                seenColumns[index] = generation;
                if (IsExcluded(column)) continue;
                AppendCell(output, cells, index, delimiter, ignoredColumns, pane, ref first);
            }
        }
    }

    private bool IsExcluded(int column)
    {
        var low = 0;
        var high = exclusions.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var range = exclusions[middle];
            if (column < range.Start) high = middle - 1;
            else if (column > range.End) low = middle + 1;
            else return true;
        }
        return false;
    }

    private static void AppendCell(StringBuilder output, IReadOnlyList<TableCell> cells, int index, char delimiter,
        IReadOnlyDictionary<int, TablePaneMask> ignoredColumns, TablePaneMask pane, ref bool first)
    {
        if (IsIgnored(index + 1, ignoredColumns, pane)) return;
        if (!first) output.Append(delimiter);
        output.Append(cells[index].Value);
        first = false;
    }

    private static bool IsIgnored(int column, IReadOnlyDictionary<int, TablePaneMask> ignoredColumns, TablePaneMask pane)
        => ignoredColumns.TryGetValue(column, out var panes) && (panes & pane) != 0;

    private static (int Start, int End)[] MergeExclusions(IEnumerable<(int Start, int End)> ranges)
    {
        var ordered = ranges.OrderBy(range => range.Start).ThenBy(range => range.End).ToArray();
        if (ordered.Length < 2) return ordered;
        var merged = new List<(int Start, int End)>(ordered.Length);
        foreach (var range in ordered)
        {
            if (merged.Count == 0 || range.Start > merged[^1].End + (merged[^1].End == int.MaxValue ? 0 : 1))
                merged.Add(range);
            else if (range.End > merged[^1].End)
                merged[^1] = (merged[^1].Start, range.End);
        }
        return merged.ToArray();
    }

    private static int ParseColumnNumber(string specification, ref int position)
    {
        var begin = position;
        while (position < specification.Length && char.IsAsciiDigit(specification[position])) position++;
        if (begin == position || !int.TryParse(specification.AsSpan(begin, position - begin), NumberStyles.None,
            CultureInfo.InvariantCulture, out var value) || value < 1)
            throw InvalidSpecification();
        return value;
    }

    private static void SkipWhitespace(string specification, ref int position)
    {
        while (position < specification.Length && specification[position] is ' ' or '\t' or '\r' or '\n' or '\f' or '\v') position++;
    }

    private static ArgumentException InvalidSpecification()
        => new("列範囲は1以上の列番号・昇順範囲・除外指定をカンマ区切りで指定してください。", "specification");
}
