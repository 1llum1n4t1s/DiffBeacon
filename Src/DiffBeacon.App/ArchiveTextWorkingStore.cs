using System.Security.Cryptography;
using System.Text.Json.Serialization;
using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveWorkingSnapshot
{
    public string[] EntryChain { get; set; } = [];
    public string LeafEntry { get; set; } = "";
    [JsonIgnore]
    internal byte[]? Bytes { get; set; }
    public string? SnapshotPath { get; set; }
    public string Sha256 { get; set; } = "";
    public string EncodingName { get; set; } = "";
    public bool HasBom { get; set; }
    // v4のTextはkindを省略する。Binaryを含むprojectはv5で明示する。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; }
    internal bool IsBinary => Kind == "Binary";
    internal int MaximumFileBytes => IsBinary ? BinaryEditSession.MaximumFileBytes : checked((int)new TextLoadOptions().MaxFileSize);
    internal string AssetExtension => IsBinary ? ".bin" : ".text";

    internal ArchiveWorkingSnapshot Copy() => this with { EntryChain = EntryChain.Select(ArchiveProjectInput.CanonicalEntry).ToArray(),
        LeafEntry = ArchiveProjectInput.CanonicalEntry(LeafEntry) };
    internal TextDocument Document() => !IsBinary ? TextDocument.FromSavedSnapshot(Bytes ?? throw new InvalidDataException("作業文書のsnapshotを読み込んでください。"), EncodingName, HasBom)
        : throw new InvalidDataException("Binary作業版をTextとして復号できません。Binary形式で開き直してください。");
    internal void Validate(string root)
    {
        if (EntryChain is null || EntryChain.Length > 8 || string.IsNullOrWhiteSpace(LeafEntry)
            || Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit)
            || Kind is not (null or "Binary")
            || (IsBinary ? EncodingName != "" || HasBom : EncodingName is not ("utf-8" or "utf-16" or "utf-16BE" or "utf-32" or "utf-32BE" or "windows-1252") || EncodingName == "windows-1252" && HasBom)
            || Bytes is null && string.IsNullOrWhiteSpace(SnapshotPath))
            throw new InvalidDataException("作業文書の格納階層が不正です。");
        _ = new ArchiveSource(Path.GetFullPath(root), EntryChain);
        _ = new ArchiveSource(Path.GetFullPath(root), [LeafEntry]);
        if (Bytes is null) return;
        if (Bytes.Length > MaximumFileBytes) throw new InvalidDataException("作業版が形式別の保持上限を超えています。");
        if (!StringComparer.Ordinal.Equals(Sha256, Convert.ToHexString(SHA256.HashData(Bytes))))
            throw new InvalidDataException("作業文書のSHA-256が一致しません。");
        if (!IsBinary) _ = Document();
    }
}

/// <summary>ウィンドウ寿命で保存点だけを保持する。原本／editor本文／作業版は別の所有物。</summary>
internal sealed class ArchiveWorkingStore
{
    internal const long MaximumBytes = 128L * 1024 * 1024;
    internal const int MaximumDocuments = 256;
    private readonly Dictionary<string, (ArchiveProjectInput Origin, ArchiveWorkingSnapshot Snapshot, long Revision)> _saved = new(StringComparer.Ordinal);
    private long _bytes, _revision;
    internal long Generation => _revision;
    internal void Clear() { _saved.Clear(); _bytes = 0; _revision++; }

