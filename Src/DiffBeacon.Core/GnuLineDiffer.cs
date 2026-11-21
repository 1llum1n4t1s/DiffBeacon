// GNU DIFF の行比較: Src/diffutils/src/analyze.c からの C# 移植。
// Copyright (C) 1988, 1989, 1992, 1993 Free Software Foundation, Inc.
// GNU General Public License version 2 またはそれ以降。Src/COPYING を参照。
// 無保証 (MERCHANTABILITY / FITNESS FOR A PARTICULAR PURPOSE を含む)。
// Myers, Algorithmica 1(2), 1986, pp.251-266 の二方向探索と、
// Paul Eggert の TOO_EXPENSIVE heuristic を保持する。
namespace DiffBeacon.Core;

internal readonly record struct GnuLineChange(int LeftStart, int RightStart, int LeftCount, int RightCount);

internal sealed class GnuLineResult
{
    internal GnuLineResult(GnuLineChange[] changes, int workUsed, bool fallback, string? fallbackReason)
    {
        Changes = Array.AsReadOnly(changes);
        WorkUsed = workUsed;
        Fallback = fallback;
        FallbackReason = fallbackReason;
    }

    public IReadOnlyList<GnuLineChange> Changes { get; }
    public int WorkUsed { get; }
    public bool Fallback { get; }
    public string? FallbackReason { get; }
}

internal static class GnuLineDiffer
{
    internal const int MaximumWork = 8_000_000;
    private const int SnakeLimit = 20;

    // io.c の prefix/suffix 選定は呼出し側の責務。座標は渡された配列内の0始まり。
    internal static GnuLineResult Compare(int[] leftEquivalences, int[] rightEquivalences,
        int equivalenceCount, int maxWork, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(leftEquivalences);
        ArgumentNullException.ThrowIfNull(rightEquivalences);
        ArgumentOutOfRangeException.ThrowIfNegative(equivalenceCount);
        ArgumentOutOfRangeException.ThrowIfNegative(maxWork);
        token.ThrowIfCancellationRequested();
        var work = new WorkBudget(Math.Min(maxWork, MaximumWork), token);
        try
        {
            var engine = new Engine(leftEquivalences, rightEquivalences, equivalenceCount, work);
            var changes = engine.Execute();
            token.ThrowIfCancellationRequested();
            return new(changes, work.Used, false, null);
        }
        catch (WorkLimitException)
        {
            token.ThrowIfCancellationRequested();
            // 部分 script は捨て、両側の元行を一つの変更として完全に保持する。
            GnuLineChange[] changes = leftEquivalences.Length == 0 && rightEquivalences.Length == 0
                ? [] : [new(0, 0, leftEquivalences.Length, rightEquivalences.Length)];
            return new(changes, work.Used, true, "work-limit:gnu-algorithm");
        }
    }

    private sealed class WorkLimitException : Exception { }

    private sealed class WorkBudget(int limit, CancellationToken token)
    {
        public int Used { get; private set; }

        // vectorは確保する要素数、scan/探索は反復数、同値比較・flag操作は呼出し数。
        // すべて同じ予算へ加算し、確保前と各反復で取消を確認する。
        public void Spend(long amount = 1)
        {
            token.ThrowIfCancellationRequested();
            if (amount > limit - Used)
            {
                Used = limit;
                throw new WorkLimitException();
            }
            Used += (int)amount;
        }
    }

    private sealed class FileLines(int[] equivalents)
    {
        public int[] Equivalents { get; } = equivalents;
        public int[] Undiscarded { get; set; } = [];
        public int[] RealIndexes { get; set; } = [];
        // 原本の changed[-1] と changed[length] を1要素のoffsetで保持する。
        public bool[] Changed { get; set; } = [];
        public int UndiscardedCount { get; set; }
    }

