using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class ImageTransformProjectScenarios
{
    internal static async Task RunAsync(string output, string folder, string input, JsonElement oracle,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var proof = Path.Combine(output, "image-transform-project"); Directory.CreateDirectory(proof);
        var item = oracle.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("name").GetString() == "copy-103");
        var expected = item.GetProperty("states")[6]; var orientations = expected.GetProperty("orientations");
        var inputHash = Hash(input); var inputAttributes = File.GetAttributes(input);
        Dictionary<string, object?> Settings() => new()
        {
            ["blockSize"] = 1, ["showDifferences"] = false, ["reportAllFrames"] = false,
            ["leftOrientation"] = orientations[0].Clone(), ["middleOrientation"] = orientations[1].Clone(), ["rightOrientation"] = orientations[2].Clone()
        };
        string Project(string name, Dictionary<string, object?> settings, bool middle = true)
        {
            var path = Path.Combine(folder, "transform-project-" + name + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { leftPath = input, basePath = middle ? input : "", rightPath = input, mode = "Image", imageSettings = settings })); return path;
        }
        var project = Project("three", Settings()); var projectHash = Hash(project);
        var options = new List<string> { "--block-size", "1" };
        for (var p = 0; p < 3; p++)
        {
            var orientation = orientations[p]; options.Add("--" + new[] { "left", "middle", "right" }[p] + "-orientation");
            options.Add(orientation.GetProperty("rotation").GetInt32() + "," + (orientation.GetProperty("flipHorizontal").GetBoolean() ? "1" : "0") + "," + (orientation.GetProperty("flipVertical").GetBoolean() ? "1" : "0"));
        }
        var compared = await run("image-transform-direct-compare", expected.GetProperty("differenceCount").GetInt32() == 0 ? 0 : 1, true,
            ["--image", input, input, input, .. options]);
        using (var result = JsonDocument.Parse(compared.Stdout))
        {
            var frame = result.RootElement.GetProperty("frames")[0];
            for (var p = 0; p < 3; p++)
            {
                var side = new[] { "left", "middle", "right" }[p]; var wanted = expected.GetProperty("frames")[p];
                check("image-transform-direct-" + side, frame.GetProperty(side + "Width").GetInt32() == wanted.GetProperty("width").GetInt32()
                    && frame.GetProperty(side + "Height").GetInt32() == wanted.GetProperty("height").GetInt32()
                    && string.Equals(frame.GetProperty(side + "PixelSha256").GetString(), wanted.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "");
            }
        }
        var copy = Path.Combine(proof, "copied.json"); await run("image-transform-project-copy", 0, true, ["--project-copy", project, copy]);
        VerifySettings(copy, false);
        var html = Path.Combine(proof, "report.html"); await run("image-transform-project-report", 0, true, ["--report-project", copy, html]); VerifyHtml(html);
        var zip = Path.Combine(proof, "package.zip"); await run("image-transform-project-package", 0, true, ["--package-project", copy, zip, "--report"]);
        var unpacked = Path.Combine(proof, "unpacked"); ZipFile.ExtractToDirectory(zip, unpacked);
        var restored = Path.Combine(unpacked, "project.json"); VerifySettings(restored, true); VerifyHtml(Path.Combine(unpacked, "report.files", "1.html"));
        var restoredReport = Path.Combine(proof, "restored.html"); await run("image-transform-project-restored-report", 0, true, ["--report-project", restored, restoredReport]); VerifyHtml(restoredReport);
        foreach (var (name, key, value) in new (string, string, object?)[]
        {
            ("orientation-null", "leftOrientation", null), ("rotation45", "leftOrientation", new { rotation = 45 }),
            ("rotation-string", "leftOrientation", new { rotation = "90" }), ("flip-string", "rightOrientation", new { flipHorizontal = "true" }),
            ("block0", "blockSize", 0), ("block257", "blockSize", 257), ("block-fraction", "blockSize", 1.5)
        })
        {
            var settings = Settings(); settings[key] = value; await Reject(name, Project(name, settings));
        }
        var missingMiddle = Settings(); missingMiddle["middleOrientation"] = new { rotation = 90 };
        await Reject("middle-without-input", Project("middle-without-input", missingMiddle, false));
        foreach (var option in new[] { "--left-orientation", "--middle-orientation", "--block-size" })
        {
            var result = await run("image-transform-direct-reject-" + option[2..], 2, false,
                ["--image", input, input, option, option == "--block-size" ? "257" : "45,1,0"]);
            check("image-transform-direct-reject-output-" + option[2..], string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), "");
        }
        check("image-transform-project-input-preserved", Hash(input) == inputHash && File.GetAttributes(input) == inputAttributes && Hash(project) == projectHash, "");
        // 小さな圧縮入力で変換canvasと累積作業量を検査し、大きな出力を作らない。
        var thin = Path.Combine(folder, "thin-4001.png"); WriteEmptyPng(thin, 4001, 1); var thinHash = Hash(thin);
        var canvasResult = await run("image-transform-canvas-budget", 2, false, ["--image", thin, thin, "--left-orientation", "90,0,0"]);
        check("image-transform-canvas-budget-preserved", string.IsNullOrWhiteSpace(canvasResult.Stdout) && canvasResult.Stderr.Contains("1600万", StringComparison.Ordinal) && Hash(thin) == thinHash, "");
        var square = Path.Combine(folder, "square-1200.png"); WriteEmptyPng(square, 1200, 1200); var squareHash = Hash(square);
        var budgetOutput = Path.Combine(proof, "budget-output.png"); File.WriteAllText(budgetOutput, "protected existing output");
        var outputHash = Hash(budgetOutput); var outputAttributes = File.GetAttributes(budgetOutput);
        var budgetActions = Enumerable.Range(0, 63).Select(i => (object)new { kind = "rotate", dst = 0, index = i % 2 == 0 ? 90 : 0 }).ToList();
        budgetActions.Add(new { kind = "export", dst = 0, path = budgetOutput });
        var budgetScript = Path.Combine(folder, "budget-script.json"); File.WriteAllText(budgetScript, JsonSerializer.Serialize(new { blockSize = 256, actions = budgetActions }));
        var scriptHash = Hash(budgetScript);
        var budgetResult = await run("image-transform-cumulative-budget", 2, false, ["--image-copy", square, square, "--script", budgetScript, "--hashes-only"]);
        check("image-transform-cumulative-budget-preserved", string.IsNullOrWhiteSpace(budgetResult.Stdout) && budgetResult.Stderr.Contains("256M", StringComparison.Ordinal)
            && Hash(square) == squareHash && Hash(budgetScript) == scriptHash && Hash(budgetOutput) == outputHash && File.GetAttributes(budgetOutput) == outputAttributes, "");

        void VerifySettings(string path, bool wrapped)
        {
            using var parsed = JsonDocument.Parse(File.ReadAllText(path)); var entry = wrapped ? parsed.RootElement.GetProperty("entries")[0] : parsed.RootElement;
            var settings = entry.GetProperty("imageSettings");
            check("image-transform-project-settings-" + Path.GetFileName(path), settings.GetProperty("blockSize").GetInt32() == 1, "");
            for (var p = 0; p < 3; p++) check("image-transform-project-orientation-" + Path.GetFileName(path) + "-" + p,
                JsonElement.DeepEquals(settings.GetProperty(new[] { "leftOrientation", "middleOrientation", "rightOrientation" }[p]), orientations[p]), "");
        }
        void VerifyHtml(string path)
        {
            var text = File.ReadAllText(path); var tags = Regex.Matches(text, "<img\\b[^>]*>", RegexOptions.CultureInvariant).Cast<Match>().ToArray();
            for (var p = 0; p < 3; p++)
                foreach (var original in new[] { false, true })
                {
                    var side = new[] { "left", "middle", "right" }[p] + (original ? "-original" : "");
                    var tag = tags.Single(m => m.Value.Contains("data-side=\"" + side + "\"", StringComparison.Ordinal)).Value;
                    var encoded = Regex.Match(tag, "src=\"data:image/png;base64,([^\"]+)\"", RegexOptions.CultureInvariant).Groups[1].Value;
                    var pixels = ImageReportScenarios.DecodePng(Convert.FromBase64String(encoded));
                    var wanted = original ? oracle.GetProperty("input") : expected.GetProperty("frames")[p];
                    var rawWidth = wanted.GetProperty("width").GetInt32(); var rawHeight = wanted.GetProperty("height").GetInt32();
                    var width = original ? rawWidth : expected.GetProperty("frames").EnumerateArray().Max(row => row.GetProperty("width").GetInt32());
                    var height = original ? rawHeight : expected.GetProperty("frames").EnumerateArray().Max(row => row.GetProperty("height").GetInt32());
                    var padded = new byte[width * height * 4]; var raw = Convert.FromBase64String(wanted.GetProperty("bgraBase64").GetString()!);
                    for (var y = 0; y < rawHeight; y++) raw.AsSpan(y * rawWidth * 4, rawWidth * 4).CopyTo(padded.AsSpan(y * width * 4));
                    check("image-transform-project-html-" + Path.GetFileName(path) + "-" + side,
                        pixels.Width == width && pixels.Height == height && pixels.Bgra.SequenceEqual(padded), "independent original pixels and transparent canvas padding");
                }
        }
        async Task Reject(string name, string path)
        {
            foreach (var kind in new[] { "--project-copy", "--report-project", "--package-project" })
            {
                var target = Path.Combine(proof, name + kind[2..] + (kind == "--project-copy" ? ".json" : kind == "--report-project" ? ".html" : ".zip"));
                File.WriteAllText(target, "existing protected output"); var before = Hash(target); var attrs = File.GetAttributes(target);
                var result = await run("image-transform-project-reject-" + name + "-" + kind[2..], 2, false, [kind, path, target]);
                check("image-transform-project-reject-preserved-" + name + "-" + kind[2..], string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr)
                    && Hash(target) == before && File.GetAttributes(target) == attrs, "");
            }
        }
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void WriteEmptyPng(string path, int width, int height)
    {
        using var file = File.Create(path); file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = 6; Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, true))
        { var row = new byte[width * 4 + 1]; for (var y = 0; y < height; y++) z.Write(row); }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        void Chunk(string type, byte[] data)
        {
            var bytes = new byte[data.Length + 4]; System.Text.Encoding.ASCII.GetBytes(type).CopyTo(bytes, 0); data.CopyTo(bytes, 4);
            var number = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, data.Length); file.Write(number); file.Write(bytes);
            uint crc = uint.MaxValue;
            foreach (var value in bytes) { crc ^= value; for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1; }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); file.Write(number);
        }
    }
}
