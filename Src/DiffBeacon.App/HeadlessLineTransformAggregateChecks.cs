using System.IO.Compression;
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

internal static class HeadlessLineTransformAggregateChecks
{
    private const string GoldenSha = "74e1f5b6bb58af0433cb0d198ee3360da1ebcd1f8845da9b5ce2fd0e2f549e91";
    private const string CasesSha = "a8e2bd941871ab9f77e737e2888c5936dc633e147d6bd02c82d6f268138ceaf9";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Functions = ["Average", "Sum", "Maximum", "Minimum"];

    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "line-transform-aggregates"));
        RequireExternal(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("集計検証には新規出力が必要です。");
        Directory.CreateDirectory(directory);
        void Verify(string name, bool ok, string detail = "") => check("line transform aggregate " + name, ok, detail);
        WriteNativeDiagnostics(directory, Verify);
        WriteEngineeringDiagnostics(directory, Verify);
        RunUi(window, pane, directory, Verify, screenshot, pump);
    }

    private static void WriteNativeDiagnostics(string directory, Action<string, bool, string> verify)
    {
        var goldenBytes = ReadEmbedded("DiffBeacon.LineTransformAggregateGolden");
        if (Hex(SHA256.HashData(goldenBytes)) != GoldenSha
            || Hex(SHA256.HashData(ReadEmbedded("DiffBeacon.LineTransformAggregateNativeCases"))) != CasesSha)
            throw new InvalidDataException("固定Native原本のSHAが一致しません。");
        using var plan = JsonDocument.Parse(ReadEmbedded("DiffBeacon.LineTransformAggregateCases"));
        using var probePlan = JsonDocument.Parse(ReadEmbedded("DiffBeacon.LineTransformAggregateProbes"));
        var goldenLines = Utf8.GetString(goldenBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r')).ToArray();
        var pending = new List<(string Id, bool Ok, string Detail)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var bridge = 0; var skipped = 0; var count = 0;
        using (var file = NewFile(directory, "diagnostics.ndjson"))
        {
            foreach (var item in plan.RootElement.GetProperty("cases").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var reference = item.GetProperty("nativeFirstStepReference");
                var lineNumber = reference.GetProperty("lineNumber").GetInt32();
                if (!seen.Add(id) || lineNumber != ++count || count > goldenLines.Length)
                    throw new InvalidDataException("Native141のIDまたは順序が一致しません。");
                var originalLine = goldenLines[lineNumber - 1];
                using var original = JsonDocument.Parse(originalLine);
                var native = original.RootElement.GetProperty("steps")[0];
                if (original.RootElement.GetProperty("id").GetString() != id
                    || reference.GetProperty("fileSha256").GetString() != GoldenSha
                    || Hex(SHA256.HashData(Utf8.GetBytes(originalLine))) != reference.GetProperty("lineSha256WithoutTerminator").GetString()
                    || !JsonElement.DeepEquals(native, reference.GetProperty("originalStep")))
                    throw new InvalidDataException("Native first step参照が一致しません。");
                var isBridge = item.GetProperty("classification").GetString() == "serialized-key/value-bridge-planned";
                string[]? keys = null; Exception? error = null; var budget = new LineTransformBudget(); var producerCompiled = false;
                var source = isBridge ? item.GetProperty("publicInvocationPlan").GetProperty("providerLines")
                    .EnumerateArray().Select(value => value.GetString()!).ToArray() : [];
                var before = source.ToArray();
                if (isBridge)
                {
                    bridge++;
                    try
                    {
                        var compiled = LineTransformPlan.Compile([new(item.GetProperty("expression").GetString()!, TablePaneMask.All)], budget);
                        producerCompiled = true;
                        keys = compiled.Transform(source, TablePaneMask.Left, false);
                    }
                    catch (Exception exception) when (ExpectedError(exception)) { error = exception; }
                }
                else
                {
                    if (item.GetProperty("classification").GetString() != "unrepresentable-not-executed"
                        || item.GetProperty("expression").ValueKind != JsonValueKind.Null
                        || string.IsNullOrWhiteSpace(item.GetProperty("reason").GetString()))
                        throw new InvalidDataException("Native37のskip理由または分類が不正です。");
                    skipped++;
                }
                var hasSerialized = native.TryGetProperty("serializedUtf8Hex", out var serialized) && serialized.ValueKind == JsonValueKind.String;
                if (isBridge && !hasSerialized) throw new InvalidDataException("公開bridgeの原本serialized bytesがありません。");
                var expected = hasSerialized ? serialized.GetString() : null;
                var actual = keys is { Length: > 0 } ? Hex(Utf8.GetBytes(keys[0])) : null;
                native.TryGetProperty("value", out var nativeValue);
                var originalKind = nativeValue.ValueKind == JsonValueKind.Object && nativeValue.TryGetProperty("kind", out var kind) ? kind.GetString() : null;
                var originalBits = nativeValue.ValueKind == JsonValueKind.Object && nativeValue.TryGetProperty("bitsHex", out var bits) ? bits.GetString() : null;
                var ok = !isBridge || (error is null && keys?.Length == source.Length && actual == expected
                    && keys!.All(key => Hex(Utf8.GetBytes(key)) == expected) && source.SequenceEqual(before));
                Row(file, json =>
                {
                    json.WriteString("id", id); json.WriteString("classification", item.GetProperty("classification").GetString());
                    json.WriteString("qualification", "public serialized-key bridge only; Native typed AST/context/cache/exception equivalence not observed");
                    json.WriteBoolean("contextEquivalent", false); json.WriteBoolean("actualTypedValueObserved", false);
                    json.WriteString("originalNativeKind", originalKind); json.WriteString("originalNumericBitsHex", originalBits);
                    json.WriteNull("actualNumericBitsHex"); json.WriteBoolean("executed", isBridge);
                    json.WriteBoolean("producerCompiled", producerCompiled);
                    json.WriteBoolean("skipped", !isBridge); json.WriteString("reason", item.GetProperty("reason").GetString());
                    json.WriteString("nativeGoldenSha256", GoldenSha); json.WriteNumber("nativeLineNumber", lineNumber);
                    json.WritePropertyName("nativeFirstStep"); native.WriteTo(json);
                    json.WritePropertyName("nativeCase"); item.GetProperty("nativeCase").WriteTo(json);
                    json.WritePropertyName("publicPlan"); item.WriteTo(json);
                    json.WriteString("actualFirstSerializedUtf8Hex", actual); json.WriteString("expectedFirstSerializedUtf8Hex", expected);
                    json.WriteBoolean("sourceRetained", source.SequenceEqual(before)); json.WriteBoolean("matched", isBridge && ok);
                    json.WriteNumber("workUsed", budget.WorkUsed); json.WriteNumber("outputCharacters", budget.OutputCharacters);
                    WriteError(json, error); WriteKeys(json, keys);
                });
                pending.Add((id, ok, isBridge ? "first serialized bytes only" : "unrepresentable; no execution or compatibility pass"));
            }
        }
        var probes = 0;
        using (var file = NewFile(directory, "arity-probes.ndjson"))
            foreach (var probe in probePlan.RootElement.GetProperty("probes").EnumerateArray())
            {
                probes++; var expression = probe.GetProperty("expression").GetString()!; Exception? error = null;
                try { LineTransformPlan.Compile([new(expression, TablePaneMask.All)], new LineTransformBudget()); }
                catch (Exception exception) when (ExpectedError(exception)) { error = exception; }
                Row(file, json => { json.WritePropertyName("plan"); probe.WriteTo(json); json.WriteBoolean("executed", true);
                    json.WriteString("qualification", "separate public argument-count contract; outside Native141"); WriteError(json, error); });
                pending.Add((probe.GetProperty("id").GetString()!, error is ArgumentException, expression));
            }
        verify("141 main / 104 bridge / 37 unrepresentable / 8 separate probes", count == 141 && goldenLines.Length == 141
            && bridge == 104 && skipped == 37 && probes == 8, directory);
        foreach (var row in pending) verify(row.Id, row.Ok, row.Detail);
    }

    private static void WriteEngineeringDiagnostics(string directory, Action<string, bool, string> verify)
    {
        var pending = new List<(string Id, bool Ok)>();
        using var file = NewFile(directory, "engineering.ndjson");
        void Observe(string id, IReadOnlyList<LineTransformRule> rules, string[] source, string[]? expected,
            Func<Exception?, bool>? acceptError = null, LineTransformBudget? budget = null, LineTransformPlan? compiled = null,
            CancellationToken token = default, TablePaneMask side = TablePaneMask.Left)
        {
            budget ??= new(); var before = source.ToArray(); string[]? keys = null; Exception? error = null;
            try { compiled ??= LineTransformPlan.Compile(rules, budget); keys = compiled.Transform(source, side, false, cancellationToken: token); }
            catch (Exception exception) when (ExpectedError(exception)) { error = exception; }
            var ok = source.SequenceEqual(before) && (acceptError is not null ? acceptError(error)
                : error is null && keys is not null && keys.SequenceEqual(expected!));
            Row(file, json =>
            {
                json.WriteString("id", id); json.WriteString("qualification", "separate public engineering; no Native golden expected value");
                json.WriteBoolean("sourceRetained", source.SequenceEqual(before)); json.WriteBoolean("passed", ok);
                json.WriteNumber("workUsed", budget.WorkUsed); json.WriteNumber("maximumWork", budget.MaximumWork);
                json.WriteNumber("outputCharacters", budget.OutputCharacters); json.WriteString("pane", side.ToString());
                json.WriteStartArray("expressions"); foreach (var rule in rules) json.WriteStringValue(rule.Expression); json.WriteEndArray();
                json.WriteStartArray("source"); foreach (var line in before) json.WriteStringValue(line); json.WriteEndArray();
                json.WriteStartArray("expectedKeys"); foreach (var key in expected ?? []) json.WriteStringValue(key); json.WriteEndArray();
                WriteKeys(json, keys); WriteError(json, error);
            });
            pending.Add((id, ok));
        }
        LineTransformRule[] Rules(params string[] bodies) => bodies.Select(body => new LineTransformRule("@cs @noopt " + body, TablePaneMask.All)).ToArray();
        var sum = Rules("Sum(ToInt(Line))"); var reusedBudget = new LineTransformBudget(); var reused = LineTransformPlan.Compile(sum, reusedBudget);
        Observe("transform-cache-first", sum, ["1", "2", "3"], ["6", "6", "6"], budget: reusedBudget, compiled: reused);
        Observe("transform-cache-second-pane", sum, ["10", "20"], ["30", "30"], budget: reusedBudget, compiled: reused, side: TablePaneMask.Right);
        Observe("rule-cache-isolation", Rules("Sum(ToInt(Line))", "Sum(ToInt(Line))"), ["1", "2", "3"], ["11", "11", "11"]);
        Observe("first-reached-provider-freeze", Rules("If(LineNumber = 1, \"100\", Sum(ToInt(Line)))"), ["1", "2", "3"], ["100", "105", "105"]);
        foreach (var function in Functions)
            Observe(function + "-false-skips-error-value", Rules(function + "(1/(LineNumber-LineNumber),false)"), ["a", "b"], ["none", "none"]);
        Observe("nested-aggregate", Rules("Sum(Sum(ToInt(Line)))"), ["1", "2", "3"], ["18", "18", "18"]);
        Observe("mixed-numeric-first-count", Rules("Average(Choose(LineNumber-1,2,\"x\",4))"), ["", "", ""], ["2.000000", "2.000000", "2.000000"]);
        Observe("mixed-string-first", Rules("Maximum(Choose(LineNumber-1,\"a\",9,\"z\"))"), ["", "", ""], ["z", "z", "z"]);
        Observe("mixed-bool-first", Rules("Average(Choose(LineNumber-1,true,9,false))"), ["", "", ""], ["0.333333", "0.333333", "0.333333"]);
        Observe("lower-int64-bound", Rules("Sum(ToInt(\"-9223372036854775808\"))"), [""], ["-9223372036854775808"]);
        Observe("safe-upper-double-integer", Rules("Maximum(ToInt(\"9223372036854774784\"))"), [""], ["9223372036854774784"]);
        foreach (var function in new[] { "Sum", "Maximum", "Minimum" })
            Observe(function + "-int64-upper-reject", Rules(function + "(ToInt(\"9223372036854775807\"))"), [""], null, error => error is OverflowException);
        Observe("nonfinite-result-reject", Rules("Average(1e308)"), ["", ""], null, error => error is ArithmeticException);
        var measuredBudget = new LineTransformBudget(); var measured = LineTransformPlan.Compile(sum, measuredBudget);
        measured.Transform(["1", "2", "3"], TablePaneMask.Left, false); var once = measuredBudget.WorkUsed;
        var limitedBudget = new LineTransformBudget(maximumWork: once); var limited = LineTransformPlan.Compile(sum, limitedBudget);
        Observe("shared-budget-first", sum, ["1", "2", "3"], ["6", "6", "6"], budget: limitedBudget, compiled: limited);
        Observe("shared-budget-second-reject", sum, ["1", "2", "3"], null, error => error is InvalidOperationException, limitedBudget, limited);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Observe("transform-cancel", sum, ["1", "2"], null, error => error is OperationCanceledException, token: cancelled.Token);
        Exception? compileError = null;
        try { LineTransformPlan.Compile(sum, new LineTransformBudget(), cancelled.Token); } catch (OperationCanceledException error) { compileError = error; }
        Row(file, json => { json.WriteString("id", "compile-cancel"); json.WriteString("qualification", "separate public engineering"); WriteError(json, compileError); });
        pending.Add(("compile-cancel", compileError is OperationCanceledException));
        file.Flush(); foreach (var row in pending) verify(row.Id, row.Ok, "engineering.ndjson");
    }

    private static void RunUi(MainWindow window, ComparisonPane pane, string directory,
        Action<string, bool, string> verify, Action<string> screenshot, Action<Task> pump)
    {
        using var uiEvidence = NewFile(directory, "ui-assertions.ndjson");
        var report = verify;
        verify = (name, ok, detail) => { Row(uiEvidence, json => { json.WriteString("name", name); json.WriteBoolean("passed", ok);
            json.WriteString("detail", detail); json.WriteString("qualification", "headless routed buttons; OS dialogs/pointer not observed"); }); uiEvidence.Flush(); report(name, ok, detail); };
        T Wait<T>(Task<T> task) { pump(task); return task.GetAwaiter().GetResult(); }
        TablePanel Table(ComparisonPane? target = null) => (target ?? pane).GetVisualDescendants().OfType<TablePanel>().Single();
        void TableClick(TablePanel table, string caption) { Click(Caption(table, caption)); if (table.PendingOperation is { } task) pump(task); Settle(); }
        string Write(string name, string value) { var path = Path.Combine(directory, name); using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write); file.Write(Utf8.GetBytes(value)); return path; }
        ComparisonProject? finalProject = null;
        foreach (var function in Functions)
        {
            var rule = new LineTransformRule("@cs @noopt If(LineNumber = 1, Line, " + function + "(LineNumber,LineNumber > 1))", TablePaneMask.All);
            var oldShown = LineTransformDialog.Shown; LineTransformDialog? shown = null;
            try
            {
                LineTransformDialog.Shown = dialog => shown = dialog; pane.SetLineTransforms([]); Click(Caption(pane, "行変換…")); Settle();
                var dialog = shown ?? throw new InvalidOperationException("集計dialogが開きません。");
                foreach (var expression in new[] { "", function + "()", function + "(1,2,3)" })
                {
                    dialog.Expression.Text = expression; Click(Named(dialog, "LineTransformAdd"));
                    verify(function + " dialog invalid " + expression, dialog.Rules.Count == 0 && !string.IsNullOrEmpty(dialog.Error.Text), "");
                }
                dialog.Expression.Text = rule.Expression; Click(Named(dialog, "LineTransformAdd"));
                Settle();
                using (var bitmap = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("集計dialogのPNGがありません。"))
                using (var png = NewFile(directory, function + "-dialog.png")) bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                Click(dialog.Accept); Click(dialog.Accept); Settle();
                verify(function + " duplicate apply", pane.CaptureProject().LineTransformRules.SequenceEqual([rule]), "");
                shown = null; Click(Caption(pane, "行変換…")); Settle(); dialog = shown ?? throw new InvalidOperationException("集計dialogが再開しません。");
                dialog.Expression.Text = function + "(LineNumber)"; Click(Named(dialog, "LineTransformAdd")); Click(dialog.Cancel); Settle();
                verify(function + " dialog cancel", pane.CaptureProject().LineTransformRules.SequenceEqual([rule]), "");
            }
            finally { LineTransformDialog.Shown = oldShown; }
            var raw = new[] { "id,value\r\nA,\"left,raw\"\r\nB,keep\r\n", "id,value\r\nA,\"middle,raw\"\r\nB,keep\r\n", "id,value\r\nA,\"right,raw\"\r\nB,keep\r\n" };
            var paths = raw.Select((text, side) => Write(function + "-" + side + ".csv", text)).ToArray();
            // 通常保存で上書きする入力には、既存bytesの復元可能な原本コピーを先に置く。
            Write(function + "-left-before-save.csv", raw[0]);
            var project = new ComparisonProject { Mode = "Table", LeftPath = paths[0], BasePath = paths[1], RightPath = paths[2], TableDelimiter = ',', TableQuote = '"', LineTransformRules = [rule] };
            pane.DiscardChanges(); pane.ApplyProject(project); pump(pane.CompareProjectAsync()); Settle(); var table = Table();
            verify(function + " three raw sources and ignored difference", table.Comparison.Documents.Count == 3 && !table.Comparison.HasDifferences
                && table.Comparison.GetCell(0, 1, 1)?.Value == "left,raw" && table.Comparison.GetCell(1, 1, 1)?.Value == "middle,raw"
                && table.Comparison.GetCell(2, 1, 1)?.Value == "right,raw" && pane.LeftEditor.Text == raw[0], "");
            table.SelectCell(0, 1, 1); table.CellEditor.Text = "edited,raw"; TableClick(table, "セルを変更");
            var edited = pane.LeftEditor.Text!; verify(function + " edit original cell", edited == raw[0].Replace("left,raw", "edited,raw", StringComparison.Ordinal), "");
            TableClick(table, "セル編集を戻す"); verify(function + " undo", pane.LeftEditor.Text == raw[0], "");
            TableClick(table, "セル編集をやり直す"); verify(function + " redo", pane.LeftEditor.Text == edited, "");
            var oldSaveObserved = pane.TextSaveTaskObserved; Task? saved = null;
            try
            {
                pane.TextSaveTaskObserved = (operation, task) => { oldSaveObserved?.Invoke(operation, task); if (operation == "左を保存") saved = task; };
                Click(Caption(pane, "左を保存"));
                pump(saved ?? throw new InvalidOperationException("集計Tableの実保存Taskが観測されません。")); Settle();
            }
            finally { pane.TextSaveTaskObserved = oldSaveObserved; }
            verify(function + " save raw bytes", File.ReadAllBytes(paths[0]).SequenceEqual(Utf8.GetBytes(edited)), "");
            var workspace = Path.Combine(directory, function + "-workspace.json");
            pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new ComparisonWorkspace { Entries = [pane.CaptureProject()], ActiveEntryIndex = 0 }));
            var restored = Wait(WorkspaceStore.LoadWorkspaceAsync(workspace)); pane.DiscardChanges(); pane.ApplyProject(restored.Entries.Single()); pump(pane.CompareProjectAsync()); Settle();
            verify(function + " workspace restoration", pane.CaptureProject().LineTransformRules.SequenceEqual([rule]) && pane.LeftEditor.Text == edited, "");
            var reportPath = Path.Combine(directory, function + "-report.html"); pump(pane.SaveReportAsync(reportPath));
            var html = File.ReadAllText(reportPath); verify(function + " report original cells", html.Contains("edited,raw", StringComparison.Ordinal) && html.Contains("middle,raw", StringComparison.Ordinal) && html.Contains("right,raw", StringComparison.Ordinal), "");
            var package = Path.Combine(directory, function + "-package.zip");
            pump(ComparisonPackage.CreateAsync(restored, package, new ComparisonPackageOptions(IncludeReport: true), sourceProject: workspace));
            var unpack = Path.Combine(directory, function + "-unpacked"); RequireExternal(unpack); Directory.CreateDirectory(unpack);
            ZipFile.ExtractToDirectory(package, unpack); var packaged = Wait(WorkspaceStore.LoadWorkspaceAsync(Path.Combine(unpack, "project.json")));
            pane.ApplyProject(packaged.Entries.Single()); pump(pane.CompareProjectAsync()); Settle();
            verify(function + " package reread", pane.CaptureProject().LineTransformRules.SequenceEqual([rule]) && pane.LeftEditor.Text == edited
                && Table().Comparison.Documents[1].SourceText == raw[1] && pane.RightEditor.Text == raw[2], "");
            pane.ApplyProject(restored.Entries.Single()); pump(pane.CompareProjectAsync()); Settle();
            pane.SetLineTransforms([new(rule.Expression.Replace("@noopt ", "", StringComparison.Ordinal), TablePaneMask.All)]); pump(pane.CompareProjectAsync()); Settle();
            verify(function + " defaultopt", !Table().Comparison.HasDifferences && pane.LeftEditor.Text == edited, "");
            pane.SetLineTransforms([rule]); pump(pane.CompareProjectAsync()); Settle(); var adopted = Table(); var state = pane.CaptureAdoptionState();
            pane.SetLineTransforms([new("@noopt " + function + "(1/(LineNumber-LineNumber))", TablePaneMask.All)]); pump(pane.CompareProjectAsync()); Settle();
            verify(function + " runtime failure preserves view", ReferenceEquals(adopted, Table()) && Equals(state, pane.CaptureAdoptionState()) && pane.LeftEditor.Text == edited, "");
            var ready = pane.TableReadyForAdoption;
            try
            {
                pane.SetLineTransforms([rule]); pane.TableReadyForAdoption = () => Click(Caption(pane, "中止")); pump(pane.CompareProjectAsync()); Settle();
                verify(function + " cancel preserves view", ReferenceEquals(adopted, Table()) && Equals(state, pane.CaptureAdoptionState()), "");
                var later = rule with { Expression = "@noopt " + function + "(LineNumber)" };
                pane.TableReadyForAdoption = () => pane.SetLineTransforms([later]); pump(pane.CompareProjectAsync()); Settle();
                verify(function + " stale completion preserves view", ReferenceEquals(adopted, Table()) && Equals(state, pane.CaptureAdoptionState()) && pane.CaptureProject().LineTransformRules.SequenceEqual([later]), "");
            }
            finally { pane.TableReadyForAdoption = ready; }
            var readOnly = restored.Entries.Single(); readOnly.LeftReadOnly = true; pane.DiscardChanges(); pane.ApplyProject(readOnly); pump(pane.CompareProjectAsync()); Settle();
            table = Table(); table.SelectCell(0, 1, 1); var cell = table.CellEditor.Text; var rejected = false;
            try { table.CellEditor.Text = "forbidden"; TableClick(table, "セルを変更"); }
            catch (InvalidOperationException error) when (error.Message == "読取り専用のセルは変更できません。") { rejected = true; }
            finally { table.CellEditor.Text = cell; }
            verify(function + " readonly real button", rejected && table.CellEditor.IsReadOnly && pane.LeftEditor.Text == edited, "");
            readOnly.LeftReadOnly = false; pane.ApplyProject(readOnly); pump(pane.CompareProjectAsync()); Settle(); finalProject = readOnly;
            verify(function + " original file bytes", File.ReadAllBytes(paths[0]).SequenceEqual(Utf8.GetBytes(edited))
                && File.ReadAllBytes(paths[1]).SequenceEqual(Utf8.GetBytes(raw[1])) && File.ReadAllBytes(paths[2]).SequenceEqual(Utf8.GetBytes(raw[2])), "");
            screenshot("line-transform-aggregate-" + function + "-table.png");
        }
        var width = window.Width; var height = window.Height;
        try
        {
            window.Width = 1280; window.Height = 850; Layout("normal", pane);
            window.Width = 850; window.Height = 550; Layout("minimum", pane);
            if (window.SessionPanes.Count > 37) throw new InvalidOperationException("37tab検証の新規window条件を満たしません。");
            while (window.SessionPanes.Count < 37) window.AddSession();
            var extra = window.ActivePane; extra.ApplyProject(finalProject!); pump(extra.CompareProjectAsync()); Settle();
            verify("37 comparison tabs", window.SessionPanes.Count == 37, ""); Layout("minimum-37-tabs", extra);
        }
        finally { window.Width = width; window.Height = height; Settle(); }

        void Layout(string name, ComparisonPane target)
        {
            Settle(); var table = Table(target); table.SelectCell(0, 1, 1);
            var toolbar = target.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Content is Control content && content.GetVisualDescendants().Contains(target.CompareButton));
            var tableToolbar = table.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Content is Control content && content.GetVisualDescendants().Contains(table.CellEditor));
            var results = new List<(string Label, bool Ok)>(); using var file = NewFile(directory, name + "-bounds.ndjson");
            foreach (var (label, control, scroll) in new (string, Control, ScrollViewer)[] {
                ("transform", Caption(target, "行変換…"), toolbar), ("left", target.LeftPath, toolbar), ("right", target.RightPath, toolbar),
                ("compare", target.CompareButton, toolbar), ("editor", table.CellEditor, tableToolbar), ("edit", Caption(table, "セルを変更"), tableToolbar),
                ("undo", Caption(table, "セル編集を戻す"), tableToolbar), ("redo", Caption(table, "セル編集をやり直す"), tableToolbar), ("save", Caption(target, "左を保存"), toolbar) })
            {
                control.BringIntoView(); Settle(); var point = control.TranslatePoint(default, scroll);
                var ok = point is { } p && control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0
                    && p.X >= -.01 && p.X + control.Bounds.Width <= scroll.Viewport.Width + .01
                    && p.Y >= -.01 && p.Y + control.Bounds.Height <= scroll.Viewport.Height + .01;
                Row(file, json => { json.WriteString("control", label); json.WriteNumber("windowWidth", window.Width); json.WriteNumber("windowHeight", window.Height);
                    json.WriteNumber("x", point?.X ?? -1); json.WriteNumber("y", point?.Y ?? -1); json.WriteNumber("width", control.Bounds.Width); json.WriteNumber("height", control.Bounds.Height);
                    json.WriteNumber("viewportWidth", scroll.Viewport.Width); json.WriteNumber("viewportHeight", scroll.Viewport.Height);
                    json.WriteNumber("offsetX", scroll.Offset.X); json.WriteNumber("offsetY", scroll.Offset.Y); json.WriteBoolean("reachable", ok); json.WriteBoolean("enabled", control.IsEnabled); });
                results.Add((label, ok)); screenshot("line-transform-aggregate-" + name + "-" + label + ".png");
            }
            file.Flush(); toolbar.Offset = default; tableToolbar.Offset = default; Settle(); foreach (var row in results) verify(name + " reachable " + row.Label, row.Ok, "bounds.ndjson");
        }
    }

    private static bool ExpectedError(Exception error) => error is ArgumentException or InvalidOperationException or FormatException or ArithmeticException or OperationCanceledException;
    private static void WriteError(Utf8JsonWriter json, Exception? error) { json.WriteString("actualExceptionType", error?.GetType().FullName); json.WriteString("actualErrorUtf8Hex", error is null ? null : Hex(Utf8.GetBytes(error.Message))); }
    private static void WriteKeys(Utf8JsonWriter json, string[]? keys)
    {
        json.WriteStartArray("actualKeys"); foreach (var key in keys ?? []) { json.WriteStartObject(); json.WriteString("text", key); json.WriteNumber("utf16Length", key.Length);
            var bytes = Utf8.GetBytes(key); json.WriteNumber("utf8Length", bytes.Length); json.WriteString("utf8Hex", Hex(bytes)); json.WriteString("sha256", Hex(SHA256.HashData(bytes))); json.WriteEndObject(); } json.WriteEndArray();
    }
    private static FileStream NewFile(string directory, string name) => new(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write);
    private static void Row(Stream file, Action<Utf8JsonWriter> write) { using (var json = new Utf8JsonWriter(file)) { json.WriteStartObject(); write(json); json.WriteEndObject(); json.Flush(); } file.WriteByte((byte)'\n'); }
    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
    private static byte[] ReadEmbedded(string name)
    {
        using var resource = typeof(HeadlessLineTransformAggregateChecks).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("集計固定resourceがありません: " + name);
        using var buffer = new MemoryStream(); resource.CopyTo(buffer); return buffer.ToArray();
    }
    private static void Settle() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Button Caption(Control owner, string caption) => owner.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, caption));
    private static Button Named(LineTransformDialog dialog, string name) => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
    private static void RequireExternal(string path)
    {
        var full = Path.GetFullPath(path); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("集計検証は外部出力が必要です。");
        }
        if (full.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase))) throw new IOException("禁止名の出力です。");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("リンク経由の出力です。");
    }
}
