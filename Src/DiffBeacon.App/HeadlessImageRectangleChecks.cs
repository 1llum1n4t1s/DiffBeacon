using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessImageRectangleChecks
{
    // 原本fixtureはadapter宣言の範囲だけで使用する。OS clipboardへの書込みは行わない。
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "image-rectangles"); Directory.CreateDirectory(folder);
        var inputHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        File.WriteAllText(Path.Combine(folder, "failure-contract.txt"), "半開endpoint/offset/ghost/oriented座標、透明RGB、clipboard失敗・flush失敗・古い完了のCut禁止、readonly/取消/128履歴、原本resize独立履歴、floating Enter/Escape/外側click/focuslossとCtrl-stamp、PNG原画保存。OS clipboardは注入し実操作しない。\n");
        using var log = File.Create(Path.Combine(folder, "observations.ndjson")); using var writer = new StreamWriter(log);
        var resizeBytes = Resource("ImageResize.Golden.gz", true); var resizeCasesBytes = Resource("ImageResize.Cases.json", false);
        check("rectangle GUI resize source SHA", Hash(resizeBytes) == "02CC94AE415C836631787D8657C15FCDE58026E8B7BF5D837160CF091794AD1B", "96 unmodified C++ resize cases; declared orientation/zero-filled adapter");
        check("rectangle GUI resize inputs SHA", Hash(resizeCasesBytes) == "ABCBEAC32B9B4FF197DF106AD546A284D481FB8DA5F8020734D3B363E5E7392D", "");
        using var resizeGolden = JsonDocument.Parse(resizeBytes); using var resizeCases = JsonDocument.Parse(resizeCasesBytes);
        var resized = 0;
        foreach (var item in resizeCases.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("id").GetString()!; var target = Frame(item.GetProperty("target"));
            var orientation = new ImageOrientation { Rotation = item.GetProperty("rotation").GetInt32(),
                FlipHorizontal = item.GetProperty("flipHorizontal").GetBoolean(), FlipVertical = item.GetProperty("flipVertical").GetBoolean() };
            var panel = Open(name, [target, target], new() { LeftOrientation = orientation, Zoom = 8, BlockSize = 1, ShowDifferences = false });
            var expected = resizeGolden.RootElement.EnumerateArray().Single(row => row.GetProperty("id").GetString() == name);
            pump(panel.ResizeImageAsync(0, item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32()));
            var wanted = new ImageComparisonEngine.DecodedFrame(1, expected.GetProperty("width").GetInt32(), expected.GetProperty("height").GetInt32(), Convert.FromHexString(expected.GetProperty("bgraHex").GetString()!));
            State(name, panel, wanted, target);
            var pushes = expected.GetProperty("historyPushes").GetInt32();
            check(name + " independent history", panel.HistoryCount == pushes && panel.PaneModified(0) == (pushes != 0), "raw-size early no-op and resize history");
            check(name + " direction retained", panel.CaptureSettings().LeftOrientation == orientation, "");
            pump(panel.UndoEditAsync()); State(name + " undo", panel, target, target);
            pump(panel.RedoEditAsync()); State(name + " redo", panel, wanted, target);
            var path = Path.Combine(folder, name + "-saved.png"); pump(panel.SaveToAsync(0, path));
            check(name + " raw PNG independent decode", Same(HeadlessImageCopyChecks.ReadPng(path), wanted), "full BGRA including transparent RGB");
            resized++;
        }
        check("rectangle GUI original resize scope", resized == 96, resized.ToString());

        var originalBytes = Resource("ImageRectangles.Golden.gz", true);
        check("rectangle GUI original kernel SHA", Hash(originalBytes) == "E32876DC786381285E6FDA5363073A1C09881934AB1B9B8C9645FA5A132A14E2", "identity BGRA shim; original GUI/FreeImage crop not dynamically measured");
        using var rectangles = JsonDocument.Parse(Resource("ImageRectangles.Cases.json", false)); using var original = JsonDocument.Parse(originalBytes);
        var originalCount = 0;
        foreach (var item in rectangles.RootElement.GetProperty("cases").EnumerateArray())
        {
            var target = item.GetProperty("target"); var source = item.GetProperty("source"); var name = item.GetProperty("id").GetString()!;
            var paste = item.GetProperty("op").GetString() == "paste"; var ro = item.GetProperty("readOnly").GetBoolean();
            if (item.GetProperty("pane").GetInt32() is < 0 or > 1 || target.GetProperty("width").GetInt32() == 0 || target.GetProperty("height").GetInt32() == 0
                || source.GetProperty("width").GetInt32() == 0 || source.GetProperty("height").GetInt32() == 0 || paste && ro) continue;
            var before = Frame(target); var payload = Frame(source);
            var panel = Open(name, [before, payload], readOnly: ro);
            var expected = original.RootElement.EnumerateArray().Single(row => row.GetProperty("id").GetString() == name);
            var wanted = new ImageComparisonEngine.DecodedFrame(1, before.Width, before.Height, Convert.FromHexString(expected.GetProperty("bgraHex").GetString()!));
            var rect = item.GetProperty("rect");
            using var clipboard = new ClipboardStub(); panel.ClipboardOverride = ImageClipboard.ForPlatform(() => clipboard);
            if (paste)
            {
                clipboard.Seed(payload); pump(panel.PasteClipboardAsync());
                check(name + " floating before confirmation", panel.HasFloatingImage && panel.HistoryCount == 0, "same raw size; paste pixels not committed");
                panel.MoveFloating(rect[0].GetInt32(), rect[1].GetInt32()); pump(panel.CommitFloatingAsync());
            }
            else
            {
                panel.SelectRectangle(0, new(rect[0].GetInt32(), rect[1].GetInt32(), rect[2].GetInt32(), rect[3].GetInt32()));
                if (ro) Rejected(name + " GUI readonly", () => pump(panel.DeleteRectangleAsync())); else pump(panel.DeleteRectangleAsync());
            }
            State(name + " kernel", panel, wanted, payload);
            check(name + " history", panel.HistoryCount == expected.GetProperty("historyPushes").GetInt32(), "original kernel history count");
            pump(panel.UndoEditAsync()); State(name + " undo", panel, before, payload); pump(panel.RedoEditAsync()); State(name + " redo", panel, wanted, payload);
            originalCount++;
        }
        check("rectangle GUI original pixel scope", originalCount == 20, "12 explicitly excluded empty/invalid-pane/read-only-paste cases");

        var raw = new ImageComparisonEngine.DecodedFrame(1, 4, 3, Enumerable.Range(0, 48).Select(i => (byte)(17 + i * 37)).ToArray());
        var piece = new ImageComparisonEngine.DecodedFrame(1, 2, 2, new byte[] { 19, 41, 67, 0, 173, 11, 239, 0, 7, 21, 45, 128, 255, 128, 32, 255 });
        var basic = Open("interaction", [raw, raw]);
        using var platform = new ClipboardStub(); basic.ClipboardOverride = ImageClipboard.ForPlatform(() => platform);
        basic.SelectRectangle(0, new(2, 1, 4, 3));
        var crop = new byte[16]; for (var y = 0; y < 2; y++) raw.Pixels.AsSpan(((y + 1) * 4 + 2) * 4, 8).CopyTo(crop.AsSpan(y * 8));
        var copyButton = Button(basic, "ImageCopyRectangle"); check("rectangle GUI real copy button", copyButton.IsEnabled, "");
        copyButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); pump(basic.CurrentRectangleOperation);
        var owned = ReadClipboard(platform);
        check("rectangle GUI half-open crop/custom transfer", owned.Width == 2 && owned.Height == 2 && owned.Pixels.SequenceEqual(crop)
            && platform.Flushed == 1 && platform.BorrowedDisposed > 0, "actual production clipboard bridge on injected platform; transfer lifetime and flush");
        check("rectangle GUI standard bitmap included", platform.ContainsBitmap, "same transfer advertises bitmap and owned raw BGRA");
        check("rectangle GUI copy creates no history", basic.HistoryCount == 0, "");
        platform.FailSet = true;
        Rejected("rectangle GUI Cut set failure", () => pump(basic.CutRectangleAsync())); State("cut-set-failure", basic, raw, raw);
        platform.FailSet = false; platform.FailFlush = true;
        Rejected("rectangle GUI Cut flush failure", () => pump(basic.CutRectangleAsync())); State("cut-flush-failure", basic, raw, raw);
        check("rectangle GUI failed Cut keeps selection/history", basic.RectangleSelection(0) is not null && basic.HistoryCount == 0, "clipboard write/flush failure never deletes pixels");
        platform.FailFlush = false; pump(basic.CutRectangleAsync());
        var cut = (byte[])raw.Pixels.Clone(); for (var y = 1; y < 3; y++) cut.AsSpan((y * 4 + 2) * 4, 8).Clear();
        State("cut-success", basic, new(1, 4, 3, cut), raw); check("rectangle GUI Cut clears rectangle", basic.RectangleSelection(0) is null && basic.HistoryCount == 1, "");
        pump(basic.UndoEditAsync()); State("cut-undo", basic, raw, raw);
        platform.Seed(piece); pump(basic.PasteClipboardAsync());
        check("rectangle GUI floating protected save/report", basic.HasFloatingImage, "");
        Rejected("rectangle GUI floating save guard", () => pump(basic.SaveToAsync(0, Path.Combine(folder, "unconfirmed.png"))));
        Rejected("rectangle GUI floating report guard", () => basic.CaptureReport());
        screenshot("image-rectangle-floating.png");
        var surface = Surface(basic, 0); surface.Focus(); window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Dispatcher.UIThread.RunJobs();
        check("rectangle GUI Escape cancels floating", !basic.HasFloatingImage && basic.HistoryIndex == -1, "same-size no resize; original pixels remain");
        State("escape", basic, raw, raw);
        pump(basic.BeginFloatingAsync(0, piece)); basic.MoveFloating(2, 1); surface.Focus();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); pump(basic.CurrentRectangleOperation);
        var pasted = Paste(raw, piece, 2, 1); State("enter", basic, pasted, raw);
        check("rectangle GUI Enter confirms", !basic.HasFloatingImage && basic.HistoryIndex == 0, "");
        screenshot("image-rectangle-confirmed.png");

        // 実ポインタイベントで倍率、端点、Ctrl-stamp後の再captureを検証する。
        basic = Open("pointer", [raw, raw]); surface = Surface(basic, 0); Render();
        var actualImage = basic.GetVisualDescendants().OfType<Avalonia.Controls.Image>().Single(control => control.Name == "ImagePaneLeft");
        check("rectangle GUI image and pointer share origin", actualImage.Bounds.Position == new Point(0, 0) && actualImage.Bounds.Width == raw.Width * 8 && actualImage.Bounds.Height == raw.Height * 8, "resize margin must not center fixed-size original bitmap");
        var start = PointOn(surface, .25, .25); var end = PointOn(surface, 3, 2);
        window.MouseDown(start, MouseButton.Left); window.MouseUp(start, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        check("rectangle GUI zero-distance click clears selection", basic.RectangleSelection(0) is null && basic.HistoryCount == 0, "GUI click clearing does not change core empty-rectangle history contract");
        window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        check("rectangle GUI pointer half-open selection", basic.RectangleSelection(0) == new ImageRectangle(0, 0, 3, 2), "8x zoom actual pointer drag");
        pump(basic.BeginFloatingAsync(0, piece)); Render();
        start = PointOn(surface, .25, .25); end = PointOn(surface, 1.25, 1.25);
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.Control); pump(basic.CurrentRectangleOperation);
        window.MouseMove(end, RawInputModifiers.Control | RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs();
        check("rectangle GUI Ctrl-stamp continues drag", basic.HasFloatingImage && basic.FloatingPosition == (0, 1, 1) && basic.HistoryCount == 1, "capture restored after committing stamp");
        pump(basic.CommitFloatingAsync()); State("ctrl-stamp", basic, Paste(Paste(raw, piece, 0, 0), piece, 1, 1), raw);
        pump(basic.BeginFloatingAsync(0, piece)); basic.MoveFloating(0, 0); Render();
        var outside = PointOn(surface, 3.5, 2.5); window.MouseDown(outside, MouseButton.Left); pump(basic.CurrentRectangleOperation); window.MouseUp(outside, MouseButton.Left);
        check("rectangle GUI outside click confirms", !basic.HasFloatingImage, "");
        pump(basic.BeginFloatingAsync(0, piece)); Surface(basic, 1).Focus(); Dispatcher.UIThread.RunJobs(); pump(basic.CurrentRectangleOperation);
        check("rectangle GUI pane focus loss confirms", !basic.HasFloatingImage, "");

        foreach (var copy in new[] { false, true })
        {
            basic = Open(copy ? "selection-copy-drag" : "selection-move-drag", [raw, raw]); surface = Surface(basic, 0); Render();
            basic.SelectRectangle(0, new(0, 0, 2, 2));
            var modifiers = copy ? RawInputModifiers.Control : RawInputModifiers.None;
            start = PointOn(surface, .25, .25); var firstMove = PointOn(surface, .75, .75); end = PointOn(surface, 2.25, 1.25);
            window.MouseDown(start, MouseButton.Left, modifiers); window.MouseMove(firstMove, modifiers | RawInputModifiers.LeftMouseButton); pump(basic.CurrentRectangleOperation);
            var cropped = new byte[16]; for (var y = 0; y < 2; y++) raw.Pixels.AsSpan(y * 16, 8).CopyTo(cropped.AsSpan(y * 8));
            var movedPayload = new ImageComparisonEngine.DecodedFrame(1, 2, 2, cropped);
            var removed = (byte[])raw.Pixels.Clone(); if (!copy) for (var y = 0; y < 2; y++) removed.AsSpan(y * 16, 8).Clear();
            check("rectangle GUI selection drag initial " + copy, basic.HasFloatingImage && basic.FloatingPosition == (0, 0, 0) && basic.HistoryCount == (copy ? 0 : 1), "initial move crops at selection origin; Ctrl retains source; no Resize");
            window.MouseMove(end, modifiers | RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left, modifiers); Dispatcher.UIThread.RunJobs();
            check("rectangle GUI selection drag subsequent movement " + copy, basic.FloatingPosition == (0, 2, 1), "cursor baseline is original first MouseMove");
            pump(basic.CommitFloatingAsync()); State("selection-drag " + copy, basic, Paste(new(1, 4, 3, removed), movedPayload, 2, 1), raw);
            pump(basic.UndoEditAsync()); State("selection-drag undo-paste " + copy, basic, new(1, 4, 3, removed), raw);
            if (!copy) { pump(basic.UndoEditAsync()); State("selection-drag undo-delete", basic, raw, raw); }
        }

        basic = Open("keyboard-pane", [raw, raw, raw], readOnly: true); using var keyboardClipboard = new ClipboardStub(); keyboardClipboard.Seed(piece);
        basic.ClipboardOverride = ImageClipboard.ForPlatform(() => keyboardClipboard); Surface(basic, 2).Focus();
        KeyStroke(Key.V, PhysicalKey.V, RawInputModifiers.Control); pump(basic.CurrentRectangleOperation);
        check("rectangle GUI focused right CtrlV targets right", basic.FloatingPosition.Pane == 2 && basic.HasFloatingImage && basic.HistoryCount == 0, "readonly left must not intercept focused right");
        KeyStroke(Key.Enter, PhysicalKey.Enter); pump(basic.CurrentRectangleOperation);
        check("rectangle GUI focused right Enter raw panes", Same(basic.CaptureEditFrames()[0], raw) && Same(basic.CaptureEditFrames()[1], raw) && Same(basic.CaptureEditFrames()[2], Paste(raw, piece, 0, 0)), "all three panes; paste follows keyboard focus");
        pump(basic.UndoEditAsync()); basic.SelectRectangle(1, new(0, 0, 2, 2)); basic.SelectRectangle(2, new(2, 1, 4, 3)); Surface(basic, 1).Focus();
        KeyStroke(Key.Delete, PhysicalKey.Delete); pump(basic.CurrentRectangleOperation);
        var deletedMiddle = (byte[])raw.Pixels.Clone(); for (var y = 0; y < 2; y++) deletedMiddle.AsSpan(y * 16, 8).Clear();
        check("rectangle GUI focused middle Delete targets middle", Same(basic.CaptureEditFrames()[0], raw) && Same(basic.CaptureEditFrames()[1], new(1, 4, 3, deletedMiddle)) && Same(basic.CaptureEditFrames()[2], raw), "focus differs from last selected pane");
        pump(basic.UndoEditAsync()); basic.SelectRectangle(2, new(0, 0, 2, 2)); basic.SelectRectangle(1, new(2, 1, 4, 3)); Surface(basic, 2).Focus();
        KeyStroke(Key.X, PhysicalKey.X, RawInputModifiers.Control); pump(basic.CurrentRectangleOperation);
        check("rectangle GUI focused right Cut targets right", Same(basic.CaptureEditFrames()[0], raw) && Same(basic.CaptureEditFrames()[1], raw) && Same(basic.CaptureEditFrames()[2], new(1, 4, 3, deletedMiddle)), "clipboard success then right Delete");
        Surface(basic, 0).Focus(); KeyStroke(Key.V, PhysicalKey.V, RawInputModifiers.Control);
        Rejected("rectangle GUI focused readonly CtrlV", () => pump(basic.CurrentRectangleOperation));
        check("rectangle GUI readonly keyboard keeps all panes", !basic.HasFloatingImage && Same(basic.CaptureEditFrames()[0], raw), "no resize or paste at readonly focus");

        var enlarged = new ImageComparisonEngine.DecodedFrame(1, 5, 4, new byte[80]);
        basic = Open("resize-cancel", [raw, raw]); pump(basic.BeginFloatingAsync(0, enlarged));
        check("rectangle GUI paste resize distinct history", basic.HistoryCount == 1 && basic.HasFloatingImage, "max oriented dimensions resize happens before confirmation");
        basic.CancelRectangleInteraction(); check("rectangle GUI cancel retains resize", basic.HistoryCount == 1 && basic.PaneModified(0), "Escape does not Undo original resize");
        var extended = new byte[80]; for (var y = 0; y < 3; y++) raw.Pixels.AsSpan(y * 16, 16).CopyTo(extended.AsSpan(y * 20));
        State("resize-cancel", basic, new(1, 5, 4, extended), raw); pump(basic.UndoEditAsync()); State("resize-cancel-undo", basic, raw, raw);
        basic = Open("resize-confirm", [raw, raw]); pump(basic.BeginFloatingAsync(0, enlarged)); pump(basic.CommitFloatingAsync());
        check("rectangle GUI resize/paste two histories", basic.HistoryCount == 2, ""); pump(basic.UndoEditAsync()); State("undo-paste-retains-resize", basic, new(1, 5, 4, extended), raw);
        pump(basic.UndoEditAsync()); State("undo-resize", basic, raw, raw);
        basic = Open("pending-resize-Escape", [raw, raw]); Render(); Surface(basic, 0).Focus();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        basic.EditCandidateReady = () => { entered.TrySetResult(); return release.Task; };
        var pendingFloating = basic.BeginFloatingAsync(0, enlarged); pump(entered.Task);
        check("rectangle GUI pending Resize entered candidate boundary", !pendingFloating.IsCompleted && Surface(basic, 0).IsKeyboardFocusWithin, "computed candidate waits before adoption; no timing/large image dependency");
        KeyStroke(Key.Escape, PhysicalKey.Escape); release.SetResult();
        Rejected("rectangle GUI Escape during async Resize", () => pump(pendingFloating));
        basic.EditCandidateReady = null;
        check("rectangle GUI Escape cannot resurrect floating", !basic.HasFloatingImage && basic.HistoryCount == 1, "completed original Resize retains independent history");
        State("pending-resize-Escape", basic, new(1, 5, 4, extended), raw);
        foreach (var stamp in new[] { true, false })
        {
            basic = Open(stamp ? "async-stamp-release" : "async-selection-release", [raw, raw]); surface = Surface(basic, 0); Render();
            if (stamp) pump(basic.BeginFloatingAsync(0, piece)); else basic.SelectRectangle(0, new(0, 0, 2, 2));
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            basic.EditCandidateReady = () => { entered.TrySetResult(); return release.Task; };
            start = PointOn(surface, .25, .25); var modifiers = stamp ? RawInputModifiers.Control : RawInputModifiers.None;
            window.MouseDown(start, MouseButton.Left, modifiers);
            if (!stamp) window.MouseMove(PointOn(surface, .75, .75), RawInputModifiers.LeftMouseButton);
            pump(entered.Task); check("rectangle GUI async drag candidate pending " + stamp, !basic.CurrentRectangleOperation.IsCompleted, "actual pointer operation blocked at adoption boundary");
            check("rectangle GUI async drag keeps actual capture " + stamp, basic.HasRectanglePointerCapture, "capture survives candidate await so OS reports outside-window release");
            // 通常moveはwindow外でreleaseし、実captureの経路で受ける。
            var releasePoint = stamp ? start : new Point(-100, -100);
            window.MouseUp(releasePoint, MouseButton.Left, modifiers); release.SetResult(); pump(basic.CurrentRectangleOperation); Dispatcher.UIThread.RunJobs();
            basic.EditCandidateReady = null; var position = basic.FloatingPosition;
            window.MouseMove(PointOn(surface, 2.25, 1.25)); Dispatcher.UIThread.RunJobs();
            check("rectangle GUI release during async drag never recaptures " + stamp, basic.HasFloatingImage && basic.FloatingPosition == position, "buttonless movement after awaited stamp/delete cannot move floating payload");
            basic.CancelRectangleInteraction();
        }

        foreach (var mode in new[] { 1, 2, 3 })
        {
            basic = Open("manual-resize-" + mode, [raw, raw]); surface = Surface(basic, 0); Render();
            start = PointOn(surface, mode == 2 ? 1.25 : 4.25, mode == 1 ? 1.25 : 3.25);
            end = PointOn(surface, mode == 2 ? 1.25 : 6.25, mode == 1 ? 1.25 : 5.25);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton); Dispatcher.UIThread.RunJobs();
            check("rectangle GUI manual Resize preview " + mode, basic.HistoryCount == 0 && Same(basic.CaptureEditFrames()[0], raw) && basic.RectangleSelection(0) is not null, "preview does not edit pixels");
            screenshot("image-rectangle-resize-preview-" + mode + ".png");
            window.MouseUp(end, MouseButton.Left); pump(basic.CurrentRectangleOperation);
            var width = mode == 2 ? 4 : 6; var height = mode == 1 ? 3 : 5; var wanted = new byte[width * height * 4];
            for (var y = 0; y < 3; y++) raw.Pixels.AsSpan(y * 16, 16).CopyTo(wanted.AsSpan(y * width * 4));
            State("manual-resize " + mode, basic, new(1, width, height, wanted), raw);
            check("rectangle GUI manual Resize single history " + mode, basic.HistoryCount == 1 && basic.RectangleSelection(0) is null, "right/bottom/corner handles original commit");
            pump(basic.UndoEditAsync()); State("manual-resize undo " + mode, basic, raw, raw); pump(basic.RedoEditAsync()); State("manual-resize redo " + mode, basic, new(1, width, height, wanted), raw);
        }
        basic = Open("manual-resize-readonly", [raw, raw]); surface = Surface(basic, 0); Render(); start = PointOn(surface, 4.25, 3.25); end = PointOn(surface, 6.25, 5.25);
        window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton); basic.SetReadOnly([true, false]); window.MouseUp(end, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        check("rectangle GUI Resize readonly change cancels preview", basic.HistoryCount == 0 && basic.RectangleSelection(0) is null && Same(basic.CaptureEditFrames()[0], raw), "no history/pixel adoption");
        basic.SetReadOnly([false, false]); Render(); window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton); KeyStroke(Key.Escape, PhysicalKey.Escape); window.MouseUp(end, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        check("rectangle GUI Resize Escape cancels preview", basic.HistoryCount == 0 && basic.RectangleSelection(0) is null && Same(basic.CaptureEditFrames()[0], raw), "mouse release after Escape cannot commit");
        Render(); window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton); pump(basic.SetOrientationAsync(0, new() { Rotation = 90 })); window.MouseUp(end, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        check("rectangle GUI Resize stale orientation cancels preview", basic.HistoryCount == 0 && basic.RectangleSelection(0) is null && Same(basic.CaptureEditFrames()[0], raw), "old resize coordinates rejected after generation change");
        var rotateCase = resizeCases.RootElement.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "r90-x0-y0-4x5");
        var rotateInput = Frame(rotateCase.GetProperty("target"));
        basic = Open("manual-resize-rotated", [rotateInput, rotateInput], new() { LeftOrientation = new() { Rotation = 90 }, Zoom = 8, BlockSize = 1, ShowDifferences = false }); surface = Surface(basic, 0); Render();
        start = PointOn(surface, 2.25, 3.25); end = PointOn(surface, 4.25, 5.25); window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left); pump(basic.CurrentRectangleOperation);
        var rotateExpected = resizeGolden.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetString() == "r90-x0-y0-4x5");
        var rotateWanted = new ImageComparisonEngine.DecodedFrame(1, rotateExpected.GetProperty("width").GetInt32(), rotateExpected.GetProperty("height").GetInt32(), Convert.FromHexString(rotateExpected.GetProperty("bgraHex").GetString()!));
        State("manual-resize rotated original", basic, rotateWanted, rotateInput);
        check("rectangle GUI rotated Resize follows original", basic.HistoryCount == 1 && basic.CaptureSettings().LeftOrientation.Rotation == 90, "raw-oldbitmap copy order matches independently captured original kernel");
        pump(basic.UndoEditAsync()); State("manual-resize rotated undo", basic, rotateInput, rotateInput);
        basic.SelectRectangle(0, new(0, 0, 2, 2)); surface.Focus(); Render(); start = PointOn(surface, .25, .25); end = PointOn(surface, .75, .75);
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.Control); window.MouseMove(end, RawInputModifiers.Control | RawInputModifiers.LeftMouseButton); pump(basic.CurrentRectangleOperation); window.MouseUp(end, MouseButton.Left, RawInputModifiers.Control);
        check("rectangle GUI rotated selection drag has no Resize", basic.HasFloatingImage && basic.HistoryIndex == -1 && Same(basic.CaptureEditFrames()[0], rotateInput), "selection move never uses clipboard pre-Resize");
        basic.CancelRectangleInteraction();

        basic = Open("async-clipboard", [raw, raw]); using var delayed = new ClipboardStub(); basic.ClipboardOverride = ImageClipboard.ForPlatform(() => delayed);
        basic.SelectRectangle(0, new(0, 0, 4, 3)); delayed.SetGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingCut = basic.CutRectangleAsync(); pump(basic.SetOrientationAsync(0, new() { Rotation = 90 })); delayed.SetGate.SetResult();
        Rejected("rectangle GUI old Cut completion", () => pump(pendingCut)); State("old-cut", basic, raw, raw);
        check("rectangle GUI old Cut no history", basic.HistoryCount == 0, "orientation changed while clipboard write pending");
        delayed.SetGate = null; delayed.Seed(piece); delayed.ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingPaste = basic.PasteClipboardAsync(); basic.SetReadOnly([true, false]); delayed.ReadGate.SetResult();
        Rejected("rectangle GUI pending Paste becomes readonly", () => pump(pendingPaste));
        check("rectangle GUI no stale/readonly floating", !basic.HasFloatingImage && basic.HistoryCount == 0, "");
        basic.SetReadOnly([false, false]); delayed.ReadGate = null;
        basic.SelectRectangle(0, new(0, 0, 3, 4));
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = basic.DeleteRectangleAsync(cancellation.Token); cancellation.Cancel(); Rejected("rectangle GUI cancellation transaction", () => pump(pending));
        }
        State("cancelled-delete", basic, raw, raw);
        basic.SelectRectangle(0, new(0, 0, 3, 4)); var readonlyEdit = basic.DeleteRectangleAsync(); basic.SetReadOnly([true, false]);
        Rejected("rectangle GUI readonly before adoption", () => pump(readonlyEdit)); State("readonly-before-adopt", basic, raw, raw);
        basic.SetReadOnly([false, false]);

        var rowA = new byte[] { 0, 0, 255, 255 }; var rowB = new byte[] { 0, 255, 0, 255 }; var rowY = new byte[] { 255, 0, 0, 255 };
        var shortFrame = new ImageComparisonEngine.DecodedFrame(1, 1, 2, [.. rowA, .. rowB]);
        var longFrame = new ImageComparisonEngine.DecodedFrame(1, 1, 3, [.. rowY, .. rowA, .. rowB]);
        basic = Open("offset-ghost", [shortFrame, longFrame], new() { BlockSize = 1, InsertionDeletionMode = 1, LeftOffset = new(5, 7), Zoom = 8, ShowDifferences = false });
        basic.SelectRectangle(0, new(5, 8, 6, 10));
        check("rectangle GUI offset/ghost half-open inverse", basic.SelectedRawRectangle(0) == new ImageRectangle(0, 0, 1, 2), "leading inserted row excluded; full last raw row retained");
        using var ghostClipboard = new ClipboardStub(); basic.ClipboardOverride = ImageClipboard.ForPlatform(() => ghostClipboard); pump(basic.CopyRectangleAsync());
        check("rectangle GUI ghost never copied", Same(ReadClipboard(ghostClipboard), shortFrame), "actual crop clipboard all raw BGRA");
        basic.SelectAllRectangle(0); Rejected("rectangle GUI invalid leading ghost boundary", () => pump(basic.CopyRectangleAsync()));
        basic.CancelRectangleInteraction(); surface = Surface(basic, 0); Render(); start = PointOn(surface, 6.25, 8.25); end = PointOn(surface, 7.25, 8.25);
        window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left); pump(basic.CurrentRectangleOperation);
        State("manual-resize ghost-offset", basic, new(1, 2, 2, [.. rowA, 0, 0, 0, 0, .. rowB, 0, 0, 0, 0]), longFrame);
        check("rectangle GUI Resize ghost/offset dimensions", basic.CaptureSettings().LeftOffset == new ImageOffset(5, 7) && basic.HistoryCount == 1, "preprocessed edge plus delta, raw oriented dimensions plus delta");

        basic = Open("history-limit", [new(1, 1, 1, rowA), new(1, 1, 1, rowB)]);
        for (var i = 0; i < 128; i++) { basic.SelectRectangle(0, new(0, 0, 0, 0)); pump(basic.DeleteRectangleAsync()); }
        basic.SelectRectangle(0, new(0, 0, 0, 0)); Rejected("rectangle GUI history 129 rejected", () => pump(basic.DeleteRectangleAsync()));
        check("rectangle GUI 128 history preserved", basic.HistoryCount == 128 && basic.HistoryIndex == 127, "valid empty rectangles still count");
        var malformed = ImageClipboard.Encode(piece); BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(8), int.MaxValue);
        Rejected("rectangle GUI custom huge dimensions", () => ImageClipboard.Decode(malformed));
        Rejected("rectangle GUI custom truncated bytes", () => ImageClipboard.Decode(ImageClipboard.Encode(piece)[..^1]));
        Rejected("rectangle GUI custom unknown version", () => { var encoded = ImageClipboard.Encode(piece); encoded[4] = 2; ImageClipboard.Decode(encoded); });
        foreach (var bottomUp in new[] { true, false })
        {
            var dib = IndependentDib(piece, bottomUp); var decoded = ImageClipboard.DecodeDib(dib);
            check("rectangle clipboard raw DIB " + (bottomUp ? "bottom-up" : "top-down"), decoded is not null && Same(decoded, piece), "alpha and hidden RGB retained; independently constructed header/rows");
        }
        Rejected("rectangle clipboard DIB truncated", () => ImageClipboard.DecodeDib(IndependentDib(piece, true)[..^1]));
        Rejected("rectangle clipboard DIB height overflow", () => { var dib = IndependentDib(piece, true); BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), int.MinValue); ImageClipboard.DecodeDib(dib); });
        Rejected("rectangle clipboard DIB planes invalid", () => { var dib = IndependentDib(piece, true); dib[12] = 2; ImageClipboard.DecodeDib(dib); });
        Rejected("rectangle clipboard DIB declared size invalid", () => { var dib = IndependentDib(piece, true); dib[20] = 1; ImageClipboard.DecodeDib(dib); });
        var otherDib = IndependentDib(piece, true); otherDib[14] = 24;
        check("rectangle clipboard other DIB falls back", ImageClipboard.DecodeDib(otherDib) is null, "only verified 32bit BI_RGB uses raw reader");
        NativeClipboardChecks();
        check("rectangle GUI inputs unchanged", Directory.EnumerateFiles(folder, "*-input-*.png").All(path => inputHashes[path] == Hash(File.ReadAllBytes(path))), "all original PNG SHA retained");
        screenshot("image-rectangle-selection.png");
        writer.Flush();
        File.WriteAllText(Path.Combine(folder, "scope.json"), Json(json =>
        {
            json.WriteNumber("resizeCases", resized); json.WriteNumber("rectangleCases", originalCount);
            json.WriteString("clipboard", "production Avalonia bridge with injected platform; native HGLOBAL publication with injected API; no OS clipboard mutation");
            json.WriteString("originalGui", "static source flow only; resize kernel and rectangle kernel independently captured C++ adapters");
            json.WriteString("nativeDialog", "not measured"); json.WriteString("desktopFocusCursorClipboard", "not measured");
        }));

        SpecializedViews.ImagePanel Open(string name, ImageComparisonEngine.DecodedFrame[] frames, ImageViewSettings? settings = null, bool readOnly = false)
        {
            var paths = frames.Select((frame, index) => Path.Combine(folder, name + "-input-" + index + ".png")).ToArray();
            for (var i = 0; i < frames.Length; i++) { WritePng(paths[i], frames[i]); inputHashes[paths[i]] = Hash(File.ReadAllBytes(paths[i])); }
            pane.DiscardChanges(); pane.ApplyProject(new() { Mode = "Image", LeftPath = paths[0], BasePath = frames.Length == 3 ? paths[1] : "", RightPath = paths[^1],
                LeftReadOnly = readOnly, ImageSettings = settings ?? new() { BlockSize = 1, Zoom = 8, ShowDifferences = false } });
            pump(pane.ComparePathsAsync()); var panel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
            return panel;
        }
        void State(string name, SpecializedViews.ImagePanel panel, ImageComparisonEngine.DecodedFrame first, ImageComparisonEngine.DecodedFrame second)
        {
            var actual = panel.CaptureEditFrames(); check(name + " all pane raw BGRA", Same(actual[0], first) && Same(actual[1], second), "all BGRA/dimensions; other pane unchanged");
            writer.WriteLine(Json(json =>
            {
                json.WriteString("name", name); json.WriteNumber("historyCount", panel.HistoryCount); json.WriteNumber("historyIndex", panel.HistoryIndex); json.WriteBoolean("hasFloatingImage", panel.HasFloatingImage);
                json.WriteStartArray("frames"); foreach (var frame in actual)
                { json.WriteStartObject(); json.WriteNumber("width", frame.Width); json.WriteNumber("height", frame.Height); json.WriteString("bgraHex", Convert.ToHexString(frame.Pixels)); json.WriteString("sha256", Hash(frame.Pixels)); json.WriteEndObject(); }
                json.WriteEndArray();
            })); writer.Flush();
        }
        void Rejected(string name, Action operation)
        {
            var rejected = false; try { operation(); } catch (Exception error) when (error is InvalidOperationException or ArgumentException or InvalidDataException or OperationCanceledException)
            { rejected = true; writer.WriteLine(Json(json => { json.WriteString("name", name); json.WriteString("rejected", error.Message); })); }
            check(name, rejected, "must fail without partial pixels/history");
        }
        ImageComparisonEngine.DecodedFrame ReadClipboard(ClipboardStub clipboard)
        { ImageComparisonEngine.DecodedFrame? frame = null; pump(Read()); return frame!; async Task Read() => frame = await ImageClipboard.ForPlatform(() => clipboard).ReadAsync(default); }
        Point PointOn(Control control, double x, double y) => control.TranslatePoint(new(x * 8, y * 8), window) ?? throw new InvalidOperationException("rectangle pointer surface");
        void Render() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        void KeyStroke(Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
        { window.KeyPress(key, modifiers, physical, null); window.KeyRelease(key, modifiers, physical, null); Dispatcher.UIThread.RunJobs(); }
        void NativeClipboardChecks()
        {
            var api = new NativeClipboardStub(); WindowsImageClipboard.Write(piece, new IntPtr(123), api);
            check("rectangle native clipboard allocation before Empty", api.Calls.IndexOf("allocate2") < api.Calls.IndexOf("empty") && api.Owner == new IntPtr(123), "all payload prepared with real non-null owner contract");
            check("rectangle native clipboard format/header/rows", api.FormatName == "avn-app-fmt:com.kagayoi.diffbeacon.bgra.v1"
                && api.Published[8].SequenceEqual(IndependentDib(piece, true)) && Same(ImageClipboard.Decode(api.Published[49152]), piece), "bottom-up original CF_DIB32 plus Avalonia-compatible raw custom format");
            check("rectangle native clipboard OS owns successful handles", api.Freed.Count == 0 && api.Published.Count == 2 && api.Calls.Last() == "close", "caller never frees published HGLOBAL");
            foreach (var failure in new[] { "register", "allocate1", "allocate2", "open", "empty", "set1", "set2", "close" })
            {
                api = new() { Failure = failure };
                Rejected("rectangle native clipboard " + failure + " failure", () => WindowsImageClipboard.Write(piece, new IntPtr(123), api));
                check("rectangle native clipboard " + failure + " ownership", api.Freed.Count + api.Published.Count == api.Allocations.Count
                    && api.Freed.All(handle => !api.PublishedHandles.Contains(handle)), "all allocated handles either OS-owned or freed exactly once");
                if (failure is "register" or "allocate1" or "allocate2" or "open")
                    check("rectangle native clipboard " + failure + " keeps prior data", !api.Calls.Contains("empty"), "no EmptyClipboard before complete preparation/open");
                if (failure == "close") check("rectangle native clipboard retries failed close", api.Calls.Count(value => value == "close") == 2, "finally retains Open ownership until successful Close");
            }
            Rejected("rectangle native clipboard rejects null owner", () => WindowsImageClipboard.Write(piece, IntPtr.Zero, new NativeClipboardStub()));
            using var cancelled = new CancellationTokenSource(); api = new() { AfterAllocation = count => { if (count == 2) cancelled.Cancel(); } };
            Rejected("rectangle native clipboard cancellation before Empty", () => WindowsImageClipboard.Write(piece, new IntPtr(123), api, cancelled.Token));
            check("rectangle native clipboard cancelled preparation cleanup", api.Freed.Count == 2 && !api.Calls.Contains("empty"), "prior clipboard retained");
            using var late = new CancellationTokenSource(); api = new() { AfterSet = count => { if (count == 1) late.Cancel(); } };
            Rejected("rectangle native clipboard cancellation after publication", () => WindowsImageClipboard.Write(piece, new IntPtr(123), api, late.Token));
            check("rectangle native clipboard late cancel completes ownership", api.Published.Count == 2 && api.Freed.Count == 0 && api.Calls.Last() == "close", "write rejection prevents Cut while both formats remain OS-owned");
        }
    }

    private static Grid Surface(SpecializedViews.ImagePanel panel, int pane) => panel.GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "ImageRectangleSurface" + pane);
    private static Button Button(SpecializedViews.ImagePanel panel, string name) => panel.GetVisualDescendants().OfType<Button>().Single(control => control.Name == name);
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static string Json(Action<Utf8JsonWriter> body)
    {
        using var bytes = new MemoryStream(); using (var json = new Utf8JsonWriter(bytes)) { json.WriteStartObject(); body(json); json.WriteEndObject(); }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
    private static bool Same(ImageComparisonEngine.DecodedFrame a, ImageComparisonEngine.DecodedFrame b) => a.Width == b.Width && a.Height == b.Height && a.Pixels.SequenceEqual(b.Pixels);
    private static ImageComparisonEngine.DecodedFrame Frame(JsonElement value) => new(1, value.GetProperty("width").GetInt32(), value.GetProperty("height").GetInt32(), value.GetProperty("bgra").EnumerateArray().Select(part => part.GetByte()).ToArray());
    private static byte[] Resource(string name, bool packed)
    {
        using var source = typeof(HeadlessImageRectangleChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest." + name) ?? throw new InvalidDataException(name);
        using var bytes = new MemoryStream(); if (packed) { using var gzip = new GZipStream(source, CompressionMode.Decompress); gzip.CopyTo(bytes); } else source.CopyTo(bytes);
        return bytes.ToArray();
    }
    private static ImageComparisonEngine.DecodedFrame Paste(ImageComparisonEngine.DecodedFrame target, ImageComparisonEngine.DecodedFrame source, int x, int y)
    {
        var pixels = (byte[])target.Pixels.Clone();
        for (var sy = 0; sy < source.Height; sy++) for (var sx = 0; sx < source.Width; sx++)
            if (sx + x >= 0 && sx + x < target.Width && sy + y >= 0 && sy + y < target.Height)
                source.Pixels.AsSpan((sy * source.Width + sx) * 4, 4).CopyTo(pixels.AsSpan(((sy + y) * target.Width + sx + x) * 4));
        return new(1, target.Width, target.Height, pixels);
    }
    private static byte[] IndependentDib(ImageComparisonEngine.DecodedFrame frame, bool bottomUp)
    {
        var bytes = new byte[40 + frame.Pixels.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 40); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), frame.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), bottomUp ? frame.Height : -frame.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), frame.Pixels.Length);
        for (var y = 0; y < frame.Height; y++) for (var x = 0; x < frame.Width * 4; x++)
            bytes[40 + y * frame.Width * 4 + x] = frame.Pixels[((bottomUp ? frame.Height - 1 - y : y) * frame.Width * 4) + x];
        return bytes;
    }
    private static void WritePng(string path, ImageComparisonEngine.DecodedFrame frame)
    {
        using var file = File.Create(path); file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, frame.Width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), frame.Height); header[8] = 8; header[9] = 6;
        Chunk("IHDR", header); using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
            for (var y = 0; y < frame.Height; y++)
            { var row = new byte[frame.Width * 4 + 1]; for (var x = 0; x < frame.Width; x++) { var at = (y * frame.Width + x) * 4; var to = x * 4 + 1; row[to] = frame.Pixels[at + 2]; row[to + 1] = frame.Pixels[at + 1]; row[to + 2] = frame.Pixels[at]; row[to + 3] = frame.Pixels[at + 3]; } zlib.Write(row); }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        void Chunk(string kind, byte[] data)
        { var type = System.Text.Encoding.ASCII.GetBytes(kind); Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, data.Length); file.Write(number); file.Write(type); file.Write(data); uint crc = 0xffffffff;
            foreach (var value in type.Concat(data)) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320 : 0); } BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); file.Write(number); }
    }

    private sealed class NativeClipboardStub : IWindowsImageClipboardApi
    {
        internal string? Failure, FormatName;
        internal IntPtr Owner;
        internal Action<int>? AfterAllocation, AfterSet;
        internal readonly List<string> Calls = [];
        internal readonly Dictionary<IntPtr, byte[]> Allocations = [];
        internal readonly Dictionary<uint, byte[]> Published = [];
        internal readonly HashSet<IntPtr> PublishedHandles = [], Freed = [];
        private int allocationAttempts, setAttempts;
        public uint RegisterFormat(string name) { Calls.Add("register"); FormatName = name; return Failure == "register" ? 0u : 49152u; }
        public IntPtr Allocate(byte[] bytes)
        {
            Calls.Add("allocate" + ++allocationAttempts);
            if (Failure == "allocate" + allocationAttempts) throw new InvalidOperationException("injected GlobalAlloc failure");
            var handle = new IntPtr(allocationAttempts); Allocations.Add(handle, (byte[])bytes.Clone()); AfterAllocation?.Invoke(allocationAttempts); return handle;
        }
        public void Free(IntPtr memory) { Calls.Add("free"); if (!Freed.Add(memory)) throw new InvalidOperationException("duplicate GlobalFree"); }
        public bool Open(IntPtr owner) { Calls.Add("open"); Owner = owner; return Failure != "open"; }
        public bool Empty() { Calls.Add("empty"); return Failure != "empty"; }
        public bool Set(uint format, IntPtr memory)
        {
            Calls.Add("set" + ++setAttempts); if (Failure == "set" + setAttempts) return false;
            Published.Add(format, Allocations[memory]); PublishedHandles.Add(memory); AfterSet?.Invoke(setAttempts); return true;
        }
        public bool Close() { Calls.Add("close"); return Failure != "close"; }
    }

    private sealed class ClipboardStub : IImageClipboardPlatform, IDisposable
    {
        private IAsyncDataTransfer? stored;
        internal bool FailSet, FailFlush;
        internal int Flushed, BorrowedDisposed;
        internal TaskCompletionSource? SetGate, ReadGate;
        internal bool ContainsBitmap => stored?.Formats.Contains(DataFormat.Bitmap) == true;
        internal void Seed(ImageComparisonEngine.DecodedFrame frame) { stored?.Dispose(); stored = ImageClipboard.CreateTransfer(frame); }
        public async Task SetDataAsync(IAsyncDataTransfer? transfer)
        {
            if (FailSet) throw new InvalidOperationException("injected clipboard SetData failure");
            stored?.Dispose(); stored = transfer;
            if (SetGate is not null) await SetGate.Task;
        }
        public Task FlushAsync() { if (FailFlush) throw new InvalidOperationException("injected clipboard flush failure"); Flushed++; return Task.CompletedTask; }
        public Task ClearAsync() { stored?.Dispose(); stored = null; return Task.CompletedTask; }
        public async Task<IAsyncDataTransfer?> TryGetDataAsync()
        { if (ReadGate is not null) await ReadGate.Task; return stored is null ? null : new Borrowed(stored, () => BorrowedDisposed++); }
        public Task<IAsyncDataTransfer?> TryGetInProcessDataAsync() => Task.FromResult(stored);
        public void Dispose() { stored?.Dispose(); stored = null; }
        private sealed class Borrowed(IAsyncDataTransfer source, Action disposed) : IAsyncDataTransfer
        {
            public IReadOnlyList<DataFormat> Formats => source.Formats;
            public IReadOnlyList<IAsyncDataTransferItem> Items => source.Items;
            public void Dispose() => disposed();
        }
    }
}
