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

internal static class HeadlessLineTransformUnicodeNormalizationChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "line-transform-unicode-normalization"));
        RequireExternal(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Normalization自己検証には新規出力が必要です。");
        Directory.CreateDirectory(directory);
        void Verify(string name, bool passed, string detail = "") => check("line transform normalization " + name, passed, detail);
        var cases = new (string Id, string Expression, string Expected)[]
        {
            ("default", "NormalizeUnicode(\"e\u0301\ufb03\uff21\")", "\u00e9\ufb03\uff21"),
            ("decompose", "NormalizeUnicode(\"\u00e9\ufb03\uff21\",\"nFd\")", "e\u0301\ufb03\uff21"),
            ("compat-compose", "NormalizeUnicode(\"e\u0301\ufb03\uff21\",5)", "\u00e9ffiA"),
            ("compat-decompose", "NormalizeUnicode(\"\u00e9\ufb03\uff21\",\"kd\")", "e\u0301ffiA"),
            ("empty", "NormalizeUnicode(\"\")", ""),
            ("none", "NormalizeUnicode(none)", "none"),
            ("boolean", "NormalizeUnicode(true)", "true"),
            ("number", "NormalizeUnicode(-42)", "-42"),
            ("double", "NormalizeUnicode(1.5)", "1.500000"),
            ("empty-array", "NormalizeUnicode(Array())", "[]"),
            ("array", "NormalizeUnicode(Array(\"e\u0301\",none,false,12))", "[\u00e9, none, false, 12]"),
            ("nested", "NormalizeUnicode(Array(Array(\"e\u0301\",true,none),none))", "[[\u00e9, true, none], none]"),
            ("nested-string", "StrLen(At(NormalizeUnicode(Array(Array(\"e\u0301\",true))),0))", "9"),
            ("non-string-form", "NormalizeUnicode(\"e\u0301\",2.0)", "\u00e9"),
            ("array-form", "NormalizeUnicode(\"e\u0301\",Array(2))", "\u00e9")
        };
        using (var file = File.Create(Path.Combine(directory, "observations.json")))
        using (var json = new Utf8JsonWriter(file, new() { Indented = true }))
        {
            json.WriteStartObject(); json.WriteString("qualification", "hand-authored public contract from NormalizeUnicodeFunc/getAsString/applyToScalarOrArray; no native golden; other OS uses managed Unicode data");
            json.WriteStartArray("cases");
            foreach (var item in cases)
                foreach (var noopt in new[] { false, true })
                {
                    var expression = "@cs " + (noopt ? "@noopt " : "") + item.Expression;
                    LineTransformRule[] rules = [new(expression, TablePaneMask.Left)];
                    var plan = LineTransformPlan.Compile(rules, new LineTransformBudget(),
                        stringEnvironment: LineTransformStringEnvironments.ForRules(rules));
                    string[] source = ["physical RAW"];
                    var actual = plan.Transform(source, TablePaneMask.Left, false).Single();
                    var passed = actual == item.Expected && source.Single() == "physical RAW";
                    Verify(item.Id + (noopt ? " noopt" : " optimized"), passed, expression);
                    json.WriteStartObject(); json.WriteString("id", item.Id); json.WriteString("expression", expression);
                    json.WriteString("expectedKey", item.Expected); json.WriteString("actualKey", actual);
                    json.WriteBoolean("passed", passed); json.WriteBoolean("nativeGolden", false); json.WriteEndObject();
                }
            json.WriteEndArray(); json.WriteEndObject();
        }

        // 最適化でも環境を呼ばないことと、form→value→変換の評価順を観測する。
        foreach (var noopt in new[] { false, true })
        {
            var environment = new CountingEnvironment();
            var prefix = "@cs " + (noopt ? "@noopt " : "");
            LineTransformPlan Compile(string expression) => LineTransformPlan.Compile([new(prefix + expression, TablePaneMask.All)],
                new LineTransformBudget(), stringEnvironment: environment);
            var plan = Compile("NormalizeUnicode(Array(\"e\u0301\",none,Array(\"e\u0301\",true)))");
            Verify("not folded " + noopt, environment.Values.Count == 0);
            var actual = plan.Transform(["raw"], TablePaneMask.Left, false).Single();
            Verify("one-level calls " + noopt, environment.Values.SequenceEqual([("e\u0301", NormalizationForm.FormC), ("[e\u0301, true]", NormalizationForm.FormC)])
                && actual == "[\u00e9, none, [\u00e9, true]]");
            environment.Values.Clear();
            actual = Compile("NormalizeUnicode(NormalizeUnicode(\"value\"),NormalizeUnicode(\"nfd\"))")
                .Transform(["raw"], TablePaneMask.Left, false).Single();
            Verify("form before value " + noopt, environment.Values.SequenceEqual([("nfd", NormalizationForm.FormC), ("value", NormalizationForm.FormC), ("value", NormalizationForm.FormD)])
                && actual == "value");
            environment.Values.Clear();
            actual = Compile("NormalizeUnicode(NormalizeUnicode(\"unreached\"),NormalizeUnicode(\" invalid\"))")
                .Transform(["raw"], TablePaneMask.Left, false).Single();
            Verify("invalid form skips value " + noopt, environment.Values.SequenceEqual([(" invalid", NormalizationForm.FormC)]) && actual == "none");
        }
        foreach (var directive in new[] { "@ci", "@cs" })
        {
            LineTransformRule[] rules = [new(directive + " NormalizeUnicode(\"E\u0301\",2)", TablePaneMask.All)];
            Verify("case directive independent " + directive, LineTransformPlan.Compile(rules, new LineTransformBudget(),
                stringEnvironment: LineTransformStringEnvironments.ForRules(rules)).Transform(["raw"], TablePaneMask.Left, false).Single() == "E\u0301");
        }
        CheckBudgetAndCancellation(Verify);
        var production = LineTransformStringEnvironments.ForRules([new("NormalizeUnicode(Line)", TablePaneMask.All)])!;
        Verify("invalid UTF16 preserved", production.NormalizeUnicode("\ud800", NormalizationForm.FormC) == "\ud800");
        Verify("explicit length includes embedded NUL", production.NormalizeUnicode("e\u0301\0e\u0301", NormalizationForm.FormC) == "\u00e9\0\u00e9");
        foreach (var scope in new[] { TablePaneMask.Left, TablePaneMask.Base, TablePaneMask.Right })
            foreach (var side in new[] { TablePaneMask.Left, TablePaneMask.Base, TablePaneMask.Right })
            {
                LineTransformRule[] rules = [new("@cs NormalizeUnicode(Line)", scope)];
                var actual = LineTransformPlan.Compile(rules, new LineTransformBudget(),
                    stringEnvironment: LineTransformStringEnvironments.ForRules(rules)).Transform(["e\u0301"], side, false).Single();
                Verify("scope " + scope + "/" + side, actual == (scope == side ? "\u00e9" : "e\u0301"));
            }

        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Settle() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        var utf8 = new UTF8Encoding(false, true);
        var left = Path.Combine(directory, "left.txt"); var right = Path.Combine(directory, "right.txt");
        const string rawLeft = "e\u0301\r\n"; const string rawRight = "\u00e9\r\n";
        File.WriteAllText(left, rawLeft, utf8); File.WriteAllText(right, rawRight, utf8);
        pane.DiscardChanges();
        pane.ApplyProject(new ComparisonProject { Mode = "Text", LeftPath = left, RightPath = right, LineTransformRules = [] });
        pane.DiscardChanges(); pump(pane.CompareProjectAsync()); Settle();
        Verify("raw comparison differs", pane.CurrentDiff is { HasDifferences: true });
        var oldShown = LineTransformDialog.Shown; LineTransformDialog? shown = null;
        LineTransformDialog.Shown = dialog => shown = dialog;
        try
        {
            Click(pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "行変換…"))); Settle();
            var dialog = shown ?? throw new InvalidOperationException("行変換ダイアログが開きませんでした。");
            foreach (var invalid in new[] { "NormalizeUnicode()", "NormalizeUnicode(Line,1,1)" })
            {
                dialog.Expression.Text = invalid;
                Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "LineTransformAdd"));
                Verify("invalid arity retains rules " + invalid, dialog.Rules.Count == 0 && !string.IsNullOrEmpty(dialog.Error.Text));
            }
            dialog.Target.SelectedIndex = 1; dialog.Expression.Text = "@cs NormalizeUnicode(Line)";
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "LineTransformAdd"));
            Verify("dialog accepts normalization with left scope", dialog.Rules.Count == 1 && dialog.Rules[0].Panes == TablePaneMask.Left);
            Click(dialog.Accept); Settle();
            pump(pane.CompareProjectAsync()); Settle();
            Verify("GUI transforms keys and preserves raw editors", pane.CurrentDiff is { HasDifferences: false }
                && pane.LeftEditor.Text == rawLeft && pane.RightEditor.Text == rawRight);
            screenshot("line-transform-unicode-normalization.png");
        }
        finally { LineTransformDialog.Shown = oldShown; }
        var workspace = Path.Combine(directory, "workspace.json");
        pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new ComparisonWorkspace { Entries = [pane.CaptureProject()], ActiveEntryIndex = 0 }));
        var loading = WorkspaceStore.LoadWorkspaceAsync(workspace); pump(loading);
        var restored = loading.GetAwaiter().GetResult().Entries.Single();
        Verify("workspace retains expression and scope", restored.LineTransformRules.SequenceEqual([new LineTransformRule("@cs NormalizeUnicode(Line)", TablePaneMask.Left)]));
        pane.ApplyProject(restored); pane.DiscardChanges(); pump(pane.CompareProjectAsync()); Settle();
        Verify("workspace reapplies normalization", pane.CurrentDiff is { HasDifferences: false } && pane.LeftEditor.Text == rawLeft);
        Verify("input bytes preserved", File.ReadAllBytes(left).SequenceEqual(utf8.GetBytes(rawLeft))
            && File.ReadAllBytes(right).SequenceEqual(utf8.GetBytes(rawRight)));
    }

    private static void CheckBudgetAndCancellation(Action<string, bool, string> verify)
    {
        LineTransformRule[] rules = [new("@cs NormalizeUnicode(Line,2)", TablePaneMask.All)];
        var measurement = new LineTransformBudget();
        var environment = new CountingEnvironment();
        var measured = LineTransformPlan.Compile(rules, measurement, stringEnvironment: environment);
        var compileOutput = measurement.OutputCharacters;
        measured.Transform(["\u00e9"], TablePaneMask.Left, false);
        var oneRunOutput = measurement.OutputCharacters - compileOutput;
        var budget = new LineTransformBudget(maximumOutputCharacters: compileOutput + oneRunOutput);
        var plan = LineTransformPlan.Compile(rules, budget, stringEnvironment: environment);
        string[] source = ["\u00e9"];
        var first = plan.Transform(source, TablePaneMask.Left, false).Single();
        var rejected = false;
        try { plan.Transform(source, TablePaneMask.Right, false); }
        catch (InvalidOperationException) { rejected = true; }
        verify("shared output budget rejects without source mutation", first == "e\u0301" && rejected && source.Single() == "\u00e9", "");
        using var cancel = new CancellationTokenSource();
        environment = new CountingEnvironment { AfterNormalize = cancel.Cancel };
        plan = LineTransformPlan.Compile(rules, new LineTransformBudget(), stringEnvironment: environment);
        var canceled = false;
        try { plan.Transform(source, TablePaneMask.Left, false, cancellationToken: cancel.Token); }
        catch (OperationCanceledException) { canceled = true; }
        verify("cancellation after environment escapes", canceled && environment.Values.Count == 1 && source.Single() == "\u00e9", "");
    }

    private sealed class CountingEnvironment : ILineTransformStringEnvironment
    {
        internal List<(string Value, NormalizationForm Form)> Values { get; } = [];
        internal Action? AfterNormalize { get; init; }
        public string ToLower(string value) => value.ToLowerInvariant();
        public string ToUpper(string value) => value.ToUpperInvariant();
        public string NormalizeUnicode(string value, NormalizationForm form)
        {
            Values.Add((value, form));
            var result = value.Normalize(form);
            AfterNormalize?.Invoke();
            return result;
        }
    }

    private static void RequireExternal(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var assemblyDirectory = AppContext.BaseDirectory;
        DirectoryInfo? repository = new(assemblyDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "DiffBeacon.slnx"))) repository = repository.Parent;
        foreach (var root in new[] { repository?.FullName, Directory.GetCurrentDirectory(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), Environment.GetEnvironmentVariable("CODEX_HOME") })
            if (!string.IsNullOrEmpty(root) && (path.Equals(Path.GetFullPath(root), comparison)
                || path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, comparison)))
                throw new IOException("Normalization UI requires external output.");
        for (DirectoryInfo? parent = new(path); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked normalization output refused.");
    }
}
