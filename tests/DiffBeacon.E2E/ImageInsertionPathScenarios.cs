using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class ImageInsertionPathScenarios
{
    // 失敗先行: modeの保存・上書き消失、整列画素/領域数の不一致、ghostの原画混入、
    // 包装の相対参照破壊、null/型/範囲/重複の受理、入力と既存出力の破壊。
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var root = FixtureRepository.FindRoot();
        if (root is null) throw new DirectoryNotFoundException("ImageInsertions fixture root");
        var packed = await File.ReadAllBytesAsync(Path.Combine(root.FullName, "tests", "Fixtures", "ImageInsertions", "winimerge-insertions-golden.json.gz"));
        const string gzipSha = "2B0275994E7445F8BF4A745CF7DCE8BBF531C647D43095751F12AE6278A7AF21";
        const string sourceSha = "991FC3F5CBAA2B039D6320E85AB9ADE0F6BB2CE186FD5AFFC30BA43D4777EDDB";
        check("image-insertion-path-gzip-sha", Sha(packed) == gzipSha, Sha(packed));
        if (Sha(packed) != gzipSha) throw new InvalidDataException("Insertion golden changed");
        using var stream = new MemoryStream(packed); using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var decompressed = new MemoryStream(); await gzip.CopyToAsync(decompressed);
        var source = decompressed.ToArray(); check("image-insertion-path-source-sha", Sha(source) == sourceSha, Sha(source));
        if (Sha(source) != sourceSha) throw new InvalidDataException("Insertion observations changed");
        using var golden = JsonDocument.Parse(source);
        var folder = Path.Combine(fixtures, "image-insertion-paths"); var proof = Path.Combine(output, "image-insertion-paths");
        Directory.CreateDirectory(folder); Directory.CreateDirectory(proof);
        await File.WriteAllTextAsync(Path.Combine(proof, "failure-contract.md"),
            "拒否: 未知/重複/欠落CLI mode、JSON null/string/fraction/negative/out-of-range/bool。\n保持: PNG入力のSHA/属性、保存プロジェクト、失敗時の既存出力。\n一致: 原本DLLの整列全画素SHA・canvas寸法・領域/競合数、設定往復、HTML原画全画素、強調なし整列全画素、通常強調のCLI/HTML一致、包装展開再読込。\n");
        var tracked = new Dictionary<string, (string Hash, FileAttributes Attributes)>();
        var observations = new List<object>(); string[] sides = ["left", "middle", "right"];
        string[] names = ["1-insert-middle-copy0to1", "2-insert-middle-copy0to1", "1-three-conflict", "2-three-independent",
            "threshold-0.5", "threshold-2", "transparent-rgb", "1-equal-copy0to1"];
        string[]? firstPaths = null; string? firstProject = null;
        foreach (var name in names)
        {
            var item = golden.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
            var mode = item.GetProperty("actions")[0][3].GetInt32(); var state = item.GetProperty("states")[1];
            var inputs = item.GetProperty("inputs"); var panes = state.GetProperty("panes"); var count = inputs.GetArrayLength();
            var paths = new string[count];
            for (var pane = 0; pane < count; pane++)
            {
                paths[pane] = Path.Combine(folder, name + "-" + pane + ".png");
                await File.WriteAllBytesAsync(paths[pane], Convert.FromBase64String(inputs[pane].GetProperty("pngBase64").GetString()!));
                check(Label(name + "-input-oracle-" + pane), Hash(paths[pane]) == inputs[pane].GetProperty("pngSha256").GetString(), "pinned original PNG"); Track(paths[pane]);
            }
            var options = new[] { "--block-size", item.GetProperty("blockSize").GetInt32().ToString(CultureInfo.InvariantCulture),
                "--threshold", item.GetProperty("threshold").GetDouble().ToString("R", CultureInfo.InvariantCulture), "--insertion-deletion-mode", Mode(mode) };
            var direct = await run(Label(name + "-direct"), state.GetProperty("differenceCount").GetInt32() == 0 ? 0 : 1, true, ["--image", .. paths, .. options]);
            using var actual = JsonDocument.Parse(direct.Stdout); var frame = actual.RootElement.GetProperty("frames")[0];
            check(Label(name + "-json-mode"), actual.RootElement.GetProperty("insertionDeletionMode").GetInt32() == mode, actual.RootElement.GetRawText());
            foreach (var property in new[] { "differenceCount", "conflictCount" })
                check(Label(name + "-" + property), frame.GetProperty(property).GetInt32() == state.GetProperty(property).GetInt32(), "original initial mode state");
            for (var pane = 0; pane < count; pane++)
            {
                var side = sides[count == 2 && pane == 1 ? 2 : pane]; var expected = panes[pane];
                check(Label(name + "-aligned-" + side), frame.GetProperty(side + "Width").GetInt32() == expected.GetProperty("canvasWidth").GetInt32()
                    && frame.GetProperty(side + "Height").GetInt32() == expected.GetProperty("canvasHeight").GetInt32()
                    && string.Equals(frame.GetProperty(side + "PixelSha256").GetString(), expected.GetProperty("bgraSha256").GetString(), StringComparison.OrdinalIgnoreCase), "original aligned dimensions and all BGRA SHA");
            }
            var settings = new Dictionary<string, object?> { ["insertionDeletionMode"] = mode, ["blockSize"] = item.GetProperty("blockSize").GetInt32(),
                ["threshold"] = item.GetProperty("threshold").GetDouble(), ["showDifferences"] = false };
            var project = Project(name, paths, settings); var copy = Path.Combine(proof, name + "-copy.json");
            await run(Label(name + "-copy"), 0, true, ["--project-copy", project, copy]); VerifySettings(name + "-copy", copy, mode, paths); Track(copy);
            var again = Path.Combine(proof, name + "-again.json"); await run(Label(name + "-roundtrip"), 0, true, ["--project-copy", copy, again]);
            VerifySettings(name + "-roundtrip", again, mode, paths); Track(again);
            var report = Path.Combine(proof, name + ".html"); await run(Label(name + "-report"), 0, true, ["--report-project", again, report]);
            VerifyHtml(name + "-report", report, inputs, panes, mode, state, null);
            settings["showDifferences"] = true; var highlighted = Project(name + "-highlight", paths, settings);
            var highlightReport = Path.Combine(proof, name + "-highlight.html"); await run(Label(name + "-highlight"), 0, true, ["--report-project", highlighted, highlightReport]);
            VerifyHtml(name + "-highlight", highlightReport, inputs, panes, mode, state, frame.GetProperty("highlightPixelSha256"));
            var overridden = Path.Combine(proof, name + "-none.html"); await run(Label(name + "-override"), 0, true,
                ["--report-project", again, overridden, "--insertion-deletion-mode", "none"]);
            VerifyHtml(name + "-override", overridden, inputs, panes, 0, null, null);
            var zip = Path.Combine(proof, name + ".zip"); await run(Label(name + "-package"), 0, true, ["--package-project", copy, zip, "--report"]);
            var unpacked = Path.Combine(proof, name + "-unpacked"); ZipFile.ExtractToDirectory(zip, unpacked);
            var restored = Path.Combine(unpacked, "project.json"); VerifySettings(name + "-packaged", restored, mode, paths, unpacked);
            VerifyHtml(name + "-packaged", Path.Combine(unpacked, "report.files", "1.html"), inputs, panes, mode, state, null);
            var rereport = Path.Combine(proof, name + "-restored.html"); await run(Label(name + "-restored"), 0, true, ["--report-project", restored, rereport]);
            VerifyHtml(name + "-restored", rereport, inputs, panes, mode, state, null);
            observations.Add(new { name, mode, panes = count, differenceCount = state.GetProperty("differenceCount").GetInt32(), conflictCount = state.GetProperty("conflictCount").GetInt32() });
            firstPaths ??= paths; firstProject ??= project;
        }
        foreach (var (label, options) in new[] { ("bad", new[] { "--insertion-deletion-mode", "diagonal" }),
            ("numeric", new[] { "--insertion-deletion-mode", "1" }), ("missing", new[] { "--insertion-deletion-mode" }),
            ("duplicate", new[] { "--insertion-deletion-mode", "vertical", "--insertion-deletion-mode", "horizontal" }) })
        {
            var result = await run(Label("reject-cli-" + label), 2, false, ["--image", .. firstPaths!, .. options]);
            check(Label("reject-cli-output-" + label), string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            await Reject("cli-" + label, firstProject!, ["--report-project"], options);
        }
        foreach (var (label, value) in new (string, object?)[] { ("null", null), ("string", "1"), ("fraction", 1.5), ("negative", -1), ("range", 3), ("bool", true) })
            await Reject("json-" + label, Project("invalid-" + label, firstPaths!, new Dictionary<string, object?> { ["insertionDeletionMode"] = value }),
                ["--project-copy", "--report-project", "--package-project"], []);
        foreach (var (label, settings) in new[] { ("omitted", new Dictionary<string, object?>()), ("zero", new Dictionary<string, object?> { ["insertionDeletionMode"] = 0 }) })
        {
            var project = Project("default-" + label, firstPaths!, settings); var copy = Path.Combine(proof, "default-" + label + ".json");
            await run(Label("default-" + label), 0, true, ["--project-copy", project, copy]); VerifySettings("default-" + label, copy, 0, firstPaths!);
        }
        var equal = golden.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == names[^1]);
        var equalPaths = Enumerable.Range(0, 2).Select(i => Path.Combine(folder, names[^1] + "-" + i + ".png")).ToArray();
        foreach (var explicitNone in new[] { false, true })
        {
            var result = await run(Label("default-cli-" + explicitNone), 0, true, ["--image", .. equalPaths, .. (explicitNone ? new[] { "--insertion-deletion-mode", "none" } : [])]);
            using var parsed = JsonDocument.Parse(result.Stdout); check(Label("default-json-omitted-" + explicitNone), !parsed.RootElement.TryGetProperty("insertionDeletionMode", out _), "legacy default JSON shape");
            var frame = parsed.RootElement.GetProperty("frames")[0];
            for (var pane = 0; pane < 2; pane++) check(Label("default-raw-" + explicitNone + "-" + pane),
                string.Equals(frame.GetProperty((pane == 0 ? "left" : "right") + "PixelSha256").GetString(),
                    Convert.ToHexString(SHA256.HashData(Convert.FromHexString(equal.GetProperty("inputs")[pane].GetProperty("bgraHex").GetString()!))),
                    StringComparison.OrdinalIgnoreCase), "default raw pixel SHA from original literal BGRA");
        }
        foreach (var input in tracked) check(Label("preserved-" + Path.GetFileName(input.Key)), Hash(input.Key) == input.Value.Hash && File.GetAttributes(input.Key) == input.Value.Attributes, input.Key);
        await File.WriteAllTextAsync(Path.Combine(proof, "observations.json"), JsonSerializer.Serialize(new { sourceSha256 = sourceSha, gzipSha256 = gzipSha,
            cases = observations, highlightScope = "CLI/HTML decoded PNG consistency; no generated highlight oracle",
            inputs = tracked.Select(p => new { path = p.Key, before = p.Value.Hash, after = Hash(p.Key), attributesBefore = (int)p.Value.Attributes, attributesAfter = (int)File.GetAttributes(p.Key) }) }, new JsonSerializerOptions { WriteIndented = true }));

        void Track(string path) => tracked[path] = (Hash(path), File.GetAttributes(path));
        string Project(string name, string[] paths, Dictionary<string, object?> settings)
        {
            var path = Path.Combine(folder, name + ".json"); File.WriteAllText(path, JsonSerializer.Serialize(new { formatVersion = 1, entries = new[] {
                new { leftPath = paths[0], basePath = paths.Length == 3 ? paths[1] : "", rightPath = paths[^1], mode = "Image", imageSettings = settings } }, activeEntryIndex = 0 })); Track(path); return path;
        }
        void VerifySettings(string label, string path, int mode, string[] paths, string? packageRoot = null)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var entry = doc.RootElement.TryGetProperty("entries", out var entries) ? entries[0] : doc.RootElement;
            var settings = entry.GetProperty("imageSettings");
            var found = settings.TryGetProperty("insertionDeletionMode", out var actual);
            check(Label(label + "-settings"), mode == 0 ? !found : found && actual.ValueKind == JsonValueKind.Number && actual.GetInt32() == mode, settings.GetRawText());
            for (var pane = 0; pane < paths.Length; pane++)
            {
                var reference = entry.GetProperty(pane == 0 ? "leftPath" : paths.Length == 3 && pane == 1 ? "basePath" : "rightPath").GetString()!;
                var local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, reference));
                check(Label(label + "-path-" + pane), File.Exists(local) && Hash(local) == Hash(paths[pane]) && (packageRoot is null
                    || !Path.IsPathRooted(reference) && local.StartsWith(Path.GetFullPath(packageRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), reference);
            }
        }
        void VerifyHtml(string label, string path, JsonElement inputs, JsonElement panes, int mode, JsonElement? state, JsonElement? highlight)
        {
            var html = File.ReadAllText(path); var body = Regex.Match(html, "<body\\b[^>]*>", RegexOptions.CultureInvariant).Value;
            check(Label(label + "-html-mode"), mode == 0 ? !body.Contains("data-insertion-deletion-mode=", StringComparison.Ordinal)
                : body.Contains("data-insertion-deletion-mode=\"" + mode + "\"", StringComparison.Ordinal), body);
            if (state.HasValue) foreach (var property in new[] { ("differenceCount", "data-difference-count"), ("conflictCount", "data-conflict-count") })
                check(Label(label + "-html-" + property.Item1), html.Contains(property.Item2 + "=\"" + state.Value.GetProperty(property.Item1).GetInt32() + "\"", StringComparison.Ordinal), "original region count");
            var tags = Regex.Matches(html, "<img\\b[^>]*>", RegexOptions.CultureInvariant).Cast<Match>().ToArray();
            for (var pane = 0; pane < inputs.GetArrayLength(); pane++) foreach (var original in new[] { true, false })
            {
                var side = sides[inputs.GetArrayLength() == 2 && pane == 1 ? 2 : pane] + (original ? "-original" : "");
                var tag = tags.Single(t => t.Value.Contains("data-side=\"" + side + "\"", StringComparison.Ordinal)).Value;
                var encoded = Regex.Match(tag, "src=\"data:image/png;base64,([^\"]+)\"", RegexOptions.CultureInvariant).Groups[1].Value;
                var decoded = ImageReportScenarios.DecodePng(Convert.FromBase64String(encoded)); var input = inputs[pane];
                var w = original ? input.GetProperty("width").GetInt32() : mode != 0 ? panes[pane].GetProperty("canvasWidth").GetInt32() : inputs.EnumerateArray().Max(p => p.GetProperty("width").GetInt32());
                var h = original ? input.GetProperty("height").GetInt32() : mode != 0 ? panes[pane].GetProperty("canvasHeight").GetInt32() : inputs.EnumerateArray().Max(p => p.GetProperty("height").GetInt32());
                byte[] expected;
                if (original || mode != 0) expected = Convert.FromHexString((original ? input : panes[pane]).GetProperty("bgraHex").GetString()!);
                else
                {
                    expected = new byte[w * h * 4]; var raw = Convert.FromHexString(input.GetProperty("bgraHex").GetString()!); var rawWidth = input.GetProperty("width").GetInt32();
                    for (var row = 0; row < input.GetProperty("height").GetInt32(); row++) raw.AsSpan(row * rawWidth * 4, rawWidth * 4).CopyTo(expected.AsSpan(row * w * 4));
                }
                check(Label(label + "-html-" + side), decoded.Width == w && decoded.Height == h && (highlight.HasValue && !original
                    ? string.Equals(Sha(decoded.Bgra), highlight.Value[pane].GetString(), StringComparison.OrdinalIgnoreCase) : decoded.Bgra.AsSpan().SequenceEqual(expected)),
                    highlight.HasValue && !original ? "independently decoded PNG agrees with normal CLI highlight SHA" : "original DLL/input all BGRA");
            }
        }
        async Task Reject(string label, string project, string[] commands, string[] options)
        {
            foreach (var command in commands)
            {
                var destination = Path.Combine(proof, "reject-" + label + "-" + command[2..] + (command == "--package-project" ? ".zip" : command == "--report-project" ? ".html" : ".json"));
                await File.WriteAllTextAsync(destination, "protected existing output"); var hash = Hash(destination); var attributes = File.GetAttributes(destination);
                var result = await run(Label("reject-" + label + "-" + command[2..]), 2, false,
                    [command, project, destination, .. options, .. (command == "--package-project" ? new[] { "--report" } : [])]);
                check(Label("reject-preserved-" + label + "-" + command[2..]), string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr)
                    && Hash(destination) == hash && File.GetAttributes(destination) == attributes, result.Stderr);
            }
        }
    }
    private static string Label(string value) => "image-insertion-path-" + value;
    private static string Mode(int value) => value == 1 ? "vertical" : value == 2 ? "horizontal" : "none";
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Hash(string path) => Sha(File.ReadAllBytes(path));
}
