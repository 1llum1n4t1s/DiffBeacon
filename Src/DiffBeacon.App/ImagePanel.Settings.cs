using System.Globalization;

namespace DiffBeacon.App;

public static partial class SpecializedViews
{
    public sealed partial class ImagePanel
    {
        internal ImageViewSettings CaptureSettings() => new()
        {
            // 番号と閾値は採用済みの原画に対応する値を使い、復号待ちの選択を保存しない。
            LeftFrame = LeftFrame, MiddleFrame = MiddleFrame ?? 1, RightFrame = RightFrame,
            Threshold = _displayThreshold, ShowDifferences = _displayShowDifferences,
            Zoom = _zoom.Value, OverlayOpacity = _opacity.Value, ReportAllFrames = ReportAllFrames,
            View = _imageViews.SelectedIndex switch { 1 => "Overlay", 2 => "PixelDifference", _ => "SideBySide" }
        };

        internal async Task ApplySettingsAsync(ImageViewSettings settings, CancellationToken token = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var requested = settings with { };
            ImageViewSettings.Validate(requested);
            if (_counts.Length == 2 && requested.MiddleFrame != 1)
                throw new InvalidDataException("中央入力のない比較では中央の画像ページ番号を1にしてください。");
            var numbers = requested.FrameNumbers(_counts.Length == 3);
            ImageComparisonEngine.ValidateSelection(_snapshots!, numbers);
            token.ThrowIfCancellationRequested();
            if (_saving) throw new InvalidOperationException("画像の保存が完了してから表示を変更してください。");
            var previous = CaptureSettings();
            SetSettingsControls(requested);
            var generation = _generation;
            try
            {
                var operation = SetNumbersAsync(numbers, token);
                generation = _generation;
                await operation;
            }
            catch
            {
                if (!_disposed && generation == _generation) SetSettingsControls(previous);
                throw;
            }
        }

        private void SetSettingsControls(ImageViewSettings settings)
        {
            _updatingSelectors = true;
            try
            {
                var numbers = settings.FrameNumbers(_counts.Length == 3);
                for (var i = 0; i < numbers.Length; i++) _selectors[i].Value = numbers[i];
                _threshold.Value = ThresholdControlValue(settings.Threshold);
                // decimal表示の丸めを復号・保存・レポートのDouble閾値へ戻さない。
                _requestedThreshold = settings.Threshold;
                _showDifferences.IsChecked = settings.ShowDifferences;
                _zoom.Value = settings.Zoom; _opacity.Value = settings.OverlayOpacity;
                _reportAllFrames.IsChecked = settings.ReportAllFrames;
                _imageViews.SelectedIndex = settings.View switch { "Overlay" => 1, "PixelDifference" => 2, _ => 0 };
            }
            finally { _updatingSelectors = false; }
        }

        private static decimal ThresholdControlValue(double value)
            => decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
