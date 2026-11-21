using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class FolderComparisons
{
    internal static bool IsDirectory(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try { return Directory.Exists(DirectoryComparer.NormalizeRootPath(path)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    internal static Task<DirectoryComparisonResult> CompareAsync(string left, string right,
        DirectoryComparisonOptions options, CancellationToken token)
        => Task.Run(() => DirectoryComparer.CompareAsync(left, right, ValidateTypes(options), token), token);

    internal static Task<DirectoryComparisonResult> CompareAsync(string left, string middle, string right,
        DirectoryComparisonOptions options, CancellationToken token)
        => Task.Run(() => DirectoryComparer.CompareThreeWayAsync(left, middle, right, ValidateTypes(options), token), token);

    private static DirectoryComparisonOptions ValidateTypes(DirectoryComparisonOptions options)
        => options with
        {
            PropertyService = options.FileFilter?.UsesProperties == true ? WindowsFileFilterProperties.Instance : options.PropertyService,
            EntryTypeValidator = static (path, kind) =>
            {
                if (OperatingSystem.IsMacOS()) FolderCopyMacFileKind.Require(path, kind);
            }
        };
}
