using System.Diagnostics;
using System.Text;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

// 実アプリのself-test内で実controlを操作する。OSの設定パスとclipboardには触れない。
internal static class HeadlessFolderOptionsChecks
{
    internal static void Run(string output, Action<string, bool, string> check,
        Action<MainWindow, string> screenshot)
    {
        var root = Path.Combine(output, "folder-options"); Directory.CreateDirectory(root);
        var assertions = new List<(string Name, bool Passed)>();
        var views = new List<(string Name, string[] Expected, string[] Actual)>();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var layouts = new List<(string Name, double Width, double Height, double ListHeight, Rect Mode, Rect Policy)>();
        var raw = new List<(string Name, Action<Utf8JsonWriter> Write)>();
        var taskEvidence = new Dictionary<Task, OwnedTaskEvidence>(ReferenceEqualityComparer.Instance);
        var referenceTokens = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        int Token(object? value) { if (value is null) return 0; if (!referenceTokens.TryGetValue(value, out var token)) referenceTokens.Add(value, token = referenceTokens.Count + 1); return token; }
        string[] collapsed = ["diff", "equal"];
        string[] all = ["diff", "diff/a.bin", "equal", "equal/a.bin"];
        string[][] policies = [collapsed, all, ["diff", "diff/a.bin", "equal"], ["diff", "equal", "equal/a.bin"]];
        var memoryDefaults = new FolderApplicationOptionsStore(); var desktopDefaults = new FolderApplicationOptionsStore(desktopDefaults: true);
        CaptureStore("defaults/memory", memoryDefaults, null, "literal:false/0"); CaptureStore("defaults/desktop", desktopDefaults, null, "literal:true/0");
        Report("legacy headless defaults flat none", !memoryDefaults.Current.TreeMode && memoryDefaults.Current.InitialExpansion == 0);
        Report("desktop defaults tree none without OS access", desktopDefaults.Current.TreeMode && desktopDefaults.Current.InitialExpansion == 0);
        for (var policy = 0; policy < 4; policy++) RunPolicy(policy);
        RunStartup();
        RunProjectCompatibility();
        RunAssetOnly();
        RunLayoutOnly();
        WriteEvidence();

        void RunPolicy(int policy)
        {
            var caseRoot = Path.Combine(root, "policy-" + policy); Directory.CreateDirectory(caseRoot);
            var left = Fixture(caseRoot, "left", 1); var right = Fixture(caseRoot, "right", 2);
            var settingsPath = Path.Combine(caseRoot, "folder-options.json");
            var store = new FolderApplicationOptionsStore(settingsPath);
            CaptureStore("policy-" + policy + "/seed/before", store, settingsPath, "initialExpansion=" + policy);
            var seeded = store.SetOptions(new() { InitialExpansion = policy });
            CaptureStore("policy-" + policy + "/seed/after", store, settingsPath, "initialExpansion=" + policy);
            Report("policy " + policy + " seed", seeded);
            var window = new MainWindow(null, new ImageApplicationOptionsStore(), store) { Width = 1280, Height = 850 };
            var clock = Stopwatch.StartNew(); var tasks = new List<Task>(); Exception? caseFailure = null; ComparisonPane? peer = null; string? peerPath = null; var operationSequence = 0;
            try
            {
                window.Show(); var pane = window.ActivePane;
                pane.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, Recursive = true });
                Compare(pane); View("initial", policies[policy]);
                Hit(Button(pane, "すべて展開")); View("manual-all", all);
                Compare(pane); View("same-context-keeps-manual", all);
                Hit(Button(pane, "すべて折り畳み")); View("manual-none", collapsed);
                Attempt("policy-save", () => SelectPolicy(pane, (policy + 1) % 4)); View("policy-does-not-reproject-current", collapsed);
                var next = (policy + 1) % 4;
                var nextLeft = Fixture(caseRoot, "next-left", 1); var nextRight = Fixture(caseRoot, "next-right", 2);
                pane.ApplyProject(new() { Mode = "Folder", LeftPath = nextLeft, RightPath = nextRight, Recursive = true });
                Compare(pane); View("new-context-applies-policy", policies[next]);
                var oldRows = Rows(pane).ItemsSource; var settings = File.ReadAllBytes(settingsPath);
                File.SetAttributes(settingsPath, File.GetAttributes(settingsPath) | FileAttributes.ReadOnly);
                try
                {
                    Attempt("readonly-mode", () => Hit(Mode(pane))); Preserved("readonly-mode", oldRows, settings);
                    Attempt("readonly-policy", () => SelectPolicy(pane, policy)); Preserved("readonly-policy", oldRows, settings);
                }
                finally { File.SetAttributes(settingsPath, File.GetAttributes(settingsPath) & ~FileAttributes.ReadOnly); }
                Action<string> deny = _ => throw new InvalidOperationException("injected settings output guard");
                store.AddOutputGuard(deny);
                try { Attempt("guard-mode", () => Hit(Mode(pane))); Preserved("guard-mode", oldRows, settings); }
                finally { store.RemoveOutputGuard(deny); }
                store.SaveBeforePublish = () => throw new OperationCanceledException("settings publish cancelled");
                try { Attempt("cancel-publish", () => Hit(Mode(pane))); Preserved("cancel-publish", oldRows, settings); }
                finally { store.SaveBeforePublish = null; }
                store.SaveBeforePublish = () => { pane.LeftPath.Text = nextRight; pane.LeftPath.Text = nextLeft; };
                try { Attempt("stale-publish", () => Hit(Mode(pane))); Preserved("stale-publish", oldRows, settings); }
                finally { store.SaveBeforePublish = null; }
                Compare(pane);
                peerPath = Path.Combine(caseRoot, "peer-saved.txt"); File.WriteAllBytes(peerPath, Encoding.UTF8.GetBytes("saved peer\n"));
                peer = window.AddSession(); peer.ApplyProject(new() { Mode = "Text", LeftPath = peerPath, RightPath = peerPath }); AwaitOwned(peer.ComparePathsAsync(), clock, tasks, "dirty-text-peer-compare", "policy-" + policy); Jobs();
                // 実TextBoxの履歴を作る。countは非公開なので実Undo/Redoで保存点を検証する。
                peer.LeftEditor.Focus(); peer.LeftEditor.CaretIndex = peer.LeftEditor.Text?.Length ?? 0;
                peer.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1; Jobs();
                peer.LeftEditor.Focus(); window.KeyTextInput("edited peer"); Jobs();
                State("peer-created-dirty", pane);
                Report("real dirty peer with actual Undo history " + policy, peer.TextDirty(0) && peer.LeftEditor.CanUndo && peer.LeftEditor.Text == "saved peer\nedited peer");
                PeerHistory("peer-history-created"); window.SelectSession(0); Jobs();
                foreach (var failure in new[] { "cancel", "stale", "busy-control" })
                {
                    oldRows = Rows(pane).ItemsSource; settings = File.ReadAllBytes(settingsPath); var reached = false; var cancelClicked = false; var cancellationCaught = false; Task? failureTask = null;
                    var beforeCancelRows = Token(oldRows); var beforeCancelMode = Mode(pane).IsChecked; var beforeCancelPolicy = Policy(pane).SelectedIndex; var beforeCancelBytes = settings;
                    State("comparison-" + failure + "/before", pane);
                    pane.DirectoryReadyForAdoption = () =>
                    {
                        reached = true;
                        if (failure == "cancel") { Hit(Button(pane, "中止")); cancelClicked = true; }
                        else if (failure == "stale") { pane.LeftPath.Text = nextRight; pane.LeftPath.Text = nextLeft; }
                        else { Hit(Mode(pane), allowDisabled: true); Hit(Policy(pane), allowDisabled: true); }
                    };
                    try { Compare(pane, task => failureTask = task); } catch (OperationCanceledException) when (failure == "cancel" && reached) { cancellationCaught = true; }
                    finally { pane.DirectoryReadyForAdoption = null; }
                    State("comparison-" + failure + "/after", pane); PeerHistory("comparison-" + failure + "/history");
                    Report("comparison " + failure + " hook reached " + policy, reached);
                    if (failure != "busy-control") Preserved("comparison-" + failure, oldRows, settings);
                    else Report("comparison busy controls preserve settings " + policy, File.ReadAllBytes(settingsPath).SequenceEqual(settings));
                    if (failure == "cancel" && failureTask is not null)
                    {
                        var afterCancelRows = Token(Rows(pane).ItemsSource); var afterCancelMode = Mode(pane).IsChecked; var afterCancelPolicy = Policy(pane).SelectedIndex; var afterCancelBytes = File.ReadAllBytes(settingsPath);
                        var preserved = beforeCancelRows > 0 && beforeCancelRows == afterCancelRows && beforeCancelMode == true && afterCancelMode == true
                            && beforeCancelPolicy == next && afterCancelPolicy == next && beforeCancelBytes.SequenceEqual(afterCancelBytes);
                        var expectedCanceled = reached && cancelClicked && cancellationCaught && failureTask.IsCanceled && preserved;
                        // 固定cancel caseのこの実Taskだけを、到達・実中止・保持を確認後に許容する。
                        taskEvidence[failureTask] = taskEvidence[failureTask] with
                        {
                            Case = "policy-" + policy + "/comparison-cancel", Hook = "DirectoryReadyForAdoption", ExpectedTerminal = expectedCanceled ? "Canceled" : "RanToCompletion",
                            CancellationEvidence = json =>
                            {
                                json.WriteBoolean("hookReached", reached); json.WriteBoolean("stopButtonClicked", cancelClicked); json.WriteBoolean("operationCanceledExceptionCaught", cancellationCaught);
                                json.WriteString("beforeRawName", "policy-" + policy + "/comparison-cancel/before"); json.WriteString("afterRawName", "policy-" + policy + "/comparison-cancel/after");
                                json.WriteNumber("beforeRowsToken", beforeCancelRows); json.WriteNumber("afterRowsToken", afterCancelRows);
                                if (beforeCancelMode is bool beforeMode) json.WriteBoolean("beforeTreeMode", beforeMode); else json.WriteNull("beforeTreeMode");
                                if (afterCancelMode is bool afterMode) json.WriteBoolean("afterTreeMode", afterMode); else json.WriteNull("afterTreeMode");
                                json.WriteNumber("literalExpectedPolicy", next); json.WriteNumber("beforePolicy", beforeCancelPolicy); json.WriteNumber("afterPolicy", afterCancelPolicy);
                                Bytes(json, "settingsBefore", beforeCancelBytes); Bytes(json, "settingsAfter", afterCancelBytes);
                            }
                        };
                        Report("comparison cancel actual canceled task and preservation " + policy, expectedCanceled);
                        if (!expectedCanceled) throw new InvalidOperationException("Fixed comparison-cancel case did not prove its expected canceled terminal state and preserved display/settings.");
                    }
                    Compare(pane);
                }
                // 他tabの入力として設定fileを登録し、activepaneからの設定保存を拒否する。
                peer.DiscardChanges();
                var protectedPane = window.AddSession(); var protectedIndex = window.SessionPanes.Count - 1;
                protectedPane.ApplyProject(new() { Mode = "Text", LeftPath = settingsPath, RightPath = settingsPath });
                window.SelectSession(0); Jobs(); oldRows = Rows(pane).ItemsSource; settings = File.ReadAllBytes(settingsPath);
                Attempt("all-tab-input", () => Hit(Mode(pane))); Preserved("all-tab-input", oldRows, settings);
                protectedPane.ApplyProject(new() { Mode = "Text", LeftPath = left, RightPath = right, FileFilterPath = settingsPath });
                Attempt("all-tab-filter", () => Hit(Mode(pane))); Preserved("all-tab-filter", oldRows, settings);
                protectedPane.ApplyProject(new() { Mode = "Text", LeftPath = settingsPath, RightPath = settingsPath, LeftReadOnly = true, RightReadOnly = true });
                Attempt("all-tab-readonly", () => Hit(Mode(pane))); Preserved("all-tab-readonly", oldRows, settings);
                protectedPane.ApplyProject(new() { Mode = "Text", LeftPath = left, RightPath = right });
                // 新しいタブだけ最新既定値を読み、既存tabは自身のmode/policyを維持する。
                Attempt("mode-save", () => Hit(Mode(pane))); Report("mode click persisted " + policy, store.Current.TreeMode == false && Mode(pane).IsChecked == false);
                window.SelectSession(protectedIndex); protectedPane.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, Recursive = true });
                Compare(protectedPane); State("existing-tab/actual", protectedPane);
                Report("existing tab keeps creation mode " + policy, Mode(protectedPane).IsChecked == true);
                var newPane = window.AddSession(); newPane.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, Recursive = true }); Compare(newPane); State("new-tab/actual", newPane);
                Report("new tab reads latest mode policy " + policy, Mode(newPane).IsChecked == false && Policy(newPane).SelectedIndex == next);
                var reopened = new FolderApplicationOptionsStore(settingsPath);
                CaptureStore("policy-" + policy + "/reload/after", reopened, settingsPath, "reload-existing-options");
                Report("reload matches bytes " + policy, reopened.Current == store.Current && reopened.Diagnostic is null);
                // 旧tabからpolicyを保存しても、新tab向けmodeと自身のmodeを同時変更しない。
                window.SelectSession(protectedIndex); Jobs(); Attempt("individual-policy-save", () => SelectPolicy(protectedPane, policy), protectedPane);
                Report("individual option save preserves other option " + policy, !store.Current.TreeMode && Mode(protectedPane).IsChecked == true);
                window.Width = 850; window.Height = 550; Jobs();
                Mode(protectedPane).BringIntoView(); Jobs(); screenshot(window, "folder-options-policy-" + policy + "-minimum.png");
                Policy(protectedPane).BringIntoView(); Jobs(); screenshot(window, "folder-options-policy-" + policy + "-policy-minimum.png");
                var list = Rows(protectedPane); Report("minimum list 100 DIP " + policy, list.Bounds.Height >= 100);
                Layout("policy-" + policy + "-minimum", protectedPane);
                // raw optionsを保持してから、同じpathをworkspace保存先に採用する。
                var rawOptions = Path.Combine(caseRoot, "saved-settings-raw.json"); File.Copy(settingsPath, rawOptions);
                files[Path.GetRelativePath(root, rawOptions)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rawOptions)));
                State("workspace-save/before", protectedPane); AwaitOwned(window.SaveWorkspaceAsync(settingsPath), clock, tasks, "workspace-guard-save", "policy-" + policy); State("workspace-save/after", protectedPane); settings = File.ReadAllBytes(settingsPath);
                oldRows = Rows(protectedPane).ItemsSource;
                Attempt("workspace-guard", () => Hit(Mode(protectedPane)), protectedPane);
                Report("workspace guard preserves display and bytes " + policy, Mode(protectedPane).IsChecked == true && ReferenceEquals(Rows(protectedPane).ItemsSource, oldRows) && settings.SequenceEqual(File.ReadAllBytes(settingsPath)));

                foreach (var invalid in new[] { "null", "{\"initialExpansion\":4}", "{\"treeMode\":0}", "{\"unknown\":1}", new string(' ', FolderApplicationOptionsStore.MaximumBytes + 1) })
                {
                    var badPath = Path.Combine(caseRoot, "invalid-" + Guid.NewGuid().ToString("N") + ".json");
                    File.WriteAllText(badPath, invalid); var badBytes = File.ReadAllBytes(badPath);
                    var invalidStore = new FolderApplicationOptionsStore(badPath);
                    CaptureStore("policy-" + policy + "/invalid-loaded", invalidStore, badPath, invalid);
                    Report("invalid settings preserve bytes " + policy + " " + invalid.Length, invalidStore.Diagnostic is not null && invalidStore.Current == new FolderApplicationOptions() && badBytes.SequenceEqual(File.ReadAllBytes(badPath)));
                    files[Path.GetRelativePath(root, badPath)] = Convert.ToHexString(SHA256.HashData(badBytes));
                }
                var directoryOptions = new FolderApplicationOptionsStore(Path.Combine(caseRoot, "left"));
                CaptureStore("policy-" + policy + "/directory/before", directoryOptions, Path.Combine(caseRoot, "left"), "directory");
                var directoryResult = directoryOptions.SetOptions(new() { TreeMode = false });
                CaptureStore("policy-" + policy + "/directory/after", directoryOptions, Path.Combine(caseRoot, "left"), "treeMode:false");
                Report("directory settings rejected " + policy, directoryOptions.Diagnostic is not null && !directoryResult);
                settings = File.ReadAllBytes(settingsPath);
                var linkPath = Path.Combine(caseRoot, "linked-options.json");
                File.CreateSymbolicLink(linkPath, settingsPath);
                var linked = new FolderApplicationOptionsStore(linkPath);
                CaptureStore("policy-" + policy + "/link/before", linked, settingsPath, "link:" + linkPath);
                var linkedResult = linked.SetOptions(new() { TreeMode = false });
                CaptureStore("policy-" + policy + "/link/after", linked, settingsPath, "treeMode:false;link:" + linkPath);
                Report("linked settings rejected " + policy, linked.Diagnostic is not null && !linkedResult && File.ReadAllBytes(settingsPath).SequenceEqual(settings));
                var directSerial = store.SetOptionsAttemptCount; State("direct-invalid/before", protectedPane); var invalidDirect = store.SetOptions(store.Current with { InitialExpansion = int.MaxValue }); State("direct-invalid/after", protectedPane, directSerial);
                CaptureStore("policy-" + policy + "/direct-invalid/request", store, settingsPath, "initialExpansion:2147483647");
                Report("invalid direct policy rejected " + policy, !invalidDirect);
                files[Path.GetRelativePath(root, settingsPath)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(settingsPath)));
                foreach (var file in Directory.EnumerateFiles(caseRoot, "*.bin", SearchOption.AllDirectories))
                    files[Path.GetRelativePath(root, file)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

                void State(string name, ComparisonPane target, long? beforeSerial = null) => CaptureState("policy-" + policy + "/" + name, store, settingsPath, target, peer, peerPath, beforeSerial);
                void Attempt(string name, Action action, ComparisonPane? target = null)
                {
                    var actual = target ?? pane; var serial = store.SetOptionsAttemptCount; State(name + "/before", actual);
                    try { action(); } finally { State(name + "/after", actual, serial); }
                }
                void PeerHistory(string name)
                {
                    if (peer is null) throw new InvalidOperationException("Dirty text peer missing.");
                    State(name + "/before-undo", pane); peer.LeftEditor.Undo(); Jobs(); State(name + "/after-undo", pane);
                    Report(name + " actual Undo reaches literal saved point " + policy, peer.LeftEditor.Text == "saved peer\n" && !peer.TextDirty(0) && peer.LeftEditor.CanRedo);
                    peer.LeftEditor.Redo(); Jobs(); State(name + "/after-redo", pane);
                    Report(name + " actual Undo/Redo restores literal peer and dirty " + policy, peer.LeftEditor.Text == "saved peer\nedited peer" && peer.TextDirty(0) && peer.LeftEditor.CanUndo && !peer.LeftEditor.CanRedo);
                }
                void Preserved(string name, object? source, byte[] originalSettings)
                {
                    Report(name + " rows/settings/mode retained " + policy, ReferenceEquals(Rows(pane).ItemsSource, source)
                        && Mode(pane).IsChecked == true && Policy(pane).SelectedIndex == next
                        && File.ReadAllBytes(settingsPath).SequenceEqual(originalSettings));
                }
                void View(string name, string[] expected)
                {
                    Jobs(); State(name + "/view", pane); var actual = Rows(pane).Items.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).ToArray();
                    views.Add(("policy-" + policy + "/" + name, expected, actual));
                    Report(name + " literal rows " + policy, actual.SequenceEqual(expected));
                    Layout("policy-" + policy + "-" + name, pane);
                    screenshot(window, "folder-options-policy-" + policy + "-" + name + ".png");
                }
                void Layout(string name, ComparisonPane target)
                {
                    var mode = Mode(target); var policyControl = Policy(target);
                    layouts.Add((name, window.Bounds.Width, window.Bounds.Height, Rows(target).Bounds.Height,
                        new Rect(mode.TranslatePoint(default, window)!.Value, mode.Bounds.Size),
                        new Rect(policyControl.TranslatePoint(default, window)!.Value, policyControl.Bounds.Size)));
                }
                void Compare(ComparisonPane target, Action<Task>? observe = null)
                {
                    var serialBefore = store.SetOptionsAttemptCount; var rawName = "compare-" + (++operationSequence); State(rawName + "/before", target);
                    Task? pending = null; var oldObserver = target.TextSaveTaskObserved;
                    target.TextSaveTaskObserved = (route, task) => { if (route == "path-compare") { pending = task; RegisterOwned(task, tasks, rawName, "policy-" + policy, "path-compare"); observe?.Invoke(task); } oldObserver?.Invoke(route, task); };
                    try { Hit(target.CompareButton); if (pending is null) throw new InvalidOperationException("Actual compare button did not expose operation."); AwaitOwned(pending, clock, tasks); Jobs(); }
                    finally { target.TextSaveTaskObserved = oldObserver; State(rawName + "/after", target, serialBefore); }
                }
                void SelectPolicy(ComparisonPane target, int value)
                {
                    var serialBefore = store.SetOptionsAttemptCount; var rawName = "select-policy-" + (++operationSequence); State(rawName + "/before", target);
                    var combo = Policy(target); Hit(combo); Stroke(Key.Home, PhysicalKey.Home);
                    for (var i = 0; i < value; i++) Stroke(Key.Down, PhysicalKey.ArrowDown);
                    Stroke(Key.Enter, PhysicalKey.Enter); Jobs(); State(rawName + "/after", target, serialBefore);
                }
                void Stroke(Key key, PhysicalKey physical)
                {
                    var serialBefore = store.SetOptionsAttemptCount; var rawName = "key-" + (++operationSequence) + "-" + key; State(rawName + "/before", window.ActivePane);
                    window.KeyPress(key, RawInputModifiers.None, physical, null); window.KeyRelease(key, RawInputModifiers.None, physical, null); Jobs(); State(rawName + "/after", window.ActivePane, serialBefore);
                }
                void Hit(Control control, bool allowDisabled = false)
                {
                    var serialBefore = store.SetOptionsAttemptCount; var rawName = "click-" + (++operationSequence) + "-" + (control.Name ?? "unnamed-control"); State(rawName + "/before", window.ActivePane);
                    control.BringIntoView(); Jobs(); if (!allowDisabled && !control.IsEnabled) throw new InvalidOperationException("Disabled actual control: " + control.Name);
                    var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                    window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Jobs(); State(rawName + "/after", window.ActivePane, serialBefore);
                }
                void Jobs()
                { OwnedJobs(clock); }
            }
            catch (Exception error) { caseFailure = error; throw; }
            finally { EndOwnedCase(caseRoot, window, tasks, clock, caseFailure, -1); }
        }
        void CaptureStore(string name, FolderApplicationOptionsStore store, string? path, string literal)
        {
            var current = store.Current; var diagnostic = store.Diagnostic; var result = store.LastSetOptionsResult; var serial = store.SetOptionsAttemptCount;
            var kind = path is null ? "memory" : Directory.Exists(path) ? "directory" : File.Exists(path) ? "file" : "missing";
            var bytes = path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
            raw.Add((name, json =>
            {
                json.WriteString("literal", literal); json.WriteString("settingsPath", path); json.WriteString("targetKind", kind);
                Options(json, "Current", current); json.WriteString("diagnostic", diagnostic); json.WriteNumber("setOptionsAttemptCount", serial);
                if (result.HasValue) json.WriteBoolean("lastSetOptionsResult", result.Value); else json.WriteNull("lastSetOptionsResult");
                Bytes(json, "settingsBytes", bytes);
            }));
        }
        void CaptureState(string name, FolderApplicationOptionsStore store, string settingsPath, ComparisonPane pane, ComparisonPane? peer, string? peerPath, long? beforeSerial = null)
        {
            var current = store.Current; var checkedMode = Mode(pane).IsChecked; var policy = Policy(pane).SelectedIndex;
            var modeEnabled = Mode(pane).IsEnabled; var policyEnabled = Policy(pane).IsEnabled; var compareEnabled = pane.CompareButton.IsEnabled;
            var rows = Rows(pane).Items.OfType<DirectoryEntry>().Select(item => item.RelativePath).ToArray();
            var selected = Rows(pane).SelectedItems?.OfType<DirectoryEntry>().Select(item => item.RelativePath).ToArray() ?? [];
            var itemsSourceToken = Token(Rows(pane).ItemsSource); var adoptionToken = Token(pane.FolderOptionsAdoptedModel);
            var settings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
            var body = Encoding.UTF8.GetBytes(pane.LeftEditor.Text ?? ""); var diagnostic = store.Diagnostic; var status = pane.ComparisonStatus;
            var serial = store.SetOptionsAttemptCount; var lastResult = store.LastSetOptionsResult; var saveResult = beforeSerial.HasValue && serial > beforeSerial.Value ? lastResult : null;
            var peerBody = peer is null ? null : Encoding.UTF8.GetBytes(peer.LeftEditor.Text ?? "");
            var peerSource = peerPath is not null && File.Exists(peerPath) ? File.ReadAllBytes(peerPath) : null;
            var revision = peer?.CaptureIndependentTextRevisions(); var dirty = peer?.TextDirty(0); var canUndo = peer?.LeftEditor.CanUndo; var canRedo = peer?.LeftEditor.CanRedo;
            // CaptureAdoptionStateは既存internal API。ITupleはreflectionを使わず、保存済み本文と実document referenceを読む。
            var adoption = peer?.CaptureAdoptionState() as ITuple;
            var savepoint = adoption is null ? null : Encoding.UTF8.GetBytes((string?)adoption[13] ?? "");
            var comparisonToken = Token(peer?.CurrentDiff); var documentToken = Token(adoption?[0]);
            raw.Add((name, json =>
            {
                Options(json, "Current", current); json.WriteStartObject("controls");
                if (checkedMode.HasValue) json.WriteBoolean("treeMode", checkedMode.Value); else json.WriteNull("treeMode");
                json.WriteNumber("initialExpansion", policy); json.WriteBoolean("modeEnabled", modeEnabled); json.WriteBoolean("policyEnabled", policyEnabled); json.WriteBoolean("compareEnabled", compareEnabled); json.WriteEndObject();
                StringValues(json, "rowPaths", rows); StringValues(json, "selection", selected);
                json.WriteNumber("itemsSourceToken", itemsSourceToken); json.WriteNumber("adoptionToken", adoptionToken);
                json.WriteNumber("setOptionsAttemptCount", serial); if (lastResult.HasValue) json.WriteBoolean("lastSetOptionsResult", lastResult.Value); else json.WriteNull("lastSetOptionsResult");
                // UIが保存APIを呼ばなかった場合は前回returnを今回の結果にしない。readerは前後serialの差を照合する。
                if (saveResult.HasValue) json.WriteBoolean("saveResult", saveResult.Value); else json.WriteNull("saveResult");
                if (beforeSerial.HasValue) json.WriteNumber("beforeSetOptionsAttemptCount", beforeSerial.Value); else json.WriteNull("beforeSetOptionsAttemptCount");
                json.WriteString("saveResultRule", "before/after serial equal => not-called (null); increment => lastSetOptionsResult");
                json.WriteString("diagnostic", diagnostic); json.WriteString("comparisonStatus", status);
                Bytes(json, "settingsBytes", settings); Bytes(json, "bodyBytes", body);
                json.WriteStartObject("textPeer"); json.WriteString("literalSavedText", "saved peer\n"); json.WriteString("literalEditedText", "saved peer\nedited peer"); json.WriteString("sourcePath", peerPath); json.WriteNumber("comparisonToken", comparisonToken); json.WriteNumber("documentToken", documentToken);
                json.WriteStartArray("documentRevision"); if (revision is not null) foreach (var item in revision) json.WriteNumberValue(item); json.WriteEndArray();
                NullableBool("dirty", dirty); NullableBool("canUndo", canUndo); NullableBool("canRedo", canRedo);
                Bytes(json, "bodyBytes", peerBody); Bytes(json, "savepointBytes", savepoint); Bytes(json, "sourceBytes", peerSource); json.WriteEndObject();
                void NullableBool(string key, bool? value) { if (value.HasValue) json.WriteBoolean(key, value.Value); else json.WriteNull(key); }
            }));
        }
        void Options(Utf8JsonWriter json, string name, FolderApplicationOptions options)
        { json.WriteStartObject(name); json.WriteBoolean("treeMode", options.TreeMode); json.WriteNumber("initialExpansion", options.InitialExpansion); json.WriteEndObject(); }
        void Bytes(Utf8JsonWriter json, string name, byte[]? bytes)
        {
            if (bytes is null) { json.WriteNull(name + "Base64"); json.WriteNull(name + "Sha"); json.WriteNull(name + "Length"); return; }
            json.WriteBase64String(name + "Base64", bytes); json.WriteString(name + "Sha", Convert.ToHexString(SHA256.HashData(bytes))); json.WriteNumber(name + "Length", bytes.Length);
        }
        void RunStartup()
        {
            var caseRoot = Path.Combine(root, "startup"); Directory.CreateDirectory(caseRoot);
            var badImage = Path.Combine(caseRoot, "bad-image.json"); var badFolder = Path.Combine(caseRoot, "bad-folder.json");
            var literal = Encoding.UTF8.GetBytes("null"); File.WriteAllBytes(badImage, literal); File.WriteAllBytes(badFolder, literal);
            var imageStore = new ImageApplicationOptionsStore(badImage); var folderStore = new FolderApplicationOptionsStore(badFolder);
            var imageDiagnostic = imageStore.Diagnostic; var folderDiagnostic = folderStore.Diagnostic;
            var window = new MainWindow(null, imageStore, folderStore) { Width = 1280, Height = 850 }; var clock = Stopwatch.StartNew(); var tasks = new List<Task>(); Exception? caseFailure = null;
            try
            {
                window.Show(); OwnedJobs(clock);
                var dialogs = window.OwnedWindows.Where(item => item.IsVisible).ToArray();
                var dialog = dialogs.Single(item => item.Title == "表示・操作設定");
                var title = dialog.Title; var message = string.Join("\n", dialog.GetVisualDescendants().OfType<TextBlock>().Select(item => item.Text));
                var beforeImage = File.ReadAllBytes(badImage); var beforeFolder = File.ReadAllBytes(badFolder);
                var ok = dialog.GetVisualDescendants().OfType<Button>().Single(item => Equals(item.Content, "閉じる"));
                var point = ok.TranslatePoint(new Point(ok.Bounds.Width / 2, ok.Bounds.Height / 2), dialog) ?? throw new InvalidOperationException("Startup OK button did not lay out.");
                dialog.MouseDown(point, MouseButton.Left); dialog.MouseUp(point, MouseButton.Left); OwnedJobs(clock);
                var afterDialogs = window.OwnedWindows.Count(item => item.IsVisible); var afterImage = File.ReadAllBytes(badImage); var afterFolder = File.ReadAllBytes(badFolder); var visibleAfter = dialog.IsVisible;
                raw.Add(("startup/two-invalid-stores", json =>
                {
                    json.WriteString("scope", "actual headless ShowDialog and pointer button; native OS dialogs unverified");
                    json.WriteString("badImagePath", badImage); json.WriteString("badFolderPath", badFolder); json.WriteString("title", title); json.WriteString("messageBody", message);
                    json.WriteString("imageDiagnostic", imageDiagnostic); json.WriteString("folderDiagnostic", folderDiagnostic); json.WriteString("okContent", (string?)ok.Content);
                    json.WriteNumber("modalCount", dialogs.Length); json.WriteNumber("modalCountAfterOK", afterDialogs); json.WriteBoolean("dialogVisibleAfterOK", visibleAfter);
                    Bytes(json, "literalInput", literal); Bytes(json, "imageBefore", beforeImage); Bytes(json, "imageAfter", afterImage); Bytes(json, "folderBefore", beforeFolder); Bytes(json, "folderAfter", afterFolder);
                }));
                Report("startup one actual combined modal and OK close preserves both inputs", dialogs.Length == 1 && afterDialogs == 0 && !visibleAfter
                    && imageDiagnostic is not null && folderDiagnostic is not null && message.Contains(imageDiagnostic, StringComparison.Ordinal) && message.Contains(folderDiagnostic, StringComparison.Ordinal)
                    && literal.SequenceEqual(beforeImage) && literal.SequenceEqual(afterImage) && literal.SequenceEqual(beforeFolder) && literal.SequenceEqual(afterFolder));
            }
            catch (Exception error) { caseFailure = error; throw; }
            finally { EndOwnedCase(caseRoot, window, tasks, clock, caseFailure, 1); }
        }
        void RunProjectCompatibility()
        {
            var caseRoot = Path.Combine(root, "project-compatibility"); Directory.CreateDirectory(caseRoot);
            var left = Fixture(caseRoot, "left", 1); var right = Fixture(caseRoot, "right", 2);
            var store = new FolderApplicationOptionsStore(desktopDefaults: true);
            var window = new MainWindow(null, new ImageApplicationOptionsStore(), store) { Width = 1280, Height = 850 }; var clock = Stopwatch.StartNew(); var tasks = new List<Task>(); Exception? caseFailure = null;
            try
            {
                window.Show(); var pane = window.ActivePane; pane.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, Recursive = true }); CompareOwned(window, pane, clock, tasks);
                foreach (var version in new[] { 1, 2, 3, 4, 5 })
                {
                    var sourcePath = Path.Combine(caseRoot, "v" + version + "-source.json"); var roundtripPath = Path.Combine(caseRoot, "v" + version + "-roundtrip.json");
                    AwaitOwned(WorkspaceStore.SaveWorkspaceAsync(sourcePath, new ComparisonWorkspace { FormatVersion = version, Entries = [pane.CaptureProject()] }), clock, tasks, "workspace-source-save", "project-compatibility/v" + version);
                    var sourceBytes = File.ReadAllBytes(sourcePath); var loading = WorkspaceStore.LoadWorkspaceAsync(sourcePath); AwaitOwned(loading, clock, tasks, "workspace-source-load", "project-compatibility/v" + version); var loaded = loading.GetAwaiter().GetResult();
                    AwaitOwned(WorkspaceStore.SaveWorkspaceAsync(roundtripPath, loaded), clock, tasks, "workspace-roundtrip-save", "project-compatibility/v" + version); var roundtripBytes = File.ReadAllBytes(roundtripPath);
                    var reloading = WorkspaceStore.LoadWorkspaceAsync(roundtripPath); AwaitOwned(reloading, clock, tasks, "workspace-roundtrip-load", "project-compatibility/v" + version); var reloaded = reloading.GetAwaiter().GetResult();
                    var actualVersion = loaded.FormatVersion; var reloadedVersion = reloaded.FormatVersion; var capturedMode = loaded.Entries.Single().Mode; var recursive = loaded.Entries.Single().Recursive;
                    raw.Add(("project-compatibility/v" + version, json =>
                    {
                        json.WriteNumber("literalFormatVersion", version); json.WriteNumber("loadedFormatVersion", actualVersion); json.WriteNumber("reloadedFormatVersion", reloadedVersion);
                        json.WriteString("loadedMode", capturedMode); json.WriteBoolean("loadedRecursive", recursive); json.WriteString("sourcePath", sourcePath); json.WriteString("roundtripPath", roundtripPath);
                        Bytes(json, "source", sourceBytes); Bytes(json, "roundtrip", roundtripBytes);
                    }));
                    using var before = JsonDocument.Parse(sourceBytes); using var after = JsonDocument.Parse(roundtripBytes);
                    var forbidden = new[] { "treeMode", "initialExpansion", "folderTreeMode", "folderInitialExpansion" };
                    Report("project old DTO v" + version + " roundtrip retains Folder and no tree options", actualVersion == version && reloadedVersion == version && capturedMode == "Folder" && recursive
                        && !HasForbidden(before.RootElement) && !HasForbidden(after.RootElement));
                    bool HasForbidden(JsonElement item) => item.ValueKind == JsonValueKind.Object ? item.EnumerateObject().Any(field => forbidden.Contains(field.Name, StringComparer.Ordinal) || HasForbidden(field.Value))
                        : item.ValueKind == JsonValueKind.Array && item.EnumerateArray().Any(HasForbidden);
                }
            }
            catch (Exception error) { caseFailure = error; throw; }
            finally { EndOwnedCase(caseRoot, window, tasks, clock, caseFailure, 1); }
        }
        void RunAssetOnly()
        {
            var caseRoot = Path.Combine(root, "asset-only"); Directory.CreateDirectory(caseRoot);
            var left = Fixture(caseRoot, "left", 1); var right = Fixture(caseRoot, "right", 2);
            var settingsPath = Path.Combine(caseRoot, "folder-options.json");
            var store = new FolderApplicationOptionsStore(settingsPath);
            var window = new MainWindow(null, new ImageApplicationOptionsStore(), store) { Width = 1280, Height = 850 };
            var clock = Stopwatch.StartNew(); var tasks = new List<Task>(); Exception? caseFailure = null; var disposed = 0;
            window.ActivePane.IndependentTextInputParentDisposed += () => disposed++;
            try
            {
                window.Show(); var pane = window.ActivePane;
                pane.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, Recursive = true });
                CompareOwned(window, pane, clock, tasks);
                var workspaceBefore = window.WorkspaceSourcePath; var assetsBefore = window.ArchiveLifetime.Assets;
                var baselineWithoutAsset = assetsBefore.Length == 0 && workspaceBefore is null;
                var baselineSerial = store.SetOptionsAttemptCount; CaptureState("asset-only/baseline1/before", store, settingsPath, pane, null, null);
                OwnedHit(window, Mode(pane), clock); CaptureState("asset-only/baseline1/after", store, settingsPath, pane, null, null, baselineSerial); var firstSave = !store.Current.TreeMode && Mode(pane).IsChecked == false && File.Exists(settingsPath);
                baselineSerial = store.SetOptionsAttemptCount; CaptureState("asset-only/baseline2/before", store, settingsPath, pane, null, null);
                OwnedHit(window, Mode(pane), clock); CaptureState("asset-only/baseline2/after", store, settingsPath, pane, null, null, baselineSerial);
                var baselineSaveSucceeded = firstSave && store.Current.TreeMode && Mode(pane).IsChecked == true && store.Diagnostic is null;
                Report("asset-only baseline actual saves succeed without workspace or asset", baselineWithoutAsset && baselineSaveSucceeded);
                var beforeBytes = File.ReadAllBytes(settingsPath); var beforeSource = Rows(pane).ItemsSource;
                var beforeRows = Rows(pane).Items.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).ToArray();
                var beforeSelection = Rows(pane).SelectedItems!.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).ToArray();
                var beforeAdoption = pane.CaptureAdoptionState(); var beforeOptions = store.Current;
                Report("asset-only baseline literal rows", beforeRows.SequenceEqual(collapsed) && beforeSelection.Length == 0);
                File.WriteAllBytes(Path.Combine(caseRoot, "before-settings.json"), beforeBytes);
                window.ArchiveLifetime.RegisterAsset(settingsPath);
                var assetSerial = store.SetOptionsAttemptCount; CaptureState("asset-only/guard/before", store, settingsPath, pane, null, null);
                OwnedHit(window, Mode(pane), clock); CaptureState("asset-only/guard/after", store, settingsPath, pane, null, null, assetSerial);
                var afterBytes = File.ReadAllBytes(settingsPath);
                var afterRows = Rows(pane).Items.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).ToArray();
                var afterSelection = Rows(pane).SelectedItems!.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).ToArray();
                File.WriteAllBytes(Path.Combine(caseRoot, "after-settings.json"), afterBytes);
                Report("asset-only rejection retains display and settings", window.WorkspaceSourcePath is null
                    && window.ArchiveLifetime.Assets.SequenceEqual([Path.GetFullPath(settingsPath)])
                    && beforeBytes.SequenceEqual(afterBytes) && ReferenceEquals(beforeSource, Rows(pane).ItemsSource)
                    && beforeRows.SequenceEqual(afterRows) && beforeSelection.SequenceEqual(afterSelection)
                    && Equals(beforeAdoption, pane.CaptureAdoptionState()) && beforeOptions == store.Current
                    && Mode(pane).IsChecked == true && Policy(pane).SelectedIndex == 0
                    && store.Diagnostic?.Contains("作業snapshot", StringComparison.Ordinal) == true);
                using var evidence = File.Create(Path.Combine(caseRoot, "asset-only.json"));
                using var json = new Utf8JsonWriter(evidence, new() { Indented = true });
                json.WriteStartObject(); json.WriteString("settingsPath", settingsPath); json.WriteString("left", left); json.WriteString("right", right);
                if (workspaceBefore is { } beforeWorkspace) json.WriteString("workspaceSourcePathBefore", beforeWorkspace); else json.WriteNull("workspaceSourcePathBefore");
                if (window.WorkspaceSourcePath is { } workspace) json.WriteString("workspaceSourcePathAfter", workspace); else json.WriteNull("workspaceSourcePathAfter");
                StringValues(json, "assetsBefore", assetsBefore); json.WriteStartArray("assetsAfter"); foreach (var asset in window.ArchiveLifetime.Assets) json.WriteStringValue(asset); json.WriteEndArray();
                json.WriteBoolean("baselineWithoutAsset", baselineWithoutAsset); json.WriteBoolean("baselineSaveSucceeded", baselineSaveSucceeded);
                json.WriteString("beforeSettingsSha256", Convert.ToHexString(SHA256.HashData(beforeBytes))); json.WriteString("afterSettingsSha256", Convert.ToHexString(SHA256.HashData(afterBytes)));
                json.WriteBoolean("sameItemsSource", ReferenceEquals(beforeSource, Rows(pane).ItemsSource)); json.WriteBoolean("sameAdoption", Equals(beforeAdoption, pane.CaptureAdoptionState()));
                json.WriteBoolean("treeModeBefore", beforeOptions.TreeMode); json.WriteBoolean("treeModeAfter", store.Current.TreeMode);
                json.WriteNumber("policyBefore", beforeOptions.InitialExpansion); json.WriteNumber("policyAfter", store.Current.InitialExpansion);
                json.WriteString("diagnostic", store.Diagnostic); StringValues(json, "rowsBefore", beforeRows); StringValues(json, "rowsAfter", afterRows);
                StringValues(json, "selectedBefore", beforeSelection); StringValues(json, "selectedAfter", afterSelection); json.WriteEndObject();
                screenshot(window, "folder-options-asset-only.png");
                foreach (var file in Directory.EnumerateFiles(caseRoot, "*", SearchOption.AllDirectories))
                    if (Path.GetFileName(file) != "asset-only.json" && (Path.GetExtension(file) is ".bin" or ".json")) files[Path.GetRelativePath(root, file)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            }
            catch (Exception error) { caseFailure = error; throw; }
            finally
            {
                EndOwnedCase(caseRoot, window, tasks, clock, caseFailure, 1, () => disposed,
                    () => Report("asset-only window closes and tasks end", !window.IsVisible && window.OwnedWindows.Count() == 0 && disposed == 1 && tasks.Count == 1 && tasks.All(task => task.IsCompletedSuccessfully) && caseFailure is null && clock.Elapsed.TotalSeconds <= 30));
            }
        }

        void RunLayoutOnly()
        {
            var caseRoot = Path.Combine(root, "layout-only"); Directory.CreateDirectory(caseRoot);
            var left = Fixture(caseRoot, "left", 1); var right = Fixture(caseRoot, "right", 2);
            var store = new FolderApplicationOptionsStore(Path.Combine(caseRoot, "folder-options.json"));
            var window = new MainWindow(null, new ImageApplicationOptionsStore(), store) { Width = 1280, Height = 850 };
            var clock = Stopwatch.StartNew(); var tasks = new List<Task>(); Exception? caseFailure = null; var disposed = 0; var owned = new List<ComparisonPane>();
            Track(window.ActivePane);
            try
            {
                window.Show(); var pane = window.ActivePane;
                pane.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, Recursive = true });
                CompareOwned(window, pane, clock, tasks);
                LayoutCase("normal", 1);
                window.Width = 850; window.Height = 550; OwnedJobs(clock); LayoutCase("minimum", 1);
                while (window.SessionPanes.Count < 45) Track(window.AddSession());
                window.SelectSession(0); OwnedJobs(clock); LayoutCase("minimum-45-tabs", 45);

                void LayoutCase(string name, int expectedTabs)
                {
                    OwnedJobs(clock); var list = Rows(pane); var mode = Mode(pane); var policyControl = Policy(pane);
                    var deferredReports = new List<(string Name, bool Passed)>();
                    using var evidence = File.Create(Path.Combine(caseRoot, name + "-bounds.json"));
                    using var json = new Utf8JsonWriter(evidence, new() { Indented = true });
                    json.WriteStartObject(); json.WriteString("case", name); json.WriteNumber("width", window.Bounds.Width); json.WriteNumber("height", window.Bounds.Height);
                    json.WriteNumber("tabs", window.SessionPanes.Count); json.WriteNumber("expectedTabs", expectedTabs); json.WriteNumber("listMinHeight", list.MinHeight); json.WriteNumber("listHeight", list.Bounds.Height);
                    StringValues(json, "literalRows", list.Items.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).ToArray());
                    deferredReports.Add(("layout " + name + " fixed rows and tabs", list.Items.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).SequenceEqual(collapsed) && window.SessionPanes.Count == expectedTabs));
                    json.WriteStartArray("controls");
                    foreach (var control in new Control[] { mode, policyControl })
                    {
                        control.BringIntoView(); OwnedJobs(clock);
                        var bounds = new Rect(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
                        var viewport = control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                        var presenter = viewport?.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>().FirstOrDefault();
                        var viewportBounds = presenter is null ? default : new Rect(presenter.TranslatePoint(default, window)!.Value, presenter.Bounds.Size);
                        var visibleBounds = Intersect(Intersect(bounds, viewportBounds), new Rect(default(Point), window.Bounds.Size));
                        var reached = presenter is not null && control.IsEffectivelyVisible && bounds.Width > 0 && bounds.Height > 0
                            && visibleBounds.Width + .01 >= bounds.Width && visibleBounds.Height + .01 >= bounds.Height;
                        var isPolicy = ReferenceEquals(control, policyControl);
                        var popup = isPolicy ? policyControl.GetTemplateDescendants().OfType<Popup>().SingleOrDefault(item => item.Name == "PART_Popup") : null;
                        var popupOpenBeforeClick = popup?.IsOpen == true; var dropDownOpenBeforeClick = policyControl.IsDropDownOpen;
                        var settingsPath = Path.Combine(caseRoot, "folder-options.json");
                        var settingsBeforeClick = isPolicy && File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
                        var currentTreeBeforeClick = store.Current.TreeMode; var currentPolicyBeforeClick = store.Current.InitialExpansion;
                        OwnedHit(window, control, clock); var pointerFocus = control.IsKeyboardFocusWithin; var modeClick1 = mode.IsChecked;
                        var popupOpenAfterClick = popup?.IsOpen == true; var dropDownOpenAfterClick = policyControl.IsDropDownOpen;
                        var focusedElement = window.FocusManager?.GetFocusedElement();
                        var selectedContainer = isPolicy ? policyControl.ContainerFromIndex(policyControl.SelectedIndex) : null;
                        var focusedElementIsFocused = focusedElement is Control focusedControl && focusedControl.IsFocused;
                        var focusedInsidePopup = focusedElement is Visual focusedVisual && popup?.IsInsidePopup(focusedVisual) == true;
                        var focusedElementIsControl = ReferenceEquals(focusedElement, policyControl);
                        var focusedElementIsSelectedContainer = selectedContainer is not null && ReferenceEquals(focusedElement, selectedContainer);
                        var policyControlToken = Token(policyControl); var popupToken = Token(popup); var popupTemplatedParentToken = Token(popup?.TemplatedParent);
                        var focusedElementToken = Token(focusedElement); var selectedContainerToken = Token(selectedContainer);
                        if (ReferenceEquals(control, mode)) OwnedHit(window, control, clock);
                        else OwnedStroke(window, Key.Escape, PhysicalKey.Escape, clock);
                        var popupClosedAfterEscape = popup is not null && !popup.IsOpen && !policyControl.IsDropDownOpen;
                        var settingsAfterEscape = isPolicy && File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
                        var settingsRetainedAfterEscape = isPolicy && (settingsBeforeClick is null ? settingsAfterEscape is null : settingsAfterEscape is not null && settingsBeforeClick.AsSpan().SequenceEqual(settingsAfterEscape))
                            && currentTreeBeforeClick == store.Current.TreeMode && currentPolicyBeforeClick == store.Current.InitialExpansion;
                        var policyPointerObserved = isPolicy && popupToken > 0 && popupTemplatedParentToken == policyControlToken
                            && !popupOpenBeforeClick && !dropDownOpenBeforeClick && popupOpenAfterClick && dropDownOpenAfterClick && focusedElementIsFocused
                            && ((pointerFocus && focusedElementIsControl) || (focusedInsidePopup && focusedElementIsSelectedContainer && focusedElementToken > 0 && focusedElementToken == selectedContainerToken))
                            && popupClosedAfterEscape && settingsRetainedAfterEscape;
                        var stateRestored = Mode(pane).IsChecked == true && Policy(pane).SelectedIndex == 0 && !policyControl.IsDropDownOpen;
                        json.WriteStartObject(); json.WriteString("name", control.Name); WriteRectangle(json, "bounds", bounds); WriteRectangle(json, "scrollViewport", viewportBounds); WriteRectangle(json, "visibleIntersection", visibleBounds);
                        json.WriteNumber("pointerX", bounds.X + bounds.Width / 2); json.WriteNumber("pointerY", bounds.Y + bounds.Height / 2);
                        json.WriteBoolean("viewportMeasured", presenter is not null); json.WriteBoolean("effectivelyVisible", control.IsEffectivelyVisible); json.WriteBoolean("reachable", reached);
                        if (ReferenceEquals(control, mode)) { if (modeClick1.HasValue) json.WriteBoolean("modeClick1", modeClick1.Value); else json.WriteNull("modeClick1"); if (mode.IsChecked.HasValue) json.WriteBoolean("modeClick2", mode.IsChecked.Value); else json.WriteNull("modeClick2"); }
                        if (mode.IsChecked.HasValue) json.WriteBoolean("finalTreeMode", mode.IsChecked.Value); else json.WriteNull("finalTreeMode"); json.WriteNumber("finalPolicy", policyControl.SelectedIndex); json.WriteBoolean("dropDownClosed", !policyControl.IsDropDownOpen);
                        json.WriteBoolean("pointerFocus", pointerFocus); json.WriteBoolean("stateRestored", stateRestored);
                        if (isPolicy)
                        {
                            json.WriteNumber("policyControlToken", policyControlToken); json.WriteNumber("partPopupToken", popupToken); json.WriteNumber("popupTemplatedParentToken", popupTemplatedParentToken);
                            json.WriteNumber("focusedElementToken", focusedElementToken); json.WriteNumber("selectedContainerToken", selectedContainerToken);
                            json.WriteBoolean("popupOpenBeforeClick", popupOpenBeforeClick); json.WriteBoolean("popupOpenAfterClick", popupOpenAfterClick);
                            json.WriteBoolean("dropDownOpenBeforeClick", dropDownOpenBeforeClick); json.WriteBoolean("dropDownOpenAfterClick", dropDownOpenAfterClick);
                            json.WriteBoolean("focusedElementIsFocused", focusedElementIsFocused); json.WriteBoolean("focusedInsideOwnedPopup", focusedInsidePopup);
                            json.WriteBoolean("focusedElementIsControl", focusedElementIsControl); json.WriteBoolean("focusedElementIsSelectedContainer", focusedElementIsSelectedContainer);
                            json.WriteBoolean("popupClosedAfterEscape", popupClosedAfterEscape); json.WriteBoolean("settingsRetainedAfterEscape", settingsRetainedAfterEscape);
                            json.WriteBoolean("currentTreeBeforeClick", currentTreeBeforeClick); json.WriteNumber("currentPolicyBeforeClick", currentPolicyBeforeClick);
                            json.WriteBoolean("currentTreeAfterEscape", store.Current.TreeMode); json.WriteNumber("currentPolicyAfterEscape", store.Current.InitialExpansion);
                            json.WriteString("settingsBeforeClickBase64", settingsBeforeClick is null ? null : Convert.ToBase64String(settingsBeforeClick));
                            json.WriteString("settingsAfterEscapeBase64", settingsAfterEscape is null ? null : Convert.ToBase64String(settingsAfterEscape));
                        }
                        json.WriteEndObject();
                        deferredReports.Add(("layout " + name + " actual control " + control.Name, reached && (isPolicy ? policyPointerObserved : pointerFocus) && stateRestored));
                        screenshot(window, "folder-options-layout-" + name + "-" + control.Name + ".png");
                    }
                    json.WriteEndArray();
                    var first = list.Items.OfType<DirectoryEntry>().First(); list.ScrollIntoView(first); OwnedJobs(clock);
                    var row = list.ContainerFromItem(first) ?? throw new InvalidOperationException("Layout first row was not realized.");
                    row.BringIntoView(); OwnedJobs(clock); OwnedHit(window, row, clock);
                    var rowBounds = new Rect(row.TranslatePoint(default, window)!.Value, row.Bounds.Size);
                    var listBounds = new Rect(list.TranslatePoint(default, window)!.Value, list.Bounds.Size);
                    var listScroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                    var listPresenter = listScroll?.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>().FirstOrDefault();
                    var listViewport = listPresenter is null ? default : new Rect(listPresenter.TranslatePoint(default, window)!.Value, listPresenter.Bounds.Size);
                    var unclippedListPresenterBounds = listViewport;
                    listViewport = Intersect(Intersect(listViewport, listBounds), new Rect(default(Point), window.Bounds.Size));
                    var rowIntersection = Intersect(rowBounds, listViewport);
                    var rowReached = listPresenter is not null && row.IsEffectivelyVisible && rowBounds.Height > 0
                        && rowIntersection.Width > 0 && rowIntersection.Height + .01 >= rowBounds.Height;
                    var rowFocus = list.IsKeyboardFocusWithin && row.IsFocused && ReferenceEquals(list.SelectedItem, first);
                    WriteRectangle(json, "unclippedListPresenterBounds", unclippedListPresenterBounds);
                    json.WriteString("firstRowPath", first.RelativePath); WriteRectangle(json, "listBounds", listBounds); WriteRectangle(json, "listViewport", listViewport); WriteRectangle(json, "firstRowBounds", rowBounds); WriteRectangle(json, "firstRowIntersection", rowIntersection);
                    json.WriteBoolean("listViewportMeasured", listPresenter is not null); json.WriteBoolean("firstRowReachable", rowReached); json.WriteBoolean("firstRowPointerFocus", rowFocus);
                    json.WriteNumber("firstRowPointerX", rowBounds.X + rowBounds.Width / 2); json.WriteNumber("firstRowPointerY", rowBounds.Y + rowBounds.Height / 2); json.WriteEndObject();
                    deferredReports.Add(("layout " + name + " list and viewport 100 DIP", list.MinHeight >= 100 && list.Bounds.Height >= 100 && listViewport.Height >= 100));
                    deferredReports.Add(("layout " + name + " first row actual pointer focus", rowReached && rowFocus));
                    screenshot(window, "folder-options-layout-" + name + "-first-row.png");
                    // 失敗判定より先に実測JSON全体を閉じ、元のassert名と順序で失敗を返す。
                    json.Flush(); evidence.Flush();
                    foreach (var result in deferredReports) Report(result.Name, result.Passed);
                }
            }
            catch (Exception error) { caseFailure = error; throw; }
            finally
            {
                // 45個のタブを逐次closeせず、専用ownerのClosedで一括disposeする。
                EndOwnedCase(caseRoot, window, tasks, clock, caseFailure, 45, () => disposed, () =>
                {
                    var tasksEnded = tasks.Count == 1 && tasks.All(task => task.IsCompletedSuccessfully) && owned.All(pane => pane.PendingFolderCopy is null or { IsCompleted: true });
                    Report("layout owned window 45 panes dispose and tasks end", !window.IsVisible && window.OwnedWindows.Count() == 0 && owned.Count == 45 && disposed == owned.Count && tasksEnded && caseFailure is null && clock.Elapsed.TotalSeconds <= 30);
                });
            }
            void Track(ComparisonPane pane) { owned.Add(pane); pane.IndependentTextInputParentDisposed += () => disposed++; }
        }

        void CompareOwned(MainWindow window, ComparisonPane pane, Stopwatch clock, List<Task> tasks)
        {
            Task? pending = null; var previous = pane.TextSaveTaskObserved;
            pane.TextSaveTaskObserved = (route, task) => { if (route == "path-compare") { pending = task; RegisterOwned(task, tasks, "owned-compare", "", "path-compare"); } previous?.Invoke(route, task); };
            try
            {
                OwnedHit(window, pane.CompareButton, clock);
                if (pending is null) throw new InvalidOperationException("Owned actual comparison did not expose task.");
                // 操作ごとに30秒を再設定せず、専用helper開始からの共通30秒を守る。
                AwaitOwned(pending, clock, tasks);
            }
            finally { pane.TextSaveTaskObserved = previous; }
        }
        void OwnedHit(MainWindow window, Control control, Stopwatch clock)
        {
            control.BringIntoView(); OwnedJobs(clock); if (!control.IsEnabled) throw new InvalidOperationException("Disabled owned actual control: " + control.Name);
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); OwnedJobs(clock);
        }
        void OwnedStroke(MainWindow window, Key key, PhysicalKey physical, Stopwatch clock)
        { window.KeyPress(key, RawInputModifiers.None, physical, null); window.KeyRelease(key, RawInputModifiers.None, physical, null); OwnedJobs(clock); }
        void RegisterOwned(Task task, List<Task> ownedTasks, string label = "", string caseName = "", string hook = "")
        {
            if (!ownedTasks.Contains(task)) ownedTasks.Add(task);
            if (!taskEvidence.ContainsKey(task)) taskEvidence.Add(task, new(label.Length == 0 ? "owned-task-" + ownedTasks.Count : label, caseName, hook, "RanToCompletion", null));
        }
        bool ExpectedTerminalReached(Task task) => task.IsCompleted && (taskEvidence[task].ExpectedTerminal == "Canceled" ? task.IsCanceled : task.IsCompletedSuccessfully);
        void AwaitOwned(Task task, Stopwatch clock, List<Task> ownedTasks, string label = "", string caseName = "")
        {
            // timeout前に登録し、未終了・取消・faultも終了証拠へ残す。
            RegisterOwned(task, ownedTasks, label, caseName);
            CheckDeadline(clock);
            while (!task.IsCompleted) { OwnedJobs(clock); Thread.Sleep(2); }
            CheckDeadline(clock);
            task.GetAwaiter().GetResult(); OwnedJobs(clock);
        }
        void CheckDeadline(Stopwatch clock)
        { if (clock.Elapsed.TotalSeconds > 30) throw new TimeoutException("Folder options owned helper 30-second cap exceeded."); }
        void OwnedJobs(Stopwatch clock)
        { CheckDeadline(clock); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); CheckDeadline(clock); }
        void EndOwnedCase(string directory, MainWindow window, List<Task> tasks, Stopwatch clock, Exception? caseFailure, int expected, Func<int>? disposedCount = null, Action? finalReport = null)
        {
            // 集計・証拠出力自身が失敗しても、callerのcatch/throw中の元例外を置換しない。
            try { EndOwnedCore(); }
            catch (Exception secondary)
            {
                if (caseFailure is null) throw;
                try { TrySecondaryDiagnostics(directory, caseFailure, [("end-processing", secondary)]); } catch (Exception) { }
            }
            void EndOwnedCore()
            {
                var secondaryFailures = new List<(string Phase, Exception Error)>();
                var panes = Array.Empty<ComparisonPane>(); var pendingCopies = Array.Empty<(ComparisonPane Pane, Task? Pending)>(); var observedDisposed = 0; var disposed = -1; var expectedPanes = expected; Exception? cleanupFailure = null;
                Capture("pane-snapshot", () =>
                {
                    panes = window.SessionPanes.ToArray(); pendingCopies = panes.Select(pane => (Pane: pane, Pending: pane.PendingFolderCopy)).ToArray(); if (expectedPanes < 0) expectedPanes = panes.Length;
                    if (disposedCount is null) foreach (var pane in panes) pane.IndependentTextInputParentDisposed += () => observedDisposed++;
                });
                Capture("discard", () => { foreach (var pane in panes) pane.DiscardChanges(); });
                Capture("close-dialogs", () => { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); });
                Capture("close-window", window.Close);
                Capture("final-pump", () => { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); });
                Capture("disposed-count", () => disposed = disposedCount?.Invoke() ?? observedDisposed);
                Capture("owned-window-end", () => WriteOwnedEnd(directory, window, panes, pendingCopies, tasks, disposed, expectedPanes, clock, caseFailure, cleanupFailure));
                Capture("cleanup-report", () =>
                {
                    var completed = caseFailure is null && cleanupFailure is null
                        && clock.Elapsed.TotalSeconds <= 30 && !window.IsVisible && window.OwnedWindows.Count() == 0 && panes.Length == expectedPanes && disposed == expectedPanes
                        && tasks.All(ExpectedTerminalReached) && pendingCopies.Length == panes.Length && pendingCopies.All(item => item.Pending is null or { IsCompletedSuccessfully: true });
                    Report(Path.GetFileName(directory) + " owned cleanup completed", completed);
                    if (!completed && caseFailure is null && cleanupFailure is null) throw new InvalidOperationException("Owned cleanup did not reach its required terminal/disposal/deadline state.");
                });
                if (finalReport is not null) Capture("final-report", () =>
                {
                    var assertionStart = assertions.Count; finalReport();
                    if (caseFailure is null && assertions.Skip(assertionStart).Any(item => !item.Passed)) throw new InvalidOperationException("Owned final report assertion failed.");
                });
                Capture("aggregate-evidence", WriteEvidence);
                if (secondaryFailures.Count != 0) TrySecondaryDiagnostics(directory, caseFailure, secondaryFailures);
                // body成功時の証拠・Report失敗も、最初のsecondary例外を必ず送出する。
                if (caseFailure is null && cleanupFailure is not null) throw cleanupFailure;
                void Capture(string phase, Action action)
                {
                    try { action(); }
                    catch (Exception error) { cleanupFailure ??= error; secondaryFailures.Add((phase, error)); }
                }
            }
        }
        void TrySecondaryDiagnostics(string directory, Exception? caseFailure, List<(string Phase, Exception Error)> failures)
        {
            try
            {
                using var evidence = File.Create(Path.Combine(directory, "owned-window-secondary-failures.json"));
                using var json = new Utf8JsonWriter(evidence, new() { Indented = true });
                json.WriteStartObject(); json.WriteString("caseFailureType", caseFailure?.GetType().FullName); json.WriteString("caseFailureMessage", caseFailure?.Message);
                json.WriteStartArray("secondaryFailures"); foreach (var failure in failures)
                { json.WriteStartObject(); json.WriteString("phase", failure.Phase); json.WriteString("type", failure.Error.GetType().FullName); json.WriteString("message", failure.Error.Message); json.WriteEndObject(); }
                json.WriteEndArray(); json.WriteEndObject();
            }
            catch (Exception diagnosticFailure)
            {
                // 診断保存とstderrの失敗は元case例外・最初のsecondary例外へ影響させない。
                try { Console.Error.WriteLine("Folder options secondary diagnostics unavailable: " + diagnosticFailure); foreach (var failure in failures) Console.Error.WriteLine(failure.Phase + ": " + failure.Error); }
                catch (Exception) { }
            }
        }
        void WriteOwnedEnd(string directory, MainWindow window, ComparisonPane[] panes, (ComparisonPane Pane, Task? Pending)[] pendingCopies, List<Task> tasks, int disposed, int expected, Stopwatch clock, Exception? caseFailure, Exception? cleanupFailure)
        {
            using var evidence = File.Create(Path.Combine(directory, "owned-window-end.json")); using var json = new Utf8JsonWriter(evidence, new() { Indented = true });
            json.WriteStartObject(); json.WriteNumber("limitSeconds", 30); json.WriteNumber("elapsedMilliseconds", clock.Elapsed.TotalMilliseconds); json.WriteBoolean("withinTimeLimit", clock.Elapsed.TotalSeconds <= 30); json.WriteBoolean("isVisible", window.IsVisible); json.WriteNumber("ownedWindows", window.OwnedWindows.Count()); json.WriteNumber("expectedPanes", expected); json.WriteNumber("observedPanes", panes.Length); json.WriteNumber("disposedCallbacks", disposed);
            json.WriteNumber("observedTasks", tasks.Count); json.WriteBoolean("allTasksCompleted", tasks.All(task => task.IsCompleted)); json.WriteBoolean("allTasksCompletedSuccessfully", tasks.All(task => task.IsCompletedSuccessfully)); json.WriteBoolean("allTasksExpectedTerminalReached", tasks.All(ExpectedTerminalReached)); json.WriteBoolean("pendingFolderCopiesCompleted", pendingCopies.All(item => item.Pending is null or { IsCompleted: true }));
            json.WriteBoolean("pendingFolderCopiesCompletedSuccessfully", pendingCopies.All(item => item.Pending is null or { IsCompletedSuccessfully: true }));
            json.WriteStartArray("pendingFolderCopies"); foreach (var item in pendingCopies)
            {
                json.WriteStartObject(); json.WriteNumber("paneToken", Token(item.Pane));
                if (item.Pending is not Task pending) json.WriteNull("pendingTask");
                else
                {
                    json.WriteStartObject("pendingTask"); json.WriteNumber("taskToken", Token(pending)); json.WriteString("label", taskEvidence.TryGetValue(pending, out var owned) ? owned.Label : "pending-folder-copy");
                    json.WriteString("actualStatus", pending.Status.ToString()); json.WriteBoolean("completed", pending.IsCompleted); json.WriteBoolean("completedSuccessfully", pending.IsCompletedSuccessfully);
                    json.WriteBoolean("canceled", pending.IsCanceled); json.WriteBoolean("faulted", pending.IsFaulted); json.WriteString("failureType", pending.Exception?.GetBaseException().GetType().FullName); json.WriteString("failureMessage", pending.Exception?.GetBaseException().Message); json.WriteEndObject();
                }
                json.WriteEndObject();
            } json.WriteEndArray();
            json.WriteString("caseFailureType", caseFailure?.GetType().FullName); json.WriteString("caseFailureMessage", caseFailure?.Message);
            json.WriteString("cleanupFailureType", cleanupFailure?.GetType().FullName); json.WriteString("cleanupFailureMessage", cleanupFailure?.Message);
            json.WriteStartArray("tasks"); foreach (var task in tasks) { var item = taskEvidence[task]; json.WriteStartObject(); json.WriteNumber("taskToken", Token(task)); json.WriteString("label", item.Label); json.WriteString("case", item.Case.Length == 0 ? Path.GetFileName(directory) : item.Case); json.WriteString("hook", item.Hook); json.WriteString("expectedTerminal", item.ExpectedTerminal); json.WriteString("actualStatus", task.Status.ToString()); json.WriteString("status", task.Status.ToString()); json.WriteBoolean("completed", task.IsCompleted); json.WriteBoolean("completedSuccessfully", task.IsCompletedSuccessfully); json.WriteBoolean("canceled", task.IsCanceled); json.WriteBoolean("faulted", task.IsFaulted); json.WriteString("failureType", task.Exception?.GetBaseException().GetType().FullName); json.WriteString("failureMessage", task.Exception?.GetBaseException().Message); json.WriteStartObject("cancellationEvidence"); item.CancellationEvidence?.Invoke(json); json.WriteEndObject(); json.WriteEndObject(); } json.WriteEndArray(); json.WriteEndObject();
        }
        Rect Intersect(Rect first, Rect second)
        {
            var x = Math.Max(first.X, second.X); var y = Math.Max(first.Y, second.Y);
            return new Rect(x, y, Math.Max(0, Math.Min(first.Right, second.Right) - x), Math.Max(0, Math.Min(first.Bottom, second.Bottom) - y));
        }
        void WriteRectangle(Utf8JsonWriter json, string name, Rect rect)
        { json.WriteStartObject(name); json.WriteNumber("x", rect.X); json.WriteNumber("y", rect.Y); json.WriteNumber("width", rect.Width); json.WriteNumber("height", rect.Height); json.WriteEndObject(); }
        void StringValues(Utf8JsonWriter json, string name, string[] values)
        { json.WriteStartArray(name); foreach (var value in values) json.WriteStringValue(value); json.WriteEndArray(); }
        string Fixture(string parent, string side, byte changed)
        {
            var directory = Path.Combine(parent, side);
            Directory.CreateDirectory(Path.Combine(directory, "diff")); Directory.CreateDirectory(Path.Combine(directory, "equal"));
            File.WriteAllBytes(Path.Combine(directory, "diff", "a.bin"), Enumerable.Repeat(changed, changed).ToArray()); File.WriteAllBytes(Path.Combine(directory, "equal", "a.bin"), [9]);
            return directory;
        }
        void Report(string name, bool passed)
        { assertions.Add((name, passed)); check("folder options " + name, passed, "run-local options; actual controls; literal independent expectations"); }
        void WriteEvidence()
        {
            using var evidence = File.Create(Path.Combine(root, "assertions.json"));
            using var json = new Utf8JsonWriter(evidence, new() { Indented = true });
            json.WriteStartObject(); json.WriteNumber("schemaVersion", 3); json.WriteString("scope", "headless injected settings path; desktop pointer/permissions not claimed");
            json.WriteStartArray("raw"); foreach (var item in raw) { json.WriteStartObject(); json.WriteString("name", item.Name); item.Write(json); json.WriteEndObject(); } json.WriteEndArray();
            json.WriteStartArray("assertions"); foreach (var item in assertions) { json.WriteStartObject(); json.WriteString("name", item.Name); json.WriteBoolean("passed", item.Passed); json.WriteEndObject(); } json.WriteEndArray();
            json.WriteStartArray("views"); foreach (var item in views) { json.WriteStartObject(); json.WriteString("name", item.Name); Strings("expected", item.Expected); Strings("actual", item.Actual); json.WriteEndObject(); } json.WriteEndArray();
            json.WriteStartArray("layouts");
            foreach (var item in layouts)
            {
                json.WriteStartObject(); json.WriteString("name", item.Name); json.WriteNumber("width", item.Width); json.WriteNumber("height", item.Height); json.WriteNumber("listHeight", item.ListHeight);
                Bounds("mode", item.Mode); Bounds("policy", item.Policy); json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartObject("files"); foreach (var item in files) json.WriteString(item.Key, item.Value); json.WriteEndObject(); json.WriteEndObject();
            void Bounds(string name, Rect value)
            { json.WriteStartObject(name); json.WriteNumber("x", value.X); json.WriteNumber("y", value.Y); json.WriteNumber("width", value.Width); json.WriteNumber("height", value.Height); json.WriteEndObject(); }
            void Strings(string name, string[] values) { json.WriteStartArray(name); foreach (var value in values) json.WriteStringValue(value); json.WriteEndArray(); }
        }
    }
    private sealed record OwnedTaskEvidence(string Label, string Case, string Hook, string ExpectedTerminal, Action<Utf8JsonWriter>? CancellationEvidence);
    private static CheckBox Mode(ComparisonPane pane) => pane.FolderOptionsRawMode;
    private static ComboBox Policy(ComparisonPane pane) => pane.FolderOptionsRawPolicy;
    private static ListBox Rows(ComparisonPane pane) => pane.FolderOptionsRawRows;
    private static Button Button(ComparisonPane pane, string title) => pane.GetVisualDescendants().OfType<Button>().Single(control => Equals(control.Content, title));
}