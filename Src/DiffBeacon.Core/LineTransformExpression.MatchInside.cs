namespace DiffBeacon.Core;

internal sealed partial class LineTransformExpression
{
    private static object? EvaluateMatchInside(Node begin, Node end, Context c)
    {
        if (c.Provider is null) return null;
        var cached = GetOrCreateInsideRanges(begin, end, c);
        foreach (var range in cached.Ranges)
        {
            c.Budget.Charge(1, c.Token);
            if (range.Start <= c.LineIndex && c.LineIndex <= range.End) return true;
        }
        return false;
    }

    private static MatchRangeCache GetOrCreateInsideRanges(Node begin, Node end, Context c)
    {
        var state = c.State ?? throw new InvalidOperationException("行matchの評価状態がありません。");
        var provider = c.Provider ?? throw new InvalidOperationException("行matchにはproviderが必要です。");
        c.Budget.Charge(1, c.Token);
        if (state.TryGetInsideRanges(begin, end, out var cached))
        {
            var result = (MatchRangeCache)cached!;
            provider.RestoreMultilineRead(result.ReadMultilineKey);
            return result;
        }

        var priorRead = provider.ReadMultilineKey;
        provider.BeginEvaluation();
        try
        {
            var ranges = new List<MatchRange>();
            var start = -1;
            for (var index = 0; index < provider.LineCount; index++)
            {
                c.Budget.Charge(1, c.Token);
                var lineContext = c with { LineIndex = index };
                // 状態にかかわらずbegin、endの順に両方を評価する。内側のbeginは入れ子にしない。
                var isBegin = BooleanValue(begin.Evaluate(lineContext), lineContext) is true;
                var isEnd = BooleanValue(end.Evaluate(lineContext), lineContext) is true;
                if (start != -1 && isEnd)
                {
                    c.Budget.Charge(1, c.Token);
                    ranges.Add(new(start, index));
                    start = isBegin ? index : -1;
                }
                else if (start == -1 && isBegin) start = index;
            }
            if (start != -1)
            {
                c.Budget.Charge(1, c.Token);
                ranges.Add(new(start, provider.LineCount - 1));
            }

            // 失敗・取消・予算超過時は部分的な範囲を公開しない。
            c.Budget.Charge(ranges.Count + 1L, c.Token);
            var result = new MatchRangeCache(ranges.ToArray(), provider.ReadMultilineKey);
            state.PublishInsideRanges(begin, end, result);
            return result;
        }
        finally { provider.RestoreMultilineRead(priorRead); }
    }
}
