// GNU DIFF util.c line_cmp のC-locale byte移植。GPL-2.0-or-later。COPYING参照。
using System.Text;

namespace DiffBeacon.Core;

[Flags]
public enum NativeLineFlags
{
    None = 0, IgnoreCase = 1, IgnoreSpaceChange = 2,
    IgnoreAllSpace = 4, IgnoreEol = 8, IgnoreNumbers = 16
}

public static class LegacyLineComparator
{
    // 復号本文には従来製品のUnicode/空白キーを使い、native flagで近似しない。
    internal static GnuLineKey ProductKey(ReadOnlySpan<byte> line, bool missingFinalNewline,
        ComparisonOptions options, Action<long> spend, Action<long> reserve, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        spend(line.Length); reserve(checked(2L * line.Length));
        var endingLength = line.EndsWith("\r\n"u8) ? 2 :
            line.Length > 0 && line[^1] is 10 or 13 ? 1 : 0;
        var content = new UTF8Encoding(false, true).GetString(line[..(line.Length - endingLength)]);
        spend(content.Length);
        var key = options.NormalizeKey(content, token);
        token.ThrowIfCancellationRequested();
        var ending = options.CompareLineEndings && !missingFinalNewline
            ? Encoding.UTF8.GetString(line[(line.Length - endingLength)..]) : "";
        return new(key, ending, !options.IgnoreFinalNewLine && missingFinalNewline);
    }

    public static bool IsHorizontalSpace(byte c) => c is 32 or 9;
    public static bool IsCSpace(byte c) => c is 32 or >= 9 and <= 13;
    public static bool IsCDigit(byte c) => c is >= 48 and <= 57;
    public static bool IsCUpper(byte c) => c is >= 65 and <= 90;
    public static byte CToLower(byte c) => IsCUpper(c) ? (byte)(c + 32) : c;

    // 戻り値0/1、読み取り順、元関数のcontinue/早期終了を保持する。
    public static int Compare(ReadOnlySpan<byte> s1, ReadOnlySpan<byte> s2,
        NativeLineFlags flags, Action spend)
    {
        ArgumentNullException.ThrowIfNull(spend);
        if (((int)flags & ~31) != 0) throw new ArgumentOutOfRangeException(nameof(flags));
        spend();
        if (s1.Length == s2.Length)
        {
            var same = true;
            for (var i = 0; i < s1.Length; i++)
            {
                spend();
                if (s1[i] != s2[i]) { same = false; break; }
            }
            if (same) return 0;
        }
        if (flags == 0) return 1;
        var t1 = 0; var t2 = 0;
        while (true)
        {
            spend();
            byte c1 = t1 < s1.Length ? s1[t1++] : (byte)0;
            byte c2 = t2 < s2.Length ? s2[t2++] : (byte)0;
            if (c1 != c2)
            {
                if (flags.HasFlag(NativeLineFlags.IgnoreAllSpace))
                {
                    while (IsHorizontalSpace(c1))
                    {
                        spend();
                        if (t1 < s1.Length) c1 = s1[t1++];
                        else { c1 = 0; break; }
                    }
                    while (IsHorizontalSpace(c2))
                    {
                        spend();
                        if (t2 < s2.Length) c2 = s2[t2++];
                        else { c2 = 0; break; }
                    }
                }
                else if (flags.HasFlag(NativeLineFlags.IgnoreSpaceChange))
                {
                    if (IsHorizontalSpace(c1))
                    {
                        c1 = 32;
                        while (t1 < s1.Length && IsHorizontalSpace(s1[t1])) { spend(); t1++; }
                        if (c2 is 13 or 10) c1 = t1 < s1.Length ? s1[t1++] : (byte)0;
                    }
                    if (IsHorizontalSpace(c2))
                    {
                        c2 = 32;
                        while (t2 < s2.Length && IsHorizontalSpace(s2[t2])) { spend(); t2++; }
                        if (c1 is 13 or 10) c2 = t2 < s2.Length ? s2[t2++] : (byte)0;
                    }
                    if (c1 != c2)
                    {
                        if (c1 == 32 && c2 == 0) c2 = 32;
                        else if (c2 == 32 && c1 == 0) c1 = 32;
                    }
                    if (c1 != c2)
                    {
                        if (c2 == 32 && c1 != 0 && c1 is not (10 or 13) && t1 > 1 && IsCSpace(s1[t1 - 2]))
                        { t1--; continue; }
                        if (c1 == 32 && c2 != 0 && c2 is not (10 or 13) && t2 > 1 && IsCSpace(s2[t2 - 2]))
                        { t2--; continue; }
                    }
                }
                if (flags.HasFlag(NativeLineFlags.IgnoreNumbers))
                {
                    while (IsCDigit(c1))
                    {
                        spend();
                        if (t1 < s1.Length) c1 = s1[t1++];
                        else { c1 = 0; break; }
                    }
                    while (IsCDigit(c2))
                    {
                        spend();
                        if (t2 < s2.Length) c2 = s2[t2++];
                        else { c2 = 0; break; }
                    }
                }
                if (flags.HasFlag(NativeLineFlags.IgnoreCase))
                {
                    if (IsCUpper(c1)) c1 = CToLower(c1);
                    if (IsCUpper(c2)) c2 = CToLower(c2);
                }
                if (flags.HasFlag(NativeLineFlags.IgnoreEol))
                {
                    if (c1 == 13) c1 = 0;
                    else if (c2 == 13) c2 = 0;
                }
                if (c1 != c2) break;
            }
            if (c1 == 0) return 0;
        }
        return 1;
    }
}
