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

internal static class HeadlessLineTransformReplaceChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "line-transform-replace"));
        RequireExternal(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Replace UI requires fresh output.");
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false);
        void Verify(string name, bool success, string detail = "") => check("line transform replace " + name, success, detail);
        T Wait<T>(Task<T> task) { pump(task); return task.GetAwaiter().GetResult(); }
        void Settle() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static Button Caption(Control owner, string caption) => owner.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, caption));
        static Button Named(LineTransformDialog dialog, string name) => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
        WriteFixtureDiagnostics(Path.Combine(directory, "diagnostics.ndjson"), Verify);
        CheckSharedBudgetAndCancellation(Verify);

        var oldShown = LineTransformDialog.Shown;
        LineTransformDialog? shown = null;
        LineTransformDialog.Shown = dialog => shown = dialog;
        try
        {
            pane.SetLineTransforms([]); Click(Caption(pane, "行変換…")); Settle();
            var dialog = shown ?? throw new InvalidOperationException("置換行変換ダイアログがありません。");
            foreach (var expression in new[] { "Replace()", "Replace(Line)", "Replace(Line, \"a\")", "Replace(Line, \"a\", \"b\", 1)" })
            {
                dialog.Expression.Text = expression; Click(Named(dialog, "LineTransformAdd"));
                Verify("invalid replace dialog expression is rejected " + expression, dialog.Rules.Count == 0 && !string.IsNullOrEmpty(dialog.Error.Text));
            }
            dialog.Expression.Text = "@cs @noopt Replace(Line, \"a\", \"b\")"; Click(Named(dialog, "LineTransformAdd"));
            dialog.Expression.Text = "@ci @noopt Replace(Line, \"é\", \"X\")"; Click(Named(dialog, "LineTransformAdd"));
            SaveDialogFrame(dialog, directory, Verify);
            var intended = dialog.Rules.ToArray(); Click(dialog.Accept); Click(dialog.Accept); Settle();
            Verify("duplicate dialog apply retains dynamic replace rule", intended.Length == 2 && pane.CaptureProject().LineTransformRules.SequenceEqual(intended));
            shown = null; Click(Caption(pane, "行変換…")); Settle();
            dialog = shown ?? throw new InvalidOperationException("置換行変換の再開に失敗しました。");
            dialog.Expression.Text = "Replace(Line, \"replacement\", \"x\")"; Click(Named(dialog, "LineTransformAdd")); Click(dialog.Cancel); Settle();
            Verify("cancel replace edit retains adopted metadata", pane.CaptureProject().LineTransformRules.SequenceEqual(intended));
        }
        finally { LineTransformDialog.Shown = oldShown; }

        var rawLeft = "id,value\r\nA,\"Ééあx,left\"\r\n";
        var rawMiddle = "id,value\r\nA,\"éÉあx,middle\"\r\n";
        var rawRight = "id,value\r\nA,\"ééあx,right\"\r\n";
        string Write(string side, string text)
        {
            var path = Path.Combine(directory, side + ".csv"); using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            file.Write(utf8.GetBytes(text)); return path;
        }
        var paths = new[] { Write("left", rawLeft), Write("middle", rawMiddle), Write("right", rawRight) };
        var offsetPaths = new[] { Write("offset-left", "A,\"Ééあx,left\"\r\n"), Write("offset-middle", "A,\"Ééあx,left\"\r\n"), Write("offset-right", "proof\r\n") };
        var offsetOriginals = offsetPaths.Select(File.ReadAllBytes).ToArray();
        var originals = paths.Select(File.ReadAllBytes).ToArray();
        using (var inputManifest = new FileStream(Path.Combine(directory, "input-sha.json"), FileMode.CreateNew, FileAccess.Write))
        using (var json = new Utf8JsonWriter(inputManifest))
        {
            json.WriteStartArray();
            var inputPaths = paths.Concat(offsetPaths).ToArray();
            for (var side = 0; side < inputPaths.Length; side++)
            {
                var path = inputPaths[side];
                json.WriteStartObject();
                json.WriteNumber("side", side);
                json.WriteString("path", path);
                json.WriteString("sha256", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
                json.WriteNumber("size", new FileInfo(path).Length);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.Flush();
        }
        var dynamicRule = new LineTransformRule("@ci @noopt If(LineNumber = 1, Line, Replace(Replace(Replace(Replace(Column2, \"é\", \"X\"), \"left\", \"shared\"), \"middle\", \"shared\"), \"right\", \"shared\"))", TablePaneMask.All);
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
        Verify("dynamic getter replace keys ignore details across three actual panes", table.Comparison.Documents.Count == 3
            && !table.Comparison.HasDifferences && table.Comparison.IsIgnored(0, 1) && table.Comparison.IsIgnored(2, 1)
            && table.Comparison.GetCell(0, 1, 1)?.Value == "Ééあx,left" && table.Comparison.GetCell(1, 1, 1)?.Value == "éÉあx,middle"
            && table.Comparison.GetCell(2, 1, 1)?.Value == "ééあx,right");
        Verify("replace semantic rows expose Equal ignored header classification", table.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.Name is "table-row-header-0-1" or "table-row-header-2-1")
            .Count(block => Equals(block.Tag, DiffKind.Equal) && block.Background is null) == 2);
        screenshot("line-transform-replace-table.png");

        // RAW Column2はCSV引用符を含む。復号cellを使用すると一致しない。
        pane.DiscardChanges(); pane.ApplyProject(new ComparisonProject
        {
            Mode = "Table", LeftPath = offsetPaths[0], BasePath = offsetPaths[1], RightPath = offsetPaths[2],
            TableDelimiter = ',', TableQuote = '"',
            LineTransformRules = [new("@cs @noopt Replace(Column2, \"\"\"Ééあx,left\"\"\", \"proof\")", TablePaneMask.Left | TablePaneMask.Base)]
        });
        pump(pane.CompareProjectAsync()); Settle();
        Verify("RAW quoted Column2 and physical three-pane mask", Table().Comparison.Documents.Count == 3
            && !Table().Comparison.HasDifferences && pane.LeftEditor.Text == "A,\"Ééあx,left\"\r\n"
            && pane.RightEditor.Text == "proof\r\n" && pane.CaptureProject().LineTransformRules.Single().Panes == (TablePaneMask.Left | TablePaneMask.Base));
        for (var side = 0; side < offsetPaths.Length; side++) Verify("offset-proof input SHA " + side,
            SHA256.HashData(File.ReadAllBytes(offsetPaths[side])).SequenceEqual(SHA256.HashData(offsetOriginals[side])));
        pane.DiscardChanges(); pane.ApplyProject(new ComparisonProject
        {
            Mode = "Table", LeftPath = paths[0], BasePath = paths[1], RightPath = paths[2], TableDelimiter = ',', TableQuote = '"',
            LineTransformRules = [dynamicRule]
        });
        pump(pane.CompareProjectAsync()); Settle(); table = Table();
        table.SelectCell(0, 1, 1); table.CellEditor.Text = "edited,raw"; TableClick(table, "セルを変更");
        var edited = pane.LeftEditor.Text ?? throw new InvalidOperationException("置換変換後の編集原文がありません。");
        Verify("replace transform leaves original quoted cell editable", edited == "id,value\r\nA,\"edited,raw\"\r\n");
        TableClick(table, "セル編集を戻す"); Verify("replace cell undo restores raw CSV", pane.LeftEditor.Text == rawLeft);
        TableClick(table, "セル編集をやり直す"); Verify("replace cell redo restores raw edit", pane.LeftEditor.Text == edited);
        pump(pane.SaveAsync(false)); Verify("replace save writes raw source bytes", File.ReadAllBytes(paths[0]).SequenceEqual(utf8.GetBytes(edited)));
        originals[0] = utf8.GetBytes(edited);
        var workspace = Path.Combine(directory, "workspace.json");
        pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new ComparisonWorkspace { Entries = [pane.CaptureProject()], ActiveEntryIndex = 0 }));
        var restored = Wait(WorkspaceStore.LoadWorkspaceAsync(workspace));
        pane.DiscardChanges(); pane.ApplyProject(restored.Entries.Single()); pump(pane.CompareProjectAsync()); Settle();
        Verify("replace workspace restores expression and raw saved source", pane.CaptureProject().LineTransformRules.SequenceEqual([dynamicRule])
            && pane.LeftEditor.Text == edited && Table().Comparison.GetCell(0, 1, 1)?.Value == "edited,raw");
        var report = Path.Combine(directory, "report.html"); pump(pane.SaveReportAsync(report));
        var html = File.ReadAllText(report);
        Verify("replace report keeps original cell contents", html.Contains("edited,raw", StringComparison.Ordinal)
            && html.Contains("éÉあx,middle", StringComparison.Ordinal) && html.Contains("ééあx,right", StringComparison.Ordinal));

        // 同じ実Tableをdefaultoptでも再比較し、dynamic Column/LineNumberを定数化しないことを確認する。
        pane.SetLineTransforms([new(dynamicRule.Expression.Replace("@noopt ", "", StringComparison.Ordinal), TablePaneMask.All)]);
        pump(pane.CompareProjectAsync()); Settle();
        Verify("default optimize preserves raw Column getter behavior", Table().Comparison.HasDifferences
            && Table().Comparison.GetCell(0, 1, 1)?.Value == "edited,raw" && pane.LeftEditor.Text == edited);
        pane.SetLineTransforms([dynamicRule]); pump(pane.CompareProjectAsync()); Settle();
        var adopted = Table(); var adoptedState = pane.CaptureAdoptionState();
        pane.SetLineTransforms([new("@cs @noopt Replace(Line, \"a\", (1 / 0))", TablePaneMask.All)]);
        pump(pane.CompareProjectAsync()); Settle();
        Verify("eager replacement division rejects without changing view", ReferenceEquals(adopted, Table())
            && Equals(adoptedState, pane.CaptureAdoptionState()) && pane.LeftEditor.Text == edited
            && pane.CaptureProject().LineTransformRules.SequenceEqual([new LineTransformRule("@cs @noopt Replace(Line, \"a\", (1 / 0))", TablePaneMask.All)]));
        screenshot("line-transform-replace-failure-preserved.png");
        var oldReady = pane.TableReadyForAdoption;
        try
        {
            pane.SetLineTransforms([dynamicRule]);
            pane.TableReadyForAdoption = () => Click(Caption(pane, "中止"));
            pump(pane.CompareProjectAsync()); Settle();
            Verify("replace cancellation preserves adopted table", ReferenceEquals(adopted, Table()) && pane.LeftEditor.Text == edited
                && pane.CaptureProject().LineTransformRules.SequenceEqual([dynamicRule]));
            var later = new LineTransformRule("@cs @noopt Replace(Line, \"a\", \"X\")", TablePaneMask.All);
            pane.TableReadyForAdoption = () => pane.SetLineTransforms([later]);
            pump(pane.CompareProjectAsync()); Settle();
            Verify("late replace settings reject stale candidate and retain newer metadata", ReferenceEquals(adopted, Table())
                && pane.CaptureProject().LineTransformRules.SequenceEqual([later]) && pane.LeftEditor.Text == edited);
        }
        finally { pane.TableReadyForAdoption = oldReady; }
        pane.SetLineTransforms([new("@cs @noopt Replace(Line, \"a\", \"\n\")", TablePaneMask.All)]);
        pump(pane.CompareProjectAsync()); Settle();
        Verify("generated newline failure preserves adopted raw view", ReferenceEquals(adopted, Table())
            && Equals(adoptedState, pane.CaptureAdoptionState()) && pane.LeftEditor.Text == edited);
        for (var side = 0; side < paths.Length; side++) Verify("replace flow preserves raw input bytes " + side, SHA256.HashData(File.ReadAllBytes(paths[side])).SequenceEqual(SHA256.HashData(originals[side])));

        pane.DiscardChanges(); var textRule = new LineTransformRule("@ci @noopt If(LineNumber = 1, Line, Replace(Line, Line, \"same\"))", TablePaneMask.Left | TablePaneMask.Right);
        pane.ApplyProject(new ComparisonProject { Mode = "Text", LeftPath = paths[0], RightPath = paths[2], LineTransformRules = [textRule] });
        pump(pane.CompareProjectAsync()); Settle();
        Verify("text dynamic replace getter preserves physical source and side mask", pane.CurrentDiff is { HasDifferences: false }
            && pane.LeftEditor.Text == edited && pane.RightEditor.Text == rawRight && pane.CaptureProject().LineTransformRules.Single().Panes == textRule.Panes);

        pane.DiscardChanges(); pane.ApplyProject(restored.Entries.Single()); pump(pane.CompareProjectAsync()); Settle();
        Table().SelectCell(0, 1, 1);

        var originalWidth = window.Width; var originalHeight = window.Height;
        try
        {
            window.Width = 1280; window.Height = 850; Layout("normal");
            window.Width = 850; window.Height = 550; Layout("minimum");
        }
        finally { window.Width = originalWidth; window.Height = originalHeight; Settle(); }

        void Layout(string name)
        {
            Settle();
            var pending = new List<(string Label, bool Reachable, string Detail)>();
            var toolbar = pane.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll =>
                scroll.Content is Control content && content.GetVisualDescendants().Contains(pane.CompareButton));
            using var file = new FileStream(Path.Combine(directory, name + "-bounds.json"), FileMode.CreateNew, FileAccess.Write);
            using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
            json.WriteStartArray();
            foreach (var (label, control) in new (string, Control)[] { ("transform", Caption(pane, "行変換…")),
                ("left input", pane.LeftPath), ("right input", pane.RightPath), ("compare", pane.CompareButton) })
            {
                control.BringIntoView(); Settle();
                var point = control.TranslatePoint(default, toolbar);
                var reachable = point is { } p && control.IsEffectivelyVisible && control.Bounds.Height > 0
                    && p.Y >= -.01 && p.Y + control.Bounds.Height <= toolbar.Viewport.Height + .01;
                var detail = FormattableString.Invariant($"x={point?.X}; y={point?.Y}; width={control.Bounds.Width}; height={control.Bounds.Height}; viewportWidth={toolbar.Viewport.Width}; viewportHeight={toolbar.Viewport.Height}; offset={toolbar.Offset}; visible={control.IsEffectivelyVisible}");
                pending.Add((label, reachable, detail));
                json.WriteStartObject(); json.WriteString("control", label); json.WriteNumber("windowWidth", window.Width);
                json.WriteNumber("windowHeight", window.Height); json.WriteNumber("x", point?.X ?? -1); json.WriteNumber("y", point?.Y ?? -1);
                json.WriteNumber("width", control.Bounds.Width); json.WriteNumber("viewportWidth", toolbar.Viewport.Width);
                json.WriteNumber("height", control.Bounds.Height); json.WriteNumber("viewportHeight", toolbar.Viewport.Height);
                json.WriteNumber("offsetX", toolbar.Offset.X); json.WriteNumber("offsetY", toolbar.Offset.Y);
                json.WriteBoolean("visible", control.IsEffectivelyVisible); json.WriteBoolean("enabled", control.IsEnabled);
                json.WriteBoolean("reachable", reachable); json.WriteString("detail", detail); json.WriteEndObject();
                if (!reachable) screenshot("line-transform-replace-" + name + "-failure-" + label.Replace(' ', '-') + ".png");
            }
            toolbar.Offset = default; Settle();
            var currentTable = Table();
            var tableToolbar = currentTable.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll =>
                scroll.Content is Control content && content.GetVisualDescendants().Contains(currentTable.CellEditor));
            foreach (var (label, control) in new (string, Control)[] { ("cell editor", currentTable.CellEditor),
                ("edit", Caption(currentTable, "セルを変更")), ("undo", Caption(currentTable, "セル編集を戻す")),
                ("redo", Caption(currentTable, "セル編集をやり直す")) })
            {
                control.BringIntoView(); Settle();
                var point = control.TranslatePoint(default, tableToolbar);
                var reachable = point is { } p && control.IsEffectivelyVisible && control.Bounds.Height > 0
                    && p.Y >= -.01 && p.Y + control.Bounds.Height <= tableToolbar.Viewport.Height + .01;
                var detail = FormattableString.Invariant($"x={point?.X}; y={point?.Y}; width={control.Bounds.Width}; height={control.Bounds.Height}; viewportWidth={tableToolbar.Viewport.Width}; viewportHeight={tableToolbar.Viewport.Height}; offset={tableToolbar.Offset}; visible={control.IsEffectivelyVisible}");
                pending.Add((label, reachable, detail));
                json.WriteStartObject(); json.WriteString("control", label); json.WriteNumber("windowWidth", window.Width);
                json.WriteNumber("windowHeight", window.Height); json.WriteNumber("x", point?.X ?? -1); json.WriteNumber("y", point?.Y ?? -1);
                json.WriteNumber("width", control.Bounds.Width); json.WriteNumber("viewportWidth", tableToolbar.Viewport.Width);
                json.WriteNumber("height", control.Bounds.Height); json.WriteNumber("viewportHeight", tableToolbar.Viewport.Height);
                json.WriteNumber("offsetX", tableToolbar.Offset.X); json.WriteNumber("offsetY", tableToolbar.Offset.Y);
                json.WriteBoolean("visible", control.IsEffectivelyVisible); json.WriteBoolean("enabled", control.IsEnabled);
                json.WriteBoolean("reachable", reachable); json.WriteString("detail", detail); json.WriteEndObject();
                if (!reachable) screenshot("line-transform-replace-" + name + "-failure-" + label.Replace(' ', '-') + ".png");
            }
            // Checkは失敗時に例外を投げるため、全寸法と失敗時の実画面を確定してから判定する。
            json.WriteEndArray(); json.Flush(); file.Flush(); screenshot("line-transform-replace-" + name + ".png");
            toolbar.Offset = default; Settle();
            foreach (var result in pending) Verify(name + " reachable " + result.Label, result.Reachable, result.Detail);
        }
    }

    private static void SaveDialogFrame(LineTransformDialog dialog, string directory, Action<string, bool, string> verify)
    {
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var bitmap = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("置換ダイアログの描画に失敗しました。");
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
            verify("actual Replace dialog reachable " + control.Name, reachable, "");
            json.WriteStartObject(); json.WriteString("control", control.Name); json.WriteNumber("y", point?.Y ?? -1);
            json.WriteNumber("height", control.Bounds.Height); json.WriteNumber("clientHeight", dialog.ClientSize.Height);
            json.WriteBoolean("reachable", reachable); json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    private static void WriteFixtureDiagnostics(string path, Action<string, bool, string> verify)
    {
        var assembly = typeof(HeadlessLineTransformReplaceChecks).Assembly;
        using var resource = assembly.GetManifestResourceStream("DiffBeacon.LineTransformReplaceCases")
            ?? throw new InvalidOperationException("Replace固定planがありません。");
        using var cases = JsonDocument.Parse(resource);
        using var oracleResource = assembly.GetManifestResourceStream("DiffBeacon.LineTransformReplaceSerializedGolden")
            ?? throw new InvalidOperationException("Replace固定serialized oracleがありません。");
        using var reader = new StreamReader(oracleResource, Encoding.UTF8);
        var oracle = new Dictionary<string, string?>(StringComparer.Ordinal);
        var oracleOrder = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            using var row = JsonDocument.Parse(line);
            var id = row.RootElement.GetProperty("id").GetString()!;
            oracle.Add(id, row.RootElement.GetProperty("serializedUtf8Hex").GetString()); oracleOrder.Add(id);
        }
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        var count = 0; var successes = 0; var rejections = 0; var unsupported = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var utf8 = new UTF8Encoding(false, true);
        foreach (var item in cases.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var expression = item.GetProperty("expression").GetString();
            var classification = item.GetProperty("oracleMatchedOrBoundary").GetString()!;
            var context = item.GetProperty("context");
            var originalHex = context.GetProperty("sourceLineHex").GetString()!;
            var lineCount = context.GetProperty("sourceLineCount").GetInt32();
            var rowIndex = context.GetProperty("rowIndex").GetInt32();
            var keyHex = originalHex;
            var status = "ok"; var phase = "compile"; var exceptionType = ""; var error = "";
            verify("fixed context " + id, lineCount == 1 && rowIndex == 0 && context.GetProperty("outerEol").GetInt32() == 0
                && !context.GetProperty("tableMode").GetBoolean() && context.GetProperty("pane").GetString() == "Left"
                && context.GetProperty("targetMask").GetString() == "Left", "");
            if (expression is null)
            {
                // このadapterでは表現不能。解析・評価の代用品で成功にしない。
                status = "unsupported"; phase = "unrepresentable"; exceptionType = "UnrepresentableAdapterType"; unsupported++;
            }
            else
            {
                try
                {
                    var plan = LineTransformPlan.Compile([new(expression, TablePaneMask.Left)], new LineTransformBudget());
                    phase = "evaluate";
                    // 空EOFも空配列ではなく、固定context通りの一行として評価する。
                    var source = new[] { utf8.GetString(Convert.FromHexString(originalHex)) };
                    var keys = plan.Transform(source, TablePaneMask.Left, false);
                    if (keys.Length != lineCount) throw new InvalidOperationException("Replace changed source row count.");
                    keyHex = Convert.ToHexString(utf8.GetBytes(keys[rowIndex])).ToLowerInvariant(); phase = "";
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or ArithmeticException)
                { status = "error"; exceptionType = exception.GetType().Name; error = exception.Message; }
                if (classification == "oracle-match-candidate")
                {
                    successes++;
                    verify("native serialized bytes " + id, status == "ok" && phase == "" && exceptionType == ""
                        && oracle.TryGetValue(id, out var expected) && keyHex == expected, error);
                }
                else
                {
                    rejections++;
                    var expectedPhase = classification == "generated-crlf-boundary" ? "evaluate" : "compile";
                    verify("public rejection " + id, status == "error" && phase == expectedPhase
                        && exceptionType == "ArgumentException" && keyHex == originalHex, error);
                }
            }
            verify("original ID order " + id, ids.Add(id) && count < oracleOrder.Count && oracleOrder[count] == id, "");
            using (var json = new Utf8JsonWriter(output))
            {
                json.WriteStartObject(); json.WriteString("id", id); json.WriteString("classification", classification);
                json.WriteString("expression", expression); json.WritePropertyName("context"); context.WriteTo(json);
                json.WriteString("sourceCommit", "6780924ba35f7f55af0f6f14dd302561175830a6");
                json.WriteString("sourceSha256", cases.RootElement.GetProperty("originalNodesSha256").GetString());
                json.WriteString("originalHex", originalHex); json.WriteString("keyHex", keyHex);
                json.WriteNumber("sourceLineCount", lineCount); json.WriteNumber("rowIndex", rowIndex);
                json.WriteString("status", status); json.WriteString("errorPhase", phase); json.WriteString("exceptionType", exceptionType);
                json.WriteString("error", error); json.WriteEndObject(); json.Flush();
            }
            output.WriteByte((byte)'\n'); count++;
        }
        verify("all fixed IDs executed or explicitly unrepresentable", count == 100 && ids.Count == 100 && oracleOrder.Count == 100, path);
        verify("78 native matches 10 public rejections 12 unrepresentable", successes == 78 && rejections == 10 && unsupported == 12, path);
    }

    private static void CheckSharedBudgetAndCancellation(Action<string, bool, string> verify)
    {
        var rule = new LineTransformRule("@cs @noopt Replace(Line, \"a\", \"X\")", TablePaneMask.All);
        var budget = new LineTransformBudget(maximumOutputCharacters: 1024);
        var plan = LineTransformPlan.Compile([rule], budget);
        var source = new[] { "aba" };
        var first = plan.Transform(source, TablePaneMask.Left, false);
        var before = budget.OutputCharacters;
        var second = plan.Transform(source, TablePaneMask.Base, false);
        verify("shared pane budget accumulates actual Replace output", first.Single() == "XbX" && second.Single() == "XbX"
            && before > 0 && budget.OutputCharacters > before && source.Single() == "aba", "");
        var oversized = new[] { new string('a', 2048) };
        var rejected = false;
        try { plan.Transform(oversized, TablePaneMask.Right, false); }
        catch (InvalidOperationException) { rejected = true; }
        verify("shared output exhaustion preserves source and prior keys", rejected && oversized.Single() == new string('a', 2048)
            && first.Single() == "XbX" && second.Single() == "XbX", "");

        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var compileCanceled = false; var evaluateCanceled = false;
        try { LineTransformPlan.Compile([rule], new LineTransformBudget(), cancellation.Token); }
        catch (OperationCanceledException) { compileCanceled = true; }
        var freshPlan = LineTransformPlan.Compile([rule], new LineTransformBudget());
        try { freshPlan.Transform(source, TablePaneMask.Left, false, cancellationToken: cancellation.Token); }
        catch (OperationCanceledException) { evaluateCanceled = true; }
        verify("public compile and evaluate cancellation preserve source", compileCanceled && evaluateCanceled && source.Single() == "aba", "");
    }

    private static void RequireExternal(string path)
    {
        var full = Path.GetFullPath(path); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { Directory.GetCurrentDirectory(),
            Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("Replace UI requires external output.");
        }
        if (full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Replace UI cannot use an artifacts directory.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Replace UI output cannot traverse links.");
    }
}
