using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DiffBeacon.App;

public static partial class SpecializedViews
{
    public sealed partial class ImagePanel : DockPanel, IDisposable
    {
        private ImageComparisonEngine.Snapshot[]? _snapshots;
        private ImageComparisonEngine.DecodedFrame[]? _decoded, _rendered;
        private ImageRegionDiffer.Result? _regions;
        private readonly int[] _counts, _numbers;
        private readonly NumericUpDown[] _selectors;
        private readonly TextBlock[] _positions;
        private readonly Image[] _images;
        private readonly List<(Image Image, int Pane)> _overlays = [];
        private readonly List<Grid> _overlayGrids = [];
        private WriteableBitmap[]? _bitmaps;
        private WriteableBitmap? _differenceBitmap;
        private readonly Image _difference = new() { Stretch = Stretch.Fill };
        private readonly List<(Button Button, int Pane, int Direction)> _frameButtons = [];
        private readonly CancellationTokenSource _lifetime = new();
        private CancellationTokenSource? _operationCancellation;
        private long _generation;
        private bool _disposed, _updatingSelectors;
        private Window? _owner;
        private double _displayThreshold;
        private double _requestedThreshold;
        private int _requestedBlockSize = 8, _displayBlockSize = 8;
        private readonly NumericUpDown _blockSizeControl = new() { Name = "ImageBlockSize", Minimum = 1, Maximum = 256, Value = 8, Increment = 1, Width = 90 };
        private bool _displayShowDifferences = true;
        private ImageOrientation[] _requestedOrientations = [];
        private ImageOrientation[] _displayOrientations = [];
        private ImageOffset[] _requestedOffsets = [], _displayOffsets = [];
        private ImageComparisonEngine.DecodedFrame[]? _rawDecoded;
        private int _selectedDiffIndex = -1;
        private readonly NumericUpDown _threshold = new() { Name = "ImageThreshold", Minimum = 0, Maximum = 510, Value = 0, Increment = 1, Width = 120 };
        private readonly CheckBox _reportAllFrames = new() { Name = "ImageReportAllFrames", Content = "レポートは全フレーム", IsChecked = true };
        private readonly CheckBox _showDifferences = new() { Name = "ImageShowDifferences", Content = "差分を強調", IsChecked = true };
        private readonly Slider _zoom = new() { Name = "ImageZoom", Minimum = .1, Maximum = 8, Value = 1, Width = 130 };
        private readonly Slider _opacity = new() { Name = "ImageOpacity", Minimum = 0, Maximum = 1, Value = .3, Width = 130 };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TabControl _imageViews = new() { Name = "ImageDisplayMode" };

        internal int LeftFrame => _numbers[0];
        internal int RightFrame => _numbers[^1];
        internal int? MiddleFrame => _numbers.Length == 3 ? _numbers[1] : null;
        internal int LeftFrameCount => _counts[0];
        internal int RightFrameCount => _counts[^1];
        internal int? MiddleFrameCount => _counts.Length == 3 ? _counts[1] : null;
        internal long DifferentPixels { get; private set; }
        internal long TotalPixels { get; private set; }
        internal int DifferenceCount => _regions?.Regions.Count ?? 0;
        internal int ConflictCount => _counts.Length == 3 ? _regions?.ConflictCount ?? 0 : 0;
        internal int SelectedDiffIndex => _selectedDiffIndex;
        internal IReadOnlyList<ImageRegionDiffer.Region> Regions => _regions?.Regions ?? [];
        internal IReadOnlyList<ImageComparisonEngine.DecodedFrame> RenderedFrames => _rendered ?? [];
        internal Task CurrentFrameOperation { get; private set; } = Task.CompletedTask;
        internal bool ReportAllFrames { get => _reportAllFrames.IsChecked == true; set => _reportAllFrames.IsChecked = value; }