    private readonly record struct Section(int XOff, int XLim, int YOff, int YLim, bool Minimal);
    private readonly record struct Partition(int XMid, int YMid, bool LowMinimal, bool HighMinimal, int Cost);
    private sealed record SectionFrame(Section Section, SectionFrame? Next);
    private sealed record ChangeLink(GnuLineChange Change, ChangeLink? Next);

    private sealed class Engine(int[] left, int[] right, int equivalenceCount, WorkBudget work)
    {
        private readonly FileLines[] files = [new(left), new(right)];
        private int[] forward = [];
        private int[] backward = [];
        private int diagonalOffset;
        private int tooExpensive;

        public GnuLineChange[] Execute()
        {
            if (left.Length == 0 && right.Length == 0)
                return [];
            foreach (var file in files)
            {
                foreach (var equivalent in file.Equivalents)
                {
                    work.Spend();
                    if ((uint)equivalent >= (uint)equivalenceCount)
                        throw new ArgumentOutOfRangeException(nameof(equivalenceCount), "Invalid line equivalence class.");
                }
                work.Spend((long)file.Equivalents.Length * 3 + 2);
                file.Undiscarded = new int[file.Equivalents.Length];
                file.RealIndexes = new int[file.Equivalents.Length];
                file.Changed = new bool[file.Equivalents.Length + 2];
            }
            DiscardConfusingLines();
            var count = files[0].UndiscardedCount + files[1].UndiscardedCount;
            work.Spend(((long)count + 3) * 2);
            forward = new int[count + 3];
            backward = new int[count + 3];
            diagonalOffset = files[1].UndiscardedCount + 1;
            tooExpensive = 1;
            for (var size = count; size != 0; size >>= 2)
            {
                work.Spend();
                tooExpensive <<= 1;
            }
            tooExpensive = Math.Max(4096, tooExpensive);
            CompareSequences();
            ShiftBoundaries();
            return BuildScript();
        }

        // analyze.c: discard_confusing_lines。no_discards=0 の全passを保持する。
        private void DiscardConfusingLines()
        {
            work.Spend((long)equivalenceCount * 2 + left.Length + right.Length);
            int[][] counts = [new int[equivalenceCount], new int[equivalenceCount]];
            byte[][] discarded = [new byte[left.Length], new byte[right.Length]];
            for (var f = 0; f < 2; f++)
                foreach (var equivalent in files[f].Equivalents)
                {
                    work.Spend();
                    counts[f][equivalent]++;
                }

            for (var f = 0; f < 2; f++)
            {
                var equivalents = files[f].Equivalents;
                var discards = discarded[f];
                var end = equivalents.Length;
                var many = 5;
                var temp = end / 64;
                while ((temp >>= 2) > 0)
                {
                    work.Spend();
                    many *= 2;
                }
                for (var i = 0; i < end; i++)
                {
                    work.Spend();
                    if (equivalents[i] == 0)
                        continue;
                    var matches = counts[1 - f][equivalents[i]];
                    if (matches == 0) discards[i] = 1;
                    else if (matches > many) discards[i] = 2;
                }
            }

            for (var f = 0; f < 2; f++)
            {
                var discards = discarded[f];
                var end = discards.Length;
                for (var i = 0; i < end; i++)
                {
                    work.Spend();
                    if (discards[i] == 2)
                        discards[i] = 0;
                    else if (discards[i] != 0)
                    {
                        var provisional = 0;
                        var j = i;
                        for (; j < end; j++)
                        {
                            work.Spend();
                            if (discards[j] == 0) break;
                            if (discards[j] == 2) provisional++;
                        }
                        while (j > i && discards[j - 1] == 2)
                        {
                            work.Spend();
                            discards[--j] = 0;
                            provisional--;
                        }
                        var length = j - i;
                        if ((long)provisional * 4 > length)
                        {
                            while (j > i)
                            {
                                work.Spend();
                                if (discards[--j] == 2) discards[j] = 0;
                            }
                        }
                        else
                        {
                            var minimum = 1;
                            var temp = length / 4;
                            while ((temp >>= 2) > 0)
                            {
                                work.Spend();
                                minimum *= 2;
                            }
                            minimum++;
                            var consecutive = 0;
                            for (j = 0; j < length; j++)
                            {
                                work.Spend();
                                if (discards[i + j] != 2) consecutive = 0;
                                else if (minimum == ++consecutive) j -= consecutive;
                                else if (minimum < consecutive) discards[i + j] = 0;
                            }
                            for (j = 0, consecutive = 0; j < length; j++)
                            {
                                work.Spend();
                                if (j >= 8 && discards[i + j] == 1) break;
                                if (discards[i + j] == 2)
                                {
                                    consecutive = 0;
                                    discards[i + j] = 0;
                                }
                                else if (discards[i + j] == 0) consecutive = 0;
                                else consecutive++;
                                if (consecutive == 3) break;
                            }
                            i += length - 1;
                            for (j = 0, consecutive = 0; j < length; j++)
                            {
                                work.Spend();
                                if (j >= 8 && discards[i - j] == 1) break;
                                if (discards[i - j] == 2)
                                {
                                    consecutive = 0;
                                    discards[i - j] = 0;
                                }
                                else if (discards[i - j] == 0) consecutive = 0;
                                else consecutive++;
                                if (consecutive == 3) break;
                            }
                        }
                    }
                }
            }
            for (var f = 0; f < 2; f++)
            {
                var file = files[f];
                var j = 0;
                for (var i = 0; i < file.Equivalents.Length; i++)
                {
                    work.Spend();
                    if (discarded[f][i] == 0)
                    {
                        file.Undiscarded[j] = file.Equivalents[i];
                        file.RealIndexes[j++] = i;
                    }
                    else SetChanged(file, i, true);
                }
                file.UndiscardedCount = j;
            }
        }

