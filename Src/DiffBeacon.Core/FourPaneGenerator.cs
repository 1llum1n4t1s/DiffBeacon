using System.Text;
using System.Text.RegularExpressions;

namespace DiffBeacon.Core;

public enum FourPaneOriginalKind { Common, FirstOnly, SecondOnly, ThirdOnly, Conflict, Trivial }
public readonly record struct RawRange(int StartLine, int LineCount);
public sealed record RawDescriptor(int Id, int? OriginalDiffId, FourPaneOriginalKind Kind,
    RawRange Base, RawRange Left, RawRange Right, string BaseText, string LeftText, string RightText,
    int? FallbackSource, string RawFallbackText, int?[] SourceApparentLineCounts);
public sealed record PairLedger(IReadOnlyList<FourPanePairSpan> Raw, IReadOnlyList<FourPanePairSpan> Filtered);
public sealed record GenerationResult(string Ancestor, string Left, string Right,
    IReadOnlyList<RawDescriptor> Descriptors, PairLedger? BaseLeft, PairLedger? BaseRight,
    IReadOnlyList<FourPaneThreeSpan> Ranges, int[] SourceRawLineCounts, int?[] SourceApparentLineCounts,
    bool Fallback, string? FallbackReason, int WorkUsed, long RetainedCharacters,
    IReadOnlyList<string> BoundaryReasons)
{
    // 旧GUIのlocale/codepage、filters、ghost行とexport EOLの互換性は未検証。
    public bool LegacyCompatibilityVerified => false;
}
internal sealed class FourPaneGenerationLimit(string reason) : Exception(reason);

public sealed partial class FourPaneGenerator
{
    private readonly ComparisonOptions options;
    private readonly CancellationToken token;
    private readonly GnuLineMatcher.LineBudget budget;
    private readonly long capacity;
    private readonly FourPaneTextProfile textProfile;
    private long retained;
    private readonly List<string> reasons = [];
    private bool consumed;
    private const string FilteredLine = "\u0001FILTERED_LINE";

