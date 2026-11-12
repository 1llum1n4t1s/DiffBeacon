using System.ComponentModel;
using System.Runtime.InteropServices;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public sealed class FolderMovePlan
{
    internal FolderMovePlan(FolderCopyPlan copyPlan) => CopyPlan = copyPlan;
    public FolderCopyPlan CopyPlan { get; }
}

public enum FolderMoveEntryStatus { Moved, Failed, Cancelled, NotExecuted }

public sealed record FolderMoveEntryResult(string RelativePath, string SourcePath,
    string DestinationPath, FolderMoveEntryStatus Status, string? Reason = null);

public sealed class FolderMoveResult
{
    internal FolderMoveResult(IEnumerable<FolderMoveEntryResult> entries, string? reason,
        bool cancelled, bool mutationOccurred)
    {
        Entries = Array.AsReadOnly(entries.ToArray());
        Reason = reason;
        Cancelled = cancelled;
        MutationOccurred = mutationOccurred;
    }

    public IReadOnlyList<FolderMoveEntryResult> Entries { get; }
    public string? Reason { get; }
    public bool Cancelled { get; }
    public bool MutationOccurred { get; }
    public bool Succeeded => Reason is null && !Cancelled && Entries.All(entry => entry.Status == FolderMoveEntryStatus.Moved);
    public int MovedCount => Entries.Count(entry => entry.Status == FolderMoveEntryStatus.Moved);
}

