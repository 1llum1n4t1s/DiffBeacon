using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ArchivePresentTextScenarios
{
    internal static async Task RunAsync(string output, string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check, string python)
    {
        var work = Path.Combine(fixtures, "archive-present-text"); Directory.CreateDirectory(work);
        var gui = Path.Combine(work, "gui");
        await run("archive-present-gui", 0, false, ["--self-test", gui, "--archive-sources-only"]);
        var proof = await Independent("first", null);
        if (proof is null) return;
        var workspace = proof.RootElement.GetProperty("workspace").GetString()!;
        var largeWorkspace = proof.RootElement.GetProperty("largeWorkspace").GetString()!;
        var package = proof.RootElement.GetProperty("package").GetString()!;
        var source = proof.RootElement.GetProperty("patchSource").GetString()!;
        var expected = proof.RootElement.GetProperty("patchExpected").GetString()!;
        var patch = proof.RootElement.GetProperty("patch").GetString()!;
        await run("archive-present-cli-report", 0, true, ["--report-project", workspace, Path.Combine(work, "cli.html")]);
        await run("archive-present-project-copy", 0, true, ["--project-copy", workspace, Path.Combine(work, "copied.json")]);
        await run("archive-present-copy-report", 0, true, ["--report-project", Path.Combine(work, "copied.json"), Path.Combine(work, "copied.html")]);
        await run("archive-present-large-project-copy", 0, true, ["--project-copy", largeWorkspace, Path.Combine(work, "large-copied.json")]);
        await run("archive-present-package-cli", 0, true, ["--package-project", workspace, Path.Combine(work, "cli-package.zip"), "--report", "--patch"]);
        await run("archive-present-package-extract", 0, true, ["--archive-extract", package, Path.Combine(work, "extracted")]);
        await run("archive-present-package-reopen", 0, true, ["--report-project", Path.Combine(work, "extracted", "project.json"), Path.Combine(work, "reopened.html")]);
        await run("archive-present-patch", 0, true, ["--patch-apply", source, patch, Path.Combine(work, "patch-applied.txt")]);
        check("archive present patch fullbytes", Hash(Path.Combine(work, "patch-applied.txt")) == Hash(expected), "");
        await Independent("reopened", work);

        // snapshotを残したまま、型・SHA・相対path・原本世代の拒否を各出力公開経路で観測する。
        foreach (var kind in new[] { "version", "sha", "unknown", "absolute", "outside", "encoding", "root-sha", "chain" })
        {
            var descriptor = JsonNode.Parse(File.ReadAllText(workspace))!.AsObject();
            foreach (var side in new[] { "left", "right" })
            {
                var input = descriptor["entries"]![0]![side + "ArchiveInput"]!.AsObject();
                var copy = input["workingTexts"]![0]!.AsObject();
                var originalAsset = Path.GetFullPath(copy["snapshotPath"]!.GetValue<string>(), Path.GetDirectoryName(workspace)!);
                var localAsset = Path.Combine(work, "assets", Path.GetFileName(originalAsset)); Directory.CreateDirectory(Path.GetDirectoryName(localAsset)!);
                if (!File.Exists(localAsset)) File.Copy(originalAsset, localAsset);
                copy["snapshotPath"] = Path.GetRelativePath(work, localAsset);
            }
            var left = descriptor["entries"]![0]!["leftArchiveInput"]!.AsObject(); var snapshot = left["workingTexts"]![0]!.AsObject();
            switch (kind)
            {
                case "version": descriptor["formatVersion"] = 3; break;
                case "sha": snapshot["sha256"] = new string('0', 64); break;
                case "unknown": snapshot["password"] = "not a real secret"; break;
                case "absolute": snapshot["snapshotPath"] = Path.GetFullPath(snapshot["snapshotPath"]!.GetValue<string>(), work); break;
                case "outside": snapshot["snapshotPath"] = "../outside.text"; break;
                case "encoding": snapshot["encodingName"] = "unknown"; break;
                case "root-sha": left["rootSha256"] = new string('0', 64); break;
                case "chain": snapshot["entryChain"] = new JsonArray("other.zip"); break;
            }
            var invalid = Path.Combine(work, kind + ".json"); File.WriteAllText(invalid, descriptor.ToJsonString());
            foreach (var command in kind == "root-sha" ? new[] { "--report-project", "--package-project" } : new[] { "--project-copy", "--report-project", "--package-project" })
            {
                var target = Path.Combine(work, "reject-" + kind + command + (command == "--package-project" ? ".zip" : ".json"));
                File.WriteAllText(target, "keep output"); var before = Hash(target);
                await run("archive-present-reject-" + kind + command, 2, false, [command, invalid, target]);
                check("archive present failure preserves output " + kind + command, Hash(target) == before, "");
            }
        }
        proof.Dispose();

        async Task<JsonDocument?> Independent(string name, string? reopened)
        {
            var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-X", "utf8", Path.GetFullPath("tests/Fixtures/ArchiveWorkingText/verify.py"), gui }) info.ArgumentList.Add(argument);
            if (reopened is not null) info.ArgumentList.Add(reopened);
            using var process = Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); var text = await stdout; var errors = await stderr;
            File.WriteAllText(Path.Combine(work, name + "-independent.stdout.txt"), text); File.WriteAllText(Path.Combine(work, name + "-independent.stderr.txt"), errors);
            check("archive present independent " + name, process.ExitCode == 0, text + errors);
            return process.ExitCode == 0 ? JsonDocument.Parse(text) : null;
        }
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
