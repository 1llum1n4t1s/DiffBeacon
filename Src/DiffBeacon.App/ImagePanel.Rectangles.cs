using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

public static partial class SpecializedViews
{
    public sealed partial class ImagePanel
    {
        // 失敗先行契約: GUI契約JSON。原本GUIは静的照合、画素核はImageRectangles/ImageResize原本観測。
        private readonly List<Canvas> _rectangleLayers = [];
        private readonly List<Border> _selectionBorders = [];
        private readonly List<Image> _floatingImages = [];
        private readonly List<Grid> _rectangleGrids = [];
        private readonly List<EventHandler<PointerPressedEventArgs>> _rectanglePressHandlers = [];
        private readonly List<Border[]> _resizeHandles = [];
        private ImageRectangle?[] _rectangles = [];
        private long _rectangleEpoch;
        private CancellationTokenSource? _clipboardCancellation;
        private bool _clipboardBusy, _draggingRectangle, _draggingFloating, _draggingSelection;
        private long _rectangleDragSerial;
        private long _rectanglePressSerial;
        private IPointer? _pressedRectanglePointer;
        private bool _rectanglePressed;
        private Point _rectangleAnchor, _lastDrag;
        private IPointer? _rectanglePointer;
        private int _dragPane = -1, _floatingPane = -1;
        private int _floatingX, _floatingY;
        private int _resizeMode;
        private ImageRectangle _resizeStart;
        private long _resizeGeneration, _resizeEpoch;
        private ImageComparisonEngine.DecodedFrame? _floatingPayload;
        private WriteableBitmap? _floatingBitmap;
        internal IImageClipboard? ClipboardOverride { get; set; }
        internal Task CurrentRectangleOperation { get; private set; } = Task.CompletedTask;
        internal bool HasFloatingImage => _floatingPayload is not null;
        internal bool HasRectanglePointerCapture => _rectanglePointer?.Captured is not null;
        internal ImageRectangle? RectangleSelection(int pane) => (uint)pane < (uint)_rectangles.Length ? _rectangles[pane] : null;
        internal (int Pane, int X, int Y) FloatingPosition => (_floatingPane, _floatingX, _floatingY);
        private bool RectangleReady => !_disposed && !_saving && !_resetEditing && !_clipboardBusy
            && _editSession is not null && _operationCancellation is null;
        private IImageClipboard Clipboard => ClipboardOverride ?? new ImageClipboard(() => TopLevel.GetTopLevel(this)?.Clipboard,
            () => TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);

