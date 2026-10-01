using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ImageFrameScenarios
{
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var source = FindFixtures();
        var folder = Path.Combine(fixtures, "image-frames");
        Directory.CreateDirectory(folder);
        using var expectations = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(source, "expectations.json")));
        var assets = expectations.RootElement.GetProperty("assets");
        var baseline = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var asset in assets.EnumerateObject())
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(source, asset.Name));
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            check("image-fixture-hash-" + asset.Name, hash == asset.Value.GetProperty("fileSha256").GetString(), hash);
            var destination = Path.Combine(folder, asset.Name);
            await File.WriteAllBytesAsync(destination, bytes);
            baseline[destination] = hash;
        }
        File.Copy(Path.Combine(source, "expectations.json"), Path.Combine(output, "image-frame-expectations.json"), true);

        await Compare("static-equal", "red.png", "red.png");
        await Compare("static-different", "red.png", "green.png");
        await Compare("static-selected", "red.png", "red.png", 1, 1);
        await Compare("all-second-different", "same-first-left.gif", "same-first-right.gif");
        await Compare("selected-first-equal", "same-first-left.gif", "same-first-right.gif", 1, 1);
        await Compare("selected-second-different", "same-first-left.gif", "same-first-right.gif", 2, 2);
        await Compare("selected-cross-equal", "same-first-left.gif", "green.png", 2, 1);
        await Compare("selected-cross-different", "same-first-left.gif", "same-first-right.gif", 2, 1);
        await Compare("all-equal", "same-first-left.gif", "same-first-left.gif");
        await Compare("missing-right", "same-first-left.gif", "short.gif");
        await Compare("missing-left", "short.gif", "same-first-left.gif");
        await Compare("missing-at-max-threshold", "short.gif", "same-first-left.gif", threshold: 255);
        await Compare("dimensions", "red.png", "red-wide.png");
        await Compare("alpha", "alpha-opaque.png", "alpha-half.png");
        await Compare("threshold-default", "threshold-100.png", "threshold-105.png");
        await Compare("threshold-below", "threshold-100.png", "threshold-105.png", threshold: 4);
        await Compare("threshold-equal", "threshold-100.png", "threshold-105.png", threshold: 5);
        await Compare("threshold-above", "threshold-100.png", "threshold-105.png", threshold: 6);
        await Compare("threshold-alpha-equal", "alpha-opaque.png", "alpha-half.png", threshold: 127);
        await Compare("threshold-maximum", "red.png", "green.png", threshold: 255);
        for (var frame = 1; frame <= 4; frame++)
            await Compare("disposal-page-" + frame, "disposal.gif", "disposal-" + frame + ".png", frame, 1);
        await Compare("disposal-all", "disposal.gif", "disposal.gif");
        await Compare("transparent-update", "transparent-update.gif", "disposal-2.png", 2, 1);
        await Compare("webp-all-second-different", "lossless-left.webp", "lossless-right.webp");
        await Compare("webp-first-equal", "lossless-left.webp", "lossless-right.webp", 1, 1);
        await Compare("webp-transparent-all", "lossless-transparent.webp", "lossless-transparent.webp");

        string PathOf(string name) => Path.Combine(folder, name);
        var left = PathOf("same-first-left.gif");
        var right = PathOf("same-first-right.gif");
        await Reject("left-selection-only", ["--image", left, right, "--left-frame", "1"]);
        await Reject("right-selection-only", ["--image", left, right, "--right-frame", "1"]);
        foreach (var value in new[] { "0", "-1", "3", "abc", "1.5", "2147483648" })
        {
            await Reject("left-frame-" + value, ["--image", left, right, "--left-frame", value, "--right-frame", "1"]);
            await Reject("right-frame-" + value, ["--image", left, right, "--left-frame", "1", "--right-frame", value]);
        }
        foreach (var value in new[] { "-1", "NaN", "Infinity", "abc", "1e9999" })
            await Reject("threshold-" + value, ["--image", left, right, "--threshold", value]);
        await Reject("unknown-option", ["--image", left, right, "--not-an-image-option"]);
        await Reject("duplicate-threshold", ["--image", left, right, "--threshold", "0", "--threshold", "1"]);
        await Reject("duplicate-left-frame", ["--image", left, right, "--left-frame", "1", "--left-frame", "2", "--right-frame", "1"]);
        await Reject("duplicate-right-frame", ["--image", left, right, "--left-frame", "1", "--right-frame", "1", "--right-frame", "2"]);
        await Reject("missing-threshold-value", ["--image", left, right, "--threshold"]);
        await Reject("missing-frame-value", ["--image", left, right, "--left-frame"]);
        await Reject("missing-right-input", ["--image", left]);
        await Reject("extra-input", ["--image", left, right, left, right]);
        await Reject("missing-file", ["--image", PathOf("nonexistent.gif"), right]);
        foreach (var name in new[] { "broken.gif", "truncated.gif", "over-pixel-limit.gif", "over-frame-limit.gif", "over-work-limit.gif" })
        {
            await Reject(name, ["--image", PathOf(name), right], name == "over-work-limit.gif" ? "復号作業量" : null);
            await Reject("selected-" + name, ["--image", PathOf(name), right, "--left-frame",
                name == "over-work-limit.gif" ? "17" : "1", "--right-frame", "1"], name == "over-work-limit.gif" ? "復号作業量" : null);
        }
        await Reject("canvas-limit", ["--image", PathOf("canvas-wide.gif"), PathOf("canvas-tall.gif")], "比較キャンバス");
        await Reject("selected-canvas-limit", ["--image", PathOf("canvas-wide.gif"), PathOf("canvas-tall.gif"), "--left-frame", "1", "--right-frame", "1"], "比較キャンバス");
        var wide = PathOf("render-wide.gif"); var tall = PathOf("render-tall.gif");
        WriteSolidGif(wide, 2048, 1, 64); WriteSolidGif(tall, 1, 2048, 64);
        baseline[wide] = Hash(wide); baseline[tall] = Hash(tall);
        await Reject("render-work", ["--image", wide, tall], "描画作業量");
        var selectedRender = await run("image-selected-render-work", 1, true, ["--image", wide, tall, "--left-frame", "1", "--right-frame", "1"]);
        using (var selectedJson = JsonDocument.Parse(selectedRender.Stdout))
            check("image-selected-render-work-counts", selectedJson.RootElement.GetProperty("leftFrames").GetInt32() == 64
                && selectedJson.RootElement.GetProperty("rightFrames").GetInt32() == 64
                && selectedJson.RootElement.GetProperty("frames").GetArrayLength() == 1, selectedRender.Stdout);
        var renderProject = PathOf("render-work.json");
        await File.WriteAllTextAsync(renderProject, JsonSerializer.Serialize(new { formatVersion = 1, entries = new[] {
            new { leftPath = wide, rightPath = tall, mode = "Image" } }, activeEntryIndex = 0 }));
        foreach (var package in new[] { false, true })
        {
            var destination = PathOf(package ? "render-work.zip" : "render-work.html"); File.WriteAllText(destination, "protected output");
            var before = Hash(destination);
            var refused = await run("image-render-work-" + (package ? "package" : "report"), 2, false,
                [package ? "--package-project" : "--report-project", renderProject, destination, .. (package ? new[] { "--report" } : Array.Empty<string>())]);
            check("image-render-work-diagnostic-" + package, refused.Stderr.Contains("描画作業量", StringComparison.Ordinal), refused.Stderr);
            check("image-render-work-output-preserved-" + package, before == Hash(destination), destination);
        }
        // 大きい実画像を生成せず、入力サイズ上限を実ファイルで確認する。
        var oversized = PathOf("over-file-limit.gif");
        using (var stream = File.Create(oversized))
        {
            stream.Write(await File.ReadAllBytesAsync(left));
            stream.SetLength(64L * 1024 * 1024 + 1);
        }
        baseline[oversized] = Hash(oversized);
        await Reject("over-file-limit", ["--image", oversized, right]);
        foreach (var pair in baseline)
            check("image-final-input-preserved-" + Path.GetFileName(pair.Key), Hash(pair.Key) == pair.Value, pair.Key);

        async Task Compare(string name, string leftName, string rightName, int? leftFrame = null, int? rightFrame = null, int threshold = 0)
        {
            name = "image-" + name;
            var leftFrames = assets.GetProperty(leftName).GetProperty("frames").EnumerateArray().ToArray();
            var rightFrames = assets.GetProperty(rightName).GetProperty("frames").EnumerateArray().ToArray();
            var selected = leftFrame.HasValue;
            var pairs = selected ? new[] { (Left: leftFrame, Right: rightFrame) }
                : Enumerable.Range(1, Math.Max(leftFrames.Length, rightFrames.Length))
                    .Select(i => (Left: (int?)Math.Min(i, leftFrames.Length), Right: (int?)Math.Min(i, rightFrames.Length))).ToArray();
            var expected = pairs.Select(pair => Expected(pair.Left.HasValue ? leftFrames[pair.Left.Value - 1] : (JsonElement?)null,
                pair.Right.HasValue ? rightFrames[pair.Right.Value - 1] : (JsonElement?)null, threshold)).ToArray();
            var different = expected.Any(frame => frame.Different) || !selected && leftFrames.Length != rightFrames.Length;
            var arguments = new List<string> { "--image", PathOf(leftName), PathOf(rightName) };
            if (selected) arguments.AddRange(["--left-frame", leftFrame!.Value.ToString(CultureInfo.InvariantCulture),
                "--right-frame", rightFrame!.Value.ToString(CultureInfo.InvariantCulture)]);
            if (threshold != 0) arguments.AddRange(["--threshold", threshold.ToString(CultureInfo.InvariantCulture)]);
            var result = await run(name, different ? 1 : 0, true, arguments.ToArray());
            try
            {
                using var json = JsonDocument.Parse(result.Stdout);
                var root = json.RootElement;
                check(name + "-metadata", root.GetProperty("different").GetBoolean() == different
                    && root.GetProperty("leftFrames").GetInt32() == leftFrames.Length
                    && root.GetProperty("rightFrames").GetInt32() == rightFrames.Length
                    && root.GetProperty("threshold").GetInt32() == threshold
                    && root.GetProperty("mode").GetString() == (selected ? "selected" : "all"), result.Stdout);
                var actual = root.GetProperty("frames").EnumerateArray().ToArray();
                check(name + "-frame-count", actual.Length == expected.Length, result.Stdout);
                for (var i = 0; i < Math.Min(actual.Length, expected.Length); i++)
                {
                    var frame = actual[i];
                    var wanted = expected[i];
                    check(name + "-page-" + (i + 1), NullableNumber(frame, "leftFrame", pairs[i].Left)
                        && NullableNumber(frame, "rightFrame", pairs[i].Right)
                        && NullableNumber(frame, "leftWidth", wanted.LeftWidth) && NullableNumber(frame, "leftHeight", wanted.LeftHeight)
                        && NullableNumber(frame, "rightWidth", wanted.RightWidth) && NullableNumber(frame, "rightHeight", wanted.RightHeight)
                        && NullableText(frame, "leftPixelSha256", wanted.LeftSha) && NullableText(frame, "rightPixelSha256", wanted.RightSha)
                        && frame.GetProperty("differentPixels").GetInt64() == wanted.DifferentPixels
                        && frame.GetProperty("totalPixels").GetInt64() == wanted.TotalPixels, frame.GetRawText());
                }
                check(name + "-stderr-empty", string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                check(name + "-json-contract", false, error.Message + ": " + result.Stdout);
            }
            Preserved(name, arguments);
        }

        async Task Reject(string name, string[] arguments, string? requiredDiagnostic = null)
        {
            name = "image-reject-" + name;
            var result = await run(name, 2, false, arguments);
            var success = false;
            try { using var document = JsonDocument.Parse(result.Stdout); success = document.RootElement.TryGetProperty("different", out _); }
            catch (JsonException) { }
            check(name + "-no-success-json", !success, result.Stdout);
            check(name + "-diagnostic", !string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            if (requiredDiagnostic is not null) check(name + "-limit-reason", result.Stderr.Contains(requiredDiagnostic, StringComparison.Ordinal), result.Stderr);
            Preserved(name, arguments);
        }

        void Preserved(string name, IEnumerable<string> arguments)
        {
            foreach (var path in arguments.Distinct(StringComparer.Ordinal).Where(baseline.ContainsKey))
                check(name + "-input-" + Path.GetFileName(path), baseline[path] == Hash(path), path);
        }
    }

    private static ExpectedFrame Expected(JsonElement? left, JsonElement? right, int threshold)
    {
        int? Width(JsonElement? frame) => frame?.GetProperty("width").GetInt32();
        int? Height(JsonElement? frame) => frame?.GetProperty("height").GetInt32();
        var lw = Width(left); var lh = Height(left); var rw = Width(right); var rh = Height(right);
        var width = Math.Max(lw ?? 0, rw ?? 0); var height = Math.Max(lh ?? 0, rh ?? 0);
        var lb = left.HasValue ? Convert.FromHexString(left.Value.GetProperty("bgraHex").GetString()!) : [];
        var rb = right.HasValue ? Convert.FromHexString(right.Value.GetProperty("bgraHex").GetString()!) : [];
        long differences = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (x >= (lw ?? 0) || y >= (lh ?? 0) || x >= (rw ?? 0) || y >= (rh ?? 0)) { differences++; continue; }
            var li = (y * lw!.Value + x) * 4; var ri = (y * rw!.Value + x) * 4;
            var squared = Enumerable.Range(0, 4).Sum(c => (lb[li + c] - rb[ri + c]) * (lb[li + c] - rb[ri + c]));
            if (squared > (double)threshold * threshold) differences++;
        }
        return new(lw, lh, rw, rh, left?.GetProperty("pixelSha256").GetString(), right?.GetProperty("pixelSha256").GetString(),
            differences, (long)width * height, differences != 0 || lw != rw || lh != rh);
    }

    private static bool NullableNumber(JsonElement element, string property, int? expected)
        => expected.HasValue ? element.GetProperty(property).GetInt32() == expected.Value : element.GetProperty(property).ValueKind == JsonValueKind.Null;
    private static bool NullableText(JsonElement element, string property, string? expected)
        => expected is null ? element.GetProperty(property).ValueKind == JsonValueKind.Null
            : string.Equals(element.GetProperty(property).GetString(), expected, StringComparison.OrdinalIgnoreCase);
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    // clear-codeを各画素の前へ置き、辞書の成長に依存しない単色の完全GIFを作る。
    private static void WriteSolidGif(string path, int width, int height, int frames)
    {
        using var file = File.Create(path); using var writer = new BinaryWriter(file);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("GIF89a")); writer.Write((ushort)width); writer.Write((ushort)height);
        writer.Write(new byte[] { 0x80, 0, 0, 20, 40, 60, 255, 255, 255 });
        var data = new List<byte>(); var bits = 0; var pending = 0;
        void Code(int value) { pending |= value << bits; bits += 3; while (bits >= 8) { data.Add((byte)pending); pending >>= 8; bits -= 8; } }
        for (var pixel = 0; pixel < width * height; pixel++) { Code(4); Code(0); } Code(5); if (bits > 0) data.Add((byte)pending);
        for (var frame = 0; frame < frames; frame++)
        {
            writer.Write(new byte[] { 0x21, 0xf9, 4, 4, 1, 0, 0, 0, 0x2c });
            writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)width); writer.Write((ushort)height); writer.Write((byte)0); writer.Write((byte)2);
            for (var p = 0; p < data.Count; p += 255) { var length = Math.Min(255, data.Count - p); writer.Write((byte)length); writer.Write(data.GetRange(p, length).ToArray()); }
            writer.Write((byte)0);
        }
        writer.Write((byte)0x3b);
    }
    private static string FindFixtures()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "Fixtures", "Images");
            if (File.Exists(Path.Combine(path, "expectations.json"))) return path;
        }
        throw new FileNotFoundException("Image frame fixtures were not found in the repository.");
    }
    private sealed record ExpectedFrame(int? LeftWidth, int? LeftHeight, int? RightWidth, int? RightHeight,
        string? LeftSha, string? RightSha, long DifferentPixels, long TotalPixels, bool Different);
}
