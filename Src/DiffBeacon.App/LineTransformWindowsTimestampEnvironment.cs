// 固定原典: WinMerge commit 6780924ba35f7f55af0f6f14dd302561175830a6。
// Poco Timezone/LocalDateTime/Timestamp reference: BSL-1.0.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal sealed record WindowsTimestampProfile(uint UserLcid, string UserLocale, uint TimezoneState, string TimezoneStandardName,
    int BiasMinutes, int StandardBiasMinutes, int DaylightBiasMinutes, int CurrentTzdSeconds, string? AmbientTz, string CapturedUtc);

// 現在の時差を比較単位で捕捉。地域形式の変換と対象日時のDST判定はWindows APIへ渡す。
internal sealed class LineTransformWindowsTimestampEnvironment : ILineTransformTimestampClockEnvironment
{
    internal WindowsTimestampProfile Snapshot { get; }
    public string ProfileId { get; }
    public int CurrentTzdSeconds => Snapshot.CurrentTzdSeconds;
    private LineTransformWindowsTimestampEnvironment(WindowsTimestampProfile snapshot)
    { Snapshot = snapshot; ProfileId = $"windows:{Guid.NewGuid():N}"; }

    [SupportedOSPlatform("windows")]
    internal static unsafe LineTransformWindowsTimestampEnvironment Capture()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows timestamp environment requires Windows");
        if (sizeof(WinSystemTime) != 16 || sizeof(WinFileTime) != 8 || sizeof(WinTimezoneInformation) != 172 || sizeof(CrtTm) != 36)
            throw new InvalidOperationException("Native ABI layout mismatch");
        uint state = NativeTimestampApi.GetTimeZoneInformation(out var zone);
        if (state == uint.MaxValue) throw new InvalidOperationException("GetTimeZoneInformation failed");
        char* locale = stackalloc char[85];
        int length = NativeTimestampApi.GetUserDefaultLocaleName(locale, 85);
        if (length == 0) throw new InvalidOperationException("GetUserDefaultLocaleName failed");
        int tzd = checked(-zone.Bias * 60 + (state == 2 ? -zone.DaylightBias * 60 : 0));
        string standardName = new string(zone.StandardName).TrimEnd('\0');
        var profile = new WindowsTimestampProfile(NativeTimestampApi.GetUserDefaultLCID(), new string(locale, 0, length - 1), state, standardName,
            zone.Bias, zone.StandardBias, zone.DaylightBias, tzd, Environment.GetEnvironmentVariable("TZ"), DateTime.UtcNow.ToString("O"));
        return new(profile);
    }
    public long CaptureNowEpochMicroseconds()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows timestamp environment requires Windows");
        // Poco Timestamp::updateと同じ非precise API・unsigned差分・10除算。
        NativeTimestampApi.GetSystemTimeAsFileTime(out var fileTime);
        ulong ticks = ((ulong)fileTime.High << 32) | fileTime.Low;
        return unchecked((long)(unchecked(ticks - 0x019DB1DED53E8000UL) / 10UL));
    }
    public bool TryParseWindowsLocale(string text, out long value)
    {
        value = default;
        if (text.Length == 0) return false;
        // locality.cpp149–181を実APIで同順序に呼ぶ。LCID_USER_DEFAULTは固定原典と同じ0x400。
        if (NativeTimestampApi.VarDateFromStr(text, 0x400, 0, out double oleDate) < 0) return false;
        if (NativeTimestampApi.VariantTimeToSystemTime(oleDate, out var systemTime) == 0) return false;
        if (NativeTimestampApi.SystemTimeToFileTime(in systemTime, out var localFileTime) == 0) return false;
        if (NativeTimestampApi.LocalFileTimeToFileTime(in localFileTime, out var utcFileTime) == 0) return false;
        ulong rawFileTime = ((ulong)utcFileTime.High << 32) | utcFileTime.Low;
        // Timestamp::fromFileTimeNP: unsigned subtractionが先、unsigned divide10が後。
        ulong difference = unchecked(rawFileTime - 0x019DB1DED53E8000UL);
        value = unchecked((long)(difference / 10UL));
        return true;
    }
    public int GetCalendarMidnightOffset(int year, int month, int day, long naiveEpochMicroseconds)
    {
        var broken = new CrtTm { Year = checked(year - 1900), Mon = month - 1, Mday = day, IsDst = -1 };
        // LocalDateTime.cpp298-315はmktimeの-1を拒否せず、更新されたtm_isdstだけを使用する。
        _ = NativeTimestampApi.MakeTime64(ref broken);
        int daylight = 0;
        if (broken.IsDst == 1)
        {
            // Timezone::dstはmktimeのUTC返値ではなく、naive local DateTime timestampを見る。
            long seconds = naiveEpochMicroseconds / 1_000_000L;
            if (NativeTimestampApi.LocalTime64(out var local, in seconds) != 0) throw new InvalidOperationException("System exception");
            if (local.IsDst > 0)
            {
                // 原典Timezone::dst(timestamp)はDST時だけOSのDaylightBiasを取り直す。
                if (NativeTimestampApi.GetTimeZoneInformation(out var zone) == uint.MaxValue) throw new InvalidOperationException("GetTimeZoneInformation failed");
                daylight = checked(-zone.DaylightBias * 60);
            }
        }
        return checked(-Snapshot.BiasMinutes * 60 + daylight);
    }
    public bool TryGetSerializationOffset(long value, out int seconds, out string failureText)
    {
        seconds = 0; failureText = "";
        // Timestamp.h224–226は0方向Int64整数除算。ここでfloorしない。
        long epochSeconds = value / 1_000_000L;
        // LocalDateTime.determineTzd(true) の localtime_s と Timezone.isDst のlocaltime_sを別々に保持。
        if (NativeTimestampApi.LocalTime64(out _, in epochSeconds) != 0) { failureText = "System exception"; return false; }
        if (NativeTimestampApi.LocalTime64(out var local, in epochSeconds) != 0) { failureText = "System exception"; return false; }
        seconds = checked(-Snapshot.BiasMinutes * 60 + (local.IsDst > 0 ? -Snapshot.DaylightBiasMinutes * 60 : 0));
        return true;
    }
}

