using System.Text;

namespace DiffBeacon.Core;

public enum MergeSource { Left, Base, Right }
public enum MergeSectionState { Common, Automatic, Chosen, Unresolved, Conflict, Edited }

public sealed record MergeSection(int Id, int BaseStartLine, MergeSectionState State, string Text,
    string BaseText, string LeftText, string RightText, IReadOnlyList<MergeSource> Sources)
{
    public bool HasAncestor { get; init; } = true;
    public bool IsWhitespaceOnly { get; init; }
    public bool HasManualText { get; init; }
    public int PendingUnits { get; init; } = 1;
    public IReadOnlyList<string> LineSources { get; init; } = [];
    public IReadOnlyList<MergeMarkerSpan> PendingMarkers { get; init; } = [];
    public bool IsPending => State is MergeSectionState.Unresolved or MergeSectionState.Conflict;
    public bool IsDifference => State != MergeSectionState.Common;
    public string SourceText(MergeSource source) => source switch
    {
        MergeSource.Left => LeftText,
        MergeSource.Base when HasAncestor => BaseText,
        MergeSource.Right => RightText,
        _ => throw new ArgumentException("二者マージには祖先がありません。", nameof(source))
    };
}

public sealed record MergeSectionRange(MergeSection Section, int Start, int Length);
public sealed record MergeLineProvenance(int Line, string Source);
public sealed record MergeMarkerSpan(int Start, int Length, int Group);

public sealed class MergeSession
{
    private const int MaximumHistoryCharacters = 64 * 1024 * 1024;
    private readonly string _newline;
    private sealed record Snapshot(MergeSection[] Sections);
    private readonly List<Snapshot> _history = [];
    private int _historyIndex;
    private List<MergeSection> _sections;
    private int _nextId;
    public string Text { get; private set; }
    public IReadOnlyList<MergeSectionRange> Sections { get; private set; }
    public int UnresolvedCount => _sections.Where(section => section.IsPending).Sum(section => section.PendingUnits);
    public int ConflictCount => _sections.Where(section => section.State == MergeSectionState.Conflict).Sum(section => section.PendingUnits);
    public bool CanUndo => _historyIndex > 0;
    public bool CanRedo => _historyIndex + 1 < _history.Count;

    private MergeSession(IEnumerable<MergeSection> sections, string newline)
    {
        _sections = sections.Select(section => section.IsPending ? section with { PendingMarkers = GeneratedMarkers(section, newline) } : section).ToList();
        _newline = newline;
        _nextId = _sections.Count == 0 ? 0 : _sections.Max(section => section.Id) + 1;
        Text = ""; Sections = []; Reflow();
        _history.Add(new(_sections.ToArray()));
    }

    public static MergeSession CreateThreeWay(string ancestor, string left, string right,
        ComparisonOptions? options = null, bool autoResolve = true, CancellationToken token = default)
    {
        var result = ThreeWayMerger.Merge(ancestor, left, right, options, token);
        return new MergeSession(result.Sections.Select(section => !autoResolve && section.State == MergeSectionState.Automatic
            ? section with { State = MergeSectionState.Unresolved, Sources = [], Text = ConflictText(section.LeftText, section.BaseText, section.RightText, result.NewLine, true) }
            : section), result.NewLine);
    }

    public static MergeSession CreateTwoWay(string left, string right, ComparisonOptions? options = null,
        CancellationToken token = default)
    {
        var diff = TextDiffer.Compare(left, right, options, token);
        var lines = TextLines.Parse(left); var rightLines = TextLines.Parse(right);
        var sections = new List<MergeSection>(); var cursor = 0; var newline = TextLines.NewLine(left);
        void Common(int end)
        {
            if (end <= cursor) return;
            var text = TextLines.Join(lines.GetRange(cursor, end - cursor));
            sections.Add(new(sections.Count, cursor + 1, MergeSectionState.Common, text, "", text, text, [MergeSource.Left]) { HasAncestor = false });
        }
        foreach (var block in diff.Blocks)
        {
            token.ThrowIfCancellationRequested(); Common(block.LeftStart);
            var l = TextLines.Join(lines.GetRange(block.LeftStart, block.LeftCount));
            var r = TextLines.Join(rightLines.GetRange(block.RightStart, block.RightCount));
            sections.Add(new(sections.Count, block.LeftStart + 1, MergeSectionState.Conflict,
                ConflictText(l, "", r, newline, false), "", l, r, []) { HasAncestor = false, IsWhitespaceOnly = WhitespaceEqual(l, r) });
            cursor = block.LeftStart + block.LeftCount;
        }
        Common(lines.Count);
        return new MergeSession(sections, newline);
    }

