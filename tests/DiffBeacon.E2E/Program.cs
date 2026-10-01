using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

var outputArgument = Option("--output") ?? "artifacts/e2e/local";
var output = Path.GetFullPath(outputArgument);
Directory.CreateDirectory(output);
var fixtures = Path.Combine(output, "fixtures", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(fixtures);
var app = Path.GetFullPath(Option("--app") ?? "Src/DiffBeacon.App/bin/Release/net10.0/DiffBeacon.dll");
var assertions = new List<Assertion>();
var commands = new List<CommandResult>();
var seededCases = new List<SeededCase>();
var links = new List<LinkEvidence>();
var utf8 = new UTF8Encoding(false);
var commandIndex = 0;

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    if (index < 0) return null;
    if (index + 1 >= args.Length) throw new ArgumentException($"値が必要です: {name}");
    return args[index + 1];
}

string Text(string name, string text, Encoding? encoding = null)
{
    var path = Path.Combine(fixtures, name);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text, encoding ?? utf8);
    return path;
}

string Zip(string name, params (string Entry, string Content)[] entries)
{
    var path = Path.Combine(fixtures, name);
    using var stream = File.Create(path);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    foreach (var (entryName, content) in entries)
    {
        using var entry = archive.CreateEntry(entryName).Open();
        entry.Write(utf8.GetBytes(content));
    }
    return path;
}

