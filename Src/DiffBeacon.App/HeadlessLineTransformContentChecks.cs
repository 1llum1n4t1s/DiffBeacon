using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessLineTransformContentChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "line-transform-content"));
        RequireExternal(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Content自己検証には新規出力が必要です。");
        Directory.CreateDirectory(directory);
        void Verify(string name, bool passed) => check("line transform content " + name, passed, "");
        // 空の物理文書では通常の比較が式を評価しないため、公開planの空文字列rowでも確認する。
        foreach (var noopt in new[] { false, true })
        {
            foreach (var (expression, source, expected) in new (string, string[], string)[]
            {
                ("LineCount(Content)", [""], "0"),
                ("LineCount(Content)", ["a", "b"], "2"),
                ("Sublines(Content,0)", [""], ""),
                ("Sublines(Content,0)", ["a", "b"], "a\nb"),
                ("If(Content contains \"needle\",\"hit\",\"miss\")", ["first needle", "second"], "hit"),
                ("@cs If(Content contains \"NEEDLE\",\"hit\",\"miss\")", ["first needle", "second"], "miss"),
                ("@ci If(Content contains \"NEEDLE\",\"hit\",\"miss\")", ["first needle", "second"], "hit"),
                ("If(Content contains \"\",\"hit\",\"miss\")", ["needle"], "miss"),
                ("If(Content contains \"alpha\nbeta\",\"hit\",\"miss\")", ["alpha", "beta"], "miss"),
                ("LineCount(Array(Content,Array(Content),none))", ["a", "b"], "[2, none, none]"),
                ("Sublines(Content,ToInt(\"-9223372036854775808\"),1)", ["a", "b"], "a\n"),
                ("Sublines(Content,0,ToInt(\"9223372036854775807\"))", ["a", "b"], "a\nb")
            })
            {
                var plan = LineTransformPlan.Compile([new((noopt ? "@noopt " : "") + expression, TablePaneMask.All)], new());
                Verify(expression + " noopt=" + noopt, plan.Transform(source, TablePaneMask.Left, false).All(key => key == expected));
            }
        }
        var pathlessMarker = LineTransformPlan.Compile([new("Content", TablePaneMask.Left)], new())
            .Transform(["snapshot"], TablePaneMask.Left, false);
        Verify("pathless snapshot uses virtual marker", pathlessMarker.SequenceEqual(["<Content>"]));
        var original = new[] { "a", "b" };
        var sequential = LineTransformPlan.Compile([new("\"mutated\"", TablePaneMask.All), new("Sublines(Content,0)", TablePaneMask.All)], new());
        Verify("snapshot precedes sequential mutation", sequential.Transform(original, TablePaneMask.Left, false).SequenceEqual(["a\nb", "a\nb"])
            && original.SequenceEqual(["a", "b"]));
        foreach (var aggregate in new[] { "Minimum(Line)", "Minimum(Minimum(Line))", "Array(Minimum(Line))" })
        {
            var cached = LineTransformPlan.Compile([new("Sublines(Content,0,1)", TablePaneMask.All), new(aggregate, TablePaneMask.All)], new());
            var expected = aggregate.StartsWith("Array", StringComparison.Ordinal) ? "[a\n]" : "a\n";
            Verify("cached aggregate restores opaque multiline origin " + aggregate,
                cached.Transform(original, TablePaneMask.Left, false).SequenceEqual([expected, expected]));
        }
        long UntakenWork(string expression)
        {
            var budget = new LineTransformBudget(); var plan = LineTransformPlan.Compile([new(expression, TablePaneMask.All)], budget);
            var before = budget.WorkUsed;
            plan.Transform([new string('a', 8192)], TablePaneMask.Left, false);
            return budget.WorkUsed - before;
        }
        Verify("untaken content function does not scan snapshot", UntakenWork("If(false,LineCount(Content),Line)") == UntakenWork("If(false,Content,Line)"));
        CheckBudgetAndCancellation(Verify);
        CheckFourPaneOpaqueKeys();

        var utf8 = new UTF8Encoding(false, true);
        var left = Path.Combine(directory, "left.txt"); var right = Path.Combine(directory, "right.txt");
        const string rawLeft = "a\r\nb\r\n"; const string rawRight = "a\r\nc\r\n";
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
            foreach (var invalid in new[] { "LineCount()", "Sublines(Content)", "Prop(Content,\"size\")" })
            {
                dialog.Expression.Text = invalid;
                Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "LineTransformAdd"));
                Verify("invalid arity/unsupported property retains rules " + invalid, dialog.Rules.Count == 0 && !string.IsNullOrEmpty(dialog.Error.Text));
            }
            dialog.Expression.Text = "Sublines(Content,0,1)";
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "LineTransformAdd"));
            Verify("dialog accepts content expression", dialog.Rules.Count == 1);
            Click(dialog.Accept); Settle(); pump(pane.CompareProjectAsync()); Settle();
            Verify("opaque EOL key preserves original editors and rows", pane.CurrentDiff is { HasDifferences: false, Rows.Count: 2 }
                && pane.LeftEditor.Text == rawLeft && pane.RightEditor.Text == rawRight);
            screenshot("line-transform-content.png");
        }
        finally { LineTransformDialog.Shown = oldShown; }
        var workspace = Path.Combine(directory, "workspace.json");
        pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new ComparisonWorkspace { Entries = [pane.CaptureProject()], ActiveEntryIndex = 0 }));
        var loading = WorkspaceStore.LoadWorkspaceAsync(workspace); pump(loading);
        var restored = loading.GetAwaiter().GetResult().Entries.Single();
        Verify("workspace retains content expression", restored.LineTransformRules.SequenceEqual([new LineTransformRule("Sublines(Content,0,1)", TablePaneMask.All)]));
        pane.ApplyProject(restored); pane.DiscardChanges(); pump(pane.CompareProjectAsync()); Settle();
        Verify("workspace reapplies immutable original snapshot", pane.CurrentDiff is { HasDifferences: false } && pane.LeftEditor.Text == rawLeft);
        var leftMarker = "<FileContent:" + Path.GetFullPath(left) + ">";
        var rightMarker = "<FileContent:" + Path.GetFullPath(right) + ">";
        LineTransformRule[] leftPathRules = [new("Content", TablePaneMask.Left), new(Quote(leftMarker), TablePaneMask.Right)];
        pane.SetLineTransforms(leftPathRules); pump(pane.CompareProjectAsync()); Settle();
        Verify("actual pane uses exact left source path", pane.CurrentDiff is { HasDifferences: false, Rows.Count: 2 }
            && pane.LeftEditor.Text == rawLeft && pane.RightEditor.Text == rawRight);
        LineTransformRule[] rightPathRules = [new(Quote(rightMarker), TablePaneMask.Left), new("Content", TablePaneMask.Right)];
        pane.SetLineTransforms(rightPathRules); pump(pane.CompareProjectAsync()); Settle();
        Verify("actual pane uses exact right source path", pane.CurrentDiff is { HasDifferences: false, Rows.Count: 2 }
            && pane.LeftEditor.Text == rawLeft && pane.RightEditor.Text == rawRight);
        var previousDiff = pane.CurrentDiff;
        LineTransformRule[] aggregateRules = [new("Sublines(Content,0,1)", TablePaneMask.All), new("Minimum(Line)", TablePaneMask.All)];
        pane.SetLineTransforms(aggregateRules); pump(pane.CompareProjectAsync()); Settle();
        Verify("actual pane adopts all-mask cached multiline minimum", pane.CurrentDiff is { HasDifferences: false, Rows.Count: 2 }
            && !ReferenceEquals(pane.CurrentDiff, previousDiff) && pane.CaptureProject().LineTransformRules.SequenceEqual(aggregateRules)
            && pane.LeftEditor.Text == rawLeft && pane.RightEditor.Text == rawRight);
        screenshot("line-transform-content-aggregate-cache.png");
        Verify("source bytes preserved", File.ReadAllBytes(left).SequenceEqual(utf8.GetBytes(rawLeft)) && File.ReadAllBytes(right).SequenceEqual(utf8.GetBytes(rawRight)));

        void Settle() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        void CheckFourPaneOpaqueKeys()
        {
            var options = new ComparisonOptions { LineTransformRules = [new("Sublines(Content,0,1)", TablePaneMask.All)] };
            var legacyRejected = false;
            try { _ = new FourPaneGenerator(options); }
            catch (ArgumentException error) { legacyRejected = error.ParamName == "options" && error.Message.Contains("LegacyRawUtf8", StringComparison.Ordinal); }
            check("line transform content rejects raw-oracle profile", legacyRejected, "");
            var paths = new TextComparisonSourcePaths(Path.Combine(directory, "four-left.txt"),
                Path.Combine(directory, "four-base.txt"), Path.Combine(directory, "four-right.txt"));
            var settings = new FourPaneMaterializationSettings { SourcePaths = paths };
            foreach (var independent in new[] { false, true })
            {
                var name = independent ? "independent" : "ancestor";
                try
                {
                    var result = independent
                        ? FourPaneMaterialization.CreateIndependentThreeWay("a\nb", "a\nb", "a\nb", options, settings)
                        : FourPaneMaterialization.CreateThreeWay("a\nb", "a\nb", "a\nb", options, true, settings);
                    check("line transform content four-pane " + name, result.Alignment.InputCount == 3
                        && result.InitialBuffer.Text == "a\nb" && result.ExpandedText(result.InitialBuffer) == "a\nb", "");
                }
                catch (Exception error)
                {
                    check("line transform content four-pane " + name, false, error.ToString());
                }
            }

            LineTransformRule[] pairedRules =
            [
                new("If(LineNumber=1,Sublines(Content,0),Sublines(Content,2))", TablePaneMask.Left),
                new("If(LineNumber=1,Sublines(Content,1),Sublines(Content,0,1))", TablePaneMask.Right)
            ];
            var pairedPaths = new TextComparisonSourcePaths(Path.Combine(directory, "pair-left.txt"),
                right: Path.Combine(directory, "pair-right.txt"));
            var expectedLeft = new[] { "a\nb", "" };
            var expectedRight = new[] { "a", "b\n" };
            var pairedPlan = LineTransformPlan.Compile(pairedRules, new());
            var actualLeft = pairedPlan.TransformWithSourcePaths(["a", "b"], TablePaneMask.Left, false, pairedPaths);
            var actualRight = pairedPlan.TransformWithSourcePaths(["b", "a"], TablePaneMask.Right, false, pairedPaths);
            check("line transform content four-pane pair input rows", actualLeft.SequenceEqual(expectedLeft)
                && actualRight.SequenceEqual(expectedRight), "");
            try
            {
                var pair = FourPaneMaterialization.CreateTwoWay("a\nb", "b\na", new ComparisonOptions { LineTransformRules = pairedRules },
                    new FourPaneMaterializationSettings { SourcePaths = pairedPaths });
                check("line transform content four-pane pair keeps row boundaries",
                    pair.Alignment.Diffs.Count > 0 && pair.Choices.Descriptors.Count > 0, "");
            }
            catch (Exception error)
            {
                check("line transform content four-pane pair keeps row boundaries", false, error.ToString());
            }

            var boundaryRules = new LineTransformRule[]
            {
                new("If(LineNumber=1,Sublines(Content,1,1),\"same\")", TablePaneMask.Left),
                new("If(LineNumber=1,TrimRight(Sublines(Content,1,1)),\"same\")", TablePaneMask.Right)
            };
            try
            {
                var boundaryPair = FourPaneMaterialization.CreateTwoWay("X\na\rb", "Y\r\na\rb",
                    new ComparisonOptions { CompareLineEndings = true, LineTransformRules = boundaryRules });
                check("line transform content four-pane distinguishes equal bytes with different EOL boundary",
                    boundaryPair.Alignment.Diffs.Count > 0, "");
            }
            catch (Exception error)
            {
                check("line transform content four-pane distinguishes equal bytes with different EOL boundary", false, error.ToString());
            }
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static void CheckBudgetAndCancellation(Action<string, bool> verify)
    {
        LineTransformRule[] rules = [new("Sublines(Content,0)", TablePaneMask.All)];
        var measurement = new LineTransformBudget(); var measured = LineTransformPlan.Compile(rules, measurement);
        var compileOutput = measurement.OutputCharacters;
        measured.Transform(["a", "b"], TablePaneMask.Left, false);
        var budget = new LineTransformBudget(maximumOutputCharacters: measurement.OutputCharacters);
        var plan = LineTransformPlan.Compile(rules, budget);
        string[] source = ["a", "b"];
        var first = plan.Transform(source, TablePaneMask.Left, false);
        var rejected = false;
        try { plan.Transform(source, TablePaneMask.Right, false); } catch (InvalidOperationException) { rejected = true; }
        verify("all panes share output budget", compileOutput < measurement.OutputCharacters && first.Length == 2 && rejected && source.SequenceEqual(["a", "b"]));
        LineTransformRule[] containsRules = [new("If(Content contains \"needle\",\"hit\",\"miss\")", TablePaneMask.All)];
        var manyEmptyLines = Enumerable.Repeat("", 128).ToArray();
        var containsMeasurement = new LineTransformBudget();
        var containsMeasuredPlan = LineTransformPlan.Compile(containsRules, containsMeasurement);
        var containsCompileWork = containsMeasurement.WorkUsed;
        var containsExpected = containsMeasuredPlan.Transform(manyEmptyLines, TablePaneMask.Left, false);
        var containsTransformWork = containsMeasurement.WorkUsed - containsCompileWork;
        var containsLimitedBudget = new LineTransformBudget(maximumWork: containsCompileWork + containsTransformWork - 1);
        var containsLimitedPlan = LineTransformPlan.Compile(containsRules, containsLimitedBudget);
        var containsRejected = false;
        try { _ = containsLimitedPlan.Transform(manyEmptyLines, TablePaneMask.Left, false); }
        catch (InvalidOperationException) { containsRejected = true; }
        verify("Content contains scan uses shared work budget", containsTransformWork > 0 && containsRejected
            && containsExpected.SequenceEqual(Enumerable.Repeat("miss", manyEmptyLines.Length)));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var cancelled = false;
        try { measured.Transform(source, TablePaneMask.Base, false, cancellationToken: cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        verify("cancelled content transform preserves source", cancelled && source.SequenceEqual(["a", "b"]));
    }

    private static void RequireExternal(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "DiffBeacon.slnx"))) repository = repository.Parent;
        foreach (var root in new[] { repository?.FullName, Directory.GetCurrentDirectory(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), Environment.GetEnvironmentVariable("CODEX_HOME") })
            if (!string.IsNullOrEmpty(root) && (path.Equals(Path.GetFullPath(root), comparison)
                || path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, comparison)))
                throw new IOException("Content UI requires external output.");
        for (DirectoryInfo? parent = new(path); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked content output refused.");
    }
}
