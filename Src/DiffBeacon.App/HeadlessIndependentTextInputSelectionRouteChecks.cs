using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

// 新入口から採用したtabだけを使い、実buttonの元Taskを最後まで待つ。
internal static class HeadlessIndependentTextInputSelectionRouteChecks
{
    internal static void Run(MainWindow unused, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> unusedScreenshot)
    {
        var folder = Path.Combine(output, "independent-text-input-selection-routes");
        if (Directory.Exists(folder)) throw new IOException("input322は未使用runが必要です。");
        Directory.CreateDirectory(folder);
        var fixture = Path.GetFullPath("tests/Fixtures/IndependentTextInputSelectionRoutes");
        string[] roles = ["left", "middle", "right"];
        string[] texts = ["shared café\r\nLEFT €\nend-left\r", "shared café\r\nMIDDLE £\nend-middle\r", "shared café\r\nRIGHT “quote”\nend-right\r"];
        var edits = texts.Select(t => t.Replace("end-", "edited-", StringComparison.Ordinal)).ToArray();
        var receipts = new List<Action<Utf8JsonWriter>>();
        void Jobs() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        void Click(Button button) { if (!button.IsEnabled) throw new InvalidOperationException("disabled button: " + button.Content); button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); }
        Button Button(Control control, string label) => control.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, label));
        void Verify(string name, bool value) { check("Input322 " + name, value, ""); if (!value) throw new InvalidOperationException(name); }
        void Execute(string id, int? source = null, int? destination = null, bool mixed = false)
        {
            var run = Path.Combine(folder, id); Directory.CreateDirectory(run); Directory.CreateDirectory(Path.Combine(run, "inputs"));
            foreach (var file in Directory.EnumerateFiles(Path.Combine(fixture, "inputs"))) File.Copy(file, Path.Combine(run, "inputs", Path.GetFileName(file)));
            string PathOf(string name) => Path.Combine(run, name);
            var host = new MainWindow(null, new ImageApplicationOptionsStore(PathOf("options.json"))) { Width = 1000, Height = 680 }; host.Show(); Jobs();
            var actions = new List<string>(); var tasks = new Dictionary<string, Task>();
            Task<bool>? opening = null; Task? submission = null; IndependentTextInputDialog? selection = null;
            host.IndependentTextInputOperationObserved = task => opening = task;
            host.IndependentTextInputDialogShown = dialog =>
            {
                selection = dialog; dialog.SubmissionTaskObserved = task => submission = task;
                for (var side = 0; side < 3; side++)
                {
                    var captured = side; dialog.Side(side).OperationTaskObserved = (name, task) => tasks[captured + ":" + name] = task;
                    dialog.Side(side).PickerPathProvider = (_, _, _) => Task.FromResult<string?>(PathOf("inputs/" + roles[captured] + (mixed && captured == 1 ? ".txt" : ".zip")));
                }
            };
            void Observed(string name, Action click)
            {
                tasks.Remove(name); click(); actions.Add(name);
                if (!tasks.Remove(name, out var task)) throw new InvalidOperationException("元Task未観測: " + name);
                pump(task); Verify(id + " observed task " + name, task.IsCompletedSuccessfully); Jobs();
            }
            try
            {
                var parent = host.ActivePane; Click(parent.IndependentTextInputsButton); actions.Add("entry");
                if (selection is null || opening is null) throw new InvalidOperationException("入口元Task/dialog未観測");
                for (var side = 0; side < 3; side++)
                {
                    selection.SideTabs.SelectedIndex = side; Jobs(); var browser = selection.Side(side);
                    browser.Kind.SelectedIndex = mixed ? side switch { 0 => 2, 1 => 0, _ => 1 } : 2;
                    if (mixed && side == 2) continue;
                    Observed(side + ":pick", () => Click(browser.Pick));
                    if (!mixed || side == 0)
                    {
                        browser.AllowWorkingEdit.IsChecked = true; Observed(side + ":load", () => Click(browser.Load));
                        browser.Entries.SelectedItem = browser.Entries.ItemsSource!.Cast<object>().Single(row => row.ToString()!.StartsWith("docs/leaf.txt （", StringComparison.Ordinal));
                        actions.Add(side + ":leaf"); Jobs();
                    }
                }
                Click(selection.Compare); actions.Add("compare");
                if (submission is null) throw new InvalidOperationException("Compare元Task未観測"); pump(submission); pump(opening); Jobs();
                Verify(id + " adopted new tab", opening.Result && host.SessionPanes.Count == 2 && !ReferenceEquals(host.ActivePane, parent));
                var pane = host.ActivePane; var initial = pane.CaptureProject();
                Verify(id + " initial kinds", Enumerable.Range(0, 3).All(side => initial.TextInputs!.Side(side).Kind == (mixed ? new[] { "Archive", "Physical", "Untitled" }[side] : "Archive")));
                Verify(id + " literal bodies", Enumerable.Range(0, 3).All(side => pane.TextEditor(side).Text == (mixed && side == 2 ? "" : texts[side])));
                Verify(id + " explicit editable metadata", Enumerable.Range(0, 3).Where(side => !mixed || side == 0).All(side => ProjectInputs.Archive(initial, side)!.InheritedReadOnly == false));
                pane.TextSaveTaskObserved = (name, task) => tasks[name] = task;
                for (var side = 0; side < 3; side++) pane.TextEditor(side).Text = edits[side]; Jobs();
                Observed("route-compare", () => Click(Button(pane, "再比較")));
                var expected = edits.ToArray();
                if (source is { } from && destination is { } to)
                {
                    var pair = from + to == 1 ? 0 : from + to == 3 ? 1 : 2;
                    pane.SelectTextPair(pair); pump(pane.IndependentTextUiTask); var forward = from < to;
                    for (var attempt = 0; attempt < 20 && pane.TextEditor(to).Text != edits[from]; attempt++)
                    {
                        pane.NavigateDifference(1); Click(Button(pane, forward ? "選択差分 →" : "← 選択差分")); actions.Add("copy:" + from + ":" + to); pump(pane.IndependentTextUiTask); Jobs();
                    }
                    expected[to] = edits[from]; Verify(id + " copy and third side", Enumerable.Range(0, 3).All(side => pane.TextEditor(side).Text == expected[side]));
                }
                else if (!mixed)
                {
                    for (var side = 0; side < 3; side++)
                    {
                        var captured = side; var label = side == 1 ? "中央の作業版を保存" : new[] { "左を保存", "", "右を保存" }[side];
                        Observed(label, () => Click(Button(pane, label)));
                        Verify(id + " individual working savepoint " + side, Enumerable.Range(0, 3).All(i => pane.TextDirty(i) == (i > captured)));
                    }
                    Verify(id + " three working descriptors", Enumerable.Range(0, 3).All(side => ProjectInputs.Archive(pane.CaptureProject(), side)!.WorkingDocuments is { Length: 1 }));
                }
                void SaveProject(string filename)
                {
                    pane.ProjectPathPicker = _ => Task.FromResult<string?>(PathOf(filename));
                    Observed("project-save", () => Click(Button(pane, "プロジェクトを保存")));
                }
                void Package(string filename)
                {
                    host.PackagePathPicker = () => Task.FromResult<string?>(PathOf(filename));
                    host.PackageTaskObserved = task => tasks["package-save"] = task;
                    Window? dialog = null; host.PackagingDialogShown = shown => dialog = shown;
                    Click(host.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "package-comparisons")); actions.Add("package-open"); Jobs();
                    if (dialog is null) throw new InvalidOperationException("包装実dialog未観測");
                    dialog.GetVisualDescendants().OfType<CheckBox>().Single(b => b.Name == "package-patch").IsChecked = false;
                    Observed("package-save", () => Click(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "package-save")));
                    Click(Button(dialog, "閉じる")); Jobs();
                }
                if (source is null && !mixed) { SaveProject("working.json"); Package("working.zip"); }
                pane.TextSideSavePathPicker = side => Task.FromResult<string?>(PathOf(roles[side] + "-external.txt"));
                foreach (var side in source is null ? new[] { 0, 1, 2 } : new[] { destination!.Value })
                {
                    var label = new[] { "左を外部保存", "中央を外部保存", "右を外部保存" }[side];
                    var before = pane.CaptureProject(); Observed(label, () => Click(Button(pane, label)));
                    Verify(id + " only saved side physical " + side, Enumerable.Range(0, 3).All(i => pane.CaptureProject().TextInputs!.Side(i).Kind == (i == side ? "Physical" : before.TextInputs!.Side(i).Kind)));
                    Verify(id + " external body retained " + side, pane.TextEditor(side).Text == expected[side] && !pane.TextDirty(side));
                }
                if (source is null)
                {
                    Observed("route-compare", () => Click(Button(pane, "再比較")));
                    SaveProject("external.json"); Package("external.zip");
                    pane.ProjectPathPicker = _ => Task.FromResult<string?>(PathOf("external.json"));
                    Observed("project-open", () => Click(Button(pane, "プロジェクトを開く")));
                    pane = host.ActivePane; Verify(id + " reload bodies", Enumerable.Range(0, 3).All(side => pane.TextEditor(side).Text == expected[side] && !pane.TextDirty(side)));
                    pane.TextSaveTaskObserved = (name, task) => tasks[name] = task;
                    foreach (var guard in new[] { "input", "filter", "workspace", "asset", "link" })
                    for (var side = 0; side < 3; side++)
                    {
                        var target = PathOf("guard-" + guard + "-" + side + ".txt"); var sentinel = target;
                        if (guard == "workspace") target = sentinel = PathOf("external.json");
                        else { if (guard == "link") sentinel += ".target"; File.WriteAllBytes(sentinel, "input322 keep\r\n"u8.ToArray()); }
                        if (guard == "link") File.CreateSymbolicLink(target, sentinel);
                        if (guard is "input" or "filter") { var other = host.AddSession(); other.ApplyProject(guard == "input" ? new() { LeftPath = target } : new() { FileFilterPath = target }); host.SelectSession(host.SessionPanes.ToList().IndexOf(pane)); }
                        if (guard == "asset") host.ArchiveLifetime.RegisterAsset(target);
                        var bytes = File.ReadAllBytes(sentinel); var before = pane.CaptureIndependentTextState();
                        pane.TextSideSavePathPicker = _ => Task.FromResult<string?>(target);
                        var label = new[] { "左を外部保存", "中央を外部保存", "右を外部保存" }[side];
                        tasks.Remove(label); Click(Button(pane, label)); actions.Add("guard:" + guard + ":" + side);
                        if (!tasks.Remove(label, out var task)) throw new InvalidOperationException("拒否元Task未観測");
                        var refused = false; try { pump(task); } catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { refused = true; }
                        Jobs(); Verify(id + " guard " + guard + " " + side, refused && File.ReadAllBytes(sentinel).SequenceEqual(bytes) && Equals(before, pane.CaptureIndependentTextState()));
                        var error = host.OwnedWindows.Single(w => w.Title == "操作を完了できませんでした"); Click(Button(error, "閉じる")); Jobs();
                    }
                }
                for (var side = 0; side < 3; side++) Verify(id + " retained original ZIP " + side, File.ReadAllBytes(PathOf("inputs/" + roles[side] + ".zip")).SequenceEqual(File.ReadAllBytes(Path.Combine(fixture, "inputs", roles[side] + ".zip"))));
                using (var frame = host.CaptureRenderedFrame() ?? throw new IOException("描画未取得")) using (var file = File.Create(PathOf("result.png"))) frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                // 閉鎖前に値だけを捕捉し、反射に依存せず固定schemaを出力する。
                var observedTexts = Enumerable.Range(0, 3).Select(side => pane.TextEditor(side).Text).ToArray();
                var observedKinds = Enumerable.Range(0, 3).Select(side => pane.CaptureProject().TextInputs!.Side(side).Kind).ToArray();
                var rootSha = roles.Select(role => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PathOf("inputs/" + role + ".zip"))))).ToArray();
                receipts.Add(writer =>
                {
                    writer.WriteStartObject(); writer.WriteString("id", id); writer.WriteBoolean("mixed", mixed);
                    if (source is { } copySource) writer.WriteNumber("source", copySource); else writer.WriteNull("source");
                    if (destination is { } copyDestination) writer.WriteNumber("destination", copyDestination); else writer.WriteNull("destination");
                    Strings("actions", actions); Strings("expected", expected); Strings("texts", observedTexts);
                    Strings("kinds", observedKinds); Strings("rootSha", rootSha); writer.WriteEndObject();
                    void Strings(string name, IEnumerable<string?> values)
                    { writer.WriteStartArray(name); foreach (var value in values) writer.WriteStringValue(value); writer.WriteEndArray(); }
                });
            }
            finally { host.IndependentTextInputDialogShown = null; host.PackagingDialogShown = null; foreach (var pane in host.SessionPanes) pane.DiscardChanges(); host.Close(); Jobs(); }
        }
        Execute("working-external"); Execute("mixed", mixed: true);
        for (var source = 0; source < 3; source++) for (var destination = 0; destination < 3; destination++) if (source != destination) Execute("copy-" + source + "-" + destination, source, destination);
        using var observations = File.Create(Path.Combine(folder, "observations.json"));
        using var json = new Utf8JsonWriter(observations, new JsonWriterOptions { Indented = true });
        json.WriteStartObject(); json.WriteString("schema", "input322-routes-v1"); json.WriteString("status", "managed-headless-subset");
        json.WriteStartArray("cases"); foreach (var receipt in receipts) receipt(json); json.WriteEndArray(); json.WriteEndObject();
    }
}

public sealed partial class ComparisonPane
{
    internal Func<bool, Task<string?>>? ProjectPathPicker { get; set; }
}

public sealed partial class MainWindow
{
    internal Func<Task<string?>>? PackagePathPicker { get; set; }
    internal Action<Window>? PackagingDialogShown { get; set; }
    internal Action<Task>? PackageTaskObserved { get; set; }
    internal Task ShowPackagingDialogAsync()
    { var dialog = CreatePackagingDialog(); var task = dialog.ShowDialog(this); PackagingDialogShown?.Invoke(dialog); return task; }
    internal async Task<string?> PickPackagingPathAsync()
    {
        if (PackagePathPicker is { } picker) return await picker();
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions { Title = "比較文書の包装先", SuggestedFileName = "comparison.zip", FileTypeChoices = ArchivePickers.FileTypes, ShowOverwritePrompt = true });
        return file?.TryGetLocalPath();
    }
}
