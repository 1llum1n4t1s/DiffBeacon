namespace DiffBeacon.Core;

// 三者のpair比較でも、元の側と文書全体の逐次変換を共有する。
internal sealed class TextFilterContext
{
    private readonly LineTransformPlan? plan;
    private readonly CancellationToken token;
    private readonly TextComparisonSourcePaths? sourcePaths;
    private readonly Dictionary<TablePaneMask, (string Source, string[] Keys)> prepared = [];

    internal TextFilterContext(ComparisonOptions options, CancellationToken token, TextComparisonSourcePaths? sourcePaths = null)
    {
        ArgumentNullException.ThrowIfNull(options.LineTransformRules);
        this.token = token;
        this.sourcePaths = sourcePaths;
        if (options.LineTransformRules.Count != 0)
            plan = LineTransformPlan.Compile(options.LineTransformRules, new LineTransformBudget(), token,
                options.LineTransformTimestampEnvironment, options.LineTransformStringEnvironment, options.LineTransformLogEnvironment, options.LineTransformListEnvironment);
    }

    internal string[]? Transform(string source, IReadOnlyList<TextLine> lines, TablePaneMask pane)
    {
        token.ThrowIfCancellationRequested();
        if (plan is null) return null;
        if (prepared.TryGetValue(pane, out var cached))
        {
            if (cached.Source != source)
                throw new InvalidOperationException("一つの比較で同じ側の原文が変更されました。");
            return cached.Keys;
        }
        var keys = plan.Transform(lines, pane, false, cancellationToken: token, originalContent: source,
            sourcePath: sourcePaths?.ForPane(pane),
            includeFinalEofCandidate: sourcePaths?.IncludeFinalEofCandidateForPane(pane) ?? true);
        prepared.Add(pane, (source, keys));
        return keys;
    }
}
