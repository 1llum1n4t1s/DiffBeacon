using System.Collections.ObjectModel;

namespace DiffBeacon.Core;

public enum SegmentState { Common, Auto, Chosen, Conflict, Unresolved, Edited }
public readonly record struct Position(int Line, int Column);
public readonly record struct Selection(Position Anchor, Position Active);
public sealed record Origin(int OwnerId, int SourcePane, int SourceLine, bool RealEmpty);
public sealed record SourceLine(string Content, string Eol, int SourceLineIndex);
public sealed record SourcePart(int Pane, IReadOnlyList<SourceLine> Lines);
public sealed record SegmentSeed(int Id, int OriginalDiffId, SegmentState State,
    int OriginalStartLine, int OriginalLineCount, uint BaselineRevision,
    IReadOnlyList<SourcePart> Parts, bool Linked = false);
public sealed record PhysicalLine(string Content, string Eol, uint Revision, IReadOnlyList<Origin> Origins)
{
    public string Raw => Content + Eol;
}
public sealed record ResultSegment(int Id, int OriginalDiffId, SegmentState State,
    int StartLine, int LineCount, uint BaselineRevision, IReadOnlyList<int> SourcePanes,
    IReadOnlyList<int> SourceLineCounts, bool Linked)
{
    // 原sourceの宣言行数とは独立した、採用済みraw本文の正確な範囲。
    public int TextStart { get; init; }
    public int TextLength { get; init; }
    public bool OwnsTrailingEmptyLine { get; init; }
    public string BlockText { get; init; } = "";
    public int BlockLineCount { get; init; }
    public bool IsPlaceholder => Linked && OriginalDiffId >= 0 && State is SegmentState.Conflict or SegmentState.Unresolved;
    public bool IsTracked => OriginalDiffId >= 0 || State is SegmentState.Conflict or SegmentState.Unresolved;
}

