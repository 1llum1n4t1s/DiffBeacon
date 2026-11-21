using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal enum ArchiveEntryOpenMode { Auto, Text, Binary, Archive, Image }

internal sealed record ArchiveOpenRequest(ArchiveSource LeftSource, ArchiveSource RightSource,
    ArchiveEntryDifference Row, ArchiveEntryOpenMode Mode, IReadOnlyList<string?> LeftPasswords,
    IReadOnlyList<string?> RightPasswords, int Generation, CancellationToken Token,
    IReadOnlyList<string>? LeftMissingEntryChain = null, IReadOnlyList<string>? RightMissingEntryChain = null);

internal static class ArchiveImageInputValidation
{
    internal static void Validate(ComparisonProject project)
    {
        if (!ProjectInputs.HasArchives(project) || project.Mode.ToLowerInvariant() is not ("image" or "4")) return;
        if (ProjectInputs.HasBase(project) || !project.LeftReadOnly || !project.RightReadOnly)
            throw new InvalidDataException("内包画像は中央入力なしの読取り専用比較で開いてください。");
        foreach (var side in new[] { 0, 2 })
            if (ProjectInputs.Archive(project, side) is { } input
                && (string.IsNullOrWhiteSpace(input.LeafEntry) || input.MissingEntryChain is not null
                    || input.WorkingDocuments is not null || input.InheritedReadOnly == false))
                throw new InvalidDataException("内包画像には実在するleafを指定し、作業版を含めず読取り専用で開いてください。");
    }
}
