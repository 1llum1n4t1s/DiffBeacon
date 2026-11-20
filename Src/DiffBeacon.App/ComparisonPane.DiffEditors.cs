using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using DiffBeacon.Core;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMergeと同じく、「差分」タブの本文を直接編集できるエディター（AvaloniaEdit）で表示する。
///
/// 設計の要点:
/// - 正本は従来どおり各側の <see cref="TextBox"/>（<see cref="TextEditor(int)"/>）。保存・dirty・revision・古い完了の破棄・
///   文字コード／BOM／改行の保持は、正本への書込みを通じて既存の仕組みがそのまま扱う。ここでは表示と編集の入口だけを持つ。
/// - エディターは側ごとに一つ（0=左、1=中央、2=右）で、ペインの位置と側は常に一致する。三つ目の側があるとき
///   （固定の祖先がある三者比較、独立三者Text）は本家と同じく左・中・右の3ペインで表示し、二者比較では左右の2ペインにする。
/// - 行をそろえるため、他の側にだけ行がある位置へ「ゴースト行」（本家の CGhostTextBuffer と同じ考え方の空行）を入れる。
///   ゴースト行は文書の中では空行だが、正本へ戻すときには取り除く。利用者がゴースト行へ文字を入力すると、その行は本物の行になる。
/// - 2ペインの行は比較結果（<see cref="DiffResult.Rows"/>）の1行＝表示の1行。3ペインの行は Core の三者整列
///   （<see cref="FourPaneMaterialization"/>）の行で、差分の移動・選択は二者の比較結果の行と行番号で対応させる。
/// - 編集は正本へ書き戻し、少し待ってから再比較して表示をそろえ直す。そろえ直しは文書を差し替えず、ゴースト行の出し入れだけで行う。
/// - 元に戻す／やり直すは全ペインで一つの履歴（ComparisonPane.TextHistory.cs）を使い、エディター自身の履歴は使わない。
/// - 行の対応が比較結果から再現できない場合（想定外の行番号の並び）は、ゴースト行を入れずに本文をそのまま表示して編集を許す。
/// - 一覧（<see cref="DiffList"/>）は表示しないが、行の選択・差分ブロックの移動・Diff ペイン・既存の自己検証の参照先として残す。
/// </summary>
public sealed partial class ComparisonPane
{
    private const int DiffEditRecompareDelayMilliseconds = 600;
    private readonly DiffEditorSide[] _diffViews = [new("diff-editor-left", 0), new("diff-editor-middle", 1), new("diff-editor-right", 2)];
    private readonly TextBlock[] _diffPaneCaptions = [PaneCaptionText(), PaneCaptionText(), PaneCaptionText()];
    private readonly TextBlock _middleHeaderPath = HeaderPathText("file-header-middle");
    private readonly Grid _diffEditorGrid = new() { Name = "diff-editor-grid" };
    private readonly Border[] _diffEditorHosts = new Border[3];
    private DiffDisplayModel _diffDisplay = DiffDisplayModel.Empty;
    private DispatcherTimer? _diffEditTimer;
    private CancellationTokenSource? _diffEditCompare;
    private bool _diffViewSyncing;

    private DiffEditorSide _leftDiffView => _diffViews[0];
    private DiffEditorSide _rightDiffView => _diffViews[2];
    internal (TextEditor Left, TextEditor Right) DiffEditors => (_diffViews[0].View, _diffViews[2].View);
    internal TextEditor DiffEditor(int side) => _diffViews[side].View;
    /// <summary>3ペイン（左・中・右）で表示しているか。</summary>
    internal bool DiffEditorsShowThreePanes => _diffDisplay.ThreeWay;
    /// <summary>自己検証用: その側のペインを置いている行。垂直分割では 0、上下では上から 0,1,2。</summary>
    internal int DiffEditorGridRow(int side) => Grid.GetRow(_diffEditorHosts[side]);
    internal int DiffEditorGhostLineCount(bool right) => (right ? _rightDiffView : _leftDiffView).GhostLineNumbers().Count;
    internal int DiffEditorGhostLineCount(int side) => _diffViews[side].GhostLineNumbers().Count;
    internal bool DiffEditRecomparePending => _diffEditTimer?.IsEnabled == true;

    private static TextBlock PaneCaptionText() => new()
    {
        Margin = new Thickness(6, 3), FontSize = 12, Foreground = WinMergeColors.FrameText, VerticalAlignment = VerticalAlignment.Center,
    };

    private Control BuildDiffEditors()
    {
        var paths = new[] { _leftHeaderPath, _middleHeaderPath, _rightHeaderPath };
        for (var index = 0; index < 3; index++)
        {
            var side = _diffViews[index];
            // 本家のファイル名の帯（CEditorFilePathBar）。ペインごとに灰色の帯へ見出しとパスを出す。
            var bar = new DockPanel();
            DockPanel.SetDock(_diffPaneCaptions[index], Dock.Left); bar.Children.Add(_diffPaneCaptions[index]); bar.Children.Add(paths[index]);
            // 本家 CEditorFilePathBar。地は COLOR_3DFACE、パスは COLOR_ACTIVECAPTION と地の中間色の角丸。
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(120, 180, 227)),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(4, 3),
                Padding = new Thickness(6, 1),
                MinHeight = 18,
                BoxShadow = BoxShadows.Parse("1 1 0 0 #A0A0A0"),
                Child = bar,
            };
            var header = new Border { Background = WinMergeColors.FrameFace, Child = chip };
            var pane = new DockPanel();
            DockPanel.SetDock(header, Dock.Top); pane.Children.Add(header);
            DockPanel.SetDock(side.Ruler, Dock.Top); pane.Children.Add(side.Ruler);
            pane.Children.Add(side.View);
            _diffEditorHosts[index] = new Border { BorderBrush = WinMergeColors.FrameBorder, BorderThickness = new Thickness(index == 0 ? 0 : 1, 0, 0, 0), Child = pane };
            side.Renderer.Owner = this; side.Margin.Owner = this; side.EndOfLine.Owner = this;
            side.View.TextArea.TextView.ScrollOffsetChanged += (_, _) => { side.Ruler.InvalidateVisual(); SyncDiffEditorScroll(side); };
            side.View.TextArea.Caret.PositionChanged += (_, _) => { SyncBookmarkCaret(side); OnDiffEditorCaretMoved(side); };
            // 行ブックマークは、フォーカスのあるエディターの側を対象にする（本家と同じ）。
            side.View.GotFocus += (_, _) => { _activeLineBookmarkSide = side.SourceSide; SyncBookmarkCaret(side); RefreshLineBookmarkToolbar(); };
            side.View.Document.TextChanged += (_, _) => OnDiffEditorTextChanged(side);
            // 元に戻す／やり直すのキーは、アプリの他の入力欄と同じく実行環境の設定（PlatformSettings の HotkeyConfiguration）に従い、
            // どのペインで押しても全ペイン共有の履歴を操作する（本家と同じ）。AvaloniaEdit の固定のキーより先に受け取る。
            side.View.TextArea.AddHandler(InputElement.KeyDownEvent, (_, args) =>
            {
                if (Application.Current?.PlatformSettings?.HotkeyConfiguration is not { } hotkeys) return;
                if (hotkeys.Undo.Any(gesture => gesture.Matches(args))) { UndoText(); args.Handled = true; }
                else if (hotkeys.Redo.Any(gesture => gesture.Matches(args))) { RedoText(); args.Handled = true; }
                // コピー・切り取り・貼り付けも、目印の文字を写さないよう組込みの処理より先に受ける（ComparisonPane.EditCommands.cs）。
                else if (HandleDiffClipboardKey(side, args)) args.Handled = true;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AttachDiffEditorContextMenu(side);
        }
        AttachTextHistory();
        LayoutDiffEditors(false);
        // 正本の読取り専用や本文が他の操作で変わったら、エディターの編集可否を合わせる。
        foreach (var source in new[] { LeftEditor, MiddleEditor, RightEditor })
            source.PropertyChanged += (_, args) =>
            {
                if (args.Property == TextBox.IsReadOnlyProperty || args.Property == TextBox.TextProperty) UpdateDiffEditorReadOnly();
            };
        // 既存の見出し（左／右・読取り専用・未保存などの状態）は他の処理が更新する。ペインの見出しへ写す。
        foreach (var caption in new[] { _leftCaption, _rightCaption })
            caption.PropertyChanged += (_, args) => { if (args.Property == TextBlock.TextProperty) UpdateDiffPaneCaptions(); };
        return _diffEditorGrid;
    }

