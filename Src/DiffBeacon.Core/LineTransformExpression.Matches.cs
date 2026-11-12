namespace DiffBeacon.Core;

internal sealed partial class LineTransformExpression
{
    private readonly record struct MatchRange(int Start, int End);

    private sealed record MatchRangeCache(MatchRange[] Ranges, bool ReadMultilineKey);

    private static object? EvaluateMatchBlock(string name, Node predicate, Context c)
    {
        if (c.Provider is null) return null;
        var cached = GetOrCreateMatchRanges(predicate, c);
        if (name == "matchblockcount") return (long)cached.Ranges.Length;

        for (var index = 0; index < cached.Ranges.Length; index++)
        {
            c.Budget.Charge(1, c.Token);
            var range = cached.Ranges[index];
            if (range.Start <= c.LineIndex && c.LineIndex <= range.End)
                return name switch
                {
                    "matchblocknumber" => (long)index + 1,
                    "matchblocksize" => (long)(range.End - range.Start + 1),
                    "matchblockoffset" => (long)(c.LineIndex - range.Start + 1),
                    _ => throw new ArgumentException("未対応のmatch block関数です。")
                };
        }
        return null;
    }

    private static object? EvaluateMatchContext(Node predicate, Node beforeExpression, Node afterExpression, Context c)
    {
        if (c.Provider is null) return null;
        var beforeValue = beforeExpression.Evaluate(c);
        var afterValue = afterExpression.Evaluate(c);
        if (beforeValue is not long before || afterValue is not long after) return null;

        var cached = GetOrCreateMatchRanges(predicate, c);
        var distances = GetDistancesToMatchRanges(c.LineIndex, cached.Ranges, c);
        // 原典は前の区間との距離をafter引数、次の区間との距離をbefore引数と比較する。
        return distances.Before >= 0 && distances.Before <= after
            || distances.After >= 0 && distances.After <= before;
    }

    private static object? EvaluateMatchMetric(string name, Node predicate, Context c)
    {
        if (c.Provider is null) return null;
        var cached = GetOrCreateMatchRanges(predicate, c);
        if (name == "matchcount")
        {
            long count = 0;
            foreach (var range in cached.Ranges)
            {
                c.Budget.Charge(1, c.Token);
                count += (long)range.End - range.Start + 1;
            }
            return count;
        }
        if (name == "matchnumber")
        {
            long number = 0;
            foreach (var range in cached.Ranges)
            {
                c.Budget.Charge(1, c.Token);
                if (c.LineIndex < range.Start) return null;
                var length = (long)range.End - range.Start + 1;
                if (c.LineIndex <= range.End) return number + c.LineIndex - range.Start + 1;
                number += length;
            }
            return null;
        }

        var distances = GetDistancesToMatchRanges(c.LineIndex, cached.Ranges, c);
        if (name == "matchdistancebefore") return distances.Before < 0 ? null : (long)distances.Before;
        if (name == "matchdistanceafter") return distances.After < 0 ? null : (long)distances.After;
        if (name == "matchdistance")
        {
            if (distances.Before < 0) return distances.After < 0 ? null : (long)distances.After;
            if (distances.After < 0) return (long)distances.Before;
            return (long)Math.Min(distances.Before, distances.After);
        }
        throw new ArgumentException("未対応のmatch metric関数です。");
    }

    private static MatchRangeCache GetOrCreateMatchRanges(Node predicate, Context c)
    {
        var state = c.State ?? throw new InvalidOperationException("行matchの評価状態がありません。");
        var provider = c.Provider ?? throw new InvalidOperationException("行matchにはproviderが必要です。");
        c.Budget.Charge(1, c.Token);
        if (state.TryGetMatchRanges(predicate, out var cached))
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
                var matches = BooleanValue(predicate.Evaluate(lineContext), lineContext) is true;
                if (matches)
                {
                    if (start == -1) start = index;
                }
                else if (start != -1)
                {
                    c.Budget.Charge(1, c.Token);
                    ranges.Add(new(start, index - 1));
                    start = -1;
                }
            }
            if (start != -1)
            {
                c.Budget.Charge(1, c.Token);
                ranges.Add(new(start, provider.LineCount - 1));
            }

            c.Budget.Charge(ranges.Count + 1L, c.Token);
            var result = new MatchRangeCache(ranges.ToArray(), provider.ReadMultilineKey);
            state.PublishMatchRanges(predicate, result);
            return result;
        }
        finally { provider.RestoreMultilineRead(priorRead); }
    }

    private static (int Before, int After) GetDistancesToMatchRanges(int line, MatchRange[] ranges, Context c)
    {
        var previousEnd = -1;
        for (var index = 0; index < ranges.Length; index++)
        {
            c.Budget.Charge(1, c.Token);
            var range = ranges[index];
            if (range.Start <= line && line <= range.End) return (0, 0);
            if (line < range.Start)
            {
                var after = range.Start - line;
                if (previousEnd == -1) return (-1, after);
                return (line - previousEnd, after);
            }
            previousEnd = range.End;
        }
        return previousEnd == -1 ? (-1, -1) : (line - previousEnd, -1);
    }
}
