using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;

namespace DiffBeacon.App;

/// <summary>
/// 本家 Crystal Edit の上マージン。横スクロールに合わせて、10桁ごとに数字、5桁ごとに長い目盛りを描く。
/// </summary>
internal sealed class ColumnRuler : Control
{
    private readonly TextEditor _editor;

    internal ColumnRuler(TextEditor editor)
    {
        _editor = editor;
        IsVisible = false;
        Height = 0;
    }

    /// <summary>表示の切替。隠すときは高さを 0 にして本文の位置を変えない。</summary>
    internal void SetShown(bool shown)
    {
        IsVisible = shown;
        Height = shown ? Math.Max(14, _editor.FontSize + 4) : 0;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (!IsVisible || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        context.FillRectangle(WinMergeColors.FrameFace, new Rect(Bounds.Size));
        var text = _editor.TextArea.TextView;
        var charWidth = text.WideSpaceWidth;
        if (charWidth < 1) charWidth = 8;
        var scroll = text.ScrollOffset.X;
        var first = (int)(scroll / charWidth);
        var height = Bounds.Height;
        var pen = new Pen(Brushes.DimGray, 1);
        var face = new Typeface(_editor.FontFamily);
        var fontSize = Math.Max(9, _editor.FontSize * 0.75);
        for (var column = first; ; column++)
        {
            var x = column * charWidth - scroll;
            if (x > Bounds.Width) break;
            var scale = column % 10 == 0 ? 0.6 : column % 5 == 0 ? 0.4 : 0.2;
            context.DrawLine(pen, new Point(x, height * (1 - scale)), new Point(x, height - 1));
            if (column % 10 == 0)
            {
                var label = new FormattedText(column.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, face, fontSize, Brushes.Black);
                context.DrawText(label, new Point(x + 1, 0));
            }
        }
        context.DrawLine(pen, new Point(0, height - 1), new Point(Bounds.Width, height - 1));
    }
}
