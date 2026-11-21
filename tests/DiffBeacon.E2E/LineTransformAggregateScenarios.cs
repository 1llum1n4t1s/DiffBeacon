using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformAggregateScenarios
{
    private sealed record Inputs(string Left, string Right, byte[] LeftBytes, byte[] RightBytes);
    private sealed record Rule(string Expression, int Panes);
    private const string GoldenSha = "74e1f5b6bb58af0433cb0d198ee3360da1ebcd1f8845da9b5ce2fd0e2f549e91";

    // 公開keyの派生観測。Native AST/contextの一致とは区別する。
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Aggregate fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformAggregates");
        var work = Path.GetFullPath(Path.Combine(fixtures, "line-transform-aggregates"));
        RequireExternal(work, repository.FullName);
        if (Directory.Exists(work) || File.Exists(work)) throw new IOException("Aggregate E2E requires fresh task output.");
        Directory.CreateDirectory(work);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var sources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var expected = new List<object>();
        var selection = new List<object>();
        var goldenPath = Path.Combine(reference, "golden.ndjson");
        var goldenBytes = await File.ReadAllBytesAsync(goldenPath);
        if (!Hex(goldenBytes).Equals(GoldenSha, StringComparison.OrdinalIgnoreCase)) throw new IOException("Frozen aggregate golden SHA differs.");
        var golden = File.ReadLines(goldenPath).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => { using var parsed = JsonDocument.Parse(line); return parsed.RootElement.Clone(); })
            .ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "fixed-manifest.json")));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = file.GetProperty("path").GetString()!;
            var path = Path.GetFullPath(Path.Combine(reference, name)); Within(path, reference);
            var bytes = await File.ReadAllBytesAsync(path);
            if (bytes.LongLength != file.GetProperty("size").GetInt64()
                || !Hex(bytes).Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Aggregate fixed source differs: " + name);
            check("aggregate-fixed-source-" + name, true, "Frozen source SHA and size.");
        }
        using var plan = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "public-case-plan.json")));
        if (golden.Count != 141 || plan.RootElement.GetProperty("cases").GetArrayLength() != 141)
            throw new IOException("Aggregate main corpus count differs.");
        int bridges = 0, unsupported = 0;
        foreach (var test in plan.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = test.GetProperty("id").GetString()!;
            var expression = test.GetProperty("expression").ValueKind == JsonValueKind.String ? test.GetProperty("expression").GetString() : null;
            var native = test.GetProperty("nativeFirstStepReference");
            // first-step原value/bit/errorは原典参照として保存し、CLI例外へ読み替えない。
            selection.Add(new { id, expression, contextEquivalent = false, physicalFaithful = false,
                physicalStatus = expression is null ? "unrepresentable-not-executed" : "derived-serialized-key-probe",
                nativeFirstStepReference = native.Clone(), publicInvocationPlan = test.TryGetProperty("publicInvocationPlan", out var invocation) ? invocation.Clone() : (JsonElement?)null });
            if (expression is null) { unsupported++; continue; }
            var originalStep = golden[id].GetProperty("steps")[0];
            if (!originalStep.GetProperty("returned").GetBoolean()
                || originalStep.GetProperty("serializedUtf8Hex").GetString() != native.GetProperty("serializedUtf8Hex").GetString())
                throw new IOException("Bridge does not reference successful native first step: " + id);
            var key = Convert.FromHexString(originalStep.GetProperty("serializedUtf8Hex").GetString()!);
            if (key.Any(value => value is 0 or 10 or 13)) throw new IOException("Physical key needs an explicit boundary plan: " + id);
            int count = test.GetProperty("publicInvocationPlan").GetProperty("providerLines").GetArrayLength();
            if (count < 1) throw new IOException("Zero-row native cases must remain unsupported.");
            var input = await Create(id, Utf8(string.Concat(Enumerable.Repeat("\r\n", count))), Repeat(key, count));
            await Compare("bridge-" + id, input, false, [new(expression, 1)], false, native.Clone());
            // Tableは空record境界を避ける非空一列producer。原provider/contextの再現とは別。
            var table = await Create(id + "-table", Utf8(string.Concat(Enumerable.Repeat("physical probe\r\n", count))), Repeat(key, count));
            await Compare("bridge-table-" + id, table, true, [new(expression, 1)], false, native.Clone());
            if (id.EndsWith("-integers", StringComparison.Ordinal)) await Projects(id, table, expression);
            bridges++;
        }
        check("aggregate-cli-selection-counts", bridges == 104 && unsupported == 37,
            $"faithful=0; derived serialized-key bridges={bridges}; unrepresentable={unsupported}; native main=141.");
        var simple = await Create("simple", Utf8("1\r\n2\r\n"), Utf8("3\r\n3\r\n"));
        using var probes = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "probes.json")));
        check("aggregate-separate-arity-count", probes.RootElement.GetProperty("probes").GetArrayLength() == 8, "Not added to native main corpus.");
        foreach (var probe in probes.RootElement.GetProperty("probes").EnumerateArray())
            await Reject(probe.GetProperty("id").GetString()!, simple, [new(probe.GetProperty("expression").GetString()!, 1)], true);
        // 公開の工程順序・cache寿命契約。期待keyはNative goldenの再生成ではない。
        await Compare("source-all-rows-frozen", simple, false, [new("Sum(ToInt(Line))", 1)], false, "public: both left rows become 3; SetLine does not recompute same aggregate");
        var perRule = await Create("per-rule", Utf8("1\r\n2\r\n"), Utf8("5\r\n5\r\n"));
        await Compare("per-rule-same-expression", perRule, false, [new("Sum(ToInt(Line))", 1), new("Sum(ToInt(Line))", 1)], false,
            "public: rule2 first scan sees 3,2; its independent cached sum is 5 for both rows");
        var ordered = await Create("mask-order", Utf8("1\r\n2\r\n"), Utf8("6\r\n6\r\n"));
        await Compare("mask-boundary-order", ordered, false, [new("Sum(ToInt(Line))", 1), new("Sum(ToInt(Line))", 3)], false,
            "public: inactive Base mask bit splits groups; rule2 sees completed 3,3 and yields 6");
        var panes = await Create("per-input", Utf8("1\r\n2\r\n"), Utf8("3\r\n4\r\n"));
        await Compare("per-input-cache", panes, false, [new("Sum(ToInt(Line))", 7)], true,
            "public: left key 3, right key 7; cross-input cached 3 would incorrectly compare equal");
        await Compare("new-process-transform-repeat", simple, false, [new("Sum(ToInt(Line))", 1)], false,
            "new process only; same compiled plan per-Transform cache remains a diagnostics/UI hook requirement");
        await Reject("caller-runtime-error", simple, [new("Sum(1/(LineNumber-LineNumber))", 1)], false);
        await Reject("rule-count-257", simple, Enumerable.Repeat(new Rule("Line", 1), 257).ToArray(), true);
        var large = Utf8(new string('a', 262144) + "\r\n" + new string('a', 262144) + "\r\n");
        var budget = await Create("budget", large, large);
        await Reject("shared-budget-256", budget, Enumerable.Repeat(new Rule("If(Sum(ToInt(\"1\"))==2,Line,Line)", 1), 256).ToArray(), false);
        foreach (var source in sources)
            check("aggregate-source-sha-" + Path.GetFileName(source.Key), File.Exists(source.Key)
                && Hex(await File.ReadAllBytesAsync(source.Key)) == Hex(source.Value), "Entire original input/project/sentinel preserved.");
        await WriteSource(Path.Combine(work, "cli-context-selection.ndjson"), Utf8(string.Join("\n", selection.Select(value => JsonSerializer.Serialize(value, options))) + "\n"));
        await WriteSource(Path.Combine(work, "expected.json"), JsonSerializer.SerializeToUtf8Bytes(new { faithful = 0, bridges, unsupported,
            separateArity = 8, cancellation = "Not executed: CLI has no cancellation injection.", cases = expected }, options));

        async Task WriteSource(string path, byte[] bytes)
        {
            Within(path, work);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await stream.WriteAsync(bytes); sources.Add(path, bytes);
        }
        async Task<Inputs> Create(string name, byte[] left, byte[] right)
        {
            var lp = Path.Combine(work, name + "-left.txt"); var rp = Path.Combine(work, name + "-right.txt");
            await WriteSource(lp, left); await WriteSource(rp, right); return new(lp, rp, left, right);
        }
        async Task Compare(string name, Inputs input, bool table, Rule[] rules, bool different, object provenance)
        {
            var args = new List<string> { table ? "--table" : "--compare", input.Left, input.Right };
            foreach (var rule in rules) args.AddRange(["--line-transform", rule.Panes == 1 ? "left" : "all", rule.Expression]);
            // 数値mask3のCLI表記は既存parser原典で確認する。mask群probeはproject経路を使う。
            CommandResult result;
            if (rules.Any(rule => rule.Panes == 3))
            {
                var project = Path.Combine(work, name + "-project.json");
                await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Text", leftPath = input.Left, rightPath = input.Right, lineTransformRules = rules }, options));
                // report-projectはCLI実プロセスでルール適用順を観測する。
                var html = Path.Combine(work, name + ".html");
                result = await run("aggregate-" + name, 0, true, ["--report-project", project, html]);
                CheckHtml(html, different);
            }
            else
            {
                var expectedExit = different ? 1 : 0;
                result = await run("aggregate-" + name, expectedExit, true, args.ToArray());
                using var parsed = JsonDocument.Parse(result.Stdout);
                check(result.Name + "-classification", result.ExitCode == expectedExit && parsed.RootElement.GetProperty("different").GetBoolean() == different, "Expected serialized-key comparison and difference exit code.");
                if (!table)
                    foreach (var (side, bytes) in new[] { ("left", input.LeftBytes), ("right", input.RightBytes) })
                    {
                        var rows = parsed.RootElement.GetProperty("rows").EnumerateArray().Where(row => row.TryGetProperty(side + "Line", out _)).ToArray();
                        var original = Encoding.UTF8.GetString(bytes).Split("\r\n", StringSplitOptions.None)[..^1];
                        check(result.Name + "-original-" + side, rows.Select(row => row.GetProperty(side + "Line").GetInt32()).SequenceEqual(Enumerable.Range(1, original.Length))
                            && rows.Select(row => row.GetProperty(side).GetString()).SequenceEqual(original), "All original rows/indexes preserved.");
                    }
            }
            await Evidence(result, result.Arguments, input, new { different, provenance });
        }
        async Task Evidence(CommandResult result, string[] args, Inputs input, object expectation)
        {
            expected.Add(new { name = result.Name, expected = expectation, leftSha256 = Hex(input.LeftBytes), rightSha256 = Hex(input.RightBytes) });
            await WriteSource(Path.Combine(work, result.Name + ".stdout.raw"), Utf8(result.Stdout));
            await WriteSource(Path.Combine(work, result.Name + ".stderr.raw"), Utf8(result.Stderr));
            await WriteSource(Path.Combine(work, result.Name + ".receipt.json"), JsonSerializer.SerializeToUtf8Bytes(new { result.Name,
                arguments = args, result.ExitCode, leftSha256 = Hex(input.LeftBytes), rightSha256 = Hex(input.RightBytes), expected = expectation,
                stdoutSha256 = Hex(Utf8(result.Stdout)), stderrSha256 = Hex(Utf8(result.Stderr)) }, options));
        }
        async Task Reject(string name, Inputs input, Rule[] rules, bool importReject)
        {
            var project = Path.Combine(work, name + "-project.json");
            await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Text", leftPath = input.Left, rightPath = input.Right, lineTransformRules = rules }, options));
            var html = Path.Combine(work, name + "-existing.html"); var sentinel = Utf8("existing aggregate output\r\n");
            await WriteSource(html, sentinel);
            var reportArgs = new[] { "--report-project", project, html };
            var result = await run("aggregate-reject-report-" + name, 2, false, reportArgs);
            check(result.Name + "-atomic", result.ExitCode == 2 && string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), "Whole public comparison abort; no native error enum equivalence.");
            check(result.Name + "-output", Hex(await File.ReadAllBytesAsync(html)) == Hex(sentinel), "Existing output unchanged.");
            if (name == "shared-budget-256") check(result.Name + "-budget-reason", result.Stderr.Contains("行変換の共有処理量または生成文字数", StringComparison.Ordinal), "Shared cumulative budget across 256 rules.");
            await Evidence(result, reportArgs, input, new { exitCode = 2, stdoutEmpty = true, name });
            if (importReject)
            {
                var copy = Path.Combine(work, name + "-existing.json"); await WriteSource(copy, sentinel);
                await run("aggregate-reject-copy-" + name, 2, false, ["--project-copy", project, copy]);
                check("aggregate-reject-copy-" + name + "-preserved", Hex(await File.ReadAllBytesAsync(copy)) == Hex(sentinel), "Import rejection preserves existing JSON.");
            }
            if (rules.Length == 1 || rules.Length == 257)
            {
                var args = new List<string> { "--compare", input.Left, input.Right };
                foreach (var rule in rules) args.AddRange(["--line-transform", "left", rule.Expression]);
                var cli = await run("aggregate-reject-cli-" + name, 2, false, args.ToArray());
                check(cli.Name + "-atomic", cli.ExitCode == 2 && string.IsNullOrWhiteSpace(cli.Stdout) && !string.IsNullOrWhiteSpace(cli.Stderr), "CLI rejection has no partial JSON.");
                if (rules.Length == 257) check(cli.Name + "-count-reason", cli.Stderr.Contains("行変換は256件まで指定できます。", StringComparison.Ordinal), "Explicit CLI rule count.");
                await Evidence(cli, args.ToArray(), input, new { exitCode = 2, stdoutEmpty = true, name });
            }
        }
        async Task Projects(string name, Inputs input, string expression)
        {
            var project = Path.Combine(work, name + "-project.json");
            await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Table", leftPath = input.Left, rightPath = input.Right,
                tableDelimiter = ',', lineTransformRules = new[] { new Rule(expression, 1) } }, options));
            var copy = Path.Combine(work, name + "-copy.json"); var again = Path.Combine(work, name + "-again.json");
            await run("aggregate-project-copy-" + name, 0, true, ["--project-copy", project, copy]);
            await run("aggregate-project-recopy-" + name, 0, true, ["--project-copy", copy, again]);
            check("aggregate-project-stable-" + name, File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(again)), "Stable project JSON.");
            using (var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(copy)))
                check("aggregate-project-rule-" + name, saved.RootElement.GetProperty("lineTransformRules")[0].GetProperty("expression").GetString() == expression
                    && saved.RootElement.GetProperty("lineTransformRules")[0].GetProperty("panes").GetInt32() == 1, "Exact expression/mask.");
            var html = Path.Combine(work, name + "-report.html");
            await run("aggregate-project-report-" + name, 0, true, ["--report-project", project, html]); CheckHtmlText(name + "-normal", File.ReadAllText(html), false);
            var overridden = Path.Combine(work, name + "-override.html");
            await run("aggregate-project-override-" + name, 0, true, ["--report-project", project, overridden, "--line-transform", "all", "Line"]); CheckHtmlText(name + "-override", File.ReadAllText(overridden), true);
            var package = Path.Combine(work, name + "-package.zip");
            await run("aggregate-package-" + name, 0, true, ["--package-project", project, package, "--report"]);
            using (var zip = ZipFile.OpenRead(package))
            {
                var assets = new List<byte[]>();
                foreach (var entry in zip.Entries.Where(entry => entry.Name.Length > 0))
                { using var stream = entry.Open(); using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer); assets.Add(buffer.ToArray()); }
                check("aggregate-package-assets-" + name, assets.Any(bytes => bytes.SequenceEqual(input.LeftBytes)) && assets.Any(bytes => bytes.SequenceEqual(input.RightBytes)), "Original assets independently read from ZIP.");
                var report = zip.GetEntry("report.files/1.html");
                check("aggregate-package-report-" + name, report is not null, "Report included.");
                if (report is not null) { using var reader = new StreamReader(report.Open()); CheckHtmlText("package-" + name, await reader.ReadToEndAsync(), false); }
            }
            var expanded = Path.Combine(work, name + "-expanded");
            await run("aggregate-package-expand-" + name, 0, true, ["--archive-extract", package, expanded]);
            var reopened = Path.Combine(work, name + "-reopened.json");
            await run("aggregate-package-reopen-" + name, 0, true, ["--project-copy", Path.Combine(expanded, "project.json"), reopened]);
            var reloaded = Path.Combine(work, name + "-reloaded.html");
            await run("aggregate-package-reloaded-report-" + name, 0, true, ["--report-project", reopened, reloaded]); CheckHtmlText(name + "-reloaded", File.ReadAllText(reloaded), false);
            await run("aggregate-protect-input-" + name, 2, false, ["--report-project", project, input.Left]);
            await run("aggregate-protect-workspace-" + name, 2, false, ["--report-project", project, project]);
            void CheckHtmlText(string label, string text, bool different)
            {
                check("aggregate-html-" + label, text.Contains("data-different=\"" + (different ? "true" : "false") + "\"", StringComparison.Ordinal)
                    && text.Contains("physical probe", StringComparison.Ordinal)
                    && System.Text.RegularExpressions.Regex.Matches(text, "<td\\b[^>]*><pre>").Count == 2 * Encoding.UTF8.GetString(input.LeftBytes).Split("\r\n", StringSplitOptions.None)[..^1].Length
                    && (different || text.Contains("data-ignored=\"true\"", StringComparison.Ordinal)), "Original cells and ignored classification.");
            }
        }
        void CheckHtml(string path, bool different)
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            check("aggregate-html-" + Path.GetFileName(path), text.Contains("data-different=\"" + (different ? "true" : "false") + "\"", StringComparison.Ordinal), "Report key classification.");
        }
    }
    private static byte[] Utf8(string value) => new UTF8Encoding(false, true).GetBytes(value);
    private static string Hex(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static byte[] Repeat(byte[] key, int count) => Utf8(string.Concat(Enumerable.Repeat(Encoding.UTF8.GetString(key) + "\r\n", count)));
    private static void Within(string path, string root)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Aggregate path escapes root.");
        NoLinks(path);
    }
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Aggregate paths cannot traverse links.");
    }
    private static void RequireExternal(string path, string repository)
    {
        var full = Path.GetFullPath(path); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { repository, Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("Aggregate E2E requires external output.");
        }
        if (full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase))) throw new IOException("Aggregate output cannot use an artifacts folder.");
        NoLinks(path);
    }
}
