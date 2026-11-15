using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMergeの「検索」（ID_EDIT_FIND＝Ctrl+F）・「置換」（ID_EDIT_REPLACE＝Ctrl+H）・続けての検索（ID_EDIT_REPEAT＝F3、Shift+F3 で前、
/// Ctrl+F3 で選択中の文字列）・「移動」（ID_EDIT_WMGOTO＝Ctrl+G）を、差分タブのエディターで行う。
///
/// 設計の要点:
/// - 検索は画面の文書ではなく、その側の正本（本物の本文）を対象にする。画面の文書にはゴースト行・足した改行・
///   ゴースト行へ入力した行に残る目印の文字があり、そのまま探すと目印に一致したり位置がずれたりするため。
///   見つけた位置は、本物の行（<see cref="DiffEditorSide.DisplayLineOfReal"/>）と目印を飛ばした列で画面の位置へ直して選択する。
/// - 置換は正本の本文を書き換え、全ペイン共有の履歴へ一つの変更として残す（<see cref="WriteRecordedText"/>）。「すべて置換」も一つの変更で、
///   一回の「元に戻す」で元に戻る（本家もまとめて一つの取り消し単位にする）。読取り専用の側・他の操作の実行中は置き換えない。
/// - 検索の対象の側は、フォーカスのあるペイン（行ブックマークと同じ側）。ダイアログは開いたときの側に結び付く（本家の m_pBuddy）。
/// - 長さ0の一致（正規表現の ^ など）は、直前に見つけた同じ位置では止まらず、その先を探す。
/// </summary>
public sealed partial class ComparisonPane
{
    private const int SearchHistoryLimit = 20;
    private TextSearchSettings _lastSearch = TextSearchSettings.Default;
    private bool _hasLastSearch;
    private string _lastReplacement = "";
    private readonly List<string> _findHistory = [];
    private readonly List<string> _replaceHistory = [];
    private TextSearchDialog? _searchDialog;
    private int _searchSide;
    // 直前に見つけた位置（正本の文字位置）。長さ0の一致で同じ位置に止まり続けないために使う。
    private (int Side, int Start, int Length)? _lastFound;
    // 置換ダイアログを開いたときの選択（範囲を「選択範囲」にしたときの対象、正本の文字位置）。
    private (int Side, int Start, int End)? _replaceBlock;

    /// <summary>headless検証用: 開いている検索・置換ダイアログ。</summary>
    internal TextSearchDialog? TextSearchDialogForTest => _searchDialog;

    private static bool IsGhostMark(char c) => c is DiffEditorSide.GhostMark or DiffEditorSide.GhostMarkAfterBreak;
    private static bool HasGhostMarks(string text) => text.Contains(DiffEditorSide.GhostMark) || text.Contains(DiffEditorSide.GhostMarkAfterBreak);

    /// <summary>フォーカスのあるペインの側。二者比較では中央を使わない。</summary>
    private int SearchSideIndex()
    {
        var side = ActiveLineBookmarkSide;
        return side == 1 && !_diffDisplay.ThreeWay ? 0 : side;
    }

    /// <summary>
    /// 外枠のキーのうち、検索・置換・移動（Ctrl+F／Ctrl+H／Ctrl+G／F3）をこのペインで受けるか。差分タブを表示していて、
    /// フォーカスが通常の入力欄（パスの欄など、自前のキー操作を持つ TextBox）に無いときだけ受ける。バイナリ・表などの
    /// 形式別ビューは別のタブなので、そちらの Ctrl+F／F3 には触れない。
    /// </summary>
    internal bool AcceptsTextSearchKeys(object? focused)
    {
        if (!DiffTabActive()) return false;
        return focused is not Avalonia.Visual visual || visual.FindAncestorOfType<TextBox>(includeSelf: true) is null;
    }

