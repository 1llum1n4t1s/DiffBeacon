using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ImageRegionScenarios
{
    // 失敗条件を artifacts/verification/image-regions/implementation-contract.md に先行固定。
    // 判定の期待値はC++原本による固定goldenだけを使う。
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var source = FindFixtures("ImageRegions", "winimerge-image-regions-golden.json");
        var golden = await File.ReadAllBytesAsync(Path.Combine(source, "winimerge-image-regions-golden.json"));
        var hash = Convert.ToHexString(SHA256.HashData(golden));
        check("image-regions-golden-sha256", hash == "07BF82D5179A748A568A0B6EB0AAA37B171F04FBF4228920A38698DDD8C05965", hash);
        if (hash != "07BF82D5179A748A568A0B6EB0AAA37B171F04FBF4228920A38698DDD8C05965")
            throw new InvalidDataException("画像領域goldenのbytesが変わっています。");
        await File.WriteAllBytesAsync(Path.Combine(output, "image-regions-golden.json"), golden);
        using var document = JsonDocument.Parse(golden);
        var root = document.RootElement;
        check("image-regions-reference-revision", root.GetProperty("sourceRevision").GetString() == "da639cdfaeca87aaad0eaceec509afa11ad61421"
            && root.GetProperty("sourceSha256").GetString() == "7071EDFA31CCC138685CA7B9FD3D41EE5E84B942FB3D8028C7E3A31C76F93F28", root.GetRawText()[..250]);
        var folder = Path.Combine(fixtures, "image-regions"); Directory.CreateDirectory(folder);
        var observations = new List<object>();
        string? first = null;
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var input = new List<string>(); var before = new List<string>();
            var width = 0; var height = 0;
            var index = 0;
            foreach (var image in item.GetProperty("images").EnumerateArray())
            {
                var w = image.GetProperty("width").GetInt32(); var h = image.GetProperty("height").GetInt32();
                var pixels = Convert.FromBase64String(image.GetProperty("bgraBase64").GetString()!);
                var path = Path.Combine(folder, name + "-" + index++ + ".png");
                WritePng(path, w, h, pixels); input.Add(path); before.Add(Hash(path));
                width = Math.Max(width, w); height = Math.Max(height, h);
                first ??= path;
            }
            var block = item.GetProperty("blockSize").GetInt32(); var threshold = item.GetProperty("threshold").GetDouble();
            var expected = item.GetProperty("expected");
            var command = new[] { "--image-regions" }.Concat(input).Concat(new[] { "--block-size", block.ToString(CultureInfo.InvariantCulture),
                "--threshold", threshold.ToString("R", CultureInfo.InvariantCulture) }).ToArray();
            var result = await run("image-regions-" + name, expected.GetProperty("differenceCount").GetInt32() == 0 ? 0 : 1, true, command);
            using var actualDocument = JsonDocument.Parse(result.Stdout);
            var actual = actualDocument.RootElement;
            foreach (var property in expected.EnumerateObject())
                check("image-regions-" + name + "-" + property.Name, actual.TryGetProperty(property.Name, out var value)
                    && JsonElement.DeepEquals(value, property.Value), "C++=" + property.Value.GetRawText() + "; app=" + (value.ValueKind == JsonValueKind.Undefined ? "missing" : value.GetRawText()));
            check("image-regions-" + name + "-canvas", actual.GetProperty("width").GetInt32() == width && actual.GetProperty("height").GetInt32() == height
                && actual.GetProperty("columns").GetInt32() == (width + block - 1) / block
                && actual.GetProperty("rows").GetInt32() == (height + block - 1) / block
                && actual.GetProperty("blockSize").GetInt32() == block && actual.GetProperty("threshold").GetDouble() == threshold,
                $"{width}x{height}; block={block}; threshold={threshold}");
            check("image-regions-" + name + "-input-unchanged", input.Select(Hash).SequenceEqual(before), string.Join(',', before));
            observations.Add(new { name, sourceRevision = root.GetProperty("sourceRevision").GetString(), input,
                fileSha256 = before, expected = expected.Clone(), actual = actual.Clone() });
        }
        check("image-regions-reference-case-count", observations.Count == 164, observations.Count.ToString(CultureInfo.InvariantCulture));
        await File.WriteAllTextAsync(Path.Combine(output, "image-region-observations.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));

        async Task Reject(string name, string[] command, string? message = null)
        {
            var result = await run("image-regions-reject-" + name, 2, false, command);
            check("image-regions-reject-" + name + "-no-success-json", string.IsNullOrWhiteSpace(result.Stdout), result.Stdout);
            if (message is not null) check("image-regions-reject-" + name + "-reason", result.Stderr.Contains(message, StringComparison.Ordinal), result.Stderr);
        }
        var a = first!;
        var firstHash = Hash(a);
        foreach (var value in new[] { "0", "-1", "257", "1.5", "NaN", "2147483648" })
            await Reject("block-" + value, ["--image-regions", a, a, "--block-size", value]);
        foreach (var value in new[] { "-1", "NaN", "Infinity", "-Infinity", "1,5", "1e309", "abc" })
            await Reject("threshold-" + value.Replace(',', '_'), ["--image-regions", a, a, "--threshold", value]);
        foreach (var value in new[] { "0", "-1", "2", "1.5", "NaN", "2147483648" })
            await Reject("frame-" + value, ["--image-regions", a, a, "--left-frame", value, "--right-frame", "1"]);
        await Reject("one-input", ["--image-regions", a]);
        await Reject("four-inputs", ["--image-regions", a, a, a, a]);
        await Reject("unknown-option", ["--image-regions", a, a, "--unknown", "1"]);
        await Reject("duplicate-block", ["--image-regions", a, a, "--block-size", "1", "--block-size", "2"]);
        await Reject("duplicate-threshold", ["--image-regions", a, a, "--threshold", "0", "--threshold", "1"]);
        await Reject("duplicate-frame", ["--image-regions", a, a, "--left-frame", "1", "--left-frame", "1", "--right-frame", "1"]);
        await Reject("missing-value", ["--image-regions", a, a, "--threshold"]);
        await Reject("missing-file", ["--image-regions", a, Path.Combine(folder, "not-found.png")]);
        await Reject("selection-left-only", ["--image-regions", a, a, "--left-frame", "1"]);
        await Reject("selection-right-only", ["--image-regions", a, a, "--right-frame", "1"]);
        await Reject("selection-two-with-middle", ["--image-regions", a, a, "--left-frame", "1", "--middle-frame", "1", "--right-frame", "1"]);
        await Reject("selection-three-without-middle", ["--image-regions", a, a, a, "--left-frame", "1", "--right-frame", "1"]);
        await Reject("selection-three-middle-only", ["--image-regions", a, a, a, "--middle-frame", "1"]);
        var images = FindFixtures("Images", "expectations.json");
        foreach (var name in new[] { "broken.gif", "truncated.gif", "over-pixel-limit.gif", "over-frame-limit.gif" })
            await Reject(name, ["--image-regions", Path.Combine(images, name), a]);
        await Reject("decode-work", ["--image-regions", Path.Combine(images, "over-work-limit.gif"), a, "--left-frame", "17", "--right-frame", "1"], "復号作業量");
        await Reject("canvas", ["--image-regions", Path.Combine(images, "canvas-wide.gif"), Path.Combine(images, "canvas-tall.gif")], "比較キャンバス");
        var cap = Path.Combine(folder, "over-file-limit.png");
        using (var stream = File.Create(cap)) stream.SetLength(64L * 1024 * 1024 + 1);
        await Reject("file-size", ["--image-regions", cap, a], "64 MiB");
        var large = Path.Combine(folder, "grid-limit.png"); WritePng(large, 513, 512, new byte[513 * 512 * 4]);
        await Reject("grid-limit", ["--image-regions", large, large, "--block-size", "1"], "262,144");
        var boundary = Path.Combine(folder, "grid-boundary.png"); WritePng(boundary, 512, 512, new byte[512 * 512 * 4]);
        var allowed = await run("image-regions-grid-boundary", 0, true, ["--image-regions", boundary, boundary, "--block-size", "1"]);
        using (var data = JsonDocument.Parse(allowed.Stdout))
            check("image-regions-grid-boundary-shape", data.RootElement.GetProperty("regionIds").GetArrayLength() == 512
                && data.RootElement.GetProperty("regionIds")[511].GetArrayLength() == 512, "262,144 blocks; equal");
        var selected = await run("image-regions-cross-frame", 0, true, ["--image-regions", Path.Combine(images, "same-first-left.gif"), Path.Combine(images, "green.png"), "--left-frame", "2", "--right-frame", "1"]);
        using (var data = JsonDocument.Parse(selected.Stdout))
            check("image-regions-cross-frame-metadata", data.RootElement.GetProperty("frameNumbers").GetRawText() == "[2,1]", selected.Stdout);
        var triple = await run("image-regions-three-selected", 0, true, ["--image-regions", a, a, a, "--left-frame", "1", "--middle-frame", "1", "--right-frame", "1", "--block-size", "256", "--threshold", "10000000000000000000000000000000000000000"]);
        using (var data = JsonDocument.Parse(triple.Stdout))
            check("image-regions-large-finite-threshold", data.RootElement.GetProperty("regions").GetArrayLength() == 0, triple.Stdout);
        check("image-regions-original-first-input-unchanged", Hash(a) == firstHash, Hash(a));
    }

    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static string FindFixtures(string name, string marker)
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "Fixtures", name);
            if (File.Exists(Path.Combine(path, marker))) return path;
        }
        throw new FileNotFoundException("Image fixtures were not found: " + name);
    }

    // BCLだけでraw BGRAをPNG RGBAへ包装。差分判定は行わない。
    private static void WritePng(string path, int width, int height, byte[] bgra)
    {
        if (bgra.Length != checked(width * height * 4)) throw new InvalidDataException("Fixture pixel length mismatch.");
        using var file = File.Create(path); file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6; Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
        {
            var row = new byte[checked(width * 4 + 1)];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var input = (y * width + x) * 4; var target = x * 4 + 1;
                    row[target] = bgra[input + 2]; row[target + 1] = bgra[input + 1];
                    row[target + 2] = bgra[input]; row[target + 3] = bgra[input + 3];
                }
                zlib.Write(row);
            }
        }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        void Chunk(string name, byte[] data)
        {
            Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, data.Length); file.Write(number);
            var type = System.Text.Encoding.ASCII.GetBytes(name); file.Write(type); file.Write(data);
            uint crc = 0xffffffff;
            foreach (var value in type.Concat(data))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
            }
            BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); file.Write(number);
        }
    }
}
