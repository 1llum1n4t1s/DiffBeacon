/*
 * Image row/column alignment ported from WinIMerge ImgDiffBuffer.hpp,
 * revision da639cdfaeca87aaad0eaceec509afa11ad61421.
 * Copyright (C) WinIMerge contributors.
 * This program is free software; you can redistribute it and/or modify it
 * under the terms of the GNU General Public License as published by the
 * Free Software Foundation; either version 2, or (at your option) any later
 * version. This program is distributed without any warranty; without even
 * the implied warranty of merchantability or fitness for a particular purpose.
 * See <https://www.gnu.org/licenses/> for the GNU General Public License.
 */

namespace DiffBeacon.App;

internal static class ImageLineAlignment
{
    internal const long MaximumPixelBufferBytes = 3L * 16_000_000 * 4;

    internal sealed record LineDiffInfo(int[] Begin, int[] End, int DisplayBegin,
        int[] DisplayEnd, int DisplayEndMaximum, int Operation);
    internal sealed record PaneMapping(int[] SourceLines, bool[] GhostFlags);
    internal readonly record struct Position(bool Inside, int X, int Y);

    internal sealed record Result(IReadOnlyList<ImageComparisonEngine.DecodedFrame> Frames,
        IReadOnlyList<LineDiffInfo> LineDiffInfos, IReadOnlyList<PaneMapping> PaneMappings,
        bool Horizontal, long Work, IReadOnlyList<ImageComparisonEngine.DecodedFrame> ViewFrames)
    {
        // offsetは呼出し側で減算する。戻り値は方向変換済みview内の座標。
        // 原本はghost時にclamp指定によらず区間end（先頭なら-1）を返す。
        internal Position ConvertToRealPosition(int pane, int x, int y, bool clamp = true)
        {
            if ((uint)pane >= (uint)Frames.Count) throw new ArgumentOutOfRangeException(nameof(pane));
            var view = ViewFrames[pane];
            if (LineDiffInfos.Count == 0)
            {
                var rx = Bound(x, view.Width, clamp, out var xInside);
                var ry = Bound(y, view.Height, clamp, out var yInside);
                return new(xInside && yInside, rx, ry);
            }
            var cross = Horizontal ? y : x;
            var axis = Horizontal ? x : y;
            var crossSize = Horizontal ? Frames[pane].Height : Frames[pane].Width;
            var realCross = Bound(cross, crossSize, clamp, out var inside);
            int realAxis;
            foreach (var info in LineDiffInfos)
            {
                if (axis <= info.DisplayEnd[pane])
                {
                    if (axis < 0) { realAxis = clamp ? 0 : axis; inside = false; }
                    else realAxis = axis - info.DisplayBegin + info.Begin[pane];
                    return Horizontal ? new(inside, realAxis, realCross) : new(inside, realCross, realAxis);
                }
                if (axis <= info.DisplayEndMaximum)
                {
                    realAxis = info.End[pane];
                    return Horizontal ? new(false, realAxis, realCross) : new(false, realCross, realAxis);
                }
            }
            var last = LineDiffInfos[^1];
            var mapped = (long)axis - last.DisplayEndMaximum + last.End[pane];
            if (mapped < int.MinValue || mapped > int.MaxValue)
                throw new ArgumentOutOfRangeException(Horizontal ? nameof(x) : nameof(y));
            realAxis = (int)mapped;
            var axisSize = Horizontal ? view.Width : view.Height;
            if (realAxis >= axisSize) { if (clamp) realAxis = axisSize - 1; inside = false; }
            return Horizontal ? new(inside, realAxis, realCross) : new(inside, realCross, realAxis);
        }

        private static int Bound(int value, int size, bool clamp, out bool inside)
        {
            inside = value >= 0 && value < size;
            return inside || !clamp ? value : value < 0 ? 0 : size - 1;
        }
    }

