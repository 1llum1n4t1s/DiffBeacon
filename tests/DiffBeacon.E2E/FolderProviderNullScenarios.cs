using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class FolderProviderNullScenarios
{
    internal static async Task RunAsync(string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check, Action<string, string> skip, string python)
    {
        // 外部候補のfixtureと原典repoは別経路で解決する。
        var relative = Path.Combine("tests", "Fixtures", "FolderProviderNull");
        var oracle = new[] { Path.Combine(AppContext.BaseDirectory, relative), Path.GetFullPath(relative) }
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "native-folder-null-proof.zip")) && File.Exists(Path.Combine(path, "verify.py")))
            ?? throw new DirectoryNotFoundException("Folder provider-null の固定原本が見つかりません。");
        var sourceOracle = Path.GetFullPath("tests/Fixtures/LineTransformAggregates");
        var repository = Path.GetFullPath(Path.Combine(sourceOracle, "..", "..", ".."));
        if (!File.Exists(Path.Combine(repository, "DiffBeacon.slnx")) || !File.Exists(Path.Combine(sourceOracle, "source-manifest.json")))
            throw new DirectoryNotFoundException("原典repoを解決できません。E2Eはrepo rootから実行してください。");
        var work = Path.Combine(fixtures, "folder-properties", "provider-null");
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos"
            : throw new PlatformNotSupportedException("provider-nullの実行証拠はWindows/macOS用です。");
        Directory.CreateDirectory(work);
        async Task Reader(string id, params string[] arguments)
        {
            var info = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-E", "-B", "-X", "utf8", Path.Combine(oracle, "verify.py"), "--repo-root", repository, "--platform", platform }.Concat(arguments)) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new IOException("provider-null独立readerを起動できません。");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await stdout; var error = await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, id + ".stdout"), output, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(work, id + ".stderr"), error, new UTF8Encoding(false));
            check("folder provider-null independent " + id, process.ExitCode == 0 && error.Length == 0, output + error);
            if (process.ExitCode != 0 || error.Length != 0) throw new InvalidDataException("provider-null独立readerが不合格です: " + output + error);
        }
        await Reader("native-proof");
        using var archive = ZipFile.OpenRead(Path.Combine(oracle, "native-folder-null-proof.zip"));
        using (var stream = (archive.GetEntry("functions.json") ?? throw new InvalidDataException("functions.json")).Open())
        using (var functions = await JsonDocument.ParseAsync(stream))
            check("folder provider-null canonical registrations", functions.RootElement.GetArrayLength() == 15, "Names and arities come from retained native functions.json.");
        using var plan = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(oracle, "public-plan.json")));
        var utf8 = new UTF8Encoding(false, true);
        static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        static string Quote(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
        var emptyList = Path.Combine(work, "empty-search.tsv");
        await File.WriteAllBytesAsync(emptyList, [9, 88, 10]);
        var unregisteredList = Path.Combine(work, "not-registered-and-absent.tsv");
        if (File.Exists(unregisteredList)) throw new IOException("未登録listの検証パスが既に存在します。");
        var sentinel = Path.Combine(work, "existing-output.sentinel");
        await File.WriteAllTextAsync(sentinel, "existing output bytes remain unchanged\n", utf8);
        var observations = new List<object>();
        foreach (var count in new[] { 2, 3 })
        {
            var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "sides-" + count, "side-" + side)).ToArray();
            foreach (var (root, side) in roots.Select((root, side) => (root, side)))
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(Path.Combine(root, "row.dir"));
                await File.WriteAllBytesAsync(Path.Combine(root, "row.bin"), Enumerable.Repeat((byte)(side + 1), side + 1).ToArray());
            }
            foreach (var item in plan.RootElement.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var expression = item.GetProperty("expression").GetString()!
                    .Replace("__EMPTY_LIST__", Quote(emptyList), StringComparison.Ordinal)
                    .Replace("__UNREGISTERED_LIST__", Quote(unregisteredList), StringComparison.Ordinal);
                var rule = item.GetProperty("rule").GetString()!;
                var directive = item.GetProperty("optimizationDirective").GetString()!;
                if (directive != "@noopt" || item.GetProperty("nativeOptimizedPhaseQualified").GetBoolean())
                    throw new InvalidDataException("provider-nullの公開投影はnative function-body / @noopt に限定します。");
                var propertyPreparationRejected = item.GetProperty("windowsPropertyPreparation").GetBoolean() && !OperatingSystem.IsWindows();
                var rejected = item.GetProperty("rejected").GetBoolean() || propertyPreparationRejected;
                var selected = item.GetProperty("selected").GetBoolean();
                var expectedExit = rejected ? 2 : selected && rule is "e" or "fe" ? 1 : 0;
                var filter = Path.Combine(work, count + "-" + id + ".flt");
                await File.WriteAllTextAsync(filter, "def: exclude\n" + rule + ": " + directive + " " + expression + "\n", utf8);
                List<string> arguments = ["--folder", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                var protectedPaths = roots.Select(root => Path.Combine(root, "row.bin")).Concat([filter, emptyList, sentinel]).ToArray();
                var before = protectedPaths.ToDictionary(path => path, Hash, StringComparer.Ordinal);
                var result = await run("folder-provider-null-" + count + "-" + id, expectedExit, !rejected, arguments.ToArray());
                var after = protectedPaths.ToDictionary(path => path, Hash, StringComparer.Ordinal);
                var stdout = Path.Combine(work, count + "-" + id + ".stdout"); var stderr = Path.Combine(work, count + "-" + id + ".stderr");
                await File.WriteAllTextAsync(stdout, result.Stdout, utf8);
                await File.WriteAllTextAsync(stderr, result.Stderr, utf8);
                check("folder provider-null exit " + count + " " + id, result.ExitCode == expectedExit, result.Stderr);
                check("folder provider-null input and existing sentinel " + count + " " + id,
                    before.All(pair => after[pair.Key] == pair.Value) && !File.Exists(unregisteredList), "Folder CLI has no output-file option; sentinel retention is checked separately.");
                if (rejected)
                {
                    var diagnostic = item.GetProperty("diagnostic");
                    check("folder provider-null strict rejection " + count + " " + id,
                        result.Stdout.Length == 0 && result.Stderr.Length > 0
                        && (propertyPreparationRejected ? result.Stderr.Contains("Windows", StringComparison.Ordinal)
                            : diagnostic.ValueKind == JsonValueKind.Null || result.Stderr.Contains(diagnostic.GetString()!, StringComparison.Ordinal)), result.Stderr);
                }
                else
                {
                    var logCount = item.GetProperty("expectedLogCount").GetInt32();
                    var lines = result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    check("folder provider-null runtime logs " + count + " " + id,
                        lines.Length == logCount && lines.All(line => line.StartsWith("info: provider-null-outer ", StringComparison.Ordinal))
                        && !result.Stderr.Contains("provider-null-inner", StringComparison.Ordinal), result.Stderr);
                    if (result.ExitCode == expectedExit)
                    {
                        using var model = JsonDocument.Parse(result.Stdout);
                        var rows = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                        var names = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                        check("folder provider-null rows and sides " + count + " " + id,
                            model.RootElement.GetProperty("allEntryCount").GetInt32() == 2 && rows.Length == 2
                            && rows.Select(row => row.GetProperty("path").GetString()).Order().SequenceEqual(new[] { "row.bin", "row.dir" })
                            && rows.All(row =>
                            {
                                var isFile = row.GetProperty("path").GetString() == "row.bin";
                                var filtered = !selected || (isFile ? rule == "de" : rule == "fe");
                                return row.GetProperty("filtered").GetBoolean() == filtered && names.All(name =>
                                    row.TryGetProperty(name, out var side) && side.ValueKind == JsonValueKind.Object && side.GetProperty("filtered").GetBoolean() == filtered);
                            }), result.Stdout);
                    }
                }
                observations.Add(new { id, count, expression, filter, roots, arguments, exitCode = result.ExitCode, stdout, stderr,
                    optimizationDirective = directive, nativePhaseQualification = "native-function-body-only",
                    nativeOptimizedPhaseQualified = false, nativeDefaultOptimizationQualified = false,
                    protectedBefore = before, protectedAfter = after, stdoutSha256 = Hash(stdout), stderrSha256 = Hash(stderr),
                    pid = result.Pid, creationUtc = result.CreationUtc, launchEvidence = result.LaunchEvidence,
                    launchUtc = result.LaunchUtc, exitObservedUtc = result.ExitObservedUtc, durationMs = result.DurationMilliseconds, platform,
                    actualTypedValueObserved = false, nativeFullAstEquivalent = false, nativeExceptionEquivalent = false,
                    propertyReadInstrumented = false, listReadInstrumented = false, windows = OperatingSystem.IsWindows() });
            }
        }
        var actual = Path.Combine(work, "public-observations.json");
        await File.WriteAllBytesAsync(actual, JsonSerializer.SerializeToUtf8Bytes(observations));
        await Reader("public-reader", "--actual", actual);
        skip("folder provider-null lexer/cancellation injection", "No public CLI injection for lexer/depth budgets or cancellation; shared 65,536 evaluation budget is unchanged. Native full constructor and child Optimize remain source-only.");
    }
}
