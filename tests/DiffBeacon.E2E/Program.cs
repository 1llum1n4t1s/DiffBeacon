using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

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
        Check("advanced project fields and rule order preserved", JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
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
            await VerifyBzipWithSystemTar(extension, archive, expected);
        }
    }
    var originalHashes = expected.ToDictionary(p => p.Key, p => Convert.ToHexString(SHA256.HashData(p.Value)));
    Check("format creation preserves input", originalHashes.All(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, p.Key)))) == p.Value));
    var systemTar = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/tar.exe") : "/usr/bin/tar";
    if (File.Exists(systemTar))
    {
        var rootTar = Path.Combine(fixtures, "formats-system-root.tar");
        var info = new ProcessStartInfo(systemTar) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-cf", rootTar, "-C", root, "." }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!; var errors = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); var diagnostic = await errors;
        File.AppendAllText(Path.Combine(output, "system-tar.log"), $"{systemTar} -cf {rootTar} -C {root} .\nexit={process.ExitCode}\n{diagnostic}", utf8);
        Check("independent system TAR creation", process.ExitCode == 0, diagnostic);
        await Run("formats-system-root-compare", 0, true, "--archive-compare", baseline, rootTar);
        var rootExtracted = Path.Combine(fixtures, "formats-system-root-extracted");
        await Run("formats-system-root-extract", 0, true, "--archive-extract", rootTar, rootExtracted); CheckExtracted("system TAR root extraction", rootExtracted, expected);
    }
    else assertions.Add(new("system TAR relative root compatibility", "skipped", "System tar is unavailable."));
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

async Task VerifyBzipWithSystemTar(string label, string archive, Dictionary<string, byte[]> expected)
{
    var tool = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/tar.exe") : "/usr/bin/tar";
    if (!File.Exists(tool)) { assertions.Add(new("independent BZip2 decode " + label, "skipped", "System tar is unavailable.")); return; }
    foreach (var pair in expected)
    {
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-xOf", archive, pair.Key }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!; using var data = new MemoryStream();
        var stderr = process.StandardError.ReadToEndAsync(); var bytes = process.StandardOutput.BaseStream.CopyToAsync(data);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); await bytes; } catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var diagnostic = await stderr;
        File.AppendAllText(Path.Combine(output, "system-tar.log"), $"{tool} -xOf {archive} {pair.Key}\nexit={process.ExitCode}\n{diagnostic}", utf8);
        Check("independent BZip2 bytes " + label + " " + pair.Key, process.ExitCode == 0 && data.ToArray().SequenceEqual(pair.Value), diagnostic);
    }
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
    if (args.Contains("--archives-only", StringComparer.Ordinal))
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
    await TextAdvancedCases();
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