public static partial class FolderOperations
{
    public static async Task<FolderMovePlan> PrepareMoveAsync(DirectoryCopySelection selection,
        FolderCopyLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        // コピー側の候補縮約で、ディレクトリや重複選択を暗黙に移動対象へ変えない。
        var selected = selection.SelectedPaths.ToArray();
        var candidates = selection.Candidates.ToArray();
        var paths = selected.Select(NormalizeRelativePath).ToHashSet(PathComparer);
        if (paths.Count == 0 || paths.Count != selected.Length || candidates.Length != selected.Length)
            throw new IOException("移動には重複のない通常ファイルを一件ずつ選択してください。");
        var candidatePaths = new HashSet<string>(PathComparer);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Source.Kind != DirectoryEntryKind.File || candidate.ExpandPhysicalDirectory)
                throw new IOException("ディレクトリは移動できません。通常ファイルを選択してください。");
            if (!paths.Contains(NormalizeRelativePath(candidate.RelativePath))
                || !candidatePaths.Add(NormalizeRelativePath(candidate.RelativePath)))
                throw new IOException("移動の選択と通常ファイル候補が一対一で一致しません。");
        }
        var captured = selection with
        {
            SelectedPaths = Array.AsReadOnly(selected),
            Candidates = Array.AsReadOnly(candidates)
        };
        var plan = await PrepareAsync(captured, limits, cancellationToken).ConfigureAwait(false);
        if (plan.Entries.Count != selected.Length || !paths.SetEquals(plan.Entries.Select(entry => entry.RelativePath)))
            throw new IOException("移動計画と選択した通常ファイルが一対一で一致しません。");
        foreach (var entry in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireMoveEntry(entry);
            RequireMoveParent(entry);
            RequireSnapshot(entry.DestinationPath, null);
        }
        return new(plan);
    }

    public static Task<FolderMoveResult> ExecuteMoveAsync(FolderMovePlan plan,
        Action<string>? validateSource = null, Action<string>? validateDestination = null,
        Action? validateContext = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // callback の実行 thread は保証しない。UI は必要な dispatcher 照合を行う。
        return Task.Run(() => ExecuteMoveCoreAsync(plan.CopyPlan, validateSource,
            validateDestination, validateContext, cancellationToken));
    }

    private static async Task<FolderMoveResult> ExecuteMoveCoreAsync(FolderCopyPlan plan,
        Action<string>? validateSource, Action<string>? validateDestination,
        Action? validateContext, CancellationToken token)
    {
        var results = new List<FolderMoveEntryResult>();
        FolderCopyEntry? current = null;
        var moved = false;
        try
        {
            Guard(token, validateContext);
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
                throw new PlatformNotSupportedException("このOSでは上書きしない同一ボリューム移動を検証していないため、移動できません。");
            ValidateLimits(plan.Limits);
            var budget = new CopyBudget(plan.Limits, plan.PreparationReadBytes, plan.MetadataRetainedBytes,
                plan.PreparationMetadataQueryBytes, plan.PreparationMetadataNativeOperations);
            var sourceState = plan.Entries.ToDictionary(entry => entry.SourcePath, entry => entry, PathComparer);
            var destinationState = plan.DestinationParents.ToDictionary(pair => pair.Key, pair => pair.Value, PathComparer);
            foreach (var entry in plan.Entries) destinationState.Add(entry.DestinationPath, null);
            var windows = plan.UsesWindowsMetadata ? new MoveWindowsState(plan, budget) : null;
            // 既存rootの外側はnamespaceだけ検査し、出力保護callbackはroot内または未作成親だけに適用する。
            Action<string>? destinationGuard = validateDestination is null ? null : path =>
            {
                if (SameOrInside(path, plan.DestinationRoot) || !Directory.Exists(path)) validateDestination(path);
            };

            // 全件の本文・ADS・namespace・volumeを、最初のrenameより前に照合する。
            foreach (var entry in plan.Entries)
            {
                current = entry;
                RequireMoveEntry(entry);
                await ValidateEntryAsync(entry).ConfigureAwait(false);
            }
            foreach (var entry in plan.Entries)
            {
                current = entry;
                moved = false;
                await ValidateEntryAsync(entry).ConfigureAwait(false);
                Guard(token, validateContext);
                // callback の後にもnamespaceを照合する。no-replaceはOSの原子的renameで保証する。
                RequireSourcePathMetadata(entry, sourceState, token, validateContext);
                windows?.RequireChain(entry.SourcePath, true, token, validateContext);
                ValidateDestinationChain(entry.DestinationPath, plan.DestinationRoot, destinationState,
                    token, destinationGuard, validateContext);
                windows?.RequireChain(entry.DestinationPath, false, token, validateContext);
                RequireMoveParent(entry);
                Guard(token, validateContext);
                RenameMoveFile(entry.SourcePath, entry.DestinationPath);
                moved = true;
                // rename成功は取消・後続検査失敗でも取り消せない。先にMovedを確定する。
                results.Add(MoveEntryResult(entry, FolderMoveEntryStatus.Moved));
                RefreshMoveParents(entry, destinationState, windows);
            }
            return new(results, null, false, results.Count != 0);

            async Task ValidateEntryAsync(FolderCopyEntry entry)
            {
                Guard(token, validateContext);
                ValidateMoveCallbackChain(entry.SourcePath, plan.SourceRoot, validateSource, token, validateContext);
                ValidateMoveCallbackChain(entry.DestinationPath, plan.DestinationRoot, destinationGuard, token, validateContext);
                await RequireSourceAsync(entry, budget, token).ConfigureAwait(false);
                // 本文・ADS確認中に入力保護やフィルターが変わっていないことを、namespace検査より先に再確認する。
                ValidateMoveCallbackChain(entry.SourcePath, plan.SourceRoot, validateSource, token, validateContext);
                ValidateMoveCallbackChain(entry.DestinationPath, plan.DestinationRoot, destinationGuard, token, validateContext);
                RequireSourcePathMetadata(entry, sourceState, token, validateContext);
                windows?.RequireChain(entry.SourcePath, true, token, validateContext);
                ValidateDestinationChain(entry.DestinationPath, plan.DestinationRoot, destinationState,
                    token, destinationGuard, validateContext);
                windows?.RequireChain(entry.DestinationPath, false, token, validateContext);
                RequireMoveParent(entry);
                if (!MoveVolume(entry.SourcePath).Equals(MoveVolume(Path.GetDirectoryName(entry.DestinationPath)!), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("別ボリュームへの移動はできません。コピーと削除への切替は行いません。");
                Guard(token, validateContext);
            }
        }
        catch (Exception exception) when (IsCopyFailure(exception))
        {
            var cancelled = exception is OperationCanceledException;
            if (moved && results.Count != 0)
                results[^1] = results[^1] with { Reason = exception.Message };
            foreach (var entry in plan.Entries.Skip(results.Count))
                results.Add(MoveEntryResult(entry,
                    ReferenceEquals(entry, current) || (current is null && results.Count == 0)
                        ? cancelled ? FolderMoveEntryStatus.Cancelled : FolderMoveEntryStatus.Failed
                        : FolderMoveEntryStatus.NotExecuted, exception.Message));
            return new(results, exception.Message, cancelled,
                results.Any(entry => entry.Status == FolderMoveEntryStatus.Moved));
        }
    }

    private static FolderMoveEntryResult MoveEntryResult(FolderCopyEntry entry,
        FolderMoveEntryStatus status, string? reason = null) =>
        new(entry.RelativePath, entry.SourcePath, entry.DestinationPath, status, reason);

    private static void RequireMoveEntry(FolderCopyEntry entry)
    {
        if (entry.Kind != DirectoryEntryKind.File || entry.Children.Count != 0 || entry.SourceStreams is null)
            throw new IOException("ディレクトリは移動できません。通常ファイルを選択してください。");
        if (entry.Destination is not null)
            throw new IOException($"既存の移動先は上書きできません: {entry.DestinationPath}");
    }

    private static void RequireMoveParent(FolderCopyEntry entry)
    {
        var parent = Path.GetDirectoryName(entry.DestinationPath)!;
        if (ReadSnapshot(parent)?.Kind != DirectoryEntryKind.Directory)
            throw new IOException($"移動先の親ディレクトリが存在しません: {parent}");
    }

    private static void ValidateMoveCallbackChain(string path, string root, Action<string>? validate,
        CancellationToken token, Action? context)
    {
        for (var current = path; current is not null && SameOrInside(current, root); current = Path.GetDirectoryName(current))
        {
            Guard(token, context);
            validate?.Invoke(current);
        }
        Guard(token, context);
    }

    private static void RefreshMoveParents(FolderCopyEntry entry,
        Dictionary<string, FolderCopySnapshot?> destinationState, MoveWindowsState? windows)
    {
        // 自分のrenameで変化した親mtimeだけを追跡する。元fileは再利用しない。
        windows?.RefreshParents(Path.GetDirectoryName(entry.SourcePath)!, true);
        windows?.RefreshParents(Path.GetDirectoryName(entry.DestinationPath)!, false);
        for (var parent = Path.GetDirectoryName(entry.DestinationPath); parent is not null; parent = Path.GetDirectoryName(parent))
            if (destinationState.TryGetValue(parent, out var expected) && expected is not null)
            {
                var actual = ReadSnapshot(parent) ?? throw new IOException("移動後に親ディレクトリが不在になりました。");
                if ((actual with { LastWriteTimeUtc = expected.LastWriteTimeUtc }) != expected)
                    throw new IOException("移動後に親ディレクトリの属性が変わっています。");
                destinationState[parent] = actual;
            }
    }

    private sealed class MoveWindowsState
    {
        private readonly FolderCopyPlan _plan;
        private readonly CopyBudget _budget;
        private readonly Dictionary<string, ulong> _writeTimes = new(PathComparer);
        private readonly Dictionary<string, FolderCopyWindowsMetadata> _source, _destination;
        internal MoveWindowsState(FolderCopyPlan plan, CopyBudget budget)
        {
            _plan = plan;
            _budget = budget;
            _source = plan.SourceWindowsMetadata.ToDictionary(pair => pair.Key, pair => pair.Value, PathComparer);
            _destination = plan.DestinationWindowsMetadata.ToDictionary(pair => pair.Key, pair => pair.Value, PathComparer);
            foreach (var pair in plan.SourceWindowsMetadata.Concat(plan.DestinationWindowsMetadata))
                _writeTimes.TryAdd(pair.Key, pair.Value.LastWriteTime);
        }

        internal void RequireChain(string path, bool source, CancellationToken token, Action? context)
        {
            var root = source ? _plan.SourceRoot : _plan.DestinationRoot;
            var metadata = source ? _source : _destination;
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                if (metadata.TryGetValue(current, out var expected))
                    {
                        Guard(token, context);
                        if (SameOrInside(current, root))
                            FolderCopyWindowsMetadata.RequireUnchanged(current, expected, _writeTimes[current], _budget, token);
                        else RequirePreparedWindowsPath(current, root, expected, _budget, token, context);
                    }
            Guard(token, context);
        }

        internal void RefreshParents(string path, bool source)
        {
            var root = source ? _plan.SourceRoot : _plan.DestinationRoot;
            var metadata = source ? _source : _destination;
            for (var current = path; current is not null && SameOrInside(current, root); current = Path.GetDirectoryName(current))
                if (metadata.TryGetValue(current, out var expected))
                    _writeTimes[current] = FolderCopyWindowsMetadata.RefreshOwnedParentMtime(current,
                        expected, _writeTimes[current], _budget, CancellationToken.None);
        }
    }

    private static void RenameMoveFile(string source, string destination)
    {
        if (OperatingSystem.IsMacOS())
        {
            // Apple sys/stdio.h: RENAME_EXCL=0x4。未対応filesystem/EXDEVも失敗として返し、fallbackしない。
            // https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/stdio.h
            if (RenameMoveMac(source, destination, 0x00000004) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException($"排他的な同一ボリューム移動に失敗しました (errno {error})。");
            }
            return;
        }
        // flags=0なら別volumeのcopy/delete fallbackも既存先の置換も許可しない。
        if (MoveFileNoReplace(FolderCopyWindowsStreams.ExtendedPath(source), FolderCopyWindowsStreams.ExtendedPath(destination), 0) == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"同一ボリューム移動に失敗しました (Win32 error {error})。", new Win32Exception(error));
        }
    }

    private static unsafe string MoveVolume(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            // 既存FolderCopyMacFileKindと同じDarwin inode64 ABI。dev_tは先頭のint32。
            var result = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => MoveLStatX64(path, out var x64State) == 0 ? x64State.Device : MoveStatFailure(),
                Architecture.Arm64 => MoveLStatArm64(path, out var armState) == 0 ? armState.Device : MoveStatFailure(),
                _ => throw new PlatformNotSupportedException("macOSの対応アーキテクチャはx64とARM64です。")
            };
            return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var mount = stackalloc char[32768];
        var volume = stackalloc char[64];
        if (GetMoveVolumePath(FolderCopyWindowsStreams.ExtendedPath(path), mount, 32768) == 0
            || GetMoveVolumeName(mount, volume, 64) == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"移動対象のvolumeを確認できません (Win32 error {error})。", new Win32Exception(error));
        }
        return new string(volume);
    }

    private static int MoveStatFailure() => throw new IOException($"移動対象のvolumeを確認できません (errno {Marshal.GetLastPInvokeError()})。");

    [StructLayout(LayoutKind.Explicit, Size = 512)]
    private struct MoveStatBuffer
    {
        [FieldOffset(0)] public int Device;
    }

    [LibraryImport("libSystem.B.dylib", EntryPoint = "renamex_np", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameMoveMac(string source, string destination, uint flags);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MoveLStatX64(string path, out MoveStatBuffer state);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MoveLStatArm64(string path, out MoveStatBuffer state);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int MoveFileNoReplace(string source, string destination, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial int GetMoveVolumePath(string path, char* volumePath, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true)]
    private static unsafe partial int GetMoveVolumeName(char* volumePath, char* volumeName, uint bufferLength);
}