        private bool Equal(int[] x, int xi, int[] y, int yi)
        {
            work.Spend();
            return x[xi] == y[yi];
        }

        private bool Changed(FileLines file, int index)
        {
            work.Spend();
            return file.Changed[index + 1];
        }

        private void SetChanged(FileLines file, int index, bool value)
        {
            work.Spend();
            file.Changed[index + 1] = value;
        }

        // analyze.c: compareseq。後半を先に積み、原本の前半→後半順を保つ。
        private void CompareSequences()
        {
            work.Spend();
            SectionFrame? pending = new(new(0, files[0].UndiscardedCount, 0,
                files[1].UndiscardedCount, false), null);
            var xv = files[0].Undiscarded;
            var yv = files[1].Undiscarded;
            while (pending is not null)
            {
                work.Spend();
                var (xoff, xlim, yoff, ylim, minimal) = pending.Section;
                pending = pending.Next;
                while (xoff < xlim && yoff < ylim && Equal(xv, xoff, yv, yoff))
                {
                    xoff++;
                    yoff++;
                }
                while (xlim > xoff && ylim > yoff && Equal(xv, xlim - 1, yv, ylim - 1))
                {
                    xlim--;
                    ylim--;
                }
                if (xoff == xlim)
                    while (yoff < ylim) SetChanged(files[1], files[1].RealIndexes[yoff++], true);
                else if (yoff == ylim)
                    while (xoff < xlim) SetChanged(files[0], files[0].RealIndexes[xoff++], true);
                else
                {
                    var part = Diagonal(xoff, xlim, yoff, ylim, minimal);
                    if (part.Cost == 1)
                        throw new InvalidOperationException("GNU diagonal returned an impossible single-edit partition.");
                    work.Spend(2);
                    pending = new(new(part.XMid, xlim, part.YMid, ylim, part.HighMinimal), pending);
                    pending = new(new(xoff, part.XMid, yoff, part.YMid, part.LowMinimal), pending);
                }
            }
        }

