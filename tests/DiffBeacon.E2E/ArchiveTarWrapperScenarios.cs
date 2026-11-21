using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ArchiveTarWrapperScenarios
{
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check, string python)
    {
        var root = Path.GetFullPath("tests/Fixtures/Archives/TarWrappers");
        var work = Path.Combine(fixtures, "archive-tar-wrappers");
        Directory.CreateDirectory(work);
        var originals = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, Hash);
        var manifest = Path.Combine(root, "manifest.json");
        check("tar wrapper fixed manifest SHA", Hash(manifest) == "40735306000EFE828BBA5C2348C8FCB0CB4D66215A6B03AB257547EED493ADB4", "");
        await Independent("before", null);
        var gui = Path.Combine(work, "gui");
        await run("tar-wrapper-gui", 0, false, ["--self-test", gui, "--tar-wrapper-gui-only"]);
        var guiFacts = Directory.Exists(gui) ? Directory.GetFiles(gui, "facts.json", SearchOption.AllDirectories) : [];
        check("tar wrapper GUI one complete proof", guiFacts.Length == 1, "actual completed GUI evidence required");
        if (guiFacts.Length == 1)
        {
            var readerInfo = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(root, "verify-gui.py"), guiFacts[0] }) readerInfo.ArgumentList.Add(argument);
            using var reader = Process.Start(readerInfo)!;
            var stdout = reader.StandardOutput.ReadToEndAsync(); var stderr = reader.StandardError.ReadToEndAsync();
            await reader.WaitForExitAsync(); var text = await stdout; var errors = await stderr;
            File.WriteAllText(Path.Combine(work, "gui-independent.stdout.txt"), text); File.WriteAllText(Path.Combine(work, "gui-independent.stderr.txt"), errors);
            check("tar wrapper GUI independent all 22 cases", reader.ExitCode == 0, text + errors);
        }
        using var golden = JsonDocument.Parse(File.ReadAllBytes(manifest));
        var cases = golden.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var evidence = new List<object>();
        var raw = Path.GetFullPath(Path.Combine(root, "../TarZ/small_repeated.tar"));
        var rawHash = Hash(raw);
        var empty = Path.Combine(work, "empty-reference.tar");
        File.WriteAllBytes(empty, new byte[10240]);
        foreach (var item in cases)
        {
            var name = item.GetProperty("name").GetString()!;
            var path = Path.Combine(root, name);
            check("tar wrapper original SHA " + name, Hash(path) == item.GetProperty("sha256").GetString()
                && new FileInfo(path).Length == item.GetProperty("bytes").GetInt64(), "");
            var valid = item.GetProperty("valid").GetBoolean();
            var list = await run("tar-wrapper-list-" + name, valid ? 0 : 2, valid, ["--archive-list", path]);
            if (!valid)
            {
                Rejected("tar wrapper list " + name, list);
                foreach (var provider in new[] { "archive", "tar", "tar-metadata" })
                    Rejected("tar wrapper provider " + provider + name,
                        await run("tar-wrapper-reject-provider-" + provider + "-" + name, 2, false, ["--provider", provider, path, raw]));
                await RejectOutputs(name, path, null);
                continue;
            }
            if (list.ExitCode != 0) continue;
            VerifyList(name, list, item, true);
            var reference = item.GetProperty("entries").GetArrayLength() == 0 ? empty : raw;
            await run("tar-wrapper-compare-" + name, 0, true, ["--archive-compare", path, reference]);
            foreach (var provider in new[] { "archive", "tar", "tar-metadata" })
            {
                var comparison = await run("tar-wrapper-provider-" + provider + "-" + name, 0, true, ["--provider", provider, path, reference]);
                if (comparison.ExitCode == 0)
                {
                    using var result = JsonDocument.Parse(comparison.Stdout);
                    check("tar wrapper provider semantic equality " + provider + name, !result.RootElement.GetProperty("different").GetBoolean(), "independently verified full TAR reference");
                }
            }
            var exports = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in item.GetProperty("entries").EnumerateArray().Where(entry => !entry.GetProperty("directory").GetBoolean()))
            {
                var entryName = entry.GetProperty("path").GetString()!;
                var destination = Path.Combine(work, name + "-entry-" + exports.Count + ".bin");
                await run("tar-wrapper-entry-" + name + "-" + exports.Count, 0, true, ["--archive-entry", path, entryName, destination]);
                VerifyFile(name + "/" + entryName, destination, entry);
                exports.Add(entryName, destination);
            }
            var extracted = Path.Combine(work, name + "-extracted");
            await run("tar-wrapper-extract-" + name, 0, true, ["--archive-extract", path, extracted]);
            var repacked = Path.Combine(work, name + "-repacked.zip");
            await run("tar-wrapper-repack-" + name, 0, true, ["--archive-repack", path, repacked]);
            var repackedList = await run("tar-wrapper-repack-list-" + name, 0, true, ["--archive-list", repacked]);
            if (repackedList.ExitCode == 0) VerifyList("repacked-" + name, repackedList, item, false);
            var descriptor = Descriptor("root-" + name, path, [], item.GetProperty("sha256").GetString());
            var typed = await run("tar-wrapper-root-" + name, 0, true, ["--archive-source-list", descriptor]);
            if (typed.ExitCode == 0) VerifyList("typed-root-" + name, typed, item, true, requireTime: true);
            var typedExports = await SourceExports("root-" + name, descriptor, item);
            evidence.Add(new { kind = "physical", caseName = name, list = JsonSerializer.Deserialize<JsonElement>(list.Stdout), exports, extracted, repacked,
                typedList = typed.ExitCode == 0 ? JsonSerializer.Deserialize<JsonElement>(typed.Stdout) : (JsonElement?)null, typedExports });
        }

        foreach (var source in golden.RootElement.GetProperty("sources").EnumerateArray())
        {
            var name = source.GetProperty("name").GetString()!;
            var fixture = source.GetProperty("fixture").GetString()!;
            var expected = cases.Single(item => item.GetProperty("name").GetString() == fixture);
            var descriptor = Descriptor(name, Path.Combine(root, name), [source.GetProperty("entry").GetString()!], source.GetProperty("sha256").GetString());
            var valid = expected.GetProperty("valid").GetBoolean() && expected.GetProperty("layers").GetArrayLength() < 8;
            var result = await run("tar-wrapper-source-" + name, valid ? 0 : 2, valid, ["--archive-source-list", descriptor]);
            if (!valid)
            {
                Rejected("tar wrapper shared source refusal " + name, result);
                await RejectOutputs("source-" + name, Path.Combine(root, name), descriptor);
                continue;
            }
            if (result.ExitCode != 0) continue;
            VerifyList("source-" + name, result, expected, true, requireTime: true);
            var exports = await SourceExports(name, descriptor, expected);
            evidence.Add(new { kind = "source", sourceName = name, caseName = fixture, list = JsonSerializer.Deserialize<JsonElement>(result.Stdout), exports });
        }

        var target = Path.Combine(root, "payload.tar.gz.bz2");
        var limits = new Dictionary<string, long>[]
        {
            new() { ["maximumEntryBytes"] = 20479 }, new() { ["maximumDecodedBytes"] = 20480 },
            new() { ["maximumWorkBytes"] = 64 }, new() { ["maximumWrapperDepth"] = 1 },
            new() { ["maximumEntries"] = 2 }, new() { ["maximumPathCharacters"] = 5 }
        };
        for (var index = 0; index < limits.Length; index++)
        {
            var descriptor = Descriptor("budget-" + index, target, [], Hash(target), limits[index]);
            Rejected("tar wrapper budget " + index, await run("tar-wrapper-budget-" + index, 2, false, ["--archive-source-list", descriptor]));
            await RejectOutputs("budget-" + index, target, descriptor);
        }
        var wrongSha = Descriptor("root-sha-mismatch", target, [], new string('0', 64));
        var decodeBudget = Descriptor("decoder-work-budget", target, [], Hash(target), new() { ["maximumWorkBytes"] = 1000 });
        var decodeRefusal = await run("tar-wrapper-decoder-work-budget", 2, false, ["--archive-source-list", decodeBudget]);
        Rejected("tar wrapper decoder work budget", decodeRefusal);
        check("tar wrapper decoder work reached after input SHA", decodeRefusal.Stderr.Contains("アーカイブ作業量の上限を超えました。", StringComparison.Ordinal)
            && !decodeRefusal.Stderr.Contains("入力SHA照合", StringComparison.Ordinal), decodeRefusal.Stderr);
        await RejectOutputs("decoder-work-budget", target, decodeBudget);
        Rejected("tar wrapper source root SHA mismatch", await run("tar-wrapper-root-sha-mismatch", 2, false, ["--archive-source-list", wrongSha]));
        await RejectOutputs("root-sha-mismatch", target, wrongSha);
        var evidencePath = Path.Combine(work, "independent-evidence.json");
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(evidence));
        await Independent("after", evidencePath);
        check("tar wrapper all originals preserved", originals.All(pair => Hash(pair.Key) == pair.Value) && Hash(raw) == rawHash, "all fixture files and raw TAR");
        check("tar wrapper no stage remnants", !Directory.EnumerateFileSystemEntries(work, ".diffbeacon*", SearchOption.AllDirectories).Any(), "");

        async Task<Dictionary<string, string>> SourceExports(string name, string descriptor, JsonElement expected)
        {
            var exports = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in expected.GetProperty("entries").EnumerateArray().Where(entry => !entry.GetProperty("directory").GetBoolean()))
            {
                var entryName = entry.GetProperty("path").GetString()!;
                var destination = Path.Combine(work, "source-entry-" + name + "-" + exports.Count + ".bin");
                await run("tar-wrapper-source-entry-" + name + "-" + exports.Count, 0, true, ["--archive-source-entry", descriptor, entryName, destination]);
                VerifyFile(name + "/" + entryName, destination, entry);
                exports.Add(entryName, destination);
            }
            return exports;
        }

        async Task RejectOutputs(string name, string input, string? descriptor)
        {
            var export = Path.Combine(work, "protected-" + name + ".bin");
            File.WriteAllText(export, "protected TAR wrapper output"); var before = Hash(export); var inputBefore = Hash(input);
            var args = descriptor is null ? new[] { "--archive-entry", input, "data.bin", export } : ["--archive-source-entry", descriptor, "data.bin", export];
            Rejected("tar wrapper rejected export " + name, await run("tar-wrapper-reject-export-" + name, 2, false, args));
            check("tar wrapper export sentinel retained " + name, Hash(export) == before && Hash(input) == inputBefore, "");
            if (descriptor is not null) return;
            var repacked = Path.Combine(work, "protected-" + name + ".zip");
            File.WriteAllText(repacked, "protected repack output"); var repackedBefore = Hash(repacked);
            Rejected("tar wrapper rejected repack " + name, await run("tar-wrapper-reject-repack-" + name, 2, false, ["--archive-repack", input, repacked]));
            var extraction = Path.Combine(work, "rejected-" + name + "-extracted");
            Rejected("tar wrapper rejected extraction " + name, await run("tar-wrapper-reject-extract-" + name, 2, false, ["--archive-extract", input, extraction]));
            check("tar wrapper refusal preserves input/output " + name, Hash(repacked) == repackedBefore && Hash(input) == inputBefore && !Directory.Exists(extraction), "");
            var existing = Path.Combine(work, "protected-extraction-" + name); Directory.CreateDirectory(existing);
            var sentinel = Path.Combine(existing, "sentinel.bin"); File.WriteAllText(sentinel, "protected extraction"); var sentinelBefore = Hash(sentinel);
            await run("tar-wrapper-reject-existing-extract-" + name, 2, false, ["--archive-extract", input, existing]);
            check("tar wrapper extraction sentinel retained " + name, Hash(sentinel) == sentinelBefore && Directory.GetFileSystemEntries(existing).Length == 1, "");
        }

        string Descriptor(string name, string path, string[] chain, string? sha, Dictionary<string, long>? limits = null)
        {
            var descriptor = Path.Combine(work, name + "-descriptor.json");
            var value = new Dictionary<string, object?> { ["rootPath"] = path, ["entryChain"] = chain, ["rootSha256"] = sha };
            if (limits is not null) value["limits"] = limits;
            File.WriteAllText(descriptor, JsonSerializer.Serialize(value));
            return descriptor;
        }

        void Rejected(string name, CommandResult result) => check(name, result.ExitCode == 2 && result.Stdout.Length == 0 && result.Stderr.Length > 0, "no partial success stdout");

        void VerifyFile(string name, string path, JsonElement expected) => check("tar wrapper full export bytes " + name,
            File.Exists(path) && new FileInfo(path).Length == expected.GetProperty("size").GetInt64() && Hash(path) == expected.GetProperty("sha256").GetString(), "");

        void VerifyList(string name, CommandResult result, JsonElement expected, bool wrapped, bool requireTime = false)
        {
            using var list = JsonDocument.Parse(result.Stdout);
            var actual = list.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            var entries = expected.GetProperty("entries").EnumerateArray().ToArray();
            check("tar wrapper full manifest " + name, actual.Length == entries.Length && entries.All(entry => actual.Any(value =>
                value.GetProperty("path").GetString() == entry.GetProperty("path").GetString()
                && value.GetProperty("directory").GetBoolean() == entry.GetProperty("directory").GetBoolean()
                && value.GetProperty("size").GetInt64() == entry.GetProperty("size").GetInt64()
                && value.GetProperty("sha256").GetString() == entry.GetProperty("sha256").GetString()
                && !value.GetProperty("encrypted").GetBoolean()
                && (!requireTime || value.TryGetProperty("modifiedTime", out var time) && time.GetDateTimeOffset().ToUnixTimeSeconds() == entry.GetProperty("mtime").GetInt64()))), "");
            check("tar wrapper exact format " + name, list.RootElement.GetProperty("format").GetString() == (wrapped ? expected.GetProperty("format").GetString() : "zip"), "alias canonical format and layer order");
        }

        async Task Independent(string name, string? evidenceFile)
        {
            var proof = Path.Combine(work, name + "-independent.json");
            var info = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(root, "verify.py"), "--output", proof }) info.ArgumentList.Add(argument);
            if (evidenceFile is not null) { info.ArgumentList.Add("--evidence"); info.ArgumentList.Add(evidenceFile); }
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); var text = await stdout; var errors = await stderr;
            File.WriteAllText(Path.Combine(work, name + "-independent.stdout.txt"), text);
            File.WriteAllText(Path.Combine(work, name + "-independent.stderr.txt"), errors);
            check("tar wrapper independent " + name, process.ExitCode == 0, text + errors);
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