    /// <summary>
    /// 検索の前に、その側の表示が正本と一致していることを確かめる（一致しなければ比較し直してそろえる）。
    /// 一致すれば正本の本文を返す。比較していない・その側を表示していないときは理由を状態欄に出して false。
    /// </summary>
    private bool PrepareTextSearch(int side, out DiffEditorSide view, out string real)
    {
        view = _diffViews[side]; real = "";
        if (_disposed) return false;
        if (CurrentDiff is null || _diffDisplay.RowCount == 0) { _status.Text = "比較してから検索してください。"; return false; }
        if (side == 1 && !_diffDisplay.ThreeWay) { _status.Text = "中央のペインを表示していません。"; return false; }
        var current = view;
        bool Consistent(string source) => current.SourceText == source
            && (HasGhostMarks(source) ? current.View.Document.Text == source : current.RealText() == source);
        var source = TextEditor(side).Text ?? "";
        if (!Consistent(source)) { CompareEditors(); source = TextEditor(side).Text ?? ""; }
        if (!Consistent(source)) { _status.Text = "本文の表示をそろえられないため検索できません。"; return false; }
        real = source;
        return true;
    }

    /// <summary>置換できる側か（読取り専用の側・祖先・他の操作の実行中は置き換えない。<see cref="StepTextHistory"/> と同じ確かめ方）。</summary>
    private bool CanReplaceText(int side, DiffEditorSide view) => CanEditText(side, view, "置換できません");

    // 正本の文字位置と画面の文書の位置の変換。目印を含む本文はゴースト行なしで表示している（位置はそのまま対応する）。
    private static int RealOffsetOfDisplay(DiffEditorSide view, string real, List<(int Start, int Length)> lines, int offset)
    {
        var document = view.View.Document;
        offset = Math.Clamp(offset, 0, document.TextLength);
        if (HasGhostMarks(real)) return Math.Min(offset, real.Length);
        var line = document.GetLineByOffset(offset);
        var realLine = view.RealLineOfDisplay(line.LineNumber);
        if (realLine >= lines.Count) return real.Length;
        var (start, length) = lines[realLine];
        // ゴースト行の位置は、次の本物の行の先頭として扱う。
        if (view.GhostLineNumbers().Contains(line.LineNumber)) return start;
        if (offset > line.EndOffset)
        {
            var next = realLine + 1 < lines.Count ? lines[realLine + 1].Start : real.Length;
            return Math.Min(start + length + (offset - line.EndOffset), next);
        }
        var column = 0;
        for (var p = line.Offset; p < offset; p++) if (!IsGhostMark(document.GetCharAt(p))) column++;
        return start + Math.Min(column, length);
    }

    private static int DisplayOffsetOfReal(DiffEditorSide view, string real, List<(int Start, int Length)> lines, int offset)
    {
        var document = view.View.Document;
        offset = Math.Clamp(offset, 0, real.Length);
        if (HasGhostMarks(real)) return Math.Min(offset, document.TextLength);
        var realLine = TextSearcher.LineOf(lines, offset);
        var (start, length) = lines[realLine];
        var display = view.DisplayLineOfReal(realLine);
        if (display < 1) return document.TextLength;
        var line = document.GetLineByNumber(display);
        var column = offset - start;
        if (column > length) return Math.Min(line.EndOffset + (column - length), line.EndOffset + line.DelimiterLength);
        // 目印の文字は数えずに進み、目的の列に着いたら直後の目印も飛ばす（選択に目印を含めない）。
        var p = line.Offset; var counted = 0;
        while (p < line.EndOffset && (counted < column || IsGhostMark(document.GetCharAt(p))))
        {
            if (!IsGhostMark(document.GetCharAt(p))) counted++;
            p++;
        }
        return p;
    }

    /// <summary>その側の選択（選択が無ければキャレット）を正本の文字位置で返す。</summary>
    private static (int Start, int End) RealSelection(DiffEditorSide view, string real, List<(int Start, int Length)> lines)
    {
        var area = view.View.TextArea;
        var (start, end) = area.Selection.IsEmpty ? (area.Caret.Offset, area.Caret.Offset) : (area.Selection.SurroundingSegment.Offset, area.Selection.SurroundingSegment.EndOffset);
        var realStart = RealOffsetOfDisplay(view, real, lines, start);
        return (realStart, Math.Max(realStart, RealOffsetOfDisplay(view, real, lines, end)));
    }

    /// <summary>正本の範囲を画面で選択し、その行が見えるようにスクロールする。</summary>
    private void SelectRealRange(int side, DiffEditorSide view, string real, int start, int length)
    {
        var lines = TextSearcher.Lines(real);
        var displayStart = DisplayOffsetOfReal(view, real, lines, start);
        var displayEnd = Math.Max(displayStart, DisplayOffsetOfReal(view, real, lines, start + length));
        view.View.Select(displayStart, displayEnd - displayStart);
        _activeLineBookmarkSide = side;
        ScrollDiffEditorsToDisplayRow(view.View.Document.GetLineByOffset(displayStart).LineNumber - 1);
        view.View.TextArea.Caret.BringCaretToView();
    }

