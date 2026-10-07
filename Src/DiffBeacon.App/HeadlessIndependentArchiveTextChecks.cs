using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessIndependentArchiveTextChecks
{
    internal static void Run(MainWindow window, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        var fixture = Path.GetFullPath("tests/Fixtures/IndependentArchiveText");
        var folder = Path.Combine(output, "independent-archive-text"); Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(folder, Path.GetRelativePath(fixture, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        string[] roles = ["left", "middle", "right"];
        string[] originals = ["Shared café\r\nLEFT original €\nthird left\rtail-left", "Shared café\r\nMIDDLE original £\nthird middle\rtail-middle", "Shared café\r\nRIGHT original “quote”\nthird right\rtail-right"];
        string[] edits = ["Shared café\r\nLEFT edited €\ninsert-left\rthird left\rtail-left", "Shared café\r\nMIDDLE edited £\ninsert-middle\rthird middle\rtail-middle", "Shared café\r\nRIGHT edited “quote”\ninsert-right\rthird right\rtail-right"];
        var roots = roles.Select(role => Path.Combine(folder, "inputs", role + ".zip")).ToArray();
        var rootBytes = roots.Select(File.ReadAllBytes).ToArray();
        var observations = new List<(string Name, string?[] Texts, bool[] Dirty)>();
        void Verify(string name, bool passed, string detail = "") => check("Independent Archive Text " + name, passed, detail);
        var opened = 0;
        ComparisonProject Load(string? inputPath = null)
        {
            ComparisonProject? project = null;
            async Task Read() { project = await WorkspaceStore.LoadAsync(inputPath ?? Path.Combine(folder, "workspace-original.json")); }
            pump(Read());
            // 独立caseの保存点が共有storeを通じて次caseの初期本文へ混入しないようroot identityを分ける。
            if (opened++ > 0)
                for (var side = 0; side < 3; side++)
                {
                    var root = Path.Combine(folder, $"case-{opened}-{roles[side]}.zip");
                    File.WriteAllBytes(root, rootBytes[side]); ProjectInputs.Archive(project!, side)!.RootPath = root;
                }
            return project!;
        }
        ComparisonPane Open(ComparisonProject? project = null)
        {
            var pane = window.AddSession(); pane.ApplyProject(project ?? Load()); pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs(); return pane;
        }
        void Observe(string name, ComparisonPane pane) => observations.Add((name, Enumerable.Range(0, 3).Select(side => pane.TextEditor(side).Text).ToArray(), Enumerable.Range(0, 3).Select(pane.TextDirty).ToArray()));
        void Click(ComparisonPane pane, string label) => pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Button MiddleSave(ComparisonPane pane)
        {
            var label = pane.CaptureProject().BaseArchiveInput is not null ? "中央の作業版を保存" : "中央を保存";
            return pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label));
        }
        bool Refused(Func<Task> action) { try { pump(action()); return false; } catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException) { return true; } }
        bool RefusedReadonlyCopy(Func<Task> action, int side)
        {
            try { pump(action()); return false; }
            catch (InvalidOperationException error) when (error.Message.StartsWith(new[] { "左", "中央", "右" }[side], StringComparison.Ordinal)
                && error.Message.EndsWith("は読取り専用です。", StringComparison.Ordinal)) { return true; }
        }
        var main = Open();
        Verify("three present leaves editable without treating middle as ancestor", Enumerable.Range(0, 3).All(side => main.TextEditor(side).Text == originals[side] && !main.TextEditor(side).IsReadOnly), main.ComparisonStatus ?? "");
        Verify("Independent merge refused", Refused(() => { main.StartMergeSession(); return Task.CompletedTask; }));
        for (var side = 0; side < 3; side++) main.TextEditor(side).Text = edits[side];
        pump(main.SaveReportAsync(Path.Combine(folder, "pending.html")));
        Verify("unsaved middle protects workspace and package", Refused(() => { main.EnsureArchiveDraftSaved(); return Task.CompletedTask; }));
        Verify("Archive middle actual working save button visible", MiddleSave(main).IsVisible);
        MiddleSave(main).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pump(Wait(() => !main.TextDirty(1)));
        Verify("middle button commits only middle savepoint", !main.TextDirty(1) && main.TextDirty(0) && main.TextDirty(2));
        pump(main.SaveWorkingTextAsync(0)); pump(main.SaveWorkingTextAsync(2));
        Verify("all three working saves retain Archive routes and encoding", Enumerable.Range(0, 3).All(side => !main.TextDirty(side) && ProjectInputs.Archive(main.CaptureProject(), side)?.WorkingDocuments is { Length: 1 }));
        Observe("saved", main);
        var saved = main.CaptureProject();
        pump(WorkspaceStore.SaveWorkspaceAsync(Path.Combine(folder, "gui-saved.json"), new() { Entries = [saved] }));
        pump(main.SaveReportAsync(Path.Combine(folder, "gui-saved.html")));
        Verify("Independent packaging patch refused", Refused(() => ComparisonPackage.CreateAsync(new() { Entries = [saved] }, Path.Combine(folder, "forbidden-patch.zip"), new(true, false, true, true))));
        pump(ComparisonPackage.CreateAsync(new() { Entries = [saved] }, Path.Combine(folder, "gui-package.zip"), new(true, true, false, true)));
        for (var pair = 0; pair < 3; pair++) foreach (var forward in new[] { true, false })
        {
            var pane = Open();
            for (var side = 0; side < 3; side++) pane.TextEditor(side).Text = edits[side];
            pane.SelectTextPair(pair); pump(pane.IndependentTextUiTask);
            var (first, second) = pair switch { 0 => (0, 1), 1 => (1, 2), _ => (0, 2) };
            var source = forward ? first : second; var destination = forward ? second : first;
            for (var attempt = 0; attempt < 20 && pane.TextEditor(destination).Text != edits[source]; attempt++)
            {
                pane.NavigateDifference(1); Click(pane, forward ? "選択差分 →" : "← 選択差分"); pump(pane.IndependentTextUiTask);
            }
            Verify($"all hunks copy {source} to {destination} preserves third side", Enumerable.Range(0, 3).All(side => pane.TextEditor(side).Text == edits[side == destination ? source : side]));
            var target = Path.Combine(folder, $"copy-{roles[source]}-to-{roles[destination]}.text");
            pump(pane.SaveTextToAsync(destination, target));
            Verify($"copy {source} to {destination} retains destination bytes", File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "expected", $"copy-{roles[source]}-to-{roles[destination]}.text"))));
            Observe($"copy-{source}-{destination}", pane);
        }
        var history = Open();
        // 本文tabを表示して実入力から履歴を作る。Text property代入は利用者入力のUndo履歴とは別の操作。
        window.SelectSession(window.SessionPanes.ToList().IndexOf(history));
        history.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
        var previous = history.MiddleEditor.Text!;
        Verify("middle history editor accepts actual focus", history.MiddleEditor.Focus());
        history.MiddleEditor.CaretIndex = previous.Length;
        window.KeyTextInput("typed-middle"); Dispatcher.UIThread.RunJobs();
        var historyEdited = history.MiddleEditor.Text;
        Verify("actual middle text input creates edited body", historyEdited == previous + "typed-middle" && history.TextDirty(1));
        pump(history.SaveWorkingTextAsync(1));
        Verify("middle working save records actual input savepoint", history.MiddleEditor.Text == historyEdited && !history.TextDirty(1));
        history.MiddleEditor.Focus(); window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null); window.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null); Dispatcher.UIThread.RunJobs();
        Verify("real Control Undo after middle working save retains history", history.MiddleEditor.Text == previous && history.TextDirty(1), $"previous={previous}; current={history.MiddleEditor.Text}; dirty={history.TextDirty(1)}; focused={history.MiddleEditor.IsFocused}");
        window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, null); window.KeyRelease(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, null); Dispatcher.UIThread.RunJobs();
        if (history.MiddleEditor.Text != historyEdited) { window.KeyPress(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Z, null); window.KeyRelease(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Z, null); Dispatcher.UIThread.RunJobs(); }
        Verify("real Control Redo returns middle saved point", history.MiddleEditor.Text == historyEdited && !history.TextDirty(1));
        for (var side = 0; side < 3; side++) foreach (var readonlyValue in new[] { "true", "none", "omitted" })
        {
            // readonly/の原bytesを同じ入力root基準位置へコピーし、nullと省略を実DTO読込みで区別する。
            var readonlyPath = Path.Combine(folder, $"readonly-input-{roles[side]}-{readonlyValue}.json");
            File.Copy(Path.Combine(folder, "readonly", $"{roles[side]}-{readonlyValue}.json"), readonlyPath);
            var project = Load(readonlyPath);
            var pane = Open(project); var original = pane.TextEditor(side).Text;
            Verify($"readonly {readonlyValue} side {side} retains actual Archive middle save label", MiddleSave(pane).IsVisible && Equals(MiddleSave(pane).Content, "中央の作業版を保存"));
            Verify($"inherited readonly {readonlyValue} side {side} blocks editing and working save", pane.TextEditor(side).IsReadOnly && Refused(() => pane.SaveWorkingTextAsync(side)) && pane.TextEditor(side).Text == original);
            var pair = side == 2 ? 1 : 0; var sourceSide = side == 0 ? 1 : side == 1 ? 0 : 1;
            pane.TextEditor(sourceSide).Text += "\ncopy-to-readonly"; pane.SelectTextPair(pair); pump(pane.IndependentTextUiTask); pane.NavigateDifference(1);
            var readonlyBeforeState = pane.CaptureIndependentTextState(); var readonlyBeforeRevisions = pane.CaptureIndependentTextRevisions();
            Click(pane, side == 0 ? "← 選択差分" : "選択差分 →");
            var refusedReadonlyCopy = RefusedReadonlyCopy(() => pane.IndependentTextUiTask, side);
            pump(Wait(() => window.OwnedWindows.Any(dialog => dialog.Title == "操作を完了できませんでした")));
            var readonlyDialog = window.OwnedWindows.Single(dialog => dialog.Title == "操作を完了できませんでした");
            var readonlyDiagnostic = readonlyDialog.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text)
                .Single(text => text?.EndsWith("は読取り専用です。", StringComparison.Ordinal) == true);
            Verify($"inherited readonly {readonlyValue} side {side} actual copy refuses only expected readonly exception and shows error dialog", refusedReadonlyCopy
                && readonlyDiagnostic!.StartsWith(new[] { "左", "中央", "右" }[side], StringComparison.Ordinal), readonlyDiagnostic!);
            readonlyDialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "閉じる")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            pump(Wait(() => !window.OwnedWindows.Any(dialog => dialog.Title == "操作を完了できませんでした")));
            Verify($"inherited readonly {readonlyValue} side {side} copy preserves all bodies revisions documents and savepoints", pane.TextEditor(side).Text == original && !pane.TextDirty(side)
                && Equals(readonlyBeforeState, pane.CaptureIndependentTextState()) && readonlyBeforeRevisions.SequenceEqual(pane.CaptureIndependentTextRevisions()));
            Verify($"inherited readonly {readonlyValue} side {side} actual error dismissal leaves no owned dialog", !window.OwnedWindows.Any(dialog => dialog.Title == "操作を完了できませんでした")
                && ReferenceEquals(window.ActivePane, pane));
        }
        var cancel = Open(); cancel.MiddleEditor.Text = edits[1];
        using (var token = new CancellationTokenSource()) { token.Cancel(); Verify("cancellation retains middle edit and route", Refused(() => cancel.SaveWorkingTextAsync(1, token.Token)) && cancel.TextDirty(1) && cancel.CaptureProject().BaseArchiveInput is not null); }
        var sourcePane = Open(); var sourceProject = sourcePane.CaptureProject();
        var cleanSibling = Open(sourceProject); var dirtySibling = Open(sourceProject);
        var dirtyText = originals[1] + "\ndirty-sibling"; dirtySibling.MiddleEditor.Text = dirtyText;
        sourcePane.MiddleEditor.Text = edits[1]; pump(sourcePane.SaveWorkingTextAsync(1));
        Verify("middle save updates clean sibling and preserves dirty sibling", cleanSibling.MiddleEditor.Text == edits[1] && !cleanSibling.TextDirty(1) && dirtySibling.MiddleEditor.Text == dirtyText && dirtySibling.TextDirty(1));
        Verify("dirty middle sibling stale revision refuses save", Refused(() => dirtySibling.SaveWorkingTextAsync(1)) && dirtySibling.MiddleEditor.Text == dirtyText && sourcePane.MiddleEditor.Text == edits[1]);
        var late = Open(); late.MiddleEditor.Text = edits[1];
        late.TextSaveBeforePublish = () => { late.MiddleEditor.Text += "\nlate"; return Task.CompletedTask; };
        pump(late.SaveWorkingTextAsync(1)); late.TextSaveBeforePublish = null;
        Verify("late middle edit remains dirty after captured working save", late.MiddleEditor.Text == edits[1] + "\nlate" && late.TextDirty(1));
        var stale = Open(); var retainedDiff = stale.CurrentDiff; var retainedMiddle = stale.MiddleEditor.Text;
        stale.IndependentTextReadyForAdoption = () => { stale.IndependentTextReadyForAdoption = null; stale.MiddleEditor.Text += "\nlate-adoption"; };
        pump(stale.ComparePathsAsync());
        Verify("late comparison adoption preserves current middle edit", ReferenceEquals(stale.CurrentDiff, retainedDiff) && stale.MiddleEditor.Text == retainedMiddle + "\nlate-adoption" && stale.TextDirty(1));
        var ancestor = Open();
        Verify("clean Archive role case starts with independent savepoints", Enumerable.Range(0, 3).All(side => !ancestor.TextDirty(side)));
        ancestor.SelectIndependentText(false); pump(ancestor.ComparePathsAsync());
        var ancestorProject = ancestor.CaptureProject();
        Verify("clean Archive Independent to ancestor adopts legacy roles", ancestorProject.TextInputs is null && ancestorProject.TextComparisonPair is null
            && ancestor.MiddleEditor.IsReadOnly && !ancestor.CanEditArchiveText(1) && !ancestor.LeftEditor.IsReadOnly && !ancestor.RightEditor.IsReadOnly
            && Enumerable.Range(0, 3).All(side => ancestor.TextEditor(side).Text == originals[side]), ancestor.ComparisonStatus ?? "");
        var roleLate = Open(); var roleLateDiff = roleLate.CurrentDiff; var roleLateDocuments = roleLate.CaptureIndependentTextDocuments();
        var roleLateBodies = Enumerable.Range(0, 3).Select(side => roleLate.TextEditor(side).Text).ToArray();
        object? roleLateExpectedState = null; var roleLateHookCalled = false;
        roleLate.SelectIndependentText(false);
        roleLate.ArchiveSourceReadyForAdoption = () =>
        {
            roleLateHookCalled = true; roleLate.ArchiveSourceReadyForAdoption = null;
            roleLate.MiddleEditor.Text += "\nlate-role-edit"; roleLateExpectedState = roleLate.CaptureIndependentTextState();
        };
        pump(roleLate.ComparePathsAsync());
        Verify("Archive ancestor candidate rejects later middle edit and retains all documents savepoints and old diff", roleLateHookCalled
            && Equals(roleLateExpectedState, roleLate.CaptureIndependentTextState()) && ReferenceEquals(roleLateDiff, roleLate.CurrentDiff)
            && roleLate.CaptureIndependentTextDocuments().Select((document, side) => ReferenceEquals(document, roleLateDocuments[side])).All(value => value)
            && roleLate.LeftEditor.Text == roleLateBodies[0] && roleLate.MiddleEditor.Text == roleLateBodies[1] + "\nlate-role-edit" && roleLate.RightEditor.Text == roleLateBodies[2]
            && !roleLate.TextDirty(0) && roleLate.TextDirty(1) && !roleLate.TextDirty(2) && roleLate.CaptureProject().TextInputs?.Semantics == "Independent",
            roleLate.ComparisonStatus ?? "");
        roleLate.SelectIndependentText();
        Verify("rejected Archive ancestor switch can return to editable Independent role without discarding late edit", !roleLate.MiddleEditor.IsReadOnly
            && roleLate.MiddleEditor.Text == roleLateBodies[1] + "\nlate-role-edit" && roleLate.TextDirty(1) && ReferenceEquals(roleLateDiff, roleLate.CurrentDiff)
            && roleLate.CaptureProject().TextInputs?.Semantics == "Independent" && MiddleSave(roleLate).IsVisible);
        var roleRollback = Open(); var roleRollbackState = roleRollback.CaptureIndependentTextState(); var roleRollbackHookCalled = false;
        roleRollback.SelectIndependentText(false);
        roleRollback.ArchiveSourceReadyForAdoption = () => { roleRollbackHookCalled = true; roleRollback.ArchiveSourceReadyForAdoption = null; roleRollback.SelectIndependentText(); };
        pump(roleRollback.ComparePathsAsync());
        Verify("Archive ancestor candidate rejects central role rollback and keeps old independent documents", roleRollbackHookCalled
            && Equals(roleRollbackState, roleRollback.CaptureIndependentTextState()) && roleRollback.CaptureProject().TextInputs?.Semantics == "Independent",
            roleRollback.ComparisonStatus ?? "");
        var roleAba = Open(); var roleAbaDiff = roleAba.CurrentDiff; var roleAbaHookCalled = false;
        roleAba.SelectIndependentText(false); var roleAbaState = roleAba.CaptureIndependentTextState(); var roleAbaRevisions = roleAba.CaptureIndependentTextRevisions();
        roleAba.ArchiveSourceReadyForAdoption = () =>
        {
            roleAbaHookCalled = true; roleAba.ArchiveSourceReadyForAdoption = null;
            var before = roleAba.MiddleEditor.Text; roleAba.MiddleEditor.Text = before + "\nABA"; roleAba.MiddleEditor.Text = before;
        };
        pump(roleAba.ComparePathsAsync());
        Verify("Archive ancestor candidate rejects middle revision ABA even when original text returns", roleAbaHookCalled
            && roleAba.CaptureIndependentTextRevisions()[1] > roleAbaRevisions[1] && Equals(roleAbaState, roleAba.CaptureIndependentTextState())
            && ReferenceEquals(roleAbaDiff, roleAba.CurrentDiff) && roleAba.CaptureProject().TextInputs?.Semantics == "Independent", roleAba.ComparisonStatus ?? "");
        roleAba.SelectIndependentText();
        for (var savedSide = 0; savedSide < 3; savedSide++)
        {
            var externalCase = Open();
            for (var side = 0; side < 3; side++) { externalCase.TextEditor(side).Text = edits[side]; pump(externalCase.SaveWorkingTextAsync(side)); }
            var externalSourceProject = externalCase.CaptureProject();
            var sourceRoots = Enumerable.Range(0, 3).Select(side => ProjectInputs.Archive(externalSourceProject, side)!.RootPath).ToArray();
            var target = Path.Combine(folder, $"external-{roles[savedSide]}.text");
            externalCase.TextSideSavePathPicker = requestedSide => requestedSide == savedSide ? Task.FromResult<string?>(target) : throw new InvalidOperationException("異なる側の保存dialogが要求されました。");
            pump(externalCase.SaveTextAsAsync(savedSide)); externalCase.TextSideSavePathPicker = null;
            var externalProject = externalCase.CaptureProject();
            Verify($"explicit external SaveAs side {savedSide} changes only selected kind and exact encoding BOM EOL", Enumerable.Range(0, 3).All(side => externalProject.TextInputs!.Side(side).Kind == (side == savedSide ? "Physical" : "Archive")
                && (ProjectInputs.Archive(externalProject, side) is null) == (side == savedSide) && !externalCase.TextDirty(side) && externalCase.TextEditor(side).Text == edits[side])
                && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "expected", roles[savedSide] + "-edited.text"))));
            Verify($"explicit external SaveAs side {savedSide} retains every source root and forbids former root output", sourceRoots.Select((path, side) => File.ReadAllBytes(path).SequenceEqual(rootBytes[side])).All(value => value)
                && Refused(() => externalCase.SaveTextToAsync(savedSide, sourceRoots[savedSide])) && File.ReadAllBytes(sourceRoots[savedSide]).SequenceEqual(rootBytes[savedSide]));
            var workspace = Path.Combine(folder, $"external-{roles[savedSide]}.json");
            pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new() { Entries = [externalProject] }));
            ComparisonProject? restored = null; async Task ReloadExternal() { restored = await WorkspaceStore.LoadAsync(workspace); }
            pump(ReloadExternal()); var restoredPane = Open(restored!);
            Verify($"external side {savedSide} v6 reload preserves all three edited bodies and selected Physical role", Enumerable.Range(0, 3).All(side => restoredPane.TextEditor(side).Text == edits[side]
                && restoredPane.CaptureProject().TextInputs!.Side(side).Kind == (side == savedSide ? "Physical" : "Archive") && !restoredPane.TextDirty(side)));
        }
        bool RootChangedRefused(Func<Task> action)
        {
            try { pump(action()); return false; }
            catch (InvalidDataException error) when (error.Message == "内包文書の原本アーカイブが変更されています。") { return true; }
        }
        using (var evidenceStream = File.Create(Path.Combine(folder, "tampered-roots.json")))
        using (var evidence = new Utf8JsonWriter(evidenceStream, new JsonWriterOptions { Indented = true }))
        {
            evidence.WriteStartArray();
            for (var changedSide = 0; changedSide < 3; changedSide++)
            {
                var tampered = Open(); var source = ProjectInputs.Archive(tampered.CaptureProject(), changedSide)!;
                var workspace = Path.Combine(folder, $"tamper-{roles[changedSide]}.json");
                pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new() { Entries = [tampered.CaptureProject()] }));
                var beforeTime = File.GetLastWriteTimeUtc(source.RootPath); var beforeBytes = File.ReadAllBytes(source.RootPath);
                var backup = source.RootPath + ".before-byte-swap"; File.Copy(source.RootPath, backup);
                var replacement = beforeBytes.ToArray(); replacement[^1] ^= 1; File.WriteAllBytes(source.RootPath, replacement); File.SetLastWriteTimeUtc(source.RootPath, beforeTime);
                Verify($"side {changedSide} root byte swap really preserves size and mtime", File.ReadAllBytes(source.RootPath).Length == beforeBytes.Length
                    && File.GetLastWriteTimeUtc(source.RootPath) == beforeTime && !File.ReadAllBytes(source.RootPath).SequenceEqual(beforeBytes));
                var oldState = tampered.CaptureIndependentTextState(); var oldRevisions = tampered.CaptureIndependentTextRevisions(); var retries = 0;
                ArchiveSourceRetryDialog? tamperedRetryDialog = null; var tamperedRetryCanceled = false;
                tampered.ArchiveSourceRetryShown = dialog => { retries++; tamperedRetryDialog = dialog; dialog.Close(); };
                try { pump(tampered.ComparePathsAsync()); }
                catch (OperationCanceledException) when (retries == 1 && tamperedRetryDialog is { IsVisible: false }) { tamperedRetryCanceled = true; }
                finally { tampered.ArchiveSourceRetryShown = null; }
                Verify($"side {changedSide} rejected root read reaches retry and explicit dialog cancellation is observed", tamperedRetryCanceled && retries == 1
                    && tamperedRetryDialog is { IsVisible: false } && !window.OwnedWindows.OfType<ArchiveSourceRetryDialog>().Any(), $"retryCount={retries}; cancellationObserved={tamperedRetryCanceled}");
                Verify($"side {changedSide} same size mtime root swap rejects compare and retains all old documents and savepoints", retries == 1
                    && Equals(oldState, tampered.CaptureIndependentTextState()) && oldRevisions.SequenceEqual(tampered.CaptureIndependentTextRevisions()));
                tampered.TextEditor(changedSide).Text = edits[changedSide];
                var dirtyState = tampered.CaptureIndependentTextState(); var dirtyRevisions = tampered.CaptureIndependentTextRevisions();
                var protectedOutput = Path.Combine(folder, $"tamper-{roles[changedSide]}-saved.text"); File.WriteAllText(protectedOutput, "KEEP-TAMPER-SAVE");
                Verify($"side {changedSide} same size mtime root swap rejects working and external save before publication", RootChangedRefused(() => tampered.SaveWorkingTextAsync(changedSide))
                    && RootChangedRefused(() => tampered.SaveTextToAsync(changedSide, protectedOutput)) && File.ReadAllText(protectedOutput) == "KEEP-TAMPER-SAVE"
                    && Equals(dirtyState, tampered.CaptureIndependentTextState()) && dirtyRevisions.SequenceEqual(tampered.CaptureIndependentTextRevisions()) && tampered.TextDirty(changedSide));
                evidence.WriteStartObject(); evidence.WriteNumber("side", changedSide); evidence.WriteString("rootPath", source.RootPath); evidence.WriteString("backupPath", backup);
                evidence.WriteString("restore", "Copy backupPath to rootPath and restore beforeMtimeUtc; preserve original attributes.");
                evidence.WriteString("attributes", File.GetAttributes(backup).ToString()); evidence.WriteNumber("beforeSize", beforeBytes.Length); evidence.WriteNumber("afterSize", replacement.Length);
                evidence.WriteString("beforeMtimeUtc", beforeTime); evidence.WriteString("afterMtimeUtc", File.GetLastWriteTimeUtc(source.RootPath));
                evidence.WriteString("beforeSha256", Convert.ToHexString(SHA256.HashData(beforeBytes))); evidence.WriteString("afterSha256", Convert.ToHexString(SHA256.HashData(replacement))); evidence.WriteEndObject();
            }
            evidence.WriteEndArray();
        }
        var corruptFixture = Path.GetFullPath("tests/Fixtures/Archives/Sources/late-bad-sibling.zip");
        const string corruptSha = "FA5B0EF47AB5A53318AE5907818B2848A72DE6523207C50490C1C652F3E1A453";
        Verify("legacy CC0 later entry fixture retains fixed SHA", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corruptFixture))) == corruptSha);
        var corruptRoot = Path.Combine(folder, "late-bad-sibling.zip"); File.Copy(corruptFixture, corruptRoot);
        for (var corruptSide = 0; corruptSide < 3; corruptSide++)
        {
            var pane = Open(); var corruptProject = pane.CaptureProject(); var corruptSource = ProjectInputs.Archive(corruptProject, corruptSide)!;
            corruptSource.RootPath = corruptRoot; corruptSource.EntryChain = ["inner.zip"]; corruptSource.LeafEntry = "leaf.txt"; corruptSource.RootSha256 = corruptSha;
            pump(WorkspaceStore.SaveWorkspaceAsync(Path.Combine(folder, $"corrupt-{roles[corruptSide]}.json"), new() { Entries = [corruptProject] }));
            pane.ApplyProject(corruptProject); var state = pane.CaptureIndependentTextState(); var revisions = pane.CaptureIndependentTextRevisions(); var retryCount = 0;
            ArchiveSourceRetryDialog? corruptRetryDialog = null; var corruptRetryCanceled = false;
            pane.ArchiveSourceRetryShown = dialog => { retryCount++; corruptRetryDialog = dialog; dialog.Close(); };
            try { pump(pane.ComparePathsAsync()); }
            catch (OperationCanceledException) when (retryCount == 1 && corruptRetryDialog is { IsVisible: false }) { corruptRetryCanceled = true; }
            finally { pane.ArchiveSourceRetryShown = null; }
            Verify($"side {corruptSide} later CRC rejection reaches retry and explicit dialog cancellation is observed", corruptRetryCanceled && retryCount == 1
                && corruptRetryDialog is { IsVisible: false } && !window.OwnedWindows.OfType<ArchiveSourceRetryDialog>().Any(), $"retryCount={retryCount}; cancellationObserved={corruptRetryCanceled}");
            Verify($"side {corruptSide} valid leading leaf never adopted when later sibling CRC is corrupt despite matching root SHA", retryCount == 1
                && ProjectInputs.Archive(pane.CaptureProject(), corruptSide)!.RootSha256 == corruptSha && Equals(state, pane.CaptureIndependentTextState())
                && revisions.SequenceEqual(pane.CaptureIndependentTextRevisions()) && Enumerable.Range(0, 3).All(side => pane.TextEditor(side).Text == originals[side])
                && !window.OwnedWindows.OfType<ArchiveSourceRetryDialog>().Any());
        }
        var external = Open(); external.MiddleEditor.Text = edits[1]; var externalPath = Path.Combine(folder, "middle-external.text");
        pump(external.SaveTextToAsync(1, externalPath));
        Verify("external Physical middle actual save label updates", MiddleSave(external).IsVisible && Equals(MiddleSave(external).Content, "中央を保存"));
        Verify("external save makes only middle Physical and preserves BOM", external.CaptureProject().BaseArchiveInput is null && external.CaptureProject().TextInputs?.Middle?.Kind == "Physical" && external.CaptureProject().LeftArchiveInput is not null && external.CaptureProject().RightArchiveInput is not null && File.ReadAllBytes(externalPath).SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "expected", "middle-edited.text"))));
        Verify("externalized middle still protects former root", Refused(() => external.SaveTextToAsync(1, roots[1])) && roots.Select((path, side) => File.ReadAllBytes(path).SequenceEqual(rootBytes[side])).All(value => value));
        var mixed = Load(); mixed.LeftArchiveInput = null; mixed.LeftReadOnly = false; mixed.LeftPath = Path.Combine(folder, "expected", "left-original.text"); mixed.TextInputs!.Side(0).Kind = "Physical";
        mixed.RightArchiveInput = null; mixed.RightReadOnly = false; mixed.RightPath = ""; mixed.TextInputs.Side(2).Kind = "Untitled";
        var mixedPane = Open(mixed); Verify("Physical Archive Untitled mixture retains three present roles", mixedPane.LeftEditor.Text == originals[0] && mixedPane.MiddleEditor.Text == originals[1] && mixedPane.RightEditor.Text == "" && Enumerable.Range(0, 3).All(side => !mixedPane.TextEditor(side).IsReadOnly));
        Verify("mixed roles keep actual Archive middle save label", MiddleSave(mixedPane).IsVisible && Equals(MiddleSave(mixedPane).Content, "中央の作業版を保存"));
        window.SelectSession(window.SessionPanes.ToList().IndexOf(main)); Dispatcher.UIThread.RunJobs();
        Layout("normal", main); window.Width = 850; window.Height = 550; Dispatcher.UIThread.RunJobs(); Layout("minimum", main);
        for (var index = 0; index < 16; index++) window.AddSession();
        window.SelectSession(window.SessionPanes.ToList().IndexOf(main)); Dispatcher.UIThread.RunJobs(); Layout("many-tabs", main);
        using (var stream = File.Create(Path.Combine(folder, "observations.json")))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var observation in observations)
            {
                writer.WriteStartObject(); writer.WriteString("name", observation.Name);
                writer.WriteStartArray("texts"); foreach (var text in observation.Texts) writer.WriteStringValue(text); writer.WriteEndArray();
                writer.WriteStartArray("dirty"); foreach (var dirty in observation.Dirty) writer.WriteBooleanValue(dirty); writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        using (var stream = File.Create(Path.Combine(folder, "original-roots.json")))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            for (var side = 0; side < roots.Length; side++)
            {
                var bytes = File.ReadAllBytes(roots[side]);
                writer.WriteStartObject(); writer.WriteString("path", roots[side]);
                writer.WriteString("sha256", Convert.ToHexString(SHA256.HashData(bytes)));
                writer.WriteBoolean("retained", bytes.SequenceEqual(rootBytes[side])); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        Verify("every original ZIP byte retained", roots.Select((path, side) => File.ReadAllBytes(path).SequenceEqual(rootBytes[side])).All(value => value));
        foreach (var pane in window.SessionPanes) pane.DiscardChanges();
        HeadlessIndependentArchiveBoundaryChecks.Run(window, output, pump, check, screenshot);
        void Layout(string name, ComparisonPane pane)
        {
            // session選択だけでは差分一覧tabのままなので、三者本文をvisual treeへ接続してから測る。
            window.SelectSession(window.SessionPanes.ToList().IndexOf(pane));
            pane.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
            Verify(name + " three editor visuals attached to window", Enumerable.Range(0, 3).All(side => pane.TextEditor(side).TranslatePoint(new Point(0, 0), window) is not null));
            var save = MiddleSave(pane);
            var toolbar = save.GetVisualAncestors().OfType<ScrollViewer>().First();
            Verify(name + " save is contained in measured toolbar", toolbar.GetVisualDescendants().Contains(save));
            toolbar.Offset = new Vector(0, toolbar.Extent.Height); Dispatcher.UIThread.RunJobs();
            save.BringIntoView(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
            using (var stream = File.Create(Path.Combine(folder, "layout-" + name + ".json")))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject(); writer.WriteNumber("windowWidth", window.Bounds.Width); writer.WriteNumber("windowHeight", window.Bounds.Height);
                writer.WriteNumber("tabCount", window.SessionPanes.Count());
                writer.WriteNumber("toolbarViewportWidth", toolbar.Viewport.Width); writer.WriteNumber("toolbarViewportHeight", toolbar.Viewport.Height);
                Bounds("leftEditor", pane.LeftEditor); Bounds("middleEditor", pane.MiddleEditor); Bounds("rightEditor", pane.RightEditor); Bounds("middleSave", save); Bounds("toolbar", toolbar);
                writer.WriteEndObject();
                void Bounds(string key, Control control)
                {
                    var point = control.TranslatePoint(new Point(0, 0), window);
                    writer.WriteStartObject(key); writer.WriteNumber("width", control.Bounds.Width); writer.WriteNumber("height", control.Bounds.Height); writer.WriteBoolean("visible", control.IsVisible);
                    if (point is { } position) { writer.WriteNumber("windowX", position.X); writer.WriteNumber("windowY", position.Y); }
                    else { writer.WriteNull("windowX"); writer.WriteNull("windowY"); }
                    writer.WriteEndObject();
                }
            }
            Verify(name + " three editor viewports reachable", Enumerable.Range(0, 3).All(side => pane.TextEditor(side).Bounds.Height >= 100 && pane.TextEditor(side).Bounds.Width > 0));
            var position = save.TranslatePoint(new Point(0, 0), window);
            var clippedPosition = save.TranslatePoint(new Point(0, 0), toolbar);
            Verify(name + " middle save reachable inside actual toolbar clip after scrolling to target", position is { } at && at.X >= 0 && at.Y >= 0
                && at.X + save.Bounds.Width <= window.Bounds.Width && at.Y + save.Bounds.Height <= window.Bounds.Height
                && clippedPosition is { } inside && inside.X >= 0 && inside.Y >= 0
                && inside.X + save.Bounds.Width <= toolbar.Viewport.Width && inside.Y + save.Bounds.Height <= toolbar.Viewport.Height);
            screenshot("independent-archive-text-" + name + ".png");
        }
    }
    private static async Task Wait(Func<bool> ready) { for (var attempt = 0; attempt < 1500; attempt++) { if (ready()) return; await Task.Delay(10); } throw new TimeoutException("Independent Archive中央保存が完了しません。"); }
}
