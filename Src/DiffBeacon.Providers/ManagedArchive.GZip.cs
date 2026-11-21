using System.Buffers.Binary;
using System.Text;
using SharpDeflateStream = SharpCompress.Compressors.Deflate.DeflateStream;

namespace DiffBeacon.Providers;

public sealed partial class ManagedArchive
{
    private static bool IsGZip(Stream input)
    {
        input.Position = 0;
        var first = input.ReadByte(); var second = input.ReadByte(); input.Position = 0;
        return first == 0x1f && second == 0x8b;
    }

    private ManagedArchiveManifest ReadGZip(Stream input, string logicalName, ManagedArchiveReadOptions options,
        CancellationToken token, Func<ManagedArchiveEntry, bool>? capture,
        Action<ManagedArchiveEntry, MemoryStream?>? consume, long? captureLimit, bool prefixOnly,
        ArchiveReadBudget? sharedBudget, OwnedEntryCapture? ownedCapture)
    {
        var budget = sharedBudget ?? new ArchiveReadBudget(_limits);
        budget.Layers(1);
        using var workInput = sharedBudget is null ? new WorkReadStream(input, budget, token) : null;
        using var gzip = new VerifiedGZipStream(workInput ?? input, token, _limits.MaximumEntries, budget, captureFirstName: true);
        var buffer = new byte[64 * 1024];
        // 物理名やFNAMEから再帰せず、同じdecoderで得た先頭だけでTARを判定する。
        // 512bytes未満の裸fileやmember境界を跨ぐ先頭もそのままsinkへ渡す。
        var count = gzip.ReadAtLeast(buffer.AsSpan(0, 512), 512, throwOnEndOfStream: false);
        // Fileは任意本文を一つのfileとして扱い、Tarは先頭sniffに関係なく完全TAR検証する。
        if (options.GZipPayloadKind == GZipPayloadKind.Tar ||
            options.GZipPayloadKind == GZipPayloadKind.Auto && count == 512 && IsTarHeader(buffer.AsSpan(0, 512)))
            return ReadGZipTar(input, gzip, buffer, count, token, capture, consume, captureLimit,
                prefixOnly, budget, ownedCapture);
        ManagedArchiveEntry? provisional = null;
        Exception? nameFailure = null;
        try
        {
            if (gzip.FirstNameOverflow) throw new InvalidDataException("gzip格納名の上限を超えました。");
            budget.Work(gzip.FirstName.Length);
            RejectUnambiguousNamePath(gzip.FirstName, options.GZipNameCodePage);
            string decodedName;
            try
            {
                var encoding = options.NameEncoding();
                var characters = encoding.GetCharCount(gzip.FirstName);
                if (characters > 4096) throw new InvalidDataException("gzip格納名の上限を超えました。");
                budget.PathCharacters(characters);
                decodedName = encoding.GetString(gzip.FirstName);
            }
            catch (DecoderFallbackException) { throw new ArchiveNameDecodingException(); }
            // Latin1のC1制御文字は旧CP932名の明示選択で回復できる復号失敗。
            if (options.GZipNameCodePage == 28591 && decodedName.Any(character => character is >= '\u0080' and <= '\u009f'))
                throw new ArchiveNameDecodingException();
            if (decodedName.Length == 0)
            {
                decodedName = Path.GetFileName(logicalName.Replace('\\', '/'));
                var dot = decodedName.LastIndexOf('.');
                // WinMergeのMerge7z GetDefaultNameはSDKの既定名を上書きする。
                decodedName = dot >= 0 ? decodedName[..dot] : "noname";
            }
            var name = ValidateEntryPath(decodedName);
            var names = new EntryNames(_limits.MaximumEntries, _limits.MaximumPathCharacters, budget);
            names.Add(name, false);
            provisional = new(name, false, 0, "", false, gzip.FirstModifiedTime);
        }
        catch (Exception exception) when (exception is ArchiveNameDecodingException or InvalidDataException) { nameFailure = exception; }
        var wants = provisional is not null && (ownedCapture?.Wants(provisional) == true || capture?.Invoke(provisional) == true);
        var limit = Math.Min(_limits.MaximumEntryBytes, _limits.MaximumDecodedBytes);
        var retainedLimit = captureLimit ?? _limits.MaximumPreviewBytes;
        MemoryStream? content = wants ? prefixOnly ? new PrefixMemoryStream(checked((int)retainedLimit)) : new BoundedCaptureStream(checked((int)Math.Min(limit, retainedLimit))) : null;
        var transferred = false;
        try
        {
            using var sink = new DecodedSink(content, limit, token, budget);
            while (count != 0)
            {
                sink.Write(buffer, 0, count);
                count = gzip.Read(buffer);
            }
            token.ThrowIfCancellationRequested();
            if (input.Position != input.Length) throw new InvalidDataException("gzipの後続内容を完全に検証できませんでした。");
            if (nameFailure is not null) throw nameFailure;
            var entry = provisional! with { Size = sink.Length, Sha256 = sink.Hash() };
            if (content is not null) content.Position = 0;
            if (ownedCapture is not null) transferred = ownedCapture.Take(entry, content);
            else consume?.Invoke(entry, content);
            return new("gzip", new[] { entry });
        }
        finally { if (!transferred) content?.Dispose(); }
    }

