using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

// 固定原本・全byte・実button・採用直前の世代を外部reader用の証跡へ残す。
internal static partial class HeadlessBareCompressionChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "bare-compression"); Directory.CreateDirectory(folder);
        var observations = new List<(string Id, bool Passed, string Detail)>();
        void Verify(string id, bool passed, string detail)
        { observations.Add((id, passed, detail)); check("bare compression " + id, passed, detail); }
        void Jobs() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        string Input(string name) => Path.Combine(folder, name);
        // 共有paneの設定・寸法は成功と失敗の両方で元へ戻す。子比較は専用windowに隔離する。
        var originalProject = pane.CaptureProject(); var originalWidth = window.Width; var originalHeight = window.Height;
        var originalCompareEnabled = pane.CompareButton.IsEnabled; var originalDirty = pane.HasUnsavedChanges;
        if (originalDirty || !originalCompareEnabled) throw new InvalidOperationException("bare compression検証は未保存編集と進行中操作のないpaneで開始してください。");
        WriteProject(Input("shared-project-before.json"), originalProject);
        Exception? primaryFailure = null;
        try
        {
        using var resource = typeof(HeadlessBareCompressionChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.BareCompression.inputs.zip")!;
        Verify("fixed-resource-zip", Convert.ToHexString(SHA256.HashData(resource)) == "CA8BABE9D94D1584BBEAE908516870EF17BCD5DB8A26941193D4323CEC4A91FC", "exact immutable fixture ZIP"); resource.Position = 0;
        using (var zip = new ZipArchive(resource))
            foreach (var entry in zip.Entries)
            { using var source = entry.Open(); using var target = File.Create(Input(entry.FullName)); source.CopyTo(target); }
        using var oracle = typeof(HeadlessBareCompressionChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.BareCompression.expected.json")!;
        Verify("fixed-resource-expected", Convert.ToHexString(SHA256.HashData(oracle)) == "C606FD0E63C6D62AFD3AC220CC023D1E2D8ECC07ECF4E741AF55EF13E4022DA6", "exact immutable independent byte oracle"); oracle.Position = 0;
        using var expected = JsonDocument.Parse(oracle);
        File.WriteAllText(Input("expected.json"), expected.RootElement.GetRawText());
        var cases = expected.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var hashes = Directory.EnumerateFiles(folder).ToDictionary(file => Path.GetFileName(file)!, file => Hash(File.ReadAllBytes(file)));
        var service = new ManagedArchive(readOptions: new(CompressionPayloadKind: CompressionPayloadKind.File));
        foreach (var spec in cases.Where(item => item.GetProperty("valid").GetBoolean()))
        {
            var name = spec.GetProperty("input").GetString()!;
            var bytes = service.ReadEntryForExport(Input(name), service.ReadManifest(Input(name)).Entries.Single().Path);
            File.WriteAllBytes(Input(name + ".decoded.bin"), bytes);
            Verify("file-bytes-" + name, Convert.ToHexString(bytes).Equals(spec.GetProperty("decodedHex").GetString(), StringComparison.OrdinalIgnoreCase)
                && Hash(File.ReadAllBytes(Input(name))) == spec.GetProperty("inputSHA256").GetString(), "full original and decoded bytes");
        }
        var corruptRejected = false;
        try { service.ReadEntryPreview(Input("bad-tail.bz2"), "bad-tail", maximumBytes: 1); }
        catch (InvalidDataException) { corruptRejected = true; }
        catch (SharpCompress.Common.ArchiveOperationException) { corruptRejected = true; }
        Verify("preview-bad-tail-max1", corruptRejected, "valid first member cannot publish prefix before corrupt later CRC");
        var wrongCodec = false;
        try { new ManagedArchive(readOptions: new(CompressionPayloadKind: CompressionPayloadKind.File)).ReadManifest(Input("expected.json")); }
        catch (InvalidDataException) { wrongCodec = true; }
        Verify("wrong-codec", wrongCodec, "new choice does not invent a decoder for JSON");
        var conflict = false; try { _ = new ManagedArchiveReadOptions(GZipPayloadKind: GZipPayloadKind.File, CompressionPayloadKind: CompressionPayloadKind.File); }
        catch (ArgumentException) { conflict = true; }
        Verify("choice-conflict", conflict, "gzip and BZip2/Z nonAuto are mutually exclusive");

        foreach (var ext in new[] { "bz2", "Z" })
        {
            pane.DiscardChanges(); pane.SelectMode(7); pane.LeftPath.Text = pane.RightPath.Text = Input("zero512." + ext);
            var retries = 0; var owner = false;
            pane.ArchiveRetryShown = dialog =>
            {
                retries++; owner = ReferenceEquals(dialog.Owner, window);
                dialog.LeftCompressionPayloadKind.SelectedItem = dialog.RightCompressionPayloadKind.SelectedItem = CompressionPayloadKind.File;
                dialog.Retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            try { pump(pane.ComparePathsAsync()); } finally { pane.ArchiveRetryShown = null; }
            Jobs(); var panel = Panel(pane);
            Verify("initial-retry-" + ext, retries == 1 && owner && panel.Rows.Single().Path == "zero512"
                && panel.ConfirmedLeft.ContainerCompressionPayloadKinds[0] == CompressionPayloadKind.File
                && panel.ConfirmedRight.ContainerCompressionPayloadKinds[0] == CompressionPayloadKind.File, "owned actual Retry button adopts both File choices");
        }
        pane.DiscardChanges(); pane.SelectMode(7); pane.LeftPath.Text = pane.RightPath.Text = Input("short.bz2");
        pump(pane.ComparePathsAsync()); Jobs(); var rootPanel = Panel(pane);
        void Refresh()
        {
            Task? operation = null; rootPanel.ButtonTaskObserved = (_, task) => operation = task;
            try { rootPanel.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "アーカイブを再比較")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (operation is null) throw new InvalidDataException("再比較button Task未観測"); pump(operation); Jobs(); }
            finally { rootPanel.ButtonTaskObserved = null; }
        }
        rootPanel.LeftCompressionPayloadKind.SelectedItem = CompressionPayloadKind.File; Refresh();
        rootPanel.EntryList.SelectedItem = rootPanel.Rows.Single(); pump(rootPanel.PreviewAsync(rootPanel.Rows.Single())); Jobs();
        var rows = rootPanel.Rows; var preview = rootPanel.PreviewText; var left = rootPanel.ConfirmedLeft; var right = rootPanel.ConfirmedRight;
        var selected = rootPanel.EntryList.SelectedItem; var openEnabled = rootPanel.OpenEntryButton.IsEnabled;
        string State() => string.Join("|", new[] { rootPanel.ConfirmedLeft.RootPath, rootPanel.ConfirmedRight.RootPath, string.Join(",", rootPanel.ConfirmedLeft.ContainerCompressionPayloadKinds), string.Join(",", rootPanel.ConfirmedRight.ContainerCompressionPayloadKinds), string.Join(";", rootPanel.Rows.Select(row => row.Path + ":" + row.Status + ":" + row.Left?.Sha256 + ":" + row.Right?.Sha256)), rootPanel.PreviewText, (rootPanel.EntryList.SelectedItem as ArchiveEntryDifference)?.Path ?? "", rootPanel.OpenEntryButton.IsEnabled.ToString(), pane.HasUnsavedChanges.ToString(), pane.CompareButton.IsEnabled.ToString() });
        var baselineState = State(); var retainedStates = new List<string>();
        bool Retained() { retainedStates.Add(State()); return ReferenceEquals(Panel(pane), rootPanel) && ReferenceEquals(rootPanel.Rows, rows)
            && rootPanel.PreviewText == preview && ReferenceEquals(rootPanel.ConfirmedLeft, left) && ReferenceEquals(rootPanel.ConfirmedRight, right)
            && ReferenceEquals(rootPanel.EntryList.SelectedItem, selected) && rootPanel.OpenEntryButton.IsEnabled == openEnabled
            && !pane.HasUnsavedChanges; }
        Verify("left-right-independent", left.ContainerCompressionPayloadKinds[0] == CompressionPayloadKind.File
            && right.ContainerCompressionPayloadKinds[0] == CompressionPayloadKind.Auto, "real left picker preserves right Auto");
        rootPanel.LeftCompressionPayloadKind.SelectedItem = CompressionPayloadKind.Auto;
        using (var canceled = new CancellationTokenSource())
        { canceled.Cancel(); try { pump(rootPanel.RefreshAsync(canceled.Token)); } catch (OperationCanceledException) { } }
        Verify("cancel-keeps-display", Retained(), "confirmed panel rows selection preview buttons dirty retained");
        rootPanel.LeftCompressionPayloadKind.SelectedItem = CompressionPayloadKind.File;
        var generations = 0;
        rootPanel.RefreshReadyForAdoption = () => { generations++; rootPanel.LeftCompressionPayloadKind.SelectedItem = CompressionPayloadKind.Auto; rootPanel.LeftCompressionPayloadKind.SelectedItem = CompressionPayloadKind.File; };
        try { pump(rootPanel.RefreshAsync()); } catch (OperationCanceledException) { }
        finally { rootPanel.RefreshReadyForAdoption = null; }
        Verify("stale-choice-reverted", generations == 1 && Retained(), "change then revert still invalidates pending generation");
        rootPanel.LeftCompressionPayloadKind.SelectedItem = CompressionPayloadKind.Tar;
        var failed = false; try { pump(rootPanel.RefreshAsync()); } catch (InvalidDataException) { failed = true; }
        Verify("failed-tar-keeps-display", failed && Retained(), "strict Tar cannot adopt plain text");
        rootPanel.LeftCompressionPayloadKind.SelectedItem = CompressionPayloadKind.File;
        rootPanel.RefreshReadyForAdoption = () => rootPanel.CancelOperation();
        try { pump(rootPanel.RefreshAsync()); } catch (OperationCanceledException) { }
        finally { rootPanel.RefreshReadyForAdoption = null; }
        Verify("cancel-at-adoption", Retained(), "cancel after full decode rejects publication");
        // 別比較を採用前に開始し、古い候補のTaskを最後まで回収する。
        Task? newest = null; var oldCandidates = 0;
        pane.ArchiveReadyForAdoption = candidate =>
        {
            oldCandidates++; pane.ArchiveReadyForAdoption = null;
            pane.LeftPath.Text = pane.RightPath.Text = Input("short.Z"); newest = pane.ComparePathsAsync();
        };
        pane.LeftPath.Text = pane.RightPath.Text = Input("tar.bz2");
        try { pump(pane.ComparePathsAsync()); if (newest is not null) pump(newest); }
        finally { pane.ArchiveReadyForAdoption = null; }
        Jobs(); Verify("old-candidate-discarded", oldCandidates == 1 && newest is not null
            && Panel(pane).ConfirmedLeft.RootPath == Input("short.Z") && Panel(pane).Rows.Single().Path == "short", "new generation and root survive old completion");
        rootPanel = Panel(pane); var rootBeforeChoice = pane.CaptureProject();
        var baselineRightReadonly = rootBeforeChoice.RightReadOnly; var baselineRightPath = rootBeforeChoice.RightPath;
        rootPanel.LeftCompressionPayloadKind.SelectedItem = CompressionPayloadKind.File; Refresh();
        var project = pane.CaptureProject(); var workspace = WorkspaceStore.SerializeWorkspace(new() { Entries = [project] });
        File.WriteAllBytes(Input("workspace-v9.json"), workspace);
        using (var document = JsonDocument.Parse(workspace)) Verify("workspace-v9", document.RootElement.GetProperty("formatVersion").GetInt32() == 9
            && project.LeftArchiveInput?.ContainerCompressionPayloadKinds?.SequenceEqual(new[] { "File" }) == true
            && project.RightArchiveInput is null && project.RightPath == baselineRightPath && project.RightPath == Input("short.Z")
            && project.LeftReadOnly && project.RightReadOnly == baselineRightReadonly, "explicit File DTO and Auto physical path preserve user readonly");
        ComparisonWorkspace? loaded = null;
        async Task Load() => loaded = await WorkspaceStore.LoadWorkspaceAsync(Input("workspace-v9.json"));
        pump(Load()); var reloaded = loaded?.Entries.SingleOrDefault();
        Verify("workspace-v9-reload", reloaded is not null && reloaded.LeftArchiveInput?.ContainerCompressionPayloadKinds?.SequenceEqual(new[] { "File" }) == true
            && reloaded.RightArchiveInput is null && reloaded.RightPath == baselineRightPath && reloaded.RightPath == Input("short.Z")
            && reloaded.LeftReadOnly && reloaded.RightReadOnly == baselineRightReadonly, "strict DTO and physical path readonly roundtrip");
        var protectedOutput = false;
        try { pump(rootPanel.ExportToAsync(false, "short", Input("short.Z"))); } catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException) { protectedOutput = true; }
        Verify("input-output-protected", protectedOutput && Hash(File.ReadAllBytes(Input("short.Z"))) == hashes["short.Z"], "actual export refuses original input");
        CheckRoutesAndWorking(window, folder, pump, Verify);
        CheckBrowser(window, folder, pump, Verify);
        CheckRootWrites(window, folder, cases, pump, Verify);
        Jobs();
        using (var proof = File.Create(Input("proof.json")))
        using (var writer = new Utf8JsonWriter(proof, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1); writer.WriteNumber("adoptionGateCalls", generations); writer.WriteNumber("oldCandidateGateCalls", oldCandidates);
            writer.WriteBoolean("baselineRightReadonly", baselineRightReadonly);
            writer.WriteString("baselineState", baselineState); writer.WriteStartArray("retainedStates"); foreach (var state in retainedStates) writer.WriteStringValue(state); writer.WriteEndArray();
            writer.WriteStartArray("cases"); foreach (var item in observations) { writer.WriteStartObject(); writer.WriteString("id", item.Id); writer.WriteBoolean("passed", item.Passed); writer.WriteString("detail", item.Detail); writer.WriteEndObject(); } writer.WriteEndArray();
            writer.WriteStartArray("originals"); foreach (var item in hashes) { writer.WriteStartObject(); writer.WriteString("file", item.Key); writer.WriteString("beforeSha256", item.Value); writer.WriteString("afterSha256", Hash(File.ReadAllBytes(Input(item.Key!)))); writer.WriteEndObject(); } writer.WriteEndArray();
            writer.WriteEndObject();
        }
        foreach (var (width, height, name) in new[] { (1280d,850d,"normal"), (850d,550d,"minimum") })
        {
            window.Width = width; window.Height = height; Jobs();
            var panel = Panel(pane); var controls = new Control[] { pane.LeftPath, pane.RightPath, pane.CompareButton, panel.LeftCompressionPayloadKind, panel.RightCompressionPayloadKind, panel.EntryList,
                panel.GetVisualDescendants().OfType<TextBox>().Single(c => c.Name == "archive-preview") };
            using var file = File.Create(Input("bounds-" + name + ".json")); using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject(); writer.WriteString("png", "bare-compression-" + name + ".png"); writer.WriteNumber("width",width); writer.WriteNumber("height",height); writer.WriteStartArray("controls");
            var controlIndex = 0; foreach (var c in controls) { c.BringIntoView(); Jobs(); var p = c.TranslatePoint(default,window); var reachable = p is { } point && point.X >= 0 && point.Y >= 0 && point.Y + c.Bounds.Height <= height;
                var controlPng="bare-compression-"+name+"-control-"+(controlIndex++)+".png"; screenshot(controlPng);
                WriteLayoutDiagnostic(Input("layout-diagnostic-" + name + "-control-" + (controlIndex - 1) + ".json"), window, panel, c, p, width, height, reachable, controlPng);
                Verify("layout-"+name+"-"+c.Name, reachable && (c != panel.EntryList || c.Bounds.Height >= 100), "scroll reach and list >=100 DIP");
                writer.WriteStartObject(); writer.WriteString("png",controlPng); writer.WriteString("name",c.Name); writer.WriteNumber("x",p?.X ?? -1); writer.WriteNumber("y",p?.Y ?? -1); writer.WriteNumber("width",c.Bounds.Width); writer.WriteNumber("height",c.Bounds.Height); writer.WriteBoolean("reachable",reachable); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteEndObject(); screenshot("bare-compression-"+name+".png");
        }
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            try
            {
                pane.DiscardChanges(); pane.ApplyProject(originalProject);
                window.Width = originalWidth; window.Height = originalHeight; Jobs();
                var restored = pane.CaptureProject(); WriteProject(Input("shared-project-after.json"), restored);
                var restoredExactly = ProjectJson(originalProject).SequenceEqual(ProjectJson(restored))
                    && window.Width == originalWidth && window.Height == originalHeight
                    && pane.CompareButton.IsEnabled == originalCompareEnabled && pane.HasUnsavedChanges == originalDirty;
                using (var stateFile = File.Create(Input("shared-state-proof.json")))
                using (var state = new Utf8JsonWriter(stateFile, new JsonWriterOptions { Indented = true }))
                {
                    state.WriteStartObject(); state.WriteNumber("beforeWidth", originalWidth); state.WriteNumber("beforeHeight", originalHeight);
                    state.WriteNumber("afterWidth", window.Width); state.WriteNumber("afterHeight", window.Height);
                    state.WriteBoolean("beforeCompareEnabled", originalCompareEnabled); state.WriteBoolean("afterCompareEnabled", pane.CompareButton.IsEnabled);
                    state.WriteBoolean("beforeHasUnsavedChanges", originalDirty); state.WriteBoolean("afterHasUnsavedChanges", pane.HasUnsavedChanges);
                    state.WriteBoolean("beforeLeftArchiveInput", originalProject.LeftArchiveInput is not null); state.WriteBoolean("beforeRightArchiveInput", originalProject.RightArchiveInput is not null);
                    state.WriteBoolean("afterLeftArchiveInput", restored.LeftArchiveInput is not null); state.WriteBoolean("afterRightArchiveInput", restored.RightArchiveInput is not null);
                    state.WriteBoolean("restoredExactly", restoredExactly); state.WriteString("primaryFailure", primaryFailure?.GetType().FullName); state.WriteEndObject();
                }
                if (!restoredExactly) throw new InvalidDataException("bare compression shared pane復元が開始時設定と一致しません。");
            }
            catch (Exception restorationFailure) when (primaryFailure is not null)
            {
                primaryFailure.Data["bare-compression-restoration"] = restorationFailure.ToString();
                try { File.WriteAllText(Input("shared-restoration-error.txt"), restorationFailure.ToString()); }
                catch (Exception diagnosticFailure) { primaryFailure.Data["bare-compression-restoration-diagnostic"] = diagnosticFailure.ToString(); }
            }
        }
    }
    // 判定がthrowしても、直前の実frameと寸法を独立JSONへ確定して残す。本文やpasswordは記録しない。
    private static void WriteLayoutDiagnostic(string path, MainWindow window, ArchivePanel panel, Control control,
        Point? point, double width, double height, bool reachable, string png)
    {
        using var file = File.Create(path);
        using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        void Bounds(string name, Rect bounds)
        {
            writer.WriteStartObject(name); writer.WriteNumber("x", bounds.X); writer.WriteNumber("y", bounds.Y);
            writer.WriteNumber("width", bounds.Width); writer.WriteNumber("height", bounds.Height); writer.WriteEndObject();
        }
        void Geometry(string name, Control? item)
        {
            if (item is null) { writer.WriteNull(name); return; }
            writer.WriteStartObject(name); writer.WriteString("actualName", item.Name); writer.WriteString("type", item.GetType().Name);
            Bounds("bounds", item.Bounds);
            writer.WriteStartObject("desiredSize"); writer.WriteNumber("width", item.DesiredSize.Width); writer.WriteNumber("height", item.DesiredSize.Height); writer.WriteEndObject();
            writer.WriteStartObject("margin"); writer.WriteNumber("left", item.Margin.Left); writer.WriteNumber("top", item.Margin.Top); writer.WriteNumber("right", item.Margin.Right); writer.WriteNumber("bottom", item.Margin.Bottom); writer.WriteEndObject();
            var origin = item.TranslatePoint(default, window);
            if (origin is { } position) Bounds("boundsWindow", new Rect(position, item.Bounds.Size)); else writer.WriteNull("boundsWindow");
            if (double.IsFinite(item.MaxHeight)) writer.WriteNumber("maxHeight", item.MaxHeight); else writer.WriteString("maxHeight", item.MaxHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteBoolean("useLayoutRounding", item.UseLayoutRounding); writer.WriteBoolean("clipToBounds", item.ClipToBounds);
            writer.WriteEndObject();
        }
        writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1); writer.WriteString("png", png); writer.WriteString("pngBase", "self-test-output");
        writer.WriteNumber("width", width); writer.WriteNumber("height", height); writer.WriteString("actualName", control.Name);
        writer.WriteNumber("renderScaling", window.RenderScaling); writer.WriteBoolean("useLayoutRounding", window.UseLayoutRounding);
        Bounds("bounds", control.Bounds);
        if (point is { } translated)
        {
            writer.WriteStartObject("translatePoint"); writer.WriteNumber("x", translated.X); writer.WriteNumber("y", translated.Y); writer.WriteEndObject();
            Bounds("boundsWindow", new Rect(translated, control.Bounds.Size));
        }
        else { writer.WriteNull("translatePoint"); writer.WriteNull("boundsWindow"); }
        Geometry("archivePanel", panel); Geometry("dockPanel", panel.Content as Control);
        Geometry("pairGrid", panel.EntryList.GetVisualAncestors().OfType<Grid>().FirstOrDefault());
        Geometry("entryList", panel.EntryList);
        var viewports = panel.GetVisualDescendants().OfType<ScrollViewer>().ToArray();
        Geometry("toolbar", viewports.FirstOrDefault(item => item.Name == "archive-toolbar"));
        var status = viewports.FirstOrDefault(item => item.Content is TextBlock && item.Name != "archive-toolbar");
        Geometry("statusViewport", status); Geometry("status", status?.Content as Control);
        Geometry("nearestViewport", control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault());
        writer.WriteStartObject("ancestors"); var index = 0;
        foreach (var ancestor in control.GetVisualAncestors().OfType<Control>()) Geometry((index++).ToString(System.Globalization.CultureInfo.InvariantCulture), ancestor);
        writer.WriteEndObject();
        writer.WriteBoolean("reachable", reachable); writer.WriteBoolean("heightRequired", control == panel.EntryList);
        writer.WriteNumber("minimumListHeight", 100); writer.WriteBoolean("heightPassed", control != panel.EntryList || control.Bounds.Height >= 100);
        writer.WriteEndObject();
    }
    private static ArchivePanel Panel(ComparisonPane pane) => pane.GetVisualDescendants().OfType<ArchivePanel>().Single();
}
