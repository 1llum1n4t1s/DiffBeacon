using System.Security.Cryptography;
using System.Text.Json;

internal static class ImageTransformScenarios
{
    // 失敗先行契約: E:/DiffBeacon-artifacts/local/image-transforms/contract.md。
    // Copy画素は公式DLL観測のみ。内部region/historycounterの期待値は再計算しない。
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var source = FindFixtures(); var bytes = await File.ReadAllBytesAsync(Path.Combine(source, "winimerge-transforms-golden.json"));
        const string pinned = "5383447EA3FF48F5C1F1A99F6CFFF8438568ED68445F5BB59FD4C2BC31FEFD82";
        check("image-transforms-golden-sha", Sha(bytes) == pinned, Sha(bytes)); if (Sha(bytes) != pinned) throw new InvalidDataException("Transform oracle changed");
        var folder = Path.Combine(fixtures, "image-transforms"); var proof = Path.Combine(output, "image-transforms"); Directory.CreateDirectory(folder); Directory.CreateDirectory(proof);
        await File.WriteAllBytesAsync(Path.Combine(proof, "golden.json"), bytes); using var golden = JsonDocument.Parse(bytes); var root = golden.RootElement;
        check("image-transforms-provenance", root.GetProperty("sourceRevision").GetString() == "da639cdfaeca87aaad0eaceec509afa11ad61421"
            && root.GetProperty("dllSha256").GetString() == "36F2A726C34A2323D2E569903B4F1DC6C28ABF1C10447006335F2785FC511BA6"
            && root.GetProperty("headerBlob").GetString() == "d5c1a0c0bcf463fd7ef3237b18e4b7a60b4853fa", "WinIMerge v1.0.54");
        check("image-transforms-extractor-sha", Hash(Path.Combine(source, "extract.py")) == root.GetProperty("extractorSha256").GetString(), "extraction provenance");
        check("image-transforms-gpl-sha", Hash(Path.Combine(source, "..", "ImageRegions", "LICENSE.txt")) == root.GetProperty("sources").GetProperty("GPLLicenseSha256").GetString(), "canonical GPLv2");
        var input = Path.Combine(folder, "input.png"); File.Copy(Path.Combine(source, "input.png"), input);
        var inputHash = Hash(input); var inputAttrs = File.GetAttributes(input); var inputOracle = root.GetProperty("input");
        check("image-transforms-input-sha", inputHash == inputOracle.GetProperty("pngSha256").GetString(), inputHash);
        VerifyPng("input", await File.ReadAllBytesAsync(input), inputOracle); var cases = root.GetProperty("cases"); var statesChecked = 0;
        foreach (var item in cases.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!; var paneCount = item.GetProperty("paneCount").GetInt32(); var expected = item.GetProperty("states");
            var readOnly = item.GetProperty("readOnly").EnumerateArray().Any(v => v.GetBoolean());
            var actions = item.GetProperty("actions").EnumerateArray().Select(a => (object)a.Clone()).ToList(); var exports = new List<string>();
            for (var pane = 0; pane < paneCount; pane++)
            {
                var png = Convert.FromBase64String(item.GetProperty("finalRaw")[pane].GetProperty("pngBase64").GetString()!);
                check("image-transforms-" + name + "-original-png-sha-" + pane, Sha(png) == item.GetProperty("finalRaw")[pane].GetProperty("pngSha256").GetString(), Sha(png));
                VerifyPng(name + "-original-" + pane, png, item.GetProperty("finalRaw")[pane]);
                if (readOnly) continue;
                var destination = Path.Combine(proof, name + "-raw-" + pane + ".png"); exports.Add(destination);
                actions.Add(new { kind = "export", dst = pane, path = destination });
            }
            var script = Script(name, actions, item); var scriptHash = Hash(script);
            var result = await run("image-transforms-" + name, 0, true, ["--image-copy", .. Enumerable.Repeat(input, paneCount), "--script", script]);
            using var actual = JsonDocument.Parse(result.Stdout); var states = actual.RootElement.GetProperty("states");
            check("image-transforms-" + name + "-state-count", states.GetArrayLength() == expected.GetArrayLength() + exports.Count, "state count");
            for (var index = 0; index < Math.Min(expected.GetArrayLength(), states.GetArrayLength()); index++)
            {
                var wanted = expected[index]; var observed = index == 0 ? states[index] : states[index].GetProperty("state");
                var prefix = "image-transforms-" + name + "-state" + index;
                if (index > 0) check(prefix + "-result", states[index].GetProperty("actionResult").GetInt32() == wanted.GetProperty("actionResult").GetInt32(), "action result");
                foreach (var property in new[] { "differenceCount", "conflictCount", "orientations" })
                    check(prefix + "-" + property, JsonElement.DeepEquals(observed.GetProperty(property), wanted.GetProperty(property)), observed.GetProperty(property).GetRawText());
                var history = observed.GetProperty("history");
                foreach (var property in new[] { "undoable", "redoable" }) check(prefix + "-" + property, JsonElement.DeepEquals(history.GetProperty(property), wanted.GetProperty(property)), property);
                var frames = observed.GetProperty("frames"); check(prefix + "-pane-count", frames.GetArrayLength() == paneCount, "pane count");
                for (var pane = 0; pane < Math.Min(paneCount, frames.GetArrayLength()); pane++)
                {
                    VerifyFrame(prefix + "-pane" + pane, frames[pane], wanted.GetProperty("frames")[pane]);
                    foreach (var property in new[] { "modified", "savepoint" }) check(prefix + "-pane" + pane + "-" + property,
                        JsonElement.DeepEquals(history.GetProperty("panes")[pane].GetProperty(property), wanted.GetProperty("panes")[pane].GetProperty(property)), property);
                }
                statesChecked++;
            }
            for (var pane = 0; pane < exports.Count; pane++)
            {
                VerifyPng(name + "-export-" + pane, await File.ReadAllBytesAsync(exports[pane]), item.GetProperty("finalRaw")[pane]);
                var exported = states[expected.GetArrayLength() + pane]; var view = exported.GetProperty("state"); var final = expected[expected.GetArrayLength() - 1];
                check("image-transforms-" + name + "-export-result-" + pane, exported.GetProperty("actionResult").GetInt32() == 1
                    && JsonElement.DeepEquals(view.GetProperty("orientations"), final.GetProperty("orientations")), "export keeps view orientation");
                for (var p = 0; p < paneCount; p++) VerifyFrame("image-transforms-" + name + "-export" + pane + "-view" + p, view.GetProperty("frames")[p], final.GetProperty("frames")[p]);
            }
            check("image-transforms-" + name + "-inputs-preserved", Hash(input) == inputHash && File.GetAttributes(input) == inputAttrs && Hash(script) == scriptHash, "input and script immutable");
        }
        check("image-transforms-case-count", cases.GetArrayLength() == 288, "32 display / 256 copy");
        check("image-transforms-state-count", statesChecked == 2816, statesChecked.ToString());
        await ImageTransformProjectScenarios.RunAsync(output, folder, input, root, run, check);
        var sample = cases[0];
        foreach (var (kind, value) in new[] { ("rotate", -90), ("rotate", 45), ("rotate", 360), ("flipx", -1), ("flipx", 2), ("flipy", 2) })
            await Reject(kind + "-" + value, [new { kind, dst = 0, index = value }], sample, null);
        await Reject("missing-index", [new { kind = "rotate", dst = 0 }], sample, null);
        await Reject("missing-dst", [new { kind = "flipx", index = 1 }], sample, null);
        await Reject("invalid-pane", [new { kind = "rotate", dst = 2, index = 90 }], sample, null);
        var sentinel = Path.Combine(proof, "readonly-export.png"); await File.WriteAllTextAsync(sentinel, "readonly output sentinel");
        var readonlyCase = cases.EnumerateArray().First(c => c.GetProperty("readOnly")[0].GetBoolean());
        await Reject("readonly-export", [new { kind = "rotate", dst = 0, index = 90 }, new { kind = "export", dst = 0, path = sentinel }], readonlyCase, sentinel);
        await File.WriteAllTextAsync(Path.Combine(proof, "counts.json"), JsonSerializer.Serialize(new { cases = 288, states = statesChecked, inputSha256 = inputHash }));

