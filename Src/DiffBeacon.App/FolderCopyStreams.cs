using System.Security.Cryptography;

namespace DiffBeacon.App;

public static partial class FolderOperations
{
    private static FolderCopyStreamLayout ReadPlannedStreamLayout(string path, long mainSize, CopyBudget budget, CancellationToken token)
    {
        var layout = ReadFileStreamLayout(path, mainSize, budget.RemainingStreamDescriptors, budget.RemainingStreamNameCharacters, token);
        budget.ReserveStreamLayout(layout);
        return layout;
    }

    private static FolderCopyStreamLayout ReadFileStreamLayout(string path, long mainSize, int maximumStreams,
        int maximumNameCharacters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RejectLinks(path);
        if (maximumStreams < 1 || maximumNameCharacters < FolderCopyWindowsStreams.DefaultStreamName.Length)
            throw new IOException("ストリーム件数または名前文字数の共有予算を超えました。");
        try
        {
            return OperatingSystem.IsWindows()
                ? FolderCopyWindowsStreams.EnumerateFile(path, mainSize, maximumStreams, maximumNameCharacters, token)
                : new(false, new[] { new FolderCopyStreamDefinition(FolderCopyWindowsStreams.DefaultStreamName, mainSize) });
        }
        catch (Exception exception) when (NativeCloseFailure(exception) is not null)
        {
            // Prepareの外側も通常はMessageだけを表示するため、close診断を本文にも伝える。
            throw WithStreamCloseDiagnostic(exception);
        }
    }

    private static long StreamLayoutBytes(FolderCopyStreamLayout layout)
        => layout.Streams.Aggregate(0L, (total, stream) => checked(total + stream.Size));

    private static bool ReadDestinationCapability(string destination, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return false;
        for (var parent = Path.GetDirectoryName(destination); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            token.ThrowIfCancellationRequested();
            var snapshot = ReadSnapshot(parent);
            if (snapshot is null) continue;
            if (snapshot.Kind != DiffBeacon.Core.DirectoryEntryKind.Directory)
                throw new IOException("コピー先の祖先がディレクトリではありません。");
            bool supported;
            try { supported = FolderCopyWindowsStreams.SupportsNamedStreams(parent, token); }
            catch (Exception exception) when (NativeCloseFailure(exception) is not null)
            { throw WithStreamCloseDiagnostic(exception); }
            RequireSnapshot(parent, snapshot);
            return supported;
        }
        throw new DirectoryNotFoundException("コピー先の既存親を確認できません。");
    }

    private static void RequireDestinationCapability(FolderCopyEntry entry, CancellationToken token)
    {
        if (ReadDestinationCapability(entry.DestinationPath, token) != entry.DestinationSupportsNamedStreams
            || (entry.SourceStreams!.HasNamedStreams && !entry.DestinationSupportsNamedStreams))
            throw new IOException("確認後にコピー先のストリーム対応状態が変わりました。");
    }

    private static string FileStreamPath(string basePath, string name)
        => OperatingSystem.IsWindows() ? FolderCopyWindowsStreams.PathForStream(basePath, name) : basePath;

    private static async Task<FolderCopyStreamSet> CaptureFileStreamsAsync(string path, FolderCopySnapshot snapshot,
        FolderCopyStreamLayout layout, CopyBudget budget, CancellationToken token)
    {
        RequireSnapshot(path, snapshot);
        var streams = new List<FolderCopyStreamSnapshot>(layout.Streams.Count);
        foreach (var stream in layout.Streams)
        {
            token.ThrowIfCancellationRequested();
            var hash = await HashStreamAsync(path, stream.Name, stream.Size, budget, token).ConfigureAwait(false);
            streams.Add(new(stream.Name, stream.Size, hash));
        }
        var result = new FolderCopyStreamSet(layout.SupportsNamedStreams, streams);
        RequireStreamLayout(path, snapshot.Size, result, result.SupportsNamedStreams, token);
        RequireSnapshot(path, snapshot);
        return result;
    }

    private static void RequireStreamLayout(string path, long mainSize, FolderCopyStreamSet expected,
        bool expectedSupport, CancellationToken token)
    {
        // 同じdescriptorを二重計上しない。新規一件・名前変更を検出できる有界列挙。
        var actual = ReadFileStreamLayout(path, mainSize, Math.Min(200_000, expected.Streams.Count + 1),
            Math.Min(4_000_000, checked(expected.NameCharacters + 296)), token);
        if (actual.SupportsNamedStreams != expectedSupport || actual.Streams.Count != expected.Streams.Count)
            throw new IOException($"確認後にストリーム集合または対応状態が変わりました: {path}");
        var names = actual.Streams.ToDictionary(stream => stream.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var stream in expected.Streams)
            if (!names.TryGetValue(stream.Name, out var found) || found.Size != stream.Size)
                throw new IOException($"確認後にストリーム名または長さが変わりました: {path}");
    }