string Docx(string name, string text) => Zip(name,
    ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"),
    ("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
    ("word/document.xml", $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>{WebUtility.HtmlEncode(text)}</w:t></w:r></w:p></w:body></w:document>"));

string Xlsx(string name, string sharedStrings, string cells) => Zip(name,
    ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/></Types>"),
    ("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
    ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>"),
    ("xl/sharedStrings.xml", $"<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">{sharedStrings}</sst>"),
    ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\">{cells}</row></sheetData></worksheet>"));

string Tar(string name, params (string Entry, string Content)[] entries)
{
    var path = Path.Combine(fixtures, name);
    using var stream = File.Create(path);
    using var archive = new TarWriter(stream);
    foreach (var (entryName, content) in entries)
    {
        using var data = new MemoryStream(utf8.GetBytes(content));
        archive.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName) { DataStream = data });
    }
    return path;
}

void Check(string name, bool passed, string detail = "") => assertions.Add(new(name, passed ? "passed" : "failed", detail));
void Skip(string name, string detail) => assertions.Add(new(name, "skipped", detail));

string ExpectedProjectPath(string path, string directory)
{
    if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.StartsWith('/')
        || (path.Length > 1 && path[1] == ':') || path.StartsWith("\\\\", StringComparison.Ordinal)
        || (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")) return path;
    return Path.GetFullPath(Path.Combine(directory, path.Replace('\\', Path.DirectorySeparatorChar)));
}

async Task ReportCases()
{
    // 失敗条件は artifacts/verification/reports-contract/e2e-plan.md に実装前に固定。
    Dictionary<string, object?> Entry(string left, string right, string mode = "Text", string? ancestor = null) => new()
    { ["leftPath"] = left, ["rightPath"] = right, ["basePath"] = ancestor ?? "", ["mode"] = mode };
    string Project(string name, Dictionary<string, object?>[] entries, int active = 0) => Text("reports/" + name + ".json",
        JsonSerializer.Serialize(new { formatVersion = 1, entries, activeEntryIndex = active }));
    bool Tag(string html, string tag, params (string Name, string Value)[] attributes) => Regex.IsMatch(html,
        "<" + tag + @"\b" + string.Concat(attributes.Select(attribute => @"(?=[^>]*\b" + Regex.Escape(attribute.Name)
            + "=[\"']" + Regex.Escape(attribute.Value) + "[\"'])")) + "[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    string Visible(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", "", RegexOptions.CultureInvariant));
    void Verify(string name, string html, string mode, bool different, bool three = false)
    {
        Check(name + " format and difference", Tag(html, "body", ("data-mode", mode), ("data-different", different ? "true" : "false")));
        Check(name + " side columns", Tag(html, "th", ("data-side", "left")) && Tag(html, "th", ("data-side", "right"))
            && (three ? Tag(html, "th", ("data-side", "base")) : !Tag(html, "th", ("data-side", "base"))));
        Check(name + " standalone HTML", html.Contains("<!doctype html>", StringComparison.OrdinalIgnoreCase)
            && !Regex.IsMatch(html, @"\b(?:src|href)\s*=\s*[""'](?!#)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && !html.Contains("@import", StringComparison.OrdinalIgnoreCase) && !html.Contains("url(", StringComparison.OrdinalIgnoreCase));
    }
    async Task<string> Render(string name, string project, params string[] options)
    {
        var destination = Path.Combine(fixtures, "reports", name + ".html");
        var result = await Run("report-" + name, 0, true, new[] { "--report-project", project, destination }.Concat(options).ToArray());
        Check("report " + name + " output exists", File.Exists(destination));
        if (result.ExitCode == 0)
        {
            using var metadata = JsonDocument.Parse(result.Stdout);
            using var source = JsonDocument.Parse(File.ReadAllText(project));
            var explicitIndex = Array.IndexOf(options, "--entry");
            var expectedEntry = explicitIndex >= 0 ? int.Parse(options[explicitIndex + 1], System.Globalization.CultureInfo.InvariantCulture)
                : source.RootElement.GetProperty("activeEntryIndex").GetInt32() + 1;
            Check("report " + name + " metadata", metadata.RootElement.GetProperty("output").GetString() == destination
                && metadata.RootElement.GetProperty("entry").GetInt32() == expectedEntry);
        }
        return File.Exists(destination) ? File.ReadAllText(destination) : "";
    }
    async Task<string> Pack(string name, string project)
    {
        var destination = Path.Combine(fixtures, "reports", name + ".zip");
        await Run("report-package-" + name, 0, true, "--package-project", project, destination, "--report");
        Check("report package " + name + " ZIP exists", File.Exists(destination));
        if (!File.Exists(destination)) return "";
        using var archive = ZipFile.OpenRead(destination); var entry = archive.GetEntry("report.files/1.html");
        Check("report package " + name + " HTML entry", entry is not null);
        if (entry is null) return "";
        using var stream = entry.Open(); using var reader = new StreamReader(stream, Encoding.UTF8);
        var html = await reader.ReadToEndAsync();
        Text("reports/" + name + "-bcl-zip.html", html);
        return html;
    }
    async Task Reject(string name, string project, params string[] options)
    {
        var destination = Text("reports/rejected/" + name + ".html", "protected HTML output\n");
        var bytes = File.ReadAllBytes(destination);
        await Run("report-reject-" + name, 2, false, new[] { "--report-project", project, destination }.Concat(options).ToArray());
        Check("report rejection preserves " + name, File.ReadAllBytes(destination).SequenceEqual(bytes));
    }
    async Task RejectPackage(string name, string project)
    {
        var destination = Text("reports/rejected/" + name + ".zip", "protected report package\n");
        var bytes = File.ReadAllBytes(destination);
        await Run("report-package-reject-" + name, 2, false, "--package-project", project, destination, "--report");
        Check("report package rejection preserves " + name, File.ReadAllBytes(destination).SequenceEqual(bytes));
    }
    void VerifyThreeAlignment(string name, string html, string[] leftLines, string[] baseLines, string[] rightLines,
        params (int Base, int? Left, int? Right)[] anchors)
    {
        var rows = new List<Dictionary<string, (string Side, int? Line, bool Missing, string Content)>>();
        foreach (Match rowMatch in Regex.Matches(html, @"<tr\b[^>]*>(?<row>[\s\S]*?)</tr>", RegexOptions.CultureInvariant))
        {
            var row = new Dictionary<string, (string Side, int? Line, bool Missing, string Content)>(StringComparer.Ordinal);
            foreach (Match cellMatch in Regex.Matches(rowMatch.Groups["row"].Value, @"<td\b(?<attributes>[^>]*)>(?<content>[\s\S]*?)</td>", RegexOptions.CultureInvariant))
            {
                string Attribute(string attribute) => WebUtility.HtmlDecode(Regex.Match(cellMatch.Groups["attributes"].Value,
                    @"\b" + Regex.Escape(attribute) + "=[\"'](?<value>[^\"']*)[\"']", RegexOptions.CultureInvariant).Groups["value"].Value);
                var side = Attribute("data-side");
                var number = Attribute("data-line");
                var validMissing = bool.TryParse(Attribute("data-missing"), out var missing);
                int? line = int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
                var pre = Regex.Match(cellMatch.Groups["content"].Value, @"<pre\b[^>]*>(?<value>[\s\S]*?)</pre>", RegexOptions.CultureInvariant);
                var content = Visible(pre.Groups["value"].Value);
                Check(name + " parsed side cell", (side is "left" or "base" or "right") && validMissing && pre.Success
                    && (missing ? number == "" && content == "" : line is > 0), $"side={side}; line={number}; missing={missing}");
                Check(name + " unique side in row", row.TryAdd(side, (side, line, missing, content)), side);
            }
            // thだけの見出し行は比較行に含めない。
            if (row.Count == 0) continue;
            Check(name + " three cells per comparison row", row.Count == 3 && new[] { "left", "base", "right" }.All(row.ContainsKey));
            rows.Add(row);
        }
        Text("reports/" + name + "-parsed-rows.json", JsonSerializer.Serialize(rows.Select((row, index) => new
        { row = index + 1, cells = row.Values.Select(cell => new { cell.Side, cell.Line, cell.Missing, cell.Content }) }), new JsonSerializerOptions { WriteIndented = true }));
        foreach (var (side, expected) in new[] { ("left", leftLines), ("base", baseLines), ("right", rightLines) })
        {
            var cells = rows.Where(row => row.ContainsKey(side)).Select(row => row[side]).Where(cell => !cell.Missing).ToArray();
            Check(name + " " + side + " original line numbers retained", cells.Select(cell => cell.Line).SequenceEqual(Enumerable.Range(1, expected.Length).Select(line => (int?)line)));
            Check(name + " " + side + " all original contents retained", cells.Select(cell => cell.Content).SequenceEqual(expected), $"expected-lines={expected.Length}; actual-lines={cells.Length}");
        }
        foreach (var anchor in anchors)
        {
            var matching = rows.Where(row => row.TryGetValue("base", out var cell) && !cell.Missing && cell.Line == anchor.Base).ToArray();
            Check(name + " ancestor anchor " + anchor.Base + " aligned", matching.Length == 1
                && matching[0].TryGetValue("left", out var leftCell) && leftCell.Line == anchor.Left && leftCell.Missing == (anchor.Left is null)
                && matching[0].TryGetValue("right", out var rightCell) && rightCell.Line == anchor.Right && rightCell.Missing == (anchor.Right is null),
                $"base={anchor.Base}; left={anchor.Left}; right={anchor.Right}");
        }
        if (baseLines.Length == 0) Check(name + " empty ancestor remains missing", rows.Count > 0
            && rows.All(row => row.TryGetValue("base", out var cell) && cell.Missing && cell.Line is null));
    }
    string VerifyTableAlignment(string name, string html, string[][] leftRows, string[][]? baseRows, string[][] rightRows, string[] expectedMap)
    {
        var mapped = new List<List<(string Side, int? Original, int Aligned, int Column, bool Missing, string? Value)>>();
        string Attribute(string attributes, string name) => WebUtility.HtmlDecode(Regex.Match(attributes,
            @"\b" + Regex.Escape(name) + "=[\"'](?<value>[^\"']*)[\"']", RegexOptions.CultureInvariant).Groups["value"].Value);
        foreach (Match rowMatch in Regex.Matches(html, @"<tr\b[^>]*>(?<row>[\s\S]*?)</tr>", RegexOptions.CultureInvariant))
        {
            var cells = new List<(string Side, int? Original, int Aligned, int Column, bool Missing, string? Value)>();
            foreach (Match cell in Regex.Matches(rowMatch.Groups["row"].Value, @"<td\b(?<attributes>[^>]*)>(?<content>[\s\S]*?)</td>", RegexOptions.CultureInvariant))
            {
                var attributes = cell.Groups["attributes"].Value; var side = Attribute(attributes, "data-side");
                var originalAttribute = Attribute(attributes, "data-row");
                int? original = int.TryParse(originalAttribute, out var parsedRow) ? parsedRow : null;
                var validAligned = int.TryParse(Attribute(attributes, "data-aligned-row"), out var aligned);
                var validColumn = int.TryParse(Attribute(attributes, "data-column"), out var column);
                var validMissing = bool.TryParse(Attribute(attributes, "data-missing"), out var missing);
                var pre = Regex.Match(cell.Groups["content"].Value, @"<pre\b[^>]*>(?<value>[\s\S]*?)</pre>", RegexOptions.CultureInvariant);
                Check(name + " table coordinate attributes", (side is "left" or "base" or "right") && validAligned && aligned > 0
                    && validColumn && column > 0 && validMissing && pre.Success && (original is > 0 || originalAttribute == ""));
                cells.Add((side, original, aligned, column, missing, missing ? null : Visible(pre.Groups["value"].Value)));
            }
            if (cells.Count == 0) continue;
            var display = mapped.Count + 1;
            var sides = baseRows is null ? new[] { "left", "right" } : new[] { "left", "base", "right" };
            Check(name + " table row display index", cells.All(cell => cell.Aligned == display));
            foreach (var side in sides)
            {
                var sideCells = cells.Where(cell => cell.Side == side).OrderBy(cell => cell.Column).ToArray();
                Check(name + " " + side + " column order", sideCells.Length > 0
                    && sideCells.Select(cell => cell.Column).SequenceEqual(Enumerable.Range(1, sideCells.Length))
                    && sideCells.Select(cell => cell.Original).Distinct().Count() == 1);
                var original = sideCells.FirstOrDefault().Original;
                Check(name + " " + side + " row header mapping", Tag(rowMatch.Value, "th", ("data-side", side),
                    ("data-row", original?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""),
                    ("data-aligned-row", display.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                if (original is null) Check(name + " " + side + " ghost cells missing", sideCells.All(cell => cell.Missing));
            }
            mapped.Add(cells);
        }
        foreach (var (side, expectedRows) in new[] { ("left", leftRows), ("base", baseRows), ("right", rightRows) })
        {
            if (expectedRows is null) continue;
            var originals = mapped.Select(row => row.Where(cell => cell.Side == side).OrderBy(cell => cell.Column).ToArray())
                .Where(cells => cells.Length > 0 && cells[0].Original is not null).ToArray();
            Check(name + " " + side + " every original row once in order", originals.Select(cells => cells[0].Original)
                .SequenceEqual(Enumerable.Range(1, expectedRows.Length).Select(row => (int?)row)));
            Check(name + " " + side + " full decoded cells preserved", originals.Length == expectedRows.Length
                && originals.Select((cells, row) => cells.Length >= expectedRows[row].Length
                    && cells.Take(expectedRows[row].Length).All(cell => !cell.Missing)
                    && cells.Take(expectedRows[row].Length).Select(cell => cell.Value).SequenceEqual(expectedRows[row])
                    && cells.Skip(expectedRows[row].Length).All(cell => cell.Missing && cell.Value is null)).All(passed => passed));
        }
        string Original(List<(string Side, int? Original, int Aligned, int Column, bool Missing, string? Value)> cells, string side)
            => cells.FirstOrDefault(cell => cell.Side == side).Original?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
        Check(name + " table matched anchors and ghosts", mapped.Select(row => Original(row, "left") + "/" + Original(row, "base") + "/" + Original(row, "right")).SequenceEqual(expectedMap));
        var signature = JsonSerializer.Serialize(mapped.Select((row, index) => new { aligned = index + 1,
            cells = row.OrderBy(cell => cell.Side, StringComparer.Ordinal).ThenBy(cell => cell.Column)
                .Select(cell => new { cell.Side, cell.Original, cell.Aligned, cell.Column, cell.Missing, cell.Value }) }));
        Text("reports/" + name + "-mapped-table.json", signature);
        return signature;
    }

    var left = Text("reports/text-left.txt", "prefix LEFT suffix\n<script>left</script>\n", new UTF8Encoding(true));
    var right = Text("reports/text-right.txt", "prefix RIGHT suffix\n<script>right</script>\n", new UTF8Encoding(true));
    var ancestor = Text("reports/text-base.txt", "prefix ANCESTORONLY suffix\n<script>base</script>\n");
    var textEntry = Entry(left, right);
    textEntry["leftDescription"] = "左 & <label>"; textEntry["rightDescription"] = "右 <script>description</script>";
    var textProject = Project("text-project", [textEntry]);
    foreach (var (name, html) in new[] { ("text", await Render("text", textProject)), ("text-package", await Pack("text", textProject)) })
    {
        Verify("report " + name, html, "Text", true);
        Check("report " + name + " escaped descriptions and content", html.Contains("&lt;label&gt;", StringComparison.Ordinal)
            && html.Contains("&lt;script&gt;", StringComparison.Ordinal) && !html.Contains("<script>", StringComparison.Ordinal)
            && Visible(html).Contains("左 & <label>", StringComparison.Ordinal));
        Check("report " + name + " line numbers", Tag(html, "td", ("data-side", "left"), ("data-line", "1"))
            && Tag(html, "td", ("data-side", "right"), ("data-line", "2")));
        Check("report " + name + " inline spans", Regex.IsMatch(html, @"<span\b[^>]*class=[""'][^""']*\binline-diff\b", RegexOptions.CultureInvariant));
    }
    var threeEntry = Entry(left, right, ancestor: ancestor); threeEntry["baseDescription"] = "祖先 & <base-label>";
    var threeProject = Project("three-text-project", [threeEntry]);
    foreach (var (name, html) in new[] { ("three-text", await Render("three-text", threeProject)), ("three-text-package", await Pack("three-text", threeProject)) })
    {
        Verify("report " + name, html, "Text", true, true);
        Check("report " + name + " ancestor content and description", Visible(html).Contains("ANCESTORONLY", StringComparison.Ordinal)
            && html.Contains("&lt;base-label&gt;", StringComparison.Ordinal) && Tag(html, "td", ("data-side", "base"), ("data-line", "1")));
    }
    foreach (var (name, baseLines, leftLines, rightLines, anchors) in new (string, string[], string[], string[], (int Base, int? Left, int? Right)[])[]
    {
        ("ancestor-anchors", ["head", "anchor", "tail"], ["before", "head", "left-insert", "anchor", "tail", "left-end"],
            ["head", "right-insert", "anchor", "right-tail", "right-end"], [(1, 2, 1), (2, 4, 3), (3, 5, 4)]),
        ("consecutive-empty-lines", ["head", "", "", "anchor", "tail"], ["before", "head", "", "", "left-insert", "anchor", "tail"],
            ["head", "", "", "anchor", "tail", "right-end"], [(1, 2, 1), (2, 3, 2), (3, 4, 3), (4, 6, 4), (5, 7, 5)]),
        ("one-sided-deletion", ["head", "remove", "anchor", "tail"], ["head", "anchor", "tail"],
            ["head", "remove", "anchor", "tail"], [(1, 1, 1), (2, null, 2), (3, 2, 3), (4, 3, 4)]),
        ("empty-ancestor-insertions", [], ["left<&>", "", ""], ["right<&>", "", "right-end", ""], [])
    })
    {
        string Document(string side, string[] lines) => Text("reports/alignment-" + name + "-" + side + ".txt", lines.Length == 0 ? "" : string.Join('\n', lines) + "\n");
        var project = Project("alignment-" + name, [Entry(Document("left", leftLines), Document("right", rightLines), ancestor: Document("base", baseLines))]);
        foreach (var (route, html) in new[] { ("standalone", await Render("alignment-" + name, project)), ("package", await Pack("alignment-" + name, project)) })
        {
            Verify("report alignment " + name + " " + route, html, "Text", true, true);
            VerifyThreeAlignment("alignment-" + name + "-" + route, html, leftLines, baseLines, rightLines, anchors);
        }
    }
    var same = Text("reports/same.txt", "same left and right\n");
    var ancestorOnlyProject = Project("ancestor-only", [Entry(same, same, ancestor: ancestor)]);
    Verify("report ancestor-only difference", await Render("ancestor-only", ancestorOnlyProject), "Text", true, true);
    var normalizedLeft = Text("reports/normalized-left.txt", "alpha  123\n"); var normalizedRight = Text("reports/normalized-right.txt", "ALPHA 456\n");
    var normalizedEntry = Entry(normalizedLeft, normalizedRight); normalizedEntry["ignoreCase"] = true; normalizedEntry["ignoreNumbers"] = true; normalizedEntry["whitespace"] = 2;
    var normalizedProject = Project("normalized-text-project", [normalizedEntry]);
    Verify("report text normalized equality", await Render("normalized-text", normalizedProject), "Text", false);
    Verify("report packaged text normalized equality", await Pack("normalized-text", normalizedProject), "Text", false);
    var legacyOutput = Path.Combine(fixtures, "reports", "legacy.html");
    await Run("report-legacy-cli", 0, true, "--report", left, right, legacyOutput);
    Check("report legacy CLI output", File.Exists(legacyOutput) && File.ReadAllText(legacyOutput).Contains("&lt;script&gt;", StringComparison.Ordinal));
    if (File.Exists(legacyOutput)) Verify("report legacy shared renderer", File.ReadAllText(legacyOutput), "Text", true);
    var originalLeftBytes = File.ReadAllBytes(left);
    await Run("report-legacy-protected-source", 2, false, "--report", left, right, left);
    Check("report legacy source output preserved", File.ReadAllBytes(left).SequenceEqual(originalLeftBytes));
    var fallbackEntry = Entry(left, right); fallbackEntry["leftDescription"] = "  "; fallbackEntry["rightDescription"] = "";
    var fallbackHtml = await Render("description-fallback", Project("description-fallback-project", [fallbackEntry]));
    Check("report blank descriptions fall back to paths", Visible(fallbackHtml).Contains(Path.GetFileName(left), StringComparison.Ordinal)
        && Visible(fallbackHtml).Contains(Path.GetFileName(right), StringComparison.Ordinal));

    var tableLeft = Text("reports/table-left.txt", "id;value\r\none;'hello;world'\r\ntwo;'multi\nline'\r\n");
    var tableRight = Text("reports/table-right.txt", "id;value\r\none;'hello;changed'\r\ntwo;'multi\nline'\r\n");
    var tableEntry = Entry(tableLeft, tableRight, "Table");
    tableEntry["tableDelimiter"] = ";"; tableEntry["tableQuote"] = "'"; tableEntry["tableAllowNewlinesInQuotes"] = true;
    var tableProject = Project("table-project", [tableEntry]);
    foreach (var (name, html) in new[] { ("table", await Render("table", tableProject)), ("table-package", await Pack("table", tableProject)) })
    {
        Verify("report " + name, html, "Table", true);
        Check("report " + name + " real cell coordinates", Tag(html, "td", ("data-side", "left"), ("data-row", "2"), ("data-column", "2"), ("data-missing", "false"))
            && Tag(html, "td", ("data-side", "right"), ("data-row", "3"), ("data-column", "2")));
        Check("report " + name + " custom quoted content", Visible(html).Contains("hello;world", StringComparison.Ordinal)
            && Visible(html).Contains("hello;changed", StringComparison.Ordinal) && Visible(html).Contains("multi\nline", StringComparison.Ordinal));
    }
    var missingLeft = Text("reports/empty-cell.csv", "id,value\none,\n"); var missingRight = Text("reports/missing-cell.csv", "id,value\none\n");
    var missingProject = Project("missing-cell-project", [Entry(missingLeft, missingRight, "Table")]);
    foreach (var (name, html) in new[] { ("missing-cell", await Render("missing-cell", missingProject)), ("missing-cell-package", await Pack("missing-cell", missingProject)) })
    {
        Verify("report " + name, html, "Table", true);
        Check("report " + name + " missing differs from empty", Tag(html, "td", ("data-side", "left"), ("data-row", "2"), ("data-column", "2"), ("data-missing", "false"))
            && Tag(html, "td", ("data-side", "right"), ("data-row", "2"), ("data-column", "2"), ("data-missing", "true")));
    }
    var optionLeft = Text("reports/table-options-left.csv", "name,value\nALPHA  X,build=123\n");
    var optionRight = Text("reports/table-options-right.csv", "name,value\nalpha x,build=456\n");
    var optionEntry = Entry(optionLeft, optionRight, "Table"); optionEntry["ignoreCase"] = true; optionEntry["ignoreNumbers"] = true; optionEntry["whitespace"] = 2;
    var optionProject = Project("table-options-project", [optionEntry]);
    Verify("report table normalized options", await Render("table-options", optionProject), "Table", false);
    Verify("report table packaged normalized options", await Pack("table-options", optionProject), "Table", false);
    var tabLeft = Text("reports/table-tab-left.txt", "id\tvalue\none\tleft\n"); var tabRight = Text("reports/table-tab-right.txt", "id\tvalue\none\tright\n");
    var tabProject = Project("table-tab-project", [Entry(tabLeft, tabRight, "Table")]);
    foreach (var (name, html) in new[] { ("table-tab", await Render("table-tab", tabProject)), ("table-tab-package", await Pack("table-tab", tabProject)) })
    {
        Verify("report " + name, html, "Table", true);
        Check("report " + name + " inferred tab separator", Tag(html, "td", ("data-side", "right"), ("data-row", "2"), ("data-column", "2")));
    }
    var tableBase = Text("reports/table-base.txt", "id;value\r\none;'BASEONLY;cell'\r\ntwo;'multi\nline'\r\n");
    var tableThree = new Dictionary<string, object?>(tableEntry) { ["basePath"] = tableBase, ["baseDescription"] = "祖先セル" };
    var tableThreeProject = Project("table-three-project", [tableThree]);
    foreach (var (name, html) in new[] { ("table-three", await Render("table-three", tableThreeProject)), ("table-three-package", await Pack("table-three", tableThreeProject)) })
    {
        Verify("report " + name, html, "Table", true, true);
        Check("report " + name + " ancestor cell", Tag(html, "td", ("data-side", "base"), ("data-row", "2"), ("data-column", "2"))
            && Visible(html).Contains("BASEONLY;cell", StringComparison.Ordinal));
    }
    var tableAncestorOnly = new Dictionary<string, object?>(tableThree) { ["rightPath"] = tableLeft };
    var tableAncestorProject = Project("table-ancestor-only-project", [tableAncestorOnly]);
    Verify("report table ancestor-only difference", await Render("table-ancestor-only", tableAncestorProject), "Table", true, true);
    Verify("report packaged table ancestor-only difference", await Pack("table-ancestor-only", tableAncestorProject), "Table", true, true);
    foreach (var (name, leftRows, baseRows, rightRows, expectedMap) in new (string, string[][], string[][]?, string[][], string[])[]
    {
        ("two-middle-insert", [["id", "value"], ["head", "H"], ["insert", "L"], ["anchor", "A"], ["tail", "T"]], null,
            [["id", "value"], ["head", "H"], ["anchor", "A"], ["tail", "T"]], ["1/-/1", "2/-/2", "3/-/-", "4/-/3", "5/-/4"]),
        ("two-middle-delete", [["id", "value"], ["head", "H"], ["anchor", "A"], ["tail", "T"]], null,
            [["id", "value"], ["head", "H"], ["remove", "R"], ["anchor", "A"], ["tail", "T"]], ["1/-/1", "2/-/2", "-/-/3", "3/-/4", "4/-/5"]),
        ("three-independent-insertions", [["id", "value"], ["head", "H"], ["left-only", "L"], ["anchor", "A"], ["tail", "T"]],
            [["id", "value"], ["head", "H"], ["anchor", "A"], ["tail", "T"]],
            [["id", "value"], ["head", "H"], ["anchor", "A"], ["right-only", "R"], ["tail", "T"]], ["1/1/1", "2/2/2", "3/-/-", "4/3/3", "-/-/4", "5/4/5"]),
        ("three-same-insertion", [["id", "value"], ["head", "H"], ["shared", "S"], ["anchor", "A"]],
            [["id", "value"], ["head", "H"], ["anchor", "A"]],
            [["id", "value"], ["head", "H"], ["shared", "S"], ["anchor", "A"]], ["1/1/1", "2/2/2", "3/-/3", "4/3/4"]),
        ("three-ancestor-only-change", [["id", "value"], ["head", "H"], ["anchor", "NEW"]],
            [["id", "value"], ["head", "H"], ["anchor", "OLD"]],
            [["id", "value"], ["head", "H"], ["anchor", "NEW"]], ["1/1/1", "2/2/2", "3/3/3"])
    })
    {
        string Document(string side, string[][] rows) => Text("reports/aligned-table-" + name + "-" + side + ".csv", string.Join('\n', rows.Select(row => string.Join(',', row))) + "\n");
        var leftPath = Document("left", leftRows); var rightPath = Document("right", rightRows);
        var project = Project("aligned-table-" + name, [Entry(leftPath, rightPath, "Table", baseRows is null ? null : Document("base", baseRows))]);
        var html = await Render("aligned-table-" + name, project); var packedHtml = await Pack("aligned-table-" + name, project);
        Verify("report aligned table " + name, html, "Table", true, baseRows is not null);
        Verify("report packaged aligned table " + name, packedHtml, "Table", true, baseRows is not null);
        var standaloneMap = VerifyTableAlignment("aligned-table-" + name + "-standalone", html, leftRows, baseRows, rightRows, expectedMap);
        var packageMap = VerifyTableAlignment("aligned-table-" + name + "-package", packedHtml, leftRows, baseRows, rightRows, expectedMap);
        Check("report table standalone and package maps identical " + name, standaloneMap == packageMap);
        if (baseRows is null)
        {
            var result = await Run("table-aligned-cli-" + name, 1, true, "--table", leftPath, rightPath);
            if (result.ExitCode == 1)
            {
                using var data = JsonDocument.Parse(result.Stdout);
                Check("table CLI original and aligned row counts " + name, data.RootElement.GetProperty("rows").GetInt32() == Math.Max(leftRows.Length, rightRows.Length)
                    && data.RootElement.GetProperty("alignedRows").GetInt32() == expectedMap.Length
                    && data.RootElement.GetProperty("cols").GetInt32() == 2 && data.RootElement.GetProperty("different").GetBoolean()
                    && !data.RootElement.GetProperty("alignmentFallback").GetBoolean());
                string SourceRow(JsonElement row, string side) => row.GetProperty(side).ValueKind == JsonValueKind.Null ? "-"
                    : row.GetProperty(side).GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
                Check("table CLI source mapping matches report " + name, data.RootElement.GetProperty("mapping").EnumerateArray()
                    .Select(row => SourceRow(row, "left") + "/" + SourceRow(row, "right"))
                    .SequenceEqual(expectedMap.Select(row => row.Split('/')[0] + "/" + row.Split('/')[2])));
            }
        }
    }
    // この全変更区間は旧4096本文文字上限を超え、元順序のfallbackへ入る。
    var fallbackLeft = Text("reports/table-fallback-left.csv", string.Concat(Enumerable.Repeat("left-only\n", 600)));
    var fallbackRight = Text("reports/table-fallback-right.csv", string.Concat(Enumerable.Repeat("right-only\n", 600)));
    var fallbackHashes = new[] { fallbackLeft, fallbackRight }.ToDictionary(path => path,
        path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
    var fallback = await Run("table-gap-capacity-fallback", 1, true, "--table", fallbackLeft, fallbackRight);
    if (fallback.ExitCode == 1)
    {
        using var data = JsonDocument.Parse(fallback.Stdout); var root = data.RootElement;
        Check("table bounded gap fallback reported", root.GetProperty("alignmentFallback").GetBoolean()
            && root.GetProperty("different").GetBoolean() && root.GetProperty("rows").GetInt32() == 600
            && root.GetProperty("alignedRows").GetInt32() == 600 && root.GetProperty("cols").GetInt32() == 1);
        var mapping = root.GetProperty("mapping").EnumerateArray().ToArray();
        foreach (var side in new[] { "left", "right" })
            Check("table fallback every " + side + " original row once in order", mapping.Select(row => row.GetProperty(side).ValueKind == JsonValueKind.Number
                ? row.GetProperty(side).GetInt32() : -1).SequenceEqual(Enumerable.Range(1, 600)));
    }
    var rowLimitInput = Text("reports/table-over-row-limit.csv", string.Concat(Enumerable.Repeat("x\n", 262145)));
    var cellLimitInput = Text("reports/table-over-cell-limit.csv", string.Join(',', Enumerable.Repeat("x", 1048577)) + "\n");
    var tableCapacityInputs = new List<object>();
    foreach (var (name, path, logicalRows, cells) in new[]
        { ("row-limit", rowLimitInput, 262145, 262145), ("cell-limit", cellLimitInput, 1, 1048577) })
    {
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        tableCapacityInputs.Add(new { name, path, logicalRows, cells, length = new FileInfo(path).Length, sha256 = before });
        // 同じ入力でも解析上限は省略せず検査する。
        var result = await Run("table-reject-" + name, 2, false, "--table", path, path);
        Check("table " + name + " emits no success JSON", string.IsNullOrWhiteSpace(result.Stdout));
        Check("table " + name + " diagnostic retained", !string.IsNullOrWhiteSpace(result.Stderr));
        Check("table " + name + " original input unchanged", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == before);
    }
    foreach (var path in new[] { fallbackLeft, fallbackRight })
    {
        Check("table fallback original input unchanged " + Path.GetFileName(path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == fallbackHashes[path]);
        tableCapacityInputs.Add(new { name = "gap-fallback", path, logicalRows = 600, cells = 600, length = new FileInfo(path).Length, sha256 = fallbackHashes[path] });
    }
    Text("reports/table-capacity-inputs.json", JsonSerializer.Serialize(tableCapacityInputs, new JsonSerializerOptions { WriteIndented = true }));
    var quotedLeft = Text("reports/aligned-quoted-left.txt", "id;value;extra\r\nhead;H;\r\n'line\r\none';'say ''same''; <tag>'\r\nanchor;A;\r\n");
    var quotedRight = Text("reports/aligned-quoted-right.txt", "id;value;extra\r\nhead;H;\r\ninsert;I;\r\n'line\r\none';'say ''same''; <tag>';\r\nanchor;A;\r\n");
    var quotedEntry = Entry(quotedLeft, quotedRight, "Table"); quotedEntry["tableDelimiter"] = ";"; quotedEntry["tableQuote"] = "'"; quotedEntry["tableAllowNewlinesInQuotes"] = true;
    var quotedProject = Project("aligned-quoted-project", [quotedEntry]);
    string[][] quotedLeftRows = [["id", "value", "extra"], ["head", "H", ""], ["line\r\none", "say 'same'; <tag>"], ["anchor", "A", ""]];
    string[][] quotedRightRows = [["id", "value", "extra"], ["head", "H", ""], ["insert", "I", ""], ["line\r\none", "say 'same'; <tag>", ""], ["anchor", "A", ""]];
    var quotedMap = new[] { "1/-/1", "2/-/2", "-/-/3", "3/-/4", "4/-/5" };
    var quotedHtml = await Render("aligned-quoted", quotedProject); var quotedPackedHtml = await Pack("aligned-quoted", quotedProject);
    var quotedSignature = VerifyTableAlignment("aligned-quoted-standalone", quotedHtml, quotedLeftRows, null, quotedRightRows, quotedMap);
    var quotedPackedSignature = VerifyTableAlignment("aligned-quoted-package", quotedPackedHtml, quotedLeftRows, null, quotedRightRows, quotedMap);
    Check("report quoted logical rows standalone and package maps identical", quotedSignature == quotedPackedSignature);
    Check("report quoted trailing empty differs from missing", Tag(quotedHtml, "td", ("data-side", "left"), ("data-row", "3"), ("data-aligned-row", "4"), ("data-column", "3"), ("data-missing", "true"))
        && Tag(quotedHtml, "td", ("data-side", "right"), ("data-row", "4"), ("data-aligned-row", "4"), ("data-column", "3"), ("data-missing", "false")));
    var fullOptionLeft = Text("reports/table-full-options-left.csv", "id,value,environment\nGENERATED-alpha,build=123,prod\nanchor,value  X,prod\n");
    var fullOptionRight = Text("reports/table-full-options-right.csv", "id,value,environment\ngenerated-beta,build=456,dev\nANCHOR,VALUE X,dev\n");
    var fullOptionEntry = Entry(fullOptionLeft, fullOptionRight, "Table"); fullOptionEntry["ignoreCase"] = true; fullOptionEntry["ignoreNumbers"] = true;
    fullOptionEntry["whitespace"] = 2; fullOptionEntry["ignoreLinePattern"] = "(?i)^generated-";
    fullOptionEntry["substitutionRules"] = new[] { new { pattern = "prod|dev", replacement = "ENV", matchCase = false, useRegex = true, wholeWord = true, enabled = true } };
    var fullOptionProject = Project("table-full-options-project", [fullOptionEntry]);
    var fullOptionHtml = await Render("table-full-options", fullOptionProject); var fullOptionPack = await Pack("table-full-options", fullOptionProject);
    Verify("report table full options", fullOptionHtml, "Table", false); Verify("report packaged table full options", fullOptionPack, "Table", false);
    string[][] fullLeftRows = [["id", "value", "environment"], ["GENERATED-alpha", "build=123", "prod"], ["anchor", "value  X", "prod"]];
    string[][] fullRightRows = [["id", "value", "environment"], ["generated-beta", "build=456", "dev"], ["ANCHOR", "VALUE X", "dev"]];
    var fullMap = new[] { "1/-/1", "2/-/2", "3/-/3" };
    Check("report normalized table retains raw original cells and package mapping", VerifyTableAlignment("table-full-options-standalone", fullOptionHtml, fullLeftRows, null, fullRightRows, fullMap)
        == VerifyTableAlignment("table-full-options-package", fullOptionPack, fullLeftRows, null, fullRightRows, fullMap));

    var jsonLeft = Text("reports/json-left.json", "{\"b\":2,\"a\":{\"x\":1,\"y\":\"<script>value</script>\"}}");
    var jsonSame = Text("reports/json-same.json", "{\"a\":{\"y\":\"<script>value</script>\",\"x\":1},\"b\":2}");
    var jsonChanged = Text("reports/json-changed.json", "{\"a\":{\"x\":9,\"y\":\"<script>value</script>\"},\"b\":2}");
    foreach (var (name, other, different) in new[] { ("json-property-order", jsonSame, false), ("json-value-change", jsonChanged, true) })
    {
        var project = Project(name + "-project", [Entry(jsonLeft, other, "Json")]);
        Verify("report " + name, await Render(name, project), "Json", different);
        Verify("report packaged " + name, await Pack(name, project), "Json", different);
    }
    var jsonThreeProject = Project("json-three-project", [Entry(jsonLeft, jsonSame, "Json", jsonChanged)]);
    Verify("report Json ancestor-only difference", await Render("json-three", jsonThreeProject), "Json", true, true);
    Verify("report packaged Json ancestor-only difference", await Pack("json-three", jsonThreeProject), "Json", true, true);
    var activeProject = Project("active-selection", [Entry(left, right), Entry(missingLeft, missingRight, "Table")], 1);
    Verify("report default active selection", await Render("default-active", activeProject), "Table", true);
    Verify("report explicit one-based selection", await Render("explicit-entry", activeProject, "--entry", "1"), "Text", true);
    foreach (var entry in new[] { "0", "3", "-1", "abc" }) await Reject("entry-" + entry, activeProject, "--entry", entry);
    await Reject("entry-value-missing", activeProject, "--entry"); await Reject("entry-duplicate", activeProject, "--entry", "1", "--entry", "2");
    await Reject("unknown-option", activeProject, "--unknown");
    var malformedQuote = Text("reports/bad-quote.csv", "id,value\nrow,\"unclosed\n");
    var badQuoteProject = Project("bad-quote-project", [Entry(malformedQuote, missingRight, "Table")]);
    await Reject("table-invalid-quote", badQuoteProject); await RejectPackage("table-invalid-quote", badQuoteProject);
    var badTableAncestorProject = Project("bad-table-ancestor-project", [Entry(missingLeft, missingRight, "Table", malformedQuote)]);
    await Reject("table-invalid-ancestor", badTableAncestorProject); await RejectPackage("table-invalid-ancestor", badTableAncestorProject);
    var noNewlines = new Dictionary<string, object?>(tableEntry) { ["tableAllowNewlinesInQuotes"] = false };
    await Reject("table-quoted-newline-disabled", Project("table-no-newline-project", [noNewlines]));
    var badJson = Text("reports/bad.json", "{\"broken\":");
    var badJsonAncestorProject = Project("bad-json-ancestor-project", [Entry(jsonLeft, jsonSame, "Json", badJson)]);
    await Reject("json-invalid-ancestor", badJsonAncestorProject); await RejectPackage("json-invalid-ancestor", badJsonAncestorProject);
    foreach (var mode in new[] { "Binary", "Archive", "Provider", "Folder" })
    {
        var project = Project("unsupported-" + mode, [Entry(left, right, mode)]);
        await Reject("unsupported-" + mode.ToLowerInvariant(), project);
        if (mode == "Folder") await RejectPackage("unsupported-folder", project);
        else
        {
            var metadata = await Pack("metadata-" + mode.ToLowerInvariant(), project);
            Check("report packaged " + mode + " explicitly marks metadata fallback", metadata.Contains("詳細な比較レポートは未対応", StringComparison.Ordinal)
                && metadata.Contains("SHA-256", StringComparison.Ordinal));
        }
    }
    await Reject("url-source", Project("url-source-project", [Entry("https://example.invalid/source", right)]));
    var escaped = Text("reports/too-large-report.txt", new string('<', 4 * 1024 * 1024));
    await Reject("generated-capacity", Project("generated-capacity-project", [Entry(escaped, escaped)]));
    var largeInput = Path.Combine(fixtures, "reports", "too-large-input.txt"); using (var stream = File.Create(largeInput)) stream.SetLength(256L * 1024 * 1024 + 1);
    await Reject("input-capacity", Project("input-capacity-project", [Entry(largeInput, right)]));
    var readOnly = Text("reports/rejected/readonly.html", "protected readonly HTML\n"); var readOnlyBytes = File.ReadAllBytes(readOnly); var attributes = File.GetAttributes(readOnly);
    try
    {
        File.SetAttributes(readOnly, attributes | FileAttributes.ReadOnly);
        await Run("report-readonly-output", 2, false, "--report-project", textProject, readOnly);
        Check("report readonly output preserved", File.ReadAllBytes(readOnly).SequenceEqual(readOnlyBytes));
    }
    finally { File.SetAttributes(readOnly, attributes); }
    var protectedFilter = Text("reports/protected-filter.html", "name: filter\ndef: include\n");
    var protectedEntry = Entry(left, right, ancestor: ancestor); protectedEntry["fileFilterPath"] = protectedFilter;
    var protectedProject = Project("protected-project", [protectedEntry, Entry(jsonLeft, jsonSame, "Json")]);
    foreach (var (name, path) in new[] { ("left", left), ("right", right), ("base", ancestor), ("unselected", jsonLeft), ("filter", protectedFilter), ("project", protectedProject) })
    {
        var bytes = File.ReadAllBytes(path);
        await Run("report-protected-" + name, 2, false, "--report-project", protectedProject, path);
        Check("report protected source preserved " + name, File.ReadAllBytes(path).SequenceEqual(bytes));
    }
    var readOnlyFolderLeft = Path.Combine(fixtures, "reports", "readonly-folder-left");
    var readOnlyFolderRight = Path.Combine(fixtures, "reports", "readonly-folder-right");
    Directory.CreateDirectory(readOnlyFolderLeft); Directory.CreateDirectory(readOnlyFolderRight);
    var folderEntry = Entry(readOnlyFolderLeft, readOnlyFolderRight, "Folder"); folderEntry["leftReadOnly"] = true;
    var readOnlyFolderProject = Project("unselected-readonly-folder-project", [Entry(left, right), folderEntry]);
    var existingFolderHtml = Text("reports/readonly-folder-left/existing.html", "unselected readonly folder HTML\n");
    var existingFolderHtmlBytes = File.ReadAllBytes(existingFolderHtml);
    await Run("report-unselected-readonly-folder-existing", 2, false, "--report-project", readOnlyFolderProject, existingFolderHtml);
    Check("report unselected readonly folder existing HTML preserved", File.ReadAllBytes(existingFolderHtml).SequenceEqual(existingFolderHtmlBytes));
    var newFolderHtml = Path.Combine(readOnlyFolderLeft, "new.html");
    await Run("report-unselected-readonly-folder-new", 2, false, "--report-project", readOnlyFolderProject, newFolderHtml);
    Check("report unselected readonly folder creates no new HTML", !File.Exists(newFolderHtml));
    var existingFolderZip = Text("reports/readonly-folder-left/existing.zip", "unselected readonly folder ZIP\n");
    var existingFolderZipBytes = File.ReadAllBytes(existingFolderZip);
    await Run("report-package-unselected-readonly-folder", 2, false, "--package-project", readOnlyFolderProject, existingFolderZip, "--entries", "1");
    Check("report package unselected readonly folder existing ZIP preserved", File.ReadAllBytes(existingFolderZip).SequenceEqual(existingFolderZipBytes));
    try
    {
        var target = Text("reports/link-target/output.html", "protected linked HTML\n"); var bytes = File.ReadAllBytes(target);
        var fileLink = Path.Combine(fixtures, "reports", "linked-output.html"); File.CreateSymbolicLink(fileLink, target);
        links.Add(new(fileLink, new FileInfo(fileLink).LinkTarget!, false, IsDirectory: false));
        await Run("report-linked-output", 2, false, "--report-project", textProject, fileLink);
        Check("report linked output preserves target", File.ReadAllBytes(target).SequenceEqual(bytes));
        await Reject("linked-input", Project("linked-input-project", [Entry(fileLink, right)]));
        var directoryLink = Path.Combine(fixtures, "reports", "linked-parent"); Directory.CreateSymbolicLink(directoryLink, Path.GetDirectoryName(target)!);
        links.Add(new(directoryLink, new DirectoryInfo(directoryLink).LinkTarget!, false));
        await Run("report-linked-parent-output", 2, false, "--report-project", textProject, Path.Combine(directoryLink, "output.html"));
        Check("report linked parent preserves target", File.ReadAllBytes(target).SequenceEqual(bytes));
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
    { assertions.Add(new("report link protection", "skipped", exception.Message)); }
    Check("report owned temporary output removed", !Directory.EnumerateFiles(Path.Combine(fixtures, "reports"), "*.tmp", SearchOption.AllDirectories).Any()
        && !Directory.EnumerateDirectories(Path.Combine(fixtures, "reports"), ".diffbeacon-package-*", SearchOption.AllDirectories).Any());
}

async Task PackagingCases()
{
    // 包装前に固定する失敗条件: 選択/active/全設定の欠落、相対名の誤変換、
    // 同一原本の重複・別原本の衝突、URL取得、BOM/改行破損、拒否時の原本/出力破壊。
    Dictionary<string, object?> Entry(string left, string right, string? ancestor = null, string mode = "Text") => new()
    { ["leftPath"] = left, ["basePath"] = ancestor ?? "", ["rightPath"] = right, ["mode"] = mode };
    string Project(string name, Dictionary<string, object?>[] entries, int active = 0) => Text("packaging/" + name + ".json",
        JsonSerializer.Serialize(new { formatVersion = 1, entries, activeEntryIndex = active }));
    Dictionary<string, byte[]> ReadZip(string name, string path)
    {
        Check(name + " archive exists", File.Exists(path), path);
        if (!File.Exists(path)) return [];
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var content = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries.Where(entry => !entry.FullName.EndsWith('/')))
            {
                using var stream = entry.Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes);
                Check(name + " unique entry " + entry.FullName, content.TryAdd(entry.FullName, bytes.ToArray()));
            }
            Check(name + " safe relative ZIP names", content.Keys.All(key => !key.StartsWith('/') && !key.Contains('\\')
                && !key.Split('/').Any(part => part is ".." or ".") && !(key.Length > 1 && key[1] == ':')));
            return content;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        { Check(name + " BCL ZIP decode", false, exception.Message); return []; }
    }
    void Bytes(string name, Dictionary<string, byte[]> content, string entry, string original) =>
        Check(name + " bytes " + entry, content.TryGetValue(entry, out var bytes) && bytes.SequenceEqual(File.ReadAllBytes(original)));
    async Task Reject(string name, string project, params string[] options)
    {
        var destination = Text("packaging/rejected/" + name + ".zip", "existing package output\n");
        var bytes = File.ReadAllBytes(destination);
        await Run("packaging-reject-" + name, 2, false, new[] { "--package-project", project, destination }.Concat(options).ToArray());
        Check("packaging reject preserves " + name, File.Exists(destination) && File.ReadAllBytes(destination).SequenceEqual(bytes));
    }
    JsonElement[] Entries(JsonElement project) => project.TryGetProperty("entries", out var entries) ? entries.EnumerateArray().ToArray() : [project];
    async Task<JsonDocument?> ExtractAndReopen(string name, string archive)
    {
        var extracted = Path.Combine(fixtures, "packaging", name + "-extracted");
        await Run("packaging-extract-" + name, 0, true, "--archive-extract", archive, extracted);
        var reopened = Path.Combine(fixtures, "packaging", name + "-reopened.json");
        await Run("packaging-reopen-" + name, 0, true, "--project-copy", Path.Combine(extracted, "project.json"), reopened);
        Check("packaging reopened project " + name, File.Exists(reopened));
        return File.Exists(reopened) ? JsonDocument.Parse(File.ReadAllText(reopened)) : null;
    }

    var left = Text("packaging/input-left/左 & one.txt", "<script>left</script>\r\nanchor\r\nlast", new UTF8Encoding(true));
    var right = Text("packaging/input-right/右 & one.txt", "<script>right</script>\r\nanchor\r\nlast", new UTF8Encoding(true));
    var filter = Text("packaging/filters/example.flt", "name: Packaging fixture\ndef: include\nf: .*\\.txt$\n");
    var detailed = Entry(left, right);
    detailed["fileFilterPath"] = filter; detailed["leftDescription"] = "左 & <説明>"; detailed["baseDescription"] = "祖先";
    detailed["rightDescription"] = "右"; detailed["leftReadOnly"] = true; detailed["baseReadOnly"] = true; detailed["rightReadOnly"] = true;
    detailed["recursive"] = false; detailed["folderMode"] = "Hash"; detailed["excludedPaths"] = "cache;generated";
    detailed["legacyFilter"] = "*.txt"; detailed["tableDelimiter"] = ";"; detailed["tableQuote"] = "\"";
    detailed["tableAllowNewlinesInQuotes"] = true; detailed["ignoreCase"] = true; detailed["ignoreNumbers"] = true;
    detailed["ignoreWhitespace"] = false; detailed["providerId"] = "external-do-not-execute";
    detailed["ignoreBlankLines"] = true; detailed["ignoreLinePattern"] = "^generated$"; detailed["commentSyntax"] = 0;
    detailed["whitespace"] = 1; detailed["substitutionRules"] = new[] { new { pattern = "[", replacement = "disabled", matchCase = false, useRegex = true, wholeWord = false, enabled = false } };
    detailed["legacySettings"] = new Dictionary<string, string> { ["unpacker"] = "DO_NOT_EXECUTE.exe", ["prediffer"] = "DO_NOT_EXECUTE_TOO.exe" };
    var single = Project("single", [detailed]);
    var singleArchive = Path.Combine(fixtures, "packaging", "single.zip");
    await Run("packaging-single-all-options", 0, true, "--package-project", single, singleArchive, "--report", "--patch");
    var singleContent = ReadZip("packaging single", singleArchive);
    var leftName = "original/" + Path.GetFileName(left); var rightName = "altered/" + Path.GetFileName(right);
    Check("packaging single exact file names", singleContent.Keys.Order().SequenceEqual(new[]
        { leftName, rightName, "filters/1-example.flt", "report.html", "report.files/1.html", "patch.diff", "project.json" }.Order()));
    Bytes("packaging single", singleContent, leftName, left); Bytes("packaging single", singleContent, rightName, right);
    Bytes("packaging single", singleContent, "filters/1-example.flt", filter);
    if (singleContent.TryGetValue("report.files/1.html", out var htmlBytes))
    {
        var html = Encoding.UTF8.GetString(htmlBytes);
        Check("packaging report escapes document markup", html.Contains("&lt;script&gt;", StringComparison.Ordinal) && !html.Contains("<script>", StringComparison.Ordinal));
    }
    if (singleContent.TryGetValue("patch.diff", out var patchBytes))
    {
        var patch = Encoding.UTF8.GetString(patchBytes);
        Check("packaging patch relative header", patch.Contains(leftName, StringComparison.Ordinal) && patch.Contains(rightName, StringComparison.Ordinal)
            && !patch.Contains(Path.GetDirectoryName(left)!, StringComparison.Ordinal) && !patch.Contains(Path.GetDirectoryName(right)!, StringComparison.Ordinal));
    }
    if (singleContent.TryGetValue("project.json", out var projectBytes))
    {
        using var embedded = JsonDocument.Parse(projectBytes);
        var entry = embedded.RootElement.GetProperty("entries")[0];
        Check("packaging project portable references", entry.GetProperty("leftPath").GetString() == leftName
            && entry.GetProperty("rightPath").GetString() == rightName && entry.GetProperty("fileFilterPath").GetString() == "filters/1-example.flt");
        using var expected = JsonDocument.Parse(JsonSerializer.Serialize(detailed));
        Check("packaging project all settings preserved", expected.RootElement.EnumerateObject()
            .Where(property => property.Name is not ("leftPath" or "basePath" or "rightPath" or "fileFilterPath"))
            .All(property => entry.TryGetProperty(property.Name, out var value) && JsonElement.DeepEquals(property.Value, value)));
    }
    using (var reopened = await ExtractAndReopen("single", singleArchive))
    {
        if (reopened is not null)
        {
            var entry = Entries(reopened.RootElement)[0];
            var extracted = Path.Combine(fixtures, "packaging", "single-extracted");
            var extractedLeft = entry.GetProperty("leftPath").GetString()!; var extractedRight = entry.GetProperty("rightPath").GetString()!;
            Check("packaging reopen resolves against extraction", extractedLeft == Path.Combine(extracted, "original", Path.GetFileName(left))
                && extractedRight == Path.Combine(extracted, "altered", Path.GetFileName(right))
                && entry.GetProperty("fileFilterPath").GetString() == Path.Combine(extracted, "filters", "1-example.flt"));
            await Run("packaging-reopened-compare", 1, true, "--compare", extractedLeft, extractedRight);
            var applied = Path.Combine(fixtures, "packaging", "single-applied.txt");
            await Run("packaging-reopened-patch", 0, true, "--patch-apply", extractedLeft, Path.Combine(extracted, "patch.diff"), applied);
            Check("packaging patch preserves BOM CRLF final newline", File.Exists(applied) && File.ReadAllBytes(applied).SequenceEqual(File.ReadAllBytes(right)));
        }
    }

    var aLeft = Text("packaging/tree-left/a/same.txt", "a left\n"); var bLeft = Text("packaging/tree-left/b/same.txt", "b left\n");
    var aRight = Text("packaging/tree-right/a/same.txt", "a right\n"); var bRight = Text("packaging/tree-right/b/same.txt", "b right\n");
    var a = Entry(aLeft, aRight); a["leftDescription"] = "first";
    var b = Entry(bLeft, bRight); b["leftDescription"] = "second";
    var multi = Project("multiple", [a, b, a], 2);
    var multiArchive = Path.Combine(fixtures, "packaging", "multiple.zip");
    await Run("packaging-selection-order", 0, true, "--package-project", multi, multiArchive, "--entries", "2,1,3", "--report");
    var multiContent = ReadZip("packaging multiple", multiArchive);
    Check("packaging common parents and shared input dedupe", multiContent.Keys.Where(key => key.StartsWith("original/") || key.StartsWith("altered/"))
        .Order().SequenceEqual(new[] { "original/a/same.txt", "original/b/same.txt", "altered/a/same.txt", "altered/b/same.txt" }.Order()));
    Bytes("packaging multiple", multiContent, "original/a/same.txt", aLeft); Bytes("packaging multiple", multiContent, "original/b/same.txt", bLeft);
    Bytes("packaging multiple", multiContent, "altered/a/same.txt", aRight); Bytes("packaging multiple", multiContent, "altered/b/same.txt", bRight);
    Check("packaging selected reports", Enumerable.Range(1, 3).All(index => multiContent.ContainsKey($"report.files/{index}.html")));
    if (multiContent.TryGetValue("report.html", out var indexHtml))
    {
        var text = Encoding.UTF8.GetString(indexHtml);
        Check("packaging report index portable links", Enumerable.Range(1, 3).All(index => text.Contains($"report.files/{index}.html", StringComparison.Ordinal))
            && !text.Contains(fixtures, StringComparison.Ordinal));
    }
    using (var reopened = await ExtractAndReopen("multiple", multiArchive))
    {
        if (reopened is not null)
        {
            var entries = Entries(reopened.RootElement);
            Check("packaging selection order and active retained", entries.Length == 3 && reopened.RootElement.GetProperty("activeEntryIndex").GetInt32() == 2
                && entries.Select(entry => entry.GetProperty("leftDescription").GetString()).SequenceEqual(new[] { "second", "first", "first" })
                && entries[1].GetProperty("leftPath").GetString() == entries[2].GetProperty("leftPath").GetString());
            foreach (var (entry, index) in entries.Select((entry, index) => (entry, index)))
                await Run("packaging-multiple-compare-" + index, 1, true, "--compare", entry.GetProperty("leftPath").GetString()!, entry.GetProperty("rightPath").GetString()!);
        }
    }
    var inactiveArchive = Path.Combine(fixtures, "packaging", "inactive-selection.zip");
    await Run("packaging-active-not-selected", 0, true, "--package-project", multi, inactiveArchive, "--entries", "2,1");
    var inactiveContent = ReadZip("packaging inactive selection", inactiveArchive);
    if (inactiveContent.TryGetValue("project.json", out var inactiveProject))
    {
        using var document = JsonDocument.Parse(inactiveProject);
        Check("packaging unselected active resets to zero", document.RootElement.GetProperty("activeEntryIndex").GetInt32() == 0);
    }
    var multiPatchArchive = Path.Combine(fixtures, "packaging", "multiple-patches.zip");
    await Run("packaging-multiple-patches", 0, true, "--package-project", multi, multiPatchArchive, "--entries", "2,1", "--patch");
    var multiPatchContent = ReadZip("packaging multiple patches", multiPatchArchive);
    if (multiPatchContent.TryGetValue("patch.diff", out var multiplePatchBytes))
    {
        var patch = Encoding.UTF8.GetString(multiplePatchBytes);
        var first = patch.IndexOf("--- original/b/same.txt", StringComparison.Ordinal);
        var second = patch.IndexOf("--- original/a/same.txt", StringComparison.Ordinal);
        Check("packaging patch sections follow selection order", first >= 0 && second > first && !patch.Contains(fixtures, StringComparison.Ordinal));
    }

    var ancestor = Text("packaging/three/base.txt", "base\n");
    var triple = Project("three", [Entry(left, right, ancestor)]);
    var tripleArchive = Path.Combine(fixtures, "packaging", "three.zip");
    await Run("packaging-three-way", 0, true, "--package-project", triple, tripleArchive);
    var tripleContent = ReadZip("packaging three", tripleArchive);
    Bytes("packaging three", tripleContent, "1/" + Path.GetFileName(left), left);
    Bytes("packaging three", tripleContent, "2/base.txt", ancestor);
    Bytes("packaging three", tripleContent, "3/" + Path.GetFileName(right), right);
    using (var reopened = await ExtractAndReopen("three", tripleArchive))
    {
        if (reopened is not null)
        {
            var entry = Entries(reopened.RootElement)[0];
            Check("packaging three reopened ancestor bytes", File.Exists(entry.GetProperty("basePath").GetString())
                && File.ReadAllBytes(entry.GetProperty("basePath").GetString()!).SequenceEqual(File.ReadAllBytes(ancestor)));
            await Run("packaging-three-reopened-compare", 1, true, "--compare", entry.GetProperty("leftPath").GetString()!, entry.GetProperty("rightPath").GetString()!);
        }
    }

    foreach (var (name, options, expectedNames) in new (string, string[], string[])[]
    {
        ("documents-only", ["--no-project"], [leftName, rightName]),
        ("project-only", ["--no-documents"], ["project.json", "filters/1-example.flt"]),
        ("report-only", ["--no-documents", "--no-project", "--report"], ["report.html", "report.files/1.html"]),
        ("patch-only", ["--no-documents", "--no-project", "--patch"], ["patch.diff"])
    })
    {
        var destination = Path.Combine(fixtures, "packaging", name + ".zip");
        await Run("packaging-" + name, 0, true, new[] { "--package-project", single, destination }.Concat(options).ToArray());
        var content = ReadZip("packaging " + name, destination);
        Check("packaging options exact files " + name, content.Keys.Order().SequenceEqual(expectedNames.Order()));
        if (name == "project-only" && content.TryGetValue("project.json", out var projectOnly))
        {
            using var document = JsonDocument.Parse(projectOnly); var entry = document.RootElement.GetProperty("entries")[0];
            Check("packaging project only preserves original document references", entry.GetProperty("leftPath").GetString() == Path.GetFullPath(left)
                && entry.GetProperty("rightPath").GetString() == Path.GetFullPath(right) && entry.GetProperty("fileFilterPath").GetString() == "filters/1-example.flt");
            Bytes("packaging project only", content, "filters/1-example.flt", filter);
        }
    }
    var tableLeft = Text("packaging/table/left.csv", "name,value\r\nalpha,1\r\n", new UnicodeEncoding(false, true));
    var tableRight = Text("packaging/table/right.csv", "name,value\r\nalpha,2\r\n", new UnicodeEncoding(false, true));
    var tableArchive = Path.Combine(fixtures, "packaging", "table.zip");
    await Run("packaging-table-patch", 0, true, "--package-project", Project("table", [Entry(tableLeft, tableRight, mode: "Table")]), tableArchive, "--patch");
    var tableContent = ReadZip("packaging table", tableArchive);
    Bytes("packaging table", tableContent, "original/left.csv", tableLeft); Bytes("packaging table", tableContent, "altered/right.csv", tableRight);
    using (var reopened = await ExtractAndReopen("table", tableArchive))
    {
        if (reopened is not null)
        {
            var entry = Entries(reopened.RootElement)[0];
            await Run("packaging-table-reopened-compare", 1, true, "--table", entry.GetProperty("leftPath").GetString()!, entry.GetProperty("rightPath").GetString()!);
            var applied = Path.Combine(fixtures, "packaging", "table-applied.csv");
            await Run("packaging-table-reopened-patch", 0, true, "--patch-apply", entry.GetProperty("leftPath").GetString()!, Path.Combine(fixtures, "packaging", "table-extracted", "patch.diff"), applied);
            Check("packaging table patch preserves UTF16 BOM and CRLF", File.Exists(applied) && File.ReadAllBytes(applied).SequenceEqual(File.ReadAllBytes(tableRight)));
        }
    }
    var urls = Project("urls", [Entry("https://example.invalid/left?a=1&b=2", "http://example.invalid/right", mode: "Web")]);
    var urlArchive = Path.Combine(fixtures, "packaging", "urls.zip");
    await Run("packaging-url-reference-only", 0, true, "--package-project", urls, urlArchive);
    var urlContent = ReadZip("packaging URL", urlArchive);
    Check("packaging URLs create no fetched documents", urlContent.Count == 1 && urlContent.ContainsKey("project.json"));
    if (urlContent.TryGetValue("project.json", out var urlProject))
    {
        using var document = JsonDocument.Parse(urlProject); var entry = document.RootElement.GetProperty("entries")[0];
        Check("packaging URL references retained", entry.GetProperty("leftPath").GetString() == "https://example.invalid/left?a=1&b=2"
            && entry.GetProperty("rightPath").GetString() == "http://example.invalid/right");
    }
    await Reject("url-patch", urls, "--patch");
    await Reject("url-report", urls, "--report");
    foreach (var indices in new[] { "", "0", "4", "-1", "abc", "2,2" }) await Reject("indices-" + (indices.Length == 0 ? "empty" : indices.Replace(',', '-')), multi, "--entries", indices);
    await Reject("all-options-disabled", single, "--no-documents", "--no-project");
    await Reject("missing-side", Project("missing-side", [Entry(Path.Combine(fixtures, "packaging", "missing.txt"), right)]));
    await Reject("empty-side", Project("empty-side", [Entry("", right)]));
    await Reject("folder-mode", Project("folder-mode", [Entry(Path.GetDirectoryName(left)!, Path.GetDirectoryName(right)!, mode: "Folder")]));
    var binaryArchive = Path.Combine(fixtures, "packaging", "binary-empty-patch.zip");
    await Run("packaging-binary-empty-patch", 0, true, "--package-project", Project("binary-patch", [Entry(left, right, mode: "Binary")]), binaryArchive, "--patch");
    var binaryContent = ReadZip("packaging binary patch", binaryArchive);
    Check("packaging no Text or Table comparison creates empty patch", binaryContent.TryGetValue("patch.diff", out var emptyPatch) && emptyPatch.Length == 0);
    await Reject("invalid-project", Text("packaging/invalid.json", "{\"formatVersion\":99,\"entries\":[]}"));
    var huge = Path.Combine(fixtures, "packaging", "too-large.txt");
    using (var stream = File.Create(huge)) stream.SetLength(256L * 1024 * 1024 + 1);
    await Reject("large-document", Project("large-document", [Entry(huge, right)]));
    var escapedReport = Text("packaging/escaped-report.txt", new string('<', 4 * 1024 * 1024));
    await Reject("generated-report-limit", Project("generated-report-limit", [Entry(escapedReport, escapedReport)]), "--report");
    var autoTar = Tar("packaging/auto.tar", ("nested/value.txt", "archive bytes are not text\n"));
    var autoPackage = Path.Combine(fixtures, "packaging", "auto-archive.zip");
    await Run("packaging-auto-archive", 0, true, "--package-project", Project("auto-archive", [Entry(autoTar, autoTar, mode: "Auto")]), autoPackage, "--patch");
    var autoContent = ReadZip("packaging auto archive", autoPackage);
    Check("packaging Auto archive creates empty patch rather than decoding bytes", autoContent.TryGetValue("patch.diff", out var autoPatch) && autoPatch.Length == 0);
    foreach (var extension in new[] { ".7z", ".tar.gz", ".tar.bz2" })
    {
        var formatArchive = Path.Combine(fixtures, "packaging", "format" + extension);
        await Run("packaging-format" + extension, 0, true, "--package-project", single, formatArchive, "--report", "--patch");
        using var reopened = await ExtractAndReopen("format" + extension.Replace('.', '-'), formatArchive);
        if (reopened is null) continue;
        var entry = Entries(reopened.RootElement).Single();
        var restored = entry.GetProperty("leftPath").GetString()!;
        Check("packaging alternate format preserves document bytes " + extension, File.ReadAllBytes(restored).SequenceEqual(File.ReadAllBytes(left)));
    }
    var unsupportedOutput = Text("packaging/rejected/unsupported.txt", "keep unsupported output\n");
    var unsupportedBytes = File.ReadAllBytes(unsupportedOutput);
    await Run("packaging-unsupported-output", 2, false, "--package-project", single, unsupportedOutput);
    Check("packaging unsupported output preserved", File.ReadAllBytes(unsupportedOutput).SequenceEqual(unsupportedBytes));
    var original = Text("packaging/original.zip", "original text input\n"); var originalBytes = File.ReadAllBytes(original);
    await Run("packaging-original-output", 2, false, "--package-project", Project("original-output", [Entry(original, right)]), original);
    Check("packaging original document preserved", File.ReadAllBytes(original).SequenceEqual(originalBytes));
    await Run("packaging-unselected-original-output", 2, false, "--package-project", Project("unselected-original-output", [Entry(left, right), Entry(original, right)]), original, "--entries", "1");
    Check("packaging unselected original document preserved", File.ReadAllBytes(original).SequenceEqual(originalBytes));
    var projectOriginal = Text("packaging/project-original.zip", File.ReadAllText(single));
    var projectOriginalBytes = File.ReadAllBytes(projectOriginal);
    await Run("packaging-original-project-output", 2, false, "--package-project", projectOriginal, projectOriginal);
    Check("packaging source project preserved", File.ReadAllBytes(projectOriginal).SequenceEqual(projectOriginalBytes));
    var readOnly = Text("packaging/rejected/readonly.zip", "protected output\n"); var readOnlyBytes = File.ReadAllBytes(readOnly);
    var readOnlyAttributes = File.GetAttributes(readOnly);
    try
    {
        File.SetAttributes(readOnly, readOnlyAttributes | FileAttributes.ReadOnly);
        await Run("packaging-readonly-output", 2, false, "--package-project", single, readOnly);
        Check("packaging readonly output preserved", File.ReadAllBytes(readOnly).SequenceEqual(readOnlyBytes));
    }
    finally { File.SetAttributes(readOnly, readOnlyAttributes); }
    try
    {
        var target = Text("packaging/link-target/output.zip", "protected linked output\n"); var bytes = File.ReadAllBytes(target);
        var link = Path.Combine(fixtures, "packaging", "linked-output.zip"); File.CreateSymbolicLink(link, target);
        var storedTarget = new FileInfo(link).LinkTarget!;
        links.Add(new(link, storedTarget, false, IsDirectory: false));
        await Run("packaging-linked-output", 2, false, "--package-project", single, link);
        Check("packaging linked output target preserved", File.ReadAllBytes(target).SequenceEqual(bytes) && new FileInfo(link).LinkTarget == storedTarget);
        await Reject("linked-input", Project("linked-input", [Entry(link, right)]));
        var directoryLink = Path.Combine(fixtures, "packaging", "linked-parent"); Directory.CreateSymbolicLink(directoryLink, Path.GetDirectoryName(target)!);
        links.Add(new(directoryLink, new DirectoryInfo(directoryLink).LinkTarget!, false));
        await Run("packaging-linked-parent-output", 2, false, "--package-project", single, Path.Combine(directoryLink, "output.zip"));
        Check("packaging linked parent target preserved", File.ReadAllBytes(target).SequenceEqual(bytes));
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
    { assertions.Add(new("packaging symlink protection", "skipped", exception.Message)); }
    foreach (var (name, firstName, secondName) in new[] { ("case", "Same.txt", "same.txt"), ("nfc", "é.txt", "e\u0301.txt") })
    {
        var first = Text("packaging/collision-" + name + "/" + firstName, "first collision source\n");
        var second = Text("packaging/collision-" + name + "/" + secondName, "second collision source\n");
        if (File.ReadAllText(first) != "first collision source\n")
        { assertions.Add(new("packaging " + name + " collision", "skipped", "ファイルシステムが二つの入力名を同じファイルへ解決します。")); continue; }
        await Reject(name + "-collision", Project(name + "-collision", [Entry(first, aRight), Entry(second, bRight)]));
        Check("packaging collision originals preserved " + name, File.ReadAllText(first) == "first collision source\n" && File.ReadAllText(second) == "second collision source\n");
    }
    Check("packaging original documents unchanged", File.ReadAllBytes(left).SequenceEqual(singleContent.GetValueOrDefault(leftName) ?? [])
        && File.ReadAllBytes(right).SequenceEqual(singleContent.GetValueOrDefault(rightName) ?? []));
    Check("packaging leaves no owned temporary output", !Directory.EnumerateFiles(Path.Combine(fixtures, "packaging"), "*.tmp", SearchOption.AllDirectories).Any());
    Check("packaging removes owned snapshot stages", !Directory.EnumerateDirectories(Path.Combine(fixtures, "packaging"), ".diffbeacon-package-*", SearchOption.AllDirectories).Any());
}

async Task ProjectWorkspaceCases()
{
    // 失敗条件: 比較組・順序・active・設定の欠落、旧単組形式の破壊、パスの誤変換、
    // 不正/過大入力の受入れ、旧プラグイン実行、拒否時の原本破壊、一時ファイル残留。
    bool ContainsFields(JsonElement expected, JsonElement actual) => expected.ValueKind switch
    {
        JsonValueKind.Object => actual.ValueKind == JsonValueKind.Object && expected.EnumerateObject().All(p => actual.TryGetProperty(p.Name, out var value)
            && ((p.Name is "leftPath" or "basePath" or "rightPath" or "fileFilterPath") && p.Value.ValueKind == JsonValueKind.String
                ? value.ValueKind == JsonValueKind.String && value.GetString() == ExpectedProjectPath(p.Value.GetString()!, fixtures)
                : ContainsFields(p.Value, value))),
        JsonValueKind.Array => actual.ValueKind == JsonValueKind.Array && expected.GetArrayLength() == actual.GetArrayLength()
            && expected.EnumerateArray().Select((entry, index) => ContainsFields(entry, actual[index])).All(equal => equal),
        _ => JsonElement.DeepEquals(expected, actual)
    };
    void VerifyJson(string name, string path, Action<JsonElement> verify)
    {
        Check(name + " output exists", File.Exists(path), path);
        if (!File.Exists(path)) return;
        try { using var document = JsonDocument.Parse(File.ReadAllText(path)); verify(document.RootElement); }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        { Check(name + " structure", false, exception.Message); }
    }
    async Task Reject(string name, string source)
    {
        var saved = Text("workspace-rejected/" + name + ".json", "existing output must survive\n");
        var bytes = File.ReadAllBytes(saved);
        await Run("workspace-reject-" + name, 2, false, "--project-copy", source, saved);
        Check("workspace reject preserves " + name, File.Exists(saved) && File.ReadAllBytes(saved).SequenceEqual(bytes));
    }

    var fullEntry = """
        {"leftPath":"left.txt","basePath":"base.txt","rightPath":"right.txt","mode":"Table",
         "providerId":"html-text","fileFilterPath":"filters/example.flt","ignoreCase":true,
         "ignoreWhitespace":true,"ignoreBlankLines":true,"ignoreLinePattern":"^generated$",
         "ignoreNumbers":true,"commentSyntax":3,"whitespace":3,
         "substitutionRules":[{"pattern":"foo","replacement":"bar","matchCase":false,"useRegex":false,"wholeWord":true,"enabled":true},
           {"pattern":"[","replacement":"disabled","matchCase":true,"useRegex":true,"wholeWord":false,"enabled":false}],
         "leftDescription":"左 & <日本語>","baseDescription":"祖先","rightDescription":"右",
         "leftReadOnly":true,"baseReadOnly":true,"rightReadOnly":true,"recursive":false,
         "folderMode":"Hash","excludedPaths":"cache;generated","legacyFilter":"*.cs;*.xml",
         "tableDelimiter":";","tableQuote":"\"","tableAllowNewlinesInQuotes":true,
         "legacySettings":{"unpacker":"DO_NOT_EXECUTE.exe","prediffer":"DO_NOT_EXECUTE_TOO.exe","future-option":"preserve & value"}}
        """;
    var secondEntry = """
        {"leftPath":"https://example.invalid/left?a=1&b=2","rightPath":"http://example.invalid/right",
         "mode":"Web","recursive":true,"folderMode":"TimestampAndSize","tableAllowNewlinesInQuotes":false}
        """;
    var workspace = Text("workspace-all-fields.json", "{\"formatVersion\":1,\"entries\":[" + fullEntry + "," + secondEntry + "],\"activeEntryIndex\":1}");
    var savedWorkspace = Path.Combine(fixtures, "workspace-all-fields-saved.json");
    var result = await Run("workspace-all-fields", 0, true, "--project-copy", workspace, savedWorkspace);
    VerifyJson("workspace all fields", savedWorkspace, actual =>
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(workspace));
        Check("workspace all fields order and active preserved", ContainsFields(expected.RootElement, actual));
    });
    if (result.ExitCode == 0)
    {
        using var response = JsonDocument.Parse(result.Stdout);
        var root = response.RootElement;
        Check("workspace CLI metadata", root.TryGetProperty("entries", out var count) && count.GetInt32() == 2
            && root.TryGetProperty("activeEntryIndex", out var active) && active.GetInt32() == 1
            && root.TryGetProperty("output", out var target) && target.GetString() == savedWorkspace);
    }
    var savedAgain = Path.Combine(fixtures, "workspace-all-fields-again.json");
    await Run("workspace-json-roundtrip", 0, true, "--project-copy", savedWorkspace, savedAgain);
    Check("workspace stable roundtrip bytes", File.Exists(savedWorkspace) && File.Exists(savedAgain)
        && File.ReadAllBytes(savedWorkspace).SequenceEqual(File.ReadAllBytes(savedAgain)));

    var single = Text("workspace-old-single.json", "{\"leftPath\":\"old-left.txt\",\"rightPath\":\"old-right.txt\",\"mode\":\"Text\"}");
    var savedSingle = Path.Combine(fixtures, "workspace-old-single-saved.json");
    await Run("workspace-old-single", 0, true, "--project-copy", single, savedSingle);
    VerifyJson("workspace old single", savedSingle, actual => Check("workspace old single root retained",
        !actual.TryGetProperty("entries", out _) && actual.GetProperty("leftPath").GetString() == Path.Combine(fixtures, "old-left.txt")));
    VerifyJson("workspace old single defaults", savedSingle, actual => Check("workspace omitted fields preserve constructor defaults",
        actual.GetProperty("basePath").GetString() == "" && actual.GetProperty("recursive").GetBoolean()
        && actual.GetProperty("folderMode").GetString() == "Content" && actual.GetProperty("substitutionRules").GetArrayLength() == 0
        && !actual.GetProperty("legacySettings").EnumerateObject().Any()));
    var defaultWrapper = Text("workspace-default-wrapper.json", "{\"entries\":[{},{}]}");
    var defaultWrapperSaved = Path.Combine(fixtures, "workspace-default-wrapper-saved.json");
    await Run("workspace-wrapper-defaults", 0, true, "--project-copy", defaultWrapper, defaultWrapperSaved);
    VerifyJson("workspace wrapper defaults", defaultWrapperSaved, actual => Check("workspace omitted version and active index restored",
        actual.GetProperty("formatVersion").GetInt32() == 1 && actual.GetProperty("activeEntryIndex").GetInt32() == 0
        && actual.GetProperty("entries").EnumerateArray().All(entry => entry.GetProperty("recursive").GetBoolean() && entry.GetProperty("basePath").GetString() == "")));
    foreach (var enabled in new[] { true, false })
    {
        var suffix = enabled ? "omitted" : "false";
        var flags = enabled ? "" : ",\"matchCase\":false,\"useRegex\":false,\"enabled\":false";
        var ruleProject = Text("workspace-rule-" + suffix + ".json", "{\"substitutionRules\":[{\"pattern\":\"build=[0-9]+\",\"replacement\":\"build=*\"" + flags + "}]}");
        var ruleSaved = Path.Combine(fixtures, "workspace-rule-" + suffix + "-saved.json");
        await Run("workspace-rule-" + suffix, 0, true, "--project-copy", ruleProject, ruleSaved);
        VerifyJson("workspace rule " + suffix, ruleSaved, actual =>
        {
            var rule = actual.GetProperty("substitutionRules")[0];
            Check("workspace rule flags " + suffix, rule.GetProperty("matchCase").GetBoolean() == enabled
                && rule.GetProperty("useRegex").GetBoolean() == enabled && rule.GetProperty("enabled").GetBoolean() == enabled
                && !rule.GetProperty("wholeWord").GetBoolean());
        });
    }
    var fullSingle = Text("workspace-full-single.json", fullEntry);
    var savedFullSingle = Path.Combine(fixtures, "workspace-full-single-saved.json");
    await Run("workspace-full-single", 0, true, "--project-copy", fullSingle, savedFullSingle);
    VerifyJson("workspace full single", savedFullSingle, actual =>
    {
        using var expected = JsonDocument.Parse(fullEntry);
        Check("workspace full single fields", !actual.TryGetProperty("entries", out _) && ContainsFields(expected.RootElement, actual));
    });
    var maximum = Text("workspace-maximum.json", "{\"formatVersion\":1,\"entries\":[" + string.Join(',', Enumerable.Repeat(secondEntry, 256)) + "],\"activeEntryIndex\":255}");
    var savedMaximum = Path.Combine(fixtures, "workspace-maximum-saved.json");
    await Run("workspace-maximum", 0, true, "--project-copy", maximum, savedMaximum);
    VerifyJson("workspace maximum", savedMaximum, actual => Check("workspace maximum count active",
        actual.GetProperty("entries").GetArrayLength() == 256 && actual.GetProperty("activeEntryIndex").GetInt32() == 255));
    var oneEntryWrapper = Text("workspace-one-entry.json", "{\"formatVersion\":1,\"entries\":[" + fullEntry + "],\"activeEntryIndex\":0}");
    var savedOneEntry = Path.Combine(fixtures, "workspace-one-entry-saved.json");
    await Run("workspace-one-entry", 0, true, "--project-copy", oneEntryWrapper, savedOneEntry);
    VerifyJson("workspace one entry", savedOneEntry, actual =>
    {
        using var expected = JsonDocument.Parse(fullEntry);
        Check("workspace one entry uses single root", !actual.TryGetProperty("entries", out _) && ContainsFields(expected.RootElement, actual));
    });

    foreach (var (method, mode) in new[] { (0, "Content"), (2, "Hash"), (4, "TimestampAndSize"), (99, "Content") })
    {
        var legacy = Text($"workspace-method-{method}.WinMerge", $"<project><paths><left>left</left><right>right</right><window-type>6</window-type><compare-method>{method}</compare-method></paths></project>");
        var migrated = Path.Combine(fixtures, $"workspace-method-{method}.json");
        await Run($"workspace-method-{method}", 0, true, "--project-copy", legacy, migrated);
        VerifyJson($"workspace method {method}", migrated, actual =>
        {
            Check($"workspace folder method {method}", actual.GetProperty("folderMode").GetString() == mode && actual.GetProperty("recursive").GetBoolean());
            if (method == 99) Check("workspace unsupported folder method preserved", actual.GetProperty("legacySettings").GetProperty("compare-method").GetString() == "99");
        });
    }

    var environmentName = "DIFFBEACON_E2E_PROJECT_ROOT";
    var previousEnvironment = Environment.GetEnvironmentVariable(environmentName);
    var environmentRoot = Path.Combine(fixtures, "environment-root");
    Environment.SetEnvironmentVariable(environmentName, environmentRoot);
    try
    {
        var legacy = Text("workspace-nested/multiple.WinMerge", """
            <project><paths><left>left &amp; 日本語.txt</left><middle>base.txt</middle><right>right.txt</right>
            <left-desc>左 &amp; &lt;説明&gt;</left-desc><middle-desc>祖先</middle-desc><right-desc>右</right-desc>
            <left-readonly>1</left-readonly><middle-readonly>1</middle-readonly><right-readonly>1</right-readonly>
            <subfolders>0</subfolders><filter>*.cs;*.xml</filter><table-delimiter>;</table-delimiter><table-quote>&quot;</table-quote>
            <table-allownewlinesinquotes>1</table-allownewlinesinquotes><unpacker>DO_NOT_EXECUTE.exe</unpacker>
            <prediffer>DO_NOT_EXECUTE_TOO.exe</prediffer><future-option>preserve &amp; value</future-option></paths>
            <paths><left>https://example.invalid/left?a=1&amp;b=2</left><right>http://example.invalid/right</right><window-type>5</window-type></paths>
            <paths><left>%DIFFBEACON_E2E_PROJECT_ROOT%/env.txt</left><middle>C:\foreign\base.txt</middle><right>/foreign/right.txt</right></paths></project>
            """);
        var migrated = Path.Combine(fixtures, "workspace-multiple-legacy.json");
        await Run("workspace-multiple-legacy", 0, true, "--project-copy", legacy, migrated);
        VerifyJson("workspace legacy", migrated, actual =>
        {
            var entries = actual.GetProperty("entries");
            Check("workspace legacy count active", entries.GetArrayLength() == 3 && actual.GetProperty("activeEntryIndex").GetInt32() == 0);
            var first = entries[0];
            Check("workspace legacy relative paths", first.GetProperty("leftPath").GetString() == Path.Combine(Path.GetDirectoryName(legacy)!, "left & 日本語.txt")
                && first.GetProperty("basePath").GetString() == Path.Combine(Path.GetDirectoryName(legacy)!, "base.txt"));
            Check("workspace legacy descriptions and read only", first.GetProperty("leftDescription").GetString() == "左 & <説明>"
                && first.GetProperty("baseDescription").GetString() == "祖先" && first.GetProperty("rightDescription").GetString() == "右"
                && first.GetProperty("leftReadOnly").GetBoolean() && first.GetProperty("baseReadOnly").GetBoolean() && first.GetProperty("rightReadOnly").GetBoolean());
            Check("workspace legacy table folder settings", !first.GetProperty("recursive").GetBoolean()
                && first.GetProperty("legacyFilter").GetString() == "*.cs;*.xml" && first.GetProperty("tableDelimiter").GetString() == ";"
                && first.GetProperty("tableQuote").GetString() == "\"" && first.GetProperty("tableAllowNewlinesInQuotes").GetBoolean());
            var unsupported = first.GetProperty("legacySettings");
            Check("workspace unsupported legacy settings preserved", unsupported.GetProperty("unpacker").GetString() == "DO_NOT_EXECUTE.exe"
                && unsupported.GetProperty("prediffer").GetString() == "DO_NOT_EXECUTE_TOO.exe" && unsupported.GetProperty("future-option").GetString() == "preserve & value");
            Check("workspace legacy URLs unchanged", entries[1].GetProperty("leftPath").GetString() == "https://example.invalid/left?a=1&b=2"
                && entries[1].GetProperty("rightPath").GetString() == "http://example.invalid/right" && entries[1].GetProperty("mode").GetString() == "Web");
            Check("workspace legacy environment and foreign absolute paths", Path.GetFullPath(entries[2].GetProperty("leftPath").GetString()!) == Path.Combine(environmentRoot, "env.txt")
                && entries[2].GetProperty("basePath").GetString() == @"C:\foreign\base.txt" && entries[2].GetProperty("rightPath").GetString() == "/foreign/right.txt");
        });
        var migratedAgain = Path.Combine(fixtures, "workspace-multiple-legacy-again.json");
        await Run("workspace-legacy-json-roundtrip", 0, true, "--project-copy", migrated, migratedAgain);
        Check("workspace legacy stable roundtrip", File.Exists(migrated) && File.Exists(migratedAgain)
            && File.ReadAllBytes(migrated).SequenceEqual(File.ReadAllBytes(migratedAgain)));
    }
    finally { Environment.SetEnvironmentVariable(environmentName, previousEnvironment); }

    foreach (var (name, content) in new (string, string)[]
    {
        ("empty", "{\"formatVersion\":1,\"entries\":[],\"activeEntryIndex\":0}"),
        ("null-entry", "{\"formatVersion\":1,\"entries\":[null],\"activeEntryIndex\":0}"),
        ("null-entries", "{\"formatVersion\":1,\"entries\":null,\"activeEntryIndex\":0}"),
        ("null-base", "{\"basePath\":null}"),
        ("null-mode", "{\"mode\":null}"),
        ("null-folder-mode", "{\"folderMode\":null}"),
        ("null-rules", "{\"substitutionRules\":null}"),
        ("null-legacy-settings", "{\"legacySettings\":null}"),
        ("unknown-mode", "{\"mode\":\"Unsupported\"}"),
        ("unknown-folder-mode", "{\"folderMode\":\"Unsupported\"}"),
        ("unknown-comments", "{\"commentSyntax\":999}"),
        ("unknown-whitespace", "{\"whitespace\":999}"),
        ("null-rule", "{\"substitutionRules\":[null]}"),
        ("null-pattern", "{\"substitutionRules\":[{\"pattern\":null,\"replacement\":\"x\"}]}"),
        ("null-replacement", "{\"substitutionRules\":[{\"pattern\":\"x\",\"replacement\":null}]}"),
        ("null-rule-flag", "{\"substitutionRules\":[{\"pattern\":\"x\",\"replacement\":\"y\",\"enabled\":null}]}"),
        ("null-legacy-value", "{\"legacySettings\":{\"unpacker\":null}}"),
        ("unknown-version", "{\"formatVersion\":2,\"entries\":[{}],\"activeEntryIndex\":0}"),
        ("negative-active", "{\"formatVersion\":1,\"entries\":[{}],\"activeEntryIndex\":-1}"),
        ("large-active", "{\"formatVersion\":1,\"entries\":[{}],\"activeEntryIndex\":1}"),
        ("too-many", "{\"formatVersion\":1,\"entries\":[" + string.Join(',', Enumerable.Repeat("{}", 257)) + "],\"activeEntryIndex\":0}"),
        ("large-json", "{\"leftPath\":\"" + new string('x', 4 * 1024 * 1024 + 1) + "\"}"),
        ("malformed-json", "{\"formatVersion\":1,\"entries\":[")
    }) await Reject(name, Text("workspace-invalid/" + name + ".json", content));
    await Reject("empty-xml", Text("workspace-invalid/empty.WinMerge", "<project/>"));
    await Reject("too-many-xml", Text("workspace-invalid/many.WinMerge", "<project>" + string.Concat(Enumerable.Repeat("<paths><left>a</left><right>b</right></paths>", 257)) + "</project>"));
    await Reject("dtd", Text("workspace-invalid/dtd.WinMerge", "<!DOCTYPE project [<!ENTITY attack 'injected'>]><project><paths><left>&attack;</left></paths></project>"));

    var readOnly = Text("workspace-rejected/readonly.json", "protected project output\n");
    var readOnlyBytes = File.ReadAllBytes(readOnly);
    var attributes = File.GetAttributes(readOnly);
    try
    {
        File.SetAttributes(readOnly, attributes | FileAttributes.ReadOnly);
        await Run("workspace-readonly-output", 2, false, "--project-copy", workspace, readOnly);
        Check("workspace read only output preserved", File.ReadAllBytes(readOnly).SequenceEqual(readOnlyBytes));
    }
    finally { File.SetAttributes(readOnly, attributes); }
    if (OperatingSystem.IsWindows())
    {
        var attributed = Text("workspace-rejected/attributes.json", "old project bytes");
        var originalAttributes = File.GetAttributes(attributed);
        try
        {
            File.SetAttributes(attributed, FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed);
            await Run("workspace-windows-attributes", 0, true, "--project-copy", single, attributed);
            var preserved = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed;
            Check("workspace Windows attributes preserved", (File.GetAttributes(attributed) & preserved) == preserved);
        }
        finally { File.SetAttributes(attributed, originalAttributes); }
    }
    else
    {
        var executableProject = Text("workspace-rejected/mode.json", "old project bytes");
        var originalMode = File.GetUnixFileMode(executableProject);
        try
        {
            var executableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(executableProject, executableMode);
            await Run("workspace-unix-mode", 0, true, "--project-copy", single, executableProject);
            Check("workspace Unix mode preserved", File.GetUnixFileMode(executableProject) == executableMode);
        }
        finally { File.SetUnixFileMode(executableProject, originalMode); }
    }
    try
    {
        var link = Path.Combine(fixtures, "workspace-linked-output");
        var target = Path.Combine(fixtures, "workspace-link-target");
        Directory.CreateDirectory(target);
        var targetFile = Path.Combine(target, "project.json");
        File.WriteAllText(targetFile, "protected linked project\n", utf8);
        var bytes = File.ReadAllBytes(targetFile);
        Directory.CreateSymbolicLink(link, target);
        links.Add(new(link, new DirectoryInfo(link).LinkTarget!, false));
        await Run("workspace-linked-output", 2, false, "--project-copy", workspace, Path.Combine(link, "project.json"));
        Check("workspace linked target preserved", File.ReadAllBytes(targetFile).SequenceEqual(bytes));
        var fileLink = Path.Combine(fixtures, "workspace-file-link.json");
        File.CreateSymbolicLink(fileLink, targetFile);
        links.Add(new(fileLink, new FileInfo(fileLink).LinkTarget!, false, IsDirectory: false));
        await Run("workspace-file-link-output", 2, false, "--project-copy", workspace, fileLink);
        Check("workspace file link target preserved", File.ReadAllBytes(targetFile).SequenceEqual(bytes)
            && new FileInfo(fileLink).LinkTarget == targetFile);
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
    { assertions.Add(new("workspace output symlink", "skipped", exception.Message)); }
    Check("workspace temporary outputs removed", !Directory.EnumerateFiles(fixtures, ".diffbeacon-project-*.tmp", SearchOption.AllDirectories).Any());
}

async Task TextAdvancedCases()
{
    async Task Compare(string name, int exit, string left, string right, params string[] options)
    {
        var leftPath = Text(name + "-left.txt", left);
        var rightPath = Text(name + "-right.txt", right);
        await Run(name, exit, exit != 2, new[] { "--compare", leftPath, rightPath }.Concat(options).ToArray());
    }
    await Compare("advanced-numbers-only", 0, "version=123; amount=4.56\n", "version=789; amount=9.10\n", "--ignore-numbers");
    await Compare("advanced-numbers-preserve-letters", 1, "alpha123\n", "beta456\n", "--ignore-numbers");
    await Compare("advanced-cstyle-multiline", 0, "int x=1; /* first comment\nfirst body */ int y=2;\n", "int x=1; /* other comment\nother body */ int y=2;\n", "--comments", "CStyle");
    await Compare("advanced-cstyle-quoted-markers", 1, "var s=\"/* LEFT */ // literal\";\n", "var s=\"/* RIGHT */ // literal\";\n", "--comments", "CStyle");
    foreach (var prefix in new[] { "u8R", "uR", "UR", "LR", "R" })
        await Compare("advanced-cstyle-raw-" + prefix, 1, $"auto s = {prefix}\"tag(\" // LEFT)tag\";\n", $"auto s = {prefix}\"tag(\" // RIGHT)tag\";\n", "--comments", "CStyle");
    await Compare("advanced-python-comments", 0, "x = 1 # first\nprint(x) # original\n", "x = 1 # second\nprint(x) # changed\n", "--comments", "Python");
    await Compare("advanced-python-quoted-marker", 1, "value = \"# LEFT\"\n", "value = \"# RIGHT\"\n", "--comments", "Python");
    await Compare("advanced-xml-multiline", 0, "<a><!-- first\nfirst body --><b>same</b></a>\n", "<a><!-- changed\nother body --><b>same</b></a>\n", "--comments", "Xml");
    await Compare("advanced-substitution-ignore-case-regex", 0, "id=ABC\n", "id=def\n", "--substitute", "(?i)(abc|def)", "VALUE");
    await Compare("advanced-substitution-repeated", 0, "env=prod;build=111\n", "env=dev;build=222\n", "--substitute", "(prod|dev)", "ENV", "--substitute", "[0-9]+", "NUMBER");
    await Compare("advanced-substitution-invalid-regex", 2, "left\n", "right\n", "--substitute", "[", "value");
    await Compare("advanced-whitespace-trim", 0, "  alpha beta  \n", "alpha beta\n", "--whitespace", "trim");
    await Compare("advanced-whitespace-changes", 0, "alpha  beta\n", "alpha beta\n", "--whitespace", "changes");
    await Compare("advanced-whitespace-all", 0, "a l p h a\tb e t a\n", "alphabeta\n", "--whitespace", "all");
    await Compare("advanced-whitespace-none", 1, "alpha  beta\n", "alpha beta\n", "--whitespace", "none");

    foreach (var (label, encoding) in new (string, Encoding)[] { ("utf16-bom", new UnicodeEncoding(false, true)), ("utf8-bom", new UTF8Encoding(true)) })
    {
        var ancestorText = "left-old\r\nanchor-a\r\nconflict-old\r\nanchor-b\r\nright-old";
        var leftText = "left-NEW\r\nanchor-a\r\nconflict-LEFT\r\nanchor-b\r\nright-old";
        var rightText = "left-old\r\nanchor-a\r\nconflict-RIGHT\r\nanchor-b\r\nright-NEW";
        var ancestor = Text("advanced-merge-" + label + "-base.txt", ancestorText, encoding);
        var left = Text("advanced-merge-" + label + "-left.txt", leftText, encoding);
        var right = Text("advanced-merge-" + label + "-right.txt", rightText, encoding);
        foreach (var choice in new[] { "LEFT", "BASE", "RIGHT" })
        {
            var name = "advanced-merge-select-" + label + "-" + choice.ToLowerInvariant();
            var merged = Path.Combine(fixtures, name + ".txt");
            var conflictValue = choice switch { "LEFT" => "conflict-LEFT", "RIGHT" => "conflict-RIGHT", _ => "conflict-old" };
            var expected = Text(name + "-expected.txt", "left-NEW\r\nanchor-a\r\n" + conflictValue + "\r\nanchor-b\r\nright-NEW", encoding);
            await Run(name, 0, true, "--merge-select", ancestor, left, right, merged, choice);
            Check(name + " byte preservation and independent changes", File.Exists(merged) && File.ReadAllBytes(merged).SequenceEqual(File.ReadAllBytes(expected)), $"expected={expected}; actual={merged}");
        }
    }
    var crlfAncestor = Text("advanced-merge-fast-base.txt", "one\r\ntwo\r\n");
    var changedLf = Text("advanced-merge-fast-changed-lf.txt", "one\nTHREE\n");
    var unchangedLf = Text("advanced-merge-fast-unchanged-lf.txt", "one\ntwo\n");
    foreach (var (name, left, right, expected) in new[]
    {
        ("advanced-merge-identical-changed-lf", changedLf, changedLf, changedLf),
        ("advanced-merge-identical-unchanged-lf", unchangedLf, unchangedLf, unchangedLf),
        ("advanced-merge-left-change-lf", changedLf, crlfAncestor, changedLf),
        ("advanced-merge-right-change-lf", crlfAncestor, changedLf, changedLf)
    })
    {
        var merged = Path.Combine(fixtures, name + ".txt");
        await Run(name, 0, true, "--merge", crlfAncestor, left, right, merged);
        Check(name + " exact selected bytes", File.Exists(merged) && File.ReadAllBytes(merged).SequenceEqual(File.ReadAllBytes(expected)), $"expected={expected}; actual={merged}");
    }
    await WorkspaceAdvancedCases();
    await LegacyCommentCases();
}

async Task LegacyCommentCases()
{
    foreach (var (name, left, middle, right, enabled, expected) in new (string, string, string, string, bool, int)[]
    {
        ("python", "left.py", "", "right.py", true, 3),
        ("official-tag", "left.py", "", "right.py", true, 3),
        ("csharp", "left.cs", "", "right.cs", true, 2),
        ("xml", "left.xml", "", "right.xml", true, 4),
        ("cpp", "left.cpp", "", "right.cpp", true, 1),
        ("middle-pyw", "left.unknown", "ancestor.pyw", "right.unknown", true, 3),
        ("right-axaml", "left.unknown", "", "right.AXAML", true, 4),
        ("unknown", "left.txt", "ancestor.unknown", "right.txt", true, 0),
        ("disabled", "left.py", "", "right.py", false, 0),
        ("left-precedence", "left.cpp", "ancestor.cs", "right.py", true, 1)
    })
    {
        var tag = name == "official-tag" ? "ignore-comment-diff" : "ignore-comments";
        var legacy = Text("legacy-comments-" + name + ".WinMerge", $"<project><paths><left>{left}</left><middle>{middle}</middle><right>{right}</right><{tag}>{(enabled ? 1 : 0)}</{tag}></paths></project>");
        var saved = Path.Combine(fixtures, "legacy-comments-" + name + ".json");
        await Run("legacy-comments-" + name, 0, true, "--project-copy", legacy, saved);
        if (File.Exists(saved))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(saved));
            Check("legacy comments inferred " + name, document.RootElement.GetProperty("commentSyntax").GetInt32() == expected, $"expected enum={expected}; saved={saved}");
        }
        else Check("legacy comments inferred " + name, false, "保存先が作成されませんでした。");
    }
}

async Task WorkspaceAdvancedCases()
{
    var source = Text("advanced-project-all-fields.json", """
        {
          "leftPath":"left.txt", "basePath":"base.txt", "rightPath":"right.txt", "mode":"Text",
          "providerId":"html-text", "fileFilterPath":"filters/example.flt",
          "ignoreCase":true, "ignoreWhitespace":false, "ignoreBlankLines":true, "ignoreLinePattern":"^generated$",
          "ignoreNumbers":true, "commentSyntax":3, "whitespace":1,
          "substitutionRules":[
            {"pattern":"foo", "replacement":"bar", "matchCase":false, "useRegex":false, "wholeWord":true, "enabled":true},
            {"pattern":"[", "replacement":"disabled invalid expression", "matchCase":true, "useRegex":true, "wholeWord":false, "enabled":false},
            {"pattern":"[0-9]+", "replacement":"NUMBER", "matchCase":true, "useRegex":true, "wholeWord":false, "enabled":true}
          ]
        }
        """);
    var saved = Path.Combine(fixtures, "advanced-project-all-fields-saved.json");
    await Run("advanced-project-all-fields", 0, true, "--project-copy", source, saved);
    if (File.Exists(saved))
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(source));
        using var actual = JsonDocument.Parse(File.ReadAllText(saved));
        Check("advanced project fields and rule order preserved", expected.RootElement.EnumerateObject().All(property =>
            actual.RootElement.TryGetProperty(property.Name, out var value)
            && (property.Name is "leftPath" or "basePath" or "rightPath" or "fileFilterPath"
                ? value.GetString() == ExpectedProjectPath(property.Value.GetString()!, Path.GetDirectoryName(source)!)
                : JsonElement.DeepEquals(property.Value, value))));
    }
    else Check("advanced project fields and rule order preserved", false, "保存先が作成されませんでした。");

    foreach (var legacyMode in new[] { 1, 2 })
    {
        var legacy = Text($"advanced-white-spaces-{legacyMode}.WinMerge", $"<project><paths><left>left.txt</left><right>right.txt</right><white-spaces>{legacyMode}</white-spaces></paths></project>");
        var migrated = Path.Combine(fixtures, $"advanced-white-spaces-{legacyMode}.json");
        await Run($"advanced-project-legacy-whitespace-{legacyMode}", 0, true, "--project-copy", legacy, migrated);
        if (File.Exists(migrated))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(migrated));
            var values = document.RootElement;
            Check($"legacy whitespace {legacyMode} remains distinct", values.GetProperty("ignoreWhitespace").GetBoolean() == (legacyMode == 2) && values.GetProperty("whitespace").GetInt32() == (legacyMode == 1 ? 2 : 3));
        }
        else Check($"legacy whitespace {legacyMode} remains distinct", false, "保存先が作成されませんでした。");
    }
}

async Task ProviderBoundaryCases()
{
    const string xsi = "http://www.w3.org/2001/XMLSchema-instance";
    var qnameLeft = Text("qname-left.xml", $"<root xmlns:xsi=\"{xsi}\" xmlns:t=\"urn:first\" xsi:type=\"t:Foo\"/>");
    var qnameChanged = Text("qname-changed.xml", $"<root xmlns:xsi=\"{xsi}\" xmlns:t=\"urn:second\" xsi:type=\"t:Foo\"/>");
    var qnameRenamed = Text("qname-renamed.xml", $"<root xmlns:xsi=\"{xsi}\" xmlns:u=\"urn:first\" xsi:type=\"u:Foo\"/>");
    await Run("provider-xml-qname-binding", 1, true, "--provider", "xml", qnameLeft, qnameChanged);
    await Run("provider-xml-qname-prefix", 0, true, "--provider", "xml", qnameLeft, qnameRenamed);
    await Run("provider-html-literal-angle", 1, true, "--provider", "html-text",
        Text("html-literal-angle.html", "<p>1 < 2 and 3 > 2</p>"), Text("html-missing-literal.html", "<p>1 2</p>"));
    await Run("provider-html-script-close-boundary", 0, true, "--provider", "html-text",
        Text("html-script-boundary.html", "<p>before<script>var x=\"</scripture>\";keep();</script>after</p>"), Text("html-script-expected.html", "<p>beforeafter</p>"));
    string Presentation(string name, bool reversed) => Zip(name,
        ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/></Types>"),
        ("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>"),
        ("ppt/presentation.xml", "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst>" +
            (reversed ? "<p:sldId id=\"257\" r:id=\"rId2\"/><p:sldId id=\"256\" r:id=\"rId1\"/>" : "<p:sldId id=\"256\" r:id=\"rId1\"/><p:sldId id=\"257\" r:id=\"rId2\"/>") + "</p:sldIdLst></p:presentation>"),
        ("ppt/_rels/presentation.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide2.xml\"/></Relationships>"),
        ("ppt/slides/slide1.xml", "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>First visible slide</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>"),
        ("ppt/slides/slide2.xml", "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>Second visible slide</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>"));
    await Run("provider-pptx-presentation-order", 1, true, "--provider", "office", Presentation("slides-original.pptx", false), Presentation("slides-reversed.pptx", true));
}

async Task ArchiveCases()
{
    var officialRoot = Path.GetFullPath("tests/Fixtures/Archives");
    var manifestPath = Path.Combine(officialRoot, "manifest.json");
    using var provenance = JsonDocument.Parse(File.ReadAllText(manifestPath));
    string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    foreach (var fixture in provenance.RootElement.GetProperty("fixtures").EnumerateArray())
    {
        var path = Path.Combine(officialRoot, fixture.GetProperty("file").GetString()!);
        Check("archive fixture provenance " + Path.GetFileName(path), File.Exists(path) && Hash(path).Equals(fixture.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase));
    }
    var license = Path.Combine(officialRoot, "LICENSE.txt");
    Check("archive fixture MIT license", File.Exists(license) && Hash(license).Equals(provenance.RootElement.GetProperty("licenseSha256").GetString(), StringComparison.OrdinalIgnoreCase));
    Check("published SharpCompress license", File.Exists(Path.Combine(Path.GetDirectoryName(app)!, "SharpCompress.LICENSE.txt")));
    var expected = provenance.RootElement.GetProperty("expectedEntries").EnumerateArray().ToDictionary(
        e => e.GetProperty("path").GetString()!, e => (Size: e.GetProperty("size").GetInt64(), Sha: e.GetProperty("sha256").GetString()!), StringComparer.Ordinal);
    IReadOnlyList<ArchiveFile> Entries(CommandResult command)
    {
        if (command.ExitCode != 0) return [];
        try
        {
            using var document = JsonDocument.Parse(command.Stdout);
            Check(command.Name + " format", !string.IsNullOrEmpty(document.RootElement.GetProperty("format").GetString()));
            return document.RootElement.GetProperty("entries").EnumerateArray().Select(e => new ArchiveFile(
                e.GetProperty("path").GetString()!.Replace('\\', '/').TrimEnd('/'),
                e.GetProperty("directory").GetBoolean(), e.GetProperty("size").GetInt64(),
                e.GetProperty("sha256").GetString() ?? "", e.GetProperty("encrypted").GetBoolean())).ToArray();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        { Check(command.Name + " manifest shape", false, exception.Message); return []; }
    }
    void VerifyOriginal(string name, IReadOnlyList<ArchiveFile> entries)
    {
        var files = entries.Where(e => !e.Directory).ToArray();
        Check(name + " original file count", files.Length == expected.Count);
        foreach (var (path, value) in expected)
        {
            var actual = files.SingleOrDefault(e => e.Path == path);
            Check(name + " original bytes " + path, actual is not null && actual.Size == value.Size && actual.Sha256.Equals(value.Sha, StringComparison.OrdinalIgnoreCase));
        }
    }
    foreach (var filename in new[] { "7Zip.solid.7z", "Rar5.solid.rar" })
    {
        var result = await Run("archive-solid-" + filename, 0, true, "--archive-list", Path.Combine(officialRoot, filename));
        VerifyOriginal(result.Name, Entries(result));
    }
    foreach (var (name, filename, password) in new[]
    {
        ("7z-aes", "7Zip.LZMA2.Aes.7z", "testpassword"),
        ("rar4-header", "Rar.encrypted_filesAndHeader.rar", "test"),
        ("rar5-header", "Rar5.encrypted_filesAndHeader.rar", "test"),
        ("zip-aes", "Zip.deflate.WinzipAES.zip", "test")
    })
    {
        var path = Path.Combine(officialRoot, filename);
        var result = await RunWithInput("archive-encrypted-" + name, 0, true, password + "\n", "--archive-list", path, "--password-stdin");
        var entries = Entries(result);
        VerifyOriginal(result.Name, entries);
        Check(result.Name + " encryption reported", entries.Any(e => !e.Directory && e.Encrypted));
        await Run("archive-no-password-" + name, 2, false, "--archive-list", path);
        await RunWithInput("archive-wrong-password-" + name, 2, false, "not-the-fixture-password\n", "--archive-list", path, "--password-stdin");
    }
    var solid = Path.Combine(officialRoot, "7Zip.solid.7z");
    var crossFormat = await Run("archive-compare-cross-format", 1, true, "--archive-compare", solid, Path.Combine(officialRoot, "Rar5.solid.rar"));
    if (crossFormat.ExitCode == 1)
    {
        using var comparison = JsonDocument.Parse(crossFormat.Stdout);
        var differences = comparison.RootElement.GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("status").GetString() != "Equal").ToArray();
        Check("cross-format only extra empty directory differs", differences.Length == 1 && differences[0].GetProperty("path").GetString() == "Empty" && differences[0].GetProperty("status").GetString() == "OnlyRight");
    }
    var encrypted7z = Path.Combine(officialRoot, "7Zip.LZMA2.Aes.7z");
    await RunWithInput("archive-compare-encrypted", 0, true, "testpassword\ntestpassword\n", "--archive-compare", encrypted7z, encrypted7z, "--password-stdin");
    var exported = Path.Combine(fixtures, "archive-entry-original.exe");
    await Run("archive-entry-export", 0, true, "--archive-entry", solid, "exe/test.exe", exported);
    Check("archive entry export original bytes", File.Exists(exported) && Hash(exported).Equals(expected["exe/test.exe"].Sha, StringComparison.OrdinalIgnoreCase));
    var repacked = Path.Combine(fixtures, "archive-repacked.7z");
    await Run("archive-repack-solid", 0, true, "--archive-repack", solid, repacked);
    VerifyOriginal("archive-repacked", Entries(await Run("archive-list-repacked", 0, true, "--archive-list", repacked)));
    await Run("archive-compare-repacked", 0, true, "--archive-compare", solid, repacked);
    var reexported = Path.Combine(fixtures, "archive-entry-repacked.exe");
    await Run("archive-entry-repacked", 0, true, "--archive-entry", repacked, "exe/test.exe", reexported);
    Check("archive repack entry bytes", File.Exists(exported) && File.Exists(reexported) && File.ReadAllBytes(exported).SequenceEqual(File.ReadAllBytes(reexported)));
    var decrypted = Path.Combine(fixtures, "archive-rar-decrypted.7z");
    await RunWithInput("archive-repack-encrypted-rar", 0, true, "test\n", "--archive-repack", Path.Combine(officialRoot, "Rar5.encrypted_filesAndHeader.rar"), decrypted, "--password-stdin");
    var decryptedEntries = Entries(await Run("archive-list-decrypted-repack", 0, true, "--archive-list", decrypted));
    VerifyOriginal("archive-decrypted-repack", decryptedEntries);
    Check("repack is not encrypted", decryptedEntries.Count > 0 && decryptedEntries.All(e => !e.Encrypted));

    var createRoot = Path.Combine(fixtures, "archive-create-source");
    Text("archive-create-source/nested/text.txt", "created\r\nwithout final newline", new UnicodeEncoding(false, true));
    var binary = Path.Combine(createRoot, "nested/binary.bin");
    File.WriteAllBytes(binary, [0, 255, 1, 0, 127, 3]);
    var created = Path.Combine(fixtures, "archive-created.7z");
    await Run("archive-create", 0, true, "--archive-create", createRoot, created);
    var createdEntries = Entries(await Run("archive-list-created", 0, true, "--archive-list", created));
    Check("created archive binary hash", createdEntries.Any(e => e.Path == "nested/binary.bin" && e.Sha256.Equals(Hash(binary), StringComparison.OrdinalIgnoreCase)));
    var createdExport = Path.Combine(fixtures, "archive-created-binary.bin");
    await Run("archive-created-entry", 0, true, "--archive-entry", created, "nested/binary.bin", createdExport);
    Check("created archive exported bytes", File.Exists(createdExport) && File.ReadAllBytes(binary).SequenceEqual(File.ReadAllBytes(createdExport)));
    await Run("archive-compare-different", 1, true, "--archive-compare", solid, created);

    var unsafeZip = Zip("archive-unsafe.zip", ("../archive-escape.txt", "unsafe"));
    await Run("archive-unsafe-entry", 2, false, "--archive-list", unsafeZip);
    Check("archive unsafe does not extract", !File.Exists(Path.Combine(fixtures, "archive-escape.txt")) && !File.Exists(Path.Combine(output, "archive-escape.txt")));
    await Run("archive-duplicate-entry", 2, false, "--archive-list", Zip("archive-duplicates.zip", ("same.txt", "first"), ("same.txt", "second")));
    var corrupt = Path.Combine(fixtures, "archive-corrupt.zip");
    File.WriteAllBytes(corrupt, [0x50, 0x4b, 3, 4, 0, 0, 0]);
    await Run("archive-corrupt-zip", 2, false, "--archive-list", corrupt);
    // 実データを残したまま両方の ZIP ヘッダーの宣言 CRC だけをゼロにする。
    var zeroCrc = Path.Combine(fixtures, "archive-invalid-crc-zero.zip");
    using (var file = File.Create(zeroCrc))
    using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("data.txt", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false)))
        writer.Write("Archive CRC integrity sentinel");
    var crcBytes = File.ReadAllBytes(zeroCrc);
    for (var index = 0; index <= crcBytes.Length - 20; index++)
    {
        if (crcBytes[index] != 0x50 || crcBytes[index + 1] != 0x4b) continue;
        var crcOffset = crcBytes[index + 2] == 3 && crcBytes[index + 3] == 4 ? 14 : crcBytes[index + 2] == 1 && crcBytes[index + 3] == 2 ? 16 : -1;
        if (crcOffset >= 0) Array.Clear(crcBytes, index + crcOffset, 4);
    }
    File.WriteAllBytes(zeroCrc, crcBytes);
    var zeroCrcHash = Hash(zeroCrc);
    await Run("archive-invalid-crc-zero", 2, false, "--archive-list", zeroCrc);
    var crcOutput = Text("archive-crc-existing-output.7z", "keep existing output bytes");
    var crcOutputHash = Hash(crcOutput);
    await Run("archive-invalid-crc-repack-preserves-output", 2, false, "--archive-repack", zeroCrc, crcOutput);
    Check("CRC failure preserves repack input and output", Hash(zeroCrc) == zeroCrcHash && Hash(crcOutput) == crcOutputHash);
    var crcExport = Text("archive-crc-existing-export.txt", "keep existing export bytes");
    var crcExportHash = Hash(crcExport);
    await Run("archive-invalid-crc-export-preserves-output", 2, false, "--archive-entry", zeroCrc, "data.txt", crcExport);
    Check("CRC failure preserves export input and output", Hash(zeroCrc) == zeroCrcHash && Hash(crcExport) == crcExportHash);
    await Run("archive-entry-invalid-name", 2, false, "--archive-entry", solid, "../exe/test.exe", Path.Combine(fixtures, "archive-invalid-export.bin"));
    await Run("archive-invalid-parameters", 2, false, "--archive-entry", solid);

    var protectedInput = Path.Combine(fixtures, "archive-protected-source.7z");
    File.Copy(solid, protectedInput);
    var originalHash = Hash(protectedInput);
    await Run("archive-repack-input-output-same", 2, false, "--archive-repack", protectedInput, protectedInput);
    Check("same archive output leaves original", Hash(protectedInput) == originalHash);
    var readonlyOutput = Path.Combine(fixtures, "archive-readonly-output.7z");
    File.Copy(solid, readonlyOutput);
    var outputHash = Hash(readonlyOutput);
    var attributes = File.GetAttributes(readonlyOutput);
    try
    {
        File.SetAttributes(readonlyOutput, attributes | FileAttributes.ReadOnly);
        await Run("archive-repack-readonly-output", 2, false, "--archive-repack", protectedInput, readonlyOutput);
        Check("readonly archive output preserved", Hash(readonlyOutput) == outputHash && Hash(protectedInput) == originalHash);
    }
    finally { File.SetAttributes(readonlyOutput, attributes); }
    if (!OperatingSystem.IsWindows())
    {
        var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var privateArchive = Text("archive-private-mode.7z", "existing private archive");
        File.SetUnixFileMode(privateArchive, privateMode);
        await Run("archive-repack-preserves-unix-mode", 0, true, "--archive-repack", solid, privateArchive);
        Check("repack preserves Unix 0600", File.GetUnixFileMode(privateArchive) == privateMode);
        var privateEntry = Text("archive-private-entry.bin", "existing private export");
        File.SetUnixFileMode(privateEntry, privateMode);
        await Run("archive-export-preserves-unix-mode", 0, true, "--archive-entry", solid, "exe/test.exe", privateEntry);
        Check("export preserves Unix 0600 and bytes", File.GetUnixFileMode(privateEntry) == privateMode && Hash(privateEntry).Equals(expected["exe/test.exe"].Sha, StringComparison.OrdinalIgnoreCase));
    }
    else assertions.Add(new("archive Unix 0600 preservation", "skipped", "Windows does not expose UnixFileMode."));
    if (OperatingSystem.IsMacOS())
    {
        var alias = Path.Combine(Path.GetDirectoryName(protectedInput)!, Path.GetFileName(protectedInput).ToUpperInvariant());
        if (File.Exists(alias))
        {
            await Run("archive-mac-case-alias-export-rejected", 2, false, "--archive-entry", protectedInput, "exe/test.exe", alias);
            Check("Mac alias export preserves original", Hash(protectedInput) == originalHash);
            await Run("archive-mac-case-alias-repack-rejected", 2, false, "--archive-repack", protectedInput, alias);
            Check("Mac alias repack preserves original", Hash(protectedInput) == originalHash);
        }
        else assertions.Add(new("archive Mac case alias preservation", "skipped", "The alternate casing does not resolve to the existing file on this case-sensitive filesystem."));
    }
    else assertions.Add(new("archive Mac case alias preservation", "skipped", "The current host is not macOS; actual APFS behavior requires the macOS CI runner."));
    var insideOutput = Path.Combine(createRoot, "self.7z");
    await Run("archive-create-output-inside-source", 0, true, "--archive-create", createRoot, insideOutput);
    var insideEntries = Entries(await Run("archive-list-inside-source", 0, true, "--archive-list", insideOutput));
    Check("archive inside output excludes itself and temporary files", insideEntries.Count > 0 && insideEntries.All(e => e.Path != "self.7z" && !e.Path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
    await Run("archive-inside-source-content", 0, true, "--archive-compare", created, insideOutput);
    await Run("archive-recreate-output-inside-source", 0, true, "--archive-create", createRoot, insideOutput);
    var recreatedEntries = Entries(await Run("archive-list-recreated-inside", 0, true, "--archive-list", insideOutput));
    Check("archive recreate excludes existing output", recreatedEntries.Count > 0 && recreatedEntries.All(e => e.Path != "self.7z" && !e.Path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
    Check("archive create does not alter source", File.ReadAllBytes(binary).SequenceEqual(new byte[] { 0, 255, 1, 0, 127, 3 }));
    await ArchiveFormatCases();
}

async Task ArchiveFormatCases()
{
    var root = Path.Combine(fixtures, "formats-source"); Directory.CreateDirectory(Path.Combine(root, "empty")); Directory.CreateDirectory(Path.Combine(root, "folder"));
    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["folder/data.bin"] = [0, 255, 10, 13, 128, 0], ["日本語.txt"] = utf8.GetBytes("本文\r\nfinal") };
    foreach (var pair in expected) File.WriteAllBytes(Path.Combine(root, pair.Key), pair.Value);
    var baseline = Path.Combine(fixtures, "formats-baseline.7z");
    await Run("formats-baseline", 0, true, "--archive-create", root, baseline);
    foreach (var extension in new[] { "zip", "jar", "ear", "war", "xpi", "tar", "tar.gz", "tgz", "tar.bz2", "tbz2", "tbz" })
    {
        var archive = Path.Combine(fixtures, "formats." + extension);
        await Run("formats-create-" + extension, 0, true, "--archive-create", root, archive);
        await Run("formats-compare-" + extension, 0, true, "--archive-compare", baseline, archive);
        var entry = Path.Combine(fixtures, "formats-entry-" + extension + ".bin");
        await Run("formats-entry-" + extension, 0, true, "--archive-entry", archive, "folder/data.bin", entry);
        Check("formats entry bytes " + extension, File.Exists(entry) && File.ReadAllBytes(entry).SequenceEqual(expected["folder/data.bin"]));
        var repacked = Path.Combine(fixtures, "formats-repacked." + extension);
        await Run("formats-repack-" + extension, 0, true, "--archive-repack", baseline, repacked);
        await Run("formats-repack-compare-" + extension, 0, true, "--archive-compare", baseline, repacked);
        var extracted = Path.Combine(fixtures, "formats-extracted-" + extension);
        await Run("formats-extract-" + extension, 0, true, "--archive-extract", archive, extracted);
        CheckExtracted("formats extract " + extension, extracted, expected);
        if (extension is "zip" or "jar" or "ear" or "war" or "xpi")
        {
            using var zip = ZipFile.OpenRead(archive);
            Check("independent ZIP names " + extension, zip.Entries.Select(e => e.FullName.TrimEnd('/')).Order().SequenceEqual(new[] { "empty", "folder", "folder/data.bin", "日本語.txt" }.Order()));
            foreach (var pair in expected) { using var stream = zip.GetEntry(pair.Key)!.Open(); using var data = new MemoryStream(); stream.CopyTo(data); Check("independent ZIP bytes " + extension + " " + pair.Key, data.ToArray().SequenceEqual(pair.Value)); }
        }
        else if (extension is "tar" or "tar.gz" or "tgz")
        {
            using var input = File.OpenRead(archive);
            using Stream decoded = extension == "tar" ? input : new GZipStream(input, CompressionMode.Decompress);
            using var tar = new TarReader(decoded); var found = new Dictionary<string, byte[]>(); var directories = new HashSet<string>();
            while (tar.GetNextEntry() is { } item)
            {
                if (item.EntryType == TarEntryType.Directory) directories.Add(item.Name.TrimEnd('/'));
                else { using var bytes = new MemoryStream(); item.DataStream!.CopyTo(bytes); found.Add(item.Name, bytes.ToArray()); }
            }
            Check("independent TAR contents " + extension, directories.SetEquals(["empty", "folder"]) && found.Count == expected.Count && expected.All(p => found.TryGetValue(p.Key, out var bytes) && bytes.SequenceEqual(p.Value)));
        }
        else
        {
            Check("BZip2 container signature " + extension, File.ReadAllBytes(archive).AsSpan(0, 3).SequenceEqual("BZh"u8));
            await VerifyBzipWithPython(extension, archive, expected);
        }
    }
    var originalHashes = expected.ToDictionary(p => p.Key, p => Convert.ToHexString(SHA256.HashData(p.Value)));
    Check("format creation preserves input", originalHashes.All(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, p.Key)))) == p.Value));
    var rootTar = Path.Combine(fixtures, "formats-independent-root.tar");
    using (var verifier = await RunArchiveVerifier("create-root", rootTar, root)) { }
    await Run("formats-independent-root-compare", 0, true, "--archive-compare", baseline, rootTar);
    var rootExtracted = Path.Combine(fixtures, "formats-independent-root-extracted");
    await Run("formats-independent-root-extract", 0, true, "--archive-extract", rootTar, rootExtracted); CheckExtracted("independent TAR root extraction", rootExtracted, expected);
    var concatenated = Path.Combine(fixtures, "formats-concatenated.tar.gz"); var tarBytes = File.ReadAllBytes(Path.Combine(fixtures, "formats.tar"));
    using (var outputStream = File.Create(concatenated))
    {
        using (var first = new GZipStream(outputStream, CompressionLevel.Optimal, leaveOpen: true)) first.Write(tarBytes.AsSpan(0, tarBytes.Length / 2));
        using (var second = new GZipStream(outputStream, CompressionLevel.Optimal, leaveOpen: true)) second.Write(tarBytes.AsSpan(tarBytes.Length / 2));
    }
    await Run("formats-concatenated-gzip-compare", 0, true, "--archive-compare", baseline, concatenated);
    var existingDirectory = Path.Combine(fixtures, "formats-existing-directory"); Directory.CreateDirectory(existingDirectory); var marker = Path.Combine(existingDirectory, "keep.txt"); File.WriteAllText(marker, "keep");
    await Run("formats-extract-existing-directory", 2, false, "--archive-extract", baseline, existingDirectory);
    Check("extract preserves existing directory", File.ReadAllText(marker) == "keep" && Directory.GetFileSystemEntries(existingDirectory).Length == 1);
    var existingFile = Text("formats-existing-output", "keep existing bytes");
    await Run("formats-extract-existing-file", 2, false, "--archive-extract", baseline, existingFile);
    Check("extract preserves existing file", File.ReadAllText(existingFile) == "keep existing bytes");
    await Run("formats-create-unknown-output", 2, false, "--archive-create", root, existingFile);
    await Run("formats-repack-unknown-output", 2, false, "--archive-repack", baseline, existingFile);
    Check("unknown output format preserves existing file", File.ReadAllText(existingFile) == "keep existing bytes");
    foreach (var name in new[] { "7Zip.LZMA2.Aes.7z", "Rar.encrypted_filesAndHeader.rar", "Rar5.encrypted_filesAndHeader.rar", "Zip.deflate.WinzipAES.zip" })
    {
        var password = name.EndsWith(".7z") ? "testpassword\n" : "test\n";
        var destination = Path.Combine(fixtures, "formats-encrypted-extracted-" + name);
        var source = Path.GetFullPath(Path.Combine("tests/Fixtures/Archives", name));
        await RunWithInput("formats-extract-encrypted-" + name, 0, true, password, "--archive-extract", source, destination, "--password-stdin");
        var originalManifest = JsonDocument.Parse(File.ReadAllText("tests/Fixtures/Archives/manifest.json"));
        using (originalManifest)
        {
            var originals = originalManifest.RootElement.GetProperty("expectedEntries").EnumerateArray().ToArray();
            Check("encrypted extract exact files " + name, originals.All(p => File.Exists(Path.Combine(destination, p.GetProperty("path").GetString()!)) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(destination, p.GetProperty("path").GetString()!)))) == p.GetProperty("sha256").GetString()));
        }
    }
    var badTar = Tar("formats-bad-checksum.tar", ("good.txt", "content"));
    var badTarBytes = File.ReadAllBytes(badTar); badTarBytes[0] ^= 1; File.WriteAllBytes(badTar, badTarBytes);
    var linkedTar = Path.Combine(fixtures, "formats-link.tar");
    using (var stream = File.Create(linkedTar)) using (var tar = new TarWriter(stream)) tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "../outside" });
    foreach (var bad in new[] { Zip("formats-parent-collision.zip", ("file", "one"), ("file/child", "two")), Zip("formats-case-collision.zip", ("A.txt", "one"), ("a.txt", "two")), Zip("formats-unsafe.zip", ("../escape", "bad")), Path.Combine(fixtures, "archive-invalid-crc-zero.zip"), badTar, linkedTar, Tar("formats-parent-collision.tar", ("file", "one"), ("file/child", "two")) })
    {
        var destination = Path.Combine(fixtures, "formats-invalid-" + Path.GetFileName(bad)); var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bad)));
        await Run("formats-extract-reject-" + Path.GetFileName(bad), 2, false, "--archive-extract", bad, destination);
        Check("invalid extraction rollback " + Path.GetFileName(bad), !Directory.Exists(destination) && !File.Exists(destination) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bad))) == hash);
    }
    foreach (var missing in new[] { 1, 4, 8 })
    {
        var truncated = Path.Combine(fixtures, $"formats-truncated-{missing}.tar.gz");
        var full = File.ReadAllBytes(Path.Combine(fixtures, "formats.tar.gz")); File.WriteAllBytes(truncated, full[..^missing]);
        await Run("formats-truncated-gzip-list-" + missing, 2, false, "--archive-list", truncated);
        var destination = Path.Combine(fixtures, "formats-truncated-extract-" + missing);
        await Run("formats-truncated-gzip-extract-" + missing, 2, false, "--archive-extract", truncated, destination);
        Check("truncated gzip leaves no extraction " + missing, !Directory.Exists(destination));
        var keep = Text($"formats-truncated-keep-{missing}.zip", "keep output");
        await Run("formats-truncated-gzip-repack-" + missing, 2, false, "--archive-repack", truncated, keep);
        Check("truncated gzip preserves repack output " + missing, File.ReadAllText(keep) == "keep output");
    }
    Check("extract removes staging directories", !Directory.GetDirectories(fixtures).Any(path => Path.GetFileName(path).StartsWith(".diffbeacon-", StringComparison.Ordinal)));
    var parentAmplification = Path.Combine(fixtures, "formats-implicit-parent-limit.zip");
    using (var stream = File.Create(parentAmplification)) using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        for (var branch = 0; branch < 64; branch++) zip.CreateEntry($"branch-{branch}/" + string.Concat(Enumerable.Repeat("d/", 1600)) + "file");
    await Run("formats-implicit-parent-limit", 2, false, "--archive-list", parentAmplification);
    var boundedRepack = Text("formats-parent-limit-keep.zip", "keep original output");
    await Run("formats-implicit-parent-repack", 2, false, "--archive-repack", parentAmplification, boundedRepack);
    Check("parent limit preserves repack output", File.ReadAllText(boundedRepack) == "keep original output");
    void CheckExtracted(string name, string directory, Dictionary<string, byte[]> contents)
        => Check(name, Directory.Exists(Path.Combine(directory, "empty")) && Directory.Exists(Path.Combine(directory, "folder")) && Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length == contents.Count && contents.All(p => File.Exists(Path.Combine(directory, p.Key)) && File.ReadAllBytes(Path.Combine(directory, p.Key)).SequenceEqual(p.Value)));
}

