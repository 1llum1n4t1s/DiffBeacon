using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ArchiveMissingScenarios
{
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, string python)
    {
        // 不在・破損・仮想階層・空fileパッチの失敗条件を実装前にfailure-plan.mdへ整理した。
        var fixedRoot = Path.GetFullPath("tests/Fixtures/Archives/Sources");
        var work = Path.Combine(fixtures, "archive-missing"); Directory.CreateDirectory(work);
        var originals = Directory.GetFiles(fixedRoot).ToDictionary(path => path, Hash);
        string Fixed(string name) => Path.Combine(fixedRoot, name);
        JsonObject Input(string root, string[] chain, string[]? missing, string? leaf) => new JsonObject
        {
            ["rootPath"] = Path.GetRelativePath(work, root), ["entryChain"] = JsonSerializer.SerializeToNode(chain),
            ["rootSha256"] = Hash(root), ["leafEntry"] = leaf
        }.Also(value => { if (missing is not null) value["missingEntryChain"] = JsonSerializer.SerializeToNode(missing); });
        JsonObject Present(string root, string leaf, params string[] chain) => Input(Fixed(root), chain, null, leaf);
        JsonObject Missing(string root, params string[] tail) => Input(Fixed(root), [], tail, null);
        JsonObject Project(JsonObject left, JsonObject right, JsonObject? middle = null) => new()
        {
            ["formatVersion"] = 3, ["activeEntryIndex"] = 0,
            ["entries"] = new JsonArray(new JsonObject
            {
                ["mode"] = "Text", ["leftPath"] = "", ["basePath"] = "", ["rightPath"] = "",
                ["leftArchiveInput"] = left.DeepClone(), ["rightArchiveInput"] = right.DeepClone(), ["baseArchiveInput"] = middle?.DeepClone(),
                ["leftReadOnly"] = true, ["rightReadOnly"] = true, ["baseReadOnly"] = true,
                ["leftDescription"] = "左 <選択>", ["rightDescription"] = "右 & 選択", ["baseDescription"] = "祖先"
            })
        };
        string Save(string name, JsonObject document)
        { var path = Path.Combine(work, name + ".json"); File.WriteAllText(path, document.ToJsonString()); return path; }
        var cases = new (string Name, JsonObject Project)[]
        {
            ("create-text", Project(Missing("leaf.zip", "same.txt"), Present("root-a.zip", "same.txt"))),
            ("delete-text", Project(Present("root-a.zip", "same.txt"), Missing("leaf.zip", "same.txt"))),
            ("create-empty", Project(Missing("root-a.zip", "empty.txt"), Present("leaf.zip", "empty.txt"))),
            ("delete-empty", Project(Present("leaf.zip", "empty.txt"), Missing("root-a.zip", "empty.txt"))),
            ("virtual-leaf", Project(Missing("root-a.zip", "inner.zip", "leaf.txt"), Present("nested.zip", "leaf.txt", "inner.zip"))),
            ("virtual-deep", Project(Missing("root-a.zip", "first.zip", "second.zip", "leaf.txt"), Present("leaf.zip", "leaf.txt"))),
            ("both-missing", Project(Missing("root-a.zip", "absent.txt"), Missing("root-b.zip", "absent.txt"))),
            ("missing-ancestor", Project(Present("root-a.zip", "same.txt"), Present("root-b.zip", "same.txt"), Missing("leaf.zip", "same.txt")))
        };
        var proof = new List<object>();
        foreach (var item in cases)
        {
            var source = Save(item.Name, item.Project); var original = Hash(source);
            var copy = Path.Combine(work, item.Name + "-copy.json");
            await run("archive-missing-copy-" + item.Name, 0, true, ["--project-copy", source, copy]);
            if (!File.Exists(copy)) continue;
            using (var saved = JsonDocument.Parse(File.ReadAllBytes(copy)))
            {
                check("archive missing version3 " + item.Name, saved.RootElement.GetProperty("formatVersion").GetInt32() == 3, "");
                var entry = saved.RootElement.GetProperty("entries")[0];
                foreach (var side in new[] { "left", "base", "right" })
                {
                    var originalInput = item.Project["entries"]![0]![side + "ArchiveInput"];
                    if (originalInput is not JsonObject input || input["missingEntryChain"] is null) continue;
                    var actual = entry.GetProperty(side + "ArchiveInput");
                    check("archive missing chain retained " + item.Name + " " + side,
                        actual.GetProperty("missingEntryChain").EnumerateArray().Select(node => node.GetString()).SequenceEqual(
                            input["missingEntryChain"]!.AsArray().Select(node => node!.GetValue<string>()))
                        && Path.IsPathFullyQualified(actual.GetProperty("rootPath").GetString()!) && actual.GetProperty("leafEntry").ValueKind == JsonValueKind.Null, "");
                }
            }
            var html = Path.Combine(work, item.Name + ".html"); var package = Path.Combine(work, item.Name + ".zip");
            await run("archive-missing-report-" + item.Name, 0, true, ["--report-project", copy, html]);
            await run("archive-missing-package-" + item.Name, 0, true, ["--package-project", copy, package, "--report", "--patch"]);
            var extracted = Path.Combine(work, item.Name + "-extracted");
            if (File.Exists(package)) await run("archive-missing-extract-" + item.Name, 0, true, ["--archive-extract", package, extracted]);
            var packedProject = Path.Combine(extracted, "project.json");
            var reopened = Path.Combine(work, item.Name + "-reopened.html");
            if (File.Exists(packedProject)) await run("archive-missing-reopen-" + item.Name, 0, true, ["--report-project", packedProject, reopened]);
            check("archive missing descriptor retained " + item.Name, Hash(source) == original, "");
            proof.Add(new { name = item.Name, source, copy, html, package, extracted, reopened });
        }
        var valid = cases[0].Project;
        foreach (var inherited in new[] { false, true })
        {
            var descriptor = valid.DeepClone().AsObject();
            Leaf(descriptor)["inheritedReadOnly"] = inherited;
            var source = Save("inherited-readonly-" + inherited, descriptor);
            var before = Hash(source);
            var target = Path.Combine(work, "inherited-readonly-" + inherited + "-copy.json");
            await run("archive-missing-inherited-readonly-" + inherited, 0, true, ["--project-copy", source, target]);
            using var saved = JsonDocument.Parse(File.ReadAllBytes(target));
            var entry = saved.RootElement.GetProperty("entries")[0];
            check("archive missing inherited readonly roundtrip " + inherited,
                entry.GetProperty("leftArchiveInput").GetProperty("inheritedReadOnly").GetBoolean() == inherited
                && entry.GetProperty("leftReadOnly").GetBoolean(), "");
            check("archive missing inherited readonly input retained " + inherited, Hash(source) == before, "");
        }
        foreach (var (name, change) in new (string, Action<JsonObject>)[]
        {
            ("version2", json => json["formatVersion"] = 2),
            ("null-chain", json => Leaf(json)["missingEntryChain"] = null),
            ("empty-chain", json => Leaf(json)["missingEntryChain"] = new JsonArray()),
            ("null-item", json => Leaf(json)["missingEntryChain"] = new JsonArray((JsonNode?)null)),
            ("traversal", json => Leaf(json)["missingEntryChain"] = new JsonArray("../escape.zip")),
            ("absolute", json => Leaf(json)["missingEntryChain"] = new JsonArray("/escape.zip")),
            ("too-deep", json => Leaf(json)["missingEntryChain"] = JsonSerializer.SerializeToNode(Enumerable.Repeat("virtual.zip", 10).ToArray())),
            ("also-leaf", json => Leaf(json)["leafEntry"] = "leaf.txt"),
            ("writable", json => json["entries"]![0]!["leftReadOnly"] = false),
            ("inherited-string", json => Leaf(json)["inheritedReadOnly"] = "false"),
            ("inherited-number", json => Leaf(json)["inheritedReadOnly"] = 0),
            ("inherited-object", json => Leaf(json)["inheritedReadOnly"] = new JsonObject())
        })
        {
            var json = valid.DeepClone().AsObject(); change(json); await Reject("schema-" + name, Save("invalid-" + name, json), true);
        }
        foreach (var (name, input) in new[]
        {
            ("actually-file", Missing("root-a.zip", "same.txt")),
            ("actually-directory", Missing("leaf.zip", "folder/")),
            ("corrupt-sibling", Missing("late-bad-sibling.zip", "absent.txt")),
            ("bad-root-sha", Missing("root-a.zip", "absent.txt").Also(value => value["rootSha256"] = new string('0', 64)))
        }) await Reject(name, Save("reject-" + name, Project(input, Present("root-a.zip", "same.txt"))), false);

        File.WriteAllText(Path.Combine(work, "proof.json"), JsonSerializer.Serialize(new { cases = proof, fixedRoot }));
        // Python独立readerはCLI入力を利用せず、保存したrootと実containerから存在状態・全文を再計算する。
        var info = new System.Diagnostics.ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("-X"); info.ArgumentList.Add("utf8"); info.ArgumentList.Add(Path.GetFullPath("tests/Fixtures/ArchiveMissing/verify.py")); info.ArgumentList.Add(Path.Combine(work, "proof.json"));
        using var process = System.Diagnostics.Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var outText = await stdout; var errorText = await stderr;
        File.WriteAllText(Path.Combine(work, "independent.stdout.txt"), outText); File.WriteAllText(Path.Combine(work, "independent.stderr.txt"), errorText);
        check("archive missing independent reader", process.ExitCode == 0, outText + errorText);
        if (process.ExitCode == 0)
        {
            using var patchChecks = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(work, "patch-checks.json")));
            check("archive missing independent patch case count", patchChecks.RootElement.GetArrayLength() == 6, "");
            foreach (var item in patchChecks.RootElement.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString()!;
                var source = item.GetProperty("source").GetString()!;
                var expected = item.GetProperty("expected").GetString()!;
                var patch = item.GetProperty("patch").GetString()!;
                var target = Path.Combine(work, name + "-patch-result.txt");
                var sourceBefore = Hash(source); var patchBefore = Hash(patch);
                await run("archive-missing-patch-apply-" + name, 0, true, ["--patch-apply", source, patch, target]);
                check("archive missing patch independent bytes " + name, File.Exists(target) && Hash(target) == Hash(expected), "");
                check("archive missing patch inputs retained " + name, Hash(source) == sourceBefore && Hash(patch) == patchBefore, "");
            }
        }
        check("archive missing fixed roots unchanged", originals.All(pair => Hash(pair.Key) == pair.Value), "");
        var gui = Path.Combine(work, "draft-gui");
        await run("archive-missing-draft-gui", 0, false, ["--self-test", gui, "--archive-sources-only"]);
        var draftInfo = new System.Diagnostics.ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        draftInfo.ArgumentList.Add("-X"); draftInfo.ArgumentList.Add("utf8");
        draftInfo.ArgumentList.Add(Path.GetFullPath("tests/Fixtures/ArchiveDraft/verify.py")); draftInfo.ArgumentList.Add(gui);
        using var draftProcess = System.Diagnostics.Process.Start(draftInfo)!;
        var draftOut = draftProcess.StandardOutput.ReadToEndAsync(); var draftError = draftProcess.StandardError.ReadToEndAsync();
        await draftProcess.WaitForExitAsync(); var draftOutput = await draftOut; var draftErrors = await draftError;
        File.WriteAllText(Path.Combine(work, "draft-independent.stdout.txt"), draftOutput);
        File.WriteAllText(Path.Combine(work, "draft-independent.stderr.txt"), draftErrors);
        check("archive missing draft independent reader", draftProcess.ExitCode == 0, draftOutput + draftErrors);
        if (draftProcess.ExitCode == 0)
        {
            using var draft = JsonDocument.Parse(draftOutput);
            var source = draft.RootElement.GetProperty("patchSource").GetString()!;
            var expected = draft.RootElement.GetProperty("patchExpected").GetString()!;
            var patch = draft.RootElement.GetProperty("patch").GetString()!;
            var archive = draft.RootElement.GetProperty("package").GetString()!;
            var extractedDraft = Path.Combine(work, "draft-package-extracted");
            await run("archive-missing-draft-package-extract", 0, true, ["--archive-extract", archive, extractedDraft]);
            var reopenedDraft = Path.Combine(work, "draft-package-reopened.html");
            await run("archive-missing-draft-package-reopen", 0, true, ["--report-project", Path.Combine(extractedDraft, "project.json"), reopenedDraft]);
            draftInfo.ArgumentList.Add(reopenedDraft);
            using var reopenedProcess = System.Diagnostics.Process.Start(draftInfo)!;
            var reopenedOut = reopenedProcess.StandardOutput.ReadToEndAsync(); var reopenedError = reopenedProcess.StandardError.ReadToEndAsync();
            await reopenedProcess.WaitForExitAsync(); var reopenedOutput = await reopenedOut; var reopenedErrors = await reopenedError;
            File.WriteAllText(Path.Combine(work, "draft-reopened-independent.stdout.txt"), reopenedOutput);
            File.WriteAllText(Path.Combine(work, "draft-reopened-independent.stderr.txt"), reopenedErrors);
            check("archive missing saved draft package independent reopen", reopenedProcess.ExitCode == 0, reopenedOutput + reopenedErrors);
            var target = Path.Combine(work, "draft-patch-applied.txt"); var beforeSource = Hash(source); var beforePatch = Hash(patch);
            await run("archive-missing-draft-packaged-patch-apply", 0, true, ["--patch-apply", source, patch, target]);
            check("archive missing draft packaged patch independent bytes", File.Exists(target) && Hash(target) == Hash(expected), "");
            check("archive missing draft packaged patch inputs retained", Hash(source) == beforeSource && Hash(patch) == beforePatch, "");
        }

        async Task Reject(string name, string descriptor, bool schema)
        {
            foreach (var command in schema ? new[] { "--project-copy", "--report-project", "--package-project" } : new[] { "--report-project", "--package-project" })
            {
                var extension = command == "--package-project" ? ".zip" : command == "--report-project" ? ".html" : ".json";
                var target = Path.Combine(work, name + command + extension); File.WriteAllText(target, "preserve existing output"); var before = Hash(target);
                await run("archive-missing-reject-" + name + command, 2, false, [command, descriptor, target]);
                check("archive missing existing output retained " + name + command, Hash(target) == before, "");
            }
        }
        static JsonObject Leaf(JsonObject json) => json["entries"]![0]!["leftArchiveInput"]!.AsObject();
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static JsonObject Also(this JsonObject value, Action<JsonObject> action) { action(value); return value; }
}
