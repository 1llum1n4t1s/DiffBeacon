// Poco parser/calendar の参照: Copyright (c) 2004-2006 Applied Informatics
// Software Engineering GmbH and Contributors. SPDX-License-Identifier: BSL-1.0
// FilterExpressionNodes の固定原典 commit: 6780924ba35f7f55af0f6f14dd302561175830a6
using System.Globalization;
using System.Text.RegularExpressions;

namespace DiffBeacon.Core;

// DateTimeOffset の年範囲で値を切り落とさない。
internal readonly record struct TimestampEpochMicroseconds(long Value);

internal interface ITimestampParserEnvironment
{
    string ProfileId { get; }
    // 構築時点の Poco::Timezone::tzd()。対象日の DST と混同しない。
    int CurrentTzdSeconds { get; }
    // VarDateFromStr -> SYSTEMTIME -> local FILETIME -> UTC FILETIME のホスト境界。
    bool TryParseWindowsLocale(string text, out TimestampEpochMicroseconds value);
    // LocalDateTime の対象時刻に対する localtime_s / UTC offset / DST 境界。
    bool TryGetSerializationOffset(TimestampEpochMicroseconds value, out int seconds, out string failureText);
}

internal interface ITimestampCalendarEnvironment : ITimestampParserEnvironment
{
    int GetCalendarMidnightOffset(int year, int month, int day, TimestampEpochMicroseconds naiveValue);
}
internal readonly record struct TimestampConstructionResult(bool Success, TimestampEpochMicroseconds Value, string Error)
{
    public static TimestampConstructionResult Invalid(string text) => new(false, default, "Unrecognized date/time format: " + text);
}

internal static class LineTransformTimestampParser
{
    private static readonly string[] Formats = ["%Y-%m-%dT%H:%M:%S%z", "%Y-%m-%dT%H:%M:%s%z",
        "%Y/%m/%d %H:%M:%S", "%Y.%m.%d %H:%M:%S", "%d-%b-%Y %H:%M:%S", "%Y-%m-%d", "%Y/%m/%d", "%Y.%m.%d"];
    private static readonly string[] Months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    private const string ZonesPattern = "(UT)|(GMT)|(BST)|(IST)|(WET)|(WEST)|(CET)|(CEST)|(EET)|(EEST)|(EST)|(MSK)|(MSD)|(NST)|(NDT)|(AST)|(ADT)|(EST)|(EDT)|(CST)|(CDT)|(MST)|(MDT)|(PST)|(PDT)|(AKST)|(AKDT)|(HST)|(AEST)|(AEDT)|(ACST)|(ACDT)|(AWST)|(AWDT)";
    // DateTimeFormat.cpp の元 PCRE パターン。HTTP の possessive optional は下の atomic group に置換。
    // \\d / \\w / \\s は ASCII 設定にする。パターン全体を括ると元 backreference 番号がずれるため非capture。
    private static readonly Regex[] Validators = CreateValidators();
    private static Regex[] CreateValidators()
    {
        const string iso = @"([\+-]?\d{4}(?!\d{2}\b))((-?)((0[1-9]|1[0-2])(\3([12]\d|0[1-9]|3[01]))?|W([0-4]\d|5[0-2])(-?[1-7])?|(00[1-9]|0[1-9]\d|[12]\d{2}|3([0-5]\d|6[1-6])))([T\s]((([01]\d|2[0-3])((:?)[0-5]\d)?|24\:?00)([\.,]\d+(?!:))?)?(\17[0-5]\d([\.,]\d+)?)?([A-I]|[K-Z]|([\+-])([01]\d|2[0-3]):?([0-5]\d)?)?)?)?";
        const string shortWeek = "(((Mon)|(Tue)|(Wed)|(Thu)|(Fri)|(Sat)|(Sun)), *)?";
        const string longWeek = "(((Monday)|(Tuesday)|(Wednesday)|(Thursday)|(Friday)|(Saturday)|(Sunday)|(Mon)|(Tue)|(Wed)|(Thu)|(Fri)|(Sat)|(Sun)), *)?";
        const string month = "((Jan)|(Feb)|(Mar)|(Apr)|(May)|(Jun)|(Jul)|(Aug)|(Sep)|(Oct)|(Nov)|(Dec))";
        string rfc822 = shortWeek + @"\d\d? +" + month + @" +\d\d(\d\d)? +\d\d:\d\d(:\d\d)? +(([+\-]?\d\d\d\d)|" + ZonesPattern + @"|\w)";
        string http = shortWeek + @"\d\d? +" + month + @" +\d\d(\d\d)? +\d\d:\d\d(:\d\d)? (?>((" + ZonesPattern + @"|))?)(([+\-]?\d\d\d\d)?|" + ZonesPattern + @"|\w)";
        string rfc850 = longWeek + @"\d\d?-" + month + @"-\d\d(\d\d)? +\d\d:\d\d(:\d\d)? (" + ZonesPattern + @"|)?+(([+\-]?\d\d\d\d)?|" + ZonesPattern + @"|\w)";
        string rfc1036 = "(((Monday)|(Tuesday)|(Wednesday)|(Thursday)|(Friday)|(Saturday)|(Sunday)), *)?" + @"\d\d? +" + month + @" +\d\d(\d\d)? +\d\d:\d\d(:\d\d)? (" + ZonesPattern + @"|)?+(([+\-]?\d\d\d\d)?|" + ZonesPattern + @"|\w)";
        // 下記二形式にも PCRE の possessive optional がある。
        rfc850 = ReplacePossessiveOptional(rfc850); rfc1036 = ReplacePossessiveOptional(rfc1036);
        string asctime = "((Mon)|(Tue)|(Wed)|(Thu)|(Fri)|(Sat)|(Sun)) +" + month + @" +\d\d? +\d\d:\d\d:\d\d +\d\d\d\d";
        return new[] { iso, rfc822, http, rfc850, rfc1036, asctime, @"(\d\d\d\d-\d\d-\d\d \d\d:\d\d:\d\d)" }
            .Select(p => new Regex(@"\A(?:" + p + @")\z", RegexOptions.CultureInvariant | RegexOptions.ECMAScript, TimeSpan.FromMilliseconds(100))).ToArray();
    }
    private static string ReplacePossessiveOptional(string pattern)
    {
        // この固定パターンでは対象が最後の timezone alternation group 一つだけ。
        string target = "(" + ZonesPattern + "|)?+";
        return pattern.Replace(target, "(?>(" + ZonesPattern + "|)?)", StringComparison.Ordinal);
    }

