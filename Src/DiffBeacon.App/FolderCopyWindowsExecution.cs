using DiffBeacon.Core;

namespace DiffBeacon.App;

public static partial class FolderOperations
{
    private sealed class WindowsExecutionState
    {
        private sealed class Destination(FolderCopyWindowsMetadata metadata)
        {
            internal FolderCopyWindowsMetadata Metadata { get; } = metadata;
            internal ulong ExpectedWriteTime { get; set; } = metadata.LastWriteTime;
        }

        private readonly string _sourceRoot, _destinationRoot;
        private readonly Dictionary<string, FolderCopyWindowsMetadata> _source;
        private readonly Dictionary<string, Destination> _destination;
        private readonly CopyBudget _budget;

        internal WindowsExecutionState(FolderCopyPlan plan, CopyBudget budget)
        {
            // per-path map/stateのallowanceはPrepareで予約済み。raw4配列は同じimmutable参照を使う。
            budget.ReserveRetainedBytes(256);
            _sourceRoot = plan.SourceRoot;
            _destinationRoot = plan.DestinationRoot;
            _budget = budget;
            _source = plan.SourceWindowsMetadata.ToDictionary(pair => pair.Key, pair => pair.Value, PathComparer);
            _destination = plan.DestinationWindowsMetadata.ToDictionary(pair => pair.Key, pair => new Destination(pair.Value), PathComparer);
        }

        internal void RequireAll(CancellationToken token, Action? context)
        {
            Guard(token, context);
            RequirePreparedWindowsMetadata(_sourceRoot, _source, _budget, token);
            foreach (var pair in _destination) RequireDestinationPath(pair.Key, pair.Value, token, null);
            Guard(token, context);
        }

        internal void RequireSourceChain(string path, CancellationToken token, Action? context)
            => RequirePreparedWindowsChain(path, _sourceRoot, _source, _budget, token, context);

        internal void RequireDestinationChain(string path, CancellationToken token, Action? context)
        {
            Guard(token, context);
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                if (_destination.TryGetValue(current, out var expected)) RequireDestinationPath(current, expected, token, null);
            Guard(token, context);
        }

        private void RequireDestinationPath(string path, Destination expected, CancellationToken token, Action? context)
        {
            if (SameOrInside(path, _destinationRoot))
            {
                Guard(token, context);
                FolderCopyWindowsMetadata.RequireUnchanged(path, expected.Metadata, expected.ExpectedWriteTime, _budget, token);
                Guard(token, context);
            }
            else RequirePreparedWindowsPath(path, _destinationRoot, expected.Metadata, _budget, token, context);
        }

        internal bool MatchesBasic(string path, FolderCopySnapshot? actual, FolderCopySnapshot? expected)
        {
            if (actual is null || expected is null) return actual == expected;
            if (actual.Kind != expected.Kind || actual.Size != expected.Size || actual.Attributes != expected.Attributes || actual.UnixMode != expected.UnixMode)
                return false;
            if (!SameOrInside(path, _destinationRoot)) return true; // root外mtimeは追跡しない。
            return _destination.TryGetValue(path, out var metadata)
                ? checked((ulong)actual.LastWriteTimeUtc.ToFileTimeUtc()) == metadata.ExpectedWriteTime
                : actual.LastWriteTimeUtc == expected.LastWriteTimeUtc;
        }

        internal void RefreshKnownParentMtime(string path, CancellationToken token, Action? context)
        {
            Guard(token, context);
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                if (!_destination.TryGetValue(current, out var state)) continue;
                if (!state.Metadata.IsDirectory) throw new IOException("既知namespace更新の親がdirectoryではありません。");
                token.ThrowIfCancellationRequested();
                var writeTime = FolderCopyWindowsMetadata.RefreshOwnedParentMtime(current, state.Metadata,
                    state.ExpectedWriteTime, _budget, token);
                token.ThrowIfCancellationRequested();
                state.ExpectedWriteTime = writeTime;
            }
            Guard(token, context);
        }

        internal void AdoptCreatedDirectory(string path, Dictionary<string, FolderCopySnapshot?> basicState,
            CancellationToken token, Action? context)
        {
            if (_destination.ContainsKey(path)) throw new IOException("既存directory metadataを新規として取り込めません。");
            Guard(token, context);
            _budget.ReserveRetainedBytes(checked(WindowsMetadataStateAllowance + 2L * path.Length));
            var metadata = FolderCopyWindowsMetadata.Capture(path, _budget, token);
            Guard(token, context);
            if (!metadata.IsDirectory) throw new IOException("作成したdirectoryの種別が変わりました。");
            var basic = ReadSnapshot(path) ?? throw new IOException("作成したdirectoryを確認できません。");
            RequireWindowsSnapshot(basic, metadata);
            _destination.Add(path, new(metadata));
            basicState[path] = basic;
        }

