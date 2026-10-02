using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DiffBeacon.App;

internal interface IImageClipboard
{
    Task WriteAsync(ImageComparisonEngine.DecodedFrame frame, CancellationToken token);
    Task<ImageComparisonEngine.DecodedFrame?> ReadAsync(CancellationToken token);
}

// AvaloniaのIClipboardはuser codeから実装できないため、同じ所有権契約を注入可能な境界へ置く。
internal interface IImageClipboardPlatform
{
    Task SetDataAsync(IAsyncDataTransfer transfer);
    Task FlushAsync();
    Task<IAsyncDataTransfer?> TryGetDataAsync();
}

// 標準bitmapは他アプリ向け。同アプリ間は透明RGBを失わない所有BGRA形式を優先する。
internal sealed class ImageClipboard : IImageClipboard
{
    private readonly Func<IImageClipboardPlatform?> clipboard;
    private readonly Func<IntPtr>? nativeOwner;
    internal ImageClipboard(Func<IClipboard?> source, Func<IntPtr>? nativeOwner = null)
    { clipboard = () => source() is { } value ? new Platform(value) : null; this.nativeOwner = nativeOwner; }
    private ImageClipboard(Func<IImageClipboardPlatform?> source) => clipboard = source;
    internal static ImageClipboard ForPlatform(Func<IImageClipboardPlatform?> source) => new(source);
    internal static readonly DataFormat<byte[]> RawFormat = DataFormat.CreateBytesApplicationFormat("com.kagayoi.diffbeacon.bgra.v1");
    internal static readonly DataFormat<byte[]> DibFormat = DataFormat.CreateBytesPlatformFormat("CF_DIB");

