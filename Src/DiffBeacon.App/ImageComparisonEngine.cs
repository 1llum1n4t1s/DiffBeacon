using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SkiaSharp;

namespace DiffBeacon.App;

// GUI と CLI の復号・ピクセル判定を共有する。Avalonia の初期化は不要。
internal static class ImageComparisonEngine
{
    internal const int MaximumFileBytes = 64 * 1024 * 1024;
    internal const long MaximumPixels = 16_000_000;
    internal const int MaximumFrames = 1024;
    internal const long MaximumDecodeWork = 256_000_000;

    internal sealed class Snapshot
    {
        private readonly byte[] _bytes;
        private readonly ApngImage? _animation;
        private readonly TiffImage? _tiff;
        internal int Width { get; }
        internal int Height { get; }
        internal int FrameCount { get; }
        internal long Pixels => (long)Width * Height;

        internal Snapshot(byte[] bytes, int width, int height, int frameCount, ApngImage? animation = null, TiffImage? tiff = null)
        { _bytes = bytes; Width = width; Height = height; FrameCount = frameCount; _animation = animation; _tiff = tiff; }

        internal (int Width, int Height) GetDimensions(int frame)
        {
            ValidateFrame(this, frame);
            return _tiff is null ? (Width, Height) : (_tiff.Pages[frame - 1].Width, _tiff.Pages[frame - 1].Height);
        }

        internal long DecodeWork(int frame)
        { ValidateFrame(this, frame); return _tiff is null ? Pixels * frame : _tiff.Pages[frame - 1].Work; }

