using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ImageHighlightScenarios
{
    // 原本採取担当の続き。期待値は無改変C++原本の72goldenだけを使う。
    // 先行失敗契約: artifacts/verification/image-regions/next-integration-contract.md
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        const string goldenSha = "853A08102656CD1F726E39647D98AF47CF4C8918C7F3EB051D078FEA87BC057A";
        var source = FindFixtures("ImageHighlight", "winimerge-image-highlight-golden.json");
        var golden = await File.ReadAllBytesAsync(Path.Combine(source, "winimerge-image-highlight-golden.json"));
        var hash = Convert.ToHexString(SHA256.HashData(golden));
        check("image-highlight-golden-sha256", hash == goldenSha, hash);
        if (hash != goldenSha) throw new InvalidDataException("画像強調goldenのbytesが変わっています。");
        await File.WriteAllBytesAsync(Path.Combine(output, "image-highlight-golden.json"), golden);
        using var document = JsonDocument.Parse(golden);
        var root = document.RootElement;
        check("image-highlight-reference-revision", root.GetProperty("sourceRevision").GetString() == "da639cdfaeca87aaad0eaceec509afa11ad61421"
            && root.GetProperty("sourceSha256").GetString() == "7071EDFA31CCC138685CA7B9FD3D41EE5E84B942FB3D8028C7E3A31C76F93F28", root.GetProperty("sourceRevision").GetString()!);
        var folder = Path.Combine(fixtures, "image-highlight"); Directory.CreateDirectory(folder);
        var observations = new List<object>();
        string[]? firstInput = null; string[]? firstCommand = null;
        JsonElement firstCase = default;
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var input = new List<string>(); var before = new List<string>();
            var pane = 0;
            foreach (var image in item.GetProperty("images").EnumerateArray())
            {
                var path = Path.Combine(folder, name + "-" + pane++ + ".png");
                var pixels = Convert.FromBase64String(image.GetProperty("bgraBase64").GetString()!);
                WritePng(path, image.GetProperty("width").GetInt32(), image.GetProperty("height").GetInt32(), pixels);
                input.Add(path); before.Add(Hash(path));
            }
            var expected = item.GetProperty("expected");
            var selected = item.GetProperty("selectedDiffIndex");
            var selectedNumber = selected.ValueKind == JsonValueKind.String ? expected.GetProperty("currentDiffIndex").GetInt32() + 1 : selected.GetInt32() + 1;
            var command = new[] { "--image-regions" }.Concat(input).Concat(new[] {
                "--block-size", item.GetProperty("blockSize").GetInt32().ToString(CultureInfo.InvariantCulture),
                "--threshold", item.GetProperty("threshold").GetDouble().ToString("R", CultureInfo.InvariantCulture),
                "--highlight-alpha", item.GetProperty("highlightAlpha").GetDouble().ToString("R", CultureInfo.InvariantCulture),
                "--selected-region", selectedNumber.ToString(CultureInfo.InvariantCulture) }).ToArray();
            var result = await run("image-highlight-" + name, expected.GetProperty("differenceCount").GetInt32() == 0 ? 0 : 1, true, command);
            var actual = Verify(name, item, result.Stdout);
            if (item.GetProperty("highlightAlpha").GetDouble() == .7 && selected.ValueKind == JsonValueKind.Number && selected.GetInt32() == -1)
                await ImageHighlightReportScenarios.RunCaseAsync(output, folder, item, input.ToArray(), run, check);
            check("image-highlight-" + name + "-inputs-unchanged", input.Select(Hash).SequenceEqual(before), string.Join(',', before));
            observations.Add(new { name, input, fileSha256 = before, command, expected = expected.Clone(), actual });
            if (firstInput is null) { firstInput = input.ToArray(); firstCommand = command; firstCase = item.Clone(); }
        }
        check("image-highlight-reference-case-count", observations.Count == 72, observations.Count.ToString(CultureInfo.InvariantCulture));
        await File.WriteAllTextAsync(Path.Combine(output, "image-highlight-observations.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));

        var preservedInputs = firstInput ?? throw new InvalidDataException("画像強調goldenに入力がありません。");
        var readOnlyCommand = firstCommand ?? throw new InvalidDataException("画像強調goldenにコマンドがありません。");
        var a = preservedInputs[0]; var b = preservedInputs[1]; var inputHashes = preservedInputs.Select(Hash).ToArray();
        foreach (var single in new[] { (Name: "alpha-only", Case: "two-separated-alpha0.7-selected-1", Option: "--highlight-alpha", Value: ".7"),
            (Name: "selected-only", Case: "two-separated-alpha0.7-selected0", Option: "--selected-region", Value: "1") })
        {
            var oracleCase = root.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("name").GetString() == single.Case);
            var result = await run("image-highlight-" + single.Name, 1, true, ["--image-regions", a, b, single.Option, single.Value]);
            Verify(single.Name, oracleCase, result.Stdout);
            check("image-highlight-" + single.Name + "-inputs-unchanged", preservedInputs.Select(Hash).SequenceEqual(inputHashes), string.Join(',', inputHashes));
        }
        var defaultResult = await run("image-highlight-default-no-render", 1, true, ["--image-regions", a, b]);
        using (var defaultJson = JsonDocument.Parse(defaultResult.Stdout))
        {
            check("image-highlight-default-no-render-field", !defaultJson.RootElement.TryGetProperty("renderedFrames", out _), defaultResult.Stdout);
            foreach (var property in firstCase.GetProperty("expected").EnumerateObject())
            {
                if (property.Name is "processed" or "currentDiffIndex") continue;
                check("image-highlight-default-" + property.Name, defaultJson.RootElement.TryGetProperty(property.Name, out var actual)
                    && JsonElement.DeepEquals(actual, property.Value), property.Value.GetRawText());
            }
        }
        check("image-highlight-default-inputs-unchanged", preservedInputs.Select(Hash).SequenceEqual(inputHashes), string.Join(',', inputHashes));
        async Task Reject(string name, params string[] options)
        {
            var command = new[] { "--image-regions", a, b }.Concat(options).ToArray();
            var result = await run("image-highlight-reject-" + name, 2, false, command);
            check("image-highlight-reject-" + name + "-no-success-json", string.IsNullOrWhiteSpace(result.Stdout), result.Stdout);
            check("image-highlight-reject-" + name + "-diagnostic", !string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            check("image-highlight-reject-" + name + "-inputs-unchanged", preservedInputs.Select(Hash).SequenceEqual(inputHashes), string.Join(',', inputHashes));
        }
        foreach (var value in new[] { "-1", "1.01", "NaN", "Infinity", "-Infinity", "1e309", "0,7", "abc" })
            await Reject("alpha-" + value.Replace(',', '_'), "--highlight-alpha", value);
        foreach (var value in new[] { "-1", "1.5", "NaN", "2147483648", "999999" })
            await Reject("selected-" + value, "--selected-region", value);
        await Reject("selected-out-of-range", "--block-size", "8", "--selected-region", "3");
        await Reject("duplicate-alpha", "--highlight-alpha", ".3", "--highlight-alpha", ".7");
        await Reject("duplicate-selected", "--selected-region", "0", "--selected-region", "1");
        await Reject("missing-alpha", "--highlight-alpha");
        await Reject("missing-selected", "--selected-region");
        await Reject("invalid-frame", "--highlight-alpha", ".7", "--left-frame", "2", "--right-frame", "1");
        await Reject("partial-frame-selection", "--selected-region", "0", "--left-frame", "1");
        await Reject("two-with-middle", "--highlight-alpha", ".7", "--left-frame", "1", "--middle-frame", "1", "--right-frame", "1");
        var noRegions = await run("image-highlight-reject-selected-with-no-regions", 2, false,
            ["--image-regions", a, a, "--selected-region", "1"]);
        check("image-highlight-reject-selected-with-no-regions-no-json", string.IsNullOrWhiteSpace(noRegions.Stdout)
            && !string.IsNullOrWhiteSpace(noRegions.Stderr), noRegions.Stderr);
        check("image-highlight-reject-selected-with-no-regions-input-unchanged", Hash(a) == inputHashes[0], Hash(a));
        // 読取り専用入力は成功し、bytesと属性を保存する。出力を書き込む経路ではない。
        var attributes = preservedInputs.Select(File.GetAttributes).ToArray();
        try
        {
            for (var index = 0; index < preservedInputs.Length; index++) File.SetAttributes(preservedInputs[index], attributes[index] | FileAttributes.ReadOnly);
            var readOnly = await run("image-highlight-readonly-inputs", firstCase.GetProperty("expected").GetProperty("differenceCount").GetInt32() == 0 ? 0 : 1, true, readOnlyCommand);
            Verify("readonly-inputs", firstCase, readOnly.Stdout);
            await Reject("readonly-invalid-alpha", "--highlight-alpha", "1.1");
            check("image-highlight-readonly-inputs-bytes", preservedInputs.Select(Hash).SequenceEqual(inputHashes), string.Join(',', inputHashes));
            check("image-highlight-readonly-inputs-attributes", preservedInputs.All(path => File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)), string.Join(',', preservedInputs));
        }
        finally { for (var index = 0; index < preservedInputs.Length; index++) File.SetAttributes(preservedInputs[index], attributes[index]); }

        JsonElement Verify(string name, JsonElement item, string stdout)
        {
            using var actualDocument = JsonDocument.Parse(stdout);
            var actual = actualDocument.RootElement; var expected = item.GetProperty("expected");
            foreach (var property in expected.EnumerateObject())
            {
                if (property.Name is "processed" or "currentDiffIndex") continue;
                check("image-highlight-" + name + "-" + property.Name, actual.TryGetProperty(property.Name, out var value)
                    && JsonElement.DeepEquals(value, property.Value), "C++=" + property.Value.GetRawText() + "; app=" + (value.ValueKind == JsonValueKind.Undefined ? "missing" : value.GetRawText()));
            }
            var block = item.GetProperty("blockSize").GetInt32();
            var processed = expected.GetProperty("processed"); var first = processed[0];
            var w = first.GetProperty("width").GetInt32(); var h = first.GetProperty("height").GetInt32();
            check("image-highlight-" + name + "-canvas", actual.GetProperty("width").GetInt32() == w && actual.GetProperty("height").GetInt32() == h
                && actual.GetProperty("columns").GetInt32() == (w + block - 1) / block && actual.GetProperty("rows").GetInt32() == (h + block - 1) / block
                && actual.GetProperty("blockSize").GetInt32() == block && actual.GetProperty("threshold").GetDouble() == item.GetProperty("threshold").GetDouble(), $"{w}x{h}; block={block}");
            check("image-highlight-" + name + "-frame-numbers", actual.GetProperty("frameNumbers").EnumerateArray().Select(value => value.GetInt32())
                .SequenceEqual(Enumerable.Repeat(1, item.GetProperty("images").GetArrayLength())), actual.GetProperty("frameNumbers").GetRawText());
            var found = actual.TryGetProperty("renderedFrames", out var rendered) && rendered.ValueKind == JsonValueKind.Array;
            check("image-highlight-" + name + "-rendered-count", found && rendered.GetArrayLength() == processed.GetArrayLength(), found ? rendered.GetRawText() : "missing");
            for (var pane = 0; pane < processed.GetArrayLength(); pane++)
            {
                var oracle = processed[pane]; var raw = Convert.FromBase64String(oracle.GetProperty("bgraBase64").GetString()!);
                var expectedHash = oracle.GetProperty("sha256").GetString()!;
                check($"image-highlight-{name}-pane{pane}-oracle-bytes-sha", Convert.ToHexString(SHA256.HashData(raw)) == expectedHash
                    && raw.Length == oracle.GetProperty("width").GetInt32() * oracle.GetProperty("height").GetInt32() * 4, expectedHash);
                if (!found || pane >= rendered.GetArrayLength()) continue;
                var frame = rendered[pane];
                check($"image-highlight-{name}-pane{pane}-rendered", frame.GetProperty("frame").GetInt32() == 1
                    && frame.GetProperty("width").GetInt32() == oracle.GetProperty("width").GetInt32()
                    && frame.GetProperty("height").GetInt32() == oracle.GetProperty("height").GetInt32()
                    && string.Equals(frame.GetProperty("pixelSha256").GetString(), expectedHash, StringComparison.OrdinalIgnoreCase), "C++=" + expectedHash + "; app=" + frame.GetRawText());
            }
            return actual.Clone();
        }
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
    internal static void WritePng(string path, int width, int height, byte[] bgra)
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