        // analyze.c: diag。下降diagonal順、前後の同点選択、minimal伝播を保持する。
        private Partition Diagonal(int xoff, int xlim, int yoff, int ylim, bool minimal)
        {
            var xv = files[0].Undiscarded;
            var yv = files[1].Undiscarded;
            var dmin = xoff - ylim;
            var dmax = xlim - yoff;
            var fmid = xoff - yoff;
            var bmid = xlim - ylim;
            var fmin = fmid;
            var fmax = fmid;
            var bmin = bmid;
            var bmax = bmid;
            var odd = ((fmid - bmid) & 1) != 0;
            forward[fmid + diagonalOffset] = xoff;
            backward[bmid + diagonalOffset] = xlim;
            for (var c = 1; ; c++)
            {
                work.Spend();
                var bigSnake = false;
                if (fmin > dmin) forward[--fmin - 1 + diagonalOffset] = -1;
                else fmin++;
                if (fmax < dmax) forward[++fmax + 1 + diagonalOffset] = -1;
                else fmax--;
                for (var d = fmax; d >= fmin; d -= 2)
                {
                    work.Spend();
                    var low = forward[d - 1 + diagonalOffset];
                    var high = forward[d + 1 + diagonalOffset];
                    var x = low >= high ? low + 1 : high;
                    var oldx = x;
                    var y = x - d;
                    while (x < xlim && y < ylim && Equal(xv, x, yv, y)) { x++; y++; }
                    if (x - oldx > SnakeLimit) bigSnake = true;
                    forward[d + diagonalOffset] = x;
                    if (odd && bmin <= d && d <= bmax && backward[d + diagonalOffset] <= x)
                        return new(x, y, true, true, 2 * c - 1);
                }
                if (bmin > dmin) backward[--bmin - 1 + diagonalOffset] = int.MaxValue;
                else bmin++;
                if (bmax < dmax) backward[++bmax + 1 + diagonalOffset] = int.MaxValue;
                else bmax--;
                for (var d = bmax; d >= bmin; d -= 2)
                {
                    work.Spend();
                    var low = backward[d - 1 + diagonalOffset];
                    var high = backward[d + 1 + diagonalOffset];
                    var x = low < high ? low : high - 1;
                    var oldx = x;
                    var y = x - d;
                    while (x > xoff && y > yoff && Equal(xv, x - 1, yv, y - 1)) { x--; y--; }
                    if (oldx - x > SnakeLimit) bigSnake = true;
                    backward[d + diagonalOffset] = x;
                    if (!odd && fmin <= d && d <= fmax && x <= forward[d + diagonalOffset])
                        return new(x, y, true, true, 2 * c);
                }
                if (minimal) continue;

                // 既定 heuristic=1。20行snakeの再確認と最良候補の同点順も維持する。
                if (c > 200 && bigSnake)
                {
                    var best = 0;
                    var bestX = 0;
                    var bestY = 0;
                    for (var d = fmax; d >= fmin; d -= 2)
                    {
                        work.Spend();
                        var dd = d - fmid;
                        var x = forward[d + diagonalOffset];
                        var y = x - d;
                        var value = (x - xoff) * 2 - dd;
                        if (value > 12 * (c + Math.Abs(dd)) && value > best
                            && xoff + SnakeLimit <= x && x < xlim
                            && yoff + SnakeLimit <= y && y < ylim)
                            for (var k = 1; Equal(xv, x - k, yv, y - k); k++)
                                if (k == SnakeLimit)
                                {
                                    best = value; bestX = x; bestY = y;
                                    break;
                                }
                    }
                    if (best > 0) return new(bestX, bestY, true, false, 2 * c - 1);
                    for (var d = bmax; d >= bmin; d -= 2)
                    {
                        work.Spend();
                        var dd = d - bmid;
                        var x = backward[d + diagonalOffset];
                        var y = x - d;
                        var value = (xlim - x) * 2 + dd;
                        if (value > 12 * (c + Math.Abs(dd)) && value > best
                            && xoff < x && x <= xlim - SnakeLimit
                            && yoff < y && y <= ylim - SnakeLimit)
                            for (var k = 0; Equal(xv, x + k, yv, y + k); k++)
                                if (k == SnakeLimit - 1)
                                {
                                    best = value; bestX = x; bestY = y;
                                    break;
                                }
                    }
                    if (best > 0) return new(bestX, bestY, false, true, 2 * c - 1);
                }
                if (c >= tooExpensive)
                {
                    var fxybest = -1;
                    var fxbest = 0;
                    var bxybest = int.MaxValue;
                    var bxbest = 0;
                    for (var d = fmax; d >= fmin; d -= 2)
                    {
                        work.Spend();
                        var x = Math.Min(forward[d + diagonalOffset], xlim);
                        var y = x - d;
                        if (ylim < y) { x = ylim + d; y = ylim; }
                        if (fxybest < x + y) { fxybest = x + y; fxbest = x; }
                    }
                    for (var d = bmax; d >= bmin; d -= 2)
                    {
                        work.Spend();
                        var x = Math.Max(xoff, backward[d + diagonalOffset]);
                        var y = x - d;
                        if (y < yoff) { x = yoff + d; y = yoff; }
                        if (x + y < bxybest) { bxybest = x + y; bxbest = x; }
                    }
                    return (xlim + ylim) - bxybest < fxybest - (xoff + yoff)
                        ? new(fxbest, fxybest - fxbest, true, false, 2 * c - 1)
                        : new(bxbest, bxybest - bxbest, false, true, 2 * c - 1);
                }
            }
        }

