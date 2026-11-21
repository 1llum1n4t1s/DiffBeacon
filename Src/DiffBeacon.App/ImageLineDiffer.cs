/*
 * Uses the source code from the LibXDiff library used by git with
 * modifications so that it can be used for image comparison.
 * Port of WinIMerge Diff.hpp, revision da639cdfaeca87aaad0eaceec509afa11ad61421.
 * WinIMerge is distributed under GPL-2.0-or-later.
 *
 * LibXDiff by Davide Libenzi ( File Differential Library )
 * Copyright (C) 2003 Davide Libenzi
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, see
 * <http://www.gnu.org/licenses/>.
 *
 * Davide Libenzi <davidel@xmailserver.org>
 */

namespace DiffBeacon.App;

// 固定WinIMergeのMYERS（flags=0）。GNU行算法やcompactchangeへ置き換えない。
internal static class ImageLineDiffer
{
    internal const int MaximumRows = 1_000_000;
    internal const long MaximumMetadataBytes = 128L * 1024 * 1024;
    internal const long MaximumWork = 200_000_000;
    private const int SnakeCount = 20;
    private const int HeuristicMinimum = 256;

    internal sealed record Result(string Script, uint[] LeftHashes, uint[] RightHashes, long Work);

    // BGRAは上から下の行順で借用する。0行は許容するが幅は正数とする。
    // 閾値の定義域は有限・非負かつ floor(sqrt(threshold²/3))*2 がintに収まる範囲。
    // メタデータは入力BGRAを除く累積確保量。上限時は近似結果を返さず失敗する。
    internal static Result Compare(int leftWidth, byte[] leftBgra, int rightWidth, byte[] rightBgra,
        double threshold, CancellationToken token = default, long maximumWork = MaximumWork)
    {
        token.ThrowIfCancellationRequested();
        if (maximumWork < 0 || maximumWork > MaximumWork) throw new ArgumentOutOfRangeException(nameof(maximumWork));
        ArgumentNullException.ThrowIfNull(leftBgra);
        ArgumentNullException.ThrowIfNull(rightBgra);
        var leftRows = ValidateRows(leftWidth, leftBgra, nameof(leftWidth));
        var rightRows = ValidateRows(rightWidth, rightBgra, nameof(rightWidth));
        if (!double.IsFinite(threshold) || threshold < 0)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        var quantum = 1;
        if (threshold > 0)
        {
            var halfQuantum = Math.Sqrt(threshold * threshold / 3.0);
            // 原本のint変換とsigned intの倍算が未定義になる領域は拒否する。
            if (halfQuantum >= 1_073_741_824.0)
                throw new ArgumentOutOfRangeException(nameof(threshold), "画像行の閾値が整数量子化の上限を超えています。");
            quantum = (int)halfQuantum * 2;
            if (quantum == 0) quantum = 1;
        }
        var total = (long)leftRows + rightRows;
        if (total > MaximumRows)
            throw new InvalidDataException("画像行の総数が比較上限を超えています。");
        var budget = new Budget(token, maximumWork);
        var left = new FileState(leftWidth, leftBgra, leftRows, budget);
        var right = new FileState(rightWidth, rightBgra, rightRows, budget);
        HashRows(left, threshold > 0, quantum, budget);
        HashRows(right, threshold > 0, quantum, budget);

        // 同じfull hashだけを同bucketへ置けば、原本の代表探索順は変わらない。
        // 異hashのbucket衝突は意味を持たず、Dictionaryの列挙順にも依存しない。
        var bucketCount = 1;
        while (bucketCount < total) bucketCount <<= 1;
        var heads = budget.Array<int>(bucketCount, sizeof(int));
        System.Array.Fill(heads, -1);
        var classes = budget.Array<Representative>((int)total, 32);
        var classCount = 0;
        Classify(left, 0);
        Classify(right, 1);

        var start = 0;
        var limit = Math.Min(leftRows, rightRows);
        while (start < limit)
        {
            budget.Spend();
            if (left.ClassIds[start] != right.ClassIds[start]) break;
            start++;
        }
        var suffix = 0;
        while (suffix < limit - start)
        {
            budget.Spend();
            if (left.ClassIds[leftRows - suffix - 1] != right.ClassIds[rightRows - suffix - 1]) break;
            suffix++;
        }
        Cleanup(left, true, start, leftRows - suffix - 1, classes, budget);
        Cleanup(right, false, start, rightRows - suffix - 1, classes, budget);
        CompareRecords(left, right, budget);
        var script = EmitScript(left, right, budget);
        token.ThrowIfCancellationRequested();
        return new(script, left.Hashes, right.Hashes, budget.Work);

        void Classify(FileState file, int side)
        {
            for (var row = 0; row < file.RowCount; row++)
            {
                budget.Spend();
                var hash = file.Hashes[row];
                var bucket = (int)(hash & (uint)(bucketCount - 1));
                var id = heads[bucket];
                while (id >= 0)
                {
                    budget.Spend();
                    ref var candidate = ref classes[id];
                    var representativeFile = candidate.Side == 0 ? left : right;
                    if (candidate.Hash == hash && RowsEqual(representativeFile, candidate.Row, file, row,
                        threshold * threshold, threshold > 0, budget)) break;
                    id = candidate.Next;
                }
                if (id < 0)
                {
                    id = classCount++;
                    classes[id] = new Representative
                    {
                        Hash = hash, Next = heads[bucket], Side = side, Row = row
                    };
                    heads[bucket] = id;
                }
                if (side == 0) classes[id].LeftCount++;
                else classes[id].RightCount++;
                file.ClassIds[row] = id;
            }
        }
    }

