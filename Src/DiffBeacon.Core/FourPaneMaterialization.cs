using System.Text;
using System.Collections.Frozen;

namespace DiffBeacon.Core;

public sealed record FourPaneMaterializationSettings
{
    public string? DefaultEol { get; init; }
    public string LeftLabel { get; init; } = "left";
    public string MiddleLabel { get; init; } = "middle";
    public string RightLabel { get; init; } = "right";
    public IReadOnlyList<bool?>? ExplicitUtf8Boms { get; init; }
    public IReadOnlySet<int>? WhitespaceOnlyOriginalDiffIds { get; init; }
    public long MaximumRetainedCharacters { get; init; } = 64L * 1024 * 1024;
}

public sealed class FourPaneMaterializedResult
{
    public ResultLineBuffer InitialBuffer { get; }
    public ResultEditSession InitialSession { get; }
    public ChoiceCatalog Choices { get; }
    public FourPaneAlignmentResult Alignment { get; }
    public bool HasAncestor => Alignment.HasAncestor;
    public int InputCount => Alignment.InputCount;
    public int WorkUsed { get; }
    public long RetainedCharacters { get; }
    public string DefaultEol { get; }
    private readonly string initialExpanded;
    private readonly long maximumCharacters;
    private readonly FourPaneMaterializationSettings settings;
    internal FourPaneMaterializedResult(ResultEditSession session, ChoiceCatalog choices, FourPaneAlignmentResult alignment,
        string expanded, string defaultEol, FourPaneMaterializationSettings settings, FourPanePipelineBudget budget)
    {
        InitialSession = session; InitialBuffer = session.Current; Choices = choices; Alignment = alignment; initialExpanded = expanded;
        DefaultEol = defaultEol; this.settings = settings; maximumCharacters = settings.MaximumRetainedCharacters;
        WorkUsed = budget.WorkUsed; RetainedCharacters = budget.RetainedCharacters;
    }
    // 現在の確定char spanを使用し、linked compactだけ保存版へ展開する。
    public string ExpandedText(ResultLineBuffer buffer, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(buffer); token.ThrowIfCancellationRequested();
        if (ReferenceEquals(buffer, InitialBuffer)) return initialExpanded;
        return FourPaneMaterialization.Expand(buffer, maximumCharacters, token);
    }
    public string ConflictText(int originalDiffIndex, CancellationToken token = default)
    {
        var sources = Choices.At(originalDiffIndex).Sources;
        bool whitespace = settings.WhitespaceOnlyOriginalDiffIds?.Contains(originalDiffIndex) == true;
        return FourPaneMaterialization.Markers(sources, DefaultEol, settings, whitespace, maximumCharacters, token).Text;
    }
}

