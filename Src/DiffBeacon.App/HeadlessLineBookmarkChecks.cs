using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessLineBookmarkChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check, Action<string> screenshot)
    {
        var directory = Path.Combine(output, "line-bookmarks");
        Directory.CreateDirectory(directory);
        var observations = new List<(string Name, bool Passed, string Detail)>();
        var originalLeft = pane.LeftEditor.Text ?? "";
        var originalRight = pane.RightEditor.Text ?? "";
        var originalLeftReadonly = pane.LeftEditor.IsReadOnly;
        var originalRightReadonly = pane.RightEditor.IsReadOnly;

        void Verify(string name, bool passed, string detail = "")
        {
            observations.Add((name, passed, detail));
            check("text line bookmarks " + name, passed, detail);
        }

        Button ButtonByName(string name) => window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
        ComboBox NumberSelector() => window.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Name == "line-bookmark-number");
        TextBlock Summary() => window.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "line-bookmark-summary");
        string SummaryText() => Summary().Text ?? "";

        void Click(string name)
        {
            ButtonByName(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }

        void FocusLine(int side, int line)
        {
            var editor = pane.TextEditor(side);
            editor.Focus();
            var text = editor.Text ?? "";
            var start = 0;
            for (var current = 0; current < line; current++)
            {
                var next = text.IndexOf('\n', start);
                if (next < 0) throw new InvalidOperationException($"Line {line} is outside side {side} input.");
                start = next + 1;
            }
            editor.CaretIndex = start;
            Dispatcher.UIThread.RunJobs();
        }

        int CaretLine(TextBox editor)
        {
            var text = editor.Text ?? "";
            var caret = Math.Clamp(editor.CaretIndex, 0, text.Length);
            var line = 0;
            for (var index = 0; index < caret; index++)
                if (text[index] == '\n') line++;
            return line;
        }

        try
        {
            var editorTab = window.GetVisualDescendants().OfType<TabControl>().First(control => control.Items.Count >= 4);
            editorTab.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();

            var text = "one\ntwo\nthree\nfour";
            pane.LeftEditor.IsReadOnly = false;
            pane.RightEditor.IsReadOnly = false;
            pane.LeftEditor.Text = text;
            pane.RightEditor.Text = "right one\nright two";
            FocusLine(0, 0);
            Click("line-bookmark-toggle");
            FocusLine(0, 2);
            Click("line-bookmark-toggle");
            Verify("plain marks list 1-based lines", SummaryText().Contains("左: 行 1, 3", StringComparison.Ordinal), SummaryText());
            Verify("toggle leaves editor text unchanged", pane.LeftEditor.Text == text, pane.LeftEditor.Text ?? "");
            screenshot("line-bookmarks.png");

            FocusLine(0, 1);
            Click("line-bookmark-next");
            Verify("next moves to the following marked line", CaretLine(pane.LeftEditor) == 2, $"line={CaretLine(pane.LeftEditor)}");
            Click("line-bookmark-previous");
            Verify("previous wraps to the earlier marked line", CaretLine(pane.LeftEditor) == 0, $"line={CaretLine(pane.LeftEditor)}");
            FocusLine(0, 3);
            Click("line-bookmark-next");
            Verify("next wraps at the final line", CaretLine(pane.LeftEditor) == 0, $"line={CaretLine(pane.LeftEditor)}");
            FocusLine(0, 0);
            Click("line-bookmark-previous");
            Verify("previous wraps at the first line", CaretLine(pane.LeftEditor) == 2, $"line={CaretLine(pane.LeftEditor)}");

            var number = NumberSelector();
            number.SelectedIndex = 4;
            FocusLine(0, 1);
            Click("line-bookmark-number-toggle");
            FocusLine(0, 3);
            Click("line-bookmark-number-toggle");
            FocusLine(0, 0);
            Click("line-bookmark-number-go");
            Verify("numbered slot moves to its newly assigned line", CaretLine(pane.LeftEditor) == 3, $"line={CaretLine(pane.LeftEditor)}; summary={SummaryText()}");
            Verify("numbered id has one location per side", SummaryText().Contains("#4=4", StringComparison.Ordinal)
                && !SummaryText().Contains("#4=2", StringComparison.Ordinal), SummaryText());
            Click("line-bookmark-number-toggle");
            Verify("toggling the numbered slot at its line clears it", !SummaryText().Contains("#4=", StringComparison.Ordinal)
                && SummaryText().Contains("行 1, 3", StringComparison.Ordinal), SummaryText());
            number.SelectedIndex = 7;
            FocusLine(0, 1);
            Click("line-bookmark-number-toggle");
            Click("line-bookmark-clear-numbered");
            Verify("clearing numbered slots preserves plain marks", SummaryText().Contains("行 1, 3", StringComparison.Ordinal)
                && !SummaryText().Contains("番号 #", StringComparison.Ordinal), SummaryText());

            Click("line-bookmark-clear-all");
            pane.LeftEditor.Text = text;
            FocusLine(0, 1);
            Click("line-bookmark-toggle");
            pane.LeftEditor.Text = "inserted\n" + text;
            FocusLine(0, 0);
            Click("line-bookmark-next");
            Verify("inserting a line before a mark shifts its line", CaretLine(pane.LeftEditor) == 2, $"line={CaretLine(pane.LeftEditor)}; summary={SummaryText()}");
            pane.LeftEditor.Text = "inserted\none\nthree\nfour";
            Dispatcher.UIThread.RunJobs();
            Verify("deleting the bookmarked line removes it", !ButtonByName("line-bookmark-next").IsEnabled, $"text={pane.LeftEditor.Text}; summary={SummaryText()}");

            Click("line-bookmark-clear-all");
            pane.LeftEditor.Text = "first\nsecond\nthird";
            FocusLine(0, 1);
            Click("line-bookmark-toggle");
            pane.LeftEditor.Text = "second\nthird";
            FocusLine(0, 1);
            Click("line-bookmark-next");
            Verify("deleting from column zero transfers the surviving line mark", CaretLine(pane.LeftEditor) == 0, $"line={CaretLine(pane.LeftEditor)}; summary={SummaryText()}");

            Click("line-bookmark-clear-all");
            pane.LeftEditor.Text = "tail\nother";
            FocusLine(0, 0);
            Click("line-bookmark-toggle");
            pane.LeftEditor.Text = "inserted\ntail\nother";
            Dispatcher.UIThread.RunJobs();
            Verify("splitting a marked line leaves its mark on the first line", SummaryText().Contains("左: 行 1", StringComparison.Ordinal), SummaryText());

            Click("line-bookmark-clear-all");
            FocusLine(2, 1);
            Click("line-bookmark-toggle");
            Verify("focus selects an independent bookmark side", SummaryText().Contains("右: 行 2", StringComparison.Ordinal), SummaryText());
            FocusLine(0, 0);
            Verify("bookmark positions are isolated per side", SummaryText().Contains("左: 位置なし", StringComparison.Ordinal), SummaryText());
            FocusLine(2, 0);
            Click("line-bookmark-next");
            Verify("navigation uses the focused side", CaretLine(pane.RightEditor) == 1, $"line={CaretLine(pane.RightEditor)}; summary={SummaryText()}");

            Verify("right editor text is unchanged by bookmark actions", pane.RightEditor.Text == "right one\nright two", pane.RightEditor.Text ?? "");
        }
        finally
        {
            try
            {
                FocusLine(0, 0);
                Click("line-bookmark-clear-all");
                FocusLine(2, 0);
                Click("line-bookmark-clear-all");
                pane.LeftEditor.Text = originalLeft;
                pane.RightEditor.Text = originalRight;
                pane.LeftEditor.IsReadOnly = originalLeftReadonly;
                pane.RightEditor.IsReadOnly = originalRightReadonly;
                FocusLine(0, 0);
            }
            catch
            {
                pane.LeftEditor.Text = originalLeft;
                pane.RightEditor.Text = originalRight;
                pane.LeftEditor.IsReadOnly = originalLeftReadonly;
                pane.RightEditor.IsReadOnly = originalRightReadonly;
            }
        }

        var reportPath = Path.Combine(directory, "observations.json");
        using var stream = File.Create(reportPath);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("runtime", Environment.Version.ToString());
        writer.WriteStartArray("checks");
        foreach (var item in observations)
        {
            writer.WriteStartObject();
            writer.WriteString("name", item.Name);
            writer.WriteBoolean("passed", item.Passed);
            writer.WriteString("detail", item.Detail);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteBoolean("leftTextRestored", pane.LeftEditor.Text == originalLeft);
        writer.WriteBoolean("rightTextRestored", pane.RightEditor.Text == originalRight);
        writer.WriteEndObject();
        writer.Flush();
        stream.Flush(true);
    }
}