    /// <summary>本家の検索ダイアログの初期の文字列: 一行の中の選択ならその文字列、選択が無ければキャレットの位置の単語。</summary>
    private static string? SelectedOrWordAtCaret(DiffEditorSide view, string real, List<(int Start, int Length)> lines)
    {
        var (start, end) = RealSelection(view, real, lines);
        if (end > start)
        {
            var selected = real[start..end];
            return selected.IndexOfAny(['\r', '\n']) < 0 ? selected : null;
        }
        var (lineStart, lineLength) = lines[TextSearcher.LineOf(lines, start)];
        int left = start, right = start;
        static bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';
        while (left > lineStart && Word(real[left - 1])) left--;
        while (right < lineStart + lineLength && Word(real[right])) right++;
        return right > left ? real[left..right] : null;
    }

    private void RememberSearch(TextSearchSettings settings, string? replacement)
    {
        _lastSearch = settings; _hasLastSearch = true;
        Remember(_findHistory, settings.Pattern);
        if (replacement is not null) { _lastReplacement = replacement; Remember(_replaceHistory, replacement); }
        static void Remember(List<string> history, string value)
        {
            if (value.Length == 0) return;
            history.Remove(value); history.Insert(0, value);
            if (history.Count > SearchHistoryLimit) history.RemoveAt(history.Count - 1);
        }
    }

    private static string NotFoundMessage(string pattern) => $"文字列 \"{pattern}\" が見つかりません。";

    /// <summary>検索・置換ダイアログを開く（Ctrl+F／Ctrl+H）。既に同じ側の同じダイアログを開いていれば前へ出す。</summary>
    internal void OpenTextSearch(bool replace)
    {
        var side = SearchSideIndex();
        if (!PrepareTextSearch(side, out var view, out var real)) return;
        // 本家は書き込めない文書では置換ダイアログを開かない（OnEditReplace の QueryEditable）。
        if (replace && !CanReplaceText(side, view)) return;
        var lines = TextSearcher.Lines(real);
        var seed = SelectedOrWordAtCaret(view, real, lines);
        var (selectionStart, selectionEnd) = RealSelection(view, real, lines);
        var hasSelection = selectionEnd > selectionStart;
        _replaceBlock = hasSelection ? (side, selectionStart, selectionEnd) : null;
        if (_searchDialog is { } open)
        {
            if (open.ReplaceMode == replace && _searchSide == side) { open.Reuse(seed); return; }
            open.Close();
        }
        _searchSide = side; _lastFound = null;
        // 複数行を選んでいれば、範囲を初めから「選択範囲」にする（本家 SetScope）。
        var multiline = hasSelection && real.AsSpan(selectionStart, selectionEnd - selectionStart).IndexOfAny('\r', '\n') >= 0;
        var dialog = new TextSearchDialog(replace, _lastSearch, multiline ? null : seed, _findHistory, _replaceHistory, _lastReplacement,
            replace && hasSelection, multiline, ExecuteTextSearchRequest);
        dialog.Closed += (_, _) => { if (ReferenceEquals(_searchDialog, dialog)) _searchDialog = null; };
        _searchDialog = dialog;
        dialog.ShowFor(_owner);
    }

    /// <summary>ペインを閉じるときに、開いている検索・置換ダイアログも閉じる。</summary>
    private void CloseTextSearchDialog()
    {
        var dialog = _searchDialog; _searchDialog = null;
        dialog?.Close();
    }

    private bool ExecuteTextSearchRequest(TextSearchRequest request) => request.Action switch
    {
        TextSearchAction.FindNext => FindText(request.Settings, backward: false, _searchSide, request.SelectionOnly),
        TextSearchAction.FindPrevious => FindText(request.Settings, backward: true, _searchSide, false),
        TextSearchAction.Replace => ReplaceText(request.Settings, request.Replacement, _searchSide),
        TextSearchAction.ReplaceAll => ReplaceAllText(request.Settings, request.Replacement, _searchSide, request.SelectionOnly),
        _ => false,
    };

