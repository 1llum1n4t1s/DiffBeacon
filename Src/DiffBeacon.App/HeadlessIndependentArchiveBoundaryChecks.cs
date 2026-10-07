using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessIndependentArchiveBoundaryChecks
{
    private sealed record State(object Stamp, TextDocument[] Documents, string[] Bodies, bool[] Dirty,
        long[] TextRevisions, long Generation, long[] StoreRevisions, ArchiveWorkingSnapshot?[] Snapshots, string?[] SnapshotHashes);

    internal static void Run(MainWindow window, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "independent-archive-boundaries");
        Directory.CreateDirectory(folder);
        var fixture = Path.GetFullPath("tests/Fixtures/IndependentArchiveText");
        foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(folder, "fixture", Path.GetRelativePath(fixture, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        string[] edits = ["boundary left café\r\nleft €", "boundary middle £\r\ncentral", "boundary right “quote”\r\nright"];
        var number = 0;
        using var proof = File.Create(Path.Combine(folder, "cases.json"));
        using var writer = new Utf8JsonWriter(proof, new() { Indented = true });
        writer.WriteStartObject(); writer.WriteString("schema", "independent-archive-boundaries-1"); writer.WriteStartArray("cases");

        MainWindow Isolated(string name)
        {
            var host = new MainWindow(null, new ImageApplicationOptionsStore(Path.Combine(folder, name + "-options.json")));
            host.Show(); Dispatcher.UIThread.RunJobs(); return host;
        }
        ComparisonProject Load()
        {
            ComparisonProject? project = null;
            async Task Read() { project = await WorkspaceStore.LoadAsync(Path.Combine(folder, "fixture", "workspace-original.json")); }
            pump(Read());
            // 全caseのroot identityを分け、共有保存版を次の初期状態へ流入させない。
            var id = ++number;
            for (var side = 0; side < 3; side++)
            {
                var input = ProjectInputs.Archive(project!, side)!;
                var root = Path.Combine(folder, $"case-{id}-root-{side}.zip");
                File.Copy(input.RootPath, root); input.RootPath = root;
            }
            return project!;
        }
        ComparisonPane Open(MainWindow host, ComparisonProject? project = null)
        {
            var pane = host.AddSession(); pane.ApplyProject(project ?? Load()); pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs(); return pane;
        }
        void Edit(ComparisonPane pane)
        { for (var side = 0; side < 3; side++) pane.TextEditor(side).Text = edits[side]; }
        State Capture(MainWindow host, ComparisonPane pane)
        {
            var project = pane.CaptureProject();
            var inputs = Enumerable.Range(0, 3).Select(side => ProjectInputs.Archive(project, side)).ToArray();
            var snapshots = inputs.Select(input => input?.LeafEntry is { } leaf ? host.ArchiveTexts.Find(input.ToSource(), leaf) : null).ToArray();
            return new(pane.CaptureIndependentTextState(), pane.CaptureIndependentTextDocuments(),
                Enumerable.Range(0, 3).Select(side => pane.TextEditor(side).Text ?? "").ToArray(),
                Enumerable.Range(0, 3).Select(pane.TextDirty).ToArray(), pane.CaptureIndependentTextRevisions(), host.ArchiveTexts.Generation,
                inputs.Select(input => input is null ? 0 : host.ArchiveTexts.Revision(input)).ToArray(),
                snapshots, snapshots.Select(snapshot => snapshot?.Bytes is { } bytes ? Convert.ToHexString(SHA256.HashData(bytes)) : null).ToArray());
        }
        bool Same(State before, State after) => Equals(before.Stamp, after.Stamp)
            && before.Documents.Select((document, side) => ReferenceEquals(document, after.Documents[side])).All(value => value)
            && before.Bodies.SequenceEqual(after.Bodies) && before.Dirty.SequenceEqual(after.Dirty)
            && before.TextRevisions.SequenceEqual(after.TextRevisions) && before.Generation == after.Generation
            && before.StoreRevisions.SequenceEqual(after.StoreRevisions) && before.SnapshotHashes.SequenceEqual(after.SnapshotHashes)
            && before.Snapshots.Select((snapshot, side) => ReferenceEquals(snapshot, after.Snapshots[side])).All(value => value);
        Exception? Attempt(Func<Task> action)
        {
            try { pump(action()); return null; }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException) { return error; }
        }
        void Result(string name, int side, State before, State after, Exception? error, bool expected, bool hook,
            string? target = null, byte[]? initial = null, byte[]? final = null, string? sentinel = null)
        {
            check("Independent Archive boundary " + name, expected, error?.Message ?? "");
            writer.WriteStartObject(); writer.WriteString("name", name); writer.WriteNumber("side", side);
            writer.WriteBoolean("passed", expected); writer.WriteBoolean("hookInvoked", hook);
            writer.WriteBoolean("stateRetained", Same(before, after)); writer.WriteString("exception", error?.GetType().Name);
            writer.WriteBoolean("documentsRetained", before.Documents.Select((document, index) => ReferenceEquals(document, after.Documents[index])).All(value => value));
            writer.WriteBoolean("snapshotReferencesRetained", before.Snapshots.Select((snapshot, index) => ReferenceEquals(snapshot, after.Snapshots[index])).All(value => value));
            writer.WriteString("reason", error?.Message); writer.WriteString("target", target); writer.WriteString("sentinel", sentinel);
            writer.WriteString("initialBase64", initial is null ? null : Convert.ToBase64String(initial));
            writer.WriteString("finalBase64", final is null ? null : Convert.ToBase64String(final));
            WriteState("before", before); WriteState("after", after); writer.WriteEndObject(); writer.Flush();
        }
        void WriteState(string name, State state)
        {
            writer.WriteStartObject(name); writer.WriteNumber("generation", state.Generation); writer.WriteStartArray("sides");
            for (var side = 0; side < 3; side++)
            {
                var text = state.Bodies[side]; var document = state.Documents[side];
                writer.WriteStartObject(); writer.WriteNumber("side", side); writer.WriteNumber("length", text.Length);
                writer.WriteString("body", text.Length <= 4096 ? text : null);
                writer.WriteString("bodyUtf8Sha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
                writer.WriteBoolean("dirty", state.Dirty[side]); writer.WriteNumber("textRevision", state.TextRevisions[side]);
                writer.WriteNumber("storeRevision", state.StoreRevisions[side]); writer.WriteString("encodingName", document.EncodingName);
                writer.WriteBoolean("hasBom", document.HasBom); writer.WriteString("documentPath", document.Path);
                writer.WriteString("bodyBytesBase64", text.Length <= 4096 ? Convert.ToBase64String(document.CaptureBytes(text)) : null);
                var snapshot = state.Snapshots[side]; writer.WriteString("snapshotSha256", state.SnapshotHashes[side]);
                writer.WriteNumber("snapshotLength", snapshot?.Bytes?.Length ?? 0);
                writer.WriteString("snapshotBase64", snapshot?.Bytes is { Length: <= 4096 } bytes ? Convert.ToBase64String(bytes) : null);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        void Finish(MainWindow host)
        { foreach (var pane in host.SessionPanes) pane.DiscardChanges(); host.Close(); Dispatcher.UIThread.RunJobs(); }
        void Activate(MainWindow host, ComparisonPane pane)
        { host.SelectSession(host.SessionPanes.ToList().IndexOf(pane)); Dispatcher.UIThread.RunJobs(); }
        void ClosePane(MainWindow host, ComparisonPane pane)
        {
            var tab = host.GetVisualDescendants().OfType<TabControl>().SelectMany(control => control.Items.OfType<TabItem>())
                .Single(item => ReferenceEquals(item.Content, pane));
            ((StackPanel)tab.Header!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        }

        foreach (var external in new[] { false, true })
        {
            var host = Isolated(external ? "tab-external" : "tab-working");
            try
            {
                var pane = Open(host); pump(pane.SaveWorkingTextAsync(1)); Edit(pane);
                var other = host.AddSession(); Activate(host, pane); var before = Capture(host, pane); var hook = false;
                var target = Path.Combine(folder, "tab-external-sentinel.txt");
                var sentinel = "retain tab-move sentinel\r\n"u8.ToArray(); if (external) File.WriteAllBytes(target, sentinel);
                pane.TextSaveBeforePublish = () => { hook = true; Activate(host, other); return Task.CompletedTask; };
                var error = Attempt(() => external ? pane.SaveTextToAsync(1, target) : pane.SaveWorkingTextAsync(1)); pane.TextSaveBeforePublish = null;
                var after = Capture(host, pane); var final = external ? File.ReadAllBytes(target) : null;
                Result(external ? "tab-move-external" : "tab-move-working", 1, before, after, error,
                    hook && error is OperationCanceledException && error.Message == (external ? "保存元の比較または権限が変更されました。" : "保存元の比較が変更されました。")
                    && Same(before, after) && (!external || sentinel.SequenceEqual(final!)), hook,
                    external ? target : null, external ? sentinel : null, final);
            }
            finally { Finish(host); }
        }

        // 実一覧から子を採用した後は親との所有関係を持たず、非active親の終了は保存を取消しない。
        foreach (var external in new[] { false, true })
        {
            var host = Isolated(external ? "parent-external" : "parent-working");
            try
            {
                var project = Load();
                var root = host.AddSession(); root.ApplyProject(new() { Mode = "Archive", LeftPath = project.BaseArchiveInput!.RootPath, RightPath = project.RightArchiveInput!.RootPath });
                pump(root.ComparePathsAsync());
                void OpenEntry(ComparisonPane parent, string path)
                {
                    var panel = parent.GetVisualDescendants().OfType<ArchivePanel>().Single();
                    panel.EntryList.SelectedItem = panel.Rows.Single(row => row.Path == path); Dispatcher.UIThread.RunJobs(); pump(panel.OpenSelectedAsync());
                }
                OpenEntry(root, "outer.zip"); var outer = host.ActivePane; OpenEntry(outer, "inner.zip"); var parent = host.ActivePane;
                OpenEntry(parent, "texts/leaf.txt"); var child = host.ActivePane;
                var actualChild = child.CaptureProject();
                project.BaseArchiveInput = actualChild.LeftArchiveInput!.Copy(); project.RightArchiveInput = actualChild.RightArchiveInput!.Copy();
                child.ApplyProject(project); pump(child.ComparePathsAsync()); Edit(child);
                var before = Capture(host, child); var hook = false; var activeRetained = false;
                var target = Path.Combine(folder, "parent-external.txt"); var initial = "parent-external sentinel"u8.ToArray();
                if (external) File.WriteAllBytes(target, initial);
                child.TextSaveBeforePublish = () => { hook = true; ClosePane(host, parent); activeRetained = ReferenceEquals(host.ActivePane, child); return Task.CompletedTask; };
                var error = Attempt(() => external ? child.SaveTextToAsync(1, target) : child.SaveWorkingTextAsync(1)); child.TextSaveBeforePublish = null;
                var after = Capture(host, child); var final = external ? File.ReadAllBytes(target) : null;
                var preservedOtherSides = new[] { 0, 2 }.All(side => before.Bodies[side] == after.Bodies[side]
                    && ReferenceEquals(before.Documents[side], after.Documents[side]) && before.Dirty[side] == after.Dirty[side]);
                var success = hook && activeRetained && error is null && parent.IsDisposed && !child.IsDisposed && host.SessionPanes.Contains(child)
                    && !after.Dirty[1] && before.Bodies.SequenceEqual(after.Bodies) && preservedOtherSides
                    && (external ? final!.SequenceEqual(before.Documents[1].CaptureBytes(edits[1])) && child.CaptureProject().BaseArchiveInput is null
                        : after.Generation == before.Generation + 1 && after.StoreRevisions[1] > before.StoreRevisions[1]);
                Result(external ? "parent-close-external-success" : "parent-close-working-success", 1, before, after, error, success, hook,
                    external ? target : null, external ? initial : null, final);
            }
            finally { Finish(host); }
        }

        foreach (var budget in new[] { "bytes", "documents", "text-file" })
        {
            var host = Isolated("capacity-" + budget);
            try
            {
                var project = Load();
                // UTF-8の実在leafを中央に使い、128MiBから正確に1byte増やす。
                project.BaseArchiveInput = project.LeftArchiveInput!.Copy();
                var pane = Open(host, project); pump(pane.SaveWorkingTextAsync(1));
                var input = pane.CaptureProject().BaseArchiveInput!;
                ArchiveWorkingSnapshot Snapshot(string leaf, byte[] bytes) => new() { LeafEntry = leaf, EntryChain = input.EntryChain,
                    Bytes = bytes, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), EncodingName = "utf-8" };
                if (budget == "bytes")
                {
                    var prior = host.ArchiveTexts.Find(input.ToSource(), input.LeafEntry!)!.Bytes!.Length;
                    byte[] first = new byte[64 * 1024 * 1024]; Array.Fill(first, (byte)'x');
                    byte[] second = new byte[64 * 1024 * 1024 - prior]; Array.Fill(second, (byte)'x');
                    host.ArchiveTexts.Import(input.Copy() with { WorkingDocuments = [Snapshot("budget-first.txt", first), Snapshot("budget-second.txt", second)] });
                }
                else if (budget == "documents")
                {
                    host.ArchiveTexts.Import(input.Copy() with { WorkingDocuments = Enumerable.Range(0, 255).Select(index => Snapshot($"budget-{index}.txt", [])).ToArray() });
                    // 別の実在leafを同じ中央paneへ読込み、新規257件目を実保存APIから追加する。
                    project = pane.CaptureProject(); project.BaseArchiveInput!.LeafEntry = "texts/sibling.txt"; project.BaseArchiveInput.WorkingDocuments = null;
                    pane.DiscardChanges(); pane.ApplyProject(project); pump(pane.ComparePathsAsync());
                }
                (string Leaf, int Length, string Sha)[] Inventory() => (host.ArchiveTexts.Capture(input.Copy() with { LeafEntry = null }).WorkingDocuments ?? [])
                    .OrderBy(copy => copy.LeafEntry, StringComparer.Ordinal).Select(copy => (copy.LeafEntry, copy.Bytes!.Length, Convert.ToHexString(SHA256.HashData(copy.Bytes!)))).ToArray();
                var inventoryBefore = Inventory(); var bytesAtStart = inventoryBefore.Sum(item => (long)item.Length); var documentsAtStart = inventoryBefore.Length;
                var exactLimit = budget == "bytes" ? bytesAtStart == ArchiveWorkingStore.MaximumBytes
                    : budget == "documents" ? documentsAtStart == ArchiveWorkingStore.MaximumDocuments : documentsAtStart == 1;
                // 64MiB超の本文は実paneの非表示editorへ設定し、拒否検証のために巨大な行を描画しない。
                pane.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = budget == "text-file" ? 0 : 1; Dispatcher.UIThread.RunJobs();
                var previous = pane.MiddleEditor.Text!;
                if (budget == "text-file") pane.MiddleEditor.Text = new string('x', 64 * 1024 * 1024 + 1);
                else { pane.MiddleEditor.Focus(); pane.MiddleEditor.CaretIndex = previous.Length; host.KeyTextInput("x"); Dispatcher.UIThread.RunJobs(); }
                var edited = pane.MiddleEditor.Text; var before = Capture(host, pane);
                var error = Attempt(() => pane.SaveWorkingTextAsync(1)); var after = Capture(host, pane); var inventoryAfter = Inventory();
                var history = true;
                if (budget != "text-file")
                {
                    pane.MiddleEditor.Focus(); host.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null); host.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null); Dispatcher.UIThread.RunJobs();
                    history = pane.MiddleEditor.Text == previous;
                    host.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, null); host.KeyRelease(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, null); Dispatcher.UIThread.RunJobs();
                    if (pane.MiddleEditor.Text != edited) { host.KeyPress(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Z, null); host.KeyRelease(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Z, null); Dispatcher.UIThread.RunJobs(); }
                    history &= pane.MiddleEditor.Text == edited && pane.TextDirty(1);
                }
                var capacityReason = budget == "text-file" ? "テキストファイルがサイズ上限を超えています。" : "作業文書は256件、保存済み本文の合計128 MiBまでです。";
                Result("capacity-" + budget, 1, before, after, error, exactLimit && error is InvalidDataException && error.Message == capacityReason
                    && Same(before, after) && history && inventoryBefore.SequenceEqual(inventoryAfter), false);
                writer.WriteStartObject(); writer.WriteString("name", "capacity-" + budget + "-setup"); writer.WriteNumber("bytesAtStart", bytesAtStart);
                writer.WriteNumber("documentsAtStart", documentsAtStart); writer.WriteNumber("requestedBodyBytes", budget == "text-file" ? 64 * 1024 * 1024 + 1 : Encoding.UTF8.GetByteCount(edited!));
                writer.WriteBoolean("undoRedoRetained", history); writer.WriteBoolean("undoRedoExecuted", budget != "text-file");
                foreach (var (key, inventory) in new[] { ("inventoryBefore", inventoryBefore), ("inventoryAfter", inventoryAfter) })
                {
                    writer.WriteStartArray(key); foreach (var item in inventory)
                    { writer.WriteStartObject(); writer.WriteString("leaf", item.Leaf); writer.WriteNumber("length", item.Length); writer.WriteString("sha256", item.Sha); writer.WriteEndObject(); }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject(); writer.Flush();
            }
            finally { Finish(host); }
        }

        for (var side = 0; side < 3; side++)
        foreach (var guard in new[] { "physical", "root", "workspace", "filter", "asset", "readonly", "link", "late-newtabinput", "late-readonly", "late-link" })
        {
            var name = $"guard-{side}-{guard}"; var host = Isolated(name);
            try
            {
                var sourceWorkspace = Path.Combine(folder, name + "-workspace.json"); pump(host.SaveWorkspaceAsync(sourceWorkspace));
                var pane = Open(host); pump(pane.SaveWorkingTextAsync(1));
                var target = Path.Combine(folder, name + ".txt"); var sentinelPath = target; var initial = Encoding.UTF8.GetBytes("retain " + name + "\r\n");
                if (guard == "workspace") target = sourceWorkspace;
                else if (guard == "root")
                {
                    target = Path.Combine(folder, name + "-protected-root.zip");
                    var protectedInput = pane.CaptureProject().BaseArchiveInput!.Copy();
                    File.Copy(protectedInput.RootPath, target); protectedInput.RootPath = target; protectedInput.WorkingDocuments = null;
                    var other = host.AddSession(); other.ApplyProject(new() { LeftArchiveInput = protectedInput, LeftReadOnly = true }); Activate(host, pane);
                }
                else if (guard is "link" or "late-link") sentinelPath = Path.Combine(folder, name + "-link-sentinel.txt");
                if (guard is "workspace" or "root") { initial = File.ReadAllBytes(target); sentinelPath = target; }
                else File.WriteAllBytes(sentinelPath, initial);
                if (guard == "late-link") { /* 出力は不在、リンク先だけsentinelを保持する。 */ }
                else if (guard == "link") File.CreateSymbolicLink(target, sentinelPath);
                if (guard is "physical" or "filter")
                {
                    var other = host.AddSession(); other.ApplyProject(guard == "physical" ? new() { LeftPath = target } : new() { FileFilterPath = target }); Activate(host, pane);
                }
                if (guard == "asset") host.ArchiveLifetime.RegisterAsset(target);
                if (guard == "readonly") File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
                Edit(pane); var before = Capture(host, pane); var hook = false;
                pane.TextSaveBeforePublish = () =>
                {
                    hook = true;
                    if (guard == "late-newtabinput") { var added = host.AddSession(); added.ApplyProject(new() { BasePath = target }); Activate(host, pane); }
                    else if (guard == "late-readonly") File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
                    else if (guard == "late-link") File.CreateSymbolicLink(target, sentinelPath);
                    return Task.CompletedTask;
                };
                var error = Attempt(() => pane.SaveTextToAsync(side, target)); pane.TextSaveBeforePublish = null;
                var after = Capture(host, pane); var final = File.ReadAllBytes(sentinelPath == target || guard is "workspace" or "root" ? target : sentinelPath);
                var guardReason = guard switch
                {
                    "readonly" or "late-readonly" => error is UnauthorizedAccessException && error.Message == "読み取り専用のファイルは保存できません。",
                    "link" or "late-link" => error is IOException && error.Message == "アーカイブ操作のパスにリンクを使用できません。",
                    "asset" => error is InvalidOperationException && error.Message is "比較入力・アーカイブ原本・フィルター・プロジェクトを上書きできません。" or "公開または読込み済みの作業snapshotを上書きできません。",
                    _ => error is InvalidOperationException && error.Message == "比較入力・アーカイブ原本・フィルター・プロジェクトを上書きできません。"
                };
                Result(name, side, before, after, error, guardReason && Same(before, after) && initial.SequenceEqual(final)
                    && (!guard.StartsWith("late-", StringComparison.Ordinal) || hook), hook, target, initial, final, sentinelPath);
                if (guard is "readonly" or "late-readonly") File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
            }
            finally { Finish(host); }
        }
        writer.WriteEndArray(); writer.WriteEndObject();
        // 出力保護検証ではOS clipboard/ネイティブ保存dialogを操作しない。
        _ = window; _ = screenshot;
    }
}
