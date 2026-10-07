using System.Text;

namespace DiffBeacon.Core;

public static class TextDiffer
{
    public static DiffResult Compare(string left, string right, ComparisonOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        var a = TextLines.Parse(left, cancellationToken);
        var b = TextLines.Parse(right, cancellationToken);
        var preprocessor = new TextPreprocessor(options, cancellationToken);
        var ignoredPattern = options.CreateIgnoredLineRegex();
        var preparedA = Prepare(a, options, preprocessor, ignoredPattern, cancellationToken);
        var preparedB = Prepare(b, options, preprocessor, ignoredPattern, cancellationToken);
        var ignoredA = preparedA.Ignored;
        var ignoredB = preparedB.Ignored;
        var activeA = preparedA.Active;
        var activeB = preparedB.Active;
        var keysA = preparedA.Keys;
        var keysB = preparedB.Keys;
        var lineMatches = GnuLineMatcher.Match(keysA, keysB, a, b, activeA, activeB, options, cancellationToken);
        var matches = lineMatches.Pairs;
        var rows = new List<DiffRow>();
        var nextA = 0;
        var nextB = 0;
        var inlineBudget = Math.Clamp(options.MaxFallbackComparisons, 0, 8_000_000);
        var inlineInitialBudget = inlineBudget;
        var inlineFallbackCount = 0;
        void AddRow(int? ai, int? bi, DiffKind kind)
        {
            var row = new DiffRow(ai + 1, bi + 1, ai.HasValue ? a[ai.Value].Content : null,
                bi.HasValue ? b[bi.Value].Content : null, kind);
            rows.Add(row);
        }
        void AddSegment(int stopA, int stopB)
        {
            while (nextA < stopA || nextB < stopB)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var skipA = nextA < stopA && ignoredA[nextA];
                var skipB = nextB < stopB && ignoredB[nextB];
                if (skipA || skipB)
                {
                    AddRow(skipA ? nextA++ : null, skipB ? nextB++ : null, DiffKind.Equal);
                    continue;
                }
                var ai = nextA < stopA ? nextA++ : (int?)null;
                var bi = nextB < stopB ? nextB++ : (int?)null;
                AddRow(ai, bi, ai.HasValue ? bi.HasValue ? DiffKind.Modified : DiffKind.Deleted : DiffKind.Added);
            }
        }
        foreach (var (ai, bi) in matches)
        {
            var rawA = activeA[ai];
            var rawB = activeB[bi];
            AddSegment(rawA, rawB);
            AddRow(rawA, rawB, DiffKind.Equal);
            nextA = rawA + 1;
            nextB = rawB + 1;
        }
        AddSegment(a.Count, b.Count);
        var blocks = new List<DiffBlock>();
        var consumedA = 0;
        var consumedB = 0;
        for (var index = 0; index < rows.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows[index].Kind == DiffKind.Equal)
            {
                if (rows[index].LeftLineNumber.HasValue) consumedA++;
                if (rows[index++].RightLineNumber.HasValue) consumedB++;
                continue;
            }
            var start = index;
            var beginA = consumedA;
            var beginB = consumedB;
            while (index < rows.Count && rows[index].Kind != DiffKind.Equal)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rows[index].LeftLineNumber.HasValue) consumedA++;
                if (rows[index++].RightLineNumber.HasValue) consumedB++;
            }
            blocks.Add(new(blocks.Count, start, index - start, beginA, consumedA - beginA,
                beginB, consumedB - beginB));
        }
        var wordOptions = new WordDiffOptions
        {
            CharacterLevel = options.InlineCharacterLevel,
            MatchCase = !options.IgnoreCase, IgnoreNumbers = options.IgnoreNumbers,
            Whitespace = (options.IgnoreWhitespace ? WhitespaceMode.IgnoreAll : options.Whitespace) switch
            {
                WhitespaceMode.IgnoreAll => WordWhitespaceMode.IgnoreAll,
                WhitespaceMode.IgnoreChanges => WordWhitespaceMode.IgnoreChanges,
                _ => WordWhitespaceMode.CompareAll
            },
            Eol = options.CompareLineEndings ? WordEolMode.Strict : WordEolMode.Ignore
        };
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leftBlock = JoinBlock(block, true);
            var rightBlock = JoinBlock(block, false);
            var words = WordDiffer.Compare(leftBlock.Text, rightBlock.Text, wordOptions,
                inlineBudget, cancellationToken);
            inlineBudget -= words.WorkUsed;
            if (words.Fallback)
                for (var row = block.RowStart; row < block.RowStart + block.RowCount; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (rows[row].Kind == DiffKind.Modified) inlineFallbackCount++;
                }
            Project(leftBlock.Lines, words, true);
            Project(rightBlock.Lines, words, false);
        }

        // 各側の実在する行を原文終端ごと連結する。ghost は文字も offset も持たない。
        (string Text, List<(int Row, int Start, int Length)> Lines) JoinBlock(DiffBlock block, bool leftSide)
        {
            var text = new StringBuilder();
            var lines = new List<(int Row, int Start, int Length)>();
            for (var row = block.RowStart; row < block.RowStart + block.RowCount; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceRow = leftSide ? rows[row].LeftLineNumber : rows[row].RightLineNumber;
                if (sourceRow is null) continue;
                var line = (leftSide ? a : b)[sourceRow.Value - 1];
                lines.Add((row, text.Length, line.Content.Length));
                Append(line.Content);
                Append(line.Ending);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return (text.ToString(), lines);

            void Append(string value)
            {
                for (var start = 0; start < value.Length; start += 4096)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    text.Append(value, start, Math.Min(4096, value.Length - start));
                }
            }
        }

        // 単調な原文区間を前向きに走査し、改行を除く Content との交差だけを投影する。
        void Project(List<(int Row, int Start, int Length)> lines, WordDiffResult words, bool leftSide)
        {
            var ranges = new List<WordRange>();
            foreach (var difference in words.Differences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var range = leftSide ? difference.Left : difference.Right;
                if (range.Length > 0) ranges.Add(range);
            }
            var rangeIndex = 0;
            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (rangeIndex < ranges.Count && ranges[rangeIndex].Start + ranges[rangeIndex].Length <= line.Start)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rangeIndex++;
                }
                if (rows[line.Row].Kind != DiffKind.Modified) continue;
                var spans = new List<InlineSpan>();
                var end = line.Start + line.Length;
                while (rangeIndex < ranges.Count && ranges[rangeIndex].Start < end)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var range = ranges[rangeIndex];
                    var rangeEnd = range.Start + range.Length;
                    var start = Math.Max(line.Start, range.Start);
                    var stop = Math.Min(end, rangeEnd);
                    if (stop > start) spans.Add(new(start - line.Start, stop - start));
                    if (rangeEnd > end) break;
                    rangeIndex++;
                }
                rows[line.Row] = leftSide ? rows[line.Row] with { LeftSpans = spans.AsReadOnly() }
                    : rows[line.Row] with { RightSpans = spans.AsReadOnly() };
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(left, right, rows, blocks)
        {
            InlineWorkUsed = inlineInitialBudget - inlineBudget, InlineFallbackCount = inlineFallbackCount,
            LineWorkUsed = lineMatches.WorkUsed, LineFallback = lineMatches.Fallback,
            LineFallbackReason = lineMatches.FallbackReason
        };
    }

    // 通常比較とfolder Fullで、構文状態・無視行・原文座標・終端キーを共用する。
    internal static PreparedText Prepare(IReadOnlyList<TextLine> lines, ComparisonOptions options,
        TextPreprocessor preprocessor, System.Text.RegularExpressions.Regex? ignoredPattern,
        CancellationToken cancellationToken)
    {
        var filtered = preprocessor.Process(lines);
        var ignored = new bool[lines.Count];
        var active = new List<int>();
        var keys = new List<GnuLineKey>();
        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[index];
            if (ignoredPattern is not null) TextPreprocessor.CheckRegexInput(line.Content);
            ignored[index] = filtered.CommentOnly[index] ||
                (options.IgnoreBlankLines && string.IsNullOrWhiteSpace(line.Content)) ||
                (ignoredPattern?.IsMatch(line.Content) ?? false);
            if (ignored[index]) continue;
            active.Add(index);
            keys.Add(new(filtered.Keys[index], options.CompareLineEndings ? line.Ending : "",
                !options.IgnoreFinalNewLine && index == lines.Count - 1 && line.Ending.Length == 0));
        }
        return new(lines, ignored, active.ToArray(), keys.ToArray());
    }
}
