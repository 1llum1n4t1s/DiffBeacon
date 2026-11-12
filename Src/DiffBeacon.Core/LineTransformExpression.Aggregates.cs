using System.Runtime.CompilerServices;

namespace DiffBeacon.Core;

internal sealed partial class LineTransformExpression
{
    // 値と条件のAST参照identityがキー。関数名や式の文字列をキーにしない。
    internal sealed class EvaluationState
    {
        private readonly Dictionary<(object Value, object? Condition), object> statistics = new(new AggregateKeyComparer());
        private readonly Dictionary<object, object> matchRanges = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(object Value, object? Condition), object> insideRanges = new(new AggregateKeyComparer());

        internal bool TryGetStatistics(object value, object? condition, out object? result)
            => statistics.TryGetValue((value, condition), out result);

        internal void PublishStatistics(object value, object? condition, object result)
            => statistics.Add((value, condition), result);

        internal bool TryGetMatchRanges(object predicate, out object? result)
            => matchRanges.TryGetValue(predicate, out result);

        internal void PublishMatchRanges(object predicate, object result)
            => matchRanges.Add(predicate, result);

        internal bool TryGetInsideRanges(object begin, object end, out object? result)
            => insideRanges.TryGetValue((begin, end), out result);

        internal void PublishInsideRanges(object begin, object end, object result)
            => insideRanges.Add((begin, end), result);
    }

    private sealed class AggregateKeyComparer : IEqualityComparer<(object Value, object? Condition)>
    {
        public bool Equals((object Value, object? Condition) left, (object Value, object? Condition) right)
            => ReferenceEquals(left.Value, right.Value) && ReferenceEquals(left.Condition, right.Condition);

        public int GetHashCode((object Value, object? Condition) key)
            => HashCode.Combine(RuntimeHelpers.GetHashCode(key.Value),
                key.Condition is null ? 0 : RuntimeHelpers.GetHashCode(key.Condition));
    }

    // 生成中はlocalに保持し、全行の成功後だけcacheへ公開する。公開後は変更しない。
    private sealed class AggregateStatistics
    {
        internal long Count;
        internal int ValueType;
        internal double Sum;
        internal double? MaximumNumber;
        internal double? MinimumNumber;
        internal string? MaximumString;
        internal string? MinimumString;
        internal TimestampValue? MaximumTimestamp;
        internal TimestampValue? MinimumTimestamp;
        internal long TrueCount;
        internal long FalseCount;
        internal bool ReadMultilineKey;
    }

    private static object? EvaluateAggregate(string name, Node value, Node? condition, Context c)
    {
        c.Budget.Charge(1, c.Token);
        if (c.Provider is null) return null;
        var statistics = GetOrCreateStatistics(value, condition, c);
        c.Budget.Charge(1, c.Token);
        if (statistics.Count == 0) return null;
        if (statistics.ValueType == 1)
        {
            var number = name switch
            {
                "average" => statistics.Sum / statistics.Count,
                "sum" => statistics.Sum,
                "maximum" => statistics.MaximumNumber!.Value,
                "minimum" => statistics.MinimumNumber!.Value,
                _ => throw new ArgumentException("未対応の集計関数です。")
            };
            return name == "average" ? Finite(number) : AggregateNumericResult(number);
        }
        if (statistics.ValueType == 2)
            return name switch
            {
                "maximum" => statistics.MaximumString,
                "minimum" => statistics.MinimumString,
                _ => null
            };
        if (statistics.ValueType == 3)
            return name switch
            {
                "maximum" => statistics.MaximumTimestamp,
                "minimum" => statistics.MinimumTimestamp,
                _ => null
            };
        if (statistics.ValueType == 4)
            return name switch
            {
                "average" => (double)statistics.TrueCount / statistics.Count,
                "sum" => statistics.TrueCount,
                "maximum" => statistics.TrueCount > 0,
                "minimum" => statistics.FalseCount == 0,
                _ => null
            };
        return null;
    }

    private static object AggregateNumericResult(double number)
    {
        number = Finite(number);
        if (number != Math.Floor(number)) return number;
        // (double)long.MaxValueは2^63へ丸められるため上端を排他的に照合する。
        // 原典の範囲外int64_t castは未定義。数学的な値やwrap値へ補正しない。
        if (number < -9223372036854775808.0 || number >= 9223372036854775808.0)
            throw new OverflowException("行集計の整数結果が64ビット符号付き整数の範囲を超えています。");
        return checked((long)number);
    }

