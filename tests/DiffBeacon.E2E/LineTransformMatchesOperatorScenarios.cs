using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformMatchesOperatorScenarios
{
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, string python)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Matches operator fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformMatchesOperator");
        var directory = Path.GetFullPath(Path.Combine(fixtures, "line-transform-matches-operator"));
        foreach (var root in new[] { repository.FullName, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), Environment.GetEnvironmentVariable("CODEX_HOME") })
            if (!string.IsNullOrEmpty(root) && (directory.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
                || directory.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Matches operator E2E output must remain outside repository and Codex home.");
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Matches operator E2E requires fresh output.");
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false, true);
        await ReadReference("native-reader.json", []);
        var native = File.ReadLines(Path.Combine(reference, "golden.ndjson"))
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
        using var cases = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "cases.json")));
        var left = Path.Combine(directory, "left.txt"); var right = Path.Combine(directory, "right.txt");
        await File.WriteAllTextAsync(left, "probe", utf8); await File.WriteAllTextAsync(right, "probe", utf8);
        var inputHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(left)));
        var observations = new List<object>();
        var successes = 0; var rejections = 0;
        // 失敗条件: 部分一致/空一致の誤採用、alternationの終端backtrack、array余り・否定・型変換、
        // 大文字小文字、定数不正patternの評価時への遅延、動的不正patternの例外化、左右の評価順。
        foreach (var item in cases.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var expression = item.GetProperty("expression").GetString()!;
            var row = native[id]; var rejected = row.GetProperty("error").GetString()!.Length > 0;
            var expected = rejected ? "" : utf8.GetString(Convert.FromHexString(row.GetProperty("serializedUtf8Hex").GetString()!));
            var arguments = new List<string> { "--compare", left, right, "--line-transform", "left", expression };
            if (!rejected) arguments.AddRange(["--line-transform", "right", Quote(expected)]);
            var result = await run("matches-operator-" + id, rejected ? 2 : 0, !rejected, arguments.ToArray());
            var passed = rejected
                ? result.ExitCode == 2 && result.Stdout.Length == 0 && result.Stderr.Length > 0
                : result.ExitCode == 0 && result.Stderr.Length == 0;
            if (passed && !rejected)
            {
                using var json = JsonDocument.Parse(result.Stdout);
                passed = !json.RootElement.GetProperty("different").GetBoolean();
            }
            if (rejected) rejections++; else successes++;
            check(result.Name + "-native-derived-public", passed, result.Stderr);
            observations.Add(new { id, expression, expectedSerializedKey = expected, passed,
                actualTypedValueObserved = false, nativeExceptionEquivalent = false,
                exitCode = result.ExitCode, stdout = result.Stdout, stderr = result.Stderr });
        }
        check("matches-operator-fixed-public-count", successes == 100 && rejections == 6, $"keys={successes}; rejections={rejections}");
        var observationsPath = Path.Combine(directory, "public-observations.json");
        await File.WriteAllBytesAsync(observationsPath, JsonSerializer.SerializeToUtf8Bytes(observations, new JsonSerializerOptions { WriteIndented = true }));
        await ReadReference("public-reader.json", ["--actual", observationsPath]);

        var order = await run("matches-operator-public-left-to-right", 0, true, ["--compare", left, right,
            "--line-transform", "left", "@noopt LogInfo(\"left\",\"a\") matches LogInfo(\"right\",\"a\")",
            "--line-transform", "right", "true"]);
        check(order.Name + "-trace", order.Stderr == "info: left a" + Environment.NewLine + "info: right a" + Environment.NewLine, order.Stderr);
        var invalid = await run("matches-operator-constant-invalid-before-left", 2, false, ["--compare", left, right,
            "--line-transform", "left", "LogInfo(\"left\",\"a\") matches \"[\""]);
        check(invalid.Name + "-atomic", invalid.Stdout.Length == 0 && !invalid.Stderr.Contains("info: left", StringComparison.Ordinal), invalid.Stderr);
        var dynamicInvalid = await run("matches-operator-dynamic-invalid-after-left", 0, true, ["--compare", left, right,
            "--line-transform", "left", "LogInfo(\"left\",\"a\") matches If(LineNumber>0,\"[\",none)",
            "--line-transform", "right", "false"]);
        check(dynamicInvalid.Name + "-trace", dynamicInvalid.Stderr == "info: left a" + Environment.NewLine, dynamicInvalid.Stderr);
        foreach (var input in new[] { left, right })
            check("matches-operator-input-preserved-" + Path.GetFileName(input), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(input))) == inputHash, "Input bytes retained.");

        var projectLeft = Path.Combine(directory, "project-left.txt"); var projectRight = Path.Combine(directory, "project-right.txt");
        await File.WriteAllTextAsync(projectLeft, "a", utf8); await File.WriteAllTextAsync(projectRight, "b", utf8);
        const string rule = "Line matches \"[ab]\"";
        var table = await run("matches-operator-table", 0, true, ["--table", projectLeft, projectRight, "--line-transform", "all", rule]);
        using (var json = JsonDocument.Parse(table.Stdout))
            check(table.Name + "-classification", !json.RootElement.GetProperty("different").GetBoolean(), table.Stdout);
        var project = Path.Combine(directory, "project.json"); var copy = Path.Combine(directory, "project-copy.json");
        await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new { mode = "Text", leftPath = projectLeft, rightPath = projectRight,
            lineTransformRules = new[] { new { expression = rule, panes = 7 } } }), utf8);
        await run("matches-operator-project-copy", 0, false, ["--project-copy", project, copy]);
        var html = Path.Combine(directory, "report.html");
        await run("matches-operator-project-report", 0, false, ["--report-project", copy, html]);
        check("matches-operator-project-report-ignored", File.ReadAllText(html).Contains("data-ignored=\"true\"", StringComparison.Ordinal), "Saved/restored rule reaches HTML comparison.");
        var invalidProject = Path.Combine(directory, "invalid-project.json");
        await File.WriteAllTextAsync(invalidProject, JsonSerializer.Serialize(new { mode = "Text", leftPath = projectLeft, rightPath = projectRight,
            lineTransformRules = new[] { new { expression = "Line matches \"[\"", panes = 7 } } }), utf8);
        var sentinel = await File.ReadAllBytesAsync(html);
        var failedReport = await run("matches-operator-invalid-report-output-preserved", 2, false, ["--report-project", invalidProject, html]);
        check(failedReport.Name + "-atomic", failedReport.Stdout.Length == 0 && (await File.ReadAllBytesAsync(html)).SequenceEqual(sentinel), "Existing output retained after constant pattern rejection.");

        async Task ReadReference(string name, string[] arguments)
        {
            var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-B", Path.Combine(reference, "verify.py") }.Concat(arguments)) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Matches operator reader did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
            var text = await stdout; var error = await stderr;
            await File.WriteAllTextAsync(Path.Combine(directory, name), text, utf8);
            check("matches-operator-" + name, process.ExitCode == 0 && error.Length == 0, text + error);
            if (process.ExitCode != 0) throw new IOException("Matches operator reader failed: " + error);
        }
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