    private static string Key(ArchiveProjectInput input, string[] chain, string leaf)
    {
        var root = Path.GetFullPath(input.RootPath);
        if (OperatingSystem.IsWindows()) root = root.ToUpperInvariant();
        return string.Concat(new[] { root, input.RootSha256?.ToUpperInvariant() ?? "" }.Concat(chain.Select(ArchiveProjectInput.CanonicalEntry)).Append(ArchiveProjectInput.CanonicalEntry(leaf))
            .Select(part => part.Length + ":" + part));
    }
    internal long Revision(ArchiveProjectInput input) => input.LeafEntry is null ? 0
        : _saved.GetValueOrDefault(Key(input, input.EntryChain, input.LeafEntry)).Revision;
    internal ArchiveWorkingSnapshot? Find(ArchiveSource source, string leaf)
    {
        var input = new ArchiveProjectInput { RootPath = source.RootPath, RootSha256 = source.RootSha256 };
        return _saved.GetValueOrDefault(Key(input, source.EntryChain.ToArray(), leaf)).Snapshot;
    }
    internal ManagedArchiveManifest Overlay(ManagedArchiveManifest manifest, ArchiveSource source)
    {
        var input = Capture(new() { RootPath = source.RootPath, RootSha256 = source.RootSha256, EntryChain = source.EntryChain.ToArray() });
        return manifest with { Entries = manifest.Entries.Select(entry =>
        {
            if (entry.IsDirectory) return entry;
            if (Find(source, entry.Path) is { } saved) return entry with { Size = saved.Bytes!.Length, Sha256 = saved.Sha256 };
            var descendants = (input.WorkingDocuments ?? []).Where(copy => copy.EntryChain.Length > source.EntryChain.Count
                && copy.EntryChain[source.EntryChain.Count] == entry.Path).OrderBy(copy => string.Join('/', copy.EntryChain.Append(copy.LeafEntry)), StringComparer.Ordinal).ToArray();
            if (descendants.Length == 0) return entry;
            var fingerprint = string.Concat(descendants.Select(copy => string.Concat(copy.EntryChain.Append(copy.LeafEntry).Append(copy.Sha256).Select(part => part.Length + ":" + part))));
            return entry with { Sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entry.Sha256 + fingerprint))) };
        }).ToArray() };
    }
    internal ArchiveProjectInput Capture(ArchiveProjectInput input)
    {
        var copies = _saved.Values.Where(value => Key(value.Origin, [], "") == Key(input, [], "")
            && (input.LeafEntry is null ? value.Snapshot.EntryChain.Take(input.EntryChain.Length).SequenceEqual(input.EntryChain)
                : value.Snapshot.LeafEntry == input.LeafEntry && value.Snapshot.EntryChain.SequenceEqual(input.EntryChain)))
            .Select(value => value.Snapshot.Copy()).ToArray();
        return input.Copy() with { WorkingDocuments = copies.Length == 0 ? null : copies };
    }
    internal void Import(ArchiveProjectInput input) => Import([input]);
    internal void Import(IEnumerable<ArchiveProjectInput> inputs)
    {
        var pending = new Dictionary<string, (ArchiveProjectInput Origin, ArchiveWorkingSnapshot Snapshot)>(StringComparer.Ordinal);
        long additionalBytes = 0;
        foreach (var input in inputs)
        foreach (var copy in input.WorkingDocuments ?? [])
        {
            copy.Validate(input.RootPath);
            var key = Key(input, copy.EntryChain, copy.LeafEntry);
            var previous = _saved.TryGetValue(key, out var saved) ? saved.Snapshot : pending.GetValueOrDefault(key).Snapshot;
            if (previous is not null)
            {
                if (previous.Sha256 != copy.Sha256 || previous.Kind != copy.Kind || previous.EncodingName != copy.EncodingName || previous.HasBom != copy.HasBom)
                    throw new InvalidDataException("同じ格納項目に異なる作業保存版が指定されています。");
                continue;
            }
            if (copy.Bytes is null) throw new InvalidDataException("作業文書のsnapshotを読み込んでください。");
            additionalBytes += copy.Bytes.Length;
            if (_saved.Count + pending.Count + 1 > MaximumDocuments || _bytes + additionalBytes > MaximumBytes)
                throw new InvalidDataException("作業文書は256件、保存済み本文の合計128 MiBまでです。");
            pending.Add(key, (input.Copy() with { WorkingDocuments = null }, copy.Copy()));
        }
        // すべての型・SHA・衝突・容量を検証してから、共有storeへ一括採用する。
        foreach (var (key, value) in pending) _saved.Add(key, (value.Origin, value.Snapshot, ++_revision));
        _bytes += additionalBytes;
    }
    private void Capacity(int length, bool exists, int previous)
    {
        if (!exists && _saved.Count >= MaximumDocuments || _bytes - previous + length > MaximumBytes)
            throw new InvalidDataException("作業文書は256件、保存済み本文の合計128 MiBまでです。");
    }
    internal void EnsureCurrent(ArchiveProjectInput input, long expected)
    {
        if (Revision(input) != expected)
            throw new InvalidOperationException("別のタブで同じ内包文書が保存されています。本文を退避して比較し直してください。");
    }
    internal void Save(ArchiveProjectInput input, long expected, ArchiveWorkingSnapshot snapshot)
    {
        EnsureCurrent(input, expected); snapshot.Validate(input.RootPath);
        if (snapshot.Bytes is null) throw new InvalidDataException("作業文書のsnapshotを読み込んでください。");
        var key = Key(input, snapshot.EntryChain, snapshot.LeafEntry);
        var exists = _saved.TryGetValue(key, out var old);
        Capacity(snapshot.Bytes.Length, exists, old.Snapshot?.Bytes?.Length ?? 0);
        _bytes += snapshot.Bytes.Length - (old.Snapshot?.Bytes?.Length ?? 0);
        _saved[key] = (input.Copy() with { WorkingDocuments = null }, snapshot.Copy(), ++_revision);
    }
}
