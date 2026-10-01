using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

// 原本期待値と実比較タブの操作を照合する。核だけを直接呼ぶ検証ではない。
internal static class HeadlessImageCopyChecks
{
    private const string GoldenSha = "A1645415624D84B3CB4A328148151A0FAE48B05412CF2E504589CD98C289D65D";
    private static readonly string[] Cases =
    [
        "3-ring-hole-selected-last-0to1", "3-ring-hole-selected-last-0to2",
        "3-ring-hole-selected-last-1to0", "3-ring-hole-selected-last-1to2",
        "3-ring-hole-selected-last-2to0", "3-ring-hole-selected-last-2to1",
        "2-transparent-copy-0to1", "2-multi-region-expand-all-1to0",
        "3-left-only-auto-to1", "3-conflict-auto-to2",
        "3-history-branch", "3-history-shared-panes"
    ];

    internal static void Run(MainWindow window, ComparisonPane pane, string fixtures, string output,
        Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        using var resource = typeof(HeadlessImageCopyChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.ImageCopy.json")
            ?? throw new InvalidOperationException("画像copy原本fixtureがありません。");
        using var buffer = new MemoryStream(); resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        check("image copy GUI original fixture SHA", Hash(bytes) == GoldenSha, "143 original cases / 935 states; GUI selects 12 sequences");
        using var golden = JsonDocument.Parse(bytes);
        var root = Path.Combine(fixtures, "image-copy-gui"); Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(output, "image-copy-gui-golden.json"), bytes);
        using var stream = File.Create(Path.Combine(output, "image-copy-gui-observations.json"));
        using var evidence = new Utf8JsonWriter(stream, new() { Indented = true });
        evidence.WriteStartArray();
        try
        {
            foreach (var name in Cases)
            {
                var item = golden.RootElement.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("name").GetString() == name);
                var folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
                var paths = Inputs(item, folder);
                var inputHashes = paths.Select(path => Hash(File.ReadAllBytes(path))).ToArray();
                var panel = Open(pane, item, paths, pump);
                State(name, 0, item.GetProperty("expected").GetProperty("states")[0], panel, evidence, check);
                var step = 0;
                foreach (var action in item.GetProperty("actions").EnumerateArray())
                {
                    step++;
                    var source = action.GetProperty("src").GetInt32(); var destination = action.GetProperty("dst").GetInt32();
                    switch (action.GetProperty("kind").GetString())
                    {
                        case "copy":
                            Select(panel, action.GetProperty("index").GetInt32(), pump);
                            if (step == 1)
                            {
                                var direction = panel.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "ImageCopyDirection");
                                direction.SelectedIndex = source * (paths.Length - 1) + (destination < source ? destination : destination - 1);
                                var button = panel.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "ImageCopyRegion");
                                check(name + " GUI copy button enabled", button.IsEnabled, "selected source/destination/region");
                                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump(panel.CurrentFrameOperation);
                            }
                            else pump(panel.CopyRegionAsync(source, destination));
                            break;
                        case "all": pump(panel.CopyRegionAsync(source, destination, all: true)); break;
                        case "auto": pump(panel.AutoMergeAsync(destination)); break;
                        case "undo": pump(panel.UndoEditAsync()); break;
                        case "redo": pump(panel.RedoEditAsync()); break;
                        case "save":
                            var before = panel.CaptureEditFrames()[destination];
                            var png = Path.Combine(folder, "saved-" + step + ".png");
                            pump(panel.SaveToAsync(destination, png));
                            var decoded = ReadPng(png);
                            check(name + " GUI save PNG " + step, Same(before, decoded), Hash(decoded.Pixels));
                            var project = pane.CaptureProject();
                            var capturedPath = destination == 0 ? project.LeftPath : destination == paths.Length - 1 ? project.RightPath : project.BasePath;
                            check(name + " GUI Save As project path " + step, Path.GetFullPath(capturedPath) == Path.GetFullPath(png), capturedPath);
                            pane.EnsureComparedForPackaging();
                            check(name + " GUI Save As retains compared paths " + step, true, "packaging comparison snapshot follows saved pane");
                            var snapshot = ImageComparisonEngine.OpenAsync(png, default);
                            pump(snapshot);
                            check(name + " GUI save reload " + step, Same(before, snapshot.Result.Decode(1, default)), png);
                            break;
                        default: throw new InvalidOperationException("GUI代表列に未対応actionがあります。");
                    }
                    State(name, step, item.GetProperty("expected").GetProperty("states")[step].GetProperty("state"), panel, evidence, check);
                }
                check(name + " GUI preserves input files", paths.Select((path, i) => Hash(File.ReadAllBytes(path)) == inputHashes[i]).All(value => value), folder);
                if (name is "2-transparent-copy-0to1" or "3-ring-hole-selected-last-0to1")
                {
                    var toolbar = panel.GetVisualDescendants().OfType<ScrollViewer>().Single(value => value.Name == "ImageToolbar");
                    toolbar.ScrollToEnd();
                    screenshot("image-copy-" + name + ".png");
                    if (name == "3-ring-hole-selected-last-0to1") CheckLayout(window, panel, toolbar, check, screenshot);
                }
            }
            Boundaries(window, pane, golden.RootElement, root, pump, check, screenshot);
        }
        finally { evidence.WriteEndArray(); pane.DiscardChanges(); }
    }

    private static string[] Inputs(JsonElement item, string folder)
    {
        var images = item.GetProperty("images").EnumerateArray().ToArray();
        var paths = images.Select((_, i) => Path.Combine(folder, "input-" + i + ".png")).ToArray();
        for (var i = 0; i < images.Length; i++)
            WritePng(paths[i], images[i].GetProperty("width").GetInt32(), images[i].GetProperty("height").GetInt32(),
                Convert.FromBase64String(images[i].GetProperty("bgraBase64").GetString()!));
        return paths;
    }

    private static void CheckLayout(MainWindow window, SpecializedViews.ImagePanel panel, ScrollViewer toolbar,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var width = window.Width; var height = window.Height;
        try
        {
            foreach (var compact in new[] { false, true })
            {
                window.Width = compact ? window.MinWidth : width;
                window.Height = compact ? window.MinHeight : height;
                Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                toolbar.ScrollToEnd(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                if (compact) screenshot("image-copy-compact.png");
                var viewport = panel.GetVisualDescendants().OfType<Image>().Where(image => image.Name?.StartsWith("ImagePane", StringComparison.Ordinal) == true)
                    .Select(image => image.GetVisualAncestors().OfType<ScrollViewer>().First().Bounds.Height).ToArray();
                var save = toolbar.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ImageSavePng");
                var position = save.TranslatePoint(new Point(0, 0), toolbar);
                check("image copy GUI viewport remains visible " + compact, viewport.Length == 3 && viewport.All(value => value >= 24),
                    $"viewport={string.Join(",", viewport)};panel={panel.Bounds};toolbar={toolbar.Bounds};window={window.Bounds};parents={string.Join(";", panel.GetVisualAncestors().OfType<Control>().Select(parent => parent.GetType().Name + ":" + parent.Bounds))}");
                check("image copy GUI scroll reaches PNG save " + compact, position.HasValue && position.Value.Y >= 0
                    && position.Value.Y + save.Bounds.Height <= toolbar.Bounds.Height + 1, $"save={position};toolbar={toolbar.Bounds}");
            }
        }
        finally { window.Width = width; window.Height = height; Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private static SpecializedViews.ImagePanel Open(ComparisonPane pane, JsonElement item, string[] paths, Action<Task> pump)
    {
        pane.DiscardChanges();
        var readOnly = item.GetProperty("readOnly").EnumerateArray().Select(value => value.GetBoolean()).ToArray();
        pane.ApplyProject(new() { Mode = "Image", LeftPath = paths[0], RightPath = paths[^1], BasePath = paths.Length == 3 ? paths[1] : "",
            LeftReadOnly = readOnly[0], RightReadOnly = readOnly[^1], BaseReadOnly = paths.Length == 3 && readOnly[1] });
        pump(pane.ComparePathsAsync());
        return pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
    }

    private static void Select(SpecializedViews.ImagePanel panel, int index, Action<Task> pump)
    {
        if (index < 0 || index >= panel.DifferenceCount) return;
        for (var i = 0; i <= panel.DifferenceCount && panel.SelectedDiffIndex != index; i++) pump(panel.NavigateRegionAsync(1));
        if (panel.SelectedDiffIndex != index) throw new InvalidOperationException("期待する画像領域を選択できません。");
    }

    private static void State(string name, int step, JsonElement expected, SpecializedViews.ImagePanel panel,
        Utf8JsonWriter evidence, Action<string, bool, string> check)
    {
        var frames = panel.CaptureEditFrames(); var history = expected.GetProperty("history");
        var oracleFrames = expected.GetProperty("frames").EnumerateArray().ToArray();
        var sameFrames = frames.Count == oracleFrames.Length;
        var compared = ImageRegionDiffer.Compare(frames);
        var expectedIds = expected.GetProperty("regionIds").EnumerateArray().SelectMany(row => row.EnumerateArray().Select(value => value.GetInt32())).ToArray();
        evidence.WriteStartObject(); evidence.WriteString("case", name); evidence.WriteNumber("step", step);
        evidence.WriteNumber("historyIndex", panel.HistoryIndex); evidence.WriteNumber("historyCount", panel.HistoryCount);
        evidence.WriteBoolean("dirty", panel.HasUnsavedChanges); evidence.WriteStartArray("frames");
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i]; evidence.WriteStartObject(); evidence.WriteNumber("pane", i);
            evidence.WriteNumber("width", frame.Width); evidence.WriteNumber("height", frame.Height);
            evidence.WriteString("sha256", Hash(frame.Pixels)); evidence.WriteBase64String("bgraBase64", frame.Pixels);
            evidence.WriteBoolean("modified", panel.PaneModified(i)); evidence.WriteEndObject();
            sameFrames &= i < oracleFrames.Length && frame.Width == oracleFrames[i].GetProperty("width").GetInt32()
                && frame.Height == oracleFrames[i].GetProperty("height").GetInt32()
                && frame.Pixels.SequenceEqual(Convert.FromBase64String(oracleFrames[i].GetProperty("bgraBase64").GetString()!))
                && Hash(frame.Pixels).Equals(oracleFrames[i].GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        evidence.WriteEndArray(); evidence.WriteStartArray("regions");
        foreach (var region in panel.Regions)
        {
            evidence.WriteStartObject(); evidence.WriteNumber("id", region.Id); evidence.WriteNumber("op", region.Op);
            evidence.WriteNumber("left", region.Left); evidence.WriteNumber("top", region.Top);
            evidence.WriteNumber("right", region.Right); evidence.WriteNumber("bottom", region.Bottom); evidence.WriteEndObject();
        }
        evidence.WriteEndArray();
        // 実panelの領域矩形とは別に、採取したrawを再比較して原本grid全セルを照合する。
        evidence.WriteStartArray("rawRecomparisonRegionIds"); foreach (var id in compared.RegionIds) evidence.WriteNumberValue(id); evidence.WriteEndArray();
        evidence.WriteEndObject(); evidence.Flush();
        var prefix = name + " GUI state " + step;
        check(prefix + " raw bytes and SHA", sameFrames, "all panes; unhighlighted BGRA");
        var oracleRegions = expected.GetProperty("regions").EnumerateArray().ToArray();
        check(prefix + " displayed regions", panel.DifferenceCount == expected.GetProperty("differenceCount").GetInt32()
            && panel.ConflictCount == (frames.Count == 3 ? expected.GetProperty("conflictCount").GetInt32() : 0)
            && panel.Regions.Count == oracleRegions.Length && panel.Regions.Select((region, i) => region.Id == oracleRegions[i].GetProperty("id").GetInt32()
                && region.Op == oracleRegions[i].GetProperty("op").GetInt32() && region.Left == oracleRegions[i].GetProperty("left").GetInt32()
                && region.Top == oracleRegions[i].GetProperty("top").GetInt32() && region.Right == oracleRegions[i].GetProperty("right").GetInt32()
                && region.Bottom == oracleRegions[i].GetProperty("bottom").GetInt32()).All(value => value), "classification / rectangles / counts");
        check(prefix + " raw region grid", compared.RegionIds.SequenceEqual(expectedIds), "independent golden; raw recomparison observation");
        var panes = history.GetProperty("panes").EnumerateArray().ToArray();
        check(prefix + " shared history and dirty", panel.HistoryIndex == history.GetProperty("index").GetInt32()
            && panel.HistoryCount == history.GetProperty("count").GetInt32()
            && Enumerable.Range(0, frames.Count).All(i => panel.PaneModified(i) == panes[i].GetProperty("modified").GetBoolean())
            && panel.HasUnsavedChanges == panes.Any(p => p.GetProperty("modified").GetBoolean()), "save / undo / redo / branch");
    }

    private static void Boundaries(MainWindow window, ComparisonPane pane, JsonElement golden, string root,
        Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        var item = golden.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("name").GetString() == "2-transparent-copy-0to1");
        var folder = Path.Combine(root, "boundaries"); Directory.CreateDirectory(folder); var paths = Inputs(item, folder);
        var panel = Open(pane, item, paths, pump); Select(panel, 0, pump); pump(panel.CopyRegionAsync(0, 1));
        var edited = panel.CaptureEditFrames(); var savedState = Stamp(panel);
        panel.DiscardChanges();
        panel.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ImageCancelEdit")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var discardCancelled = false;
        try { pump(panel.CurrentFrameOperation); } catch (OperationCanceledException) { discardCancelled = true; }
        check("image copy GUI cancelled discard restores dirty history", discardCancelled && panel.HasUnsavedChanges && Stamp(panel) == savedState,
            "the failed discard keeps the original edited session usable");
        var detached = panel.CaptureEditFrames(); detached[0].Pixels[0] ^= 255;
        check("image copy GUI detached capture", Stamp(panel) == savedState, "mutating captured BGRA does not mutate live session");
        var threshold = panel.GetVisualDescendants().OfType<NumericUpDown>().Single(value => value.Name == "ImageThreshold");
        threshold.Value = 510; pump(panel.CurrentFrameOperation);
        check("image copy GUI threshold keeps raw history dirty", Stamp(panel) == savedState, "threshold=510");
        threshold.Value = 0; pump(panel.CurrentFrameOperation);
        panel.SetReadOnly([true, true]); pump(panel.UndoEditAsync());
        check("image copy GUI readonly undo", panel.HistoryIndex == -1 && !panel.HasUnsavedChanges, "readonly added after copy");
        pump(panel.RedoEditAsync());
        check("image copy GUI readonly redo", Stamp(panel) == savedState, "existing history may restore readonly pane");
        var beforeReadOnly = Stamp(panel);
        try { pump(panel.CopyRegionAsync(1, 0, all: true)); } catch (InvalidOperationException) { }
        check("image copy GUI readonly copy preserves state", Stamp(panel) == beforeReadOnly, "no new history");
        panel.SetReadOnly([false, false]);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var rejected = false;
            try { pump(panel.CopyRegionAsync(1, 0, all: true, token: cancelled.Token)); } catch (OperationCanceledException) { rejected = true; }
            check("image copy GUI cancelled copy", rejected && Stamp(panel) == savedState, "no raw/history/dirty change");
            var target = Path.Combine(folder, "cancelled.png"); File.WriteAllText(target, "unchanged");
            var pathBefore = pane.CaptureProject().RightPath;
            rejected = false;
            try { pump(panel.SaveToAsync(1, target, cancelled.Token)); } catch (OperationCanceledException) { rejected = true; }
            check("image copy GUI cancelled save", rejected && File.ReadAllText(target) == "unchanged" && Stamp(panel) == savedState
                && pane.CaptureProject().RightPath == pathBefore, "output/path/savepoint retained");
        }
        panel.ReportAllFrames = false;
        var capture = panel.CaptureReport(); var report = Path.Combine(folder, "edited.html"); pump(pane.SaveReportAsync(report));
        check("image copy GUI edited HTML raw", OriginalHashes(File.ReadAllText(report), edited), report);
        pump(panel.UndoEditAsync());
        var capturedHtml = ImageReport.Create(capture, ["left", "right"], default);
        File.WriteAllText(Path.Combine(folder, "captured-before-undo.html"), capturedHtml);
        check("image copy GUI report capture immutable", OriginalHashes(capturedHtml, edited), "capture then undo");
        pump(panel.RedoEditAsync());
        var package = Path.Combine(folder, "unsaved.zip"); File.WriteAllText(package, "unchanged");
        var packageRejected = false;
        try { pump(window.PackageWorkspaceAsync(package, [window.SessionPanes.ToList().IndexOf(pane)])); } catch (InvalidOperationException ex) { packageRejected = ex.Message.Contains("未保存", StringComparison.Ordinal); }
        check("image copy GUI unsaved package rejected", packageRejected && File.ReadAllText(package) == "unchanged", "actual workspace packaging");
        foreach (var input in paths) RejectSave(panel, 1, input, pump, check, "current input");
        var readonlyPng = Path.Combine(folder, "readonly.png"); File.WriteAllText(readonlyPng, "unchanged");
        File.SetAttributes(readonlyPng, File.GetAttributes(readonlyPng) | FileAttributes.ReadOnly);
        try { RejectSave(panel, 1, readonlyPng, pump, check, "readonly output"); }
        finally { File.SetAttributes(readonlyPng, FileAttributes.Normal); }
        var png = Path.Combine(folder, "undo-savepoint.png"); pump(panel.UndoEditAsync()); pump(panel.SaveToAsync(1, png));
        check("image copy GUI undo savepoint", !panel.HasUnsavedChanges && panel.HistoryIndex == -1 && panel.HistoryCount == 1, png);
        pump(panel.RedoEditAsync()); check("image copy GUI redo after undo savepoint dirty", panel.PaneModified(1), "saved current index, not old modcount");
        RejectSave(panel, 1, paths[1], pump, check, "initial input after Save As");
        var busyOutput = Path.Combine(folder, "busy.png"); var busySave = panel.SaveToAsync(1, busyOutput);
        var busyCaptureRejected = false;
        try { panel.CaptureEditFrames(); } catch (InvalidOperationException) { busyCaptureRejected = true; }
        check("image copy GUI saving rejects raw capture", busyCaptureRejected, "cannot observe mutable save revision");
        check("image copy GUI saving disables threshold and page controls", !threshold.IsEnabled
            && panel.GetVisualDescendants().OfType<NumericUpDown>().Where(control => control.Name?.EndsWith("Frame", StringComparison.Ordinal) == true).All(control => !control.IsEnabled), "saving revision");
        var busyOperations = new (string Name, Func<Task> Run)[]
        {
            ("copy", () => panel.CopyRegionAsync(1, 0, all: true)), ("undo", () => panel.UndoEditAsync()),
            ("redo", () => panel.RedoEditAsync()), ("page", () => panel.SetFramesAsync(1, 1)),
            ("recompare", pane.ComparePathsAsync),
            ("discard", () => { panel.DiscardChanges(); return Task.CompletedTask; }),
            ("save", () => panel.SaveToAsync(1, Path.Combine(folder, "busy-second.png")))
        };
        foreach (var operation in busyOperations)
        {
            var rejected = false;
            try { pump(operation.Run()); } catch (InvalidOperationException) { rejected = true; }
            check("image copy GUI saving rejects " + operation.Name, rejected, "busy revision");
        }
        pump(busySave);
        check("image copy GUI busy save commits captured bytes", Same(edited[1], ReadPng(busyOutput)) && !panel.HasUnsavedChanges
            && !File.Exists(Path.Combine(folder, "busy-second.png")), busyOutput);
        screenshot("image-copy-saved.png");
        Guards(item, folder, pump, check);
        var three = golden.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("name").GetString() == "3-ring-hole-copy-0to2");
        var raceFolder = Path.Combine(folder, "latest"); Directory.CreateDirectory(raceFolder);
        panel = Open(pane, three, Inputs(three, raceFolder), pump); Select(panel, 0, pump);
        var old = panel.CopyRegionAsync(0, 1);
        var pendingCaptureRejected = false;
        try { panel.CaptureEditFrames(); } catch (InvalidOperationException) { pendingCaptureRejected = true; }
        check("image copy GUI pending candidate rejects capture", pendingCaptureRejected, "before bitmap/session adoption");
        var latest = panel.CopyRegionAsync(0, 2); pump(latest);
        try { pump(old); } catch (OperationCanceledException) { }
        var oracle = three.GetProperty("expected").GetProperty("states")[1].GetProperty("state");
        check("image copy GUI latest candidate wins", FramesEqual(panel.CaptureEditFrames(), oracle.GetProperty("frames"))
            && panel.HistoryCount == 1 && panel.HistoryIndex == 0, "two overlapping candidates, latest destination=2");
        pane.DiscardChanges(); pane.ApplyProject(new() { Mode = "Image", LeftPath = Path.Combine(Path.GetDirectoryName(root)!, "same-first-left.gif"), BasePath = paths[0], RightPath = paths[1] });
        pump(pane.ComparePathsAsync()); panel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
        var count = panel.LeftFrameCount; var animatedRejected = 0;
        foreach (var operation in new Func<Task>[] { () => panel.CopyRegionAsync(0, 1, all: true), () => panel.AutoMergeAsync(1), () => panel.SaveToAsync(1, Path.Combine(folder, "animated.png")) })
            try { pump(operation()); } catch (InvalidOperationException) { animatedRejected++; }
        pump(panel.SetFramesAsync(2, 1, 1));
        check("image copy GUI animated editing refused pages retained", count > 1 && animatedRejected == 3 && panel.LeftFrame == 2
            && !panel.HasUnsavedChanges && !File.Exists(Path.Combine(folder, "animated.png")), "static only; page browsing continues");
    }

    private static void Guards(JsonElement item, string folder, Action<Task> pump, Action<string, bool, string> check)
    {
        // 他tabとworkspaceを持つ本物のWindowで調べ、元selftestのタブ構成を変えない。
        var guardRoot = Path.Combine(folder, "guards"); Directory.CreateDirectory(guardRoot);
        var owner = new MainWindow { Width = 1280, Height = 850 }; owner.Show();
        try
        {
            var paths = Inputs(item, guardRoot); var pane = owner.ActivePane; var panel = Open(pane, item, paths, pump);
            pane.ApplyProject(pane.CaptureProject() with { RightReadOnly = true }); pump(pane.ComparePathsAsync());
            panel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single(); Select(panel, 0, pump);
            var copyRejected = false; var saveRejected = false; var readonlySave = Path.Combine(guardRoot, "project-readonly.png");
            try { pump(panel.CopyRegionAsync(0, 1)); } catch (InvalidOperationException) { copyRejected = true; }
            try { pump(panel.SaveToAsync(1, readonlySave)); } catch (InvalidOperationException) { saveRejected = true; }
            check("image copy GUI project readonly propagated", copyRejected && saveRejected && panel.HistoryCount == 0 && !panel.HasUnsavedChanges
                && !File.Exists(readonlySave), "actual ComparePathsAsync project flags");
            pane.ApplyProject(pane.CaptureProject() with { RightReadOnly = false }); pump(pane.ComparePathsAsync());
            panel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
            Select(panel, 0, pump); pump(panel.CopyRegionAsync(0, 1));
            var otherInput = Path.Combine(guardRoot, "other-input.png");
            var raw = item.GetProperty("images")[0];
            WritePng(otherInput, raw.GetProperty("width").GetInt32(), raw.GetProperty("height").GetInt32(), Convert.FromBase64String(raw.GetProperty("bgraBase64").GetString()!));
            var other = owner.AddSession(); other.ApplyProject(new() { Mode = "Image", LeftPath = otherInput, RightPath = paths[1] });
            pump(other.ComparePathsAsync());
            RejectSave(panel, 1, otherInput, pump, check, "other tab input");
            var filter = Path.Combine(guardRoot, "filter.png"); File.WriteAllText(filter, "unchanged");
            other.ApplyProject(other.CaptureProject() with { FileFilterPath = filter });
            RejectSave(panel, 1, filter, pump, check, "other tab filter");
            var workspace = Path.Combine(guardRoot, "workspace.png"); pump(owner.SaveWorkspaceAsync(workspace));
            RejectSave(panel, 1, workspace, pump, check, "workspace source");
            owner.SelectSession(0);
            var projectOutput = Path.Combine(guardRoot, "saved.png"); pump(panel.SaveToAsync(1, projectOutput));
            check("image copy GUI workspace project switches saved image", pane.CaptureProject().RightPath == projectOutput, projectOutput);
            var originalOther = other.CaptureProject().LeftPath;
            other.LeftPath.Text = paths[0];
            RejectSave(panel, 1, originalOther, pump, check, "other tab origin");
            pump(panel.UndoEditAsync());
            var lateTarget = Path.Combine(guardRoot, "late-other-input.png"); File.WriteAllText(lateTarget, "unchanged");
            var lateState = Stamp(panel); var latePath = pane.CaptureProject().RightPath;
            var lateSave = panel.SaveToAsync(1, lateTarget);
            other.LeftPath.Text = lateTarget;
            var lateRejected = false;
            try { pump(lateSave); } catch (InvalidOperationException) { lateRejected = true; }
            check("image copy GUI final guard sees latest other tab", lateRejected && File.ReadAllText(lateTarget) == "unchanged"
                && Stamp(panel) == lateState && pane.CaptureProject().RightPath == latePath, "protected after save starts");
            other.LeftPath.Text = paths[0];
            var lateReadonlyTarget = Path.Combine(guardRoot, "late-readonly.png"); File.WriteAllText(lateReadonlyTarget, "unchanged");
            lateSave = panel.SaveToAsync(1, lateReadonlyTarget); panel.SetReadOnly([false, true]); lateRejected = false;
            try { pump(lateSave); } catch (InvalidOperationException) { lateRejected = true; }
            check("image copy GUI final guard sees latest readonly", lateRejected && File.ReadAllText(lateReadonlyTarget) == "unchanged"
                && Stamp(panel) == lateState && pane.CaptureProject().RightPath == latePath, "readonly after save starts; savepoint retained");
            panel.SetReadOnly([false, false]); pump(panel.RedoEditAsync());
            var savedFrame = panel.CaptureEditFrames()[1]; pump(pane.ComparePathsAsync());
            var reloaded = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
            check("image copy GUI Save As actual comparison reload", Same(savedFrame, reloaded.CaptureEditFrames()[1]) && !reloaded.HasUnsavedChanges, projectOutput);
        }
        finally { foreach (var pane in owner.SessionPanes) { pane.DiscardChanges(); pane.Dispose(); } owner.Close(); }
    }

    private static void RejectSave(SpecializedViews.ImagePanel panel, int pane, string target, Action<Task> pump,
        Action<string, bool, string> check, string reason)
    {
        var before = Stamp(panel); var bytes = File.ReadAllBytes(target); var rejected = false;
        try { pump(panel.SaveToAsync(pane, target)); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException or ArgumentException) { rejected = true; }
        check("image copy GUI guards " + reason, rejected && Stamp(panel) == before && bytes.SequenceEqual(File.ReadAllBytes(target)), target);
    }

    private static string Stamp(SpecializedViews.ImagePanel panel) => panel.HistoryIndex + ":" + panel.HistoryCount + ":" + panel.HasUnsavedChanges
        + ":" + string.Join(";", panel.CaptureEditFrames().Select((frame, pane) => frame.Width + "x" + frame.Height + ":" + Hash(frame.Pixels) + ":" + panel.PaneModified(pane)));
    private static bool FramesEqual(IReadOnlyList<ImageComparisonEngine.DecodedFrame> frames, JsonElement expected)
        => frames.Count == expected.GetArrayLength() && frames.Select((frame, i) => frame.Width == expected[i].GetProperty("width").GetInt32()
            && frame.Height == expected[i].GetProperty("height").GetInt32() && frame.Pixels.SequenceEqual(Convert.FromBase64String(expected[i].GetProperty("bgraBase64").GetString()!))).All(value => value);
    private static bool Same(ImageComparisonEngine.DecodedFrame a, ImageComparisonEngine.DecodedFrame b)
        => a.Width == b.Width && a.Height == b.Height && a.Pixels.SequenceEqual(b.Pixels);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool OriginalHashes(string html, IReadOnlyList<ImageComparisonEngine.DecodedFrame> frames)
    {
        var sides = frames.Count == 3 ? new[] { "left", "middle", "right" } : ["left", "right"];
        return frames.Select((frame, i) => Regex.IsMatch(html, "data-side=\"" + sides[i] + "-original\"[^>]*data-pixel-sha256=\"" + Hash(frame.Pixels) + "\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).All(value => value);
    }

    // 独立したBCL PNG writer/reader。製品encoder/decoderに期待値を依存させない。
    private static void WritePng(string path, int width, int height, byte[] bgra)
    {
        using var file = File.Create(path); file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6; Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
        {
            var row = new byte[width * 4 + 1];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var source = (y * width + x) * 4; var target = x * 4 + 1;
                    row[target] = bgra[source + 2]; row[target + 1] = bgra[source + 1]; row[target + 2] = bgra[source]; row[target + 3] = bgra[source + 3];
                }
                zlib.Write(row);
            }
        }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        void Chunk(string name, byte[] data)
        {
            var type = Encoding.ASCII.GetBytes(name); Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(word, data.Length); file.Write(word); file.Write(type); file.Write(data);
            BinaryPrimitives.WriteUInt32BigEndian(word, Crc(type, data)); file.Write(word);
        }
    }

    private static ImageComparisonEngine.DecodedFrame ReadPng(string path)
    {
        var bytes = File.ReadAllBytes(path); var signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(signature)) throw new InvalidDataException("PNG signature");
        var width = 0; var height = 0; var channels = 0; var ended = false; using var compressed = new MemoryStream();
        for (var position = 8; position < bytes.Length;)
        {
            if (bytes.Length - position < 12) throw new InvalidDataException("PNG truncated chunk");
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(position));
            if (length < 0 || length > bytes.Length - position - 12) throw new InvalidDataException("PNG chunk length");
            var type = bytes.AsSpan(position + 4, 4).ToArray(); var data = bytes.AsSpan(position + 8, length).ToArray();
            if (Crc(type, data) != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 8 + length))) throw new InvalidDataException("PNG CRC");
            var name = Encoding.ASCII.GetString(type);
            if (name == "IHDR")
            {
                if (position != 8 || length != 13 || data[8] != 8 || data[9] is not (2 or 6) || data[10] != 0 || data[11] != 0 || data[12] != 0) throw new InvalidDataException("PNG format");
                width = BinaryPrimitives.ReadInt32BigEndian(data); height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4)); channels = data[9] == 6 ? 4 : 3;
                if (width <= 0 || height <= 0 || (long)width * height > ImageComparisonEngine.MaximumPixels) throw new InvalidDataException("PNG dimensions");
            }
            else if (name == "IDAT") compressed.Write(data);
            else if (name == "IEND") { if (length != 0 || position + 12 != bytes.Length) throw new InvalidDataException("PNG trailing data"); ended = true; }
            position += length + 12;
        }
        if (!ended || channels == 0) throw new InvalidDataException("PNG incomplete");
        compressed.Position = 0; using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        var stride = checked(width * channels); var packed = new byte[checked((stride + 1) * height)]; inflater.ReadExactly(packed);
        if (inflater.ReadByte() != -1) throw new InvalidDataException("PNG surplus pixels");
        var pixels = new byte[checked(width * height * 4)]; var previous = new byte[stride]; var row = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var offset = y * (stride + 1); var filter = packed[offset];
            for (var x = 0; x < stride; x++)
            {
                var a = x < channels ? 0 : row[x - channels]; var b = previous[x]; var c = x < channels ? 0 : previous[x - channels];
                var predictor = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c), _ => throw new InvalidDataException("PNG filter") };
                row[x] = unchecked((byte)(packed[offset + 1 + x] + predictor));
            }
            for (var x = 0; x < width; x++)
            {
                var destination = (y * width + x) * 4; var source = x * channels;
                pixels[destination] = row[source + 2]; pixels[destination + 1] = row[source + 1]; pixels[destination + 2] = row[source]; pixels[destination + 3] = channels == 4 ? row[source + 3] : (byte)255;
            }
            (row, previous) = (previous, row);
        }
        return new(1, width, height, pixels);
    }
    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c; var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
    private static uint Crc(byte[] type, byte[] data)
    {
        uint crc = uint.MaxValue;
        foreach (var value in type.Concat(data)) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) == 0 ? 0u : 0xedb88320u); }
        return ~crc;
    }
}
