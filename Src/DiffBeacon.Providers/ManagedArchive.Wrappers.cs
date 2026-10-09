using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;

namespace DiffBeacon.Providers;

public sealed partial class ManagedArchive
{
    /// <summary>読込み可能な明示名。出力形式と wrapper 入力の対応を分ける。</summary>
    public static bool SupportsInput(string path) => SupportsOutput(path) ||
        path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) || IsSingleCompressionName(path) ||
        path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) || TryGetWrapperChain(path, out _, out _, out _);

    // FNAME や内包エントリを使わず、呼び出し元が指定した名前だけで鎖を確定する。
    private static bool TryGetWrapperChain(string path, out int terminalLength, out ArchiveType terminalType, out int depth)
    {
        terminalLength = path.Length;
        depth = 0;
        while (WrapperSuffix(path.AsSpan(0, terminalLength)) is { } suffix)
        {
            terminalLength -= suffix.Length;
            depth++;
        }
        var terminal = path.AsSpan(0, terminalLength);
        var aliasWrapper = TarAliasWrapper(terminal);
        if (aliasWrapper is not null)
        {
            terminalType = ArchiveType.Tar;
            // 単層aliasは従来のTAR経路を維持し、外層があるときだけ共通鎖へ渡す。
            if (depth == 0) return false;
            depth++;
            return true;
        }
        terminalType = terminal.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ? ArchiveType.SevenZip :
            terminal.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) ? ArchiveType.Rar :
            terminal.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? ArchiveType.Tar : ArchiveType.Zip;
        // 単層TAR圧縮は既存の逐次読込み・サイズ境界を維持する。
        if (terminalType == ArchiveType.Tar) return depth > 1;
        return depth > 0 && (terminalType != ArchiveType.Zip ||
            terminal.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || terminal.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            terminal.EndsWith(".ear", StringComparison.OrdinalIgnoreCase) || terminal.EndsWith(".war", StringComparison.OrdinalIgnoreCase) ||
            terminal.EndsWith(".xpi", StringComparison.OrdinalIgnoreCase));
    }

    private static string? TarAliasWrapper(ReadOnlySpan<char> name) =>
        name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? ".gz" :
        name.EndsWith(".tbz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tbz2", StringComparison.OrdinalIgnoreCase) ? ".bz2" :
        name.EndsWith(".taz", StringComparison.OrdinalIgnoreCase) ? ".Z" : null;

    private static string? WrapperSuffix(ReadOnlySpan<char> name) =>
        name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? ".gz" :
        name.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase) ? ".bz2" :
        name.EndsWith(".Z", StringComparison.OrdinalIgnoreCase) ? ".Z" : null;

    private ManagedArchiveManifest ReadWrapped(Stream physicalInput, string logicalName, int terminalLength,
        ArchiveType terminalType, int depth, string? password, CancellationToken token,
        Func<ManagedArchiveEntry, bool>? capture, Action<ManagedArchiveEntry, MemoryStream?>? consume,
        long? captureLimit, bool prefixOnly, ArchiveReadBudget? sharedBudget = null, OwnedEntryCapture? ownedCapture = null)
    {
        var budget = sharedBudget ?? new ArchiveReadBudget(_limits);
        using var decoded = DecodeWrappers(physicalInput, logicalName, depth, token, budget, out var suffixes);
        using var terminalInput = new WorkReadStream(decoded, budget, token);
        var manifest = ReadCore(terminalInput, logicalName[..terminalLength], password, token,
            capture, consume, captureLimit, prefixOnly, budget, terminalType, ownedCapture, inputAlreadyDecoded: true);
        return manifest with { Format = manifest.Format + string.Concat(suffixes) };
    }

    // managed一覧・typed Source・TAR metadataの各consumerで同じ全層検証と予算を使う。
    private MemoryStream DecodeWrappers(Stream physicalInput, string logicalName, int depth,
        CancellationToken token, ArchiveReadBudget budget, out string[] suffixes)
    {
        if (depth > _limits.MaximumWrapperDepth)
            throw new InvalidDataException("アーカイブ wrapper の深度上限を超えました。");
        budget.Layers(depth);
        Stream current = physicalInput;
        MemoryStream? owned = null;
        var nameLength = logicalName.Length;
        suffixes = new string[depth];
        var buffer = new byte[64 * 1024];
        try
        {
            for (var layer = 0; layer < depth; layer++)
            {
                token.ThrowIfCancellationRequested();
                budget.Item(); // 次段の確保・decoder 構築より前に共有残量を確認する。
                var suffix = WrapperSuffix(logicalName.AsSpan(0, nameLength));
                var alias = suffix is null;
                suffix ??= TarAliasWrapper(logicalName.AsSpan(0, nameLength))!;
                suffixes[depth - layer - 1] = suffix;
                if (!alias) nameLength -= suffix.Length;
                budget.PathCharacters(suffix.Length);
                using var workInput = new WorkReadStream(current, budget, token);
                VerifyWrapperMagic(workInput, suffix);
                using var decoder = suffix switch
                {
                    ".gz" => (Stream)new VerifiedGZipStream(workInput, token, _limits.MaximumEntries, budget),
                    ".bz2" => new VerifiedBZip2Stream(workInput, token, budget),
                    _ => new ZReadStream(workInput, token, leaveOpen: true)
                };
                var next = new MemoryStream();
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var remaining = Math.Min(_limits.MaximumEntryBytes - next.Length, budget.DecodedRemaining);
                        // 残量 0 でも次の 1 byte を読み、正規 EOF と超過を区別する。
                        var count = decoder.Read(buffer, 0, remaining < buffer.Length ? checked((int)remaining + 1) : buffer.Length);
                        if (count == 0) break;
                        if (count > _limits.MaximumEntryBytes - next.Length)
                            throw new InvalidDataException("wrapper 中間内容のサイズ上限を超えました。");
                        budget.Decoded(count);
                        var needed = checked((int)next.Length + count);
                        if (needed > next.Capacity)
                            next.Capacity = (int)Math.Min(_limits.MaximumEntryBytes, Math.Max(needed, (long)next.Capacity * 2));
                        next.Write(buffer, 0, count);
                    }
                    if (next.Length == 0) throw new InvalidDataException("wrapper の内容が空です。");
                    if (workInput.Position != workInput.Length)
                        throw new InvalidDataException("wrapper の後続内容を完全に検証できませんでした。");
                    token.ThrowIfCancellationRequested();
                    decoder.Dispose();
                    owned?.Dispose();
                    next.Position = 0;
                    owned = next;
                    current = next;
                }
                catch { next.Dispose(); throw; }
            }
            return owned!;
        }
        catch { owned?.Dispose(); throw; }
    }

    private static void VerifyWrapperMagic(Stream input, string suffix)
    {
        Span<byte> bytes = stackalloc byte[4];
        var count = input.ReadAtLeast(bytes, suffix == ".bz2" ? 4 : 3, throwOnEndOfStream: false);
        input.Position = 0;
        var valid = suffix switch
        {
            ".gz" => count >= 3 && bytes[0] == 0x1f && bytes[1] == 0x8b && bytes[2] == 8,
            ".bz2" => count == 4 && bytes[..3].SequenceEqual("BZh"u8) && bytes[3] is >= (byte)'1' and <= (byte)'9',
            _ => count >= 3 && bytes[0] == 0x1f && bytes[1] == 0x9d
        };
        if (!valid) throw new InvalidDataException("明示した wrapper 形式とヘッダーが一致しません。");
    }

    internal sealed class ArchiveReadBudget(ManagedArchiveLimits limits)
    {
        public long DecodedRemaining { get; private set; } = limits.MaximumDecodedBytes;
        private long _work = limits.MaximumWorkBytes;
        private long _items = limits.MaximumEntries;
        private long _characters = limits.MaximumPathCharacters;
        private long _layers = limits.MaximumWrapperDepth;
        private long _headers = (long)limits.MaximumEntries * 3;
        public long WorkRemaining => _work;
        public void Work(long amount) => Charge(ref _work, amount, "アーカイブ作業量の上限を超えました。");
        public void Layers(long amount) => Charge(ref _layers, amount, "格納階層とwrapperの共有深度上限を超えました。");
        public void TarHeader() => Charge(ref _headers, 1, "TARヘッダーの共有件数上限を超えました。");
        public void Item() => Charge(ref _items, 1, "wrapper・メンバー・格納パスの共有件数上限を超えました。");
        public void PathCharacters(long amount)
        {
            Charge(ref _characters, amount, "アーカイブの共有文字数上限を超えました。");
            Work(checked(amount * 2)); // 名前のUTF-16保持/処理bytes。
        }
        public void CheckPath(string? path)
        {
            if (path is not null && (long)path.Length * 2 > _characters)
                throw new InvalidDataException("アーカイブの共有文字数上限を超えました。");
        }
        public void Decoded(long amount)
        {
            var remaining = DecodedRemaining;
            Charge(ref remaining, amount, "アーカイブの共有復号サイズ上限を超えました。");
            Work(amount);
            DecodedRemaining = remaining;
        }
        private static void Charge(ref long remaining, long amount, string message)
        {
            if (amount < 0 || amount > remaining) throw new InvalidDataException(message);
            remaining -= amount;
        }
    }

    // 所有権を移さず、seek 後に再読込みした bytes も含めた実 I/O を計上する。
    private sealed class WorkReadStream(Stream inner, ArchiveReadBudget budget, CancellationToken token) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            var count = inner.Read(buffer);
            budget.Work(count);
            return count;
        }
        public override int ReadByte()
        {
            token.ThrowIfCancellationRequested();
            var value = inner.ReadByte();
            if (value >= 0) budget.Work(1);
            return value;
        }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => Seek(value, SeekOrigin.Begin); }
        public override long Seek(long offset, SeekOrigin origin)
        {
            token.ThrowIfCancellationRequested();
            return inner.Seek(offset, origin);
        }
        public override void Flush() => token.ThrowIfCancellationRequested();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // 公開 decoder の concat 終端判定に依存せず、全次ヘッダーを検査する。
    private sealed class VerifiedBZip2Stream(Stream input, CancellationToken token, ArchiveReadBudget budget) : Stream
    {
        private Stream? _member;
        private bool _end;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (buffer.IsEmpty || _end) return 0;
            Span<byte> header = stackalloc byte[4];
            while (true)
            {
                if (_member is null)
                {
                    if (input.Position == input.Length) { _end = true; return 0; }
                    budget.Item();
                    var start = input.Position;
                    if (input.ReadAtLeast(header, 4, throwOnEndOfStream: false) != 4 ||
                        !header[..3].SequenceEqual("BZh"u8) || header[3] is < (byte)'1' or > (byte)'9')
                        throw new InvalidDataException("bzip2 メンバーのヘッダーまたは後続内容が不正です。");
                    budget.Work(4);
                    input.Position = start;
                    _member = BZip2Stream.Create(new CheckedStream(input, input.Length, token),
                        SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: false, leaveOpen: true);
                }
                var read = _member.Read(buffer);
                if (read != 0) return read;
                _member.Dispose();
                _member = null;
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) _member?.Dispose(); base.Dispose(disposing); }
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
