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
}

public static class ThreeWayMerger
{
    private sealed record Edit(int Start, int Count, List<TextLine> Replacement, bool Left);

    public static MergeResult Merge(string baseText, string left, string right,
        ComparisonOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (left == right) return new(left, []);
        if (left == baseText) return new(right, []);
        if (right == baseText) return new(left, []);
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
        var result = new StringBuilder();
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
        void ConflictPart(string text)
        {
            result.Append(text);
            if (text.Length > 0 && !TextLines.HasFinalNewLine(text)) result.Append(newline);
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
            TextLines.AppendSeparated(result, TextLines.Join(baseLines.GetRange(cursor, start - cursor)), newline);
            var l = Apply(group, start, end, true);
            var r = Apply(group, start, end, false);
            var hasLeft = group.Any(edit => edit.Left);
            var hasRight = group.Any(edit => !edit.Left);
            if (!hasLeft) TextLines.AppendSeparated(result, r, newline);
            else if (!hasRight || l == r) TextLines.AppendSeparated(result, l, newline);
            else
            {
                var original = TextLines.Join(baseLines.GetRange(start, end - start));
                conflicts.Add(new(start + 1, end - start, original, l, r));
                if (result.Length > 0 && result[^1] is not ('\r' or '\n')) result.Append(newline);
                result.Append("<<<<<<< LEFT").Append(newline);
                ConflictPart(l);
                result.Append("||||||| BASE").Append(newline);
                ConflictPart(original);
                result.Append("=======").Append(newline);
                ConflictPart(r);
                result.Append(">>>>>>> RIGHT").Append(newline);
            }
            cursor = end;
        }
        TextLines.AppendSeparated(result, TextLines.Join(baseLines.Skip(cursor)), newline);
        return new(result.ToString(), conflicts);
    }
}
