namespace DiffBeacon.App;

// WinIMergeの表示変換は水平flip・垂直flip・反時計回り回転の順で適用する。
// 原画を変更せず、コピーの確定時だけ逆写像して保存・履歴へ戻す。
public sealed record ImageOrientation
{
    public int Rotation { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }

    internal bool IsIdentity => Rotation == 0 && !FlipHorizontal && !FlipVertical;
    internal bool SwapsDimensions => Rotation is 90 or 270;

    internal static void Validate(ImageOrientation? value)
    {
        if (value is null || value.Rotation is not (0 or 90 or 180 or 270))
            throw new InvalidDataException("画像の回転は0・90・180・270度です。");
    }

    internal ImageComparisonEngine.DecodedFrame Apply(ImageComparisonEngine.DecodedFrame frame,
        CancellationToken token = default, bool inverse = false)
    {
        Validate(this); ImageComparisonEngine.ValidateDimensions(frame.Width, frame.Height);
        if (frame.Pixels.LongLength != (long)frame.Width * frame.Height * 4)
            throw new InvalidDataException("画像の原画サイズと画素数が一致しません。");
        token.ThrowIfCancellationRequested();
        if (IsIdentity) return frame;
        var width = SwapsDimensions ? frame.Height : frame.Width;
        var height = SwapsDimensions ? frame.Width : frame.Height;
        var pixels = new byte[frame.Pixels.Length];
        var rows = inverse ? height : frame.Height;
        var columns = inverse ? width : frame.Width;
        for (var y = 0; y < rows; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < columns; x++)
            {
                if ((x & 4095) == 0) token.ThrowIfCancellationRequested();
                var (tx, ty) = Position(x, y, columns, rows);
                var source = inverse ? (ty * frame.Width + tx) * 4 : (y * frame.Width + x) * 4;
                var target = inverse ? (y * width + x) * 4 : (ty * width + tx) * 4;
                frame.Pixels.AsSpan(source, 4).CopyTo(pixels.AsSpan(target, 4));
            }
        }
        token.ThrowIfCancellationRequested();
        return new(frame.Number, width, height, pixels);
    }

    private (int X, int Y) Position(int x, int y, int width, int height)
    {
        if (FlipHorizontal) x = width - 1 - x;
        if (FlipVertical) y = height - 1 - y;
        return Rotation switch
        {
            90 => (y, width - 1 - x),
            180 => (width - 1 - x, height - 1 - y),
            270 => (height - 1 - y, x),
            _ => (x, y)
        };
    }
}
