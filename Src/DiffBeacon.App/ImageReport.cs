using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace DiffBeacon.App;

// 確定済みの画像・原本領域強調・原画を自己完結HTMLへ出力する。
internal static class ImageReport
{
    internal static string Create(ImageComparisonEngine.Snapshot left, ImageComparisonEngine.Snapshot right,
        string leftTitle, string rightTitle, double threshold = 0, int? leftFrame = null, int? rightFrame = null,
        CancellationToken token = default, int maximumBytes = ProjectReport.MaximumBytes)
    {
        ImageComparisonEngine.ValidateComparison(left, right, leftFrame, rightFrame, threshold);
        return Create(new([left, right], threshold, leftFrame.HasValue ? [leftFrame.Value, rightFrame!.Value] : null),
            [leftTitle, rightTitle], token, maximumBytes);
    }

    internal static string Create(ImageComparisonEngine.ReportInput input, IReadOnlyList<string> titles,
        CancellationToken token = default, int maximumBytes = ProjectReport.MaximumBytes,
        IImageDisplayClock? clock = null, Action<IReadOnlyList<int>, ImageDisplaySample>? observe = null)
    {
        token.ThrowIfCancellationRequested();
        var images = input.Images;
        var orientations = input.Orientations?.ToArray();
        var offsets = input.Offsets?.ToArray();
        ImageComparisonEngine.ValidateComparison(images, input.FrameNumbers, input.Threshold, orientations, offsets);
        ImageComparisonEngine.ValidateInsertionDeletionMode(input.InsertionDeletionMode);
        ImageComparisonEngine.ValidateHighlightAlpha(input.HighlightAlpha);
        input.Wipe?.Validate();
        input.DisplayCapture?.ValidateImages(images, token);
        input.DisplayCapture?.ValidateReport(input);
        var displaySettings = input.DisplayCapture?.Settings ?? input.DisplaySettings;
        displaySettings?.Validate();
        if (input.EditedFrames is { } edited && (edited.Count != images.Count || images.Any(image => image.FrameCount != 1)
            || edited.Any(frame => frame.Number != 1))) throw new ArgumentException("編集済みレポートは静止画の全入力が必要です。");
        if (titles.Count != images.Count) throw new ArgumentException("全画像の見出しが必要です。");
        var selected = input.FrameNumbers is not null;
        var count = selected ? 1 : images.Max(image => image.FrameCount);
        // 整列後の寸法は画素を調べないと確定しない。全ページの予算成立前に描画しない。
        using var preparation = !selected && count > 1 && input.InsertionDeletionMode != 0
            ? PrepareAll(input, orientations, offsets, count, token) : null;
        if (!selected && count > 1 && input.InsertionDeletionMode == 0 && (input.Wipe is not null || NeedsDisplay(displaySettings)))
            PreflightDisplay(input, orientations, offsets, count, token);
        try
        {
        var html = new BoundedHtml(Math.Min(maximumBytes, ProjectReport.MaximumBytes), token);
        html.Append("<!doctype html><html lang=\"ja\"><head><meta charset=\"utf-8\"><title>画像比較</title>"
            + "<style>body{font-family:system-ui,sans-serif}table{border-collapse:collapse;width:100%}"
            + "th,td{border:1px solid #888;padding:.5rem;vertical-align:top}img{max-width:100%;height:auto;"
            + "background:repeating-conic-gradient(#ddd 0% 25%,#fff 0% 50%) 0 0/16px 16px}"
            + "caption{text-align:left;padding:.5rem}</style></head>"
            + "<body data-mode=\"Image\" data-different=\"");
        var differentPosition = html.Length;
        html.Append("true\" data-frame-mode=\"");
        html.Append(selected ? "selected" : "all");
        html.Append("\" data-left-frames=\""); html.Number(images[0].FrameCount);
        html.Append("\" data-right-frames=\""); html.Number(images[^1].FrameCount);
        if (images.Count == 3) { html.Append("\" data-middle-frames=\""); html.Number(images[1].FrameCount); }
        html.Append("\" data-threshold=\""); html.Append(input.Threshold.ToString("R", CultureInfo.InvariantCulture));
        html.Append("\" data-highlight-alpha=\""); html.Append(input.HighlightAlpha.ToString("R", CultureInfo.InvariantCulture));
        if (input.InsertionDeletionMode != 0)
        { html.Append("\" data-insertion-deletion-mode=\""); html.Number(input.InsertionDeletionMode); }
        if (displaySettings is not null)
        {
            html.Append("\" data-overlay-mode=\""); html.Number(displaySettings.Mode);
            html.Append("\" data-overlay-alpha=\""); html.Append(displaySettings.Alpha.ToString("R", CultureInfo.InvariantCulture));
            html.Append("\" data-overlay-blink=\""); html.Append(displaySettings.BlinkDifferences ? "true" : "false");
            html.Append("\" data-overlay-period=\""); html.Number(displaySettings.AnimationPeriod);
            html.Append("\" data-blink-period=\""); html.Number(displaySettings.BlinkPeriod);
        }
        html.Append("\"><h1>画像比較</h1><table><caption>閾値: "); html.Append(input.Threshold.ToString("R", CultureInfo.InvariantCulture));
        html.Append(" / フレーム: "); html.Append(selected ? "選択した組" : "全同番号フレーム");
        html.Append("</caption><thead><tr>");
        var sides = images.Count == 3 ? new[] { "left", "middle", "right" } : ["left", "right"];
        for (var i = 0; i < titles.Count; i++) { html.Append("<th data-side=\""); html.Append(sides[i]); html.Append("\">"); html.Escape(titles[i]); html.Append("</th>"); }
        html.Append("<th>左右の画素差・領域</th></tr></thead><tbody>");
        var different = !selected && images.Select(image => image.FrameCount).Distinct().Count() != 1;
        long alignmentWork = 0;
        long canvasWork = 0;
        var wipe = input.Wipe;
        var selectedDiffIndex = input.SelectedDiffIndex;
        var lastTuple = input.DisplayCapture?.FrameNumbers;
        var lastComparison = input.DisplayCapture?.Comparison;
        ImageOverlayRenderer.Result? lastDisplay = input.DisplayCapture is { } initial ? new(initial.Frames, initial.Sample) : null;
        clock ??= new ImageSystemDisplayClock();
        for (var index = 1; index <= count; index++)
        {
            token.ThrowIfCancellationRequested();
            var numbers = input.FrameNumbers ?? images.Select(image => Math.Min(index, image.FrameCount)).ToArray();
            var reuse = lastTuple is not null && lastDisplay is not null && numbers.SequenceEqual(lastTuple);
            // spoolはページ順なので、captureを再利用する場合も当該ページを消費する。
            var prepared = preparation?.Next();
            var set = reuse ? lastComparison! : prepared is not null
                ? ImageComparisonEngine.ComparePrepared(prepared, input.Threshold, true, token, input.BlockSize, offsets)
                : input.EditedFrames is { } raw
                ? ImageComparisonEngine.CompareDecoded(raw, input.Threshold, true, token, orientations, input.BlockSize, offsets,
                    input.InsertionDeletionMode, ImageLineDiffer.MaximumWork - alignmentWork, ImageComparisonEngine.MaximumDecodeWork - canvasWork)
                : ImageComparisonEngine.DecodeSelection(images, numbers, input.Threshold, true, token, orientations, input.BlockSize, offsets,
                    input.InsertionDeletionMode, ImageLineDiffer.MaximumWork - alignmentWork, ImageComparisonEngine.MaximumDecodeWork - canvasWork);
            alignmentWork += set.AlignmentWork;
            canvasWork += set.CanvasWork;
            var a = set.Frames[0]; var b = set.Frames[^1];
            token.ThrowIfCancellationRequested();
            var comparison = set.Pixels;
            IReadOnlyList<ImageComparisonEngine.DecodedFrame> rendered;
            ImageDisplaySample sample;
            wipe = wipe?.Clamp(set.Regions.Width, set.Regions.Height);
            // CompareImages1161–1162の選択縮小を次ページへ継承する。
            selectedDiffIndex = Math.Min(selectedDiffIndex, set.Regions.Regions.Count - 1);
            if (reuse) { rendered = lastDisplay!.Frames; sample = lastDisplay.Sample; }
            else if (NeedsDisplay(displaySettings))
            {
                var settings = PageSettings(input with { SelectedDiffIndex = selectedDiffIndex }, displaySettings!, set.Regions.Regions.Count, wipe);
                var budget = new ImageDisplayWorkBudget(ImageComparisonEngine.MaximumDecodeWork - canvasWork, token);
                budget.EnsureAvailable(ImageOverlayRenderer.RefreshWork(set, settings, true));
                var baseline = ImageOverlayRenderer.FromComparison(set, settings, budget);
                var display = ImageOverlayRenderer.Render(baseline, clock, budget);
                canvasWork = checked(canvasWork + budget.Used); rendered = display.Frames; sample = display.Sample;
            }
            else
            {
                rendered = ImageRegionRenderer.Render(set.Frames, set.Regions, blockSize: input.BlockSize,
                    highlightAlpha: input.HighlightAlpha, selectedDiffIndex: selectedDiffIndex,
                    token: token, showDifferences: input.ShowDifferences, alignment: set.Alignment);
                if (wipe is not null)
                {
                    rendered = ImageWipeRenderer.Render(rendered, wipe, token, ImageComparisonEngine.MaximumDecodeWork - canvasWork);
                    canvasWork += ImageWipeRenderer.Work(rendered[0].Width, rendered[0].Height, rendered.Count, wipe);
                }
                sample = new([], [], input.ShowDifferences, wipe);
            }
            lastTuple = numbers.ToArray(); lastComparison = set; lastDisplay = new(rendered, sample);
            observe?.Invoke(Array.AsReadOnly(numbers.ToArray()), sample);
            different |= set.Regions.Regions.Count > 0;
            html.Append("<tr data-left-frame=\""); html.Frame(a?.Number);
            html.Append("\" data-right-frame=\""); html.Frame(b?.Number);
            html.Append("\" data-different-pixels=\""); html.Number(comparison.DifferentPixels);
            html.Append("\" data-total-pixels=\""); html.Number(comparison.TotalPixels);
            if (images.Count == 3) { html.Append("\" data-middle-frame=\""); html.Number(set.Frames[1].Number); }
            html.Append("\" data-difference-count=\""); html.Number(set.Regions.Regions.Count);
            html.Append("\" data-conflict-count=\""); html.Number(set.Regions.ConflictCount); html.Append("\">");
            for (var i = 0; i < rendered.Count; i++)
            {
                html.Append("<td>"); AppendSource(html, rendered[i], sides[i], titles[i], token);
                html.Append("<details><summary>原画</summary>"); AppendSource(html, (set.OriginalFrames ?? set.Frames)[i], sides[i] + "-original", titles[i], token); html.Append("</details></td>");
            }
            html.Append("<td><p>左右の差分画素: "); html.Number(comparison.DifferentPixels);
            html.Append(" / "); html.Number(comparison.TotalPixels); html.Append("</p>");
            AppendImage(html, comparison.DifferencePixels!, comparison.Width, comparison.Height,
                "difference", null, "ピクセル差分", token);
            html.Append("<p>領域 "); html.Number(set.Regions.Regions.Count); html.Append(" / 競合 "); html.Number(images.Count == 3 ? set.Regions.ConflictCount : 0); html.Append("</p><ol>");
            foreach (var region in set.Regions.Regions)
            {
                token.ThrowIfCancellationRequested(); html.Append("<li data-region=\""); html.Number(region.Id); html.Append("\" data-op=\""); html.Number(region.Op); html.Append("\">");
                html.Append(images.Count == 2 ? "差分" : region.Op switch { 1 => "左だけ", 2 => "中央だけ", 3 => "右だけ", _ => "競合" }); html.Append("</li>");
            }
            html.Append("</ol>");
            html.Append("</td></tr>");
        }
        html.Append("</tbody></table></body></html>");
        if (!different) html.MarkSame(differentPosition);
        return html.Finish();
        }
        catch (Exception error) { preparation?.RecordFailure(error); throw; }
    }