    private static int ValidateRows(int width, byte[] pixels, string parameter)
    {
        if (width <= 0 || width > int.MaxValue / 4)
            throw new ArgumentOutOfRangeException(parameter);
        var rowBytes = width * 4;
        if (pixels.Length % rowBytes != 0)
            throw new ArgumentException("画像行の幅とBGRAバッファ長が一致しません。", parameter);
        return pixels.Length / rowBytes;
    }

    private static void HashRows(FileState file, bool quantize, int quantum, Budget budget)
    {
        var rowBytes = file.Width * 4;
        for (var row = 0; row < file.RowCount; row++)
        {
            uint hash = 5381;
            var end = (row + 1) * rowBytes;
            for (var i = row * rowBytes; i < end; i++)
            {
                budget.Spend();
                // Windowsのconst charはsigned。除算は負数も0方向へ切り捨てる。
                var term = quantize ? (unchecked((sbyte)file.Pixels[i]) / quantum * quantum) & 255 : file.Pixels[i];
                hash = unchecked((hash + (hash << 5)) ^ (uint)term);
            }
            file.Hashes[row] = hash;
        }
    }

    private static bool RowsEqual(FileState a, int aRow, FileState b, int bRow,
        double thresholdSquared, bool useThreshold, Budget budget)
    {
        if (a.Width != b.Width) return false;
        var aStart = aRow * a.Width * 4;
        var bStart = bRow * b.Width * 4;
        for (var x = 0; x < a.Width; x++)
        {
            budget.Spend();
            var ai = aStart + x * 4;
            var bi = bStart + x * 4;
            var blue = a.Pixels[ai] - b.Pixels[bi];
            var green = a.Pixels[ai + 1] - b.Pixels[bi + 1];
            var red = a.Pixels[ai + 2] - b.Pixels[bi + 2];
            var alpha = a.Pixels[ai + 3] - b.Pixels[bi + 3];
            var distance = blue * blue + green * green + red * red + alpha * alpha;
            if (useThreshold ? distance > thresholdSquared : distance != 0) return false;
        }
        return true;
    }

    // Diff.hpp xdl_bogosqrt。通常の整数sqrtへ変更しない。
    private static int BogoSqrt(int n)
    {
        var result = 1;
        for (; n > 0; n >>= 2) result <<= 1;
        return result;
    }

