using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

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
        private double _requestedHighlightAlpha = .7, _displayHighlightAlpha = .7;
        private int _requestedBlockSize = 8, _displayBlockSize = 8;
        private int _requestedInsertionDeletionMode, _displayInsertionDeletionMode;
        private readonly ComboBox _insertionDeletionMode = new() { Name = "ImageInsertionDeletionMode", ItemsSource = new[] { "なし", "縦方向", "横方向" }, SelectedIndex = 0, MinWidth = 100 };
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
        private readonly Slider _highlightAlpha = new() { Name = "ImageHighlightAlpha", Minimum = 0, Maximum = 1, Value = .7, Width = 130 };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TabControl _imageViews = new() { Name = "ImageDisplayMode" };
        private readonly ScrollViewer _toolbarScroll = new() { Name = "ImageToolbar", MaxHeight = 130,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        private readonly StackPanel _imageFooter = new() { Margin = new Thickness(8), Spacing = 4 };
        private readonly List<TextBlock> _paneCaptions = [];

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
        internal IReadOnlyList<ImageComparisonEngine.DecodedFrame> RenderedFrames => _wipeRendered ?? _rendered ?? [];
        internal Task CurrentFrameOperation { get; private set; } = Task.CompletedTask;
        // headless操作検証では実計算後の採用待ちを再現し、候補画素や検査を差し替えない。
        internal Func<Task>? FrameCandidateReady { get; set; }
        internal bool ReportAllFrames { get => _reportAllFrames.IsChecked == true; set => _reportAllFrames.IsChecked = value; }

        internal ImageComparisonEngine.ReportInput CaptureReport()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_operationCancellation is not null || _saving || _decoded is null || HasFloatingImage || _clipboardBusy)
                throw new InvalidOperationException("画像フレームの表示が完了してからレポートを生成してください。");
            var display = AdoptedDisplay ?? throw new InvalidOperationException("確定画像の表示がありません。");
            var capture = new ImageReportDisplayCapture(display, _displayComparison!, _numbers, _snapshots!, CaptureSettings());
            return new(_snapshots!.ToArray(), display.Settings.Threshold, ReportAllFrames ? null : capture.FrameNumbers,
                display.Settings.SelectedDiffIndex, display.Settings.ShowDifferences, _editSession?.CaptureFrames(),
                _displayOrientations.ToArray(), display.Settings.BlockSize, _displayOffsets.ToArray(), _displayInsertionDeletionMode,
                display.Settings.HighlightAlpha, display.Sample.Wipe, capture);
        }

        internal ImagePanel(ImageComparisonEngine.Snapshot left, ImageComparisonEngine.Snapshot right, ImageComparisonEngine.Snapshot? middle = null,
            ImageApplicationOptionsStore? applicationOptions = null)
        {
            _applicationOptions = applicationOptions ?? new();
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
                _positions[pane] = new(); _images[pane] = new() { Name = "ImagePane" + names[pane], Stretch = Stretch.Fill,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                foreach (var control in new Control[] { FrameButton("ImagePrevious" + names[pane], labels[pane] + " ◀", pane, -1), _selectors[pane],
                    FrameButton("ImageNext" + names[pane], labels[pane] + " ▶", pane, 1), _positions[pane] }) Add(frameControls, control);
                var column = new DockPanel(); var caption = new TextBlock { Text = labels[pane], Margin = new Thickness(4) };
                _paneCaptions.Add(caption);
                DockPanel.SetDock(caption, Dock.Top); column.Children.Add(caption); column.Children.Add(AttachImageScroll(pane, AttachRectanglePane(pane)));
                Grid.SetColumn(column, pane); side.Children.Add(column);
                _selectors[pane].ValueChanged += async (_, _) => { if (!_updatingSelectors && !_disposed) await SelectFromControlsAsync(); };
            }
            Add(frameControls, FrameButton("ImagePreviousBoth", "同期 ◀", -1, -1)); Add(frameControls, FrameButton("ImageNextBoth", "同期 ▶", -1, 1));
            toolbar.Children.Add(frameControls);
            var settings = new WrapPanel();
            foreach (var control in new Control[] { new TextBlock { Text = "倍率" }, _zoom,
                new TextBlock { Text = "差分色の不透明度" }, _highlightAlpha,
                new TextBlock { Text = "差分閾値" }, _threshold, new TextBlock { Text = "挿入・削除" }, _insertionDeletionMode, _showDifferences, _reportAllFrames }) Add(settings, control);
            toolbar.Children.Add(settings);
            toolbar.Children.Add(CreateOverlayControls());
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
            _toolbarScroll.Content = toolbar;
            DockPanel.SetDock(_toolbarScroll, Dock.Top); Children.Add(_toolbarScroll);
            _imageFooter.Children.Add(_status); _imageFooter.Children.Add(new TextBlock { Text = "領域は8近傍でまとめます。中央は第三の比較画像です。画素差の表示は左と右を比較します。", TextWrapping = TextWrapping.Wrap });
            DockPanel.SetDock(_imageFooter, Dock.Bottom); Children.Add(_imageFooter);
            _imageViews.ItemsSource = new[] { new TabItem { Header = _counts.Length == 3 ? "左・中央・右" : "左右", Content = side },
                new TabItem { Header = "左右の画素差", Content = Scroll(_difference) } };
            _imageViews.SelectedIndex = 0;
            Children.Add(_imageViews);
            InitializeDragOptions(); InitializeOverlay();
            _zoom.ValueChanged += (_, _) => { PreserveWipeOrCancelDrag(); UpdateZoom(); };
            _highlightAlpha.ValueChanged += async (_, _) =>
            {
                if (_updatingSelectors || _disposed) return;
                _requestedHighlightAlpha = _highlightAlpha.Value;
                await SelectFromControlsAsync(preserveRectangle: true);
            };
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
            _insertionDeletionMode.SelectionChanged += async (_, _) =>
            {
                if (_updatingSelectors || _disposed) return;
                if (_insertionDeletionMode.SelectedIndex is < 0 or > 2) { RestoreSelectors(); return; }
                _requestedInsertionDeletionMode = _insertionDeletionMode.SelectedIndex;
                await SelectFromControlsAsync();
            };
            AttachedToVisualTree += (_, _) => { if (!_disposed && _owner is null && TopLevel.GetTopLevel(this) is Window owner) { _owner = owner; owner.Closed += OwnerClosed; AttachRectangleOwner(owner); } };
            UpdateNavigation();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // 小さいパネルでは縦の余白だけを詰め、テーマ差があっても画像領域を残す。
            // 測定の前に切り替え、切り替える前のDesiredSizeを再利用しない。
            var compact = double.IsFinite(availableSize.Height) && availableSize.Height < 300;
            _imageFooter.Margin = new Thickness(8, compact ? 0 : 8);
            _imageFooter.Spacing = compact ? 0 : 4;
            foreach (var caption in _paneCaptions) caption.Margin = new Thickness(4, compact ? 0 : 4);
            var measured = base.MeasureOverride(availableSize);
            // テーマの操作部品より短いviewportではBringIntoViewでも全体を表示できない。
            // 初回の測定値から下限を決め、同じ測定内の再計算は一度だけにする。
            var controlHeight = _toolbarScroll.GetVisualDescendants().OfType<Control>()
                .Where(control => control is Slider or Button or ComboBox or NumericUpDown or CheckBox)
                .Select(control => control.DesiredSize.Height).DefaultIfEmpty(0).Max();
            var proportionalHeight = double.IsFinite(availableSize.Height) ? availableSize.Height * .2 : 130;
            // 内容の外側余白は一緒にスクロールするため、viewportの下限へ加えない。
            var maxHeight = Math.Max(Math.Max(32, proportionalHeight), controlHeight);
            if (Math.Abs(_toolbarScroll.MaxHeight - maxHeight) > .01)
            {
                _toolbarScroll.MaxHeight = maxHeight;
                measured = base.MeasureOverride(availableSize);
            }
            return measured;
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

        private async Task SelectFromControlsAsync(bool preserveRectangle = false)
        {
            var generation = _generation;
            try { var task = SetNumbersAsync(ReadNumbers(), CancellationToken.None, preserveRectangle: preserveRectangle); generation = _generation; UpdateNavigation(); await task; }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!_disposed && generation == _generation) { RestoreSelectors(); _status.Text = "画像を表示できません: " + error.Message; } }
        }

        internal Task SetFramesAsync(int left, int right, CancellationToken token = default)
            => SetNumbersAsync(_counts.Length == 3 ? [left, MiddleFrame!.Value, right] : [left, right], token);
        internal Task SetFramesAsync(int left, int middle, int right, CancellationToken token = default) => SetNumbersAsync([left, middle, right], token);

        private Task SetNumbersAsync(int[] numbers, CancellationToken token, int? requestedSelection = null, bool preserveRectangle = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); ImageComparisonEngine.ValidateSelection(_snapshots!, numbers, _requestedOrientations, _requestedOffsets); token.ThrowIfCancellationRequested();
            if (_saving) throw new InvalidOperationException("画像の保存が完了してから表示を変更してください。");
            PreserveWipeOrCancelDrag();
            // 差分色だけの再描画は原本RefreshImagesと同様に選択・浮動貼り付けを保持する。
            // 同時にページや座標系が変わる場合は従来どおり作業状態を解除する。
            var sameDisplayCoordinates = !_resetEditing && numbers.SequenceEqual(_numbers)
                && _requestedOrientations.SequenceEqual(_displayOrientations) && _requestedOffsets.SequenceEqual(_displayOffsets)
                && _requestedThreshold == _displayThreshold && _requestedBlockSize == _displayBlockSize
                && _requestedInsertionDeletionMode == _displayInsertionDeletionMode;
            if (!preserveRectangle || !sameDisplayCoordinates) CancelRectangleInteraction(preservePointerPress: IsWipePress);
            _operationCancellation?.Cancel(); var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token); _operationCancellation = cancel;
            _displayCandidateCancellation?.Cancel();
            _displayStateCompletion?.TrySetResult();
            CurrentFrameOperation = LoadFramesAsync(numbers, _requestedThreshold, _showDifferences.IsChecked == true, ++_generation, cancel, requestedSelection: requestedSelection);
            UpdateEditControls();
            return CurrentFrameOperation;
        }

        private async Task LoadFramesAsync(int[] numbers, double threshold, bool show, long generation, CancellationTokenSource cancel,
            Action<ImageEditSession, CancellationToken>? edit = null, int writablePane = -1, int? requestedSelection = null)
        {
            var token = cancel.Token; var snapshots = _snapshots!; var cached = _rawDecoded; var selected = requestedSelection ?? _selectedDiffIndex;
            var candidateReady = FrameCandidateReady;
            var orientations = _requestedOrientations.ToArray();
            var offsets = _requestedOffsets.ToArray();
            var blockSize = _requestedBlockSize;
            var highlightAlpha = _requestedHighlightAlpha;
            var insertionDeletionMode = _requestedInsertionDeletionMode;
            var candidateSession = _resetEditing ? null : _editSession?.Fork();
            var readOnly = _readOnly.ToArray(); var reset = _resetEditing; var adopted = false;
            var next = new WriteableBitmap?[numbers.Length]; WriteableBitmap? nextDifference = null;
            WriteableBitmap[]? wipeNext = null;
            try
            {
                ImageComparisonEngine.ValidateHighlightAlpha(highlightAlpha);
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
                        candidateSession.SetInsertionDeletionMode(insertionDeletionMode, token);
                        edit?.Invoke(candidateSession, token);
                        offsets = candidateSession.CaptureOffsets().ToArray();
                        frames = candidateSession.CaptureAlignedFrames().ToArray();
                        comparison = new(frames, candidateSession.Regions, ImageComparisonEngine.ComparePixels(frames[0], frames[^1], threshold, true, token, offsets[0], offsets[^1]), candidateSession.CaptureFrames(), Alignment: candidateSession.Alignment);
                    }
                    else
                    {
                        if (edit is not null) throw new InvalidOperationException("画像コピーとPNG保存は静止画の比較で使用してください。");
                        for (var i = 0; i < frames.Length; i++) { token.ThrowIfCancellationRequested(); frames[i] = cached is not null && cached[i].Number == numbers[i] ? cached[i] : snapshots[i].Decode(numbers[i], token); }
                        comparison = ImageComparisonEngine.CompareDecoded(frames, threshold, true, token, orientations, blockSize, offsets, insertionDeletionMode);
                        frames = comparison.Frames.ToArray();
                    }
                    var selection = Math.Min(selected, comparison.Regions.Regions.Count - 1);
                    var rendered = ImageRegionRenderer.Render(frames, comparison.Regions, blockSize: blockSize, highlightAlpha: highlightAlpha, selectedDiffIndex: selection, token: token, showDifferences: show,
                        alignment: comparison.Alignment).ToArray();
                    return (comparison, rendered, selection);
                }, token);
                if (edit is not null && EditCandidateReady is { } ready) await ready();
                if (candidateReady is not null) await candidateReady();
                token.ThrowIfCancellationRequested(); if (_disposed || generation != _generation) throw new OperationCanceledException(token);
                var display = await PrepareFrameDisplayAsync(result.comparison, result.rendered, result.selection, threshold,
                    show, blockSize, highlightAlpha, generation, token);
                var wipe = display.Display.Sample.Wipe;
                var wipeFrames = ReferenceEquals(display.Display.Frames, result.rendered) ? null : display.Display.Frames.ToArray();
                if (wipeFrames is not null)
                {
                    wipeNext = new WriteableBitmap[wipeFrames.Length];
                    for (var i = 0; i < wipeFrames.Length; i++) wipeNext[i] = CreateBitmap(wipeFrames[i], token);
                }
                for (var i = 0; i < next.Length; i++) next[i] = CreateBitmap(result.rendered[i], token);
                var pixels = result.comparison.Pixels;
                nextDifference = CreateBitmap(new(1, pixels.Width, pixels.Height, pixels.DifferencePixels!), token);
                token.ThrowIfCancellationRequested(); if (_disposed || generation != _generation) throw new OperationCanceledException(token);
                if (writablePane >= 0 && _readOnly[writablePane]) throw new InvalidOperationException("コピー先は読取り専用です。");
                if (candidateSession is not null) for (var pane = 0; pane < _readOnly.Length; pane++) candidateSession.SetReadOnly(pane, _readOnly[pane]);
                var old = _bitmaps; var oldDifference = _differenceBitmap;
                _bitmaps = next.Select(bitmap => bitmap!).ToArray(); _differenceBitmap = nextDifference;
                for (var i = 0; i < next.Length; i++) _images[i].Source = next[i];
                _difference.Source = nextDifference; Array.Fill(next, null); nextDifference = null;
                _decoded = result.comparison.Frames.ToArray(); _regions = result.comparison.Regions; _rendered = result.rendered; _selectedDiffIndex = result.selection;
                _displayCanvasWork = (long)_rendered[0].Width * _rendered[0].Height * (numbers.Length + 1);
                _displayComparison = result.comparison; _overlayPrepared = display.Prepared; _overlayPreparationWork = display.PreparationWork;
                if (_wipeBitmaps is not null) foreach (var bitmap in _wipeBitmaps) bitmap.Dispose();
                _wipeBitmaps = wipeNext; _wipeRendered = wipeFrames;
                _activeWipe = wipe;
                // 確定表示は旧activeで維持し、待機中の最新要求は新canvasへ引き継ぐ。
                _requestedWipe = _requestedWipe?.Clamp(_rendered[0].Width, _rendered[0].Height);
                if (wipeNext is not null) for (var i = 0; i < _images.Length; i++) _images[i].Source = wipeNext[i];
                wipeNext = null;
                if (IsWipePress) _displayDragGeneration = generation;
                _rawDecoded = (result.comparison.OriginalFrames ?? result.comparison.Frames).ToArray(); _displayOrientations = orientations; _displayBlockSize = blockSize;
                _displayOffsets = offsets;
                _displayInsertionDeletionMode = insertionDeletionMode;
                _editSession = candidateSession; _resetEditing = _discarded = false; adopted = true;
                _displayThreshold = threshold; _displayShowDifferences = show;
                _displayHighlightAlpha = highlightAlpha;
                _adoptedOpacity = display.Settings.Alpha; _adoptedOpacityExplicit = display.OpacityExplicit;
                AdoptedDisplay = new(generation, display.Revision, display.Display.Frames, display.Display.Sample, display.Settings);
                _displayError = null;
                if (_displayCompletionRevision == display.Revision) _displayStateCompletion?.TrySetResult();
                numbers.CopyTo(_numbers, 0); DifferentPixels = pixels.DifferentPixels; TotalPixels = pixels.TotalPixels;
                RestoreSelectors(); UpdateZoom();
                _status.Text = $"領域 {DifferenceCount} 個" + (_counts.Length == 3 ? $" · 競合 {ConflictCount} 個" : "")
                    + (_selectedDiffIndex >= 0 ? $" · 選択 {_selectedDiffIndex + 1}/{DifferenceCount}" : "") + $" · 左右の画素差 {DifferentPixels:N0}/{TotalPixels:N0} px";
                if (old is not null) foreach (var bitmap in old) bitmap.Dispose(); oldDifference?.Dispose();
                _displayRefreshPending = false;
                if (_requestedWipe != _activeWipe) RequestDisplayRefresh();
                UpdateOverlayTimer();
            }
            catch (Exception error)
            {
                if (!_disposed && generation == _generation && _displayCompletionRevision == _wipeRevision)
                {
                    if (error is OperationCanceledException) _displayStateCompletion?.TrySetCanceled(token);
                    else _displayStateCompletion?.TrySetException(error);
                }
                throw;
            }
            finally
            {
                foreach (var bitmap in next) bitmap?.Dispose(); nextDifference?.Dispose();
                if (wipeNext is not null) foreach (var bitmap in wipeNext) bitmap?.Dispose();
                if (ReferenceEquals(_operationCancellation, cancel)) _operationCancellation = null;
                cancel.Dispose();
                if (!_disposed && generation == _generation)
                {
                    if (!adopted && _decoded is not null)
                    {
                        _displayRefreshPending = false;
                        if (IsWipePress) _displayDragGeneration = generation;
                        if (reset) _resetEditing = _discarded = false;
                        _updatingSelectors = true;
                        try { _threshold.Value = ThresholdControlValue(_displayThreshold); _showDifferences.IsChecked = _displayShowDifferences; }
                        finally { _updatingSelectors = false; }
                        RestoreSelectors();
                    }
                    UpdateEditControls();
                    UpdateOverlayTimer();
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
                _requestedHighlightAlpha = _displayHighlightAlpha; _highlightAlpha.Value = _displayHighlightAlpha;
                _requestedBlockSize = _displayBlockSize; _blockSizeControl.Value = _displayBlockSize;
                _requestedInsertionDeletionMode = _displayInsertionDeletionMode; _insertionDeletionMode.SelectedIndex = _displayInsertionDeletionMode;
                _requestedOrientations = _displayOrientations.ToArray();
                _requestedOffsets = _displayOffsets.ToArray();
                _threshold.Value = ThresholdControlValue(_displayThreshold); _showDifferences.IsChecked = _displayShowDifferences;
            }
            finally { _updatingSelectors = false; }
            RestoreOverlayControls();
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
            _insertionDeletionMode.IsEnabled = !_saving;
            UpdateEditControls();
            UpdateDragControls();
        }
        private void UpdateZoom()
        {
            if (_disposed || _rendered is null) return;
            for (var i = 0; i < _images.Length; i++) { _images[i].Width = _rendered[i].Width * _zoom.Value; _images[i].Height = _rendered[i].Height * _zoom.Value; }
            if (_decoded is not null) { _difference.Width = Math.Max(_decoded[0].Width, _decoded[^1].Width) * _zoom.Value; _difference.Height = Math.Max(_decoded[0].Height, _decoded[^1].Height) * _zoom.Value; }
            UpdateRectangleVisuals(); UpdateWipeGuide();
        }
        private void OwnerClosed(object? sender, EventArgs e) => Dispose();
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; _generation++; _lifetime.Cancel(); _operationCancellation?.Cancel();
            CancelDisplayDrag(); CancelRectangleInteraction();
            _applicationOptions.Changed -= ApplicationDragModeChanged;
            if (_optionsGuard is not null) _applicationOptions.RemoveOutputGuard(_optionsGuard);
            _saveCancellation?.Cancel(); _editSession = null;
            if (_owner is not null) { _owner.Closed -= OwnerClosed; DetachRectangleOwner(_owner); _owner = null; }
            DisposeOverlay();
            foreach (var image in _images) image.Source = null; _difference.Source = null;
            if (_bitmaps is not null) foreach (var bitmap in _bitmaps) bitmap.Dispose(); _differenceBitmap?.Dispose();
            _bitmaps = null; _differenceBitmap = null; _decoded = _rendered = _rawDecoded = null; _regions = null; _snapshots = null; _lifetime.Dispose();
        }
    }
}
