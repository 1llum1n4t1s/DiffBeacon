using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessLineTransformSearchChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "line-transform-search"));
        RequireExternal(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Search UI requires fresh output.");
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false);
        void Verify(string name, bool success, string detail = "") => check("line transform search " + name, success, detail);
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
            var dialog = shown ?? throw new InvalidOperationException("検索行変換ダイアログがありません。");
            foreach (var expression in new[] { "StrCount()", "StrCount(Line)", "StrFind()", "StrFind(Line)", "StrFind(Line, \"a\", 0, 1)" })
            {
                dialog.Expression.Text = expression; Click(Named(dialog, "LineTransformAdd"));
                Verify("invalid search dialog expression is rejected " + expression, dialog.Rules.Count == 0 && !string.IsNullOrEmpty(dialog.Error.Text));
            }
            dialog.Expression.Text = "@cs @noopt StrCount(Line, \"a\")"; Click(Named(dialog, "LineTransformAdd"));
            dialog.Expression.Text = "@ci @noopt StrFind(Line, \"A\", LineNumber)"; Click(Named(dialog, "LineTransformAdd"));
            var intended = dialog.Rules.ToArray(); Click(dialog.Accept); Click(dialog.Accept); Settle();
            Verify("duplicate dialog apply retains dynamic search rule", intended.Length == 2 && pane.CaptureProject().LineTransformRules.SequenceEqual(intended));
            shown = null; Click(Caption(pane, "行変換…")); Settle();
            dialog = shown ?? throw new InvalidOperationException("検索行変換の再開に失敗しました。");
            dialog.Expression.Text = "StrFind(Line, \"replacement\")"; Click(Named(dialog, "LineTransformAdd")); Click(dialog.Cancel); Settle();
            Verify("cancel search edit retains adopted metadata", pane.CaptureProject().LineTransformRules.SequenceEqual(intended));
        }
        finally { LineTransformDialog.Shown = oldShown; }

        var rawLeft = "id,value\r\nA,\"あx,left\"\r\n";
        var rawMiddle = "id,value\r\nA,\"あx,middle\"\r\n";
        var rawRight = "id,value\r\nA,\"あx,right\"\r\n";
        string Write(string side, string text)
        {
            var path = Path.Combine(directory, side + ".csv"); using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            file.Write(utf8.GetBytes(text)); return path;
        }
        var paths = new[] { Write("left", rawLeft), Write("middle", rawMiddle), Write("right", rawRight) };
        var offsetPaths = new[] { Write("offset-left", "A,\"あx,left\"\r\n"), Write("offset-middle", "A,\"あx,middle\"\r\n"), Write("offset-right", "5\r\n") };
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
        var dynamicRule = new LineTransformRule("@cs @noopt If(LineNumber = 1, Line, StrFind(Column2, \",\"))", TablePaneMask.All);
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
        Verify("dynamic getter search keys ignore details across three actual panes", table.Comparison.Documents.Count == 3
            && !table.Comparison.HasDifferences && table.Comparison.IsIgnored(0, 1) && table.Comparison.IsIgnored(2, 1)
            && table.Comparison.GetCell(0, 1, 1)?.Value == "あx,left" && table.Comparison.GetCell(1, 1, 1)?.Value == "あx,middle"
            && table.Comparison.GetCell(2, 1, 1)?.Value == "あx,right");
        Verify("search semantic rows expose Equal ignored header classification", table.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.Name is "table-row-header-0-1" or "table-row-header-2-1")
            .Count(block => Equals(block.Tag, DiffKind.Equal) && block.Background is null) == 2);
        screenshot("line-transform-search-table.png");

        // 実三者Tableで左・中央だけRAW Column2を変換し、右の普通の数字5と照合する。
        // 引用符1byte + あ3byte + x1byte = 5。復号済みcellの4やUTF16の3では一致しない。
        pane.DiscardChanges(); pane.ApplyProject(new ComparisonProject
        {
            Mode = "Table", LeftPath = offsetPaths[0], BasePath = offsetPaths[1], RightPath = offsetPaths[2],
            TableDelimiter = ',', TableQuote = '"',
            LineTransformRules = [new("@cs @noopt StrFind(Column2, \",\")", TablePaneMask.Left | TablePaneMask.Base)]
        });
        pump(pane.CompareProjectAsync()); Settle();
        Verify("RAW Column2 byte offset and physical three-pane mask", Table().Comparison.Documents.Count == 3
            && !Table().Comparison.HasDifferences && pane.LeftEditor.Text == "A,\"あx,left\"\r\n"
            && pane.RightEditor.Text == "5\r\n" && pane.CaptureProject().LineTransformRules.Single().Panes == (TablePaneMask.Left | TablePaneMask.Base));
        for (var side = 0; side < offsetPaths.Length; side++) Verify("offset-proof input SHA " + side,
            SHA256.HashData(File.ReadAllBytes(offsetPaths[side])).SequenceEqual(SHA256.HashData(offsetOriginals[side])));
        pane.DiscardChanges(); pane.ApplyProject(new ComparisonProject
        {
            Mode = "Table", LeftPath = paths[0], BasePath = paths[1], RightPath = paths[2], TableDelimiter = ',', TableQuote = '"',
            LineTransformRules = [dynamicRule]
        });
        pump(pane.CompareProjectAsync()); Settle(); table = Table();
        table.SelectCell(0, 1, 1); table.CellEditor.Text = "edited,raw"; TableClick(table, "セルを変更");
        var edited = pane.LeftEditor.Text ?? throw new InvalidOperationException("検索変換後の編集原文がありません。");
        Verify("search transform leaves original quoted cell editable", edited == "id,value\r\nA,\"edited,raw\"\r\n");
        TableClick(table, "セル編集を戻す"); Verify("search cell undo restores raw CSV", pane.LeftEditor.Text == rawLeft);
        TableClick(table, "セル編集をやり直す"); Verify("search cell redo restores raw edit", pane.LeftEditor.Text == edited);
        pump(pane.SaveAsync(false)); Verify("search save writes raw source bytes", File.ReadAllBytes(paths[0]).SequenceEqual(utf8.GetBytes(edited)));
        originals[0] = utf8.GetBytes(edited);
        var workspace = Path.Combine(directory, "workspace.json");
        pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new ComparisonWorkspace { Entries = [pane.CaptureProject()], ActiveEntryIndex = 0 }));
        var restored = Wait(WorkspaceStore.LoadWorkspaceAsync(workspace));
        pane.DiscardChanges(); pane.ApplyProject(restored.Entries.Single()); pump(pane.CompareProjectAsync()); Settle();
        Verify("search workspace restores expression and raw saved source", pane.CaptureProject().LineTransformRules.SequenceEqual([dynamicRule])
            && pane.LeftEditor.Text == edited && Table().Comparison.GetCell(0, 1, 1)?.Value == "edited,raw");
        var report = Path.Combine(directory, "report.html"); pump(pane.SaveReportAsync(report));
        var html = File.ReadAllText(report);
        Verify("search report keeps original cell contents", html.Contains("edited,raw", StringComparison.Ordinal)
            && html.Contains("あx,middle", StringComparison.Ordinal) && html.Contains("あx,right", StringComparison.Ordinal));

        // 同じ実Tableをdefaultoptでも再比較し、dynamic Column/LineNumberを定数化しないことを確認する。
        pane.SetLineTransforms([new(dynamicRule.Expression.Replace("@noopt ", "", StringComparison.Ordinal), TablePaneMask.All)]);
        pump(pane.CompareProjectAsync()); Settle();
        Verify("default optimize preserves raw Column getter behavior", Table().Comparison.HasDifferences
            && Table().Comparison.GetCell(0, 1, 1)?.Value == "edited,raw" && pane.LeftEditor.Text == edited);
        pane.SetLineTransforms([dynamicRule]); pump(pane.CompareProjectAsync()); Settle();
        var adopted = Table(); var adoptedState = pane.CaptureAdoptionState();
        pane.SetLineTransforms([new("@cs @noopt StrFind(None, \"\", (1 / 0))", TablePaneMask.All)]);
        pump(pane.CompareProjectAsync()); Settle();
        Verify("eager occurrence division rejects without changing view", ReferenceEquals(adopted, Table())
            && Equals(adoptedState, pane.CaptureAdoptionState()) && pane.LeftEditor.Text == edited
            && pane.CaptureProject().LineTransformRules.SequenceEqual([new LineTransformRule("@cs @noopt StrFind(None, \"\", (1 / 0))", TablePaneMask.All)]));
        screenshot("line-transform-search-failure-preserved.png");
        var oldReady = pane.TableReadyForAdoption;
        try
        {
            pane.SetLineTransforms([dynamicRule]);
            pane.TableReadyForAdoption = () => Click(Caption(pane, "中止"));
            pump(pane.CompareProjectAsync()); Settle();
            Verify("search cancellation preserves adopted table", ReferenceEquals(adopted, Table()) && pane.LeftEditor.Text == edited
                && pane.CaptureProject().LineTransformRules.SequenceEqual([dynamicRule]));
            var later = new LineTransformRule("@cs @noopt StrCount(Line, \",\")", TablePaneMask.All);
            pane.TableReadyForAdoption = () => pane.SetLineTransforms([later]);
            pump(pane.CompareProjectAsync()); Settle();
            Verify("late search settings reject stale candidate and retain newer metadata", ReferenceEquals(adopted, Table())
                && pane.CaptureProject().LineTransformRules.SequenceEqual([later]) && pane.LeftEditor.Text == edited);
        }
        finally { pane.TableReadyForAdoption = oldReady; }
        for (var side = 0; side < paths.Length; side++) Verify("search flow preserves raw input bytes " + side, SHA256.HashData(File.ReadAllBytes(paths[side])).SequenceEqual(SHA256.HashData(originals[side])));

        pane.DiscardChanges(); var textRule = new LineTransformRule("@ci @noopt If(LineNumber = 1, Line, StrFind(Line, \"a\"))", TablePaneMask.Left | TablePaneMask.Right);
        pane.ApplyProject(new ComparisonProject { Mode = "Text", LeftPath = paths[0], RightPath = paths[2], LineTransformRules = [textRule] });
        pump(pane.CompareProjectAsync()); Settle();
        Verify("text dynamic search getter preserves physical source and side mask", pane.CurrentDiff is { HasDifferences: false }
            && pane.LeftEditor.Text == edited && pane.RightEditor.Text == rawRight && pane.CaptureProject().LineTransformRules.Single().Panes == textRule.Panes);
    }

    private static void WriteFixtureDiagnostics(string path, Action<string, bool, string> verify)
    {
        using var resource = typeof(HeadlessLineTransformSearchChecks).Assembly.GetManifestResourceStream("DiffBeacon.LineTransformSearchCases")
            ?? throw new InvalidOperationException("行変換の固定case resourceがありません。");
        using var cases = JsonDocument.Parse(resource);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
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
        verify("all fixed oracle cases executed in app process", count == 68, path);
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
    private static void RequireExternal(string path)
    {
        var full = Path.GetFullPath(path); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { Directory.GetCurrentDirectory(),
            Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("Search UI requires external output.");
        }
        if (full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Search UI cannot use an artifacts directory.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Search UI output cannot traverse links.");
    }}
