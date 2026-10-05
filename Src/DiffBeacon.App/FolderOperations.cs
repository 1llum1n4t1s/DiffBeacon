using DiffBeacon.Core;

namespace DiffBeacon.App;

public static partial class FolderOperations
{
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static async Task CopyAsync(string sourceRoot, string destinationRoot, string relativePath, CancellationToken token = default)
    {
        var relative = NormalizeRelativePath(relativePath);
        var sourceBase = Path.GetFullPath(sourceRoot);
        var source = ResolveInside(sourceBase, relative);
        var snapshot = ReadSourceSnapshot(source) ?? throw new FileNotFoundException("コピー元がありません。", source);
        var side = new DirectorySideSnapshot(source, snapshot.Kind, snapshot.Size,
            snapshot.LastWriteTimeUtc, snapshot.Attributes, false, DirectoryScanState.Unscanned);
        var selection = new DirectoryCopySelection(sourceBase, Path.GetFullPath(destinationRoot),
            DirectoryCopyDirection.LeftToRight, DirectoryCopyMode.All, Array.AsReadOnly(new[] { relative }),
            Array.AsReadOnly(new[] { new DirectoryCopyCandidate(relative, side, null, snapshot.Kind == DirectoryEntryKind.Directory) }));
        var plan = await PrepareAsync(selection, cancellationToken: token).ConfigureAwait(false);
        var result = await ExecuteAsync(plan, cancellationToken: token).ConfigureAwait(false);
        if (result.Cancelled) throw new OperationCanceledException(result.Reason, token);
        if (!result.Succeeded) throw new IOException(result.Reason ?? "フォルダーコピーに失敗しました。");
    }

    private static bool Inside(string path, string root)
    {
        var prefix = Path.TrimEndingDirectorySeparator(root);
        if (!Path.EndsInDirectorySeparator(prefix)) prefix += Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static void RejectLinks(string path)
    {
        // 既存の親ディレクトリも確認し、リンクを介したルート外への書込みを防ぐ。
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException($"リンクはコピー対象にできません: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

}
