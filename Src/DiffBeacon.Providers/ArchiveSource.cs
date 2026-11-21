namespace DiffBeacon.Providers;

/// <summary>物理入力と格納階層を表す。認証情報と復号した内容は保持しない。</summary>
public sealed class ArchiveSource
{
    public string RootPath { get; }
    public IReadOnlyList<string> EntryChain { get; }
    public string? RootSha256 { get; }
    public IReadOnlyList<int> ContainerNameCodePages { get; }
    public IReadOnlyList<GZipPayloadKind> ContainerGZipPayloadKinds { get; }
    public IReadOnlyList<CompressionPayloadKind> ContainerCompressionPayloadKinds { get; }

    public ArchiveSource(string rootPath, IReadOnlyList<string>? entryChain = null, string? rootSha256 = null,
        IReadOnlyList<int>? containerNameCodePages = null,
        IReadOnlyList<GZipPayloadKind>? containerGZipPayloadKinds = null,
        IReadOnlyList<CompressionPayloadKind>? containerCompressionPayloadKinds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
            throw new ArgumentException("アーカイブの物理入力には絶対パスを指定してください。", nameof(rootPath));
        RootPath = Path.GetFullPath(rootPath);
        var count = entryChain?.Count ?? 0;
        if (count < 0 || count > 4096) throw new ArgumentOutOfRangeException(nameof(entryChain));
        var entries = new string[count];
        for (var index = 0; index < count; index++)
            entries[index] = ManagedArchive.ValidateEntryPath(entryChain![index]);
        EntryChain = Array.AsReadOnly(entries);
        if (containerNameCodePages is not null && containerNameCodePages.Count != count + 1)
            throw new ArgumentException("格納名の文字コードはrootと各コンテナーに一つずつ指定してください。", nameof(containerNameCodePages));
        var codePages = new int[count + 1];
        for (var index = 0; index < codePages.Length; index++)
        {
            codePages[index] = containerNameCodePages?[index] ?? 28591;
            _ = new ManagedArchiveReadOptions(codePages[index]).NameEncoding();
        }
        ContainerNameCodePages = Array.AsReadOnly(codePages);
        if (containerGZipPayloadKinds is not null && containerGZipPayloadKinds.Count != count + 1)
            throw new ArgumentException("gzip本文の形式はrootと各コンテナーに一つずつ指定してください。", nameof(containerGZipPayloadKinds));
        var payloadKinds = new GZipPayloadKind[count + 1];
        for (var index = 0; index < payloadKinds.Length; index++)
        {
            payloadKinds[index] = containerGZipPayloadKinds?[index] ?? GZipPayloadKind.Auto;
            if (!Enum.IsDefined(payloadKinds[index]))
                throw new ArgumentOutOfRangeException(nameof(containerGZipPayloadKinds), "gzip本文の形式が不正です。");
        }
        ContainerGZipPayloadKinds = Array.AsReadOnly(payloadKinds);
        if (containerCompressionPayloadKinds is not null && containerCompressionPayloadKinds.Count != count + 1)
            throw new ArgumentException("BZip2/Z本文の形式はrootと各コンテナーに一つずつ指定してください。", nameof(containerCompressionPayloadKinds));
        var compressionKinds = new CompressionPayloadKind[count + 1];
        for (var index = 0; index < compressionKinds.Length; index++)
        {
            compressionKinds[index] = containerCompressionPayloadKinds?[index] ?? CompressionPayloadKind.Auto;
            _ = new ManagedArchiveReadOptions(codePages[index], payloadKinds[index], compressionKinds[index]);
        }
        ContainerCompressionPayloadKinds = Array.AsReadOnly(compressionKinds);
        if (rootSha256 is not null && (rootSha256.Length != 64 ||
            rootSha256.Any(character => !char.IsAsciiHexDigit(character))))
            throw new ArgumentException("入力のSHA-256が不正です。", nameof(rootSha256));
        RootSha256 = rootSha256?.ToUpperInvariant();
    }

    public ArchiveSource WithChild(string entryPath)
    {
        var entries = new string[checked(EntryChain.Count + 1)];
        for (var index = 0; index < EntryChain.Count; index++) entries[index] = EntryChain[index];
        entries[^1] = ManagedArchive.ValidateEntryPath(entryPath);
        return new(RootPath, entries, RootSha256, ContainerNameCodePages.Append(28591).ToArray(),
            ContainerGZipPayloadKinds.Append(GZipPayloadKind.Auto).ToArray(),
            ContainerCompressionPayloadKinds.Append(CompressionPayloadKind.Auto).ToArray());
    }

    public ArchiveSource WithNameCodePage(int containerIndex, int codePage)
    {
        if (containerIndex < 0 || containerIndex >= ContainerNameCodePages.Count)
            throw new ArgumentOutOfRangeException(nameof(containerIndex));
        var choices = ContainerNameCodePages.ToArray(); choices[containerIndex] = codePage;
        return new(RootPath, EntryChain, RootSha256, choices, ContainerGZipPayloadKinds, ContainerCompressionPayloadKinds);
    }
    public ArchiveSource WithGZipPayloadKind(int containerIndex, GZipPayloadKind payloadKind)
    {
        if (containerIndex < 0 || containerIndex >= ContainerGZipPayloadKinds.Count)
            throw new ArgumentOutOfRangeException(nameof(containerIndex));
        if (!Enum.IsDefined(payloadKind))
            throw new ArgumentOutOfRangeException(nameof(payloadKind), "gzip本文の形式が不正です。");
        var choices = ContainerGZipPayloadKinds.ToArray(); choices[containerIndex] = payloadKind;
        return new(RootPath, EntryChain, RootSha256, ContainerNameCodePages, choices, ContainerCompressionPayloadKinds);
    }
    public ArchiveSource WithCompressionPayloadKind(int containerIndex, CompressionPayloadKind payloadKind)
    {
        if (containerIndex < 0 || containerIndex >= ContainerCompressionPayloadKinds.Count)
            throw new ArgumentOutOfRangeException(nameof(containerIndex));
        if (!Enum.IsDefined(payloadKind))
            throw new ArgumentOutOfRangeException(nameof(payloadKind), "BZip2/Z本文の形式が不正です。");
        var choices = ContainerCompressionPayloadKinds.ToArray(); choices[containerIndex] = payloadKind;
        return new(RootPath, EntryChain, RootSha256, ContainerNameCodePages, ContainerGZipPayloadKinds, choices);
    }
}

public sealed record ManagedArchiveSourceManifest(ArchiveSource Source, ManagedArchiveManifest Manifest);
