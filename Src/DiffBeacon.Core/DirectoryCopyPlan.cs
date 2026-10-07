namespace DiffBeacon.Core;

public enum DirectoryCopyMode { All, DifferencesOnly }
public enum DirectoryCopyDirection { LeftToRight = 0, RightToLeft = 1, LeftToMiddle = 2, MiddleToLeft = 3, MiddleToRight = 4, RightToMiddle = 5 }
public sealed record DirectoryCopyCandidate(string RelativePath, DirectorySideSnapshot Source,
    DirectorySideSnapshot? Destination, bool ExpandPhysicalDirectory);
public sealed record DirectoryCopySelection(string SourceRoot, string DestinationRoot,
    DirectoryCopyDirection Direction, DirectoryCopyMode Mode, IReadOnlyList<string> SelectedPaths,
    IReadOnlyList<DirectoryCopyCandidate> Candidates);

public static class DirectoryCopyPlanner
{
    public static DirectoryCopySelection Create(DirectoryComparisonResult comparison,
        IEnumerable<string> selectedPaths, DirectoryCopyDirection direction, DirectoryCopyMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(selectedPaths);
        if (!Enum.IsDefined(direction) || !Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(direction));
        var (sourceSide, destinationSide) = DirectorySideMapping.GetSides(direction);
        var sourceRoot = DirectorySideMapping.GetRoot(comparison, sourceSide);
        var destinationRoot = DirectorySideMapping.GetRoot(comparison, destinationSide);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var selected = selectedPaths.Select(path => path.Replace('\\', '/')).Distinct(comparer).Order(comparer).ToArray();
        if (selected.Length == 0 || selected.Length > comparison.Options.MaximumEntries)
            throw new ArgumentException("コピー対象を比較結果から一つ以上選択してください。", nameof(selectedPaths));
        var nodes = comparison.AllEntries.ToDictionary(entry => entry.RelativePath, comparer);
        var candidates = new Dictionary<string, DirectoryCopyCandidate>(comparer);
        foreach (var path in selected)
        {
            ValidateRelativePath(path);
            if (!nodes.TryGetValue(path, out var node)) throw new ArgumentException("選択項目が比較結果にありません。", nameof(selectedPaths));
            var pending = new Stack<DirectoryEntry>();
            pending.Push(node);
            while (pending.TryPop(out var current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = DirectorySideMapping.GetState(current, sourceSide);
                var destination = DirectorySideMapping.GetState(current, destinationSide);
                if (source is null) continue;
                if (mode == DirectoryCopyMode.DifferencesOnly
                    && (source.IsFiltered || current.Status is DirectoryDifferenceKind.Equal or DirectoryDifferenceKind.Error)) continue;
                // 親の適格性を先に検査する。明示選択した子は祖先のゲートを通らない。
                if (source.Kind == DirectoryEntryKind.Directory && source.ScanState != DirectoryScanState.Unscanned
                    && current.Children.Count > 0)
                {
                    // 片側が空でもモデルに子があれば再帰し、元側不在の子を個別に除外する。
                    for (var index = current.Children.Count - 1; index >= 0; index--) pending.Push(current.Children[index]);
                }
                else
                {
                    ValidateRelativePath(current.RelativePath);
                    candidates[current.RelativePath] = new(current.RelativePath, source, destination,
                        source.Kind == DirectoryEntryKind.Directory);
                }
            }
        }
        // 物理終端ディレクトリと明示子を同時選択しても、同じコピー先は一度だけ扱う。
        var ordered = candidates.Values.OrderBy(candidate => candidate.RelativePath.Count(character => character == '/'))
            .ThenBy(candidate => candidate.RelativePath, comparer).ToArray();
        var result = new List<DirectoryCopyCandidate>();
        var physicalAncestors = new HashSet<string>(comparer);
        foreach (var candidate in ordered)
        {
            var relative = candidate.RelativePath;
            var covered = false;
            for (var separator = relative.LastIndexOf('/'); separator >= 0; separator = relative.LastIndexOf('/', separator - 1))
            {
                if (physicalAncestors.Contains(relative[..separator])) { covered = true; break; }
                if (separator == 0) break;
            }
            if (covered) continue;
            result.Add(candidate);
            if (candidate.ExpandPhysicalDirectory) physicalAncestors.Add(relative);
        }
        return new(sourceRoot, destinationRoot,
            direction, mode, Array.AsReadOnly(selected), result.AsReadOnly());
    }

    public static void ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
            throw new ArgumentException("コピー対象はルート内の相対パスで指定してください。", nameof(relativePath));
        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Any(segment => segment is "" or "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')))
            throw new ArgumentException("相対パスに空要素や . / .. は使用できません。", nameof(relativePath));
    }
}
