using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static partial class HeadlessBareGZipChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var initialSessionCount = window.SessionPanes.Count();
        var folder = Path.Combine(output, "bare-gzip"); Directory.CreateDirectory(folder);
        var assembly = typeof(HeadlessBareGZipChecks).Assembly;
        using (var resource = assembly.GetManifestResourceStream("DiffBeacon.SelfTest.BareGZip.extra.zip")!)
        using (var zip = new ZipArchive(resource))
            foreach (var entry in zip.Entries)
            { using var input = entry.Open(); using var file = File.Create(Path.Combine(folder, entry.FullName)); input.CopyTo(file); }
        using var expectedResource = assembly.GetManifestResourceStream("DiffBeacon.SelfTest.BareGZip.extra.json")!;
        using var expected = JsonDocument.Parse(expectedResource);
        var text = Convert.FromHexString(expected.RootElement.GetProperty("textHex").GetString()!);
        var binary = Convert.FromHexString(expected.RootElement.GetProperty("binaryHex").GetString()!);
        var nested = Path.Combine(folder, "nested-mixed.gz"); var original = File.ReadAllBytes(nested); var originalSha = Hash(original);
        var source = new ArchiveSource(nested, containerNameCodePages: new[] { 932 }).WithChild("階層.gz").WithNameCodePage(1, 65001);
        var service = new ManagedArchive();
        var resolved = service.ResolveManifest(source);
        check("bare gzip Source choices survive SHA recreation", resolved.Source.RootSha256 == originalSha
            && resolved.Source.ContainerNameCodePages.SequenceEqual(new[] { 932, 65001 }), "concrete portable IDs at each container");
        check("bare gzip Source full entry and prefix", service.ResolveEntry(source, "café.txt", 4096).SequenceEqual(text)
            && service.ResolveEntryPreview(source, "café.txt", maximumBytes: 3).SequenceEqual(text[..3]), "all inner and outer footers before bytes");
        var tail = Path.Combine(folder, "encoded-bad-tail.gz");
        var structural = false;
        try { service.ReadEntryPreview(tail, "階層.bin", maximumBytes: 1); }
        catch (InvalidDataException) { structural = true; }
        check("bare gzip preview validates corrupt tail before name retry", structural, "no prefix publication");
        var registry = BuiltinComparisonProviders.CreateDefault();
        var legacy = Path.Combine(folder, "windows-1252.gz");
        ProviderResult? provider = null;
        pump(ReadProvider());
        check("bare gzip standard provider explicit name decoder", provider is not null && provider.LeftText == provider.RightText
            && provider.LeftText.Contains("Euro-€.bin", StringComparison.Ordinal), "normal provider shares ReadOptions");
        async Task ReadProvider() => provider = await registry.Get("archive").CompareAsync(new(legacy, legacy, "archive", new(1252), new(1252)), CancellationToken.None);
        var invalid = false; try { _ = new ManagedArchive(readOptions: new(1200)); } catch (ArgumentOutOfRangeException) { invalid = true; }
        check("bare gzip name decoder rejects UTF16", invalid, "NUL-terminated byte-compatible encodings only");
        var fallback = Path.Combine(folder, "no-extension"); File.WriteAllBytes(fallback, GZip(binary));
        check("bare gzip WinMerge logical fallback", service.ReadManifest(fallback).Entries.Single().Path == "noname", "local Merge7z overrides SDK defaults");
        var dotOnly = Path.Combine(folder, ".gz"); File.WriteAllBytes(dotOnly, GZip(binary));
        var emptyRejected = false; try { service.ReadManifest(dotOnly); } catch (InvalidDataException) { emptyRejected = true; }
        check("bare gzip empty logical fallback rejected", emptyRejected, "same safe entry-path rules");
        var wrapped = Path.Combine(folder, "ignored.zip.gz");
        using (var zip = new MemoryStream())
        {
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            { using var stream = archive.CreateEntry("a").Open(); stream.Write(binary); }
            var gzip = GZip(zip.ToArray()); gzip[3] |= 8;
            File.WriteAllBytes(wrapped, [.. gzip[..10], .. Enumerable.Repeat((byte)'a', 20 * 1024), 0, .. gzip[10..]]);
        }
        check("bare gzip legacy wrapper ignores long FNAME", service.ReadEntry(wrapped, "a").SequenceEqual(binary), "20KiB ignored name remains within original header/shared path budget");
        var store = new ArchiveWorkingStore();
        var origin = new ArchiveProjectInput { RootPath = nested, EntryChain = new[] { "階層.gz" }, LeafEntry = "café.txt", RootSha256 = originalSha,
            ContainerNameCodePages = new[] { 932, 65001 }, InheritedReadOnly = false };
        store.Save(origin, 0, new() { EntryChain = origin.EntryChain, ContainerNameCodePages = origin.ContainerNameCodePages, LeafEntry = "café.txt", Bytes = text,
            EncodingName = "utf-8", HasBom = true, Sha256 = Hash(text) });
        check("bare gzip working store distinguishes decoder choices", store.Find(resolved.Source, "café.txt")?.Sha256 == Hash(text)
            && store.Find(resolved.Source.WithNameCodePage(1, 28591), "café.txt") is null, "body encoding does not select FNAME decoding");
        CheckWorkingDecoderIdentity(folder, pump, check);
        var limits = new ManagedArchiveLimits(MaximumPathCharacters: 1);
        var budgetRejected = false; try { new ManagedArchive(limits, new(1252)).ReadManifest(legacy); } catch (InvalidDataException) { budgetRejected = true; }
        check("bare gzip raw and decoded names share path budget", budgetRejected, "before allocation/publication");
        var lower = 1; var upper = 1024;
        bool AcceptNameBudget(int characters)
        {
            try { new ManagedArchive(new(MaximumPathCharacters: characters), new(1252)).ReadManifest(legacy); return true; }
            catch (InvalidDataException) { return false; }
        }
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (AcceptNameBudget(middle)) upper = middle; else lower = middle + 1;
        }
        check("bare gzip shared name budget exact boundary", AcceptNameBudget(lower) && !AcceptNameBudget(lower - 1), "raw retention, decoded name, normalized path all share one budget");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); var rejected = false;
            try { service.ResolveManifest(source, cancellationToken: canceled.Token); } catch (OperationCanceledException) { rejected = true; }
            check("bare gzip pre-cancel preserves source", rejected && Hash(File.ReadAllBytes(nested)) == originalSha, "same cancellation contract");
        }
        pane.DiscardChanges(); pane.SelectMode(7); pane.LeftPath.Text = pane.RightPath.Text = nested;
        var retryCount = 0;
        pane.ArchiveRetryShown = dialog =>
        {
            retryCount++;
            check("bare gzip initial precise name retry", dialog.LeftNameCodePage.Parent is not null && dialog.RightNameCodePage.Parent is not null, "before ArchivePanel exists");
            dialog.LeftNameCodePage.SelectedItem = 932; dialog.RightNameCodePage.SelectedItem = 932;
            dialog.Retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        try { pump(pane.ComparePathsAsync()); } finally { pane.ArchiveRetryShown = null; }
        Dispatcher.UIThread.RunJobs();
        var rootPanel = Panel(pane);
        check("bare gzip initial chosen names adopted", retryCount == 1 && rootPanel.Rows.Single().Path == "階層.gz"
            && rootPanel.ConfirmedLeft.ContainerNameCodePages[0] == 932, "successful candidate only");
        var captured = pane.CaptureProject();
        check("bare gzip GUI saved root choice", captured.LeftArchiveInput?.ContainerNameCodePages?.SequenceEqual(new[] { 932 }) == true
            && captured.LeftPath == "" && captured.LeftReadOnly, "typed root keeps root protection");
        var normalPath = Path.Combine(folder, "normal-input.gz"); File.WriteAllBytes(normalPath, GZip(text));
        var normalSha = Hash(File.ReadAllBytes(normalPath));
        var compareStop = pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止")
            && !button.GetVisualAncestors().OfType<ArchivePanel>().Any());
        var pathObservations = new List<(string Side, bool Hook, bool Canceled, bool SamePanel, bool Rows, bool Preview, bool RootProtected)>();
        pump(rootPanel.PreviewAsync(rootPanel.Rows.Single()));
        var rootRows = rootPanel.Rows; var rootPreview = rootPanel.PreviewText;
        foreach (var editingPath in new[] { "", "\0", "https://invalid.example/input.gz" })
        {
            pane.LeftPath.Text = editingPath;
            var actualEditingPath = pane.LeftPath.Text ?? ""; var editing = pane.CaptureProject();
            check("bare gzip editing path does not capture previous root " + editingPath.Length,
                actualEditingPath == editingPath && editing.LeftArchiveInput is null && editing.LeftPath == actualEditingPath,
                "actual textbox string and clone are preserved; empty/NUL/URL-like path never matches confirmed root");
        }
        pane.LeftPath.Text = nested;
        foreach (var changedSide in new[] { "left", "right", "both" })
        {
            pane.LeftPath.Text = changedSide == "right" ? nested : normalPath;
            pane.RightPath.Text = changedSide == "left" ? nested : normalPath;
            var pendingProject = pane.CaptureProject();
            check("bare gzip changed physical paths remain normal " + changedSide,
                (changedSide == "right" || pendingProject.LeftArchiveInput is null && pendingProject.LeftPath == normalPath)
                && (changedSide == "left" || pendingProject.RightArchiveInput is null && pendingProject.RightPath == normalPath),
                "new input is not overwritten by previous confirmed root");
            check("bare gzip unchanged side keeps confirmed decoder " + changedSide,
                (changedSide != "right" || pendingProject.LeftArchiveInput?.ContainerNameCodePages?.SequenceEqual(new[] { 932 }) == true)
                && (changedSide != "left" || pendingProject.RightArchiveInput?.ContainerNameCodePages?.SequenceEqual(new[] { 932 }) == true),
                "mixed physical and typed input retains the unchanged side decoder");
            // 未変更側の確定decoderはSource経路、両側が新pathなら通常Archive経路で取消する。
            var hook = false; var stopped = false;
            Action stopChangedInput = () => { hook = true; compareStop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            if (changedSide == "both") pane.ArchiveReadStarting = stopChangedInput;
            else pane.ArchiveSourceReadStarting = stopChangedInput;
            try { pump(pane.ComparePathsAsync()); } catch (OperationCanceledException) { stopped = true; }
            finally { pane.ArchiveReadStarting = null; pane.ArchiveSourceReadStarting = null; }
            var samePanel = ReferenceEquals(Panel(pane), rootPanel); var sameRows = ReferenceEquals(rootPanel.Rows, rootRows);
            var samePreview = rootPanel.PreviewText == rootPreview; var rootProtected = false;
            try { pane.EnsureProjectOutputWritable(nested); } catch (InvalidOperationException) { rootProtected = true; }
            pathObservations.Add((changedSide, hook, stopped, samePanel, sameRows, samePreview, rootProtected));
            check("bare gzip changed path canceled keeps old root " + changedSide,
                hook && stopped && samePanel && sameRows && samePreview && rootProtected
                && Hash(File.ReadAllBytes(nested)) == originalSha && Hash(File.ReadAllBytes(normalPath)) == normalSha,
                $"hook={hook}; canceled={stopped}; samePanel={samePanel}; rows={sameRows}; preview={samePreview}; oldRootProtected={rootProtected}");
        }
        pane.LeftPath.Text = pane.RightPath.Text = nested;
        var sameRootProject = pane.CaptureProject();
        check("bare gzip same root preserves confirmed decoder", sameRootProject.LeftArchiveInput?.ContainerNameCodePages?.SequenceEqual(new[] { 932 }) == true
            && sameRootProject.RightArchiveInput?.ContainerNameCodePages?.SequenceEqual(new[] { 932 }) == true, "both adopted root decoders captured");
        var sameRootWorkspace = Path.Combine(folder, "same-root-workspace.json");
        pump(WorkspaceStore.SaveWorkspaceAsync(sameRootWorkspace, new() { Entries = new[] { sameRootProject } }));
        ComparisonWorkspace? restoredRootWorkspace = null;
        async Task ReadRootWorkspace() => restoredRootWorkspace = await WorkspaceStore.LoadWorkspaceAsync(sameRootWorkspace);
        pump(ReadRootWorkspace());
        var sessionTabs = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Items.OfType<TabItem>().Any(item => ReferenceEquals(item.Content, pane)));
        var originalTab = sessionTabs.Items.OfType<TabItem>().Single(item => ReferenceEquals(item.Content, pane));
        var restoredTabClosed = false; var switchedTabClosed = false;
        var restoredPane = window.AddSession(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        try
        {
            restoredPane.ApplyProject(restoredRootWorkspace!.Entries[0]); pump(restoredPane.ComparePathsAsync());
            var restoredPanel = Panel(restoredPane);
            check("bare gzip typed root restore preserves decoder", ReferenceEquals(TopLevel.GetTopLevel(restoredPane), window)
                && window.SessionPanes.Contains(restoredPane) && restoredPanel.ConfirmedLeft.ContainerNameCodePages.SequenceEqual(new[] { 932 })
                && restoredPanel.ConfirmedRight.ContainerNameCodePages.SequenceEqual(new[] { 932 }) && restoredPanel.Rows.Single().Path == "階層.gz", "isolated typed pane does not alter normal pane metadata");
            var oldTypedRows = restoredPanel.Rows; var oldTypedPreview = restoredPanel.PreviewText;
            var requested = restoredPane.CaptureProject(); var requestedSHA = new string('0', 64);
            foreach (var input in new[] { requested.LeftArchiveInput!, requested.RightArchiveInput! })
            {
                input.RootSha256 = requestedSHA; input.EntryChain = new[] { "階層.gz" };
                input.ContainerNameCodePages = new[] { 932, 65001 };
            }
            restoredPane.ApplyProject(requested); var pendingTyped = restoredPane.CaptureProject();
            check("bare gzip typed new request not overwritten by old panel",
                new[] { pendingTyped.LeftArchiveInput!, pendingTyped.RightArchiveInput! }.All(input => input.RootPath == nested
                    && input.RootSha256 == requestedSHA && input.EntryChain.SequenceEqual(new[] { "階層.gz" })
                    && input.ContainerNameCodePages!.SequenceEqual(new[] { 932, 65001 })), "same physical root, new SHA/chain/decoder remain requested before adoption");
            var typedHook = false; var typedCanceled = false;
            var typedStop = restoredPane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止")
                && !button.GetVisualAncestors().OfType<ArchivePanel>().Any());
            restoredPane.ArchiveSourceReadStarting = () => { typedHook = true; typedStop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            try { pump(restoredPane.ComparePathsAsync()); } catch (OperationCanceledException) { typedCanceled = true; }
            finally { restoredPane.ArchiveSourceReadStarting = null; }
            var afterTypedCancel = restoredPane.CaptureProject();
            check("bare gzip typed canceled request stays pending", typedHook && typedCanceled && ReferenceEquals(Panel(restoredPane), restoredPanel)
                && ReferenceEquals(restoredPanel.Rows, oldTypedRows) && restoredPanel.PreviewText == oldTypedPreview
                && afterTypedCancel.LeftArchiveInput!.RootSha256 == requestedSHA && afterTypedCancel.RightArchiveInput!.RootSha256 == requestedSHA,
                "old display remains, canceled new typed SHA is never replaced by old confirmed SHA");
            var invalidTyped = WorkspaceStore.CloneProject(restoredRootWorkspace!.Entries[0]);
            invalidTyped.LeftArchiveInput!.RootSha256 = invalidTyped.RightArchiveInput!.RootSha256 = null;
            var requestedBeforeReject = WorkspaceStore.SerializeWorkspace(new() { Entries = new[] { restoredPane.CaptureProject() } });
            var beforeRejectLeft = restoredPanel.ConfirmedLeft; var beforeRejectRight = restoredPanel.ConfirmedRight; var nullRejected = false;
            try { restoredPane.ApplyProject(invalidTyped); } catch (InvalidDataException) { nullRejected = true; }
            check("bare gzip typed null SHA rejected preserves request and panel", nullRejected
                && WorkspaceStore.SerializeWorkspace(new() { Entries = new[] { restoredPane.CaptureProject() } }).SequenceEqual(requestedBeforeReject)
                && ReferenceEquals(Panel(restoredPane), restoredPanel) && ReferenceEquals(restoredPanel.Rows, oldTypedRows)
                && restoredPanel.PreviewText == oldTypedPreview && ReferenceEquals(restoredPanel.ConfirmedLeft, beforeRejectLeft)
                && ReferenceEquals(restoredPanel.ConfirmedRight, beforeRejectRight), "saved typed DTO requires SHA; full requested project and confirmed display remain untouched");
            var correctedRequest = WorkspaceStore.CloneProject(restoredRootWorkspace!.Entries[0]);
            restoredPane.ApplyProject(correctedRequest); var beforeCorrectedAdoption = restoredPane.CaptureProject();
            pump(restoredPane.ComparePathsAsync()); var correctedPanel = Panel(restoredPane); var adoptedCorrected = restoredPane.CaptureProject();
            check("bare gzip typed confirmed SHA retry adopts exact source", !ReferenceEquals(correctedPanel, restoredPanel) && restoredPanel.IsDisposed
                && beforeCorrectedAdoption.LeftArchiveInput!.RootSha256 == originalSha && beforeCorrectedAdoption.RightArchiveInput!.RootSha256 == originalSha
                && correctedPanel.ConfirmedLeft.RootSha256 == originalSha && correctedPanel.ConfirmedRight.RootSha256 == originalSha
                && adoptedCorrected.LeftArchiveInput!.RootSha256 == originalSha && adoptedCorrected.RightArchiveInput!.RootSha256 == originalSha
                && adoptedCorrected.LeftArchiveInput!.ContainerNameCodePages!.SequenceEqual(new[] { 932 })
                && adoptedCorrected.RightArchiveInput!.ContainerNameCodePages!.SequenceEqual(new[] { 932 }), "new successful panel disposes old owner and captures both concrete SHA/decoders immediately");
        }
        finally { restoredTabClosed = CloseOwnedTab(restoredPane); }
        var switchedPane = window.AddSession(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        try
        {
            switchedPane.SelectMode(7); switchedPane.LeftPath.Text = switchedPane.RightPath.Text = nested;
            switchedPane.ArchiveRetryShown = dialog =>
            {
                dialog.LeftNameCodePage.SelectedItem = dialog.RightNameCodePage.SelectedItem = 932;
                dialog.Retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            try { pump(switchedPane.ComparePathsAsync()); } finally { switchedPane.ArchiveRetryShown = null; }
            var previousPanel = Panel(switchedPane); var normalHookReached = false;
            switchedPane.LeftPath.Text = switchedPane.RightPath.Text = normalPath;
            switchedPane.ArchiveReadStarting = () => normalHookReached = true;
            try { pump(switchedPane.ComparePathsAsync()); } finally { switchedPane.ArchiveReadStarting = null; }
            var switchedPanel = Panel(switchedPane);
            check("bare gzip new normal root adopts independently", ReferenceEquals(TopLevel.GetTopLevel(switchedPane), window)
                && window.SessionPanes.Contains(switchedPane) && normalHookReached && previousPanel.IsDisposed
                && ArchivePaths.SameFile(switchedPanel.ConfirmedLeft.RootPath, normalPath) && ArchivePaths.SameFile(switchedPanel.ConfirmedRight.RootPath, normalPath)
                && switchedPanel.ConfirmedLeft.ContainerNameCodePages[0] == 28591 && switchedPanel.ConfirmedRight.ContainerNameCodePages[0] == 28591
                && service.ResolveEntry(switchedPanel.ConfirmedLeft, switchedPanel.Rows.Single().Path, 4096).SequenceEqual(text),
                "new root and default decoder replace old confirmed name/source; original pane stays normal");
        }
        finally { switchedTabClosed = CloseOwnedTab(switchedPane); }
        check("bare gzip original normal pane remains unchanged", restoredTabClosed && switchedTabClosed && ReferenceEquals(window.ActivePane, pane)
            && ReferenceEquals(Panel(pane), rootPanel) && ReferenceEquals(rootPanel.Rows, rootRows) && rootPanel.PreviewText == rootPreview,
            $"restoredTabClosed={restoredTabClosed}; switchedTabClosed={switchedTabClosed}; original tab and confirmed panel retained");
        bool CloseOwnedTab(ComparisonPane owned)
        {
            var tab = sessionTabs.Items.OfType<TabItem>().Single(item => ReferenceEquals(item.Content, owned));
            var close = ((StackPanel)tab.Header!).Children.OfType<Button>().Single(button => Equals(button.Content, "×"));
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            sessionTabs.SelectedItem = originalTab; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            return owned.IsDisposed && !window.SessionPanes.Contains(owned) && !sessionTabs.Items.OfType<TabItem>().Contains(tab)
                && ReferenceEquals(sessionTabs.SelectedItem, originalTab) && ReferenceEquals(window.ActivePane, pane);
        }
        using (var pathProof = File.Create(Path.Combine(folder, "confirmed-root-path-proof.json")))
        using (var pathWriter = new Utf8JsonWriter(pathProof, new JsonWriterOptions { Indented = true }))
        {
            pathWriter.WriteStartObject(); pathWriter.WriteString("oldRoot", nested); pathWriter.WriteString("oldRootSHA", originalSha);
            pathWriter.WriteString("newRoot", normalPath); pathWriter.WriteString("newRootSHA", normalSha); pathWriter.WriteString("workspace", sameRootWorkspace);
            pathWriter.WriteBoolean("restoredTabClosed", restoredTabClosed); pathWriter.WriteBoolean("switchedTabClosed", switchedTabClosed);
            pathWriter.WriteBoolean("originalTabActive", ReferenceEquals(window.ActivePane, pane));
            pathWriter.WriteStartArray("observations"); foreach (var item in pathObservations)
            {
                pathWriter.WriteStartObject(); pathWriter.WriteString("changedSide", item.Side); pathWriter.WriteBoolean("hookReached", item.Hook);
                pathWriter.WriteBoolean("canceled", item.Canceled); pathWriter.WriteBoolean("samePanel", item.SamePanel);
                pathWriter.WriteBoolean("rowsPreserved", item.Rows); pathWriter.WriteBoolean("previewPreserved", item.Preview);
                pathWriter.WriteBoolean("oldRootProtected", item.RootProtected); pathWriter.WriteEndObject();
            }
            pathWriter.WriteEndArray(); pathWriter.WriteEndObject();
        }
        rootPanel.EntryList.SelectedItem = rootPanel.Rows.Single(); rootPanel.EntryKind.SelectedIndex = 3;
        pump(rootPanel.OpenSelectedAsync()); Dispatcher.UIThread.RunJobs();
        var child = window.ActivePane; var panel = Panel(child);
        panel.LeftNameCodePage.SelectedItem = panel.RightNameCodePage.SelectedItem = 65001;
        Task? refresh = null; panel.ButtonTaskObserved = (_, task) => refresh = task;
        panel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "アーカイブを再比較")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (refresh is not null) pump(refresh); Dispatcher.UIThread.RunJobs(); panel.ButtonTaskObserved = null;
        check("bare gzip real panel recompare retains ancestors", panel.Rows.Single().Path == "café.txt"
            && panel.ConfirmedLeft.ContainerNameCodePages.SequenceEqual(new[] { 932, 65001 }), "valid mojibake corrected after panel exists");
        pump(panel.PreviewAsync(panel.Rows.Single()));
        var rows = panel.Rows; var preview = panel.PreviewText; var confirmed = panel.ConfirmedLeft;
        panel.RefreshReadyForAdoption = () => panel.LeftNameCodePage.SelectedItem = 28591;
        try { pump(panel.RefreshAsync()); } finally { panel.RefreshReadyForAdoption = null; }
        check("bare gzip stale settings preserve confirmed display", ReferenceEquals(panel.Rows, rows) && panel.PreviewText == preview
            && ReferenceEquals(panel.ConfirmedLeft, confirmed), "pending setting changes never enter captured project");
        panel.LeftNameCodePage.SelectedItem = 65001;
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); try { pump(panel.RefreshAsync(canceled.Token)); } catch (OperationCanceledException) { }
        }
        check("bare gzip canceled refresh preserves display", ReferenceEquals(panel.Rows, rows) && panel.PreviewText == preview
            && ReferenceEquals(panel.ConfirmedLeft, confirmed), "previous decoder remains confirmed");
        File.WriteAllBytes(nested, original[..^3]); var failed = false;
        try { pump(panel.RefreshAsync()); } catch (InvalidDataException) { failed = true; }
        finally { File.WriteAllBytes(nested, original); }
        check("bare gzip failed refresh preserves display", failed && ReferenceEquals(panel.Rows, rows) && panel.PreviewText == preview
            && ReferenceEquals(panel.ConfirmedLeft, confirmed), "failed source SHA/structure cannot adopt");
        var childProject = child.CaptureProject();
        var protectedRoot = false; try { pump(panel.ExportToAsync(false, "café.txt", nested)); } catch (IOException) { protectedRoot = true; }
        check("bare gzip GUI export protects physical root", protectedRoot && Hash(File.ReadAllBytes(nested)) == originalSha, "readonly Source and all-tab output guards");
        var bytes = WorkspaceStore.SerializeWorkspace(new() { Entries = new[] { childProject } });
        using var saved = JsonDocument.Parse(bytes);
        check("bare gzip GUI v7 preserves all container choices", saved.RootElement.GetProperty("formatVersion").GetInt32() == 7
            && childProject.LeftArchiveInput?.ContainerNameCodePages?.SequenceEqual(new[] { 932, 65001 }) == true, "adopted settings only");
        CheckWriting(window, pane, child, folder, pump, check, screenshot);
        screenshot("bare-gzip-confirmed.png");
        var savedCodePages = confirmed.ContainerNameCodePages.ToArray(); var savedEntry = panel.Rows.Single().Path;
        var savedRowsPreserved = ReferenceEquals(panel.Rows, rows); var savedPreviewPreserved = panel.PreviewText == preview;
        child.DiscardChanges(); var childTabClosed = CloseOwnedTab(child);
        pane.DiscardChanges(); pane.SelectMode(0);
        using (var proofFile = File.Create(Path.Combine(folder, "ui-proof.json")))
        using (var proofWriter = new Utf8JsonWriter(proofFile, new JsonWriterOptions { Indented = true }))
        {
            proofWriter.WriteStartObject(); proofWriter.WriteString("rootSha256", originalSha); proofWriter.WriteString("leafSha256", Hash(text));
            proofWriter.WriteNumber("retryCount", retryCount); proofWriter.WriteStartArray("containerNameCodePages");
            foreach (var codePage in savedCodePages) proofWriter.WriteNumberValue(codePage);
            proofWriter.WriteEndArray(); proofWriter.WriteString("entry", savedEntry);
            proofWriter.WriteBoolean("rowsPreserved", savedRowsPreserved); proofWriter.WriteBoolean("previewPreserved", savedPreviewPreserved);
            proofWriter.WriteBoolean("childTabClosed", childTabClosed);
            proofWriter.WriteNumber("initialSessionCount", initialSessionCount); proofWriter.WriteNumber("finalSessionCount", window.SessionPanes.Count());
            proofWriter.WriteEndObject();
        }
        check("bare gzip UI root preserved", Hash(File.ReadAllBytes(nested)) == originalSha && childTabClosed
            && child.IsDisposed && !window.SessionPanes.Contains(child) && ReferenceEquals(window.ActivePane, pane)
            && window.SessionPanes.Count() == initialSessionCount,
            "original child closed through session header; original tab restored and physical root retained");
        CheckPayloadKinds(window, pane, folder, nested, pump, check, screenshot);
    }

    private static void CheckPayloadKinds(MainWindow window, ComparisonPane pane, string folder, string nested,
        Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        foreach (var (name, bytes) in new[] { ("payload-zero512.gz", new byte[512]), ("payload-misnamed.tar.gz", "plain body"u8.ToArray()) })
        {
            var initialPath = Path.Combine(folder, name); File.WriteAllBytes(initialPath, GZip(bytes));
            pane.DiscardChanges(); pane.SelectMode(7); pane.LeftPath.Text = pane.RightPath.Text = initialPath;
            var retries = 0;
            pane.ArchiveRetryShown = dialog =>
            {
                retries++;
                dialog.LeftGZipPayloadKind.SelectedItem = dialog.RightGZipPayloadKind.SelectedItem = GZipPayloadKind.File;
                dialog.Retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            try { pump(pane.ComparePathsAsync()); } finally { pane.ArchiveRetryShown = null; }
            Dispatcher.UIThread.RunJobs();
            var initialPanel = Panel(pane); var initialCapture = pane.CaptureProject();
            check("gzip payload initial File retry " + name, retries == 1 && initialPanel.Rows.Count == 1
                && initialCapture.LeftArchiveInput?.ContainerGZipPayloadKinds?.SequenceEqual(new[] { "File" }) == true
                && initialCapture.RightArchiveInput?.ContainerGZipPayloadKinds?.SequenceEqual(new[] { "File" }) == true,
                "real first-failure retry button adopts File and captures both sides");
        }
        var root = Path.Combine(folder, "payload-kind.txt.gz"); var body = "payload kind\r\n"u8.ToArray();
        File.WriteAllBytes(root, GZip(body)); var rootHash = Hash(File.ReadAllBytes(root));
        pane.DiscardChanges(); pane.SelectMode(7); pane.LeftPath.Text = pane.RightPath.Text = root;
        pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
        var panel = Panel(pane);
        void ClickRefresh()
        {
            Task? operation = null; panel.ButtonTaskObserved = (_, task) => operation = task;
            try
            {
                panel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "アーカイブを再比較"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (operation is not null) pump(operation);
                Dispatcher.UIThread.RunJobs();
            }
            finally { panel.ButtonTaskObserved = null; }
        }
        panel.LeftGZipPayloadKind.SelectedItem = GZipPayloadKind.File; ClickRefresh();
        check("gzip payload left independent adopted choice", panel.ConfirmedLeft.ContainerGZipPayloadKinds[0] == GZipPayloadKind.File
            && panel.ConfirmedRight.ContainerGZipPayloadKinds[0] == GZipPayloadKind.Auto, "real left picker and refresh button");
        panel.RightGZipPayloadKind.SelectedItem = GZipPayloadKind.File; ClickRefresh();
        check("gzip payload right independent adopted choice", panel.ConfirmedLeft.ContainerGZipPayloadKinds[0] == GZipPayloadKind.File
            && panel.ConfirmedRight.ContainerGZipPayloadKinds[0] == GZipPayloadKind.File, "real right picker and refresh button");
        pump(panel.PreviewAsync(panel.Rows.Single()));
        var rows = panel.Rows; var preview = panel.PreviewText; var confirmed = panel.ConfirmedLeft;
        bool Retained() => ReferenceEquals(panel.Rows, rows) && panel.PreviewText == preview && ReferenceEquals(panel.ConfirmedLeft, confirmed)
            && pane.CaptureProject().LeftArchiveInput?.ContainerGZipPayloadKinds?.SequenceEqual(new[] { "File" }) == true;
        panel.LeftGZipPayloadKind.SelectedItem = GZipPayloadKind.Auto;
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); try { pump(panel.RefreshAsync(canceled.Token)); } catch (OperationCanceledException) { }
        }
        check("gzip payload canceled choice retains display and capture", Retained(), "pending Auto is never captured");
        panel.LeftGZipPayloadKind.SelectedItem = GZipPayloadKind.File;
        panel.RefreshReadyForAdoption = () =>
        {
            panel.LeftGZipPayloadKind.SelectedItem = GZipPayloadKind.Auto;
            panel.LeftGZipPayloadKind.SelectedItem = GZipPayloadKind.File;
        };
        try { pump(panel.RefreshAsync()); } catch (OperationCanceledException) { }
        finally { panel.RefreshReadyForAdoption = null; }
        check("gzip payload stale reverted picker retains display and capture", Retained(), "generation rejects change-and-revert during completion");
        panel.LeftGZipPayloadKind.SelectedItem = GZipPayloadKind.Tar;
        var rejected = false; try { pump(panel.RefreshAsync()); } catch (InvalidDataException) { rejected = true; }
        check("gzip payload failed Tar retains display and capture", rejected && Retained(), "plain body rejects explicit Tar");
        panel.LeftGZipPayloadKind.SelectedItem = GZipPayloadKind.File;
        var captured = pane.CaptureProject(); var workspace = WorkspaceStore.SerializeWorkspace(new() { Entries = new[] { captured } });
        using var document = JsonDocument.Parse(workspace);
        check("gzip payload GUI nonAuto selects v8", document.RootElement.GetProperty("formatVersion").GetInt32() == 8
            && captured.LeftArchiveInput!.ContainerGZipPayloadKinds!.SequenceEqual(new[] { "File" }), "confirmed choices only");
        var workspacePath = Path.Combine(folder, "payload-kind-workspace.json"); File.WriteAllBytes(workspacePath, workspace);
        ComparisonWorkspace? loaded = null; async Task Reload() => loaded = await WorkspaceStore.LoadWorkspaceAsync(workspacePath);
        pump(Reload());
        check("gzip payload workspace reload preserves selected mode", loaded!.FormatVersion == 8
            && loaded.Entries[0].LeftArchiveInput!.ToSource().ContainerGZipPayloadKinds[0] == GZipPayloadKind.File, "strict string enum DTO roundtrip");
        var requested = new ComparisonProject { Mode = "Archive", LeftReadOnly = true, RightReadOnly = true,
            LeftArchiveInput = new() { RootPath = nested, EntryChain = new[] { "階層.gz" }, RootSha256 = Hash(File.ReadAllBytes(nested)), ContainerNameCodePages = new[] { 932, 65001 } },
            RightArchiveInput = new() { RootPath = nested, EntryChain = new[] { "階層.gz" }, RootSha256 = Hash(File.ReadAllBytes(nested)), ContainerNameCodePages = new[] { 932, 65001 } } };
        var retry = new ArchiveSourceRetryDialog(requested, [new string?[2], new string?[1], new string?[2]]);
        retry.LeftPayloadKinds[0].SelectedItem = GZipPayloadKind.File; retry.RightPayloadKinds[1].SelectedItem = GZipPayloadKind.File;
        retry.ApplyNameChoices(requested); retry.ClearPasswords();
        check("gzip payload Source retry independent layers", requested.LeftArchiveInput!.ContainerGZipPayloadKinds!.SequenceEqual(new[] { "File", "Auto" })
            && requested.RightArchiveInput!.ContainerGZipPayloadKinds!.SequenceEqual(new[] { "Auto", "File" }), "side and depth are independent");
        var childSource = requested.LeftArchiveInput.ToSource().WithChild("new.gz");
        check("gzip payload new child starts Auto", childSource.ContainerGZipPayloadKinds.SequenceEqual(new[] { GZipPayloadKind.File, GZipPayloadKind.Auto, GZipPayloadKind.Auto }), "outer selections survive");
        var source = new ManagedArchive().ResolveManifest(new ArchiveSource(root)).Source;
        var leaf = panel.Rows.Single().Path;
        var origin = new ArchiveProjectInput { RootPath = root, RootSha256 = rootHash, LeafEntry = leaf };
        var store = new ArchiveWorkingStore();
        var savedAuto = body.Concat("AUTO-WORKING-V1\r\n"u8.ToArray()).ToArray();
        var savedFile = body.Concat("FILE-WORKING-V1\r\n"u8.ToArray()).ToArray();
        ArchiveWorkingSnapshot Snapshot(string[]? modes, byte[] bytes) => new() { LeafEntry = leaf, Bytes = bytes, EncodingName = "utf-8", Sha256 = Hash(bytes), ContainerGZipPayloadKinds = modes };
        store.Save(origin, 0, Snapshot(null, savedAuto));
        var autoAliasRevision = store.Revision(origin with { ContainerGZipPayloadKinds = new[] { "Auto" } });
        var autoBeforeFile = store.Find(source, leaf);
        check("gzip payload null and allAuto share working identity", autoAliasRevision == store.Revision(origin) && autoAliasRevision > 0
            && autoBeforeFile?.Bytes?.SequenceEqual(savedAuto) == true && autoBeforeFile.Sha256 == Hash(savedAuto), "validated default normalization and saved Auto bytes");
        var fileOrigin = origin with { ContainerGZipPayloadKinds = new[] { "File" } };
        var fileSource = source.WithGZipPayloadKind(0, GZipPayloadKind.File);
        var fileMissingBeforeSave = store.Revision(fileOrigin) == 0 && store.Find(fileSource, leaf) is null;
        check("gzip payload different working mode has no old bytes", fileMissingBeforeSave, "same root, chain, leaf and codepage");
        store.Save(fileOrigin, 0, Snapshot(new[] { "File" }, savedFile));
        var actualAuto = store.Find(source, leaf); var actualFile = store.Find(fileSource, leaf);
        var autoRevision = store.Revision(origin); var fileRevision = store.Revision(fileOrigin);
        check("gzip payload different working modes remain separate", actualAuto?.Bytes?.SequenceEqual(savedAuto) == true && actualAuto.Sha256 == Hash(savedAuto)
            && actualFile?.Bytes?.SequenceEqual(savedFile) == true && actualFile.Sha256 == Hash(savedFile)
            && autoRevision > 0 && fileRevision > 0 && fileRevision != autoRevision, "distinct saved bytes, SHA and positive revisions");
        var autoBytes = actualAuto?.Bytes ?? throw new InvalidDataException("Autoの作業保存版を取得できません。");
        var fileBytes = actualFile?.Bytes ?? throw new InvalidDataException("Fileの作業保存版を取得できません。");
        var autoByteFilename = "payload-kind-working-auto.bin"; var fileByteFilename = "payload-kind-working-file.bin";
        File.WriteAllBytes(Path.Combine(folder, autoByteFilename), autoBytes);
        File.WriteAllBytes(Path.Combine(folder, fileByteFilename), fileBytes);
        using (var proof = File.Create(Path.Combine(folder, "payload-kind-working-identity-proof.json")))
        using (var writer = new Utf8JsonWriter(proof, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("rootPath", source.RootPath); writer.WriteString("rootSha256", source.RootSha256); writer.WriteString("leaf", actualAuto!.LeafEntry);
            writer.WriteStartArray("codePages"); foreach (var codePage in source.ContainerNameCodePages) writer.WriteNumberValue(codePage); writer.WriteEndArray();
            writer.WriteNumber("autoAliasRevision", autoAliasRevision); writer.WriteBoolean("fileMissingBeforeSave", fileMissingBeforeSave);
            writer.WriteStartArray("variants");
            foreach (var (route, revision, filename, snapshot) in new[] { (source, autoRevision, autoByteFilename, actualAuto!), (fileSource, fileRevision, fileByteFilename, actualFile!) })
            {
                writer.WriteStartObject(); writer.WriteStartArray("choices");
                foreach (var choice in route.ContainerGZipPayloadKinds) writer.WriteStringValue(choice.ToString()); writer.WriteEndArray();
                writer.WriteNumber("actualRevision", revision); writer.WriteString("actualByteFilename", filename);
                writer.WriteString("sha256", snapshot.Sha256); writer.WriteNumber("length", snapshot.Bytes!.Length); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        foreach (var invalid in new[] { Array.Empty<string>(), new[] { "Auto", "Auto" }, new[] { "auto" }, new[] { "1" } })
        {
            rejected = false; try { _ = (origin with { ContainerGZipPayloadKinds = invalid }).Copy(); } catch (InvalidDataException) { rejected = true; }
            check("gzip payload Copy rejects invalid choices " + string.Join(',', invalid), rejected, "before allAuto/null normalization");
        }
        var savedWidth = window.Width; var savedHeight = window.Height;
        using (var proof = File.Create(Path.Combine(folder, "payload-kind-bounds.json")))
        using (var writer = new Utf8JsonWriter(proof, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var size in new[] { (1280d, 850d), (850d, 550d) })
            {
                window.Width = size.Item1; window.Height = size.Item2; Dispatcher.UIThread.RunJobs();
                var toolbar = panel.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "archive-toolbar");
                foreach (var control in new Control[] { panel.LeftNameCodePage, panel.RightNameCodePage, panel.LeftGZipPayloadKind, panel.RightGZipPayloadKind })
                {
                    control.BringIntoView(); Dispatcher.UIThread.RunJobs(); Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    var point = control.TranslatePoint(default, toolbar)!.Value;
                    check($"gzip payload picker reachable {size.Item1}x{size.Item2} {control.Name}", control.Bounds.Height > 0
                        && point.Y >= -.01 && point.Y + control.Bounds.Height <= toolbar.Viewport.Height + .01, "scrollable toolbar");
                    writer.WriteStartObject(); writer.WriteString("control", control.Name); writer.WriteNumber("width", size.Item1); writer.WriteNumber("height", size.Item2);
                    writer.WriteNumber("y", point.Y); writer.WriteNumber("pickerHeight", control.Bounds.Height); writer.WriteNumber("viewportHeight", toolbar.Viewport.Height); writer.WriteEndObject();
                }
                screenshot($"gzip-payload-{size.Item1}x{size.Item2}.png");
            }
            writer.WriteEndArray();
        }
        window.Width = savedWidth; window.Height = savedHeight; Dispatcher.UIThread.RunJobs();
        check("gzip payload original retained", Hash(File.ReadAllBytes(root)) == rootHash, "all picker operations preserve input");
        pane.DiscardChanges(); pane.SelectMode(0);
    }

    private static ArchivePanel Panel(ComparisonPane pane) => pane.GetVisualDescendants().OfType<ArchivePanel>().Single();

    private static void CheckWorkingDecoderIdentity(string parent, Action<Task> pump, Action<string, bool, string> check)
    {
        // 同じroot/chain/leafの二版をStoreから捕捉し、旧DTOの重複判定だけが衝突する条件を作る。
        var folder = Path.Combine(parent, "working-decoder-identity"); Directory.CreateDirectory(folder);
        var original = "\uFEFForiginal ascii\r\n"u8.ToArray();
        var first = "\uFEFFsaved with decoder 932\r\n"u8.ToArray();
        var second = "\uFEFFsaved with decoder 65001\r\n"u8.ToArray();
        var root = Path.Combine(folder, "root.zip"); var gzip = GZip(original); gzip[3] |= 8;
        byte[] named = [.. gzip[..10], .. "a.txt"u8, 0, .. gzip[10..]];
        using (var archive = new ZipArchive(File.Create(root), ZipArchiveMode.Create))
        { using var entry = archive.CreateEntry("a.txt.gz").Open(); entry.Write(named); }
        var rootHash = Hash(File.ReadAllBytes(root)); var store = new ArchiveWorkingStore();
        foreach (var (codePage, bytes) in new[] { (932, first), (65001, second) })
        {
            var input = new ArchiveProjectInput { RootPath = root, RootSha256 = rootHash, EntryChain = new[] { "a.txt.gz" }, LeafEntry = "a.txt",
                ContainerNameCodePages = new[] { 28591, codePage }, InheritedReadOnly = false };
            store.Save(input, 0, new() { EntryChain = input.EntryChain.ToArray(), LeafEntry = input.LeafEntry!, ContainerNameCodePages = input.ContainerNameCodePages!.ToArray(),
                Bytes = bytes, Sha256 = Hash(bytes), EncodingName = "utf-8", HasBom = true });
            check("bare gzip working decoder independent bytes " + codePage, store.Find(input.ToSource(), "a.txt")?.Bytes?.SequenceEqual(bytes) == true,
                "same physical root, chain and leaf; independent revision/save bytes");
        }
        var captured = store.Capture(new() { RootPath = root, RootSha256 = rootHash, InheritedReadOnly = false });
        var snapshots = captured.WorkingDocuments!;
        var legacyKeyCount = snapshots.Select(copy => string.Concat(copy.EntryChain.Append(copy.LeafEntry)
            .Select(ArchiveProjectInput.CanonicalEntry).Select(part => part.Length + ":" + part))).Distinct(StringComparer.Ordinal).Count();
        check("bare gzip working DTO old-key collision precondition", snapshots.Length == 2 && legacyKeyCount == 1,
            "real Store capture; old chain/leaf-only DTO rule would reject this workspace");
        ComparisonWorkspace Workspace(ArchiveProjectInput input) => new() { Entries = new[] { new ComparisonProject
        { Mode = "Archive", LeftArchiveInput = input.Copy(), RightArchiveInput = input.Copy(), LeftReadOnly = true, RightReadOnly = true } } };
        var workspacePath = Path.Combine(folder, "workspace.json"); pump(WorkspaceStore.SaveWorkspaceAsync(workspacePath, Workspace(captured)));
        ComparisonWorkspace? restored = null;
        async Task Read(string path) => restored = await WorkspaceStore.LoadWorkspaceAsync(path);
        pump(Read(workspacePath));
        check("bare gzip working DTO distinct decoders save and reload", restored!.FormatVersion == 7
            && restored.Entries[0].LeftArchiveInput!.WorkingDocuments!.Length == 2
            && restored.Entries[0].LeftArchiveInput!.WorkingDocuments!.All(copy => copy.Bytes!.SequenceEqual(copy.ContainerNameCodePages![1] == 932 ? first : second)),
            "both concrete codepages and full saved bytes");
        var shared = captured.Copy(); shared.WorkingDocuments = snapshots.Select(copy => copy.Copy() with { Bytes = first, Sha256 = Hash(first) }).ToArray();
        var sharedPath = Path.Combine(folder, "shared-workspace.json"); pump(WorkspaceStore.SaveWorkspaceAsync(sharedPath, Workspace(shared))); pump(Read(sharedPath));
        var sharedCopies = restored!.Entries[0].LeftArchiveInput!.WorkingDocuments!;
        check("bare gzip working DTO identical bytes share content asset", sharedCopies.Length == 2 && sharedCopies.Select(copy => copy.SnapshotPath).Distinct(StringComparer.Ordinal).Count() == 1
            && sharedCopies.All(copy => copy.Bytes!.SequenceEqual(first)), "different decoder identities retain safe SHA-addressed asset sharing");
        var priorWorkspace = Hash(File.ReadAllBytes(workspacePath)); var generation = store.Generation;
        foreach (var defaultAlias in new[] { false, true })
        {
            var duplicate = captured.Copy();
            duplicate.WorkingDocuments = defaultAlias
                ? new[] { snapshots[0].Copy() with { ContainerNameCodePages = null }, snapshots[0].Copy() with { ContainerNameCodePages = new[] { 28591, 28591 } } }
                : new[] { snapshots[0].Copy(), snapshots[0].Copy() };
            var rejected = false;
            try { pump(WorkspaceStore.SaveWorkspaceAsync(workspacePath, Workspace(duplicate))); } catch (InvalidDataException) { rejected = true; }
            check("bare gzip working DTO duplicate rejected " + (defaultAlias ? "null-default-alias" : "same-decoder"), rejected
                && Hash(File.ReadAllBytes(workspacePath)) == priorWorkspace && store.Generation == generation, "previous workspace and saved revisions retained");
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); var rejected = false;
            try { pump(WorkspaceStore.SaveWorkspaceAsync(workspacePath, Workspace(captured), canceled.Token)); } catch (OperationCanceledException) { rejected = true; }
            check("bare gzip working DTO canceled save retained", rejected && Hash(File.ReadAllBytes(workspacePath)) == priorWorkspace && store.Generation == generation,
                "no change to asset identity, capacity or saved revisions");
        }
        using (var proofFile = File.Create(Path.Combine(folder, "identity-proof.json")))
        using (var proofWriter = new Utf8JsonWriter(proofFile, new JsonWriterOptions { Indented = true }))
        {
            proofWriter.WriteStartObject(); proofWriter.WriteString("rootZip", root); proofWriter.WriteString("rootSha256", rootHash);
            proofWriter.WriteString("originalPayloadHex", Convert.ToHexString(original)); proofWriter.WriteString("workspace", workspacePath); proofWriter.WriteString("sharedWorkspace", sharedPath);
            proofWriter.WriteNumber("legacyKeyCount", legacyKeyCount); proofWriter.WriteNumber("snapshotCount", snapshots.Length);
            proofWriter.WriteStartArray("variants");
            foreach (var (codePage, bytes) in new[] { (932, first), (65001, second) })
            {
                proofWriter.WriteStartObject(); proofWriter.WriteNumber("codePage", codePage); proofWriter.WriteString("payloadHex", Convert.ToHexString(bytes)); proofWriter.WriteEndObject();
            }
            proofWriter.WriteEndArray(); proofWriter.WriteEndObject();
        }
        check("bare gzip working DTO original retained", Hash(File.ReadAllBytes(root)) == rootHash, "same original entry with two explicit decoder choices");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] GZip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }
}
