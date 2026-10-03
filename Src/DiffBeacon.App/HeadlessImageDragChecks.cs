using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessImageDragChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "image-drag-modes"); Directory.CreateDirectory(folder);
        var incomingProject = pane.CaptureProject();
        File.WriteAllText(Path.Combine(folder, "failure-contract.txt"),
            "NONE/MOVE/OFFSET/RECTANGLE、press固定、zoom2 delta+3/-5/+3、負preview、優先編集、readonly/複数page、Escape/captureloss/zoom/page/save/dispose/古いrelease、原画と履歴保持、全pane MOVE/wheel/key同期、共有保存/reload/不正設定/通知失敗。wipe/Overlay/PixelDifferenceドラッグは未移植。\n");
        using var stream = File.Create(Path.Combine(folder, "observations.ndjson")); using var log = new StreamWriter(stream);
        var raw = Frame(32, 24); var hashes = new Dictionary<string, string>();
        var sharedMenuResults = new List<(string Name, bool Passed, string Detail)>();
        SharedMenuBoundaryChecks();
        for (var count = 2; count <= 3; count++)
        {
            var panel = Open("offset-" + count, Enumerable.Repeat(raw, count).ToArray()); panel.SetDragMode(ImageDragMode.AdjustOffset);
            var before = panel.CaptureSettings(); var origin = At(panel, 0, 16, 16); window.MouseDown(origin, MouseButton.Left);
            window.MouseDown(origin + new Vector(1, 0), MouseButton.Right, RawInputModifiers.LeftMouseButton);
            window.MouseUp(origin + new Vector(1, 0), MouseButton.Right, RawInputModifiers.LeftMouseButton);
            check("image drag unrelated button retains left gesture " + count, panel.HasDisplayPointerCapture && panel.CaptureSettings() == before,
                "right press/release neither resets press origin nor commits left offset drag");
            foreach (var dx in new[] { 3, -5, 3 })
            {
                window.MouseMove(origin + new Vector(dx, -5), RawInputModifiers.LeftMouseButton); Render();
                var expected = new ImageRectangle((int)Math.Truncate(dx / 2d), -2, raw.Width + (int)Math.Truncate(dx / 2d), raw.Height - 2);
                check("image drag offset preview " + count + ":" + dx, panel.OffsetPreview == expected && panel.CaptureSettings() == before
                    && panel.HistoryCount == 0 && !panel.HasUnsavedChanges && RawSame(panel, raw), "press delta / zoom trunc; full raw unchanged");
                Observe("preview-" + count + "-" + dx, panel);
            }
            screenshot("image-drag-negative-preview-" + count + ".png");
            window.MouseUp(origin + new Vector(3, -5), MouseButton.Left); pump(panel.CurrentDragOperation); Render();
            var after = panel.CaptureSettings();
            check("image drag offset normalized release " + count, after.LeftOffset == new ImageOffset(1, 0) && after.RightOffset == new ImageOffset(0, 2)
                && (count == 2 || after.MiddleOffset == new ImageOffset(0, 2)) && panel.OffsetPreview is null && !panel.HasDisplayPointerCapture
                && panel.HistoryCount == 0 && RawSame(panel, raw), "one release commit across all panes");
            Observe("release-" + count, panel);

            panel = Open("escape-" + count, Enumerable.Repeat(raw, count).ToArray()); panel.SetDragMode(ImageDragMode.AdjustOffset);
            origin = At(panel, 0, 16, 16); window.MouseDown(origin, MouseButton.Left); window.MouseMove(origin + new Vector(-5, 3), RawInputModifiers.LeftMouseButton);
            KeyStroke(Key.Escape, PhysicalKey.Escape); Render();
            check("image drag Escape preserves offset capture " + count, panel.HasDisplayPointerCapture && panel.CaptureSettings().LeftOffset == default, "original Cancel clears only rectangle/floating");
            panel.SetDragMode(ImageDragMode.None); window.MouseUp(origin + new Vector(-5, 3), MouseButton.Left); pump(panel.CurrentDragOperation);
            check("image drag press mode fixed " + count, panel.CaptureSettings().LeftOffset == new ImageOffset(0, 1) && panel.CaptureSettings().RightOffset == new ImageOffset(2, 0), "mode changed after press; release keeps OFFSET");
            Observe("escape-mode-fixed-" + count, panel);
        }

        foreach (var reason in new[] { "zoom", "page-generation", "capture-loss", "save", "dispose" })
        {
            var panel = Open("cancel-" + reason, [raw, raw]); panel.SetDragMode(ImageDragMode.AdjustOffset);
            var origin = At(panel, 0, 16, 16); window.MouseDown(origin, MouseButton.Left); window.MouseMove(origin + new Vector(-5, 3), RawInputModifiers.LeftMouseButton); Render();
            if (reason == "zoom") panel.GetVisualDescendants().OfType<Slider>().Single(x => x.Name == "ImageZoom").Value = 3;
            else if (reason == "page-generation") pump(panel.SetFramesAsync(1, 1));
            else if (reason == "capture-loss")
            {
                // 次の実MouseDownでcaptureを奪うのではなく、取得した実pointerへcapture解除を要求する。
                IPointer? actual = null;
                Surface(panel, 0).AddHandler(InputElement.PointerMovedEvent, CapturePointer, RoutingStrategies.Tunnel, handledEventsToo: true);
                window.MouseMove(origin + new Vector(-4, 3), RawInputModifiers.LeftMouseButton);
                Surface(panel, 0).RemoveHandler(InputElement.PointerMovedEvent, CapturePointer); actual?.Capture(null);
                void CapturePointer(object? sender, PointerEventArgs args) => actual = args.Pointer;
            }
            else if (reason == "save") pump(panel.SaveToAsync(0, Path.Combine(folder, "save-during-drag.png")));
            else panel.Dispose();
            window.MouseUp(origin + new Vector(-5, 3), MouseButton.Left); Render();
            check("image drag stale release after " + reason, !panel.HasDisplayPointerCapture && panel.OffsetPreview is null
                && (reason == "dispose" || panel.CaptureSettings().LeftOffset == default && panel.CaptureSettings().RightOffset == default && RawSame(panel, raw)), "display context invalidated; no release commit");
            if (reason != "dispose") Observe("cancel-" + reason, panel);
        }

        var none = Open("none", [raw, raw]); none.SetDragMode(ImageDragMode.None);
        var start = At(none, 0, 16, 16); window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(12, 10), RawInputModifiers.LeftMouseButton); window.MouseUp(start + new Vector(12, 10), MouseButton.Left); Render();
        check("image drag NONE has no new selection", none.RectangleSelection(0) is null && none.CaptureSettings().LeftOffset == default && none.HistoryCount == 0, "no configured drag");
        none.SelectRectangle(0, new(2, 2, 8, 8)); start = At(none, 0, 8, 8);
        window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(2, 2), RawInputModifiers.LeftMouseButton); pump(none.CurrentRectangleOperation); window.MouseUp(start + new Vector(2, 2), MouseButton.Left); Render();
        check("image drag NONE retains selection editing", none.HasFloatingImage, "existing selection overrides NONE"); none.CancelRectangleInteraction();

        for (var count = 2; count <= 3; count++)
        {
            var panel = Open("move-" + count, Enumerable.Repeat(Frame(520, 440), count).ToArray()); panel.SetDragMode(ImageDragMode.Move);
            var scrolls = Scrolls(panel); Render();
            check("image drag MOVE scroll ranges " + count, scrolls.All(s => s.Extent.Width > s.Viewport.Width && s.Extent.Height > s.Viewport.Height), "both axes positive");
            start = At(panel, 0, 120, 90); window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(-3, -5), RawInputModifiers.LeftMouseButton); Render();
            check("image drag MOVE zoom formula " + count, scrolls.All(s => s.Offset == new Vector(6, 10)), "trunc(previous-current)*zoom; synchronized absolute position");
            window.MouseUp(start + new Vector(-3, -5), MouseButton.Left); Render();
            window.MouseWheel(At(panel, 0, 60, 60), new Vector(0, -1)); Render();
            check("image drag wheel synchronous " + count, scrolls.All(s => s.Offset.Y == 26), "each pane +16, original wheel message delta");
            Surface(panel, 0).Focus(); KeyStroke(Key.Down, PhysicalKey.ArrowDown); Render();
            check("image drag keyboard synchronous " + count, scrolls.All(s => s.Offset.Y == 27), "each pane line delta +1");
            start = At(panel, 0, 120, 90); window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(3000, 3000), RawInputModifiers.LeftMouseButton); window.MouseUp(start + new Vector(3000, 3000), MouseButton.Left); Render();
            check("image drag MOVE minimum clamp " + count, scrolls.All(s => s.Offset == default), "negative endpoint clamps separately");
            start = scrolls[0].TranslatePoint(new(120, 90), window)!.Value;
            window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(-3000, -3000), RawInputModifiers.LeftMouseButton); window.MouseUp(start + new Vector(-3000, -3000), MouseButton.Left); Render();
            check("image drag MOVE maximum per-pane clamp " + count, scrolls.All(s => s.Offset.X == s.Extent.Width - s.Viewport.Width && s.Offset.Y == s.Extent.Height - s.Viewport.Height),
                "three-pane viewport differs by one pixel; each range applied");
            var prior = scrolls.Select(s => s.Offset).ToArray();
            window.MouseWheel(scrolls[0].TranslatePoint(new(60, 60), window)!.Value, new Vector(0, 1), RawInputModifiers.Shift); Render();
            check("image drag wheel preserves per-pane clamp difference " + count, scrolls.Select((s, i) => s.Offset.X == prior[i].X - 16 && s.Offset.Y == prior[i].Y).All(x => x),
                "wheel message delta differs from absolute thumb");
            var bar = scrolls[0].GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);
            var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single(); var thumbStart = thumb.TranslatePoint(new(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            IPointer? thumbPointer = null; var thumbSource = "";
            window.AddHandler(InputElement.PointerPressedEvent, ObserveThumbPress, RoutingStrategies.Tunnel, handledEventsToo: true);
            window.MouseDown(thumbStart, MouseButton.Left); window.RemoveHandler(InputElement.PointerPressedEvent, ObserveThumbPress);
            var thumbCapture = (thumbPointer?.Captured as Control)?.Name ?? thumbPointer?.Captured?.GetType().Name ?? "none";
            Observe("thumb-press-" + count + " source=" + thumbSource + " capture=" + thumbCapture + " bar=" + bar.Bounds, panel); log.Flush();
            window.MouseMove(thumbStart + new Vector(-25, 0), RawInputModifiers.LeftMouseButton); window.MouseUp(thumbStart + new Vector(-25, 0), MouseButton.Left); Render();
            SaveReviewPng("thumb-press-" + count);
            check("image drag actual scrollbar thumb uses absolute " + count, scrolls.All(s => s.Offset.X == scrolls[0].Offset.X),
                "real template ThumbTrack restores common position after per-pane clamp; source=" + thumbSource + "; capture=" + thumbCapture + "; bar=" + bar.Bounds);
            Observe("move-sync-" + count, panel); screenshot("image-drag-move-" + count + ".png");
            void ObserveThumbPress(object? sender, PointerPressedEventArgs args)
            {
                thumbPointer = args.Pointer; var visual = args.Source as Visual;
                var presenter = scrolls[0].GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>().Single();
                thumbSource = visual?.GetType().Name + " ancestry=" + string.Join('/', visual?.GetVisualAncestors().Select(v => v.GetType().Name) ?? [])
                    + " presenter=" + presenter.Bounds + " point=" + args.GetPosition(presenter);
            }
        }

        var narrow = Open("one-axis", [Frame(16, 440), Frame(16, 440)]); narrow.SetDragMode(ImageDragMode.Move); Render();
        start = At(narrow, 0, 12, 90); window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(-3, -5), RawInputModifiers.LeftMouseButton); window.MouseUp(start + new Vector(-3, -5), MouseButton.Left); Render();
        check("image drag axis zero remains zero", Scrolls(narrow).All(s => s.Offset.X == 0 && s.Offset.Y == 10), "horizontal range zero; vertical remains synchronized");

        var mixedRanges = Open("axis-zero-peer", [Frame(187, 440), Frame(187, 440), Frame(187, 440)]); mixedRanges.SetDragMode(ImageDragMode.Move); Render();
        var mixedScrolls = Scrolls(mixedRanges);
        check("image drag one-pixel mixed ranges", mixedScrolls[1].Extent.Width == mixedScrolls[1].Viewport.Width
            && mixedScrolls[0].Extent.Width - mixedScrolls[0].Viewport.Width == 1 && mixedScrolls[2].Extent.Width - mixedScrolls[2].Viewport.Width == 1,
            "3-pane middle viewport is one pixel wider");
        window.MouseWheel(mixedScrolls[0].TranslatePoint(new(60, 60), window)!.Value, new Vector(0, -1), RawInputModifiers.Shift); Render();
        start = mixedScrolls[1].TranslatePoint(new(60, 60), window)!.Value;
        window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(-3, -5), RawInputModifiers.LeftMouseButton); window.MouseUp(start + new Vector(-3, -5), MouseButton.Left); Render();
        check("image drag zero source axis preserves peer position", mixedScrolls[0].Offset.X == 1 && mixedScrolls[1].Offset.X == 0 && mixedScrolls[2].Offset.X == 1
            && mixedScrolls.All(s => s.Offset.Y == 10), "no horizontal source message; vertical MOVE leaves peer horizontal clamped state");
        Observe("mixed-axis-ranges", mixedRanges);

        var readonlyPanel = Open("readonly", [raw, raw], true); readonlyPanel.SetDragMode(ImageDragMode.AdjustOffset);
        start = At(readonlyPanel, 0, 16, 16); window.MouseDown(start, MouseButton.Left); window.MouseUp(start + new Vector(3, -5), MouseButton.Left); pump(readonlyPanel.CurrentDragOperation);
        check("image drag readonly offsets allowed", readonlyPanel.CaptureSettings().LeftOffset == new ImageOffset(1, 0) && readonlyPanel.HistoryCount == 0 && RawSame(readonlyPanel, raw), "readOnly protects pixels, not view mode");
        var tabs = readonlyPanel.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "ImageDisplayMode"); tabs.SelectedIndex = 1; Render();
        check("image drag pixel difference view disabled retains configured mode", !readonlyPanel.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ImageDragMode").IsEnabled
            && readonlyPanel.DragMode == ImageDragMode.AdjustOffset, "pixel difference has no image gesture; canonical overlay uses image panes"); tabs.SelectedIndex = 0;

        foreach (var mode in new[] { ImageDragMode.Move, ImageDragMode.AdjustOffset })
        {
            var priority = Open("priority-" + mode, [raw, raw]); priority.SetDragMode(mode); priority.SelectRectangle(0, new(2, 2, 8, 8));
            start = At(priority, 0, 8, 8); window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(2, 2), RawInputModifiers.LeftMouseButton); pump(priority.CurrentRectangleOperation);
            window.MouseUp(start + new Vector(2, 2), MouseButton.Left); Render();
            check("image drag selection precedes " + mode, priority.HasFloatingImage && priority.CaptureSettings().LeftOffset == default && priority.OffsetPreview is null, "existing selection becomes floating");
            priority.CancelRectangleInteraction(); pump(priority.BeginFloatingAsync(0, Frame(2, 2))); start = At(priority, 0, 1, 1);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(2, 2), RawInputModifiers.LeftMouseButton); window.MouseUp(start + new Vector(2, 2), MouseButton.Left); Render();
            check("image drag floating precedes " + mode, priority.HasFloatingImage && priority.FloatingPosition == (0, 1, 1) && priority.CaptureSettings().LeftOffset == default, "floating drag priority");
            priority.CancelRectangleInteraction(); var previousHistory = priority.HistoryCount; start = At(priority, 0, raw.Width * 2 + 2, raw.Height * 2 + 2);
            Observe("priority-before-resize-" + mode, priority);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(2, 2), RawInputModifiers.LeftMouseButton); window.MouseUp(start + new Vector(2, 2), MouseButton.Left); pump(priority.CurrentRectangleOperation); Render();
            Observe("priority-after-resize-" + mode, priority);
            check("image drag resize precedes " + mode, priority.HistoryCount == previousHistory + 1 && priority.CaptureEditFrames()[0].Width == raw.Width + 2 && priority.CaptureSettings().LeftOffset == default,
                $"actual pointer Resize: history {previousHistory}->{priority.HistoryCount}, width {raw.Width}->{priority.CaptureEditFrames()[0].Width}; prior selection move history retained");
        }

        var history = Open("history", [raw, raw]); pump(history.ResizeImageAsync(0, 33, 24)); history.SetDragMode(ImageDragMode.AdjustOffset);
        start = At(history, 0, 16, 16); window.MouseDown(start, MouseButton.Left); window.MouseUp(start + new Vector(3, -5), MouseButton.Left); pump(history.CurrentDragOperation);
        var historyOffsets = history.CaptureSettings().Offsets(false); pump(history.UndoEditAsync()); pump(history.RedoEditAsync());
        check("image drag Undo Redo retain offset", history.CaptureSettings().Offsets(false).SequenceEqual(historyOffsets) && history.HistoryCount == 1,
            "offset outside raw shared history");

        foreach (var replacement in new[] { false, true })
        {
            var panel = Open("pending-" + replacement, [raw, raw]); panel.SetDragMode(ImageDragMode.AdjustOffset); var before = panel.CaptureSettings();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            panel.FrameCandidateReady = async () => { entered.TrySetResult(); await release.Task; };
            start = At(panel, 0, 16, 16); window.MouseDown(start, MouseButton.Left); window.MouseUp(start + new Vector(3, -5), MouseButton.Left);
            pump(entered.Task); var pending = panel.CurrentDragOperation; panel.FrameCandidateReady = null;
            check("image drag pending release keeps confirmed settings " + replacement, panel.CaptureSettings() == before && panel.OffsetPreview is null && !panel.HasDisplayPointerCapture,
                "candidate compare waits at adoption boundary; display settings not mixed");
            if (replacement) pump(panel.ApplySettingsAsync(before));
            else panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ImageCancelEdit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            release.TrySetResult(); var canceled = false;
            try { pump(pending); } catch (OperationCanceledException) { canceled = true; }
            Render();
            check("image drag pending cancel or stale adoption " + replacement, canceled && panel.CaptureSettings().LeftOffset == default
                && panel.CaptureSettings().RightOffset == default && panel.HistoryCount == 0 && RawSame(panel, raw), "actual release comparison canceled/replaced; original display and raw retained");
            Observe("pending-" + replacement, panel);
        }

        foreach (var extreme in new[] { double.MaxValue, double.NaN, double.PositiveInfinity })
        {
            var panel = Open("extreme-" + (double.IsNaN(extreme) ? "nan" : double.IsInfinity(extreme) ? "infinity" : "max"), [raw, raw]); panel.SetDragMode(ImageDragMode.AdjustOffset);
            IPointer? actualPointer = null; var surface = Surface(panel, 0);
            surface.AddHandler(InputElement.PointerPressedEvent, Capture, RoutingStrategies.Tunnel, handledEventsToo: true);
            start = At(panel, 0, 16, 16); window.MouseDown(start, MouseButton.Left); surface.RemoveHandler(InputElement.PointerPressedEvent, Capture);
            surface.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, surface, actualPointer!, surface, new(extreme, extreme), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
            window.MouseUp(start, MouseButton.Left); Render();
            check("image drag injected coordinate rejection " + extreme, !panel.HasDisplayPointerCapture && panel.OffsetPreview is null
                && panel.CaptureSettings().LeftOffset == default && panel.HistoryCount == 0 && RawSame(panel, raw), "actual captured pointer with injected non-OS coordinate; no async-void exception");
            Observe("coordinate-rejected-" + extreme, panel);
            void Capture(object? sender, PointerPressedEventArgs args) => actualPointer = args.Pointer;
        }

        var tiffPath = Path.Combine(folder, "readonly-two-pages.tif");
        using (var resource = typeof(HeadlessImageDragChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.Tiff.two-pages-le.tif") ?? throw new InvalidDataException("TIFF drag fixture"))
        using (var file = File.Create(tiffPath)) resource.CopyTo(file);
        hashes[tiffPath] = Hash(File.ReadAllBytes(tiffPath));
        pane.DiscardChanges(); pane.ApplyProject(new() { Mode = "Image", LeftPath = tiffPath, RightPath = tiffPath, LeftReadOnly = true, RightReadOnly = true,
            ImageSettings = new() { Zoom = 2, ShowDifferences = false, LeftFrame = 2, RightFrame = 2 } }); pump(pane.ComparePathsAsync()); Render();
        var tiff = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single(); tiff.SetDragMode(ImageDragMode.AdjustOffset);
        var originalTiff = tiff.CaptureReport().Images.Select(s => Hash(s.Decode(2, default).Pixels)).ToArray();
        start = At(tiff, 0, 1, 1); window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(3, -5), RawInputModifiers.LeftMouseButton); window.MouseUp(start + new Vector(3, -5), MouseButton.Left); pump(tiff.CurrentDragOperation);
        check("image drag readonly TIFF page two", tiff.CaptureSettings().LeftOffset == new ImageOffset(1, 0) && tiff.CaptureSettings().RightOffset == new ImageOffset(0, 2)
            && tiff.HistoryCount == 0 && tiff.CaptureReport().Images.Select(s => Hash(s.Decode(2, default).Pixels)).SequenceEqual(originalTiff), "multipage view does not require editSession");
        pump(tiff.SetFramesAsync(1, 1)); Render();
        check("image drag TIFF page switch retains offsets", tiff.LeftFrame == 1 && tiff.RightFrame == 1 && tiff.CaptureSettings().LeftOffset == new ImageOffset(1, 0), "page two to one");
        screenshot("image-drag-readonly-tiff.png");

        ReviewRegressionChecks();
        BlankViewportChecks();
        foreach (var result in sharedMenuResults) check(result.Name, result.Passed, result.Detail);
        SettingsChecks();
        check("image drag inputs preserved", hashes.All(pair => Hash(File.ReadAllBytes(pair.Key)) == pair.Value), "all PNG original bytes");
        window.ImageOptions.SetMode(ImageDragMode.Move);
        pane.DiscardChanges(); pane.ApplyProject(incomingProject);
        log.Flush();

        SpecializedViews.ImagePanel Open(string name, ImageComparisonEngine.DecodedFrame[] frames, bool readOnly = false)
        {
            var paths = frames.Select((frame, i) => Path.Combine(folder, name + "-input-" + i + ".png")).ToArray();
            for (var i = 0; i < frames.Length; i++) { pump(ImagePngStore.SaveAsync(paths[i], frames[i], [])); hashes[paths[i]] = Hash(File.ReadAllBytes(paths[i])); }
            pane.DiscardChanges(); pane.ApplyProject(new() { Mode = "Image", LeftPath = paths[0], RightPath = paths[^1], BasePath = frames.Length == 3 ? paths[1] : "",
                LeftReadOnly = readOnly, ImageSettings = new() { Zoom = 2, ShowDifferences = false, BlockSize = 1 } });
            pump(pane.ComparePathsAsync()); Render(); return pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
        }
        void SharedMenuBoundaryChecks()
        {
            var panel = Open("shared-menu-image", [Frame(520, 440), Frame(520, 440)]); panel.SetDragMode(ImageDragMode.Move);
            var imageBar = Scrolls(panel)[0].GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
            var imageMenu = (MenuFlyout)imageBar.ContextFlyout!;
            var plain = new ScrollViewer { Content = new Border { Width = 1000, Height = 1000 }, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var host = new Window { Width = 320, Height = 240, Content = plain, Title = "Shared scrollbar command verification" };
            var results = new List<(string Name, bool Passed, string Detail)>();
            try
            {
                host.Show(); Render();
                var plainBar = plain.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
                var plainMenu = (MenuFlyout)plainBar.ContextFlyout!;
                imageMenu.ShowAt(imageBar); Render(); Click(imageMenu, "Bottom"); imageMenu.Hide(); Render();
                foreach (var id in new[] { "Bottom", "Top" })
                {
                    plainMenu.ShowAt(plainBar); Render(); var item = Item(plainMenu, id); var canExecute = item.Command?.CanExecute(item.CommandParameter) == true;
                    var events = 0; plainBar.Scroll += OnScroll; Click(plainMenu, id); Render(); plainBar.Scroll -= OnScroll;
                    var expected = id == "Bottom" ? Math.Max(0, plain.Extent.Height - plain.Viewport.Height) : 0;
                    results.Add(("image drag shared menu nonimage fallback " + id, canExecute && events == 1 && plain.Offset.Y == expected,
                        $"sameMenu={ReferenceEquals(imageMenu, plainMenu)}; CanExecute={canExecute}; events={events}; actual={plain.Offset.Y}; expected={expected}; command={item.Command?.GetType().Name}"));
                    plainMenu.Hide(); Render();
                    void OnScroll(object? sender, ScrollEventArgs args) => events++;
                }
                imageMenu.ShowAt(imageBar); Render(); panel.Dispose();
                var disposedItem = Item(imageMenu, "Bottom"); var allowed = disposedItem.Command?.CanExecute(disposedItem.CommandParameter) == true;
                var disposedEvents = 0; imageBar.Scroll += OnDisposedScroll; Click(imageMenu, "Bottom"); Render(); imageBar.Scroll -= OnDisposedScroll;
                results.Add(("image drag shared menu disposed owner fallback", allowed && disposedEvents == 1,
                    $"image owner disposed while native menu remains open; CanExecute={allowed}; nativeScrollEvents={disposedEvents}"));
                imageMenu.Hide(); Render();
                void OnDisposedScroll(object? sender, ScrollEventArgs args) => disposedEvents++;
                using var bitmap = host.CaptureRenderedFrame() ?? throw new InvalidOperationException("shared menu rendered frame unavailable");
                bitmap.Save(Path.Combine(folder, "shared-menu-nonimage.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            finally { host.Close(); Render(); }
            using (var resultFile = File.Create(Path.Combine(folder, "shared-menu-results.json")))
            using (var writer = new Utf8JsonWriter(resultFile, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartArray(); foreach (var result in results)
                { writer.WriteStartObject(); writer.WriteString("name", result.Name); writer.WriteBoolean("passed", result.Passed); writer.WriteString("detail", result.Detail); writer.WriteEndObject(); }
                writer.WriteEndArray();
            }
            // 失敗先行runでもThumb/blank viewportの後続証拠を採取する。
            log.Flush();
            // 最終判定は既存mode検証後にまとめて行う。
            sharedMenuResults.AddRange(results);
            static MenuItem Item(MenuFlyout menu, string id) => menu.Items.OfType<MenuItem>().Single(i => Avalonia.Automation.AutomationProperties.GetAutomationId(i) == id);
            static void Click(MenuFlyout menu, string id) => Item(menu, id).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        void BlankViewportChecks()
        {
            var results = new List<(string Name, bool Passed, string Detail)>();
            foreach (var count in new[] { 2, 3 })
            foreach (var mode in new[] { ImageDragMode.Move, ImageDragMode.AdjustOffset })
            {
                var image = Frame(16, 440); var panel = Open("blank-viewport-" + count + "-" + mode, Enumerable.Repeat(image, count).ToArray()); panel.SetDragMode(mode);
                var scrolls = Scrolls(panel); var grid = Surface(panel, 0); var origin = scrolls[0].TranslatePoint(new(200, 100), window)!.Value;
                var gridPoint = grid.TranslatePoint(new(0, 0), window)!.Value; var clientPoint = origin - gridPoint;
                var expectedBlank = clientPoint.X >= grid.Bounds.Width && 200 < scrolls[0].Viewport.Width && 100 < scrolls[0].Viewport.Height;
                var before = panel.CaptureSettings(); var extents = scrolls.Select(s => s.Extent).ToArray(); var hit = "";
                window.AddHandler(InputElement.PointerPressedEvent, ObservePress, RoutingStrategies.Tunnel, handledEventsToo: true);
                window.MouseDown(origin, MouseButton.Left); window.RemoveHandler(InputElement.PointerPressedEvent, ObservePress);
                var delta = mode == ImageDragMode.Move ? new Vector(0, -5) : new Vector(3, -5);
                window.MouseMove(origin + delta, RawInputModifiers.LeftMouseButton); Render();
                var moving = mode == ImageDragMode.Move ? scrolls.All(s => s.Offset.Y == 10 && s.Offset.X == 0)
                    : panel.OffsetPreview == new ImageRectangle(1, -2, 17, 438);
                results.Add(("image drag blank viewport press " + count + " " + mode,
                    expectedBlank && moving && panel.HasDisplayPointerCapture && panel.CaptureSettings() == before && RawSame(panel, image)
                        && panel.HistoryCount == 0 && !panel.HasUnsavedChanges && extents.SequenceEqual(scrolls.Select(s => s.Extent)),
                    $"actual Pointer source={hit}; grid={grid.Bounds}; grid point={clientPoint}; viewport={scrolls[0].Viewport}; expectedBlank={expectedBlank}; mode={mode}; scrollY={scrolls[0].Offset.Y}; preview={panel.OffsetPreview}"));
                Observe("blank-viewport-press-" + count + "-" + mode, panel); SaveReviewPng("blank-viewport-press-" + count + "-" + mode);
                window.MouseUp(origin + delta, MouseButton.Left); pump(panel.CurrentDragOperation); Render();
                var after = panel.CaptureSettings();
                results.Add(("image drag blank viewport release " + count + " " + mode,
                    !panel.HasDisplayPointerCapture && panel.OffsetPreview is null && RawSame(panel, image) && panel.HistoryCount == 0 && !panel.HasUnsavedChanges
                        && (mode == ImageDragMode.Move ? after == before && scrolls.All(s => s.Offset.Y == 10)
                            : after.LeftOffset == new ImageOffset(1, 0) && after.RightOffset == new ImageOffset(0, 2) && (count == 2 || after.MiddleOffset == new ImageOffset(0, 2))),
                    "real captured release; view changes without raw/history edits"));
                Observe("blank-viewport-release-" + count + "-" + mode, panel);
                void ObservePress(object? sender, PointerPressedEventArgs args) => hit = (args.Source as Control)?.GetType().Name + ":" + (args.Source as Control)?.Name;
            }
            using (var resultFile = File.Create(Path.Combine(folder, "blank-viewport-results.json")))
            using (var writer = new Utf8JsonWriter(resultFile, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartArray(); foreach (var result in results)
                { writer.WriteStartObject(); writer.WriteString("name", result.Name); writer.WriteBoolean("passed", result.Passed); writer.WriteString("detail", result.Detail); writer.WriteEndObject(); }
                writer.WriteEndArray();
            }
            log.Flush(); foreach (var result in results) check(result.Name, result.Passed, result.Detail);
        }
        void ReviewRegressionChecks()
        {
            // 全指摘の実測を先に保存し、失敗版でも後続の再現証拠を失わない。
            var results = new List<(string Name, bool Passed, string Detail)>();
            foreach (var mode in new[] { ImageDragMode.Move, ImageDragMode.AdjustOffset })
            foreach (var reason in new[] { "release", "capture-loss", "new-press" })
            {
                var image = mode == ImageDragMode.Move ? Frame(520, 440) : raw;
                var panel = Open("review-await-" + mode + "-" + reason, [image, image]); panel.SetDragMode(mode);
                pump(panel.BeginFloatingAsync(0, Frame(2, 2)));
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                panel.EditCandidateReady = () => { entered.TrySetResult(); return release.Task; };
                IPointer? pointer = null; var surface = Surface(panel, 0);
                surface.AddHandler(InputElement.PointerPressedEvent, Capture, RoutingStrategies.Tunnel, handledEventsToo: true);
                var origin = At(panel, 0, 16, 16); window.MouseDown(origin, MouseButton.Left); pump(entered.Task);
                surface.RemoveHandler(InputElement.PointerPressedEvent, Capture);
                if (reason == "capture-loss") pointer!.Capture(null);
                window.MouseUp(origin, MouseButton.Left);
                var replacementStarted = false;
                var selectAll = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ImageSelectAllRectangle");
                if (reason == "new-press")
                    selectAll.PropertyChanged += StartReplacement;
                release.TrySetResult(); pump(panel.CurrentRectangleOperation); Render(); panel.EditCandidateReady = null;
                selectAll.PropertyChanged -= StartReplacement;
                if (reason == "new-press")
                {
                    results.Add(("image drag review superseded handler retains new selection " + mode,
                        replacementStarted && panel.RectangleSelection(0) == new ImageRectangle(10, 10, 14, 14), "real new press when adoption reenables controls, before old await returns; serial owns current selection"));
                    window.MouseUp(At(panel, 0, 24, 24), MouseButton.Left); panel.SetDragMode(mode);
                }
                void StartReplacement(object? sender, AvaloniaPropertyChangedEventArgs args)
                {
                    if (args.Property == InputElement.IsEnabledProperty && selectAll.IsEnabled && !replacementStarted)
                    {
                        selectAll.PropertyChanged -= StartReplacement; replacementStarted = true;
                        panel.SetDragMode(ImageDragMode.RectangleSelect); panel.SelectRectangle(0, new(10, 10, 14, 14));
                        window.MouseDown(At(panel, 0, 24, 24), MouseButton.Left);
                    }
                }
                var settings = panel.CaptureSettings(); var pixels = panel.CaptureEditFrames().Select(f => Hash(f.Pixels)).ToArray();
                var positions = Scrolls(panel).Select(s => s.Offset).ToArray(); var historyCount = panel.HistoryCount;
                window.MouseMove(origin + new Vector(-3, -5)); Render();
                var valid = !panel.HasDisplayPointerCapture && panel.OffsetPreview is null && settings == panel.CaptureSettings()
                    && positions.SequenceEqual(Scrolls(panel).Select(s => s.Offset)) && historyCount == panel.HistoryCount
                    && pixels.SequenceEqual(panel.CaptureEditFrames().Select(f => Hash(f.Pixels)));
                results.Add(("image drag review awaited outside press " + mode + " " + reason, valid,
                    "real floating outside press + EditCandidateReady gate; stale handler and buttonless hover must be inert"));
                Observe("review-await-" + mode + "-" + reason, panel); SaveReviewPng("review-await-" + mode + "-" + reason);
                // 次の有効pressは通常のconfigured modeで動く。
                panel.CancelRectangleInteraction(); origin = At(panel, 0, 16, 16); window.MouseDown(origin, MouseButton.Left);
                window.MouseMove(origin + new Vector(-3, -5), RawInputModifiers.LeftMouseButton); Render();
                results.Add(("image drag review fresh press " + mode + " " + reason,
                    panel.HasDisplayPointerCapture && (mode == ImageDragMode.AdjustOffset ? panel.OffsetPreview is not null : Scrolls(panel)[0].Offset != positions[0]), "real next press remains usable"));
                window.MouseUp(origin + new Vector(-3, -5), MouseButton.Left); pump(panel.CurrentDragOperation); Render();
                void Capture(object? sender, PointerPressedEventArgs args) => pointer = args.Pointer;
            }
            foreach (var count in new[] { 2, 3 })
            foreach (var command in new[] { "LineDown", "LineUp", "PageDown", "PageUp" })
            {
                var image = Frame(count == 3 ? 187 : 520, 440);
                var panel = Open("review-bar-" + count + "-" + command, Enumerable.Repeat(image, count).ToArray()); panel.SetDragMode(ImageDragMode.Move);
                var scrolls = Scrolls(panel); var source = count == 3 ? 1 : 0;
                // 実layoutでpeer viewportを17px短くし、source端にもpeerには残rangeを作る。
                for (var i = 0; i < count; i++) if (i != source) scrolls[i].MaxHeight = scrolls[i].Bounds.Height - 17;
                Render();
                window.MouseWheel(scrolls[0].TranslatePoint(new(60, 60), window)!.Value, new Vector(0, -1), RawInputModifiers.Shift); Render();
                if (command.EndsWith("Down", StringComparison.Ordinal))
                {
                    var origin = scrolls[source].TranslatePoint(new(60, 60), window)!.Value;
                    window.MouseDown(origin, MouseButton.Left); window.MouseMove(origin + new Vector(0, -10000), RawInputModifiers.LeftMouseButton);
                    window.MouseUp(origin + new Vector(0, -10000), MouseButton.Left); Render();
                }
                else { window.MouseWheel(scrolls[0].TranslatePoint(new(60, 60), window)!.Value, new Vector(0, -10000)); Render(); }
                var before = scrolls.Select(s => s.Offset).ToArray(); var sign = command.EndsWith("Down", StringComparison.Ordinal) ? 1 : -1;
                var expected = scrolls.Select((s, i) => new Vector(before[i].X, Math.Clamp(before[i].Y + sign * (command.StartsWith("Line", StringComparison.Ordinal) ? 1 : s.Viewport.Height),
                    0, Math.Max(0, s.Extent.Height - s.Viewport.Height)))).ToArray();
                var bar = scrolls[source].GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
                var button = bar.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_" + command + "Button");
                var events = new List<string>(); bar.Scroll += OnScroll;
                // 実template Button.Click→ScrollBarのclamp→Scrollイベント。端の零寸法page領域も同じcommand経路で発火する。
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Render(); bar.Scroll -= OnScroll;
                results.Add(("image drag review actual scrollbar " + count + " " + command,
                    scrolls.Select(s => s.Offset).SequenceEqual(expected) && events.Count == 1,
                    $"Button.Click real ScrollBar event {string.Join(';', events)}; before={string.Join(';', before)}; expected={string.Join(';', expected)}; actual={string.Join(';', scrolls.Select(s => s.Offset))}"));
                Observe("review-bar-" + count + "-" + command, panel); SaveReviewPng("review-bar-" + count + "-" + command);
                void OnScroll(object? sender, ScrollEventArgs args) => events.Add(args.ScrollEventType + ":" + args.NewValue);
            }
            foreach (var delta in new[] { new Vector(100, 80), new Vector(-20, -16) })
            {
                var panel = Open(delta.X > 0 ? "review-preview-positive" : "review-preview-negative", [raw, raw]); panel.SetDragMode(ImageDragMode.AdjustOffset);
                var settings = panel.CaptureSettings(); var extent = Scrolls(panel).Select(s => s.Extent).ToArray(); var origin = At(panel, 0, 16, 16);
                window.MouseDown(origin, MouseButton.Left); window.MouseMove(origin + delta, RawInputModifiers.LeftMouseButton); Render();
                var preview = panel.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ImageOffsetPreview0");
                var edge = preview.TranslatePoint(delta.X > 0 ? new(.5, preview.Bounds.Height / 2) : new(preview.Bounds.Width - .5, preview.Bounds.Height / 2), window)!.Value;
                var name = delta.X > 0 ? "review-preview-positive" : "review-preview-negative"; var png = SaveReviewPng(name);
                var decoded = HeadlessImageCopyChecks.ReadPng(png); var gold = 0;
                for (var y = Math.Max(0, (int)edge.Y - 2); y <= Math.Min(decoded.Height - 1, (int)edge.Y + 2); y++)
                for (var x = Math.Max(0, (int)edge.X - 2); x <= Math.Min(decoded.Width - 1, (int)edge.X + 2); x++)
                { var at = (y * decoded.Width + x) * 4; if (decoded.Pixels[at] < 40 && decoded.Pixels[at + 1] > 170 && decoded.Pixels[at + 2] > 220) gold++; }
                results.Add(("image drag review viewport preview " + name, gold > 0 && settings == panel.CaptureSettings() && RawSame(panel, raw)
                    && panel.HistoryCount == 0 && !panel.HasUnsavedChanges && extent.SequenceEqual(Scrolls(panel).Select(s => s.Extent)),
                    $"independent rendered PNG gold edge pixels={gold}, root edge={edge}; preview does not enlarge scroll extent or edit pixels/settings"));
                Observe(name, panel); window.MouseUp(origin + delta, MouseButton.Left); pump(panel.CurrentDragOperation); Render();
            }
            var context = Open("review-context", [Frame(520, 440), Frame(520, 440), Frame(520, 440)]); context.SetDragMode(ImageDragMode.Move);
            var contextScrolls = Scrolls(context); contextScrolls[0].MaxHeight = contextScrolls[0].Bounds.Height - 17; Render();
            foreach (var source in new[] { 1, 0 })
            foreach (var id in new[] { "PageDown", "Bottom", "PageUp", "Top", "RightEdge", "LeftEdge" })
            {
                // page/端が同NewValueになるsource端から開始し、peer残rangeと別axisを観測する。
                window.MouseWheel(contextScrolls[source].TranslatePoint(new(60, 60), window)!.Value, new Vector(0, -1), RawInputModifiers.Shift); Render();
                var horizontal = id is "RightEdge" or "LeftEdge";
                if (id is "PageDown" or "Bottom")
                {
                    var origin = contextScrolls[source].TranslatePoint(new(60, 60), window)!.Value;
                    window.MouseDown(origin, MouseButton.Left); window.MouseMove(origin + new Vector(0, -10000), RawInputModifiers.LeftMouseButton);
                    window.MouseUp(origin + new Vector(0, -10000), MouseButton.Left); Render();
                }
                else { window.MouseWheel(contextScrolls[source].TranslatePoint(new(60, 60), window)!.Value, new Vector(0, -10000)); Render(); }
                var before = contextScrolls.Select(s => s.Offset).ToArray(); var page = id is "PageDown" or "PageUp"; var end = id is "Bottom" or "RightEdge";
                var expected = contextScrolls.Select((s, i) => horizontal ? new Vector(end ? Math.Max(0, s.Extent.Width - s.Viewport.Width) : 0, before[i].Y)
                    : new Vector(before[i].X, page ? Math.Clamp(before[i].Y + (id == "PageDown" ? 1 : -1) * s.Viewport.Height, 0, Math.Max(0, s.Extent.Height - s.Viewport.Height))
                        : end ? Math.Max(0, s.Extent.Height - s.Viewport.Height) : 0)).ToArray();
                var bar = contextScrolls[source].GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == (horizontal ? Avalonia.Layout.Orientation.Horizontal : Avalonia.Layout.Orientation.Vertical));
                var menu = (MenuFlyout)bar.ContextFlyout!; menu.ShowAt(bar); Render();
                var item = menu.Items.OfType<MenuItem>().Single(i => Avalonia.Automation.AutomationProperties.GetAutomationId(i) == id);
                var events = new List<string>(); bar.Scroll += OnScroll;
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Render(); bar.Scroll -= OnScroll; menu.Hide(); Render();
                results.Add(("image drag review real context " + source + " " + id,
                    contextScrolls.Select(s => s.Offset).SequenceEqual(expected) && events.Count == 1,
                    $"MenuFlyout.ShowAt + MenuItem.Click; command={item.Command?.GetType().Name}; native Scroll events={string.Join(';', events)}; expected={string.Join(';', expected)}; actual={string.Join(';', contextScrolls.Select(s => s.Offset))}"));
                Observe("review-context-" + source + "-" + id, context);
                void OnScroll(object? sender, ScrollEventArgs args) => events.Add(args.ScrollEventType + ":" + args.NewValue);
            }
            SaveReviewPng("review-context");
            using (var resultFile = File.Create(Path.Combine(folder, "review-results.json")))
            using (var writer = new Utf8JsonWriter(resultFile, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartArray(); foreach (var result in results)
                { writer.WriteStartObject(); writer.WriteString("name", result.Name); writer.WriteBoolean("passed", result.Passed); writer.WriteString("detail", result.Detail); writer.WriteEndObject(); }
                writer.WriteEndArray();
            }
            log.Flush(); foreach (var result in results) check(result.Name, result.Passed, result.Detail);
        }
        string SaveReviewPng(string name)
        {
            Render(); using var bitmap = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("review rendered frame unavailable");
            var path = Path.Combine(folder, name + ".png"); bitmap.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); return path;
        }
        void SettingsChecks()
        {
            var path = Path.Combine(folder, "shared-options.json"); var store = new ImageApplicationOptionsStore(path);
            var unavailable = ImageApplicationOptionsStore.ForDesktop("");
            check("image drag desktop missing path is not memory success", !unavailable.SetMode(ImageDragMode.None) && unavailable.Mode == ImageDragMode.Move
                && unavailable.Diagnostic is not null, "explicit fixture override avoids OS path lookup");
            check("image drag settings default MOVE", store.Mode == ImageDragMode.Move, "missing file");
            check("image drag settings persist", store.SetMode(ImageDragMode.AdjustOffset) && new ImageApplicationOptionsStore(path).Mode == ImageDragMode.AdjustOffset, "fixture path reload; no AppData access");
            var saved = File.ReadAllBytes(path); File.SetAttributes(path, FileAttributes.ReadOnly);
            check("image drag settings readonly failure keeps mode", !store.SetMode(ImageDragMode.None) && store.Mode == ImageDragMode.AdjustOffset && File.ReadAllBytes(path).AsSpan().SequenceEqual(saved), store.Diagnostic ?? "");
            File.SetAttributes(path, FileAttributes.Normal);
            foreach (var json in new[] { "{", "{\"dragMode\":9}", "{\"dragMode\":null}", "{\"unknown\":1}" })
            {
                File.WriteAllText(path, json); var invalid = File.ReadAllBytes(path);
                check("image drag invalid settings retain " + json, !store.Reload() && store.Mode == ImageDragMode.AdjustOffset
                    && new ImageApplicationOptionsStore(path).Mode == ImageDragMode.Move && File.ReadAllBytes(path).AsSpan().SequenceEqual(invalid), "diagnostic plus preserved bytes; initial default versus existing state");
            }
            var oversized = new string(' ', ImageApplicationOptionsStore.MaximumBytes + 1); File.WriteAllText(path, oversized);
            check("image drag oversized settings rejected", !store.Reload() && store.Mode == ImageDragMode.AdjustOffset, "bounded read");
            store.Changed += () => throw new InvalidOperationException("injected notification failure");
            check("image drag notification failure keeps committed result", store.SetMode(ImageDragMode.RectangleSelect) && store.Mode == ImageDragMode.RectangleSelect
                && new ImageApplicationOptionsStore(path).Mode == ImageDragMode.RectangleSelect && store.Diagnostic?.Contains("確定") == true, "saved before notification; never claims rollback");
            var input = hashes.First().Key; var blocked = new ImageApplicationOptionsStore(input); var bytes = File.ReadAllBytes(input);
            blocked.AddOutputGuard(target => { if (target == input) throw new InvalidOperationException("protected input"); });
            check("image drag settings input collision rejected", !blocked.SetMode(ImageDragMode.None) && File.ReadAllBytes(input).AsSpan().SequenceEqual(bytes), blocked.Diagnostic ?? "");
            var current = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
            var applicationPath = Path.Combine(output, "image-application-options.json"); var applicationBytes = File.ReadAllBytes(applicationPath);
            var applicationMode = current.DragMode; var combo = current.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ImageDragMode");
            var selectedIndex = combo.SelectedIndex;
            File.SetAttributes(applicationPath, FileAttributes.ReadOnly);
            try
            {
                combo.SelectedIndex = selectedIndex == 0 ? 1 : 0; Render();
                check("image drag actual settings control save failure restores UI", current.DragMode == applicationMode && combo.SelectedIndex == selectedIndex
                    && File.ReadAllBytes(applicationPath).AsSpan().SequenceEqual(applicationBytes), "actual SelectionChanged; no partial global adoption");
            }
            finally { File.SetAttributes(applicationPath, FileAttributes.Normal); }
            var secondPane = window.AddSession(); var paths = hashes.Keys.Take(2).ToArray();
            secondPane.ApplyProject(new() { Mode = "Image", LeftPath = paths[0], RightPath = paths[1] }); pump(secondPane.ComparePathsAsync()); Render();
            var second = secondPane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
            current.SetDragMode(ImageDragMode.None);
            check("image drag global sharing all tabs", second.DragMode == ImageDragMode.None && window.ImageOptions.Mode == ImageDragMode.None
                && second.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ImageDragMode").SelectedIndex == 0, "one store shared through desktop caller and all pane controls");
            secondPane.DiscardChanges(); second.Dispose();
            window.SelectSession(window.SessionPanes.ToList().IndexOf(pane)); Render();
        }
        void Observe(string name, SpecializedViews.ImagePanel panel)
        {
            using var buffer = new MemoryStream(); using (var writer = new Utf8JsonWriter(buffer))
            {
                var settings = panel.CaptureSettings(); writer.WriteStartObject(); writer.WriteString("name", name); writer.WriteNumber("mode", (int)panel.DragMode);
                writer.WriteString("leftOffset", settings.LeftOffset.ToString()); writer.WriteString("rightOffset", settings.RightOffset.ToString()); writer.WriteString("middleOffset", settings.MiddleOffset.ToString());
                writer.WriteNumber("historyCount", panel.HistoryCount); writer.WriteBoolean("dirty", panel.HasUnsavedChanges); writer.WriteString("preview", panel.OffsetPreview?.ToString());
                writer.WriteStartArray("scrolls"); foreach (var scroll in Scrolls(panel)) { writer.WriteStartObject(); writer.WriteNumber("x", scroll.Offset.X); writer.WriteNumber("y", scroll.Offset.Y);
                    writer.WriteNumber("rangeX", Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width)); writer.WriteNumber("rangeY", Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)); writer.WriteEndObject(); } writer.WriteEndArray();
                writer.WriteStartArray("rawSha256"); foreach (var frame in panel.CaptureEditFrames()) writer.WriteStringValue(Hash(frame.Pixels)); writer.WriteEndArray(); writer.WriteEndObject();
            }
            log.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        }
        Point At(SpecializedViews.ImagePanel panel, int side, double x, double y) => Surface(panel, side).TranslatePoint(new(x, y), window) ?? throw new InvalidOperationException("image drag pointer surface");
        void Render() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        void KeyStroke(Key key, PhysicalKey physical) { window.KeyPress(key, RawInputModifiers.None, physical, null); window.KeyRelease(key, RawInputModifiers.None, physical, null); Dispatcher.UIThread.RunJobs(); }
    }
    private static Grid Surface(SpecializedViews.ImagePanel panel, int pane) => panel.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ImageRectangleSurface" + pane);
    private static ScrollViewer[] Scrolls(SpecializedViews.ImagePanel panel) => panel.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.Name?.StartsWith("ImagePaneScroll", StringComparison.Ordinal) == true).OrderBy(s => s.Name).ToArray();
    private static ImageComparisonEngine.DecodedFrame Frame(int width, int height) => new(1, width, height, Enumerable.Range(0, width * height * 4).Select(i => (byte)(i * 37 + 17)).ToArray());
    private static bool RawSame(SpecializedViews.ImagePanel panel, ImageComparisonEngine.DecodedFrame expected) => panel.CaptureEditFrames().All(frame => frame.Width == expected.Width && frame.Height == expected.Height && frame.Pixels.AsSpan().SequenceEqual(expected.Pixels));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