// 物理行と元apparent intervalを混同しない。全公開collectionは防御copy。
public sealed class ResultLineBuffer
{
    public IReadOnlyList<PhysicalLine> Lines { get; }
    public IReadOnlyList<ResultSegment> Segments { get; }
    public uint CurrentRevision { get; }
    public string Text { get; }
    public Position Cursor { get; }
    public Selection Selection { get; }
    public long Version { get; }
    internal ResultLineBuffer(IEnumerable<PhysicalLine> lines, IEnumerable<ResultSegment> segments,
        uint revision, long version, Position cursor, Selection selection)
    {
        Lines = Freeze(lines.Select(l => l with { Origins = Freeze(l.Origins) }));
        Segments = Freeze(segments.Select(s => s with {
            SourcePanes = Freeze(s.SourcePanes), SourceLineCounts = Freeze(s.SourceLineCounts) }));
        if (Lines.Count == 0 || Lines.Any(l => l.Eol is not ("" or "\n" or "\r" or "\r\n")
            || l.Content.IndexOfAny(['\r', '\n']) >= 0) || Lines[^1].Eol != ""
            || Lines.Take(Lines.Count - 1).Any(l => l.Eol == ""))
            throw new ArgumentException("物理行構造が不正です。");
        var ids = new HashSet<int>(); var previous = 0;
        foreach (var s in Segments)
        {
            if (!ids.Add(s.Id) || s.Id < 0 || s.OriginalDiffId < -1 || !Enum.IsDefined(s.State)
                || s.StartLine != previous || s.LineCount < 0
                || s.SourcePanes.Count != s.SourceLineCounts.Count || s.SourceLineCounts.Any(c => c < 0))
                throw new ArgumentException("元segment metadataが不正です。");
            previous = checked(s.StartLine + s.LineCount);
        }
        CurrentRevision = revision; Version = version; Cursor = cursor; Selection = selection;
        Text = string.Concat(Lines.Select(l => l.Raw));
        Validate(cursor); Validate(selection.Anchor); Validate(selection.Active);
    }
    public static ResultLineBuffer FromRawSources(IEnumerable<SegmentSeed> seeds, uint currentRevision = 0)
    {
        var segments = new List<ResultSegment>();
        var spans = new List<(int Start, int End, Origin Origin)>();
        var text = new System.Text.StringBuilder();
        foreach (var s in seeds)
        {
            var parts = s.Parts.ToArray();
            if (parts.Any(p => p.Pane is < 0 or > 2)) throw new ArgumentException("source paneが不正です。");
            int sourceStart = text.Length;
            segments.Add(new(s.Id, s.OriginalDiffId, s.State, s.OriginalStartLine,
                s.OriginalLineCount, s.BaselineRevision, Freeze(parts.Select(p => p.Pane)),
                Freeze(parts.Select(p => p.Lines.Count)), s.Linked));
            foreach (var p in parts) foreach (var line in p.Lines)
            {
                if (line.Content.IndexOfAny(['\r', '\n']) >= 0 || line.Eol is not ("" or "\n" or "\r" or "\r\n"))
                    throw new ArgumentException("source line/EOLが不正です。");
                int start = text.Length; text.Append(line.Content); text.Append(line.Eol);
                spans.Add((start, text.Length, new(s.Id, p.Pane, line.SourceLineIndex,
                    line.Content.Length == 0 && line.Eol.Length == 0)));
            }
            var last = parts.SelectMany(p => p.Lines).LastOrDefault();
            segments[^1] = segments[^1] with { TextStart = sourceStart, TextLength = text.Length - sourceStart,
                OwnsTrailingEmptyLine = last is { Content.Length: 0, Eol.Length: 0 } };
        }
        var raw = text.ToString(); var result = new List<PhysicalLine>(); int offset = 0;
        foreach (var (content, eol) in Split(raw))
        {
            int end = offset + content.Length + eol.Length;
            var origins = spans.Where(s => s.Start < end && s.End > offset
                || s.Start == s.End && s.Start >= offset && (s.Start < end || end == raw.Length && eol == ""))
                .Select(s => s.Origin).Distinct();
            result.Add(new(content, eol, currentRevision, Freeze(origins))); offset = end;
        }
        return new(result, segments, currentRevision, 0, new(0, 0), new(new(0, 0), new(0, 0)));
    }
    public int SegmentIndexAt(int line)
    {
        for (int i = Segments.Count - 1; i >= 0; i--)
        {
            var s = Segments[i]; if (s.StartLine > line || s.LineCount == 0) continue;
            return line < s.StartLine + s.LineCount ? i : -1;
        }
        return -1;
    }
    public int SegmentIndexAtPosition(Position position)
    {
        int offset = Offset(position);
        // 行末のEOL前に追記するときは、直前の実本文ownerを維持する。
        int lookup = position.Column > 0 && offset < Text.Length && Text[offset] is '\r' or '\n' ? offset - 1 : offset;
        for (int i = Segments.Count - 1; i >= 0; i--)
        {
            var s = Segments[i];
            if (s.TextLength > 0 && lookup >= s.TextStart && lookup < (long)s.TextStart + s.TextLength) return i;
            if (offset == Text.Length && offset == (long)s.TextStart + s.TextLength
                && (s.TextLength > 0 && Lines[position.Line].Content.Length > 0 || s.OwnsTrailingEmptyLine)) return i;
        }
        return -1;
    }
    public bool TouchesPlaceholder(int start, int end, bool wholeLines)
        => Segments.Any(s => s.IsPlaceholder && (start < (long)s.TextStart + s.TextLength && end > s.TextStart
            || start == end && start >= s.TextStart && start < (long)s.TextStart + s.TextLength
            || !wholeLines && start < end && end == s.TextStart));
    internal void ValidateTextRanges()
    {
        int prior = 0;
        foreach (var s in Segments)
        { if (s.TextStart != prior || s.TextLength < 0) throw new InvalidOperationException("raw segment範囲が不正です。"); prior = checked(prior + s.TextLength); }
        if (prior != Text.Length) throw new InvalidOperationException("raw segment範囲が本文を覆っていません。");
    }
    public string Marker(int line)
    {
        Validate(new(line, 0)); int i = SegmentIndexAt(line); if (i < 0) return "";
        var s = Segments[i]; if (s.State == SegmentState.Common) return "";
        if (s.State is SegmentState.Conflict or SegmentState.Unresolved) return "?";
        if (s.State == SegmentState.Edited && Lines[line].Revision > s.BaselineRevision) return "m";
        int rel = line - s.StartLine;
        for (int n = 0; n < s.SourcePanes.Count; n++)
        { if (rel < s.SourceLineCounts[n]) return (s.SourcePanes[n] + 1).ToString(); rel -= s.SourceLineCounts[n]; }
        return s.SourcePanes.Count > 0 ? (s.SourcePanes[0] + 1).ToString() : s.State == SegmentState.Edited ? "m" : "";
    }
    internal void Validate(Position p)
    { if (p.Line < 0 || p.Line >= Lines.Count || p.Column < 0 || p.Column > Lines[p.Line].Content.Length)
        throw new ArgumentException("物理座標が不正です。"); }
    internal int Offset(Position p)
    { Validate(p); int offset = p.Column; for (int i = 0; i < p.Line; i++) offset += Lines[i].Raw.Length; return offset; }
    public int CharacterOffset(Position p) => Offset(p);
    public Position PositionAtOffset(int offset)
    {
        if (offset < 0 || offset > Text.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        for (int i = 0; i < Lines.Count; i++)
        {
            var l = Lines[i];
            if (offset <= l.Content.Length) return new(i, offset);
            if (offset < l.Raw.Length) throw new ArgumentException("EOL内部は編集座標にできません。");
            offset -= l.Raw.Length;
        }
        throw new ArgumentOutOfRangeException(nameof(offset));
    }
    internal static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => new ReadOnlyCollection<T>(values.ToArray());
    internal static IReadOnlyList<(string Content, string Eol)> Split(string raw)
    {
        var result = new List<(string, string)>(); int start = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] is not ('\r' or '\n')) continue;
            int size = raw[i] == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n' ? 2 : 1;
            result.Add((raw[start..i], raw.Substring(i, size))); i += size - 1; start = i + 1;
        }
        result.Add((raw[start..], "")); return result;
    }
    public static IReadOnlyList<string> ParseLineEndings(string raw) => Freeze(Split(raw).Select(p => p.Eol).Where(e => e.Length != 0));
    internal ResultLineBuffer With(IEnumerable<PhysicalLine> lines, IEnumerable<ResultSegment> segments,
        uint revision, long? version = null, Position? cursor = null, Selection? selection = null)
        => new(lines, segments, revision, version ?? Version, cursor ?? new(0, 0), selection ?? new(new(0, 0), new(0, 0)));
}
