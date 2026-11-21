using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class ImageTiffScenarios
{
    // 失敗契約: tiff-fixture-contract.md。期待画素は手書きCC0 manifestのみ。
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var source = FindFixtures(); var folder = Path.Combine(fixtures, "image-tiff");
        var proof = Path.Combine(output, "image-tiff"); Directory.CreateDirectory(folder); Directory.CreateDirectory(proof);
        var manifest = await File.ReadAllBytesAsync(Path.Combine(source, "expectations.json"));
        check("tiff-manifest-sha", Sha(manifest) == "0AC09BBC0D62A821484A77A1C8F7F7B4554216E051349F2674F255A401A8D75B", Sha(manifest));
        if (Sha(manifest) != "0AC09BBC0D62A821484A77A1C8F7F7B4554216E051349F2674F255A401A8D75B") throw new InvalidDataException("TIFF manifest changed");
        await File.WriteAllBytesAsync(Path.Combine(proof, "expectations.json"), manifest);
        using var document = JsonDocument.Parse(manifest); var assets = document.RootElement.GetProperty("assets");
        check("tiff-generator-sha", Hash(Path.Combine(source, "generate.py")) == document.RootElement.GetProperty("generatorSha256").GetString(), "generator provenance");
        check("tiff-license-sha", Hash(Path.Combine(source, "LICENSE.txt")) == document.RootElement.GetProperty("licenseSha256").GetString(), "CC0 provenance");
        var originals = new Dictionary<string, string>();
        foreach (var asset in assets.EnumerateObject())
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(source, asset.Name));
            check("tiff-input-sha-" + asset.Name, Sha(bytes) == asset.Value.GetProperty("fileSha256").GetString(), Sha(bytes));
            await File.WriteAllBytesAsync(PathOf(asset.Name), bytes); originals[PathOf(asset.Name)] = Sha(bytes);
            if (asset.Value.GetProperty("valid").GetBoolean())
                check("tiff-oracle-count-" + asset.Name, asset.Value.GetProperty("frameCount").GetInt32() == asset.Value.GetProperty("frames").GetArrayLength(), asset.Name);
            if (asset.Value.GetProperty("valid").GetBoolean()) foreach (var frame in asset.Value.GetProperty("frames").EnumerateArray())
            {
                var raw = Raw(frame);
                check("tiff-oracle-" + asset.Name + "-" + frame.GetProperty("pixelSha256").GetString(),
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
                await Reject("invalid-" + name, ["--image", PathOf(name), PathOf("single-page.tif")]);
                await Reject("invalid-selected-" + name, ["--image", PathOf(name), PathOf("single-page.tif"), "--left-frame", "1", "--right-frame", "1"]);
                var project = await Project("invalid-" + name, [name, "single-page.tif"]);
                foreach (var package in new[] { false, true })
                {
                    var destination = Path.Combine(proof, "protected-" + name + (package ? ".zip" : ".html"));
                    await File.WriteAllTextAsync(destination, "TIFF sentinel"); var hash = Hash(destination); var attributes = File.GetAttributes(destination);
                    await Reject("invalid-output-" + name + "-" + package, [package ? "--package-project" : "--report-project", project, destination, .. (package ? new[] { "--report" } : Array.Empty<string>())]);
                    check("tiff-protected-" + name + "-" + package, Hash(destination) == hash && File.GetAttributes(destination) == attributes, destination);
                }
                continue;
            }
            await Compare("all-" + name, [name, "single-page.tif"], 0);
            for (var n = 1; n <= Frames(name).Length; n++) await Compare("selected-" + name + "-" + n, [name, "single-page.tif"], n);
            await Report("all-" + name, [name, "single-page.tif"], 0);
            if (Frames(name).Length >= 2) await Report("selected-" + name, [name, name], 2);
        }
        await Compare("same-first", ["two-pages-le.tif", "same-first-right.tif"], 0);
        await Compare("byte-order-equal", ["two-pages-le.tif", "two-pages-be.tiff"], 0);
        await Compare("same-first-selected1", ["two-pages-le.tif", "same-first-right.tif"], 1);
        await Compare("same-first-selected", ["two-pages-le.tif", "same-first-right.tif"], 2);
        await Compare("repeat-last-left", ["single-page.tif", "two-pages-le.tif"], 0);
        var three = new[] { "two-pages-le.tif", "same-first-middle.tif", "same-first-right.tif" };
        await Compare("three", three, 0); await Compare("three-selected", three, 2);
        await Report("three", three, 0); await Report("three-selected", three, 2);
        foreach (var names in new[] { new[] { "two-pages-le.tif", "two-pages-be.tiff" }, three })
        {
            var different = names.Length == 3; var label = "tiff-regions-page2-" + names.Length;
            var result = await run(label, different ? 1 : 0, true, ["--image-regions", .. names.Select(PathOf), .. Selection(names, 2), "--highlight-alpha", "0"]);
            using var json = JsonDocument.Parse(result.Stdout); var root = json.RootElement;
            check(label + "-metadata", root.GetProperty("width").GetInt32() == 1 && root.GetProperty("height").GetInt32() == 3
                && root.GetProperty("frameNumbers").EnumerateArray().All(n => n.GetInt32() == 2)
                && root.GetProperty("frameNumbers").GetArrayLength() == names.Length
                && root.GetProperty("differenceCount").GetInt32() == (different ? 1 : 0)
                && root.GetProperty("conflictCount").GetInt32() == (different ? 1 : 0), result.Stdout);
            var rendered = root.GetProperty("renderedFrames");
            check(label + "-pane-count", rendered.GetArrayLength() == names.Length, result.Stdout);
            for (var pane = 0; pane < Math.Min(names.Length, rendered.GetArrayLength()); pane++)
            {
                var expected = Frames(names[pane])[1]; var actual = rendered[pane];
                check(label + "-raw-page-" + pane, actual.GetProperty("frame").GetInt32() == 2 && actual.GetProperty("width").GetInt32() == 1
                    && actual.GetProperty("height").GetInt32() == 3 && EqualHash(actual.GetProperty("pixelSha256").GetString(), expected.GetProperty("pixelSha256").GetString()), actual.GetRawText());
            }
        }
        foreach (var value in new[] { "0", "-1", "3", "abc", "2147483648" })
        {
            await Reject("frame-range-" + value, ["--image", PathOf("two-pages-le.tif"), PathOf("same-first-right.tif"), "--left-frame", value, "--right-frame", "1"]);
            await Reject("right-frame-range-" + value, ["--image", PathOf("two-pages-le.tif"), PathOf("same-first-right.tif"), "--left-frame", "1", "--right-frame", value]);
        }
        var tripleProject = await Project("package", three); var zip = Path.Combine(proof, "package.zip");
        await run("tiff-package", 0, true, ["--package-project", tripleProject, zip, "--report"]);
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
                check("tiff-package-input-" + pane, inside && File.Exists(path) && Hash(path) == originals[PathOf(three[pane])], relative);
            }
        }
        await Report("reloaded", three, 0, reloaded);
        foreach (var input in originals) check("tiff-input-preserved-" + Path.GetFileName(input.Key), Hash(input.Key) == input.Value, input.Key);
        await File.WriteAllTextAsync(Path.Combine(proof, "input-sha256.json"), JsonSerializer.Serialize(originals.Select(input => new {
            path = input.Key, before = input.Value, after = Hash(input.Key) }), new JsonSerializerOptions { WriteIndented = true }));

        string PathOf(string name) => Path.Combine(folder, name);
        JsonElement[] Frames(string name) => assets.GetProperty(name).GetProperty("frames").EnumerateArray().ToArray();
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
            var result = await run("tiff-" + label, 2, false, args);
            check("tiff-" + label + "-no-success-json", string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        }
        async Task Compare(string label, string[] names, int selected)
        {
            var count = selected == 0 ? names.Max(n => Frames(n).Length) : 1;
            var different = selected == 0 && names.Select(n => Frames(n).Length).Distinct().Count() > 1;
            for (var row = 0; row < count; row++) different |= names.Select(n => Frames(n)[Number(n, row, selected) - 1].GetProperty("pixelSha256").GetString()).Distinct().Count() > 1;
            var result = await run("tiff-" + label, different ? 1 : 0, true, ["--image", .. names.Select(PathOf), .. Selection(names, selected)]);
            using var json = JsonDocument.Parse(result.Stdout); var root = json.RootElement; var actual = root.GetProperty("frames");
            check("tiff-" + label + "-mode", root.GetProperty("different").GetBoolean() == different && root.GetProperty("mode").GetString() == (selected == 0 ? "all" : "selected") && actual.GetArrayLength() == count, result.Stdout);
            var sides = Sides(names);
            for (var p = 0; p < names.Length; p++)
            {
                check("tiff-" + label + "-count-" + sides[p], root.GetProperty(sides[p] + "Frames").GetInt32() == Frames(names[p]).Length, result.Stdout);
                for (var row = 0; row < Math.Min(count, actual.GetArrayLength()); row++)
                {
                    var number = Number(names[p], row, selected); var expected = Frames(names[p])[number - 1]; var frame = actual[row]; var side = sides[p];
                    check("tiff-" + label + "-" + side + "-" + row, frame.GetProperty(side + "Frame").GetInt32() == number
                        && frame.GetProperty(side + "Width").GetInt32() == expected.GetProperty("width").GetInt32()
                        && frame.GetProperty(side + "Height").GetInt32() == expected.GetProperty("height").GetInt32()
                        && EqualHash(frame.GetProperty(side + "PixelSha256").GetString(), expected.GetProperty("pixelSha256").GetString()), frame.GetRawText());
                }
            }
        }
        async Task Report(string label, string[] names, int selected, string? project = null)
        {
            var destination = Path.Combine(proof, label + ".html");
            await run("tiff-report-" + label, 0, true, ["--report-project", project ?? await Project(label, names), destination, .. Selection(names, selected)]);
            VerifyHtml(label, await File.ReadAllTextAsync(destination), names, selected);
        }
        void VerifyHtml(string label, string html, string[] names, int selected)
        {
            var body = Regex.Match(html, @"<body\b(?<a>[^>]*)>").Groups["a"].Value;
            check("tiff-html-" + label + "-mode", Attr(body, "data-mode") == "Image" && Attr(body, "data-frame-mode") == (selected == 0 ? "all" : "selected"), body);
            var rows = Regex.Matches(html, @"<tr\b(?<a>[^>]*)>(?<c>[\s\S]*?)</tr>").Cast<Match>().Where(m => Attr(m.Groups["a"].Value, "data-left-frame") != "").ToArray();
            var count = selected == 0 ? names.Max(n => Frames(n).Length) : 1; var sides = Sides(names);
            check("tiff-html-" + label + "-rows", rows.Length == count, rows.Length.ToString(CultureInfo.InvariantCulture));
            for (var p = 0; p < names.Length; p++)
            {
                check("tiff-html-" + label + "-count-" + sides[p], Attr(body, "data-" + sides[p] + "-frames") == Frames(names[p]).Length.ToString(CultureInfo.InvariantCulture), body);
                for (var row = 0; row < Math.Min(count, rows.Length); row++)
                {
                    var number = Number(names[p], row, selected); var expected = Frames(names[p])[number - 1];
                    var images = Regex.Matches(rows[row].Groups["c"].Value, @"<img\b(?<a>[^>]*)>").Cast<Match>().Where(m => Attr(m.Groups["a"].Value, "data-side") == sides[p] + "-original").ToArray();
                    var id = "tiff-html-" + label + "-" + sides[p] + "-" + row;
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
    private static string FindFixtures()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        { var path = Path.Combine(d.FullName, "tests", "Fixtures", "Images", "Tiff"); if (File.Exists(Path.Combine(path, "expectations.json"))) return path; }
        throw new FileNotFoundException("TIFF fixtures not found");
    }
}
