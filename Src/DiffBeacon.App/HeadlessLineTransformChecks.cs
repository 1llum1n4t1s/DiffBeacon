using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessLineTransformChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.Combine(output, "line-transforms");
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false);
        void Verify(string name, bool passed, string detail = "") => check("line transform " + name, passed, detail);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static Button Named(LineTransformDialog dialog, string name) => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
        static Button Caption(Control owner, string caption) => owner.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, caption));
        void Settle() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        T Wait<T>(Task<T> task) { pump(task); return task.GetAwaiter().GetResult(); }

        HeadlessLineTransformLoggingChecks.Run(Verify);
        WriteFixtureDiagnostics(Path.Combine(directory, "diagnostics.ndjson"), Verify);
        foreach (var mask in Enumerable.Range(1, 7).Select(value => (TablePaneMask)value))
        {
            var plan = LineTransformPlan.Compile([new("\"changed\"", mask)], new LineTransformBudget());
            foreach (var side in new[] { TablePaneMask.Left, TablePaneMask.Base, TablePaneMask.Right })
                Verify("scope applies only to targeted side " + mask + "/" + side,
                    plan.Transform(["source"], side, false).Single() == ((mask & side) != 0 ? "changed" : "source"));
        }
        Verify("legacy noopt directive remains usable", LineTransformPlan.Compile(
            [new("@noopt ColumnRange(\"1\")", TablePaneMask.All)], new LineTransformBudget())
            .Transform(["a,b"], TablePaneMask.Left, true).Single() == "a");
        foreach (var expression in new[] { "UnknownFunction()", "LineCount", "\"a\nb\"", "\"a\rb\"" })
        {
            var rejected = false;
            try { LineTransformPlan.Compile([new(expression, TablePaneMask.All)], new LineTransformBudget()).Transform(["source"], TablePaneMask.Left, false); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or ArithmeticException) { rejected = true; }
            Verify("unsupported expression rejects atomically " + expression, rejected);
        }

        // 実際の設定入口を使い、登録前のエラーと一覧の変更を確認する。
        LineTransformDialog? shown = null;
        var oldShown = LineTransformDialog.Shown;
        LineTransformDialog.Shown = dialog => shown = dialog;
        try
        {
            pane.SetLineTransforms([]);
            Click(Caption(pane, "行変換…")); Settle();
            var dialog = shown ?? throw new InvalidOperationException("行変換ダイアログが開きませんでした。");
            foreach (var expression in new[] { "", "UnknownFunction()", "ColumnRange(\"0\")", "LineCount" })
            {
                dialog.Expression.Text = expression; Click(Named(dialog, "LineTransformAdd"));
                Verify("invalid dialog input retains empty list: " + expression, dialog.Rules.Count == 0 && !string.IsNullOrEmpty(dialog.Error.Text));
            }
            var masks = new[] { TablePaneMask.All, TablePaneMask.Left, TablePaneMask.Base, TablePaneMask.Right,
                TablePaneMask.Left | TablePaneMask.Base, TablePaneMask.Left | TablePaneMask.Right, TablePaneMask.Base | TablePaneMask.Right };
            for (var index = 0; index < masks.Length; index++)
            {
                dialog.Target.SelectedIndex = index; dialog.Expression.Text = "ColumnRange(\"1\")";
                Click(Named(dialog, "LineTransformAdd"));
                Verify("dialog scope " + index, dialog.Rules[index].Panes == masks[index]);
            }
            dialog.RuleList.SelectedIndex = 0; dialog.Expression.Text = "ColumnRange(\"2\")";
            Click(Named(dialog, "LineTransformChange"));
            Verify("change replaces selected rule", dialog.Rules.Count == 7 && dialog.Rules[0].Expression == "ColumnRange(\"2\")");
            Click(Named(dialog, "LineTransformDown"));
            Verify("down moves ordered rule", dialog.Rules[1].Expression == "ColumnRange(\"2\")");
            Click(Named(dialog, "LineTransformUp"));
            Verify("up restores rule order", dialog.Rules[0].Expression == "ColumnRange(\"2\")");
            Click(Named(dialog, "LineTransformDelete"));
            Verify("delete removes only selected rule", dialog.Rules.Count == 6);
            Settle(); SaveDialogFrame(dialog, Path.Combine(output, "line-transform-dialog.png"),
                Path.Combine(directory, "dialog-bounds.json"), Verify);
            var submitted = dialog.Rules.ToArray();
            Click(dialog.Accept); Click(dialog.Accept); Settle();
            Verify("duplicate apply adopts one immutable result", pane.CaptureProject().LineTransformRules.SequenceEqual(submitted));
            shown = null; Click(Caption(pane, "行変換…")); Settle();
            dialog = shown ?? throw new InvalidOperationException("行変換ダイアログの再開に失敗しました。");
            Verify("reopen retains ordered rules", dialog.Rules.SequenceEqual(submitted));
            Click(Named(dialog, "LineTransformClear"));
            Verify("clear empties candidate only", dialog.Rules.Count == 0 && pane.CaptureProject().LineTransformRules.SequenceEqual(submitted));
            Click(dialog.Cancel); Settle();
            Verify("cancel keeps adopted rules", pane.CaptureProject().LineTransformRules.SequenceEqual(submitted));
            shown = null; Click(Caption(pane, "行変換…")); Settle();
            dialog = shown ?? throw new InvalidOperationException("行変換ダイアログの再開に失敗しました。");
            pane.SetLineTransforms([new("ColumnRange(\"3\")", TablePaneMask.All)]);
            Click(dialog.Accept); Settle();
            Verify("stale dialog cannot overwrite later settings", pane.CaptureProject().LineTransformRules.Single().Expression == "ColumnRange(\"3\")");
        }
        finally { LineTransformDialog.Shown = oldShown; }

        var originals = new Dictionary<string, byte[]>();
        void Load(string name, string mode, string left, string right, LineTransformRule[] rules, string? middle = null,
            TableColumnRangeRule[]? staticRules = null)
        {
            pane.DiscardChanges();
            string Write(string side, string text)
            {
                var path = Path.Combine(directory, name + "-" + side + (mode == "Table" ? ".csv" : ".txt"));
                File.WriteAllText(path, text, utf8); originals[path] = File.ReadAllBytes(path); return path;
            }
            pane.ApplyProject(new ComparisonProject { LeftPath = Write("left", left), RightPath = Write("right", right),
                BasePath = middle is null ? "" : Write("middle", middle), Mode = mode, TableDelimiter = ',', TableQuote = '"',
                TableAllowNewlinesInQuotes = true, LineTransformRules = rules, TableColumnRangeRules = staticRules ?? [] });
            pane.DiscardChanges(); pump(pane.CompareProjectAsync()); Settle();
        }
        TablePanel Table() => pane.GetVisualDescendants().OfType<TablePanel>().Single();
        void TableClick(TablePanel table, string caption)
        {
            Click(Caption(table, caption)); if (table.PendingOperation is { } task) pump(task); Settle();
        }
        var first = new LineTransformRule("ColumnRange(\"1\")", TablePaneMask.All);
        Load("text", "Text", "a,old\r\nb,old\rc,old\n", "a,new\r\nb,new\rc,new\n", [new("\"same\"", TablePaneMask.All)]);
        Verify("text key equality exposes raw ignored rows", pane.CurrentDiff is { HasDifferences: false } diff
            && diff.Rows.Any(row => row.IsIgnored) && diff.LeftText == "a,old\r\nb,old\rc,old\n"
            && pane.LeftEditor.Text == diff.LeftText && pane.RightEditor.Text == diff.RightText);
        screenshot("line-transform-text.png");
        var previous = pane.CurrentDiff;
        pane.SetLineTransforms([new("ColumnRange(If(true, \"0\", \"1\"))", TablePaneMask.All)]);
        pump(pane.CompareProjectAsync()); Settle();
        Verify("evaluation failure retains prior text display", ReferenceEquals(previous, pane.CurrentDiff)
            && pane.LeftEditor.Text == "a,old\r\nb,old\rc,old\n");

        Load("table", "Table", "id,value\r\na,\"old,quoted\"\r\n", "id,value\r\na,\"new,quoted\"\r\n", [first]);
        var table = Table();
        Verify("table keys ignore raw cell difference and preserve quoted source", table.Comparison.IsIgnored(0, 1)
            && table.Comparison.Documents[0].SourceText == "id,value\r\na,\"old,quoted\"\r\n"
            && table.Comparison.GetCell(0, 1, 1)?.Value == "old,quoted");
        table.SelectCell(0, 1, 1); table.CellEditor.Text = "edited,raw";
        TableClick(table, "セルを変更");
        var edited = pane.LeftEditor.Text ?? throw new InvalidOperationException("編集後の原文がありません。");
        Verify("raw cell remains editable under transform", edited == "id,value\r\na,\"edited,raw\"\r\n");
        TableClick(table, "セル編集を戻す");
        Verify("undo restores full raw source", pane.LeftEditor.Text == "id,value\r\na,\"old,quoted\"\r\n");
        TableClick(table, "セル編集をやり直す");
        Verify("redo restores raw edit", pane.LeftEditor.Text == edited);
        pump(pane.SaveAsync(false));
        var savedPath = pane.CaptureProject().LeftPath;
        Verify("save writes original cells and EOL rather than comparison keys", File.ReadAllBytes(savedPath).SequenceEqual(utf8.GetBytes(edited)));
        originals[savedPath] = utf8.GetBytes(edited);
        pump(pane.CompareProjectAsync()); Settle();
        Verify("save reload keeps raw edit and transform", pane.LeftEditor.Text == edited && pane.CaptureProject().LineTransformRules.SequenceEqual([first]));
        screenshot("line-transform-table-edited.png");

        var workspacePath = Path.Combine(directory, "workspace.json");
        var captured = pane.CaptureProject();
        pump(WorkspaceStore.SaveWorkspaceAsync(workspacePath, new ComparisonWorkspace { Entries = [captured], ActiveEntryIndex = 0 }));
        var restored = Wait(WorkspaceStore.LoadWorkspaceAsync(workspacePath));
        Verify("workspace roundtrip retains exact expression and mask", restored.Entries.Single().LineTransformRules.SequenceEqual(captured.LineTransformRules));
        pane.ApplyProject(restored.Entries.Single()); pane.DiscardChanges(); pump(pane.CompareProjectAsync()); Settle();
        Verify("workspace reopen reapplies transform to raw source", Table().Comparison.IsIgnored(0, 1) && pane.LeftEditor.Text == edited);

        TextBlock RowHeader(TablePanel source, int side, int row) => source.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => block.Name == $"table-row-header-{side}-{row}");
        const string identicalRows = "a,b\r\nc,d\r\n";
        Load("row-only", "Table", identicalRows, identicalRows, []);
        pane.SetLineTransforms([new("If(LineNumber == 1, \"x\", Line)", TablePaneMask.Left)]);
        pump(pane.CompareProjectAsync()); Settle();
        var rowOnly = Table();
        Verify("row classification exposes transformed difference without recoloring raw cells", rowOnly.Comparison.UsesLineTransforms && rowOnly.Comparison.HasDifferences
            && rowOnly.Comparison.Rows.Count == 2 && rowOnly.Comparison.GetRowKind(0, 0) == DiffKind.Modified
            && Enumerable.Range(0, 2).All(side => Enumerable.Range(0, 2).All(row => Enumerable.Range(0, 2)
                .All(column => rowOnly.Comparison.GetKind(side, row, column) == DiffKind.Equal))));
        Verify("changed row header is visible while other row stays transparent", Enumerable.Range(0, 2).All(side =>
            Equals(RowHeader(rowOnly, side, 0).Tag, DiffKind.Modified) && RowHeader(rowOnly, side, 0).Background is not null
            && Equals(RowHeader(rowOnly, side, 1).Tag, DiffKind.Equal) && RowHeader(rowOnly, side, 1).Background is null));
        Verify("row-only classification retains both raw editors", pane.LeftEditor.Text == identicalRows && pane.RightEditor.Text == identicalRows);
        pump(pane.SaveAsync(false)); pump(pane.SaveAsync(true));
        Verify("row-only classification never saves transformed keys", File.ReadAllBytes(pane.CaptureProject().LeftPath).SequenceEqual(utf8.GetBytes(identicalRows))
            && File.ReadAllBytes(pane.CaptureProject().RightPath).SequenceEqual(utf8.GetBytes(identicalRows)));
        using (var evidence = File.Create(Path.Combine(directory, "row-only-classification.json")))
        using (var json = new Utf8JsonWriter(evidence, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartArray();
            for (var side = 0; side < 2; side++) for (var row = 0; row < 2; row++)
            {
                var header = RowHeader(rowOnly, side, row);
                json.WriteStartObject(); json.WriteNumber("side", side); json.WriteNumber("alignedRow", row);
                json.WriteNumber("sourceRow", rowOnly.Comparison.GetSourceRow(side, row) ?? -1);
                json.WriteString("rowKind", rowOnly.Comparison.GetRowKind(side, row).ToString()); json.WriteString("headerTag", header.Tag?.ToString());
                json.WriteBoolean("hasBackground", header.Background is not null); json.WriteString("sourceText", rowOnly.Comparison.Documents[side].SourceText);
                json.WriteStartArray("cellKinds"); for (var column = 0; column < 2; column++) json.WriteStringValue(rowOnly.Comparison.GetKind(side, row, column).ToString());
                json.WriteEndArray(); json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        screenshot("line-transform-row-only-classification.png");
        Load("ignored-header", "Table", "a,old\r\n", "a,new\r\n", [first]);
        Verify("key equal raw differences retain Equal ignored row headers", Table().Comparison.IsIgnored(0, 0)
            && Enumerable.Range(0, 2).All(side => Equals(RowHeader(Table(), side, 0).Tag, DiffKind.Equal)
                && RowHeader(Table(), side, 0).Background is null));

        static string RowMapping(TableComparisonResult result) => string.Join(";", Enumerable.Range(0, result.Rows.Count)
            .Select(row => string.Join(",", Enumerable.Range(0, result.Documents.Count).Select(side => result.GetSourceRow(side, row)?.ToString() ?? "missing"))));
        Load("static-baseline", "Table", "A\r\nB\r\n", "A,x\r\nB,y\r\n", []);
        var staticBaseline = RowMapping(Table().Comparison);
        var staticBaselineKinds = Enumerable.Range(0, Table().Comparison.Rows.Count)
            .SelectMany(row => Enumerable.Range(0, 2).Select(side => Table().Comparison.GetRowKind(side, row))).ToArray();
        Load("static-base-only", "Table", "A\r\nB\r\n", "A,x\r\nB,y\r\n", [], staticRules: [new("1", TablePaneMask.Base)]);
        Verify("ancestor only static range cannot affect two-way mapping", RowMapping(Table().Comparison) == staticBaseline
            && !Table().Comparison.UsesLineTransforms && pane.CaptureProject().TableColumnRangeRules.Single().Panes == TablePaneMask.Base
            && Enumerable.Range(0, Table().Comparison.Rows.Count)
                .SelectMany(row => Enumerable.Range(0, 2).Select(side => Table().Comparison.GetRowKind(side, row))).SequenceEqual(staticBaselineKinds));
        var staticRight = Table();
        staticRight.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "table-column-range-scope").SelectedIndex = 1;
        staticRight.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "table-side").SelectedIndex = 1;
        staticRight.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "table-column-range").Text = "1";
        Click(staticRight.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "table-column-range-replace"));
        if (staticRight.PendingOperation is { } staticTask) pump(staticTask); Settle();
        Verify("two-way right static range targets actual right pane", pane.CaptureProject().TableColumnRangeRules.Any(rule => rule.Panes == TablePaneMask.Right && rule.Specification == "1")
            && staticRight.Comparison.Rows.Count == 2 && Enumerable.Range(0, 2).All(row => staticRight.Comparison.GetSourceRow(0, row) == row + 1
                && staticRight.Comparison.GetSourceRow(1, row) == row + 1));
        Verify("static right anchors preserve extra raw values and existing cell colors", !staticRight.Comparison.UsesLineTransforms
            && staticRight.Comparison.GetCell(1, 0, 1)?.Value == "x" && staticRight.Comparison.GetCell(1, 1, 1)?.Value == "y"
            && Enumerable.Range(0, 2).All(row => staticRight.Comparison.GetKind(1, row, 0) == DiffKind.Equal
                && staticRight.Comparison.GetKind(1, row, 1) != DiffKind.Equal && RowHeader(staticRight, 1, row).Background is null));
        screenshot("line-transform-static-right.png");

        TextBox IgnoreRows() => pane.GetVisualDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "除外行の正規表現");
        CheckBox IgnoreBlank() => pane.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, "空行を無視"));
        Load("ignored-regex", "Table", "A,old\r\n", "A,new\r\n", []);
        IgnoreRows().Text = ".*"; pump(pane.CompareProjectAsync()); Settle();
        Verify("regex without general rules ignores raw cell difference", !Table().Comparison.HasDifferences);
        pane.SetLineTransforms([new("Line", TablePaneMask.All)]); pump(pane.CompareProjectAsync()); Settle();
        Verify("no-op general transform preserves regex equality and ignored row", !Table().Comparison.HasDifferences
            && Table().Comparison.IsIgnored(0, 0) && Table().Comparison.GetCell(0, 0, 1)?.Value == "old"
            && Table().Comparison.GetCell(1, 0, 1)?.Value == "new" && pane.LeftEditor.Text == "A,old\r\n" && pane.RightEditor.Text == "A,new\r\n"
            && pane.CaptureProject().IgnoreLinePattern == ".*");

        void PlainColumn()
        {
            // Table parserはNUL区切りを拒否する。入力に現れないASCII区切りで一列を保持する。
            var project = pane.CaptureProject(); project.TableDelimiter = '|'; pane.ApplyProject(project); pane.DiscardChanges();
        }
        Load("ignored-blank", "Table", " \r\nanchor\r\n", "\t\r\nanchor\r\n", [new("Line", TablePaneMask.All)]);
        PlainColumn(); IgnoreBlank().IsChecked = false; pump(pane.CompareProjectAsync()); Settle();
        Verify("blank rows remain different when ignore blank is disabled", Table().Comparison.HasDifferences);
        IgnoreBlank().IsChecked = true; pump(pane.CompareProjectAsync()); Settle();
        Verify("no-op general transform preserves blank ignore in plain column", !Table().Comparison.HasDifferences
            && Table().Comparison.IsIgnored(0, 0) && Table().Comparison.GetCell(0, 0, 0)?.Value == " "
            && Table().Comparison.GetCell(1, 0, 0)?.Value == "\t" && pane.LeftEditor.Text == " \r\nanchor\r\n"
            && pane.RightEditor.Text == "\t\r\nanchor\r\n" && pane.CaptureProject().IgnoreBlankLines);

        Load("transformed-empty", "Table", "source\r\nanchor\r\n", " \t\r\nanchor\r\n",
            [new("If(LineNumber == 1, \"\", Line)", TablePaneMask.Left)]);
        PlainColumn(); IgnoreBlank().IsChecked = false; pump(pane.CompareProjectAsync()); Settle();
        Verify("transformed empty versus whitespace differs before blank ignore", Table().Comparison.HasDifferences);
        IgnoreBlank().IsChecked = true; pump(pane.CompareProjectAsync()); Settle();
        Verify("blank ignore uses transformed key while keeping original source cell", !Table().Comparison.HasDifferences
            && Table().Comparison.IsIgnored(0, 0) && Table().Comparison.GetCell(0, 0, 0)?.Value == "source"
            && Table().Comparison.GetCell(1, 0, 0)?.Value == " \t" && pane.LeftEditor.Text == "source\r\nanchor\r\n"
            && pane.RightEditor.Text == " \t\r\nanchor\r\n");

        Load("dynamic", "Table", "a,old\r\nb,keep\r\n", "a,new\r\nb,keep\r\n",
            [new("ColumnRange(If(LineNumber == 1, \"1\", \"1-\"))", TablePaneMask.All)]);
        Verify("dynamic column range uses current logical row", Table().Comparison.IsIgnored(0, 0)
            && Table().Comparison.GetCell(0, 1, 1)?.Value == "keep");
        Load("residual", "Table", "a,old\n", "b,new\n", [first], staticRules: [new("1", TablePaneMask.All)]);
        Verify("static range and transform retain residual cell difference", Table().Comparison.GetKind(0, 0, 1) == DiffKind.Modified
            && !Table().Comparison.IsIgnored(0, 0));
        Load("three", "Table", "a,left\r\n", "a,right\r\n", [first], "a,middle\r\n");
        Verify("three-way uses original pane identities and raw cells", Table().Comparison.Documents.Count == 3
            && Table().Comparison.GetCell(0, 0, 1)?.Value == "left" && Table().Comparison.GetCell(1, 0, 1)?.Value == "middle"
            && Table().Comparison.GetCell(2, 0, 1)?.Value == "right" && Table().Comparison.IsIgnored(0, 0) && Table().Comparison.IsIgnored(2, 0));
        screenshot("line-transform-three.png");
        var adopted = Table();
        var adoptedLeft = pane.LeftEditor.Text; var adoptedRight = pane.RightEditor.Text;
        var inputPath = pane.LeftPath.Text;
        var oldReady = pane.TableReadyForAdoption;
        try
        {
            pane.TableReadyForAdoption = () => Click(Caption(pane, "中止"));
            pump(pane.CompareProjectAsync()); Settle();
            Verify("cancel before adoption keeps previous table and source", ReferenceEquals(adopted, Table())
                && pane.LeftEditor.Text == adoptedLeft && pane.RightEditor.Text == adoptedRight);
            pane.TableReadyForAdoption = () => pane.LeftPath.Text = inputPath + ".stale";
            pump(pane.CompareProjectAsync()); Settle();
            Verify("stale completion cannot replace previous table", ReferenceEquals(adopted, Table())
                && pane.LeftEditor.Text == adoptedLeft && pane.RightEditor.Text == adoptedRight);
            pane.LeftPath.Text = inputPath;
            pane.TableReadyForAdoption = () => pane.LeftEditor.Text = adoptedLeft + "late,edit\r\n";
            pump(pane.CompareProjectAsync()); Settle();
            Verify("late editor change rejects table adoption", ReferenceEquals(adopted, Table())
                && pane.LeftEditor.Text == adoptedLeft + "late,edit\r\n" && pane.RightEditor.Text == adoptedRight);
            pane.LeftEditor.Text = adoptedLeft; pane.DiscardChanges();
        }
        finally { pane.TableReadyForAdoption = oldReady; pane.LeftPath.Text = inputPath; }
        foreach (var entry in originals) Verify("input bytes retained " + Path.GetFileName(entry.Key), File.ReadAllBytes(entry.Key).SequenceEqual(entry.Value));

        // 内包入力は通常project再開の案内ではなく、明示読込みの実経路を使う。
        var archiveLeft = Path.Combine(directory, "archive-stale-left.zip");
        var archiveRight = Path.Combine(directory, "archive-stale-right.zip");
        void WriteArchive(string path, string text)
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            using var leaf = archive.CreateEntry("leaf.txt").Open();
            leaf.Write(utf8.GetBytes(text));
        }
        static string ArchiveHash(string path)
        {
            using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        }
        WriteArchive(archiveLeft, "id,left\r\n"); WriteArchive(archiveRight, "id,right\r\n");
        var archiveLeftHash = ArchiveHash(archiveLeft); var archiveRightHash = ArchiveHash(archiveRight);
        pane.DiscardChanges();
        pane.ApplyProject(new ComparisonProject
        {
            Mode = "Text", LeftReadOnly = true, RightReadOnly = true,
            LeftArchiveInput = new ArchiveProjectInput { RootPath = archiveLeft, LeafEntry = "leaf.txt", RootSha256 = archiveLeftHash },
            RightArchiveInput = new ArchiveProjectInput { RootPath = archiveRight, LeafEntry = "leaf.txt", RootSha256 = archiveRightHash },
            LineTransformRules = [new("Line", TablePaneMask.All)]
        });
        pane.DiscardChanges();
        Verify("archive initial comparison adopts raw differing text", Wait(pane.CompareArchiveProjectAsync())
            && pane.CurrentDiff is { HasDifferences: true } && pane.LeftEditor.Text == "id,left\r\n" && pane.RightEditor.Text == "id,right\r\n");
        var archiveDiff = pane.CurrentDiff; var archiveState = pane.CaptureAdoptionState();
        var archiveLeftText = pane.LeftEditor.Text; var archiveRightText = pane.RightEditor.Text;
        var oldArchiveReady = pane.ArchiveSourceReadyForAdoption;
        var archiveRule = new LineTransformRule("\"same\"", TablePaneMask.All);
        bool archiveAdopted;
        try
        {
            pane.ArchiveSourceReadyForAdoption = () => pane.SetLineTransforms([archiveRule]);
            archiveAdopted = Wait(pane.CompareArchiveProjectAsync()); Settle();
        }
        finally { pane.ArchiveSourceReadyForAdoption = oldArchiveReady; }
        Verify("archive stale transform candidate is rejected before adoption", !archiveAdopted && ReferenceEquals(archiveDiff, pane.CurrentDiff)
            && Equals(archiveState, pane.CaptureAdoptionState()) && pane.LeftEditor.Text == archiveLeftText && pane.RightEditor.Text == archiveRightText);
        var archiveProject = pane.CaptureProject();
        Verify("archive rejected candidate retains later transform metadata", archiveProject.LineTransformRules.SequenceEqual([archiveRule])
            && archiveProject.LeftArchiveInput?.RootPath == archiveLeft && archiveProject.RightArchiveInput?.RootPath == archiveRight
            && archiveProject.LeftArchiveInput?.LeafEntry == "leaf.txt" && archiveProject.RightArchiveInput?.LeafEntry == "leaf.txt");
        Verify("archive transform race retains root input SHA", ArchiveHash(archiveLeft) == archiveLeftHash && ArchiveHash(archiveRight) == archiveRightHash);
        screenshot("line-transform-archive-stale.png");

        var originalWidth = window.Width; var originalHeight = window.Height;
        try
        {
            window.Width = 1280; window.Height = 850; Layout("normal");
            window.Width = 850; window.Height = 550; Layout("minimum");
            for (var index = 0; index < 12; index++) window.AddSession();
            // AddSession selects the last pane; its common settings use the same actual layout.
            Layout("minimum-many-tabs", window.ActivePane);
        }
        finally { window.Width = originalWidth; window.Height = originalHeight; Settle(); }

        void Layout(string name, ComparisonPane? target = null)
        {
            target ??= pane; Settle();
            var toolbar = target.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll =>
                scroll.Content is Control content && content.GetVisualDescendants().Contains(target.CompareButton));
            var transform = Caption(target, "行変換…");
            var filter = target.GetVisualDescendants().OfType<TextBox>().Single(box => Equals(box.PlaceholderText, "ファイルフィルター (.flt)"));
            using var stream = File.Create(Path.Combine(directory, name + "-bounds.json"));
            using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            json.WriteStartArray();
            foreach (var (label, control) in new (string, Control)[] { ("transform", transform), ("filter", filter),
                ("left input", target.LeftPath), ("right input", target.RightPath), ("compare", target.CompareButton) })
            {
                control.BringIntoView(); Settle();
                var point = control.TranslatePoint(default, toolbar);
                var reachable = point is { } p && control.IsEffectivelyVisible && control.Bounds.Height > 0
                    && p.Y >= -.01 && p.Y + control.Bounds.Height <= toolbar.Viewport.Height + .01;
                Verify(name + " reachable " + label, reachable);
                json.WriteStartObject(); json.WriteString("control", label); json.WriteNumber("windowWidth", window.Width);
                json.WriteNumber("windowHeight", window.Height); json.WriteNumber("y", point?.Y ?? -1);
                json.WriteNumber("height", control.Bounds.Height); json.WriteNumber("viewportHeight", toolbar.Viewport.Height);
                json.WriteBoolean("reachable", reachable); json.WriteEndObject();
            }
            json.WriteEndArray(); screenshot("line-transform-" + name + ".png"); toolbar.Offset = default; Settle();
        }
    }

    private static void SaveDialogFrame(LineTransformDialog dialog, string pngPath, string boundsPath,
        Action<string, bool, string> verify)
    {
        var scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single(view => view.Name == "LineTransformScroll");
        dialog.Expression.Focus(); scroll.Offset = default;
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        verify("actual dialog scroll starts at top", Math.Abs(scroll.Offset.Y) < .01, $"offset={scroll.Offset}");
        var size = dialog.ClientSize;
        var validSize = dialog.IsVisible && size.Width > 0 && size.Height > 0
            && double.IsFinite(size.Width) && double.IsFinite(size.Height)
            && dialog.Bounds.Width > 0 && dialog.Bounds.Height > 0;
        verify("actual dialog has a drawable client area", validSize, $"client={size}; bounds={dialog.Bounds}");
        if (!validSize) throw new InvalidOperationException("行変換ダイアログの描画サイズが不正です。");
        var pixels = new PixelSize(checked((int)Math.Ceiling(size.Width)), checked((int)Math.Ceiling(size.Height)));
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96, 96));
        bitmap.Render(dialog);
        // 過去runの証拠を上書きせず、今回の正規出力だけを作成する。
        using (var png = new FileStream(pngPath, FileMode.CreateNew, FileAccess.Write))
            bitmap.Save(png, new PngBitmapEncoderOptions());
        using var bounds = new FileStream(boundsPath, FileMode.CreateNew, FileAccess.Write);
        using var json = new Utf8JsonWriter(bounds, new JsonWriterOptions { Indented = true });
        json.WriteStartObject(); json.WriteString("target", nameof(LineTransformDialog));
        json.WriteNumber("windowWidth", dialog.Width); json.WriteNumber("windowHeight", dialog.Height);
        json.WriteNumber("clientWidth", size.Width); json.WriteNumber("clientHeight", size.Height);
        json.WriteNumber("boundsWidth", dialog.Bounds.Width); json.WriteNumber("boundsHeight", dialog.Bounds.Height);
        json.WriteNumber("pixelWidth", pixels.Width); json.WriteNumber("pixelHeight", pixels.Height);
        json.WriteStartArray("controls");
        foreach (var control in new Control[] { dialog.Expression, dialog.Target, dialog.RuleList, dialog.Error, dialog.Accept, dialog.Cancel })
        {
            var point = control.TranslatePoint(default, dialog);
            json.WriteStartObject(); json.WriteString("name", control.Name);
            json.WriteNumber("x", point?.X ?? -1); json.WriteNumber("y", point?.Y ?? -1);
            json.WriteNumber("width", control.Bounds.Width); json.WriteNumber("height", control.Bounds.Height);
            json.WriteBoolean("visible", control.IsEffectivelyVisible); json.WriteEndObject();
        }
        json.WriteEndArray(); json.WriteEndObject();
        verify("dialog PNG renders the dialog visual itself", bitmap.PixelSize == pixels, pngPath);
    }

    // 固定C++原典を答えとして読み込まず、公開Planを実Appプロセス内で実行した全byte証拠を出す。
    private static void WriteFixtureDiagnostics(string path, Action<string, bool, string> verify)
    {
        using var resource = typeof(HeadlessLineTransformChecks).Assembly.GetManifestResourceStream("DiffBeacon.LineTransformCases")
            ?? throw new InvalidOperationException("行変換の固定case resourceがありません。");
        using var cases = JsonDocument.Parse(resource);
        using var output = File.Create(path);
        var count = 0;
        foreach (var item in cases.RootElement.EnumerateArray())
        {
            var originals = item.GetProperty("lines").EnumerateArray().Select(line => line.GetString()!).ToArray();
            var endings = originals.Select(Ending).ToArray();
            var content = originals.Select((line, index) => line[..(line.Length - endings[index].Length)]).ToArray();
            var keys = content.ToArray();
            var rules = item.GetProperty("groups").EnumerateArray().SelectMany(group =>
                group.GetProperty("expressions").EnumerateArray().Select(expression =>
                    new LineTransformRule(expression.GetString()!, (TablePaneMask)group.GetProperty("mask").GetInt32()))).ToArray();
            var status = "ok"; var phase = "compile"; var error = "";
            var delimiter = item.GetProperty("delimiter").GetString()![0]; var quote = item.GetProperty("quote").GetString()![0];
            try
            {
                var plan = LineTransformPlan.Compile(rules, new LineTransformBudget()); phase = "evaluate";
                keys = plan.Transform(content, (TablePaneMask)(1 << item.GetProperty("pane").GetInt32()),
                    item.GetProperty("table").GetBoolean(), new DelimitedSyntax(delimiter, quote)); phase = "";
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or ArithmeticException)
            { status = "error"; error = exception.Message; }
            using (var json = new Utf8JsonWriter(output))
            {
                json.WriteStartObject(); json.WriteString("id", item.GetProperty("id").GetString()); json.WriteString("status", status);
                json.WriteString("errorPhase", phase); json.WriteString("error", error); json.WriteStartArray("lines");
                for (var index = 0; index < originals.Length; index++)
                {
                    json.WriteStartObject(); json.WriteString("originalHex", Hex(originals[index]));
                    json.WriteString("keyHex", Hex(keys[index] + endings[index])); json.WriteNumber("originalEol", Eol(endings[index]));
                    json.WriteNumber("keyEol", Eol(Ending(keys[index] + endings[index])));
                    json.WriteNumber("columnCount", ColumnCount(content[index], delimiter, quote)); json.WriteEndObject();
                }
                json.WriteEndArray(); json.WriteEndObject(); json.Flush();
            }
            output.WriteByte((byte)'\n'); count++;
        }
        verify("all fixed oracle cases executed in app process", count == 86, path);
    }

    private static string Ending(string value) => value.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
        : value.EndsWith('\r') ? "\r" : value.EndsWith('\n') ? "\n" : "";
    private static int Eol(string value) => value switch { "\n" => 1, "\r" => 2, "\r\n" => 3, _ => 0 };
    private static string Hex(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value)).ToLowerInvariant();
    private static int ColumnCount(string value, char delimiter, char quote)
    {
        var count = 1; var quoted = false;
        foreach (var character in value) { if (character == quote) quoted = !quoted; else if (!quoted && character == delimiter) count++; }
        return count;
    }
}
