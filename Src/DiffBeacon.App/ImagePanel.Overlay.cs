using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace DiffBeacon.App;

public static partial class SpecializedViews
{
    public sealed partial class ImagePanel
    {
        private readonly ComboBox _overlayMode = new() { Name = "ImageOverlayMode", ItemsSource = new[] { "なし", "XOR", "Alpha", "アニメーション" }, MinWidth = 130 };
        private readonly ComboBox _alphaScope = new() { Name = "ImageAlphaScope", ItemsSource = new[] { "全比較の既定", "この比較" }, MinWidth = 130 };
        private readonly CheckBox _blink = new() { Name = "ImageBlink", Content = "差分を点滅" };
        private readonly NumericUpDown _animationPeriod = new() { Name = "ImageAnimationPeriod", Minimum = 200, Maximum = 8000, Increment = 100, Value = 1000, Width = 160 };
        private readonly NumericUpDown _blinkPeriod = new() { Name = "ImageBlinkPeriod", Minimum = 200, Maximum = 8000, Increment = 100, Value = 800, Width = 160 };
        private readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
        private bool _updatingOverlay, _displayRefreshPending, _displayActive = true, _displayAttached, _hasAttached;
        private long _lastOverlayGate;
        private double _requestedOpacity = .3, _adoptedOpacity = .3;
        private bool _requestedOpacityExplicit, _adoptedOpacityExplicit;
        private string? _displayError;
        private string _imageViewToken = "SideBySide";
        private ImageComparisonEngine.FrameComparison? _displayComparison;
        private ImageOverlayRenderer.Prepared? _overlayPrepared;
        private long _overlayPreparationWork;
        internal IImageDisplayClock RenderClock { get; set; } = new ImageSystemDisplayClock();
        internal IImageDisplayClock GateClock { get; set; } = new ImageSystemDisplayClock();
        internal ImageAdoptedDisplay? AdoptedDisplay { get; private set; }
        internal Task CurrentDisplayOperation { get; private set; } = Task.CompletedTask;
        private TaskCompletionSource? _displayStateCompletion;
        private long _displayCompletionRevision;
        private Task _displayWorkerTask = Task.CompletedTask;
        private CancellationTokenSource? _displayCandidateCancellation;
        internal Func<Task>? OverlayCandidateReady { get; set; }
        internal int DisplayCandidatesStarted { get; private set; }
        internal int DisplayCandidatesAdopted { get; private set; }
        internal bool OverlayTimerRunning => _overlayTimer.IsEnabled;
        internal Task DisplayWorkerOperation => _displayWorkerTask;
        private bool NeedsOverlay => _applicationOptions.Current.OverlayMode != 0
            || _displayShowDifferences && _applicationOptions.Current.BlinkDifferences;
        private bool OverlayVisible => _displayActive && IsEffectivelyVisible && _imageViews.SelectedIndex == 0;
        private bool CanDisplayRefresh => _displayActive && _imageViews.SelectedIndex == 0 && (!_hasAttached || _displayAttached);
        internal void SetDisplayActive(bool active)
        {
            if (_disposed || active == _displayActive) return;
            _displayActive = active; ++_wipeRevision;
            if (!active) _displayStateCompletion?.TrySetResult();
            if (active) RequestDisplayRefresh(false);
            UpdateOverlayTimer();
        }

