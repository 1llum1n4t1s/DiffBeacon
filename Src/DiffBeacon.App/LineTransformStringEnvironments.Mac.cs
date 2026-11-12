using System.Runtime.InteropServices;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static partial class LineTransformStringEnvironments
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private static unsafe string MapMacCharacters(string value, LineTransformCharacterMapping mapping)
    {
        if (value.Length == 0) return value;
        // CFStringTransformの公式契約で許されるICU ID。互換正規化は追加しない。
        var (identifier, reverse) = mapping switch
        {
            LineTransformCharacterMapping.HalfWidth => ("Fullwidth-Halfwidth", (byte)0),
            LineTransformCharacterMapping.FullWidth => ("Fullwidth-Halfwidth", (byte)1),
            LineTransformCharacterMapping.Hiragana => ("Hiragana-Katakana", (byte)1),
            LineTransformCharacterMapping.Katakana => ("Hiragana-Katakana", (byte)0),
            LineTransformCharacterMapping.SimplifiedChinese => ("Traditional-Simplified", (byte)0),
            LineTransformCharacterMapping.TraditionalChinese => ("Simplified-Traditional", (byte)0),
            _ => throw new ArgumentOutOfRangeException(nameof(mapping))
        };
        nint source = 0, mutable = 0, transform = 0;
        try
        {
            fixed (char* characters = value)
                source = CFStringCreateWithCharacters(0, characters, value.Length);
            if (source == 0) throw new InvalidOperationException("文字種変換の入力を確保できませんでした。");
            mutable = CFStringCreateMutableCopy(0, 0, source);
            fixed (char* characters = identifier)
                transform = CFStringCreateWithCharacters(0, characters, identifier.Length);
            if (mutable == 0 || transform == 0) throw new InvalidOperationException("文字種変換の作業領域を確保できませんでした。");
            // Booleanは1 byte、CFIndexはpointer幅。NULL rangeで埋込みNULを含む全文を変換する。
            if (CFStringTransform(mutable, 0, transform, reverse) == 0)
                throw new InvalidOperationException("システムの文字種変換に失敗しました。");
            var length = CFStringGetLength(mutable);
            if (length < 0 || length > int.MaxValue) throw new InvalidOperationException("文字種変換結果が上限を超えています。");
            if (length == 0) return "";
            var result = new char[(int)length];
            fixed (char* characters = result)
                CFStringGetCharacters(mutable, new CFCharacterRange { Location = 0, Length = length }, characters);
            return new string(result);
        }
        finally
        {
            if (transform != 0) CFRelease(transform);
            if (mutable != 0) CFRelease(mutable);
            if (source != 0) CFRelease(source);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CFCharacterRange
    {
        internal nint Location;
        internal nint Length;
    }

    [LibraryImport(CoreFoundation)]
    private static unsafe partial nint CFStringCreateWithCharacters(nint allocator, char* characters, nint length);
    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringCreateMutableCopy(nint allocator, nint maximumLength, nint source);
    [LibraryImport(CoreFoundation)]
    private static partial byte CFStringTransform(nint value, nint range, nint transform, byte reverse);
    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(nint value);
    [LibraryImport(CoreFoundation)]
    private static unsafe partial void CFStringGetCharacters(nint value, CFCharacterRange range, char* characters);
    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);
}