        internal void AdoptPublishedFile(string path, FolderCopyWindowsMetadata metadata, Dictionary<string, FolderCopySnapshot?> basicState)
        {
            _budget.ReserveRetainedBytes(128); // 新規publication state/basic DTO。metadata配列はlease参照を共有。
            _destination[path] = new(metadata);
            basicState[path] = new(DirectoryEntryKind.File, metadata.Size, DateTime.FromFileTimeUtc(checked((long)metadata.LastWriteTime)),
                metadata.ObservedAttributes, null);
        }

        internal FolderCopyWindowsMetadataVerification VerifyDirectory(string path, CancellationToken token, Action? context)
        {
            var expected = _destination[path];
            RequireDestinationPath(path, expected, token, context);
            var proof = WindowsMetadataSnapshot(expected.Metadata, _budget, expected.ExpectedWriteTime);
            // fixed caller比較が全fieldの同一を確認したため、DTOの観測値も同じscalar/contentとなる。
            return new(proof, proof, true, FolderCopyWindowsMetadata.QualificationScope + ";DirectoryMetadataPreserved;OutsideRootAncestorWriteTimeUntracked");
        }
    }

    private static async Task CopyWindowsFileAsync(FolderCopyEntry entry, string temporary, CopyBudget budget,
        Dictionary<string, FolderCopyEntry> sourceState, Dictionary<string, FolderCopySnapshot?> state,
        WindowsExecutionState windows, CopyMutation item, string destinationRoot, CancellationToken token,
        Action<string>? validateDestination, Action? validateContext)
    {
        var source = entry.SourceWindowsMetadata ?? throw new IOException("Windows source metadataが計画にありません。");
        Guard(token, validateContext);
        var initial = FolderCopyWindowsMetadata.Capture(temporary, budget, token);
        Guard(token, validateContext);
        var lease = FolderCopyWindowsMetadata.OpenStaging(temporary, initial, budget, token);
        Exception? primary = null;
        try
        {
            Guard(token, validateContext);
            lease.ApplySourceMetadata(source, entry.DestinationWindowsMetadata, token);
            Guard(token, validateContext);
            var expectedProof = WindowsMetadataSnapshot(lease.PreparedSnapshot, budget);
            item.WindowsMetadata = new(expectedProof, null, false, FolderCopyWindowsMetadata.OwnedStaging.Qualification);
            Guard(token, validateContext);
            ValidateDestinationChain(entry.DestinationPath, destinationRoot, state, token, validateDestination, validateContext, windows);
            await RequireSourceAsync(entry, budget, token).ConfigureAwait(false);
            await RequireDestinationStreamsAsync(entry, budget, token).ConfigureAwait(false);
            RequireDestinationCapability(entry, token);
            Guard(token, validateContext);
            RequireSourcePathMetadata(entry, sourceState, token, validateContext);
            windows.RequireSourceChain(entry.SourcePath, token, validateContext);
            ValidateDestinationChain(entry.DestinationPath, destinationRoot, state, token, validateDestination, validateContext, windows);
            RejectLinks(temporary);
            // DATA/ADS検証はleaseをopenする前に完了。最後のsource/oldtarget DATA guardsもawaitでclose済み。
            lease.Publish(entry.DestinationPath, entry.DestinationWindowsMetadata, () => Guard(token, validateContext), () =>
            {
                item.Published = true;
                item.Mutated = true;
                item.TemporaryPath = null;
            }, token);
            if (!lease.Finalized) throw new IOException("公開したstagingの最終close/属性照合が完了していません。");
            item.WindowsMetadata = new(expectedProof, expectedProof, true, FolderCopyWindowsMetadata.OwnedStaging.Qualification);
            Guard(token, validateContext);
            windows.AdoptPublishedFile(entry.DestinationPath, lease.PreparedSnapshot, state);
            windows.RefreshKnownParentMtime(Path.GetDirectoryName(entry.DestinationPath)!, token, validateContext);
        }
        catch (Exception exception) { primary = exception; throw; }
        finally
        {
            try
            {
                if (lease.Published) lease.Close(primary);
                else if (lease.DiscardUnpublished(primary)) item.TemporaryPath = null;
            }
            catch (Exception cleanup) when (IsCopyFailure(cleanup))
            {
                // 成功経路のclose失敗は一次例外としてthrowされるため、別cleanup欄も保持する。
                if (NativeCloseFailure(cleanup) is null) item.NativeCleanupFailure = cleanup.Message;
                throw;
            }
        }
    }
}
