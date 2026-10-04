using System.Text;
using Avalonia.Input;
using Avalonia.Input.Platform;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal enum BinaryClipboardKind { Raw, Ansi, Oem, Unicode, PlatformText }
internal sealed record BinaryClipboardValue(string Name, BinaryClipboardKind Kind, byte[] Bytes, string? Text = null)
{
    private int TextEnd()
    {
        if (Kind == BinaryClipboardKind.Unicode)
        {
            if (Bytes.Length % 2 != 0) throw new InvalidDataException("Unicode文字列の長さが不正です。");
            for (var index = 0; index < Bytes.Length; index += 2) if (Bytes[index] == 0 && Bytes[index + 1] == 0) return index;
        }
        else { var end = Array.IndexOf(Bytes, (byte)0); if (end >= 0) return end; }
        throw new InvalidDataException("クリップボード文字列に終端がありません。");
    }
    internal string PlainText()
    {
        if (Kind == BinaryClipboardKind.PlatformText) return Text ?? "";
        var end = TextEnd(); return Kind == BinaryClipboardKind.Unicode ? Encoding.Unicode.GetString(Bytes.AsSpan(0, end)) : BinaryTextEncoding.Decode(Bytes.AsSpan(0, end).ToArray(), Kind == BinaryClipboardKind.Oem);
    }
    internal byte[] Decode(bool asText, bool bigEndian, bool oem)
    {
        if (Kind == BinaryClipboardKind.Raw)
        { if (Bytes.Length > BinaryEditSession.MaximumFileBytes) throw new InvalidDataException("選択形式は16 MiBまでです。"); return Bytes.ToArray(); }
        if (Kind == BinaryClipboardKind.PlatformText)
        {
            var text = Text ?? "";
            // macOSの標準Unicode textは明示的なUTF-16LE文字bytes。bytecodeはANSI/OEM指定へ変換する。
            return asText ? Bounded(Encoding.Unicode.GetBytes(text)) : BinaryClipboardDialog.DecodeText(text, false, bigEndian, oem);
        }
        var end = TextEnd();
        if (asText) return Bounded(Bytes.AsSpan(0, end).ToArray());
        if (Kind == BinaryClipboardKind.Unicode)
            return BinaryClipboardDialog.DecodeText(Encoding.Unicode.GetString(Bytes, 0, end), false, bigEndian, oem);
        var literal = Bytes.AsSpan(0, end).ToArray();
        return BinaryTextEncoding.DecodeBytecode(literal, bigEndian, oem);
    }
    private static byte[] Bounded(byte[] bytes)
    { if (bytes.Length > BinaryEditSession.MaximumFileBytes) throw new InvalidDataException("貼り付けは16 MiBまでです。"); return bytes; }
}

internal interface IBinaryClipboard
{
    Task WriteAsync(byte[] bytes, string bytecode, CancellationToken token);
    Task<IReadOnlyList<BinaryClipboardValue>> ReadAsync(CancellationToken token);
}

// raw/header、ANSI/Unicode/OEMの終端と最大4形式のallocator余白を共有して検査する。
internal sealed class BinaryClipboardReadBudget
{
    internal const int AllocatorPadding = 64;
    // Windowsの合成CF_LOCALE等、小さい付随metadataにも1block分を残す。
    internal const long MaximumTotal = BinaryEditSession.MaximumFileBytes + BinaryBytecode.MaximumTextBytes * 4L + 8 + 4 + AllocatorPadding * 5;
    internal long Total { get; private set; }
    internal static long Maximum(BinaryClipboardKind kind, bool owned = false) => kind switch
    {
        BinaryClipboardKind.Unicode => BinaryBytecode.MaximumTextBytes * 2L + 2 + AllocatorPadding,
        BinaryClipboardKind.PlatformText => BinaryBytecode.MaximumTextBytes * 2L,
        BinaryClipboardKind.Ansi or BinaryClipboardKind.Oem => BinaryBytecode.MaximumTextBytes + 1L + AllocatorPadding,
        _ => BinaryEditSession.MaximumFileBytes + (owned ? 8L + AllocatorPadding : 0)
    };
    internal void Add(long size, BinaryClipboardKind kind, bool owned = false)
    {
        if (size < 0 || size > Maximum(kind, owned) || size > MaximumTotal - Total) throw new InvalidDataException("クリップボードの容量上限を超えています。");
        Total += size;
    }
}

