using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace DiffBeacon.Providers;

public sealed partial class ManagedArchive
{
    private void WriteGZipOutput(Stream output, Action<Action<string, Stream?, DateTime?>> populate,
        ManagedArchiveWriteOptions options, CancellationToken token)
    {
        var encoding = options.NameEncoding();
        var count = 0;
        populate((path, content, time) =>
        {
            token.ThrowIfCancellationRequested();
            if (content is null || count != 0)
                throw new InvalidDataException("gzip出力にはディレクトリを含まない単一ファイルが必要です。");
            count++;
            // 全格納名の安全性を先に検査し、OSに依存せずbasenameだけをFNAMEへ保存する。
            var fullName = ValidateEntryPath(path);
            var name = fullName[(fullName.LastIndexOf('/') + 1)..];
            if (name.Length > 4096) throw new InvalidDataException("gzip格納名の文字数上限を超えました。");
            byte[] nameBytes;
            try
            {
                if (encoding.GetByteCount(name) > 16 * 1024)
                    throw new InvalidDataException("gzip格納名のbyte数上限を超えました。");
                nameBytes = encoding.GetBytes(name);
                if (nameBytes.AsSpan().Contains((byte)0) || !string.Equals(encoding.GetString(nameBytes), name, StringComparison.Ordinal))
                    throw new InvalidDataException("gzip格納名を選択した文字コードで保持できません。");
            }
            catch (EncoderFallbackException exception)
            {
                throw new InvalidDataException("gzip格納名を選択した文字コードで符号化できません。", exception);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("gzip格納名を選択した文字コードで復号できません。", exception);
            }
            Span<byte> header = stackalloc byte[10];
            header.Clear();
            header[0] = 0x1f; header[1] = 0x8b; header[2] = 8; header[3] = 8;
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], GZipWriteTime(time));
            header[9] = 255; // OS不明、XFL=0（DeflateStreamの実装依存levelを宣言しない）。
            output.Write(header);
            output.Write(nameBytes);
            output.WriteByte(0);
            uint crc = uint.MaxValue;
            long length = 0;
            var buffer = new byte[64 * 1024];
            token.ThrowIfCancellationRequested();
            var read = content.Read(buffer);
            if (read == 0)
            {
                // WriteのないDeflateStreamはDispose時の出力を破棄するため、空の最終blockを一度だけ書く。
                // BFINAL=1/BTYPE=01、固定HuffmanのEOB(256)=0000000とbyte境界padding。
                output.Write(new byte[] { 0x03, 0x00 });
            }
            else
            {
                using var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true);
                do
                {
                    token.ThrowIfCancellationRequested();
                    if (read > _limits.MaximumEntryBytes - length || read > _limits.MaximumDecodedBytes - length)
                        throw new InvalidDataException("アーカイブ内容のサイズ上限を超えました。");
                    length += read;
                    foreach (var value in buffer.AsSpan(0, read))
                        crc = DecodedSink.CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
                    deflate.Write(buffer, 0, read);
                    token.ThrowIfCancellationRequested();
                    read = content.Read(buffer);
                } while (read != 0);
            }
            token.ThrowIfCancellationRequested();
            Span<byte> trailer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(trailer[..4], ~crc);
            BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], unchecked((uint)length));
            output.Write(trailer);
        });
        // RepackのReadが全entry/CRC/EOF検証を終えた後だけSaveOutputに公開を返す。
        token.ThrowIfCancellationRequested();
        if (count != 1) throw new InvalidDataException("gzip出力には単一ファイルが必要です。");
    }

    private static uint GZipWriteTime(DateTime? time)
    {
        // 固定7-Zip GzHandlerのFileTime_To_UnixTimeと同じ秒切捨て・上下限への飽和。
        // 未指定/epoch以前は0（gzipではmtime不明）、上限超過は0xffffffff。
        if (time is not { } modified) return 0;
        var utc = modified.ToUniversalTime();
        if (utc <= DateTime.UnixEpoch) return 0;
        var seconds = (utc.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond;
        return seconds > uint.MaxValue ? uint.MaxValue : (uint)seconds;
    }
}