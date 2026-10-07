using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class IndependentArchiveTextScenarios
{
    internal static async Task RunAsync(string fixtures, string output, string python,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var source = Path.GetFullPath("tests/Fixtures/IndependentArchiveText");
        var work = Path.Combine(fixtures, "independent-archive-text");
        if (Directory.Exists(work)) throw new IOException("Independent Archive Textには新しいrunが必要です。");
        Directory.CreateDirectory(work);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(work, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        var records = new List<object>();
        async Task<CommandResult> Invoke(string label, int exit, bool json, params string[] arguments)
        {
            var result = await run("independent-archive-text-" + label, exit, json, arguments);
            records.Add(new { label, expectedExit = exit, arguments, result }); return result;
        }
        foreach (var state in new[] { "original", "saved" })
        {
            var input = Path.Combine(work, "workspace-" + state + ".json");
            var copied = Path.Combine(work, state + "-copy.json");
            await Invoke(state + "-copy", 0, true, "--project-copy", input, copied);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(copied));
            var entry = document.RootElement.GetProperty("entries")[0];
            check("Independent Archive " + state + " v6 keeps three Archive roles",
                document.RootElement.GetProperty("formatVersion").GetInt32() == 6 &&
                entry.GetProperty("textInputs").GetProperty("semantics").GetString() == "Independent" &&
                new[] { "left", "middle", "right" }.All(side => entry.GetProperty("textInputs").GetProperty(side).GetProperty("kind").GetString() == "Archive"), entry.ToString());
            await Invoke(state + "-report", 0, true, "--report-project", input, Path.Combine(work, state + ".html"));
            var package = Path.Combine(work, state + "-package.zip");
            await Invoke(state + "-package", 0, true, "--package-project", input, package, "--report");
            var extracted = Path.Combine(work, state + "-extracted");
            await Invoke(state + "-extract", 0, true, "--archive-extract", package, extracted);
            await Invoke(state + "-reopen", 0, true, "--project-copy", Path.Combine(extracted, "project.json"), Path.Combine(work, state + "-reopened.json"));
            await Invoke(state + "-reopened-report", 0, true, "--report-project", Path.Combine(work, state + "-reopened.json"), Path.Combine(work, state + "-reopened.html"));
        }
        var baseline = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(work, "workspace-original.json")))!.AsObject();
        foreach (var (name, change) in new (string, Action<JsonObject>)[]
        {
            ("absent", value => value["textInputs"]!["middle"]!["kind"] = "Absent"),
            ("missing", value => { value["baseArchiveInput"]!["leafEntry"] = null; value["baseArchiveInput"]!["missingEntryChain"] = new JsonArray("missing.txt"); }),
            ("provider", value => { value["mode"] = "Provider"; value["providerId"] = "unregistered-independent-provider"; }),
            ("mutable-root", value => value["baseReadOnly"] = false),
            ("old-version", value => value["textInputs"]!["semantics"] = "Other")
        })
        {
            var rejected = baseline.DeepClone().AsObject(); change(rejected["entries"]![0]!.AsObject());
            var input = Path.Combine(work, "reject-" + name + ".json");
            await File.WriteAllTextAsync(input, rejected.ToJsonString());
            var sentinel = Path.Combine(work, "reject-" + name + "-output.json");
            const string keep = "KEEP-INDEPENDENT-ARCHIVE"; await File.WriteAllTextAsync(sentinel, keep);
            var result = await Invoke("reject-" + name, 2, false, "--project-copy", input, sentinel);
            var scope = name == "provider" ? "Independent typed Text with Provider comparison mode" : name;
            check("Independent Archive rejects " + scope + " and preserves output", result.Stdout.Length == 0 && await File.ReadAllTextAsync(sentinel) == keep, result.Stderr);
        }
        await Invoke("gui", 0, false, "--self-test-independent-archive-text", Path.Combine(work, "gui"));
        using (var guiReport = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(work, "gui", "ui-report.json"))))
        {
            var guiFolder = Path.Combine(guiReport.RootElement.GetProperty("fixtures").GetString()!, "independent-archive-text");
            foreach (var side in new[] { "left", "middle", "right" })
            {
                var input = Path.Combine(guiFolder, "external-" + side + ".json");
                var copy = Path.Combine(work, "external-" + side + "-copy.json");
                var package = Path.Combine(work, "external-" + side + "-package.zip");
                var extracted = Path.Combine(work, "external-" + side + "-extracted");
                var reopened = Path.Combine(work, "external-" + side + "-reopened.json");
                await Invoke("external-" + side + "-copy", 0, true, "--project-copy", input, copy);
                await Invoke("external-" + side + "-report", 0, true, "--report-project", copy, Path.Combine(work, "external-" + side + ".html"));
                await Invoke("external-" + side + "-package", 0, true, "--package-project", copy, package, "--report");
                await Invoke("external-" + side + "-extract", 0, true, "--archive-extract", package, extracted);
                await Invoke("external-" + side + "-reopen", 0, true, "--project-copy", Path.Combine(extracted, "project.json"), reopened);
                await Invoke("external-" + side + "-reopened-report", 0, true, "--report-project", reopened, Path.Combine(work, "external-" + side + "-reopened.html"));
                foreach (var failure in new[] { "tamper", "corrupt" })
                {
                    var rejectedOutput = Path.Combine(work, "reject-" + failure + "-" + side + ".html");
                    await File.WriteAllTextAsync(rejectedOutput, "KEEP-BOUNDARY-REPORT");
                    var result = await Invoke("reject-" + failure + "-" + side, 2, false, "--report-project", Path.Combine(guiFolder, failure + "-" + side + ".json"), rejectedOutput);
                    check("Independent Archive rejects " + failure + " side " + side + " and preserves existing report", result.Stdout.Length == 0 && await File.ReadAllTextAsync(rejectedOutput) == "KEEP-BOUNDARY-REPORT", result.Stderr);
                }
            }
        }
        await File.WriteAllTextAsync(Path.Combine(work, "commands.json"), JsonSerializer.Serialize(records));
        var readerPath = Path.Combine(source, "verify-products.py");
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-B", "-X", "utf8", readerPath, "--work", work, "--receipt", Path.Combine(work, "independent-proof.json") }) start.ArgumentList.Add(argument);
        var launched = DateTime.UtcNow;
        using var reader = Process.Start(start) ?? throw new IOException("Independent Archive Python readerを開始できません。");
        var birth = reader.StartTime.ToUniversalTime(); var stdoutTask = reader.StandardOutput.ReadToEndAsync(); var stderrTask = reader.StandardError.ReadToEndAsync();
        await reader.WaitForExitAsync(); var stdout = await stdoutTask; var stderr = await stderrTask;
        await File.WriteAllTextAsync(Path.Combine(work, "independent.stdout.json"), stdout, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(work, "independent.stderr.txt"), stderr, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(work, "independent-process.json"), JsonSerializer.Serialize(new
        {
            pid = reader.Id, creationUtc = birth, launchUtc = launched, exitObservedUtc = DateTime.UtcNow, actualExit = reader.ExitCode,
            waitCompleted = true, stdoutComplete = true, stderrComplete = true,
            readerSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(readerPath))), arguments = start.ArgumentList.ToArray()
        }));
        check("Independent Archive stdlib reader verifies all roots entries assets HTML GUI and package", reader.ExitCode == 0, stdout + stderr);
    }
}
