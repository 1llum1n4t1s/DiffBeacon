using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformCasingScenarios
{
    // 手定義の公開契約ケース。Native実行・locale依存Unicodeのgoldenではない。
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var work = Path.GetFullPath(Path.Combine(fixtures, "line-transform-casing"));
        RequireExternal(work);
        if (Directory.Exists(work) || File.Exists(work)) throw new IOException("Fresh casing E2E output required.");
        Directory.CreateDirectory(work);
        var originals = new Dictionary<string, byte[]>();
        using var reportFile = File.Create(Path.Combine(work, "observations.json"));
        using var report = new Utf8JsonWriter(reportFile, new() { Indented = true });
        report.WriteStartObject(); report.WriteString("qualification", "hand-authored public contract; nativeExecuted=false; ASCII excludes I/i");
        report.WriteStartArray("cases");
        var cases = new (string Id, string Expression, string Source, string Expected)[]
        {
            ("lower-line", "ToLower(Line)", "AbC XYZ 09!?", "abc xyz 09!?"),
            ("upper-line", "ToUpper(Line)", "aBc xyz 09!?", "ABC XYZ 09!?"),
            ("empty", "ToLower(\"\")", "raw", ""),
            ("none", "ToUpper(none)", "raw", "none"),
            ("bool", "ToUpper(true)", "raw", "TRUE"),
            ("integer", "ToLower(-42)", "raw", "-42"),
            ("double", "ToUpper(1.5)", "raw", "1.500000"),
            ("empty-array", "ToLower(Array())", "raw", "[]"),
            ("array", "ToUpper(Array(\"Ab\",none,false,12))", "raw", "[AB, none, FALSE, 12]"),
            ("nested-array", "ToUpper(Array(Array(\"Ab\",true,none),none))", "raw", "[[AB, TRUE, NONE], none]"),
            ("nested-is-string", "StrLen(At(ToUpper(Array(Array(\"Ab\",true))),0))", "raw", "10"),
            ("composition", "ToUpper(ToLower(Line))", "aBc xyz", "ABC XYZ")
        };
        foreach (var item in cases)
            foreach (var noopt in new[] { false, true })
            {
                var id = item.Id + (noopt ? "-noopt" : "-optimized");
                var left = await Source(id + "-left", item.Source); var right = await Source(id + "-right", item.Expected);
                var expression = "@cs " + (noopt ? "@noopt " : "") + item.Expression;
                var result = await run("casing-" + id, 0, true, ["--compare", left, right, "--line-transform", "left", expression]);
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
        var tableLeft = await Source("table-left", "\"AbC,XYZ\",KEEP");
        var tableRight = await Source("table-right", "\"abc,xyz\",KEEP");
        var table = await run("casing-table-column", 0, true,
            ["--table", tableLeft, tableRight, "--line-transform", "all", "@cs ToLower(Column1)"]);
        using (var tableJson = JsonDocument.Parse(table.Stdout))
            check(table.Name + "-key", table.ExitCode == 0 && !tableJson.RootElement.GetProperty("different").GetBoolean(),
                "Quoted column text is converted through the table provider.");
        var rejectLeft = await Source("reject-left", "AbC"); var rejectRight = await Source("reject-right", "abc");
        foreach (var expression in new[] { "ToLower()", "ToUpper()", "ToLower(Line,Line)", "ToUpper(Line,Line)" })
        {
            var result = await run("casing-arity-" + (expression.StartsWith("ToLower", StringComparison.Ordinal) ? "lower" : "upper")
                + (expression.Contains(',') ? "-two" : "-zero"), 2, false,
                ["--compare", rejectLeft, rejectRight, "--line-transform", "left", expression]);
            check(result.Name + "-atomic", result.ExitCode == 2 && string.IsNullOrWhiteSpace(result.Stdout)
                && !string.IsNullOrWhiteSpace(result.Stderr), "Invalid arity produces no partial comparison JSON.");
        }
        report.WriteEndArray(); report.WriteEndObject(); report.Flush();
        foreach (var source in originals)
            check("casing-source-" + Path.GetFileName(source.Key), SHA256.HashData(await File.ReadAllBytesAsync(source.Key))
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
                throw new IOException("Casing E2E requires external output.");
        for (DirectoryInfo? parent = new(path); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked casing output refused.");
    }
}
