using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class ImageProjectScenarios
{
    // 失敗先行: settings消失、defaults破壊、null/型/範囲/viewの受理、中央なし選択、
    // 保存状態とCLI override/HTML/包装の不一致、実page範囲外で出力破壊、入力属性変更。
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var folder = Path.Combine(fixtures, "image-project-settings"); var proof = Path.Combine(output, "image-project-settings");
        Directory.CreateDirectory(folder); Directory.CreateDirectory(proof); var source = FindFixtures();
        var manifest = await File.ReadAllBytesAsync(Path.Combine(source, "expectations.json"));
        const string pinned = "0AC09BBC0D62A821484A77A1C8F7F7B4554216E051349F2674F255A401A8D75B";
        check("image-settings-manifest-sha", Sha(manifest) == pinned, Sha(manifest)); if (Sha(manifest) != pinned) throw new InvalidDataException("TIFF oracle changed");
        await File.WriteAllBytesAsync(Path.Combine(proof, "expectations.json"), manifest);
        using var doc = JsonDocument.Parse(manifest); var assets = doc.RootElement.GetProperty("assets");
        foreach (var asset in assets.EnumerateObject())
        {
            check("image-settings-fixture-sha-" + asset.Name, Hash(Path.Combine(source, asset.Name)) == asset.Value.GetProperty("fileSha256").GetString(), asset.Name);
            if (asset.Value.GetProperty("valid").GetBoolean()) foreach (var frame in asset.Value.GetProperty("frames").EnumerateArray())
                check("image-settings-oracle-" + asset.Name + "-" + frame.GetProperty("pixelSha256").GetString(), Sha(Raw(frame)) == frame.GetProperty("pixelSha256").GetString()
                    && Raw(frame).SequenceEqual(frame.GetProperty("bgra").EnumerateArray().SelectMany(p => p.EnumerateArray().Select(v => v.GetByte()))), "literal/base64/SHA");
        }
        var names = new[] { "two-pages-le.tif", "same-first-right.tif" }; var inputs = new Dictionary<string, (string Hash, FileAttributes Attributes)>();
        foreach (var name in names) { var path = Path.Combine(folder, name); File.Copy(Path.Combine(source, name), path); Track(path); }
        var defaults = Settings(); var selected = Settings(); selected["leftFrame"] = 2; selected["rightFrame"] = 2;
        selected["middleFrame"] = 2; selected["reportAllFrames"] = false; selected["showDifferences"] = false;
        selected["threshold"] = 510; selected["zoom"] = 2.5; selected["overlayOpacity"] = .75; selected["highlightAlpha"] = .3; selected["view"] = "Overlay";
        var twoSettings = new Dictionary<string, object?>(selected) { ["middleFrame"] = 1 };
        var two = Project("selected-two", twoSettings); var three = Project("selected-three", selected, true);
        await Copy("selected-two", two, twoSettings); await Copy("selected-three", three, selected);
        var omitted = Project("omitted", null, omit: true); var empty = Project("empty", new Dictionary<string, object?>());
        await Copy("omitted", omitted, defaults, true); await Copy("empty", empty, defaults);
        await Html("omitted", omitted, 0, 0, false, false); await Html("empty", empty, 0, 0, false, false);
        await Html("saved-two", two, 2, 510, false, true); await Html("saved-three", three, 2, 510, true, true);
        var allSaved = new Dictionary<string, object?>(twoSettings) { ["reportAllFrames"] = true };
        await Html("saved-all", Project("saved-all", allSaved), 0, 510, false, true);
        await Html("override-frame", two, 1, 510, false, true, ["--left-frame", "1", "--right-frame", "1"]);
        await Html("override-threshold", two, 2, 0, false, true, ["--threshold", "0"]);
        await Html("override-three", three, 1, 0, true, true, ["--left-frame", "1", "--middle-frame", "1", "--right-frame", "1", "--threshold", "0"]);
        foreach (var view in new[] { "SideBySide", "Overlay", "PixelDifference" })
        { var s = Settings(); s["view"] = view; await Copy("view-" + view, Project("view-" + view, s), s); }
        foreach (var upper in new[] { false, true })
        {
            var s = Settings(); foreach (var key in new[] { "leftFrame", "middleFrame", "rightFrame" }) s[key] = upper ? 1024 : 1;
            s["threshold"] = upper ? 510 : 0; s["zoom"] = upper ? 8 : .1; s["overlayOpacity"] = upper ? 1 : 0; s["highlightAlpha"] = upper ? 1 : 0;
            var path = Project("bounds-" + upper, s, true); await Copy("bounds-" + upper, path, s);
            if (upper) { s["reportAllFrames"] = false; await Reject("actual-pages", Project("actual-pages", s, true), reportsOnly: true); }
        }
        foreach (var (field, value) in new (string, object?)[] {
            ("leftFrame",0),("leftFrame",1025),("leftFrame",1.5),("leftFrame","1"),("leftFrame",null),
            ("middleFrame",0),("middleFrame",1025),("middleFrame",1.5),("rightFrame",0),("rightFrame",1025),("rightFrame","1"),
            ("threshold",-.1),("threshold",510.1),("threshold","NaN"),("threshold",null),
            ("zoom",.09),("zoom",8.01),("zoom","1"),("overlayOpacity",-.1),("overlayOpacity",1.1),
            ("highlightAlpha",-.1),("highlightAlpha",1.1),("highlightAlpha","NaN"),("highlightAlpha",null),("highlightAlpha","0.3"),
            ("view","overlay"),("view","Other"),("view",1),("view",null),
            ("showDifferences",1),("showDifferences",null),("reportAllFrames","false"),("reportAllFrames",null) })
        {
            var s = Settings(); s[field] = value; var label = "invalid-" + field + "-" + JsonSerializer.Serialize(value).Replace('"','_');
            await Reject(label, Project(label, s, true));
        }
        await Reject("null-settings", Project("null-settings", null));
        foreach (var value in new object[] { 1, "bad", new[] { 1 } }) await Reject("settings-type-" + value.GetType().Name, Project("settings-type-" + value.GetType().Name, value));
        var middle = Settings(); middle["middleFrame"] = 2; await Reject("middle-without-base", Project("middle-without-base", middle));
        var overflow = Project("number-overflow", Settings()); await File.WriteAllTextAsync(overflow, (await File.ReadAllTextAsync(overflow)).Replace("\"threshold\":0", "\"threshold\":1e999", StringComparison.Ordinal)); Track(overflow);
        await Reject("number-overflow", overflow);
        // 包装は保存状態で1row、相対pathとsettingsを保持し展開後も同じレポート。
        var zip = Path.Combine(proof, "selected-three.zip"); await run("image-settings-package", 0, true, ["--package-project", three, zip, "--report"]);
        var unpacked = Path.Combine(proof, "unpacked"); ZipFile.ExtractToDirectory(zip, unpacked);
        VerifyHtml("packaged", await File.ReadAllTextAsync(Path.Combine(unpacked, "report.files", "1.html")), 2, 510, true, true);
        var restored = Path.Combine(unpacked, "project.json"); using (var package = JsonDocument.Parse(await File.ReadAllTextAsync(restored)))
        {
            var entry = package.RootElement.GetProperty("entries")[0]; VerifySettings("packaged", entry, selected, false);
            foreach (var key in new[] { "leftPath", "basePath", "rightPath" })
            {
                var relative = entry.GetProperty(key).GetString()!; var local = Path.GetFullPath(Path.Combine(unpacked, relative));
                var inside = !Path.IsPathRooted(relative) && local.StartsWith(Path.GetFullPath(unpacked) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                check("image-settings-package-path-" + key, inside && File.Exists(local) && Hash(local) == inputs[Path.Combine(folder, key == "rightPath" ? names[1] : names[0])].Hash, relative);
            }
        }
        await Copy("restored", restored, selected); await Html("restored", restored, 2, 510, true, true);
        foreach (var input in inputs) check("image-settings-input-preserved-" + Path.GetFileName(input.Key), Hash(input.Key) == input.Value.Hash && File.GetAttributes(input.Key) == input.Value.Attributes, input.Key);
        await File.WriteAllTextAsync(Path.Combine(proof, "input-observations.json"), JsonSerializer.Serialize(inputs.Select(i => new { path = i.Key, before = i.Value.Hash, after = Hash(i.Key), attributesBefore = (int)i.Value.Attributes, attributesAfter = (int)File.GetAttributes(i.Key) }), new JsonSerializerOptions { WriteIndented = true }));

        void Track(string path) => inputs[path] = (Hash(path), File.GetAttributes(path));
        string Project(string label, object? settings, bool triple = false, bool omit = false)
        {
            var entry = new Dictionary<string, object?> { ["leftPath"] = names[0], ["rightPath"] = names[1], ["basePath"] = triple ? names[0] : "", ["mode"] = "Image" };
            if (!omit) entry["imageSettings"] = settings;
            var path = Path.Combine(folder, label + ".json"); File.WriteAllText(path, JsonSerializer.Serialize(new { formatVersion = 1, entries = new[] { entry }, activeEntryIndex = 0 })); Track(path); return path;
        }
        async Task Copy(string label, string project, Dictionary<string, object?> expected, bool allowOmitted = false)
        {
            var destination = Path.Combine(proof, label + "-copy.json"); await run("image-settings-copy-" + label, 0, true, ["--project-copy", project, destination]);
            using var copy = JsonDocument.Parse(await File.ReadAllTextAsync(destination));
            var entry = copy.RootElement.TryGetProperty("entries", out var entries) ? entries[0] : copy.RootElement;
            VerifySettings(label, entry, expected, allowOmitted);
            foreach (var key in new[] { "leftPath", "basePath", "rightPath" })
            {
                var reference = entry.GetProperty(key).GetString(); if (string.IsNullOrWhiteSpace(reference)) continue;
                var local = Path.GetFullPath(Path.Combine(proof, reference)); var wanted = inputs[Path.Combine(folder, key == "rightPath" ? names[1] : names[0])].Hash;
                check("image-settings-copy-" + label + "-path-" + key, File.Exists(local) && Hash(local) == wanted, reference);
            }
            if (label == "selected-two") await Copy("copy-roundtrip", destination, expected);
        }
        void VerifySettings(string label, JsonElement entry, Dictionary<string, object?> expected, bool allowOmitted)
        {
            if (!entry.TryGetProperty("imageSettings", out var settings)) { check("image-settings-" + label + "-settings", allowOmitted, "missing imageSettings"); return; }
            var wanted = JsonSerializer.SerializeToElement(expected);
            foreach (var property in wanted.EnumerateObject()) check("image-settings-" + label + "-" + property.Name, settings.ValueKind == JsonValueKind.Object
                && settings.TryGetProperty(property.Name, out var actual) && JsonElement.DeepEquals(actual, property.Value), settings.GetRawText());
        }
        async Task Reject(string label, string project, bool reportsOnly = false)
        {
            foreach (var command in reportsOnly ? new[] { "--report-project", "--package-project" } : new[] { "--project-copy", "--report-project", "--package-project" })
            {
                var extension = command switch { "--package-project" => ".zip", "--report-project" => ".html", _ => ".json" };
                var destination = Path.Combine(proof, label + "-" + command[2..] + extension); await File.WriteAllTextAsync(destination, "settings sentinel"); var hash = Hash(destination); var attrs = File.GetAttributes(destination);
                var result = await run("image-settings-reject-" + label + "-" + command[2..], 2, false, [command, project, destination, .. (command == "--package-project" ? new[] { "--report" } : Array.Empty<string>())]);
                check("image-settings-reject-" + label + "-" + command[2..], (label != "actual-pages" || result.Stderr.Contains("フレーム", StringComparison.Ordinal)) && string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr) && Hash(destination) == hash && File.GetAttributes(destination) == attrs, result.Stderr);
            }
        }
        async Task Html(string label, string project, int selectedFrame, int threshold, bool triple, bool rawPrimary, string[]? options = null)
        {
            var destination = Path.Combine(proof, label + ".html"); await run("image-settings-html-" + label, 0, true, ["--report-project", project, destination, .. (options ?? [])]);
            VerifyHtml(label, await File.ReadAllTextAsync(destination), selectedFrame, threshold, triple, rawPrimary);
        }
        void VerifyHtml(string label, string html, int selectedFrame, int threshold, bool triple, bool rawPrimary)
        {
            var body = Regex.Match(html, @"<body\b(?<a>[^>]*)>").Groups["a"].Value;
            check("image-settings-html-" + label + "-metadata", Attr(body, "data-mode") == "Image" && Attr(body, "data-frame-mode") == (selectedFrame == 0 ? "all" : "selected") && Attr(body, "data-threshold") == threshold.ToString(CultureInfo.InvariantCulture)
                && Attr(body, "data-different") == (threshold == 0 && selectedFrame != 1 ? "true" : "false"), body);
            var rows = Regex.Matches(html, @"<tr\b(?<a>[^>]*)>(?<c>[\s\S]*?)</tr>").Cast<Match>().Where(r => Attr(r.Groups["a"].Value, "data-left-frame") != "").ToArray();
            check("image-settings-html-" + label + "-rows", rows.Length == (selectedFrame == 0 ? 2 : 1), rows.Length.ToString(CultureInfo.InvariantCulture));
            var sides = triple ? new[] { "left", "middle", "right" } : ["left", "right"];
            for (var row = 0; row < rows.Length; row++) foreach (var side in sides)
            {
                var number = selectedFrame == 0 ? row + 1 : selectedFrame; var expected = assets.GetProperty(side == "right" ? names[1] : names[0]).GetProperty("frames")[number - 1];
                foreach (var original in rawPrimary ? new[] { true, false } : new[] { true })
                {
                    var matches = Regex.Matches(rows[row].Groups["c"].Value, @"<img\b(?<a>[^>]*)>").Cast<Match>().Where(i => Attr(i.Groups["a"].Value, "data-side") == side + (original ? "-original" : "")).ToArray();
                    var id = "image-settings-png-" + label + "-" + row + "-" + side + "-" + original; check(id + "-unique", matches.Length == 1, "PNG count"); if (matches.Length != 1) continue;
                    var attrs = matches[0].Groups["a"].Value; var uri = Attr(attrs, "src"); var png = Convert.FromBase64String(uri.StartsWith("data:image/png;base64,", StringComparison.Ordinal) ? uri[22..] : throw new InvalidDataException("Expected PNG"));
                    var decoded = ImageReportScenarios.DecodePng(png);
                    check(id, decoded.Width == expected.GetProperty("width").GetInt32() && decoded.Height == expected.GetProperty("height").GetInt32() && decoded.Bgra.SequenceEqual(Raw(expected))
                        && Attr(attrs, "data-frame") == number.ToString(CultureInfo.InvariantCulture) && Attr(rows[row].Groups["a"].Value, "data-" + side + "-frame") == number.ToString(CultureInfo.InvariantCulture)
                        && string.Equals(Attr(attrs, "data-pixel-sha256"), expected.GetProperty("pixelSha256").GetString(), StringComparison.OrdinalIgnoreCase), Sha(decoded.Bgra));
                    File.WriteAllBytes(Path.Combine(proof, id + ".png"), png);
                }
            }
        }
    }
    private static Dictionary<string, object?> Settings() => new() { ["leftFrame"] = 1, ["middleFrame"] = 1, ["rightFrame"] = 1, ["threshold"] = 0, ["zoom"] = 1, ["overlayOpacity"] = .3, ["highlightAlpha"] = .7, ["showDifferences"] = true, ["reportAllFrames"] = true, ["view"] = "SideBySide" };
    private static byte[] Raw(JsonElement frame) => Convert.FromBase64String(frame.GetProperty("bgraBase64").GetString()!);
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Hash(string path) => Sha(File.ReadAllBytes(path));
    private static string Attr(string source, string name) => WebUtility.HtmlDecode(Regex.Match(source, "\\b" + Regex.Escape(name) + "=[\"'](?<v>[^\"']*)[\"']").Groups["v"].Value);
    private static string FindFixtures()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }) for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        { var path = Path.Combine(d.FullName, "tests", "Fixtures", "Images", "Tiff"); if (File.Exists(Path.Combine(path, "expectations.json"))) return path; }
        throw new FileNotFoundException("TIFF fixtures not found");
    }
}
