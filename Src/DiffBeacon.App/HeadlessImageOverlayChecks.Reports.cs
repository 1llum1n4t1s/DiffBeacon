using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static partial class HeadlessImageOverlayChecks
{
    private static void RunSelectedReportChecks(MainWindow window, ComparisonPane pane, SpecializedViews.ImagePanel panel,
        ImageComparisonEngine.DecodedFrame[] raw, byte[][] expected, string label, string folder, Action<Task> pump,
        Action<string, bool, string> check,
        List<(string Name, string Png, byte[] Pixels, int Width, int Height)> observations)
    {
        var capture = panel.CaptureReport(); var originalOptions = window.ImageOptions.Current;
        var frozenProject = pane.CaptureProject();
        var titles = raw.Select((_, i) => "pane" + i).ToArray();
        var package = Path.Combine(folder, label + "-captured.zip");
        // 実GUI包装入口も全imageentryのcanonical表示を捕捉する。
        pump(window.PackageWorkspaceAsync(package, [Array.IndexOf(window.SessionPanes.ToArray(), pane)], new(IncludeReport: true)));
        using (var archive = System.IO.Compression.ZipFile.OpenRead(package))
        using (var reader = new StreamReader(archive.Entries.Single(entry => entry.FullName == "report.files/1.html").Open()))
            Verify(reader.ReadToEnd(), "gui-package");
        panel.SetOverlayOptions(originalOptions with { OverlayMode = 1 }); pump(panel.CurrentDisplayOperation);
        var reportClock = new Clock(750);
        var html = ImageReport.Create(capture, titles, clock: reportClock);
        File.WriteAllText(Path.Combine(folder, label + "-captured.html"), html);
        Verify(html, "after-mode-change");
        check("overlay selected report frozen clock0 " + label, reportClock.Reads.Count == 0
            && capture.DisplayCapture!.Settings.Mode == originalOptions.OverlayMode, "owned adopted frame/sample/settings; live global mode changed");
        var protectedPackage = Path.Combine(folder, label + "-source-mismatch.zip"); File.WriteAllText(protectedPackage, "protected ZIP");
        var inputPath = frozenProject.LeftPath; var bytes = File.ReadAllBytes(inputPath); var modified = File.GetLastWriteTimeUtc(inputPath);
        try
        {
            var changed = bytes.ToArray(); changed[^1] ^= 1; File.WriteAllBytes(inputPath, changed); File.SetLastWriteTimeUtc(inputPath, modified);
            var rejected = false;
            try { pump(ComparisonPackage.CreateWithImageDisplaysAsync(new() { Entries = [frozenProject] }, protectedPackage,
                new(IncludeReport: true), [0], CancellationToken.None, null, new Dictionary<int, ImageReportDisplayCapture> { [0] = capture.DisplayCapture! })); }
            catch (InvalidDataException error) { rejected = error.Message.Contains("staging", StringComparison.Ordinal); }
            check("overlay package same-size same-time SHA rejection " + label, rejected && File.ReadAllText(protectedPackage) == "protected ZIP",
                "staged SHA checked against Snapshot bytes before decoding/report/output replacement");
        }
        finally { File.WriteAllBytes(inputPath, bytes); File.SetLastWriteTimeUtc(inputPath, modified); }
        var mismatch = false;
        try { _ = ImageReport.Create(capture with { Threshold = capture.Threshold + 1 }, titles, clock: reportClock); }
        catch (InvalidDataException) { mismatch = true; }
        check("overlay capture settings mismatch rejects " + label, mismatch && reportClock.Reads.Count == 0, "no capture reuse with changed comparison settings");
        panel.SetOverlayOptions(originalOptions); pump(panel.CurrentDisplayOperation);
        if (originalOptions.OverlayMode == 2)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            panel.OverlayCandidateReady = () => { entered.TrySetResult(); return release.Task; };
            var confirmedSample = panel.AdoptedDisplay!.Sample;
            panel.SetOverlayOptions(originalOptions with { OverlayMode = 1 }); var request = panel.CurrentDisplayOperation; pump(entered.Task);
            var pendingCapture = panel.CaptureReport();
            check("overlay pending candidate capture uses adopted settings " + label, pendingCapture.DisplayCapture!.Settings.Mode == 2
                && pendingCapture.DisplayCapture.Sample == confirmedSample, "candidate mode1 must not relabel confirmed Alpha pixels");
            panel.OverlayCandidateReady = null; release.TrySetResult(); pump(request);
            Verify(ImageReport.Create(pendingCapture, titles, clock: reportClock), "pending-capture");
            panel.SetOverlayOptions(originalOptions); pump(panel.CurrentDisplayOperation);
        }

        void Verify(string report, string state)
        {
            for (var side = 0; side < raw.Length; side++)
            foreach (var original in new[] { false, true })
            {
                var sideName = side == 0 ? "left" : side == raw.Length - 1 ? "right" : "middle";
                if (original) sideName += "-original";
                var tag = Regex.Matches(report, "<img[^>]+>").Select(match => match.Value).Single(tag => tag.Contains("data-side=\"" + sideName + "\"", StringComparison.Ordinal));
                var png = Convert.FromBase64String(Regex.Match(tag, "src=\"data:image/png;base64,([^\"]+)").Groups[1].Value);
                var path = Path.Combine(folder, label + "-" + state + "-" + sideName + ".png"); File.WriteAllBytes(path, png);
                var decoded = HeadlessImageCopyChecks.ReadPng(path); var pixels = original ? raw[side].Pixels : expected[side];
                check("overlay report actual PNG " + label + ":" + state + ":" + sideName,
                    decoded.Width == raw[side].Width && decoded.Height == raw[side].Height && decoded.Pixels.AsSpan().SequenceEqual(pixels),
                    "independent fixed-phase blend before capture; original BGRA unchanged");
                observations.Add((label + ":" + state + ":" + sideName, path, pixels, raw[side].Width, raw[side].Height));
            }
        }
    }

    private static void RunFrameBudgetAndStateChecks(MainWindow window, SpecializedViews.ImagePanel panel, string folder, Action<Task> pump,
        Action<string, bool, string> check, List<(string Name, string Png, byte[] Pixels, int Width, int Height)> observations)
    {
        panel.GateClock = new Clock(0);
        var budgetDisplayBefore = panel.CurrentDisplayOperation;
        var budgetDisplayBeforeStatus = budgetDisplayBefore.Status.ToString();
        var resetFrameOperation = panel.ApplySettingsAsync(panel.CaptureSettings() with { MiddleOffset = new() });
        pump(resetFrameOperation);
        using (var stateFile = new FileStream(Path.Combine(folder, "frame-budget-start-state.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var stateWriter = new System.Text.Json.Utf8JsonWriter(stateFile, new() { Indented = true }))
        {
            var startSettings = panel.CaptureSettings();
            stateWriter.WriteStartObject();
            stateWriter.WriteBoolean("sameDisplayTask", ReferenceEquals(budgetDisplayBefore, panel.CurrentDisplayOperation));
            stateWriter.WriteString("displayTaskStatusBeforeApply", budgetDisplayBeforeStatus);
            stateWriter.WriteString("previousDisplayTaskStatusAfterApply", budgetDisplayBefore.Status.ToString());
            stateWriter.WriteString("displayTaskStatusAfterApply", panel.CurrentDisplayOperation.Status.ToString());
            stateWriter.WriteString("frameTaskStatusAfterAwait", resetFrameOperation.Status.ToString());
            stateWriter.WriteBoolean("frameAwaitCompletedSuccessfully", resetFrameOperation.IsCompletedSuccessfully);
            stateWriter.WriteNumber("adoptedMode", panel.AdoptedDisplay!.Settings.Mode);
            stateWriter.WriteNumber("globalMode", window.ImageOptions.Current.OverlayMode);
            stateWriter.WriteNumber("middleOffsetX", startSettings.MiddleOffset.X);
            stateWriter.WriteNumber("middleOffsetY", startSettings.MiddleOffset.Y);
            stateWriter.WriteStartArray("renderedFrames");
            foreach (var startFrame in panel.RenderedFrames)
            { stateWriter.WriteStartObject(); stateWriter.WriteNumber("width", startFrame.Width); stateWriter.WriteNumber("height", startFrame.Height); stateWriter.WriteEndObject(); }
            stateWriter.WriteEndArray(); stateWriter.WriteEndObject();
        }
        foreach (var mode in new[] { 2, 3 })
        {
            if (window.ImageOptions.Current.OverlayMode != mode)
            { panel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = mode }); pump(panel.CurrentDisplayOperation); }
            var previous = panel.CaptureSettings(); var frames = panel.RenderedFrames.ToArray();
            var hashes = frames.Select(frame => ImageComparisonEngine.PixelHash(frame.Pixels, CancellationToken.None)).ToArray();
            var clock = new Clock(200, 300, 500, 600); panel.RenderClock = clock;
            var operation = panel.ApplySettingsAsync(previous with { MiddleOffset = new(3999, 3999) });
            var rejected = false; try { pump(operation); } catch (InvalidOperationException error) { rejected = error.Message.Contains("256M", StringComparison.Ordinal); }
            check("overlay frame transaction budget preserves " + mode, rejected && operation.IsFaulted && clock.Reads.Count == 0
                && panel.CaptureSettings() == previous && panel.RenderedFrames.Select(frame => ImageComparisonEngine.PixelHash(frame.Pixels, CancellationToken.None)).SequenceEqual(hashes)
                && !panel.HasUnsavedChanges && panel.HistoryCount == 0, "small Alpha/ANIM→legal16Mcanvas but additionaldraw exceeds256M; no intermediateNone adoption; rejected="
                    + rejected + ";faulted=" + operation.IsFaulted + ";reads=" + clock.Reads.Count + ";settings=" + (panel.CaptureSettings() == previous));
            for (var side = 0; side < frames.Length; side++)
            {
                var path = Path.Combine(folder, "frame-budget-" + mode + "-" + side + ".png"); pump(ImagePngStore.SaveAsync(path, panel.RenderedFrames[side], []));
                observations.Add(("frame-budget:" + mode + ":" + side, path, frames[side].Pixels, frames[side].Width, frames[side].Height));
            }
        }
        panel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = 2 }); pump(panel.CurrentDisplayOperation);
        var adopted = panel.CaptureSettings(); var before = panel.RenderedFrames.Select(frame => ImageComparisonEngine.PixelHash(frame.Pixels, CancellationToken.None)).ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.FrameCandidateReady = () => { entered.TrySetResult(); return release.Task; };
        var frameOperation = panel.ApplySettingsAsync(adopted with { OverlayOpacity = .4 }); pump(entered.Task);
        panel.OverlayCandidateReady = () => throw new InvalidOperationException("state during frame failure");
        panel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = 3 }); var stateOperation = panel.CurrentDisplayOperation;
        panel.FrameCandidateReady = null; release.TrySetResult(); try { pump(frameOperation); } catch (InvalidOperationException) { }
        panel.OverlayCandidateReady = null;
        check("overlay state request during failed frame completes", frameOperation.IsFaulted && stateOperation.IsFaulted
            && panel.CaptureSettings() == adopted && panel.RenderedFrames.Select(frame => ImageComparisonEngine.PixelHash(frame.Pixels, CancellationToken.None)).SequenceEqual(before),
            "latest revision terminal on frame overlay failure; no dynamic worker lifecycle await and no caller-only cancellation");
        // faulted Taskも観測し、未観測例外を残さない。
        _ = stateOperation.Exception;
    }

    private static void RunMultipageReportChecks(MainWindow window, ComparisonPane pane, string folder, Action<Task> pump,
        Action<string, bool, string> check, List<(string Name, string Png, byte[] Pixels, int Width, int Height)> observations)
    {
        var paths = new[] { "two-pages-le.tif", "same-first-right.tif", "single-page.tif" }.Select(name =>
        {
            var path = Path.Combine(folder, "report-" + name);
            using var stream = typeof(HeadlessImageOverlayChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.Tiff." + name)
                ?? throw new InvalidDataException("TIFF report resource " + name);
            using var file = File.Create(path); stream.CopyTo(file); return path;
        }).ToArray();
        var traces = new List<(int Count, int Start, long[] Reads, int[][] Tuples, long[][] Samples)>();
        foreach (var count in new[] { 2, 3 })
        {
            window.ImageOptions.SetOptions(new()); pane.DiscardChanges();
            pane.ApplyProject(new() { Mode = "Image", LeftPath = paths[0], BasePath = count == 3 ? paths[2] : "", RightPath = paths[1],
                LeftReadOnly = true, BaseReadOnly = true, RightReadOnly = true,
                ImageSettings = new() { ShowDifferences = false, BlockSize = 1, OverlayOpacity = .3, ReportAllFrames = true } });
            pump(pane.ComparePathsAsync());
            var panel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single(); panel.GateClock = new Clock(0);
            foreach (var start in new[] { 1, 2 })
            {
                var captureEpochs = count == 2 ? new long[] { 200, 300 } : [200, 300, 500, 600];
                panel.RenderClock = new Clock(captureEpochs);
                panel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = 3 }); pump(panel.CurrentDisplayOperation);
                panel.RenderClock = new Clock(captureEpochs);
                if (count == 2) pump(panel.SetFramesAsync(start, start)); else pump(panel.SetFramesAsync(start, 1, start));
                var capture = panel.CaptureReport();
                var epochs = Enumerable.Range(0, 16).Select(i => 100L + i * 100).ToArray(); var clock = new Clock(epochs);
                var tuples = new List<int[]>(); var samples = new List<ImageDisplaySample>();
                var html = ImageReport.Create(capture, count == 2 ? ["left", "right"] : ["left", "short", "right"], clock: clock,
                    observe: (tuple, sample) => { tuples.Add(tuple.ToArray()); samples.Add(sample); });
                var blends = count == 2 ? 2 : 4; var expectedReads = epochs.Take(start == 1 ? blends : blends * 2).ToArray();
                check("overlay allpage immediate tuple clock " + count + ":" + start, clock.Reads.SequenceEqual(expectedReads)
                    && tuples.Count == 2 && tuples[0][0] == 1 && tuples[1][0] == 2 && (count == 2 || tuples.All(tuple => tuple[1] == 1)),
                    "same initial tuple clock0; return to start after changed tuple refreshes again; short input last page");
                check("overlay allpage leaves GUI tuple/sample " + count + ":" + start,
                    panel.CaptureReport().DisplayCapture!.Sample == capture.DisplayCapture!.Sample
                    && panel.CaptureReport().DisplayCapture!.FrameNumbers.SequenceEqual(capture.DisplayCapture.FrameNumbers), "report-local state; no GUI page restore refresh");
                traces.Add((count, start, clock.Reads.ToArray(), tuples.ToArray(), samples.Select(sample => sample.Epochs.ToArray()).ToArray()));
                File.WriteAllText(Path.Combine(folder, "allpages-" + count + "-" + start + ".html"), html);
                var tags = Regex.Matches(html, "<img[^>]+>").Select(match => match.Value).ToArray();
                for (var page = 1; page <= 2; page++)
                {
                    var raw = capture.Images.Select(image => image.Decode(Math.Min(page, image.FrameCount), CancellationToken.None)).ToArray();
                    var sampleEpochs = start == 1 && page == 1 ? captureEpochs : expectedReads.Skip((start == 1 ? page - 2 : page - 1) * blends).Take(blends).ToArray();
                    var width = raw.Max(frame => frame.Width); var height = raw.Max(frame => frame.Height);
                    var expected = IndependentReportBlend(raw, width, height, sampleEpochs);
                    for (var side = 0; side < count; side++) foreach (var original in new[] { false, true })
                    {
                        var sideName = side == 0 ? "left" : side == count - 1 ? "right" : "middle";
                        if (original) sideName += "-original";
                        var tag = tags.Where(tag => tag.Contains("data-side=\"" + sideName + "\"", StringComparison.Ordinal)).ElementAt(page - 1);
                        var png = Convert.FromBase64String(Regex.Match(tag, "src=\"data:image/png;base64,([^\"]+)").Groups[1].Value);
                        var name = "allpage:" + count + ":" + start + ":" + page + ":" + sideName;
                        var path = Path.Combine(folder, name.Replace(':', '-') + ".png"); File.WriteAllBytes(path, png);
                        var decoded = HeadlessImageCopyChecks.ReadPng(path); var pixels = original ? raw[side].Pixels : expected[side];
                        var w = original ? raw[side].Width : width; var h = original ? raw[side].Height : height;
                        check("overlay allpage full BGRA " + name, decoded.Width == w && decoded.Height == h && decoded.Pixels.AsSpan().SequenceEqual(pixels),
                            "independent epoch formula, individual blends, support rectangles, raw original");
                        observations.Add((name, path, pixels, w, h));
                    }
                }
                var budgetClock = new Clock(100);
                var rejected = false;
                try { _ = ImageReport.Create(capture with { DisplayCapture = null, DisplaySettings = capture.DisplayCapture.Settings,
                    Offsets = count == 2 ? [new(3997, 3997), new()] : [new(3997, 3997), new(), new()], BlockSize = 256 },
                    count == 2 ? ["left", "right"] : ["left", "short", "right"], clock: budgetClock); }
                catch (InvalidOperationException error) { rejected = error.Message.Contains("256M", StringComparison.Ordinal); }
                check("overlay allpage budget before clock " + count + ":" + start, rejected && budgetClock.Reads.Count == 0,
                    "metadata total draw work including baseline/clone/blend/highlight/wipe before any render clock/output");
                var alignedClock = new Clock(100); var alignedRejected = false;
                try { _ = ImageReport.Create(capture with { DisplayCapture = null, DisplaySettings = capture.DisplayCapture.Settings,
                    Offsets = count == 2 ? [new(3992, 3992), new()] : [new(3992, 3992), new(), new()], BlockSize = 256, InsertionDeletionMode = 1 },
                    count == 2 ? ["left", "right"] : ["left", "short", "right"], clock: alignedClock); }
                catch (InvalidOperationException error) { alignedRejected = error.Message.Contains("256M", StringComparison.Ordinal); }
                check("overlay aligned allpage budget before clock " + count + ":" + start, alignedRejected && alignedClock.Reads.Count == 0,
                    "raw/aligned spool only; whole draw preflight before clock/output, source/alignment algorithm budgets retained");
            }
            pump(panel.ApplySettingsAsync(panel.CaptureSettings() with { ShowDifferences = true }));
            var blinkEpochs = count == 2 ? new long[] { 100, 200, 0, 300, 400, 400 } : [100, 200, 300, 400, 0, 500, 600, 700, 800, 400];
            panel.RenderClock = new Clock(blinkEpochs);
            panel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = 3, BlinkDifferences = true }); pump(panel.CurrentDisplayOperation);
            var blinkCapture = panel.CaptureReport(); var blinkClock = new Clock(blinkEpochs); var blinkSamples = new List<ImageDisplaySample>();
            var blinkHtml = ImageReport.Create(blinkCapture, count == 2 ? ["left", "right"] : ["left", "short", "right"],
                clock: blinkClock, observe: (_, sample) => blinkSamples.Add(sample));
            check("overlay allpage perblend then blink clocks " + count, blinkClock.Reads.SequenceEqual(blinkEpochs)
                && blinkSamples.Select(sample => sample.HighlightVisible).SequenceEqual([false, true]), "changed tuple2→1→2: 2/4 individual blend reads, then one show&&blink read per Refresh");
            File.WriteAllText(Path.Combine(folder, "allpages-blink-" + count + ".html"), blinkHtml);
            for (var page = 1; page <= 2; page++)
            {
                var numbers = blinkCapture.Images.Select(image => Math.Min(page, image.FrameCount)).ToArray();
                var set = ImageComparisonEngine.DecodeSelection(blinkCapture.Images, numbers, 0, true, CancellationToken.None, blockSize: 1);
                var pixels = IndependentReportBlend(set.Frames.ToArray(), set.Regions.Width, set.Regions.Height,
                    blinkEpochs.Skip((page - 1) * (count == 2 ? 3 : 5)).Take(count == 2 ? 2 : 4).ToArray());
                var canvas = set.Frames.Select((frame, side) => new ImageComparisonEngine.DecodedFrame(frame.Number, set.Regions.Width, set.Regions.Height, pixels[side])).ToArray();
                var expected = ImageRegionRenderer.Render(set.Frames, set.Regions, 1, showDifferences: page == 2, displayCanvas: canvas);
                for (var side = 0; side < count; side++)
                {
                    var sideName = side == 0 ? "left" : side == count - 1 ? "right" : "middle";
                    var tag = Regex.Matches(blinkHtml, "<img[^>]+>").Select(match => match.Value).Where(tag => tag.Contains("data-side=\"" + sideName + "\"", StringComparison.Ordinal)).ElementAt(page - 1);
                    var png = Convert.FromBase64String(Regex.Match(tag, "src=\"data:image/png;base64,([^\"]+)").Groups[1].Value);
                    var path = Path.Combine(folder, "allpages-blink-" + count + "-" + page + "-" + side + ".png"); File.WriteAllBytes(path, png);
                    check("overlay allpage blink BGRA " + count + ":" + page + ":" + side,
                        HeadlessImageCopyChecks.ReadPng(path).Pixels.AsSpan().SequenceEqual(expected[side].Pixels), "independent perblend epoch formula then original-validated raw-support MarkDiff gate");
                    observations.Add(("allpage-blink:" + count + ":" + page + ":" + side, path, expected[side].Pixels, expected[side].Width, expected[side].Height));
                }
            }
            panel.SetOverlayOptions(window.ImageOptions.Current with { BlinkDifferences = false }); pump(panel.CurrentDisplayOperation);
            foreach (var mode in new[] { 2, 3 })
            {
                panel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = mode }); pump(panel.CurrentDisplayOperation);
                var prior = panel.CaptureSettings(); var hashes = panel.RenderedFrames.Select(frame => ImageComparisonEngine.PixelHash(frame.Pixels, CancellationToken.None)).ToArray();
                panel.OverlayCandidateReady = () => throw new InvalidOperationException("phase3 page display failure");
                var target = prior.LeftFrame == 1 ? 2 : 1; var operation = count == 2 ? panel.SetFramesAsync(target, target) : panel.SetFramesAsync(target, 1, target);
                try { pump(operation); } catch (InvalidOperationException) { }
                panel.OverlayCandidateReady = null;
                check("overlay page transaction preserves " + count + ":" + mode, operation.IsFaulted && panel.CaptureSettings() == prior
                    && panel.RenderedFrames.Select(frame => ImageComparisonEngine.PixelHash(frame.Pixels, CancellationToken.None)).SequenceEqual(hashes),
                    "readonly TIFF raw/page/settings/display adoption atomic on candidate failure");
            }
        }
        RunSelectedClampChecks(window, pane, paths, folder, pump, check, observations);
        RunSavedIdentityChecks(window, pane, folder, pump, check, observations);
        using var traceFile = File.Create(Path.Combine(folder, "report-clock-trace.json"));
        using var writer = new System.Text.Json.Utf8JsonWriter(traceFile, new() { Indented = true });
        writer.WriteStartArray();
        foreach (var trace in traces)
        {
            writer.WriteStartObject(); writer.WriteNumber("count", trace.Count); writer.WriteNumber("start", trace.Start);
            writer.WriteStartArray("clockReads"); foreach (var epoch in trace.Reads) writer.WriteNumberValue(epoch); writer.WriteEndArray();
            writer.WriteStartArray("tuples"); foreach (var tuple in trace.Tuples)
            { writer.WriteStartArray(); foreach (var page in tuple) writer.WriteNumberValue(page); writer.WriteEndArray(); } writer.WriteEndArray();
            writer.WriteStartArray("sampleEpochs"); foreach (var sample in trace.Samples)
            { writer.WriteStartArray(); foreach (var epoch in sample) writer.WriteNumberValue(epoch); writer.WriteEndArray(); } writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void RunSelectedClampChecks(MainWindow window, ComparisonPane pane, string[] paths, string folder, Action<Task> pump,
        Action<string, bool, string> check, List<(string Name, string Png, byte[] Pixels, int Width, int Height)> observations)
    {
        window.ImageOptions.SetOptions(new()); pane.DiscardChanges();
        pane.ApplyProject(new() { Mode = "Image", LeftPath = paths[0], RightPath = paths[1],
            ImageSettings = new() { ShowDifferences = true, BlockSize = 1, OverlayOpacity = .3, ReportAllFrames = true, LeftFrame = 2, RightFrame = 2 } });
        pump(pane.ComparePathsAsync()); var panel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single(); panel.GateClock = new Clock(0);
        pump(panel.NavigateRegionAsync(1));
        var results = new List<(int Mode, bool Matches, string[] Actual, string[] Expected)>();
        foreach (var mode in new[] { 0, 2 })
        {
            panel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = mode }); pump(panel.CurrentDisplayOperation);
            var capture = panel.CaptureReport(); var html = ImageReport.Create(capture, ["left", "right"]);
            File.WriteAllText(Path.Combine(folder, "selected-clamp-" + mode + ".html"), html);
            var set = ImageComparisonEngine.DecodeSelection(capture.Images, [2, 2], 0, true, CancellationToken.None, blockSize: 1);
            IReadOnlyList<ImageComparisonEngine.DecodedFrame>? canvas = null;
            if (mode == 2)
            {
                var pixels = IndependentReportBlend(set.Frames.ToArray(), set.Regions.Width, set.Regions.Height, [0, 0], .3);
                canvas = set.Frames.Select((frame, side) => new ImageComparisonEngine.DecodedFrame(frame.Number, set.Regions.Width, set.Regions.Height, pixels[side])).ToArray();
            }
            // MarkDiff自体は既存無改変原本fixtureで検証済み。今回の期待差はpage-local選択-1。
            var expected = ImageRegionRenderer.Render(set.Frames, set.Regions, 1, selectedDiffIndex: -1, displayCanvas: canvas);
            var hashes = new List<string>(); var expectedHashes = new List<string>();
            foreach (var side in new[] { 0, 1 })
            {
                var sideName = side == 0 ? "left" : "right";
                var tag = Regex.Matches(html, "<img[^>]+>").Select(match => match.Value)
                    .Where(tag => tag.Contains("data-side=\"" + sideName + "\"", StringComparison.Ordinal)).Last();
                var png = Convert.FromBase64String(Regex.Match(tag, "src=\"data:image/png;base64,([^\"]+)").Groups[1].Value);
                var path = Path.Combine(folder, "selected-clamp-" + mode + "-" + sideName + ".png"); File.WriteAllBytes(path, png);
                var actual = HeadlessImageCopyChecks.ReadPng(path); hashes.Add(ImageComparisonEngine.PixelHash(actual.Pixels, CancellationToken.None));
                expectedHashes.Add(ImageComparisonEngine.PixelHash(expected[side].Pixels, CancellationToken.None));
                observations.Add(("selected-clamp:" + mode + ":" + sideName, path, expected[side].Pixels, expected[side].Width, expected[side].Height));
            }
            results.Add((mode, hashes.SequenceEqual(expectedHashes), hashes.ToArray(), expectedHashes.ToArray()));
            check("overlay selected clamp GUI stays captured " + mode, panel.SelectedDiffIndex == 0 && capture.DisplayCapture!.Settings.SelectedDiffIndex == 0,
                "report-local selection only, GUI adopted tuple untouched");
        }
        using (var file = File.Create(Path.Combine(folder, "selected-clamp-trace.json")))
        using (var writer = new System.Text.Json.Utf8JsonWriter(file, new() { Indented = true }))
        {
            writer.WriteStartArray(); foreach (var result in results)
            {
                writer.WriteStartObject(); writer.WriteNumber("mode", result.Mode); writer.WriteBoolean("matchesInheritedMinusOne", result.Matches);
                writer.WriteStartArray("actualSHA256"); foreach (var hash in result.Actual) writer.WriteStringValue(hash); writer.WriteEndArray();
                writer.WriteStartArray("expectedSHA256"); foreach (var hash in result.Expected) writer.WriteStringValue(hash); writer.WriteEndArray(); writer.WriteEndObject();
            } writer.WriteEndArray();
        }
        check("overlay allpage selection never revives", results.All(result => result.Matches), "canonical1161:0→-1→-1, None and Alpha full PNG BGRA");
    }

    private static void RunSavedIdentityChecks(MainWindow window, ComparisonPane pane, string folder, Action<Task> pump,
        Action<string, bool, string> check, List<(string Name, string Png, byte[] Pixels, int Width, int Height)> observations)
    {
        window.ImageOptions.SetOptions(new()); var red = new byte[] { 0, 0, 255, 255 }; var blue = new byte[] { 255, 0, 0, 255 };
        var left = Path.Combine(folder, "identity-left.png"); var right = Path.Combine(folder, "identity-right.png");
        pump(ImagePngStore.SaveAsync(left, new(1, 1, 1, red), [])); pump(ImagePngStore.SaveAsync(right, new(1, 1, 1, blue), []));
        pane.DiscardChanges(); pane.ApplyProject(new() { Mode = "Image", LeftPath = left, RightPath = right,
            ImageSettings = new() { ShowDifferences = false, OverlayOpacity = .3, BlockSize = 1, ReportAllFrames = false } });
        pump(pane.ComparePathsAsync()); var panel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single(); panel.GateClock = new Clock(0);
        panel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = 2 }); pump(panel.CurrentDisplayOperation);
        var original = panel.CaptureReport(); var originalHash = original.DisplayCapture!.InputHashes[0];
        pump(panel.CopyRegionAsync(1, 0, all: true));
        var dirtyPackage = Path.Combine(folder, "identity-dirty.zip"); File.WriteAllText(dirtyPackage, "protected ZIP"); var rejected = false;
        try { pump(window.PackageWorkspaceAsync(dirtyPackage, [Array.IndexOf(window.SessionPanes.ToArray(), pane)], new(IncludeReport: true))); }
        catch (InvalidOperationException error) { rejected = error.Message.Contains("未保存", StringComparison.Ordinal); }
        check("overlay typed dirty package preserves ZIP", rejected && File.ReadAllText(dirtyPackage) == "protected ZIP", "canonical capture does not bypass raw savepoint guard");
        Verify(panel.CaptureReport(), "edited", [blue, blue], [blue, blue]);
        var savedPath = Path.Combine(folder, "identity-saved.png"); pump(panel.SaveToAsync(0, savedPath));
        var saved = panel.CaptureReport(); var savedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(savedPath)));
        check("overlay saved Snapshot identity updates", !panel.HasUnsavedChanges && StringComparer.OrdinalIgnoreCase.Equals(saved.DisplayCapture!.InputHashes[0], savedHash)
            && !StringComparer.OrdinalIgnoreCase.Equals(originalHash, savedHash) && original.Images[0].SourceHash(CancellationToken.None) == originalHash && pane.CaptureProject().LeftPath == savedPath,
            "owned capture retains old Snapshot; successful raw PNG save supplies new Snapshot bytes/SHA/path/savepoint");
        var package = Path.Combine(folder, "identity-saved.zip"); pump(window.PackageWorkspaceAsync(package,
            [Array.IndexOf(window.SessionPanes.ToArray(), pane)], new(IncludeReport: true)));
        using (var archive = System.IO.Compression.ZipFile.OpenRead(package))
        using (var reader = new StreamReader(archive.Entries.Single(entry => entry.FullName == "report.files/1.html").Open()))
            VerifyHtml(reader.ReadToEnd(), "saved-package", [blue, blue], [blue, blue]);
        panel.SetDisplayActive(false); panel.Dispose();
        Verify(original, "capture-after-edit-save-dispose", [[76, 0, 178, 255], [178, 0, 76, 255]], [red, blue]);
        check("overlay saved and original capture clock0", !panel.OverlayTimerRunning, "hidden/dispose does not invalidate owned report frames/sample/originalidentity");

        void Verify(ImageComparisonEngine.ReportInput capture, string name, byte[][] processed, byte[][] raw)
        {
            var clock = new Clock(750); var html = ImageReport.Create(capture, ["left", "right"], clock: clock);
            File.WriteAllText(Path.Combine(folder, "identity-" + name + ".html"), html);
            VerifyHtml(html, name, processed, raw); check("overlay identity capture clock0 " + name, clock.Reads.Count == 0, "selected saved/original capture renders owned pixels");
        }
        void VerifyHtml(string html, string name, byte[][] processed, byte[][] raw)
        {
            for (var side = 0; side < 2; side++) foreach (var originalPixels in new[] { false, true })
            {
                var sideName = side == 0 ? "left" : "right"; if (originalPixels) sideName += "-original";
                var tag = Regex.Matches(html, "<img[^>]+>").Select(match => match.Value).Single(tag => tag.Contains("data-side=\"" + sideName + "\"", StringComparison.Ordinal));
                var png = Convert.FromBase64String(Regex.Match(tag, "src=\"data:image/png;base64,([^\"]+)").Groups[1].Value);
                var path = Path.Combine(folder, "identity-" + name + "-" + sideName + ".png"); File.WriteAllBytes(path, png);
                var actual = HeadlessImageCopyChecks.ReadPng(path); var expected = originalPixels ? raw[side] : processed[side];
                check("overlay identity full BGRA " + name + ":" + sideName, actual.Width == 1 && actual.Height == 1 && actual.Pixels.AsSpan().SequenceEqual(expected), "independent literal BGRA");
                observations.Add(("identity:" + name + ":" + sideName, path, expected, 1, 1));
            }
        }
    }

    private static byte[][] IndependentReportBlend(ImageComparisonEngine.DecodedFrame[] raw, int width, int height, long[] epochs, double? fixedAlpha = null)
    {
        var output = raw.Select(frame =>
        {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < frame.Height; y++) frame.Pixels.AsSpan(y * frame.Width * 4, frame.Width * 4).CopyTo(pixels.AsSpan(y * width * 4));
            return pixels;
        }).ToArray();
        var pairs = raw.Length == 2 ? new[] { (1, 0), (0, 1) } : [(1, 0), (0, 1), (2, 1), (1, 2)];
        for (var pair = 0; pair < pairs.Length; pair++)
        {
            var (source, target) = pairs[pair]; var phase = epochs[pair] % 1000;
            var alpha = fixedAlpha ?? (phase < 200 ? phase / 200d : phase < 500 ? 1 : phase < 700 ? (700 - phase) / 200d : 0);
            for (var y = 0; y < raw[source].Height; y++) for (var x = 0; x < raw[source].Width; x++) for (var channel = 0; channel < 4; channel++)
            {
                var index = (y * width + x) * 4 + channel; var from = raw[source].Pixels[(y * raw[source].Width + x) * 4 + channel];
                output[target][index] = (byte)(output[target][index] * (1 - alpha) + from * alpha);
            }
        }
        return output;
    }
}
