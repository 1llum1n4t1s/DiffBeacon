using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class IndependentTextScenarios
{
    internal static async Task RunAsync(string fixtures, string output, string python, Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check)
    {
        var records = new List<object>();
        async Task<CommandResult> Invoke(string label, int expectedExit, bool json, string[] arguments)
        {
            var result = await run(label, expectedExit, json, arguments);
            records.Add(new { label, expectedExit, json, arguments, result });
            return result;
        }
        var work = Path.Combine(fixtures, "independent-text");
        if (Directory.Exists(work)) throw new IOException("独立Text検証には新しいrunを指定してください。");
        Directory.CreateDirectory(work);
        var texts = new[] { "共通\r\nleft <&>\r\nlast", "共通\nmiddle <&>\nlast\n", "共通\nright <&>\nlast" };
        var paths = new[] { Path.Combine(work, "left.txt"), Path.Combine(work, "middle.txt"), Path.Combine(work, "right.txt") };
        Encoding[] encodings = [new UTF8Encoding(true), new UnicodeEncoding(false, true), new UTF8Encoding(false)];
        for (var side = 0; side < 3; side++)
            await File.WriteAllBytesAsync(paths[side], encodings[side].GetPreamble().Concat(encodings[side].GetBytes(texts[side])).ToArray());
        var before = paths.ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        await File.WriteAllTextAsync(Path.Combine(work, "inputs-before.json"), JsonSerializer.Serialize(before));
        var pairs = new[] { ("left-middle", "LeftMiddle", 0, 1), ("middle-right", "MiddleRight", 1, 2), ("left-right", "LeftRight", 0, 2) };
        foreach (var (option, canonical, first, second) in pairs)
        {
            var result = await Invoke("independent-text-compare-" + option, 1, true,
                ["--compare", paths[0], paths[2], "--middle", paths[1], "--independent-text", "--pair", option]);
            using var parsed = JsonDocument.Parse(result.Stdout);
            var value = parsed.RootElement;
            check("Independent Text " + option + " selected original texts",
                value.GetProperty("textSemantics").GetString() == "Independent" &&
                value.GetProperty("comparisonPair").GetString() == canonical &&
                value.GetProperty("firstText").GetString() == texts[first] && value.GetProperty("secondText").GetString() == texts[second], result.Stdout);
            check("Independent Text " + option + " retains all three physical inputs", value.GetProperty("inputs").GetArrayLength() == 3, result.Stdout);
            await Invoke("independent-text-equal-" + option, 0, true,
                ["--compare", paths[0], paths[0], "--middle", paths[0], "--independent-text", "--pair", option]);
        }
        foreach (var (id, arguments) in new[]
        {
            ("missing-middle", new[] { "--compare", paths[0], paths[2], "--middle", Path.Combine(work, "missing.txt"), "--independent-text" }),
            ("unknown-pair", new[] { "--compare", paths[0], paths[2], "--middle", paths[1], "--independent-text", "--pair", "other" }),
            ("pair-without-independent", new[] { "--compare", paths[0], paths[2], "--pair", "left-middle" })
        })
        {
            var result = await Invoke("independent-text-reject-" + id, 2, false, arguments);
            check("Independent Text rejected " + id + " has empty stdout", result.Stdout.Length == 0, result.Stderr);
        }
        var project = Project(paths);
        var source = await Save("physical.json", project);
        var copied = Path.Combine(work, "physical-copy.json");
        await Invoke("independent-text-project-copy", 0, true, ["--project-copy", source, copied]);
        using (var copy = JsonDocument.Parse(await File.ReadAllTextAsync(copied)))
        {
            var entry = copy.RootElement.GetProperty("entries")[0];
            check("Independent Text v6 roles pair and readonly retained", copy.RootElement.GetProperty("formatVersion").GetInt32() == 6 &&
                entry.GetProperty("textInputs").GetProperty("semantics").GetString() == "Independent" &&
                entry.GetProperty("textComparisonPair").GetString() == "MiddleRight" && entry.GetProperty("baseReadOnly").GetBoolean(), entry.ToString());
        }
        var html = Path.Combine(work, "physical.html");
        await Invoke("independent-text-project-report", 0, true, ["--report-project", source, html]);
        var report = await File.ReadAllTextAsync(html);
        check("Independent Text HTML contains all three original snapshots", texts.All(text => report.Contains(WebUtility.HtmlEncode(text), StringComparison.Ordinal)) &&
            report.Contains("data-mode=\"IndependentText\"", StringComparison.Ordinal), report);
        var untitled = project.DeepClone().AsObject();
        var untitledEntry = untitled["entries"]![0]!.AsObject();
        untitledEntry["basePath"] = ""; untitledEntry["textInputs"]!["middle"]!["kind"] = "Untitled";
        var untitledPath = await Save("untitled.json", untitled);
        var untitledCopy = Path.Combine(work, "untitled-copy.json");
        await Invoke("independent-text-untitled-copy", 0, true, ["--project-copy", untitledPath, untitledCopy]);
        using (var copy = JsonDocument.Parse(await File.ReadAllTextAsync(untitledCopy)))
        {
            var entry = copy.RootElement.GetProperty("entries")[0];
            check("Independent Text empty middle remains present Untitled", entry.GetProperty("basePath").GetString() == "" &&
                entry.GetProperty("textInputs").GetProperty("middle").GetProperty("kind").GetString() == "Untitled", entry.ToString());
        }
        var untitledHtml = Path.Combine(work, "untitled.html");
        await Invoke("independent-text-untitled-report", 0, true, ["--report-project", untitledPath, untitledHtml]);
        var untitledReport = await File.ReadAllTextAsync(untitledHtml);
        check("Independent Text Untitled HTML retains three roles", new[] { "left", "middle", "right" }.All(role =>
            untitledReport.Contains("data-input-role=\"" + role + "\"", StringComparison.Ordinal)), untitledReport);
        var package = Path.Combine(work, "untitled.zip");
        await Invoke("independent-text-untitled-package", 0, true, ["--package-project", untitledPath, package, "--report"]);
        var extracted = Path.Combine(work, "untitled-extracted");
        await Invoke("independent-text-untitled-extract", 0, true, ["--archive-extract", package, extracted]);
        var reopened = Path.Combine(work, "untitled-reopened.json");
        await Invoke("independent-text-untitled-reopen", 0, true, ["--project-copy", Path.Combine(extracted, "project.json"), reopened]);
        using (var copy = JsonDocument.Parse(await File.ReadAllTextAsync(reopened)))
        {
            var entry = copy.RootElement.GetProperty("entries")[0];
            check("Independent Text packaging retains v6 Untitled presence", copy.RootElement.GetProperty("formatVersion").GetInt32() == 6 &&
                entry.GetProperty("basePath").GetString() == "" &&
                entry.GetProperty("textInputs").GetProperty("middle").GetProperty("kind").GetString() == "Untitled", entry.ToString());
        }
        for (var version = 1; version <= 5; version++)
            await Reject("old-version-" + version, value => value["formatVersion"] = version);
        await Reject("unknown-semantics", value => Entry(value)["textInputs"]!["semantics"] = "Other");
        await Reject("unknown-kind", value => Entry(value)["textInputs"]!["middle"]!["kind"] = "Other");
        await Reject("absent-middle", value => Entry(value)["textInputs"]!["middle"]!["kind"] = "Absent");
        await Reject("archive-middle", value => Entry(value)["textInputs"]!["middle"]!["kind"] = "Archive");
        await Reject("null-middle", value => Entry(value)["textInputs"]!["middle"] = null);
        await Reject("missing-middle", value => Entry(value)["textInputs"]!.AsObject().Remove("middle"));
        await Reject("extra-side-field", value => Entry(value)["textInputs"]!["middle"]!["extra"] = 1);
        await Reject("physical-empty-path", value => Entry(value)["basePath"] = "");
        await Reject("untitled-has-path", value => Entry(value)["textInputs"]!["middle"]!["kind"] = "Untitled");
        await Reject("non-text-mode", value => Entry(value)["mode"] = "Folder");
        await Reject("unknown-selected-pair", value => Entry(value)["textComparisonPair"] = "Other");
        await Reject("fixed-ancestor-untitled", value =>
        {
            Entry(value)["textInputs"]!["semantics"] = "FixedAncestor";
            Entry(value)["textInputs"]!["middle"]!["kind"] = "Untitled";
            Entry(value)["basePath"] = "";
            Entry(value)["textComparisonPair"] = null;
        });
        foreach (var format in new[] { "report", "package" })
        {
            var protectedOutput = Path.Combine(work, "fixed-ancestor-output." + (format == "report" ? "html" : "zip"));
            await File.WriteAllTextAsync(protectedOutput, "KEEP", new UTF8Encoding(false));
            var rejected = await Invoke("independent-text-fixed-ancestor-" + format, 2, false,
                [format == "report" ? "--report-project" : "--package-project", Path.Combine(work, "reject-fixed-ancestor-untitled.json"), protectedOutput]);
            check("Independent Text fixed ancestor Untitled " + format + " refuses before output", rejected.Stdout.Length == 0 &&
                await File.ReadAllTextAsync(protectedOutput) == "KEEP", rejected.Stderr);
        }
        var oversizedPath = Path.Combine(work, "oversized-cli.txt");
        await File.WriteAllTextAsync(oversizedPath, new string('制', 3 * 1024 * 1024), new UTF8Encoding(false));
        var oversizedBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(oversizedPath)));
        var oversized = await Invoke("independent-text-json-budget", 2, false,
            ["--compare", oversizedPath, oversizedPath, "--middle", oversizedPath, "--independent-text", "--max-work", "0"]);
        check("Independent Text bounded JSON rejects before stdout and preserves input", oversized.Stdout.Length == 0 &&
            oversized.Stderr.Contains("32 MiB", StringComparison.Ordinal) &&
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(oversizedPath))) == oversizedBefore, oversized.Stderr);
        var duplicate = project.ToJsonString().Replace("\"kind\":\"Physical\"", "\"kind\":\"Physical\",\"kind\":\"Physical\"", StringComparison.Ordinal);
        await RejectRaw("duplicate-side-kind", duplicate);
        await Invoke("independent-text-gui", 0, false, ["--self-test-independent-text", Path.Combine(work, "gui")]);
        check("Independent Text all physical inputs retained", paths.All(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == before[path]), JsonSerializer.Serialize(before));

        var legacyPayloadPath = Path.Combine(work, "legacy-payload-input.txt");
        const string legacyPayload = "tag --pair --independent-text\n";
        await File.WriteAllTextAsync(legacyPayloadPath, legacyPayload, new UTF8Encoding(false));
        var legacyComparison = await run("legacy-text-flags-in-option-payload", 0, true,
            ["--compare", legacyPayloadPath, legacyPayloadPath, "--substitute", "--pair", "--independent-text"]);
        using (var json = JsonDocument.Parse(legacyComparison.Stdout))
            check("Independent Text preserves legacy compare option payload", !json.RootElement.GetProperty("different").GetBoolean() &&
                !json.RootElement.TryGetProperty("textSemantics", out _), legacyComparison.Stdout);
        var legacyTable = await run("legacy-table-flags-in-option-payload", 0, true,
            ["--table", legacyPayloadPath, legacyPayloadPath, "--substitute", "--pair", "--independent-text"]);
        using (var table = JsonDocument.Parse(legacyTable.Stdout))
            check("Independent Text preserves legacy table option payload and input", !table.RootElement.GetProperty("different").GetBoolean() &&
                table.RootElement.GetProperty("rows").GetInt32() == 1 && table.RootElement.GetProperty("cols").GetInt32() == 1 &&
                await File.ReadAllTextAsync(legacyPayloadPath) == legacyPayload, legacyTable.Stdout);
        await File.WriteAllTextAsync(Path.Combine(work, "legacy-payload-proof.json"), JsonSerializer.Serialize(new[] { legacyComparison, legacyTable }));

        await File.WriteAllTextAsync(Path.Combine(work, "commands.json"), JsonSerializer.Serialize(records));
        var readerPath = Path.GetFullPath("tests/Fixtures/IndependentText/verify.py");
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-B", "-X", "utf8", readerPath, "--run", output, "--products-only", "--receipt", Path.Combine(work, "independent-proof.json") }) start.ArgumentList.Add(argument);
        var launched = DateTime.UtcNow;
        using var reader = Process.Start(start) ?? throw new IOException("独立TextのPython readerを開始できません。");
        var creation = reader.StartTime.ToUniversalTime();
        var stdoutTask = reader.StandardOutput.ReadToEndAsync(); var stderrTask = reader.StandardError.ReadToEndAsync();
        await reader.WaitForExitAsync(); var stdout = await stdoutTask; var stderr = await stderrTask;
        await File.WriteAllTextAsync(Path.Combine(work, "independent.stdout.json"), stdout, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(work, "independent.stderr.txt"), stderr, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(work, "independent-process.json"), JsonSerializer.Serialize(new
        {
            pid = reader.Id, creationUtc = creation, launchUtc = launched, exitObservedUtc = DateTime.UtcNow,
            actualExit = reader.ExitCode, waitCompleted = true, stdoutComplete = true, stderrComplete = true,
            readerSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(readerPath))), arguments = start.ArgumentList.ToArray()
        }));
        check("Independent Text separate reader verifies originals saved bytes ZIP HTML and GUI bounds", reader.ExitCode == 0, stdout + stderr);

        JsonObject Entry(JsonObject value) => value["entries"]![0]!.AsObject();
        async Task<string> Save(string name, JsonObject value)
        {
            var path = Path.Combine(work, name); await File.WriteAllTextAsync(path, value.ToJsonString()); return path;
        }
        async Task Reject(string id, Action<JsonObject> change)
        {
            var invalid = project.DeepClone().AsObject(); change(invalid); await RejectRaw(id, invalid.ToJsonString());
        }
        async Task RejectRaw(string id, string json)
        {
            var input = Path.Combine(work, "reject-" + id + ".json"); await File.WriteAllTextAsync(input, json);
            var output = Path.Combine(work, "reject-" + id + "-output.json"); await File.WriteAllTextAsync(output, "KEEP");
            var result = await Invoke("independent-text-project-reject-" + id, 2, false, ["--project-copy", input, output]);
            check("Independent Text rejected " + id + " preserves input and output", result.Stdout.Length == 0 &&
                await File.ReadAllTextAsync(input) == json && await File.ReadAllTextAsync(output) == "KEEP", result.Stderr);
        }
    }

    private static JsonObject Project(string[] paths) => new()
    {
        ["formatVersion"] = 6, ["activeEntryIndex"] = 0,
        ["entries"] = new JsonArray(new JsonObject
        {
            ["leftPath"] = paths[0], ["basePath"] = paths[1], ["rightPath"] = paths[2], ["mode"] = "Text",
            ["baseReadOnly"] = true, ["textComparisonPair"] = "MiddleRight",
            ["textInputs"] = new JsonObject
            {
                ["semantics"] = "Independent", ["left"] = new JsonObject { ["kind"] = "Physical" },
                ["middle"] = new JsonObject { ["kind"] = "Physical" }, ["right"] = new JsonObject { ["kind"] = "Physical" }
            }
        })
    };
}
