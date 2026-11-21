using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessLineTransformArrayChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.Combine(output, "line-transform-arrays"); Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false);
        void Verify(string name, bool success, string detail = "") => check("line transform array " + name, success, detail);
        T Wait<T>(Task<T> task) { pump(task); return task.GetAwaiter().GetResult(); }
        void Settle() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static Button Caption(Control owner, string caption) => owner.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, caption));
        static Button Named(LineTransformDialog dialog, string name) => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
        WriteFixtureDiagnostics(Path.Combine(directory, "diagnostics.ndjson"), Verify);

        var oldShown = LineTransformDialog.Shown;
        LineTransformDialog? shown = null;
        LineTransformDialog.Shown = dialog => shown = dialog;
        try
        {
            pane.SetLineTransforms([]); Click(Caption(pane, "行変換…")); Settle();
            var dialog = shown ?? throw new InvalidOperationException("配列行変換ダイアログがありません。");
            foreach (var expression in new[] { "", "Array(", "At(Array())" })
            {
                dialog.Expression.Text = expression; Click(Named(dialog, "LineTransformAdd"));
                Verify("invalid array dialog expression is rejected " + expression, dialog.Rules.Count == 0 && !string.IsNullOrEmpty(dialog.Error.Text));
            }
            dialog.Expression.Text = "@cs @noopt At(Array(Line, LineNumber), 0)"; Click(Named(dialog, "LineTransformAdd"));
            var intended = dialog.Rules.ToArray(); Click(dialog.Accept); Click(dialog.Accept); Settle();
            Verify("duplicate dialog apply retains dynamic array rule", intended.Length == 1 && pane.CaptureProject().LineTransformRules.SequenceEqual(intended));
            shown = null; Click(Caption(pane, "行変換…")); Settle();
            dialog = shown ?? throw new InvalidOperationException("配列行変換の再開に失敗しました。");
            dialog.Expression.Text = "At(Array(\"replacement\", Line), 0)"; Click(Named(dialog, "LineTransformAdd")); Click(dialog.Cancel); Settle();
            Verify("cancel array edit retains adopted metadata", pane.CaptureProject().LineTransformRules.SequenceEqual(intended));
        }
        finally { LineTransformDialog.Shown = oldShown; }

        var rawLeft = "id,value\r\nA,\"left,quoted\"\r\n";
        var rawMiddle = "id,value\r\nA,\"middle,quoted\"\r\n";
        var rawRight = "id,value\r\nA,\"right,quoted\"\r\n";
        string Write(string side, string text)
        {
            var path = Path.Combine(directory, side + ".csv"); using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            file.Write(utf8.GetBytes(text)); return path;
        }
        var paths = new[] { Write("left", rawLeft), Write("middle", rawMiddle), Write("right", rawRight) };
        var originals = paths.Select(File.ReadAllBytes).ToArray();
        var dynamicRule = new LineTransformRule("@cs @noopt At(Array(Column1, Line, LineNumber), 0)", TablePaneMask.All);
        pane.DiscardChanges(); pane.ApplyProject(new ComparisonProject
        {
            Mode = "Table", LeftPath = paths[0], BasePath = paths[1], RightPath = paths[2], TableDelimiter = ',', TableQuote = '"',
            LineTransformRules = [dynamicRule]
        });
        pump(pane.CompareProjectAsync()); Settle();
        TablePanel Table() => pane.GetVisualDescendants().OfType<TablePanel>().Single();
        void TableClick(TablePanel target, string caption)
        {
            Click(Caption(target, caption)); if (target.PendingOperation is { } task) pump(task); Settle();
        }
        var table = Table();
        Verify("dynamic getter array keys ignore details across three actual panes", table.Comparison.Documents.Count == 3
            && !table.Comparison.HasDifferences && table.Comparison.IsIgnored(0, 1) && table.Comparison.IsIgnored(2, 1)
            && table.Comparison.GetCell(0, 1, 1)?.Value == "left,quoted" && table.Comparison.GetCell(1, 1, 1)?.Value == "middle,quoted"
            && table.Comparison.GetCell(2, 1, 1)?.Value == "right,quoted");
        Verify("array semantic rows expose Equal ignored header classification", table.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.Name is "table-row-header-0-1" or "table-row-header-2-1")
            .Count(block => Equals(block.Tag, DiffKind.Equal) && block.Background is null) == 2);
        screenshot("line-transform-array-table.png");

        table.SelectCell(0, 1, 1); table.CellEditor.Text = "edited,raw"; TableClick(table, "セルを変更");
        var edited = pane.LeftEditor.Text ?? throw new InvalidOperationException("配列変換後の編集原文がありません。");
        Verify("array transform leaves original quoted cell editable", edited == "id,value\r\nA,\"edited,raw\"\r\n");
        TableClick(table, "セル編集を戻す"); Verify("array cell undo restores raw CSV", pane.LeftEditor.Text == rawLeft);
        TableClick(table, "セル編集をやり直す"); Verify("array cell redo restores raw edit", pane.LeftEditor.Text == edited);
        pump(pane.SaveAsync(false)); Verify("array save writes raw source bytes", File.ReadAllBytes(paths[0]).SequenceEqual(utf8.GetBytes(edited)));
        originals[0] = utf8.GetBytes(edited);
        var workspace = Path.Combine(directory, "workspace.json");
        pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new ComparisonWorkspace { Entries = [pane.CaptureProject()], ActiveEntryIndex = 0 }));
        var restored = Wait(WorkspaceStore.LoadWorkspaceAsync(workspace));
        pane.DiscardChanges(); pane.ApplyProject(restored.Entries.Single()); pump(pane.CompareProjectAsync()); Settle();
        Verify("array workspace restores expression and raw saved source", pane.CaptureProject().LineTransformRules.SequenceEqual([dynamicRule])
            && pane.LeftEditor.Text == edited && Table().Comparison.GetCell(0, 1, 1)?.Value == "edited,raw");
        var report = Path.Combine(directory, "report.html"); pump(pane.SaveReportAsync(report));
        var html = File.ReadAllText(report);
        Verify("array report keeps original cell contents", html.Contains("edited,raw", StringComparison.Ordinal)
            && html.Contains("middle,quoted", StringComparison.Ordinal) && html.Contains("right,quoted", StringComparison.Ordinal));

        var adopted = Table(); var adoptedState = pane.CaptureAdoptionState();
        pane.SetLineTransforms([new("@cs @noopt IfEach(true, Line, (1 / 0))", TablePaneMask.All)]);
        pump(pane.CompareProjectAsync()); Settle();
        Verify("eager IfEach untaken division rejects without changing view", ReferenceEquals(adopted, Table())
            && Equals(adoptedState, pane.CaptureAdoptionState()) && pane.LeftEditor.Text == edited);
        screenshot("line-transform-array-failure-preserved.png");
        var oldReady = pane.TableReadyForAdoption;
        try
        {
            pane.SetLineTransforms([dynamicRule]);
            pane.TableReadyForAdoption = () => Click(Caption(pane, "中止"));
            pump(pane.CompareProjectAsync()); Settle();
            Verify("array cancellation preserves adopted table", ReferenceEquals(adopted, Table()) && pane.LeftEditor.Text == edited);
            var later = new LineTransformRule("At(Array(Line), 0)", TablePaneMask.All);
            pane.TableReadyForAdoption = () => pane.SetLineTransforms([later]);
            pump(pane.CompareProjectAsync()); Settle();
            Verify("late array settings reject stale candidate and retain newer metadata", ReferenceEquals(adopted, Table())
                && pane.CaptureProject().LineTransformRules.SequenceEqual([later]) && pane.LeftEditor.Text == edited);
        }
        finally { pane.TableReadyForAdoption = oldReady; }
        for (var side = 0; side < paths.Length; side++) Verify("array flow preserves raw input bytes " + side, File.ReadAllBytes(paths[side]).SequenceEqual(originals[side]));

        pane.DiscardChanges(); var textRule = new LineTransformRule("At(Array(Line, LineNumber), 0)", TablePaneMask.Left | TablePaneMask.Right);
        pane.ApplyProject(new ComparisonProject { Mode = "Text", LeftPath = paths[0], RightPath = paths[2], LineTransformRules = [textRule] });
        pump(pane.CompareProjectAsync()); Settle();
        Verify("text dynamic array getter preserves physical source and side mask", pane.CurrentDiff is { HasDifferences: true }
            && pane.LeftEditor.Text == edited && pane.RightEditor.Text == rawRight && pane.CaptureProject().LineTransformRules.Single().Panes == textRule.Panes);
    }

    private static void WriteFixtureDiagnostics(string path, Action<string, bool, string> verify)
    {
        using var resource = typeof(HeadlessLineTransformArrayChecks).Assembly.GetManifestResourceStream("DiffBeacon.LineTransformArrayCases")
            ?? throw new InvalidOperationException("行変換の固定case resourceがありません。");
        using var cases = JsonDocument.Parse(resource);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        var count = 0; var boundaryCount = 0;
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
            if (item.GetProperty("id").GetString() == "arrays-value-eol-values")
            {
                boundaryCount++;
                var preserved = keys.SequenceEqual(content);
                verify("physical CR/LF array result rejects at public boundary", status == "error" && phase == "evaluate" && preserved, error);
                using var boundaryFile = new FileStream(Path.Combine(Path.GetDirectoryName(path)!, "public-boundary.json"), FileMode.CreateNew, FileAccess.Write);
                using var evidence = new Utf8JsonWriter(boundaryFile, new JsonWriterOptions { Indented = true });
                evidence.WriteStartObject(); evidence.WriteString("id", "arrays-value-eol-values"); evidence.WriteString("boundary", "public-plan-generated-physical-CRLF");
                evidence.WriteString("status", status); evidence.WriteString("errorPhase", phase); evidence.WriteString("error", error);
                evidence.WriteBoolean("originalsPreserved", preserved); evidence.WriteStartArray("lines");
                for (var index = 0; index < originals.Length; index++)
                {
                    evidence.WriteStartObject(); evidence.WriteString("originalHex", Hex(originals[index]));
                    evidence.WriteString("keyHex", Hex(keys[index] + endings[index])); evidence.WriteNumber("originalEol", Eol(endings[index]));
                    evidence.WriteNumber("keyEol", Eol(Ending(keys[index] + endings[index]))); evidence.WriteEndObject();
                }
                evidence.WriteEndArray(); evidence.WriteEndObject();
            }
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
        verify("all fixed oracle cases executed in app process", count == 90, path);
        verify("exactly one explicit public caller boundary was recorded", boundaryCount == 1, path);
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
