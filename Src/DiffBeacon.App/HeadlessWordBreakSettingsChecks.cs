using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessWordBreakSettingsChecks
{
    internal static void Run(ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<Task> pump, Action<string> screenshot)
    {
        var originalProject = pane.CaptureProject();
        var originalLeft = pane.LeftEditor.Text ?? "";
        var originalRight = pane.RightEditor.Text ?? "";
        var separatorToggle = pane.GetVisualDescendants().OfType<CheckBox>()
            .Single(box => box.Name == "comparison-break-on-separators");
        var separatorText = pane.GetVisualDescendants().OfType<TextBox>()
            .Single(box => box.Name == "comparison-word-break-separators");
        var inlineDiffLevel = pane.GetVisualDescendants().OfType<ComboBox>()
            .Single(box => box.Name == "comparison-inline-diff-level");

        void Verify(string name, bool passed, string detail = "")
            => check("word-break settings " + name, passed, detail);

        try
        {
            var defaults = pane.CaptureProject();
            Verify("default mode and text match WinMerge", inlineDiffLevel.SelectedIndex == 0
                && defaults.InlineCharacterLevel && separatorToggle.IsChecked == true
                && separatorText.Text == ComparisonOptions.DefaultWordBreakSeparators
                && separatorText.MaxLength == ComparisonOptions.MaximumWordBreakSeparatorsLength
                && defaults.BreakOnSeparators && defaults.WordBreakSeparators == ComparisonOptions.DefaultWordBreakSeparators);
            pane.LeftEditor.Text = "prefixold suffix\n";
            pane.RightEditor.Text = "prefixnew suffix\n";
            inlineDiffLevel.SelectedIndex = 0;
            pane.CompareEditors();
            Dispatcher.UIThread.RunJobs();
            Verify("character mode narrows a changed word", Spans(pane.CurrentDiff?.Rows.FirstOrDefault()?.LeftSpans) == "6:3"
                && Spans(pane.CurrentDiff?.Rows.FirstOrDefault()?.RightSpans) == "6:3");
            inlineDiffLevel.SelectedIndex = 1;
            pane.CompareEditors();
            Dispatcher.UIThread.RunJobs();
            Verify("word mode highlights the whole changed token", Spans(pane.CurrentDiff?.Rows.FirstOrDefault()?.LeftSpans) == "0:9"
                && Spans(pane.CurrentDiff?.Rows.FirstOrDefault()?.RightSpans) == "0:9");
            screenshot("word-break-settings-word-level.png");

            inlineDiffLevel.SelectedIndex = 0;
            separatorText.Text = "_";
            separatorToggle.IsChecked = true;
            pane.LeftEditor.Text = "a_b_c\n";
            pane.RightEditor.Text = "a_c_b\n";
            pane.CompareEditors();
            Dispatcher.UIThread.RunJobs();
            Verify("custom separator reaches inline diff", Spans(pane.CurrentDiff?.Rows.FirstOrDefault()?.LeftSpans) == "3:2"
                && Spans(pane.CurrentDiff?.Rows.FirstOrDefault()?.RightSpans) == "2:2", Spans(pane.CurrentDiff?.Rows.FirstOrDefault()?.LeftSpans));
            screenshot("word-break-settings-custom.png");

            separatorToggle.IsChecked = false;
            pane.CompareEditors();
            Dispatcher.UIThread.RunJobs();
            Verify("disabled separator mode treats punctuation as word text", Spans(pane.CurrentDiff?.Rows.FirstOrDefault()?.LeftSpans) == "2:3"
                && Spans(pane.CurrentDiff?.Rows.FirstOrDefault()?.RightSpans) == "2:3", Spans(pane.CurrentDiff?.Rows.FirstOrDefault()?.LeftSpans));

            separatorToggle.IsChecked = true;
            inlineDiffLevel.SelectedIndex = 1;
            pane.LeftEditor.Text = "prefixold suffix\n";
            pane.RightEditor.Text = "prefixnew suffix\n";
            var projectPath = Path.Combine(output, "word-break-settings-project.json");
            pump(WorkspaceStore.SaveAsync(projectPath, pane.CaptureProject()));
            var load = WorkspaceStore.LoadAsync(projectPath);
            pump(load);
            var restored = load.Result;
            pane.ApplyProject(restored);
            pane.CompareEditors();
            Dispatcher.UIThread.RunJobs();
            Verify("workspace save and reload restore settings and behavior", !pane.CaptureProject().InlineCharacterLevel
                && pane.CaptureProject().BreakOnSeparators
                && pane.CaptureProject().WordBreakSeparators == "_"
                && Spans(pane.CurrentDiff?.Rows.FirstOrDefault()?.LeftSpans) == "0:9"
                && Spans(pane.CurrentDiff?.Rows.FirstOrDefault()?.RightSpans) == "0:9");

            separatorText.Text = ".";
            pane.LeftEditor.Text = "alpha.old\n";
            pane.RightEditor.Text = "alpha.new\n";
            pane.CompareEditors();
            Dispatcher.UIThread.RunJobs();
            var reportPath = Path.Combine(output, "word-break-settings-report.html");
            pump(pane.SaveReportAsync(reportPath));
            var report = File.ReadAllText(reportPath);
            Verify("GUI report retains workspace separator settings", report.Contains("<dt>BreakOnSeparators</dt><dd>true</dd>", StringComparison.Ordinal)
                && report.Contains("<dt>InlineCharacterLevel</dt><dd>false</dd>", StringComparison.Ordinal)
                && report.Contains("<dt>WordBreakSeparators</dt><dd>.</dd>", StringComparison.Ordinal)
                && report.Contains("<span class=\"inline-diff\">old</span>", StringComparison.Ordinal)
                && report.Contains("<span class=\"inline-diff\">new</span>", StringComparison.Ordinal), reportPath);
        }
        finally
        {
            pane.ApplyProject(originalProject);
            pane.LeftEditor.Text = originalLeft;
            pane.RightEditor.Text = originalRight;
            pane.CompareEditors();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string Spans(IEnumerable<InlineSpan>? spans)
        => spans is null ? "<missing>" : string.Join(";", spans.Select(span => $"{span.Start}:{span.Length}"));
}
