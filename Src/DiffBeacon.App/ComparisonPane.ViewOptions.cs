using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
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
    private readonly FoldingManager?[] _contextFolds = new FoldingManager?[3];

    /// <summary>この比較が使っている表示の設定（アプリ全体で共有）。</summary>
    internal DiffTextViewOptions TextViewOptions => _textViewOptions;

    // 自己検証用: 表示の行の改行の印、行番号の欄の幅（配置後）、欄と本文の間の縦線の表示。
    internal string? DiffEditorEndOfLineGlyph(int side, int displayLine) => _diffViews[side].EndOfLine.GlyphFor(displayLine);
    internal double DiffEditorMarginWidth(int side) => _diffViews[side].Margin.Bounds.Width;
    internal bool DiffEditorMarginSeparatorVisible(int side) => _diffViews[side].MarginSeparator.Opacity > 0;
    /// <summary>自己検証用: その側のペインの一番上に見えている表示の行（1始まり）。</summary>
    internal int DiffEditorTopDisplayLine(int side) => TopDisplayLine(_diffViews[side]);
    /// <summary>自己検証用: Diff コンテキストで折りたたんでいる区間の数。</summary>
    internal int DiffEditorFoldCount(int side) => _contextFolds[side]?.AllFoldings.Count(section => section.IsFolded) ?? 0;
    /// <summary>自己検証用: 垂直分割（左右）で並べているか。</summary>
    internal bool DiffEditorsSplitVertically => _textViewOptions.VerticalSplit;

    /// <summary>表示メニューの設定を保存できなかった理由などを状態欄へ出す（外枠から呼ぶ）。</summary>
    internal void ShowViewOptionsStatus(string text) => _status.Text = text;

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

    /// <summary>
    /// 設定を全ペインへ反映する。文字の大きさを変えるとスクロールの画素位置はそのままでも見える行が変わるので、
    /// 本家と同じく一番上に見えていた行を保ち、ほかのペインをその位置へそろえ直す。
    /// </summary>
    private void ApplyTextViewOptions()
    {
        var options = _textViewOptions;
        var wrapChangedOff = _leftDiffView.View.WordWrap && !options.WordWrap;
        var family = ResolvedFontFamily(options.FontFamily);
        var fontChanged = Math.Abs(_leftDiffView.View.FontSize - options.FontSize) > .001
            || _leftDiffView.View.FontFamily.Name != family.Name;
        var topLine = fontChanged ? TopDisplayLine(_leftDiffView) : 0;
        var splitChanged = _diffEditorGrid.Tag is not bool laidOut || laidOut != options.VerticalSplit;
        _diffViewSyncing = true;
        try
        {
            foreach (var side in _diffViews)
            {
                var view = side.View;
                if (Math.Abs(view.FontSize - options.FontSize) > .001) view.FontSize = options.FontSize;
                if (view.FontFamily.Name != family.Name) view.FontFamily = family;
                if (view.WordWrap != options.WordWrap) view.WordWrap = options.WordWrap;
                view.Options.ShowSpaces = options.ViewWhitespace;
                view.Options.ShowTabs = options.ViewWhitespace;
                ApplySyntaxHighlight(side, options.SyntaxHighlight);
                // 両方の欄を隠したときも縦線の幅と余白は残し、本文がペインの境界に接しないようにする（本家も数画素あける）。
                side.MarginSeparator.Opacity = options.ViewLineNumbers || options.ViewMargins ? 1 : 0;
                side.Ruler.SetShown(options.ViewTopMargin);
                side.Redraw();
            }
            if (splitChanged) LayoutDiffEditors(_diffDisplay.ThreeWay);
        }
        finally { _diffViewSyncing = false; }
        ApplyDiffContext();
        if (topLine > 1) RestoreTopDisplayLine(_leftDiffView, topLine);
        if (wrapChangedOff) RebuildDiffEditors();
        else if (options.WordWrap) AlignWrappedLines();
        SyncDiffEditorScroll(_leftDiffView);
        UpdateDiffPane();
    }

    /// <summary>空のフォント名は既定の等幅。指定した名前はそのまま Avalonia のフォント解決へ渡す。</summary>
    private static FontFamily ResolvedFontFamily(string name) =>
        new(string.IsNullOrEmpty(name) ? DiffTextViewOptions.DefaultFontFamilyName : name);

    /// <summary>拡張子に合う定義があるときだけ色を付ける。定義が無い拡張子は色を消す。</summary>
    private void ApplySyntaxHighlight(DiffEditorSide side, bool enabled)
    {
        IHighlightingDefinition? definition = null;
        if (enabled)
        {
            var extension = Path.GetExtension(PathForSide(side.SourceSide));
            if (!string.IsNullOrEmpty(extension)) definition = HighlightingManager.Instance.GetDefinitionByExtension(extension);
        }
        if (side.View.SyntaxHighlighting?.Name != definition?.Name) side.View.SyntaxHighlighting = definition;
    }

    /// <summary>側に対応するパス欄。ハイライトの拡張子だけに使う。</summary>
    private string PathForSide(int side) => side switch { 0 => LeftPath.Text ?? "", 1 => BasePath.Text ?? "", _ => RightPath.Text ?? "" };

    /// <summary>
    /// 差異の前後だけを残し、それ以外の連続した表示行を折りたたむ。全ペインで同じ行番号を折るので、行の対応は崩れない。
    /// 反転中は、残す側と隠す側を入れ替える。全行かつ反転なしのときは折りたたみを外す。
    /// </summary>
    private void ApplyDiffContext()
    {
        var options = _textViewOptions;
        var model = _diffDisplay;
        for (var index = 0; index < _diffViews.Length; index++)
        {
            var side = _diffViews[index];
            var showAll = options.DiffContextLines < 0 && !options.DiffContextInvert && !DisplayFilterHidesAny(model);
            var documentLines = side.View.Document.LineCount;
            // 末尾改行があると AvaloniaEdit は空の最終行を1行足す。差分の行数か、その1行多いときだけ折る。
            var aligned = model.RowCount > 0 && documentLines >= model.RowCount && documentLines <= model.RowCount + 1;
            if (showAll || !aligned)
            {
                if (_contextFolds[index] is { } installed)
                {
                    FoldingManager.Uninstall(installed);
                    _contextFolds[index] = null;
                }
                continue;
            }
            var manager = _contextFolds[index] ??= FoldingManager.Install(side.View.TextArea);
            var visible = ContextVisibility(model, options);
            for (var row = 0; row < visible.Length; row++) if (DisplayFilterHides(model, row)) visible[row] = false;
            var folds = new List<NewFolding>();
            var line = 1;
            var last = model.RowCount;
            while (line <= last)
            {
                if (visible[line - 1]) { line++; continue; }
                var start = line;
                while (line <= last && !visible[line - 1]) line++;
                var startOffset = side.View.Document.GetLineByNumber(start).Offset;
                // 次の表示行の先頭までを隠す。空行でも改行ごと折りたためる。
                var endOffset = line <= documentLines ? side.View.Document.GetLineByNumber(line).Offset : side.View.Document.TextLength;
                if (endOffset > startOffset)
                    folds.Add(new NewFolding(startOffset, endOffset) { DefaultClosed = true, Name = "…" });
            }
            manager.UpdateFoldings(folds, -1);
            foreach (var section in manager.AllFoldings) section.IsFolded = true;
        }
    }

    /// <summary>表示の各行を出すか。0～9 は差異行から前後 N 行。全行の反転は差異行だけを隠す。</summary>
    private static bool[] ContextVisibility(DiffDisplayModel model, DiffTextViewOptions options)
    {
        var count = model.RowCount;
        var window = options.DiffContextLines;
        var visible = new bool[count];
        for (var row = 0; row < count; row++)
        {
            var inside = window < 0
                ? !options.DiffContextInvert || model.Difference[row]
                : NearDifference(model, row, window);
            visible[row] = options.DiffContextInvert ? !inside : inside;
        }
        return visible;
    }

    /// <summary>表示行が差異行から radius 行以内か。</summary>
    private static bool NearDifference(DiffDisplayModel model, int row, int radius)
    {
        var start = Math.Max(0, row - radius);
        var end = Math.Min(model.RowCount - 1, row + radius);
        for (var index = start; index <= end; index++) if (model.Difference[index]) return true;
        return false;
    }

    // ペインの一番上に見えている表示の行（1始まり）。スクロールしていなければ 1。
    private static int TopDisplayLine(DiffEditorSide side)
    {
        var view = side.View.TextArea.TextView;
        if (view.ScrollOffset.Y <= 0) return 1;
        // スクロール位置は画素へ丸められて行の上端より 1 画素未満上になることがあるので、その分を許して行を求める。
        return view.GetDocumentLineByVisualTop(view.ScrollOffset.Y + 1)?.LineNumber ?? 1;
    }

    // 新しい文字の大きさで配置し直してから、その行が一番上に来るようにスクロールする（ほかのペインは同期でそろう）。
    private static void RestoreTopDisplayLine(DiffEditorSide side, int line)
    {
        if (side.Scroller() is not { } scroller || line > side.View.Document.LineCount) return;
        side.View.UpdateLayout();
        var top = side.View.TextArea.TextView.GetVisualTopByDocumentLine(line);
        scroller.Offset = new Vector(scroller.Offset.X, top);
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
