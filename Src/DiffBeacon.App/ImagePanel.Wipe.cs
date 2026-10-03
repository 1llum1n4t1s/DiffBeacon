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
            if (!_wipeRunning) CurrentWipeOperation = RunWipeAsync();
        }
        private async Task RunWipeAsync()
        {
            _wipeRunning = true;
            try
            {
                while (!_disposed && _requestedWipe is { } requested && _rendered is { } baseline)
                {
                    var revision = _wipeRevision; var generation = _generation;
                    requested = requested.Clamp(baseline[0].Width, baseline[0].Height);
                    WriteableBitmap[]? candidate = null;
                    try
                    {
                        var frames = await Task.Run(() => ImageWipeRenderer.Render(baseline, requested, _lifetime.Token,
                            ImageComparisonEngine.MaximumDecodeWork - _displayCanvasWork).ToArray(), _lifetime.Token);
                        if (WipeCandidateReady is { } ready) await ready();
                        if (_disposed || revision != _wipeRevision || generation != _generation) continue;
                        candidate = new WriteableBitmap[frames.Length];
                        for (var i = 0; i < frames.Length; i++) candidate[i] = CreateBitmap(frames[i], _lifetime.Token);
                        var old = _wipeBitmaps; _wipeBitmaps = candidate; candidate = null;
                        _wipeRendered = frames; _activeWipe = _requestedWipe = requested;
                        for (var i = 0; i < frames.Length; i++) _images[i].Source = _wipeBitmaps[i];
                        if (old is not null) foreach (var bitmap in old) bitmap.Dispose();
                        UpdateWipeGuide();
                    }
                    catch (OperationCanceledException) when (_disposed) { return; }
                    catch (Exception error) { if (!_disposed && revision == _wipeRevision) _status.Text = "ワイプを表示できません: " + error.Message; }
                    finally { if (candidate is not null) foreach (var bitmap in candidate) bitmap?.Dispose(); }
                    if (revision == _wipeRevision) return;
                }
            }
            finally { _wipeRunning = false; }
        }
        private void ClearWipe()
        {
            ++_wipeRevision; _requestedWipe = _activeWipe = null; _wipeRendered = null; _wipeGuideVisible = false;
            if (!_disposed && _bitmaps is not null) for (var i = 0; i < _images.Length; i++) _images[i].Source = _bitmaps[i];
            if (_wipeBitmaps is not null) foreach (var bitmap in _wipeBitmaps) bitmap.Dispose();
            _wipeBitmaps = null; UpdateWipeGuide();
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
