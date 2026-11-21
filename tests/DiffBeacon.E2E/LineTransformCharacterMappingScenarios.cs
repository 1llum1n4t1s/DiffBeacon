using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformCharacterMappingScenarios
{
    // 想定失敗: arity不正、互換文字の過剰変換、配列の再帰適用、原文破壊、HTMLの未変換。
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var work = Path.GetFullPath(Path.Combine(fixtures, "line-transform-character-mapping"));
        RequireExternal(work);
        if (Directory.Exists(work) || File.Exists(work)) throw new IOException("Fresh character mapping output required.");
        Directory.CreateDirectory(work);
        var originals = new Dictionary<string, byte[]>();
        var cases = new List<(string Id, string Expression, string Source, string Expected)>();
        var functions = new[]
        {
            (Name: "ToHalfWidth", Source: "ＡＢＣ１２ ガ", Expected: "ABC12 ｶﾞ"),
            (Name: "ToFullWidth", Source: "ABC12 ｶﾞ", Expected: "ＡＢＣ１２　ガ"),
            (Name: "ToHiragana", Source: "アイウガ", Expected: "あいうが"),
            (Name: "ToKatakana", Source: "あいうが", Expected: "アイウガ"),
            (Name: "ToSimplifiedChinese", Source: "漢語國", Expected: "汉语国"),
            (Name: "ToTraditionalChinese", Source: "汉语国", Expected: "漢語國")
        };
        foreach (var function in functions)
        {
            cases.Add((function.Name, function.Name + "(Line)", function.Source, function.Expected));
            cases.Add((function.Name + "-none", function.Name + "(none)", "raw", "none"));
            cases.Add((function.Name + "-empty", function.Name + "(\"\")", "raw", ""));
            cases.Add((function.Name + "-empty-array", function.Name + "(Array())", "raw", "[]"));
            cases.Add((function.Name + "-lazy", "If(false," + function.Name + "(Line),\"kept\")", "raw", "kept"));
            cases.Add((function.Name + "-unrelated", function.Name + "(Line)", "①ﬃé", "①ﬃé"));
        }
        cases.AddRange([
            ("bool", "ToHalfWidth(true)", "raw", "true"),
            ("integer", "ToHalfWidth(-42)", "raw", "-42"),
            ("double", "ToHalfWidth(1.5)", "raw", "1.500000"),
            ("array", "ToHalfWidth(Array(\"Ａ\",none,true,12))", "raw", "[A, none, true, 12]"),
            ("nested-string", "StrLen(At(ToHalfWidth(Array(Array(\"Ａ\"))),0))", "raw", "3"),
            ("composition", "ToHiragana(ToFullWidth(Line))", "ｶﾞ", "が")
        ]);
        var observations = new List<object>();
        foreach (var item in cases)
            foreach (var noopt in new[] { false, true })
            {
                var id = item.Id + (noopt ? "-noopt" : "-optimized");
                var left = await Source(id + "-left", item.Source); var right = await Source(id + "-right", item.Expected);
                var expression = "@cs " + (noopt ? "@noopt " : "") + item.Expression;
                var result = await run("character-mapping-" + id, 0, true,
                    ["--compare", left, right, "--line-transform", "left", expression]);
                var passed = false;
                try
                {
                    using var json = JsonDocument.Parse(result.Stdout);
                    passed = !json.RootElement.GetProperty("different").GetBoolean();
                    var rows = json.RootElement.GetProperty("rows").EnumerateArray().ToArray();
                    foreach (var (side, original) in new[] { ("left", item.Source), ("right", item.Expected) })
                    {
                        var actual = rows.Where(row => row.TryGetProperty(side + "Line", out _)).ToArray();
                        check(result.Name + "-raw-" + side, actual.Length == 1 && actual[0].GetProperty(side + "Line").GetInt32() == 1
                            && actual[0].GetProperty(side).GetString() == original, "Original source and physical line preserved.");
                    }
                }
                catch (JsonException error) { check(result.Name + "-json", false, error.Message); }
                check(result.Name + "-key", result.ExitCode == 0 && passed, expression + " => " + item.Expected);
                observations.Add(new { id, expression, expected = item.Expected, passed, result.ExitCode });
            }
        var tableLeft = await Source("table-left", "\"Ａ,ガ\",KEEP");
        var tableRight = await Source("table-right", "\"A,ｶﾞ\",KEEP");
        var table = await run("character-mapping-table", 0, true,
            ["--table", tableLeft, tableRight, "--line-transform", "all", "@cs ToHalfWidth(Column1)"]);
        using (var json = JsonDocument.Parse(table.Stdout))
            check(table.Name + "-key", table.ExitCode == 0 && !json.RootElement.GetProperty("different").GetBoolean(), "Table column transform.");
        foreach (var function in functions)
            foreach (var arguments in new[] { "", "Line,Line" })
            {
                var result = await run("character-mapping-arity-" + function.Name + (arguments.Length == 0 ? "-zero" : "-two"), 2, false,
                    ["--compare", tableLeft, tableRight, "--line-transform", "left", function.Name + "(" + arguments + ")"]);
                check(result.Name + "-atomic", result.ExitCode == 2 && string.IsNullOrWhiteSpace(result.Stdout)
                    && !string.IsNullOrWhiteSpace(result.Stderr), "Arity rejection has no partial output.");
            }
        var reportLeft = await Source("report-left", "ガ"); var reportRight = await Source("report-right", "ｶﾞ");
        var project = Path.Combine(work, "project.json");
        await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new
        {
            mode = "Text", leftPath = reportLeft, rightPath = reportRight,
            lineTransformRules = new[] { new { expression = "@cs ToHalfWidth(Line)", panes = 7 } }
        }));
        var html = Path.Combine(work, "report.html");
        var report = await run("character-mapping-report", 0, true, ["--report-project", project, html]);
        CheckHtml(report.Name, File.Exists(html) ? await File.ReadAllTextAsync(html) : "");
        var package = Path.Combine(work, "report.zip");
        var packaged = await run("character-mapping-package-report", 0, true, ["--package-project", project, package, "--report"]);
        string packagedHtml = "";
        if (File.Exists(package))
        {
            using var zip = ZipFile.OpenRead(package);
            if (zip.GetEntry("report.files/1.html") is { } entry)
            {
                using var reader = new StreamReader(entry.Open());
                packagedHtml = await reader.ReadToEndAsync();
            }
        }
        CheckHtml(packaged.Name, packagedHtml);
        await File.WriteAllTextAsync(Path.Combine(work, "observations.json"), JsonSerializer.Serialize(new
        {
            qualification = "hand-authored contract; not native WinMerge golden; OS mapping data may differ",
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, cases = observations
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var source in originals)
            check("character-mapping-source-" + Path.GetFileName(source.Key), SHA256.HashData(await File.ReadAllBytesAsync(source.Key))
                .SequenceEqual(SHA256.HashData(source.Value)), "Original bytes preserved.");

        void CheckHtml(string name, string text)
        {
            var decoded = System.Net.WebUtility.HtmlDecode(text);
            check(name + "-classification", decoded.Contains("data-ignored=\"true\"", StringComparison.Ordinal)
                && decoded.Contains("ガ", StringComparison.Ordinal) && decoded.Contains("ｶﾞ", StringComparison.Ordinal), "HTML transformed classification and original text.");
        }
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
                throw new IOException("Character mapping E2E requires external output.");
        for (DirectoryInfo? parent = new(path); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked character mapping output refused.");
    }
}
