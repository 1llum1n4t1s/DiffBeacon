using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformLikeScenarios
{
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, string python)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("LIKE fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformLike");
        var directory = Path.GetFullPath(Path.Combine(fixtures, "line-transform-like"));
        foreach (var root in new[] { repository.FullName,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Environment.GetEnvironmentVariable("CODEX_HOME") })
            if (!string.IsNullOrEmpty(root) && (directory.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
                || directory.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("LIKE E2E output must remain outside repository and Codex home.");
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("LIKE E2E requires fresh output.");
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false, true);
        await ReadReference();
        var native = File.ReadLines(Path.Combine(reference, "golden.ndjson"))
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
        using var cases = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "cases.json")));
        var left = Path.Combine(directory, "left.txt");
        var right = Path.Combine(directory, "right.txt");
        var observations = new List<object>();
        var nativeCases = 0; var nativeRuns = 0; var physicalRuns = 0; var nativeErrors = 0;

        // 失敗条件: 部分一致への変更、空入力/UTF scalar/結合文字の混同、regex化、
        // subject依存の不正setの先行拒否、大小文字、NOTの集約、原文と比較キーの取り違え。
        // Goldenはscalar Globだけ。以下のArray包装とNOTは既存lifting/BooleanValueの接続検証。
        foreach (var item in cases.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var subject = item.GetProperty("subject").GetString()!;
            var pattern = item.GetProperty("pattern").GetString()!;
            var sensitive = item.GetProperty("caseSensitive").GetBoolean();
            var row = native[id];
            var rejected = row.GetProperty("status").GetString() == "error";
            var value = !rejected && row.GetProperty("value").GetBoolean();
            // 空fileには変換対象行がなく、CR/LFは物理行を分ける。これらはliteralのまま照合する。
            var physical = subject.Length > 0 && subject.IndexOfAny(['\r', '\n', '\0']) < 0;
            var input = physical ? subject : "probe";
            await WriteInputs(input);
            foreach (var raw in new[] { false, true })
            {
                var literal = Quote(subject);
                var expressions = new List<string> { literal + " like " + Quote(pattern), literal + " not like " + Quote(pattern) };
                if (physical) expressions.AddRange(["Line like " + Quote(pattern), "Line not like " + Quote(pattern)]);
                var expression = (sensitive ? "@cs " : "@ci ") + (raw ? "@noopt " : "")
                    + "Array(" + string.Join(",", expressions.Select(x => "(" + x + ")")) + ")";
                var expected = "[" + string.Join(", ", Enumerable.Range(0, expressions.Count)
                    .Select(index => (index % 2 == 0 ? value : !value) ? "true" : "false")) + "]";
                await Probe(id + (raw ? "-raw" : "-optimized"), expression, rejected ? null : expected,
                    new { nativeId = id, nativeStatus = row.GetProperty("status").GetString(),
                        scalarExpected = rejected ? (bool?)null : value, physicalLine = physical,
                        raw, actualTypedValueObserved = false, nativeExceptionEquivalent = false });
                nativeRuns++;
                if (physical) physicalRuns++;
                if (rejected) nativeErrors++;
            }
            nativeCases++;
        }
        check("like-native-case-coverage", nativeCases == 99 && nativeRuns == 198,
            $"nativeCases={nativeCases}; raw/optimized={nativeRuns}; physical={physicalRuns}; errors={nativeErrors}");

        await WriteInputs("ordinary original");
        // 値はscalar native結果と旧computeのbroadcast/pair/noneおよびNotNodeのbool集約から導出する。
        foreach (var raw in new[] { false, true })
        {
            var prefix = "@cs " + (raw ? "@noopt " : "");
            var suffix = raw ? "-raw" : "-optimized";
            foreach (var (id, expression, expected) in new (string, string, string)[]
            {
                ("broadcast-left", "Array(\"abc\",\"z\",none,1,true) like \"[abc]\"", "[false, false, none, none, none]"),
                ("broadcast-right", "\"abc\" like Array(\"abc\",\"[abc]\",none)", "[true, false, none]"),
                ("paired-with-tail", "Array(\"abc\",\"z\",\"unused\") like Array(\"abc\",\"[abc]\")", "[true, false, none]"),
                ("paired-right-tail", "Array(\"abc\") like Array(\"abc\",\"[abc]\")", "[true, none]"),
                ("not-aggregate", "Array(\"abc\",\"z\",none) not like Array(\"abc\",\"[abc]\")", "false"),
                ("not-all-false", "Array(\"z\",none) not like \"[abc]\"", "true"),
                ("none-left", "none like \"[\"", "none"),
                ("none-right", "\"abc\" like none", "none"),
                ("none-not", "none not like \"[\"", "none"),
                ("empty-array", "Array() like \"[\"", "[]"),
                ("all-none-array", "Array(none,none) like \"[\"", "[none, none]"),
                ("content-left-marker", "Content like \"*\"", "none"),
                ("content-right-marker", "\"ordinary original\" like Content", "none"),
                ("content-both-markers", "Content like Content", "none"),
                ("content-not-marker", "Content not like \"[\"", "none"),
                ("content-explicit-string", "\"\"+Content like \"<FileContent:*>\"", "true"),
            })
                await Probe(id + suffix, prefix + expression, expected,
                    new { source = "native scalar results plus original compute/NotNode type and lifting contracts", raw });
        }

        // patternの構築と左右評価はruntimeで行う。Globにはregexの事前compile拒否を流用しない。
        await Probe("dynamic-invalid-set-miss", "@cs Line like If(LineNumber>0,\"[a\",none)", "false",
            new { source = "native invalid-b; dynamic pattern" }, input: "b");
        await Probe("dynamic-invalid-set-negated", "@cs Line like If(LineNumber>0,\"[!a\",none)", "true",
            new { source = "native invalid-neg-b; dynamic pattern" }, input: "b");
        await Probe("dynamic-invalid-set-hit", "@cs Line like If(LineNumber>0,\"[a\",none)", null,
            new { source = "native invalid-a; dynamic pattern" }, input: "a");
        await Probe("public-left-to-right", "@cs @noopt LogInfo(\"left\",\"b\") like LogInfo(\"right\",\"[a\")", "false",
            new { source = "native invalid-b and original BinaryOpNode left-to-right evaluation" },
            expectedStderr: "info: left b" + Environment.NewLine + "info: right [a" + Environment.NewLine);

        // 共有予算と既存出力保護の製品契約。原典fixtureの上限同等性とは扱わない。
        var budgetLeft = Path.Combine(directory, "budget-left.txt");
        var budgetRight = Path.Combine(directory, "budget-right.txt");
        var large = "first\r\n" + new string('x', 262144) + "\r\n";
        await File.WriteAllTextAsync(budgetLeft, large, utf8);
        await File.WriteAllTextAsync(budgetRight, large, utf8);
        var budgetProject = Path.Combine(directory, "budget-project.json");
        await File.WriteAllTextAsync(budgetProject, JsonSerializer.Serialize(new
        {
            mode = "Text", leftPath = budgetLeft, rightPath = budgetRight,
            lineTransformRules = Enumerable.Range(0, 256).Select(_ => new
            { expression = "@cs @noopt If(Line like \"*\",Line,Line)", panes = 1 }).ToArray()
        }), utf8);
        var html = Path.Combine(directory, "budget-existing.html");
        var sentinel = utf8.GetBytes("LIKE existing output sentinel");
        await File.WriteAllBytesAsync(html, sentinel);
        var budget = await run("like-shared-budget-not-false", 2, false, ["--report-project", budgetProject, html]);
        var budgetPassed = budget.ExitCode == 2 && budget.Stdout.Length == 0
            && budget.Stderr.Contains("行変換の共有処理量または生成文字数", StringComparison.Ordinal)
            && (await File.ReadAllBytesAsync(html)).SequenceEqual(sentinel)
            && await File.ReadAllTextAsync(budgetLeft, utf8) == large
            && await File.ReadAllTextAsync(budgetRight, utf8) == large;
        check(budget.Name + "-atomic", budgetPassed, budget.Stderr);
        observations.Add(new { id = "shared-budget", passed = budgetPassed,
            exitCode = budget.ExitCode, stdout = budget.Stdout, stderr = budget.Stderr,
            source = "existing product shared budget and report output protection; no native budget equivalence" });

        await File.WriteAllBytesAsync(Path.Combine(directory, "public-observations.json"),
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                nativeCases, nativeRuns, physicalRuns, nativeErrors,
                cancellation = "Not executed: existing run delegate has no cancellation injection; App diagnostics/UI required.",
                observations
            }, new JsonSerializerOptions { WriteIndented = true }));

        async Task WriteInputs(string input)
        {
            await File.WriteAllTextAsync(left, input, utf8);
            await File.WriteAllTextAsync(right, input, utf8);
        }

        async Task Probe(string id, string expression, string? expected, object evidence,
            string? input = null, string expectedStderr = "")
        {
            if (input is not null) await WriteInputs(input);
            var original = new[] { await File.ReadAllBytesAsync(left), await File.ReadAllBytesAsync(right) };
            var rejected = expected is null;
            var arguments = new List<string> { "--compare", left, right, "--line-transform", "left", expression };
            if (!rejected) arguments.AddRange(["--line-transform", "right", Quote(expected!)]);
            var result = await run("like-" + id, rejected ? 2 : 0, !rejected, arguments.ToArray());
            var passed = rejected
                ? result.ExitCode == 2 && result.Stdout.Length == 0 && result.Stderr.Length > 0
                : result.ExitCode == 0 && result.Stderr == expectedStderr;
            if (passed && !rejected)
            {
                using var json = JsonDocument.Parse(result.Stdout);
                passed = !json.RootElement.GetProperty("different").GetBoolean();
            }
            foreach (var (path, index) in new[] { (left, 0), (right, 1) })
                check(result.Name + "-input-" + index,
                    SHA256.HashData(await File.ReadAllBytesAsync(path)).SequenceEqual(SHA256.HashData(original[index])),
                    "Original input bytes retained.");
            check(result.Name + "-public-key", passed, result.Stderr);
            observations.Add(new { id, expression, expectedSerializedKey = expected, passed, evidence,
                actualTypedValueObserved = false, exitCode = result.ExitCode, stdout = result.Stdout, stderr = result.Stderr });
        }

        async Task ReadReference()
        {
            var start = new ProcessStartInfo(python)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-B"); start.ArgumentList.Add(Path.Combine(reference, "verify.py"));
            using var process = Process.Start(start) ?? throw new IOException("LIKE reader did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
            var text = await stdout; var error = await stderr;
            await File.WriteAllTextAsync(Path.Combine(directory, "native-reader.json"), text, utf8);
            check("like-native-reader", process.ExitCode == 0 && error.Length == 0, text + error);
            if (process.ExitCode != 0 || error.Length != 0) throw new IOException("LIKE reader failed: " + error);
        }

        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
