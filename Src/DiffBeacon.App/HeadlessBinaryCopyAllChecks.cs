using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessBinaryCopyAllChecks
{
    internal static void Run(MainWindow window, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "binary-copy-all"); Directory.CreateDirectory(folder);
        byte[][] original = [[0, 1, 255], [10, 11, 12, 13, 128], [20, 21, 22, 23, 24, 25, 26]];
        var files = new[] { "left.bin", "middle.bin", "right.bin" }.Select(name => Path.Combine(folder, name)).ToArray();
        var roots = new[] { "left.zip", "middle.zip", "right.zip" }.Select(name => Path.Combine(folder, name)).ToArray();
        for (var side = 0; side < 3; side++) { File.WriteAllBytes(files[side], original[side]); File.WriteAllBytes(roots[side], HeadlessBinaryWorkingChecks.Zip(("leaf.bin", original[side]))); }
        var hashes = roots.Select(Hash).ToArray();
        var snapshots = new List<(string Name, int Source, int Destination, string[] Hex)>();
        var confirmationCount = 0;
        try
        {
            foreach (var three in new[] { true, false })
                foreach (var source in three ? new[] { 0, 1, 2 } : new[] { 0, 2 })
                    foreach (var destination in (three ? new[] { 0, 1, 2 } : new[] { 0, 2 }).Where(side => side != source))
                    {
                        var pane = Add(three); var panel = Binary(pane); var before = Bytes(panel);
                        var expected = before.Select(bytes => bytes.ToArray()).ToArray(); var target = panel.LocalSide(destination); var from = panel.LocalSide(source);
                        expected[target] = before[from].Concat(before[target].Skip(before[from].Length)).ToArray();
                        var name = (three ? "three" : "two") + $"-{source}-{destination}";
                        Respond(false); Click(panel, Label(source, destination));
                        Report(name + " cancel preserves bytes history savedpoint", Equal(panel, before) && !panel.Session.CanUndo && !panel.Session.HasUnsavedChanges);
                        Respond(true); Click(panel, Label(source, destination));
                        Report(name + " accept prefix and independent lengths", Equal(panel, expected) && panel.Session.CanUndo && panel.Dirty(destination));
                        Observe(name, source, destination, panel);
                        var saved = Path.Combine(folder, name + ".bin"); pump(panel.SaveToAsync(destination, saved));
                        Report(name + " save exact and own savepoint", File.ReadAllBytes(saved).SequenceEqual(expected[target]) && !panel.Dirty(destination));
                        Click(panel, "元に戻す"); Report(name + " shared Undo restores exact bytes length dirty", Equal(panel, before) && panel.Dirty(destination) && panel.Session.CanRedo);
                        Click(panel, "やり直す"); Report(name + " shared Redo restores savedpoint", Equal(panel, expected) && !panel.Dirty(destination));
                        if (three && source == 0 && destination == 1)
                        { window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), pane)); screenshot("binary-copy-all-normal.png"); window.Width = 900; window.Height = 650; Dispatcher.UIThread.RunJobs(); panel.Viewport.Offset = new Avalonia.Vector(0, 100000); Dispatcher.UIThread.RunJobs(); screenshot("binary-copy-all-minimum.png"); window.Width = 1280; window.Height = 850; }
                    }
            foreach (var pending in new[] { "AA BB CC", "GG" })
            {
                var pane = Add(true); var panel = Binary(pane); var before = Bytes(panel); panel.LeftHex.Text = pending; var count = confirmationCount;
                Report("pending draft refused " + pending, Refused(() => pump(panel.CopyAllAsync(0, 1))) && Equal(panel, before) && panel.LeftHex.Text == pending && !panel.Session.CanUndo && count == confirmationCount);
                pane.DiscardChanges();
            }
            foreach (var kind in new[] { "draft", "revision", "readonly", "path", "mode", "recompare", "disposed", "owner" })
            {
                var pane = Add(true); var panel = Binary(pane); var before = Bytes(panel);
                Dialogs.ConfirmationShown = dialog =>
                {
                    confirmationCount++;
                    if (kind == "draft") panel.LeftHex.Text = "GG";
                    if (kind == "revision") { panel.LeftHex.Text = "AA 01 FF"; panel.Apply(0); panel.Undo(); }
                    if (kind == "readonly") panel.MiddleReadOnly = true;
                    if (kind == "path") pane.LeftPath.Text = files[0] + ".changed";
                    if (kind == "mode") pane.SelectMode(1);
                    if (kind == "recompare") pump(pane.ComparePathsAsync());
                    if (kind == "disposed")
                    {
                        var tab = window.GetVisualDescendants().OfType<TabControl>().SelectMany(control => control.Items.OfType<TabItem>()).Single(item => ReferenceEquals(item.Content, pane));
                        ((StackPanel)tab.Header!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    if (kind == "owner") { pane.ApplyProject(new() { Mode = "Binary", LeftPath = files[0], RightPath = files[2] }); pump(pane.ComparePathsAsync()); }
                    Answer(dialog, true);
                };
                Report("confirmation stale guard " + kind, Refused(() => pump(panel.CopyAllAsync(0, 1))) && (panel.IsDisposed || Equal(panel, before)));
                if (kind != "disposed") pane.DiscardChanges();
            }
            {
                var pane = Add(true); var panel = Binary(pane); var before = Bytes(panel); panel.MiddleReadOnly = true; var count = confirmationCount;
                Report("GUI readonly destination rejects before dialog", Refused(() => pump(panel.CopyAllAsync(0, 1))) && Equal(panel, before) && !panel.Session.CanUndo && count == confirmationCount);
                panel.MiddleReadOnly = false; panel.LeftReadOnly = true; Respond(true); Click(panel, Label(0, 1));
                Report("GUI readonly source copies into writable destination", panel.Capture(1).CopyBytes().SequenceEqual(new byte[] { 0, 1, 255, 13, 128 }));
                pane.DiscardChanges();
            }
            using (var equal = new SpecializedViews.BinaryPanel([1, 2], [3, 4], false, false))
            { equal.CopyAll(0, 2); Report("equal length different bytes copies", equal.Capture(2).CopyBytes().SequenceEqual(new byte[] { 1, 2 }) && equal.Session.CanUndo); equal.Undo(); Report("equal length Undo exact", equal.Capture(2).CopyBytes().SequenceEqual(new byte[] { 3, 4 })); }
            Dialogs.ConfirmationShown = null;
            using (var panel = new SpecializedViews.BinaryPanel([], [9, 8], true, false, [9, 8]))
            {
                panel.CopyAll(0, 2); Report("empty readonly source noop", panel.Capture(2).CopyBytes().SequenceEqual(new byte[] { 9, 8 }) && !panel.Session.CanUndo);
                panel.CopyAll(1, 2); Report("identical bytes noop", !panel.Session.CanUndo && !panel.Dirty(2)); panel.RightReadOnly = true;
                Report("empty source still refuses readonly destination", Refused(() => panel.CopyAll(0, 2)) && !panel.Session.CanUndo);
            }
            using (var panel = new SpecializedViews.BinaryPanel([1, 2, 3], [8], true, false))
            { panel.CopyAll(0, 2); Report("readonly source allowed", panel.Capture(2).CopyBytes().SequenceEqual(new byte[] { 1, 2, 3 })); panel.Undo(); Report("Undo restores originally shorter length", panel.Capture(2).Length == 1); }
            using (var history = new SpecializedViews.BinaryPanel([1], [0], false, false, [0]))
            {
                for (var index = 0; index < BinaryEditSession.MaximumHistoryActions; index++) { history.MiddleHex.Text = index % 2 == 0 ? "01" : "00"; history.Apply(1); }
                var stamp = history.StateStamp; var before = Bytes(history);
                Report("256 shared actions CopyAll atomic refusal", Refused(() => history.CopyAll(0, 1)) && Equal(history, before) && history.StateStamp == stamp && history.Session.CanUndo);
                history.CopyAll(2, 1); Report("capacity noop does not advance revision", history.StateStamp == stamp);
            }
            using (var history = new SpecializedViews.BinaryPanel(Enumerable.Repeat((byte)1, BinaryEditSession.MaximumFileBytes).ToArray(), Enumerable.Repeat((byte)2, BinaryEditSession.MaximumFileBytes).ToArray(), false, false, new byte[BinaryEditSession.MaximumFileBytes]))
            {
                history.CopyAll(0, 1); history.CopyAll(2, 0); var before = Bytes(history); var stamp = history.StateStamp;
                Report("64MiB shared budget atomic refusal", Refused(() => history.CopyAll(0, 1)) && Equal(history, before) && history.StateStamp == stamp && history.Session.CanUndo);
                history.Undo(); history.Undo();
                Report("16MiB Undo exact", history.Capture(0).CopyBytes().All(value => value == 1) && history.Capture(1).CopyBytes().All(value => value == 0) && history.Capture(2).CopyBytes().All(value => value == 2));
            }
            Report("over 16MiB refused before editor creation", Refused(() => { using var session = new BinaryEditSession(new byte[BinaryEditSession.MaximumFileBytes + 1], []); }));
            var sourcePane = window.AddSession(); sourcePane.ApplyProject(new() { Mode = "Binary", LeftArchiveInput = Input(0), BaseArchiveInput = Input(1), RightArchiveInput = Input(2), LeftReadOnly = true, BaseReadOnly = true, RightReadOnly = true }); pump(sourcePane.ComparePathsAsync());
            var sourcePanel = Binary(sourcePane); Respond(true); Click(sourcePanel, Label(2, 1)); pump(sourcePanel.SaveAsync(1));
            Observe("archive-2-1", 2, 1, sourcePanel);
            Report("archive working save exact and roots preserved", sourcePanel.Capture(1).CopyBytes().SequenceEqual(original[2]) && !sourcePanel.Dirty(1) && roots.Select(Hash).SequenceEqual(hashes));
            var project = sourcePane.CaptureProject(); var workspace = Path.Combine(folder, "working.json"); pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new() { Entries = [project] }, publishedAsset: window.ArchiveLifetime.RegisterAsset));
            var load = WorkspaceStore.LoadAsync(workspace); pump(load); var loaded = window.AddSession(); loaded.ApplyProject(load.Result); pump(loaded.ComparePathsAsync());
            Report("workspace reload copied bytes", Binary(loaded).Capture(1).CopyBytes().SequenceEqual(original[2]));
            var package = Path.Combine(folder, "working-package.zip"); pump(ComparisonPackage.CreateAsync(new() { Entries = [project] }, package, new(true, false, false, true)));
            using (var stream = File.Create(Path.Combine(folder, "facts.json")))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject(); writer.WriteString("workspace", workspace); writer.WriteString("package", package); writer.WriteNumber("confirmations", confirmationCount); writer.WriteStartArray("rootSha256"); foreach (var sha in hashes) writer.WriteStringValue(sha); writer.WriteEndArray(); writer.WriteStartArray("snapshots");
                foreach (var state in snapshots) { writer.WriteStartObject(); writer.WriteString("name", state.Name); writer.WriteNumber("source", state.Source); writer.WriteNumber("destination", state.Destination); writer.WriteStartArray("hex"); foreach (var hex in state.Hex) writer.WriteStringValue(hex); writer.WriteEndArray(); writer.WriteEndObject(); }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
        }
        finally { Dialogs.ConfirmationShown = null; foreach (var pane in window.SessionPanes) pane.DiscardChanges(); }
        ComparisonPane Add(bool three) { var pane = window.AddSession(); pane.ApplyProject(new() { Mode = "Binary", LeftPath = files[0], BasePath = three ? files[1] : "", RightPath = files[2] }); pump(pane.ComparePathsAsync()); return pane; }
        ArchiveProjectInput Input(int side) => new() { RootPath = roots[side], RootSha256 = hashes[side], EntryChain = [], LeafEntry = "leaf.bin", InheritedReadOnly = false };
        void Respond(bool accept) => Dialogs.ConfirmationShown = dialog => { confirmationCount++; Answer(dialog, accept); };
        void Observe(string name, int source, int destination, SpecializedViews.BinaryPanel panel) => snapshots.Add((name, source, destination, Bytes(panel).Select(Convert.ToHexString).ToArray()));
        void Report(string name, bool passed) { try { check("Binary Copy All " + name, passed, "actual GUI buttons, exact captures and independent artifacts"); } catch (InvalidOperationException) { } }
    }
    private static string Label(int source, int destination) => Name(source) + "→" + Name(destination) + " 全体コピー";
    private static string Name(int side) => side switch { 0 => "左", 1 => "中央", _ => "右" };
    private static byte[][] Bytes(SpecializedViews.BinaryPanel panel) => panel.ProjectSides.Select(side => panel.CaptureApplied(side).CopyBytes()).ToArray();
    private static bool Equal(SpecializedViews.BinaryPanel panel, byte[][] expected) => Bytes(panel).Zip(expected).All(pair => pair.First.SequenceEqual(pair.Second));
    private static SpecializedViews.BinaryPanel Binary(ComparisonPane pane) => pane.GetVisualDescendants().OfType<SpecializedViews.BinaryPanel>().Single();
    private static void Click(SpecializedViews.BinaryPanel panel, string title) { panel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, title)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    private static void Answer(Window dialog, bool accept) => dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, accept ? "続行" : "キャンセル")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static bool Refused(Action action) { try { action(); return false; } catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException or OperationCanceledException or ObjectDisposedException) { return true; } }
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
}