internal sealed class BinaryClipboard(Func<IClipboard?> source, Func<IntPtr>? owner = null) : IBinaryClipboard
{
    internal static readonly DataFormat<byte[]> RawFormat = DataFormat.CreateBytesApplicationFormat("com.kagayoi.diffbeacon.binary.v1");
    internal static BinaryClipboard ForPlatform(Func<IImageClipboardPlatform?> source) => new(() => null) { platform = source };
    private Func<IImageClipboardPlatform?>? platform;
    internal static byte[] EncodeRaw(byte[] bytes)
    {
        if (bytes.Length > BinaryEditSession.MaximumFileBytes) throw new InvalidDataException("バイナリ形式は16 MiBまでです。");
        var encoded = new byte[bytes.Length + 8]; "DBBN"u8.CopyTo(encoded); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(4), bytes.Length); bytes.CopyTo(encoded, 8); return encoded;
    }
    internal static byte[] DecodeRaw(byte[] bytes)
    {
        if (bytes.Length < 8 || bytes.Length > BinaryClipboardReadBudget.Maximum(BinaryClipboardKind.Raw, true) || !bytes.AsSpan(0, 4).SequenceEqual("DBBN"u8)) throw new InvalidDataException("所有バイナリ形式のheaderが不正です。");
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        if (length < 0 || length > BinaryEditSession.MaximumFileBytes || length > bytes.Length - 8 || bytes.AsSpan(length + 8).ContainsAnyExcept((byte)0)) throw new InvalidDataException("所有バイナリ形式の宣言長が不正です。");
        return bytes.AsSpan(8, length).ToArray();
    }
    private IImageClipboardPlatform Target() => platform?.Invoke() ?? (source() is { } value ? new Platform(value) : throw new InvalidOperationException("クリップボードを使用できません。"));
    public async Task WriteAsync(byte[] bytes, string bytecode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > BinaryEditSession.MaximumFileBytes || bytecode.Length > BinaryBytecode.MaximumTextBytes) throw new InvalidDataException("クリップボードの容量上限を超えています。");
        if (platform is null && OperatingSystem.IsWindows() && owner?.Invoke() is { } handle && handle != IntPtr.Zero)
        { WindowsBinaryClipboard.Write(bytes, bytecode, handle, token); return; }
        var target = Target(); var item = new DataTransferItem(); item.Set(RawFormat, EncodeRaw(bytes)); item.SetText(bytecode);
        var transfer = new OwnedTransfer(item); var handed = false;
        try { await target.SetDataAsync(transfer); handed = true; token.ThrowIfCancellationRequested(); await target.FlushAsync(); token.ThrowIfCancellationRequested(); }
        catch { if (!handed) transfer.Dispose(); throw; }
    }
    public async Task<IReadOnlyList<BinaryClipboardValue>> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (platform is null && OperatingSystem.IsWindows() && owner?.Invoke() is { } handle && handle != IntPtr.Zero) return WindowsBinaryClipboard.Read(handle, token);
        using var transfer = await Target().TryGetDataAsync(); token.ThrowIfCancellationRequested();
        if (transfer is null) return [];
        var values = new List<BinaryClipboardValue>(); var budget = new BinaryClipboardReadBudget();
        foreach (var format in transfer.Formats.Take(128))
        {
            token.ThrowIfCancellationRequested();
            if (format is DataFormat<byte[]> binary && await transfer.TryGetValueAsync(binary) is { } bytes)
            {
                budget.Add(bytes.Length, BinaryClipboardKind.Raw, format == RawFormat);
                values.Add(new(format.Identifier ?? format.ToString(), BinaryClipboardKind.Raw, format == RawFormat ? DecodeRaw(bytes) : bytes.ToArray()));
            }
            else if (format is DataFormat<string> text && await transfer.TryGetValueAsync(text) is { } value)
            {
                budget.Add((long)value.Length * 2, BinaryClipboardKind.PlatformText);
                values.Add(new(format == DataFormat.Text ? "標準Unicode文字列" : format.Identifier ?? format.ToString(), BinaryClipboardKind.PlatformText, [], value));
            }
        }
        token.ThrowIfCancellationRequested(); return values;
    }
    private sealed class Platform(IClipboard value) : IImageClipboardPlatform
    {
        public Task SetDataAsync(IAsyncDataTransfer transfer) => value.SetDataAsync(transfer);
        public Task FlushAsync() => value.FlushAsync();
        public Task<IAsyncDataTransfer?> TryGetDataAsync() => value.TryGetDataAsync();
    }
    private sealed class OwnedTransfer(DataTransferItem item) : IDataTransfer, IAsyncDataTransfer
    {
        private DataTransferItem[] items = [item];
        public IReadOnlyList<DataFormat> Formats => items.Length == 0 ? [] : items[0].Formats;
        IReadOnlyList<IDataTransferItem> IDataTransfer.Items => items;
        IReadOnlyList<IAsyncDataTransferItem> IAsyncDataTransfer.Items => items;
        public void Dispose() => items = [];
    }
}

internal static class BinaryTextEncoding
{
    static BinaryTextEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    // Windowsは実ACP/OEMCPを使用。macOSの相当する選択値は固定1252/437として表示する。
    private static Encoding CharacterEncoding(bool oem) => Encoding.GetEncoding(OperatingSystem.IsWindows() ? (int)(oem ? WindowsBinaryClipboard.OemCodePage : WindowsBinaryClipboard.AnsiCodePage) : oem ? 437 : 1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    internal static byte[] Encode(string text, bool oem)
    {
        if (text.Length > BinaryBytecode.MaximumTextBytes) throw new InvalidDataException("貼り付けの文字数上限を超えています。");
        var encoding = CharacterEncoding(oem); if (encoding.GetByteCount(text) > BinaryBytecode.MaximumTextBytes) throw new InvalidDataException("貼り付けの文字数上限を超えています。");
        return encoding.GetBytes(text);
    }
    internal static byte[] DecodeBytecode(byte[] ansi, bool bigEndian, bool oem) => BinaryBytecode.Decode(ansi, bigEndian, oem ? OemLiteral : null);
    private static byte OemLiteral(byte value)
    {
        if (OperatingSystem.IsWindows()) return WindowsBinaryClipboard.ToOemLiteral(value);
        var bytes = CharacterEncoding(true).GetBytes(CharacterEncoding(false).GetString(new byte[] { value }));
        if (bytes.Length != 1) throw new FormatException("OEM変換は1バイト文字だけに対応します。"); return bytes[0];
    }
    internal static string Decode(byte[] bytes, bool oem) => CharacterEncoding(oem).GetString(bytes);
}
