using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class ImageApngScenarios
{
    // 失敗契約: next-image-formats-contract.md。期待画素は手書きCC0 manifestのみ。
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var source = FindFixtures(); var folder = Path.Combine(fixtures, "image-apng");
        var proof = Path.Combine(output, "image-apng"); Directory.CreateDirectory(folder); Directory.CreateDirectory(proof);
        var manifest = await File.ReadAllBytesAsync(Path.Combine(source, "expectations.json"));
        check("apng-manifest-sha", Sha(manifest) == "DDA56A7F2EE200D47F76FC5C96313A7A3E98EE2EEF3E12EE70E40108EE68FAE9", Sha(manifest));
        await File.WriteAllBytesAsync(Path.Combine(proof, "expectations.json"), manifest);
        using var document = JsonDocument.Parse(manifest); var assets = document.RootElement.GetProperty("assets");
        var originals = new Dictionary<string, string>();
        foreach (var asset in assets.EnumerateObject())
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(source, asset.Name));
            check("apng-input-sha-" + asset.Name, Sha(bytes) == asset.Value.GetProperty("fileSha256").GetString(), Sha(bytes));
            await File.WriteAllBytesAsync(PathOf(asset.Name), bytes); originals[PathOf(asset.Name)] = Sha(bytes);
            if (asset.Value.GetProperty("valid").GetBoolean())
                check("apng-oracle-count-" + asset.Name, asset.Value.GetProperty("frameCount").GetInt32() == asset.Value.GetProperty("frames").GetArrayLength(), asset.Name);
            if (asset.Value.GetProperty("valid").GetBoolean()) foreach (var frame in asset.Value.GetProperty("frames").EnumerateArray())
            {
                var raw = Raw(frame);
                check("apng-oracle-" + asset.Name + "-" + frame.GetProperty("pixelSha256").GetString(),
                    raw.Length == frame.GetProperty("width").GetInt32() * frame.GetProperty("height").GetInt32() * 4
                    && Sha(raw) == frame.GetProperty("pixelSha256").GetString()
                    && raw.SequenceEqual(frame.GetProperty("bgra").EnumerateArray().SelectMany(p => p.EnumerateArray().Select(v => v.GetByte()))), Sha(raw));
            }
        }
        foreach (var asset in assets.EnumerateObject())
        {
            var name = asset.Name;
            if (!asset.Value.GetProperty("valid").GetBoolean())
            {
                await Reject("invalid-" + name, ["--image", PathOf(name), PathOf("static-red.png")]);
                await Reject("invalid-selected-" + name, ["--image", PathOf(name), PathOf("static-red.png"), "--left-frame", "1", "--right-frame", "1"]);
                var project = await Project("invalid-" + name, [name, "static-red.png"]);
                foreach (var package in new[] { false, true })
                {
                    var destination = Path.Combine(proof, "protected-" + name + (package ? ".zip" : ".html"));
                    await File.WriteAllTextAsync(destination, "APNG sentinel"); var hash = Hash(destination); var attributes = File.GetAttributes(destination);
                    await Reject("invalid-output-" + name + "-" + package, [package ? "--package-project" : "--report-project", project, destination, .. (package ? new[] { "--report" } : Array.Empty<string>())]);
                    check("apng-protected-" + name + "-" + package, Hash(destination) == hash && File.GetAttributes(destination) == attributes, destination);
                }
                continue;
            }
            await Compare("all-" + name, [name, "static-red.png"], 0);
            for (var n = 1; n <= Frames(name).Length; n++) await Compare("selected-" + name + "-" + n, [name, "static-red.png"], n);
            await Report("all-" + name, [name, "static-red.png"], 0);
            if (Frames(name).Length >= 2) await Report("selected-" + name, [name, name], 2);
        }
        await Compare("same-first", ["same-first-left.png", "same-first-right.png"], 0);
        await Compare("same-first-selected", ["same-first-left.png", "same-first-right.png"], 2);
        await Compare("repeat-last-left", ["static-red.png", "same-first-left.png"], 0);
        var three = new[] { "same-first-left.png", "default-excluded.png", "same-first-right.png" };
        await Compare("three", three, 0); await Compare("three-selected", three, 2);
        await Report("three", three, 0); await Report("three-selected", three, 2);
        var alias = PathOf("alias.apng"); File.Copy(PathOf("same-first-left.png"), alias); originals[alias] = Hash(alias);
        await Compare("extension-alias", ["alias.apng", "same-first-right.png"], 0);
        foreach (var value in new[] { "0", "-1", "3", "abc", "2147483648" })
        {
            await Reject("frame-range-" + value, ["--image", PathOf("same-first-left.png"), PathOf("same-first-right.png"), "--left-frame", value, "--right-frame", "1"]);
            await Reject("right-frame-range-" + value, ["--image", PathOf("same-first-left.png"), PathOf("same-first-right.png"), "--left-frame", "1", "--right-frame", value]);
        }
        var tripleProject = await Project("package", three); var zip = Path.Combine(proof, "package.zip");
        await run("apng-package", 0, true, ["--package-project", tripleProject, zip, "--report"]);
        var unpacked = Path.Combine(proof, "unpacked"); ZipFile.ExtractToDirectory(zip, unpacked);
        VerifyHtml("package", await File.ReadAllTextAsync(Path.Combine(unpacked, "report.files", "1.html")), three, 0);
        var reloaded = Path.Combine(unpacked, "project.json");
        using (var packed = JsonDocument.Parse(await File.ReadAllTextAsync(reloaded)))
        {
            var entry = packed.RootElement.GetProperty("entries")[0]; var keys = new[] { "leftPath", "basePath", "rightPath" };
            for (var pane = 0; pane < keys.Length; pane++)
            {
                var relative = entry.GetProperty(keys[pane]).GetString()!; var path = Path.GetFullPath(Path.Combine(unpacked, relative));
                var inside = !Path.IsPathRooted(relative) && path.StartsWith(Path.GetFullPath(unpacked) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                check("apng-package-input-" + pane, inside && File.Exists(path) && Hash(path) == originals[PathOf(three[pane])], relative);
            }
        }
        await Report("reloaded", three, 0, reloaded);
        var workInput = PathOf("shared-work-limit.png"); WriteWorkLimitApng(workInput); originals[workInput] = Hash(workInput);
        check("apng-work-input-small", new FileInfo(workInput).Length <= 40_000, new FileInfo(workInput).Length.ToString(CultureInfo.InvariantCulture));
        await File.WriteAllTextAsync(Path.Combine(proof, "shared-work-input.json"), JsonSerializer.Serialize(new {
            width = 3000, height = 3000, frames = 15, source = "BCL valid RGBA APNG, zero default plus 14 transparent 1x1 SOURCE/NONE frames",
            bytes = new FileInfo(workInput).Length, sha256 = originals[workInput], selectedDecodePixels = 270_000_000L,
            allDecodePixels = 2_160_000_000L, expected = "exit2 before native pixel allocation; no success JSON; 復号作業量" }));
        foreach (var (command, selected) in new[] { ("--image", false), ("--image", true), ("--image-regions", true) })
        {
            var label = "apng-work-" + command[2..] + (selected ? "-selected15" : "-all");
            var result = await run(label, 2, false, [command, workInput, workInput, .. (selected ? new[] { "--left-frame", "15", "--right-frame", "15" } : Array.Empty<string>())]);
            check(label + "-preflight", string.IsNullOrWhiteSpace(result.Stdout) && result.Stderr.Contains("復号作業量", StringComparison.Ordinal), result.Stderr);
            check(label + "-input-preserved", Hash(workInput) == originals[workInput], workInput);
        }
        foreach (var input in originals) check("apng-input-preserved-" + Path.GetFileName(input.Key), Hash(input.Key) == input.Value, input.Key);

        string PathOf(string name) => Path.Combine(folder, name);
        JsonElement[] Frames(string name) => assets.GetProperty(name == "alias.apng" ? "same-first-left.png" : name).GetProperty("frames").EnumerateArray().ToArray();
        string[] Sides(string[] names) => names.Length == 3 ? ["left", "middle", "right"] : ["left", "right"];
        int Number(string name, int row, int selected) => Math.Min(selected == 0 ? row + 1 : selected, Frames(name).Length);
        string[] Selection(string[] names, int selected) => selected == 0 ? [] : Sides(names).SelectMany((s, p) => new[] { "--" + s + "-frame", Number(names[p], 0, selected).ToString(CultureInfo.InvariantCulture) }).ToArray();
        async Task<string> Project(string label, string[] names)
        {
            var path = Path.Combine(folder, label + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { formatVersion = 1, activeEntryIndex = 0, entries = new[] {
                new { leftPath = PathOf(names[0]), basePath = names.Length == 3 ? PathOf(names[1]) : "", rightPath = PathOf(names[^1]), mode = "Image" } } }));
            originals[path] = Hash(path); return path;
        }
        async Task Reject(string label, string[] args)
        {
            var result = await run("apng-" + label, 2, false, args);
            check("apng-" + label + "-no-success-json", string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        }
        async Task Compare(string label, string[] names, int selected)
        {
            var count = selected == 0 ? names.Max(n => Frames(n).Length) : 1;
            var different = selected == 0 && names.Select(n => Frames(n).Length).Distinct().Count() > 1;
            for (var row = 0; row < count; row++) different |= names.Select(n => Frames(n)[Number(n, row, selected) - 1].GetProperty("pixelSha256").GetString()).Distinct().Count() > 1;
            var result = await run("apng-" + label, different ? 1 : 0, true, ["--image", .. names.Select(PathOf), .. Selection(names, selected)]);
            using var json = JsonDocument.Parse(result.Stdout); var root = json.RootElement; var actual = root.GetProperty("frames");
            check("apng-" + label + "-mode", root.GetProperty("different").GetBoolean() == different && root.GetProperty("mode").GetString() == (selected == 0 ? "all" : "selected") && actual.GetArrayLength() == count, result.Stdout);
            var sides = Sides(names);
            for (var p = 0; p < names.Length; p++)
            {
                check("apng-" + label + "-count-" + sides[p], root.GetProperty(sides[p] + "Frames").GetInt32() == Frames(names[p]).Length, result.Stdout);
                for (var row = 0; row < Math.Min(count, actual.GetArrayLength()); row++)
                {
                    var number = Number(names[p], row, selected); var expected = Frames(names[p])[number - 1]; var frame = actual[row]; var side = sides[p];
                    check("apng-" + label + "-" + side + "-" + row, frame.GetProperty(side + "Frame").GetInt32() == number
                        && frame.GetProperty(side + "Width").GetInt32() == expected.GetProperty("width").GetInt32()
                        && frame.GetProperty(side + "Height").GetInt32() == expected.GetProperty("height").GetInt32()
                        && EqualHash(frame.GetProperty(side + "PixelSha256").GetString(), expected.GetProperty("pixelSha256").GetString()), frame.GetRawText());
                }
            }
        }
        async Task Report(string label, string[] names, int selected, string? project = null)
        {
            var destination = Path.Combine(proof, label + ".html");
            await run("apng-report-" + label, 0, true, ["--report-project", project ?? await Project(label, names), destination, .. Selection(names, selected)]);
            VerifyHtml(label, await File.ReadAllTextAsync(destination), names, selected);
        }
        void VerifyHtml(string label, string html, string[] names, int selected)
        {
            var body = Regex.Match(html, @"<body\b(?<a>[^>]*)>").Groups["a"].Value;
            check("apng-html-" + label + "-mode", Attr(body, "data-mode") == "Image" && Attr(body, "data-frame-mode") == (selected == 0 ? "all" : "selected"), body);
            var rows = Regex.Matches(html, @"<tr\b(?<a>[^>]*)>(?<c>[\s\S]*?)</tr>").Cast<Match>().Where(m => Attr(m.Groups["a"].Value, "data-left-frame") != "").ToArray();
            var count = selected == 0 ? names.Max(n => Frames(n).Length) : 1; var sides = Sides(names);
            check("apng-html-" + label + "-rows", rows.Length == count, rows.Length.ToString(CultureInfo.InvariantCulture));
            for (var p = 0; p < names.Length; p++)
            {
                check("apng-html-" + label + "-count-" + sides[p], Attr(body, "data-" + sides[p] + "-frames") == Frames(names[p]).Length.ToString(CultureInfo.InvariantCulture), body);
                for (var row = 0; row < Math.Min(count, rows.Length); row++)
                {
                    var number = Number(names[p], row, selected); var expected = Frames(names[p])[number - 1];
                    var images = Regex.Matches(rows[row].Groups["c"].Value, @"<img\b(?<a>[^>]*)>").Cast<Match>().Where(m => Attr(m.Groups["a"].Value, "data-side") == sides[p] + "-original").ToArray();
                    var id = "apng-html-" + label + "-" + sides[p] + "-" + row;
                    check(id + "-unique", images.Length == 1, images.Length.ToString(CultureInfo.InvariantCulture)); if (images.Length != 1) continue;
                    var attrs = images[0].Groups["a"].Value; var uri = Attr(attrs, "src");
                    var png = Convert.FromBase64String(uri.StartsWith("data:image/png;base64,", StringComparison.Ordinal) ? uri[22..] : throw new InvalidDataException("Expected PNG"));
                    var decoded = ImageReportScenarios.DecodePng(png);
                    check(id + "-bgra", decoded.Width == expected.GetProperty("width").GetInt32() && decoded.Height == expected.GetProperty("height").GetInt32() && decoded.Bgra.SequenceEqual(Raw(expected)), Sha(decoded.Bgra));
                    check(id + "-metadata", Attr(attrs, "data-frame") == number.ToString(CultureInfo.InvariantCulture)
                        && Attr(rows[row].Groups["a"].Value, "data-" + sides[p] + "-frame") == number.ToString(CultureInfo.InvariantCulture)
                        && Attr(attrs, "data-width") == decoded.Width.ToString(CultureInfo.InvariantCulture) && Attr(attrs, "data-height") == decoded.Height.ToString(CultureInfo.InvariantCulture)
                        && EqualHash(Attr(attrs, "data-pixel-sha256"), expected.GetProperty("pixelSha256").GetString()) && EqualHash(Sha(decoded.Bgra), expected.GetProperty("pixelSha256").GetString()), attrs);
                    File.WriteAllBytes(Path.Combine(proof, label + "-" + sides[p] + "-" + row + ".png"), png);
                }
            }
        }
    }
    private static byte[] Raw(JsonElement frame) => Convert.FromBase64String(frame.GetProperty("bgraBase64").GetString()!);
    private static bool EqualHash(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Hash(string path) => Sha(File.ReadAllBytes(path));
    private static string Attr(string source, string name) => WebUtility.HtmlDecode(Regex.Match(source, "\\b" + Regex.Escape(name) + "=[\"'](?<v>[^\"']*)[\"']").Groups["v"].Value);
    // 正常な小圧縮データ。36MB rawは生成時だけ使い、期待画素のoracleにはしない。
    private static void WriteWorkLimitApng(string path)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; Put(header, 0, 3000); Put(header, 4, 3000); header[8] = 8; header[9] = 6;
        Chunk("IHDR", header); var animation = new byte[8]; Put(animation, 0, 15); Chunk("acTL", animation);
        var large = Compress(new byte[3000 * (3000 * 4 + 1)]); var tiny = Compress(new byte[5]); uint sequence = 0;
        for (var frame = 0; frame < 15; frame++)
        {
            var control = new byte[26]; Put(control, 0, sequence++); Put(control, 4, frame == 0 ? 3000u : 1u); Put(control, 8, frame == 0 ? 3000u : 1u);
            control[21] = 1; control[23] = 10; Chunk("fcTL", control);
            if (frame == 0) Chunk("IDAT", large);
            else { var data = new byte[tiny.Length + 4]; Put(data, 0, sequence++); tiny.CopyTo(data, 4); Chunk("fdAT", data); }
        }
        Chunk("IEND", []);
        void Chunk(string type, byte[] data)
        {
            var name = System.Text.Encoding.ASCII.GetBytes(type); var number = new byte[4]; Put(number, 0, (uint)data.Length);
            output.Write(number); output.Write(name); output.Write(data); uint crc = uint.MaxValue;
            foreach (var b in name) Update(b); foreach (var b in data) Update(b);
            Put(number, 0, ~crc); output.Write(number);
            void Update(byte b) { crc ^= b; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u); }
        }
        static void Put(byte[] bytes, int offset, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
        static byte[] Compress(byte[] raw)
        {
            using var result = new MemoryStream(); using (var zlib = new ZLibStream(result, CompressionLevel.SmallestSize, true)) zlib.Write(raw);
            return result.ToArray();
        }
    }
    private static string FindFixtures()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        { var path = Path.Combine(d.FullName, "tests", "Fixtures", "Images", "Apng"); if (File.Exists(Path.Combine(path, "expectations.json"))) return path; }
        throw new FileNotFoundException("APNG fixtures not found");
    }
}
