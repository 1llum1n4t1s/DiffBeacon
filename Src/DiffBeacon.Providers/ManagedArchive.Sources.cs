using System.Security.Cryptography;

namespace DiffBeacon.Providers;

public sealed partial class ManagedArchive
{
    /// <summary>全外側を検証した後、指定された格納階層の一覧を返す。</summary>
    public ManagedArchiveSourceManifest ResolveManifest(ArchiveSource source,
        IReadOnlyList<string?>? containerPasswords = null, CancellationToken cancellationToken = default)
        => ResolveSource(source, containerPasswords, cancellationToken, null);

    /// <summary>全外側・全内側を検証した後、指定されたファイルの内容を返す。</summary>
    public byte[] ResolveEntry(ArchiveSource source, string entryPath, int maximumBytes,
        IReadOnlyList<string?>? containerPasswords = null, CancellationToken cancellationToken = default)
    {
        if (maximumBytes <= 0 || maximumBytes > _limits.MaximumEntryBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var capture = new OwnedEntryCapture(ValidateEntryPath(entryPath), maximumBytes);
        ResolveSource(source, containerPasswords, cancellationToken, capture);
        using var content = capture.Detach();
        cancellationToken.ThrowIfCancellationRequested();
        return content.ToArray();
    }

    /// <summary>全containerと全entryを検証し、選択ファイルの先頭だけを保持する。</summary>
    public byte[] ResolveEntryPreview(ArchiveSource source, string entryPath,
        IReadOnlyList<string?>? containerPasswords = null, CancellationToken cancellationToken = default, int maximumBytes = 4096)
    {
        if (maximumBytes <= 0 || maximumBytes > _limits.MaximumPreviewBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var capture = new OwnedEntryCapture(ValidateEntryPath(entryPath), maximumBytes, prefixOnly: true);
        ResolveSource(source, containerPasswords, cancellationToken, capture);
        using var content = capture.Detach();
        cancellationToken.ThrowIfCancellationRequested();
        return content.ToArray();
    }

    private ManagedArchiveSourceManifest ResolveSource(ArchiveSource source,
        IReadOnlyList<string?>? containerPasswords, CancellationToken token, OwnedEntryCapture? finalCapture)
    {
        ArgumentNullException.ThrowIfNull(source);
        token.ThrowIfCancellationRequested();
        var budget = new ArchiveReadBudget(_limits);
        // rootは0、格納entryへの遷移と各wrapperを1と数える。
        budget.Layers(source.EntryChain.Count);
        var passwordCount = checked(source.EntryChain.Count + 1);
        if (containerPasswords is not null && containerPasswords.Count > passwordCount)
            throw new ArgumentException("格納階層より多いパスワードが指定されました。", nameof(containerPasswords));
        var passwords = new string?[passwordCount];
        MemoryStream? owned = null;
        try
        {
            for (var index = 0; index < (containerPasswords?.Count ?? 0); index++)
            {
                var password = containerPasswords![index];
                if (password?.Length > 4096) throw new ArgumentException("パスワードが長すぎます。", nameof(containerPasswords));
                passwords[index] = password;
            }
            var path = ValidateLocalPath(source.RootPath, mustExist: true);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var input = new CheckedStream(file, _limits.MaximumInputBytes, token);
            var rootSha = InputHash(input, budget, token);
            if (source.RootSha256 is not null && !StringComparer.Ordinal.Equals(source.RootSha256, rootSha))
                throw new InvalidDataException("親比較後にアーカイブ入力が変更されました。再比較してください。");
            Stream current = input;
            var logicalName = path;
            var inputAlreadyDecoded = false;
            for (var index = 0; index < source.EntryChain.Count; index++)
            {
                using var capture = new OwnedEntryCapture(source.EntryChain[index], checked((int)_limits.MaximumEntryBytes));
                ReadLogical(current, logicalName, passwords[index], token, budget, inputAlreadyDecoded, capture,
                    new(source.ContainerNameCodePages[index], source.ContainerGZipPayloadKinds[index], source.ContainerCompressionPayloadKinds[index]));
                // 全entry・footer・EOF検証とreader解放が済むまで次段を公開しない。
                var next = capture.Detach();
                owned?.Dispose();
                owned = next;
                current = next;
                logicalName = source.EntryChain[index];
                inputAlreadyDecoded = true;
            }
            var manifest = ReadLogical(current, logicalName, passwords[^1], token, budget, inputAlreadyDecoded, finalCapture,
                new(source.ContainerNameCodePages[^1], source.ContainerGZipPayloadKinds[^1], source.ContainerCompressionPayloadKinds[^1]));
            if (!StringComparer.Ordinal.Equals(rootSha, InputHash(input, budget, token)))
                throw new InvalidDataException("比較中にアーカイブ入力が変更されました。再比較してください。");
            // 開いたhandleの内容と現在の物理pathを別々に検査し、path差替えも拒否する。
            ValidateLocalPath(path, mustExist: true);
            using var currentFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var currentInput = new CheckedStream(currentFile, _limits.MaximumInputBytes, token);
            if (!StringComparer.Ordinal.Equals(rootSha, InputHash(currentInput, budget, token)))
                throw new InvalidDataException("比較中にアーカイブの物理入力が差し替えられました。再比較してください。");
            token.ThrowIfCancellationRequested();
            return new(new ArchiveSource(path, source.EntryChain, rootSha, source.ContainerNameCodePages, source.ContainerGZipPayloadKinds, source.ContainerCompressionPayloadKinds), manifest);
        }
        catch (OperationCanceledException) { throw; }
        catch (ArchiveNameDecodingException) { throw; }
        catch (Exception) when (passwords.Any(password => password is not null))
        {
            throw new InvalidDataException("アーカイブを読み取れません。格納階層のパスワード、破損、圧縮方式を確認してください。");
        }
        finally { owned?.Dispose(); Array.Clear(passwords); }
    }

    private ManagedArchiveManifest ReadLogical(Stream input, string name, string? password, CancellationToken token,
        ArchiveReadBudget budget, bool inputAlreadyDecoded, OwnedEntryCapture? capture, ManagedArchiveReadOptions readOptions)
    {
        input.Position = 0;
        if (readOptions.GZipPayloadKind != GZipPayloadKind.Auto)
        {
            using var gzipWork = new WorkReadStream(input, budget, token);
            if (!IsGZip(gzipWork)) throw new InvalidDataException("gzip本文の形式はgzip入力にだけ指定できます。");
            return ReadGZip(gzipWork, name, readOptions, token, null, null, capture?.MaximumBytes,
                capture?.PrefixOnly == true, budget, capture);
        }
        if (readOptions.CompressionPayloadKind != CompressionPayloadKind.Auto)
        {
            using var compressionWork = new WorkReadStream(input, budget, token);
            if (DetectSingleCompression(compressionWork) is not { } compression)
                throw new InvalidDataException("BZip2/Z本文の形式はBZip2/Z入力にだけ指定できます。");
            return ReadCompression(compressionWork, name, compression, readOptions.CompressionPayloadKind, token,
                null, null, capture?.MaximumBytes, capture?.PrefixOnly == true, budget, capture);
        }
        if (TryGetWrapperChain(name, out var terminalLength, out var terminalType, out var depth))
            return ReadWrapped(input, name, terminalLength, terminalType, depth, password, token,
                null, null, capture?.MaximumBytes, capture?.PrefixOnly == true, budget, capture);
        using var work = new WorkReadStream(input, budget, token);
        return ReadCore(work, name, password, token, null, null, capture?.MaximumBytes, capture?.PrefixOnly == true,
            budget, ownedCapture: capture, inputAlreadyDecoded: inputAlreadyDecoded, readOptions: readOptions);
    }

    private string InputHash(Stream input, ArchiveReadBudget budget, CancellationToken token)
    {
        if (input.Length > _limits.MaximumInputBytes)
            throw new InvalidDataException("アーカイブ入力のサイズ上限を超えました。");
        if (input.Length > budget.WorkRemaining)
            throw new InvalidDataException("入力SHA照合の作業量上限を超えました。");
        input.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = input.Read(buffer);
            if (count == 0) break;
            budget.Work(count);
            hash.AppendData(buffer, 0, count);
        }
        if (input.Length > _limits.MaximumInputBytes)
            throw new InvalidDataException("アーカイブ入力のサイズ上限を超えました。");
        input.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class OwnedEntryCapture(string path, int maximumBytes, bool prefixOnly = false) : IDisposable
    {
        private MemoryStream? _content;
        private bool _directory;
        public int MaximumBytes { get; } = maximumBytes;
        public bool PrefixOnly { get; } = prefixOnly;
        public bool Wants(ManagedArchiveEntry entry)
        {
            if (entry.Path != path) return false;
            _directory = entry.IsDirectory;
            if (_directory) return false;
            if (!PrefixOnly && entry.Size > MaximumBytes) throw new InvalidDataException("内包ファイルの保持サイズ上限を超えました。");
            return true;
        }
        public bool Take(ManagedArchiveEntry entry, MemoryStream? content)
        {
            if (entry.Path != path || entry.IsDirectory || content is null) return false;
            if (_content is not null) throw new InvalidDataException("選択した格納ファイルが重複しています。");
            _content = content;
            return true;
        }
        public MemoryStream Detach()
        {
            if (_directory) throw new InvalidDataException("選択した格納項目はファイルではありません。");
            var result = _content ?? throw new FileNotFoundException("選択したアーカイブ内ファイルがありません。");
            _content = null;
            result.Position = 0;
            return result;
        }
        public void Dispose() { _content?.Dispose(); _content = null; }
    }

    private sealed class BoundedCaptureStream(int maximumBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > maximumBytes - Length) throw new InvalidDataException("内包ファイルの保持サイズ上限を超えました。");
            var needed = checked((int)Length + buffer.Length);
            if (needed > Capacity) Capacity = (int)Math.Min(maximumBytes, Math.Max(needed, (long)Capacity * 2));
            var start = checked((int)Length);
            // 派生MemoryStreamのSpan Writeからbase.Writeを呼ぶとbyte[] overrideへ再入する。
            base.SetLength(needed);
            buffer.CopyTo(GetBuffer().AsSpan(start, buffer.Length));
            Position = needed;
        }
    }
}
