using System.Text;

namespace DiffBeacon.Core;

public static class TextMerger
{
    public static string CopyLeftToRight(DiffResult result, DiffBlock block) => Copy(result, block, true);
    public static string CopyRightToLeft(DiffResult result, DiffBlock block) => Copy(result, block, false);

    private static string Copy(DiffResult result, DiffBlock block, bool leftToRight)
    {
        if (!result.Blocks.Contains(block)) throw new ArgumentException("比較結果に属していないブロックです。", nameof(block));
        var source = TextLines.Parse(leftToRight ? result.LeftText : result.RightText);
        var targetText = leftToRight ? result.RightText : result.LeftText;
        var target = TextLines.Parse(targetText);
        var sourceStart = leftToRight ? block.LeftStart : block.RightStart;
        var sourceCount = leftToRight ? block.LeftCount : block.RightCount;
        var targetStart = leftToRight ? block.RightStart : block.LeftStart;
        var targetCount = leftToRight ? block.RightCount : block.LeftCount;
        target.RemoveRange(targetStart, targetCount);
        target.InsertRange(targetStart, source.GetRange(sourceStart, sourceCount));
        return TextLines.JoinSeparated(target, TextLines.NewLine(targetText));
    }
}

public sealed record MergeConflict(int BaseStartLine, int BaseLineCount, string BaseText, string LeftText, string RightText);
public sealed record MergeResult(string Text, IReadOnlyList<MergeConflict> Conflicts)
{
    public string MergedText => Text;
    public bool HasConflicts => Conflicts.Count != 0;
    public IReadOnlyList<MergeSection> Sections { get; init; } = [];
    public string NewLine { get; init; } = "\n";
}

public static class ThreeWayMerger
{
    private sealed record Edit(int Start, int Count, List<TextLine> Replacement, bool Left);

    public static MergeResult Merge(string baseText, string left, string right,
        ComparisonOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (left == right || left == baseText || right == baseText)
            return Uncontested(baseText, left, right, options, cancellationToken);
        var baseLines = TextLines.Parse(baseText);
        var leftLines = TextLines.Parse(left);
        var rightLines = TextLines.Parse(right);
        var edits = new List<Edit>();
        foreach (var block in TextDiffer.Compare(baseText, left, options, cancellationToken).Blocks)
            edits.Add(new(block.LeftStart, block.LeftCount, leftLines.GetRange(block.RightStart, block.RightCount), true));
        foreach (var block in TextDiffer.Compare(baseText, right, options, cancellationToken).Blocks)
            edits.Add(new(block.LeftStart, block.LeftCount, rightLines.GetRange(block.RightStart, block.RightCount), false));
        edits.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Count.CompareTo(b.Count));
        var conflicts = new List<MergeConflict>();
        var sections = new List<MergeSection>();
        var cursor = 0;
        var index = 0;
        var newline = TextLines.NewLine(baseText);
        string Apply(List<Edit> group, int start, int end, bool isLeft)
        {
            var output = new StringBuilder();
            var position = start;
            foreach (var edit in group.Where(edit => edit.Left == isLeft))
            {
                TextLines.AppendSeparated(output, TextLines.Join(baseLines.GetRange(position, edit.Start - position)), newline);
                TextLines.AppendSeparated(output, TextLines.Join(edit.Replacement), newline);
                position = edit.Start + edit.Count;
            }
            TextLines.AppendSeparated(output, TextLines.Join(baseLines.GetRange(position, end - position)), newline);
            return output.ToString();
        }
        void Common(int begin, int end)
        {
            if (begin >= end) return;
            var text = TextLines.Join(baseLines.GetRange(begin, end - begin));
            sections.Add(new(sections.Count, begin + 1, MergeSectionState.Common, text, text, text, text, [MergeSource.Base]));
        }
        while (index < edits.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = edits[index].Start;
            var end = start + edits[index].Count;
            var group = new List<Edit> { edits[index++] };
            while (index < edits.Count && (edits[index].Start < end || edits[index].Start == start))
            {
                var edit = edits[index++];
                group.Add(edit);
                end = Math.Max(end, edit.Start + edit.Count);
            }
            Common(cursor, start);
            var l = Apply(group, start, end, true);
            var r = Apply(group, start, end, false);
            var hasLeft = group.Any(edit => edit.Left);
            var hasRight = group.Any(edit => !edit.Left);
            var original = TextLines.Join(baseLines.GetRange(start, end - start));
            if (!hasLeft) sections.Add(new(sections.Count, start + 1, MergeSectionState.Automatic, r, original, l, r, [MergeSource.Right]));
            else if (!hasRight || l == r) sections.Add(new(sections.Count, start + 1, MergeSectionState.Automatic, l, original, l, r, [MergeSource.Left]));
            else
            {
                conflicts.Add(new(start + 1, end - start, original, l, r));
                sections.Add(new(sections.Count, start + 1, MergeSectionState.Conflict,
                    MergeSession.ConflictText(l, original, r, newline, true), original, l, r, []) { IsWhitespaceOnly = MergeSession.WhitespaceEqual(l, r) });
            }
            cursor = end;
        }
        Common(cursor, baseLines.Count);
        return new(MergeSession.Render(sections, newline).Text, conflicts) { Sections = sections, NewLine = newline };
    }

    private static MergeResult Uncontested(string ancestor, string left, string right, ComparisonOptions? options, CancellationToken token)
    {
        // 合意した枝の原文（改行を含む）をそのまま使い、セクション情報も残す。
        var source = left == right || right == ancestor ? MergeSource.Left : MergeSource.Right;
        var text = source == MergeSource.Left ? left : right;
        var diff = TextDiffer.Compare(ancestor, text, options, token);
        var baseLines = TextLines.Parse(ancestor); var lines = TextLines.Parse(text);
        var sections = new List<MergeSection>(); var baseCursor = 0; var cursor = 0;
        void Add(int baseEnd, int end, MergeSectionState state)
        {
            var original = TextLines.Join(baseLines.GetRange(baseCursor, baseEnd - baseCursor));
            var selected = TextLines.Join(lines.GetRange(cursor, end - cursor));
            if (original.Length != 0 || selected.Length != 0)
                sections.Add(new(sections.Count, baseCursor + 1, state, selected, original,
                    source == MergeSource.Left || left == right ? selected : original,
                    source == MergeSource.Right || left == right ? selected : original, [source]));
            baseCursor = baseEnd; cursor = end;
        }
        foreach (var block in diff.Blocks)
        {
            Add(block.LeftStart, block.RightStart, MergeSectionState.Common);
            Add(block.LeftStart + block.LeftCount, block.RightStart + block.RightCount, MergeSectionState.Automatic);
        }
        Add(baseLines.Count, lines.Count, MergeSectionState.Common);
        return new(text, []) { Sections = sections, NewLine = TextLines.NewLine(text) };
    }
}