    public static TimestampConstructionResult Construct(string input, ITimestampParserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(environment);
        // 現 public lexer の入力境界を保持。Native の constructor専用 NUL 観測を公開しない。
        if (input.Contains('\0')) return new(false, default, "Public lexer rejects NUL");
        for (int i = 0; i < Formats.Length; i++)
        {
            try
            {
                if (!TryComponents(input, Formats[i], i < 2, out var c, out int parsedTzd)) continue;
                int tzd = input.IndexOfAny(['z', 'Z', '+']) >= 0 ? parsedTzd : environment.CurrentTzdSeconds;
                long days = DaysFromCivil(c.Year, c.Month, c.Day);
                long epoch = checked(days * 86_400_000_000L + ((c.Hour * 60L + c.Minute) * 60 + c.Second) * 1_000_000 + c.Fraction - tzd * 1_000_000L);
                if (!IsValidUtcDateTime(epoch)) throw new ArgumentException("Invalid argument");
                return new(true, new(epoch), "");
            }
            catch (FormatException) { /* 元の SyntaxException は次形式へ。 */ }
        }
        return environment.TryParseWindowsLocale(input, out var fallback) ? new(true, fallback, "") : TimestampConstructionResult.Invalid(input);
    }

    public static string Serialize(TimestampEpochMicroseconds value, ITimestampParserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        // 原典Timestamp::utcTimeのsigned乗算/加算が未定義になる範囲は表示せず拒否する。
        if (value.Value < long.MinValue / 10 || value.Value > (long.MaxValue - 122_192_928_000_000_000L) / 10)
            throw new InvalidOperationException("日時の値が安全に表示できる範囲を超えています。");
        // DateTime(Timestamp)はLocalDateTimeのホスト処理より先にUTC値を検査する。
        if (!IsValidUtcDateTime(value.Value)) return "Invalid argument";
        // 入力値はInt64のまま保持し、ホストが表示可能性と対象日DSTを判断する。
        if (!environment.TryGetSerializationOffset(value, out int offset, out string failure)) return failure;
        try
        {
            long local = checked(value.Value + offset * 1_000_000L);
            if (!IsValidUtcDateTime(local)) return "Invalid argument";
            long day = FloorDivide(local, 86_400_000_000L);
            long daytime = local - day * 86_400_000_000L;
            var c = CivilFromDays(day);
            if (c.Year < -4713 || c.Year > 9999) return "Invalid argument";
            return c.Year.ToString(c.Year < 0 ? "D3" : "D4", CultureInfo.InvariantCulture) + "-" + c.Month.ToString("D2", CultureInfo.InvariantCulture) + "-" + c.Day.ToString("D2", CultureInfo.InvariantCulture) + " " +
                (daytime / 3_600_000_000L).ToString("D2", CultureInfo.InvariantCulture) + ":" + (daytime / 60_000_000L % 60).ToString("D2", CultureInfo.InvariantCulture) + ":" + (daytime / 1_000_000 % 60).ToString("D2", CultureInfo.InvariantCulture);
        }
        catch (OverflowException) { return "Invalid argument"; }
    }

