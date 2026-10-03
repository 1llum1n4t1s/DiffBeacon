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
            HighlightAlpha = _displayHighlightAlpha,
            Zoom = _zoom.Value, OverlayOpacity = _opacity.Value, ReportAllFrames = ReportAllFrames,
            View = _imageViews.SelectedIndex switch { 1 => "Overlay", 2 => "PixelDifference", _ => "SideBySide" },
            LeftOrientation = _displayOrientations[0], RightOrientation = _displayOrientations[^1],
            LeftOffset = _displayOffsets[0], RightOffset = _displayOffsets[^1],
            MiddleOffset = _displayOffsets.Length == 3 ? _displayOffsets[1] : default,
            BlockSize = _displayBlockSize,
            InsertionDeletionMode = _displayInsertionDeletionMode,
            MiddleOrientation = _displayOrientations.Length == 3 ? _displayOrientations[1] : new()
        };

        internal async Task ApplySettingsAsync(ImageViewSettings settings, CancellationToken token = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var requested = settings with { };
            ImageViewSettings.Validate(requested);
            if (_counts.Length == 2 && (requested.MiddleFrame != 1 || !requested.MiddleOrientation.IsIdentity || requested.MiddleOffset != default))
                throw new InvalidDataException("中央入力のない比較では中央の画像ページ番号を1、回転・反転を無効にしてください。");
            var numbers = requested.FrameNumbers(_counts.Length == 3);
            ImageComparisonEngine.ValidateSelection(_snapshots!, numbers, requested.Orientations(_counts.Length == 3), requested.Offsets(_counts.Length == 3));
            token.ThrowIfCancellationRequested();
            if (_saving) throw new InvalidOperationException("画像の保存が完了してから表示を変更してください。");
            var previous = CaptureSettings();
            SetSettingsControls(requested);
            var generation = _generation;
            try
            {
                var alphaOnly = requested with { HighlightAlpha = previous.HighlightAlpha } == previous;
                var operation = SetNumbersAsync(numbers, token, preserveRectangle: alphaOnly);
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
                _requestedHighlightAlpha = settings.HighlightAlpha; _highlightAlpha.Value = settings.HighlightAlpha;
                _requestedOrientations = settings.Orientations(_counts.Length == 3);
                _requestedOffsets = settings.Offsets(_counts.Length == 3);
                _requestedBlockSize = settings.BlockSize; _blockSizeControl.Value = settings.BlockSize;
                _requestedInsertionDeletionMode = settings.InsertionDeletionMode; _insertionDeletionMode.SelectedIndex = settings.InsertionDeletionMode;
                _showDifferences.IsChecked = settings.ShowDifferences;
                _zoom.Value = settings.Zoom; _opacity.Value = settings.OverlayOpacity;
                _reportAllFrames.IsChecked = settings.ReportAllFrames;
                _imageViews.SelectedIndex = settings.View switch { "Overlay" => 1, "PixelDifference" => 2, _ => 0 };
            }
            finally { _updatingSelectors = false; }
        }

        private static decimal ThresholdControlValue(double value)
            => decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);

        internal Task SetInsertionDeletionModeAsync(int mode, CancellationToken token = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (mode is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(mode));
            token.ThrowIfCancellationRequested();
            if (_saving) throw new InvalidOperationException("画像の保存が完了してから表示を変更してください。");
            _requestedInsertionDeletionMode = mode;
            return SetNumbersAsync(_numbers.ToArray(), token);
        }

        internal Task SetOrientationAsync(int pane, ImageOrientation orientation, CancellationToken token = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); ImageOrientation.Validate(orientation); token.ThrowIfCancellationRequested();
            if (_saving) throw new InvalidOperationException("画像の保存が完了してから表示を変更してください。");
            if (pane < 0 || pane >= _counts.Length) throw new ArgumentOutOfRangeException(nameof(pane));
            var next = _requestedOrientations.ToArray(); next[pane] = orientation;
            ImageComparisonEngine.ValidateSelection(_snapshots!, _numbers, next, _requestedOffsets);
            _requestedOrientations = next;
            return SetNumbersAsync(_numbers.ToArray(), token);
        }

        internal Task AddOffsetAsync(int pane, int dx, int dy, CancellationToken token = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
            if (_saving) throw new InvalidOperationException("画像の保存が完了してから表示を変更してください。");
            var next = ImageOffset.Move(_requestedOffsets, pane, dx, dy);
            ImageComparisonEngine.ValidateSelection(_snapshots!, _numbers, _requestedOrientations, next);
            _requestedOffsets = next;
            return SetNumbersAsync(_numbers.ToArray(), token);
        }

        private Task MoveChosenAsync(int dx, int dy)
        {
            var pane = _editPane.SelectedIndex;
            return pane < 0 || pane >= _counts.Length ? Task.CompletedTask : AddOffsetAsync(pane, dx, dy);
        }

        private Task RotateChosenAsync(int degrees)
        {
            var pane = _editPane.SelectedIndex;
            if (pane < 0 || pane >= _counts.Length) return Task.CompletedTask;
            var current = _requestedOrientations[pane];
            return SetOrientationAsync(pane, current with { Rotation = (current.Rotation + degrees + 360) % 360 });
        }

        private Task FlipChosenAsync(bool horizontal)
        {
            var pane = _editPane.SelectedIndex;
            if (pane < 0 || pane >= _counts.Length) return Task.CompletedTask;
            var current = _requestedOrientations[pane];
            return SetOrientationAsync(pane, horizontal ? current with { FlipHorizontal = !current.FlipHorizontal }
                : current with { FlipVertical = !current.FlipVertical });
        }
    }
}
