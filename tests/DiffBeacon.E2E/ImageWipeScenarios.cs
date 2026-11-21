using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ImageWipeScenarios
{
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var source = Path.Combine(Environment.CurrentDirectory, "tests", "Fixtures", "ImageWipes", "winimerge-image-wipe-golden.json.gz");
        var compressed = File.ReadAllBytes(source);
        check("wipe fixed golden SHA", Convert.ToHexString(SHA256.HashData(compressed)) == "0EAFB9AB3051F4781A190CE987956A5732BF7FCA1D71BAEED2E42993BC5E2A78", source);
        using var gzip = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var golden = JsonDocument.Parse(gzip);
        var cases = golden.RootElement.GetProperty("cases");
        var script = Path.Combine(fixtures, "wipe-script.json");
        using (var file = File.Create(script))
        using (var writer = new Utf8JsonWriter(file))
        {
            writer.WriteStartObject(); writer.WriteStartArray("cases");
            foreach (var item in cases.EnumerateArray())
            {
                writer.WriteStartObject(); writer.WriteString("name", item.GetProperty("name").GetString()); writer.WriteString("mode", item.GetProperty("mode").GetString());
                writer.WriteStartArray("images");
                foreach (var pixels in item.GetProperty("initialCanvasBgraBytes").EnumerateArray())
                {
                    writer.WriteStartObject(); writer.WriteNumber("width", 3); writer.WriteNumber("height", 4);
                    writer.WriteBase64String("bgraBase64", pixels.EnumerateArray().Select(value => value.GetByte()).ToArray()); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteStartArray("positions");
                foreach (var action in item.GetProperty("actions").EnumerateArray()) writer.WriteNumberValue(action.GetProperty("position").GetInt32());
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        var result = await run("wipe-400-state-kernel", 0, true, ["--image-wipe-script", script]);
        using var actual = JsonDocument.Parse(result.Stdout);
        var index = 0; var states = 0;
        foreach (var item in cases.EnumerateArray())
        {
            var found = actual.RootElement.GetProperty("cases")[index++];
            var action = 0;
            foreach (var expected in item.GetProperty("expected").GetProperty("states").EnumerateArray())
            {
                var state = found.GetProperty("states")[action++]; states++;
                check($"wipe {index} state {action} position", state.GetProperty("position").GetInt32() == expected.GetProperty("position").GetInt32(), found.GetProperty("name").GetString()!);
                for (var pane = 0; pane < expected.GetProperty("processed").GetArrayLength(); pane++)
                    check($"wipe {index} state {action} pane {pane} BGRA", state.GetProperty("processed")[pane].GetProperty("bgraBase64").GetBytesFromBase64()
                        .AsSpan().SequenceEqual(expected.GetProperty("processed")[pane].GetProperty("bgraBase64").GetBytesFromBase64()), "all 4 bytes including hidden RGB");
            }
        }
        check("wipe kernel 40 cases 400 states", index == 40 && states == 400, $"{index}/{states}");
        var overlayPath = Path.Combine(Environment.CurrentDirectory, "tests", "Fixtures", "ImageOverlays", "winimerge-image-overlay-golden.json.gz");
        using (var overlayZip = new GZipStream(File.OpenRead(overlayPath), CompressionMode.Decompress))
        using (var overlays = JsonDocument.Parse(overlayZip))
        {
            var noneCount = 0;
            foreach (var item in overlays.RootElement.GetProperty("cases").EnumerateArray())
            {
                if (item.GetProperty("mode").GetString() != "none") continue;
                var name = item.GetProperty("name").GetString()!; var input = Inputs(item, name);
                var settings = new Dictionary<string, object> { ["showDifferences"] = false, ["blockSize"] = 1 };
                var keys = input.Length == 3 ? new[] { "leftOffset", "middleOffset", "rightOffset" } : new[] { "leftOffset", "rightOffset" };
                for (var i = 0; i < input.Length; i++) settings[keys[i]] = new { x = item.GetProperty("images")[i].GetProperty("offsetX").GetInt32(), y = item.GetProperty("images")[i].GetProperty("offsetY").GetInt32() };
                var projectPath = Path.Combine(fixtures, name + ".json");
                await File.WriteAllTextAsync(projectPath, JsonSerializer.Serialize(new { leftPath = input[0], rightPath = input[^1], basePath = input.Length == 3 ? input[1] : "", mode = "Image", imageSettings = settings }));
                var reportPath = Path.Combine(output, name + ".html");
                await run("wipe-none-overlay-" + name, 0, true, ["--report-project", projectPath, reportPath]);
                VerifyHtml(reportPath, item.GetProperty("expected").GetProperty("processed")); noneCount++;
            }
            check("wipe None overlay six full BGRA", noneCount == 6, "alpha31/xor12 are retained reference only; no None-overlay+wiping original GUI golden");
        }
        var highlightPath = Path.Combine(Environment.CurrentDirectory, "tests", "Fixtures", "ImageHighlight", "winimerge-image-highlight-golden.json");
        using (var highlights = JsonDocument.Parse(File.ReadAllBytes(highlightPath)))
        {
            foreach (var item in highlights.RootElement.GetProperty("cases").EnumerateArray().Where(item => item.GetProperty("selectedDiffIndex").ValueKind == JsonValueKind.Number).Take(4))
            {
                var name = item.GetProperty("name").GetString()!; var input = Inputs(item, "order-" + name);
                foreach (var mode in new[] { "vertical", "horizontal" })
                {
                    var resultOrder = await run("wipe-highlight-order-" + name + "-" + mode, item.GetProperty("expected").GetProperty("differenceCount").GetInt32() == 0 ? 0 : 1, true,
                        new[] { "--image-regions" }.Concat(input).Concat(new[] { "--block-size", item.GetProperty("blockSize").ToString(), "--threshold", item.GetProperty("threshold").ToString(),
                            "--highlight-alpha", item.GetProperty("highlightAlpha").ToString(), "--selected-region", (item.GetProperty("selectedDiffIndex").GetInt32() + 1).ToString(), "--wipe-mode", mode, "--wipe-position", "1" }).ToArray());
                    using var observed = JsonDocument.Parse(resultOrder.Stdout); var expected = item.GetProperty("expected").GetProperty("processed");
                    for (var pane = 0; pane < input.Length; pane++)
                    {
                        var w = expected[pane].GetProperty("width").GetInt32(); var h = expected[pane].GetProperty("height").GetInt32();
                        var pixels = expected[pane].GetProperty("bgraBase64").GetBytesFromBase64(); var other = expected[(pane + 1) % input.Length].GetProperty("bgraBase64").GetBytesFromBase64();
                        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) if (mode == "vertical" ? y >= 1 : x >= 1) other.AsSpan((y * w + x) * 4, 4).CopyTo(pixels.AsSpan((y * w + x) * 4));
                        check("wipe highlight before wipe " + name + mode + pane, observed.RootElement.GetProperty("renderedFrames")[pane].GetProperty("bgraBase64").GetBytesFromBase64().AsSpan().SequenceEqual(pixels), "original highlight golden then independent closed-form wipe");
                    }
                }
            }
        }
        string[] Inputs(JsonElement item, string name)
        {
            var input = new List<string>();
            foreach (var image in item.GetProperty("images").EnumerateArray())
            {
                var path = Path.Combine(fixtures, name + "-" + input.Count + ".png");
                var pixels = image.TryGetProperty("bgraBase64", out var encoded) ? encoded.GetBytesFromBase64() : image.GetProperty("bgraBytes").EnumerateArray().Select(value => value.GetByte()).ToArray();
                ImageHighlightScenarios.WritePng(path, image.GetProperty("width").GetInt32(), image.GetProperty("height").GetInt32(), pixels); input.Add(path);
            }
            return input.ToArray();
        }
        void VerifyHtml(string path, JsonElement expected)
        {
            var tags = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path), "<img[^>]+>").Select(match => match.Value).Where(tag => !tag.Contains("-original") && !tag.Contains("data-side=\"difference\"")).ToArray();
            for (var pane = 0; pane < expected.GetArrayLength(); pane++)
            {
                var png = Convert.FromBase64String(System.Text.RegularExpressions.Regex.Match(tags[pane], "src=\"data:image/png;base64,([^\"]+)").Groups[1].Value);
                var decoded = ImageReportScenarios.DecodePng(png); File.WriteAllBytes(path + "." + pane + ".png", png);
                check("wipe None HTML " + Path.GetFileName(path) + pane, decoded.Bgra.AsSpan().SequenceEqual(expected[pane].GetProperty("bgraBase64").GetBytesFromBase64()), "independent BCL PNG decode");
            }
        }
        foreach (var (suffix, mode, imagesJson, positions) in new[] {
            ("empty", "vertical", "[{\"width\":1,\"height\":1,\"bgraBase64\":\"AQID/w==\"},{\"width\":1,\"height\":1,\"bgraBase64\":\"BAUG/w==\"}]", "[]"),
            ("escaped-overflow", "vertical", "[{\"width\":1,\"height\":1,\"bgraBase64\":\"AQID/w==\"},{\"width\":1,\"height\":1,\"bgraBase64\":\"BAUG/w==\"}]", "[0]"),
            ("invalid-mode-empty", "bad", "[]", "[]"), ("invalid-canvas-empty", "vertical", "[]", "[]") })
        {
            var badScript = Path.Combine(fixtures, "wipe-raw-" + suffix + ".json");
            var name = suffix is "empty" or "escaped-overflow" ? new string('<', 6_000_000) : suffix;
            await File.WriteAllTextAsync(badScript, "{\"cases\":[{\"name\":\"" + name + "\",\"mode\":\"" + mode + "\",\"images\":" + imagesJson + ",\"positions\":" + positions + "}]}");
            var invalid = await run("wipe-raw-reject-" + suffix, 2, false, ["--image-wipe-script", badScript]);
            check("wipe raw no success stdout " + suffix, invalid.Stdout.Length == 0, "input16MiB/output32MiB includes escaped headers and empty action boundary");
            if (suffix == "escaped-overflow") check("wipe raw escaped header shared output limit", invalid.Stderr.Contains("32 MiB"), "actual6MB literal name expands to36MB UTF8 JSON");
        }
        var first = cases[0]; var paths = new List<string>();
        foreach (var image in first.GetProperty("images").EnumerateArray())
        {
            var path = Path.Combine(fixtures, "wipe-input-" + paths.Count + ".png");
            ImageHighlightScenarios.WritePng(path, image.GetProperty("width").GetInt32(), image.GetProperty("height").GetInt32(), image.GetProperty("bgraBytes").EnumerateArray().Select(value => value.GetByte()).ToArray()); paths.Add(path);
        }
        var before = paths.Select(Hash).ToArray();
        foreach (var mode in new[] { "vertical", "horizontal" })
        {
            var command = new[] { "--image-regions" }.Concat(paths).Concat(new[] { "--wipe-mode", mode, "--wipe-position", "1", "--highlight-alpha", "0", "--block-size", "1" }).ToArray();
            var diagnostic = await run("wipe-diagnostic-" + mode, 1, true, command);
            using var parsed = JsonDocument.Parse(diagnostic.Stdout);
            check("wipe diagnostic full BGRA " + mode, parsed.RootElement.GetProperty("renderedFrames").EnumerateArray().All(frame => frame.GetProperty("bgraBase64").GetBytesFromBase64().Length == 48), "actual decoder/region/highlight/wipe");
            await run("wipe-normal-" + mode, 1, true, new[] { "--image" }.Concat(paths).Concat(new[] { "--wipe-mode", mode, "--wipe-position", "1" }).ToArray());
        }
        var project = Path.Combine(fixtures, "wipe-project.json");
        await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new { leftPath = paths[0], rightPath = paths[1], mode = "Image", imageSettings = new { showDifferences = false } }));
        var projectHash = Hash(project); var html = Path.Combine(output, "wipe-report.html");
        await run("wipe-report-project", 0, true, ["--report-project", project, html, "--wipe-mode", "vertical", "--wipe-position", "1"]);
        check("wipe report project unchanged", Hash(project) == projectHash, projectHash);
        var oldHtml = File.ReadAllBytes(html);
        foreach (var options in new[] { new[] { "--wipe-mode", "vertical" }, new[] { "--wipe-position", "1" }, new[] { "--wipe-mode", "bad", "--wipe-position", "1" }, new[] { "--wipe-mode", "vertical", "--wipe-position", "-1" }, new[] { "--wipe-mode", "horizontal", "--wipe-position", "2147483648" } })
        {
            await run("wipe-invalid-" + string.Join('-', options).Replace("--", ""), 2, false, new[] { "--report-project", project, html }.Concat(options).ToArray());
            check("wipe invalid preserves previous HTML " + string.Join(' ', options), File.ReadAllBytes(html).AsSpan().SequenceEqual(oldHtml), html);
        }
        var tiffSource = Path.Combine(Environment.CurrentDirectory, "tests", "Fixtures", "Images", "Tiff", "two-pages-le.tif");
        var tiffHash = Hash(tiffSource);
        var oversizedRegions = await run("wipe-regions-second-pane-output-limit", 2, false,
            ["--image-regions", tiffSource, tiffSource, "--right-offset", "2046,2047", "--wipe-mode", "vertical", "--wipe-position", "0"]);
        check("wipe regions oversized no partial stdout", oversizedRegions.Stdout.Length == 0 && oversizedRegions.Stderr.Contains("32 MiB", StringComparison.Ordinal), "first pane BGRA fits; second pane crosses shared32MiB bound before stdout publication");
        var legacyRegions = await run("wipe-regions-legacy-no-wipe-output", 1, true,
            ["--image-regions", tiffSource, tiffSource, "--right-offset", "2046,2047"]);
        using (var legacy = JsonDocument.Parse(legacyRegions.Stdout))
            check("wipe regions legacy output contract", legacy.RootElement.GetProperty("width").GetInt32() == 2048 && legacy.RootElement.GetProperty("height").GetInt32() == 2048
                && !legacy.RootElement.TryGetProperty("renderedFrames", out _), "no wipe retains preexisting diagnostic payload and exit1");
        check("wipe regions output refusal preserves input", tiffHash == Hash(tiffSource), tiffHash);
        var budgetProject = Path.Combine(fixtures, "wipe-budget-project.json");
        await File.WriteAllTextAsync(budgetProject, JsonSerializer.Serialize(new { leftPath = tiffSource, basePath = tiffSource, rightPath = tiffSource, mode = "Image",
            imageSettings = new { showDifferences = false, reportAllFrames = true, rightOffset = new { x = 3998, y = 3997 } } }));
        await run("wipe-allpage-budget-before-render", 2, false, ["--report-project", budgetProject, html, "--wipe-mode", "vertical", "--wipe-position", "0"]);
        check("wipe budget previous output preserved", File.ReadAllBytes(html).AsSpan().SequenceEqual(oldHtml), "shared256M reserves both pages before HTML/canvas rendering");
        check("wipe inputs unchanged", paths.Select(Hash).SequenceEqual(before), string.Join(',', before));
        await File.WriteAllTextAsync(Path.Combine(output, "wipe-observations.json"), JsonSerializer.Serialize(new { cases = index, states, sourceSha256 = Hash(source), script, inputSha256 = before, html, noneOverlayProductCases = 6, overlayReferenceOnlyCases = 43, noneOverlayWipeGuiGolden = "not collected; independent highlight golden plus closed-form wipe", allPagePositionEvidence = "static caller + product TIFF PNG; original GUI not observed" }, new JsonSerializerOptions { WriteIndented = true }));
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }
}
