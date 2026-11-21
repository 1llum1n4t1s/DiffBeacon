using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class ImageInsertionHighlightScenarios
{
    // 失敗先行: ghost/透明実画素の混同、削除色/選択色の欠落、alpha0のhidden RGB消失、
    // 水平/垂直/offset座標の誤適用、三者paneの色混同、失敗時の部分JSON・入力PNG変更。
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var root = FixtureRepository.FindRoot();
        if (root is null) throw new DirectoryNotFoundException("ImageInsertionHighlight fixture root");
        var packed = await File.ReadAllBytesAsync(Path.Combine(root.FullName, "tests", "Fixtures", "ImageInsertionHighlight", "winimerge-insertion-highlight-golden.json.gz"));
        const string gzipSha = "4636258D1C81E7D45CDBC2343334CC36FC7215E63490132B775E4338340D2C81";
        const string sourceSha = "FA89130794BD54918CF95B8D701729B61840B0105263C8D05C48710749F37B01";
        const string repeatSha = "AC8E457D3D718B63663CCA99E70E619F7F4446C783C9DD880B3884F978833674";
        check(Label("gzip-sha"), Sha(packed) == gzipSha, Sha(packed));
        if (Sha(packed) != gzipSha) throw new InvalidDataException("Insertion highlight golden changed");
        using var stream = new MemoryStream(packed); using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var decompressed = new MemoryStream(); await gzip.CopyToAsync(decompressed); var source = decompressed.ToArray();
        check(Label("source-sha"), Sha(source) == sourceSha, Sha(source));
        if (Sha(source) != sourceSha) throw new InvalidDataException("Insertion highlight observations changed");
        using var golden = JsonDocument.Parse(source);
        check(Label("repeat-provenance"), golden.RootElement.GetProperty("observationsSha256").GetString() == repeatSha
            && golden.RootElement.GetProperty("sourceRevision").GetString() == "da639cdfaeca87aaad0eaceec509afa11ad61421", "two original DLL runs; pinned revision and observation SHA");
        var folder = Path.Combine(fixtures, "image-insertion-highlight"); var proof = Path.Combine(output, "image-insertion-highlight");
        Directory.CreateDirectory(folder); Directory.CreateDirectory(proof);
        await File.WriteAllTextAsync(Path.Combine(proof, "failure-contract.md"),
            "全12件146状態: NONE/垂直/水平・alpha0/.3/.7/1・未選択/先頭/末尾・offset・透明実画素・三者の原本強調PNG全canvas BGRA SHA/寸法を実CLIと照合。\n全未選択状態は通常 --image・単体HTML・CLI上書き・包装HTML・展開再読込みへ接続し、独立PNG復号で全BGRA照合。既定alpha.7の省略も確認。期待画素は原本DLL二回採取固定SHAだけから読み、製品の結果から生成しない。\n拒否時stdoutは空、入力PNG bytes/属性を保持。PNG出力を作らない診断CLIに出力保存の検証は適用しない。\n");
        var observations = new List<object>(); var inputObservations = new List<object>(); var casesChecked = 0; var statesChecked = 0; var normalAlphaStates = 0;
        string[] sides = ["left", "middle", "right"];
        string[]? firstPaths = null;
        foreach (var item in golden.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!; var inputs = item.GetProperty("inputs"); var count = inputs.GetArrayLength();
            var paths = new string[count]; var hashes = new string[count]; var attributes = new FileAttributes[count];
            var preparedAttributes = new FileAttributes[count];
            var readonlyFlags = item.GetProperty("readOnly");
            for (var pane = 0; pane < count; pane++)
            {
                paths[pane] = Path.Combine(folder, name + "-" + pane + ".png");
                var bytes = Convert.FromBase64String(inputs[pane].GetProperty("pngBase64").GetString()!);
                await File.WriteAllBytesAsync(paths[pane], bytes); hashes[pane] = Hash(paths[pane]); attributes[pane] = File.GetAttributes(paths[pane]);
                check(Label(name + "-input-sha-" + pane), hashes[pane] == inputs[pane].GetProperty("pngSha256").GetString(), "original input PNG SHA");
                var decoded = ImageReportScenarios.DecodePng(bytes); var raw = Convert.FromHexString(inputs[pane].GetProperty("bgraHex").GetString()!);
                check(Label(name + "-input-pixels-" + pane), decoded.Width == inputs[pane].GetProperty("width").GetInt32()
                    && decoded.Height == inputs[pane].GetProperty("height").GetInt32() && decoded.Bgra.AsSpan().SequenceEqual(raw), "independent PNG decode and every original pixel");
                if (readonlyFlags[pane].GetInt32() != 0)
                    File.SetAttributes(paths[pane], (attributes[pane] & ~FileAttributes.Normal) | FileAttributes.ReadOnly);
                // Normalの正規化を含め、実アプリに渡す直前のOS属性を保存する。
                preparedAttributes[pane] = File.GetAttributes(paths[pane]);
                if (readonlyFlags[pane].GetInt32() != 0)
                    check(Label(name + "-readonly-fixture-" + pane), (preparedAttributes[pane] & FileAttributes.ReadOnly) != 0,
                        "prepared OS attributes=" + preparedAttributes[pane]);
            }
            firstPaths ??= paths;
            try
            {
                var states = item.GetProperty("states");
                foreach (var state in states.EnumerateArray())
                {
                    var stateIndex = state.GetProperty("stateIndex").GetInt32(); var mode = state.GetProperty("mode").GetInt32();
                    var alpha = state.GetProperty("highlightAlpha").GetDouble(); var selected = state.GetProperty("currentDiffIndex").GetInt32(); var panes = state.GetProperty("panes");
                    var options = new List<string> { "--block-size", item.GetProperty("blockSize").GetInt32().ToString(CultureInfo.InvariantCulture),
                        "--threshold", item.GetProperty("threshold").GetDouble().ToString("R", CultureInfo.InvariantCulture), "--insertion-deletion-mode", Mode(mode),
                        "--highlight-alpha", alpha.ToString("R", CultureInfo.InvariantCulture), "--selected-region", (selected + 1).ToString(CultureInfo.InvariantCulture) };
                    for (var pane = 0; pane < count; pane++)
                    {
                        var side = sides[count == 2 && pane == 1 ? 2 : pane];
                        options.Add("--" + side + "-offset"); options.Add(panes[pane].GetProperty("offsetX").GetInt32().ToString(CultureInfo.InvariantCulture)
                            + "," + panes[pane].GetProperty("offsetY").GetInt32().ToString(CultureInfo.InvariantCulture));
                        var oraclePixels = Convert.FromHexString(panes[pane].GetProperty("bgraHex").GetString()!);
                        var oraclePng = Convert.FromBase64String(panes[pane].GetProperty("pngBase64").GetString()!); var decoded = ImageReportScenarios.DecodePng(oraclePng);
                        check(Label(name + "-state" + stateIndex + "-oracle-" + pane), Sha(oraclePixels) == panes[pane].GetProperty("bgraSha256").GetString()
                            && Sha(oraclePng) == panes[pane].GetProperty("pngSha256").GetString()
                            && decoded.Width == panes[pane].GetProperty("canvasWidth").GetInt32() && decoded.Height == panes[pane].GetProperty("canvasHeight").GetInt32()
                            && decoded.Bgra.AsSpan().SequenceEqual(oraclePixels), "original PNG independently decoded; literal full BGRA and SHA agree");
                    }
                    var result = await run(Label(name + "-state" + stateIndex), state.GetProperty("differenceCount").GetInt32() == 0 ? 0 : 1, true,
                        ["--image-regions", .. paths, .. options]);
                    using var parsed = JsonDocument.Parse(result.Stdout); var actual = parsed.RootElement;
                    foreach (var property in new[] { "differenceCount", "conflictCount" })
                        check(Label(name + "-state" + stateIndex + "-" + property), actual.GetProperty(property).GetInt32() == state.GetProperty(property).GetInt32(), "original public DLL count");
                    var rendered = actual.GetProperty("renderedFrames");
                    check(Label(name + "-state" + stateIndex + "-pane-count"), rendered.GetArrayLength() == count, "one rendered canvas per input");
                    for (var pane = 0; pane < Math.Min(count, rendered.GetArrayLength()); pane++)
                        check(Label(name + "-state" + stateIndex + "-rendered-" + pane), rendered[pane].GetProperty("frame").GetInt32() == 1
                            && rendered[pane].GetProperty("width").GetInt32() == panes[pane].GetProperty("canvasWidth").GetInt32()
                            && rendered[pane].GetProperty("height").GetInt32() == panes[pane].GetProperty("canvasHeight").GetInt32()
                            && string.Equals(rendered[pane].GetProperty("pixelSha256").GetString(), panes[pane].GetProperty("bgraSha256").GetString(), StringComparison.OrdinalIgnoreCase),
                            "original every canvas BGRA SHA including hidden RGB, ghost, selected color and offsets");
                    observations.Add(new { name, stateIndex, mode, alpha, selected, rendered = rendered.Clone() }); statesChecked++;
                    if (selected < 0)
                    {
                        var normalOptions = new List<string>();
                        for (var i = 0; i < options.Count; i += 2)
                            if (options[i] != "--selected-region") { normalOptions.Add(options[i]); normalOptions.Add(options[i + 1]); }
                        var normalAlpha = await run(Label(name + "-normal-alpha-state" + stateIndex), state.GetProperty("differenceCount").GetInt32() == 0 ? 0 : 1, true,
                            ["--image", .. paths, .. normalOptions]);
                        using var normalAlphaJson = JsonDocument.Parse(normalAlpha.Stdout);
                        check(Label(name + "-normal-alpha-setting-" + stateIndex), normalAlphaJson.RootElement.GetProperty("highlightAlpha").GetDouble() == alpha,
                            "normal CLI accepts the explicit original highlight alpha");
                        var alphaHashes = normalAlphaJson.RootElement.GetProperty("frames")[0].GetProperty("highlightPixelSha256");
                        for (var pane = 0; pane < count; pane++) check(Label(name + "-normal-alpha-pixels-" + stateIndex + "-" + pane),
                            string.Equals(alphaHashes[pane].GetString(), panes[pane].GetProperty("bgraSha256").GetString(), StringComparison.OrdinalIgnoreCase),
                            "normal CLI every BGRA pixel matches independently verified original PNG");
                        await Reports(name + "-state" + stateIndex, item, state, paths);
                        normalAlphaStates++;
                    }
                }
                // 全件のstate1は整列直後・offsetなし・既定alpha.7・選択解除。
                var initial = states[1]; var initialPanes = initial.GetProperty("panes");
                check(Label(name + "-normal-initial-contract"), initial.GetProperty("highlightAlpha").GetDouble() == .7
                    && initial.GetProperty("currentDiffIndex").GetInt32() == -1
                    && initialPanes.EnumerateArray().All(p => p.GetProperty("offsetX").GetInt32() == 0 && p.GetProperty("offsetY").GetInt32() == 0), "normal CLI default highlight state");
                var normal = await run(Label(name + "-normal-cli"), initial.GetProperty("differenceCount").GetInt32() == 0 ? 0 : 1, true,
                    ["--image", .. paths, "--insertion-deletion-mode", Mode(initial.GetProperty("mode").GetInt32()),
                        "--block-size", item.GetProperty("blockSize").GetInt32().ToString(CultureInfo.InvariantCulture),
                        "--threshold", item.GetProperty("threshold").GetDouble().ToString("R", CultureInfo.InvariantCulture)]);
                using var normalJson = JsonDocument.Parse(normal.Stdout); var normalFrame = normalJson.RootElement.GetProperty("frames")[0];
                check(Label(name + "-normal-default-alpha"), normalJson.RootElement.GetProperty("highlightAlpha").GetDouble() == .7, "omitted alpha preserves existing default");
                var normalHashes = normalFrame.GetProperty("highlightPixelSha256");
                for (var pane = 0; pane < count; pane++) check(Label(name + "-normal-original-highlight-" + pane),
                    string.Equals(normalHashes[pane].GetString(), initialPanes[pane].GetProperty("bgraSha256").GetString(), StringComparison.OrdinalIgnoreCase), "normal CLI default vs original DLL all highlighted pixels");
                foreach (var property in new[] { "differenceCount", "conflictCount" }) check(Label(name + "-normal-" + property),
                    normalFrame.GetProperty(property).GetInt32() == initial.GetProperty(property).GetInt32(), "normal comparison original counts");
                for (var pane = 0; pane < count; pane++)
                {
                    var actualAttributes = File.GetAttributes(paths[pane]);
                    check(Label(name + "-input-preserved-" + pane), Hash(paths[pane]) == hashes[pane] && actualAttributes == preparedAttributes[pane],
                        "PNG bytes; expected OS attributes=" + preparedAttributes[pane] + "; actual=" + actualAttributes);
                    inputObservations.Add(new { path = paths[pane], before = hashes[pane], after = Hash(paths[pane]), attributes = (int)actualAttributes });
                }
            }
            finally { for (var pane = 0; pane < count; pane++) File.SetAttributes(paths[pane], attributes[pane]); }
            casesChecked++;
        }
        foreach (var (label, options) in new[] { ("invalid-mode", new[] { "--insertion-deletion-mode", "diagonal", "--highlight-alpha", ".7" }),
            ("invalid-alpha", new[] { "--insertion-deletion-mode", "vertical", "--highlight-alpha", "1.1" }),
            ("invalid-selected", new[] { "--insertion-deletion-mode", "vertical", "--selected-region", "999999" }),
            ("duplicate-mode", new[] { "--insertion-deletion-mode", "vertical", "--insertion-deletion-mode", "horizontal", "--highlight-alpha", ".7" }) })
        {
            var before = firstPaths!.Select(p => (Hash: Hash(p), Attributes: File.GetAttributes(p))).ToArray();
            var result = await run(Label("reject-" + label), 2, false, ["--image-regions", .. firstPaths!, .. options]);
            check(Label("reject-atomic-json-" + label), string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            for (var pane = 0; pane < firstPaths!.Length; pane++) check(Label("reject-input-preserved-" + label + "-" + pane),
                Hash(firstPaths[pane]) == before[pane].Hash && File.GetAttributes(firstPaths[pane]) == before[pane].Attributes, "input PNG hash and attributes");
        }
        var rejectionProject = Path.Combine(folder, "alpha-rejection-project.json");
        await File.WriteAllTextAsync(rejectionProject, JsonSerializer.Serialize(new { formatVersion = 1, activeEntryIndex = 0,
            entries = new[] { new { mode = "Image", leftPath = firstPaths![0], rightPath = firstPaths[^1],
                basePath = firstPaths.Length == 3 ? firstPaths[1] : "", imageSettings = new { highlightAlpha = .7 } } } }));
        var protectedOutput = Path.Combine(proof, "alpha-rejection-output.html");
        await File.WriteAllTextAsync(protectedOutput, "existing HTML must remain byte-for-byte");
        var protectedBytes = await File.ReadAllBytesAsync(protectedOutput); var protectedAttributes = File.GetAttributes(protectedOutput);
        var rejectionInputs = firstPaths!.Append(rejectionProject).Select(path => (Path: path, Hash: Hash(path), Attributes: File.GetAttributes(path))).ToArray();
        var invalidAlphaOptions = new List<string[]>();
        foreach (var value in new[] { "-.1", "1.1", "NaN", "Infinity", "1e309", "bad" }) invalidAlphaOptions.Add(["--highlight-alpha", value]);
        invalidAlphaOptions.Add(["--highlight-alpha"]);
        invalidAlphaOptions.Add(["--highlight-alpha", ".3", "--highlight-alpha", ".7"]);
        for (var invalidIndex = 0; invalidIndex < invalidAlphaOptions.Count; invalidIndex++)
        {
            var options = invalidAlphaOptions[invalidIndex];
            var rejectedCompare = await run(Label("normal-alpha-reject-" + invalidIndex), 2, false, ["--image", .. firstPaths!, .. options]);
            var rejectedReport = await run(Label("report-alpha-reject-" + invalidIndex), 2, false,
                ["--report-project", rejectionProject, protectedOutput, .. options]);
            check(Label("alpha-rejection-atomic-" + invalidIndex), string.IsNullOrWhiteSpace(rejectedCompare.Stdout)
                && string.IsNullOrWhiteSpace(rejectedReport.Stdout) && !string.IsNullOrWhiteSpace(rejectedCompare.Stderr)
                && !string.IsNullOrWhiteSpace(rejectedReport.Stderr)
                && (await File.ReadAllBytesAsync(protectedOutput)).AsSpan().SequenceEqual(protectedBytes)
                && File.GetAttributes(protectedOutput) == protectedAttributes, "invalid range/type, missing value and duplicate option preserve existing output");
            foreach (var input in rejectionInputs) check(Label("alpha-rejection-input-" + invalidIndex + "-" + Path.GetFileName(input.Path)),
                Hash(input.Path) == input.Hash && File.GetAttributes(input.Path) == input.Attributes, "input PNG and project bytes/attributes retained");
        }
        check(Label("case-count"), casesChecked == 12, casesChecked.ToString()); check(Label("state-count"), statesChecked == 146, statesChecked.ToString());
        var expectedNormalStates = golden.RootElement.GetProperty("cases").EnumerateArray()
            .Sum(item => item.GetProperty("states").EnumerateArray().Count(state => state.GetProperty("currentDiffIndex").GetInt32() < 0));
        check(Label("normal-alpha-state-count"), normalAlphaStates == expectedNormalStates && normalAlphaStates > 0,
            "covered=" + normalAlphaStates + "; original unselected=" + expectedNormalStates);
        await File.WriteAllTextAsync(Path.Combine(proof, "observations.json"), JsonSerializer.Serialize(new { gzipSha256 = gzipSha, sourceSha256 = sourceSha,
            repeatObservationsSha256 = repeatSha, cases = casesChecked, states = statesChecked, normalCliCases = casesChecked, normalAlphaStates, observations, inputs = inputObservations }, new JsonSerializerOptions { WriteIndented = true }));

        async Task Reports(string label, JsonElement item, JsonElement state, string[] paths)
        {
            var panes = state.GetProperty("panes"); var count = paths.Length; var alpha = state.GetProperty("highlightAlpha").GetDouble();
            var settings = new Dictionary<string, object?> { ["highlightAlpha"] = alpha, ["threshold"] = item.GetProperty("threshold").GetDouble(),
                ["blockSize"] = item.GetProperty("blockSize").GetInt32(), ["insertionDeletionMode"] = state.GetProperty("mode").GetInt32(), ["reportAllFrames"] = false };
            for (var p = 0; p < count; p++)
            {
                var side = sides[count == 2 && p == 1 ? 2 : p]; var value = panes[p];
                settings[side + "Offset"] = new { x = value.GetProperty("offsetX").GetInt32(), y = value.GetProperty("offsetY").GetInt32() };
            }
            var project = Path.Combine(folder, label + ".json");
            await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new { leftPath = paths[0], rightPath = paths[^1], basePath = count == 3 ? paths[1] : "", mode = "Image", imageSettings = settings }));
            var standalone = Path.Combine(proof, label + ".html");
            await run(Label(label + "-alpha-report"), 0, true, ["--report-project", project, standalone]);
            CheckHtml("standalone", await File.ReadAllTextAsync(standalone));
            // 保存値を変えず、明示CLI指定が優先される経路も確認する。
            var overrideSettings = new Dictionary<string, object?>(settings) { ["highlightAlpha"] = alpha == 0 ? 1 : 0 };
            var overrideProject = Path.Combine(folder, label + "-override.json");
            await File.WriteAllTextAsync(overrideProject, JsonSerializer.Serialize(new { leftPath = paths[0], rightPath = paths[^1], basePath = count == 3 ? paths[1] : "", mode = "Image", imageSettings = overrideSettings }));
            var overrideOutput = Path.Combine(proof, label + "-override.html");
            await run(Label(label + "-alpha-report-override"), 0, true, ["--report-project", overrideProject, overrideOutput, "--highlight-alpha", alpha.ToString("R", CultureInfo.InvariantCulture)]);
            CheckHtml("override", await File.ReadAllTextAsync(overrideOutput));
            var packed = Path.Combine(proof, label + ".zip");
            await run(Label(label + "-alpha-package"), 0, true, ["--package-project", project, packed, "--report"]);
            using (var zip = ZipFile.OpenRead(packed))
            {
                using var content = new StreamReader(zip.GetEntry("report.files/1.html")!.Open()); CheckHtml("packaged", await content.ReadToEndAsync());
                using var saved = JsonDocument.Parse(zip.GetEntry("project.json")!.Open());
                check(Label(label + "-alpha-package-setting"), saved.RootElement.GetProperty("entries")[0].GetProperty("imageSettings").GetProperty("highlightAlpha").GetDouble() == alpha,
                    "source-generated packed project retains alpha");
            }
            var extracted = Path.Combine(proof, label + "-expanded"); ZipFile.ExtractToDirectory(packed, extracted);
            var regenerated = Path.Combine(proof, label + "-regenerated.html");
            await run(Label(label + "-alpha-expanded-report"), 0, true, ["--report-project", Path.Combine(extracted, "project.json"), regenerated]);
            CheckHtml("regenerated", await File.ReadAllTextAsync(regenerated));

            void CheckHtml(string kind, string html)
            {
                check(Label(label + "-" + kind + "-alpha-metadata"), Attr(html, "data-highlight-alpha") == alpha.ToString("R", CultureInfo.InvariantCulture), "adopted alpha metadata");
                var tags = Regex.Matches(html, @"<img\b(?<a>[^>]*)>").Cast<Match>().ToArray();
                for (var p = 0; p < count; p++)
                {
                    var side = sides[count == 2 && p == 1 ? 2 : p]; var tagsForSide = tags.Where(tag => Attr(tag.Groups["a"].Value, "data-side") == side).ToArray();
                    check(Label(label + "-" + kind + "-unique-" + side), tagsForSide.Length == 1, "exactly one highlighted source PNG");
                    if (tagsForSide.Length != 1) continue;
                    var uri = Attr(tagsForSide[0].Groups["a"].Value, "src");
                    if (!uri.StartsWith("data:image/png;base64,", StringComparison.Ordinal)) throw new InvalidDataException("embedded PNG missing");
                    var png = Convert.FromBase64String(uri[22..]); var decoded = ImageReportScenarios.DecodePng(png); var expected = panes[p];
                    check(Label(label + "-" + kind + "-all-pixels-" + side), decoded.Width == expected.GetProperty("canvasWidth").GetInt32()
                        && decoded.Height == expected.GetProperty("canvasHeight").GetInt32()
                        && decoded.Bgra.AsSpan().SequenceEqual(Convert.FromHexString(expected.GetProperty("bgraHex").GetString()!)), "independent PNG decode vs original full BGRA including alpha0 hidden RGB");
                    File.WriteAllBytes(Path.Combine(proof, label + "-" + kind + "-" + side + ".png"), png);
                }
            }
        }
    }
    private static string Label(string value) => "image-insertion-highlight-" + value;
    private static string Mode(int value) => value == 1 ? "vertical" : value == 2 ? "horizontal" : "none";
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Hash(string path) => Sha(File.ReadAllBytes(path));
    private static string Attr(string text, string name) => WebUtility.HtmlDecode(Regex.Match(text, "\\b" + Regex.Escape(name) + "=[\"'](?<v>[^\"']*)[\"']").Groups["v"].Value);
}
