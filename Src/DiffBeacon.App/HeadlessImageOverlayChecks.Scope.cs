using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json;

namespace DiffBeacon.App;

internal static partial class HeadlessImageOverlayChecks
{
    private static void RunScopeControlChecks(MainWindow window, ComparisonPane pane, string folder, Action<Task> pump,
        Action<string, bool, string> check)
    {
        var paths = new[] { "scope-left.png", "scope-right.png" }.Select(name => Path.Combine(folder, name)).ToArray();
        for (var i = 0; i < 2; i++) pump(ImagePngStore.SaveAsync(paths[i], new(1, 1, 1, [(byte)(i * 200), 10, 30, 255]), []));
        window.ImageOptions.SetOptions(new() { OverlayMode = 2, OverlayAlpha = .65 });
        pane.DiscardChanges(); pane.ApplyProject(new() { Mode = "Image", LeftPath = paths[0], RightPath = paths[1],
            ImageSettings = new() { OverlayOpacity = .3, ShowDifferences = false, BlockSize = 1 } });
        var originalIndex = Array.IndexOf(window.SessionPanes.ToArray(), pane); window.SelectSession(originalIndex);
        pump(pane.ComparePathsAsync()); Render();
        var panel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single(); panel.GateClock = new Clock(0);
        var observations = new List<(string Name, int Scope, bool Explicit, double Alpha, bool BlinkEnabled, bool Blink, bool PeriodEnabled, double Global, bool Matches)>();
        Observe("active-recompare-explicit", 1);
        var inheritedPane = window.AddSession();
        try
        {
            inheritedPane.ApplyProject(pane.CaptureProject() with { ImageSettings = new() { ShowDifferences = false, BlockSize = 1 } });
            pump(inheritedPane.ComparePathsAsync()); Render();
            var other = inheritedPane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single(); other.GateClock = new Clock(0);
            window.SelectSession(originalIndex); Render(); pump(panel.CurrentDisplayOperation);
            var slider = panel.GetVisualDescendants().OfType<Slider>().Single(x => x.Name == "ImageOpacity"); slider.Value = .4; pump(panel.CurrentDisplayOperation);
            Observe("explicit-slider-range", 1, panel.CaptureSettings().OverlayOpacity == .4 && window.ImageOptions.Current.OverlayAlpha == .65 && other.CaptureSettings().OverlayOpacity == .65);
            var inherited = panel.CaptureSettings(); inherited.SetInheritedOverlayOpacity(.65); pump(panel.ApplySettingsAsync(inherited)); Observe("apply-inherited", 0);
            pump(panel.ApplySettingsAsync(panel.CaptureSettings() with { OverlayOpacity = .5 })); Observe("apply-explicit", 1);
            var before = panel.CaptureSettings(); var failed = before with { }; failed.SetInheritedOverlayOpacity(.65);
            panel.OverlayCandidateReady = () => throw new InvalidOperationException("scope failure");
            var operation = panel.ApplySettingsAsync(failed); try { pump(operation); } catch (InvalidOperationException) { }
            panel.OverlayCandidateReady = null; Observe("failed-inherited-restores-explicit", 1, operation.IsFaulted && panel.CaptureSettings() == before);
            window.ImageOptions.SetOptions(window.ImageOptions.Current with { OverlayAlpha = .67 }); pump(panel.CurrentDisplayOperation);
            pump(panel.ApplySettingsAsync(panel.CaptureSettings() with { ShowDifferences = true }));
            var blink = panel.GetVisualDescendants().OfType<CheckBox>().Single(x => x.Name == "ImageBlink");
            blink.BringIntoView(); Render(); var point = blink.TranslatePoint(new Point(blink.Bounds.Width / 2, blink.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Render(); pump(panel.CurrentDisplayOperation);
            Observe("show-true-checkbox-reachable", 1, blink.IsEnabled && blink.IsChecked == true && window.ImageOptions.Current.BlinkDifferences
                && panel.GetVisualDescendants().OfType<NumericUpDown>().Single(x => x.Name == "ImageBlinkPeriod").IsEnabled);
        }
        finally
        {
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(x => x.Items.OfType<TabItem>().Any(t => ReferenceEquals(t.Content, inheritedPane)));
            var tab = tabs.Items.OfType<TabItem>().Single(x => ReferenceEquals(x.Content, inheritedPane));
            ((StackPanel)tab.Header!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.SelectSession(originalIndex); Render();
            using var file = File.Create(Path.Combine(folder, "scope-control-trace.json")); using var writer = new Utf8JsonWriter(file, new() { Indented = true });
            writer.WriteStartArray(); foreach (var item in observations)
            {
                writer.WriteStartObject(); writer.WriteString("name", item.Name); writer.WriteNumber("scope", item.Scope); writer.WriteBoolean("explicit", item.Explicit);
                writer.WriteNumber("alpha", item.Alpha); writer.WriteBoolean("blinkEnabled", item.BlinkEnabled); writer.WriteBoolean("blink", item.Blink);
                writer.WriteBoolean("periodEnabled", item.PeriodEnabled); writer.WriteNumber("globalAlpha", item.Global); writer.WriteBoolean("matches", item.Matches); writer.WriteEndObject();
            } writer.WriteEndArray();
        }
        check("overlay scope and blink controls match actual action", observations.All(item => item.Matches), "active recompare, slider scope, ApplySettings success/failure, actual checkbox aftershowtrue");
        void Observe(string name, int expectedScope, bool additional = true)
        {
            var settings = panel.CaptureSettings(); var scope = panel.GetVisualDescendants().OfType<ComboBox>().Single(x => x.Name == "ImageAlphaScope");
            var blink = panel.GetVisualDescendants().OfType<CheckBox>().Single(x => x.Name == "ImageBlink");
            var period = panel.GetVisualDescendants().OfType<NumericUpDown>().Single(x => x.Name == "ImageBlinkPeriod");
            observations.Add((name, scope.SelectedIndex, settings.HasExplicitOverlayOpacity, settings.OverlayOpacity, blink.IsEnabled,
                blink.IsChecked == true, period.IsEnabled, window.ImageOptions.Current.OverlayAlpha,
                scope.SelectedIndex == expectedScope && settings.HasExplicitOverlayOpacity == (expectedScope == 1) && additional));
        }
        void Render() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    }
}
