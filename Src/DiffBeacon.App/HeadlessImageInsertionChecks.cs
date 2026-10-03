using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessImageInsertionChecks
{
    // 失敗先行: ghostの原画混入、整列canvasの画素変質、modeと共有履歴の混同、
    // readonly誤許可、取消/古い完了/保存中の状態破壊、多ページでのmode消失。
    // WinIMerge v1.0.54固定原本58case/304state。GPL-2.0-or-later、入力は自作CC0。
    // 出典とSHAの正本はtests/Fixtures/ImageInsertions/README.md。goldenは複製しない。
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        using var resource = typeof(HeadlessImageInsertionChecks).Assembly.GetManifestResourceStream("DiffBeacon.ImageInsertions.Golden.json.gz")
            ?? throw new InvalidDataException("画像挿入削除原本fixtureがありません。");
        using var packed = new MemoryStream(); resource.CopyTo(packed); var zip = packed.ToArray();
        const string zipSha = "2B0275994E7445F8BF4A745CF7DCE8BBF531C647D43095751F12AE6278A7AF21";
        check("image insertions GUI original gzip SHA", Hash(zip) == zipSha, "58 cases / 304 states");
        if (Hash(zip) != zipSha) throw new InvalidDataException("画像挿入削除gzipのSHAが一致しません。");
        packed.Position = 0;
        using var gzip = new GZipStream(packed, CompressionMode.Decompress);
        using var content = new MemoryStream(); gzip.CopyTo(content); var bytes = content.ToArray();
        const string jsonSha = "991FC3F5CBAA2B039D6320E85AB9ADE0F6BB2CE186FD5AFFC30BA43D4777EDDB";
        check("image insertions GUI original JSON SHA", Hash(bytes) == jsonSha, "original DLL observations; no generated renderer oracle");
        if (Hash(bytes) != jsonSha) throw new InvalidDataException("画像挿入削除JSONのSHAが一致しません。");
        using var golden = JsonDocument.Parse(bytes);
        var root = Path.Combine(output, "image-insertions"); Directory.CreateDirectory(root);
        var observations = Path.Combine(root, "view-observations.ndjson");
        using var evidence = new StreamWriter(observations, false, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "failure-contract.txt"),
            "固定原本58case/304stateの全整列canvas BGRA・差分/競合・mode・方向/位置・dirty・共有Undo/Redo、原画PNG独立再読込み、readonly、設定不正/取消/古い完了/保存中保護、多ページmode、通常/最小viewportを実比較タブで照合する。OS保存ダイアログと通常desktop操作は未検証。\n");
        var cases = 0; var states = 0;
        foreach (var item in golden.RootElement.GetProperty("cases").EnumerateArray())
        {
            cases++; var name = item.GetProperty("name").GetString()!;
            var paths = Inputs(item, root); var hashes = paths.Select(path => Hash(File.ReadAllBytes(path))).ToArray();
            var attributes = paths.Select(File.GetAttributes).ToArray();
            var readOnly = item.GetProperty("readOnly").EnumerateArray().Select(value => value.GetInt32() != 0).ToArray();
            var panel = Open(item, paths); var step = 0;
            var rawHistory = new Dictionary<int, IReadOnlyList<ImageComparisonEngine.DecodedFrame>>();
            State(item.GetProperty("states")[0]);
            for (var p = 0; p < paths.Length; p++) check("image insertions GUI " + name + " initial raw " + p,
                MatchesRaw(panel.CaptureEditFrames()[p], item.GetProperty("inputs")[p]), "every original BGRA byte");
            foreach (var action in item.GetProperty("actions").EnumerateArray())
            {
                step++; var kind = action[0].GetString(); var source = action[1].GetInt32();
                var destination = action[2].GetInt32(); var value = action[3].GetInt32();
                var beforeRaw = panel.CaptureEditFrames();
                switch (kind)
                {
                    case "mode": pump(panel.SetInsertionDeletionModeAsync(value)); break;
                    case "offset": pump(panel.AddOffsetAsync(destination, source, value)); break;
                    case "rotate":
                        var orientation = panel.CaptureSettings().Orientations(paths.Length == 3)[destination];
                        pump(panel.SetOrientationAsync(destination, orientation with { Rotation = value })); break;
                    case "copy":
                    case "all":
                        if (kind == "copy") Select(panel, value);
                        if (readOnly[destination])
                        {
                            var stable = Stamp(panel); var rejected = false;
                            try { pump(panel.CopyRegionAsync(source, destination, all: kind == "all")); }
                            catch (InvalidOperationException) { rejected = true; }
                            check("image insertions GUI " + name + " readonly copy", rejected && Stamp(panel) == stable,
                                "GUI rejects original readonly no-op without changing frames or history");
                        }
                        else pump(panel.CopyRegionAsync(source, destination, all: kind == "all"));
                        break;
                    case "auto": pump(panel.AutoMergeAsync(destination)); break;
                    case "undo": pump(panel.UndoEditAsync()); break;
                    case "redo": pump(panel.RedoEditAsync()); break;
                    case "save":
                        var path = Path.Combine(root, name + "-saved.png"); pump(panel.SaveToAsync(destination, path));
                        check("image insertions GUI " + name + " independent raw PNG", Same(beforeRaw[destination], HeadlessImageCopyChecks.ReadPng(path)),
                            "independent PNG decoder; no aligned ghost pixels");
                        var project = pane.CaptureProject(); var savedPath = destination == 0 ? project.LeftPath : destination == paths.Length - 1 ? project.RightPath : project.BasePath;
                        check("image insertions GUI " + name + " saved project path", Path.GetFullPath(savedPath) == Path.GetFullPath(path), savedPath);
                        break;
                    default: throw new InvalidDataException("未対応の画像挿入削除原本操作です。");
                }
                if (kind is "mode" or "offset" or "rotate" or "save") check("image insertions GUI " + name + " raw unchanged " + step,
                    SameFrames(beforeRaw, panel.CaptureEditFrames()), "view settings and save never rewrite raw pixels");
                State(item.GetProperty("states")[step]);
                if (step == 1 && name is "1-insert-start-copy0to1" or "2-insert-middle-copy1to0" or "1-mode-history" or "2-three-independent")
                    screenshot("image-insertions-" + name + ".png");
                if (step == 1 && name == "2-three-independent") Layout(window, panel, check, screenshot);
            }
            var exported = panel.CaptureEditFrames();
            for (var p = 0; p < paths.Length; p++)
            {
                check("image insertions GUI " + name + " final original raw " + p, MatchesRaw(exported[p], item.GetProperty("exports")[p]), "original SaveImageAs dimensions and every raw byte");
                check("image insertions GUI " + name + " input preserved " + p,
                    Hash(File.ReadAllBytes(paths[p])) == hashes[p] && File.GetAttributes(paths[p]) == attributes[p], "bytes and file attributes");
            }

            void State(JsonElement expected)
            {
                states++; var settings = panel.CaptureSettings(); var rendered = panel.RenderedFrames; var raw = panel.CaptureEditFrames();
                var prefix = "image insertions GUI " + name + " state " + step;
                check(prefix + " mode", settings.InsertionDeletionMode == expected.GetProperty("mode").GetInt32()
                    && panel.CaptureReport().InsertionDeletionMode == settings.InsertionDeletionMode, "only adopted mode is captured for project and report");
                check(prefix + " regions", panel.DifferenceCount == expected.GetProperty("differenceCount").GetInt32()
                    && panel.ConflictCount == (paths.Length == 3 ? expected.GetProperty("conflictCount").GetInt32() : 0), "two-pane GUI hides conflict count");
                check(prefix + " shared history", Button(panel, "ImageUndo").IsEnabled == expected.GetProperty("undoable").GetBoolean()
                    && Button(panel, "ImageRedo").IsEnabled == expected.GetProperty("redoable").GetBoolean(), "shared raw history");
                if (rawHistory.TryGetValue(panel.HistoryIndex, out var prior)) check(prefix + " raw history restoration", SameFrames(prior, raw), "Undo/Redo returns the previously observed raw revision");
                else rawHistory[panel.HistoryIndex] = raw;
                var orientations = settings.Orientations(paths.Length == 3); var offsets = settings.Offsets(paths.Length == 3);
                for (var p = 0; p < paths.Length; p++)
                {
                    var wanted = expected.GetProperty("panes")[p]; var frame = rendered[p];
                    check(prefix + " all aligned BGRA " + p, frame.Width == wanted.GetProperty("canvasWidth").GetInt32()
                        && frame.Height == wanted.GetProperty("canvasHeight").GetInt32()
                        && frame.Pixels.AsSpan().SequenceEqual(Convert.FromHexString(wanted.GetProperty("bgraHex").GetString()!)), "original SaveDiffImageAs whole canvas; no highlight");
                    check(prefix + " orientation and offset " + p, orientations[p].Rotation == wanted.GetProperty("angle").GetInt32()
                        && orientations[p].FlipHorizontal == wanted.GetProperty("flipx").GetBoolean() && orientations[p].FlipVertical == wanted.GetProperty("flipy").GetBoolean()
                        && offsets[p].X == wanted.GetProperty("offsetX").GetInt32() && offsets[p].Y == wanted.GetProperty("offsetY").GetInt32(), "display settings survive history");
                    check(prefix + " dirty " + p, panel.PaneModified(p) == wanted.GetProperty("modified").GetBoolean(), "per-pane savepoint");
                }
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject(); writer.WriteString("case", name); writer.WriteNumber("state", step);
                    writer.WriteNumber("mode", settings.InsertionDeletionMode); writer.WriteNumber("differenceCount", panel.DifferenceCount);
                    writer.WriteNumber("conflictCount", panel.ConflictCount); writer.WriteNumber("historyIndex", panel.HistoryIndex); writer.WriteNumber("historyCount", panel.HistoryCount);
                    WriteFrames(writer, "alignedCanvas", rendered); WriteFrames(writer, "raw", raw); writer.WriteEndObject();
                }
                evidence.WriteLine(Encoding.UTF8.GetString(buffer.ToArray())); evidence.Flush();
            }
        }
        check("image insertions GUI all original states", cases == 58 && states == 304, $"cases={cases}; states={states}");
        Boundaries();
        pane.DiscardChanges();

        SpecializedViews.ImagePanel Open(JsonElement item, string[] paths)
        {
            pane.DiscardChanges(); var ro = item.GetProperty("readOnly").EnumerateArray().Select(value => value.GetInt32() != 0).ToArray();
            pane.ApplyProject(new() { Mode = "Image", LeftPath = paths[0], RightPath = paths[^1], BasePath = paths.Length == 3 ? paths[1] : "",
                LeftReadOnly = ro[0], RightReadOnly = ro[^1], BaseReadOnly = paths.Length == 3 && ro[1],
                ImageSettings = new() { BlockSize = item.GetProperty("blockSize").GetInt32(), Threshold = item.GetProperty("threshold").GetDouble(),
                    ShowDifferences = false, Zoom = 8, ReportAllFrames = false } });
            pump(pane.ComparePathsAsync()); return Panel();
        }
        SpecializedViews.ImagePanel Panel() => pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
        void Select(SpecializedViews.ImagePanel panel, int index)
        {
            for (var i = 0; i <= panel.DifferenceCount && panel.SelectedDiffIndex != index; i++) pump(panel.NavigateRegionAsync(1));
            if (panel.SelectedDiffIndex != index) throw new InvalidOperationException("画像挿入削除のコピー領域を選択できません。");
        }
        void Boundaries()
        {
            var item = golden.RootElement.GetProperty("cases")[0]; var panel = Open(item, Inputs(item, root));
            var combo = panel.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "ImageInsertionDeletionMode");
            combo.SelectedIndex = 1; pump(panel.CurrentFrameOperation);
            check("image insertions GUI actual combo adopts mode", panel.CaptureSettings().InsertionDeletionMode == 1 && combo.SelectedIndex == 1, "actual SelectionChanged event");
            var stable = Stamp(panel);
            foreach (var bad in new[] { -1, 3, int.MinValue, int.MaxValue })
            {
                var rejected = false; try { pump(panel.SetInsertionDeletionModeAsync(bad)); } catch (ArgumentOutOfRangeException) { rejected = true; }
                check("image insertions GUI invalid mode " + bad, rejected && Stamp(panel) == stable, "rejected before adopting settings or pixels");
            }
            var invalidSettings = panel.CaptureSettings() with { InsertionDeletionMode = 3 }; var invalidRejected = false;
            try { pump(panel.ApplySettingsAsync(invalidSettings)); } catch (InvalidDataException) { invalidRejected = true; }
            check("image insertions GUI invalid project settings unchanged", invalidRejected && Stamp(panel) == stable, "validated before controls change");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); var rejected = false; try { pump(panel.SetInsertionDeletionModeAsync(2, cancel.Token)); } catch (OperationCanceledException) { rejected = true; }
                check("image insertions GUI precancel unchanged", rejected && Stamp(panel) == stable, "settings/raw/rendered/history");
            }
            using (var cancel = new CancellationTokenSource())
            {
                var pending = panel.SetInsertionDeletionModeAsync(2, cancel.Token); cancel.Cancel(); var rejected = false;
                try { pump(pending); } catch (OperationCanceledException) { rejected = true; }
                check("image insertions GUI cancelled candidate unchanged", rejected && Stamp(panel) == stable && combo.SelectedIndex == 1, "candidate discarded and selector restored");
            }
            var obsolete = panel.SetInsertionDeletionModeAsync(0); var latest = panel.SetInsertionDeletionModeAsync(2);
            try { pump(obsolete); } catch (OperationCanceledException) { } pump(latest);
            check("image insertions GUI stale keeps latest", panel.CaptureSettings().InsertionDeletionMode == 2 && combo.SelectedIndex == 2
                && SameFrames(panel.CaptureEditFrames(), InputsRaw(item)), "old completion cannot adopt pixels or mode");
            panel.SetReadOnly([false, true]); stable = Stamp(panel);
            var rejectedSave = false; var readonlyPath = Path.Combine(root, "readonly-rejected.png");
            try { pump(panel.SaveToAsync(1, readonlyPath)); } catch (InvalidOperationException) { rejectedSave = true; }
            check("image insertions GUI readonly save rejected", rejectedSave && Stamp(panel) == stable && !File.Exists(readonlyPath), "readonly still allows comparison mode");
            var busyPath = Path.Combine(root, "busy-mode-raw.png"); var rawBeforeSave = panel.CaptureEditFrames()[0]; var saving = panel.SaveToAsync(0, busyPath);
            var rejectedMode = false; try { pump(panel.SetInsertionDeletionModeAsync(1)); } catch (InvalidOperationException) { rejectedMode = true; }
            check("image insertions GUI saving rejects mode", rejectedMode && !combo.IsEnabled, "captured save revision remains fixed"); pump(saving);
            check("image insertions GUI busy saved raw unchanged", Same(rawBeforeSave, HeadlessImageCopyChecks.ReadPng(busyPath)) && panel.CaptureSettings().InsertionDeletionMode == 2, "independent raw PNG");
            pane.DiscardChanges(); var pages = Path.Combine(output, "tiff", "two-pages-le.tif");
            var multiSettings = new ImageViewSettings { InsertionDeletionMode = 1, BlockSize = 1, ShowDifferences = false, ReportAllFrames = false };
            pane.ApplyProject(new() { Mode = "Image", LeftPath = pages, RightPath = pages, ImageSettings = multiSettings }); pump(pane.ComparePathsAsync());
            panel = Panel(); pump(panel.SetFramesAsync(2, 2));
            check("image insertions GUI TIFF pages retain mode", panel.LeftFrame == 2 && panel.RightFrame == 2 && panel.CaptureSettings().InsertionDeletionMode == 1
                && panel.CaptureReport().InsertionDeletionMode == 1 && !panel.HasUnsavedChanges, "multiple pages use comparison engine and remain unedited");
            var projectPath = Path.Combine(root, "project.json"); pump(WorkspaceStore.SaveAsync(projectPath, pane.CaptureProject()));
            var load = WorkspaceStore.LoadAsync(projectPath); pump(load); var loaded = load.Result; var priorFrames = panel.RenderedFrames.ToArray();
            pane.ApplyProject(loaded); pump(pane.ComparePathsAsync());
            check("image insertions GUI project roundtrip", Panel().CaptureSettings() == loaded.ImageSettings && SameFrames(priorFrames, Panel().RenderedFrames), "mode/pages/full canvas survive restoration");
        }
    }

    private static void Layout(MainWindow window, SpecializedViews.ImagePanel panel, Action<string, bool, string> check, Action<string> screenshot)
    {
        var width = window.Width; var height = window.Height;
        var windowMinHeight = window.MinHeight;
        var toolbar = panel.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "ImageToolbar");
        var alpha = panel.GetVisualDescendants().OfType<Slider>().Single(control => control.Name == "ImageHighlightAlpha");
        var minHeight = alpha.MinHeight;
        // 本来の最小ウィンドウより小さい寸法は、明示したWindows実験だけで使う。
        var stressLayout = OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("DIFFBEACON_IMAGE_LAYOUT_STRESS") == "1";
        try
        {
            // 失敗先行: テーマのSliderが20%上限より高いと全体へのスクロールが不可能になる。
            // 実テーマに加え64/80pxの操作部品を実レイアウトで測り、画像領域と保存操作も守る。
            foreach (var sliderHeight in new[] { minHeight, 64d, 80d }.Distinct())
            foreach (var compact in new[] { false, true })
            foreach (var constrained in stressLayout && sliderHeight == 80 && compact ? new[] { false, true } : new[] { false })
            {
                alpha.MinHeight = sliderHeight;
                // 共通toolbarの20%を除いたパネルを約32px縮める寸法ストレス。
                // 実Macの描画やフォントを再現したものではない。
                window.MinHeight = windowMinHeight - (constrained ? 40 : 0);
                var stressSuffix = constrained ? " constrained-height" : "";
                var scenario = compact + (sliderHeight == minHeight ? "" : " slider-" + sliderHeight) + stressSuffix;
                var imageSuffix = (sliderHeight == minHeight ? "" : "-slider-" + sliderHeight) + (constrained ? "-constrained-height" : "");
                window.Width = compact ? window.MinWidth : width; window.Height = compact ? window.MinHeight : height;
                Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                alpha.BringIntoView(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var alphaPosition = alpha.TranslatePoint(new Point(), toolbar);
                check("image insertions GUI alpha reachable " + scenario, alphaPosition.HasValue && alphaPosition.Value.Y >= -1
                    && alphaPosition.Value.Y + alpha.Bounds.Height <= toolbar.Bounds.Height + 1 && alpha.IsEnabled,
                    $"alpha={alphaPosition}; alphaBounds={alpha.Bounds}; desired={alpha.DesiredSize}; toolbar={toolbar.Bounds}; viewport={toolbar.Viewport}; maxHeight={toolbar.MaxHeight}; panel={panel.Bounds}");
                if (compact) screenshot("image-alpha-compact-control" + imageSuffix + ".png");
                var mode = panel.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "ImageInsertionDeletionMode");
                mode.BringIntoView(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var modePosition = mode.TranslatePoint(new Point(), toolbar);
                check("image insertions GUI mode reachable " + scenario, modePosition.HasValue && modePosition.Value.Y >= -1
                    && modePosition.Value.Y + mode.Bounds.Height <= toolbar.Bounds.Height + 1 && mode.IsEnabled,
                    $"mode={modePosition}; toolbar={toolbar.Bounds}");
                if (compact) screenshot("image-insertions-compact-mode" + imageSuffix + ".png");
                toolbar.ScrollToEnd(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var heights = panel.GetVisualDescendants().OfType<Image>().Where(image => image.Name?.StartsWith("ImagePane", StringComparison.Ordinal) == true)
                    .Select(image => image.GetVisualAncestors().OfType<ScrollViewer>().First().Bounds.Height).ToArray();
                var save = Button(panel, "ImageSavePng"); var position = save.TranslatePoint(new Point(), toolbar);
                check("image insertions GUI viewport visible " + scenario, heights.Length == 3 && heights.All(value => value >= 24), "heights=" + string.Join(",", heights));
                check("image insertions GUI PNG save reachable " + scenario, position.HasValue && position.Value.Y >= 0
                    && position.Value.Y + save.Bounds.Height <= toolbar.Bounds.Height + 1, $"save={position}; toolbar={toolbar.Bounds}");
                if (compact) screenshot("image-insertions-compact" + imageSuffix + ".png");
            }
        }
        finally { alpha.MinHeight = minHeight; window.MinHeight = windowMinHeight; window.Width = width; window.Height = height; Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }
    private static string[] Inputs(JsonElement item, string root)
    {
        var inputs = item.GetProperty("inputs"); var name = item.GetProperty("name").GetString()!; var paths = new string[inputs.GetArrayLength()];
        for (var p = 0; p < paths.Length; p++)
        {
            paths[p] = Path.Combine(root, name + "-input-" + p + ".png"); var png = Convert.FromBase64String(inputs[p].GetProperty("pngBase64").GetString()!);
            if (Hash(png) != inputs[p].GetProperty("pngSha256").GetString()) throw new InvalidDataException("画像挿入削除入力PNGのSHAが一致しません。");
            File.WriteAllBytes(paths[p], png);
        }
        return paths;
    }
    private static IReadOnlyList<ImageComparisonEngine.DecodedFrame> InputsRaw(JsonElement item) => item.GetProperty("inputs").EnumerateArray()
        .Select(value => new ImageComparisonEngine.DecodedFrame(1, value.GetProperty("width").GetInt32(), value.GetProperty("height").GetInt32(), Convert.FromHexString(value.GetProperty("bgraHex").GetString()!))).ToArray();
    private static Button Button(SpecializedViews.ImagePanel panel, string name) => panel.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
    private static bool MatchesRaw(ImageComparisonEngine.DecodedFrame frame, JsonElement expected) => frame.Width == expected.GetProperty("width").GetInt32()
        && frame.Height == expected.GetProperty("height").GetInt32() && frame.Pixels.AsSpan().SequenceEqual(Convert.FromHexString(expected.GetProperty("bgraHex").GetString()!));
    private static bool Same(ImageComparisonEngine.DecodedFrame a, ImageComparisonEngine.DecodedFrame b) => a.Width == b.Width && a.Height == b.Height && a.Pixels.AsSpan().SequenceEqual(b.Pixels);
    private static bool SameFrames(IReadOnlyList<ImageComparisonEngine.DecodedFrame> a, IReadOnlyList<ImageComparisonEngine.DecodedFrame> b) => a.Count == b.Count && a.Select((frame, i) => Same(frame, b[i])).All(value => value);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Stamp(SpecializedViews.ImagePanel panel) => string.Join("|", panel.CaptureSettings().InsertionDeletionMode, panel.DifferenceCount,
        panel.ConflictCount, panel.HistoryIndex, panel.HistoryCount, panel.HasUnsavedChanges,
        string.Join(";", panel.CaptureEditFrames().Select(frame => $"{frame.Width},{frame.Height},{Hash(frame.Pixels)}")),
        string.Join(";", panel.RenderedFrames.Select(frame => $"{frame.Width},{frame.Height},{Hash(frame.Pixels)}")));
    private static void WriteFrames(Utf8JsonWriter writer, string name, IReadOnlyList<ImageComparisonEngine.DecodedFrame> frames)
    {
        writer.WriteStartArray(name);
        foreach (var frame in frames)
        {
            writer.WriteStartObject(); writer.WriteNumber("width", frame.Width); writer.WriteNumber("height", frame.Height);
            writer.WriteBase64String("bgraBase64", frame.Pixels); writer.WriteString("sha256", Hash(frame.Pixels)); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
