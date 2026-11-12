using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class FolderSideFunctionScenarios
{
    internal static async Task RunAsync(string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check, Action<string, string> skip, string python)
    {
        // 外部候補のfixtureと原典repoは別経路で解決する。
        var relative = Path.Combine("tests", "Fixtures", "FolderSideFunctions");
        var oracle = new[] { Path.Combine(AppContext.BaseDirectory, relative), Path.GetFullPath(relative) }
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "native-proof.zip")) && File.Exists(Path.Combine(path, "verify.py")))
            ?? throw new DirectoryNotFoundException("Folder side-functions の固定原本が見つかりません。");
        var sourceOracle = Path.GetFullPath("tests/Fixtures/LineTransformAggregates");
        var repository = Path.GetFullPath(Path.Combine(sourceOracle, "..", "..", ".."));
        if (!File.Exists(Path.Combine(repository, "DiffBeacon.slnx")) || !File.Exists(Path.Combine(sourceOracle, "source-manifest.json")))
            throw new DirectoryNotFoundException("原典repoを解決できません。E2Eはrepo rootから実行してください。");
        var work = Path.Combine(fixtures, "folder-properties", "side-functions");
        Directory.CreateDirectory(work);
        async Task Reader(string id, params string[] arguments)
        {
            var info = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-E", "-B", "-X", "utf8", Path.Combine(oracle, "verify.py"), "--repo-root", repository }.Concat(arguments)) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new IOException("side-functions独立readerを起動できません。");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await stdout; var error = await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, id + ".stdout"), output, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(work, id + ".stderr"), error, new UTF8Encoding(false));
            check("folder side-functions independent " + id, process.ExitCode == 0 && error.Length == 0, output + error);
            if (process.ExitCode != 0 || error.Length != 0) throw new InvalidDataException("side-functions独立readerが不合格です: " + output + error);
        }
        await Reader("native-proof");
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
            foreach (var item in plan.RootElement.EnumerateArray().Where(item => item.GetProperty("count").GetInt32() == count))
            {
                var id = item.GetProperty("id").GetString()!;
                var expression = item.GetProperty("expression").GetString()!
                    .Replace("__EMPTY_LIST__", Quote(emptyList), StringComparison.Ordinal)
                    .Replace("__UNREGISTERED_LIST__", Quote(unregisteredList), StringComparison.Ordinal);
                var rule = item.GetProperty("rule").GetString()!;
                var propertyPreparationRejected = item.GetProperty("windowsPropertyPreparation").GetBoolean() && !OperatingSystem.IsWindows();
                var rejected = item.GetProperty("rejected").GetBoolean() || propertyPreparationRejected;
                var selected = item.GetProperty("selected").GetBoolean();
                var expectedExit = rejected ? 2 : selected && rule is "e" or "fe" ? 1 : 0;
                var filter = Path.Combine(work, count + "-" + id + ".flt");
                await File.WriteAllTextAsync(filter, "def: exclude\n" + rule + ": " + expression + "\n", utf8);
                List<string> arguments = ["--folder", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                var protectedPaths = roots.Select(root => Path.Combine(root, "row.bin")).Concat([filter, emptyList, sentinel]).ToArray();
                var before = protectedPaths.ToDictionary(path => path, Hash, StringComparer.Ordinal);
                var result = await run("folder-side-functions-" + count + "-" + id, expectedExit, !rejected, arguments.ToArray());
                var after = protectedPaths.ToDictionary(path => path, Hash, StringComparer.Ordinal);
                var stdout = Path.Combine(work, count + "-" + id + ".stdout"); var stderr = Path.Combine(work, count + "-" + id + ".stderr");
                await File.WriteAllTextAsync(stdout, result.Stdout, utf8);
                await File.WriteAllTextAsync(stderr, result.Stderr, utf8);
                check("folder side-functions exit " + count + " " + id, result.ExitCode == expectedExit, result.Stderr);
                check("folder side-functions input and existing sentinel " + count + " " + id,
                    before.All(pair => after[pair.Key] == pair.Value) && !File.Exists(unregisteredList), "Folder CLI has no output-file option; sentinel retention is checked separately.");
                if (rejected)
                {
                    var diagnostic = item.GetProperty("diagnostic");
                    check("folder side-functions strict rejection " + count + " " + id,
                        result.Stdout.Length == 0 && result.Stderr.Length > 0
                        && (propertyPreparationRejected ? result.Stderr.Contains("Windows", StringComparison.Ordinal)
                            : diagnostic.ValueKind == JsonValueKind.Null || result.Stderr.Contains(diagnostic.GetString()!, StringComparison.Ordinal)), result.Stderr);
                }
                else
                {
                    var tokens = item.GetProperty("expectedLogTokens").EnumerateArray().Select(token => token.GetString()!).ToArray();
                    var lines = result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    check("folder side-functions runtime logs " + count + " " + id,
                        lines.Length == tokens.Length && lines.Zip(tokens).All(pair => pair.First.StartsWith("info: " + pair.Second + " ", StringComparison.Ordinal)), result.Stderr);
                    if (result.ExitCode == expectedExit)
                    {
                        using var model = JsonDocument.Parse(result.Stdout);
                        var rows = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                        var names = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                        check("folder side-functions rows and sides " + count + " " + id,
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
                    protectedBefore = before, protectedAfter = after, stdoutSha256 = Hash(stdout), stderrSha256 = Hash(stderr),
                    pid = result.Pid, creationUtc = result.CreationUtc, launchUtc = result.LaunchUtc, exitObservedUtc = result.ExitObservedUtc, durationMilliseconds = result.DurationMilliseconds,
                    platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : "other", launchEvidence = result.LaunchEvidence,
                    actualTypedValueObserved = false, nativeFullAstEquivalent = false, nativeExceptionEquivalent = false,
                    propertyReadInstrumented = false, listReadInstrumented = false, windows = OperatingSystem.IsWindows() });
            }
        }
        var actual = Path.Combine(work, "public-observations.json");
        await File.WriteAllBytesAsync(actual, JsonSerializer.SerializeToUtf8Bytes(observations));
        await Reader("public-reader", "--actual", actual);
        skip("folder side-functions unobserved boundaries", "No public internal-phase observation, native literal cache trace, lexer/depth/cancellation injection, or provider-present execution. Required child-Optimize fold gap is retained in required-unimplemented.json and is not counted as passed.");
        await RunChildOptimizeFoldAsync(oracle, work, run, check, utf8);
    }

    // 固定原本の required-unimplemented.json に残した子Optimize foldを、固定原本を書き換えずに別checkで検証する。
    // 期待値の根拠は原典 Src/FilterEngine/FilterExpressionNodes.cpp の FunctionNode::Optimize。
    // 子を先にOptimizeしてSubstr('0',0)をStringLiteral "0"へ畳み、外側ColumnRangeがliteral列指定を
    // ParseColumnRangesで準備する時点で "invalid column specification" を投げる。LineAtのprovider guardより前の拒否である。
    // @noopt（native Optimize=false）ではprovider不在のLineAtが子を評価しないため拒否されない。
    // 製品出力から期待値を作らず、固定原本の式文字列だけを入力として読む。
    private static async Task RunChildOptimizeFoldAsync(string oracle, string work, Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check, UTF8Encoding utf8)
    {
        using var gaps = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(oracle, "required-unimplemented.json")));
        var gap = gaps.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetString() == "child-optimize-fold-before-provider-guard");
        const string optPrefix = "@opt ";
        var fixedExpression = gap.GetProperty("expression").GetString()!;
        if (!fixedExpression.StartsWith(optPrefix, StringComparison.Ordinal)) throw new InvalidDataException("子fold条件の固定式が@optで始まりません。");
        var body = fixedExpression[optPrefix.Length..];
        // (id, 式, 拒否されるか)。既定（接頭辞なし）はnativeの既定Optimize=trueと同じく拒否される。
        // 対照の Substr('1',0) は有効な列指定 "1" へ畳まれるため、@optでも拒否されない。
        var cases = new (string Id, string Expression, bool Rejected)[]
        {
            ("opt-fixed", fixedExpression, true),
            ("default", body, true),
            ("noopt", "@noopt " + body, false),
            ("opt-valid-control", optPrefix + body.Replace("Substr('0',0)", "Substr('1',0)", StringComparison.Ordinal), false),
        };
        if (cases[^1].Expression == fixedExpression) throw new InvalidDataException("対照式を固定式から作れませんでした。");
        var root = Path.Combine(work, "child-optimize-fold");
        foreach (var count in new[] { 2, 3 })
        {
            var roots = Enumerable.Range(0, count).Select(side => Path.Combine(root, "sides-" + count, "side-" + side)).ToArray();
            foreach (var (path, side) in roots.Select((path, side) => (path, side)))
            {
                Directory.CreateDirectory(Path.Combine(path, "row.dir"));
                await File.WriteAllBytesAsync(Path.Combine(path, "row.bin"), Enumerable.Repeat((byte)(side + 1), side + 1).ToArray());
            }
            foreach (var (id, expression, rejected) in cases)
            {
                var filter = Path.Combine(root, count + "-" + id + ".flt");
                await File.WriteAllTextAsync(filter, "def: exclude\nfe: " + expression + "\n", utf8);
                List<string> arguments = ["--folder", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                var protectedPaths = roots.Select(path => Path.Combine(path, "row.bin")).Append(filter).ToArray();
                var before = protectedPaths.ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
                // provider不在のLineAtはNoneを返すため、受理時も行は選択されず終了コード0になる。
                var expectedExit = rejected ? 2 : 0;
                var result = await run("folder-side-functions-child-fold-" + count + "-" + id, expectedExit, !rejected, arguments.ToArray());
                await File.WriteAllTextAsync(Path.Combine(root, count + "-" + id + ".stdout"), result.Stdout, utf8);
                await File.WriteAllTextAsync(Path.Combine(root, count + "-" + id + ".stderr"), result.Stderr, utf8);
                var unchanged = before.All(pair => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value);
                check("folder side-functions child fold exit " + count + " " + id, result.ExitCode == expectedExit, result.Stderr);
                check("folder side-functions child fold inputs " + count + " " + id, unchanged, "入力とフィルターのSHAが変化しました。");
                if (rejected)
                {
                    check("folder side-functions child fold rejection " + count + " " + id,
                        result.Stdout.Length == 0 && result.Stderr.Contains("invalid column specification", StringComparison.Ordinal), result.Stderr);
                    continue;
                }
                var accepted = result.ExitCode == 0 && result.Stderr.Length == 0;
                if (accepted)
                {
                    using var model = JsonDocument.Parse(result.Stdout);
                    var rows = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    // fe規則が選ばれないため、def: excludeにより全行が除外表示になる。
                    accepted = rows.Length == 2 && rows.All(row => row.GetProperty("filtered").GetBoolean());
                }
                check("folder side-functions child fold acceptance " + count + " " + id, accepted, result.Stdout + result.Stderr);
            }
        }
    }
}