    private static bool NeedsDisplay(ImageOverlayRenderer.Settings? settings)
        => settings is not null && (settings.Mode != 0 || settings.ShowDifferences && settings.BlinkDifferences);

    internal static ImageReportPreparation? PreflightComparison(ImageComparisonEngine.ReportInput input, CancellationToken token)
    {
        if (input.FrameNumbers is not null || !NeedsDisplay(input.DisplaySettings) || input.Images.Max(image => image.FrameCount) <= 1) return null;
        var count = input.Images.Max(image => image.FrameCount);
        if (input.InsertionDeletionMode != 0) return PrepareAll(input, input.Orientations, input.Offsets, count, token);
        PreflightDisplay(input, input.Orientations, input.Offsets, count, token); return null;
    }

    private static ImageOverlayRenderer.Settings PageSettings(ImageComparisonEngine.ReportInput input,
        ImageOverlayRenderer.Settings settings, int regionCount, ImageWipeSnapshot? wipe)
        => settings with { Threshold = input.Threshold, BlockSize = input.BlockSize, HighlightAlpha = input.HighlightAlpha,
            SelectedDiffIndex = Math.Min(input.SelectedDiffIndex, regionCount - 1), ShowDifferences = input.ShowDifferences, Wipe = wipe };

    private static long AdditionalWork(ImageComparisonEngine.ReportInput input, int page, int width, int height,
        IReadOnlyList<ImageComparisonEngine.DecodedFrame>? frames, IReadOnlyList<ImageOrientation>? orientations, ImageWipeSnapshot? wipe)
    {
        var numbers = input.Images.Select(image => Math.Min(page, image.FrameCount)).ToArray();
        if (page == 1 && input.DisplayCapture is { } capture && numbers.SequenceEqual(capture.FrameNumbers)) return 0;
        var settings = input.DisplayCapture?.Settings ?? input.DisplaySettings;
        if (!NeedsDisplay(settings)) return wipe is null ? 0 : ImageWipeRenderer.Work(width, height, input.Images.Count, wipe);
        long source = 0, middle = 0;
        for (var pane = 0; pane < input.Images.Count; pane++)
        {
            var size = frames is null ? input.Images[pane].GetDimensions(numbers[pane]) : (frames[pane].Width, frames[pane].Height);
            var area = (long)size.Item1 * size.Item2; source += area; if (pane == 1) middle = area;
        }
        return ImageOverlayRenderer.RefreshWorkMetadata(width, height, input.Images.Count, source, middle,
            PageSettings(input, settings!, int.MaxValue, wipe));
    }

