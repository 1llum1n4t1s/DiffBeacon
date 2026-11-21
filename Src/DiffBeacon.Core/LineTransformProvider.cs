using System.Text;

namespace DiffBeacon.Core;

internal sealed class LineTransformProvider(string[] lines, bool tableMode, DelimitedSyntax syntax,
    LineTransformBudget budget, CancellationToken token, LineTransformContent? content = null)
{
    internal LineTransformContent? Content => content;
    internal LineTransformBudget Budget => budget;
    internal CancellationToken Token => token;
    private int cachedIndex = -1;
    private (int Start, int Length)[]? cachedColumns;
    private bool[]? opaqueMultiline;
    internal bool ReadMultilineKey { get; private set; }
    internal void BeginEvaluation() => ReadMultilineKey = false;
    internal void RestoreMultilineRead(bool readMultilineKey) => ReadMultilineKey |= readMultilineKey;
    internal int LineCount => lines.Length;
    internal void SetLine(int index, string value, bool multiline = false)
    {
        if (multiline && opaqueMultiline is null)
        {
            budget.Charge(lines.Length, token);
            opaqueMultiline = new bool[lines.Length];
        }
        if (opaqueMultiline is not null) opaqueMultiline[index] = multiline;
        lines[index] = value; cachedIndex = -1; cachedColumns = null;
    }

    internal string? Line(long index)
    {
        budget.Charge(1, token);
        if (index < 0 || index >= lines.Length) return null;
        if (opaqueMultiline?[(int)index] is true) ReadMultilineKey = true;
        var result = lines[(int)index];
        budget.Charge(result.Length, token, result.Length);
        return result;
    }

    private (int Start, int Length)[] Columns(int index)
    {
        if (opaqueMultiline?[index] is true) ReadMultilineKey = true;
        if (cachedIndex == index && cachedColumns is not null) return cachedColumns;
        var line = lines[index];
        var spans = new List<(int Start, int Length)>();
        var start = 0;
        var inQuote = false;
        // VectorLineDataProviderは引用符ごとに状態を反転する。二重引用符は原文のまま残す。
        for (var position = 0; position < line.Length; position++)
        {
            if ((position & 255) == 0) budget.Charge(Math.Min(256, line.Length - position), token);
            if (line[position] == syntax.Quote) inQuote = !inQuote;
            else if (!inQuote && line[position] == syntax.Delimiter)
            {
                budget.Charge(1, token);
                spans.Add((start, position - start));
                start = position + 1;
            }
        }
        budget.Charge(spans.Count + 1L, token);
        spans.Add((start, line.Length - start));
        cachedIndex = index;
        return cachedColumns = spans.ToArray();
    }

    internal int ColumnCount(int index) => Columns(index).Length;
    internal string? Column(long lineIndex, long column)
    {
        budget.Charge(1, token);
        if (lineIndex < 0 || lineIndex >= lines.Length || column < 1 || column > int.MaxValue) return null;
        var spans = Columns((int)lineIndex);
        if (column > spans.Length) return null;
        var span = spans[(int)column - 1];
        budget.Charge(span.Length, token, span.Length);
        return lines[(int)lineIndex].Substring(span.Start, span.Length);
    }

    internal string? ColumnByName(long lineIndex, string name, bool caseSensitive)
    {
        if (lines.Length == 0 || lineIndex < 0 || lineIndex >= lines.Length) return null;
        var count = ColumnCount(0);
        for (var column = 1; column <= count; column++)
        {
            var header = Column(0, column)!;
            if (LineTransformExpression.CompareStrings(header, name, caseSensitive, budget, token) == 0)
                return Column(lineIndex, column);
        }
        return null;
    }

    internal string SelectColumns(int index, ColumnRangeSegment[] ranges)
    {
        var spans = Columns(index);
        budget.Charge(spans.Length * 2L + ranges.Length, token);
        var excluded = new bool[spans.Length];
        var seen = new bool[spans.Length];
        var hasInclusion = false;
        foreach (var range in ranges)
        {
            if (!range.Excluded) { hasInclusion = true; continue; }
            var end = Math.Min(range.End ?? spans.Length, spans.Length);
            for (var column = range.Start; column <= end; column++)
            {
                budget.Charge(1, token);
                excluded[column - 1] = true;
            }
        }
        var selected = new List<int>();
        if (hasInclusion)
        {
            foreach (var range in ranges)
            {
                if (range.Excluded) continue;
                var end = Math.Min(range.End ?? spans.Length, spans.Length);
                for (var column = range.Start; column <= end; column++)
                {
                    budget.Charge(1, token);
                    if (seen[column - 1] || excluded[column - 1]) continue;
                    seen[column - 1] = true;
                    selected.Add(column - 1);
                }
            }
        }
        else
            for (var column = 0; column < spans.Length; column++)
            {
                budget.Charge(1, token);
                if (!excluded[column]) selected.Add(column);
            }
        if (!tableMode) return selected.Count == 0 ? string.Empty : Line(index)!;
        long length = Math.Max(0, selected.Count - 1);
        foreach (var column in selected) { budget.Charge(1, token); length += spans[column].Length; }
        // StringBuilderと最終stringの両方のコピーを共有予算へ計上する。
        budget.Charge(length * 2, token, length * 2);
        if (length > int.MaxValue) throw new InvalidOperationException("列抽出の出力が上限を超えています。");
        var output = new StringBuilder((int)length);
        var first = true;
        foreach (var column in selected)
        {
            token.ThrowIfCancellationRequested();
            if (!first) output.Append(syntax.Delimiter);
            var span = spans[column];
            output.Append(lines[index], span.Start, span.Length);
            first = false;
        }
        return output.ToString();
    }
}
