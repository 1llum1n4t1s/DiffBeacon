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

internal static class HeadlessLineTransformTimestampFunctionChecks
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string Qualification = "engineering/public App path; selected original Native function bodies observed separately; full AST, other locale, Native non-Syntax host exceptions and OS dialogs not qualified";

    internal static void Run(MainWindow window, ComparisonPane pane, string output,
        Action<string, bool, string> check, Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "line-transform-timestamp-functions"));
        RequireExternal(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("日時関数検証には新規出力が必要です。");
        Directory.CreateDirectory(directory);
        using var evidence = NewFile(directory, "timestamp-function-assertions.ndjson");
        void Verify(string name, bool ok, string detail = "")
        {
            Row(evidence, json => { json.WriteString("name", name); json.WriteBoolean("passed", ok);
                json.WriteString("detail", detail); json.WriteString("qualification", Qualification); });
            evidence.Flush(); check("line transform timestamp function " + name, ok, detail);
        }
        T Wait<T>(Task<T> task) { pump(task); return task.GetAwaiter().GetResult(); }
        string Write(string name, string value)
        {
            using var file = NewFile(directory, name); file.Write(Utf8.GetBytes(value));
            return Path.Combine(directory, name);
        }
        TablePanel Table() => pane.GetVisualDescendants().OfType<TablePanel>().Single();
        void Compare() { pump(pane.CompareProjectAsync()); Settle(); }

        // 自然なApp hostで実行し、Noneという正常値をcompile/transform例外と分ける。
        var cases = new (string Id, string Body, string? Expected, string? ErrorPhase)[]
        {
            ("timestamp-epoch", "todatetime(\"2024-01-02T03:04:05Z\") - d\"2024-01-02T03:04:05Z\"", "0", null),
            ("local-date-string", "todatestr(\"2000-01-02\")", "2000-01-02", null),
            ("local-timestamp-roundtrip", "todatestr(todatetime(\"2000-01-02\"))", "2000-01-02", null),
            ("invalid-timestamp-none", "todatetime(\"not a date\")", "none", null),
            ("timestamp-none", "todatetime(None)", "none", null),
            ("datestring-none", "todatestr(None)", "none", null),
            ("timestamp-empty-array", "todatetime(Array())", "[]", null),
            ("timestamp-nested-none", "todatetime(Array(None, 7, Array(\"2000-01-02\")))", "[none, none, none]", null),
            ("datestring-mixed-nested", "todatestr(Array(\"2000-01-02\", None, 7, false, todatetime(\"2000-01-02\"), Array(\"2000-01-02\")))", "[2000-01-02, none, none, none, 2000-01-02, none]", null),
            ("datestring-invalid", "todatestr(\"not a date\")", null, "transform"),
            ("datestring-invalid-array", "todatestr(Array(\"2000-01-02\", \"not a date\"))", null, "transform"),
            ("datestring-arity", "todatestr()", null, "compile")
        };
        using (var observations = NewFile(directory, "timestamp-function-public-observations.ndjson"))
        foreach (var item in cases)
        {
            var rules = new[] { new LineTransformRule("@cs @noopt " + item.Body, TablePaneMask.All) };
            var raw = new[] { "first RAW", "second RAW" }; var before = raw.ToArray();
            var phase = "compile"; string[]? keys = null; Exception? failure = null;
            try
            {
                var plan = LineTransformPlan.Compile(rules, new LineTransformBudget(),
                    timestampEnvironment: LineTransformTimestampEnvironments.ForRules(rules));
                phase = "transform"; keys = plan.Transform(raw, TablePaneMask.Left, false); phase = "complete";
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or ArithmeticException)
            { failure = error; }
            var ok = raw.SequenceEqual(before) && (item.ErrorPhase is null
                ? failure is null && phase == "complete" && keys is not null && keys.SequenceEqual(Enumerable.Repeat(item.Expected!, raw.Length))
                : failure is ArgumentException && phase == item.ErrorPhase && keys is null);
            Row(observations, json => { json.WriteString("id", item.Id); json.WriteString("expression", rules[0].Expression);
                json.WriteString("phase", phase); json.WriteString("expected", item.Expected); json.WriteString("expectedErrorPhase", item.ErrorPhase);
                json.WriteString("exceptionType", failure?.GetType().FullName); json.WriteString("exceptionMessage", failure?.Message);
                json.WriteStartArray("actualKeys"); foreach (var key in keys ?? []) json.WriteStringValue(key); json.WriteEndArray();
                json.WriteBoolean("inputPreserved", raw.SequenceEqual(before)); json.WriteBoolean("passed", ok);
                json.WriteString("qualification", Qualification); json.WriteBoolean("nativeFullAstExecuted", false); });
            Verify("App host " + item.Id, ok, "timestamp-function-public-observations.ndjson");
        }

        var textRule = new LineTransformRule("@cs @noopt If(LineNumber = 2, todatestr(todatetime(Line)), Line)", TablePaneMask.All);
        var tableRule = new LineTransformRule("@cs @noopt If(LineNumber = 1, Line, todatestr(todatetime(\"2000-01-02\")))", TablePaneMask.All);
        var oldShown = LineTransformDialog.Shown; LineTransformDialog? shown = null;
        try
        {
            pane.SetLineTransforms([]); LineTransformDialog.Shown = dialog => shown = dialog;
            Click(Caption(pane, "行変換…")); Settle();
            var dialog = shown ?? throw new InvalidOperationException("日時関数dialogがありません。");
            foreach (var invalid in new[] { "", "todatetime()", "todatestr(1, 2)" })
            {
                dialog.Expression.Text = invalid; Click(Named(dialog, "LineTransformAdd"));
                Verify("dialog rejects " + Convert.ToHexString(Utf8.GetBytes(invalid)), dialog.Rules.Count == 0 && !string.IsNullOrEmpty(dialog.Error.Text));
            }
            dialog.Expression.Text = "@noopt todatestr(todatetime(\"2000-01-02\"))"; Click(Named(dialog, "LineTransformAdd"));
            Verify("dialog add functions", dialog.Rules.Count == 1);
            dialog.RuleList.SelectedIndex = 0; dialog.Expression.Text = textRule.Expression; Click(Named(dialog, "LineTransformChange"));
            Verify("dialog change dynamic Line", dialog.Rules.SequenceEqual([textRule]));
            dialog.Expression.Text = "todatestr()"; Click(Named(dialog, "LineTransformChange"));
            Verify("invalid change retains candidate", dialog.Rules.SequenceEqual([textRule]) && !string.IsNullOrEmpty(dialog.Error.Text));
            Settle();
            using (var bitmap = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("日時関数dialog PNGがありません。"))
            using (var png = NewFile(directory, "timestamp-function-dialog.png")) bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            using (var bounds = NewFile(directory, "timestamp-function-dialog-bounds.ndjson"))
            foreach (var control in new Control[] { dialog.Expression, dialog.Target, dialog.RuleList, Named(dialog, "LineTransformAdd"), dialog.Accept, dialog.Cancel })
            {
                control.BringIntoView(); Settle(); var point = control.TranslatePoint(default, dialog);
                var reachable = point is { } p && control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0
                    && p.X >= -.01 && p.Y >= -.01 && p.X + control.Bounds.Width <= dialog.ClientSize.Width + .01 && p.Y + control.Bounds.Height <= dialog.ClientSize.Height + .01;
                Row(bounds, json => { json.WriteString("control", control.Name); json.WriteNumber("x", point?.X ?? -1); json.WriteNumber("y", point?.Y ?? -1);
                    json.WriteNumber("width", control.Bounds.Width); json.WriteNumber("height", control.Bounds.Height);
                    json.WriteNumber("clientWidth", dialog.ClientSize.Width); json.WriteNumber("clientHeight", dialog.ClientSize.Height); json.WriteBoolean("reachable", reachable); });
                Verify("dialog reachable " + control.Name, reachable, "timestamp-function-dialog-bounds.ndjson");
            }
            Click(dialog.Accept); Click(dialog.Accept); Settle();
            Verify("duplicate apply adopts one rule", pane.CaptureProject().LineTransformRules.SequenceEqual([textRule]));
            shown = null; Click(Caption(pane, "行変換…")); Settle(); dialog = shown ?? throw new InvalidOperationException("日時関数dialog再開失敗です。");
            dialog.Expression.Text = "@noopt todatestr(None)"; Click(Named(dialog, "LineTransformAdd")); Click(dialog.Cancel); Settle();
            Verify("cancel keeps adopted settings", pane.CaptureProject().LineTransformRules.SequenceEqual([textRule]));
        }
        finally { LineTransformDialog.Shown = oldShown; }

        foreach (var mode in new[] { "Text", "Table" })
        {
            var rule = mode == "Text" ? textRule : tableRule;
            var raw = mode == "Text" ? new[] { "header\r\n2000-01-02T03:04:05\r\nkeep\r\n", "header\r\n2000-01-02T10:11:12\r\nkeep\r\n", "header\r\n2000-01-02\r\nkeep\r\n" }
                : new[] { "id,value\r\nA,left-RAW\r\nB,keep\r\n", "id,value\r\nA,middle-RAW\r\nB,keep\r\n", "id,value\r\nA,right-RAW\r\nB,keep\r\n" };
            var paths = raw.Select((text, side) => Write("function-" + mode + "-" + side + (mode == "Table" ? ".csv" : ".txt"), text)).ToArray();
            var hashes = paths.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
            var project = new ComparisonProject { Mode = mode, LeftPath = paths[0], BasePath = paths[1], RightPath = paths[2],
                TableDelimiter = ',', TableQuote = '"', LineTransformRules = [rule] };
            pane.DiscardChanges(); pane.ApplyProject(project); Compare();
            bool EqualKeys() => mode == "Text" ? pane.CurrentDiff is { HasDifferences: false } : !Table().Comparison.HasDifferences && Table().Comparison.Documents.Count == 3;
            Verify(mode + " comparison retains raw", EqualKeys() && pane.LeftEditor.Text == raw[0] && pane.RightEditor.Text == raw[2]);
            Compare(); Verify(mode + " recompare", EqualKeys() && pane.CompareButton.IsEnabled);
            screenshot("timestamp-function-" + mode.ToLowerInvariant() + ".png");
            var workspace = Path.Combine(directory, "timestamp-function-" + mode + "-workspace.json");
            pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new ComparisonWorkspace { Entries = [pane.CaptureProject()], ActiveEntryIndex = 0 }));
            var restored = Wait(WorkspaceStore.LoadWorkspaceAsync(workspace)); pane.ApplyProject(restored.Entries.Single()); Compare();
            Verify(mode + " workspace rules roundtrip", pane.CaptureProject().LineTransformRules.SequenceEqual([rule]) && EqualKeys());
            var serialized = File.ReadAllText(workspace, Utf8);
            Verify(mode + " runtime host not persisted", !serialized.Contains("TimestampEnvironment", StringComparison.OrdinalIgnoreCase)
                && !serialized.Contains("CurrentTzdSeconds", StringComparison.OrdinalIgnoreCase) && !serialized.Contains("ProfileId", StringComparison.OrdinalIgnoreCase));
            var htmlPath = Path.Combine(directory, "timestamp-function-" + mode + "-report.html"); pump(pane.SaveReportAsync(htmlPath));
            var html = File.ReadAllText(htmlPath, Utf8);
            Verify(mode + " report raw and functions", html.Contains("todatetime", StringComparison.OrdinalIgnoreCase) && html.Contains("todatestr", StringComparison.OrdinalIgnoreCase)
                && html.Contains(mode == "Text" ? "2000-01-02T03:04:05" : "left-RAW", StringComparison.Ordinal)
                && html.Contains(mode == "Text" ? "2000-01-02" : "right-RAW", StringComparison.Ordinal));

            var state = pane.CaptureAdoptionState(); var diff = pane.CurrentDiff;
            var adoptedTable = mode == "Table" ? Table() : null;
            pane.SetLineTransforms([new("@noopt todatestr(\"not a date\")", TablePaneMask.All)]); Compare();
            Verify(mode + " invalid ToDateStr keeps view and restores controls", Equals(state, pane.CaptureAdoptionState())
                && (mode == "Text" ? ReferenceEquals(diff, pane.CurrentDiff) : ReferenceEquals(adoptedTable, Table()))
                && pane.LeftEditor.Text == raw[0] && pane.RightEditor.Text == raw[2] && pane.CompareButton.IsEnabled);
            screenshot("timestamp-function-" + mode.ToLowerInvariant() + "-invalid-preserved.png");
            if (mode == "Table")
            {
                var ready = pane.TableReadyForAdoption;
                try
                {
                    pane.SetLineTransforms([rule]); pane.TableReadyForAdoption = () => Click(Caption(pane, "中止")); Compare();
                    Verify("table cancel before adoption keeps view and restores controls", ReferenceEquals(adoptedTable, Table())
                        && Equals(state, pane.CaptureAdoptionState()) && pane.LeftEditor.Text == raw[0] && pane.RightEditor.Text == raw[2] && pane.CompareButton.IsEnabled);
                }
                finally { pane.TableReadyForAdoption = ready; }
            }
            pane.SetLineTransforms([rule]); Compare();
            Verify(mode + " recovery comparison", EqualKeys() && pane.CompareButton.IsEnabled);
            Verify(mode + " input bytes preserved", paths.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).SequenceEqual(hashes));
        }
        var shared = pane.CaptureProject(); window.AddSession(); var extra = window.ActivePane;
        extra.ApplyProject(shared); pump(extra.CompareProjectAsync()); Settle();
        Verify("second pane settings", extra.CaptureProject().LineTransformRules.SequenceEqual([tableRule])
            && !extra.GetVisualDescendants().OfType<TablePanel>().Single().Comparison.HasDifferences && extra.CompareButton.IsEnabled);
        screenshot("timestamp-function-shared-settings.png");
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
            if (path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("日時関数検証は外部出力が必要です。");
        }
        if (path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase))) throw new IOException("禁止名の出力です。");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("リンク経由の出力です。");
    }
}