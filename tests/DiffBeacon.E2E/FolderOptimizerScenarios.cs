using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class FolderOptimizerScenarios
{
    internal static async Task RunAsync(string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check, Action<string, string> skip, string python)
    {
        var relative = Path.Combine("tests", "Fixtures", "FolderOptimizer");
        var oracle = new[] { Path.Combine(AppContext.BaseDirectory, relative), Path.GetFullPath(relative) }
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "verify.py")) && File.Exists(Path.Combine(path, "native-proof.zip")))
            ?? throw new DirectoryNotFoundException("FolderOptimizer の固定原本が見つかりません。");
        var work = Path.Combine(fixtures, "folder-properties", "optimizer");
        Directory.CreateDirectory(work);
        var utf8 = new UTF8Encoding(false, true);
        static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        static string Escape(string text) => text.Replace("\"", "\"\"", StringComparison.Ordinal);
        async Task Reader(string id, params string[] arguments)
        {
            var info = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-E", "-B", "-X", "utf8", Path.Combine(oracle, "verify.py") }.Concat(arguments)) info.ArgumentList.Add(argument);
            var started = DateTime.UtcNow;
            using var process = Process.Start(info) ?? throw new IOException("Optimizer readerを起動できません。");
            var pid = process.Id; var birth = process.StartTime.ToUniversalTime();
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            var timeout = false;
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                timeout = true;
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            var output = await stdout.WaitAsync(TimeSpan.FromSeconds(30));
            var error = await stderr.WaitAsync(TimeSpan.FromSeconds(30));
            var actualExit = process.ExitCode; var ended = DateTime.UtcNow;
            await File.WriteAllTextAsync(Path.Combine(work, id + ".stdout"), output, utf8);
            await File.WriteAllTextAsync(Path.Combine(work, id + ".stderr"), error, utf8);
            await File.WriteAllBytesAsync(Path.Combine(work, id + ".reader-process.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                pid, birth, started, ended, actualExit, timeout, waitCompleted = true,
                stdoutComplete = stdout.IsCompletedSuccessfully, stderrComplete = stderr.IsCompletedSuccessfully,
                readerSha256 = Hash(Path.Combine(oracle, "verify.py")), arguments = info.ArgumentList.ToArray()
            }));
            check("folder optimizer independent " + id, !timeout && actualExit == 0 && error.Length == 0, output + error);
            if (timeout || actualExit != 0 || error.Length != 0) throw new InvalidDataException("Optimizer readerが不合格です: " + output + error);
        }
        await Reader("native-proof");
        using var plan = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(oracle, "public-plan.json")));
        var rootsByData = new Dictionary<(string Dataset, int Count), string[]>();
        string[] Roots(string dataset, int count)
        {
            if (rootsByData.TryGetValue((dataset, count), out var found)) return found;
            var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "data-" + dataset + "-" + count, "side-" + side)).ToArray();
            for (var side = 0; side < count; side++)
            {
                Directory.CreateDirectory(roots[side]);
                var file = Path.Combine(roots[side], "row.bin");
                if (dataset == "kind-collision" && side == count - 1) Directory.CreateDirectory(file);
                else if (dataset != "missing-side" || side == count - 1)
                {
                    File.WriteAllBytes(file, [(byte)(side + 1)]);
                    File.SetLastWriteTimeUtc(file, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                }
                if (dataset != "kind-collision") Directory.CreateDirectory(Path.Combine(roots[side], "row.dir"));
            }
            rootsByData.Add((dataset, count), roots); return roots;
        }
        var sentinel = Path.Combine(work, "existing-output.sentinel");
        await File.WriteAllTextAsync(sentinel, "existing output bytes remain unchanged\n", utf8);
        var observations = new List<object>();
        foreach (var item in plan.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!; var count = item.GetProperty("count").GetInt32();
            if (item.TryGetProperty("windowsOnly", out var windowsOnly) && windowsOnly.GetBoolean() && !OperatingSystem.IsWindows())
            {
                skip("folder optimizer " + count + " " + id, "Windows Shell propertyのみ。無Propのmissing-side対照は各OSで実行します。");
                continue;
            }
            var dataset = item.TryGetProperty("dataset", out var data) ? data.GetString()! : "all-present";
            var roots = Roots(dataset, count);
            var expression = item.GetProperty("expression").GetString()!;
            var rule = item.GetProperty("rule").GetString()!;
            var rejected = item.GetProperty("rejected").GetBoolean(); var selected = item.GetProperty("selected").GetBoolean();
            var list = item.GetProperty("listFixture").GetBoolean() ? Path.Combine(work, count + "-" + id + ".list") : null;
            if (list is not null)
            {
                await File.WriteAllTextAsync(list, "a\tfirst\n", utf8);
                expression = expression.Replace("$LIST", Escape(list), StringComparison.Ordinal);
            }
            var filter = Path.Combine(work, count + "-" + id + ".flt");
            await File.WriteAllTextAsync(filter, "def: exclude\n" + rule + ": " + expression + "\n", utf8);
            var protectedPaths = roots.SelectMany(root => Directory.GetFiles(root, "*", SearchOption.AllDirectories)).Append(filter).Append(sentinel)
                .Concat(list is null ? Array.Empty<string>() : new string[] { list }).Distinct(StringComparer.Ordinal).ToArray();
            var before = protectedPaths.ToDictionary(path => path, Hash, StringComparer.Ordinal);
            string[] arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered", .. (count == 3 ? new[] { "--middle", roots[1] } : Array.Empty<string>())];
            var expectedExit = rejected ? 2 : selected ? 1 : 0;
            var result = await run("folder-optimizer-" + count + "-" + id, expectedExit, !rejected, arguments);
            var after = protectedPaths.ToDictionary(path => path, Hash, StringComparer.Ordinal);
            var stdout = Path.Combine(work, count + "-" + id + ".stdout"); var stderr = Path.Combine(work, count + "-" + id + ".stderr");
            await File.WriteAllTextAsync(stdout, result.Stdout, utf8); await File.WriteAllTextAsync(stderr, result.Stderr, utf8);
            check("folder optimizer input/filter/list/sentinel " + count + " " + id, before.All(pair => after[pair.Key] == pair.Value), filter);
            check("folder optimizer streams " + count + " " + id, rejected ? result.Stdout.Length == 0 && result.Stderr.Length > 0 : result.Stderr.Length == 0, result.Stderr);
            observations.Add(new
            {
                id, count, dataset, expression, filter, roots, arguments, list, exitCode = result.ExitCode, stdout, stderr,
                stdoutSha256 = Hash(stdout), stderrSha256 = Hash(stderr), protectedBefore = before, protectedAfter = after,
                pid = result.Pid, creationUtc = result.CreationUtc, launchUtc = result.LaunchUtc, exitObservedUtc = result.ExitObservedUtc,
                durationMilliseconds = result.DurationMilliseconds, platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "other",
                windows = OperatingSystem.IsWindows(), launchEvidence = result.LaunchEvidence,
                actualTypedValueObserved = false, nativeFullAstEquivalent = false, nativeExceptionEquivalent = false, compilePhaseObserved = false, listReadInstrumented = false
            });
        }
        var actual = Path.Combine(work, "public-observations.json");
        await File.WriteAllBytesAsync(actual, JsonSerializer.SerializeToUtf8Bytes(observations));
        await Reader("public-reader", "--actual", actual);
        await FolderLexerScenarios.RunAsync(fixtures, oracle, run, check);
        skip("folder optimizer remaining host evidence", "CLI consumerは型・AST・compile phase・list read回数・2回目の変異後評価を直接観測しません。required-host-checks.jsonの実App headless検証は別資格です。");
    }
}
