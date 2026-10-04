using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessBinaryClipboardChecks
{
    internal static void Run(MainWindow window, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "binary-clipboard"); Directory.CreateDirectory(folder);
        var original = Enumerable.Range(0, 3).Select(side => Enumerable.Range(side * 10, 8).Select(value => (byte)value).ToArray()).ToArray();
        var files = new[] { "left.bin", "middle.bin", "right.bin" }.Select(name => Path.Combine(folder, name)).ToArray();
        var roots = new[] { "left.zip", "middle.zip", "right.zip" }.Select(name => Path.Combine(folder, name)).ToArray();
        for (var side = 0; side < 3; side++) { File.WriteAllBytes(files[side], original[side]); File.WriteAllBytes(roots[side], HeadlessBinaryWorkingChecks.Zip(("leaf.bin", original[side]))); }
        var hashes = roots.Select(Hash).ToArray(); var normalWorkspace = ""; var normalPackage = ""; var archiveWorkspace = ""; var archivePackage = "";
        try
        {
            foreach (var three in new[] { true, false })
            {
                var pane = Add(three); var panel = Binary(pane); var clipboard = new MemoryClipboard(); panel.ClipboardOverride = clipboard;
                foreach (var command in Enum.GetValues<BinaryClipboardCommand>())
                {
                    BinaryClipboardDialog.Shown = dialog => dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    pump(panel.ClipboardAsync(0, command)); Report("dialog cancel " + three + "/" + command, Bytes(panel, 0).SequenceEqual(original[0]) && !panel.Session.CanUndo && clipboard.Writes == 0);
                }
                Respond(dialog => { dialog.Start.Text = "0x1"; dialog.End.Text = "0x2"; }); Click(panel, 0, BinaryClipboardCommand.Select);
                Report("selection dialog inclusive hex offsets " + three, panel.Selection(0).Start == 1 && panel.Selection(0).Count == 2);
                foreach (var side in panel.ProjectSides)
                {
                    panel.SelectBytes(side, 1, 2); panel.Editor(side).Focus(); window.KeyTextInput("a5"); Dispatcher.UIThread.RunJobs();
                    var nibble = new[] { original[side][0], (byte)0xa5 }.Concat(original[side].Skip(3)).ToArray();
                    Report($"{three}/{side} real Hex nibbles selected replacement", Bytes(panel, side).SequenceEqual(nibble)); panel.Undo(); panel.Undo();
                    panel.SelectBytes(side, 1, 2, true, true); panel.Editor(side).Focus(); window.KeyTextInput("c"); Dispatcher.UIThread.RunJobs();
                    Report($"{three}/{side} low nibble selected replacement", Bytes(panel, side).SequenceEqual(new[] { original[side][0], (byte)12 }.Concat(original[side].Skip(3)))); panel.Undo();
                    panel.SelectBytes(side, 8, 8, false, true); panel.Editor(side).Focus(); window.KeyTextInput("f1"); Dispatcher.UIThread.RunJobs();
                    Report($"{three}/{side} EOF overwrite appends", Bytes(panel, side).SequenceEqual(original[side].Concat(new byte[] { 0xf1 }))); panel.Undo(); panel.Undo();
                    panel.SelectBytes(side, 0, 0, false); panel.AsciiEditor(side).Focus(); window.KeyTextInput("Z"); Dispatcher.UIThread.RunJobs();
                    Report($"{three}/{side} real ASCII input", Bytes(panel, side).SequenceEqual(new byte[] { 90 }.Concat(original[side].Skip(1))));
                    Stroke(Key.Z, PhysicalKey.Z, RawInputModifiers.Control);
                    Report($"{three}/{side} Control Z undo", Bytes(panel, side).SequenceEqual(original[side]));
                    Stroke(Key.Z, PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
                    Report($"{three}/{side} Control Shift Z redo", Bytes(panel, side).SequenceEqual(new byte[] { 90 }.Concat(original[side].Skip(1))));
                    Stroke(Key.Z, PhysicalKey.Z, RawInputModifiers.Meta);
                    Stroke(Key.Z, PhysicalKey.Z, RawInputModifiers.Meta | RawInputModifiers.Shift);
                    Report($"{three}/{side} Meta Shift Z redo", Bytes(panel, side).SequenceEqual(new byte[] { 90 }.Concat(original[side].Skip(1)))); panel.Undo();
                    var initialWrites = clipboard.Writes;
                    panel.SelectBytes(side, 1, 2); Respond(dialog => { dialog.Start.Text = "1"; dialog.End.Text = "2"; }); Click(panel, side, BinaryClipboardCommand.Copy);
                    Report($"{three}/{side} Copy exact bytes and bytecode", clipboard.Bytes.SequenceEqual(original[side].Skip(1).Take(2)) && clipboard.Text == BinaryBytecode.Encode(original[side].AsSpan(1, 2)));
                    Respond(dialog => { dialog.Start.Text = "1"; dialog.UseCount.IsChecked = true; dialog.Count.Text = "2"; }); Click(panel, side, BinaryClipboardCommand.Cut);
                    Report($"{three}/{side} Cut after publication", Bytes(panel, side).SequenceEqual(original[side].Take(1).Concat(original[side].Skip(3))) && clipboard.Writes == initialWrites + 2); panel.Undo();
                    panel.SelectBytes(side, 2, 1); Respond(Paste); Click(panel, side, BinaryClipboardCommand.Paste);
                    Report($"{three}/{side} repeat skip selected replacement", Bytes(panel, side).SequenceEqual(Expected(side)));
                    pump(panel.SaveToAsync(side, Path.Combine(folder, (three ? "three" : "two") + "-" + side + ".bin")));
                    Report($"{three}/{side} savepoint and shared undo redo", !panel.Dirty(side) && panel.Undo() && panel.Dirty(side) && panel.Redo() && !panel.Dirty(side));
                }
                if (three)
                {
                    normalWorkspace = Path.Combine(folder, "normal.json"); normalPackage = Path.Combine(folder, "normal.zip"); var project = pane.CaptureProject();
                    pump(WorkspaceStore.SaveWorkspaceAsync(normalWorkspace, new() { Entries = [project] })); pump(ComparisonPackage.CreateAsync(new() { Entries = [project] }, normalPackage, new(true, false, false, true)));
                    window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs(); screenshot("binary-clipboard-normal.png");
                    // 実pointerからTextBoxの文字位置を経て、byte inclusive選択へ変換する。
                    panel.SelectBytes(0, 0, 0, false); panel.Editor(0).Focus(); Dispatcher.UIThread.RunJobs();
                    var presenter = panel.Editor(0).GetVisualDescendants().OfType<TextPresenter>().Single(); var a = presenter.TextLayout.HitTestTextPosition(3); var b = presenter.TextLayout.HitTestTextPosition(9);
                    var first = presenter.TranslatePoint(new Point(a.X + .1, a.Y + a.Height / 2), window)!.Value; var last = presenter.TranslatePoint(new Point(b.X + .1, b.Y + b.Height / 2), window)!.Value;
                    window.MouseDown(first, MouseButton.Left); window.MouseMove(last, RawInputModifiers.LeftMouseButton); window.MouseUp(last, MouseButton.Left); Dispatcher.UIThread.RunJobs();
                    Report("real pointer byte selection", panel.Selection(0).Start == 1 && panel.Selection(0).Count == 2);
                    screenshot("binary-clipboard-pointer-selection.png");
                }
                pane.DiscardChanges();
            }
            foreach (var change in new[] { "selection", "revision", "draft", "readonly", "path", "mode", "recompare", "cancel", "dispose" })
            {
                var pane = Add(true); var panel = Binary(pane); var clipboard = new MemoryClipboard(); panel.ClipboardOverride = clipboard; panel.SelectBytes(1, 1, 2); var before = Bytes(panel, 1);
                Respond(_ => { }); clipboard.AfterWrite = () =>
                {
                    if (change == "selection") panel.SelectBytes(1, 0, 0);
                    if (change == "revision") { panel.Session.ReplaceRange(0, 0, 1, [99]); panel.Session.Undo(); }
                    if (change == "draft") panel.RightHex.Text = "GG";
                    if (change == "readonly") panel.MiddleReadOnly = true;
                    if (change == "path") pane.LeftPath.Text = files[0] + ".changed";
                    if (change == "mode") pane.SelectMode(1);
                    if (change == "recompare") pump(pane.ComparePathsAsync());
                    if (change == "cancel") pane.GetVisualDescendants().OfType<Button>().Single(value => Equals(value.Content, "中止")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (change == "dispose")
                    {
                        var tab = window.GetVisualDescendants().OfType<TabControl>().SelectMany(control => control.Items.OfType<TabItem>()).Single(item => ReferenceEquals(item.Content, pane));
                        ((StackPanel)tab.Header!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                };
                Report("Cut stale " + change, Refused(() => pump(panel.ClipboardAsync(1, BinaryClipboardCommand.Cut))) && (panel.IsDisposed || Bytes(panel, 1).SequenceEqual(before)));
                if (!panel.IsDisposed) { Report("Cut stale preserves draft " + change, change != "draft" || panel.RightHex.Text == "GG"); pane.DiscardChanges(); }
            }
            foreach (var fail in new[] { "publish", "flush" })
            {
                var pane = Add(false); var panel = Binary(pane); var platform = new PlatformClipboard { FailSet = fail == "publish", FailFlush = fail == "flush" }; panel.ClipboardOverride = BinaryClipboard.ForPlatform(() => platform); panel.SelectBytes(2, 1, 2); Respond(_ => { }); var stamp = panel.StateStamp;
                Report("Cut " + fail + " failure keeps bytes/history/savepoint", Refused(() => pump(panel.ClipboardAsync(2, BinaryClipboardCommand.Cut))) && Bytes(panel, 2).SequenceEqual(original[2]) && !panel.Session.CanUndo && !panel.Dirty(2) && panel.StateStamp == stamp);
            }
            {
                var pane = Add(true); var panel = Binary(pane); var clipboard = new MemoryClipboard(); panel.ClipboardOverride = clipboard;
                clipboard.Formats = [new("CF_TEXT", BinaryClipboardKind.Ansi, Encoding.ASCII.GetBytes("41 00\0tail")), new("raw-test", BinaryClipboardKind.Raw, [0, 255])];
                panel.SelectBytes(1, 0, 1); Respond(dialog => { dialog.Formats.SelectedIndex = 0; }); Click(panel, 1, BinaryClipboardCommand.FastPaste);
                Report("FastPaste bytecode is literal text", Bytes(panel, 1).SequenceEqual("41 00"u8.ToArray().Concat(original[1].Skip(2)))); panel.Undo();
                panel.SelectBytes(1, 0, 1); Respond(dialog => { dialog.Formats.SelectedIndex = 1; }); Click(panel, 1, BinaryClipboardCommand.FastPaste);
                Report("FastPaste arbitrary raw format includes NUL", Bytes(panel, 1).AsSpan(0, 2).SequenceEqual(new byte[] { 0, 255 })); panel.Undo();
                clipboard.Formats = [new("CF_TEXT", BinaryClipboardKind.Ansi, [0xe9, 0])];
                panel.SelectBytes(1, 0, 1); Respond(dialog => { dialog.Formats.SelectedIndex = 0; dialog.CharacterSet.SelectedIndex = 1; }); Click(panel, 1, BinaryClipboardCommand.FastPaste);
                File.WriteAllBytes(Path.Combine(folder, "oem-ui.bin"), Bytes(panel, 1));
                Report("FastPaste OEM literal real format dialog", Bytes(panel, 1).Length == 7); panel.Undo();
                clipboard.Formats = [new("CF_TEXT", BinaryClipboardKind.Ansi, [0x81])];
                panel.SelectBytes(1, 0, 0, false); Respond(dialog => { Report("unterminated ANSI prefill remains editable dialog", !string.IsNullOrEmpty(dialog.Status.Text) && string.IsNullOrEmpty(dialog.Text.Text)); dialog.Text.Text = "A"; }); Click(panel, 1, BinaryClipboardCommand.Paste);
                Report("invalid ANSI prefill manual correction", Bytes(panel, 1).SequenceEqual(new byte[] { 65 }.Concat(original[1].Skip(1)))); panel.Undo(); clipboard.Formats = [];
                panel.SelectBytes(1, 0, 0, false); Respond(dialog => { dialog.Text.Text = "<wh:1234>"; dialog.BigEndian.IsChecked = true; }); Click(panel, 1, BinaryClipboardCommand.Paste);
                Report("Paste endian exact", Bytes(panel, 1).AsSpan(0, 2).SequenceEqual(new byte[] { 0x12, 0x34 })); panel.Undo();
                panel.SelectBytes(1, 0, 0, false); Respond(dialog => { dialog.Text.Text = "<bh:ff>"; dialog.AsText.IsChecked = true; }); Click(panel, 1, BinaryClipboardCommand.Paste);
                Report("Paste AsText bypasses tokens", Bytes(panel, 1).AsSpan(0, 7).SequenceEqual("<bh:ff>"u8)); panel.Undo();
                foreach (var invalid in new[] { "", "<bd:->", "<ld:2147483648>", "<bh:" + new string('1', 50) + ">", "<fd:1>", "<fl:1e999>" })
                {
                    panel.SelectBytes(1, 0, 1); var before = Bytes(panel, 1);
                    BinaryClipboardDialog.Shown = dialog => { dialog.Text.Text = invalid; dialog.Accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Report("invalid input stays in dialog " + invalid, dialog.IsVisible && !string.IsNullOrEmpty(dialog.Status.Text)); dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
                    pump(panel.ClipboardAsync(1, BinaryClipboardCommand.Paste)); Report("invalid payload preserves selection and history " + invalid, Bytes(panel, 1).SequenceEqual(before) && panel.Selection(1).Count == 2);
                }
                panel.MiddleReadOnly = true; var reads = clipboard.Reads; Report("readonly before read", Refused(() => pump(panel.ClipboardAsync(1, BinaryClipboardCommand.FastPaste))) && clipboard.Reads == reads);
                panel.MiddleReadOnly = false; panel.RightHex.Text = "GG"; Report("unapplied Hex before read", Refused(() => pump(panel.ClipboardAsync(1, BinaryClipboardCommand.Paste))) && clipboard.Reads == reads); pane.DiscardChanges();
            }
            {
                var longPath = Path.Combine(folder, "boundary.bin"); File.WriteAllBytes(longPath, Enumerable.Range(0, 8193).Select(value => (byte)value).ToArray());
                var pane = window.AddSession(); pane.ApplyProject(new() { Mode = "Binary", LeftPath = longPath, RightPath = longPath }); pump(pane.ComparePathsAsync()); var panel = Binary(pane);
                panel.SelectBytes(0, 4095, 4095, false); panel.Editor(0).Focus(); Stroke(Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.Shift);
                Report("real key 4096 page crossing inclusive selection", panel.Selection(0).Start == 4095 && panel.Selection(0).Count == 2);
                panel.SelectBytes(0, 3, 3, false); panel.Selection(0).Insert = true; panel.Editor(0).Focus(); Stroke(Key.Back, PhysicalKey.Backspace); Report("Backspace insert removes previous byte", Bytes(panel, 0).AsSpan(0, 5).SequenceEqual(new byte[] { 0, 1, 3, 4, 5 })); panel.Undo();
                panel.Selection(0).Insert = false; panel.SelectBytes(0, 3, 3, false); var revision = panel.Session.Revision(0); Stroke(Key.Back, PhysicalKey.Backspace); Report("Backspace overwrite moves only", panel.Session.Revision(0) == revision && panel.Selection(0).Caret == 2);
                Stroke(Key.Delete, PhysicalKey.Delete); Report("Delete closes byte gap", Bytes(panel, 0).AsSpan(0, 5).SequenceEqual(new byte[] { 0, 1, 3, 4, 5 })); panel.Undo(); pane.DiscardChanges();
            }
            {
                var repeated = string.Concat(Enumerable.Repeat("<bh:", 262144)); var watch = System.Diagnostics.Stopwatch.StartNew();
                var pane = Add(false); var panel = Binary(pane); panel.ClipboardOverride = new MemoryClipboard(); panel.SelectBytes(0, 0, 7);
                Respond(dialog => dialog.Text.Text = repeated); Click(panel, 0, BinaryClipboardCommand.Paste); watch.Stop();
                Report("unclosed repeated token real Paste linear output", Bytes(panel, 0).SequenceEqual(Encoding.ASCII.GetBytes(repeated))); panel.Undo();
                var before = Bytes(panel, 0); var revision = panel.Session.Revision(0); panel.SelectBytes(0, 0, 7);
                BinaryClipboardDialog.Shown = dialog => { dialog.Text.Text = "<bh:" + new string('1', 1048576); dialog.Accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Report("long numeric prefix bounded rejection", dialog.IsVisible && !string.IsNullOrEmpty(dialog.Status.Text)); dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
                pump(panel.ClipboardAsync(0, BinaryClipboardCommand.Paste)); Report("long prefix retains bytes and history", Bytes(panel, 0).SequenceEqual(before) && panel.Session.Revision(0) == revision);
                File.WriteAllBytes(Path.Combine(folder, "unclosed-repeat.bin"), Encoding.ASCII.GetBytes(repeated));
                File.WriteAllText(Path.Combine(folder, "processing-boundary.json"), $"{{\"inputBytes\":{repeated.Length},\"elapsedMilliseconds\":{watch.ElapsedMilliseconds},\"maximumTokenLookahead\":54,\"passes\":2,\"longNumericPrefixRejected\":true}}"); pane.DiscardChanges();
            }
            {
                // 16MiB全NULの公開はraw16MiBとbytecode112MiB（Unicode224MiB）になる。
                var raw = BinaryClipboard.EncodeRaw(new byte[BinaryEditSession.MaximumFileBytes]); var text = string.Create(BinaryBytecode.MaximumTextBytes, 0, (span, _) => { for (var at = 0; at < span.Length; at += 7) "<bh:00>".AsSpan().CopyTo(span[at..]); });
                foreach (var reverse in new[] { false, true })
                {
                    var item = new DataTransferItem(); if (reverse) { item.SetText(text); item.Set(BinaryClipboard.RawFormat, raw); } else { item.Set(BinaryClipboard.RawFormat, raw); item.SetText(text); }
                    var platform = new PlatformClipboard { Input = new ReadTransfer(item) }; var result = BinaryClipboard.ForPlatform(() => platform).ReadAsync(default); pump(result);
                    Report("maximum self formats platform order " + reverse, result.Result.Count == 2 && result.Result.Single(value => value.Kind == BinaryClipboardKind.Raw).Bytes.Length == BinaryEditSession.MaximumFileBytes && result.Result.Single(value => value.Kind == BinaryClipboardKind.PlatformText).Text!.Length == BinaryBytecode.MaximumTextBytes);
                }
                foreach (var oem in new[] { false, true }) foreach (var reverse in new[] { false, true })
                {
                    var formats = new List<(BinaryClipboardKind Kind, long Size, bool Owned)> { (BinaryClipboardKind.Raw, raw.Length, true), (BinaryClipboardKind.Ansi, text.Length + 1L, false), (BinaryClipboardKind.Unicode, text.Length * 2L + 2, false) }; if (oem) formats.Add((BinaryClipboardKind.Oem, text.Length + 1L, false)); if (reverse) formats.Reverse();
                    var budget = new BinaryClipboardReadBudget(); foreach (var format in formats) budget.Add(format.Size + BinaryClipboardReadBudget.AllocatorPadding, format.Kind, format.Owned);
                    Report($"maximum Windows format budget OEM={oem} reverse={reverse}", budget.Total <= BinaryClipboardReadBudget.MaximumTotal);
                    var total = budget.Total; Report($"format budget boundary rejects before mutation {oem}/{reverse}", Refused(() => budget.Add(BinaryClipboardReadBudget.Maximum(BinaryClipboardKind.Unicode), BinaryClipboardKind.Unicode)) && budget.Total == total);
                }
                var pane = Add(false); var panel = Binary(pane); panel.SelectBytes(0, 0, 1); var stamp = panel.StateStamp; var itemTooLarge = new DataTransferItem(); itemTooLarge.Set(DataFormat.CreateBytesApplicationFormat("too-large"), raw); panel.ClipboardOverride = BinaryClipboard.ForPlatform(() => new PlatformClipboard { Input = new ReadTransfer(itemTooLarge) });
                Report("unknown raw over limit retains bytes and history", Refused(() => pump(panel.ClipboardAsync(0, BinaryClipboardCommand.Paste))) && panel.StateStamp == stamp && !panel.Session.CanUndo && Bytes(panel, 0).SequenceEqual(original[0])); pane.DiscardChanges();
                File.WriteAllText(Path.Combine(folder, "maximum-format-budget.json"), $"{{\"rawBytes\":{raw.Length},\"textCharacters\":{text.Length},\"totalLimit\":{BinaryClipboardReadBudget.MaximumTotal},\"platformOrders\":2,\"windowsOrders\":4,\"oemPresentAndAbsent\":true,\"osClipboardUsed\":false}}");
            }
            using (var session = new BinaryEditSession([0, 1, 2, 3, 4, 5, 6, 7], []))
            {
                var candidate = session.PreparePaste(0, 1, 0, [0xaa, 0xbb], true, 2, 1); session.CommitPrepared(candidate);
                Report("insert repeat crosses original suffix", session.Capture(0).CopyBytes().SequenceEqual(new byte[] { 0, 0xaa, 0xbb, 1, 0xaa, 0xbb, 2, 3, 4, 5, 6, 7 })); session.Undo();
                session.CommitPrepared(session.PreparePaste(0, 1, 0, [0xaa, 0xbb], false, 2, 1)); Report("overwrite repeat preserves skipped bytes", session.Capture(0).CopyBytes().SequenceEqual(new byte[] { 0, 0xaa, 0xbb, 3, 0xaa, 0xbb, 6, 7 })); session.Undo();
                Report("last skip capacity refusal with Redo retained", Refused(() => session.PreparePaste(0, 3, 0, [0xaa, 0xbb], false, 2, 1)) && session.CanRedo);
                Report("EOF no zero padding", Refused(() => session.PreparePaste(0, 8, 0, [1], true, 2, 1)) && session.Length(0) == 8);
                Report("repeat overflow refusal", Refused(() => session.PreparePaste(0, 0, 0, [1], true, long.MaxValue, long.MaxValue)) && session.CanRedo);
            }
            {
                var raw = BinaryClipboard.EncodeRaw([0, 65, 255]);
                Report("raw ownership length trims HGLOBAL zero padding", BinaryClipboard.DecodeRaw(raw.Concat(new byte[5]).ToArray()).SequenceEqual(new byte[] { 0, 65, 255 }));
                Report("raw corrupt header rejected", Refused(() => BinaryClipboard.DecodeRaw([0, 0, 0, 0, 0, 0, 0, 0])));
                Report("raw nonzero suffix rejected", Refused(() => BinaryClipboard.DecodeRaw(raw.Concat(new byte[] { 1 }).ToArray())));
                Report("CF_TEXT missing NUL rejected", Refused(() => new BinaryClipboardValue("CF_TEXT", BinaryClipboardKind.Ansi, [65]).Decode(true, false, false)));
                Report("CF_UNICODETEXT odd or missing NUL rejected", Refused(() => new BinaryClipboardValue("CF_UNICODETEXT", BinaryClipboardKind.Unicode, [65, 0, 66]).Decode(true, false, false)) && Refused(() => new BinaryClipboardValue("CF_UNICODETEXT", BinaryClipboardKind.Unicode, [65, 0]).Decode(true, false, false)));
                Report("CF_UNICODETEXT AsText exact UTF16", new BinaryClipboardValue("CF_UNICODETEXT", BinaryClipboardKind.Unicode, [65, 0, 66, 0, 0, 0, 88, 0]).Decode(true, false, false).SequenceEqual(new byte[] { 65, 0, 66, 0 }));
                Report("macOS standard AsText canonical UTF16", new BinaryClipboardValue("text", BinaryClipboardKind.PlatformText, [], "Aé").Decode(true, false, false).SequenceEqual(new byte[] { 65, 0, 233, 0 }));
            }
            using (var session = new BinaryEditSession(new byte[BinaryEditSession.MaximumFileBytes], []))
            {
                var block = Enumerable.Repeat((byte)170, BinaryEditSession.MaximumFileBytes).ToArray();
                session.ReplaceRange(0, 0, block.Length, block); session.ReplaceRange(0, 0, block.Length, []); session.ReplaceRange(0, 0, 0, block);
                var revision = session.Revision(0); Report("64MiB Cut candidate rejection is atomic", Refused(() => session.PrepareReplaceRange(0, 0, block.Length, [])) && session.Revision(0) == revision && !session.CanRedo);
                session.Undo(); Report("64MiB rejection preserves Redo", session.CanRedo && session.Length(0) == 0); session.Redo();
                File.WriteAllBytes(Path.Combine(folder, "history64-redo.bin"), session.Capture(0).CopyBytes());
                Report("16MiB exact Redo full bytes", session.Capture(0).CopyBytes().SequenceEqual(block));
                Report("16MiB paste overflow preserves history", Refused(() => session.PreparePaste(0, 0, 0, [1], true)) && session.Revision(0) == revision + 2);
            }
            {
                var pane = Add(false); var panel = Binary(pane); var clipboard = new MemoryClipboard(); panel.ClipboardOverride = clipboard;
                for (var step = 0; step < BinaryEditSession.MaximumHistoryActions; step++) panel.EditRange(0, new(BinaryRangeKind.Insert, 0, 0, "01"));
                panel.SelectBytes(0, 0, 0); Respond(_ => { });
                Report("Cut history capacity rejects before publication", Refused(() => pump(panel.ClipboardAsync(0, BinaryClipboardCommand.Cut))) && clipboard.Writes == 0 && panel.Session.Length(0) == 264);
                panel.Undo(); Report("Cut rejection preserves usable Redo", panel.Session.CanRedo); pane.DiscardChanges();
            }
            {
                var pane = window.AddSession(); pane.ApplyProject(new() { Mode = "Binary", LeftReadOnly = true, BaseReadOnly = true, RightReadOnly = true, LeftArchiveInput = Input(0), BaseArchiveInput = Input(1), RightArchiveInput = Input(2) }); pump(pane.ComparePathsAsync()); var panel = Binary(pane); panel.ClipboardOverride = new MemoryClipboard();
                foreach (var side in panel.ProjectSides) { panel.SelectBytes(side, 1, 2); Respond(Paste); Click(panel, side, BinaryClipboardCommand.Paste); pump(panel.SaveAsync(side)); Report("archive working side " + side, !panel.Dirty(side) && Bytes(panel, side).SequenceEqual(Expected(side))); }
                var project = pane.CaptureProject(); archiveWorkspace = Path.Combine(folder, "archive.json"); archivePackage = Path.Combine(folder, "archive.zip");
                pump(WorkspaceStore.SaveWorkspaceAsync(archiveWorkspace, new() { Entries = [project] })); pump(ComparisonPackage.CreateAsync(new() { Entries = [project] }, archivePackage, new(true, false, false, true)));
                var loaded = window.AddSession(); var restoration = WorkspaceStore.LoadAsync(archiveWorkspace); pump(restoration); loaded.ApplyProject(restoration.Result); pump(loaded.ComparePathsAsync());
                Report("archive restored all working sides", Binary(loaded).ProjectSides.All(side => Bytes(Binary(loaded), side).SequenceEqual(Expected(side))));
                while (window.SessionPanes.Count < 45) window.AddSession();
                var layoutPane = window.AddSession(); layoutPane.ApplyProject(new() { Mode = "Binary", LeftPath = files[0], BasePath = files[1], RightPath = files[2] }); pump(layoutPane.ComparePathsAsync()); var layout = Binary(layoutPane);
                window.Width = 850; window.Height = 550; Dispatcher.UIThread.RunJobs(); var content = (Control)layout.Viewport.Content!;
                layout.Viewport.Offset = new Vector(0, layout.MiddleHex.TranslatePoint(default, content)!.Value.Y); Dispatcher.UIThread.RunJobs(); screenshot("binary-clipboard-minimum-45tabs.png");
                Report("minimum Hex and ASCII dimensions", layout.ProjectSides.All(side => layout.Editor(side).Bounds.Height >= 100 && layout.AsciiEditor(side).Bounds.Height >= 40));
                var hexY = layout.MiddleHex.TranslatePoint(default, layout.Viewport)!.Value.Y; Report("minimum Hex first text line visible", hexY >= -1 && hexY + 24 <= layout.Viewport.Bounds.Height);
                layout.Viewport.Offset = new Vector(0, layout.AsciiEditor(1).TranslatePoint(default, content)!.Value.Y); Dispatcher.UIThread.RunJobs(); var asciiY = layout.AsciiEditor(1).TranslatePoint(default, layout.Viewport)!.Value.Y; Report("minimum ASCII first text line visible", asciiY >= -1 && asciiY + 24 <= layout.Viewport.Bounds.Height); screenshot("binary-clipboard-minimum-ascii.png");
                var save = layout.GetVisualDescendants().OfType<Button>().Single(value => Equals(value.Content, "中央を別名保存")); layout.Viewport.Offset = new Vector(0, save.TranslatePoint(default, content)!.Value.Y); Dispatcher.UIThread.RunJobs(); screenshot("binary-clipboard-minimum-save.png");
                var position = save.TranslatePoint(default, layout.Viewport)!.Value; Report("minimum save reachable after scroll", position.Y >= -1 && position.Y + save.Bounds.Height <= layout.Viewport.Bounds.Height + 1);
                File.WriteAllText(Path.Combine(folder, "layout.json"), $"{{\"tabs\":{window.SessionPanes.Count},\"hexHeight\":{layout.MiddleHex.Bounds.Height},\"asciiHeight\":{layout.AsciiEditor(1).Bounds.Height},\"hexY\":{hexY},\"asciiY\":{asciiY},\"saveY\":{position.Y},\"viewportHeight\":{layout.Viewport.Bounds.Height}}}");
            }
            using var factsFile = File.Create(Path.Combine(folder, "facts.json")); using var facts = new Utf8JsonWriter(factsFile); facts.WriteStartObject(); facts.WriteString("normalWorkspace", normalWorkspace); facts.WriteString("normalPackage", normalPackage); facts.WriteString("archiveWorkspace", archiveWorkspace); facts.WriteString("archivePackage", archivePackage); facts.WriteStartArray("rootSha256"); foreach (var hash in hashes) facts.WriteStringValue(hash); facts.WriteEndArray(); facts.WriteEndObject();
        }
        finally { BinaryClipboardDialog.Shown = null; foreach (var pane in window.SessionPanes) pane.DiscardChanges(); }
        ComparisonPane Add(bool three) { var pane = window.AddSession(); pane.ApplyProject(new() { Mode = "Binary", LeftPath = files[0], BasePath = three ? files[1] : "", RightPath = files[2] }); pump(pane.ComparePathsAsync()); return pane; }
        ArchiveProjectInput Input(int side) => new() { RootPath = roots[side], RootSha256 = hashes[side], EntryChain = [], LeafEntry = "leaf.bin", InheritedReadOnly = false };
        byte[] Expected(int side) => new[] { original[side][0], (byte)65, (byte)0, original[side][3], (byte)65, (byte)0 }.Concat(original[side].Skip(4)).ToArray();
        void Paste(BinaryClipboardDialog dialog) { dialog.Text.Text = "A<bh:00>"; dialog.Repeat.Text = "2"; dialog.Skip.Text = "1"; }
        void Respond(Action<BinaryClipboardDialog> action) => BinaryClipboardDialog.Shown = dialog => { action(dialog); dialog.Accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        void Click(SpecializedViews.BinaryPanel panel, int side, BinaryClipboardCommand command) { var name = side switch { 0 => "左", 1 => "中央", _ => "右" }; panel.GetVisualDescendants().OfType<Button>().Single(value => Equals(value.Content, name + " " + BinaryClipboardDialog.Label(command))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); if (panel.CurrentClipboardOperation is { } operation) pump(operation); Dispatcher.UIThread.RunJobs(); }
        void Stroke(Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None) { window.KeyPress(key, modifiers, physical, null); window.KeyRelease(key, modifiers, physical, null); Dispatcher.UIThread.RunJobs(); }
        void Report(string name, bool passed) { try { check("Binary clipboard " + name, passed, "real Avalonia controls/dialogs/keys/pointer + full bytes; OS clipboard untouched"); } catch (InvalidOperationException) { } }
    }
    private static byte[] Bytes(SpecializedViews.BinaryPanel panel, int side) => panel.CaptureApplied(side).CopyBytes();
    private static SpecializedViews.BinaryPanel Binary(ComparisonPane pane) => pane.GetVisualDescendants().OfType<SpecializedViews.BinaryPanel>().Single();
    private static bool Refused(Action action) { try { action(); return false; } catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException or FormatException or OperationCanceledException or IOException) { return true; } }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed class MemoryClipboard : IBinaryClipboard
    {
        internal byte[] Bytes = []; internal string Text = ""; internal int Writes, Reads; internal Action? AfterWrite;
        internal IReadOnlyList<BinaryClipboardValue> Formats = [];
        public Task WriteAsync(byte[] bytes, string text, CancellationToken token) { token.ThrowIfCancellationRequested(); Writes++; Bytes = bytes.ToArray(); Text = text; AfterWrite?.Invoke(); return Task.CompletedTask; }
        public Task<IReadOnlyList<BinaryClipboardValue>> ReadAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Reads++; return Task.FromResult(Formats); }
    }
    private sealed class PlatformClipboard : IImageClipboardPlatform
    {
        internal bool FailSet, FailFlush;
        internal IAsyncDataTransfer? Input;
        public Task SetDataAsync(IAsyncDataTransfer transfer) { if (FailSet) throw new IOException("injected publication failure"); return Task.CompletedTask; }
        public Task FlushAsync() { if (FailFlush) throw new IOException("injected flush failure"); return Task.CompletedTask; }
        public Task<IAsyncDataTransfer?> TryGetDataAsync() => Task.FromResult(Input);
    }
    private sealed class ReadTransfer(DataTransferItem item) : IAsyncDataTransfer
    {
        private DataTransferItem[] items = [item];
        public IReadOnlyList<DataFormat> Formats => items.Length == 0 ? [] : items[0].Formats;
        public IReadOnlyList<IAsyncDataTransferItem> Items => items;
        public void Dispose() => items = [];
    }
}
