using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessBinaryThreeWayChecks
{
    internal static void Run(MainWindow window, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "binary-threeway"); Directory.CreateDirectory(folder);
        byte[][] originals = [[0, 1, 2, 3, 255, 128], [0, 9, 2, 5, 255, 130], [0, 10, 2, 4, 255, 129]];
        var files = new[] { "left.bin", "middle.bin", "right.bin" }.Select(name => Path.Combine(folder, name)).ToArray();
        var roots = new[] { "left.zip", "middle.zip", "right.zip" }.Select(name => Path.Combine(folder, name)).ToArray();
        for (var side = 0; side < 3; side++)
        {
            File.WriteAllBytes(files[side], originals[side]);
            File.WriteAllBytes(roots[side], HeadlessBinaryWorkingChecks.Zip(("inner.zip", HeadlessBinaryWorkingChecks.Zip(("leaf.bin", originals[side]), ("empty.bin", [])))));
        }
        var hashes = roots.Select(Hash).ToArray(); var snapshots = new List<(string Name, string[] Hex, bool[] Dirty, bool[] ReadOnly)>(); var refusals = new List<string>();
        var mainFiles = new[] { "main-left.bin", "main-middle.bin", "main-right.bin" }.Select(name => Path.Combine(folder, name)).ToArray();
        for (var side = 0; side < 3; side++) File.WriteAllBytes(mainFiles[side], originals[side]);
        var normal = Add(new() { Mode = "Binary", LeftPath = mainFiles[0], BasePath = mainFiles[1], RightPath = mainFiles[2] }); var panel = Binary(normal);
        Report("three ordinary sides read exact independent bytes", panel.HasMiddle && AllBytes(panel, originals)); Observe("ordinary-initial", panel);
        window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), normal)); screenshot("binary-threeway-initial.png");
        CheckCandidateBoundaries();
        var same = Ordinary(); var sp = Binary(same);
        sp.MiddleHex.Text = "10 09 02 05 FF 82"; Click(sp, "中央の編集を適用");
        Report("actual middle apply button edits middle only", sp.Capture(1).CopyBytes()[0] == 16 && sp.Dirty(1) && !sp.Dirty(0) && !sp.Dirty(2));
        var before = sp.Capture(1).Sha256; var revision = sp.Session.Revision(1); sp.MiddleReadOnly = true; sp.ApplyReadOnly?.Invoke(); Click(sp, "元に戻す");
        Report("readonly middle Undo button refuses without cursor movement", sp.Capture(1).Sha256 == before && sp.Session.Revision(1) == revision && sp.Session.CanUndo);
        sp.MiddleReadOnly = false; sp.ApplyReadOnly?.Invoke(); Click(sp, "元に戻す");
        Report("unblocked middle Undo retries same shared action", AllBytes(sp, originals)); Click(sp, "やり直す");
        Report("middle Redo restores shared action", sp.Capture(1).CopyBytes()[0] == 16); Observe("middle-redo", sp);
        window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), same));
        same.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "比較の設定…")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        var settings = window.OwnedWindows.Single(value => value.Title == "比較の設定");
        settings.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, "中央を読取り専用にする")).IsChecked = true;
        settings.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "適用")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Report("middle readonly metadata applies without byte history change", same.CaptureProject().BaseReadOnly && sp.MiddleReadOnly && sp.Capture(1).CopyBytes()[0] == 16);
        var appliedHex = sp.MiddleHex.Text; var pendingHex = "AA 09 02 05 FF 82"; sp.MiddleHex.Text = pendingHex;
        var sentinel = Path.Combine(folder, "guarded.bin"); File.WriteAllBytes(sentinel, [0x66]);
        Report("middle pending prevents save copy and navigation", Refused(() => pump(sp.SaveToAsync(1, sentinel))) && Refused(() => sp.CopyRange(0, 2, 0, 1)) && Refused(() => sp.SetOffset(1)) && sp.MiddleHex.Text == pendingHex && File.ReadAllBytes(sentinel).SequenceEqual(new byte[] { 0x66 }));
        sp.MiddleHex.Text = appliedHex;
        foreach (var source in Enumerable.Range(0, 3)) foreach (var destination in Enumerable.Range(0, 3).Where(value => value != source))
        {
            var copy = Ordinary(); var cp = Binary(copy); cp.Differences.SelectedIndex = 0;
            var labels = new[] { "左", "中央", "右" }; Click(cp, labels[source] + "→" + labels[destination] + " 差分範囲");
            var expected = originals.Select(bytes => bytes.ToArray()).ToArray(); expected[destination][1] = originals[source][1];
            Report($"all direction actual difference button {source} to {destination}", AllBytes(cp, expected));
            cp.Undo(); cp.CopyRange(source, destination, 0, 6); expected[destination] = originals[source];
            Report($"full difference range pair {source} to {destination}", AllBytes(cp, expected));
            pump(cp.SaveToAsync(destination, Path.Combine(folder, $"copy-{source}-{destination}.bin"))); Observe($"copy-{source}-{destination}", cp);
        }
        using (var lengths = new SpecializedViews.BinaryPanel([1, 2], [1], false, false, [1, 2, 3, 4, 5, 6, 7, 8]))
        {
            var rev = lengths.Session.Revision(2);
            Report("third-side length cannot authorize pair range", Refused(() => lengths.CopyRange(0, 2, 4, 1)) && lengths.Session.Revision(2) == rev && lengths.Capture(2).CopyBytes().SequenceEqual(new byte[] { 1 }));
            lengths.CopyRange(0, 2, 1, 1); Report("pair EOF extension ignores longer middle", lengths.Capture(2).CopyBytes().SequenceEqual(new byte[] { 1, 2 }));
            lengths.CopyRange(2, 0, 1, 1); Report("pair no-op leaves shared revision", lengths.Session.Revision(0) == 0);
        }
        using (var equal = new SpecializedViews.BinaryPanel([1, 2], [1, 2], false, false, [1, 2]))
        { Report("three offset equality has zero differences", equal.Differences.ItemCount == 0); equal.MiddleHex.Text = "01 03"; equal.Apply(1); Report("middle-only difference is one offset range", equal.Session.Differences(10, out var total).Single() == (1, 1) && total == 1); }
        using (var history = new SpecializedViews.BinaryPanel([0], [0], false, false, [0]))
        {
            for (var index = 0; index < 256; index++) { var side = index % 3; history.Editor(side).Text = ((index / 3) % 2 == 0 ? "01" : "00"); history.Apply(side); }
            var bytes = history.ProjectSides.Select(side => history.Capture(side).Sha256).ToArray(); var revs = history.ProjectSides.Select(side => history.Session.Revision(side)).ToArray();
            history.MiddleHex.Text = history.Capture(1).CopyBytes()[0] == 0 ? "01" : "00";
            Report("three sides share 256 action budget and atomic refusal", Refused(() => history.Apply(1)) && history.ProjectSides.Select(side => history.CaptureApplied(side).Sha256).SequenceEqual(bytes) && history.ProjectSides.Select(side => history.Session.Revision(side)).SequenceEqual(revs) && history.Pending(1));
        }
        using (var budget = new SpecializedViews.BinaryPanel(new byte[BinaryEditSession.MaximumFileBytes], new byte[BinaryEditSession.MaximumFileBytes], false, false, new byte[BinaryEditSession.MaximumFileBytes]))
        {
            foreach (var offset in new[] { 0, BinaryEditSession.MaximumFileBytes - 4096 })
            { budget.SetOffset(offset); var bytes = budget.Session.Page(0, offset); bytes[offset == 0 ? 0 : bytes.Length - 1] = 1; budget.LeftHex.Text = Convert.ToHexString(bytes); budget.Apply(0); }
            budget.SetOffset(0); budget.CopyRange(0, 1, 0, BinaryEditSession.MaximumFileBytes);
            var bytesBefore = budget.Capture(2).Sha256; var revisionBefore = budget.Session.Revision(2);
            Report("three sides share 64 MiB history atomic refusal", Refused(() => budget.CopyRange(0, 2, 0, BinaryEditSession.MaximumFileBytes)) && budget.Capture(2).Sha256 == bytesBefore && budget.Session.Revision(2) == revisionBefore && budget.Session.CanUndo);
        }
        SetFirst(panel, 0, 16); SetFirst(panel, 1, 32); SetFirst(panel, 2, 48); panel.Undo(); panel.Undo(); panel.Redo(); panel.Redo(); Observe("shared-history", panel);
        var middle = mainFiles[1]; Click(panel, "中央を保存"); pump(Wait(() => !panel.Dirty(1)));
        Report("middle ordinary Save only updates own savepoint", File.ReadAllBytes(middle)[0] == 32 && panel.Dirty(0) && !panel.Dirty(1) && panel.Dirty(2)); Observe("middle-saved", panel);
        var late = Path.Combine(folder, "middle-late.bin"); normal.BinarySaveBeforePublish = () => { SetFirst(panel, 1, 33); return Task.CompletedTask; };
        pump(panel.SaveToAsync(1, late)); normal.BinarySaveBeforePublish = null;
        Report("middle late edit retains dirty and immutable publication", File.ReadAllBytes(late)[0] == 32 && panel.Capture(1).CopyBytes()[0] == 33 && panel.Dirty(1) && normal.CaptureProject().BasePath == late && normal.CaptureProject().RightPath == mainFiles[2]); Observe("middle-late", panel);
        var appliedMiddleHex = panel.MiddleHex.Text; normal.BinarySaveBeforePublish = () => { panel.MiddleHex.Text = "22 09 02 05 FF 82"; return Task.CompletedTask; };
        var pending = Path.Combine(folder, "middle-pending-save.bin"); pump(panel.SaveToAsync(1, pending)); normal.BinarySaveBeforePublish = null;
        Report("middle pending during publication retains draft and capture", File.ReadAllBytes(pending)[0] == 33 && panel.Pending(1) && panel.MiddleHex.Text == "22 09 02 05 FF 82" && panel.Dirty(1)); panel.MiddleHex.Text = appliedMiddleHex;
        foreach (var kind in new[] { "cancel", "readonly", "path", "mode" })
        {
            using var cancellation = new CancellationTokenSource();
            normal.BinarySaveBeforePublish = () => { if (kind == "cancel") cancellation.Cancel(); if (kind == "readonly") panel.MiddleReadOnly = true; if (kind == "path") normal.BasePath.Text = middle; if (kind == "mode") normal.SelectMode(1); return Task.CompletedTask; };
            Report("middle publication guard " + kind, Refused(() => pump(panel.SaveToAsync(1, sentinel, cancellation.Token))) && File.ReadAllBytes(sentinel).SequenceEqual(new byte[] { 0x66 }));
            panel.MiddleReadOnly = false; normal.BasePath.Text = pending; normal.SelectMode(3); normal.BinarySaveBeforePublish = null; panel.ApplyReadOnly?.Invoke();
        }
        var sourceProject = new ComparisonProject { Mode = "Binary", LeftArchiveInput = Input(0), BaseArchiveInput = Input(1), RightArchiveInput = Input(2), LeftReadOnly = true, BaseReadOnly = true, RightReadOnly = true, LeftDescription = "left binary", BaseDescription = "middle binary", RightDescription = "right binary" };
        var sourcePane = Add(sourceProject); var sourcePanel = Binary(sourcePane); Report("three Source reads include writable middle", AllBytes(sourcePanel, originals) && !sourcePanel.MiddleReadOnly && sourcePanel.Caption(1) == "middle binary"); Observe("source-initial", sourcePanel);
        var clean = Add(sourceProject); var dirty = Add(sourceProject); var dirtyPanel = Binary(dirty); SetFirst(dirtyPanel, 1, 112); SetFirst(sourcePanel, 1, 96);
        pump(sourcePanel.SaveAsync(1));
        Report("middle working save synchronizes clean sibling only", Binary(clean).Capture(1).CopyBytes()[0] == 96 && dirtyPanel.Capture(1).CopyBytes()[0] == 112 && dirtyPanel.Dirty(1));
        Report("middle stale sibling cannot overwrite newer revision", Refused(() => pump(dirtyPanel.SaveAsync(1))));
        var shared = Add(sourceProject with { LeftArchiveInput = Input(1), BaseArchiveInput = Input(1), RightArchiveInput = Input(2) }); var sharedPanel = Binary(shared); SetFirst(sharedPanel, 1, 97); pump(sharedPanel.SaveAsync(1));
        Report("same route left middle clean sync preserves shared store", sharedPanel.Capture(0).CopyBytes()[0] == 97 && sharedPanel.Capture(1).CopyBytes()[0] == 97 && !sharedPanel.Dirty(0));
        sourcePanel = Binary(sourcePane); Report("same route update reaches source clean middle", sourcePanel.Capture(1).CopyBytes()[0] == 97);
        var savedProject = sourcePane.CaptureProject(); var workspace = Path.Combine(folder, "working.json"); pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new() { Entries = [savedProject] }, publishedAsset: window.ArchiveLifetime.RegisterAsset));
        var reload = WorkspaceStore.LoadAsync(workspace); pump(reload); var reloaded = reload.Result; var loaded = Add(reloaded);
        Report("v5 middle relative asset restores bytes and metadata", reloaded.BaseArchiveInput!.WorkingDocuments![0].Bytes![0] == 97 && reloaded.BaseDescription == "middle binary" && Binary(loaded).Capture(1).CopyBytes()[0] == 97);
        var asset = reloaded.BaseArchiveInput.WorkingDocuments[0].SnapshotPath!;
        foreach (var session in window.SessionPanes) session.DiscardChanges();
        var windowWorkspace = Path.Combine(folder, "window-workspace.json"); pump(window.SaveWorkspaceAsync(windowWorkspace));
        foreach (var target in roots.Concat(files.Where(path => path != middle)).Append(windowWorkspace).Append(asset))
            Report("middle output protects every input asset workspace " + Path.GetFileName(target), Refused(() => pump(sourcePanel.SaveToAsync(1, target))));
        var package = Path.Combine(folder, "working-package.zip"); pump(ComparisonPackage.CreateAsync(new() { Entries = [savedProject] }, package, new(true, false, false, true)));
        Report("three Source reportless package preserves root and assets", File.Exists(package) && roots.Select(Hash).SequenceEqual(hashes));
        Report("Source three report patch remain explicit unsupported", Refused(() => pump(ComparisonPackage.CreateAsync(new() { Entries = [savedProject] }, Path.Combine(folder, "report-refused.zip"), new(true, true, false, true)))) && Refused(() => pump(ComparisonPackage.CreateAsync(new() { Entries = [savedProject] }, Path.Combine(folder, "patch-refused.zip"), new(true, false, true, true)))));
        var detached = Path.Combine(folder, "middle-detached.bin"); pump(sourcePanel.SaveToAsync(1, detached));
        Report("middle external SaveAs detaches middle only", sourcePane.CaptureProject().BaseArchiveInput is null && sourcePane.CaptureProject().LeftArchiveInput is not null && sourcePane.CaptureProject().RightArchiveInput is not null && sourcePane.CaptureProject().BasePath == detached && File.ReadAllBytes(detached)[0] == 97);
        Report("detached central root remains protected", Refused(() => pump(sourcePanel.SaveToAsync(1, roots[1]))) && roots.Select(Hash).SequenceEqual(hashes));
        var readonlySource = Add(sourceProject with { BaseArchiveInput = Input(1) with { InheritedReadOnly = true } }); var readonlyPanel = Binary(readonlySource);
        Report("central inherited readonly rejects edit and ordinary Save", readonlyPanel.MiddleReadOnly && Refused(() => readonlyPanel.CopyRange(0, 1, 0, 1)) && Refused(() => pump(readonlyPanel.SaveAsync(1))));
        readonlyPanel.CopyRange(1, 2, 0, 6); Report("readonly central source can copy into writable right", readonlyPanel.Capture(2).CopyBytes().SequenceEqual(readonlyPanel.Capture(1).CopyBytes()));
        var roExport = Path.Combine(folder, "middle-readonly-copy.bin"); pump(readonlyPanel.SaveToAsync(1, roExport)); Report("readonly middle SaveAs leaves source DTO unchanged", readonlySource.CaptureProject().BaseArchiveInput is not null && readonlyPanel.MiddleReadOnly);
        var rootOriginal = File.ReadAllBytes(roots[1]); var rootTime = File.GetLastWriteTimeUtc(roots[1]); var changedRoot = rootOriginal.ToArray(); changedRoot[^1] ^= 1;
        File.WriteAllBytes(roots[1], changedRoot); File.SetLastWriteTimeUtc(roots[1], rootTime);
        try { Report("central same size mtime root substitution rejected", Refused(() => pump(Binary(loaded).SaveAsync(1)))); }
        finally { File.WriteAllBytes(roots[1], rootOriginal); File.SetLastWriteTimeUtc(roots[1], rootTime); }
        var corruptRoot = Path.Combine(folder, "corrupt-middle.zip"); var badCrc = rootOriginal.ToArray();
        for (var index = 0; index < badCrc.Length - 20; index++)
        {
            if (badCrc.AsSpan(index, 4).SequenceEqual(new byte[] { 0x50, 0x4b, 3, 4 })) badCrc[index + 14] ^= 1;
            if (badCrc.AsSpan(index, 4).SequenceEqual(new byte[] { 0x50, 0x4b, 1, 2 })) badCrc[index + 16] ^= 1;
        }
        File.WriteAllBytes(corruptRoot, badCrc); var corrupt = Add(sourceProject); var previous = Binary(corrupt); var previousSha = previous.Capture(1).Sha256;
        corrupt.ApplyProject(sourceProject with { BaseArchiveInput = Input(1) with { RootPath = corruptRoot, RootSha256 = Hash(corruptRoot) } });
        corrupt.ArchiveSourceRetryShown = dialog => dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Report("central CRC failure preserves previous adopted Binary", Refused(() => pump(corrupt.CompareArchiveProjectAsync())) && !previous.IsDisposed && ReferenceEquals(previous, Binary(corrupt)) && previous.Capture(1).Sha256 == previousSha);
        corrupt.ArchiveSourceRetryShown = null;
        var candidatePanel = Binary(loaded); loaded.ArchiveSourceReadyForAdoption = () => candidatePanel.MiddleHex.Text = "AA 09 02 05 FF 82";
        pump(loaded.CompareArchiveProjectAsync()); loaded.ArchiveSourceReadyForAdoption = null;
        Report("middle pending during reload discards candidate", ReferenceEquals(candidatePanel, Binary(loaded)) && !candidatePanel.IsDisposed && candidatePanel.Pending(1)); candidatePanel.MiddleHex.Text = "61 09 02 05 FF 82";
        var empty = Add(sourceProject with { BaseArchiveInput = Input(1) with { LeafEntry = "empty.bin" } }); pump(Binary(empty).SaveAsync(1)); Report("central real empty leaf remains distinct", Binary(empty).Capture(1).Length == 0 && empty.CaptureProject().BaseArchiveInput is { MissingEntryChain: null, WorkingDocuments: not null });
        var missing = Add(sourceProject with { BaseArchiveInput = Input(1) with { LeafEntry = null, MissingEntryChain = ["missing.bin"] } }); var missingOutput = Path.Combine(folder, "middle-missing.bin"); missing.BinarySavePathPicker = side => side == 1 ? Task.FromResult<string?>(missingOutput) : throw new InvalidOperationException("wrong side"); pump(Binary(missing).SaveAsync(1)); Report("central missing zero save detaches only middle", File.ReadAllBytes(missingOutput).Length == 0 && missing.CaptureProject().BaseArchiveInput is null && missing.CaptureProject().RightArchiveInput is not null);
        CheckPassword();
        window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), readonlySource)); window.Width = 900; window.Height = 600; Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
        var headerTabs = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Items.OfType<TabItem>().Any(item => ReferenceEquals(item.Content, readonlySource)));
        var headers = window.SessionHeaders!;
        foreach (var index in new[] { 0, window.SessionPanes.Count / 2, window.SessionPanes.Count - 1 })
        {
            window.SelectSession(index); Dispatcher.UIThread.RunJobs(); screenshot("binary-threeway-header-" + index + ".png");
            var item = headerTabs.Items.OfType<TabItem>().ElementAt(index); var position = item.TranslatePoint(new Point(0, 0), headers);
            Report("many tab minimum header reachable " + (index == 0 ? "first" : index == window.SessionPanes.Count - 1 ? "last" : "middle"),
                headers.Bounds.Height > 0 && headers.Bounds.Height <= 110 && position.HasValue && position.Value.Y >= -1
                && position.Value.Y + item.Bounds.Height <= headers.Bounds.Height + 1, $"tabs={window.SessionPanes.Count};headers={headers.Bounds};item={item.Bounds};position={position};offset={headers.Offset}");
        }
        window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
        window.Width = 900; window.Height = 600; Dispatcher.UIThread.RunJobs(); screenshot("binary-threeway-header-resize.png");
        var resizedItem = headerTabs.Items.OfType<TabItem>().Last(); var resizedPosition = resizedItem.TranslatePoint(new Point(0, 0), headers);
        Report("many tab selected header remains reachable after resize", resizedPosition.HasValue && resizedPosition.Value.Y >= -1
            && resizedPosition.Value.Y + resizedItem.Bounds.Height <= headers.Bounds.Height + 1, $"selected={headerTabs.SelectedIndex};headers={headers.Bounds};position={resizedPosition};item={resizedItem.Bounds};offset={headers.Offset}");
        var extra = window.AddSession(); Dispatcher.UIThread.RunJobs(); screenshot("binary-threeway-header-close.png");
        var extraTab = headerTabs.Items.OfType<TabItem>().Single(item => ReferenceEquals(item.Content, extra));
        var close = ((StackPanel)extraTab.Header!).Children.OfType<Button>().Single(); var closePosition = close.TranslatePoint(new Point(0, 0), headers);
        var closeVisible = close.Bounds.Height > 0 && closePosition.HasValue && closePosition.Value.Y >= -1 && closePosition.Value.Y + close.Bounds.Height <= headers.Bounds.Height + 1;
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Report("many tab minimum actual close remains reachable", closeVisible && !window.SessionPanes.Contains(extra));
        window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), readonlySource)); Dispatcher.UIThread.RunJobs();
        var viewport = readonlyPanel.Viewport; var content = (Control)viewport.Content!;
        var editorPoint = readonlyPanel.MiddleHex.TranslatePoint(new Point(0, 0), content)!.Value;
        viewport.Offset = new Vector(0, editorPoint.Y); Dispatcher.UIThread.RunJobs(); screenshot("binary-threeway-multitab-editor.png");
        var editorOnScreen = readonlyPanel.MiddleHex.TranslatePoint(new Point(0, 0), viewport)!.Value;
        var differencesPoint = readonlyPanel.Differences.TranslatePoint(new Point(0, 0), content)!.Value;
        Report("multi-tab minimum scroll reaches Hex without difference overlap", readonlyPanel.MiddleHex.Bounds.Height >= 80 && editorOnScreen.Y >= -1 && editorOnScreen.Y < viewport.Bounds.Height && editorPoint.Y + readonlyPanel.MiddleHex.Bounds.Height <= differencesPoint.Y + 1, $"editor={readonlyPanel.MiddleHex.Bounds};editorY={editorPoint};diffY={differencesPoint};onscreen={editorOnScreen};viewport={viewport.Bounds};offset={viewport.Offset}");
        var centralSaveMulti = readonlyPanel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中央を別名保存"));
        viewport.Offset = new Vector(0, centralSaveMulti.TranslatePoint(new Point(0, 0), content)!.Value.Y); Dispatcher.UIThread.RunJobs(); screenshot("binary-threeway-multitab-save.png");
        var saveOnScreen = centralSaveMulti.TranslatePoint(new Point(0, 0), viewport)!.Value;
        Report("multi-tab minimum scroll reaches central SaveAs", saveOnScreen.Y >= -1 && saveOnScreen.Y + centralSaveMulti.Bounds.Height <= viewport.Bounds.Height + 1);
        window.Width = 850; window.Height = 550; Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
        var minimumEditor = readonlyPanel.MiddleHex.TranslatePoint(new Point(0, 0), content)!.Value;
        viewport.Offset = new Vector(0, minimumEditor.Y); Dispatcher.UIThread.RunJobs(); screenshot("binary-threeway-actual-minimum-editor.png");
        var minimumEditorOnScreen = readonlyPanel.MiddleHex.TranslatePoint(new Point(0, 0), viewport)!.Value;
        var minimumDifferences = readonlyPanel.Differences.TranslatePoint(new Point(0, 0), content)!.Value;
        Report("actual minimum 850x550 Hex viewport and differences reachable", readonlyPanel.ProjectSides.All(side => readonlyPanel.Editor(side).Bounds.Height >= 80)
            && viewport.Bounds.Height >= 80 && minimumEditorOnScreen.Y >= -1 && minimumEditorOnScreen.Y < viewport.Bounds.Height
            && minimumEditor.Y + readonlyPanel.MiddleHex.Bounds.Height <= minimumDifferences.Y + 1,
            $"tabs={window.SessionPanes.Count};window={window.Bounds};viewport={viewport.Bounds};editor={readonlyPanel.MiddleHex.Bounds};editorY={minimumEditor};diffY={minimumDifferences};onscreen={minimumEditorOnScreen}");
        var minimumHeader = headerTabs.Items.OfType<TabItem>().Single(item => ReferenceEquals(item.Content, readonlySource));
        var minimumHeaderPosition = minimumHeader.TranslatePoint(new Point(0, 0), headers);
        Report("actual minimum 850x550 selected header reachable", minimumHeaderPosition.HasValue && minimumHeaderPosition.Value.Y >= -1
            && minimumHeaderPosition.Value.Y + minimumHeader.Bounds.Height <= headers.Bounds.Height + 1,
            $"headers={headers.Bounds};item={minimumHeader.Bounds};position={minimumHeaderPosition};offset={headers.Offset}");
        viewport.Offset = new Vector(0, centralSaveMulti.TranslatePoint(new Point(0, 0), content)!.Value.Y); Dispatcher.UIThread.RunJobs(); screenshot("binary-threeway-actual-minimum-save.png");
        var minimumSavePosition = centralSaveMulti.TranslatePoint(new Point(0, 0), viewport)!.Value;
        Report("actual minimum 850x550 central SaveAs reachable", minimumSavePosition.Y >= -1 && minimumSavePosition.Y + centralSaveMulti.Bounds.Height <= viewport.Bounds.Height + 1,
            $"viewport={viewport.Bounds};save={centralSaveMulti.Bounds};position={minimumSavePosition};offset={viewport.Offset}");
        window.Width = 1280; window.Height = 850; viewport.Offset = default; Dispatcher.UIThread.RunJobs();
        foreach (var session in window.SessionPanes.Where(value => !ReferenceEquals(value, readonlySource)).ToArray())
        {
            session.DiscardChanges();
            var tab = window.GetVisualDescendants().OfType<TabControl>().SelectMany(value => value.Items.OfType<TabItem>()).Single(value => ReferenceEquals(value.Content, session));
            ((StackPanel)tab.Header!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        }
        window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), readonlySource)); screenshot("binary-threeway-readonly.png");
        window.Width = 900; window.Height = 600; readonlyPanel.Viewport.Offset = default; Dispatcher.UIThread.RunJobs(); screenshot("binary-threeway-minimum.png");
        var centralSave = readonlyPanel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中央を別名保存"));
        var saveContentPosition = centralSave.TranslatePoint(new Avalonia.Point(0, 0), content);
        if (saveContentPosition.HasValue) viewport.Offset = new Vector(0, Math.Max(0, saveContentPosition.Value.Y));
        Dispatcher.UIThread.RunJobs(); screenshot("binary-threeway-minimum-save.png");
        var savePosition = centralSave.TranslatePoint(new Avalonia.Point(0, 0), viewport);
        Report("minimum window keeps three Hex editors and central SaveAs reachable", readonlyPanel.ProjectSides.All(side => readonlyPanel.Editor(side).Bounds.Height >= 80) && savePosition.HasValue && savePosition.Value.Y >= 0 && savePosition.Value.Y + centralSave.Bounds.Height <= viewport.Bounds.Height, $"editorHeights={string.Join(",", readonlyPanel.ProjectSides.Select(side => readonlyPanel.Editor(side).Bounds.Height))};save={savePosition};viewport={viewport.Bounds};offset={viewport.Offset}");
        window.Width = 1280; window.Height = 850;
        Report("original sources remain full-byte unchanged", roots.Select(Hash).SequenceEqual(hashes) && File.ReadAllBytes(files[0]).SequenceEqual(originals[0]) && File.ReadAllBytes(files[2]).SequenceEqual(originals[2]));
        using var stream = File.Create(Path.Combine(folder, "facts.json")); using var json = new Utf8JsonWriter(stream, new() { Indented = true });
        json.WriteStartObject(); json.WriteString("workspace", workspace); json.WriteString("package", package); json.WriteString("folder", folder);
        json.WriteStartArray("rootSha256"); foreach (var hash in hashes) json.WriteStringValue(hash); json.WriteEndArray();
        json.WriteStartArray("snapshots"); foreach (var state in snapshots)
        { json.WriteStartObject(); json.WriteString("name", state.Name); json.WriteStartArray("hex"); foreach (var value in state.Hex) json.WriteStringValue(value); json.WriteEndArray(); json.WriteStartArray("dirty"); foreach (var value in state.Dirty) json.WriteBooleanValue(value); json.WriteEndArray(); json.WriteStartArray("readOnly"); foreach (var value in state.ReadOnly) json.WriteBooleanValue(value); json.WriteEndArray(); json.WriteEndObject(); } json.WriteEndArray();
        json.WriteStartArray("refusals"); foreach (var refusal in refusals) json.WriteStringValue(refusal); json.WriteEndArray(); json.WriteEndObject();
        foreach (var session in window.SessionPanes) session.DiscardChanges();

        void CheckPassword()
        {
            var encrypted = Path.Combine(folder, "encrypted.zip"); using (var resource = typeof(HeadlessBinaryThreeWayChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.Sources.two-passwords.zip")!) using (var file = File.Create(encrypted)) resource.CopyTo(file);
            var project = sourceProject with { BaseArchiveInput = new() { RootPath = encrypted, RootSha256 = Hash(encrypted), EntryChain = ["inner.zip"], LeafEntry = "leaf.txt", InheritedReadOnly = false } };
            var current = window.AddSession(); current.ApplyProject(project); var rounds = 0;
            current.ArchiveSourceRetryShown = dialog => { rounds++; if (rounds > 1) { dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return; } Report("central password retry is complete masked route", dialog.BasePasswords.Count == 2 && dialog.BasePasswords.All(box => box.PasswordChar == '●')); dialog.BasePasswords[0].Text = "outer-source-fixture"; dialog.BasePasswords[1].Text = "inner-source-fixture"; dialog.Retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            pump(current.CompareArchiveProjectAsync(middlePasswords: ["wrong-fixture", "wrong-fixture"])); current.ArchiveSourceRetryShown = null;
            Report("central explicit password retry opens Binary and normal save", rounds == 1 && Binary(current).HasMiddle && !Binary(current).MiddleReadOnly); pump(Binary(current).SaveAsync(1));
            var encryptedWorkspace = Path.Combine(folder, "encrypted-workspace.json"); pump(WorkspaceStore.SaveWorkspaceAsync(encryptedWorkspace, new() { Entries = [current.CaptureProject()] }));
            var text = File.ReadAllText(encryptedWorkspace); Report("central credentials absent from workspace", !text.Contains("source-fixture", StringComparison.Ordinal));
        }
        void CheckCandidateBoundaries()
        {
            var tableLeft = Path.Combine(folder, "candidate-left.csv"); var tableRight = Path.Combine(folder, "candidate-right.csv");
            File.WriteAllText(tableLeft, "id,value\n1,left\n"); File.WriteAllText(tableRight, "id,value\n1,right\n");
            var image = Path.Combine(folder, "candidate.png");
            using (var resource = typeof(HeadlessBinaryThreeWayChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.Images.disposal-2.png")!)
            using (var target = File.Create(image)) resource.CopyTo(target);
            var leftFolder = Path.Combine(folder, "candidate-left-folder"); var rightFolder = Path.Combine(folder, "candidate-right-folder");
            Directory.CreateDirectory(leftFolder); Directory.CreateDirectory(rightFolder);
            File.WriteAllText(Path.Combine(leftFolder, "value.txt"), "left"); File.WriteAllText(Path.Combine(rightFolder, "value.txt"), "right");
            foreach (var route in new[] { "Archive", "Image", "Folder", "Table" })
            {
                var pane = Add(new() { Mode = "Text", LeftPath = tableLeft, RightPath = tableRight });
                pane.ApplyProject(new() { Mode = "Binary", LeftPath = files[0], BasePath = files[1], RightPath = files[2] }); pump(pane.ComparePathsAsync());
                var old = Binary(pane); SetFirst(old, 1, 16); old.Undo();
                var originalSession = old.Session; var originalBytes = old.CaptureApplied(1).CopyBytes(); var originalRevision = old.Session.Revision(1);
                pane.ApplyProject(new() { Mode = route, LeftPath = route switch { "Archive" => roots[0], "Image" => image, "Folder" => leftFolder, _ => tableLeft },
                    RightPath = route switch { "Archive" => roots[2], "Image" => image, "Folder" => rightFolder, _ => tableRight } });
                var state = pane.CaptureAdoptionState(); var ready = 0; ArchivePanel? archiveCandidate = null; SpecializedViews.ImagePanel? imageCandidate = null;
                void LateEdit()
                {
                    ready++; Report("candidate boundary " + route + " middle editor enabled writable", old.MiddleHex.IsEffectivelyEnabled && !old.MiddleHex.IsReadOnly && !old.MiddleReadOnly,
                        $"effectivelyEnabled={old.MiddleHex.IsEffectivelyEnabled};readOnly={old.MiddleHex.IsReadOnly};middleReadOnly={old.MiddleReadOnly}");
                    old.MiddleHex.Text = "AA 09 02 05 FF 82";
                }
                pane.ArchiveReadyForAdoption = candidate => { archiveCandidate = candidate; LateEdit(); };
                pane.ImageReadyForAdoption = candidate => { imageCandidate = candidate; LateEdit(); };
                pane.DirectoryReadyForAdoption = pane.TableReadyForAdoption = LateEdit;
                pump(pane.ComparePathsAsync());
                pane.ArchiveReadyForAdoption = null; pane.ImageReadyForAdoption = null; pane.DirectoryReadyForAdoption = pane.TableReadyForAdoption = null;
                var retained = !old.IsDisposed && pane.GetVisualDescendants().Contains(old);
                Report("candidate boundary " + route + " retains old Binary draft bytes history", ready == 1 && retained && old.MiddleHex.Text == "AA 09 02 05 FF 82"
                    && old.Pending(1) && old.CaptureApplied(1).CopyBytes().SequenceEqual(originalBytes) && old.Session.Revision(1) == originalRevision
                    && ReferenceEquals(old.Session, originalSession) && !old.Session.CanUndo && old.Session.CanRedo && !old.Session.IsDirty(1) && old.Dirty(1),
                    $"ready={ready};retained={retained};draft={old.MiddleHex.Text};disposed={old.IsDisposed};coreRevisionBefore={originalRevision};editorVersion={old.StateVersion}");
                if (retained) snapshots.Add(("candidate-" + route, Enumerable.Range(0, 3).Select(side => Convert.ToHexString(old.CaptureApplied(side).CopyBytes())).ToArray(),
                    Enumerable.Range(0, 3).Select(old.Dirty).ToArray(), Enumerable.Range(0, 3).Select(old.ReadOnly).ToArray()));
                Report("candidate boundary " + route + " retains editor documents diff save state", Equals(state, pane.CaptureAdoptionState()));
                Report("candidate boundary " + route + " rejected resources released", (archiveCandidate is null || archiveCandidate.IsDisposed) && (imageCandidate is null || imageCandidate.IsDisposed));
                Report("candidate boundary " + route + " rejected status and compare button complete", pane.CompareButton.IsEnabled
                    && pane.ComparisonStatus == "比較中に入力が変更されたため、前の比較を保持しました。", pane.ComparisonStatus ?? "");
                window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), pane)); screenshot("binary-candidate-" + route.ToLowerInvariant() + ".png");
                pane.DiscardChanges(); pump(pane.ComparePathsAsync());
                Report("candidate boundary " + route + " clean retry adopts and clears Binary owner", old.IsDisposed && !pane.GetVisualDescendants().OfType<SpecializedViews.BinaryPanel>().Any()
                    && !pane.HasUnsavedChanges && (route == "Table" ? pane.GetVisualDescendants().OfType<TablePanel>().Any() : pane.CurrentDiff is null));
            }
            foreach (var kind in new[] { "cancel", "hook-exception", "path", "mode", "provider", "operation", "disposed" })
            {
                var pane = Ordinary(); var old = Binary(pane);
                pane.ApplyProject(new() { Mode = "Image", LeftPath = image, RightPath = image }); var state = pane.CaptureAdoptionState();
                SpecializedViews.ImagePanel? candidate = null;
                pane.ImageReadyForAdoption = value =>
                {
                    candidate = value;
                    if (kind == "cancel") pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (kind == "hook-exception") throw new InvalidOperationException("headless image adoption hook exception");
                    if (kind == "path") pane.LeftPath.Text = image + ".changed";
                    if (kind == "mode") pane.SelectMode(1);
                    if (kind == "provider") { var provider = pane.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Items.OfType<string>().Contains("tar-metadata")); provider.SelectedIndex = (provider.SelectedIndex + 1) % provider.Items.Count; }
                    if (kind == "operation") pane.ApplyProject(pane.CaptureProject());
                    if (kind == "disposed")
                    {
                        var tab = window.GetVisualDescendants().OfType<TabControl>().SelectMany(control => control.Items.OfType<TabItem>()).Single(item => ReferenceEquals(item.Content, pane));
                        ((StackPanel)tab.Header!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                };
                _ = Refused(() => pump(pane.ComparePathsAsync())); pane.ImageReadyForAdoption = null;
                Report("image adoption guard " + kind + " releases rejected candidate", candidate is { IsDisposed: true });
                if (kind != "disposed") Report("image adoption guard " + kind + " retains old owner and state", !old.IsDisposed && pane.GetVisualDescendants().Contains(old)
                    && Equals(state, pane.CaptureAdoptionState()) && AllBytes(old, originals));
            }
        }
        ComparisonPane Ordinary() => Add(new() { Mode = "Binary", LeftPath = files[0], BasePath = files[1], RightPath = files[2] });
        ComparisonPane Add(ComparisonProject project) { var value = window.AddSession(); value.ApplyProject(project); value.ArchiveSourceRetryShown = dialog => dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump(value.ComparePathsAsync()); value.ArchiveSourceRetryShown = null; return value; }
        ArchiveProjectInput Input(int side) => new() { RootPath = roots[side], RootSha256 = hashes[side], EntryChain = ["inner.zip"], LeafEntry = "leaf.bin", InheritedReadOnly = false };
        void Observe(string name, SpecializedViews.BinaryPanel value) => snapshots.Add((name, Enumerable.Range(0, 3).Select(side => Convert.ToHexString(value.Capture(side).CopyBytes())).ToArray(), Enumerable.Range(0, 3).Select(value.Dirty).ToArray(), Enumerable.Range(0, 3).Select(value.ReadOnly).ToArray()));
        void Report(string name, bool passed, string detail = "actual pane buttons and captures") { try { check("Binary threeway " + name, passed, detail); } catch (InvalidOperationException) { } }
        bool Refused(Action action) { try { action(); return false; } catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException or ArgumentException or OperationCanceledException or UnauthorizedAccessException) { refusals.Add(exception.GetType().Name + ": " + exception.Message); return true; } }
    }
    private static void SetFirst(SpecializedViews.BinaryPanel panel, int side, byte value) { var bytes = panel.Capture(side).CopyBytes(); bytes[0] = value; panel.Editor(side).Text = Convert.ToHexString(bytes); panel.Apply(side); }
    private static bool AllBytes(SpecializedViews.BinaryPanel panel, byte[][] expected) => Enumerable.Range(0, 3).All(side => panel.Capture(side).CopyBytes().SequenceEqual(expected[side]));
    private static SpecializedViews.BinaryPanel Binary(ComparisonPane value) => value.GetVisualDescendants().OfType<SpecializedViews.BinaryPanel>().Single();
    private static void Click(SpecializedViews.BinaryPanel panel, string title) { panel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, title)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    private static async Task Wait(Func<bool> ready) { for (var index = 0; index < 2000; index++) { if (ready()) return; await Task.Delay(5); } throw new TimeoutException("central save button did not complete"); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
