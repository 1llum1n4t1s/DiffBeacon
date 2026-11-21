using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

// 候補専用。実行時出力はself-testが渡す外部runだけ。利用者clipboardを使わない。
internal static class HeadlessBinarySearchChecks
{
    internal static void Run(MainWindow window, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        var root = Path.Combine(output, "binary-search"); Directory.CreateDirectory(root);
        var blobs = Path.Combine(root, "bytes"); Directory.CreateDirectory(blobs);
        var rows = new List<byte[]>(); var uiEvents = new List<byte[]>(); SpecializedViews.BinaryPanel? eventPanel = null; var eventSide = 0; string? archiveRoot = null; var identities = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        var tasks = new List<Task>(); var panes = new List<ComparisonPane>(); var clocks = new Stopwatch();
        var clipboard = new UnusedClipboard(); var disposed = 0; Exception? failure = null;
        var fixedBytes = new byte[8194]; Array.Fill(fixedBytes, (byte)0xcc);
        "ABABABaA"u8.CopyTo(fixedBytes); fixedBytes[8] = 0; fixedBytes[9] = 0x12; fixedBytes[10] = 0x34;
        fixedBytes[11] = 0x34; fixedBytes[12] = 0x12; fixedBytes[13] = 0xff;
        new byte[] { 0xde, 0xad, 0xbe, 0xef }.CopyTo(fixedBytes, 4094); new byte[] { 0xde, 0xad, 0xbe, 0xef }.CopyTo(fixedBytes, 8190);
        window.Width = 1280; window.Height = 850;
        var source = Blob(fixedBytes); var empty = Blob([]);
        foreach (var initial in window.SessionPanes) { panes.Add(initial); initial.IndependentTextInputParentDisposed += () => disposed++; }
        try
        {
            CoreCases();
            foreach (var three in new[] { false, true })
            {
                var pane = Add(source, three); var panel = Binary(pane);
                foreach (var side in panel.ProjectSides)
                {
                    var prefix = (three ? "three" : "two") + "/" + side; window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), pane)); Jobs();
                    panel.SelectBytes(side, 0, 0, false); panel.Editor(side).Focus();
                    Step(prefix + "/no-word", "no-word", panel, side, () => Stroke(Key.F3, PhysicalKey.F3));
                    Step(prefix + "/selection-add", "selection", panel, side, () => { panel.Editor(side).SelectionStart = 0; panel.Editor(side).SelectionEnd = 5; Jobs(); });
                    Step(prefix + "/selection-clear", "selection", panel, side, () => { panel.Editor(side).SelectionEnd = 0; Jobs(); });
                    Dialog(prefix + "/find", panel, side, "AB", false, false, false, false, "button");
                    Step(prefix + "/next", "search", panel, side, () => Hit(SearchButton(panel, side, "次を検索")));
                    Step(prefix + "/previous", "search", panel, side, () => Stroke(Key.F3, PhysicalKey.F3, RawInputModifiers.Shift));
                    panel.SelectBytes(side, 0, 0, false);
                    Dialog(prefix + "/no-match", panel, side, "ZZ", false, false, false, false, "CtrlF");
                    Dialog(prefix + "/utf16", panel, side, "A", true, true, false, false, "button");
                    panel.SelectBytes(side, 0, 0, false);
                    Dialog(prefix + "/ascii-case", panel, side, "a", false, false, false, false, "button");
                    panel.SelectBytes(side, 0, 0, false);
                    Dialog(prefix + "/big-endian", panel, side, "<wh:1234>", true, false, true, false, "button");
                    panel.Selection(side).Oem = true; panel.SelectBytes(side, 0, 0, false);
                    Dialog(prefix + "/oem-token", panel, side, "A<bh:ff>", true, false, false, false, "CtrlF");
                    panel.Selection(side).Oem = false;
                    Invalid(prefix + "/invalid-correct", panel, side);
                    SetReadOnly(panel, side, true); panel.SelectBytes(side, 0, 0, false);
                    Dialog(prefix + "/readonly", panel, side, "AB", false, false, false, false, "button");
                    SetReadOnly(panel, side, false);
                    panel.SelectBytes(side, 0, 0, false); panel.AsciiEditor(side).Focus(); window.KeyTextInput("Z"); Jobs();
                    Dialog(prefix + "/dirty-search", panel, side, "AB", false, false, false, false, "button");
                    Step(prefix + "/undo", "history", panel, side, () => Hit(FindButton(panel, "元に戻す")));
                    Step(prefix + "/redo", "history", panel, side, () => Hit(FindButton(panel, "やり直す")));
                    var save = Path.Combine(root, prefix.Replace('/', '-') + "-saved.bin");
                    Step(prefix + "/save", "save", panel, side, () => Wait(panel.SaveToAsync(side, save)), save);
                    pane.DiscardChanges();
                    panel.SelectBytes(side, 0, 0, false);
                    Dialog(prefix + "/page", panel, side, "<bh:de><bh:ad><bh:be><bh:ef>", true, false, false, false, "button");
                    foreach (var route in new[] { "cancel", "selection", "page", "readonly", "owner-input", "observer-throw", "observer-reentry" })
                        Stale(prefix + "/stale-" + route, pane, panel, side, route);
                    screenshot("binary-search-" + prefix.Replace('/', '-') + ".png");
                }
            }
            archiveRoot = Path.Combine(root, "source.zip"); File.WriteAllBytes(archiveRoot, HeadlessBinaryWorkingChecks.Zip(("leaf.bin", fixedBytes)));
            var archived = window.AddSession(); panes.Add(archived); archived.IndependentTextInputParentDisposed += () => disposed++;
            ArchiveProjectInput Input() => new() { RootPath = archiveRoot, RootSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archiveRoot))), EntryChain = [], LeafEntry = "leaf.bin", InheritedReadOnly = true };
            archived.ApplyProject(new() { Mode = "Binary", LeftArchiveInput = Input(), BaseArchiveInput = Input(), RightArchiveInput = Input(), LeftReadOnly = true, BaseReadOnly = true, RightReadOnly = true }); Wait(archived.ComparePathsAsync()); var archivePanel = Binary(archived); archivePanel.ClipboardOverride = clipboard;
            foreach (var side in archivePanel.ProjectSides) Dialog("archive/" + side + "/readonly", archivePanel, side, "AB", false, false, false, false, "button");
            var emptyPane = Add(empty, true); var emptyPanel = Binary(emptyPane);
            foreach (var side in emptyPanel.ProjectSides)
            { emptyPanel.Editor(side).Focus(); Step("empty/" + side, "empty", emptyPanel, side, () => Stroke(Key.F3, PhysicalKey.F3)); }
            Large();
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            BinarySearchDialog.Shown = null;
            foreach (var pane in panes) { try { pane.DiscardChanges(); } catch (Exception cleanup) { failure ??= cleanup; } }
            try { window.Close(); Jobs(); } catch (Exception cleanup) { failure ??= cleanup; }
            using var file = File.Create(Path.Combine(root, "assertions.json")); using var json = new Utf8JsonWriter(file, new() { Indented = true });
            json.WriteStartObject(); json.WriteNumber("schemaVersion", 1); json.WriteString("scope", "actual headless controls/keys with injected snapshot hooks; OS pointer and clipboard unverified");
            json.WriteString("archiveRootPath", archiveRoot); if (archiveRoot is not null && File.Exists(archiveRoot)) Bytes(json, "archiveRoot", File.ReadAllBytes(archiveRoot)); else json.WriteNull("archiveRoot");
            json.WriteString("failureType", failure?.GetType().FullName); json.WriteString("failureMessage", failure?.Message);
            json.WriteStartArray("cases"); foreach (var row in rows) { using var doc = JsonDocument.Parse(row); doc.RootElement.WriteTo(json); } json.WriteEndArray();
            json.WriteStartObject("ownedEnd"); json.WriteNumber("expectedPanes", panes.Count); json.WriteNumber("disposedCallbacks", disposed); json.WriteBoolean("visible", window.IsVisible); json.WriteNumber("ownedWindows", window.OwnedWindows.Count());
            json.WriteStartArray("tasks"); foreach (var task in tasks) { json.WriteStartObject(); json.WriteNumber("token", Token(task)); json.WriteString("status", task.Status.ToString()); json.WriteBoolean("completed", task.IsCompleted); json.WriteBoolean("canceled", task.IsCanceled); json.WriteBoolean("faulted", task.IsFaulted); json.WriteEndObject(); } json.WriteEndArray();
            json.WriteNumber("clipboardReads", clipboard.Reads); json.WriteNumber("clipboardWrites", clipboard.Writes); json.WriteEndObject(); json.WriteEndObject();
            check("Binary search raw completed", failure is null && disposed == panes.Count && tasks.All(task => task.IsCompletedSuccessfully) && clipboard.Reads == 0 && clipboard.Writes == 0, "Independent reader verifies literals/state; producer bool is supplemental");
        }
        int Token(object? value) { if (value is null) return 0; if (!identities.TryGetValue(value, out var id)) identities.Add(value, id = identities.Count + 1); return id; }
        string Blob(ReadOnlySpan<byte> bytes)
        { var path = Path.Combine(blobs, Convert.ToHexString(SHA256.HashData(bytes)) + ".bin"); if (!File.Exists(path)) File.WriteAllBytes(path, bytes.ToArray()); return path; }
        void Bytes(Utf8JsonWriter json, string name, ReadOnlySpan<byte> bytes)
        { var path = Blob(bytes); json.WriteStartObject(name); json.WriteString("path", path); json.WriteNumber("length", bytes.Length); json.WriteString("sha256", Path.GetFileNameWithoutExtension(path)); json.WriteEndObject(); }
        byte[] State(SpecializedViews.BinaryPanel panel, int side)
        {
            using var buffer = new MemoryStream(); using var json = new Utf8JsonWriter(buffer);
            json.WriteStartObject(); json.WriteNumber("sessionToken", Token(panel.Session)); json.WriteNumber("panelToken", Token(panel)); json.WriteNumber("selectionVersion", panel.SelectionVersion); json.WriteNumber("stateVersion", panel.SearchObservedStateVersion); json.WriteNumber("pageStart", panel.SearchObservedPageStart);
            json.WriteBoolean("canUndo", panel.Session.CanUndo); json.WriteBoolean("canRedo", panel.Session.CanRedo); json.WriteNumber("modalCount", window.OwnedWindows.Count(item => item.IsVisible));
            json.WriteStartArray("sides"); foreach (var value in panel.ProjectSides)
            {
                var local = panel.LocalSide(value); var selection = panel.Selection(value); json.WriteStartObject(); json.WriteNumber("side", value); json.WriteNumber("revision", panel.Session.Revision(local)); json.WriteBoolean("dirty", panel.Session.IsDirty(local)); json.WriteBoolean("readOnly", panel.ReadOnly(value)); json.WriteBoolean("sessionReadOnly", panel.Session.IsReadOnly(local));
                Bytes(json, "bytes", panel.CaptureApplied(value).CopyBytes()); json.WriteNumber("anchor", selection.Anchor); json.WriteNumber("caret", selection.Caret); json.WriteBoolean("selected", selection.Selected); json.WriteBoolean("oem", selection.Oem);
                var request = panel.SearchObservedPrevious(value); json.WriteStartObject("previous"); json.WriteBoolean("present", request is not null); json.WriteString("text", request?.Text); json.WriteBoolean("fromSelection", request?.FromSelection ?? false); json.WriteBoolean("utf16", request?.Utf16 ?? false); json.WriteBoolean("bigEndian", request?.BigEndian ?? false); json.WriteBoolean("matchCase", request?.MatchCase ?? false); json.WriteEndObject();
                var cache = panel.SearchObservedSelectedPattern(value); if (cache is null) json.WriteNull("cache"); else Bytes(json, "cache", cache);
                json.WriteBoolean("findEnabled", SearchButton(panel, value, "検索").IsEnabled); json.WriteBoolean("nextEnabled", SearchButton(panel, value, "次を検索").IsEnabled); json.WriteBoolean("previousEnabled", SearchButton(panel, value, "前を検索").IsEnabled); json.WriteEndObject();
            } json.WriteEndArray(); json.WriteEndObject(); json.Flush(); return buffer.ToArray();
        }
        void Step(string id, string kind, SpecializedViews.BinaryPanel panel, int side, Action action, string? saved = null)
        {
            clocks.Restart(); uiEvents.Clear(); eventPanel = panel; eventSide = side; var before = State(panel, side); byte[]? observed = null; byte[]? eventState = null; var hook = false;
            var priorObserver = panel.SearchSnapshotObserved;
            panel.SearchSnapshotObserved = (target, request, input, pattern, caret, found) =>
            {
                using var memory = new MemoryStream(); using var json = new Utf8JsonWriter(memory); json.WriteStartObject(); json.WriteNumber("side", target); json.WriteNumber("caret", caret); json.WriteNumber("found", found); json.WriteBoolean("backwards", request.Backwards); json.WriteBoolean("matchCase", request.MatchCase); json.WriteBoolean("utf16", request.Utf16); json.WriteBoolean("bigEndian", request.BigEndian); json.WriteBoolean("oem", panel.Selection(target).Oem); json.WriteBoolean("fromSelection", request.FromSelection); json.WriteString("text", request.Text); Bytes(json, "input", input.Span); Bytes(json, "pattern", pattern.Span); json.WriteEndObject(); json.Flush(); observed = memory.ToArray();
            };
            var hookPrevious = panel.SearchReadyForAdoption;
            if (PendingHook is { } callback) panel.SearchReadyForAdoption = () => { hook = true; callback(); Jobs(); eventState = State(panel, side); };
            try { action(); if (panel.CurrentSearchOperation is { } operation) Wait(operation); Jobs(); }
            finally { panel.SearchSnapshotObserved = priorObserver; panel.SearchReadyForAdoption = hookPrevious; PendingHook = null; }
            var after = State(panel, side);
            using var row = new MemoryStream(); using var writer = new Utf8JsonWriter(row); writer.WriteStartObject(); writer.WriteString("id", id); writer.WriteString("kind", kind); writer.WriteNumber("side", side); writer.WriteNumber("elapsedMilliseconds", clocks.ElapsedMilliseconds); writer.WriteNumber("limitMilliseconds", 30000); writer.WriteBoolean("hookReached", hook);
            Add("before", before); Add("after", after); Add("search", observed); Add("eventState", eventState); writer.WriteString("savedPath", saved); if (saved is not null && File.Exists(saved)) Bytes(writer, "savedBytes", File.ReadAllBytes(saved)); else writer.WriteNull("savedBytes");
            writer.WriteString("observationFailure", panel.SearchObservedFailure?.GetType().FullName);
            var actualTask = panel.CurrentSearchOperation; writer.WriteNumber("searchTaskToken", Token(actualTask)); writer.WriteString("searchTaskStatus", actualTask?.Status.ToString()); if (actualTask is Task<bool> actual && actual.IsCompletedSuccessfully) writer.WriteBoolean("returnAdopted", actual.Result); else writer.WriteNull("returnAdopted");
            writer.WriteStartArray("uiEvents"); foreach (var item in uiEvents) { using var doc = JsonDocument.Parse(item); doc.RootElement.WriteTo(writer); } writer.WriteEndArray();
            if (clocks.ElapsedMilliseconds > 30000) throw new TimeoutException("Binary search shared case deadline"); writer.WriteEndObject(); writer.Flush(); rows.Add(row.ToArray());
            void Add(string name, byte[]? value) { writer.WritePropertyName(name); if (value is null) writer.WriteNullValue(); else { using var doc = JsonDocument.Parse(value); doc.RootElement.WriteTo(writer); } }
        }
        void Dialog(string id, SpecializedViews.BinaryPanel panel, int side, string text, bool match, bool utf16, bool big, bool back, string route)
        {
            BinarySearchDialog.Shown = dialog => { DialogEvent(dialog, "opened"); dialog.Text.Text = text; dialog.MatchCase.IsChecked = match; dialog.Utf16.IsChecked = utf16; dialog.BigEndian.IsChecked = big; dialog.Direction.SelectedIndex = back ? 1 : 0; DialogEvent(dialog, "input"); Hit(dialog.Accept); Wait(dialog.CurrentOperation); DialogEvent(dialog, "finished"); };
            try { panel.Editor(side).Focus(); Step(id, "search", panel, side, () => { if (route == "CtrlF") Stroke(Key.F, PhysicalKey.F, RawInputModifiers.Control); else Hit(SearchButton(panel, side, "検索")); }); }
            finally { BinarySearchDialog.Shown = null; }
        }
        void Invalid(string id, SpecializedViews.BinaryPanel panel, int side)
        {
            BinarySearchDialog.Shown = dialog => { DialogEvent(dialog, "opened"); dialog.Text.Text = ""; DialogEvent(dialog, "invalid-input"); Hit(dialog.Accept); Wait(dialog.CurrentOperation); DialogEvent(dialog, "invalid-result"); if (!dialog.IsVisible || !dialog.Accept.IsEnabled) throw new InvalidOperationException("Invalid search dialog was not correctable."); dialog.Utf16.IsChecked = true; dialog.Text.Text = "\ud800"; DialogEvent(dialog, "invalid-surrogate-input"); Hit(dialog.Accept); Wait(dialog.CurrentOperation); DialogEvent(dialog, "invalid-surrogate-result"); if (!dialog.IsVisible || !dialog.Accept.IsEnabled) throw new InvalidOperationException("Surrogate failure did not preserve dialog."); dialog.Utf16.IsChecked = false; dialog.Text.Text = "AB"; DialogEvent(dialog, "corrected-input"); Hit(dialog.Accept); Wait(dialog.CurrentOperation); DialogEvent(dialog, "finished"); };
            try { Step(id, "search", panel, side, () => Hit(SearchButton(panel, side, "検索"))); } finally { BinarySearchDialog.Shown = null; }
        }
        void Stale(string id, ComparisonPane pane, SpecializedViews.BinaryPanel panel, int side, string route)
        {
            BinarySearchDialog? active = null; PendingHook = () =>
            {
                if (route == "cancel") Click(active!.Cancel);
                else if (route == "selection") panel.SelectBytes(side, 1, 1, false);
                else if (route == "page") panel.SelectBytes(side, 4096, 4096, false);
                else if (route == "readonly") SetReadOnly(panel, side, !panel.ReadOnly(side));
                else if (route == "owner-input") { var original = pane.LeftPath.Text; pane.LeftPath.Text = source + ".missing"; pane.LeftPath.Text = original; }
                else if (route == "observer-throw") throw new IOException("injected observer failure");
                else panel.SelectBytes(side, 3, 3, false);
            };
            BinarySearchDialog.Shown = dialog => { active = dialog; dialog.Text.Text = "AB"; Hit(dialog.Accept); Wait(dialog.CurrentOperation); if (dialog.IsVisible) Hit(dialog.Cancel); };
            try { Step(id, "stale", panel, side, () => Hit(SearchButton(panel, side, "検索"))); }
            finally { BinarySearchDialog.Shown = null; PendingHook = null; SetReadOnly(panel, side, false); }
        }
        void DialogEvent(BinarySearchDialog dialog, string phase)
        {
            using var stream = new MemoryStream(); using var json = new Utf8JsonWriter(stream); json.WriteStartObject(); json.WriteString("event", "dialog"); json.WriteString("phase", phase); json.WriteBoolean("visible", dialog.IsVisible); json.WriteString("title", dialog.Title); json.WriteString("status", dialog.Status.Text); json.WriteBoolean("acceptEnabled", dialog.Accept.IsEnabled); json.WriteBoolean("textEnabled", dialog.Text.IsEnabled); json.WriteBoolean("cancelEnabled", dialog.Cancel.IsEnabled); json.WriteBoolean("matchCase", dialog.MatchCase.IsChecked == true); json.WriteBoolean("utf16", dialog.Utf16.IsChecked == true); json.WriteBoolean("bigEndian", dialog.BigEndian.IsChecked == true); json.WriteNumber("direction", dialog.Direction.SelectedIndex); json.WriteStartArray("textCodeUnits"); foreach (var ch in dialog.Text.Text ?? "") json.WriteNumberValue(ch); json.WriteEndArray(); if (eventPanel is not null) { json.WritePropertyName("state"); using var doc = JsonDocument.Parse(State(eventPanel, eventSide)); doc.RootElement.WriteTo(json); } json.WriteEndObject(); json.Flush(); uiEvents.Add(stream.ToArray());
        }
        void Large()
        {
            var bytes = new byte[BinaryEditSession.MaximumFileBytes]; var large = Blob(bytes); var pane = Add(large, false); var panel = Binary(pane);
            panel.SelectBytes(0, 0, bytes.Length - 1); Step("large/cache16MiB", "search", panel, 0, () => Hit(SearchButton(panel, 0, "次を検索")));
            panel.SelectBytes(0, bytes.Length, bytes.Length, false); Step("large/previous16MiB", "search", panel, 0, () => Stroke(Key.F3, PhysicalKey.F3, RawInputModifiers.Shift));
            panel.SelectBytes(0, 0, 4681); Step("large/selection4682", "search", panel, 0, () => Stroke(Key.F3, PhysicalKey.F3));
            Step("large/dialog-reject4682", "dialog-cap", panel, 0, () => Hit(SearchButton(panel, 0, "検索")));
            panel.SelectBytes(0, 0, 4680); BinarySearchDialog.Shown = dialog => { DialogEvent(dialog, "opened-boundary"); Hit(dialog.Cancel); };
            try { Step("large/dialog-accept4681-cancel", "dialog-cancel", panel, 0, () => Hit(SearchButton(panel, 0, "検索"))); } finally { BinarySearchDialog.Shown = null; }
            var shared = new byte[BinaryEditSession.MaximumFileBytes + 1]; Core("core/over-input", shared, [0], 0); Core("core/over-pattern", [0], shared, 0);
        }
        void CoreCases()
        {
            foreach (var back in new[] { false, true }) foreach (var caret in new[] { 0, 1, 2, 3, 4 }) Core("core/overlap/" + back + "/" + caret, "AAAA"u8.ToArray(), "AA"u8.ToArray(), caret, back);
            Core("core/ascii", [0x61, 0x41, 0xc0, 0xe0, 0x61], [0x41], 1, false, false); Core("core/nonascii", [0xc0, 0xe0], [0xc0], 0, false, false); Core("core/empty", [], [1], 0); Core("core/no-pattern", [1], [], 0); Core("core/caret-negative", [1], [1], -1); Core("core/canceled", [1], [1], 0, false, true, true);
        }
        void Core(string id, byte[] input, byte[] pattern, int caret, bool back = false, bool match = true, bool canceled = false)
        {
            int? found = null; string? error = null; using var cancellation = new CancellationTokenSource(); if (canceled) cancellation.Cancel();
            try { found = BinaryByteSearch.Find(input, pattern, caret, back, match, cancellation.Token); } catch (Exception failure) { error = failure.GetType().FullName; }
            using var row = new MemoryStream(); using var json = new Utf8JsonWriter(row); json.WriteStartObject(); json.WriteString("id", id); json.WriteString("kind", "core"); Bytes(json, "input", input); Bytes(json, "pattern", pattern); json.WriteNumber("caret", caret); json.WriteBoolean("backwards", back); json.WriteBoolean("matchCase", match); json.WriteBoolean("cancellationRequested", canceled); if (found.HasValue) json.WriteNumber("found", found.Value); else json.WriteNull("found"); json.WriteString("exceptionType", error); json.WriteEndObject(); json.Flush(); rows.Add(row.ToArray());
        }
        ComparisonPane Add(string path, bool three)
        { var pane = window.AddSession(); panes.Add(pane); pane.IndependentTextInputParentDisposed += () => disposed++; pane.ApplyProject(new() { Mode = "Binary", LeftPath = path, BasePath = three ? path : "", RightPath = path }); Wait(pane.ComparePathsAsync()); var panel = Binary(pane); panel.ClipboardOverride = clipboard; return pane; }
        void Wait(Task task) { if (!tasks.Contains(task)) tasks.Add(task); var timer = Stopwatch.StartNew(); while (!task.IsCompleted) { Jobs(); if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException("Binary search task30second cap"); Thread.Sleep(1); } task.GetAwaiter().GetResult(); Jobs(); }
        void Jobs() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        void FocusEvidence(Utf8JsonWriter json, Window owner)
        {
            var focused = owner.FocusManager?.GetFocusedElement();
            json.WriteNumber("focusedOpaqueId", Token(focused)); json.WriteString("focusedType", focused?.GetType().FullName); json.WriteString("focusedContent", (focused as ContentControl)?.Content?.ToString());
            json.WriteBoolean("ownerIsActive", owner.IsActive); json.WriteBoolean("focusedIsEnabled", focused is InputElement input && input.IsEffectivelyEnabled);
        }
        void Stroke(Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None) { window.KeyPress(key, modifiers, physical, null); window.KeyRelease(key, modifiers, physical, null); Jobs(); using var stream = new MemoryStream(); using var json = new Utf8JsonWriter(stream); json.WriteStartObject(); json.WriteString("event", "key"); json.WriteString("key", key.ToString()); json.WriteString("modifiers", modifiers.ToString()); json.WriteBoolean("pressedAndReleased", true); FocusEvidence(json, window); json.WriteEndObject(); json.Flush(); uiEvents.Add(stream.ToArray()); }
        void Hit(Control control) { control.BringIntoView(); Jobs(); if (!control.IsEnabled) throw new InvalidOperationException("Disabled test control"); var owner = TopLevel.GetTopLevel(control) as Window ?? throw new InvalidOperationException("Test control owner missing"); var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), owner) ?? throw new InvalidOperationException("Test control bounds missing"); if (point.X < 0 || point.Y < 0 || point.X >= owner.Bounds.Width || point.Y >= owner.Bounds.Height) throw new InvalidOperationException("Test control pointer is outside viewport"); owner.MouseDown(point, MouseButton.Left); owner.MouseUp(point, MouseButton.Left); Jobs(); using var stream = new MemoryStream(); using var json = new Utf8JsonWriter(stream); json.WriteStartObject(); json.WriteString("event", "pointer"); json.WriteString("content", (control as ContentControl)?.Content?.ToString()); json.WriteNumber("x", point.X); json.WriteNumber("y", point.Y); json.WriteBoolean("pressedAndReleased", true); FocusEvidence(json, owner); json.WriteEndObject(); json.Flush(); uiEvents.Add(stream.ToArray()); }
        void Click(Button button)
        {
            // 採用直前の取消はnested dispatcher内なので、遅延しない実ボタンClick経路で発火する。
            if (!button.IsEnabled) throw new InvalidOperationException("Disabled test button");
            var owner = TopLevel.GetTopLevel(button) as Window ?? throw new InvalidOperationException("Test button owner missing");
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Jobs();
            using var stream = new MemoryStream(); using var json = new Utf8JsonWriter(stream);
            json.WriteStartObject(); json.WriteString("event", "routed-click"); json.WriteString("content", button.Content?.ToString());
            json.WriteBoolean("raised", true); FocusEvidence(json, owner); json.WriteEndObject(); json.Flush(); uiEvents.Add(stream.ToArray());
        }
    }
    private static Action? PendingHook;
    private static SpecializedViews.BinaryPanel Binary(ComparisonPane pane) => pane.GetVisualDescendants().OfType<SpecializedViews.BinaryPanel>().Single();
    private static Button FindButton(SpecializedViews.BinaryPanel panel, string text) => panel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, text));
    private static Button SearchButton(SpecializedViews.BinaryPanel panel, int side, string text) => FindButton(panel, (side switch { 0 => "左", 1 => "中央", _ => "右" }) + " " + text);
    private static void SetReadOnly(SpecializedViews.BinaryPanel panel, int side, bool value) { if (side == 0) panel.LeftReadOnly = value; else if (side == 1) panel.MiddleReadOnly = value; else panel.RightReadOnly = value; panel.ApplyReadOnly?.Invoke(); }
    private sealed class UnusedClipboard : IBinaryClipboard
    { internal int Reads, Writes; public Task WriteAsync(byte[] bytes, string text, CancellationToken token) { Writes++; throw new InvalidOperationException("Search accessed clipboard"); } public Task<IReadOnlyList<BinaryClipboardValue>> ReadAsync(CancellationToken token) { Reads++; throw new InvalidOperationException("Search accessed clipboard"); } }
}
