using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static partial class HeadlessImageOverlayChecks
{
    private sealed class Clock(params long[] epochs) : IImageDisplayClock
    {
        internal readonly List<long> Reads = [];
        public long ReadEpochMilliseconds()
        {
            var value = epochs[Math.Min(Reads.Count, epochs.Length - 1)]; Reads.Add(value); return value;
        }
    }
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "image-overlay"); Directory.CreateDirectory(folder);
        var incoming = pane.CaptureProject(); var originalOptions = window.ImageOptions.Current;
        var observations = new List<(string Name, string Png, byte[] Pixels, int Width, int Height)>();
        var reachability = new List<(string Name, string Detail)>();
        var clockTrace = new List<(string Name, ImageDisplaySample Sample, long[] Reads, long[] Gate)>();
        try
        {
            var inherited = new ImageViewSettings(); var explicitDefault = new ImageViewSettings { OverlayOpacity = .3 };
            check("overlay presence value equality/hash", inherited == explicitDefault && inherited.GetHashCode() == explicitDefault.GetHashCode()
                && !inherited.HasExplicitOverlayOpacity && explicitDefault.HasExplicitOverlayOpacity, "private readonly value equality; presence separate");
            check("overlay presence generated with copy", !(inherited with { Zoom = 2 }).HasExplicitOverlayOpacity
                && (explicitDefault with { }).HasExplicitOverlayOpacity && (inherited with { OverlayOpacity = .3 }).HasExplicitOverlayOpacity, "with preserves field/setter distinction");
            var omitted = JsonSerializer.Deserialize("{\"imageSettings\":{}}", ProjectJsonContext.Default.ComparisonProject)!;
            var explicitJson = JsonSerializer.Deserialize("{\"imageSettings\":{\"overlayOpacity\":0.3}}", ProjectJsonContext.Default.ComparisonProject)!;
            check("overlay sourcegen setter presence", !omitted.ImageSettings.HasExplicitOverlayOpacity && explicitJson.ImageSettings.HasExplicitOverlayOpacity, "actual source-generated project loader");
            var optionsPath = Path.Combine(folder, "options.json"); File.WriteAllText(optionsPath, "{\"dragMode\":1}");
            var store = new ImageApplicationOptionsStore(optionsPath);
            check("overlay legacy global defaults", store.Current.OverlayMode == 0 && store.Current.OverlayAlpha == .3 && !store.Current.BlinkDifferences
                && store.Current.AnimationPeriod == 1000 && store.Current.BlinkPeriod == 800, "DragMode-only DTO");
            var global = store.Current with { OverlayMode = 2, OverlayAlpha = .65, BlinkDifferences = true, AnimationPeriod = 200, BlinkPeriod = 8000 };
            check("overlay global complete atomic save", store.SetOptions(global) && store.SetMode(ImageDragMode.RectangleSelect)
                && new ImageApplicationOptionsStore(optionsPath).Current == global with { DragMode = ImageDragMode.RectangleSelect }, "drag update keeps overlay fields");
            var bytes = File.ReadAllBytes(optionsPath);
            foreach (var invalid in new[] { global with { OverlayMode = 4 }, global with { OverlayAlpha = double.NaN },
                global with { AnimationPeriod = 199 }, global with { BlinkPeriod = 8001 } })
                check("overlay global rejects " + invalid, !store.SetOptions(invalid) && File.ReadAllBytes(optionsPath).AsSpan().SequenceEqual(bytes), "disk and complete adopted DTO unchanged");
            store.AddOutputGuard(_ => throw new InvalidOperationException("output guard"));
            check("overlay options outputguard preservation", !store.SetOptions(global with { OverlayAlpha = .1 })
                && File.ReadAllBytes(optionsPath).AsSpan().SequenceEqual(bytes), "atomic failure no partial file");

            RunScopeControlChecks(window, pane, folder, pump, check);
            SpecializedViews.ImagePanel? last = null;
            for (var count = 2; count <= 3; count++)
            {
                var raw = Enumerable.Range(0, count).Select(i => new ImageComparisonEngine.DecodedFrame(1, 8, 6,
                    Enumerable.Range(0, 8 * 6 * 4).Select(n => (byte)(n * 17 + i * 71)).ToArray())).ToArray();
                var paths = raw.Select((_, i) => Path.Combine(folder, count + "-input" + i + ".png")).ToArray();
                for (var i = 0; i < count; i++) pump(ImagePngStore.SaveAsync(paths[i], raw[i], []));
                window.ImageOptions.SetOptions(new());
                pane.DiscardChanges(); pane.ApplyProject(new() { Mode = "Image", LeftPath = paths[0], BasePath = count == 3 ? paths[1] : "", RightPath = paths[^1],
                    ImageSettings = new() { ShowDifferences = false, BlockSize = 1, OverlayOpacity = .3, Zoom = 8, View = "Overlay" } });
                pump(pane.ComparePathsAsync()); Render();
                var panel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single(); last = panel;
                panel.GateClock = new Clock(0);
                check("overlay alias keeps token " + count, panel.CaptureSettings().View == "Overlay"
                    && panel.GetVisualDescendants().OfType<TabControl>().Single(x => x.Name == "ImageDisplayMode").SelectedIndex == 0, "canonical image viewport with global None");
                foreach (var mode in new[] { 0, 1, 2, 3 })
                {
                    var epochs = count == 2 ? new long[] { 200, 300 } : [200, 300, 500, 600];
                    var clock = new Clock(epochs); panel.RenderClock = clock;
                    panel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = mode, BlinkDifferences = false });
                    pump(panel.CurrentDisplayOperation); Render();
                    var expected = Expected(raw, mode);
                    for (var i = 0; i < count; i++)
                    {
                        check("overlay actual Bitmap BGRA " + count + ":" + mode + ":" + i,
                            panel.RenderedFrames[i].Pixels.AsSpan().SequenceEqual(expected[i]), "independent fixed blend phases and each-stage byte trunc");
                        var image = panel.GetVisualDescendants().OfType<Image>().Single(x => x.Name == (i == 0 ? "ImagePaneLeft" : i == count - 1 ? "ImagePaneRight" : "ImagePaneMiddle"));
                        var actualBitmap = ReadPixels((Avalonia.Media.Imaging.WriteableBitmap)image.Source!);
                        check("overlay adopted bitmap matches owned BGRA " + count + ":" + mode + ":" + i,
                            actualBitmap.AsSpan().SequenceEqual(expected[i]), "actual Image.Source bitmap");
                        var png = Path.Combine(folder, count + "-mode" + mode + "-pane" + i + ".png");
                        pump(ImagePngStore.SaveAsync(png, panel.RenderedFrames[i], []));
                        observations.Add((count + ":" + mode + ":" + i, png, expected[i], 8, 6));
                    }
                    check("overlay individual clock reads " + count + ":" + mode, panel.AdoptedDisplay!.Sample.Epochs.SequenceEqual(mode == 3 ? epochs : []), "gateClock separate; mode0/1/2 no blend reads");
                    clockTrace.Add((count + ":mode:" + mode, panel.AdoptedDisplay.Sample, clock.Reads.ToArray(), []));
                    check("overlay raw/history unchanged " + count + ":" + mode, !panel.HasUnsavedChanges && panel.HistoryCount == 0
                        && panel.CaptureEditFrames().Select((frame, i) => frame.Pixels.AsSpan().SequenceEqual(raw[i].Pixels)).All(x => x), "display only");
                    if (mode is 2 or 3) RunSelectedReportChecks(window, pane, panel, raw, expected, count + "-mode" + mode,
                        folder, pump, check, observations);
                }
                screenshot("image-overlay-" + count + ".png");
                panel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = 2, BlinkDifferences = false }); pump(panel.CurrentDisplayOperation);
                pump(panel.ApplySettingsAsync(panel.CaptureSettings() with { ShowDifferences = true }));
                pump(panel.NavigateRegionAsync(1)); pump(panel.CurrentDisplayOperation);
                foreach (var epoch in new long[] { 0, 400 })
                {
                    var blinkClock = new Clock(epoch); panel.RenderClock = blinkClock;
                    panel.SetOverlayOptions(window.ImageOptions.Current with { BlinkDifferences = true }); pump(panel.CurrentDisplayOperation);
                    if (epoch > 0) { panel.GateClock = new Clock(epoch); panel.TickOverlay(); pump(panel.DisplayWorkerOperation); }
                    void SaveBlinkFailure(SpecializedViews.ImagePanel failedPanel, int side, long failedEpoch, byte[] expectedPixels)
                    {
                        var actual = failedPanel.RenderedFrames[side];
                        var stem = count + "-blink" + failedEpoch + "-pane" + side + "-failure";
                        var actualPath = Path.Combine(folder, stem + "-actual.bgra"); var expectedPath = Path.Combine(folder, stem + "-expected.bgra");
                        using (var target = new FileStream(actualPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) target.Write(actual.Pixels);
                        using (var target = new FileStream(expectedPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) target.Write(expectedPixels);
                        using var file = new FileStream(Path.Combine(folder, stem + ".json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        using var writer = new Utf8JsonWriter(file);
                        writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1); writer.WriteNumber("count", count); writer.WriteNumber("side", side); writer.WriteNumber("epoch", failedEpoch);
                        writer.WriteNumber("width", actual.Width); writer.WriteNumber("height", actual.Height); writer.WriteNumber("selectedDiffIndex", failedPanel.SelectedDiffIndex);
                        writer.WriteString("actualBGRA", actualPath); writer.WriteNumber("actualBytes", actual.Pixels.Length); writer.WriteString("actualSHA256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(actual.Pixels)));
                        writer.WriteString("expectedBGRA", expectedPath); writer.WriteNumber("expectedBytes", expectedPixels.Length); writer.WriteString("expectedSHA256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expectedPixels)));
                        writer.WritePropertyName("capturedProject"); JsonSerializer.Serialize(writer, new ComparisonProject { ImageSettings = failedPanel.CaptureSettings() }, ProjectJsonContext.Default.ComparisonProject);
                        writer.WritePropertyName("globalOptions"); JsonSerializer.Serialize(writer, window.ImageOptions.Current, ImageApplicationOptionsJsonContext.Default.ImageApplicationOptions);
                        writer.WriteString("displayTaskStatus", failedPanel.CurrentDisplayOperation.Status.ToString()); writer.WriteString("workerTaskStatus", failedPanel.DisplayWorkerOperation.Status.ToString());
                        writer.WriteStartArray("renderClockReads"); foreach (var value in ((Clock)failedPanel.RenderClock).Reads) writer.WriteNumberValue(value); writer.WriteEndArray();
                        writer.WriteStartArray("gateClockReads"); foreach (var value in ((Clock)failedPanel.GateClock).Reads) writer.WriteNumberValue(value); writer.WriteEndArray();
                        writer.WritePropertyName("adoptedDisplay");
                        if (failedPanel.AdoptedDisplay is not { } display) writer.WriteNullValue();
                        else
                        {
                            var settings = display.Settings; writer.WriteStartObject(); writer.WriteStartObject("settings");
                            writer.WriteNumber("mode", settings.Mode); writer.WriteNumber("alpha", settings.Alpha); writer.WriteBoolean("showDifferences", settings.ShowDifferences); writer.WriteBoolean("blinkDifferences", settings.BlinkDifferences);
                            writer.WriteNumber("animationPeriod", settings.AnimationPeriod); writer.WriteNumber("blinkPeriod", settings.BlinkPeriod); writer.WriteNumber("blockSize", settings.BlockSize); writer.WriteNumber("threshold", settings.Threshold);
                            writer.WriteNumber("highlightAlpha", settings.HighlightAlpha); writer.WriteNumber("selectedDiffIndex", settings.SelectedDiffIndex); writer.WriteEndObject();
                            writer.WriteStartArray("sampleEpochs"); foreach (var value in display.Sample.Epochs) writer.WriteNumberValue(value); writer.WriteEndArray();
                            writer.WriteStartArray("blendAlphas"); foreach (var value in display.Sample.BlendAlphas) writer.WriteNumberValue(value); writer.WriteEndArray(); writer.WriteBoolean("highlightVisible", display.Sample.HighlightVisible); writer.WriteEndObject();
                        }
                        writer.WriteEndObject(); writer.Flush();
                    }
                    var expected = Expected(raw, 2);
                    if (epoch == 400) foreach (var pixels in expected) Highlight(pixels);
                    for (var i = 0; i < count; i++)
                    {
                        var actualPixels = panel.RenderedFrames[i].Pixels;
                        if (!actualPixels.AsSpan().SequenceEqual(expected[i])) SaveBlinkFailure(panel, i, epoch, expected[i]);
                        check("overlay selected blink ordering " + count + ":" + epoch + ":" + i,
                            actualPixels.AsSpan().SequenceEqual(expected[i]), "overlay bytes then blink suppression then selected red highlight");
                        var png = Path.Combine(folder, count + "-blink" + epoch + "-pane" + i + ".png");
                        pump(ImagePngStore.SaveAsync(png, panel.RenderedFrames[i], [])); observations.Add((count + ":blink:" + epoch + ":" + i, png, expected[i], 8, 6));
                    }
                    check("overlay blink extra individual clock " + count + ":" + epoch,
                        panel.AdoptedDisplay!.Sample.Epochs.SequenceEqual(new[] { epoch }) && panel.AdoptedDisplay.Sample.HighlightVisible == (epoch == 400), "static alpha consumes blink clock only");
                    clockTrace.Add((count + ":blink:" + epoch, panel.AdoptedDisplay.Sample, blinkClock.Reads.ToArray(), []));
                }
                panel.SetOverlayOptions(window.ImageOptions.Current with { BlinkDifferences = false }); pump(panel.CurrentDisplayOperation);
                pump(panel.ApplySettingsAsync(panel.CaptureSettings() with { ShowDifferences = false }));
                panel.RenderClock = new Clock(500); panel.GateClock = new Clock(1000);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                panel.OverlayCandidateReady = () => { entered.TrySetResult(); return release.Task; };
                panel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = 3 });
                var requestTask = panel.CurrentDisplayOperation; pump(entered.Task);
                var started = panel.DisplayCandidatesStarted; for (var i = 0; i < 100; i++) panel.TickOverlay();
                check("overlay pending ticks do not spawn worker " + count, panel.DisplayCandidatesStarted == started && !requestTask.IsCompleted, "100 tick requests, one computed candidate");
                panel.OverlayCandidateReady = null; release.TrySetResult(); pump(requestTask); pump(panel.DisplayWorkerOperation);
                check("overlay slow valid adoption and request completion " + count, requestTask.IsCompletedSuccessfully
                    && panel.DisplayCandidatesStarted == started + 1, "valid candidate adopted then one pending refresh");
                clockTrace.Add((count + ":slow-candidate", panel.AdoptedDisplay!.Sample, ((Clock)panel.RenderClock).Reads.ToArray(), ((Clock)panel.GateClock).Reads.ToArray()));
                check("overlay capture while temporal active " + count, panel.CaptureReport().Images.Count == count, "adopted frame capture not blocked by timer worker");
                var adopted = panel.CaptureSettings(); var fixedPixels = panel.RenderedFrames.Select(x => x.Pixels.ToArray()).ToArray();
                panel.OverlayCandidateReady = () => throw new InvalidOperationException("candidate failure");
                panel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = 2, OverlayAlpha = .9 });
                try { pump(panel.CurrentDisplayOperation); } catch (InvalidOperationException) { }
                check("overlay failed candidate keeps adopted state " + count, panel.CaptureSettings() == adopted
                    && panel.RenderedFrames.Select((x, i) => x.Pixels.AsSpan().SequenceEqual(fixedPixels[i])).All(x => x), "no bitmap or project setting rollback from stale controls");
                panel.OverlayCandidateReady = null;
                panel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = 3 }); pump(panel.CurrentDisplayOperation);
                check("overlay successful adoption clears own failure " + count,
                    !panel.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("candidate failure", StringComparison.Ordinal) == true), "display error restored to adopted region status only");
                var pixelView = panel.GetVisualDescendants().OfType<TabControl>().Single(x => x.Name == "ImageDisplayMode");
                pixelView.SelectedIndex = 1; Render(); check("overlay pixel view stops timer " + count, !panel.OverlayTimerRunning, "pixel difference not canonical overlay");
                pixelView.SelectedIndex = 0; Render();
                panel.SetDisplayActive(false); var priorReads = ((Clock)panel.RenderClock).Reads.Count;
                panel.TickOverlay(); check("overlay hidden panel stops work " + count, !panel.OverlayTimerRunning
                    && ((Clock)panel.RenderClock).Reads.Count == priorReads, "outer/special view active notification");
                panel.SetDisplayActive(true); pump(panel.CurrentDisplayOperation); Render();
                check("overlay visible resumes timer epoch " + count, panel.OverlayTimerRunning && panel.AdoptedDisplay!.Sample.Epochs.All(epoch => epoch == 500), "no start phase reset");
            }
            if (last is not null)
            {
                var failures = new List<(string Name, bool Canceled, bool Faulted, bool Preserved, int Mode, string[] Before, string[] After)>();
                foreach (var mode in new[] { 2, 3 })
                {
                    last.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = mode, BlinkDifferences = false }); pump(last.CurrentDisplayOperation);
                    foreach (var action in new[] { "cancel-after-frame", "offset-display-failure", "rotation-display-failure" })
                    {
                        var previous = last.CaptureSettings();
                        var beforeFrames = last.RenderedFrames.ToArray();
                        var before = beforeFrames.Select(x => ImageComparisonEngine.PixelHash(x.Pixels, CancellationToken.None)).ToArray();
                        Task operation; using var caller = new CancellationTokenSource();
                        if (action == "cancel-after-frame")
                        {
                            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            last.OverlayCandidateReady = () => { entered.TrySetResult(); return release.Task; };
                            operation = last.ApplySettingsAsync(previous with { OverlayOpacity = .4 }, caller.Token);
                            pump(entered.Task); caller.Cancel(); last.OverlayCandidateReady = null; release.TrySetResult();
                        }
                        else
                        {
                            last.OverlayCandidateReady = () => throw new InvalidOperationException("phase3 display failure");
                            var requested = action == "offset-display-failure" ? previous with { LeftOffset = new(previous.LeftOffset.X + 1, previous.LeftOffset.Y) }
                                : previous with { LeftOrientation = new() { Rotation = 90 } };
                            operation = last.ApplySettingsAsync(requested);
                        }
                        try { pump(operation); } catch (Exception error) when (error is OperationCanceledException or InvalidOperationException) { }
                        pump(last.DisplayWorkerOperation); Render();
                        var afterFrames = last.RenderedFrames.ToArray();
                        var after = afterFrames.Select(x => ImageComparisonEngine.PixelHash(x.Pixels, CancellationToken.None)).ToArray();
                        var bitmapHashes = last.GetVisualDescendants().OfType<Image>().Where(x => x.Name?.StartsWith("ImagePane", StringComparison.Ordinal) == true)
                            .Select(x => ImageComparisonEngine.PixelHash(ReadPixels((Avalonia.Media.Imaging.WriteableBitmap)x.Source!), CancellationToken.None)).ToArray();
                        var preserved = last.CaptureSettings() == previous && before.SequenceEqual(after) && before.SequenceEqual(bitmapHashes)
                            && !last.HasUnsavedChanges && last.HistoryCount == 0;
                        failures.Add((mode + ":" + action, operation.IsCanceled, operation.IsFaulted, preserved, last.AdoptedDisplay!.Settings.Mode, before, after));
                        for (var i = 0; i < beforeFrames.Length; i++)
                        {
                            pump(ImagePngStore.SaveAsync(Path.Combine(folder, "phase3-" + mode + "-" + action + "-before" + i + ".png"), beforeFrames[i], []));
                            pump(ImagePngStore.SaveAsync(Path.Combine(folder, "phase3-" + mode + "-" + action + "-after" + i + ".png"), afterFrames[i], []));
                        }
                        last.OverlayCandidateReady = null; pump(last.ApplySettingsAsync(previous)); pump(last.CurrentDisplayOperation);
                    }
                }
                using (var file = File.Create(Path.Combine(folder, "phase3-transaction-failures.json")))
                using (var writer = new Utf8JsonWriter(file))
                {
                    writer.WriteStartArray(); foreach (var item in failures)
                    {
                        writer.WriteStartObject(); writer.WriteString("name", item.Name); writer.WriteBoolean("canceled", item.Canceled);
                        writer.WriteBoolean("faulted", item.Faulted); writer.WriteBoolean("preserved", item.Preserved); writer.WriteNumber("adoptedMode", item.Mode);
                        writer.WriteStartArray("beforeHashes"); foreach (var hash in item.Before) writer.WriteStringValue(hash); writer.WriteEndArray();
                        writer.WriteStartArray("afterHashes"); foreach (var hash in item.After) writer.WriteStringValue(hash); writer.WriteEndArray(); writer.WriteEndObject();
                    } writer.WriteEndArray();
                }
                foreach (var item in failures) check("overlay transaction " + item.Name,
                    item.Preserved && (item.Name.Contains("cancel", StringComparison.Ordinal) ? item.Canceled : item.Faulted), "caller terminal and prior adopted settings/raw/actual bitmap preserved");
                var controls = last.GetVisualDescendants().OfType<Control>().Where(x => x.Name is "ImageOverlayMode" or "ImageAlphaScope" or "ImageOpacity" or "ImageAnimationPeriod" or "ImageBlink" or "ImageBlinkPeriod").ToArray();
                check("overlay concrete single control row", controls.Length == 6 && controls.All(x => x.Bounds.Width > 0), "mode/scope/one alpha/P/blink/B");
                var toolbar = last.GetVisualDescendants().OfType<ScrollViewer>().Single(x => x.Name == "ImageToolbar");
                var width = window.Width; var height = window.Height;
                var alpha = last.GetVisualDescendants().OfType<Slider>().Single(x => x.Name == "ImageOpacity"); var minHeight = alpha.MinHeight;
                var periods = controls.OfType<NumericUpDown>().ToArray(); var periodValues = periods.Select(x => x.Value).ToArray();
                try
                {
                    foreach (var period in periods) { period.Value = 8000; pump(last.CurrentDisplayOperation); }
                    foreach (var tall in new[] { minHeight, 64d, 80d }.Distinct()) foreach (var compact in new[] { false, true })
                    {
                        alpha.MinHeight = tall; window.Width = compact ? window.MinWidth : width; window.Height = compact ? window.MinHeight : height; Render();
                        foreach (var control in controls)
                        {
                            control.BringIntoView(); Render(); var position = control.TranslatePoint(default, toolbar); var screen = control.TranslatePoint(default, window);
                            var label = compact + ":" + tall + ":" + control.Name;
                            var detail = $"toolbar={position};screen={screen};bounds={control.Bounds};viewport={toolbar.Viewport};window={window.Bounds}";
                            reachability.Add((label, detail));
                            check("overlay control reachable " + label, position.HasValue && screen.HasValue && position.Value.Y >= -1
                                && position.Value.Y + control.Bounds.Height <= toolbar.Bounds.Height + 1
                                && screen.Value.Y >= 0 && screen.Value.Y + control.Bounds.Height <= window.Bounds.Height + 1, detail);
                            if (control is NumericUpDown period)
                            {
                                var textBox = period.GetVisualDescendants().OfType<TextBox>().Single();
                                var presenter = textBox.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().Single();
                                var text = new FormattedText("8000", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                    new Typeface(textBox.FontFamily, textBox.FontStyle, textBox.FontWeight, textBox.FontStretch), textBox.FontSize, Brushes.Black);
                                var textDetail = $"text={textBox.Text};required={text.Width};presenter={presenter.Bounds};numeric={period.Bounds}";
                                reachability.Add((label + ":full-number", textDetail));
                                check("overlay four-digit period visible " + label, textBox.Text == "8000" && presenter.Bounds.Width >= text.Width, textDetail);
                            }
                            screenshot("image-overlay-control-" + compact + "-" + tall + "-" + control.Name + ".png");
                        }
                        toolbar.ScrollToEnd(); Render();
                        var save = last.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "ImageSavePng"); var savePosition = save.TranslatePoint(default, toolbar);
                        check("overlay PNG save reachable " + compact + ":" + tall, savePosition.HasValue && savePosition.Value.Y >= -1
                            && savePosition.Value.Y + save.Bounds.Height <= toolbar.Bounds.Height + 1, $"save={savePosition};toolbar={toolbar.Bounds}");
                        var panes = last.GetVisualDescendants().OfType<Image>().Where(x => x.Name?.StartsWith("ImagePane", StringComparison.Ordinal) == true)
                            .Select(x => x.GetVisualAncestors().OfType<ScrollViewer>().First().Bounds.Height).ToArray();
                        check("overlay three pane viewport preserved " + compact + ":" + tall, panes.Length == 3 && panes.All(x => x >= 24), string.Join(",", panes));
                    }
                }
                finally { for (var i = 0; i < periods.Length; i++) periods[i].Value = periodValues[i]; pump(last.CurrentDisplayOperation);
                    alpha.MinHeight = minHeight; window.Width = width; window.Height = height; Render(); }
                controls[0].BringIntoView(); Render(); screenshot("image-overlay-controls.png");
                last.Dispose(); last.TickOverlay(); check("overlay dispose stops timer and sample", !last.OverlayTimerRunning && last.AdoptedDisplay is null, "no candidate adoption after disposal");
            }
            // global継承とexplicitを同じstoreの実比較tabで観測する。
            window.ImageOptions.SetOptions(new() { OverlayMode = 2, OverlayAlpha = .65 });
            pane.DiscardChanges(); pane.ApplyProject(new() { Mode = "Image", LeftPath = Path.Combine(folder, "3-input0.png"),
                RightPath = Path.Combine(folder, "3-input2.png"), ImageSettings = new() { OverlayOpacity = .3, ShowDifferences = false, BlockSize = 1 } });
            pump(pane.ComparePathsAsync()); Render();
            var originalIndex = Array.IndexOf(window.SessionPanes.ToArray(), pane);
            var explicitPanel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
            var inheritedPane = window.AddSession();
            try
            {
                inheritedPane.ApplyProject(pane.CaptureProject() with { ImageSettings = new() { ShowDifferences = false, BlockSize = 1 } });
                pump(inheritedPane.ComparePathsAsync()); Render();
                var inheritedPanel = inheritedPane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single(); inheritedPanel.GateClock = new Clock(0);
                check("overlay actual tab inherits global opacity", inheritedPanel.CaptureSettings().OverlayOpacity == .65 && !inheritedPanel.CaptureSettings().HasExplicitOverlayOpacity,
                    "sourcegen omitted/default project inherits GUI global");
                window.ImageOptions.SetOptions(window.ImageOptions.Current with { OverlayAlpha = .9 }); pump(inheritedPanel.CurrentDisplayOperation);
                check("overlay global affects inherited tab only", inheritedPanel.CaptureSettings().OverlayOpacity == .9
                    && explicitPanel.CaptureSettings().OverlayOpacity == .3 && !explicitPanel.OverlayTimerRunning, "inactive explicit tab requested/adopted split");
                window.SelectSession(originalIndex); Render(); pump(explicitPanel.CurrentDisplayOperation);
                check("overlay explicit restored after tab visibility", explicitPanel.CaptureSettings().OverlayOpacity == .3
                    && !inheritedPanel.OverlayTimerRunning, "global .9 preserves explicit .3");
            }
            finally
            {
                var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(x => x.Items.OfType<TabItem>().Any(t => ReferenceEquals(t.Content, inheritedPane)));
                var tab = tabs.Items.OfType<TabItem>().Single(x => ReferenceEquals(x.Content, inheritedPane));
                ((StackPanel)tab.Header!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.SelectSession(originalIndex); Render();
            }
            var detachEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var detachRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            explicitPanel.OverlayCandidateReady = () => { detachEntered.TrySetResult(); return detachRelease.Task; };
            explicitPanel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = 1 }); var detachTask = explicitPanel.CurrentDisplayOperation; pump(detachEntered.Task);
            var viewTabs = pane.GetVisualDescendants().OfType<TabControl>().Single(x => x.Items.OfType<TabItem>().Any(t => ReferenceEquals(t.Content, explicitPanel)));
            var imageTab = viewTabs.Items.OfType<TabItem>().Single(x => ReferenceEquals(x.Content, explicitPanel));
            imageTab.Content = null; Render();
            check("overlay actual detach completes state task", detachTask.IsCompleted && !explicitPanel.OverlayTimerRunning, "real TabItem content removal invalidates computed candidate");
            explicitPanel.OverlayCandidateReady = null; detachRelease.TrySetResult(); pump(explicitPanel.DisplayWorkerOperation);
            imageTab.Content = explicitPanel; Render(); pump(explicitPanel.CurrentDisplayOperation); pump(explicitPanel.DisplayWorkerOperation);
            check("overlay actual reattach reaches latest candidate", explicitPanel.AdoptedDisplay?.Settings.Mode == 1, "pending refresh retained after direct detach");
            window.ImageOptions.SetOptions(new());
            pump(explicitPanel.ApplySettingsAsync(explicitPanel.CaptureSettings() with { ShowDifferences = false,
                LeftOrientation = new() { Rotation = 90 }, RightOrientation = new() { FlipHorizontal = true },
                LeftOffset = new(2, 0), RightOffset = new(0, 2) }));
            explicitPanel.GateClock = new Clock(0);
            foreach (var mode in new[] { 1, 2, 3 })
            {
                explicitPanel.RenderClock = new Clock(200, 300); explicitPanel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = mode }); pump(explicitPanel.CurrentDisplayOperation);
                var expected = OffsetExpected(mode);
                for (var i = 0; i < 2; i++)
                {
                    var image = explicitPanel.GetVisualDescendants().OfType<Image>().Single(x => x.Name == (i == 0 ? "ImagePaneLeft" : "ImagePaneRight"));
                    check("overlay rotation offset support bitmap " + mode + ":" + i,
                        ReadPixels((Avalonia.Media.Imaging.WriteableBitmap)image.Source!).AsSpan().SequenceEqual(expected[i]), "literal CCW/flip mappings; distinct source rectangles with transparent canvas");
                    var png = Path.Combine(folder, "offset-rotation-" + mode + "-" + i + ".png"); pump(ImagePngStore.SaveAsync(png, explicitPanel.RenderedFrames[i], []));
                    observations.Add(("offset-rotation:" + mode + ":" + i, png, expected[i], 8, 8));
                }
            }
            using var resource = typeof(HeadlessImageOverlayChecks).Assembly.GetManifestResourceStream("DiffBeacon.ImageInsertionHighlight.Golden.json.gz")!;
            using var gzip = new System.IO.Compression.GZipStream(resource, System.IO.Compression.CompressionMode.Decompress);
            using var insertionGolden = JsonDocument.Parse(gzip);
            foreach (var name in new[] { "1-transparent-middle", "2-insert-middle" })
            {
                var item = insertionGolden.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
                var inputs = item.GetProperty("inputs").EnumerateArray().ToArray();
                var raw = inputs.Select(x => new ImageComparisonEngine.DecodedFrame(1, x.GetProperty("width").GetInt32(), x.GetProperty("height").GetInt32(), Convert.FromHexString(x.GetProperty("bgraHex").GetString()!))).ToArray();
                var paths = inputs.Select((x, i) => Path.Combine(folder, name + "-input" + i + ".png")).ToArray();
                for (var i = 0; i < paths.Length; i++) File.WriteAllBytes(paths[i], Convert.FromBase64String(inputs[i].GetProperty("pngBase64").GetString()!));
                window.ImageOptions.SetOptions(new()); pane.DiscardChanges();
                var axis = name[0] - '0';
                pane.ApplyProject(new() { Mode = "Image", LeftPath = paths[0], RightPath = paths[1], LeftReadOnly = true,
                    ImageSettings = new() { InsertionDeletionMode = axis, BlockSize = 1, HighlightAlpha = .7, OverlayOpacity = .3 } });
                pump(pane.ComparePathsAsync()); Render();
                var panel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single(); panel.GateClock = new Clock(0);
                var comparison = ImageComparisonEngine.CompareDecoded(raw, 0, false, CancellationToken.None, blockSize: 1, insertionDeletionMode: axis);
                var aligned = item.GetProperty("states")[1].GetProperty("panes").EnumerateArray().ToArray();
                for (var i = 0; i < 2; i++) check("overlay aligned canonical raw " + name + ":" + i,
                    comparison.Frames[i].Pixels.AsSpan().SequenceEqual(Convert.FromHexString(aligned[i].GetProperty("alignedRawBgraHex").GetString()!)), "unchanged original alignment golden");
                foreach (var mode in new[] { 1, 2, 3 })
                {
                    panel.RenderClock = new Clock(200, 300); panel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = mode }); pump(panel.CurrentDisplayOperation);
                    var canvas = Expected(comparison.Frames.ToArray(), mode);
                    var display = comparison.Frames.Select((x, i) => x with { Pixels = canvas[i] }).ToArray();
                    var expected = ImageRegionRenderer.Render(comparison.Frames, comparison.Regions, 1, .7, token: CancellationToken.None,
                        alignment: comparison.Alignment, displayCanvas: display);
                    for (var i = 0; i < 2; i++)
                    {
                        var image = panel.GetVisualDescendants().OfType<Image>().Single(x => x.Name == (i == 0 ? "ImagePaneLeft" : "ImagePaneRight"));
                        check("overlay aligned ghost actual Bitmap " + name + ":" + mode + ":" + i,
                            ReadPixels((Avalonia.Media.Imaging.WriteableBitmap)image.Source!).AsSpan().SequenceEqual(expected[i].Pixels),
                            "canonical aligned raw plus independent fixed blend, existing canonical MarkDiff with Alignment; detects GUI omission");
                        var png = Path.Combine(folder, name + "-mode" + mode + "-pane" + i + ".png"); pump(ImagePngStore.SaveAsync(png, panel.RenderedFrames[i], []));
                        observations.Add((name + ":" + mode + ":" + i, png, expected[i].Pixels, expected[i].Width, expected[i].Height));
                    }
                }
                screenshot("image-overlay-" + name + ".png");
            }
            // 小さい原画のoffsetで最大canvasを作る。独立の低上限は導入しない。
            window.ImageOptions.SetOptions(new());
            var budgetPaths = Enumerable.Range(0, 3).Select(i => Path.Combine(folder, "budget-input" + i + ".png")).ToArray();
            for (var i = 0; i < 3; i++) pump(ImagePngStore.SaveAsync(budgetPaths[i], new(1, 1, 1, [(byte)(i + 1), 2, 3, 255]), []));
            pane.DiscardChanges(); pane.ApplyProject(new() { Mode = "Image", LeftPath = budgetPaths[0], BasePath = budgetPaths[1], RightPath = budgetPaths[2],
                LeftReadOnly = true, BaseReadOnly = true, RightReadOnly = true,
                ImageSettings = new() { BlockSize = 256, MiddleOffset = new(3999, 3999), ShowDifferences = true, OverlayOpacity = .3, Zoom = .1 } });
            pump(pane.ComparePathsAsync()); Render();
            var budgetPanel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
            var budgetSettings = budgetPanel.CaptureSettings();
            var beforeHashes = budgetPanel.RenderedFrames.Select(x => ImageComparisonEngine.PixelHash(x.Pixels, CancellationToken.None)).ToArray();
            check("overlay None maximum canvas old acceptance", budgetPanel.RenderedFrames.All(x => x.Width == 4000 && x.Height == 4000)
                && budgetPanel.AdoptedDisplay!.Settings.Mode == 0, "16Mcanvas, old CanvasWork64M; no extra None clone/classification");
            var budgetClock = new Clock(200, 300, 500, 600); budgetPanel.RenderClock = budgetClock;
            budgetPanel.SetOverlayOptions(window.ImageOptions.Current with { OverlayMode = 2 });
            var budgetRejected = false; try { pump(budgetPanel.CurrentDisplayOperation); } catch (InvalidOperationException) { budgetRejected = true; }
            check("overlay GUI actual budget rejects before clone and clock", budgetRejected && budgetPanel.CurrentDisplayOperation.IsCompleted && budgetClock.Reads.Count == 0
                && budgetPanel.CaptureSettings() == budgetSettings && budgetPanel.RenderedFrames.Select((x, i) => ImageComparisonEngine.PixelHash(x.Pixels, CancellationToken.None) == beforeHashes[i]).All(x => x)
                && !budgetPanel.HasUnsavedChanges && budgetPanel.HistoryCount == 0, "64Mbaseline + 48Mpreparation + 144Mstate + grid/source >256M; adopted frame/settings/raw retained");
            for (var i = 0; i < 3; i++)
            {
                var image = budgetPanel.GetVisualDescendants().OfType<Image>().Single(x => x.Name == new[] { "ImagePaneLeft", "ImagePaneMiddle", "ImagePaneRight" }[i]);
                check("overlay budget failure actual bitmap retained " + i,
                    ImageComparisonEngine.PixelHash(ReadPixels((Avalonia.Media.Imaging.WriteableBitmap)image.Source!), CancellationToken.None) == beforeHashes[i], "full actual BGRA hash");
            }
            using (var file = File.Create(Path.Combine(folder, "budget-observation.json")))
            using (var writer = new Utf8JsonWriter(file))
            {
                writer.WriteStartObject(); writer.WriteNumber("canvasPixels", 16000000); writer.WriteNumber("canvasWork", 64000000);
                writer.WriteNumber("limit", 256000000); writer.WriteBoolean("rejected", budgetRejected); writer.WriteBoolean("operationComplete", budgetPanel.CurrentDisplayOperation.IsCompleted);
                writer.WriteNumber("clockReads", budgetClock.Reads.Count); writer.WriteStartArray("adoptedSHA256"); foreach (var hash in beforeHashes) writer.WriteStringValue(hash); writer.WriteEndArray(); writer.WriteEndObject();
            }
            RunFrameBudgetAndStateChecks(window, budgetPanel, folder, pump, check, observations);
            RunMultipageReportChecks(window, pane, folder, pump, check, observations);
            using (var file = File.Create(Path.Combine(folder, "control-reachability.json")))
            using (var writer = new Utf8JsonWriter(file))
            {
                writer.WriteStartArray(); foreach (var item in reachability)
                { writer.WriteStartObject(); writer.WriteString("name", item.Name); writer.WriteString("detail", item.Detail); writer.WriteEndObject(); }
                writer.WriteEndArray();
            }
        }
        finally
        {
            using (var file = File.Create(Path.Combine(folder, "clock-trace.json")))
            using (var writer = new Utf8JsonWriter(file))
            {
                writer.WriteStartArray(); foreach (var item in clockTrace)
                {
                    writer.WriteStartObject(); writer.WriteString("name", item.Name);
                    writer.WriteStartArray("sampleEpochs"); foreach (var value in item.Sample.Epochs) writer.WriteNumberValue(value); writer.WriteEndArray();
                    writer.WriteStartArray("blendAlphas"); foreach (var value in item.Sample.BlendAlphas) writer.WriteNumberValue(value); writer.WriteEndArray();
                    writer.WriteBoolean("highlightVisible", item.Sample.HighlightVisible);
                    writer.WriteStartArray("renderClockReads"); foreach (var value in item.Reads) writer.WriteNumberValue(value); writer.WriteEndArray();
                    writer.WriteStartArray("gateClockReads"); foreach (var value in item.Gate) writer.WriteNumberValue(value); writer.WriteEndArray(); writer.WriteEndObject();
                } writer.WriteEndArray();
            }
            using (var file = File.Create(Path.Combine(folder, "observations.json")))
            using (var writer = new Utf8JsonWriter(file))
            {
                writer.WriteStartArray();
                foreach (var item in observations)
                {
                    writer.WriteStartObject(); writer.WriteString("name", item.Name); writer.WriteString("png", item.Png);
                    writer.WriteNumber("width", item.Width); writer.WriteNumber("height", item.Height); writer.WriteBase64String("bgraBase64", item.Pixels); writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            window.ImageOptions.SetOptions(originalOptions); pane.DiscardChanges(); pane.ApplyProject(incoming); pump(pane.ComparePathsAsync());
        }
        void Render() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    }
    private static byte[][] Expected(ImageComparisonEngine.DecodedFrame[] raw, int mode)
    {
        var output = raw.Select(frame => frame.Pixels.ToArray()).ToArray();
        if (mode == 0) return output;
        var pairs = raw.Length == 2 ? new[] { (1, 0), (0, 1) } : [(1, 0), (0, 1), (2, 1), (1, 2)];
        var fixedAlphas = new[] { 1d, 1d, 1d, .5d };
        for (var step = 0; step < pairs.Length; step++)
        {
            var (source, destination) = pairs[step]; var alpha = mode == 3 ? fixedAlphas[step] : .3;
            for (var offset = 0; offset < raw[source].Pixels.Length; offset++)
                if (mode != 1 || offset % 4 != 3) output[destination][offset] = mode == 1
                    ? (byte)(output[destination][offset] ^ raw[source].Pixels[offset])
                    : (byte)(output[destination][offset] * (1 - alpha) + raw[source].Pixels[offset] * alpha);
        }
        return output;
    }
    private static byte[] ReadPixels(Avalonia.Media.Imaging.WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock(); var pixels = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
        for (var y = 0; y < bitmap.PixelSize.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(buffer.Address, y * buffer.RowBytes), pixels, y * bitmap.PixelSize.Width * 4, bitmap.PixelSize.Width * 4);
        return pixels;
    }
    private static void Highlight(byte[] pixels)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] == 0)
            { pixels[offset] = 64; pixels[offset + 1] = 64; pixels[offset + 2] = 255; pixels[offset + 3] = (byte)(255 * .7); }
            else for (var channel = 0; channel < 3; channel++)
                pixels[offset + channel] = (byte)(pixels[offset + channel] * (1 - .7) + (channel == 2 ? 255 : 64) * .7);
        }
    }
    private static byte[][] OffsetExpected(int mode)
    {
        var sources = new[] { new byte[6 * 8 * 4], new byte[8 * 6 * 4] };
        for (var y = 0; y < 6; y++) for (var x = 0; x < 8; x++) for (var channel = 0; channel < 4; channel++)
        {
            sources[0][((7 - x) * 6 + y) * 4 + channel] = (byte)(((y * 8 + x) * 4 + channel) * 17);
            sources[1][(y * 8 + 7 - x) * 4 + channel] = (byte)(((y * 8 + x) * 4 + channel) * 17 + 142);
        }
        var widths = new[] { 6, 8 }; var heights = new[] { 8, 6 }; var ox = new[] { 2, 0 }; var oy = new[] { 0, 2 };
        var output = new[] { new byte[8 * 8 * 4], new byte[8 * 8 * 4] };
        for (var pane = 0; pane < 2; pane++) for (var y = 0; y < heights[pane]; y++) for (var x = 0; x < widths[pane]; x++)
            sources[pane].AsSpan((y * widths[pane] + x) * 4, 4).CopyTo(output[pane].AsSpan(((y + oy[pane]) * 8 + x + ox[pane]) * 4));
        for (var source = 1; source >= 0; source--) for (var y = 0; y < heights[source]; y++) for (var x = 0; x < widths[source]; x++)
        for (var channel = 0; channel < (mode == 1 ? 3 : 4); channel++)
        {
            var dst = ((y + oy[source]) * 8 + x + ox[source]) * 4 + channel; var src = (y * widths[source] + x) * 4 + channel;
            output[1 - source][dst] = mode == 1 ? (byte)(output[1 - source][dst] ^ sources[source][src])
                : (byte)(output[1 - source][dst] * (1 - (mode == 3 ? 1 : .3)) + sources[source][src] * (mode == 3 ? 1 : .3));
        }
        return output;
    }
}