    private static AggregateStatistics GetOrCreateStatistics(Node value, Node? condition, Context c)
    {
        var state = c.State ?? throw new InvalidOperationException("行集計の評価状態がありません。");
        c.Budget.Charge(1, c.Token);
        var provider = c.Provider!;
        if (state.TryGetStatistics(value, condition, out var cached))
        {
            var statistics = (AggregateStatistics)cached!;
            provider.RestoreMultilineRead(statistics.ReadMultilineKey);
            return statistics;
        }
        // 外側の式の読込みをcacheへ混ぜず、集計が実際に読んだ由来だけを保持する。
        var priorRead = provider.ReadMultilineKey;
        provider.BeginEvaluation();
        try
        {
            var statistics = CreateStatistics(value, condition, c);
            statistics.ReadMultilineKey = provider.ReadMultilineKey;
            state.PublishStatistics(value, condition, statistics);
            return statistics;
        }
        finally { provider.RestoreMultilineRead(priorRead); }
    }

    private static AggregateStatistics CreateStatistics(Node value, Node? condition, Context c)
    {
        c.Budget.Charge(1, c.Token);
        var statistics = new AggregateStatistics();
        var count = c.Provider!.LineCount;
        for (var index = 0; index < count; index++)
        {
            c.Budget.Charge(1, c.Token);
            var lineContext = c with { LineIndex = index };
            if (condition is not null && BooleanValue(condition.Evaluate(lineContext), lineContext) is not true)
                continue;
            var result = value.Evaluate(lineContext);
            if (result is ValueArray array)
            {
                // 入れ子arrayはscalar処理で無視する。外側の全要素は共有予算へ課金する。
                for (var position = 0; position < array.Count; position++)
                    ProcessValueForStatistics(array[position], statistics, lineContext);
            }
            else ProcessValueForStatistics(result, statistics, lineContext);
        }
        // 未使用のnumeric累計はmixed型でも更新するが、戻り値に採用するまでfiniteを要求しない。
        // 内側集計は同じstateへ独立に公開済み。外側失敗でそのcacheを消さない。
        // 原典は選ばれたkindの統計だけをStatisticsResultへ移す。未使用の値を保持・課金しない。
        c.Budget.Charge(1, c.Token);
        if (statistics.ValueType != 1)
        {
            statistics.Sum = 0;
            statistics.MaximumNumber = null;
            statistics.MinimumNumber = null;
        }
        if (statistics.ValueType != 2)
        {
            statistics.MaximumString = null;
            statistics.MinimumString = null;
        }
        if (statistics.ValueType != 3)
        {
            statistics.MaximumTimestamp = null;
            statistics.MinimumTimestamp = null;
        }
        if (statistics.ValueType != 4)
        {
            statistics.TrueCount = 0;
            statistics.FalseCount = 0;
        }
        var retainedCharacters = (statistics.MaximumString?.Length ?? 0L) + (statistics.MinimumString?.Length ?? 0L);
        c.Budget.Charge(1 + retainedCharacters, c.Token, retainedCharacters);
        return statistics;
    }

    private static void ProcessValueForStatistics(object? value, AggregateStatistics statistics, Context c)
    {

        c.Budget.Charge(1, c.Token);
        if (value is bool boolean)
        {
            statistics.Count = checked(statistics.Count + 1);
            if (boolean) statistics.TrueCount = checked(statistics.TrueCount + 1);
            else statistics.FalseCount = checked(statistics.FalseCount + 1);
            if (statistics.ValueType == 0) statistics.ValueType = 4;
        }
        else if (value is long or double)
        {
            var number = value is long integer ? (double)integer : (double)value;
            statistics.Sum += number;
            statistics.Count = checked(statistics.Count + 1);
            if (statistics.MaximumNumber is null || number > statistics.MaximumNumber.Value)
                statistics.MaximumNumber = number;
            if (statistics.MinimumNumber is null || number < statistics.MinimumNumber.Value)
                statistics.MinimumNumber = number;
            if (statistics.ValueType == 0) statistics.ValueType = 1;
        }
        else if (value is string text)
        {
            statistics.Count = checked(statistics.Count + 1);
            if (statistics.MaximumString is null)
            {
                statistics.MaximumString = text;
                statistics.MinimumString = text;
            }
            else
            {
                var maximum = CompareStrings(text, statistics.MaximumString, c.CaseSensitive, c.Budget, c.Token);
                var minimum = CompareStrings(text, statistics.MinimumString!, c.CaseSensitive, c.Budget, c.Token);
                if (maximum > 0) statistics.MaximumString = text;
                if (minimum < 0) statistics.MinimumString = text;
            }
            if (statistics.ValueType == 0) statistics.ValueType = 2;
        }
        else if (value is TimestampValue timestamp)
        {
            statistics.Count = checked(statistics.Count + 1);
            if (statistics.MaximumTimestamp is null || timestamp.EpochMicroseconds > statistics.MaximumTimestamp.EpochMicroseconds)
                statistics.MaximumTimestamp = timestamp;
            if (statistics.MinimumTimestamp is null || timestamp.EpochMicroseconds < statistics.MinimumTimestamp.EpochMicroseconds)
                statistics.MinimumTimestamp = timestamp;
            if (statistics.ValueType == 0) statistics.ValueType = 3;
        }
        // noneと入れ子arrayはcountにもkindにも寄与しない。
    }
}
