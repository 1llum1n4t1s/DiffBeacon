using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ArchiveWrapperScenarios
{
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run,
        Func<string, int, bool, string?, string[], Task<CommandResult>> runInput,
        Action<string, bool, string> check, string python, string? reference)
    {
        var root = Path.GetFullPath("tests/Fixtures/Archives/Wrappers");
        var work = Path.Combine(fixtures, "archive-wrappers"); Directory.CreateDirectory(work);
        await VerifyHarnessArguments(work, output, check);
        var originals = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
        check("wrapper fixed manifest SHA", Hash(Path.Combine(root, "manifest.json")) == "A9248C0E0B28068E6031634FEE8E0AEE08940949B641EA427A0817DDA5843632", "");
        using var golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "manifest.json")));
        using var upstream = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "../manifest.json")));
        var entries = golden.RootElement.GetProperty("terminal").GetProperty("entries").EnumerateArray().ToArray();
        var upstreamEntries = upstream.RootElement.GetProperty("expectedEntries").EnumerateArray().ToArray();
        foreach (var item in golden.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!; var path = Path.Combine(root, name);
            check("wrapper fixed original " + name, Hash(path) == item.GetProperty("sha256").GetString() && new FileInfo(path).Length == item.GetProperty("bytes").GetInt64(), "");
            var valid = item.GetProperty("valid").GetBoolean();
            var encrypted = name.Contains("Aes", StringComparison.Ordinal) || name.Contains("encrypted", StringComparison.Ordinal) || name.Contains("WinzipAES", StringComparison.Ordinal);
            var fixturePassword = name.Contains("Aes", StringComparison.Ordinal) ? "testpassword\n" : "test\n";
            var result = await run("wrapper-list-" + name, valid && !encrypted ? 0 : 2, valid && !encrypted, ["--archive-list", path]);
            if (!valid)
            {
                check("wrapper rejection no partial stdout " + name, result.Stdout.Length == 0 && result.Stderr.Length > 0, "");
                var saved = Path.Combine(work, "protected.zip"); File.WriteAllText(saved, "protected output"); var before = Hash(saved);
                var repack = await run("wrapper-reject-repack-" + name, 2, false, ["--archive-repack", path, saved]);
                check("wrapper reject preserves output " + name, Hash(saved) == before && repack.Stdout.Length == 0, "");
                var savedEntry = Path.Combine(work, "protected-entry.bin"); File.WriteAllText(savedEntry, "protected entry"); var entryBefore = Hash(savedEntry);
                var export = await run("wrapper-reject-entry-" + name, 2, false, ["--archive-entry", path, "folder/text.txt", savedEntry]);
                check("wrapper reject preserves entry output " + name, Hash(savedEntry) == entryBefore && export.Stdout.Length == 0, "");
                var badExtract = Path.Combine(work, "reject-" + name + "-extract");
                var extraction = await run("wrapper-reject-extract-" + name, 2, false, ["--archive-extract", path, badExtract]);
                check("wrapper reject publishes no extraction " + name, !Directory.Exists(badExtract) && extraction.Stdout.Length == 0, "");
                continue;
            }
            if (encrypted)
            {
                await runInput("wrapper-wrong-password-" + name, 2, false, "wrong-fixture-password\n", ["--archive-list", path, "--password-stdin"]);
                result = await runInput("wrapper-password-" + name, 0, true, fixturePassword, ["--archive-list", path, "--password-stdin"]);
            }
            if (result.ExitCode != 0) continue;
            using var list = JsonDocument.Parse(result.Stdout);
            var actual = list.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            var ownPayload = item.GetProperty("source").GetString() == "payload.zip";
            var expected = ownPayload ? entries : upstreamEntries;
            check("wrapper all manifest content " + name, expected.All(e => actual.Any(a => a.GetProperty("path").GetString() == e.GetProperty("path").GetString()
                && a.GetProperty("size").GetInt64() == e.GetProperty("size").GetInt64() && a.GetProperty("sha256").GetString() == e.GetProperty("sha256").GetString()))
                && (ownPayload ? actual.Length == entries.Length : actual.Count(a => !a.GetProperty("directory").GetBoolean()) == upstreamEntries.Length), "");
            if (ownPayload)
                check("wrapper format and contained archive leaf " + name, list.RootElement.GetProperty("format").GetString() == "zip" + string.Concat(item.GetProperty("layers").EnumerateArray().Reverse().Select(l => l.GetProperty("wrapper").GetString()))
                    && actual.Any(a => a.GetProperty("path").GetString() == "contained.zip"), "");
            foreach (var entry in expected.Where(e => !e.TryGetProperty("directory", out var d) || !d.GetBoolean()))
            {
                var destination = Path.Combine(work, "entry-" + Guid.NewGuid().ToString("N") + ".bin");
                var arguments = new[] { "--archive-entry", path, entry.GetProperty("path").GetString()!, destination };
                if (encrypted) await runInput("wrapper-entry-" + name, 0, true, fixturePassword, [.. arguments, "--password-stdin"]);
                else await run("wrapper-entry-" + name, 0, true, arguments);
                check("wrapper all exported bytes " + name + "/" + entry.GetProperty("path").GetString(), File.Exists(destination) && Hash(destination) == entry.GetProperty("sha256").GetString(), "");
            }
            var sourcePath = Path.GetFullPath(Path.Combine(root, item.GetProperty("source").GetString()!));
            await RunArchive("wrapper-compare-" + name, ["--archive-compare", path, sourcePath]);
            var repacked = Path.Combine(work, name + ".zip");
            await RunArchive("wrapper-repack-" + name, ["--archive-repack", path, repacked]);
            var repackedList = await run("wrapper-repack-list-" + name, 0, true, ["--archive-list", repacked]);
            using (var repackedJson = JsonDocument.Parse(repackedList.Stdout))
            {
                var repackedEntries = repackedJson.RootElement.GetProperty("entries").EnumerateArray().Where(e => !e.GetProperty("directory").GetBoolean()).ToArray();
                var expectedFiles = expected.Where(e => !e.TryGetProperty("directory", out var d) || !d.GetBoolean()).ToArray();
                check("wrapper all repacked bytes " + name, repackedEntries.Length == expectedFiles.Length && expectedFiles.All(e => repackedEntries.Any(a =>
                    a.GetProperty("path").GetString() == e.GetProperty("path").GetString() && a.GetProperty("size").GetInt64() == e.GetProperty("size").GetInt64()
                    && a.GetProperty("sha256").GetString() == e.GetProperty("sha256").GetString())), "nonencrypted output after fully validated input");
            }
            var extracted = Path.Combine(work, name + "-extracted");
            await RunArchive("wrapper-extract-" + name, ["--archive-extract", path, extracted]);
            var files = expected.Where(e => !e.TryGetProperty("directory", out var d) || !d.GetBoolean()).ToArray();
            check("wrapper all extraction bytes " + name, Directory.GetFiles(extracted, "*", SearchOption.AllDirectories).Length == files.Length && files.All(e =>
                File.Exists(Path.Combine(extracted, e.GetProperty("path").GetString()!)) && Hash(Path.Combine(extracted, e.GetProperty("path").GetString()!)) == e.GetProperty("sha256").GetString()), "");
            Task<CommandResult> RunArchive(string command, string[] arguments) => encrypted
                ? runInput(command, 0, true, arguments[0] == "--archive-compare" ? fixturePassword + fixturePassword : fixturePassword, [.. arguments, "--password-stdin"])
                : run(command, 0, true, arguments);
        }
        await VerifyProtectedOutputs(root, work, run, check);
        check("wrapper originals preserved", originals.All(p => Hash(p.Key) == p.Value), "");
        check("wrapper no stage files remain", !Directory.EnumerateFileSystemEntries(work, ".diffbeacon*", SearchOption.AllDirectories).Any(), "");
        check("wrapper independent reference provided", reference is not null && File.Exists(reference), "explicit --z-reference required");
        if (reference is not null && File.Exists(reference))
        {
            var start = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { Path.GetFullPath("tests/DiffBeacon.E2E/archive_wrapper_verifier.py"), "--root", root, "--decoder", Path.GetFullPath(reference), "--output", Path.Combine(output, "wrapper-oracle"), "--products", work }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            File.WriteAllText(Path.Combine(output, "wrapper-oracle.stdout.txt"), await stdout);
            File.WriteAllText(Path.Combine(output, "wrapper-oracle.stderr.txt"), await stderr);
            check("wrapper independent all-layer full bytes/time oracle", process.ExitCode == 0 && (await stderr).Length == 0, await stderr);
        }
    }
    private static async Task VerifyProtectedOutputs(string root, string work,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var input = Path.Combine(root, "payload.zip.gz"); var inputSha = Hash(input);
        var output = Path.Combine(work, "readonly.zip"); File.WriteAllText(output, "protected readonly");
        var outputSha = Hash(output); var attributes = File.GetAttributes(output);
        try
        {
            File.SetAttributes(output, attributes | FileAttributes.ReadOnly);
            await run("wrapper-readonly-repack", 2, false, ["--archive-repack", input, output]);
            await run("wrapper-readonly-export", 2, false, ["--archive-entry", input, "folder/text.txt", output]);
            check("wrapper readonly output and attributes preserved", Hash(output) == outputSha && File.GetAttributes(output).HasFlag(FileAttributes.ReadOnly), "actual protected outputs");
        }
        finally { File.SetAttributes(output, attributes); }
        await run("wrapper-export-over-input", 2, false, ["--archive-entry", input, "folder/text.txt", input]);
        await run("wrapper-repack-over-input", 2, false, ["--archive-repack", input, input]);
        check("wrapper input output protection", Hash(input) == inputSha, "export same-input; wrapper repack output suffix rejected before writing");
        var existing = Path.Combine(work, "existing-extraction"); Directory.CreateDirectory(existing);
        var sentinel = Path.Combine(existing, "preserve.bin"); File.WriteAllText(sentinel, "existing directory"); var sentinelSha = Hash(sentinel);
        await run("wrapper-extract-existing", 2, false, ["--archive-extract", input, existing]);
        check("wrapper existing extraction preserved", Hash(sentinel) == sentinelSha && Directory.GetFileSystemEntries(existing).Length == 1, "no merge/publication into existing destination");
        var link = Path.Combine(work, "output-link.zip"); var inputLink = Path.Combine(work, "input-link.zip.gz");
        try { File.CreateSymbolicLink(link, output); File.CreateSymbolicLink(inputLink, input); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            check("wrapper link creation unavailable (unmeasured)", true, ex.GetType().Name + ": no link protection success inferred"); return;
        }
        await run("wrapper-link-repack", 2, false, ["--archive-repack", input, link]);
        await run("wrapper-link-export", 2, false, ["--archive-entry", input, "folder/text.txt", link]);
        await run("wrapper-link-input", 2, false, ["--archive-list", inputLink]);
        check("wrapper links and target bytes preserved", Hash(output) == outputSha && Hash(input) == inputSha
            && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint) && File.GetAttributes(inputLink).HasFlag(FileAttributes.ReparsePoint), "actual input/output links, retained in dedicated run");
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static async Task VerifyHarnessArguments(string work, string output, Action<string, bool, string> check)
    {
        var forbiddenOutput = Path.Combine(work, "invalid-options-must-not-create");
        var cases = new (string Name, string[] Args, string Error)[]
        {
            ("unknown-selector", ["--archive-wrapper-only", "--output", forbiddenOutput], "不明"),
            ("unknown-option", ["--output", forbiddenOutput, "--not-an-option"], "不明"),
            ("missing-app-value", ["--output", forbiddenOutput, "--app"], "値"),
            ("missing-python-value", ["--output", forbiddenOutput, "--python"], "値"),
            ("missing-z-value", ["--output", forbiddenOutput, "--z-reference"], "値"),
            ("missing-sevenzip-value", ["--output", forbiddenOutput, "--z-sevenzip"], "値"),
            ("missing-output-value", ["--output"], "値"),
            ("next-option-is-not-value", ["--output", "--archive-wrappers-only"], "値"),
            ("duplicate-output", ["--output", forbiddenOutput, "--output", forbiddenOutput], "重複"),
            ("multiple-selectors", ["--archive-wrappers-only", "--image-only", "--output", forbiddenOutput], "1件"),
            ("duplicate-selector", ["--image-only", "--image-only", "--output", forbiddenOutput], "重複")
        };
        foreach (var item in cases)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "DiffBeacon.E2E.dll"));
            foreach (var argument in item.Args) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            File.WriteAllText(Path.Combine(output, "harness-" + item.Name + ".stdout.txt"), await stdout);
            File.WriteAllText(Path.Combine(output, "harness-" + item.Name + ".stderr.txt"), await stderr);
            check("E2E rejects " + item.Name + " before output/App", process.ExitCode == 2 && (await stdout).Length == 0
                && (await stderr).Contains(item.Error, StringComparison.Ordinal) && !Directory.Exists(forbiddenOutput), "actual harness child exit2, stdout empty, no output directory");
        }
    }
}
