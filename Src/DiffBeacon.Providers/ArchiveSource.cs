namespace DiffBeacon.Providers;

/// <summary>物理入力と格納階層を表す。認証情報と復号した内容は保持しない。</summary>
public sealed class ArchiveSource
{
    public string RootPath { get; }
    public IReadOnlyList<string> EntryChain { get; }
    public string? RootSha256 { get; }

    public ArchiveSource(string rootPath, IReadOnlyList<string>? entryChain = null, string? rootSha256 = null)
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
        return new(RootPath, entries, RootSha256);
    }
}

public sealed record ManagedArchiveSourceManifest(ArchiveSource Source, ManagedArchiveManifest Manifest);
