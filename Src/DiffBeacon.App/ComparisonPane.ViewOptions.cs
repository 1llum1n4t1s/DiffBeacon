using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Rendering;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMergeの「表示」メニューの本文の設定（<see cref="DiffTextViewOptions"/>）を、差分タブの全ペインへ反映する。
///
/// 設計の要点:
/// - 設定はアプリ全体で一つ（外枠の <see cref="MainWindow.TextViewOptions"/>）。ペインは購読して見た目だけを変え、本文・履歴・比較結果・
///   ゴースト行の並びには触れない。行の高さは全ペインで同じ文字の大きさから決まるので、スクロールの画素位置での同期（行の対応）は崩れない。
/// - 空白は AvaloniaEdit の表示（ShowSpaces／ShowTabs）を本家の印（· と »）で使う。ゴースト行は目印の文字だけなので空白の印は出ない。
/// - 改行の印は AvaloniaEdit の ShowEndOfLine を使わず自前で描く。表示の文書ではゴースト行にも、改行で終わらない最終行の後ろに足した改行にも
///   改行があるため、組込みの表示では本物の本文に無い改行まで印が付くから。本物の行の本物の改行にだけ印を描く（本家のゴースト行は改行を持たない）。
/// - 本家と同じく、その側の本文の改行が混在しているときだけ CR・LF・CR+LF を描き分け、混在していなければ一つの印にする。
/// - Ctrl+ホイールの拡大／縮小は本家 CMergeEditView::OnMouseWheel と同じく本文の上で受け、スクロールはさせない。
/// </summary>
public sealed partial class ComparisonPane
{
    // 本家 ViewableWhitespace.cpp の印。
    internal const string WhitespaceSpaceGlyph = "·";
    internal const string WhitespaceTabGlyph = "»";
    internal const string EndOfLineGlyph = "◄┘";
    internal const string CarriageReturnGlyph = "←";
    internal const string LineFeedGlyph = "↓";

    private DiffTextViewOptions _textViewOptions = new();
    private bool _textViewOptionsAttached;

    /// <summary>この比較が使っている表示の設定（アプリ全体で共有）。</summary>
    internal DiffTextViewOptions TextViewOptions => _textViewOptions;

    // 自己検証用: 表示の行の改行の印、行番号の欄の幅（配置後）、欄と本文の間の縦線の表示。
    internal string? DiffEditorEndOfLineGlyph(int side, int displayLine) => _diffViews[side].EndOfLine.GlyphFor(displayLine);
    internal double DiffEditorMarginWidth(int side) => _diffViews[side].Margin.Bounds.Width;
    internal bool DiffEditorMarginSeparatorVisible(int side) => _diffViews[side].MarginSeparator.IsVisible;

