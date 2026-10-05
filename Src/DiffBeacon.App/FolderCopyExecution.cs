using DiffBeacon.Core;

namespace DiffBeacon.App;

public static partial class FolderOperations
{
    public static Task<FolderCopyResult> ExecuteAsync(FolderCopyPlan plan,
        Action<string>? validateDestination = null, Action? validateContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // callback の thread は保証しない。UI 側は必要な dispatcher 照合を実施する。
        return Task.Run(() => ExecuteCoreAsync(plan, validateDestination, validateContext, cancellationToken));
    }

    private static async Task<FolderCopyResult> ExecuteCoreAsync(FolderCopyPlan plan,
        Action<string>? destinationGuard, Action? validateContext, CancellationToken token)
    {
        // 既存root上の祖先は検査だけ行い、出力callbackへ渡さない。新規作成が必要な親は渡す。
        Action<string>? validateDestination = destinationGuard is null ? null : path =>
        {
            if (SameOrInside(path, plan.DestinationRoot) || !Directory.Exists(path)) destinationGuard(path);
        };
        var budget = new CopyBudget(plan.Limits, plan.PreparationReadBytes);
        var results = new List<FolderCopyEntryResult>();
        var mutation = new CopyMutation();
        var sourceState = plan.Entries.ToDictionary(entry => entry.SourcePath, entry => entry, PathComparer);
        var destinationState = plan.DestinationParents.ToDictionary(pair => pair.Key, pair => pair.Value, PathComparer);
        var expectedKinds = plan.DestinationParents.ToDictionary(pair => pair.Key, _ => DirectoryEntryKind.Directory, PathComparer);
        var destinationOwners = new Dictionary<string, FolderCopyEntry>(PathComparer);
        foreach (var entry in plan.Entries)
        {
            destinationState[entry.DestinationPath] = entry.Destination;
            expectedKinds[entry.DestinationPath] = entry.Kind;
            destinationOwners[entry.DestinationPath] = entry;
            for (var parent = Path.GetDirectoryName(entry.DestinationPath); parent is not null; parent = Path.GetDirectoryName(parent))
            {
                if (!destinationState.ContainsKey(parent)) continue;
                if (!destinationOwners.TryAdd(parent, entry)) break;
            }
        }
        FolderCopyEntry? preflightEntry = null;
        try
        {
            Guard(token, validateContext);
            ValidateLimits(plan.Limits);
            if (plan.PlannedIoBytes > plan.Limits.MaximumIoBytes || plan.PreparationReadBytes != checked(plan.LogicalBytes + plan.DestinationBytes))
                throw new IOException("コピー計画の共有予算が一致しません。");
            // 一件も書き込む前に、全元 SHA と directory membership、および全出力状態を検査する。
            foreach (var entry in plan.Entries)
            {
                preflightEntry = entry;
                Guard(token, validateContext);
                validateDestination?.Invoke(entry.DestinationPath);
                await RequireSourceAsync(entry, budget, token).ConfigureAwait(false);
            }
            foreach (var target in destinationState)
            {
                preflightEntry = destinationOwners.GetValueOrDefault(target.Key);
                Guard(token, validateContext);
                validateDestination?.Invoke(target.Key);
                RequireSnapshot(target.Key, target.Value);
                ValidateDestinationKind(target.Key, target.Value, expectedKinds[target.Key],
                    SameOrInside(target.Key, plan.DestinationRoot) || target.Value is null);
                if (preflightEntry is { Kind: DirectoryEntryKind.File } owner && PathComparer.Equals(target.Key, owner.DestinationPath))
                {
                    await RequireDestinationStreamsAsync(owner, budget, token).ConfigureAwait(false);
                    RequireDestinationCapability(owner, token);
                }
            }
            foreach (var entry in plan.Entries)
            {
                preflightEntry = entry;
                Guard(token, validateContext);
                RequireSourceSnapshot(entry.SourcePath, entry.Source);
                if (entry.Kind == DirectoryEntryKind.Directory) RequireMembership(entry.SourcePath, entry.Children, token);
            }
        }
        catch (Exception exception) when (IsCopyFailure(exception))
        {
            var cancelled = exception is OperationCanceledException;
            for (var index = 0; index < plan.Entries.Count; index++)
            {
                var entry = plan.Entries[index];
                results.Add(new(entry.RelativePath, entry.DestinationPath,
                    ReferenceEquals(entry, preflightEntry) || (preflightEntry is null && index == 0)
                        ? (cancelled ? FolderCopyEntryStatus.Cancelled : FolderCopyEntryStatus.Failed) : FolderCopyEntryStatus.NotExecuted,
                    false, exception.Message, NativeCloseFailure(exception)));
            }
            return new(results, exception.Message, cancelled, false, budget.ReadBytes, budget.WriteBytes);
        }

        foreach (var entry in plan.Entries)
        {
            var item = new CopyMutation();
            string? failure = null;
            string? cleanupFailure = null;
            var cancelled = false;
            try
            {
                Guard(token, validateContext);
                RequireSourcePathMetadata(entry, sourceState, token, validateContext);
                ValidateDestinationChain(entry.DestinationPath, plan.DestinationRoot, destinationState, token, validateDestination, validateContext);
                if (entry.Kind == DirectoryEntryKind.Directory)
                {
                    EnsureDirectory(entry.DestinationPath, plan.DestinationRoot, destinationState, item, token, validateDestination, validateContext);
                    if (entry.Children.Count == 0)
                    {
                        Guard(token, validateContext);
                        ValidateDestinationChain(entry.DestinationPath, plan.DestinationRoot, destinationState, token, validateDestination, validateContext);
                        RequireSourceSnapshot(entry.SourcePath, entry.Source);
                        RequireMembership(entry.SourcePath, entry.Children, token);
                        // 独立 Shell API reference に合わせ、empty root と empty leaf の mtime を保持する。
                        Directory.SetLastWriteTimeUtc(entry.DestinationPath, entry.LastWriteTimeUtc);
                        item.Mutated = true;
                        RefreshDestinationState(entry.DestinationPath, destinationState);
                    }
                    item.Published = true;
                }
                else
                {
                    await CopyFileAsync(entry, plan.DestinationRoot, budget, sourceState, destinationState, item,
                        token, validateDestination, validateContext).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (IsCopyFailure(exception))
            {
                failure = exception.Message;
                cleanupFailure = JoinCleanupFailures(cleanupFailure, NativeCloseFailure(exception));
                cancelled = exception is OperationCanceledException;
            }
            finally
            {
                if (item.TemporaryPath is { } temporary)
                {
                    try
                    {
                        // 製品が作った未公開 temp の寿命を既存 executor と同様に閉じる。
                        RejectLinks(temporary);
                        if (File.Exists(temporary))
                        {
                            if (OperatingSystem.IsWindows()) File.SetAttributes(temporary, FileAttributes.Normal);
                            File.Delete(temporary);
                        }
                    }
                    catch (Exception exception) when (IsCopyFailure(exception))
                    { cleanupFailure = JoinCleanupFailures(cleanupFailure, exception.Message); }
                }
            }
            mutation.Mutated |= item.Mutated;
            failure ??= cleanupFailure;
            FolderCopySnapshot? actualDestination = null;
            if (item.Published)
            {
                try
                {
                    actualDestination = ReadSnapshot(entry.DestinationPath);
                    if (actualDestination is null || actualDestination.Kind != entry.Kind)
                        throw new IOException("公開したコピー先を再確認できません。");
                    if (entry.Kind == DirectoryEntryKind.File
                        && (actualDestination.Size != entry.Size || actualDestination.LastWriteTimeUtc != entry.LastWriteTimeUtc
                            || actualDestination.UnixMode != entry.UnixMode))
                        throw new IOException("公開したファイルの metadata を保持できませんでした。");
                    if (OperatingSystem.IsWindows() && entry.Kind == DirectoryEntryKind.File
                        && (actualDestination.Attributes & (PortableWindowsFileAttributes & ~FileAttributes.Normal))
                            != (entry.Attributes & (PortableWindowsFileAttributes & ~FileAttributes.Normal)))
                        throw new IOException("公開したファイルの明示 Windows 属性を保持できませんでした。");
                    if (entry.Kind == DirectoryEntryKind.File)
                        RequireStreamLayout(entry.DestinationPath, entry.Size, entry.SourceStreams!, entry.DestinationSupportsNamedStreams, token);
                    if (entry.Kind == DirectoryEntryKind.Directory && entry.Children.Count == 0
                        && actualDestination.LastWriteTimeUtc != entry.LastWriteTimeUtc)
                        throw new IOException("空ディレクトリの更新日時を保持できませんでした。");
                }
                catch (Exception exception) when (IsCopyFailure(exception))
                {
                    failure ??= exception.Message;
                    cancelled |= exception is OperationCanceledException;
                    cleanupFailure = JoinCleanupFailures(cleanupFailure, NativeCloseFailure(exception));
                }
            }
            var explicitAttributes = OperatingSystem.IsWindows() && entry.Kind == DirectoryEntryKind.File
                ? PortableWindowsFileAttributes : (FileAttributes)0;
            var metadata = new FolderCopyMetadataResult(entry.Source, actualDestination, explicitAttributes,
                OperatingSystem.IsWindows() ? entry.Attributes & ~explicitAttributes : (FileAttributes)0);
            results.Add(new(entry.RelativePath, entry.DestinationPath,
                failure is null ? FolderCopyEntryStatus.Published : cancelled ? FolderCopyEntryStatus.Cancelled : FolderCopyEntryStatus.Failed,
                item.Mutated, failure, cleanupFailure, item.Published, metadata));
            if (failure is not null)
            {
                foreach (var remaining in plan.Entries.Skip(results.Count))
                    results.Add(new(remaining.RelativePath, remaining.DestinationPath, FolderCopyEntryStatus.NotExecuted, false, "前の項目で停止しました。"));
                return new(results, failure, cancelled, mutation.Mutated, budget.ReadBytes, budget.WriteBytes);
            }
        }
        return new(results, null, false, mutation.Mutated, budget.ReadBytes, budget.WriteBytes);
    }

    private static void Guard(CancellationToken token, Action? validateContext)
    {
        token.ThrowIfCancellationRequested();
        validateContext?.Invoke();
        token.ThrowIfCancellationRequested();
    }

    private static bool IsCopyFailure(Exception exception) => exception is not OutOfMemoryException
        and not StackOverflowException and not AccessViolationException;

    private static async Task RequireSourceAsync(FolderCopyEntry entry, CopyBudget budget, CancellationToken token)
    {
        RequireSourceSnapshot(entry.SourcePath, entry.Source);
        if (entry.Kind == DirectoryEntryKind.Directory) RequireMembership(entry.SourcePath, entry.Children, token);
        else await RequireFileStreamsAsync(entry.SourcePath, entry.Source, entry.SourceStreams!,
            entry.SourceStreams!.SupportsNamedStreams, budget, token).ConfigureAwait(false);
        RequireSourceSnapshot(entry.SourcePath, entry.Source);
    }

    private static void RequireSourcePathMetadata(FolderCopyEntry entry, Dictionary<string, FolderCopyEntry> state,
        CancellationToken token, Action? validateContext)
    {
        // 毎項目の全manifest再走査は二次処理量になるため、全体事前照合後は対象と元祖先を検査する。
        for (var current = entry.SourcePath; current is not null; current = Path.GetDirectoryName(current))
        {
            Guard(token, validateContext);
            if (state.TryGetValue(current, out var captured)) RequireSourceSnapshot(current, captured.Source);
            else RejectLinks(current);
        }
        if (entry.Kind == DirectoryEntryKind.Directory) RequireMembership(entry.SourcePath, entry.Children, token);
    }

    private static void ValidateDestinationChain(string path, string destinationRoot, Dictionary<string, FolderCopySnapshot?> state,
        CancellationToken token, Action<string>? validateDestination, Action? validateContext)
    {
        var childMissing = false;
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            Guard(token, validateContext);
            validateDestination?.Invoke(current);
            var actual = ReadSnapshot(current);
            if (state.TryGetValue(current, out var expected) && actual != expected)
                throw new IOException($"確認後にコピー先の状態が変わっています: {current}");
            ValidateDestinationKind(current, actual,
                current.Equals(path, PathComparison) ? actual?.Kind ?? DirectoryEntryKind.Directory : DirectoryEntryKind.Directory,
                SameOrInside(current, destinationRoot) || childMissing || actual is null);
            childMissing = actual is null;
        }
    }

    private static void EnsureDirectory(string path, string destinationRoot, Dictionary<string, FolderCopySnapshot?> state, CopyMutation item,
        CancellationToken token, Action<string>? validateDestination, Action? validateContext)
    {
        var missing = new Stack<string>();
        for (var current = path; current is not null && ReadSnapshot(current) is null; current = Path.GetDirectoryName(current))
            missing.Push(current);
        while (missing.TryPop(out var directory))
        {
            Guard(token, validateContext);
            ValidateDestinationChain(directory, destinationRoot, state, token, validateDestination, validateContext);
            Directory.CreateDirectory(directory);
            item.Mutated = true;
            RefreshDestinationState(directory, state);
        }
        ValidateDestinationKind(path, ReadSnapshot(path), DirectoryEntryKind.Directory);
    }

    private static void RefreshDestinationState(string path, Dictionary<string, FolderCopySnapshot?> state)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (state.ContainsKey(current)) state[current] = ReadSnapshot(current);
    }

