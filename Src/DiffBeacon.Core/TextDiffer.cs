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
        var filteredA = preprocessor.Process(a);
        var filteredB = preprocessor.Process(b);
        var ignoredPattern = options.CreateIgnoredLineRegex();
        bool[] Ignored(IReadOnlyList<TextLine> lines, bool[] commentOnly)
        {
            var result = new bool[lines.Count];
            for (var index = 0; index < lines.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = lines[index].Content;
                if (ignoredPattern is not null) TextPreprocessor.CheckRegexInput(content);
                result[index] = commentOnly[index] || (options.IgnoreBlankLines && string.IsNullOrWhiteSpace(content)) ||
                    (ignoredPattern?.IsMatch(content) ?? false);
            }
            return result;
        }
        var ignoredA = Ignored(a, filteredA.CommentOnly);
        var ignoredB = Ignored(b, filteredB.CommentOnly);
        var activeA = Enumerable.Range(0, a.Count).Where(index => !ignoredA[index]).ToArray();
        var activeB = Enumerable.Range(0, b.Count).Where(index => !ignoredB[index]).ToArray();
        string Key(string content, TextLine line, bool last) => content +
            (options.CompareLineEndings ? "\0ending:" + line.Ending : "") +
            (!options.IgnoreFinalNewLine && last && line.Ending.Length == 0 ? "\0no-final-newline" : "");
        var keysA = activeA.Select(index => Key(filteredA.Keys[index], a[index], index == a.Count - 1)).ToArray();
        var keysB = activeB.Select(index => Key(filteredB.Keys[index], b[index], index == b.Count - 1)).ToArray();
        var matches = Match(keysA, keysB, Math.Max(0, options.MaxFallbackComparisons), cancellationToken);
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
            InlineWorkUsed = inlineInitialBudget - inlineBudget, InlineFallbackCount = inlineFallbackCount
        };
    }

    // 一意な行をアンカーにし、アンカーのない区間だけ線形メモリの LCS で比較する。
    private static List<(int A, int B)> Match(string[] a, string[] b, int budget, CancellationToken token)
    {
        var matches = new List<(int, int)>();
        void Solve(int a0, int a1, int b0, int b1, int depth)
        {
            token.ThrowIfCancellationRequested();
            while (a0 < a1 && b0 < b1 && a[a0] == b[b0]) matches.Add((a0++, b0++));
            var tails = new List<(int, int)>();
            while (a0 < a1 && b0 < b1 && a[a1 - 1] == b[b1 - 1]) tails.Add((--a1, --b1));
            if (a0 < a1 && b0 < b1)
            {
                var seenA = new Dictionary<string, int>(StringComparer.Ordinal);
                var seenB = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = a0; i < a1; i++) seenA[a[i]] = seenA.ContainsKey(a[i]) ? -1 : i;
                for (var i = b0; i < b1; i++) seenB[b[i]] = seenB.ContainsKey(b[i]) ? -1 : i;
                var candidates = new List<(int A, int B)>();
                for (var i = a0; i < a1; i++)
                    if (seenA[a[i]] == i && seenB.TryGetValue(a[i], out var j) && j >= 0) candidates.Add((i, j));
                var anchors = Increasing(candidates);
                if (anchors.Count > 0 && depth < 128)
                {
                    foreach (var (i, j) in anchors)
                    {
                        Solve(a0, i, b0, j, depth + 1);
                        matches.Add((i, j));
                        a0 = i + 1;
                        b0 = j + 1;
                    }
                    Solve(a0, a1, b0, b1, depth + 1);
                }
                else
                {
                    var cost = (long)(a1 - a0) * (b1 - b0);
                    if (cost <= budget)
                    {
                        budget -= (int)cost;
                        Hirschberg(a0, a1, b0, b1);
                    }
                    // 上限超過時は区間を変更扱いにする。結果の再構成可能性は失わない。
                }
            }
            for (var i = tails.Count - 1; i >= 0; i--) matches.Add(tails[i]);
        }
        int[] Lengths(int a0, int a1, int b0, int b1, bool reverse)
        {
            var width = b1 - b0;
            var row = new int[width + 1];
            for (var i = 0; i < a1 - a0; i++)
            {
                token.ThrowIfCancellationRequested();
                var diagonal = 0;
                for (var j = 1; j <= width; j++)
                {
                    var previous = row[j];
                    row[j] = a[reverse ? a1 - i - 1 : a0 + i] == b[reverse ? b1 - j : b0 + j - 1]
                        ? diagonal + 1 : Math.Max(row[j], row[j - 1]);
                    diagonal = previous;
                }
            }
            return row;
        }
        void Hirschberg(int a0, int a1, int b0, int b1)
        {
            token.ThrowIfCancellationRequested();
            if (a0 == a1 || b0 == b1) return;
            if (a1 - a0 == 1)
            {
                for (var j = b0; j < b1; j++) if (a[a0] == b[j]) { matches.Add((a0, j)); break; }
                return;
            }
            var middle = a0 + (a1 - a0) / 2;
            var forward = Lengths(a0, middle, b0, b1, false);
            var backward = Lengths(middle, a1, b0, b1, true);
            var split = 0;
            for (var j = 1; j <= b1 - b0; j++)
                if (forward[j] + backward[b1 - b0 - j] > forward[split] + backward[b1 - b0 - split]) split = j;
            Hirschberg(a0, middle, b0, b0 + split);
            Hirschberg(middle, a1, b0 + split, b1);
        }
        Solve(0, a.Length, 0, b.Length, 0);
        return matches;
    }

    private static List<(int A, int B)> Increasing(List<(int A, int B)> candidates)
    {
        if (candidates.Count == 0) return [];
        var tails = new int[candidates.Count];
        var previous = new int[candidates.Count];
        var length = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var low = 0;
            var high = length;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (candidates[tails[middle]].B < candidates[i].B) low = middle + 1;
                else high = middle;
            }
            previous[i] = low > 0 ? tails[low - 1] : -1;
            tails[low] = i;
            if (low == length) length++;
        }
        var result = new List<(int, int)>(length);
        for (var i = tails[length - 1]; i >= 0; i = previous[i]) result.Add(candidates[i]);
        result.Reverse();
        return result;
    }
}
