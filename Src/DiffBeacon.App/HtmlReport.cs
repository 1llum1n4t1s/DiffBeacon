using System.Globalization;
using System.Text;
using System.Text.Json;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public sealed record ReportDocument(string Name, string Text);

public static class HtmlReport
{
    private const string Header = "<!doctype html><html lang=\"ja\"><head><meta charset=\"utf-8\"><title>DiffBeacon 比較レポート</title><style>body{font-family:system-ui;margin:2rem}table{border-collapse:collapse;width:100%;table-layout:fixed}td,th{border:1px solid #aaa;padding:.4rem;vertical-align:top}pre{margin:0;white-space:pre-wrap;overflow-wrap:anywhere}.Added{background:#dff5df}.Deleted{background:#f9dddd}.Modified{background:#fff0c5}.inline-diff{background:#f4b76a;font-weight:600}.missing{color:#666;background:#eee}.line-number{display:block;color:#666;font-size:.85em}.options dd{overflow-wrap:anywhere}.table-scroll{overflow-x:auto}table.delimited{width:max-content;min-width:100%;table-layout:auto}.delimited td,.delimited th{min-width:8rem;max-width:28rem}.delimited .row-number{min-width:3rem}</style></head>";
    private static readonly string[] TwoSides = ["left", "right"];
    private static readonly string[] ThreeSides = ["left", "base", "right"];

    public static string Create(DiffResult result, string leftName, string rightName, int maxCharacters = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var html = new BoundedHtml(maxCharacters, cancellationToken);
        Begin(html, "Text", result.HasDifferences);
        html.Append("<p>差分ブロック数: "); html.Number(result.Blocks.Count); html.Append("</p>");
        RenderText(html, [new(leftName, result.LeftText), new(rightName, result.RightText)],
            TwoRows(result, cancellationToken), cancellationToken);
        return Finish(html);
    }

    public static string CreateText(IReadOnlyList<ReportDocument> documents, ComparisonOptions? options = null,
        int maxCharacters = int.MaxValue, CancellationToken cancellationToken = default) =>
        CreateTextReport(documents, options, maxCharacters, cancellationToken, "Text");

    public static string CreateJsonComparison(IReadOnlyList<ReportDocument> documents, ComparisonOptions? options = null,
        int maxCharacters = int.MaxValue, CancellationToken cancellationToken = default)
    {
        ValidateDocuments(documents, cancellationToken);
        // 極小の出力上限では正規化の大きな一時文書を作る前に拒否する。
        _ = Begin(new BoundedHtml(maxCharacters, cancellationToken), "Json", false);
        var normalized = new ReportDocument[documents.Count];
        for (var index = 0; index < documents.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            normalized[index] = documents[index] with
            {
                Text = StructuredComparer.NormalizeJson(documents[index].Text, cancellationToken: cancellationToken)
            };
        }
        return CreateTextReport(normalized, options, maxCharacters, cancellationToken, "Json");
    }

    private static string CreateTextReport(IReadOnlyList<ReportDocument> documents, ComparisonOptions? options,
        int maxCharacters, CancellationToken token, string mode)
    {
        ValidateDocuments(documents, token);
        var html = new BoundedHtml(maxCharacters, token);
        // true は false より短いため、未確定な属性でも最終出力の上限を先に消費しない。
        var differencePosition = Begin(html, mode, true);
        options ??= new();
        ValidateOptions(options);
        bool different;
        IEnumerable<TextReportRow> rows;
        if (documents.Count == 2)
        {
            var result = TextDiffer.Compare(documents[0].Text, documents[1].Text, options, token);
            different = result.HasDifferences;
            rows = TwoRows(result, token);
        }
        else
        {
            var ancestorLeft = TextDiffer.Compare(documents[1].Text, documents[0].Text, options, token);
            var ancestorRight = TextDiffer.Compare(documents[1].Text, documents[2].Text, options, token);
            var leftRight = TextDiffer.Compare(documents[0].Text, documents[2].Text, options, token);
            different = ancestorLeft.HasDifferences || ancestorRight.HasDifferences || leftRight.HasDifferences;
            rows = ThreeRows(ancestorLeft, ancestorRight, options, token);
        }
        html.SetDifferent(differencePosition, different);
        RenderOptions(html, options);
        RenderText(html, documents, rows, token);
        return Finish(html);
    }