    private static async Task CopyFileAsync(FolderCopyEntry entry, string destinationRoot, CopyBudget budget,
        Dictionary<string, FolderCopyEntry> sourceState, Dictionary<string, FolderCopySnapshot?> state, CopyMutation item, CancellationToken token,
        Action<string>? validateDestination, Action? validateContext)
    {
        var parent = Path.GetDirectoryName(entry.DestinationPath)!;
        EnsureDirectory(parent, destinationRoot, state, item, token, validateDestination, validateContext);
        Guard(token, validateContext);
        ValidateDestinationChain(entry.DestinationPath, destinationRoot, state, token, validateDestination, validateContext);
        RequireSourceSnapshot(entry.SourcePath, entry.Source);
        var temporary = Path.Combine(parent, ".diffbeacon-copy-" + Guid.NewGuid().ToString("N") + ".tmp");
        validateDestination?.Invoke(temporary);
        Guard(token, validateContext);
        ValidateDestinationChain(entry.DestinationPath, destinationRoot, state, token, validateDestination, validateContext);
        RequireSourceSnapshot(entry.SourcePath, entry.Source);
        RejectLinks(temporary);
        RequireDestinationCapability(entry, token);
        await CopyStreamsToTemporaryAsync(entry, temporary, parent, budget, state, item, token, validateContext).ConfigureAwait(false);
        // temp 全本文を独立して読み戻し、すべてのhandleを閉じてから公開する。
        var temporarySnapshot = ReadSnapshot(temporary) ?? throw new IOException("所有tempを確認できません。");
        await RequireFileStreamsAsync(temporary, temporarySnapshot, entry.SourceStreams!,
            entry.DestinationSupportsNamedStreams, budget, token).ConfigureAwait(false);
        RequireSourcePathMetadata(entry, sourceState, token, validateContext);
        File.SetLastWriteTimeUtc(temporary, entry.LastWriteTimeUtc);
        if (!OperatingSystem.IsWindows() && entry.UnixMode is { } mode) File.SetUnixFileMode(temporary, mode);
        if (OperatingSystem.IsWindows())
            File.SetAttributes(temporary, entry.Attributes & (PortableWindowsFileAttributes & ~FileAttributes.ReadOnly));
        Guard(token, validateContext);
        ValidateDestinationChain(entry.DestinationPath, destinationRoot, state, token, validateDestination, validateContext);
        await RequireSourceAsync(entry, budget, token).ConfigureAwait(false);
        await RequireDestinationStreamsAsync(entry, budget, token).ConfigureAwait(false);
        RequireDestinationCapability(entry, token);
        Guard(token, validateContext);
        RequireSourcePathMetadata(entry, sourceState, token, validateContext);
        ValidateDestinationChain(entry.DestinationPath, destinationRoot, state, token, validateDestination, validateContext);
        RequireStreamLayout(temporary, entry.Size, entry.SourceStreams!, entry.DestinationSupportsNamedStreams, token);
        RejectLinks(temporary);
        File.Move(temporary, entry.DestinationPath, overwrite: true);
        item.Published = true;
        item.Mutated = true;
        item.TemporaryPath = null;
        RefreshDestinationState(entry.DestinationPath, state);
        // Windows の readonly は Move 後に設定する。失敗しても Published を失わない。
        if (OperatingSystem.IsWindows() && (entry.Attributes & FileAttributes.ReadOnly) != 0)
        {
            Guard(token, validateContext);
            ValidateDestinationChain(entry.DestinationPath, destinationRoot, state, token, validateDestination, validateContext);
            File.SetAttributes(entry.DestinationPath, File.GetAttributes(entry.DestinationPath) | FileAttributes.ReadOnly);
        }
        RefreshDestinationState(entry.DestinationPath, state);
    }

    private sealed class CopyMutation
    {
        public bool Mutated { get; set; }
        public bool Published { get; set; }
        public string? TemporaryPath { get; set; }
    }
}
