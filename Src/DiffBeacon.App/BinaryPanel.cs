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
        public TextBox LeftHex { get; } = HexEditor();
        public TextBox RightHex { get; } = HexEditor();
        public ListBox Differences { get; } = new() { MaxHeight = 160, FontFamily = Mono };
        private readonly NumericUpDown _offset = new() { Minimum = 0, Increment = 4096, Value = 0, Width = 140 };
        private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        private readonly string[] _baseline = ["", ""];
        private IReadOnlyList<(int Start, int Length)> _ranges = [];
        private bool _updating, _disposed, _leftReadOnly, _rightReadOnly;
        private int _pageStart;
        internal bool FixedLeftReadOnly { get; init; }
        internal bool FixedRightReadOnly { get; init; }
        internal Func<bool, bool>? RequiredReadOnly { get; set; }
        public bool LeftReadOnly { get => FixedLeftReadOnly || _leftReadOnly || RequiredReadOnly?.Invoke(false) == true; set => _leftReadOnly = value; }
        public bool RightReadOnly { get => FixedRightReadOnly || _rightReadOnly || RequiredReadOnly?.Invoke(true) == true; set => _rightReadOnly = value; }
        public Action? ApplyReadOnly { get; }
        internal Func<bool, string?, CancellationToken, Task>? SaveContent { get; set; }
        internal Func<bool, Task<string?>>? SavePathPicker { get; set; }
        public Func<bool>? IsDirty { get; }
        public Action? MarkClean { get; }
        internal long StateVersion { get; private set; }
        internal bool IsDisposed => _disposed;
        internal bool Pending(bool right) => ((right ? RightHex.Text : LeftHex.Text) ?? "") != _baseline[right ? 1 : 0];
        internal bool Dirty(bool right) => Session.IsDirty(right ? 1 : 0) || Pending(right);
        internal void EnsureApplied()
        { if (Pending(false) || Pending(true)) throw new InvalidOperationException("先に16進編集を適用してください。未適用の値は保持しています。"); }
        public Task SaveToAsync(bool rightSide, string path, CancellationToken token = default) => SaveContent?.Invoke(rightSide, path, token) ?? throw new InvalidOperationException("保存元の比較がありません。");
        internal Task SaveAsync(bool right, CancellationToken token = default) => SaveContent?.Invoke(right, null, token) ?? throw new InvalidOperationException("保存元の比較がありません。");

        internal BinaryPanel(byte[] left, byte[] right, bool leftReadOnly, bool rightReadOnly)
        {
            Session = new(left, right); _leftReadOnly = leftReadOnly; _rightReadOnly = rightReadOnly;
            IsDirty = () => Dirty(false) || Dirty(true);
            MarkClean = () => { Session.MarkClean(); _baseline[0] = LeftHex.Text ?? ""; _baseline[1] = RightHex.Text ?? ""; };
            ApplyReadOnly = () => { Session.SetReadOnly(0, LeftReadOnly); Session.SetReadOnly(1, RightReadOnly); LeftHex.IsReadOnly = LeftReadOnly; RightHex.IsReadOnly = RightReadOnly; };
            LeftHex.TextChanged += (_, _) => { if (!_updating) StateVersion++; };
            RightHex.TextChanged += (_, _) => { if (!_updating) StateVersion++; };
            _offset.ValueChanged += (_, _) =>
            {
                if (_updating) return;
                if (Pending(false) || Pending(true)) { _updating = true; _offset.Value = _pageStart; _updating = false; _status.Text = "先に16進編集を適用してください。"; return; }
                _pageStart = (int)(_offset.Value ?? 0); Render();
            };
            Differences.SelectionChanged += (_, _) =>
            {
                if (!_updating && !_disposed && Differences.SelectedIndex >= 0 && Differences.SelectedIndex < _ranges.Count)
                    _offset.Value = _ranges[Differences.SelectedIndex].Start;
            };
            var actions = new WrapPanel(); actions.Children.Add(new TextBlock { Text = "表示オフセット", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }); actions.Children.Add(_offset);
            AddButton(actions, "左の編集を適用", () => Run(() => Apply(false)));
            AddButton(actions, "右の編集を適用", () => Run(() => Apply(true)));
            AddButton(actions, "→ 選択範囲", () => Run(() => CopySelected(true)));
            AddButton(actions, "← 選択範囲", () => Run(() => CopySelected(false)));
            AddButton(actions, "元に戻す", () => Run(() => Undo()));
            AddButton(actions, "やり直す", () => Run(() => Redo()));
            AddButton(actions, "左を保存", () => SaveAsync(false)); AddButton(actions, "右を保存", () => SaveAsync(true));
            AddButton(actions, "左を別名保存", () => PickBinaryOutputAsync(this, false, "left.bin"));
            AddButton(actions, "右を別名保存", () => PickBinaryOutputAsync(this, true, "right.bin"));
            DockPanel.SetDock(actions, Dock.Top); Children.Add(actions);
            DockPanel.SetDock(_status, Dock.Top); Children.Add(_status);
            DockPanel.SetDock(Differences, Dock.Bottom); Children.Add(Differences);
            Children.Add(Pair(LeftHex, RightHex)); ApplyReadOnly(); Render();
        }
        private Task Run(Action action)
        { try { action(); } catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or FormatException or ArgumentException) { _status.Text = exception.Message; } return Task.CompletedTask; }
        public void Apply(bool right)
        {
            ThrowDisposed(); ApplyReadOnly?.Invoke(); var side = right ? 1 : 0;
            Session.ApplyHexPage(side, _pageStart, (right ? RightHex.Text : LeftHex.Text) ?? "");
            _baseline[side] = (right ? RightHex.Text : LeftHex.Text) ?? ""; StateVersion++; Render();
        }
        private void CopySelected(bool right)
        { if (Differences.SelectedIndex < 0 || Differences.SelectedIndex >= _ranges.Count) return; var range = _ranges[Differences.SelectedIndex]; CopyRange(right, range.Start, range.Length); }
        public void CopyRange(bool toRight, int start, int length)
        { ThrowDisposed(); EnsureApplied(); ApplyReadOnly?.Invoke(); Session.CopyRange(toRight ? 0 : 1, toRight ? 1 : 0, start, length); StateVersion++; Render(); }
        public bool Undo() { ThrowDisposed(); EnsureApplied(); ApplyReadOnly?.Invoke(); var changed = Session.Undo(); if (changed) { StateVersion++; Render(); } return changed; }
        public bool Redo() { ThrowDisposed(); EnsureApplied(); ApplyReadOnly?.Invoke(); var changed = Session.Redo(); if (changed) { StateVersion++; Render(); } return changed; }
        internal BinaryCapture Capture(bool right) { ThrowDisposed(); EnsureApplied(); return Session.Capture(right ? 1 : 0); }
        internal BinaryCapture CaptureApplied(bool right) { ThrowDisposed(); return Session.Capture(right ? 1 : 0); }
        internal void SetOffset(int value) { ThrowDisposed(); EnsureApplied(); if (value < 0 || value > _offset.Maximum) throw new ArgumentOutOfRangeException(nameof(value)); _offset.Value = value; }
        internal void MarkSaved(bool right, BinaryCapture capture) { Session.MarkSaved(right ? 1 : 0, capture); Render(); }
        internal void AdoptSavedBytes(bool right, byte[] bytes)
        { if (Dirty(right)) throw new InvalidOperationException("編集中のバイナリは保持しています。比較し直してください。"); Session.AdoptSavedBytes(right ? 1 : 0, bytes); StateVersion++; Render(); }
        internal void SetStatus(string value) => _status.Text = value;
        private void ThrowDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(BinaryPanel)); }
        private void Render()
        {
            _updating = true;
            _offset.Maximum = Math.Max(Session.Length(0), Session.Length(1));
            for (var side = 0; side < 2; side++)
            {
                if (Pending(side == 1)) continue;
                var editor = side == 1 ? RightHex : LeftHex;
                editor.Text = Hex(Session.Page(side, _pageStart)); _baseline[side] = editor.Text ?? "";
            }
            _ranges = Session.Differences(10_000, out var total);
            Differences.ItemsSource = _ranges.Select(range => $"0x{range.Start:X8} · {range.Length:N0} bytes").ToArray();
            _status.Text = $"左 {Session.Length(0):N0} bytes / 右 {Session.Length(1):N0} bytes · 差分範囲 {total:N0} · 表示先頭 0x{_pageStart:X8}（最大4096 bytes）。同じ長さの16進値を入力して適用します。";
            _updating = false;
        }
        public void Dispose() { if (_disposed) return; _disposed = true; SaveContent = null; SavePathPicker = null; RequiredReadOnly = null; Session.Dispose(); }
    }
}
