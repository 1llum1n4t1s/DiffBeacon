using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessImageTransformChecks
{
    // 表示とrawの混同、readonly表示の拒否、変換コピー/履歴/保存の座標、ページ/設定/取消/世代の消失を検査する。
    internal static void Run(ComparisonPane pane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        byte[] Resource(string name)
        {
            using var stream = typeof(HeadlessImageTransformChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.ImageTransforms." + name)
                ?? throw new InvalidDataException("画像変換fixtureがありません。");
            using var data = new MemoryStream(); stream.CopyTo(data); return data.ToArray();
        }
        var packed = Resource("json.gz");
        using var compressed = new MemoryStream(packed); using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var plain = new MemoryStream(); var buffer = new byte[65536]; int read;
        while ((read = gzip.Read(buffer)) != 0)
        { if (plain.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("画像変換fixtureが上限を超えます。"); plain.Write(buffer, 0, read); }
        var bytes = plain.ToArray();
        check("image transform GUI canonical fixture SHA", Convert.ToHexString(SHA256.HashData(bytes)) == "5383447EA3FF48F5C1F1A99F6CFFF8438568ED68445F5BB59FD4C2BC31FEFD82", "");
        using var json = JsonDocument.Parse(bytes); var cases = json.RootElement.GetProperty("cases");
        var root = Path.Combine(output, "image-transforms"); Directory.CreateDirectory(root);
        var observations = Path.Combine(root, "view-observations.ndjson"); File.WriteAllText(observations, "");
        var input = Path.Combine(root, "input.png"); File.WriteAllBytes(input, Resource("input.png"));
        var inputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));
        check("image transform GUI input SHA", inputHash == json.RootElement.GetProperty("input").GetProperty("pngSha256").GetString(), "");
        var names = new[] { "display-003", "display-005", "display-010", "display-015", "display-019", "display-021", "display-026", "display-031",
            "copy-017", "copy-055", "copy-103", "copy-155", "copy-240", "copy-255" };
        var evidence = new List<string>();
        foreach (var name in names)
        {
            var item = cases.EnumerateArray().Single(row => row.GetProperty("name").GetString() == name);
            var count = item.GetProperty("paneCount").GetInt32(); var ro = item.GetProperty("readOnly");
            pane.DiscardChanges();
            pane.ApplyProject(new() { LeftPath = input, BasePath = count == 3 ? input : "", RightPath = input, Mode = "Image",
                LeftReadOnly = ro[0].GetBoolean(), BaseReadOnly = count == 3 && ro[1].GetBoolean(), RightReadOnly = ro[count - 1].GetBoolean(),
                ImageSettings = new() { BlockSize = 1, Zoom = 8, ShowDifferences = false, ReportAllFrames = false } });
            pump(pane.ComparePathsAsync()); var panel = Panel(); var index = 0;
            State(item.GetProperty("states")[index], name, index, panel);
            foreach (var action in item.GetProperty("actions").EnumerateArray())
            {
                index++; var kind = action.GetProperty("kind").GetString();
                var destination = action.TryGetProperty("dst", out var dst) ? dst.GetInt32() : 0;
                if (kind is "rotate" or "flipx" or "flipy")
                {
                    var orientation = panel.CaptureSettings().Orientations(count == 3)[destination];
                    var value = action.GetProperty("index").GetInt32();
                    pump(panel.SetOrientationAsync(destination, kind switch
                    {
                        "rotate" => orientation with { Rotation = value }, "flipx" => orientation with { FlipHorizontal = value != 0 },
                        _ => orientation with { FlipVertical = value != 0 }
                    }));
                }
                else if (kind == "all") pump(panel.CopyRegionAsync(action.GetProperty("src").GetInt32(), destination, all: true));
                else if (kind == "undo") pump(panel.UndoEditAsync());
                else if (kind == "redo") pump(panel.RedoEditAsync());
                else if (kind == "save" && !ro[destination].GetBoolean())
                    pump(panel.SaveToAsync(destination, Path.Combine(root, name + "-mark.png")));
                State(item.GetProperty("states")[index], name, index, panel);
            }
            if (name is "display-015" or "display-031" or "copy-103") screenshot("image-transform-" + name + ".png");
            if (!ro.EnumerateArray().Any(value => value.GetBoolean()))
                for (var p = 0; p < count; p++)
                {
                    var path = Path.Combine(root, name + "-raw-" + p + ".png"); pump(panel.SaveToAsync(p, path));
                    var raw = HeadlessImageCopyChecks.ReadPng(path); var expected = item.GetProperty("finalRaw")[p];
                    check(name + " GUI independent raw PNG " + p, Matches(raw, expected), "");
                }
            evidence.Add(name + ":" + index);
        }
        var current = Panel(); var identity = current.CaptureSettings();
        var block = current.GetVisualDescendants().OfType<NumericUpDown>().Single(control => control.Name == "ImageBlockSize");
        var blockHistory = current.HistoryCount;
        block.Value = 1.5m; pump(current.CurrentFrameOperation);
        check("image transform GUI fractional block rejected", block.Value == 1 && current.CaptureSettings().BlockSize == 1, "");
        block.Value = 256; pump(current.CurrentFrameOperation);
        check("image transform GUI block256 preserves history", current.CaptureSettings().BlockSize == 256 && current.HistoryCount == blockHistory, "");
        block.Value = 1; pump(current.CurrentFrameOperation);
        // 実ボタンとreadonly表示を結び、回転だけで保存点やUndoを変えない。
        current.SetReadOnly(Enumerable.Repeat(true, current.CaptureSettings().Orientations(!string.IsNullOrWhiteSpace(pane.BasePath.Text)).Length).ToArray());
        var chooser = current.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "ImageEditPane"); chooser.SelectedIndex = 0;
        var right = current.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "ImageRotateRight");
        var history = current.HistoryCount;
        check("image transform GUI readonly rotate button enabled", right.IsEnabled, "");
        right.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump(current.CurrentFrameOperation);
        check("image transform GUI right button preserves history", current.CaptureSettings().LeftOrientation.Rotation == (identity.LeftOrientation.Rotation + 270) % 360
            && current.HistoryCount == history, "");
        var before = current.CaptureSettings(); using (var cancelled = new CancellationTokenSource())
        { cancelled.Cancel(); try { pump(current.SetOrientationAsync(0, new(), cancelled.Token)); } catch (OperationCanceledException) { }
          check("image transform GUI precancel preserves display", current.CaptureSettings() == before, ""); }
        try { pump(current.SetOrientationAsync(0, new() { Rotation = 45 })); } catch (InvalidDataException) { }
        check("image transform GUI invalid rotation preserves display", current.CaptureSettings() == before, "");
        var old = current.SetOrientationAsync(0, new() { Rotation = 180 }); var latest = current.SetOrientationAsync(0, new() { Rotation = 90, FlipHorizontal = true });
        try { pump(old); } catch (OperationCanceledException) { } pump(latest);
        check("image transform GUI obsolete operation keeps latest", current.CaptureSettings().LeftOrientation == new ImageOrientation { Rotation = 90, FlipHorizontal = true }, "");
        pane.DiscardChanges();
        var pages = Path.Combine(output, "tiff", "two-pages-le.tif");
        var setting = new ImageViewSettings { LeftOrientation = new() { Rotation = 90, FlipVertical = true }, RightOrientation = new() { Rotation = 270 }, BlockSize = 1, Zoom = 8, ReportAllFrames = false };
        pane.ApplyProject(new() { LeftPath = pages, RightPath = pages, Mode = "Image", ImageSettings = setting }); pump(pane.ComparePathsAsync());
        var multi = Panel(); pump(multi.SetFramesAsync(2, 2));
        check("image transform GUI TIFF page retains orientation", multi.CaptureSettings().LeftOrientation == setting.LeftOrientation
            && multi.CaptureSettings().RightOrientation == setting.RightOrientation && !multi.HasUnsavedChanges, "");
        var project = Path.Combine(root, "project.json"); pump(WorkspaceStore.SaveAsync(project, pane.CaptureProject()));
        var load = WorkspaceStore.LoadAsync(project); pump(load); var loaded = load.Result;
        pane.ApplyProject(loaded); pump(pane.ComparePathsAsync());
        check("image transform GUI project roundtrip", Panel().CaptureSettings() == loaded.ImageSettings, "");
        var report = Path.Combine(root, "report.html");
        pump(pane.SaveReportAsync(report));
        screenshot("image-transform-tiff.png");
        check("image transform GUI input bytes unchanged", inputHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))), "");
        File.WriteAllLines(Path.Combine(root, "cases.txt"), evidence);
        File.WriteAllBytes(Path.Combine(root, "project-observation.json"), JsonSerializer.SerializeToUtf8Bytes(pane.CaptureProject(), ProjectJsonContext.Default.ComparisonProject));

        SpecializedViews.ImagePanel Panel() => pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
        void State(JsonElement expected, string name, int step, SpecializedViews.ImagePanel panel)
        {
            var frames = panel.RenderedFrames; var orientations = panel.CaptureSettings().Orientations(expected.GetProperty("frames").GetArrayLength() == 3);
            var prefix = name + " GUI state " + step;
            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject(); writer.WriteString("case", name); writer.WriteNumber("state", step);
                    writer.WriteNumber("differenceCount", panel.DifferenceCount); writer.WriteNumber("conflictCount", panel.ConflictCount);
                    writer.WriteStartArray("frames");
                    for (var p = 0; p < frames.Count; p++)
                    {
                        writer.WriteStartObject(); writer.WriteNumber("width", frames[p].Width); writer.WriteNumber("height", frames[p].Height);
                        writer.WriteBase64String("bgraBase64", frames[p].Pixels); writer.WriteString("sha256", Convert.ToHexString(SHA256.HashData(frames[p].Pixels)));
                        writer.WriteNumber("rotation", orientations[p].Rotation); writer.WriteBoolean("flipHorizontal", orientations[p].FlipHorizontal);
                        writer.WriteBoolean("flipVertical", orientations[p].FlipVertical); writer.WriteBoolean("modified", panel.PaneModified(p)); writer.WriteEndObject();
                    }
                    writer.WriteEndArray(); writer.WriteEndObject();
                }
                File.AppendAllText(observations, System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n");
            }
            check(prefix + " regions", panel.DifferenceCount == expected.GetProperty("differenceCount").GetInt32()
                && panel.ConflictCount == (frames.Count == 3 ? expected.GetProperty("conflictCount").GetInt32() : 0),
                $"regions={panel.DifferenceCount}; conflicts={panel.ConflictCount}; GUI two-pane conflicts are hidden");
            check(prefix + " shared history", panel.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ImageUndo").IsEnabled == expected.GetProperty("undoable").GetBoolean()
                && panel.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ImageRedo").IsEnabled == expected.GetProperty("redoable").GetBoolean(), "");
            for (var p = 0; p < frames.Count; p++)
            {
                check(prefix + " BGRA " + p, MatchesCanvas(frames[p], expected.GetProperty("frames"), p),
                    $"rendered={frames[p].Width}x{frames[p].Height}; transparent padding to shared canvas");
                var orientation = expected.GetProperty("orientations")[p];
                check(prefix + " orientation " + p, orientations[p].Rotation == orientation.GetProperty("rotation").GetInt32()
                    && orientations[p].FlipHorizontal == orientation.GetProperty("flipHorizontal").GetBoolean()
                    && orientations[p].FlipVertical == orientation.GetProperty("flipVertical").GetBoolean(), "");
                check(prefix + " dirty " + p, panel.PaneModified(p) == expected.GetProperty("panes")[p].GetProperty("modified").GetBoolean(), "");
            }
        }
        static bool Matches(ImageComparisonEngine.DecodedFrame frame, JsonElement expected) => frame.Width == expected.GetProperty("width").GetInt32()
            && frame.Height == expected.GetProperty("height").GetInt32() && frame.Pixels.SequenceEqual(Convert.FromBase64String(expected.GetProperty("bgraBase64").GetString()!));
        static bool MatchesCanvas(ImageComparisonEngine.DecodedFrame frame, JsonElement expectedFrames, int pane)
        {
            var width = expectedFrames.EnumerateArray().Max(value => value.GetProperty("width").GetInt32());
            var height = expectedFrames.EnumerateArray().Max(value => value.GetProperty("height").GetInt32());
            var original = expectedFrames[pane]; var rawWidth = original.GetProperty("width").GetInt32();
            var rawHeight = original.GetProperty("height").GetInt32(); var raw = Convert.FromBase64String(original.GetProperty("bgraBase64").GetString()!);
            var padded = new byte[width * height * 4];
            for (var y = 0; y < rawHeight; y++) raw.AsSpan(y * rawWidth * 4, rawWidth * 4).CopyTo(padded.AsSpan(y * width * 4));
            return frame.Width == width && frame.Height == height && frame.Pixels.SequenceEqual(padded);
        }
    }
}
