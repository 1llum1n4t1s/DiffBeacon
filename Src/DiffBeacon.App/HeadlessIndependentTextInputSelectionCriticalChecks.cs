using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

// 実Buttonが開始した元Taskを観測する。共有storeの初期Importだけはfixture setup。
internal static class HeadlessIndependentTextInputSelectionCriticalChecks
{
    private static readonly DateTime FixedUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
    internal static void Run(MainWindow unused, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> unusedScreenshot)
    {
        var folder = Path.Combine(output, "independent-text-input-selection-critical");
        if (Directory.Exists(folder)) throw new IOException("新規critical run出力が必要です。");
        Directory.CreateDirectory(folder);
        var fixture = Path.GetFullPath("tests/Fixtures/IndependentTextInputSelectionCritical");
        var receipts = new List<Action<Utf8JsonWriter>>();
        string[] roles = ["left", "middle", "right"];
        void Jobs() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Verify(string id, bool value, string detail = "") => check("Independent Text Input Selection Critical " + id, value, detail);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture, "manifest.json")));
        var sides = manifest.RootElement.GetProperty("sides").EnumerateArray().Select(value => value.Clone()).ToArray();

        void Execute(string mode)
        {
            var run = Path.Combine(folder, mode); Directory.CreateDirectory(run);
            Directory.CreateDirectory(Path.Combine(run, "inputs"));
            foreach (var file in Directory.EnumerateFiles(Path.Combine(fixture, "inputs")))
            {
                var target = Path.Combine(run, "inputs", Path.GetFileName(file));
                File.Copy(file, target); File.SetAttributes(target, FileAttributes.Normal); File.SetLastWriteTimeUtc(target, FixedUtc);
            }
            foreach (var file in Directory.EnumerateFiles(fixture, "metadata-*.json")) File.Copy(file, Path.Combine(run, Path.GetFileName(file)));
            var host = new MainWindow(null, new ImageApplicationOptionsStore(Path.Combine(run, "options.json"))) { Width = 1000, Height = 680 };
            host.Show(); Jobs();
            ComparisonProject? project = null;
            var metadata = mode.StartsWith("metadata-", StringComparison.Ordinal) ? mode[9..] : "false";
            async Task Load() => project = await WorkspaceStore.LoadAsync(Path.Combine(run, "metadata-" + metadata + ".json"));
            pump(Load());
            if (mode == "default-archive")
                project = project! with { LeftArchiveInput = null, BaseArchiveInput = null, RightArchiveInput = null, LeftReadOnly = false, BaseReadOnly = false, RightReadOnly = false,
                    TextInputs = new() { Semantics = "Independent", Left = new() { Kind = "Untitled" }, Middle = new() { Kind = "Untitled" }, Right = new() { Kind = "Untitled" } } };
            if (mode is "physical-readonly" or "pending-hex")
            {
                project = project! with { Mode = mode == "pending-hex" ? "Binary" : "Text", TextComparisonPair = mode == "pending-hex" ? null : "LeftMiddle", TextInputs = mode == "pending-hex" ? null : new() { Semantics = "Independent", Left = new() { Kind = "Physical" }, Middle = new() { Kind = "Physical" }, Right = new() { Kind = "Physical" } },
                    LeftArchiveInput = null, BaseArchiveInput = null, RightArchiveInput = null,
                    LeftPath = Path.Combine(run, "inputs", "left.txt"), BasePath = Path.Combine(run, "inputs", "middle.txt"), RightPath = Path.Combine(run, "inputs", "right.txt"),
                    LeftReadOnly = mode == "physical-readonly", BaseReadOnly = mode == "physical-readonly", RightReadOnly = mode == "physical-readonly" };
            }
            var store = host.ArchiveTexts;
            if (mode == "working-cache")
            {
                for (var side = 0; side < 3; side++)
                {
                    var input = ProjectInputs.Archive(project!, side)!;
                    var bytes = File.ReadAllBytes(Path.Combine(run, "inputs", roles[side] + "-working.text"));
                    store.Import(input.Copy() with { WorkingDocuments = [new() { EntryChain = [], LeafEntry = input.LeafEntry!, Bytes = bytes, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), EncodingName = side == 1 ? "utf-16" : "utf-8", HasBom = side != 2 }] });
                }
            }
            var parent = host.ActivePane; parent.ApplyProject(project!); pump(parent.ComparePathsAsync()); Jobs();
            var initialBinary = mode == "pending-hex" ? parent.GetVisualDescendants().OfType<SpecializedViews.BinaryPanel>().Single() : null;
            var binaryBefore = initialBinary?.ProjectSides.Select(side => initialBinary.CaptureApplied(side).CopyBytes()).ToArray();
            if (mode is not ("physical-readonly" or "pending-hex") && !mode.StartsWith("metadata-", StringComparison.Ordinal))
                for (var side = 0; side < 3; side++) parent.TextEditor(side).Text += "\nparent-dirty";
            var before = parent.CaptureInputSelectionTestEvidence();
            var generation = store.Generation;
            var revisions = Enumerable.Range(0, 3).Select(side => ProjectInputs.Archive(project!, side) is { } input ? store.Revision(input) : 0).ToArray();
            var noopGeneration = generation;
            if (mode == "working-cache")
            {
                store.Import(Enumerable.Range(0, 3).Select(side => store.Capture(ProjectInputs.Archive(project!, side)!)));
                noopGeneration = store.Generation;
                Verify(mode + " same SHA Import no-op", noopGeneration == generation && Enumerable.Range(0, 3).All(side => store.Revision(ProjectInputs.Archive(project!, side)!) == revisions[side]));
            }
            var protectedOutput = Path.Combine(run, "existing.out"); File.WriteAllBytes(protectedOutput, "existing-critical-output\r\n"u8.ToArray());
            IndependentTextInputDialog? dialog = null; Task<bool>? opening = null; Task? submission = null;
            ComparisonPane? candidate = null;
            var browserTasks = new Dictionary<string, Task>(); var saveTasks = new Dictionary<string, Task>();
            var clicks = new List<string>(); var diagnostics = new List<string>();
            var fields = new List<TextBox>();
            var nonEmptyFields = 0;
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gated = mode is "parent-body" or "parent-role" or "parent-pair" or "parent-readonly" or "pending-hex" or "store-save" or "parent-close" or "window-close";
            var gateReturned = false; var tokenCancelled = false; var waitedForCallback = true;
            host.IndependentTextInputOperationObserved = task => opening = task;
            host.IndependentTextInputCandidateCreated = created =>
            {
                candidate = created;
                created.ArchiveSourceRetryShown = retry => retry.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "キャンセル")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            host.IndependentTextInputDialogShown = shown =>
            {
                dialog = shown; shown.SubmissionTaskObserved = task => submission = task;
                for (var side = 0; side < 3; side++)
                {
                    var captured = side;
                    shown.Side(side).OperationTaskObserved = (name, task) => browserTasks[captured + ":" + name] = task;
                    shown.Side(side).PickerPathProvider = (_, _, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult<string?>(Path.Combine(run, "inputs", roles[captured] + ".zip")); };
                }
            };
            if (gated) host.IndependentTextInputAdoptionGate = async (_, token) =>
            { reached.TrySetResult(); await release.Task; tokenCancelled = token.IsCancellationRequested; gateReturned = true; };
            Action<Utf8JsonWriter>? adopted = null; Action<Utf8JsonWriter>? afterMutation = null;
            bool? accepted = null; var tabsBefore = host.SessionPanes.Count(); var tabsAfter = 0;
            var restored = new bool[3]; var finalReadOnly = new bool[3];
            try
            {
                Click(parent.IndependentTextInputsButton); clicks.Add("entry");
                if (dialog is null || opening is null) throw new InvalidOperationException("入口元Task/dialogの観測がありません。");
                void BrowserClick(int side, string operation, Button button)
                {
                    browserTasks.Remove(side + ":" + operation); Click(button); clicks.Add(side + ":" + operation);
                    if (!browserTasks.Remove(side + ":" + operation, out var task)) throw new InvalidOperationException("browser元Task未観測");
                    pump(task); Jobs();
                }
                void Row(int side, string name)
                {
                    var browser = dialog.Side(side);
                    browser.Entries.SelectedItem = browser.Entries.ItemsSource!.Cast<object>().Single(item => item.ToString()!.StartsWith(name + " （", StringComparison.Ordinal));
                    Jobs(); clicks.Add(side + ":row:" + name);
                }
                for (var side = 0; side < 3; side++)
                {
                    dialog.SideTabs.SelectedIndex = side; Jobs(); var browser = dialog.Side(side);
                    restored[side] = mode == "physical-readonly" ? browser.ReadOnly.IsChecked == true : browser.AllowWorkingEdit.IsChecked == (metadata == "false" && mode is not ("default-archive" or "pending-hex"));
                    Verify(mode + " restored metadata " + side, restored[side]);
                    if (mode != "physical-readonly")
                    {
                        browser.Kind.SelectedIndex = 2;
                        if (mode is "default-archive" or "pending-hex") { browser.Kind.SelectedIndex = 1; browser.Kind.SelectedIndex = 2; BrowserClick(side, "pick", browser.Pick); }
                        // 未暗号化fixtureにも任意passwordを渡せる。public markerはJSONへ保存しない。
                        foreach (var field in browser.PasswordFields) field.Text = "synthetic-critical-marker";
                        BrowserClick(side, "load", browser.Load); Row(side, "docs/leaf.txt");
                        fields.AddRange(browser.PasswordFields);
                        if (mode is "default-archive" or "pending-hex") Verify(mode + " new archive default disallows editing " + side, browser.AllowWorkingEdit.IsChecked != true);
                    }
                }
                nonEmptyFields = fields.Count(field => !string.IsNullOrEmpty(field.Text));
                Verify(mode + " nonempty fields before submission", nonEmptyFields == (mode == "physical-readonly" ? 0 : 3));
                if (mode == "working-cache")
                {
                    Row(0, "docs/alternate.txt"); Row(0, "docs/leaf.txt");
                }
                if (mode == "sha-before-first-read")
                {
                    var original = Path.Combine(run, "inputs", "left.zip"); var beforeRaw = Path.Combine(run, "before.raw"); var afterRaw = Path.Combine(run, "after.raw");
                    File.Copy(original, beforeRaw);
                    var bytes = File.ReadAllBytes(Path.Combine(run, "inputs", "left-replacement.zip"));
                    if (bytes.Length != new FileInfo(original).Length) throw new IOException("同sizefixtureが必要です。");
                    File.WriteAllBytes(original, bytes); File.SetLastWriteTimeUtc(original, FixedUtc); File.Copy(original, afterRaw);
                    Verify(mode + " same size exact mtime replacement", new FileInfo(beforeRaw).Length == new FileInfo(afterRaw).Length && File.GetLastWriteTimeUtc(beforeRaw) == FixedUtc && File.GetLastWriteTimeUtc(afterRaw) == FixedUtc);
                }
                Jobs(); SavePng(dialog, Path.Combine(run, "dialog.png"));
                Click(dialog.Compare); clicks.Add("compare");
                if (submission is null) throw new InvalidOperationException("Compare元Task未観測");
                if (gated)
                {
                    pump(reached.Task);
                    switch (mode)
                    {
                        case "parent-body": parent.MiddleEditor.Text += "\nlater-body"; break;
                        case "parent-role": parent.SelectIndependentText(false); break;
                        case "parent-pair": parent.SelectTextPair(1); pump(parent.IndependentTextUiTask); break;
                        case "parent-readonly": parent.MiddleEditor.IsReadOnly = true; break;
                        case "pending-hex": initialBinary!.RightHex.Text = "GG"; break;
                        case "store-save":
                            parent.TextSaveTaskObserved = (name, task) => saveTasks[name] = task;
                            Click(parent.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中央の作業版を保存"))); clicks.Add("save:中央の作業版を保存");
                            if (!saveTasks.Remove("中央の作業版を保存", out var savedTask)) throw new InvalidOperationException("実保存元Task未観測: 登録proposalが必要です。");
                            pump(savedTask); Jobs(); break;
                    }
                    afterMutation = parent.CaptureInputSelectionTestEvidence();
                    if (mode == "parent-close")
                    {
                        var tab = host.GetVisualDescendants().OfType<TabControl>().SelectMany(control => control.Items.OfType<TabItem>()).Single(item => ReferenceEquals(item.Content, parent));
                        Click(((StackPanel)tab.Header!).Children.OfType<Button>().Single()); clicks.Add("parent-close");
                        Jobs();
                        var confirmation = host.OwnedWindows.Single(window => window.Title == "未保存の変更");
                        Click(confirmation.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "続行"))); clicks.Add("parent-close-confirm");
                    }
                    if (mode == "window-close")
                    {
                        host.Close(); clicks.Add("window-close"); Jobs();
                        var confirmation = host.OwnedWindows.Single(window => window.Title == "未保存の変更");
                        Click(confirmation.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "続行"))); clicks.Add("window-close-confirm");
                    }
                    Jobs(); waitedForCallback = !opening.IsCompleted && !submission.IsCompleted;
                    Verify(mode + " actual callback pending before release", waitedForCallback);
                    release.TrySetResult();
                }
                pump(submission); Jobs();
                var success = mode is "working-cache" or "default-archive" or "physical-readonly" || mode.StartsWith("metadata-", StringComparison.Ordinal);
                if (!success && dialog.IsVisible) Click(dialog.Cancel);
                pump(opening); Jobs(); accepted = opening.Result; tabsAfter = host.SessionPanes.Count();
                Verify(mode + " acceptance", accepted == success);
                Verify(mode + " actual tasks complete", opening.IsCompletedSuccessfully && submission.IsCompletedSuccessfully && (!gated || gateReturned));
                Verify(mode + " pending browser fields cleared", fields.All(field => string.IsNullOrEmpty(field.Text)));
                if (success)
                {
                    var actual = host.ActivePane;
                    adopted = actual.CaptureInputSelectionTestEvidence();
                    finalReadOnly = Enumerable.Range(0, 3).Select(side => actual.TextEditor(side).IsReadOnly).ToArray();
                    for (var side = 0; side < 3; side++)
                    {
                        Verify(mode + " adopted literal " + side, actual.TextEditor(side).Text == sides[side].GetProperty(mode == "working-cache" ? "workingText" : "text").GetString());
                        var blocked = mode == "physical-readonly" || mode == "default-archive" || metadata != "false";
                        Verify(mode + " adopted edit permission " + side, finalReadOnly[side] == blocked);
                    }
                    Jobs(); SavePng(host, Path.Combine(run, "adopted.png"));
                    if (mode is "metadata-true" or "metadata-null" or "metadata-omitted" or "default-archive" or "physical-readonly")
                        ExerciseReadOnly(actual, host, mode, clicks, diagnostics, saveTasks, pump, Verify, Jobs, Click);
                }
                Verify(mode + " protected output", File.ReadAllBytes(protectedOutput).AsSpan().SequenceEqual("existing-critical-output\r\n"u8));
                if (mode == "working-cache") Verify(mode + " shared store remains unchanged", store.Generation == generation && ReferenceEquals(store, host.ArchiveTexts));
                if (mode == "store-save") Verify(mode + " actual save advanced shared revision", store.Generation > generation && store.Revision(ProjectInputs.Archive(project!, 1)!) > revisions[1]);
                if (mode == "store-save")
                {
                    var input = ProjectInputs.Archive(project!, 1)!;
                    var snapshot = store.Capture(input).WorkingDocuments!.Single(value => value.LeafEntry == input.LeafEntry);
                    File.WriteAllBytes(Path.Combine(run, "saved-middle.text"), snapshot.Bytes!);
                }
                if (mode is not ("parent-close" or "window-close"))
                    Verify(mode + " tab count", tabsAfter == tabsBefore + (success ? 1 : 0));
                var after = parent.CaptureInputSelectionTestEvidence(); var finalGeneration = store.Generation;
                var binaryRetained = initialBinary is not null && !initialBinary.IsDisposed && ReferenceEquals(initialBinary, parent.GetVisualDescendants().OfType<SpecializedViews.BinaryPanel>().SingleOrDefault());
                var pendingHex = initialBinary?.RightHex.Text;
                var binaryAfter = binaryRetained ? initialBinary!.ProjectSides.Select(side => initialBinary.CaptureApplied(side).CopyBytes()).ToArray() : null;
                if (mode == "pending-hex") Verify(mode + " same Binary panel applied bytes and pending Hex retained", binaryRetained && pendingHex == "GG" && binaryBefore!.Zip(binaryAfter!).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)));
                receipts.Add(writer =>
                {
                    writer.WriteStartObject(); writer.WriteString("id", mode); writer.WriteBoolean("accepted", accepted == true); writer.WriteNumber("tabsBefore", tabsBefore); writer.WriteNumber("tabsAfter", tabsAfter);
                    writer.WriteNumber("storeBefore", generation); writer.WriteNumber("storeAfter", finalGeneration); writer.WriteNumber("noopGeneration", noopGeneration);
                    writer.WriteBoolean("sameStore", ReferenceEquals(store, host.ArchiveTexts)); writer.WriteBoolean("callbackWaited", waitedForCallback); writer.WriteBoolean("gateReturned", gateReturned); writer.WriteBoolean("tokenCancelled", tokenCancelled);
                    writer.WriteBoolean("tasksCompleted", opening.IsCompletedSuccessfully && submission.IsCompletedSuccessfully); writer.WriteBoolean("fieldsCleared", fields.All(field => string.IsNullOrEmpty(field.Text)));
                    writer.WriteNumber("nonEmptyFieldsBeforeSubmission", nonEmptyFields);
                    if (binaryBefore is not null && binaryAfter is not null)
                    {
                        writer.WriteBoolean("sameBinaryPanel", binaryRetained); writer.WriteString("pendingHex", pendingHex);
                        writer.WriteStartArray("binaryBefore"); foreach (var bytes in binaryBefore) writer.WriteBase64StringValue(bytes); writer.WriteEndArray();
                        writer.WriteStartArray("binaryAfter"); foreach (var bytes in binaryAfter) writer.WriteBase64StringValue(bytes); writer.WriteEndArray();
                    }
                    writer.WritePropertyName("before"); before(writer); writer.WritePropertyName("after"); after(writer);
                    if (afterMutation is not null) { writer.WritePropertyName("afterMutation"); afterMutation(writer); }
                    if (adopted is not null) { writer.WritePropertyName("adopted"); adopted(writer); }
                    writer.WriteStartArray("restored"); foreach (var value in restored) writer.WriteBooleanValue(value); writer.WriteEndArray();
                    writer.WriteStartArray("clicks"); foreach (var value in clicks) writer.WriteStringValue(value); writer.WriteEndArray();
                    writer.WriteStartArray("diagnostics"); foreach (var value in diagnostics) writer.WriteStringValue(value); writer.WriteEndArray();
                    writer.WriteString("rawTiming", mode == "sha-before-first-read" ? "browser-confirmed-before-first-candidate-read" : "none"); writer.WriteEndObject();
                });
            }
            finally
            {
                release.TrySetResult();
                if (dialog?.IsVisible == true) dialog.Close();
                if (submission is not null && !submission.IsCompleted) pump(submission);
                if (opening is not null && !opening.IsCompleted) pump(opening);
                foreach (var pane in host.SessionPanes) pane.DiscardChanges();
                host.Close(); Jobs();
                // raw captureのmtimeは変更しない。再開に必要なbefore/afterは保持する。
                if (mode == "sha-before-first-read")
                {
                    var path = Path.Combine(run, "inputs", "left.zip");
                    File.Copy(Path.Combine(fixture, "inputs", "left.zip"), path, true); File.SetLastWriteTimeUtc(path, FixedUtc);
                }
            }
        }

        foreach (var mode in new[] { "working-cache", "metadata-false", "metadata-true", "metadata-null", "metadata-omitted", "default-archive", "physical-readonly", "parent-body", "parent-role", "parent-pair", "parent-readonly", "pending-hex", "store-save", "parent-close", "window-close", "sha-before-first-read" }) Execute(mode);
        using var file = File.Create(Path.Combine(folder, "observations.json")); using var json = new Utf8JsonWriter(file, new() { Indented = true });
        json.WriteStartObject(); json.WriteString("schema", "input-selection-critical-observations-v1"); json.WriteString("status", "critical-subset-only");
        json.WriteStartArray("cases"); foreach (var receipt in receipts) receipt(json); json.WriteEndArray(); json.WriteEndObject();
    }

    private static void SavePng(Window window, string path)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("実描画PNG未取得");
        using var file = File.Create(path); frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void ExerciseReadOnly(ComparisonPane actual, MainWindow host, string mode, List<string> clicks, List<string> diagnostics,
        Dictionary<string, Task> tasks, Action<Task> pump, Action<string, bool, string> verify, Action jobs, Action<Button> click)
    {
        actual.TextSaveTaskObserved = (name, task) => tasks[name] = task;
        var pickerCalls = 0;
        actual.TextSideSavePathPicker = _ => { pickerCalls++; throw new InvalidOperationException("readonly archive must refuse before picker"); };
        bool Refused(Task task) { try { pump(task); return false; } catch (InvalidOperationException error) when (error.Message.EndsWith("は読取り専用です。", StringComparison.Ordinal)) { diagnostics.Add(error.Message); return true; } }
        void CloseError()
        {
            jobs(); var error = host.OwnedWindows.Single(window => window.Title == "操作を完了できませんでした");
            click(error.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "閉じる"))); jobs();
        }
        string[] normal = ["左を保存", "中央の作業版を保存", "右を保存"];
        if (mode == "physical-readonly") normal[1] = "中央を保存";
        string[] external = ["左を外部保存", "中央を外部保存", "右を外部保存"];
        for (var side = 0; side < 3; side++)
        {
            var previousUiTask = actual.IndependentTextUiTask;
            actual.SelectTextPair(side == 2 ? 1 : 0);
            var comparisonTask = actual.IndependentTextUiTask;
            // 同じpairでは比較が始まらず、直前の拒否済みcopy Taskが残る。
            if (!ReferenceEquals(previousUiTask, comparisonTask)) pump(comparisonTask);
            actual.NavigateDifference(1);
            var before = actual.CaptureIndependentTextState();
            click(actual.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, side == 0 ? "← 選択差分" : "選択差分 →"))); clicks.Add("readonly-copy:" + side);
            verify(mode + " readonly real copy " + side, Refused(actual.IndependentTextUiTask), ""); CloseError();
            foreach (var label in mode == "physical-readonly" ? new[] { normal[side] } : new[] { normal[side], external[side] })
            {
                tasks.Remove(label); click(actual.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label))); clicks.Add("save:" + label);
                if (!tasks.Remove(label, out var task)) throw new InvalidOperationException("実保存元Task未観測: 登録proposalが必要です。");
                verify(mode + " readonly real save " + label, Refused(task), ""); CloseError();
            }
            verify(mode + " readonly body/savepoints retained " + side, Equals(before, actual.CaptureIndependentTextState()), "");
        }
        verify(mode + " refused before picker", pickerCalls == 0, "");
    }
}

public sealed partial class ComparisonPane
{
    // 親が適用する実保存handler proposalからのみ呼ぶ。Taskの成功・失敗は変更しない。
    internal Action<string, Task>? TextSaveTaskObserved { get; set; }
    internal Task ObserveCriticalTextSaveTask(string operation, Task task) { TextSaveTaskObserved?.Invoke(operation, task); return task; }
}