    public FourPaneGenerator(ComparisonOptions options, CancellationToken token = default,
        long maximumRetainedCharacters = 64 * 1024 * 1024,
        FourPaneTextProfile textProfile = FourPaneTextProfile.LegacyRawUtf8)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.SubstitutionRules);
        if (options.SubstitutionRules.Count > 256) throw new ArgumentException("置換規則は256個までです。");
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetainedCharacters);
        if (!Enum.IsDefined(textProfile)) throw new ArgumentOutOfRangeException(nameof(textProfile));
        this.options = options with { SubstitutionRules = options.SubstitutionRules.ToArray() };
        this.token = token;
        budget = new(Math.Clamp(options.MaxFallbackComparisons, 0, GnuLineDiffer.MaximumWork), token);
        capacity = maximumRetainedCharacters;
        this.textProfile = textProfile;
    }

    public GenerationResult Generate(string ancestor, string left, string right,
        Func<int, RawRange, int>? apparentCountAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(ancestor); ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right); token.ThrowIfCancellationRequested();
        if (consumed) throw new InvalidOperationException("生成器は候補ごとに新しいinstanceを使用してください。");
        consumed = true;
        PairLedger? al = null, ar = null;
        int[] counts = []; int?[] apparent = [null, null, null];
        try
        {
            Reserve((long)ancestor.Length + left.Length + right.Length);
            // replacement token一個の最大一時量を先に予約する。
            if (options.SubstitutionRules.Any(x => x.Enabled)) Reserve(TextPreprocessor.MaxRegexTextLength);
            var a = Parse(ancestor); var l = Parse(left); var r = Parse(right);
            counts = [l.Count, a.Count, r.Count];
            var branchBytes = new FourPaneByteBridge(left, right, l.Count, r.Count, options,
                budget, token, Reserve, textProfile);
            if (apparentCountAdapter != null)
                apparent = [Count(0, new(0, l.Count)), Count(1, new(0, a.Count)), Count(2, new(0, r.Count))];
            // 全文の文脈でコメント解析するが、raw scriptには使わない。
            var ac = Comments(a); var lc = Comments(l); var rc = Comments(r);

            var gate = options.IgnoreBlankLines || options.CommentSyntax != CommentSyntax.None ||
                !string.IsNullOrEmpty(options.IgnoreLinePattern) || options.SubstitutionRules.Any(x => x.Enabled);
            al = Pair(a, l, ac, lc, gate); ar = Pair(a, r, ac, rc, gate);
            Reserve(checked(16L * (al.Filtered.Count + ar.Filtered.Count)));
            var spans = FourPaneDiff3Assembler.Assemble(al.Filtered, ar.Filtered, EqualBranches,
                gate, budget, token);
            var descriptors = Coverage(spans, a, l, r, apparentCountAdapter);
            token.ThrowIfCancellationRequested();
            return Result(descriptors, spans, false, null);

            bool EqualBranches(FourPaneThreeSpan s)
            {
                return branchBytes.EqualBranches(s);
            }
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
        GenerationResult Result(IReadOnlyList<RawDescriptor> descriptors,
            IReadOnlyList<FourPaneThreeSpan> ranges, bool fallback, string? reason)
            => new(ancestor, left, right, descriptors, al, ar, ranges, counts, apparent,
                fallback, reason, budget.Used, retained, reasons.AsReadOnly());
    }

    private List<TextLine> Parse(string text)
    {
        Spend(text.Length);
        var result = TextLines.Parse(text, token);
        Reserve(checked(8L * result.Count + text.Length));
        return result;
    }

    private (string[] Keys, bool[] CommentOnly) Comments(IReadOnlyList<TextLine> lines)
    {
        Reserve(checked(8L * lines.Count + lines.Sum(x => (long)x.Content.Length)));
        return new TextPreprocessor(new() { CommentSyntax = options.CommentSyntax }, token).Process(lines);
    }

    private List<FourPanePairSpan> Compare(IReadOnlyList<TextLine> a, IReadOnlyList<TextLine> b,
        int offsetA = 0, int offsetB = 0)
    {
        Reserve(checked(12L * (a.Count + b.Count)));
        Reserve(a.Sum(x => (long)x.Raw.Length) + b.Sum(x => (long)x.Raw.Length));
        var classified = RawByteEquivalence.Classify(string.Concat(a.Select(x => x.Raw)),
            string.Concat(b.Select(x => x.Raw)), options, RawSpend, Reserve, textProfile, token);
        if (classified.Left.Length != a.Count || classified.Right.Length != b.Count)
            throw new ArgumentException("BOM-only raw/prepared line mapping is outside raw pair adapter");
        var boundaryA = RawByteEquivalence.BoundaryLines(classified.A);
        var boundaryB = RawByteEquivalence.BoundaryLines(classified.B);
        var matched = GnuLineMatcher.Match(classified.Left, classified.Right, boundaryA, boundaryB,
            Enumerable.Range(0, a.Count).ToArray(), Enumerable.Range(0, b.Count).ToArray(),
            options with { CompareLineEndings = true }, budget, token);
        if (matched.Fallback) throw new FourPaneGenerationLimit(matched.FallbackReason ?? "work-limit:gnu");
        var gaps = new List<FourPanePairSpan>(); int ai = 0, bi = 0;
        foreach (var (x, y) in matched.Pairs)
        {
            Spend();
            if (x > ai || y > bi) Add(x, y);
            ai = x + 1; bi = y + 1;
        }
        if (ai < a.Count || bi < b.Count) Add(a.Count, b.Count);
        return gaps;

        void Add(int x, int y) => gaps.Add(new(ai + offsetA, bi + offsetB,
            x + offsetA - 1, y + offsetB - 1, false));
    }

    private PairLedger Pair(IReadOnlyList<TextLine> a, IReadOnlyList<TextLine> b,
        (string[] Keys, bool[] CommentOnly) ac, (string[] Keys, bool[] CommentOnly) bc, bool applyPostFilter = true)
    {
        var raw = Compare(a, b);
        if (!applyPostFilter) return new(raw.AsReadOnly(), raw.AsReadOnly());
        var filtered = new List<FourPanePairSpan>();
        var regex = options.CreateIgnoredLineRegex();
        foreach (var gap in raw)
        {
            Spend();
            var an = gap.BaseEnd - gap.BaseBegin + 1; var bn = gap.SideEnd - gap.SideBegin + 1;
            var at = Filter(a, ac, gap.BaseBegin, an, regex);
            var bt = Filter(b, bc, gap.SideBegin, bn, regex);
            if (regex != null && at.AllRegex && bt.AllRegex)
            { filtered.Add(gap with { Trivial = true }); continue; }
            var ast = Substitute(at.Text); var bst = Substitute(bt.Text);
            if (ast == bst) { filtered.Add(gap with { Trivial = true }); continue; }
            var aa = TextLines.Parse(ast, token); var bb = TextLines.Parse(bst, token);
            // 原典SplitLinesは空の最終実行を一行だけ補完する。
            if (aa.Count < an) aa.Add(new("", ""));
            if (bb.Count < bn) bb.Add(new("", ""));
            // 置換の改行変更は原典同様にraw hunkを維持。任意の1:1対応を作らない。
            if (aa.Count != an || bb.Count != bn)
            {
                reasons.Add($"postfilter-line-count-preserved-raw:{gap.BaseBegin}:{gap.SideBegin}");
                filtered.Add(gap); continue;
            }
            var changes = Compare(aa, bb, gap.BaseBegin, gap.SideBegin);
            var ai = gap.BaseBegin; var bi = gap.SideBegin;
            foreach (var c in changes)
            {
                Spend();
                if (c.BaseBegin > ai || c.SideBegin > bi)
                    filtered.Add(new(ai, bi, c.BaseBegin - 1, c.SideBegin - 1, true));
                SplitExcess(c);
                ai = c.BaseEnd + 1; bi = c.SideEnd + 1;
            }
            if (ai <= gap.BaseEnd || bi <= gap.SideEnd)
                filtered.Add(new(ai, bi, gap.BaseEnd, gap.SideEnd, true));

            void SplitExcess(FourPanePairSpan c)
            {
                var na = c.BaseEnd - c.BaseBegin + 1; var nb = c.SideEnd - c.SideBegin + 1;
                var small = Math.Min(na, nb); var ignorable = na != nb;
                if (na > nb)
                    for (var k = small; k < na; k++) ignorable &= Ignorable(aa[c.BaseBegin - gap.BaseBegin + k],
                        ac.CommentOnly[c.BaseBegin + k]);
                else if (nb > na)
                    for (var k = small; k < nb; k++) ignorable &= Ignorable(bb[c.SideBegin - gap.SideBegin + k],
                        bc.CommentOnly[c.SideBegin + k]);
                if (!ignorable) { filtered.Add(c); return; }
                if (small == 0) { filtered.Add(c with { Trivial = true }); return; }
                filtered.Add(new(c.BaseBegin, c.SideBegin, c.BaseBegin + small - 1, c.SideBegin + small - 1, false));
                filtered.Add(new(c.BaseBegin + small, c.SideBegin + small, c.BaseEnd, c.SideEnd, true));
            }
        }
        return new(raw.AsReadOnly(), filtered.AsReadOnly());
    }

    private bool Ignorable(TextLine line, bool commentOnly)
    { Spend(line.Content.Length); return commentOnly || line.Content == FilteredLine ||
        (options.IgnoreBlankLines && CProfileBlank(line.Content)); }

    private bool CProfileBlank(string text)
    {
        // rawは原典C locale、復号本文は従来製品のUnicode空白判定を使う。
        foreach (var c in text)
        {
            Spend();
            if (textProfile == FourPaneTextProfile.DecodedBody ? !char.IsWhiteSpace(c) :
                c != 32 && c is not (>= (char)9 and <= (char)13)) return false;
        }
        return true;
    }
    private (string Text, bool AllRegex) Filter(IReadOnlyList<TextLine> lines,
        (string[] Keys, bool[] CommentOnly) comments, int start, int count, Regex? regex)
    {
        var text = new StringBuilder(); var all = true;
        for (var k = start; k < start + count; k++)
        {
            Spend(comments.Keys[k].Length);
            TextPreprocessor.CheckRegexInput(comments.Keys[k]);
            var matches = regex?.IsMatch(comments.Keys[k]) ?? false; all &= matches;
            var part = matches ? FilteredLine : comments.Keys[k];
            Reserve(part.Length + lines[k].Ending.Length);
            text.Append(part).Append(lines[k].Ending);
        }
        return (text.ToString(), all);
    }

    private string Substitute(string text)
    {
        foreach (var rule in options.SubstitutionRules)
        {
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(rule);
            if (!rule.Enabled || rule.Pattern.Length == 0) continue;
            TextPreprocessor.CheckRegexInput(text);
            if (rule.Replacement.Length > 65_536) throw new ArgumentException("置換文字列が長すぎます。");
            var pattern = rule.UseRegex ? rule.Pattern : Regex.Escape(rule.Pattern);
            if (!rule.UseRegex && rule.WholeWord) pattern = @"\b" + pattern + @"\b";
            var regex = TextPreprocessor.CreateRegex(pattern, rule.MatchCase);
            var output = new StringBuilder(); var cursor = 0;
            foreach (Match match in regex.Matches(text))
            {
                Spend(match.Length + 1);
                Append(text.AsSpan(cursor, match.Index - cursor));
                var replacement = DecodeReplacement(rule.Replacement);
                var index = 0;
                while (index < replacement.Length)
                {
                    Spend();
                    if (replacement[index] != '$')
                    {
                        var end = replacement.IndexOf('$', index);
                        if (end < 0) end = replacement.Length;
                        Append(replacement.AsSpan(index, end - index)); index = end; continue;
                    }
                    var stop = index + 1;
                    if (stop < replacement.Length)
                    {
                        if (replacement[stop] is >= '0' and <= '9')
                            while (stop < replacement.Length && replacement[stop] is >= '0' and <= '9') stop++;
                        else if (replacement[stop] == '{' && replacement.IndexOf('}', stop) is var close && close >= 0)
                            stop = close + 1;
                        else stop++;
                    }
                    Append(match.Result(replacement[index..stop]).AsSpan()); index = stop;
                }
                cursor = match.Index + match.Length;
            }
            Append(text.AsSpan(cursor)); text = output.ToString();
            TextPreprocessor.CheckRegexInput(text);

            void Append(ReadOnlySpan<char> part)
            {
                Spend(part.Length); Reserve(part.Length);
                if (part.Length > TextPreprocessor.MaxRegexTextLength - output.Length)
                    throw new FourPaneGenerationLimit("capacity-limit:substitution-output");
                output.Append(part);
            }
        }
        return text;
    }

    private static string DecodeReplacement(string replacement)
    {
        // 現Coreと同じ制御文字/キャプチャ文法。PCRE全体の互換性は別境界。
        var result = new StringBuilder(replacement.Length);
        for (var index = 0; index < replacement.Length; index++)
        {
            if (replacement[index] != '\\') { result.Append(replacement[index]); continue; }
            if (++index == replacement.Length) break;
            var next = replacement[index];
            if (next is >= '0' and <= '9') { result.Append('$').Append(next); continue; }
            if (next == 'x' && index + 2 < replacement.Length && byte.TryParse(replacement.AsSpan(index + 1, 2),
                System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hex))
            { result.Append((char)hex); index += 2; continue; }
            result.Append(next switch { 'a' => '\a', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r',
                't' => '\t', 'v' => '\v', _ => next });
        }
        return result.ToString();
    }

    private List<RawDescriptor> Coverage(IReadOnlyList<FourPaneThreeSpan> spans,
        IReadOnlyList<TextLine> a, IReadOnlyList<TextLine> l, IReadOnlyList<TextLine> r,
        Func<int, RawRange, int>? apparentCountAdapter)
    {
        var result = new List<RawDescriptor>(); int ai = 0, li = 0, ri = 0, diffId = 0;
        foreach (var s in spans)
        {
            Add(FourPaneOriginalKind.Common, null, s.BaseBegin, s.LeftBegin, s.RightBegin);
            Add(s.Kind, diffId++, s.BaseEnd + 1, s.LeftEnd + 1, s.RightEnd + 1);
        }
        Add(FourPaneOriginalKind.Common, null, a.Count, l.Count, r.Count);
        return result;

        void Add(FourPaneOriginalKind kind, int? id, int ae, int le, int re)
        {
            Spend();
            if (ae < ai || le < li || re < ri || ae > a.Count || le > l.Count || re > r.Count)
                throw new InvalidOperationException("原三区間coverageが逆行または範囲外です。");
            if (id == null && ae == ai && le == li && re == ri) return;
            var bt = Slice(a, ai, ae); var lt = Slice(l, li, le); var rt = Slice(r, ri, re);
            int? source = kind switch { FourPaneOriginalKind.Common or FourPaneOriginalKind.Trivial => 1,
                FourPaneOriginalKind.FirstOnly or FourPaneOriginalKind.SecondOnly => 0,
                FourPaneOriginalKind.ThirdOnly => 2, _ => null };
            result.Add(new(result.Count, id, kind, new(ai, ae - ai), new(li, le - li), new(ri, re - ri),
                bt, lt, rt, source, source == 0 ? lt : source == 1 ? bt : source == 2 ? rt : "",
                [Count(0, new(li, le - li)), Count(1, new(ai, ae - ai)), Count(2, new(ri, re - ri))]));
            ai = ae; li = le; ri = re;
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

    private void RawSpend(long amount)
    {
        Spend(amount);
        token.ThrowIfCancellationRequested();
    }
    private void Spend(long amount = 1)
    {
        token.ThrowIfCancellationRequested();
        if (amount > budget.Remaining) throw new FourPaneGenerationLimit("work-limit:fourpane-input-filter");
        budget.Spend(amount);
    }
    private void Reserve(long amount)
    {
        token.ThrowIfCancellationRequested();
        if (amount > capacity - retained) throw new FourPaneGenerationLimit("capacity-limit:fourpane-retention");
        retained += amount;
    }
}
