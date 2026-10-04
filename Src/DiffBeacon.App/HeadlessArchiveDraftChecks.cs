using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessArchiveDraftChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string folder, string[] roots, string[] hashes,
        Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        void Verify(string name, bool result) => check("archive Draft GUI " + name, result, "same comparison side; real save controller");
        bool Refused(Action action) { try { action(); return false; } catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException) { return true; } }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var original = pane.CaptureProject(); var chain = original.RightArchiveInput!.MissingEntryChain!.ToArray();
        pane.NavigateDifference(1);
        pane.CopyRightButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Verify("diff copy edits same missing side", pane.RightEditor.Text == pane.LeftEditor.Text && pane.HasUnsavedChanges
            && pane.CaptureProject().RightArchiveInput?.MissingEntryChain!.SequenceEqual(chain) == true);
        const string draft = "draft 日本\r\nsecond\n";
        pane.RightEditor.Text = draft;
        pane.TextSavePathPicker = _ => Task.FromResult<string?>(null);
        pump(pane.SaveAsync(true));
        Verify("save picker cancellation retains draft and absence", pane.RightEditor.Text == draft && pane.HasUnsavedChanges
            && pane.CaptureProject().RightArchiveInput is not null);
        var workspace = Path.Combine(folder, "draft-workspace.json"); File.WriteAllText(workspace, "retain workspace"); var workspaceHash = Hash(workspace);
        Verify("workspace rejects unsaved draft without losing output", Refused(() => pump(window.SaveWorkspaceAsync(workspace))) && Hash(workspace) == workspaceHash);
        var package = Path.Combine(folder, "draft-package.zip"); File.WriteAllText(package, "retain package"); var packageHash = Hash(package);
        Verify("packaging rejects unsaved draft", Refused(() => pump(window.PackageWorkspaceAsync(package))) && Hash(package) == packageHash);
        var html = Path.Combine(folder, "draft-unsaved.html"); pump(pane.SaveReportAsync(html));
        var decodedHtml = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(html), "<[^>]+>", ""));
        Verify("unsaved HTML uses draft with explicit state", decodedHtml.Contains("未保存の編集", StringComparison.Ordinal)
            && decodedHtml.Contains("draft", StringComparison.Ordinal) && pane.HasUnsavedChanges);
        pane.CompareEditors();
        screenshot("archive-draft-unsaved.png");
        foreach (var root in roots)
            Verify("SaveAs protects root " + Path.GetFileName(root), Refused(() => pump(pane.SaveTextToAsync(true, root))));
        var readonlyOutput = Path.Combine(folder, "draft-readonly.txt"); File.WriteAllText(readonlyOutput, "keep readonly"); var readonlyHash = Hash(readonlyOutput);
        File.SetAttributes(readonlyOutput, File.GetAttributes(readonlyOutput) | FileAttributes.ReadOnly);
        try { Verify("readonly SaveAs preserves draft and output", Refused(() => pump(pane.SaveTextToAsync(true, readonlyOutput))) && Hash(readonlyOutput) == readonlyHash && pane.HasUnsavedChanges); }
        finally { File.SetAttributes(readonlyOutput, File.GetAttributes(readonlyOutput) & ~FileAttributes.ReadOnly); }
        Verify("missing output parent preserves absence", Refused(() => pump(pane.SaveTextToAsync(true, Path.Combine(folder, "no-draft-parent", "draft.txt"))))
            && pane.CaptureProject().RightArchiveInput is not null && pane.RightEditor.Text == draft);
        var nulOutput = Path.Combine(folder, "draft-nul.txt"); File.WriteAllText(nulOutput, "keep nul"); var nulHash = Hash(nulOutput);
        pane.RightEditor.Text = "bad\0text";
        Verify("NUL save refuses existing output", Refused(() => pump(pane.SaveTextToAsync(true, nulOutput))) && Hash(nulOutput) == nulHash);
        pane.RightEditor.Text = draft;
        var staleOutput = Path.Combine(folder, "draft-stale.txt"); File.WriteAllText(staleOutput, "keep stale"); var staleHash = Hash(staleOutput);
        pane.TextSaveBeforePublish = () => { pane.ApplyProject(pane.CaptureProject()); return Task.CompletedTask; };
        var staleCanceled = false;
        try { pump(pane.SaveTextToAsync(true, staleOutput)); } catch (OperationCanceledException) { staleCanceled = true; }
        pane.TextSaveBeforePublish = null;
        Verify("replaced comparison cancels old publication and adoption", staleCanceled && Hash(staleOutput) == staleHash
            && pane.CaptureProject().RightArchiveInput is not null && pane.RightPath.Text != staleOutput);
        pane.DiscardChanges(); pump(pane.ComparePathsAsync()); pane.RightEditor.Text = draft;
        var canceled = Path.Combine(folder, "draft-canceled.txt"); File.WriteAllText(canceled, "keep cancel"); var canceledHash = Hash(canceled);
        using (var cancellation = new CancellationTokenSource())
        {
            pane.TextSaveBeforePublish = () => { cancellation.Cancel(); return Task.CompletedTask; };
            bool stopped = false; try { pump(pane.SaveTextToAsync(true, canceled, cancellation.Token)); } catch (OperationCanceledException) { stopped = true; }
            Verify("cancel before publish preserves output and draft", stopped && Hash(canceled) == canceledHash
                && pane.CaptureProject().RightArchiveInput is not null && pane.RightEditor.Text == draft);
            pane.TextSaveBeforePublish = null;
        }
        var newlyOpened = Path.Combine(folder, "draft-new-input.txt"); File.WriteAllText(newlyOpened, "keep new input");
        var newInputHash = Hash(newlyOpened); ComparisonPane? newInputPane = null;
        pane.TextSaveBeforePublish = () =>
        {
            newInputPane = window.AddSession(); newInputPane.LeftPath.Text = newlyOpened;
            return Task.CompletedTask;
        };
        Verify("new tab input before publication preserves existing bytes", Refused(() => pump(pane.SaveTextToAsync(true, newlyOpened)))
            && Hash(newlyOpened) == newInputHash && pane.RightEditor.Text == draft && pane.HasUnsavedChanges);
        pane.TextSaveBeforePublish = null; Close(newInputPane!);
        pane.TextSaveBeforePublish = () =>
        {
            File.SetAttributes(readonlyOutput, File.GetAttributes(readonlyOutput) | FileAttributes.ReadOnly);
            return Task.CompletedTask;
        };
        try { Verify("readonly change before publication preserves existing bytes", Refused(() => pump(pane.SaveTextToAsync(true, readonlyOutput))) && Hash(readonlyOutput) == readonlyHash); }
        finally { pane.TextSaveBeforePublish = null; File.SetAttributes(readonlyOutput, File.GetAttributes(readonlyOutput) & ~FileAttributes.ReadOnly); }
        var adoptionOutput = Path.Combine(folder, "draft-adoption-failed.txt");
        pane.TextSaveReadyForAdoption = () => throw new IOException("injected adoption failure");
        Verify("post-publication adoption failure preserves draft identity", Refused(() => pump(pane.SaveTextToAsync(true, adoptionOutput)))
            && File.ReadAllText(adoptionOutput) == draft && pane.CaptureProject().RightArchiveInput is not null && pane.HasUnsavedChanges);
        pane.TextSaveReadyForAdoption = null;
        var saved = Path.Combine(folder, "draft-saved.txt");
        const string latest = "latest edit 日本\r\n";
        pane.TextSaveBeforePublish = () => { pane.RightEditor.Text = latest; return Task.CompletedTask; };
        pane.TextSavePathPicker = _ => Task.FromResult<string?>(saved);
        pump(pane.SaveAsync(true)); pane.TextSaveBeforePublish = null; pane.TextSavePathPicker = null;
        var adopted = pane.CaptureProject();
        Verify("SaveAs publishes captured bytes and detaches only saved side", File.ReadAllText(saved) == draft
            && adopted.RightArchiveInput is null && adopted.RightPath == saved && !adopted.RightReadOnly
            && adopted.LeftArchiveInput?.RootPath == original.LeftArchiveInput?.RootPath && adopted.LeftReadOnly);
        Verify("concurrent edit remains dirty and editable", pane.RightEditor.Text == latest && pane.HasUnsavedChanges && !pane.RightEditor.IsReadOnly);
        foreach (var root in roots)
            Verify("detached SaveAs still protects origin " + Path.GetFileName(root), Refused(() => pump(pane.SaveTextToAsync(true, root))) && Hash(root) == hashes[Array.IndexOf(roots, root)]);
        pane.RightPath.Text = "";
        pane.TextSavePathPicker = _ => Task.FromResult<string?>(roots[1]);
        Verify("detached picker protects original root after clearing path", Refused(() => pump(pane.SaveAsync(true))) && Hash(roots[1]) == hashes[1]);
        pane.RightPath.Text = saved; pane.TextSavePathPicker = null;
        pump(pane.SaveAsync(true));
        Verify("ordinary save uses newly adopted file and save point", File.ReadAllText(saved) == latest && !pane.HasUnsavedChanges);
        pane.CompareEditors();
        screenshot("archive-draft-saved.png");
        var partialRoot = Path.Combine(folder, "draft-partial-root.zip");
        byte[] emptyZip;
        using (var emptyStream = new MemoryStream())
        {
            using (new System.IO.Compression.ZipArchive(emptyStream, System.IO.Compression.ZipArchiveMode.Create, true)) { }
            emptyZip = emptyStream.ToArray();
        }
        using (var outer = System.IO.Compression.ZipFile.Open(partialRoot, System.IO.Compression.ZipArchiveMode.Create))
        { using var entry = outer.CreateEntry("one.zip").Open(); entry.Write(emptyZip); }
        var partialHash = Hash(partialRoot);
        var partial = window.AddSession(); var partialProject = WorkspaceStore.CloneProject(original);
        partialProject.RightArchiveInput!.RootPath = partialRoot;
        partialProject.RightArchiveInput.RootSha256 = partialHash;
        partialProject.RightArchiveInput.EntryChain = ["one.zip"];
        partialProject.RightArchiveInput.MissingEntryChain = ["two.zip", "leaf.txt"];
        partial.ApplyProject(partialProject); pump(partial.CompareArchiveProjectAsync());
        partial.RightEditor.Text = latest;
        var partialSaved = Path.Combine(folder, "draft-partial-saved.txt"); pump(partial.SaveTextToAsync(true, partialSaved));
        pump(partial.CompareArchiveProjectAsync());
        Verify("nested missing SaveAs recompare clears only saved password hierarchy", partial.RightEditor.Text == latest
            && partial.LeftEditor.Text == pane.LeftEditor.Text && !partial.RightEditor.IsReadOnly
            && partial.CaptureProject().RightArchiveInput is null && Hash(partialRoot) == partialHash);
        Close(partial);
        var legacy = window.AddSession(); var legacyProject = WorkspaceStore.CloneProject(original);
        legacyProject.RightArchiveInput!.InheritedReadOnly = null; legacy.ApplyProject(legacyProject); pump(legacy.ComparePathsAsync());
        Verify("legacy absent readonly intention stays conservative", legacy.RightEditor.IsReadOnly
            && Refused(() => pump(legacy.SaveTextToAsync(true, saved))) && File.ReadAllText(saved) == latest);
        Close(legacy);
        var empty = window.AddSession(); empty.ApplyProject(original); pump(empty.ComparePathsAsync());
        var emptySaved = Path.Combine(folder, "draft-empty-saved.txt"); pump(empty.SaveTextToAsync(true, emptySaved));
        Verify("saving untouched missing side creates actual zero-byte file", File.Exists(emptySaved) && new FileInfo(emptySaved).Length == 0
            && empty.CaptureProject().RightArchiveInput is null && empty.CaptureProject().RightPath == emptySaved && !empty.HasUnsavedChanges);
        Close(empty);
        pump(window.SaveWorkspaceAsync(workspace));
        using (var json = JsonDocument.Parse(File.ReadAllBytes(workspace)))
            Verify("workspace stores external file instead of old missing input", json.RootElement.GetProperty("entries").EnumerateArray()
                .Any(entry => entry.GetProperty("rightPath").GetString() == saved && (!entry.TryGetProperty("rightArchiveInput", out var input) || input.ValueKind == JsonValueKind.Null)));
        var restore = new MainWindow();
        try
        {
            pump(restore.OpenWorkspaceAsync(workspace, discardChanges: true)); pump(restore.ActivePane.ComparePathsAsync());
            Verify("restored mixed source uses saved external bytes", restore.ActivePane.RightEditor.Text == latest
                && restore.ActivePane.CaptureProject().RightArchiveInput is null && restore.ActivePane.CaptureProject().RightPath == saved
                && !restore.ActivePane.RightEditor.IsReadOnly);
        }
        finally { restore.Close(); }
        pump(window.PackageWorkspaceAsync(package, [Array.IndexOf(window.SessionPanes.ToArray(), pane)], new(true, true, true, true)));
        Verify("saved draft can package documents HTML project and patch", new FileInfo(package).Length > 0 && !pane.HasUnsavedChanges);
        Verify("all original root bytes remain unchanged", roots.Select((root, index) => Hash(root) == hashes[index]).All(same => same));
        using var proof = new MemoryStream();
        using (var writer = new Utf8JsonWriter(proof, new() { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("roots"); foreach (var root in roots) writer.WriteStringValue(root); writer.WriteEndArray();
            writer.WriteStartArray("hashes"); foreach (var hash in hashes) writer.WriteStringValue(hash); writer.WriteEndArray();
            foreach (var (name, value) in new[] { ("draft", draft), ("latest", latest), ("html", html), ("saved", saved),
                ("workspace", workspace), ("adoptionOutput", adoptionOutput), ("canceled", canceled), ("canceledHash", canceledHash),
                ("partialRoot", partialRoot), ("partialHash", partialHash), ("partialSaved", partialSaved), ("emptySaved", emptySaved), ("staleOutput", staleOutput), ("staleHash", staleHash), ("package", package) }) writer.WriteString(name, value);
            writer.WriteEndObject();
        }
        File.WriteAllBytes(Path.Combine(folder, "draft-proof.json"), proof.ToArray());
        void Close(ComparisonPane entry)
        {
            var tab = window.GetVisualDescendants().OfType<TabControl>().SelectMany(tabs => tabs.Items.OfType<TabItem>()).Single(item => ReferenceEquals(item.Content, entry));
            ((StackPanel)tab.Header!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        }
    }
}
