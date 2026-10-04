using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public static partial class SpecializedViews
{
    public sealed class BinaryPanel : DockPanel, IDisposable
    {
        internal BinaryEditSession Session { get; }
        internal ScrollViewer Viewport { get; }
        public TextBox LeftHex { get; } = HexEditor();
        public TextBox MiddleHex { get; } = HexEditor();
        public TextBox RightHex { get; } = HexEditor();
        private readonly TextBlock[] _captions = [new() { TextTrimming = TextTrimming.CharacterEllipsis }, new() { TextTrimming = TextTrimming.CharacterEllipsis }, new() { TextTrimming = TextTrimming.CharacterEllipsis }];
        internal void SetCaption(int side, string value) { _ = LocalSide(side); _captions[side].Text = value; }
        internal string Caption(int side) => _captions[side].Text ?? "";
        public bool HasMiddle => Session.SideCount == 3;
        internal IEnumerable<int> ProjectSides => HasMiddle ? [0, 1, 2] : [0, 2];
        // 二者のCore右1とproject右2の変換はここだけで行う。
        internal int LocalSide(int side) => side is < 0 or > 2 || side == 1 && !HasMiddle
            ? throw new ArgumentOutOfRangeException(nameof(side)) : HasMiddle ? side : side == 2 ? 1 : 0;
        internal TextBox Editor(int side) { _ = LocalSide(side); return side switch { 0 => LeftHex, 1 => MiddleHex, _ => RightHex }; }
        public ListBox Differences { get; } = new() { MaxHeight = 120, FontFamily = Mono };
        private readonly NumericUpDown _offset = new() { Minimum = 0, Increment = 4096, Value = 0, Width = 140 };
        private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        private readonly string[] _baseline = ["", "", ""];
        private readonly bool[] _readOnly = new bool[3];
        private IReadOnlyList<(int Start, int Length)> _ranges = [];
        private bool _updating, _disposed;
        private int _pageStart;
        internal bool FixedLeftReadOnly { get; init; }
        internal bool FixedRightReadOnly { get; init; }
        internal Func<int, bool>? RequiredReadOnly { get; set; }
        internal bool ReadOnly(int side) { _ = LocalSide(side); return _readOnly[side] || (side == 0 && FixedLeftReadOnly) || (side == 2 && FixedRightReadOnly) || RequiredReadOnly?.Invoke(side) == true; }
        public bool LeftReadOnly { get => ReadOnly(0); set => _readOnly[0] = value; }
        public bool MiddleReadOnly { get => ReadOnly(1); set => _readOnly[1] = value; }
        public bool RightReadOnly { get => ReadOnly(2); set => _readOnly[2] = value; }
        public Action? ApplyReadOnly { get; }
        internal Func<int, string?, CancellationToken, Task>? SaveContent { get; set; }
        internal Func<int, Task<string?>>? SavePathPicker { get; set; }
        internal Func<int, int, Task>? CopyAllContent { get; set; }
        internal Func<int, BinaryRangeKind, Task>? RangeEditContent { get; set; }
        public Func<bool>? IsDirty { get; }
        public Action? MarkClean { get; }
        internal long StateVersion { get; private set; }
        // TextChangedの通知前でも未適用draftを古い候補で置き換えない。
        internal (long Version, string? Left, string? Middle, string? Right) StateStamp
            => (StateVersion, LeftHex.Text, HasMiddle ? MiddleHex.Text : null, RightHex.Text);
        internal bool IsDisposed => _disposed;
        internal bool Pending(int side) => (Editor(side).Text ?? "") != _baseline[side];
        internal bool Pending(bool right) => Pending(right ? 2 : 0);
        internal bool Dirty(int side) => Session.IsDirty(LocalSide(side)) || Pending(side);
        internal bool Dirty(bool right) => Dirty(right ? 2 : 0);
        internal void EnsureApplied()
        { if (ProjectSides.Any(Pending)) throw new InvalidOperationException("先に16進編集を適用してください。未適用の値は保持しています。"); }
        public Task SaveToAsync(int side, string path, CancellationToken token = default)
        { _ = LocalSide(side); return SaveContent?.Invoke(side, path, token) ?? throw new InvalidOperationException("保存元の比較がありません。"); }
        public Task SaveToAsync(bool rightSide, string path, CancellationToken token = default) => SaveToAsync(rightSide ? 2 : 0, path, token);
        internal Task SaveAsync(int side, CancellationToken token = default)
        { _ = LocalSide(side); return SaveContent?.Invoke(side, null, token) ?? throw new InvalidOperationException("保存元の比較がありません。"); }
        internal Task SaveAsync(bool right, CancellationToken token = default) => SaveAsync(right ? 2 : 0, token);

        internal BinaryPanel(byte[] left, byte[] right, bool leftReadOnly, bool rightReadOnly, byte[]? middle = null, bool middleReadOnly = false)
        {
            Session = middle is null ? new(left, right) : new(left, middle, right);
            _readOnly[0] = leftReadOnly; _readOnly[1] = middleReadOnly; _readOnly[2] = rightReadOnly;
            IsDirty = () => ProjectSides.Any(Dirty);
            MarkClean = () => { Session.MarkClean(); foreach (var side in ProjectSides) _baseline[side] = Editor(side).Text ?? ""; };
            ApplyReadOnly = () => { foreach (var side in ProjectSides) { Session.SetReadOnly(LocalSide(side), ReadOnly(side)); Editor(side).IsReadOnly = ReadOnly(side); } };
            foreach (var side in ProjectSides) Editor(side).TextChanged += (_, _) => { if (!_updating) StateVersion++; };
            _offset.ValueChanged += (_, _) =>
            {
                if (_updating) return;
                if (ProjectSides.Any(Pending)) { _updating = true; _offset.Value = _pageStart; _updating = false; _status.Text = "先に16進編集を適用してください。"; return; }
                _pageStart = (int)(_offset.Value ?? 0); Render();
            };
            Differences.SelectionChanged += (_, _) =>
            {
                if (!_updating && !_disposed && Differences.SelectedIndex >= 0 && Differences.SelectedIndex < _ranges.Count)
                    _offset.Value = _ranges[Differences.SelectedIndex].Start;
            };
            var actions = new WrapPanel(); actions.Children.Add(new TextBlock { Text = "表示オフセット", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }); actions.Children.Add(_offset);
            string Name(int side) => side switch { 0 => "左", 1 => "中央", _ => "右" };
            foreach (var side in ProjectSides) AddButton(actions, Name(side) + "の編集を適用", () => Run(() => Apply(side)));
            if (HasMiddle)
                foreach (var source in ProjectSides)
                    foreach (var destination in ProjectSides.Where(side => side != source))
                        AddButton(actions, Name(source) + "→" + Name(destination) + " 差分範囲", () => Run(() => CopySelected(source, destination)));
            else
            {
                AddButton(actions, "→ 選択範囲", () => Run(() => CopySelected(0, 2)));
                AddButton(actions, "← 選択範囲", () => Run(() => CopySelected(2, 0)));
            }
            AddButton(actions, "元に戻す", () => Run(() => Undo()));
            foreach (var source in ProjectSides)
                foreach (var destination in ProjectSides.Where(side => side != source))
                    AddButton(actions, Name(source) + "→" + Name(destination) + " 全体コピー", () => RunAsync(() => CopyAllAsync(source, destination)));
            AddButton(actions, "やり直す", () => Run(() => Redo()));
            foreach (var side in ProjectSides)
            {
                foreach (var kind in Enum.GetValues<BinaryRangeKind>())
                    AddButton(actions, Name(side) + " " + BinaryRangeDialog.Label(kind), () => RunAsync(() => RangeEditAsync(side, kind)));
                AddButton(actions, Name(side) + "を保存", () => SaveAsync(side));
                AddButton(actions, Name(side) + "を別名保存", () => PickBinaryOutputAsync(this, side, side switch { 0 => "left.bin", 1 => "middle.bin", _ => "right.bin" }));
            }
            // タブと操作欄の折返しで本文が狭くても、Dockの子を重ねず全操作へスクロールできる。
            var content = new StackPanel();
            Viewport = new ScrollViewer { Content = content, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            content.Children.Add(actions); content.Children.Add(_status);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(HasMiddle ? "*,*,*" : "*,*"), ColumnSpacing = 8, Height = 140 };
            var column = 0; foreach (var side in ProjectSides)
            {
                var editorPane = new DockPanel(); _captions[side].Text = Name(side); _captions[side].Margin = new Thickness(4);
                DockPanel.SetDock(_captions[side], Dock.Top); editorPane.Children.Add(_captions[side]); editorPane.Children.Add(Editor(side));
                Grid.SetColumn(editorPane, column++); grid.Children.Add(editorPane);
            }
            content.Children.Add(grid); content.Children.Add(Differences); Children.Add(Viewport);
            LayoutUpdated += (_, _) =>
            {
                // 操作・長い保存先・フォントの実寸を先に確保し、Hex本文は140px以上で自然高さへ足す。
                var desired = Math.Max(140, Viewport.Bounds.Height - actions.Bounds.Height - _status.Bounds.Height
                    - _status.Margin.Top - _status.Margin.Bottom - Differences.Bounds.Height);
                if (Math.Abs(grid.Height - desired) > .5) grid.Height = desired;
            };
            ApplyReadOnly(); Render();
        }
        private Task Run(Action action)
        { try { action(); } catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or FormatException or ArgumentException) { _status.Text = exception.Message; } return Task.CompletedTask; }
        private async Task RunAsync(Func<Task> action)
        { try { await action(); } catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or FormatException or ArgumentException or OperationCanceledException) { if (!_disposed) _status.Text = exception.Message; } }
        internal Task CopyAllAsync(int source, int destination)
        { ThrowDisposed(); EnsureApplied(); _ = LocalSide(source); _ = LocalSide(destination); return CopyAllContent?.Invoke(source, destination) ?? throw new InvalidOperationException("コピー元の比較がありません。"); }
        internal void CopyAll(int source, int destination)
        { ThrowDisposed(); EnsureApplied(); ApplyReadOnly?.Invoke(); var revision = Session.Revision(LocalSide(destination)); Session.CopyAll(LocalSide(source), LocalSide(destination)); if (revision != Session.Revision(LocalSide(destination))) { StateVersion++; Render(); } }
        internal Task RangeEditAsync(int side, BinaryRangeKind kind)
        { ThrowDisposed(); EnsureApplied(); _ = LocalSide(side); return RangeEditContent?.Invoke(side, kind) ?? throw new InvalidOperationException("編集元の比較がありません。"); }
        internal void EditRange(int side, BinaryRangeRequest request)
        {
            ThrowDisposed(); EnsureApplied(); ApplyReadOnly?.Invoke();
            var local = LocalSide(side); var revision = Session.Revision(local);
            Session.EditRange(local, request.Start, request.Count, request.Hex, request.Kind == BinaryRangeKind.Insert, request.Kind == BinaryRangeKind.Delete);
            if (revision != Session.Revision(local)) { StateVersion++; Render(); }
        }
        public void Apply(int side)
        {
            ThrowDisposed(); ApplyReadOnly?.Invoke();
            Session.ApplyHexPage(LocalSide(side), _pageStart, Editor(side).Text ?? "");
            _baseline[side] = Editor(side).Text ?? ""; StateVersion++; Render();
        }
        public void Apply(bool right) => Apply(right ? 2 : 0);
        private void CopySelected(int source, int destination)
        {
            if (Differences.SelectedIndex < 0 || Differences.SelectedIndex >= _ranges.Count) return;
            var range = _ranges[Differences.SelectedIndex];
            // 第三側だけが長い末尾は、このpairの差分コピーへ混ぜない。
            var length = Math.Min(range.Length, Math.Max(Session.Length(LocalSide(source)), Session.Length(LocalSide(destination))) - range.Start);
            if (length > 0) CopyRange(source, destination, range.Start, length);
        }
        public void CopyRange(int source, int destination, int start, int length)
        { ThrowDisposed(); EnsureApplied(); ApplyReadOnly?.Invoke(); Session.CopyRange(LocalSide(source), LocalSide(destination), start, length); StateVersion++; Render(); }
        public void CopyRange(bool toRight, int start, int length) => CopyRange(toRight ? 0 : 2, toRight ? 2 : 0, start, length);
        public bool Undo() { ThrowDisposed(); EnsureApplied(); ApplyReadOnly?.Invoke(); var changed = Session.Undo(); if (changed) { StateVersion++; Render(); } return changed; }
        public bool Redo() { ThrowDisposed(); EnsureApplied(); ApplyReadOnly?.Invoke(); var changed = Session.Redo(); if (changed) { StateVersion++; Render(); } return changed; }
        internal BinaryCapture Capture(int side) { ThrowDisposed(); EnsureApplied(); return Session.Capture(LocalSide(side)); }
        internal BinaryCapture Capture(bool right) => Capture(right ? 2 : 0);
        internal BinaryCapture CaptureApplied(int side) { ThrowDisposed(); return Session.Capture(LocalSide(side)); }
        internal BinaryCapture CaptureApplied(bool right) => CaptureApplied(right ? 2 : 0);
        internal void SetOffset(int value) { ThrowDisposed(); EnsureApplied(); if (value < 0 || value > _offset.Maximum) throw new ArgumentOutOfRangeException(nameof(value)); _offset.Value = value; }
        internal void MarkSaved(int side, BinaryCapture capture) { Session.MarkSaved(LocalSide(side), capture); Render(); }
        internal void MarkSaved(bool right, BinaryCapture capture) => MarkSaved(right ? 2 : 0, capture);
        internal void AdoptSavedBytes(int side, byte[] bytes)
        { if (Dirty(side)) throw new InvalidOperationException("編集中のバイナリは保持しています。比較し直してください。"); Session.AdoptSavedBytes(LocalSide(side), bytes); StateVersion++; Render(); }
        internal void AdoptSavedBytes(bool right, byte[] bytes) => AdoptSavedBytes(right ? 2 : 0, bytes);
        internal void SetStatus(string value) => _status.Text = value;
        private void ThrowDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(BinaryPanel)); }
        private void Render()
        {
            _updating = true;
            _offset.Maximum = ProjectSides.Max(side => Session.Length(LocalSide(side)));
            _pageStart = Math.Min(_pageStart, (int)_offset.Maximum); _offset.Value = _pageStart;
            foreach (var side in ProjectSides)
            {
                if (Pending(side)) continue;
                var editor = Editor(side); editor.Text = Hex(Session.Page(LocalSide(side), _pageStart)); _baseline[side] = editor.Text ?? "";
                editor.CaretIndex = Math.Min(editor.CaretIndex, editor.Text?.Length ?? 0);
            }
            _ranges = Session.Differences(10_000, out var total);
            Differences.ItemsSource = _ranges.Select(range => $"0x{range.Start:X8} · {range.Length:N0} bytes").ToArray();
            _status.Text = string.Join(" / ", ProjectSides.Select(side => $"{(side switch { 0 => "左", 1 => "中央", _ => "右" })} {Session.Length(LocalSide(side)):N0} bytes"))
                + $" · 差分範囲 {total:N0} · 表示先頭 0x{_pageStart:X8}（最大4096 bytes）。同じ長さの16進値を入力して適用します。";
            _updating = false;
        }
        public void Dispose() { if (_disposed) return; _disposed = true; SaveContent = null; SavePathPicker = null; CopyAllContent = null; RangeEditContent = null; RequiredReadOnly = null; Session.Dispose(); }
    }
}
