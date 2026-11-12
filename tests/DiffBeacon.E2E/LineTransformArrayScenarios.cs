using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// 草案: 親が製品へ配列実装と固定fixtureを統合した後に既存E2Eへ接続する。
internal static class LineTransformArrayScenarios
{
    private sealed record Inputs(string Left, string Right, byte[] LeftBytes, byte[] RightBytes);
    private sealed record Rule(string Expression, int Panes);

    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Array fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformArrays");
        var work = Path.GetFullPath(Path.Combine(fixtures, "line-transform-arrays"));
        RequireExternal(work, repository.FullName);
        if (File.Exists(work) || Directory.Exists(work)) throw new IOException("Array E2E requires fresh task output.");
        Directory.CreateDirectory(work);
        var sources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "manifest.json")));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            var path = Path.GetFullPath(Path.Combine(reference, file.Name));
            Within(path, reference);
            var bytes = await File.ReadAllBytesAsync(path);
            check("arrays-fixed-source-" + file.Name, bytes.LongLength == file.Value.GetProperty("size").GetInt64()
                && Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.Value.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Fixed input SHA and size.");
        }
        using var cases = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "cases.json")));
        var golden = File.ReadLines(Path.Combine(reference, "golden.ndjson")).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => { using var parsed = JsonDocument.Parse(line); return parsed.RootElement.Clone(); })
            .ToDictionary(value => value.GetProperty("id").GetString()!, StringComparer.Ordinal);
        check("arrays-fixed-expectation-input-complete", cases.RootElement.GetArrayLength() == 90 && golden.Count == 90, "Expected input corpus size; this is not a claim that CLI executes all 90 oracle contexts.");
        var callerCases = new HashSet<string>(StringComparer.Ordinal)
        {
            "arrays-value-mixed", "arrays-value-nested", "arrays-value-utf8", "arrays-at-neg1",
            "arrays-at-9223372036854775807", "arrays-at-neg9223372036854775808", "arrays-at-nonint",
            "arrays-anyof-mixed", "arrays-allof-mixed", "arrays-allequal-strict", "arrays-andeach-mismatch",
            "arrays-oreach-lift-left", "arrays-noteach-mixed", "arrays-ifeach-lift", "arrays-ifeach-eager-true",
            "arrays-choose-lazy", "arrays-choose-max", "arrays-choose-min", "arrays-chooseeach-mixed",
            "arrays-compute-lift-left", "arrays-compute-nested", "arrays-compute-eq-lift", "arrays-len-nested", "arrays-trim-lift",
            "arrays-value-eol-values"
        };
        foreach (var test in cases.RootElement.EnumerateArray().Where(test => callerCases.Contains(test.GetProperty("id").GetString()!)))
        {
            var id = test.GetProperty("id").GetString()!;
            var expected = golden[id];
            var left = expected.GetProperty("lines").EnumerateArray().SelectMany(line => Convert.FromHexString(line.GetProperty("originalHex").GetString()!)).ToArray();
            var right = expected.GetProperty("lines").EnumerateArray().SelectMany(line => Convert.FromHexString(line.GetProperty("keyHex").GetString()!)).ToArray();
            var input = await Create(id, left, right);
            var options = new List<string>();
            // ここはCLIの片側変換を検査する独立ケース。固定provider maskを再現したという主張はしない。
            // 単一グループ・PlainText・pane=Leftで、期待文字列が通常Text入力として有効な原典例だけ採用する。
            if (test.GetProperty("table").GetBoolean() || test.GetProperty("pane").GetInt32() != 0 || test.GetProperty("groups").GetArrayLength() != 1)
                throw new IOException("Selected CLI array case requires an unsupported provider context: " + id);
            foreach (var group in test.GetProperty("groups").EnumerateArray())
            {
                if (group.GetProperty("mask").GetInt32() != 7) throw new IOException("Selected CLI case has a different fixed mask: " + id);
                foreach (var expression in group.GetProperty("expressions").EnumerateArray())
                    options.AddRange(["--line-transform", "left", expression.GetString()!]);
            }
            var refused = expected.GetProperty("status").GetString() == "error" || id == "arrays-value-eol-values";
            if (refused) await Reject(id, input, options.ToArray());
            else await Compare(id, input, false, false, options.ToArray());
        }
        check("arrays-selected-cli-case-count", callerCases.Count == 25 && callerCases.All(golden.ContainsKey), "25 caller regressions selected; full public Plan90 belongs to separate independent reader.");

        var simple = await Create("source-grounded-opt", Utf8("old\r\n"), Utf8("new\r\n"));
        // 固定captureは@noopt。default Optimizeのbool '-' fold順だけは旧bodyの分岐に基づく追加回帰。
        await Compare("defaultopt-bool-fold", simple, false, false, ["--line-transform", "all", "If((true - false) = 1, \"same\", Line)"]);
        await Compare("noopt-bool-compute", simple, false, false, ["--line-transform", "all", "@noopt If((true - false) = -1, \"same\", Line)"]);
        await Reject("defaultopt-untaken-divide", simple, ["--line-transform", "all", "If(true, \"ok\", 1 / 0)"]);
        foreach (var (name, expression) in new[] { ("at-arity", "At(Array(1))"), ("if-each-arity", "IfEach(true, 1)"),
            ("choose-arity", "Choose(0)"), ("depth", new string('-', 140) + "1") })
            await Reject(name, simple, ["--line-transform", "all", expression]);

        var table = await Create("raw-quoted-table", Utf8("\"A,\"\"q\"\"\",old\r\n"), Utf8("\"A,\"\"q\"\"\",new\r\n"));
        await Compare("raw-quoted-column-through-array", table, true, false,
            ["--line-transform", "all", "At(Array(Column1, Column2), 0)"]);
        var pipeline = await Create("ordered-pipeline", Utf8("source\r\n"), Utf8("[[source, x], y]\r\n"));
        await Compare("same-mask-ordered-pipeline", pipeline, false, false,
            ["--line-transform", "left", "Array(Line, \"x\")", "--line-transform", "left", "Array(Line, \"y\")"]);
        var middle = Path.Combine(work, "physical-middle.txt");
        await WriteSource(middle, Utf8("middle\r\n"));
        foreach (var (pair, scope, expression) in new[] { ("left-middle", "base", "Array(\"old\")"),
            ("middle-right", "right", "Array(\"middle\")"), ("left-right", "left", "Array(\"new\")") })
        {
            // Array比較対象も配列codecにするため、他方へ1件を明示指定する。
            var counterpart = pair == "left-middle" ? "left" : pair == "middle-right" ? "base" : "right";
            var result = await run("arrays-physical-mask-" + pair, 0, true,
                ["--compare", simple.Left, simple.Right, "--middle", middle, "--independent-text", "--pair", pair,
                    "--line-transform", scope, expression, "--line-transform", counterpart, "Array(Line)"]);
            using var parsed = Parse(result);
            if (parsed is not null) check(result.Name + "-physical-classification", !parsed.RootElement.GetProperty("different").GetBoolean()
                && parsed.RootElement.GetProperty("pair").GetString() == (pair == "left-middle" ? "LeftMiddle" : pair == "middle-right" ? "MiddleRight" : "LeftRight"), "Actual physical target identity and canonical pair.");
        }
        await Projects(table);
        await SlotBoundary();
        foreach (var source in sources)
        {
            var current = File.Exists(source.Key) ? await File.ReadAllBytesAsync(source.Key) : [];
            check("arrays-source-sha-" + Path.GetFileName(source.Key), SHA256.HashData(source.Value).SequenceEqual(SHA256.HashData(current)), source.Key);
        }

        async Task<Inputs> Create(string name, byte[] left, byte[] right)
        {
            var lp = Path.Combine(work, name + "-left.txt"); var rp = Path.Combine(work, name + "-right.txt");
            await WriteSource(lp, left); await WriteSource(rp, right); return new(lp, rp, left, right);
        }
        async Task WriteSource(string path, byte[] bytes)
        {
            await File.WriteAllBytesAsync(path, bytes); sources.Add(path, bytes);
        }
        async Task Compare(string name, Inputs input, bool tableMode, bool different, string[] options)
        {
            var result = await run("arrays-" + name, different ? 1 : 0, true,
                new[] { tableMode ? "--table" : "--compare", input.Left, input.Right }.Concat(options).ToArray());
            using var parsed = Parse(result);
            if (parsed is null) return;
            var root = parsed.RootElement;
            check(result.Name + "-classification", result.ExitCode == (different ? 1 : 0) && root.GetProperty("different").GetBoolean() == different, "Expected different=" + different);
            if (!tableMode)
            {
                var rows = root.GetProperty("rows").EnumerateArray().ToArray();
                foreach (var (side, original) in new[] { ("left", input.LeftBytes), ("right", input.RightBytes) })
                {
                    var actual = rows.Where(row => row.TryGetProperty(side + "Line", out _)).ToArray();
                    var originalRows = Lines(Encoding.UTF8.GetString(original));
                    check(result.Name + "-original-" + side, actual.Select(row => row.GetProperty(side + "Line").GetInt32()).SequenceEqual(Enumerable.Range(1, originalRows.Length))
                        && actual.Select(row => row.GetProperty(side).GetString()).SequenceEqual(originalRows), "Original line numbers and contents remain unchanged.");
                }
            }
        }
        async Task Reject(string name, Inputs input, string[] options)
        {
            var result = await run("arrays-reject-" + name, 2, false, new[] { "--compare", input.Left, input.Right }.Concat(options).ToArray());
            check(result.Name + "-atomic", result.ExitCode == 2 && !string.IsNullOrWhiteSpace(result.Stderr) && string.IsNullOrWhiteSpace(result.Stdout), "Product rejection; no partial comparison JSON.");
        }
        JsonDocument? Parse(CommandResult result)
        {
            try { return JsonDocument.Parse(result.Stdout); }
            catch (JsonException error) { check(result.Name + "-json", false, "exit=" + result.ExitCode + "; " + error.Message); return null; }
        }
        async Task Projects(Inputs input)
        {
            var rules = new[] { new Rule("At(Array(Column1, Column2), 0)", 7) };
            object Entry(Rule[] expressions) => new { mode = "Table", leftPath = input.Left, rightPath = input.Right,
                tableDelimiter = ',', lineTransformRules = expressions };
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var project = Path.Combine(work, "project.json"); var workspace = Path.Combine(work, "workspace.json");
            await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(Entry(rules), jsonOptions));
            await WriteSource(workspace, JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, entries = new[] { Entry(rules), Entry([new("Array(Line)", 7)]) }, activeEntryIndex = 0 }, jsonOptions));
            foreach (var source in new[] { project, workspace })
            {
                var copy = source + ".copy.json"; var again = source + ".again.json";
                await run("arrays-copy-" + Path.GetFileName(source), 0, true, ["--project-copy", source, copy]);
                await run("arrays-recopy-" + Path.GetFileName(source), 0, true, ["--project-copy", copy, again]);
                check("arrays-json-roundtrip-" + Path.GetFileName(source), File.Exists(copy) && File.Exists(again)
                    && File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(again)), "Stable array expression JSON.");
                if (File.Exists(copy))
                {
                    using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(copy));
                    var entry = saved.RootElement.TryGetProperty("entries", out var entries) ? entries[0] : saved.RootElement;
                    check("arrays-json-expression-" + Path.GetFileName(source), entry.GetProperty("lineTransformRules")[0].GetProperty("expression").GetString() == rules[0].Expression
                        && entry.GetProperty("lineTransformRules")[0].GetProperty("panes").GetInt32() == 7, "Expression and mask preserved.");
                    if (source == workspace) check("arrays-json-second-entry", entries.GetArrayLength() == 2 && entries[1].GetProperty("lineTransformRules")[0].GetProperty("expression").GetString() == "Array(Line)"
                        && saved.RootElement.GetProperty("activeEntryIndex").GetInt32() == 0, "Every workspace entry and active position.");
                }
            }
            var html = Path.Combine(work, "report.html");
            await run("arrays-project-report", 0, true, ["--report-project", project, html]);
            CheckHtml(html, false);
            var overridden = Path.Combine(work, "override.html");
            await run("arrays-project-report-override", 0, true, ["--report-project", project, overridden, "--line-transform", "all", "Array(Line)"]);
            CheckHtml(overridden, true);
            var package = Path.Combine(work, "package.zip");
            await run("arrays-package", 0, true, ["--package-project", project, package, "--report"]);
            if (File.Exists(package))
            {
                using var zip = ZipFile.OpenRead(package);
                var entries = new List<byte[]>();
                foreach (var entry in zip.Entries.Where(entry => entry.Name.Length > 0))
                { using var stream = entry.Open(); using var buffer = new MemoryStream(); stream.CopyTo(buffer); entries.Add(buffer.ToArray()); }
                check("arrays-package-original-assets", entries.Any(bytes => bytes.SequenceEqual(input.LeftBytes)) && entries.Any(bytes => bytes.SequenceEqual(input.RightBytes)), "Full original asset bytes, independently read ZIP.");
                var expanded = Path.Combine(work, "expanded");
                await run("arrays-package-expand", 0, true, ["--archive-extract", package, expanded]);
                var reopened = Path.Combine(work, "reopened.json");
                await run("arrays-package-reopen", 0, true, ["--project-copy", Path.Combine(expanded, "project.json"), reopened]);
                var reloaded = Path.Combine(work, "reloaded.html");
                await run("arrays-package-reloaded-report", 0, true, ["--report-project", reopened, reloaded]); CheckHtml(reloaded, false);
            }
            void CheckHtml(string path, bool different)
            {
                var html = File.Exists(path) ? File.ReadAllText(path) : "";
                check("arrays-html-" + Path.GetFileName(path), html.Contains("data-different=\"" + (different ? "true" : "false") + "\"", StringComparison.Ordinal)
                    && html.Contains("old", StringComparison.Ordinal) && html.Contains("new", StringComparison.Ordinal)
                    && (different || html.Contains("data-ignored=\"true\"", StringComparison.Ordinal))
                    && Regex.Matches(html, "<td\\b[^>]*><pre>").Count == 4, "Original quoted cells, classification and all cells retained.");
            }
        }
        async Task SlotBoundary()
        {
            var expression = "@noopt Array(" + string.Join(",", Enumerable.Repeat("Line", 128)) + ")";
            var encoded = "[" + string.Join(", ", Enumerable.Repeat("x", 128)) + "]\r\n";
            var accepted = await Create("slots-8192", Utf8(string.Concat(Enumerable.Repeat("x\r\n", 8192))), Utf8(string.Concat(Enumerable.Repeat(encoded, 8192))));
            await Compare("shared-slots-exact-1048576", accepted, false, false, ["--line-transform", "left", expression, "--max-work", "0"]);
            var refused = await Create("slots-8193", Utf8(string.Concat(Enumerable.Repeat("x\r\n", 8193))), Utf8(string.Concat(Enumerable.Repeat(encoded, 8193))));
            await Reject("shared-slots-1048704", refused, ["--line-transform", "left", expression, "--max-work", "0"]);
            var project = Path.Combine(work, "slot-limit-project.json");
            await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Text", leftPath = refused.Left, rightPath = refused.Right,
                lineTransformRules = new[] { new { expression, panes = 1 } } }));
            var html = Path.Combine(work, "slot-limit-existing.html"); var sentinel = Utf8("existing report\r\n");
            await File.WriteAllBytesAsync(html, sentinel);
            var rejected = await run("arrays-slot-limit-project", 2, false, ["--report-project", project, html]);
            check("arrays-slot-limit-existing-output", rejected.ExitCode == 2 && string.IsNullOrWhiteSpace(rejected.Stdout)
                && !string.IsNullOrWhiteSpace(rejected.Stderr) && File.ReadAllBytes(html).SequenceEqual(sentinel), "Shared slot rejection preserves existing report bytes.");
        }
    }
    private static byte[] Utf8(string value) => new UTF8Encoding(false).GetBytes(value);
    private static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        var rows = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return text[^1] is '\r' or '\n' ? rows[..^1] : rows;
    }
    private static void Within(string path, string root)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Array fixture/output path escapes root.");
        NoLinks(path);
    }
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Array output cannot traverse links.");
    }
    private static void RequireExternal(string path, string repository)
    {
        var full = Path.GetFullPath(path); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { repository, Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("Array E2E requires external task output.");
        }
        if (full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase))) throw new IOException("Array E2E cannot use an artifacts directory.");
        NoLinks(path);
    }
}
