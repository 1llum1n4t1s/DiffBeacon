using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessLineTransformTimestampLiteralChecks
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string Stamp = "d\"2024-01-02T03:04:05Z\"";
    private const string Qualification = "engineering/source-derived; timestamp literal full AST native execution not observed; headless routed controls, not OS dialogs/pointer";

    internal static void Run(MainWindow window, ComparisonPane pane, string output,
        Action<string, bool, string> check, Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "line-transform-timestamp-literals"));
        RequireExternal(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("日時検証には新規出力が必要です。");
        Directory.CreateDirectory(directory);
        using var evidence = NewFile(directory, "timestamp-assertions.ndjson");
        void Verify(string name, bool ok, string detail = "")
        {
            Row(evidence, json => { json.WriteString("name", name); json.WriteBoolean("passed", ok);
                json.WriteString("detail", detail); json.WriteString("qualification", Qualification); });
            evidence.Flush(); check("line transform timestamp literal " + name, ok, detail);
        }
        T Wait<T>(Task<T> task) { pump(task); return task.GetAwaiter().GetResult(); }
        string Write(string name, string value)
        {
            using var file = NewFile(directory, name); file.Write(Utf8.GetBytes(value));
            return Path.Combine(directory, name);
        }
        TablePanel Table() => pane.GetVisualDescendants().OfType<TablePanel>().Single();
        void Compare() { pump(pane.CompareProjectAsync()); Settle(); }

        // 原典full ASTのgoldenではなく、Appの自然なOS環境を注入する製品経路の検証。
        var cases = new (string Id, string Body, string Expected)[]
        {
            ("compare-d-D", Stamp + " = D\"2024-01-02T03:04:05Z\"", "true"),
            ("subtract", "d\"2024-01-02T03:04:06Z\" - " + Stamp, "1000000"),
            ("plus-integer", "(" + Stamp + " + 7) - " + Stamp, "7"),
            ("minus-integer", "(" + Stamp + " - 7) - " + Stamp, "-7"),
            ("range-exclusive", "InRange(" + Stamp + ", " + Stamp + ", " + Stamp + ")", "false"),
            ("within-inclusive", "IsWithin(" + Stamp + ", " + Stamp + ", " + Stamp + ")", "true"),
            ("minimum", "Minimum(" + Stamp + " + LineNumber) - " + Stamp, "1"),
            ("maximum", "Maximum(" + Stamp + " + LineNumber) - " + Stamp, "3"),
            ("array-count", "Len(Array(" + Stamp + ", D\"2024-01-02T03:04:06Z\"))", "2"),
            ("aggregate-count-contribution", "Average(If(LineNumber = 1, 4, If(LineNumber = 2, " + Stamp + ", None)))", "2.000000"),
            ("local-host", "d\"2024-01-02T03:04:05\" = D\"2024-01-02T03:04:05\"", "true")
        };
        using (var observations = NewFile(directory, "timestamp-public-observations.ndjson"))
        {
            foreach (var item in cases)
            {
                var rules = new[] { new LineTransformRule("@cs @noopt " + item.Body, TablePaneMask.All) };
                var raw = new[] { "first RAW", "second RAW", "third RAW" };
                var before = raw.ToArray();
                var plan = LineTransformPlan.Compile(rules, new LineTransformBudget(),
                    timestampEnvironment: LineTransformTimestampEnvironments.ForRules(rules));
                var keys = plan.Transform(raw, TablePaneMask.Left, false);
                var ok = keys.SequenceEqual(Enumerable.Repeat(item.Expected, 3)) && raw.SequenceEqual(before);
                Row(observations, json => { json.WriteString("id", item.Id); json.WriteString("expression", rules[0].Expression);
                    json.WriteString("expected", item.Expected); json.WriteStartArray("actualKeys");
                    foreach (var key in keys) json.WriteStringValue(key); json.WriteEndArray();
                    json.WriteBoolean("passed", ok); json.WriteString("qualification", Qualification);
                    json.WriteBoolean("nativeTypedObserved", false); json.WriteBoolean("nativeFullAstExecuted", false); });
                Verify("App host public " + item.Id, ok, "timestamp-public-observations.ndjson");
            }
        }

        var rule = new LineTransformRule("@cs @noopt If(LineNumber = 1, Line, (" + Stamp + " + 7) - " + Stamp + ")", TablePaneMask.All);
        var oldShown = LineTransformDialog.Shown; LineTransformDialog? shown = null;
        try
        {
            pane.SetLineTransforms([]); LineTransformDialog.Shown = dialog => shown = dialog;
            Click(Caption(pane, "行変換…")); Settle();
            var dialog = shown ?? throw new InvalidOperationException("日時ルールdialogがありません。");
            foreach (var invalid in new[] { "", "d\"invalid\"", "d'2024-01-02T03:04:05Z'", "D\"2024-01-02T03:04:05Z\0\"" })
            {
                dialog.Expression.Text = invalid; Click(Named(dialog, "LineTransformAdd"));
                Verify("dialog rejects " + Convert.ToHexString(Utf8.GetBytes(invalid)), dialog.Rules.Count == 0 && !string.IsNullOrEmpty(dialog.Error.Text));
            }
            dialog.Expression.Text = "@noopt " + Stamp + " = " + Stamp; Click(Named(dialog, "LineTransformAdd"));
            Verify("dialog add", dialog.Rules.Count == 1);
            dialog.RuleList.SelectedIndex = 0; dialog.Expression.Text = rule.Expression; Click(Named(dialog, "LineTransformChange"));
            Verify("dialog change", dialog.Rules.SequenceEqual([rule]));
            dialog.Expression.Text = "d\"invalid\""; Click(Named(dialog, "LineTransformChange"));
            Verify("invalid change retains candidate", dialog.Rules.SequenceEqual([rule]) && !string.IsNullOrEmpty(dialog.Error.Text));
            Settle();
            using (var bitmap = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("日時dialog PNGがありません。"))
            using (var png = NewFile(directory, "timestamp-dialog.png")) bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            using (var bounds = NewFile(directory, "timestamp-dialog-bounds.ndjson"))
            foreach (var control in new Control[] { dialog.Expression, dialog.Target, dialog.RuleList, Named(dialog, "LineTransformAdd"), dialog.Accept, dialog.Cancel })
            {
                control.BringIntoView(); Settle(); var point = control.TranslatePoint(default, dialog);
                var reachable = point is { } p && control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0
                    && p.X >= -.01 && p.Y >= -.01 && p.X + control.Bounds.Width <= dialog.ClientSize.Width + .01 && p.Y + control.Bounds.Height <= dialog.ClientSize.Height + .01;
                Row(bounds, json => { json.WriteString("control", control.Name); json.WriteNumber("x", point?.X ?? -1); json.WriteNumber("y", point?.Y ?? -1);
                    json.WriteNumber("width", control.Bounds.Width); json.WriteNumber("height", control.Bounds.Height);
                    json.WriteNumber("clientWidth", dialog.ClientSize.Width); json.WriteNumber("clientHeight", dialog.ClientSize.Height); json.WriteBoolean("reachable", reachable); });
                Verify("dialog reachable " + control.Name, reachable, "timestamp-dialog-bounds.ndjson");
            }
            Click(dialog.Accept); Click(dialog.Accept); Settle();
            Verify("duplicate apply", pane.CaptureProject().LineTransformRules.SequenceEqual([rule]));
            shown = null; Click(Caption(pane, "行変換…")); Settle(); dialog = shown ?? throw new InvalidOperationException("日時dialog再開失敗です。");
            dialog.Expression.Text = "@noopt " + Stamp + " - " + Stamp; Click(Named(dialog, "LineTransformAdd")); Click(dialog.Cancel); Settle();
            Verify("cancel preserves settings", pane.CaptureProject().LineTransformRules.SequenceEqual([rule]));
        }
        finally { LineTransformDialog.Shown = oldShown; }

        foreach (var mode in new[] { "Text", "Table" })
        {
            var raw = mode == "Table" ? new[] { "id,value\r\nA,left-RAW\r\nB,keep\r\n", "id,value\r\nA,middle-RAW\r\nB,keep\r\n", "id,value\r\nA,right-RAW\r\nB,keep\r\n" }
                : new[] { "header\r\nleft-RAW\r\nkeep\r\n", "header\r\nmiddle-RAW\r\nkeep\r\n", "header\r\nright-RAW\r\nkeep\r\n" };
            var paths = raw.Select((text, side) => Write("timestamp-" + mode + "-" + side + (mode == "Table" ? ".csv" : ".txt"), text)).ToArray();
            var hashes = paths.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
            var project = new ComparisonProject { Mode = mode, LeftPath = paths[0], BasePath = paths[1], RightPath = paths[2],
                TableDelimiter = ',', TableQuote = '"', LineTransformRules = [rule] };
            pane.DiscardChanges(); pane.ApplyProject(project); Compare();
            Verify(mode + " comparison retains raw", pane.LeftEditor.Text == raw[0] && pane.RightEditor.Text == raw[2]
                && (mode == "Text" ? pane.CurrentDiff is { HasDifferences: false } : !Table().Comparison.HasDifferences && Table().Comparison.Documents.Count == 3));
            Compare(); Verify(mode + " recompare", mode == "Text" ? pane.CurrentDiff is { HasDifferences: false } : !Table().Comparison.HasDifferences);
            screenshot("timestamp-" + mode.ToLowerInvariant() + ".png");
            var workspace = Path.Combine(directory, "timestamp-" + mode + "-workspace.json");
            pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new ComparisonWorkspace { Entries = [pane.CaptureProject()], ActiveEntryIndex = 0 }));
            var restored = Wait(WorkspaceStore.LoadWorkspaceAsync(workspace)); pane.ApplyProject(restored.Entries.Single()); Compare();
            Verify(mode + " workspace rules roundtrip", pane.CaptureProject().LineTransformRules.SequenceEqual([rule]));
            var serialized = File.ReadAllText(workspace);
            Verify(mode + " runtime host not persisted", !serialized.Contains("TimestampEnvironment", StringComparison.OrdinalIgnoreCase)
                && !serialized.Contains("CurrentTzdSeconds", StringComparison.OrdinalIgnoreCase) && !serialized.Contains("ProfileId", StringComparison.OrdinalIgnoreCase));
            var htmlPath = Path.Combine(directory, "timestamp-" + mode + "-report.html"); pump(pane.SaveReportAsync(htmlPath));
            var html = File.ReadAllText(htmlPath);
            Verify(mode + " report raw and rule", html.Contains("left-RAW", StringComparison.Ordinal) && html.Contains("right-RAW", StringComparison.Ordinal)
                && html.Contains("2024-01-02T03:04:05Z", StringComparison.Ordinal));
            if (mode == "Table")
            {
                var adopted = Table(); var state = pane.CaptureAdoptionState();
                pane.SetLineTransforms([new("@noopt (" + Stamp + " - " + Stamp + ") / (LineNumber - LineNumber)", TablePaneMask.All)]); Compare();
                Verify("runtime failure preserves adopted table", ReferenceEquals(adopted, Table()) && Equals(state, pane.CaptureAdoptionState()));
                var previousRules = pane.CaptureProject().LineTransformRules.ToArray(); var invalidRejected = false;
                try { pane.SetLineTransforms([new("d\"invalid\"", TablePaneMask.All)]); }
                catch (InvalidDataException) { invalidRejected = true; }
                Verify("invalid datetime preserves adopted table", invalidRejected && ReferenceEquals(adopted, Table()) && Equals(state, pane.CaptureAdoptionState())
                    && pane.CaptureProject().LineTransformRules.SequenceEqual(previousRules));
                var oldReady = pane.TableReadyForAdoption;
                try
                {
                    pane.SetLineTransforms([rule]); pane.TableReadyForAdoption = () => Click(Caption(pane, "中止")); Compare();
                    Verify("cancel preserves adopted table", ReferenceEquals(adopted, Table()) && Equals(state, pane.CaptureAdoptionState()));
                    var later = rule with { Expression = "@noopt " + Stamp + " = " + Stamp };
                    pane.TableReadyForAdoption = () => pane.SetLineTransforms([later]); Compare();
                    Verify("stale completion preserves adopted table", ReferenceEquals(adopted, Table()) && Equals(state, pane.CaptureAdoptionState())
                        && pane.CaptureProject().LineTransformRules.SequenceEqual([later]));
                }
                finally { pane.TableReadyForAdoption = oldReady; }
                project.LeftReadOnly = true; pane.ApplyProject(project); Compare(); var table = Table(); table.SelectCell(0, 1, 1);
                var cell = table.CellEditor.Text; table.CellEditor.Text = "forbidden"; var rejected = false;
                try { Click(Caption(table, "セルを変更")); if (table.PendingOperation is { } task) pump(task); Settle(); }
                catch (InvalidOperationException error) when (error.Message == "読取り専用のセルは変更できません。") { rejected = true; }
                Verify("readonly button preserves raw", rejected && table.CellEditor.IsReadOnly && pane.LeftEditor.Text == raw[0]); table.CellEditor.Text = cell;
            }
            Verify(mode + " input bytes preserved", paths.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).SequenceEqual(hashes));
            pane.SetLineTransforms([rule]);
        }
        // 別paneにも同じルールを渡し、ホストを含まない設定の実比較を確認する。
        var shared = pane.CaptureProject(); window.AddSession(); var extra = window.ActivePane;
        extra.ApplyProject(shared); pump(extra.CompareProjectAsync()); Settle();
        Verify("second pane settings", extra.CaptureProject().LineTransformRules.SequenceEqual([rule])
            && !extra.GetVisualDescendants().OfType<TablePanel>().Single().Comparison.HasDifferences);
        screenshot("timestamp-shared-settings.png");
    }

    private static FileStream NewFile(string directory, string name) => new(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write);
    private static void Row(Stream file, Action<Utf8JsonWriter> write)
    {
        using (var json = new Utf8JsonWriter(file)) { json.WriteStartObject(); write(json); json.WriteEndObject(); json.Flush(); }
        file.WriteByte((byte)'\n');
    }
    private static void Settle() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Button Caption(Control owner, string caption) => owner.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, caption));
    private static Button Named(LineTransformDialog dialog, string name) => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
    private static void RequireExternal(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("日時検証は外部出力が必要です。");
        }
        if (path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase))) throw new IOException("禁止名の出力です。");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("リンク経由の出力です。");
    }
}
