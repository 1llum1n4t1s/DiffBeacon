using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class LineTransformRangeScenarios
{
    private sealed record Inputs(string Left, string Right, byte[] LeftBytes, byte[] RightBytes);
    private sealed record Rule(string Expression, int Panes);

    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("InRange fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformRanges");
        var work = Path.GetFullPath(Path.Combine(fixtures, "line-transform-ranges"));
        RequireExternal(work, repository.FullName);
        if (File.Exists(work) || Directory.Exists(work)) throw new IOException("InRange E2E requires fresh task output.");
        Directory.CreateDirectory(work);
        var sources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await WriteSource(Path.Combine(work, "failure-plan.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            failures = new[] { "serialized value/key differs", "bridge overclaims parser equivalence", "EOF empty string[1] retargeted to physical file",
                "constructor/property accepted", "shared work budget resets across 256 rules", "257 rules accepted", "input/workspace/existing HTML/JSON changed",
                "table/report/package loses original cells/rules/assets" },
            scope = "59 physical derived CLI keys, 3 derived constructor errors and 1 separate public arity4 probe. Strict 72+1 context/value/error metadata is independently read from real App diagnostics; no fabricated diagnostics.",
            cancellation = "CLI has no cancellation injection; this family does not verify cancellation."
        }, jsonOptions));
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "fixed-manifest.json")));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = file.GetProperty("path").GetString()!;
            var path = Path.GetFullPath(Path.Combine(reference, name)); Within(path, reference);
            var bytes = await File.ReadAllBytesAsync(path);
            check("inrange-fixed-source-" + name, bytes.LongLength == file.GetProperty("size").GetInt64()
                && Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Fixed source SHA and size.");
        }
        using var plan = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "public-case-plan.json")));
        var golden = File.ReadLines(Path.Combine(reference, "golden.ndjson"))
            .Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => { using var parsed = JsonDocument.Parse(line); return parsed.RootElement.Clone(); })
            .ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
        check("inrange-fixed-72-inputs", plan.RootElement.GetProperty("cases").GetArrayLength() == 72 && golden.Count == 72
            && plan.RootElement.GetProperty("sourceCommit").GetString() == "6780924ba35f7f55af0f6f14dd302561175830a6", "Fixed corpus completeness and commit; not a physical CLI context claim.");
        var selection = new List<object>(); int derived = 0, unsupported = 0, constructor = 0, bridges = 0;
        var simple = await Create("physical", Utf8("physical probe\r\n"), Utf8("physical peer\r\n"));
        foreach (var test in plan.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = test.GetProperty("id").GetString()!;
            var context = test.GetProperty("context");
            var expression = test.GetProperty("expression").ValueKind == JsonValueKind.String ? test.GetProperty("expression").GetString() : null;
            var outcome = test.GetProperty("expectedPublicOutcome").GetString();
            check("inrange-normative-context-" + id, context.GetProperty("sourceLineHex").GetString() == ""
                && context.GetProperty("sourceLineCount").GetInt32() == 1 && context.GetProperty("rowIndex").GetInt32() == 0
                && context.GetProperty("outerEol").GetInt32() == 0 && !context.GetProperty("tableMode").GetBoolean()
                && context.GetProperty("pane").GetString() == "Left" && context.GetProperty("targetMask").GetString() == "Left"
                && context.GetProperty("forbidRetargeting").GetBoolean(), "Frozen EOF empty string[1] remains distinct from a zero-byte file.");
            selection.Add(new { id, expression, originalContext = context.Clone(), outcome,
                physicalFaithful = false, physicalStatus = expression is null ? "unrepresentable-not-executed" : "separate-derived-probe",
                publicBridges = test.GetProperty("publicBridges").Clone(),
                reason = "No exact physical EOF empty string[1]. Derived probes retain expression but use a nonempty CRLF source; native golden supplies serialized key only." });
            if (expression is null) { unsupported++; continue; }
            if (outcome == "compile-constructor-rejection")
            { await Reject("derived-" + id, simple, [new(expression, 1)]); constructor++; continue; }
            if (outcome != "serialized-value-equivalence" || context.GetProperty("kind").GetString() != "literal-no-getter")
                throw new IOException("Unexpected InRange corpus expression/context: " + id);
            var key = Convert.FromHexString(golden[id].GetProperty("serializedUtf8Hex").GetString()!);
            if (key.Any(value => value is 0 or 10 or 13)) throw new IOException("InRange serialized key needs an explicit boundary selection: " + id);
            var input = await Create("derived-" + id, Utf8("physical probe\r\n"), key.Concat(Utf8("\r\n")).ToArray());
            await Compare("derived-" + id, input, false, expression);
            await WriteSource(Path.Combine(work, "derived-" + id + "-context.json"), JsonSerializer.SerializeToUtf8Bytes(new
            { probeId = "derived-" + id, expectedSerializedBytesFrom = id, expression, originalContext = context.Clone(),
                actualContext = new { sourceLineHex = Convert.ToHexString(Utf8("physical probe")), sourceLineCount = 1, rowIndex = 0, outerEol = 2, tableMode = false, pane = "Left", targetMask = "Left" },
                equivalence = test.GetProperty("publicBridges").GetArrayLength() > 0 ? "typed ToInt bridge: serialized key only" : "literal serialized key only" }, jsonOptions));
            derived++; if (test.GetProperty("publicBridges").GetArrayLength() > 0) bridges++;
        }
        await WriteSource(Path.Combine(work, "cli-context-selection.ndjson"), Utf8(string.Join("\n", selection.Select(value => JsonSerializer.Serialize(value, jsonOptions))) + "\n"));
        check("inrange-cli-selection-counts", derived == 59 && constructor == 3 && unsupported == 10 && bridges == 12,
            $"faithful=0; derived={derived}; constructor={constructor}; unsupported={unsupported}; ToInt bridges={bridges}");
        var probes = plan.RootElement.GetProperty("publicConstructorProbes").EnumerateArray().ToArray();
        check("inrange-separate-probe-count", probes.Length == 1 && probes[0].GetProperty("id").GetString() == "public-arity-4", "Separate public argument substitution; not native arity4 AST equivalence.");
        foreach (var probe in probes)
            await Reject("derived-" + probe.GetProperty("id").GetString(), simple, [new(probe.GetProperty("expression").GetString()!, 1)]);
        await Reject("property-pollution", simple, [new("@noopt InRange(Line,0,3).Injected", 1)]);
        var table = await Create("table", Utf8("\"b,part\",keep\r\n"), Utf8("\"c,part\",keep\r\n"));
        const string tableExpression = "@cs @noopt InRange(Column1,\"a\",\"z\")";
        await Compare("table-column", table, true, tableExpression);
        await Projects(table, tableExpression);
        await Reject("rule-count-257", simple, Enumerable.Repeat(new Rule("@noopt InRange(Line,Line,Line)", 1), 257).ToArray(), "比較プロジェクトの行変換が不正です。");
        var large = Utf8(new string('a', 262144) + "\r\n" + new string('a', 262144) + "\r\n");
        var budgetInput = await Create("shared-budget", large, large);
        // 判定のbooleanで長いキーを短縮せず、各後続groupにも同じ本文を渡す。
        await Reject("shared-budget-256", budgetInput, Enumerable.Repeat(new Rule("@noopt If(InRange(Line,Line,Line),Line,Line)", 1), 256).ToArray(), "行変換の共有処理量または生成文字数");
        foreach (var source in sources)
            check("inrange-source-sha-" + Path.GetFileName(source.Key), File.Exists(source.Key)
                && SHA256.HashData(await File.ReadAllBytesAsync(source.Key)).SequenceEqual(SHA256.HashData(source.Value)), "Complete original input/workspace/output sentinel bytes preserved: " + source.Key);
        async Task WriteSource(string path, byte[] bytes)
        {
            Within(path, work);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await stream.WriteAsync(bytes);
            sources.Add(path, bytes);
        }
        async Task<Inputs> Create(string name, byte[] left, byte[] right)
        {
            var lp = Path.Combine(work, name + "-left.txt"); var rp = Path.Combine(work, name + "-right.txt");
            await WriteSource(lp, left); await WriteSource(rp, right); return new(lp, rp, left, right);
        }
        async Task Compare(string name, Inputs input, bool tableMode, string expression)
        {
            var result = await run("inrange-" + name, 0, true,
                [tableMode ? "--table" : "--compare", input.Left, input.Right, "--line-transform", tableMode ? "all" : "left", expression]);
            using var parsed = Parse(result); if (parsed is null) return;
            check(result.Name + "-classification", result.ExitCode == 0 && !parsed.RootElement.GetProperty("different").GetBoolean(), "Golden serialized bytes produce equal comparison keys.");
            if (tableMode) return;
            var rows = parsed.RootElement.GetProperty("rows").EnumerateArray().ToArray();
            foreach (var (side, bytes) in new[] { ("left", input.LeftBytes), ("right", input.RightBytes) })
            {
                var actual = rows.Where(row => row.TryGetProperty(side + "Line", out _)).ToArray();
                var expected = Lines(Encoding.UTF8.GetString(bytes));
                check(result.Name + "-original-" + side, actual.Select(row => row.GetProperty(side + "Line").GetInt32()).SequenceEqual(Enumerable.Range(1, expected.Length))
                    && actual.Select(row => row.GetProperty(side).GetString()).SequenceEqual(expected), "Original row text and indexes retained.");
            }
        }
        async Task Reject(string name, Inputs input, Rule[] rules, string? reason = null)
        {
            var project = Path.Combine(work, name + "-project.json");
            await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Text", leftPath = input.Left, rightPath = input.Right, lineTransformRules = rules }, jsonOptions));
            var html = Path.Combine(work, name + "-existing.html"); var sentinel = Utf8("existing InRange report\r\n");
            await WriteSource(html, sentinel);
            var result = await run("inrange-reject-report-" + name, 2, false, ["--report-project", project, html]);
            check(result.Name + "-atomic", result.ExitCode == 2 && string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr)
                && (reason is null || result.Stderr.Contains(reason, StringComparison.Ordinal)), "No partial stdout; public rejection reason=" + (reason ?? "nonempty stderr"));
            check(result.Name + "-output-sha", File.Exists(html) && (await File.ReadAllBytesAsync(html)).SequenceEqual(sentinel), "Complete existing HTML unchanged.");
            if (name != "shared-budget-256")
            {
                var existingCopy = Path.Combine(work, name + "-existing-copy.json");
                var copySentinel = Utf8("existing InRange project output\r\n");
                await WriteSource(existingCopy, copySentinel);
                var copyResult = await run("inrange-reject-copy-" + name, 2, false, ["--project-copy", project, existingCopy]);
                check(copyResult.Name + "-atomic", copyResult.ExitCode == 2 && string.IsNullOrWhiteSpace(copyResult.Stdout)
                    && !string.IsNullOrWhiteSpace(copyResult.Stderr), "Rejected invalid imported rules; no partial stdout.");
                check(copyResult.Name + "-output-sha", File.Exists(existingCopy)
                    && (await File.ReadAllBytesAsync(existingCopy)).SequenceEqual(copySentinel), "Existing JSON unchanged.");
            }
            // NULをOS argvへ渡さない。project JSON経路では正確な式を渡せる。
            if (rules.Length == 1 && !rules[0].Expression.Contains('\0'))
            {
                var cli = await run("inrange-reject-cli-" + name, 2, false, ["--compare", input.Left, input.Right, "--line-transform", "left", rules[0].Expression]);
                check(cli.Name + "-atomic", cli.ExitCode == 2 && string.IsNullOrWhiteSpace(cli.Stdout) && !string.IsNullOrWhiteSpace(cli.Stderr)
                    && (!name.Contains("arity", StringComparison.Ordinal) || cli.Stderr.Contains("行変換関数inrangeの引数数が不正です。", StringComparison.Ordinal)), "Public CLI rejection without partial JSON; exact arity error for constructor probes.");
            }
            // project importの例外包装と公開CLIの件数検証を区別する。この境界だけ多重flagsで実行する。
            if (name == "rule-count-257")
            {
                var arguments = new List<string> { "--compare", input.Left, input.Right };
                foreach (var rule in rules) arguments.AddRange(["--line-transform", "left", rule.Expression]);
                var cli = await run("inrange-reject-cli-" + name, 2, false, arguments.ToArray());
                check(cli.Name + "-atomic", cli.ExitCode == 2 && string.IsNullOrWhiteSpace(cli.Stdout)
                    && cli.Stderr.Contains("行変換は256件まで指定できます。", StringComparison.Ordinal),
                    "257 explicit CLI rules reject at the public count boundary, with no partial JSON.");
            }
        }
        JsonDocument? Parse(CommandResult result)
        {
            try { return JsonDocument.Parse(result.Stdout); }
            catch (JsonException error) { check(result.Name + "-json", false, error.Message); return null; }
        }
        async Task Projects(Inputs input, string expression)
        {
            var project = Path.Combine(work, "table-project.json");
            await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Table", leftPath = input.Left, rightPath = input.Right,
                tableDelimiter = ',', lineTransformRules = new[] { new Rule(expression, 7) } }, jsonOptions));
            var copy = Path.Combine(work, "table-project-copy.json"); var again = Path.Combine(work, "table-project-again.json");
            await run("inrange-project-copy", 0, true, ["--project-copy", project, copy]);
            await run("inrange-project-recopy", 0, true, ["--project-copy", copy, again]);
            check("inrange-project-stable-roundtrip", File.Exists(copy) && File.Exists(again) && File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(again)), "Stable project JSON.");
            if (File.Exists(copy))
            {
                using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(copy));
                check("inrange-project-expression-mask", saved.RootElement.GetProperty("lineTransformRules")[0].GetProperty("expression").GetString() == expression
                    && saved.RootElement.GetProperty("lineTransformRules")[0].GetProperty("panes").GetInt32() == 7, "Expression and physical mask persisted.");
            }
            var html = Path.Combine(work, "table-report.html");
            await run("inrange-project-report", 0, true, ["--report-project", project, html]); CheckHtml(html, false);
            var overridden = Path.Combine(work, "table-override.html");
            await run("inrange-project-report-override", 0, true, ["--report-project", project, overridden, "--line-transform", "all", "Line"]); CheckHtml(overridden, true);
            var package = Path.Combine(work, "table-package.zip");
            await run("inrange-package", 0, true, ["--package-project", project, package, "--report"]);
            if (!File.Exists(package)) return;
            using (var zip = ZipFile.OpenRead(package))
            {
                var assets = new List<byte[]>();
                foreach (var entry in zip.Entries.Where(entry => entry.Name.Length > 0))
                { using var stream = entry.Open(); using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer); assets.Add(buffer.ToArray()); }
                check("inrange-package-original-assets", assets.Any(bytes => bytes.SequenceEqual(input.LeftBytes)) && assets.Any(bytes => bytes.SequenceEqual(input.RightBytes)), "ZIP independently read; original full assets preserved.");
            }
            var expanded = Path.Combine(work, "expanded");
            await run("inrange-package-expand", 0, true, ["--archive-extract", package, expanded]);
            var reopened = Path.Combine(work, "reopened.json");
            await run("inrange-package-reopen", 0, true, ["--project-copy", Path.Combine(expanded, "project.json"), reopened]);
            var reloaded = Path.Combine(work, "reloaded.html");
            await run("inrange-package-reloaded-report", 0, true, ["--report-project", reopened, reloaded]); CheckHtml(reloaded, false);
            void CheckHtml(string path, bool different)
            {
                var htmlText = File.Exists(path) ? File.ReadAllText(path) : "";
                check("inrange-html-" + Path.GetFileName(path), htmlText.Contains("data-different=\"" + (different ? "true" : "false") + "\"", StringComparison.Ordinal)
                    && htmlText.Contains("b,part", StringComparison.Ordinal) && htmlText.Contains("c,part", StringComparison.Ordinal)
                    && (different || htmlText.Contains("data-ignored=\"true\"", StringComparison.Ordinal))
                    && Regex.Matches(htmlText, "<td\\b[^>]*><pre>").Count == 4, "Original cells, ignored classification and all rows retained.");
            }
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
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("InRange path escapes root.");
        NoLinks(path);
    }
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("InRange paths cannot traverse links.");
    }
    private static void RequireExternal(string path, string repository)
    {
        var full = Path.GetFullPath(path); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { repository, Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("InRange E2E requires external output.");
        }
        if (full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase))) throw new IOException("InRange output cannot use an artifacts folder.");
        NoLinks(path);
    }
}
