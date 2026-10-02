namespace DiffBeacon.App;

// WinIMerge v1.0.54 (da639cdfaeca87aaad0eaceec509afa11ad61421)
// ImgDiffBuffer.hpp 1888–1964 の GetDiffColorFromPosition / MarkDiff を移植。
// GPL version 2 or later。原本・ライセンス: tests/Fixtures/ImageRegions、採取: ImageHighlight。
// overlay/wipeなし。色は原本constructorの通常色／選択色／削除色。
internal static class ImageRegionRenderer
{
    internal static IReadOnlyList<ImageComparisonEngine.DecodedFrame> Render(
        IReadOnlyList<ImageComparisonEngine.DecodedFrame> frames, ImageRegionDiffer.Result regions,
        int blockSize = 8, double highlightAlpha = .7, int selectedDiffIndex = -1,
        CancellationToken token = default, bool showDifferences = true, ImageLineAlignment.Result? alignment = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(regions.Regions);
        token.ThrowIfCancellationRequested();
        if (frames.Count is not (2 or 3)) throw new ArgumentException("画像は二者または三者です。", nameof(frames));
        if (blockSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(blockSize));
        if (!double.IsFinite(highlightAlpha) || highlightAlpha is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(highlightAlpha), "強調alphaは有限の0..1です。");
        if (selectedDiffIndex < -1 || selectedDiffIndex >= regions.Regions.Count)
            throw new ArgumentOutOfRangeException(nameof(selectedDiffIndex), "選択差分は未選択または存在する領域です。");
        var width = 0; var height = 0;
        foreach (var frame in frames)
        {
            token.ThrowIfCancellationRequested();
            if (frame is null || frame.Width <= 0 || frame.Height <= 0
                || (long)frame.Width * frame.Height > ImageComparisonEngine.MaximumPixels
                || frame.Pixels is null || frame.Pixels.LongLength != (long)frame.Width * frame.Height * 4)
                throw new ArgumentException("画像フレームの寸法またはBGRAバッファ長が不正です。", nameof(frames));
            width = Math.Max(width, frame.Width); height = Math.Max(height, frame.Height);
        }
        var offsets = ImageOffset.Validate(regions.Offsets, frames.Count);
        (width, height) = ImageOffset.Canvas(frames, offsets);
        var columns = (width + blockSize - 1) / blockSize;
        var rows = (height + blockSize - 1) / blockSize;
        var length = checked(columns * rows);
        if (regions.Width != width || regions.Height != height || regions.Columns != columns || regions.Rows != rows
            || regions.Pair01 is null || regions.Pair01.Length != length
            || regions.RegionIds is null || regions.RegionIds.Length != length || regions.Regions.Count > length
            || frames.Count == 2 && (regions.Pair21 is not null || regions.Pair02 is not null)
            || frames.Count == 3 && (regions.Pair21 is null || regions.Pair21.Length != length
                || regions.Pair02 is null || regions.Pair02.Length != length))
            throw new ArgumentException("領域のキャンバス・ブロックgridと画像が一致しません。", nameof(regions));
        var present = new bool[regions.Regions.Count];
        var conflicts = 0;
        for (var index = 0; index < present.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var region = regions.Regions[index];
            if (region.Id != index + 1 || region.Op is < 1 or > 4 || frames.Count == 2 && region.Op != 4
                || region.Left < 0 || region.Top < 0 || region.Right <= region.Left || region.Bottom <= region.Top
                || region.Right > columns || region.Bottom > rows)
                throw new ArgumentException("領域の番号・分類・矩形が不正です。", nameof(regions));
            if (region.Op == 4) conflicts++;
        }
        for (var index = 0; index < length; index++)
        {
            if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
            var id = regions.RegionIds[index];
            if (id < 0 || id > present.Length || regions.Pair01[index] > 1
                || regions.Pair21 is not null && regions.Pair21[index] > 1
                || regions.Pair02 is not null && regions.Pair02[index] > 1
                || (id != 0) != (regions.Pair01[index] != 0 || regions.Pair21 is not null && regions.Pair21[index] != 0))
                throw new ArgumentException("領域gridの番号または比較blockが不正です。", nameof(regions));
            if (id == 0) continue;
            var region = regions.Regions[id - 1]; var x = index % columns; var y = index / columns;
            if (x < region.Left || x >= region.Right || y < region.Top || y >= region.Bottom)
                throw new ArgumentException("領域gridが領域矩形の外にあります。", nameof(regions));
            present[id - 1] = true;
        }
        if (conflicts != regions.ConflictCount || present.Contains(false))
            throw new ArgumentException("領域の個数またはConflict数が不正です。", nameof(regions));

        bool[]? deleted = null;
        if (alignment is not null)
        {
            if (alignment.Frames.Count != frames.Count || alignment.Frames.Where((frame, pane) =>
                frame.Width != frames[pane].Width || frame.Height != frames[pane].Height).Any())
                throw new ArgumentException("整列の寸法・pane数が描画画像と一致しません。", nameof(alignment));
            var axis = alignment.Horizontal ? frames[0].Width : frames[0].Height;
            deleted = new bool[axis];
            foreach (var interval in alignment.LineDiffInfos)
            {
                token.ThrowIfCancellationRequested();
                if (interval.DisplayBegin < 0 || interval.DisplayEndMaximum < interval.DisplayBegin || interval.DisplayEndMaximum >= axis)
                    throw new ArgumentException("整列の表示区間が描画画像の範囲外です。", nameof(alignment));
                Array.Fill(deleted, true, interval.DisplayBegin, interval.DisplayEndMaximum - interval.DisplayBegin + 1);
            }
        }
        var rendered = new ImageComparisonEngine.DecodedFrame[frames.Count];
        for (var pane = 0; pane < frames.Count; pane++)
        {
            token.ThrowIfCancellationRequested();
            var original = frames[pane];
            var pixels = new byte[checked(width * height * 4)];
            for (var y = 0; y < original.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                original.Pixels.AsSpan(y * original.Width * 4, original.Width * 4)
                    .CopyTo(pixels.AsSpan(((y + offsets[pane].Y) * width + offsets[pane].X) * 4, original.Width * 4));
            }
            for (var by = 0; by < rows; by++)
            for (var bx = 0; bx < columns; bx++)
            {
                token.ThrowIfCancellationRequested();
                var id = regions.RegionIds[by * columns + bx];
                if (id == 0 || !showDifferences) continue;
                var op = regions.Regions[id - 1].Op;
                if (pane == 0 && op == 3 || pane == 2 && op == 1) continue;
                var blue = 64; var green = id - 1 == selectedDiffIndex ? 64 : 255; var red = 255;
                var blockRows = Math.Min(blockSize, height - by * blockSize);
                for (var i = 0; i < blockRows; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var y = by * blockSize + i;
                    var blockColumns = Math.Min(blockSize, width - bx * blockSize);
                    for (var j = 0; j < blockColumns; j++)
                    {
                        var offset = (y * width + bx * blockSize + j) * 4;
                        if (pixels[offset + 3] != 0)
                        {
                            pixels[offset] = (byte)(pixels[offset] * (1 - highlightAlpha) + blue * highlightAlpha);
                            pixels[offset + 1] = (byte)(pixels[offset + 1] * (1 - highlightAlpha) + green * highlightAlpha);
                            pixels[offset + 2] = (byte)(pixels[offset + 2] * (1 - highlightAlpha) + red * highlightAlpha);
                        }
                        else
                        {
                            // 原本は透明画素のみ、ghostを含む変更区間全体へ削除色を使う。
                            var localX = bx * blockSize + j - offsets[pane].X;
                            var localY = y - offsets[pane].Y;
                            var deletedColor = deleted is not null && localX >= 0 && localY >= 0
                                && localX < original.Width && localY < original.Height
                                && deleted[alignment!.Horizontal ? localX : localY];
                            pixels[offset] = deletedColor ? (byte)192 : (byte)blue;
                            pixels[offset + 1] = deletedColor ? (byte)192 : (byte)green;
                            pixels[offset + 2] = deletedColor ? (byte)(id - 1 == selectedDiffIndex ? 240 : 192) : (byte)red;
                            pixels[offset + 3] = (byte)(255 * highlightAlpha);
                        }
                    }
                }
            }
            rendered[pane] = new(original.Number, width, height, pixels);
        }
        token.ThrowIfCancellationRequested();
        return rendered;
    }
}
