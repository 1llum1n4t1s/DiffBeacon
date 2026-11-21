using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiffBeacon.Core;

namespace DiffBeacon.App;

// Poco Foundation の Timezone_UNIX.cpp / LocalDateTime.cpp (BSL-1.0) に従う。
// 原典: 6780924ba35f7f55af0f6f14dd302561175830a6。
// 標準時差と現在の時差は比較開始時に固定し、対象日時の DST 判定だけを libc に委ねる。
internal sealed partial class LineTransformMacTimestampEnvironment : ILineTransformTimestampClockEnvironment
{
    private readonly int _standardOffsetSeconds;

    private LineTransformMacTimestampEnvironment(int standardOffsetSeconds, int currentTzdSeconds)
    {
        _standardOffsetSeconds = standardOffsetSeconds;
        CurrentTzdSeconds = currentTzdSeconds;
    }

    public string ProfileId => "poco-unix-macos";
    public int CurrentTzdSeconds { get; }

    [SupportedOSPlatform("macos")]
    internal static LineTransformMacTimestampEnvironment Capture()
    {
        RequireMacOS();
        RefreshTimezone();
        var standardOffset = CaptureStandardOffset();
        var currentSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var current = ReadLocalTime(currentSeconds);
        var currentOffset = current.IsDst > 0 ? checked((int)current.GmtOffset) : standardOffset;
        return new LineTransformMacTimestampEnvironment(standardOffset, currentOffset);
    }

    public long CaptureNowEpochMicroseconds()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("日時の macOS 環境は macOS でのみ使用できます。");
        // 固定Poco Timestamp.cppはAppleでclock_gettimeの自動選択を除外する。
        if (GetTimeOfDay(out var value, 0) != 0) throw new IOException("cannot get time of day");
        return checked(value.Seconds * 1_000_000L + value.Microseconds);
    }
    // 元実装の locale parser は Windows 専用。macOS に別の解析規則を混ぜない。
    public bool TryParseWindowsLocale(string text, out long epochMicroseconds)
    {
        epochMicroseconds = 0;
        return false;
    }

    public int GetCalendarMidnightOffset(int year, int month, int day, long naiveEpochMicroseconds)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("日時の macOS 環境は macOS でのみ使用できます。");
        var broken = new NativeTm { Year = checked(year - 1900), Month = month - 1, Day = day, IsDst = -1 };
        // 原典dstOffsetは返値-1を失敗扱いせず、tm_isdst==1のときだけnaive timestampのDSTを照会する。
        _ = MakeTime(ref broken);
        if (broken.IsDst != 1) return _standardOffsetSeconds;
        var local = ReadLocalTime(naiveEpochMicroseconds / 1_000_000L);
        return local.IsDst > 0 ? checked((int)local.GmtOffset) : _standardOffsetSeconds;
    }
    public bool TryGetSerializationOffset(long epochMicroseconds, out int seconds, out string failureText)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("日時の macOS 環境は macOS でのみ使用できます。");
        seconds = 0;
        failureText = "System exception";

        try
        {
            // Poco Timestamp::epochTime() と同じく、負の端数も 0 に向けて除算する。
            // DateTimeOffset の年範囲へ変換せず、元の Int64 値を保持する。
            var epochSeconds = epochMicroseconds / 1_000_000;
            _ = ReadLocalTime(epochSeconds);
            // LocalDateTime の初回検査と Timezone::dst(timestamp) の再検査を保つ。
            var local = ReadLocalTime(epochSeconds);
            seconds = local.IsDst > 0 ? checked((int)local.GmtOffset) : _standardOffsetSeconds;
            failureText = string.Empty;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("macos")]
    private static int CaptureStandardOffset()
    {
        RequireMacOS();
        var january = new NativeTm { Year = 2024 - 1900, Month = 0, Day = 15, Hour = 12 };
        var januarySeconds = MakeTime(ref january);
        var januaryLocal = ReadLocalTime(januarySeconds);
        if (januaryLocal.IsDst <= 0)
            return checked((int)januaryLocal.GmtOffset);

        var july = new NativeTm { Year = 2024 - 1900, Month = 6, Day = 15, Hour = 12 };
        var julySeconds = MakeTime(ref july);
        return checked((int)ReadLocalTime(julySeconds).GmtOffset);
    }

    [SupportedOSPlatform("macos")]
    private static NativeTm ReadLocalTime(long epochSeconds)
    {
        RequireMacOS();
        if (LocalTime(in epochSeconds, out var local) == 0)
            throw new IOException("System exception");
        return local;
    }

    private static void RequireMacOS()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("日時の macOS 環境は macOS でのみ使用できます。");
    }

    // Darwin x64/arm64 timeval: 8-byte time_t、4-byte suseconds_tと末尾padding。
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTimeval { public long Seconds; public int Microseconds; }

    [SupportedOSPlatform("macos")]
    [LibraryImport("libSystem.B.dylib", EntryPoint = "gettimeofday")]
    private static partial int GetTimeOfDay(out NativeTimeval value, nint timezone);

    // Darwin x64 / arm64: int 9 個、8-byte long、pointer。time_t も 8-byte long。
    // Apple Libc include/_time.h の struct tm と localtime_r 宣言に対応する。
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTm
    {
        public int Second;
        public int Minute;
        public int Hour;
        public int Day;
        public int Month;
        public int Year;
        public int WeekDay;
        public int YearDay;
        public int IsDst;
        public long GmtOffset;
        public nint Zone;
    }

    [SupportedOSPlatform("macos")]
    [LibraryImport("libSystem.B.dylib", EntryPoint = "localtime_r")]
    private static partial nint LocalTime(in long epochSeconds, out NativeTm local);

    [SupportedOSPlatform("macos")]
    [LibraryImport("libSystem.B.dylib", EntryPoint = "mktime")]
    private static partial long MakeTime(ref NativeTm local);

    [SupportedOSPlatform("macos")]
    [LibraryImport("libSystem.B.dylib", EntryPoint = "tzset")]
    private static partial void RefreshTimezone();
}