async Task VerifyBzipWithPython(string label, string archive, Dictionary<string, byte[]> expected)
{
    using var result = await RunArchiveVerifier("read", archive);
    var entries = result.RootElement.GetProperty("entries").EnumerateArray().ToArray();
    Check("independent BZip2 exact contents " + label, entries.Length == expected.Count + 2
        && entries.Where(e => e.GetProperty("directory").GetBoolean()).Select(e => e.GetProperty("path").GetString()).Order().SequenceEqual(new[] { "empty", "folder" }.Order())
        && expected.All(p => entries.Any(e => e.GetProperty("path").GetString() == p.Key && !e.GetProperty("directory").GetBoolean() && e.GetProperty("size").GetInt64() == p.Value.Length && e.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(p.Value)))));
}

async Task<JsonDocument> RunArchiveVerifier(string operation, string archive, string? source = null)
{
    var python = Option("--python") ?? "python";
    var script = Path.GetFullPath("tests/DiffBeacon.E2E/archive_verifier.py");
    var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in new[] { script, operation, archive }) info.ArgumentList.Add(argument);
    if (source is not null) info.ArgumentList.Add(source);
    using var process = Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
    var response = await stdout; var diagnostic = await stderr;
    File.AppendAllText(Path.Combine(output, "independent-archive.log"), $"{python} {script} {operation} {archive}\nexit={process.ExitCode}\n{response}\n{diagnostic}", utf8);
    Check("independent archive verifier " + operation + " " + Path.GetFileName(archive), process.ExitCode == 0, diagnostic);
    return JsonDocument.Parse(response);
}

