using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessArchiveWorkingTextChecks
{
    internal static void Run(MainWindow window, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        // 原本上書き、別tab競合、世代/取消/readonly、文字コード再推測、snapshot公開失敗を先に列挙した契約の実経路。
        var folder = Path.Combine(output, "archive-working-text"); Directory.CreateDirectory(folder);
        const string originalLeft = "header\r\nleft café\r\ntail\n", originalRight = "header\r\nright 日本\r\ntail\n";
        const string savedLeft = "header\r\nleft ASCII\r\ntail\n", savedRight = "header\r\nsaved 日本\r\ntail\n", latestRight = "header\r\nlatest 日本\r\ntail\n";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp1252 = Encoding.GetEncoding(1252); var utf8 = new UTF8Encoding(true);
        var leftBytes = cp1252.GetBytes(originalLeft); var rightBytes = utf8.GetPreamble().Concat(utf8.GetBytes(originalRight)).ToArray();
        var roots = new[] { Path.Combine(folder, "left.zip"), Path.Combine(folder, "right.zip") };
        File.WriteAllBytes(roots[0], Zip(("inner.zip", Zip(("leaf.txt", leftBytes), ("empty.txt", []))), ("other.txt", "other"u8.ToArray())));
        File.WriteAllBytes(roots[1], Zip(("inner.zip", Zip(("leaf.txt", rightBytes), ("empty.txt", []))), ("other.txt", "other"u8.ToArray())));
        var hashes = roots.Select(Hash).ToArray();
        var parent = window.AddSession(); parent.ApplyProject(new() { LeftPath = roots[0], RightPath = roots[1], Mode = "Archive" }); pump(parent.ComparePathsAsync());
        var rootPanel = Panel(parent); Open(rootPanel, "inner.zip"); var inner = window.ActivePane; var innerPanel = Panel(inner);
        Open(innerPanel, "leaf.txt"); var leaf = window.ActivePane;
        Verify("present editors inherit independent writable intent", !leaf.LeftEditor.IsReadOnly && !leaf.RightEditor.IsReadOnly && leaf.LeftEditor.Text == originalLeft && leaf.RightEditor.Text == originalRight);
        var captions = leaf.GetVisualDescendants().OfType<TextBlock>().Where(label => label.Text?.StartsWith("leaf.txt", StringComparison.Ordinal) == true).ToArray();
        Verify("writable present diff captions agree with editor controls", captions.Length >= 2 && captions.All(label => !label.Text!.Contains("読取り専用", StringComparison.Ordinal)));
        leaf.NavigateDifference(1); leaf.CopyRightButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Verify("actual difference copy edits present side without detaching", leaf.RightEditor.Text == originalLeft && leaf.CaptureProject().RightArchiveInput is not null && leaf.HasUnsavedChanges);
        leaf.LeftEditor.Text = savedLeft; leaf.RightEditor.Text = savedRight;
        var pendingHtml = Path.Combine(folder, "pending.html"); pump(leaf.SaveReportAsync(pendingHtml));
        var protectedProject = Path.Combine(folder, "pending-project.json"); File.WriteAllText(protectedProject, "keep project");
        var protectedPackage = Path.Combine(folder, "pending-package.zip"); File.WriteAllText(protectedPackage, "keep package");
        Verify("typed unsaved text rejects project and package", Refused(() => pump(window.SaveWorkspaceAsync(protectedProject))) && File.ReadAllText(protectedProject) == "keep project"
            && Refused(() => pump(window.PackageWorkspaceAsync(protectedPackage))) && File.ReadAllText(protectedPackage) == "keep package");
        leaf.TextSaveBeforePublish = () => { leaf.RightEditor.Text = latestRight; return Task.CompletedTask; };
        pump(leaf.SaveAsync(true)); leaf.TextSaveBeforePublish = null;
        Verify("ordinary save commits captured bytes and retains later dirty edit", leaf.RightEditor.Text == latestRight && leaf.HasUnsavedChanges && leaf.CaptureProject().RightArchiveInput?.WorkingTexts is [{ HasBom: true, EncodingName: "utf-8" }]);
        pump(leaf.SaveAsync(true)); pump(leaf.SaveAsync(false));
        Verify("ordinary save retains typed source and independent encoding", !leaf.HasUnsavedChanges && leaf.CaptureProject().LeftArchiveInput?.WorkingTexts is [{ HasBom: false, EncodingName: "windows-1252" }]
            && roots.Select(Hash).SequenceEqual(hashes));
        pump(leaf.ComparePathsAsync());
        Verify("recompare reads saved working bytes and known legacy encoding", leaf.LeftEditor.Text == savedLeft && leaf.RightEditor.Text == latestRight
            && leaf.CaptureProject().LeftArchiveInput?.WorkingTexts![0].EncodingName == "windows-1252");
        pump(innerPanel.PreviewAsync(innerPanel.Rows.Single(row => row.Path == "leaf.txt")));
        var previewBytes = innerPanel.PreviewText.Split('\n').Where(line => line.StartsWith("0000", StringComparison.Ordinal))
            .SelectMany(line => line[10..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => Convert.ToByte(value, 16))).ToArray();
        Verify("parent preview reads saved working bytes", previewBytes.SequenceEqual(cp1252.GetBytes(savedLeft).Concat(utf8.GetPreamble()).Concat(utf8.GetBytes(latestRight))));
        Verify("ancestor project captures descendant working snapshots", parent.CaptureProject().LeftArchiveInput?.WorkingTexts is { Length: 1 }
            && inner.CaptureProject().LeftArchiveInput?.WorkingTexts is { Length: 1 });
        Activate(inner); Open(innerPanel, "leaf.txt"); var sibling = window.ActivePane;
        sibling.RightEditor.Text = "sibling dirty\n"; leaf.RightEditor.Text = "new saved 日本\n"; pump(leaf.SaveAsync(true));
        Verify("same entry dirty sibling is retained and stale save refuses", sibling.RightEditor.Text == "sibling dirty\n" && sibling.HasUnsavedChanges
            && Refused(() => pump(sibling.SaveAsync(true))) && leaf.RightEditor.Text == "new saved 日本\n");
        sibling.DiscardChanges(); pump(sibling.ComparePathsAsync());
        leaf.RightEditor.Text = latestRight; pump(leaf.SaveAsync(true));
        Verify("same entry clean sibling adopts current save point", sibling.RightEditor.Text == latestRight && !sibling.HasUnsavedChanges);
        leaf.RightEditor.Text = savedRight;
        using (var cancel = new CancellationTokenSource())
        {
            leaf.TextSaveBeforePublish = () => { cancel.Cancel(); return Task.CompletedTask; };
            Verify("ordinary cancellation retains dirty text and saved bytes", Canceled(() => pump(leaf.SaveWorkingTextAsync(true, cancel.Token)))
                && leaf.RightEditor.Text == savedRight && leaf.HasUnsavedChanges && sibling.RightEditor.Text == latestRight);
            leaf.TextSaveBeforePublish = null;
        }
        leaf.TextSaveReadyForAdoption = () => throw new IOException("injected work adoption failure");
        Verify("working adoption failure keeps prior saved revision", Refused(() => pump(leaf.SaveAsync(true))) && sibling.RightEditor.Text == latestRight && leaf.HasUnsavedChanges);
        leaf.TextSaveReadyForAdoption = null;
        var priorProject = leaf.CaptureProject(); var priorGeneration = window.ArchiveTexts.Generation;
        var conflicted = inner.CaptureProject();
        var existing = conflicted.LeftArchiveInput!.WorkingTexts![0];
        var extraBytes = "new empty leaf work\n"u8.ToArray();
        conflicted.LeftArchiveInput.WorkingTexts = [new() { EntryChain = ["inner.zip"], LeafEntry = "empty.txt", Bytes = extraBytes,
            Sha256 = Convert.ToHexString(SHA256.HashData(extraBytes)), EncodingName = "utf-8" }, existing with
            { Bytes = "conflict\n"u8.ToArray(), Sha256 = Convert.ToHexString(SHA256.HashData("conflict\n"u8)) }];
        Verify("two entry import failure leaves shared revision and pane unchanged", Refused(() => inner.ApplyProject(conflicted))
            && window.ArchiveTexts.Generation == priorGeneration && inner.CaptureProject().LeftArchiveInput?.WorkingTexts is { Length: 1 }
            && leaf.RightEditor.Text == savedRight);
        leaf.TextSaveBeforePublish = () =>
        {
            var changed = leaf.CaptureProject(); changed.RightArchiveInput!.InheritedReadOnly = true; leaf.ApplyProject(changed); return Task.CompletedTask;
        };
        Verify("project readonly change rejects old working completion", Canceled(() => pump(leaf.SaveAsync(true))) && sibling.RightEditor.Text == latestRight && leaf.RightEditor.Text == savedRight);
        leaf.TextSaveBeforePublish = null; leaf.ApplyProject(priorProject); leaf.DiscardChanges(); pump(leaf.ComparePathsAsync()); leaf.RightEditor.Text = savedRight;
        leaf.TextSaveBeforePublish = () => { File.SetAttributes(roots[1], File.GetAttributes(roots[1]) | FileAttributes.ReadOnly); return Task.CompletedTask; };
        try { Verify("physical readonly change at commit retains saved state", Refused(() => pump(leaf.SaveAsync(true))) && sibling.RightEditor.Text == latestRight && leaf.RightEditor.Text == savedRight); }
        finally { leaf.TextSaveBeforePublish = null; File.SetAttributes(roots[1], File.GetAttributes(roots[1]) & ~FileAttributes.ReadOnly); }
        leaf.RightEditor.Text = latestRight; leaf.DiscardChanges();
        leaf.ArchiveSourceReadyForAdoption = () => leaf.RightEditor.Text = "edit during recompare\n";
        pump(leaf.CompareArchiveProjectAsync()); leaf.ArchiveSourceReadyForAdoption = null;
        Verify("new editor text rejects old comparison adoption", leaf.RightEditor.Text == "edit during recompare\n" && leaf.HasUnsavedChanges);
        leaf.DiscardChanges(); pump(leaf.ComparePathsAsync());
        var mixed = leaf.CaptureProject(); mixed.LeftArchiveInput!.InheritedReadOnly = true; leaf.ApplyProject(mixed); pump(leaf.ComparePathsAsync());
        Verify("present side readonly remains independent", leaf.LeftEditor.IsReadOnly && !leaf.RightEditor.IsReadOnly && Refused(() => pump(leaf.SaveAsync(false))));
        mixed.LeftArchiveInput!.InheritedReadOnly = false; leaf.ApplyProject(mixed); pump(leaf.ComparePathsAsync());
        var workspace = Path.Combine(folder, "working.json"); pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new() { Entries = [leaf.CaptureProject()] }));
        var assets = Directory.GetFiles(workspace + ".assets"); var assetHashes = assets.Select(Hash).ToArray();
        pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new() { Entries = [leaf.CaptureProject()] }));
        Verify("workspace references durable immutable snapshots and reuses bytes", assets.Length == 2 && Directory.GetFiles(workspace + ".assets").Length == 2 && assets.Select(Hash).SequenceEqual(assetHashes));
        var restored = Load(workspace);
        var restoredPane = window.AddSession(); restoredPane.ApplyProject(restored); pump(restoredPane.ComparePathsAsync());
        Verify("relative snapshot restoration retains all bytes and encoding", restoredPane.LeftEditor.Text == savedLeft && restoredPane.RightEditor.Text == latestRight && !restoredPane.HasUnsavedChanges);
        var html = Path.Combine(folder, "saved.html"); Activate(leaf); pump(leaf.SaveReportAsync(html));
        var package = Path.Combine(folder, "working-package.zip"); pump(ComparisonPackage.CreateAsync(new() { Entries = [leaf.CaptureProject()] }, package, new(true, true, true, true)));
        var ancestorWorkspace = Path.Combine(folder, "ancestor.json"); pump(WorkspaceStore.SaveWorkspaceAsync(ancestorWorkspace, new() { Entries = [parent.CaptureProject()] }));
        Verify("final saved diff captions agree with writable editor after layout", leaf.ArchiveDiffCaptions == ("leaf.txt", "leaf.txt") && !leaf.LeftEditor.IsReadOnly && !leaf.RightEditor.IsReadOnly);
        screenshot("archive-working-text-saved.png");
        Close(inner); Close(parent);
        Verify("child saved state survives closing every ancestor", leaf.LeftEditor.Text == savedLeft && leaf.RightEditor.Text == latestRight && !leaf.HasUnsavedChanges);
        var external = Path.Combine(folder, "external-right.txt");
        leaf.TextSavePathPicker = _ => Task.FromResult<string?>(external);
        var saveButton = leaf.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "右を外部保存"));
        saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump(Wait(() => leaf.CaptureProject().RightArchiveInput is null)); leaf.TextSavePathPicker = null;
        Verify("actual external save button detaches only successful side", leaf.CaptureProject().RightArchiveInput is null && leaf.CaptureProject().RightPath == external
            && leaf.CaptureProject().LeftArchiveInput is not null && File.ReadAllBytes(external).SequenceEqual(utf8.GetPreamble().Concat(utf8.GetBytes(latestRight))) && !leaf.RightEditor.IsReadOnly);
        foreach (var root in roots) Verify("detached source remains protected " + Path.GetFileName(root), Refused(() => pump(leaf.SaveTextToAsync(true, root))) && Hash(root) == hashes[Array.IndexOf(roots, root)]);
        // 合法な4MiB超本文をmetadata上限に巻き込まず、CLI包装でも同じ全bytesを再読込みできる。
        var largeText = new string('x', 4 * 1024 * 1024 + 257) + "\r\n";
        restoredPane.RightEditor.Text = largeText; pump(restoredPane.SaveAsync(true));
        var largeWorkspace = Path.Combine(folder, "large-working.json"); pump(WorkspaceStore.SaveWorkspaceAsync(largeWorkspace, new() { Entries = [restoredPane.CaptureProject()] }));
        var largeLoaded = Load(largeWorkspace);
        Verify("4MiB plus saved text restores through small metadata", new FileInfo(largeWorkspace).Length < WorkspaceStore.MaxFileBytes
            && largeLoaded.RightArchiveInput!.WorkingTexts![0].Document().Text == largeText);
        var largePackage = Path.Combine(folder, "large-working-package.zip"); pump(ComparisonPackage.CreateAsync(new() { Entries = [largeLoaded] }, largePackage, new(true, false, false, true)));
        var retainedProject = Path.Combine(folder, "failed-working.json"); File.WriteAllText(retainedProject, "keep failed project");
        File.SetAttributes(retainedProject, File.GetAttributes(retainedProject) | FileAttributes.ReadOnly);
        try { Verify("workspace publication failure preserves existing project", Refused(() => pump(WorkspaceStore.SaveWorkspaceAsync(retainedProject, new() { Entries = [largeLoaded] }))) && File.ReadAllText(retainedProject) == "keep failed project"); }
        finally { File.SetAttributes(retainedProject, File.GetAttributes(retainedProject) & ~FileAttributes.ReadOnly); }
        var anotherRoot = Path.Combine(folder, "other-identical-left.zip"); File.WriteAllBytes(anotherRoot, File.ReadAllBytes(roots[0]));
        var distinct = leaf.CaptureProject(); distinct.LeftArchiveInput!.RootPath = anotherRoot; distinct.LeftArchiveInput.WorkingTexts = null;
        var other = window.AddSession(); other.ApplyProject(distinct); pump(other.ComparePathsAsync());
        Verify("identical hash under another root keeps independent original", other.LeftEditor.Text == originalLeft);
        using (var proofFile = File.Create(Path.Combine(folder, "proof.json")))
        using (var writer = new Utf8JsonWriter(proofFile, new() { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var (name, values) in new[] { ("roots", roots), ("hashes", hashes) })
            { writer.WriteStartArray(name); foreach (var value in values) writer.WriteStringValue(value); writer.WriteEndArray(); }
            foreach (var (name, value) in new Dictionary<string, string> { ["originalLeft"] = originalLeft, ["originalRight"] = originalRight,
                ["savedLeft"] = savedLeft, ["savedRight"] = savedRight, ["latestRight"] = latestRight, ["workspace"] = workspace, ["html"] = html,
                ["pendingHtml"] = pendingHtml, ["package"] = package, ["external"] = external, ["ancestorWorkspace"] = ancestorWorkspace,
                ["largeWorkspace"] = largeWorkspace, ["largePackage"] = largePackage, ["anotherRoot"] = anotherRoot }) writer.WriteString(name, value);
            writer.WriteNumber("largeTextLength", largeText.Length); writer.WriteEndObject();
        }
        Close(other); Close(restoredPane); Close(sibling); Close(leaf);

        void Verify(string name, bool passed) => check("archive working Text " + name, passed, "");
        ComparisonProject Load(string path)
        {
            ComparisonProject? result = null;
            async Task Read() { result = await WorkspaceStore.LoadAsync(path); }
            pump(Read()); return result!;
        }
        void Activate(ComparisonPane pane) { window.SelectSession(window.SessionPanes.ToList().IndexOf(pane)); Dispatcher.UIThread.RunJobs(); }
        void Open(ArchivePanel panel, string name) { panel.EntryList.SelectedItem = panel.Rows.Single(row => row.Path == name); Dispatcher.UIThread.RunJobs(); pump(panel.OpenSelectedAsync()); Dispatcher.UIThread.RunJobs(); }
        void Close(ComparisonPane pane)
        {
            pane.DiscardChanges(); var tab = window.GetVisualDescendants().OfType<TabControl>().SelectMany(control => control.Items.OfType<TabItem>()).Single(item => ReferenceEquals(item.Content, pane));
            ((StackPanel)tab.Header!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        }
    }
    private static async Task Wait(Func<bool> ready) { for (var i = 0; i < 1000; i++) { if (ready()) return; await Task.Delay(5); } throw new TimeoutException("保存ボタンの採用待ちが終了しません。"); }
    private static ArchivePanel Panel(ComparisonPane pane) => pane.GetVisualDescendants().OfType<ArchivePanel>().Single();
    private static bool Refused(Action action) { try { action(); return false; } catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException) { return true; } }
    private static bool Canceled(Action action) { try { action(); return false; } catch (OperationCanceledException) { return true; } }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var buffer = new MemoryStream(); using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var (name, bytes) in entries) { var entry = archive.CreateEntry(name, CompressionLevel.NoCompression); using var stream = entry.Open(); stream.Write(bytes); }
        return buffer.ToArray();
    }
}
