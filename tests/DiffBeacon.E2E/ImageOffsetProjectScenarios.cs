using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class ImageOffsetProjectScenarios
{
    // 失敗先行契約: E:/DiffBeacon-artifacts/local/image-offsets/project-contract.md。
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var source = FindFixtures();
        var goldenBytes = await File.ReadAllBytesAsync(Path.Combine(source, "winimerge-offsets-golden.json"));
        const string goldenSha = "89C0CECCEFAC194294200E1EB227595C223E9FB96897FF2066113877DFD48E50";
        check("image-offset-project-source-sha", Convert.ToHexString(SHA256.HashData(goldenBytes)) == goldenSha, goldenSha);
        if (Convert.ToHexString(SHA256.HashData(goldenBytes)) != goldenSha) throw new InvalidDataException("Offset golden changed");
        using var golden = JsonDocument.Parse(goldenBytes);
        var folder = Path.Combine(fixtures, "image-offset-project"); Directory.CreateDirectory(folder);
        var proof = Path.Combine(output, "image-offset-project"); Directory.CreateDirectory(proof);
        await File.WriteAllBytesAsync(Path.Combine(proof, "golden.json"), goldenBytes);
        var tracked = new Dictionary<string, (string Hash, FileAttributes Attributes)>();
        var observations = new List<object>();
        string[] sides = ["left", "middle", "right"];
        // この状態は位置だけが変わり、原画は入力と同じ。回転・コピー後の状態は使わない。
        foreach (var (name, stateIndex) in new[] { ("normalize-two", 1), ("normalize-three", 2),
            ("different-shape", 1), ("nonoverlap", 1), ("far-third-block8", 1), ("offset-first", 1) })
        {
            var item = golden.RootElement.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("name").GetString() == name);
            var inputs = item.GetProperty("inputs"); var state = item.GetProperty("states")[stateIndex];
            var panes = state.GetProperty("panes"); var count = inputs.GetArrayLength();
            var paths = new string[count]; var offsets = new (int X, int Y)[count];
            for (var pane = 0; pane < count; pane++)
            {
                var input = inputs[pane]; var expected = panes[pane];
                check("image-offset-project-" + name + "-oracle-" + pane,
                    expected.GetProperty("angle").GetInt32() == 0 && !expected.GetProperty("flipx").GetBoolean()
                    && !expected.GetProperty("flipy").GetBoolean() && expected.GetProperty("bgraHex").GetString() == input.GetProperty("bgraHex").GetString(), "unmodified original pixels");
                paths[pane] = Path.Combine(folder, name + "-" + pane + ".png");
                await File.WriteAllBytesAsync(paths[pane], Convert.FromBase64String(input.GetProperty("pngBase64").GetString()!));
                check("image-offset-project-" + name + "-input-sha-" + pane, Hash(paths[pane]) == input.GetProperty("pngSha256").GetString(), "original input PNG"); Track(paths[pane]);
                offsets[pane] = (expected.GetProperty("offsetX").GetInt32(), expected.GetProperty("offsetY").GetInt32());
            }
            var settings = Settings(offsets); settings["blockSize"] = item.GetProperty("blockSize").GetInt32();
            var project = Project(name, paths, settings); var options = Options(offsets);
            var direct = await run("image-offset-project-" + name + "-direct", state.GetProperty("differenceCount").GetInt32() == 0 ? 0 : 1, true,
                ["--image", .. paths, "--block-size", item.GetProperty("blockSize").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), .. options]);
            using (var parsed = JsonDocument.Parse(direct.Stdout))
            {
                var frame = parsed.RootElement.GetProperty("frames")[0];
                check("image-offset-project-" + name + "-regions", frame.GetProperty("differenceCount").GetInt32() == state.GetProperty("differenceCount").GetInt32()
                    && frame.GetProperty("conflictCount").GetInt32() == state.GetProperty("conflictCount").GetInt32(), frame.GetRawText());
                for (var pane = 0; pane < count; pane++)
                {
                    var side = sides[count == 2 && pane == 1 ? 2 : pane]; var input = inputs[pane];
                    check("image-offset-project-" + name + "-raw-" + side,
                        frame.GetProperty(side + "Width").GetInt32() == input.GetProperty("width").GetInt32()
                        && frame.GetProperty(side + "Height").GetInt32() == input.GetProperty("height").GetInt32()
                        && string.Equals(frame.GetProperty(side + "PixelSha256").GetString(), panes[pane].GetProperty("bgraSha256").GetString(), StringComparison.OrdinalIgnoreCase), "raw dimensions and full pixel SHA");
                }
            }
            var copy = Path.Combine(proof, name + "-copy.json"); await run("image-offset-project-" + name + "-copy", 0, true, ["--project-copy", project, copy]); VerifySettings(name + "-copy", copy, offsets, paths); Track(copy);
            var copyAgain = Path.Combine(proof, name + "-copy-again.json"); await run("image-offset-project-" + name + "-copy-again", 0, true, ["--project-copy", copy, copyAgain]); VerifySettings(name + "-roundtrip", copyAgain, offsets, paths); Track(copyAgain);
            var report = Path.Combine(proof, name + ".html"); await run("image-offset-project-" + name + "-report", 0, true, ["--report-project", copyAgain, report]); VerifyHtml(name + "-report", report, inputs, offsets);
            var zeros = new (int X, int Y)[count]; var overridden = Path.Combine(proof, name + "-override.html");
            var copyHash = Hash(copyAgain); var copyAttrs = File.GetAttributes(copyAgain);
            await run("image-offset-project-" + name + "-override", 0, true, ["--report-project", copyAgain, overridden, .. Options(zeros)]); VerifyHtml(name + "-override", overridden, inputs, zeros);
            check("image-offset-project-" + name + "-override-project-preserved", Hash(copyAgain) == copyHash && File.GetAttributes(copyAgain) == copyAttrs, "CLI override does not save project");
            var zip = Path.Combine(proof, name + ".zip"); await run("image-offset-project-" + name + "-package", 0, true, ["--package-project", copy, zip, "--report"]);
            var unpacked = Path.Combine(proof, name + "-unpacked"); ZipFile.ExtractToDirectory(zip, unpacked);
            var restored = Path.Combine(unpacked, "project.json"); VerifySettings(name + "-packaged", restored, offsets, paths);
            VerifyHtml(name + "-packaged", Path.Combine(unpacked, "report.files", "1.html"), inputs, offsets);
            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(restored)))
            {
                var entry = Entry(document.RootElement);
                for (var pane = 0; pane < count; pane++)
                {
                    var key = pane == 0 ? "leftPath" : count == 3 && pane == 1 ? "basePath" : "rightPath";
                    var reference = entry.GetProperty(key).GetString()!; var local = Path.GetFullPath(Path.Combine(unpacked, reference));
                    check("image-offset-project-" + name + "-package-path-" + pane, !Path.IsPathRooted(reference)
                        && local.StartsWith(Path.GetFullPath(unpacked) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(local) && Hash(local) == Hash(paths[pane]), reference);
                }
            }
            var reloaded = Path.Combine(proof, name + "-restored.html"); await run("image-offset-project-" + name + "-restored", 0, true, ["--report-project", restored, reloaded]); VerifyHtml(name + "-restored", reloaded, inputs, offsets);
            observations.Add(new { name, stateIndex, project, paths, offsets = offsets.Select(p => new { x = p.X, y = p.Y }), report, overridden, zip, restored });
        }
        var basic = golden.RootElement.GetProperty("cases")[0].GetProperty("inputs");
        var two = new[] { Path.Combine(folder, "normalize-two-0.png"), Path.Combine(folder, "normalize-two-1.png") };
        var omitted = Project("omitted", two, new Dictionary<string, object?> { ["showDifferences"] = false, ["reportAllFrames"] = false });
        var omittedCopy = Path.Combine(proof, "omitted-copy.json"); await run("image-offset-project-omitted-copy", 0, true, ["--project-copy", omitted, omittedCopy]); VerifySettings("omitted", omittedCopy, new (int X, int Y)[2], two, true);
        var omittedHtml = Path.Combine(proof, "omitted.html"); await run("image-offset-project-omitted-report", 0, true, ["--report-project", omittedCopy, omittedHtml]); VerifyHtml("omitted", omittedHtml, basic, new (int X, int Y)[2]);
        foreach (var (name, value) in new (string, object?)[] { ("null", null), ("scalar", 1), ("array", new[] { 0, 0 }),
            ("negative-x", new { x = -1, y = 0 }), ("negative-y", new { x = 0, y = -1 }), ("fraction", new { x = .5, y = 0 }),
            ("string", new { x = "1", y = 0 }), ("null-x", new { x = (object?)null, y = 0 }), ("overflow", new { x = 2147483648L, y = 0 }) })
        {
            foreach (var field in new[] { "leftOffset", "middleOffset", "rightOffset" })
            {
                var settings = Settings(new (int X, int Y)[3]); settings[field] = value;
                await Reject(name + "-" + field, Project(name + "-" + field, [two[0], two[0], two[1]], settings), false);
            }
        }
        var middle = Settings(new (int X, int Y)[2]); middle["middleOffset"] = new { x = 1, y = 0 };
        await Reject("middle-without-input", Project("middle-without-input", two, middle), false);
        foreach (var coordinate in new[] { "-1,0", "0,-1", "1.5,0", "2147483648,0", "1", "1,2,3", "bad,0" })
            await RejectDirect("syntax-" + coordinate.Replace(',', '_'), ["--left-offset", coordinate]);
        await RejectDirect("middle-without-input", ["--middle-offset", "1,0"]);
        await RejectDirect("duplicate", ["--left-offset", "0,0", "--left-offset", "1,0"]);
        await RejectDirect("missing-value", ["--right-offset"]);
        foreach (var coordinate in new[] { (X: 10000, Y: 10000), (X: int.MaxValue, Y: 0) })
        {
            var settings = Settings([(0, 0), coordinate]);
            await Reject("canvas-" + coordinate.X, Project("canvas-" + coordinate.X, two, settings), true);
            await RejectDirect("canvas-" + coordinate.X, ["--right-offset", coordinate.X + "," + coordinate.Y]);
        }
        foreach (var input in tracked) check("image-offset-project-input-preserved-" + Path.GetFileName(input.Key), Hash(input.Key) == input.Value.Hash && File.GetAttributes(input.Key) == input.Value.Attributes, input.Key);
        await File.WriteAllTextAsync(Path.Combine(proof, "observations.json"), JsonSerializer.Serialize(new { goldenSha, cases = observations, inputs = tracked.Select(p => new { path = p.Key, sha256 = p.Value.Hash, attributes = (int)p.Value.Attributes }) }, new JsonSerializerOptions { WriteIndented = true }));

        void Track(string path) => tracked[path] = (Hash(path), File.GetAttributes(path));
        Dictionary<string, object?> Settings((int X, int Y)[] positions)
        {
            var result = new Dictionary<string, object?> { ["blockSize"] = 1, ["showDifferences"] = false, ["reportAllFrames"] = false };
            for (var pane = 0; pane < positions.Length; pane++) result[sides[positions.Length == 2 && pane == 1 ? 2 : pane] + "Offset"] = new { x = positions[pane].X, y = positions[pane].Y };
            return result;
        }
        string[] Options((int X, int Y)[] positions) => positions.SelectMany((p, i) => new[] { "--" + sides[positions.Length == 2 && i == 1 ? 2 : i] + "-offset", p.X + "," + p.Y }).ToArray();
        string Project(string name, string[] paths, Dictionary<string, object?> settings)
        {
            var path = Path.Combine(folder, name + ".json"); File.WriteAllText(path, JsonSerializer.Serialize(new { formatVersion = 1, entries = new[] { new { leftPath = paths[0], basePath = paths.Length == 3 ? paths[1] : "", rightPath = paths[^1], mode = "Image", imageSettings = settings } }, activeEntryIndex = 0 })); Track(path); return path;
        }
        void VerifySettings(string label, string path, (int X, int Y)[] wanted, string[] paths, bool allowOmitted = false)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path)); var entry = Entry(document.RootElement); var settings = entry.GetProperty("imageSettings");
            for (var pane = 0; pane < paths.Length; pane++)
            {
                var key = pane == 0 ? "leftPath" : paths.Length == 3 && pane == 1 ? "basePath" : "rightPath";
                var reference = entry.GetProperty(key).GetString()!; var local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, reference));
                check("image-offset-project-settings-path-" + label + "-" + pane, File.Exists(local) && Hash(local) == Hash(paths[pane]), reference);
            }
            foreach (var side in sides)
            {
                var pane = side == "left" ? 0 : side == "right" ? wanted.Length - 1 : wanted.Length == 3 ? 1 : -1;
                var expected = pane < 0 ? (X: 0, Y: 0) : wanted[pane];
                var found = settings.TryGetProperty(side + "Offset", out var actual);
                check("image-offset-project-settings-" + label + "-" + side, !found ? (allowOmitted || pane < 0) && expected == (0, 0)
                    : actual.GetProperty("x").GetInt32() == expected.X && actual.GetProperty("y").GetInt32() == expected.Y, settings.GetRawText());
            }
        }
        void VerifyHtml(string label, string path, JsonElement inputs, (int X, int Y)[] positions)
        {
            var tags = Regex.Matches(File.ReadAllText(path), "<img\\b[^>]*>", RegexOptions.CultureInvariant).Cast<Match>().ToArray();
            var width = inputs.EnumerateArray().Select((p, i) => p.GetProperty("width").GetInt32() + positions[i].X).Max();
            var height = inputs.EnumerateArray().Select((p, i) => p.GetProperty("height").GetInt32() + positions[i].Y).Max();
            for (var pane = 0; pane < positions.Length; pane++)
                foreach (var original in new[] { false, true })
                {
                    var side = sides[positions.Length == 2 && pane == 1 ? 2 : pane] + (original ? "-original" : "");
                    var tag = tags.Single(p => p.Value.Contains("data-side=\"" + side + "\"", StringComparison.Ordinal)).Value;
                    var encoded = Regex.Match(tag, "src=\"data:image/png;base64,([^\"]+)\"", RegexOptions.CultureInvariant).Groups[1].Value;
                    var decoded = ImageReportScenarios.DecodePng(Convert.FromBase64String(encoded)); var input = inputs[pane];
                    var w = input.GetProperty("width").GetInt32(); var h = input.GetProperty("height").GetInt32(); var raw = Convert.FromHexString(input.GetProperty("bgraHex").GetString()!);
                    var canvasW = original ? w : width; var canvasH = original ? h : height; var padded = new byte[canvasW * canvasH * 4];
                    var x = original ? 0 : positions[pane].X; var y = original ? 0 : positions[pane].Y;
                    for (var row = 0; row < h; row++) raw.AsSpan(row * w * 4, w * 4).CopyTo(padded.AsSpan(((row + y) * canvasW + x) * 4));
                    check("image-offset-project-html-" + label + "-" + side, decoded.Width == canvasW && decoded.Height == canvasH && decoded.Bgra.AsSpan().SequenceEqual(padded), "all BGRA including transparent padding");
                }
        }
        async Task Reject(string label, string project, bool reportsOnly)
        {
            foreach (var command in reportsOnly ? new[] { "--report-project", "--package-project" } : new[] { "--project-copy", "--report-project", "--package-project" })
            {
                var extension = command == "--project-copy" ? ".json" : command == "--report-project" ? ".html" : ".zip";
                var destination = Path.Combine(proof, "reject-" + label + "-" + command[2..] + extension); File.WriteAllText(destination, "protected existing output"); var hash = Hash(destination); var attrs = File.GetAttributes(destination);
                var result = await run("image-offset-project-reject-" + label + "-" + command[2..], 2, false, [command, project, destination, .. (command == "--package-project" ? new[] { "--report" } : Array.Empty<string>())]);
                check("image-offset-project-reject-preserved-" + label + "-" + command[2..], string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr) && Hash(destination) == hash && File.GetAttributes(destination) == attrs, result.Stderr);
            }
        }
        async Task RejectDirect(string label, string[] options)
        {
            var result = await run("image-offset-project-direct-reject-" + label, 2, false, ["--image", .. two, .. options]);
            check("image-offset-project-direct-reject-output-" + label, string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            var destination = Path.Combine(proof, "reject-report-option-" + label + ".html"); File.WriteAllText(destination, "protected existing HTML"); var hash = Hash(destination); var attrs = File.GetAttributes(destination);
            var report = await run("image-offset-project-report-reject-" + label, 2, false, ["--report-project", omittedCopy, destination, .. options]);
            check("image-offset-project-report-reject-preserved-" + label, string.IsNullOrWhiteSpace(report.Stdout) && !string.IsNullOrWhiteSpace(report.Stderr) && Hash(destination) == hash && File.GetAttributes(destination) == attrs, report.Stderr);
        }
    }
    private static JsonElement Entry(JsonElement root) => root.TryGetProperty("entries", out var entries) ? entries[0] : root;
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
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
