using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

// 実暗号化outerと、別々のpasswordを持つinner兄弟を実buttonで操作する。
internal static class HeadlessIndependentTextInputSelectionCipherChecks
{
    private const string Outer = "input306-public-outer", Left = "input306-public-left", Right = "input306-public-right";
    internal static void Run(MainWindow unusedWindow, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> unusedScreenshot)
    {
        var folder = Path.Combine(output, "independent-text-input-selection-cipher");
        if (Directory.Exists(folder)) throw new IOException("input306は新しいrunが必要です。");
        Directory.CreateDirectory(folder);
        var fixture = Path.GetFullPath("tests/Fixtures/IndependentTextInputSelectionCipher");
        var root = Path.Combine(folder, "cipher-siblings.zip");
        File.Copy(Path.Combine(fixture, "inputs", "cipher-siblings.zip"), root); FixTime(root);
        File.Copy(Path.Combine(fixture, "manifest.json"), Path.Combine(folder, "manifest.json"));
        var parentLeft = Path.Combine(folder, "parent-left.txt"); var parentRight = Path.Combine(folder, "parent-right.txt");
        File.WriteAllBytes(parentLeft, "input306 parent left\r\n"u8.ToArray()); FixTime(parentLeft);
        File.WriteAllBytes(parentRight, "input306 parent right\r\n"u8.ToArray()); FixTime(parentRight);
        var host = new MainWindow(null, new ImageApplicationOptionsStore(Path.Combine(folder, "options.json"))) { Width = 1000, Height = 680 };
        host.Show(); Jobs(); var parent = host.ActivePane;
        parent.ApplyProject(new ComparisonProject { Mode = "Text", LeftPath = parentLeft, RightPath = parentRight });
        pump(parent.ComparePathsAsync()); parent.TextEditor(0).Text += "parent dirty\n"; Jobs();
        var before = parent.CaptureInputSelectionTestEvidence(); var identity = parent.CaptureIndependentTextState();
        var store = host.ArchiveTexts; var storeGeneration = store.Generation; var tabs = host.SessionPanes.Count();
        IndependentTextInputDialog? dialog = null; Task<bool>? opening = null; Task? submission = null;
        var pending = new Dictionary<string, Task>(); var steps = new List<JsonElement>(); var actions = new List<string>();
        var checks = new Dictionary<string, bool>(); var fields = new List<TextBox>(); var layouts = new List<JsonElement>(); var candidates = 0;
        void Verify(string name, bool condition)
        {
            checks[name] = condition; check("Input306 " + name, condition, "");
        }
        void Jobs() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Track(IndependentTextArchiveBrowser browser)
        {
            foreach (var field in browser.PasswordFields) if (!fields.Contains(field)) fields.Add(field);
        }
        string[] Rows(IndependentTextArchiveBrowser browser) => browser.Entries.ItemsSource?.Cast<object>().Select(row => row.ToString()!).ToArray() ?? [];
        void Step(string name, IndependentTextArchiveBrowser browser)
        {
            Track(browser);
            steps.Add(Json(w =>
            {
                w.WriteStartObject(); w.WriteString("name", name); w.WriteString("summary", browser.Summary);
                w.WriteStartArray("rows"); foreach (var row in Rows(browser)) w.WriteStringValue(row); w.WriteEndArray();
                w.WriteStartArray("fieldLengths"); foreach (var field in browser.PasswordFields) w.WriteNumberValue(field.Text?.Length ?? 0); w.WriteEndArray();
                w.WriteBoolean("outerRetained", browser.PasswordFields.Count > 0 && browser.PasswordFields[0].Text == Outer); w.WriteEndObject();
            }));
        }
        void Operate(string name, Button button)
        {
            pending.Remove(name); Click(button); actions.Add(name);
            if (!pending.Remove(name, out var task)) throw new InvalidOperationException("元Taskが未観測です: " + name);
            pump(task); Jobs(); Verify("task-" + actions.Count, task.IsCompletedSuccessfully);
        }
        void Select(IndependentTextArchiveBrowser browser, string path)
        {
            browser.Entries.SelectedItem = (browser.Entries.ItemsSource?.Cast<object>() ?? [])
                .Single(row => row.ToString()!.StartsWith(path + " （", StringComparison.Ordinal));
            actions.Add("row:" + path); Jobs();
        }
        void Reject()
        {
            submission = null; Click(dialog!.Compare); actions.Add("compare-reject");
            if (submission is null) throw new InvalidOperationException("元Compare Taskが未観測です。");
            pump(submission); Jobs(); Verify("reject-" + actions.Count, submission.IsCompletedSuccessfully && candidates == 0 && host.SessionPanes.Count() == tabs && dialog.IsVisible);
        }
        host.IndependentTextInputDialogShown = shown =>
        {
            dialog = shown; shown.SubmissionTaskObserved = task => submission = task;
            for (var side = 0; side < 3; side++) shown.Side(side).Kind.SelectedIndex = 1;
            var browser = shown.Side(0); browser.OperationTaskObserved = (name, task) => pending[name] = task;
            browser.PickerPathProvider = (_, _, _) => Task.FromResult<string?>(root);
        };
        host.IndependentTextInputOperationObserved = task => opening = task;
        host.IndependentTextInputCandidateCreated = _ => candidates++;
        try
        {
            Click(parent.IndependentTextInputsButton); actions.Add("entry"); Jobs();
            if (dialog is null || opening is null) throw new InvalidOperationException("実入力選択dialogが未観測です。");
            var browser = dialog.Side(0); browser.Kind.SelectedIndex = 2; Operate("pick", browser.Pick); Track(browser);
            Verify("initial-optional", browser.PasswordFields.Count == 1 && string.IsNullOrEmpty(browser.PasswordFields[0].Text));
            Operate("load", browser.Load); Step("outer-missing", browser); Reject();
            browser.PasswordFields[0].Text = "input306-wrong"; Operate("load", browser.Load); Step("outer-wrong", browser); Reject();
            browser.PasswordFields[0].Text = Outer; Operate("load", browser.Load); Step("outer-correct", browser);
            var outerRows = Rows(browser);
            Select(browser, "left.zip"); Operate("open", browser.OpenContainer); Step("left-missing", browser); Reject();
            Verify("left-common-outer", browser.PasswordFields.Count == 2 && browser.PasswordFields[0].Text == Outer && string.IsNullOrEmpty(browser.PasswordFields[1].Text));
            Verify("left-missing-display-held", outerRows.SequenceEqual(Rows(browser)));
            browser.PasswordFields[1].Text = Right; Operate("load", browser.Load); Step("left-cross", browser); Reject();
            Verify("left-cross-display-held", outerRows.SequenceEqual(Rows(browser)));
            browser.PasswordFields[1].Text = Left; Operate("load", browser.Load); Step("left-correct", browser); Select(browser, "leaf.txt");
            Track(browser); var leftField = browser.PasswordFields[1];
            Operate("back", browser.Back); Step("outer-back", browser);
            Verify("back-outer-retained", browser.PasswordFields.Count == 1 && browser.PasswordFields[0].Text == Outer && outerRows.SequenceEqual(Rows(browser)));
            Verify("removed-left-field-cleared", string.IsNullOrEmpty(leftField.Text));
            Select(browser, "right.zip"); Operate("open", browser.OpenContainer); Step("right-missing", browser); Reject();
            Verify("right-common-outer-only", browser.PasswordFields.Count == 2 && browser.PasswordFields[0].Text == Outer && string.IsNullOrEmpty(browser.PasswordFields[1].Text));
            Verify("right-missing-display-held", outerRows.SequenceEqual(Rows(browser)));
            browser.PasswordFields[1].Text = Left; Operate("load", browser.Load); Step("right-cross", browser); Reject();
            Verify("right-cross-display-held", outerRows.SequenceEqual(Rows(browser)));
            browser.PasswordFields[1].Text = Right; Operate("load", browser.Load); Step("right-correct", browser); Select(browser, "leaf.txt");
            foreach (var size in new[] { (1000d, 680d), (850d, 550d) })
            {
                dialog.Width = size.Item1; dialog.Height = size.Item2; Jobs();
                foreach (var field in browser.PasswordFields)
                {
                    Verify("masked-" + layouts.Count, field.PasswordChar == '●'); field.BringIntoView(); Jobs(); Capture(dialog, field, "field");
                }
                browser.Load.BringIntoView(); Jobs(); Capture(dialog, browser.Load, "load");
                browser.Entries.BringIntoView(); Jobs(); Capture(dialog, browser.Entries, "list");
            }
            submission = null; Click(dialog.Compare); actions.Add("compare-accept");
            if (submission is null) throw new InvalidOperationException("採用Compare Taskが未観測です。");
            pump(submission); pump(opening); Jobs();
            Verify("one-adopted-tab", opening.Result && candidates == 1 && host.SessionPanes.Count() == tabs + 1 && !ReferenceEquals(host.ActivePane, parent));
            Verify("opener-terminal", opening.IsCompletedSuccessfully && submission.IsCompletedSuccessfully);
            Verify("closed-fields-clear", fields.All(field => string.IsNullOrEmpty(field.Text)) && Enumerable.Range(0, 3).All(side => dialog.Side(side).PasswordFields.Count == 0));
            Verify("parent-identity-store-held", Equals(identity, parent.CaptureIndependentTextState()) && ReferenceEquals(store, host.ArchiveTexts) && storeGeneration == store.Generation);
            var adopted = host.ActivePane; var project = adopted.CaptureProject();
            File.WriteAllBytes(Path.Combine(folder, "adopted-project.json"), JsonSerializer.SerializeToUtf8Bytes(project, ProjectJsonContext.Default.ComparisonProject));
            var after = parent.CaptureInputSelectionTestEvidence(); var payload = adopted.CaptureInputSelectionTestEvidence();
            using var file = File.Create(Path.Combine(folder, "observations.json")); using var w = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
            w.WriteStartObject(); w.WriteString("schema", "input306-observations-v1"); w.WriteString("status", "completed"); w.WriteString("id", "encrypted-outer-different-inner-siblings");
            w.WriteBoolean("accepted", opening.Result); w.WriteNumber("candidateCount", candidates); w.WriteNumber("tabsBefore", tabs); w.WriteNumber("tabsAfter", host.SessionPanes.Count());
            w.WritePropertyName("before"); before(w); w.WritePropertyName("after"); after(w); w.WritePropertyName("payload"); payload(w);
            w.WriteStartArray("actions"); foreach (var action in actions) w.WriteStringValue(action); w.WriteEndArray();
            w.WriteStartArray("steps"); foreach (var step in steps) step.WriteTo(w); w.WriteEndArray();
            w.WriteStartArray("layouts"); foreach (var layout in layouts) layout.WriteTo(w); w.WriteEndArray();
            w.WriteStartObject("checks"); foreach (var item in checks) w.WriteBoolean(item.Key, item.Value); w.WriteEndObject(); w.WriteEndObject();
        }
        finally
        {
            if (dialog?.IsVisible == true) { Click(dialog.Cancel); if (opening is not null) pump(opening); }
            host.IndependentTextInputDialogShown = null; host.IndependentTextInputOperationObserved = null; host.IndependentTextInputCandidateCreated = null;
            foreach (var pane in host.SessionPanes) pane.DiscardChanges(); host.Close(); Jobs();
        }
        void Capture(Window owner, Control control, string kind)
        {
            var name = "cipher-" + layouts.Count + ".png"; Jobs();
            using (var frame = owner.CaptureRenderedFrame() ?? throw new InvalidOperationException("PNGが未取得です。"))
            using (var file = File.Create(Path.Combine(folder, name))) frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            var point = control.TranslatePoint(new Point(), owner) ?? throw new InvalidOperationException("visualが未接続です。");
            var rect = new Rect(point, control.Bounds.Size); var clip = new Rect(0, 0, owner.Bounds.Width, owner.Bounds.Height);
            foreach (var ancestor in control.GetVisualAncestors().OfType<Control>().Where(a => a.ClipToBounds))
                if (ancestor.TranslatePoint(new Point(), owner) is { } location) clip = clip.Intersect(new Rect(location, ancestor.Bounds.Size));
            var visible = control.IsVisible && control.GetVisualAncestors().OfType<Control>().All(a => a.IsVisible);
            Verify("reach-" + layouts.Count, visible && rect.Width > 0 && rect.Height > 0 && clip.Contains(rect) && (kind != "list" || rect.Height >= 100));
            layouts.Add(Json(w =>
            {
                w.WriteStartObject(); w.WriteString("png", name); w.WriteString("control", kind); w.WriteNumber("width", owner.Bounds.Width); w.WriteNumber("height", owner.Bounds.Height);
                w.WriteBoolean("visible", visible); RectValue(w, "bounds", rect); RectValue(w, "clip", clip); w.WriteEndObject();
            }));
        }
    }
    private static JsonElement Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) write(writer);
        using var document = JsonDocument.Parse(stream.ToArray()); return document.RootElement.Clone();
    }
    private static void RectValue(Utf8JsonWriter w, string name, Rect rect)
    {
        w.WriteStartObject(name); w.WriteNumber("x", rect.X); w.WriteNumber("y", rect.Y); w.WriteNumber("width", rect.Width); w.WriteNumber("height", rect.Height); w.WriteEndObject();
    }
    private static void FixTime(string path) => File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234567));
}
