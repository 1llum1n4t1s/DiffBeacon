using System.Buffers;
using System.Buffers.Binary;
using TiffLibrary;
using TiffLibrary.PixelFormats;

namespace DiffBeacon.App;

// libraryがIFDの宣言量を確保する前に、主ページ列と全参照領域を検査する。
internal sealed class TiffImage
{
    private const int MaximumEntries = 65_536, MaximumSegments = 65_536;
    private const long MaximumScratchBytes = 128L * 1024 * 1024;
    private readonly byte[] _bytes;
    private readonly bool _little, _big;
    private readonly List<Page> _pages = [];
    internal IReadOnlyList<Page> Pages => _pages;
    internal sealed record Page(long Offset, int Width, int Height, long Work);
    private readonly record struct Field(ushort Type, ulong Count, int Offset);
    private TiffImage(byte[] bytes, bool little, bool big) { _bytes = bytes; _little = little; _big = big; }

    internal static TiffImage? TryOpen(byte[] bytes, CancellationToken token)
    {
        if (bytes.Length < 2 || !(bytes[0] == bytes[1] && bytes[0] is 73 or 77)) return null;
        var image = new TiffImage(bytes, bytes[0] == 73, false);
        image.Range(0, 8);
        var magic = image.U16(2);
        if (magic is not (42 or 43)) throw Invalid("ヘッダーが不正です。");
        if (magic == 43)
        {
            image.Range(0, 16);
            if (image.U16(4) != 8 || image.U16(6) != 0) throw Invalid("BigTIFFヘッダーが不正です。");
            image = new(bytes, bytes[0] == 73, true);
        }
        image.Parse(token);
        return image;
    }

