using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DiffBeacon.Providers;
using SkiaSharp;

namespace DiffBeacon.App;

// 元画素をPNGまたは単ページTIFFとして別名保存する。元format・palette・多ページcontainerの保存とは区別する。
internal static class ImagePngStore
{
    internal static string ValidateTarget(string output, IEnumerable<string> protectedPaths)
    {
        var target = ValidateLocal(output);
        var extension = Path.GetExtension(target);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("画像編集の保存先はPNG（.png）または単ページTIFF（.tif、.tiff）です。");
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("画像の保存先フォルダーがありません。");
        if (Directory.Exists(target)) throw new IOException("保存先がディレクトリです。");
        foreach (var source in protectedPaths)
            if (ArchivePaths.SameFile(source, target)) throw new InvalidOperationException("画像の別名保存で入力画像・操作scriptを上書きできません。");
        if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException("読取り専用の画像出力を上書きできません。");
        return target;
    }

    internal static bool IsTiffTarget(string path)
        => Path.GetExtension(path).Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".tiff", StringComparison.OrdinalIgnoreCase);

    internal static string ValidateLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            throw new ArgumentException("ローカルのファイルを指定してください。");
        var full = Path.GetFullPath(path);
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            FileSystemInfo item = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (item.LinkTarget is not null || item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("画像編集のパスにリンクを使用できません。");
        }
        return full;
    }

    internal static async Task<ImageComparisonEngine.Snapshot> SaveAsync(string output, ImageComparisonEngine.DecodedFrame frame,
        IEnumerable<string> protectedPaths, CancellationToken token = default,
        Func<IEnumerable<string>>? currentProtectedPaths = null, Action<string>? outputGuard = null)
    {
        var paths = protectedPaths.ToArray();
        var target = ValidateTarget(output, paths);
        if (frame.Width <= 0 || frame.Height <= 0 || (long)frame.Width * frame.Height > ImageComparisonEngine.MaximumPixels
            || frame.Pixels.LongLength != (long)frame.Width * frame.Height * 4) throw new ArgumentException("保存するBGRA画像が不正です。");
        token.ThrowIfCancellationRequested();
        // 操作状態を描画してから、encoderをUIスレッドの外で実行する。
        await Task.Yield();
        var tiff = IsTiffTarget(target);
        var bytes = await Task.Run(() => tiff ? EncodeTiff(frame, token) : EncodePng(frame, token), token);
        var savedSnapshot = tiff
            ? await ImageComparisonEngine.OpenBytesAsync(bytes, token)
            : new ImageComparisonEngine.Snapshot(bytes, frame.Width, frame.Height, 1);
        var exists = File.Exists(target);
        var attributes = exists ? File.GetAttributes(target) : FileAttributes.Normal;
        UnixFileMode? mode = !OperatingSystem.IsWindows() && exists ? File.GetUnixFileMode(target) : null;
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65_536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (mode.HasValue && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, mode.Value);
            if (OperatingSystem.IsWindows() && exists)
            {
                var preserved = attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed);
                File.SetAttributes(temporary, preserved == 0 ? FileAttributes.Normal : preserved);
            }
            ValidateTarget(target, paths.Concat(currentProtectedPaths?.Invoke() ?? []));
            outputGuard?.Invoke(target);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
            return savedSnapshot;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] EncodePng(ImageComparisonEngine.DecodedFrame frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pin = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888,
                SKAlphaType.Unpremul), pin.AddrOfPinnedObject(), checked(frame.Width * 4));
            using var png = pixmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("PNGを作成できません。");
            token.ThrowIfCancellationRequested();
            if (png.Size > ImageComparisonEngine.MaximumFileBytes) throw new InvalidOperationException("保存するPNGが64 MiBを超えます。");
            return png.ToArray();
        }
        finally { pin.Free(); }
    }

    private static byte[] EncodeTiff(ImageComparisonEngine.DecodedFrame frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        const int ifdOffset = 8;
        const int entryCount = 12;
        const int bitsOffset = ifdOffset + 2 + entryCount * 12 + 4;
        const int pixelOffset = bitsOffset + 8;
        var pixelByteCount = checked(frame.Width * frame.Height * 4);
        var totalLength = checked(pixelOffset + pixelByteCount);
        if (totalLength > ImageComparisonEngine.MaximumFileBytes)
            throw new InvalidOperationException("保存するTIFFが64 MiBを超えます。");

        var bytes = new byte[totalLength];
        bytes[0] = (byte)'I'; bytes[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), ifdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(ifdOffset, 2), entryCount);
        WriteIfdEntry(bytes, 0, 256, 4, 1, checked((uint)frame.Width));
        WriteIfdEntry(bytes, 1, 257, 4, 1, checked((uint)frame.Height));
        WriteIfdEntry(bytes, 2, 258, 3, 4, bitsOffset);
        WriteIfdEntry(bytes, 3, 259, 3, 1, 1);
        WriteIfdEntry(bytes, 4, 262, 3, 1, 2);
        WriteIfdEntry(bytes, 5, 273, 4, 1, pixelOffset);
        WriteIfdEntry(bytes, 6, 274, 3, 1, 1);
        WriteIfdEntry(bytes, 7, 277, 3, 1, 4);
        WriteIfdEntry(bytes, 8, 278, 4, 1, checked((uint)frame.Height));
        WriteIfdEntry(bytes, 9, 279, 4, 1, checked((uint)pixelByteCount));
        WriteIfdEntry(bytes, 10, 284, 3, 1, 1);
        WriteIfdEntry(bytes, 11, 338, 3, 1, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(ifdOffset + 2 + entryCount * 12, 4), 0);
        for (var channel = 0; channel < 4; channel++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bitsOffset + channel * 2, 2), 8);

        var rowBytes = checked(frame.Width * 4);
        for (var y = 0; y < frame.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            var rowStart = y * rowBytes;
            for (var x = 0; x < frame.Width; x++)
            {
                if ((x & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                var source = rowStart + x * 4;
                var target = pixelOffset + source;
                bytes[target] = frame.Pixels[source + 2];
                bytes[target + 1] = frame.Pixels[source + 1];
                bytes[target + 2] = frame.Pixels[source];
                bytes[target + 3] = frame.Pixels[source + 3];
            }
        }
        token.ThrowIfCancellationRequested();
        return bytes;
    }

    private static void WriteIfdEntry(byte[] bytes, int index, ushort tag, ushort type, uint count, uint value)
    {
        var offset = 10 + index * 12;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2, 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4, 4), count);
        if (type == 3 && count == 1)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 8, 2), checked((ushort)value));
        else
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8, 4), value);
    }
}