    /// <summary>
    /// 選択（キャレット）の後ろ（backward なら前）を探して選択する。見つからなければ状態欄に出して false。
    /// inBlock なら置換ダイアログを開いたときの選択範囲の中だけを探す。
    /// </summary>
    internal bool FindText(TextSearchSettings settings, bool backward, int side, bool inBlock = false)
    {
        if (!PrepareTextSearch(side, out var view, out var real)) return false;
        TextSearcher searcher;
        try { searcher = new TextSearcher(settings.Query); }
        catch (FormatException error) { _status.Text = error.Message; return false; }
        RememberSearch(settings, null);
        var lines = TextSearcher.Lines(real);
        var (selectionStart, selectionEnd) = RealSelection(view, real, lines);
        var from = backward ? selectionStart : selectionEnd;
        int? skip = _lastFound is { Length: 0 } last && last.Side == side && last.Start == from ? from : null;
        var (blockStart, blockEnd) = inBlock && _replaceBlock is { } block && block.Side == side
            ? (Math.Min(block.Start, real.Length), Math.Min(block.End, real.Length)) : (0, real.Length);
        TextSearchMatch? found;
        try
        {
            found = backward ? searcher.FindBackward(real, from, !settings.NoWrap, skip, out _)
                : searcher.FindForward(real, from, blockStart, blockEnd, !settings.NoWrap, skip, out _);
        }
        catch (RegexMatchTimeoutException) { _status.Text = "正規表現の検索に時間がかかりすぎたため中止しました。"; return false; }
        if (found is null) { _status.Text = NotFoundMessage(settings.Pattern); return false; }
        SelectRealRange(side, view, real, found.Start, found.Length);
        _lastFound = (side, found.Start, found.Length);
        return true;
    }

    /// <summary>
    /// 本家の「置換して次に」: 選択している文字列が検索の一致なら置き換えて次を探す。一致でなければ、まず次の一致を選ぶだけにする
    /// （本家 OnEditReplace の m_bFound が偽のとき）。
    /// </summary>
    internal bool ReplaceText(TextSearchSettings settings, string replacement, int side)
    {
        if (!PrepareTextSearch(side, out var view, out var real)) return false;
        if (!CanReplaceText(side, view)) return false;
        TextSearcher searcher;
        try { searcher = new TextSearcher(settings.Query); }
        catch (FormatException error) { _status.Text = error.Message; return false; }
        var lines = TextSearcher.Lines(real);
        var (selectionStart, selectionEnd) = RealSelection(view, real, lines);
        TextSearchMatch? match;
        try { match = searcher.MatchAt(real, selectionStart, selectionEnd - selectionStart); }
        catch (RegexMatchTimeoutException) { _status.Text = "正規表現の検索に時間がかかりすぎたため中止しました。"; return false; }
        // 長さ0の一致は、キャレットがその位置にあって、直前にそこを見つけたときだけ置き換える（キャレットの位置だけで置き換えない）。
        if (match is { Length: 0 } && !(_lastFound is { Length: 0 } last && last.Side == side && last.Start == match.Start)) match = null;
        if (match is null) return FindText(settings, backward: false, side);
        string inserted;
        try { inserted = searcher.ReplacementFor(match, replacement); }
        catch (FormatException error) { _status.Text = error.Message; return false; }
        RememberSearch(settings, replacement);
        var text = string.Concat(real.AsSpan(0, match.Start), inserted, real.AsSpan(match.Start + match.Length));
        ApplyReplacedText(side, text);
        var caret = match.Start + inserted.Length;
        view = _diffViews[side];
        SelectRealRange(side, view, text, caret, 0);
        _lastFound = match.Length == 0 ? (side, caret, 0) : null;
        FindText(settings, backward: false, side);
        return true;
    }

