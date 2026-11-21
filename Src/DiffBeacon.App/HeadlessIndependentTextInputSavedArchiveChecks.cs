using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

// 保存版の実再読込みをflat/nestedの別caseとして測定する。native pickerだけを注入する。
internal static class HeadlessIndependentTextInputSavedArchiveChecks
{
    internal static void Run(MainWindow unused, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> unusedScreenshot)
    {
        var folder = Path.Combine(output, "independent-text-input-saved-archives");
        if (Directory.Exists(folder)) throw new IOException("保存版検証は新しいrunが必要です。");
        Directory.CreateDirectory(folder);
        var fixture = Path.GetFullPath("tests/Fixtures/IndependentTextInputSavedArchives");
        string[] roles = ["left", "middle", "right"];
        string[] texts = ["shared café\r\nLEFT €\nend-left\r", "shared café\r\nMIDDLE £\nend-middle\r", "shared café\r\nRIGHT “quote”\nend-right\r"];
        var edits = texts.Select(value => value.Replace("end-", "edited-", StringComparison.Ordinal)).ToArray();
        var cases = new List<Action<Utf8JsonWriter>>();
        var containerCases = new List<Action<Utf8JsonWriter>>();
        void Jobs() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        void Click(Button control) { if (!control.IsEnabled) throw new InvalidOperationException("無効なbutton: " + control.Content); control.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); }
        Button Find(Control owner, string text) => owner.GetVisualDescendants().OfType<Button>().Single(control => Equals(control.Content, text));
        foreach (var depth in new[] { 1, 2 })
        {
            var run = Path.Combine(folder, "container-" + depth); Directory.CreateDirectory(run); Directory.CreateDirectory(Path.Combine(run, "inputs"));
            foreach (var role in roles) File.Copy(Path.Combine(fixture, "inputs", "nested", role + ".zip"), Path.Combine(run, "inputs", role + ".zip"));
            string PathOf(string name) => Path.GetFullPath(Path.Combine(run, name));
            void Trace(string name, string root, IEnumerable<string> chain, IEnumerable<string?> rows)
            {
                using var file = File.Create(PathOf(name)); using var writer = new Utf8JsonWriter(file);
                writer.WriteStartObject(); writer.WriteString("rootPath", root); writer.WriteStartArray("entryChain"); foreach (var entry in chain) writer.WriteStringValue(entry); writer.WriteEndArray();
                writer.WriteStartArray("rows"); foreach (var row in rows) writer.WriteStringValue(row); writer.WriteEndArray(); writer.WriteEndObject();
            }
            var chain = depth == 1 ? new[] { "inner.zip" } : new[] { "inner.zip", "deep.zip" };
            ArchiveProjectInput Input(int side) => new() { RootPath = PathOf("inputs/" + roles[side] + ".zip"), EntryChain = chain.ToArray(), RootSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(PathOf("inputs/" + roles[side] + ".zip")))), InheritedReadOnly = false };
            var host = new MainWindow(null, new ImageApplicationOptionsStore(PathOf("options.json"))) { Width = 1000, Height = 680 }; host.Show(); Jobs();
            var tasks = new Dictionary<string, Task>(); var actions = new List<string>(); var reads = new int[3];
            Task<bool>? opening = null; Task? submission = null; IndependentTextInputDialog? dialog = null;
            void Verify(string name, bool passed) { check("Saved Archive container-" + depth + " " + name, passed, ""); if (!passed) throw new InvalidOperationException(name); }
            void Observed(string name, Action click) { tasks.Remove(name); click(); actions.Add(name); if (!tasks.Remove(name, out var task)) throw new InvalidOperationException("元Task未観測: " + name); pump(task); Verify("task completed " + name, task.IsCompletedSuccessfully); Jobs(); }
            var parent = host.ActivePane;
            parent.ApplyProject(new ComparisonProject { Mode = depth == 1 ? "Archive" : "7",
                LeftArchiveInput = Input(0), RightArchiveInput = Input(2), LeftReadOnly = true, RightReadOnly = true });
            parent.TextSaveTaskObserved = (name, task) => tasks[name == "path-compare" ? "parent-compare" : name] = task;
            host.IndependentTextInputOperationObserved = task => opening = task;
            host.IndependentTextInputDialogShown = shown =>
            {
                dialog = shown; shown.SubmissionTaskObserved = task => submission = task;
                for (var side = 0; side < 3; side++)
                {
                    var captured = side; shown.Side(side).OperationTaskObserved = (name, task) => tasks[captured + ":" + name] = task;
                    shown.Side(side).PickerPathProvider = (_, _, _) => Task.FromResult<string?>(PathOf("inputs/" + roles[captured] + ".zip"));
                    shown.Side(side).ManifestReadGate = (_, request, _) => { Trace(captured + "-load-request-" + (++reads[captured]) + ".json", request.RootPath, request.EntryChain, []); return Task.CompletedTask; };
                }
            };
            try
            {
                Observed("parent-compare", () => Click(parent.CompareButton));
                var initial = parent.CaptureProject();
                File.WriteAllBytes(PathOf("parent.json"), JsonSerializer.SerializeToUtf8Bytes(initial, ProjectJsonContext.Default.ComparisonProject));
                Verify("parent container routes", initial.Mode == "Archive" && new[] { 0, 2 }.All(side => ProjectInputs.Archive(initial, side) is { LeafEntry: null } input && input.EntryChain.SequenceEqual(chain)));
                Click(parent.IndependentTextInputsButton); actions.Add("entry"); Jobs();
                if (dialog is null && opening is { IsFaulted: true }) throw opening.Exception!.GetBaseException();
                Verify("public dialog original task", dialog is not null && opening is not null && !opening.IsCompleted);
                Click(dialog!.Compare); actions.Add("initial-compare"); if (submission is null) throw new InvalidOperationException("初期Compare元Task未観測");
                pump(submission); Jobs(); Verify("no initial leaf submission", submission.IsCompletedSuccessfully && !opening!.IsCompleted && host.SessionPanes.Count == 1); submission = null;
                for (var side = 0; side < 3; side++)
                {
                    var browser = dialog.Side(side); dialog.SideTabs.SelectedIndex = side; Jobs();
                    if (side == 1) { browser.Kind.SelectedIndex = 2; browser.ReadOnly.IsChecked = true; browser.AllowWorkingEdit.IsChecked = true; }
                    Verify("initial kind and readonly " + side, browser.KindName == "Archive" && browser.ReadOnly.IsChecked == true && browser.AllowWorkingEdit.IsChecked == true);
                    Verify("initial container no leaf " + side, browser.Summary.Contains("葉未選択", StringComparison.Ordinal) && browser.Entries.SelectedItem is null);
                    if (side == 1) { Observed(side + ":pick", () => Click(browser.Pick)); Observed(side + ":pick", () => Click(browser.Pick)); }
                    Observed(side + ":load", () => Click(browser.Load));
                    Trace(side + "-load-rows.json", browser.Summary, [], browser.Entries.ItemsSource?.Cast<object>().Select(row => row.ToString()) ?? []);
                    Verify("verified container no leaf " + side, browser.Entries.SelectedItem is null);
                    string[] Names() => browser.Entries.ItemsSource!.Cast<object>().Select(row => row.ToString()!.Split(" （", StringSplitOptions.None)[0]).Order(StringComparer.Ordinal).ToArray();
                    var expected = side == 1 ? new[] { "inner.zip", "outer.bin" } : depth == 1 ? new[] { "deep.zip", "inner.bin" } : new[] { "docs/leaf.txt", "docs/other.bin" };
                    Verify("literal initial listing " + side, Names().SequenceEqual(expected));
                    if (side != 1)
                    {
                        Observed(side + ":pick", () => Click(browser.Pick)); Observed(side + ":pick", () => Click(browser.Pick)); Observed(side + ":load", () => Click(browser.Load));
                        Verify("same root retains container route " + side, Names().SequenceEqual(expected) && browser.Entries.SelectedItem is null);
                    }
                    if (side == 0)
                    {
                        File.Copy(PathOf("inputs/left.zip"), PathOf("inputs/changed-left.zip"));
                        browser.PickerPathProvider = (_, _, _) => Task.FromResult<string?>(PathOf("inputs/changed-left.zip"));
                        Observed(side + ":pick", () => Click(browser.Pick)); Verify("changed root clears selection", browser.Entries.SelectedItem is null && browser.PasswordFields.Count == 1);
                        Observed(side + ":load", () => Click(browser.Load)); Verify("changed root clears container route", Names().SequenceEqual(new[] { "inner.zip", "outer.bin" }) && browser.Entries.SelectedItem is null);
                        browser.PickerPathProvider = (_, _, _) => Task.FromResult<string?>(PathOf("inputs/left.zip"));
                        Observed(side + ":pick", () => Click(browser.Pick)); Observed(side + ":load", () => Click(browser.Load)); Verify("restored root starts at root", Names().SequenceEqual(new[] { "inner.zip", "outer.bin" }));
                    }
                    void Row(string name) { browser.Entries.SelectedItem = browser.Entries.ItemsSource!.Cast<object>().Single(row => row.ToString()!.StartsWith(name + " （", StringComparison.Ordinal)); Jobs(); actions.Add(side + ":row:" + name); }
                    if (side is 0 or 1) { Row("inner.zip"); Observed(side + ":open", () => Click(browser.OpenContainer)); }
                    if (depth == 1 || side is 0 or 1) { Row("deep.zip"); Observed(side + ":open", () => Click(browser.OpenContainer)); }
                    Row("docs/leaf.txt");
                }
                Click(dialog.Compare); actions.Add("compare"); if (submission is null || opening is null) throw new InvalidOperationException("Compare元Task未観測");
                pump(submission); pump(opening); Jobs(); Verify("original submission and opening completed", submission.IsCompletedSuccessfully && opening.IsCompletedSuccessfully);
                Verify("new tab adopted", opening.Result && host.SessionPanes.Count == 2);
                var pane = host.ActivePane; var adopted = pane.CaptureProject();
                File.WriteAllBytes(PathOf("adopted.json"), JsonSerializer.SerializeToUtf8Bytes(adopted, ProjectJsonContext.Default.ComparisonProject));
                var bodies = Enumerable.Range(0, 3).Select(side => pane.TextEditor(side).Text).ToArray();
                var leafReadOnly = new[] { adopted.LeftReadOnly, adopted.BaseReadOnly, adopted.RightReadOnly };
                var editorReadOnly = Enumerable.Range(0, 3).Select(side => pane.TextEditor(side).IsReadOnly).ToArray();
                Verify("literal bodies and readonly originals", bodies.SequenceEqual(texts) && leafReadOnly.All(value => value) && editorReadOnly.All(value => !value));
                Verify("explicit adopted leaf route", Enumerable.Range(0, 3).All(side => ProjectInputs.Archive(adopted, side) is { LeafEntry: "docs/leaf.txt", InheritedReadOnly: false } input && input.EntryChain.SequenceEqual(new[] { "inner.zip", "deep.zip" })));
                using (var frame = host.CaptureRenderedFrame() ?? throw new IOException("描画未取得")) using (var image = File.Create(PathOf("adopted.png"))) frame.Save(image, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                containerCases.Add(writer =>
                {
                    writer.WriteStartObject(); writer.WriteNumber("depth", depth); writer.WriteString("parentMode", initial.Mode); writer.WriteBoolean("initialLeafNull", new[] { 0, 2 }.All(side => ProjectInputs.Archive(initial, side)!.LeafEntry is null));
                    writer.WriteStartArray("initialChain"); foreach (var name in chain) writer.WriteStringValue(name); writer.WriteEndArray();
                    writer.WriteStartArray("actions"); foreach (var action in actions) writer.WriteStringValue(action); writer.WriteEndArray();
                    writer.WriteStartArray("texts"); foreach (var body in bodies) writer.WriteStringValue(body); writer.WriteEndArray();
                    writer.WriteStartArray("leafReadOnly"); foreach (var value in leafReadOnly) writer.WriteBooleanValue(value); writer.WriteEndArray();
                    writer.WriteStartArray("editorReadOnly"); foreach (var value in editorReadOnly) writer.WriteBooleanValue(value); writer.WriteEndArray(); writer.WriteEndObject();
                });
            }
            finally { host.IndependentTextInputDialogShown = null; foreach (var pane in host.SessionPanes) pane.DiscardChanges(); host.Close(); Jobs(); }
        }
        foreach (var shape in new[] { "flat", "nested" })
        {
            var run = Path.Combine(folder, shape); Directory.CreateDirectory(run); Directory.CreateDirectory(Path.Combine(run, "inputs"));
            foreach (var role in roles) File.Copy(Path.Combine(fixture, "inputs", shape, role + ".zip"), Path.Combine(run, "inputs", role + ".zip"));
            string PathOf(string name) => Path.Combine(run, name);
            var host = new MainWindow(null, new ImageApplicationOptionsStore(PathOf("options.json"))) { Width = 1000, Height = 680 }; host.Show(); Jobs();
            var tasks = new Dictionary<string, Task>(); var actions = new List<string>(); var snapshots = new List<Action<Utf8JsonWriter>>();
            Task<bool>? opening = null; Task? submission = null; IndependentTextInputDialog? inputDialog = null;
            void Verify(string name, bool passed) { check("Saved Archive " + shape + " " + name, passed, ""); if (!passed) throw new InvalidOperationException(name); }
            void Observed(string name, Action click)
            { tasks.Remove(name); click(); actions.Add(name); if (!tasks.Remove(name, out var task)) throw new InvalidOperationException("元Task未観測: " + name); pump(task); Verify("task completed " + name, task.IsCompletedSuccessfully); Jobs(); }
            void Bind(ComparisonPane pane) => pane.TextSaveTaskObserved = (name, task) => tasks[name] = task;
            void Snapshot(string stage, ComparisonPane pane)
            {
                var project = pane.CaptureProject();
                var bodies = Enumerable.Range(0, 3).Select(side => pane.TextEditor(side).Text).ToArray();
                var dirty = Enumerable.Range(0, 3).Select(pane.TextDirty).ToArray();
                var editorsReadOnly = Enumerable.Range(0, 3).Select(side => pane.TextEditor(side).IsReadOnly).ToArray();
                var leavesReadOnly = new[] { project.LeftReadOnly, project.BaseReadOnly, project.RightReadOnly };
                var archives = Enumerable.Range(0, 3).Select(side => ProjectInputs.Archive(project, side)!).ToArray();
                var encodings = archives.Select(input => input.WorkingDocuments!.Single().EncodingName).ToArray();
                var boms = archives.Select(input => input.WorkingDocuments!.Single().HasBom).ToArray();
                Verify(stage + " three literal saved bodies", bodies.SequenceEqual(edits) && dirty.All(value => !value));
                Verify(stage + " readonly original leaves and editable saved bodies", leavesReadOnly.All(value => value) && editorsReadOnly.All(value => !value) && archives.All(input => input.InheritedReadOnly == false));
                Verify(stage + " exact nested routes", archives.All(input => input.EntryChain.SequenceEqual(shape == "nested" ? new[] { "inner.zip", "deep.zip" } : Array.Empty<string>()) && input.LeafEntry == "docs/leaf.txt"));
                Verify(stage + " saved encoding and BOM", encodings.SequenceEqual(new[] { "utf-8", "utf-16", "utf-8" }) && boms.SequenceEqual(new[] { true, true, false }));
                snapshots.Add(writer =>
                {
                    writer.WriteStartObject(); writer.WriteString("stage", stage); Strings("texts", bodies); Strings("encodings", encodings);
                    Bools("hasBom", boms); Bools("dirty", dirty); Bools("editorReadOnly", editorsReadOnly); Bools("leafReadOnly", leavesReadOnly); writer.WriteEndObject();
                    void Strings(string name, IEnumerable<string?> values) { writer.WriteStartArray(name); foreach (var value in values) writer.WriteStringValue(value); writer.WriteEndArray(); }
                    void Bools(string name, IEnumerable<bool> values) { writer.WriteStartArray(name); foreach (var value in values) writer.WriteBooleanValue(value); writer.WriteEndArray(); }
                });
                using var frame = host.CaptureRenderedFrame() ?? throw new IOException("描画未取得"); using var image = File.Create(PathOf(stage + ".png")); frame.Save(image, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            host.IndependentTextInputOperationObserved = task => opening = task;
            host.IndependentTextInputDialogShown = shown =>
            {
                inputDialog = shown; shown.SubmissionTaskObserved = task => submission = task;
                for (var side = 0; side < 3; side++)
                {
                    var captured = side; shown.Side(side).OperationTaskObserved = (name, task) => tasks[captured + ":" + name] = task;
                    shown.Side(side).PickerPathProvider = (_, _, _) => Task.FromResult<string?>(PathOf("inputs/" + roles[captured] + ".zip"));
                }
            };
            try
            {
                Click(host.ActivePane.IndependentTextInputsButton); actions.Add("entry");
                if (inputDialog is null || opening is null) throw new InvalidOperationException("入口/dialog元Task未観測");
                for (var side = 0; side < 3; side++)
                {
                    var browser = inputDialog.Side(side); inputDialog.SideTabs.SelectedIndex = side; Jobs(); browser.Kind.SelectedIndex = 2;
                    Observed(side + ":pick", () => Click(browser.Pick)); Observed(side + ":pick", () => Click(browser.Pick)); browser.AllowWorkingEdit.IsChecked = true; Observed(side + ":load", () => Click(browser.Load));
                    void Row(string name) { browser.Entries.SelectedItem = browser.Entries.ItemsSource!.Cast<object>().Single(row => row.ToString()!.StartsWith(name + " （", StringComparison.Ordinal)); Jobs(); actions.Add(side + ":row:" + name); }
                    if (shape == "nested") foreach (var name in new[] { "inner.zip", "deep.zip" }) { Row(name); Observed(side + ":open", () => Click(browser.OpenContainer)); }
                    Row("docs/leaf.txt");
                }
                Click(inputDialog.Compare); actions.Add("compare"); if (submission is null) throw new InvalidOperationException("Compare元Task未観測"); pump(submission); pump(opening); Jobs(); Verify("new tab adopted", opening.Result && host.SessionPanes.Count == 2);
                var pane = host.ActivePane; Bind(pane); Verify("original literal bodies", Enumerable.Range(0, 3).All(side => pane.TextEditor(side).Text == texts[side]));
                for (var side = 0; side < 3; side++) pane.TextEditor(side).Text = edits[side];
                Observed("route-compare", () => Click(Find(pane, "再比較")));
                foreach (var label in new[] { "左を保存", "中央の作業版を保存", "右を保存" }) Observed(label, () => Click(Find(pane, label)));
                pane.ProjectPathPicker = _ => Task.FromResult<string?>(PathOf("working.json")); Observed("project-save", () => Click(Find(pane, "プロジェクトを保存")));
                host.PackagePathPicker = () => Task.FromResult<string?>(PathOf("working.zip")); host.PackageTaskObserved = task => tasks["package-save"] = task;
                Window? packaging = null; host.PackagingDialogShown = shown => packaging = shown;
                Click(host.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "package-comparisons")); actions.Add("package-open"); Jobs();
                if (packaging is null) throw new InvalidOperationException("包装dialog未観測");
                packaging.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "package-patch").IsChecked = false;
                Observed("package-save", () => Click(packaging.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "package-save"))); Click(Find(packaging, "閉じる")); Jobs();
                Observed("project-open", () => Click(Find(pane, "プロジェクトを開く"))); pane = host.ActivePane; Bind(pane); Snapshot("workspace-reload", pane);
                // 製品の通常Archive比較と実展開buttonを通り、生成物のprojectを実再読込みする。
                var packagePane = host.AddSession(); packagePane.ApplyProject(new ComparisonProject { Mode = "Archive", LeftPath = PathOf("working.zip"), RightPath = PathOf("working.zip") }); Bind(packagePane);
                Observed("path-compare", () => Click(packagePane.CompareButton));
                var archivePanel = packagePane.GetVisualDescendants().OfType<ArchivePanel>().Single();
                archivePanel.ExtractionParentPathPicker = () => Task.FromResult<string?>(run); archivePanel.ExtractionName.Text = "expanded";
                archivePanel.ButtonTaskObserved = (name, task) => tasks[name] = task;
                Observed("左をすべて展開", () => Click(Find(archivePanel, "左をすべて展開")));
                packagePane.ProjectPathPicker = _ => Task.FromResult<string?>(PathOf("expanded/project.json"));
                Observed("project-open", () => Click(Find(packagePane, "プロジェクトを開く"))); pane = host.ActivePane; Bind(pane); Snapshot("package-reload", pane);
                cases.Add(writer => { writer.WriteStartObject(); writer.WriteString("shape", shape); writer.WriteStartArray("actions"); foreach (var action in actions) writer.WriteStringValue(action); writer.WriteEndArray(); writer.WriteStartArray("snapshots"); foreach (var snapshot in snapshots) snapshot(writer); writer.WriteEndArray(); writer.WriteEndObject(); });
            }
            finally { host.IndependentTextInputDialogShown = null; host.PackagingDialogShown = null; foreach (var pane in host.SessionPanes) pane.DiscardChanges(); host.Close(); Jobs(); }
        }
        using var file = File.Create(Path.Combine(folder, "observations.json")); using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        json.WriteStartObject(); json.WriteString("schema", "input348-saved-archive-v1"); json.WriteStartArray("cases"); foreach (var write in cases) write(json); json.WriteEndArray(); json.WriteStartArray("containerCases"); foreach (var write in containerCases) write(json); json.WriteEndArray(); json.WriteEndObject();
    }
}
