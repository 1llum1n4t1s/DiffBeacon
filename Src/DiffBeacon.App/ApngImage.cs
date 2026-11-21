using System.Buffers.Binary;

namespace DiffBeacon.App;

// APNGの枠と合成だけを扱い、各フレームのPNG復号は既存Skia経路へ渡す。
internal sealed class ApngImage
{
    private const uint Ihdr = 0x49484452, Idat = 0x49444154, Iend = 0x49454e44;
    private const uint Actl = 0x6163544c, Fctl = 0x6663544c, Fdat = 0x66644154;
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = CreateCrcTable();
    private readonly byte[] _bytes;
    private readonly List<Segment> _shared = [];
    private readonly List<Frame> _frames = [];
    private byte[] _header = [];
    internal int Width { get; private set; }
    internal int Height { get; private set; }
    internal int FrameCount => _frames.Count;
    private readonly record struct Segment(int Offset, int Length);
    private sealed class Frame(int width, int height, int x, int y, byte dispose, byte blend, bool usesIdat)
    {
        internal readonly int Width = width, Height = height, X = x, Y = y;
        internal readonly byte Dispose = dispose, Blend = blend;
        internal readonly bool UsesIdat = usesIdat;
        internal readonly List<Segment> Data = [];
        internal long DataBytes => Data.Sum(item => (long)item.Length);
    }
    private ApngImage(byte[] bytes) => _bytes = bytes;

    internal static ApngImage? TryOpen(byte[] bytes, CancellationToken token)
    {
        if (!bytes.AsSpan().StartsWith(Signature)) return null;
        // 静止PNGの検証・復号を置換しない。APNG制御を認識した後は静止画へ退避しない。
        var animated = false;
        for (var offset = 8; bytes.Length - offset >= 8;)
        {
            token.ThrowIfCancellationRequested();
            var length = U32(bytes.AsSpan(offset, 4));
            var type = U32(bytes.AsSpan(offset + 4, 4));
            if (type is Actl or Fctl or Fdat) { animated = true; break; }
            if (length > bytes.Length - offset - 12) break;
            offset += (int)length + 12;
        }
        if (!animated) return null;
        var image = new ApngImage(bytes);
        image.Parse(token);
        return image;
    }

    private void Parse(CancellationToken token)
    {
        var position = 8; uint declared = 0, sequence = 0;
        var idatSeen = false; var idatEnded = false; var ended = false; long defaultBytes = 0;
        Frame? current = null;
        while (position < _bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            if (_bytes.Length - position < 12) throw Invalid("チャンクが途中で終了しています。");
            var length = U32(_bytes.AsSpan(position, 4));
            if (length > _bytes.Length - position - 12) throw Invalid("チャンク長が不正です。");
            var size = (int)length; var type = U32(_bytes.AsSpan(position + 4, 4));
            var data = _bytes.AsSpan(position + 8, size);
            if (Crc(_bytes.AsSpan(position + 4, size + 4), token) != U32(_bytes.AsSpan(position + 8 + size, 4)))
                throw Invalid("チャンクCRCが一致しません。");
            if (position == 8 && type != Ihdr) throw Invalid("先頭にIHDRがありません。");
            if (idatSeen && type != Idat) idatEnded = true;
            switch (type)
            {
                case Ihdr:
                    if (position != 8 || size != 13) throw Invalid("IHDRが不正です。");
                    var w = U32(data); var h = U32(data[4..]);
                    if (w > int.MaxValue || h > int.MaxValue) throw Invalid("画像寸法が不正です。");
                    Width = (int)w; Height = (int)h;
                    ImageComparisonEngine.ValidateDimensions(Width, Height);
                    _header = data.ToArray();
                    break;
                case Actl:
                    if (declared != 0 || idatSeen || size != 8) throw Invalid("acTLの順序・長さが不正です。");
                    declared = U32(data);
                    if (declared == 0) throw Invalid("フレーム数は1以上です。");
                    if (declared > ImageComparisonEngine.MaximumFrames) throw new InvalidOperationException("画像フレームの上限は各1024です。");
                    if (_frames.Count > declared) throw Invalid("宣言されたフレーム数を超えています。");
                    break;
                case Fctl:
                    // acTLと先頭fcTLの相対順序は固定されていない。どちらもIDATより前に必要。
                    if (size != 26 || U32(data) != sequence++) throw Invalid("fcTLの順序・番号・長さが不正です。");
                    if (current is not null && current.DataBytes == 0) throw Invalid("フレームデータがありません。");
                    if (_frames.Count >= ImageComparisonEngine.MaximumFrames || declared != 0 && _frames.Count >= declared)
                        throw Invalid("宣言されたフレーム数を超えています。");
                    var fw = U32(data[4..]); var fh = U32(data[8..]); var x = U32(data[12..]); var y = U32(data[16..]);
                    if (fw == 0 || fh == 0 || (long)x + fw > Width || (long)y + fh > Height || data[24] > 2 || data[25] > 1)
                        throw Invalid("フレーム矩形・合成方法が不正です。");
                    if (!idatSeen && (fw != Width || fh != Height || x != 0 || y != 0)) throw Invalid("既定画像のフレーム矩形が一致しません。");
                    current = new((int)fw, (int)fh, (int)x, (int)y, data[24], data[25], !idatSeen);
                    _frames.Add(current);
                    break;
                case Idat:
                    if (declared == 0 || idatEnded || current is { UsesIdat: false }) throw Invalid("IDATの順序が不正です。");
                    idatSeen = true; defaultBytes += size;
                    current?.Data.Add(new(position + 8, size));
                    break;
                case Fdat:
                    if (!idatSeen || current is null || current.UsesIdat || size < 4 || U32(data) != sequence++)
                        throw Invalid("fdATの順序・番号・長さが不正です。");
                    current.Data.Add(new(position + 12, size - 4));
                    break;
                case Iend:
                    if (size != 0 || position + 12 != _bytes.Length) throw Invalid("IEND・末尾データが不正です。");
                    ended = true;
                    break;
                default:
                    if ((type & 0x20000000) == 0 && (type != 0x504c5445 || idatSeen))
                        throw Invalid("未知または順序が不正な必須チャンクです。");
                    // 色形式・palette・透明度・色プロファイルなどは各PNGフレームへ継承する。
                    if (!idatSeen) _shared.Add(new(position, size + 12));
                    break;
            }
            position += size + 12;
        }
        if (!ended || !idatSeen || defaultBytes == 0 || _frames.Count != declared || current is null || current.DataBytes == 0)
            throw Invalid("アニメーションの枚数・データが不完全です。");
    }

