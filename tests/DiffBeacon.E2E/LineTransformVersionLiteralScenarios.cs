using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformVersionLiteralScenarios
{
    private sealed record Inputs(string Left, string Right, byte[] LeftBytes, byte[] RightBytes);
    private sealed record Rule(string Expression, int Panes);
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Version fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformVersionLiterals");
        var work = Path.GetFullPath(Path.Combine(fixtures, "line-transform-version-literals"));
        RequireExternal(work, repository.FullName);
        if (Directory.Exists(work) || File.Exists(work)) throw new IOException("Fresh Version E2E output required.");
        Directory.CreateDirectory(work);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var sources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var expected = new List<object>(); var engineeringExpected = new List<object>();
        using var immutable = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "immutable-stages.json")));
        foreach (var stage in immutable.RootElement.EnumerateArray())
        {
            var stageRoot = Path.GetFullPath(Path.Combine(reference, stage.GetProperty("relativePath").GetString()!)); Within(stageRoot, reference);
            var manifestPath = Path.Combine(stageRoot, "fixed-manifest.json");
            var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
            if (Hex(manifestBytes) != stage.GetProperty("fixedManifestSha256").GetString()) throw new IOException("Version stage manifest SHA differs.");
            using var manifest = JsonDocument.Parse(manifestBytes);
            foreach (var file in manifest.RootElement.EnumerateArray())
            {
                var path = Path.GetFullPath(Path.Combine(stageRoot, file.GetProperty("path").GetString()!)); Within(path, stageRoot);
                var bytes = await File.ReadAllBytesAsync(path);
                if (bytes.LongLength != file.GetProperty("size").GetInt64() || Hex(bytes) != file.GetProperty("sha256").GetString())
                    throw new IOException("Frozen Version source differs: " + path);
            }
        }
        using var plan = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "public-case-plan.json")));
        if (plan.RootElement.GetProperty("schema").GetString() != "diffbeacon.version-public-plan.v1") throw new IOException("Version plan schema differs.");
        var cases = plan.RootElement.GetProperty("records").EnumerateArray().Select(x => x.Clone()).ToArray();
        var success = cases.Where(x => x.GetProperty("classification").GetString() == "public-success-proposal").ToArray();
        var rejected = cases.Where(x => x.GetProperty("classification").GetString() == "constructor-only-nul-rejected").ToArray();
        if (cases.Length != 156 || success.Length != 154 || rejected.Length != 2
            || cases.Select(x => x.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count() != 156)
            throw new IOException("Version154+NUL2 count/ID differs.");
        var bridgeCount = 0;
        foreach (var test in success)
        {
            var id = test.GetProperty("id").GetString()!;
            var expression = new UTF8Encoding(false, true).GetString(Convert.FromHexString(test.GetProperty("expressionUtf8Hex").GetString()!));
            var key = Convert.FromHexString(test.GetProperty("serializedByteHex").GetString()!);
            if (key.Any(x => x is 0 or 10 or 13)) throw new IOException("Unplanned physical key boundary.");
            // Windows file名には ":" を使わず、case IDはreceipt provenance内でそのまま保持。
            var name = "bridge-" + bridgeCount.ToString("D3");
            var input = await Create(name, Utf8("physical probe\r\n"), Repeat(key, 1));
            var observed = await Compare(name, input, false, [new(expression, 1)], false, test.Clone());
            observed &= await Compare("table-" + name, input, true, [new(expression, 1)], false, test.Clone());
            check("version-literal-full-bridge-" + bridgeCount, observed, id + "; fixed native serialized bytes; public typed value unobserved.");
            if (bridgeCount == 0)
            {
                await Compare("raw-negative-control", input, false, [], true, test.Clone());
                await Projects(name, input, expression);
                await Scopes(expression, key);
            }
            bridgeCount++;
        }
        var simple = await Create("rejections", Utf8("physical probe\r\n"), Utf8("physical probe\r\n"));
        using var probePlan = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "probes-case-plan.json")));
        var proposalIndex = 0;
        foreach (var proposal in probePlan.RootElement.GetProperty("records").EnumerateArray())
        {
            var expression = new UTF8Encoding(false, true).GetString(Convert.FromHexString(proposal.GetProperty("expressionUtf8Hex").GetString()!));
            var project = Path.Combine(work, "proposal-" + proposalIndex + ".json");
            await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Text", leftPath = simple.Left,
                rightPath = simple.Right, lineTransformRules = new[] { new Rule(expression, 1) } }, options));
            var target = Path.Combine(work, "proposal-" + proposalIndex + "-existing.json");
            var sentinel = Utf8("existing Version proposal output\r\n");
            var shouldCompile = proposal.GetProperty("expectCompile").GetBoolean();
            if (!shouldCompile) await WriteSource(target, sentinel);
            var result = await run("version-literal-compile-proposal-" + proposalIndex, shouldCompile ? 0 : 2, false, ["--project-copy", project, target]);
            if (!shouldCompile)
                check(result.Name + "-sentinel-preserved", File.ReadAllBytes(target).SequenceEqual(sentinel), "Invalid proposal preserves existing project output.");
            else
            {
                using var copiedProposal = JsonDocument.Parse(await File.ReadAllBytesAsync(target));
                var copiedProposalRules = copiedProposal.RootElement.GetProperty("lineTransformRules");
                check(result.Name + "-expression-retained", copiedProposalRules.GetArrayLength() == 1
                    && copiedProposalRules[0].GetProperty("expression").GetString() == expression
                    && copiedProposalRules[0].GetProperty("panes").GetInt32() == 1,
                    "Exact imported expression, pane mask and rule count.");
            }
            await Evidence(result, result.Arguments, simple, new { staticProposal = proposal.Clone(),
                publicCompileExecuted = true, executed = false, publicTypedObserved = false,
                existingOutputShaBefore = shouldCompile ? null : Hex(sentinel),
                existingOutputShaAfter = shouldCompile ? null : Hex(File.ReadAllBytes(target)),
                nativeFullParserExecuted = false, nativeLexerExecuted = false, actualNativelexerExecuted = false, nativeTypedContextQualified = false, nativeFullAstQualified = false });
            proposalIndex++;
        }
        check("version-literal-compile-proposal-count", proposalIndex == 12, "12 separate compile-only static proposals; no Native lexer/full parser execution.");
        foreach (var test in rejected)
        {
            var expression = new UTF8Encoding(false, true).GetString(Convert.FromHexString(test.GetProperty("expressionUtf8Hex").GetString()!));
            await Reject("nul-" + (test.GetProperty("prefix").GetString() == "v" ? "lower" : "upper"), simple, [new(expression, 1)], true);
        }
        var engineering = new (string Id, string Expression, string? Expected)[]
        {
            ("array-constructor", "Array(v\"0.0.0.1\",v\"0.0.0.2\")", "[1, 2]"),
            ("array-noopt", "@noopt Array(v\"0.0.0.1\",v\"0.0.0.2\")", "[1, 2]"),
            ("abs-constructor", "Abs(v\"0.0.0.-1\")", "1"),
            ("abs-noopt", "@noopt Abs(v\"0.0.0.-1\")", "1"),
            ("at-constructor", "At(Array(10,20),v\"0.0.0.1\")", "20"),
            ("at-noopt", "@noopt At(Array(10,20),v\"0.0.0.1\")", "20"),
            ("substr-constructor", "Substr(\"abc\",v\"0.0.0.1\",v\"0.0.0.1\")", "b"),
            ("substr-noopt", "@noopt Substr(\"abc\",v\"0.0.0.1\",v\"0.0.0.1\")", "b"),
            ("strfind-constructor", "StrFind(\"abc\",\"b\",v\"0\")", "1"),
            ("strfind-noopt", "@noopt StrFind(\"abc\",\"b\",v\"0\")", "1"),
            ("signbit-binary", "v\"32768\" + 0", "-9223372036854775808"),
            ("ignored-doubled-quote", "v\"0.0.0.1\"\"tail\"", "1"),
            ("ignored-backslash", "v\"0.0.0.1\\tail\"", "1"),
            ("ignored-newline", "v\"0.0.0.1\nTail\"", "1"),
            ("partial-first", "v\"1.tail\"", "281474976710656"),
            ("partial-second", "v\"0.1.tail\"", "4294967296"),
            ("partial-third", "v\"0.0.1.tail\"", "65536"),
            ("partial-fourth", "v\"0.0.0.1tail\"", "1"),
            ("plus", "v\"+0.+0.+0.+1\"", "1"),
            ("revision-min", "v\"0.0.0.-2147483648\"", "-2147483648"),
            ("major-overflow", "v\"65536\"", null),
            ("negative-major", "v\"-1\"", null),
            ("negative-minor", "v\"0.-1\"", null),
            ("minor-int32-overflow", "v\"0.2147483648\"", null),
            ("build-int32-overflow", "v\"0.0.2147483648\"", null),
            ("revision-int32-overflow", "v\"0.0.0.2147483648\"", null),
            ("sum-overflow", "v\"32767.2147483647.2147483647.2147483647\"", null),
            ("binary-overflow", "v\"32768\" - 1", null),
            ("unary-overflow", "-v\"32768\"", null),
        };
        foreach (var item in engineering)
        {
            if (item.Expected is null) await Reject("engineering-" + item.Id, simple, [new(item.Expression, 1)], true);
            else
            {
                var input = await Create("engineering-" + item.Id, Utf8("physical probe\r\n"), Repeat(Utf8(item.Expected), 1));
                await Compare("engineering-" + item.Id, input, false, [new(item.Expression, 1)], false,
                    new { engineering = true, item.Id, item.Expression, expectedSerializedText = item.Expected, publicTypedObserved = false, nativeFullParserExecuted = false });
            }
        }
        // 256 accepted rulesと257 compile rejectionを区別。共有累積予算は別入力で拒否を観測。
        var rule = new Rule("If(v\"0.0.0.1\"==1,Line,Line)", 1);
        var large = Utf8(new string('a', 262144) + "\r\n" + new string('a', 262144) + "\r\n");
        var budgetInput = await Create("shared-budget", large, large);
        await Reject("shared-budget-256", budgetInput, Enumerable.Repeat(rule, 256).ToArray(), false);
        await Reject("rules-257", simple, Enumerable.Repeat(rule, 257).ToArray(), true);
        check("version-literal-full-selection", bridgeCount == 154, "All77 Native source cases * v/V, no sampled subset.");
        foreach (var source in sources)
            check("version-literal-source-sha-" + Path.GetFileName(source.Key),
                File.Exists(source.Key) && Hex(await File.ReadAllBytesAsync(source.Key)) == Hex(source.Value), "All original input/project/sentinel retained.");
        await WriteSource(Path.Combine(work, "expected.json"), JsonSerializer.SerializeToUtf8Bytes(new {
            nativeSafe = 77, publicBridges = bridgeCount, nulRejections = rejected.Length,
            publicTypedObserved = false, nativeFullParserExecuted = false, nativeTypedContextQualified = false, nativeFullAstQualified = false,
            cancellation = "CLI run delegate has no cancellation injection; App cancellation checks required.", cases = expected }, options));
        await WriteSource(Path.Combine(work, "engineering-observations.json"), JsonSerializer.SerializeToUtf8Bytes(new {
            engineering = true, publicTypedObserved = false, nativeFullParserExecuted = false, cases = engineeringExpected }, options));

        async Task Scopes(string expression, byte[] key)
        {
            var input = await Create("scopes", Utf8("id,value\r\nA,\"physical,probe\"\r\n"), Utf8("id,value\r\nA,\"right,raw\"\r\n"));
            var basePath = Path.Combine(work, "scope-base.csv"); await WriteSource(basePath, Utf8("id,value\r\nA,\"base,raw\"\r\n"));
            foreach (var mask in new[] { 1, 2, 4, 3, 5, 6, 7 })
            {
                var project = Path.Combine(work, "scope-" + mask + ".json");
                var scoped = "If(LineNumber=1,Line," + expression + ")";
                await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Table", leftPath = input.Left,
                    basePath, rightPath = input.Right, tableDelimiter = ',', lineTransformRules = new[] { new Rule(scoped, mask) } }, options));
                var html = Path.Combine(work, "scope-" + mask + ".html");
                var report = await run("version-literal-scope-" + mask, 0, true, ["--report-project", project, html]);
                CheckHtml(html, mask != 7);
                check(report.Name + "-raw", File.ReadAllText(html).Contains("physical,probe", StringComparison.Ordinal)
                    && File.ReadAllText(html).Contains("right,raw", StringComparison.Ordinal), "Original CSV cells retained.");
                await Evidence(report, report.Arguments, input, new { mask, expression, expectedSerializedByteHex = Convert.ToHexString(key),
                    nativeFullParserExecuted = false, publicTypedObserved = false });
                var copy = Path.Combine(work, "scope-" + mask + "-copy.json");
                await run("version-literal-scope-copy-" + mask, 0, true, ["--project-copy", project, copy]);
                var restoredHtml = Path.Combine(work, "scope-" + mask + "-restored.html");
                await run("version-literal-scope-restored-" + mask, 0, true, ["--report-project", copy, restoredHtml]);
                CheckHtml(restoredHtml, mask != 7);
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
                result = await run("version-literal-" + name, 0, true, ["--report-project", project, html]);
                CheckHtml(html, different);
            }
            else
            {
                var expectedExit = different ? 1 : 0;
                result = await run("version-literal-" + name, expectedExit, true, args.ToArray());
                using var parsed = JsonDocument.Parse(result.Stdout);
                check(result.Name + "-classification", result.ExitCode == expectedExit && parsed.RootElement.GetProperty("different").GetBoolean() == different, "Expected serialized-key comparison and difference exit code.");
                if (table)
                {
                    var tableSourceRows = parsed.RootElement.GetProperty("mapping").EnumerateArray().ToArray();
                    foreach (var (tableSide, tableBytes) in new[] { ("left", input.LeftBytes), ("right", input.RightBytes) })
                    {
                        var tableOriginalLines = Encoding.UTF8.GetString(tableBytes).Split("\r\n", StringSplitOptions.None)[..^1];
                        check(result.Name + "-original-mapping-" + tableSide,
                            tableSourceRows.Where(row => row.GetProperty(tableSide).ValueKind != JsonValueKind.Null)
                                .Select(row => row.GetProperty(tableSide).GetInt32()).SequenceEqual(Enumerable.Range(1, tableOriginalLines.Length))
                            && parsed.RootElement.GetProperty("rows").GetInt32() == tableOriginalLines.Length
                            && parsed.RootElement.GetProperty("cols").GetInt32() == 1,
                            "Every original table row and source index retained.");
                    }
                    // CLIの表JSONはセル本文を公開しないため、同じ入力と規則の実HTML経路でも全セルを照合する。
                    var tableRawProject = Path.Combine(work, name + "-raw-table-project.json");
                    await WriteSource(tableRawProject, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Table", leftPath = input.Left,
                        rightPath = input.Right, tableDelimiter = ',', lineTransformRules = rules }, options));
                    var tableRawHtml = Path.Combine(work, name + "-raw-table-report.html");
                    var tableRawResult = await run("version-literal-table-raw-" + name, 0, true, ["--report-project", tableRawProject, tableRawHtml]);
                    var tableActualCells = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(tableRawHtml), "<td\\b[^>]*><pre>(.*?)</pre></td>", System.Text.RegularExpressions.RegexOptions.Singleline)
                        .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();
                    var tableExpectedCells = Encoding.UTF8.GetString(input.LeftBytes).Split("\r\n", StringSplitOptions.None)[..^1]
                        .Zip(Encoding.UTF8.GetString(input.RightBytes).Split("\r\n", StringSplitOptions.None)[..^1], (leftCell, rightCell) => new[] { leftCell, rightCell }).SelectMany(cells => cells).ToArray();
                    check(tableRawResult.Name + "-all-original-cells", tableActualCells.SequenceEqual(tableExpectedCells), "All raw table cells retained byte-for-byte after HTML decoding.");
                    CheckHtml(tableRawHtml, different);
                    await Evidence(tableRawResult, tableRawResult.Arguments, input, new { different, provenance, tableOriginalCells = true });
                }
                else
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
                qualification = "engineering CLI serialized observation; not original Native parser/directive/typed compatibility; formal154 unchanged",
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
            var html = Path.Combine(work, name + "-existing.html"); var sentinel = Utf8("existing version-literal output\r\n");
            await WriteSource(html, sentinel);
            var reportArgs = new[] { "--report-project", project, html };
            var result = await run("version-literal-reject-report-" + name, 2, false, reportArgs);
            check(result.Name + "-atomic", result.ExitCode == 2 && string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), "Whole public comparison abort; no native error enum equivalence.");
            check(result.Name + "-output", Hex(await File.ReadAllBytesAsync(html)) == Hex(sentinel), "Existing output unchanged.");
            if (name == "shared-budget-256") check(result.Name + "-budget-reason", result.Stderr.Contains("行変換の共有処理量または生成文字数", StringComparison.Ordinal), "Shared cumulative budget across 256 rules.");
            await Evidence(result, reportArgs, input, new { exitCode = 2, stdoutEmpty = true, name });
            if (importReject)
            {
                var copy = Path.Combine(work, name + "-existing.json"); await WriteSource(copy, sentinel);
                await run("version-literal-reject-copy-" + name, 2, false, ["--project-copy", project, copy]);
                check("version-literal-reject-copy-" + name + "-preserved", Hex(await File.ReadAllBytesAsync(copy)) == Hex(sentinel), "Import rejection preserves existing JSON.");
            }
            if (rules.Length == 1 || rules.Length == 257)
            {
                var args = new List<string> { "--compare", input.Left, input.Right };
                foreach (var rule in rules) args.AddRange(["--line-transform", "left", rule.Expression]);
                var cli = await run("version-literal-reject-cli-" + name, 2, false, args.ToArray());
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
            await run("version-literal-project-copy-" + name, 0, true, ["--project-copy", project, copy]);
            await run("version-literal-project-recopy-" + name, 0, true, ["--project-copy", copy, again]);
            check("version-literal-project-stable-" + name, File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(again)), "Stable project JSON.");
            using (var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(copy)))
                check("version-literal-project-rule-" + name, saved.RootElement.GetProperty("lineTransformRules")[0].GetProperty("expression").GetString() == expression
                    && saved.RootElement.GetProperty("lineTransformRules")[0].GetProperty("panes").GetInt32() == 1
                    && saved.RootElement.GetProperty("leftReadOnly").GetBoolean() && saved.RootElement.GetProperty("rightReadOnly").GetBoolean(), "Exact expression/mask/read-only state.");
            using (var originalProject = JsonDocument.Parse(await File.ReadAllBytesAsync(project)))
            {
                var workspace = Path.Combine(work, name + "-workspace.json");
                await WriteSource(workspace, JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1,
                    entries = new[] { originalProject.RootElement.Clone() }, activeEntryIndex = 0 }, options));
                var workspaceCopy = Path.Combine(work, name + "-workspace-copy.json");
                await run("version-literal-workspace-copy-" + name, 0, true, ["--project-copy", workspace, workspaceCopy]);
                var workspaceHtml = Path.Combine(work, name + "-workspace.html");
                await run("version-literal-workspace-report-" + name, 0, true, ["--report-project", workspaceCopy, workspaceHtml]);
                CheckHtmlText(name + "-workspace", File.ReadAllText(workspaceHtml), false);
            }
            var html = Path.Combine(work, name + "-report.html");
            await run("version-literal-project-report-" + name, 0, true, ["--report-project", project, html]); CheckHtmlText(name + "-normal", File.ReadAllText(html), false);
            var overridden = Path.Combine(work, name + "-override.html");
            await run("version-literal-project-override-" + name, 0, true, ["--report-project", project, overridden, "--line-transform", "all", "Line"]); CheckHtmlText(name + "-override", File.ReadAllText(overridden), true);
            var package = Path.Combine(work, name + "-package.zip");
            await run("version-literal-package-" + name, 0, true, ["--package-project", project, package, "--report"]);
            using (var zip = ZipFile.OpenRead(package))
            {
                var assets = new List<byte[]>();
                foreach (var entry in zip.Entries.Where(entry => entry.Name.Length > 0))
                { using var stream = entry.Open(); using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer); assets.Add(buffer.ToArray()); }
                check("version-literal-package-assets-" + name, assets.Any(bytes => bytes.SequenceEqual(input.LeftBytes)) && assets.Any(bytes => bytes.SequenceEqual(input.RightBytes)), "Original assets independently read from ZIP.");
                var report = zip.GetEntry("report.files/1.html");
                check("version-literal-package-report-" + name, report is not null, "Report included.");
                if (report is not null) { using var reader = new StreamReader(report.Open()); CheckHtmlText("package-" + name, await reader.ReadToEndAsync(), false); }
            }
            var expanded = Path.Combine(work, name + "-expanded");
            await run("version-literal-package-expand-" + name, 0, true, ["--archive-extract", package, expanded]);
            var reopened = Path.Combine(work, name + "-reopened.json");
            await run("version-literal-package-reopen-" + name, 0, true, ["--project-copy", Path.Combine(expanded, "project.json"), reopened]);
            var reloaded = Path.Combine(work, name + "-reloaded.html");
            await run("version-literal-package-reloaded-report-" + name, 0, true, ["--report-project", reopened, reloaded]); CheckHtmlText(name + "-reloaded", File.ReadAllText(reloaded), false);
            await run("version-literal-protect-input-" + name, 2, false, ["--report-project", project, input.Left]);
            await run("version-literal-protect-workspace-" + name, 2, false, ["--report-project", project, project]);
            void CheckHtmlText(string label, string text, bool different)
            {
                check("version-literal-html-" + label, text.Contains("data-different=\"" + (different ? "true" : "false") + "\"", StringComparison.Ordinal)
                    && text.Contains("physical probe", StringComparison.Ordinal)
                    && System.Text.RegularExpressions.Regex.Matches(text, "<td\\b[^>]*><pre>").Count == 2 * Encoding.UTF8.GetString(input.LeftBytes).Split("\r\n", StringSplitOptions.None)[..^1].Length
                    && (different || text.Contains("data-ignored=\"true\"", StringComparison.Ordinal)), "Original cells and ignored classification.");
            }
        }
        void CheckHtml(string path, bool different)
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            check("version-literal-html-" + Path.GetFileName(path), text.Contains("data-different=\"" + (different ? "true" : "false") + "\"", StringComparison.Ordinal), "Report key classification.");
        }
    }
    private static byte[] Utf8(string value) => new UTF8Encoding(false, true).GetBytes(value);
    private static string Hex(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static byte[] Repeat(byte[] key, int count) => Utf8(string.Concat(Enumerable.Repeat(Encoding.UTF8.GetString(key) + "\r\n", count)));
    private static void Within(string path, string root)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("VersionLiteral path escapes root.");
        NoLinks(path);
    }
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("VersionLiteral paths cannot traverse links.");
    }
    private static void RequireExternal(string path, string repository)
    {
        var full = Path.GetFullPath(path); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { repository, Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("VersionLiteral E2E requires external output.");
        }
        if (full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase))) throw new IOException("VersionLiteral output cannot use an artifacts folder.");
        NoLinks(path);
    }
}
