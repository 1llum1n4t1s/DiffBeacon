using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class BinaryThreeWayScenarios
{
    internal static async Task RunAsync(string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, string python)
    {
        var work = Path.Combine(fixtures, "binary-threeway"); Directory.CreateDirectory(work); var gui = Path.Combine(work, "gui");
        await run("binary-threeway-gui", 0, false, ["--self-test", gui, "--binary-threeway-only"]);
        using var proof = await Independent("gui", false); if (proof is null) return;
        var workspace = proof.RootElement.GetProperty("workspace").GetString()!; var package = proof.RootElement.GetProperty("package").GetString()!;
        await run("binary-threeway-copy", 0, true, ["--project-copy", workspace, Path.Combine(work, "copied.json")]);
        await run("binary-threeway-package", 0, true, ["--package-project", workspace, Path.Combine(work, "cli-package.zip")]);
        await run("binary-threeway-extract", 0, true, ["--archive-extract", package, Path.Combine(work, "extracted")]);
        await run("binary-threeway-extracted-reload", 0, true, ["--project-copy", Path.Combine(work, "extracted", "project.json"), Path.Combine(work, "reloaded.json")]);
        await Independent("cli", true);
        foreach (var kind in new[] { "sha", "root-sha", "outside", "version", "kind" })
        {
            var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(work, "copied.json")))!; var input = doc["entries"]![0]!["baseArchiveInput"]!; var snapshot = input["workingTexts"]![0]!;
            switch (kind)
            {
                case "sha": snapshot["sha256"] = new string('0', 64); break;
                case "root-sha": input["rootSha256"] = new string('0', 64); break;
                case "outside": snapshot["snapshotPath"] = "../outside.bin"; break;
                case "version": doc["formatVersion"] = 4; break;
                case "kind": snapshot["kind"] = "invalid"; break;
            }
            var malformed = Path.Combine(work, kind + ".json"); File.WriteAllText(malformed, doc.ToJsonString()); var target = Path.Combine(work, kind + "-preserve.json"); File.WriteAllText(target, "preserve");
            await run("binary-threeway-refuse-" + kind, 2, false, [kind == "root-sha" ? "--package-project" : "--project-copy", malformed, target]);
            check("Binary threeway malformed middle preserves output " + kind, File.ReadAllText(target) == "preserve", target);
        }
        async Task<JsonDocument?> Independent(string name, bool cli)
        {
            var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.GetFullPath("tests/Fixtures/BinaryThreeWay/verify.py"), gui }) info.ArgumentList.Add(argument); if (cli) info.ArgumentList.Add(work);
            using var process = Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); var text = await stdout; var errors = await stderr;
            File.WriteAllText(Path.Combine(work, name + "-independent.stdout.txt"), text); File.WriteAllText(Path.Combine(work, name + "-independent.stderr.txt"), errors);
            check("Binary threeway independent fullbytes " + name, process.ExitCode == 0, text + errors); return process.ExitCode == 0 ? JsonDocument.Parse(text) : null;
        }
    }
}