    // ToDateStrはStringValueのcatch契約を使わず、LocalDateTime構築失敗を呼出し元へ返す。
    public static string FormatLocalDate(TimestampEpochMicroseconds value, ITimestampParserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (value.Value < long.MinValue / 10 || value.Value > (long.MaxValue - 122_192_928_000_000_000L) / 10)
            throw new InvalidOperationException("日時の値が安全に表示できる範囲を超えています。");
        if (!IsValidUtcDateTime(value.Value)) throw new ArgumentException("Invalid argument");
        if (!environment.TryGetSerializationOffset(value, out int offset, out string failure))
            throw new InvalidOperationException(failure);
        long local = checked(value.Value + offset * 1_000_000L);
        if (!IsValidUtcDateTime(local)) throw new ArgumentException("Invalid argument");
        var civil = CivilFromDays(FloorDivide(local, 86_400_000_000L));
        if (civil.Year < -4713 || civil.Year > 9999) throw new ArgumentException("Invalid argument");
        return civil.Year.ToString(civil.Year < 0 ? "D3" : "D4", CultureInfo.InvariantCulture) + "-"
            + civil.Month.ToString("D2", CultureInfo.InvariantCulture) + "-" + civil.Day.ToString("D2", CultureInfo.InvariantCulture);
    }

    public static TimestampEpochMicroseconds Today(TimestampEpochMicroseconds now, ITimestampCalendarEnvironment environment)
    {
        var local = LocalCalendar(now, environment);
        int year = checked((int)local.Year), month = checked((int)local.Month), day = checked((int)local.Day);
        long naive = checked(DaysFromCivil(year, month, day) * 86_400_000_000L);
        RequireCalendarTimestamp(naive);
        int offset = environment.GetCalendarMidnightOffset(year, month, day, new(naive));
        long utc = checked(naive - offset * 1_000_000L);
        RequireCalendarTimestamp(utc);
        return new(utc);
    }
    public static TimestampEpochMicroseconds StartOf(TimestampEpochMicroseconds value, string function, ITimestampCalendarEnvironment environment)
    {
        var local = LocalCalendar(value, environment);
        int year = checked((int)local.Year);
        int month = function == "startofyear" ? 1 : checked((int)local.Month);
        int day = function == "startofweek" ? checked((int)local.Day) : 1;
        long naive = checked(DaysFromCivil(year, month, day) * 86_400_000_000L);
        RequireCalendarTimestamp(naive);
        // 原典calendar constructorはmktime返値ではなくtm_isdstとnaive timestampからtzdを求める。
        int tzd = environment.GetCalendarMidnightOffset(year, month, day, new(naive));
        long utc = checked(naive - tzd * 1_000_000L);
        if (function == "startofweek")
        {
            // 元ldtの日曜日=0。暦日を先に引かず、UTC時間軸で24hを引いてLocalDateTimeを再構築する。
            long weekday = (local.DayNumber + 4) % 7;
            if (weekday < 0) weekday += 7;
            utc = checked(utc - weekday * 86_400_000_000L);
            _ = LocalCalendar(new(utc), environment);
        }
        else if (function is not ("startofmonth" or "startofyear"))
            throw new ArgumentException("未対応のローカル暦関数です。");
        // .utc()もDateTimeを検査するが、month/yearの返値を再びlocaltimeへ渡す経路はない。
        RequireCalendarTimestamp(utc);
        return new(utc);
    }
    private static (long Year, long Month, long Day, long DayNumber) LocalCalendar(TimestampEpochMicroseconds value, ITimestampParserEnvironment environment)
    {
        RequireCalendarTimestamp(value.Value);
        if (!environment.TryGetSerializationOffset(value, out int offset, out string failure)) throw new InvalidOperationException(failure);
        long local = checked(value.Value + offset * 1_000_000L);
        RequireCalendarTimestamp(local);
        long days = FloorDivide(local, 86_400_000_000L);
        var civil = CivilFromDays(days);
        return (civil.Year, civil.Month, civil.Day, days);
    }
    private static void RequireCalendarTimestamp(long value)
    {
        if (value < long.MinValue / 10 || value > (long.MaxValue - 122_192_928_000_000_000L) / 10)
            throw new InvalidOperationException("日時の値が安全に表示できる範囲を超えています。");
        if (!IsValidUtcDateTime(value)) throw new ArgumentException("Invalid argument");
    }
    private static bool IsValidUtcDateTime(long epochMicroseconds)
    {
        var utc = CivilFromDays(FloorDivide(epochMicroseconds, 86_400_000_000L));
        if (utc.Year < -4713 || utc.Year > 9999) return false;
        // computeDaytimeは負のutcTimeへ1600*365日を足し、Timespanの符号付き余りを使う。
        // 補正後も負なら日内の端数は負の成分になるため、checkValidで拒否される。
        long utcTime = checked(epochMicroseconds * 10L + 122_192_928_000_000_000L);
        if (utcTime < 0) utcTime += 86400L * 1000 * 1000 * 10 * 1600 * 365;
        return utcTime >= 0 || (utcTime / 10) % 86_400_000_000L == 0;
    }

