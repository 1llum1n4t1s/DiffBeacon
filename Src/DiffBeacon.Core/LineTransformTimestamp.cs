namespace DiffBeacon.Core;
/// <summary>解析済み行変換へ渡す日時環境。値は符号付き64ビットのepoch microseconds。</summary>
public interface ILineTransformTimestampEnvironment
{
    string ProfileId { get; }
    int CurrentTzdSeconds { get; }
    bool TryParseWindowsLocale(string text, out long epochMicroseconds);
    bool TryGetSerializationOffset(long epochMicroseconds, out int seconds, out string failureText);
}
/// <summary>startof専用の任意拡張。検査済みローカル日付の0時と、そのnaive epoch値を受ける。</summary>
public interface ILineTransformTimestampCalendarEnvironment : ILineTransformTimestampEnvironment
{
    // mktimeのUTC返値ではなく、Poco LocalDateTime calendar constructorの時差を返す。
    int GetCalendarMidnightOffset(int year, int month, int day, long naiveEpochMicroseconds);
}
/// <summary>任意の時計拡張。各式の解析開始時に一度だけepoch microsecondsを捕捉する。</summary>
public interface ILineTransformTimestampClockEnvironment : ILineTransformTimestampCalendarEnvironment
{
    long CaptureNowEpochMicroseconds();
}
internal sealed partial class LineTransformExpression
{
    private sealed class TimestampValue(long epochMicroseconds, ILineTransformTimestampEnvironment environment)
    {
        internal long EpochMicroseconds { get; } = epochMicroseconds;
        internal ILineTransformTimestampEnvironment Environment { get; } = environment;
        public override bool Equals(object? other) => other is TimestampValue timestamp && EpochMicroseconds == timestamp.EpochMicroseconds;
        public override int GetHashCode() => EpochMicroseconds.GetHashCode();
    }
    private sealed record TimestampClockSnapshot(TimestampValue Now, TimestampValue Today);
    private static TimestampClockSnapshot? CaptureTimestampClock(ILineTransformTimestampEnvironment? environment,
        LineTransformBudget budget, CancellationToken token)
    {
        if (environment is not ILineTransformTimestampClockEnvironment clock) return null;
        // 原典ParseのUpdateTimestampと同じく、構文解析より前に両方を確定する。
        budget.Charge(64, token);
        var now = clock.CaptureNowEpochMicroseconds();
        token.ThrowIfCancellationRequested();
        var today = LineTransformTimestampParser.Today(new(now), new TimestampEnvironmentAdapter(clock));
        token.ThrowIfCancellationRequested();
        return new(new(now, clock), new(today.Value, clock));
    }
    internal sealed class TimestampEnvironmentAdapter(ILineTransformTimestampEnvironment host) : ITimestampCalendarEnvironment
    {
        public string ProfileId => host.ProfileId;
        public int CurrentTzdSeconds => host.CurrentTzdSeconds;
        public bool TryParseWindowsLocale(string text, out TimestampEpochMicroseconds value)
        { var success = host.TryParseWindowsLocale(text, out var raw); value = new(raw); return success; }
        public int GetCalendarMidnightOffset(int year, int month, int day, TimestampEpochMicroseconds naiveValue)
            => host is ILineTransformTimestampCalendarEnvironment calendar
                ? calendar.GetCalendarMidnightOffset(year, month, day, naiveValue.Value)
                : throw new InvalidOperationException("この日時環境はローカル暦の構築に対応していません。");
        public bool TryGetSerializationOffset(TimestampEpochMicroseconds value, out int seconds, out string failureText)
            => host.TryGetSerializationOffset(value.Value, out seconds, out failureText);
    }
    private static TimestampConstructionResult ParseTimestampString(string text, ILineTransformTimestampEnvironment environment, Context c)
    {
        // literalと同じUnicode・共有作業量・取消境界を使い、配列も同じ予算を消費する。
        for (var at = 0; at < text.Length;) { c.Budget.Charge(1, c.Token); _ = ReadScalar(text, ref at); }
        c.Budget.Charge(text.Length * 16L + 1, c.Token, text.Length * 2L);
        c.Token.ThrowIfCancellationRequested();
        var result = LineTransformTimestampParser.Construct(text, new TimestampEnvironmentAdapter(environment));
        c.Token.ThrowIfCancellationRequested();
        return result;
    }
    private static object? ConvertToTimestamp(object? value, ILineTransformTimestampEnvironment? environment, Context c)
    {
        if (value is not string text || text.Length == 0) return null;
        if (environment is null) throw new ArgumentException("日時関数には明示的な環境が必要です。");
        var result = ParseTimestampString(text, environment, c);
        return result.Success ? new TimestampValue(result.Value.Value, environment) : null;
    }
    private static object? ConvertToDateString(object? value, ILineTransformTimestampEnvironment? environment, Context c)
    {
        TimestampValue timestamp;
        if (value is TimestampValue existing) timestamp = existing;
        else if (value is string text)
        {
            if (environment is null) throw new ArgumentException("日時関数には明示的な環境が必要です。");
            var result = ParseTimestampString(text, environment, c);
            if (!result.Success) throw new ArgumentException(result.Error);
            timestamp = new TimestampValue(result.Value.Value, environment);
        }
        else return null;
        c.Budget.Charge(32, c.Token); c.Token.ThrowIfCancellationRequested();
        var date = LineTransformTimestampParser.FormatLocalDate(new(timestamp.EpochMicroseconds), new TimestampEnvironmentAdapter(timestamp.Environment));
        c.Budget.Charge(date.Length, c.Token, date.Length); c.Token.ThrowIfCancellationRequested();
        return date;
    }
    private static object? ConvertStartOf(object? value, string function, ILineTransformTimestampEnvironment? environment, Context c)
    {
        TimestampValue timestamp;
        if (value is TimestampValue existing) timestamp = existing;
        else if (value is string text)
        {
            if (environment is null) throw new ArgumentException("日時関数には明示的な環境が必要です。");
            var parsed = ParseTimestampString(text, environment, c);
            if (!parsed.Success) throw new ArgumentException(parsed.Error);
            timestamp = new TimestampValue(parsed.Value.Value, environment);
        }
        else return null;
        c.Budget.Charge(64, c.Token); c.Token.ThrowIfCancellationRequested();
        var result = LineTransformTimestampParser.StartOf(new(timestamp.EpochMicroseconds), function, new TimestampEnvironmentAdapter(timestamp.Environment));
        c.Token.ThrowIfCancellationRequested();
        return new TimestampValue(result.Value, timestamp.Environment);
    }
    private static string SerializeTimestamp(TimestampValue timestamp, LineTransformBudget budget, CancellationToken token)
    {
        budget.Charge(32, token); token.ThrowIfCancellationRequested();
        var text = LineTransformTimestampParser.Serialize(new(timestamp.EpochMicroseconds), new TimestampEnvironmentAdapter(timestamp.Environment));
        budget.Charge(text.Length, token, text.Length); token.ThrowIfCancellationRequested(); return text;
    }
}
