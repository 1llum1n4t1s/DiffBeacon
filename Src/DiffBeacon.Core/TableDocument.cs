namespace DiffBeacon.Core;

public sealed record DelimitedSyntax(char Delimiter = ',', char Quote = '"', bool AllowNewlinesInQuotes = true);

// Raw 区間は引用符を含み、区切り文字と行末を含まない。
public sealed record TableCell(string Value, int RawStart, int RawLength);
public sealed record TableRow(int SourceRow, int RawStart, int RawLength, string Ending, IReadOnlyList<TableCell> Cells);
public sealed record TableDocument(string SourceText, DelimitedSyntax Syntax, IReadOnlyList<TableRow> Rows)
{
    public int ColumnCount { get; } = Rows.Count == 0 ? 0 : Rows.Max(row => row.Cells.Count);
}

public sealed record TableTextEdit(int Start, int Length, string Replacement);
