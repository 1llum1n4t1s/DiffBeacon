using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public static partial class FolderOperations
{
    private const int StreamBufferSize = 65_536;
    private const FileAttributes PortableWindowsFileAttributes = FileAttributes.ReadOnly | FileAttributes.Hidden
        | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed | FileAttributes.Temporary
        | FileAttributes.Offline | FileAttributes.Normal;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static Task<FolderCopyPlan> PrepareAsync(DirectoryCopySelection selection,
        FolderCopyLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!Enum.IsDefined(selection.Direction) || !Enum.IsDefined(selection.Mode)) throw new ArgumentOutOfRangeException(nameof(selection));
        // 選択元の配列は呼出し元が変更できるため、非同期開始前にコピーする。
        var candidates = selection.Candidates.ToArray();
        var selected = selection.SelectedPaths.ToArray();
        var effectiveLimits = (limits ?? new FolderCopyLimits()) with { };
        ValidateLimits(effectiveLimits);
        return Task.Run(() => PrepareCoreAsync(selection.SourceRoot, selection.DestinationRoot,
            selected, candidates, effectiveLimits, cancellationToken), cancellationToken);
    }

    private static async Task<FolderCopyPlan> PrepareCoreAsync(string sourceRoot, string destinationRoot,
        string[] selected, DirectoryCopyCandidate[] candidates, FolderCopyLimits limits, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        RejectLinks(sourceRoot);
        RejectLinks(destinationRoot);
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException(sourceRoot);
        if (SameOrInside(sourceRoot, destinationRoot) || SameOrInside(destinationRoot, sourceRoot))
            throw new IOException("コピー元とコピー先のルートが重なっています。");
        if (selected.Length > limits.MaximumEntries || candidates.Length > limits.MaximumEntries)
            throw new IOException("コピー項目数が上限を超えています。");
        var normalizedSelected = selected.Select(NormalizeRelativePath).ToHashSet(PathComparer);
        if (normalizedSelected.Count == 0) throw new ArgumentException("選択項目がありません。", nameof(selected));
        var entries = new List<FolderCopyEntry>();
        var parents = new Dictionary<string, FolderCopySnapshot?>(PathComparer);
        var seen = new HashSet<string>(PathComparer);
        var budget = new CopyBudget(limits);
        var usesWindowsMetadata = UsesLocalNtfsMetadata(sourceRoot, destinationRoot);
        var sourceWindowsMetadata = new Dictionary<string, FolderCopyWindowsMetadata>(PathComparer);
        var destinationWindowsMetadata = new Dictionary<string, FolderCopyWindowsMetadata>(PathComparer);
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(candidate.RelativePath);
            if (!CoveredBySelection(relative, normalizedSelected))
                throw new IOException("コピー候補が選択範囲にありません。");
            var source = ResolveInside(sourceRoot, relative);
            if (!source.Equals(Path.GetFullPath(candidate.Source.Path), PathComparison))
                throw new IOException("選択したコピー元がルート内の対象と一致しません。");
            var captured = ReadSourceSnapshot(source) ?? throw new FileNotFoundException("コピー元がありません。", source);
            if (captured.Kind != candidate.Source.Kind || candidate.ExpandPhysicalDirectory != (captured.Kind == DirectoryEntryKind.Directory))
                throw new IOException("コピー元の種別が選択時から変わっています。");
            var pending = new Stack<string>();
            pending.Push(relative);
            while (pending.TryPop(out var current))
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(current)) continue;
                if (entries.Count >= limits.MaximumEntries || current.Count(character => character == '/') + 1 > limits.MaximumDepth)
                    throw new IOException("コピー項目数または深さが上限を超えています。");
                source = ResolveInside(sourceRoot, current);
                var destination = ResolveInside(destinationRoot, current);
                if (SameOrInside(source, destination) || SameOrInside(destination, source))
                    throw new IOException("コピー元とコピー先の対象が重なっています。");
                var snapshot = ReadSourceSnapshot(source) ?? throw new FileNotFoundException("コピー元がありません。", source);
                var target = ReadSnapshot(destination);
                ValidateDestinationKind(destination, target, snapshot.Kind);
                CaptureDestinationParents(destinationRoot, destination, parents);
                if (usesWindowsMetadata)
                {
                    CaptureWindowsMetadataChain(source, sourceRoot, sourceWindowsMetadata, budget, token);
                    CaptureWindowsMetadataChain(destination, destinationRoot, destinationWindowsMetadata, budget, token);
                    RequireWindowsSnapshot(snapshot, sourceWindowsMetadata[source]);
                    if (target is not null) RequireWindowsSnapshot(target, destinationWindowsMetadata[destination]);
                }
                FolderCopyStreamSet? sourceStreams = null;
                FolderCopyStreamSet? destinationStreams = null;
                var destinationSupportsNamedStreams = false;
                string[] children = [];
                if (snapshot.Kind == DirectoryEntryKind.Directory)
                {
                    children = ReadMembership(source, limits.MaximumEntries, token);
                    // 明示選択の親子が重なっても、物理子は一度だけ登録する。
                    for (var index = children.Length - 1; index >= 0; index--)
                        pending.Push(NormalizeRelativePath(current + '/' + children[index]));
                }
                else
                {
                    // この stream backend は EFS 保存を保証できない。復号した平文を暗黙公開しない。
                    if (OperatingSystem.IsWindows() && (snapshot.Attributes & FileAttributes.Encrypted) != 0)
                        throw new NotSupportedException("EFS ファイルの暗号化を保持するコピー経路は未実装です。");
                    if (OperatingSystem.IsWindows() && target is not null && (target.Attributes & FileAttributes.Encrypted) != 0)
                        throw new NotSupportedException("EFS コピー先の暗号化を保持する置換経路は未実装です。");
                    var sourceLayout = ReadPlannedStreamLayout(source, snapshot.Size, budget, token);
                    var destinationLayout = target is null ? null : ReadPlannedStreamLayout(destination, target.Size, budget, token);
                    destinationSupportsNamedStreams = ReadDestinationCapability(destination, token);
                    if (sourceLayout.Streams.Count > 1 && !destinationSupportsNamedStreams)
                        throw new NotSupportedException("コピー先は名前付きストリームに対応していません。");
                    // 全streamの予定量を予約してから内容を読む。0byte ADSもdescriptor/nameへ計上する。
                    budget.ReserveFile(StreamLayoutBytes(sourceLayout), destinationLayout is null ? 0 : StreamLayoutBytes(destinationLayout));
                    sourceStreams = await CaptureFileStreamsAsync(source, snapshot, sourceLayout, budget, token).ConfigureAwait(false);
                    if (target is not null)
                        destinationStreams = await CaptureFileStreamsAsync(destination, target, destinationLayout!, budget, token).ConfigureAwait(false);
                    RequireSnapshot(destination, target);
                }
                RequireSourceSnapshot(source, snapshot);
                if (snapshot.Kind == DirectoryEntryKind.Directory) RequireMembership(source, children, token);
                if (usesWindowsMetadata)
                {
                    RequirePreparedWindowsChain(source, sourceRoot, sourceWindowsMetadata, budget, token);
                    RequirePreparedWindowsChain(destination, destinationRoot, destinationWindowsMetadata, budget, token);
                }
                entries.Add(new(current, source, destination, snapshot, target, sourceStreams, destinationStreams,
                    destinationSupportsNamedStreams, children,
                    usesWindowsMetadata ? sourceWindowsMetadata[source] : null,
                    usesWindowsMetadata && target is not null ? destinationWindowsMetadata[destination] : null));
            }
        }
        // 重複した子選択が先に来ても、親を先に実行する順序に固定する。
        var ordered = entries.OrderBy(entry => entry.RelativePath.Count(character => character == '/'))
            .ThenBy(entry => entry.RelativePath, PathComparer).ToArray();
        ValidateCrossSelectionOverlap(ordered);
        if (usesWindowsMetadata)
        {
            RequirePreparedWindowsMetadata(sourceRoot, sourceWindowsMetadata, budget, token);
            RequirePreparedWindowsMetadata(destinationRoot, destinationWindowsMetadata, budget, token);
        }
        return new(sourceRoot, destinationRoot, started, limits, ordered, parents, budget.LogicalBytes,
            budget.DestinationBytes, budget.PlannedIoBytes, budget.StreamDescriptors, budget.StreamNameCharacters, budget.ReadBytes,
            usesWindowsMetadata, WindowsMetadataQualification(usesWindowsMetadata), sourceWindowsMetadata, destinationWindowsMetadata,
            budget.MetadataRetainedBytes, budget.MetadataQueryBytes, budget.MetadataNativeOperations);
    }

    private static void ValidateLimits(FolderCopyLimits limits)
    {
        if (limits.MaximumEntries is < 1 or > 100_000 || limits.MaximumDepth is < 1 or > 256
            || limits.MaximumStreamDescriptors is < 1 or > 200_000 || limits.MaximumStreamNameCharacters is < 7 or > 4_000_000
            || limits.MaximumLogicalBytes is < 0 or > (1L << 40) || limits.MaximumIoBytes is < 0 or > (5L << 40)
            || limits.MaximumMetadataRetainedBytes is < 0 or > (512L << 20)
            || limits.MaximumMetadataQueryBytes is < 0 or > (256L << 30)
            || limits.MaximumMetadataNativeOperations is < 0 or > 128_000_000)
            throw new ArgumentOutOfRangeException(nameof(limits), "コピー予算は既定上限以下で指定してください。");
    }

    private static string NormalizeRelativePath(string relative)
    {
        DirectoryCopyPlanner.ValidateRelativePath(relative);
        var normalized = relative.Replace('\\', '/');
        foreach (var segment in normalized.Split('/'))
        {
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Any(char.IsControl))
                throw new ArgumentException("相対パスに使用できない文字があります。", nameof(relative));
            if (OperatingSystem.IsWindows())
            {
                var stem = segment.Split('.')[0].ToUpperInvariant();
                if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                    || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                        && stem[3] is >= '1' and <= '9'))
                    throw new ArgumentException("予約済みデバイス名はコピー対象にできません。", nameof(relative));
            }
        }
        return normalized;
    }

    private static string ResolveInside(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Inside(path, root)) throw new IOException("コピー対象が比較ルートの外です。");
        return path;
    }

    private static bool SameOrInside(string path, string root)
    {
        path = FolderPathProtection.ContainerPath(path);
        root = FolderPathProtection.ContainerPath(root);
        if (path.Equals(root, PathComparison) || Inside(path, root)) return true;
        if (!OperatingSystem.IsMacOS()) return false;
        // APFS が同じ項目へ解決する大小文字・Unicode別表記も重複として拒否する。
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (ArchivePaths.SameFile(current, root)) return true;
        return false;
    }

    private static bool CoveredBySelection(string relative, HashSet<string> selected)
    {
        if (selected.Contains(relative)) return true;
        for (var separator = relative.LastIndexOf('/'); separator >= 0; separator = relative.LastIndexOf('/', separator - 1))
        {
            if (selected.Contains(relative[..separator])) return true;
            if (separator == 0) break;
        }
        return false;
    }

    private static void ValidateCrossSelectionOverlap(IReadOnlyList<FolderCopyEntry> entries)
    {
        var sourcePaths = new SortedSet<string>(entries.Select(entry => entry.SourcePath.Replace('\\', '/')), PathComparer);
        foreach (var entry in entries)
        {
            var destination = entry.DestinationPath.Replace('\\', '/');
            for (var current = destination; current.Length != 0;)
            {
                if (sourcePaths.Contains(current)) throw new IOException("コピー先が別のコピー元と重なっています。");
                var separator = current.LastIndexOf('/');
                if (separator < 0) break;
                current = current[..separator];
            }
            var prefix = destination + '/';
            if (sourcePaths.Count > 0 && PathComparer.Compare(prefix, sourcePaths.Max!) <= 0)
            {
                using var iterator = sourcePaths.GetViewBetween(prefix, sourcePaths.Max!).GetEnumerator();
                if (iterator.MoveNext() && iterator.Current.StartsWith(prefix, PathComparison))
                    throw new IOException("コピー先が別のコピー元の親になっています。");
            }
        }
    }

    private static FolderCopySnapshot? ReadSnapshot(string path)
    {
        RejectLinks(path);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var directory = (attributes & FileAttributes.Directory) != 0;
        // コピー先や祖先も通常項目に限定し、FIFO/socketをMoveで置換しない。
        ValidatePlatformEntryKind(path, directory ? DirectoryEntryKind.Directory : DirectoryEntryKind.File);
        var size = directory ? 0 : new FileInfo(path).Length;
        UnixFileMode? mode = null;
        if (!OperatingSystem.IsWindows()) mode = File.GetUnixFileMode(path);
        return new(directory ? DirectoryEntryKind.Directory : DirectoryEntryKind.File, size,
            File.GetLastWriteTimeUtc(path), attributes, mode);
    }

    private static void RequireSnapshot(string path, FolderCopySnapshot? expected)
    {
        if (ReadSnapshot(path) != expected) throw new IOException($"確認後に項目の状態が変わっています: {path}");
    }

    private static FolderCopySnapshot? ReadSourceSnapshot(string path)
        => ReadSnapshot(path);

    private static void RequireSourceSnapshot(string path, FolderCopySnapshot expected)
    {
        if (ReadSourceSnapshot(path) != expected) throw new IOException($"確認後にコピー元の状態が変わっています: {path}");
    }

    private static void ValidatePlatformEntryKind(string path, DirectoryEntryKind kind)
    {
        if (OperatingSystem.IsMacOS()) FolderCopyMacFileKind.Require(path, kind);
        else if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("フォルダーコピーはWindowsとmacOSに対応しています。");
    }

    private static string[] ReadMembership(string directory, int maximumEntries = 100_000, CancellationToken token = default)
    {
        var children = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            if (children.Count >= maximumEntries) throw new IOException("ディレクトリ項目数が上限を超えています。");
            children.Add(Path.GetFileName(path));
        }
        return children.Order(PathComparer).ToArray();
    }

    private static void RequireMembership(string directory, IReadOnlyList<string> expected, CancellationToken token = default)
    {
        if (!ReadMembership(directory, Math.Min(100_000, expected.Count + 1), token).SequenceEqual(expected, PathComparer))
            throw new IOException($"確認後にディレクトリの項目が変わっています: {directory}");
    }

    private static void ValidateDestinationKind(string path, FolderCopySnapshot? state, DirectoryEntryKind kind,
        bool requireWritable = true)
    {
        if (state is null) return;
        if (state.Kind != kind) throw new IOException($"コピー先に異なる種別の項目があります: {path}");
        if (!requireWritable) return;
        if ((state.Attributes & FileAttributes.ReadOnly) != 0
            || (state.UnixMode is { } mode && (mode & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0))
            throw new UnauthorizedAccessException($"読み取り専用のコピー先へは書き込めません: {path}");
    }

    private static void CaptureDestinationParents(string root, string destination,
        Dictionary<string, FolderCopySnapshot?> parents)
    {
        for (var current = Path.GetDirectoryName(destination); current is not null && SameOrInside(current, root);
            current = Path.GetDirectoryName(current))
        {
            if (parents.ContainsKey(current)) continue;
            var state = ReadSnapshot(current);
            ValidateDestinationKind(current, state, DirectoryEntryKind.Directory);
            parents.Add(current, state);
        }
        // root が既存なら上位parentには書き込まない。root作成時だけ既存parentの書込み条件を検査する。
        var rootMissing = !Directory.Exists(root);
        for (var current = Path.GetDirectoryName(root); current is not null; current = Path.GetDirectoryName(current))
        {
            var state = ReadSnapshot(current);
            ValidateDestinationKind(current, state, DirectoryEntryKind.Directory, rootMissing);
            parents.TryAdd(current, state);
            if (state is not null) break;
        }
    }

    private sealed class CopyBudget(FolderCopyLimits limits, long previousReadBytes = 0,
        long previousMetadataRetainedBytes = 0, long previousMetadataQueryBytes = 0,
        int previousMetadataNativeOperations = 0) : IFolderCopyWindowsMetadataBudget
    {
        public long LogicalBytes { get; private set; }
        public long DestinationBytes { get; private set; }
        public long PlannedIoBytes { get; private set; }
        public int StreamDescriptors { get; private set; }
        public int StreamNameCharacters { get; private set; }
        public int RemainingStreamDescriptors => limits.MaximumStreamDescriptors - StreamDescriptors;
        public int RemainingStreamNameCharacters => limits.MaximumStreamNameCharacters - StreamNameCharacters;
        public long ReadBytes { get; private set; } = previousReadBytes;
        public long WriteBytes { get; private set; }
        public long MetadataRetainedBytes { get; private set; } = previousMetadataRetainedBytes;
        public long MetadataQueryBytes { get; private set; } = previousMetadataQueryBytes;
        public int MetadataNativeOperations { get; private set; } = previousMetadataNativeOperations;

        public void ReserveRetainedBytes(long bytes)
        {
            var next = checked(MetadataRetainedBytes + bytes);
            if (bytes < 0 || MetadataRetainedBytes < 0 || next > limits.MaximumMetadataRetainedBytes)
                throw new IOException("全コピー計画のWindows metadata保持予算が上限を超えています。");
            MetadataRetainedBytes = next;
        }

        public void ReserveQueryWork(long bytes, int nativeQueries)
        {
            var nextBytes = checked(MetadataQueryBytes + bytes);
            var nextOperations = checked(MetadataNativeOperations + nativeQueries);
            if (bytes < 0 || nativeQueries < 0 || MetadataQueryBytes < 0 || MetadataNativeOperations < 0
                || nextBytes > limits.MaximumMetadataQueryBytes || nextOperations > limits.MaximumMetadataNativeOperations)
                throw new IOException("全コピー計画のWindows metadata照合量またはnative操作予算が上限を超えています。");
            MetadataQueryBytes = nextBytes;
            MetadataNativeOperations = nextOperations;
        }

        public void ReserveFile(long sourceBytes, long destinationBytes)
        {
            LogicalBytes = checked(LogicalBytes + sourceBytes);
            DestinationBytes = checked(DestinationBytes + destinationBytes);
            PlannedIoBytes = checked(checked(6 * LogicalBytes) + checked(3 * DestinationBytes));
            if (sourceBytes < 0 || destinationBytes < 0 || LogicalBytes > limits.MaximumLogicalBytes || PlannedIoBytes > limits.MaximumIoBytes)
                throw new IOException("コピー容量または全読込み・書込み予算が上限を超えています。");
        }

        public void ReserveStreamLayout(FolderCopyStreamLayout layout)
        {
            StreamDescriptors = checked(StreamDescriptors + layout.Streams.Count);
            StreamNameCharacters = checked(StreamNameCharacters + layout.Streams.Sum(stream => stream.Name.Length));
            if (StreamDescriptors > limits.MaximumStreamDescriptors || StreamNameCharacters > limits.MaximumStreamNameCharacters)
                throw new IOException("全コピー計画のストリーム件数または名前文字数が上限を超えています。");
        }
        public void AddRead(int count)
        {
            if (checked(ReadBytes + WriteBytes + count) > limits.MaximumIoBytes) throw new IOException("コピーの全読込み・書込み予算を超えました。");
            ReadBytes = checked(ReadBytes + count);
        }
        public void AddWrite(int count)
        {
            if (checked(ReadBytes + WriteBytes + count) > limits.MaximumIoBytes) throw new IOException("コピーの全読込み・書込み予算を超えました。");
            WriteBytes = checked(WriteBytes + count);
        }
    }
}
