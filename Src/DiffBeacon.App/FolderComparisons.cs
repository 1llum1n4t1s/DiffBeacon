using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class FolderComparisons
{
    internal static Task<DirectoryComparisonResult> CompareAsync(string left, string right,
        DirectoryComparisonOptions options, CancellationToken token)
        => DirectoryComparer.CompareAsync(left, right, options with
        {
            EntryTypeValidator = static (path, kind) =>
            {
                if (OperatingSystem.IsMacOS()) FolderCopyMacFileKind.Require(path, kind);
            }
        }, token);
}
