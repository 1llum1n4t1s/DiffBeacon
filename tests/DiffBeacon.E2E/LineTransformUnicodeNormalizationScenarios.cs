using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformUnicodeNormalizationScenarios
{
    // 手定義の公開契約ケース。Windows Nativeで採取したgoldenではない。
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var work = Path.GetFullPath(Path.Combine(fixtures, "line-transform-unicode-normalization"));
        RequireExternal(work);
        if (Directory.Exists(work) || File.Exists(work)) throw new IOException("Fresh normalization E2E output required.");
        Directory.CreateDirectory(work);
        var originals = new Dictionary<string, byte[]>();
        using var reportFile = File.Create(Path.Combine(work, "observations.json"));
        using var report = new Utf8JsonWriter(reportFile, new() { Indented = true });
        report.WriteStartObject(); report.WriteString("qualification", "hand-authored normalization contract; nativeGolden=false; Windows NormalizeString / other OS managed Unicode data");
        report.WriteStartArray("cases");
        var cases = new List<(string Id, string Expression, string Source, string Expected)>
        {
            ("default", "NormalizeUnicode(Line)", "e\u0301\ufb03\uff21", "\u00e9\ufb03\uff21"),
            ("empty", "NormalizeUnicode(\"\")", "raw", ""),
            ("none", "NormalizeUnicode(none)", "raw", "none"),
            ("bool", "NormalizeUnicode(true)", "raw", "true"),
            ("integer", "NormalizeUnicode(-42)", "raw", "-42"),
            ("double", "NormalizeUnicode(1.5)", "raw", "1.500000"),
            ("timestamp", "NormalizeUnicode(d\"2000-01-01T00:00:00Z\")==Substr(d\"2000-01-01T00:00:00Z\",0)", "raw", "true"),
            ("empty-array", "NormalizeUnicode(Array())", "raw", "[]"),
            ("array", "NormalizeUnicode(Array(\"e\u0301\",none,false,12))", "raw", "[\u00e9, none, false, 12]"),
            ("nested-array", "NormalizeUnicode(Array(Array(\"e\u0301\",true,none),none))", "raw", "[[\u00e9, true, none], none]"),
            ("nested-is-string", "StrLen(At(NormalizeUnicode(Array(Array(\"e\u0301\",true))),0))", "raw", "9"),
            ("composition", "NormalizeUnicode(NormalizeUnicode(Line,2),1)", "\u00e9", "\u00e9")
        };
        foreach (var (form, expected) in new[]
        {
            ("\"NFC\"", "\u00e9\ufb03\uff21"), ("\"c\"", "\u00e9\ufb03\uff21"), ("1", "\u00e9\ufb03\uff21"),
            ("\"nFd\"", "e\u0301\ufb03\uff21"), ("\"d\"", "e\u0301\ufb03\uff21"), ("2", "e\u0301\ufb03\uff21"),
            ("\"nfkc\"", "\u00e9ffiA"), ("\"kC\"", "\u00e9ffiA"), ("5", "\u00e9ffiA"),
            ("\"NFKD\"", "e\u0301ffiA"), ("\"kd\"", "e\u0301ffiA"), ("6", "e\u0301ffiA")
        }) cases.Add(("form-" + cases.Count, "NormalizeUnicode(Line," + form + ")", "e\u0301\ufb03\uff21", expected));
        foreach (var form in new[] { "none", "true", "false", "2.0", "Array(2)", "Array()", "d\"2000-01-01T00:00:00Z\"" })
            cases.Add(("default-type-" + cases.Count, "NormalizeUnicode(Line," + form + ")", "e\u0301", "\u00e9"));
        foreach (var form in new[] { "\" NFC\"", "\"NFD \"", "\"\"", "\"NFKX\"", "\"\u212aC\"", "0", "3", "-1", "ToInt(\"9223372036854775807\")" })
            cases.Add(("invalid-lazy-" + cases.Count, "NormalizeUnicode(Abs(ToInt(Line))," + form + ")", "-9223372036854775808", "none"));
        foreach (var item in cases)
            foreach (var noopt in new[] { false, true })
            {
                var id = item.Id + (noopt ? "-noopt" : "-optimized");
                var left = await Source(id + "-left", item.Source); var right = await Source(id + "-right", item.Expected);
                var expression = "@cs " + (noopt ? "@noopt " : "") + item.Expression;
                var result = await run("normalization-" + id, 0, true, ["--compare", left, right, "--line-transform", "left", expression]);
                var passed = false;
                try
                {
                    using var json = JsonDocument.Parse(result.Stdout);
                    var rows = json.RootElement.GetProperty("rows").EnumerateArray().ToArray();
                    passed = result.ExitCode == 0 && !json.RootElement.GetProperty("different").GetBoolean();
                    foreach (var (side, original) in new[] { ("left", item.Source), ("right", item.Expected) })
                    {
                        var actual = rows.Where(row => row.TryGetProperty(side + "Line", out _)).ToArray();
                        check(result.Name + "-raw-" + side, actual.Length == 1 && actual[0].GetProperty(side + "Line").GetInt32() == 1
                            && actual[0].GetProperty(side).GetString() == original, "Physical source and line number preserved.");
                    }
                }
                catch (JsonException error) { check(result.Name + "-json", false, error.Message); }
                check(result.Name + "-key", passed, expression + " => " + item.Expected);
                report.WriteStartObject(); report.WriteString("id", id); report.WriteString("expression", expression);
                report.WriteString("expectedKey", item.Expected); report.WriteBoolean("passed", passed); report.WriteNumber("exitCode", result.ExitCode); report.WriteEndObject();
            }
        var tableLeft = await Source("table-left", "\"e\u0301,\ufb03\",KEEP");
        var tableRight = await Source("table-right", "\"\u00e9,ffi\",KEEP");
        var table = await run("normalization-table-column", 0, true,
            ["--table", tableLeft, tableRight, "--line-transform", "all", "@cs NormalizeUnicode(Column1,5)"]);
        using (var tableJson = JsonDocument.Parse(table.Stdout))
            check(table.Name + "-key", table.ExitCode == 0 && !tableJson.RootElement.GetProperty("different").GetBoolean(),
                "Quoted column text is converted through the table provider.");
        var reportLeft = await Source("report-left", "e\u0301"); var reportRight = await Source("report-right", "\u00e9");
        var reportProject = Path.Combine(work, "report-project.json");
        await File.WriteAllTextAsync(reportProject, JsonSerializer.Serialize(new
        {
            mode = "Text", leftPath = reportLeft, rightPath = reportRight,
            lineTransformRules = new[] { new { expression = "@cs NormalizeUnicode(Line)", panes = 7 } }
        }));
        var reportHtml = Path.Combine(work, "normalization-report.html");
        var reportResult = await run("normalization-report-project", 0, true, ["--report-project", reportProject, reportHtml]);
        var decodedReport = File.Exists(reportHtml) ? System.Net.WebUtility.HtmlDecode(await File.ReadAllTextAsync(reportHtml)) : "";
        check(reportResult.Name + "-classification", reportResult.ExitCode == 0 && decodedReport.Contains("data-ignored=\"true\"", StringComparison.Ordinal)
            && decodedReport.Contains("e\u0301", StringComparison.Ordinal) && decodedReport.Contains("\u00e9", StringComparison.Ordinal),
            "HTML uses normalized comparison keys while preserving both original source strings.");
        var reportPackage = Path.Combine(work, "normalization-report-package.zip");
        var packageResult = await run("normalization-package-report", 0, true,
            ["--package-project", reportProject, reportPackage, "--report"]);
        string packagedReport = "";
        if (File.Exists(reportPackage))
        {
            using var archive = ZipFile.OpenRead(reportPackage);
            var reportEntry = archive.GetEntry("report.files/1.html");
            if (reportEntry is not null)
            {
                using var reader = new StreamReader(reportEntry.Open());
                packagedReport = System.Net.WebUtility.HtmlDecode(await reader.ReadToEndAsync());
            }
        }
        check(packageResult.Name + "-classification", packageResult.ExitCode == 0
            && packagedReport.Contains("data-ignored=\"true\"", StringComparison.Ordinal)
            && packagedReport.Contains("e\u0301", StringComparison.Ordinal) && packagedReport.Contains("\u00e9", StringComparison.Ordinal),
            "Packaged HTML uses normalized comparison keys and preserves both original source strings.");
        var rejectLeft = await Source("reject-left", "e\u0301"); var rejectRight = await Source("reject-right", "\u00e9");
        foreach (var expression in new[] { "NormalizeUnicode()", "NormalizeUnicode(Line,1,1)" })
        {
            var result = await run("normalization-arity-" + (expression.Contains(',') ? "three" : "zero"), 2, false,
                ["--compare", rejectLeft, rejectRight, "--line-transform", "left", expression]);
            check(result.Name + "-atomic", result.ExitCode == 2 && string.IsNullOrWhiteSpace(result.Stdout)
                && !string.IsNullOrWhiteSpace(result.Stderr), "Invalid arity produces no partial comparison JSON.");
        }
        report.WriteEndArray(); report.WriteEndObject(); report.Flush();
        foreach (var source in originals)
            check("normalization-source-" + Path.GetFileName(source.Key), SHA256.HashData(await File.ReadAllBytesAsync(source.Key))
                .SequenceEqual(SHA256.HashData(source.Value)), "Original bytes preserved.");

        async Task<string> Source(string name, string text)
        {
            var path = Path.Combine(work, name + ".txt"); var bytes = new UTF8Encoding(false, true).GetBytes(text + "\r\n");
            await File.WriteAllBytesAsync(path, bytes); originals.Add(path, bytes); return path;
        }
    }

    private static void RequireExternal(string path)
    {
        var repository = FixtureRepository.FindRoot()?.FullName ?? throw new IOException("Repository missing.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in new[] { repository, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), Environment.GetEnvironmentVariable("CODEX_HOME") })
            if (!string.IsNullOrEmpty(root) && (path.Equals(Path.GetFullPath(root), comparison)
                || path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, comparison)))
                throw new IOException("Normalization E2E requires external output.");
        for (DirectoryInfo? parent = new(path); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked normalization output refused.");
    }
}
