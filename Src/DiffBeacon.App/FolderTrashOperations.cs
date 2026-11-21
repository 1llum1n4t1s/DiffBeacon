using DiffBeacon.Core;

namespace DiffBeacon.App;

public enum FolderTrashEntryStatus { Trashed, Unsupported, Failed, Cancelled, NotExecuted }

public sealed record FolderTrashPlanEntry(string RelativePath, string SourcePath,
    bool IsSupported, long LogicalBytes, string? Reason)
{
    internal FolderCopySnapshot? Snapshot { get; init; }
    internal FolderCopyStreamSet? Streams { get; init; }
}

public sealed class FolderTrashPlan
{
    internal FolderTrashPlan(string sourceRoot, string otherRoot, IEnumerable<FolderTrashPlanEntry> entries,
        FolderCopyLimits limits, long logicalBytes)
    {
        SourceRoot = sourceRoot;
        OtherRoot = otherRoot;
        Entries = Array.AsReadOnly(entries.ToArray());
        Limits = limits with { };
        LogicalBytes = logicalBytes;
    }

    public string SourceRoot { get; }
    public string OtherRoot { get; }
    public IReadOnlyList<FolderTrashPlanEntry> Entries { get; }
    public FolderCopyLimits Limits { get; }
    public long LogicalBytes { get; }
    public int SupportedCount => Entries.Count(entry => entry.IsSupported);
    public int UnsupportedCount => Entries.Count - SupportedCount;
}

public sealed record FolderTrashEntryResult(string RelativePath, string SourcePath,
    FolderTrashEntryStatus Status, string? Reason = null);

public sealed class FolderTrashResult
{
    internal FolderTrashResult(IEnumerable<FolderTrashEntryResult> entries, string? reason,
        bool cancelled, bool mutationOccurred)
    {
        Entries = Array.AsReadOnly(entries.ToArray());
        Reason = reason;
        Cancelled = cancelled;
        MutationOccurred = mutationOccurred;
    }

    public IReadOnlyList<FolderTrashEntryResult> Entries { get; }
    public string? Reason { get; }
    public bool Cancelled { get; }
    public bool MutationOccurred { get; }
    public int TrashedCount => Entries.Count(entry => entry.Status == FolderTrashEntryStatus.Trashed);
    public int UnsupportedCount => Entries.Count(entry => entry.Status == FolderTrashEntryStatus.Unsupported);
    public bool Succeeded => Reason is null && !Cancelled
        && Entries.All(entry => entry.Status is FolderTrashEntryStatus.Trashed or FolderTrashEntryStatus.Unsupported);
}

public static partial class FolderOperations
{
    public static Task<FolderTrashPlan> PrepareTrashAsync(DirectoryCopySelection selection,
        FolderCopyLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!Enum.IsDefined(selection.Direction) || !Enum.IsDefined(selection.Mode))
            throw new ArgumentOutOfRangeException(nameof(selection));
        if (selection.Mode != DirectoryCopyMode.DifferencesOnly)
            throw new ArgumentException("ごみ箱へ移せるのは比較結果で選んだ差分だけです。", nameof(selection));

