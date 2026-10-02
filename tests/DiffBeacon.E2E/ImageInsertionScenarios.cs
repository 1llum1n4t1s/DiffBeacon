using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ImageInsertionScenarios
{
    // 失敗先行契約はImageInsertions/README.md。全期待画素・状態は原本DLLから採取。
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiffBeacon.slnx"))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("ImageInsertions fixture root");
        var zip = await File.ReadAllBytesAsync(Path.Combine(root.FullName, "tests", "Fixtures", "ImageInsertions", "winimerge-insertions-golden.json.gz"));
        var zipSha = Convert.ToHexString(SHA256.HashData(zip));
        const string pinnedZip = "2B0275994E7445F8BF4A745CF7DCE8BBF531C647D43095751F12AE6278A7AF21";
        check("image-insertions-gzip-sha", zipSha == pinnedZip, zipSha);
        if (zipSha != pinnedZip) throw new InvalidDataException("Insertion golden changed");
        using var packed = new MemoryStream(zip); using var gzip = new GZipStream(packed, CompressionMode.Decompress);
        using var content = new MemoryStream(); await gzip.CopyToAsync(content);
        var bytes = content.ToArray(); var sha = Convert.ToHexString(SHA256.HashData(bytes));
        const string pinned = "991FC3F5CBAA2B039D6320E85AB9ADE0F6BB2CE186FD5AFFC30BA43D4777EDDB";
        check("image-insertions-source-sha", sha == pinned, sha);
        if (sha != pinned) throw new InvalidDataException("Insertion observations changed");
        using var golden = JsonDocument.Parse(bytes);
        var folder = Path.Combine(fixtures, "image-insertions"); Directory.CreateDirectory(folder);
        var proof = Path.Combine(output, "image-insertions"); Directory.CreateDirectory(proof);
        var statesChecked = 0; var casesChecked = 0;
        foreach (var item in golden.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!; var inputs = item.GetProperty("inputs");
            var paths = new string[inputs.GetArrayLength()]; var hashes = new string[paths.Length];
            var readOnly = item.GetProperty("readOnly").EnumerateArray().Select(x => x.GetInt32() != 0).ToArray();
            for (var pane = 0; pane < paths.Length; pane++)
            {
                paths[pane] = Path.Combine(folder, name + "-" + pane + ".png");
                var png = Convert.FromBase64String(inputs[pane].GetProperty("pngBase64").GetString()!);
                await File.WriteAllBytesAsync(paths[pane], png); hashes[pane] = Convert.ToHexString(SHA256.HashData(png));
            }
            var actions = item.GetProperty("actions").EnumerateArray().Select(value => (object)new
            { kind = value[0].GetString(), src = value[1].GetInt32(), dst = value[2].GetInt32(), index = value[3].GetInt32() }).ToList();
            var exports = new string[paths.Length];
            for (var pane = 0; pane < paths.Length; pane++) if (!readOnly[pane])
            {
                exports[pane] = Path.Combine(proof, name + "-raw-" + pane + ".png");
                actions.Add(new { kind = "export", dst = pane, path = exports[pane] });
            }
            var script = Path.Combine(folder, name + ".json");
            await File.WriteAllTextAsync(script, JsonSerializer.Serialize(new
            { blockSize = item.GetProperty("blockSize").GetInt32(), threshold = item.GetProperty("threshold").GetDouble(),
                includeAlignment = true, includeOffsets = true, readOnly, actions }));
            var scriptSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(script)));
            var result = await run("image-insertions-" + name, 0, true, ["--image-copy", .. paths, "--script", script]);
            if (result.ExitCode != 0) continue;
            using var actual = JsonDocument.Parse(result.Stdout);
            var observedStates = actual.RootElement.GetProperty("states"); var expectedStates = item.GetProperty("states");
            check("image-insertions-" + name + "-state-count", observedStates.GetArrayLength() == expectedStates.GetArrayLength() + readOnly.Count(x => !x), "original states plus raw exports");
            for (var i = 0; i < Math.Min(observedStates.GetArrayLength(), expectedStates.GetArrayLength()); i++)
            {
                var observed = i == 0 ? observedStates[i] : observedStates[i].GetProperty("state"); var expected = expectedStates[i];
                var label = "image-insertions-" + name + "-state" + i;
                foreach (var property in new[] { "mode", "differenceCount", "conflictCount" })
                    check(label + "-" + property, observed.GetProperty(property).GetInt32() == expected.GetProperty(property).GetInt32(), "original state");
                if (i > 0) check(label + "-action-result", observedStates[i].GetProperty("actionResult").GetInt32() == expected.GetProperty("actionResult").GetInt32(), "original action result");
                foreach (var property in new[] { "undoable", "redoable" })
                    check(label + "-" + property, observed.GetProperty("history").GetProperty(property).GetBoolean() == expected.GetProperty(property).GetBoolean(), "shared raw history");
                for (var pane = 0; pane < paths.Length; pane++)
                {
                    var wanted = expected.GetProperty("panes")[pane]; var aligned = observed.GetProperty("aligned")[pane];
                    var frame = observed.GetProperty("frames")[pane]; var position = observed.GetProperty("offsets")[pane];
                    var orientation = observed.GetProperty("orientations")[pane]; var state = observed.GetProperty("history").GetProperty("panes")[pane];
                    check(label + "-dimensions-" + pane, frame.GetProperty("width").GetInt32() == wanted.GetProperty("width").GetInt32()
                        && frame.GetProperty("height").GetInt32() == wanted.GetProperty("height").GetInt32(), "original view dimensions");
                    check(label + "-offset-" + pane, position.GetProperty("x").GetInt32() == wanted.GetProperty("offsetX").GetInt32()
                        && position.GetProperty("y").GetInt32() == wanted.GetProperty("offsetY").GetInt32(), "offset retained after copy and Undo");
                    check(label + "-orientation-" + pane, orientation.GetProperty("rotation").GetInt32() == wanted.GetProperty("angle").GetInt32()
                        && orientation.GetProperty("flipHorizontal").GetBoolean() == wanted.GetProperty("flipx").GetBoolean()
                        && orientation.GetProperty("flipVertical").GetBoolean() == wanted.GetProperty("flipy").GetBoolean(), "view transform");
                    check(label + "-dirty-" + pane, state.GetProperty("modified").GetBoolean() == wanted.GetProperty("modified").GetBoolean()
                        && state.GetProperty("savepoint").GetInt32() == wanted.GetProperty("savepoint").GetInt32(), "dirty and savepoint");
                    check(label + "-canvas-" + pane, aligned.GetProperty("canvasWidth").GetInt32() == wanted.GetProperty("canvasWidth").GetInt32()
                        && aligned.GetProperty("canvasHeight").GetInt32() == wanted.GetProperty("canvasHeight").GetInt32(), "common canvas");
                    check(label + "-all-bgra-" + pane, Convert.FromHexString(aligned.GetProperty("bgraHex").GetString()!).SequenceEqual(
                        Convert.FromHexString(wanted.GetProperty("bgraHex").GetString()!)), "all aligned original BGRA");
                    static IEnumerable<string> Points(JsonElement values) => values.EnumerateArray().Select(value => string.Join(",", value.EnumerateArray().Select(part => part.ToString())));
                    check(label + "-all-mapping-" + pane, Points(aligned.GetProperty("mapping")).SequenceEqual(Points(wanted.GetProperty("mapping"))), "whole canvas and perimeter inverse coordinates");
                }
                statesChecked++;
            }
            for (var pane = 0; pane < paths.Length; pane++)
            {
                check("image-insertions-" + name + "-input-" + pane, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(paths[pane]))) == hashes[pane], "original PNG unchanged");
                if (readOnly[pane]) continue;
                var decoded = ImageReportScenarios.DecodePng(await File.ReadAllBytesAsync(exports[pane]));
                var wanted = item.GetProperty("exports")[pane];
                check("image-insertions-" + name + "-raw-export-" + pane, decoded.Width == wanted.GetProperty("width").GetInt32()
                    && decoded.Height == wanted.GetProperty("height").GetInt32() && decoded.Bgra.SequenceEqual(Convert.FromHexString(wanted.GetProperty("bgraHex").GetString()!)), "independent PNG dimensions and every raw pixel");
            }
            check("image-insertions-" + name + "-script-preserved", Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(script))) == scriptSha, "script SHA");
            casesChecked++;
        }
        check("image-insertions-original-cases", casesChecked == 58, casesChecked.ToString());
        check("image-insertions-original-states", statesChecked == 304, statesChecked.ToString());
        await File.WriteAllTextAsync(Path.Combine(output, "image-insertions-proof.json"), JsonSerializer.Serialize(new
        { cases = casesChecked, states = statesChecked, sourceSha256 = sha, gzipSha256 = zipSha }));
    }
}