    /// <summary>2ペインと3ペインを切り替える。中央のペインは三つ目の側があるときだけ置く。</summary>
    private void LayoutDiffEditors(bool threeWay)
    {
        var vertical = _textViewOptions.VerticalSplit;
        _diffEditorGrid.Tag = vertical;
        _diffEditorGrid.Children.Clear();
        var shown = threeWay ? new[] { 0, 1, 2 } : new[] { 0, 2 };
        if (vertical)
        {
            _diffEditorGrid.RowDefinitions = new RowDefinitions("*");
            _diffEditorGrid.ColumnDefinitions = new ColumnDefinitions(threeWay ? "*,*,*" : "*,*");
        }
        else
        {
            _diffEditorGrid.ColumnDefinitions = new ColumnDefinitions("*");
            _diffEditorGrid.RowDefinitions = new RowDefinitions(threeWay ? "*,*,*" : "*,*");
        }
        for (var index = 0; index < shown.Length; index++)
        {
            var host = _diffEditorHosts[shown[index]];
            host.BorderThickness = vertical ? new Thickness(index == 0 ? 0 : 1, 0, 0, 0) : new Thickness(0, index == 0 ? 0 : 1, 0, 0);
            Grid.SetRow(host, vertical ? 0 : index);
            Grid.SetColumn(host, vertical ? index : 0);
            _diffEditorGrid.Children.Add(host);
        }
    }

    private void UpdateDiffPaneCaptions()
    {
        _diffPaneCaptions[0].Text = IndependentText ? TextSideCaption(0) : _leftCaption.Text;
        _diffPaneCaptions[1].Text = IndependentText ? TextSideCaption(1) : (_projectMetadata.BaseDescription ?? "共通の祖先") + "（読取り専用）";
        _diffPaneCaptions[2].Text = IndependentText ? TextSideCaption(2) : _rightCaption.Text;
    }

    /// <summary>比較結果が変わったら表示を作り直す。正本の側が書き込めなければエディターも読取り専用にする。</summary>
    private void RebuildDiffEditors()
    {
        var model = BuildDiffDisplayModel();
        var layoutChanged = model.ThreeWay != _diffDisplay.ThreeWay;
        _diffDisplay = model;
        if (layoutChanged) LayoutDiffEditors(model.ThreeWay);
        _middleHeaderPath.Text = model.ThreeWay ? BasePath.Text ?? "" : "";
        UpdateDiffPaneCaptions();
        _diffViewSyncing = true;
        try
        {
            for (var side = 0; side < 3; side++) _diffViews[side].Rebuild(model.Texts[side], model.LineNumbers[side]);
        }
        finally { _diffViewSyncing = false; }
        UpdateDiffEditorReadOnly();
        ApplyDiffContext();
        SyncDiffEditorScroll(_leftDiffView);
    }