    private void AttachTextViewOptions(DiffTextViewOptions options)
    {
        _textViewOptions = options;
        options.Changed += ApplyTextViewOptions;
        _textViewOptionsAttached = true;
        foreach (var side in _diffViews)
        {
            side.View.Options.ShowSpacesGlyph = WhitespaceSpaceGlyph;
            side.View.Options.ShowTabsGlyph = WhitespaceTabGlyph;
            side.View.TextArea.TextView.NonPrintableCharacterBrush = Brushes.Gray;
            side.View.TextArea.AddHandler(InputElement.PointerWheelChangedEvent, (_, args) =>
            {
                // 本家と同じく Ctrl だけを押したホイールで拡大／縮小する（上で拡大）。
                if (args.KeyModifiers != KeyModifiers.Control || args.Delta.Y == 0) return;
                _textViewOptions.Zoom(args.Delta.Y > 0 ? 1 : -1);
                args.Handled = true;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }
        ApplyTextViewOptions();
    }

    private void DetachTextViewOptions()
    {
        if (!_textViewOptionsAttached) return;
        _textViewOptions.Changed -= ApplyTextViewOptions;
        _textViewOptionsAttached = false;
    }

    /// <summary>設定を全ペインへ反映する。文字の大きさを変えた後は、左のペインの位置へほかのペインをそろえ直す。</summary>
    private void ApplyTextViewOptions()
    {
        var options = _textViewOptions;
        _diffViewSyncing = true;
        try
        {
            foreach (var side in _diffViews)
            {
                var view = side.View;
                if (Math.Abs(view.FontSize - options.FontSize) > .001) view.FontSize = options.FontSize;
                view.Options.ShowSpaces = options.ViewWhitespace;
                view.Options.ShowTabs = options.ViewWhitespace;
                side.MarginSeparator.IsVisible = options.ViewLineNumbers || options.ViewMargins;
                side.Redraw();
            }
        }
        finally { _diffViewSyncing = false; }
        SyncDiffEditorScroll(_leftDiffView);
        UpdateDiffPane();
    }

    /// <summary>
    /// 本物の行の本物の改行の後ろに、本家と同じ改行の印を描く。ゴースト行・足した改行・本文の末尾（改行の無い最終行）には描かない。
    /// </summary>
    internal sealed class EndOfLineRenderer(DiffEditorSide side) : IBackgroundRenderer
    {
        private object? _mixedVersion;
        private bool _mixed;

        internal ComparisonPane? Owner { get; set; }
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Owner is not { } owner || !owner.TextViewOptions.ViewEndOfLine) return;
            var face = new Typeface(side.View.FontFamily);
            foreach (var visual in textView.VisualLines)
            {
                if (GlyphFor(visual.FirstDocumentLine.LineNumber) is not { } glyph) continue;
                var text = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, side.View.FontSize, Brushes.Gray);
                var position = visual.GetVisualPosition(visual.VisualLength, VisualYPosition.TextTop) - textView.ScrollOffset;
                drawingContext.DrawText(text, position);
            }
        }

        /// <summary>表示の行（1始まり）の後ろに描く改行の印。印を描かない行（ゴースト行・足した改行・改行の無い行）は null。</summary>
        internal string? GlyphFor(int number)
        {
            var document = side.View.Document;
            if (number < 1 || number > document.LineCount) return null;
            var ghosts = side.GhostLineNumbers();
            var line = document.GetLineByNumber(number);
            if (ghosts.Contains(number) || line.DelimiterLength == 0 || AddedBreak(number)) return null;
            if (!Mixed(ghosts)) return EndOfLineGlyph;
            return document.GetText(line.EndOffset, line.DelimiterLength) switch { "\r\n" => EndOfLineGlyph, "\r" => CarriageReturnGlyph, _ => LineFeedGlyph };
        }

        // 改行で終わらない最終行の後ろにゴースト行を並べるため足した改行（次の行が足した改行の印のゴースト行）は本物の本文に無い。
        private bool AddedBreak(int number)
        {
            var document = side.View.Document;
            if (number >= document.LineCount) return false;
            var next = document.GetLineByNumber(number + 1);
            return next.Length == 1 && document.GetCharAt(next.Offset) == DiffEditorSide.GhostMarkAfterBreak;
        }

        /// <summary>この側の本物の行の改行に、二種類以上が混ざっているか。文書が変わるまで結果を使い回す。</summary>
        private bool Mixed(HashSet<int> ghosts)
        {
            var document = side.View.Document;
            if (_mixedVersion is not null && ReferenceEquals(_mixedVersion, document.Version)) return _mixed;
            string? first = null;
            var mixed = false;
            foreach (var line in document.Lines)
            {
                if (ghosts.Contains(line.LineNumber) || line.DelimiterLength == 0 || AddedBreak(line.LineNumber)) continue;
                var ending = document.GetText(line.EndOffset, line.DelimiterLength);
                if (first is null) first = ending;
                else if (first != ending) { mixed = true; break; }
            }
            _mixed = mixed; _mixedVersion = document.Version;
            return mixed;
        }
    }
}
