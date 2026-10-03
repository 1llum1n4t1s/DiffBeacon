namespace DiffBeacon.App;

// 確定表示の所有frame/sampleを共有する。後続UI操作はこの配列を変更しない。
// panel/store/timer/Bitmapを参照せず、原本identityはSnapshot所有bytesから得る。
internal sealed class ImageReportDisplayCapture
{
    internal IReadOnlyList<int> FrameNumbers { get; }
    internal IReadOnlyList<string> InputHashes { get; }
    internal IReadOnlyList<ImageComparisonEngine.DecodedFrame> Frames { get; }
    internal ImageComparisonEngine.FrameComparison Comparison { get; }
    internal ImageOverlayRenderer.Settings Settings { get; }
    internal ImageDisplaySample Sample { get; }
    internal ImageViewSettings ViewSettings { get; }

    internal ImageReportDisplayCapture(ImageAdoptedDisplay display, ImageComparisonEngine.FrameComparison comparison,
        IReadOnlyList<int> numbers, IReadOnlyList<ImageComparisonEngine.Snapshot> images, ImageViewSettings viewSettings)
    {
        FrameNumbers = Array.AsReadOnly(numbers.ToArray());
        InputHashes = Array.AsReadOnly(images.Select(image => image.SourceHash(CancellationToken.None)).ToArray());
        Frames = Array.AsReadOnly(display.Frames.ToArray()); Comparison = comparison;
        Settings = display.Settings; Sample = display.Sample; ViewSettings = viewSettings with { };
    }

    internal void ValidateImages(IReadOnlyList<ImageComparisonEngine.Snapshot> images, CancellationToken token)
    {
        if (images.Count != InputHashes.Count || images.Where((image, i) =>
            !StringComparer.OrdinalIgnoreCase.Equals(image.SourceHash(token), InputHashes[i])).Any())
            throw new InvalidDataException("表示した画像原本とレポート/包装の入力SHAが一致しません。");
    }

    internal void ValidateSettings(ImageViewSettings settings, int panes)
    {
        if (settings.Threshold != ViewSettings.Threshold || settings.BlockSize != ViewSettings.BlockSize
            || settings.InsertionDeletionMode != ViewSettings.InsertionDeletionMode || settings.HighlightAlpha != Settings.HighlightAlpha
            || settings.ShowDifferences != Settings.ShowDifferences || settings.OverlayOpacity != Settings.Alpha
            || !settings.Orientations(panes == 3).SequenceEqual(ViewSettings.Orientations(panes == 3))
            || !settings.Offsets(panes == 3).SequenceEqual(ViewSettings.Offsets(panes == 3))
            || !settings.FrameNumbers(panes == 3).SequenceEqual(FrameNumbers))
            throw new InvalidDataException("確定表示と包装の画像設定/ページが一致しません。");
    }

    internal void ValidateReport(ImageComparisonEngine.ReportInput input)
    {
        if (input.Threshold != Settings.Threshold || input.BlockSize != Settings.BlockSize
            || input.InsertionDeletionMode != ViewSettings.InsertionDeletionMode || input.HighlightAlpha != Settings.HighlightAlpha
            || input.ShowDifferences != Settings.ShowDifferences || input.SelectedDiffIndex != Settings.SelectedDiffIndex
            || input.Wipe != Sample.Wipe || input.FrameNumbers is { } numbers && !numbers.SequenceEqual(FrameNumbers)
            || !(input.Orientations ?? Enumerable.Repeat(new ImageOrientation(), Frames.Count).ToArray()).SequenceEqual(ViewSettings.Orientations(Frames.Count == 3))
            || !ImageOffset.Validate(input.Offsets, Frames.Count).SequenceEqual(ViewSettings.Offsets(Frames.Count == 3)))
            throw new InvalidDataException("確定表示とレポートの画像設定/ページが一致しません。");
    }
}
