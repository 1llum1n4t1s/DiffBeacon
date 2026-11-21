using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformSizeLiteralScenarios
{
    private sealed record Inputs(string Left, string Right, byte[] LeftBytes, byte[] RightBytes);
    private sealed record Rule(string Expression, int Panes);
    private const string GoldenSha = "abe36d531ca546c5683a153b36be4e1f5ed31f971b9b3c322f535cf55e1e3fe8";

    // Native constructor/key 原本の派生公開観測。Native parser/typed value 資格とは別。
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("SizeLiteral fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformSizeLiterals");
        var work = Path.GetFullPath(Path.Combine(fixtures, "line-transform-size-literals"));
        RequireExternal(work, repository.FullName);
        if (Directory.Exists(work) || File.Exists(work)) throw new IOException("Fresh SizeLiteral E2E output required.");
        Directory.CreateDirectory(work);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var sources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var expected = new List<object>(); var selection = new List<object>();
        var engineeringExpected = new List<object>();
        var goldenBytes = await File.ReadAllBytesAsync(Path.Combine(reference, "golden.ndjson"));
        if (Hex(goldenBytes) != GoldenSha) throw new IOException("Frozen Native95 golden SHA differs.");
        var goldenLines = Encoding.UTF8.GetString(goldenBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimEnd('\r')).ToArray();
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "fixed-manifest.json")));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = file.GetProperty("path").GetString()!;
            var path = Path.GetFullPath(Path.Combine(reference, name)); Within(path, reference);
            var bytes = await File.ReadAllBytesAsync(path);
            if (bytes.LongLength != file.GetProperty("size").GetInt64() || Hex(bytes) != file.GetProperty("sha256").GetString())
                throw new IOException("Frozen SizeLiteral source differs: " + name);
        }
        using var plan = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "public-case-plan.json")));
        if (goldenLines.Length != 95 || plan.RootElement.GetProperty("goldenSha256").GetString() != GoldenSha)
            throw new IOException("Native95 plan SHA/count differs.");
        var cases = plan.RootElement.GetProperty("cases").EnumerateArray().Select(x => x.Clone()).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal); int ordinal = 0, bridges = 0, constructors = 0, rounding = 0;
        foreach (var test in cases)
        {
            var id = test.GetProperty("id").GetString()!; var native = test.GetProperty("nativeReference");
            if (!seen.Add(id) || native.GetProperty("lineNumber").GetInt32() != ++ordinal || ordinal > goldenLines.Length)
                throw new IOException("Native95 order/id differs.");
            var line = goldenLines[ordinal - 1]; using var document = JsonDocument.Parse(line); var record = document.RootElement;
            if (record.GetProperty("id").GetString() != id || native.GetProperty("fileSha256").GetString() != GoldenSha
                || Hex(Utf8(line)) != native.GetProperty("lineSha256WithoutTerminator").GetString()
                || !JsonElement.DeepEquals(record, native.GetProperty("record"))) throw new IOException("Native95 raw record differs.");
            var classification = test.GetProperty("classification").GetString();
            var bridge = classification == "planned-public-serialized-bridge";
            selection.Add(new { id, classification, producerCompiled = false, executed = false,
                parserEquivalent = false, directiveEquivalent = false, actualTypedObserved = false, contextEquivalent = false,
                publicCliExecutionPlanned = bridge, nativeReference = native.Clone(), expression = test.GetProperty("expression").Clone() });
            if (!bridge)
            {
                if (classification == "constructor-only-unsupported-public") constructors++;
                else if (classification == "defined-cast-platform-rounding-observation") rounding++;
                else throw new IOException("Unknown Native95 classification.");
                continue;
            }
            var expression = test.GetProperty("expression").GetString()!;
            var key = Convert.FromHexString(record.GetProperty("serializedUtf8Hex").GetString()!);
            if (!record.GetProperty("returned").GetBoolean() || key.Any(x => x is 0 or 10 or 13)) throw new IOException("Unplanned physical key boundary.");
            var count = test.GetProperty("nativeCase").GetProperty("providerCount").GetInt32();
            if (count < 1) throw new IOException("Cannot synthesize Native zero-provider rows.");
            var input = await Create(id, Utf8(string.Concat(Enumerable.Repeat("physical probe\r\n", count))), Repeat(key, count));
            await Compare("bridge-" + id, input, false, [new(expression, 1)], false, native.Clone());
            await Compare("bridge-table-" + id, input, true, [new(expression, 1)], false, native.Clone());
            if (id == "unit-1-1.5")
            {
                await Compare("raw-negative-control", input, false, [], true, "Original rows differ without transform; exit1 required.");
                await Projects(id, input, expression);
                var textProject = Path.Combine(work, "plain-project.json");
                await WriteSource(textProject, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Text", leftPath = input.Left,
                    rightPath = input.Right, lineTransformRules = new[] { new Rule(expression, 1) } }, options));
                var textHtml = Path.Combine(work, "plain-report.html");
                await run("size-literal-plain-report", 0, true, ["--report-project", textProject, textHtml]);
                CheckHtml(textHtml, false);
                check("size-literal-plain-raw-html", File.ReadAllText(textHtml).Contains("physical probe", StringComparison.Ordinal), "Whole-line report retains original source.");
            }
            bridges++;
        }
        check("size-literal-selection-counts", ordinal == 95 && bridges == 77 && constructors == 14 && rounding == 4,
            $"Native95: serialized bridges={bridges}; constructor skips={constructors}; rounding unqualified={rounding}; Native parser qualified=0.");
        foreach (var id in new[] { "constructor-probe-10", "constructor-stod-underflow" })
        {
            var test = cases.Single(x => x.GetProperty("id").GetString() == id);
            var record = test.GetProperty("nativeReference").GetProperty("record");
            var expression = Encoding.UTF8.GetString(Convert.FromHexString(record.GetProperty("inputUtf8Hex").GetString()!));
            var key = Convert.FromHexString(record.GetProperty("serializedUtf8Hex").GetString()!);
            var input = await Create("additional-" + id, Utf8("physical probe\r\n"), Repeat(key, 1));
            await Compare("additional-public-" + id, input, false, [new(expression, 1)], false,
                new { originalNativeId = id, originalNativeRecord = record.Clone(), nativeParserExecuted = false,
                    contextEquivalent = false, actualTypedObserved = false, main95SkipUnchanged = true });
        }
        var simple = await Create("public-probes", Utf8("physical probe\r\n"), Utf8("physical probe\r\n"));
        using var probes = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "public-probes-and-boundaries.json")));
        var probeIndex = 0;
        foreach (var probe in probes.RootElement.GetProperty("tokenRuleProbes").EnumerateArray())
        {
            var expression = probe.GetProperty("text").GetString()!; var name = "token-" + ++probeIndex;
            if (probe.GetProperty("sizeLexemeMatchesStaticRule").GetBoolean())
                await Compare(name, simple, false, [new(expression, 7)], false, "Public Compile observation; static lexer rule only; Native parser unexecuted");
            else await Reject(name, simple, [new(expression, 1)], true);
        }
        var upper = probes.RootElement.GetProperty("unsupported").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "int64-upper-cast");
        foreach (var expression in upper.GetProperty("inputs").EnumerateArray())
            await Reject("finite-cast-upper-" + ++probeIndex, simple, [new(expression.GetString()!, 1)], true);
        await Reject("invalid-unit", simple, [new("1KiB", 1)], true);
        await Reject("rule-count-257", simple, Enumerable.Repeat(new Rule("1KB", 1), 257).ToArray(), true);
        var large = Utf8(new string('a', 262144) + "\r\n" + new string('a', 262144) + "\r\n");
        var budget = await Create("budget", large, large);
        await Reject("shared-budget-256", budget, Enumerable.Repeat(new Rule("If(1KB==1024,Line,Line)", 1), 256).ToArray(), false);
        // engineering専用。main95選択・public-probes18・既存expected一覧へ追加しない。
        var engineeringCount = 0;
        foreach (var test in new (string Id, string Adjacent, string Spaced, string ExpectedHex)[]
        {
            ("size-and", "1KB==1KBAND TRUE", "1KB==1KB AND TRUE", "74727565"),
            ("integer-and", "1KB==1024AND TRUE", "1KB==1024 AND TRUE", "74727565"),
            ("integer-or", "1B==1OR FALSE", "1B==1 OR FALSE", "74727565"),
            ("size-contains", "1KBCONTAINS 1B", "1KB CONTAINS 1B", "6e6f6e65")
        })
        {
            foreach (var form in new[] { (Name: "adjacent", Expression: test.Adjacent), (Name: "spaced", Expression: test.Spaced) })
            {
                var name = "engineering-adjacency-" + test.Id + "-" + form.Name;
                var input = await Create(name, Utf8("engineering source RAW\r\n"), Repeat(Convert.FromHexString(test.ExpectedHex), 1));
                await Compare(name, input, false, [new(form.Expression, 1)], false,
                    new { engineering = true, classification = "engineering-adjacency-public-serialized-observation", expression = form.Expression,
                        expectedSerializedUtf8Hex = test.ExpectedHex, expectedSource = "literal constant; never product-regenerated" });
                engineeringCount++;
            }
        }
        foreach (var expression in new[] { "1KBB", "1KB2", "1KBANDTRUE", "1KiB", "1ns" })
        {
            await Reject("engineering-" + expression, simple, [new(expression, 1)], true); engineeringCount++;
        }
        var engineeringRounding = 0;
        foreach (var item in cases.Where(x => x.GetProperty("classification").GetString() == "defined-cast-platform-rounding-observation"))
        {
            var native = item.GetProperty("nativeReference").GetProperty("record");
            var nativeInput = Convert.FromHexString(native.GetProperty("inputUtf8Hex").GetString()!);
            var expression = new UTF8Encoding(false, true).GetString(nativeInput);
            if (expression != item.GetProperty("nativeCase").GetProperty("input").GetString()) throw new IOException("Engineering rounding input reference differs.");
            var key = Convert.FromHexString(native.GetProperty("serializedUtf8Hex").GetString()!);
            var name = "engineering-public-" + item.GetProperty("id").GetString();
            var input = await Create(name, Utf8("engineering rounding RAW\r\n"), Repeat(key, 1));
            await Compare(name, input, false, [new(expression, 1)], false,
                new { engineering = true, classification = "defined-cast-platform-rounding-observation", expression,
                    expectedSerializedUtf8Hex = native.GetProperty("serializedUtf8Hex").GetString(), nativeInputUtf8Hex = native.GetProperty("inputUtf8Hex").GetString(),
                    originalNativeId = item.GetProperty("id").GetString(), nativeReference = item.GetProperty("nativeReference").Clone() });
            engineeringCount++; engineeringRounding++;
        }
        check("size-literal-engineering-separate-counts", engineeringCount == 17 && engineeringRounding == 4,
            "engineering only: 8 adjacency, 5 reject, 4 public rounding; main95/public-probes18 unchanged; no Native parser/typed compatibility.");
        await Scopes(cases.Single(x => x.GetProperty("id").GetString() == "array-0"));
        foreach (var source in sources)
            check("size-literal-source-sha-" + Path.GetFileName(source.Key), File.Exists(source.Key) && Hex(await File.ReadAllBytesAsync(source.Key)) == Hex(source.Value), "Entire original input/project/sentinel retained.");
        await WriteSource(Path.Combine(work, "cli-context-selection.ndjson"), Utf8(string.Join("\n", selection.Select(x => JsonSerializer.Serialize(x, options))) + "\n"));
        await WriteSource(Path.Combine(work, "expected.json"), JsonSerializer.SerializeToUtf8Bytes(new { nativeMain = 95, bridges, constructors, rounding,
            additionalPublicProbes = 2, nativeParserQualified = 0, nativeTypedQualified = 0,
            cancellation = "Not executed: existing run delegate has no cancellation injection; App diagnostics/UI required.", cases = expected }, options));

        await WriteSource(Path.Combine(work, "engineering-observations.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            engineering = true, observations = engineeringCount, rounding = engineeringRounding,
            qualification = "engineering CLI serialized observation; not original Native parser/directive/typed compatibility; historical main95/public-probes18 unchanged",
            nativeParserQualified = 0, nativeTypedQualified = 0, cases = engineeringExpected
        }, options));
        async Task Scopes(JsonElement test)
        {
            var expression = test.GetProperty("expression").GetString()!;
            var key = Convert.FromHexString(test.GetProperty("nativeReference").GetProperty("record").GetProperty("serializedUtf8Hex").GetString()!);
            var csvKey = Repeat(key, 1);
            var csvRaw = Utf8("\"physical,probe\",tail,raw\r\n");
            foreach (var scope in new[] { "left", "base", "right", "all" })
            {
                var input = await Create("scope-" + scope, scope is "left" or "all" ? csvRaw : csvKey, scope is "right" or "all" ? csvRaw : csvKey);
                var basePath = Path.Combine(work, "scope-" + scope + "-base.csv");
                await WriteSource(basePath, scope is "base" or "all" ? csvRaw : csvKey);
                var args = new List<string> { "--table", input.Left, input.Right, "--base", basePath, "--line-transform", scope,
                    expression };
                var result = await run("size-literal-scope-" + scope, 0, true, args.ToArray());
                using var actual = JsonDocument.Parse(result.Stdout);
                check(result.Name + "-equal", !actual.RootElement.GetProperty("different").GetBoolean(), "Three panes whole-record CSV key; tail raw retained.");
                await Evidence(result, result.Arguments, input, new { scope, expression, expectedSerializedUtf8Hex = Convert.ToHexString(key), nativeParserEquivalent = false });
                var project = Path.Combine(work, "scope-" + scope + "-project.json");
                var mask = scope switch { "left" => 1, "base" => 2, "right" => 4, _ => 7 };
                await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Table", leftPath = input.Left,
                    basePath, rightPath = input.Right, tableDelimiter = ',', lineTransformRules = new[] { new Rule(expression, mask) } }, options));
                var html = Path.Combine(work, "scope-" + scope + ".html");
                await run("size-literal-scope-report-" + scope, 0, true, ["--report-project", project, html]);
                CheckHtml(html, false);
                check("size-literal-scope-raw-html-" + scope, File.ReadAllText(html).Contains("physical,probe", StringComparison.Ordinal)
                    && File.ReadAllText(html).Contains("tail", StringComparison.Ordinal) && File.ReadAllText(html).Contains("raw", StringComparison.Ordinal),
                    "Active CSV original cells remain visible.");
            }
        }
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
                result = await run("size-literal-" + name, 0, true, ["--report-project", project, html]);
                CheckHtml(html, different);
            }
            else
            {
                var expectedExit = different ? 1 : 0;
                result = await run("size-literal-" + name, expectedExit, true, args.ToArray());
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
            var engineering = result.Name.Contains("engineering-", StringComparison.Ordinal);
            if (engineering) expectation = new
            {
                engineering = true,
                qualification = "engineering CLI serialized observation; not original Native parser/directive/typed compatibility; main95/public-probes18 unchanged",
                nativeParserExecuted = false, nativeDirectiveExecuted = false, actualTypedObserved = false, contextEquivalent = false,
                observation = expectation
            };
            (engineering ? engineeringExpected : expected).Add(new { name = result.Name, expected = expectation, leftSha256 = Hex(input.LeftBytes), rightSha256 = Hex(input.RightBytes) });
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
            var html = Path.Combine(work, name + "-existing.html"); var sentinel = Utf8("existing size-literal output\r\n");
            await WriteSource(html, sentinel);
            var reportArgs = new[] { "--report-project", project, html };
            var result = await run("size-literal-reject-report-" + name, 2, false, reportArgs);
            check(result.Name + "-atomic", result.ExitCode == 2 && string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), "Whole public comparison abort; no native error enum equivalence.");
            check(result.Name + "-output", Hex(await File.ReadAllBytesAsync(html)) == Hex(sentinel), "Existing output unchanged.");
            if (name == "shared-budget-256") check(result.Name + "-budget-reason", result.Stderr.Contains("行変換の共有処理量または生成文字数", StringComparison.Ordinal), "Shared cumulative budget across 256 rules.");
            await Evidence(result, reportArgs, input, new { exitCode = 2, stdoutEmpty = true, name });
            if (importReject)
            {
                var copy = Path.Combine(work, name + "-existing.json"); await WriteSource(copy, sentinel);
                await run("size-literal-reject-copy-" + name, 2, false, ["--project-copy", project, copy]);
                check("size-literal-reject-copy-" + name + "-preserved", Hex(await File.ReadAllBytesAsync(copy)) == Hex(sentinel), "Import rejection preserves existing JSON.");
            }
            if (rules.Length == 1 || rules.Length == 257)
            {
                var args = new List<string> { "--compare", input.Left, input.Right };
                foreach (var rule in rules) args.AddRange(["--line-transform", "left", rule.Expression]);
                var cli = await run("size-literal-reject-cli-" + name, 2, false, args.ToArray());
                check(cli.Name + "-atomic", cli.ExitCode == 2 && string.IsNullOrWhiteSpace(cli.Stdout) && !string.IsNullOrWhiteSpace(cli.Stderr), "CLI rejection has no partial JSON.");
                if (rules.Length == 257) check(cli.Name + "-count-reason", cli.Stderr.Contains("行変換は256件まで指定できます。", StringComparison.Ordinal), "Explicit CLI rule count.");
                await Evidence(cli, args.ToArray(), input, new { exitCode = 2, stdoutEmpty = true, name });
            }
        }
        async Task Projects(string name, Inputs input, string expression)
        {
            var project = Path.Combine(work, name + "-project.json");
            await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Table", leftPath = input.Left, rightPath = input.Right, leftReadOnly = true, rightReadOnly = true,
                tableDelimiter = ',', lineTransformRules = new[] { new Rule(expression, 1) } }, options));
            var copy = Path.Combine(work, name + "-copy.json"); var again = Path.Combine(work, name + "-again.json");
            await run("size-literal-project-copy-" + name, 0, true, ["--project-copy", project, copy]);
            await run("size-literal-project-recopy-" + name, 0, true, ["--project-copy", copy, again]);
            check("size-literal-project-stable-" + name, File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(again)), "Stable project JSON.");
            using (var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(copy)))
                check("size-literal-project-rule-" + name, saved.RootElement.GetProperty("lineTransformRules")[0].GetProperty("expression").GetString() == expression
                    && saved.RootElement.GetProperty("lineTransformRules")[0].GetProperty("panes").GetInt32() == 1
                    && saved.RootElement.GetProperty("leftReadOnly").GetBoolean() && saved.RootElement.GetProperty("rightReadOnly").GetBoolean(), "Exact expression/mask/read-only state.");
            using (var originalProject = JsonDocument.Parse(await File.ReadAllBytesAsync(project)))
            {
                var workspace = Path.Combine(work, name + "-workspace.json");
                await WriteSource(workspace, JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1,
                    entries = new[] { originalProject.RootElement.Clone() }, activeEntryIndex = 0 }, options));
                var workspaceCopy = Path.Combine(work, name + "-workspace-copy.json");
                await run("size-literal-workspace-copy-" + name, 0, true, ["--project-copy", workspace, workspaceCopy]);
                var workspaceHtml = Path.Combine(work, name + "-workspace.html");
                await run("size-literal-workspace-report-" + name, 0, true, ["--report-project", workspaceCopy, workspaceHtml]);
                CheckHtml(workspaceHtml, false);
            }
            var html = Path.Combine(work, name + "-report.html");
            await run("size-literal-project-report-" + name, 0, true, ["--report-project", project, html]); CheckHtmlText(name + "-normal", File.ReadAllText(html), false);
            var overridden = Path.Combine(work, name + "-override.html");
            await run("size-literal-project-override-" + name, 0, true, ["--report-project", project, overridden, "--line-transform", "all", "Line"]); CheckHtmlText(name + "-override", File.ReadAllText(overridden), true);
            var package = Path.Combine(work, name + "-package.zip");
            await run("size-literal-package-" + name, 0, true, ["--package-project", project, package, "--report"]);
            using (var zip = ZipFile.OpenRead(package))
            {
                var assets = new List<byte[]>();
                foreach (var entry in zip.Entries.Where(entry => entry.Name.Length > 0))
                { using var stream = entry.Open(); using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer); assets.Add(buffer.ToArray()); }
                check("size-literal-package-assets-" + name, assets.Any(bytes => bytes.SequenceEqual(input.LeftBytes)) && assets.Any(bytes => bytes.SequenceEqual(input.RightBytes)), "Original assets independently read from ZIP.");
                var report = zip.GetEntry("report.files/1.html");
                check("size-literal-package-report-" + name, report is not null, "Report included.");
                if (report is not null) { using var reader = new StreamReader(report.Open()); CheckHtmlText("package-" + name, await reader.ReadToEndAsync(), false); }
            }
            var expanded = Path.Combine(work, name + "-expanded");
            await run("size-literal-package-expand-" + name, 0, true, ["--archive-extract", package, expanded]);
            var reopened = Path.Combine(work, name + "-reopened.json");
            await run("size-literal-package-reopen-" + name, 0, true, ["--project-copy", Path.Combine(expanded, "project.json"), reopened]);
            var reloaded = Path.Combine(work, name + "-reloaded.html");
            await run("size-literal-package-reloaded-report-" + name, 0, true, ["--report-project", reopened, reloaded]); CheckHtmlText(name + "-reloaded", File.ReadAllText(reloaded), false);
            await run("size-literal-protect-input-" + name, 2, false, ["--report-project", project, input.Left]);
            await run("size-literal-protect-workspace-" + name, 2, false, ["--report-project", project, project]);
            void CheckHtmlText(string label, string text, bool different)
            {
                check("size-literal-html-" + label, text.Contains("data-different=\"" + (different ? "true" : "false") + "\"", StringComparison.Ordinal)
                    && text.Contains("physical probe", StringComparison.Ordinal)
                    && System.Text.RegularExpressions.Regex.Matches(text, "<td\\b[^>]*><pre>").Count == 2 * Encoding.UTF8.GetString(input.LeftBytes).Split("\r\n", StringSplitOptions.None)[..^1].Length
                    && (different || text.Contains("data-ignored=\"true\"", StringComparison.Ordinal)), "Original cells and ignored classification.");
            }
        }
        void CheckHtml(string path, bool different)
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            check("size-literal-html-" + Path.GetFileName(path), text.Contains("data-different=\"" + (different ? "true" : "false") + "\"", StringComparison.Ordinal), "Report key classification.");
        }
    }
    private static byte[] Utf8(string value) => new UTF8Encoding(false, true).GetBytes(value);
    private static string Hex(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static byte[] Repeat(byte[] key, int count) => Utf8(string.Concat(Enumerable.Repeat(Encoding.UTF8.GetString(key) + "\r\n", count)));
    private static void Within(string path, string root)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("SizeLiteral path escapes root.");
        NoLinks(path);
    }
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("SizeLiteral paths cannot traverse links.");
    }
    private static void RequireExternal(string path, string repository)
    {
        var full = Path.GetFullPath(path); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { repository, Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("SizeLiteral E2E requires external output.");
        }
        if (full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase))) throw new IOException("SizeLiteral output cannot use an artifacts folder.");
        NoLinks(path);
    }
}
