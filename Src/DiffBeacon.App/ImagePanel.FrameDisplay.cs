namespace DiffBeacon.App;

public static partial class SpecializedViews
{
    public sealed partial class ImagePanel
    {
        private sealed record FrameDisplay(ImageOverlayRenderer.Result Display, ImageOverlayRenderer.Prepared? Prepared,
            long PreparationWork, long Revision, ImageOverlayRenderer.Settings Settings, bool OpacityExplicit);

        // 原画/設定/frameとoverlayを同じ採用単位にする。途中のNone画面を公開しない。
        private async Task<FrameDisplay> PrepareFrameDisplayAsync(ImageComparisonEngine.FrameComparison comparison,
            ImageComparisonEngine.DecodedFrame[] baseline, int selected, double threshold, bool show, int blockSize,
            double highlightAlpha, long generation, CancellationToken token)
        {
            await _displayWorkerTask.WaitAsync(token);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (_disposed || generation != _generation) throw new OperationCanceledException(token);
                var revision = _wipeRevision; var options = _applicationOptions.Current;
                var wipe = _activeWipe?.Clamp(baseline[0].Width, baseline[0].Height);
                var settings = new ImageOverlayRenderer.Settings(options.OverlayMode, _requestedOpacity, show,
                    options.BlinkDifferences, options.AnimationPeriod, options.BlinkPeriod, blockSize, threshold,
                    highlightAlpha, selected, wipe);
                var explicitOpacity = _requestedOpacityExplicit; var clock = RenderClock;
                var needsOverlay = settings.Mode != 0 || settings.ShowDifferences && settings.BlinkDifferences;
                var canvasWork = (long)baseline[0].Width * baseline[0].Height * (baseline.Length + 1);
                var display = await Task.Run(() =>
                {
                    var budget = new ImageDisplayWorkBudget(ImageComparisonEngine.MaximumDecodeWork, token);
                    budget.Reserve(canvasWork);
                    if (!needsOverlay)
                    {
                        var frames = wipe is null ? baseline : ImageWipeRenderer.Render(baseline, wipe, token,
                            ImageComparisonEngine.MaximumDecodeWork - budget.Used);
                        return new FrameDisplay(new(frames, new([], [], show, wipe)), null, 0, revision, settings, explicitOpacity);
                    }
                    budget.EnsureAvailable(ImageOverlayRenderer.RefreshWork(comparison, settings, true));
                    var before = budget.Used;
                    var prepared = ImageOverlayRenderer.FromComparison(comparison, settings, budget);
                    var work = budget.Used - before;
                    return new FrameDisplay(ImageOverlayRenderer.Render(prepared, clock, budget), prepared, work,
                        revision, settings, explicitOpacity);
                }, token);
                if (needsOverlay && OverlayCandidateReady is { } ready) await ready().WaitAsync(token);
                token.ThrowIfCancellationRequested();
                if (_disposed || generation != _generation) throw new OperationCanceledException(token);
                if (revision != _wipeRevision) continue;
                return display;
            }
        }
    }
}
