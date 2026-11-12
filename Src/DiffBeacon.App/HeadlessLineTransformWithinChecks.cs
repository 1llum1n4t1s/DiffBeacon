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

internal static class HeadlessLineTransformWithinChecks
{
    private const string NativeSourceSha = "A2FCCA950BD5E5CFA48F3FF43E4436AE0F5FD55EFF9661DA1D5A979CC9051135";
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "line-transform-within"));
        RequireExternal(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("IsWithin UI requires fresh output.");
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false, true);
        void Verify(string name, bool success, string detail = "") => check("line transform within " + name, success, detail);
        void Settle() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static Button Caption(Control owner, string caption) => owner.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, caption));
        static Button Named(LineTransformDialog dialog, string name) => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
        WriteFixtureDiagnostics(directory, Verify);
        CheckSharedBudgetAndCancellation(directory, Verify);
        CheckBothStringComparisons(directory, Verify);
        var oldShown = LineTransformDialog.Shown;
        LineTransformDialog? shown = null;
        LineTransformDialog.Shown = dialog => shown = dialog;
        try
        {
            pane.SetLineTransforms([]); Click(Caption(pane, "行変換…")); Settle();
            var dialog = shown ?? throw new InvalidOperationException("IsWithin dialog missing.");
            foreach (var expression in new[] { "", "IsWithin()", "IsWithin(Line)", "IsWithin(Line, 1)", "IsWithin(Line, 1, 2, none)" })
            {
                dialog.Expression.Text = expression; Click(Named(dialog, "LineTransformAdd"));
                Verify("invalid dialog expression " + expression, dialog.Rules.Count == 0 && !string.IsNullOrEmpty(dialog.Error.Text));
            }
            dialog.Expression.Text = "@cs @noopt IsWithin(LineNumber, 1, 2)"; Click(Named(dialog, "LineTransformAdd"));
            SaveDialogFrame(dialog, directory, Verify);
            var intended = dialog.Rules.ToArray(); Click(dialog.Accept); Click(dialog.Accept); Settle();
            Verify("duplicate Apply", intended.Length == 1 && pane.CaptureProject().LineTransformRules.SequenceEqual(intended));
            shown = null; Click(Caption(pane, "行変換…")); Settle();
            dialog = shown ?? throw new InvalidOperationException("IsWithin dialog reopen missing.");
            dialog.Expression.Text = "IsWithin(LineNumber, 0, 3)"; Click(Named(dialog, "LineTransformAdd")); Click(dialog.Cancel); Settle();
            Verify("Cancel retains adopted metadata", pane.CaptureProject().LineTransformRules.SequenceEqual(intended));
        }
        finally { LineTransformDialog.Shown = oldShown; }

        // GUI派生ケース。固定native literal診断をTableへretargetしない。
        var rawLeft = "id,value\r\nA,\"left,raw\"\r\n";
        var rawMiddle = "id,value\r\nA,\"middle,raw\"\r\n";
        var rawRight = "id,value\r\nA,\"right,raw\"\r\n";
        string Write(string side, string text)
        {
            var path = Path.Combine(directory, side + ".csv"); using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            file.Write(utf8.GetBytes(text)); return path;
        }
        var paths = new[] { Write("left", rawLeft), Write("middle", rawMiddle), Write("right", rawRight) };
        var originals = paths.Select(File.ReadAllBytes).ToArray();
        var dynamicRule = new LineTransformRule("@cs @noopt If(LineNumber = 1, Line, IsWithin(Column2, \"\", \"zz\"))", TablePaneMask.All);
        var project = new ComparisonProject { Mode = "Table", LeftPath = paths[0], BasePath = paths[1], RightPath = paths[2],
            TableDelimiter = ',', TableQuote = '"', LineTransformRules = [dynamicRule] };
        pane.DiscardChanges(); pane.ApplyProject(project); pump(pane.CompareProjectAsync()); Settle();
        TablePanel Table() => pane.GetVisualDescendants().OfType<TablePanel>().Single();
        void TableClick(TablePanel target, string caption)
        { Click(Caption(target, caption)); if (target.PendingOperation is { } task) pump(task); Settle(); }
        var table = Table();
        Verify("dynamic three pane raw getter", table.Comparison.Documents.Count == 3 && !table.Comparison.HasDifferences
            && table.Comparison.IsIgnored(0, 1) && table.Comparison.GetCell(0, 1, 1)?.Value == "left,raw"
            && table.Comparison.GetCell(1, 1, 1)?.Value == "middle,raw" && table.Comparison.GetCell(2, 1, 1)?.Value == "right,raw");
        screenshot("line-transform-within-table.png");
        // 上限/equal-boundsのGUI派生検証。native literal72行は診断だけで評価する。
        var nativeTrueHex = ReadNativeSerializedHex("ascii-upper-cs");
        var nativeTrue = utf8.GetString(Convert.FromHexString(nativeTrueHex));
        var referenceRule = new LineTransformRule($"@cs @noopt If(LineNumber = 2, \"{nativeTrue}\", Line)", TablePaneMask.Base | TablePaneMask.Right);
        var guiPending = new List<(string Name, bool Ok, string Detail)>();
        using (var file = new FileStream(Path.Combine(directory, "getter-bounds.json"), FileMode.CreateNew, FileAccess.Write))
        using (var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject(); json.WriteString("qualification", "GUI engineering only; separate from 72 fixed native/public contexts"); json.WriteStartArray("cases");
            foreach (var (label, body) in new[] { ("upper-equal", "IsWithin(LineNumber, 1, 2)"), ("equal-column-bounds", "IsWithin(Column2, Column2, Column2)") })
            {
                var withinRule = new LineTransformRule("@cs @noopt If(LineNumber = 2, " + body + ", Line)", TablePaneMask.Left);
                var exclusiveRule = withinRule with { Expression = withinRule.Expression.Replace("IsWithin(", "InRange(", StringComparison.Ordinal) };
                pane.SetLineTransforms([withinRule, referenceRule]); pump(pane.CompareProjectAsync()); Settle();
                var inclusiveTable = Table(); var inclusiveDifferences = inclusiveTable.Comparison.HasDifferences;
                var inclusiveRaw = pane.LeftEditor.Text == rawLeft && inclusiveTable.Comparison.Documents[1].SourceText == rawMiddle && pane.RightEditor.Text == rawRight;
                var inclusiveCells = inclusiveTable.Comparison.GetCell(0, 1, 1)?.Value == "left,raw"
                    && inclusiveTable.Comparison.GetCell(1, 1, 1)?.Value == "middle,raw" && inclusiveTable.Comparison.GetCell(2, 1, 1)?.Value == "right,raw";
                screenshot("line-transform-within-" + label + "-included.png");
                pane.SetLineTransforms([exclusiveRule, referenceRule]); pump(pane.CompareProjectAsync()); Settle();
                var exclusiveDifferences = Table().Comparison.HasDifferences;
                var exclusiveRaw = pane.LeftEditor.Text == rawLeft && Table().Comparison.Documents[1].SourceText == rawMiddle && pane.RightEditor.Text == rawRight;
                screenshot("line-transform-within-" + label + "-exclusive.png");
                json.WriteStartObject(); json.WriteString("id", label); json.WriteString("withinExpression", withinRule.Expression);
                json.WriteString("inRangeExpression", exclusiveRule.Expression); json.WriteString("referenceExpression", referenceRule.Expression);
                json.WriteString("referenceNativeId", "ascii-upper-cs"); json.WriteString("referenceKeyHex", nativeTrueHex);
                json.WriteBoolean("withinHasDifferences", inclusiveDifferences); json.WriteBoolean("inRangeHasDifferences", exclusiveDifferences);
                json.WriteBoolean("inclusiveRawRetained", inclusiveRaw); json.WriteBoolean("exclusiveRawRetained", exclusiveRaw);
                json.WriteBoolean("allThreeRawCellsRetained", inclusiveCells); json.WriteEndObject();
                guiPending.Add((label + " inclusive versus exclusive", !inclusiveDifferences && exclusiveDifferences && inclusiveRaw && exclusiveRaw && inclusiveCells, withinRule.Expression));
            }
            json.WriteEndArray(); json.WriteEndObject(); json.Flush(); file.Flush();
        }
        foreach (var row in guiPending) Verify(row.Name, row.Ok, row.Detail);
        pane.SetLineTransforms([dynamicRule]); pump(pane.CompareProjectAsync()); Settle(); table = Table();
        table.SelectCell(0, 1, 1); table.CellEditor.Text = "edited,raw"; TableClick(table, "セルを変更");
        var edited = pane.LeftEditor.Text ?? throw new InvalidOperationException("Edited source missing.");
        Verify("cell edit preserves raw CSV", edited == "id,value\r\nA,\"edited,raw\"\r\n");
        TableClick(table, "セル編集を戻す"); Verify("undo", pane.LeftEditor.Text == rawLeft);
        TableClick(table, "セル編集をやり直す"); Verify("redo", pane.LeftEditor.Text == edited);
        pump(pane.SaveAsync(false)); Verify("save raw source", File.ReadAllBytes(paths[0]).SequenceEqual(utf8.GetBytes(edited)));
        originals[0] = utf8.GetBytes(edited);
        var workspace = Path.Combine(directory, "workspace.json");
        pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new ComparisonWorkspace { Entries = [pane.CaptureProject()], ActiveEntryIndex = 0 }));
        var load = WorkspaceStore.LoadWorkspaceAsync(workspace); pump(load); var restored = load.GetAwaiter().GetResult();
        pane.DiscardChanges(); pane.ApplyProject(restored.Entries.Single()); pump(pane.CompareProjectAsync()); Settle();
        Verify("workspace restores rules and raw source", pane.CaptureProject().LineTransformRules.SequenceEqual([dynamicRule]) && pane.LeftEditor.Text == edited);
        var report = Path.Combine(directory, "report.html"); pump(pane.SaveReportAsync(report));
        var html = File.ReadAllText(report); Verify("report raw cells", html.Contains("edited,raw", StringComparison.Ordinal) && html.Contains("middle,raw", StringComparison.Ordinal));
        pane.SetLineTransforms([new(dynamicRule.Expression.Replace("@noopt ", "", StringComparison.Ordinal), TablePaneMask.All)]);
        pump(pane.CompareProjectAsync()); Settle(); Verify("default optimization getter", !Table().Comparison.HasDifferences && pane.LeftEditor.Text == edited);
        pane.SetLineTransforms([dynamicRule]); pump(pane.CompareProjectAsync()); Settle();
        var adopted = Table(); var state = pane.CaptureAdoptionState();
        pane.SetLineTransforms([new("@cs @noopt IsWithin(None, 0, (1 / 0))", TablePaneMask.All)]);
        pump(pane.CompareProjectAsync()); Settle();
        Verify("eager upper bound failure preserves adopted view", ReferenceEquals(adopted, Table()) && Equals(state, pane.CaptureAdoptionState()) && pane.LeftEditor.Text == edited);
        screenshot("line-transform-within-failure-preserved.png");
        var oldReady = pane.TableReadyForAdoption;
        try
        {
            pane.SetLineTransforms([dynamicRule]); pane.TableReadyForAdoption = () => Click(Caption(pane, "中止"));
            pump(pane.CompareProjectAsync()); Settle(); Verify("cancel preserves adopted table", ReferenceEquals(adopted, Table()) && Equals(state, pane.CaptureAdoptionState()) && pane.LeftEditor.Text == edited);
            var later = new LineTransformRule("@cs @noopt IsWithin(LineNumber, 1, 2)", TablePaneMask.All);
            pane.TableReadyForAdoption = () => pane.SetLineTransforms([later]);
            pump(pane.CompareProjectAsync()); Settle(); Verify("generation discards stale candidate", ReferenceEquals(adopted, Table()) && Equals(state, pane.CaptureAdoptionState())
                && pane.CaptureProject().LineTransformRules.SequenceEqual([later]) && pane.LeftEditor.Text == edited);
        }
        finally { pane.TableReadyForAdoption = oldReady; }
        pane.DiscardChanges(); var readOnlyProject = restored.Entries.Single(); readOnlyProject.LeftReadOnly = true;
        pane.ApplyProject(readOnlyProject); pump(pane.CompareProjectAsync()); Settle();
        table = Table(); table.SelectCell(0, 1, 1); 
        var adoptedCell = table.CellEditor.Text;
        try
        {
            table.CellEditor.Text = "forbidden";
            var readOnlyRejected = false;
            try { TableClick(table, "セルを変更"); }
            catch (InvalidOperationException error) when (error.Message == "読取り専用のセルは変更できません。") { readOnlyRejected = true; }
            Verify("readonly edit button preserves source", readOnlyRejected && table.CellEditor.IsReadOnly && pane.LeftEditor.Text == edited);
        }
        finally
        {
            // headlessの注入値は失敗時も戻し、後続の再比較を阻害しない。
            table.CellEditor.Text = adoptedCell;
        }
        readOnlyProject.LeftReadOnly = false; pane.ApplyProject(readOnlyProject); pump(pane.CompareProjectAsync()); Settle(); Table().SelectCell(0, 1, 1);
        for (var side = 0; side < paths.Length; side++) Verify("input bytes " + side, File.ReadAllBytes(paths[side]).SequenceEqual(originals[side]));
        var originalWidth = window.Width; var originalHeight = window.Height;
        try
        {
            window.Width = 1280; window.Height = 850; Layout("normal");
            window.Width = 850; window.Height = 550; Layout("minimum");
            for (var index = 0; index < 12; index++) window.AddSession();
            var extra = window.ActivePane; extra.ApplyProject(readOnlyProject); pump(extra.CompareProjectAsync()); Settle();
            Layout("minimum-many-tabs", extra);
        }
        finally { window.Width = originalWidth; window.Height = originalHeight; Settle(); }

        void Layout(string name, ComparisonPane? target = null)
        {
            var current = target ?? pane; Settle();
            var currentTable = current.GetVisualDescendants().OfType<TablePanel>().Single();
            currentTable.SelectCell(0, 1, 1);
            var toolbar = current.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Content is Control content && content.GetVisualDescendants().Contains(current.CompareButton));
            var tableToolbar = currentTable.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Content is Control content && content.GetVisualDescendants().Contains(currentTable.CellEditor));
            using var file = new FileStream(Path.Combine(directory, name + "-bounds.json"), FileMode.CreateNew, FileAccess.Write);
            using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
            var pending = new List<(string Label, bool Ok, string Detail)>(); json.WriteStartArray();
            foreach (var (label, control, scroll) in new (string, Control, ScrollViewer)[] {
                ("transform", Caption(current, "行変換…"), toolbar), ("left input", current.LeftPath, toolbar), ("right input", current.RightPath, toolbar),
                ("compare", current.CompareButton, toolbar), ("editor", currentTable.CellEditor, tableToolbar),
                ("edit", Caption(currentTable, "セルを変更"), tableToolbar), ("undo", Caption(currentTable, "セル編集を戻す"), tableToolbar),
                ("redo", Caption(currentTable, "セル編集をやり直す"), tableToolbar) })
            {
                control.BringIntoView(); Settle(); var point = control.TranslatePoint(default, scroll);
                var reachable = point is { } p && control.IsEffectivelyVisible && control.Bounds.Height > 0 && p.Y >= -.01 && p.Y + control.Bounds.Height <= scroll.Viewport.Height + .01;
                var detail = FormattableString.Invariant($"x={point?.X};y={point?.Y};height={control.Bounds.Height};viewport={scroll.Viewport};offset={scroll.Offset}");
                pending.Add((label, reachable, detail)); json.WriteStartObject(); json.WriteString("control", label); json.WriteNumber("windowWidth", window.Width); json.WriteNumber("windowHeight", window.Height);
                json.WriteNumber("x", point?.X ?? -1); json.WriteNumber("y", point?.Y ?? -1); json.WriteNumber("width", control.Bounds.Width); json.WriteNumber("height", control.Bounds.Height);
                json.WriteNumber("viewportWidth", scroll.Viewport.Width); json.WriteNumber("viewportHeight", scroll.Viewport.Height); json.WriteNumber("offsetX", scroll.Offset.X); json.WriteNumber("offsetY", scroll.Offset.Y);
                json.WriteBoolean("visible", control.IsEffectivelyVisible); json.WriteBoolean("enabled", control.IsEnabled); json.WriteBoolean("reachable", reachable); json.WriteString("detail", detail); json.WriteEndObject();
            }
            json.WriteEndArray(); json.Flush(); file.Flush(); screenshot("line-transform-within-" + name + ".png"); toolbar.Offset = default; tableToolbar.Offset = default; Settle();
            foreach (var row in pending) Verify(name + " reachable " + row.Label, row.Ok, row.Detail);
        }
    }
    private static void SaveDialogFrame(LineTransformDialog dialog, string directory, Action<string, bool, string> verify)
    {
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var bitmap = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("範囲ダイアログの描画に失敗しました。");
        using (bitmap)
        using (var png = new FileStream(Path.Combine(directory, "dialog.png"), FileMode.CreateNew, FileAccess.Write))
            bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        using var file = new FileStream(Path.Combine(directory, "dialog-bounds.json"), FileMode.CreateNew, FileAccess.Write);
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        json.WriteStartArray();
        foreach (var control in new Control[] { dialog.Expression, dialog.Target, dialog.RuleList, dialog.Accept, dialog.Cancel })
        {
            control.BringIntoView(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var point = control.TranslatePoint(default, dialog);
            var reachable = point is { } p && control.IsEffectivelyVisible && control.Bounds.Height > 0
                && p.Y >= -.01 && p.Y + control.Bounds.Height <= dialog.ClientSize.Height + .01;
            verify("actual IsWithin dialog reachable " + control.Name, reachable, "");
            json.WriteStartObject(); json.WriteString("control", control.Name); json.WriteNumber("y", point?.Y ?? -1);
            json.WriteNumber("height", control.Bounds.Height); json.WriteNumber("clientHeight", dialog.ClientSize.Height);
            json.WriteBoolean("reachable", reachable); json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    private static void WriteFixtureDiagnostics(string directory, Action<string, bool, string> verify)
    {
        var assembly = typeof(HeadlessLineTransformWithinChecks).Assembly;
        using var resource = assembly.GetManifestResourceStream("DiffBeacon.LineTransformWithinCases") ?? throw new InvalidOperationException("IsWithin public plan missing.");
        using var plan = JsonDocument.Parse(resource);
        using var goldenResource = assembly.GetManifestResourceStream("DiffBeacon.LineTransformWithinGolden") ?? throw new InvalidOperationException("IsWithin native golden missing.");
        using var reader = new StreamReader(goldenResource, Encoding.UTF8);
        var golden = new Dictionary<string, string?>(StringComparer.Ordinal);
        var order = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            using var row = JsonDocument.Parse(line); var id = row.RootElement.GetProperty("id").GetString()!;
            golden.Add(id, row.RootElement.GetProperty("serializedUtf8Hex").GetString()); order.Add(id);
        }
        var commit = plan.RootElement.GetProperty("sourceCommit").GetString()!;
        var pending = new List<(string Name, bool Ok, string Detail)>();
        var counts = new int[3]; var bridgeCount = 0; var seen = new HashSet<string>(StringComparer.Ordinal); var index = 0;
        var actualPath = Path.Combine(directory, "diagnostics.ndjson");
        var probesPath = Path.Combine(directory, "probes.ndjson");
        using (var actual = new FileStream(actualPath, FileMode.CreateNew, FileAccess.Write))
        {
            foreach (var item in plan.RootElement.GetProperty("cases").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                pending.Add(("fixed ID order " + id, seen.Add(id) && index < order.Count && order[index] == id, "")); index++;
                Execute(item, actual, false);
            }
        }
        var probeCount = 0;
        using (var probes = new FileStream(probesPath, FileMode.CreateNew, FileAccess.Write))
            foreach (var item in plan.RootElement.GetProperty("publicConstructorProbes").EnumerateArray()) { Execute(item, probes, true); probeCount++; }
        pending.Add(("72 fixed IDs", index == 72 && seen.Count == 72 && order.Count == 72, actualPath));
        pending.Add(("59 native keys 3 arity rejections 10 unsupported", counts[0] == 59 && counts[1] == 3 && counts[2] == 10, actualPath));
        pending.Add(("separate arity4 probe", probeCount == 1, probesPath));
        pending.Add(("12 value/key-only bridges", bridgeCount == 12, actualPath));
        foreach (var row in pending) verify(row.Name, row.Ok, row.Detail);

        void Execute(JsonElement item, Stream stream, bool probe)
        {
            var id = item.GetProperty("id").GetString()!; var expression = item.GetProperty("expression").GetString();
            var classification = probe ? "public-constructor-probe" : item.GetProperty("classification").GetString()!;
            var context = item.GetProperty("context"); var original = context.GetProperty("sourceLineHex").GetString()!;
            var lineCount = context.GetProperty("sourceLineCount").GetInt32(); var rowIndex = context.GetProperty("rowIndex").GetInt32();
            var outcome = item.GetProperty("expectedPublicOutcome").GetString();
            var key = original; var status = "ok"; var phase = "compile"; var exceptionType = ""; var error = "";
            var budget = new LineTransformBudget(); var executed = false;
            var source = new[] { new UTF8Encoding(false, true).GetString(Convert.FromHexString(original)) };
            var fixedContext = source.Length == 1 && source[0] == "" && lineCount == 1 && rowIndex == 0
                && context.EnumerateObject().Count() == 9 && context.GetProperty("kind").GetString() == "literal-no-getter"
                && context.GetProperty("outerEol").GetInt32() == 0 && !context.GetProperty("tableMode").GetBoolean()
                && context.GetProperty("pane").GetString() == "Left" && context.GetProperty("targetMask").GetString() == "Left"
                && context.GetProperty("forbidRetargeting").GetBoolean();
            if (!fixedContext) throw new InvalidOperationException("Refusing to retarget fixed IsWithin context " + id);
            if (!probe && item.GetProperty("publicBridges").GetArrayLength() > 0) bridgeCount++;
            pending.Add(("frozen EOF context " + id, fixedContext, ""));
            if (expression is null)
            {
                // 公開parserでは表現不能。Compile/Transformを一切呼ばない。
                status = "unsupported"; phase = "unrepresentable"; exceptionType = "UnrepresentableAdapterType";
                if (!probe) counts[2]++;
            }
            else
            {
                try
                {
                    executed = true;
                    var compiled = LineTransformPlan.Compile([new(expression, TablePaneMask.Left)], budget);
                    phase = "evaluate";
                    var keys = compiled.Transform(source, TablePaneMask.Left, false);
                    if (keys.Length != lineCount) throw new InvalidOperationException("Source line count changed.");
                    key = Convert.ToHexString(new UTF8Encoding(false, true).GetBytes(keys[rowIndex])).ToLowerInvariant(); phase = "";
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or ArithmeticException)
                { status = "error"; exceptionType = exception.GetType().Name; error = exception.Message; }
                if (!probe) counts[outcome == "serialized-value-equivalence" ? 0 : 1]++;
            }
            var success = outcome switch
            {
                "serialized-value-equivalence" => status == "ok" && phase == "" && exceptionType == "" && golden.TryGetValue(id, out var expected) && key == expected,
                "compile-constructor-rejection" => status == "error" && phase == "compile" && exceptionType == "ArgumentException" && key == original,
                _ => expression is null && !executed && status == "unsupported" && phase == "unrepresentable" && key == original
            };
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject(); json.WriteString("id", id); json.WritePropertyName("context"); context.WriteTo(json);
                json.WriteString("expression", expression); json.WriteString("classification", classification);
                json.WriteString("sourceCommit", probe ? commit : item.GetProperty("sourceCommit").GetString()); json.WriteString("sourceSha256", NativeSourceSha);
                json.WriteString("originalHex", original); json.WriteString("keyHex", key); json.WriteNumber("sourceLineCount", source.Length); json.WriteNumber("rowIndex", rowIndex);
                json.WriteString("status", status); json.WriteString("errorPhase", phase); json.WriteString("exceptionType", exceptionType); json.WriteString("error", error);
                json.WriteNumber("workUsed", budget.WorkUsed); json.WriteBoolean("executed", executed);
                json.WriteString("publicExecution", executed ? "executed" : "NONEXECUTED");
                json.WriteString("compatibilityScope", !probe && item.GetProperty("publicBridges").GetArrayLength() > 0 ? "serialized-value/key-only; no native typed-AST/full-parser compatibility" : "frozen context public evaluation");
                json.WriteString("producer", "App-headless-public-Compile-Transform");
                json.WriteString("qualification", "fixed72 public contexts and separate arity probe; not full native qualification");
                json.WriteEndObject(); json.Flush();
            }
            stream.WriteByte((byte)'\n'); pending.Add(("native diagnostic " + id, success, error));
        }
    }

    private static void CheckSharedBudgetAndCancellation(string directory, Action<string, bool, string> verify)
    {
        var source = new[] { "" };
        var rule = new LineTransformRule("@cs @noopt IsWithin(0, 1, 3)", TablePaneMask.All);
        var measurement = new LineTransformBudget(); var measured = LineTransformPlan.Compile([rule], measurement);
        var compileWork = measurement.WorkUsed; var firstMeasured = measured.Transform(source, TablePaneMask.Left, false);
        var evaluationWork = measurement.WorkUsed - compileWork;
        // public予算だけでexact 255を構成する。空白paddingも実parserを通して計測する。
        LineTransformRule? boundaryRule = null; string[]? boundarySource = null;
        for (var padding = 0; padding <= 510; padding++)
        {
            var candidate = rule with { Expression = rule.Expression + new string(' ', padding / 2) };
            var candidateBudget = new LineTransformBudget(); var candidatePlan = LineTransformPlan.Compile([candidate], candidateBudget);
            var candidateSource = new[] { padding % 2 == 0 ? "" : "x" };
            candidatePlan.Transform(candidateSource, TablePaneMask.Left, false);
            candidatePlan.Transform(source, TablePaneMask.Base, false);
            if (candidateBudget.WorkUsed == 255) { boundaryRule = candidate; boundarySource = candidateSource; break; }
        }
        if (boundaryRule is null || boundarySource is null) throw new InvalidOperationException("Public parser cannot construct measured work255 boundary.");
        var budget = new LineTransformBudget(maximumWork: 255);
        var plan = LineTransformPlan.Compile([boundaryRule], budget);
        var boundaryOriginal = boundarySource.Single();
        var first = plan.Transform(boundarySource, TablePaneMask.Left, false); var before = budget.WorkUsed;
        var second = plan.Transform(source, TablePaneMask.Base, false); var atBoundary = budget.WorkUsed;
        var rejected = false;
        try { plan.Transform(source, TablePaneMask.Right, false); } catch (InvalidOperationException) { rejected = true; }
        using (var file = new FileStream(Path.Combine(directory, "shared-work-boundary.json"), FileMode.CreateNew, FileAccess.Write))
        using (var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject(); json.WriteNumber("compileWork", compileWork); json.WriteNumber("evaluationWork", evaluationWork);
            json.WriteNumber("afterLeft", before); json.WriteNumber("afterBase", atBoundary); json.WriteNumber("afterRejectedRight", budget.WorkUsed);
            json.WriteNumber("maximumWork", budget.MaximumWork); json.WriteBoolean("rejected", rejected); json.WriteString("expression", boundaryRule.Expression); json.WriteEndObject();
        }
        verify("shared work255 plus1 rejects and retains keys", before < 255 && atBoundary == 255 && rejected && budget.WorkUsed == 255
            && first.SequenceEqual(firstMeasured) && second.SequenceEqual(firstMeasured) && source.Single() == "" && boundarySource.Single() == boundaryOriginal, "");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var compileCanceled = false; var evaluateCanceled = false;
        try { LineTransformPlan.Compile([rule], new LineTransformBudget(), cancellation.Token); } catch (OperationCanceledException) { compileCanceled = true; }
        var fresh = LineTransformPlan.Compile([rule], new LineTransformBudget());
        try { fresh.Transform(source, TablePaneMask.Left, false, cancellationToken: cancellation.Token); } catch (OperationCanceledException) { evaluateCanceled = true; }
        verify("public cancellation retains source", compileCanceled && evaluateCanceled && source.Single() == "", "");
    }
    private static string ReadNativeSerializedHex(string id)
    {
        using var resource = typeof(HeadlessLineTransformWithinChecks).Assembly.GetManifestResourceStream("DiffBeacon.LineTransformWithinGolden")
            ?? throw new InvalidOperationException("IsWithin native golden missing.");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            using var row = JsonDocument.Parse(line);
            if (row.RootElement.GetProperty("id").GetString() != id) continue;
            if (!row.RootElement.GetProperty("serializationApplied").GetBoolean()
                || row.RootElement.GetProperty("serializedUtf8Hex").ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Native serialized key unavailable: " + id);
            return row.RootElement.GetProperty("serializedUtf8Hex").GetString()!;
        }
        throw new InvalidOperationException("Native serialized ID missing: " + id);
    }

    private static void CheckBothStringComparisons(string directory, Action<string, bool, string> verify)
    {
        // GUI工程の追加診断。72固定literalの意味/件数へ加算しない。
        var prefix = new string('a', 10_000);
        var rule = new LineTransformRule("@cs @noopt IsWithin(\"" + prefix + "x\", \"" + prefix + "z\", \"" + prefix + "y\")", TablePaneMask.All);
        var source = new[] { "unchanged" };
        var utf8 = new UTF8Encoding(false, true);
        var originalSha = Convert.ToHexString(SHA256.HashData(utf8.GetBytes(source.Single())));
        var nativeKeyHex = ReadNativeSerializedHex("ascii-mixed-cs");
        var measuredBudget = new LineTransformBudget();
        var measuredPlan = LineTransformPlan.Compile([rule], measuredBudget);
        var compileWork = measuredBudget.WorkUsed;
        var keys = measuredPlan.Transform(source, TablePaneMask.Left, false);
        var evaluationWork = measuredBudget.WorkUsed - compileWork;
        // 下限falseでも上限側prefixを比較する。最終keyの期待値はraw native serializedだけ。
        var limitedBudget = new LineTransformBudget(maximumWork: compileWork + 30_000);
        var limitedPlan = LineTransformPlan.Compile([rule], limitedBudget);
        var rejected = false;
        try { limitedPlan.Transform(source, TablePaneMask.Right, false); }
        catch (InvalidOperationException) { rejected = true; }
        var afterSha = Convert.ToHexString(SHA256.HashData(utf8.GetBytes(source.Single())));
        var actualKeyHex = Convert.ToHexString(utf8.GetBytes(keys.Single())).ToLowerInvariant();
        using (var file = new FileStream(Path.Combine(directory, "both-string-comparisons.json"), FileMode.CreateNew, FileAccess.Write))
        using (var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject(); json.WriteString("qualification", "engineering work/budget observation; separate from fixed native72");
            json.WriteString("referenceNativeId", "ascii-mixed-cs"); json.WriteString("referenceKeyHex", nativeKeyHex); json.WriteString("actualKeyHex", actualKeyHex);
            json.WriteNumber("prefixCharacters", prefix.Length); json.WriteNumber("compileWork", compileWork); json.WriteNumber("evaluationWork", evaluationWork);
            json.WriteNumber("limitedMaximumWork", limitedBudget.MaximumWork); json.WriteNumber("limitedWork", limitedBudget.WorkUsed);
            json.WriteBoolean("rejected", rejected); json.WriteString("sourceShaBefore", originalSha); json.WriteString("sourceShaAfter", afterSha); json.WriteEndObject(); json.Flush(); file.Flush();
        }
        verify("both string comparisons charge work and retain source/key", evaluationWork > 40_000 && rejected && originalSha == afterSha && actualKeyHex == nativeKeyHex, "");
    }
    private static void RequireExternal(string path)
    {
        var full = Path.GetFullPath(path); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { Directory.GetCurrentDirectory(),
            Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("IsWithin UI requires external output.");
        }
        if (full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("IsWithin UI cannot use an artifacts directory.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("IsWithin UI output cannot traverse links.");
    }
}
