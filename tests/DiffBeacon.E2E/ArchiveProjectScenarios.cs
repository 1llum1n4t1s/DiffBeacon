using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ArchiveProjectScenarios
{
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, string python)
    {
        // 実装前の失敗条件はnested-archive-source/gui-failure-plan.mdへ固定。
        var originalsRoot = Path.GetFullPath("tests/Fixtures/Archives/Sources");
        var work = Path.Combine(fixtures, "archive-projects"); Directory.CreateDirectory(work);
        var originalHashes = Directory.GetFiles(originalsRoot).ToDictionary(path => path, Hash);
        string Fixed(string name) => Path.Combine(originalsRoot, name);
        var normal = Path.Combine(work, "normal.txt"); File.WriteAllText(normal, "nested archive leaf\r\nUTF-8: 日本\n");
        var wrapped = Path.Combine(work, "root-a.zip.gz");
        using (var target = File.Create(wrapped))
        using (var gzip = new GZipStream(target, CompressionLevel.SmallestSize)) gzip.Write(File.ReadAllBytes(Fixed("root-a.zip")));
        JsonObject Input(string path, string[] chain, string leaf) => new()
        {
            ["rootPath"] = Path.GetRelativePath(work, path), ["entryChain"] = JsonSerializer.SerializeToNode(chain),
            ["leafEntry"] = leaf, ["rootSha256"] = Hash(path)
        };
        JsonObject Project(JsonObject left, JsonObject right, JsonObject? middle = null) => new()
        {
            ["formatVersion"] = 2, ["activeEntryIndex"] = 0,
            ["entries"] = new JsonArray(new JsonObject
            {
                ["mode"] = "Text", ["leftPath"] = "", ["basePath"] = "", ["rightPath"] = "",
                ["leftArchiveInput"] = left.DeepClone(), ["rightArchiveInput"] = right.DeepClone(), ["baseArchiveInput"] = middle?.DeepClone(),
                ["leftReadOnly"] = true, ["baseReadOnly"] = true, ["rightReadOnly"] = true,
                ["leftDescription"] = "左 <原画> 日本", ["rightDescription"] = "右 & 内包"
            })
        };
        string Save(string name, JsonObject document)
        {
            var path = Path.Combine(work, name + ".json"); File.WriteAllText(path, document.ToJsonString()); return path;
        }
        var cases = new (string Name, JsonObject Project)[]
        {
            ("roots", Project(Input(Fixed("root-a.zip"), [], "same.txt"), Input(Fixed("root-b.zip"), [], "same.txt"))),
            ("nested", Project(Input(Fixed("nested.zip"), ["inner.zip"], "leaf.txt"), Input(Fixed("leaf.zip"), [], "leaf.txt"))),
            ("raw-tar", Project(Input(Fixed("raw-tar.zip"), ["inner.tar"], "leaf.txt"), Input(Fixed("leaf.zip"), [], "leaf.txt"))),
            ("wrapper", Project(Input(wrapped, [], "same.txt"), Input(Fixed("root-b.zip"), [], "same.txt"))),
            ("empty", Project(Input(Fixed("leaf.zip"), [], "empty.txt"), Input(Fixed("nested.zip"), ["inner.zip"], "empty.txt"))),
            ("three", Project(Input(Fixed("root-a.zip"), [], "same.txt"), Input(Fixed("root-b.zip"), [], "same.txt"), Input(Fixed("root-a.zip"), [], "same.txt"))),
            ("mixed", Project(Input(Fixed("nested.zip"), ["inner.zip"], "leaf.txt"), Input(Fixed("leaf.zip"), [], "leaf.txt")))
        };
        var mixed = cases[^1].Project["entries"]![0]!.AsObject(); mixed["rightArchiveInput"] = null; mixed["rightPath"] = "normal.txt";
        var proofCases = new List<object>();
        foreach (var item in cases)
        {
            var source = Save(item.Name, item.Project); var sourceHash = Hash(source);
            var copy = Path.Combine(work, "copied", item.Name + ".json");
            await run("archive-project-copy-" + item.Name, 0, true, ["--project-copy", source, copy]);
            if (!File.Exists(copy)) continue;
            using (var copied = JsonDocument.Parse(File.ReadAllBytes(copy)))
            {
                var root = copied.RootElement; var entry = root.GetProperty("entries")[0];
                check("archive project version2 retained " + item.Name, root.GetProperty("formatVersion").GetInt32() == 2, "old version1 reader rejects this wrapper");
                var saved = entry.GetProperty("leftArchiveInput");
                check("archive project root physicalized " + item.Name, Path.IsPathFullyQualified(saved.GetProperty("rootPath").GetString()!), "relative root resolved from original project, not copy destination");
                check("archive project virtual chain and hash retained " + item.Name,
                    saved.GetProperty("entryChain").GetRawText().Replace(" ", "").Replace("\n", "").Replace("\r", "") == item.Project["entries"]![0]!["leftArchiveInput"]!["entryChain"]!.ToJsonString()
                    && saved.GetProperty("rootSha256").GetString() == item.Project["entries"]![0]!["leftArchiveInput"]!["rootSha256"]!.GetValue<string>(), "");
            }
            var report = Path.Combine(work, item.Name + ".html");
            await run("archive-project-report-" + item.Name, 0, true, ["--report-project", copy, report]);
            var package = Path.Combine(work, item.Name + ".zip");
            await run("archive-project-package-" + item.Name, 0, true, ["--package-project", copy, package, "--report", "--patch"]);
            var extracted = Path.Combine(work, item.Name + "-extracted");
            if (File.Exists(package)) await run("archive-project-extract-" + item.Name, 0, true, ["--archive-extract", package, extracted]);
            var packedProject = Path.Combine(extracted, "project.json"); var packedReport = Path.Combine(work, item.Name + "-reopened.html");
            if (File.Exists(packedProject)) await run("archive-project-reopened-report-" + item.Name, 0, true, ["--report-project", packedProject, packedReport]);
            string? applied = null, exported = null;
            var patch = Path.Combine(extracted, "patch.diff");
            if (File.Exists(patch) && new FileInfo(patch).Length > 0)
            {
                using var packed = JsonDocument.Parse(File.ReadAllBytes(packedProject));
                var left = packed.RootElement.GetProperty("entries")[0].GetProperty("leftArchiveInput");
                var descriptor = Path.Combine(work, item.Name + "-leaf-source.json");
                File.WriteAllText(descriptor, JsonSerializer.Serialize(new
                {
                    rootPath = Path.GetFullPath(left.GetProperty("rootPath").GetString()!, extracted),
                    entryChain = left.GetProperty("entryChain").EnumerateArray().Select(value => value.GetString()).ToArray(),
                    rootSha256 = left.GetProperty("rootSha256").GetString()
                }));
                exported = Path.Combine(work, item.Name + "-left-export.txt"); applied = Path.Combine(work, item.Name + "-applied.txt");
                await run("archive-project-export-leaf-" + item.Name, 0, true,
                    ["--archive-source-entry", descriptor, left.GetProperty("leafEntry").GetString()!, exported]);
                await run("archive-project-apply-leaf-patch-" + item.Name, 0, true, ["--patch-apply", exported, patch, applied]);
            }
            check("archive project source JSON preserved " + item.Name, Hash(source) == sourceHash, "");
            proofCases.Add(new { name = item.Name, source, copy, report, package, extracted, packedReport, exported, applied });
        }
        var valid = cases[0].Project;
        foreach (var (name, change) in new (string, Action<JsonObject>)[]
        {
            ("old-version", json => json["formatVersion"] = 1),
            ("unknown-version", json => json["formatVersion"] = 3),
            ("mixed-path", json => Entry(json)["leftPath"] = "root-a.zip"),
            ("writable", json => Entry(json)["leftReadOnly"] = false),
            ("auto-mode", json => Entry(json)["mode"] = "Auto"),
            ("binary-physical-base", json => { Entry(json)["mode"] = "Binary"; Entry(json)["basePath"] = Fixed("root-a.zip"); }),
            ("missing-sha", json => Leaf(json).Remove("rootSha256")),
            ("bad-sha", json => Leaf(json)["rootSha256"] = "invalid"),
            ("unsafe-chain", json => Leaf(json)["entryChain"] = new JsonArray("../escape.zip")),
            ("null-chain", json => Leaf(json)["entryChain"] = null),
            ("null-chain-item", json => Leaf(json)["entryChain"] = new JsonArray((JsonNode?)null)),
            ("too-deep", json => Leaf(json)["entryChain"] = JsonSerializer.SerializeToNode(Enumerable.Repeat("inner.zip", 9).ToArray())),
            ("password-property", json => Leaf(json)["password"] = "synthetic-unused-password"),
            ("leaf-missing", json => Leaf(json)["leafEntry"] = null),
            ("root-url", json => Leaf(json)["rootPath"] = "https://example.invalid/source.zip")
        })
        {
            var changed = valid.DeepClone().AsObject(); change(changed); var descriptor = Save("invalid-" + name, changed);
            await Reject("schema-" + name, descriptor, true);
        }
        var single = Path.Combine(work, "invalid-single.json"); File.WriteAllText(single, Entry(valid).ToJsonString()); await Reject("single-old-shape", single, true);
        var duplicate = Path.Combine(work, "invalid-duplicate.json");
        File.WriteAllText(duplicate, valid.ToJsonString().Replace("\"leafEntry\":\"same.txt\"", "\"leafEntry\":\"same.txt\",\"leafEntry\":\"same.txt\"", StringComparison.Ordinal));
        await Reject("duplicate-leaf", duplicate, true);
        foreach (var (name, left) in new (string, JsonObject)[]
        {
            ("later-crc", Input(Fixed("late-bad-sibling.zip"), ["inner.zip"], "leaf.txt")),
            ("inner-crc", Input(Fixed("bad-inner.zip"), ["inner.zip"], "leaf.txt")),
            ("directory", Input(Fixed("leaf.zip"), [], "folder")),
            ("missing", Input(Fixed("leaf.zip"), [], "missing.txt")),
            ("encrypted", Input(Fixed("two-passwords.zip"), ["inner.zip"], "leaf.txt"))
        }) await Reject("read-" + name, Save("invalid-" + name, Project(left, Input(Fixed("leaf.zip"), [], "leaf.txt"))), false);
        var stale = valid.DeepClone().AsObject(); Leaf(stale)["rootSha256"] = new string('0', 64); await Reject("changed-root-sha", Save("invalid-stale", stale), false);
        var protectedProject = Save("protected", valid);
        foreach (var target in new[] { Fixed("root-a.zip"), protectedProject })
            foreach (var command in new[] { "--project-copy", "--report-project", "--package-project" })
            {
                var before = Hash(target); await run("archive-project-protect-" + command + "-" + Path.GetFileName(target), 2, false, [command, protectedProject, target]);
                check("archive project protected input " + command + " " + Path.GetFileName(target), Hash(target) == before, "");
            }
        var products = Path.Combine(work, "products.json"); File.WriteAllText(products, JsonSerializer.Serialize(proofCases));
        var proof = Path.Combine(work, "independent-proof.json");
        var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-X", "utf8", Path.GetFullPath("tests/Fixtures/ArchiveProjects/verify.py"), "--products", products, "--proof", proof }) start.ArgumentList.Add(argument);
        using (var process = Process.Start(start) ?? throw new IOException("Python独立検証を開始できません。"))
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); throw; }
            var text = await stdout; var errors = await stderr;
            File.WriteAllText(Path.Combine(work, "independent.stdout.txt"), text); File.WriteAllText(Path.Combine(work, "independent.stderr.txt"), errors);
            File.WriteAllText(Path.Combine(work, "independent.actual-exit.txt"), process.ExitCode.ToString());
            check("archive projects independent full verification", process.ExitCode == 0 && File.Exists(proof), errors);
        }
        check("archive project fixed originals retained", originalHashes.All(row => Hash(row.Key) == row.Value), "");

        static JsonObject Entry(JsonObject json) => json["entries"]![0]!.AsObject();
        static JsonObject Leaf(JsonObject json) => Entry(json)["leftArchiveInput"]!.AsObject();
        async Task Reject(string name, string descriptor, bool syntax)
        {
            var before = Hash(descriptor);
            foreach (var command in syntax ? new[] { "--project-copy", "--report-project", "--package-project" } : new[] { "--report-project", "--package-project" })
            {
                var target = Path.Combine(work, "reject-" + name + command + (command == "--package-project" ? ".zip" : ".out"));
                File.WriteAllText(target, "existing archive project output\r\n"); var targetHash = Hash(target);
                var result = await run("archive-project-reject-" + name + command, 2, false, [command, descriptor, target]);
                check("archive project rejection keeps output " + name + command, Hash(target) == targetHash && result.Stdout.Length == 0 && result.Stderr.Length > 0, "");
            }
            check("archive project rejected descriptor retained " + name, Hash(descriptor) == before, "");
        }
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
