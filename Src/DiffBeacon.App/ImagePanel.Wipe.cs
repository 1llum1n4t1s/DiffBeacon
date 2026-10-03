using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DiffBeacon.App;

public static partial class SpecializedViews
{
    public sealed partial class ImagePanel
    {
        private ImageWipeSnapshot? _activeWipe, _requestedWipe;
        private ImageComparisonEngine.DecodedFrame[]? _wipeRendered;
        private WriteableBitmap[]? _wipeBitmaps;
        private readonly List<Border> _wipeGuides = [];
        private bool _wipeRunning, _wipeGuideVisible;
        private long _wipeRevision;
        private long _displayCanvasWork;
        internal Task CurrentWipeOperation { get; private set; } = Task.CompletedTask;
        internal Func<Task>? WipeCandidateReady { get; set; }
        internal ImageWipeSnapshot? ActiveWipe => _activeWipe;
        private bool IsWipePress => _displayDragPane >= 0 && _pressedDragMode is ImageDragMode.VerticalWipe or ImageDragMode.HorizontalWipe;

        private void StartWipe(int pane, PointerEventArgs args)
        { _wipeGuideVisible = true; MoveWipe(pane, args); }
        private void MoveWipe(int pane, PointerEventArgs args)
        {
            if (_rendered is null) return;
            var point = args.GetPosition(_rectangleGrids[pane]);
            var coordinate = (_pressedDragMode == ImageDragMode.VerticalWipe ? point.Y : point.X) / _zoom.Value;
            if (!double.IsFinite(coordinate)) throw new ArgumentException("ワイプ座標は有限値です。");
            var dimension = _pressedDragMode == ImageDragMode.VerticalWipe ? _rendered[0].Height : _rendered[0].Width;
            var position = (int)Math.Truncate(Math.Clamp(coordinate, 0d, dimension));
            _wipeGuideVisible = true;
            RequestWipe(new(_pressedDragMode, position)); UpdateWipeGuide();
        }
        internal Task SetWipeAsync(ImageWipeSnapshot wipe)
        { wipe.Validate(); RequestWipe(wipe); return CurrentWipeOperation; }
        private void RequestWipe(ImageWipeSnapshot wipe)
        {
            _requestedWipe = wipe; ++_wipeRevision;
            RequestDisplayRefresh(false);
        }
        // timer/設定/wipe要求を一つのworkerへ集約。tickはrevisionを進めない。
        private async Task RunWipeAsync()
        {
            _wipeRunning = true;
            try
            {
                while (!_disposed && _operationCancellation is null && _displayRefreshPending && CanDisplayRefresh && _rendered is { } baseline)
                {
                    _displayRefreshPending = false;
                    var revision = _wipeRevision; var generation = _generation;
                    var requested = _requestedWipe?.Clamp(baseline[0].Width, baseline[0].Height);
                    var settings = DisplaySettings(requested);
                    var comparison = _displayComparison;
                    var previousPrepared = _overlayPrepared;
                    var previousWork = _overlayPreparationWork;
                    var opacityExplicit = _requestedOpacityExplicit;
                    var canvasWork = _displayCanvasWork; var renderClock = RenderClock;
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    _displayCandidateCancellation = cancellation;
                    var displayToken = cancellation.Token;
                    WriteableBitmap[]? candidate = null;
                    try
                    {
                        DisplayCandidatesStarted++;
                        var computed = await Task.Run(() =>
                        {
                            var budget = new ImageDisplayWorkBudget(ImageComparisonEngine.MaximumDecodeWork, displayToken);
                            budget.Reserve(canvasWork);
                            var prepared = previousPrepared;
                            var preparationWork = previousWork;
                            ImageOverlayRenderer.Result result;
                            if (settings.Mode == 0 && (!settings.BlinkDifferences || !settings.ShowDifferences))
                            {
                                var frames = requested is null ? baseline : ImageWipeRenderer.Render(baseline, requested, displayToken,
                                    ImageComparisonEngine.MaximumDecodeWork - budget.Used);
                                result = new(frames, new([], [], settings.ShowDifferences, requested));
                            }
                            else
                            {
                                if (comparison is null) throw new InvalidOperationException("比較済み画像がありません。");
                                budget.EnsureAvailable(checked(ImageOverlayRenderer.RefreshWork(comparison, settings, prepared is null)
                                    + (prepared is null ? 0 : preparationWork)));
                                if (prepared is null)
                                {
                                    var before = budget.Used;
                                    prepared = ImageOverlayRenderer.FromComparison(comparison, settings, budget);
                                    preparationWork = budget.Used - before;
                                }
                                else budget.Reserve(preparationWork);
                                result = ImageOverlayRenderer.Render(prepared with { Settings = settings }, renderClock, budget);
                            }
                            return (result, prepared, preparationWork);
                        }, displayToken);
                        if (requested is not null && WipeCandidateReady is { } ready) await ready().WaitAsync(displayToken);
                        if (OverlayCandidateReady is { } overlayReady) await overlayReady().WaitAsync(displayToken);
                        displayToken.ThrowIfCancellationRequested();
                        if (_disposed || revision != _wipeRevision || generation != _generation) continue;
                        var frames = computed.result.Frames;
                        candidate = new WriteableBitmap[frames.Count];
                        for (var i = 0; i < frames.Count; i++) candidate[i] = CreateBitmap(frames[i], displayToken);
                        if (_disposed || revision != _wipeRevision || generation != _generation) continue;
                        var old = _wipeBitmaps; _wipeBitmaps = candidate; candidate = null;
                        _wipeRendered = frames.ToArray(); _activeWipe = _requestedWipe = requested;
                        _overlayPrepared = computed.prepared; _overlayPreparationWork = computed.preparationWork;
                        if (_displayError is not null && _status.Text == _displayError)
                            _status.Text = $"領域 {DifferenceCount} 個" + (_counts.Length == 3 ? $" · 競合 {ConflictCount} 個" : "")
                                + (_selectedDiffIndex >= 0 ? $" · 選択 {_selectedDiffIndex + 1}/{DifferenceCount}" : "") + $" · 左右の画素差 {DifferentPixels:N0}/{TotalPixels:N0} px";
                        _displayError = null;
                        _adoptedOpacity = settings.Alpha; _adoptedOpacityExplicit = opacityExplicit;
                        AdoptedDisplay = new(generation, revision, frames, computed.result.Sample, settings);
                        for (var i = 0; i < frames.Count; i++) _images[i].Source = _wipeBitmaps[i];
                        if (old is not null) foreach (var bitmap in old) bitmap.Dispose();
                        DisplayCandidatesAdopted++;
                        if (_displayCompletionRevision == revision) _displayStateCompletion?.TrySetResult();
                        RestoreOverlayControls(); UpdateWipeGuide();
                    }
                    catch (OperationCanceledException) when (_disposed) { return; }
                    catch (OperationCanceledException) when (displayToken.IsCancellationRequested) { }
                    catch (Exception error)
                    {
                        if (!_disposed && revision == _wipeRevision)
                        {
                            _status.Text = _displayError = "画像の表示を更新できません: " + error.Message;
                            _requestedOpacity = _adoptedOpacity; _requestedOpacityExplicit = _adoptedOpacityExplicit;
                            RestoreOverlayControls();
                            if (_displayCompletionRevision == revision) _displayStateCompletion?.TrySetException(error);
                        }
                    }
                    finally
                    {
                        if (ReferenceEquals(_displayCandidateCancellation, cancellation)) _displayCandidateCancellation = null;
                        if (candidate is not null) foreach (var bitmap in candidate) bitmap?.Dispose();
                    }
                }
            }
            finally { _wipeRunning = false; }
        }
        private void ClearWipe()
        {
            if (_requestedWipe is null && _activeWipe is null)
            { _wipeGuideVisible = false; UpdateWipeGuide(); return; }
            ++_wipeRevision; _requestedWipe = _activeWipe = null; _wipeRendered = null; _wipeGuideVisible = false;
            _displayStateCompletion?.TrySetResult();
            if (!_disposed && _bitmaps is not null) for (var i = 0; i < _images.Length; i++) _images[i].Source = _bitmaps[i];
            if (_wipeBitmaps is not null) foreach (var bitmap in _wipeBitmaps) bitmap.Dispose();
            _wipeBitmaps = null; UpdateWipeGuide();
            if (!_disposed && _rendered is not null) AdoptBaselineDisplay(_rendered, null);
            if (!_disposed && NeedsOverlay) RequestDisplayRefresh(false);
        }
        private void UpdateWipeGuide()
        {
            var wipe = _requestedWipe ?? _activeWipe;
            for (var pane = 0; pane < _wipeGuides.Count; pane++)
            {
                var guide = _wipeGuides[pane]; guide.IsVisible = _wipeGuideVisible && pane == _displayDragPane && wipe is not null && _rendered is not null;
                if (!guide.IsVisible) continue;
                var vertical = wipe!.Mode == ImageDragMode.VerticalWipe;
                Canvas.SetLeft(guide, vertical ? 0 : wipe.Position * _zoom.Value);
                Canvas.SetTop(guide, vertical ? wipe.Position * _zoom.Value : 0);
                guide.Width = vertical ? _rendered![0].Width * _zoom.Value : 1;
                guide.Height = vertical ? 1 : _rendered![0].Height * _zoom.Value;
            }
        }
        private void PreserveWipeOrCancelDrag()
        { if (!IsWipePress && _activeWipe is null && _requestedWipe is null) CancelDisplayDrag(); }
    }
}