    internal ImageComparisonEngine.DecodedFrame Decode(int number, CancellationToken token)
    {
        if (number < 1 || number > FrameCount) throw new ArgumentOutOfRangeException(nameof(number));
        if ((long)Width * Height * number > ImageComparisonEngine.MaximumDecodeWork)
            throw new InvalidOperationException("画像の復号作業量が256Mピクセルを超えます。");
        token.ThrowIfCancellationRequested();
        var canvas = new byte[checked(Width * Height * 4)];
        for (var index = 0; index < number; index++)
        {
            token.ThrowIfCancellationRequested();
            var frame = _frames[index];
            var png = BuildPng(frame, token);
            var decoded = new ImageComparisonEngine.Snapshot(png, frame.Width, frame.Height, 1).Decode(1, token);
            byte[]? previous = frame.Dispose == 2 && index != 0 && index + 1 < number ? new byte[decoded.Pixels.Length] : null;
            for (var row = 0; row < frame.Height; row++)
            {
                token.ThrowIfCancellationRequested();
                var target = canvas.AsSpan(checked(((frame.Y + row) * Width + frame.X) * 4), frame.Width * 4);
                var source = decoded.Pixels.AsSpan(row * frame.Width * 4, frame.Width * 4);
                if (previous is not null) target.CopyTo(previous.AsSpan(row * frame.Width * 4));
                if (frame.Blend == 0) source.CopyTo(target);
                else for (var column = 0; column < source.Length; column += 4)
                {
                    if ((column & 65535) == 0) token.ThrowIfCancellationRequested();
                    Blend(source.Slice(column, 4), target.Slice(column, 4));
                }
            }
            if (index + 1 == number) break;
            for (var row = 0; row < frame.Height && frame.Dispose != 0; row++)
            {
                token.ThrowIfCancellationRequested();
                var target = canvas.AsSpan(checked(((frame.Y + row) * Width + frame.X) * 4), frame.Width * 4);
                if (previous is null) target.Clear();
                else previous.AsSpan(row * frame.Width * 4, frame.Width * 4).CopyTo(target);
            }
        }
        return new(number, Width, Height, canvas);
    }

    private byte[] BuildPng(Frame frame, CancellationToken token)
    {
        using var png = new MemoryStream(); png.Write(Signature);
        var header = (byte[])_header.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)frame.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)frame.Height);
        WriteChunk(png, Ihdr, header, token);
        foreach (var segment in _shared) { token.ThrowIfCancellationRequested(); png.Write(_bytes.AsSpan(segment.Offset, segment.Length)); }
        foreach (var segment in frame.Data) { token.ThrowIfCancellationRequested(); WriteChunk(png, Idat, _bytes.AsSpan(segment.Offset, segment.Length), token); }
        WriteChunk(png, Iend, [], token); token.ThrowIfCancellationRequested();
        return png.ToArray();
    }

    private static void Blend(ReadOnlySpan<byte> source, Span<byte> target)
    {
        var alpha = source[3];
        if (alpha == 0) return;
        if (alpha == 255 || target[3] == 0) { source.CopyTo(target); return; }
        var remaining = target[3] * (255 - alpha); var numerator = alpha * 255 + remaining;
        for (var channel = 0; channel < 3; channel++)
            target[channel] = (byte)((source[channel] * alpha * 255 + target[channel] * remaining + numerator / 2) / numerator);
        target[3] = (byte)((numerator + 127) / 255);
    }

    private static void WriteChunk(Stream stream, uint type, ReadOnlySpan<byte> data, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length); BinaryPrimitives.WriteUInt32BigEndian(header[4..], type);
        stream.Write(header); stream.Write(data);
        var crc = Crc(header[4..], token, uint.MaxValue, false);
        crc = Crc(data, token, crc, true);
        BinaryPrimitives.WriteUInt32BigEndian(header, crc); stream.Write(header[..4]);
    }
    private static uint U32(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32BigEndian(data);
    private static InvalidDataException Invalid(string detail) => new("APNG: " + detail);
    private static uint Crc(ReadOnlySpan<byte> data, CancellationToken token, uint crc = uint.MaxValue, bool finish = true)
    {
        for (var index = 0; index < data.Length; index++)
        {
            if ((index & 65535) == 0) token.ThrowIfCancellationRequested();
            crc = CrcTable[(crc ^ data[index]) & 255] ^ (crc >> 8);
        }
        return finish ? ~crc : crc;
    }
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }
}
