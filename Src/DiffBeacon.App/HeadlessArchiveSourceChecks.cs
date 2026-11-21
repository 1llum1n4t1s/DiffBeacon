using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static class HeadlessArchiveSourceChecks
{
    internal static void Run(MainWindow window, ComparisonPane original, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "archive-sources"); Directory.CreateDirectory(folder);
        const string leftText = "header\r\nleft 日本\r\ntail\n", rightText = "header\r\nright 日本\r\ntail\n";
        var large = Enumerable.Range(0, 16385).Select(i => (byte)(i * 37)).ToArray();
        var leftGif = ImageFixture("same-first-left.gif"); var rightGif = ImageFixture("same-first-right.gif");
        var staticPng = ImageFixture("disposal-2.png");
        var brokenGif = new byte[] { 1, 2, 3 };
        var leftInner = Zip(("leaf.txt", Encoding.UTF8.GetBytes(leftText)), ("empty.txt", []), ("binary.bin", large), ("folder/", []), ("frames.gif", leftGif), ("still.png", staticPng), ("broken.gif", brokenGif));
        var rightInner = Zip(("leaf.txt", Encoding.UTF8.GetBytes(rightText)), ("empty.txt", []), ("binary.bin", large), ("folder/", []), ("frames.gif", rightGif), ("still.png", staticPng), ("broken.gif", brokenGif));
        var roots = new[] { Path.Combine(folder, "left.zip"), Path.Combine(folder, "right.zip") };
        File.WriteAllBytes(roots[0], Zip(("inner.zip", leftInner), ("bad.zip", [1, 2, 3]), ("only.txt", [1])));
        File.WriteAllBytes(roots[1], Zip(("inner.zip", rightInner), ("bad.zip", [1, 2, 3])));
        var hashes = roots.Select(HashFile).ToArray();
        var filter = Path.Combine(folder, "source.flt"); File.WriteAllText(filter, "def: include\n"); var filterHash = HashFile(filter);
        var observations = new List<(string Stage, bool Rows, bool Preview, bool Active, int Tabs)>();
        var viewports = new List<(string Name, double Width, double Height, double Entries, double Preview, bool Reachable)>();
        var parent = window.AddSession(); parent.ApplyProject(new() { LeftPath = roots[0], RightPath = roots[1], Mode = "Archive", FileFilterPath = filter });
        pump(parent.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
        var panel = parent.GetVisualDescendants().OfType<ArchivePanel>().Single();
        var count = window.SessionPanes.Count;
        Select(panel, "inner.zip"); pump(panel.OpenSelectedAsync()); Dispatcher.UIThread.RunJobs();
        var innerPane = window.ActivePane; var inner = innerPane.GetVisualDescendants().OfType<ArchivePanel>().Single();
        var innerProject = innerPane.CaptureProject();
        Verify("nested source preserves original editable intention without unlocking archive",
            innerProject.LeftArchiveInput?.InheritedReadOnly == false && innerProject.RightArchiveInput?.InheritedReadOnly == false
            && innerProject.LeftReadOnly && innerProject.RightReadOnly);
        Verify("nested container opens after complete preparation", count + 1 == window.SessionPanes.Count && inner.Rows.Count == 7
            && innerProject.LeftPath == "" && innerProject.LeftArchiveInput?.RootPath == roots[0]
            && innerProject.LeftArchiveInput.EntryChain.SequenceEqual(new[] { "inner.zip" }) && innerProject.LeftArchiveInput.RootSha256 == hashes[0]);
        var innerRowsBeforeImage = inner.Rows;
        Select(inner, "frames.gif"); pump(inner.OpenSelectedAsync()); Dispatcher.UIThread.RunJobs();
        var autoImagePane = window.ActivePane;
        var autoImage = autoImagePane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
        var autoImageProject = autoImagePane.CaptureProject();
        var autoSnapshots = autoImage.CaptureReport().Images;
        var leftImageFrames = FrameProof(autoSnapshots[0]); var rightImageFrames = FrameProof(autoSnapshots[1]);
        string[] leftExpectedFrames = ["62D7BF737BCBF3DF90471F8FB00F6BCD5F1148B18327EE57943A1E80E7C06281", "9B35F2646DFF9F19FA0DD89F334E5800C4925F9294E1F825B0DFB61DAA59D0A0"];
        string[] rightExpectedFrames = ["62D7BF737BCBF3DF90471F8FB00F6BCD5F1148B18327EE57943A1E80E7C06281", "62D7BF737BCBF3DF90471F8FB00F6BCD5F1148B18327EE57943A1E80E7C06281"];
        var leftPixelsMatch = Matches(autoSnapshots[0], leftExpectedFrames); var rightPixelsMatch = Matches(autoSnapshots[1], rightExpectedFrames);
        var imageRootsMatch = autoImageProject.LeftArchiveInput?.RootSha256 == hashes[0] && autoImageProject.RightArchiveInput?.RootSha256 == hashes[1];
        var imageLeavesMatch = autoImageProject.LeftArchiveInput?.LeafEntry == "frames.gif" && autoImageProject.RightArchiveInput?.LeafEntry == "frames.gif"
            && autoImageProject.LeftArchiveInput?.EntryChain.SequenceEqual(["inner.zip"]) == true && autoImageProject.RightArchiveInput?.EntryChain.SequenceEqual(["inner.zip"]) == true;
        var imageSourceHashesMatch = Hash(leftGif) == "6B583AA0F396699D50B0DF1C65C4667290515A99022527E1D5126A27F81567EE"
            && Hash(rightGif) == "8B813BCBFDA2954F70B770ADFB3C5F85101978686B2C8C9750FBA5BA6488481A";
        var persistedZoom = window.ImageOptions.Current.Zoom == 1.25 ? 1.5 : 1.25;
        pump(autoImage.ApplySettingsAsync(autoImage.CaptureSettings() with { Zoom = persistedZoom }));
        var persistedImageOptions = new ImageApplicationOptionsStore(Path.Combine(output, "image-application-options.json"));
        Verify("adopted archived image persists changed application defaults", window.ImageOptions.Current.Zoom == persistedZoom
            && persistedImageOptions.Current.Zoom == persistedZoom, persistedImageOptions.Current.Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture));
        autoImageProject = autoImagePane.CaptureProject();
        var autoModeSelected = inner.EntryKind.SelectedIndex == 0;
        Verify("automatic image child opens both archived GIFs read-only", autoModeSelected && autoImageProject.Mode == "Image" && autoImageProject.LeftPath == "" && autoImageProject.RightPath == ""
            && imageRootsMatch && imageLeavesMatch && autoImageProject.LeftArchiveInput?.InheritedReadOnly == true && autoImageProject.RightArchiveInput?.InheritedReadOnly == true
            && autoImageProject.LeftReadOnly && autoImageProject.RightReadOnly && autoImage.LeftFrameCount == 2 && autoImage.RightFrameCount == 2
            && leftPixelsMatch && rightPixelsMatch && imageSourceHashesMatch && HashFile(roots[0]) == hashes[0] && HashFile(roots[1]) == hashes[1],
            $"selectedIndex={inner.EntryKind.SelectedIndex}; mode={autoImageProject.Mode}; roots={imageRootsMatch}; leaves={imageLeavesMatch}; inheritedReadonly={autoImageProject.LeftArchiveInput?.InheritedReadOnly}/{autoImageProject.RightArchiveInput?.InheritedReadOnly}; projectReadonly={autoImageProject.LeftReadOnly}/{autoImageProject.RightReadOnly}; frames={autoImage.LeftFrameCount}/{autoImage.RightFrameCount}; pixels={leftPixelsMatch}/{rightPixelsMatch}; actual={string.Join(',', leftImageFrames.Select(frame => frame.PixelSha256))}/{string.Join(',', rightImageFrames.Select(frame => frame.PixelSha256))}; imageHashes={imageSourceHashesMatch}; rootBytes={HashFile(roots[0]) == hashes[0]}/{HashFile(roots[1]) == hashes[1]}");
        screenshot("archive-source-image.png");
        SpecializedViews.SetProjectReadOnly(autoImage, false, false);
        var imageSave = Path.Combine(folder, "archive-image-save.png");
        Verify("archived animation refuses project readonly clearing", Refused(() => autoImagePane.ApplyProject(autoImageProject with { LeftReadOnly = false }))
            && Refused(() => pump(autoImage.SaveToAsync(0, imageSave))) && !File.Exists(imageSave)
            && autoImagePane.CaptureProject().LeftReadOnly && autoImagePane.CaptureProject().RightReadOnly
            && HashFile(roots[0]) == hashes[0] && HashFile(roots[1]) == hashes[1]);
        var imageWorkspace = Path.Combine(folder, "image-child-workspace.json"); pump(window.SaveWorkspaceAsync(imageWorkspace));
        var imageEntryIndex = window.SessionPanes.ToList().IndexOf(autoImagePane);
        using (var json = JsonDocument.Parse(File.ReadAllBytes(imageWorkspace)))
        {
            var entry = json.RootElement.GetProperty("entries")[imageEntryIndex];
            Verify("image workspace saves typed roots and leaf", entry.GetProperty("mode").GetString() == "Image"
                && entry.GetProperty("leftArchiveInput").GetProperty("leafEntry").GetString() == "frames.gif"
                && entry.GetProperty("rightArchiveInput").GetProperty("leafEntry").GetString() == "frames.gif"
                && entry.GetProperty("leftArchiveInput").GetProperty("rootSha256").GetString() == hashes[0]
                && entry.GetProperty("rightArchiveInput").GetProperty("rootSha256").GetString() == hashes[1]);
        }
        WriteImageProof(Path.Combine(folder, "image-child-proof.json"), autoImageProject, inner.EntryKind.SelectedIndex, leftGif, rightGif, leftImageFrames, rightImageFrames);
        var imageRestore = new MainWindow(null, new ImageApplicationOptionsStore(Path.Combine(folder, "image-restore-options.json")));
        try
        {
            imageRestore.Show(); pump(imageRestore.OpenWorkspaceAsync(imageWorkspace, discardChanges: true));
            var restoredImagePane = imageRestore.ActivePane; var restoredImageProject = restoredImagePane.CaptureProject();
            Verify("restored image waits for an explicit read", restoredImageProject.Mode == "Image" && restoredImageProject.LeftPath == "" && restoredImageProject.RightPath == ""
                && restoredImageProject.LeftArchiveInput is { LeafEntry: "frames.gif", EntryChain.Length: 1, RootSha256: var restoredLeftRoot } && restoredLeftRoot == hashes[0]
                && restoredImageProject.RightArchiveInput is { LeafEntry: "frames.gif", EntryChain.Length: 1, RootSha256: var restoredRightRoot } && restoredRightRoot == hashes[1]
                && !restoredImagePane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Any());
            pump(restoredImagePane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
            var restoredImage = restoredImagePane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
            var restoredSnapshots = restoredImage.CaptureReport().Images;
            Verify("restored archived image decodes every frame and remains read-only", restoredImagePane.CaptureProject().Mode == "Image"
                && restoredImagePane.CaptureProject().LeftReadOnly && restoredImagePane.CaptureProject().RightReadOnly
                && Matches(restoredSnapshots[0], leftExpectedFrames) && Matches(restoredSnapshots[1], rightExpectedFrames)
                && Refused(() => pump(restoredImage.SaveToAsync(0, Path.Combine(folder, "restored-image-save.png"))))
                && HashFile(roots[0]) == hashes[0] && HashFile(roots[1]) == hashes[1]);

            var imageInput = restoredImagePane.CaptureProject().LeftArchiveInput!;
            var binaryWorkBytes = rightGif.ToArray();
            var binaryWork = new ArchiveWorkingSnapshot
            {
                EntryChain = imageInput.EntryChain.ToArray(), ContainerNameCodePages = imageInput.ContainerNameCodePages?.ToArray(),
                ContainerGZipPayloadKinds = imageInput.ContainerGZipPayloadKinds?.ToArray(),
                ContainerCompressionPayloadKinds = imageInput.ContainerCompressionPayloadKinds?.ToArray(),
                LeafEntry = imageInput.LeafEntry!, Bytes = binaryWorkBytes, Sha256 = Hash(binaryWorkBytes), Kind = "Binary"
            };
            imageRestore.ArchiveTexts.Save(imageInput, imageRestore.ArchiveTexts.Revision(imageInput), binaryWork);
            restoredImagePane.WorkingTextSaved();
            var binaryRevisionWorkspace = Path.Combine(folder, "image-binary-revision-workspace.json");
            string? workspaceSaveError = null;
            try { pump(imageRestore.SaveWorkspaceAsync(binaryRevisionWorkspace)); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException) { workspaceSaveError = error.Message; }
            var workspaceOmitsBinaryWork = false;
            if (workspaceSaveError is null)
            {
                using var savedWorkspace = JsonDocument.Parse(File.ReadAllBytes(binaryRevisionWorkspace));
                var imageEntry = savedWorkspace.RootElement.GetProperty("entries").EnumerateArray()
                    .Single(entry => entry.GetProperty("mode").GetString() == "Image"
                        && entry.TryGetProperty("leftArchiveInput", out var leftInput) && leftInput.ValueKind == JsonValueKind.Object
                        && leftInput.GetProperty("rootSha256").GetString() == hashes[0]
                        && entry.TryGetProperty("rightArchiveInput", out var rightInput) && rightInput.ValueKind == JsonValueKind.Object
                        && rightInput.GetProperty("rootSha256").GetString() == hashes[1]);
                workspaceOmitsBinaryWork = !imageEntry.GetProperty("leftArchiveInput").TryGetProperty("workingTexts", out _)
                    && !imageEntry.GetProperty("rightArchiveInput").TryGetProperty("workingTexts", out _);
            }
            Verify("binary work revision does not stale or enter archived image workspace", workspaceSaveError is null && workspaceOmitsBinaryWork,
                workspaceSaveError ?? "Image workspace saves only its read-only source roots.");
            if (workspaceSaveError is null)
            {
                var binaryRevisionRestore = new MainWindow(null, new ImageApplicationOptionsStore(Path.Combine(folder, "binary-revision-restore-options.json")));
                try
                {
                    binaryRevisionRestore.Show(); pump(binaryRevisionRestore.OpenWorkspaceAsync(binaryRevisionWorkspace, discardChanges: true));
                    var pendingPane = binaryRevisionRestore.ActivePane;
                    var pendingProject = pendingPane.CaptureProject();
                    pump(pendingPane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
                    var reopenedImage = pendingPane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
                    var reopenedSnapshots = reopenedImage.CaptureReport().Images;
                    Verify("binary work revision keeps image reread on original roots", pendingProject.Mode == "Image"
                        && Matches(reopenedSnapshots[0], leftExpectedFrames) && Matches(reopenedSnapshots[1], rightExpectedFrames)
                        && HashFile(roots[0]) == hashes[0] && HashFile(roots[1]) == hashes[1]);
                }
                finally { binaryRevisionRestore.Close(); }
            }
        }
        finally { imageRestore.Close(); }
        ClosePane(autoImagePane); Activate(innerPane);
        Select(inner, "still.png"); pump(inner.OpenSelectedAsync()); Dispatcher.UIThread.RunJobs();
        var staticImagePane = window.ActivePane;
        var staticImage = staticImagePane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
        var staticImagePath = Path.Combine(folder, "static-archive-image-save.png");
        SpecializedViews.SetProjectReadOnly(staticImage, false, false);
        Verify("single-frame archived PNG remains fixed readonly at save boundary", staticImage.LeftFrameCount == 1 && staticImage.RightFrameCount == 1
            && RefusedWithMessage(() => pump(staticImage.SaveToAsync(0, staticImagePath)), "読取り専用") && !File.Exists(staticImagePath)
            && staticImagePane.CaptureProject().LeftReadOnly && staticImagePane.CaptureProject().RightReadOnly
            && HashFile(roots[0]) == hashes[0] && HashFile(roots[1]) == hashes[1]);
        ClosePane(staticImagePane); Activate(innerPane);
        inner.EntryKind.SelectedIndex = 4; Select(inner, "frames.gif"); pump(inner.OpenSelectedAsync()); Dispatcher.UIThread.RunJobs();
        var explicitImagePane = window.ActivePane; var explicitImage = explicitImagePane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
        var explicitImageProject = explicitImagePane.CaptureProject(); var explicitSnapshots = explicitImage.CaptureReport().Images;
        Verify("explicit Image dropdown opens full archive snapshots", inner.EntryKind.SelectedItem?.ToString() == "画像"
            && explicitImageProject.Mode == "Image" && explicitImageProject.LeftArchiveInput?.LeafEntry == "frames.gif"
            && Matches(explicitSnapshots[0], leftExpectedFrames) && Matches(explicitSnapshots[1], rightExpectedFrames));
        ClosePane(explicitImagePane); Activate(innerPane); inner.EntryKind.SelectedIndex = 0;
        var retries = 0; innerPane.ArchiveSourceRetryShown = dialog => { retries++; dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        var tabCountBeforeBadImage = window.SessionPanes.Count; Select(inner, "broken.gif");
        Canceled(inner.OpenSelectedAsync(), "corrupt archived image"); innerPane.ArchiveSourceRetryShown = null;
        Verify("corrupt auto image does not create a child or alter roots", retries == 1 && window.SessionPanes.Count == tabCountBeforeBadImage
            && ReferenceEquals(window.ActivePane, innerPane) && ReferenceEquals(inner.Rows, innerRowsBeforeImage)
            && HashFile(roots[0]) == hashes[0] && HashFile(roots[1]) == hashes[1]);
        Verify("deep container keeps unsupported full extraction disabled", inner.GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Content, "左をすべて展開") || Equals(b.Content, "左を再梱包")).All(b => !b.IsEnabled));
        var initialWidth = window.Width; var initialHeight = window.Height;
        Viewport("normal");
        window.Width = 850; window.Height = 550; Dispatcher.UIThread.RunJobs();
        Viewport("minimum");
        window.Width = initialWidth; window.Height = initialHeight; Dispatcher.UIThread.RunJobs();
        Select(inner, "binary.bin"); pump(inner.PreviewAsync(inner.Rows.Single(row => row.Path == "binary.bin")));
        Verify("large leaf preview validates beyond its retained prefix", inner.PreviewText.Contains("00000FF0  ", StringComparison.Ordinal) && !inner.PreviewText.Contains("00001000  ", StringComparison.Ordinal));
        Verify("public preview retains exactly 4096 bytes", new ManagedArchive().ResolveEntryPreview(inner.ConfirmedLeft, "binary.bin").SequenceEqual(large[..4096]));
        Verify("public preview accepts an explicit smaller prefix", new ManagedArchive().ResolveEntryPreview(inner.ConfirmedLeft, "binary.bin", maximumBytes: 29).SequenceEqual(large[..29]));
        var badCrcBytes = Zip(("binary.bin", large));
        var central = badCrcBytes.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x01, 0x02 });
        badCrcBytes[14] ^= 1; badCrcBytes[central + 16] ^= 1;
        var badCrcPath = Path.Combine(folder, "bad-preview-crc.zip"); File.WriteAllBytes(badCrcPath, badCrcBytes);
        Verify("public preview rejects final CRC beyond retained prefix", Refused(() => new ManagedArchive().ResolveEntryPreview(new(badCrcPath), "binary.bin")) && HashFile(badCrcPath) == Hash(badCrcBytes));
        var export = Path.Combine(folder, "exported-large.bin"); pump(inner.ExportToAsync(false, "binary.bin", export));
        Verify("deep GUI entry export keeps every byte", File.ReadAllBytes(export).SequenceEqual(large));
        Verify("deep export protects a different tab filter", Refused(() => pump(inner.ExportToAsync(false, "binary.bin", filter))) && HashFile(filter) == filterHash);
        var innerStop = innerPane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止"));
        var canceledExport = Path.Combine(folder, "canceled-export.bin"); File.WriteAllText(canceledExport, "preserve canceled export"); var canceledExportHash = HashFile(canceledExport);
        inner.ExportReadStarting = () => innerStop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Canceled(inner.ExportToAsync(false, "binary.bin", canceledExport), "entry export before read"); inner.ExportReadStarting = null;
        inner.ExportReadyForPublication = () => innerStop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Canceled(inner.ExportToAsync(false, "binary.bin", canceledExport), "entry export before publication"); inner.ExportReadyForPublication = null;
        pump(inner.PreviewAsync(inner.Rows.Single(row => row.Path == "binary.bin")));
        Verify("real Stop preserves export output and leaves preview operable", HashFile(canceledExport) == canceledExportHash && inner.PreviewText.Contains("00000FF0  ", StringComparison.Ordinal));
        Select(inner, "folder"); Verify("directory cannot open a file comparison", !inner.OpenEntryButton.IsEnabled && Refused(() => pump(inner.OpenSelectedAsync())));
        Select(inner, "leaf.txt"); pump(inner.OpenSelectedAsync()); Dispatcher.UIThread.RunJobs();
        var leaf = window.ActivePane; var leafProject = leaf.CaptureProject();
        Verify("inner text uses original decoded leaf and inherited editable editors", leaf.LeftEditor.Text == leftText && leaf.RightEditor.Text == rightText
            && leaf.CurrentDiff is { Blocks.Count: > 0 } && !leaf.LeftEditor.IsReadOnly && !leaf.RightEditor.IsReadOnly
            && leaf.LeftPath.IsReadOnly && leafProject.LeftArchiveInput?.LeafEntry == "leaf.txt" && leafProject.LeftPath == "");
        leafProject.LeftArchiveInput!.EntryChain[0] = "mutated.zip";
        Verify("captured DTO does not mutate active source", leaf.CaptureProject().LeftArchiveInput!.EntryChain[0] == "inner.zip");
        var protectedLeaf = leaf.CaptureProject();
        protectedLeaf.LeftArchiveInput!.InheritedReadOnly = protectedLeaf.RightArchiveInput!.InheritedReadOnly = true;
        leaf.ApplyProject(protectedLeaf); pump(leaf.ComparePathsAsync());
        Verify("source readonly and mode cannot be changed through ApplyProject", Refused(() => leaf.ApplyProject(leaf.CaptureProject() with { LeftReadOnly = false }))
            && Refused(() => leaf.ApplyProject(leaf.CaptureProject() with { Mode = "Image", BasePath = "forbidden-base" }))
            && leaf.CaptureProject().LeftReadOnly && leaf.CaptureProject().Mode == "Text");
        leaf.ApplyProject(leaf.CaptureProject() with { Mode = "1" }); pump(leaf.ComparePathsAsync());
        Verify("GUI accepts the same explicit numeric source mode as CLI", leaf.CaptureProject().Mode == "Text" && leaf.LeftEditor.Text == leftText);
        var sourceRefreshStarted = false; leaf.ArchiveSourceReadStarting = () => sourceRefreshStarted = true;
        leaf.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "再比較")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pump(WaitRefreshAsync()); leaf.ArchiveSourceReadStarting = null;
        Verify("common recompare reads source with readonly state intact", sourceRefreshStarted && leaf.LeftEditor.Text == leftText && leaf.RightEditor.Text == rightText && leaf.LeftEditor.IsReadOnly);
        Verify("text save/copy cannot overwrite source", Refused(() => pump(leaf.SaveAsync(false))) && Refused(() => leaf.CopySelected(true)) && HashFile(roots[0]) == hashes[0] && HashFile(roots[1]) == hashes[1]);
        var html = Path.Combine(folder, "leaf.html"); pump(leaf.SaveReportAsync(html)); screenshot("archive-source-text.png");
        Verify("GUI HTML includes leaf text and input root is protected", System.Net.WebUtility.HtmlDecode(File.ReadAllText(html)).Contains("日本", StringComparison.Ordinal) && File.ReadAllText(html).Contains("left", StringComparison.Ordinal)
            && Refused(() => pump(leaf.SaveReportAsync(roots[0]))) && HashFile(roots[0]) == hashes[0]);
        var workspace = Path.Combine(folder, "workspace.json"); pump(window.SaveWorkspaceAsync(workspace));
        using (var json = JsonDocument.Parse(File.ReadAllBytes(workspace))) Verify("GUI workspace uses typed version 2", json.RootElement.GetProperty("formatVersion").GetInt32() == 2);
        var restore = new MainWindow(null, new ImageApplicationOptionsStore(Path.Combine(folder, "restore-options.json")));
        try
        {
            restore.Show(); pump(restore.OpenWorkspaceAsync(workspace, discardChanges: true));
            var restored = restore.ActivePane;
            Verify("workspace source restore waits for explicit comparison", restored.LeftEditor.Text == "" && restored.CurrentDiff is null
                && restored.CaptureProject().LeftArchiveInput?.EntryChain[0] == "inner.zip");
            Verify("workspace restores inherited intention separately from fixed source readonly",
                restored.CaptureProject().LeftArchiveInput?.InheritedReadOnly == true
                && restored.CaptureProject().RightArchiveInput?.InheritedReadOnly == true
                && restored.LeftEditor.IsReadOnly && restored.RightEditor.IsReadOnly);
            var retained = Path.Combine(folder, "pending-report.html"); File.WriteAllText(retained, "keep pending output"); var retainedHash = HashFile(retained);
            Verify("restored pending source cannot emit blank HTML", Refused(() => pump(restored.SaveReportAsync(retained))) && HashFile(retained) == retainedHash);
            pump(restored.ComparePathsAsync());
            Verify("explicit restored comparison reopens original leaf", restored.LeftEditor.Text == leftText && restored.RightEditor.Text == rightText);
            Verify("GUI report preserves source workspace", Refused(() => pump(restored.SaveReportAsync(workspace))));
        }
        finally { restore.Close(); }
        var three = window.AddSession(); var threeProject = leaf.CaptureProject();
        threeProject.BaseArchiveInput = threeProject.LeftArchiveInput!.Copy(); threeProject.BaseReadOnly = true; threeProject.BaseDescription = "共通の祖先";
        three.ApplyProject(threeProject); pump(three.ComparePathsAsync());
        three.StartMergeSession(false);
        Verify("three source leaves share original ancestor for merge", three.CurrentMergeSession is not null && three.LeftEditor.Text == leftText && three.RightEditor.Text == rightText);
        var threeHtml = Path.Combine(folder, "three.html"); pump(three.SaveReportAsync(threeHtml));
        Verify("three source HTML keeps ancestor input", System.Net.WebUtility.HtmlDecode(File.ReadAllText(threeHtml)).Contains("共通の祖先", StringComparison.Ordinal));
        three.DiscardChanges();
        var mixedFile = Path.Combine(folder, "mixed-normal.txt"); File.WriteAllText(mixedFile, rightText, new UTF8Encoding(false));
        var mixed = window.AddSession(); var mixedProject = leaf.CaptureProject(); mixedProject.RightArchiveInput = null; mixedProject.RightPath = mixedFile; mixedProject.RightReadOnly = false;
        mixed.ApplyProject(mixedProject); pump(mixed.ComparePathsAsync());
        Verify("mixed comparison preserves writable ordinary side", mixed.LeftEditor.IsReadOnly && !mixed.RightEditor.IsReadOnly && mixed.LeftEditor.Text == leftText && mixed.RightEditor.Text == rightText);
        mixed.RightEditor.Text = "saved ordinary side\r\n"; pump(mixed.SaveAsync(true));
        Verify("mixed ordinary save cannot write into source root", File.ReadAllText(mixedFile) == "saved ordinary side\r\n" && HashFile(roots[0]) == hashes[0]);
        Activate(innerPane); Select(inner, "empty.txt"); inner.EntryKind.SelectedIndex = 1; pump(inner.OpenSelectedAsync());
        Verify("empty leaves open a valid equal text comparison", window.ActivePane.LeftEditor.Text == "" && window.ActivePane.RightEditor.Text == "" && window.ActivePane.CurrentDiff is { Blocks.Count: 0 });
        Activate(innerPane); Select(inner, "binary.bin"); inner.EntryKind.SelectedIndex = 0; pump(inner.OpenSelectedAsync()); Dispatcher.UIThread.RunJobs();
        var binaryPane = window.ActivePane; var binary = binaryPane.GetVisualDescendants().OfType<SpecializedViews.BinaryPanel>().Single();
        SpecializedViews.SetProjectReadOnly(binary, false, false);
        Verify("auto binary restores inherited editable intention", binaryPane.CaptureProject().Mode == "Binary" && !binary.LeftReadOnly && !binary.RightReadOnly);
        var binaryOutput = Path.Combine(folder, "binary-snapshot.bin"); pump(binary.SaveToAsync(false, binaryOutput));
        Verify("binary snapshot export retains leaf and protects all source roots", File.ReadAllBytes(binaryOutput).SequenceEqual(large)
            && Refused(() => pump(binary.SaveToAsync(false, roots[1]))) && Refused(() => pump(binary.SaveToAsync(false, workspace)))
            && Refused(() => pump(binary.SaveToAsync(false, mixedFile))) && File.ReadAllText(mixedFile) == "saved ordinary side\r\n");
        screenshot("archive-source-binary.png");
        Activate(innerPane);
        var innerTabs = window.SessionPanes.Count; var imageStop = innerPane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止"));
        Select(inner, "frames.gif"); innerPane.ArchiveChildReadStarting = () => imageStop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Canceled(inner.OpenSelectedAsync(), "image child stopped before decode"); innerPane.ArchiveChildReadStarting = null;
        Verify("stopped image child is not adopted", window.SessionPanes.Count == innerTabs && ReferenceEquals(window.ActivePane, innerPane)
            && HashFile(roots[0]) == hashes[0] && HashFile(roots[1]) == hashes[1]);
        ComparisonPane? discardedImage = null;
        innerPane.ArchiveChildReadyForAdoption = candidate => { discardedImage = candidate; imageStop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        Canceled(inner.OpenSelectedAsync(), "image child stopped after decode"); innerPane.ArchiveChildReadyForAdoption = null;
        Verify("stopped completed image candidate is disposed", discardedImage?.IsDisposed == true && window.SessionPanes.Count == innerTabs
            && HashFile(roots[0]) == hashes[0] && HashFile(roots[1]) == hashes[1]);
        innerPane.ArchiveChildReadyForAdoption = candidate => { discardedImage = candidate; pump(inner.RefreshAsync()); };
        Canceled(inner.OpenSelectedAsync(), "image parent refresh supersedes child"); innerPane.ArchiveChildReadyForAdoption = null;
        Verify("refresh generation rejects completed image candidate", discardedImage?.IsDisposed == true && window.SessionPanes.Count == innerTabs
            && ReferenceEquals(window.ActivePane, innerPane) && HashFile(roots[0]) == hashes[0] && HashFile(roots[1]) == hashes[1]);
        innerPane.ArchiveChildReadyForAdoption = candidate => { discardedImage = candidate; ClosePane(innerPane); };
        Canceled(inner.OpenSelectedAsync(), "image parent closed before adoption"); innerPane.ArchiveChildReadyForAdoption = null;
        Verify("closed image parent cancels and disposes completed candidate", innerPane.IsDisposed && discardedImage?.IsDisposed == true
            && window.SessionPanes.Count == innerTabs - 1 && HashFile(roots[0]) == hashes[0] && HashFile(roots[1]) == hashes[1]);
        // 同サイズ・同mtime差替えでは確定した元表示を維持し、次の読込みを拒否する。
        Activate(leaf); var timestamp = File.GetLastWriteTimeUtc(roots[0]); var rootBytes = File.ReadAllBytes(roots[0]);
        var changed = rootBytes.ToArray(); changed[^1] ^= 1; File.WriteAllBytes(roots[0], changed); File.SetLastWriteTimeUtc(roots[0], timestamp);
        var retryCount = 0; leaf.ArchiveSourceRetryShown = dialog => { retryCount++; dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        Canceled(leaf.ComparePathsAsync(), "changed root"); leaf.ArchiveSourceRetryShown = null;
        Verify("changed root retains confirmed text and active child", retryCount == 1 && leaf.LeftEditor.Text == leftText && leaf.RightEditor.Text == rightText
            && ReferenceEquals(window.ActivePane, leaf) && HashFile(roots[0]) == Hash(changed));
        File.WriteAllBytes(roots[0], rootBytes); File.SetLastWriteTimeUtc(roots[0], timestamp);
        // 原本の固定SHAと、異なる二階層のパスワードを本物のGUI経路で確認する。
        using (var resource = typeof(HeadlessArchiveSourceChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.Sources.two-passwords.zip") ?? throw new InvalidOperationException("Source暗号化原本がありません。"))
        {
            var encrypted = Path.Combine(folder, "encrypted-source.zip"); using (var file = File.Create(encrypted)) resource.CopyTo(file);
            Verify("fixed encrypted source bytes", HashFile(encrypted) == "496ED5656BDBC5C165412FFBF25326EF48088AA8A94CC4F8CF2E6AEBE3DA1E6E");
            var encryptedPane = window.AddSession(); encryptedPane.ApplyProject(new() { LeftPath = encrypted, RightPath = encrypted, Mode = "Archive" });
            encryptedPane.ArchiveRetryShown = dialog => { dialog.LeftPassword.Text = dialog.RightPassword.Text = "outer-source-fixture"; dialog.Retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            pump(encryptedPane.ComparePathsAsync()); encryptedPane.ArchiveRetryShown = null;
            var encryptedPanel = encryptedPane.GetVisualDescendants().OfType<ArchivePanel>().Single(); Select(encryptedPanel, "inner.zip"); var rounds = 0;
            encryptedPane.ArchiveSourceRetryShown = dialog =>
            {
                rounds++; Verify("source retry masks each ancestor password", dialog.LeftPasswords.Count == 2 && dialog.RightPasswords.Count == 2
                    && dialog.LeftPasswords.Concat(dialog.RightPasswords).All(box => box.PasswordChar == '●' && box.MaxLength == 4096));
                dialog.LeftPasswords[0].Text = dialog.RightPasswords[0].Text = "outer-source-fixture";
                dialog.LeftPasswords[1].Text = "inner-source-fixture"; dialog.RightPasswords[1].Text = rounds == 1 ? "wrong-test-password" : "inner-source-fixture";
                dialog.Retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            pump(encryptedPanel.OpenSelectedAsync()); encryptedPane.ArchiveSourceRetryShown = null; Dispatcher.UIThread.RunJobs();
            var decryptedPane = window.ActivePane; var decrypted = decryptedPane.GetVisualDescendants().OfType<ArchivePanel>().Single();
            Verify("source two password retries complete one child only", rounds == 2 && decrypted.Rows.Count == 1 && decrypted.Rows[0].Left?.Sha256 == "535E2480D2A0C3A5121F931FB445EC423EE82FAFD33973B5B880FCEF7407489C");
            Select(decrypted, "leaf.txt"); pump(decrypted.OpenSelectedAsync());
            Verify("encrypted child retains ancestor passwords only in memory", window.ActivePane.LeftEditor.Text == "nested archive leaf\r\nUTF-8: 日本\n");
            var secretWorkspace = Path.Combine(folder, "encrypted-workspace.json"); pump(window.SaveWorkspaceAsync(secretWorkspace));
            var saved = File.ReadAllText(secretWorkspace);
            Verify("workspace never saves password or decrypted contents", !saved.Contains("outer-source-fixture", StringComparison.Ordinal) && !saved.Contains("inner-source-fixture", StringComparison.Ordinal)
                && !saved.Contains("wrong-test-password", StringComparison.Ordinal) && !saved.Contains("nested archive leaf", StringComparison.Ordinal));
        }
        Activate(parent); Select(panel, "only.txt"); panel.EntryKind.SelectedIndex = 2;
        pump(panel.OpenSelectedAsync()); Dispatcher.UIThread.RunJobs();
        var onlyPane = window.ActivePane!; var onlyProject = onlyPane.CaptureProject();
        Verify("single-side binary entry retains typed absence", panel.OpenEntryButton.IsEnabled && !ReferenceEquals(onlyPane, parent)
            && onlyProject.Mode == "Binary" && onlyProject.LeftArchiveInput?.LeafEntry == "only.txt"
            && onlyProject.RightArchiveInput?.MissingEntryChain is ["only.txt"] && onlyProject.RightPath == ""
            && onlyProject.LeftReadOnly && onlyProject.RightReadOnly);
        ClosePane(onlyPane); Activate(parent); panel.EntryKind.SelectedIndex = 0;
        // 実在する親と不在の仮想containerを分け、二段先のTextまで実controlで開く。
        var missingLeft = Path.Combine(folder, "missing-left.zip"); var missingRight = Path.Combine(folder, "missing-right.zip");
        File.WriteAllBytes(missingLeft, Zip(("one.zip", Zip(("two.zip", Zip(("leaf.txt", Encoding.UTF8.GetBytes(leftText)), ("empty.txt", [])))))));
        File.WriteAllBytes(missingRight, Zip()); var missingHashes = new[] { HashFile(missingLeft), HashFile(missingRight) };
        var missingParent = window.AddSession(); missingParent.ApplyProject(new() { LeftPath = missingLeft, RightPath = missingRight, Mode = "Archive", LeftReadOnly = true });
        pump(missingParent.ComparePathsAsync());
        var missingPanel = missingParent.GetVisualDescendants().OfType<ArchivePanel>().Single();
        Select(missingPanel, "one.zip"); pump(missingPanel.OpenSelectedAsync()); Dispatcher.UIThread.RunJobs();
        var virtualPane = window.ActivePane; var virtualPanel = virtualPane.GetVisualDescendants().OfType<ArchivePanel>().Single();
        Verify("one-sided archive opens validated empty counterpart", virtualPanel.Rows.Count == 1 && virtualPanel.Rows[0].Right is null
            && virtualPane.CaptureProject().RightArchiveInput?.MissingEntryChain is ["one.zip"]);
        var prohibited = Path.Combine(folder, "missing-export.bin"); File.WriteAllText(prohibited, "keep output"); var prohibitedHash = HashFile(prohibited);
        Verify("virtual archive cannot export repack or extract its real parent", Refused(() => pump(virtualPanel.ExportToAsync(true, "two.zip", prohibited)))
            && Refused(() => pump(virtualPanel.RepackToAsync(true, prohibited))) && Refused(() => pump(virtualPanel.ExtractToAsync(true, folder)))
            && HashFile(prohibited) == prohibitedHash);
        Select(virtualPanel, "two.zip"); pump(virtualPanel.OpenSelectedAsync()); Dispatcher.UIThread.RunJobs();
        var deeperPane = window.ActivePane; var deeperPanel = deeperPane.GetVisualDescendants().OfType<ArchivePanel>().Single();
        Verify("second virtual container retains only actual parent source", deeperPanel.Rows.Count == 2
            && deeperPane.CaptureProject().RightArchiveInput is { EntryChain.Length: 0, MissingEntryChain: ["one.zip", "two.zip"] });
        Select(deeperPanel, "leaf.txt"); deeperPanel.EntryKind.SelectedIndex = 1; pump(deeperPanel.OpenSelectedAsync()); Dispatcher.UIThread.RunJobs();
        var missingText = window.ActivePane; var missingProject = missingText.CaptureProject();
        Verify("virtual two-level child keeps original readonly intention",
            missingProject.LeftArchiveInput?.InheritedReadOnly == true && missingProject.RightArchiveInput?.InheritedReadOnly == false
            && missingProject.LeftReadOnly && missingProject.RightReadOnly);
        Verify("virtual leaf text keeps readonly original and empty editable missing document", missingText.LeftEditor.Text == leftText && missingText.RightEditor.Text == ""
            && missingText.LeftEditor.IsReadOnly && !missingText.RightEditor.IsReadOnly
            && missingProject.RightArchiveInput?.MissingEntryChain is ["one.zip", "two.zip", "leaf.txt"]);
        missingProject.RightArchiveInput!.MissingEntryChain![0] = "changed.zip";
        Verify("missing chain capture is a deep copy", missingText.CaptureProject().RightArchiveInput!.MissingEntryChain![0] == "one.zip");
        var missingHtml = Path.Combine(folder, "missing.html"); pump(missingText.SaveReportAsync(missingHtml));
        Verify("missing GUI report identifies absent side", System.Net.WebUtility.HtmlDecode(File.ReadAllText(missingHtml)).Contains("（存在しない）", StringComparison.Ordinal));
        Verify("missing GUI visible caption identifies absent side", missingText.GetVisualDescendants().OfType<TextBlock>()
            .Any(label => label.Text == "leaf.txt（存在しない）"));
        screenshot("archive-source-missing.png");
        Activate(deeperPane); Select(deeperPanel, "empty.txt"); pump(deeperPanel.OpenSelectedAsync()); Dispatcher.UIThread.RunJobs();
        var emptyMissingPane = window.ActivePane;
        Verify("empty existing leaf remains distinct from absent leaf", emptyMissingPane.LeftEditor.Text == "" && emptyMissingPane.RightEditor.Text == ""
            && emptyMissingPane.CaptureProject().LeftArchiveInput?.MissingEntryChain is null
            && emptyMissingPane.CaptureProject().RightArchiveInput?.MissingEntryChain is ["one.zip", "two.zip", "empty.txt"]);
        ClosePane(emptyMissingPane); ClosePane(deeperPane); ClosePane(virtualPane); ClosePane(missingParent); Activate(missingText);
        Verify("missing leaf remains open after all archive parents close", window.SessionPanes.Contains(missingText)
            && !window.SessionPanes.Contains(missingParent) && !window.SessionPanes.Contains(virtualPane) && !window.SessionPanes.Contains(deeperPane));
        HeadlessArchiveDraftChecks.Run(window, missingText, folder, [missingLeft, missingRight], missingHashes, pump, check, screenshot);
        Verify("missing navigation preserves both physical roots", HashFile(missingLeft) == missingHashes[0] && HashFile(missingRight) == missingHashes[1]);
        ClosePane(missingText); Activate(parent);
        Select(panel, "bad.zip"); pump(panel.PreviewAsync(panel.Rows.Single(row => row.Path == "bad.zip")));
        var stableRows = panel.Rows; var stablePreview = panel.PreviewText; var stableTabs = window.SessionPanes.Count;
        parent.ArchiveSourceRetryShown = dialog => dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Canceled(panel.OpenSelectedAsync(), "invalid inner canceled"); parent.ArchiveSourceRetryShown = null; Observe("invalid-inner-cancel");
        Select(panel, "inner.zip"); pump(panel.PreviewAsync(panel.Rows.Single(row => row.Path == "inner.zip"))); stablePreview = panel.PreviewText;
        var stop = parent.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "中止"));
        parent.ArchiveChildReadStarting = () => stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Canceled(panel.OpenSelectedAsync(), "early stop"); parent.ArchiveChildReadStarting = null; Observe("early-stop");
        ComparisonPane? discarded = null;
        parent.ArchiveChildReadyForAdoption = candidate => { discarded = candidate; stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        Canceled(panel.OpenSelectedAsync(), "late stop"); parent.ArchiveChildReadyForAdoption = null; Observe("late-stop");
        pump(panel.PreviewAsync(panel.Rows.Single(row => row.Path == "inner.zip"))); Observe("preview-after-stop");
        Verify("canceled complete child is disposed", discarded?.IsDisposed == true);
        parent.ArchiveChildReadyForAdoption = candidate => { discarded = candidate; Activate(original); };
        pump(panel.OpenSelectedAsync()); parent.ArchiveChildReadyForAdoption = null;
        Verify("tab switch discards late child without stealing focus", discarded?.IsDisposed == true && window.SessionPanes.Count == stableTabs && ReferenceEquals(window.ActivePane, original));
        Activate(parent); Observe("tab-switch-return"); screenshot("archive-source-cancel.png");
        parent.ArchiveChildReadyForAdoption = candidate => { discarded = candidate; pump(panel.RefreshAsync()); };
        Canceled(panel.OpenSelectedAsync(), "refresh supersedes child"); parent.ArchiveChildReadyForAdoption = null;
        Verify("refresh generation rejects older ready child", discarded?.IsDisposed == true && window.SessionPanes.Count == stableTabs && ReferenceEquals(window.ActivePane, parent));
        Select(panel, "inner.zip"); parent.ArchiveChildReadyForAdoption = candidate => { discarded = candidate; ClosePane(parent); };
        Canceled(panel.OpenSelectedAsync(), "parent closed"); parent.ArchiveChildReadyForAdoption = null;
        Verify("closing parent cancels and disposes prepared child", parent.IsDisposed && discarded?.IsDisposed == true && window.SessionPanes.Count == stableTabs - 1);
        Verify("all failed/canceled requests retain parent display", observations.All(row => row.Rows && row.Preview && row.Active && row.Tabs == stableTabs));
        Verify("Source GUI roots and exported payload remain unchanged", roots.Select(HashFile).SequenceEqual(hashes) && File.ReadAllBytes(export).SequenceEqual(large));
        using (var file = File.Create(Path.Combine(folder, "source-gui-proof.json")))
        using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("leftRootSha256", hashes[0]); writer.WriteString("rightRootSha256", hashes[1]);
            writer.WriteString("exportSha256", Hash(large)); writer.WriteNumber("previewBytes", 4096); writer.WriteNumber("fullLeafBytes", large.Length);
            writer.WriteString("leftText", leftText); writer.WriteString("rightText", rightText); writer.WriteString("html", html); writer.WriteString("threeHtml", threeHtml);
            writer.WriteString("badCrcPath", badCrcPath); writer.WriteString("badCrcSha256", Hash(badCrcBytes));
            writer.WriteStartArray("viewports");
            foreach (var row in viewports) { writer.WriteStartObject(); writer.WriteString("name", row.Name); writer.WriteNumber("width", row.Width); writer.WriteNumber("height", row.Height); writer.WriteNumber("entryListHeight", row.Entries); writer.WriteNumber("previewHeight", row.Preview); writer.WriteBoolean("openEntryReachable", row.Reachable); writer.WriteEndObject(); }
            writer.WriteEndArray();
            writer.WriteStartArray("observations");
            foreach (var row in observations) { writer.WriteStartObject(); writer.WriteString("stage", row.Stage); writer.WriteBoolean("rowsPreserved", row.Rows); writer.WriteBoolean("previewPreserved", row.Preview); writer.WriteBoolean("activeParent", row.Active); writer.WriteNumber("tabs", row.Tabs); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        HeadlessArchiveWorkingTextChecks.Run(window, folder, pump, check, screenshot);
        Activate(original);

        void Verify(string label, bool result, string detail = "real public service, controls and session controller") => check("archive Source GUI " + label, result, detail);
        void Activate(ComparisonPane pane) { window.SelectSession(window.SessionPanes.ToList().IndexOf(pane)); Dispatcher.UIThread.RunJobs(); }
        void Viewport(string name)
        {
            var toolbar = inner.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "archive-toolbar");
            inner.OpenEntryButton.BringIntoView(); Dispatcher.UIThread.RunJobs();
            var point = inner.OpenEntryButton.TranslatePoint(new Point(), toolbar);
            var reachable = point is { } location && location.Y >= -1 && location.Y + inner.OpenEntryButton.Bounds.Height <= toolbar.Viewport.Height + 1;
            var previewHeight = inner.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "archive-preview").Bounds.Height;
            viewports.Add((name, window.Width, window.Height, inner.EntryList.Bounds.Height, previewHeight, reachable));
            screenshot(name == "normal" ? "archive-source-container.png" : "archive-source-minimum.png");
            Verify("entry viewport and open button reachable " + name, inner.EntryList.Bounds.Height >= 40 && previewHeight >= 40 && reachable,
                $"window={window.Width}x{window.Height};entries={inner.EntryList.Bounds.Height};preview={previewHeight};panel={inner.Bounds.Height};toolbarViewport={toolbar.Viewport.Height};buttonY={point?.Y};buttonHeight={inner.OpenEntryButton.Bounds.Height};reachable={reachable}");
        }
        void Observe(string stage) => observations.Add((stage, ReferenceEquals(panel.Rows, stableRows), panel.PreviewText == stablePreview,
            ReferenceEquals(window.ActivePane, parent), window.SessionPanes.Count));
        void Select(ArchivePanel target, string path) { target.EntryList.SelectedItem = target.Rows.Single(row => row.Path == path); Dispatcher.UIThread.RunJobs(); }
        (int Number, int Width, int Height, string PixelSha256)[] FrameProof(ImageComparisonEngine.Snapshot image)
        {
            var frames = new (int Number, int Width, int Height, string PixelSha256)[image.FrameCount];
            for (var number = 1; number <= frames.Length; number++)
            {
                var frame = image.Decode(number, CancellationToken.None);
                frames[number - 1] = (number, frame.Width, frame.Height, ImageComparisonEngine.PixelHash(frame.Pixels, CancellationToken.None));
            }
            return frames;
        }
        bool Matches(ImageComparisonEngine.Snapshot image, IReadOnlyList<string> expected)
        {
            if (image.FrameCount != expected.Count) return false;
            var frames = FrameProof(image);
            return frames.All(frame => frame.Width == 3 && frame.Height == 2)
                && frames.Select(frame => frame.PixelSha256).SequenceEqual(expected, StringComparer.OrdinalIgnoreCase);
        }
        void WriteImageProof(string path, ComparisonProject project, int selectedModeIndex, byte[] leftBytes, byte[] rightBytes,
            IReadOnlyList<(int Number, int Width, int Height, string PixelSha256)> leftFrames,
            IReadOnlyList<(int Number, int Width, int Height, string PixelSha256)> rightFrames)
        {
            using var file = File.Create(path); using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject(); writer.WriteNumber("selectedModeIndex", selectedModeIndex);
            writer.WriteString("selectedMode", selectedModeIndex == 0 ? "Auto" : $"Index{selectedModeIndex}"); writer.WriteString("mode", project.Mode);
            writer.WriteBoolean("leftReadOnly", project.LeftReadOnly); writer.WriteBoolean("rightReadOnly", project.RightReadOnly);
            writer.WriteString("leftRootSha256", project.LeftArchiveInput!.RootSha256); writer.WriteString("rightRootSha256", project.RightArchiveInput!.RootSha256);
            writer.WriteStartArray("leftEntryChain"); foreach (var entry in project.LeftArchiveInput.EntryChain) writer.WriteStringValue(entry); writer.WriteEndArray();
            writer.WriteStartArray("rightEntryChain"); foreach (var entry in project.RightArchiveInput.EntryChain) writer.WriteStringValue(entry); writer.WriteEndArray();
            writer.WriteString("leftLeafEntry", project.LeftArchiveInput.LeafEntry); writer.WriteString("rightLeafEntry", project.RightArchiveInput.LeafEntry);
            writer.WriteString("leftImageSha256", Hash(leftBytes)); writer.WriteString("rightImageSha256", Hash(rightBytes));
            WriteFrames("leftFrames", leftFrames); WriteFrames("rightFrames", rightFrames); writer.WriteEndObject(); writer.Flush();
            void WriteFrames(string name, IReadOnlyList<(int Number, int Width, int Height, string PixelSha256)> frames)
            {
                writer.WriteStartArray(name);
                foreach (var frame in frames)
                { writer.WriteStartObject(); writer.WriteNumber("number", frame.Number); writer.WriteNumber("width", frame.Width); writer.WriteNumber("height", frame.Height); writer.WriteString("pixelSha256", frame.PixelSha256); writer.WriteEndObject(); }
                writer.WriteEndArray();
            }
        }
        void Canceled(Task task, string stage) { var canceled = false; try { pump(task); } catch (OperationCanceledException) { canceled = true; } Verify("cancellation " + stage, canceled); }
        void ClosePane(ComparisonPane pane)
        {
            var tab = window.GetVisualDescendants().OfType<TabControl>().SelectMany(tabs => tabs.Items.OfType<TabItem>()).Single(item => ReferenceEquals(item.Content, pane));
            ((StackPanel)tab.Header!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        }
        async Task WaitRefreshAsync()
        {
            for (var attempt = 0; attempt < 600; attempt++)
            { if (sourceRefreshStarted && leaf.CompareButton.IsEnabled) return; await Task.Delay(5); }
            throw new TimeoutException("Source再比較が完了しませんでした。");
        }
    }
    private static bool Refused(Action action)
    {
        try { action(); return false; }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException or SharpCompress.Common.InvalidFormatException) { return true; }
    }
    private static bool RefusedWithMessage(Action action, string fragment)
    {
        try { action(); return false; }
        catch (InvalidOperationException exception) { return exception.Message.Contains(fragment, StringComparison.Ordinal); }
    }
    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            { var entry = archive.CreateEntry(name, CompressionLevel.NoCompression); entry.LastWriteTime = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero); using var output = entry.Open(); output.Write(bytes); }
        return buffer.ToArray();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string HashFile(string path) => Hash(File.ReadAllBytes(path));
    private static byte[] ImageFixture(string name)
    {
        using var resource = typeof(HeadlessArchiveSourceChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.Images." + name)
            ?? throw new InvalidOperationException("画像検証fixtureがありません: " + name);
        using var buffer = new MemoryStream(); resource.CopyTo(buffer); return buffer.ToArray();
    }
}
