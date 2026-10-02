using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ImageRectangleScenarios
{
    // 失敗先行契約と原本shimの範囲はImageRectangles/README.mdを参照。
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "DiffBeacon.slnx"))) repository = repository.Parent;
        if (repository is null) throw new DirectoryNotFoundException("ImageRectangles fixtures");
        var source = Path.Combine(repository.FullName, "tests", "Fixtures", "ImageRectangles");
        var packed = await File.ReadAllBytesAsync(Path.Combine(source, "winimerge-rectangles-golden.json.gz"));
        const string packedSha = "1EC081CE7158F9D17C4A59E1218AA6D8EFF8D33210EF5A48B6D5463748D8D6AA";
        check("image-rectangles-gzip-sha", Hash(packed) == packedSha, Hash(packed));
        if (Hash(packed) != packedSha) throw new InvalidDataException("Rectangle golden changed");
        using var compressed = new MemoryStream(packed); using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var bytes = new MemoryStream(); await gzip.CopyToAsync(bytes);
        const string originalSha = "E32876DC786381285E6FDA5363073A1C09881934AB1B9B8C9645FA5A132A14E2";
        check("image-rectangles-observations-sha", Hash(bytes.ToArray()) == originalSha, Hash(bytes.ToArray()));
        if (Hash(bytes.ToArray()) != originalSha) throw new InvalidDataException("Rectangle observations changed");
        using var golden = JsonDocument.Parse(bytes.ToArray());
        var casesBytes = await File.ReadAllBytesAsync(Path.Combine(source, "cases.json"));
        const string casesSha = "371961F9AE6E88689C76DDFB76CB995F3E5239E1E1450DDE58FA3F7F588CFCE1";
        check("image-rectangles-cases-sha", Hash(casesBytes) == casesSha, Hash(casesBytes));
        if (Hash(casesBytes) != casesSha) throw new InvalidDataException("Rectangle inputs changed");
        using var cases = JsonDocument.Parse(casesBytes);
        var folder = Path.Combine(fixtures, "image-rectangles"); Directory.CreateDirectory(folder);
        var proof = Path.Combine(output, "image-rectangles"); Directory.CreateDirectory(proof);
        var originalCases = 0; var exclusions = new List<object>();
        foreach (var item in cases.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("id").GetString()!; var target = item.GetProperty("target"); var input = item.GetProperty("source");
            var paste = item.GetProperty("op").GetString() == "paste"; var ro = item.GetProperty("readOnly").GetBoolean();
            var pane = item.GetProperty("pane").GetInt32();
            var expected = golden.RootElement.EnumerateArray().Single(value => value.GetProperty("id").GetString() == name);
            if (pane is < 0 or > 1 || target.GetProperty("width").GetInt32() == 0 || target.GetProperty("height").GetInt32() == 0
                || input.GetProperty("width").GetInt32() == 0 || input.GetProperty("height").GetInt32() == 0 || paste && ro)
            {
                exclusions.Add(new { name, reason = paste && ro ? "safe read-only divergence" : pane is < 0 or > 1 ? "strict CLI pane validation" : "current nonempty decode boundary" });
                continue;
            }
            var paths = new[] { Path.Combine(folder, name + "-target.png"), Path.Combine(folder, name + "-source.png") };
            var before = PixelBytes(target); var sourcePixels = PixelBytes(input);
            WritePng(paths[0], target.GetProperty("width").GetInt32(), target.GetProperty("height").GetInt32(), before);
            WritePng(paths[1], input.GetProperty("width").GetInt32(), input.GetProperty("height").GetInt32(), sourcePixels);
            var export = Path.Combine(proof, name + ".png"); var rect = item.GetProperty("rect");
            object edit = paste ? new { kind = "paste-image", source = 1, destination = 0, x = rect[0].GetInt32(), y = rect[1].GetInt32() }
                : new { kind = "delete-rectangle", destination = 0, rectangle = rect.EnumerateArray().Select(v => v.GetInt32()).ToArray() };
            var actions = new List<object> { edit, new { kind = "undo" }, new { kind = "redo" } };
            if (!ro) actions.Add(new { kind = "export", dst = 0, path = export });
            var script = await Script(name, actions, [ro, false]);
            var hashes = paths.Append(script).Select(FileHash).ToArray();
            var result = await run("image-rectangles-original-" + name, 0, true, ["--image-copy", .. paths, "--script", script]);
            if (result.ExitCode != 0) continue;
            using var observed = JsonDocument.Parse(result.Stdout); var states = observed.RootElement.GetProperty("states");
            var wanted = Convert.FromHexString(expected.GetProperty("bgraHex").GetString()!);
            check(name + "-state-count", states.GetArrayLength() == actions.Count + 1, "initial, edit, Undo, Redo, optional export");
            for (var index = 0; index < states.GetArrayLength(); index++)
            {
                var state = index == 0 ? states[index] : states[index].GetProperty("state");
                var pixels = index is 0 or 2 ? before : wanted;
                CheckFrame(name + "-state" + index + "-target", state.GetProperty("frames")[0], pixels,
                    expected.GetProperty("width").GetInt32(), expected.GetProperty("height").GetInt32());
                CheckFrame(name + "-state" + index + "-source", state.GetProperty("frames")[1], sourcePixels,
                    input.GetProperty("width").GetInt32(), input.GetProperty("height").GetInt32());
                CheckPairRecomputed(name + "-state" + index, state, pixels, target.GetProperty("width").GetInt32(),
                    target.GetProperty("height").GetInt32(), sourcePixels, input.GetProperty("width").GetInt32(), input.GetProperty("height").GetInt32());
            }
            var pushes = expected.GetProperty("historyPushes").GetInt32();
            var editState = states[1].GetProperty("state"); var history = editState.GetProperty("history");
            check(name + "-original-history", history.GetProperty("count").GetInt32() == pushes
                && history.GetProperty("index").GetInt32() == pushes - 1 && history.GetProperty("panes")[0].GetProperty("modcount").GetInt32() == pushes
                && history.GetProperty("panes")[0].GetProperty("modified").GetBoolean() == (pushes != 0), "original historyPushes; no-op edits are dirty");
            check(name + "-undo-redo", states[2].GetProperty("actionResult").GetInt32() == pushes
                && states[3].GetProperty("actionResult").GetInt32() == pushes
                && !states[2].GetProperty("state").GetProperty("history").GetProperty("panes")[0].GetProperty("modified").GetBoolean(), "shared Undo/Redo and dirty restore");
            if (!ro)
            {
                var decoded = ImageReportScenarios.DecodePng(await File.ReadAllBytesAsync(export));
                check(name + "-independent-png", decoded.Width == expected.GetProperty("width").GetInt32()
                    && decoded.Height == expected.GetProperty("height").GetInt32() && decoded.Bgra.SequenceEqual(wanted), "independent full PNG decode");
                var saved = states[4].GetProperty("state").GetProperty("history").GetProperty("panes")[0];
                check(name + "-savepoint", !saved.GetProperty("modified").GetBoolean() && saved.GetProperty("savepoint").GetInt32() == 1, "only exported pane saved");
            }
            check(name + "-input-script-retention", paths.Append(script).Select(FileHash).SequenceEqual(hashes), "all input/script SHA");
            originalCases++;
        }
        check("image-rectangles-original-scope", originalCases == 20 && exclusions.Count == 12, $"matched={originalCases}; declared excluded={exclusions.Count}");

        // 原本を偽装せず、安全差異と統合契約をBCL側の独立期待値で検証する。
        var a = Path.Combine(folder, "safe-target.png"); var b = Path.Combine(folder, "safe-source.png");
        var raw = Enumerable.Range(0, 4 * 3 * 4).Select(i => (byte)(17 + i * 37)).ToArray();
        var src = Enumerable.Range(0, 2 * 2 * 4).Select(i => (byte)(91 + i * 37)).ToArray();
        WritePng(a, 4, 3, raw); WritePng(b, 2, 2, src);
        foreach (var (x, y) in new[] { (4, 0), (0, 3), (int.MaxValue, int.MaxValue), (int.MinValue, int.MinValue) })
        {
            var name = "safe-outside-" + x + "-" + y;
            var script = await Script(name, [new { kind = "paste-image", source = 1, destination = 0, x, y }]);
            var result = await run(name, 0, true, ["--image-copy", a, b, "--script", script]);
            using var observed = JsonDocument.Parse(result.Stdout); var state = observed.RootElement.GetProperty("states")[1].GetProperty("state");
            CheckFrame(name, state.GetProperty("frames")[0], raw, 4, 3);
            check(name + "-history", state.GetProperty("history").GetProperty("count").GetInt32() == 1, "safe no-op with one history; original UB excluded");
        }
        var roScript = await Script("safe-readonly", [new { kind = "paste-image", source = 1, destination = 0, x = 0, y = 0 },
            new { kind = "delete-rectangle", destination = 0, rectangle = new[] { 0, 0, 4, 3 } }], [true, false]);
        var roResult = await run("safe-readonly", 0, true, ["--image-copy", a, b, "--script", roScript]);
        using (var observed = JsonDocument.Parse(roResult.Stdout))
        foreach (var state in observed.RootElement.GetProperty("states").EnumerateArray().Skip(1))
        {
            check("safe-readonly-result", state.GetProperty("actionResult").GetInt32() == 0
                && state.GetProperty("state").GetProperty("history").GetProperty("count").GetInt32() == 0, "safe paste/delete rejected before allocation");
            CheckFrame("safe-readonly-unchanged", state.GetProperty("state").GetProperty("frames")[0], raw, 4, 3);
        }
        var identicalScript = await Script("same-pane", [new { kind = "paste-image", source = 0, destination = 0, x = 1, y = 0 }]);
        var identicalResult = await run("same-pane", 0, true, ["--image-copy", a, b, "--script", identicalScript]);
        var aliasedExpected = (byte[])raw.Clone();
        for (var y = 0; y < 3; y++) raw.AsSpan(y * 16, 12).CopyTo(aliasedExpected.AsSpan(y * 16 + 4));
        using (var observed = JsonDocument.Parse(identicalResult.Stdout))
            CheckFrame("same-pane-owned-snapshot", observed.RootElement.GetProperty("states")[1].GetProperty("state").GetProperty("frames")[0], aliasedExpected, 4, 3);

        // 表示方向の独立座標式とraw PNG逆写像を全16組合せで照合する。
        foreach (var rotation in new[] { 0, 90, 180, 270 })
        foreach (var flipx in new[] { false, true })
        foreach (var flipy in new[] { false, true })
        {
            var name = $"orientation-{rotation}-{flipx}-{flipy}";
            var view = Transform(raw, 4, 3, rotation, flipx, flipy); var viewSource = Transform(src, 2, 2, rotation, !flipx, flipy);
            var expectedView = (byte[])view.Pixels.Clone();
            for (var row = 0; row < 2; row++) viewSource.Pixels.AsSpan(row * 8, 8).CopyTo(expectedView.AsSpan(row * view.Width * 4));
            // 貼付け後の右下1画素の削除は半開endpointを含む。
            expectedView.AsSpan(expectedView.Length - 4, 4).Clear();
            var export = Path.Combine(proof, name + ".png");
            var script = await Script(name, [new { kind = "rotate", dst = 0, index = rotation }, new { kind = "flipx", dst = 0, index = flipx ? 1 : 0 },
                new { kind = "flipy", dst = 0, index = flipy ? 1 : 0 }, new { kind = "rotate", dst = 1, index = rotation },
                new { kind = "flipx", dst = 1, index = !flipx ? 1 : 0 }, new { kind = "flipy", dst = 1, index = flipy ? 1 : 0 },
                new { kind = "offset", src = 5, dst = 0, index = 7 }, new { kind = "mode", index = 1 },
                new { kind = "paste-image", source = 1, destination = 0, x = 0, y = 0 },
                new { kind = "delete-rectangle", destination = 0, rectangle = new[] { view.Width - 1, view.Height - 1, view.Width, view.Height } },
                new { kind = "undo" }, new { kind = "redo" }, new { kind = "export", dst = 0, path = export }]);
            var result = await run(name, 0, true, ["--image-copy", a, b, "--script", script]);
            using var observed = JsonDocument.Parse(result.Stdout); var final = observed.RootElement.GetProperty("states")[13].GetProperty("state");
            CheckFrame(name + "-view", final.GetProperty("frames")[0], expectedView, view.Width, view.Height);
            var decoded = ImageReportScenarios.DecodePng(await File.ReadAllBytesAsync(export));
            var expectedRaw = Inverse(expectedView, 4, 3, rotation, flipx, flipy);
            check(name + "-raw-png", decoded.Width == 4 && decoded.Height == 3 && decoded.Bgra.SequenceEqual(expectedRaw), "BCL independent inverse coordinate formula; full raw BGRA");
            check(name + "-offset-retained", final.GetProperty("offsets")[0].GetProperty("x").GetInt32() == 5
                && final.GetProperty("offsets")[0].GetProperty("y").GetInt32() == 7, "offset retained across oriented edit and shared Undo/Redo");
        }
        // 三者全paneの状態とbranch破棄・各pane保存点を同時に確認する。
        var chainScript = await Script("chain", [new { kind = "paste-image", source = 1, destination = 0, x = 0, y = 0 },
            new { kind = "delete-rectangle", destination = 1, rectangle = new[] { 0, 0, 2, 2 } },
            new { kind = "paste-image", source = 0, destination = 2, x = 0, y = 0 }, new { kind = "save", dst = 0 },
            new { kind = "undo" }, new { kind = "delete-rectangle", destination = 2, rectangle = new[] { 0, 0, 0, 0 } }, new { kind = "redo" }], [false, false, false]);
        var chainResult = await run("chain", 0, true, ["--image-copy", a, b, a, "--script", chainScript]);
        using (var observed = JsonDocument.Parse(chainResult.Stdout))
        {
            var states = observed.RootElement.GetProperty("states"); var afterPaste = (byte[])raw.Clone();
            for (var y = 0; y < 2; y++) src.AsSpan(y * 8, 8).CopyTo(afterPaste.AsSpan(y * 16));
            CheckFrame("chain-owned-source-at-operation", states[3].GetProperty("state").GetProperty("frames")[2], afterPaste, 4, 3);
            var final = states[7].GetProperty("state"); var history = final.GetProperty("history");
            check("chain-branch", states[7].GetProperty("actionResult").GetInt32() == 0 && history.GetProperty("count").GetInt32() == 3
                && !history.GetProperty("redoable").GetBoolean(), "Redo tail discarded by new no-op rectangle history");
            check("chain-savepoint", !history.GetProperty("panes")[0].GetProperty("modified").GetBoolean()
                && history.GetProperty("panes")[1].GetProperty("modified").GetBoolean() && history.GetProperty("panes")[2].GetProperty("modified").GetBoolean(), "per-pane saved point in shared history");
        }

        var retainedOutput = Path.Combine(proof, "protected-output.png"); await File.WriteAllTextAsync(retainedOutput, "retained output");
        var exportJson = JsonSerializer.Serialize(new { kind = "export", dst = 0, path = retainedOutput });
        var invalid = new[] {
            "{\"kind\":\"delete-rectangle\",\"destination\":0,\"rectangle\":[0,0,5,3]}",
            "{\"kind\":\"delete-rectangle\",\"destination\":0,\"rectangle\":[2,0,1,3]}",
            "{\"kind\":\"delete-rectangle\",\"destination\":0,\"rectangle\":[0,0,4]}",
            "{\"kind\":\"delete-rectangle\",\"destination\":0,\"rectangle\":[0,0,4,3.5]}",
            "{\"kind\":\"delete-rectangle\",\"destination\":0,\"destination\":1,\"rectangle\":[0,0,4,3]}",
            "{\"kind\":\"paste-image\",\"source\":1,\"destination\":0,\"x\":0,\"y\":0,\"src\":1}",
            "{\"kind\":\"paste-image\",\"source\":-1,\"destination\":0,\"x\":0,\"y\":0}",
            "{\"kind\":\"paste-image\",\"source\":1,\"destination\":2,\"x\":0,\"y\":0}",
            "{\"kind\":\"paste-image\",\"source\":1,\"destination\":0,\"x\":2147483648,\"y\":0}",
            "{\"kind\":\"paste-image\",\"source\":1,\"destination\":0,\"x\":0}",
            "{\"kind\":\"paste-image\",\"source\":1,\"destination\":0,\"x\":0,\"x\":1,\"y\":0}"
        };
        for (var i = 0; i < invalid.Length; i++)
            await Reject("invalid-schema-" + i, "{\"actions\":[" + exportJson + "," + invalid[i] + "]}", [a, b]);
        var budget = Path.Combine(folder, "history-budget.png"); WritePng(budget, 2000, 2000, new byte[2000 * 2000 * 4]);
        var historyActions = new List<object> { new { kind = "export", dst = 0, path = retainedOutput } };
        historyActions.AddRange(Enumerable.Range(0, 9).Select(_ => (object)new { kind = "delete-rectangle", destination = 0, rectangle = new[] { 0, 0, 0, 0 } }));
        await Reject("history-bytes", JsonSerializer.Serialize(new { blockSize = 256, actions = historyActions }), [budget, budget], "256 MiB");
        var work = Path.Combine(folder, "work-budget.png"); WritePng(work, 1500, 1500, new byte[1500 * 1500 * 4]);
        var workActions = new List<object> { new { kind = "export", dst = 0, path = retainedOutput } };
        workActions.AddRange(Enumerable.Range(0, 20).SelectMany(_ => new object[] {
            new { kind = "paste-image", source = 1, destination = 0, x = 0, y = 0 }, new { kind = "undo" } }));
        await Reject("cumulative-work", JsonSerializer.Serialize(new { blockSize = 256, actions = workActions }), [work, work], "256M");
        await File.WriteAllTextAsync(Path.Combine(proof, "proof.json"), JsonSerializer.Serialize(new { originalCases, exclusions,
            sourceSha256 = originalSha, gzipSha256 = packedSha, casesSha256 = casesSha, adapter = "identity orientation / BGRA rows / historyPushes / compareCalls",
            additionalChecks = "orientation inverse; shared history; operation snapshot; input retention; independent PNG; strict schema; safe outside/read-only; history bytes/work rejection" }, new JsonSerializerOptions { WriteIndented = true }));

        async Task<string> Script(string name, IEnumerable<object> actions, bool[]? readOnly = null)
        {
            var path = Path.Combine(folder, name + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { blockSize = 1, actions, readOnly = readOnly ?? new[] { false, false } }));
            return path;
        }
        async Task Reject(string name, string scriptJson, string[] paths, string? reason = null)
        {
            var path = Path.Combine(folder, name + ".json"); await File.WriteAllTextAsync(path, scriptJson);
            var protectedPaths = paths.Append(path).Append(retainedOutput).ToArray(); var hashes = protectedPaths.Select(FileHash).ToArray();
            var result = await run("image-rectangles-" + name, 2, false, ["--image-copy", .. paths, "--script", path, "--hashes-only"]);
            check(name + "-atomic", string.IsNullOrWhiteSpace(result.Stdout) && (reason is null || result.Stderr.Contains(reason, StringComparison.Ordinal)), result.Stderr);
            check(name + "-retained", protectedPaths.Select(FileHash).SequenceEqual(hashes), "input/script/existing output all SHA unchanged");
        }
        void CheckFrame(string name, JsonElement frame, byte[] expected, int width, int height)
            => check(name, frame.GetProperty("width").GetInt32() == width && frame.GetProperty("height").GetInt32() == height
                && Convert.FromBase64String(frame.GetProperty("bgraBase64").GetString()!).SequenceEqual(expected)
                && string.Equals(frame.GetProperty("sha256").GetString(), Hash(expected), StringComparison.OrdinalIgnoreCase), "dimensions, all BGRA and independent SHA");
        void CheckPairRecomputed(string name, JsonElement state, byte[] left, int lw, int lh, byte[] right, int rw, int rh)
        {
            // 全画素の不一致集合から8近傍の成分を独立に構成し、古い比較stateの保持を検出する。
            var width = Math.Max(lw, rw); var height = Math.Max(lh, rh); var ids = new int[width * height];
            var different = new bool[ids.Length];
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
                different[y * width + x] = x >= lw || x >= rw || y >= lh || y >= rh
                    || !left.AsSpan((y * lw + x) * 4, 4).SequenceEqual(right.AsSpan((y * rw + x) * 4, 4));
            var boxes = new List<(int Left, int Top, int Right, int Bottom)>();
            for (var start = 0; start < ids.Length; start++)
            {
                if (!different[start] || ids[start] != 0) continue;
                var id = boxes.Count + 1; var queue = new Queue<int>(); queue.Enqueue(start); ids[start] = id;
                var l = width; var t = height; var r = 0; var b = 0;
                while (queue.TryDequeue(out var at))
                {
                    var x = at % width; var y = at / width; l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x + 1); b = Math.Max(b, y + 1);
                    for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx; var ny = y + dy;
                        if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                        var neighbor = ny * width + nx;
                        if (!different[neighbor] || ids[neighbor] != 0) continue;
                        ids[neighbor] = id; queue.Enqueue(neighbor);
                    }
                }
                boxes.Add((l, t, r, b));
            }
            var grid = state.GetProperty("regionIds").EnumerateArray().SelectMany(row => row.EnumerateArray().Select(v => v.GetInt32())).ToArray();
            check(name + "-recompare-grid", grid.SequenceEqual(ids) && state.GetProperty("differenceCount").GetInt32() == boxes.Count, "independent pixel mask and connected components");
            var regions = state.GetProperty("regions");
            var match = regions.GetArrayLength() == boxes.Count;
            for (var i = 0; i < Math.Min(regions.GetArrayLength(), boxes.Count); i++)
            {
                var box = boxes[i]; var region = regions[i];
                match &= region.GetProperty("id").GetInt32() == i + 1 && region.GetProperty("op").GetInt32() == 4
                    && region.GetProperty("left").GetInt32() == box.Left && region.GetProperty("top").GetInt32() == box.Top
                    && region.GetProperty("right").GetInt32() == box.Right && region.GetProperty("bottom").GetInt32() == box.Bottom;
            }
            check(name + "-recompare-regions", match, "whole connected-component bounds and classification after edit/Undo/Redo");
        }
    }

    private static byte[] PixelBytes(JsonElement image) => image.GetProperty("bgra").EnumerateArray().Select(v => v.GetByte()).ToArray();
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string FileHash(string path) => Hash(File.ReadAllBytes(path));
    private static (int X, int Y) Position(int x, int y, int width, int height, int rotation, bool flipx, bool flipy)
    {
        if (flipx) x = width - 1 - x; if (flipy) y = height - 1 - y;
        return rotation switch { 90 => (y, width - 1 - x), 180 => (width - 1 - x, height - 1 - y),
            270 => (height - 1 - y, x), _ => (x, y) };
    }
    private static (int Width, int Height, byte[] Pixels) Transform(byte[] raw, int width, int height, int rotation, bool flipx, bool flipy)
    {
        var w = rotation is 90 or 270 ? height : width; var h = rotation is 90 or 270 ? width : height; var pixels = new byte[raw.Length];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        { var (tx, ty) = Position(x, y, width, height, rotation, flipx, flipy); raw.AsSpan((y * width + x) * 4, 4).CopyTo(pixels.AsSpan((ty * w + tx) * 4)); }
        return (w, h, pixels);
    }
    private static byte[] Inverse(byte[] view, int width, int height, int rotation, bool flipx, bool flipy)
    {
        var w = rotation is 90 or 270 ? height : width; var raw = new byte[view.Length];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        { var (tx, ty) = Position(x, y, width, height, rotation, flipx, flipy); view.AsSpan((ty * w + tx) * 4, 4).CopyTo(raw.AsSpan((y * width + x) * 4)); }
        return raw;
    }
    private static void WritePng(string path, int width, int height, byte[] bgra)
    {
        using var file = File.Create(path); file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6; Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
        {
            var row = new byte[width * 4 + 1];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                { var i = (y * width + x) * 4; var j = x * 4 + 1; row[j] = bgra[i + 2]; row[j + 1] = bgra[i + 1]; row[j + 2] = bgra[i]; row[j + 3] = bgra[i + 3]; }
                zlib.Write(row);
            }
        }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        void Chunk(string name, byte[] data)
        {
            Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, data.Length); file.Write(number);
            var type = System.Text.Encoding.ASCII.GetBytes(name); file.Write(type); file.Write(data); uint crc = 0xffffffff;
            foreach (var value in type.Concat(data))
            { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) == 0 ? 0 : 0xedb88320); }
            BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); file.Write(number);
        }
    }
}
