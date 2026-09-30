using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DiffBeacon.Core;

public sealed record PatchApplyResult(bool Success, string Text, string? Error);

public static class UnifiedPatch
{
    public static string Create(string leftText, string rightText, string leftPath = "a/file",
        string rightPath = "b/file", int contextLines = 3)
    {
        if (contextLines < 0) throw new ArgumentOutOfRangeException(nameof(contextLines));
        if (leftPath.IndexOfAny(['\r', '\n']) >= 0 || rightPath.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("パッチのパスには改行を指定できません。");
        var diff = TextDiffer.Compare(leftText, rightText, new ComparisonOptions { CompareLineEndings = true });
        if (!diff.HasDifferences) return "";
        var a = TextLines.Parse(leftText);
        var b = TextLines.Parse(rightText);
        var rows = diff.Rows;
        var output = new StringBuilder().Append("--- ").Append(leftPath).Append('\n')
            .Append("+++ ").Append(rightPath).Append('\n');
        var ranges = new List<(int Start, int End)>();
        foreach (var block in diff.Blocks)
        {
            var start = Math.Max(0, block.RowStart - contextLines);
            var end = Math.Min(rows.Count, block.RowStart + block.RowCount + contextLines);
            if (ranges.Count > 0 && start <= ranges[^1].End) ranges[^1] = (ranges[^1].Start, Math.Max(end, ranges[^1].End));
            else ranges.Add((start, end));
        }
        void Emit(char prefix, TextLine line)
        {
            output.Append(prefix).Append(line.Content).Append(line.Ending.Length == 0 ? "\n" : line.Ending);
            if (line.Ending.Length == 0) output.Append("\\ No newline at end of file\n");
        }
        foreach (var (start, end) in ranges)
        {
            var leftBefore = rows.Take(start).Count(row => row.LeftLineNumber.HasValue);
            var rightBefore = rows.Take(start).Count(row => row.RightLineNumber.HasValue);
            var leftCount = rows.Skip(start).Take(end - start).Count(row => row.LeftLineNumber.HasValue);
            var rightCount = rows.Skip(start).Take(end - start).Count(row => row.RightLineNumber.HasValue);
            output.Append("@@ -").Append(leftCount == 0 ? leftBefore : leftBefore + 1).Append(',').Append(leftCount)
                .Append(" +").Append(rightCount == 0 ? rightBefore : rightBefore + 1).Append(',').Append(rightCount).Append(" @@\n");
            for (var index = start; index < end;)
            {
                if (rows[index].Kind == DiffKind.Equal)
                {
                    Emit(' ', a[rows[index].LeftLineNumber!.Value - 1]);
                    index++;
                    continue;
                }
                var stop = index;
                while (stop < end && rows[stop].Kind != DiffKind.Equal) stop++;
                for (var i = index; i < stop; i++) if (rows[i].LeftLineNumber is int line) Emit('-', a[line - 1]);
                for (var i = index; i < stop; i++) if (rows[i].RightLineNumber is int line) Emit('+', b[line - 1]);
                index = stop;
            }
        }
        return output.ToString();
    }

    public static PatchApplyResult TryApply(string text, string patch)
    {
        try { return new(true, Apply(text, patch), null); }
        catch (Exception error) when (error is FormatException or InvalidOperationException or OverflowException)
        { return new(false, text, error.Message); }
    }

    public static string Apply(string text, string patch)
    {
        if (patch.Length == 0) return text;
        var original = TextLines.Parse(text);
        var lines = TextLines.Parse(patch);
        var output = new List<TextLine>();
        var cursor = 0;
        var index = 0;
        var hunks = 0;
        var fileHeaders = 0;
        var header = new Regex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(?:.*)$", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
        while (index < lines.Count)
        {
            var line = lines[index++].Content;
            if (!line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    if (++fileHeaders > 1 || hunks > 0) throw new FormatException("複数ファイルのパッチはファイルごとに適用してください。");
                    if (index >= lines.Count || !lines[index++].Content.StartsWith("+++ ", StringComparison.Ordinal))
                        throw new FormatException("パッチの新ファイルヘッダーがありません。");
                    continue;
                }
                if (hunks > 0 || line.StartsWith("+++", StringComparison.Ordinal)) throw new FormatException("パッチに予期しない行があります。");
                continue;
            }
            var match = header.Match(line);
            if (!match.Success) throw new FormatException("パッチの hunk ヘッダーが不正です。");
            int Number(int group, int fallback) => match.Groups[group].Success
                ? int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) : fallback;
            var oldStart = Number(1, 0);
            var oldCount = Number(2, 1);
            var newStart = Number(3, 0);
            var newCount = Number(4, 1);
            if (oldCount == 0 && newCount == 0) throw new FormatException("空の hunk は適用できません。");
            var target = oldCount == 0 ? oldStart : oldStart - 1;
            var newTarget = newCount == 0 ? newStart : newStart - 1;
            if (target < cursor || target > original.Count) throw new InvalidOperationException("パッチの適用位置が元のテキストと一致しません。");
            output.AddRange(original.GetRange(cursor, target - cursor));
            cursor = target;
            if (output.Count != newTarget) throw new FormatException("パッチの新しい行位置が不正です。");
            var removed = 0;
            var added = 0;
            while (index < lines.Count && !lines[index].Content.StartsWith("@@", StringComparison.Ordinal))
            {
                var payload = lines[index++];
                if (payload.Content.Length == 0 || payload.Content[0] is not (' ' or '+' or '-'))
                    throw new FormatException("パッチのデータ行が不正です。");
                var prefix = payload.Content[0];
                var ending = payload.Ending;
                if (index < lines.Count && lines[index].Content == "\\ No newline at end of file") { ending = ""; index++; }
                var content = payload.Content[1..];
                if (prefix is ' ' or '-')
                {
                    if (cursor >= original.Count || original[cursor].Content != content ||
                        (original[cursor].Ending.Length == 0) != (ending.Length == 0))
                        throw new InvalidOperationException($"パッチの文脈が元の {cursor + 1} 行目と一致しません。");
                    if (prefix == ' ') output.Add(original[cursor]);
                    cursor++;
                    removed++;
                }
                if (prefix is ' ' or '+')
                {
                    if (prefix == '+') output.Add(new(content, ending));
                    added++;
                }
                if (removed > oldCount || added > newCount) throw new FormatException("パッチの行数が hunk ヘッダーを超えています。");
                if (removed == oldCount && added == newCount) break;
            }
            if (removed != oldCount || added != newCount) throw new FormatException("パッチの行数が hunk ヘッダーと一致しません。");
            hunks++;
        }
        if (hunks == 0) throw new FormatException("適用する hunk がありません。");
        output.AddRange(original.Skip(cursor));
        for (var position = 0; position < output.Count - 1; position++)
            if (output[position].Ending.Length == 0) throw new FormatException("最終行以外に改行なしマーカーがあります。");
        return TextLines.Join(output);
    }
}
