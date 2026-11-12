namespace DiffBeacon.Core;

/// <summary>原文を変更せず、比較キーへ順番に適用する式と対象ペイン。</summary>
public sealed record LineTransformRule(string Expression, TablePaneMask Panes)
{
    public const int MaximumRules = 256;
    public const int MaximumExpressionLength = 65_536;
    public const int MaximumTotalExpressionLength = 1_048_576;

    public static void ValidateRules(IReadOnlyList<LineTransformRule>? rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count > MaximumRules) throw new ArgumentException("行変換は256件まで指定できます。", nameof(rules));
        long total = 0;
        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rule is null || string.IsNullOrWhiteSpace(rule.Expression)
                || rule.Expression.Length > MaximumExpressionLength
                || rule.Panes == 0 || (rule.Panes & ~TablePaneMask.All) != 0)
                throw new ArgumentException("行変換の式または対象ペインが不正です。", nameof(rules));
            total += rule.Expression.Length;
            if (total > MaximumTotalExpressionLength)
                throw new ArgumentException("行変換の式の合計文字数が上限を超えています。", nameof(rules));
        }
    }
}

/// <summary>一つの比較の解析・全ペイン・全変換グループで共有する累積予算。</summary>
public sealed class LineTransformBudget
{
    private readonly object gate = new();
    private long workUsed;
    private long outputCharacters;
    private long arraySlots;
    internal const long MaximumArraySlots = 1_048_576;
    public long MaximumWork { get; }
    public long MaximumOutputCharacters { get; }
    public long WorkUsed { get { lock (gate) return workUsed; } }
    public long OutputCharacters { get { lock (gate) return outputCharacters; } }

    /// <summary>ホスト読込みも式全体の共有予算から、割当て前に予約する。</summary>
    public void ChargeListRead(long bytes, CancellationToken cancellationToken = default)
        => Charge(bytes, cancellationToken, bytes);

    public LineTransformBudget(long maximumWork = 128L * 1024 * 1024,
        long maximumOutputCharacters = 64L * 1024 * 1024)
    {
        if (maximumWork < 1) throw new ArgumentOutOfRangeException(nameof(maximumWork));
        if (maximumOutputCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maximumOutputCharacters));
        MaximumWork = maximumWork;
        MaximumOutputCharacters = maximumOutputCharacters;
    }

    internal void ChargeArray(int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            var work = count + 1L;
            if (count < 0 || count > MaximumArraySlots - arraySlots || work > MaximumWork - workUsed)
                throw new InvalidOperationException("行変換の共有処理量または配列生成量が上限を超えています。");
            arraySlots += count;
            workUsed += work;
        }
    }

    internal void Charge(long work, CancellationToken token, long output = 0)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (work < 0 || output < 0 || work > MaximumWork - workUsed
                || output > MaximumOutputCharacters - outputCharacters)
                throw new InvalidOperationException("行変換の共有処理量または生成文字数が上限を超えています。");
            workUsed += work;
            outputCharacters += output;
        }
    }
}

/// <summary>解析済みの不変な式。作業用の行は文書ごとに分離する。</summary>
public sealed class LineTransformPlan
{
    private readonly Entry[] entries;
    private readonly LineTransformBudget budget;
    private sealed record Entry(TablePaneMask Panes, LineTransformExpression Expression);
    private LineTransformPlan(Entry[] entries, LineTransformBudget budget) { this.entries = entries; this.budget = budget; }

    public static IReadOnlyList<LineTransformListDependency> InspectListDependencies(IReadOnlyList<LineTransformRule> rules,
        CancellationToken cancellationToken = default, ILineTransformTimestampEnvironment? timestampEnvironment = null,
        ILineTransformStringEnvironment? stringEnvironment = null)
    {
        LineTransformRule.ValidateRules(rules, cancellationToken);
        var budget = new LineTransformBudget();
        return rules.SelectMany(rule => LineTransformExpression.InspectListDependencies(rule.Expression, budget,
            cancellationToken, timestampEnvironment, stringEnvironment)).Distinct().ToArray();
    }