        internal DecodedFrame Decode(int frame, CancellationToken token)
        {
            ValidateFrame(this, frame);
            token.ThrowIfCancellationRequested();
            if (_tiff is not null) return _tiff.Decode(frame, token);
            if (_animation is not null) return _animation.Decode(frame, token);
            using var data = SKData.CreateCopy(_bytes);
            using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("画像のデコーダーを作成できません。");
            token.ThrowIfCancellationRequested();
            if (codec.FrameCount > 0 && (!codec.GetFrameInfo(frame - 1, out var frameInfo) || !frameInfo.FullyRecieved))
                throw new InvalidDataException($"画像フレーム {frame} のデータが不完全です。");
            token.ThrowIfCancellationRequested();
            var pixels = new byte[checked(Width * Height * 4)];
            var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                // prior=-1 なら codec が依存する前フレームと disposal/blend を合成する。
                var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                token.ThrowIfCancellationRequested();
                var result = codec.GetPixels(info, pinned.AddrOfPinnedObject(), checked(Width * 4), new SKCodecOptions(frame - 1, -1));
                token.ThrowIfCancellationRequested();
                if (result != SKCodecResult.Success)
                    throw new InvalidDataException($"画像フレーム {frame} を完全に復号できません: {result}");
            }
            finally { pinned.Free(); }
            return new DecodedFrame(frame, Width, Height, pixels);
        }
    }

    internal sealed record DecodedFrame(int Number, int Width, int Height, byte[] Pixels);
    internal sealed record PixelComparison(long DifferentPixels, long TotalPixels, int Width, int Height, byte[]? DifferencePixels);
    internal sealed record FrameResult(int? LeftFrame, int? RightFrame, int? LeftWidth, int? LeftHeight,
        int? RightWidth, int? RightHeight, long DifferentPixels, long TotalPixels,
        string? LeftPixelSha256, string? RightPixelSha256, int? MiddleFrame, int? MiddleWidth, int? MiddleHeight,
        string? MiddlePixelSha256, IReadOnlyList<ImageRegionDiffer.Region> Regions, int ConflictCount,
        IReadOnlyList<string> HighlightPixelSha256);
    internal sealed record ComparisonResult(bool Different, int LeftFrames, int RightFrames, double Threshold,
        string Mode, IReadOnlyList<FrameResult> Frames, int? MiddleFrames);
    internal sealed record FrameComparison(IReadOnlyList<DecodedFrame> Frames, ImageRegionDiffer.Result Regions,
        PixelComparison Pixels, IReadOnlyList<DecodedFrame>? OriginalFrames = null, long AlignmentWork = 0,
        ImageLineAlignment.Result? Alignment = null, long CanvasWork = 0);
    internal sealed record ReportInput(IReadOnlyList<Snapshot> Images, double Threshold, IReadOnlyList<int>? FrameNumbers,
        int SelectedDiffIndex = -1, bool ShowDifferences = true, IReadOnlyList<DecodedFrame>? EditedFrames = null,
        IReadOnlyList<ImageOrientation>? Orientations = null, int BlockSize = 8, IReadOnlyList<ImageOffset>? Offsets = null,
        int InsertionDeletionMode = 0);

    internal static async Task<Snapshot> OpenAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > MaximumFileBytes) throw new InvalidOperationException("画像ファイルの上限は各64 MiBです。");
        using var content = new MemoryStream();
        var buffer = new byte[65_536];
        int read;
        while ((read = await file.ReadAsync(buffer, token)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (content.Length + read > MaximumFileBytes) throw new InvalidOperationException("画像ファイルの上限は各64 MiBです。");
            content.Write(buffer.AsSpan(0, read));
        }
        var bytes = content.ToArray();
        // フレーム情報の走査は native が行うため、UI スレッド上で実行しない。
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if (ApngImage.TryOpen(bytes, token) is { } animation)
                return new Snapshot(bytes, animation.Width, animation.Height, animation.FrameCount, animation);
            if (TiffImage.TryOpen(bytes, token) is { } tiff)
                return new Snapshot(bytes, tiff.Pages[0].Width, tiff.Pages[0].Height, tiff.Pages.Count, tiff: tiff);
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("対応する画像データを読み込めません。");
            var info = codec.Info;
            ValidateDimensions(info.Width, info.Height);
            var count = Math.Max(1, codec.FrameCount);
            token.ThrowIfCancellationRequested();
            if (count > MaximumFrames) throw new InvalidOperationException("画像フレームの上限は各1024です。");
            return new Snapshot(bytes, info.Width, info.Height, count);
        }, token);
    }

    internal static void ValidateSelection(Snapshot left, Snapshot right, int leftFrame, int rightFrame)
        => ValidateSelection([left, right], [leftFrame, rightFrame]);

    internal static void ValidateSelection(IReadOnlyList<Snapshot> images, IReadOnlyList<int> numbers,
        IReadOnlyList<ImageOrientation>? orientations = null, IReadOnlyList<ImageOffset>? offsets = null)
    {
        ValidateImages(images);
        if (numbers.Count != images.Count) throw new ArgumentException("全入力のフレーム番号が必要です。");
        ValidateOrientations(orientations, images.Count);
        long work = 0;
        for (var i = 0; i < images.Count; i++) work = checked(work + images[i].DecodeWork(numbers[i]) + TransformWork(images[i], numbers[i], orientations?[i]));
        ValidateWork(work);
        ValidateCanvas(images, numbers, orientations, offsets);
    }

    internal static void ValidateComparison(Snapshot left, Snapshot right, int? leftFrame, int? rightFrame,
        double threshold)
    {
        if (leftFrame.HasValue != rightFrame.HasValue) throw new ArgumentException("左右両方のフレームを指定してください。");
        ValidateComparison([left, right], leftFrame.HasValue ? [leftFrame.Value, rightFrame!.Value] : null, threshold);
    }

    internal static void ValidateComparison(IReadOnlyList<Snapshot> images, IReadOnlyList<int>? numbers, double threshold,
        IReadOnlyList<ImageOrientation>? orientations = null, IReadOnlyList<ImageOffset>? offsets = null)
    {
        ValidateThreshold(threshold); ValidateImages(images);
        ValidateOrientations(orientations, images.Count);
        if (numbers is not null) { ValidateSelection(images, numbers, orientations, offsets); return; }
        // 短い入力は最後に選ばれたページを保持し、全ページ出力にもその復号費用を含める。
        var pages = images.Max(image => image.FrameCount);
        long work = 0;
        long canvasWork = 0;
        for (var page = 1; page <= pages; page++)
        {
            var current = images.Select(image => Math.Min(page, image.FrameCount)).ToArray();
            for (var i = 0; i < images.Count; i++) work = checked(work + images[i].DecodeWork(current[i]) + TransformWork(images[i], current[i], orientations?[i]));
            canvasWork = checked(canvasWork + ValidateCanvas(images, current, orientations, offsets) * (images.Count + 1));
        }
        ValidateWork(work);
        if (canvasWork > MaximumDecodeWork) throw new InvalidOperationException("画像の描画作業量が256Mピクセルを超えます。");
    }

    private static void ValidateImages(IReadOnlyList<Snapshot> images)
    {
        if (images.Count is not (2 or 3)) throw new ArgumentException("画像入力は二者または三者です。");
        foreach (var image in images) ValidateDimensions(image.Width, image.Height);
    }

    private static long TransformWork(Snapshot image, int frame, ImageOrientation? orientation)
    { var size = image.GetDimensions(frame); return orientation is null || orientation.IsIdentity ? 0 : (long)size.Width * size.Height; }

    internal static void ValidateOrientations(IReadOnlyList<ImageOrientation>? orientations, int count)
    {
        if (orientations is null) return;
        if (orientations.Count != count) throw new ArgumentException("全入力の画像変換が必要です。");
        foreach (var value in orientations) ImageOrientation.Validate(value);
    }

    private static long ValidateCanvas(IReadOnlyList<Snapshot> images, IReadOnlyList<int> numbers,
        IReadOnlyList<ImageOrientation>? orientations = null, IReadOnlyList<ImageOffset>? offsets = null)
    {
        var positions = ImageOffset.Validate(offsets, images.Count);
        var width = 0; var height = 0;
        for (var i = 0; i < images.Count; i++)
        {
            var size = images[i].GetDimensions(numbers[i]); var swap = orientations?[i].SwapsDimensions == true;
            width = Math.Max(width, checked((swap ? size.Height : size.Width) + positions[i].X));
            height = Math.Max(height, checked((swap ? size.Width : size.Height) + positions[i].Y));
        }
        var pixels = (long)width * height;
        if (pixels > MaximumPixels) throw new InvalidOperationException("比較キャンバスが1600万ピクセルを超えます。");
        return pixels;
    }

    internal static void ValidateThreshold(double threshold)
    { if (!double.IsFinite(threshold) || threshold < 0) throw new ArgumentOutOfRangeException(nameof(threshold), "差分閾値は有限の非負数です。"); }

    internal static void ValidateInsertionDeletionMode(int mode)
    { if (mode is < 0 or > 2) throw new InvalidDataException("画像の挿入・削除モードは0（なし）、1（縦）、2（横）です。"); }

    internal static int ParseInsertionDeletionMode(string mode) => mode switch
    {
        "none" => 0, "vertical" => 1, "horizontal" => 2,
        _ => throw new ArgumentException("画像の挿入・削除モードはnone、vertical、horizontalです。")
    };

    private static void ValidateAlignmentWork(long maximumAlignmentWork)
    {
        if (maximumAlignmentWork < 0 || maximumAlignmentWork > ImageLineDiffer.MaximumWork)
            throw new ArgumentOutOfRangeException(nameof(maximumAlignmentWork));
    }

    internal static FrameComparison CompareDecoded(IReadOnlyList<DecodedFrame> frames, double threshold,
        bool includeDifferencePixels, CancellationToken token, IReadOnlyList<ImageOrientation>? orientations = null, int blockSize = 8,
        IReadOnlyList<ImageOffset>? offsets = null, int insertionDeletionMode = 0,
        long maximumAlignmentWork = ImageLineDiffer.MaximumWork, long maximumCanvasWork = MaximumDecodeWork)
    {
        ValidateThreshold(threshold);
        ValidateInsertionDeletionMode(insertionDeletionMode);
        ValidateAlignmentWork(maximumAlignmentWork);
        if (maximumCanvasWork < 0 || maximumCanvasWork > MaximumDecodeWork) throw new ArgumentOutOfRangeException(nameof(maximumCanvasWork));
        ValidateOrientations(orientations, frames.Count);
        var views = orientations is null ? frames : frames.Select((frame, index) => orientations[index].Apply(frame, token)).ToArray();
        var positions = ImageOffset.Validate(offsets, frames.Count);
        long work = 0;
        ImageLineAlignment.Result? alignment = null;
        if (insertionDeletionMode != 0)
        {
            alignment = ImageLineAlignment.Align(views, insertionDeletionMode == 2, threshold, token, maximumAlignmentWork);
            views = alignment.Frames;
            work = alignment.Work;
        }
        var canvas = ImageOffset.Canvas(views, positions);
        var canvasWork = checked((long)canvas.Width * canvas.Height * (frames.Count + 1));
        if (canvasWork > maximumCanvasWork) throw new InvalidOperationException("画像の描画作業量が256Mピクセルを超えます。");
        var regions = ImageRegionDiffer.Compare(views, blockSize, threshold, token, positions);
        var pixels = ComparePixels(views[0], views[^1], threshold, includeDifferencePixels, token, positions[0], positions[^1]);
        return new(views, regions, pixels, frames, work, alignment, canvasWork);
    }

    internal static FrameComparison DecodeSelection(IReadOnlyList<Snapshot> images, IReadOnlyList<int> numbers,
        double threshold, bool includeDifferencePixels, CancellationToken token, IReadOnlyList<ImageOrientation>? orientations = null, int blockSize = 8,
        IReadOnlyList<ImageOffset>? offsets = null, int insertionDeletionMode = 0,
        long maximumAlignmentWork = ImageLineDiffer.MaximumWork, long maximumCanvasWork = MaximumDecodeWork)
    {
        ValidateSelection(images, numbers, orientations, offsets); ValidateThreshold(threshold);
        ValidateInsertionDeletionMode(insertionDeletionMode); ValidateAlignmentWork(maximumAlignmentWork);
        var frames = new DecodedFrame[images.Count];
        for (var i = 0; i < images.Count; i++) frames[i] = images[i].Decode(numbers[i], token);
        return CompareDecoded(frames, threshold, includeDifferencePixels, token, orientations, blockSize, offsets,
            insertionDeletionMode, maximumAlignmentWork, maximumCanvasWork);
    }

    internal static ComparisonResult Compare(IReadOnlyList<Snapshot> images, IReadOnlyList<int>? numbers,
        double threshold, CancellationToken token, IReadOnlyList<ImageOrientation>? orientations = null, int blockSize = 8,
        IReadOnlyList<ImageOffset>? offsets = null, int insertionDeletionMode = 0,
        long maximumAlignmentWork = ImageLineDiffer.MaximumWork)
    {
        ValidateComparison(images, numbers, threshold, orientations, offsets);
        ValidateInsertionDeletionMode(insertionDeletionMode);
        ValidateAlignmentWork(maximumAlignmentWork);
        var selected = numbers is not null;
        token.ThrowIfCancellationRequested();
        var frames = new List<FrameResult>();
        var different = !selected && images.Select(image => image.FrameCount).Distinct().Count() != 1;
        var count = selected ? 1 : images.Max(image => image.FrameCount);
        long alignmentWork = 0;
        long canvasWork = 0;
        for (var index = 1; index <= count; index++)
        {
            token.ThrowIfCancellationRequested();
            var currentNumbers = numbers ?? images.Select(image => Math.Min(index, image.FrameCount)).ToArray();
            // 全ページで残予算を共有し、処理を開始する前に核へ渡す。
            var comparison = DecodeSelection(images, currentNumbers, threshold, false, token, orientations, blockSize, offsets,
                insertionDeletionMode, maximumAlignmentWork - alignmentWork, MaximumDecodeWork - canvasWork);
            alignmentWork += comparison.AlignmentWork;
            canvasWork += comparison.CanvasWork;
            var a = comparison.Frames[0]; var b = comparison.Frames[^1];
            var middle = images.Count == 3 ? comparison.Frames[1] : null;
            var rendered = ImageRegionRenderer.Render(comparison.Frames, comparison.Regions, blockSize: blockSize, token: token,
                alignment: comparison.Alignment);
            frames.Add(new(a.Number, b.Number, a.Width, a.Height, b.Width, b.Height,
                comparison.Pixels.DifferentPixels, comparison.Pixels.TotalPixels, PixelHash(a.Pixels, token), PixelHash(b.Pixels, token),
                middle?.Number, middle?.Width, middle?.Height, middle is null ? null : PixelHash(middle.Pixels, token),
                comparison.Regions.Regions, comparison.Regions.ConflictCount, rendered.Select(frame => PixelHash(frame.Pixels, token)).ToArray()));
            different |= comparison.Regions.Regions.Count != 0;
        }
        token.ThrowIfCancellationRequested();
        return new(different, images[0].FrameCount, images[^1].FrameCount, threshold, selected ? "selected" : "all", frames,
            images.Count == 3 ? images[1].FrameCount : null);
    }

    internal static PixelComparison ComparePixels(DecodedFrame? left, DecodedFrame? right, double threshold,
        bool includeDifferencePixels, CancellationToken token, ImageOffset leftOffset = default, ImageOffset rightOffset = default)
    {
        ValidateThreshold(threshold);
        var squaredThreshold = threshold * threshold;
        ImageOffset.Validate([leftOffset, rightOffset], 2);
        var width = Math.Max(checked((left?.Width ?? 0) + leftOffset.X), checked((right?.Width ?? 0) + rightOffset.X));
        var height = Math.Max(checked((left?.Height ?? 0) + leftOffset.Y), checked((right?.Height ?? 0) + rightOffset.Y));
        ValidateDimensions(width, height);
        var difference = includeDifferencePixels ? new byte[checked(width * height * 4)] : null;
        long changed = 0;
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                if ((x & 4095) == 0) token.ThrowIfCancellationRequested();
                var lx = x - leftOffset.X; var ly = y - leftOffset.Y;
                var rx = x - rightOffset.X; var ry = y - rightOffset.Y;
                var outside = left is null || right is null || lx < 0 || ly < 0 || rx < 0 || ry < 0
                    || lx >= left.Width || ly >= left.Height || rx >= right.Width || ry >= right.Height;
                var isDifferent = outside;
                if (!outside)
                {
                    var ia = (ly * left!.Width + lx) * 4;
                    var ib = (ry * right!.Width + rx) * 4;
                    isDifferent = ImageRegionDiffer.Different(left.Pixels, ia, right.Pixels, ib, squaredThreshold);
                }
                if (isDifferent) changed++;
                if (difference is not null)
                {
                    var offset = (y * width + x) * 4;
                    difference[offset] = difference[offset + 1] = isDifferent ? (byte)80 : (byte)28;
                    difference[offset + 2] = isDifferent ? (byte)255 : (byte)28;
                    difference[offset + 3] = 255;
                }
            }
        }
        token.ThrowIfCancellationRequested();
        return new(changed, (long)width * height, width, height, difference);
    }

    internal static string PixelHash(byte[] pixels, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < pixels.Length; offset += 65_536)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(pixels, offset, Math.Min(65_536, pixels.Length - offset));
        }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ValidateFrame(Snapshot image, int frame)
    {
        if (frame < 1 || frame > image.FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frame), $"画像フレームは1..{image.FrameCount}です。");
    }

    internal static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new InvalidDataException("画像の寸法が不正です。");
        if ((long)width * height > MaximumPixels) throw new InvalidOperationException("画像比較の上限は各画像1600万ピクセルです。");
    }

    private static void ValidateWork(long work)
    {
        if (work > MaximumDecodeWork) throw new InvalidOperationException("画像の復号作業量が256Mピクセルを超えます。");
    }
}
