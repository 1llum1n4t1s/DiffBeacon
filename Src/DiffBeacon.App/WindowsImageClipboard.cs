using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DiffBeacon.App;

// OSを変更しないheadless検証でも、公開順とHGLOBAL所有権の実経路を確認する。
internal interface IWindowsImageClipboardApi
{
    uint RegisterFormat(string name);
    IntPtr Allocate(byte[] bytes);
    void Free(IntPtr memory);
    bool Open(IntPtr owner);
    bool Empty();
    bool Set(uint format, IntPtr memory);
    bool Close();
}

internal static partial class WindowsImageClipboard
{
    internal const uint DibFormat = 8;
    internal static string RawFormatName => ImageClipboard.RawFormat.ToSystemName("avn-app-fmt:");

    internal static void Write(ImageComparisonEngine.DecodedFrame frame, IntPtr owner, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows clipboardが必要です。");
        Write(frame, owner, new NativeApi(), token);
    }

    internal static void Write(ImageComparisonEngine.DecodedFrame frame, IntPtr owner, IWindowsImageClipboardApi api, CancellationToken token = default)
    {
        if (owner == IntPtr.Zero) throw new ArgumentException("clipboard所有者のwindowが必要です。", nameof(owner));
        token.ThrowIfCancellationRequested();
        var dib = EncodeDib(frame, token); var raw = ImageClipboard.Encode(frame, token);
        var rawFormat = api.RegisterFormat(RawFormatName);
        if (rawFormat == 0) throw new InvalidOperationException("clipboard形式を登録できません。");
        IntPtr dibMemory = IntPtr.Zero, rawMemory = IntPtr.Zero; var opened = false;
        try
        {
            // 内容を消す前に両形式の確保・全画素コピーを終える。
            dibMemory = api.Allocate(dib);
            if (dibMemory == IntPtr.Zero) throw new InvalidOperationException("clipboard memoryを確保できません。");
            rawMemory = api.Allocate(raw);
            if (rawMemory == IntPtr.Zero) throw new InvalidOperationException("clipboard memoryを確保できません。");
            token.ThrowIfCancellationRequested();
            if (!api.Open(owner)) throw new InvalidOperationException("clipboardを開けません。");
            opened = true; token.ThrowIfCancellationRequested();
            if (!api.Empty()) throw new InvalidOperationException("clipboardを更新できません。");
            // Empty以降は両形式の公開を完了する。成功したHGLOBALだけOSへ所有権が移る。
            if (!api.Set(DibFormat, dibMemory)) throw new InvalidOperationException("clipboardへDIBを公開できません。");
            dibMemory = IntPtr.Zero;
            if (!api.Set(rawFormat, rawMemory)) throw new InvalidOperationException("clipboardへBGRAを公開できません。");
            rawMemory = IntPtr.Zero;
            if (!api.Close()) throw new InvalidOperationException("clipboardを閉じられません。");
            opened = false;
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            if (opened) api.Close();
            if (dibMemory != IntPtr.Zero) api.Free(dibMemory);
            if (rawMemory != IntPtr.Zero) api.Free(rawMemory);
        }
    }

    internal static byte[] EncodeDib(ImageComparisonEngine.DecodedFrame frame, CancellationToken token = default)
    {
        ImageClipboard.Validate(frame); token.ThrowIfCancellationRequested();
        var bytes = new byte[checked(40 + frame.Pixels.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), frame.Width);
        // 原本FreeImageが読む正の高さ・bottom-upの32bit BI_RGBを用いる。
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), frame.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), frame.Pixels.Length);
        for (var y = 0; y < frame.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            frame.Pixels.AsSpan(y * frame.Width * 4, frame.Width * 4).CopyTo(bytes.AsSpan(40 + (frame.Height - 1 - y) * frame.Width * 4));
        }
        return bytes;
    }

    private sealed class NativeApi : IWindowsImageClipboardApi
    {
        public uint RegisterFormat(string name) => RegisterClipboardFormat(name);
        public IntPtr Allocate(byte[] bytes)
        {
            var handle = GlobalAlloc(0x0042, (nuint)bytes.Length);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("clipboard memoryを確保できません。");
            var address = GlobalLock(handle);
            if (address == IntPtr.Zero) { GlobalFree(handle); throw new InvalidOperationException("clipboard memoryを固定できません。"); }
            try { Marshal.Copy(bytes, 0, address, bytes.Length); }
            catch { GlobalUnlock(handle); GlobalFree(handle); throw; }
            GlobalUnlock(handle); return handle;
        }
        public void Free(IntPtr memory) => GlobalFree(memory);
        public bool Open(IntPtr owner) => OpenClipboard(owner) != 0;
        public bool Empty() => EmptyClipboard() != 0;
        public bool Set(uint format, IntPtr memory) => SetClipboardData(format, memory) != IntPtr.Zero;
        public bool Close() => CloseClipboard() != 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial IntPtr GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial IntPtr GlobalLock(IntPtr handle);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int GlobalUnlock(IntPtr handle);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial IntPtr GlobalFree(IntPtr handle);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial int OpenClipboard(IntPtr owner);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial int EmptyClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] private static partial IntPtr SetClipboardData(uint format, IntPtr memory);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial int CloseClipboard();
    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint RegisterClipboardFormat(string name);
}
