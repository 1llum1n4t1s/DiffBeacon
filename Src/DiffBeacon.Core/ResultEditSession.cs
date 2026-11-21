namespace DiffBeacon.Core;

public enum EditOrigin { Keyboard, Paste, Cut, Drop, Replace, Rectangle, Composition, Programmatic }
public sealed record EditGroupBegin(long ExpectedVersion, string GroupId, bool MergeWithPrevious,
    EditOrigin Origin, Position BeforeCursor, Selection BeforeSelection);
public sealed record InsertPrimitive(int Order, int OwnerId, Position At, string Inserted,
    IReadOnlyList<string> ExactEols, long ExpectedVersion);
public sealed record DeletePrimitive(int Order, int OwnerId, Position Start, Position End, string Removed,
    IReadOnlyList<string> ExactEols, long ExpectedVersion);

public sealed partial class ResultEditSession
{
    internal sealed record Frame(int Start, int OldEnd, int NewEnd);
    private sealed record History(ResultLineBuffer Before, ResultLineBuffer After, string GroupId, EditOrigin Origin,
        long BeforeIdentity, long AfterIdentity, IReadOnlyList<Frame> Frames);
    private long _identity, _nextIdentity, _savedIdentity;
    public uint SavedRevision { get; private set; }
    public bool IsModified => _identity != _savedIdentity;
    private List<History> _history = [];
    private int _position;
    private readonly int _maximumGroups;
    private readonly long _maximumCharacters;
    public ResultLineBuffer Current { get; private set; }
    public int UndoCount => _position;
    public int RedoCount => _history.Count - _position;
    public long HistoryCharacters => _history.Sum(h => (long)h.Before.Text.Length + h.After.Text.Length);
    public ResultEditSession(ResultLineBuffer initial, int maximumGroups = 100,
        long maximumCharacters = 64L * 1024 * 1024)
    {
        if (maximumGroups < 1 || maximumCharacters < 0) throw new ArgumentOutOfRangeException();
        Current = initial; _maximumGroups = maximumGroups; _maximumCharacters = maximumCharacters;
    }
    public Candidate Begin(EditGroupBegin begin, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested(); Version(begin.ExpectedVersion);
        if (string.IsNullOrWhiteSpace(begin.GroupId) || !Enum.IsDefined(begin.Origin)
            ) throw new ArgumentException("group開始metadataが不正です。");
        Current.Validate(begin.BeforeCursor); Current.Validate(begin.BeforeSelection.Anchor); Current.Validate(begin.BeforeSelection.Active);
        var before = Current.With(Current.Lines, Current.Segments, Current.CurrentRevision,
            Current.Version, begin.BeforeCursor, begin.BeforeSelection);
        return new(this, before, begin, cancellation);
    }
    private void Version(long expected)
    { if (Current.Version != expected) throw new InvalidOperationException("古いversionです。"); }
    private void Publish(Candidate candidate, string exactText, Position cursor, Selection selection)
    {
        candidate.Check(); Version(candidate.Before.Version);
        if (candidate.Buffer.Text != exactText) throw new ArgumentException("確定本文が候補と不一致です。");
        candidate.Buffer.ValidateTextRanges();
        var after = candidate.Buffer.With(candidate.Buffer.Lines, candidate.Buffer.Segments,
            candidate.Buffer.CurrentRevision, checked(Current.Version + 1), cursor, selection);
        if (!candidate.HasMutation) { candidate.Close(); return; }
        var retained = _history.Take(_position).ToList();
        if (!candidate.HasPhysicalMutation) { candidate.Check(); Current = after; candidate.Close(); return; }
        long newIdentity = checked(_nextIdentity + 1);
        var h = new History(candidate.Before, after, candidate.BeginInfo.GroupId, candidate.BeginInfo.Origin,
            _identity, newIdentity, candidate.Frames.ToArray());
        if (candidate.BeginInfo.MergeWithPrevious && retained.Count > 0) h = h with { Before = retained[^1].Before,
                BeforeIdentity = retained[^1].BeforeIdentity, Frames = retained[^1].Frames.Concat(h.Frames).ToArray() };
        if (candidate.BeginInfo.MergeWithPrevious && retained.Count > 0)
            retained[^1] = h;
        else retained.Add(h);
        while (retained.Count > 1 && (retained.Count > _maximumGroups
            || retained.Sum(x => (long)x.Before.Text.Length + x.After.Text.Length) > _maximumCharacters))
            retained.RemoveAt(0);
        candidate.Check(); // 取消でpublished/historyを一部だけ更新しない。
        _history = retained; _position = retained.Count; // 準備済みlistを無割当で一括採用。
        _identity = _nextIdentity = newIdentity;
        Current = after; candidate.Close();
    }
    public bool Undo(long expectedVersion, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested(); Version(expectedVersion); if (_position == 0) return false;
        var entry = _history[_position - 1]; var target = entry.Before;
        var redoEntry = entry with { After = entry.After.With(entry.After.Lines, Current.Segments, entry.After.CurrentRevision,
            entry.After.Version, entry.After.Cursor, entry.After.Selection) };
        var prepared = target.With(target.Lines, target.Segments, checked(Current.CurrentRevision + (uint)entry.Frames.Count),
            checked(Current.Version + 1), target.Cursor, target.Selection); cancellation.ThrowIfCancellationRequested(); _history[_position - 1] = redoEntry; Current = prepared; _position--; _identity = entry.BeforeIdentity; return true;
    }
    public bool Redo(long expectedVersion, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested(); Version(expectedVersion); if (_position == _history.Count) return false;
        var entry = _history[_position]; var target = entry.After;
        uint revision = Current.CurrentRevision; var revisions = Current.Lines.Select(l => l.Revision).ToList();
        foreach (var f in entry.Frames)
        {
            revision = checked(revision + 1);
            revisions = revisions.Take(f.Start).Concat(Enumerable.Repeat(revision, f.NewEnd - f.Start + 1))
                .Concat(revisions.Skip(f.OldEnd + 1)).ToList();
        }
        if (revisions.Count != target.Lines.Count) throw new InvalidOperationException("physical replay frameが不一致です。");
        var prepared = target.With(target.Lines.Select((l, i) => l with { Revision = revisions[i] }), target.Segments, revision,
            checked(Current.Version + 1), target.Cursor, target.Selection); cancellation.ThrowIfCancellationRequested(); Current = prepared; _position++; _identity = entry.AfterIdentity; return true;
    }
    public ResultLineBuffer? HistoryTarget(bool redo, long expectedVersion)
    {
        Version(expectedVersion);
        return redo ? (_position == _history.Count ? null : _history[_position].After)
            : (_position == 0 ? null : _history[_position - 1].Before);
    }
    public object HistoryEvidence() => new { Position = _position, MaximumGroups = _maximumGroups, MaximumCharacters = _maximumCharacters,
        Identity = _identity, SavedIdentity = _savedIdentity, SavedRevision, IsModified,
        Entries = _history.Select(h => new { h.Before, h.After, h.GroupId, h.Origin, h.BeforeIdentity, h.AfterIdentity, h.Frames }).ToArray() };
    public sealed class Candidate
    {
        private readonly ResultEditSession _session;
        private readonly CancellationToken _cancellation;
        private int _order;
        private bool _closed;
        internal ResultLineBuffer Before { get; }
        internal EditGroupBegin BeginInfo { get; }
        public ResultLineBuffer Buffer { get; private set; }
        internal bool HasMutation { get; private set; }
        private readonly List<Frame> _frames = [];
        private readonly bool _internalOperation = false; // ChooseはSpliceで通常edit hookを呼ばない。
        private int? _replacementOwner;
        private (int Owner, int Offset)? _lastPartialDeletion;
        internal List<Frame> Frames => _frames;
        internal bool HasPhysicalMutation => _frames.Count != 0;
        internal Candidate(ResultEditSession session, ResultLineBuffer before, EditGroupBegin begin, CancellationToken cancellation)
        { _session = session; Before = before; Buffer = before; BeginInfo = begin; _cancellation = cancellation; }
        internal void Check()
        {
            if (_closed) throw new InvalidOperationException("終了したgroupです。");
            _cancellation.ThrowIfCancellationRequested();
            if (_session.Current.Version != Before.Version) throw new InvalidOperationException("候補が古くなりました。");
        }
        internal void Close() => _closed = true;
        public void Cancel() => Close();
        private void Prepare(int order, int ownerId, Position p, long version)
        {
            Check(); if (order != _order || version != Before.Version) throw new InvalidOperationException("操作順/versionが不正です。");
            Buffer.Validate(p); int index = _replacementOwner ?? Buffer.SegmentIndexAtPosition(p);
            if (!_internalOperation && (index < 0 || Buffer.Segments[index].Id != ownerId))
                throw new ArgumentException("明示ownerと元intervalが不一致です。");
        }
        private static void Eols(string raw, IReadOnlyList<string> exact)
        { if (!ResultLineBuffer.Split(raw).Select(p => p.Eol).Where(e => e != "").SequenceEqual(exact))
            throw new ArgumentException("EOL列が不一致です。"); }
        private static ResultSegment Edited(ResultSegment s) => s.IsTracked ? s with { State = SegmentState.Edited } : s;
        public void Insert(InsertPrimitive operation)
        {
            try { InsertCore(operation); } catch { Close(); throw; }
        }
        public void InsertAfterPartialDeletion(InsertPrimitive operation)
        {
            if (_lastPartialDeletion is not { } previous || previous.Owner != operation.OwnerId || previous.Offset != Buffer.Offset(operation.At))
                throw new InvalidOperationException("replacementのowner文脈がありません。");
            int index = Buffer.Segments.ToList().FindIndex(s => s.Id == previous.Owner);
            if (index < 0) throw new InvalidOperationException("replacement ownerがありません。");
            _replacementOwner = index;
            try { Insert(operation); } finally { _replacementOwner = null; _lastPartialDeletion = null; }
        }
        public void InsertIntoExplicitEmptySegment(InsertPrimitive operation)
        {
            int index = Buffer.Segments.ToList().FindIndex(s => s.Id == operation.OwnerId);
            if (index < 0) throw new ArgumentException("明示empty ownerがありません。");
            var s = Buffer.Segments[index];
            if (!s.Linked || s.State != SegmentState.Chosen || s.TextLength != 0 || s.TextStart != Buffer.Offset(operation.At)
                || !s.OwnsTrailingEmptyLine || s.SourceLineCounts.Sum() <= 0) throw new ArgumentException("明示realempty ownerが不正です。");
            _replacementOwner = index;
            try { Insert(operation); } finally { _replacementOwner = null; }
        }
        private void InsertCore(InsertPrimitive operation)
        {
            Prepare(operation.Order, operation.OwnerId, operation.At, operation.ExpectedVersion);
            Eols(operation.Inserted, operation.ExactEols); var b = Buffer; int index = _replacementOwner ?? b.SegmentIndexAtPosition(operation.At);
            if (!_internalOperation && b.Segments[index].IsPlaceholder) throw new InvalidOperationException("linked placeholderへの挿入です。");
            if (operation.Inserted.Length == 0) { _order++; return; }
            var old = b.Lines[operation.At.Line]; uint revision = checked(b.CurrentRevision + 1);
            // CR/LFは原操作文字列そのまま。既存行EOLと接する場合も実物理構造を再構成。
            var rebuilt = ResultLineBuffer.Split(old.Content[..operation.At.Column] + operation.Inserted + old.Raw[operation.At.Column..]);
            // Splitは必ずEOF行を返す。既存EOL後のsynthetic EOFは次の既存行へ任せる。
            var changed = rebuilt.Select(p => new PhysicalLine(p.Content, p.Eol, revision, old.Origins)).ToList();
            if (old.Eol != "") changed.RemoveAt(changed.Count - 1);
            int count = changed.Count - 1;
            var lines = b.Lines.Take(operation.At.Line).Concat(changed).Concat(b.Lines.Skip(operation.At.Line + 1));
            var segments = b.Segments.ToArray();
            if (!_internalOperation) { segments[index] = Edited(segments[index]) with { LineCount = checked(segments[index].LineCount + count) };
                for (int i = index + 1; i < segments.Length; i++) segments[i] = segments[i] with { StartLine = checked(segments[i].StartLine + count) }; }
            if (!_internalOperation)
            {
                segments[index] = segments[index] with { TextLength = checked(segments[index].TextLength + operation.Inserted.Length) };
                for (int i = index + 1; i < segments.Length; i++) segments[i] = segments[i] with { TextStart = checked(segments[i].TextStart + operation.Inserted.Length) };
            }
            var next = b.With(lines, segments, revision); Check(); _frames.Add(new(operation.At.Line, operation.At.Line, operation.At.Line + count)); Buffer = next; HasMutation = true; _order++;
        }
        public void Delete(DeletePrimitive operation)
        {
            try { DeleteCore(operation); } catch { Close(); throw; }
        }
        private void DeleteCore(DeletePrimitive operation)
        {
            Prepare(operation.Order, operation.OwnerId, operation.Start, operation.ExpectedVersion);
            var b = Buffer; int start = b.Offset(operation.Start), end = b.Offset(operation.End);
            if (end < start || b.Text[start..end] != operation.Removed) throw new ArgumentException("削除原文が不一致です。");
            Eols(operation.Removed, operation.ExactEols);
            bool whole = operation.Start.Column == 0 && operation.End.Column == 0 && operation.Start.Line < operation.End.Line;
            if (!_internalOperation && b.TouchesPlaceholder(start, end, whole)) throw new InvalidOperationException("linked placeholderへの削除/結合です。");
            if (start == end) { _order++; return; }
            uint revision = checked(b.CurrentRevision + 1);
            var first = b.Lines[operation.Start.Line]; var last = b.Lines[operation.End.Line];
            var origins = whole ? last.Origins : ResultLineBuffer.Freeze(first.Origins.Concat(last.Origins).Distinct());
            var survivor = new PhysicalLine(first.Content[..operation.Start.Column] + last.Content[operation.End.Column..], last.Eol, revision, origins);
            var lines = b.Lines.Take(operation.Start.Line).Append(survivor).Concat(b.Lines.Skip(operation.End.Line + 1));
            int count = operation.End.Line - operation.Start.Line;
            var segments = b.Segments.ToArray();
            if (!_internalOperation && count > 0) AdjustDeleted(segments, whole ? operation.Start.Line : operation.Start.Line + 1, count);
            if (!_internalOperation && !whole)
            {
                int i = IndexAt(segments, operation.Start.Line); if (i >= 0) segments[i] = Edited(segments[i]);
            }
            if (!_internalOperation) AdjustTextDeleted(segments, start, end);
            var next = b.With(lines, segments, revision); Check(); _frames.Add(new(operation.Start.Line, operation.End.Line, operation.Start.Line)); Buffer = next; HasMutation = true; _order++;
            _lastPartialDeletion = !whole ? (operation.OwnerId, start) : null;
        }
        private static int IndexAt(ResultSegment[] segments, int line)
        {
            for (int i = segments.Length - 1; i >= 0; i--)
            { var s = segments[i]; if (s.StartLine > line || s.LineCount == 0) continue;
                return line < s.StartLine + s.LineCount ? i : -1; }
            return -1;
        }
        private static void AdjustTextDeleted(ResultSegment[] segments, int start, int end)
        {
            int count = end - start;
            for (int i = 0; i < segments.Length; i++)
            {
                var s = segments[i]; int oldEnd = checked(s.TextStart + s.TextLength);
                int overlap = Math.Max(0, Math.Min(oldEnd, end) - Math.Max(s.TextStart, start));
                int shifted = s.TextStart >= end ? s.TextStart - count : s.TextStart > start ? start : s.TextStart;
                segments[i] = (overlap > 0 ? Edited(s) : s) with { TextStart = shifted, TextLength = s.TextLength - overlap };
            }
        }
        private static void AdjustDeleted(ResultSegment[] segments, int begin, int count)
        {
            int end = checked(begin + count - 1);
            for (int i = 0; i < segments.Length; i++)
            {
                var s = segments[i]; int segmentEnd = checked(s.StartLine + s.LineCount - 1);
                if (s.LineCount > 0 && segmentEnd < begin) continue;
                if (s.StartLine > end) { segments[i] = s with { StartLine = s.StartLine - count }; continue; }
                if (s.LineCount <= 0) { if (s.StartLine >= begin) segments[i] = s with { StartLine = begin }; continue; }
                int overlap = Math.Min(segmentEnd, end) - Math.Max(s.StartLine, begin) + 1;
                if (overlap > 0) s = Edited(s) with { LineCount = s.LineCount - overlap };
                if (s.StartLine >= begin) s = s with { StartLine = begin };
                segments[i] = s;
            }
        }
        public void End(string exactFinalText, Position cursor, Selection selection)
        {
            try
            {
                // 改行のない末尾本文を全削除しても、残った実空行の所有者を失わない。
                // 同じgroupの置換途中やghost zero-anchorは新しい空行にしない。
                if (_lastPartialDeletion is { } deletion && deletion.Offset == Buffer.Text.Length
                    && Buffer.Lines[^1] is { Content.Length: 0, Eol.Length: 0 } last
                    && last.Origins.Any(origin => origin.OwnerId == deletion.Owner)
                    && Before.Segments.Any(segment => segment.Id == deletion.Owner && segment.TextLength > 0))
                {
                    Buffer = Buffer.With(Buffer.Lines, Buffer.Segments.Select(segment =>
                        segment.Id == deletion.Owner && segment.TextLength == 0
                            ? segment with { OwnsTrailingEmptyLine = true } : segment), Buffer.CurrentRevision);
                }
                _session.Publish(this, exactFinalText, cursor, selection);
            }
            catch { Close(); throw; }
        }
        internal bool Choose(ChoiceDescriptor descriptor, IReadOnlyList<int> panes)
        {
            Check(); int index = Buffer.Segments.ToList().FindIndex(s => s.Id == descriptor.SegmentId);
            if (index < 0) throw new ArgumentException("元diffとsegmentのlinkがありません。");
            var b = Buffer; var seg = b.Segments[index];
            if (!seg.Linked || seg.OriginalDiffId != descriptor.OriginalDiffIndex) throw new ArgumentException("元diff linkが切れています。");
            b.ValidateTextRanges();
            var sources = panes.Select(descriptor.Source).ToArray();
            string text = panes.Count == 0 ? descriptor.PendingDisplayText : string.Concat(sources.Select(s => s.RawText));
            int newCount = panes.Count == 0 ? descriptor.PendingDisplayLineCount : checked(sources.Sum(s => s.RealLineCount));
            int insertionOffset = seg.TextStart, removedEnd = checked(seg.TextStart + seg.TextLength);
            uint revision = b.CurrentRevision; var physical = b.Lines.ToList();
            // raw範囲を置換する。CR+LFが隣境界で結合しても、元apparent数を削除長にしない。
            if (seg.TextLength > 0) physical = Splice(physical, insertionOffset, removedEnd, "", ref revision);
            if (text.Length > 0) physical = Splice(physical, insertionOffset, insertionOffset, text, ref revision);
            var segments = b.Segments.ToArray();
            int delta = checked(newCount - seg.LineCount), textDelta = checked(text.Length - seg.TextLength);
            var last = sources.SelectMany(s => s.Lines).LastOrDefault();
            segments[index] = seg with { State = panes.Count == 0 ? descriptor.PendingState : SegmentState.Chosen,
                SourcePanes = ResultLineBuffer.Freeze(panes), SourceLineCounts = ResultLineBuffer.Freeze(sources.Select(s => s.RealLineCount)),
                LineCount = newCount, TextLength = text.Length, OwnsTrailingEmptyLine = last is { Content.Length: 0, Eol.Length: 0 }, BaselineRevision = revision,
                BlockText = panes.Count == 0 ? descriptor.FallbackText : "", BlockLineCount = panes.Count == 0 ? descriptor.FallbackLineCount : 0 };
            for (int i = index + 1; i < segments.Length; i++) segments[i] = segments[i] with {
                StartLine = checked(segments[i].StartLine + delta), TextStart = checked(segments[i].TextStart + textDelta) };
            var spans = new List<(int Start, int End, Origin Origin)>(); int relative = insertionOffset;
            foreach (var source in sources) foreach (var line in source.Lines)
            {
                int end = checked(relative + line.Content.Length + line.Eol.Length);
                spans.Add((relative, end, new(seg.Id, source.Pane, line.SourceLineIndex, relative == end))); relative = end;
            }
            if (panes.Count == 0) spans.Add((insertionOffset, insertionOffset + text.Length, new(seg.Id, -1, -1, false)));
            int offset = 0;
            foreach (var old in b.Lines)
            {
                int end = offset + old.Raw.Length;
                foreach (var origin in old.Origins.Where(o => o.OwnerId != seg.Id))
                {
                    var owner = b.Segments.Single(s => s.Id == origin.OwnerId);
                    int a = Math.Max(offset, owner.TextStart), z = Math.Min(end, checked(owner.TextStart + owner.TextLength));
                    if (a < z || a == z && origin.RealEmpty)
                    {
                        if (a < insertionOffset) spans.Add((a, Math.Min(z, insertionOffset), origin));
                        if (z > removedEnd || a == z && a >= removedEnd) spans.Add((Math.Max(a, removedEnd) + textDelta, z + textDelta, origin));
                    }
                }
                offset = end;
            }
            int total = physical.Sum(l => l.Raw.Length); offset = 0; var lines = new List<PhysicalLine>();
            foreach (var line in physical)
            {
                int end = offset + line.Raw.Length;
                var origins = spans.Where(s => s.Start < end && s.End > offset || s.Start == s.End && s.Start >= offset && (s.Start < end || end == total && line.Eol.Length == 0)).Select(s => s.Origin);
                lines.Add(line with { Origins = ResultLineBuffer.Freeze(origins.Distinct()) }); offset = end;
            }
            var next = b.With(lines, segments, revision); next.ValidateTextRanges(); Check(); Buffer = next; HasMutation = true; return true;
        }
        internal void ChooseAutomatic(ChoiceDescriptor descriptor, IReadOnlyList<int> panes)
        {
            if (!Choose(descriptor, panes)) throw new InvalidOperationException("自動マージの元差分が古くなりました。");
            Check();
            Buffer = Buffer.With(Buffer.Lines, Buffer.Segments.Select(segment => segment.Id == descriptor.SegmentId
                ? segment with { State = SegmentState.Auto } : segment), Buffer.CurrentRevision);
        }
        private List<PhysicalLine> Splice(List<PhysicalLine> before, int start, int end, string inserted, ref uint revision)
        {
            string raw = string.Concat(before.Select(l => l.Raw));
            var split = ResultLineBuffer.Split(raw[..start] + inserted + raw[end..]);
            static int Row(IEnumerable<(string Content, string Eol)> lines, int offset)
            {
                int i = 0; foreach (var l in lines) { int size = l.Content.Length + l.Eol.Length; if (offset < size || l.Eol.Length == 0) return i; offset -= size; i++; } throw new ArgumentException("raw座標が範囲外です。");
            }
            int oldStart = Row(before.Select(l => (l.Content, l.Eol)), start), oldEnd = Row(before.Select(l => (l.Content, l.Eol)), end);
            int newStart = Row(split, start), newEnd = oldEnd + split.Count - before.Count;
            int affected = Math.Min(oldStart, newStart); revision = checked(revision + 1);
            uint stampedRevision = revision;
            var result = split.Select((l, i) => new PhysicalLine(l.Content, l.Eol,
                i < affected ? before[i].Revision : i <= newEnd ? stampedRevision : before[i - split.Count + before.Count].Revision,
                ResultLineBuffer.Freeze(Array.Empty<Origin>()))).ToList();
            _frames.Add(new(affected, oldEnd, newEnd)); return result;
        }
    }
}
