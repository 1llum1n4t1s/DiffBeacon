using System.Security.Cryptography;
using System.Text.Json.Serialization;
using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveTextSnapshot
{
    public string[] EntryChain { get; set; } = [];
    public string LeafEntry { get; set; } = "";
    [JsonIgnore]
    internal byte[]? Bytes { get; set; }
    public string? SnapshotPath { get; set; }
    public string Sha256 { get; set; } = "";
    public string EncodingName { get; set; } = "";
    public bool HasBom { get; set; }

    internal ArchiveTextSnapshot Copy() => this with { EntryChain = EntryChain.Select(ArchiveProjectInput.CanonicalEntry).ToArray(),
        LeafEntry = ArchiveProjectInput.CanonicalEntry(LeafEntry) };
    internal TextDocument Document() => TextDocument.FromSavedSnapshot(Bytes ?? throw new InvalidDataException("作業文書のsnapshotを読み込んでください。"), EncodingName, HasBom);
    internal void Validate(string root)
    {
        if (EntryChain is null || EntryChain.Length > 8 || string.IsNullOrWhiteSpace(LeafEntry)
            || Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit)
            || EncodingName is not ("utf-8" or "utf-16" or "utf-16BE" or "utf-32" or "utf-32BE" or "windows-1252")
            || EncodingName == "windows-1252" && HasBom || Bytes is null && string.IsNullOrWhiteSpace(SnapshotPath))
            throw new InvalidDataException("作業文書の格納階層が不正です。");
        _ = new ArchiveSource(Path.GetFullPath(root), EntryChain);
        _ = new ArchiveSource(Path.GetFullPath(root), [LeafEntry]);
        if (Bytes is null) return;
        if (!StringComparer.Ordinal.Equals(Sha256, Convert.ToHexString(SHA256.HashData(Bytes))))
            throw new InvalidDataException("作業文書のSHA-256が一致しません。");
        _ = Document();
    }
}

/// <summary>ウィンドウ寿命で保存点だけを保持する。原本／editor本文／作業版は別の所有物。</summary>
internal sealed class ArchiveTextWorkingStore
{
    internal const long MaximumBytes = 128L * 1024 * 1024;
    internal const int MaximumDocuments = 256;
    private readonly Dictionary<string, (ArchiveProjectInput Origin, ArchiveTextSnapshot Snapshot, long Revision)> _saved = new(StringComparer.Ordinal);
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
    internal ArchiveTextSnapshot? Find(ArchiveSource source, string leaf)
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
            var descendants = (input.WorkingTexts ?? []).Where(copy => copy.EntryChain.Length > source.EntryChain.Count
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
        return input.Copy() with { WorkingTexts = copies.Length == 0 ? null : copies };
    }
    internal void Import(ArchiveProjectInput input) => Import([input]);
    internal void Import(IEnumerable<ArchiveProjectInput> inputs)
    {
        var pending = new Dictionary<string, (ArchiveProjectInput Origin, ArchiveTextSnapshot Snapshot)>(StringComparer.Ordinal);
        long additionalBytes = 0;
        foreach (var input in inputs)
        foreach (var copy in input.WorkingTexts ?? [])
        {
            copy.Validate(input.RootPath);
            var key = Key(input, copy.EntryChain, copy.LeafEntry);
            var previous = _saved.TryGetValue(key, out var saved) ? saved.Snapshot : pending.GetValueOrDefault(key).Snapshot;
            if (previous is not null)
            {
                if (previous.Sha256 != copy.Sha256 || previous.EncodingName != copy.EncodingName || previous.HasBom != copy.HasBom)
                    throw new InvalidDataException("同じ格納項目に異なる作業保存版が指定されています。");
                continue;
            }
            if (copy.Bytes is null) throw new InvalidDataException("作業文書のsnapshotを読み込んでください。");
            additionalBytes += copy.Bytes.Length;
            if (_saved.Count + pending.Count + 1 > MaximumDocuments || _bytes + additionalBytes > MaximumBytes)
                throw new InvalidDataException("作業文書は256件、保存済み本文の合計128 MiBまでです。");
            pending.Add(key, (input.Copy() with { WorkingTexts = null }, copy.Copy()));
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
    internal void Save(ArchiveProjectInput input, long expected, ArchiveTextSnapshot snapshot)
    {
        EnsureCurrent(input, expected); snapshot.Validate(input.RootPath);
        if (snapshot.Bytes is null) throw new InvalidDataException("作業文書のsnapshotを読み込んでください。");
        var key = Key(input, snapshot.EntryChain, snapshot.LeafEntry);
        var exists = _saved.TryGetValue(key, out var old);
        Capacity(snapshot.Bytes.Length, exists, old.Snapshot?.Bytes?.Length ?? 0);
        _bytes += snapshot.Bytes.Length - (old.Snapshot?.Bytes?.Length ?? 0);
        _saved[key] = (input.Copy() with { WorkingTexts = null }, snapshot.Copy(), ++_revision);
    }
}
