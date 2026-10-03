using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessImageWipeChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "image-wipes"); Directory.CreateDirectory(folder);
        var incoming = pane.CaptureProject();
        var observations = new List<(string Label, string Stage, int Pane, int Width, int Height, byte[] Pixels)>();
        try
        {
            var optionsPath = Path.Combine(folder, "wipe-options.json"); var options = new ImageApplicationOptionsStore(optionsPath);
            foreach (var mode in new[] { ImageDragMode.VerticalWipe, ImageDragMode.HorizontalWipe })
                check("wipe global mode persistent " + mode, options.SetMode(mode) && new ImageApplicationOptionsStore(optionsPath).Mode == mode, "fixture-only app options path");
            foreach (var wipe in new[] { new ImageWipeSnapshot((ImageDragMode)6, 0), new ImageWipeSnapshot(ImageDragMode.VerticalWipe, -1) })
            { var rejected = false; try { wipe.Validate(); } catch (ArgumentException) { rejected = true; } check("wipe invalid snapshot " + wipe, rejected, "reject before rendering"); }
            for (var count = 2; count <= 3; count++)
            foreach (var mode in new[] { ImageDragMode.VerticalWipe, ImageDragMode.HorizontalWipe })
            {
                var label = count + "-" + mode;
                var raw = Enumerable.Range(0, count).Select(i => new ImageComparisonEngine.DecodedFrame(1, 32, 24,
                    Enumerable.Range(0, 32 * 24 * 4).Select(n => (byte)(n * 37 + 17 + i * 43)).ToArray())).ToArray();
                var paths = raw.Select((frame, i) => Path.Combine(folder, label + "-input" + i + ".png")).ToArray();
                for (var i = 0; i < count; i++) pump(ImagePngStore.SaveAsync(paths[i], raw[i], []));
                pane.DiscardChanges(); pane.ApplyProject(new() { Mode = "Image", LeftPath = paths[0], BasePath = count == 3 ? paths[1] : "", RightPath = paths[^1],
                    LeftReadOnly = true, ImageSettings = new() { Zoom = 2, ShowDifferences = false, BlockSize = 1, ReportAllFrames = false } });
                pump(pane.ComparePathsAsync()); Render();
                var panel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single(); panel.SetDragMode(mode);
                var settings = panel.CaptureSettings(); var surface = Surface(panel, 0);
                var origin = surface.TranslatePoint(new(12, 12), window)!.Value;
                window.MouseDown(origin, MouseButton.Left); pump(panel.CurrentWipeOperation); Render();
                check(label + " readonly press", panel.HasDisplayPointerCapture && panel.ActiveWipe == new ImageWipeSnapshot(mode, 6), "livezoom=2 canvas coordinate; no inverse raw transform");
                CheckPixels("press", 6);
                window.MouseDown(origin, MouseButton.Right, RawInputModifiers.LeftMouseButton);
                window.MouseUp(origin, MouseButton.Right, RawInputModifiers.LeftMouseButton);
                check(label + " right button retains left gesture", panel.HasDisplayPointerCapture && panel.ActiveWipe?.Position == 6, "ignore unrelated button release");
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Render();
                check(label + " Escape keeps wipe capture clears guide", panel.HasDisplayPointerCapture && panel.ActiveWipe is not null
                    && !panel.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ImageWipeGuide0").IsVisible, "guide only canceled");
                panel.SetDragMode(ImageDragMode.None);
                panel.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "ImageZoom").Value = 3;
                window.MouseMove(surface.TranslatePoint(new(21, 21), window)!.Value, RawInputModifiers.LeftMouseButton); pump(panel.CurrentWipeOperation); Render();
                check(label + " zoom and global mode preserve press", panel.ActiveWipe == new ImageWipeSnapshot(mode, 7) && panel.HasDisplayPointerCapture, "press mode fixed/livezoom3");
                CheckPixels("move", 7); screenshot("image-wipe-" + label + ".png");
                // 押下中の直接保存は内部snapshot経路。通常GUIはrelease後にbaselineを保存する。
                var captured = panel.CaptureReport();
                var html = Path.Combine(folder, label + "-gui.html"); pump(pane.SaveReportAsync(html));
                VerifyHtml(html, 7);
                var package = Path.Combine(folder, label + ".zip"); pump(window.PackageWorkspaceAsync(package,
                    [Array.IndexOf(window.SessionPanes.ToArray(), pane)], new(IncludeReport: true)));
                using (var archive = System.IO.Compression.ZipFile.OpenRead(package))
                {
                    var report = archive.Entries.First(entry => entry.FullName.EndsWith(".html", StringComparison.Ordinal));
                    using var reader = new StreamReader(report.Open()); var packagedHtml = Path.Combine(folder, label + "-packaged.html"); File.WriteAllText(packagedHtml, reader.ReadToEnd()); VerifyHtml(packagedHtml, 7);
                    var json = archive.Entries.First(entry => entry.FullName.EndsWith(".json", StringComparison.Ordinal));
                    using var projectReader = new StreamReader(json.Open()); check(label + " packaging gesture ephemeral", !Regex.IsMatch(projectReader.ReadToEnd(), "\"(?:wipe|wipeMode|wipePosition)\"\\s*:", RegexOptions.IgnoreCase), "projectJSON unchanged contract");
                }
                window.MouseUp(surface.TranslatePoint(new(21, 21), window)!.Value, MouseButton.Left); pump(panel.CurrentWipeOperation); Render();
                check(label + " release baseline", panel.ActiveWipe is null && !panel.HasDisplayPointerCapture, "capture/guide/pixels released");
                check(label + " release adopted display matches bitmap", panel.AdoptedDisplay is { } releasedDisplay
                    && releasedDisplay.Sample.Wipe is null && releasedDisplay.Settings.Wipe is null
                    && releasedDisplay.Frames.Select((frame, i) => frame.Pixels.AsSpan().SequenceEqual(panel.RenderedFrames[i].Pixels)).All(x => x), "typed snapshot cannot retain old wipe");
                for (var i = 0; i < count; i++) check(label + " release pixels " + i, panel.RenderedFrames[i].Pixels.AsSpan().SequenceEqual(raw[i].Pixels), "full BGRA baseline");
                var releasedHtml = Path.Combine(folder, label + "-released-gui.html"); pump(pane.SaveReportAsync(releasedHtml)); VerifyHtml(releasedHtml, null);
                var releasedPackage = Path.Combine(folder, label + "-released.zip"); pump(window.PackageWorkspaceAsync(releasedPackage,
                    [Array.IndexOf(window.SessionPanes.ToArray(), pane)], new(IncludeReport: true)));
                using (var archive = System.IO.Compression.ZipFile.OpenRead(releasedPackage))
                {
                    using var reader = new StreamReader(archive.Entries.First(entry => entry.FullName.EndsWith(".html", StringComparison.Ordinal)).Open());
                    var releasedPackageHtml = Path.Combine(folder, label + "-released-packaged.html"); File.WriteAllText(releasedPackageHtml, reader.ReadToEnd()); VerifyHtml(releasedPackageHtml, null);
                }
                var fixedHtml = Path.Combine(folder, label + "-captured-after-release.html"); File.WriteAllText(fixedHtml, ImageReport.Create(captured, paths)); VerifyHtml(fixedHtml, 7);
                check(label + " raw history settings unchanged", !panel.HasUnsavedChanges && panel.HistoryCount == 0 && panel.CaptureSettings().LeftFrame == settings.LeftFrame
                    && panel.CaptureEditFrames().Select((frame, i) => frame.Pixels.AsSpan().SequenceEqual(raw[i].Pixels)).All(x => x), "display only");
                var rawSave = Path.Combine(folder, label + "-raw-save.png"); pump(panel.SaveToAsync(count - 1, rawSave));
                check(label + " independent raw PNG", HeadlessImageCopyChecks.ReadPng(rawSave).Pixels.AsSpan().SequenceEqual(raw[^1].Pixels), rawSave);

                foreach (var initialPending in new[] { false, true })
                {
                    panel.SetDragMode(mode);
                    var crossEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var crossRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!initialPending) { window.MouseDown(surface.TranslatePoint(new(6, 6), window)!.Value, MouseButton.Left); pump(panel.CurrentWipeOperation); }
                    panel.WipeCandidateReady = () => { crossEntered.TrySetResult(); return crossRelease.Task; };
                    if (initialPending) window.MouseDown(surface.TranslatePoint(new(24, 24), window)!.Value, MouseButton.Left);
                    else window.MouseMove(surface.TranslatePoint(new(24, 24), window)!.Value, RawInputModifiers.LeftMouseButton);
                    var crossWipe = panel.CurrentWipeOperation; pump(crossEntered.Task);
                    pump(panel.SetFramesAsync(1, 1)); panel.WipeCandidateReady = null; crossRelease.TrySetResult(); pump(crossWipe); pump(panel.CurrentWipeOperation); Render();
                    var crossStage = initialPending ? "cross-initial-pending" : "cross-adopted-pending";
                    using (var crossFile = File.Create(Path.Combine(folder, label + "-" + crossStage + ".json")))
                    using (var crossWriter = new Utf8JsonWriter(crossFile))
                    {
                        crossWriter.WriteStartObject(); if (panel.ActiveWipe is { } active) crossWriter.WriteNumber("activePosition", active.Position); else crossWriter.WriteNull("activePosition");
                        crossWriter.WriteNumber("expectedPosition", 8); crossWriter.WriteBoolean("capture", panel.HasDisplayPointerCapture); crossWriter.WriteStartArray("frames");
                        for (var i = 0; i < count; i++)
                        {
                            var frame = panel.RenderedFrames[i]; crossWriter.WriteStartObject(); crossWriter.WriteNumber("pane", i); crossWriter.WriteNumber("width", frame.Width); crossWriter.WriteNumber("height", frame.Height);
                            crossWriter.WriteBase64String("actualBgra", frame.Pixels); crossWriter.WriteBase64String("expectedBgra", Expected(raw, i, mode, 8)); crossWriter.WriteEndObject();
                        }
                        crossWriter.WriteEndArray(); crossWriter.WriteEndObject();
                    }
                    check(label + " " + crossStage + " latest position", panel.ActiveWipe?.Position == 8 && panel.HasDisplayPointerCapture, "actual pointer request8 survives new baseline; adopted2 must not overwrite requested8");
                    CheckPixels(crossStage, 8);
                    for (var i = 0; i < count; i++) pump(ImagePngStore.SaveAsync(Path.Combine(folder, label + "-" + crossStage + "-" + i + ".png"), panel.RenderedFrames[i], []));
                    window.MouseUp(surface.TranslatePoint(new(24, 24), window)!.Value, MouseButton.Left); pump(panel.CurrentWipeOperation);
                }

                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                panel.WipeCandidateReady = () => { entered.TrySetResult(); return release.Task; };
                var pending = panel.SetWipeAsync(new(mode, 2)); pump(entered.Task);
                panel.SetWipeAsync(new(mode, 8)); panel.SetWipeAsync(new(mode, 4)); panel.WipeCandidateReady = null; release.TrySetResult(); pump(pending); pump(panel.CurrentWipeOperation); CheckPixels("latest", 4);
                panel.SetDragMode(mode); window.MouseDown(surface.TranslatePoint(new(18, 18), window)!.Value, MouseButton.Left); pump(panel.CurrentWipeOperation);
                IPointer? pointer = null; surface.AddHandler(InputElement.PointerMovedEvent, Capture, RoutingStrategies.Tunnel, handledEventsToo: true);
                window.MouseMove(surface.TranslatePoint(new(21, 21), window)!.Value, RawInputModifiers.LeftMouseButton);
                surface.RemoveHandler(InputElement.PointerMovedEvent, Capture); pointer?.Capture(null); pump(panel.CurrentWipeOperation);
                check(label + " capture loss baseline", panel.ActiveWipe is null && !panel.HasDisplayPointerCapture, "pending result must not adopt");
                window.MouseUp(origin, MouseButton.Left);
                var scroll = panel.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "ImagePaneScroll0");
                var blank = scroll.TranslatePoint(new(130, 130), window)!.Value;
                window.MouseDown(blank, MouseButton.Left); pump(panel.CurrentWipeOperation);
                check(label + " blank viewport clamps wipe", panel.HasDisplayPointerCapture && panel.ActiveWipe?.Position == (mode == ImageDragMode.VerticalWipe ? 24 : 32), "GetPosition(grid)/livezoom then clamp");
                window.MouseUp(blank, MouseButton.Left);
                void Capture(object? sender, PointerEventArgs args) => pointer = args.Pointer;
                void CheckPixels(string stage, int position)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var expected = Expected(raw, i, mode, position); var actual = panel.RenderedFrames[i];
                        check(label + " " + stage + " pane " + i, actual.Pixels.AsSpan().SequenceEqual(expected), "independent closed-form mask, all BGRA");
                        var sideName = count == 3 ? new[] { "Left", "Middle", "Right" }[i] : new[] { "Left", "Right" }[i];
                        var actualBitmap = (Avalonia.Media.Imaging.WriteableBitmap)panel.GetVisualDescendants().OfType<Image>().Single(image => image.Name == "ImagePane" + sideName).Source!;
                        using var bitmapBuffer = actualBitmap.Lock(); var actualBytes = new byte[expected.Length];
                        for (var y = 0; y < actual.Height; y++) System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(bitmapBuffer.Address, y * bitmapBuffer.RowBytes), actualBytes, y * actual.Width * 4, actual.Width * 4);
                        check(label + " " + stage + " actual Bitmap " + i, actualBytes.AsSpan().SequenceEqual(expected), "all BGRA includes alpha0 hiddenRGB; guide separate layer");
                        observations.Add((label, stage, i, actual.Width, actual.Height, actual.Pixels.ToArray()));
                    }
                }
                void VerifyHtml(string path, int? position)
                {
                    var text = File.ReadAllText(path); var sides = count == 3 ? new[] { "left", "middle", "right" } : ["left", "right"];
                    for (var i = 0; i < count; i++)
                    foreach (var original in new[] { false, true })
                    {
                        var side = sides[i] + (original ? "-original" : "");
                        var tag = Regex.Matches(text, "<img[^>]+>").Select(m => m.Value).Single(t => t.Contains("data-side=\"" + side + "\""));
                        var png = Convert.FromBase64String(Regex.Match(tag, "src=\"data:image/png;base64,([^\"]+)").Groups[1].Value);
                        var pngPath = path + "." + side + ".png"; File.WriteAllBytes(pngPath, png);
                        check(label + " HTML " + Path.GetFileName(path) + " " + side, HeadlessImageCopyChecks.ReadPng(pngPath).Pixels.AsSpan().SequenceEqual(original || position is null ? raw[i].Pixels : Expected(raw, i, mode, position.Value)), "independent PNG unfilter/readback; released report has baseline");
                    }
                }
            }
            var selectedPanel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
            selectedPanel.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "ImageShowDifferences").IsChecked = true; pump(selectedPanel.CurrentFrameOperation);
            pump(selectedPanel.NavigateRegionAsync(1)); Render(); selectedPanel.SetDragMode(ImageDragMode.VerticalWipe);
            var selectedSurface = Surface(selectedPanel, 0);
            window.MouseDown(selectedSurface.TranslatePoint(new(6, 6), window)!.Value, MouseButton.Left); pump(selectedPanel.CurrentWipeOperation);
            var selectedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var selectedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var latestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var latestRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var selectedCandidate = 0;
            selectedPanel.WipeCandidateReady = () =>
            {
                if (++selectedCandidate == 1) { selectedEntered.TrySetResult(); return selectedRelease.Task; }
                if (selectedCandidate == 2) { latestEntered.TrySetResult(); return latestRelease.Task; }
                return Task.CompletedTask;
            };
            window.MouseMove(selectedSurface.TranslatePoint(new(24, 24), window)!.Value, RawInputModifiers.LeftMouseButton); var selectedPending = selectedPanel.CurrentWipeOperation; pump(selectedEntered.Task);
            pump(selectedPanel.NavigateRegionAsync(1)); selectedRelease.TrySetResult(); pump(selectedPending); pump(latestEntered.Task); Render();
            WriteSelectedTrace("before-latest-adoption");
            check("wipe selected redraw replaces completed request", selectedPending.IsCompletedSuccessfully && !ReferenceEquals(selectedPending, selectedPanel.CurrentWipeOperation) && !selectedPanel.CurrentWipeOperation.IsCompleted, "completed request belongs to previous display generation; latest candidate is held");
            check("wipe selected redraw retains adopted display while latest candidate waits", selectedPanel.ActiveWipe?.Position == 2 && selectedPanel.SelectedDiffIndex >= 0 && selectedPanel.HasDisplayPointerCapture, "active=" + selectedPanel.ActiveWipe?.Position + "; selection=" + selectedPanel.SelectedDiffIndex + "; capture=" + selectedPanel.HasDisplayPointerCapture);
            selectedPanel.WipeCandidateReady = null; latestRelease.TrySetResult(); pump(selectedPanel.CurrentWipeOperation); Render();
            WriteSelectedTrace("after-latest-adoption");
            check("wipe selected redraw preserves latest position", selectedPanel.ActiveWipe?.Position == 8 && selectedPanel.SelectedDiffIndex >= 0 && selectedPanel.HasDisplayPointerCapture, "active=" + selectedPanel.ActiveWipe?.Position + "; selection=" + selectedPanel.SelectedDiffIndex + "; capture=" + selectedPanel.HasDisplayPointerCapture + "; oldTask=" + selectedPending.Status + "; currentTask=" + selectedPanel.CurrentWipeOperation.Status);
            void WriteSelectedTrace(string stage)
            {
                using var trace = new Utf8JsonWriter(File.Create(Path.Combine(folder, "selected-request-" + stage + ".json")), new() { Indented = true });
                trace.WriteStartObject(); trace.WriteString("stage", stage);
                trace.WriteNumber("activePosition", selectedPanel.ActiveWipe?.Position ?? -1);
                trace.WriteNumber("selectedDiffIndex", selectedPanel.SelectedDiffIndex);
                trace.WriteBoolean("pointerCapture", selectedPanel.HasDisplayPointerCapture);
                trace.WriteString("oldTask", selectedPending.Status.ToString());
                trace.WriteString("currentTask", selectedPanel.CurrentWipeOperation.Status.ToString());
                trace.WriteBoolean("sameTask", ReferenceEquals(selectedPending, selectedPanel.CurrentWipeOperation));
                trace.WriteString("displayWorker", selectedPanel.DisplayWorkerOperation.Status.ToString());
                trace.WriteEndObject();
            }
            var selectedExpected = Enumerable.Range(0, 3).Select(paneIndex =>
            {
                var pixels = Enumerable.Range(0, 32 * 24 * 4).Select(n => (byte)(n * 37 + 17 + paneIndex * 43)).ToArray();
                for (var index = 0; index < pixels.Length; index += 4)
                {
                    if (pixels[index + 3] == 0) { pixels[index] = 64; pixels[index + 1] = 64; pixels[index + 2] = 255; pixels[index + 3] = (byte)(255 * .7); }
                    else { pixels[index] = (byte)(pixels[index] * (1 - .7) + 64 * .7); pixels[index + 1] = (byte)(pixels[index + 1] * (1 - .7) + 64 * .7); pixels[index + 2] = (byte)(pixels[index + 2] * (1 - .7) + 255 * .7); }
                }
                return new ImageComparisonEngine.DecodedFrame(1, 32, 24, pixels);
            }).ToArray();
            for (var paneIndex = 0; paneIndex < 3; paneIndex++)
            {
                check("wipe selected redraw latest BGRA " + paneIndex, selectedPanel.RenderedFrames[paneIndex].Pixels.AsSpan().SequenceEqual(Expected(selectedExpected, paneIndex, ImageDragMode.VerticalWipe, 8)), "independent selected-color blend then full4byte cyclic copy");
                pump(ImagePngStore.SaveAsync(Path.Combine(folder, "selected-cross-" + paneIndex + ".png"), selectedPanel.RenderedFrames[paneIndex], []));
            }
            window.MouseUp(selectedSurface.TranslatePoint(new(24, 24), window)!.Value, MouseButton.Left); pump(selectedPanel.CurrentWipeOperation);
            pump(selectedPanel.SetWipeAsync(new(ImageDragMode.VerticalWipe, 7)));
            var selectedHtmlPath = Path.Combine(folder, "selected-wipe-gui.html"); pump(pane.SaveReportAsync(selectedHtmlPath));
            var selectedZip = Path.Combine(folder, "selected-wipe.zip"); pump(window.PackageWorkspaceAsync(selectedZip,
                [Array.IndexOf(window.SessionPanes.ToArray(), pane)], new(IncludeReport: true)));
            string selectedPackagedHtml;
            using (var archive = System.IO.Compression.ZipFile.OpenRead(selectedZip))
            { using var reader = new StreamReader(archive.Entries.First(entry => entry.FullName.EndsWith(".html", StringComparison.Ordinal)).Open()); selectedPackagedHtml = reader.ReadToEnd(); }
            var selectedPackagePath = Path.Combine(folder, "selected-wipe-packaged.html"); File.WriteAllText(selectedPackagePath, selectedPackagedHtml);
            foreach (var side in new[] { "left", "middle", "right" })
            {
                byte[] Pixels(string html, string suffix)
                {
                    var tag = Regex.Matches(html, "<img[^>]+>").Select(match => match.Value).Single(tag => tag.Contains("data-side=\"" + side + "\""));
                    var data = Convert.FromBase64String(Regex.Match(tag, "src=\"data:image/png;base64,([^\"]+)").Groups[1].Value);
                    var path = Path.Combine(folder, "selected-" + side + "-" + suffix + ".png"); File.WriteAllBytes(path, data); return HeadlessImageCopyChecks.ReadPng(path).Pixels;
                }
                var singlePixels = Pixels(File.ReadAllText(selectedHtmlPath), "gui"); var packagePixels = Pixels(selectedPackagedHtml, "package");
                check("selected highlight wipe GUI and package " + side, selectedPanel.SelectedDiffIndex >= 0 && singlePixels.AsSpan().SequenceEqual(packagePixels), "independent PNG all BGRA; selected red before cyclic copy");
            }
            var tiffPaths = new[] { "two-pages-le.tif", "same-first-right.tif" }.Select(name =>
            {
                var path = Path.Combine(folder, name);
                using var resource = typeof(HeadlessImageWipeChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.Tiff." + name)!;
                using var file = File.Create(path); resource.CopyTo(file); return path;
            }).ToArray();
            // 自作fixtureの私有コピーで後続白画素を赤へ変更し、位置1/2の違いを観測可能にする。
            var tiffBytes = File.ReadAllBytes(tiffPaths[1]);
            var firstIfd = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(tiffBytes.AsSpan(4));
            var firstCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tiffBytes.AsSpan(firstIfd));
            var secondIfd = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(tiffBytes.AsSpan(firstIfd + 2 + firstCount * 12));
            var secondCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tiffBytes.AsSpan(secondIfd));
            for (var tag = 0; tag < secondCount; tag++)
            {
                var entry = secondIfd + 2 + tag * 12;
                if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tiffBytes.AsSpan(entry)) != 273) continue;
                var strip = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(tiffBytes.AsSpan(entry + 8));
                tiffBytes[strip + 3] = 255; tiffBytes[strip + 4] = 0; tiffBytes[strip + 5] = 0; break;
            }
            File.WriteAllBytes(tiffPaths[1], tiffBytes);
            pane.DiscardChanges(); pane.ApplyProject(new() { Mode = "Image", LeftPath = tiffPaths[0], RightPath = tiffPaths[1], LeftReadOnly = true,
                ImageSettings = new() { Zoom = 8, ShowDifferences = false, ReportAllFrames = true } }); pump(pane.ComparePathsAsync()); Render();
            var multi = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single(); multi.SetDragMode(ImageDragMode.VerticalWipe);
            pump(multi.SetFramesAsync(2, 2)); Render();
            var multiSurface = Surface(multi, 0); var multiOrigin = multiSurface.TranslatePoint(new(4, 16), window)!.Value;
            window.MouseDown(multiOrigin, MouseButton.Left); pump(multi.CurrentWipeOperation);
            check("wipe TIFF actual readonly press", multi.HasDisplayPointerCapture && multi.ActiveWipe?.Position == 2, "multiple pages use display gesture gate");
            var multiEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var multiRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            multi.WipeCandidateReady = () => { multiEntered.TrySetResult(); return multiRelease.Task; };
            window.MouseMove(multiSurface.TranslatePoint(new(4, 0), window)!.Value, RawInputModifiers.LeftMouseButton); var multiPending = multi.CurrentWipeOperation; pump(multiEntered.Task);
            pump(multi.SetFramesAsync(1, 1)); multi.WipeCandidateReady = null; multiRelease.TrySetResult(); pump(multiPending); pump(multi.CurrentWipeOperation);
            check("wipe TIFF pending request survives new small page", multi.ActiveWipe?.Position == 0 && multi.HasDisplayPointerCapture, "requested0 retained; old active2 must not replace it with clamped1");
            pump(multi.SetFramesAsync(2, 2)); check("wipe TIFF latest position inherited after growth", multi.ActiveWipe?.Position == 0, "completed worker position0 preserved on non-loop redraw");
            ImageComparisonEngine.DecodedFrame[] crossTiffExpected = [new(2, 1, 3, [0,255,0,255, 255,255,255,255, 255,0,0,255]), new(2, 1, 3, [0,0,255,255, 0,0,255,255, 255,0,0,255])];
            for (var i = 0; i < 2; i++)
            {
                check("wipe TIFF crossed page latest BGRA " + i, multi.RenderedFrames[i].Pixels.AsSpan().SequenceEqual(Expected(crossTiffExpected, i, ImageDragMode.VerticalWipe, 0)), "literal TIFF BGRA cyclic copy after small/grown page");
                pump(ImagePngStore.SaveAsync(Path.Combine(folder, "tiff-cross-" + i + ".png"), multi.RenderedFrames[i], []));
            }
            window.MouseMove(multiSurface.TranslatePoint(new(4, 16), window)!.Value, RawInputModifiers.LeftMouseButton); pump(multi.CurrentWipeOperation);
            var multiReport = multi.CaptureReport(); var allHtml = Path.Combine(folder, "multipage-all.html"); File.WriteAllText(allHtml, ImageReport.Create(multiReport, tiffPaths));
            var allTags = Regex.Matches(File.ReadAllText(allHtml), "<img[^>]+>").Select(match => match.Value).Where(tag => tag.Contains("data-side=\"left\"")).ToArray();
            // 自作TIFFのliteral全BGRA。アプリの復号結果から期待値を作らない。
            ImageComparisonEngine.DecodedFrame[] decodedExpected =
            [new(2, 1, 3, [0,255,0,255, 255,255,255,255, 255,0,0,255]),
             new(2, 1, 3, [0,0,255,255, 0,0,255,255, 255,0,0,255])];
            var expectedLater = Expected(decodedExpected, 0, ImageDragMode.VerticalWipe, 1);
            var laterPng = Convert.FromBase64String(Regex.Match(allTags[1], "src=\"data:image/png;base64,([^\"]+)").Groups[1].Value);
            var laterPath = allHtml + ".page2.png"; File.WriteAllBytes(laterPath, laterPng);
            check("wipe report allpage inherits small-page clamp", HeadlessImageCopyChecks.ReadPng(laterPath).Pixels.AsSpan().SequenceEqual(expectedLater), "page1 height1 clamps position2 to1; page2 height3 keeps1");
            var fixedPixels = multi.RenderedFrames.Select(frame => frame.Pixels.ToArray()).ToArray();
            multi.FrameCandidateReady = () => throw new InvalidOperationException("wipe candidate injected failure");
            try { pump(multi.SetFramesAsync(1, 1)); } catch (InvalidOperationException) { }
            check("wipe failed page keeps adopted pixels", multi.ActiveWipe?.Position == 2 && multi.RenderedFrames.Select((frame, i) => frame.Pixels.AsSpan().SequenceEqual(fixedPixels[i])).All(x => x), "candidate failure before adoption");
            multi.FrameCandidateReady = null;
            using (var cancelled = new CancellationTokenSource())
            {
                var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var cancelRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                multi.FrameCandidateReady = () => { cancelEntered.TrySetResult(); return cancelRelease.Task; };
                var cancelPage = multi.SetFramesAsync(1, 1, cancelled.Token); pump(cancelEntered.Task); cancelled.Cancel(); cancelRelease.TrySetResult();
                try { pump(cancelPage); } catch (OperationCanceledException) { } multi.FrameCandidateReady = null;
            }
            window.MouseMove(multiSurface.TranslatePoint(new(4, 16), window)!.Value, RawInputModifiers.LeftMouseButton); pump(multi.CurrentWipeOperation);
            check("wipe pending cancelled page keeps capture and next move", multi.ActiveWipe?.Position == 2 && multi.LeftFrame == 2 && multi.HasDisplayPointerCapture, "generation advanced then finally restores display drag ownership");
            var candidateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var candidateRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            multi.FrameCandidateReady = () => { candidateEntered.TrySetResult(); return candidateRelease.Task; };
            var oldPage = multi.SetFramesAsync(1, 1); pump(candidateEntered.Task); multi.FrameCandidateReady = null;
            pump(multi.SetFramesAsync(2, 2)); candidateRelease.TrySetResult(); try { pump(oldPage); } catch (OperationCanceledException) { }
            check("wipe stale page cannot replace adopted pixels", multi.LeftFrame == 2 && multi.ActiveWipe?.Position == 2 && multi.RenderedFrames.Select((frame, i) => frame.Pixels.AsSpan().SequenceEqual(fixedPixels[i])).All(x => x), "generation-bound candidate");
            pump(multi.SetFramesAsync(1, 1));
            check("wipe multipage success clamps active position", multi.ActiveWipe?.Position == 1, "height1 canvas");
            pump(multi.SetFramesAsync(2, 2)); check("wipe multipage grows without restoring prior position", multi.ActiveWipe?.Position == 1, "old position remains1 after new height3 canvas");
            screenshot("image-wipe-multipage.png");
            window.MouseMove(multiSurface.TranslatePoint(new(4, 16), window)!.Value, RawInputModifiers.LeftMouseButton); pump(multi.CurrentWipeOperation);
            check("wipe TIFF move after page adoption", multi.ActiveWipe?.Position == 2 && multi.HasDisplayPointerCapture, "live canvas coordinate/generation");
            window.MouseUp(multiSurface.TranslatePoint(new(4, 16), window)!.Value, MouseButton.Left); pump(multi.CurrentWipeOperation);
            check("wipe TIFF final release baseline", multi.ActiveWipe is null && !multi.HasDisplayPointerCapture, "page changes retained capture until left release");
            multi.Dispose(); check("wipe dispose clears snapshot", multi.ActiveWipe is null && !multi.HasDisplayPointerCapture, "resources released");
        }
        finally
        {
            using (var file = File.Create(Path.Combine(folder, "observations.json")))
            using (var writer = new Utf8JsonWriter(file))
            {
                writer.WriteStartArray();
                foreach (var item in observations) { writer.WriteStartObject(); writer.WriteString("label", item.Label); writer.WriteString("stage", item.Stage); writer.WriteNumber("pane", item.Pane); writer.WriteNumber("width", item.Width); writer.WriteNumber("height", item.Height); writer.WriteBase64String("bgraBase64", item.Pixels); writer.WriteEndObject(); }
                writer.WriteEndArray();
            }
            pane.DiscardChanges(); pane.ApplyProject(incoming); pump(pane.ComparePathsAsync()); window.ImageOptions.SetMode(ImageDragMode.Move);
        }
        void Render() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    }
    private static byte[] Expected(ImageComparisonEngine.DecodedFrame[] baseline, int pane, ImageDragMode mode, int position)
    {
        var frame = baseline[pane]; var output = new byte[frame.Pixels.Length];
        for (var y = 0; y < frame.Height; y++) for (var x = 0; x < frame.Width; x++)
        { var source = mode == ImageDragMode.VerticalWipe ? y >= position : x >= position; var offset = (y * frame.Width + x) * 4; baseline[source ? (pane + 1) % baseline.Length : pane].Pixels.AsSpan(offset, 4).CopyTo(output.AsSpan(offset)); }
        return output;
    }
    private static Grid Surface(SpecializedViews.ImagePanel panel, int pane) => panel.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ImageRectangleSurface" + pane);
}
