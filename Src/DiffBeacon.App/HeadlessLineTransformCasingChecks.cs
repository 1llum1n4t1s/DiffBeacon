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

internal static class HeadlessLineTransformCasingChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "line-transform-casing"));
        RequireExternal(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Casing自己検証には新規出力が必要です。");
        Directory.CreateDirectory(directory);
        void Verify(string name, bool passed, string detail = "") => check("line transform casing " + name, passed, detail);
        var cases = new (string Id, string Expression, string Expected)[]
        {
            ("lower", "ToLower(\"AbC XYZ\")", "abc xyz"),
            ("upper", "ToUpper(\"aBc xyz\")", "ABC XYZ"),
            ("empty", "ToUpper(\"\")", ""),
            ("none", "ToLower(none)", "none"),
            ("boolean", "ToUpper(true)", "TRUE"),
            ("number", "ToLower(-42)", "-42"),
            ("double", "ToUpper(1.5)", "1.500000"),
            ("empty-array", "ToLower(Array())", "[]"),
            ("array", "ToUpper(Array(\"Ab\",none,false,12))", "[AB, none, FALSE, 12]"),
            ("nested", "ToUpper(Array(Array(\"Ab\",true,none),none))", "[[AB, TRUE, NONE], none]"),
            ("nested-string", "StrLen(At(ToUpper(Array(Array(\"Ab\",true))),0))", "10")
        };
        using (var file = File.Create(Path.Combine(directory, "observations.json")))
        using (var json = new Utf8JsonWriter(file, new() { Indented = true }))
        {
            json.WriteStartObject(); json.WriteString("qualification", "hand-authored public contract from ToXFunc/getAsString/applyToScalarOrArray and managed StringValue; no native execution");
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
                    json.WriteBoolean("passed", passed); json.WriteBoolean("nativeExecuted", false); json.WriteEndObject();
                }
            json.WriteEndArray(); json.WriteEndObject();
        }

        // @nooptでprovider呼出し回数を観測し、直下要素一回／none無変換を確認する。
        foreach (var name in new[] { "ToLower", "ToUpper" })
        {
            var environment = new CountingEnvironment();
            LineTransformRule[] rules = [new("@noopt " + name + "(Array(\"Ab\",none,Array(\"Cd\",true)))", TablePaneMask.All)];
            var actual = LineTransformPlan.Compile(rules, new LineTransformBudget(), stringEnvironment: environment)
                .Transform(["raw"], TablePaneMask.Left, false).Single();
            Verify(name + " maps immediate non-none elements once", environment.Values.SequenceEqual(["Ab", "[Cd, true]"])
                && actual == "[Ab, none, [Cd, true]]", "Injected identity conversion observes inputs, not native locale mapping.");
        }
        foreach (var scope in new[] { TablePaneMask.Left, TablePaneMask.Base, TablePaneMask.Right })
            foreach (var side in new[] { TablePaneMask.Left, TablePaneMask.Base, TablePaneMask.Right })
            {
                LineTransformRule[] rules = [new("@cs ToLower(Line)", scope)];
                var actual = LineTransformPlan.Compile(rules, new LineTransformBudget(),
                    stringEnvironment: LineTransformStringEnvironments.ForRules(rules)).Transform(["AbC"], side, false).Single();
                Verify("scope " + scope + "/" + side, actual == (scope == side ? "abc" : "AbC"));
            }

        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Settle() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        var utf8 = new UTF8Encoding(false, true);
        var left = Path.Combine(directory, "left.txt"); var right = Path.Combine(directory, "right.txt");
        const string rawLeft = "AbC XYZ\r\n"; const string rawRight = "abc xyz\r\n";
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
            foreach (var invalid in new[] { "ToLower()", "ToUpper(Line,Line)" })
            {
                dialog.Expression.Text = invalid;
                Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "LineTransformAdd"));
                Verify("invalid arity retains rules " + invalid, dialog.Rules.Count == 0 && !string.IsNullOrEmpty(dialog.Error.Text));
            }
            dialog.Target.SelectedIndex = 1; dialog.Expression.Text = "@cs ToLower(Line)";
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "LineTransformAdd"));
            Verify("dialog accepts casing with left scope", dialog.Rules.Count == 1 && dialog.Rules[0].Panes == TablePaneMask.Left);
            Click(dialog.Accept); Settle();
            pump(pane.CompareProjectAsync()); Settle();
            Verify("GUI transforms keys and preserves raw editors", pane.CurrentDiff is { HasDifferences: false }
                && pane.LeftEditor.Text == rawLeft && pane.RightEditor.Text == rawRight);
            screenshot("line-transform-casing.png");
        }
        finally { LineTransformDialog.Shown = oldShown; }
        var workspace = Path.Combine(directory, "workspace.json");
        pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new ComparisonWorkspace { Entries = [pane.CaptureProject()], ActiveEntryIndex = 0 }));
        var loading = WorkspaceStore.LoadWorkspaceAsync(workspace); pump(loading);
        var restored = loading.GetAwaiter().GetResult().Entries.Single();
        Verify("workspace retains expression and scope", restored.LineTransformRules.SequenceEqual([new LineTransformRule("@cs ToLower(Line)", TablePaneMask.Left)]));
        pane.ApplyProject(restored); pane.DiscardChanges(); pump(pane.CompareProjectAsync()); Settle();
        Verify("workspace reapplies casing", pane.CurrentDiff is { HasDifferences: false } && pane.LeftEditor.Text == rawLeft);
        Verify("input bytes preserved", File.ReadAllBytes(left).SequenceEqual(utf8.GetBytes(rawLeft))
            && File.ReadAllBytes(right).SequenceEqual(utf8.GetBytes(rawRight)));
    }

    private sealed class CountingEnvironment : ILineTransformStringEnvironment
    {
        internal List<string> Values { get; } = [];
        public string ToLower(string value) { Values.Add(value); return value; }
        public string ToUpper(string value) { Values.Add(value); return value; }
        public string NormalizeUnicode(string value, NormalizationForm form) { Values.Add(value); return value.Normalize(form); }
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
                throw new IOException("Casing UI requires external output.");
        for (DirectoryInfo? parent = new(path); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked casing output refused.");
    }
}
