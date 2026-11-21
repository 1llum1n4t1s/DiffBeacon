using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiffBeacon.Core;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMergeのロケーションペイン（Src/LocationView.cpp）に相当する、ファイル全体の差分の見取り図。
/// 左右の縦バーに差分ブロックを本家の差分色で描き、表示中の範囲を枠で示す。クリック・ドラッグした位置へ本文を移動する。
/// 描画は差分ブロック単位で行い、行数が多いファイルでも描画回数をブロック数に抑える。
/// </summary>
internal sealed class LocationPane : Control
{
    // 本家の描画定数（LocationView.cpp の Y_OFFSET / MAX_LINEPIX / INDICATOR_MIN_HEIGHT）。
    private const double YOffset = 5;
    private const double MaxLinePixels = 4;
    private const double IndicatorMinHeight = 2;
    private const double BarMargin = 4;
    private const double BarGap = 3;

    private static readonly IBrush BarBackground = Brushes.White;
    private static readonly IPen BarOutline = new Pen(new SolidColorBrush(Color.FromRgb(160, 160, 160)), 1);
    private static readonly IBrush ViewportFill = new SolidColorBrush(Color.FromArgb(48, 64, 96, 224));
    private static readonly IPen ViewportOutline = new Pen(new SolidColorBrush(Color.FromRgb(64, 96, 224)), 1);

    private IReadOnlyList<DiffRow> _rows = [];
    private IReadOnlyList<DiffBlock> _blocks = [];
    private int _currentBlock = -1;
    private double _viewportTop, _viewportHeight;

    public LocationPane()
    {
        Width = 36;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>クリック・ドラッグで指定された行（0始まりの整列行）。比較ペインが本文をその行へ移動する。</summary>
    internal event Action<int>? RowRequested;

    internal int RowCount => _rows.Count;
    internal int CurrentBlock => _currentBlock;
    internal (double Top, double Height) Viewport => (_viewportTop, _viewportHeight);

    internal void SetDiff(DiffResult? diff)
    {
        _rows = diff?.Rows ?? [];
        _blocks = diff?.Blocks ?? [];
        _currentBlock = -1;
        InvalidateVisual();
    }

    internal void SetCurrentBlock(int block)
    {
        if (_currentBlock == block) return;
        _currentBlock = block;
        InvalidateVisual();
    }

    /// <summary>表示範囲を全体に対する割合（0〜1）で受け取る。</summary>
    internal void SetViewport(double top, double height)
    {
        top = double.IsFinite(top) ? Math.Clamp(top, 0, 1) : 0;
        height = double.IsFinite(height) ? Math.Clamp(height, 0, 1) : 1;
        if (Math.Abs(top - _viewportTop) < 1e-6 && Math.Abs(height - _viewportHeight) < 1e-6) return;
        _viewportTop = top; _viewportHeight = height;
        InvalidateVisual();
    }

    // 1行あたりの高さ。本家と同じく 4px を上限にし、短いファイルは上に詰めて描く。
    private double LinePixels => _rows.Count == 0 ? 0 : Math.Min(MaxLinePixels, Math.Max(0, Bounds.Height - 2 * YOffset) / _rows.Count);

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width; var height = Bounds.Height;
        context.FillRectangle(WinMergeColors.FrameFace, new Rect(0, 0, width, height));
        var barWidth = Math.Max(4, (width - 2 * BarMargin - BarGap) / 2);
        var lineHeight = LinePixels;
        var barHeight = Math.Max(0, Math.Min(height - 2 * YOffset, _rows.Count * lineHeight));
        var bars = new[] { new Rect(BarMargin, YOffset, barWidth, barHeight), new Rect(BarMargin + barWidth + BarGap, YOffset, barWidth, barHeight) };
        foreach (var bar in bars) { context.FillRectangle(BarBackground, bar); context.DrawRectangle(BarOutline, bar); }
        foreach (var block in _blocks)
        {
            var top = YOffset + block.RowStart * lineHeight;
            var blockHeight = Math.Max(1, block.RowCount * lineHeight);
            var selected = block.Index == _currentBlock;
            for (var side = 0; side < 2; side++)
            {
                // 行のある側は差分色、行の無い側は削除色。選択中のブロックは本家の選択色にする。
                var hasLines = side == 0 ? block.LeftCount > 0 : block.RightCount > 0;
                var brush = hasLines
                    ? selected ? WinMergeColors.SelectedDifference : WinMergeColors.Difference
                    : selected ? WinMergeColors.SelectedDifferenceDeleted : WinMergeColors.DifferenceDeleted;
                context.FillRectangle(brush, new Rect(bars[side].X + 1, top, bars[side].Width - 2, blockHeight));
            }
        }
        if (_rows.Count == 0 || barHeight <= 0) return;
        var indicatorTop = YOffset + _viewportTop * barHeight;
        var indicatorHeight = Math.Max(IndicatorMinHeight, _viewportHeight * barHeight);
        var indicator = new Rect(1, indicatorTop, width - 2, Math.Min(indicatorHeight, YOffset + barHeight - indicatorTop + 1));
        context.FillRectangle(ViewportFill, indicator);
        context.DrawRectangle(ViewportOutline, indicator);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Pointer.Capture(this);
        RequestRowAt(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (ReferenceEquals(e.Pointer.Captured, this) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) RequestRowAt(e.GetPosition(this).Y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
    }

    /// <summary>ペイン内の Y 座標を整列行へ変換する。自己検証からも同じ変換で操作を再現する。</summary>
    internal int RowAt(double y)
    {
        var lineHeight = LinePixels;
        if (_rows.Count == 0 || lineHeight <= 0) return -1;
        return Math.Clamp((int)((y - YOffset) / lineHeight), 0, _rows.Count - 1);
    }

    internal void RequestRowAt(double y)
    {
        var row = RowAt(y);
        if (row >= 0) RowRequested?.Invoke(row);
    }
}
