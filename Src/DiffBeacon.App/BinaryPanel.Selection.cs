using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public static partial class SpecializedViews
{
    public sealed partial class BinaryPanel
    {
        internal sealed class ByteSelection
        {
            internal int Anchor, Caret;
            internal bool Selected, LowNibble, Insert, Oem;
            internal int Start => Selected ? Math.Min(Anchor, Caret) : Caret;
            internal int Count => Selected ? Math.Abs(Anchor - Caret) + 1 : 0;
        }
        private readonly ByteSelection[] _selection = [new(), new(), new()];
        private readonly TextBox[] _ascii = [AsciiBox(), AsciiBox(), AsciiBox()];
        private static TextBox AsciiBox() => new() { FontFamily = Mono, AcceptsReturn = true, Height = 52, PlaceholderText = "文字入力", TextWrapping = Avalonia.Media.TextWrapping.NoWrap };
        internal TextBox AsciiEditor(int side) { _ = LocalSide(side); return _ascii[side]; }
        internal ByteSelection Selection(int side) { _ = LocalSide(side); return _selection[side]; }
        internal long SelectionVersion { get; private set; }
        internal Func<int, BinaryClipboardCommand, Task>? ClipboardContent { get; set; }
        internal Func<bool>? EditingCurrent { get; set; }
        internal IBinaryClipboard? ClipboardOverride { get; set; }
        internal Task? CurrentClipboardOperation { get; private set; }
        internal Task ClipboardAsync(int side, BinaryClipboardCommand command)
        { ThrowDisposed(); _ = LocalSide(side); ApplyReadOnly?.Invoke(); if (command is BinaryClipboardCommand.Cut or BinaryClipboardCommand.Paste or BinaryClipboardCommand.FastPaste && ReadOnly(side)) throw new InvalidOperationException("この側は読取り専用です。"); EnsureApplied(); return CurrentClipboardOperation = ClipboardContent?.Invoke(side, command) ?? throw new InvalidOperationException("比較元がありません。"); }
        internal void SelectBytes(int side, int anchor, int caret, bool selected = true, bool low = false)
        {
            ThrowDisposed(); EnsureApplied(); var length = Session.Length(LocalSide(side));
            if (anchor < 0 || caret < 0 || anchor > length || caret > length || selected && (anchor >= length || caret >= length)) throw new ArgumentOutOfRangeException(nameof(caret));
            var selection = Selection(side); selection.Anchor = anchor; selection.Caret = caret; selection.Selected = selected; selection.LowNibble = low; SelectionVersion++;
            RevealCaret(side); Render();
        }
        private void InitializeByteEditing()
        {
            foreach (var side in ProjectSides)
            {
                Bind(Editor(side), false); Bind(AsciiEditor(side), true);
                void Bind(TextBox editor, bool ascii)
                {
                    editor.AddHandler(InputElement.TextInputEvent, (_, args) =>
                    {
                        args.Handled = true;
                        _ = Run(() => { if (args.Text is { Length: > 0 }) InputBytes(side, args.Text, ascii); });
                    }, RoutingStrategies.Tunnel);
                    editor.AddHandler(InputElement.KeyDownEvent, (_, args) =>
                    {
                        if (HandleByteKey(side, args, ascii)) args.Handled = true;
                    }, RoutingStrategies.Tunnel);
                    editor.PropertyChanged += (_, args) =>
                    {
                        if (_updating || Pending(side) || args.Property != TextBox.CaretIndexProperty && args.Property != TextBox.SelectionStartProperty && args.Property != TextBox.SelectionEndProperty) return;
                        var length = Session.Length(LocalSide(side)); var selection = Selection(side);
                        var caret = Math.Min(length, _pageStart + Position(editor.CaretIndex, ascii));
                        var start = Math.Min(editor.SelectionStart, editor.SelectionEnd); var end = Math.Max(editor.SelectionStart, editor.SelectionEnd);
                        if (end > start && length != 0)
                        {
                            var first = Math.Min(length - 1, _pageStart + Position(start, ascii)); var last = Math.Min(length - 1, _pageStart + Position(end - 1, ascii));
                            selection.Anchor = editor.SelectionStart <= editor.SelectionEnd ? first : last; selection.Caret = editor.SelectionStart <= editor.SelectionEnd ? last : first; selection.Selected = true;
                        }
                        else { selection.Caret = caret; selection.Anchor = caret; selection.Selected = false; }
                        selection.LowNibble = !ascii && editor.CaretIndex % 3 == 1; SelectionVersion++;
                    };
                }
            }
        }
        private void AddByteModes(Panel actions, int side)
        {
            var name = side switch { 0 => "左", 1 => "中央", _ => "右" };
            var mode = new CheckBox { Content = name + " 挿入", Margin = new Thickness(4) };
            mode.IsCheckedChanged += (_, _) => { Selection(side).Insert = mode.IsChecked == true; SelectionVersion++; };
            var characters = new ComboBox { ItemsSource = new[] { name + " ANSI", name + " OEM" }, SelectedIndex = 0, Margin = new Thickness(4) };
            characters.SelectionChanged += (_, _) => { Selection(side).Oem = characters.SelectedIndex == 1; SelectionVersion++; };
            actions.Children.Add(mode); actions.Children.Add(characters);
        }
        private static int Position(int character, bool ascii) => ascii ? character / 17 * 16 + Math.Min(16, character % 17) : character / 3;
        private void RenderByteSelection(int side)
        {
            var selection = Selection(side); var length = Session.Length(LocalSide(side));
            selection.Caret = Math.Min(selection.Caret, length); selection.Anchor = Math.Min(selection.Anchor, length);
            if (selection.Selected && (selection.Anchor >= length || selection.Caret >= length)) selection.Selected = false;
            var page = Session.Page(LocalSide(side), _pageStart);
            var text = new System.Text.StringBuilder(); for (var i = 0; i < page.Length; i++) { text.Append(page[i] is >= 32 and < 127 ? (char)page[i] : '·'); if (i % 16 == 15) text.Append('\n'); }
            AsciiEditor(side).Text = text.ToString(); AsciiEditor(side).IsReadOnly = ReadOnly(side);
            foreach (var ascii in new[] { false, true })
            {
                var editor = ascii ? AsciiEditor(side) : Editor(side);
                int Character(int value) { var local = Math.Clamp(value - _pageStart, 0, page.Length); return ascii ? local + local / 16 : local * 3; }
                editor.CaretIndex = Math.Min(editor.Text?.Length ?? 0, Character(selection.Caret) + (!ascii && selection.LowNibble ? 1 : 0));
                if (selection.Selected)
                {
                    editor.SelectionStart = Character(selection.Start); editor.SelectionEnd = Math.Min(editor.Text?.Length ?? 0, Character(selection.Start + selection.Count));
                }
                else editor.SelectionStart = editor.SelectionEnd = editor.CaretIndex;
            }
        }
        private void RevealCaret(int side)
        {
            var caret = Selection(side).Caret;
            if (caret < _pageStart || caret >= _pageStart + 4096) _pageStart = caret / 4096 * 4096;
        }
        private void WritableSelection(int side)
        {
            ThrowDisposed(); ApplyReadOnly?.Invoke();
            if (ReadOnly(side)) throw new InvalidOperationException("この側は読取り専用です。");
            EnsureApplied();
            if (EditingCurrent?.Invoke() == false) throw new OperationCanceledException("比較元が変更されました。");
        }
        internal void CommitBytes(int side, BinaryEditSession.Candidate candidate, int caret, bool low = false)
        {
            WritableSelection(side); var changed = Session.CommitPrepared(candidate); var selection = Selection(side);
            selection.Caret = selection.Anchor = Math.Min(caret, Session.Length(LocalSide(side))); selection.Selected = false; selection.LowNibble = low;
            SelectionVersion++; if (changed) StateVersion++; RevealCaret(side); Render();
        }
        internal void InputBytes(int side, string text, bool ascii)
        {
            WritableSelection(side); var selection = Selection(side);
            foreach (var character in text)
            {
                byte value;
                if (ascii)
                {
                    var encoded = BinaryTextEncoding.Encode(character.ToString(), selection.Oem);
                    if (encoded.Length != 1) throw new FormatException("直接文字入力は1文字1バイトです。複数バイト文字は貼り付けダイアログを使用してください。");
                    value = encoded[0];
                }
                else if (!Uri.IsHexDigit(character)) continue;
                else value = (byte)(character <= '9' ? character - '0' : char.ToLowerInvariant(character) - 'a' + 10);
                var start = selection.Start; var length = Session.Length(LocalSide(side));
                var remove = selection.Count; var append = start == length; var insert = !selection.Selected && (selection.Insert && !selection.LowNibble || append);
                byte current = selection.Selected || insert ? (byte)0 : Session.Page(LocalSide(side), start, 1)[0];
                var low = selection.LowNibble && !append;
                if (!ascii) value = (byte)(low ? (current & 0xf0) | value : (current & 0x0f) | value << 4);
                if (remove == 0 && !insert) remove = 1;
                var candidate = Session.PrepareReplaceRange(LocalSide(side), start, remove, new byte[] { value });
                CommitBytes(side, candidate, ascii || low ? start + 1 : start, !ascii && !low);
            }
        }
        private bool HandleByteKey(int side, KeyEventArgs args, bool ascii)
        {
            var command = args.KeyModifiers.HasFlag(KeyModifiers.Control) || args.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (command && args.Key is Key.C or Key.X or Key.V)
            { _ = RunAsync(() => ClipboardAsync(side, args.Key == Key.C ? BinaryClipboardCommand.Copy : args.Key == Key.X ? BinaryClipboardCommand.Cut : BinaryClipboardCommand.FastPaste)); return true; }
            if (command && args.Key is Key.Z or Key.Y) { _ = Run(() => { if (args.Key == Key.Z && !args.KeyModifiers.HasFlag(KeyModifiers.Shift)) Undo(); else Redo(); }); return true; }
            if (command && args.Key == Key.A) { _ = Run(() => { var length = Session.Length(LocalSide(side)); if (length != 0) SelectBytes(side, 0, length - 1); }); return true; }
            if (args.Key == Key.Insert) { Selection(side).Insert = !Selection(side).Insert; SelectionVersion++; SetStatus(Selection(side).Insert ? "挿入モード" : "上書きモード"); return true; }
            if (args.Key is Key.Delete or Key.Back) { _ = Run(() => DeleteBytes(side, args.Key == Key.Back)); return true; }
            if (args.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Escape)) return false;
            _ = Run(() =>
            {
                EnsureApplied(); var selection = Selection(side); var length = Session.Length(LocalSide(side)); var shift = args.KeyModifiers.HasFlag(KeyModifiers.Shift);
                var next = args.Key switch { Key.Left => selection.Caret - 1, Key.Right => selection.Caret + 1, Key.Up => selection.Caret - 16, Key.Down => selection.Caret + 16, Key.PageUp => selection.Caret - 4096, Key.PageDown => selection.Caret + 4096, Key.Home => command ? 0 : selection.Caret / 16 * 16, Key.End => command ? length : Math.Min(length, selection.Caret / 16 * 16 + 15), _ => selection.Caret };
                var low = false;
                if (!shift && !ascii && args.Key is Key.Left or Key.Right && !selection.Selected)
                { if (args.Key == Key.Left && selection.LowNibble) { next = selection.Caret; low = false; } else if (args.Key == Key.Right && !selection.LowNibble) { next = selection.Caret; low = true; } else low = args.Key == Key.Left; }
                next = Math.Clamp(next, 0, shift && length > 0 ? length - 1 : length);
                SelectBytes(side, shift && length > 0 ? Math.Min(selection.Selected ? selection.Anchor : selection.Caret, length - 1) : next, next, shift && length > 0, low);
            }); return true;
        }
        internal void DeleteBytes(int side, bool backspace)
        {
            WritableSelection(side); var selection = Selection(side); var start = selection.Start; var count = selection.Count;
            if (count == 0)
            {
                if (backspace)
                {
                    if (start == 0) return;
                    if (!selection.Insert) { SelectBytes(side, start - 1, start - 1, false); return; }
                    start--;
                }
                if (start >= Session.Length(LocalSide(side))) return; count = 1;
            }
            CommitBytes(side, Session.PrepareReplaceRange(LocalSide(side), start, count, []), start);
        }
    }
}