public static class FourPaneMaterialization
{
    public static FourPaneMaterializedResult CreateTwoWay(string left, string right, ComparisonOptions? options = null,
        CancellationToken token = default) => CreateTwoWay(left, right, options, new(), token);
    public static FourPaneMaterializedResult CreateTwoWay(string left, string right, ComparisonOptions? options,
        FourPaneMaterializationSettings settings, CancellationToken token = default)
    {
        options ??= new(); Validate(settings);
        var generated = new FourPaneGenerator(options, token, settings.MaximumRetainedCharacters, FourPaneTextProfile.DecodedBody).GenerateTwoWay(left, right).Generation;
        return FromGeneration(generated, FourPaneInputMode.TwoWay, options, false, settings, token);
    }
    public static FourPaneMaterializedResult CreateThreeWay(string ancestor, string left, string right,
        ComparisonOptions? options = null, bool autoResolve = false, CancellationToken token = default)
        => CreateThreeWay(ancestor, left, right, options, autoResolve, new(), token);
    public static FourPaneMaterializedResult CreateThreeWay(string ancestor, string left, string right,
        ComparisonOptions? options, bool autoResolve, FourPaneMaterializationSettings settings, CancellationToken token = default)
    {
        options ??= new(); Validate(settings);
        var generated = new FourPaneGenerator(options, token, settings.MaximumRetainedCharacters, FourPaneTextProfile.DecodedBody).Generate(ancestor, left, right);
        return FromGeneration(generated, FourPaneInputMode.AncestorThreeWay, options, autoResolve, settings, token);
    }
    public static FourPaneMaterializedResult CreateIndependentThreeWay(string left, string middle, string right,
        ComparisonOptions? options = null, CancellationToken token = default)
        => CreateIndependentThreeWay(left, middle, right, options, new(), token);
    public static FourPaneMaterializedResult CreateIndependentThreeWay(string left, string middle, string right,
        ComparisonOptions? options, FourPaneMaterializationSettings settings, CancellationToken token = default)
    {
        options ??= new(); Validate(settings);
        var generated = new FourPaneGenerator(options, token, settings.MaximumRetainedCharacters, FourPaneTextProfile.DecodedBody).Generate(middle, left, right);
        return FromGeneration(generated, FourPaneInputMode.IndependentThreeWay, options, false, settings, token);
    }
    public static FourPaneMaterializedResult FromGeneration(GenerationResult generation, FourPaneInputMode mode,
        ComparisonOptions? options = null, bool autoResolve = false, FourPaneMaterializationSettings? settings = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generation); options ??= new(); settings ??= new(); Validate(settings);
        if (generation.Fallback) throw new ArgumentException(generation.FallbackReason ?? "four-pane generation failed");
        if (autoResolve && mode != FourPaneInputMode.AncestorThreeWay) throw new ArgumentException("automatic ancestor merge requires an ancestor");
        var budget = new FourPanePipelineBudget(generation, Math.Clamp(options.MaxFallbackComparisons, 0, GnuLineDiffer.MaximumWork), settings.MaximumRetainedCharacters, token);
        budget.Reserve(256 + 16L * (settings.WhitespaceOnlyOriginalDiffIds?.Count ?? 0));
        settings = settings with { ExplicitUtf8Boms = settings.ExplicitUtf8Boms is null ? null : ResultLineBuffer.Freeze(settings.ExplicitUtf8Boms),
            WhitespaceOnlyOriginalDiffIds = settings.WhitespaceOnlyOriginalDiffIds?.ToFrozenSet() };
        var alignment = FourPaneAlignment.Create(generation, mode, options.IgnoreBlankLines, settings.ExplicitUtf8Boms, budget);
        if (settings.WhitespaceOnlyOriginalDiffIds?.Any(x => x < 0 || x >= alignment.Diffs.Count) == true) throw new ArgumentException("whitespace diff ID is not present");
        string eol = settings.DefaultEol ?? PickEol(alignment);
        var text = new StringBuilder(); var entries = new List<ResultSegment>(); var choices = new List<ChoiceDescriptor>();
        var automaticPlan = new List<(int Diff, int Pane)>();
        var spans = new List<(int Start, int End, Origin Origin)>(); int logicalStart = 0;
        foreach (var s in alignment.Segments)
        {
            budget.Spend(); var sources = s.Sources.Select(x => x.Source).ToArray(); int common = alignment.InputCount == 2 ? 0 : 1;
            bool conflict = s.Kind == FourPaneOriginalKind.Conflict;
            bool whitespace = conflict && settings.WhitespaceOnlyOriginalDiffIds?.Contains(s.OriginalDiffId ?? -1) == true;
            SegmentState pendingState = conflict ? SegmentState.Conflict : SegmentState.Unresolved;
            int fallback = alignment.InputCount == 2 ? 1 : s.Kind switch
            { FourPaneOriginalKind.FirstOnly or FourPaneOriginalKind.SecondOnly => 0, FourPaneOriginalKind.ThirdOnly => 2, _ => 1 };
            string fallbackText = sources[fallback].RawText; int fallbackCount = sources[fallback].RealLineCount;
            string block = fallbackText; int blockCount = fallbackCount;
            if (conflict || alignment.InputCount == 2 && s.OriginalDiffId.HasValue)
            {
                var marker = Markers(sources, eol, settings, whitespace, settings.MaximumRetainedCharacters, token, budget);
                block = marker.Text; blockCount = marker.LineCount;
                if (conflict) { fallbackText = block; fallbackCount = blockCount; }
            }
            budget.Reserve(checked(128L + 2L * (32 + eol.Length)));
            string placeholder = (conflict ? whitespace ? "<Merge Conflict (Whitespace only)>" : "<Merge Conflict>" : "<Unresolved Difference>") + eol;
            bool commonState = !s.OriginalDiffId.HasValue || alignment.InputCount == 3 && s.Kind == FourPaneOriginalKind.Trivial;
            if (autoResolve && !commonState && !conflict) automaticPlan.Add((s.OriginalDiffId!.Value, sources[fallback].Pane));
            SegmentState state = commonState ? SegmentState.Common : pendingState;
            bool pending = state is SegmentState.Conflict or SegmentState.Unresolved;
            int selected = commonState ? common : fallback;
            var selectedSources = pending ? Array.Empty<ApparentSource>() : new[] { sources[selected] };
            int count = pending ? 1 : sources[selected].RealLineCount;
            string body = pending ? placeholder : sources[selected].RawText;
            budget.Reserve(checked(2L * body.Length + 160)); int start = text.Length; text.Append(body);
            if (pending) spans.Add((start, text.Length, new(s.SegmentId, -1, -1, false)));
            else
            {
                int offset = start;
                foreach (var source in selectedSources) foreach (var line in source.Lines)
                {
                    budget.Spend(); budget.Reserve(48); int end = checked(offset + line.Content.Length + line.Eol.Length);
                    spans.Add((offset, end, new(s.SegmentId, source.Pane, line.SourceLineIndex, offset == end))); offset = end;
                }
            }
            var last = selectedSources.SelectMany(x => x.Lines).LastOrDefault();
            entries.Add(new(s.SegmentId, s.OriginalDiffId ?? -1, state, logicalStart, count, 0,
                ResultLineBuffer.Freeze(selectedSources.Select(x => x.Pane)), ResultLineBuffer.Freeze(selectedSources.Select(x => x.RealLineCount)), s.OriginalDiffId.HasValue)
            { TextStart = start, TextLength = text.Length - start, OwnsTrailingEmptyLine = last is { Content.Length: 0, Eol.Length: 0 },
                BlockText = pending ? block : "", BlockLineCount = pending ? blockCount : 0 });
            logicalStart = checked(logicalStart + count);
            if (s.OriginalDiffId is int id)
            {
                budget.Reserve(128 + 16L * sources.Length);
                choices.Add(new(s.SegmentId, id, sources, pendingState, placeholder, 1, fallbackText, fallbackCount));
            }
        }
        budget.Reserve(text.Length); string raw = text.ToString(); var lines = new List<PhysicalLine>();
        int rowStart = 0, spanStart = 0;
        for (int i = 0; i <= raw.Length; i++)
        {
            budget.Spend(); if (i < raw.Length && raw[i] is not ('\r' or '\n')) continue;
            int bodyEnd = i, end = i;
            if (i < raw.Length) { end++; if (raw[i] == '\r' && end < raw.Length && raw[end] == '\n') { budget.Spend(); end++; } }
            budget.Reserve(checked(end - (long)rowStart + 96)); var origins = new List<Origin>();
            while (spanStart < spans.Count && (spans[spanStart].End < rowStart || spans[spanStart].End == rowStart && spans[spanStart].Start < rowStart)) spanStart++;
            for (int j = spanStart; j < spans.Count && (spans[j].Start < end || end == raw.Length && bodyEnd == end && spans[j].Start == end); j++)
            {
                budget.Spend(); var span = spans[j];
                if (span.Start < end && span.End > rowStart || span.Start == span.End && span.Start >= rowStart && (span.Start < end || end == raw.Length && bodyEnd == end))
                { budget.Reserve(16); origins.Add(span.Origin); }
            }
            lines.Add(new(raw[rowStart..bodyEnd], raw[bodyEnd..end], 0, ResultLineBuffer.Freeze(origins.Distinct()))); rowStart = end;
            if (i < raw.Length) i = end - 1;
        }
        budget.Reserve(checked(48L * lines.Count + 64L * entries.Count + 16L * choices.Count));
        var buffer = new ResultLineBuffer(lines, entries, 0, 0, new(0, 0), new(new(0, 0), new(0, 0))); buffer.ValidateTextRanges();
        var catalog = new ChoiceCatalog(choices, alignment.Diffs.Count);
        budget.Reserve(256);
        var session = new ResultEditSession(buffer, maximumCharacters: settings.MaximumRetainedCharacters);
        if (automaticPlan.Count > 0)
        {
            // 原典は末尾から一つのUndo groupで採用する。候補の履歴も含めて公開する。
            long addedText = automaticPlan.Sum(x => (long)catalog.At(x.Diff).Source(x.Pane).RawText.Length);
            long maximumText = checked(buffer.Text.Length + addedText);
            long maximumLines = checked(buffer.Lines.Count + automaticPlan.Sum(x => (long)catalog.At(x.Diff).Source(x.Pane).RealLineCount));
            long work = checked(automaticPlan.Count * (4 * maximumText + 8 * maximumLines + 64));
            if (work > int.MaxValue) throw new ArgumentException("automatic merge work limit");
            budget.Spend((int)work);
            budget.Reserve(checked(automaticPlan.Count * (8 * maximumText + 192 * maximumLines + 256) + 2 * maximumText));
            var requests = automaticPlan.AsEnumerable().Reverse().Select(x => new ChoiceRequest(session.Current.Version, x.Diff, new[] { x.Pane })).ToArray();
            session.ApplyAutomaticChoices(catalog, requests, token); buffer = session.Current;
        }
        string expanded = Expand(buffer, settings.MaximumRetainedCharacters, token, budget); budget.Spend();
        return new(session, catalog, alignment, expanded, eol, settings, budget);
    }

    internal static string Expand(ResultLineBuffer buffer, long maximum, CancellationToken token, FourPanePipelineBudget? budget = null)
    {
        buffer.ValidateTextRanges(); long size = 0;
        foreach (var s in buffer.Segments) { token.ThrowIfCancellationRequested(); budget?.Spend(); size = checked(size + (s.IsPlaceholder ? s.BlockText.Length : s.TextLength)); }
        if (size > maximum || size > int.MaxValue) throw new ArgumentException("expanded result capacity exceeded");
        budget?.Reserve(checked(2 * size)); var text = new StringBuilder((int)size);
        foreach (var s in buffer.Segments)
        { token.ThrowIfCancellationRequested(); if (s.IsPlaceholder) text.Append(s.BlockText); else text.Append(buffer.Text.AsSpan(s.TextStart, s.TextLength)); }
        return text.ToString();
    }

    internal static (string Text, int LineCount) Markers(IReadOnlyList<ApparentSource> sources, string eol,
        FourPaneMaterializationSettings settings, bool whitespace, long maximum, CancellationToken token, FourPanePipelineBudget? budget = null)
    {
        if (sources.Count is not (2 or 3)) throw new ArgumentException("marker source count");
        int right = sources.Count - 1; long size = 64L + settings.LeftLabel.Length + settings.MiddleLabel.Length + settings.RightLabel.Length
            + sources.Sum(x => (long)x.RawText.Length) + eol.Length * (sources.Count + 4L);
        if (size > maximum || size > int.MaxValue) throw new ArgumentException("conflict marker capacity exceeded");
        budget?.Reserve(checked(size * 2)); var text = new StringBuilder((int)size);
        text.Append("<<<<<<< ").Append(settings.RightLabel); if (whitespace) text.Append(" (whitespace only)"); text.Append(eol);
        void Append(int source)
        { token.ThrowIfCancellationRequested(); budget?.Spend(); text.Append(sources[source].RawText); if (text[^1] is not ('\r' or '\n')) text.Append(eol); }
        Append(right);
        if (sources.Count == 3) { text.Append("||||||| ").Append(settings.MiddleLabel).Append(eol); Append(1); }
        text.Append("=======").Append(eol); Append(0); text.Append(">>>>>>> ").Append(settings.LeftLabel).Append(eol);
        return (text.ToString(), checked((sources.Count == 3 ? 4 : 3) + sources.Sum(x => x.RealLineCount)));
    }
    private static void Validate(FourPaneMaterializationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings); ArgumentOutOfRangeException.ThrowIfNegative(settings.MaximumRetainedCharacters);
        if (settings.DefaultEol is not (null or "\n" or "\r" or "\r\n")) throw new ArgumentException("default EOL is invalid");
        foreach (string label in new[] { settings.LeftLabel, settings.MiddleLabel, settings.RightLabel })
            if (label is null || label.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("source label must be a single line");
    }
    // 固定GetTextFileStyle/PickResultCRLFStyle: 混在は未確定、空入力はDOS。
    private static string PickEol(FourPaneAlignmentResult alignment)
    {
        string? Style(FourPaneAlignedSource source)
        {
            var endings = source.Rows.Where(x => x.Origin == FourPaneRowOrigin.OriginalPhysical && x.Eol.Length > 0).Select(x => x.Eol).Distinct().Take(2).ToArray();
            return endings.Length == 0 ? "\r\n" : endings.Length == 1 ? endings[0] : null;
        }
        var styles = alignment.Sources.Select(Style).ToArray();
        if (styles.Length == 2) return styles[1] ?? styles[0] ?? "\r\n";
        string? selected = styles[1] == styles[0] ? styles[2] : styles[1] == styles[2] ? styles[0] : styles[0] == styles[2] ? styles[0] : styles[1];
        return selected ?? styles[1] ?? "\r\n";
    }
}
