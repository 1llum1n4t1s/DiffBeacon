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
        internal int Width { get; }
        internal int Height { get; }
        internal int FrameCount { get; }
        internal long Pixels => (long)Width * Height;

        internal Snapshot(byte[] bytes, int width, int height, int frameCount)
        { _bytes = bytes; Width = width; Height = height; FrameCount = frameCount; }

        internal DecodedFrame Decode(int frame, CancellationToken token)
        {
            ValidateFrame(this, frame);
            token.ThrowIfCancellationRequested();
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
        string? LeftPixelSha256, string? RightPixelSha256);
    internal sealed record ComparisonResult(bool Different, int LeftFrames, int RightFrames, int Threshold,
        string Mode, IReadOnlyList<FrameResult> Frames);

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
    {
        ValidateFrame(left, leftFrame); ValidateFrame(right, rightFrame);
        ValidateCanvas(left.Width, left.Height, right.Width, right.Height);
        ValidateWork(checked(left.Pixels * leftFrame + right.Pixels * rightFrame));
    }

    internal static void ValidateComparison(Snapshot left, Snapshot right, int? leftFrame, int? rightFrame,
        int threshold)
    {
        if (threshold is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(threshold), "差分閾値は0..255です。");
        if (leftFrame.HasValue != rightFrame.HasValue) throw new ArgumentException("左右両方のフレームを指定してください。");
        var selected = leftFrame.HasValue;
        ValidateCanvas(left.Width, left.Height, right.Width, right.Height);
        if (selected) ValidateSelection(left, right, leftFrame!.Value, rightFrame!.Value);
        else ValidateWork(checked(left.Pixels * left.FrameCount * (left.FrameCount + 1L) / 2
            + right.Pixels * right.FrameCount * (right.FrameCount + 1L) / 2));
    }

    internal static ComparisonResult Compare(Snapshot left, Snapshot right, int? leftFrame, int? rightFrame,
        int threshold, CancellationToken token)
    {
        ValidateComparison(left, right, leftFrame, rightFrame, threshold);
        var selected = leftFrame.HasValue;
        token.ThrowIfCancellationRequested();
        var frames = new List<FrameResult>();
        var different = !selected && left.FrameCount != right.FrameCount;
        var count = selected ? 1 : Math.Max(left.FrameCount, right.FrameCount);
        for (var index = 1; index <= count; index++)
        {
            token.ThrowIfCancellationRequested();
            var aNumber = selected ? leftFrame!.Value : index;
            var bNumber = selected ? rightFrame!.Value : index;
            var a = aNumber <= left.FrameCount ? left.Decode(aNumber, token) : null;
            var b = bNumber <= right.FrameCount ? right.Decode(bNumber, token) : null;
            var pixels = ComparePixels(a, b, threshold, false, token);
            var aHash = a is null ? null : PixelHash(a.Pixels, token);
            var bHash = b is null ? null : PixelHash(b.Pixels, token);
            frames.Add(new(a?.Number, b?.Number, a?.Width, a?.Height, b?.Width, b?.Height,
                pixels.DifferentPixels, pixels.TotalPixels, aHash, bHash));
            different |= a is null || b is null || pixels.DifferentPixels > 0;
        }
        token.ThrowIfCancellationRequested();
        return new(different, left.FrameCount, right.FrameCount, threshold, selected ? "selected" : "all", frames);
    }

    internal static PixelComparison ComparePixels(DecodedFrame? left, DecodedFrame? right, int threshold,
        bool includeDifferencePixels, CancellationToken token)
    {
        if (threshold is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(threshold));
        var width = Math.Max(left?.Width ?? 0, right?.Width ?? 0);
        var height = Math.Max(left?.Height ?? 0, right?.Height ?? 0);
        ValidateDimensions(width, height);
        var difference = includeDifferencePixels ? new byte[checked(width * height * 4)] : null;
        long changed = 0;
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                if ((x & 4095) == 0) token.ThrowIfCancellationRequested();
                var outside = left is null || right is null || x >= left.Width || y >= left.Height || x >= right.Width || y >= right.Height;
                var delta = 0;
                if (!outside)
                {
                    var ia = (y * left!.Width + x) * 4;
                    var ib = (y * right!.Width + x) * 4;
                    for (var channel = 0; channel < 4; channel++)
                        delta = Math.Max(delta, Math.Abs(left.Pixels[ia + channel] - right.Pixels[ib + channel]));
                }
                var isDifferent = outside || delta > threshold;
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

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new InvalidDataException("画像の寸法が不正です。");
        if ((long)width * height > MaximumPixels) throw new InvalidOperationException("画像比較の上限は各画像1600万ピクセルです。");
    }

    private static void ValidateCanvas(int leftWidth, int leftHeight, int rightWidth, int rightHeight)
    {
        if ((long)Math.Max(leftWidth, rightWidth) * Math.Max(leftHeight, rightHeight) > MaximumPixels)
            throw new InvalidOperationException("比較キャンバスが1600万ピクセルを超えます。");
    }

    private static void ValidateWork(long work)
    {
        if (work > MaximumDecodeWork) throw new InvalidOperationException("画像の復号作業量が256Mピクセルを超えます。");
    }
}
