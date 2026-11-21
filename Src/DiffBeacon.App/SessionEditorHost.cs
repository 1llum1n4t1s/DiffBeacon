using DiffBeacon.Core;


namespace DiffBeacon.App;

// UI thread上でserializedに使う。暫定427 Candidateは公開せず、原文を順序どおり適用する。
public sealed partial class SessionEditorHost : IResultEditorHost
{
    private readonly object _gate = new();
    public ResultEditSession Session { get; }
    public bool IsReadOnly { get; set; }
    public CancellationToken Cancellation { get; set; }
    public Action? BeforePublish { get; set; }
    // UIが選択した元diffだけを渡す。null時はzero anchorへownerを推測しない。
    public int? ActiveOriginalDiffIndex { get; set; }
    internal HostProbeObserver? ProbeObserver { get; set; }
    internal bool ProbeObserverFailed { get; private set; }
    public sealed record Resolution(long Version, int PrecedingCount, int Start, int End,
        string ProgressiveText, int OwnerId, IReadOnlyList<ResultSegment> Segments);
    public SessionEditorHost(ResultLineBuffer initial) : this(new ResultEditSession(initial)) { }
    public SessionEditorHost(ResultEditSession session)
    { ArgumentNullException.ThrowIfNull(session); Session = session; }
    public EditorSnapshot Capture()
    {
        lock (_gate) return Snapshot(Session.Current);
    }
    private static EditorSnapshot Snapshot(ResultLineBuffer b) => new(b.Text, b.Version,
        new(b.CharacterOffset(b.Cursor), b.CharacterOffset(b.Selection.Anchor), b.CharacterOffset(b.Selection.Active)));
    private static (Position Cursor, Selection Selection) Positions(ResultLineBuffer b, EditorSelection s)
        => (b.PositionAtOffset(s.Caret), new(b.PositionAtOffset(s.Start), b.PositionAtOffset(s.End)));
    private void Writable()
    { Cancellation.ThrowIfCancellationRequested(); if (IsReadOnly) throw new InvalidOperationException("host読取り専用です。"); }
    private ResultEditSession.Candidate Begin(EditorSnapshot original, string id, EditOrigin origin)
    {
        Writable();
        if (original.Version != Session.Current.Version || original.Text != Session.Current.Text)
            throw new InvalidOperationException("表示原文/versionが古いです。");
        var p = Positions(Session.Current, original.Selection);
        return Session.Begin(new(original.Version, id, false, origin, p.Cursor, p.Selection), Cancellation);
    }
    private int Owner(ResultLineBuffer b, int start, int end)
    {
        var p = b.PositionAtOffset(start); var q = b.PositionAtOffset(end);
        if (end < start) throw new ArgumentException("終了座標が開始前です。");
        int index = b.SegmentIndexAtPosition(p);
        if (start == end && ActiveOriginalDiffIndex is { } diff && Choices is { } catalog)
        {
            var descriptor = catalog.At(diff); int selected = b.Segments.ToList().FindIndex(s => s.Id == descriptor.SegmentId);
            if (selected >= 0) { var s = b.Segments[selected];
                if (s.Linked && s.OriginalDiffId == diff && s.State == SegmentState.Chosen && s.TextStart == start && s.TextLength == 0
                    && s.OwnsTrailingEmptyLine && s.SourceLineCounts.Sum() > 0) index = selected; }
        }
        if (index < 0) throw new ArgumentException("物理行にownerがありません。見かけzero-anchorは編集ownerに推測しません。");
        bool whole = p.Column == 0 && q.Column == 0 && p.Line < q.Line;
        if (b.TouchesPlaceholder(start, end, whole)) throw new InvalidOperationException("linked placeholderへの編集/結合です。");
        return b.Segments[index].Id;
    }
    private static IReadOnlyList<string> Eols(string raw)
        => ResultLineBuffer.ParseLineEndings(raw);
    private void Replay(ResultEditSession.Candidate candidate, IReadOnlyList<ExactEdit> edits, long version)
    {
        int primitiveOrder = 0;
        for (int i = 0; i < edits.Count; i++)
        {
            Writable(); var e = edits[i];
            if (e.Order != i || e.Removed is null || e.Inserted is null) throw new ArgumentException("exact edit順序/文字列が不正です。");
            var b = candidate.Buffer; int end = checked(e.Start + e.Removed.Length);
            int owner = Owner(b, e.Start, end);
            if (owner != e.OwnerId || b.Text.Substring(e.Start, e.Removed.Length) != e.Removed)
                throw new ArgumentException("exact removed/明示ownerが不一致です。");
            if (e.Removed.Length != 0)
                candidate.Delete(new(primitiveOrder++, owner, b.PositionAtOffset(e.Start), b.PositionAtOffset(end),
                    e.Removed, Eols(e.Removed), version));
            if (e.Inserted.Length != 0)
            {
                // 全行削除でsurvivorが隣segmentへ移る場合、挿入ownerは削除後の物理intervalから解決する。
                var oldStart = b.PositionAtOffset(e.Start); var oldEnd = b.PositionAtOffset(end);
                bool partialReplacement = e.Removed.Length != 0 && !(oldStart.Column == 0 && oldEnd.Column == 0 && oldStart.Line < oldEnd.Line);
                b = candidate.Buffer;
                if (partialReplacement) candidate.InsertAfterPartialDeletion(new(primitiveOrder++, e.OwnerId, b.PositionAtOffset(e.Start), e.Inserted, Eols(e.Inserted), version));
                else { owner = Owner(b, e.Start, e.Start); var operation = new InsertPrimitive(primitiveOrder++, owner, b.PositionAtOffset(e.Start), e.Inserted, Eols(e.Inserted), version);
                    int physical = b.SegmentIndexAtPosition(operation.At);
                    if (physical < 0 || b.Segments[physical].Id != owner) candidate.InsertIntoExplicitEmptySegment(operation);
                    else candidate.Insert(operation); }
            }
        }
    }
    public bool TryHostResolveOwner(EditorSnapshot original, IReadOnlyList<ExactEdit> precedingEdits,
        int start, int end, out int ownerId, out string reason)
    {
        lock (_gate)
        {
            ownerId = -1; reason = ""; ResultEditSession.Candidate? c = null;
            try
            {
                c = Begin(original, "owner-probe", EditOrigin.Programmatic);
                Replay(c, precedingEdits, original.Version);
                ownerId = Owner(c.Buffer, start, end);
                if (ProbeObserver is { } probe)
                {
                    try { probe.Resolved(new(original.Version, precedingEdits.Count, start, end, c.Buffer.Text, ownerId, c.Buffer.Segments)); }
                    catch (Exception) { ProbeObserverFailed = true; }
                }
                return true;
            }
            catch (Exception e) { reason = e.GetType().Name + ": " + e.Message; return false; }
            finally { c?.Cancel(); }
        }
    }
    private static EditOrigin Origin(EditorOrigin origin) => origin switch
    {
        EditorOrigin.Keyboard => EditOrigin.Keyboard, EditorOrigin.Paste => EditOrigin.Paste,
        EditorOrigin.Cut => EditOrigin.Cut, EditorOrigin.SelectedText => EditOrigin.Replace,
        EditorOrigin.Composition => EditOrigin.Composition, _ => throw new ArgumentException("originが不正です。")
    };
    public bool TryCommit(EditorTransaction transaction, out EditorSnapshot adopted, out string reason)
    {
        lock (_gate)
        {
            adopted = Snapshot(Session.Current); reason = ""; ResultEditSession.Candidate? c = null;
            try
            {
                int? activeDiff = ActiveOriginalDiffIndex;
                c = Begin(new(Session.Current.Text, transaction.ExpectedVersion, transaction.Before),
                    transaction.GroupId, Origin(transaction.Origin));
                Replay(c, transaction.Edits, transaction.ExpectedVersion);
                if (c.Buffer.Text != transaction.ExactFinalText) throw new ArgumentException("exact final textが候補と不一致です。");
                var p = Positions(c.Buffer, transaction.After);
                // 同一本文への置換はrevision/history/Redoを作らず、確定modelそのものを保持。
                bool noChange = c.Buffer.Text == Session.Current.Text;
                var prepared = noChange ? adopted : new EditorSnapshot(c.Buffer.Text,
                    checked(Session.Current.Version + 1), transaction.After);
                var probe = ProbeObserver; probe?.PrepareCommit(transaction);
                BeforePublish?.Invoke(); Writable();
                if (activeDiff != ActiveOriginalDiffIndex) throw new InvalidOperationException("編集ownerの選択文脈が古くなりました。");
                if (noChange) c.Cancel(); else c.End(transaction.ExactFinalText, p.Cursor, p.Selection);
                adopted = prepared;
                // 診断側の失敗で、既に採用した本文を失敗として返さない。
                if (probe is not null) { try { probe.Committed(transaction); } catch (Exception) { ProbeObserverFailed = true; } }
                return true;
            }
            catch (Exception e) { adopted = Snapshot(Session.Current); reason = e.GetType().Name + ": " + e.Message; return false; }
            finally { c?.Cancel(); }
        }
    }
    public bool TryUndo(long expectedVersion, out EditorSnapshot adopted, out string reason)
        => History(false, expectedVersion, out adopted, out reason);
    public bool TryRedo(long expectedVersion, out EditorSnapshot adopted, out string reason)
        => History(true, expectedVersion, out adopted, out reason);
    private bool History(bool redo, long expectedVersion, out EditorSnapshot adopted, out string reason)
    {
        lock (_gate)
        {
            adopted = Snapshot(Session.Current); reason = "";
            try
            {
                Writable();
                var target = Session.HistoryTarget(redo, expectedVersion);
                if (target is null) { reason = "履歴がありません。"; return false; }
                var prepared = Snapshot(target) with { Version = checked(Session.Current.Version + 1) };
                bool ok = redo ? Session.Redo(expectedVersion, Cancellation) : Session.Undo(expectedVersion, Cancellation);
                if (!ok) { reason = "履歴がありません。"; return false; }
                adopted = prepared; return true;
            }
            catch (Exception e) { reason = e.GetType().Name + ": " + e.Message; return false; }
        }
    }
}

// 製品の通常hostはobserverを持たない。記録容量の確保は公開前だけに行う。
internal abstract class HostProbeObserver
{
    internal virtual void PrepareCommit(EditorTransaction transaction) { }
    internal virtual void Committed(EditorTransaction transaction) { }
    internal virtual void Resolved(SessionEditorHost.Resolution resolution) { }
}
