using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class ImageOverlayReportScenarios
{
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var folder = Path.Combine(fixtures, "overlay-reports"); Directory.CreateDirectory(folder);
        var evidence = Path.Combine(output, "overlay-report-pixels"); Directory.CreateDirectory(evidence);
        var source = Path.Combine("tests", "Fixtures", "ImageOverlays", "winimerge-image-overlay-golden.json.gz");
        var sourceHash = Hash(source);
        using var stream = new GZipStream(File.OpenRead(source), CompressionMode.Decompress);
        using var golden = JsonDocument.Parse(stream);
        string[]? firstInputs = null; string? firstProject = null; var reportCases = 0; var regionCases = 0; var imageCases = 0;
        foreach (var item in golden.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!; var images = item.GetProperty("images");
            var inputs = images.EnumerateArray().Select((image, side) =>
            {
                var path = Path.Combine(folder, name + "-" + side + ".png");
                ImageHighlightScenarios.WritePng(path, image.GetProperty("width").GetInt32(), image.GetProperty("height").GetInt32(),
                    image.GetProperty("bgraBytes").EnumerateArray().Select(value => value.GetByte()).ToArray()); return path;
            }).ToArray();
            var hashes = inputs.Select(Hash).ToArray();
            var mode = item.GetProperty("mode").GetString()!; var alpha = item.GetProperty("overlayAlpha").GetDouble();
            var show = item.GetProperty("showDifferences").GetBoolean(); var selected = item.GetProperty("selectedDiffIndex").GetInt32();
            var options = new List<string> { "--overlay-mode", mode, "--overlay-alpha", alpha.ToString("R", CultureInfo.InvariantCulture),
                "--block-size", item.GetProperty("blockSize").GetRawText(), "--threshold", item.GetProperty("threshold").GetRawText(),
                "--highlight-alpha", item.GetProperty("highlightAlpha").GetRawText() };
            for (var side = 0; side < inputs.Length; side++) options.AddRange([side == 0 ? "--left-offset" : side == inputs.Length - 1 ? "--right-offset" : "--middle-offset",
                images[side].GetProperty("offsetX").GetRawText() + "," + images[side].GetProperty("offsetY").GetRawText()]);
            var wipe = item.GetProperty("wipeMode").GetInt32();
            if (wipe != 0) options.AddRange(["--wipe-mode", wipe == 1 ? "vertical" : "horizontal", "--wipe-position", item.GetProperty("wipePosition").GetRawText()]);
            var expected = item.GetProperty("expected").GetProperty("processed");
            var regions = item.GetProperty("expected").GetProperty("classificationBefore").GetProperty("regions").GetArrayLength();
            if (show)
            {
                var result = await run("overlay-regions-" + name, regions == 0 ? 0 : 1, true,
                    ["--image-regions", .. inputs, .. options, "--selected-region", (selected + 1).ToString(CultureInfo.InvariantCulture)]);
                using var json = JsonDocument.Parse(result.Stdout); var rendered = json.RootElement.GetProperty("renderedFrames");
                for (var side = 0; side < inputs.Length; side++)
                {
                    var actual = rendered[side]; var pixels = actual.GetProperty("bgraBase64").GetBytesFromBase64();
                    check("overlay normal regions original BGRA " + name + ":" + side, pixels.AsSpan().SequenceEqual(expected[side].GetProperty("bgraBase64").GetBytesFromBase64())
                        && actual.GetProperty("width").GetInt32() == expected[side].GetProperty("width").GetInt32()
                        && actual.GetProperty("height").GetInt32() == expected[side].GetProperty("height").GetInt32(), "fixed original static golden; expected absent from product input");
                }
                regionCases++;
                if (selected == -1 && wipe == 0)
                {
                    var compared = await run("overlay-image-" + name, regions == 0 ? 0 : 1, true, ["--image", .. inputs, .. options]);
                    using var normal = JsonDocument.Parse(compared.Stdout);
                    check("overlay normal image raw/hash " + name, normal.RootElement.GetProperty("frames")[0].GetProperty("highlightPixelSha256").EnumerateArray()
                        .Select(value => value.GetString()).SequenceEqual(expected.EnumerateArray().Select(value => value.GetProperty("sha256").GetString()), StringComparer.OrdinalIgnoreCase),
                        "raw classification/exit unchanged; display hashes original expected"); imageCases++;
                }
            }
            var settings = new Dictionary<string, object?> { ["showDifferences"] = show, ["reportAllFrames"] = false,
                ["blockSize"] = item.GetProperty("blockSize").GetInt32(), ["threshold"] = item.GetProperty("threshold").GetDouble(),
                ["highlightAlpha"] = item.GetProperty("highlightAlpha").GetDouble(), ["overlayOpacity"] = alpha };
            for (var side = 0; side < inputs.Length; side++) settings[side == 0 ? "leftOffset" : side == inputs.Length - 1 ? "rightOffset" : "middleOffset"]
                = new { x = images[side].GetProperty("offsetX").GetInt32(), y = images[side].GetProperty("offsetY").GetInt32() };
            var project = Path.Combine(folder, name + ".json");
            await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new { formatVersion = 1, activeEntryIndex = 0,
                entries = new[] { new { mode = "Image", leftPath = Path.GetFullPath(inputs[0]), basePath = inputs.Length == 3 ? Path.GetFullPath(inputs[1]) : "",
                    rightPath = Path.GetFullPath(inputs[^1]), imageSettings = settings } } }));
            var projectHash = Hash(project);
            if (selected == -1)
            {
                var path = Path.Combine(evidence, name + ".html");
                await run("overlay-report-" + name, 0, true, ["--report-project", project, path, "--overlay-mode", mode]);
                var html = await File.ReadAllTextAsync(path); VerifyHtml(name, html, expected, images);
                check("overlay project alpha precedence " + name, html.Contains("data-overlay-alpha=\"" + alpha.ToString("R", CultureInfo.InvariantCulture) + "\"", StringComparison.Ordinal),
                    "omitted CLI alpha uses existing project OverlayOpacity"); reportCases++;
            }
            check("overlay normal inputs/project preserved " + name, Hash(project) == projectHash && inputs.Select(Hash).SequenceEqual(hashes), "no temporal settings persisted");
            firstInputs ??= inputs; firstProject ??= project;
        }
        check("overlay normal boundary case counts", reportCases == 30 && regionCases == 25 && imageCases == 6,
            $"reports{reportCases}/regions{regionCases}/images{imageCases}; original49 cases overlap across eligible routes");
        var baseline = await run("overlay-normal-default", 1, true, ["--image", .. firstInputs!]);
        using (var json = JsonDocument.Parse(baseline.Stdout)) check("overlay default JSON contract", !json.RootElement.TryGetProperty("overlayMode", out _), "None&&!blink default; no new metadata");
        var specifiedNone = await run("overlay-normal-explicit-none", 1, true, ["--image", .. firstInputs!, "--overlay-mode", "none"]);
        var noneNode = JsonNode.Parse(specifiedNone.Stdout)!.AsObject();
        foreach (var key in new[] { "overlayMode", "overlayAlpha", "overlayBlink", "overlayPeriod", "blinkPeriod" }) noneNode.Remove(key);
        check("overlay no-flag unchanged complete payload", JsonNode.DeepEquals(JsonNode.Parse(baseline.Stdout), noneNode), "all old fields/raw SHA/classification/display hashes/exit equal explicitNone after new metadata removal");
        var inherited = JsonNode.Parse(File.ReadAllText(firstProject!))!.AsObject();
        inherited["entries"]![0]!["imageSettings"]!.AsObject().Remove("overlayOpacity");
        var inheritedPath = Path.Combine(folder, "inherited-alpha.json"); await File.WriteAllTextAsync(inheritedPath, inherited.ToJsonString()); var inheritedHash = Hash(inheritedPath);
        var inheritedHtml = Path.Combine(evidence, "inherited-alpha.html");
        await run("overlay-project-omitted-alpha", 0, true, ["--report-project", inheritedPath, inheritedHtml, "--overlay-mode", "alpha"]);
        check("overlay project omitted alpha default", File.ReadAllText(inheritedHtml).Contains("data-overlay-alpha=\"0.3\"", StringComparison.Ordinal)
            && Hash(inheritedPath) == inheritedHash, "normalCLI noAppData: omitted project alpha .3, JSON unchanged");
        var overridePath = Path.Combine(evidence, "explicit-alpha.html");
        await run("overlay-explicit-alpha", 0, true, ["--report-project", firstProject!, overridePath, "--overlay-mode", "alpha", "--overlay-alpha", "0.65"]);
        check("overlay CLI alpha overrides project", File.ReadAllText(overridePath).Contains("data-overlay-alpha=\"0.65\"", StringComparison.Ordinal), "CLI explicit > project >.3");
        var invalid = new[] { new[] { "--overlay-mode", "4" }, ["--overlay-mode", "Alpha"], ["--overlay-alpha", "NaN"], ["--overlay-alpha", "1.01"],
            ["--overlay-alpha", "-0.1"], ["--overlay-period", "199"], ["--overlay-period", "8001"], ["--overlay-period", "200.5"],
            ["--blink-period", "0"], ["--overlay-blink", "1"], ["--overlay-mode", "none", "--overlay-mode", "alpha"],
            ["--overlay-alpha", "0.3", "--overlay-alpha", "0.3"], ["--overlay-opacity", "0.3"], ["--overlay-mode"] };
        for (var i = 0; i < invalid.Length; i++) foreach (var command in new[] { "--image", "--image-regions", "--report-project" })
        {
            var protectedHtml = Path.Combine(evidence, "protected.html"); await File.WriteAllTextAsync(protectedHtml, "protected HTML");
            var result = await run("overlay-invalid-" + i + "-" + command[2..], 2, false,
                command == "--report-project" ? [command, firstProject!, protectedHtml, .. invalid[i]] : [command, .. firstInputs!, .. invalid[i]]);
            check("overlay invalid output protection " + i + ":" + command, result.Stdout.Length == 0 && File.ReadAllText(protectedHtml) == "protected HTML", "common parser late/missing/duplicate rejection, stdout empty");
        }
        foreach (var mode in new[] { "none", "alpha", "anim" })
        {
            var tiff = Path.Combine("tests", "Fixtures", "Images", "Tiff", "two-pages-le.tif"); var before = Hash(tiff);
            var result = await run("overlay-regions-32MiB-" + mode, 2, false, ["--image-regions", tiff, tiff, "--right-offset", "2046,2047", "--overlay-mode", mode]);
            check("overlay complete32MiB stdout empty " + mode, result.Stdout.Length == 0 && result.Stderr.Contains("32 MiB", StringComparison.Ordinal) && Hash(tiff) == before,
                "legal grid/canvas/draw work; late fullBGRA serialization rejects atomically");
        }
        foreach (var mode in new[] { "anim", "none" })
        {
            var dynamic = await run("overlay-normal-temporal-" + mode, 1, true, ["--image-regions", .. firstInputs!, "--overlay-mode", mode, "--overlay-blink", "true",
                "--overlay-period", "200", "--blink-period", "8000"]);
            using var json = JsonDocument.Parse(dynamic.Stdout);
            foreach (var frame in json.RootElement.GetProperty("renderedFrames").EnumerateArray())
                check("overlay normal temporal fullBGRA digest " + mode, Convert.ToHexString(SHA256.HashData(frame.GetProperty("bgraBase64").GetBytesFromBase64()))
                    .Equals(frame.GetProperty("pixelSha256").GetString(), StringComparison.OrdinalIgnoreCase), "realUTC sampled individually; exact temporal golden remains script/internal-clock boundary");
        }
        check("overlay fixed static source retained", Hash(source) == sourceHash, sourceHash);

        void VerifyHtml(string name, string html, JsonElement expected, JsonElement raw)
        {
            for (var side = 0; side < raw.GetArrayLength(); side++) foreach (var original in new[] { false, true })
            {
                var sideName = side == 0 ? "left" : side == raw.GetArrayLength() - 1 ? "right" : "middle";
                if (original) sideName += "-original";
                var tag = Regex.Matches(html, "<img[^>]+>").Select(match => match.Value).Single(tag => tag.Contains("data-side=\"" + sideName + "\"", StringComparison.Ordinal));
                var png = Convert.FromBase64String(Regex.Match(tag, "src=\"data:image/png;base64,([^\"]+)").Groups[1].Value);
                var actual = ImageReportScenarios.DecodePng(png); var frame = original ? raw[side] : expected[side];
                var pixels = original ? frame.GetProperty("bgraBytes").EnumerateArray().Select(value => value.GetByte()).ToArray() : frame.GetProperty("bgraBase64").GetBytesFromBase64();
                check("overlay normal HTML original BGRA " + name + ":" + sideName, actual.Width == frame.GetProperty("width").GetInt32()
                    && actual.Height == frame.GetProperty("height").GetInt32() && actual.Bgra.AsSpan().SequenceEqual(pixels), "independent PNG decoder, fixed original static golden/raw");
                File.WriteAllBytes(Path.Combine(evidence, name + "-" + sideName + ".png"), png);
            }
        }
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