    private static void PreflightDisplay(ImageComparisonEngine.ReportInput input,
        IReadOnlyList<ImageOrientation>? orientations, IReadOnlyList<ImageOffset>? offsets, int count, CancellationToken token)
    {
        var positions = ImageOffset.Validate(offsets, input.Images.Count); var wipe = input.Wipe; long work = 0;
        for (var page = 1; page <= count; page++)
        {
            token.ThrowIfCancellationRequested(); var width = 0; var height = 0;
            for (var pane = 0; pane < input.Images.Count; pane++)
            {
                var size = input.Images[pane].GetDimensions(Math.Min(page, input.Images[pane].FrameCount));
                var swap = orientations?[pane].SwapsDimensions == true;
                width = Math.Max(width, checked((swap ? size.Height : size.Width) + positions[pane].X));
                height = Math.Max(height, checked((swap ? size.Width : size.Height) + positions[pane].Y));
            }
            wipe = wipe?.Clamp(width, height);
            work = checked(work + (long)width * height * (input.Images.Count + 1) + AdditionalWork(input, page, width, height, null, orientations, wipe));
            if (work > ImageComparisonEngine.MaximumDecodeWork) throw new InvalidOperationException("overlay/wipeを含む描画作業量が256Mピクセルを超えます。");
        }
    }