        // analyze.c: shift_boundaries。inhibit=0 の両側調整を同じ順で行う。
        private void ShiftBoundaries()
        {
            for (var f = 0; f < 2; f++)
            {
                var file = files[f];
                var other = files[1 - f];
                var equivalents = file.Equivalents;
                var end = equivalents.Length;
                var i = 0;
                var j = 0;
                while (true)
                {
                    work.Spend();
                    while (i < end && !Changed(file, i))
                    {
                        while (Changed(other, j++)) { }
                        i++;
                    }
                    if (i == end) break;
                    var start = i;
                    while (Changed(file, ++i)) { }
                    while (Changed(other, j)) j++;
                    int runLength;
                    int corresponding;
                    do
                    {
                        work.Spend();
                        runLength = i - start;
                        while (start != 0 && Equal(equivalents, start - 1, equivalents, i - 1))
                        {
                            SetChanged(file, --start, true);
                            SetChanged(file, --i, false);
                            while (Changed(file, start - 1)) start--;
                            while (Changed(other, --j)) { }
                        }
                        corresponding = Changed(other, j - 1) ? i : end;
                        while (i != end && Equal(equivalents, start, equivalents, i))
                        {
                            SetChanged(file, start++, false);
                            SetChanged(file, i++, true);
                            while (Changed(file, i)) i++;
                            while (Changed(other, ++j)) corresponding = i;
                        }
                    } while (runLength != i - start);
                    while (corresponding < i)
                    {
                        SetChanged(file, --start, true);
                        SetChanged(file, --i, false);
                        while (Changed(other, --j)) { }
                    }
                }
            }
        }

        // analyze.c: build_script。末尾から採取し、原本同様に先頭へ連結する。
        private GnuLineChange[] BuildScript()
        {
            ChangeLink? script = null;
            var count = 0;
            var i0 = left.Length;
            var i1 = right.Length;
            while (i0 >= 0 || i1 >= 0)
            {
                work.Spend();
                if (Changed(files[0], i0 - 1) || Changed(files[1], i1 - 1))
                {
                    var end0 = i0;
                    var end1 = i1;
                    while (Changed(files[0], i0 - 1)) i0--;
                    while (Changed(files[1], i1 - 1)) i1--;
                    work.Spend();
                    script = new(new(i0, i1, end0 - i0, end1 - i1), script);
                    count++;
                }
                i0--;
                i1--;
            }
            work.Spend(count);
            var changes = new GnuLineChange[count];
            for (var index = 0; script is not null; index++, script = script.Next)
            {
                work.Spend();
                changes[index] = script.Change;
            }
            return changes;
        }
    }
}