    /// <summary>本家の「すべて置換」。範囲（ファイル全体か、開いたときの選択範囲）の一致をすべて置き換え、置き換えた数を状態欄に出す。</summary>
    internal bool ReplaceAllText(TextSearchSettings settings, string replacement, int side, bool selectionOnly)
    {
        if (!PrepareTextSearch(side, out var view, out var real)) return false;
        if (!CanReplaceText(side, view)) return false;
        TextSearcher searcher;
        try { searcher = new TextSearcher(settings.Query); }
        catch (FormatException error) { _status.Text = error.Message; return false; }
        var (start, end) = selectionOnly && _replaceBlock is { } block && block.Side == side
            ? (Math.Min(block.Start, real.Length), Math.Min(block.End, real.Length)) : (0, real.Length);
        string text; int count;
        try { (text, count) = searcher.ReplaceAll(real, replacement, start, end); }
        catch (FormatException error) { _status.Text = error.Message; return false; }
        catch (RegexMatchTimeoutException) { _status.Text = "正規表現の検索に時間がかかりすぎたため中止しました。"; return false; }
        RememberSearch(settings, replacement);
        if (!string.Equals(text, real, StringComparison.Ordinal)) ApplyReplacedText(side, text);
        view = _diffViews[side];
        if (selectionOnly && _replaceBlock is { } kept && kept.Side == side)
        {
            // 選択範囲を、置き換えた後の範囲で選び直す（続けて同じ範囲で置き換えられるように）。
            var newEnd = end + text.Length - real.Length;
            _replaceBlock = (side, start, newEnd);
            SelectRealRange(side, view, text, start, newEnd - start);
        }
        _lastFound = null;
        _status.Text = $"{count} 個の文字列を置換しました。";
        return true;
    }

    /// <summary>置換した本文を、共有の履歴に一つの変更として残して書き込み、比較し直す（元に戻すと同じ経路）。</summary>
    private void ApplyReplacedText(int side, string text)
    {
        _diffEditTimer?.Stop();
        WriteRecordedText(side, text);
        CompareEditors();
    }

    /// <summary>
    /// F3／Shift+F3（本家 OnEditRepeat）: 前回の条件で次（前）を探す。selected（Ctrl+F3）なら選択中の文字列か、キャレットの単語を探す。
    /// 探す文字列が無ければ検索ダイアログを開く。
    /// </summary>
    internal bool RepeatTextSearch(bool backward, bool selected)
    {
        var side = SearchSideIndex();
        if (!PrepareTextSearch(side, out var view, out var real)) return false;
        var settings = _lastSearch;
        if (selected && SelectedOrWordAtCaret(view, real, TextSearcher.Lines(real)) is { Length: > 0 } text) settings = settings with { Pattern = text };
        else if (!_hasLastSearch || settings.Pattern.Length == 0) { OpenTextSearch(false); return false; }
        return FindText(settings, backward, side);
    }

    /// <summary>各側の最後の行番号（ゴースト行を数えない本物の行の数。本家 ComputeRealLine(行数-1)+1）。表示していない側は -1。</summary>
    internal int[] DiffEditorLastLines() =>
    [
        RealLineCount(_diffViews[0]),
        _diffDisplay.ThreeWay ? RealLineCount(_diffViews[1]) : -1,
        RealLineCount(_diffViews[2]),
    ];

    private static int RealLineCount(DiffEditorSide view) => Math.Max(1, view.View.Document.LineCount - view.GhostLineNumbers().Count);

    /// <summary>本家の「移動」（OnWMGoto）。フォーカスのある側とキャレットの行を初期値にしてダイアログを開き、行か差異へ移動する。</summary>
    internal async Task GoToAsync()
    {
        if (CurrentDiff is not { } diff || _diffDisplay.RowCount == 0) { _status.Text = "比較してから移動してください。"; return; }
        var side = SearchSideIndex();
        var view = _diffViews[side];
        var lastLines = DiffEditorLastLines();
        var line = Math.Clamp(view.RealLineOfDisplay(view.View.TextArea.Caret.Line) + 1, 1, lastLines[side]);
        var choice = await new GoToDialog(lastLines, diff.Blocks.Count, side, line).ShowAsync(_owner);
        if (choice is null || _disposed) return;
        ApplyGoTo(choice);
    }

    /// <summary>移動の結果を当てる。番号は今の行数・差異の数で切り詰める（本家と同じ）。</summary>
    internal void ApplyGoTo(GoToDialog.Choice choice)
    {
        if (CurrentDiff is not { } diff) return;
        if (choice.Difference)
        {
            if (diff.Blocks.Count == 0) return;
            _diffIndex = Math.Clamp(choice.Number - 1, 0, diff.Blocks.Count - 1);
            DiffList.SelectedIndex = diff.Blocks[_diffIndex].RowStart;
            RevealDifference(diff.Blocks[_diffIndex]);
            return;
        }
        var lastLines = DiffEditorLastLines();
        if (lastLines[choice.Side] < 1) return;
        FocusDiffEditorRealLine(choice.Side, Math.Clamp(choice.Number - 1, 0, lastLines[choice.Side] - 1));
    }
}