        private Grid AttachRectanglePane(int pane)
        {
            if (_rectangles.Length == 0) _rectangles = new ImageRectangle?[_counts.Length];
            var layer = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
            var border = new Border { BorderBrush = Brushes.Gold, BorderThickness = new Thickness(1), IsVisible = false };
            var floating = new Image { Stretch = Stretch.Fill, IsVisible = false };
            layer.Children.Add(floating); layer.Children.Add(border);
            var preview = new Border { Name = "ImageOffsetPreview" + pane, BorderBrush = Brushes.Gold, BorderThickness = new Thickness(1), IsVisible = false };
            // 支持枠は画像extentの外にも描く。selection/floating/Resizeの既存clipは維持する。
            var previewLayer = new Canvas { Name = "ImageOffsetPreviewLayer" + pane, IsHitTestVisible = false, ClipToBounds = false };
            previewLayer.Children.Add(preview); _offsetPreviewBorders.Add(preview);
            var handles = new[] { "Right", "Bottom", "Corner" }.Select(name => new Border
            { Name = "ImageResize" + name + pane, BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(1), Background = Brushes.LightSteelBlue, Opacity = .6 }).ToArray();
            foreach (var handle in handles) layer.Children.Add(handle);
            var grid = new Grid { Name = "ImageRectangleSurface" + pane, Focusable = true, Background = Brushes.Transparent,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            grid.Children.Add(_images[pane]); grid.Children.Add(layer); grid.Children.Add(previewLayer);
            _rectangleLayers.Add(layer); _selectionBorders.Add(border); _floatingImages.Add(floating); _rectangleGrids.Add(grid);
            _resizeHandles.Add(handles);
            grid.GotFocus += (_, _) => { if (!_disposed) _editPane.SelectedIndex = pane; };
            // 入口はpane client全体へ接続し、編集座標とcapture先は従来の画像gridを使う。
            _rectanglePressHandlers.Add(async (_, e) =>
            {
                if (e.GetCurrentPoint(grid).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed || !DisplayDragReady) return;
                var configuredMode = DragMode;
                _rectanglePressed = true; _pressedRectanglePointer = e.Pointer; var pressSerial = ++_rectanglePressSerial;
                // pending編集中もwindow外のrelease/capturelostを受けるため、実captureを先に保持する。
                _rectanglePointer = e.Pointer; e.Pointer.Capture(grid);
                await RectangleEventAsync(async () =>
                {
                    var point = PixelPointFromControl(e.GetPosition(grid));
                    if (HasFloatingImage && (_floatingPane != pane || !ContainsFloating(point)))
                    {
                        await CommitFloatingAsync();
                        if (!CanContinueRectangleDrag(e.Pointer, pressSerial)) { CancelStaleRectangleDrag(pressSerial); return; }
                        if (!RectangleReady) return;
                    }
                    if (_rectangles[pane] is { } outside && !Contains(outside, point))
                    { ++_rectangleEpoch; _rectangles[pane] = null; UpdateRectangleVisuals(); }
                    _editPane.SelectedIndex = pane; grid.Focus();
                    var resizeMode = RectangleReady ? ResizeHandleAt(pane, point) : 0;
                    if (resizeMode != 0)
                    {
                        if (_readOnly[pane]) throw new InvalidOperationException("読取り専用の画像はResizeできません。");
                        _resizeMode = resizeMode; _resizeStart = PreprocessedRectangle(pane); ++_rectangleEpoch;
                        _rectangles[pane] = _resizeStart; _resizeGeneration = _generation; _resizeEpoch = _rectangleEpoch;
                        UpdateRectangleVisuals(); UpdateEditControls();
                    }
                    else if (HasFloatingImage && _floatingPane == pane && ContainsFloating(point))
                    {
                        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                        {
                            await CommitFloatingAsync(keepFloating: true);
                            if (!CanContinueRectangleDrag(e.Pointer, pressSerial)) { CancelStaleRectangleDrag(pressSerial); return; }
                        }
                        if (!HasFloatingImage) return;
                        _draggingFloating = true;
                    }
                    else if (_rectangles[pane] is { } selected && Contains(selected, point))
                        _draggingSelection = true;
                    else if (configuredMode == ImageDragMode.RectangleSelect && RectangleReady)
                    { _rectangleAnchor = point; _draggingRectangle = true; SelectRectangle(pane, Rectangle(point, point)); }
                    else if (configuredMode is ImageDragMode.Move or ImageDragMode.AdjustOffset)
                    {
                        if (!CanContinueRectangleDrag(e.Pointer, pressSerial)) { CancelStaleRectangleDrag(pressSerial); return; }
                        StartDisplayDrag(pane, configuredMode, e);
                    }
                    else { EndRectangleDrag(); return; }
                    // Ctrl押下のstampはEditAsyncでcaptureを解除するため、確定完了後に取り直す。
                    if (!CanContinueRectangleDrag(e.Pointer, pressSerial)) { CancelStaleRectangleDrag(pressSerial); return; }
                    _rectanglePointer = e.Pointer; e.Pointer.Capture(grid); _dragPane = pane; _lastDrag = point;
                    e.Handled = true;
                });
            });
            grid.PointerMoved += async (_, e) =>
            {
                try { if (MoveDisplayDrag(pane, e)) return; }
                catch (Exception error) when (error is ArithmeticException or ArgumentException)
                { CancelDisplayDrag(); EndRectangleDrag(); _status.Text = "画像ドラッグの座標を処理できません: " + error.Message; e.Handled = true; return; }
                if (_dragPane != pane || !_draggingRectangle && !_draggingFloating && !_draggingSelection && _resizeMode == 0 || !RectangleReady) return;
                var point = PixelPointFromControl(e.GetPosition(grid));
                if (_resizeMode != 0)
                {
                    var right = (_resizeMode & 1) != 0 ? (int)point.X : _resizeStart.Right;
                    var bottom = (_resizeMode & 2) != 0 ? (int)point.Y : _resizeStart.Bottom;
                    _rectangles[pane] = Rectangle(new(_resizeStart.Left, _resizeStart.Top), new(right, bottom));
                    UpdateRectangleVisuals(); e.Handled = true; return;
                }
                if (_draggingSelection)
                {
                    await RectangleEventAsync(async () =>
                    {
                        var pressSerial = _rectanglePressSerial;
                        var operation = CurrentRectangleOperation = StartSelectionMoveAsync(pane, (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0);
                        var serial = _rectangleDragSerial;
                        await operation;
                        if (!HasFloatingImage || serial != _rectangleDragSerial || !CanContinueRectangleDrag(e.Pointer, pressSerial)) return;
                        _draggingSelection = false; _draggingFloating = true; _dragPane = pane; _lastDrag = point;
                        _rectanglePointer = e.Pointer; e.Pointer.Capture(grid);
                    });
                    e.Handled = true; return;
                }
                if (_draggingRectangle) SelectRectangle(pane, Rectangle(_rectangleAnchor, point));
                else
                {
                    var x = (long)_floatingX + (long)point.X - (long)_lastDrag.X;
                    var y = (long)_floatingY + (long)point.Y - (long)_lastDrag.Y;
                    if (x >= int.MinValue && x <= int.MaxValue && y >= int.MinValue && y <= int.MaxValue) MoveFloating((int)x, (int)y);
                }
                _lastDrag = point; e.Handled = true;
            };
            grid.PointerReleased += async (_, e) =>
            {
                if (e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed || e.InitialPressMouseButton != MouseButton.Left) return;
                if (_displayDragPane == pane)
                { await RectangleEventAsync(() => CurrentDragOperation = FinishDisplayDragAsync(pane, e)); return; }
                var mode = _resizeMode; var start = _resizeStart; var generation = _resizeGeneration; var epoch = _resizeEpoch;
                var current = _dragPane == pane;
                var selecting = _draggingRectangle;
                EndRectangleDrag(); e.Handled = true;
                if (current && selecting && _rectangles[pane] is { } selected && selected.Left == selected.Right && selected.Top == selected.Bottom)
                { ++_rectangleEpoch; _rectangles[pane] = null; UpdateRectangleVisuals(); UpdateEditControls(); }
                if (current && mode != 0)
                    await RectangleEventAsync(() => CurrentRectangleOperation = FinishResizeAsync(pane, mode, start, PixelPointFromControl(e.GetPosition(grid)), generation, epoch));
            };
            grid.PointerCaptureLost += (_, _) => { CancelDisplayDrag(); CancelRectanglePress(); EndRectangleDrag(); };
            grid.KeyDown += async (_, e) =>
            {
                if (_disposed || e.Handled) return;
                _editPane.SelectedIndex = pane;
                var shortcut = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
                Func<Task>? command = e.Key switch
                {
                    Key.Escape => () => { CancelRectangleInteraction(preservePointerPress: _displayDragPane >= 0); return Task.CompletedTask; },
                    Key.Enter when HasFloatingImage => () => CommitFloatingAsync(),
                    Key.Delete when _rectangles[pane] is not null => () => DeleteRectangleAsync(),
                    Key.A when shortcut => () => { SelectAllRectangle(pane); return Task.CompletedTask; },
                    Key.C when shortcut => () => CopyRectangleAsync(), Key.X when shortcut => () => CutRectangleAsync(),
                    Key.V when shortcut => () => PasteClipboardAsync(),
                    _ => null
                };
                if (command is null) return; e.Handled = true; await RectangleEventAsync(command);
            };
            grid.LostFocus += (_, _) => Dispatcher.UIThread.Post(async () =>
            {
                if (!RectangleReady || _floatingPane != pane || grid.IsKeyboardFocusWithin) return;
                var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                // toolbar内への焦点移動ではUndo/Cancelなど各コマンドを先に処理する。
                if (focused is Visual visual && this.IsVisualAncestorOf(visual) && !_rectangleGrids.Any(other => other != grid && other.IsKeyboardFocusWithin)) return;
                await RectangleEventAsync(() => CommitFloatingAsync());
            });
            return grid;
        }

        private void AttachRectangleOwner(Window owner)
            => owner.AddHandler(PointerReleasedEvent, RectanglePointerReleasedAnywhere, RoutingStrategies.Tunnel, handledEventsToo: true);
        private void DetachRectangleOwner(Window owner)
            => owner.RemoveHandler(PointerReleasedEvent, RectanglePointerReleasedAnywhere);
        private void RectanglePointerReleasedAnywhere(object? sender, PointerReleasedEventArgs args)
        {
            if (args.InitialPressMouseButton == MouseButton.Left && !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                && _pressedRectanglePointer == args.Pointer) CancelRectanglePress();
        }
        private bool CanContinueRectangleDrag(IPointer pointer, long serial)
            => !_disposed && _rectanglePressed && _pressedRectanglePointer == pointer && serial == _rectanglePressSerial;
        private void CancelStaleRectangleDrag(long serial)
        {
            // 旧awaitの後に始まったpressは、現在のgestureの所有者として保護する。
            if (serial != _rectanglePressSerial) return;
            CancelDisplayDrag(); EndRectangleDrag();
        }
        private void CancelRectanglePress() { _rectanglePressed = false; _pressedRectanglePointer = null; ++_rectanglePressSerial; }

        private void AddRectangleControls(Panel panel)
        {
            Add(panel, new TextBlock { Text = "ドラッグ操作" }); Add(panel, _dragMode); Add(panel, _dragCondition);
            AddEditButton(panel, "ImageSelectAllRectangle", "画像全体を選択", "rectangle-select", () => { SelectAllRectangle(_editPane.SelectedIndex); return Task.CompletedTask; });
            AddEditButton(panel, "ImageCopyRectangle", "矩形をコピー", "rectangle-copy", CopyRectangleAsync);
            AddEditButton(panel, "ImageCutRectangle", "矩形を切取り", "rectangle-cut", CutRectangleAsync);
            AddEditButton(panel, "ImageDeleteRectangle", "矩形を削除", "rectangle-delete", () => DeleteRectangleAsync());
            AddEditButton(panel, "ImagePasteRectangle", "画像を貼り付け", "rectangle-paste", PasteClipboardAsync);
            AddEditButton(panel, "ImageCommitPaste", "貼り付けを確定", "rectangle-commit", () => CommitFloatingAsync());
            AddEditButton(panel, "ImageCancelRectangle", "選択・貼り付けを取消", "rectangle-cancel", () => { CancelRectangleInteraction(); return Task.CompletedTask; });
        }

        private async Task RectangleEventAsync(Func<Task> action)
        {
            try { await action(); }
            catch (OperationCanceledException) { if (!_disposed) _status.Text = "矩形の操作を中止しました。"; }
            catch (Exception error) { if (!_disposed) _status.Text = "矩形の操作を完了できません: " + error.Message; }
            finally { if (!_disposed) UpdateEditControls(); }
        }

        private static Point PixelPoint(Point point)
            => new(Math.Clamp(Math.Floor(point.X), int.MinValue, int.MaxValue), Math.Clamp(Math.Floor(point.Y), int.MinValue, int.MaxValue));
        private Point PixelPointFromControl(Point point) => PixelPoint(point / _zoom.Value);
        private static ImageRectangle Rectangle(Point first, Point last)
            => new((int)Math.Min(first.X, last.X), (int)Math.Min(first.Y, last.Y), (int)Math.Max(first.X, last.X), (int)Math.Max(first.Y, last.Y));
        private static bool Contains(ImageRectangle rectangle, Point point) => point.X >= rectangle.Left && point.Y >= rectangle.Top && point.X < rectangle.Right && point.Y < rectangle.Bottom;
        private ImageRectangle PreprocessedRectangle(int pane)
        {
            var frame = _decoded![pane]; var offset = _displayOffsets[pane];
            return new(offset.X, offset.Y, checked(offset.X + frame.Width), checked(offset.Y + frame.Height));
        }
        private int ResizeHandleAt(int pane, Point point)
        {
            var rect = PreprocessedRectangle(pane);
            var right = point.X >= rect.Right && point.X < (long)rect.Right + 8;
            var bottom = point.Y >= rect.Bottom && point.Y < (long)rect.Bottom + 8;
            if (right && bottom) return 3;
            if (right && point.Y >= rect.Top && point.Y < rect.Bottom) return 1;
            if (bottom && point.X >= rect.Left && point.X < rect.Right) return 2;
            return 0;
        }
        private Task FinishResizeAsync(int pane, int mode, ImageRectangle start, Point point, long generation, long epoch)
        {
            if (_disposed || generation != _generation || epoch != _rectangleEpoch || !RectangleReady || _readOnly[pane])
                throw new OperationCanceledException("Resizeの開始位置が古くなったか、読取り専用になりました。");
            _rectangles[pane] = null; UpdateRectangleVisuals(); UpdateEditControls();
            var frame = _rawDecoded![pane]; var swapped = _displayOrientations[pane].SwapsDimensions;
            var width = (long)(swapped ? frame.Height : frame.Width) + ((mode & 1) != 0 ? (long)point.X - start.Right : 0);
            var height = (long)(swapped ? frame.Width : frame.Height) + ((mode & 2) != 0 ? (long)point.Y - start.Bottom : 0);
            if (width <= 0 || height <= 0) { CancelRectangleInteraction(); return Task.CompletedTask; }
            if (width > int.MaxValue || height > int.MaxValue) throw new InvalidOperationException("Resizeの寸法が上限を超えます。");
            ImageComparisonEngine.ValidateDimensions((int)width, (int)height);
            return ResizeImageAsync(pane, (int)width, (int)height);
        }
        internal void SelectRectangle(int pane, ImageRectangle rectangle)
        {
            RequireEditing(); if (!RectangleReady) throw new InvalidOperationException("画像の操作完了後に矩形を選択してください。");
            if ((uint)pane >= (uint)_rectangles.Length) throw new ArgumentOutOfRangeException(nameof(pane));
            var frame = _rendered![pane];
            rectangle = new(Math.Clamp(rectangle.Left, 0, frame.Width), Math.Clamp(rectangle.Top, 0, frame.Height),
                Math.Clamp(rectangle.Right, 0, frame.Width), Math.Clamp(rectangle.Bottom, 0, frame.Height));
            if (rectangle.Left > rectangle.Right || rectangle.Top > rectangle.Bottom) throw new ArgumentOutOfRangeException(nameof(rectangle));
            ++_rectangleEpoch; _rectangles[pane] = rectangle; _editPane.SelectedIndex = pane; UpdateRectangleVisuals(); UpdateEditControls();
        }
        internal void SelectAllRectangle(int pane)
        {
            RequireEditing(); if ((uint)pane >= (uint)_counts.Length) throw new ArgumentOutOfRangeException(nameof(pane));
            var offset = _displayOffsets[pane]; var frame = _decoded![pane];
            SelectRectangle(pane, new(offset.X, offset.Y, checked(offset.X + frame.Width), checked(offset.Y + frame.Height)));
        }

        internal ImageRectangle SelectedRawRectangle(int pane)
        {
            RequireEditing();
            var selected = RectangleSelection(pane) ?? throw new InvalidOperationException("矩形を選択してください。");
            var start = _editSession!.ConvertToRealPosition(pane, selected.Left, selected.Top, clamp: false);
            var end = _editSession.ConvertToRealPosition(pane, selected.Right, selected.Bottom, clamp: false);
            var dimensions = _displayOrientations[pane].SwapsDimensions
                ? (_rawDecoded![pane].Height, _rawDecoded[pane].Width) : (_rawDecoded![pane].Width, _rawDecoded[pane].Height);
            if (start.X < 0 || start.Y < 0 || end.X < start.X || end.Y < start.Y || end.X > dimensions.Item1 || end.Y > dimensions.Item2)
                throw new InvalidOperationException("原画の範囲内の矩形を選択してください。ghost・余白だけの選択は編集できません。");
            return new(start.X, start.Y, end.X, end.Y);
        }

        internal Task CopyRectangleAsync() => CurrentRectangleOperation = CopyOrCutAsync(false);
        internal Task CutRectangleAsync() => CurrentRectangleOperation = CopyOrCutAsync(true);
        private async Task CopyOrCutAsync(bool cut)
        {
            RequireEditing(); if (!RectangleReady) throw new InvalidOperationException("画像の操作完了後にコピーしてください。");
            var pane = _editPane.SelectedIndex; if (cut && _readOnly[pane]) throw new InvalidOperationException("読取り専用の画像を切り取れません。");
            var rectangle = SelectedRawRectangle(pane); var session = _editSession!;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _clipboardCancellation = cancellation; _clipboardBusy = true; UpdateEditControls();
            var generation = _generation; var epoch = _rectangleEpoch;
            try
            {
                var payload = session.CaptureRectangle(pane, rectangle, cancellation.Token);
                await Clipboard.WriteAsync(payload, cancellation.Token);
                CheckClipboardCurrent(generation, epoch, pane, cancellation.Token);
                if (cut)
                {
                    if (_readOnly[pane]) throw new InvalidOperationException("切取り元が読取り専用になりました。");
                    _clipboardBusy = false; _clipboardCancellation = null;
                    await DeleteRectangleAsync();
                }
                else _status.Text = "矩形をクリップボードへコピーしました。";
            }
            finally
            {
                if (_clipboardCancellation == cancellation) { _clipboardCancellation = null; _clipboardBusy = false; }
                if (!_disposed) UpdateEditControls();
            }
        }

        internal Task DeleteRectangleAsync(CancellationToken token = default)
        {
            RequireEditing(); if (!RectangleReady) throw new InvalidOperationException("画像の操作完了後に削除してください。");
            var pane = _editPane.SelectedIndex; var rectangle = SelectedRawRectangle(pane);
            return CurrentRectangleOperation = EditAsync((session, cancellation) => session.DeleteRectangle(pane, rectangle, cancellation), pane, token);
        }

        internal Task PasteClipboardAsync() => CurrentRectangleOperation = ReadAndPasteAsync();
        private async Task ReadAndPasteAsync()
        {
            RequireEditing(); if (!RectangleReady) throw new InvalidOperationException("画像の操作完了後に貼り付けてください。");
            var pane = _editPane.SelectedIndex; if (_readOnly[pane]) throw new InvalidOperationException("読取り専用の画像へ貼り付けられません。");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _clipboardCancellation = cancellation; _clipboardBusy = true; UpdateEditControls();
            var generation = _generation; var epoch = _rectangleEpoch;
            try
            {
                var payload = await Clipboard.ReadAsync(cancellation.Token);
                CheckClipboardCurrent(generation, epoch, pane, cancellation.Token);
                if (payload is null) throw new InvalidOperationException("クリップボードに画像がありません。");
                _clipboardBusy = false; _clipboardCancellation = null;
                await BeginFloatingAsync(pane, payload, cancellation.Token);
            }
            finally
            {
                if (_clipboardCancellation == cancellation) { _clipboardCancellation = null; _clipboardBusy = false; }
                if (!_disposed) UpdateEditControls();
            }
        }

        private void CheckClipboardCurrent(long generation, long epoch, int pane, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _generation || epoch != _rectangleEpoch || pane != _editPane.SelectedIndex)
                throw new OperationCanceledException(token);
        }

        internal async Task BeginFloatingAsync(int pane, ImageComparisonEngine.DecodedFrame payload, CancellationToken token = default)
        {
            RequireEditing(); token.ThrowIfCancellationRequested();
            if (!RectangleReady || (uint)pane >= (uint)_counts.Length || _readOnly[pane]) throw new InvalidOperationException("貼付け先が不正・処理中または読取り専用です。");
            ImageClipboard.Validate(payload);
            var owned = new ImageComparisonEngine.DecodedFrame(payload.Number, payload.Width, payload.Height, (byte[])payload.Pixels.Clone());
            var bitmap = CreateBitmap(owned, token);
            try
            {
                // 原本はoriented寸法とのmaxをraw寸法early-noop判定のResizeへ渡す。確定とは別履歴。
                var frame = _rawDecoded![pane]; var swapped = _displayOrientations[pane].SwapsDimensions;
                var width = swapped ? frame.Height : frame.Width; var height = swapped ? frame.Width : frame.Height;
                var operation = EditAsync((session, cancellation) => session.Resize(pane, Math.Max(width, owned.Width), Math.Max(height, owned.Height), cancellation), pane, token);
                var generation = _generation;
                var epoch = _rectangleEpoch;
                await operation; token.ThrowIfCancellationRequested();
                if (_disposed || _generation != generation || _rectangleEpoch != epoch || _readOnly[pane]) throw new OperationCanceledException(token);
                _floatingPayload = owned; _floatingPane = pane; _floatingX = _floatingY = 0; _floatingBitmap = bitmap; bitmap = null!;
                _editPane.SelectedIndex = pane; _rectangleGrids[pane].Focus(); UpdateRectangleVisuals(); UpdateEditControls();
                _status.Text = "貼り付け画像をドラッグで移動できます。Enterで確定、Escapeで取消します。";
            }
            finally { bitmap?.Dispose(); }
        }

        private async Task StartSelectionMoveAsync(int pane, bool copy)
        {
            RequireEditing(); if (!RectangleReady || _readOnly[pane]) throw new InvalidOperationException("読取り専用または処理中の画像は移動できません。");
            var selected = _rectangles[pane]!.Value; var rectangle = SelectedRawRectangle(pane);
            var payload = _editSession!.CaptureRectangle(pane, rectangle, _lifetime.Token);
            var bitmap = CreateBitmap(payload, _lifetime.Token);
            try
            {
                // 原本MOVE_IMAGEは最初のMouseMoveで採取し、通常は元を削除、Ctrlは元を保持する。
                Task operation;
                if (copy) { CancelRectangleInteraction(preservePointerPress: true); operation = Task.CompletedTask; }
                else operation = EditAsync((session, token) => session.DeleteRectangle(pane, rectangle, token), pane, _lifetime.Token);
                var generation = _generation; var epoch = _rectangleEpoch;
                await operation;
                if (_disposed || generation != _generation || epoch != _rectangleEpoch || _readOnly[pane]) throw new OperationCanceledException(_lifetime.Token);
                _floatingPayload = payload; _floatingPane = pane; _floatingX = selected.Left; _floatingY = selected.Top; _floatingBitmap = bitmap; bitmap = null!;
                UpdateRectangleVisuals(); UpdateEditControls();
            }
            finally { bitmap?.Dispose(); }
        }

        private bool ContainsFloating(Point point) => _floatingPayload is not null && point.X >= _floatingX && point.Y >= _floatingY
            && point.X < (long)_floatingX + _floatingPayload.Width && point.Y < (long)_floatingY + _floatingPayload.Height;
        internal void MoveFloating(int x, int y)
        {
            if (!RectangleReady || _floatingPayload is null) throw new InvalidOperationException("移動する貼付け画像がありません。");
            ++_rectangleEpoch; _floatingX = x; _floatingY = y; UpdateRectangleVisuals();
        }
        internal Task CommitFloatingAsync(CancellationToken token = default, bool keepFloating = false)
            => CurrentRectangleOperation = CommitFloatingCoreAsync(token, keepFloating);
        private async Task CommitFloatingCoreAsync(CancellationToken token, bool keepFloating)
        {
            if (_floatingPayload is null) return;
            RequireEditing(); if (!RectangleReady) throw new InvalidOperationException("画像の操作完了後に貼り付けを確定してください。");
            var payload = _floatingPayload; var pane = _floatingPane; var x = _floatingX; var y = _floatingY;
            var position = _editSession!.ConvertToRealPosition(pane, x, y, clamp: false);
            var operation = EditAsync((session, cancellation) => session.PasteImage(pane, payload, position.X, position.Y, cancellation), pane, token);
            var generation = _generation;
            var epoch = _rectangleEpoch;
            await operation;
            if (keepFloating && !_disposed && generation == _generation && epoch == _rectangleEpoch && !_readOnly[pane])
            {
                _floatingBitmap = CreateBitmap(payload, token); _floatingPayload = payload; _floatingPane = pane; _floatingX = x; _floatingY = y;
                UpdateRectangleVisuals(); UpdateEditControls();
            }
        }

        internal void CancelRectangleInteraction(bool preservePointerPress = false)
        {
            ++_rectangleEpoch; var clipboard = _clipboardCancellation; _clipboardCancellation = null; _clipboardBusy = false; clipboard?.Cancel(); Array.Clear(_rectangles);
            foreach (var image in _floatingImages) image.Source = null;
            _floatingPayload = null; _floatingPane = -1; _floatingBitmap?.Dispose(); _floatingBitmap = null;
            if (!preservePointerPress) CancelRectanglePress();
            EndRectangleDrag(preserveCapture: preservePointerPress);
            UpdateRectangleVisuals(); if (!_disposed) UpdateEditControls();
        }
        internal Task ResizeImageAsync(int pane, int width, int height, CancellationToken token = default)
            => EditAsync((session, cancellation) => session.Resize(pane, width, height, cancellation), pane, token);
        private void EndRectangleDrag(bool preserveCapture = false)
        {
            ++_rectangleDragSerial; _resizeMode = 0; _draggingRectangle = _draggingFloating = _draggingSelection = false; _dragPane = -1;
            if (preserveCapture) return;
            var pointer = _rectanglePointer; _rectanglePointer = null; pointer?.Capture(null);
        }
        private void UpdateRectangleVisuals()
        {
            for (var pane = 0; pane < _rectangleLayers.Count; pane++)
            {
                if (_rendered is not null)
                {
                    var rect = PreprocessedRectangle(pane);
                    _rectangleLayers[pane].Width = Math.Max(_rendered[pane].Width, (long)rect.Right + 8) * _zoom.Value;
                    _rectangleLayers[pane].Height = Math.Max(_rendered[pane].Height, (long)rect.Bottom + 8) * _zoom.Value;
                    var handles = _resizeHandles[pane];
                    foreach (var handle in handles) handle.IsVisible = _editSession is not null && !_readOnly[pane];
                    Place(handles[0], rect.Right, rect.Top, 8, rect.Bottom - rect.Top);
                    Place(handles[1], rect.Left, rect.Bottom, rect.Right - rect.Left, 8);
                    Place(handles[2], rect.Right, rect.Bottom, 8, 8);
                }
                var border = _selectionBorders[pane]; border.IsVisible = _rectangles[pane] is not null;
                if (_rectangles[pane] is ImageRectangle rectangle)
                {
                    Canvas.SetLeft(border, rectangle.Left * _zoom.Value); Canvas.SetTop(border, rectangle.Top * _zoom.Value);
                    border.Width = Math.Max(1, (rectangle.Right - rectangle.Left) * _zoom.Value); border.Height = Math.Max(1, (rectangle.Bottom - rectangle.Top) * _zoom.Value);
                }
                var floating = _floatingImages[pane]; floating.IsVisible = _floatingPane == pane && _floatingPayload is not null;
                floating.Source = floating.IsVisible ? _floatingBitmap : null;
                if (floating.IsVisible)
                {
                    Canvas.SetLeft(floating, _floatingX * _zoom.Value); Canvas.SetTop(floating, _floatingY * _zoom.Value);
                    floating.Width = _floatingPayload!.Width * _zoom.Value; floating.Height = _floatingPayload.Height * _zoom.Value;
                }
            }
            void Place(Border handle, int x, int y, int width, int height)
            { Canvas.SetLeft(handle, x * _zoom.Value); Canvas.SetTop(handle, y * _zoom.Value); handle.Width = width * _zoom.Value; handle.Height = height * _zoom.Value; }
        }
    }
}
