using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static class ProjectInputReader
{
    internal static async Task<TextDocument> ReadTextAsync(ComparisonProject project, int side,
        CancellationToken token, string? physicalSnapshot = null, IReadOnlyList<string?>? passwords = null)
    {
        var input = ProjectInputs.Archive(project, side)?.Copy();
        if (input is null)
            return await TextDocument.LoadAsync(ArchiveActions.ValidatePath(physicalSnapshot ?? ProjectInputs.PathFor(project, side)), token).ConfigureAwait(false);
        if (input.LeafEntry is null) throw new InvalidDataException("Text比較には内包ファイルを指定してください。");
        if (physicalSnapshot is not null) input.RootPath = physicalSnapshot;
        var options = new TextLoadOptions();
        var source = input.ToSource();
        var bytes = await Task.Run(() => new ManagedArchive().ResolveEntry(source, input.LeafEntry,
            checked((int)options.MaxFileSize), passwords, token), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return TextDocument.FromSnapshot(bytes, options);
    }

    internal static async Task<byte[]> ReadBytesAsync(ComparisonProject project, int side, int maximumBytes,
        CancellationToken token, IReadOnlyList<string?>? passwords = null)
    {
        var input = ProjectInputs.Archive(project, side)?.Copy();
        if (input is not null)
        {
            if (input.LeafEntry is null) throw new InvalidDataException("内包ファイルを指定してください。");
            return await Task.Run(() => new ManagedArchive().ResolveEntry(input.ToSource(), input.LeafEntry,
                maximumBytes, passwords, token), token).ConfigureAwait(false);
        }
        var path = ArchiveActions.ValidatePath(ProjectInputs.PathFor(project, side));
        if (new FileInfo(path).Length > maximumBytes) throw new InvalidDataException("内包比較の保持サイズ上限を超えました。");
        var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        if (bytes.Length > maximumBytes) throw new InvalidDataException("内包比較の保持サイズ上限を超えました。");
        return bytes;
    }

    internal static void ValidateSnapshot(ArchiveProjectInput input, string physicalSnapshot, CancellationToken token)
    {
        var captured = input.Copy(); captured.RootPath = physicalSnapshot;
        var result = new ManagedArchive().ResolveManifest(captured.ToSource(), cancellationToken: token);
        if (captured.LeafEntry is { } leaf && !result.Manifest.Entries.Any(entry => entry.Path == leaf && !entry.IsDirectory))
            throw new InvalidDataException("包装する内包ファイルが存在しないか、ディレクトリです。");
    }
}
