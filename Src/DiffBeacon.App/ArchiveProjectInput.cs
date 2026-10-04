using System.Text.Json.Serialization;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

/// <summary>物理rootと格納名。JSONにpasswordや本文を含めず、保存した作業本文は明示的な外部assetで参照する。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveProjectInput
{
    public string RootPath { get; set; } = "";
    public string[] EntryChain { get; set; } = [];
    public string? LeafEntry { get; set; }
    public string? RootSha256 { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? MissingEntryChain { get; set; }
    // 原本の固定readonlyとは別に、最初に開いた側の編集指定を保持する。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? InheritedReadOnly { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ArchiveTextSnapshot[]? WorkingTexts { get; set; }

    internal ArchiveProjectInput Copy() => this with
    {
        EntryChain = EntryChain.Select(CanonicalEntry).ToArray(), LeafEntry = LeafEntry is null ? null : CanonicalEntry(LeafEntry),
        MissingEntryChain = MissingEntryChain?.Select(CanonicalEntry).ToArray(), WorkingTexts = WorkingTexts?.Select(copy => copy.Copy()).ToArray()
    };
    internal static string CanonicalEntry(string value) => value.Replace('\\', '/').TrimEnd('/');
    internal ArchiveSource ToSource() => new(RootPath, EntryChain, RootSha256);

    internal void Validate(bool container, bool readOnly)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || EntryChain is null || EntryChain.Length > 8 ||
            Uri.TryCreate(RootPath, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            throw new InvalidDataException("内包入力の物理rootと格納階層が不正です。");
        // 相対rootを保存可能にし、格納名はruntimeのimmutable型と同じ規則で検証する。
        _ = new ArchiveSource(Path.GetFullPath(RootPath), EntryChain, RootSha256);
        if (MissingEntryChain is { } missing)
        {
            if (missing.Length == 0 || missing.Length > 9 || LeafEntry is not null ||
                EntryChain.Length + missing.Length - (container ? 0 : 1) > 8)
                throw new InvalidDataException("不在入力の格納階層またはleaf指定が不正です。");
            _ = new ArchiveSource(Path.GetFullPath(RootPath), missing);
        }
        else if (container ? LeafEntry is not null : string.IsNullOrWhiteSpace(LeafEntry))
            throw new InvalidDataException("内包入力のcontainer／leaf指定が比較形式と一致しません。");
        if (LeafEntry is not null) _ = new ArchiveSource(Path.GetFullPath(RootPath), [LeafEntry]);
        if (RootSha256 is null) throw new InvalidDataException("保存する内包入力には確定rootのSHA-256が必要です。");
        if (!readOnly) throw new InvalidDataException("内包入力は読取り専用で開いてください。");
        if (WorkingTexts is { } copies)
        {
            if (MissingEntryChain is not null || copies.Length is < 1 or > ArchiveTextWorkingStore.MaximumDocuments
                || copies.Sum(copy => (long)(copy?.Bytes?.Length ?? 0)) > ArchiveTextWorkingStore.MaximumBytes)
                throw new InvalidDataException("作業文書の件数または容量が不正です。");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var copy in copies)
            {
                if (copy is null) throw new InvalidDataException("作業文書がnullです。");
                copy.Validate(RootPath);
                if (!keys.Add(string.Concat(copy.EntryChain.Append(copy.LeafEntry).Select(CanonicalEntry).Select(part => part.Length + ":" + part)))
                    || (container ? !copy.EntryChain.Take(EntryChain.Length).SequenceEqual(EntryChain)
                        : !copy.EntryChain.SequenceEqual(EntryChain) || copy.LeafEntry != LeafEntry))
                    throw new InvalidDataException("作業文書が入力の格納階層と一致しないか、重複しています。");
            }
        }
    }
}

internal static class ProjectInputs
{
    internal static ArchiveProjectInput? Archive(ComparisonProject project, int side) => side switch
    {
        0 => project.LeftArchiveInput, 1 => project.BaseArchiveInput, 2 => project.RightArchiveInput,
        _ => throw new ArgumentOutOfRangeException(nameof(side))
    };
    internal static string PathFor(ComparisonProject project, int side) => Archive(project, side)?.RootPath ?? side switch
    {
        0 => project.LeftPath, 1 => project.BasePath, 2 => project.RightPath,
        _ => throw new ArgumentOutOfRangeException(nameof(side))
    };
    internal static IEnumerable<string> PhysicalPaths(ComparisonProject project)
        => Enumerable.Range(0, 3).Select(side => PathFor(project, side)).Append(project.FileFilterPath ?? "").Concat(project.ProtectedArchiveAssets)
            .Concat(Enumerable.Range(0, 3).SelectMany(side => Archive(project, side)?.WorkingTexts ?? [])
                .Select(copy => copy.SnapshotPath ?? ""));
    internal static bool HasArchives(ComparisonProject project)
        => project.LeftArchiveInput is not null || project.BaseArchiveInput is not null || project.RightArchiveInput is not null;
    internal static bool HasMissing(ComparisonProject project)
        => Enumerable.Range(0, 3).Any(side => Archive(project, side)?.MissingEntryChain is not null);
    internal static bool HasBase(ComparisonProject project) => !string.IsNullOrWhiteSpace(PathFor(project, 1));
    internal static string Caption(ComparisonProject project, int side)
    {
        var input = Archive(project, side);
        return input is null ? PathFor(project, side) : string.Join(" / ",
            new[] { System.IO.Path.GetFileName(input.RootPath) }.Concat(input.EntryChain)
                .Concat(input.MissingEntryChain ?? (input.LeafEntry is null ? [] : new[] { input.LeafEntry })))
            + (input.MissingEntryChain is null ? "" : "（存在しない）");
    }

    internal static void EnsureOutput(string output, IEnumerable<ComparisonProject> projects, string? sourceProject = null)
    {
        foreach (var path in projects.SelectMany(PhysicalPaths).Append(sourceProject))
        {
            if (string.IsNullOrWhiteSpace(path) || Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") continue;
            var savedRelative = Path.IsPathFullyQualified(path) ? path
                : System.IO.Path.GetFullPath(path, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(output))!);
            if (ArchivePaths.SameFile(path, output) || ArchivePaths.SameFile(savedRelative, output))
                throw new InvalidOperationException("比較入力・アーカイブ原本・フィルター・プロジェクトを上書きできません。");
        }
    }
}
