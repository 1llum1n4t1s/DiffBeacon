using System.Globalization;
using System.Text;

namespace DiffBeacon.Core;

// Frhedのb/w/lとliteral契約を保ち、未定義のscanfや浮動値returnは実行しない。
public static class BinaryBytecode
{
    public const int MaximumTextBytes = BinaryEditSession.MaximumFileBytes * 7;
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > BinaryEditSession.MaximumFileBytes) throw new InvalidDataException("コピーは16 MiBまでです。");
        var size = 0;
        foreach (var value in bytes) size = checked(size + (value is 60 or 92 ? 2 : value is >= 32 and < 127 or 10 or 13 ? 1 : 7));
        var output = new StringBuilder(size);
        foreach (var value in bytes)
            if (value is 60 or 92) output.Append('\\').Append((char)value);
            else if (value is >= 32 and < 127 or 10 or 13) output.Append((char)value);
            else output.Append("<bh:").Append(value.ToString("x2", CultureInfo.InvariantCulture)).Append('>');
        return output.ToString();
    }
    public static byte[] Decode(ReadOnlySpan<byte> text, bool bigEndian = false, Func<byte, byte>? literalMapper = null)
    {
        if (text.Length > MaximumTextBytes) throw new InvalidDataException("バイトコードの文字数上限を超えています。");
        // 一回目で出力量を検査し、不正入力や容量超過時に巨大な出力を確保しない。
        var length = Translate(text, Span<byte>.Empty, bigEndian, null);
        var output = new byte[length]; Translate(text, output, bigEndian, literalMapper); return output;
    }
    private static int Translate(ReadOnlySpan<byte> text, Span<byte> output, bool bigEndian, Func<byte, byte>? literalMapper)
    {
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var size = Token(text[index..], out var end);
            if (size != 0)
            {
                var source = text.Slice(index + 4, end - 4); var value = Number(text[index + 1], text[index + 2], source);
                if (count > BinaryEditSession.MaximumFileBytes - size) throw new InvalidDataException("貼り付けは16 MiBまでです。");
                if (!output.IsEmpty) for (var at = 0; at < size; at++) output[count + at] = (byte)(value >> (8 * (bigEndian ? size - at - 1 : at)));
                count += size; index += end;
            }
            else
            {
                if (count == BinaryEditSession.MaximumFileBytes) throw new InvalidDataException("貼り付けは16 MiBまでです。");
                var escaped = text[index] == 92;
                if (escaped && index + 1 < text.Length && text[index + 1] is 60 or 92) index++;
                if (!output.IsEmpty) output[count] = !escaped && literalMapper is not null ? literalMapper(text[index]) : text[index]; count++;
            }
        }
        return count;
    }
    private static int Token(ReadOnlySpan<byte> source, out int end)
    {
        end = 0;
        if (source.Length < 5 || source[0] != 60 || source[1] is not (98 or 119 or 108 or 102 or 100) || source[2] is not (100 or 104 or 108 or 111) || source[3] != 58) return 0;
        // suffixの閉じ括弧を先に探すと、未閉鎖prefixの反復が二次走査になる。
        // 数値の有効文字だけを最大50文字見る。50文字以上の数値prefixは安全境界で拒否する。
        for (end = 4; end < source.Length; end++)
        {
            var value = source[end];
            if (value == 62) break;
            if (!(value is >= 48 and <= 57 || source[2] == 100 && value == 45 || source[2] == 104 && value is >= 97 and <= 102 || source[2] is 108 or 111 && value is 45 or 46 or 101 or 69)) return 0;
            if (end - 4 >= 49) throw new FormatException("数値バイトコードは49文字までです。");
        }
        if (end == source.Length || end <= 4) return 0;
        return source[1] == 98 ? 1 : source[1] == 119 ? 2 : source[1] == 100 ? 8 : 4;
    }
    private static ulong Number(byte type, byte format, ReadOnlySpan<byte> source)
    {
        if (source.Length >= 50) throw new FormatException("数値バイトコードは49文字までです。");
        if (type is 102 or 100)
        {
            if (!(type == 102 && format == 108 || type == 100 && format == 111)) throw new FormatException("浮動値は<fl:値>または<do:値>を使ってください。");
            var text = Encoding.ASCII.GetString(source);
            if (type == 102 && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) && float.IsFinite(single)) return BitConverter.SingleToUInt32Bits(single);
            if (type == 100 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return BitConverter.DoubleToUInt64Bits(number);
            throw new FormatException("有限のIEEE浮動値を入力してください。");
        }
        if (format is not (100 or 104)) throw new FormatException("整数はdまたはh形式を使ってください。");
        var negative = format == 100 && source[0] == 45; var offset = negative ? 1 : 0; var first = offset; ulong integer = 0;
        while (offset < source.Length)
        {
            var value = source[offset]; var digit = value <= 57 ? value - 48 : value - 97 + 10;
            if (value == 45) break;
            integer = integer * (format == 104 ? 16u : 10u) + (uint)digit;
            if (integer > (format == 104 ? uint.MaxValue : negative ? 2147483648u : int.MaxValue)) throw new FormatException("32bit整数の範囲を超えています。");
            offset++;
        }
        if (offset == first) throw new FormatException("数値バイトコードの値が不正です。");
        return negative ? unchecked((uint)-(long)integer) : integer;
    }
}
