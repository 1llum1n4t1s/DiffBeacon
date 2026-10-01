using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public static class SpecializedViews
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Menlo, Consolas, monospace");
    public static void Release(Control? control) { if (control is IDisposable disposable) disposable.Dispose(); }
    public static bool HasUnsavedChanges(Control? control) => control is BinaryPanel binary && binary.IsDirty?.Invoke() == true || control is TablePanel table && table.HasPendingCellEdit;
    public static void DiscardChanges(Control? control) { if (control is BinaryPanel binary) binary.MarkClean?.Invoke(); if (control is TablePanel table) table.DiscardCellDraft(); }
    public static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp";

    public static async Task<Control> ImagesAsync(string left, string right, CancellationToken cancellationToken)
    {
        var leftSnapshot = await ImageComparisonEngine.OpenAsync(left, cancellationToken);
        var rightSnapshot = await ImageComparisonEngine.OpenAsync(right, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var panel = new ImagePanel(leftSnapshot, rightSnapshot);
        try
        {
            await panel.SetFramesAsync(1, 1, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return panel;
        }
        catch { panel.Dispose(); throw; }
    }
    public static async Task<Control> BinaryAsync(string left, string right, CancellationToken cancellationToken, bool leftReadOnly = false, bool rightReadOnly = false, Action<string>? guardOutput = null)
    {
        const int limit = 16 * 1024 * 1024;
        if (new FileInfo(left).Length > limit || new FileInfo(right).Length > limit) throw new InvalidOperationException("16進比較の上限は各16 MiBです。");
        var a = await ReadBinaryAsync(left, limit, cancellationToken);
        var b = await ReadBinaryAsync(right, limit, cancellationToken);
        var root = new BinaryPanel { LeftReadOnly = leftReadOnly, RightReadOnly = rightReadOnly };
        var actions = new WrapPanel();
        var offset = new NumericUpDown { Minimum = 0, Maximum = Math.Max(a.Length, b.Length), Increment = 4096, Value = 0, Width = 140 };
        var status = new TextBlock { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        var leftEditor = HexEditor(); var rightEditor = HexEditor();
        root.ApplyReadOnly = () => { leftEditor.IsReadOnly = root.LeftReadOnly; rightEditor.IsReadOnly = root.RightReadOnly; };
        root.ApplyReadOnly();
        var differences = new ListBox { MaxHeight = 160, FontFamily = Mono };
        var pageStart = 0; var updating = false;
        var leftDirty = false; var rightDirty = false;
        var leftBaseline = ""; var rightBaseline = "";
        bool PendingEdits() => leftEditor.Text != leftBaseline || rightEditor.Text != rightBaseline;
        root.IsDirty = () => leftDirty || rightDirty || PendingEdits();
        root.MarkClean = () => { leftDirty = false; rightDirty = false; leftBaseline = leftEditor.Text ?? ""; rightBaseline = rightEditor.Text ?? ""; };
        var ranges = new List<(int Start, int Length)>();
        var totalRanges = 0;
        void Refresh(bool rebuildRanges = true)
        {
            updating = true;
            pageStart = (int)(offset.Value ?? 0);
            leftEditor.Text = Hex(a.AsSpan(Math.Min(pageStart, a.Length), Math.Min(4096, Math.Max(0, a.Length - pageStart))));
            rightEditor.Text = Hex(b.AsSpan(Math.Min(pageStart, b.Length), Math.Min(4096, Math.Max(0, b.Length - pageStart))));
            leftBaseline = leftEditor.Text ?? ""; rightBaseline = rightEditor.Text ?? "";
            if (rebuildRanges)
            {
                ranges.Clear();
                totalRanges = 0;
                var n = Math.Max(a.Length, b.Length);
                for (var i = 0; i < n; i++)
                {
                    if (i < a.Length && i < b.Length && a[i] == b[i]) continue;
                    var start = i;
                    while (i + 1 < n && (i + 1 >= a.Length || i + 1 >= b.Length || a[i + 1] != b[i + 1])) i++;
                    totalRanges++;
                    if (ranges.Count < 10_000) ranges.Add((start, i - start + 1));
                }
                differences.ItemsSource = ranges.Select(x => $"0x{x.Start:X8} · {x.Length:N0} bytes").ToArray();
            }
            status.Text = $"左 {a.Length:N0} bytes / 右 {b.Length:N0} bytes · 差分範囲 {totalRanges:N0}（一覧は先頭1万範囲まで）· 表示先頭 0x{pageStart:X8}（最大4096 bytes）。編集は同じ長さの16進値を入力し適用します。保存は別名保存です。";
            updating = false;
        }
        void Apply(bool toRight)
        {
            if (toRight ? root.RightReadOnly : root.LeftReadOnly) { status.Text = "この側はプロジェクトで読取り専用に指定されています。"; return; }
            var editor = toRight ? rightEditor : leftEditor;
            var bytes = toRight ? b : a;
            try
            {
                var compact = string.Concat((editor.Text ?? "").Where(c => !char.IsWhiteSpace(c)));
                var replacement = Convert.FromHexString(compact);
                var length = Math.Min(4096, Math.Max(0, bytes.Length - pageStart));
                if (replacement.Length != length) throw new FormatException("編集前と同じバイト数を入力してください。");
                var start = Math.Min(pageStart, bytes.Length);
                if (toRight) { rightDirty |= !replacement.AsSpan().SequenceEqual(bytes.AsSpan(start, length)); rightBaseline = editor.Text ?? ""; }
                else { leftDirty |= !replacement.AsSpan().SequenceEqual(bytes.AsSpan(start, length)); leftBaseline = editor.Text ?? ""; }
                replacement.CopyTo(bytes, start);
                if (!PendingEdits()) Refresh(); else status.Text = "適用しました。もう一方の編集中の値も適用してください。";
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { status.Text = ex.Message; }
        }
        void Merge(bool toRight)
        {
            if (toRight ? root.RightReadOnly : root.LeftReadOnly) { status.Text = "この側はプロジェクトで読取り専用に指定されています。"; return; }
            if (PendingEdits()) { status.Text = "先に16進編集を適用してください。"; return; }
            if (differences.SelectedIndex < 0 || differences.SelectedIndex >= ranges.Count) return;
            var range = ranges[differences.SelectedIndex];
            var source = toRight ? a : b; var target = toRight ? b : a;
            // オフセット整列を保つため、末尾の長さ差以外は挿入・削除しない。
            var sourceLength = Math.Min(range.Length, Math.Max(0, source.Length - range.Start));
            if (range.Start + range.Length >= target.Length && range.Start + range.Length >= source.Length)
                Array.Resize(ref target, range.Start + sourceLength);
            source.AsSpan(Math.Min(range.Start, source.Length), sourceLength).CopyTo(target.AsSpan(Math.Min(range.Start, target.Length)));
            if (toRight) { b = target; rightDirty = true; } else { a = target; leftDirty = true; }
            offset.Maximum = Math.Max(a.Length, b.Length); Refresh();
        }
        actions.Children.Add(new TextBlock { Text = "表示オフセット", Margin = new Thickness(8) }); actions.Children.Add(offset);
        AddButton(actions, "左編集を適用", () => { Apply(false); return Task.CompletedTask; });
        AddButton(actions, "右編集を適用", () => { Apply(true); return Task.CompletedTask; });
        AddButton(actions, "選択範囲 →", () => { Merge(true); return Task.CompletedTask; });
        AddButton(actions, "← 選択範囲", () => { Merge(false); return Task.CompletedTask; });
        root.SaveContent = async (rightSide, path, token) =>
        {
            if (PendingEdits()) throw new InvalidOperationException("先に16進編集を適用してください。");
            GuardOutput(path);
            await WriteBinaryCopyAsync(path, rightSide ? b : a, token);
            if (rightSide) rightDirty = false; else leftDirty = false;
            status.Text = "バイナリを保存しました。";
        };
        AddButton(actions, "左を別名保存", () => PickBinaryOutputAsync(root, false, Path.GetFileName(left)));
        AddButton(actions, "右を別名保存", () => PickBinaryOutputAsync(root, true, Path.GetFileName(right)));
        void GuardOutput(string path)
        {
            guardOutput?.Invoke(path);
            if ((root.LeftReadOnly && DiffBeacon.Providers.ArchivePaths.SameFile(left, path)) || (root.RightReadOnly && DiffBeacon.Providers.ArchivePaths.SameFile(right, path)))
                throw new InvalidOperationException("読取り専用に指定された入力を上書きできません。");
        }
        offset.ValueChanged += (_, _) =>
        {
            if (updating) return;
            if (PendingEdits()) { updating = true; offset.Value = pageStart; updating = false; status.Text = "先に16進編集を適用してください。"; return; }
            Refresh(false);
        };
        differences.SelectionChanged += (_, _) => { if (!updating && differences.SelectedIndex >= 0 && differences.SelectedIndex < ranges.Count) offset.Value = ranges[differences.SelectedIndex].Start; };
        DockPanel.SetDock(actions, Dock.Top); root.Children.Add(actions);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        DockPanel.SetDock(differences, Dock.Bottom); root.Children.Add(differences);
        root.Children.Add(Pair(leftEditor, rightEditor)); Refresh(); return root;
    }

    public static Control StructuredJson(string leftText, string rightText)
    {
        var a = StructuredComparer.NormalizeJson(leftText); var b = StructuredComparer.NormalizeJson(rightText);
        var root = new DockPanel(); var status = new TextBlock { Text = a == b ? "JSONの構造と値は一致しています（キー順と同値な数値表現は無視）。" : "JSONの構造または値が異なります。オブジェクトキー順と同値な数値表現は無視し、配列順は保持します。", Margin = new Thickness(8) };
        DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        var x = HexEditor(); x.Text = a; x.IsReadOnly = true; var y = HexEditor(); y.Text = b; y.IsReadOnly = true;
        root.Children.Add(Pair(x, y)); return root;
    }

    internal static char DetectSeparator(string a, string b)
    {
        // 引用符内のタブは区切りとして数えない。
        foreach (var text in new[] { a, b })
        { var quoted = false; foreach (var c in text) { if (c == '"') quoted = !quoted; if (!quoted && c == '\t') return '\t'; if (!quoted && c is '\r' or '\n') break; } }
        return ',';
    }
    public sealed class ImagePanel : DockPanel, IDisposable
    {
        private ImageComparisonEngine.Snapshot? _leftSnapshot, _rightSnapshot;
        private ImageComparisonEngine.DecodedFrame? _leftDecoded, _rightDecoded;
        private readonly CancellationTokenSource _lifetime = new();
        private CancellationTokenSource? _operationCancellation;
        private long _generation;
        private bool _disposed, _updatingSelectors;
        private Window? _owner;
        private WriteableBitmap? _leftBitmap, _rightBitmap, _differenceBitmap;
        private readonly NumericUpDown _leftSelector, _rightSelector;
        private readonly NumericUpDown _threshold = new() { Name = "ImageThreshold", Minimum = 0, Maximum = 255, Value = 0, Increment = 1, Width = 140 };
        private readonly CheckBox _reportAllFrames = new() { Name = "ImageReportAllFrames", Content = "レポートは全フレーム", IsChecked = true };
        private int _displayThreshold;
        private readonly Slider _zoom = new() { Name = "ImageZoom", Minimum = 0.1, Maximum = 4, Value = 1, Width = 150 };
        private readonly Slider _opacity = new() { Name = "ImageOpacity", Minimum = 0, Maximum = 1, Value = 0.5, Width = 150 };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _leftPosition = new(), _rightPosition = new();
        private readonly Image _leftImage = new() { Stretch = Stretch.Fill };
        private readonly Image _rightImage = new() { Stretch = Stretch.Fill };
        private readonly Image _overlayLeft = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly Image _overlayRight = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Opacity = 0.5 };
        private readonly Image _difference = new() { Stretch = Stretch.Fill };
        private readonly Grid _overlay = new();
        private readonly Button _previousLeft, _nextLeft, _previousRight, _nextRight, _previousBoth, _nextBoth;

        internal int LeftFrame { get; private set; } = 1;
        internal int RightFrame { get; private set; } = 1;
        internal int LeftFrameCount { get; }
        internal int RightFrameCount { get; }
        internal long DifferentPixels { get; private set; }
        internal long TotalPixels { get; private set; }
        internal Task CurrentFrameOperation { get; private set; } = Task.CompletedTask;
        internal bool ReportAllFrames { get => _reportAllFrames.IsChecked == true; set => _reportAllFrames.IsChecked = value; }

        internal (ImageComparisonEngine.Snapshot Left, ImageComparisonEngine.Snapshot Right, int Threshold, int? LeftFrame, int? RightFrame) CaptureReport()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!CurrentFrameOperation.IsCompleted || _leftDecoded is null || _rightDecoded is null)
                throw new InvalidOperationException("画像フレームの表示が完了してからレポートを生成してください。");
            // bitmapの所有権を移さず、表示の原本と確定した設定だけを渡す。
            return (_leftSnapshot!, _rightSnapshot!, _displayThreshold,
                ReportAllFrames ? null : LeftFrame, ReportAllFrames ? null : RightFrame);
        }

        internal ImagePanel(ImageComparisonEngine.Snapshot left, ImageComparisonEngine.Snapshot right)
        {
            _leftSnapshot = left; _rightSnapshot = right;
            LeftFrameCount = left.FrameCount; RightFrameCount = right.FrameCount;
            _leftSelector = new() { Name = "ImageLeftFrame", Minimum = 1, Maximum = LeftFrameCount, Value = 1, Increment = 1, Width = 140 };
            _rightSelector = new() { Name = "ImageRightFrame", Minimum = 1, Maximum = RightFrameCount, Value = 1, Increment = 1, Width = 140 };
            _previousLeft = FrameButton("ImagePreviousLeft", "左 ◀", -1, true, false);
            _nextLeft = FrameButton("ImageNextLeft", "左 ▶", 1, true, false);
            _previousRight = FrameButton("ImagePreviousRight", "右 ◀", -1, false, true);
            _nextRight = FrameButton("ImageNextRight", "右 ▶", 1, false, true);
            _previousBoth = FrameButton("ImagePreviousBoth", "同期 ◀", -1, true, true);
            _nextBoth = FrameButton("ImageNextBoth", "同期 ▶", 1, true, true);
            var toolbar = new StackPanel { Spacing = 6, Margin = new Thickness(8) };
            var frames = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var control in new Control[] { _previousLeft, _leftSelector, _nextLeft, _leftPosition,
                _previousRight, _rightSelector, _nextRight, _rightPosition, _previousBoth, _nextBoth })
            { control.Margin = new Thickness(0, 0, 8, 0); frames.Children.Add(control); }
            toolbar.Children.Add(frames);
            var settings = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var control in new Control[] { new TextBlock { Text = "倍率" }, _zoom,
                new TextBlock { Text = "右の不透明度" }, _opacity, new TextBlock { Text = "差分閾値" }, _threshold, _reportAllFrames })
            { control.Margin = new Thickness(0, 0, 12, 0); settings.Children.Add(control); }
            toolbar.Children.Add(settings); DockPanel.SetDock(toolbar, Dock.Top); Children.Add(toolbar);
            var footer = new StackPanel { Margin = new Thickness(8), Spacing = 4 };
            footer.Children.Add(_status);
            footer.Children.Add(new TextBlock { Text = "選択したフレームの各チャンネルの最大差を比較します。サイズ外は差分です。色管理・位置合わせ・画像マージは対象外です。", TextWrapping = TextWrapping.Wrap });
            DockPanel.SetDock(footer, Dock.Bottom); Children.Add(footer);
            _overlay.Children.Add(_overlayLeft); _overlay.Children.Add(_overlayRight);
            var side = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
            side.Children.Add(Scroll(_leftImage)); var rightScroll = Scroll(_rightImage); Grid.SetColumn(rightScroll, 1); side.Children.Add(rightScroll);
            Children.Add(new TabControl { ItemsSource = new[] {
                new TabItem { Header = "左右", Content = side },
                new TabItem { Header = "重ね合わせ", Content = Scroll(_overlay) },
                new TabItem { Header = "ピクセル差分", Content = Scroll(_difference) } } });
            _zoom.ValueChanged += (_, _) => UpdateZoom();
            _opacity.ValueChanged += (_, _) => { if (!_disposed) _overlayRight.Opacity = _opacity.Value; };
            _threshold.ValueChanged += async (_, _) =>
            { if (!_updatingSelectors && !_disposed) await SelectFromControlsAsync(); };
            _leftSelector.ValueChanged += async (_, _) =>
            { if (!_updatingSelectors && !_disposed) await SelectFromControlsAsync(); };
            _rightSelector.ValueChanged += async (_, _) =>
            { if (!_updatingSelectors && !_disposed) await SelectFromControlsAsync(); };
            AttachedToVisualTree += (_, _) =>
            { if (!_disposed && _owner is null && TopLevel.GetTopLevel(this) is Window owner) { _owner = owner; owner.Closed += OwnerClosed; } };
            UpdateNavigation();
        }

        private Button FrameButton(string name, string content, int direction, bool moveLeft, bool moveRight)
        {
            var button = new Button { Name = name, Content = content };
            button.Click += async (_, _) =>
            {
                if (_disposed) return;
                try
                {
                    // 未完了の選択も起点にし、連続クリックを失わない。
                    var left = ReadFrame(_leftSelector, LeftFrameCount);
                    var right = ReadFrame(_rightSelector, RightFrameCount);
                    if (moveLeft && moveRight)
                    {
                        // 旧同期操作は最大の選択位置を共通起点にし、短い側は末尾に留める。
                        var target = Math.Max(left, right) + direction;
                        if (target < 1 || target > Math.Max(LeftFrameCount, RightFrameCount)) return;
                        left = Math.Min(target, LeftFrameCount); right = Math.Min(target, RightFrameCount);
                    }
                    else if (moveLeft) left += direction;
                    else if (moveRight) right += direction;
                    if (left < 1 || left > LeftFrameCount || right < 1 || right > RightFrameCount) return;
                    _updatingSelectors = true;
                    try { _leftSelector.Value = left; _rightSelector.Value = right; }
                    finally { _updatingSelectors = false; }
                    await SelectFromControlsAsync();
                }
                catch (ArgumentException error)
                { RestoreSelectors(); _status.Text = $"画像フレームを表示できません: {error.Message}"; }
            };
            return button;
        }

        private static int ReadFrame(NumericUpDown selector, int count)
        {
            var value = selector.Value;
            if (!value.HasValue || value.Value != decimal.Truncate(value.Value) || value.Value < 1 || value.Value > count)
                throw new ArgumentException($"画像フレームは1..{count}の整数を指定してください。");
            return (int)value.Value;
        }

        private async Task SelectFromControlsAsync()
        {
            var generation = _generation;
            try
            {
                var left = ReadFrame(_leftSelector, LeftFrameCount);
                var right = ReadFrame(_rightSelector, RightFrameCount);
                var operation = SetFramesAsync(left, right);
                generation = _generation;
                UpdateNavigation();
                await operation;
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!_disposed && generation == _generation)
                { RestoreSelectors(); _status.Text = $"画像フレームを表示できません: {error.Message}"; }
            }
        }

        internal Task SetFramesAsync(int leftFrame, int rightFrame, CancellationToken token = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var left = _leftSnapshot!; var right = _rightSnapshot!;
            ImageComparisonEngine.ValidateSelection(left, right, leftFrame, rightFrame);
            token.ThrowIfCancellationRequested();
            _operationCancellation?.Cancel();
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            _operationCancellation = cancel;
            var generation = ++_generation;
            var threshold = (int)(_threshold.Value ?? 0);
            var cachedLeft = _leftDecoded?.Number == leftFrame ? _leftDecoded : null;
            var cachedRight = _rightDecoded?.Number == rightFrame ? _rightDecoded : null;
            var operation = LoadFramesAsync(left, right, leftFrame, rightFrame, threshold, cachedLeft, cachedRight, generation, cancel);
            CurrentFrameOperation = operation;
            return operation;
        }

        private async Task LoadFramesAsync(ImageComparisonEngine.Snapshot left, ImageComparisonEngine.Snapshot right,
            int leftFrame, int rightFrame, int threshold, ImageComparisonEngine.DecodedFrame? cachedLeft,
            ImageComparisonEngine.DecodedFrame? cachedRight, long generation, CancellationTokenSource cancel)
        {
            var token = cancel.Token;
            WriteableBitmap? nextLeft = null, nextRight = null, nextDifference = null;
            try
            {
                var decoded = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var a = cachedLeft ?? left.Decode(leftFrame, token);
                    var b = cachedRight ?? right.Decode(rightFrame, token);
                    var comparison = ImageComparisonEngine.ComparePixels(a, b, threshold, true, token);
                    return (Left: a, Right: b, Comparison: comparison);
                }, token);
                token.ThrowIfCancellationRequested();
                if (_disposed || generation != _generation) throw new OperationCanceledException(token);
                nextLeft = CreateBitmap(decoded.Left.Width, decoded.Left.Height, decoded.Left.Pixels, token);
                nextRight = CreateBitmap(decoded.Right.Width, decoded.Right.Height, decoded.Right.Pixels, token);
                nextDifference = CreateBitmap(decoded.Comparison.Width, decoded.Comparison.Height, decoded.Comparison.DifferencePixels!, token);
                token.ThrowIfCancellationRequested();
                if (_disposed || generation != _generation) throw new OperationCanceledException(token);
                var oldLeft = _leftBitmap; var oldRight = _rightBitmap; var oldDifference = _differenceBitmap;
                _leftBitmap = nextLeft; _rightBitmap = nextRight; _differenceBitmap = nextDifference;
                _leftImage.Source = _overlayLeft.Source = nextLeft;
                _rightImage.Source = _overlayRight.Source = nextRight;
                _difference.Source = nextDifference;
                nextLeft = nextRight = nextDifference = null;
                _leftDecoded = decoded.Left; _rightDecoded = decoded.Right;
                _displayThreshold = threshold;
                LeftFrame = leftFrame; RightFrame = rightFrame;
                DifferentPixels = decoded.Comparison.DifferentPixels; TotalPixels = decoded.Comparison.TotalPixels;
                RestoreSelectors(); UpdateZoom();
                _status.Text = $"左 {decoded.Left.Width}×{decoded.Left.Height} / 右 {decoded.Right.Width}×{decoded.Right.Height} · 差分 {DifferentPixels:N0} / {TotalPixels:N0} px";
                oldLeft?.Dispose(); oldRight?.Dispose(); oldDifference?.Dispose();
            }
            finally
            {
                nextLeft?.Dispose(); nextRight?.Dispose(); nextDifference?.Dispose();
                if (ReferenceEquals(_operationCancellation, cancel)) _operationCancellation = null;
                cancel.Dispose();
            }
        }

        private static WriteableBitmap CreateBitmap(int width, int height, byte[] pixels, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            try
            {
                using var buffer = bitmap.Lock();
                for (var y = 0; y < height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    Marshal.Copy(pixels, y * width * 4, IntPtr.Add(buffer.Address, y * buffer.RowBytes), width * 4);
                }
                token.ThrowIfCancellationRequested();
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }

        private void RestoreSelectors()
        {
            _updatingSelectors = true;
            try { _leftSelector.Value = LeftFrame; _rightSelector.Value = RightFrame; }
            finally { _updatingSelectors = false; }
            UpdateNavigation();
        }

        private void UpdateNavigation()
        {
            var left = (int)Math.Clamp(_leftSelector.Value ?? LeftFrame, 1, LeftFrameCount);
            var right = (int)Math.Clamp(_rightSelector.Value ?? RightFrame, 1, RightFrameCount);
            _leftPosition.Text = $"左 {LeftFrame}/{LeftFrameCount}"; _rightPosition.Text = $"右 {RightFrame}/{RightFrameCount}";
            _previousLeft.IsEnabled = left > 1; _nextLeft.IsEnabled = left < LeftFrameCount;
            _previousRight.IsEnabled = right > 1; _nextRight.IsEnabled = right < RightFrameCount;
            _previousBoth.IsEnabled = Math.Max(left, right) > 1;
            _nextBoth.IsEnabled = Math.Max(left, right) < Math.Max(LeftFrameCount, RightFrameCount);
        }

        private void UpdateZoom()
        {
            if (_disposed || _leftDecoded is null || _rightDecoded is null) return;
            _leftImage.Width = _overlayLeft.Width = _leftDecoded.Width * _zoom.Value;
            _leftImage.Height = _overlayLeft.Height = _leftDecoded.Height * _zoom.Value;
            _rightImage.Width = _overlayRight.Width = _rightDecoded.Width * _zoom.Value;
            _rightImage.Height = _overlayRight.Height = _rightDecoded.Height * _zoom.Value;
            _overlay.Width = _difference.Width = Math.Max(_leftDecoded.Width, _rightDecoded.Width) * _zoom.Value;
            _overlay.Height = _difference.Height = Math.Max(_leftDecoded.Height, _rightDecoded.Height) * _zoom.Value;
        }

        private void OwnerClosed(object? sender, EventArgs e) => Dispose();
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _generation++;
            _lifetime.Cancel(); _operationCancellation?.Cancel();
            if (_owner is not null) { _owner.Closed -= OwnerClosed; _owner = null; }
            _leftImage.Source = _rightImage.Source = _overlayLeft.Source = _overlayRight.Source = _difference.Source = null;
            _leftBitmap?.Dispose(); _rightBitmap?.Dispose(); _differenceBitmap?.Dispose();
            _leftBitmap = _rightBitmap = _differenceBitmap = null;
            _leftDecoded = _rightDecoded = null;
            _leftSnapshot = _rightSnapshot = null;
            _lifetime.Dispose();
        }
    }
    public sealed class BinaryPanel : DockPanel
    {
        public bool LeftReadOnly { get; set; }
        public bool RightReadOnly { get; set; }
        public Action? ApplyReadOnly { get; set; }
        internal Func<bool, string, CancellationToken, Task>? SaveContent { get; set; }
        public Task SaveToAsync(bool rightSide, string path, CancellationToken token = default) => SaveContent?.Invoke(rightSide, path, token) ?? throw new InvalidOperationException("バイナリを読み込んでいません。");
        public Func<bool>? IsDirty { get; set; }
        public Action? MarkClean { get; set; }
    }
    private static async Task<byte[]> ReadBinaryAsync(string path, int limit, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        using var output = new MemoryStream(); await CopyBoundedAsync(input, output, limit, token); return output.ToArray();
    }
    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        var buffer = new byte[64 * 1024]; long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, token); if (count == 0) return;
            total += count; if (total > limit) throw new InvalidDataException("ファイルの読込サイズが上限を超えました。");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }
    public static void SetProjectReadOnly(Control? control, bool left, bool right)
    {
        if (control is BinaryPanel panel) { panel.LeftReadOnly = left; panel.RightReadOnly = right; panel.ApplyReadOnly?.Invoke(); }
    }
    private static TextBox HexEditor() => new() { AcceptsReturn = true, AcceptsTab = true, FontFamily = Mono, TextWrapping = TextWrapping.NoWrap, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private static string Hex(ReadOnlySpan<byte> bytes)
    { var text = new StringBuilder(); for (var i = 0; i < bytes.Length; i++) { text.Append(bytes[i].ToString("X2")); text.Append((i + 1) % 16 == 0 ? '\n' : ' '); } return text.ToString(); }
    private static ScrollViewer Scroll(Control control) => new() { Content = control, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    private static Grid Pair(Control left, Control right)
    { var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 }; grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right); return grid; }
    private static void AddButton(Panel panel, string title, Func<Task> action)
    {
        var button = new Button { Content = title, Margin = new Thickness(4) };
        button.Click += async (_, _) =>
        {
            try { button.IsEnabled = false; await action(); }
            catch (Exception ex)
            {
                if (TopLevel.GetTopLevel(button) is Window owner)
                { var dialog = new Window { Title = "操作エラー", Width = 500, Height = 180, Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20) } }; await dialog.ShowDialog(owner); }
            }
            finally { button.IsEnabled = true; }
        };
        panel.Children.Add(button);
    }
    private static async Task PickBinaryOutputAsync(BinaryPanel control, bool rightSide, string name)
    {
        var top = TopLevel.GetTopLevel(control); if (top is null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = name, Title = "バイナリを別名保存" });
        if (file is null) return;
        if (file.TryGetLocalPath() is not string path) throw new IOException("ローカルの保存先を選択してください。");
        await control.SaveToAsync(rightSide, path);
    }
    private static async Task WriteBinaryCopyAsync(string path, byte[] bytes, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(path);
        void ValidateOutput()
        {
            for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
            {
                try
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("リンクを経由して保存できません。");
                    if (current == fullPath && (attributes & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("読取り専用ファイルへ保存できません。");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        token.ThrowIfCancellationRequested(); ValidateOutput();
        var exists = File.Exists(fullPath);
        var attributes = exists ? File.GetAttributes(fullPath) : FileAttributes.Normal;
        UnixFileMode? mode = exists && !OperatingSystem.IsWindows() ? File.GetUnixFileMode(fullPath) : null;
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, ".diffbeacon-binary-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true); }
            if (mode.HasValue && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, mode.Value);
            if (OperatingSystem.IsWindows() && exists)
            { var preserved = attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed); File.SetAttributes(temporary, preserved == 0 ? FileAttributes.Normal : preserved); }
            ValidateOutput(); token.ThrowIfCancellationRequested();
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null); else File.Move(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