        internal ImageViewSettings CaptureSettings()
        {
            var settings = CaptureSettingsValues(); settings.RestoreOverlayOpacityPresence(_adoptedOpacityExplicit); return settings;
        }
        private WrapPanel CreateOverlayControls()
        {
            var row = new WrapPanel();
            foreach (var control in new Control[] { new TextBlock { Text = "重ね合わせ" }, _overlayMode, _alphaScope,
                new TextBlock { Text = "Alpha" }, _opacity, new TextBlock { Text = "周期(ms)" }, _animationPeriod,
                _blink, new TextBlock { Text = "点滅周期(ms)" }, _blinkPeriod }) Add(row, control);
            return row;
        }
        private void InitializeOverlay()
        {
            _applicationOptions.Changed += ApplicationOverlayChanged;
            _overlayTimer.Tick += OverlayTick;
            AttachedToVisualTree += (_, _) => { _hasAttached = _displayAttached = true; UpdateOverlayTimer(); };
            DetachedFromVisualTree += (_, _) =>
            { _displayAttached = false; _overlayTimer.Stop(); ++_wipeRevision; _displayRefreshPending = true; _displayStateCompletion?.TrySetResult(); };
            PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty) UpdateOverlayTimer(); };
            _overlayMode.SelectionChanged += (_, _) => SaveOverlayControls();
            _blink.IsCheckedChanged += (_, _) => SaveOverlayControls();
            _animationPeriod.ValueChanged += (_, _) => SaveOverlayControls();
            _blinkPeriod.ValueChanged += (_, _) => SaveOverlayControls();
            _alphaScope.SelectionChanged += (_, _) =>
            {
                if (_updatingOverlay || _updatingSelectors || _disposed) return;
                _requestedOpacityExplicit = _alphaScope.SelectedIndex == 1;
                if (!_requestedOpacityExplicit) _requestedOpacity = _applicationOptions.Current.OverlayAlpha;
                RequestDisplayRefresh();
            };
            _opacity.ValueChanged += (_, _) =>
            {
                if (_updatingOverlay || _updatingSelectors || _disposed) return;
                if (_requestedOpacityExplicit) { _requestedOpacity = _opacity.Value; RequestDisplayRefresh(); }
                else if (!_applicationOptions.SetOptions(_applicationOptions.Current with { OverlayAlpha = _opacity.Value })) RestoreOverlayControls();
            };
            ApplicationOverlayChanged();
        }
        private void SaveOverlayControls()
        {
            if (_updatingOverlay || _updatingSelectors || _disposed) return;
            var p = _animationPeriod.Value ?? 1000; var b = _blinkPeriod.Value ?? 800;
            if (p != decimal.Truncate(p) || b != decimal.Truncate(b)) { RestoreOverlayControls(); return; }
            if (!_applicationOptions.SetOptions(_applicationOptions.Current with { OverlayMode = _overlayMode.SelectedIndex,
                BlinkDifferences = _blink.IsChecked == true, AnimationPeriod = (int)p, BlinkPeriod = (int)b }))
            { RestoreOverlayControls(); _status.Text = _applicationOptions.Diagnostic; }
        }
        private void ApplicationOverlayChanged()
        {
            if (_disposed) return;
            if (!_requestedOpacityExplicit) _requestedOpacity = _applicationOptions.Current.OverlayAlpha;
            RestoreOverlayControls(); RequestDisplayRefresh(); UpdateOverlayTimer();
        }
        private void RestoreOverlayControls()
        {
            _updatingOverlay = true;
            try
            {
                var options = _applicationOptions.Current;
                _overlayMode.SelectedIndex = options.OverlayMode; _blink.IsChecked = options.BlinkDifferences;
                _animationPeriod.Value = options.AnimationPeriod; _blinkPeriod.Value = options.BlinkPeriod;
                _alphaScope.SelectedIndex = _requestedOpacityExplicit ? 1 : 0; _opacity.Value = _requestedOpacity;
                _opacity.IsEnabled = options.OverlayMode == 2; _animationPeriod.IsEnabled = options.OverlayMode == 3;
                _blink.IsEnabled = _displayShowDifferences; _blinkPeriod.IsEnabled = options.BlinkDifferences && _displayShowDifferences;
            }
            finally { _updatingOverlay = false; }
        }
        internal bool SetOverlayOptions(ImageApplicationOptions options) => _applicationOptions.SetOptions(options);
        private ImageOverlayRenderer.Settings DisplaySettings(ImageWipeSnapshot? wipe)
        {
            var options = _applicationOptions.Current;
            return new(options.OverlayMode, _requestedOpacity, _displayShowDifferences, options.BlinkDifferences,
                options.AnimationPeriod, options.BlinkPeriod, _displayBlockSize, _displayThreshold,
                _displayHighlightAlpha, _selectedDiffIndex, wipe);
        }
        private void RequestDisplayRefresh(bool stateChanged = true, bool waitForState = true)
        {
            if (_disposed) return;
            if (stateChanged) ++_wipeRevision;
            if (waitForState)
            {
                _displayStateCompletion?.TrySetResult();
                _displayStateCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _displayCompletionRevision = _wipeRevision;
                CurrentDisplayOperation = CurrentWipeOperation = _displayStateCompletion.Task;
            }
            _displayRefreshPending = true;
            if (!_wipeRunning && _operationCancellation is null && _rendered is not null && CanDisplayRefresh)
                _displayWorkerTask = RunWipeAsync();
            else if (!CanDisplayRefresh || _rendered is null) _displayStateCompletion?.TrySetResult();
        }
        private void OverlayTick(object? sender, EventArgs args) => TickOverlay();
        internal void TickOverlay()
        {
            if (_disposed || !OverlayVisible || _rendered is null) { _overlayTimer.Stop(); return; }
            var options = _applicationOptions.Current;
            if (options.OverlayMode != 3 && !(_displayShowDifferences && options.BlinkDifferences)) { _overlayTimer.Stop(); return; }
            var now = GateClock.ReadEpochMilliseconds();
            if (now < 0) return;
            if (now >= _lastOverlayGate && now - _lastOverlayGate < options.AnimationPeriod / 20) return;
            _lastOverlayGate = now; RequestDisplayRefresh(false, false);
        }
        private void UpdateOverlayTimer()
        {
            if (_disposed) return;
            var options = _applicationOptions.Current;
            if (OverlayVisible && _displayRefreshPending && !_wipeRunning) RequestDisplayRefresh(false, false);
            if (OverlayVisible && _rendered is not null && (options.OverlayMode == 3 || _displayShowDifferences && options.BlinkDifferences))
            _overlayTimer.Start();
            else _overlayTimer.Stop();
        }
        private void DisposeOverlay()
        {
            _overlayTimer.Stop(); _overlayTimer.Tick -= OverlayTick; _applicationOptions.Changed -= ApplicationOverlayChanged;
            _displayRefreshPending = false; _displayComparison = null; _overlayPrepared = null; AdoptedDisplay = null;
            _displayStateCompletion?.TrySetResult();
        }
        private void AdoptBaselineDisplay(IReadOnlyList<ImageComparisonEngine.DecodedFrame> frames, ImageWipeSnapshot? wipe)
        {
            var settings = DisplaySettings(wipe) with { Mode = 0, BlinkDifferences = false, Alpha = _adoptedOpacity };
            AdoptedDisplay = new(_generation, _wipeRevision, frames, new([], [], _displayShowDifferences, wipe), settings);
        }
    }
}