    private ManagedArchiveManifest ReadGZipTar(Stream input, VerifiedGZipStream gzip, byte[] buffer, int count,
        CancellationToken token, Func<ManagedArchiveEntry, bool>? capture,
        Action<ManagedArchiveEntry, MemoryStream?>? consume, long? captureLimit, bool prefixOnly,
        ArchiveReadBudget budget, OwnedEntryCapture? ownedCapture)
    {
        // TAR全体は単entry上限では制限しない。padding/metadataを含むgzip復号を一度だけ課金し、
        // 全memberのfooter/EOFが確定してから既存のTAR検証と各entry上限を適用する。
        using var decoded = new MemoryStream();
        var limit = Math.Min(_limits.MaximumDecodedBytes, int.MaxValue);
        while (count != 0)
        {
            token.ThrowIfCancellationRequested();
            if (count > limit - decoded.Length) throw new InvalidDataException("展開サイズの上限を超えました。");
            budget.Decoded(count);
            var needed = checked((int)decoded.Length + count);
            if (needed > decoded.Capacity)
                decoded.Capacity = (int)Math.Min(limit, Math.Max(needed, (long)decoded.Capacity * 2));
            decoded.Write(buffer, 0, count);
            var remaining = Math.Min(limit - decoded.Length, budget.DecodedRemaining);
            // exact limitでも次の1byteを検査し、正規EOFと超過を区別する。
            count = gzip.Read(buffer, 0, remaining < buffer.Length ? checked((int)remaining + 1) : buffer.Length);
        }
        token.ThrowIfCancellationRequested();
        if (input.Position != input.Length) throw new InvalidDataException("gzipの後続内容を完全に検証できませんでした。");
        decoded.Position = 0;
        using var tarInput = new WorkReadStream(decoded, budget, token);
        return ReadTar(tarInput, "tar", token, capture, consume, captureLimit, prefixOnly,
            budget, ownedCapture, inputAlreadyDecoded: true) with { Format = "tar.gz" };
    }

