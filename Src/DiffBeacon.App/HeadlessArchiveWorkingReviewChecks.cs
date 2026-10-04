using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessArchiveWorkingReviewChecks
{
    internal static void Run(MainWindow window, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        // 公開asset保護、祖先更新、別枝の暗号階層、最終diff captionを実アプリの保存・比較で検証する。
        var folder = Path.Combine(output, "review-working"); Directory.CreateDirectory(folder);
        var root = Path.Combine(folder, "source.zip");
        using (var file = File.Create(root)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        { using var entry = zip.CreateEntry("leaf.txt").Open(); entry.Write("original\n"u8); }
        ArchiveProjectInput Input() => new() { RootPath = root, RootSha256 = Hash(root), LeafEntry = "leaf.txt", InheritedReadOnly = false };
        var leaf = window.AddSession(); leaf.ApplyProject(new() { LeftArchiveInput = Input(), RightArchiveInput = Input(), LeftReadOnly = true, RightReadOnly = true });
        pump(leaf.ComparePathsAsync()); leaf.LeftEditor.Text = "saved work\n"; pump(leaf.SaveAsync(false));
        var workspace = Path.Combine(folder, "saved.json"); pump(window.SaveWorkspaceAsync(workspace));
        var asset = Directory.GetFiles(workspace + ".assets").Single(); var before = Hash(asset);
        // before runでも実際のSaveAs公開を試み、破損証拠を残す。以降の復元は別snapshotを使う。
        leaf.LeftEditor.Text = "would overwrite asset\n";
        var refused = Refused(() => pump(leaf.SaveTextToAsync(false, asset)));
        Report("P1 new published asset is protected from SaveAs", refused && Hash(asset) == before);
        leaf.DiscardChanges();
        if (!refused) { leaf = window.AddSession(); leaf.ApplyProject(new() { LeftArchiveInput = Input(), RightArchiveInput = Input(), LeftReadOnly = true, RightReadOnly = true }); pump(leaf.ComparePathsAsync()); }
        leaf.LeftEditor.Text = "restore asset\n"; pump(leaf.SaveAsync(false));
        var oldWorkspace = Path.Combine(folder, "restored.json"); pump(window.SaveWorkspaceAsync(oldWorkspace));
        var oldAsset = Directory.GetFiles(oldWorkspace + ".assets").Single(); var oldHash = Hash(oldAsset);
        pump(window.OpenWorkspaceAsync(oldWorkspace, discardChanges: true)); leaf = window.SessionPanes.Single(p => p.CaptureProject().LeftArchiveInput is not null);
        pump(leaf.ComparePathsAsync());
        leaf.LeftEditor.Text = "new revision\n"; pump(leaf.SaveAsync(false));
        Report("P1 restored asset remains protected after revision", Refused(() => pump(leaf.SaveTextToAsync(false, oldAsset))) && Hash(oldAsset) == oldHash);
        leaf.DiscardChanges();
        leaf.LeftEditor.Text = "failed publication work\n"; pump(leaf.SaveAsync(false));
        var failedWorkspace = Path.Combine(folder, "failed.json");
        var many = new ComparisonWorkspace { Entries = [leaf.CaptureProject() with { LegacySettings = new() { ["large"] = "" } }] };
        var metadataBytes = WorkspaceStore.SerializeWorkspace(many).Length;
        many.Entries[0].LegacySettings["large"] = new string('m', WorkspaceStore.MaxFileBytes - metadataBytes - 16);
        // snapshotPathの追加により最後のJSON容量検査だけが拒否する。既存projectを先に残す。
        File.WriteAllText(failedWorkspace, "keep project");
        var initialValid = true; try { _ = WorkspaceStore.SerializeWorkspace(many); } catch (InvalidDataException) { initialValid = false; }
        var failed = Refused(() => pump(WorkspaceStore.SaveWorkspaceAsync(failedWorkspace, many, publishedAsset: window.ArchiveLifetime.RegisterAsset)));
        var failedAssets = Directory.Exists(failedWorkspace + ".assets") ? Directory.GetFiles(failedWorkspace + ".assets") : [];
        Report("P1 JSON publication failure keeps registered immutable asset", initialValid && failed && File.ReadAllText(failedWorkspace) == "keep project"
            && failedAssets.Length == 1 && Refused(() => pump(leaf.SaveTextToAsync(false, failedAssets[0]))));
        leaf.DiscardChanges();

        var three = window.AddSession(); three.ApplyProject(new() { LeftArchiveInput = Input(), BaseArchiveInput = Input(), RightArchiveInput = Input(), LeftReadOnly = true, BaseReadOnly = true, RightReadOnly = true });
        pump(three.ComparePathsAsync()); three.StartMergeSession(); three.ResultEditor.Text = "manual result\n";
        var merge = three.CurrentMergeSession;
        leaf.LeftEditor.Text = "updated ancestor\n"; pump(leaf.SaveAsync(false));
        window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), three)); screenshot("review-ancestor.png");
        var html = Path.Combine(folder, "ancestor.html"); pump(three.SaveReportAsync(html));
        Report("P2 ancestor GUI HTML reflects shared save", File.ReadAllText(html).Contains("updated ancestor", StringComparison.Ordinal)
            && three.ArchiveAncestorText == "updated ancestor\n");
        Report("P2 manual merge remains dirty and explicitly stale", ReferenceEquals(merge, three.CurrentMergeSession) && three.ResultEditor.Text == "manual result\n"
            && three.HasUnsavedChanges && three.GetVisualDescendants().OfType<TextBlock>().Any(box => box.Text?.Contains("旧入力", StringComparison.Ordinal) == true));
        Report("P2 stale source adoption is refused", Refused(() => three.ChooseMergeSources(MergeSource.Base)));
        var undoBefore = merge!.CanUndo; three.UndoMerge(); var undone = three.ResultEditor.Text; three.RedoMerge();
        var mergeHistoryPreserved = undoBefore && undone != "manual result\n" && three.ResultEditor.Text == "manual result\n" && three.HasUnsavedChanges;
        Report("P2 stale merge retains manual undo redo and dirty", mergeHistoryPreserved);
        var invalidMergeProject = three.CaptureProject(); invalidMergeProject.IgnoreLinePattern = "("; three.ApplyProject(invalidMergeProject);
        var failedRestart = false;
        try { three.StartMergeSession(); } catch (ArgumentException) { failedRestart = true; }
        var failedRestartPreserved = failedRestart && ReferenceEquals(merge, three.CurrentMergeSession) && three.ResultEditor.Text == "manual result\n"
            && merge.CanUndo && three.HasUnsavedChanges && three.MergeSourcesStale;
        Report("P2 failed merge restart preserves previous session dirty history and stale guard", failedRestartPreserved);
        Report("P2 failed merge restart continues refusing old source adoption", Refused(() => three.ChooseMergeSources(MergeSource.Base)) && three.MergeSourcesStale);
        invalidMergeProject.IgnoreLinePattern = null; three.ApplyProject(invalidMergeProject);

        var caption = window.AddSession(); var captionProject = new ComparisonProject { LeftArchiveInput = Input(), RightArchiveInput = Input(), LeftReadOnly = true, RightReadOnly = true };
        captionProject.LeftArchiveInput!.InheritedReadOnly = true; caption.ApplyProject(captionProject); pump(caption.ComparePathsAsync());
        captionProject.LeftArchiveInput.InheritedReadOnly = false; caption.ApplyProject(captionProject); pump(caption.ComparePathsAsync());
        window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), caption)); screenshot("review-final-caption.png");
        Report("P2 final laid out diff captions agree with writable editor", caption.ArchiveDiffCaptions == ("左", "右") && !caption.LeftEditor.IsReadOnly);

        var encrypted = Path.Combine(folder, "encrypted.zip");
        using (var resource = typeof(HeadlessArchiveSourceChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.Sources.two-passwords.zip")!) using (var file = File.Create(encrypted)) resource.CopyTo(file);
        var bytes = Encoding.UTF8.GetBytes("saved encrypted work\n");
        var parent = window.AddSession(); parent.ApplyProject(new() { Mode = "Archive", LeftArchiveInput = new() { RootPath = encrypted, RootSha256 = Hash(encrypted), InheritedReadOnly = false,
            WorkingTexts = [new() { EntryChain = ["inner.zip"], LeafEntry = "leaf.txt", Bytes = bytes, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), EncodingName = "utf-8" }] }, RightPath = root, LeftReadOnly = true });
        var fullRoute = false; var dialogs = 0;
        parent.ArchiveSourceRetryShown = dialog =>
        {
            dialogs++; fullRoute |= dialog.LeftPasswords.Count == 2 && dialog.LeftPasswords.All(box => box.PasswordChar == '●');
            if (dialogs > 2 || dialog.LeftPasswords.Count != 2) { dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return; }
            dialog.LeftPasswords[0].Text = "outer-source-fixture"; dialog.LeftPasswords[1].Text = "inner-source-fixture";
            dialog.Retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        var completed = true; try { pump(parent.CompareArchiveProjectAsync(leftPasswords: ["outer-source-fixture"])); } catch (OperationCanceledException) { completed = false; }
        Report("P2 restored parent retries complete masked descendant chain", completed && fullRoute);
        parent.ArchiveSourceRetryShown = null;

        var branches = Path.Combine(folder, "branches.zip");
        File.WriteAllBytes(branches, Zip(("a.zip", EncryptedZip("leaf.txt", "branch a original\n"u8.ToArray(), "branch-a-fixture")),
            ("b.zip", EncryptedZip("leaf.txt", "branch b original\n"u8.ToArray(), "branch-b-fixture"))));
        ArchiveTextSnapshot Snapshot(string branch) { var data = Encoding.UTF8.GetBytes("working " + branch + "\n"); return new() { EntryChain = [branch], LeafEntry = "leaf.txt", Bytes = data, Sha256 = Convert.ToHexString(SHA256.HashData(data)), EncodingName = "utf-8" }; }
        var branched = window.AddSession(); branched.ApplyProject(new() { Mode = "Archive", LeftReadOnly = true, RightPath = root,
            LeftArchiveInput = new() { RootPath = branches, RootSha256 = Hash(branches), InheritedReadOnly = false, WorkingTexts = [Snapshot("a.zip"), Snapshot("b.zip")] } });
        var requested = new List<string>(); var branchMasks = true; var isolated = true;
        branched.ArchiveSourceRetryShown = dialog =>
        {
            var route = dialog.RequestedProject.LeftArchiveInput?.EntryChain;
            if (route is not { Length: 1 } || dialog.LeftPasswords.Count != 2 || requested.Count > 3) { dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return; }
            requested.Add(route[0]); branchMasks &= dialog.LeftPasswords.All(box => box.PasswordChar == '●');
            isolated &= string.IsNullOrEmpty(dialog.LeftPasswords[1].Text);
            dialog.LeftPasswords[1].Text = route[0] == "a.zip" ? "branch-a-fixture" : "branch-b-fixture";
            dialog.Retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        var branchesCompleted = true; try { pump(branched.CompareArchiveProjectAsync()); } catch (OperationCanceledException) { branchesCompleted = false; }
        Report("P2 same depth encrypted branches request isolated masked credentials", branchesCompleted && branchMasks && isolated && requested.SequenceEqual(new[] { "a.zip", "b.zip" }));
        var routeDialogs = requested.Count; pump(branched.CompareArchiveProjectAsync());
        Report("P2 full route cache recompare does not mix branches", requested.Count == routeDialogs);
        var branchedWorkspace = Path.Combine(folder, "branches.json"); pump(WorkspaceStore.SaveWorkspaceAsync(branchedWorkspace, new() { Entries = [branched.CaptureProject()] }));
        var branchJson = File.ReadAllText(branchedWorkspace);
        Report("P2 workspace omits branch credentials and raw body", !branchJson.Contains("branch-a-fixture", StringComparison.Ordinal) && !branchJson.Contains("branch-b-fixture", StringComparison.Ordinal)
            && !branchJson.Contains("working a.zip", StringComparison.Ordinal));
        var originalBranchBytes = File.ReadAllBytes(branches); var originalBranchProject = branched.CaptureProject();
        var changedRequests = 0; var oldCredentialsAbsent = true;
        try
        {
            File.WriteAllBytes(branches, Zip(("a.zip", EncryptedZip("leaf.txt", "branch a original\n"u8.ToArray(), "changed-a-fixture")),
                ("b.zip", EncryptedZip("leaf.txt", "branch b original\n"u8.ToArray(), "changed-b-fixture"))));
            var changedProject = WorkspaceStore.CloneProject(originalBranchProject); changedProject.LeftArchiveInput!.RootSha256 = Hash(branches);
            branched.ApplyProject(changedProject);
            branched.ArchiveSourceRetryShown = dialog =>
            {
                var chain = dialog.RequestedProject.LeftArchiveInput?.EntryChain;
                if (chain is not { Length: 1 } || dialog.LeftPasswords.Count != 2 || changedRequests > 2) { dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return; }
                changedRequests++; oldCredentialsAbsent &= string.IsNullOrEmpty(dialog.LeftPasswords[1].Text);
                dialog.LeftPasswords[1].Text = chain[0] == "a.zip" ? "changed-a-fixture" : "changed-b-fixture";
                dialog.Retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            pump(branched.CompareArchiveProjectAsync());
            Report("P2 changed root SHA cannot reuse previous branch credentials", changedRequests == 2 && oldCredentialsAbsent);
        }
        finally { File.WriteAllBytes(branches, originalBranchBytes); branched.ApplyProject(originalBranchProject); }
        branched.ArchiveSourceRetryShown = null;
        using (var stream = File.Create(Path.Combine(folder, "facts.json"))) using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("source", root); writer.WriteString("sourceSha", Hash(root)); writer.WriteString("workspace", oldWorkspace);
            writer.WriteString("restoredAsset", oldAsset); writer.WriteString("restoredAssetSha", oldHash); writer.WriteString("actualRestoredAssetSha", Hash(oldAsset));
            writer.WriteString("html", html); writer.WriteString("expectedLeft", "updated ancestor\n"); writer.WriteString("expectedBase", "updated ancestor\n"); writer.WriteString("expectedRight", "updated ancestor\n");
            writer.WriteString("ancestorEditor", three.ArchiveAncestorText); writer.WriteString("manualResult", three.ResultEditor.Text); writer.WriteString("undoResult", undone);
            writer.WriteBoolean("manualHistoryPreserved", mergeHistoryPreserved); writer.WriteBoolean("dirty", three.HasUnsavedChanges);
            writer.WriteBoolean("failedRestartPreserved", failedRestartPreserved);
            writer.WriteBoolean("staleAdoptionRefused", Refused(() => three.ChooseMergeSources(MergeSource.Base)));
            writer.WriteString("leftCaption", caption.ArchiveDiffCaptions.Left); writer.WriteString("rightCaption", caption.ArchiveDiffCaptions.Right);
            writer.WriteString("branches", branches); writer.WriteString("branchesSha", Hash(branches)); writer.WriteString("branchWorkspace", branchedWorkspace);
            writer.WriteStartArray("requestedBranches"); foreach (var branch in requested) writer.WriteStringValue(branch); writer.WriteEndArray();
            writer.WriteNumber("branchDialogs", requested.Count); writer.WriteBoolean("branchMasked", branchMasks); writer.WriteBoolean("branchIsolated", isolated); writer.WriteEndObject();
        }

        void Report(string name, bool passed) { try { check(name, passed, ""); } catch (InvalidOperationException) { } }
        static bool Refused(Action action) { try { action(); return false; } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException) { return true; } }
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var memory = new MemoryStream(); using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var (name, bytes) in entries) { using var entry = zip.CreateEntry(name).Open(); entry.Write(bytes); }
        return memory.ToArray();
    }

    // CC0 Source fixtureと同じ標準ZipCryptoの小さな生成器。公開fixture用passwordだけを使う。
    private static byte[] EncryptedZip(string name, byte[] bytes, string password)
    {
        static uint Crc(uint current, byte value) { current ^= value; for (var bit = 0; bit < 8; bit++) current = (current & 1) != 0 ? (current >> 1) ^ 0xedb88320 : current >> 1; return current; }
        uint k0 = 0x12345678, k1 = 0x23456789, k2 = 0x34567890;
        void Update(byte value) { k0 = Crc(k0, value); k1 = unchecked((k1 + (byte)k0) * 134775813 + 1); k2 = Crc(k2, (byte)(k1 >> 24)); }
        foreach (var value in Encoding.UTF8.GetBytes(password)) Update(value);
        var crc = uint.MaxValue; foreach (var value in bytes) crc = Crc(crc, value); crc ^= uint.MaxValue;
        var plain = Enumerable.Range(0, 11).Select(value => (byte)value).Append((byte)(crc >> 24)).Concat(bytes).ToArray();
        var encrypted = new byte[plain.Length];
        for (var i = 0; i < plain.Length; i++) { var temp = k2 | 2; encrypted[i] = (byte)(plain[i] ^ ((temp * (temp ^ 1)) >> 8)); Update(plain[i]); }
        var nameBytes = Encoding.UTF8.GetBytes(name); using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(0x04034b50u); writer.Write((ushort)20); writer.Write((ushort)1); writer.Write((ushort)0); writer.Write(0u);
        writer.Write(crc); writer.Write(encrypted.Length); writer.Write(bytes.Length); writer.Write((ushort)nameBytes.Length); writer.Write((ushort)0); writer.Write(nameBytes); writer.Write(encrypted);
        var central = (uint)stream.Position; writer.Write(0x02014b50u); writer.Write((ushort)20); writer.Write((ushort)20); writer.Write((ushort)1); writer.Write((ushort)0); writer.Write(0u);
        writer.Write(crc); writer.Write(encrypted.Length); writer.Write(bytes.Length); writer.Write((ushort)nameBytes.Length); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u); writer.Write(0u); writer.Write(nameBytes);
        var size = (uint)stream.Position - central; writer.Write(0x06054b50u); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(size); writer.Write(central); writer.Write((ushort)0);
        return stream.ToArray();
    }
}