    public async Task WriteAsync(ImageComparisonEngine.DecodedFrame frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows() && nativeOwner?.Invoke() is { } owner && owner != IntPtr.Zero)
        {
            WindowsImageClipboard.Write(frame, owner, token);
            return;
        }
        var target = clipboard() ?? throw new InvalidOperationException("クリップボードを使用できません。");
        var transfer = CreateTransfer(frame, token);
        var transferred = false;
        try
        {
            // 成功後のtransferとbitmapはOS側が不要になるまで所有する。callerでdisposeしない。
            await target.SetDataAsync(transfer);
            transferred = true;
            // Windowsでは終了後にも標準bitmap/custom bytesを残す。flush失敗時もCutは削除しない。
            token.ThrowIfCancellationRequested();
            await target.FlushAsync();
        }
        catch
        {
            // SetData成功後はOS所有。flush失敗でOSがまだ参照するbitmapを破棄しない。
            if (!transferred) transfer.Dispose();
            throw;
        }
        token.ThrowIfCancellationRequested();
    }

    public async Task<ImageComparisonEngine.DecodedFrame?> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = clipboard() ?? throw new InvalidOperationException("クリップボードを使用できません。");
        // 取得transferは呼出し側がdisposeし、借用bitmapをその有効期間内に所有BGRAへ変換する。
        using var transfer = await target.TryGetDataAsync();
        token.ThrowIfCancellationRequested();
        if (transfer is null) return null;
        var raw = await transfer.TryGetValueAsync(RawFormat);
        token.ThrowIfCancellationRequested();
        if (raw is not null) return Decode(raw, token);
        if (OperatingSystem.IsWindows())
        {
            // AvaloniaのGDI bitmap経路はCF_DIBのalphaをopaqueへ変換するため、32bit BI_RGBは原画を直接読む。
            var dib = await transfer.TryGetValueAsync(DibFormat);
            token.ThrowIfCancellationRequested();
            if (dib is not null && DecodeDib(dib, token) is { } frame) return frame;
        }
        var bitmap = await transfer.TryGetBitmapAsync();
        token.ThrowIfCancellationRequested();
        return bitmap is null ? null : DecodeBitmap(bitmap, token);
    }

    internal static IAsyncDataTransfer CreateTransfer(ImageComparisonEngine.DecodedFrame frame, CancellationToken token = default)
    {
        var bytes = Encode(frame, token);
        var bitmap = new WriteableBitmap(new(frame.Width, frame.Height), new(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        try
        {
            using (var locked = bitmap.Lock())
                for (var y = 0; y < frame.Height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    Marshal.Copy(frame.Pixels, y * frame.Width * 4, locked.Address + y * locked.RowBytes, frame.Width * 4);
                }
            var item = new DataTransferItem(); item.Set(RawFormat, bytes); item.SetBitmap(bitmap);
            return new OwnedTransfer(item, bitmap);
        }
        catch { bitmap.Dispose(); throw; }
    }

    internal static byte[] Encode(ImageComparisonEngine.DecodedFrame frame, CancellationToken token = default)
    {
        Validate(frame); token.ThrowIfCancellationRequested();
        var bytes = new byte[checked(frame.Pixels.Length + 16)];
        "DBBG"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), frame.Width); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), frame.Height);
        for (var at = 0; at < frame.Pixels.Length; at += 65_536)
        {
            token.ThrowIfCancellationRequested();
            frame.Pixels.AsSpan(at, Math.Min(65_536, frame.Pixels.Length - at)).CopyTo(bytes.AsSpan(at + 16));
        }
        return bytes;
    }

    internal static ImageComparisonEngine.DecodedFrame Decode(byte[] bytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length < 16 || !bytes.AsSpan(0, 4).SequenceEqual("DBBG"u8) || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) != 1)
            throw new InvalidDataException("クリップボードのBGRA形式が不正です。");
        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)); var height = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        ImageComparisonEngine.ValidateDimensions(width, height);
        if (bytes.LongLength != 16 + (long)width * height * 4) throw new InvalidDataException("クリップボードの寸法と画素数が一致しません。");
        var pixels = new byte[bytes.Length - 16];
        for (var at = 0; at < pixels.Length; at += 65_536)
        {
            token.ThrowIfCancellationRequested();
            bytes.AsSpan(at + 16, Math.Min(65_536, pixels.Length - at)).CopyTo(pixels.AsSpan(at));
        }
        return new(1, width, height, pixels);
    }

    internal static ImageComparisonEngine.DecodedFrame DecodeBitmap(Bitmap bitmap, CancellationToken token = default)
    {
        var size = bitmap.PixelSize; ImageComparisonEngine.ValidateDimensions(size.Width, size.Height); token.ThrowIfCancellationRequested();
        using var target = new WriteableBitmap(size, new(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        var pixels = new byte[checked(size.Width * size.Height * 4)];
        using (var locked = target.Lock())
        {
            bitmap.CopyPixels(locked);
            for (var y = 0; y < size.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                Marshal.Copy(locked.Address + y * locked.RowBytes, pixels, y * size.Width * 4, size.Width * 4);
            }
        }
        token.ThrowIfCancellationRequested();
        return new(1, size.Width, size.Height, pixels);
    }

    internal static ImageComparisonEngine.DecodedFrame? DecodeDib(byte[] dib, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (dib.Length < 40 || dib.LongLength > ImageComparisonEngine.MaximumFileBytes)
            throw new InvalidDataException("クリップボードDIBの長さが不正です。");
        // その他のDIB形式は通常bitmap経路へ任せる。ここで未検証のmask/paletteをBGRAとみなさない。
        if (BinaryPrimitives.ReadInt32LittleEndian(dib) != 40 || BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14)) != 32
            || BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16)) != 0) return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(12)) != 1 || BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32)) != 0)
            throw new InvalidDataException("クリップボードDIBのplanes/paletteが不正です。");
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4)); var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8));
        var height = Math.Abs((long)signedHeight);
        if (height > int.MaxValue) throw new InvalidDataException("クリップボードDIBの高さが不正です。");
        ImageComparisonEngine.ValidateDimensions(width, (int)height);
        var pixelBytes = checked((long)width * height * 4);
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(20));
        if (pixelBytes > dib.LongLength - 40 || declared != 0 && declared != pixelBytes)
            throw new InvalidDataException("クリップボードDIBの宣言量と画素数が一致しません。");
        var pixels = new byte[checked((int)pixelBytes)];
        for (var y = 0; y < (int)height; y++)
        {
            token.ThrowIfCancellationRequested();
            var sourceY = signedHeight > 0 ? (int)height - 1 - y : y;
            dib.AsSpan(checked(40 + sourceY * width * 4), width * 4).CopyTo(pixels.AsSpan(y * width * 4));
        }
        return new(1, width, (int)height, pixels);
    }

    internal static void Validate(ImageComparisonEngine.DecodedFrame frame)
    {
        ImageComparisonEngine.ValidateDimensions(frame.Width, frame.Height);
        if (frame.Pixels.LongLength != (long)frame.Width * frame.Height * 4)
            throw new InvalidDataException("クリップボードの寸法と画素数が一致しません。");
    }

    private sealed class OwnedTransfer(DataTransferItem item, Bitmap bitmap) : IDataTransfer, IAsyncDataTransfer
    {
        private Bitmap? _bitmap = bitmap;
        private DataTransferItem[] _items = [item];
        public IReadOnlyList<DataFormat> Formats => _items.Length == 0 ? [] : _items[0].Formats;
        IReadOnlyList<IDataTransferItem> IDataTransfer.Items => _items;
        IReadOnlyList<IAsyncDataTransferItem> IAsyncDataTransfer.Items => _items;
        public void Dispose() { _bitmap?.Dispose(); _bitmap = null; _items = []; }
    }

    private sealed class Platform(IClipboard value) : IImageClipboardPlatform
    {
        public Task SetDataAsync(IAsyncDataTransfer transfer) => value.SetDataAsync(transfer);
        public Task FlushAsync() => value.FlushAsync();
        public Task<IAsyncDataTransfer?> TryGetDataAsync() => value.TryGetDataAsync();
    }
}
