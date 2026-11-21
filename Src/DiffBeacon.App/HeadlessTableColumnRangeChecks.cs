using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessTableColumnRangeChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.Combine(output, "table-column-range");
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false);

        void Verify(string name, bool passed, string detail = "")
            => check("table column range " + name, passed, detail);

        TablePanel LoadOn(ComparisonPane target, string name, string left, string right, string? baseText = null)
        {
            target.DiscardChanges();
            var leftPath = Path.Combine(directory, name + "-left.csv");
            var rightPath = Path.Combine(directory, name + "-right.csv");
            var basePath = baseText is null ? "" : Path.Combine(directory, name + "-base.csv");
            File.WriteAllText(leftPath, left, utf8);
            File.WriteAllText(rightPath, right, utf8);
            if (baseText is not null) File.WriteAllText(basePath, baseText, utf8);
            target.ApplyProject(new ComparisonProject
            {
                LeftPath = leftPath, RightPath = rightPath, BasePath = basePath, Mode = "Table",
                TableDelimiter = ',', TableQuote = '"', TableAllowNewlinesInQuotes = true
            });
            target.DiscardChanges();
            pump(target.CompareProjectAsync());
            Dispatcher.UIThread.RunJobs();
            return target.GetVisualDescendants().OfType<TablePanel>().Single();
        }

        static int?[][] Mapping(TableComparisonResult result)
            => Enumerable.Range(0, result.Rows.Count)
                .Select(row => Enumerable.Range(0, result.Documents.Count).Select(side => result.GetSourceRow(side, row)).ToArray())
                .ToArray();

        static bool SameMap(int?[][] left, int?[][] right)
            => left.Length == right.Length && left.Zip(right).All(pair => pair.First.SequenceEqual(pair.Second));

        TextBox RangeBox(TablePanel panel) => panel.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "table-column-range");
        ComboBox RangeScope(TablePanel panel) => panel.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "table-column-range-scope");
        ComboBox SideBox(TablePanel panel) => panel.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "table-side");
        Button ActionButton(TablePanel panel, string name) => panel.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);

        void Click(TablePanel panel, string name)
        {
            ActionButton(panel, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (panel.PendingOperation is { } operation) pump(operation);
            Dispatcher.UIThread.RunJobs();
        }

        T Wait<T>(Task<T> task)
        {
            pump(task);
            return task.GetAwaiter().GetResult();
        }

        static string Describe(int?[][] mapping)
            => string.Join(";", mapping.Select(row => string.Join(",", row.Select(value => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—"))));

        var leftText = "key,detail,group\r\nA,old-A,one\r\nB,old-B,two\r\nshort,only\r\n";
        var rightText = "key,detail,group\r\nX,new-X,extra\r\nA,new-A,one\r\nB,new-B,two\r\nshort,other\r\n";
        var panel = LoadOn(pane, "two-way", leftText, rightText);
        var defaultMapping = Mapping(panel.Comparison);
        Verify("new projects default to all columns", pane.CaptureProject().TableColumnRangeRules.Length == 0);
        Verify("controls expose range input and replace/add/reset actions", RangeBox(panel) is not null
            && RangeScope(panel).Items.Count == 2
            && ActionButton(panel, "table-column-range-replace") is not null
            && ActionButton(panel, "table-column-range-add") is not null
            && ActionButton(panel, "table-column-range-reset") is not null);

        RangeBox(panel).Text = "1,3";
        Click(panel, "table-column-range-add");
        var included = Mapping(panel.Comparison);
        var leftAAlignedRow = Array.FindIndex(included, row => row[0] == 2);
        Verify("include columns realign rows without hiding original cell diffs", pane.CaptureProject().TableColumnRangeRules
            .SequenceEqual([new TableColumnRangeRule("1,3", TablePaneMask.All)])
            && included.Any(row => row[0] == 2 && row[1] == 3)
            && leftAAlignedRow >= 0 && panel.Comparison.GetCell(0, leftAAlignedRow, 1)?.Value == "old-A"
            && panel.Comparison.GetKind(0, leftAAlignedRow, 1) == DiffKind.Modified,
            Describe(included));
        Verify("source buffers remain unchanged", pane.LeftEditor.Text == leftText && pane.RightEditor.Text == rightText);
        screenshot("table-column-range-included.png");

        RangeBox(panel).Text = "1-3, !2";
        Click(panel, "table-column-range-replace");
        var excluded = Mapping(panel.Comparison);
        Verify("exclusions take priority over included ranges", SameMap(included, excluded)
            && pane.CaptureProject().TableColumnRangeRules.Single().Specification == "1-3, !2");

        RangeScope(panel).SelectedIndex = 1;
        SideBox(panel).SelectedIndex = 0;
        RangeBox(panel).Text = "1,3";
        Click(panel, "table-column-range-add");
        Verify("selected-pane range keeps all-pane range and targets left", pane.CaptureProject().TableColumnRangeRules.Length == 2
            && pane.CaptureProject().TableColumnRangeRules.Any(rule => rule.Panes == TablePaneMask.All && rule.Specification == "1-3, !2")
            && pane.CaptureProject().TableColumnRangeRules.Any(rule => rule.Panes == TablePaneMask.Left && rule.Specification == "1,3"));

        var beforeInvalid = pane.CaptureProject().TableColumnRangeRules.ToArray();
        var beforeInvalidMap = Mapping(panel.Comparison);
        RangeBox(panel).Text = "3-2";
        Click(panel, "table-column-range-add");
        Verify("malformed range leaves current mapping and settings intact", pane.CaptureProject().TableColumnRangeRules.SequenceEqual(beforeInvalid)
            && SameMap(beforeInvalidMap, Mapping(panel.Comparison))
            && panel.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text?.Contains("列範囲は1以上", StringComparison.Ordinal) == true));

        var savedProject = pane.CaptureProject();
        var workspacePath = Path.Combine(directory, "column-range-workspace.json");
        pump(WorkspaceStore.SaveWorkspaceAsync(workspacePath, new ComparisonWorkspace { Entries = [savedProject], ActiveEntryIndex = 0 }));
        var restoredWorkspace = Wait(WorkspaceStore.LoadWorkspaceAsync(workspacePath));
        Verify("workspace roundtrip retains ordered pane ranges", restoredWorkspace.ActiveEntryIndex == 0
            && restoredWorkspace.Entries.Single().TableColumnRangeRules.SequenceEqual(savedProject.TableColumnRangeRules));
        pane.ApplyProject(restoredWorkspace.Entries.Single());
        pane.DiscardChanges();
        pump(pane.CompareProjectAsync());
        panel = pane.GetVisualDescendants().OfType<TablePanel>().Single();
        Verify("reopened project reapplies the range selection", pane.CaptureProject().TableColumnRangeRules.SequenceEqual(savedProject.TableColumnRangeRules)
            && SameMap(beforeInvalidMap, Mapping(panel.Comparison)));
        screenshot("table-column-range-workspace-reopen.png");

        Click(panel, "table-column-range-reset");
        Verify("reset removes range rules and restores default row mapping", pane.CaptureProject().TableColumnRangeRules.Length == 0
            && SameMap(defaultMapping, Mapping(panel.Comparison)));

        var baseText = "key,detail,group\r\nB,base-B,two\r\nA,base-A,one\r\nshort,base-only\r\n";
        panel = LoadOn(pane, "three-way", leftText, rightText, baseText);
        RangeScope(panel).SelectedIndex = 1;
        SideBox(panel).SelectedIndex = 1;
        RangeBox(panel).Text = "1,3";
        Click(panel, "table-column-range-replace");
        Verify("selected ancestor uses the middle-pane range mask", panel.Comparison.Documents.Count == 3
            && pane.CaptureProject().TableColumnRangeRules.Single().Panes == TablePaneMask.Base
            && pane.CaptureProject().TableColumnRangeRules.Single().Specification == "1,3");
        screenshot("table-column-range-base.png");
    }
}