async Task<CommandResult> Run(string name, int expectedExit, bool json, params string[] arguments)
    => await RunWithInput(name, expectedExit, json, null, arguments);

async Task<CommandResult> RunWithInput(string name, int expectedExit, bool json, string? standardInput, params string[] arguments)
{
    var start = new ProcessStartInfo(app.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : app)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = standardInput is not null,
        StandardInputEncoding = standardInput is null ? null : new UTF8Encoding(false),
        StandardOutputEncoding = new UTF8Encoding(false),
        StandardErrorEncoding = new UTF8Encoding(false),
        UseShellExecute = false,
        CreateNoWindow = true
    };
    if (app.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(app);
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = new Process { StartInfo = start };
    var timer = Stopwatch.StartNew();
    var exit = -1;
    var stdout = "";
    var stderr = "";
    var timedOut = false;
    try
    {
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            timedOut = true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            stderr += "検証の制限時間 30 秒を超えました。\n";
        }
        stdout = await stdoutTask;
        stderr += await stderrTask;
        exit = timedOut ? -2 : process.ExitCode;
    }
    catch (Exception exception) { stderr += exception.ToString(); }
    timer.Stop();
    var result = new CommandResult(name, arguments, exit, stdout, stderr, timer.ElapsedMilliseconds);
    commands.Add(result);
    var prefix = Path.Combine(output, $"{++commandIndex:D2}-{name}");
    await File.WriteAllTextAsync(prefix + ".stdout.txt", stdout, utf8);
    await File.WriteAllTextAsync(prefix + ".stderr.txt", stderr, utf8);
    Check(name + " exit", exit == expectedExit, $"expected={expectedExit}, actual={exit}; {stderr}");
    if (json)
    {
        try { using var document = JsonDocument.Parse(stdout); Check(name + " JSON", document.RootElement.ValueKind == JsonValueKind.Object); }
        catch (JsonException exception) { Check(name + " JSON", false, exception.Message); }
    }
    return result;
}