    public static string CreateDelimited(IReadOnlyList<ReportDocument> documents, char delimiter = ',', char quote = '"',
        bool allowNewlinesInQuotes = true, ComparisonOptions? options = null, int maxCharacters = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        ValidateDocuments(documents, cancellationToken);
        var html = new BoundedHtml(maxCharacters, cancellationToken);
        // 上限が極小の場合は解析・比較する前に拒否する。
        var differencePosition = Begin(html, "Table", true);
        options ??= new(); ValidateOptions(options);
        var comparison = StructuredComparer.CompareTables(documents.Select(document => document.Text).ToArray(),
            new(delimiter, quote, allowNewlinesInQuotes), options, cancellationToken);
        html.SetDifferent(differencePosition, comparison.HasDifferences);
        RenderOptions(html, options);
        html.Append("<dl class=\"table-options\"><dt>Delimiter</dt><dd>"); html.Escape(delimiter.ToString());
        html.Append("</dd><dt>Quote</dt><dd>"); html.Escape(quote.ToString());
        html.Append("</dd><dt>AllowNewlinesInQuotes</dt><dd>"); html.Boolean(allowNewlinesInQuotes);
        html.Append("</dd><dt>AlignmentFallback</dt><dd>"); html.Boolean(comparison.AlignmentFallback); html.Append("</dd></dl>");
        DelimitedHeader(html, documents, comparison.ColumnCount);
        var sides = documents.Count == 2 ? TwoSides : ThreeSides;
        for (var row = 0; row < comparison.Rows.Count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            html.Append("<tr data-aligned-row=\""); html.Number(row + 1); html.Append("\">");
            for (var side = 0; side < comparison.Documents.Count; side++)
            {
                var sourceRow = comparison.GetSourceRow(side, row);
                html.Append("<th class=\"row-number\" scope=\"row\" data-side=\""); html.Append(sides[side]);
                html.Append("\" data-row=\""); if (sourceRow is int number) html.Number(number);
                html.Append("\" data-aligned-row=\""); html.Number(row + 1);
                html.Append("\" data-missing=\""); html.Boolean(sourceRow is null); html.Append("\">");
                if (sourceRow is int visible) html.Number(visible);
                html.Append("</th>");
                for (var column = 0; column < comparison.ColumnCount; column++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var cell = comparison.GetCell(side, row, column);
                    var kind = comparison.GetKind(side, row, column);
                    html.Append("<td data-side=\""); html.Append(sides[side]); html.Append("\" data-row=\"");
                    if (sourceRow is int source) html.Number(source);
                    html.Append("\" data-aligned-row=\""); html.Number(row + 1);
                    html.Append("\" data-column=\""); html.Number(column + 1); html.Append("\" data-missing=\""); html.Boolean(cell is null);
                    html.Append("\" class=\""); html.Append(kind.ToString()); if (cell is null) html.Append(" missing"); html.Append("\"><pre>");
                    if (cell is not null) html.Escape(cell.Value);
                    html.Append("</pre></td>");
                }
            }
            html.Append("</tr>");
        }
        html.Append("</tbody></table></div>");
        return Finish(html);
    }

    private static void DelimitedHeader(BoundedHtml html, IReadOnlyList<ReportDocument> documents, int columns)
    {
        var sides = documents.Count == 2 ? TwoSides : ThreeSides;
        html.Append("<div class=\"table-scroll\"><table class=\"delimited\"><thead><tr>");
        for (var side = 0; side < documents.Count; side++)
        {
            html.Append("<th data-side=\""); html.Append(sides[side]); html.Append("\" colspan=\""); html.Number(checked(columns + 1));
            html.Append("\" scope=\"colgroup\">"); html.Escape(documents[side].Name); html.Append("</th>");
        }
        html.Append("</tr><tr>");
        for (var side = 0; side < documents.Count; side++)
        {
            html.Append("<th class=\"row-number\" scope=\"col\">行</th>");
            for (var column = 0; column < columns; column++)
            {
                html.Append("<th scope=\"col\">列 "); html.Number(column + 1); html.Append("</th>");
            }
        }
        html.Append("</tr></thead><tbody>");
    }

    private static void ValidateDocuments(IReadOnlyList<ReportDocument> documents, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(documents);
        token.ThrowIfCancellationRequested();
        if (documents.Count is not (2 or 3)) throw new ArgumentException("レポートには 2 または 3 文書が必要です。", nameof(documents));
        foreach (var document in documents)
            if (document is null || document.Name is null || document.Text is null)
                throw new ArgumentException("レポート文書の名前と本文が必要です。", nameof(documents));
    }