    // rawviewを変更せず、全候補の作成に成功してから一括で返す。
    internal static Result Align(IReadOnlyList<ImageComparisonEngine.DecodedFrame> viewFrames,
        bool horizontal, double threshold, CancellationToken token = default,
        long maximumWork = ImageLineDiffer.MaximumWork)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(viewFrames);
        if (maximumWork < 0 || maximumWork > ImageLineDiffer.MaximumWork)
            throw new ArgumentOutOfRangeException(nameof(maximumWork));
        if (viewFrames.Count is not (2 or 3)) throw new ArgumentException("画像は二者または三者です。", nameof(viewFrames));
        if (!double.IsFinite(threshold) || threshold < 0
            || threshold > 0 && Math.Sqrt(threshold * threshold / 3.0) >= 1_073_741_824.0)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        var budget = new Budget(token, maximumWork);
        var sources = budget.Array<ImageComparisonEngine.DecodedFrame>(viewFrames.Count, 8);
        long totalRows = 0;
        for (var pane = 0; pane < sources.Length; pane++)
        {
            token.ThrowIfCancellationRequested();
            var frame = viewFrames[pane];
            if (frame is null || frame.Width <= 0 || frame.Height <= 0
                || (long)frame.Width * frame.Height > ImageComparisonEngine.MaximumPixels
                || frame.Pixels is null || frame.Pixels.LongLength != (long)frame.Width * frame.Height * 4)
                throw new ArgumentException("画像フレームの寸法またはBGRA長が不正です。", nameof(viewFrames));
            totalRows += horizontal ? frame.Width : frame.Height;
            sources[pane] = frame;
        }
        if (totalRows > ImageLineDiffer.MaximumRows)
            throw new InvalidDataException("画像整列の総行数が比較上限を超えています。");
        var rows = budget.Array<ImageComparisonEngine.DecodedFrame>(sources.Length, 8);
        for (var pane = 0; pane < rows.Length; pane++)
        {
            var source = sources[pane];
            if (!horizontal) { rows[pane] = source; continue; }
            var transposed = budget.Pixels(source.Pixels.Length);
            // 原本の-90度回転: 比較row=x、row内画素は元yの降順。
            for (var x = 0; x < source.Width; x++)
            for (var y = 0; y < source.Height; y++)
            {
                budget.Spend();
                source.Pixels.AsSpan((y * source.Width + x) * 4, 4)
                    .CopyTo(transposed.AsSpan((x * source.Height + source.Height - 1 - y) * 4, 4));
            }
            rows[pane] = new(source.Number, source.Height, source.Width, transposed);
        }
        Block[] blocks;
        int blockCount;
        if (sources.Length == 2)
            (blocks, blockCount) = Pair(0, 1);
        else
        {
            var (diff10, count10) = Pair(1, 0);
            var (diff12, count12) = Pair(1, 2);
            (blocks, blockCount) = MergeThree(diff10, count10, diff12, count12, sources, threshold, budget);
        }
        var infos = Prime(blocks, blockCount, sources.Length, sources[0].Height, budget);
        // Primeのheight0はHでも元height。原本はその戻り値を利用せず、
        // CopyImageWithGhostLineがtranspose済みsrc[0].heightから高さを再計算する。
        var alignedRows = rows[0].Height;
        if (infos.Length > 0)
        {
            var last = infos[^1];
            alignedRows = checked(last.DisplayEndMaximum + rows[0].Height - last.End[0]);
        }
        if (alignedRows <= 0 || alignedRows > ImageLineDiffer.MaximumRows)
            throw new InvalidDataException("画像整列の出力行数が上限を超えています。");
        var crossMaximum = 0;
        foreach (var rowFrame in rows) crossMaximum = Math.Max(crossMaximum, rowFrame.Width);
        if ((long)crossMaximum * alignedRows > ImageComparisonEngine.MaximumPixels)
            throw new InvalidDataException("画像整列の共通キャンバスが1600万ピクセルを超えています。");
        var aligned = budget.Array<ImageComparisonEngine.DecodedFrame>(sources.Length, 8);
        var mappings = budget.Array<PaneMapping>(sources.Length, 8);
        for (var pane = 0; pane < sources.Length; pane++)
        {
            var source = sources[pane];
            var rowFrame = rows[pane];
            var pixels = budget.Pixels(checked(rowFrame.Width * alignedRows * 4));
            var sourceLines = budget.Array<int>(alignedRows, sizeof(int));
            var ghostFlags = budget.Array<bool>(alignedRows, sizeof(byte));
            System.Array.Fill(sourceLines, -1);
            System.Array.Fill(ghostFlags, true);
            var previousEnd = -1;
            var displayCursor = 0;
            foreach (var info in infos)
            {
                Copy(previousEnd + 1, info.Begin[pane] - 1, displayCursor);
                Copy(info.Begin[pane], info.End[pane], info.DisplayBegin);
                for (var ghost = info.DisplayEnd[pane] + 1; ghost <= info.DisplayEndMaximum; ghost++)
                {
                    budget.Spend();
                    if ((uint)ghost >= (uint)alignedRows) throw UnsafeRange();
                    sourceLines[ghost] = info.End[pane];
                }
                previousEnd = info.End[pane];
                displayCursor = info.DisplayEndMaximum + 1;
            }
            Copy(previousEnd + 1, rowFrame.Height - 1, displayCursor);
            aligned[pane] = new(source.Number, horizontal ? alignedRows : source.Width,
                horizontal ? source.Height : alignedRows, pixels);
            mappings[pane] = new(sourceLines, ghostFlags);

            void Copy(int first, int last, int destination)
            {
                if (first < 0 || last < first - 1 || last >= rowFrame.Height || destination < 0
                    || (long)destination + last - first + 1 > alignedRows) throw UnsafeRange();
                for (var originalRow = first; originalRow <= last; originalRow++, destination++)
                {
                    budget.Spend();
                    sourceLines[destination] = originalRow;
                    ghostFlags[destination] = false;
                    if (!horizontal)
                    {
                        budget.Spend(source.Width);
                        source.Pixels.AsSpan(originalRow * source.Width * 4, source.Width * 4)
                            .CopyTo(pixels.AsSpan(destination * source.Width * 4, source.Width * 4));
                    }
                    else
                    {
                        // +90度で戻したcanvasを直接生成し、二度目の回転bufferは作らない。
                        for (var y = 0; y < source.Height; y++)
                        {
                            budget.Spend();
                            source.Pixels.AsSpan((y * source.Width + originalRow) * 4, 4)
                                .CopyTo(pixels.AsSpan((y * alignedRows + destination) * 4, 4));
                        }
                    }
                }
            }
        }
        token.ThrowIfCancellationRequested();
        return new(aligned, infos, mappings, horizontal, budget.Work, sources);

