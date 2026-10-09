using System.Text;
using System.Text.RegularExpressions;

namespace DiffBeacon.Core;

public enum DiffKind { Equal, Added, Deleted, Modified }
public enum WhitespaceMode { None, Trim, IgnoreChanges, IgnoreAll }
public enum CommentSyntax { None, CStyle, CSharp, Python, Xml }

public sealed record SubstitutionRule(string Pattern, string Replacement, bool MatchCase = true)
{
    public bool UseRegex { get; init; } = true;
    public bool WholeWord { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record ComparisonOptions
{
    public bool IgnoreCase { get; init; }
    public bool IgnoreNumbers { get; init; }
    public CommentSyntax CommentSyntax { get; init; }
    public IReadOnlyList<SubstitutionRule> SubstitutionRules { get; init; } = [];
    public bool IgnoreWhitespace { get; init; }
    public WhitespaceMode Whitespace { get; init; }
    public bool IgnoreBlankLines { get; init; }
    public string? IgnoreLinePattern { get; init; }
    public bool IgnoreFinalNewLine { get; init; }
    public bool CompareLineEndings { get; init; }
    public bool InlineCharacterLevel { get; init; } = true;
    public int MaxFallbackComparisons { get; init; } = 4_000_000;

    public string Normalize(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new TextPreprocessor(this, cancellationToken).NormalizeStandalone(text);
    }

    internal string NormalizeKey(string text, CancellationToken cancellationToken)
    {
        var mode = IgnoreWhitespace ? WhitespaceMode.IgnoreAll : Whitespace;
        if (mode == WhitespaceMode.Trim) text = text.Trim();
        else if (mode is WhitespaceMode.IgnoreAll or WhitespaceMode.IgnoreChanges)
        {
            var builder = new StringBuilder(text.Length);
            var space = false;
            foreach (var character in text)
            {
                if ((builder.Length & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (char.IsWhiteSpace(character))
                {
                    if (mode == WhitespaceMode.IgnoreChanges) space = true;
                    continue;
                }
                if (space && builder.Length > 0) builder.Append(' ');
                space = false;
                builder.Append(character);
            }
            text = builder.ToString();
        }
        if (IgnoreNumbers)
        {
            var builder = new StringBuilder(text.Length);
            for (var index = 0; index < text.Length; index++)
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (text[index] is not (>= '0' and <= '9')) builder.Append(text[index]);
            }
            text = builder.ToString();
        }
        return IgnoreCase ? text.ToUpperInvariant() : text;
    }

    internal Regex? CreateIgnoredLineRegex() => string.IsNullOrEmpty(IgnoreLinePattern) ? null :
        TextPreprocessor.CreateRegex(IgnoreLinePattern, true);
}

public sealed record InlineSpan(int Start, int Length);
public sealed record DiffRow(int? LeftLineNumber, int? RightLineNumber, string? LeftText,
    string? RightText, DiffKind Kind)
{
    public IReadOnlyList<InlineSpan> LeftSpans { get; init; } = [];
    public IReadOnlyList<InlineSpan> RightSpans { get; init; } = [];
}
public sealed record DiffBlock(int Index, int RowStart, int RowCount, int LeftStart,
    int LeftCount, int RightStart, int RightCount);
public sealed record TextSyncPoint(int LeftLineNumber, int RightLineNumber);
public sealed record DiffResult(string LeftText, string RightText, IReadOnlyList<DiffRow> Rows,
    IReadOnlyList<DiffBlock> Blocks)
{
    public bool HasDifferences => Blocks.Count != 0;
    public int InlineWorkUsed { get; init; }
    public int InlineFallbackCount { get; init; }
    public int LineWorkUsed { get; init; }
    public bool LineFallback { get; init; }
    public string? LineFallbackReason { get; init; }
}

internal readonly record struct TextLine(string Content, string Ending)
{
    public string Raw => Content + Ending;
}

internal static class TextLines
{
    // 最後の改行は空行へ置き換えず、各行の終端として保持する。
    internal static List<TextLine> Parse(string text, CancellationToken cancellationToken = default)
    {
        var result = new List<TextLine>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (text[index] is not ('\r' or '\n')) continue;
            var end = index;
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            result.Add(new(text[start..end], text[end..(index + 1)]));
            start = index + 1;
        }
        if (start < text.Length) result.Add(new(text[start..], ""));
        return result;
    }

    internal static string Join(IEnumerable<TextLine> lines) => string.Concat(lines.Select(line => line.Raw));
    // 別文書の最終行を途中へ移す場合も、前後の独立した行を連結しない。
    internal static void AppendSeparated(StringBuilder output, string text, string newline)
    {
        if (text.Length == 0) return;
        if (output.Length > 0 && output[^1] is not ('\r' or '\n')) output.Append(newline);
        output.Append(text);
    }

    internal static string JoinSeparated(IEnumerable<TextLine> lines, string newline)
    {
        var output = new StringBuilder();
        foreach (var line in lines) AppendSeparated(output, line.Raw, newline);
        return output.ToString();
    }
    internal static bool HasFinalNewLine(string text) => text.EndsWith('\n') || text.EndsWith('\r');
    internal static string NewLine(string text) => Parse(text).FirstOrDefault(line => line.Ending.Length > 0).Ending ?? Environment.NewLine;
}