    private static void Cleanup(FileState file, bool isLeft, int start, int end,
        Representative[] classes, Budget budget)
    {
        var discard = budget.Array<byte>(file.RowCount + 1, sizeof(byte));
        var matchLimit = Math.Min(BogoSqrt(file.RowCount), 1024);
        for (var row = start; row <= end; row++)
        {
            budget.Spend();
            var representative = classes[file.ClassIds[row]];
            var matches = isLeft ? representative.RightCount : representative.LeftCount;
            discard[row] = (byte)(matches == 0 ? 0 : matches >= matchLimit ? 2 : 1);
        }
        for (var row = start; row <= end; row++)
        {
            budget.Spend();
            if (discard[row] == 1 || discard[row] == 2 && !CleanMultiMatch(discard, row, start, end, budget))
            {
                file.Indices[file.ReducedCount] = row;
                file.Reduced[file.ReducedCount++] = file.ClassIds[row];
            }
            else file.Changed[row] = 1;
        }
    }

    private static bool CleanMultiMatch(byte[] discard, int row, int start, int end, Budget budget)
    {
        start = Math.Max(start, row - 100);
        end = Math.Min(end, row + 100);
        var noMatchBefore = 0;
        var multiMatchBefore = 1;
        for (var r = 1; row - r >= start; r++)
        {
            budget.Spend();
            if (discard[row - r] == 0) noMatchBefore++;
            else if (discard[row - r] == 2) multiMatchBefore++;
            else break;
        }
        if (noMatchBefore == 0) return false;
        var noMatchAfter = 0;
        var multiMatchAfter = 1;
        for (var r = 1; row + r <= end; r++)
        {
            budget.Spend();
            if (discard[row + r] == 0) noMatchAfter++;
            else if (discard[row + r] == 2) multiMatchAfter++;
            else break;
        }
        if (noMatchAfter == 0) return false;
        var multiple = multiMatchBefore + multiMatchAfter;
        return multiple * 4 < multiple + noMatchBefore + noMatchAfter;
    }

    private static void CompareRecords(FileState left, FileState right, Budget budget)
    {
        var count = left.ReducedCount + right.ReducedCount;
        var diagonalCount = count + 3;
        var forward = budget.Array<int>(diagonalCount, sizeof(int));
        var backward = budget.Array<int>(diagonalCount, sizeof(int));
        var offset = right.ReducedCount + 1;
        var maxCost = Math.Max(BogoSqrt(diagonalCount), 256);
        // 再帰を明示stackへ移し、highを先に積んで原本のlow→high順を保持する。
        var pending = budget.Array<Box>(Math.Max(count, 1), 24);
        var pendingCount = 1;
        pending[0] = new(0, left.ReducedCount, 0, right.ReducedCount, false);
        while (pendingCount > 0)
        {
            budget.Spend();
            var box = pending[--pendingCount];
            var off1 = box.Off1; var lim1 = box.Lim1;
            var off2 = box.Off2; var lim2 = box.Lim2;
            while (off1 < lim1 && off2 < lim2)
            {
                budget.Spend();
                if (left.Reduced[off1] != right.Reduced[off2]) break;
                off1++; off2++;
            }
            while (off1 < lim1 && off2 < lim2)
            {
                budget.Spend();
                if (left.Reduced[lim1 - 1] != right.Reduced[lim2 - 1]) break;
                lim1--; lim2--;
            }
            if (off1 == lim1)
            {
                for (; off2 < lim2; off2++)
                {
                    budget.Spend();
                    right.Changed[right.Indices[off2]] = 1;
                }
            }
            else if (off2 == lim2)
            {
                for (; off1 < lim1; off1++)
                {
                    budget.Spend();
                    left.Changed[left.Indices[off1]] = 1;
                }
            }
            else
            {
                var split = Split(left.Reduced, off1, lim1, right.Reduced, off2, lim2,
                    forward, backward, offset, box.NeedMinimum, maxCost, budget);
                // 内部境界異常を近似結果や無限再帰として隠さない。
                if (split.I1 < off1 || split.I1 > lim1 || split.I2 < off2 || split.I2 > lim2
                    || split.I1 == off1 && split.I2 == off2 || split.I1 == lim1 && split.I2 == lim2)
                    throw new InvalidDataException("画像行のMyers分割が進行しません。");
                if (pendingCount > pending.Length - 2)
                    throw new InvalidDataException("画像行のMyers分割stackが上限を超えています。");
                pending[pendingCount++] = new(split.I1, lim1, split.I2, lim2, split.MinimumHigh);
                pending[pendingCount++] = new(off1, split.I1, off2, split.I2, split.MinimumLow);
            }
        }
    }

