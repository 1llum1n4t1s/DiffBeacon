using System.Security.Cryptography;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Readers;
using SharpCompress.Writers.SevenZip;

namespace DiffBeacon.Providers;

public sealed record ManagedArchiveLimits(
    int MaximumEntries = 100_000,
    long MaximumInputBytes = 1024L * 1024 * 1024,
    long MaximumEntryBytes = 256L * 1024 * 1024,
    long MaximumDecodedBytes = 1024L * 1024 * 1024,
    int MaximumPreviewBytes = 16 * 1024 * 1024,
    long MaximumOutputBytes = 1024L * 1024 * 1024);

public sealed record ManagedArchiveEntry(string Path, bool IsDirectory, long Size,
    string Sha256, bool IsEncrypted, DateTime? LastModifiedTime);

public sealed record ManagedArchiveManifest(string Format, IReadOnlyList<ManagedArchiveEntry> Entries);

/// <summary>Content が null の場合はディレクトリ。格納名は安全な相対パスに限定する。</summary>
public sealed record ManagedArchiveWriteEntry(string Path, ReadOnlyMemory<byte>? Content,
    DateTime? LastModifiedTime = null);

/// <summary>7z・RAR・ZIP をディスクへ展開せずに読み、非暗号化 7z を保存する。</summary>
public sealed class ManagedArchive
{
    private readonly ManagedArchiveLimits _limits;