[StructLayout(LayoutKind.Sequential)] internal struct WinSystemTime { internal ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }
[StructLayout(LayoutKind.Sequential)] internal struct WinFileTime { internal uint Low, High; }
[StructLayout(LayoutKind.Sequential)] internal unsafe struct WinTimezoneInformation
{
    internal int Bias; internal fixed char StandardName[32]; internal WinSystemTime StandardDate; internal int StandardBias;
    internal fixed char DaylightName[32]; internal WinSystemTime DaylightDate; internal int DaylightBias;
}
[StructLayout(LayoutKind.Sequential)] internal struct CrtTm { internal int Sec, Min, Hour, Mday, Mon, Year, Wday, Yday, IsDst; }
internal static partial class NativeTimestampApi
{
    [LibraryImport("kernel32.dll")] internal static partial void GetSystemTimeAsFileTime(out WinFileTime value);
    [LibraryImport("oleaut32.dll", EntryPoint = "VarDateFromStr", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int VarDateFromStr(string text, uint lcid, uint flags, out double date);
    [LibraryImport("oleaut32.dll")] internal static partial int VariantTimeToSystemTime(double date, out WinSystemTime result);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial int SystemTimeToFileTime(in WinSystemTime value, out WinFileTime result);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial int LocalFileTimeToFileTime(in WinFileTime value, out WinFileTime result);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial uint GetTimeZoneInformation(out WinTimezoneInformation value);
    [LibraryImport("kernel32.dll")] internal static partial uint GetUserDefaultLCID();
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static unsafe partial int GetUserDefaultLocaleName(char* locale, int length);
    [LibraryImport("ucrtbase.dll", EntryPoint = "_mktime64")]
    internal static partial long MakeTime64(ref CrtTm value);
    [LibraryImport("ucrtbase.dll", EntryPoint = "_localtime64_s")] internal static partial int LocalTime64(out CrtTm value, in long epochSeconds);
}
