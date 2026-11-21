namespace DiffBeacon.Core;

// 旧SHA Diff3.h:9-222のindex更新・包含端・空区間を残す移植草案。
// 初期pair/後filter/byte comparatorが未確定なら全生成器の旧互換を主張しない。
public readonly record struct FourPanePairSpan(int BaseBegin, int SideBegin, int BaseEnd, int SideEnd, bool Trivial);
public sealed record FourPaneThreeSpan(int LeftBegin, int BaseBegin, int RightBegin,
    int LeftEnd, int BaseEnd, int RightEnd, FourPaneOriginalKind Kind);

internal static class FourPaneDiff3Assembler
{
    internal static IReadOnlyList<FourPaneThreeSpan> Assemble(IReadOnlyList<FourPanePairSpan> baseLeft,
        IReadOnlyList<FourPanePairSpan> baseRight, Func<FourPaneThreeSpan, bool> nativeLeftRightEqual,
        bool hasTrivialDiffs, GnuLineMatcher.LineBudget sharedBudget, CancellationToken token)
    {
        var output = new List<FourPaneThreeSpan>();
        var i = 0; var j = 0; var lastLeft = 0; var lastBase = 0; var lastRight = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            FourPanePairSpan leftFirst = default, leftLast = default, rightFirst = default, rightLast = default;
            bool firstIsRight;
            if (i >= baseLeft.Count)
            {
                if (j >= baseRight.Count) break;
                rightLast = rightFirst = baseRight[j]; firstIsRight = true;
            }
            else if (j >= baseRight.Count)
            {
                leftLast = leftFirst = baseLeft[i]; firstIsRight = false;
            }
            else
            {
                leftLast = leftFirst = baseLeft[i]; rightLast = rightFirst = baseRight[j];
                firstIsRight = rightFirst.BaseBegin <= leftFirst.BaseBegin;
            }
            TakeWork();
            var lastIsRight = firstIsRight; var stopI = i; var stopJ = j;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (stopI >= baseLeft.Count || stopJ >= baseRight.Count) break;
                TakeWork();
                var a = baseLeft[stopI]; var b = baseRight[stopJ];
                if (a.BaseEnd == b.BaseEnd)
                {
                    stopI++; lastIsRight = true; leftLast = a; rightLast = b; break;
                }
                if (lastIsRight)
                {
                    if (Math.Max(b.BaseBegin, b.BaseEnd) < a.BaseBegin) break;
                }
                else if (Math.Max(a.BaseBegin, a.BaseEnd) < b.BaseBegin) break;
                if (b.BaseEnd > a.BaseEnd) { stopI++; lastIsRight = true; }
                else { stopJ++; lastIsRight = false; }
                leftLast = a; rightLast = b;
            }
            if (lastIsRight) stopJ++; else stopI++;
            int lb, bb, rb, le, be, re;
            if (firstIsRight)
            {
                bb = rightFirst.BaseBegin; rb = rightFirst.SideBegin;
                lb = stopI == i ? bb - lastBase + lastLeft : bb - leftFirst.BaseBegin + leftFirst.SideBegin;
            }
            else
            {
                lb = leftFirst.SideBegin; bb = leftFirst.BaseBegin;
                rb = stopJ == j ? bb - lastBase + lastRight : bb - rightFirst.BaseBegin + rightFirst.SideBegin;
            }
            if (lastIsRight)
            {
                be = rightLast.BaseEnd; re = rightLast.SideEnd;
                le = stopI == i ? be - lastBase + lastLeft : be - leftLast.BaseEnd + leftLast.SideEnd;
            }
            else
            {
                le = leftLast.SideEnd; be = leftLast.BaseEnd;
                re = stopJ == j ? be - lastBase + lastRight : be - rightLast.BaseEnd + rightLast.SideEnd;
            }
            lastLeft = le + 1; lastBase = be + 1; lastRight = re + 1;
            var span = new FourPaneThreeSpan(lb, bb, rb, le, be, re,
                stopI == i ? FourPaneOriginalKind.ThirdOnly : stopJ == j ? FourPaneOriginalKind.FirstOnly : FourPaneOriginalKind.Conflict);
            if (stopI != i && stopJ != j)
                span = span with { Kind = nativeLeftRightEqual(span) ? FourPaneOriginalKind.SecondOnly : FourPaneOriginalKind.Conflict };
            // 旧gateはusefilters||IgnoreBlank。左右option等価とは別の後置override。
            if (hasTrivialDiffs)
            {
                var allLeft = true; var allRight = true;
                for (var k = i; k < stopI; k++) { TakeWork(); allLeft &= baseLeft[k].Trivial; }
                for (var k = j; k < stopJ; k++) { TakeWork(); allRight &= baseRight[k].Trivial; }
                // 空側は旧for-loop同様true。片側のみTrivialでもTrivialになり得る。
                if (allLeft && allRight) span = span with { Kind = FourPaneOriginalKind.Trivial };
            }
            i = stopI; j = stopJ; output.Add(span);
        }
        // 旧Diff3.h:208-219の隣接端補正。raw sourceは補正後の三区間で再sliceする。
        for (var k = 0; k + 1 < output.Count; k++)
        {
            TakeWork(); var a = output[k]; var b = output[k + 1];
            output[k] = a with
            {
                LeftEnd = a.LeftEnd >= b.LeftBegin ? b.LeftBegin - 1 : a.LeftEnd,
                BaseEnd = a.BaseEnd >= b.BaseBegin ? b.BaseBegin - 1 : a.BaseEnd,
                RightEnd = a.RightEnd >= b.RightBegin ? b.RightBegin - 1 : a.RightEnd
            };
        }
        return output.AsReadOnly();

        void TakeWork()
        {
            token.ThrowIfCancellationRequested();
            if (sharedBudget.Remaining == 0) throw new FourPaneGenerationLimit("work-limit:fourpane-assembly");
            sharedBudget.Spend();
        }
    }
}
