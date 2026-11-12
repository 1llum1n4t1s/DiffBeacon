using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessTableColumnHeaderChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.Combine(output, "table-column-headers");
        Directory.CreateDirectory(directory);
        var results = new List<(string Name, bool Passed, string Detail)>();
        var utf8 = new UTF8Encoding(false);

        void Verify(string name, bool passed, string detail = "")
        {
            results.Add((name, passed, detail));
            check("table column headers " + name, passed, detail);
        }

        TablePanel LoadOn(ComparisonPane targetPane, string name, string left, string right, string? ancestor = null)
        {
            targetPane.DiscardChanges();
            var leftPath = Path.Combine(directory, name + "-left.csv");
            var rightPath = Path.Combine(directory, name + "-right.csv");
            var basePath = ancestor is null ? "" : Path.Combine(directory, name + "-base.csv");
            File.WriteAllText(leftPath, left, utf8);
            File.WriteAllText(rightPath, right, utf8);
            if (ancestor is not null) File.WriteAllText(basePath, ancestor, utf8);
            targetPane.ApplyProject(new ComparisonProject
            {
                LeftPath = leftPath, RightPath = rightPath, BasePath = basePath, Mode = "Table",
                TableDelimiter = ';', TableQuote = '\'', TableAllowNewlinesInQuotes = true
            });
            targetPane.DiscardChanges();
            pump(targetPane.CompareProjectAsync());
            Dispatcher.UIThread.RunJobs();
            return targetPane.GetVisualDescendants().OfType<TablePanel>().Single();
        }
        TablePanel Load(string name, string left, string right, string? ancestor = null) => LoadOn(pane, name, left, right, ancestor);

        CheckBox Toggle(TablePanel panel) => panel.GetVisualDescendants().OfType<CheckBox>()
            .Single(box => box.Name == "table-use-first-row-as-headers");
        Border HeaderRow(TablePanel panel) => panel.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Name == "table-column-headers");
        string HeaderText(TablePanel panel, int side, int column) => panel.GetVisualDescendants().OfType<TextBlock>()
            .SingleOrDefault(block => block.Name == $"table-column-header-{side}-{column}")?.Text ?? "<missing>";
        double ColumnWidth(TablePanel panel, int side, int row, int visibleColumn) => panel.GetVisualDescendants().OfType<Grid>()
            .Single(grid => grid.Name == $"table-row-cells-{side}-{row}").ColumnDefinitions[visibleColumn + 1].Width.Value;
        double HeaderColumnWidth(TablePanel panel, int side, int visibleColumn) => panel.GetVisualDescendants().OfType<Grid>()
            .Single(grid => grid.Name == $"table-header-cells-{side}").ColumnDefinitions[visibleColumn + 1].Width.Value;
        void Click(TablePanel panel, string label)
        {
            var button = panel.GetVisualDescendants().OfType<Button>().Single(item => Equals(item.Content, label));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (panel.PendingOperation is { } operation) pump(operation);
            Dispatcher.UIThread.RunJobs();
        }

        void RunPreferenceChecks()
        {
            var settingsPath = Path.Combine(directory, "shared-table-options.json");
            var sharedOptions = new TableApplicationOptionsStore(settingsPath);
            Verify("path-backed preference defaults off without creating a file", !sharedOptions.Current.UseFirstRowAsHeaders
                && !File.Exists(settingsPath));
            bool PersistedHeaderSetting()
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(settingsPath));
                return document.RootElement.GetProperty("useFirstRowAsHeaders").GetBoolean();
            }

            var sharedWindow = new MainWindow(null, new ImageApplicationOptionsStore(), new FolderApplicationOptionsStore(), sharedOptions)
                { Width = 1280, Height = 850 };
            sharedWindow.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                var firstPane = sharedWindow.SessionPanes[0];
                var secondPane = sharedWindow.AddSession();
                sharedWindow.SelectSession(0);
                var firstPanel = LoadOn(firstPane, "preference-tab-one", "id;value\r\n1;left\r\n", "id;value\r\n1;right\r\n");
                sharedWindow.SelectSession(1);
                var secondPanel = LoadOn(secondPane, "preference-tab-two", "key;value\r\n1;left\r\n", "key;value\r\n1;right\r\n");
                Verify("new table tabs use the shared default", Toggle(firstPanel).IsChecked == false && !HeaderRow(firstPanel).IsVisible
                    && Toggle(secondPanel).IsChecked == false && !HeaderRow(secondPanel).IsVisible);

                sharedWindow.SelectSession(0);
                Toggle(firstPanel).IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                var persistedEnabled = PersistedHeaderSetting();
                Verify("enabling headers updates the other open tab and saves", sharedOptions.Current.UseFirstRowAsHeaders
                    && persistedEnabled && Toggle(firstPanel).IsChecked == true && HeaderRow(firstPanel).IsVisible
                    && Toggle(secondPanel).IsChecked == true && HeaderRow(secondPanel).IsVisible);

                sharedWindow.SelectSession(1);
                Toggle(secondPanel).IsChecked = false;
                Dispatcher.UIThread.RunJobs();
                var persistedDisabled = !PersistedHeaderSetting();
                Verify("disabling headers in another tab synchronizes and saves", !sharedOptions.Current.UseFirstRowAsHeaders
                    && persistedDisabled && Toggle(firstPanel).IsChecked == false && !HeaderRow(firstPanel).IsVisible
                    && Toggle(secondPanel).IsChecked == false && !HeaderRow(secondPanel).IsVisible);

                Toggle(secondPanel).IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Verify("re-enabled preference is stored before reopening", sharedOptions.Current.UseFirstRowAsHeaders
                    && PersistedHeaderSetting());
            }
            finally
            {
                foreach (var session in sharedWindow.SessionPanes) session.DiscardChanges();
                sharedWindow.Close();
                Dispatcher.UIThread.RunJobs();
            }

            var reopenedOptions = new TableApplicationOptionsStore(settingsPath);
            Verify("fresh settings store restores the saved preference", reopenedOptions.Current.UseFirstRowAsHeaders
                && reopenedOptions.Diagnostic is null);
            var reopenedWindow = new MainWindow(null, new ImageApplicationOptionsStore(), new FolderApplicationOptionsStore(), reopenedOptions)
                { Width = 1280, Height = 850 };
            reopenedWindow.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                var reopenedPane = reopenedWindow.SessionPanes[0];
                var reopenedPanel = LoadOn(reopenedPane, "preference-after-reopen", "name;value\r\n1;left\r\n", "name;value\r\n1;right\r\n");
                Verify("new table panel restores the saved preference", Toggle(reopenedPanel).IsChecked == true
                    && HeaderRow(reopenedPanel).IsVisible && HeaderText(reopenedPanel, 0, 0) == "name");
            }
            finally
            {
                foreach (var session in reopenedWindow.SessionPanes) session.DiscardChanges();
                reopenedWindow.Close();
                Dispatcher.UIThread.RunJobs();
            }

            var blockedInputPath = Path.Combine(directory, "blocked-table-options-input.csv");
            var blockedRightPath = Path.Combine(directory, "blocked-table-options-right.csv");
            var blockedInputBytes = utf8.GetBytes("id;value\r\n1;left\r\n");
            File.WriteAllBytes(blockedInputPath, blockedInputBytes);
            File.WriteAllText(blockedRightPath, "id;value\r\n1;right\r\n", utf8);
            var blockedOptions = new TableApplicationOptionsStore(blockedInputPath);
            var blockedWindow = new MainWindow(null, new ImageApplicationOptionsStore(), new FolderApplicationOptionsStore(), blockedOptions)
                { Width = 1280, Height = 850 };
            blockedWindow.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                var blockedPane = blockedWindow.SessionPanes[0];
                blockedPane.ApplyProject(new ComparisonProject
                {
                    LeftPath = blockedInputPath, RightPath = blockedRightPath, Mode = "Table",
                    TableDelimiter = ';', TableQuote = '\'', TableAllowNewlinesInQuotes = true
                });
                blockedPane.DiscardChanges();
                pump(blockedPane.CompareProjectAsync());
                Dispatcher.UIThread.RunJobs();
                var blockedPanel = blockedPane.GetVisualDescendants().OfType<TablePanel>().Single();
                Toggle(blockedPanel).IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Verify("settings write to a compared input is refused without changing it", blockedOptions.LastSetOptionsResult == false
                    && blockedOptions.SetOptionsAttemptCount == 1 && !blockedOptions.Current.UseFirstRowAsHeaders
                    && Toggle(blockedPanel).IsChecked == false && !HeaderRow(blockedPanel).IsVisible
                    && File.ReadAllBytes(blockedInputPath).SequenceEqual(blockedInputBytes)
                    && blockedOptions.Diagnostic?.Contains("比較入力", StringComparison.Ordinal) == true);
            }
            finally
            {
                foreach (var session in blockedWindow.SessionPanes) session.DiscardChanges();
                blockedWindow.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        try
        {
            var panel = Load("two-way", "id;value\r\n1;left\r\n", "key;value\r\n1;right\r\n");
            var toggle = Toggle(panel);
            var originalLeft = pane.LeftEditor.Text;
            var originalRight = pane.RightEditor.Text;
            Verify("disabled by default", toggle.IsChecked == false && !HeaderRow(panel).IsVisible);
            Verify("first row remains compared and numbered", panel.Comparison.Rows.Count == 2
                && panel.Comparison.GetSourceRow(0, 0) == 1 && panel.Comparison.GetSourceRow(1, 0) == 1
                && panel.Comparison.GetCell(0, 0, 0)?.Value == "id" && panel.Comparison.GetCell(1, 0, 0)?.Value == "key"
                && panel.Comparison.GetKind(0, 0, 0) == DiffBeacon.Core.DiffKind.Modified && panel.Comparison.HasDifferences);
            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Verify("shows each pane first-row values", HeaderRow(panel).IsVisible
                && HeaderText(panel, 0, 0) == "id" && HeaderText(panel, 0, 1) == "value"
                && HeaderText(panel, 1, 0) == "key" && HeaderText(panel, 1, 1) == "value");
            Verify("toggle does not change source or comparison", pane.LeftEditor.Text == originalLeft && pane.RightEditor.Text == originalRight
                && panel.Comparison.Rows.Count == 2 && panel.Comparison.HasDifferences);
            screenshot("table-column-headers.png");

            panel.SelectCell(0, 0, 0);
            panel.CellEditor.Text = "identifier";
            Click(panel, "セルを変更");
            Verify("first-row edit refreshes column header", HeaderText(panel, 0, 0) == "identifier"
                && panel.Comparison.GetCell(0, 0, 0)?.Value == "identifier" && panel.Comparison.GetSourceRow(0, 0) == 1);
            Click(panel, "セル編集を戻す");
            Verify("undo refreshes column header", HeaderText(panel, 0, 0) == "id" && pane.LeftEditor.Text == originalLeft);
            Click(panel, "セル編集をやり直す");
            Verify("redo refreshes column header", HeaderText(panel, 0, 0) == "identifier"
                && pane.LeftEditor.Text?.StartsWith("identifier;value\r\n", StringComparison.Ordinal) == true);
            Toggle(panel).IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Verify("turning off only hides the pinned row", !HeaderRow(panel).IsVisible && panel.Comparison.Rows.Count == 2
                && panel.Comparison.GetSourceRow(0, 0) == 1 && panel.Comparison.GetCell(0, 0, 0)?.Value == "identifier");

            panel = Load("three-way", "left;value\r\n1;L\r\n", "right;value\r\n1;R\r\n", "base;value\r\n1;B\r\n");
            Toggle(panel).IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Verify("three-way headers follow left base right pane order", panel.Comparison.Documents.Count == 3
                && HeaderText(panel, 0, 0) == "left" && HeaderText(panel, 1, 0) == "base" && HeaderText(panel, 2, 0) == "right");

            panel = Load("empty-header-cells", ";value;;\r\n1;v;3;4\r\n", ";value;;\r\n1;v;3;4\r\n");
            Toggle(panel).IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Verify("empty names fall back to spreadsheet column letters", HeaderText(panel, 0, 0) == "A"
                && HeaderText(panel, 0, 1) == "value" && HeaderText(panel, 0, 2) == "C" && HeaderText(panel, 0, 3) == "D");

            var longData = string.Join(';', Enumerable.Repeat("x", 33));
            panel = Load("paged", ";named;\r\n" + longData + "\r\n", ";named;\r\n" + longData + "\r\n");
            Toggle(panel).IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Verify("first page uses header values and empty fallbacks", HeaderText(panel, 0, 0) == "A" && HeaderText(panel, 0, 1) == "named");
            Click(panel, "次の列");
            Verify("header follows the 32-column page", HeaderText(panel, 0, 32) == "AG");

            var leftHeaders = Enumerable.Repeat("", 33).ToArray();
            var rightHeaders = Enumerable.Repeat("", 33).ToArray();
            var leftValues = Enumerable.Repeat("x", 33).ToArray();
            var rightValues = Enumerable.Repeat("y", 33).ToArray();
            leftHeaders[0] = "id"; leftHeaders[32] = "last";
            rightHeaders[0] = "key"; rightHeaders[32] = "tail";
            leftValues[0] = "long-left-column-content-123456789";
            rightValues[32] = new string('R', 2048);
            static string Row(string[] cells) => string.Join(';', cells) + "\r\n";
            var leftSource = Row(leftHeaders) + Row(leftValues);
            var rightSource = Row(rightHeaders) + Row(rightValues);
            panel = Load("autofit", leftSource, rightSource);
            Toggle(panel).IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Click(panel, "列幅を内容に合わせる");
            var leftFirstWidth = ColumnWidth(panel, 0, 0, 0);
            var narrowColumnWidth = ColumnWidth(panel, 0, 0, 1);
            Verify("fits content across every row and pane", leftFirstWidth > narrowColumnWidth
                && leftFirstWidth == ColumnWidth(panel, 1, 0, 0) && narrowColumnWidth < 140
                && HeaderColumnWidth(panel, 0, 0) == leftFirstWidth);
            Verify("autofit leaves source text and comparison intact", pane.LeftEditor.Text == leftSource
                && pane.RightEditor.Text == rightSource && panel.Comparison.ColumnCount == 33);
            Click(panel, "次の列");
            var offPageColumnWidth = ColumnWidth(panel, 0, 0, 0);
            var maximumWidth = panel.Bounds.Width > 0 && double.IsFinite(panel.Bounds.Width)
                ? Math.Clamp(Math.Floor(panel.Bounds.Width / panel.Comparison.Documents.Count - 50), 48, 600)
                : 600;
            Verify("autofit includes columns outside the current page", offPageColumnWidth > narrowColumnWidth
                && offPageColumnWidth == ColumnWidth(panel, 1, 0, 0));
            Verify("autofit caps wide columns to the pane viewport", Math.Abs(offPageColumnWidth - maximumWidth) < .01);
            screenshot("table-column-autofit.png");

            panel = Load("autofit-three-way", "id;value\r\n1;left\r\n", "id;value\r\n1;right\r\n", "id;value\r\n1;long-ancestor-column-value\r\n");
            Click(panel, "列幅を内容に合わせる");
            Verify("autofit includes the ancestor pane", ColumnWidth(panel, 1, 0, 1) > ColumnWidth(panel, 0, 0, 0)
                && ColumnWidth(panel, 0, 0, 1) == ColumnWidth(panel, 1, 0, 1)
                && ColumnWidth(panel, 1, 0, 1) == ColumnWidth(panel, 2, 0, 1));

            panel = Load("empty-table", "", "");
            Toggle(panel).IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Verify("empty table hides the empty pinned row", panel.Comparison.ColumnCount == 0 && panel.Comparison.Rows.Count == 0
                && !HeaderRow(panel).IsVisible);
            RunPreferenceChecks();
        }
        catch (Exception exception)
        {
            Verify("unexpected exception", false, exception.ToString());
        }
        finally
        {
            var reportPath = Path.Combine(directory, "table-column-header-report.json");
            using var file = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("scope", "table-first-row-headers-column-autofit-and-shared-preference");
                writer.WriteBoolean("activeWindow", ReferenceEquals(window.ActivePane, pane));
                writer.WriteStartArray("assertions");
                foreach (var result in results)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", result.Name);
                    writer.WriteBoolean("passed", result.Passed);
                    writer.WriteString("detail", result.Detail);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
            }
            file.Flush(true);
        }
    }
}
