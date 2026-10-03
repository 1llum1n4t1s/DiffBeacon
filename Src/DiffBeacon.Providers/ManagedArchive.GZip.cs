using System.Buffers.Binary;
using SharpDeflateStream = SharpCompress.Compressors.Deflate.DeflateStream;

namespace DiffBeacon.Providers;

public sealed partial class ManagedArchive
{
    // BCL GZipStreamは欠損footerでもEOFを返すため、メンバー境界を明示検証する。
    // managed DEFLATEの消費量を使い、先読みされたfooter/次メンバーを正確に巻き戻す。
    private sealed class VerifiedGZipStream(Stream input, CancellationToken token, int maximumMembers, ArchiveReadBudget? budget = null) : Stream
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private SharpDeflateStream? _deflate;
        private long _deflateStart;
        private uint _crc;
        private long _size;
        private int _members;
        private bool _end;

        private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes) crc = DecodedSink.CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
            return crc;
        }

        private bool StartMember()
        {
            token.ThrowIfCancellationRequested();
            if (input.Position == input.Length) return false;
            if (++_members > maximumMembers) throw new InvalidDataException("gzipメンバー数の上限を超えました。");
            budget?.Item();
            uint headerCrc = uint.MaxValue;
            var headerBytes = 0;
            byte HeaderByte()
            {
                token.ThrowIfCancellationRequested();
                var value = input.ReadByte();
                if (value < 0 || ++headerBytes > 1024 * 1024)
                    throw new InvalidDataException("gzipヘッダーが欠損または上限を超えています。");
                var octet = (byte)value;
                budget?.Work(1);
                headerCrc = DecodedSink.CrcTable[(headerCrc ^ octet) & 255] ^ (headerCrc >> 8);
                return octet;
            }
            if (HeaderByte() != 0x1f || HeaderByte() != 0x8b || HeaderByte() != 8)
                throw new InvalidDataException("gzipメンバーのヘッダーが不正です。");
            var flags = HeaderByte();
            if ((flags & 0xe0) != 0) throw new InvalidDataException("gzip予約フラグが不正です。");
            for (var index = 0; index < 6; index++) HeaderByte();
            if ((flags & 4) != 0)
            {
                var length = HeaderByte() | HeaderByte() << 8;
                for (var index = 0; index < length; index++) HeaderByte();
            }
            if ((flags & 8) != 0) while (HeaderByte() != 0) { budget?.PathCharacters(1); }
            if ((flags & 16) != 0) while (HeaderByte() != 0) { budget?.PathCharacters(1); }
            if ((flags & 2) != 0)
            {
                Span<byte> expected = stackalloc byte[2];
                ReadRequired(expected);
                if (BinaryPrimitives.ReadUInt16LittleEndian(expected) != unchecked((ushort)~headerCrc))
                    throw new InvalidDataException("gzipヘッダーCRCが一致しません。");
            }
            _deflateStart = input.Position;
            // wrapperのみ閉じ、ライブラリの内部bufferを解放しつつ元入力を維持する。
            _deflate = new SharpDeflateStream(new CheckedStream(input, input.Length, token),
                SharpCompress.Compressors.CompressionMode.Decompress);
            _crc = uint.MaxValue;
            _size = 0;
            return true;
        }

        private void ReadRequired(Span<byte> bytes)
        {
            if (input.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) != bytes.Length)
                throw new InvalidDataException("gzipのフッターまたはヘッダーが欠損しています。");
        }

        private void FinishMember()
        {
            var consumed = _deflate!.TotalIn;
            _deflate.Dispose();
            _deflate = null;
            input.Position = checked(_deflateStart + consumed);
            Span<byte> trailer = stackalloc byte[8];
            ReadRequired(trailer);
            if (BinaryPrimitives.ReadUInt32LittleEndian(trailer) != ~_crc ||
                BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) != unchecked((uint)_size))
                throw new InvalidDataException("gzipのCRCまたは復号サイズが一致しません。");
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (buffer.IsEmpty || _end) return 0;
            while (true)
            {
                if (_deflate is null && !StartMember()) { _end = true; return 0; }
                var count = _deflate!.Read(_buffer, 0, Math.Min(buffer.Length, _buffer.Length));
                if (count == 0) { FinishMember(); continue; }
                var bytes = _buffer.AsSpan(0, count);
                _crc = UpdateCrc(_crc, bytes);
                _size += count;
                bytes.CopyTo(buffer);
                return count;
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _deflate?.Dispose();
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => token.ThrowIfCancellationRequested();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
