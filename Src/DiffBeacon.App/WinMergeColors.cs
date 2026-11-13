using Avalonia.Media;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMergeの既定配色。値は旧ソースの Options::DiffColors::Init（Src/OptionsDiffColors.cpp）と
/// Options::DirColors（Src/OptionsDirColors.cpp）の既定値をそのまま写したもの。
/// 本家は文字色を黒に固定し、背景色だけで差分の種類を表すため、ここでも背景用のブラシだけを持つ。
/// </summary>
internal static class WinMergeColors
{
    // 差分ブロック（OPT_CLR_DIFF）と、片側に行が無い側（OPT_CLR_DIFF_DELETED）。
    internal static readonly IBrush Difference = Brush(239, 203, 5);
    internal static readonly IBrush DifferenceDeleted = Brush(192, 192, 192);
    // 選択中の差分ブロック（OPT_CLR_SELECTED_DIFF / _DELETED）。
    internal static readonly IBrush SelectedDifference = Brush(239, 119, 116);
    internal static readonly IBrush SelectedDifferenceDeleted = Brush(240, 192, 192);
    // 無視した差分（OPT_CLR_TRIVIAL_DIFF）。
    internal static readonly IBrush TrivialDifference = Brush(251, 242, 191);
    // 行内差分（OPT_CLR_WORDDIFF / _DELETED）。
    internal static readonly IBrush WordDifference = Brush(241, 226, 173);
    internal static readonly IBrush WordDifferenceDeleted = Brush(255, 170, 130);
    // 移動ブロック（OPT_CLR_MOVEDBLOCK）。
    internal static readonly IBrush MovedBlock = Brush(228, 155, 82);
    // 本家の差分文字色（OPT_CLR_DIFF_TEXT）。テーマに関係なく黒で、黄色背景の上でも読める。
    internal static readonly IBrush DifferenceText = Brushes.Black;
    // フォルダー比較の行色（OPT_CLR_DIRCMP_DIFF / _MISSING / _FILTERED）。
    internal static readonly IBrush FolderDifference = Brush(240, 222, 125);
    internal static readonly IBrush FolderMissing = Brush(221, 221, 221);
    internal static readonly IBrush FolderFiltered = Brush(250, 245, 215);
    // 外枠（メニュー・ツールバー・ステータスバー）の地色。Windows のクラシックな 3D 面の色に合わせる。
    internal static readonly IBrush FrameFace = Brush(240, 240, 240);
    internal static readonly IBrush FrameBorder = Brush(204, 204, 204);
    internal static readonly IBrush FrameText = Brushes.Black;

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
}
