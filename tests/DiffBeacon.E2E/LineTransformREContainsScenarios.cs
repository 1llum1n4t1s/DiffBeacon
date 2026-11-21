using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformREContainsScenarios
{
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, string python)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("REContains fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformREContains");
        var directory = Path.GetFullPath(Path.Combine(fixtures, "line-transform-recontains"));
        foreach (var root in new[] { repository.FullName, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), Environment.GetEnvironmentVariable("CODEX_HOME") })
            if (!string.IsNullOrEmpty(root) && (directory.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
                || directory.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("REContains E2E output must remain outside repository and Codex home.");
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("REContains E2E requires fresh output.");
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false, true);
        await ReadReference("native-reader.json", []);
        var native = File.ReadLines(Path.Combine(reference, "golden.ndjson"))
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
        using var cases = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "cases.json")));
        var left = Path.Combine(directory, "left.txt"); var right = Path.Combine(directory, "right.txt");
        await File.WriteAllTextAsync(left, "probe", utf8); await File.WriteAllTextAsync(right, "probe", utf8);

        var observations = new List<object>();
        var successes = 0; var rejections = 0;
        // 失敗条件: 部分一致/空一致の拒否、ContentのEOL混入/EOF脱落、array余り・否定・型変換、
        // 大文字小文字、定数不正patternの評価時への遅延、動的不正patternの例外化、左右の評価順。
        foreach (var item in cases.RootElement.EnumerateArray())
        {
            if (!item.GetProperty("public").GetBoolean()) continue;
            var input = Convert.FromHexString(item.GetProperty("inputHex").GetString()!);
            await File.WriteAllBytesAsync(left, input); await File.WriteAllBytesAsync(right, input);
            var inputHash = Convert.ToHexString(SHA256.HashData(input));
            var id = item.GetProperty("id").GetString()!;
            var expression = item.GetProperty("expression").GetString()!;
            var row = native[id]; var rejected = row.GetProperty("error").GetString()!.Length > 0;
            var expected = rejected ? "" : utf8.GetString(Convert.FromHexString(row.GetProperty("serializedUtf8Hex").GetString()!));
            var arguments = new List<string> { "--compare", left, right, "--line-transform", "left", expression };
            if (!rejected) arguments.AddRange(["--line-transform", "right", Quote(expected)]);
            var result = await run("recontains-" + id, rejected ? 2 : 0, !rejected, arguments.ToArray());
            var passed = rejected
                ? result.ExitCode == 2 && result.Stdout.Length == 0 && result.Stderr.Length > 0
                : result.ExitCode == 0 && result.Stderr.Length == 0;
            if (passed && !rejected)
            {
                using var json = JsonDocument.Parse(result.Stdout);
                passed = !json.RootElement.GetProperty("different").GetBoolean();
            }
            foreach (var path in new[] { left, right })
                check(result.Name + "-input-" + Path.GetFileName(path), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))) == inputHash, "Original bytes retained.");
            if (rejected) rejections++; else successes++;
            check(result.Name + "-native-derived-public", passed, result.Stderr);
            observations.Add(new { id, expression, expectedSerializedKey = expected, passed,
                actualTypedValueObserved = false, nativeExceptionEquivalent = false,
                exitCode = result.ExitCode, stdout = result.Stdout, stderr = result.Stderr });
        }
        check("recontains-fixed-public-count", successes + rejections == cases.RootElement.EnumerateArray().Count(item => item.GetProperty("public").GetBoolean()) && rejections > 0, $"keys={successes}; rejections={rejections}");
        var observationsPath = Path.Combine(directory, "public-observations.json");
        await File.WriteAllBytesAsync(observationsPath, JsonSerializer.SerializeToUtf8Bytes(observations, new JsonSerializerOptions { WriteIndented = true }));
        await ReadReference("public-reader.json", ["--actual", observationsPath]);

        await File.WriteAllTextAsync(left, "probe", utf8); await File.WriteAllTextAsync(right, "probe", utf8);
        var order = await run("recontains-public-left-to-right", 0, true, ["--compare", left, right,
            "--line-transform", "left", "@noopt LogInfo(\"left\",\"a\") recontains LogInfo(\"right\",\"a\")",
            "--line-transform", "right", "true"]);
        check(order.Name + "-trace", order.Stderr == "info: left a" + Environment.NewLine + "info: right a" + Environment.NewLine, order.Stderr);
        var invalid = await run("recontains-constant-invalid-before-left", 2, false, ["--compare", left, right,
            "--line-transform", "left", "LogInfo(\"left\",\"a\") recontains \"[\""]);
        check(invalid.Name + "-atomic", invalid.Stdout.Length == 0 && !invalid.Stderr.Contains("info: left", StringComparison.Ordinal), invalid.Stderr);
        var dynamicInvalid = await run("recontains-dynamic-invalid-after-left", 0, true, ["--compare", left, right,
            "--line-transform", "left", "LogInfo(\"left\",\"a\") recontains If(LineNumber>0,\"[\",none)",
            "--line-transform", "right", "false"]);
        check(dynamicInvalid.Name + "-trace", dynamicInvalid.Stderr == "info: left a" + Environment.NewLine, dynamicInvalid.Stderr);


        var projectLeft = Path.Combine(directory, "project-left.txt"); var projectRight = Path.Combine(directory, "project-right.txt");
        await File.WriteAllTextAsync(projectLeft, "a", utf8); await File.WriteAllTextAsync(projectRight, "b", utf8);
        const string rule = "Line recontains \"[ab]\"";
        var table = await run("recontains-table", 0, true, ["--table", projectLeft, projectRight, "--line-transform", "all", rule]);
        using (var json = JsonDocument.Parse(table.Stdout))
            check(table.Name + "-classification", !json.RootElement.GetProperty("different").GetBoolean(), table.Stdout);
        var project = Path.Combine(directory, "project.json"); var copy = Path.Combine(directory, "project-copy.json");
        await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new { mode = "Text", leftPath = projectLeft, rightPath = projectRight,
            lineTransformRules = new[] { new { expression = rule, panes = 7 } } }), utf8);
        await run("recontains-project-copy", 0, false, ["--project-copy", project, copy]);
        var html = Path.Combine(directory, "report.html");
        await run("recontains-project-report", 0, false, ["--report-project", copy, html]);
        check("recontains-project-report-ignored", File.ReadAllText(html).Contains("data-ignored=\"true\"", StringComparison.Ordinal), "Saved/restored rule reaches HTML comparison.");
        var invalidProject = Path.Combine(directory, "invalid-project.json");
        await File.WriteAllTextAsync(invalidProject, JsonSerializer.Serialize(new { mode = "Text", leftPath = projectLeft, rightPath = projectRight,
            lineTransformRules = new[] { new { expression = "Line recontains \"[\"", panes = 7 } } }), utf8);
        var sentinel = await File.ReadAllBytesAsync(html);
        var failedReport = await run("recontains-invalid-report-output-preserved", 2, false, ["--report-project", invalidProject, html]);
        check(failedReport.Name + "-atomic", failedReport.Stdout.Length == 0 && (await File.ReadAllBytesAsync(html)).SequenceEqual(sentinel), "Existing output retained after constant pattern rejection.");

        // 同じ復号本文でもNONEとUnicodeの最終非EOLでEOF候補が異なる。
        await File.WriteAllBytesAsync(projectLeft, [0x63, 0x61, 0x66, 0xe9]);
        const string eofRule = "Content recontains \"^$\"";
        foreach (var encoding in new Encoding[] { utf8, new UnicodeEncoding(false, true, true) })
        {
            await File.WriteAllTextAsync(projectRight, "café", encoding);
            var encodingId = encoding.WebName;
            foreach (var mode in new[] { "--compare", "--table" })
            {
                var eof = await run("recontains-codepage-eof-" + encodingId + "-" + mode[2..], 1, true,
                    [mode, projectLeft, projectRight, "--line-transform", "all", eofRule]);
                using var json = JsonDocument.Parse(eof.Stdout);
                check(eof.Name + "-reader-policy", json.RootElement.GetProperty("different").GetBoolean(), eof.Stdout);
            }
            foreach (var mode in new[] { "Text", "Table" })
            {
                var encodedProject = Path.Combine(directory, "codepage-" + encodingId + "-" + mode + ".json");
                var encodedCopy = Path.Combine(directory, "codepage-" + encodingId + "-" + mode + "-copy.json");
                var encodedHtml = Path.Combine(directory, "codepage-" + encodingId + "-" + mode + ".html");
                await File.WriteAllTextAsync(encodedProject, JsonSerializer.Serialize(new { mode, leftPath = projectLeft, rightPath = projectRight,
                    lineTransformRules = new[] { new { expression = eofRule, panes = 7 } } }), utf8);
                await run("recontains-codepage-project-copy-" + encodingId + "-" + mode, 0, false, ["--project-copy", encodedProject, encodedCopy]);
                await run("recontains-codepage-project-report-" + encodingId + "-" + mode, 0, false, ["--report-project", encodedCopy, encodedHtml]);
                check("recontains-codepage-project-html-" + encodingId + "-" + mode, File.ReadAllText(encodedHtml).Contains("data-different=\"true\"", StringComparison.Ordinal), "Snapshot reader policy retained in restored report.");
            }
        }

        await File.WriteAllTextAsync(left, "original", utf8); await File.WriteAllTextAsync(right, "other", utf8);
        var snapshot = await run("recontains-original-content-after-key", 0, true, ["--compare", left, right,
            "--line-transform", "all", "\"changed\"", "--line-transform", "left", "Content recontains \"original\"",
            "--line-transform", "right", "true"]);
        using (var json = JsonDocument.Parse(snapshot.Stdout))
            check(snapshot.Name + "-snapshot", !json.RootElement.GetProperty("different").GetBoolean(), snapshot.Stdout);
        await File.WriteAllTextAsync(left, "\"abc\ndef\",tail\nlast,row", utf8);
        await File.WriteAllTextAsync(right, "\"xyz\nuvw\",tail\nlast,row", utf8);
        var raw = await run("recontains-raw-table-content", 0, true, ["--table", left, right,
            "--line-transform", "left", "Content recontains \"^def\"", "--line-transform", "right", "true"]);
        using (var json = JsonDocument.Parse(raw.Stdout))
            check(raw.Name + "-physical-body", !json.RootElement.GetProperty("different").GetBoolean(), raw.Stdout);
        // 動的不正patternはfalse、timeoutと共有予算超過は出力前の失敗にする。
        await File.WriteAllTextAsync(left, new string('a', 20000) + "!", utf8);
        await File.WriteAllTextAsync(right, "probe", utf8);
        var timeoutResult = await run("recontains-timeout-not-false", 2, false, ["--compare", left, right,
            "--line-transform", "left", "Content recontains If(LineNumber>0,\"^(a+)+$\",none)"]);
        check(timeoutResult.Name + "-atomic", timeoutResult.Stdout.Length == 0 && timeoutResult.Stderr.Length > 0, timeoutResult.Stderr);
        await File.WriteAllTextAsync(left, string.Join('\n', Enumerable.Repeat(new string('x', 512), 1024)), utf8);
        var budget = await run("recontains-shared-budget-not-false", 2, false, ["--compare", left, right,
            "--line-transform", "left", "Content recontains \"^never$\""]);
        check(budget.Name + "-atomic", budget.Stdout.Length == 0 && budget.Stderr.Length > 0, budget.Stderr);

        async Task ReadReference(string name, string[] arguments)
        {
            var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-B", Path.Combine(reference, "verify.py") }.Concat(arguments)) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("REContains reader did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
            var text = await stdout; var error = await stderr;
            await File.WriteAllTextAsync(Path.Combine(directory, name), text, utf8);
            check("recontains-" + name, process.ExitCode == 0 && error.Length == 0, text + error);
            if (process.ExitCode != 0) throw new IOException("REContains reader failed: " + error);
        }
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
