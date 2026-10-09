
namespace DiffBeacon.Core
{
// Native GetPaneApparentLinesText の ghost 除外後の行を明示入力する境界。
// SourceLine の空文字+空EOLは実在する空行。ghost はこの列に入れない。
public sealed class ApparentSource
{
    public int Pane { get; }
    public int RealLineCount { get; }
    public IReadOnlyList<SourceLine> Lines { get; }
    public string RawText { get; }
    public ApparentSource(int pane, int realLineCount, IEnumerable<SourceLine> lines)
    {
        Lines = ResultLineBuffer.Freeze(lines); Pane = pane; RealLineCount = realLineCount;
        if (pane is < 0 or > 2 || realLineCount < 0 || Lines.Count != realLineCount
            || Lines.Any(l => l.SourceLineIndex < 0 || l.Content.IndexOfAny(['\r', '\n']) >= 0 || l.Eol is not ("" or "\n" or "\r" or "\r\n")))
            throw new ArgumentException("明示apparent source metadataが不正です。");
        RawText = string.Concat(Lines.Select(l => l.Content + l.Eol));
    }
}
public sealed class ChoiceDescriptor
{
    public int SegmentId { get; }
    public int OriginalDiffIndex { get; }
    public IReadOnlyList<ApparentSource> Sources { get; }
    public SegmentState PendingState { get; }
    public string PendingDisplayText { get; }
    public int PendingDisplayLineCount { get; }
    public string FallbackText { get; }
    public int FallbackLineCount { get; }
    public ChoiceDescriptor(int segmentId, int originalDiffIndex, IEnumerable<ApparentSource> sources,
        SegmentState pendingState, string pendingDisplayText, int pendingDisplayLineCount, string fallbackText, int fallbackLineCount)
    {
        Sources = ResultLineBuffer.Freeze(sources); SegmentId = segmentId; OriginalDiffIndex = originalDiffIndex;
        if (segmentId < 0 || originalDiffIndex < 0 || Sources.Select(s => s.Pane).Distinct().Count() != Sources.Count
            || pendingState is not (SegmentState.Conflict or SegmentState.Unresolved) || pendingDisplayLineCount != 1
            || fallbackLineCount < 0 || pendingDisplayText.Length == 0 || fallbackLineCount == 0 && fallbackText.Length != 0)
            throw new ArgumentException("元diff / pending / fallback metadataが不正です。");
        PendingState = pendingState; PendingDisplayText = pendingDisplayText; PendingDisplayLineCount = pendingDisplayLineCount;
        FallbackText = fallbackText; FallbackLineCount = fallbackLineCount;
    }
    public ApparentSource Source(int pane) => Sources.SingleOrDefault(s => s.Pane == pane) ?? throw new ArgumentException("元source paneがありません。");
}
public sealed class ChoiceCatalog
{
    public IReadOnlyList<ChoiceDescriptor> Descriptors { get; }
    public int CurrentOriginalDiffCount { get; }
    public ChoiceCatalog(IEnumerable<ChoiceDescriptor> descriptors, int currentOriginalDiffCount)
    {
        Descriptors = ResultLineBuffer.Freeze(descriptors); CurrentOriginalDiffCount = currentOriginalDiffCount;
        if (currentOriginalDiffCount < 0 || Descriptors.Select(d => d.SegmentId).Distinct().Count() != Descriptors.Count
            || Descriptors.Select(d => d.OriginalDiffIndex).Distinct().Count() != Descriptors.Count)
            throw new ArgumentException("元diff tableが不正です。");
    }
    public ChoiceDescriptor At(int actualDiffIndex)
    {
        if (actualDiffIndex < 0 || actualDiffIndex >= CurrentOriginalDiffCount) throw new ArgumentException("現行の元diff listにありません。");
        return Descriptors.SingleOrDefault(d => d.OriginalDiffIndex == actualDiffIndex) ?? throw new ArgumentException("元diff→segment linkがありません。");
    }
}
public sealed record ChoiceRequest(long ExpectedVersion, int OriginalDiffIndex, IReadOnlyList<int> OrderedPanes,
    bool MergeWithPrevious = false);
public sealed partial class ResultEditSession
{
    public sealed class SaveCapture
    {
        internal ResultEditSession Owner { get; }
        internal long Identity { get; }
        public ResultLineBuffer Buffer { get; }
        internal SaveCapture(ResultEditSession owner, long identity, ResultLineBuffer buffer)
        { Owner = owner; Identity = identity; Buffer = buffer; }
    }
    public SaveCapture CaptureSave(long expectedVersion)
    { Version(expectedVersion); return new(this, _identity, Current); }
    public bool CompleteSave(SaveCapture capture, bool publishedSuccessfully)
    {
        if (!ReferenceEquals(capture.Owner, this)) throw new ArgumentException("別sessionのsave tokenです。");
        if (!publishedSuccessfully || capture.Buffer.Version != Current.Version || capture.Identity != _identity) return false;
        _savedIdentity = _identity; SavedRevision = Current.CurrentRevision; return true;
    }
    public Candidate PrepareChoice(ChoiceCatalog catalog, ChoiceRequest request, CancellationToken cancellation, out bool replaced)
    {
        Version(request.ExpectedVersion); var descriptor = catalog.At(request.OriginalDiffIndex);
        var panes = request.OrderedPanes.ToArray(); foreach (int p in panes) descriptor.Source(p);
        var c = Begin(new(request.ExpectedVersion, "source-choose", request.MergeWithPrevious,
            EditOrigin.Programmatic, Current.Cursor, Current.Selection), cancellation);
        try { replaced = c.Choose(descriptor, panes); return c; } catch { c.Cancel(); throw; }
    }
    public ChoiceRequest ToggleRequest(ChoiceCatalog catalog, long expectedVersion, int originalDiffIndex, int pane, bool mergeWithPrevious = false)
    {
        Version(expectedVersion); var descriptor = catalog.At(originalDiffIndex); descriptor.Source(pane);
        var seg = Current.Segments.Single(s => s.Id == descriptor.SegmentId);
        var panes = seg.State is SegmentState.Auto or SegmentState.Chosen ? seg.SourcePanes.ToList() : [];
        int i = panes.IndexOf(pane); if (i < 0) panes.Add(pane); else panes.RemoveAt(i);
        return new(expectedVersion, originalDiffIndex, panes.ToArray(), mergeWithPrevious);
    }
}
}
