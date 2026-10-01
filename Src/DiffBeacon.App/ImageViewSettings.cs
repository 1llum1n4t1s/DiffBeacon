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

    internal int[] FrameNumbers(bool three) => three ? [LeftFrame, MiddleFrame, RightFrame] : [LeftFrame, RightFrame];

    internal static void Validate(ImageViewSettings? settings)
    {
        if (settings is null) throw new InvalidDataException("画像設定が null です。");
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
