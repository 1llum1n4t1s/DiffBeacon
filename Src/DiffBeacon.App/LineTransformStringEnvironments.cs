using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static partial class LineTransformStringEnvironments
{
    internal static ILineTransformStringEnvironment ForFolderFilters()
        => new CurrentLocaleEnvironment(CultureInfo.CurrentCulture.TextInfo);

    internal static ILineTransformStringEnvironment? ForRules(IReadOnlyList<LineTransformRule> rules)
        => rules.Count == 0 ? null : new CurrentLocaleEnvironment(CultureInfo.CurrentCulture.TextInfo);

    private sealed class CurrentLocaleEnvironment(TextInfo textInfo) : ILineTransformStringEnvironment
    {
        public string ToLower(string value) => OperatingSystem.IsWindows()
            ? MapWindowsLocale(value, MapLowercase)
            : textInfo.ToLower(value);

        public string ToUpper(string value) => OperatingSystem.IsWindows()
            ? MapWindowsLocale(value, MapUppercase)
            : textInfo.ToUpper(value);

        public string MapCharacters(string value, LineTransformCharacterMapping mapping)
        {
            // 別の互換文字まで変更するNFKC/NFKDでLCMapStringExを代用しない。
            if (OperatingSystem.IsMacOS()) return MapMacCharacters(value, mapping);
            if (!OperatingSystem.IsWindows())
                throw new ArgumentException("文字種の行変換はWindowsとmacOSで使用できます。");
            var flags = mapping switch
            {
                LineTransformCharacterMapping.HalfWidth => 0x00400000u,
                LineTransformCharacterMapping.FullWidth => 0x00800000u,
                LineTransformCharacterMapping.Hiragana => 0x00100000u,
                LineTransformCharacterMapping.Katakana => 0x00200000u,
                LineTransformCharacterMapping.SimplifiedChinese => 0x02000000u,
                LineTransformCharacterMapping.TraditionalChinese => 0x04000000u,
                _ => throw new ArgumentOutOfRangeException(nameof(mapping))
            };
            return MapWindowsLocale(value, flags);
        }

        public string NormalizeUnicode(string value, NormalizationForm form)
        {
            if (OperatingSystem.IsWindows()) return NormalizeWindows(value, form);
            // managed実装のUnicodeデータを使う。Windows原典との完全一致は保証しない。
            try { return value.Normalize(form); }
            catch (ArgumentException) { return value; }
        }
    }

    private const uint MapLowercase = 0x00000100;
    private const uint MapUppercase = 0x00000200;

    private static unsafe string NormalizeWindows(string value, NormalizationForm form)
    {
        if (value.Length == 0) return value;
        try
        {
            // 原典と同じ明示UTF-16長。query結果が失敗／不足した場合は再試行しない。
            var required = NormalizeString((int)form, value, value.Length, null, 0);
            if (required <= 0) return value;
            var normalized = new char[required];
            int written;
            fixed (char* destination = normalized)
                written = NormalizeString((int)form, value, value.Length, destination, normalized.Length);
            return written <= 0 || written > normalized.Length ? value : new string(normalized, 0, written);
        }
        catch (DllNotFoundException) { return value; }
        catch (EntryPointNotFoundException) { return value; }
        catch (BadImageFormatException) { return value; }
        catch (OutOfMemoryException) { return value; }
    }

    [LibraryImport("normaliz.dll", EntryPoint = "NormalizeString", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial int NormalizeString(int form, string source, int sourceLength, char* destination, int destinationLength);

    private static unsafe string MapWindowsLocale(string value, uint flags)
    {
        if (value.Length == 0) return value;
        var required = MapStringEx(null, flags, value, value.Length, null, 0, 0, 0, 0);
        if (required <= 0) return value;
        var mapped = new char[required];
        int written;
        fixed (char* destination = mapped)
            written = MapStringEx(null, flags, value, value.Length, destination, mapped.Length, 0, 0, 0);
        return written <= 0 || written > mapped.Length ? value : new string(mapped, 0, written);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LCMapStringEx", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial int MapStringEx(string? localeName, uint mapFlags, string source, int sourceLength,
        char* destination, int destinationLength, nint versionInformation, nint reserved, nint sortHandle);
}
