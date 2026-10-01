namespace DiffBeacon.App;

public sealed record ImageViewSettings
{
    public int LeftFrame { get; set; } = 1;
    public int MiddleFrame { get; set; } = 1;
    public int RightFrame { get; set; } = 1;
    public double Threshold { get; set; }
    public double Zoom { get; set; } = 1;
    public double OverlayOpacity { get; set; } = .3;
    public bool ShowDifferences { get; set; } = true;
    public bool ReportAllFrames { get; set; } = true;
    public string View { get; set; } = "SideBySide";
    public int BlockSize { get; set; } = 8;
    public ImageOrientation LeftOrientation { get; set; } = new();
    public ImageOrientation MiddleOrientation { get; set; } = new();
    public ImageOrientation RightOrientation { get; set; } = new();

    internal int[] FrameNumbers(bool three) => three ? [LeftFrame, MiddleFrame, RightFrame] : [LeftFrame, RightFrame];
    internal ImageOrientation[] Orientations(bool three) => three
        ? [LeftOrientation, MiddleOrientation, RightOrientation] : [LeftOrientation, RightOrientation];

    internal static void Validate(ImageViewSettings? settings)
    {
        if (settings is null) throw new InvalidDataException("画像設定が null です。");
        ImageOrientation.Validate(settings.LeftOrientation); ImageOrientation.Validate(settings.MiddleOrientation);
        ImageOrientation.Validate(settings.RightOrientation);
        if (settings.BlockSize is < 1 or > 256) throw new InvalidDataException("画像差分のブロックサイズは1～256です。");
        if (settings.LeftFrame is < 1 or > 1024 || settings.MiddleFrame is < 1 or > 1024 || settings.RightFrame is < 1 or > 1024)
            throw new InvalidDataException("保存する画像ページ番号は1～1024です。");
        if (!double.IsFinite(settings.Threshold) || settings.Threshold is < 0 or > 510)
            throw new InvalidDataException("保存する画像閾値は0～510の有限値です。");
        if (!double.IsFinite(settings.Zoom) || settings.Zoom is < .1 or > 8)
            throw new InvalidDataException("保存する画像倍率は0.1～8の有限値です。");
        if (!double.IsFinite(settings.OverlayOpacity) || settings.OverlayOpacity is < 0 or > 1)
            throw new InvalidDataException("画像の重ね合わせ不透明度は0～1の有限値です。");
        if (settings.View is not ("SideBySide" or "Overlay" or "PixelDifference"))
            throw new InvalidDataException("画像の表示方式が不正です。");
    }
}
