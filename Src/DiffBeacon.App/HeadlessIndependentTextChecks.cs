using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;

namespace DiffBeacon.App;

internal static class HeadlessIndependentTextChecks
{
    internal static void Run(MainWindow window, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "independent-text"); Directory.CreateDirectory(folder);
        var files = new[] { "left.txt", "middle.txt", "right.txt" }.Select(name => Path.Combine(folder, name)).ToArray();
        var originals = new[] { "head\r\nleft\r\ntail\r\n", "head\r\nmiddle\r\ntail\r\n", "head\r\nright\r\ntail\r\n" };
        File.WriteAllText(files[0], originals[0], new UTF8Encoding(false));
        File.WriteAllText(files[1], originals[1], new UnicodeEncoding(false, true));
        File.WriteAllText(files[2], originals[2], new UTF8Encoding(true));
        var observations = new List<(string Name, string[] Texts, bool[] Dirty, string[] Paths, string? Pair)>();
        var asyncProof = new List<(string Name, bool Passed, string Detail)>();
        void Check(string name, bool passed, string detail = "") => check("Independent Text " + name, passed, detail);
        ComparisonProject Project(bool middleUntitled = false, bool middleReadonly = false) => new()
        {
            Mode = "Text", LeftPath = files[0], BasePath = middleUntitled ? "" : files[1], RightPath = files[2], BaseReadOnly = middleReadonly,
            TextInputs = new() { Semantics = "Independent", Left = new() { Kind = "Physical" }, Middle = new() { Kind = middleUntitled ? "Untitled" : "Physical" }, Right = new() { Kind = "Physical" } }
        };
        ComparisonPane Open(ComparisonProject? project = null)
        {
            var pane = window.AddSession(); pane.ApplyProject(project ?? Project()); pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs(); return pane;
        }
        void Observe(string name, ComparisonPane pane)
        {
            var project = pane.CaptureProject();
            observations.Add((name, Enumerable.Range(0, 3).Select(side => pane.TextEditor(side).Text ?? "").ToArray(), Enumerable.Range(0, 3).Select(pane.TextDirty).ToArray(),
                new[] { project.LeftPath, project.BasePath, project.RightPath }, project.TextComparisonPair));
        }
        void Click(ComparisonPane pane, string content) => pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, content)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool Refused(Func<Task> action)
        {
            try { pump(action()); return false; }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException or OperationCanceledException or UnauthorizedAccessException) { return true; }
        }
        async Task Wait(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("独立Textの実ボタン操作が完了しません。"); await Task.Delay(10); }
        }
        void Proof(string name, bool passed, string detail = "")
        {
            asyncProof.Add((name, passed, detail));
            using (var stream = File.Create(Path.Combine(folder, "async-proof.json")))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartArray();
                foreach (var item in asyncProof) { writer.WriteStartObject(); writer.WriteString("name", item.Name); writer.WriteBoolean("passed", item.Passed); writer.WriteString("detail", item.Detail); writer.WriteEndObject(); }
                writer.WriteEndArray();
            }
            Check(name, passed, detail);
        }
        ComboBox PairControl(ComparisonPane pane) => pane.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Items.OfType<string>().Contains("左と中央"));
        void Layout(string name, ComparisonPane pane)
        {
            var toolbar = pane.GetVisualDescendants().OfType<ScrollViewer>().First(scroll => scroll.Content is StackPanel);
            var save = pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中央を保存"));
            using var stream = File.Create(Path.Combine(folder, "layout-" + name + ".json"));
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject(); writer.WriteNumber("windowWidth", window.Bounds.Width); writer.WriteNumber("windowHeight", window.Bounds.Height);
            writer.WriteNumber("tabCount", window.SessionPanes.Count()); writer.WriteNumber("toolbarMaxHeight", toolbar.MaxHeight);
            writer.WriteNumber("toolbarExtentHeight", toolbar.Extent.Height); writer.WriteNumber("toolbarViewportHeight", toolbar.Viewport.Height); writer.WriteNumber("toolbarOffsetY", toolbar.Offset.Y);
            void Bounds(string key, Control control)
            {
                writer.WriteStartObject(key); writer.WriteNumber("width", control.Bounds.Width); writer.WriteNumber("height", control.Bounds.Height); writer.WriteBoolean("visible", control.IsVisible);
                var position = control.TranslatePoint(new Point(0, 0), window);
                if (position is { } point) { writer.WriteNumber("windowX", point.X); writer.WriteNumber("windowY", point.Y); }
                writer.WriteEndObject();
            }
            Bounds("pane", pane); Bounds("toolbar", toolbar); Bounds("leftEditor", pane.LeftEditor); Bounds("middleEditor", pane.MiddleEditor); Bounds("rightEditor", pane.RightEditor); Bounds("middleSave", save);
            Bounds("leftPath", pane.LeftPath); Bounds("middlePath", pane.BasePath); Bounds("rightPath", pane.RightPath);
            writer.WriteEndObject();
        }
        var normalMiddle = Path.Combine(folder, "middle-save-input.txt"); File.WriteAllText(normalMiddle, originals[1], new UnicodeEncoding(false, true));
        var normal = Open(Project() with { BasePath = normalMiddle });
        Check("three physical documents load including editable middle", Enumerable.Range(0, 3).All(side => normal.TextEditor(side).Text == originals[side] && !normal.TextEditor(side).IsReadOnly),
            $"texts=[{normal.LeftEditor.Text}|{normal.MiddleEditor.Text}|{normal.RightEditor.Text}]; readonly={normal.LeftEditor.IsReadOnly}/{normal.MiddleEditor.IsReadOnly}/{normal.RightEditor.IsReadOnly}; "
            + $"kind={normal.CaptureProject().TextInputs?.Left?.Kind}/{normal.CaptureProject().TextInputs?.Middle?.Kind}/{normal.CaptureProject().TextInputs?.Right?.Kind}; status={normal.ComparisonStatus}; adoption={normal.IndependentTextAdoptionDiagnostic}");
        Check("middle document BOM and encoding differ without normalization", normal.CaptureProject().BasePath == normalMiddle);
        var sourceWorkspace = Path.Combine(folder, "async-source-workspace.json"); pump(window.SaveWorkspaceAsync(sourceWorkspace));
        var workspaceBytes = File.ReadAllBytes(sourceWorkspace); var fixedInputBytes = files.Select(File.ReadAllBytes).ToArray();
        for (var side = 0; side < 3; side++)
        {
            var invalid = Project(); invalid.TextInputs!.Semantics = "FixedAncestor"; invalid.TextInputs.Side(side).Kind = "Untitled";
            invalid = side switch { 0 => invalid with { LeftPath = "" }, 1 => invalid with { BasePath = "" }, _ => invalid with { RightPath = "" } };
            var rejectedOutput = Path.Combine(folder, $"fixed-untitled-{side}-sentinel.json"); File.WriteAllText(rejectedOutput, "KEEP-FIXED-UNTITLED"); var rejectedBytes = File.ReadAllBytes(rejectedOutput);
            var previous = normal.CaptureIndependentTextState();
            Proof($"FixedAncestor Untitled side {side} rejects before save and GUI adoption", Refused(() => WorkspaceStore.SaveAsync(rejectedOutput, invalid))
                && Refused(() => { normal.ApplyProject(invalid); return Task.CompletedTask; }) && File.ReadAllBytes(rejectedOutput).SequenceEqual(rejectedBytes)
                && Equals(previous, normal.CaptureIndependentTextState()));
        }
        var invalidWorkspace = Path.Combine(folder, "raw-fixed-untitled.json");
        using (var rawWorkspaceStream = File.Create(invalidWorkspace))
        using (var rawWorkspaceWriter = new Utf8JsonWriter(rawWorkspaceStream))
        {
            rawWorkspaceWriter.WriteStartObject(); rawWorkspaceWriter.WriteNumber("formatVersion", 6); rawWorkspaceWriter.WriteNumber("activeEntryIndex", 0); rawWorkspaceWriter.WriteStartArray("entries"); rawWorkspaceWriter.WriteStartObject();
            rawWorkspaceWriter.WriteString("mode", "Text"); rawWorkspaceWriter.WriteString("leftPath", files[0]); rawWorkspaceWriter.WriteString("basePath", ""); rawWorkspaceWriter.WriteString("rightPath", files[2]);
            rawWorkspaceWriter.WriteStartObject("textInputs"); rawWorkspaceWriter.WriteString("semantics", "FixedAncestor");
            foreach (var name in new[] { "left", "middle", "right" }) { rawWorkspaceWriter.WriteStartObject(name); rawWorkspaceWriter.WriteString("kind", name == "middle" ? "Untitled" : "Physical"); rawWorkspaceWriter.WriteEndObject(); }
            rawWorkspaceWriter.WriteEndObject(); rawWorkspaceWriter.WriteEndObject(); rawWorkspaceWriter.WriteEndArray(); rawWorkspaceWriter.WriteEndObject();
        }
        var paneBeforeInvalid = window.ActivePane; var sourceBeforeInvalid = window.WorkspaceSourcePath; var invalidJsonBytes = File.ReadAllBytes(invalidWorkspace);
        Proof("raw v6 FixedAncestor Untitled load preserves old tab source inputs and workspace", Refused(() => window.OpenWorkspaceAsync(invalidWorkspace, discardChanges: true))
            && ReferenceEquals(paneBeforeInvalid, window.ActivePane) && sourceBeforeInvalid == window.WorkspaceSourcePath && File.ReadAllBytes(sourceWorkspace).SequenceEqual(workspaceBytes)
            && File.ReadAllBytes(invalidWorkspace).SequenceEqual(invalidJsonBytes) && Enumerable.Range(0, 3).All(side => File.ReadAllBytes(files[side]).SequenceEqual(fixedInputBytes[side])));
        var regexPane = Open(); var regexDiff = regexPane.CurrentDiff; var regexTexts = Enumerable.Range(0, 3).Select(side => regexPane.TextEditor(side).Text).ToArray();
        var regexBox = regexPane.GetVisualDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "除外行の正規表現"); regexBox.Text = "[";
        PairControl(regexPane).SelectedIndex = 1;
        pump(Wait(() => window.OwnedWindows.Any(dialog => dialog.Title == "操作を完了できませんでした")));
        var errorDialog = window.OwnedWindows.Single(dialog => dialog.Title == "操作を完了できませんでした");
        Proof("actual pair selection invalid regex guarded with old diff and all texts", regexPane.IndependentTextUiTask.IsFaulted && ReferenceEquals(regexDiff, regexPane.CurrentDiff)
            && Enumerable.Range(0, 3).All(side => regexPane.TextEditor(side).Text == regexTexts[side]) && regexPane.ComparisonStatus?.Contains("保持", StringComparison.Ordinal) == true, regexPane.ComparisonStatus ?? "");
        errorDialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "閉じる")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        regexBox.Text = ""; PairControl(regexPane).SelectedIndex = 2; pump(regexPane.IndependentTextUiTask);
        Proof("corrected regex pair selection recovers without process failure", regexPane.CurrentDiff is { } corrected && corrected.LeftText == originals[0] && corrected.RightText == originals[2]
            && !ReferenceEquals(regexDiff, corrected) && !window.OwnedWindows.Any(dialog => dialog.Title == "操作を完了できませんでした"));
        var cancelCompare = Open(); var cancelledDiff = cancelCompare.CurrentDiff; var compareGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateEntered = 0; var onWorker = 0;
        cancelCompare.IndependentEditorCompareGate = token =>
        { Interlocked.Exchange(ref gateEntered, 1); Interlocked.Exchange(ref onWorker, Dispatcher.UIThread.CheckAccess() ? 0 : 1); return compareGate.Task.WaitAsync(token); };
        Click(cancelCompare, "再比較"); pump(Wait(() => Volatile.Read(ref gateEntered) == 1));
        Proof("recompare calculation gate runs off UI and remains responsive", !cancelCompare.IndependentTextUiTask.IsCompleted && Volatile.Read(ref onWorker) == 1);
        Click(cancelCompare, "中止"); pump(cancelCompare.IndependentTextUiTask); compareGate.TrySetResult(); cancelCompare.IndependentEditorCompareGate = null;
        Proof("actual recompare Cancel preserves previous diff and all texts", ReferenceEquals(cancelledDiff, cancelCompare.CurrentDiff)
            && Enumerable.Range(0, 3).All(side => cancelCompare.TextEditor(side).Text == originals[side]) && cancelCompare.ComparisonStatus?.Contains("中止", StringComparison.Ordinal) == true
            && cancelCompare.ComparisonStatus?.Contains("保持", StringComparison.Ordinal) == true, cancelCompare.ComparisonStatus ?? "");
        foreach (var changedSide in new[] { 1, 2 })
        {
            var lateCompare = Open(); var previousDiff = lateCompare.CurrentDiff; var updated = $"late-side-{changedSide}\r\n";
            lateCompare.IndependentEditorDiffReadyForAdoption = () => { lateCompare.IndependentEditorDiffReadyForAdoption = null; lateCompare.TextEditor(changedSide).Text = updated; };
            Click(lateCompare, "再比較"); pump(lateCompare.IndependentTextUiTask);
            Proof($"async recompare rejects later side {changedSide} edit including unselected third", ReferenceEquals(previousDiff, lateCompare.CurrentDiff) && lateCompare.TextEditor(changedSide).Text == updated
                && lateCompare.TextDirty(changedSide) && Enumerable.Range(0, 3).Where(side => side != changedSide).All(side => lateCompare.TextEditor(side).Text == originals[side])
                && lateCompare.ComparisonStatus?.Contains("保持", StringComparison.Ordinal) == true, lateCompare.ComparisonStatus ?? "");
        }
        var roleCompare = Open(); var roleDiff = roleCompare.CurrentDiff;
        roleCompare.IndependentEditorDiffReadyForAdoption = () => { roleCompare.IndependentEditorDiffReadyForAdoption = null; roleCompare.SelectIndependentText(false); };
        Click(roleCompare, "再比較"); pump(roleCompare.IndependentTextUiTask);
        Proof("async recompare role change preserves adopted independent diff", ReferenceEquals(roleDiff, roleCompare.CurrentDiff) && roleCompare.CaptureProject().TextInputs?.Semantics == "Independent"
            && Enumerable.Range(0, 3).All(side => roleCompare.TextEditor(side).Text == originals[side]) && roleCompare.ComparisonStatus?.Contains("保持", StringComparison.Ordinal) == true, roleCompare.ComparisonStatus ?? "");
        roleCompare.SelectIndependentText();
        var readonlyCompare = Open(); var readonlyDiff = readonlyCompare.CurrentDiff;
        readonlyCompare.IndependentEditorDiffReadyForAdoption = () => { readonlyCompare.IndependentEditorDiffReadyForAdoption = null; readonlyCompare.MiddleEditor.IsReadOnly = true; };
        Click(readonlyCompare, "再比較"); pump(readonlyCompare.IndependentTextUiTask);
        Proof("async recompare readonly change keeps previous diff and body", ReferenceEquals(readonlyDiff, readonlyCompare.CurrentDiff) && readonlyCompare.MiddleEditor.IsReadOnly
            && Enumerable.Range(0, 3).All(side => readonlyCompare.TextEditor(side).Text == originals[side]) && readonlyCompare.ComparisonStatus?.Contains("保持", StringComparison.Ordinal) == true, readonlyCompare.ComparisonStatus ?? "");
        var optionsCompare = Open(); var optionsDiff = optionsCompare.CurrentDiff;
        var optionsRegex = optionsCompare.GetVisualDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "除外行の正規表現");
        optionsCompare.IndependentEditorDiffReadyForAdoption = () => { optionsCompare.IndependentEditorDiffReadyForAdoption = null; optionsRegex.Text = "^head$"; };
        Click(optionsCompare, "再比較"); pump(optionsCompare.IndependentTextUiTask);
        Proof("async recompare options change rejects old candidate", ReferenceEquals(optionsDiff, optionsCompare.CurrentDiff)
            && Enumerable.Range(0, 3).All(side => optionsCompare.TextEditor(side).Text == originals[side]) && optionsCompare.ComparisonStatus?.Contains("保持", StringComparison.Ordinal) == true, optionsCompare.ComparisonStatus ?? "");
        var tabCompare = Open(); var tabDiff = tabCompare.CurrentDiff;
        tabCompare.IndependentEditorDiffReadyForAdoption = () => { tabCompare.IndependentEditorDiffReadyForAdoption = null; window.SelectSession(0); };
        Click(tabCompare, "再比較"); pump(tabCompare.IndependentTextUiTask);
        Proof("async recompare tab move keeps old diff and body", ReferenceEquals(tabDiff, tabCompare.CurrentDiff)
            && Enumerable.Range(0, 3).All(side => tabCompare.TextEditor(side).Text == originals[side]) && tabCompare.ComparisonStatus?.Contains("保持", StringComparison.Ordinal) == true, tabCompare.ComparisonStatus ?? "");
        window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), tabCompare)); Dispatcher.UIThread.RunJobs();
        var pairCompare = Open(); var pairGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var pairGateCalls = 0;
        pairCompare.IndependentEditorCompareGate = token => Interlocked.Increment(ref pairGateCalls) == 1 ? pairGate.Task.WaitAsync(token) : Task.CompletedTask;
        PairControl(pairCompare).SelectedIndex = 1; var earlierPairTask = pairCompare.IndependentTextUiTask; pump(Wait(() => Volatile.Read(ref pairGateCalls) == 1));
        PairControl(pairCompare).SelectedIndex = 2; pump(pairCompare.IndependentTextUiTask); pump(earlierPairTask); pairGate.TrySetResult(); pairCompare.IndependentEditorCompareGate = null;
        Proof("later pair completes while older gated completion cannot overwrite", pairCompare.CurrentDiff is { } finalPair && finalPair.LeftText == originals[0] && finalPair.RightText == originals[2]
            && pairCompare.CaptureProject().TextComparisonPair == "LeftRight" && pairCompare.ComparisonStatus?.Contains("左と右", StringComparison.Ordinal) == true, pairCompare.ComparisonStatus ?? "");
        var copyCancel = Open(); copyCancel.NavigateDifference(1); var copyState = copyCancel.CaptureIndependentTextState();
        var copyRevisions = copyCancel.CaptureIndependentTextRevisions(); var copyDirty = Enumerable.Range(0, 3).Select(copyCancel.TextDirty).ToArray(); var copyDiff = copyCancel.CurrentDiff;
        var copyComparisons = 0;
        copyCancel.IndependentEditorCompareGate = _ => { Interlocked.Increment(ref copyComparisons); return Task.CompletedTask; };
        copyCancel.IndependentEditorCopyReadyForAdoption = () => { copyCancel.IndependentEditorCopyReadyForAdoption = null; Click(copyCancel, "中止"); };
        Click(copyCancel, "選択差分 →"); pump(copyCancel.IndependentTextUiTask);
        Proof("async copy Cancel at adoption preserves destination revision dirty savepoint and old diff", Equals(copyState, copyCancel.CaptureIndependentTextState())
            && copyRevisions.SequenceEqual(copyCancel.CaptureIndependentTextRevisions()) && copyDirty.SequenceEqual(Enumerable.Range(0, 3).Select(copyCancel.TextDirty))
            && ReferenceEquals(copyDiff, copyCancel.CurrentDiff) && Enumerable.Range(0, 3).All(side => copyCancel.TextEditor(side).Text == originals[side])
            && Volatile.Read(ref copyComparisons) == 0 && copyCancel.ComparisonStatus?.Contains("中止", StringComparison.Ordinal) == true
            && copyCancel.ComparisonStatus?.Contains("保持", StringComparison.Ordinal) == true, copyCancel.ComparisonStatus ?? "");
        copyCancel.IndependentEditorCompareGate = null;
        var pairSides = new[] { (0, 1), (1, 2), (0, 2) };
        for (var pair = 0; pair < 3; pair++) foreach (var forward in new[] { false, true })
        {
            var pane = Open(); pane.SelectTextPair(pair); pump(pane.IndependentTextUiTask); pane.NavigateDifference(1);
            var sides = pairSides[pair]; var source = forward ? sides.Item1 : sides.Item2; var destination = forward ? sides.Item2 : sides.Item1;
            Click(pane, forward ? "選択差分 →" : "← 選択差分"); Dispatcher.UIThread.RunJobs();
            pump(pane.IndependentTextUiTask);
            Check($"actual selected pair copy {source} to {destination}", Enumerable.Range(0, 3).All(side => pane.TextEditor(side).Text == originals[side == destination ? source : side]));
            Check($"copy marks destination only dirty {source} to {destination}", Enumerable.Range(0, 3).All(side => pane.TextDirty(side) == (side == destination)));
            Observe($"copy-{source}-{destination}", pane);
            var copiedOutput = Path.Combine(folder, $"copy-{source}-{destination}.txt"); pump(pane.SaveTextToAsync(destination, copiedOutput));
            Encoding destinationEncoding = destination switch { 0 => new UTF8Encoding(false), 1 => new UnicodeEncoding(false, true), _ => new UTF8Encoding(true) };
            Check($"copy saved bytes keep destination encoding and BOM {source} to {destination}", File.ReadAllBytes(copiedOutput).SequenceEqual(destinationEncoding.GetPreamble().Concat(destinationEncoding.GetBytes(originals[source]))));
        }
        normal.SelectTextPair(0); normal.NavigateDifference(1); normal.MiddleEditor.Text = "late-middle\r\n"; var beforeStale = normal.MiddleEditor.Text;
        Click(normal, "選択差分 →");
        pump(normal.IndependentTextUiTask);
        Check("stale row rejected and selection cleared", normal.MiddleEditor.Text == beforeStale && normal.DiffList.SelectedIndex == -1);
        normal.MiddleEditor.Text = originals[1] + "saved-middle\r\n"; normal.LeftEditor.Text += "unsaved-left\r\n";
        Click(normal, "中央を保存"); pump(Wait(() => !normal.TextDirty(1)));
        Check("middle ordinary save updates only own savepoint", !normal.TextDirty(1) && normal.TextDirty(0) && !normal.TextDirty(2)
            && File.ReadAllBytes(normalMiddle).SequenceEqual(new UnicodeEncoding(false, true).GetPreamble().Concat(new UnicodeEncoding(false, true).GetBytes(normal.MiddleEditor.Text!))));
        Observe("middle-saved", normal);
        var lateOutput = Path.Combine(folder, "middle-late.txt"); var snapshot = normal.MiddleEditor.Text!;
        normal.TextSideSavePathPicker = side => Task.FromResult<string?>(lateOutput);
        normal.TextSaveBeforePublish = () => { normal.MiddleEditor.Text += "newer-edit\r\n"; return Task.CompletedTask; };
        pump(normal.SaveTextAsAsync(1)); normal.TextSaveBeforePublish = null;
        Check("late middle edit survives SaveAs and remains dirty", normal.MiddleEditor.Text == snapshot + "newer-edit\r\n" && normal.TextDirty(1)
            && File.ReadAllText(lateOutput) == snapshot && normal.BasePath.Text == lateOutput);
        Observe("middle-late-edit", normal);
        var readonlyPane = Open(Project(middleReadonly: true)); readonlyPane.TextSideSavePathPicker = _ => Task.FromResult<string?>(Path.Combine(folder, "readonly-middle-copy.txt"));
        Check("readonly middle ordinary save refused", Refused(() => readonlyPane.SaveWorkingTextAsync(1)));
        pump(readonlyPane.SaveTextAsAsync(1));
        Check("explicit readonly middle external SaveAs preserves readonly", readonlyPane.MiddleEditor.IsReadOnly && readonlyPane.CaptureProject().BaseReadOnly && !readonlyPane.TextDirty(1));
        readonlyPane.TextSideSavePathPicker = _ => Task.FromResult<string?>(files[0]); var originalLeft = File.ReadAllBytes(files[0]);
        Check("readonly external SaveAs cannot overwrite any input", Refused(() => readonlyPane.SaveTextAsAsync(1)) && File.ReadAllBytes(files[0]).SequenceEqual(originalLeft));
        var untitled = Open(Project(middleUntitled: true));
        Check("empty middle Untitled stays present and editable", ProjectInputs.HasBase(untitled.CaptureProject()) && untitled.MiddleEditor.Text == "" && !untitled.MiddleEditor.IsReadOnly);
        untitled.MiddleEditor.Text = "untitled-dirty\n";
        Check("dirty Untitled workspace refused", Refused(() => window.SaveWorkspaceAsync(Path.Combine(folder, "must-not-save.json"))));
        Check("dirty Untitled packaging guard refused", Refused(() => { untitled.EnsureArchiveDraftSaved(); return Task.CompletedTask; }));
        var untitledState = untitled.CaptureIndependentTextState(); untitled.SelectIndependentText(false);
        var roleChangedWorkspace = Path.Combine(folder, "role-change-must-not-save.json");
        Check("pending ancestor role cannot omit dirty Untitled from workspace", Refused(() => window.SaveWorkspaceAsync(roleChangedWorkspace)) && !File.Exists(roleChangedWorkspace)
            && Equals(untitledState, untitled.CaptureIndependentTextState()) && untitled.CaptureProject().TextInputs?.Semantics == "Independent"
            && untitled.CaptureProject().TextInputs?.Middle?.Kind == "Untitled");
        Check("pending ancestor role cannot bypass packaging guard", Refused(() => { untitled.EnsureArchiveDraftSaved(); return Task.CompletedTask; }));
        untitled.SelectIndependentText();
        var untitledOutput = Path.Combine(folder, "untitled-middle.txt"); untitled.TextSideSavePathPicker = _ => Task.FromResult<string?>(untitledOutput);
        pump(untitled.SaveTextAsAsync(1));
        Check("saved Untitled becomes only selected Physical side", untitled.CaptureProject().TextInputs!.Middle!.Kind == "Physical" && untitled.BasePath.Text == untitledOutput && !untitled.TextDirty(1)
            && untitled.LeftEditor.Text == originals[0] && untitled.RightEditor.Text == originals[2]);
        var failed = Open(); failed.RightPath.Text = Path.Combine(folder, "missing.txt"); var state = failed.CaptureIndependentTextState();
        Check("third physical load failure keeps full adopted state", Refused(() => failed.ComparePathsAsync()) && Equals(state, failed.CaptureIndependentTextState()));
        var cancelled = Open();
        cancelled.IndependentTextReadyForAdoption = () => cancelled.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("actual cancel refuses all three adoption", Refused(() => cancelled.ComparePathsAsync()) && cancelled.LeftEditor.Text == originals[0] && cancelled.MiddleEditor.Text == File.ReadAllText(files[1]) && cancelled.RightEditor.Text == originals[2]);
        cancelled.IndependentTextReadyForAdoption = null;
        var staleInput = Path.Combine(folder, "stale-middle-input.txt"); File.WriteAllText(staleInput, originals[1], new UnicodeEncoding(false, true));
        var stale = Open(Project() with { BasePath = staleInput }); stale.IndependentTextReadyForAdoption = () => stale.MiddleEditor.Text = "middle edited during read\n";
        pump(stale.ComparePathsAsync());
        Check("middle edit during preparation keeps other documents and saved points", stale.MiddleEditor.Text == "middle edited during read\n" && stale.TextDirty(1) && stale.LeftEditor.Text == originals[0] && stale.RightEditor.Text == originals[2]);
        stale.IndependentTextReadyForAdoption = null;
        var legacyLate = Open(); legacyLate.SelectIndependentText(false);
        var legacyLeft = legacyLate.LeftEditor.Text; var legacyRight = legacyLate.RightEditor.Text; var legacyDiff = legacyLate.CurrentDiff;
        legacyLate.LegacyTextReadyForAdoption = () => legacyLate.MiddleEditor.Text = "new middle during ancestor load\r\n";
        pump(legacyLate.ComparePathsAsync());
        Check("legacy candidate cannot discard later independent middle edit", legacyLate.MiddleEditor.Text == "new middle during ancestor load\r\n" && legacyLate.TextDirty(1)
            && legacyLate.LeftEditor.Text == legacyLeft && legacyLate.RightEditor.Text == legacyRight && ReferenceEquals(legacyDiff, legacyLate.CurrentDiff)
            && legacyLate.CaptureProject().TextInputs?.Semantics == "Independent");
        legacyLate.LegacyTextReadyForAdoption = null; legacyLate.SelectIndependentText();
        var roleAgain = Open(); roleAgain.SelectIndependentText(false); var roleAgainState = roleAgain.CaptureIndependentTextState();
        roleAgain.LegacyTextReadyForAdoption = () => roleAgain.SelectIndependentText(); pump(roleAgain.ComparePathsAsync());
        Check("legacy candidate rejects changed central role and preserves all documents", Equals(roleAgainState, roleAgain.CaptureIndependentTextState()) && roleAgain.CaptureProject().TextInputs?.Semantics == "Independent");
        roleAgain.LegacyTextReadyForAdoption = null;
        var ancestorSuccess = Open(); ancestorSuccess.SelectIndependentText(false); pump(ancestorSuccess.ComparePathsAsync());
        Check("clean confirmed role switch retains legacy ancestor behavior", ancestorSuccess.CaptureProject().TextInputs is null && ancestorSuccess.MiddleEditor.IsReadOnly);
        var afterPublish = Open(); afterPublish.MiddleEditor.Text += "saved before rejection\r\n"; var afterPublishState = afterPublish.CaptureIndependentTextState();
        var afterPublishOutput = Path.Combine(folder, "published-not-adopted.txt"); var publishedText = afterPublish.MiddleEditor.Text;
        afterPublish.TextSaveReadyForAdoption = () => afterPublish.SelectIndependentText(false);
        Check("published output and UI adoption refusal are distinct", Refused(() => afterPublish.SaveTextToAsync(1, afterPublishOutput)) && File.ReadAllText(afterPublishOutput) == publishedText
            && Equals(afterPublishState, afterPublish.CaptureIndependentTextState()) && afterPublish.TextDirty(1) && afterPublish.ComparisonStatus?.Contains("公開済み", StringComparison.Ordinal) == true);
        afterPublish.TextSaveReadyForAdoption = null; afterPublish.SelectIndependentText();
        var history = Open(); var historyViews = history.GetVisualDescendants().OfType<TabControl>().Single(); historyViews.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
        var historyOriginal = history.MiddleEditor.Text; history.MiddleEditor.Focus(); history.MiddleEditor.CaretIndex = history.MiddleEditor.Text?.Length ?? 0;
        window.KeyTextInput("typed-middle"); Dispatcher.UIThread.RunJobs(); var historyEdited = history.MiddleEditor.Text;
        var historyOutput = Path.Combine(folder, "middle-history.txt"); pump(history.SaveTextToAsync(1, historyOutput));
        var commandModifier = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        history.MiddleEditor.Focus(); window.KeyPress(Key.Z, commandModifier, PhysicalKey.Z, null); window.KeyRelease(Key.Z, commandModifier, PhysicalKey.Z, null); Dispatcher.UIThread.RunJobs();
        Check("real middle Undo after save preserves history and marks dirty", history.MiddleEditor.Text == historyOriginal && history.TextDirty(1) && historyEdited != historyOriginal);
        window.KeyPress(Key.Z, commandModifier | RawInputModifiers.Shift, PhysicalKey.Z, null); window.KeyRelease(Key.Z, commandModifier | RawInputModifiers.Shift, PhysicalKey.Z, null); Dispatcher.UIThread.RunJobs();
        if (!OperatingSystem.IsMacOS() && history.MiddleEditor.Text != historyEdited)
        { window.KeyPress(Key.Y, commandModifier, PhysicalKey.Y, null); window.KeyRelease(Key.Y, commandModifier, PhysicalKey.Y, null); Dispatcher.UIThread.RunJobs(); }
        Check("real middle Redo returns to saved point", history.MiddleEditor.Text == historyEdited && !history.TextDirty(1));
        Check("independent merge explicitly refused", Refused(() => { stale.StartMergeSession(); return Task.CompletedTask; }));
        var sessions = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Items.OfType<TabItem>().Any(tab => ReferenceEquals(tab.Content, stale)));
        sessions.SelectedItem = sessions.Items.OfType<TabItem>().Single(tab => ReferenceEquals(tab.Content, stale)); Dispatcher.UIThread.RunJobs();
        var views = stale.GetVisualDescendants().OfType<TabControl>().Single(); views.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
        screenshot("independent-text-three-panes.png");
        Layout("normal", stale);
        Check("three editor panes visible", Enumerable.Range(0, 3).All(side => stale.TextEditor(side).Bounds.Width > 50 && stale.TextEditor(side).Bounds.Height > 50));
        window.Width = 850; window.Height = 550; Dispatcher.UIThread.RunJobs(); screenshot("independent-text-minimum.png");
        Layout("minimum", stale);
        Check("minimum window keeps all three editors reachable", Enumerable.Range(0, 3).All(side => stale.TextEditor(side).Bounds.Width > 50 && stale.TextEditor(side).Bounds.Height > 50));
        var toolbar = stale.GetVisualDescendants().OfType<ScrollViewer>().First(scroll => scroll.Content is StackPanel);
        toolbar.Offset = new Avalonia.Vector(0, toolbar.Extent.Height); Dispatcher.UIThread.RunJobs();
        var centralSave = stale.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中央を保存"));
        toolbar.Offset = new Avalonia.Vector(0, 0); Dispatcher.UIThread.RunJobs();
        var savePoint = centralSave.TranslatePoint(new Avalonia.Point(0, 0), toolbar);
        Layout("minimum-toolbar", stale);
        Check("minimum central Save remains reachable through toolbar scroll", centralSave.IsVisible && centralSave.Bounds.Width > 0 && centralSave.Bounds.Height > 0
            && savePoint is { } point && point.Y >= 0 && point.Y + centralSave.Bounds.Height <= toolbar.Bounds.Height);
        Check("minimum input paths remain reachable after common settings scroll", new[] { stale.LeftPath, stale.BasePath, stale.RightPath }.All(path =>
        {
            var position = path.TranslatePoint(new Point(0, 0), toolbar);
            return path.Bounds.Height > 0 && position is { } point && point.Y >= 0 && point.Y + path.Bounds.Height <= toolbar.Bounds.Height;
        }));
        Click(stale, "中央を保存"); pump(Wait(() => !stale.TextDirty(1)));
        Check("minimum actual middle Save publishes exact current text", File.ReadAllText(staleInput) == stale.MiddleEditor.Text);
        window.Width = 1280; window.Height = 850;
        Observe("stale-middle-preserved", stale);
        foreach (var pane in window.SessionPanes) pane.DiscardChanges();
        var restoredPath = Path.Combine(folder, "pristine-untitled.json"); var persisted = Project(middleUntitled: true) with { TextComparisonPair = "MiddleRight" };
        pump(WorkspaceStore.SaveAsync(restoredPath, persisted));
        using (var json = JsonDocument.Parse(File.ReadAllBytes(restoredPath))) Check("typed serializer uses v6", json.RootElement.GetProperty("formatVersion").GetInt32() == 6);
        pump(window.OpenWorkspaceAsync(restoredPath, discardChanges: true)); var restored = window.ActivePane;
        Check("v6 workspace prepares empty middle and restores pair before tabs adoption", restored.MiddleEditor.Text == "" && ProjectInputs.HasBase(restored.CaptureProject()) && restored.CaptureProject().TextComparisonPair == "MiddleRight");
        Observe("restored-pristine", restored);
        var missingProjectPath = Path.Combine(folder, "typed-missing.json"); pump(WorkspaceStore.SaveAsync(missingProjectPath, persisted with { RightPath = Path.Combine(folder, "not-there.txt") }));
        var oldPane = window.ActivePane; var oldWorkspace = window.WorkspaceSourcePath;
        Check("typed workspace later load failure preserves old tabs and source", Refused(() => window.OpenWorkspaceAsync(missingProjectPath, discardChanges: true)) && ReferenceEquals(oldPane, window.ActivePane) && oldWorkspace == window.WorkspaceSourcePath);
        using var stream = File.Create(Path.Combine(folder, "observations.json"));
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartArray();
        foreach (var observation in observations)
        {
            writer.WriteStartObject(); writer.WriteString("name", observation.Name); writer.WriteString("pair", observation.Pair);
            writer.WriteStartArray("sides");
            for (var side = 0; side < 3; side++) { writer.WriteStartObject(); writer.WriteNumber("side", side); writer.WriteString("text", observation.Texts[side]); writer.WriteBoolean("dirty", observation.Dirty[side]); writer.WriteString("path", observation.Paths[side]); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