    private readonly record struct Components(int Year, int Month, int Day, int Hour, int Minute, int Second, int Fraction);
    private static bool IsSpace(char c) => c is ' ' or '\t' or '\r' or '\n' or '\v' or '\f';
    private static bool IsPunct(char c) => c <= 127 && !char.IsAsciiLetterOrDigit(c) && !IsSpace(c) && c >= '!';
    private static int ReadNumber(string text, ref int at, int limit, bool skip)
    {
        if (skip) while (at < text.Length && !char.IsAsciiDigit(text[at])) at++;
        int start = at, value = 0;
        while (at < text.Length && at - start < limit && char.IsAsciiDigit(text[at])) value = value * 10 + text[at++] - '0';
        if (start == at) throw new FormatException();
        return value;
    }
    private static int Fraction(string text, ref int at)
    {
        int n = 0, result = 0;
        while (at < text.Length && n < 3 && char.IsAsciiDigit(text[at])) { result = result * 10 + text[at++] - '0'; n++; }
        while (n++ < 3) result *= 10;
        return result;
    }
    private static bool TryComponents(string input, string format, bool validate, out Components c, out int tzd)
    {
        string text = input.Trim(' ', '\t', '\r', '\n', '\v', '\f'); c = default; tzd = 0;
        if (text.Length == 0 || (validate && !Validators.Any(re => re.IsMatch(text)))) return false;
        int y = 0, m = 1, d = 1, h = 0, min = 0, sec = 0, frac = 0, at = 0, f = 0;
        while (f < format.Length && at < text.Length)
        {
            if (format[f] == '%')
            {
                if (++f == format.Length) break;
                switch (format[f++])
                {
                    case 'Y': y = ReadNumber(text, ref at, 4, true); break;
                    case 'm': m = ReadNumber(text, ref at, 2, true); break;
                    case 'd': d = ReadNumber(text, ref at, 2, true); break;
                    case 'H': h = ReadNumber(text, ref at, 2, true); break;
                    case 'M': min = ReadNumber(text, ref at, 2, true); break;
                    case 'S': sec = ReadNumber(text, ref at, 2, true); break;
                    case 's':
                        sec = ReadNumber(text, ref at, 2, true);
                        if (at < text.Length && text[at] is '.' or ',')
                        {
                            at++;
                            if (at < text.Length && !char.IsAsciiDigit(text[at])) throw new FormatException();
                            frac = Fraction(text, ref at) * 1000 + Fraction(text, ref at);
                            while (at < text.Length && char.IsAsciiDigit(text[at])) at++;
                        }
                        break;
                    case 'b':
                        while (at < text.Length && (IsSpace(text[at]) || IsPunct(text[at]))) at++;
                        int start = at;
                        while (at < text.Length && char.IsAsciiLetter(text[at])) at++;
                        string name = text[start..at];
                        if (name.Length < 3) throw new FormatException();
                        m = Array.FindIndex(Months, s => s.StartsWith(name, StringComparison.OrdinalIgnoreCase)) + 1;
                        if (m == 0) throw new FormatException();
                        break;
                    case 'z': tzd = ReadTimezone(text, ref at); break;
                }
            }
            else if (IsSpace(format[f]))
            {
                while (f < format.Length && IsSpace(format[f])) f++;
                while (at < text.Length && IsSpace(text[at])) at++;
            }
            else { if (format[f] == text[at]) at++; f++; }
        }
        while (at < text.Length && IsSpace(text[at])) at++;
        if (at != text.Length || y > 9999 || m < 1 || m > 12 || d < 1 || d > DaysInMonth(y, m) || h > 23 || min > 59 || sec > 60) return false;
        c = new(y, m, d, h, min, sec, frac); return true;
    }

