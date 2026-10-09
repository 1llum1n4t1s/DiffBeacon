using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ArchiveBinaryWorkingScenarios
{
    internal static async Task RunAsync(string output, string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check, string python)
    {
        var work = Path.Combine(fixtures, "archive-binary-working"); Directory.CreateDirectory(work); var gui = Path.Combine(work, "gui");
        await run("archive-binary-gui", 0, false, ["--self-test", gui, "--binary-working-only"]);
        using var proof = await Independent("gui", false); if (proof is null) return;
        var workspace = proof.RootElement.GetProperty("workspace").GetString()!; var package = proof.RootElement.GetProperty("package").GetString()!;
        await run("archive-binary-copy", 0, true, ["--project-copy", workspace, Path.Combine(work, "copied.json")]);
        await run("archive-binary-package", 0, true, ["--package-project", workspace, Path.Combine(work, "cli-package.zip")]);
        await run("archive-binary-extract", 0, true, ["--archive-extract", package, Path.Combine(work, "extracted")]);
        await run("archive-binary-extracted-reload", 0, true, ["--project-copy", Path.Combine(work, "extracted", "project.json"), Path.Combine(work, "reloaded.json")]);
        await Independent("cli", true);
        foreach (var kind in new[] { "unknown-version", "v4-kind", "v4-null-kind", "unknown-kind", "binary-encoding", "binary-bom", "sha", "outside", "root-sha" })
        {
            var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(work, "copied.json")))!;
            var input = doc["entries"]![0]!["leftArchiveInput"]!; var snapshot = input["workingTexts"]![0]!;
            switch (kind)
            {
                case "unknown-version": doc["formatVersion"] = 10; break;
                case "v4-kind": doc["formatVersion"] = 4; break;
                case "v4-null-kind": doc["formatVersion"] = 4; snapshot["kind"] = null; break;
                case "unknown-kind": snapshot["kind"] = "Text"; break;
                case "binary-encoding": snapshot["encodingName"] = "utf-8"; break;
                case "binary-bom": snapshot["hasBom"] = true; break;
                case "sha": snapshot["sha256"] = new string('0', 64); break;
                case "outside": snapshot["snapshotPath"] = "../outside.bin"; break;
                case "root-sha": input["rootSha256"] = new string('0', 64); break;
            }
            var invalid = Path.Combine(work, kind + ".json"); File.WriteAllText(invalid, doc.ToJsonString());
            foreach (var command in kind == "root-sha" ? new[] { "--package-project" } : new[] { "--project-copy", "--package-project" })
            { var target = Path.Combine(work, "reject-" + kind + command + ".zip"); File.WriteAllText(target, "keep output\n"); var before = Hash(target); await run("binary-reject-" + kind + command, 2, false, [command, invalid, target]); check("binary reject preserves " + kind + command, Hash(target) == before, "actual process"); }
        }
        foreach (var command in new[] { "--report-project", "--package-project" })
        foreach (var option in command == "--report-project" ? new[] { "" } : new[] { "--report", "--patch" })
        { var target = Path.Combine(work, "unsupported-" + command + option + ".zip"); File.WriteAllText(target, "keep output\n"); var before = Hash(target); await run("binary-unsupported-" + command + option, 2, false, new[] { command, workspace, target }.Concat(option.Length == 0 ? [] : new[] { option }).ToArray()); check("binary unsupported preserves " + command + option, Hash(target) == before, "not counted as supported"); }
        // 異形式のcached assetでも参照ごとの16MiB検査を省略しない。大きな本文はこのrunにだけ生成する。
        var maximum = new byte[16 * 1024 * 1024]; Array.Fill(maximum, (byte)'X'); var exactAsset = Path.Combine(work, "boundary-16.bin"); File.WriteAllBytes(exactAsset, maximum);
        var boundary = JsonNode.Parse(File.ReadAllText(Path.Combine(work, "copied.json")))!; var boundarySnapshot = boundary["entries"]![0]!["leftArchiveInput"]!["workingTexts"]![0]!;
        boundarySnapshot["snapshotPath"] = Path.GetFileName(exactAsset); boundarySnapshot["sha256"] = Hash(exactAsset);
        var exact = Path.Combine(work, "boundary-16.json"); File.WriteAllText(exact, boundary.ToJsonString());
        await run("binary-exact-16MiB-project-copy", 0, true, ["--project-copy", exact, Path.Combine(work, "boundary-16-copied.json")]);
        var overflow = Path.Combine(work, "boundary-17.text"); using (var file = File.Create(overflow)) { file.Write(maximum); file.Write(maximum.AsSpan(0, 1024 * 1024)); }
        var first = boundary["entries"]![0]!.DeepClone(); first["mode"] = "Text"; first["rightArchiveInput"] = null;
        var firstInput = first["leftArchiveInput"]!; firstInput["leafEntry"] = "ascii.txt"; var textSnapshot = firstInput["workingTexts"]![0]!;
        textSnapshot.AsObject().Remove("kind"); textSnapshot["leafEntry"] = "ascii.txt"; textSnapshot["encodingName"] = "utf-8"; textSnapshot["snapshotPath"] = Path.GetFileName(overflow); textSnapshot["sha256"] = Hash(overflow);
        var second = boundary["entries"]![0]!.DeepClone(); var binarySnapshot = second["leftArchiveInput"]!["workingTexts"]![0]!;
        binarySnapshot["snapshotPath"] = Path.GetFileName(overflow); binarySnapshot["sha256"] = Hash(overflow);
        boundary["entries"] = new JsonArray(first, second); var cached = Path.Combine(work, "cached-text-binary-overflow.json"); File.WriteAllText(cached, boundary.ToJsonString());
        var protectedBoundary = Path.Combine(work, "boundary-refused.json"); File.WriteAllText(protectedBoundary, "keep boundary\n"); var protectedHash = Hash(protectedBoundary);
        await run("binary-cached-Text-17MiB-refused", 2, false, ["--project-copy", cached, protectedBoundary]); check("cached asset Binary bound preserves existing output", Hash(protectedBoundary) == protectedHash, "Text17MiB cache cannot bypass Binary16MiB");
        var boundaryInfo = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-X", "utf8", "-c", "import json,pathlib,hashlib,sys;p=pathlib.Path(sys.argv[1]);j=json.loads(p.read_text());s=j['entries'][0]['leftArchiveInput']['workingTexts'][0];b=(p.parent/s['snapshotPath']).read_bytes();assert len(b)==16*1024*1024 and b==b'X'*len(b) and hashlib.sha256(b).hexdigest().upper()==s['sha256'];print('exact 16MiB fullbytes SHA verified')", Path.Combine(work, "boundary-16-copied.json") }) boundaryInfo.ArgumentList.Add(argument);
        using (var process = Process.Start(boundaryInfo)!)
        { var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); var text = await stdout; var errors = await stderr; File.WriteAllText(Path.Combine(work, "boundary-independent.stdout.txt"), text); File.WriteAllText(Path.Combine(work, "boundary-independent.stderr.txt"), errors); check("Binary16MiB stdlib fullbytes", process.ExitCode == 0, text + errors); }

        async Task<JsonDocument?> Independent(string name, bool cli)
        {
            var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-X", "utf8", Path.GetFullPath("tests/Fixtures/ArchiveBinaryWorking/verify.py"), gui }) info.ArgumentList.Add(argument); if (cli) info.ArgumentList.Add(work);
            using var process = Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); var text = await stdout; var errors = await stderr;
            File.WriteAllText(Path.Combine(work, name + "-independent.stdout.txt"), text); File.WriteAllText(Path.Combine(work, name + "-independent.stderr.txt"), errors);
            check("binary stdlib independent " + name, process.ExitCode == 0, text + errors); return process.ExitCode == 0 ? JsonDocument.Parse(text) : null;
        }
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
