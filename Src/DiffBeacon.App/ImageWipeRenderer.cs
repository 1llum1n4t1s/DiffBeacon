using System.Globalization;

namespace DiffBeacon.App;

// WinIMerge v1.0.54 ImgDiffBuffer.hpp WipeEffect (1966–2053)、GPL v2以降。
// 原本SHA/無改変採取: tests/Fixtures/ImageWipes。guideは画素へ描かない。
internal sealed record ImageWipeSnapshot(ImageDragMode Mode, int Position)
{
    internal void Validate()
    {
        if (Mode is not (ImageDragMode.VerticalWipe or ImageDragMode.HorizontalWipe) || Position < 0)
            throw new ArgumentException("ワイプはvertical（Y）またはhorizontal（X）、位置は非負整数です。");
    }
    internal ImageWipeSnapshot Clamp(int width, int height)
    { Validate(); return this with { Position = Math.Min(Position, Mode == ImageDragMode.VerticalWipe ? height : width) }; }

    internal static ImageWipeSnapshot? Parse(string? mode, string? position)
    {
        if (mode is null && position is null) return null;
        if (mode is null || position is null || !int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException("--wipe-mode と --wipe-position は両方指定してください。位置は非負整数です。");
        var result = new ImageWipeSnapshot(mode switch { "vertical" => ImageDragMode.VerticalWipe,
            "horizontal" => ImageDragMode.HorizontalWipe, _ => throw new ArgumentException("--wipe-mode はvertical（Y）またはhorizontal（X）です。") }, value);
        result.Validate(); return result;
    }
}

// 包装用の一時表示状態。gestureと選択領域はprojectJSONへ保存しない。

internal static class ImageWipeRenderer
{
    internal static long Work(int width, int height, int count, ImageWipeSnapshot wipe)
    {
        wipe = wipe.Clamp(width, height);
        var changed = wipe.Mode == ImageDragMode.VerticalWipe ? (long)width * (height - wipe.Position)
            : (long)(width - wipe.Position) * height;
        return checked(((long)width * height + changed) * count);
    }

    internal static IReadOnlyList<ImageComparisonEngine.DecodedFrame> Render(
        IReadOnlyList<ImageComparisonEngine.DecodedFrame> baseline, ImageWipeSnapshot wipe,
        CancellationToken token = default, long maximumWork = ImageComparisonEngine.MaximumDecodeWork)
    {
        wipe.Validate(); token.ThrowIfCancellationRequested();
        var (width, height) = ValidateBaseline(baseline);
        wipe = wipe.Clamp(width, height);
        if (Work(width, height, baseline.Count, wipe) > maximumWork)
            throw new InvalidOperationException("画像ワイプを含む描画作業量が256Mピクセルを超えます。");
        var result = new ImageComparisonEngine.DecodedFrame[baseline.Count];
        for (var pane = 0; pane < baseline.Count; pane++)
        {
            token.ThrowIfCancellationRequested();
            var pixels = baseline[pane].Pixels.ToArray();
            var source = baseline[(pane + 1) % baseline.Count].Pixels;
            var startY = wipe.Mode == ImageDragMode.VerticalWipe ? wipe.Position : 0;
            var startX = wipe.Mode == ImageDragMode.HorizontalWipe ? wipe.Position : 0;
            for (var y = startY; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                var offset = checked((y * width + startX) * 4);
                source.AsSpan(offset, (width - startX) * 4).CopyTo(pixels.AsSpan(offset));
            }
            result[pane] = baseline[pane] with { Pixels = pixels };
        }
        return result;
    }

    internal static (int Width, int Height) ValidateBaseline(IReadOnlyList<ImageComparisonEngine.DecodedFrame> baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (baseline.Count is not (2 or 3)) throw new ArgumentException("ワイプは二者または三者です。");
        var width = baseline[0].Width; var height = baseline[0].Height;
        if (width <= 0 || height <= 0 || (long)width * height > ImageComparisonEngine.MaximumPixels
            || baseline.Any(frame => frame.Width != width || frame.Height != height || frame.Pixels.LongLength != (long)width * height * 4))
            throw new ArgumentException("ワイプには同寸法の共通BGRAキャンバスが必要です。");
        return (width, height);
    }
}
