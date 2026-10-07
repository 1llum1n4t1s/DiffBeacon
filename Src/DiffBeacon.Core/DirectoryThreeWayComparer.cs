namespace DiffBeacon.Core;

public static partial class DirectoryComparer
{
    public static Task<DirectoryComparisonResult> CompareThreeWayAsync(string leftPath, string middlePath, string rightPath,
        DirectoryComparisonOptions? options = null, CancellationToken cancellationToken = default)
        => CompareAsync(leftPath, middlePath, rightPath, options, cancellationToken);

    public static async Task<DirectoryComparisonResult> CompareAsync(string leftPath, string middlePath, string rightPath,
        DirectoryComparisonOptions? options, CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (options.MaximumEntries < 1 || options.MaximumDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "フォルダー比較の項目数・深度の上限は正数で指定してください。");
        if (!Enum.IsDefined(options.Mode)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaximumContentBytes < 1) throw new ArgumentOutOfRangeException(nameof(options));
        options = options with
        {
            ExcludePatterns = Array.AsReadOnly(options.ExcludePatterns.ToArray()),
            TextOptions = options.TextOptions is null ? null : options.TextOptions with
            { SubstitutionRules = Array.AsReadOnly(options.TextOptions.SubstitutionRules.ToArray()) }
        };
        leftPath = NormalizeRootPath(leftPath);
        middlePath = NormalizeRootPath(middlePath);
        rightPath = NormalizeRootPath(rightPath);
        foreach (var root in new[] { leftPath, middlePath, rightPath })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("三者フォルダー比較のルートにリンクを指定できません。");
            options.EntryTypeValidator?.Invoke(root, DirectoryEntryKind.Directory);
        }
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var left = Scan(leftPath, options, comparer, cancellationToken);
        var middle = Scan(middlePath, options, comparer, cancellationToken);
        var right = Scan(rightPath, options, comparer, cancellationToken);
        var relativePaths = left.Keys.Union(middle.Keys, comparer).Union(right.Keys, comparer).Order(comparer).ToArray();
        if (relativePaths.Length > options.MaximumEntries)
            throw new InvalidDataException("フォルダー比較の項目数の上限を超えています。");
        var nodes = new Dictionary<string, DirectoryEntry>(comparer);
        var childrenByParent = new Dictionary<string, List<string>>(comparer);
        foreach (var relative in relativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            left.TryGetValue(relative, out var l);
            middle.TryGetValue(relative, out var m);
            right.TryGetValue(relative, out var r);
            var states = new[] { l, m, r };
            var active = states.Select(state => state is { IsFiltered: false } ? state : null).ToArray();
            var presence = Presence(states);
            var activePresence = Presence(active);
            var kind = active.FirstOrDefault(state => state is not null)?.Kind
                ?? states.First(state => state is not null)!.Kind;
            var error = states.FirstOrDefault(state => state?.Error is not null)?.Error;
            var ml = InitialPair(DirectorySide.Middle, DirectorySide.Left, m, l);
            var mr = InitialPair(DirectorySide.Middle, DirectorySide.Right, m, r);
            var lr = InitialPair(DirectorySide.Left, DirectorySide.Right, l, r);
            DirectoryThreeWayClassification? classification = null;
            var status = InitialOverall(activePresence);
            if (error is not null) status = DirectoryDifferenceKind.Error;
            else if (activePresence == DirectoryPresence.None) status = DirectoryDifferenceKind.Uncompared;
            else
            {
                try
                {
                    if (active.Where(state => state is not null).Select(state => state!.Kind).Distinct().Count() > 1)
                    {
                        status = DirectoryDifferenceKind.TypeConflict;
                        // 種別衝突は現行adapterのまま保持し、同種のpairだけ比較する。
                        ml = await ComparePairAsync(ml, active[1], active[0], options, cancellationToken).ConfigureAwait(false);
                        mr = await ComparePairAsync(mr, active[1], active[2], options, cancellationToken).ConfigureAwait(false);
                        lr = await ComparePairAsync(lr, active[0], active[2], options, cancellationToken).ConfigureAwait(false);
                        classification = LeafClassification(ml.Status, mr.Status, lr.Status);
                    }
                    else if (kind == DirectoryEntryKind.File && options.Mode == DirectoryComparisonMode.Content)
                    {
                        var content = await CompareThreeWayContentAsync(active[0], active[1], active[2],
                            options.TextOptions, options.MaximumContentBytes, cancellationToken).ConfigureAwait(false);
                        ml = ml with { Status = content.MiddleLeftEqual ? DirectoryDifferenceKind.Equal : DirectoryDifferenceKind.Modified };
                        mr = mr with { Status = content.MiddleRightEqual ? DirectoryDifferenceKind.Equal : DirectoryDifferenceKind.Modified };
                        lr = lr with { Status = content.LeftRightEqual ? DirectoryDifferenceKind.Equal : DirectoryDifferenceKind.Modified };
                        // 不在補正は分類を再計算しない。補正前SAMEならDIFFALLのままDIFFになる。
                        classification = content.HasSignificantDifferences
                            ? LeafClassification(ml.Status, mr.Status, lr.Status) : DirectoryThreeWayClassification.AllChanged;
                        status = content.HasSignificantDifferences || activePresence != DirectoryPresence.All
                            ? InitialDifference(activePresence) : DirectoryDifferenceKind.Equal;
                        if (status == DirectoryDifferenceKind.Equal) classification = null;
                    }
                    else if (kind != DirectoryEntryKind.Directory)
                    {
                        if (kind == DirectoryEntryKind.File && options.Mode == DirectoryComparisonMode.Hash)
                        {
                            // Hashも各側を一度だけ読み、凍結したdigestを全pairで共有する。
                            var hashes = new byte[3][];
                            for (var side = 0; side < active.Length; side++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                hashes[side] = active[side] is null ? []
                                    : await HashAsync(active[side]!.Path, cancellationToken).ConfigureAwait(false);
                            }
                            DirectoryPairComparison HashPair(DirectoryPairComparison pair, int first, int second) =>
                                active[first] is null || active[second] is null ? pair : pair with
                                { Status = active[first]!.Size == active[second]!.Size && hashes[first].AsSpan().SequenceEqual(hashes[second])
                                    ? DirectoryDifferenceKind.Equal : DirectoryDifferenceKind.Modified };
                            ml = HashPair(ml, 1, 0);
                            mr = HashPair(mr, 1, 2);
                            lr = HashPair(lr, 0, 2);
                        }
                        else
                        {
                            ml = await ComparePairAsync(ml, active[1], active[0], options, cancellationToken).ConfigureAwait(false);
                            mr = await ComparePairAsync(mr, active[1], active[2], options, cancellationToken).ConfigureAwait(false);
                            lr = await ComparePairAsync(lr, active[0], active[2], options, cancellationToken).ConfigureAwait(false);
                        }
                        status = ml.Status == DirectoryDifferenceKind.Equal && mr.Status == DirectoryDifferenceKind.Equal
                            && lr.Status == DirectoryDifferenceKind.Equal ? DirectoryDifferenceKind.Equal : InitialDifference(activePresence);
                        classification = status == DirectoryDifferenceKind.Equal ? null : LeafClassification(ml.Status, mr.Status, lr.Status);
                    }
                }
                catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException
                    or System.Text.DecoderFallbackException)
                {
                    status = DirectoryDifferenceKind.Error;
                    error = failure.Message;
                    ml = ml with { Status = DirectoryDifferenceKind.Error, Error = error };
                    mr = mr with { Status = DirectoryDifferenceKind.Error, Error = error };
                    lr = lr with { Status = DirectoryDifferenceKind.Error, Error = error };
                    classification = null;
                }
            }
            nodes.Add(relative, new(relative, l?.Path, r?.Path, kind, status, l?.Size, r?.Size, error)
            {
                MiddlePath = m?.Path, MiddleSize = m?.Size, LeftState = l, MiddleState = m, RightState = r,
                ThreeWay = new(presence, ml, mr, lr, classification)
            });
            var separator = relative.LastIndexOf('/');
            var parent = separator < 0 ? "" : relative[..separator];
            if (!childrenByParent.TryGetValue(parent, out var children)) childrenByParent[parent] = children = [];
            children.Add(relative);
        }
        foreach (var relative in relativePaths.OrderByDescending(path => path.Count(character => character == '/')))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = nodes[relative];
            var children = childrenByParent.TryGetValue(relative, out var childPaths)
                ? childPaths.Order(comparer).Select(path => nodes[path]).ToArray() : [];
            var visible = children.Where(child => !child.IsFiltered).ToArray();
            var error = entry.Error ?? visible.FirstOrDefault(child => child.Status == DirectoryDifferenceKind.Error)?.Error;
            var status = entry.Kind;
            var threeWay = entry.ThreeWay!;
            var states = new[] { entry.LeftState, entry.MiddleState, entry.RightState };
            var active = states.Where(state => state is { IsFiltered: false }).ToArray();
            if (active.Length > 0 && active.All(state => state is { Kind: DirectoryEntryKind.Directory, ScanState: DirectoryScanState.Scanned }))
            {
                status = error is not null ? DirectoryDifferenceKind.Error
                    : active.Length != 3 || visible.Any(child => child.Status != DirectoryDifferenceKind.Equal)
                        ? InitialDifference(Presence(states.Select(state => state is { IsFiltered: false } ? state : null).ToArray()))
                        : DirectoryDifferenceKind.Equal;
                threeWay = threeWay with
                {
                    MiddleLeft = AggregatePair(threeWay.MiddleLeft, entry.MiddleState, entry.LeftState, children, child => child.ThreeWay!.MiddleLeft),
                    MiddleRight = AggregatePair(threeWay.MiddleRight, entry.MiddleState, entry.RightState, children, child => child.ThreeWay!.MiddleRight),
                    LeftRight = AggregatePair(threeWay.LeftRight, entry.LeftState, entry.RightState, children, child => child.ThreeWay!.LeftRight)
                };
            }
            if (error is not null) { status = DirectoryDifferenceKind.Error; threeWay = threeWay with { Classification = null }; }
            else if (entry.EntryKind == DirectoryEntryKind.Directory && status is not (DirectoryDifferenceKind.Equal or DirectoryDifferenceKind.Uncompared))
                threeWay = threeWay with { Classification = DirectorySideMapping.AggregateClassification(threeWay.Presence, children, cancellationToken) };
            nodes[relative] = entry with { Kind = status, Error = error, ThreeWay = threeWay, Children = Array.AsReadOnly(children) };
        }
        var allEntries = relativePaths.Select(path => nodes[path]).ToArray();
        var roots = childrenByParent.TryGetValue("", out var rootPaths)
            ? rootPaths.Order(comparer).Select(path => nodes[path]).ToArray() : [];
        return new(leftPath, rightPath, Array.AsReadOnly(allEntries.Where(entry => options.ShowFiltered || !entry.IsFiltered).ToArray()))
        {
            MiddlePath = middlePath, AllEntries = Array.AsReadOnly(allEntries), RootNodes = Array.AsReadOnly(roots), Options = options
        };
    }

    private static DirectoryPresence Presence(DirectorySideSnapshot?[] states) =>
        (states[0] is null ? DirectoryPresence.None : DirectoryPresence.Left)
        | (states[1] is null ? DirectoryPresence.None : DirectoryPresence.Middle)
        | (states[2] is null ? DirectoryPresence.None : DirectoryPresence.Right);

    private static DirectoryDifferenceKind InitialOverall(DirectoryPresence presence) => presence == DirectoryPresence.All
        ? DirectoryDifferenceKind.Uncompared : InitialDifference(presence);

    private static DirectoryDifferenceKind InitialDifference(DirectoryPresence presence) => presence switch
    {
        DirectoryPresence.Left => DirectoryDifferenceKind.LeftOnly,
        DirectoryPresence.Middle => DirectoryDifferenceKind.MiddleOnly,
        DirectoryPresence.Right => DirectoryDifferenceKind.RightOnly,
        DirectoryPresence.None => DirectoryDifferenceKind.Uncompared,
        _ => DirectoryDifferenceKind.Modified
    };

    private static DirectoryPairComparison InitialPair(DirectorySide firstSide, DirectorySide secondSide,
        DirectorySideSnapshot? first, DirectorySideSnapshot? second)
    {
        var error = first?.Error ?? second?.Error;
        if (error is not null) return new(firstSide, secondSide, DirectoryDifferenceKind.Error, error);
        var a = first is { IsFiltered: false } ? first : null;
        var b = second is { IsFiltered: false } ? second : null;
        return new(firstSide, secondSide, a is null && b is null ? DirectoryDifferenceKind.Uncompared
            : a is null ? DirectoryDifferenceKind.RightOnly : b is null ? DirectoryDifferenceKind.LeftOnly
            : a.Kind != b.Kind ? DirectoryDifferenceKind.TypeConflict : DirectoryDifferenceKind.Uncompared);
    }

    private static DirectoryThreeWayClassification LeafClassification(DirectoryDifferenceKind ml, DirectoryDifferenceKind mr,
        DirectoryDifferenceKind lr) => mr == DirectoryDifferenceKind.Equal ? DirectoryThreeWayClassification.OnlyLeft
        : lr == DirectoryDifferenceKind.Equal ? DirectoryThreeWayClassification.OnlyMiddle
        : ml == DirectoryDifferenceKind.Equal ? DirectoryThreeWayClassification.OnlyRight : DirectoryThreeWayClassification.AllChanged;

    private static async Task<DirectoryPairComparison> ComparePairAsync(DirectoryPairComparison pair,
        DirectorySideSnapshot? first, DirectorySideSnapshot? second, DirectoryComparisonOptions options, CancellationToken token)
    {
        if (first is null || second is null || first.Kind != second.Kind || first.Kind == DirectoryEntryKind.Directory) return pair;
        bool equal;
        if (first.Kind == DirectoryEntryKind.SymbolicLink) equal = first.LinkTarget == second.LinkTarget;
        else if (options.Mode == DirectoryComparisonMode.TimeAndSize) equal = first.Size == second.Size && first.LastWriteTimeUtc == second.LastWriteTimeUtc;
        else if (options.Mode == DirectoryComparisonMode.Content)
        {
            var content = await CompareThreeWayContentAsync(first, second, null, options.TextOptions, options.MaximumContentBytes, token).ConfigureAwait(false);
            equal = content.MiddleLeftEqual;
        }
        else if (first.Size != second.Size) equal = false;
        else
        {
            var a = await HashAsync(first.Path, token).ConfigureAwait(false);
            var b = await HashAsync(second.Path, token).ConfigureAwait(false);
            equal = a.AsSpan().SequenceEqual(b);
        }
        return pair with { Status = equal ? DirectoryDifferenceKind.Equal : DirectoryDifferenceKind.Modified };
    }

    private static DirectoryPairComparison AggregatePair(DirectoryPairComparison pair, DirectorySideSnapshot? first,
        DirectorySideSnapshot? second, DirectoryEntry[] children, Func<DirectoryEntry, DirectoryPairComparison> select)
    {
        if (first is not { IsFiltered: false, Kind: DirectoryEntryKind.Directory, ScanState: DirectoryScanState.Scanned }
            || second is not { IsFiltered: false, Kind: DirectoryEntryKind.Directory, ScanState: DirectoryScanState.Scanned }) return pair;
        var relevant = children.Where(child => DirectorySideMapping.GetState(child, pair.First) is { IsFiltered: false }
            || DirectorySideMapping.GetState(child, pair.Second) is { IsFiltered: false }).Select(select).ToArray();
        var error = relevant.FirstOrDefault(child => child.Status == DirectoryDifferenceKind.Error)?.Error;
        return pair with { Status = error is not null ? DirectoryDifferenceKind.Error
            : relevant.Any(child => child.Status != DirectoryDifferenceKind.Equal) ? DirectoryDifferenceKind.Modified : DirectoryDifferenceKind.Equal, Error = error };
    }
}