    /// <summary>
    /// 表示の行の並びを作る。二者は比較結果の行をそのまま使い、三者は Core の三者整列の行を使う。
    /// 三者整列に失敗した（作業量の上限など）ときは、比較している二側だけの2ペインへ戻す。
    /// </summary>
    private DiffDisplayModel BuildDiffDisplayModel()
    {
        if (CurrentDiff is not { } diff) return DiffDisplayModel.Empty;
        (int Left, int Right) pair = IndependentText ? TextPair() : (0, 2);
        var threeWay = IndependentText || _baseText is not null;
        if (threeWay)
        {
            try
            {
                var texts = new string[3];
                for (var side = 0; side < 3; side++) texts[side] = TextEditor(side).Text ?? "";
                // 比較している二側は、比較結果の本文に合わせる（行番号の対応がずれないように）。
                texts[pair.Left] = diff.LeftText; texts[pair.Right] = diff.RightText;
                var options = Options();
                var aligned = IndependentText
                    ? FourPaneMaterialization.CreateIndependentThreeWay(texts[0], texts[1], texts[2], options)
                    : FourPaneMaterialization.CreateThreeWay(texts[1], texts[0], texts[2], options);
                return DiffDisplayModel.FromThreeWay(diff, pair, texts, aligned.Alignment);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
            {
                // 三者整列ができなければ二側だけを表示する（比較結果は二者のまま有効）。
            }
        }
        return DiffDisplayModel.FromTwoWay(diff, pair);
    }

    private void UpdateDiffEditorReadOnly()
    {
        foreach (var side in _diffViews)
        {
            // 比較結果が無いとき・表示が正本から外れているとき（他の操作で正本が変わった直後）・その側へ書き込めないときは受け付けない。
            var current = _diffDisplay.RowCount > 0 && TextEditor(side.SourceSide).Text == side.SourceText;
            side.View.IsReadOnly = !current || TextEditor(side.SourceSide).IsReadOnly || !TextSideWritable(side.SourceSide);
        }
    }

    private bool TextSideWritable(int side)
    {
        try { EnsureTextSideWritable(side); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private void OnDiffEditorTextChanged(DiffEditorSide side)
    {
        if (_diffViewSyncing) return;
        var editor = TextEditor(side.SourceSide);
        // 正本が書き込めなければ書き込まない（エディターも読取り専用にしてあるが、念のため二重に守る）。
        if (editor.IsReadOnly || !TextSideWritable(side.SourceSide)) return;
        var text = side.RealText();
        side.SourceText = text;
        WriteTypedText(side.SourceSide, text);
        ScheduleDiffEditRecompare();
    }

    /// <summary>
    /// エディターのキャレット行を、正本（TextBox）のキャレットへ写す。行ブックマークは正本の行を基準にするため、
    /// ゴースト行を数えない本物の行番号へ直して置く。表示が正本から外れている間は写さない。
    /// </summary>
    private void SyncBookmarkCaret(DiffEditorSide side)
    {
        var editor = TextEditor(side.SourceSide);
        if (editor.Text != side.SourceText) return;
        var realLine = side.RealLineOfDisplay(side.View.TextArea.Caret.Line);
        var text = editor.Text ?? "";
        var start = 0;
        for (var line = 0; line < realLine; line++)
        {
            var next = NextLineBreak(text, start, out var length);
            if (next < 0) return;
            start = next + length;
        }
        if (editor.CaretIndex != start) { editor.SelectionStart = start; editor.SelectionEnd = start; editor.CaretIndex = start; }
    }

    /// <summary>
    /// 指定した側の本物の行（0始まり）へ、その側のエディターのキャレットを移してフォーカスする。
    /// 表示が正本から外れていれば、先に正本どうしを比較して表示をそろえる。その側を表示していなければ false。
    /// </summary>
    internal bool FocusDiffEditorRealLine(int side, int realLine, int column = 1)
    {
        var view = _diffViews[side];
        if (view.SourceText != TextEditor(side).Text || _diffDisplay.RowCount == 0) CompareEditors();
        if (side == 1 && !_diffDisplay.ThreeWay) return false;
        var display = view.DisplayLineOfReal(realLine);
        if (display < 1) return false;
        view.View.Focus();
        view.View.TextArea.Caret.Line = display; view.View.TextArea.Caret.Column = column;
        _activeLineBookmarkSide = side;
        SyncBookmarkCaret(view);
        RefreshLineBookmarkToolbar();
        ScrollDiffEditorsToDisplayRow(display - 1);
        return true;
    }

    /// <summary>
    /// 指定した側のエディターへフォーカスし、キャレットを本文の末尾（正本の CaretIndex = 本文の長さ に当たる位置）へ置く。
    /// 表示が正本から外れていれば先に比較してそろえる。その側を表示していなければ false。
    /// </summary>
    internal bool FocusDiffEditorEnd(int side)
    {
        var view = _diffViews[side];
        if (view.SourceText != TextEditor(side).Text || _diffDisplay.RowCount == 0) CompareEditors();
        if (side == 1 && !_diffDisplay.ThreeWay) return false;
        // 本文の末尾は、改行（CRLF・LF・CR）の数だけ進んだ本物の行の行末。改行で終われば、その後の空行の先頭になる。
        var text = TextEditor(side).Text ?? "";
        var display = view.DisplayLineOfReal(DiffEditorSide.SplitLines(text).Count(line => line.Ending.Length > 0));
        if (display < 1) display = view.View.Document.LineCount;
        var focused = view.View.TextArea.Focus();
        view.View.TextArea.Caret.Offset = view.View.Document.GetLineByNumber(display).EndOffset;
        _activeLineBookmarkSide = side;
        return focused;
    }

    private void ScheduleDiffEditRecompare()
    {
        _diffEditTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(DiffEditRecompareDelayMilliseconds), DispatcherPriority.Background, (_, _) => RunDiffEditRecompare());
        _diffEditTimer.Stop(); _diffEditTimer.Start();
    }

    /// <summary>テストと本番の共通入口。待機中の再比較をすぐ実行する。</summary>
    internal Task FlushDiffEditRecompareAsync()
    {
        if (_diffEditTimer?.IsEnabled != true) return Task.CompletedTask;
        _diffEditTimer.Stop();
        return RecompareAfterDiffEditAsync();
    }

    private void RunDiffEditRecompare()
    {
        _diffEditTimer?.Stop();
        _ = GuardAsync(RecompareAfterDiffEditAsync);
    }

    /// <summary>
    /// 編集後の再比較。正本どうしを比べ、内包アーカイブでもディスクから読み直さない（RefreshEditorsAsync は通さない）。
    /// 保存・パスの比較など別の操作の取消トークン（_operation）は取り消さず、他の操作の実行中は待ち直す。
    /// </summary>
    private async Task RecompareAfterDiffEditAsync()
    {
        if (_disposed) return;
        if (!CompareButton.IsEnabled) { ScheduleDiffEditRecompare(); return; }
        // 再比較までの続けての入力を、一つの「元に戻す」単位として確定する。
        CloseTypingGroup();
        if (IndependentText) { await StartIndependentEditorComparisonAsync(); return; }
        _diffEditCompare?.Cancel(); _diffEditCompare?.Dispose();
        var cancellation = _diffEditCompare = new CancellationTokenSource(); var token = cancellation.Token;
        var left = LeftEditor.Text ?? ""; var right = RightEditor.Text ?? ""; var options = Options();
        var syncPointRevision = _syncPointRevision;
        var syncPoints = _mode.SelectedIndex is 0 or 1 ? TextSyncPointsFor(0, 2).ToArray() : Array.Empty<TextSyncPoint>();
        var sourcePaths = CurrentTextSourcePaths();
        DiffResult diff;
        try { diff = await Task.Run(() => CompareTextWithSyncPoints(left, right, syncPoints, options, token, sourcePaths: sourcePaths), token); }
        catch (OperationCanceledException) { return; }
        // 比較の間に正本・同期点・操作が変わっていれば、古い結果として捨てる。
        if (_disposed || token.IsCancellationRequested || left != LeftEditor.Text || right != RightEditor.Text || syncPointRevision != _syncPointRevision) return;
        ApplyDiff(diff);
    }

    private void SyncDiffEditorScroll(DiffEditorSide source)
    {
        if (_diffViewSyncing) return;
        // 折り返すと側ごとに見た目の高さが変わる。画素でそろえると行がずれるので、一番上の表示行でそろえる。
        var wrap = _textViewOptions.WordWrap;
        var top = wrap ? TopDisplayLine(source) : 0;
        var offset = source.View.TextArea.TextView.ScrollOffset;
        _diffViewSyncing = true;
        try
        {
            foreach (var other in _diffViews)
            {
                if (ReferenceEquals(other, source) || other.Scroller() is not { } scroller) continue;
                if (_textViewOptions.LockedSide == source.SourceSide || _textViewOptions.LockedSide == other.SourceSide) continue;
                if (wrap)
                {
                    if (top > other.View.Document.LineCount) continue;
                    var y = other.View.TextArea.TextView.GetVisualTopByDocumentLine(top);
                    if (Math.Abs(scroller.Offset.Y - y) > .5) scroller.Offset = new Vector(scroller.Offset.X, y);
                }
                else if (Math.Abs(scroller.Offset.Y - offset.Y) > .5 || Math.Abs(scroller.Offset.X - offset.X) > .5)
                    scroller.Offset = new Vector(offset.X, offset.Y);
            }
        }
        finally { _diffViewSyncing = false; }
        UpdateLocationViewport();
    }

    private void OnDiffEditorCaretMoved(DiffEditorSide side)
    {
        if (_diffViewSyncing || CurrentDiff is not { } diff) return;
        var displayRow = side.View.TextArea.Caret.Line - 1;
        var row = _diffDisplay.TwoWayRowOf(displayRow);
        if (row < 0 || row >= diff.Rows.Count || DiffList.SelectedIndex == row) return;
        _diffViewSyncing = true;
        try { DiffList.SelectedIndex = row; }
        finally { _diffViewSyncing = false; }
        InvalidateDiffEditorHighlights();
    }

    /// <summary>一覧の選択（差分の移動など、比較結果の行）を、全エディターのキャレット行へ写す。</summary>
    private void MoveDiffEditorsToRow(int row, bool center)
    {
        var display = _diffDisplay.DisplayRowOf(row);
        if (display < 0) return;
        _diffViewSyncing = true;
        try
        {
            foreach (var side in _diffViews)
            {
                if (display + 1 > side.View.Document.LineCount) continue;
                side.View.TextArea.Caret.Line = display + 1; side.View.TextArea.Caret.Column = 1;
            }
        }
        finally { _diffViewSyncing = false; }
        if (center) ScrollDiffEditorsToDisplayRow(display);
        InvalidateDiffEditorHighlights();
    }

    /// <summary>比較結果の行を中央付近へスクロールする。</summary>
    private void ScrollDiffEditorsToRow(int row)
    {
        var display = _diffDisplay.DisplayRowOf(row);
        if (display >= 0) ScrollDiffEditorsToDisplayRow(display);
    }

    private void ScrollDiffEditorsToDisplayRow(int display)
    {
        var view = _leftDiffView.View;
        var lineHeight = view.TextArea.TextView.DefaultLineHeight;
        var viewport = view.TextArea.TextView.Bounds.Height;
        var target = Math.Max(0, display * lineHeight - viewport / 2 + lineHeight / 2);
        if (_leftDiffView.Scroller() is { } scroller) scroller.Offset = new Vector(scroller.Offset.X, Math.Min(target, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
        // スクロール位置は次の配置で TextView に反映されるため、ここで配置を済ませてから他のペインとロケーションペインへ写す。
        view.UpdateLayout();
        SyncDiffEditorScroll(_leftDiffView);
    }

    private void InvalidateDiffEditorHighlights()
    {
        foreach (var side in _diffViews) { side.View.TextArea.TextView.InvalidateLayer(KnownLayer.Background); side.Margin.InvalidateVisual(); }
    }

    /// <summary>ロケーションペインの表示範囲（0〜1の割合）。エディターが描画済みならエディターの位置を使う。</summary>
    private (double Top, double Height)? DiffEditorViewport()
    {
        var text = _leftDiffView.View.TextArea.TextView;
        var total = text.DocumentHeight;
        if (total <= 0 || text.Bounds.Height <= 0) return null;
        return (text.ScrollOffset.Y / total, Math.Min(1, text.Bounds.Height / total));
    }

    /// <summary>選択中の差分ブロックが占める表示の行の範囲（両端を含む）。</summary>
    private (int Start, int End)? SelectedDisplayRange()
    {
        if (CurrentDiff is not { } diff || _selectedBlock < 0 || _selectedBlock >= diff.Blocks.Count) return null;
        var block = diff.Blocks[_selectedBlock];
        var rows = Enumerable.Range(block.RowStart, Math.Max(1, block.RowCount)).Select(_diffDisplay.DisplayRowOf).Where(row => row >= 0).ToArray();
        return rows.Length == 0 ? null : (rows.Min(), rows.Max());
    }

    /// <summary>表示の行（1始まり）の背景色。差分でない行は塗らない。描画と自己検証で同じ判定を使う。</summary>
    internal IBrush? BrushForLine(DiffEditorSide side, int number, HashSet<int> ghosts, (int Start, int End)? selected)
    {
        var row = number - 1;
        if (row < 0 || row >= _diffDisplay.RowCount) return null;
        var ghost = ghosts.Contains(number);
        if (!_diffDisplay.Difference[row] && !ghost) return null;
        var inSelected = selected is { } range && row >= range.Start && row <= range.End;
        return !ghost ? inSelected ? WinMergeColors.SelectedDifference : WinMergeColors.Difference
            : inSelected ? WinMergeColors.SelectedDifferenceDeleted : WinMergeColors.DifferenceDeleted;
    }

    /// <summary>表示の行の行内差分（比較している二側だけ。比較結果の行の範囲をその行の文字へ当てる）。</summary>
    private IReadOnlyList<InlineSpan> SpansForLine(DiffEditorSide side, int number)
    {
        if (CurrentDiff is not { } diff) return [];
        var display = number - 1;
        var row = _diffDisplay.TwoWayRowOf(display);
        if (row < 0 || row >= diff.Rows.Count || _diffDisplay.DisplayRowOf(row) != display) return [];
        var lineNumber = side.LineNumberOfDisplay(number);
        if (side.SourceSide == _diffDisplay.PairLeft && diff.Rows[row].LeftLineNumber == lineNumber) return diff.Rows[row].LeftSpans;
        if (side.SourceSide == _diffDisplay.PairRight && diff.Rows[row].RightLineNumber == lineNumber) return diff.Rows[row].RightSpans;
        return [];
    }

    /// <summary>headless検証用: 表示中のエディターの全行に塗っている背景色（重複なし）。</summary>
    internal IReadOnlyList<Color> DiffEditorLineColors()
    {
        var colors = new HashSet<Color>();
        var selected = SelectedDisplayRange();
        foreach (var side in _diffViews.Where(side => side.SourceSide != 1 || _diffDisplay.ThreeWay))
        {
            var ghosts = side.GhostLineNumbers();
            for (var number = 1; number <= side.View.Document.LineCount; number++)
                if (BrushForLine(side, number, ghosts, selected) is ISolidColorBrush brush) colors.Add(brush.Color);
        }
        return colors.ToArray();
    }

    /// <summary>headless検証用: 行内差分色で塗る文字列（左、中、右の順）。</summary>
    internal IReadOnlyList<string> DiffEditorWordDifferenceTexts()
    {
        var result = new List<string>();
        foreach (var side in _diffViews)
        {
            var ghosts = side.GhostLineNumbers(); var document = side.View.Document;
            for (var number = 1; number <= document.LineCount; number++)
            {
                if (ghosts.Contains(number)) continue;
                var line = document.GetLineByNumber(number);
                foreach (var span in SpansForLine(side, number))
                    if (span.Start + span.Length <= line.Length) result.Add(document.GetText(line.Offset + span.Start, span.Length));
            }
        }
        return result;
    }

    /// <summary>表示の行の並び。行番号は側ごとに1始まり（null はゴースト行）。比較結果の行との対応を両方向で持つ。</summary>
    internal sealed class DiffDisplayModel
    {
        internal static readonly DiffDisplayModel Empty = new(false, ["", "", ""], [[], [], []], [], [], [], 0, 2);
        private DiffDisplayModel(bool threeWay, string[] texts, int?[][] lineNumbers, bool[] difference, int[] twoWayToDisplay, int[] displayToTwoWay, int pairLeft, int pairRight)
        {
            ThreeWay = threeWay; Texts = texts; LineNumbers = lineNumbers; Difference = difference;
            _twoWayToDisplay = twoWayToDisplay; _displayToTwoWay = displayToTwoWay; PairLeft = pairLeft; PairRight = pairRight;
        }
        private readonly int[] _twoWayToDisplay, _displayToTwoWay;
        internal bool ThreeWay { get; }
        internal string[] Texts { get; }
        internal int?[][] LineNumbers { get; }
        internal bool[] Difference { get; }
        internal int PairLeft { get; }
        internal int PairRight { get; }
        internal int RowCount => Difference.Length;
        internal int DisplayRowOf(int twoWayRow) => twoWayRow >= 0 && twoWayRow < _twoWayToDisplay.Length ? _twoWayToDisplay[twoWayRow] : -1;
        internal int TwoWayRowOf(int displayRow) => displayRow >= 0 && displayRow < _displayToTwoWay.Length ? _displayToTwoWay[displayRow] : -1;

        internal static DiffDisplayModel FromTwoWay(DiffResult diff, (int Left, int Right) pair)
        {
            var count = diff.Rows.Count;
            var numbers = new int?[3][];
            numbers[pair.Left] = diff.Rows.Select(row => row.LeftLineNumber).ToArray();
            numbers[pair.Right] = diff.Rows.Select(row => row.RightLineNumber).ToArray();
            var third = 3 - pair.Left - pair.Right; numbers[third] = [];
            var texts = new string[3]; texts[pair.Left] = diff.LeftText; texts[pair.Right] = diff.RightText; texts[third] = "";
            var identity = Enumerable.Range(0, count).ToArray();
            return new(false, texts, numbers, diff.Rows.Select(row => row.Kind != DiffKind.Equal).ToArray(), identity, identity, pair.Left, pair.Right);
        }

        internal static DiffDisplayModel FromThreeWay(DiffResult diff, (int Left, int Right) pair, string[] texts, FourPaneAlignmentResult alignment)
        {
            // Sources は論理の面の順（0=左、1=中央／祖先、2=右）。本物の行だけに行番号（元の物理行＋1）を付ける。
            var numbers = new int?[3][];
            foreach (var source in alignment.Sources)
                numbers[source.LogicalPane] = source.Rows.Select(row => row.Origin == FourPaneRowOrigin.OriginalPhysical && row.OriginalPhysicalIndex is int index ? index + 1 : (int?)null).ToArray();
            var count = numbers.Max(lines => lines?.Length ?? 0);
            if (numbers.Any(lines => lines is null || lines.Length != count)) throw new InvalidOperationException("三者整列の行数がそろっていません。");
            var difference = new bool[count];
            foreach (var item in alignment.Diffs)
                for (var row = Math.Max(0, item.ApparentBegin); row <= Math.Min(count - 1, item.ApparentEnd); row++) difference[row] = true;
            // 比較結果の行 ⇔ 表示の行。比較している二側の行番号で対応させる。
            var leftRows = new Dictionary<int, int>(); var rightRows = new Dictionary<int, int>();
            for (var row = 0; row < count; row++)
            {
                if (numbers[pair.Left]![row] is int l) leftRows.TryAdd(l, row);
                if (numbers[pair.Right]![row] is int r) rightRows.TryAdd(r, row);
            }
            var twoWayToDisplay = new int[diff.Rows.Count];
            var displayToTwoWay = Enumerable.Repeat(-1, count).ToArray();
            for (var index = 0; index < diff.Rows.Count; index++)
            {
                var row = diff.Rows[index];
                var display = row.LeftLineNumber is int l && leftRows.TryGetValue(l, out var a) ? a
                    : row.RightLineNumber is int r && rightRows.TryGetValue(r, out var b) ? b : -1;
                twoWayToDisplay[index] = display;
                if (display >= 0 && displayToTwoWay[display] < 0) displayToTwoWay[display] = index;
            }
            // 二側どちらにも行が無い表示の行（三つ目の側だけの行）は、直前の比較結果の行へ寄せる。
            var last = 0;
            for (var row = 0; row < count; row++) { if (displayToTwoWay[row] >= 0) last = displayToTwoWay[row]; else displayToTwoWay[row] = diff.Rows.Count == 0 ? -1 : last; }
            return new(true, texts, numbers!, difference, twoWayToDisplay, displayToTwoWay, pair.Left, pair.Right);
        }
    }

    /// <summary>
    /// 一つの側の表示と、ゴースト行の管理。
    /// ゴースト行は「目印の文字（幅0の空白）だけの行」として文書へ入れる。目印は文書の一部なので、元に戻す／やり直すで
    /// 本文と一緒に正しく戻る（位置の記録を別に持つと、元に戻したときにゴースト行が本物の空行と見分けられなくなる）。
    /// 正本へ戻すときは目印を取り除く。目印の文字を含む本文はゴースト行なしの表示にする（取り違えを防ぐ）。
    /// </summary>
    internal sealed class DiffEditorSide
    {
        // ゴースト行の目印。後者は「直前の行の改行は、ゴースト行を並べるために足したもの」（改行で終わらない最終行の後ろ）を表す。
        internal const char GhostMark = '​';
        internal const char GhostMarkAfterBreak = '‌';

        internal DiffEditorSide(string name, int sourceSide)
        {
            SourceSide = sourceSide;
            View = new TextEditor
            {
                Name = name, FontFamily = new FontFamily(DiffTextViewOptions.DefaultFontFamilyName), FontSize = 13,
                ShowLineNumbers = false, WordWrap = false, Background = Brushes.White, Foreground = Brushes.Black,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            };
            View.Options.EnableHyperlinks = false; View.Options.EnableEmailHyperlinks = false;
            View.Options.AllowScrollBelowDocument = false;
            // エディター自身の履歴は使わない（全ペイン共有の履歴を ComparisonPane が持つ）。組込みのキー・コマンドで戻らないよう容量を0にする。
            View.Document.UndoStack.SizeLimit = 0;
            // 検索は本家と同じ検索・置換ダイアログ（ComparisonPane.FindReplace.cs）で行う。エディター組込みの検索欄は外す。
            View.SearchPanel?.Uninstall();
            Renderer = new DiffLineRenderer(this);
            View.TextArea.TextView.BackgroundRenderers.Add(Renderer);
            // 「改行を表示」の印は本物の行の改行にだけ描く（ゴースト行・足した改行には描かない。ComparisonPane.ViewOptions.cs）。
            EndOfLine = new EndOfLineRenderer(this);
            View.TextArea.TextView.BackgroundRenderers.Add(EndOfLine);
            // 本家と同じく本物の行番号だけを出し、ゴースト行は空欄にする。
            Margin = new RealLineNumberMargin(this);
            View.TextArea.LeftMargins.Insert(0, Margin);
            MarginSeparator = new Border { Width = 1, Background = WinMergeColors.FrameBorder, Margin = new Thickness(0, 0, 4, 0) };
            View.TextArea.LeftMargins.Insert(1, MarginSeparator);
            Ruler = new ColumnRuler(View);
        }

        /// <summary>本家の上マージン（列のルーラー）。既定では隠す。</summary>
        internal ColumnRuler Ruler { get; }

        internal TextEditor View { get; }
        internal DiffLineRenderer Renderer { get; }
        internal RealLineNumberMargin Margin { get; }
        internal EndOfLineRenderer EndOfLine { get; }
        /// <summary>行番号の欄と本文の間の縦線。行番号もマージンも表示しないときは隠す。</summary>
        internal Border MarginSeparator { get; }
        /// <summary>この表示が対応する側（0=左、1=中央、2=右）。</summary>
        internal int SourceSide { get; }
        /// <summary>この表示を作った（または最後に書き戻した）正本の本文。正本がこれと異なるなら表示は古い。</summary>
        internal string SourceText { get; set; } = "";
        private object? _ghostVersion;
        private HashSet<int> _ghostCache = [];

        /// <summary>エディターのテンプレート内のスクロール領域。TextView のスクロール位置はこれに従う。</summary>
        internal ScrollViewer? Scroller() => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(View).OfType<ScrollViewer>().FirstOrDefault();

        /// <summary>表示の行（1始まり）の、この側の本物の行番号（1始まり）。ゴースト行・範囲外は null。</summary>
        internal int? LineNumberOfDisplay(int number) => GhostLineNumbers().Contains(number) ? null : RealLineOfDisplay(number) + 1;

        /// <summary>
        /// 表示を作り直す。正本の本文が表示の本物の行と同じなら、文書を差し替えずゴースト行の出し入れだけでそろえる
        /// （入力中のキャレットや選択を保つため）。本文が違う（読込み・元に戻す・他の操作による変更）ときは差し替える。
        /// </summary>
        internal void Rebuild(string text, int?[] lineNumbers)
        {
            SourceText = text;
            var document = View.Document;
            var layout = BuildLayout(text, lineNumbers);
            if (layout is { } rows && document.TextLength > 0 && RealText() == text && Realign(rows))
            {
                InvalidateAll();
                return;
            }
            // 全体を差し替える。差し替えは元に戻す履歴に残さない（読込み・比較による整列は利用者の編集ではない）。
            var display = BuildDisplay(text, lineNumbers);
            var caretLine = View.TextArea.Caret.Line; var caretColumn = View.TextArea.Caret.Column;
            document.Text = display;
            document.UndoStack.ClearAll();
            if (document.LineCount >= 1)
            {
                View.TextArea.Caret.Line = Math.Clamp(caretLine, 1, document.LineCount);
                View.TextArea.Caret.Column = caretColumn;
            }
            InvalidateAll();
        }

        private void InvalidateAll()
        {
            View.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            View.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            Margin.InvalidateMeasure(); Margin.InvalidateVisual();
        }

        /// <summary>表示を描き直す（表示の設定が変わったとき）。</summary>
        internal void Redraw() => InvalidateAll();

        /// <summary>表示の行ごとに「本物の行か」だけを並べた配列。行番号の並びが比較結果と合わない・目印を含む本文なら null。</summary>
        private static bool[]? BuildLayout(string text, int?[] numbers)
        {
            if (ContainsMark(text)) return null;
            var lines = SplitLines(text);
            if (!ValidNumbers(numbers, lines.Count, EndsWithBreak(text))) return null;
            return numbers.Select(number => number is not null).ToArray();
        }

        private static bool ContainsMark(string text) => text.Contains(GhostMark) || text.Contains(GhostMarkAfterBreak);

        private static bool EndsWithBreak(string text) => text.Length == 0 || text[^1] is '\n' or '\r';

        // 行番号が1始まりの連番で全行を一度ずつ並べているか。末尾が改行で終わる本文は、最後の空行（N+1行目）を含んでもよい。
        private static bool ValidNumbers(int?[] numbers, int lineCount, bool trailingEmptyAllowed)
        {
            var expected = 1;
            foreach (var number in numbers)
            {
                if (number is null) continue;
                if (number != expected) return false;
                expected++;
            }
            return expected - 1 == lineCount || (trailingEmptyAllowed && expected - 1 == lineCount + 1);
        }

        /// <summary>
        /// 本物の行はそのままに、ゴースト行の出し入れだけで新しい並びにそろえる。ゴースト行は表示の状態なので履歴には残さない。
        /// </summary>
        private bool Realign(bool[] rows)
        {
            var document = View.Document;
            var plan = PlanRealign(rows, GhostLineNumbers(), document.LineCount);
            if (plan is null) return false;
            // 何も変わらなければ文書には触れない（元に戻す履歴も変えない）。
            if (plan.Count == 0) return true;
            document.BeginUpdate();
            try { ApplyPlan(plan); }
            finally { document.EndUpdate(); }
            return true;
        }

        private static List<(int Line, bool Insert)>? PlanRealign(bool[] rows, HashSet<int> ghosts, int lineCount)
        {
            // 文書の各行が「ゴースト行か」を並べた写しの上で、上から順に新しい並びへ合わせる。操作の行番号は適用時点のもの。
            var lines = Enumerable.Range(1, lineCount).Select(ghosts.Contains).ToList();
            var plan = new List<(int, bool)>();
            var index = 0;
            foreach (var real in rows)
            {
                if (real)
                {
                    // 本物の行の前にある、もう要らないゴースト行は取り除く。
                    while (index < lines.Count && lines[index]) { plan.Add((index + 1, false)); lines.RemoveAt(index); }
                    if (index >= lines.Count) return null;
                    index++;
                }
                else if (index < lines.Count && lines[index]) index++;
                else { plan.Add((index + 1, true)); lines.Insert(index, true); index++; }
            }
            // 並びの後に残ったゴースト行は取り除く。本文の末尾改行の後の空行（本物）は1行まで残ってよい。
            while (index < lines.Count && lines[index]) { plan.Add((index + 1, false)); lines.RemoveAt(index); }
            return lines.Count - index <= 1 ? plan : null;
        }

        private void ApplyPlan(List<(int Line, bool Insert)> plan)
        {
            var document = View.Document;
            var dominant = DominantEnding();
            foreach (var (line, insert) in plan)
            {
                if (insert)
                {
                    if (line <= document.LineCount)
                        document.Insert(document.GetLineByNumber(line).Offset, GhostMark + (EndingBefore(line) ?? dominant));
                    else
                    {
                        // 文書の末尾へゴースト行を足す。最終行が改行で終わらなければ、間の改行は足したもの（正本へ戻すときに除く）。
                        var lastLine = document.GetLineByNumber(document.LineCount);
                        var ending = EndingBefore(line) ?? dominant;
                        document.Insert(document.TextLength, lastLine.DelimiterLength == 0 ? ending + GhostMarkAfterBreak : ending + GhostMark);
                    }
                }
                else
                {
                    var target = document.GetLineByNumber(line);
                    if (target.DelimiterLength > 0) document.Remove(target.Offset, target.TotalLength);
                    else if (line > 1)
                    {
                        // 最後の行（改行なし）を消すときは、直前の改行も消す。
                        var previous = document.GetLineByNumber(line - 1);
                        document.Remove(previous.EndOffset, previous.DelimiterLength + target.Length);
                    }
                }
            }
        }

        // 直前の行の改行（ゴースト行へ入力したとき、その行の改行が周りとそろうように使う）。
        private string? EndingBefore(int line)
        {
            var document = View.Document;
            for (var number = Math.Min(line - 1, document.LineCount); number >= 1; number--)
            {
                var previous = document.GetLineByNumber(number);
                if (previous.DelimiterLength > 0) return document.GetText(previous.EndOffset, previous.DelimiterLength);
            }
            return null;
        }

        private string DominantEnding()
        {
            var document = View.Document;
            var counts = new Dictionary<string, int>();
            foreach (var line in document.Lines)
                if (line.DelimiterLength > 0) { var ending = document.GetText(line.EndOffset, line.DelimiterLength); counts[ending] = counts.GetValueOrDefault(ending) + 1; }
            return counts.Count == 0 ? "\n" : counts.MaxBy(pair => pair.Value).Key;
        }

        /// <summary>
        /// 表示用の文書を作る。行の対応が比較結果の行番号（1始まりで連番）と一致しない・目印を含む本文は、ゴースト行なしで本文をそのまま返す。
        /// </summary>
        internal static string BuildDisplay(string text, int?[] numbers)
        {
            var lines = SplitLines(text);
            if (ContainsMark(text) || !ValidNumbers(numbers, lines.Count, EndsWithBreak(text))) return text;
            // 末尾改行の後の空行（N+1行目）を並びに含むときは、内容も改行も無い本物の行として扱う。
            if (numbers.Any(number => number == lines.Count + 1)) lines.Add(("", ""));
            var builder = new System.Text.StringBuilder(text.Length + numbers.Length * 2);
            // ゴースト行の改行は、直前の行と同じ改行にする（無ければ本文で最も多い改行）。
            // ゴースト行へ入力して本物の行になったとき、その行の改行が周りとそろうようにするため。
            var dominant = lines.Where(line => line.Ending.Length > 0).GroupBy(line => line.Ending)
                .OrderByDescending(group => group.Count()).Select(group => group.Key).FirstOrDefault() ?? "\n";
            var previous = dominant;
            var pendingBreak = false; // 改行で終わらない本物の行の直後（次がゴースト行なら改行を足して印を付ける）
            for (var i = 0; i < numbers.Length; i++)
            {
                if (pendingBreak) { builder.Append(previous); }
                if (numbers[i] is { } number)
                {
                    var (content, ending) = lines[number - 1];
                    builder.Append(content);
                    pendingBreak = false;
                    if (ending.Length > 0) { builder.Append(ending); previous = ending; }
                    else pendingBreak = i < numbers.Length - 1;
                    if (pendingBreak && numbers[i + 1] is not null) { builder.Append(previous); pendingBreak = false; }
                    continue;
                }
                // 改行で終わらない最終行の直後のゴースト行は、間の改行が足したものだと分かる印を使う。
                var afterBreak = i > 0 && pendingBreak;
                builder.Append(afterBreak ? GhostMarkAfterBreak : GhostMark);
                pendingBreak = false;
                if (i < numbers.Length - 1) builder.Append(previous);
            }
            return builder.ToString();
        }

        /// <summary>行と改行（\r\n・\n・\r）に分ける。改行の種類は行ごとに保つ。</summary>
        internal static List<(string Content, string Ending)> SplitLines(string text)
        {
            var lines = new List<(string, string)>();
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r')
                {
                    var ending = i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : "\r";
                    lines.Add((text[start..i], ending)); i += ending.Length - 1; start = i + 1;
                }
                else if (text[i] == '\n') { lines.Add((text[start..i], "\n")); start = i + 1; }
            }
            if (start < text.Length) lines.Add((text[start..], ""));
            return lines;
        }

        /// <summary>表示の行（1始まり）が正本の何行目（0始まり）か。ゴースト行なら次の本物の行。</summary>
        internal int RealLineOfDisplay(int display)
        {
            var ghosts = GhostLineNumbers();
            var real = 0;
            for (var number = 1; number < display; number++) if (!ghosts.Contains(number)) real++;
            return real;
        }

        /// <summary>正本の行（0始まり）を表示している行（1始まり）。範囲外なら -1。</summary>
        internal int DisplayLineOfReal(int realLine)
        {
            var ghosts = GhostLineNumbers();
            var real = -1;
            for (var number = 1; number <= View.Document.LineCount; number++)
            {
                if (ghosts.Contains(number)) continue;
                if (++real == realLine) return number;
            }
            return -1;
        }

        /// <summary>ゴースト行（目印の文字だけの行）の行番号（1始まり）。文書が変わるまで結果を使い回す。</summary>
        internal HashSet<int> GhostLineNumbers()
        {
            var document = View.Document;
            if (ReferenceEquals(_ghostVersion, document.Version) && _ghostVersion is not null) return _ghostCache;
            var result = new HashSet<int>();
            foreach (var line in document.Lines)
                if (line.Length == 1 && document.GetCharAt(line.Offset) is GhostMark or GhostMarkAfterBreak) result.Add(line.LineNumber);
            _ghostCache = result; _ghostVersion = document.Version;
            return result;
        }

        /// <summary>表示中の文書から、ゴースト行・足した改行・目印を除いた本物の本文を作る。</summary>
        internal string RealText()
        {
            var document = View.Document;
            var ghosts = GhostLineNumbers();
            var builder = new System.Text.StringBuilder(document.TextLength);
            var lastReal = 0;
            for (var number = document.LineCount; number >= 1; number--)
                if (!ghosts.Contains(number)) { lastReal = number; break; }
            for (var number = 1; number <= document.LineCount; number++)
            {
                if (ghosts.Contains(number)) continue;
                var line = document.GetLineByNumber(number);
                var content = document.GetText(line.Offset, line.Length);
                // ゴースト行へ入力して本物になった行には目印が残るので取り除く。
                if (content.Contains(GhostMark) || content.Contains(GhostMarkAfterBreak)) content = content.Replace(GhostMark.ToString(), "").Replace(GhostMarkAfterBreak.ToString(), "");
                builder.Append(content);
                if (line.DelimiterLength == 0) continue;
                // 改行で終わらない最終行の後ろに足した改行は、後ろが全部ゴースト行のままで、その印が付いていれば取り除く。
                if (number == lastReal && number < document.LineCount)
                {
                    var next = document.GetLineByNumber(number + 1);
                    if (next.Length == 1 && document.GetCharAt(next.Offset) == GhostMarkAfterBreak) continue;
                }
                builder.Append(document.GetText(line.EndOffset, line.DelimiterLength));
            }
            return builder.ToString();
        }
    }

    /// <summary>差分の色（本家の差分色・削除色・選択中の差分の色・行内差分）を行の背景に塗る。</summary>
    internal sealed class DiffLineRenderer(DiffEditorSide side) : IBackgroundRenderer
    {
        internal ComparisonPane? Owner { get; set; }
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Owner is not { } owner || owner._diffDisplay.RowCount == 0) return;
            var ghosts = side.GhostLineNumbers();
            var selected = owner.SelectedDisplayRange();
            foreach (var visual in textView.VisualLines)
            {
                var number = visual.FirstDocumentLine.LineNumber;
                if (owner.BrushForLine(side, number, ghosts, selected) is not { } brush) continue;
                var top = visual.VisualTop - textView.ScrollOffset.Y;
                drawingContext.FillRectangle(brush, new Rect(0, top, textView.Bounds.Width, visual.Height));
                // 行内差分は本家の行内差分色で文字の範囲を塗る。「行内差異を表示」を外すと塗らない（本家 OPT_WORDDIFF_HIGHLIGHT）。
                if (ghosts.Contains(number) || !owner.TextViewOptions.ViewLineDifferences) continue;
                var line = visual.FirstDocumentLine;
                foreach (var span in owner.SpansForLine(side, number))
                {
                    if (span.Start + span.Length > line.Length) continue;
                    var segment = new TextSegment { StartOffset = line.Offset + span.Start, Length = Math.Max(span.Length, 1) };
                    foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                        drawingContext.FillRectangle(WinMergeColors.WordDifference, rect);
                }
            }
        }
    }

    /// <summary>本物の行の番号だけを出す行番号欄。ゴースト行は空欄にする（本家と同じ）。</summary>
    internal sealed class RealLineNumberMargin(DiffEditorSide side) : AbstractMargin
    {
        private static readonly Typeface Face = new("Cascadia Mono, Menlo, Consolas, monospace");
        internal ComparisonPane? Owner { get; set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            var (bookmark, numbers) = Widths();
            return new Size(bookmark + numbers, 0);
        }

        private const double BookmarkWidth = 14;
        private static readonly IBrush BookmarkBrush = new SolidColorBrush(Color.FromRgb(0, 102, 204));

        // 本文の拡大／縮小に合わせて、行番号の文字と印の大きさも同じ倍率で変える（本家は行番号にも本文のフォントを使う）。
        private double Scale => Owner?.TextViewOptions.Scale ?? 1;
        private double NumberFontSize => 12 * Scale;

        /// <summary>
        /// 左端のブックマークの印の欄（本家の「マージンを表示」＝選択余白）と、行番号の欄（「行番号を表示」）の幅。
        /// どちらも表示しない設定では 0 にする。
        /// </summary>
        private (double Bookmark, double Numbers) Widths()
        {
            var options = Owner?.TextViewOptions;
            var bookmark = options?.ViewMargins == false ? 0 : BookmarkWidth * Scale;
            if (options?.ViewLineNumbers == false) return (bookmark, 0);
            var digits = Math.Max(3, (side.View.Document?.LineCount ?? 1).ToString().Length);
            var digit = new FormattedText("0", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, NumberFontSize, Brushes.Gray).Width;
            return (bookmark, digits * Math.Max(digit, 8 * Scale) + 10 * Scale);
        }

        public override void Render(DrawingContext context)
        {
            var view = TextView;
            if (view is null || !view.VisualLinesValid || Bounds.Width <= 0) return;
            context.FillRectangle(WinMergeColors.FrameFace, new Rect(Bounds.Size));
            var options = Owner?.TextViewOptions;
            var showNumbers = options?.ViewLineNumbers != false;
            var showMarks = options?.ViewMargins != false;
            var ghosts = side.GhostLineNumbers();
            // 表示の行番号から本物の行番号を求める。ゴースト行を数えずに数え直す。
            var realNumbers = new Dictionary<int, int>();
            var real = 0;
            var lastVisible = view.VisualLines.Count == 0 ? 0 : view.VisualLines[^1].LastDocumentLine.LineNumber;
            // 本文の末尾改行の後の空行（最終行）は本家と同じく番号を付けない。
            var document = side.View.Document;
            var phantom = document.LineCount > 1 && document.GetLineByNumber(document.LineCount).Length == 0 ? document.LineCount : -1;
            for (var number = 1; number <= lastVisible; number++)
                if (!ghosts.Contains(number) && number != phantom) realNumbers[number] = ++real;
            var marks = showMarks ? Owner?.LineBookmarkMarks(side.SourceSide) ?? new Dictionary<int, int?>() : new Dictionary<int, int?>();
            foreach (var visual in view.VisualLines)
            {
                var number = visual.FirstDocumentLine.LineNumber;
                if (!realNumbers.TryGetValue(number, out var shown)) continue;
                if (showNumbers)
                {
                    var text = new FormattedText(shown.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, NumberFontSize, Brushes.Gray);
                    var top = visual.VisualTop - view.ScrollOffset.Y + (visual.Height - text.Height) / 2;
                    context.DrawText(text, new Point(Bounds.Width - text.Width - 4 * Scale, top));
                }
                // 行ブックマークの印。番号付きのブックマークは番号を白で重ねる（本家は番号付きのアイコンを出す）。
                if (!marks.TryGetValue(shown - 1, out var bookmark)) continue;
                var size = Math.Min(10 * Scale, visual.Height - 2);
                var box = new Rect(2 * Scale, visual.VisualTop - view.ScrollOffset.Y + (visual.Height - size) / 2, size, size);
                context.FillRectangle(BookmarkBrush, box, (float)(2 * Scale));
                if (bookmark is { } digit)
                {
                    var label = new FormattedText(digit.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, Face, 9 * Scale, Brushes.White);
                    context.DrawText(label, new Point(box.X + (box.Width - label.Width) / 2, box.Y + (box.Height - label.Height) / 2));
                }
            }
        }

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
            InvalidateVisual();
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();
    }
}
