using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace DiffBeacon.App;

internal static class HeadlessBinaryWorkingChecks
{
    internal static void Run(MainWindow window, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "binary-working"); Directory.CreateDirectory(folder);
        var refusals = new List<string>();
        byte[] a = [0, 1, 2, 3, 0xff, 0x80], b = [0, 9, 2, 4, 0xff, 0x81];
        var navigationLeft = new byte[8194]; var navigationRight = new byte[8194]; navigationRight[8192] = 7;
        using (var navigation = new SpecializedViews.BinaryPanel(navigationLeft, navigationRight, false, false))
        {
            // ScrollViewerのtemplateも実Windowで作り、二者の実ボタン経路を操作する。
            var navigationWindow = new Window { Content = navigation, Width = 1000, Height = 700 };
            navigationWindow.Show(); Dispatcher.UIThread.RunJobs();
            try
            {
            navigation.Differences.SelectedIndex = 0;
            Report("difference selection navigates beyond initial Hex page", navigation.RightHex.Text?.Trim() == "07 00");
            Report("difference navigation retains selected copy range", navigation.Differences.SelectedIndex == 0);
            navigation.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "← 選択範囲"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Report("selected difference copy after navigation updates exact bytes", navigation.Capture(false).CopyBytes().SequenceEqual(navigationRight));
            navigation.Undo();
            navigation.SetOffset(0); var pending = "01" + (navigation.LeftHex.Text ?? "")[2..]; navigation.LeftHex.Text = pending;
            navigation.Differences.SelectedIndex = -1; navigation.Differences.SelectedIndex = 0;
            Report("difference navigation retains unapplied Hex draft and page", navigation.LeftHex.Text == pending
                && navigation.RightHex.Text?.Trim() != "07 00" && navigation.Pending(false));
            }
            finally { navigationWindow.Close(); }
        }
        var roots = new[] { Path.Combine(folder, "left.zip"), Path.Combine(folder, "right.zip") };
        for (var side = 0; side < 2; side++) File.WriteAllBytes(roots[side], Zip(("inner.zip", Zip(("leaf.bin", side == 0 ? a : b), ("empty.bin", []), ("ascii.txt", "original text\n"u8.ToArray())))));
        var hashes = roots.Select(Hash).ToArray();
        var pane = window.AddSession(); pane.ApplyProject(new() { Mode = "Binary", LeftReadOnly = true, RightReadOnly = true,
            LeftArchiveInput = Input(0), RightArchiveInput = Input(1) }); pump(pane.ComparePathsAsync());
        var panel = Binary(pane);
        Report("Binary archive inherits writable intent independently", !panel.LeftReadOnly && !panel.RightReadOnly);
        var workingSaved = true; try { pump(pane.SaveAsync(false)); } catch (InvalidOperationException) { workingSaved = false; }
        Report("Binary archive ordinary save retains original source and saves working bytes", workingSaved && pane.CaptureProject().LeftArchiveInput?.WorkingDocuments is not null && roots.Select(Hash).SequenceEqual(hashes));
        var savedProject = pane.CaptureProject(); var projectPath = Path.Combine(folder, "working.json");
        pump(WorkspaceStore.SaveWorkspaceAsync(projectPath, new() { Entries = [savedProject] }, publishedAsset: window.ArchiveLifetime.RegisterAsset));
        var restored = WorkspaceStore.LoadAsync(projectPath); pump(restored);
        Report("Binary workspace v5 typed asset reloads exact bytes", JsonDocument.Parse(File.ReadAllText(projectPath)).RootElement.GetProperty("formatVersion").GetInt32() == 5
            && restored.Result.LeftArchiveInput!.WorkingDocuments![0].Kind == "Binary" && restored.Result.LeftArchiveInput.WorkingDocuments[0].Bytes!.SequenceEqual(a));
        var asset = restored.Result.LeftArchiveInput!.WorkingDocuments![0].SnapshotPath!;
        Report("published Binary asset is protected for window lifetime", Refused(() => pump(panel.SaveToAsync(false, asset))) && Hash(asset) == HashBytes(a));
        var reopened = window.AddSession(); reopened.ApplyProject(restored.Result); pump(reopened.ComparePathsAsync());
        Report("Binary restored pane reads working bytes", Binary(reopened).Capture(false).CopyBytes().SequenceEqual(a));
        var package = Path.Combine(folder, "working-package.zip"); pump(ComparisonPackage.CreateAsync(new() { Entries = [savedProject] }, package, new(true, false, false, true)));
        Report("Binary reportless package succeeds and HTML patch remain refused", File.Exists(package)
            && Refused(() => pump(ComparisonPackage.CreateAsync(new() { Entries = [savedProject] }, Path.Combine(folder, "refused-report.zip"), new(true, true, false, true))))
            && Refused(() => pump(ComparisonPackage.CreateAsync(new() { Entries = [savedProject] }, Path.Combine(folder, "refused-patch.zip"), new(true, false, true, true)))));
        window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), pane)); screenshot("binary-working-initial.png");
        var external = Path.Combine(folder, "external-left.bin"); pump(panel.SaveToAsync(false, external));
        Report("Binary external save adopts only the successful side", pane.CaptureProject().LeftArchiveInput is null && pane.CaptureProject().LeftPath == external
            && pane.CaptureProject().RightArchiveInput is not null && File.ReadAllBytes(external).SequenceEqual(a));
        var normal = window.AddSession(); var leftFile = Path.Combine(folder, "ordinary-left.bin"); var rightFile = Path.Combine(folder, "ordinary-right.bin");
        File.WriteAllBytes(leftFile, a); File.WriteAllBytes(rightFile, b); normal.ApplyProject(new() { Mode = "Binary", LeftPath = leftFile, RightPath = rightFile }); pump(normal.ComparePathsAsync());
        var normalPanel = Binary(normal); var normalOutput = Path.Combine(folder, "ordinary-saved.bin"); normal.BinarySavePathPicker = _ => Task.FromResult<string?>(normalOutput);
        var saveButton = normalPanel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "左を別名保存"));
        saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump(WaitSaveButton()); normal.BinarySavePathPicker = null;
        Report("ordinary Binary external save adopts selected path", normal.CaptureProject().LeftPath == normalOutput && normal.CaptureProject().RightPath == rightFile);
        normalPanel.LeftHex.Text = "10 01 02 03 FF 80";
        var noApply = Path.Combine(folder, "pending-refused.bin"); File.WriteAllBytes(noApply, [0x55]);
        Report("unapplied Hex cannot be silently saved or copied", Refused(() => pump(normalPanel.SaveToAsync(false, noApply)))
            && Refused(() => normalPanel.CopyRange(true, 0, 1)) && File.ReadAllBytes(noApply).SequenceEqual(new byte[] { 0x55 }) && normalPanel.Pending(false));
        normalPanel.Apply(false); normalPanel.RightHex.Text = "20 09 02 04 FF 81"; normalPanel.Apply(true);
        var rightBefore = normalPanel.Capture(true).Sha256; pump(normal.SaveAsync(false));
        Report("ordinary normal save publishes own input and only its savepoint", File.ReadAllBytes(normalOutput).SequenceEqual(new byte[] { 0x10, 1, 2, 3, 0xff, 0x80 })
            && !normalPanel.Dirty(false) && normalPanel.Dirty(true) && normalPanel.Capture(true).Sha256 == rightBefore);
        normalPanel.Undo(); Report("shared Undo affects last side and saved SHA", normalPanel.Capture(true).CopyBytes().SequenceEqual(b) && !normalPanel.Dirty(true) && !normalPanel.Dirty(false));
        normalPanel.Redo(); Report("shared Redo restores opposite dirty side", normalPanel.Dirty(true));
        var beforeLate = normalPanel.Capture(false).CopyBytes();
        normal.BinarySaveBeforePublish = () => { normalPanel.LeftHex.Text = "30 01 02 03 FF 80"; normalPanel.Apply(false); return Task.CompletedTask; };
        var late = Path.Combine(folder, "late-edit.bin"); pump(normalPanel.SaveToAsync(false, late)); normal.BinarySaveBeforePublish = null;
        Report("late applied edit remains dirty after immutable publication", File.ReadAllBytes(late).SequenceEqual(beforeLate) && normalPanel.Dirty(false) && normalPanel.Capture(false).CopyBytes()[0] == 0x30 && normalPanel.Dirty(true));
        var captured = normalPanel.Capture(false); var other = Binary(reopened).Capture(false);
        Report("capture is owner and side bound", Refused(() => normalPanel.Session.MarkSaved(0, other)) && Refused(() => normalPanel.Session.MarkSaved(1, captured)) && normalPanel.Dirty(false));
        var guarded = Path.Combine(folder, "guarded.bin"); File.WriteAllBytes(guarded, [0x66]);
        using (var cancel = new CancellationTokenSource())
        {
            normal.BinarySaveBeforePublish = () => { cancel.Cancel(); return Task.CompletedTask; };
            Report("cancel before publication preserves output and dirty", Refused(() => pump(normalPanel.SaveToAsync(false, guarded, cancel.Token))) && File.ReadAllBytes(guarded).SequenceEqual(new byte[] { 0x66 }) && normalPanel.Dirty(false));
        }
        normal.BinarySaveBeforePublish = () => { normal.LeftPath.Text = leftFile; return Task.CompletedTask; };
        Report("changed path before publication preserves output and bytes", Refused(() => pump(normalPanel.SaveToAsync(false, guarded))) && File.ReadAllBytes(guarded).SequenceEqual(new byte[] { 0x66 }) && normalPanel.Capture(false).Sha256 == captured.Sha256);
        normal.LeftPath.Text = late;
        normal.BinarySaveBeforePublish = () => { normalPanel.LeftReadOnly = true; return Task.CompletedTask; };
        Report("readonly change before publication preserves output and savepoint", Refused(() => pump(normalPanel.SaveToAsync(false, guarded))) && File.ReadAllBytes(guarded).SequenceEqual(new byte[] { 0x66 }) && normalPanel.Dirty(false));
        normalPanel.LeftReadOnly = false; normalPanel.ApplyReadOnly?.Invoke(); normal.BinarySaveBeforePublish = null;
        normal.BinarySaveReadyForAdoption = () => throw new InvalidOperationException("injected adoption refusal"); var adoption = Path.Combine(folder, "adoption-failed.bin");
        Report("publication adoption failure preserves source and savepoint", Refused(() => pump(normalPanel.SaveToAsync(false, adoption))) && normal.CaptureProject().LeftPath == late && File.ReadAllBytes(adoption).SequenceEqual(captured.CopyBytes()) && normalPanel.Dirty(false));
        normal.BinarySaveReadyForAdoption = null;
        var physical = Path.Combine(folder, "physical-readonly.bin"); File.WriteAllBytes(physical, [0x44]); var physicalAttributes = File.GetAttributes(physical);
        normal.BinarySaveBeforePublish = () => { File.SetAttributes(physical, physicalAttributes | FileAttributes.ReadOnly); return Task.CompletedTask; };
        try { Report("physical output readonly at publication preserves bytes", Refused(() => pump(normalPanel.SaveToAsync(false, physical))) && File.ReadAllBytes(physical).SequenceEqual(new byte[] { 0x44 }) && normalPanel.Dirty(false)); }
        finally { normal.BinarySaveBeforePublish = null; File.SetAttributes(physical, physicalAttributes); }
        var link = Path.Combine(folder, "output-link.bin"); File.CreateSymbolicLink(link, physical);
        Report("link output refuses publication and retains target", Refused(() => pump(normalPanel.SaveToAsync(false, link))) && File.ReadAllBytes(physical).SequenceEqual(new byte[] { 0x44 }));
        normal.BinarySaveBeforePublish = () => { normal.SelectMode(1); return Task.CompletedTask; };
        Report("mode change rejects old publication without losing Binary body", Refused(() => pump(normalPanel.SaveToAsync(false, guarded))) && normalPanel.Capture(false).Sha256 == captured.Sha256 && File.ReadAllBytes(guarded).SequenceEqual(new byte[] { 0x66 }));
        normal.SelectMode(3); normal.BinarySaveBeforePublish = null;
        Report("existing directory output preserves existing children", Refused(() => pump(normalPanel.SaveToAsync(false, folder))) && File.Exists(projectPath) && Hash(asset) == HashBytes(a));
        var unchanged = normalPanel.Capture(false).Sha256; var revision = normalPanel.Session.Revision(0); normalPanel.CopyRange(false, 6, 0);
        Report("zero length copy endpoint is a true no-op", normalPanel.Capture(false).Sha256 == unchanged && normalPanel.Session.Revision(0) == revision);
        Report("huge integer copy rejects without mutation", Refused(() => normalPanel.CopyRange(false, int.MaxValue, int.MaxValue)) && normalPanel.Session.Revision(0) == revision && normalPanel.Capture(false).Sha256 == unchanged);
        var writer = reopened; var writerPanel = Binary(writer);
        var cleanSibling = window.AddSession(); cleanSibling.ApplyProject(restored.Result); pump(cleanSibling.ComparePathsAsync());
        var dirtySibling = window.AddSession(); dirtySibling.ApplyProject(restored.Result); pump(dirtySibling.ComparePathsAsync());
        var dirtyPanel = Binary(dirtySibling); dirtyPanel.LeftHex.Text = "70 01 02 03 FF 80"; dirtyPanel.Apply(false); var dirtyBytes = dirtyPanel.Capture(false).CopyBytes();
        writerPanel.LeftHex.Text = "60 01 02 03 FF 80"; writerPanel.Apply(false); pump(writer.SaveAsync(false));
        Report("working save updates clean sibling preserves dirty sibling", Binary(cleanSibling).Capture(false).CopyBytes()[0] == 0x60 && dirtyPanel.Capture(false).CopyBytes().SequenceEqual(dirtyBytes) && dirtyPanel.Dirty(false));
        Report("dirty sibling cannot overwrite new saved revision", Refused(() => pump(dirtySibling.SaveAsync(false))) && writer.CaptureProject().LeftArchiveInput!.WorkingDocuments![0].Bytes![0] == 0x60);
        var parent = window.AddSession(); parent.ApplyProject(new() { Mode = "Archive", LeftReadOnly = true, RightReadOnly = true,
            LeftArchiveInput = Input(0) with { LeafEntry = null }, RightArchiveInput = Input(1) with { LeafEntry = null } }); pump(parent.ComparePathsAsync());
        var parentPanel = parent.GetVisualDescendants().OfType<ArchivePanel>().Single(); var row = parentPanel.Rows.Single(value => value.Path == "leaf.bin"); pump(parentPanel.PreviewAsync(row));
        Report("parent rows and preview use saved Binary bytes", row.Left!.Sha256 == writerPanel.Capture(false).Sha256 && parentPanel.PreviewText.Contains("60 01 02 03 FF 80", StringComparison.Ordinal)
            && parent.CaptureProject().LeftArchiveInput!.WorkingDocuments![0].Kind == "Binary");
        parentPanel.EntryList.SelectedItem = row; parentPanel.EntryKind.SelectedIndex = 2; pump(parentPanel.OpenSelectedAsync()); var child = window.ActivePane;
        Report("reopened Binary child reads latest saved working version", Binary(child).Capture(false).CopyBytes()[0] == 0x60);
        var tab = window.GetVisualDescendants().OfType<TabControl>().SelectMany(value => value.Items.OfType<TabItem>()).Single(value => ReferenceEquals(value.Content, parent));
        ((StackPanel)tab.Header!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Report("parent close preserves child saved bytes and ordinary save", parent.IsDisposed && !child.IsDisposed && Binary(child).Capture(false).CopyBytes()[0] == 0x60);
        pump(child.SaveAsync(false));
        var text = window.AddSession(); text.ApplyProject(new() { Mode = "Text", LeftReadOnly = true, RightReadOnly = true,
            LeftArchiveInput = Input(0) with { LeafEntry = "ascii.txt" }, RightArchiveInput = Input(1) with { LeafEntry = "ascii.txt" } }); pump(text.ComparePathsAsync());
        text.LeftEditor.Text = "dirty original Text\n";
        var cross = window.AddSession(); cross.ApplyProject(new() { Mode = "Binary", LeftReadOnly = true, RightReadOnly = true,
            LeftArchiveInput = Input(0) with { LeafEntry = "ascii.txt" }, RightArchiveInput = Input(1) with { LeafEntry = "ascii.txt" } }); pump(cross.ComparePathsAsync());
        var crossPanel = Binary(cross); var crossBytes = crossPanel.Capture(false).CopyBytes(); crossBytes[0] = 0; crossPanel.LeftHex.Text = Convert.ToHexString(crossBytes); crossPanel.Apply(false); pump(cross.SaveAsync(false));
        Report("Binary kind change preserves dirty Text and explicitly rejects stale save", text.LeftEditor.Text == "dirty original Text\n" && text.HasUnsavedChanges && Refused(() => pump(text.SaveAsync(false))) && Refused(text.EnsureArchiveDraftSaved));
        text.DiscardChanges(); text.SelectMode(3); pump(text.ComparePathsAsync());
        Report("same leaf explicit Binary recompare consumes current working kind", Binary(text).Capture(false).CopyBytes().SequenceEqual(crossBytes));
        var roBefore = File.GetAttributes(roots[0]); writer.BinarySaveBeforePublish = () => { File.SetAttributes(roots[0], roBefore | FileAttributes.ReadOnly); return Task.CompletedTask; };
        try { Report("physical root readonly change rejects working commit", Refused(() => pump(writer.SaveAsync(false))) && roots.Select(Hash).SequenceEqual(hashes)); }
        finally { writer.BinarySaveBeforePublish = null; File.SetAttributes(roots[0], roBefore); }
        var rootOriginal = File.ReadAllBytes(roots[0]); var rootTime = File.GetLastWriteTimeUtc(roots[0]); var rootChanged = rootOriginal.ToArray(); rootChanged[^1] ^= 1;
        var rootRevision = window.ArchiveTexts.Generation; var rootDisplay = writerPanel.Capture(false).Sha256;
        File.WriteAllBytes(roots[0], rootChanged); File.SetLastWriteTimeUtc(roots[0], rootTime);
        try { Report("same size mtime root replacement cannot hide behind working bytes", Refused(() => pump(writer.SaveAsync(false))) && writerPanel.Capture(false).Sha256 == rootDisplay && window.ArchiveTexts.Generation == rootRevision); }
        finally { File.WriteAllBytes(roots[0], rootOriginal); File.SetLastWriteTimeUtc(roots[0], rootTime); }
        var isolatedRoot = Path.Combine(folder, "same-bytes-different-root.zip"); File.Copy(roots[0], isolatedRoot);
        var isolated = window.AddSession(); isolated.ApplyProject(new() { Mode = "Binary", LeftReadOnly = true, RightReadOnly = true, LeftArchiveInput = Input(0) with { RootPath = isolatedRoot }, RightArchiveInput = Input(1) }); pump(isolated.ComparePathsAsync());
        Report("same SHA distinct root does not share working bytes", Binary(isolated).Capture(false).CopyBytes().SequenceEqual(a));
        Report("workspace rejects dirty ordinary Binary", Refused(() => pump(window.SaveWorkspaceAsync(Path.Combine(folder, "dirty-workspace.json")))));
        var readonlyPane = window.AddSession(); readonlyPane.ApplyProject(new() { Mode = "Binary", LeftArchiveInput = Input(0) with { InheritedReadOnly = true }, RightArchiveInput = Input(1), LeftReadOnly = true, RightReadOnly = true }); pump(readonlyPane.ComparePathsAsync());
        var readonlyPanel = Binary(readonlyPane); var readonlyExport = Path.Combine(folder, "readonly-export.bin"); var readonlyPath = readonlyPane.CaptureProject().LeftArchiveInput!.RootPath;
        SpecializedViews.SetProjectReadOnly(readonlyPanel, false, false);
        Report("inherited readonly cannot be relaxed by panel setter", readonlyPanel.LeftReadOnly && Refused(() => readonlyPanel.CopyRange(false, 1, 1))); pump(readonlyPanel.SaveToAsync(false, readonlyExport));
        Report("readonly export does not adopt path or savepoint", readonlyPane.CaptureProject().LeftArchiveInput!.RootPath == readonlyPath && !readonlyPanel.Dirty(false));
        var undoBefore = normalPanel.Capture(false).Sha256; var undoVersion = normalPanel.Session.Revision(0); normalPanel.LeftReadOnly = true;
        Report("readonly shared Undo does not mutate history or body", Refused(() => normalPanel.Undo()) && normalPanel.Capture(false).Sha256 == undoBefore && normalPanel.Session.Revision(0) == undoVersion);
        normalPanel.LeftReadOnly = false; normalPanel.ApplyReadOnly?.Invoke();
        Report("all inputs old roots and assets remain protected", Refused(() => pump(normalPanel.SaveToAsync(false, roots[0]))) && Refused(() => pump(normalPanel.SaveToAsync(false, rightFile))) && Refused(() => pump(normalPanel.SaveToAsync(false, asset))) && roots.Select(Hash).SequenceEqual(hashes));
        var empty = window.AddSession(); empty.ApplyProject(new() { Mode = "Binary", LeftReadOnly = true, RightReadOnly = true, LeftArchiveInput = Input(0) with { LeafEntry = "empty.bin" }, RightArchiveInput = Input(1) with { LeafEntry = "empty.bin" } }); pump(empty.ComparePathsAsync()); pump(empty.SaveAsync(false));
        Report("existing zero byte leaf gets working save with identity", empty.CaptureProject().LeftArchiveInput is { MissingEntryChain: null, WorkingDocuments: not null } && Binary(empty).Capture(false).Length == 0);
        var missing = window.AddSession(); missing.ApplyProject(new() { Mode = "Binary", LeftReadOnly = true, RightReadOnly = true, LeftArchiveInput = Input(0) with { LeafEntry = null, MissingEntryChain = ["new.bin"] }, RightArchiveInput = Input(1) with { LeafEntry = "empty.bin" } }); pump(missing.ComparePathsAsync());
        var missingPath = Path.Combine(folder, "missing-zero.bin"); missing.BinarySavePathPicker = _ => Task.FromResult<string?>(missingPath); pump(missing.SaveAsync(false));
        Report("missing zero Binary uses external save side detach", File.ReadAllBytes(missingPath).Length == 0 && missing.CaptureProject().LeftArchiveInput is null && missing.CaptureProject().RightArchiveInput is not null);
        using (var history = new SpecializedViews.BinaryPanel([0], [0], false, false))
        {
            for (var index = 0; index < 256; index++) { history.LeftHex.Text = index % 2 == 0 ? "01" : "00"; history.Apply(false); }
            var before = history.Capture(false).Sha256; var beforeRevision = history.Session.Revision(0); history.LeftHex.Text = "01";
            Report("shared history 256 action refusal is atomic", Refused(() => history.Apply(false)) && history.CaptureApplied(false).Sha256 == before && history.Session.Revision(0) == beforeRevision && history.Pending(false) && history.Session.CanUndo);
        }
        using (var history = new SpecializedViews.BinaryPanel(new byte[16 * 1024 * 1024], new byte[16 * 1024 * 1024], false, false))
        {
            Endpoints(history, 1); history.CopyRange(true, 0, 16 * 1024 * 1024); Endpoints(history, 2);
            var before = history.Capture(true).Sha256; var beforeRevision = history.Session.Revision(1);
            Report("shared history 64 MiB refusal is atomic", Refused(() => history.CopyRange(true, 0, 16 * 1024 * 1024)) && history.Capture(true).Sha256 == before && history.Session.Revision(1) == beforeRevision && history.Session.CanUndo);
        }
        StoreBudget(256, false); StoreBudget(8, true);
        var settingsSha = writerPanel.Capture(false).Sha256; var settingsRevision = writerPanel.Session.Revision(0);
        var oldHex = writerPanel.RightHex.Text; writerPanel.RightHex.Text = "AA 09 02 04 FF 81";
        ApplySettings(writer);
        Report("settings apply preserves contained Binary writable intent and pending bytes", !writerPanel.LeftReadOnly && !writerPanel.RightReadOnly
            && writerPanel.Pending(true) && writerPanel.RightHex.Text == "AA 09 02 04 FF 81"
            && writerPanel.CaptureApplied(false).Sha256 == settingsSha && writerPanel.Session.Revision(0) == settingsRevision);
        writerPanel.RightHex.Text = oldHex;
        ApplySettings(normal, true);
        Report("settings apply updates ordinary Binary readonly without changing dirty bytes", normalPanel.LeftReadOnly && normalPanel.Dirty(false)
            && normalPanel.Capture(false).Sha256 == captured.Sha256);
        ApplySettings(readonlyPane);
        Report("settings apply retains inherited Binary readonly independently", readonlyPanel.LeftReadOnly && !readonlyPanel.RightReadOnly && !readonlyPanel.Dirty(false));
        var settingsText = window.AddSession(); settingsText.ApplyProject(new() { Mode = "Text", LeftReadOnly = true, RightReadOnly = true,
            LeftArchiveInput = Input(0) with { RootPath = isolatedRoot, LeafEntry = "ascii.txt" }, RightArchiveInput = Input(0) with { RootPath = isolatedRoot, LeafEntry = "ascii.txt", InheritedReadOnly = true } }); pump(settingsText.ComparePathsAsync());
        settingsText.LeftEditor.Text = "settings dirty Text\n"; ApplySettings(settingsText);
        Report("settings apply preserves contained Text intention and unsaved body", !settingsText.LeftEditor.IsReadOnly && settingsText.RightEditor.IsReadOnly
            && settingsText.LeftEditor.Text == "settings dirty Text\n" && settingsText.HasUnsavedChanges);
        CheckTextTransition();
        var facts = new Dictionary<string, object?> {
            ["roots"] = roots, ["rootSha256"] = hashes, ["originalLeftHex"] = Convert.ToHexString(a), ["originalRightHex"] = Convert.ToHexString(b), ["workspace"] = projectPath,
            ["asset"] = asset, ["package"] = package, ["external"] = external, ["late"] = late, ["lateHex"] = Convert.ToHexString(beforeLate), ["adoption"] = adoption,
            ["adoptionHex"] = Convert.ToHexString(captured.CopyBytes()), ["readonlyExport"] = readonlyExport,
            ["ordinaryDisplayHex"] = Convert.ToHexString(normalPanel.Capture(false).CopyBytes()), ["ordinaryLeftDirty"] = normalPanel.Dirty(false), ["ordinaryRightDirty"] = normalPanel.Dirty(true),
            ["ordinaryLeftPath"] = normal.CaptureProject().LeftPath, ["ordinaryRightPath"] = normal.CaptureProject().RightPath, ["ordinaryLeftRevision"] = normalPanel.Session.Revision(0),
            ["ordinaryCanUndo"] = normalPanel.Session.CanUndo, ["ordinaryCanRedo"] = normalPanel.Session.CanRedo, ["refusals"] = refusals.ToArray() };
        using (var file = File.Create(Path.Combine(folder, "binary-working-facts.json")))
        using (var json = new Utf8JsonWriter(file, new() { Indented = true }))
        {
            json.WriteStartObject();
            foreach (var (name, value) in facts)
            { if (value is string[] values) { json.WriteStartArray(name); foreach (var item in values) json.WriteStringValue(item); json.WriteEndArray(); } else if (value is bool flag) json.WriteBoolean(name, flag); else if (value is long number) json.WriteNumber(name, number); else json.WriteString(name, (string?)value); }
            json.WriteEndObject();
        }
        window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), writer)); screenshot("binary-working-saved.png");

        static void Endpoints(SpecializedViews.BinaryPanel history, byte value)
        {
            foreach (var offset in new[] { 0, 16 * 1024 * 1024 - 4096 })
            { history.SetOffset(offset); var bytes = history.Session.Page(0, offset); bytes[offset == 0 ? 0 : bytes.Length - 1] = value; history.LeftHex.Text = Convert.ToHexString(bytes); history.Apply(false); }
            history.SetOffset(0);
        }
        async Task WaitSaveButton()
        { for (var count = 0; count < 2000; count++) { if (saveButton.IsEnabled) return; await Task.Delay(5); } throw new TimeoutException("Binary保存ボタンの処理が完了しませんでした。"); }
        void ApplySettings(ComparisonPane target, bool? ordinaryLeftReadOnly = null)
        {
            window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), target)); Dispatcher.UIThread.RunJobs();
            target.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "比較の設定…")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            var dialog = window.OwnedWindows.Single(value => value.Title == "比較の設定");
            if (ordinaryLeftReadOnly is { } value) dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, "左を読取り専用にする")).IsChecked = value;
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "適用")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        }
        void CheckTextTransition()
        {
            var transition = window.AddSession(); var left = Path.Combine(folder, "transition-left.txt"); var right = Path.Combine(folder, "transition-right.txt");
            File.WriteAllText(left, "left transition\n"); File.WriteAllText(right, "right transition\n");
            transition.ApplyProject(new() { Mode = "Binary", LeftPath = left, RightPath = right }); pump(transition.ComparePathsAsync());
            var old = Binary(transition); old.LeftHex.Text = "00";
            transition.SelectMode(1); var opening = transition.ComparePathsAsync(); Dispatcher.UIThread.RunJobs();
            var confirmation = window.OwnedWindows.Single(value => value.Title == "未保存の変更");
            confirmation.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "キャンセル")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump(opening);
            Report("Binary to Text cancelled opening preserves pending Binary owner", !old.IsDisposed && old.Pending(false) && old.LeftHex.Text == "00");
            transition.DiscardChanges(); transition.LeftPath.Text = Path.Combine(folder, "transition-absent.txt");
            Report("Binary to Text failed loading retains previous Binary bytes", Refused(() => pump(transition.ComparePathsAsync())) && !old.IsDisposed
                && old.CaptureApplied(false).CopyBytes().SequenceEqual("left transition\n"u8.ToArray()));
            transition.LeftPath.Text = left;
            var later = transition.ComparePathsAsync(); old.RightHex.Text = "AA"; pump(later);
            Report("Binary edit during Text loading rejects candidate without releasing owner", !old.IsDisposed && old.Pending(true) && old.RightHex.Text == "AA");
            transition.DiscardChanges(); pump(transition.ComparePathsAsync());
            Report("Binary to Text adopted document releases obsolete Binary owner", old.IsDisposed && transition.CurrentDiff is not null && transition.LeftEditor.Text == "left transition\n");
            transition.LeftEditor.Text = "saved transition\n";
            Report("Binary to Text ordinary save publishes current Text", !Refused(() => pump(transition.SaveAsync(false))) && File.ReadAllText(left) == "saved transition\n");
            var original = File.ReadAllBytes(left); var attributes = File.GetAttributes(left); var physicalRefused = false;
            try
            {
                File.SetAttributes(left, attributes | FileAttributes.ReadOnly); transition.LeftEditor.Text = "readonly transition\n";
                try { pump(transition.SaveAsync(false)); } catch (UnauthorizedAccessException) { physicalRefused = true; }
                catch (InvalidOperationException) { }
                Report("Binary to Text physical readonly save follows Text refusal", physicalRefused && File.ReadAllBytes(left).SequenceEqual(original));
            }
            finally { File.SetAttributes(left, attributes); }
            var output = Path.Combine(folder, "transition-external.txt"); File.WriteAllText(output, "preserve external\n"); var textPicker = false;
            transition.TextSavePathPicker = _ => { textPicker = true; return Task.FromResult<string?>(output); };
            transition.BinarySavePathPicker = _ => Task.FromResult<string?>(Path.Combine(folder, "transition-wrong.bin"));
            Report("Binary to Text external save selects Text picker and bytes", !Refused(() => pump(transition.SaveTextAsAsync(false))) && textPicker
                && File.ReadAllText(output) == "readonly transition\n" && transition.CaptureProject().LeftPath == output);
            ApplySettings(transition, true);
            Report("Binary to Text settings readonly applies to current Text", transition.LeftEditor.IsReadOnly && Refused(() => pump(transition.SaveAsync(false)))
                && File.ReadAllText(output) == "readonly transition\n");
            var typed = window.AddSession(); var source = Input(0) with { RootPath = isolatedRoot, LeafEntry = "ascii.txt" };
            typed.ApplyProject(new() { Mode = "Binary", LeftArchiveInput = source, RightArchiveInput = source.Copy(), LeftReadOnly = true, RightReadOnly = true }); pump(typed.ComparePathsAsync());
            var typedOld = Binary(typed); typed.SelectMode(1); pump(typed.ComparePathsAsync());
            Report("typed Binary to Text adopts Text and releases prior owner", typedOld.IsDisposed && typed.CurrentDiff is not null && !typed.LeftEditor.IsReadOnly);
            pump(settingsText.SaveAsync(false));
            Report("Text sibling working save after Binary transition updates Text owner", typed.LeftEditor.Text == "settings dirty Text\n" && !typed.HasUnsavedChanges);
            var provider = window.AddSession(); var xml = Path.Combine(folder, "transition-provider.xml"); File.WriteAllText(xml, "<root a=\"1\"/>\n");
            provider.ApplyProject(new() { Mode = "Binary", LeftPath = xml, RightPath = xml, ProviderId = "xml" }); pump(provider.ComparePathsAsync());
            var providerOld = Binary(provider); provider.SelectMode(8);
            provider.ProviderResultReadyForAdoption = _ => providerOld.RightHex.Text = "AA"; pump(provider.ComparePathsAsync());
            Report("Binary edit during provider Text loading preserves previous owner", !providerOld.IsDisposed && providerOld.Pending(true) && providerOld.RightHex.Text == "AA");
            provider.ProviderResultReadyForAdoption = null; provider.DiscardChanges(); pump(provider.ComparePathsAsync());
            Report("Binary to provider Text releases owner and preserves transformed save refusal", providerOld.IsDisposed && provider.CurrentDiff is not null && provider.LeftEditor.IsReadOnly
                && Refused(() => pump(provider.SaveAsync(false))) && File.ReadAllText(xml) == "<root a=\"1\"/>\n");
        }
        void StoreBudget(int documents, bool byteLimit)
        {
            var budgetRoot = Path.Combine(folder, byteLimit ? "budget-bytes.zip" : "budget-documents.zip");
            File.WriteAllBytes(budgetRoot, Zip(Enumerable.Range(0, documents + 1).Select(index => ($"{index}.bin", Array.Empty<byte>())).ToArray()));
            var snapshotBytes = byteLimit ? new byte[16 * 1024 * 1024] : Array.Empty<byte>(); var snapshotSha = HashBytes(snapshotBytes);
            var budget = new MainWindow(null, new ImageApplicationOptionsStore(Path.Combine(folder, byteLimit ? "budget-bytes-options.json" : "budget-documents-options.json")));
            try
            {
                budget.Show(); var current = budget.AddSession();
                var source = new ArchiveProjectInput { RootPath = budgetRoot, RootSha256 = Hash(budgetRoot), InheritedReadOnly = false };
                source.WorkingDocuments = Enumerable.Range(0, documents).Select(index => new ArchiveWorkingSnapshot { LeafEntry = $"{index}.bin", Bytes = snapshotBytes, Sha256 = snapshotSha,
                    Kind = byteLimit || index % 2 == 0 ? "Binary" : null, EncodingName = byteLimit || index % 2 == 0 ? "" : "utf-8" }).ToArray();
                current.ApplyProject(new() { Mode = "Archive", LeftReadOnly = true, LeftArchiveInput = source });
                var generation = budget.ArchiveTexts.Generation; var candidate = budget.AddSession(); candidate.LeftEditor.Text = "preserve candidate";
                var extra = "X"u8.ToArray(); var attempted = source.Copy() with { LeafEntry = $"{documents}.bin", WorkingDocuments = [new() { LeafEntry = $"{documents}.bin", Bytes = extra, Sha256 = HashBytes(extra), EncodingName = "utf-8" }] };
                Report(byteLimit ? "shared 128 MiB Binary store rejects additional Text atomically" : "shared 256 document mixed store rejects new entry atomically",
                    Refused(() => candidate.ApplyProject(new() { Mode = "Text", LeftReadOnly = true, LeftArchiveInput = attempted }))
                    && budget.ArchiveTexts.Generation == generation && candidate.LeftEditor.Text == "preserve candidate" && candidate.CaptureProject().LeftArchiveInput is null
                    && current.CaptureProject().LeftArchiveInput!.WorkingDocuments!.Length == documents);
            }
            finally { foreach (var session in budget.SessionPanes) session.DiscardChanges(); budget.Close(); }
        }

        ArchiveProjectInput Input(int side) => new() { RootPath = roots[side], RootSha256 = hashes[side], EntryChain = ["inner.zip"], LeafEntry = "leaf.bin", InheritedReadOnly = false };
        void Report(string name, bool passed) { try { check(name, passed, "actual pane controls and save path"); } catch (InvalidOperationException) { } }
        static SpecializedViews.BinaryPanel Binary(ComparisonPane value) => value.GetVisualDescendants().OfType<SpecializedViews.BinaryPanel>().Single();
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        bool Refused(Action action) { try { action(); return false; } catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException or ArgumentException or OperationCanceledException or UnauthorizedAccessException) { refusals.Add(exception.GetType().Name + ": " + exception.Message); return true; } }
    }
    internal static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var memory = new MemoryStream(); using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var (name, bytes) in entries) { using var entry = zip.CreateEntry(name).Open(); entry.Write(bytes); }
        return memory.ToArray();
    }
}
