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

// 期待値は製品で生成せず、固定原本と独立readerで全bytesを照合する。
internal static class HeadlessBinaryReplaceChecks
{
    private sealed record Evidence((string Name, object? Value)[] Fields);
    private sealed record SideState(int Side, string? BytesBase64, string? BytesFile, string Sha256, long Revision, bool Dirty,
        bool ReadOnly, bool Pending, int SelectionStart, int SelectionLength, int Caret, string Hex);
    private sealed record State(bool CanUndo, bool CanRedo, SideState[] Sides);
    private sealed record Saved(string Id, string Path, string? ReferenceCase, string? LiteralBase64, int Side, string Sha256);
    private sealed record CliProject(string Id, string ProjectFile, string Kind, int Side, string? ReferenceCase, string? LiteralBase64);

    internal static void Run(MainWindow window, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var root = Path.Combine(output, "binary-replace"); Directory.CreateDirectory(root);
        var fixture = Path.GetFullPath("tests/Fixtures/BinaryReplace");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture, "manifest.json")));
        var cases = new List<Evidence>(); var saved = new List<Saved>(); var projects = new List<CliProject>();
        var layouts = new List<Evidence>(); var archives = new List<Evidence>(); var tasks = new List<Task>();
        var panes = new List<ComparisonPane>(); var failures = new List<string>(); var sequence = 0;
        var clipboard = new UnusedClipboard();
        window.Width = 1280; window.Height = 850;
        var originalWindowSize = (window.Width, window.Height);
        using var reference = new StreamWriter(Path.Combine(root, "reference-product.ndjson"), false, new System.Text.UTF8Encoding(false));
        try
        {
            Reference();
            AllSides();
            DialogCases();
            StaleCases();
            Guards();
            SaveGuards();
            Limits();
            Archive();
            Layout();
        }
        catch (Exception error) { failures.Add(error.ToString()); throw; }
        finally
        {
            BinaryReplaceDialog.Shown = null;
            foreach (var pane in panes) { try { pane.DiscardChanges(); } catch (Exception error) { failures.Add(error.ToString()); } }
            window.Width = originalWindowSize.Item1; window.Height = originalWindowSize.Item2;
            reference.Flush();
            WriteFile(Path.Combine(root, "proof.json"), E(
                ("schemaVersion", 1),
                ("scope", "actual Avalonia headless buttons/keys; injected stale/cancel hooks; OS clipboard, desktop pointer and native save dialog unverified"),
                ("cases", cases), ("saved", saved), ("cliProjects", projects), ("archives", archives), ("layouts", layouts), ("failures", failures),
                ("clipboardReads", clipboard.Reads), ("clipboardWrites", clipboard.Writes),
                ("tasks", tasks.Select(task => E(("status", task.Status.ToString()), ("completed", task.IsCompleted), ("faulted", task.IsFaulted)))),
                ("ownedWindows", window.OwnedWindows.Count())));
            check("Binary replacement evidence", failures.Count == 0 && clipboard.Reads == 0 && clipboard.Writes == 0,
                "fixed Frhed 51 cases plus raw proof; independent verify.py and verify_gui.py are required");
        }

        void Reference()
        {
            ComparisonPane? pane = null;
            foreach (var entry in manifest.RootElement.GetProperty("cases").EnumerateArray())
            {
                var id = entry.GetProperty("caseId").GetString()!;
                var input = Path.Combine(fixture, entry.GetProperty("inputFile").GetString()!);
                if (pane is null) pane = Add(input, false); else Reload(pane, input, false);
                var panel = Binary(pane); var side = sequence++ % 2 == 0 ? 0 : 2;
                Select(panel, side, entry.GetProperty("caret").GetInt32(), entry.GetProperty("selectionStart").GetInt32(), entry.GetProperty("selectionLength").GetInt32());
                var pattern = Convert.FromBase64String(entry.GetProperty("patternBase64").GetString()!);
                var replacement = Convert.FromBase64String(entry.GetProperty("replacementBase64").GetString()!);
                var mode = entry.GetProperty("mode").GetString();
                var action = mode switch { "Selection" => BinaryReplaceAction.Selection, "Forward" => BinaryReplaceAction.Following, _ => BinaryReplaceAction.Previous };
                var before = Capture(panel); BinaryReplacementResult? observed = null; string status = "";
                panel.ReplacementSnapshotObserved = (_, _, result) => observed = result;
                var request = Request(pattern, replacement, entry.GetProperty("matchCase").GetBoolean());
                var route = "modal-pointer";
                try
                {
                    if (!ReplaceButton(panel, side).IsEnabled || action == BinaryReplaceAction.Selection && !panel.Selection(side).Selected)
                    {
                        route = "disabled-control-and-injected-refusal";
                        Report(id + "/disabled", !ReplaceButton(panel, side).IsEnabled || action == BinaryReplaceAction.Selection);
                        if (!ReplaceButton(panel, side).IsEnabled) status = Refuse(() => Wait(panel.ReplaceDialogAsync(side)));
                        else Modal(panel, side, dialog =>
                        {
                            Configure(dialog, request); Report(id + "/selection-disabled", !dialog.ReplaceSelection.IsEnabled);
                            status = Refuse(() => Wait(panel.ExecuteReplacementAsync(side, request, action, null, CancellationToken.None)));
                        });
                    }
                    else Modal(panel, side, dialog => { Configure(dialog, request); Hit(ActionButton(dialog, action)); Wait(dialog.CurrentOperation); status = dialog.Status.Text ?? ""; });
                }
                finally { panel.ReplacementSnapshotObserved = null; }
                var after = Capture(panel); var state = after.Sides.Single(value => value.Side == side);
                // 非選択時の旧API startは0。UIではcaretをStartとするため、生状態も別途保持する。
                var observedLength = observed?.SelectionLength ?? state.SelectionLength;
                var referenceStart = observedLength == 0 ? 0 : observed?.SelectionStart ?? state.SelectionStart;
                using (var row = new MemoryStream())
                {
                    using (var writer = new Utf8JsonWriter(row))
                    {
                        writer.WriteStartObject(); writer.WriteString("caseId", id); writer.WriteString("bytesBase64", state.BytesBase64);
                        writer.WriteNumber("replacementCount", observed?.ReplacementCount ?? 0);
                        writer.WriteNumber("selectionStart", referenceStart);
                        writer.WriteNumber("selectionLength", observedLength);
                        writer.WriteNumber("caret", observed?.Caret ?? state.Caret); writer.WriteEndObject();
                    }
                    reference.WriteLine(System.Text.Encoding.UTF8.GetString(row.ToArray())); reference.Flush();
                }
                var path = Path.Combine(root, "reference-" + id + ".bin"); Wait(panel.SaveToAsync(side, path));
                saved.Add(new(id, path, id, null, side, Hash(File.ReadAllBytes(path))));
                var clean = Capture(panel); var changed = before.Sides.Single(value => value.Side == side).BytesBase64 != state.BytesBase64;
                State? undo = null, redo = null;
                if (changed) { Click(HistoryButton(panel, "元に戻す")); undo = Capture(panel); Click(HistoryButton(panel, "やり直す")); redo = Capture(panel); }
                cases.Add(E(("id", "reference/" + id), ("kind", "reference"), ("referenceCase", id), ("side", side), ("route", route), ("before", before), ("after", after), ("clean", clean), ("undo", undo), ("redo", redo), ("status", status)));
                Report(id + "/bytes", File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(Path.Combine(fixture, entry.GetProperty("expectedFile").GetString()!))));
                Report(id + "/savepoint", !panel.Dirty(side));
                if (id == "forward-grow") Project(pane, "ordinary", side, id, null);
                pane.DiscardChanges();
            }
        }

        void AllSides()
        {
            foreach (var three in new[] { false, true })
            {
                var path = Blob("xABABABz"u8.ToArray()); var pane = Add(path, three);
                foreach (var side in Binary(pane).ProjectSides.ToArray())
                {
                    Reload(pane, path, three); var panel = Binary(pane); panel.SelectBytes(side, 2, 1);
                    var before = Capture(panel);
                    Modal(panel, side, dialog => { Configure(dialog, new("ignored", "XYZ", true, false, false)); Hit(dialog.ReplaceSelection); Wait(dialog.CurrentOperation); });
                    var after = Capture(panel); var outputPath = Path.Combine(root, (three ? "three" : "two") + "-" + side + ".bin");
                    Wait(panel.SaveToAsync(side, outputPath)); var clean = Capture(panel);
                    Click(HistoryButton(panel, "元に戻す")); var undo = Capture(panel);
                    Click(HistoryButton(panel, "やり直す")); var redo = Capture(panel);
                    var id = (three ? "three/" : "two/") + side;
                    saved.Add(new(id, outputPath, null, Convert.ToBase64String("xXYZABABz"u8), side, Hash(File.ReadAllBytes(outputPath))));
                    cases.Add(E(("id", id), ("kind", "all-sides"), ("side", side), ("before", before), ("after", after), ("clean", clean), ("undo", undo), ("redo", redo)));
                    Report(id, panel.CaptureApplied(side).CopyBytes().SequenceEqual("xXYZABABz"u8.ToArray()) && !panel.Dirty(side));
                }
                pane.DiscardChanges();
            }
        }

        void DialogCases()
        {
            var pane = Add(Blob("xABABABz"u8.ToArray()), false); var panel = Binary(pane); var before = Capture(panel);
            Modal(panel, 0, dialog =>
            {
                Configure(dialog, new("AB", "", true, false, false));
                Hit(dialog.FindNext); Wait(dialog.CurrentOperation); var first = Capture(panel);
                Hit(dialog.FindNext); Wait(dialog.CurrentOperation); var next = Capture(panel);
                Hit(dialog.FindPrevious); Wait(dialog.CurrentOperation); var previous = Capture(panel);
                Hit(dialog.FindPrevious); Wait(dialog.CurrentOperation); var noWrap = Capture(panel);
                cases.Add(E(("id", "navigation"), ("kind", "navigation"), ("side", 0), ("before", before), ("first", first), ("next", next), ("previous", previous), ("noWrap", noWrap)));
                Report("navigation selected range and no wrap", panel.Selection(0).Start == 1 && panel.Selection(0).Count == 2 && !panel.Session.CanUndo);
                Configure(dialog, new("AB", "<bh:" + new string('1', 50) + ">", true, false, false));
                var invalidBefore = Capture(panel);
                Hit(dialog.ReplaceFollowing); Wait(dialog.CurrentOperation); var invalid = Capture(panel); var invalidStatus = dialog.Status.Text;
                Configure(dialog, new("", "Z", true, false, false)); Jobs(); var emptyDisabled = !dialog.ReplaceFollowing.IsEnabled;
                Configure(dialog, new("AB", "<bh:ff>", true, true, false));
                Hit(dialog.ReplaceSelection); Wait(dialog.CurrentOperation); var literal = Capture(panel);
                cases.Add(E(("id", "invalid-correction-literal"), ("kind", "literal"), ("side", 0), ("invalidBefore", invalidBefore), ("invalid", invalid), ("invalidStatus", invalidStatus), ("emptyDisabled", emptyDisabled), ("after", literal)));
                Report("correctable input literal", panel.CaptureApplied(0).CopyBytes().SequenceEqual("x<bh:ff>ABABz"u8.ToArray()) && emptyDisabled);
            }, true);
            pane.DiscardChanges();

            foreach (var big in new[] { false, true })
            {
                Reload(pane, Blob(new byte[] { 0, 0x12, 0x34, 0x34, 0x12, 0xff }), false); panel = Binary(pane);
                Modal(panel, 2, dialog => { Configure(dialog, new("<wh:1234>", "Q", true, false, big)); Hit(dialog.ReplaceFollowing); Wait(dialog.CurrentOperation); });
                cases.Add(E(("id", big ? "endian/big" : "endian/little"), ("kind", "endian"), ("big", big), ("side", 2), ("after", Capture(panel))));
                pane.DiscardChanges();
            }
            Reload(pane, Blob(new byte[] { 0, 0x80, 0xff }), false); panel = Binary(pane); panel.Selection(0).Oem = true;
            Modal(panel, 0, dialog => { Configure(dialog, new("<bh:80>", "<bh:81>", true, false, false)); Hit(dialog.ReplaceFollowing); Wait(dialog.CurrentOperation); });
            cases.Add(E(("id", "oem/raw-bytecode"), ("kind", "oem"), ("side", 0), ("after", Capture(panel)))); pane.DiscardChanges();

            var large = Enumerable.Repeat((byte)'A', 40000).Concat(new byte[] { 0 }).Concat(Enumerable.Repeat((byte)'A', 40000)).ToArray();
            Reload(pane, Blob(large), false); panel = Binary(pane); panel.SelectBytes(0, 0, 39999);
            Modal(panel, 0, dialog =>
            {
                var truncatedDisplay = dialog.FindText.Text?.Length ?? 0; var useRaw = dialog.SavedPattern.IsChecked == true;
                Hit(dialog.FindNext); Wait(dialog.CurrentOperation);
                cases.Add(E(("id", "large-selection-raw"), ("kind", "large-pattern"), ("side", 0), ("truncatedDisplay", truncatedDisplay), ("useRaw", useRaw), ("after", Capture(panel))));
                Report("large pattern raw backing", useRaw && panel.Selection(0).Start == 40001 && panel.Selection(0).Count == 40000);
            });
            panel.SelectBytes(0, 0, 0, false);
            Modal(panel, 0, dialog => { var cached = dialog.SavedPattern.IsChecked == true; Hit(dialog.FindNext); Wait(dialog.CurrentOperation); cases.Add(E(("id", "large-selection-cache"), ("kind", "cache"), ("cached", cached), ("side", 0), ("after", Capture(panel)))); });
            pane.DiscardChanges();
        }

        void StaleCases()
        {
            var data = new byte[8194]; "xABABABz"u8.CopyTo(data); var path = Blob(data); var pane = Add(path, true);
            foreach (var route in new[] { "selection", "page", "readonly", "other-revision", "undo", "pending-hex", "comparison", "tab", "tab-roundtrip", "cancel", "observer-throw", "observer-reentry" })
            {
                Reload(pane, path, true); var panel = Binary(pane); panel.SelectBytes(0, 0, 0, false);
                if (route == "undo") panel.EditRange(2, new(BinaryRangeKind.Overwrite, 0, 0, "FF"));
                var before = Capture(panel); State? eventState = null; var hookReached = false; BinaryReplaceDialog? active = null;
                var tabGenerationBefore = window.SessionSelectionGeneration; long? tabGenerationAfterHook = null;
                panel.ReplacementReadyForAdoption = () =>
                {
                    if (hookReached) return; hookReached = true;
                    switch (route)
                    {
                        case "selection": panel.SelectBytes(0, 4, 4, false); break;
                        case "page": panel.SetOffset(4096); break;
                        case "readonly": panel.RightReadOnly = true; break;
                        case "other-revision": panel.EditRange(2, new(BinaryRangeKind.Overwrite, 0, 0, "FF")); break;
                        case "undo": panel.Undo(); break;
                        case "pending-hex": panel.Editor(2).Text = "AA"; break;
                        case "comparison": pane.LeftPath.Text = Blob("different"u8.ToArray()); break;
                        case "tab": window.AddSession(); break;
                        case "tab-roundtrip":
                            window.AddSession();
                            window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), pane));
                            break;
                        case "cancel": Click(active!.Cancel); break;
                        case "observer-reentry": panel.SelectBytes(0, 1, 1, false); break;
                    }
                    eventState = Capture(panel);
                    tabGenerationAfterHook = window.SessionSelectionGeneration;
                    if (route == "observer-throw") throw new InvalidOperationException("injected candidate observer failure");
                };
                try
                {
                    Modal(panel, 0, dialog =>
                    {
                        active = dialog; Configure(dialog, new("AB", "XYZ", true, false, false));
                        // nested hook内cancelは同期Click、通常操作はheadless pointer。
                        dialog.ReplaceFollowing.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); var busy = dialog.CurrentOperation;
                        dialog.ReplaceFollowing.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var duplicateSameTask = ReferenceEquals(busy, dialog.CurrentOperation);
                        Wait(busy); Jobs();
                        cases.Add(E(("id", "stale/" + route), ("kind", "stale"), ("side", 0), ("route", route), ("before", before), ("eventState", eventState), ("after", Capture(panel)), ("hookReached", hookReached), ("duplicateSameTask", duplicateSameTask), ("restored", !dialog.IsVisible || dialog.FindText.IsEnabled && dialog.Cancel.IsEnabled), ("observationFailure", panel.ReplacementObservedFailure?.GetType().Name), ("tabGenerationBefore", tabGenerationBefore), ("tabGenerationAfterHook", tabGenerationAfterHook)));
                    });
                }
                finally { panel.ReplacementReadyForAdoption = null; }
                Report("stale " + route, hookReached && panel.CaptureApplied(0).CopyBytes().SequenceEqual(data)); pane.DiscardChanges();
            }
        }

        void Guards()
        {
            var path = Blob("xABABABz"u8.ToArray()); var pane = Add(path, true);
            foreach (var side in new[] { 0, 1, 2 })
            {
                Reload(pane, path, true); var panel = Binary(pane); Readonly(panel, side, true); var before = Capture(panel);
                var disabled = !ReplaceButton(panel, side).IsEnabled;
                var error = Refuse(() => Wait(panel.ExecuteReplacementAsync(side, new("AB", "Z", true, false, false), BinaryReplaceAction.Following, null, CancellationToken.None)));
                cases.Add(E(("id", "readonly/" + side), ("kind", "guard"), ("side", side), ("disabled", disabled), ("error", error), ("before", before), ("after", Capture(panel))));
                Reload(pane, path, true); panel = Binary(pane); panel.Editor(side).Text = "AA"; Jobs(); before = Capture(panel);
                error = Refuse(() => Wait(panel.ExecuteReplacementAsync((side + 1) % 3, new("AB", "Z", true, false, false), BinaryReplaceAction.Following, null, CancellationToken.None)));
                cases.Add(E(("id", "pending/" + side), ("kind", "guard"), ("side", side), ("disabled", Binary(pane).ProjectSides.All(value => !ReplaceButton(panel, value).IsEnabled)), ("error", error), ("before", before), ("after", Capture(panel))));
                pane.DiscardChanges();
            }
            Reload(pane, Blob([]), true);
            foreach (var side in new[] { 0, 1, 2 })
            {
                var panel = Binary(pane); var before = Capture(panel);
                var error = Refuse(() => Wait(panel.ReplaceDialogAsync(side)));
                cases.Add(E(("id", "empty/" + side), ("kind", "guard"), ("side", side), ("disabled", !ReplaceButton(panel, side).IsEnabled), ("error", error), ("before", before), ("after", Capture(panel))));
            }
        }

        void Limits()
        {
            const string historyReason = "バイナリ履歴は共有64 MiB、256操作までです。履歴と本文は変更していません。";
            // 巨大候補はtextboxへ16 MiBを展開せず、dialogと同じ非同期採用APIを呼ぶ。
            var maxPath = Blob(new byte[BinaryEditSession.MaximumFileBytes]); var pane = Add(maxPath, false); var panel = Binary(pane);
            Inject("limit/16MiB", panel, new("<bh:00>", "<bh:00><bh:01>", true, false, false), BinaryReplaceAction.Following);
            pane.DiscardChanges(); Reload(pane, Blob(new byte[] { 0 }), false); panel = Binary(pane);
            for (var i = 0; i < BinaryEditSession.MaximumHistoryActions; i++) panel.EditRange(i % 2 == 0 ? 0 : 2, new(BinaryRangeKind.Insert, 0, 0, "01"));
            panel.SelectBytes(0, 0, 0); Inject("limit/256history", panel, new("", "Z", true, false, false), BinaryReplaceAction.Selection, historyReason);
            panel.Undo(); var before = Capture(panel); using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Wait(panel.ExecuteReplacementAsync(0, new("<bh:01>", "Z", true, false, false), BinaryReplaceAction.Following, null, canceled.Token));
            cases.Add(E(("id", "canceled-token-redo"), ("kind", "atomic"), ("before", before), ("after", Capture(panel)), ("error", "canceled"))); pane.DiscardChanges();

            Reload(pane, maxPath, false); panel = Binary(pane); var mib = 1024 * 1024;
            panel.EditRange(0, new(BinaryRangeKind.Overwrite, 0, 0, new string('1', 12 * mib * 2)));
            panel.EditRange(0, new(BinaryRangeKind.Overwrite, 0, 0, new string('2', 12 * mib * 2))); panel.MarkClean?.Invoke();
            panel.EditRange(0, new(BinaryRangeKind.Overwrite, 0, 0, new string('3', 8 * mib * 2))); panel.Undo();
            panel.SelectBytes(0, 0, BinaryEditSession.MaximumFileBytes - 1);
            Inject("limit/64MiB-redo", panel, new("", "Z", true, false, false), BinaryReplaceAction.Selection, historyReason);
            pane.DiscardChanges();

            // 後方の境界一致で置換後のaが次の元aと一致する。結果容量は16 MiBのまま。
            var workBytes = new byte[BinaryEditSession.MaximumFileBytes]; Array.Fill(workBytes, (byte)0x61);
            Reload(pane, Blob(workBytes), false); panel = Binary(pane);
            panel.EditRange(2, new(BinaryRangeKind.Overwrite, 0, 0, "62")); panel.Undo();
            panel.SelectBytes(0, workBytes.Length, workBytes.Length, false);
            Inject("limit/work", panel, new("<bh:61><bh:61>", "<bh:61><bh:62>", true, false, false), BinaryReplaceAction.Previous,
                "バイナリ置換の作業量上限を超えました。");
            pane.DiscardChanges();
        }

        void SaveGuards()
        {
            var original = "xABABABz"u8.ToArray(); var path = Blob(original); var pane = Add(path, false); var panel = Binary(pane);
            panel.SelectBytes(0, 2, 1);
            Modal(panel, 0, dialog => { Configure(dialog, new("", "XYZ", true, false, false)); Hit(dialog.ReplaceSelection); Wait(dialog.CurrentOperation); });
            var readonlyPath = Path.Combine(root, "readonly-output.bin"); File.WriteAllBytes(readonlyPath, "kept"u8.ToArray()); File.SetAttributes(readonlyPath, FileAttributes.ReadOnly);
            try
            {
                foreach (var target in new[] { path, readonlyPath })
                {
                    var bytes = File.ReadAllBytes(target); var beforeSave = Capture(panel);
                    var error = Refuse(() => Wait(panel.SaveToAsync(0, target)));
                    cases.Add(E(("id", target == path ? "save-guard/input" : "save-guard/readonly"), ("kind", "save-guard"), ("path", target), ("originalBase64", Convert.ToBase64String(bytes)), ("error", error), ("before", beforeSave), ("after", Capture(panel))));
                    Report("save output protection", error.Length != 0 && File.ReadAllBytes(target).SequenceEqual(bytes));
                }
            }
            finally { File.SetAttributes(readonlyPath, FileAttributes.Normal); }
            var beforeUndo = Capture(panel); Click(HistoryButton(panel, "元に戻す")); var before = Capture(panel);
            panel.SelectBytes(0, 2, 1);
            Modal(panel, 0, dialog => { Configure(dialog, new("", "AB", true, false, false)); Hit(dialog.ReplaceSelection); Wait(dialog.CurrentOperation); });
            cases.Add(E(("id", "same-selection-redo"), ("kind", "noop-redo"), ("beforeUndo", beforeUndo), ("before", before), ("after", Capture(panel))));
            Report("same bytes retains existing Redo", panel.Session.CanRedo && !panel.Dirty(0));
            pane.DiscardChanges();
        }

        void Inject(string id, SpecializedViews.BinaryPanel panel, BinaryReplaceRequest request, BinaryReplaceAction action, string? requiredReason = null)
        {
            // setupのRenderと、その描画が発生させた遅延通知を採用snapshotの前に処理する。
            Jobs(); Dispatcher.UIThread.RunJobs();
            var before = Capture(panel); var beforeStamp = panel.StateStamp; var tabGenerationBefore = window.SessionSelectionGeneration;
            Evidence? readyStamp = null; long? tabGenerationReady = null; BinaryReplaceFeedback? feedback = null;
            var previousReadyHook = panel.ReplacementReadyForAdoption;
            panel.ReplacementReadyForAdoption = () =>
            {
                var stamp = panel.StateStamp; readyStamp = E(("version", stamp.Version), ("selection", stamp.Selection));
                tabGenerationReady = window.SessionSelectionGeneration;
                previousReadyHook?.Invoke();
            };
            string error;
            try
            {
                error = Refuse(() =>
                {
                    var operation = panel.ExecuteReplacementAsync(0, request, action, null, CancellationToken.None);
                    Wait(operation); feedback = operation.GetAwaiter().GetResult();
                });
            }
            finally { panel.ReplacementReadyForAdoption = previousReadyHook; }
            var after = Capture(panel); var afterStamp = panel.StateStamp;
            cases.Add(E(("id", id), ("kind", "atomic"), ("route", "injected-same-adoption-api"), ("error", error), ("before", before), ("after", after),
                ("beforeStamp", E(("version", beforeStamp.Version), ("selection", beforeStamp.Selection))), ("readyStamp", readyStamp),
                ("afterStamp", E(("version", afterStamp.Version), ("selection", afterStamp.Selection))),
                ("tabGenerationBefore", tabGenerationBefore), ("tabGenerationReady", tabGenerationReady), ("tabGenerationAfter", window.SessionSelectionGeneration),
                ("feedbackAdopted", feedback?.Adopted), ("feedbackMessage", feedback?.Message), ("observationFailure", panel.ReplacementObservedFailure?.GetType().Name)));
            Report(id, error.Length != 0 && EqualBytes(before, after) && before.CanRedo == panel.Session.CanRedo
                && (requiredReason is null || error == "InvalidDataException: " + requiredReason));
        }

        void Archive()
        {
            var original = "xABABABz"u8.ToArray(); var paths = new string[3]; var hashes = new string[3];
            for (var side = 0; side < 3; side++) { paths[side] = Path.Combine(root, "archive-" + side + ".zip"); File.WriteAllBytes(paths[side], HeadlessBinaryWorkingChecks.Zip(("leaf.bin", original))); hashes[side] = Hash(File.ReadAllBytes(paths[side])); }
            ArchiveProjectInput Input(int side, bool readOnly) => new() { RootPath = paths[side], RootSha256 = hashes[side], EntryChain = [], LeafEntry = "leaf.bin", InheritedReadOnly = readOnly };
            var pane = window.AddSession(); panes.Add(pane);
            pane.ApplyProject(new() { Mode = "Binary", LeftArchiveInput = Input(0, false), BaseArchiveInput = Input(1, false), RightArchiveInput = Input(2, false), LeftReadOnly = true, BaseReadOnly = true, RightReadOnly = true }); Wait(pane.ComparePathsAsync());
            var panel = Binary(pane); panel.ClipboardOverride = clipboard;
            foreach (var side in new[] { 0, 1, 2 })
            {
                panel.SelectBytes(side, 2, 1); var before = Capture(panel);
                Modal(panel, side, dialog => { Configure(dialog, new("", "XYZ", true, false, false)); Hit(dialog.ReplaceSelection); Wait(dialog.CurrentOperation); });
                var after = Capture(panel);
                var rootError = Refuse(() => Wait(panel.SaveToAsync(side, paths[side])));
                cases.Add(E(("id", "archive-root-guard/" + side), ("kind", "save-guard"), ("path", paths[side]), ("originalBase64", Convert.ToBase64String(File.ReadAllBytes(paths[side]))), ("error", rootError), ("before", after), ("after", Capture(panel))));
                Wait(panel.SaveAsync(side)); var clean = Capture(panel);
                Click(HistoryButton(panel, "元に戻す")); var undo = Capture(panel); Click(HistoryButton(panel, "やり直す")); var redo = Capture(panel);
                cases.Add(E(("id", "archive/" + side), ("kind", "all-sides"), ("side", side), ("before", before), ("after", after), ("clean", clean), ("undo", undo), ("redo", redo)));
            }
            var workspace = Project(pane, "archive", 1, null, Convert.ToBase64String("xXYZABABz"u8));
            var loaded = WorkspaceStore.LoadAsync(workspace); Wait(loaded); var restored = window.AddSession(); panes.Add(restored); restored.ApplyProject(loaded.Result); Wait(restored.ComparePathsAsync());
            cases.Add(E(("id", "archive/reloaded"), ("kind", "archive-reload"), ("after", Capture(Binary(restored)))));
            archives.Add(E(("id", "working"), ("paths", paths), ("hashes", hashes), ("leaf", "leaf.bin"), ("originalBase64", Convert.ToBase64String(original)), ("workspace", workspace)));
            pane.DiscardChanges(); restored.DiscardChanges();
            var readonlyPane = window.AddSession(); panes.Add(readonlyPane);
            readonlyPane.ApplyProject(new() { Mode = "Binary", LeftArchiveInput = Input(0, true), BaseArchiveInput = Input(1, true), RightArchiveInput = Input(2, true), LeftReadOnly = true, BaseReadOnly = true, RightReadOnly = true }); Wait(readonlyPane.ComparePathsAsync()); panel = Binary(readonlyPane);
            foreach (var side in new[] { 0, 1, 2 })
            {
                var before = Capture(panel); var error = Refuse(() => Wait(panel.ExecuteReplacementAsync(side, new("AB", "Z", true, false, false), BinaryReplaceAction.Following, null, CancellationToken.None)));
                cases.Add(E(("id", "inherited-readonly/" + side), ("kind", "guard"), ("side", side), ("disabled", !ReplaceButton(panel, side).IsEnabled), ("error", error), ("before", before), ("after", Capture(panel))));
            }
        }

        string Project(ComparisonPane pane, string id, int side, string? referenceCase, string? literal)
        {
            var project = pane.CaptureProject(); var workspace = Path.Combine(root, id + "-workspace.json");
            Wait(WorkspaceStore.SaveWorkspaceAsync(workspace, new() { Entries = [project] }, publishedAsset: window.ArchiveLifetime.RegisterAsset));
            projects.Add(new(id, Path.GetRelativePath(root, workspace), id, side, referenceCase, literal));
            var package = Path.Combine(root, id + "-package.zip");
            Wait(ComparisonPackage.CreateAsync(new() { Entries = [project] }, package, new(true, false, false, true)));
            cases.Add(E(("id", "package/" + id), ("kind", "package"), ("workspace", workspace), ("package", package), ("side", side), ("referenceCase", referenceCase), ("literalBase64", literal)));
            return workspace;
        }

        void Layout()
        {
            var path = Blob("xABABABz"u8.ToArray()); var pane = Add(path, true);
            foreach (var name in new[] { "normal", "minimum", "many-tabs" })
            {
                if (name == "many-tabs") for (var i = 0; i < 8; i++) panes.Add(window.AddSession());
                window.Width = name == "normal" ? 1280 : 850; window.Height = name == "normal" ? 850 : 550;
                window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), pane)); Jobs(); var panel = Binary(pane);
                foreach (var side in panel.ProjectSides)
                {
                    foreach (var control in new Control[] { panel.Editor(side), ReplaceButton(panel, side), SaveButton(panel, side) })
                    {
                        control.BringIntoView(); Jobs(); var bounds = Bounds(control, window);
                        layouts.Add(E(("id", name + "/" + side + "/" + (control.Name ?? (control as ContentControl)?.Content?.ToString() ?? "hex")), ("windowWidth", window.Bounds.Width), ("windowHeight", window.Bounds.Height), ("bounds", bounds)));
                        Report("layout " + name, bounds.Visible);
                    }
                }
                screenshot("binary-replace-" + name + ".png");
                Modal(panel, 0, dialog =>
                {
                    foreach (var control in new Control[] { dialog.FindText, dialog.ReplacementText, dialog.ReplaceFollowing, dialog.Cancel })
                    {
                        control.BringIntoView(); Jobs(); var bounds = Bounds(control, dialog);
                        layouts.Add(E(("id", name + "/modal/" + (control == dialog.FindText ? "find" : control == dialog.ReplacementText ? "replacement" : (control as ContentControl)?.Content?.ToString())), ("windowWidth", dialog.Bounds.Width), ("windowHeight", dialog.Bounds.Height), ("bounds", bounds)));
                        Report("modal layout " + name, bounds.Visible);
                    }
                    using var frame = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("Replacement dialog frame missing");
                    frame.Save(Path.Combine(root, "dialog-" + name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                });
            }
        }

        void Modal(SpecializedViews.BinaryPanel panel, int side, Action<BinaryReplaceDialog> body, bool shortcut = false)
        {
            var opened = false;
            BinaryReplaceDialog.Shown = dialog =>
            {
                opened = true; Jobs();
                try { body(dialog); }
                finally { if (dialog.IsVisible) Hit(dialog.Cancel); }
            };
            try
            {
                if (shortcut)
                {
                    panel.Editor(side).Focus(); window.KeyPress(Key.H, RawInputModifiers.Control, PhysicalKey.H, null); window.KeyRelease(Key.H, RawInputModifiers.Control, PhysicalKey.H, null); Jobs();
                }
                else Hit(ReplaceButton(panel, side));
                if (panel.CurrentReplacementDialogOperation is { } operation) Wait(operation);
                Report("replacement real dialog opened", opened);
            }
            finally { BinaryReplaceDialog.Shown = null; }
        }
        ComparisonPane Add(string path, bool three) { var pane = window.AddSession(); panes.Add(pane); Reload(pane, path, three); return pane; }
        void Reload(ComparisonPane pane, string path, bool three)
        {
            pane.DiscardChanges(); pane.ApplyProject(new() { Mode = "Binary", LeftPath = path, BasePath = three ? path : "", RightPath = path });
            window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), pane)); Wait(pane.ComparePathsAsync()); Binary(pane).ClipboardOverride = clipboard;
        }
        string Blob(byte[] bytes) { var path = Path.Combine(root, "input-" + sequence++ + ".bin"); File.WriteAllBytes(path, bytes); return path; }
        void Wait(Task task)
        {
            if (!tasks.Contains(task)) tasks.Add(task); var timer = Stopwatch.StartNew();
            while (!task.IsCompleted) { Jobs(); if (timer.Elapsed.TotalSeconds > 60) throw new TimeoutException("Binary replacement task deadline"); Thread.Sleep(1); }
            task.GetAwaiter().GetResult(); Jobs();
        }
        void Report(string id, bool pass) { if (!pass) failures.Add(id); check("Binary replacement " + id, pass, "actual controls and full raw states; independent reader required"); }
        State Capture(SpecializedViews.BinaryPanel panel) => CaptureState(panel, root);
        void Jobs() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        void Hit(Control control)
        {
            control.BringIntoView(); Jobs(); if (!control.IsEnabled) throw new InvalidOperationException("Disabled replacement test control");
            var owner = TopLevel.GetTopLevel(control) as Window ?? throw new InvalidOperationException("Missing replacement test owner");
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), owner) ?? throw new InvalidOperationException("Missing replacement test bounds");
            if (point.X < 0 || point.Y < 0 || point.X >= owner.Bounds.Width || point.Y >= owner.Bounds.Height) throw new InvalidOperationException("Replacement test control outside viewport");
            owner.MouseDown(point, MouseButton.Left); owner.MouseUp(point, MouseButton.Left); Jobs();
        }
        void Click(Button control) { control.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Jobs(); }
    }

    private static Evidence E(params (string Name, object? Value)[] fields) => new(fields);
    private static void WriteFile(string path, Evidence evidence)
    {
        using var file = File.Create(path);
        using var writer = new Utf8JsonWriter(file, new() { Indented = true });
        WriteValue(writer, evidence);
    }
    // 型を明示して書く。未対応型を反射で列挙したりAOT警告を抑制したりしない。
    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case int intNumber: writer.WriteNumberValue(intNumber); break;
            case long longNumber: writer.WriteNumberValue(longNumber); break;
            case double doubleNumber: writer.WriteNumberValue(doubleNumber); break;
            case Evidence evidence:
                writer.WriteStartObject();
                foreach (var field in evidence.Fields) { writer.WritePropertyName(field.Name); WriteValue(writer, field.Value); }
                writer.WriteEndObject(); break;
            case State state:
                WriteValue(writer, E(("canUndo", state.CanUndo), ("canRedo", state.CanRedo), ("sides", state.Sides))); break;
            case SideState side:
                WriteValue(writer, E(("side", side.Side), ("bytesBase64", side.BytesBase64), ("bytesFile", side.BytesFile),
                    ("sha256", side.Sha256), ("revision", side.Revision), ("dirty", side.Dirty), ("readOnly", side.ReadOnly),
                    ("pending", side.Pending), ("selectionStart", side.SelectionStart), ("selectionLength", side.SelectionLength),
                    ("caret", side.Caret), ("hex", side.Hex))); break;
            case Saved saved:
                WriteValue(writer, E(("id", saved.Id), ("path", saved.Path), ("referenceCase", saved.ReferenceCase),
                    ("literalBase64", saved.LiteralBase64), ("side", saved.Side), ("sha256", saved.Sha256))); break;
            case CliProject project:
                WriteValue(writer, E(("id", project.Id), ("projectFile", project.ProjectFile), ("kind", project.Kind),
                    ("side", project.Side), ("referenceCase", project.ReferenceCase), ("literalBase64", project.LiteralBase64))); break;
            case ControlBounds bounds:
                WriteValue(writer, E(("x", bounds.X), ("y", bounds.Y), ("width", bounds.Width), ("height", bounds.Height),
                    ("visible", bounds.Visible))); break;
            case IEnumerable<Evidence> evidenceRows:
                writer.WriteStartArray(); foreach (var item in evidenceRows) WriteValue(writer, item); writer.WriteEndArray(); break;
            case IEnumerable<SideState> sides:
                writer.WriteStartArray(); foreach (var item in sides) WriteValue(writer, item); writer.WriteEndArray(); break;
            case IEnumerable<Saved> files:
                writer.WriteStartArray(); foreach (var item in files) WriteValue(writer, item); writer.WriteEndArray(); break;
            case IEnumerable<CliProject> projects:
                writer.WriteStartArray(); foreach (var item in projects) WriteValue(writer, item); writer.WriteEndArray(); break;
            case IEnumerable<string> strings:
                writer.WriteStartArray(); foreach (var item in strings) writer.WriteStringValue(item); writer.WriteEndArray(); break;
            default: throw new InvalidDataException("未対応の証拠型です。JSON writerへ明示的な型の書込みを追加してください。");
        }
    }

    private static BinaryReplaceRequest Request(byte[] pattern, byte[] replacement, bool match = true) => new(BinaryBytecode.Encode(pattern), BinaryBytecode.Encode(replacement), match, false, false);
    private static void Configure(BinaryReplaceDialog dialog, BinaryReplaceRequest request)
    {
        dialog.SavedPattern.IsChecked = request.UseSavedPattern; dialog.FindText.Text = request.FindText;
        dialog.ReplacementText.Text = request.ReplacementText; dialog.MatchCase.IsChecked = request.MatchCase;
        dialog.AsText.IsChecked = request.AsText; dialog.BigEndian.IsChecked = request.BigEndian; dialog.RefreshState();
    }
    private static void Select(SpecializedViews.BinaryPanel panel, int side, int caret, int start, int length)
    { if (length == 0) panel.SelectBytes(side, caret, caret, false); else panel.SelectBytes(side, start + length - 1, start); }
    private static State CaptureState(SpecializedViews.BinaryPanel panel, string root) => new(panel.Session.CanUndo, panel.Session.CanRedo, panel.ProjectSides.Select(side =>
    {
        var bytes = panel.CaptureApplied(side).CopyBytes(); var selection = panel.Selection(side);
        var hash = Hash(bytes); string? file = null;
        if (bytes.Length > 1024 * 1024) { file = Path.Combine(root, "state-" + hash + ".bin"); if (!File.Exists(file)) File.WriteAllBytes(file, bytes); }
        return new SideState(side, file is null ? Convert.ToBase64String(bytes) : null, file, hash, panel.Session.Revision(panel.LocalSide(side)), panel.Dirty(side), panel.ReadOnly(side), panel.Pending(side), selection.Start, selection.Count, selection.Caret, panel.Editor(side).Text ?? "");
    }).ToArray());
    private static bool EqualBytes(State before, State after) => before.Sides.Zip(after.Sides).All(pair => pair.First.Sha256 == pair.Second.Sha256);
    private static string Refuse(Action action)
    {
        try { action(); return ""; }
        catch (Exception error) when (error is FormatException or InvalidDataException or InvalidOperationException or ArgumentException or OperationCanceledException or IOException or UnauthorizedAccessException) { return error.GetType().Name + ": " + error.Message; }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static SpecializedViews.BinaryPanel Binary(ComparisonPane pane) => pane.GetVisualDescendants().OfType<SpecializedViews.BinaryPanel>().Single();
    private static Button ReplaceButton(SpecializedViews.BinaryPanel panel, int side) => panel.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "binary-replace-" + side);
    private static Button SaveButton(SpecializedViews.BinaryPanel panel, int side) => HistoryButton(panel, (side switch { 0 => "左", 1 => "中央", _ => "右" }) + "を保存");
    private static Button HistoryButton(SpecializedViews.BinaryPanel panel, string content) => panel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, content));
    private static Button ActionButton(BinaryReplaceDialog dialog, BinaryReplaceAction action) => action switch { BinaryReplaceAction.Selection => dialog.ReplaceSelection, BinaryReplaceAction.Following => dialog.ReplaceFollowing, BinaryReplaceAction.Previous => dialog.ReplacePrevious, BinaryReplaceAction.FindNext => dialog.FindNext, _ => dialog.FindPrevious };
    private static void Readonly(SpecializedViews.BinaryPanel panel, int side, bool value) { if (side == 0) panel.LeftReadOnly = value; else if (side == 1) panel.MiddleReadOnly = value; else panel.RightReadOnly = value; }
    private sealed record ControlBounds(double X, double Y, double Width, double Height, bool Visible);
    private static ControlBounds Bounds(Control control, Window window)
    {
        var point = control.TranslatePoint(default, window) ?? new Point(-1, -1);
        return new(point.X, point.Y, control.Bounds.Width, control.Bounds.Height, point.X >= 0 && point.Y >= 0 && point.X + control.Bounds.Width / 2 < window.Bounds.Width && point.Y + control.Bounds.Height / 2 < window.Bounds.Height && control.Bounds.Width > 0 && control.Bounds.Height > 0);
    }
    private sealed class UnusedClipboard : IBinaryClipboard
    {
        internal int Reads, Writes;
        public Task WriteAsync(byte[] bytes, string text, CancellationToken token) { Writes++; throw new InvalidOperationException("Replacement accessed OS clipboard"); }
        public Task<IReadOnlyList<BinaryClipboardValue>> ReadAsync(CancellationToken token) { Reads++; throw new InvalidOperationException("Replacement accessed OS clipboard"); }
    }
}
