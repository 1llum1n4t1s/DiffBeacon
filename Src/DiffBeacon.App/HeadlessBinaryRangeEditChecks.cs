using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessBinaryRangeEditChecks
{
    internal static void Run(MainWindow window, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "binary-range-edits"); Directory.CreateDirectory(folder);
        byte[][] originals = [[0, 1, 255], [10, 11, 12, 13, 128], [20, 21, 22, 23, 24, 25, 26]];
        var files = new[] { "left.bin", "middle.bin", "right.bin" }.Select(name => Path.Combine(folder, name)).ToArray();
        var roots = new[] { "left.zip", "middle.zip", "right.zip" }.Select(name => Path.Combine(folder, name)).ToArray();
        for (var side = 0; side < 3; side++) { File.WriteAllBytes(files[side], originals[side]); File.WriteAllBytes(roots[side], HeadlessBinaryWorkingChecks.Zip(("leaf.bin", originals[side]))); }
        var hashes = roots.Select(Hash).ToArray(); var snapshots = new List<(string Name, string[] Hex)>();
        try
        {
            foreach (var three in new[] { true, false })
                foreach (var side in three ? new[] { 0, 1, 2 } : new[] { 0, 2 })
                {
                    var pane = Add(three); var panel = Binary(pane); var initial = Bytes(panel); var target = panel.LocalSide(side); var prefix = (three ? "three" : "two") + "-" + side;
                    BinaryRangeDialog.Shown = dialog => { dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
                    Click(panel, side, BinaryRangeKind.Insert); Report(prefix + " cancel", Equal(panel, initial) && !panel.Session.CanUndo);
                    BinaryRangeDialog.Shown = dialog =>
                    {
                        dialog.Offset.Text = "-1"; dialog.Hex.Text = "GG"; Accept(dialog); Report(prefix + " invalid remains open", dialog.IsVisible && dialog.Status.Text?.Length > 0 && Equal(panel, initial));
                        if (three && side == 1)
                        {
                            Dispatcher.UIThread.RunJobs(); window.CaptureRenderedFrame()?.Dispose();
                            using var frame = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("ダイアログの描画がありません。");
                            frame.Save(Path.Combine(folder, "range-invalid-dialog.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                        }
                        dialog.Offset.Text = "1"; dialog.Hex.Text = "AA BB"; Accept(dialog);
                    };
                    Click(panel, side, BinaryRangeKind.Insert);
                    var expected = initial.Select(value => value.ToArray()).ToArray(); expected[target] = initial[target].Take(1).Concat(new byte[] { 170, 187 }).Concat(initial[target].Skip(1)).ToArray();
                    Report(prefix + " insert", Equal(panel, expected));
                    Respond(1, "CC", 0); Click(panel, side, BinaryRangeKind.Overwrite); expected[target][1] = 204; Report(prefix + " overwrite", Equal(panel, expected));
                    Respond(2, "", 1); Click(panel, side, BinaryRangeKind.Delete); expected[target] = expected[target].Take(2).Concat(expected[target].Skip(3)).ToArray(); Report(prefix + " delete closes gap", Equal(panel, expected));
                    snapshots.Add((prefix, Bytes(panel).Select(Convert.ToHexString).ToArray()));
                    var saved = Path.Combine(folder, prefix + ".bin"); pump(panel.SaveToAsync(side, saved)); Report(prefix + " savepoint", !panel.Dirty(side));
                    panel.Undo(); Report(prefix + " undo delete dirty", panel.Dirty(side)); panel.Redo(); Report(prefix + " redo savepoint", Equal(panel, expected) && !panel.Dirty(side));
                    panel.Undo(); panel.Undo(); panel.Undo(); Report(prefix + " all undo exact", Equal(panel, initial)); panel.Redo(); panel.Redo(); panel.Redo(); Report(prefix + " all redo exact", Equal(panel, expected));
                    if (three && side == 1)
                    {
                        window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs(); screenshot("binary-range-normal.png");
                        window.Width = 850; window.Height = 550; Dispatcher.UIThread.RunJobs(); panel.Viewport.Offset = new Avalonia.Vector(0, panel.Viewport.Extent.Height); Dispatcher.UIThread.RunJobs(); screenshot("binary-range-minimum.png");
                        var viewport = panel.Viewport; var content = (Control)viewport.Content!;
                        var editorPosition = panel.MiddleHex.TranslatePoint(new Point(0, 0), viewport)!.Value;
                        Report("minimum Hex reachable", panel.ProjectSides.All(value => panel.Editor(value).Bounds.Height >= 100) && editorPosition.Y >= -1 && editorPosition.Y + panel.MiddleHex.Bounds.Height <= viewport.Bounds.Height + 1);
                        var save = panel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中央を保存"));
                        viewport.Offset = new Vector(0, save.TranslatePoint(new Point(0, 0), content)!.Value.Y); Dispatcher.UIThread.RunJobs(); screenshot("binary-range-minimum-save.png");
                        var savePosition = save.TranslatePoint(new Point(0, 0), viewport)!.Value;
                        Report("minimum save control reachable after scroll", savePosition.Y >= -1 && savePosition.Y + save.Bounds.Height <= viewport.Bounds.Height + 1);
                        File.WriteAllText(Path.Combine(folder, "minimum-bounds.json"), $"{{\"windowWidth\":{window.Width},\"windowHeight\":{window.Height},\"hexHeight\":{panel.MiddleHex.Bounds.Height},\"hexY\":{editorPosition.Y},\"saveY\":{savePosition.Y},\"saveHeight\":{save.Bounds.Height},\"viewportHeight\":{viewport.Bounds.Height}}}");
                        window.Width = 1280; window.Height = 850;
                    }
                    pane.DiscardChanges();
                }
            foreach (var change in new[] { "draft", "revision", "readonly", "path", "mode", "recompare", "owner", "disposed", "save", "provider", "cancel" })
            {
                var pane = Add(true); var panel = Binary(pane); var before = Bytes(panel);
                BinaryRangeDialog.Shown = dialog =>
                {
                    dialog.Hex.Text = "AA";
                    if (change == "draft") panel.RightHex.Text = "GG";
                    if (change == "revision") { panel.LeftHex.Text = "AA 01 FF"; panel.Apply(0); panel.Undo(); }
                    if (change == "readonly") panel.RightReadOnly = true;
                    if (change == "path") pane.LeftPath.Text = files[0] + ".changed";
                    if (change == "mode") pane.SelectMode(1);
                    if (change == "recompare") pump(pane.ComparePathsAsync());
                    if (change == "owner") { pane.ApplyProject(new() { Mode = "Binary", LeftPath = files[0], RightPath = files[2] }); pump(pane.ComparePathsAsync()); }
                    if (change == "provider") { var selector = pane.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Items.OfType<string>().Contains("xml")); selector.SelectedIndex = selector.SelectedIndex == 0 ? 1 : 0; }
                    if (change == "cancel") pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (change == "save") pump(panel.SaveToAsync(1, Path.Combine(folder, "stale-save.bin")));
                    if (change == "disposed")
                    {
                        var tab = window.GetVisualDescendants().OfType<TabControl>().SelectMany(control => control.Items.OfType<TabItem>()).Single(item => ReferenceEquals(item.Content, pane));
                        ((StackPanel)tab.Header!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    Accept(dialog);
                };
                Report("stale dialog " + change, Refused(() => pump(panel.RangeEditAsync(1, BinaryRangeKind.Insert))) && (panel.IsDisposed || Equal(panel, before)));
                if (!panel.IsDisposed) { Report("stale draft retained " + change, change != "draft" || panel.RightHex.Text == "GG"); pane.DiscardChanges(); }
            }
            {
                var pane = Add(true); var panel = Binary(pane); var shown = 0; BinaryRangeDialog.Shown = _ => shown++;
                panel.RightHex.Text = "GG"; Report("unrelated draft rejects before dialog", Refused(() => pump(panel.RangeEditAsync(1, BinaryRangeKind.Delete))) && shown == 0 && panel.RightHex.Text == "GG"); pane.DiscardChanges();
                panel.MiddleReadOnly = true; Report("readonly before dialog", Refused(() => pump(panel.RangeEditAsync(1, BinaryRangeKind.Insert))) && shown == 0);
            }
            {
                var pane = Add(true); var panel = Binary(pane); var before = Bytes(panel);
                foreach (var kind in new[] { BinaryRangeKind.Insert, BinaryRangeKind.Overwrite, BinaryRangeKind.Delete })
                {
                    BinaryRangeDialog.Shown = dialog =>
                    {
                        dialog.Offset.Text = "9223372036854775807"; dialog.Hex.Text = "AA"; Accept(dialog);
                        Report("dialog extreme rejects " + kind, dialog.IsVisible && Equal(panel, before));
                        dialog.Offset.Text = kind == BinaryRangeKind.Insert ? "0" : "5"; dialog.Count.Text = "1"; dialog.Hex.Text = kind == BinaryRangeKind.Insert ? "A" : "AA"; Accept(dialog);
                        Report("dialog odd or EOF rejects " + kind, dialog.IsVisible && Equal(panel, before));
                        if (kind == BinaryRangeKind.Insert)
                        {
                            dialog.Hex.Text = "GG"; Accept(dialog); Report("dialog nonhex rejects", dialog.IsVisible && Equal(panel, before));
                        }
                        dialog.Offset.Text = "0"; dialog.Count.Text = "0"; dialog.Hex.Text = kind == BinaryRangeKind.Overwrite ? "0A 0B 0C 0D 80" : ""; Accept(dialog);
                    };
                    Click(panel, 1, kind); Report("corrected noop preserves history " + kind, Equal(panel, before) && !panel.Session.CanUndo && !panel.Dirty(1));
                }
                pane.DiscardChanges();
            }
            BinaryRangeDialog.Shown = null;
            using (var panel = new SpecializedViews.BinaryPanel([], [], false, false))
            {
                Edit(panel, 2, BinaryRangeKind.Insert, 0, "AA BB"); Edit(panel, 2, BinaryRangeKind.Insert, 2, "CC"); Edit(panel, 2, BinaryRangeKind.Delete, 0, "", 3);
                Report("empty full delete", panel.Capture(2).Length == 0); panel.Undo(); Report("empty undo exact", panel.Capture(2).CopyBytes().SequenceEqual(new byte[] { 170, 187, 204 }));
                var stamp = panel.StateStamp; Edit(panel, 2, BinaryRangeKind.Insert, 3, ""); Edit(panel, 2, BinaryRangeKind.Overwrite, 0, "AA BB CC"); Report("noops preserve redo revision", panel.StateStamp == stamp && panel.Session.CanRedo);
                foreach (var request in new[] { new BinaryRangeRequest(BinaryRangeKind.Insert, -1, 0, "AA"), new(BinaryRangeKind.Delete, long.MaxValue, long.MaxValue, ""), new(BinaryRangeKind.Delete, 0, long.MaxValue, ""), new(BinaryRangeKind.Overwrite, 3, 0, "AA"), new(BinaryRangeKind.Insert, 0, 0, "A"), new(BinaryRangeKind.Insert, 0, 0, "GG") })
                    Report("bounds atomic " + request, Refused(() => panel.EditRange(2, request)) && panel.StateStamp == stamp && panel.Session.CanRedo);
                panel.RightReadOnly = true; Report("readonly noop rejected", Refused(() => Edit(panel, 2, BinaryRangeKind.Insert, 0, "")) && panel.StateStamp == stamp);
            }
            using (var panel = new SpecializedViews.BinaryPanel(new byte[4097], new byte[4097], false, false))
            {
                panel.SetOffset(4096); Edit(panel, 0, BinaryRangeKind.Delete, 4095, "", 2); Edit(panel, 2, BinaryRangeKind.Delete, 4095, "", 2);
                Report("4095 page clamp", panel.LeftHex.Text == "" && panel.RightHex.Text == ""); panel.Undo(); Report("boundary undo visible", panel.RightHex.Text?.Length > 0);
                Edit(panel, 2, BinaryRangeKind.Delete, 0, "", 4097); Edit(panel, 0, BinaryRangeKind.Delete, 0, "", 4095); Report("all sides empty clamp", panel.LeftHex.Text == "" && panel.RightHex.Text == ""); panel.Undo(); panel.SetOffset(0); Report("empty undo reachable", panel.LeftHex.Text?.Length > 0);
            }
            using (var panel = new SpecializedViews.BinaryPanel([0], [0], false, false))
            {
                for (var i = 0; i < BinaryEditSession.MaximumHistoryActions; i++) Edit(panel, i % 2 == 0 ? 0 : 2, BinaryRangeKind.Insert, 0, "01");
                var stamp = panel.StateStamp; Report("256 atomic", Refused(() => Edit(panel, 0, BinaryRangeKind.Insert, 0, "AA")) && stamp == panel.StateStamp); panel.Undo();
                Report("rejection preserves redo", Refused(() => Edit(panel, 0, BinaryRangeKind.Delete, 0, "", long.MaxValue)) && panel.Session.CanRedo);
            }
            using (var session = new BinaryEditSession(new byte[BinaryEditSession.MaximumFileBytes], []))
            {
                Report("16MiB insert refusal", Refused(() => session.EditRange(0, 0, 0, "AA", true, false)) && session.Revision(0) == 0);
                var block = new string('A', BinaryEditSession.MaximumFileBytes * 2);
                session.EditRange(0, 0, 0, block, false, false); session.EditRange(0, 0, BinaryEditSession.MaximumFileBytes, "", false, true); session.EditRange(0, 0, 0, block, true, false);
                var revision = session.Revision(0); Report("64MiB atomic", Refused(() => session.EditRange(0, 0, BinaryEditSession.MaximumFileBytes, "", false, true)) && revision == session.Revision(0));
                session.Undo(); Report("64MiB redo after refusal", session.CanRedo && session.Length(0) == 0); session.Redo(); Report("16MiB exact redo", session.Capture(0).CopyBytes().All(value => value == 170));
            }
            {
                // 共有予算の拒否と既存Redoを同時に検証する。巨大HexのTextBox描画はせず、dialog確定と同じpanel操作を使う。
                var budgetFile = Path.Combine(folder, "history-redo-original.bin"); var emptyFile = Path.Combine(folder, "history-redo-empty.bin");
                File.WriteAllBytes(budgetFile, new byte[BinaryEditSession.MaximumFileBytes]); File.WriteAllBytes(emptyFile, []);
                var pane = window.AddSession(); pane.ApplyProject(new() { Mode = "Binary", LeftPath = budgetFile, RightPath = emptyFile }); pump(pane.ComparePathsAsync());
                var panel = Binary(pane); var mib = 1024 * 1024;
                Edit(panel, 0, BinaryRangeKind.Overwrite, 0, new string('1', 12 * mib * 2));
                Edit(panel, 0, BinaryRangeKind.Overwrite, 0, new string('2', 12 * mib * 2)); panel.MarkClean?.Invoke();
                Edit(panel, 0, BinaryRangeKind.Overwrite, 0, new string('3', 8 * mib * 2)); panel.Undo();
                var stamp = panel.StateStamp; var revision = panel.Session.Revision(0); var before = Bytes(panel);
                File.WriteAllBytes(Path.Combine(folder, "history-redo-before.bin"), before[0]);
                Report("64MiB existing Redo clean setup", panel.Session.CanRedo && !panel.Dirty(0));
                Report("64MiB existing Redo rejection atomic", Refused(() => Edit(panel, 0, BinaryRangeKind.Overwrite, 0, new string('4', 16 * mib * 2)))
                    && Equal(panel, before) && panel.StateStamp == stamp && panel.Session.Revision(0) == revision && !panel.Dirty(0) && panel.Session.CanRedo);
                panel.Redo(); var expected = Enumerable.Repeat((byte)0x33, 8 * mib).Concat(Enumerable.Repeat((byte)0x22, 4 * mib)).Concat(new byte[4 * mib]).ToArray();
                File.WriteAllBytes(Path.Combine(folder, "history-redo-after.bin"), panel.Capture(0).CopyBytes());
                Report("64MiB preserved Redo exact full bytes", panel.Capture(0).CopyBytes().SequenceEqual(expected) && panel.Capture(2).Length == 0 && panel.Dirty(0));
                panel.Undo(); Report("64MiB saved point restored after Redo", Equal(panel, before) && !panel.Dirty(0) && panel.Session.CanRedo); pane.DiscardChanges();
            }
            {
                var pane = Add(true); var panel = Binary(pane); Respond(1, "AA", 0); Click(panel, 1, BinaryRangeKind.Insert);
                pane.BinarySaveBeforePublish = () => { Edit(panel, 1, BinaryRangeKind.Insert, 2, "BB"); return Task.CompletedTask; };
                pump(panel.SaveToAsync(1, Path.Combine(folder, "late.bin"))); Report("late save edit remains dirty", panel.Dirty(1) && File.ReadAllBytes(Path.Combine(folder, "late.bin")).SequenceEqual(new byte[] { 10, 170, 11, 12, 13, 128 }));
                pane.BinarySaveBeforePublish = null; pane.DiscardChanges();
            }
            var source = window.AddSession(); source.ApplyProject(new() { Mode = "Binary", LeftArchiveInput = Input(0), BaseArchiveInput = Input(1), RightArchiveInput = Input(2), LeftReadOnly = true, BaseReadOnly = true, RightReadOnly = true }); pump(source.ComparePathsAsync());
            var working = Binary(source); Respond(1, "AA BB", 0); Click(working, 1, BinaryRangeKind.Insert); Respond(1, "CC", 0); Click(working, 1, BinaryRangeKind.Overwrite); Respond(2, "", 1); Click(working, 1, BinaryRangeKind.Delete); pump(working.SaveAsync(1));
            Report("archive roots unchanged", roots.Select(Hash).SequenceEqual(hashes));
            var project = source.CaptureProject(); var workspace = Path.Combine(folder, "working.json"); pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new() { Entries = [project] }, publishedAsset: window.ArchiveLifetime.RegisterAsset));
            var load = WorkspaceStore.LoadAsync(workspace); pump(load); var loaded = window.AddSession(); loaded.ApplyProject(load.Result); pump(loaded.ComparePathsAsync()); Report("archive reload full bytes", Binary(loaded).Capture(1).CopyBytes().SequenceEqual(new byte[] { 10, 204, 11, 12, 13, 128 }));
            var package = Path.Combine(folder, "working-package.zip"); pump(ComparisonPackage.CreateAsync(new() { Entries = [project] }, package, new(true, false, false, true)));
            using var stream = File.Create(Path.Combine(folder, "facts.json")); using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject(); writer.WriteString("workspace", workspace); writer.WriteString("package", package); writer.WriteStartArray("rootSha256"); foreach (var hash in hashes) writer.WriteStringValue(hash); writer.WriteEndArray(); writer.WriteStartArray("snapshots");
            foreach (var item in snapshots) { writer.WriteStartObject(); writer.WriteString("name", item.Name); writer.WriteStartArray("hex"); foreach (var hex in item.Hex) writer.WriteStringValue(hex); writer.WriteEndArray(); writer.WriteEndObject(); } writer.WriteEndArray(); writer.WriteEndObject();
        }
        finally { BinaryRangeDialog.Shown = null; foreach (var pane in window.SessionPanes) pane.DiscardChanges(); }
        ComparisonPane Add(bool three) { var pane = window.AddSession(); pane.ApplyProject(new() { Mode = "Binary", LeftPath = files[0], BasePath = three ? files[1] : "", RightPath = files[2] }); pump(pane.ComparePathsAsync()); return pane; }
        ArchiveProjectInput Input(int side) => new() { RootPath = roots[side], RootSha256 = hashes[side], EntryChain = [], LeafEntry = "leaf.bin", InheritedReadOnly = false };
        void Respond(long start, string hex, long count) => BinaryRangeDialog.Shown = dialog => { dialog.Offset.Text = start.ToString(System.Globalization.CultureInfo.InvariantCulture); dialog.Count.Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture); dialog.Hex.Text = hex; Accept(dialog); };
        void Report(string name, bool pass) { try { check("Binary Range " + name, pass, "real controls and full bytes"); } catch (InvalidOperationException) { } }
    }
    private static void Edit(SpecializedViews.BinaryPanel panel, int side, BinaryRangeKind kind, long start, string hex, long count = 0) => panel.EditRange(side, new(kind, start, count, hex));
    private static byte[][] Bytes(SpecializedViews.BinaryPanel panel) => panel.ProjectSides.Select(side => panel.CaptureApplied(side).CopyBytes()).ToArray();
    private static bool Equal(SpecializedViews.BinaryPanel panel, byte[][] expected) => Bytes(panel).Zip(expected).All(pair => pair.First.SequenceEqual(pair.Second));
    private static SpecializedViews.BinaryPanel Binary(ComparisonPane pane) => pane.GetVisualDescendants().OfType<SpecializedViews.BinaryPanel>().Single();
    private static void Click(SpecializedViews.BinaryPanel panel, int side, BinaryRangeKind kind) { panel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, (side switch { 0 => "左", 1 => "中央", _ => "右" }) + " " + BinaryRangeDialog.Label(kind))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    private static void Accept(BinaryRangeDialog dialog) => dialog.Accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static bool Refused(Action action) { try { action(); return false; } catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException or FormatException or OperationCanceledException or ObjectDisposedException) { return true; } }
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
}