    // Diff.hpp:1255–1453。降順diagonal、同点、snakeとheuristicの厳密不等号を維持する。
    private static SplitPoint Split(int[] a, int off1, int lim1, int[] b, int off2, int lim2,
        int[] forward, int[] backward, int offset, bool needMinimum, int maxCost, Budget budget)
    {
        var dmin = off1 - lim2; var dmax = lim1 - off2;
        var fmid = off1 - off2; var bmid = lim1 - lim2;
        var odd = ((fmid - bmid) & 1) != 0;
        var fmin = fmid; var fmax = fmid;
        var bmin = bmid; var bmax = bmid;
        forward[offset + fmid] = off1;
        backward[offset + bmid] = lim1;
        for (var cost = 1; ; cost++)
        {
            budget.Spend();
            var gotSnake = false;
            if (fmin > dmin) forward[offset + --fmin - 1] = -1;
            else fmin++;
            if (fmax < dmax) forward[offset + ++fmax + 1] = -1;
            else fmax--;
            for (var d = fmax; d >= fmin; d -= 2)
            {
                budget.Spend();
                var i1 = forward[offset + d - 1] >= forward[offset + d + 1]
                    ? forward[offset + d - 1] + 1 : forward[offset + d + 1];
                var previous = i1;
                var i2 = i1 - d;
                while (i1 < lim1 && i2 < lim2)
                {
                    budget.Spend();
                    if (a[i1] != b[i2]) break;
                    i1++; i2++;
                }
                if (i1 - previous > SnakeCount) gotSnake = true;
                forward[offset + d] = i1;
                if (odd && bmin <= d && d <= bmax && backward[offset + d] <= i1)
                    return new(i1, i2, true, true);
            }
            if (bmin > dmin) backward[offset + --bmin - 1] = int.MaxValue;
            else bmin++;
            if (bmax < dmax) backward[offset + ++bmax + 1] = int.MaxValue;
            else bmax--;
            for (var d = bmax; d >= bmin; d -= 2)
            {
                budget.Spend();
                var i1 = backward[offset + d - 1] < backward[offset + d + 1]
                    ? backward[offset + d - 1] : backward[offset + d + 1] - 1;
                var previous = i1;
                var i2 = i1 - d;
                while (i1 > off1 && i2 > off2)
                {
                    budget.Spend();
                    if (a[i1 - 1] != b[i2 - 1]) break;
                    i1--; i2--;
                }
                if (previous - i1 > SnakeCount) gotSnake = true;
                backward[offset + d] = i1;
                if (!odd && fmin <= d && d <= fmax && i1 <= forward[offset + d])
                    return new(i1, i2, true, true);
            }
            if (needMinimum) continue;
            if (gotSnake && cost > HeuristicMinimum)
            {
                var best = 0;
                var point = default(SplitPoint);
                for (var d = fmax; d >= fmin; d -= 2)
                {
                    budget.Spend();
                    var i1 = forward[offset + d]; var i2 = i1 - d;
                    var value = i1 - off1 + i2 - off2 - Math.Abs(d - fmid);
                    if (value <= 4 * cost || value <= best || off1 + SnakeCount > i1 || i1 >= lim1
                        || off2 + SnakeCount > i2 || i2 >= lim2) continue;
                    for (var k = 1; ; k++)
                    {
                        budget.Spend();
                        if (a[i1 - k] != b[i2 - k]) break;
                        if (k != SnakeCount) continue;
                        best = value; point = new(i1, i2, true, false); break;
                    }
                }
                if (best > 0) return point;
                for (var d = bmax; d >= bmin; d -= 2)
                {
                    budget.Spend();
                    var i1 = backward[offset + d]; var i2 = i1 - d;
                    var value = lim1 - i1 + lim2 - i2 - Math.Abs(d - bmid);
                    if (value <= 4 * cost || value <= best || off1 >= i1 || i1 > lim1 - SnakeCount
                        || off2 >= i2 || i2 > lim2 - SnakeCount) continue;
                    for (var k = 0; ; k++)
                    {
                        budget.Spend();
                        if (a[i1 + k] != b[i2 + k]) break;
                        if (k != SnakeCount - 1) continue;
                        best = value; point = new(i1, i2, false, true); break;
                    }
                }
                if (best > 0) return point;
            }
            if (cost < maxCost) continue;
            var fbest = -1; var fbest1 = -1;
            for (var d = fmax; d >= fmin; d -= 2)
            {
                budget.Spend();
                var i1 = Math.Min(forward[offset + d], lim1); var i2 = i1 - d;
                if (lim2 < i2) { i1 = lim2 + d; i2 = lim2; }
                if (fbest < i1 + i2) { fbest = i1 + i2; fbest1 = i1; }
            }
            var bbest = int.MaxValue; var bbest1 = int.MaxValue;
            for (var d = bmax; d >= bmin; d -= 2)
            {
                budget.Spend();
                var i1 = Math.Max(off1, backward[offset + d]); var i2 = i1 - d;
                if (i2 < off2) { i1 = off2 + d; i2 = off2; }
                if (i1 + i2 < bbest) { bbest = i1 + i2; bbest1 = i1; }
            }
            return lim1 + lim2 - bbest < fbest - (off1 + off2)
                ? new(fbest1, fbest - fbest1, true, false)
                : new(bbest1, bbest - bbest1, false, true);
        }
    }

