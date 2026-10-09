namespace DiffBeacon.Core;

public enum FourPaneInputMode { TwoWay, IndependentThreeWay, AncestorThreeWay }
public enum FourPaneRowOrigin { OriginalPhysical, AlignmentPadding, Ghost }
public sealed record FourPaneSourceRole(int NativePane, int LogicalPane);
public sealed record FourPaneSourceRow(string Content, string Eol, uint Flags, int SourceLineIndex,
    int? OriginalPhysicalIndex, int? RawIndex, FourPaneRowOrigin Origin);
public sealed record FourPaneAlignedSource(int LogicalPane, string OriginalText, bool? Utf8Bom,
    IReadOnlyList<FourPaneSourceRow> Rows);
public sealed record FourPaneSynchronizedDiff(int OriginalDiffId, FourPaneOriginalKind Kind,
    IReadOnlyList<int> OriginalBegin, IReadOnlyList<int> OriginalEnd, IReadOnlyList<int> FixedBegin,
    IReadOnlyList<int> FixedEnd, int ApparentBegin, int ApparentEnd, IReadOnlyList<int> Blank, bool AddedByEofFix);
public sealed record FourPaneSourceBinding(int SegmentId, int? OriginalDiffId, int LogicalPane,
    RawRange OriginalRawRange, RawRange ActualRawRange, int ApparentBegin, int ApparentEnd,
    ApparentSource Source, IReadOnlyList<int> RawIndices);
public sealed record FourPaneAlignedSegment(int SegmentId, int? OriginalDiffId, FourPaneOriginalKind Kind,
    int ApparentBegin, int ApparentEnd, IReadOnlyList<FourPaneSourceBinding> Sources, bool AddedTerminalCommon);
public sealed record FourPaneRawDescriptor(int Id, int? OriginalDiffId, FourPaneOriginalKind Kind,
    RawRange Base, RawRange Left, RawRange Right, string BaseText, string LeftText, string RightText,
    int? FallbackSource, string RawFallbackText);
public sealed record FourPaneGenerationMetadata(IReadOnlyList<FourPaneRawDescriptor> Descriptors,
    IReadOnlyList<FourPaneThreeSpan> Ranges, IReadOnlyList<int> RawLineCounts, IReadOnlyList<string> BoundaryReasons,
    int WorkUsed, long RetainedCharacters);
public sealed record FourPaneAlignmentResult(FourPaneInputMode Mode, IReadOnlyList<FourPaneSourceRole> Roles,
    IReadOnlyList<FourPaneAlignedSource> Sources, IReadOnlyList<FourPaneSynchronizedDiff> Diffs,
    IReadOnlyList<FourPaneAlignedSegment> Segments, FourPaneGenerationMetadata Generation, bool EofFixApplied)
{
    public bool HasAncestor => Mode == FourPaneInputMode.AncestorThreeWay;
    public int InputCount => Roles.Count;
}

// 生成器の既消費を引き継ぐ全段の勘定。単位は生成器と同じ保持量で、実RSSではない。
internal sealed class FourPanePipelineBudget
{
    internal int WorkUsed { get; private set; }
    internal long RetainedCharacters { get; private set; }
    private readonly int maximumWork;
    private readonly long maximumRetained;
    internal CancellationToken Token { get; }
    internal FourPanePipelineBudget(GenerationResult generation, int maximumWork, long maximumRetained, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumWork);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetained);
        this.maximumWork = Math.Min(maximumWork, GnuLineDiffer.MaximumWork); this.maximumRetained = maximumRetained; Token = token;
        WorkUsed = generation.WorkUsed; RetainedCharacters = generation.RetainedCharacters;
        if (WorkUsed < 0 || WorkUsed > this.maximumWork || RetainedCharacters < 0 || RetainedCharacters > maximumRetained)
            throw new ArgumentException("four-pane pipeline budget exhausted by generation");
        token.ThrowIfCancellationRequested();
    }
    internal void Spend(int count = 1)
    {
        Token.ThrowIfCancellationRequested();
        if (count < 0 || count > maximumWork - WorkUsed) throw new ArgumentException("four-pane pipeline work limit");
        WorkUsed += count;
    }
    internal void Reserve(long count)
    {
        Token.ThrowIfCancellationRequested();
        if (count < 0 || count > maximumRetained - RetainedCharacters) throw new ArgumentException("four-pane pipeline retained limit");
        RetainedCharacters += count;
    }
}

