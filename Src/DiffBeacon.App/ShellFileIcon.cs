using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DiffBeacon.Core;

namespace DiffBeacon.App;

/// <summary>
/// 文書タブの印。Windows はシェルの小さいアイコン、macOS は NSWorkspace のアイコン。
/// ファイルがまだ無いときは拡張子かフォルダー属性で種類の印を取る。
/// </summary>
internal static partial class ShellFileIcon
{
    /// <summary>比較の種類に合う 16px のアイコン。取れない環境では空の絵を返す。</summary>
    internal static Bitmap ForProject(ComparisonProject project)
    {
        var folder = string.Equals(project.Mode, "Folder", StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(project.LeftPath) || Directory.Exists(project.RightPath);
        var path = FirstPath(project);
        if (OperatingSystem.IsWindows())
        {
            var icon = WindowsIcon(string.IsNullOrWhiteSpace(path) ? (folder ? "folder" : "file.txt") : path, folder || Directory.Exists(path));
            if (icon is not null) return icon;
        }
        if (OperatingSystem.IsMacOS())
        {
            var icon = MacIcon(string.IsNullOrWhiteSpace(path) ? (folder ? "/System/Library" : "/bin/ls") : path);
            if (icon is not null) return icon;
        }
        return Placeholder(folder);
    }

    private static string FirstPath(ComparisonProject project)
    {
        foreach (var path in new[] { project.LeftPath, project.BasePath, project.RightPath })
            if (!string.IsNullOrWhiteSpace(path)) return path;
        return "";
    }

    private static Bitmap Placeholder(bool folder)
    {
        var bitmap = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        var color = folder ? 0xFF42CDFF : 0xFFFFFFFFu;
        var pixels = new uint[256];
        Array.Fill(pixels, color);
        var bytes = MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
        Marshal.Copy(bytes, 0, buffer.Address, bytes.Length);
        return bitmap;
    }

    private static Bitmap? WindowsIcon(string path, bool folder)
    {
        var info = new ShFileInfo();
        var flags = 0x100u | 0x1u | 0x10u;
        var attributes = folder ? 0x10u : 0x80u;
        var result = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
        if (result == 0 || info.hIcon == 0) return null;
        try { return FromIcon(info.hIcon); }
        finally { DestroyIcon(info.hIcon); }
    }

    private static Bitmap? FromIcon(nint icon)
    {
        if (!GetIconInfo(icon, out var info)) return null;
        try
        {
            var bits = new byte[16 * 16 * 4];
            var header = new BitmapInfoHeader { biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(), biWidth = 16, biHeight = -16, biPlanes = 1, biBitCount = 32 };
            var dc = GetDC(0);
            try
            {
                if (GetDIBits(dc, info.hbmColor, 0, 16, bits, ref header, 0) == 0) return null;
            }
            finally { ReleaseDC(0, dc); }
            var bitmap = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var buffer = bitmap.Lock();
            Marshal.Copy(bits, 0, buffer.Address, bits.Length);
            return bitmap;
        }
        finally
        {
            if (info.hbmColor != 0) DeleteObject(info.hbmColor);
            if (info.hbmMask != 0) DeleteObject(info.hbmMask);
        }
    }

    private static Bitmap? MacIcon(string path)
    {
        var pool = Mac.Send(Mac.Class("NSAutoreleasePool"), Mac.Sel("new"));
        try
        {
            var workspace = Mac.Send(Mac.Class("NSWorkspace"), Mac.Sel("sharedWorkspace"));
            var image = Mac.Send(workspace, Mac.Sel("iconForFile:"), Mac.Utf8(path));
            if (image == 0) return null;
            var data = Mac.Send(image, Mac.Sel("TIFFRepresentation"));
            if (data == 0) return null;
            var rep = Mac.Send(Mac.Class("NSBitmapImageRep"), Mac.Sel("imageRepWithData:"), data);
            if (rep == 0) return null;
            var png = Mac.Send(rep, Mac.Sel("representationUsingType:properties:"), 4, 0);
            if (png == 0) return null;
            var length = (int)Mac.Send(png, Mac.Sel("length"));
            var bytes = Mac.Send(png, Mac.Sel("bytes"));
            if (length <= 0 || bytes == 0) return null;
            var managed = new byte[length];
            Marshal.Copy(bytes, managed, 0, length);
            return new Bitmap(new MemoryStream(managed));
        }
        catch (DllNotFoundException) { return null; }
        finally { if (pool != 0) Mac.Send(pool, Mac.Sel("drain")); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader header, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ObjcClass(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ObjcSel(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nint ObjcSend(nint receiver, nint selector);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nint ObjcSend(nint receiver, nint selector, nint arg);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nint ObjcSend(nint receiver, nint selector, nint arg, nint arg2);

    private static class Mac
    {
        internal static nint Class(string name) => ObjcClass(name);
        internal static nint Sel(string name) => ObjcSel(name);
        internal static nint Send(nint receiver, nint selector) => ObjcSend(receiver, selector);
        internal static nint Send(nint receiver, nint selector, nint arg) => ObjcSend(receiver, selector, arg);
        internal static nint Send(nint receiver, nint selector, nint arg, nint arg2) => ObjcSend(receiver, selector, arg, arg2);
        internal static nint Utf8(string text)
        {
            var native = Marshal.StringToCoTaskMemUTF8(text);
            try { return Send(Class("NSString"), Sel("stringWithUTF8String:"), native); }
            finally { Marshal.FreeCoTaskMem(native); }
        }
    }
}
