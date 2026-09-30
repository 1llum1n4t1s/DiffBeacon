using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;

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

async Task<CommandResult> Run(string name, int expectedExit, bool json, params string[] arguments)
{
    var start = new ProcessStartInfo(app.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : app)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
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
    if (args.Contains("--provider-boundaries-only", StringComparer.Ordinal))
    {
        await ProviderBoundaryCases();
    }
    else
    {
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
            var current = new DirectoryInfo(link.Path).LinkTarget;
            if (current != link.Target || !link.Path.StartsWith(fixtures + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("作成したリンクと一致しないため解除を中止しました。");
            Directory.Delete(link.Path);
            links[index] = link with { Removed = true };
            Check("owned symlink removed", !Directory.Exists(link.Path), link.Path);
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
sealed record LinkEvidence(string Path, string Target, bool Removed);

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