    public void Choose(int sectionId, params MergeSource[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Length is < 1 or > 3 || sources.Distinct().Count() != sources.Length)
            throw new ArgumentException("採用するソースを重複なしで1～3個指定してください。", nameof(sources));
        var index = _sections.FindIndex(section => section.Id == sectionId);
        if (index < 0 || !_sections[index].IsDifference) throw new ArgumentException("採用できる差分がありません。", nameof(sectionId));
        var section = _sections[index]; var chosen = new StringBuilder();
        foreach (var source in sources) TextLines.AppendSeparated(chosen, section.SourceText(source), _newline);
        _sections[index] = section with { State = MergeSectionState.Chosen, Sources = sources.ToArray(), Text = chosen.ToString(), HasManualText = false, LineSources = [], PendingMarkers = [] };
        Changed();
    }

    public void ChooseAll(MergeSource source)
    {
        // 競合だけを採用し、自動解決済みの独立変更を残す。
        var chosen = _sections.Select(section => section.IsPending
            ? section with { State = MergeSectionState.Chosen, Sources = [source], Text = section.SourceText(source), HasManualText = false, LineSources = [], PendingMarkers = [] } : section).ToList();
        if (_sections.Any(section => section.IsPending)) { _sections = chosen; Changed(); }
    }

    public void UpdateText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text == Text) return;
        var oldText = Text; var oldProvenance = LineProvenance.ToArray(); var originalSections = _sections.ToList(); var originalNextId = _nextId;
        try
        {
        var diff = TextDiffer.Compare(oldText, text, new ComparisonOptions { CompareLineEndings = true });
        var oldLines = TextLines.Parse(oldText); var newLines = TextLines.Parse(text);
        var offsets = new int[oldLines.Count + 1];
        for (var index = 0; index < oldLines.Count; index++) offsets[index + 1] = offsets[index] + oldLines[index].Raw.Length;
        // 離れた編集の間にある未変更の競合を巻き込まない。
        foreach (var block in diff.Blocks.Reverse())
        {
            var original = oldText[offsets[block.LeftStart]..offsets[block.LeftStart + block.LeftCount]];
            var replacement = TextLines.Join(newLines.GetRange(block.RightStart, block.RightCount));
            var prefix = 0; while (prefix < original.Length && prefix < replacement.Length && original[prefix] == replacement[prefix]) prefix++;
            var oldEnd = original.Length; var newEnd = replacement.Length;
            while (oldEnd > prefix && newEnd > prefix && original[oldEnd - 1] == replacement[newEnd - 1]) { oldEnd--; newEnd--; }
            ApplyTextEdit(offsets[block.LeftStart] + prefix, oldEnd - prefix, replacement[prefix..newEnd]);
        }
        if (Text != text) throw new InvalidOperationException("手編集の範囲を復元できませんでした。");
        // 重複する区切り行を差分が生成マーカーへ対応付けた場合も、
        // 元ソースそのものまで編集した結果は解決済みとして扱う。
        _sections = _sections.Select(section => section.IsPending &&
            (section.Text == section.LeftText || section.Text == section.RightText || section.HasAncestor && section.Text == section.BaseText)
            ? section with { State = MergeSectionState.Edited, PendingMarkers = [], PendingUnits = 1 } : section).ToList();
        Reflow();
        var provenance = diff.Rows.Where(row => row.RightLineNumber is not null).Select(row =>
            row.Kind == DiffKind.Equal && row.LeftLineNumber is int oldLine && oldLine <= oldProvenance.Length ? oldProvenance[oldLine - 1].Source : "m").ToArray();
        var lineStarts = LineStarts(Text);
        for (var index = 0; index < _sections.Count; index++)
        {
            var range = Sections[index];
            var firstLine = LineAt(lineStarts, range.Start); var lastLine = LineAt(lineStarts, Math.Max(range.Start, range.Start + range.Length - 1));
            _sections[index] = _sections[index] with { LineSources = range.Length == 0 ? [] : provenance.Skip(firstLine).Take(lastLine - firstLine + 1)
                .Select(source => source == "?" && !range.Section.IsPending ? "m" : source).ToArray() };
        }
        Changed();
        }
        catch { _sections = originalSections; _nextId = originalNextId; Reflow(); throw; }
    }

    private void ApplyTextEdit(int begin, int length, string insertion)
    {
        var oldText = Text; var oldEnd = begin + length;
        var affected = length == 0
            ? new[] { Sections.FirstOrDefault(range => range.Start <= begin && range.Start + range.Length > begin) ?? Sections.LastOrDefault() }.OfType<MergeSectionRange>().ToArray()
            : Sections.Where(range => range.Start < oldEnd && range.Start + range.Length > begin).ToArray();
        if (affected.Length == 0)
        {
            _sections.Add(new(_nextId++, 0, MergeSectionState.Edited, insertion, "", "", "", []) { HasAncestor = false, HasManualText = true });
        }
        else
        {
            var first = affected[0]; var last = affected[^1];
            var index = _sections.FindIndex(section => section.Id == first.Section.Id);
            var lastIndex = _sections.FindIndex(section => section.Id == last.Section.Id);
            affected = Sections.Skip(index).Take(lastIndex - index + 1).ToArray();
            var prefix = oldText[first.Start..begin]; var suffix = oldText[oldEnd..(last.Start + last.Length)];
            string Combined(MergeSource source)
            {
                var output = new StringBuilder();
                foreach (var range in affected)
                    TextLines.AppendSeparated(output, source == MergeSource.Base && !range.Section.HasAncestor ? range.Section.Text : range.Section.SourceText(source), _newline);
                return output.ToString();
            }
            var changed = prefix + insertion + suffix;
            var pending = affected.Where(range => range.Section.IsPending).ToArray();
            var markers = new List<MergeMarkerSpan>();
            // 生成したマーカーの残存文字だけを追跡し、元の本文にある区切りを数えない。
            foreach (var range in pending)
            {
                foreach (var marker in range.Section.PendingMarkers)
                {
                    var markerStart = range.Start + marker.Start; var markerEnd = markerStart + marker.Length;
                    Keep(markerStart, Math.Min(markerEnd, begin), 0);
                    Keep(Math.Max(markerStart, oldEnd), markerEnd, insertion.Length - length);
                    void Keep(int start, int end, int shift)
                    {
                        if (end > start) markers.Add(new(start + shift - first.Start, end - start, marker.Group));
                    }
                }
            }
            var remainsPending = markers.Count > 0;
            var state = remainsPending ? pending.Any(range => range.Section.State == MergeSectionState.Conflict) ? MergeSectionState.Conflict : MergeSectionState.Unresolved : MergeSectionState.Edited;
            var replacement = first.Section with { State = state, Text = changed,
                BaseText = Combined(MergeSource.Base), LeftText = Combined(MergeSource.Left), RightText = Combined(MergeSource.Right), Sources = [], HasAncestor = affected.All(range => range.Section.HasAncestor),
                HasManualText = true, LineSources = [], PendingMarkers = markers, PendingUnits = remainsPending ? markers.Select(marker => marker.Group).Distinct().Count() : 1 };
            if (changed.Length == 0 && index > 0 && !_sections.Skip(lastIndex + 1).Any(section => section.Text.Length > 0))
            {
                var previousRange = Sections.Take(index).LastOrDefault(range => range.Length > 0);
                if (previousRange is not null && previousRange.Length > previousRange.Section.Text.Length)
                {
                    // 後続行を手で削除しても、削除範囲外に残した補完改行は本文として保持する。
                    var previousIndex = _sections.FindIndex(section => section.Id == previousRange.Section.Id);
                    _sections[previousIndex] = _sections[previousIndex] with { Text = oldText.Substring(previousRange.Start, previousRange.Length) };
                }
            }
            _sections.RemoveRange(index, affected.Length); _sections.Insert(index, replacement);
        }
        Reflow();
    }

    public IReadOnlyList<MergeLineProvenance> LineProvenance
    {
        get
        {
            var result = new List<MergeLineProvenance>();
            var lineStarts = LineStarts(Text);
            var sourceLines = Sections.Select(range => range.Section.Sources.SelectMany(source => TextLines.Parse(range.Section.SourceText(source)).Select(_ => Marker(source))).ToArray()).ToArray();
            var rangeIndex = 0;
            for (var line = 0; line < lineStarts.Length; line++)
            {
                var position = lineStarts[line];
                while (rangeIndex + 1 < Sections.Count && Sections[rangeIndex].Start + Sections[rangeIndex].Length <= position) rangeIndex++;
                if (Sections.Count == 0) break;
                var range = Sections[rangeIndex];
                var section = range.Section;
                var index = line - LineAt(lineStarts, range.Start);
                result.Add(new(line + 1, index < section.LineSources.Count ? section.LineSources[index] : section.IsPending ? "?" : section.State == MergeSectionState.Edited ? "m" : index < sourceLines[rangeIndex].Length ? sourceLines[rangeIndex][index] : Marker(section.Sources.FirstOrDefault(MergeSource.Base))));
            }
            return result;
        }
    }

    private static int[] LineStarts(string text)
    {
        var lines = TextLines.Parse(text); var starts = new int[lines.Count]; var position = 0;
        for (var index = 0; index < lines.Count; index++) { starts[index] = position; position += lines[index].Raw.Length; }
        return starts;
    }
    private static int LineAt(int[] starts, int position)
    {
        var index = Array.BinarySearch(starts, position); return Math.Max(0, index >= 0 ? index : ~index - 1);
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        var snapshot = _history[--_historyIndex]; _sections = snapshot.Sections.ToList(); Reflow(); return true;
    }
    public bool Redo()
    {
        if (!CanRedo) return false;
        var snapshot = _history[++_historyIndex]; _sections = snapshot.Sections.ToList(); Reflow(); return true;
    }
    private void Changed()
    {
        Reflow();
        if (_historyIndex + 1 < _history.Count) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(new(_sections.ToArray())); _historyIndex++;
        long characters = _history.Sum(snapshot => snapshot.Sections.Sum(section => (long)section.Text.Length));
        while (_history.Count > 1 && (_history.Count > 100 || characters > MaximumHistoryCharacters))
        { characters -= _history[0].Sections.Sum(section => (long)section.Text.Length); _history.RemoveAt(0); _historyIndex--; }
    }
    private void Reflow() { (Text, Sections) = Render(_sections, _newline); }

    internal static (string Text, IReadOnlyList<MergeSectionRange> Ranges) Render(IEnumerable<MergeSection> sections, string newline)
    {
        var output = new StringBuilder(); var ranges = new List<MergeSectionRange>();
        foreach (var section in sections)
        {
            var previousIndex = ranges.FindLastIndex(range => range.Length > 0);
            if (section.Text.Length > 0 && output.Length > 0 && output[^1] is not ('\r' or '\n') && !ranges[previousIndex].Section.HasManualText)
            {
                output.Append(newline);
                ranges[previousIndex] = ranges[previousIndex] with { Length = output.Length - ranges[previousIndex].Start };
                for (var index = previousIndex + 1; index < ranges.Count; index++) ranges[index] = ranges[index] with { Start = output.Length };
            }
            var start = output.Length; output.Append(section.Text);
            ranges.Add(new(section, start, output.Length - start));
        }
        return (output.ToString(), ranges);
    }
    internal static string ConflictText(string left, string ancestor, string right, string newline, bool hasAncestor)
    {
        var output = new StringBuilder("<<<<<<< LEFT" + newline);
        void Part(string text) { output.Append(text); if (text.Length > 0 && !TextLines.HasFinalNewLine(text)) output.Append(newline); }
        Part(left);
        if (hasAncestor) { output.Append("||||||| BASE").Append(newline); Part(ancestor); }
        output.Append("=======").Append(newline); Part(right); output.Append(">>>>>>> RIGHT").Append(newline);
        return output.ToString();
    }
    private static IReadOnlyList<MergeMarkerSpan> GeneratedMarkers(MergeSection section, string newline)
    {
        var markers = new List<MergeMarkerSpan>(); var position = 0;
        void MarkerLine(string value) { markers.Add(new(position, value.Length, section.Id)); position += value.Length + newline.Length; }
        void Part(string value) { position += value.Length; if (value.Length > 0 && !TextLines.HasFinalNewLine(value)) position += newline.Length; }
        MarkerLine("<<<<<<< LEFT"); Part(section.LeftText);
        if (section.HasAncestor) { MarkerLine("||||||| BASE"); Part(section.BaseText); }
        MarkerLine("======="); Part(section.RightText); MarkerLine(">>>>>>> RIGHT");
        return markers;
    }
    internal static bool WhitespaceEqual(string left, string right) => string.Concat(left.Where(c => !char.IsWhiteSpace(c))) == string.Concat(right.Where(c => !char.IsWhiteSpace(c)));
    private static string Marker(MergeSource source) => source switch { MergeSource.Left => "1", MergeSource.Base => "2", _ => "3" };
}