        var candidates = selection.Candidates.ToArray();
        var selected = selection.SelectedPaths.ToArray();
        var effectiveLimits = (limits ?? new FolderCopyLimits()) with { };
        ValidateLimits(effectiveLimits);
        return Task.Run(() => PrepareTrashCoreAsync(selection.SourceRoot, selection.DestinationRoot,
            selected, candidates, effectiveLimits, cancellationToken), cancellationToken);
    }

    private static async Task<FolderTrashPlan> PrepareTrashCoreAsync(string sourceRoot, string otherRoot,
        string[] selected, DirectoryCopyCandidate[] candidates, FolderCopyLimits limits, CancellationToken token)
    {
        sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        otherRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(otherRoot));
        RejectLinks(sourceRoot);
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException(sourceRoot);
        if (SameOrInside(sourceRoot, otherRoot) || SameOrInside(otherRoot, sourceRoot))
            throw new IOException("比較するフォルダーのルートが重なっています。");
        if (selected.Length == 0 || selected.Length > limits.MaximumEntries || candidates.Length > limits.MaximumEntries)
            throw new IOException("ごみ箱の対象件数が上限を超えているか、選択がありません。");

        var selectedSet = selected.Select(NormalizeRelativePath).ToHashSet(PathComparer);
        if (selectedSet.Count != selected.Length) throw new IOException("ごみ箱の選択に重複があります。");
        var budget = new CopyBudget(limits);
        var entries = new List<FolderTrashPlanEntry>(candidates.Length);
        var seen = new HashSet<string>(PathComparer);
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(candidate.RelativePath);
            if (!CoveredBySelection(relative, selectedSet) || !seen.Add(relative))
                throw new IOException("ごみ箱候補が選択範囲外か、重複しています。");

            var path = ResolveInside(sourceRoot, relative);
            if (!path.Equals(Path.GetFullPath(candidate.Source.Path), PathComparison))
                throw new IOException("選択した差分ファイルが入力ルート内の対象と一致しません。");
            if (candidate.Source.Kind != DirectoryEntryKind.File || candidate.ExpandPhysicalDirectory)
            {
                entries.Add(new(relative, path, false, 0, "通常ファイル以外は対象外です."));
                continue;
            }

            RejectLinks(path);
            var snapshot = ReadSnapshot(path) ?? throw new FileNotFoundException("差分ファイルがありません。", path);
            if (snapshot.Kind != DirectoryEntryKind.File || snapshot.Size != candidate.Source.Size
                || snapshot.LastWriteTimeUtc != candidate.Source.LastWriteTimeUtc
                || snapshot.Attributes != candidate.Source.Attributes)
                throw new IOException($"比較後に差分ファイルの状態が変わっています: {path}");

            var layout = ReadPlannedStreamLayout(path, snapshot.Size, budget, token);
            var logicalBytes = StreamLayoutBytes(layout);
            budget.ReserveFile(logicalBytes, 0);
            var streams = await CaptureFileStreamsAsync(path, snapshot, layout, budget, token).ConfigureAwait(false);
            RequireSnapshot(path, snapshot);
            entries.Add(new(relative, path, true, logicalBytes, null)
            { Snapshot = snapshot, Streams = streams });
        }

        return new(sourceRoot, otherRoot, entries, limits, budget.LogicalBytes);
    }

    public static Task<FolderTrashResult> ExecuteTrashAsync(FolderTrashPlan plan,
        Action<string>? validateSource = null, Action? validateContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Run(() => ExecuteTrashCoreAsync(plan, validateSource, validateContext, cancellationToken), cancellationToken);
    }

    private static async Task<FolderTrashResult> ExecuteTrashCoreAsync(FolderTrashPlan plan,
        Action<string>? validateSource, Action? validateContext, CancellationToken token)
    {
        var results = new List<FolderTrashEntryResult>(plan.Entries.Count);
        var budget = new CopyBudget(plan.Limits);
        var mutationOccurred = false;
        foreach (var entry in plan.Entries)
        {
            if (!entry.IsSupported)
            {
                results.Add(new(entry.RelativePath, entry.SourcePath, FolderTrashEntryStatus.Unsupported, entry.Reason));
                continue;
            }

            try
            {
                Guard(token, validateContext);
                FolderTrashNative.MoveFileToTrash(entry.SourcePath, () =>
                {
                    Guard(token, validateContext);
                    validateSource?.Invoke(entry.SourcePath);
                    Guard(token, validateContext);
                    VerifyTrashSourceAsync(entry, budget, token).GetAwaiter().GetResult();
                    Guard(token, validateContext);
                }, token);
                if (PathExists(entry.SourcePath))
                    throw new IOException($"ごみ箱への移動後も元ファイルが残っています: {entry.SourcePath}");
                mutationOccurred = true;
                results.Add(new(entry.RelativePath, entry.SourcePath, FolderTrashEntryStatus.Trashed));
            }
            catch (Exception error) when (error is not OutOfMemoryException
                and not StackOverflowException and not AccessViolationException)
            {
                var cancelled = error is OperationCanceledException || token.IsCancellationRequested;
                if (!PathExists(entry.SourcePath)) mutationOccurred = true;
                results.Add(new(entry.RelativePath, entry.SourcePath,
                    cancelled ? FolderTrashEntryStatus.Cancelled : FolderTrashEntryStatus.Failed, error.Message));
                foreach (var remaining in plan.Entries.Skip(results.Count))
                    results.Add(new(remaining.RelativePath, remaining.SourcePath,
                        remaining.IsSupported ? FolderTrashEntryStatus.NotExecuted : FolderTrashEntryStatus.Unsupported,
                        remaining.IsSupported ? "前の項目で停止しました。" : remaining.Reason));
                return new(results, error.Message, cancelled, mutationOccurred);
            }
        }
        return new(results, null, false, mutationOccurred);
    }

    private static async Task VerifyTrashSourceAsync(FolderTrashPlanEntry entry, CopyBudget budget, CancellationToken token)
    {
        var snapshot = entry.Snapshot ?? throw new InvalidOperationException("差分ファイルの確認情報がありません。");
        var expected = entry.Streams ?? throw new InvalidOperationException("差分ストリームの確認情報がありません。");
        RequireSourceSnapshot(entry.SourcePath, snapshot);
        var layout = ReadPlannedStreamLayout(entry.SourcePath, snapshot.Size, budget, token);
        budget.ReserveFile(StreamLayoutBytes(layout), 0);
        var actual = await CaptureFileStreamsAsync(entry.SourcePath, snapshot, layout, budget, token).ConfigureAwait(false);
        if (actual.SupportsNamedStreams != expected.SupportsNamedStreams
            || actual.Streams.Count != expected.Streams.Count
            || actual.Streams.Where((stream, index) => stream.Name != expected.Streams[index].Name
                || stream.Size != expected.Streams[index].Size
                || !stream.Sha256.Equals(expected.Streams[index].Sha256, StringComparison.Ordinal)).Any())
            throw new IOException($"確認後に差分ファイルの内容またはストリームが変わっています: {entry.SourcePath}");
        RequireSourceSnapshot(entry.SourcePath, snapshot);
    }

    private static bool PathExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
