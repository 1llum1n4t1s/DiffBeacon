using System.Runtime.InteropServices;
using DiffBeacon.Providers;
using SkiaSharp;

namespace DiffBeacon.App;

// 元画素のPNG別名保存。元format・palette・多ページcontainerの保存とは区別する。
internal static class ImagePngStore
{
    internal static string ValidateTarget(string output, IEnumerable<string> protectedPaths)
    {
        var target = ValidateLocal(output);
        if (!Path.GetExtension(target).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("画像編集の保存先はPNG（.png）です。");
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("画像の保存先フォルダーがありません。");
        if (Directory.Exists(target)) throw new IOException("保存先がディレクトリです。");
        foreach (var source in protectedPaths)
            if (ArchivePaths.SameFile(source, target)) throw new InvalidOperationException("画像の別名保存で入力画像・操作scriptを上書きできません。");
        if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException("読取り専用の画像出力を上書きできません。");
        return target;
    }

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
        // 操作状態を描画してから、ネイティブencoderをUIスレッドの外で実行する。
        await Task.Yield();
        var bytes = await Task.Run(() => EncodePng(frame, token), token);
        var savedSnapshot = new ImageComparisonEngine.Snapshot(bytes, frame.Width, frame.Height, 1);
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
}
