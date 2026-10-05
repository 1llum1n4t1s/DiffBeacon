using DiffBeacon.Core;

namespace DiffBeacon.App;

public sealed record FolderCopyLimits
{
    public int MaximumEntries { get; init; } = 100_000;
    public int MaximumDepth { get; init; } = 256;
    public int MaximumStreamDescriptors { get; init; } = 200_000;
    public int MaximumStreamNameCharacters { get; init; } = 4_000_000;
    public long MaximumLogicalBytes { get; init; } = 1L << 40;
    public long MaximumIoBytes { get; init; } = 5L << 40;
}

public sealed record FolderCopySnapshot(DirectoryEntryKind Kind, long Size,
    DateTime LastWriteTimeUtc, FileAttributes Attributes, UnixFileMode? UnixMode);

public sealed record FolderCopyStreamSnapshot(string Name, long Size, string Sha256);

public sealed class FolderCopyStreamSet
{
    internal FolderCopyStreamSet(bool supportsNamedStreams, IEnumerable<FolderCopyStreamSnapshot> streams)
    {
        SupportsNamedStreams = supportsNamedStreams;
        // default を先頭へ固定し、OS の列挙順に依存しない。呼出し元配列は保持しない。
        Streams = Array.AsReadOnly(streams.OrderBy(stream => stream.Name == FolderCopyWindowsStreams.DefaultStreamName ? 0 : 1)
            .ThenBy(stream => stream.Name, StringComparer.OrdinalIgnoreCase).ToArray());
        TotalBytes = Streams.Aggregate(0L, (total, stream) => checked(total + stream.Size));
        NameCharacters = Streams.Aggregate(0, (total, stream) => checked(total + stream.Name.Length));
    }

    public bool SupportsNamedStreams { get; }
    public IReadOnlyList<FolderCopyStreamSnapshot> Streams { get; }
    public long TotalBytes { get; }
    public int NameCharacters { get; }
    public bool HasNamedStreams => Streams.Count > 1;
}

public sealed class FolderCopyEntry
{
    internal FolderCopyEntry(string relativePath, string sourcePath, string destinationPath,
        FolderCopySnapshot source, FolderCopySnapshot? destination, FolderCopyStreamSet? sourceStreams,
        FolderCopyStreamSet? destinationStreams, bool destinationSupportsNamedStreams, IEnumerable<string> children)
    {
        RelativePath = relativePath;
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        Source = source;
        Destination = destination;
        SourceStreams = sourceStreams;
        DestinationStreams = destinationStreams;
        DestinationSupportsNamedStreams = destinationSupportsNamedStreams;
        Children = Array.AsReadOnly(children.ToArray());
    }

    public string RelativePath { get; }
    public string SourcePath { get; }
    public string DestinationPath { get; }
    public DirectoryEntryKind Kind => Source.Kind;
    public long Size => Source.Size;
    public DateTime LastWriteTimeUtc => Source.LastWriteTimeUtc;
    public FileAttributes Attributes => Source.Attributes;
    public UnixFileMode? UnixMode => Source.UnixMode;
    public FolderCopySnapshot Source { get; }
    public FolderCopySnapshot? Destination { get; }
    public FolderCopyStreamSet? SourceStreams { get; }
    public FolderCopyStreamSet? DestinationStreams { get; }
    public bool DestinationSupportsNamedStreams { get; }
    public string? Sha256 => SourceStreams?.Streams[0].Sha256;
    public IReadOnlyList<string> Children { get; }
}

public sealed class FolderCopyPlan
{
    internal FolderCopyPlan(string sourceRoot, string destinationRoot, DateTime preparedUtc,
        FolderCopyLimits limits, IEnumerable<FolderCopyEntry> entries,
        IEnumerable<KeyValuePair<string, FolderCopySnapshot?>> destinationParents,
        long logicalBytes, long destinationBytes, long plannedIoBytes, int streamDescriptors,
        int streamNameCharacters, long preparationReadBytes)
    {
        SourceRoot = sourceRoot;
        DestinationRoot = destinationRoot;
        PreparedUtc = preparedUtc;
        Limits = limits with { };
        Entries = Array.AsReadOnly(entries.ToArray());
        DestinationParents = Array.AsReadOnly(destinationParents.ToArray());
        LogicalBytes = logicalBytes;
        DestinationBytes = destinationBytes;
        PlannedIoBytes = plannedIoBytes;
        StreamDescriptors = streamDescriptors;
        StreamNameCharacters = streamNameCharacters;
        PreparationReadBytes = preparationReadBytes;
    }

    public string SourceRoot { get; }
    public string DestinationRoot { get; }
    public DateTime PreparedUtc { get; }
    public FolderCopyLimits Limits { get; }
    public IReadOnlyList<FolderCopyEntry> Entries { get; }
    public long LogicalBytes { get; }
    public long DestinationBytes { get; }
    public long PlannedIoBytes { get; }
    public int StreamDescriptors { get; }
    public int StreamNameCharacters { get; }
    public long PreparationReadBytes { get; }
    internal IReadOnlyList<KeyValuePair<string, FolderCopySnapshot?>> DestinationParents { get; }
}

public enum FolderCopyEntryStatus { Published, Failed, Cancelled, NotExecuted }

public sealed record FolderCopyMetadataResult(FolderCopySnapshot Source, FolderCopySnapshot? Destination,
    FileAttributes ExplicitWindowsAttributes, FileAttributes SourceWindowsAttributesNotExplicitlySet);

public sealed record FolderCopyEntryResult(string RelativePath, string DestinationPath,
    FolderCopyEntryStatus Status, bool MutationOccurred, string? Reason = null, string? CleanupFailure = null,
    bool Published = false, FolderCopyMetadataResult? Metadata = null);

public sealed class FolderCopyResult
{
    internal FolderCopyResult(IEnumerable<FolderCopyEntryResult> entries, string? reason,
        bool cancelled, bool mutationOccurred, long readBytes, long writeBytes)
    {
        Entries = Array.AsReadOnly(entries.ToArray());
        Reason = reason;
        Cancelled = cancelled;
        MutationOccurred = mutationOccurred;
        ReadBytes = readBytes;
        WriteBytes = writeBytes;
    }

    public IReadOnlyList<FolderCopyEntryResult> Entries { get; }
    public string? Reason { get; }
    public bool Cancelled { get; }
    public bool MutationOccurred { get; }
    public bool Succeeded => Reason is null && !Cancelled;
    public int PublishedCount => Entries.Count(entry => entry.Published);
    public long ReadBytes { get; }
    public long WriteBytes { get; }
}
