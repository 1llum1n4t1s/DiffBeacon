namespace DiffBeacon.App;

// WinIMerge v1.0.54 ImgDiffBuffer.hpp の CompareImages2 / MarkDiffIndex / MarkDiffIndex3way を移植。
// GPL version 2 or later（原本のライセンス表示を参照）。
// 原本とライセンス: tests/Fixtures/ImageRegions/reference-source / LICENSE.txt
internal static class ImageRegionDiffer
{
    internal readonly record struct Region(int Id, int Op, int Left, int Top, int Right, int Bottom);
    internal sealed record Result(int Width, int Height, int Columns, int Rows, byte[] Pair01,
        byte[]? Pair21, byte[]? Pair02, int[] RegionIds, IReadOnlyList<Region> Regions, int ConflictCount);

    internal static Result Compare(IReadOnlyList<ImageComparisonEngine.DecodedFrame> frames,
        int blockSize = 8, double threshold = 0, CancellationToken token = default)
    {
        if (frames.Count is not (2 or 3)) throw new ArgumentException("画像は二者または三者です。");
        if (blockSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(blockSize));
        if (!double.IsFinite(threshold) || threshold < 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        var width = 0; var height = 0;
        foreach (var frame in frames)
        {
            token.ThrowIfCancellationRequested();
            if (frame.Width <= 0 || frame.Height <= 0 || (long)frame.Width * frame.Height > ImageComparisonEngine.MaximumPixels
                || frame.Pixels.LongLength != (long)frame.Width * frame.Height * 4)
                throw new ArgumentException("画像フレームの寸法またはBGRAバッファ長が不正です。");
            width = Math.Max(width, frame.Width); height = Math.Max(height, frame.Height);
        }
        if ((long)width * height > ImageComparisonEngine.MaximumPixels)
            throw new InvalidOperationException("比較キャンバスが1600万ピクセルを超えます。");
        var columns = (width + blockSize - 1) / blockSize;
        var rows = (height + blockSize - 1) / blockSize;
        var length = checked(columns * rows);
        var pair01 = ComparePair(frames[0], frames[1]);
        var pair21 = frames.Count == 3 ? ComparePair(frames[2], frames[1]) : null;
        var pair02 = frames.Count == 3 ? ComparePair(frames[0], frames[2]) : null;
        var ids = new int[length];
        var regions = new List<Region>();
        var pending = new Queue<int>();
        var conflicts = 0;
        for (var index = 0; index < length; index++)
        {
            if ((index & 1023) == 0) token.ThrowIfCancellationRequested();
            if (ids[index] != 0 || !Candidate(index)) continue;
            var id = regions.Count + 1;
            var left = index % columns; var top = index / columns;
            var right = left + 1; var bottom = top + 1; var classes = 0;
            ids[index] = id; pending.Enqueue(index);
            var visited = 0;
            while (pending.TryDequeue(out var cell))
            {
                if ((visited++ & 1023) == 0) token.ThrowIfCancellationRequested();
                var x = cell % columns; var y = cell / columns;
                left = Math.Min(left, x); top = Math.Min(top, y);
                right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
                // 判定順は原本どおり。閾値による非推移的な同値も変更しない。
                classes |= pair21 is null ? 8 : pair21[cell] == 0 ? 1 : pair02![cell] == 0 ? 2 : pair01[cell] == 0 ? 4 : 8;
                for (var ny = Math.Max(0, y - 1); ny <= Math.Min(rows - 1, y + 1); ny++)
                for (var nx = Math.Max(0, x - 1); nx <= Math.Min(columns - 1, x + 1); nx++)
                {
                    var neighbor = ny * columns + nx;
                    if (ids[neighbor] != 0 || !Candidate(neighbor)) continue;
                    ids[neighbor] = id; pending.Enqueue(neighbor);
                }
            }
            var op = classes switch { 1 => 1, 2 => 2, 4 => 3, _ => 4 };
            if (op == 4) conflicts++;
            regions.Add(new(id, op, left, top, right, bottom));
        }
        token.ThrowIfCancellationRequested();
        return new(width, height, columns, rows, pair01, pair21, pair02, ids, regions, conflicts);

        bool Candidate(int index) => pair01[index] != 0 || pair21 is not null && pair21[index] != 0;

        byte[] ComparePair(ImageComparisonEngine.DecodedFrame a, ImageComparisonEngine.DecodedFrame b)
        {
            var grid = new byte[length];
            var pairWidth = Math.Max(a.Width, b.Width); var pairHeight = Math.Max(a.Height, b.Height);
            var squaredThreshold = threshold * threshold;
            for (var by = 0; by < rows; by++)
            {
                token.ThrowIfCancellationRequested();
                // 原本の unsigned 減算を保持。三者共通gridがpairより高い場合、
                // 差が0のblock行は走査なし、その後の負差はwrapして全行を走査する。
                var remaining = unchecked((uint)(pairHeight - by * blockSize));
                var blockRows = (int)Math.Min((uint)blockSize, remaining);
                for (var i = 0; i < blockRows; i++)
                {
                    var y = by * blockSize + i;
                    token.ThrowIfCancellationRequested();
                    if (y >= a.Height || y >= b.Height)
                    {
                        grid.AsSpan(by * columns, columns).Fill(1);
                        continue;
                    }
                    var aRow = y * a.Width * 4; var bRow = y * b.Width * 4;
                    if (a.Width == b.Width && threshold == 0
                        && a.Pixels.AsSpan(aRow, a.Width * 4).SequenceEqual(b.Pixels.AsSpan(bRow, b.Width * 4))) continue;
                    for (var x = 0; x < pairWidth; x++)
                    {
                        if ((x & 4095) == 0) token.ThrowIfCancellationRequested();
                        if (x >= a.Width || x >= b.Width || Different(a.Pixels, aRow + x * 4, b.Pixels, bRow + x * 4, squaredThreshold))
                            grid[by * columns + x / blockSize] = 1;
                    }
                }
            }
            return grid;
        }
    }

    internal static bool Different(byte[] a, int ai, byte[] b, int bi, double squaredThreshold)
    {
        var squaredDistance = 0;
        for (var channel = 0; channel < 4; channel++)
        {
            var distance = a[ai + channel] - b[bi + channel];
            squaredDistance += distance * distance;
        }
        return squaredDistance > squaredThreshold;
    }
}
