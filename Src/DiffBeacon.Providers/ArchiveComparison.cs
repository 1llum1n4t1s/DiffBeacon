using System.Text;

namespace DiffBeacon.Providers;

public sealed record ArchiveEntryDifference(string Path, string Status, ManagedArchiveEntry? Left, ManagedArchiveEntry? Right);

public static class ArchiveComparison
{
    public static IReadOnlyList<ArchiveEntryDifference> Compare(ManagedArchiveManifest left, ManagedArchiveManifest right)
    {
        var a = left.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var b = right.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        return a.Keys.Concat(b.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(path =>
        {
            var l = a.GetValueOrDefault(path); var r = b.GetValueOrDefault(path);
            var status = l is null ? "OnlyRight" : r is null ? "OnlyLeft" : l.IsDirectory != r.IsDirectory ? "TypeChanged"
                : l.Size == r.Size && l.Sha256 == r.Sha256 ? "Equal" : "Modified";
            return new ArchiveEntryDifference(path, status, l, r);
        }).ToArray();
    }

    public static string CanonicalText(ManagedArchiveManifest manifest)
    {
        var output = new StringBuilder();
        foreach (var entry in manifest.Entries)
            output.Append(entry.IsDirectory ? "D\t" : "F\t").Append(entry.Path.Replace("\t", "\\t", StringComparison.Ordinal))
                .Append('\t').Append(entry.Size).Append('\t').Append(entry.Sha256).Append('\n');
        return output.ToString();
    }
}

internal sealed class ArchiveComparisonProvider : IComparisonProvider
{
    public string Id => "archive";
    public IReadOnlyList<string> Formats { get; } = ["archive"];
    public async Task<ProviderResult> CompareAsync(ComparisonRequest request, CancellationToken cancellationToken)
    {
        if (request.Format != Id) throw new NotSupportedException("プロバイダーと比較形式が一致しません。");
        var left = await Task.Run(() => new ManagedArchive(readOptions: request.LeftArchiveReadOptions).ReadManifest(request.LeftPath, cancellationToken: cancellationToken), cancellationToken);
        var right = await Task.Run(() => new ManagedArchive(readOptions: request.RightArchiveReadOptions).ReadManifest(request.RightPath, cancellationToken: cancellationToken), cancellationToken);
        return new("アーカイブ: 格納名・型・サイズ・内容SHA-256を比較。パスワード付きの内容はアーカイブビューで比較してください。",
            ArchiveComparison.CanonicalText(left), ArchiveComparison.CanonicalText(right));
    }
}