// 固定74c2aeのFixLastDiffRange/GetExtraLinesCounts/PrimeTextBuffersの移植。GPL-2.0-or-later。
internal static class FourPaneAlignment
{
    internal const uint GhostFlag = 0x00400000;
    private const uint DiffFlag = 0x00200000, TrivialFlag = 0x00800000;
    private sealed class Diff(int id, FourPaneOriginalKind kind, int[] begin, int[] end, bool added = false)
    {
        internal readonly int Id = id;
        internal readonly FourPaneOriginalKind Kind = kind;
        internal readonly int[] Begin = begin, End = end, OriginalBegin = (int[])begin.Clone(), OriginalEnd = (int[])end.Clone();
        internal readonly int[] Blank = Enumerable.Repeat(-1, begin.Length).ToArray();
        internal int Start, Finish;
        internal readonly bool Added = added;
    }
    private sealed record Physical(string Content, string Eol, int? RawIndex);

    internal static FourPaneAlignmentResult Create(GenerationResult input, FourPaneInputMode mode,
        bool ignoreBlankLines, IReadOnlyList<bool?>? suppliedBoms, FourPanePipelineBudget budget)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Enum.IsDefined(mode) || input.Fallback) throw new ArgumentException(input.FallbackReason ?? "invalid four-pane input mode");
        int n = mode == FourPaneInputMode.TwoWay ? 2 : 3;
        int[] logical = n == 2 ? [0, 2] : [0, 1, 2]; int commonPane = n == 2 ? 0 : 1;
        if (suppliedBoms is not null && suppliedBoms.Count != 3) throw new ArgumentException("BOM metadata needs logical slots0/1/2");
        budget.Reserve(checked(256L + 128L * input.Descriptors.Count + 96L * input.Ranges.Count));
        var descriptors = input.Descriptors.Select(x => new FourPaneRawDescriptor(x.Id, x.OriginalDiffId, x.Kind,
            x.Base, x.Left, x.Right, x.BaseText, x.LeftText, x.RightText, x.FallbackSource, x.RawFallbackText)).ToArray();
        var ranges = input.Ranges.Select(x => x with { }).ToArray();
        var originalCounts = input.SourceRawLineCounts.ToArray(); var reasons = input.BoundaryReasons.ToArray();
        if (originalCounts.Length != 3 || descriptors.Select(x => x.Id).Distinct().Count() != descriptors.Length)
            throw new ArgumentException("invalid generator metadata");
        if (!descriptors.Where(x => x.OriginalDiffId.HasValue).Select(x => x.OriginalDiffId!.Value).Order()
            .SequenceEqual(Enumerable.Range(0, ranges.Length))) throw new ArgumentException("original diff context incomplete");
        string[] texts = logical.Select(p => p == 0 ? input.Left : p == 1 ? input.Ancestor : input.Right).ToArray();
        if (n == 2 && (input.Ancestor.Length != 0 || originalCounts[1] != 0)) throw new ArgumentException("two-way has no ancestor slot");
        var physical = texts.Select(Parse).ToArray(); int[] rawCounts = physical.Select(x => x.Count(y => y.RawIndex.HasValue)).ToArray();
        if (!logical.Select(p => originalCounts[p]).SequenceEqual(rawCounts)) throw new ArgumentException("raw source count mismatch");
        var linked = new Dictionary<int, FourPaneRawDescriptor>();
        foreach (var d in descriptors)
        {
            if (d.Id < 0 || !Enum.IsDefined(d.Kind)) throw new ArgumentException("invalid raw descriptor");
            if (d.OriginalDiffId is int id) linked.Add(id, d);
            for (int p = 0; p < n; p++)
            {
                var r = Range(d, logical[p]); string expected = Text(d, logical[p]); budget.Spend();
                if (r.StartLine < 0 || r.LineCount < 0 || (long)r.StartLine + r.LineCount > rawCounts[p]) throw new ArgumentException("raw descriptor bounds");
                int length = 0;
                for (int i = r.StartLine; i < r.StartLine + r.LineCount; i++)
                {
                    var row = physical[p][i]; budget.Spend();
                    if (!expected.AsSpan(length).StartsWith(row.Content.AsSpan())) throw new ArgumentException("raw descriptor body mismatch");
                    length = checked(length + row.Content.Length);
                    if (!expected.AsSpan(length).StartsWith(row.Eol.AsSpan())) throw new ArgumentException("raw descriptor EOL mismatch");
                    length = checked(length + row.Eol.Length);
                }
                if (length != expected.Length) throw new ArgumentException("raw descriptor length mismatch");
            }
        }
        var diffs = ranges.Select((s, i) => new Diff(i, s.Kind,
            logical.Select(p => p == 0 ? s.LeftBegin : p == 1 ? s.BaseBegin : s.RightBegin).ToArray(),
            logical.Select(p => p == 0 ? s.LeftEnd : p == 1 ? s.BaseEnd : s.RightEnd).ToArray())).ToList();
        foreach (var d in diffs)
        {
            var original = linked[d.Id];
            if (d.Kind == FourPaneOriginalKind.Common || !Enum.IsDefined(d.Kind) || d.Kind != original.Kind
                || n == 2 && d.Kind is not (FourPaneOriginalKind.Conflict or FourPaneOriginalKind.Trivial)) throw new ArgumentException("invalid diff operation");
            for (int p = 0; p < n; p++)
            {
                budget.Spend(); var r = Range(original, logical[p]);
                if (r.StartLine != d.Begin[p] || r.LineCount != d.End[p] - d.Begin[p] + 1) throw new ArgumentException("range/descriptor mismatch");
            }
        }
        bool[] missing = texts.Select(x => x.Length > 0 && x[^1] is not ('\r' or '\n')).ToArray();
        bool fix = missing.Any(x => x != missing[0]);
        if (fix)
        {
            if (diffs.Count > 0) { for (int p = 0; p < n; p++) if (!missing[p]) diffs[^1].End[p]++; }
            else
            {
                int[] end = physical.Select(x => x.Count - 1).ToArray();
                int[] begin = Enumerable.Range(0, n).Select(p => missing[p] ? end[p] : end[p] + 1).ToArray();
                diffs.Add(new(0, ignoreBlankLines ? FourPaneOriginalKind.Trivial : FourPaneOriginalKind.Conflict, begin, end, true));
            }
        }
        int[] extras = new int[n], prior = new int[n];
        foreach (var d in diffs)
        {
            int width = 0;
            for (int p = 0; p < n; p++)
            {
                budget.Spend();
                if (d.Begin[p] < prior[p] || d.End[p] < d.Begin[p] - 1 || d.End[p] >= physical[p].Count || d.Begin[p] > physical[p].Count
                    || d.Begin[p] + extras[p] != d.Begin[0] + extras[0]) throw new ArgumentException("invalid synchronized source bounds");
                width = Math.Max(width, d.End[p] - d.Begin[p] + 1); prior[p] = d.End[p] + 1;
            }
            for (int p = 0; p < n; p++) extras[p] = checked(extras[p] + width - (d.End[p] - d.Begin[p] + 1));
        }
        int[] counts = physical.Select(x => x.Count).ToArray(); int[] newCounts = Enumerable.Range(0, n).Select(p => checked(counts[p] + extras[p])).ToArray();
        int height = newCounts.Max(); budget.Reserve(checked(96L * height * n));
        var rows = Enumerable.Range(0, n).Select(p => Enumerable.Range(0, height).Select(i => i < physical[p].Count
            ? new FourPaneSourceRow(physical[p][i].Content, physical[p][i].Eol, 0, i, i, physical[p][i].RawIndex, FourPaneRowOrigin.OriginalPhysical)
            : new FourPaneSourceRow("", "", 0, -2, null, null, FourPaneRowOrigin.AlignmentPadding)).ToArray()).ToArray();
        void Move(int p, int first, int last, int target)
        {
            int delta = target - first;
            if (delta > 0) for (int i = last; i >= first; i--) { budget.Spend(); rows[p][i + delta] = rows[p][i]; }
            else if (delta < 0) for (int i = first; i <= last; i++) { budget.Spend(); rows[p][i + delta] = rows[p][i]; }
        }
        uint Flags(int p, FourPaneOriginalKind kind) => ((p == 0 && kind == FourPaneOriginalKind.ThirdOnly)
            || (p == 2 && kind == FourPaneOriginalKind.FirstOnly) ? 0x02000000u : 0)
            | (kind switch { FourPaneOriginalKind.FirstOnly => 0x04000000u, FourPaneOriginalKind.SecondOnly => 0x08000000u, FourPaneOriginalKind.ThirdOnly => 0x10000000u, _ => 0u });
        for (int i = diffs.Count - 1; i >= 0; i--)
        {
            var d = diffs[i]; int[] lengths = new int[n]; int width = 0;
            for (int p = 0; p < n; p++)
            {
                int tail = counts[p] - d.End[p] - 1; Move(p, d.End[p] + 1, counts[p] - 1, newCounts[p] - tail);
                newCounts[p] -= tail; counts[p] -= tail; lengths[p] = d.End[p] - d.Begin[p] + 1; width = Math.Max(width, lengths[p]);
            }
            for (int p = 0; p < n; p++)
            {
                Move(p, d.Begin[p], d.End[p], newCounts[p] - width); int extra = width - lengths[p];
                for (int j = 1; j <= extra; j++) { budget.Spend(); rows[p][newCounts[p] - j] = new("", "", GhostFlag | Flags(p, d.Kind), -1, null, null, FourPaneRowOrigin.Ghost); }
                newCounts[p] -= width; counts[p] -= lengths[p];
            }
            d.Start = newCounts[0]; d.Finish = d.Start + width - 1;
            for (int p = 0; p < n; p++)
            {
                int extra = width - lengths[p]; d.Blank[p] = extra > 0 ? d.Finish + 1 - extra : -1;
                for (int j = d.Start; j <= d.Finish; j++)
                {
                    budget.Spend(); uint flags = rows[p][j].Flags;
                    if (d.Blank[p] == -1 || j < d.Blank[p]) flags |= (d.Kind == FourPaneOriginalKind.Trivial ? TrivialFlag : DiffFlag) | Flags(p, d.Kind);
                    else if (d.Kind == FourPaneOriginalKind.Trivial) flags |= TrivialFlag;
                    rows[p][j] = rows[p][j] with { Flags = flags };
                }
            }
        }
        var sources = new List<FourPaneAlignedSource>();
        for (int p = 0; p < n; p++)
        {
            int real = 0, original = 0;
            for (int i = 0; i < height; i++)
            {
                budget.Spend(); var row = rows[p][i];
                if (row.Origin == FourPaneRowOrigin.Ghost) continue;
                if (row.Origin == FourPaneRowOrigin.OriginalPhysical && row.OriginalPhysicalIndex != original++) throw new InvalidOperationException("source physical order lost");
                rows[p][i] = row with { SourceLineIndex = real++ };
            }
            if (original != physical[p].Count) throw new InvalidOperationException("source physical row lost");
            sources.Add(new(logical[p], texts[p], suppliedBoms?[logical[p]], ResultLineBuffer.Freeze(rows[p])));
        }
        var segments = new List<FourPaneAlignedSegment>(); var common = new Queue<FourPaneRawDescriptor>(descriptors.Where(x => x.OriginalDiffId is null));
        int nextId = descriptors.Length == 0 ? 0 : checked(descriptors.Max(x => x.Id) + 1), apparent = 0;
        FourPaneSourceBinding Bind(int id, int? diffId, int p, int begin, int end, RawRange original)
        {
            var selected = new List<SourceLine>(); var raw = new List<int>(); long length = 0;
            for (int i = begin; i <= end; i++)
            {
                budget.Spend(); var row = rows[p][i]; if (row.Origin == FourPaneRowOrigin.Ghost) continue;
                budget.Reserve(64); selected.Add(new(row.Content, row.Eol, row.SourceLineIndex)); length += row.Content.Length + row.Eol.Length;
                if (row.RawIndex is int index) raw.Add(index);
            }
            budget.Reserve(checked(length + 32L * selected.Count + 16L * raw.Count + 96));
            int start = raw.Count > 0 ? raw[0] : rawCounts[p];
            if (raw.Count == 0) for (int i = begin; i < height; i++) { budget.Spend(); if (rows[p][i].RawIndex is int index) { start = index; break; } }
            for (int i = 0; i < raw.Count; i++) if (raw[i] != start + i) throw new InvalidOperationException("noncontiguous raw source projection");
            return new(id, diffId, logical[p], original, new(start, raw.Count), begin, end,
                new(logical[p], selected.Count, selected), ResultLineBuffer.Freeze(raw));
        }
        void Common(int begin, int end, bool terminal)
        {
            if (begin > end || !rows[commonPane].Skip(begin).Take(end - begin + 1).Any(x => x.Origin != FourPaneRowOrigin.Ghost)) return;
            var original = common.Count > 0 ? common.Dequeue() : null; int id = original?.Id ?? nextId++;
            budget.Reserve(96 + 16L * n);
            segments.Add(new(id, null, FourPaneOriginalKind.Common, begin, end,
                ResultLineBuffer.Freeze(Enumerable.Range(0, n).Select(p => Bind(id, null, p, begin, end,
                    original is null ? new(rawCounts[p], 0) : Range(original, logical[p])))), terminal && original is null));
        }
        foreach (var d in diffs)
        {
            Common(apparent, d.Start - 1, false); linked.TryGetValue(d.Id, out var original); int id = original?.Id ?? nextId++;
            budget.Reserve(96 + 16L * n);
            segments.Add(new(id, d.Id, d.Kind, d.Start, d.Finish, ResultLineBuffer.Freeze(Enumerable.Range(0, n).Select(p =>
                Bind(id, d.Id, p, d.Start, d.Finish, original is null ? new(d.OriginalBegin[p], Math.Max(0, d.OriginalEnd[p] - d.OriginalBegin[p] + 1)) : Range(original, logical[p])))), false));
            apparent = d.Finish + 1;
        }
        Common(apparent, height - 1, true);
        for (int p = 0; p < n; p++)
        {
            int next = 0; foreach (var s in segments) foreach (int index in s.Sources[p].RawIndices) { budget.Spend(); if (index != next++) throw new InvalidOperationException("raw coverage order lost"); }
            if (next != rawCounts[p]) throw new InvalidOperationException("raw source coverage incomplete");
        }
        budget.Spend(); budget.Reserve(checked(256L + 32L * height * n + 160L * diffs.Count));
        return new(mode, ResultLineBuffer.Freeze(logical.Select((p, i) => new FourPaneSourceRole(i, p))), ResultLineBuffer.Freeze(sources),
            ResultLineBuffer.Freeze(diffs.Select(d => new FourPaneSynchronizedDiff(d.Id, d.Kind, ResultLineBuffer.Freeze(d.OriginalBegin), ResultLineBuffer.Freeze(d.OriginalEnd),
                ResultLineBuffer.Freeze(d.Begin), ResultLineBuffer.Freeze(d.End), d.Start, d.Finish, ResultLineBuffer.Freeze(d.Blank), d.Added))), ResultLineBuffer.Freeze(segments),
            new(ResultLineBuffer.Freeze(descriptors), ResultLineBuffer.Freeze(ranges), ResultLineBuffer.Freeze(originalCounts), ResultLineBuffer.Freeze(reasons), input.WorkUsed, input.RetainedCharacters), fix);

        List<Physical> Parse(string text)
        {
            var result = new List<Physical>(); int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                budget.Spend(); if (text[i] is not ('\r' or '\n')) continue; int end = i;
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { budget.Spend(); i++; }
                budget.Reserve(checked(i + 1L - start + 64)); result.Add(new(text[start..end], text[end..(i + 1)], result.Count)); start = i + 1;
            }
            budget.Reserve(checked(text.Length - (long)start + 64)); result.Add(new(text[start..], "", start < text.Length ? result.Count : null)); return result;
        }
    }
    private static RawRange Range(FourPaneRawDescriptor d, int pane) => pane == 0 ? d.Left : pane == 1 ? d.Base : d.Right;
    private static string Text(FourPaneRawDescriptor d, int pane) => pane == 0 ? d.LeftText : pane == 1 ? d.BaseText : d.RightText;
}