    private static readonly Dictionary<string, (int Offset, bool Difference)> Zones = new(StringComparer.Ordinal)
    {
        ["Z"]=(0,true), ["UT"]=(0,true), ["GMT"]=(0,true), ["BST"]=(3600,false), ["IST"]=(3600,false), ["WET"]=(0,false), ["WEST"]=(3600,false),
        ["CET"]=(3600,false), ["CEST"]=(7200,false), ["EET"]=(7200,false), ["EEST"]=(10800,false), ["MSK"]=(10800,false), ["MSD"]=(14400,false),
        ["NST"]=(-12600,false), ["NDT"]=(-9000,false), ["AST"]=(-14400,false), ["ADT"]=(-10800,false), ["EST"]=(-18000,false), ["EDT"]=(-14400,false),
        ["CST"]=(-21600,false), ["CDT"]=(-18000,false), ["MST"]=(-25200,false), ["MDT"]=(-21600,false), ["PST"]=(-28800,false), ["PDT"]=(-25200,false),
        ["AKST"]=(-32400,false), ["AKDT"]=(-28800,false), ["HST"]=(-36000,false), ["AEST"]=(36000,false), ["AEDT"]=(39600,false),
        ["ACST"]=(34200,false), ["ACDT"]=(37800,false), ["AWST"]=(28800,false), ["AWDT"]=(32400,false)
    };
    private static int ReadTimezone(string text, ref int at)
    {
        while (at < text.Length && IsSpace(text[at])) at++;
        int offset = 0; bool difference = true;
        int start = at;
        while (at < text.Length && at - start < 4 && char.IsAsciiLetter(text[at])) at++;
        if (at > start)
        {
            if (!Zones.TryGetValue(text[start..at], out var zone)) throw new FormatException();
            (offset, difference) = zone;
        }
        if (at < text.Length && text[at] is '+' or '-')
        {
            if (!difference) throw new FormatException();
            int sign = text[at++] == '+' ? 1 : -1;
            int hours = ReadNumber(text, ref at, 2, false);
            if (at < text.Length && text[at] == ':') at++;
            int minutes = ReadNumber(text, ref at, 2, false);
            if (hours > 23 || minutes > 59) throw new FormatException();
            offset += sign * (hours * 3600 + minutes * 60);
        }
        return offset;
    }
    private static int DaysInMonth(int year, int month) => month == 2 ? (year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) ? 29 : 28) : (month is 4 or 6 or 9 or 11 ? 30 : 31);
    private static long FloorDivide(long a, long b) => a / b - (a % b < 0 ? 1 : 0);
    private static long DaysFromCivil(int year, int month, int day)
    {
        long y = year - (month <= 2 ? 1 : 0); long era = FloorDivide(y, 400); long yoe = y - era * 400;
        long doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
        return era * 146097 + yoe * 365 + yoe / 4 - yoe / 100 + doy - 719468;
    }
    private static (long Year, long Month, long Day) CivilFromDays(long days)
    {
        long z = days + 719468, era = FloorDivide(z, 146097), doe = z - era * 146097;
        long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        long y = yoe + era * 400, doy = doe - (365 * yoe + yoe / 4 - yoe / 100), mp = (5 * doy + 2) / 153;
        long day = doy - (153 * mp + 2) / 5 + 1, month = mp + (mp < 10 ? 3 : -9);
        return (y + (month <= 2 ? 1 : 0), month, day);
    }
}
