using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class ImageHighlightReportScenarios
{
    // 無改変C++原本goldenを通常CLI／HTML／包装経路へ照合する同担当の追加。
    // 先行契約: artifacts/verification/image-highlight-reference/normal-report-e2e-contract.md
    internal static async Task RunCaseAsync(string output, string folder, JsonElement item, string[] inputs,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var name = item.GetProperty("name").GetString()!;
        var expected = item.GetProperty("expected");
        var prefix = "image-highlight-normal-" + name;
        var sides = inputs.Length == 3 ? new[] { "left", "middle", "right" } : ["left", "right"];
        var evidence = Path.Combine(output, "image-highlight-reports", name); Directory.CreateDirectory(evidence);
        var hashes = inputs.Select(Hash).ToArray();
        var attributes = inputs.Select(File.GetAttributes).ToArray();
        var entry = new Dictionary<string, object?> {
            ["leftPath"] = inputs[0], ["rightPath"] = inputs[^1], ["basePath"] = inputs.Length == 3 ? inputs[1] : "",
            ["mode"] = "Image", ["leftDescription"] = "左 & <label>", ["baseDescription"] = "中央 & <label>", ["rightDescription"] = "右 & <label>" };
        var project = Path.Combine(folder, name + "-workspace.json");
        await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new { formatVersion = 1, entries = new[] { entry }, activeEntryIndex = 0 }));
        var projectHash = Hash(project);
        var selection = inputs.Length == 3 ? new[] { "--left-frame", "1", "--middle-frame", "1", "--right-frame", "1" }
            : ["--left-frame", "1", "--right-frame", "1"];
        try
        {
            for (var i = 0; i < inputs.Length; i++) File.SetAttributes(inputs[i], attributes[i] | FileAttributes.ReadOnly);
            await Compare("all", inputs, []);
            await Compare("selected", inputs, selection);
            await Report("all", project, []);
            await Report("selected", project, selection);
            var package = Path.Combine(folder, name + "-reports.zip");
            await run(prefix + "-package", 0, true, ["--package-project", project, package, "--report"]);
            check(prefix + "-package-exists", File.Exists(package), package);
            if (File.Exists(package))
            {
                var extracted = Path.Combine(folder, name + "-unpacked"); Directory.CreateDirectory(extracted);
                ZipFile.ExtractToDirectory(package, extracted);
                var packagedHtml = Path.Combine(extracted, "report.files", "1.html");
                check(prefix + "-packaged-html-exists", File.Exists(packagedHtml), packagedHtml);
                if (File.Exists(packagedHtml)) VerifyHtml("packaged", await File.ReadAllTextAsync(packagedHtml), false);
                var packedProject = Path.Combine(extracted, "project.json");
                using var workspace = JsonDocument.Parse(await File.ReadAllTextAsync(packedProject));
                var packedEntry = workspace.RootElement.TryGetProperty("entries", out var entries) ? entries[0] : workspace.RootElement;
                var keys = inputs.Length == 3 ? new[] { "leftPath", "basePath", "rightPath" } : ["leftPath", "rightPath"];
                var restored = new List<string>();
                for (var pane = 0; pane < keys.Length; pane++)
                {
                    var reference = packedEntry.GetProperty(keys[pane]).GetString()!;
                    var local = Path.GetFullPath(Path.Combine(extracted, reference));
                    var inside = !Path.IsPathRooted(reference) && local.StartsWith(Path.GetFullPath(extracted) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    check(prefix + "-packed-" + keys[pane] + "-relative", inside, reference);
                    if (!inside) continue;
                    check(prefix + "-packed-" + keys[pane] + "-snapshot", File.Exists(local) && Hash(local) == hashes[pane], reference);
                    restored.Add(local);
                }
                await Report("reloaded", packedProject, []);
                if (restored.Count == inputs.Length) await Compare("reloaded", restored.ToArray(), []);
            }
            // 保護境界は代表三者ケースで、中央も含む入力とprojectを出力にして検証。
            if (name.StartsWith("left-only-", StringComparison.Ordinal))
            {
                foreach (var target in inputs.Append(project))
                {
                    await Reject("protected-html-" + Path.GetFileName(target), target, false, []);
                    await Reject("protected-package-" + Path.GetFileName(target), target, true, []);
                }
                var protectedHtml = Path.Combine(folder, name + "-protected.html");
                await File.WriteAllTextAsync(protectedHtml, "protected output\n");
                await Reject("partial-selection", protectedHtml, false, ["--left-frame", "1", "--right-frame", "1"]);
                await Reject("invalid-threshold", protectedHtml, false, ["--threshold", "-1"]);
                var outputAttributes = File.GetAttributes(protectedHtml);
                try
                {
                    File.SetAttributes(protectedHtml, outputAttributes | FileAttributes.ReadOnly);
                    await Reject("readonly-html", protectedHtml, false, []);
                }
                finally { File.SetAttributes(protectedHtml, outputAttributes); }
                var protectedZip = Path.Combine(folder, name + "-protected.zip");
                await File.WriteAllTextAsync(protectedZip, "protected package\n");
                var zipAttributes = File.GetAttributes(protectedZip);
                try
                {
                    File.SetAttributes(protectedZip, zipAttributes | FileAttributes.ReadOnly);
                    await Reject("readonly-package", protectedZip, true, []);
                }
                finally { File.SetAttributes(protectedZip, zipAttributes); }
            }
            check(prefix + "-readonly-input-attributes", inputs.All(path => File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)), string.Join(',', inputs));
            check(prefix + "-input-bytes", inputs.Select(Hash).SequenceEqual(hashes), string.Join(',', hashes));
            check(prefix + "-project-bytes", Hash(project) == projectHash, projectHash);
        }
        finally { for (var i = 0; i < inputs.Length; i++) File.SetAttributes(inputs[i], attributes[i]); }

        async Task Compare(string mode, string[] files, string[] options)
        {
            var result = await run(prefix + "-cli-" + mode, expected.GetProperty("differenceCount").GetInt32() == 0 ? 0 : 1, true,
                ["--image", .. files, .. options]);
            using var parsed = JsonDocument.Parse(result.Stdout); var actual = parsed.RootElement;
            var frames = actual.GetProperty("frames");
            check(prefix + "-cli-" + mode + "-frame-count", frames.GetArrayLength() == 1, frames.GetRawText());
            check(prefix + "-cli-" + mode + "-mode", actual.GetProperty("mode").GetString() == (options.Length == 0 ? "all" : "selected")
                && actual.GetProperty("different").GetBoolean() == (expected.GetProperty("differenceCount").GetInt32() != 0)
                && actual.GetProperty("threshold").GetDouble() == item.GetProperty("threshold").GetDouble(), actual.GetRawText());
            check(prefix + "-cli-" + mode + "-source-frame-counts", actual.GetProperty("leftFrames").GetInt32() == 1
                && actual.GetProperty("rightFrames").GetInt32() == 1
                && (inputs.Length == 3 ? actual.TryGetProperty("middleFrames", out var middleCount) && middleCount.GetInt32() == 1
                    : !actual.TryGetProperty("middleFrames", out _)), actual.GetRawText());
            if (frames.GetArrayLength() == 0) return;
            var frame = frames[0];
            foreach (var field in new[] { "regions", "differenceCount", "conflictCount" })
                check(prefix + "-cli-" + mode + "-" + field, JsonElement.DeepEquals(frame.GetProperty(field), expected.GetProperty(field)), frame.GetProperty(field).GetRawText());
            var highlights = frame.GetProperty("highlightPixelSha256");
            check(prefix + "-cli-" + mode + "-highlight-count", highlights.GetArrayLength() == inputs.Length, highlights.GetRawText());
            for (var pane = 0; pane < inputs.Length; pane++)
            {
                var sourceImage = item.GetProperty("images")[pane];
                check(prefix + "-cli-" + mode + "-" + sides[pane] + "-source", frame.GetProperty(sides[pane] + "Frame").GetInt32() == 1
                    && frame.GetProperty(sides[pane] + "Width").GetInt32() == sourceImage.GetProperty("width").GetInt32()
                    && frame.GetProperty(sides[pane] + "Height").GetInt32() == sourceImage.GetProperty("height").GetInt32(), frame.GetRawText());
                var sourceBgra = Convert.FromBase64String(sourceImage.GetProperty("bgraBase64").GetString()!);
                check(prefix + "-cli-" + mode + "-" + sides[pane] + "-source-sha", string.Equals(frame.GetProperty(sides[pane] + "PixelSha256").GetString(),
                    Convert.ToHexString(SHA256.HashData(sourceBgra)), StringComparison.OrdinalIgnoreCase), frame.GetProperty(sides[pane] + "PixelSha256").GetString()!);
                if (pane < highlights.GetArrayLength()) check(prefix + "-cli-" + mode + "-" + sides[pane] + "-highlight-sha",
                    string.Equals(highlights[pane].GetString(), expected.GetProperty("processed")[pane].GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), highlights[pane].GetString()!);
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, mode + "-cli-observation.json"), JsonSerializer.Serialize(new { inputs, inputSha256 = hashes, expected = expected.Clone(), actual = actual.Clone() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        async Task Report(string mode, string sourceProject, string[] options)
        {
            var destination = Path.Combine(evidence, mode + ".html");
            await run(prefix + "-report-" + mode, 0, true, ["--report-project", sourceProject, destination, .. options]);
            check(prefix + "-report-" + mode + "-exists", File.Exists(destination), destination);
            if (File.Exists(destination)) VerifyHtml(mode, await File.ReadAllTextAsync(destination), options.Length > 0);
        }
        async Task Reject(string label, string destination, bool package, string[] options)
        {
            var before = Hash(destination); var priorAttributes = File.GetAttributes(destination);
            var result = await run(prefix + "-reject-" + label, 2, false,
                [package ? "--package-project" : "--report-project", project, destination, .. (package ? new[] { "--report" } : Array.Empty<string>()), .. options]);
            check(prefix + "-reject-" + label + "-protected", Hash(destination) == before && File.GetAttributes(destination) == priorAttributes, destination);
            check(prefix + "-reject-" + label + "-no-success-json", string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        }
        void VerifyHtml(string mode, string html, bool selected)
        {
            var label = prefix + "-html-" + mode;
            var body = Regex.Match(html, @"<body\b(?<attrs>[^>]*)>", RegexOptions.CultureInvariant).Groups["attrs"].Value;
            check(label + "-body", Attribute(body, "data-mode") == "Image" && Attribute(body, "data-frame-mode") == (selected ? "selected" : "all")
                && Attribute(body, "data-different") == (expected.GetProperty("differenceCount").GetInt32() == 0 ? "false" : "true")
                && Attribute(body, "data-left-frames") == "1" && Attribute(body, "data-right-frames") == "1"
                && (inputs.Length == 2 || Attribute(body, "data-middle-frames") == "1") && Attribute(body, "data-threshold") == "0", body);
            check(label + "-standalone", html.Contains("<!doctype html>", StringComparison.OrdinalIgnoreCase)
                && !Regex.IsMatch(html, @"\b(?:src|href)\s*=\s*[""'](?!data:image/png;base64,|#)", RegexOptions.CultureInvariant), mode);
            var rows = Regex.Matches(html, @"<tr\b(?<attrs>[^>]*)>(?<content>[\s\S]*?)</tr>", RegexOptions.CultureInvariant)
                .Cast<Match>().Where(row => Attribute(row.Groups["attrs"].Value, "data-left-frame") != "").ToArray();
            check(label + "-rows", rows.Length == 1, rows.Length.ToString(CultureInfo.InvariantCulture));
            if (rows.Length == 0) return;
            var rowAttrs = rows[0].Groups["attrs"].Value;
            check(label + "-counts", Attribute(rowAttrs, "data-difference-count") == expected.GetProperty("differenceCount").GetInt32().ToString(CultureInfo.InvariantCulture)
                && Attribute(rowAttrs, "data-conflict-count") == expected.GetProperty("conflictCount").GetInt32().ToString(CultureInfo.InvariantCulture), rowAttrs);
            var images = Regex.Matches(rows[0].Groups["content"].Value, @"<img\b(?<attrs>[^>]*)>", RegexOptions.CultureInvariant).Cast<Match>().ToArray();
            check(label + "-all-images", images.Length == inputs.Length * 2 + 1, images.Length.ToString(CultureInfo.InvariantCulture));
            var regionItems = Regex.Matches(rows[0].Groups["content"].Value, @"<li\b(?<attrs>[^>]*)>", RegexOptions.CultureInvariant).Cast<Match>().ToArray();
            var regions = expected.GetProperty("regions");
            check(label + "-region-count", regionItems.Length == regions.GetArrayLength(), regionItems.Length.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < Math.Min(regionItems.Length, regions.GetArrayLength()); i++)
                check(label + "-region-" + i, Attribute(regionItems[i].Groups["attrs"].Value, "data-region") == regions[i].GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture)
                    && Attribute(regionItems[i].Groups["attrs"].Value, "data-op") == regions[i].GetProperty("op").GetInt32().ToString(CultureInfo.InvariantCulture), regionItems[i].Value);
            for (var pane = 0; pane < sides.Length; pane++)
            {
                check(label + "-" + sides[pane] + "-row-frame", Attribute(rowAttrs, "data-" + sides[pane] + "-frame") == "1", rowAttrs);
                foreach (var original in new[] { false, true })
                {
                    var side = sides[pane] + (original ? "-original" : "");
                    var matches = images.Where(image => Attribute(image.Groups["attrs"].Value, "data-side") == side).ToArray();
                    check(label + "-" + side + "-unique", matches.Length == 1, matches.Length.ToString(CultureInfo.InvariantCulture));
                    if (matches.Length != 1) continue;
                    var attrs = matches[0].Groups["attrs"].Value; var uri = Attribute(attrs, "src");
                    var oracle = original ? item.GetProperty("images")[pane] : expected.GetProperty("processed")[pane];
                    var raw = Convert.FromBase64String(oracle.GetProperty("bgraBase64").GetString()!);
                    var png = Convert.FromBase64String(uri.StartsWith("data:image/png;base64,", StringComparison.Ordinal) ? uri[22..] : throw new InvalidDataException("Expected embedded PNG"));
                    var decoded = ImageReportScenarios.DecodePng(png);
                    check(label + "-" + side + "-independent-bgra", decoded.Width == oracle.GetProperty("width").GetInt32() && decoded.Height == oracle.GetProperty("height").GetInt32()
                        && decoded.Bgra.SequenceEqual(raw), Convert.ToHexString(SHA256.HashData(decoded.Bgra)));
                    check(label + "-" + side + "-metadata", Attribute(attrs, "data-width") == oracle.GetProperty("width").GetInt32().ToString(CultureInfo.InvariantCulture)
                        && Attribute(attrs, "data-height") == oracle.GetProperty("height").GetInt32().ToString(CultureInfo.InvariantCulture)
                        && Attribute(attrs, "data-frame") == "1" && string.Equals(Attribute(attrs, "data-pixel-sha256"), Convert.ToHexString(SHA256.HashData(raw)), StringComparison.OrdinalIgnoreCase), attrs);
                    File.WriteAllBytes(Path.Combine(evidence, mode + "-" + side + ".png"), png);
                    File.WriteAllBytes(Path.Combine(evidence, mode + "-" + side + ".bgra"), decoded.Bgra);
                }
            }
        }
    }
    private static string Attribute(string source, string name) => WebUtility.HtmlDecode(Regex.Match(source,
        @"\b" + Regex.Escape(name) + "=[\"'](?<value>[^\"']*)[\"']", RegexOptions.CultureInvariant).Groups["value"].Value);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