        string Script(string name, IReadOnlyList<object> actions, JsonElement item)
        {
            var path = Path.Combine(folder, name + ".json"); File.WriteAllText(path, JsonSerializer.Serialize(new { blockSize = item.GetProperty("blockSize").GetInt32(), threshold = item.GetProperty("threshold").GetDouble(), readOnly = item.GetProperty("readOnly").Clone(), actions })); return path;
        }
        async Task Reject(string name, object[] actions, JsonElement item, string? destination)
        {
            var script = Script("reject-" + name, actions, item); var scriptHash = Hash(script); var before = destination is null ? "" : Hash(destination); var attrs = destination is null ? default : File.GetAttributes(destination);
            var result = await run("image-transforms-reject-" + name, 2, false, ["--image-copy", .. Enumerable.Repeat(input, item.GetProperty("paneCount").GetInt32()), "--script", script]);
            check("image-transforms-reject-" + name, string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr) && Hash(input) == inputHash && Hash(script) == scriptHash
                && (destination is null || Hash(destination) == before && File.GetAttributes(destination) == attrs), result.Stderr);
        }
        void VerifyFrame(string label, JsonElement actual, JsonElement expected)
        {
            var raw = Convert.FromBase64String(actual.GetProperty("bgraBase64").GetString()!); var wanted = Convert.FromBase64String(expected.GetProperty("bgraBase64").GetString()!);
            check(label + "-BGRA", actual.GetProperty("width").GetInt32() == expected.GetProperty("width").GetInt32() && actual.GetProperty("height").GetInt32() == expected.GetProperty("height").GetInt32()
                && raw.SequenceEqual(wanted) && string.Equals(Sha(raw), expected.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(actual.GetProperty("sha256").GetString(), expected.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), Sha(raw));
        }
        void VerifyPng(string label, byte[] png, JsonElement expected)
        {
            var decoded = ImageReportScenarios.DecodePng(png); var wanted = Convert.FromBase64String(expected.GetProperty("bgraBase64").GetString()!);
            check("image-transforms-png-" + label, decoded.Width == expected.GetProperty("width").GetInt32() && decoded.Height == expected.GetProperty("height").GetInt32() && decoded.Bgra.SequenceEqual(wanted) && Sha(decoded.Bgra) == expected.GetProperty("sha256").GetString(), Sha(decoded.Bgra));
        }
    }
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Hash(string path) => Sha(File.ReadAllBytes(path));
    private static string FindFixtures()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }) for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        { var path = Path.Combine(d.FullName, "tests", "Fixtures", "ImageTransforms"); if (File.Exists(Path.Combine(path, "winimerge-transforms-golden.json"))) return path; }
        throw new FileNotFoundException("Transform fixtures not found");
    }
}
