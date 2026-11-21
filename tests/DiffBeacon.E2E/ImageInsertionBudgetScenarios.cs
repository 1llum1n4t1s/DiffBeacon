using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class ImageInsertionBudgetScenarios
{
    // 失敗先行: raw240Mの事前検査だけでaligned360Mを受理、ページごとの予算リセット、
    // 失敗時の部分JSON/既存HTML破壊、入力TIFF変更、選択1ページ12Mの誤拒否、
    // 拒否前のPNG化、spool往復での原画・整列・方向・短入力反復の破損。
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        const int width = 100, height = 200, pages = 10, alignedHeight = 300, canvasWidth = 40_000;
        const long maximumWork = 256_000_000;
        const string workError = "画像の描画作業量が256Mピクセルを超えます。";
        var folder = Path.Combine(fixtures, "image-insertion-budget"); var proof = Path.Combine(output, "image-insertion-budget");
        Directory.CreateDirectory(folder); Directory.CreateDirectory(proof);
        var paths = new[] { Path.Combine(folder, "left.tif"), Path.Combine(folder, "right.tif") };
        for (var pane = 0; pane < paths.Length; pane++) WriteTiff(paths[pane], pane, pages);
        var before = paths.Select(path => (Hash: Hash(path), Attributes: File.GetAttributes(path))).ToArray();
        var rawWork = (long)canvasWidth * height * 3 * pages;
        var alignedWork = (long)canvasWidth * alignedHeight * 3 * pages;
        var selectedPixels = (long)canvasWidth * alignedHeight;
        check(Label("fixture-budget-boundary"), rawWork == 240_000_000 && rawWork <= maximumWork
            && alignedWork == 360_000_000 && alignedWork > maximumWork && selectedPixels == 12_000_000
            && selectedPixels * 3 < maximumWork, "explicit input geometry; raw240M/aligned360M/selected36M drawing work");
        check(Label("fixture-size"), paths.All(path => new FileInfo(path).Length == 801_468), "two tiny uncompressed 10-page RGBA TIFFs");
        await File.WriteAllTextAsync(Path.Combine(proof, "failure-contract.md"),
            "入力: 各10pages100x200。左はA100行+B100行、右はB100行+C100行の固定opaqueRGBA。共有B100行の垂直整列後height300。\n右offset39900,0によりcanvas幅40000。raw描画は40000*200*3*10=240M、整列後は40000*300*3*10=360M。\n全ページCLI/HTMLは256M描画作業量拒否、CLI stdout空、既存HTMLbytes/属性と入力TIFFbytes/属性を保持。選択1ページは12Mcanvas/36M描画で成功。\nHTML原画は固定色パターン全BGRAを独立PNG decoderで照合、表示canvas寸法を確認。製品画素SHAを期待値へ転用しない。\n");
        string[] options = ["--insertion-deletion-mode", "vertical", "--right-offset", "39900,0"];
        var rejected = await run(Label("all-pages-reject"), 2, false, ["--image", .. paths, .. options]);
        check(Label("all-pages-atomic-rejection"), string.IsNullOrWhiteSpace(rejected.Stdout)
            && rejected.Stderr.Contains(workError, StringComparison.Ordinal), rejected.Stderr);
        var selected = await run(Label("selected-page-success"), 1, true,
            ["--image", .. paths, .. options, "--left-frame", "1", "--right-frame", "1"]);
        using (var parsed = JsonDocument.Parse(selected.Stdout))
        {
            var actual = parsed.RootElement; var frames = actual.GetProperty("frames");
            check(Label("selected-mode-count"), actual.GetProperty("mode").GetString() == "selected" && actual.GetProperty("different").GetBoolean()
                && actual.GetProperty("leftFrames").GetInt32() == pages && actual.GetProperty("rightFrames").GetInt32() == pages
                && actual.GetProperty("insertionDeletionMode").GetInt32() == 1 && frames.GetArrayLength() == 1, "one selected pair from two 10-page TIFFs");
            var frame = frames[0];
            check(Label("selected-geometry"), frame.GetProperty("leftFrame").GetInt32() == 1 && frame.GetProperty("rightFrame").GetInt32() == 1
                && frame.GetProperty("leftWidth").GetInt32() == width && frame.GetProperty("rightWidth").GetInt32() == width
                && frame.GetProperty("leftHeight").GetInt32() == alignedHeight && frame.GetProperty("rightHeight").GetInt32() == alignedHeight
                && frame.GetProperty("totalPixels").GetInt64() == selectedPixels, "shared B100 rows; A+B+ghost vs ghost+B+C; offset canvas40000x300");
        }
        var project = Path.Combine(folder, "all-pages.json");
        await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new { formatVersion = 1, activeEntryIndex = 0, entries = new[] {
            new { leftPath = paths[0], basePath = "", rightPath = paths[1], mode = "Image", imageSettings = new {
                insertionDeletionMode = 1, rightOffset = new { x = 39900, y = 0 }, showDifferences = false, reportAllFrames = true } } } }));
        var projectHash = Hash(project); var projectAttributes = File.GetAttributes(project);
        var protectedOutput = Path.Combine(proof, "protected-all-pages.html");
        await File.WriteAllTextAsync(protectedOutput, "protected existing HTML - drawing budget rejection must preserve this file");
        var protectedHash = Hash(protectedOutput); var protectedAttributes = File.GetAttributes(protectedOutput);
        var reportRejected = await run(Label("all-pages-report-reject"), 2, false, ["--report-project", project, protectedOutput]);
        check(Label("report-atomic-rejection"), string.IsNullOrWhiteSpace(reportRejected.Stdout)
            && reportRejected.Stderr.Contains(workError, StringComparison.Ordinal), reportRejected.Stderr);
        check(Label("report-existing-output-preserved"), Hash(protectedOutput) == protectedHash
            && File.GetAttributes(protectedOutput) == protectedAttributes, "existing HTML SHA and attributes");
        var protectedPackage = Path.Combine(proof, "protected-all-pages.zip");
        await File.WriteAllTextAsync(protectedPackage, "protected existing package");
        var packageHash = Hash(protectedPackage); var packageAttributes = File.GetAttributes(protectedPackage);
        var packageRejected = await run(Label("all-pages-package-reject"), 2, false,
            ["--package-project", project, protectedPackage, "--report"]);
        check(Label("package-atomic-rejection"), string.IsNullOrWhiteSpace(packageRejected.Stdout)
            && packageRejected.Stderr.Contains(workError, StringComparison.Ordinal), packageRejected.Stderr);
        check(Label("package-existing-output-preserved"), Hash(protectedPackage) == packageHash
            && File.GetAttributes(protectedPackage) == packageAttributes, "existing package SHA and attributes");
        var report = Path.Combine(proof, "selected-page.html");
        await run(Label("selected-page-report-success"), 0, true, ["--report-project", project, report, "--left-frame", "1", "--right-frame", "1"]);
        var html = await File.ReadAllTextAsync(report);
        var body = Regex.Match(html, "<body\\b[^>]*>", RegexOptions.CultureInvariant).Value;
        check(Label("selected-html-mode"), body.Contains("data-frame-mode=\"selected\"", StringComparison.Ordinal)
            && body.Contains("data-insertion-deletion-mode=\"1\"", StringComparison.Ordinal)
            && body.Contains("data-left-frames=\"10\"", StringComparison.Ordinal) && body.Contains("data-right-frames=\"10\"", StringComparison.Ordinal), body);
        var rows = Regex.Matches(html, "<tr\\b[^>]*data-left-frame=\"[^\"]+\"[^>]*>", RegexOptions.CultureInvariant);
        check(Label("selected-html-row"), rows.Count == 1 && rows[0].Value.Contains("data-left-frame=\"1\"", StringComparison.Ordinal)
            && rows[0].Value.Contains("data-right-frame=\"1\"", StringComparison.Ordinal)
            && rows[0].Value.Contains("data-total-pixels=\"12000000\"", StringComparison.Ordinal), "exactly one selected12M canvas");
        var images = Regex.Matches(html, "<img\\b[^>]*>", RegexOptions.CultureInvariant).Cast<Match>().ToArray();
        for (var pane = 0; pane < 2; pane++)
        {
            var side = pane == 0 ? "left" : "right";
            var original = images.Single(image => image.Value.Contains("data-side=\"" + side + "-original\"", StringComparison.Ordinal)).Value;
            var encoded = Regex.Match(original, "src=\"data:image/png;base64,([^\"]+)\"", RegexOptions.CultureInvariant).Groups[1].Value;
            var png = Convert.FromBase64String(encoded); var decoded = ImageReportScenarios.DecodePng(png);
            check(Label("selected-html-original-" + side), decoded.Width == width && decoded.Height == height
                && decoded.Bgra.AsSpan().SequenceEqual(RawBgra(pane)), "independent PNG decode; literal opaque A+B/B+C every original BGRA");
            await File.WriteAllBytesAsync(Path.Combine(proof, "selected-original-" + side + ".png"), png);
            var display = images.Single(image => image.Value.Contains("data-side=\"" + side + "\"", StringComparison.Ordinal)).Value;
            check(Label("selected-html-canvas-" + side), display.Contains("data-width=\"40000\"", StringComparison.Ordinal)
                && display.Contains("data-height=\"300\"", StringComparison.Ordinal), "aligned and offset displayed canvas dimensions");
        }
        check(Label("project-override-preserved"), Hash(project) == projectHash && File.GetAttributes(project) == projectAttributes, "selected frame override leaves saved all-pages project intact");
        for (var pane = 0; pane < paths.Length; pane++) check(Label("input-preserved-" + pane), Hash(paths[pane]) == before[pane].Hash
            && File.GetAttributes(paths[pane]) == before[pane].Attributes, "input TIFF SHA and attributes");
        await VerifyPreparedPages();
        await File.WriteAllTextAsync(Path.Combine(proof, "observations.json"), JsonSerializer.Serialize(new { inputPages = pages, inputWidth = width, inputHeight = height,
            alignedHeight, canvasWidth, rawCanvasWork = rawWork, alignedCanvasWork = alignedWork, maximumCanvasWork = maximumWork, selectedPixels,
            selectedCanvasWork = selectedPixels * 3, rejected.ExitCode, rejection = rejected.Stderr, reportRejection = reportRejected.Stderr,
            reportDurationMilliseconds = reportRejected.DurationMilliseconds, packageDurationMilliseconds = packageRejected.DurationMilliseconds,
            packageRejection = packageRejected.Stderr, selectedJson = selected.Stdout,
            protectedPackage = new { path = protectedPackage, before = packageHash, after = Hash(protectedPackage) },
            protectedOutput = new { path = protectedOutput, before = protectedHash, after = Hash(protectedOutput) },
            inputs = paths.Select((path, pane) => new { path, before = before[pane].Hash, after = Hash(path), attributesBefore = (int)before[pane].Attributes, attributesAfter = (int)File.GetAttributes(path) })
        }, new JsonSerializerOptions { WriteIndented = true }));

        async Task VerifyPreparedPages()
        {
            var shortRight = Path.Combine(folder, "right-short.tif"); var middle = Path.Combine(folder, "middle.tif");
            WriteTiff(shortRight, 1, 2); WriteTiff(middle, 0, 3);
            var originalHashes = new[] { Hash(shortRight), Hash(middle) };
            foreach (var three in new[] { false, true })
            foreach (var horizontal in new[] { false, true })
            {
                var name = (three ? "three" : "two") + (horizontal ? "-horizontal" : "-vertical");
                var currentProject = Path.Combine(folder, name + ".json");
                var rotation = horizontal ? 90 : 0;
                await File.WriteAllTextAsync(currentProject, JsonSerializer.Serialize(new { formatVersion = 1, activeEntryIndex = 0, entries = new[] {
                    new { leftPath = paths[0], basePath = three ? middle : "", rightPath = shortRight, mode = "Image", imageSettings = new {
                        insertionDeletionMode = horizontal ? 2 : 1, showDifferences = false, reportAllFrames = true,
                        leftOrientation = new { rotation }, middleOrientation = new { rotation = three ? rotation : 0 }, rightOrientation = new { rotation } } } } }));
                var destination = Path.Combine(proof, "prepared-" + name + ".html");
                await run(Label("prepared-" + name), 0, true, ["--report-project", currentProject, destination]);
                var text = await File.ReadAllTextAsync(destination);
                var rows = Regex.Matches(text, "<tr\\b[^>]*data-left-frame=\"[^\"]+\"[^>]*>.*?</tr>", RegexOptions.CultureInvariant | RegexOptions.Singleline);
                check(Label("prepared-" + name + "-count"), rows.Count == pages, "ten prepared pages; short right repeats last2 and middle repeats last3");
                var sides = three ? new[] { "left", "middle", "right" } : ["left", "right"];
                for (var page = 0; page < rows.Count; page++)
                {
                    var row = rows[page].Value;
                    check(Label("prepared-" + name + "-numbers-" + page), row.Contains("data-left-frame=\"" + (page + 1) + "\"", StringComparison.Ordinal)
                        && row.Contains("data-right-frame=\"" + Math.Min(page + 1, 2) + "\"", StringComparison.Ordinal)
                        && (!three || row.Contains("data-middle-frame=\"" + Math.Min(page + 1, 3) + "\"", StringComparison.Ordinal))
                        && row.Contains("data-total-pixels=\"30000\"", StringComparison.Ordinal)
                        && row.Contains("data-different-pixels=\"20000\"", StringComparison.Ordinal), "sourceNumber and aligned A+B+C geometry");
                    var images = Regex.Matches(row, "<img\\b[^>]*>", RegexOptions.CultureInvariant).Cast<Match>().ToArray();
                    foreach (var side in sides)
                    foreach (var original in new[] { false, true })
                    {
                        var tag = images.Single(image => image.Value.Contains("data-side=\"" + side + (original ? "-original" : "") + "\"", StringComparison.Ordinal)).Value;
                        var png = Convert.FromBase64String(Regex.Match(tag, "src=\"data:image/png;base64,([^\"]+)\"", RegexOptions.CultureInvariant).Groups[1].Value);
                        var decoded = ImageReportScenarios.DecodePng(png);
                        var expected = original ? RawBgra(side == "right" ? 1 : 0) : AlignedBgra(side == "right", horizontal);
                        check(Label("prepared-" + name + "-pixels-" + page + "-" + side + (original ? "-original" : "")),
                            decoded.Width == (original ? 100 : horizontal ? 300 : 100) && decoded.Height == (original ? 200 : horizontal ? 100 : 300)
                            && decoded.Bgra.AsSpan().SequenceEqual(expected), "independent PNG decoder; every literal original/aligned BGRA; no product SHA expectation");
                        if (page == 0) await File.WriteAllBytesAsync(Path.Combine(proof, "prepared-" + name + "-" + side + (original ? "-original" : "") + ".png"), png);
                    }
                }
                if (three && horizontal)
                {
                    var package = Path.Combine(proof, "prepared-three-horizontal.zip");
                    await run(Label("prepared-package"), 0, true, ["--package-project", currentProject, package, "--report"]);
                    using var archive = ZipFile.OpenRead(package);
                    using var reader = new StreamReader(archive.GetEntry("report.files/1.html")!.Open());
                    var packaged = await reader.ReadToEndAsync();
                    var packagedRows = Regex.Matches(packaged, "<tr\\b[^>]*data-left-frame=\"[^\"]+\"[^>]*>.*?</tr>", RegexOptions.CultureInvariant | RegexOptions.Singleline);
                    check(Label("prepared-package-pages"), packagedRows.Count == pages, "wrapped three-pane horizontal ten-page report");
                    for (var page = 0; page < packagedRows.Count; page++)
                    foreach (var side in sides)
                    foreach (var original in new[] { false, true })
                    {
                        var tags = Regex.Matches(packagedRows[page].Value, "<img\\b[^>]*>", RegexOptions.CultureInvariant).Cast<Match>();
                        var tag = tags.Single(image => image.Value.Contains("data-side=\"" + side + (original ? "-original" : "") + "\"", StringComparison.Ordinal)).Value;
                        var png = Convert.FromBase64String(Regex.Match(tag, "src=\"data:image/png;base64,([^\"]+)\"", RegexOptions.CultureInvariant).Groups[1].Value);
                        var decoded = ImageReportScenarios.DecodePng(png);
                        var expected = original ? RawBgra(side == "right" ? 1 : 0) : AlignedBgra(side == "right", true);
                        check(Label("prepared-package-pixels-" + page + "-" + side + (original ? "-original" : "")),
                            decoded.Bgra.AsSpan().SequenceEqual(expected), "packaged PNG independently decoded; every literal BGRA");
                    }
                }
            }
            check(Label("prepared-inputs-preserved"), Hash(shortRight) == originalHashes[0] && Hash(middle) == originalHashes[1], "short-page TIFF and middle input hashes");
        }
    }

    private static byte[] AlignedBgra(bool right, bool horizontal)
    {
        var width = horizontal ? 300 : 100; var height = horizontal ? 100 : 300;
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var axis = horizontal ? x : y;
            if (right ? axis < 100 : axis >= 200) continue;
            var color = axis / 100; var pixel = (y * width + x) * 4;
            result[pixel] = (byte)(30 + color * 50); result[pixel + 1] = (byte)(20 + color * 50);
            result[pixel + 2] = (byte)(10 + color * 50); result[pixel + 3] = 255;
        }
        return result;
    }

    // TIFF格納と固定色だけを構築し、比較・整列・製品期待値の採取はしない。
    private static void WriteTiff(string path, int side, int pages)
    {
        const int entryCount = 11, rawLength = 100 * 200 * 4;
        const int directoryLength = 2 + entryCount * 12 + 4, pageLength = directoryLength + 8 + rawLength;
        using var file = File.Create(path); using var writer = new BinaryWriter(file);
        writer.Write((byte)'I'); writer.Write((byte)'I'); writer.Write((ushort)42); writer.Write(8U);
        var raw = RawBgra(side);
        for (var page = 0; page < pages; page++)
        {
            var directory = checked(8U + (uint)(page * pageLength)); var bits = directory + directoryLength; var pixels = bits + 8;
            writer.Write((ushort)entryCount);
            Tag(256, 4, 1, 100); Tag(257, 4, 1, 200); Tag(258, 3, 4, bits); Tag(259, 3, 1, 1);
            Tag(262, 3, 1, 2); Tag(273, 4, 1, pixels); Tag(277, 3, 1, 4); Tag(278, 4, 1, 200);
            Tag(279, 4, 1, rawLength); Tag(284, 3, 1, 1); Tag(338, 3, 1, 2);
            writer.Write(page + 1 < pages ? directory + pageLength : 0U);
            for (var sample = 0; sample < 4; sample++) writer.Write((ushort)8);
            for (var pixel = 0; pixel < raw.Length; pixel += 4)
            { writer.Write(raw[pixel + 2]); writer.Write(raw[pixel + 1]); writer.Write(raw[pixel]); writer.Write(raw[pixel + 3]); }
        }
        void Tag(ushort tag, ushort type, uint count, uint value)
        { writer.Write(tag); writer.Write(type); writer.Write(count); writer.Write(value); }
    }
    private static byte[] RawBgra(int side)
    {
        var result = new byte[100 * 200 * 4];
        for (var row = 0; row < 200; row++) for (var x = 0; x < 100; x++)
        {
            var color = side + (row >= 100 ? 1 : 0); var pixel = (row * 100 + x) * 4;
            result[pixel] = (byte)(30 + color * 50); result[pixel + 1] = (byte)(20 + color * 50);
            result[pixel + 2] = (byte)(10 + color * 50); result[pixel + 3] = 255;
        }
        return result;
    }
    private static string Label(string value) => "image-insertion-budget-" + value;
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
