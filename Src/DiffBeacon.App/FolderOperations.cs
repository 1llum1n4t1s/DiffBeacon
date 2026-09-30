namespace DiffBeacon.App;

public static class FolderOperations
{
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static async Task CopyAsync(string sourceRoot, string destinationRoot, string relativePath, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
            throw new ArgumentException("コピー対象はルート内の相対パスで指定してください。", nameof(relativePath));
        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Any(segment => segment is "" or "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')))
            throw new ArgumentException("相対パスに空要素や . / .. は使用できません。", nameof(relativePath));
        var sourceBase = Path.GetFullPath(sourceRoot);
        var destinationBase = Path.GetFullPath(destinationRoot);
        var normalized = Path.Combine(segments);
        var source = Path.GetFullPath(Path.Combine(sourceBase, normalized));
        var destination = Path.GetFullPath(Path.Combine(destinationBase, normalized));
        if (!Inside(source, sourceBase) || !Inside(destination, destinationBase)) throw new IOException("コピー対象が比較ルートの外です。");
        if (source.Equals(destination, PathComparison) || Inside(destination, source) || Inside(source, destination)) throw new IOException("コピー元とコピー先が重なる場所へはコピーできません。");
        RejectLinks(sourceBase); RejectLinks(destinationBase);
        if (!Directory.Exists(sourceBase)) throw new DirectoryNotFoundException(sourceBase);
        token.ThrowIfCancellationRequested();
        await CopyEntryAsync(source, destination, token);
    }

    private static bool Inside(string path, string root) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);

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

    private static async Task CopyEntryAsync(string source, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RejectLinks(source); RejectLinks(destination);
        var attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            if (File.Exists(destination)) throw new IOException("コピー先に同名のファイルがあります。");
            Directory.CreateDirectory(destination);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
                await CopyEntryAsync(entry, Path.Combine(destination, Path.GetFileName(entry)), token);
            return;
        }
        var directory = Path.GetDirectoryName(destination)!;
        if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException($"読み取り専用のコピー先は上書きできません: {destination}");
        Directory.CreateDirectory(directory);
        RejectLinks(directory);
        var temporaryPath = Path.Combine(directory, ".diffbeacon-copy-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                await input.CopyToAsync(output, token);
            token.ThrowIfCancellationRequested();
            RejectLinks(destination);
            if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException($"読み取り専用のコピー先は上書きできません: {destination}");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporaryPath, File.GetUnixFileMode(source));
            if (OperatingSystem.IsWindows()) File.SetAttributes(temporaryPath, attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed));
            File.Move(temporaryPath, destination, overwrite: true);
            if (OperatingSystem.IsWindows() && (attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}
