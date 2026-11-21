using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

internal static class FolderSupplementaryCaseFoldScenarios
{
    internal static async Task RunAsync(string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check, string python)
    {
        // 外部候補のfixtureはbuild outputへ明示配置される。原典repoとは別に解決する。
        var relative = Path.Combine("tests", "Fixtures", "FolderSupplementaryCaseFold");
        var oracle = new[] { Path.Combine(AppContext.BaseDirectory, relative), Path.GetFullPath(relative) }
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "native-casefold-proof.zip")) && File.Exists(Path.Combine(path, "verify.py")))
            ?? throw new DirectoryNotFoundException("補助面casefoldの固定原本が見つかりません。");
        var sourceOracle = Path.GetFullPath("tests/Fixtures/LineTransformReplaceLists");
        var repository = Path.GetFullPath(Path.Combine(sourceOracle, "..", "..", ".."));
        if (!File.Exists(Path.Combine(repository, "DiffBeacon.slnx")) || !File.Exists(Path.Combine(sourceOracle, "source-manifest.json")))
            throw new DirectoryNotFoundException("原典repoを解決できません。E2Eはrepo rootから実行してください。");
        var work = Path.Combine(fixtures, "folder-properties", "supplementary-casefold");
        Directory.CreateDirectory(work);
        async Task Reader(string id, params string[] arguments)
        {
            var info = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(oracle, "verify.py"), "--repo-root", repository, "--platform", OperatingSystem.IsWindows() ? "windows" : "macos" }.Concat(arguments)) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new IOException("casefold独立readerを起動できません。");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await stdout; var error = await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, id + ".stdout"), output, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(work, id + ".stderr"), error, new UTF8Encoding(false));
            check("folder supplementary casefold independent " + id, process.ExitCode == 0 && error.Length == 0, output + error);
            if (process.ExitCode != 0 || error.Length != 0) throw new InvalidDataException("casefold独立readerが不合格です: " + output + error);
        }
        await Reader("native-proof");
        // readerが固定SHA/entry/CRCを検証したcompile source payloadを使う。実binaryの認証は外側chainが担う。
        using var managedSource = ZipFile.OpenRead(Path.Combine(oracle, "managed-runner-source.zip"));
        using var managedProgram = (managedSource.GetEntry("Program.cs") ?? throw new InvalidDataException("固定Program sourceがありません。")).Open();
        var runnerSourceSha256 = Convert.ToHexStringLower(SHA256.HashData(managedProgram));
        using var archive = ZipFile.OpenRead(Path.Combine(oracle, "native-casefold-proof.zip"));
        string Entry(string name)
        {
            using var stream = (archive.GetEntry(name) ?? throw new InvalidDataException(name)).Open();
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true)); return reader.ReadToEnd();
        }
        using var casesDocument = JsonDocument.Parse(Entry("cases.json"));
        var cases = casesDocument.RootElement.EnumerateArray().ToDictionary(item => item.GetProperty("id").GetString()!, item => item.Clone(), StringComparer.Ordinal);
        var golden = Entry("run1-golden.ndjson").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);
        using var derivedDocument = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(oracle, "derived-cases.json")));
        static string Decode(string hex) => new UTF8Encoding(false, true).GetString(Convert.FromHexString(hex));
        static string Quote(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
        static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        static string[] Scalars(string value) => value.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
        var observations = new List<object>(); var preserved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var count in new[] { 2, 3 })
        {
            var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "sides-" + count, "side-" + side)).ToArray();
            foreach (var (root, side) in roots.Select((root, side) => (root, side)))
            {
                Directory.CreateDirectory(root); var path = Path.Combine(root, "row.bin");
                await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)(side + 1), side + 1).ToArray()); preserved[path] = Hash(path);
            }
            async Task Compare(string id, string expression, string scope, string[] nativeIds, string? direction = null,
                int start = 0, int length = 0, string? listPath = null)
            {
                var filter = Path.Combine(work, count + "-" + id + ".flt");
                await File.WriteAllTextAsync(filter, "def: exclude\nfe: " + expression + "\n", new UTF8Encoding(false)); preserved[filter] = Hash(filter);
                List<string> arguments = ["--folder", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                var protectedPaths = roots.Select(root => Path.Combine(root, "row.bin")).Append(filter).Concat(listPath is null ? [] : new[] { listPath }).ToArray();
                var before = protectedPaths.ToDictionary(path => path, Hash, StringComparer.Ordinal);
                var result = await run("folder-supplementary-casefold-" + count + "-" + id, 1, true, arguments.ToArray());
                var after = protectedPaths.ToDictionary(path => path, Hash, StringComparer.Ordinal);
                var stdout = Path.Combine(work, count + "-" + id + ".stdout"); var stderr = Path.Combine(work, count + "-" + id + ".stderr");
                await File.WriteAllTextAsync(stdout, result.Stdout, new UTF8Encoding(false));
                await File.WriteAllTextAsync(stderr, result.Stderr, new UTF8Encoding(false));
                check("folder supplementary casefold streams " + count + " " + id, result.ExitCode == 1 && result.Stderr.Length == 0, result.Stderr);
                check("folder supplementary casefold input retention " + count + " " + id, before.All(pair => after[pair.Key] == pair.Value), id);
                if (result.ExitCode == 1)
                {
                    using var model = JsonDocument.Parse(result.Stdout); var rows = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    var names = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                    check("folder supplementary casefold row and sides " + count + " " + id, model.RootElement.GetProperty("allEntryCount").GetInt32() == 1
                        && rows.Length == 1 && rows[0].GetProperty("path").GetString() == "row.bin" && !rows[0].GetProperty("filtered").GetBoolean()
                        && names.All(name => rows[0].TryGetProperty(name, out var side) && side.ValueKind == JsonValueKind.Object && !side.GetProperty("filtered").GetBoolean()), result.Stdout);
                }
                observations.Add(new { id, count, expression, scope, nativeIds, direction, start, length, listPath, filter, roots,
                    arguments, exitCode = result.ExitCode, stdout, stderr, protectedBefore = before, protectedAfter = after,
                    stdoutSha256 = Hash(stdout), stderrSha256 = Hash(stderr),
                    pid = result.Pid, creationUtc = result.CreationUtc, launchUtc = result.LaunchUtc, exitObservedUtc = result.ExitObservedUtc,
                    durationMs = result.DurationMilliseconds, platform = OperatingSystem.IsWindows() ? "windows" : "macos", launchEvidence = result.LaunchEvidence });
            }
            foreach (var direction in new[] { "low-needle", "high-needle" })
            foreach (var cs in new[] { false, true })
            {
                var suffix = cs ? "cs" : "ci"; var directive = cs ? "@noopt @cs " : "@noopt @ci ";
                var listId = "replacewithlist-" + direction + "-" + suffix;
                var listCase = cases[listId]; var listGolden = golden[listId];
                var listLines = Decode(listCase.GetProperty("fileUtf8Hex").GetString()!).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var source = Scalars(Decode(listCase.GetProperty("sourceUtf8Hex").GetString()!));
                var expected = Scalars(Decode(listGolden.GetProperty("serializedUtf8Hex").GetString()!));
                var nativeIds = listLines.Select(line => "replace-" + suffix + "-u" + char.ConvertToUtf32(line, 0).ToString("x", CultureInfo.InvariantCulture)).ToArray();
                var calls = nativeIds.Select(id => "Replace(" + Quote(Decode(cases[id].GetProperty("sourceUtf8Hex").GetString()!)) + ","
                    + Quote(Decode(cases[id].GetProperty("needleUtf8Hex").GetString()!)) + ",'X')");
                var directExpected = nativeIds.Select(id => Quote(Decode(golden[id].GetProperty("serializedUtf8Hex").GetString()!)));
                // 既存512 token上限を保ち、全native行を重複なく16要素ずつ投影する。
                for (var start = 0; start < source.Length; start += 16)
                {
                    var length = Math.Min(16, source.Length - start);
                    await Compare("direct-" + direction + "-" + suffix + "-" + start.ToString("D3", CultureInfo.InvariantCulture),
                        directive + "AllEqual(Array(Array(" + string.Join(',', calls.Skip(start).Take(length)) + "),Array(" + string.Join(',', directExpected.Skip(start).Take(length)) + ")))",
                        "native-direct-array-projection", nativeIds.Skip(start).Take(length).ToArray(), direction, start, length);
                }
                // 282規則ではscalar検査だけで79,524課金になる。予算を維持して16規則へ投影する。
                for (var start = 0; start < source.Length; start += 16)
                {
                    var length = Math.Min(16, source.Length - start);
                    var listPath = Path.Combine(work, direction + "-" + start.ToString("D3", CultureInfo.InvariantCulture) + ".tsv");
                    await File.WriteAllTextAsync(listPath, string.Join('\n', listLines.Skip(start).Take(length)) + "\n", new UTF8Encoding(false)); preserved[listPath] = Hash(listPath);
                    var expression = directive + "AllEqual(Array(ReplaceWithList(" + Quote(string.Concat(source.Skip(start).Take(length))) + "," + Quote(listPath) + ")," + Quote(string.Concat(expected.Skip(start).Take(length))) + "))";
                    await Compare("list-" + direction + "-" + suffix + "-" + start.ToString("D3", CultureInfo.InvariantCulture), expression,
                        "native-list-scalar-slice-projection", [listId], direction, start, length, listPath);
                }
            }
            foreach (var item in derivedDocument.RootElement.EnumerateArray())
                await Compare(item.GetProperty("id").GetString()!, item.GetProperty("expression").GetString()!, "derived-shared-backend-control", []);
        }
        var inventory = Path.Combine(work, "public-observations.json");
        await File.WriteAllTextAsync(inventory, JsonSerializer.Serialize(new { observations, preserved, nativeRowsPerChild = 1132,
            productProjectionNotNativeInvocation = true, runnerSourceSha256, publicDirectChunkSize = 16, fullListScalarValidationLowerBound = 79524, publicListChunkSize = 16 }), new UTF8Encoding(false));
        await Reader("public-projection", "--public-observations", inventory);
    }
}