    public ManagedArchive(ManagedArchiveLimits? limits = null)
    {
        _limits = limits ?? new();
        if (_limits.MaximumEntries <= 0 || _limits.MaximumInputBytes <= 0 ||
            _limits.MaximumEntryBytes <= 0 || _limits.MaximumDecodedBytes <= 0 ||
            _limits.MaximumPreviewBytes <= 0 || _limits.MaximumOutputBytes <= 0 ||
            _limits.MaximumEntryBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public ManagedArchiveManifest ReadManifest(string path, string? password = null,
        CancellationToken cancellationToken = default) =>
        Read(path, password, cancellationToken, null, null);

    /// <summary>全エントリを検証してから、選択したファイルのバイト列を返す。</summary>
    public byte[] ReadEntry(string path, string entryPath, string? password = null,
        CancellationToken cancellationToken = default)
        => ReadEntryBytes(path, entryPath, password, cancellationToken, _limits.MaximumPreviewBytes);

    public byte[] ReadEntryForExport(string path, string entryPath, string? password = null,
        CancellationToken cancellationToken = default)
        => ReadEntryBytes(path, entryPath, password, cancellationToken, _limits.MaximumEntryBytes);

    public byte[] ReadEntryPreview(string path, string entryPath, string? password = null,
        CancellationToken cancellationToken = default, int maximumBytes = 4096)
    {
        if (maximumBytes <= 0 || maximumBytes > _limits.MaximumPreviewBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var name = ValidateEntryPath(entryPath); byte[]? result = null;
        Read(path, password, cancellationToken, entry => entry.Path == name && !entry.IsDirectory,
            (_, content) => { if (content is not null) result = content.ToArray(); }, maximumBytes, prefixOnly: true);
        return result ?? throw new FileNotFoundException("選択したアーカイブ内ファイルがありません。");
    }

    private byte[] ReadEntryBytes(string path, string entryPath, string? password, CancellationToken cancellationToken, long limit)
    {
        var name = ValidateEntryPath(entryPath);
        byte[]? result = null;
        Read(path, password, cancellationToken,
            entry => entry.Path == name && !entry.IsDirectory,
            (entry, content) =>
            {
                if (entry.Path == name && !entry.IsDirectory) result = content!.ToArray();
            }, limit);
        return result ?? throw new FileNotFoundException("選択したアーカイブ内ファイルがありません。");
    }

    /// <summary>元アーカイブを順次検証し、各ファイルを非 solid の LZMA2 7z に再梱包する。</summary>
    public void RepackToSevenZip(string sourcePath, string destinationPath, string? password = null,
        CancellationToken cancellationToken = default)
    {
        var source = ValidateLocalPath(sourcePath, mustExist: true);
        var destination = ValidateLocalPath(destinationPath, mustExist: false);
        if (ArchivePaths.SameFile(source, destination))
            throw new IOException("元アーカイブと出力先には別のパスが必要です。");
        Save(destination, cancellationToken, writer =>
            Read(source, password, cancellationToken, entry => !entry.IsDirectory,
                (entry, content) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.IsDirectory) writer.WriteDirectory(entry.Path, entry.LastModifiedTime);
                    else
                    {
                        content!.Position = 0;
                        using var bounded = new CheckedStream(content, _limits.MaximumEntryBytes, cancellationToken);
                        writer.Write(entry.Path, bounded, entry.LastModifiedTime);
                    }
                }));
    }

    /// <summary>呼び出し元が明示した内容から、非暗号化 LZMA2 7z を保存する。</summary>
    public void WriteSevenZip(string destinationPath, IEnumerable<ManagedArchiveWriteEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Save(ValidateLocalPath(destinationPath, mustExist: false), cancellationToken, writer =>
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            long total = 0;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(entry);
                var name = ValidateEntryPath(entry.Path);
                if (++count > _limits.MaximumEntries || !names.Add(name))
                    throw new InvalidDataException("エントリ数の上限超過、または重複した格納名です。");
                if (entry.Content is not { } bytes) writer.WriteDirectory(name, entry.LastModifiedTime);
                else
                {
                    if (bytes.Length > _limits.MaximumEntryBytes || bytes.Length > _limits.MaximumDecodedBytes - total)
                        throw new InvalidDataException("アーカイブ内容のサイズ上限を超えました。");
                    total += bytes.Length;
                    using var stream = new MemoryStream(bytes.ToArray(), writable: false);
                    using var bounded = new CheckedStream(stream, _limits.MaximumEntryBytes, cancellationToken);
                    writer.Write(name, bounded, entry.LastModifiedTime);
                }
            }
        });
    }

    private ManagedArchiveManifest Read(string path, string? password, CancellationToken token,
        Func<ManagedArchiveEntry, bool>? capture,
        Action<ManagedArchiveEntry, MemoryStream?>? consume, long? captureLimit = null, bool prefixOnly = false)
    {
        token.ThrowIfCancellationRequested();
        path = ValidateLocalPath(path, mustExist: true);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > _limits.MaximumInputBytes)
            throw new InvalidDataException("アーカイブ入力のサイズ上限を超えました。");
        using var input = new CheckedStream(file, _limits.MaximumInputBytes, token);
        try
        {
            // Stream API で隣接ボリュームの暗黙の探索・読み取りを防ぐ。
            using var archive = ArchiveFactory.OpenArchive(input, ReaderOptions.ForExternalStream.WithPassword(password));
            if (archive.Type is not (ArchiveType.SevenZip or ArchiveType.Rar or ArchiveType.Zip))
                throw new InvalidDataException("このサービスの読み取り対象は 7z・RAR・ZIP です。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<ManagedArchiveEntry>();
            long total = 0;
            if (archive.Type == ArchiveType.Rar)
            {
                input.Position = 0;
                using var reader = ReaderFactory.OpenReader(input, ReaderOptions.ForExternalStream.WithPassword(password));
                while (reader.MoveToNextEntry()) Process(reader.Entry, sink => reader.WriteEntryTo(sink));
            }
            else if (archive.Type == ArchiveType.SevenZip)
            {
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry()) Process(reader.Entry, sink => reader.WriteEntryTo(sink));
            }
            else
            {
                foreach (var entry in archive.Entries)
                    Process(entry, sink => entry.WriteTo(sink, new ExtractionOptions { CheckCrc = true }));
            }
            token.ThrowIfCancellationRequested();
            result.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
            return new(archive.Type switch
            {
                ArchiveType.SevenZip => "7z", ArchiveType.Rar => "rar", _ => "zip"
            }, result.AsReadOnly());

            void Process(IEntry entry, Action<Stream> decode)
            {
                token.ThrowIfCancellationRequested();
                var name = ValidateEntryPath(entry.Key);
                if (result.Count >= _limits.MaximumEntries || !names.Add(name))
                    throw new InvalidDataException("エントリ数の上限超過、または重複した格納名です。");
                // 7z の実装は VolumeIndex にエントリ順序を格納するため、分割判定に使わない。
                if (IsLink(entry) || entry.IsSplitAfter || archive.Type != ArchiveType.SevenZip && (entry.VolumeIndexFirst > 0 || entry.VolumeIndexLast > 0))
                    throw new InvalidDataException("リンク・分割エントリは読み取れません。");
                if (entry.Size < 0 || entry.Size > _limits.MaximumEntryBytes ||
                    entry.Size > _limits.MaximumDecodedBytes - total || (entry.IsDirectory && entry.Size != 0))
                    throw new InvalidDataException("アーカイブ内容のサイズ上限超過、または不正なサイズです。");
                if (entry.IsEncrypted && string.IsNullOrEmpty(password))
                    throw new InvalidDataException("暗号化アーカイブにはパスワードが必要です。");
                var metadata = new ManagedArchiveEntry(name, entry.IsDirectory, entry.Size,
                    "", entry.IsEncrypted, entry.LastModifiedTime);
                var keep = capture?.Invoke(metadata) == true;
                if (keep && !prefixOnly && entry.Size > (captureLimit ?? _limits.MaximumEntryBytes))
                    throw new InvalidDataException("プレビューのサイズ上限を超えました。");
                using var content = keep ? prefixOnly ? new PrefixMemoryStream(checked((int)captureLimit!.Value)) : new MemoryStream() : null;
                if (!entry.IsDirectory)
                {
                    using var sink = new DecodedSink(content, Math.Min(_limits.MaximumEntryBytes,
                        Math.Min(_limits.MaximumDecodedBytes - total,
                            keep && !prefixOnly ? captureLimit ?? _limits.MaximumEntryBytes : _limits.MaximumEntryBytes)), token);
                    decode(sink);
                    if (sink.Length != entry.Size)
                        throw new InvalidDataException("宣言されたサイズと展開したサイズが一致しません。");
                    // RAR5 暗号化の CRC は秘密鍵で変換されるため、公開 CRC 値とは比較しない。
                    // ZIP AES AE-2 と CRC を省略した 7z は値が 0。復号器の認証検証に従う。
                    if (!(archive.Type == ArchiveType.Rar && entry.IsEncrypted))
                    {
                        long crc;
                        try { crc = entry.Crc; }
                        catch (ArgumentNullException) { crc = 0; } // CRC を格納しない RAR5。
                        if (crc != 0 && unchecked((uint)crc) != sink.Crc32)
                            throw new InvalidDataException("アーカイブ内容の CRC が一致しません。");
                    }
                    total += sink.Length;
                    metadata = metadata with { Sha256 = sink.Hash() };
                }
                result.Add(metadata);
                consume?.Invoke(metadata, content);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (password is not null)
        {
            // 依存ライブラリの例外文や inner exception に認証情報を残さない。
            throw new InvalidDataException("アーカイブを読み取れません。パスワード、破損、圧縮方式を確認してください。");
        }
    }

    private void Save(string destination, CancellationToken token, Action<SevenZipWriter> write)
    {
        token.ThrowIfCancellationRequested();
        ValidateOutput(destination);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".diffbeacon-{Guid.NewGuid():N}.7z.tmp");
        var created = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                created = true;
                using var output = new CheckedStream(file, _limits.MaximumOutputBytes, token);
                using (var writer = new SevenZipWriter(output, new SevenZipWriterOptions(CompressionType.LZMA2)))
                    write(writer);
                token.ThrowIfCancellationRequested();
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            ValidateOutput(destination);
            if (!OperatingSystem.IsWindows() && File.Exists(destination)) File.SetUnixFileMode(temporary, File.GetUnixFileMode(destination));
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
            created = false;
        }
        finally
        {
            if (created) File.Delete(temporary);
        }
    }

    private static void ValidateOutput(string path)
    {
        ValidateLocalPath(path, mustExist: false);
        if (!Directory.Exists(Path.GetDirectoryName(path)))
            throw new DirectoryNotFoundException("アーカイブの出力先ディレクトリがありません。");
        if (Directory.Exists(path)) throw new IOException("アーカイブ出力先はファイルである必要があります。");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new IOException("読み取り専用アーカイブは上書きできません。");
    }

    private static string ValidateLocalPath(string path, bool mustExist)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var current = full;
        while (current is not null)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException("アーカイブのパスにはリンクを使用できません。");
            current = Path.GetDirectoryName(current);
        }
        if (mustExist && !File.Exists(full)) throw new FileNotFoundException("アーカイブがありません。", full);
        return full;
    }

    private static string ValidateEntryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 ||
            path.Any(c => char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|'))
            throw new InvalidDataException("安全でないアーカイブ格納名です。");
        var name = path.Replace('\\', '/');
        if (name.StartsWith('/')) throw new InvalidDataException("絶対格納パスは使用できません。");
        name = name.TrimEnd('/');
        foreach (var part in name.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.'))
                throw new InvalidDataException("安全でないアーカイブ格納名です。");
            var stem = part.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9'))
                throw new InvalidDataException("予約されたアーカイブ格納名です。");
        }
        return name;
    }

    private static bool IsLink(IEntry entry)
    {
        if (entry.LinkTarget is not null || entry is RarEntry { IsRedir: true }) return true;
        if (entry.Attrib is not { } attributes) return false;
        var unixKind = (attributes >> 16) & 0xf000;
        var rarUnixKind = entry is RarEntry ? attributes & 0xf000 : 0;
        return (attributes & (int)FileAttributes.ReparsePoint) != 0 || unixKind == 0xa000 || rarUnixKind == 0xa000;
    }

    private sealed class PrefixMemoryStream(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
            => base.Write(buffer, offset, Math.Min(count, limit - checked((int)Length)));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var count = Math.Min(buffer.Length, limit - checked((int)Length));
            if (count > 0) { var bytes = buffer[..count].ToArray(); base.Write(bytes, 0, bytes.Length); }
        }
    }

    private sealed class DecodedSink(MemoryStream? content, long limit, CancellationToken token) : Stream
    {
        private static readonly uint[] CrcTable = CreateCrcTable();
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;
        private uint _crc = uint.MaxValue;
        public uint Crc32 => ~_crc;
        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320U : 0);
                table[index] = value;
            }
            return table;
        }
        public string Hash() => Convert.ToHexString(_hash.GetHashAndReset());
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (buffer.Length > limit - _length) throw new InvalidDataException("展開サイズの上限を超えました。");
            _length += buffer.Length;
            _hash.AppendData(buffer);
            foreach (var value in buffer) _crc = CrcTable[(_crc ^ value) & 255] ^ (_crc >> 8);
            content?.Write(buffer);
        }
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() => token.ThrowIfCancellationRequested();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // seekable な入出力を維持し、圧縮ライブラリからの I/O に上限とキャンセルを適用する。
    private sealed class CheckedStream(Stream inner, long limit, CancellationToken token) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => Seek(value, SeekOrigin.Begin); }
        public override void Flush() { token.ThrowIfCancellationRequested(); inner.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (inner.Position > limit) throw new InvalidDataException("アーカイブ入力の上限を超えました。");
            return inner.Read(buffer[..(int)Math.Min(buffer.Length, limit - inner.Position)]);
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (buffer.Length > limit - inner.Position) throw new InvalidDataException("アーカイブ出力の上限を超えました。");
            inner.Write(buffer);
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            token.ThrowIfCancellationRequested();
            var basis = origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => inner.Position, SeekOrigin.End => inner.Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
            var position = checked(basis + offset);
            if (position < 0 || position > limit) throw new InvalidDataException("アーカイブ I/O の範囲を超えました。");
            return inner.Seek(position, SeekOrigin.Begin);
        }
        public override void SetLength(long value)
        {
            token.ThrowIfCancellationRequested();
            if (value < 0 || value > limit) throw new InvalidDataException("アーカイブ出力の上限を超えました。");
            inner.SetLength(value);
        }
    }
}
