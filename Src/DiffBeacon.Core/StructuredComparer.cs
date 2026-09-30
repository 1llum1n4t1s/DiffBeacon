using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace DiffBeacon.Core;

public sealed record StructuredTable(IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public int RowCount => Rows.Count;
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(row => row.Count);
}
public sealed record CellDifference(int Row, int Column, string? LeftValue, string? RightValue, DiffKind Kind);
public sealed record StructuredDiffResult(StructuredTable Left, StructuredTable Right, IReadOnlyList<CellDifference> Differences)
{
    public bool HasDifferences => Differences.Count > 0;
}

public static class StructuredComparer
{
    public static StructuredTable ParseDelimited(string text, char delimiter = ',', char quote = '"', bool allowNewlinesInQuotes = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delimiter is '\r' or '\n' or '\0' || char.IsSurrogate(delimiter) || delimiter == quote) throw new ArgumentException("区切り文字が不正です。", nameof(delimiter));
        if (quote is '\r' or '\n' or '\0' || char.IsSurrogate(quote)) throw new ArgumentException("引用符が不正です。", nameof(quote));
        var rows = new List<IReadOnlyList<string>>();
        if (text.Length == 0) return new(rows);
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var quoteClosed = false;
        var atStart = true;
        var endedRow = false;
        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var character = text[index];
            endedRow = false;
            if (quoted)
            {
                if (character == quote)
                {
                    if (index + 1 < text.Length && text[index + 1] == quote) { cell.Append(quote); index++; }
                    else { quoted = false; quoteClosed = true; }
                }
                else if (!allowNewlinesInQuotes && character is '\r' or '\n') throw new FormatException("引用符内の改行は許可されていません。");
                else cell.Append(character);
                continue;
            }
            if (character == quote && atStart) { quoted = true; atStart = false; continue; }
            if (character == delimiter || character is '\r' or '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                atStart = true;
                quoteClosed = false;
                if (character != delimiter)
                {
                    if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                    rows.Add(row);
                    row = [];
                    endedRow = true;
                }
                continue;
            }
            if (quoteClosed || character == quote) throw new FormatException($"区切りテキストの {index + 1} 文字目に不正な引用符があります。");
            cell.Append(character);
            atStart = false;
        }
        if (quoted) throw new FormatException("区切りテキストの引用符が閉じられていません。");
        if (!endedRow) { row.Add(cell.ToString()); rows.Add(row); }
        return new(rows);
    }

    public static StructuredDiffResult CompareDelimited(string left, string right, char delimiter = ',',
        ComparisonOptions? options = null, CancellationToken cancellationToken = default, char quote = '"', bool allowNewlinesInQuotes = true)
    {
        options ??= new();
        var a = ParseDelimited(left, delimiter, quote, allowNewlinesInQuotes, cancellationToken);
        var b = ParseDelimited(right, delimiter, quote, allowNewlinesInQuotes, cancellationToken);
        var differences = new List<CellDifference>();
        for (var row = 0; row < Math.Max(a.RowCount, b.RowCount); row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ar = row < a.RowCount ? a.Rows[row] : [];
            var br = row < b.RowCount ? b.Rows[row] : [];
            for (var column = 0; column < Math.Max(ar.Count, br.Count); column++)
            {
                var av = column < ar.Count ? ar[column] : null;
                var bv = column < br.Count ? br[column] : null;
                if (av is not null && bv is not null && options.Normalize(av) == options.Normalize(bv)) continue;
                differences.Add(new(row + 1, column + 1, av, bv,
                    av is null ? DiffKind.Added : bv is null ? DiffKind.Deleted : DiffKind.Modified));
            }
        }
        return new(a, b, differences);
    }

    // JsonDocument/Utf8JsonWriter のみを使い、反射によるシリアライズを避ける。
    public static string NormalizeJson(string text, bool sortProperties = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (text.Length > 16 * 1024 * 1024) throw new JsonException("JSON は 16,777,216 文字以下にしてください。");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 128 });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            void Write(JsonElement element)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        writer.WriteStartObject();
                        var properties = element.EnumerateObject().ToArray();
                        if (sortProperties) Array.Sort(properties, (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                        foreach (var property in properties) { writer.WritePropertyName(property.Name); Write(property.Value); }
                        writer.WriteEndObject();
                        break;
                    case JsonValueKind.Array:
                        writer.WriteStartArray();
                        foreach (var child in element.EnumerateArray()) Write(child);
                        writer.WriteEndArray();
                        break;
                    case JsonValueKind.Number:
                        writer.WriteRawValue(CanonicalNumber(element.GetRawText()), skipInputValidation: true);
                        break;
                    default: element.WriteTo(writer); break;
                }
            }
            Write(document.RootElement);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static DiffResult CompareJson(string left, string right, ComparisonOptions? options = null, CancellationToken cancellationToken = default) =>
        TextDiffer.Compare(NormalizeJson(left, cancellationToken: cancellationToken), NormalizeJson(right, cancellationToken: cancellationToken), options, cancellationToken);

    // 浮動小数点へ変換せず、十進の係数と指数を正規化して精度を保つ。
    private static string CanonicalNumber(string raw)
    {
        if (raw.Length > 65_536) throw new JsonException("JSON の数値は 65,536 文字以下にしてください。");
        var exponentAt = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentAt < 0 ? raw : raw[..exponentAt];
        var exponent = BigInteger.Zero;
        if (exponentAt >= 0)
        {
            var exponentText = raw[(exponentAt + 1)..];
            if (exponentText.Length > 512) throw new JsonException("JSON の指数は 512 文字以下にしてください。");
            exponent = BigInteger.Parse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }
        var negative = mantissa.StartsWith('-');
        if (negative) mantissa = mantissa[1..];
        var decimalAt = mantissa.IndexOf('.');
        if (decimalAt >= 0)
        {
            exponent -= mantissa.Length - decimalAt - 1;
            mantissa = mantissa.Remove(decimalAt, 1);
        }
        mantissa = mantissa.TrimStart('0');
        if (mantissa.Length == 0) return "0";
        var significant = mantissa.TrimEnd('0');
        exponent += mantissa.Length - significant.Length;
        return (negative ? "-" : "") + significant +
            (exponent.IsZero ? "" : "e" + exponent.ToString(CultureInfo.InvariantCulture));
    }
}