    private void Parse(CancellationToken token)
    {
        ulong next = _big ? U64(8) : U32(4);
        var visited = new HashSet<ulong>();
        long entries = 0, metadata = 0;
        while (next != 0)
        {
            token.ThrowIfCancellationRequested();
            if (_pages.Count >= ImageComparisonEngine.MaximumFrames) throw Invalid("画像フレームの上限は各1024です。");
            if (!visited.Add(next) || (next & 1) != 0) throw Invalid("IFDの循環・配置が不正です。");
            var countSize = _big ? 8 : 2; var entrySize = _big ? 20 : 12; var inline = _big ? 8 : 4;
            var start = Range(next, (ulong)countSize);
            var count = _big ? U64(start) : U16(start);
            if (count == 0 || count > MaximumEntries || (entries += (long)count) > MaximumEntries)
                throw Invalid("IFDタグの共有上限は65536です。");
            Range(next, (ulong)countSize + count * (ulong)entrySize + (ulong)inline);
            var fields = new Dictionary<ushort, Field>();
            ushort prior = 0;
            for (var i = 0; i < (int)count; i++)
            {
                token.ThrowIfCancellationRequested();
                var at = start + countSize + i * entrySize;
                var tag = U16(at); var type = U16(at + 2);
                if (i != 0 && tag <= prior) throw Invalid("IFDタグが重複・逆順です。");
                prior = tag;
                var values = _big ? U64(at + 4) : U32(at + 4);
                var size = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 or 16 or 17 or 18 => 8, _ => 0 };
                if (size == 0 || values > (ulong)_bytes.Length / (ulong)size) throw Invalid("タグの型・宣言量が不正です。");
                var length = values * (ulong)size;
                metadata = checked(metadata + (long)length);
                if (metadata > ImageComparisonEngine.MaximumFileBytes) throw Invalid("TIFFの共有metadataが64 MiBを超えます。");
                var valueAt = at + (_big ? 12 : 8);
                var offset = length <= (ulong)inline ? valueAt : Range(_big ? U64(valueAt) : U32(valueAt), length);
                fields.Add(tag, new(type, values, offset));
            }
            ulong Scalar(ushort tag, ulong fallback = 0)
            {
                if (!fields.TryGetValue(tag, out var f)) return fallback;
                if (f.Count != 1) throw Invalid("単一値のタグが不正です。");
                return Value(f, 0);
            }
            ulong[] Array(ushort tag, bool required = true)
            {
                if (!fields.TryGetValue(tag, out var f))
                { if (required) throw Invalid("画像データの配列がありません。"); return []; }
                if (f.Count == 0 || f.Count > MaximumSegments) throw Invalid("画像segmentの上限は65536です。");
                var result = new ulong[(int)f.Count];
                for (var i = 0; i < result.Length; i++) { token.ThrowIfCancellationRequested(); result[i] = Value(f, i); }
                return result;
            }
            var width = Scalar(256); var height = Scalar(257);
            if (width > int.MaxValue || height > int.MaxValue) throw Invalid("画像寸法が不正です。");
            ImageComparisonEngine.ValidateDimensions((int)width, (int)height);
            var samples = Scalar(277, 1); var planar = Scalar(284, 1);
            var bits = Array(258, false);
            if (bits.Length == 0) bits = [1];
            if (samples == 0 || samples > 64 || bits.Length != (int)samples || bits.Any(b => b == 0 || b > 64) || planar is not (1 or 2))
                throw Invalid("画素sampleの宣言が不正です。");
            if (Scalar(259, 1) == 6)
            {
                if (samples > 4) throw Invalid("旧JPEGのsample数が不正です。");
                // 旧JPEGはstrip以外の別領域を優先して読むため、EOFの0進捗ループより前に検査する。
                var jpegOffset = Scalar(513); var jpegLength = Scalar(514);
                if (jpegOffset != 0 || jpegLength != 0)
                {
                    if (jpegOffset == 0) throw Invalid("旧JPEGのstream参照が不正です。");
                    var jpegAt = Range(jpegOffset, 1);
                    if (jpegLength == 0) jpegLength = (ulong)(_bytes.Length - jpegAt);
                    Range(jpegOffset, jpegLength);
                    ValidateJpeg(jpegAt, (int)jpegLength, (int)width, (int)height, token);
                }
                foreach (var tag in new ushort[] { 519, 520, 521 })
                {
                    var tables = Array(tag, false);
                    if (tables.Length > 4 || tables.Length != 0 && (ulong)tables.Length != samples)
                        throw Invalid("旧JPEGのtable数とsample数が一致しません。");
                    foreach (var table in tables)
                    {
                        token.ThrowIfCancellationRequested();
                        if (tag == 519) Range(table, 64);
                        else
                        {
                            var tableAt = Range(table, 16); var values = 0;
                            for (var i = 0; i < 16; i++) values += _bytes[tableAt + i];
                            if (values > 256) throw Invalid("旧JPEGのHuffman宣言が不正です。");
                            Range(table, (ulong)(16 + values));
                        }
                    }
                }
            }
            var tiled = fields.ContainsKey(324) || fields.ContainsKey(325);
            var offsets = Array((ushort)(tiled ? 324 : 273)); var lengths = Array((ushort)(tiled ? 325 : 279));
            var blockWidth = tiled ? Scalar(322) : width;
            var blockHeight = tiled ? Scalar(323) : Math.Min(Scalar(278, uint.MaxValue), height);
            if (blockWidth == 0 || blockHeight == 0 || blockWidth > int.MaxValue || blockHeight > int.MaxValue)
                throw Invalid("strip/tileの寸法が不正です。");
            var columns = (width + blockWidth - 1) / blockWidth;
            var rows = (height + blockHeight - 1) / blockHeight;
            var perPlane = columns * rows; var segments = perPlane * (planar == 2 ? samples : 1);
            if (segments > MaximumSegments || offsets.Length != lengths.Length || (ulong)offsets.Length != segments)
                throw Invalid("strip/tile配列と画像寸法が一致しません。");
            var expanded = checked((long)(columns * blockWidth) * (long)(rows * blockHeight));
            var bytesPerPixel = bits.Sum(b => (long)((b + 7) / 8));
            if ((long)blockWidth * (long)blockHeight > ImageComparisonEngine.MaximumPixels || expanded > ImageComparisonEngine.MaximumDecodeWork
                || expanded * bytesPerPixel > MaximumScratchBytes)
                throw Invalid("strip/tileの復号作業量が上限を超えます。");
            for (var i = 0; i < offsets.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (lengths[i] == 0) throw Invalid("空のstrip/tileです。");
                Range(offsets[i], lengths[i]);
                if (Scalar(259, 1) == 7)
                    ValidateJpeg(Range(offsets[i], lengths[i]), (int)lengths[i], (int)blockWidth, (int)blockHeight, token);
                // 非圧縮の全主ページを、選択されなくても完全なsample数まで検査する。
                if (Scalar(259, 1) == 1 && Scalar(262) != 6)
                {
                    var blockRows = tiled ? blockHeight : Math.Min(blockHeight, height - (ulong)i % perPlane * blockHeight);
                    var rowBits = planar == 2 ? blockWidth * bits[i / (int)perPlane] : blockWidth * (ulong)bits.Sum(b => (long)b);
                    if (lengths[i] < ((rowBits + 7) / 8) * blockRows) throw Invalid("非圧縮strip/tileが切り詰められています。");
                }
            }
            _pages.Add(new((long)next, (int)width, (int)height, expanded));
            var nextAt = start + countSize + (int)count * entrySize;
            next = _big ? U64(nextAt) : U32(nextAt);
        }
        if (_pages.Count == 0) throw Invalid("主ページがありません。");
    }

    private void ValidateJpeg(int start, int length, int width, int height, CancellationToken token)
    {
        var data = _bytes.AsSpan(start, length);
        if (length < 4 || data[0] != 255 || data[1] != 216) throw Invalid("JPEG strip/tileのヘッダーが不正です。");
        var foundFrame = false;
        for (var at = 2; at < data.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (data[at++] != 255) throw Invalid("JPEG markerが不正です。");
            while (at < data.Length && data[at] == 255) { if ((at & 65535) == 0) token.ThrowIfCancellationRequested(); at++; }
            if (at >= data.Length) throw Invalid("JPEG markerが不完全です。");
            var marker = data[at++];
            if (marker == 217) throw Invalid("JPEGのscanがありません。");
            if (marker == 218)
            {
                if (!foundFrame || data.Length - at < 3) throw Invalid("JPEGのscanヘッダーが不完全です。");
                var scanSize = BinaryPrimitives.ReadUInt16BigEndian(data[at..]); var channels = data[at + 2];
                if (channels is < 1 or > 4 || scanSize != 6 + channels * 2 || scanSize > data.Length - at)
                    throw Invalid("JPEGのscan宣言が不正です。");
                // 宣言だけで原画ゼロの成功にならないよう、少なくともentropyとEOIを要求する。
                var entropy = false;
                for (var i = at + scanSize; i < data.Length; i++)
                {
                    if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
                    if (data[i] != 255) { entropy = true; continue; }
                    if (++i >= data.Length) break;
                    if (data[i] == 0) entropy = true;
                    else if (data[i] == 217)
                    { if (!entropy) throw Invalid("JPEGの画素データがありません。"); return; }
                    else if (data[i] is >= 208 and <= 215) continue;
                    else if (data[i] == 255) { i--; continue; }
                    else
                    {
                        if (!entropy || data[i] == 216 || data[i] is >= 192 and <= 207 && data[i] is not (196 or 200 or 204))
                            throw Invalid("JPEGのscan途中のmarkerが不正です。");
                        if (data.Length - i - 1 < 2) throw Invalid("JPEGの後続markerが不完全です。");
                        var following = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 1)..]);
                        if (following < 2 || following > data.Length - i - 1) throw Invalid("JPEGの後続markerが範囲外です。");
                        if (data[i] == 218)
                        {
                            if (following < 3) throw Invalid("JPEGの後続scanが不完全です。");
                            var nextChannels = data[i + 3];
                            if (nextChannels is < 1 or > 4 || following != 6 + nextChannels * 2)
                                throw Invalid("JPEGの後続scan宣言が不正です。");
                            entropy = false;
                        }
                        i += following;
                    }
                }
                throw Invalid("JPEGの終了markerがありません。");
            }
            if (marker is 216 or 1 || marker is >= 208 and <= 215) continue;
            if (data.Length - at < 2) throw Invalid("JPEG headerが不完全です。");
            var size = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
            if (size < 2 || size > data.Length - at) throw Invalid("JPEG headerの参照範囲が不正です。");
            if (marker is >= 192 and <= 207 && marker is not (196 or 200 or 204))
            {
                if (size < 8) throw Invalid("JPEGフレーム宣言が不完全です。");
                var h = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 3)..]);
                var w = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 5)..]);
                var channels = data[at + 7];
                if (w == 0 || h == 0 || w > width || h > height || channels == 0 || channels > 4
                    || size != 8 + channels * 3 || data[at + 2] is not (8 or 12))
                    throw Invalid("JPEGの復号寸法・sampleがstrip/tileの範囲外です。");
                for (var channel = 0; channel < channels; channel++)
                {
                    var sampling = data[at + 9 + channel * 3];
                    if ((sampling >> 4) is < 1 or > 4 || (sampling & 15) is < 1 or > 4)
                        throw Invalid("JPEGのsampling宣言が不正です。");
                }
                foundFrame = true;
            }
            at += size;
        }
        throw Invalid("JPEGのscanがありません。");
    }

    internal ImageComparisonEngine.DecodedFrame Decode(int number, CancellationToken token)
        => DecodeAsync(number, token).GetAwaiter().GetResult();

    private async Task<ImageComparisonEngine.DecodedFrame> DecodeAsync(int number, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var page = _pages[number - 1];
        using var stream = new MemoryStream(_bytes, writable: false);
        using var reader = await TiffFileReader.OpenAsync(stream, leaveOpen: true, cancellationToken: token).ConfigureAwait(false);
        var ifd = await reader.ReadImageFileDirectoryAsync(new TiffStreamOffset(page.Offset), token).ConfigureAwait(false);
        using var pool = new BoundedPool();
        var decoder = await reader.CreateImageDecoderAsync(ifd, new TiffImageDecoderOptions
        { UndoColorPreMultiplying = true, IgnoreOrientation = true, MaxDegreeOfParallelism = 1, MemoryPool = pool }, token).ConfigureAwait(false);
        if (decoder.Width != page.Width || decoder.Height != page.Height) throw Invalid("復号寸法と検査済み寸法が一致しません。");
        var rgba = new TiffRgba32[checked(page.Width * page.Height)];
        await decoder.DecodeAsync(new TiffMemoryPixelBuffer<TiffRgba32>(rgba.AsMemory(), page.Width, page.Height, writable: true), token).ConfigureAwait(false);
        var pixels = new byte[checked(rgba.Length * 4)];
        for (var i = 0; i < rgba.Length; i++)
        {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            pixels[i * 4] = rgba[i].B; pixels[i * 4 + 1] = rgba[i].G; pixels[i * 4 + 2] = rgba[i].R; pixels[i * 4 + 3] = rgba[i].A;
        }
        token.ThrowIfCancellationRequested();
        return new(number, page.Width, page.Height, pixels);
    }

    private ulong Value(Field field, int index) => field.Type switch
    {
        1 => _bytes[field.Offset + index], 3 => U16(field.Offset + index * 2),
        4 or 13 => U32(field.Offset + index * 4), 16 or 18 => U64(field.Offset + index * 8),
        _ => throw Invalid("整数タグの型が不正です。")
    };
    private int Range(ulong offset, ulong length)
    {
        if (offset > (ulong)_bytes.Length || length > (ulong)_bytes.Length - offset) throw Invalid("参照領域がファイル外です。");
        return (int)offset;
    }
    private ushort U16(int at) => _little ? BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(at, 2)) : BinaryPrimitives.ReadUInt16BigEndian(_bytes.AsSpan(at, 2));
    private uint U32(int at) => _little ? BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(at, 4)) : BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(at, 4));
    private ulong U64(int at) => _little ? BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(at, 8)) : BinaryPrimitives.ReadUInt64BigEndian(_bytes.AsSpan(at, 8));
    private static InvalidDataException Invalid(string detail) => new("TIFF: " + detail);

    private sealed class BoundedPool : MemoryPool<byte>
    {
        private readonly object _gate = new(); private long _leased; private bool _disposed;
        public override int MaxBufferSize => (int)MaximumScratchBytes;
        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            var size = minBufferSize < 0 ? 4096 : Math.Max(1, minBufferSize);
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(BoundedPool));
                if (size > MaximumScratchBytes - _leased) throw Invalid("復号bufferの共有上限は128 MiBです。");
                _leased += size;
            }
            try { return new Owner(this, new byte[size]); }
            catch { lock (_gate) _leased -= size; throw; }
        }
        protected override void Dispose(bool disposing) { lock (_gate) _disposed = true; }
        private sealed class Owner(BoundedPool pool, byte[] bytes) : IMemoryOwner<byte>
        {
            private byte[]? _bytes = bytes;
            public Memory<byte> Memory => _bytes ?? throw new ObjectDisposedException(nameof(Owner));
            public void Dispose()
            {
                var released = Interlocked.Exchange(ref _bytes, null);
                if (released is not null) lock (pool._gate) pool._leased -= released.Length;
            }
        }
    }
}
