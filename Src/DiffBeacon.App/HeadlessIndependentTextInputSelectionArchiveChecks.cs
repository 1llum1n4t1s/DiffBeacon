using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

// 実buttonから観測する元Taskのみを待つ。passwordの値は証跡へ書かない。
internal static class HeadlessIndependentTextInputSelectionArchiveChecks
{
    private const string Outer = "outer-source-fixture", Inner = "inner-source-fixture";
    internal static void Run(MainWindow unusedWindow, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> unusedScreenshot)
    {
        var folder = Path.Combine(output, "independent-text-input-selection-archives");
        if (Directory.Exists(folder)) throw new IOException("input301はクリーンなrunが必要です。");
        Directory.CreateDirectory(folder);
        var fixture = Path.GetFullPath("tests/Fixtures/IndependentTextInputSelectionArchives");
        var source = Path.GetFullPath("tests/Fixtures/Archives/Sources");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture, "manifest.json")));
        File.Copy(Path.Combine(fixture, "manifest.json"), Path.Combine(folder, "manifest.json"));
        var inputNames = new[] { "two-passwords.zip", "plain-encrypted.zip", "leaf.zip", "late-bad-sibling.zip", "depth-8.zip", "depth-9.zip", "root-a.zip" };
        foreach (var name in inputNames) CopyInput(Path.Combine(source, name), Path.Combine(folder, name));
        foreach (var path in Directory.EnumerateFiles(Path.Combine(fixture, "inputs"))) CopyInput(path, Path.Combine(folder, Path.GetFileName(path)));
        File.WriteAllBytes(Path.Combine(folder, "parent-left.txt"), "input301 parent left\r\n"u8.ToArray());
        File.WriteAllBytes(Path.Combine(folder, "parent-right.txt"), "input301 parent right\r\n"u8.ToArray());
        FixTime(Path.Combine(folder, "parent-left.txt")); FixTime(Path.Combine(folder, "parent-right.txt"));
        var receipts = new List<JsonElement>(); var layouts = new List<JsonElement>();
        void Jobs() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Verify(string id, bool passed, string detail = "") => check("Input301 " + id, passed, detail);
        string Input(string name) => Path.Combine(folder, name);

        foreach (var spec in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = spec.GetProperty("id").GetString()!;
            var host = new MainWindow(null, new ImageApplicationOptionsStore(Input("options-" + id + ".json"))) { Width = 1000, Height = 680 };
            host.Show(); Jobs();
            var parent = host.ActivePane;
            parent.ApplyProject(new ComparisonProject { Mode = "Text", LeftPath = Input("parent-left.txt"), RightPath = Input("parent-right.txt") });
            pump(parent.ComparePathsAsync()); parent.TextEditor(0).Text += "parent dirty\n"; Jobs();
            var before = parent.CaptureInputSelectionTestEvidence(); var identity = parent.CaptureIndependentTextState();
            var store = host.ArchiveTexts; var generation = store.Generation; var tabs = host.SessionPanes.Count();
            var steps = new List<JsonElement>(); var actions = new List<string>(); var checks = new Dictionary<string, bool>();
            var fields = new List<TextBox>(); var pending = new Dictionary<string, Task>();
            Task<bool>? opening = null; Task? submission = null; IndependentTextInputDialog? dialog = null;
            var candidates = 0; var accepted = false; var gateCancelled = false; var linkCreated = false;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Assert(string name, bool passed) { checks[name] = passed; Verify(id + " " + name, passed); }
            void Track(IndependentTextArchiveBrowser browser)
            {
                foreach (var box in browser.PasswordFields) if (!fields.Contains(box)) fields.Add(box);
            }
            void Step(string name, IndependentTextArchiveBrowser browser)
            {
                Track(browser);
                steps.Add(Json(w =>
                {
                    w.WriteStartObject(); w.WriteString("name", name); w.WriteString("summary", browser.Summary);
                    w.WriteStartArray("rows"); foreach (var item in browser.Entries.ItemsSource?.Cast<object>() ?? []) w.WriteStringValue(item.ToString()); w.WriteEndArray();
                    w.WriteNumber("fieldCount", browser.PasswordFields.Count); w.WriteStartArray("fieldLengths");
                    foreach (var box in browser.PasswordFields) w.WriteNumberValue(box.Text?.Length ?? 0); w.WriteEndArray();
                    w.WriteBoolean("openEnabled", browser.OpenContainer.IsEnabled); w.WriteBoolean("backEnabled", browser.Back.IsEnabled); w.WriteEndObject();
                }));
            }
            string[] Rows(IndependentTextArchiveBrowser browser) => browser.Entries.ItemsSource?.Cast<object>().Select(row => row.ToString()!).ToArray() ?? [];
            void Operate(string name, Button button)
            {
                pending.Remove(name); Click(button); actions.Add(name);
                if (!pending.Remove(name, out var task)) throw new InvalidOperationException("元browser Taskが未観測です: " + name);
                pump(task); Jobs(); Assert("task-" + actions.Count, task.IsCompletedSuccessfully);
            }
            void Select(IndependentTextArchiveBrowser browser, string? path)
            {
                browser.Entries.SelectedItem = path is null ? null : (browser.Entries.ItemsSource?.Cast<object>() ?? [])
                    .Single(row => row.ToString()!.StartsWith(path + " （", StringComparison.Ordinal));
                actions.Add("row:" + (path ?? "<none>")); Jobs();
            }
            void RejectCompare()
            {
                submission = null; Click(dialog!.Compare); actions.Add("compare-reject");
                if (submission is null) throw new InvalidOperationException("元Compare Taskが未観測です。");
                pump(submission); Jobs(); Assert("compare-rejected-" + actions.Count, candidates == 0 && host.SessionPanes.Count() == tabs && dialog.IsVisible);
            }
            host.IndependentTextInputDialogShown = shown =>
            {
                dialog = shown; shown.SubmissionTaskObserved = task => submission = task;
                for (var side = 0; side < 3; side++)
                {
                    var browser = shown.Side(side); browser.Kind.SelectedIndex = 1;
                    if (side == 0)
                    {
                        browser.OperationTaskObserved = (name, task) => pending[name] = task;
                        browser.PickerPathProvider = (_, _, _) => Task.FromResult<string?>(Input(id == "password-retry" || id == "close-pending" || id == "root-clear" ? "two-passwords.zip" : id == "siblings" ? "siblings.zip" : id == "depth8" ? "depth-8.zip" : id == "depth9" ? "depth-9.zip" : "leaf.zip"));
                    }
                }
            };
            host.IndependentTextInputOperationObserved = task => opening = task;
            host.IndependentTextInputCandidateCreated = _ => candidates++;
            try
            {
                Click(parent.IndependentTextInputsButton); actions.Add("entry"); Jobs();
                if (dialog is null || opening is null) throw new InvalidOperationException("実入口dialog/openerが未観測です。");
                var browser = dialog.Side(0); browser.Kind.SelectedIndex = 2; Operate("pick", browser.Pick); Track(browser);
                Assert("initial-optional-field", browser.PasswordFields.Count == 1 && string.IsNullOrEmpty(browser.PasswordFields[0].Text));
                if (id == "password-retry")
                {
                    Operate("load", browser.Load); Step("root-no-secret", browser); RejectCompare();
                    browser.PasswordFields[0].Text = "wrong-fixture"; Operate("load", browser.Load); Step("root-wrong", browser); RejectCompare();
                    browser.PasswordFields[0].Text = Outer; Operate("load", browser.Load); Step("root-correct", browser);
                    Select(browser, "inner.zip"); Operate("open", browser.OpenContainer); Step("inner-no-secret", browser); RejectCompare();
                    Assert("outer-retained", browser.PasswordFields[0].Text == Outer && browser.PasswordFields.Count == 2 && string.IsNullOrEmpty(browser.PasswordFields[1].Text));
                    browser.PasswordFields[1].Text = "wrong-fixture"; Operate("load", browser.Load); Step("inner-wrong", browser); RejectCompare();
                    browser.PasswordFields[1].Text = Inner; Operate("load", browser.Load); Step("inner-correct", browser); Select(browser, "leaf.txt");
                    foreach (var size in new[] { (1000d, 680d), (850d, 550d) })
                    {
                        dialog.Width = size.Item1; dialog.Height = size.Item2; Jobs();
                        foreach (var box in browser.PasswordFields)
                        {
                            Assert("masked-" + layouts.Count, box.PasswordChar == '●'); box.BringIntoView(); Jobs();
                            Capture(dialog, id + "-" + layouts.Count, box, "field");
                        }
                        browser.Load.BringIntoView(); Jobs(); Capture(dialog, id + "-" + layouts.Count, browser.Load, "load");
                        browser.Entries.BringIntoView(); Jobs(); Capture(dialog, id + "-" + layouts.Count, browser.Entries, "list");
                    }
                }
                else if (id == "siblings")
                {
                    // 任意の外側欄も検証済み値として保持する。root ZIP自体は非暗号化。
                    browser.PasswordFields[0].Text = Outer;
                    Operate("load", browser.Load); Step("siblings-root", browser);
                    Select(browser, "left.zip"); Operate("open", browser.OpenContainer); Step("left-no-secret", browser); RejectCompare();
                    browser.PasswordFields[1].Text = Inner; Operate("load", browser.Load); Step("left-correct", browser);
                    var oldInner = browser.PasswordFields[1]; Operate("back", browser.Back); Step("siblings-back", browser);
                    Assert("removed-inner-cleared", string.IsNullOrEmpty(oldInner.Text));
                    Select(browser, "right.zip"); Operate("open", browser.OpenContainer); Step("right-no-secret", browser); RejectCompare();
                    Assert("common-outer-only", browser.PasswordFields[0].Text == Outer && browser.PasswordFields.Count == 2 && string.IsNullOrEmpty(browser.PasswordFields[1].Text));
                    browser.PasswordFields[1].Text = Inner; Operate("load", browser.Load); Step("right-correct", browser); Select(browser, "leaf.txt");
                }
                else if (id == "root-clear")
                {
                    browser.PasswordFields[0].Text = Outer; Operate("load", browser.Load); Select(browser, "inner.zip"); Operate("open", browser.OpenContainer);
                    browser.PasswordFields[1].Text = Inner; Operate("load", browser.Load); Select(browser, "leaf.txt"); Track(browser);
                    var old = browser.PasswordFields.ToArray(); browser.RootPath.Text = Input("leaf.zip"); Jobs(); Step("root-changed", browser);
                    Assert("all-old-fields-cleared", old.All(box => string.IsNullOrEmpty(box.Text)) && browser.PasswordFields.All(box => string.IsNullOrEmpty(box.Text))); RejectCompare();
                }
                else if (id is "close-pending" or "stale-root")
                {
                    if (id == "stale-root") { Operate("load", browser.Load); Step("confirmed-old", browser); }
                    else browser.PasswordFields[0].Text = Outer;
                    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    browser.ManifestReadGate = async (_, _, token) => { entered.TrySetResult(); await release.Task; gateCancelled = token.IsCancellationRequested; };
                    pending.Remove("load"); Click(browser.Load); actions.Add("load-gate");
                    if (!pending.Remove("load", out var read)) throw new InvalidOperationException("gate元Task未観測です。");
                    pump(entered.Task); Track(browser);
                    if (id == "stale-root") browser.RootPath.Text = Input("root-a.zip"); else dialog.Close();
                    actions.Add(id == "stale-root" ? "root-change-gate" : "close-gate"); release.TrySetResult(); pump(read); Jobs();
                    Assert("gate-cancelled-original-task", gateCancelled && read.IsCompletedSuccessfully);
                    browser.ManifestReadGate = null;
                    if (id == "stale-root") { Step("stale-completed", browser); RejectCompare(); }
                }
                else if (id is "depth8" or "depth9")
                {
                    Operate("load", browser.Load); Step("depth-root", browser);
                    for (var depth = 1; depth <= 8; depth++) { Select(browser, "inner.zip"); Operate("open", browser.OpenContainer); Step("depth-" + depth, browser); }
                    if (id == "depth8") Select(browser, "leaf.txt");
                    else
                    {
                        Select(browser, "inner.zip"); var rows = Rows(browser); var summary = browser.Summary;
                        Operate("open", browser.OpenContainer); Step("depth-nine-refused", browser);
                        Assert("ninth-route-refused", rows.SequenceEqual(Rows(browser)) && summary == browser.Summary && browser.PasswordFields.Count == 9);
                        // 選択済みcontainerを普通Textのleafに確定する操作とは別条件。
                        Select(browser, null); RejectCompare();
                    }
                }
                else
                {
                    Operate("load", browser.Load); Step("confirmed-old", browser); var oldRows = Rows(browser);
                    if (id == "directory") { Select(browser, "folder"); Step("directory-selected", browser); RejectCompare(); }
                    else if (id == "missing") { Select(browser, "leaf.txt"); Select(browser, null); Step("missing-selection", browser); RejectCompare(); }
                    else if (id == "password-limit")
                    {
                        Assert("field-max-4096", browser.PasswordFields[0].MaxLength == 4096);
                        browser.PasswordFields[0].Text = new string('x', 4096); Operate("load", browser.Load); Step("limit-4096", browser);
                        browser.PasswordFields[0].Text = new string('x', 4097); Operate("load", browser.Load); Step("limit-over", browser);
                        Assert("over-limit-rejected-or-truncated", browser.PasswordFields[0].Text!.Length <= 4096 || !browser.Summary.EndsWith("（検証済み）", StringComparison.Ordinal));
                        Select(browser, null); RejectCompare();
                    }
                    else
                    {
                        if (id == "late-crc") browser.RootPath.Text = Input("late-bad-sibling.zip");
                        else if (id == "dangerous") browser.RootPath.Text = Input("dangerous.zip");
                        else if (id == "root-budget") browser.ReadLimits = new ManagedArchiveLimits(MaximumInputBytes: new FileInfo(Input("leaf.zip")).Length - 1);
                        else if (id == "work-budget") browser.ReadLimits = new ManagedArchiveLimits(MaximumWorkBytes: new FileInfo(Input("leaf.zip")).Length - 1);
                        else if (id == "link")
                        {
                            var link = Input("input301-root-link.zip");
                            try { File.CreateSymbolicLink(link, Input("leaf.zip")); linkCreated = true; browser.RootPath.Text = link; }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { Assert("link-created", false); }
                        }
                        if (id != "link" || linkCreated)
                        {
                            Operate("load", browser.Load); Step("failed-new-request", browser);
                            Assert("old-list-retained", oldRows.SequenceEqual(Rows(browser)) && !browser.Summary.EndsWith("（検証済み）", StringComparison.Ordinal));
                            Select(browser, "leaf.txt"); RejectCompare();
                        }
                    }
                }

                if (spec.GetProperty("accepted").GetBoolean())
                {
                    submission = null; Click(dialog.Compare); actions.Add("compare-accept");
                    if (submission is null) throw new InvalidOperationException("元Compare Taskが未観測です。");
                    pump(submission); pump(opening); Jobs(); accepted = opening.Result;
                    Assert("accepted-one-candidate", accepted && candidates == 1 && host.SessionPanes.Count() == tabs + 1);
                }
                else if (dialog.IsVisible) { Click(dialog.Cancel); actions.Add("cancel"); pump(opening); Jobs(); }
                else { pump(opening); Jobs(); }
                if (!spec.GetProperty("accepted").GetBoolean()) Assert("no-candidate", !opening.Result && candidates == 0 && host.SessionPanes.Count() == tabs);
                Assert("opener-terminal", opening.IsCompletedSuccessfully);
                Assert("closed-current-fields-cleared", fields.All(box => string.IsNullOrEmpty(box.Text)) && Enumerable.Range(0, 3).All(side => dialog.Side(side).PasswordFields.Count == 0));
                Assert("parent-state-held", Equals(identity, parent.CaptureIndependentTextState()) && ReferenceEquals(store, host.ArchiveTexts) && generation == store.Generation);
                var actual = accepted ? host.ActivePane : null; var project = actual?.CaptureProject();
                if (project is not null)
                    File.WriteAllBytes(Input(id + "-project.json"), JsonSerializer.SerializeToUtf8Bytes(project, ProjectJsonContext.Default.ComparisonProject));
                var after = parent.CaptureInputSelectionTestEvidence(); var payload = actual?.CaptureInputSelectionTestEvidence();
                receipts.Add(Json(w =>
                {
                    w.WriteStartObject(); w.WriteString("id", id); w.WriteBoolean("accepted", accepted); w.WriteNumber("candidateCount", candidates);
                    w.WriteNumber("tabsBefore", tabs); w.WriteNumber("tabsAfter", host.SessionPanes.Count()); w.WriteBoolean("linkCreated", linkCreated); w.WriteBoolean("gateCancelled", gateCancelled);
                    w.WritePropertyName("before"); before(w); w.WritePropertyName("after"); after(w);
                    if (payload is not null) { w.WritePropertyName("payload"); payload(w); w.WriteString("projectFile", id + "-project.json"); }
                    w.WriteStartArray("actions"); foreach (var action in actions) w.WriteStringValue(action); w.WriteEndArray();
                    w.WriteStartArray("steps"); foreach (var step in steps) step.WriteTo(w); w.WriteEndArray();
                    w.WriteStartObject("checks"); foreach (var item in checks) w.WriteBoolean(item.Key, item.Value); w.WriteEndObject(); w.WriteEndObject();
                }));
                WriteObservations();
            }
            finally
            {
                release.TrySetResult();
                if (dialog?.IsVisible == true) { Click(dialog.Cancel); if (opening is not null) pump(opening); }
                host.IndependentTextInputDialogShown = null; host.IndependentTextInputOperationObserved = null; host.IndependentTextInputCandidateCreated = null;
                foreach (var pane in host.SessionPanes) pane.DiscardChanges(); host.Close(); Jobs();
            }
        }

        void WriteObservations()
        {
            using var file = File.Create(Input("observations.json")); using var w = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
            w.WriteStartObject(); w.WriteString("schema", "input301-observations-v1"); w.WriteString("status", receipts.Count == manifest.RootElement.GetProperty("cases").GetArrayLength() ? "completed" : "partial");
            w.WriteStartArray("cases"); foreach (var receipt in receipts) receipt.WriteTo(w); w.WriteEndArray();
            w.WriteStartArray("layouts"); foreach (var layout in layouts) layout.WriteTo(w); w.WriteEndArray(); w.WriteEndObject();
        }
        void Capture(Window owner, string name, Control control, string kind)
        {
            Jobs(); using (var frame = owner.CaptureRenderedFrame() ?? throw new InvalidOperationException("input301 PNG未取得です。"))
            using (var file = File.Create(Input(name + ".png"))) frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            var point = control.TranslatePoint(new Point(), owner) ?? throw new InvalidOperationException("visualが未接続です。");
            var rect = new Rect(point, control.Bounds.Size); var clip = new Rect(0, 0, owner.Bounds.Width, owner.Bounds.Height);
            foreach (var ancestor in control.GetVisualAncestors().OfType<Control>().Where(a => a.ClipToBounds))
                if (ancestor.TranslatePoint(new Point(), owner) is { } location) clip = clip.Intersect(new Rect(location, ancestor.Bounds.Size));
            var visible = control.IsVisible && control.GetVisualAncestors().OfType<Control>().All(a => a.IsVisible);
            Verify(name + " reach", visible && rect.Width > 0 && rect.Height > 0 && clip.Contains(rect) && (kind != "list" || rect.Height >= 100));
            layouts.Add(Json(w =>
            {
                w.WriteStartObject(); w.WriteString("png", name + ".png"); w.WriteString("control", kind); w.WriteNumber("width", owner.Bounds.Width); w.WriteNumber("height", owner.Bounds.Height);
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
    private static void CopyInput(string source, string target) { File.Copy(source, target); FixTime(target); }
}
