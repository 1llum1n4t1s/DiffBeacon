using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessTableColumnIgnoreChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.Combine(output, "table-column-ignore");
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false);

        void Verify(string name, bool passed, string detail = "")
            => check("table column ignore " + name, passed, detail);

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

        static bool HasRule(ComparisonProject project, int column, TablePaneMask panes)
            => project.TableColumnIgnoreRules.Any(rule => rule.Column == column && rule.Panes == panes);

        TextBox ColumnBox(TablePanel panel) => panel.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "table-ignore-column");
        ComboBox ScopeBox(TablePanel panel) => panel.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "table-ignore-column-scope");
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

        var leftText = "old-A\r\nold-B\r\n";
        var rightText = "new-X\r\nold-A\r\nold-B\r\n";
        var panel = LoadOn(pane, "two-way", leftText, rightText);
        var defaultMapping = Mapping(panel.Comparison);
        var defaultProject = pane.CaptureProject();
        Verify("defaults to no rules", defaultProject.TableColumnIgnoreRules.Length == 0
            && panel.Comparison.Documents[0].Rows.SelectMany(row => row.Cells).Select(cell => cell.Value).SequenceEqual(["old-A", "old-B"]));
        Verify("controls have stable names", ColumnBox(panel) is not null && ScopeBox(panel).Items.Count == 2
            && ActionButton(panel, "table-ignore-column-replace") is not null
            && ActionButton(panel, "table-ignore-column-add") is not null
            && ActionButton(panel, "table-ignore-column-reset") is not null);

        ColumnBox(panel).Text = "1";
        ScopeBox(panel).SelectedIndex = 0;
        Click(panel, "table-ignore-column-add");
        var allProject = pane.CaptureProject();
        var allMapping = Mapping(panel.Comparison);
        Verify("all-pane rule re-compares and preserves visible differences", HasRule(allProject, 1, TablePaneMask.All)
            && !SameMap(defaultMapping, allMapping) && panel.Comparison.HasDifferences
            && panel.Comparison.GetCell(0, 0, 0)?.Value == "old-A"
            && panel.Comparison.GetKind(0, 0, 0) == DiffKind.Modified,
            "Rows are matched with the rule while source text and cell difference remain.");
        Verify("rendered cells retain original text", panel.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "old-A")
            && panel.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "new-X"));
        screenshot("table-column-ignore-all.png");

        Click(panel, "table-ignore-column-add");
        Verify("adding a duplicate is idempotent", pane.CaptureProject().TableColumnIgnoreRules.Length == 1);
        ColumnBox(panel).Text = "2";
        Click(panel, "table-ignore-column-add");
        Verify("adding a distinct column accumulates", pane.CaptureProject().TableColumnIgnoreRules.Length == 2
            && HasRule(pane.CaptureProject(), 1, TablePaneMask.All) && HasRule(pane.CaptureProject(), 2, TablePaneMask.All));

        ScopeBox(panel).SelectedIndex = 1;
        SideBox(panel).SelectedIndex = 0;
        ColumnBox(panel).Text = "1";
        Click(panel, "table-ignore-column-replace");
        var afterLeftReplace = pane.CaptureProject();
        Verify("selected-pane replacement preserves other scopes", afterLeftReplace.TableColumnIgnoreRules.Length == 3
            && HasRule(afterLeftReplace, 1, TablePaneMask.Left)
            && HasRule(afterLeftReplace, 1, TablePaneMask.All) && HasRule(afterLeftReplace, 2, TablePaneMask.All));

        ScopeBox(panel).SelectedIndex = 0;
        ColumnBox(panel).Text = "3";
        Click(panel, "table-ignore-column-replace");
        var afterAllReplace = pane.CaptureProject();
        Verify("all-pane replacement leaves selected-pane rules", afterAllReplace.TableColumnIgnoreRules.Length == 2
            && HasRule(afterAllReplace, 1, TablePaneMask.Left) && HasRule(afterAllReplace, 3, TablePaneMask.All));

        var beforeInvalid = afterAllReplace.TableColumnIgnoreRules.ToArray();
        ColumnBox(panel).Text = "0";
        Click(panel, "table-ignore-column-add");
        Verify("invalid UI column preserves settings and reports reason", pane.CaptureProject().TableColumnIgnoreRules.SequenceEqual(beforeInvalid)
            && panel.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text?.Contains("除外列は1から", StringComparison.Ordinal) == true));

        Click(panel, "table-ignore-column-reset");
        Verify("reset clears all pane rules and restores default mapping", pane.CaptureProject().TableColumnIgnoreRules.Length == 0
            && SameMap(defaultMapping, Mapping(panel.Comparison)));
        screenshot("table-column-ignore-reset.png");

        ScopeBox(panel).SelectedIndex = 1;
        SideBox(panel).SelectedIndex = 1;
        ColumnBox(panel).Text = "1";
        Click(panel, "table-ignore-column-add");
        Verify("two-way selected right maps to right mask", pane.CaptureProject().TableColumnIgnoreRules.Length == 1
            && HasRule(pane.CaptureProject(), 1, TablePaneMask.Right));

        panel = LoadOn(pane, "three-way", leftText, rightText, "new-X\r\nold-A\r\nold-B\r\n");
        ScopeBox(panel).SelectedIndex = 1;
        SideBox(panel).SelectedIndex = 1;
        ColumnBox(panel).Text = "1";
        Click(panel, "table-ignore-column-add");
        var baseProject = pane.CaptureProject();
        Verify("three-way selected ancestor maps to base mask", panel.Comparison.Documents.Count == 3
            && HasRule(baseProject, 1, TablePaneMask.Base)
            && panel.Comparison.Documents.Select(document => document.Rows.Count).SequenceEqual([2, 3, 3]));
        screenshot("table-column-ignore-base.png");

        var workspacePath = Path.Combine(directory, "column-ignore-workspace.json");
        var savedProject = pane.CaptureProject();
        pump(WorkspaceStore.SaveWorkspaceAsync(workspacePath, new ComparisonWorkspace { Entries = [savedProject], ActiveEntryIndex = 0 }));
        var restoredWorkspace = Wait(WorkspaceStore.LoadWorkspaceAsync(workspacePath));
        var restored = restoredWorkspace.Entries.Single();
        Verify("workspace roundtrip retains pane rules", restoredWorkspace.ActiveEntryIndex == 0
            && restored.TableColumnIgnoreRules.SequenceEqual(savedProject.TableColumnIgnoreRules));

        var legacyProjectPath = Path.Combine(directory, "column-ignore-legacy-project.json");
        File.WriteAllText(legacyProjectPath, "{\"mode\":\"Table\",\"leftPath\":\"left.csv\",\"rightPath\":\"right.csv\"}", utf8);
        var legacyProject = Wait(WorkspaceStore.LoadAsync(legacyProjectPath));
        Verify("project missing the new setting defaults empty", legacyProject.TableColumnIgnoreRules.Length == 0);

        pane.ApplyProject(restored);
        pane.DiscardChanges();
        pump(pane.CompareProjectAsync());
        panel = pane.GetVisualDescendants().OfType<TablePanel>().Single();
        Verify("reopened workspace restores the active rule", pane.CaptureProject().TableColumnIgnoreRules.SequenceEqual(savedProject.TableColumnIgnoreRules)
            && panel.Comparison.Documents.Count == 3 && HasRule(pane.CaptureProject(), 1, TablePaneMask.Base));
        screenshot("table-column-ignore-workspace-reopen.png");
    }
}
