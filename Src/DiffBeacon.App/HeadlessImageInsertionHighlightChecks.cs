using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessImageInsertionHighlightChecks
{
    // 失敗先行: ghost/透明実画素・通常色/選択色の混同、削除intervalとoffsetの誤投影、
    // 三者paneの色混同、強調画素の原画混入、readonly比較の誤拒否、原本改変。
    // 固定WinIMerge v1.0.54の公開API二回採取。GPL-2.0-or-later、入力は自作CC0。
    // GUIの固定alpha .7だけを照合し、alpha0/.3/1をGUI検証済み扱いにしない。
    internal static void Run(ComparisonPane pane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        using var resource = typeof(HeadlessImageInsertionHighlightChecks).Assembly
            .GetManifestResourceStream("DiffBeacon.ImageInsertionHighlight.Golden.json.gz")
            ?? throw new InvalidDataException("画像挿入削除強調原本fixtureがありません。");
        using var packed = new MemoryStream(); resource.CopyTo(packed); var zip = packed.ToArray();
        const string gzipSha = "4636258D1C81E7D45CDBC2343334CC36FC7215E63490132B775E4338340D2C81";
        const string jsonSha = "FA89130794BD54918CF95B8D701729B61840B0105263C8D05C48710749F37B01";
        const string repeatSha = "AC8E457D3D718B63663CCA99E70E619F7F4446C783C9DD880B3884F978833674";
        Fixture("gzip SHA", Hash(zip) == gzipSha, Hash(zip));
        packed.Position = 0; using var gzip = new GZipStream(packed, CompressionMode.Decompress);
        using var content = new MemoryStream(); gzip.CopyTo(content); var bytes = content.ToArray();
        Fixture("JSON SHA", Hash(bytes) == jsonSha, Hash(bytes));
        using var document = JsonDocument.Parse(bytes); var golden = document.RootElement;
        Fixture("repeat provenance", golden.GetProperty("observationsSha256").GetString() == repeatSha
            && golden.GetProperty("sourceRevision").GetString() == "da639cdfaeca87aaad0eaceec509afa11ad61421"
            && golden.GetProperty("repeatEvidence").GetProperty("runs").GetInt32() == 2
            && golden.GetProperty("repeatEvidence").GetProperty("identicalCaseFiles").GetInt32() == 720,
            "two independent original DLL runs; 720 byte-identical files");
        Fixture("source and license provenance", golden.GetProperty("sourceSha256").GetString() == "7071EDFA31CCC138685CA7B9FD3D41EE5E84B942FB3D8028C7E3A31C76F93F28"
            && golden.GetProperty("dllSha256").GetString() == "36F2A726C34A2323D2E569903B4F1DC6C28ABF1C10447006335F2785FC511BA6"
            && golden.GetProperty("publicHeaderSha256").GetString() == "597902D5FFBE8585524C86C41E73032C607E42E04E65F90F0441E521F84DB4F4"
            && golden.GetProperty("license").GetString() == "GPL-2.0-or-later; synthetic input PNGs CC0-1.0"
            && golden.GetProperty("algorithm").GetString() == "Myers", "fixed source/DLL/public header and original licensing");
        var root = Path.Combine(output, "image-insertion-highlight"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "gui-failure-contract.md"),
            "GUI固定alpha .7: 全12件62状態のNONE/垂直/水平・未選択/先頭/末尾・offset・透明実画素・三者について、実WriteableBitmap全canvas BGRAを二回採取原本literalへ照合する。設定はApplySettingsAsync、選択は実領域ナビゲーションを使う。原画全BGRAと入力PNG bytes/属性・readonlyを保持する。\n"
            + "原本146状態中、alpha0/.3/1の84状態はGUIのalpha操作がないためGUI検証対象外。期待値の再生成・golden複製は行わない。OS操作/通常desktop描画は未検証。\n");
        using var evidence = new StreamWriter(Path.Combine(root, "gui-observations.ndjson"), false, new UTF8Encoding(false));
        var cases = 0; var states = 0; var totalStates = 0;
        foreach (var item in golden.GetProperty("cases").EnumerateArray())
        {
            cases++; var name = item.GetProperty("name").GetString()!; var inputs = item.GetProperty("inputs"); var count = inputs.GetArrayLength();
            var paths = new string[count]; var inputHashes = new string[count]; var attributes = new FileAttributes[count];
            var readOnly = item.GetProperty("readOnly").EnumerateArray().Select(value => value.GetInt32() != 0).ToArray();
            for (var p = 0; p < count; p++)
            {
                paths[p] = Path.Combine(root, name + "-input-" + p + ".png"); var png = Convert.FromBase64String(inputs[p].GetProperty("pngBase64").GetString()!);
                inputHashes[p] = Hash(png); Fixture(name + " input PNG SHA " + p, inputHashes[p] == inputs[p].GetProperty("pngSha256").GetString(), inputHashes[p]);
                File.WriteAllBytes(paths[p], png); attributes[p] = File.GetAttributes(paths[p]);
                var decoded = HeadlessImageCopyChecks.ReadPng(paths[p]);
                Fixture(name + " independent input PNG " + p, MatchesRaw(decoded, inputs[p]), "independent dimensions and every original raw BGRA byte");
                if (readOnly[p]) File.SetAttributes(paths[p], attributes[p] | FileAttributes.ReadOnly);
            }
            try
            {
                pane.DiscardChanges();
                pane.ApplyProject(new() { Mode = "Image", LeftPath = paths[0], RightPath = paths[^1], BasePath = count == 3 ? paths[1] : "",
                    LeftReadOnly = readOnly[0], RightReadOnly = readOnly[^1], BaseReadOnly = count == 3 && readOnly[1],
                    ImageSettings = new() { BlockSize = item.GetProperty("blockSize").GetInt32(), Threshold = item.GetProperty("threshold").GetDouble(),
                        ShowDifferences = true, ReportAllFrames = false, Zoom = 8 } });
                pump(pane.ComparePathsAsync()); var panel = Panel();
                foreach (var state in item.GetProperty("states").EnumerateArray())
                {
                    totalStates++; if (state.GetProperty("highlightAlpha").GetDouble() != .7) continue;
                    states++; var index = state.GetProperty("stateIndex").GetInt32(); var wanted = state.GetProperty("panes");
                    var selected = state.GetProperty("currentDiffIndex").GetInt32(); var mode = state.GetProperty("mode").GetInt32();
                    var offsets = wanted.EnumerateArray().Select(value => new ImageOffset(value.GetProperty("offsetX").GetInt32(), value.GetProperty("offsetY").GetInt32())).ToArray();
                    var orientations = wanted.EnumerateArray().Select(value => new ImageOrientation { Rotation = value.GetProperty("angle").GetInt32(),
                        FlipHorizontal = value.GetProperty("flipx").GetBoolean(), FlipVertical = value.GetProperty("flipy").GetBoolean() }).ToArray();
                    var settings = panel.CaptureSettings() with { InsertionDeletionMode = mode, ShowDifferences = true,
                        Threshold = item.GetProperty("threshold").GetDouble(), BlockSize = item.GetProperty("blockSize").GetInt32(),
                        LeftOffset = offsets[0], RightOffset = offsets[^1], MiddleOffset = count == 3 ? offsets[1] : default,
                        LeftOrientation = orientations[0], RightOrientation = orientations[^1], MiddleOrientation = count == 3 ? orientations[1] : new() };
                    if (selected < 0 && panel.SelectedDiffIndex >= 0)
                    {
                        // GUIの初期未選択表示を比較する。原本APIの選択解除操作と混同しない。
                        var project = pane.CaptureProject() with { ImageSettings = settings };
                        pane.DiscardChanges(); pane.ApplyProject(project); pump(pane.ComparePathsAsync()); panel = Panel();
                    }
                    else pump(panel.ApplySettingsAsync(settings));
                    if (selected >= 0)
                        for (var i = 0; i <= panel.DifferenceCount && panel.SelectedDiffIndex != selected; i++) pump(panel.NavigateRegionAsync(1));
                    var prefix = "image insertion highlight GUI " + name + " state " + index;
                    check(prefix + " settings", panel.CaptureSettings() == settings && panel.CaptureReport().InsertionDeletionMode == mode,
                        "adopted settings include mode, full threshold, block size and offsets");
                    check(prefix + " classification and selection", panel.DifferenceCount == state.GetProperty("differenceCount").GetInt32()
                        && panel.ConflictCount == (count == 3 ? state.GetProperty("conflictCount").GetInt32() : 0)
                        && panel.SelectedDiffIndex == selected, "actual first/last region navigation; two-pane GUI hides conflicts");
                    var sideNames = count == 3 ? new[] { "Left", "Middle", "Right" } : ["Left", "Right"];
                    var rendered = panel.RenderedFrames; var raw = panel.CaptureEditFrames();
                    using var buffer = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(buffer))
                    {
                        writer.WriteStartObject(); writer.WriteString("case", name); writer.WriteNumber("state", index); writer.WriteNumber("mode", mode);
                        writer.WriteNumber("highlightAlpha", .7); writer.WriteNumber("selectedDiffIndex", panel.SelectedDiffIndex);
                        writer.WriteNumber("differenceCount", panel.DifferenceCount); writer.WriteNumber("conflictCount", panel.ConflictCount);
                        writer.WriteStartArray("panes");
                        for (var p = 0; p < count; p++)
                        {
                            var oracle = wanted[p]; var expectedBgra = Convert.FromHexString(oracle.GetProperty("bgraHex").GetString()!);
                            Fixture(name + " state " + index + " literal BGRA SHA " + p, Hash(expectedBgra) == oracle.GetProperty("bgraSha256").GetString()
                                && Hash(Convert.FromBase64String(oracle.GetProperty("pngBase64").GetString()!)) == oracle.GetProperty("pngSha256").GetString(), "original highlighted PNG and decoded literal SHA");
                            var bitmap = panel.GetVisualDescendants().OfType<Image>().Single(image => image.Name == "ImagePane" + sideNames[p]).Source as WriteableBitmap
                                ?? throw new InvalidOperationException("強調比較の実Bitmapがありません。");
                            var pixels = ReadPixels(bitmap); var width = oracle.GetProperty("canvasWidth").GetInt32(); var height = oracle.GetProperty("canvasHeight").GetInt32();
                            check(prefix + " actual Bitmap all BGRA " + p, bitmap.PixelSize.Width == width && bitmap.PixelSize.Height == height && pixels.AsSpan().SequenceEqual(expectedBgra),
                                $"{bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}; SHA={Hash(pixels)}");
                            check(prefix + " rendered frame all BGRA " + p, rendered[p].Width == width && rendered[p].Height == height
                                && rendered[p].Pixels.AsSpan().SequenceEqual(expectedBgra), "literal original full canvas; ghost/selected colors/hidden RGB");
                            check(prefix + " original raw retained " + p, MatchesRaw(raw[p], inputs[p]) && !panel.PaneModified(p)
                                && panel.HistoryCount == 0 && !panel.HasUnsavedChanges, "display highlighting and selection never edit raw history");
                            writer.WriteStartObject(); writer.WriteNumber("pane", p); writer.WriteNumber("width", bitmap.PixelSize.Width); writer.WriteNumber("height", bitmap.PixelSize.Height);
                            writer.WriteBase64String("bgraBase64", pixels); writer.WriteString("sha256", Hash(pixels)); writer.WriteString("rawSha256", Hash(raw[p].Pixels));
                            writer.WriteNumber("offsetX", offsets[p].X); writer.WriteNumber("offsetY", offsets[p].Y); writer.WriteBoolean("readOnly", readOnly[p]); writer.WriteEndObject();
                        }
                        writer.WriteEndArray(); writer.WriteEndObject();
                    }
                    evidence.WriteLine(Encoding.UTF8.GetString(buffer.ToArray())); evidence.Flush();
                    if (name == "1-insert-end" && index == 2 || name == "1-two-regions" && selected == 1 || name == "2-transparent-middle" && index == 8)
                        screenshot("image-insertion-highlight-" + name + ".png");
                }
                var exported = panel.CaptureEditFrames();
                for (var p = 0; p < count; p++)
                {
                    check("image insertion highlight GUI " + name + " final raw export " + p, MatchesRaw(exported[p], item.GetProperty("exports")[p]), "original final SaveImageAs whole raw BGRA");
                    var expectedAttributes = readOnly[p] ? attributes[p] | FileAttributes.ReadOnly : attributes[p];
                    check("image insertion highlight GUI " + name + " input preserved " + p, Hash(File.ReadAllBytes(paths[p])) == inputHashes[p]
                        && File.GetAttributes(paths[p]) == expectedAttributes, "original PNG bytes and readonly attributes");
                }
            }
            finally { for (var p = 0; p < count; p++) File.SetAttributes(paths[p], attributes[p]); }
        }
        check("image insertion highlight GUI fixed alpha coverage", cases == 12 && states == 62 && totalStates == 146,
            $"cases={cases}; alpha .7 states={states}; original states={totalStates}; alpha0/.3/1 excluded from GUI");
        using (var file = File.Create(Path.Combine(root, "gui-coverage.json")))
        using (var writer = new Utf8JsonWriter(file, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("gzipSha256", gzipSha); writer.WriteString("sourceSha256", jsonSha); writer.WriteString("repeatObservationsSha256", repeatSha);
            writer.WriteNumber("cases", cases); writer.WriteNumber("guiStates", states); writer.WriteNumber("originalStates", totalStates); writer.WriteNumber("guiHighlightAlpha", .7);
            writer.WriteString("unverifiedGuiAlpha", "0/.3/1"); writer.WriteString("source", "tests/Fixtures/ImageInsertionHighlight/README.md"); writer.WriteEndObject();
        }
        pane.DiscardChanges();

        SpecializedViews.ImagePanel Panel() => pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
        void Fixture(string label, bool success, string details)
        {
            check("image insertion highlight GUI original " + label, success, details);
            if (!success) throw new InvalidDataException("画像挿入削除強調原本の整合性が失われました: " + label);
        }
    }
    private static byte[] ReadPixels(WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock(); var width = bitmap.PixelSize.Width; var height = bitmap.PixelSize.Height; var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++) Marshal.Copy(IntPtr.Add(buffer.Address, y * buffer.RowBytes), pixels, y * width * 4, width * 4);
        return pixels;
    }
    private static bool MatchesRaw(ImageComparisonEngine.DecodedFrame frame, JsonElement expected) => frame.Width == expected.GetProperty("width").GetInt32()
        && frame.Height == expected.GetProperty("height").GetInt32() && frame.Pixels.AsSpan().SequenceEqual(Convert.FromHexString(expected.GetProperty("bgraHex").GetString()!));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