    private static async Task RequireFileStreamsAsync(string path, FolderCopySnapshot snapshot, FolderCopyStreamSet expected,
        bool expectedSupport, CopyBudget budget, CancellationToken token)
    {
        RequireSnapshot(path, snapshot);
        RequireStreamLayout(path, snapshot.Size, expected, expectedSupport, token);
        foreach (var stream in expected.Streams)
        {
            token.ThrowIfCancellationRequested();
            var hash = await HashStreamAsync(path, stream.Name, stream.Size, budget, token).ConfigureAwait(false);
            if (!hash.Equals(stream.Sha256, StringComparison.Ordinal))
                throw new IOException($"確認後にストリーム内容が変わりました: {path} ({stream.Name})");
        }
        RequireStreamLayout(path, snapshot.Size, expected, expectedSupport, token);
        RequireSnapshot(path, snapshot);
    }

    private static async Task RequireDestinationStreamsAsync(FolderCopyEntry entry, CopyBudget budget, CancellationToken token)
    {
        RequireSnapshot(entry.DestinationPath, entry.Destination);
        if (entry.Destination is { } destination)
            await RequireFileStreamsAsync(entry.DestinationPath, destination, entry.DestinationStreams!,
                entry.DestinationStreams!.SupportsNamedStreams, budget, token).ConfigureAwait(false);
    }

    private static async Task<string> HashStreamAsync(string basePath, string name, long expectedLength,
        CopyBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RejectLinks(basePath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // 0byteでも実際にopenする。列挙成功から読込み成功を推定しない。
        await using var input = new FileStream(FileStreamPath(basePath, name), FileMode.Open, FileAccess.Read, FileShare.Read,
            StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != expectedLength) throw new IOException("SHA読込み前にストリーム長が変わりました。");
        var buffer = new byte[StreamBufferSize];
        long length = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            budget.AddRead(count);
            length = checked(length + count);
            if (length > expectedLength) throw new IOException("SHA読込み中にストリーム長が変わりました。");
            hash.AppendData(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        if (length != expectedLength || input.Length != expectedLength)
            throw new IOException("SHA読込み中にストリーム長が変わりました。");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task CopyStreamsToTemporaryAsync(FolderCopyEntry entry, string temporary, string parent,
        CopyBudget budget, Dictionary<string, FolderCopySnapshot?> state, CopyMutation item,
        CancellationToken token, Action? validateContext)
    {
        foreach (var stream in entry.SourceStreams!.Streams)
        {
            Guard(token, validateContext);
            RequireSourceSnapshot(entry.SourcePath, entry.Source);
            RejectLinks(temporary);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = new FileStream(FileStreamPath(entry.SourcePath, stream.Name), FileMode.Open,
                FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length != stream.Size) throw new IOException("コピー前にストリーム長が変わりました。");
            await using var output = new FileStream(FileStreamPath(temporary, stream.Name), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Name == FolderCopyWindowsStreams.DefaultStreamName)
            {
                // defaultのCreateNew成功後だけ所有tempとする。
                item.TemporaryPath = temporary;
                item.Mutated = true;
                RefreshDestinationState(parent, state);
            }
            var buffer = new byte[StreamBufferSize];
            long length = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                budget.AddRead(count);
                Guard(token, validateContext);
                length = checked(length + count);
                if (length > stream.Size) throw new IOException("コピー中にストリーム長が変わりました。");
                hash.AppendData(buffer, 0, count);
                budget.AddWrite(count);
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            await output.FlushAsync(token).ConfigureAwait(false);
            Guard(token, validateContext);
            if (length != stream.Size || input.Length != stream.Size
                || !Convert.ToHexString(hash.GetHashAndReset()).Equals(stream.Sha256, StringComparison.Ordinal))
                throw new IOException("コピー中にストリーム内容が変わりました。");
        }
        RequireStreamLayout(entry.SourcePath, entry.Size, entry.SourceStreams, entry.SourceStreams.SupportsNamedStreams, token);
        RequireSourceSnapshot(entry.SourcePath, entry.Source);
    }

    private static string? NativeCloseFailure(Exception exception)
        => exception.Data[FolderCopyWindowsStreams.CloseFailureDataKey] is Exception closeFailure ? closeFailure.Message : null;

    private static Exception WithStreamCloseDiagnostic(Exception primary)
    {
        var message = primary.Message + Environment.NewLine + NativeCloseFailure(primary);
        Exception diagnostic = primary is OperationCanceledException cancelled
            ? new OperationCanceledException(message, primary, cancelled.CancellationToken)
            : new IOException(message, primary);
        diagnostic.Data[FolderCopyWindowsStreams.CloseFailureDataKey] = primary.Data[FolderCopyWindowsStreams.CloseFailureDataKey];
        return diagnostic;
    }

    private static string? JoinCleanupFailures(string? first, string? second)
        => first is null ? second : second is null ? first : first + Environment.NewLine + second;
}
