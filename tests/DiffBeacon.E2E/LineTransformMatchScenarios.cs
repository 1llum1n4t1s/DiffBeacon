using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformMatchScenarios
{
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, string python)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Match fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformMatches");
        var directory = Path.GetFullPath(Path.Combine(fixtures, "line-transform-matches"));
        foreach (var root in new[] { repository.FullName, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), Environment.GetEnvironmentVariable("CODEX_HOME") })
            if (!string.IsNullOrEmpty(root) && (directory.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
                || directory.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Match E2E output must remain outside repository and Codex home.");
        if (Directory.Exists(directory)) throw new IOException("Match E2E requires fresh output.");
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false, true);
        var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-B"); start.ArgumentList.Add(Path.Combine(reference, "verify.py"));
        using (var process = Process.Start(start) ?? throw new IOException("Match reader did not start."))
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
            var text = await stdout; var error = await stderr;
            await File.WriteAllTextAsync(Path.Combine(directory, "native-reader.json"), text, utf8);
            check("match-native-independent-reader", process.ExitCode == 0 && error.Length == 0, text + error);
            if (process.ExitCode != 0) throw new IOException("Match native reader failed: " + error);
        }
        using var plan = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "public-case-plan.json")));
        var observations = new List<object>();
        var inputHashes = new Dictionary<string, string>();
        int comparable = 0, rejected = 0, skipped = 0;
        foreach (var item in plan.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var expression = item.GetProperty("expression").GetString();
            if (expression is null) { skipped++; continue; }
            var count = item.GetProperty("sourceLineCount").GetInt32();
            var left = Path.Combine(directory, id + "-left.txt"); var right = Path.Combine(directory, id + "-right.txt");
            var bytes = utf8.GetBytes(string.Join("\r\n", Enumerable.Range(0, count).Select(i => "source-" + i)));
            await File.WriteAllBytesAsync(left, bytes); await File.WriteAllBytesAsync(right, bytes);
            inputHashes[left] = inputHashes[right] = Convert.ToHexString(SHA256.HashData(bytes));
            var expectedError = item.GetProperty("expectedOutcome").GetString() == "runtime-rejection";
            var expectedKeys = item.GetProperty("expectedSerializedKeys").EnumerateArray().Select(k => k.GetString()!).ToArray();
            var expectedExpression = expectedError ? "Line" : "@noopt Choose(LineNumber-1," + string.Join(",", expectedKeys.Select(k => Quote(utf8.GetString(Convert.FromHexString(k))))) + ")";
            var result = await run("match-bridge-" + id, expectedError ? 2 : 0, !expectedError,
                ["--compare", left, right, "--line-transform", "left", expression, "--line-transform", "right", expectedExpression]);
            bool passed;
            if (expectedError) { rejected++; passed = result.ExitCode == 2 && result.Stdout.Length == 0 && !string.IsNullOrWhiteSpace(result.Stderr); }
            else
            {
                comparable++;
                using var json = JsonDocument.Parse(result.Stdout);
                passed = result.ExitCode == 0 && !json.RootElement.GetProperty("different").GetBoolean();
            }
            check(result.Name + "-public", passed, "Derived all-row serialized-key comparison; native type/AST/cache and exception class are not public observations. " + result.Stderr);
            observations.Add(new { id, expression, expectedExpression, expectedSerializedKeys = expectedKeys, expectedError, passed,
                actualTypedValueObserved = false, contextEquivalent = false, nativeExceptionEquivalent = false, exitCode = result.ExitCode, stdout = result.Stdout, stderr = result.Stderr });
        }
        check("match-public-counts", comparable == 91 && rejected == 11 && skipped == 30, $"serialized={comparable}; runtime={rejected}; native-only={skipped}");
        var leftProbe = Path.Combine(directory, "engineering-left.txt"); var rightProbe = Path.Combine(directory, "engineering-right.txt");
        await File.WriteAllTextAsync(leftProbe, "a\r\nb\r\nc", utf8); await File.WriteAllTextAsync(rightProbe, "x\r\ny\r\nz", utf8);
        inputHashes[leftProbe] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(leftProbe)));
        inputHashes[rightProbe] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(rightProbe)));
        foreach (var function in new[] { "MatchContext", "MatchBlockCount", "MatchBlockNumber", "MatchBlockSize", "MatchBlockOffset" })
        {
            var arity = function == "MatchContext" ? 3 : 1;
            foreach (var n in new[] { 0, arity - 1, arity + 1 }.Distinct())
                await Reject("arity-" + function + "-" + n, "@noopt " + function + "(" + string.Join(",", Enumerable.Repeat("true", n)) + ")");
        }
        await Compare("per-pane", false, ["--line-transform", "all", "MatchBlockCount(Line==\"a\")"], 1);
        await Compare("multiline-provenance", false, ["--line-transform", "left", "MatchBlockCount(Line==\"a\")", "--line-transform", "right", "\"1\""], 0);
        await Compare("table-match", true, ["--line-transform", "all", "MatchBlockCount(LineNumber==1)"], 0);
        await Compare("separate-ast", false, ["--line-transform", "all", "MatchBlockCount(LineNumber==1)+MatchBlockCount(LineNumber==3)"], 0);
        await Reject("eager-after-invalid-before", "@noopt MatchContext(true,none,1/(LineNumber-LineNumber))");
        // 同じmaskの規則は行ごとに適用し、後続Matchは部分更新済みのproviderを走査する。
        await Compare("same-mask-group", false, ["--line-transform", "left", "MatchBlockCount(Line==\"a\")", "--line-transform", "left", "MatchBlockSize(Line==\"1\")", "--line-transform", "right", "\"1\""], 1);
        await Compare("mask-boundary", false, ["--line-transform", "left", "MatchBlockCount(Line==\"a\")", "--line-transform", "all", "MatchBlockSize(Line==\"1\")", "--line-transform", "right", "\"3\""], 0);
        var project = Path.Combine(directory, "project.json"); var copy = Path.Combine(directory, "project-copy.json");
        const string projectExpression = "@noopt MatchBlockCount(LineNumber==1)";
        await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new { mode = "Text", leftPath = leftProbe, rightPath = rightProbe,
            lineTransformRules = new[] { new { expression = projectExpression, panes = 7 } } }), utf8);
        await run("match-project-copy", 0, true, ["--project-copy", project, copy]);
        using (var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(copy)))
            check("match-project-rule-roundtrip", saved.RootElement.GetProperty("lineTransformRules")[0].GetProperty("expression").GetString() == projectExpression, "Exact expression persists.");
        var html = Path.Combine(directory, "project.html");
        await run("match-project-report", 0, true, ["--report-project", copy, html]);
        check("match-report-source-retained", File.ReadAllText(html).Contains("data-different=\"false\"", StringComparison.Ordinal)
            && File.ReadAllText(html).Contains("data-ignored=\"true\"", StringComparison.Ordinal), "Report retains source differences as ignored by transformation.");
        var budgetLeft = Path.Combine(directory, "budget-left.txt"); var budgetRight = Path.Combine(directory, "budget-right.txt");
        var budgetText = new string('a', 262144) + "\r\n" + new string('a', 262144) + "\r\n";
        await File.WriteAllTextAsync(budgetLeft, budgetText, utf8); await File.WriteAllTextAsync(budgetRight, budgetText, utf8);
        var budgetProject = Path.Combine(directory, "budget-project.json");
        await File.WriteAllTextAsync(budgetProject, JsonSerializer.Serialize(new { mode = "Text", leftPath = budgetLeft, rightPath = budgetRight,
            lineTransformRules = Enumerable.Repeat(new { expression = "If(MatchBlockCount(true)==1,Line,Line)", panes = 1 }, 256).ToArray() }), utf8);
        var sentinel = utf8.GetBytes("existing match report\r\n"); var existing = Path.Combine(directory, "existing.html");
        await File.WriteAllBytesAsync(existing, sentinel);
        var budgetResult = await run("match-shared-output-budget", 2, false, ["--report-project", budgetProject, existing]);
        check("match-shared-output-budget-atomic", budgetResult.ExitCode == 2 && budgetResult.Stdout.Length == 0
            && budgetResult.Stderr.Contains("行変換の共有処理量または生成文字数", StringComparison.Ordinal)
            && (await File.ReadAllBytesAsync(existing)).SequenceEqual(sentinel), "Shared rule budget and existing output protection.");
        foreach (var pair in inputHashes)
            check("match-source-preserved-" + Path.GetFileName(pair.Key), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(pair.Key))) == pair.Value, "Input bytes retained.");
        var observationsPath = Path.Combine(directory, "public-observations.json");
        await File.WriteAllBytesAsync(observationsPath, JsonSerializer.SerializeToUtf8Bytes(observations, new JsonSerializerOptions { WriteIndented = true }));
        // 独立readerはCLIのdifferent=falseを全行key比較の間接観測として扱い、actual key取得や型観測とは数えない。
        var bridgeStart = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-B", Path.Combine(reference, "verify-public.py"), "--actual", observationsPath }) bridgeStart.ArgumentList.Add(arg);
        using var bridge = Process.Start(bridgeStart) ?? throw new IOException("Public reader did not start.");
        var bridgeOut = bridge.StandardOutput.ReadToEndAsync(); var bridgeErr = bridge.StandardError.ReadToEndAsync();
        using var bridgeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await bridge.WaitForExitAsync(bridgeTimeout.Token); } catch { bridge.Kill(true); await bridge.WaitForExitAsync(); throw; }
        var bridgeText = await bridgeOut; var bridgeError = await bridgeErr;
        await File.WriteAllTextAsync(Path.Combine(directory, "public-reader.json"), bridgeText, utf8);
        check("match-public-independent-reader", bridge.ExitCode == 0 && bridgeError.Length == 0, bridgeText + bridgeError);

        async Task Reject(string name, string expression)
        {
            var result = await run("match-reject-" + name, 2, false, ["--compare", leftProbe, rightProbe, "--line-transform", "left", expression]);
            check(result.Name + "-atomic", result.ExitCode == 2 && result.Stdout.Length == 0 && !string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        }
        async Task Compare(string name, bool table, string[] rules, int exitCode)
        {
            var arguments = new List<string> { table ? "--table" : "--compare", leftProbe, rightProbe }; arguments.AddRange(rules);
            var result = await run("match-engineering-" + name, exitCode, true, arguments.ToArray());
            using var json = JsonDocument.Parse(result.Stdout);
            check(result.Name + "-classification", json.RootElement.GetProperty("different").GetBoolean() == (exitCode == 1), result.Stdout);
        }
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