        (Block[] Blocks, int Count) Pair(int first, int second)
        {
            var a = rows[first]; var b = rows[second];
            // kernel内部の累積確保量を上から予約する。入力画素はkernelが借用する。
            budget.ReserveMetadata(((long)a.Height + b.Height) * 100 + 2048);
            var diff = ImageLineDiffer.Compare(a.Width, a.Pixels, b.Width, b.Pixels, threshold,
                token, maximumWork - budget.Work);
            budget.Spend(diff.Work);
            return MakeBlocks(diff.Script, budget);
        }
    }

    // MakeLineDiff:2176–2239。空側end=begin-1、両側連続の場合のみ結合する。
    private static (Block[] Blocks, int Count) MakeBlocks(string script, Budget budget)
    {
        var blocks = budget.Array<Block>(script.Length, 32);
        var count = 0; var left = 0; var right = 0;
        foreach (var operation in script)
        {
            budget.Spend();
            var next = new Block { B0 = left, E0 = left - 1, B1 = right, E1 = right - 1, Op = 4 };
            switch (operation)
            {
                case '-': next.E0 = left++; break;
                case '+': next.E1 = right++; break;
                case '!': next.E0 = left++; next.E1 = right++; break;
                case '=': left++; right++; continue;
                default: throw new InvalidDataException("画像行の編集scriptが不正です。");
            }
            if (count > 0 && blocks[count - 1].E0 + 1 == next.B0 && blocks[count - 1].E1 + 1 == next.B1)
            {
                next.B0 = blocks[count - 1].B0; next.B1 = blocks[count - 1].B1;
                blocks[count - 1] = next;
            }
            else blocks[count++] = next;
        }
        return (blocks, count);
    }

    // Make3WayLineDiff:176–369。中→左／中→右を原本の同点・重なり順で統合する。
    private static (Block[] Blocks, int Count) MergeThree(Block[] diff10, int count10,
        Block[] diff12, int count12, ImageComparisonEngine.DecodedFrame[] sources, double threshold, Budget budget)
    {
        var merged = budget.Array<Block>(count10 + count12, 32);
        var i10 = 0; var i12 = 0; var count = 0;
        var last0 = 0; var last1 = 0; var last2 = 0;
        while (i10 < count10 || i12 < count12)
        {
            budget.Spend();
            Block first10 = default, last10 = default, first12 = default, last12 = default;
            bool firstIs12;
            if (i10 >= count10) { first12 = last12 = diff12[i12]; firstIs12 = true; }
            else if (i12 >= count12) { first10 = last10 = diff10[i10]; firstIs12 = false; }
            else
            {
                first10 = last10 = diff10[i10]; first12 = last12 = diff12[i12];
                firstIs12 = first12.B0 <= first10.B0;
            }
            var lastIs12 = firstIs12;
            var end10 = i10; var end12 = i12;
            while (end10 < count10 && end12 < count12)
            {
                budget.Spend();
                var d10 = diff10[end10]; var d12 = diff12[end12];
                if (d10.E0 == d12.E0)
                {
                    end10++; lastIs12 = true; last10 = d10; last12 = d12; break;
                }
                if (lastIs12 ? Math.Max(d12.B0, d12.E0) < d10.B0 : Math.Max(d10.B0, d10.E0) < d12.B0) break;
                if (d12.E0 > d10.E0) { end10++; lastIs12 = true; }
                else { end12++; lastIs12 = false; }
                last10 = d10; last12 = d12;
            }
            if (lastIs12) end12++;
            else end10++;
            var block = new Block();
            if (firstIs12)
            {
                block.B1 = first12.B0; block.B2 = first12.B1;
                block.B0 = end10 == i10 ? block.B1 - last1 + last0 : block.B1 - first10.B0 + first10.B1;
            }
            else
            {
                block.B0 = first10.B1; block.B1 = first10.B0;
                block.B2 = end12 == i12 ? block.B1 - last1 + last2 : block.B1 - first12.B0 + first12.B1;
            }
            if (lastIs12)
            {
                block.E1 = last12.E0; block.E2 = last12.E1;
                block.E0 = end10 == i10 ? block.E1 - last1 + last0 : block.E1 - last10.E0 + last10.E1;
            }
            else
            {
                block.E0 = last10.E1; block.E1 = last10.E0;
                block.E2 = end12 == i12 ? block.E1 - last1 + last2 : block.E1 - last12.E0 + last12.E1;
            }
            last0 = block.E0 + 1; last1 = block.E1 + 1; last2 = block.E2 + 1;
            block.Op = i10 == end10 ? 3 : i12 == end12 ? 1 : CompareOuter(block, sources, threshold, budget) ? 2 : 4;
            merged[count++] = block; i10 = end10; i12 = end12;
        }
        for (var i = 0; i + 1 < count; i++)
        {
            budget.Spend();
            if (merged[i].E0 >= merged[i + 1].B0) merged[i].E0 = merged[i + 1].B0 - 1;
            if (merged[i].E1 >= merged[i + 1].B1) merged[i].E1 = merged[i + 1].B1 - 1;
            if (merged[i].E2 >= merged[i + 1].B2) merged[i].E2 = merged[i + 1].B2 - 1;
        }
        return (merged, count);
    }

    // PreprocessImages compfunc02:2309–2322。Hでも元viewの「行」を再比較する。
    private static bool CompareOuter(Block block, ImageComparisonEngine.DecodedFrame[] frames, double threshold, Budget budget)
    {
        var length0 = block.E0 + 1 - block.B0; var length2 = block.E2 + 1 - block.B2;
        if (length0 < 0 || length2 < 0) throw UnsafeRange();
        if (length0 != length2) return false;
        var a = frames[0]; var b = frames[2];
        for (var row = 0; row < length0; row++)
        {
            budget.Spend();
            var ay = block.B0 + row; var by = block.B2 + row;
            // alineEqualsは幅が異なる場合、scanlineを逆参照せずfalseを返す。
            if (a.Width != b.Width) return false;
            // 原本がscanLineを範囲外で読む条件はmanagedで成功扱いにしない。
            if ((uint)ay >= (uint)a.Height || (uint)by >= (uint)b.Height) throw UnsafeRange();
            for (var x = 0; x < a.Width; x++)
            {
                budget.Spend();
                var ai = (ay * a.Width + x) * 4; var bi = (by * b.Width + x) * 4;
                var blue = a.Pixels[ai] - b.Pixels[bi]; var green = a.Pixels[ai + 1] - b.Pixels[bi + 1];
                var red = a.Pixels[ai + 2] - b.Pixels[bi + 2]; var alpha = a.Pixels[ai + 3] - b.Pixels[bi + 3];
                var distance = blue * blue + green * green + red * red + alpha * alpha;
                if (threshold > 0 ? distance > threshold * threshold : distance != 0) return false;
            }
        }
        return true;
    }

    // PrimeLineDiffInfos:2242–2261。dendmax初期値0と未使用height0計算も保持する。
    private static LineDiffInfo[] Prime(Block[] blocks, int count, int panes, int height0, Budget budget)
    {
        var infos = budget.Array<LineDiffInfo>(count, 8);
        uint displayLines = 0;
        for (var i = 0; i < count; i++)
        {
            budget.Spend();
            var block = blocks[i];
            var begin = budget.Array<int>(3, sizeof(int));
            var end = budget.Array<int>(3, sizeof(int));
            var displayEnd = budget.Array<int>(3, sizeof(int));
            begin[0] = block.B0; begin[1] = block.B1; begin[2] = block.B2;
            end[0] = block.E0; end[1] = block.E1; end[2] = block.E2;
            System.Array.Fill(displayEnd, -1);
            displayLines = unchecked(displayLines + (uint)(block.B0 - (i > 0 ? blocks[i - 1].E0 + 1 : 0)));
            if (displayLines > ImageLineDiffer.MaximumRows) throw UnsafeRange();
            var displayBegin = (int)displayLines;
            var maximum = 0;
            for (var pane = 0; pane < panes; pane++)
            {
                if (begin[pane] < 0 || end[pane] < begin[pane] - 1) throw UnsafeRange();
                displayEnd[pane] = checked(displayBegin + end[pane] - begin[pane]);
                maximum = Math.Max(maximum, displayEnd[pane]);
            }
            if (maximum >= ImageLineDiffer.MaximumRows) throw UnsafeRange();
            budget.ReserveMetadata(64);
            infos[i] = new(begin, end, displayBegin, displayEnd, maximum, block.Op);
            displayLines = (uint)(maximum + 1);
        }
        // unsigned wrapは原本どおり。Hでは値をcanvas寸法として使用しない。
        _ = unchecked(displayLines + (uint)(height0 - (count > 0 ? blocks[count - 1].E0 + 1 : 0)));
        return infos;
    }

    private static InvalidDataException UnsafeRange() => new("画像整列の原本経路が画像または出力の範囲外を参照します。");

    private struct Block
    {
        internal int B0, E0, B1, E1, B2, E2, Op;
    }

    private sealed class Budget(CancellationToken token, long maximumWork)
    {
        internal long Work { get; private set; }
        private long metadataBytes, pixelBytes;
        private int calls;

        internal void Spend(long amount = 1)
        {
            if ((calls++ & 1023) == 0) token.ThrowIfCancellationRequested();
            if (amount > maximumWork - Work)
                throw new InvalidDataException("画像整列の累積作業量が上限を超えています。");
            Work += amount;
        }

        internal void ReserveMetadata(long bytes)
        {
            token.ThrowIfCancellationRequested();
            if (bytes > ImageLineDiffer.MaximumMetadataBytes - metadataBytes)
                throw new InvalidDataException("画像整列の累積メタデータ確保量が上限を超えています。");
            metadataBytes += bytes;
        }

        internal T[] Array<T>(int count, int elementBytes)
        {
            ReserveMetadata((long)count * elementBytes + 32);
            Spend(count);
            return new T[count];
        }

        internal byte[] Pixels(int count)
        {
            token.ThrowIfCancellationRequested();
            if (count > MaximumPixelBufferBytes - pixelBytes)
                throw new InvalidDataException("画像整列の累積画素buffer確保量が上限を超えています。");
            Spend(count / 4);
            pixelBytes += count;
            return new byte[count];
        }
    }
}
