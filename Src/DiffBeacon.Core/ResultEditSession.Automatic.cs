namespace DiffBeacon.Core;

public sealed partial class ResultEditSession
{
    // 原典のBeginUndoGroup/FlushUndoGroupと同じく、全自動採用を一つの履歴へ公開する。
    public void ApplyAutomaticChoices(ChoiceCatalog catalog, IReadOnlyList<ChoiceRequest> requests,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(requests);
        cancellation.ThrowIfCancellationRequested();
        var plan = new List<(ChoiceDescriptor Descriptor, int[] Panes)>(); var ids = new HashSet<int>();
        foreach (var request in requests)
        {
            cancellation.ThrowIfCancellationRequested(); Version(request.ExpectedVersion);
            var descriptor = catalog.At(request.OriginalDiffIndex); var panes = request.OrderedPanes.ToArray();
            if (request.MergeWithPrevious || panes.Length != 1 || descriptor.Sources.Count != 3
                || descriptor.PendingState != SegmentState.Unresolved || !ids.Add(request.OriginalDiffIndex))
                throw new ArgumentException("自動採用には祖先付き三者の未解決差分を一度ずつ指定してください。");
            descriptor.Source(panes[0]);
            var segment = Current.Segments.Single(s => s.Id == descriptor.SegmentId);
            if (!segment.IsPlaceholder || segment.State != SegmentState.Unresolved)
                throw new ArgumentException("確定済みの差分や競合は自動採用できません。");
            plan.Add((descriptor, panes));
        }
        if (plan.Count == 0) return;
        var candidate = Begin(new(Current.Version, "automatic-merge", false, EditOrigin.Programmatic,
            Current.Cursor, Current.Selection), cancellation);
        try
        {
            foreach (var item in plan) { cancellation.ThrowIfCancellationRequested(); candidate.ChooseAutomatic(item.Descriptor, item.Panes); }
            var cursor = new Position(0, 0);
            candidate.End(candidate.Buffer.Text, cursor, new(cursor, cursor));
        }
        finally { candidate.Cancel(); }
    }
}
