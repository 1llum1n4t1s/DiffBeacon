namespace DiffBeacon.App;

// WinIMerge v1.0.54 ImgMergeBuffer.hpp のDeleteRectangle/PasteImageを移植。
// GPL-2.0-or-later。無改変原本核とadapter境界はtests/Fixtures/ImageRectanglesを参照。
internal sealed partial class ImageEditSession
{
    internal bool DeleteRectangle(int pane, ImageRectangle rectangle, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!ValidPane(pane) || _readOnly[pane]) return false;
        var before = _viewFrames[pane];
        if (rectangle.Left < 0 || rectangle.Top < 0 || rectangle.Right < rectangle.Left
            || rectangle.Bottom < rectangle.Top || rectangle.Right > before.Width || rectangle.Bottom > before.Height)
            throw new ArgumentOutOfRangeException(nameof(rectangle), "削除矩形は表示原画内の半開区間で指定してください。");
        var retained = RectangleHistoryBytes(pane);
        var budget = NewBudget(token);
        budget.ReservePixels(before.Pixels.Length);
        var next = Clone(before);
        budget.Spend((long)(rectangle.Right - rectangle.Left) * (rectangle.Bottom - rectangle.Top));
        for (var y = rectangle.Top; y < rectangle.Bottom; y++)
        {
            token.ThrowIfCancellationRequested();
            next.Pixels.AsSpan((y * next.Width + rectangle.Left) * 4, (rectangle.Right - rectangle.Left) * 4).Clear();
        }
        // 有効な空矩形・同一画素でも原本と同じ一件の履歴と再比較を行う。
        AdoptEdit(pane, next, (ImageOffset[])_offsets.Clone(), retained, budget, token);
        return true;
    }

    internal bool PasteImage(int source, int destination, int x, int y, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!ValidPane(source)) return false;
        return PasteImage(destination, _viewFrames[source], x, y, token);
    }

    internal bool PasteImage(int destination, ImageComparisonEngine.DecodedFrame payload, int x, int y,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // 原本の直接PasteImageにはROガードがないが、現アプリの安全境界を維持する。
        if (!ValidPane(destination) || _readOnly[destination]) return false;
        ImageComparisonEngine.ValidateDimensions(payload.Width, payload.Height);
        if (payload.Pixels.LongLength != (long)payload.Width * payload.Height * 4)
            throw new InvalidDataException("貼付け画像の寸法と画素数が一致しません。");
        var before = _viewFrames[destination];
        var retained = RectangleHistoryBytes(destination);
        var budget = NewBudget(token);
        budget.ReservePixels(payload.Pixels.Length);
        // 同じpaneへの貼付けも書込み開始前の所有snapshotを使い、aliasを防ぐ。
        var input = Clone(payload);
        budget.ReservePixels(before.Pixels.Length);
        var next = Clone(before);
        // 原本の正の完全範囲外は負のsource indexを作るため、安全な空重なりに置換する。
        // longで先に交差を計算し、整数極値もoverflowや巨大sliceへ変換しない。
        var left = Math.Max(0L, x); var top = Math.Max(0L, y);
        var right = Math.Min((long)before.Width, (long)x + input.Width);
        var bottom = Math.Min((long)before.Height, (long)y + input.Height);
        if (left < right && top < bottom)
        {
            var width = checked((int)(right - left));
            budget.Spend((right - left) * (bottom - top));
            for (var row = (int)top; row < bottom; row++)
            {
                token.ThrowIfCancellationRequested();
                var from = checked(((row - (long)y) * input.Width + left - x) * 4);
                input.Pixels.AsSpan(checked((int)from), width * 4)
                    .CopyTo(next.Pixels.AsSpan(checked((row * next.Width + (int)left) * 4), width * 4));
            }
        }
        AdoptEdit(destination, next, (ImageOffset[])_offsets.Clone(), retained, budget, token);
        return true;
    }

    internal ImageComparisonEngine.DecodedFrame CaptureRectangle(int pane, ImageRectangle rectangle,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!ValidPane(pane)) throw new ArgumentOutOfRangeException(nameof(pane));
        var frame = _viewFrames[pane];
        if (rectangle.Left < 0 || rectangle.Top < 0 || rectangle.Right <= rectangle.Left
            || rectangle.Bottom <= rectangle.Top || rectangle.Right > frame.Width || rectangle.Bottom > frame.Height)
            throw new ArgumentOutOfRangeException(nameof(rectangle), "コピー矩形は表示原画内の空でない半開区間で指定してください。");
        var width = rectangle.Right - rectangle.Left; var height = rectangle.Bottom - rectangle.Top;
        var budget = NewBudget(token);
        var pixels = AllocatePixels(width, height, budget);
        budget.Spend((long)width * height);
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            frame.Pixels.AsSpan(((y + rectangle.Top) * frame.Width + rectangle.Left) * 4, width * 4)
                .CopyTo(pixels.AsSpan(y * width * 4));
        }
        token.ThrowIfCancellationRequested();
        _work += budget.Work;
        return new(frame.Number, width, height, pixels);
    }

    // Resizeの旧bitmapはTemporaryTransformation前のraw。表示方向のまま保持する方式へ変更しない。
    internal bool Resize(int pane, int width, int height, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!ValidPane(pane) || _readOnly[pane]) return false;
        ImageComparisonEngine.ValidateDimensions(width, height);
        var old = _frames[pane];
        if (width == old.Width && height == old.Height) return false;
        ValidateEditCanvas(width, height, pane, _offsets);
        var retained = RectangleHistoryBytes(pane, (long)width * height * 4);
        var budget = NewBudget(token);
        // FreeImage AllocateBitmapはDIB全体を0初期化する。公式原本の静的裏付けはImageResize参照。
        var pixels = AllocatePixels(width, height, budget);
        var columns = Math.Min(width, old.Width); var rows = Math.Min(height, old.Height);
        budget.Spend((long)columns * rows);
        for (var y = 0; y < rows; y++)
        {
            token.ThrowIfCancellationRequested();
            old.Pixels.AsSpan(y * old.Width * 4, columns * 4).CopyTo(pixels.AsSpan(y * width * 4));
        }
        AdoptEdit(pane, new(old.Number, width, height, pixels), (ImageOffset[])_offsets.Clone(), retained, budget, token);
        return true;
    }

    private long RectangleHistoryBytes(int pane, long? afterBytes = null)
    {
        var retained = _historyBytes;
        for (var i = _history.Count - 1; i > _index; i--) retained -= RecordBytes(_history[i]);
        if (_index + 2 > MaximumHistoryRecords || retained + _frames[pane].Pixels.LongLength
            + (afterBytes ?? _frames[pane].Pixels.LongLength) > MaximumHistoryBytes)
            throw new InvalidOperationException("画像編集の履歴上限（128件・256 MiB）を超えます。");
        return retained;
    }
}
