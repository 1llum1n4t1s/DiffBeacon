using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class LineTransformTimestampEnvironments
{
    // 時計拡張を提供し、各式の解析開始時にOS環境を遅延初期化する。
    internal static ILineTransformTimestampEnvironment? ForRules(IReadOnlyList<LineTransformRule> rules)
        => rules.Count == 0 ? null : new DeferredEnvironment();

    internal static ILineTransformTimestampEnvironment ForFolderFilters() => new DeferredEnvironment();

    private sealed class DeferredEnvironment : ILineTransformTimestampClockEnvironment
    {
        private readonly Lazy<ILineTransformTimestampEnvironment> environment = new(Capture);
        public long CaptureNowEpochMicroseconds()
            => environment.Value is ILineTransformTimestampClockEnvironment clock
                ? clock.CaptureNowEpochMicroseconds()
                : throw new InvalidOperationException("この日時環境は時計の捕捉に対応していません。");
        public string ProfileId => environment.Value.ProfileId;
        public int CurrentTzdSeconds => environment.Value.CurrentTzdSeconds;
        public bool TryParseWindowsLocale(string text, out long epochMicroseconds)
            => environment.Value.TryParseWindowsLocale(text, out epochMicroseconds);
        public bool TryGetSerializationOffset(long epochMicroseconds, out int seconds, out string failureText)
            => environment.Value.TryGetSerializationOffset(epochMicroseconds, out seconds, out failureText);

        public int GetCalendarMidnightOffset(int year, int month, int day, long naiveEpochMicroseconds)
            => environment.Value is ILineTransformTimestampCalendarEnvironment calendar
                ? calendar.GetCalendarMidnightOffset(year, month, day, naiveEpochMicroseconds)
                : throw new InvalidOperationException("この日時環境はローカル暦の構築に対応していません。");
        private static ILineTransformTimestampEnvironment Capture()
        {
            if (OperatingSystem.IsWindows()) return LineTransformWindowsTimestampEnvironment.Capture();
            if (OperatingSystem.IsMacOS()) return LineTransformMacTimestampEnvironment.Capture();
            throw new PlatformNotSupportedException("日時の行変換はWindowsとmacOSに対応しています。");
        }
    }
}
