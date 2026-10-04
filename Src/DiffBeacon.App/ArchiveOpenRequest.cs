using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal enum ArchiveEntryOpenMode { Auto, Text, Binary, Archive }

internal sealed record ArchiveOpenRequest(ArchiveSource LeftSource, ArchiveSource RightSource,
    ArchiveEntryDifference Row, ArchiveEntryOpenMode Mode, IReadOnlyList<string?> LeftPasswords,
    IReadOnlyList<string?> RightPasswords, int Generation, CancellationToken Token,
    IReadOnlyList<string>? LeftMissingEntryChain = null, IReadOnlyList<string>? RightMissingEntryChain = null);
