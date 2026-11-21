using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessImageOffsetChecks
{
    // 失敗先行: 座標の正規化/変換順/コピー拡張/Undo時の位置保持、rawとcanvasの混同、
    // readonly表示ボタンの誤拒否とcopyの誤許可、取消/巨大位置による状態破壊、保存での原画変質。
    // 期待BGRAと座標は固定原本12case/43stateだけを使い、描画用透明paddingだけを投影する。
    internal static void Run(ComparisonPane pane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        using var resource = typeof(HeadlessImageOffsetChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.ImageOffsets.json")
            ?? throw new InvalidDataException("画像位置原本fixtureがありません。");
        using var buffer = new MemoryStream(); resource.CopyTo(buffer); var bytes = buffer.ToArray();
        const string pinned = "89C0CECCEFAC194294200E1EB227595C223E9FB96897FF2066113877DFD48E50";
        check("image offsets GUI canonical SHA", Hash(bytes) == pinned, "12 cases / 43 states");
        if (Hash(bytes) != pinned) throw new InvalidDataException("画像位置原本SHAが一致しません。");
        using var document = JsonDocument.Parse(bytes);
        var root = Path.Combine(output, "image-offsets"); Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "golden.json"), bytes);
        var observations = Path.Combine(root, "view-observations.ndjson"); File.WriteAllText(observations, "");
        File.WriteAllText(Path.Combine(root, "failure-contract.txt"), "原本12case/43stateの座標・方向・全BGRA・regions・dirty・Undo/Redo、readonly矢印とcopy拒否、取消/不正位置の不変、PNG原画保存を実比較タブで照合する。通常desktop/OS操作は未検証。\n");
        var stateCount = 0; var cases = 0;
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            cases++; var name = item.GetProperty("name").GetString()!; var inputs = item.GetProperty("inputs");
            var count = inputs.GetArrayLength(); var paths = new string[count];
            var ro = item.GetProperty("readOnly").EnumerateArray().Select(p => p.GetInt32() != 0).ToArray();
            for (var p = 0; p < count; p++)
            {
                paths[p] = Path.Combine(root, name + "-input-" + p + ".png");
                File.WriteAllBytes(paths[p], Convert.FromBase64String(inputs[p].GetProperty("pngBase64").GetString()!));
                check("image offsets GUI " + name + " input SHA " + p, Hash(File.ReadAllBytes(paths[p])) == inputs[p].GetProperty("pngSha256").GetString(), "");
            }
            var originalHashes = paths.Select(path => Hash(File.ReadAllBytes(path))).ToArray(); var attrs = paths.Select(File.GetAttributes).ToArray();
            pane.DiscardChanges();
            pane.ApplyProject(new() { Mode = "Image", LeftPath = paths[0], BasePath = count == 3 ? paths[1] : "", RightPath = paths[^1],
                LeftReadOnly = ro[0], BaseReadOnly = count == 3 && ro[1], RightReadOnly = ro[^1],
                ImageSettings = new() { BlockSize = item.GetProperty("blockSize").GetInt32(), Zoom = 8, ShowDifferences = false, ReportAllFrames = false } });
            pump(pane.ComparePathsAsync()); var panel = Panel(); var step = 0; var historyCreated = false;
            State(item.GetProperty("states")[step], name, step, panel, historyCreated);
            foreach (var action in item.GetProperty("actions").EnumerateArray())
            {
                step++; var kind = action[0].GetString(); var src = action[1].GetInt32(); var dst = action[2].GetInt32(); var value = action[3].GetInt32();
                var expected = item.GetProperty("states")[step];
                if (kind == "offset") pump(panel.AddOffsetAsync(dst, src, value));
                else if (kind is "rotate" or "flipx" or "flipy")
                {
                    var orientation = panel.CaptureSettings().Orientations(count == 3)[dst];
                    pump(panel.SetOrientationAsync(dst, kind switch { "rotate" => orientation with { Rotation = value },
                        "flipx" => orientation with { FlipHorizontal = value != 0 }, _ => orientation with { FlipVertical = value != 0 } }));
                }
                else if (kind is "copy" or "all")
                {
                    if (kind == "copy") Select(panel, value);
                    if (ro[dst])
                    {
                        var before = Stamp(panel); var rejected = false;
                        try { pump(panel.CopyRegionAsync(src, dst, all: kind == "all")); } catch (InvalidOperationException) { rejected = true; }
                        check("image offsets GUI " + name + " readonly copy rejected", rejected && Stamp(panel) == before, "GUI rejects original readonly no-op; pixels remain identical");
                    }
                    else { pump(panel.CopyRegionAsync(src, dst, all: kind == "all")); historyCreated |= expected.GetProperty("undoable").GetBoolean(); }
                }
                else if (kind == "undo") pump(panel.UndoEditAsync());
                else if (kind == "redo") pump(panel.RedoEditAsync());
                else if (kind == "save")
                {
                    var raw = panel.CaptureEditFrames()[dst]; var png = Path.Combine(root, name + "-saved.png"); pump(panel.SaveToAsync(dst, png));
                    check("image offsets GUI " + name + " save raw PNG", Same(raw, HeadlessImageCopyChecks.ReadPng(png)), "independent PNG full bytes; no canvas padding");
                    var project = pane.CaptureProject(); var savedPath = dst == 0 ? project.LeftPath : dst == count - 1 ? project.RightPath : project.BasePath;
                    check("image offsets GUI " + name + " save project path", Path.GetFullPath(savedPath) == Path.GetFullPath(png), savedPath);
                }
                else throw new InvalidDataException("未対応の画像位置原本操作です。");
                State(expected, name, step, panel, historyCreated);
            }
            if (name is "normalize-three" or "copy-top-left" or "history-offset") screenshot("image-offset-" + name + ".png");
            // 保存PNGを増やさず、変換付きrawの最終exportも代表1枚だけ独立再読込みする。
            if (name == "orientation-first")
            {
                var raw = panel.CaptureEditFrames()[1]; var path = Path.Combine(root, "orientation-first-raw.png"); pump(panel.SaveToAsync(1, path));
                var decoded = HeadlessImageCopyChecks.ReadPng(path); var expected = item.GetProperty("exports")[1];
                check("image offsets GUI transformed raw export", Same(raw, decoded)
                    && decoded.Width == expected.GetProperty("width").GetInt32() && decoded.Height == expected.GetProperty("height").GetInt32()
                    && decoded.Pixels.AsSpan().SequenceEqual(Convert.FromHexString(expected.GetProperty("bgraHex").GetString()!)), "raw export independently decoded");
            }
            for (var p = 0; p < count; p++) check("image offsets GUI " + name + " input preserved " + p,
                Hash(File.ReadAllBytes(paths[p])) == originalHashes[p] && File.GetAttributes(paths[p]) == attrs[p], "bytes and attributes");
        }
        check("image offsets GUI all original states", cases == 12 && stateCount == 43, $"cases={cases}; states={stateCount}");
        var current = Panel(); current.SetReadOnly([true, true]);
        var chooser = current.GetVisualDescendants().OfType<ComboBox>().Single(p => p.Name == "ImageEditPane"); chooser.SelectedIndex = 1;
        var beforeButtons = Stamp(current); var history = current.HistoryIndex; var historyCount = current.HistoryCount;
        var settings = current.CaptureSettings();
        foreach (var buttonName in new[] { "ImageOffsetRight", "ImageOffsetDown", "ImageOffsetLeft", "ImageOffsetUp" })
        {
            var button = current.GetVisualDescendants().OfType<Button>().Single(p => p.Name == buttonName);
            check("image offsets GUI readonly " + buttonName + " enabled", button.IsEnabled, "display position changes remain allowed");
            var prior = current.CaptureSettings().Offsets(false); var delta = buttonName switch { "ImageOffsetRight" => (X: 1, Y: 0),
                "ImageOffsetDown" => (X: 0, Y: 1), "ImageOffsetLeft" => (X: -1, Y: 0), _ => (X: 0, Y: -1) };
            var movedX = prior.Select((p, i) => p.X + (i == 1 ? delta.X : 0)).ToArray();
            var movedY = prior.Select((p, i) => p.Y + (i == 1 ? delta.Y : 0)).ToArray();
            var minX = movedX.Min(); var minY = movedY.Min();
            var expected = movedX.Select((x, i) => new ImageOffset(x - minX, movedY[i] - minY)).ToArray();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump(current.CurrentFrameOperation);
            check("image offsets GUI readonly " + buttonName + " coordinates", current.CaptureSettings().Offsets(false).SequenceEqual(expected)
                && current.HistoryIndex == history && current.HistoryCount == historyCount, "actual button updates position without editing history");
        }
        check("image offsets GUI readonly arrows roundtrip", Stamp(current) == beforeButtons && current.CaptureSettings() == settings, "all frame bytes/settings/dirty/history preserved after inverse moves");
        screenshot("image-offset-readonly-arrows.png");
        var stable = Stamp(current);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var rejected = false;
            try { pump(current.AddOffsetAsync(0, 1, 0, cancelled.Token)); } catch (OperationCanceledException) { rejected = true; }
            check("image offsets GUI precancel unchanged", rejected && Stamp(current) == stable, "");
        }
        foreach (var bad in new[] { (Pane: 2, X: 1, Y: 0), (Pane: 1, X: int.MaxValue, Y: 0), (Pane: 1, X: int.MinValue, Y: 0), (Pane: 1, X: 10000, Y: 10000) })
        {
            var rejected = false;
            try { pump(current.AddOffsetAsync(bad.Pane, bad.X, bad.Y)); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException) { rejected = true; }
            check("image offsets GUI invalid position unchanged " + bad, rejected && Stamp(current) == stable, "no candidate adoption or large allocation");
        }
        // 古い完了が最新の位置を巻き戻さず、多ページでも方向と位置を一緒に復元する。
        var obsolete = current.AddOffsetAsync(1, 1, 0); var latest = current.AddOffsetAsync(1, 0, 1);
        try { pump(obsolete); } catch (OperationCanceledException) { } pump(latest);
        check("image offsets GUI obsolete keeps latest", current.CaptureSettings().RightOffset == settings.RightOffset with { X = settings.RightOffset.X + 1, Y = settings.RightOffset.Y + 1 }, "");
        pane.DiscardChanges();
        var pages = Path.Combine(output, "tiff", "two-pages-le.tif");
        var multiSettings = new ImageViewSettings { LeftOrientation = new() { Rotation = 90, FlipVertical = true }, RightOrientation = new() { Rotation = 270 },
            LeftOffset = new(0, 2), RightOffset = new(3, 0), BlockSize = 1, Zoom = 8, ShowDifferences = false, ReportAllFrames = false };
        pane.ApplyProject(new() { LeftPath = pages, RightPath = pages, Mode = "Image", ImageSettings = multiSettings }); pump(pane.ComparePathsAsync());
        var multi = Panel(); pump(multi.SetFramesAsync(2, 2));
        check("image offsets GUI TIFF page retains position", multi.CaptureSettings().Offsets(false).SequenceEqual(multiSettings.Offsets(false))
            && multi.CaptureSettings().Orientations(false).SequenceEqual(multiSettings.Orientations(false)) && !multi.HasUnsavedChanges, "");
        var projectPath = Path.Combine(root, "project.json"); pump(WorkspaceStore.SaveAsync(projectPath, pane.CaptureProject()));
        var load = WorkspaceStore.LoadAsync(projectPath); pump(load); var loaded = load.Result;
        var beforeReload = multi.RenderedFrames.ToArray();
        pane.ApplyProject(loaded); pump(pane.ComparePathsAsync()); var restored = Panel();
        check("image offsets GUI project roundtrip", restored.CaptureSettings() == loaded.ImageSettings
            && restored.RenderedFrames.Count == beforeReload.Length && restored.RenderedFrames.Select((frame, i) => Same(frame, beforeReload[i])).All(p => p), "settings and full canvas bytes");
        pump(pane.SaveReportAsync(Path.Combine(root, "report.html"))); screenshot("image-offset-tiff.png");
        pane.DiscardChanges();

        SpecializedViews.ImagePanel Panel() => pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
        void Select(SpecializedViews.ImagePanel panel, int index)
        {
            for (var i = 0; i <= panel.DifferenceCount && panel.SelectedDiffIndex != index; i++) pump(panel.NavigateRegionAsync(1));
            if (panel.SelectedDiffIndex != index) throw new InvalidOperationException("画像位置のコピー領域を選択できません。");
        }
        void State(JsonElement expected, string name, int step, SpecializedViews.ImagePanel panel, bool historyCreated)
        {
            stateCount++; var panes = expected.GetProperty("panes"); var count = panes.GetArrayLength();
            var rendered = panel.RenderedFrames; var captured = panel.CaptureSettings(); var positions = captured.Offsets(count == 3); var orientations = captured.Orientations(count == 3);
            var prefix = "image offsets GUI " + name + " state " + step;
            check(prefix + " regions", panel.DifferenceCount == expected.GetProperty("differenceCount").GetInt32()
                && panel.ConflictCount == (count == 3 ? expected.GetProperty("conflictCount").GetInt32() : 0), "two-pane GUI hides conflict count");
            check(prefix + " history", panel.GetVisualDescendants().OfType<Button>().Single(p => p.Name == "ImageUndo").IsEnabled == expected.GetProperty("undoable").GetBoolean()
                && panel.GetVisualDescendants().OfType<Button>().Single(p => p.Name == "ImageRedo").IsEnabled == expected.GetProperty("redoable").GetBoolean()
                && panel.HistoryCount == (historyCreated ? 1 : 0) && panel.HistoryIndex == (expected.GetProperty("undoable").GetBoolean() ? 0 : -1), "fixtures contain at most one successful edit; offsets and orientation are outside shared edit history");
            check(prefix + " dirty", panel.HasUnsavedChanges == panes.EnumerateArray().Any(p => p.GetProperty("modified").GetBoolean()), "");
            var width = panes.EnumerateArray().Max(p => p.GetProperty("width").GetInt32() + p.GetProperty("offsetX").GetInt32());
            var height = panes.EnumerateArray().Max(p => p.GetProperty("height").GetInt32() + p.GetProperty("offsetY").GetInt32());
            var records = new List<OffsetFrameEvidence>();
            check(prefix + " frame count", rendered.Count == count, "");
            for (var p = 0; p < count; p++)
            {
                var original = panes[p]; var w = original.GetProperty("width").GetInt32(); var h = original.GetProperty("height").GetInt32();
                var x = original.GetProperty("offsetX").GetInt32(); var y = original.GetProperty("offsetY").GetInt32();
                var raw = Convert.FromHexString(original.GetProperty("bgraHex").GetString()!); var padded = new byte[width * height * 4];
                for (var row = 0; row < h; row++) raw.AsSpan(row * w * 4, w * 4).CopyTo(padded.AsSpan(((y + row) * width + x) * 4));
                var frame = rendered[p];
                check(prefix + " canvas BGRA " + p, frame.Width == width && frame.Height == height && frame.Pixels.AsSpan().SequenceEqual(padded), "all original BGRA at offset; zero transparent canvas padding");
                check(prefix + " offset " + p, positions[p].X == x && positions[p].Y == y, "");
                check(prefix + " orientation " + p, orientations[p].Rotation == original.GetProperty("angle").GetInt32()
                    && orientations[p].FlipHorizontal == original.GetProperty("flipx").GetBoolean() && orientations[p].FlipVertical == original.GetProperty("flipy").GetBoolean(), "");
                check(prefix + " pane dirty " + p, panel.PaneModified(p) == original.GetProperty("modified").GetBoolean(), "");
                records.Add(new(p, frame.Width, frame.Height, Convert.ToBase64String(frame.Pixels), Hash(frame.Pixels), positions[p].X, positions[p].Y,
                    orientations[p].Rotation, orientations[p].FlipHorizontal, orientations[p].FlipVertical, panel.PaneModified(p), original.GetProperty("savepoint").GetInt32()));
            }
            var project = pane.CaptureProject().ImageSettings!;
            check(prefix + " project coordinates", project.Offsets(count == 3).SequenceEqual(positions), "actual comparison project captures applied settings");
            File.AppendAllText(observations, JsonSerializer.Serialize(new OffsetStateEvidence(name, step, panel.DifferenceCount, panel.ConflictCount,
                panel.HistoryIndex, panel.HistoryCount, panel.HasUnsavedChanges, records.ToArray(),
                panel.CaptureEditFrames().Select(p => new OffsetRawEvidence(p.Width, p.Height, Convert.ToBase64String(p.Pixels), Hash(p.Pixels))).ToArray()),
                OffsetEvidenceJsonContext.Default.OffsetStateEvidence) + "\n");
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool Same(ImageComparisonEngine.DecodedFrame a, ImageComparisonEngine.DecodedFrame b) => a.Width == b.Width && a.Height == b.Height && a.Pixels.AsSpan().SequenceEqual(b.Pixels);
    private static string Stamp(SpecializedViews.ImagePanel panel) => JsonSerializer.Serialize(new OffsetStampEvidence(panel.CaptureSettings(), panel.HistoryIndex, panel.HistoryCount,
        panel.HasUnsavedChanges, panel.CaptureEditFrames().Select(p => new OffsetRawEvidence(p.Width, p.Height, "", Hash(p.Pixels))).ToArray(),
        panel.RenderedFrames.Select(p => new OffsetRawEvidence(p.Width, p.Height, "", Hash(p.Pixels))).ToArray()), OffsetEvidenceJsonContext.Default.OffsetStampEvidence);
}

internal sealed record OffsetFrameEvidence(int Pane, int Width, int Height, string BgraBase64, string Sha256, int X, int Y,
    int Rotation, bool FlipHorizontal, bool FlipVertical, bool Modified, int OriginalSavepoint);
internal sealed record OffsetRawEvidence(int Width, int Height, string BgraBase64, string Sha256);
internal sealed record OffsetStateEvidence(string Case, int State, int DifferenceCount, int ConflictCount, int HistoryIndex,
    int HistoryCount, bool HasUnsavedChanges, OffsetFrameEvidence[] Frames, OffsetRawEvidence[] RawFrames);
internal sealed record OffsetStampEvidence(ImageViewSettings Settings, int HistoryIndex, int HistoryCount, bool HasUnsavedChanges,
    OffsetRawEvidence[] Raw, OffsetRawEvidence[] Rendered);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OffsetStateEvidence))]
[JsonSerializable(typeof(OffsetStampEvidence))]
internal partial class OffsetEvidenceJsonContext : JsonSerializerContext;
