using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static partial class HeadlessFolderCopyChecks
{
    private static void RunFolderTree(MainWindow window, string parentRoot, Action<Task> wait,
        Action<string, bool, string> check, Action<string> screenshot, Action<ComparisonPane> activate)
    {
        var root = Path.Combine(parentRoot, "folder-tree"); Directory.CreateDirectory(root);
        var clock = Stopwatch.StartNew();
        var oldWidth = window.Width; var oldHeight = window.Height;
        var baselinePanes = window.SessionPanes.ToArray();
        var baselineActivePane = window.ActivePane;
        var ownedPanes = new List<ComparisonPane>();
        var checks = new List<(string Name, bool Passed)>();
        var views = new List<(string Name, string[] Expected, string[] Visible, string[] Expanded, string[] Selected)>();
        var layouts = new List<(string Name, double ListHeight, int Tabs, string[] Reachable)>();
        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var complete = false;
        var compareObservationActive = false;
        var left = Path.Combine(root, "two", "left"); var right = Path.Combine(root, "two", "right");
        var roots = new[] { "empty", "equal", "leftonly", "modified", "rightonly", "tree", "typeconflict" };
        var all = new[] { "empty", "equal", "equal/same.bin", "leftonly", "leftonly/one.bin", "modified", "modified/a.bin",
            "rightonly", "rightonly/one.bin", "tree", "tree/branch", "tree/branch/deep", "tree/branch/deep/leaf.bin", "typeconflict", "typeconflict/one.bin" };
        ComparisonPane? pane = null;
        try
        {
            if (baselinePanes.Length > 43) throw new InvalidOperationException("Tree verification requires two reserved tab slots within 45 tabs.");
            foreach (var side in new[] { left, right })
            {
                Directory.CreateDirectory(Path.Combine(side, "empty"));
                Put(side, "equal/same.bin", [0x10, 0x11]); Put(side, "modified/a.bin", side == left ? [0x20] : [0x21]);
                Put(side, "tree/branch/deep/leaf.bin", side == left ? [0x30] : [0x31]);
                Put(side, "filtered/hidden.bin", [0x70]);
            }
            Put(left, "leftonly/one.bin", [0x40]); Put(right, "rightonly/one.bin", [0x50]);
            Put(left, "typeconflict", [0x60]); Put(right, "typeconflict/one.bin", [0x61]);
            pane = AddOwnedSession(); pane.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, ExcludedPaths = "filtered" });
            wait(pane.ComparePathsAsync()); activate(pane);
            var tree = TreeMode(pane); Hit(tree); View("initial", roots);
            Hit(TreeButton(pane, "同じフォルダーを展開")); View("identical", ["empty", "equal", "equal/same.bin", "leftonly", "modified", "rightonly", "tree", "typeconflict"]);
            Hit(TreeButton(pane, "異なるフォルダーを展開")); View("add-different", all);
            Hit(TreeButton(pane, "すべて折り畳み")); View("collapse", roots);
            var row = Row(pane, "tree"); Hit(row.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "FolderTreeExpander"));
            View("row-expander", ["empty", "equal", "leftonly", "modified", "rightonly", "tree", "tree/branch", "typeconflict"]);
            SelectRow(pane, "tree"); var list = List(pane);
            FocusTreeRow("key-left", "tree"); Stroke(Key.Left, PhysicalKey.ArrowLeft); View("key-left", roots);
            Report("key-left selection retained", SelectedPath() == "tree");
            FocusTreeRow("modified-key-no-expand", "tree"); Stroke(Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.Control);
            View("modified-key-no-expand", roots);
            Report("modified key selection retained", SelectedPath() == "tree");
            FocusTreeRow("key-right", "tree"); Stroke(Key.Right, PhysicalKey.ArrowRight);
            View("key-right", ["empty", "equal", "leftonly", "modified", "rightonly", "tree", "tree/branch", "typeconflict"]);
            Report("key-right selection retained", SelectedPath() == "tree");
            FocusTreeRow("key-right-child", "tree"); Stroke(Key.Right, PhysicalKey.ArrowRight);
            Report("key-right moves to child", SelectedPath() == "tree/branch" && list.IsKeyboardFocusWithin);
            FocusTreeRow("key-left-parent", "tree/branch"); Stroke(Key.Left, PhysicalKey.ArrowLeft);
            Report("key-left moves to parent", SelectedPath() == "tree" && list.IsKeyboardFocusWithin);
            Hit(TreeButton(pane, "すべて展開")); View("all", all);
            SelectRow(pane, "tree/branch/deep/leaf.bin");
            Hit(Row(pane, "tree").GetVisualDescendants().OfType<Button>().Single(b => b.Name == "FolderTreeExpander"));
            Report("hidden selection cleared", List(pane).SelectedItems!.Count == 0);
            var beforeHidden = Snapshot(left).Concat(Snapshot(right)).ToArray(); var hiddenConfirmed = false;
            Dialogs.ConfirmationShown = dialog => { hiddenConfirmed = true; Answer(dialog, false); };
            Hit(TreeButton(pane, "選択をすべてコピー →")); if (pane.PendingFolderCopy is { } hidden) wait(hidden); CloseFolderMessages(window);
            Report("hidden selection copy rejected", !hiddenConfirmed && pane.LastFolderCopyResult is null);
            Report("hidden copy preserved all bytes", beforeHidden.SequenceEqual(Snapshot(left).Concat(Snapshot(right))));
            Dialogs.ConfirmationShown = null;
            Hit(TreeButton(pane, "すべて展開")); wait(pane.ComparePathsAsync()); Jobs(); View("same-recompare", all);
            Hit(TreeMode(pane)); Hit(TreeMode(pane)); View("off-on-retain", all);
            SelectRow(pane, "modified/a.bin");
            Dialogs.ConfirmationShown = dialog => Answer(dialog, true);
            Hit(TreeButton(pane, "選択をすべてコピー →")); if (pane.PendingFolderCopy is { } copy) wait(copy); else throw new InvalidOperationException("Tree copy task missing.");
            Dialogs.ConfirmationShown = null; View("copy-refresh-retain", all);
            Report("copy literal destination", File.ReadAllBytes(Path.Combine(right, "modified/a.bin")).SequenceEqual(new byte[] { 0x20 }));
            // hookは候補の投影準備より前。prepareAdoption自体の失敗注入ではない。
            foreach (var failure in new[] { "cancel", "input-roundtrip" })
            {
                Hit(TreeButton(pane, "すべて展開"));
                SelectRow(pane, "tree/branch/deep/leaf.bin");
                Hit(Row(pane, "tree").GetVisualDescendants().OfType<Button>().Single(b => b.Name == "FolderTreeExpander"));
                View("compare-" + failure + "-before", ["empty", "equal", "equal/same.bin", "leftonly", "leftonly/one.bin", "modified", "modified/a.bin", "rightonly", "rightonly/one.bin", "tree", "typeconflict", "typeconflict/one.bin"]);
                var before = CaptureTree(); var reached = false;
                pane.DirectoryReadyForAdoption = () => { reached = true; RejectCandidate(failure); };
                try { CompareViaButton("compare-" + failure); } catch (OperationCanceledException) when (failure == "cancel" && reached) { }
                finally { pane.DirectoryReadyForAdoption = null; }
                Jobs(); Report("compare-" + failure + " actual adoption hook", reached);
                Preserved("compare-" + failure, before);
                Report("compare-" + failure + " hidden selection zero", List(pane).SelectedItems!.Count == 0);
                StaleTreeRejected("compare-" + failure);
                Recover("compare-" + failure);
            }
            foreach (var failure in new[] { "cancel", "input-roundtrip" })
            {
                Hit(TreeButton(pane, "すべて展開")); SelectRow(pane, "tree/branch/deep/leaf.bin");
                var before = CaptureTree(); var reached = false; var confirmed = false;
                pane.DirectoryReadyForAdoption = () => { reached = true; RejectCandidate(failure); };
                Dialogs.ConfirmationShown = dialog => { confirmed = true; Answer(dialog, true); };
                try
                {
                    Hit(TreeButton(pane, "選択をすべてコピー →"));
                    wait(pane.PendingFolderCopy ?? throw new InvalidOperationException("Tree failed refresh copy task missing."));
                }
                finally { pane.DirectoryReadyForAdoption = null; Dialogs.ConfirmationShown = null; }
                Jobs(); Report("refresh-" + failure + " actual confirmation and adoption hook", confirmed && reached);
                Report("refresh-" + failure + " published leaf bytes", File.ReadAllBytes(Path.Combine(right, "tree/branch/deep/leaf.bin")).SequenceEqual(new byte[] { 0x30 })
                    && pane.LastFolderCopyResult is { } published && published.Entries.Any(entry => entry.Published));
                Preserved("refresh-" + failure, before);
                Report("refresh-" + failure + " visible leaf selection retained", List(pane).SelectedItems!.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).SequenceEqual(new[] { "tree/branch/deep/leaf.bin" }));
                StaleTreeRejected("refresh-" + failure);
                Recover("refresh-" + failure);
            }
            var treeFilter = Path.Combine(root, "tree-rules.flt");
            var originalFilter = System.Text.Encoding.UTF8.GetBytes("def: include\n## original\n");
            var replacementFilter = System.Text.Encoding.UTF8.GetBytes("def: include\n## modified\n");
            File.WriteAllBytes(treeFilter, originalFilter); File.SetLastWriteTimeUtc(treeFilter, FixedTime);
            pane.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, ExcludedPaths = "filtered", FileFilterPath = treeFilter });
            wait(pane.ComparePathsAsync()); if (TreeMode(pane).IsChecked != true) Hit(TreeMode(pane)); Hit(TreeButton(pane, "すべて展開"));
            SelectRow(pane, "tree/branch/deep/leaf.bin");
            Hit(Row(pane, "tree").GetVisualDescendants().OfType<Button>().Single(b => b.Name == "FolderTreeExpander"));
            var filterBefore = CaptureTree(); var filterReached = false;
            var filterLength = new FileInfo(treeFilter).Length; var filterTime = File.GetLastWriteTimeUtc(treeFilter);
            pane.DirectoryReadyForAdoption = () =>
            {
                filterReached = true; File.WriteAllBytes(treeFilter, replacementFilter); File.SetLastWriteTimeUtc(treeFilter, filterTime);
            };
            try { CompareViaButton("filter-stamp"); } finally { pane.DirectoryReadyForAdoption = null; }
            Jobs(); Report("filter same-size same-time content changed", filterReached && originalFilter.Length == replacementFilter.Length
                && new FileInfo(treeFilter).Length == filterLength && File.GetLastWriteTimeUtc(treeFilter) == filterTime
                && File.ReadAllBytes(treeFilter).SequenceEqual(replacementFilter) && !originalFilter.SequenceEqual(replacementFilter));
            Preserved("filter-stamp-adoption-rejected", filterBefore);
            StaleTreeRejected("filter-stamp");
            Report("filter-stamp hidden selection zero", List(pane).SelectedItems!.Count == 0);
            Recover("filter-stamp");
            // 成功採用後にstampだけを変え、staleフラグによる拒否とは別に実操作の拒否を照合する。
            File.WriteAllBytes(treeFilter, originalFilter); File.SetLastWriteTimeUtc(treeFilter, filterTime);
            Report("filter operation content-only invalidation", File.GetLastWriteTimeUtc(treeFilter) == filterTime && new FileInfo(treeFilter).Length == filterLength);
            StaleTreeRejected("filter-content-only");
            Report("filter-content-only hidden selection zero", List(pane).SelectedItems!.Count == 0);
            Recover("filter-content-only");
            var beforeOldConfirm = Snapshot(left).Concat(Snapshot(right)).ToArray();
            SelectRow(pane, "tree/branch/deep/leaf.bin"); Window? held = null;
            Dialogs.ConfirmationShown = dialog => held = dialog;
            Hit(TreeButton(pane, "選択をすべてコピー →"));
            var oldCopy = pane.PendingFolderCopy ?? throw new InvalidOperationException("Tree old confirmation task missing.");
            var confirmationClock = Stopwatch.StartNew();
            while (held is null && !oldCopy.IsCompleted)
            { Jobs(); CloseFolderMessages(window); if (confirmationClock.Elapsed.TotalSeconds > 10) throw new TimeoutException("Tree confirmation missing."); Thread.Sleep(2); }
            Report("held actual confirmation", held is not null);
            Report("busy tree controls disabled", !TreeMode(pane).IsEnabled && Titles.All(title => !TreeButton(pane, title).IsEnabled)
                && Row(pane, "tree").GetVisualDescendants().OfType<Button>().Where(b => b.Name == "FolderTreeExpander").All(b => !b.IsEnabled));
            // 無効なツリー操作を強制しない。入力文脈の変更による旧確認拒否を別スコープで検証する。
            pane.LeftPath.Text = right; pane.LeftPath.Text = left;
            if (held is not null) Answer(held, true); wait(oldCopy); Dialogs.ConfirmationShown = null;
            Report("stale confirmation input-context rejected", beforeOldConfirm.SequenceEqual(Snapshot(left).Concat(Snapshot(right))));
            wait(pane.ComparePathsAsync()); View("same-context-after-stale", all);
            var ignore = pane.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "大文字小文字を無視"));
            Hit(ignore); wait(pane.ComparePathsAsync()); View("configuration-reset", roots);
            var alternateLeft = Path.Combine(root, "alternate", "left"); var alternateRight = Path.Combine(root, "alternate", "right");
            Put(alternateLeft, "fresh/nested/a.bin", [0x80]); Put(alternateRight, "fresh/nested/a.bin", [0x81]);
            pane.LeftPath.Text = alternateLeft; pane.RightPath.Text = alternateRight; wait(pane.ComparePathsAsync()); View("root-reset", ["fresh"]);
            pane.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, Recursive = false, ExcludedPaths = "filtered" });
            wait(pane.ComparePathsAsync()); View("nonrecursive-flat", roots);
            Report("nonrecursive tree disabled", !TreeMode(pane).IsEnabled && Titles.All(title => !TreeButton(pane, title).IsEnabled));
            Report("nonrecursive literal unscanned", List(pane).Items.OfType<DirectoryEntry>().Single(e => e.RelativePath == "tree").LeftState?.ScanState == DirectoryScanState.Unscanned);
            var middle = Path.Combine(root, "three", "middle"); var threeLeft = Path.Combine(root, "three", "left"); var threeRight = Path.Combine(root, "three", "right");
            foreach (var side in new[] { threeLeft, middle, threeRight }) Put(side, "equal/one.bin", [0x90]);
            Put(threeLeft, "leftonly/one.bin", [0x91]); Put(middle, "baseonly/one.bin", [0x92]); Put(threeRight, "rightonly/one.bin", [0x93]);
            var firstPane = pane; pane = AddOwnedSession();
            pane.ApplyProject(new() { Mode = "Folder", LeftPath = threeLeft, BasePath = middle, RightPath = threeRight });
            wait(pane.ComparePathsAsync()); activate(pane); Report("new pane tree state reset", TreeMode(pane).IsChecked != true);
            Hit(TreeMode(pane)); View("three-roots", ["baseonly", "equal", "leftonly", "rightonly"]);
            Hit(TreeButton(pane, "異なるフォルダーを展開"));
            View("three-missing-side-different", ["baseonly", "baseonly/one.bin", "equal", "leftonly", "leftonly/one.bin", "rightonly", "rightonly/one.bin"]);
            Hit(TreeButton(pane, "同じフォルダーを展開"));
            View("three-identical-add", ["baseonly", "baseonly/one.bin", "equal", "equal/one.bin", "leftonly", "leftonly/one.bin", "rightonly", "rightonly/one.bin"]);
            pane = firstPane; activate(pane); pane.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, ExcludedPaths = "filtered" });
            wait(pane.ComparePathsAsync()); if (TreeMode(pane).IsChecked != true) Hit(TreeMode(pane)); Hit(TreeButton(pane, "すべて展開"));
            window.Width = 1280; window.Height = 850; Layout("normal");
            window.Width = 850; window.Height = 550; Layout("minimum");
            while (window.SessionPanes.Count < 45) AddOwnedSession(); activate(pane); Layout("minimum-45-tabs");
            Report("exact 45 tabs", window.SessionPanes.Count == 45);
            complete = true;
        }
        finally
        {
            Dialogs.ConfirmationShown = null; foreach (var ownedPane in ownedPanes) ownedPane.DirectoryReadyForAdoption = null; window.Width = oldWidth; window.Height = oldHeight;
            try
            {
                using var stream = File.Create(Path.Combine(root, "tree-observations.json")); using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                json.WriteStartObject(); json.WriteBoolean("complete", complete); json.WriteNumber("elapsedMilliseconds", clock.ElapsedMilliseconds);
                json.WriteString("oldConfirmScope", "actual confirmation + disabled tree controls; direct input-text context invalidation; no forced tree API");
                json.WriteString("candidateRejectionScope", "existing DirectoryReadyForAdoption hook before PrepareFolderCandidate/prepareAdoption; cancellation/input roundtrip/filter stamp; actual tree controls and existing CaptureAdoptionState; prepareAdoption exception not injected");
                json.WriteString("errorUncomparedChildrenScope", "static review only; no reliable actual comparison injection seam");
                json.WriteStartArray("checks"); foreach (var row in checks) { json.WriteStartObject(); json.WriteString("name", row.Name); json.WriteBoolean("passed", row.Passed); json.WriteEndObject(); } json.WriteEndArray();
                json.WriteStartArray("views"); foreach (var row in views)
                { json.WriteStartObject(); json.WriteString("name", row.Name); Strings("expected", row.Expected); Strings("visible", row.Visible); Strings("expectedExpandedVisible", row.Expected.Where(path => row.Expected.Any(child => child.StartsWith(path + "/", StringComparison.Ordinal)))); Strings("expandedVisible", row.Expanded); Strings("selected", row.Selected); json.WriteEndObject(); } json.WriteEndArray();
                json.WriteStartArray("layouts"); foreach (var row in layouts) { json.WriteStartObject(); json.WriteString("name", row.Name); json.WriteNumber("listHeight", row.ListHeight); json.WriteNumber("tabs", row.Tabs); Strings("reachableControls", row.Reachable); json.WriteEndObject(); } json.WriteEndArray();
                json.WriteStartArray("originalFiles"); foreach (var pair in originals.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                { json.WriteStartObject(); json.WriteString("path", pair.Key); json.WriteString("hex", Convert.ToHexString(pair.Value)); json.WriteString("sha256", Convert.ToHexString(SHA256.HashData(pair.Value))); var expectedFinal = pair.Key == "two/right/modified/a.bin" ? new byte[] { 0x20 } : pair.Key == "two/right/tree/branch/deep/leaf.bin" ? new byte[] { 0x30 } : pair.Value; json.WriteString("expectedFinalHex", Convert.ToHexString(expectedFinal)); json.WriteString("expectedFinalSha256", Convert.ToHexString(SHA256.HashData(expectedFinal))); if (File.Exists(Path.Combine(root, pair.Key))) json.WriteString("actualSha256", Hash(Path.Combine(root, pair.Key))); else json.WriteNull("actualSha256"); json.WriteEndObject(); } json.WriteEndArray();
                json.WriteEndObject();
                void Strings(string name, IEnumerable<string> values) { json.WriteStartArray(name); foreach (var value in values) json.WriteStringValue(value); json.WriteEndArray(); }
            }
            finally
            {
                // 観測結果を取得した後で、作成したタブだけを実ボタンで閉じる。
                var failures = new List<Exception>();
                var closed = new List<ComparisonPane>();
                foreach (var ownedPane in ownedPanes.AsEnumerable().Reverse())
                {
                    if (!window.SessionPanes.Contains(ownedPane)) continue;
                    try
                    {
                        ownedPane.DiscardChanges();
                        var countBefore = window.SessionPanes.Count;
                        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Items.OfType<TabItem>().Any(i => ReferenceEquals(i.Content, ownedPane)));
                        var item = tabs.Items.OfType<TabItem>().Single(i => ReferenceEquals(i.Content, ownedPane));
                        ((StackPanel)item.Header!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var closeClock = Stopwatch.StartNew();
                        while (window.SessionPanes.Contains(ownedPane))
                        {
                            Dispatcher.UIThread.RunJobs();
                            if (closeClock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Tree owned tab close exceeded five seconds.");
                            Thread.Sleep(2);
                        }
                        Dispatcher.UIThread.RunJobs();
                        if (window.SessionPanes.Count != countBefore - 1) throw new InvalidOperationException("Tree owned close changed an unexpected tab count.");
                        closed.Add(ownedPane);
                    }
                    catch (Exception error) { failures.Add(error); }
                }
                var baselineRestored = window.SessionPanes.Count == baselinePanes.Length && window.SessionPanes.SequenceEqual(baselinePanes);
                if (baselineActivePane is not null && window.SessionPanes.Contains(baselineActivePane)) activate(baselineActivePane);
                using (var cleanupStream = File.Create(Path.Combine(root, "tree-tab-cleanup.json")))
                using (var cleanupJson = new Utf8JsonWriter(cleanupStream, new JsonWriterOptions { Indented = true }))
                {
                    cleanupJson.WriteStartObject(); cleanupJson.WriteNumber("baselineCount", baselinePanes.Length);
                    cleanupJson.WriteNumber("ownedCount", ownedPanes.Count); cleanupJson.WriteNumber("closedCount", closed.Count);
                    cleanupJson.WriteNumber("finalCount", window.SessionPanes.Count); cleanupJson.WriteBoolean("baselineRestored", baselineRestored);
                    cleanupJson.WriteStartArray("failures"); foreach (var error in failures) cleanupJson.WriteStringValue(error.Message); cleanupJson.WriteEndArray(); cleanupJson.WriteEndObject();
                }
                if (!baselineRestored) failures.Add(new InvalidOperationException("Tree cleanup did not restore baseline panes and count."));
                if (failures.Count > 0) throw new AggregateException("Tree owned tab cleanup failed.", failures);
            }
        }

        ComparisonPane AddOwnedSession()
        {
            // 固定ツリーfixtureはflat/noneから開始し、全体設定の継承は別の専用検証で確認する。
            var sharedOptions = window.FolderOptions.Current;
            if (!window.FolderOptions.SetOptions(new() { TreeMode = false, InitialExpansion = 0 }))
                throw new InvalidOperationException("Tree fixture initial options could not be isolated.");
            try { var added = window.AddSession(); ownedPanes.Add(added); return added; }
            finally
            {
                if (!window.FolderOptions.SetOptions(sharedOptions))
                    throw new InvalidOperationException("Tree fixture shared options could not be restored.");
            }
        }
        void Put(string side, string relative, byte[] bytes)
        {
            var path = Path.Combine(side, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, FixedTime); originals.Add(Path.GetRelativePath(root, path).Replace('\\', '/'), bytes);
        }
        void Jobs() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); if (clock.Elapsed.TotalSeconds > 30) throw new TimeoutException("Tree verification 30-second cap exceeded; no timeout extension."); }
        void Hit(Control control, bool allowDisabled = false)
        {
            control.BringIntoView(); Jobs(); if (!allowDisabled && !control.IsEnabled) throw new InvalidOperationException("Disabled actual tree control: " + control.Name);
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Jobs();
        }
        void CompareViaButton(string name)
        {
            if (compareObservationActive) throw new InvalidOperationException("Tree compare observer helper reentered: " + name);
            var target = pane ?? throw new InvalidOperationException("Tree comparison pane missing.");
            var previousObserver = target.TextSaveTaskObserved;
            Task? observed = null; var notifications = 0; var notifying = false; string? observationError = null;
            compareObservationActive = true;
            try
            {
                target.TextSaveTaskObserved = (operation, task) =>
                {
                    if (notifying) { observationError = "observer callback reentered"; return; }
                    notifying = true;
                    try
                    {
                        if (operation == "path-compare")
                        {
                            if (++notifications != 1) observationError = "duplicate path-compare task notification";
                            else observed = task;
                        }
                        previousObserver?.Invoke(operation, task);
                    }
                    catch { observationError = "previous observer threw"; throw; }
                    finally { notifying = false; }
                };
                Hit(target.CompareButton);
                if (observationError is not null) throw new InvalidOperationException("Tree compare task observation failed: " + name + " · " + observationError);
                if (notifications != 1 || observed is null) throw new InvalidOperationException("Tree actual compare button task not observed: " + name);
                Report(name + " actual compare pointer task observed", true);
                wait(observed); Jobs();
            }
            finally
            {
                target.TextSaveTaskObserved = previousObserver; compareObservationActive = false;
                if (observationError is not null) throw new InvalidOperationException("Tree compare task observation failed: " + name + " · " + observationError);
            }
        }
        void RejectCandidate(string failure)
        {
            if (failure == "cancel") Hit(TreeButton(pane!, "中止"));
            else { pane!.LeftPath.Text = right; pane.LeftPath.Text = left; }
        }
        (object? Source, DirectoryEntry[] Rows, string[] Expanded, string[] Selected, bool? Checked, object Adoption) CaptureTree()
        {
            var list = List(pane!); var rows = list.Items.OfType<DirectoryEntry>().ToArray();
            var expanded = rows.Where(entry => Row(pane!, entry.RelativePath).GetVisualDescendants().OfType<Button>()
                .Any(button => button.Name == "FolderTreeExpander" && button.IsVisible && Equals(button.Content, "−"))).Select(entry => entry.RelativePath).ToArray();
            return (list.ItemsSource, rows, expanded, list.SelectedItems!.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).ToArray(), TreeMode(pane!).IsChecked, pane!.CaptureAdoptionState());
        }
        void Preserved(string name, (object? Source, DirectoryEntry[] Rows, string[] Expanded, string[] Selected, bool? Checked, object Adoption) before)
        {
            var after = CaptureTree();
            Report(name + " old ItemsSource and entry references retained", ReferenceEquals(before.Source, after.Source)
                && before.Rows.Length == after.Rows.Length && before.Rows.Zip(after.Rows).All(pair => ReferenceEquals(pair.First, pair.Second)));
            Report(name + " visible expansion retained", before.Expanded.SequenceEqual(after.Expanded));
            Report(name + " checkbox and selection retained", before.Checked == after.Checked && before.Selected.SequenceEqual(after.Selected));
            Report(name + " adopted state retained", Equals(before.Adoption, after.Adoption));
            View(name + "-retained", before.Rows.Select(entry => entry.RelativePath).ToArray());
        }
        void StaleTreeRejected(string name)
        {
            Report(name + " compare button restored", pane!.CompareButton.IsEnabled);
            var before = CaptureTree();
            // 無効状態も実pointerで押す。イベントやprivate APIの強制呼出しはしない。
            Hit(TreeMode(pane!), allowDisabled: true); CloseFolderMessages(window); Jobs();
            Preserved(name + "-checkbox-rejected", before);
            Hit(TreeButton(pane!, "すべて折り畳み"), allowDisabled: true); CloseFolderMessages(window); Jobs();
            Preserved(name + "-collapse-rejected", before);
            Hit(TreeButton(pane!, "すべて展開"), allowDisabled: true); CloseFolderMessages(window); Jobs();
            Preserved(name + "-expand-rejected", before);
        }
        void Recover(string name)
        {
            var oldSource = List(pane!).ItemsSource;
            CompareViaButton(name + "-recover"); Jobs();
            Report(name + " next comparison adopted new ItemsSource", !ReferenceEquals(oldSource, List(pane).ItemsSource) && pane.CompareButton.IsEnabled && TreeMode(pane).IsEnabled);
            // 展開操作で期待状態を作る前に、成功採用直後の継承またはresetを観測する。
            if (name.StartsWith("compare-", StringComparison.Ordinal))
            {
                View(name + "-recovered-before-controls", ["empty", "equal", "equal/same.bin", "leftonly", "leftonly/one.bin", "modified", "modified/a.bin", "rightonly", "rightonly/one.bin", "tree", "typeconflict", "typeconflict/one.bin"]);
                Report(name + " recovered hidden selection zero", List(pane).SelectedItems!.Count == 0);
                Hit(Row(pane, "tree").GetVisualDescendants().OfType<Button>().Single(button => button.Name == "FolderTreeExpander"));
                View(name + "-recovered-latent-expansion", all);
            }
            else if (name.StartsWith("refresh-", StringComparison.Ordinal))
            {
                View(name + "-recovered-before-controls", all);
                Report(name + " recovered visible leaf selection retained", List(pane).SelectedItems!.OfType<DirectoryEntry>()
                    .Select(entry => entry.RelativePath).SequenceEqual(new[] { "tree/branch/deep/leaf.bin" }));
            }
            else if (name.StartsWith("filter-", StringComparison.Ordinal))
            {
                // filter stamp変更時は別contextとして展開resetが仕様。
                View(name + "-recovered-context-reset", roots);
            }
            else throw new InvalidOperationException("Unknown tree recovery scope: " + name);
            Hit(TreeButton(pane, "すべて展開")); View(name + "-recovered", all);
            Hit(TreeButton(pane, "すべて折り畳み")); View(name + "-recovered-collapse", roots);
            Hit(TreeButton(pane, "すべて展開"));
        }
        string? SelectedPath() => (List(pane!).SelectedItem as DirectoryEntry)?.RelativePath;
        void FocusTreeRow(string name, string path)
        {
            var row = Row(pane!, path); row.Focus(); Jobs();
            Report(name + " real key focus and selected row", List(pane!).IsKeyboardFocusWithin && row.IsFocused && SelectedPath() == path);
        }
        void Stroke(Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            window.KeyPress(key, modifiers, physical, null); window.KeyRelease(key, modifiers, physical, null); Jobs();
        }
        void SelectRow(ComparisonPane target, string path) => Hit(Row(target, path));
        Control Row(ComparisonPane target, string path)
        {
            var list = List(target); var entry = list.Items.OfType<DirectoryEntry>().Single(e => e.RelativePath == path);
            list.ScrollIntoView(entry); Jobs(); return list.ContainerFromItem(entry) ?? throw new InvalidOperationException("Tree row unrealized: " + path);
        }
        void View(string name, string[] expected)
        {
            var visible = List(pane!).Items.OfType<DirectoryEntry>().Select(e => e.RelativePath).ToArray();
            var expanded = new List<string>();
            foreach (var path in visible)
                if (Row(pane!, path).GetVisualDescendants().OfType<Button>().Any(b => b.Name == "FolderTreeExpander" && b.IsVisible && Equals(b.Content, "−"))) expanded.Add(path);
            var selected = List(pane!).SelectedItems!.OfType<DirectoryEntry>().Select(e => e.RelativePath).ToArray();
            views.Add((name, expected, visible, expanded.ToArray(), selected)); Report(name + " literal preorder", visible.SequenceEqual(expected));
            var expectedExpanded = expected.Where(path => expected.Any(child => child.StartsWith(path + "/", StringComparison.Ordinal))).ToArray();
            Report(name + " literal visible expansion", expanded.SequenceEqual(expectedExpanded));
            Report(name + " selected visible only", selected.All(visible.Contains));
        }
        void Report(string name, bool passed)
        { checks.Add((name, passed)); try { check("Folder Tree " + name, passed, "fixed literal oracle; actual pointer/buttons/keys; separate tree artifacts"); } catch (InvalidOperationException) { } }
        Rect Intersect(Rect first, Rect second)
        {
            var x = Math.Max(first.X, second.X); var y = Math.Max(first.Y, second.Y);
            return new Rect(x, y, Math.Max(0, Math.Min(first.Right, second.Right) - x), Math.Max(0, Math.Min(first.Bottom, second.Bottom) - y));
        }
        void Layout(string name)
        {
            Jobs(); var list = List(pane!); var first = list.Items.OfType<DirectoryEntry>().First(); var row = Row(pane!, first.RelativePath);
            var reachable = new List<string>();
            var controls = new Control[] { TreeMode(pane!) }.Concat(Titles.Select(title => TreeButton(pane!, title))).Concat(new[] { "← 選択をすべてコピー", "選択をすべてコピー →", "← 選択の差分をコピー", "選択の差分をコピー →" }.Select(title => TreeButton(pane!, title))).ToArray();
            using var stream = File.Create(Path.Combine(root, name + "-bounds.json")); using var json = new Utf8JsonWriter(stream);
            json.WriteStartObject(); json.WriteNumber("width", window.Bounds.Width); json.WriteNumber("height", window.Bounds.Height); json.WriteNumber("tabs", window.SessionPanes.Count); json.WriteNumber("listHeight", list.Bounds.Height);
            json.WriteStartArray("controls"); foreach (var control in controls)
            {
                control.BringIntoView(); Jobs(); var point = control.TranslatePoint(default, window)!.Value;
                var inside = control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0 && point.X >= 0 && point.Y >= 0
                    && point.X + control.Bounds.Width <= window.Bounds.Width && point.Y + control.Bounds.Height <= window.Bounds.Height;
                var viewport = control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                var viewportPoint = viewport is null ? default : control.TranslatePoint(default, viewport)!.Value;
                if (viewport is not null) inside &= viewportPoint.Y >= -.01 && viewportPoint.Y + control.Bounds.Height <= viewport.Viewport.Height + .01;
                var title = control is ContentControl content ? content.Content?.ToString() ?? "" : control.Name ?? "";
                if (inside) reachable.Add(title); Report(name + " reachable " + title, inside);
                json.WriteStartObject(); json.WriteString("name", title); json.WriteNumber("x", point.X); json.WriteNumber("y", point.Y); json.WriteNumber("width", control.Bounds.Width); json.WriteNumber("height", control.Bounds.Height); json.WriteBoolean("reachable", inside); if (viewport is not null) { json.WriteNumber("viewportY", viewportPoint.Y); json.WriteNumber("viewportHeight", viewport.Viewport.Height); } json.WriteEndObject();
            }
            json.WriteEndArray();
            // ウィンドウ内だけでなく、実一覧のスクロールviewportに先頭行が見えることを測る。
            row = Row(pane!, first.RelativePath); row.BringIntoView(); Jobs();
            var rowPoint = row.TranslatePoint(default, window)!.Value;
            var listPoint = list.TranslatePoint(default, window)!.Value;
            var listScroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            var viewportControl = listScroll?.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>().FirstOrDefault();
            var listViewportPoint = viewportControl?.TranslatePoint(default, window) ?? listPoint;
            var listViewportSize = viewportControl?.Bounds.Size ?? listScroll?.Viewport ?? list.Bounds.Size;
            var listViewport = Intersect(new Rect(listViewportPoint, listViewportSize), new Rect(listPoint, list.Bounds.Size));
            listViewport = Intersect(listViewport, new Rect(default(Point), window.Bounds.Size));
            var rowIntersection = Intersect(new Rect(rowPoint, row.Bounds.Size), listViewport);
            var firstRowReachable = viewportControl is not null && row.IsEffectivelyVisible && row.Bounds.Height > 0 && rowIntersection.Width > 0 && rowIntersection.Height + .01 >= row.Bounds.Height;
            json.WriteNumber("firstRowX", rowPoint.X); json.WriteNumber("firstRowWidth", row.Bounds.Width); json.WriteNumber("firstRowY", rowPoint.Y); json.WriteNumber("firstRowHeight", row.Bounds.Height);
            json.WriteBoolean("listViewportMeasured", viewportControl is not null); json.WriteNumber("listViewportX", listViewport.X); json.WriteNumber("listViewportY", listViewport.Y);
            json.WriteNumber("listViewportWidth", listViewport.Width); json.WriteNumber("listViewportHeight", listViewport.Height);
            json.WriteNumber("firstRowIntersectionWidth", rowIntersection.Width); json.WriteNumber("firstRowIntersectionHeight", rowIntersection.Height);
            json.WriteBoolean("firstRowReachable", firstRowReachable); json.WriteEndObject();
            Report(name + " list 100 DIP", viewportControl is not null && list.Bounds.Height >= 100 && listViewport.Height >= 100); Report(name + " first row reachable", firstRowReachable);
            layouts.Add((name, list.Bounds.Height, window.SessionPanes.Count, reachable.ToArray())); screenshot("folder-tree-" + name + ".png");
        }
    }
    private static readonly string[] Titles = ["すべて展開", "異なるフォルダーを展開", "同じフォルダーを展開", "すべて折り畳み"];
    private static CheckBox TreeMode(ComparisonPane pane) => pane.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "FolderTreeMode");
    private static Button TreeButton(ComparisonPane pane, string title) => pane.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, title));
}
