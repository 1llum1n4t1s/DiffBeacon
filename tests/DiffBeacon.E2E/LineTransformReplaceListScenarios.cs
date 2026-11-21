using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformReplaceListScenarios
{
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, string python)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("ReplaceLists fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformReplaceLists");
        var directory = Path.GetFullPath(Path.Combine(fixtures, "line-transform-replace-lists"));
        if (Directory.Exists(directory)) throw new IOException("ReplaceLists E2E requires fresh output.");
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false, true);
        // 失敗条件: string/compiledの混同、逐次適用、型・none・直接配列lifting、eager順、
        // 空pattern/subject、file分割・escape・invalid rule、最適化寿命、上限・原本上書き、包装後の依存喪失。
        await Reader("native-reader", "verify.py", "--verify");
        using var plan = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "public-case-plan.json")));
        var observations = new List<object>();
        foreach (var item in plan.RootElement.EnumerateArray())
        {
            var outcome = item.GetProperty("expectedOutcome").GetString();
            if (outcome == "native-only") continue;
            var id = item.GetProperty("id").GetString()!;
            var expression = item.GetProperty("expression").GetString()!;
            var expectedError = outcome is "runtime-rejection" or "compile-rejection";
            var keys = item.GetProperty("expectedSerializedKeys").EnumerateArray().Select(key => key.GetString()!).ToArray();
            var expectedExpression = expectedError ? "Line" : "@noopt Choose(LineNumber-1," + string.Join(",", keys.Select(key => Quote(utf8.GetString(Convert.FromHexString(key))))) + ")";
            var result = await Compare("bridge-" + id, expression, expectedExpression, expectedError ? 2 : 0);
            var passed = expectedError ? result.ExitCode == 2 && result.Stdout.Length == 0 && result.Stderr.Length > 0 : Equal(result);
            string? optimizedExpression = null; bool? optimizedPassed = null; int? optimizedExitCode = null;
            var optimizedStdout = ""; var optimizedStderr = "";
            if (!expectedError)
            {
                optimizedExpression = expression.Replace("@noopt ", "", StringComparison.Ordinal);
                var optimized = await Compare("optimized-" + id, optimizedExpression, expectedExpression);
                optimizedPassed = Equal(optimized); optimizedExitCode = optimized.ExitCode;
                optimizedStdout = optimized.Stdout; optimizedStderr = optimized.Stderr;
                check(optimized.Name + "-keys", optimizedPassed.Value, optimized.Stderr);
            }
            check(result.Name + "-public", passed, result.Stderr);
            observations.Add(new { id, expression, expectedExpression, expectedSerializedKeys = keys, expectedError, passed,
                optimizedExpression, optimizedPassed, optimizedExitCode, optimizedStdout, optimizedStderr,
                actualTypedValueObserved = false, contextEquivalent = false, nativeExceptionEquivalent = false,
                exitCode = result.ExitCode, stdout = result.Stdout, stderr = result.Stderr });
        }
        var observationPath = Path.Combine(directory, "public-observations.json");
        await File.WriteAllBytesAsync(observationPath, JsonSerializer.SerializeToUtf8Bytes(observations));
        await Reader("public-reader", "verify-public.py", "--actual", observationPath);

        using var cases = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "cases.json")));
        var golden = (await File.ReadAllLinesAsync(Path.Combine(reference, "golden.ndjson"))).Select(line => JsonDocument.Parse(line)).ToArray();
        var fileObservations = new List<object>();
        try
        {
            var index = 0;
            foreach (var item in cases.RootElement.EnumerateArray())
            {
                var expected = golden[index++].RootElement;
                var args = item.GetProperty("args");
                if (args.GetArrayLength() != 2 || args[1].GetProperty("type").GetString() != "path") continue;
                var id = item.GetProperty("id").GetString()!;
                var list = Path.Combine(directory, id + ".tsv");
                var bytes = item.GetProperty("fileUtf8Hex").ValueKind == JsonValueKind.Null ? null
                    : Convert.FromHexString(item.GetProperty("fileUtf8Hex").GetString()!);
                if (bytes is not null) await File.WriteAllBytesAsync(list, bytes);
                var key = utf8.GetString(Convert.FromHexString(expected.GetProperty("serializedUtf8Hex").GetString()!));
                foreach (var noopt in new[] { false, true })
                {
                    var expression = "@" + (item.GetProperty("caseSensitive").GetBoolean() ? "cs " : "ci ") + (noopt ? "@noopt " : "")
                        + item.GetProperty("function").GetString() + "(" + Literal(args[0]) + "," + Quote(list) + ")";
                    var result = await Compare("file-" + id + (noopt ? "-noopt" : "-opt"), expression, Quote(key));
                    var expectedLogs = expected.GetProperty("functionLogs").GetArrayLength() > 0;
                    check(result.Name + "-native-key", Equal(result, allowLog: expectedLogs), result.Stderr);
                    fileObservations.Add(new { id, noopt, expression, expectedSerializedKey = expected.GetProperty("serializedUtf8Hex").GetString(),
                        passed = Equal(result, allowLog: expectedLogs), exitCode = result.ExitCode, stdout = result.Stdout, stderr = result.Stderr,
                        nativeFileApiEquivalent = false });
                    if (bytes is not null) check(result.Name + "-source-preserved", (await File.ReadAllBytesAsync(list)).SequenceEqual(bytes), "List input bytes retained.");
                }
            }
        }
        finally { foreach (var item in golden) item.Dispose(); }
        await File.WriteAllBytesAsync(Path.Combine(directory, "file-observations.json"), JsonSerializer.SerializeToUtf8Bytes(fileObservations));

        var listA = Path.Combine(directory, "package-a.tsv"); var listB = Path.Combine(directory, "package-b.tsv");
        await File.WriteAllTextAsync(listA, "source\tX\n", utf8); await File.WriteAllTextAsync(listB, "source\tY\n", utf8);
        var left = Path.Combine(directory, "project-left.txt"); var right = Path.Combine(directory, "project-right.txt");
        await File.WriteAllTextAsync(left, "source\r\nsource", utf8); await File.WriteAllTextAsync(right, "X\r\nY", utf8);
        var expressionWithPaths = "ReplaceWithList(Line,If(LineNumber==1," + Quote(listA) + "," + Quote(listB) + "))";
        var order = await Compare("eager-order", "@noopt RegexReplaceWithList(LogInfo(\"first\",Line),LogInfo(\"second\",Array(Array(\"source\",\"X\"))))", "\"X\"");
        check("replace-lists-eager-source-list", order.ExitCode == 0 && order.Stderr.IndexOf("first", StringComparison.Ordinal) is var first
            && first >= 0 && order.Stderr.IndexOf("second", StringComparison.Ordinal) > first, order.Stderr);
        var tooLarge = Path.Combine(directory, "oversize.tsv");
        using (var bytes = new FileStream(tooLarge, FileMode.CreateNew, FileAccess.Write)) bytes.SetLength(4 * 1024 * 1024L + 1);
        var oversized = await Compare("oversize", "ReplaceWithList(Line," + Quote(tooLarge) + ")", "Line", 2);
        check("replace-lists-oversize-atomic", oversized.ExitCode == 2 && oversized.Stdout.Length == 0 && oversized.Stderr.Length > 0, oversized.Stderr);
        var project = Path.Combine(directory, "project.json");
        await File.WriteAllBytesAsync(project, JsonSerializer.SerializeToUtf8Bytes(new { leftPath = left, rightPath = right, mode = "Text",
            lineTransformRules = new[] { new { expression = expressionWithPaths, panes = 1 } } }));
        var report = await run("replace-lists-report", 0, true, ["--report-project", project, Path.Combine(directory, "report.html")]);
        check("replace-lists-report-success", report.ExitCode == 0, report.Stderr);
        await run("replace-lists-table", 0, true, ["--table", left, right, "--line-transform", "left", expressionWithPaths]);
        foreach (var list in new[] { listA, listB })
        {
            var before = await File.ReadAllBytesAsync(list);
            var reject = await run("replace-lists-output-protected-" + Path.GetFileName(list), 2, false, ["--report-project", project, list]);
            check(reject.Name + "-preserved", reject.Stdout.Length == 0 && (await File.ReadAllBytesAsync(list)).SequenceEqual(before), reject.Stderr);
            var copy = await run("replace-lists-project-copy-protected-" + Path.GetFileName(list), 2, false, ["--project-copy", project, list]);
            check(copy.Name + "-preserved", copy.Stdout.Length == 0 && (await File.ReadAllBytesAsync(list)).SequenceEqual(before), copy.Stderr);
        }
        var package = Path.Combine(directory, "package.zip");
        await run("replace-lists-package", 0, true, ["--package-project", project, package, "--report"]);
        var finiteProject = Path.Combine(directory, "finite-project.json");
        var finiteExpression = "ReplaceWithList(Line,At(Array(" + Quote(listA) + "," + Quote(listB) + "),LineNumber-1))";
        await File.WriteAllBytesAsync(finiteProject, JsonSerializer.SerializeToUtf8Bytes(new { leftPath = left, rightPath = right, mode = "Text",
            lineTransformRules = new[] { new { expression = finiteExpression, panes = 1 } } }));
        await run("replace-lists-finite-array-package", 0, true, ["--package-project", finiteProject, Path.Combine(directory, "finite.zip"), "--report"]);
        var dynamicLeft = Path.Combine(directory, "dynamic-left.txt");
        await File.WriteAllTextAsync(dynamicLeft, listA + "\r\n" + listB, utf8);
        var dynamicProject = Path.Combine(directory, "dynamic-project.json");
        await File.WriteAllBytesAsync(dynamicProject, JsonSerializer.SerializeToUtf8Bytes(new { leftPath = dynamicLeft, rightPath = right, mode = "Text",
            lineTransformRules = new[] { new { expression = "ReplaceWithList(\"source\",Line)", panes = 1 } } }));
        var dynamicReport = Path.Combine(directory, "dynamic.html");
        await run("replace-lists-dynamic-report", 0, true, ["--report-project", dynamicProject, dynamicReport]);
        check("replace-lists-dynamic-key", (await File.ReadAllTextAsync(dynamicReport)).Contains("data-different=\"false\"", StringComparison.Ordinal), "Row paths are loaded during evaluation.");
        var dynamicSource = await File.ReadAllBytesAsync(listA);
        var dynamicOutput = await run("replace-lists-dynamic-output-protected", 2, false, ["--report-project", dynamicProject, listA]);
        check("replace-lists-dynamic-output-retained", dynamicOutput.Stdout.Length == 0 && (await File.ReadAllBytesAsync(listA)).SequenceEqual(dynamicSource), dynamicOutput.Stderr);
        var unsupportedPackage = Path.Combine(directory, "dynamic.zip"); var sentinel = utf8.GetBytes("existing package");
        await File.WriteAllBytesAsync(unsupportedPackage, sentinel);
        var unsupported = await run("replace-lists-dynamic-package-rejected", 2, false, ["--package-project", dynamicProject, unsupportedPackage]);
        check("replace-lists-dynamic-package-retained", unsupported.Stdout.Length == 0 && unsupported.Stderr.Contains("実行時", StringComparison.Ordinal)
            && (await File.ReadAllBytesAsync(unsupportedPackage)).SequenceEqual(sentinel), unsupported.Stderr);
        // 数値連結の実行時pathを依存なしとして包装してはならない。
        var numericList = Path.Combine(directory, "numeric-1.tsv");
        await File.WriteAllTextAsync(numericList, "source\tX\n", utf8);
        var numericExpression = "ReplaceWithList(Line," + Quote(Path.Combine(directory, "numeric-")) + "+1+\".tsv\")";
        await Compare("numeric-path-runtime", numericExpression, "\"X\"");
        var numericProject = Path.Combine(directory, "numeric-project.json");
        await File.WriteAllBytesAsync(numericProject, JsonSerializer.SerializeToUtf8Bytes(new { leftPath = left, rightPath = right, mode = "Text",
            lineTransformRules = new[] { new { expression = numericExpression, panes = 1 } } }));
        var numericPackage = Path.Combine(directory, "numeric.zip"); await File.WriteAllBytesAsync(numericPackage, sentinel);
        var numericRejected = await run("replace-lists-numeric-package-rejected", 2, false, ["--package-project", numericProject, numericPackage]);
        check("replace-lists-numeric-package-output-preserved", numericRejected.Stdout.Length == 0
            && (await File.ReadAllBytesAsync(numericPackage)).SequenceEqual(sentinel), numericRejected.Stderr);

        // 連結の内部にある選択枝の非stringも、包装依存から欠落させない。
        var mixedPrefix = Path.Combine(directory, "mixed-");
        await File.WriteAllTextAsync(mixedPrefix + "1.tsv", "source\tX\n", utf8);
        await File.WriteAllTextAsync(mixedPrefix + "A.tsv", "source\tX\n", utf8);
        foreach (var (name, branch) in new[] { ("if", "If(LineNumber>1,\"A\",1)"),
            ("choose", "Choose(LineNumber-1,1,\"A\")"), ("at-array", "At(Array(1,\"A\"),LineNumber-1)"),
            ("nested", "If(LineNumber>1,\"A\",Choose(0,At(Array(1,\"A\"),0),\"A\"))") })
        {
            var mixedExpression = "ReplaceWithList(Line," + Quote(mixedPrefix) + "+" + branch + "+\".tsv\")";
            foreach (var noopt in new[] { false, true })
                await Compare("mixed-" + name + "-runtime-" + noopt, (noopt ? "@noopt " : "") + mixedExpression, "\"X\"");
            var mixedProject = Path.Combine(directory, "mixed-" + name + ".json");
            await File.WriteAllBytesAsync(mixedProject, JsonSerializer.SerializeToUtf8Bytes(new { leftPath = left, rightPath = right, mode = "Text",
                lineTransformRules = new[] { new { expression = mixedExpression, panes = 1 } } }));
            var mixedPackage = Path.Combine(directory, "mixed-" + name + ".zip"); await File.WriteAllBytesAsync(mixedPackage, sentinel);
            var refused = await run("replace-lists-mixed-" + name + "-package-rejected", 2, false, ["--package-project", mixedProject, mixedPackage]);
            check("replace-lists-mixed-" + name + "-preserved", refused.Stdout.Length == 0 && refused.Stderr.Length > 0
                && (await File.ReadAllBytesAsync(mixedPackage)).SequenceEqual(sentinel)
                && (await File.ReadAllTextAsync(mixedPrefix + "1.tsv")) == "source\tX\n"
                && (await File.ReadAllTextAsync(mixedPrefix + "A.tsv")) == "source\tX\n", refused.Stderr);
        }

        // 連結中のAtがnoneを返す場合も、空のpath区間を包装候補へ含める。
        var atNonePrefix = Path.Combine(directory, "at-none-");
        var atNoneA = atNonePrefix + "A.tsv"; var atNoneFallback = atNonePrefix + ".tsv";
        await File.WriteAllTextAsync(atNoneA, "source\tX\n", utf8);
        await File.WriteAllTextAsync(atNoneFallback, "source\tY\n", utf8);
        foreach (var functionName in new[] { "ReplaceWithList", "RegexReplaceWithList" })
        {
            var atNoneExpression = functionName + "(Line," + Quote(atNonePrefix) + "+At(Array(\"A\"),LineNumber-1)+\".tsv\")";
            var atNoneProject = Path.Combine(directory, "at-none-" + functionName + ".json");
            await File.WriteAllBytesAsync(atNoneProject, JsonSerializer.SerializeToUtf8Bytes(new { leftPath = left, rightPath = right, mode = "Text",
                lineTransformRules = new[] { new { expression = atNoneExpression, panes = 1 } } }));
            var atNonePackage = Path.Combine(directory, "at-none-" + functionName + ".zip");
            await run("replace-lists-at-none-" + functionName + "-package", 0, true, ["--package-project", atNoneProject, atNonePackage]);
            var atNoneExtracted = Path.Combine(directory, "at-none-" + functionName + "-extracted");
            await run("replace-lists-at-none-" + functionName + "-extract", 0, true, ["--archive-extract", atNonePackage, atNoneExtracted]);
            var atNoneRestored = Path.Combine(atNoneExtracted, "project.json");
            using (var packaged = JsonDocument.Parse(await File.ReadAllBytesAsync(atNoneRestored)))
            {
                var assets = packaged.RootElement.GetProperty("entries")[0].GetProperty("lineTransformListAssets");
                var assetPaths = assets.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
                check("replace-lists-at-none-" + functionName + "-assets", assetPaths.ContainsKey(atNoneA) && assetPaths.ContainsKey(atNoneFallback), "Both the selected item and none-to-empty path are packaged.");
                foreach (var pair in assetPaths.Where(pair => pair.Key == atNoneA || pair.Key == atNoneFallback))
                {
                    var expected = pair.Key == atNoneA ? "source\tX\n" : "source\tY\n";
                    check("replace-lists-at-none-" + functionName + "-asset-" + Path.GetFileName(pair.Key),
                        (await File.ReadAllBytesAsync(Path.GetFullPath(pair.Value, atNoneExtracted))).SequenceEqual(utf8.GetBytes(expected)), "Packaged path maps to its original bytes.");
                }
            }
            var atNoneReport = Path.Combine(directory, "at-none-" + functionName + ".html");
            await run("replace-lists-at-none-" + functionName + "-report", 0, true, ["--report-project", atNoneRestored, atNoneReport]);
            check("replace-lists-at-none-" + functionName + "-compares", (await File.ReadAllTextAsync(atNoneReport)).Contains("data-different=\"false\"", StringComparison.Ordinal), "Extracted comparison uses both path alternatives.");

            var missingPrefix = Path.Combine(directory, "at-none-missing-" + functionName + "-");
            await File.WriteAllTextAsync(missingPrefix + "A.tsv", "source\tX\n", utf8);
            var missingExpression = functionName + "(Line," + Quote(missingPrefix) + "+At(Array(\"A\"),LineNumber-1)+\".tsv\")";
            var missingProject = Path.Combine(directory, "at-none-missing-" + functionName + ".json");
            await File.WriteAllBytesAsync(missingProject, JsonSerializer.SerializeToUtf8Bytes(new { leftPath = left, rightPath = right, mode = "Text",
                lineTransformRules = new[] { new { expression = missingExpression, panes = 1 } } }));
            var missingPackage = Path.Combine(directory, "at-none-missing-" + functionName + ".zip");
            await File.WriteAllBytesAsync(missingPackage, sentinel);
            var missing = await run("replace-lists-at-none-missing-" + functionName + "-package", 2, false, ["--package-project", missingProject, missingPackage]);
            check("replace-lists-at-none-missing-" + functionName + "-preserved", missing.Stdout.Length == 0
                && (await File.ReadAllBytesAsync(missingPackage)).SequenceEqual(sentinel)
                && (await File.ReadAllTextAsync(missingPrefix + "A.tsv")) == "source\tX\n", missing.Stderr);
        }

        // literalのcompileも包装snapshotだけを読み、元pathの後発肥大化に依存しない。
        var literalProject = Path.Combine(directory, "literal-project.json"); var literalRight = Path.Combine(directory, "literal-right.txt");
        await File.WriteAllTextAsync(literalRight, "X\r\nX", utf8);
        await File.WriteAllBytesAsync(literalProject, JsonSerializer.SerializeToUtf8Bytes(new { leftPath = left, rightPath = literalRight, mode = "Text",
            lineTransformRules = new[] { new { expression = "ReplaceWithList(Line," + Quote(listA) + ")", panes = 1 } } }));
        var literalPackage = Path.Combine(directory, "literal.zip"); var literalExtracted = Path.Combine(directory, "literal-extracted");
        await run("replace-lists-literal-package", 0, true, ["--package-project", literalProject, literalPackage, "--report"]);
        await run("replace-lists-literal-extract", 0, true, ["--archive-extract", literalPackage, literalExtracted]);
        using (var oversizedOriginal = new FileStream(listA, FileMode.Open, FileAccess.Write)) oversizedOriginal.SetLength(4 * 1024 * 1024L + 1);
        var literalRestored = Path.Combine(literalExtracted, "project.json"); var literalReport = Path.Combine(directory, "literal-restored.html");
        await run("replace-lists-literal-restored-report", 0, true, ["--report-project", literalRestored, literalReport]);
        check("replace-lists-literal-restored-compares", (await File.ReadAllTextAsync(literalReport)).Contains("data-different=\"false\"", StringComparison.Ordinal), "Embedded bytes survive oversized original.");
        var overriddenReport = Path.Combine(directory, "literal-overridden.html");
        await run("replace-lists-literal-restored-cli-override", 0, true, ["--report-project", literalRestored, overriddenReport,
            "--line-transform", "left", "ReplaceWithList(Line," + Quote(listA) + ")"]);
        check("replace-lists-literal-cli-override-compares", (await File.ReadAllTextAsync(overriddenReport)).Contains("data-different=\"false\"", StringComparison.Ordinal), "CLI override uses embedded map.");
        var unavailableOriginal = listA + ".unavailable"; File.Move(listA, unavailableOriginal);
        var literalCopy = Path.Combine(directory, "literal-copy.json");
        await run("replace-lists-literal-restored-copy", 0, true, ["--project-copy", literalRestored, literalCopy]);
        await run("replace-lists-literal-copied-report", 0, true, ["--report-project", literalCopy, Path.Combine(directory, "literal-copied.html")]);
        check("replace-lists-literal-missing-original-compares", (await File.ReadAllTextAsync(Path.Combine(directory, "literal-copied.html"))).Contains("data-different=\"false\"", StringComparison.Ordinal), "Copied embedded bytes survive missing original.");
        File.Move(unavailableOriginal, listA); await File.WriteAllTextAsync(listA, "source\tX\n", utf8);

        var extracted = Path.Combine(directory, "extracted");
        await run("replace-lists-extract", 0, true, ["--archive-extract", package, extracted]);
        // 原リストを変更しても、展開projectは包装snapshotを参照する。
        await File.WriteAllTextAsync(listA, "source\tWRONG\n", utf8); await File.WriteAllTextAsync(listB, "source\tWRONG\n", utf8);
        var extractedProject = Path.Combine(extracted, "project.json");
        var extractedReport = Path.Combine(directory, "extracted.html");
        await run("replace-lists-extracted-report", 0, true, ["--report-project", extractedProject, extractedReport]);
        using (var packaged = JsonDocument.Parse(await File.ReadAllBytesAsync(extractedProject)))
        {
            var entry = packaged.RootElement.GetProperty("entries")[0];
            check("replace-lists-packaged-map-and-expression", packaged.RootElement.GetProperty("formatVersion").GetInt32() == 10
                && entry.GetProperty("lineTransformRules")[0].GetProperty("expression").GetString() == expressionWithPaths
                && entry.GetProperty("lineTransformListAssets").EnumerateObject().Count() == 2, "v10 preserves logical paths and expression text.");
            foreach (var asset in entry.GetProperty("lineTransformListAssets").EnumerateObject())
            {
                var expected = asset.Name == listA ? "source\tX\n" : "source\tY\n";
                var assetPath = Path.GetFullPath(asset.Value.GetString()!, extracted);
                check("replace-lists-packaged-asset-" + Path.GetFileName(assetPath), (await File.ReadAllBytesAsync(assetPath)).SequenceEqual(utf8.GetBytes(expected)), "Full list snapshot bytes match.");
            }
        }
        check("replace-lists-packaged-snapshots", !(await File.ReadAllTextAsync(extractedReport)).Contains("WRONG", StringComparison.Ordinal)
            && (await File.ReadAllTextAsync(extractedReport)).Contains("data-different=\"false\"", StringComparison.Ordinal), "Packaged map prefers fixed snapshots.");
        var copiedProject = Path.Combine(directory, "copied", "project.json"); Directory.CreateDirectory(Path.GetDirectoryName(copiedProject)!);
        await run("replace-lists-extracted-copy", 0, true, ["--project-copy", extractedProject, copiedProject]);
        await run("replace-lists-copied-report", 0, true, ["--report-project", copiedProject, Path.Combine(directory, "copied.html")]);
        check("replace-lists-copied-snapshots", (await File.ReadAllTextAsync(Path.Combine(directory, "copied.html"))).Contains("data-different=\"false\"", StringComparison.Ordinal), "Copied workspace retains list assets.");

        async Task<CommandResult> Compare(string name, string expression, string expected, int exit = 0)
        {
            var leftPath = Path.Combine(directory, "compare-left.txt"); var rightPath = Path.Combine(directory, "compare-right.txt");
            if (!File.Exists(leftPath)) { await File.WriteAllTextAsync(leftPath, "source", utf8); await File.WriteAllTextAsync(rightPath, "source", utf8); }
            return await run("replace-lists-" + name, exit, exit != 2, ["--compare", leftPath, rightPath,
                "--line-transform", "left", expression, "--line-transform", "right", expected]);
        }
        static bool Equal(CommandResult result, bool allowLog = false)
        {
            if (result.ExitCode != 0 || !allowLog && result.Stderr.Length != 0) return false;
            using var json = JsonDocument.Parse(result.Stdout);
            return !json.RootElement.GetProperty("different").GetBoolean();
        }
        async Task Reader(string name, string script, params string[] args)
        {
            var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-B"); start.ArgumentList.Add(Path.Combine(reference, script));
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new IOException("ReplaceLists reader did not start.");
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch { child.Kill(true); await child.WaitForExitAsync(); throw; }
            var text = await stdout; var error = await stderr;
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), text, utf8);
            check("replace-lists-" + name, child.ExitCode == 0 && error.Length == 0, text + error);
        }
        static string Literal(JsonElement value) => value.GetProperty("type").GetString() switch
        {
            "string" => Quote(value.GetProperty("value").GetString()!), "integer" => "ToInt(" + Quote(value.GetProperty("value").ToString()) + ")",
            "bool" => value.GetProperty("value").GetBoolean() ? "true" : "false", "none" => "none",
            "array" => "Array(" + string.Join(",", value.GetProperty("value").EnumerateArray().Select(Literal)) + ")",
            _ => throw new InvalidDataException("Not a public literal.")
        };
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