    private static ImageReportPreparation PrepareAll(ImageComparisonEngine.ReportInput input,
        IReadOnlyList<ImageOrientation>? orientations, IReadOnlyList<ImageOffset>? offsets, int count, CancellationToken token)
    {
        var preparation = new ImageReportPreparation(token);
        try
        {
            long alignmentWork = 0, canvasWork = 0;
            var wipe = input.Wipe;
            for (var page = 1; page <= count; page++)
            {
                token.ThrowIfCancellationRequested();
                var numbers = input.Images.Select(image => Math.Min(page, image.FrameCount)).ToArray();
                var prepared = ImageComparisonEngine.PrepareSelection(input.Images, numbers, input.Threshold, token,
                    orientations, offsets, input.InsertionDeletionMode, ImageLineDiffer.MaximumWork - alignmentWork,
                    ImageComparisonEngine.MaximumDecodeWork - canvasWork);
                alignmentWork = checked(alignmentWork + prepared.AlignmentWork);
                canvasWork = checked(canvasWork + prepared.CanvasWork);
                var canvas = ImageOffset.Canvas(prepared.Frames, ImageOffset.Validate(offsets, input.Images.Count));
                wipe = wipe?.Clamp(canvas.Width, canvas.Height);
                canvasWork = checked(canvasWork + AdditionalWork(input, page, canvas.Width, canvas.Height, prepared.Frames, orientations, wipe));
                    if (canvasWork > ImageComparisonEngine.MaximumDecodeWork) throw new InvalidOperationException("画像ワイプを含む描画作業量が256Mピクセルを超えます。");
                preparation.Add(prepared);
            }
            preparation.Rewind();
            return preparation;
        }
        catch (Exception error) { preparation.RecordFailure(error); preparation.Dispose(); throw; }
    }

