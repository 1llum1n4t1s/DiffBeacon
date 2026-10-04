using System.Runtime.InteropServices;
using System.Text;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static partial class WindowsBinaryClipboard
{
    internal static uint AnsiCodePage => GetACP();
    internal static uint OemCodePage => GetOEMCP();
    internal static unsafe byte ToOemLiteral(byte value)
    {
        // 原関数と同じ1byte単位。APIに終端付きの有界sourceを渡し、token生成bytesには適用しない。
        byte* source = stackalloc byte[2]; source[0] = value; source[1] = 0; byte result = 0;
        if (CharToOemBuff(source, &result, 1) == 0) throw new InvalidDataException("OEM文字変換に失敗しました。元のバイトを保持しています。");
        return result;
    }
    private const uint Text = 1, OemText = 7, UnicodeText = 13, DisplayText = 0x81;
    internal static void Write(byte[] bytes, string bytecode, IntPtr owner, CancellationToken token)
    {
        var raw = RegisterClipboardFormat(BinaryClipboard.RawFormat.ToSystemName("avn-app-fmt:"));
        if (raw == 0) throw new IOException("バイナリ形式を登録できません。");
        var formats = new[] { raw, Text, UnicodeText };
        var data = new[] { BinaryClipboard.EncodeRaw(bytes), Encoding.ASCII.GetBytes(bytecode + '\0'), Encoding.Unicode.GetBytes(bytecode + '\0') };
        var handles = new IntPtr[3]; var opened = false;
        try
        {
            // Empty前に全HGLOBALを確保する。Set成功handleだけOSへ所有権を移す。
            for (var index = 0; index < handles.Length; index++)
            {
                token.ThrowIfCancellationRequested(); handles[index] = GlobalAlloc(0x42, (nuint)Math.Max(1, data[index].Length));
                if (handles[index] == IntPtr.Zero) throw new IOException("クリップボードのメモリを確保できません。");
                var pointer = GlobalLock(handles[index]); if (pointer == IntPtr.Zero) throw new IOException("クリップボードのメモリを開けません。");
                try { Marshal.Copy(data[index], 0, pointer, data[index].Length); } finally { GlobalUnlock(handles[index]); }
            }
            token.ThrowIfCancellationRequested(); if (!(opened = OpenClipboard(owner) != 0)) throw new IOException("クリップボードを開けません。");
            if (EmptyClipboard() == 0) throw new IOException("クリップボードを初期化できません。");
            for (var index = 0; index < handles.Length; index++)
            { if (SetClipboardData(formats[index], handles[index]) == IntPtr.Zero) throw new IOException("クリップボードへの公開に失敗しました。"); handles[index] = IntPtr.Zero; }
            if (CloseClipboard() == 0) throw new IOException("クリップボードの公開完了を確認できません。"); opened = false; token.ThrowIfCancellationRequested();
        }
        finally { if (opened) CloseClipboard(); foreach (var handle in handles) if (handle != IntPtr.Zero) GlobalFree(handle); }
    }
    internal static IReadOnlyList<BinaryClipboardValue> Read(IntPtr owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (OpenClipboard(owner) == 0) throw new IOException("クリップボードを開けません。");
        try
        {
            var values = new List<BinaryClipboardValue>(); uint format = 0; var budget = new BinaryClipboardReadBudget();
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                token.ThrowIfCancellationRequested(); if (values.Count == 128) throw new InvalidDataException("クリップボードの形式数上限を超えています。");
                var handle = GetClipboardData(format); if (handle == IntPtr.Zero) continue; var size = GlobalSize(handle);
                // bitmap/metafile等の非HGLOBALをbytesとして読まない。
                if (size == 0) continue;
                var kind = format is Text or DisplayText ? BinaryClipboardKind.Ansi : format == OemText ? BinaryClipboardKind.Oem : format == UnicodeText ? BinaryClipboardKind.Unicode : BinaryClipboardKind.Raw;
                var name = format == Text ? "CF_TEXT" : format == UnicodeText ? "CF_UNICODETEXT" : format == OemText ? "CF_OEMTEXT" : format == DisplayText ? "CF_DSPTEXT" : "形式 " + format;
                if (format >= 0xc000) { var label = new char[256]; var count = GetClipboardFormatName(format, label, label.Length); if (count > 0) name = new string(label, 0, count); }
                var owned = name == BinaryClipboard.RawFormat.ToSystemName("avn-app-fmt:");
                if (size > long.MaxValue) throw new InvalidDataException("クリップボードの容量上限を超えています。");
                budget.Add((long)size, kind, owned);
                var pointer = GlobalLock(handle); if (pointer == IntPtr.Zero) continue;
                var bytes = new byte[(int)size]; try { Marshal.Copy(pointer, bytes, 0, bytes.Length); } finally { GlobalUnlock(handle); }
                if (owned) bytes = BinaryClipboard.DecodeRaw(bytes);
                values.Add(new(name, kind, bytes));
            }
            return values;
        }
        finally { CloseClipboard(); }
    }
    [LibraryImport("kernel32.dll")] private static partial uint GetACP();
    [LibraryImport("kernel32.dll")] private static partial uint GetOEMCP();
    [LibraryImport("user32.dll", EntryPoint = "CharToOemBuffA")] private static unsafe partial int CharToOemBuff(byte* source, byte* destination, uint count);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial IntPtr GlobalAlloc(uint flags, nuint size);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial IntPtr GlobalLock(IntPtr memory);
    [LibraryImport("kernel32.dll")] private static partial int GlobalUnlock(IntPtr memory);
    [LibraryImport("kernel32.dll")] private static partial IntPtr GlobalFree(IntPtr memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nuint GlobalSize(IntPtr memory);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial int OpenClipboard(IntPtr owner);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial int EmptyClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] private static partial IntPtr SetClipboardData(uint format, IntPtr memory);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial IntPtr GetClipboardData(uint format);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial uint EnumClipboardFormats(uint format);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial int CloseClipboard();
    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)] private static partial uint RegisterClipboardFormat(string name);
    [LibraryImport("user32.dll", EntryPoint = "GetClipboardFormatNameW", StringMarshalling = StringMarshalling.Utf16)] private static partial int GetClipboardFormatName(uint format, [Out] char[] name, int capacity);
}
