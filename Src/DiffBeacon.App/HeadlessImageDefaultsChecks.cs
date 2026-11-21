using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media.Imaging;
using System.Runtime.InteropServices;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessImageDefaultsChecks
{
    private sealed class Clock : IImageDisplayClock
    {
        internal int Reads;
        public long ReadEpochMilliseconds() { Reads++; return 123456789; }
    }
    internal static void Run(string output, Action<Task> pump, Action<string, bool, string> check)
    {
        var folder = Path.Combine(output, "image-application-defaults"); Directory.CreateDirectory(folder);
        var settingsPath = Path.Combine(folder, "options.json");
        var store = new ImageApplicationOptionsStore(settingsPath);
        var defaults = new ImageApplicationOptions { ShowDifferences = false, Zoom = 2, BlockSize = 3,
            HighlightAlpha = .25, Threshold = 11.125, InsertionDeletionMode = 2 };
        if (!store.SetOptions(defaults)) throw new InvalidOperationException(store.Diagnostic);
        var window = new MainWindow(null, store) { Width = 1280, Height = 850 };
        var identities = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        var clock = Stopwatch.StartNew(); var sequence = 0; string? lastCompareError = null;
        using var stream = File.Create(Path.Combine(folder, "observations.ndjson"));
        using var log = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        var left = Path.Combine(folder, "left.png"); var right = Path.Combine(folder, "right.png");
        byte[] Pixels(byte shade) => Enumerable.Range(0, 32 * 24).SelectMany(_ => new byte[] { shade, shade, shade, 255 }).ToArray();
        pump(ImagePngStore.SaveAsync(left, new(1, 32, 24, Pixels(0)), []));
        pump(ImagePngStore.SaveAsync(right, new(1, 32, 24, Pixels(200)), []));
        try
        {
            window.Show(); Render();
            var a = window.ActivePane; Paths(a); Compare(a);
            var imageA = Image(a); State("new-defaults", a);
            var b = window.AddSession(); Paths(b); Compare(b); var imageB = Image(b);
            State("peer-before", b);
            Change(imageA, "ImageThreshold", 19.375); State("threshold-change", a); State("peer-after-threshold", b);
            Change(imageB, "ImageBlockSize", 5); State("peer-block-merge", b); State("first-after-peer", a);
            Change(imageA, "ImageZoom", 2.5); State("idle-zoom", a);
            Change(imageA, "ImageShowDifferences", 1); State("show-change", a);
            Change(imageA, "ImageHighlightAlpha", .6); State("alpha-change", a);
            Change(imageA, "ImageInsertionDeletionMode", 1); State("mode-change", a); State("peer-after-six", b);
            // 設定fileのread-only拒否後に、同じ確定値を明示再操作する。
            File.SetAttributes(settingsPath, File.GetAttributes(settingsPath) | FileAttributes.ReadOnly);
            try { Change(imageA, "ImageThreshold", 23.5); State("save-failed", a); }
            finally { File.SetAttributes(settingsPath, File.GetAttributes(settingsPath) & ~FileAttributes.ReadOnly); }
            pump(imageA.ApplySettingsAsync(imageA.CaptureSettings())); State("explicit-retry", a);
            State("cancel-before", a);
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = false;
            imageA.FrameCandidateReady = () => { entered = true; return pending.Task; };
            using (var cancel = new CancellationTokenSource())
            {
                var task = imageA.ApplySettingsAsync(imageA.CaptureSettings() with { Threshold = 27.5 }, cancel.Token);
                pump(Task.Run(async () => { while (!entered) await Task.Delay(1); }));
                State("cancel-pending", a); cancel.Cancel(); pending.TrySetResult();
                try { pump(task); } catch (OperationCanceledException) { }
            }
            imageA.FrameCandidateReady = null; State("cancel-after", a);
            entered = false; pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            imageA.FrameCandidateReady = () => { entered = true; return pending.Task; };
            var stale = imageA.ApplySettingsAsync(imageA.CaptureSettings() with { Threshold = 29.5 });
            pump(Task.Run(async () => { while (!entered) await Task.Delay(1); }));
            State("stale-pending", a);
            imageA.FrameCandidateReady = null;
            var newer = imageA.ApplySettingsAsync(imageA.CaptureSettings() with { Threshold = 31.5 });
            pump(newer); State("stale-newest-adopted", a);
            pending.TrySetResult(); try { pump(stale); } catch (OperationCanceledException) { }
            State("stale-after", a);
            // MainWindowの実候補採用前に入力を変更しCanAdopt拒否を起こす。
            a.ImageReadyForAdoption = candidate =>
            {
                State("candidate-before-reject", a);
                pump(candidate.ApplySettingsAsync(candidate.CaptureSettings() with { Threshold = 41.5 }));
                a.LeftPath.Text = Path.Combine(folder, "missing.png");
            };
            Compare(a); a.ImageReadyForAdoption = null; State("rejected-candidate", a);
            var broken = Path.Combine(folder, "broken.png"); File.WriteAllBytes(broken, [1, 2, 3]);
            a.LeftPath.Text = broken; Compare(a, expectFailure: true); State("decode-failed", a); Paths(a);
            // 実PNG保存Taskの同期開始直後は_savingが立ち、Zoomは保存対象でない。
            var guardCalls = 0;
            imageB.ConfigureEditing([left, right], [false, false], outputGuard: target =>
            {
                if (++guardCalls != 2) return;
                var saving = false; try { imageB.EnsureNotSaving(); } catch (InvalidOperationException) { saving = true; }
                State("save-inflight-before", b, saving: saving);
                Change(imageB, "ImageZoom", 3); State("save-inflight-zoom", b, saving: saving);
            });
            pump(imageB.SaveToAsync(0, Path.Combine(folder, "saved.png"))); State("save-complete", b);
            pump(imageB.ApplySettingsAsync(imageB.CaptureSettings())); State("save-zoom-explicit-retry", b);
            // 6項目だけのReloadに従来Overlay/Drag通知を出さないことをclock/revisionで採取。
            var guardPane = window.AddSession(); guardPane.ApplyProject(new() { Mode = "Text", LeftPath = settingsPath, RightPath = settingsPath, LeftReadOnly = true, RightReadOnly = true });
            Change(imageA, "ImageThreshold", 43.5); State("peer-output-guard", a);
            guardPane.ApplyProject(new() { Mode = "Text", LeftPath = left, RightPath = right });
            pump(imageA.ApplySettingsAsync(imageA.CaptureSettings())); State("peer-guard-explicit-retry", a);
            window.Width = 850; window.Height = 550; State("minimum-layout", b);
            foreach (var name in new[] { "ImageZoom", "ImageBlockSize", "ImageThreshold", "ImageHighlightAlpha", "ImageShowDifferences", "ImageInsertionDeletionMode" })
            {
                window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), b)); Render();
                var toolbar = imageB.GetVisualDescendants().OfType<ScrollViewer>().Single(c => c.Name == "ImageToolbar");
                toolbar.Offset = default; Render();
                var control = imageB.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);
                var beforeOffset = toolbar.Offset; control.BringIntoView(); Render();
                State("minimum-reach-" + name, b, reachedControl: name, scrollBefore: beforeOffset);
            }
            window.Width = 1280; window.Height = 850;
            var renderClock = new Clock(); imageB.RenderClock = renderClock; imageB.GateClock = renderClock;
            var notifications = 0; Action listener = () => notifications++; store.Changed += listener;
            var reload = store.Current with { Threshold = 37.5 };
            State("reload-before", b, notifications, renderClock.Reads);
            File.WriteAllBytes(settingsPath, JsonSerializer.SerializeToUtf8Bytes(reload, ImageApplicationOptionsJsonContext.Default.ImageApplicationOptions));
            store.Reload(); Render(); pump(imageB.CurrentDisplayOperation);
            State("reload-six-only", b, notifications, renderClock.Reads); store.Changed -= listener;
            foreach (var version in Enumerable.Range(1, 6)) foreach (var explicitSettings in new[] { false, true })
            {
                var project = new ComparisonProject { Mode = "Image", LeftPath = left, RightPath = right,
                    ImageSettings = explicitSettings ? new() { ShowDifferences = true, Zoom = 1.5, BlockSize = 7, HighlightAlpha = .9, Threshold = 9.25, InsertionDeletionMode = 1 } : new() };
                var projectFile = Path.Combine(folder, $"project-v{version}-{explicitSettings}.json");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new ComparisonWorkspace { FormatVersion = version, Entries = [project] }, ProjectJsonContext.Default.ComparisonWorkspace);
                if (!explicitSettings)
                {
                    using var doc = JsonDocument.Parse(bytes); using var target = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(target))
                    {
                        writer.WriteStartObject(); writer.WriteNumber("formatVersion", version); writer.WriteNumber("activeEntryIndex", 0); writer.WriteStartArray("entries"); writer.WriteStartObject();
                        foreach (var property in doc.RootElement.GetProperty("entries")[0].EnumerateObject()) if (property.Name != "imageSettings") property.WriteTo(writer);
                        writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
                    }
                    bytes = target.ToArray();
                }
                File.WriteAllBytes(projectFile, bytes); ComparisonWorkspace? loaded = null;
                pump(Load()); a.ApplyProject(loaded!.Entries[0]); Compare(a); State($"project-v{version}-{(explicitSettings ? "explicit" : "omitted")}", a);
                async Task Load() => loaded = await WorkspaceStore.LoadWorkspaceAsync(projectFile);
            }
            // 同入力の復元候補を中止してもCaptureProjectは採用済み表示を保存する。
            State("project-pending-before", a);
            a.ApplyProject(new() { Mode = "Image", LeftPath = left, RightPath = right,
                ImageSettings = new() { ShowDifferences = false, Zoom = 2.25, BlockSize = 11, HighlightAlpha = .6, Threshold = 4.25, InsertionDeletionMode = 2 } });
            a.ImageReadyForAdoption = _ =>
            {
                State("project-pending-candidate", a);
                a.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止") && !button.GetVisualAncestors().OfType<SpecializedViews.ImagePanel>().Any()).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            Compare(a, expectFailure: true); a.ImageReadyForAdoption = null; State("project-pending-cancelled", a);
            Compare(a); State("project-pending-retry", a);
            State("project-stale-before", a);
            a.ApplyProject(new() { Mode = "Image", LeftPath = left, RightPath = right, ImageSettings = new() { Threshold = 12.5 } });
            a.ImageReadyForAdoption = _ =>
            {
                State("project-stale-candidate", a);
                a.ApplyProject(new() { Mode = "Image", LeftPath = left, RightPath = right,
                    ImageSettings = new() { ShowDifferences = true, Zoom = 1.75, BlockSize = 9, HighlightAlpha = .8, Threshold = 6.25, InsertionDeletionMode = 1 } });
            };
            Compare(a, expectFailure: true); a.ImageReadyForAdoption = null; State("project-stale-rejected", a);
            Compare(a); State("project-stale-retry", a);
            // 新規非画像CaptureProjectへglobal値を混入させない。
            var plain = window.AddSession(); State("capture-plain-project", plain);
            StoreCase("legacy-options", "{\"dragMode\":1,\"overlayMode\":0,\"blinkDifferences\":false,\"animationPeriod\":1000,\"blinkPeriod\":800,\"overlayAlpha\":0.3}");
            StoreCase("unknown-options", "{\"unknown\":1}");
            StoreCase("oversize-options", new string(' ', 4097) + "{}");
            foreach (var item in new[] { ("zoom", "0"), ("threshold", "511"), ("blockSize", "0"), ("highlightAlpha", "2"), ("insertionDeletionMode", "3") })
                StoreCase("invalid-" + item.Item1, "{\"" + item.Item1 + "\":" + item.Item2 + "}");
            var roundtripPath = Path.Combine(folder, "roundtrip.json"); var roundtrip = new ImageApplicationOptionsStore(roundtripPath);
            roundtrip.SetOptions(defaults); roundtrip.Reload(); StoreState("roundtrip", roundtripPath, roundtrip, true);
            var attempts = new[] { defaults with { Zoom = double.NaN }, defaults with { Threshold = double.PositiveInfinity } };
            foreach (var item in attempts) { var result = roundtrip.SetOptions(item); StoreState("invalid-set-" + (double.IsNaN(item.Zoom) ? "nan" : "infinity"), roundtripPath, roundtrip, result); }
            check("image application defaults raw observations emitted", sequence > 30, "independent reader decides raw state/settings/PNG; producer emission is not qualification");
        }
        finally { window.Close(); }

        void Paths(ComparisonPane pane) { pane.LeftPath.Text = left; pane.RightPath.Text = right; }
        SpecializedViews.ImagePanel Image(ComparisonPane pane) => pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
        void Render() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        void Compare(ComparisonPane pane, bool expectFailure = false)
        {
            Task? actual = null; var old = pane.TextSaveTaskObserved;
            pane.TextSaveTaskObserved = (route, task) => { if (route == "path-compare") actual = task; old?.Invoke(route, task); };
            lastCompareError = null;
            try { pane.CompareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); if (actual is null) throw new InvalidOperationException("compare task absent"); try { pump(actual); } catch (Exception error) when (expectFailure) { lastCompareError = error.GetType().FullName + ": " + error.Message; } Render(); }
            finally { pane.TextSaveTaskObserved = old; }
        }
        void Change(SpecializedViews.ImagePanel panel, string name, double value)
        {
            var control = panel.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);
            if (control is Slider slider) slider.Value = value;
            else if (control is NumericUpDown number) number.Value = (decimal)value;
            else if (control is ComboBox combo) combo.SelectedIndex = (int)value;
            else if (control is CheckBox box) box.IsChecked = value != 0;
            if (name != "ImageZoom") pump(panel.CurrentFrameOperation);
            Render();
        }
        void State(string name, ComparisonPane pane, int notifications = 0, int clockReads = 0, bool saving = false, string? reachedControl = null, Vector scrollBefore = default)
        {
            window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), pane)); Render(); var panel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().SingleOrDefault();
            var stem = (++sequence).ToString("D3") + "-" + name;
            var snapshot = Path.Combine(folder, stem + "-settings.json"); if (File.Exists(settingsPath)) File.Copy(settingsPath, snapshot);
            var png = Path.Combine(folder, stem + ".png"); using (var bitmap = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("render absent")) bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            using var bytes = new MemoryStream(); using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartObject(); writer.WriteString("name", name); writer.WriteString("lastCompareError", lastCompareError); writer.WriteNumber("sequence", sequence); writer.WriteNumber("elapsedMs", clock.ElapsedMilliseconds);
                writer.WriteString("settingsFile", Path.GetFileName(snapshot)); writer.WriteString("settingsSHA256", Hash(File.ReadAllBytes(snapshot))); writer.WriteString("png", Path.GetFileName(png)); writer.WriteString("pngSHA256", Hash(File.ReadAllBytes(png)));
                writer.WriteString("reachedControl", reachedControl);
                writer.WriteStartArray("inputFiles"); foreach (var input in new[] { left, right })
                {
                    var inputBytes = File.ReadAllBytes(input); writer.WriteStartObject(); writer.WriteString("name", Path.GetFileName(input)); writer.WriteNumber("size", inputBytes.Length); writer.WriteString("sha256", Hash(inputBytes)); writer.WriteString("bytesFile", Blob(inputBytes)); writer.WriteEndObject();
                } writer.WriteEndArray();
                writer.WritePropertyName("store"); JsonSerializer.Serialize(writer, store.Current, ImageApplicationOptionsJsonContext.Default.ImageApplicationOptions);
                writer.WritePropertyName("project"); JsonSerializer.Serialize(writer, pane.CaptureProject(), ProjectJsonContext.Default.ComparisonProject);
                if (panel is not null)
                {
                    writer.WriteNumber("requestTaskIdentity", Identity(panel.CurrentFrameOperation));
                    writer.WriteString("requestTaskStatus", panel.CurrentFrameOperation.Status.ToString());
                    writer.WriteNumber("historyIndex", panel.HistoryIndex); writer.WriteNumber("historyCount", panel.HistoryCount); writer.WriteBoolean("dirty", panel.HasUnsavedChanges);
                    writer.WriteStartArray("modified"); for (var side = 0; side < (panel.MiddleFrameCount.HasValue ? 3 : 2); side++) writer.WriteBooleanValue(panel.PaneModified(side)); writer.WriteEndArray();
                    writer.WriteNumber("adoptedDisplayIdentity", Identity(panel.AdoptedDisplay));
                    try { WriteFrames(writer, "originalFrames", panel.CaptureEditFrames()); writer.WriteBoolean("originalCaptureDeferred", false); }
                    catch (InvalidOperationException error) { writer.WriteBoolean("originalCaptureDeferred", true); writer.WriteString("originalCaptureError", error.Message); }
                    WriteFrames(writer, "renderedFrames", panel.RenderedFrames);
                    writer.WriteStartArray("bitmaps");
                    foreach (var actualImage in panel.GetVisualDescendants().OfType<Image>().Where(i => i.Source is WriteableBitmap))
                    {
                        if (actualImage.Source is not WriteableBitmap bitmap) throw new InvalidOperationException("Actual displayed bitmap absent");
                        using var locked = bitmap.Lock(); var pixels = new byte[checked(bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4)];
                        for (var y = 0; y < bitmap.PixelSize.Height; y++) Marshal.Copy(IntPtr.Add(locked.Address, y * locked.RowBytes), pixels, y * bitmap.PixelSize.Width * 4, bitmap.PixelSize.Width * 4);
                        writer.WriteStartObject(); writer.WriteString("name", actualImage.Name ?? "Difference"); writer.WriteNumber("identity", Identity(bitmap)); writer.WriteNumber("width", bitmap.PixelSize.Width); writer.WriteNumber("height", bitmap.PixelSize.Height); writer.WriteString("pixelFormat", bitmap.Format?.ToString()); writer.WriteString("bgraFile", Blob(pixels)); writer.WriteString("bgraSHA256", Hash(pixels)); writer.WriteEndObject();
                    } writer.WriteEndArray();
                    var toolbar = panel.GetVisualDescendants().OfType<ScrollViewer>().Single(c => c.Name == "ImageToolbar");
                    var presenter = toolbar.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
                    var viewport = new Rect(presenter.TranslatePoint(default, window)!.Value, presenter.Bounds.Size);
                    var windowRect = new Rect(default(Point), window.Bounds.Size); viewport = viewport.Intersect(windowRect);
                    writer.WriteStartObject("viewport"); writer.WriteNumber("x", viewport.X); writer.WriteNumber("y", viewport.Y); writer.WriteNumber("width", viewport.Width); writer.WriteNumber("height", viewport.Height); writer.WriteEndObject();
                    writer.WriteNumber("windowWidth", window.Bounds.Width); writer.WriteNumber("windowHeight", window.Bounds.Height);
                    writer.WriteNumber("scrollBeforeY", scrollBefore.Y); writer.WriteNumber("scrollAfterY", toolbar.Offset.Y);
                    writer.WritePropertyName("display"); JsonSerializer.Serialize(writer, new ComparisonProject { ImageSettings = panel.CaptureSettings() }, ProjectJsonContext.Default.ComparisonProject);
                    writer.WriteNumber("displayCandidatesStarted", panel.DisplayCandidatesStarted); writer.WriteNumber("displayCandidatesAdopted", panel.DisplayCandidatesAdopted);
                    writer.WriteNumber("generation", panel.AdoptedDisplay?.Generation ?? -1); writer.WriteNumber("revision", panel.AdoptedDisplay?.Revision ?? -1);
                    writer.WriteStartArray("controls"); foreach (var c in panel.GetVisualDescendants().OfType<Control>().Where(c => c.Name is "ImageZoom" or "ImageBlockSize" or "ImageThreshold" or "ImageHighlightAlpha" or "ImageShowDifferences" or "ImageInsertionDeletionMode"))
                    {
                        writer.WriteStartObject(); writer.WriteString("name", c.Name); writer.WriteNumber("width", c.Bounds.Width); writer.WriteNumber("height", c.Bounds.Height); var position = c.TranslatePoint(default, window); writer.WriteNumber("x", position?.X ?? -1); writer.WriteNumber("y", position?.Y ?? -1);
                        if (c is Slider slider) writer.WriteNumber("value", slider.Value); else if (c is NumericUpDown number) writer.WriteNumber("value", (double)(number.Value ?? 0)); else if (c is CheckBox box) writer.WriteBoolean("value", box.IsChecked == true); else if (c is ComboBox combo) writer.WriteNumber("value", combo.SelectedIndex);
                        writer.WriteEndObject();
                    } writer.WriteEndArray();
                }
                writer.WriteNumber("notifications", notifications); writer.WriteNumber("clockReads", clockReads); writer.WriteBoolean("saving", saving); writer.WriteEndObject();
            }
            log.WriteLine(Encoding.UTF8.GetString(bytes.ToArray()));
        }
        void StoreCase(string name, string json)
        {
            var path = Path.Combine(folder, name + ".json"); File.WriteAllText(path, json, new UTF8Encoding(false));
            var candidate = new ImageApplicationOptionsStore(); candidate.SetOptions(defaults);
            // Reload test retains the seeded Current; use a file-backed store seeded before corrupting its input.
            var seedPath = Path.Combine(folder, name + "-seed.json"); var seeded = new ImageApplicationOptionsStore(seedPath); seeded.SetOptions(defaults); File.Copy(path, seedPath, true);
            var result = seeded.Reload(); StoreState(name, seedPath, seeded, result);
        }
        void StoreState(string name, string path, ImageApplicationOptionsStore candidate, bool result)
        {
            using var bytes = new MemoryStream(); using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartObject(); writer.WriteString("name", name); writer.WriteNumber("sequence", ++sequence); writer.WriteString("rawFile", Path.GetFileName(path)); writer.WriteString("rawSHA256", Hash(File.ReadAllBytes(path))); writer.WriteBoolean("result", result);
                writer.WritePropertyName("store"); JsonSerializer.Serialize(writer, candidate.Current, ImageApplicationOptionsJsonContext.Default.ImageApplicationOptions); writer.WriteString("diagnostic", candidate.Diagnostic); writer.WriteEndObject();
            } log.WriteLine(Encoding.UTF8.GetString(bytes.ToArray()));
        }
        int Identity(object? value)
        {
            if (value is null) return 0;
            if (!identities.TryGetValue(value, out var id)) identities.Add(value, id = identities.Count + 1);
            return id;
        }
        string Blob(byte[] bytes)
        {
            var name = "blob-" + Hash(bytes) + ".bin"; var path = Path.Combine(folder, name);
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            else if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Observation blob collision");
            return name;
        }
        void WriteFrames(Utf8JsonWriter writer, string name, IReadOnlyList<ImageComparisonEngine.DecodedFrame> frames)
        {
            writer.WriteStartArray(name); foreach (var frame in frames)
            {
                writer.WriteStartObject(); writer.WriteNumber("identity", Identity(frame)); writer.WriteNumber("width", frame.Width); writer.WriteNumber("height", frame.Height); writer.WriteString("bgraFile", Blob(frame.Pixels)); writer.WriteString("bgraSHA256", Hash(frame.Pixels)); writer.WriteEndObject();
            } writer.WriteEndArray();
        }
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    }
}
