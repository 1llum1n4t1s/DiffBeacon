using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessImageProjectChecks
{
    // 失敗条件: ページ・閾値・表示方式の初期化、未採用選択の保存、取消／旧完了の巻戻し、入力上書き。
    internal static void Run(ComparisonPane pane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "image-project"); Directory.CreateDirectory(folder);
        var left = Path.Combine(output, "tiff", "two-pages-le.tif");
        var right = Path.Combine(output, "tiff", "same-first-right.tif");
        var requested = new ImageViewSettings { LeftFrame = 2, RightFrame = 2, Threshold = 510, Zoom = 2.5,
            OverlayOpacity = .65, HighlightAlpha = .3, View = "Overlay", ShowDifferences = false, ReportAllFrames = false };
        var project = new ComparisonProject { LeftPath = left, RightPath = right, Mode = "Image", ImageSettings = requested };
        pane.ApplyProject(project); pump(pane.ComparePathsAsync());
        var panel = Panel(pane);
        check("image project GUI restores selected pages and threshold", panel.LeftFrame == 2 && panel.RightFrame == 2
            && panel.DifferentPixels == 0 && panel.CaptureSettings() == requested, "");
        check("image project GUI restores overlay tab", panel.GetVisualDescendants().OfType<TabControl>()
            .Single(control => control.Name == "ImageDisplayMode").SelectedIndex == 1, "");
        var copiedSettings = pane.CaptureProject().ImageSettings;
        copiedSettings.Threshold = 0;
        requested.Zoom = 1;
        check("image project GUI caller cannot mutate captured or applied settings", panel.CaptureSettings().Threshold == 510
            && pane.CaptureProject().ImageSettings.Zoom == 2.5, "");
        var frozen = panel.CaptureSettings();
        pump(pane.ComparePathsAsync()); panel = Panel(pane);
        check("image project GUI recompare retains live settings", panel.CaptureSettings() == frozen && panel.DifferentPixels == 0, "");
        // sqrt(255²+255²)のDoubleをdecimal表示へ丸めると閾値の下側になり、差分を誤検出する。
        var fractional = frozen with { Threshold = 360.62445840513925 };
        pump(panel.ApplySettingsAsync(fractional));
        using (var evidence = File.Create(Path.Combine(folder, "fractional-threshold.json")))
        using (var writer = new Utf8JsonWriter(evidence, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteNumber("requested", fractional.Threshold);
            writer.WriteNumber("restored", panel.CaptureSettings().Threshold); writer.WriteNumber("differentPixels", panel.DifferentPixels);
            writer.WriteNumber("leftFrame", panel.LeftFrame); writer.WriteNumber("rightFrame", panel.RightFrame); writer.WriteEndObject();
        }
        screenshot("image-project-fractional.png");
        check("image project GUI retains fractional threshold boundary", panel.CaptureSettings().Threshold == fractional.Threshold
            && panel.DifferentPixels == 0, panel.CaptureSettings().Threshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        pump(panel.SetFramesAsync(1, 1)); pump(panel.SetFramesAsync(2, 2));
        check("image project GUI page changes retain fractional threshold", panel.CaptureSettings().Threshold == fractional.Threshold
            && panel.DifferentPixels == 0, panel.CaptureSettings().Threshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        pump(panel.ApplySettingsAsync(frozen with { Threshold = double.Epsilon }));
        pump(panel.SetFramesAsync(1, 1)); pump(panel.SetFramesAsync(2, 2));
        check("image project GUI retains subdecimal threshold across pages", panel.CaptureSettings().Threshold == double.Epsilon
            && panel.DifferentPixels == 1, panel.CaptureSettings().Threshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        pump(panel.ApplySettingsAsync(frozen));
        screenshot("image-project-restored.png");
        var saved = Path.Combine(folder, "saved.json");
        pump(WorkspaceStore.SaveAsync(saved, pane.CaptureProject()));
        var report = Path.Combine(folder, "selected.html"); pump(pane.SaveReportAsync(report));
        var html = File.ReadAllText(report);
        check("image project GUI report follows saved selected frame and threshold", html.Contains("data-left-frame=\"2\" data-right-frame=\"2\" data-different-pixels=\"0\"", StringComparison.Ordinal)
            && !html.Contains("data-left-frame=\"1\"", StringComparison.Ordinal), report);
        var reopened = new MainWindow([]);
        try
        {
            reopened.Show(); pump(reopened.OpenWorkspaceAsync(saved, discardChanges: true));
            var restored = Panel(reopened.ActivePane);
            check("image project GUI workspace reload restores all settings", restored.CaptureSettings() == frozen
                && reopened.ActivePane.CaptureProject().ImageSettings == frozen && restored.DifferentPixels == 0, "");
            var priorPanes = reopened.SessionPanes.ToArray();
            var invalid = Path.Combine(folder, "invalid.json");
            File.WriteAllText(invalid, "{\"leftPath\":\"\",\"rightPath\":\"\",\"imageSettings\":null}");
            var rejected = false;
            try { pump(reopened.OpenWorkspaceAsync(invalid, discardChanges: true)); } catch (InvalidDataException) { rejected = true; }
            check("image project GUI invalid settings preserve old workspace", rejected && reopened.SessionPanes.SequenceEqual(priorPanes)
                && restored.CaptureSettings() == frozen, "");
        }
        finally { reopened.Close(); }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var bitmaps = panel.RenderedFrames.ToArray();
        var refused = false;
        try { pump(panel.ApplySettingsAsync(frozen with { LeftFrame = 1, RightFrame = 1, HighlightAlpha = 1 }, canceled.Token)); }
        catch (OperationCanceledException) { refused = true; }
        check("image project GUI canceled restore preserves display", refused && panel.CaptureSettings() == frozen
            && panel.RenderedFrames.SequenceEqual(bitmaps), "");
        refused = false;
        try { pump(panel.ApplySettingsAsync(frozen with { LeftFrame = 3 })); } catch (ArgumentException) { refused = true; }
        check("image project GUI unavailable page preserves display", refused && panel.CaptureSettings() == frozen
            && panel.RenderedFrames.SequenceEqual(bitmaps), "");
        foreach (var invalidAlpha in new[] { -.1, 1.1, double.NaN, double.PositiveInfinity })
        {
            refused = false;
            try { pump(panel.ApplySettingsAsync(frozen with { HighlightAlpha = invalidAlpha })); }
            catch (ArgumentException) { refused = true; }
            check("image project GUI invalid alpha preserves display " + invalidAlpha, refused && panel.CaptureSettings() == frozen
                && panel.RenderedFrames.SequenceEqual(bitmaps) && panel.HistoryCount == 0 && !panel.HasUnsavedChanges, "");
        }
        using var pendingCancellation = new CancellationTokenSource();
        var canceledEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.FrameCandidateReady = () => { canceledEntered.TrySetResult(); return canceledRelease.Task; };
        var canceledCandidate = panel.ApplySettingsAsync(frozen with { HighlightAlpha = 1 }, pendingCancellation.Token);
        pump(canceledEntered.Task); pendingCancellation.Cancel(); canceledRelease.SetResult();
        refused = false;
        try { pump(canceledCandidate); } catch (OperationCanceledException) { refused = true; }
        panel.FrameCandidateReady = null;
        check("image project GUI canceled computed alpha preserves display", refused && panel.CaptureSettings() == frozen
            && panel.CaptureReport().HighlightAlpha == frozen.HighlightAlpha && panel.RenderedFrames.SequenceEqual(bitmaps)
            && panel.HistoryCount == 0 && !panel.HasUnsavedChanges, "real candidate canceled before publication");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.FrameCandidateReady = () => { entered.TrySetResult(); return release.Task; };
        var obsolete = panel.ApplySettingsAsync(frozen with { LeftFrame = 1, RightFrame = 1, Zoom = 1, HighlightAlpha = 1 });
        pump(entered.Task);
        check("image project GUI pending alpha is not saved", !obsolete.IsCompleted && panel.CaptureSettings().HighlightAlpha == frozen.HighlightAlpha
            && panel.RenderedFrames.SequenceEqual(bitmaps),
            "computed old candidate is gated before real publication");
        panel.FrameCandidateReady = null;
        var latestSettings = frozen with { View = "PixelDifference", Zoom = 8, ShowDifferences = true, Threshold = 0, HighlightAlpha = 0 };
        var latest = panel.ApplySettingsAsync(latestSettings);
        pump(latest); release.SetResult();
        try { pump(Task.WhenAll(obsolete, latest)); } catch (OperationCanceledException) { }
        check("image project GUI latest restore wins", latest.IsCompletedSuccessfully && panel.CaptureSettings() == latestSettings
            && panel.CaptureReport().HighlightAlpha == latestSettings.HighlightAlpha && panel.DifferentPixels == 1, "");
        screenshot("image-project-latest.png");
        var three = new ComparisonProject { LeftPath = left, BasePath = right, RightPath = left, Mode = "Image",
            ImageSettings = new() { LeftFrame = 1, MiddleFrame = 2, RightFrame = 2, Zoom = .1, OverlayOpacity = 0 } };
        pane.ApplyProject(three); pump(pane.ComparePathsAsync());
        check("image project GUI restores independent three-pane pages", Panel(pane).CaptureSettings() == three.ImageSettings
            && Panel(pane).LeftFrame == 1 && Panel(pane).MiddleFrame == 2 && Panel(pane).RightFrame == 2, "");
        File.WriteAllBytes(Path.Combine(folder, "observations.json"), JsonSerializer.SerializeToUtf8Bytes(pane.CaptureProject(),
            ProjectJsonContext.Default.ComparisonProject));
        screenshot("image-project-three.png");
    }

    private static SpecializedViews.ImagePanel Panel(ComparisonPane pane)
        => pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
}
