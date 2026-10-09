using System.Text;
using System.Text.RegularExpressions;

namespace DiffBeacon.Core;

// 三者DTOは変更せず、存在しない祖先と二者pairの意味を明示する。
public sealed record TwoWayGenerationResult(GenerationResult Generation, PairLedger? LeftRight)
{
    public bool HasAncestor => false;
    public int InputCount => 2;
    public static int LogicalSourceFromNativePane(int pane) => pane switch
    { 0 => 0, 1 => 2, _ => throw new ArgumentOutOfRangeException(nameof(pane)) };
}

public sealed partial class FourPaneGenerator
{
    public TwoWayGenerationResult GenerateTwoWay(string left, string right,
        Func<int, RawRange, int>? apparentCountAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        token.ThrowIfCancellationRequested();
        if (consumed) throw new InvalidOperationException("生成器は候補ごとに新しいinstanceを使用してください。");
        consumed = true;
        PairLedger? pair = null;
        int[] counts = [];
        int?[] apparent = [null, null, null];
        try
        {
            Reserve((long)left.Length + right.Length);
            if (options.SubstitutionRules.Any(x => x.Enabled)) Reserve(TextPreprocessor.MaxRegexTextLength);
            var l = Parse(left); var r = Parse(right);
            counts = [l.Count, 0, r.Count];
            if (apparentCountAdapter != null)
                apparent = [Count(0, new(0, l.Count)), 0, Count(2, new(0, r.Count))];
            var lc = Comments(l); var rc = Comments(r);
            var gate = options.IgnoreBlankLines || options.CommentSyntax != CommentSyntax.None ||
                !string.IsNullOrEmpty(options.IgnoreLinePattern) || options.SubstitutionRules.Any(x => x.Enabled);
            pair = Pair(l, r, lc, rc, gate);
            Reserve(checked(16L * pair.Filtered.Count));
            var spans = new List<FourPaneThreeSpan>();
            foreach (var gap in pair.Filtered)
            {
                Spend();
                spans.Add(new(gap.BaseBegin, 0, gap.SideBegin, gap.BaseEnd, -1, gap.SideEnd,
                    gap.Trivial ? FourPaneOriginalKind.Trivial : FourPaneOriginalKind.Conflict));
            }
            var descriptors = TwoWayCoverage(spans, l, r, apparentCountAdapter);
            token.ThrowIfCancellationRequested();
            return Result(descriptors, spans.AsReadOnly(), false, null);
        }
        catch (FourPaneGenerationLimit e) { return Result([], [], true, e.Message); }
        catch (RegexMatchTimeoutException) { return Result([], [], true, "filter-timeout:fourpane"); }
        catch (ArgumentException e) { return Result([], [], true, "invalid-filter:" + e.Message); }

        int Count(int source, RawRange range)
        {
            var count = apparentCountAdapter!(source, range);
            if (count < 0) throw new ArgumentException("apparent countは負にできません。");
            return count;
        }
        TwoWayGenerationResult Result(IReadOnlyList<RawDescriptor> descriptors,
            IReadOnlyList<FourPaneThreeSpan> ranges, bool fallback, string? reason)
            => new(new("", left, right, descriptors, null, null, ranges, counts, apparent,
                fallback, reason, budget.Used, retained, reasons.AsReadOnly()), pair);
    }

    private List<RawDescriptor> TwoWayCoverage(IReadOnlyList<FourPaneThreeSpan> spans,
        IReadOnlyList<TextLine> l, IReadOnlyList<TextLine> r,
        Func<int, RawRange, int>? apparentCountAdapter)
    {
        var result = new List<RawDescriptor>(); int li = 0, ri = 0, diffId = 0;
        foreach (var span in spans)
        {
            Add(FourPaneOriginalKind.Common, null, span.LeftBegin, span.RightBegin);
            Add(span.Kind, diffId++, span.LeftEnd + 1, span.RightEnd + 1);
        }
        Add(FourPaneOriginalKind.Common, null, l.Count, r.Count);
        return result;

        void Add(FourPaneOriginalKind kind, int? id, int le, int re)
        {
            Spend();
            if (le < li || re < ri || le > l.Count || re > r.Count)
                throw new InvalidOperationException("原二区間coverageが逆行または範囲外です。");
            if (id == null && le == li && re == ri) return;
            var lt = Slice(l, li, le); var rt = Slice(r, ri, re);
            // 初期未解決markerはcallerでmaterialize。解除/AutoMergeのfallbackとは別。
            int? source = kind switch
            { FourPaneOriginalKind.Common => 0, FourPaneOriginalKind.Trivial => 2, _ => null };
            result.Add(new(result.Count, id, kind, new(0, 0), new(li, le - li), new(ri, re - ri),
                "", lt, rt, source, source == 0 ? lt : source == 2 ? rt : "",
                [Count(0, new(li, le - li)), apparentCountAdapter == null ? null : 0,
                    Count(2, new(ri, re - ri))]));
            li = le; ri = re;
        }
        int? Count(int source, RawRange range)
        {
            if (apparentCountAdapter == null) return null;
            var count = apparentCountAdapter(source, range);
            if (count < 0) throw new ArgumentException("apparent countは負にできません。");
            return count;
        }
        string Slice(IReadOnlyList<TextLine> lines, int begin, int end)
        {
            var output = new StringBuilder();
            for (var k = begin; k < end; k++)
            { Spend(); Reserve(lines[k].Raw.Length); output.Append(lines[k].Raw); }
            return output.ToString();
        }
    }
}