    private static void ValidateOptions(ComparisonOptions options)
    {
        if (!Enum.IsDefined(options.CommentSyntax) || !Enum.IsDefined(options.Whitespace))
            throw new ArgumentException("コメント構文または空白の比較方式が不正です。", nameof(options));
    }

    private sealed record TextReportCell(int? Line, string? Text, DiffKind Kind, IReadOnlyList<InlineSpan> Spans);
    private sealed record TextReportRow(DiffKind Kind, TextReportCell[] Cells);
    private sealed record AnchoredBranch(Dictionary<int, DiffRow> Lines, Dictionary<int, List<DiffRow>> Insertions);

    private static IEnumerable<TextReportRow> TwoRows(DiffResult result, CancellationToken token)
    {
        foreach (var row in result.Rows)
        {
            token.ThrowIfCancellationRequested();
            yield return new(row.Kind,
            [new(row.LeftLineNumber, row.LeftText, row.Kind, row.LeftSpans), new(row.RightLineNumber, row.RightText, row.Kind, row.RightSpans)]);
        }
    }

    private static AnchoredBranch Anchor(DiffResult result, CancellationToken token)
    {
        var lines = new Dictionary<int, DiffRow>();
        var insertions = new Dictionary<int, List<DiffRow>>();
        var ancestorLine = 0;
        foreach (var row in result.Rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.LeftLineNumber is int line)
            {
                ancestorLine = line;
                lines.Add(line, row);
            }
            else
            {
                if (!insertions.TryGetValue(ancestorLine, out var group)) insertions.Add(ancestorLine, group = []);
                group.Add(row);
            }
        }
        return new(lines, insertions);
    }

    private static IEnumerable<TextReportRow> ThreeRows(DiffResult ancestorLeft, DiffResult ancestorRight,
        ComparisonOptions options, CancellationToken token)
    {
        var left = Anchor(ancestorLeft, token);
        var right = Anchor(ancestorRight, token);
        if (left.Lines.Count != right.Lines.Count) throw new InvalidDataException("祖先の行整列が一致しません。");
        for (var anchor = 0; anchor <= left.Lines.Count; anchor++)
        {
            token.ThrowIfCancellationRequested();
            var leftInsertions = left.Insertions.GetValueOrDefault(anchor) ?? [];
            var rightInsertions = right.Insertions.GetValueOrDefault(anchor) ?? [];
            if (leftInsertions.Count > 0 || rightInsertions.Count > 0)
            {
                var alignment = TextDiffer.Compare(InsertedText(leftInsertions, token), InsertedText(rightInsertions, token), options, token);
                foreach (var insertion in alignment.Rows)
                {
                    token.ThrowIfCancellationRequested();
                    var leftRow = insertion.LeftLineNumber is int li ? leftInsertions[li - 1] : null;
                    var rightRow = insertion.RightLineNumber is int ri ? rightInsertions[ri - 1] : null;
                    var kind = CombineKinds(leftRow?.Kind ?? DiffKind.Equal, rightRow?.Kind ?? DiffKind.Equal);
                    yield return new(kind,
                    [new(leftRow?.RightLineNumber, leftRow?.RightText, leftRow?.Kind ?? DiffKind.Equal,
                         leftRow?.Kind is not (null or DiffKind.Equal) ? insertion.LeftSpans : []),
                     new(null, null, kind, []),
                     new(rightRow?.RightLineNumber, rightRow?.RightText, rightRow?.Kind ?? DiffKind.Equal,
                         rightRow?.Kind is not (null or DiffKind.Equal) ? insertion.RightSpans : [])]);
                }
            }
            if (anchor == left.Lines.Count) continue;
            var leftLine = left.Lines[anchor + 1];
            var rightLine = right.Lines[anchor + 1];
            var rowKind = CombineKinds(leftLine.Kind, rightLine.Kind);
            yield return new(rowKind,
            [new(leftLine.RightLineNumber, leftLine.RightText, leftLine.Kind, leftLine.RightSpans),
             new(leftLine.LeftLineNumber, leftLine.LeftText, rowKind, MergeSpans(leftLine.LeftSpans, rightLine.LeftSpans, token)),
             new(rightLine.RightLineNumber, rightLine.RightText, rightLine.Kind, rightLine.RightSpans)]);
        }
    }

    private static string InsertedText(IReadOnlyList<DiffRow> rows, CancellationToken token)
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            // 終端を必ず付け、末尾空行も挿入区間の一行として保持する。
            text.Append(row.RightText).Append('\n');
        }
        return text.ToString();
    }

    private static DiffKind CombineKinds(DiffKind first, DiffKind second) => first == DiffKind.Equal ? second :
        second == DiffKind.Equal || first == second ? first : DiffKind.Modified;

    private static IReadOnlyList<InlineSpan> MergeSpans(IReadOnlyList<InlineSpan> left, IReadOnlyList<InlineSpan> right, CancellationToken token)
    {
        if (left.Count == 0) return right;
        if (right.Count == 0) return left;
        var sorted = left.Concat(right).OrderBy(span => span.Start).ToArray();
        var result = new List<InlineSpan>();
        foreach (var span in sorted)
        {
            token.ThrowIfCancellationRequested();
            if (result.Count > 0 && (long)result[^1].Start + result[^1].Length >= span.Start)
            {
                var previous = result[^1];
                result[^1] = new(previous.Start, Math.Max(previous.Start + previous.Length, span.Start + span.Length) - previous.Start);
            }
            else result.Add(span);
        }
        return result;
    }

    private static void RenderText(BoundedHtml html, IReadOnlyList<ReportDocument> documents, IEnumerable<TextReportRow> rows, CancellationToken token)
    {
        TableHeader(html, documents, false);
        var sides = documents.Count == 2 ? TwoSides : ThreeSides;
        var endings = documents.Select(document => LineEndings(document.Text, token)).ToArray();
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            html.Append("<tr class=\""); html.Append(row.Kind.ToString()); html.Append("\">");
            for (var index = 0; index < row.Cells.Length; index++)
            {
                var cell = row.Cells[index];
                html.Append("<td data-side=\""); html.Append(sides[index]); html.Append("\" data-line=\"");
                if (cell.Line is int line) html.Number(line);
                html.Append("\" data-missing=\""); html.Boolean(cell.Text is null); html.Append("\" data-ending=\"");
                if (cell.Line is int endingLine && endingLine <= endings[index].Count) html.Append(endings[index][endingLine - 1]);
                html.Append("\" class=\""); html.Append(cell.Kind.ToString()); if (cell.Text is null) html.Append(" missing"); html.Append("\"><span class=\"line-number\">");
                if (cell.Line is int number) html.Number(number);
                html.Append("</span><pre>");
                RenderSpans(html, cell.Text ?? "", cell.Spans);
                html.Append("</pre></td>");
            }
            html.Append("</tr>");
        }
        html.Append("</tbody></table>");
    }

    private static List<string> LineEndings(string text, CancellationToken token)
    {
        var result = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
            if (text[index] is not ('\r' or '\n')) continue;
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') { result.Add("CRLF"); index++; }
            else result.Add(text[index] == '\r' ? "CR" : "LF");
            start = index + 1;
        }
        if (start < text.Length) result.Add("None");
        return result;
    }

    private static void RenderSpans(BoundedHtml html, string text, IReadOnlyList<InlineSpan> spans)
    {
        var cursor = 0;
        foreach (var span in spans)
        {
            if (span.Start < cursor || span.Length < 0 || span.Start > text.Length || span.Length > text.Length - span.Start)
                throw new InvalidDataException("レポートの行内差分位置が不正です。");
            html.Escape(text.AsSpan(cursor, span.Start - cursor));
            html.Append("<span class=\"inline-diff\">"); html.Escape(text.AsSpan(span.Start, span.Length)); html.Append("</span>");
            cursor = span.Start + span.Length;
        }
        html.Escape(text.AsSpan(cursor));
    }

    private static int Begin(BoundedHtml html, string mode, bool different)
    {
        html.Append(Header); html.Append("<body data-mode=\""); html.Append(mode); html.Append("\" data-different=\"");
        var position = html.Length;
        html.Boolean(different); html.Append("\"><h1>DiffBeacon 比較レポート</h1>");
        return position;
    }

    private static void TableHeader(BoundedHtml html, IReadOnlyList<ReportDocument> documents, bool coordinates)
    {
        html.Append("<table><thead><tr>");
        if (coordinates) html.Append("<th>行 / 列</th>");
        var sides = documents.Count == 2 ? TwoSides : ThreeSides;
        for (var index = 0; index < documents.Count; index++)
        {
            html.Append("<th data-side=\""); html.Append(sides[index]); html.Append("\">"); html.Escape(documents[index].Name); html.Append("</th>");
        }
        html.Append("</tr></thead><tbody>");
    }

    private static void RenderOptions(BoundedHtml html, ComparisonOptions options)
    {
        html.Append("<details class=\"options\"><summary>比較設定</summary><dl>");
        void Option(string name, string value) { html.Append("<dt>"); html.Escape(name); html.Append("</dt><dd>"); html.Escape(value); html.Append("</dd>"); }
        string Flag(bool value) => value ? "true" : "false";
        Option("IgnoreCase", Flag(options.IgnoreCase)); Option("IgnoreWhitespace", Flag(options.IgnoreWhitespace));
        Option("Whitespace", options.Whitespace.ToString()); Option("IgnoreBlankLines", Flag(options.IgnoreBlankLines));
        Option("IgnoreLinePattern", options.IgnoreLinePattern ?? ""); Option("IgnoreNumbers", Flag(options.IgnoreNumbers));
        Option("CommentSyntax", options.CommentSyntax.ToString()); Option("IgnoreFinalNewLine", Flag(options.IgnoreFinalNewLine));
        Option("CompareLineEndings", Flag(options.CompareLineEndings)); Option("MaxFallbackComparisons", options.MaxFallbackComparisons.ToString(CultureInfo.InvariantCulture));
        html.Append("</dl><ol class=\"substitution-rules\">");
        foreach (var rule in options.SubstitutionRules)
        {
            html.Append("<li><dl>"); Option("Pattern", rule.Pattern); Option("Replacement", rule.Replacement);
            Option("MatchCase", Flag(rule.MatchCase)); Option("UseRegex", Flag(rule.UseRegex));
            Option("WholeWord", Flag(rule.WholeWord)); Option("Enabled", Flag(rule.Enabled)); html.Append("</dl></li>");
        }
        html.Append("</ol></details>");
    }

    private static string Finish(BoundedHtml html) { html.Append("</body></html>"); return html.ToString(); }

    private sealed class BoundedHtml
    {
        private readonly StringBuilder output = new();
        private readonly int maximum;
        private readonly CancellationToken token;
        public int Length => output.Length;

        public BoundedHtml(int maximum, CancellationToken token)
        {
            if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
            token.ThrowIfCancellationRequested();
            this.maximum = maximum; this.token = token;
        }

        private void Ensure(int count)
        {
            token.ThrowIfCancellationRequested();
            if (count > maximum - output.Length) throw new InvalidDataException("HTMLレポートのサイズ上限を超えました。");
        }

        public void Append(string text) { Ensure(text.Length); output.Append(text); }
        private void Append(ReadOnlySpan<char> text) { Ensure(text.Length); output.Append(text); }
        public void Number(int value) => Append(value.ToString(CultureInfo.InvariantCulture));
        public void Boolean(bool value) => Append(value ? "true" : "false");
        public void Escape(string? text) => Escape((text ?? "").AsSpan());

        public void Escape(ReadOnlySpan<char> text)
        {
            // 未変換の区間を小さくappendし、入力全体の巨大なescape済みコピーを作らない。
            var start = 0;
            for (var index = 0; index < text.Length; index++)
            {
                if ((index & 4095) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (index > start) { Append(text[start..index]); start = index; }
                }
                var entity = text[index] switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&#39;", _ => null };
                if (entity is null) continue;
                Append(text[start..index]); Append(entity); start = index + 1;
            }
            Append(text[start..]);
        }

        public void SetDifferent(int position, bool different)
        {
            if (different) return;
            Ensure(1);
            output.Replace("true", "false", position, 4);
        }

        public override string ToString() { token.ThrowIfCancellationRequested(); return output.ToString(); }
    }

    public static string CreateJson(DiffResult result, string leftName, string rightName)
    {
        ArgumentNullException.ThrowIfNull(result);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("leftName", leftName); writer.WriteString("rightName", rightName);
            writer.WriteBoolean("different", result.HasDifferences); writer.WriteNumber("blocks", result.Blocks.Count);
            writer.WriteStartArray("rows");
            foreach (var row in result.Rows)
            {
                writer.WriteStartObject(); writer.WriteString("kind", row.Kind.ToString());
                if (row.LeftLineNumber is int left) writer.WriteNumber("leftLine", left); else writer.WriteNull("leftLine");
                if (row.RightLineNumber is int right) writer.WriteNumber("rightLine", right); else writer.WriteNull("rightLine");
                writer.WriteString("left", row.LeftText); writer.WriteString("right", row.RightText); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