    public static LineTransformPlan Compile(IReadOnlyList<LineTransformRule> rules,
        LineTransformBudget budget, CancellationToken cancellationToken = default,
        ILineTransformTimestampEnvironment? timestampEnvironment = null,
        ILineTransformStringEnvironment? stringEnvironment = null,
        ILineTransformLogEnvironment? logEnvironment = null,
        ILineTransformListEnvironment? listEnvironment = null)
    {
        var token = cancellationToken;
        ArgumentNullException.ThrowIfNull(budget);
        LineTransformRule.ValidateRules(rules, token);
        budget.Charge(rules.Count, token);
        var entries = new Entry[rules.Count];
        for (var index = 0; index < entries.Length; index++)
            entries[index] = new(rules[index].Panes, LineTransformExpression.Compile(rules[index].Expression, budget, token,
                timestampEnvironment, stringEnvironment, logEnvironment, listEnvironment));
        token.ThrowIfCancellationRequested();
        return new(entries, budget);
    }

    internal string[] Transform(IReadOnlyList<TextLine> sourceLines, TablePaneMask pane,
        bool tableMode, DelimitedSyntax? syntax = null, CancellationToken cancellationToken = default, string? originalContent = null,
        string? sourcePath = null, bool includeFinalEofCandidate = true)
    {
        ArgumentNullException.ThrowIfNull(sourceLines);
        return TransformCore(sourceLines.Count, index => sourceLines[index].Content, pane, tableMode, syntax, cancellationToken,
            () => originalContent ?? JoinContent(sourceLines.Count, index => sourceLines[index].Content, "", cancellationToken,
                index => sourceLines[index].Ending), sourcePath, includeFinalEofCandidate);
    }

