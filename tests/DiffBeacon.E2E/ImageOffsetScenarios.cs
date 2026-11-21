using System.Security.Cryptography;
using System.Text.Json;

internal static class ImageOffsetScenarios
{
    // 失敗先行: ImageOffsets/README.md。コピー期待画素は原本観測だけを使う。
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var source = FindFixtures();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(source, "winimerge-offsets-golden.json"));
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        const string pinned = "89C0CECCEFAC194294200E1EB227595C223E9FB96897FF2066113877DFD48E50";
        check("image-offsets-source-sha", sha == pinned, sha);
        if (sha != pinned) throw new InvalidDataException("Offset golden changed");
        using var golden = JsonDocument.Parse(bytes);
        var root = golden.RootElement;
        check("image-offsets-provenance", root.GetProperty("sourceRevision").GetString() == "da639cdfaeca87aaad0eaceec509afa11ad61421"
            && root.GetProperty("dllSha256").GetString() == "36F2A726C34A2323D2E569903B4F1DC6C28ABF1C10447006335F2785FC511BA6", "WinIMerge v1.0.54");
        check("image-offsets-extractor-sha", Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(source, "extract.py"))))
            == root.GetProperty("extractorSha256").GetString(), "extractor provenance");
        var folder = Path.Combine(fixtures, "image-offsets"); Directory.CreateDirectory(folder);
        var proof = Path.Combine(output, "image-offsets"); Directory.CreateDirectory(proof);
        await File.WriteAllBytesAsync(Path.Combine(proof, "golden.json"), bytes);
        var statesChecked = 0;
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var inputs = item.GetProperty("inputs"); var count = inputs.GetArrayLength();
            var paths = new string[count]; var hashes = new string[count]; var attributes = new FileAttributes[count];
            for (var pane = 0; pane < count; pane++)
            {
                paths[pane] = Path.Combine(folder, name + "-" + pane + ".png");
                var png = Convert.FromBase64String(inputs[pane].GetProperty("pngBase64").GetString()!);
                await File.WriteAllBytesAsync(paths[pane], png);
                hashes[pane] = Convert.ToHexString(SHA256.HashData(png)); attributes[pane] = File.GetAttributes(paths[pane]);
                check("image-offsets-" + name + "-input-" + pane, hashes[pane] == inputs[pane].GetProperty("pngSha256").GetString(), "input SHA");
            }
            var actions = item.GetProperty("actions").EnumerateArray().Select(value => (object)new
            { kind = value[0].GetString(), src = value[1].GetInt32(), dst = value[2].GetInt32(), index = value[3].GetInt32() }).ToList();
            var exports = new string[count];
            var readOnly = item.GetProperty("readOnly").EnumerateArray().Select(value => value.GetInt32() != 0).ToArray();
            for (var pane = 0; pane < count; pane++) if (!readOnly[pane])
            {
                exports[pane] = Path.Combine(proof, name + "-raw-" + pane + ".png");
                actions.Add(new { kind = "export", dst = pane, path = exports[pane] });
            }
            var script = Path.Combine(folder, name + ".json");
            await File.WriteAllTextAsync(script, JsonSerializer.Serialize(new
            { blockSize = item.GetProperty("blockSize").GetInt32(), includeOffsets = true, readOnly, actions }));
            var scriptHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(script)));
            var result = await run("image-offsets-" + name, 0, true, ["--image-copy", .. paths, "--script", script]);
            using var actual = JsonDocument.Parse(result.Stdout);
            var states = actual.RootElement.GetProperty("states"); var expected = item.GetProperty("states");
            check("image-offsets-" + name + "-state-count", states.GetArrayLength() == expected.GetArrayLength() + readOnly.Count(value => !value), "state count");
            for (var i = 0; i < Math.Min(states.GetArrayLength(), expected.GetArrayLength()); i++)
            {
                var wanted = expected[i]; var observed = i == 0 ? states[i] : states[i].GetProperty("state");
                var label = "image-offsets-" + name + "-state" + i;
                foreach (var property in new[] { "differenceCount", "conflictCount" })
                    check(label + "-" + property, observed.GetProperty(property).GetInt32() == wanted.GetProperty(property).GetInt32(), property);
                if (i > 0) check(label + "-action-result", states[i].GetProperty("actionResult").GetInt32() == wanted.GetProperty("actionResult").GetInt32(), "result");
                foreach (var property in new[] { "undoable", "redoable" })
                    check(label + "-" + property, observed.GetProperty("history").GetProperty(property).GetBoolean() == wanted.GetProperty(property).GetBoolean(), property);
                var frames = observed.GetProperty("frames"); var panes = wanted.GetProperty("panes");
                for (var pane = 0; pane < count; pane++)
                {
                    var frame = frames[pane]; var p = panes[pane]; var offset = observed.GetProperty("offsets")[pane];
                    check(label + "-coordinates-" + pane, offset.GetProperty("x").GetInt32() == p.GetProperty("offsetX").GetInt32()
                        && offset.GetProperty("y").GetInt32() == p.GetProperty("offsetY").GetInt32(), "coordinates");
                    check(label + "-pixels-" + pane, frame.GetProperty("width").GetInt32() == p.GetProperty("width").GetInt32()
                        && frame.GetProperty("height").GetInt32() == p.GetProperty("height").GetInt32()
                        && Convert.FromBase64String(frame.GetProperty("bgraBase64").GetString()!).AsSpan().SequenceEqual(Convert.FromHexString(p.GetProperty("bgraHex").GetString()!)), "full BGRA");
                    var orientation = observed.GetProperty("orientations")[pane];
                    check(label + "-orientation-" + pane, orientation.GetProperty("rotation").GetInt32() == p.GetProperty("angle").GetInt32()
                        && orientation.GetProperty("flipHorizontal").GetBoolean() == p.GetProperty("flipx").GetBoolean()
                        && orientation.GetProperty("flipVertical").GetBoolean() == p.GetProperty("flipy").GetBoolean(), "orientation");
                    check(label + "-dirty-" + pane, observed.GetProperty("history").GetProperty("panes")[pane].GetProperty("modified").GetBoolean() == p.GetProperty("modified").GetBoolean(), "dirty");
                    check(label + "-savepoint-" + pane, observed.GetProperty("history").GetProperty("panes")[pane].GetProperty("savepoint").GetInt32() == p.GetProperty("savepoint").GetInt32(), "savepoint");
                }
                statesChecked++;
            }
            for (var pane = 0; pane < count; pane++)
            {
                check("image-offsets-" + name + "-input-preserved-" + pane,
                    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(paths[pane]))) == hashes[pane]
                    && File.GetAttributes(paths[pane]) == attributes[pane], "bytes and attributes");
                if (readOnly[pane]) continue;
                var decoded = ImageReportScenarios.DecodePng(await File.ReadAllBytesAsync(exports[pane]));
                var wanted = item.GetProperty("exports")[pane];
                check("image-offsets-" + name + "-raw-export-" + pane,
                    decoded.Width == wanted.GetProperty("width").GetInt32() && decoded.Height == wanted.GetProperty("height").GetInt32()
                    && decoded.Bgra.AsSpan().SequenceEqual(Convert.FromHexString(wanted.GetProperty("bgraHex").GetString()!)), "independent PNG full BGRA");
            }
            check("image-offsets-" + name + "-script-preserved", Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(script))) == scriptHash, "script SHA");
        }
        check("image-offsets-all-original-states", statesChecked == 43, statesChecked.ToString());
        // 先行exportも後続の不正座標で公開されない。巨大canvasは確保前に拒否する。
        var protectedInput = Path.Combine(folder, "normalize-two-0.png");
        var inputHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(protectedInput)));
        var inputAttributes = File.GetAttributes(protectedInput);
        foreach (var invalid in new[] { (Name: "max", X: int.MaxValue, Y: 0, Pane: 1),
            (Name: "min", X: int.MinValue, Y: 0, Pane: 1), (Name: "canvas", X: 10000, Y: 10000, Pane: 1),
            (Name: "pane", X: 1, Y: 0, Pane: 2) })
        {
            var target = Path.Combine(proof, "protected-" + invalid.Name + ".png");
            await File.WriteAllTextAsync(target, "existing destination");
            var original = await File.ReadAllBytesAsync(target); var attributes = File.GetAttributes(target);
            var script = Path.Combine(folder, "invalid-" + invalid.Name + ".json");
            await File.WriteAllTextAsync(script, JsonSerializer.Serialize(new { actions = new object[] {
                new { kind = "export", dst = 1, path = target },
                new { kind = "offset", src = invalid.X, dst = invalid.Pane, index = invalid.Y } } }));
            var scriptHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(script)));
            await run("image-offsets-invalid-" + invalid.Name, 2, false, ["--image-copy", protectedInput, protectedInput, "--script", script]);
            check("image-offsets-invalid-" + invalid.Name + "-output-preserved", (await File.ReadAllBytesAsync(target)).AsSpan().SequenceEqual(original)
                && File.GetAttributes(target) == attributes, "bytes and attributes");
            check("image-offsets-invalid-" + invalid.Name + "-input-preserved", Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(protectedInput))) == inputHash
                && File.GetAttributes(protectedInput) == inputAttributes, "bytes and attributes");
            check("image-offsets-invalid-" + invalid.Name + "-script-preserved", Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(script))) == scriptHash, "script SHA");
        }
        await ImageOffsetProjectScenarios.RunAsync(output, fixtures, run, check);
    }

    private static string FindFixtures()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "tests", "Fixtures", "ImageOffsets");
                if (File.Exists(Path.Combine(path, "winimerge-offsets-golden.json"))) return path;
            }
        throw new FileNotFoundException("Offset fixtures not found");
    }
}