    private static void AppendSource(BoundedHtml html, ImageComparisonEngine.DecodedFrame? frame,
        string side, string title, CancellationToken token)
    {
        if (frame is null) { html.Append("<p>対応するフレームなし</p>"); return; }
        html.Append("<p>フレーム "); html.Number(frame.Number); html.Append("</p>");
        AppendImage(html, frame.Pixels, frame.Width, frame.Height, side, frame.Number, title, token);
    }

    private static void AppendImage(BoundedHtml html, byte[] pixels, int width, int height,
        string side, int? frame, string title, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var hash = ImageComparisonEngine.PixelHash(pixels, token);
        html.Append("<img data-side=\""); html.Append(side); html.Append("\"");
        if (frame.HasValue) { html.Append(" data-frame=\""); html.Number(frame.Value); html.Append("\""); }
        html.Append(" data-width=\""); html.Number(width);
        html.Append("\" data-height=\""); html.Number(height);
        html.Append("\" data-pixel-sha256=\""); html.Append(hash);
        html.Append("\" alt=\""); html.Escape(title); html.Append("\" src=\"data:image/png;base64,");
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            // Unpremul の pixmap を直接エンコードし、透明画素の色成分も保持する。
            using var pixmap = new SKPixmap(new SKImageInfo(width, height, SKColorType.Bgra8888,
                SKAlphaType.Unpremul), pinned.AddrOfPinnedObject(), checked(width * 4));
            token.ThrowIfCancellationRequested();
            using var png = pixmap.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidDataException("画像レポートのPNGを作成できません。");
            token.ThrowIfCancellationRequested();
            html.Base64(png.AsSpan());
        }
        finally { pinned.Free(); }
        token.ThrowIfCancellationRequested();
        html.Append("\">");
    }

    private sealed class BoundedHtml(int maximumBytes, CancellationToken token)
    {
        private readonly StringBuilder _text = new();
        private long _bytes;
        internal int Length => _text.Length;

        private void Ensure(long bytes)
        {
            token.ThrowIfCancellationRequested();
            if (bytes > maximumBytes - _bytes)
                throw new InvalidDataException("画像HTMLレポートが出力サイズ上限（最大32 MiB）を超えます。");
        }

        internal void Append(string value)
        {
            token.ThrowIfCancellationRequested();
            var bytes = Encoding.UTF8.GetByteCount(value);
            Ensure(bytes);
            _text.Append(value);
            _bytes += bytes;
        }

        internal void Number(long value) => Append(value.ToString(CultureInfo.InvariantCulture));
        internal void Frame(int? value) { if (value.HasValue) Number(value.Value); else Append("none"); }

        internal void Escape(string value)
        {
            // 全文を先にエスケープしない。UTF-8 とサロゲートペアを rune 単位で計上する。
            token.ThrowIfCancellationRequested();
            foreach (var rune in value.EnumerateRunes())
                Append(rune.Value switch
                {
                    '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&#39;",
                    _ => rune.ToString()
                });
            token.ThrowIfCancellationRequested();
        }

        internal void Base64(ReadOnlySpan<byte> png)
        {
            // 増幅後の全長を変換前に確認し、フレームごとの巨大な文字列や byte[] を作らない。
            Ensure(checked(((long)png.Length + 2) / 3 * 4));
            const int chunkBytes = 12_288; // 3の倍数で、途中の padding を避ける。
            for (var offset = 0; offset < png.Length; offset += chunkBytes)
            {
                token.ThrowIfCancellationRequested();
                Append(Convert.ToBase64String(png.Slice(offset, Math.Min(chunkBytes, png.Length - offset))));
            }
            token.ThrowIfCancellationRequested();
        }

        internal void MarkSame(int position)
        {
            Ensure(1);
            _text.Remove(position, 4).Insert(position, "false");
            _bytes++;
        }

        internal string Finish()
        {
            token.ThrowIfCancellationRequested();
            Ensure(0);
            var result = _text.ToString();
            token.ThrowIfCancellationRequested();
            return result;
        }
    }
}
