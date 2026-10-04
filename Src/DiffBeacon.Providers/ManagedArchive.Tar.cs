using System.Formats.Tar;
using SharpCompress.Compressors.BZip2;

namespace DiffBeacon.Providers;

public sealed partial class ManagedArchive
{
    private static string? DetectTarFormat(Stream input, string path)
    {
        Span<byte> header = stackalloc byte[512];
        var length = input.ReadAtLeast(header, 2, throwOnEndOfStream: false);
        input.Position = 0;
        if (OutputFormat(path) == "tar.Z") return "tar.Z";
        if (length >= 2 && header[0] == 0x1f && header[1] == 0x8b) return "tar.gz";
        if (length >= 3 && header[..3].SequenceEqual("BZh"u8)) return "tar.bz2";
        if (length >= 2 && header[0] == 0x1f && header[1] == 0x9d) return "tar.Z";
        if (length == 512 && IsTarHeader(header)) return "tar";
        // 壊れたTARを別形式へ迂回させず、TARの整合性検証へ渡す。
        return OutputFormat(path) is "tar" or "tar.gz" or "tar.bz2" ? "tar" : null;
    }

    private static bool IsTarHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != 512) return false;
        if (header.IndexOfAnyExcept((byte)0) < 0) return true;
        var checksum = 0;
        var digits = false;
        foreach (var value in header.Slice(148, 8))
        {
            if (value is 0 or 32) continue;
            if (value is < (byte)'0' or > (byte)'7') return false;
            checksum = checksum * 8 + value - '0';
            digits = true;
        }
        if (!digits) return false;
        var unsigned = 0; var signed = 0;
        for (var index = 0; index < header.Length; index++)
        {
            var value = index is >= 148 and < 156 ? (byte)32 : header[index];
            unsigned += value; signed += unchecked((sbyte)value);
        }
        return checksum == unsigned || checksum == signed;
    }

    private ManagedArchiveManifest ReadTar(Stream input, string format, CancellationToken token,
        Func<ManagedArchiveEntry, bool>? capture, Action<ManagedArchiveEntry, MemoryStream?>? consume,
        long? captureLimit, bool prefixOnly, ArchiveReadBudget? budget = null,
        OwnedEntryCapture? ownedCapture = null, bool inputAlreadyDecoded = false)
    {
        if (budget is not null && format != "tar") { budget.Layers(1); budget.Item(); }
        using var session = new TarReadSession(input, format, _limits.MaximumDecodedBytes,
            _limits.MaximumEntryBytes, _limits.MaximumEntries, token, budget, inputAlreadyDecoded);
        var reader = session.Reader;
        var names = new EntryNames(_limits.MaximumEntries, _limits.MaximumPathCharacters, budget);
        var result = new List<ManagedArchiveEntry>();
        long total = 0;
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            token.ThrowIfCancellationRequested();
            if (entry.EntryType == TarEntryType.GlobalExtendedAttributes) continue;
            var directory = entry.EntryType == TarEntryType.Directory;
            if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("TARのリンク・特殊エントリは読み取れません。");
            if (!string.IsNullOrEmpty(entry.LinkName)) throw new InvalidDataException("TARのリンクは読み取れません。");
            // PAX sizeを適用した検証境界とDataStreamの境界が一致することを確認する。
            if (entry.Length != session.EntryLength)
                throw new InvalidDataException("TARの補助metadataとヘッダーのサイズが一致しません。");
            budget?.CheckPath(entry.Name);
            var tarName = entry.Name.Replace('\\', '/');
            while (tarName.StartsWith("./", StringComparison.Ordinal)) tarName = tarName[2..];
            if (tarName is "" or ".")
            {
                if (!directory || entry.Length != 0) throw new InvalidDataException("TARのルート項目が不正です。");
                continue;
            }
            var name = ValidateEntryPath(tarName);
            names.Add(name, directory);
            if (entry.Length < 0 || entry.Length > _limits.MaximumEntryBytes ||
                entry.Length > _limits.MaximumDecodedBytes - total || directory && entry.Length != 0)
                throw new InvalidDataException("アーカイブ内容のサイズ上限超過、または不正なサイズです。");
            var metadata = new ManagedArchiveEntry(name, directory, entry.Length, "", false, entry.ModificationTime.UtcDateTime);
            budget?.Work(64);
            var keep = capture?.Invoke(metadata) == true || ownedCapture?.Wants(metadata) == true;
            if (keep && !prefixOnly && entry.Length > (captureLimit ?? _limits.MaximumEntryBytes))
                throw new InvalidDataException("プレビューのサイズ上限を超えました。");
            var content = keep ? prefixOnly ? new PrefixMemoryStream(checked((int)captureLimit!.Value))
                : ownedCapture is not null ? new BoundedCaptureStream(ownedCapture.MaximumBytes) : new MemoryStream() : null;
            var transferred = false;
            try
            {
              if (!directory)
              {
                using var sink = new DecodedSink(content, Math.Min(_limits.MaximumEntryBytes,
                    Math.Min(_limits.MaximumDecodedBytes - total, keep && !prefixOnly ? captureLimit ?? _limits.MaximumEntryBytes : _limits.MaximumEntryBytes)), token, budget, chargeDecoded: false);
                entry.DataStream?.CopyTo(sink);
                if (sink.Length != entry.Length) throw new InvalidDataException("宣言されたサイズと展開したサイズが一致しません。");
                total += sink.Length;
                metadata = metadata with { Sha256 = sink.Hash() };
              }
              result.Add(metadata);
              consume?.Invoke(metadata, content);
              transferred = ownedCapture?.Take(metadata, content) == true;
            }
            finally { if (!transferred) content?.Dispose(); }
        }
        session.CompleteRead();
        result.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        return new(format, result.AsReadOnly());
    }

    // 構造検証は共有し、リンク等のentry許可は各consumerで判断する。
    internal static TarReadSession OpenTarSession(Stream input, string path, long maximumDecoded,
        long maximumEntry, int maximumEntries, CancellationToken token)
    {
        if (!TryGetWrapperChain(path, out _, out var terminalType, out var depth) || terminalType != SharpCompress.Common.ArchiveType.Tar)
            return new(input, DetectTarFormat(input, path) ?? "tar", maximumDecoded, maximumEntry, maximumEntries, token);
        var limits = new ManagedArchiveLimits(MaximumEntries: maximumEntries, MaximumEntryBytes: maximumEntry,
            MaximumDecodedBytes: maximumDecoded);
        var service = new ManagedArchive(limits);
        var budget = new ArchiveReadBudget(limits);
        var decoded = service.DecodeWrappers(input, path, depth, token, budget, out _);
        try
        {
            // 全層検証済みのTARを所有し、metadataの読込みも共有作業量へ計上する。
            return new(new WorkReadStream(decoded, budget, token), "tar", maximumDecoded, maximumEntry,
                maximumEntries, token, budget, inputAlreadyDecoded: true, ownedInput: decoded);
        }
        catch { decoded.Dispose(); throw; }
    }

    internal sealed class TarReadSession : IDisposable
    {
        private readonly Stream? _compression;
        private readonly SequentialLimitStream _decoded;
        private readonly TarValidationStream _verified;
        private readonly CancellationToken _token;
        private readonly Stream? _ownedInput;
        private readonly ArchiveReadBudget? _budget;
        private bool _complete;
        public TarReader Reader { get; }
        public long EntryLength => _verified.EntryLength;
        internal TarReadSession(Stream input, string format, long maximumDecoded, long maximumEntry,
            int maximumEntries, CancellationToken token, ArchiveReadBudget? budget = null, bool inputAlreadyDecoded = false,
            Stream? ownedInput = null)
        {
            _token = token;
            _ownedInput = ownedInput;
            _budget = budget;
            _compression = format switch
            {
                "tar.gz" => new VerifiedGZipStream(input, token, maximumEntries, budget),
                "tar.bz2" when budget is not null => new VerifiedBZip2Stream(input, token, budget),
                "tar.bz2" => BZip2Stream.Create(input, SharpCompress.Compressors.CompressionMode.Decompress, true, leaveOpen: true),
                "tar.Z" => new ZReadStream(input, token, leaveOpen: true),
                _ => null
            };
            // 復号したmetadata/paddingも総量に含める。
            _decoded = new SequentialLimitStream(_compression ?? input, maximumDecoded, token, budget,
                chargeDecoded: _compression is not null || !inputAlreadyDecoded);
            _verified = new TarValidationStream(_decoded, maximumEntry, maximumEntries, budget);
            Reader = new TarReader(_verified, leaveOpen: true);
        }
        internal void ChargeProviderEntry(string name)
        {
            _budget?.Item();
            _budget?.PathCharacters(name.Length);
            _budget?.Work(64);
        }
        public void CompleteRead()
        {
            _token.ThrowIfCancellationRequested();
            if (_complete) return;
            Span<byte> trailing = stackalloc byte[8192];
            int read;
            while ((read = _verified.Read(trailing)) != 0)
                if (trailing[..read].IndexOfAnyExcept((byte)0) >= 0)
                    throw new InvalidDataException("TARの終端後に不正な内容があります。");
            if (_verified.ZeroBlocks < 2 || _decoded.BytesRead % 512 != 0)
                throw new InvalidDataException("TARの終端またはブロック長が不正です。");
            _token.ThrowIfCancellationRequested(); _complete = true;
        }
        public void Dispose()
        {
            try { Reader.Dispose(); _verified.Dispose(); _decoded.Dispose(); _compression?.Dispose(); }
            finally { _ownedInput?.Dispose(); }
        }
    }

    private sealed class SequentialLimitStream(Stream inner, long limit, CancellationToken token,
        ArchiveReadBudget? budget = null, bool chargeDecoded = true) : Stream
    {
        public long BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => ReadWithToken(buffer, default);
        internal int ReadWithToken(Span<byte> buffer, CancellationToken callToken)
        {
            token.ThrowIfCancellationRequested(); callToken.ThrowIfCancellationRequested();
            // 上限の次の1byteを読んでEOFと超過を区別する。
            var requested = buffer[..(int)Math.Min(buffer.Length, Math.Max(1, limit - BytesRead))];
            var length = inner is ZReadStream z ? z.Read(requested, callToken) : inner.Read(requested);
            callToken.ThrowIfCancellationRequested();
            BytesRead += length;
            if (BytesRead > limit) throw new InvalidDataException("圧縮アーカイブの復号サイズ上限を超えました。");
            if (chargeDecoded) budget?.Decoded(length);
            return length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => token.ThrowIfCancellationRequested();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // TarReaderはchecksumを比較しないため、PAX/GNU補助headerも含めて事前検証する。
    // 補助headerは1MiBまでに絞り、TarReader内部のmetadata一括確保を制限する。
    private sealed class TarValidationStream(Stream inner, long maximumEntry, int maximumEntries,
        ArchiveReadBudget? budget = null) : Stream
    {
        private readonly byte[] _header = new byte[512];
        private int _offset = 512;
        private long _remaining;
        private long _headers;
        private bool _eof;
        private byte[]? _paxMetadata;
        private int _paxOffset;
        private bool _globalMetadata;
        private long? _globalSize;
        private long? _pendingSize;
        private bool _hasPendingSize;
        public int ZeroBlocks { get; private set; }
        public long EntryLength { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 既存の同期構造検査を保ちながら、非同期consumerの取消も全inner I/Oへ渡す。
            _callToken = cancellationToken;
            try { return ValueTask.FromResult(Read(buffer.Span)); }
            finally { _callToken = default; }
        }
        private CancellationToken _callToken;
        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0 || _eof) return 0;
            if (_offset < 512)
            {
                var count = Math.Min(buffer.Length, 512 - _offset);
                _header.AsSpan(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }
            if (_remaining > 0)
            {
                var count = ReadInner(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
                if (count == 0) throw new InvalidDataException("TARの内容またはpaddingが途中で切れています。");
                if (_paxMetadata is not null)
                {
                    var metadataCount = Math.Min(count, _paxMetadata.Length - _paxOffset);
                    buffer[..metadataCount].CopyTo(_paxMetadata.AsSpan(_paxOffset));
                    _paxOffset += metadataCount;
                }
                _remaining -= count;
                if (_remaining == 0 && _paxMetadata is not null)
                {
                    ApplyPaxMetadata(_paxMetadata);
                    _paxMetadata = null;
                }
                return count;
            }
            var size = 0;
            while (size < 512)
            { var count = ReadInner(_header.AsSpan(size)); if (count == 0) break; size += count; }
            if (size == 0 && ZeroBlocks >= 2) { _eof = true; return 0; }
            if (size != 512 || !IsTarHeader(_header))
                throw new InvalidDataException("TARヘッダーのchecksumまたは終端が不正です。単一圧縮ファイルは未対応です。");
            if (_header.AsSpan().IndexOfAnyExcept((byte)0) < 0) ZeroBlocks++;
            else
            {
                if (ZeroBlocks > 0) throw new InvalidDataException("TAR終端の後にエントリがあります。");
                if (++_headers > (long)maximumEntries * 3)
                    throw new InvalidDataException("TARヘッダー数の上限を超えました。");
                budget?.TarHeader();
                var length = ParseTarLength(_header.AsSpan(124, 12));
                var metadata = _header[156] is (byte)'x' or (byte)'g' or (byte)'L' or (byte)'K';
                if (!metadata)
                {
                    length = _hasPendingSize ? _pendingSize ?? length : _globalSize ?? length;
                    _pendingSize = null;
                    _hasPendingSize = false;
                    EntryLength = length;
                }
                if (length > maximumEntry || metadata && length > 1024 * 1024)
                    throw new InvalidDataException("TAR内容またはmetadataサイズの上限を超えました。");
                if (_header[156] is (byte)'x' or (byte)'g')
                {
                    _globalMetadata = _header[156] == (byte)'g';
                    _paxOffset = 0;
                    _paxMetadata = new byte[checked((int)length)];
                    if (length == 0) _paxMetadata = null;
                }
                _remaining = checked((length + 511) / 512 * 512);
            }
            _offset = 0;
            return Read(buffer);
        }
        private int ReadInner(Span<byte> buffer)
        {
            _callToken.ThrowIfCancellationRequested();
            return inner is SequentialLimitStream bounded ? bounded.ReadWithToken(buffer, _callToken) : inner.Read(buffer);
        }
        private void ApplyPaxMetadata(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                var space = data.IndexOf((byte)' ');
                if (space <= 0 || !int.TryParse(System.Text.Encoding.ASCII.GetString(data[..space]),
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length)
                    || length <= space + 1 || length > data.Length || data[length - 1] != (byte)'\n')
                    throw new InvalidDataException("TARのPAXレコード長が不正です。");
                var record = data.Slice(space + 1, length - space - 2);
                var equals = record.IndexOf((byte)'=');
                if (equals <= 0) throw new InvalidDataException("TARのPAXレコードが不正です。");
                if (record[..equals].SequenceEqual("size"u8))
                {
                    var value = record[(equals + 1)..];
                    long? size = null;
                    if (!value.IsEmpty)
                    {
                        if (!long.TryParse(System.Text.Encoding.ASCII.GetString(value), System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed > maximumEntry)
                            throw new InvalidDataException("TARのPAXサイズが不正または上限を超えています。");
                        size = parsed;
                    }
                    if (_globalMetadata) _globalSize = size;
                    else { _pendingSize = size; _hasPendingSize = true; }
                }
                data = data[length..];
            }
        }
        private static long ParseTarLength(ReadOnlySpan<byte> field)
        {
            long length = 0;
            try
            {
                if ((field[0] & 0x80) != 0)
                {
                    if (field[0] != 0x80) throw new InvalidDataException("TARのサイズが不正です。");
                    foreach (var value in field[1..]) length = checked(length * 256 + value);
                }
                else
                {
                    var text = System.Text.Encoding.ASCII.GetString(field).Trim('\0', ' ');
                    foreach (var value in text)
                    {
                        if (value is < '0' or > '7') throw new InvalidDataException("TARのサイズが不正です。");
                        length = checked(length * 8 + value - '0');
                    }
                }
                return length;
            }
            catch (OverflowException) { throw new InvalidDataException("TARのサイズが不正です。"); }
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
