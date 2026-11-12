using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformDurationLiteralScenarios
{
    private sealed record Inputs(string Left, string Right, byte[] LeftBytes, byte[] RightBytes);
    private sealed record Rule(string Expression, int Panes);
    private const string GoldenSha = "bbd10685c14154365a905239292b0fcd3024d31ab70cd7eaf96b7d5453693aec";

    // Native constructor/key 原本の派生公開観測。Native parser/typed value 資格とは別。
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("DurationLiteral fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformDurationLiterals");
        var work = Path.GetFullPath(Path.Combine(fixtures, "line-transform-duration-literals"));
        RequireExternal(work, repository.FullName);
        if (Directory.Exists(work) || File.Exists(work)) throw new IOException("Fresh DurationLiteral E2E output required.");
        Directory.CreateDirectory(work);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var sources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var expected = new List<object>(); var selection = new List<object>();
        var engineeringExpected = new List<object>();
        var goldenBytes = await File.ReadAllBytesAsync(Path.Combine(reference, "golden.ndjson"));
        if (Hex(goldenBytes) != GoldenSha) throw new IOException("Frozen Native106 golden SHA differs.");
        var goldenLines = Encoding.UTF8.GetString(goldenBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimEnd('\r')).ToArray();
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "fixed-manifest.json")));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = file.GetProperty("path").GetString()!;
            var path = Path.GetFullPath(Path.Combine(reference, name)); Within(path, reference);
            var bytes = await File.ReadAllBytesAsync(path);
            if (bytes.LongLength != file.GetProperty("size").GetInt64() || Hex(bytes) != file.GetProperty("sha256").GetString())
                throw new IOException("Frozen DurationLiteral source differs: " + name);
        }
        using var plan = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "public-case-plan.json")));
        if (goldenLines.Length != 106 || plan.RootElement.GetProperty("goldenSha256").GetString() != GoldenSha)
            throw new IOException("Native106 plan SHA/count differs.");
        var cases = plan.RootElement.GetProperty("cases").EnumerateArray().Select(x => x.Clone()).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal); int ordinal = 0, bridges = 0, constructors = 0, rounding = 0, observedBridges = 0;
        foreach (var test in cases)
        {
            var id = test.GetProperty("id").GetString()!; var native = test.GetProperty("nativeReference");
            if (!seen.Add(id) || native.GetProperty("lineNumber").GetInt32() != ++ordinal || ordinal > goldenLines.Length)
                throw new IOException("Native106 order/id differs.");
            var line = goldenLines[ordinal - 1]; using var document = JsonDocument.Parse(line); var record = document.RootElement;
            if (record.GetProperty("id").GetString() != id || native.GetProperty("fileSha256").GetString() != GoldenSha
                || Hex(Utf8(line)) != native.GetProperty("lineSha256WithoutTerminator").GetString()
                || !JsonElement.DeepEquals(record, native.GetProperty("record"))) throw new IOException("Native106 raw record differs.");
            var classification = test.GetProperty("classification").GetString();
            var bridge = classification == "planned-public-serialized-bridge";
            if (!bridge)
            {
                if (classification == "constructor-only-unsupported-public") constructors++;
                else if (classification == "defined-cast-platform-rounding-observation") rounding++;
                else throw new IOException("Unknown Native106 classification.");
            selection.Add(new { id, classification, producerCompiled = false, executed = false,
                parserEquivalent = false, directiveEquivalent = false, nativeParserExecuted = false, nativeDirectiveExecuted = false, actualTypedObserved = false, typedPublicValueObserved = false, contextEquivalent = false, actualKeysObserved = false,
                publicCliExecutionPlanned = false, rawNativeReferenceLine = line, nativeReference = native.Clone(), actualKeys = Array.Empty<object>(), expression = test.GetProperty("expression").Clone() });
                continue;
            }
            var expression = PublicBridgeExpression(test);
            var key = Convert.FromHexString(record.GetProperty("serializedUtf8Hex").GetString()!);
            if (!record.GetProperty("returned").GetBoolean() || key.Any(x => x is 0 or 10 or 13)) throw new IOException("Unplanned physical key boundary.");
            var count = test.GetProperty("nativeCase").GetProperty("providerCount").GetInt32();
            if (count < 1) throw new IOException("Cannot synthesize Native zero-provider rows.");
            var input = await Create(id, Utf8(string.Concat(Enumerable.Repeat("physical probe\r\n", count))), Repeat(key, count));
            var publicObserved = await Compare("bridge-" + id, input, false, [new(expression, 1)], false, native.Clone());
            publicObserved &= await Compare("bridge-table-" + id, input, true, [new(expression, 1)], false, native.Clone());
            selection.Add(new { id, classification, producerCompiled = publicObserved, executed = publicObserved,
                parserEquivalent = false, directiveEquivalent = false, nativeParserExecuted = false, nativeDirectiveExecuted = false, actualTypedObserved = false, typedPublicValueObserved = false, contextEquivalent = false, actualKeysObserved = false,
                publicCliExecutionPlanned = true, rawNativeReferenceLine = line, nativeReference = native.Clone(), actualKeys = Array.Empty<object>(), expression, nativePlannedExpression = test.GetProperty("expression").Clone() });
            if (id == "aggregate-average-scalar")
            {
                await Compare("raw-negative-control", input, false, [], true, "Original rows differ without transform; exit1 required.");
                await Projects(id, input, expression);
                var textProject = Path.Combine(work, "plain-project.json");
                await WriteSource(textProject, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Text", leftPath = input.Left,
                    rightPath = input.Right, lineTransformRules = new[] { new Rule(expression, 1) } }, options));
                var textHtml = Path.Combine(work, "plain-report.html");
                await run("duration-literal-plain-report", 0, true, ["--report-project", textProject, textHtml]);
                CheckHtml(textHtml, false);
                check("duration-literal-plain-raw-html", File.ReadAllText(textHtml).Contains("physical probe", StringComparison.Ordinal), "Whole-line report retains original source.");
            }
            if (publicObserved) observedBridges++;
            bridges++;
        }
        check("duration-literal-selection-counts", ordinal == 106 && bridges == 92 && observedBridges == 92 && constructors == 10 && rounding == 4,
            $"Native106: serialized bridges={bridges}; constructor skips={constructors}; rounding unqualified={rounding}; Native parser qualified=0.");
        var simple = await Create("public-probes", Utf8("physical probe\r\n"), Utf8("physical probe\r\n"));
        using var probes = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "public-probes-and-boundaries.json")));
        var proposalObservations = new List<object>(); var probeIndex = 0;
        foreach (var probe in probes.RootElement.GetProperty("proposals").EnumerateArray())
        {
            var expression = probe.GetProperty("input").GetString()!;
            var shouldCompile = probe.TryGetProperty("expectedWholeTokenShape", out var whole) ? whole.GetBoolean() : true;
            await Probe(probe, expression, shouldCompile);
        }
        foreach (var probe in probes.RootElement.GetProperty("excluded").EnumerateArray())
            await Probe(probe, probe.GetProperty("input").GetString()!, probe.GetProperty("id").GetString() == "excluded-2");
        async Task Probe(JsonElement proposal, string expression, bool shouldCompile)
        {
            var name = "proposal-" + ++probeIndex;
            var project = Path.Combine(work, name + "-project.json");
            await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Text", leftPath = simple.Left,
                rightPath = simple.Right, lineTransformRules = new[] { new Rule(expression, 7) } }, options));
            var copy = Path.Combine(work, name + "-copy.json"); var sentinel = Utf8("existing proposal output");
            if (!shouldCompile) await WriteSource(copy, sentinel);
            var result = await run("duration-literal-" + name, shouldCompile ? 0 : 2, false, ["--project-copy", project, copy]);
            var passed = result.ExitCode == (shouldCompile ? 0 : 2) && (shouldCompile ? File.Exists(copy) : File.ReadAllBytes(copy).SequenceEqual(sentinel));
            check(result.Name + "-compile", passed, "Separate public import/Compile observation; static Native lexer proposal retained.");
            proposalObservations.Add(new { id = proposal.GetProperty("id").GetString(), staticProposal = proposal.Clone(), expression,
                publicCompileExecuted = true, producerCompiled = shouldCompile && passed, executed = false,
                nativeExecuted = false, nativeParserExecuted = false, nativeDirectiveExecuted = false,
                actualTypedObserved = false, contextEquivalent = false, expectedCompileSuccess = shouldCompile,
                actualKeys = Array.Empty<object>(), passed });
            await Evidence(result, result.Arguments, simple, new { staticProposal = proposal.Clone(), expectedCompileSuccess = shouldCompile });
        }
        await WriteSource(Path.Combine(work, "public-proposal-observations.ndjson"), Utf8(string.Join("\n", proposalObservations.Select(x => JsonSerializer.Serialize(x, options))) + "\n"));
        await Reject("invalid-unit", simple, [new("1xs", 1)], true);
        await Reject("rule-count-257", simple, Enumerable.Repeat(new Rule("1ms", 1), 257).ToArray(), true);
        var large = Utf8(new string('a', 262144) + "\r\n" + new string('a', 262144) + "\r\n");
        var budget = await Create("budget", large, large);
        await Reject("shared-budget-256", budget, Enumerable.Repeat(new Rule("If(1ms==1000,Line,Line)", 1), 256).ToArray(), false);
        // engineering専用。main106選択・static-proposals21・既存expected一覧へ追加しない。
        var engineeringCount = 0;
        foreach (var test in new (string Id, string Adjacent, string Spaced, string ExpectedHex)[]
        {
            ("milliseconds-integer-and", "1ms==1000AND TRUE", "1ms==1000 AND TRUE", "74727565"),
            ("longest-unit-and", "1milliseconds==1msAND TRUE", "1milliseconds==1ms AND TRUE", "74727565"),
            ("seconds-integer-or", "1s==1000000OR FALSE", "1s==1000000 OR FALSE", "74727565")
        })
        {
            foreach (var form in new[] { (Name: "adjacent", Expression: test.Adjacent), (Name: "spaced", Expression: test.Spaced) })
            {
                var name = "engineering-" + test.Id + "-" + form.Name;
                var input = await Create(name, Utf8("engineering source RAW\r\n"), Repeat(Convert.FromHexString(test.ExpectedHex), 1));
                await Compare(name, input, false, [new(form.Expression, 1)], false,
                    new { engineering = true, classification = "engineering-adjacency-public-serialized-observation", expression = form.Expression,
                        expectedSerializedUtf8Hex = test.ExpectedHex, expectedSource = "literal constant; never product-regenerated" });
                engineeringCount++;
            }
        }
        foreach (var expression in new[] { "1MB==1048576", "1m==60000000", "1ms==1000" })
        {
            var name = "engineering-precedence-" + engineeringCount;
            var input = await Create(name, Utf8("engineering source RAW\r\n"), Repeat(Utf8("true"), 1));
            await Compare(name, input, false, [new(expression, 1)], false,
                new { engineering = true, classification = "engineering-size-duration-precedence", expression, expectedSerializedUtf8Hex = "74727565" });
            engineeringCount++;
        }
        foreach (var expression in new[] { "1millisecondsX", "1msecX", "1ms2", "1msANDTRUE", "1xs", "1 ms", "1e2ms", ".1ms", "1.ms", "1ſ" })
        {
            await Reject("engineering-rejection-" + engineeringCount, simple, [new(expression, 1)], true); engineeringCount++;
        }
        var overflowProposal = probes.RootElement.GetProperty("excluded").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "excluded-2");
        var overflowInput = await Create("engineering-proposal-catch0", Utf8("engineering source RAW\r\n"), Repeat(Utf8("0"), 1));
        await Compare("engineering-proposal-catch0", overflowInput, false, [new(overflowProposal.GetProperty("input").GetString()!, 1)], false,
            new { engineering = true, staticProposal = overflowProposal.Clone(), expectedSerializedUtf8Hex = "30", nativeExecuted = false });
        engineeringCount++;
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
        check("duration-literal-engineering-separate-counts", engineeringCount == 24 && engineeringRounding == 4,
            "engineering only: 6 adjacency, 3 precedence, 10 reject, 1 raw stod catch0, 4 public rounding; main106/static-proposals21 unchanged; no Native parser/typed compatibility.");
        await Scopes(cases.Single(x => x.GetProperty("id").GetString() == "aggregate-average-scalar"));
        foreach (var source in sources)
            check("duration-literal-source-sha-" + Path.GetFileName(source.Key), File.Exists(source.Key) && Hex(await File.ReadAllBytesAsync(source.Key)) == Hex(source.Value), "Entire original input/project/sentinel retained.");
        await WriteSource(Path.Combine(work, "cli-context-selection.ndjson"), Utf8(string.Join("\n", selection.Select(x => JsonSerializer.Serialize(x, options))) + "\n"));
        await WriteSource(Path.Combine(work, "expected.json"), JsonSerializer.SerializeToUtf8Bytes(new { nativeMain = 106, bridges, observedBridges, constructors, rounding,
            additionalPublicProbes = 0, nativeParserQualified = 0, nativeTypedQualified = 0,
            cancellation = "Not executed: existing run delegate has no cancellation injection; App diagnostics/UI required.", cases = expected }, options));

        await WriteSource(Path.Combine(work, "engineering-observations.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            engineering = true, observations = engineeringCount, rounding = engineeringRounding,
            qualification = "engineering CLI serialized observation; not original Native parser/directive/typed compatibility; historical main106/static-proposals21 unchanged",
            nativeParserQualified = 0, nativeTypedQualified = 0, cases = engineeringExpected
        }, options));
        async Task Scopes(JsonElement test)
        {
            var expression = PublicBridgeExpression(test);
            var key = Convert.FromHexString(test.GetProperty("nativeReference").GetProperty("record").GetProperty("serializedUtf8Hex").GetString()!);
            var csvKey = Repeat(key, 1); var csvRaw = Utf8("\"physical,probe\",tail,raw\r\n");
            foreach (var (scope, mask) in new[] { ("left", 1), ("base", 2), ("right", 4), ("all", 7), ("left-base", 3), ("left-right", 5), ("base-right", 6) })
            {
                var input = await Create("scope-" + scope, (mask & 1) != 0 ? csvRaw : csvKey, (mask & 4) != 0 ? csvRaw : csvKey);
                var basePath = Path.Combine(work, "scope-" + scope + "-base.csv");
                await WriteSource(basePath, (mask & 2) != 0 ? csvRaw : csvKey);
                var project = Path.Combine(work, "scope-" + scope + "-project.json");
                await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Table", leftPath = input.Left,
                    basePath, rightPath = input.Right, tableDelimiter = ',', lineTransformRules = new[] { new Rule(expression, mask) } }, options));
                var html = Path.Combine(work, "scope-" + scope + ".html");
                var report = await run("duration-literal-scope-report-" + scope, 0, true, ["--report-project", project, html]);
                CheckHtml(html, false);
                check(report.Name + "-raw", File.ReadAllText(html).Contains("physical,probe", StringComparison.Ordinal)
                    && File.ReadAllText(html).Contains("tail", StringComparison.Ordinal) && File.ReadAllText(html).Contains("raw", StringComparison.Ordinal),
                    "Original whole-record CSV cells retained; seven pane masks use actual project path.");
                await Evidence(report, report.Arguments, input, new { scope, mask, expression, expectedSerializedUtf8Hex = Convert.ToHexString(key),
                    nativeParserEquivalent = false, baseSha256 = Hex(File.ReadAllBytes(basePath)) });
                var copy = Path.Combine(work, "scope-" + scope + "-copy.json");
                await run("duration-literal-scope-project-copy-" + scope, 0, true, ["--project-copy", project, copy]);
                var reloadHtml = Path.Combine(work, "scope-" + scope + "-recompare.html");
                await run("duration-literal-scope-recompare-" + scope, 0, true, ["--report-project", copy, reloadHtml]); CheckHtml(reloadHtml, false);
                if (mask is 1 or 2 or 4 or 7)
                {
                    var args = new[] { "--table", input.Left, input.Right, "--base", basePath, "--line-transform", scope, expression };
                    var result = await run("duration-literal-scope-" + scope, 0, true, args);
                    using var actual = JsonDocument.Parse(result.Stdout);
                    check(result.Name + "-equal", !actual.RootElement.GetProperty("different").GetBoolean(), "Three panes whole-record CSV key.");
                    await Evidence(result, result.Arguments, input, new { scope, expression, expectedSerializedUtf8Hex = Convert.ToHexString(key), nativeParserEquivalent = false });
                }
            }
        }        async Task WriteSource(string path, byte[] bytes)
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
        async Task<bool> Compare(string name, Inputs input, bool table, Rule[] rules, bool different, object provenance)
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
                result = await run("duration-literal-" + name, 0, true, ["--report-project", project, html]);
                CheckHtml(html, different);
            }
            else
            {
                var expectedExit = different ? 1 : 0;
                result = await run("duration-literal-" + name, expectedExit, true, args.ToArray());
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
            if (rules.Any(rule => rule.Panes == 3)) return result.ExitCode == 0;
            using var observed = JsonDocument.Parse(result.Stdout);
            return result.ExitCode == (different ? 1 : 0) && observed.RootElement.GetProperty("different").GetBoolean() == different;
        }
        async Task Evidence(CommandResult result, string[] args, Inputs input, object expectation)
        {
            var engineering = result.Name.Contains("engineering-", StringComparison.Ordinal);
            if (engineering) expectation = new
            {
                engineering = true,
                qualification = "engineering CLI serialized observation; not original Native parser/directive/typed compatibility; main106/static-proposals21 unchanged",
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
            var html = Path.Combine(work, name + "-existing.html"); var sentinel = Utf8("existing duration-literal output\r\n");
            await WriteSource(html, sentinel);
            var reportArgs = new[] { "--report-project", project, html };
            var result = await run("duration-literal-reject-report-" + name, 2, false, reportArgs);
            check(result.Name + "-atomic", result.ExitCode == 2 && string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), "Whole public comparison abort; no native error enum equivalence.");
            check(result.Name + "-output", Hex(await File.ReadAllBytesAsync(html)) == Hex(sentinel), "Existing output unchanged.");
            if (name == "shared-budget-256") check(result.Name + "-budget-reason", result.Stderr.Contains("行変換の共有処理量または生成文字数", StringComparison.Ordinal), "Shared cumulative budget across 256 rules.");
            await Evidence(result, reportArgs, input, new { exitCode = 2, stdoutEmpty = true, name });
            if (importReject)
            {
                var copy = Path.Combine(work, name + "-existing.json"); await WriteSource(copy, sentinel);
                await run("duration-literal-reject-copy-" + name, 2, false, ["--project-copy", project, copy]);
                check("duration-literal-reject-copy-" + name + "-preserved", Hex(await File.ReadAllBytesAsync(copy)) == Hex(sentinel), "Import rejection preserves existing JSON.");
            }
            if (rules.Length == 1 || rules.Length == 257)
            {
                var args = new List<string> { "--compare", input.Left, input.Right };
                foreach (var rule in rules) args.AddRange(["--line-transform", "left", rule.Expression]);
                var cli = await run("duration-literal-reject-cli-" + name, 2, false, args.ToArray());
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
            await run("duration-literal-project-copy-" + name, 0, true, ["--project-copy", project, copy]);
            await run("duration-literal-project-recopy-" + name, 0, true, ["--project-copy", copy, again]);
            check("duration-literal-project-stable-" + name, File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(again)), "Stable project JSON.");
            using (var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(copy)))
                check("duration-literal-project-rule-" + name, saved.RootElement.GetProperty("lineTransformRules")[0].GetProperty("expression").GetString() == expression
                    && saved.RootElement.GetProperty("lineTransformRules")[0].GetProperty("panes").GetInt32() == 1
                    && saved.RootElement.GetProperty("leftReadOnly").GetBoolean() && saved.RootElement.GetProperty("rightReadOnly").GetBoolean(), "Exact expression/mask/read-only state.");
            using (var originalProject = JsonDocument.Parse(await File.ReadAllBytesAsync(project)))
            {
                var workspace = Path.Combine(work, name + "-workspace.json");
                await WriteSource(workspace, JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1,
                    entries = new[] { originalProject.RootElement.Clone() }, activeEntryIndex = 0 }, options));
                var workspaceCopy = Path.Combine(work, name + "-workspace-copy.json");
                await run("duration-literal-workspace-copy-" + name, 0, true, ["--project-copy", workspace, workspaceCopy]);
                var workspaceHtml = Path.Combine(work, name + "-workspace.html");
                await run("duration-literal-workspace-report-" + name, 0, true, ["--report-project", workspaceCopy, workspaceHtml]);
                CheckHtml(workspaceHtml, false);
            }
            var html = Path.Combine(work, name + "-report.html");
            await run("duration-literal-project-report-" + name, 0, true, ["--report-project", project, html]); CheckHtmlText(name + "-normal", File.ReadAllText(html), false);
            var overridden = Path.Combine(work, name + "-override.html");
            await run("duration-literal-project-override-" + name, 0, true, ["--report-project", project, overridden, "--line-transform", "all", "Line"]); CheckHtmlText(name + "-override", File.ReadAllText(overridden), true);
            var package = Path.Combine(work, name + "-package.zip");
            await run("duration-literal-package-" + name, 0, true, ["--package-project", project, package, "--report"]);
            using (var zip = ZipFile.OpenRead(package))
            {
                var assets = new List<byte[]>();
                foreach (var entry in zip.Entries.Where(entry => entry.Name.Length > 0))
                { using var stream = entry.Open(); using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer); assets.Add(buffer.ToArray()); }
                check("duration-literal-package-assets-" + name, assets.Any(bytes => bytes.SequenceEqual(input.LeftBytes)) && assets.Any(bytes => bytes.SequenceEqual(input.RightBytes)), "Original assets independently read from ZIP.");
                var report = zip.GetEntry("report.files/1.html");
                check("duration-literal-package-report-" + name, report is not null, "Report included.");
                if (report is not null) { using var reader = new StreamReader(report.Open()); CheckHtmlText("package-" + name, await reader.ReadToEndAsync(), false); }
            }
            var expanded = Path.Combine(work, name + "-expanded");
            await run("duration-literal-package-expand-" + name, 0, true, ["--archive-extract", package, expanded]);
            var reopened = Path.Combine(work, name + "-reopened.json");
            await run("duration-literal-package-reopen-" + name, 0, true, ["--project-copy", Path.Combine(expanded, "project.json"), reopened]);
            var reloaded = Path.Combine(work, name + "-reloaded.html");
            await run("duration-literal-package-reloaded-report-" + name, 0, true, ["--report-project", reopened, reloaded]); CheckHtmlText(name + "-reloaded", File.ReadAllText(reloaded), false);
            await run("duration-literal-protect-input-" + name, 2, false, ["--report-project", project, input.Left]);
            await run("duration-literal-protect-workspace-" + name, 2, false, ["--report-project", project, project]);
            void CheckHtmlText(string label, string text, bool different)
            {
                check("duration-literal-html-" + label, text.Contains("data-different=\"" + (different ? "true" : "false") + "\"", StringComparison.Ordinal)
                    && text.Contains("physical probe", StringComparison.Ordinal)
                    && System.Text.RegularExpressions.Regex.Matches(text, "<td\\b[^>]*><pre>").Count == 2 * Encoding.UTF8.GetString(input.LeftBytes).Split("\r\n", StringSplitOptions.None)[..^1].Length
                    && (different || text.Contains("data-ignored=\"true\"", StringComparison.Ordinal)), "Original cells and ignored classification.");
            }
        }
        void CheckHtml(string path, bool different)
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            check("duration-literal-html-" + Path.GetFileName(path), text.Contains("data-different=\"" + (different ? "true" : "false") + "\"", StringComparison.Ordinal), "Report key classification.");
        }
    }
    // Native採取の角括弧はArrayConnectionNodeの表示ラベルで、原典parserの実行入力ではない。
    // 公開言語のArray(...)へ明示対応させ、原本case/式/serialized bytesは保持する。
    private static string PublicBridgeExpression(JsonElement item)
    {
        var expression = item.GetProperty("expression").GetString()!;
        var native = item.GetProperty("nativeCase");
        if (native.GetProperty("mode").GetString() != "array") return expression;
        var items = string.Join(", ", native.GetProperty("items").EnumerateArray().Select(value => value.GetString()!));
        var aggregate = native.GetProperty("aggregate").GetString()!;
        var label = "[" + items + "]";
        var actual = "Array(" + items + ")";
        if (aggregate.Length != 0) { label = aggregate + "(" + label + ")"; actual = aggregate + "(" + actual + ")"; }
        if (expression != label) throw new InvalidDataException("Native配列ラベルの対応が不一致です。");
        return actual;
    }

    private static byte[] Utf8(string value) => new UTF8Encoding(false, true).GetBytes(value);
    private static string Hex(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static byte[] Repeat(byte[] key, int count) => Utf8(string.Concat(Enumerable.Repeat(Encoding.UTF8.GetString(key) + "\r\n", count)));
    private static void Within(string path, string root)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("DurationLiteral path escapes root.");
        NoLinks(path);
    }
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("DurationLiteral paths cannot traverse links.");
    }
    private static void RequireExternal(string path, string repository)
    {
        var full = Path.GetFullPath(path); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { repository, Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("DurationLiteral E2E requires external output.");
        }
        if (full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase))) throw new IOException("DurationLiteral output cannot use an artifacts folder.");
        NoLinks(path);
    }
}
