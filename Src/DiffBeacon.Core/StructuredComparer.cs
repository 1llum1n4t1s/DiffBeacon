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
        => AsStructured(ParseTable(text, new(delimiter, quote, allowNewlinesInQuotes), cancellationToken), cancellationToken);

    public static TableDocument ParseTable(string text, DelimitedSyntax? syntax = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        syntax ??= new();
        ValidateSyntax(syntax);
        if (text.Length > TableAlignment.MaxTextLength) throw new ArgumentException("表は 67,108,864 文字以下にしてください。", nameof(text));
        var delimiter = syntax.Delimiter;
        var quote = syntax.Quote;
        var rows = new List<TableRow>();
        if (text.Length == 0) return new(text, syntax, rows.AsReadOnly());
        var row = new List<TableCell>();
        var cell = new StringBuilder();
        var cellStart = 0;
        var rowStart = 0;
        var cellCount = 0;
        void AddCell(int end)
        {
            if (++cellCount > TableAlignment.MaxCells) throw new ArgumentException("表のセルは 1,048,576 個までです。", nameof(text));
            row.Add(new(cell.ToString(), cellStart, end - cellStart));
        }
        void AddRow(int end, string ending)
        {
            if (rows.Count >= TableAlignment.MaxRows) throw new ArgumentException("表の論理行は 262,144 行までです。", nameof(text));
            rows.Add(new(rows.Count + 1, rowStart, end - rowStart, ending, row.AsReadOnly()));
        }
        var quoted = false;
        var quoteClosed = false;
        var atStart = true;
        var endedRow = false;
        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 4095) <= 1) cancellationToken.ThrowIfCancellationRequested();
            var character = text[index];
            endedRow = false;
            if (quoted)
            {
                if (character == quote)
                {
                    if (index + 1 < text.Length && text[index + 1] == quote) { cell.Append(quote); index++; }
                    else { quoted = false; quoteClosed = true; }
                }
                else if (!syntax.AllowNewlinesInQuotes && character is '\r' or '\n') throw new FormatException("引用符内の改行は許可されていません。");
                else cell.Append(character);
                continue;
            }
            if (character == quote && atStart) { quoted = true; atStart = false; continue; }
            if (character == delimiter || character is '\r' or '\n')
            {
                AddCell(index);
                cell.Clear();
                atStart = true;
                quoteClosed = false;
                if (character != delimiter)
                {
                    var endingStart = index;
                    if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                    AddRow(endingStart, text[endingStart..(index + 1)]);
                    row = [];
                    rowStart = index + 1;
                    endedRow = true;
                }
                cellStart = index + 1;
                continue;
            }
            if (quoteClosed || character == quote) throw new FormatException($"区切りテキストの {index + 1} 文字目に不正な引用符があります。");
            cell.Append(character);
            atStart = false;
        }
        if (quoted) throw new FormatException("区切りテキストの引用符が閉じられていません。");
        if (!endedRow)
        {
            AddCell(text.Length);
            AddRow(text.Length, "");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(text, syntax, rows.AsReadOnly());
    }

    private static void ValidateSyntax(DelimitedSyntax syntax)
    {
        if (syntax.Delimiter is '\r' or '\n' or '\0' || char.IsSurrogate(syntax.Delimiter) || syntax.Delimiter == syntax.Quote)
            throw new ArgumentException("区切り文字が不正です。", nameof(syntax));
        if (syntax.Quote is '\r' or '\n' or '\0' || char.IsSurrogate(syntax.Quote))
            throw new ArgumentException("引用符が不正です。", nameof(syntax));
    }

    private static StructuredTable AsStructured(TableDocument document, CancellationToken token)
    {
        var rows = new IReadOnlyList<string>[document.Rows.Count];
        for (var row = 0; row < rows.Length; row++)
        {
            token.ThrowIfCancellationRequested();
            var cells = document.Rows[row].Cells;
            var values = new string[cells.Count];
            for (var column = 0; column < values.Length; column++)
            {
                if ((column & 255) == 0) token.ThrowIfCancellationRequested();
                values[column] = cells[column].Value;
            }
            rows[row] = Array.AsReadOnly(values);
        }
        return new(Array.AsReadOnly(rows));
    }

    public static TableTextEdit ReplaceCell(TableDocument document, int sourceRow, int column, string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        if (value.Length > TableAlignment.MaxTextLength) throw new ArgumentException("セル値が長すぎます。", nameof(value));
        ValidateSyntax(document.Syntax);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceRow, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceRow, document.Rows.Count);
        var row = document.Rows[sourceRow - 1];
        ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(column, row.Cells.Count);
        var cell = row.Cells[column - 1];
        if (cell.RawStart < 0 || cell.RawLength < 0 || cell.RawStart > document.SourceText.Length - cell.RawLength)
            throw new ArgumentException("セルの原文区間が不正です。", nameof(document));
        var syntax = document.Syntax;
        // 終端なしの一セル行を空文字へ変えても、論理行自体を消さない。
        var quoted = cell.RawLength > 0 && document.SourceText[cell.RawStart] == syntax.Quote ||
            value.Length == 0 && row.Cells.Count == 1 && row.Ending.Length == 0;
        for (var index = 0; index < value.Length; index++)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var character = value[index];
            if (character is '\r' or '\n' && !syntax.AllowNewlinesInQuotes)
                throw new FormatException("引用符内の改行は許可されていません。");
            quoted |= character == syntax.Delimiter || character == syntax.Quote || character is '\r' or '\n';
        }
        var replacement = new StringBuilder();
        if (quoted) replacement.Append(syntax.Quote);
        for (var index = 0; index < value.Length; index++)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var character = value[index];
            if (character == syntax.Quote) replacement.Append(syntax.Quote);
            replacement.Append(character);
            if (replacement.Length > TableAlignment.MaxTextLength) throw new ArgumentException("引用符の展開後のセル値が長すぎます。", nameof(value));
        }
        if (quoted) replacement.Append(syntax.Quote);
        if ((long)document.SourceText.Length - cell.RawLength + replacement.Length > TableAlignment.MaxTextLength)
            throw new ArgumentException("編集後の表が文字数上限を超えます。", nameof(value));
        cancellationToken.ThrowIfCancellationRequested();
        return new(cell.RawStart, cell.RawLength, replacement.ToString());
    }

    public static TableComparisonResult CompareTables(IReadOnlyList<string> texts, DelimitedSyntax? syntax = null,
        ComparisonOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count is not (2 or 3)) throw new ArgumentException("表比較には二つまたは三つの文書が必要です。", nameof(texts));
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.SubstitutionRules);
        if (options.SubstitutionRules.Count > 256) throw new ArgumentException("置換規則は 256 個までです。", nameof(options));
        if (!Enum.IsDefined(options.Whitespace)) throw new ArgumentOutOfRangeException(nameof(options.Whitespace));
        var rules = new SubstitutionRule[options.SubstitutionRules.Count];
        for (var index = 0; index < rules.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rule = options.SubstitutionRules[index];
            ArgumentNullException.ThrowIfNull(rule);
            rules[index] = rule with { };
        }
        var snapshot = options with { SubstitutionRules = Array.AsReadOnly(rules) };
        var documents = new TableDocument[texts.Count];
        for (var side = 0; side < texts.Count; side++) documents[side] = ParseTable(texts[side], syntax, cancellationToken);
        return TableAlignment.Compare(documents, snapshot, cancellationToken);
    }

    public static StructuredDiffResult CompareDelimited(string left, string right, char delimiter = ',',
        ComparisonOptions? options = null, CancellationToken cancellationToken = default, char quote = '"', bool allowNewlinesInQuotes = true)
    {
        var comparison = CompareTables([left, right], new(delimiter, quote, allowNewlinesInQuotes), options, cancellationToken);
        var a = AsStructured(comparison.Documents[0], cancellationToken);
        var b = AsStructured(comparison.Documents[1], cancellationToken);
        var differences = new List<CellDifference>();
        for (var row = 0; row < comparison.Rows.Count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ar = comparison.GetSourceRow(0, row);
            var br = comparison.GetSourceRow(1, row);
            var columns = Math.Max(ar is null ? 0 : a.Rows[ar.Value - 1].Count, br is null ? 0 : b.Rows[br.Value - 1].Count);
            for (var column = 0; column < columns; column++)
            {
                if ((column & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var kind = comparison.GetKind(0, row, column);
                if (kind == DiffKind.Equal) continue;
                differences.Add(new(row + 1, column + 1, comparison.GetCell(0, row, column)?.Value,
                    comparison.GetCell(1, row, column)?.Value, kind));
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