    public string[] Transform(IReadOnlyList<string> sourceLines, TablePaneMask pane,
        bool tableMode, DelimitedSyntax? syntax = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceLines);
        return TransformCore(sourceLines.Count, index => sourceLines[index], pane, tableMode, syntax, cancellationToken,
            () => JoinContent(sourceLines.Count, index => sourceLines[index], "\n", cancellationToken));
    }

    public string[] TransformWithSourcePaths(IReadOnlyList<string> sourceLines, TablePaneMask pane,
        bool tableMode, TextComparisonSourcePaths sourcePaths, DelimitedSyntax? syntax = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceLines);
        ArgumentNullException.ThrowIfNull(sourcePaths);
        return TransformCore(sourceLines.Count, index => sourceLines[index], pane, tableMode, syntax, cancellationToken,
            () => JoinContent(sourceLines.Count, index => sourceLines[index], "\n", cancellationToken), sourcePaths.ForPane(pane),
            sourcePaths.IncludeFinalEofCandidateForPane(pane));
    }

    private string JoinContent(int count, Func<int, string> getLine, string separator, CancellationToken token,
        Func<int, string>? getEnding = null)
    {
        long length = Math.Max(0, count - 1) * (long)separator.Length;
        for (var index = 0; index < count; index++)
        {
            budget.Charge(1, token);
            length += getLine(index).Length + (long)(getEnding?.Invoke(index).Length ?? 0);
        }
        budget.Charge(length, token, length);
        if (length > int.MaxValue) throw new InvalidOperationException("行変換の原文内容が上限を超えています。");
        var result = new System.Text.StringBuilder((int)length);
        for (var index = 0; index < count; index++)
        {
            budget.Charge(1, token);
            if (index != 0) result.Append(separator);
            result.Append(getLine(index));
            if (getEnding is not null) result.Append(getEnding(index));
        }
        return result.ToString();
    }

    private string[] TransformCore(int count, Func<int, string> getLine, TablePaneMask pane,
        bool tableMode, DelimitedSyntax? syntax, CancellationToken token, Func<string> originalContent,
        string? sourcePath = null, bool includeFinalEofCandidate = true)
    {
        if (pane is not (TablePaneMask.Left or TablePaneMask.Base or TablePaneMask.Right))
            throw new ArgumentException("変換する文書のペインを一つ指定してください。", nameof(pane));
        var effectiveSyntax = syntax ?? (tableMode ? new DelimitedSyntax() : new DelimitedSyntax('\0'));
        // 旧providerのUTF-8 byteとwchar設定の照合はASCIIだけを移植・検証している。
        // 通常の表parserと静的な列指定の対応範囲へ、この制限を広げない。
        if ((effectiveSyntax.Delimiter > 0x7f || effectiveSyntax.Quote > 0x7f)
            && entries.Any(entry => (entry.Panes & pane) != 0))
            throw new ArgumentException("行変換の非ASCII区切り文字・引用符には未対応です。", nameof(syntax));
        budget.Charge(count, token);
        var lines = new string[count];
        for (var index = 0; index < count; index++)
        {
            var line = getLine(index) ?? throw new ArgumentException("行の内容がnullです。", nameof(getLine));
            budget.Charge(line.Length, token, line.Length);
            lines[index] = line;
        }
        var content = entries.Any(entry => (entry.Panes & pane) != 0 && entry.Expression.UsesContent)
            ? new LineTransformContent(originalContent(), budget, token, sourcePath, includeFinalEofCandidate) : null;
        var provider = new LineTransformProvider(lines, tableMode, effectiveSyntax, budget, token, content);
        // 原典のsharedContextは一回の変換・各ruleに一つ。文書や再実行へ共有しない。
        LineTransformExpression.EvaluationState?[]? evaluationStates = null;
        if (count > 0 && entries.Any(entry => (entry.Panes & pane) != 0
            && (entry.Expression.UsesAggregates || entry.Expression.UsesMatchRanges)))
        {
            budget.Charge(entries.Length + 1L, token);
            evaluationStates = new LineTransformExpression.EvaluationState?[entries.Length];
            for (var index = 0; index < evaluationStates.Length; index++)
                if ((entries[index].Panes & pane) != 0
                    && (entries[index].Expression.UsesAggregates || entries[index].Expression.UsesMatchRanges))
                {
                    budget.Charge(1, token);
                    evaluationStates[index] = new();
                }
        }
        // 同じmaskの連続式は行外・式内。mask境界では前グループの全行を確定する。
        for (var begin = 0; begin < entries.Length;)
        {
            var end = begin + 1;
            while (end < entries.Length && entries[end].Panes == entries[begin].Panes) end++;
            if ((entries[begin].Panes & pane) != 0)
                for (var lineIndex = 0; lineIndex < count; lineIndex++)
                    for (var expressionIndex = begin; expressionIndex < end; expressionIndex++)
                    {
                        budget.Charge(1, token);
                        provider.BeginEvaluation();
                        var result = entries[expressionIndex].Expression.Evaluate(provider, lineIndex, evaluationStates?[expressionIndex]);
                        // Sublinesを含む式と、その複数行キーを受け取った後続式だけに許可する。
                        // CR/LFを再分割せず、元行数・原文・EOLは保持する。
                        var allowsMultiline = entries[expressionIndex].Expression.UsesSublines || provider.ReadMultilineKey;
                        var containsMultiline = false;
                        for (var position = 0; position < result.Length; position++)
                        {
                            if ((position & 255) == 0) budget.Charge(Math.Min(256, result.Length - position), token);
                            if (result[position] is not ('\r' or '\n')) continue;
                            if (!allowsMultiline) throw new ArgumentException("行変換が生成したCR/LFには対応していません。");
                            containsMultiline = true;
                        }
                        budget.Charge(result.Length, token, result.Length);
                        provider.SetLine(lineIndex, result, containsMultiline);
                    }
            begin = end;
        }
        token.ThrowIfCancellationRequested();
        return lines;
    }
}
