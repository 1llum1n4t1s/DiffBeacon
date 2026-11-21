using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace DiffBeacon.App;

/// <summary>
/// 本文フォントの選択。Windows は Win32 の ChooseFont、macOS は NSFontPanel。
/// どちらも出せない環境では呼び出し側が一覧から選ぶ。
/// </summary>
internal static partial class NativeFontChoice
{
    /// <summary>ネイティブのフォント選択が確定したとき true。キャンセルと未対応は false。</summary>
    internal static bool TryChoose(MainWindow window, string currentFamily, double currentSize, out string family, out double size)
    {
        family = currentFamily;
        size = currentSize;
        try
        {
            if (OperatingSystem.IsWindows()) return Windows(window, ref family, ref size);
            if (OperatingSystem.IsMacOS()) return Mac(ref family, ref size);
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        return false;
    }

    private static bool Windows(MainWindow window, ref string family, ref double size)
    {
        var logFont = new LogFont { lfFaceName = string.IsNullOrWhiteSpace(family) ? "Yu Gothic UI" : family, lfHeight = -(int)Math.Round(size) };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<LogFont>());
        try
        {
            Marshal.StructureToPtr(logFont, pointer, false);
            var choose = new ChooseFont
            {
                lStructSize = Marshal.SizeOf<ChooseFont>(),
                hwndOwner = window.TryGetPlatformHandle()?.Handle ?? 0,
                lpLogFont = pointer,
                Flags = 0x00000001 | 0x00000040 | 0x00000004,
            };
            if (!ChooseFontDialog(ref choose)) return false;
            logFont = Marshal.PtrToStructure<LogFont>(pointer);
            family = logFont.lfFaceName;
            if (choose.iPointSize > 0) size = choose.iPointSize / 10.0;
            return !string.IsNullOrWhiteSpace(family);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static bool Mac(ref string family, ref double size)
    {
        var panel = Obj.Send(Obj.Class("NSFontPanel"), Obj.Sel("sharedFontPanel"));
        if (panel == 0) return false;
        _ = Obj.Send(panel, Obj.Sel("orderFront:"), panel);
        var font = Obj.Send(Obj.Send(Obj.Class("NSFontManager"), Obj.Sel("sharedFontManager")), Obj.Sel("selectedFont"));
        if (font == 0) return false;
        var name = Obj.Send(font, Obj.Sel("familyName"));
        var bytes = name == 0 ? 0 : Obj.Send(name, Obj.Sel("UTF8String"));
        var chosen = bytes == 0 ? "" : Marshal.PtrToStringUTF8(bytes) ?? "";
        if (string.IsNullOrWhiteSpace(chosen)) return false;
        family = chosen;
        return true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct LogFont
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ChooseFont
    {
        public int lStructSize;
        public nint hwndOwner;
        public nint hDC;
        public nint lpLogFont;
        public int iPointSize;
        public uint Flags;
        public uint rgbColors;
        public nint lCustData;
        public nint lpfnHook;
        public nint lpTemplateName;
        public nint hInstance;
        public nint lpszStyle;
        public ushort nFontType;
        public ushort alignment;
        public int nSizeMin;
        public int nSizeMax;
    }

    [DllImport("comdlg32.dll", EntryPoint = "ChooseFontW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChooseFontDialog(ref ChooseFont choose);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint FontClass(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint FontSel(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nint FontSend(nint receiver, nint selector);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nint FontSend(nint receiver, nint selector, nint arg);

    private static class Obj
    {
        internal static nint Class(string name) => FontClass(name);
        internal static nint Sel(string name) => FontSel(name);
        internal static nint Send(nint receiver, nint selector) => FontSend(receiver, selector);
        internal static nint Send(nint receiver, nint selector, nint arg) => FontSend(receiver, selector, arg);
    }
}