try
{
    Check("application exists", File.Exists(app), app);
    if (!File.Exists(app)) throw new FileNotFoundException("検証対象をビルドしてください。", app);
    if (args.Contains("--image-offsets-only", StringComparer.Ordinal))
    {
        await ImageOffsetScenarios.RunAsync(output, fixtures, Run, Check);
    }
    else if (args.Contains("--image-transforms-only", StringComparer.Ordinal))
    {
        await ImageTransformScenarios.RunAsync(output, fixtures, Run, Check);
    }
    else if (args.Contains("--image-project-only", StringComparer.Ordinal))
    {
        await ImageProjectScenarios.RunAsync(output, fixtures, Run, Check);
    }
    else if (args.Contains("--tiff-only", StringComparer.Ordinal))
    {
        await ImageTiffScenarios.RunAsync(output, fixtures, Run, Check);
    }
    else if (args.Contains("--apng-only", StringComparer.Ordinal))
    {
        await ImageApngScenarios.RunAsync(output, fixtures, Run, Check);
    }
    else if (args.Contains("--image-copy-only", StringComparer.Ordinal))
    {
        await ImageCopyScenarios.RunAsync(output, fixtures, Run, Check, Skip);
    }
    else if (args.Contains("--image-highlight-only", StringComparer.Ordinal))
    {
        await ImageHighlightScenarios.RunAsync(output, fixtures, Run, Check);
    }
    else if (args.Contains("--image-regions-only", StringComparer.Ordinal))
    {
        await ImageRegionScenarios.RunAsync(output, fixtures, Run, Check);
    }
    else if (args.Contains("--image-reports-only", StringComparer.Ordinal))
    {
        await ImageReportScenarios.RunAsync(output, fixtures, Run, Check, Skip);
    }
    else if (args.Contains("--image-only", StringComparer.Ordinal))
    {
        await ImageFrameScenarios.RunAsync(output, fixtures, Run, Check);
    }
    else if (args.Contains("--gnu-table-only", StringComparer.Ordinal))
    {
        await GnuTableScenarios.RunAsync(output, fixtures, Run, Check);
    }
    else if (args.Contains("--gnu-text-only", StringComparer.Ordinal))
    {
        await GnuTextScenarios.RunAsync(output, Run, Check);
    }
    else if (args.Contains("--gnu-line-only", StringComparer.Ordinal))
    {
        await GnuLineScenarios.RunAsync(output, fixtures, Run, Check);
    }
    else if (args.Contains("--line-alignment-only", StringComparer.Ordinal))
    {
        await LineAlignmentScenarios.RunAsync(output, fixtures, Run, Check);
    }
    else if (args.Contains("--word-diff-only", StringComparer.Ordinal))
    {
        await WordDiffScenarios.RunAsync(output, fixtures, Run, Check);
    }
    else if (args.Contains("--reports-only", StringComparer.Ordinal))
    {
        await ReportCases();
        await ImageReportScenarios.RunAsync(output, fixtures, Run, Check, Skip);
    }
    else if (args.Contains("--packaging-only", StringComparer.Ordinal))
    {
        await PackagingCases();
    }
    else if (args.Contains("--projects-only", StringComparer.Ordinal))
    {
        await ProjectWorkspaceCases();
    }
    else if (args.Contains("--archives-only", StringComparer.Ordinal))
    {
        await ArchiveCases();
    }
    else if (args.Contains("--legacy-comments-only", StringComparer.Ordinal))
    {
        await LegacyCommentCases();
    }
    else if (args.Contains("--text-advanced-only", StringComparer.Ordinal))
    {
        await TextAdvancedCases();
    }
    else if (args.Contains("--provider-boundaries-only", StringComparer.Ordinal))
    {
        await ProviderBoundaryCases();
    }
    else
    {
    await ArchiveCases();
    await WordDiffScenarios.RunAsync(output, fixtures, Run, Check);
    await LineAlignmentScenarios.RunAsync(output, fixtures, Run, Check);
    await GnuLineScenarios.RunAsync(output, fixtures, Run, Check);
    await GnuTextScenarios.RunAsync(output, Run, Check);
    await GnuTableScenarios.RunAsync(output, fixtures, Run, Check);
    await ImageFrameScenarios.RunAsync(output, fixtures, Run, Check);
    await ImageRegionScenarios.RunAsync(output, fixtures, Run, Check);
    await ImageApngScenarios.RunAsync(output, fixtures, Run, Check);
    await ImageTiffScenarios.RunAsync(output, fixtures, Run, Check);
    await ImageProjectScenarios.RunAsync(output, fixtures, Run, Check);
    await ImageTransformScenarios.RunAsync(output, fixtures, Run, Check);
    await ImageHighlightScenarios.RunAsync(output, fixtures, Run, Check);
    await ImageOffsetScenarios.RunAsync(output, fixtures, Run, Check);
    await ImageCopyScenarios.RunAsync(output, fixtures, Run, Check, Skip);
    await TextAdvancedCases();
    await ProjectWorkspaceCases();
    await PackagingCases();
    await ReportCases();
    await ImageReportScenarios.RunAsync(output, fixtures, Run, Check, Skip);
    var left = Text("left.txt", "alpha\nbeta\n");
    var equal = Text("equal.txt", "alpha\nbeta\n");
    var right = Text("right.txt", "alpha\nchanged\n");
    await Run("equal", 0, true, "--compare", left, equal);
    await Run("different", 1, true, "--compare", left, right);
    await Run("ignore-case", 0, true, "--compare", Text("case.txt", "ALPHA\nBETA\n"), left, "--ignore-case");
    await Run("ignore-space", 0, true, "--compare", Text("spaces.txt", "a l p h a\nbeta \n"), left, "--ignore-space");
    await Run("ignore-blank", 0, true, "--compare", Text("blanks.txt", "alpha\n\nbeta\n"), left, "--ignore-blank");
    await Run("final-newline", 1, true, "--compare", Text("no-final.txt", "alpha\nbeta"), left);
    await Run("utf16-bom", 0, true, "--compare", Text("utf16.txt", "alpha\nbeta\n", new UnicodeEncoding(false, true)), left);
    var newlineVariant = Text("crlf.txt", "alpha\r\nbeta\r\n");
    // 改行の差を無視する仕様の比較経路と、保存時のバイト保持を別々に確認する。
    await Run("line-endings", 0, true, "--compare", newlineVariant, left);

    var baseFile = Text("base.txt", "one\ntwo\nthree\n");
    var mergeOutput = Path.Combine(fixtures, "merge-clean.txt");
    await Run("merge-clean", 0, false, "--merge", baseFile,
        Text("merge-left.txt", "ONE\ntwo\nthree\n"), Text("merge-right.txt", "one\ntwo\nTHREE\n"), mergeOutput);
    Check("merge independent edits", File.Exists(mergeOutput) && File.ReadAllText(mergeOutput) == "ONE\ntwo\nTHREE\n");
    var conflictOutput = Path.Combine(fixtures, "merge-conflict.txt");
    await Run("merge-conflict", 1, false, "--merge", baseFile,
        Text("conflict-left.txt", "one\nLEFT\nthree\n"), Text("conflict-right.txt", "one\nRIGHT\nthree\n"), conflictOutput);
    Check("merge conflict markers", File.Exists(conflictOutput) && File.ReadAllText(conflictOutput).Contains("<<<<<<<", StringComparison.Ordinal));

    foreach (var (label, encoding, original, changed) in new (string, Encoding, string, string)[] {
        ("utf8-crlf", utf8, "one\r\ntwo\r\n", "one\r\nchanged\r\n"),
        ("utf8-bom", new UTF8Encoding(true), "one\ntwo\n", "one\nchanged\n"),
        ("utf16-bom", new UnicodeEncoding(false, true), "one\r\ntwo", "one\r\nchanged"),
        ("utf8-no-final", utf8, "one\ntwo", "one\nchanged") })
    {
        var source = Text(label + "-source.txt", original, encoding);
        var target = Text(label + "-target.txt", changed, encoding);
        var patch = Path.Combine(fixtures, label + ".patch");
        var applied = Path.Combine(fixtures, label + "-applied.txt");
        await Run("patch-create-" + label, 0, false, "--patch-create", source, target, patch);
        await Run("patch-apply-" + label, 0, false, "--patch-apply", source, patch, applied);
        Check("patch bytes " + label, File.Exists(applied) && File.ReadAllBytes(applied).SequenceEqual(File.ReadAllBytes(target)));
    }
    var externalPatch = Text("external.patch", "--- a/source.txt\n+++ b/target.txt\n@@ -1,2 +1,2 @@\n alpha\n-beta\n+changed\n");
    var externalOutput = Path.Combine(fixtures, "external-applied.txt");
    await Run("external-patch", 0, false, "--patch-apply", left, externalPatch, externalOutput);
    Check("external patch content", File.Exists(externalOutput) && File.ReadAllText(externalOutput) == "alpha\nchanged\n");

    var directoryLeft = Path.Combine(fixtures, "directory-left");
    var directoryRight = Path.Combine(fixtures, "directory-right");
    Text("directory-left/deep/inner/same.txt", "same");
    Text("directory-right/deep/inner/same.txt", "same");
    await Run("directory-equal", 0, true, "--directory", directoryLeft, directoryRight);
    Text("directory-left/deep/only-left.txt", "left");
    Text("directory-right/only-right.txt", "right");
    var directoryResult = await Run("directory-missing", 1, true, "--directory", directoryLeft, directoryRight);
    Check("directory recursive missing sides", directoryResult.Stdout.Contains("only-left.txt", StringComparison.Ordinal) && directoryResult.Stdout.Contains("only-right.txt", StringComparison.Ordinal));
    try
    {
        var symlink = Path.Combine(directoryLeft, "cycle");
        Directory.CreateSymbolicLink(symlink, directoryLeft);
        links.Add(new(symlink, new DirectoryInfo(symlink).LinkTarget!, false));
        await Run("directory-symlink", 1, true, "--directory", directoryLeft, directoryRight);
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
    { assertions.Add(new("directory symlink", "skipped", exception.Message)); }

    var binaryLeft = Path.Combine(fixtures, "binary-left.bin");
    var binaryRight = Path.Combine(fixtures, "binary-right.bin");
    File.WriteAllBytes(binaryLeft, [0, 1, 2, 0, 255]);
    File.WriteAllBytes(binaryRight, [0, 1, 2, 0, 255]);
    await Run("binary-equal", 0, true, "--binary", binaryLeft, binaryRight);
    File.WriteAllBytes(binaryRight, [0, 1, 3, 0, 255]);
    await Run("binary-different", 1, true, "--binary", binaryLeft, binaryRight);
    File.WriteAllBytes(binaryRight, [0, 1, 2, 0, 255, 0]);
    await Run("binary-length", 1, true, "--binary", binaryLeft, binaryRight);
    await Run("missing-path", 2, false, "--compare", Path.Combine(fixtures, "missing.txt"), right);
    await Run("invalid-regex", 2, false, "--compare", left, right, "--ignore-regex", "[");
    await Run("missing-arguments", 2, false, "--compare", left);
    var invalidOutput = Path.Combine(fixtures, "invalid-output.txt");
    await Run("invalid-patch", 2, false, "--patch-apply", left, Text("invalid.patch", "not a patch"), invalidOutput);
    Check("invalid patch leaves no output", !File.Exists(invalidOutput));

    var projectFile = Text("project.WinMerge", "<project><paths><left>left.txt</left><middle>base.txt</middle><right>right.txt</right><window-type>1</window-type><ignore-case>1</ignore-case><white-spaces>2</white-spaces><ignore-blank-lines>1</ignore-blank-lines></paths></project>");
    var savedProject = Path.Combine(fixtures, "project.json");
    var savedProjectAgain = Path.Combine(fixtures, "project-again.json");
    await Run("project-import", 0, true, "--project-copy", projectFile, savedProject);
    if (File.Exists(savedProject))
    {
        using var project = JsonDocument.Parse(File.ReadAllText(savedProject));
        var values = project.RootElement;
        Check("project paths resolved", values.GetProperty("leftPath").GetString() == left && values.GetProperty("basePath").GetString() == baseFile && values.GetProperty("rightPath").GetString() == right);
        Check("project basic options", values.GetProperty("ignoreCase").GetBoolean() && values.GetProperty("ignoreWhitespace").GetBoolean() && values.GetProperty("ignoreBlankLines").GetBoolean());
    }
    else Check("project file created", false);
    await Run("project-roundtrip", 0, true, "--project-copy", savedProject, savedProjectAgain);
    Check("project roundtrip bytes", File.Exists(savedProject) && File.Exists(savedProjectAgain) && File.ReadAllBytes(savedProject).SequenceEqual(File.ReadAllBytes(savedProjectAgain)));
    await Run("project-invalid-xml", 2, false, "--project-copy", Text("malformed.WinMerge", "<project><paths>"), Path.Combine(fixtures, "malformed.json"));
    await Run("project-dtd", 2, false, "--project-copy", Text("dtd.WinMerge", "<!DOCTYPE project [<!ENTITY attack 'injected'>]><project><paths><left>&attack;</left><right>right.txt</right></paths></project>"), Path.Combine(fixtures, "dtd.json"));

    var htmlPath = Path.Combine(fixtures, "report.html");
    await Run("html-report", 0, true, "--report", Text("html-left.txt", "<script>alert('x')</script>\n& special\n"), right, htmlPath);
    Check("HTML input escaped", File.Exists(htmlPath) && File.ReadAllText(htmlPath).Contains("&lt;script&gt;", StringComparison.Ordinal) && !File.ReadAllText(htmlPath).Contains("<script>", StringComparison.Ordinal) && File.ReadAllText(htmlPath).Contains("&amp; special", StringComparison.Ordinal));

    var copySource = Path.Combine(fixtures, "copy-source");
    var copyDestination = Path.Combine(fixtures, "copy-destination");
    Text("copy-source/nested/deep/source.txt", "copied\r\n", new UnicodeEncoding(false, true));
    Text("copy-source/nested/other.txt", "other");
    var sentinel = Text("copy-destination/sentinel.txt", "untouched");
    await Run("folder-copy", 0, true, "--folder-copy", copySource, copyDestination, "nested");
    Check("folder copy contents", File.Exists(Path.Combine(copyDestination, "nested/deep/source.txt")) && File.ReadAllBytes(Path.Combine(copySource, "nested/deep/source.txt")).SequenceEqual(File.ReadAllBytes(Path.Combine(copyDestination, "nested/deep/source.txt"))) && File.ReadAllText(Path.Combine(copyDestination, "nested/other.txt")) == "other");
    var escapedSource = Text("escape-source.txt", "must not overwrite");
    var destinationSnapshot = Directory.GetFiles(copyDestination, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).Select(p => (Path: p, Bytes: Convert.ToBase64String(File.ReadAllBytes(p)))).ToArray();
    foreach (var (label, attemptedPath) in new[] { ("parent", "../escape-source.txt"), ("backslash", "..\\escape-source.txt"), ("absolute", escapedSource) })
        await Run("folder-copy-blocked-" + label, 2, false, "--folder-copy", copySource, copyDestination, attemptedPath);
    Check("blocked copy destination untouched", File.ReadAllText(sentinel) == "untouched" && destinationSnapshot.SequenceEqual(Directory.GetFiles(copyDestination, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).Select(p => (Path: p, Bytes: Convert.ToBase64String(File.ReadAllBytes(p))))));
    var overlapRoot = Path.Combine(fixtures, "copy-overlap");
    var overlapInput = Text("copy-overlap/x/x/inner.txt", "overlap must remain intact\r\n", new UnicodeEncoding(false, true));
    var overlapBytes = File.ReadAllBytes(overlapInput);
    await Run("folder-copy-ancestor-overlap", 2, false, "--folder-copy", Path.Combine(overlapRoot, "x"), overlapRoot, "x");
    Check("ancestor overlap source untouched", File.Exists(overlapInput) && File.ReadAllBytes(overlapInput).SequenceEqual(overlapBytes) && !File.Exists(Path.Combine(overlapRoot, "x/inner.txt")));
    try
    {
        var sourceLink = Path.Combine(copySource, "linked");
        Directory.CreateSymbolicLink(sourceLink, Path.Combine(copySource, "nested"));
        links.Add(new(sourceLink, new DirectoryInfo(sourceLink).LinkTarget!, false));
        await Run("folder-copy-source-link", 2, false, "--folder-copy", copySource, copyDestination, "linked");
        Check("source link copy leaves no destination", !Directory.Exists(Path.Combine(copyDestination, "linked")));
        var outside = Path.Combine(fixtures, "copy-outside");
        var outsideSentinel = Text("copy-outside/sentinel.txt", "outside unchanged");
        var destinationLink = Path.Combine(copyDestination, "nested-link");
        Directory.CreateSymbolicLink(destinationLink, outside);
        links.Add(new(destinationLink, new DirectoryInfo(destinationLink).LinkTarget!, false));
        Text("copy-source/nested-link/new.txt", "must not escape");
        await Run("folder-copy-destination-link", 2, false, "--folder-copy", copySource, copyDestination, "nested-link");
        Check("destination link target untouched", File.ReadAllText(outsideSentinel) == "outside unchanged" && Directory.GetFiles(outside).Length == 1);
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
    { assertions.Add(new("folder copy symlink rejection", "skipped", exception.Message)); }

    var csvLeft = Text("table-left.csv", "name,value\n\"first\nsecond\",\"a,b\"\n");
    var csvRight = Text("table-right.csv", "\"name\",\"value\"\r\n\"first\nsecond\",\"a,b\"\r\n");
    var tableResult = await Run("table-quoted-newline", 0, true, "--table", csvLeft, csvRight);
    if (tableResult.ExitCode == 0)
    {
        using var table = JsonDocument.Parse(tableResult.Stdout);
        Check("table dimensions", table.RootElement.GetProperty("rows").GetInt32() == 2 && table.RootElement.GetProperty("cols").GetInt32() == 2);
    }
    await Run("table-difference", 1, true, "--table", csvLeft, Text("table-different.csv", "name,value\n\"first\nsecond\",changed\n"));
    await Run("table-invalid-quote", 2, false, "--table", Text("table-malformed.csv", "name,value\n\"unclosed,value"), csvRight);
    await Run("json-key-order", 0, true, "--json", Text("json-left.json", "{\"a\":1,\"b\":{\"x\":true,\"y\":null}}"), Text("json-right.json", "{\"b\":{\"y\":null,\"x\":true},\"a\":1}"));
    await Run("json-array-order", 1, true, "--json", Text("array-left.json", "[1,2,3]"), Text("array-right.json", "[3,2,1]"));
    await Run("json-number-equivalence", 0, true, "--json", Text("number-left.json", "{\"n\":1.0,\"exp\":1e2}"), Text("number-right.json", "{\"exp\":100,\"n\":1}"));
    await Run("json-malformed", 2, false, "--json", Text("json-malformed.json", "{invalid"), Text("json-valid.json", "{}"));

    var xmlLeft = Text("xml-left.xml", "<root b=\"2\" a=\"1\"><child>same</child></root>");
    var xmlRight = Text("xml-right.xml", "<root a=\"1\" b=\"2\"><child>same</child></root>");
    await Run("provider-xml-attributes", 0, true, "--provider", "xml", xmlLeft, xmlRight);
    await Run("provider-xml-dtd", 2, false, "--provider", "xml", Text("xml-dtd.xml", "<!DOCTYPE root [<!ENTITY x 'same'>]><root>&x;</root>"), xmlRight);
    await ProviderBoundaryCases();
    var fakeProvider = Path.GetFullPath(Path.Combine("tests/DiffBeacon.FakeProvider/bin/Release/net10.0", OperatingSystem.IsWindows() ? "DiffBeacon.FakeProvider.exe" : "DiffBeacon.FakeProvider"));
    Check("external provider fixture exists", File.Exists(fakeProvider), fakeProvider);
    await Run("external-provider-equal", 0, true, "--external-provider", fakeProvider, left, equal, "txt");
    await Run("external-provider-different", 1, true, "--external-provider", fakeProvider, left, right, "txt");
    await Run("external-provider-invalid-protocol", 2, false, "--external-provider", fakeProvider, left, right, "invalid");
    await Run("external-provider-response-limit", 2, false, "--external-provider", fakeProvider, left, right, "flood");
    var htmlLeft = Text("provider-left.html", "<main>Hello &amp; world</main><script>window.ignoreA=1;</script><style>.a{color:red}</style>");
    var htmlRight = Text("provider-right.html", "<div>Hello &amp; world</div><script>window.ignoreB=2;</script><style>.b{color:blue}</style>");
    await Run("provider-html-source", 1, true, "--provider", "html-source", htmlLeft, htmlRight);
    await Run("provider-html-visible-text", 0, true, "--provider", "html-text", htmlLeft, htmlRight);
    var docxLeft = Docx("word-left.docx", "Word paragraph same");
    await Run("provider-docx-equal", 0, true, "--provider", "office", docxLeft, Docx("word-equal.docx", "Word paragraph same"));
    await Run("provider-docx-different", 1, true, "--provider", "office", docxLeft, Docx("word-different.docx", "Word paragraph changed"));
    var xlsxLeft = Xlsx("sheet-left.xlsx", "<si><t>alpha</t></si><si><t>beta</t></si>", "<c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c>");
    var xlsxReordered = Xlsx("sheet-reordered.xlsx", "<si><t>beta</t></si><si><t>alpha</t></si>", "<c r=\"B1\" t=\"s\"><v>0</v></c><c r=\"A1\" t=\"s\"><v>1</v></c>");
    await Run("provider-xlsx-strings-cell-order", 0, true, "--provider", "office", xlsxLeft, xlsxReordered);
    var formulaLeft = Xlsx("formula-left.xlsx", "", "<c r=\"A1\"><f>SUM(B1:B2)</f><v>3</v></c>");
    var formulaRight = Xlsx("formula-right.xlsx", "", "<c r=\"A1\"><f>SUM(B1:B3)</f><v>3</v></c>");
    await Run("provider-xlsx-formula", 1, true, "--provider", "office", formulaLeft, formulaRight);
    var archiveEntry = "e2e-no-extract-" + Guid.NewGuid().ToString("N") + "/a.txt";
    var tarLeft = Tar("archive-left.tar", (archiveEntry, "same"), ("b.txt", "other"));
    await Run("provider-tar-equal", 0, true, "--provider", "tar", tarLeft, Tar("archive-equal.tar", ("b.txt", "other"), (archiveEntry, "same")));
    await Run("provider-tar-content", 1, true, "--provider", "tar", tarLeft, Tar("archive-content.tar", (archiveEntry, "changed"), ("b.txt", "other")));
    await Run("provider-tar-name", 1, true, "--provider", "tar", tarLeft, Tar("archive-name.tar", (archiveEntry + ".renamed", "same"), ("b.txt", "other")));
    Check("TAR comparison does not extract", !File.Exists(Path.Combine(Environment.CurrentDirectory, archiveEntry)) && !File.Exists(Path.Combine(fixtures, archiveEntry)));
    await using (var website = new LocalHttpSite(new Dictionary<string, string> { ["/left"] = File.ReadAllText(htmlLeft), ["/right"] = File.ReadAllText(htmlRight) }))
    {
        await Run("provider-web-source-loopback", 1, true, "--provider", "web-source", website.Url + "left", website.Url + "right");
        await Run("provider-web-text-loopback", 0, true, "--provider", "web-text", website.Url + "left", website.Url + "right");
    }

    var filterLeft = Path.Combine(fixtures, "filter-left");
    var filterRight = Path.Combine(fixtures, "filter-right");
    Text("filter-left/keep.txt", "same"); Text("filter-right/keep.txt", "same");
    Text("filter-left/ignored.bin", "left ignored"); Text("filter-right/ignored.bin", "right ignored");
    Text("filter-left/skip/deep.txt", "left skipped"); Text("filter-right/skip/deep.txt", "right skipped");
    var includeFilter = Text("include.flt", "name: E2E include\ndef: include\nf: \\.bin$ ## exclude binaries\nd: skip$\n");
    var filtered = await Run("filter-default-include", 0, true, "--directory", filterLeft, filterRight, "--filter", includeFilter);
    Check("filter omitted files and directories", !filtered.Stdout.Contains("ignored.bin", StringComparison.Ordinal) && !filtered.Stdout.Contains("deep.txt", StringComparison.Ordinal));
    var excludeFilter = Text("exclude.flt", "name: E2E exclude\ndef: exclude\nf: \\.txt$\n");
    await Run("filter-default-exclude-equal", 0, true, "--directory", filterLeft, filterRight, "--filter", excludeFilter);
    Text("filter-right/keep.txt", "changed included");
    await Run("filter-default-exclude-difference", 1, true, "--directory", filterLeft, filterRight, "--filter", excludeFilter);

    if (OperatingSystem.IsWindows())
    {
        var readOnlyOutput = Text("merge-readonly.txt", "protected output\r\n", new UnicodeEncoding(false, true));
        var readOnlyBytes = File.ReadAllBytes(readOnlyOutput);
        var attributes = File.GetAttributes(readOnlyOutput);
        try
        {
            File.SetAttributes(readOnlyOutput, attributes | FileAttributes.ReadOnly);
            await Run("merge-readonly-error", 2, false, "--merge", baseFile, baseFile, baseFile, readOnlyOutput);
            Check("readonly output byte preservation", File.ReadAllBytes(readOnlyOutput).SequenceEqual(readOnlyBytes));
        }
        finally { File.SetAttributes(readOnlyOutput, attributes); }
    }
    else
    {
        var executableOutput = Text("patch-executable.txt", "old output");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(executableOutput, mode);
        await Run("patch-unix-executable", 0, true, "--patch-apply", left, externalPatch, executableOutput);
        Check("patch preserves Unix mode", File.GetUnixFileMode(executableOutput) == mode);
        Check("executable patch content", File.ReadAllText(executableOutput) == "alpha\nchanged\n");
    }

    for (var caseIndex = 0; caseIndex < 60; caseIndex++)
    {
        var seed = 0xD1FF00 + caseIndex;
        var random = new Random(seed);
        var originalLines = Enumerable.Range(0, random.Next(1, 14)).Select(i => $"line-{random.Next(7)}-{i} 日本語").ToList();
        var changedLines = new List<string>(originalLines);
        for (var edit = 0; edit < random.Next(1, 5); edit++)
        {
            var position = random.Next(changedLines.Count + 1);
            switch (random.Next(3))
            {
                case 0: changedLines.Insert(position, $"insert-{seed}-{edit}"); break;
                case 1 when position < changedLines.Count: changedLines.RemoveAt(position); break;
                case 2 when position < changedLines.Count: changedLines[position] += " changed"; break;
            }
        }
        string Render(List<string> lines, bool empty)
        {
            if (empty) return "";
            var text = new StringBuilder();
            for (var index = 0; index < lines.Count; index++)
            {
                text.Append(lines[index]);
                if (index < lines.Count - 1 || random.Next(2) == 0) text.Append(random.Next(2) == 0 ? "\n" : "\r\n");
            }
            return text.ToString();
        }
        var encoding = (caseIndex % 3) switch { 0 => (Encoding)utf8, 1 => new UTF8Encoding(true), _ => new UnicodeEncoding(false, true) };
        var original = Text($"seed-{seed}/source.txt", Render(originalLines, caseIndex % 10 == 0), encoding);
        var changed = Text($"seed-{seed}/target.txt", Render(changedLines, caseIndex % 10 == 1), encoding);
        var patch = Path.Combine(fixtures, $"seed-{seed}/change.patch");
        var applied = Path.Combine(fixtures, $"seed-{seed}/applied.txt");
        seededCases.Add(new(seed, original, changed, patch, applied));
        await Run($"seed-{seed}-create", 0, true, "--patch-create", original, changed, patch);
        await Run($"seed-{seed}-apply", 0, true, "--patch-apply", original, patch, applied);
        Check($"seed-{seed}-bytes", File.Exists(applied) && File.ReadAllBytes(applied).SequenceEqual(File.ReadAllBytes(changed)), $"seed={seed}; target={changed}; applied={applied}");
    }
    }
}
catch (Exception exception) { Check("runner completed", false, exception.ToString()); }
finally
{
    for (var index = 0; index < links.Count; index++)
    {
        var link = links[index];
        try
        {
            var current = link.IsDirectory ? new DirectoryInfo(link.Path).LinkTarget : new FileInfo(link.Path).LinkTarget;
            if (current != link.Target || !link.Path.StartsWith(fixtures + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("作成したリンクと一致しないため解除を中止しました。");
            if (link.IsDirectory) Directory.Delete(link.Path); else File.Delete(link.Path);
            links[index] = link with { Removed = true };
            Check("owned symlink removed", !Directory.Exists(link.Path) && !File.Exists(link.Path), link.Path);
        }
        catch (Exception exception) { Check("owned symlink cleanup", false, exception.Message); }
    }
    var report = new { app, output, passed = assertions.Count(a => a.Status == "passed"), failed = assertions.Count(a => a.Status == "failed"), skipped = assertions.Count(a => a.Status == "skipped"), assertions, commands, seededCases, links };
    await File.WriteAllTextAsync(Path.Combine(output, "assertions.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), utf8);
}
Console.WriteLine($"E2E: passed={assertions.Count(a => a.Status == "passed")}, failed={assertions.Count(a => a.Status == "failed")}, skipped={assertions.Count(a => a.Status == "skipped")}; {output}");
return assertions.Any(a => a.Status == "failed") ? 1 : 0;

sealed record Assertion(string Name, string Status, string Detail);
sealed record CommandResult(string Name, string[] Arguments, int ExitCode, string Stdout, string Stderr, long DurationMilliseconds);
sealed record SeededCase(int Seed, string Source, string Target, string Patch, string Applied);
sealed record LinkEvidence(string Path, string Target, bool Removed, bool IsDirectory = true);
sealed record ArchiveFile(string Path, bool Directory, long Size, string Sha256, bool Encrypted);

sealed class LocalHttpSite : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly IReadOnlyDictionary<string, string> pages;
    private readonly Task serve;
    public string Url { get; }

    public LocalHttpSite(IReadOnlyDictionary<string, string> pages)
    {
        this.pages = pages;
        listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        serve = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var request = await reader.ReadLineAsync(cancellation.Token) ?? "";
                for (var line = 0; line < 64; line++)
                    if (string.IsNullOrEmpty(await reader.ReadLineAsync(cancellation.Token))) break;
                var route = request.Split(' ').ElementAtOrDefault(1) ?? "/";
                var found = pages.TryGetValue(route, out var page);
                var body = Encoding.UTF8.GetBytes(page ?? "not found");
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, cancellation.Token);
                await stream.WriteAsync(body, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (SocketException) when (cancellation.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync();
        listener.Stop();
        await serve;
        cancellation.Dispose();
    }
}
