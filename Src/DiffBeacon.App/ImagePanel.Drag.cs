using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public static partial class SpecializedViews
{
    public sealed partial class ImagePanel
    {
        private readonly ImageApplicationOptionsStore _applicationOptions;
        private readonly ComboBox _dragMode = new() { Name = "ImageDragMode", ItemsSource = new[] { "操作なし", "表示を移動", "画像位置を調整", "矩形選択" }, MinWidth = 145 };
        private readonly TextBlock _dragCondition = new() { Text = "ドラッグ操作は左右／左・中央・右表示で使用", IsVisible = false };
        private static readonly ImageDragMode[] SupportedDragModes = [ImageDragMode.None, ImageDragMode.Move, ImageDragMode.AdjustOffset, ImageDragMode.RectangleSelect];
        private readonly List<ScrollViewer> _paneScrolls = [];
        private readonly List<Vector> _observedScrolls = [];
        private readonly List<Border> _offsetPreviewBorders = [];
        private readonly HashSet<ScrollBar> _attachedScrollBars = [];
        private readonly HashSet<MenuFlyout> _attachedScrollMenus = [];
        private ScrollBar? _absoluteScrollCommand;
        private bool _updatingDragMode, _synchronizingScroll;
        private Action<string>? _optionsGuard;
        private int _displayDragPane = -1;
        private ImageDragMode _pressedDragMode;
        private IPointer? _displayDragPointer;
        private Point _displayDragStart, _displayDragPrevious;
        private double _displayDragZoom;
        private long _displayDragGeneration;
        private ImageRectangle? _offsetPreview;
        internal ImageDragMode DragMode => _applicationOptions.Mode;
        internal ImageRectangle? OffsetPreview => _offsetPreview;
        internal bool HasDisplayPointerCapture => _displayDragPointer?.Captured is not null;
        internal Task CurrentDragOperation { get; private set; } = Task.CompletedTask;
        private bool DisplayDragReady => !_disposed && !_saving && !_resetEditing && !_clipboardBusy && _decoded is not null && _operationCancellation is null;

        private void InitializeDragOptions()
        {
            _applicationOptions.Changed += ApplicationDragModeChanged;
            _optionsGuard = path =>
            {
                foreach (var source in ProtectedImagePaths())
                    if (ArchivePaths.SameFile(source, path)) throw new InvalidOperationException("画像操作設定で比較入力を上書きできません。");
                _outputGuard?.Invoke(path);
            };
            _applicationOptions.AddOutputGuard(_optionsGuard);
            ApplicationDragModeChanged();
            _dragMode.SelectionChanged += (_, _) =>
            {
                if (_disposed || _updatingDragMode || _dragMode.SelectedIndex < 0) return;
                if (!_applicationOptions.SetMode(SupportedDragModes[_dragMode.SelectedIndex]))
                { ApplicationDragModeChanged(); _status.Text = _applicationOptions.Diagnostic; }
            };
            _imageViews.SelectionChanged += (_, _) => { CancelDisplayDrag(); UpdateDragControls(); };
        }
        private void ApplicationDragModeChanged()
        {
            _updatingDragMode = true;
            try { _dragMode.SelectedIndex = Array.IndexOf(SupportedDragModes, _applicationOptions.Mode); }
            finally { _updatingDragMode = false; }
        }
        internal bool SetDragMode(ImageDragMode mode) => _applicationOptions.SetMode(mode);
        private void UpdateDragControls()
        {
            _dragMode.IsEnabled = !_disposed && !_saving && _imageViews.SelectedIndex == 0;
            _dragCondition.IsVisible = _imageViews.SelectedIndex != 0;
        }
        private ScrollViewer AttachImageScroll(int pane, Grid grid)
        {
            var scroll = Scroll(grid); scroll.Name = "ImagePaneScroll" + pane;
            _paneScrolls.Add(scroll); _observedScrolls.Add(default);
            scroll.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (_disposed) return;
                if (e.Source is Visual source && (source is ScrollBar || source.GetVisualAncestors().OfType<ScrollBar>().Any())) return;
                var presenter = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().SingleOrDefault();
                // 空白もpane clientに含める。scrollbar/viewport外は画像操作を開始しない。
                if (presenter is null || !new Rect(presenter.Bounds.Size).Contains(e.GetPosition(presenter))) return;
                _rectanglePressHandlers[pane](scroll, e);
            }, RoutingStrategies.Bubble, handledEventsToo: true);
            scroll.ScrollChanged += (_, e) =>
            {
                var previous = _observedScrolls[pane]; _observedScrolls[pane] = scroll.Offset;
                if (_disposed || _synchronizingScroll || e.OffsetDelta == default || previous == scroll.Offset) return;
                SynchronizeScroll(pane, scroll.Offset, e.OffsetDelta);
            };
            scroll.TemplateApplied += (_, _) =>
            {
                foreach (var bar in scroll.GetVisualDescendants().OfType<ScrollBar>())
                {
                    if (!_attachedScrollBars.Add(bar)) continue;
                    bar.PropertyChanged += (_, args) => { if (args.Property == Control.ContextFlyoutProperty) AttachScrollMenu(bar); };
                    AttachScrollMenu(bar);
                    bar.Scroll += (_, e) =>
                    {
                        if (_disposed || _synchronizingScroll) return;
                        var horizontal = bar.Orientation == Avalonia.Layout.Orientation.Horizontal;
                        if (_absoluteScrollCommand == bar)
                        {
                            _absoluteScrollCommand = null;
                            var end = e.ScrollEventType == ScrollEventType.LargeIncrement;
                            ScrollEach(i => horizontal ? new Vector((end ? Math.Max(0, _paneScrolls[i].Extent.Width - _paneScrolls[i].Viewport.Width) : 0) - _paneScrolls[i].Offset.X, 0)
                                : new Vector(0, (end ? Math.Max(0, _paneScrolls[i].Extent.Height - _paneScrolls[i].Viewport.Height) : 0) - _paneScrolls[i].Offset.Y));
                            return;
                        }
                        if (e.ScrollEventType is ScrollEventType.SmallIncrement or ScrollEventType.SmallDecrement or ScrollEventType.LargeIncrement or ScrollEventType.LargeDecrement)
                        {
                            var positive = e.ScrollEventType is ScrollEventType.SmallIncrement or ScrollEventType.LargeIncrement;
                            var page = e.ScrollEventType is ScrollEventType.LargeIncrement or ScrollEventType.LargeDecrement;
                            // barはsourceを先にclamp済み。sourceだけ観測済みのcommand前位置から計算する。
                            var before = _observedScrolls[pane];
                            ScrollEach(i => horizontal ? new Vector((positive ? 1 : -1) * (page ? _paneScrolls[i].Viewport.Width : 1), 0)
                                : new Vector(0, (positive ? 1 : -1) * (page ? _paneScrolls[i].Viewport.Height : 1)), pane, before);
                            return;
                        }
                        if (e.ScrollEventType != ScrollEventType.ThumbTrack) return;
                        var offset = horizontal
                            ? scroll.Offset.WithX(e.NewValue) : scroll.Offset.WithY(e.NewValue);
                        _observedScrolls[pane] = offset; SynchronizeScroll(pane, offset,
                            horizontal: horizontal, vertical: !horizontal);
                    };
                }
            };
            // 原本wheelは同じdeltaを各paneへ送る。源paneが端でも他paneの移動を失わない。
            scroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
            {
                if (_disposed || e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
                var horizontal = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                ScrollEach(i => horizontal ? new Vector(-e.Delta.Y * 16, 0) : new Vector(-e.Delta.X * 16, -e.Delta.Y * 16));
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
            scroll.AddHandler(KeyDownEvent, (_, e) =>
            {
                if (_disposed || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right or Key.PageUp or Key.PageDown)) return;
                ScrollEach(i => e.Key switch
                {
                    Key.Up => new Vector(0, -1), Key.Down => new Vector(0, 1), Key.Left => new Vector(-1, 0), Key.Right => new Vector(1, 0),
                    Key.PageUp => new Vector(0, -_paneScrolls[i].Viewport.Height), _ => new Vector(0, _paneScrolls[i].Viewport.Height)
                });
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
            return scroll;
        }
        private void AttachScrollMenu(ScrollBar bar)
        {
            if (bar.ContextFlyout is MenuFlyout menu && _attachedScrollMenus.Add(menu)) menu.Opened += ScrollMenuOpened;
        }
        private void ScrollMenuOpened(object? sender, EventArgs args)
        {
            // Opened時には$parent command bindingが解決済み。Bubble-only Clickのclass handlerより前に識別する。
            if (_disposed || sender is not MenuFlyout menu) return;
            foreach (var item in menu.Items.OfType<MenuItem>())
                if (Avalonia.Automation.AutomationProperties.GetAutomationId(item) is { } id
                    && id is "Top" or "Bottom" or "LeftEdge" or "RightEdge" && item.Command is not ScrollEdgeCommand)
                    item.Command = new ScrollEdgeCommand(menu, id is "Bottom" or "RightEdge");
        }
        private sealed class ScrollEdgeCommand(MenuFlyout menu, bool end) : System.Windows.Input.ICommand
        {
            public event EventHandler? CanExecuteChanged { add { } remove { } }
            private (ScrollBar? Bar, ImagePanel? Owner) Target()
            {
                var bar = menu.Target as ScrollBar;
                // Fluentのmenu resourceは複数paneで再使用されるため、開いたtargetから所有者を得る。
                return (bar, bar?.GetVisualAncestors().OfType<ImagePanel>().FirstOrDefault());
            }
            public bool CanExecute(object? parameter)
            {
                return Target().Bar is not null;
            }
            public void Execute(object? parameter)
            {
                var (bar, owner) = Target(); if (bar is null) return;
                // 共有resourceの通常barと、開いたmenuの画像ownerがDispose済みの場合はnative動作を保つ。
                if (owner is null || owner._disposed || !owner._attachedScrollBars.Contains(bar))
                { if (end) bar.ScrollToEnd(); else bar.ScrollToHome(); return; }
                owner._absoluteScrollCommand = bar;
                try { if (end) bar.ScrollToEnd(); else bar.ScrollToHome(); }
                finally { if (owner._absoluteScrollCommand == bar) owner._absoluteScrollCommand = null; }
            }
        }
        private void ScrollEach(Func<int, Vector> delta, int sourcePane = -1, Vector sourceBefore = default)
        {
            _synchronizingScroll = true;
            try
            {
                for (var i = 0; i < _paneScrolls.Count; i++)
                {
                    var scroll = _paneScrolls[i]; var desired = (i == sourcePane ? sourceBefore : scroll.Offset) + delta(i);
                    var next = new Vector(Math.Clamp(desired.X, 0, Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width)),
                        Math.Clamp(desired.Y, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
                    _observedScrolls[i] = next; scroll.Offset = next;
                }
            }
            finally { _synchronizingScroll = false; }
        }
        private void SynchronizeScroll(int pane, Vector offset, Vector? delta = null, bool horizontal = true, bool vertical = true)
        {
            _synchronizingScroll = true;
            try
            {
                for (var i = 0; i < _paneScrolls.Count; i++)
                {
                    if (i == pane) continue;
                    var other = _paneScrolls[i];
                    var requested = delta is { } change ? other.Offset + change : offset;
                    var next = new Vector(Math.Clamp(horizontal ? requested.X : other.Offset.X, 0, Math.Max(0, other.Extent.Width - other.Viewport.Width)),
                        Math.Clamp(vertical ? requested.Y : other.Offset.Y, 0, Math.Max(0, other.Extent.Height - other.Viewport.Height)));
                    _observedScrolls[i] = next; other.Offset = next;
                }
            }
            finally { _synchronizingScroll = false; }
        }
        private void StartDisplayDrag(int pane, ImageDragMode mode, PointerPressedEventArgs args)
        {
            _displayDragPane = pane; _pressedDragMode = mode; _displayDragPointer = args.Pointer;
            _displayDragStart = _displayDragPrevious = args.GetPosition(_paneScrolls[pane]);
            _displayDragZoom = _zoom.Value; _displayDragGeneration = _generation;
        }
        private bool DisplayDragValid(int pane, IPointer pointer)
            => _displayDragPane == pane && _displayDragPointer == pointer && _displayDragGeneration == _generation
                && _displayDragZoom == _zoom.Value && DisplayDragReady;
        private bool MoveDisplayDrag(int pane, PointerEventArgs args)
        {
            if (_displayDragPane != pane) return false;
            if (!DisplayDragValid(pane, args.Pointer)) { CancelDisplayDrag(); return true; }
            var point = args.GetPosition(_paneScrolls[pane]);
            if (_pressedDragMode == ImageDragMode.AdjustOffset)
            {
                var (dx, dy) = OffsetDelta(point);
                var support = PreprocessedRectangle(pane);
                _offsetPreview = new(checked(support.Left + dx), checked(support.Top + dy), checked(support.Right + dx), checked(support.Bottom + dy));
                UpdateOffsetPreview();
            }
            else
            {
                var scroll = _paneScrolls[pane]; var offset = scroll.Offset;
                var x = offset.X; var y = offset.Y;
                if (scroll.Extent.Width > scroll.Viewport.Width)
                { x = Math.Clamp(x + Math.Truncate((_displayDragPrevious.X - point.X) * _displayDragZoom), 0, scroll.Extent.Width - scroll.Viewport.Width); _displayDragPrevious = _displayDragPrevious.WithX(point.X); }
                if (scroll.Extent.Height > scroll.Viewport.Height)
                { y = Math.Clamp(y + Math.Truncate((_displayDragPrevious.Y - point.Y) * _displayDragZoom), 0, scroll.Extent.Height - scroll.Viewport.Height); _displayDragPrevious = _displayDragPrevious.WithY(point.Y); }
                scroll.Offset = new(x, y); _observedScrolls[pane] = scroll.Offset; SynchronizeScroll(pane, scroll.Offset,
                    horizontal: scroll.Extent.Width > scroll.Viewport.Width, vertical: scroll.Extent.Height > scroll.Viewport.Height);
            }
            args.Handled = true; return true;
        }
        private (int X, int Y) OffsetDelta(Point point)
            => (checked((int)Math.Truncate((point.X - _displayDragStart.X) / _displayDragZoom)),
                checked((int)Math.Truncate((point.Y - _displayDragStart.Y) / _displayDragZoom)));
        private async Task FinishDisplayDragAsync(int pane, PointerReleasedEventArgs args)
        {
            var valid = args.InitialPressMouseButton == MouseButton.Left && DisplayDragValid(pane, args.Pointer);
            var mode = _pressedDragMode; var point = args.GetPosition(_paneScrolls[pane]);
            (int, int) delta;
            try { delta = valid && mode == ImageDragMode.AdjustOffset ? OffsetDelta(point) : default; }
            catch { CancelDisplayDrag(); EndRectangleDrag(); throw; }
            CancelDisplayDrag(); EndRectangleDrag(); args.Handled = true;
            if (valid && mode == ImageDragMode.AdjustOffset && delta != default)
                await AddOffsetAsync(pane, delta.Item1, delta.Item2);
        }
        private void CancelDisplayDrag()
        {
            if (_disposed)
            {
                foreach (var menu in _attachedScrollMenus) menu.Opened -= ScrollMenuOpened;
                _attachedScrollMenus.Clear(); _absoluteScrollCommand = null;
            }
            var pointer = _displayDragPointer; _displayDragPointer = null; _displayDragPane = -1; _offsetPreview = null;
            UpdateOffsetPreview(); pointer?.Capture(null);
        }
        private void UpdateOffsetPreview()
        {
            for (var pane = 0; pane < _offsetPreviewBorders.Count; pane++)
            {
                var border = _offsetPreviewBorders[pane]; border.IsVisible = pane == _displayDragPane && _offsetPreview is not null;
                if (!border.IsVisible) continue;
                var rect = _offsetPreview!.Value;
                Canvas.SetLeft(border, rect.Left * _zoom.Value); Canvas.SetTop(border, rect.Top * _zoom.Value);
                border.Width = (rect.Right - rect.Left) * _zoom.Value; border.Height = (rect.Bottom - rect.Top) * _zoom.Value;
            }
        }
    }
}
