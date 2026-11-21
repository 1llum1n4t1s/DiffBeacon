namespace DiffBeacon.App;

// 回転・反転後の支持矩形の原点。原画の画素や共有Undo履歴へ焼き込まない。
public readonly record struct ImageOffset(int X, int Y)
{
    internal static ImageOffset Parse(string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var y))
            throw new ArgumentException("画像位置は非負の整数X,Yで指定してください。");
        return Validate([new ImageOffset(x, y)], 1)[0];
    }
    internal static ImageOffset[] Validate(IReadOnlyList<ImageOffset>? offsets, int count)
    {
        if (offsets is null) return new ImageOffset[count];
        if (offsets.Count != count) throw new ArgumentException("全入力の画像位置が必要です。");
        var result = offsets.ToArray();
        foreach (var offset in result)
            if (offset.X < 0 || offset.Y < 0 || offset.X > ImageComparisonEngine.MaximumPixels
                || offset.Y > ImageComparisonEngine.MaximumPixels)
                throw new ArgumentOutOfRangeException(nameof(offsets), "画像位置は正規化した非負座標です。");
        return result;
    }

    internal static ImageOffset[] Move(IReadOnlyList<ImageOffset> offsets, int pane, int dx, int dy)
    {
        if (pane < 0 || pane >= offsets.Count) throw new ArgumentOutOfRangeException(nameof(pane));
        var x = offsets.Select((offset, index) => (long)offset.X + (index == pane ? dx : 0)).ToArray();
        var y = offsets.Select((offset, index) => (long)offset.Y + (index == pane ? dy : 0)).ToArray();
        var minX = x.Min(); var minY = y.Min();
        var moved = new ImageOffset[offsets.Count];
        for (var i = 0; i < moved.Length; i++)
        {
            var nx = x[i] - minX; var ny = y[i] - minY;
            if (nx > ImageComparisonEngine.MaximumPixels || ny > ImageComparisonEngine.MaximumPixels)
                throw new InvalidOperationException("画像位置が比較キャンバスの上限を超えます。");
            moved[i] = new((int)nx, (int)ny);
        }
        return moved;
    }

    internal static (int Width, int Height) Canvas(IReadOnlyList<ImageComparisonEngine.DecodedFrame> frames,
        IReadOnlyList<ImageOffset> offsets)
    {
        var width = 0; var height = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            width = Math.Max(width, checked(frames[i].Width + offsets[i].X));
            height = Math.Max(height, checked(frames[i].Height + offsets[i].Y));
        }
        if ((long)width * height > ImageComparisonEngine.MaximumPixels)
            throw new InvalidOperationException("比較キャンバスが1600万ピクセルを超えます。");
        return (width, height);
    }
}