    private static string EmitScript(FileState left, FileState right, Budget budget)
    {
        var script = budget.Array<char>(left.RowCount + right.RowCount, sizeof(char));
        var count = 0; var i1 = 0; var i2 = 0;
        while (i1 < left.RowCount || i2 < right.RowCount)
        {
            budget.Spend();
            var changed1 = i1 < left.RowCount && left.Changed[i1] != 0;
            var changed2 = i2 < right.RowCount && right.Changed[i2] != 0;
            if (changed1 && changed2) { script[count++] = '!'; i1++; i2++; }
            else if (changed2) { script[count++] = '+'; i2++; }
            else if (changed1) { script[count++] = '-'; i1++; }
            else if (i1 < left.RowCount && i2 < right.RowCount) { script[count++] = '='; i1++; i2++; }
            else throw new InvalidDataException("画像行の変更scriptが元行を消費できません。");
        }
        budget.Reserve(count, sizeof(char));
        return new string(script, 0, count);
    }

    private struct Representative
    {
        internal uint Hash;
        internal int Next, Side, Row, LeftCount, RightCount;
    }

    private readonly record struct Box(int Off1, int Lim1, int Off2, int Lim2, bool NeedMinimum);
    private readonly record struct SplitPoint(int I1, int I2, bool MinimumLow, bool MinimumHigh);

    private sealed class FileState
    {
        internal readonly int Width, RowCount;
        internal readonly byte[] Pixels, Changed;
        internal readonly uint[] Hashes;
        internal readonly int[] ClassIds, Indices, Reduced;
        internal int ReducedCount;

        internal FileState(int width, byte[] pixels, int rows, Budget budget)
        {
            Width = width; Pixels = pixels; RowCount = rows;
            Hashes = budget.Array<uint>(rows, sizeof(uint));
            ClassIds = budget.Array<int>(rows, sizeof(int));
            Indices = budget.Array<int>(rows, sizeof(int));
            Reduced = budget.Array<int>(rows, sizeof(int));
            Changed = budget.Array<byte>(rows + 1, sizeof(byte));
        }
    }

    private sealed class Budget(CancellationToken token, long maximumWork)
    {
        internal long Work { get; private set; }
        private long metadataBytes;
        private int calls;

        internal void Spend(long amount = 1)
        {
            if ((calls++ & 1023) == 0) token.ThrowIfCancellationRequested();
            if (amount > maximumWork - Work)
                throw new InvalidDataException("画像行の累積比較作業量が上限を超えています。");
            Work += amount;
        }

        internal void Reserve(int count, int elementBytes)
        {
            token.ThrowIfCancellationRequested();
            var bytes = (long)count * elementBytes + 32;
            if (bytes > MaximumMetadataBytes - metadataBytes)
                throw new InvalidDataException("画像行の累積メタデータ確保量が上限を超えています。");
            Spend(count);
            metadataBytes += bytes;
        }

        internal T[] Array<T>(int count, int elementBytes)
        {
            Reserve(count, elementBytes);
            return new T[count];
        }
    }
}