        internal ImageComparisonEngine.ReportInput CaptureReport()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_operationCancellation is not null || _saving || _decoded is null)
                throw new InvalidOperationException("画像フレームの表示が完了してからレポートを生成してください。");
            return new(_snapshots!.ToArray(), _displayThreshold, ReportAllFrames ? null : _numbers.ToArray(), _selectedDiffIndex, _displayShowDifferences,
                _editSession?.CaptureFrames(), _displayOrientations.ToArray(), _displayBlockSize, _displayOffsets.ToArray());
        }

        internal ImagePanel(ImageComparisonEngine.Snapshot left, ImageComparisonEngine.Snapshot right, ImageComparisonEngine.Snapshot? middle = null)
        {
            _snapshots = middle is null ? [left, right] : [left, middle, right];
            _counts = _snapshots.Select(image => image.FrameCount).ToArray();
            _requestedOrientations = _counts.Select(_ => new ImageOrientation()).ToArray();
            _displayOrientations = _requestedOrientations.ToArray();
            _requestedOffsets = new ImageOffset[_counts.Length]; _displayOffsets = _requestedOffsets.ToArray();
            _readOnly = new bool[_counts.Length];
            _numbers = Enumerable.Repeat(1, _counts.Length).ToArray();
            _selectors = new NumericUpDown[_counts.Length]; _positions = new TextBlock[_counts.Length]; _images = new Image[_counts.Length];
            var labels = _counts.Length == 3 ? new[] { "左", "中央", "右" } : ["左", "右"];
            var names = _counts.Length == 3 ? new[] { "Left", "Middle", "Right" } : ["Left", "Right"];
            var toolbar = new StackPanel { Margin = new Thickness(8), Spacing = 6 };
            var frameControls = new WrapPanel();
            var side = new Grid { ColumnDefinitions = new ColumnDefinitions(_counts.Length == 3 ? "*,*,*" : "*,*"), ColumnSpacing = 8 };
            for (var pane = 0; pane < _counts.Length; pane++)
            {
                _selectors[pane] = new() { Name = "Image" + names[pane] + "Frame", Minimum = 1, Maximum = _counts[pane], Value = 1, Increment = 1, Width = 110 };
                _positions[pane] = new(); _images[pane] = new() { Name = "ImagePane" + names[pane], Stretch = Stretch.Fill };
                foreach (var control in new Control[] { FrameButton("ImagePrevious" + names[pane], labels[pane] + " ◀", pane, -1), _selectors[pane],
                    FrameButton("ImageNext" + names[pane], labels[pane] + " ▶", pane, 1), _positions[pane] }) Add(frameControls, control);
                var column = new DockPanel(); var caption = new TextBlock { Text = labels[pane], Margin = new Thickness(4) };
                var activePane = pane; _images[pane].PointerPressed += (_, _) => _editPane.SelectedIndex = activePane;
                DockPanel.SetDock(caption, Dock.Top); column.Children.Add(caption); column.Children.Add(Scroll(_images[pane]));
                Grid.SetColumn(column, pane); side.Children.Add(column);
                _selectors[pane].ValueChanged += async (_, _) => { if (!_updatingSelectors && !_disposed) await SelectFromControlsAsync(); };
            }
            Add(frameControls, FrameButton("ImagePreviousBoth", "同期 ◀", -1, -1)); Add(frameControls, FrameButton("ImageNextBoth", "同期 ▶", -1, 1));
            toolbar.Children.Add(frameControls);
            var settings = new WrapPanel();
            foreach (var control in new Control[] { new TextBlock { Text = "倍率" }, _zoom, new TextBlock { Text = "重ね合わせ不透明度" }, _opacity,
                new TextBlock { Text = "差分閾値" }, _threshold, _showDifferences, _reportAllFrames }) Add(settings, control);
            toolbar.Children.Add(settings);
            var navigation = new WrapPanel();
            foreach (var (name, label, direction, conflict) in new[] { ("ImagePreviousDifference", "前の領域", -1, false), ("ImageNextDifference", "次の領域", 1, false),
                ("ImagePreviousConflict", "前の競合", -1, true), ("ImageNextConflict", "次の競合", 1, true) })
            {
                if (conflict && _counts.Length != 3) continue;
                var button = new Button { Name = name, Content = label };
                button.Click += async (_, _) => await NavigateRegionAsync(direction, conflict);
                Add(navigation, button);
            }
            toolbar.Children.Add(navigation); toolbar.Children.Add(CreateEditControls(labels));
            var toolbarScroll = new ScrollViewer { Name = "ImageToolbar", Content = toolbar, MaxHeight = 130,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            SizeChanged += (_, args) => toolbarScroll.MaxHeight = Math.Max(32, args.NewSize.Height * .2);
            DockPanel.SetDock(toolbarScroll, Dock.Top); Children.Add(toolbarScroll);
            var footer = new StackPanel { Margin = new Thickness(8), Spacing = 4 };
            footer.Children.Add(_status); footer.Children.Add(new TextBlock { Text = "領域は8近傍でまとめます。中央は第三の比較画像です。画素差の表示は左と右を比較します。", TextWrapping = TextWrapping.Wrap });
            DockPanel.SetDock(footer, Dock.Bottom); Children.Add(footer);
            var overlays = new Grid { ColumnDefinitions = new ColumnDefinitions(_counts.Length == 3 ? "*,*" : "*"), ColumnSpacing = 8 };
            for (var pane = 0; pane < _counts.Length - 1; pane++)
            {
                var grid = new Grid(); _overlayGrids.Add(grid);
                for (var layer = 0; layer < 2; layer++)
                {
                    var image = new Image { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                        Opacity = layer == 0 ? 1 : _opacity.Value };
                    _overlays.Add((image, pane + layer)); grid.Children.Add(image);
                }
                var scroll = Scroll(grid); Grid.SetColumn(scroll, pane); overlays.Children.Add(scroll);
            }
            _imageViews.ItemsSource = new[] { new TabItem { Header = _counts.Length == 3 ? "左・中央・右" : "左右", Content = side },
                new TabItem { Header = "重ね合わせ", Content = overlays }, new TabItem { Header = "左右の画素差", Content = Scroll(_difference) } };
            _imageViews.SelectedIndex = 0;
            Children.Add(_imageViews);
            _zoom.ValueChanged += (_, _) => UpdateZoom();
            _opacity.ValueChanged += (_, _) => { for (var i = 1; i < _overlays.Count; i += 2) _overlays[i].Image.Opacity = _opacity.Value; };
            _threshold.ValueChanged += async (_, _) =>
            {
                if (_updatingSelectors || _disposed) return;
                _requestedThreshold = (double)(_threshold.Value ?? 0);
                await SelectFromControlsAsync();
            };
            _blockSizeControl.ValueChanged += async (_, _) =>
            {
                if (_updatingSelectors || _disposed) return;
                var value = _blockSizeControl.Value ?? 8;
                if (decimal.Truncate(value) != value) { RestoreSelectors(); return; }
                _requestedBlockSize = (int)value; await SelectFromControlsAsync();
            };
            _showDifferences.IsCheckedChanged += async (_, _) => { if (!_updatingSelectors && !_disposed) await SelectFromControlsAsync(); };
            AttachedToVisualTree += (_, _) => { if (!_disposed && _owner is null && TopLevel.GetTopLevel(this) is Window owner) { _owner = owner; owner.Closed += OwnerClosed; } };
            UpdateNavigation();
        }

        private static void Add(Panel panel, Control control) { control.Margin = new Thickness(0, 0, 8, 0); panel.Children.Add(control); }
        private int[] ReadNumbers() => _selectors.Select((selector, i) =>
        {
            if (selector.Value is not decimal value || value != decimal.Truncate(value) || value < 1 || value > _counts[i])
                throw new ArgumentException("画像フレーム番号は範囲内の整数です。");
            return (int)value;
        }).ToArray();

        private Button FrameButton(string name, string label, int pane, int direction)
        {
            var button = new Button { Name = name, Content = label }; _frameButtons.Add((button, pane, direction));
            button.Click += async (_, _) =>
            {
                if (_disposed) return;
                var numbers = ReadNumbers();
                if (pane < 0)
                {
                    var target = numbers.Max() + direction;
                    if (target < 1 || target > _counts.Max()) return;
                    // 原本SetCurrentPageAllどおり、対象ページがない入力は以前の選択を保持。
                    for (var i = 0; i < numbers.Length; i++) if (target <= _counts[i]) numbers[i] = target;
                }
                else { numbers[pane] += direction; if (numbers[pane] < 1 || numbers[pane] > _counts[pane]) return; }
                _updatingSelectors = true; try { for (var i = 0; i < numbers.Length; i++) _selectors[i].Value = numbers[i]; } finally { _updatingSelectors = false; }
                await SelectFromControlsAsync();
            };
            return button;
        }

        private async Task SelectFromControlsAsync()
        {
            var generation = _generation;
            try { var task = SetNumbersAsync(ReadNumbers(), CancellationToken.None); generation = _generation; UpdateNavigation(); await task; }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!_disposed && generation == _generation) { RestoreSelectors(); _status.Text = "画像を表示できません: " + error.Message; } }
        }

        internal Task SetFramesAsync(int left, int right, CancellationToken token = default)
            => SetNumbersAsync(_counts.Length == 3 ? [left, MiddleFrame!.Value, right] : [left, right], token);
        internal Task SetFramesAsync(int left, int middle, int right, CancellationToken token = default) => SetNumbersAsync([left, middle, right], token);

        private Task SetNumbersAsync(int[] numbers, CancellationToken token, int? requestedSelection = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); ImageComparisonEngine.ValidateSelection(_snapshots!, numbers, _requestedOrientations, _requestedOffsets); token.ThrowIfCancellationRequested();
            if (_saving) throw new InvalidOperationException("画像の保存が完了してから表示を変更してください。");
            _operationCancellation?.Cancel(); var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token); _operationCancellation = cancel;
            CurrentFrameOperation = LoadFramesAsync(numbers, _requestedThreshold, _showDifferences.IsChecked == true, ++_generation, cancel, requestedSelection: requestedSelection);
            UpdateEditControls();
            return CurrentFrameOperation;
        }

        private async Task LoadFramesAsync(int[] numbers, double threshold, bool show, long generation, CancellationTokenSource cancel,
            Action<ImageEditSession, CancellationToken>? edit = null, int writablePane = -1, int? requestedSelection = null)
        {
            var token = cancel.Token; var snapshots = _snapshots!; var cached = _rawDecoded; var selected = requestedSelection ?? _selectedDiffIndex;
            var orientations = _requestedOrientations.ToArray();
            var offsets = _requestedOffsets.ToArray();
            var blockSize = _requestedBlockSize;
            var candidateSession = _resetEditing ? null : _editSession?.Fork();
            var readOnly = _readOnly.ToArray(); var reset = _resetEditing; var adopted = false;
            var next = new WriteableBitmap?[numbers.Length]; WriteableBitmap? nextDifference = null;
            try
            {
                var result = await Task.Run(() =>
                {
                    var frames = new ImageComparisonEngine.DecodedFrame[numbers.Length];
                    ImageComparisonEngine.FrameComparison comparison;
                    if (_counts.All(count => count == 1))
                    {
                        if (candidateSession is null)
                        {
                            for (var i = 0; i < frames.Length; i++) { token.ThrowIfCancellationRequested(); frames[i] = !reset && cached is not null ? cached[i] : snapshots[i].Decode(1, token); }
                            candidateSession = new ImageEditSession(frames, readOnly, blockSize, threshold, token);
                        }
                        else if (candidateSession.Threshold != threshold) candidateSession.SetThreshold(threshold, token);
                        candidateSession.SetBlockSize(blockSize, token);
                        candidateSession.SetViewTransforms(orientations, offsets, token);
                        edit?.Invoke(candidateSession, token);
                        offsets = candidateSession.CaptureOffsets().ToArray();
                        frames = candidateSession.CaptureViewFrames().ToArray();
                        comparison = new(frames, candidateSession.Regions, ImageComparisonEngine.ComparePixels(frames[0], frames[^1], threshold, true, token, offsets[0], offsets[^1]), candidateSession.CaptureFrames());
                    }
                    else
                    {
                        if (edit is not null) throw new InvalidOperationException("画像コピーとPNG保存は静止画の比較で使用してください。");
                        for (var i = 0; i < frames.Length; i++) { token.ThrowIfCancellationRequested(); frames[i] = cached is not null && cached[i].Number == numbers[i] ? cached[i] : snapshots[i].Decode(numbers[i], token); }
                        comparison = ImageComparisonEngine.CompareDecoded(frames, threshold, true, token, orientations, blockSize, offsets);
                        frames = comparison.Frames.ToArray();
                    }
                    var selection = Math.Min(selected, comparison.Regions.Regions.Count - 1);
                    var rendered = ImageRegionRenderer.Render(frames, comparison.Regions, blockSize: blockSize, selectedDiffIndex: selection, token: token, showDifferences: show).ToArray();
                    return (comparison, rendered, selection);
                }, token);
                token.ThrowIfCancellationRequested(); if (_disposed || generation != _generation) throw new OperationCanceledException(token);
                for (var i = 0; i < next.Length; i++) next[i] = CreateBitmap(result.rendered[i], token);
                var pixels = result.comparison.Pixels;
                nextDifference = CreateBitmap(new(1, pixels.Width, pixels.Height, pixels.DifferencePixels!), token);
                token.ThrowIfCancellationRequested(); if (_disposed || generation != _generation) throw new OperationCanceledException(token);
                if (writablePane >= 0 && _readOnly[writablePane]) throw new InvalidOperationException("コピー先は読取り専用です。");
                if (candidateSession is not null) for (var pane = 0; pane < _readOnly.Length; pane++) candidateSession.SetReadOnly(pane, _readOnly[pane]);
                var old = _bitmaps; var oldDifference = _differenceBitmap;
                _bitmaps = next.Select(bitmap => bitmap!).ToArray(); _differenceBitmap = nextDifference;
                for (var i = 0; i < next.Length; i++) _images[i].Source = next[i];
                foreach (var overlay in _overlays) overlay.Image.Source = next[overlay.Pane];
                _difference.Source = nextDifference; Array.Fill(next, null); nextDifference = null;
                _decoded = result.comparison.Frames.ToArray(); _regions = result.comparison.Regions; _rendered = result.rendered; _selectedDiffIndex = result.selection;
                _rawDecoded = (result.comparison.OriginalFrames ?? result.comparison.Frames).ToArray(); _displayOrientations = orientations; _displayBlockSize = blockSize;
                _displayOffsets = offsets;
                _editSession = candidateSession; _resetEditing = _discarded = false; adopted = true;
                _displayThreshold = threshold; _displayShowDifferences = show;
                numbers.CopyTo(_numbers, 0); DifferentPixels = pixels.DifferentPixels; TotalPixels = pixels.TotalPixels;
                RestoreSelectors(); UpdateZoom();
                _status.Text = $"領域 {DifferenceCount} 個" + (_counts.Length == 3 ? $" · 競合 {ConflictCount} 個" : "")
                    + (_selectedDiffIndex >= 0 ? $" · 選択 {_selectedDiffIndex + 1}/{DifferenceCount}" : "") + $" · 左右の画素差 {DifferentPixels:N0}/{TotalPixels:N0} px";
                if (old is not null) foreach (var bitmap in old) bitmap.Dispose(); oldDifference?.Dispose();
            }
            finally
            {
                foreach (var bitmap in next) bitmap?.Dispose(); nextDifference?.Dispose();
                if (ReferenceEquals(_operationCancellation, cancel)) _operationCancellation = null;
                cancel.Dispose();
                if (!_disposed && generation == _generation)
                {
                    if (!adopted && _decoded is not null)
                    {
                        if (reset) _resetEditing = _discarded = false;
                        _updatingSelectors = true;
                        try { _threshold.Value = ThresholdControlValue(_displayThreshold); _showDifferences.IsChecked = _displayShowDifferences; }
                        finally { _updatingSelectors = false; }
                        RestoreSelectors();
                    }
                    UpdateEditControls();
                }
            }
        }

        internal async Task NavigateRegionAsync(int direction, bool conflictsOnly = false)
        {
            if (_disposed || _regions is null || _operationCancellation is not null || _saving) return;
            var candidates = _regions.Regions.Select((region, index) => (region, index)).Where(item => !conflictsOnly || _counts.Length == 3 && item.region.Op == 4).Select(item => item.index).ToArray();
            if (candidates.Length == 0) return;
            var position = Array.IndexOf(candidates, _selectedDiffIndex);
            var selected = position < 0 ? direction < 0 ? candidates[^1] : candidates[0] : candidates[(position + Math.Sign(direction) + candidates.Length) % candidates.Length];
            try { await SetNumbersAsync(_numbers.ToArray(), CancellationToken.None, selected); }
            catch (OperationCanceledException) { }
        }

        private static WriteableBitmap CreateBitmap(ImageComparisonEngine.DecodedFrame frame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            try { using var buffer = bitmap.Lock(); for (var y = 0; y < frame.Height; y++) { token.ThrowIfCancellationRequested(); Marshal.Copy(frame.Pixels, y * frame.Width * 4, IntPtr.Add(buffer.Address, y * buffer.RowBytes), frame.Width * 4); } return bitmap; }
            catch { bitmap.Dispose(); throw; }
        }
        private void RestoreSelectors()
        {
            _updatingSelectors = true;
            try
            {
                for (var i = 0; i < _numbers.Length; i++) _selectors[i].Value = _numbers[i];
                _requestedThreshold = _displayThreshold;
                _requestedBlockSize = _displayBlockSize; _blockSizeControl.Value = _displayBlockSize;
                _requestedOrientations = _displayOrientations.ToArray();
                _requestedOffsets = _displayOffsets.ToArray();
                _threshold.Value = ThresholdControlValue(_displayThreshold); _showDifferences.IsChecked = _displayShowDifferences;
            }
            finally { _updatingSelectors = false; }
            UpdateNavigation();
        }
        private void UpdateNavigation()
        {
            for (var i = 0; i < _numbers.Length; i++) _positions[i].Text = $"{_numbers[i]}/{_counts[i]}"
                + (_readOnly[i] ? " · 読取り専用" : "") + (PaneModified(i) ? " · 未保存" : "");
            var numbers = _selectors.Select((selector, i) => (int)(selector.Value ?? _numbers[i])).ToArray();
            foreach (var (button, pane, direction) in _frameButtons) button.IsEnabled = pane < 0 ? direction < 0 ? numbers.Max() > 1 : numbers.Max() < _counts.Max()
                : direction < 0 ? numbers[pane] > 1 : numbers[pane] < _counts[pane];
            if (_saving) foreach (var (button, _, _) in _frameButtons) button.IsEnabled = false;
            foreach (var selector in _selectors) selector.IsEnabled = !_saving;
            _threshold.IsEnabled = _showDifferences.IsEnabled = !_saving;
            _blockSizeControl.IsEnabled = !_saving;
            UpdateEditControls();
        }
        private void UpdateZoom()
        {
            if (_disposed || _rendered is null) return;
            for (var i = 0; i < _images.Length; i++) { _images[i].Width = _rendered[i].Width * _zoom.Value; _images[i].Height = _rendered[i].Height * _zoom.Value; }
            foreach (var (image, pane) in _overlays) { image.Width = _rendered[pane].Width * _zoom.Value; image.Height = _rendered[pane].Height * _zoom.Value; }
            foreach (var grid in _overlayGrids) { grid.Width = _regions!.Width * _zoom.Value; grid.Height = _regions.Height * _zoom.Value; }
            if (_decoded is not null) { _difference.Width = Math.Max(_decoded[0].Width, _decoded[^1].Width) * _zoom.Value; _difference.Height = Math.Max(_decoded[0].Height, _decoded[^1].Height) * _zoom.Value; }
        }
        private void OwnerClosed(object? sender, EventArgs e) => Dispose();
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; _generation++; _lifetime.Cancel(); _operationCancellation?.Cancel();
            _saveCancellation?.Cancel(); _editSession = null;
            if (_owner is not null) { _owner.Closed -= OwnerClosed; _owner = null; }
            foreach (var image in _images) image.Source = null; foreach (var overlay in _overlays) overlay.Image.Source = null; _difference.Source = null;
            if (_bitmaps is not null) foreach (var bitmap in _bitmaps) bitmap.Dispose(); _differenceBitmap?.Dispose();
            _bitmaps = null; _differenceBitmap = null; _decoded = _rendered = _rawDecoded = null; _regions = null; _snapshots = null; _lifetime.Dispose();
        }
    }
}