    // 復号失敗と独立して確定できるASCIIの危険な経路だけを先に拒否する。
    // DBCSの末尾0x5Cを区切りと誤認せず、名前の採用には必ず指定decoderの全文復号を使う。
    private static void RejectUnambiguousNamePath(ReadOnlySpan<byte> bytes, int codePage)
    {
        if (bytes.IsEmpty) return;
        if (bytes[0] is (byte)'/' or (byte)'\\' || bytes.Contains((byte)':'))
            throw new InvalidDataException("安全でないアーカイブ格納名です。");
        var start = 0;
        for (var index = 0; index <= bytes.Length; index++)
        {
            if (index < bytes.Length && bytes[index] >= 0x80 && index + 1 < bytes.Length)
            {
                // 未選択の旧名でも、曖昧な0x5Cを構造エラーと断定しない。
                var candidates = codePage is 932 or 936 or 949 or 950 or 1361 ? new[] { codePage }
                    : bytes[index + 1] == (byte)'\\' ? new[] { 932, 936, 949, 950, 1361 } : Array.Empty<int>();
                var pair = false;
                foreach (var candidate in candidates)
                    try { if (new ManagedArchiveReadOptions(candidate).NameEncoding().GetCharCount(bytes.Slice(index, 2)) == 1) { pair = true; break; } }
                    catch (DecoderFallbackException) { }
                if (pair) { index++; continue; }
            }
            if (index < bytes.Length)
            {
                var octet = bytes[index];
                if (octet < 32 || octet == 127 || octet is (byte)':' or (byte)'*' or (byte)'?' or (byte)'"' or (byte)'<' or (byte)'>' or (byte)'|')
                    throw new InvalidDataException("安全でないアーカイブ格納名です。");
                if (octet is not ((byte)'/' or (byte)'\\')) continue;
            }
            var part = bytes[start..index];
            if (part.SequenceEqual("."u8) || part.SequenceEqual(".."u8)
                || part.IsEmpty && index < bytes.Length && bytes[index..].IndexOfAnyExcept((byte)'/', (byte)'\\') >= 0)
                throw new InvalidDataException("安全でないアーカイブ格納名です。");
            if (!part.IsEmpty && part[^1] is (byte)' ' or (byte)'.')
                throw new InvalidDataException("安全でないアーカイブ格納名です。");
            var dot = part.IndexOf((byte)'.'); var stem = dot >= 0 ? part[..dot] : part;
            bool SameAscii(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
            {
                if (left.Length != right.Length) return false;
                for (var item = 0; item < left.Length; item++)
                    if (left[item] != right[item] && left[item] != right[item] + 32) return false;
                return true;
            }
            if (SameAscii(stem, "CON"u8) || SameAscii(stem, "PRN"u8) || SameAscii(stem, "AUX"u8) || SameAscii(stem, "NUL"u8)
                || stem.Length == 4 && (SameAscii(stem[..3], "COM"u8) || SameAscii(stem[..3], "LPT"u8)) && stem[3] is >= (byte)'1' and <= (byte)'9')
                throw new InvalidDataException("予約されたアーカイブ格納名です。");
            start = index + 1;
        }
    }

    // BCL GZipStreamは欠損footerでもEOFを返すため、メンバー境界を明示検証する。
    // managed DEFLATEの消費量を使い、先読みされたfooter/次メンバーを正確に巻き戻す。
    private sealed class VerifiedGZipStream(Stream input, CancellationToken token, int maximumMembers,
        ArchiveReadBudget? budget = null, bool captureFirstName = false) : Stream
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private SharpDeflateStream? _deflate;
        private long _deflateStart;
        private uint _crc;
        private long _size;
        private int _members;
        private bool _end;
        public byte[] FirstName { get; private set; } = [];
        public bool FirstNameOverflow { get; private set; }
        public DateTime? FirstModifiedTime { get; private set; }

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
            uint seconds = 0;
            for (var index = 0; index < 4; index++) seconds |= (uint)HeaderByte() << (index * 8);
            HeaderByte(); HeaderByte();
            if (_members == 1 && seconds != 0) FirstModifiedTime = DateTime.UnixEpoch.AddSeconds(seconds);
            if ((flags & 4) != 0)
            {
                var length = HeaderByte() | HeaderByte() << 8;
                for (var index = 0; index < length; index++) HeaderByte();
            }
            if ((flags & 8) != 0)
            {
                var name = captureFirstName && _members == 1 ? new List<byte>() : null;
                while (HeaderByte() is var octet && octet != 0)
                {
                    budget?.PathCharacters(1);
                    if (name is not null)
                    {
                        // 無視対象のTAR wrapper名も同じheader予算で最後まで検証する。
                        // 裸fileの名前超過は分類後に拒否し、後続CRCエラーを隠さない。
                        if (name.Count >= 16 * 1024) FirstNameOverflow = true;
                        else { budget?.Work(1); name.Add(octet); }
                    }
                }
                if (name is not null) { budget?.Work(name.Count); FirstName = name.ToArray(); }
            }
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
