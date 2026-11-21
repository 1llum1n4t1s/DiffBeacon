using System.Text;
using Avalonia.Controls;
using DiffBeacon.Core;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMergeの「編集」メニューの切り取り（ID_EDIT_CUT）・コピー（ID_EDIT_COPY）・貼り付け（ID_EDIT_PASTE）・すべて選択（ID_EDIT_SELECT_ALL）、
/// ブックマーク（ID_EDIT_TOGGLE_BOOKMARK＝Ctrl+F2、ID_EDIT_GOTO_NEXT_BOOKMARK＝F2、ID_EDIT_GOTO_PREV_BOOKMARK＝Shift+F2、ID_EDIT_CLEAR_ALL_BOOKMARKS）と、
/// 本文の右クリックメニュー（本家 IDR_POPUP_MERGEVIEW の一部）を、差分タブのエディターで行う。
///
/// 設計の要点:
/// - コピー・切り取りは本家 CMergeEditView::OnEditCopy／OnEditCut（GetTextWithoutEmptys）と同じく、ゴースト行・足した改行・
///   目印の文字を除いた本物の本文をクリップボードへ渡す。エディター組込みのコピーだと目印の文字まで写るため、キー（実行環境の
///   コピー・切り取り・貼り付けのキー）もエディターより先に受けてこちらで処理する。
/// - 切り取り・貼り付けは正本を書き換えて全ペイン共有の履歴へ一件で残す（置換と同じ経路）。読取り専用の側・祖先・他の操作の実行中は書き換えない。
///   貼り付けた文字列の改行は、貼り付けた位置の行の改行（無ければ本文で最も多い改行）にそろえる（本家は文書の改行の形式へ変換する）。
/// - クリップボードを待つ間に本文・選択が変わったら、古い操作として何も変えない。
/// - headless 検証は <see cref="DiffClipboardBridge"/> で利用者のクリップボードを使わずに行う。
/// </summary>
public sealed partial class ComparisonPane
{
    /// <summary>検証用: 設定するとクリップボードの代わりに使う（利用者のクリップボードを置き換えない）。</summary>
    internal IEditorClipboard? DiffClipboardBridge { get; set; }

    /// <summary>右クリックの「選択した差異(右側)」（二者の Ctrl+2）。三者では隠す。</summary>
    private readonly List<MenuItem> _copyDiffSecondItems = [];

    /// <summary>右クリックの「選択した差異(中央)」（三者の Ctrl+2）。二者では隠す。</summary>
    private readonly List<MenuItem> _copyDiffMiddleItems = [];

    /// <summary>右クリックの「選択した差異(右側)」（三者の Ctrl+3）。二者では隠す。</summary>
    private readonly List<MenuItem> _copyDiffThirdItems = [];

    /// <summary>右クリックの「選択した行を中央にコピー」。二者では隠す。</summary>
    private readonly List<MenuItem> _copyLinesToMiddleItems = [];

    private IEditorClipboard? DiffClipboard()
    {
        if (DiffClipboardBridge is { } bridge) return bridge;
        return TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? new PlatformEditorClipboard(clipboard) : null;
    }

    private sealed class PlatformEditorClipboard(Avalonia.Input.Platform.IClipboard clipboard) : IEditorClipboard
    {
        public Task<string?> ReadTextAsync() => clipboard.TryGetTextAsync();
        public Task WriteTextAsync(string text) => clipboard.SetTextAsync(text);
    }

    /// <summary>差分タブのエディターで編集の操作を受けるか（差分タブを表示していて比較結果がある）。</summary>
    private bool DiffEditCommandAvailable(out int side)
    {
        side = SearchSideIndex();
        if (!DiffTabActive() || CurrentDiff is null || _diffDisplay.RowCount == 0) { _status.Text = "比較してから操作してください。"; return false; }
        return true;
    }

    /// <summary>書き換えてよい側か（読取り専用の側・祖先・他の操作の実行中は書き換えない）。</summary>
    private bool CanEditText(int side, DiffEditorSide view, string verb)
    {
        if (!CompareButton.IsEnabled) { _status.Text = $"他の操作の実行中は{verb}。"; return false; }
        if (view.View.IsReadOnly || TextEditor(side).IsReadOnly || !TextSideWritable(side))
        { _status.Text = $"{TextSideName(side)}は読取り専用のため{verb}。"; return false; }
        return true;
    }

    /// <summary>本家の「コピー」: 選択の本物の本文（ゴースト行と目印を除く）をクリップボードへ渡す。選択が無ければ何もしない。</summary>
    internal async Task CopyDiffSelectionAsync(bool cut)
    {
        if (!DiffEditCommandAvailable(out var side)) return;
        if (!PrepareTextSearch(side, out var view, out var real)) return;
        var lines = TextSearcher.Lines(real);
        var (start, end) = RealSelection(view, real, lines);
        if (end <= start) return;
        if (cut && !CanEditText(side, view, "切り取れません")) return;
        if (DiffClipboard() is not { } clipboard) { _status.Text = "クリップボードを利用できません。"; return; }
        if (view.View.TextArea.Selection is AvaloniaEdit.Editing.RectangleSelection rectangle)
        {
            var boxed = rectangle.GetText().Replace(DiffEditorSide.GhostMark.ToString(), "").Replace(DiffEditorSide.GhostMarkAfterBreak.ToString(), "");
            await clipboard.WriteTextAsync(boxed);
            if (!cut || _disposed) return;
            if ((TextEditor(side).Text ?? "") != real || view.View.TextArea.Selection is not AvaloniaEdit.Editing.RectangleSelection current)
            { _status.Text = "本文または選択が変わったため切り取りませんでした。"; return; }
            if (!CanEditText(side, view, "切り取れません")) return;
            var ranges = new List<(int Start, int End)>();
            foreach (var segment in current.Segments)
            {
                var rangeStart = RealOffsetOfDisplay(view, real, lines, segment.StartOffset);
                var rangeEnd = RealOffsetOfDisplay(view, real, lines, segment.EndOffset);
                if (rangeEnd > rangeStart) ranges.Add((rangeStart, rangeEnd));
            }
            var merged = MergeColumnRanges(ranges);
            if (merged.Count == 0) return;
            var cutText = real;
            for (var index = merged.Count - 1; index >= 0; index--)
                cutText = string.Concat(cutText.AsSpan(0, merged[index].Start), cutText.AsSpan(merged[index].End));
            ApplyReplacedText(side, cutText);
            SelectRealRange(side, _diffViews[side], cutText, merged[0].Start, 0);
            return;
        }
        var selection = (view.View.SelectionStart, view.View.SelectionLength);
        await clipboard.WriteTextAsync(real[start..end]);
        if (!cut || _disposed) return;
        // クリップボードを待つ間に本文や選択が変わっていれば、古い操作として切り取らない（コピーは済んでいる）。
        if ((TextEditor(side).Text ?? "") != real || (view.View.SelectionStart, view.View.SelectionLength) != selection)
        { _status.Text = "本文または選択が変わったため切り取りませんでした。"; return; }
        var text = string.Concat(real.AsSpan(0, start), real.AsSpan(end));
        ApplyReplacedText(side, text);
        SelectRealRange(side, _diffViews[side], text, start, 0);
    }

    /// <summary>本家の「貼り付け」: クリップボードの文字列で選択（無ければキャレットの位置）を置き換える。</summary>
    internal async Task PasteDiffTextAsync()
    {
        if (!DiffEditCommandAvailable(out var side)) return;
        if (!PrepareTextSearch(side, out var view, out var real)) return;
        if (!CanEditText(side, view, "貼り付けできません")) return;
        if (DiffClipboard() is not { } clipboard) { _status.Text = "クリップボードを利用できません。"; return; }
        var lines = TextSearcher.Lines(real);
        var (start, end) = RealSelection(view, real, lines);
        var selection = (view.View.SelectionStart, view.View.SelectionLength, view.View.CaretOffset);
        var pasted = await clipboard.ReadTextAsync();
        if (string.IsNullOrEmpty(pasted) || _disposed) return;
        if ((TextEditor(side).Text ?? "") != real || (view.View.SelectionStart, view.View.SelectionLength, view.View.CaretOffset) != selection)
        { _status.Text = "本文または選択が変わったため貼り付けませんでした。"; return; }
        if (!CanEditText(side, _diffViews[side], "貼り付けできません")) return;
        // 目印の文字は本物の本文に入れない（ゴースト行と取り違えないため）。改行は貼り付けた位置の行の改行にそろえる。
        pasted = pasted.Replace(DiffEditorSide.GhostMark.ToString(), "").Replace(DiffEditorSide.GhostMarkAfterBreak.ToString(), "");
        var inserted = NormalizeLineBreaks(pasted, LineEndingAt(real, lines, start));
        var text = string.Concat(real.AsSpan(0, start), inserted, real.AsSpan(end));
        ApplyReplacedText(side, text);
        SelectRealRange(side, _diffViews[side], text, start + inserted.Length, 0);
    }

    /// <summary>矩形の各行の範囲を、重なりをまとめて先頭から並べる。</summary>
    private static List<(int Start, int End)> MergeColumnRanges(List<(int Start, int End)> ranges)
    {
        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<(int Start, int End)>();
        foreach (var range in ranges)
        {
            if (merged.Count == 0 || range.Start > merged[^1].End) merged.Add(range);
            else merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
        }
        return merged;
    }

    /// <summary>位置を含む行の改行。その行が改行で終わらなければ前の行の改行、それも無ければ本文で最も多い改行、本文に改行が無ければ実行環境の改行。</summary>
    private static string LineEndingAt(string real, List<(int Start, int Length)> lines, int offset)
    {
        string? Ending(int index)
        {
            var (start, length) = lines[index];
            var end = start + length;
            if (end >= real.Length) return null;
            return real[end] == '\r' && end + 1 < real.Length && real[end + 1] == '\n' ? "\r\n" : real[end].ToString();
        }
        var line = TextSearcher.LineOf(lines, offset);
        for (var index = line; index >= 0; index--) if (Ending(index) is { } ending) return ending;
        var counts = new Dictionary<string, int>();
        for (var index = 0; index < lines.Count; index++) if (Ending(index) is { } ending) counts[ending] = counts.GetValueOrDefault(ending) + 1;
        return counts.Count > 0 ? counts.MaxBy(pair => pair.Value).Key : Environment.NewLine;
    }

    private static string NormalizeLineBreaks(string text, string ending)
    {
        if (text.IndexOfAny(['\r', '\n']) < 0) return text;
        var builder = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; builder.Append(ending); }
            else if (text[i] == '\n') builder.Append(ending);
            else builder.Append(text[i]);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 本家の行番号付きコピー（ID_EDIT_COPY_LINENUMBERS、Ctrl+Shift+C）。
    /// 選択した表示行のうちゴースト行を除き、最後の表示行の本物の行番号の桁に合わせて右寄せする。
    /// 各行は「番号: 本文」に、その行の改行を続ける（行を連結しないため）。
    /// </summary>
    internal async Task CopyDiffLinesWithNumbersAsync()
    {
        if (!DiffEditCommandAvailable(out var side)) return;
        var view = _diffViews[side];
        var editor = view.View;
        if (editor.SelectionLength <= 0) { _status.Text = "選択がありません。"; return; }
        if (DiffClipboard() is not { } clipboard) { _status.Text = "クリップボードを利用できません。"; return; }
        var document = editor.Document;
        var startLine = document.GetLineByOffset(editor.SelectionStart).LineNumber;
        var endOffset = editor.SelectionStart + editor.SelectionLength;
        var endLine = document.GetLineByOffset(endOffset > editor.SelectionStart ? endOffset - 1 : editor.SelectionStart).LineNumber;
        var ghosts = view.GhostLineNumbers();
        var source = DiffEditorSide.SplitLines(view.SourceText);
        var chosen = new List<(int Number, string Content, string Ending)>();
        for (var line = startLine; line <= endLine; line++)
        {
            if (ghosts.Contains(line)) continue;
            var real = view.RealLineOfDisplay(line);
            if (real < 0 || real >= source.Count) continue;
            chosen.Add((real + 1, source[real].Content, source[real].Ending));
        }
        if (chosen.Count == 0) { _status.Text = "選択がありません。"; return; }
        // 本家は選択末尾の表示行を ComputeRealLine した行番号の桁幅を使う。
        var width = Math.Max(1, (view.RealLineOfDisplay(endLine) + 1).ToString().Length);
        var builder = new StringBuilder();
        foreach (var (number, content, ending) in chosen)
        {
            var digits = number.ToString();
            if (digits.Length < width) builder.Append(' ', width - digits.Length);
            builder.Append(digits).Append(": ").Append(content).Append(ending);
        }
        await clipboard.WriteTextAsync(builder.ToString());
    }

    /// <summary>
    /// 本家の選択した差異のコピー（ID_SEL_DIFF_COPY_1ST／2ND／3RD、Ctrl+1／2／3）。
    /// 二者では 1 が左、2 が右。三者では 1 が左、2 が中央、3 が右。
    /// ゴースト行は除き、最後の表示行の改行を足す（CMergeEditView::SelDiffCopy）。
    /// </summary>
    internal async Task CopySelectedDifferenceAsync(int ordinal)
    {
        if (!DiffEditCommandAvailable(out _)) return;
        if (SelectedDiffOrdinalSide(ordinal) is not int side) { _status.Text = "その側は比較にありません。"; return; }
        if (CurrentCopyBlock() is not { } block) { _status.Text = "コピーする差異はありません。"; return; }
        if (DiffClipboard() is not { } clipboard) { _status.Text = "クリップボードを利用できません。"; return; }
        await clipboard.WriteTextAsync(SelectedDiffText(block, side));
    }

    /// <summary>WinMerge のバッファ番号を、画面の左 0・中央 1・右 2 にする。二者の 2 番目は右。</summary>
    private int? SelectedDiffOrdinalSide(int ordinal) => ordinal switch
    {
        0 => 0,
        1 => _diffDisplay.ThreeWay ? 1 : 2,
        2 => _diffDisplay.ThreeWay ? 2 : null,
        _ => null,
    };

    /// <summary>選択中の差分。一覧で選んでいなければ、キャレットのある表示行の差分。</summary>
    private DiffBlock? CurrentCopyBlock()
    {
        if (CurrentDiff is not { } diff || diff.Blocks.Count == 0) return null;
        if (_selectedBlock >= 0 && _selectedBlock < diff.Blocks.Count) return diff.Blocks[_selectedBlock];
        var display = _diffViews[SearchSideIndex()].View.TextArea.Caret.Line - 1;
        var row = _diffDisplay.TwoWayRowOf(display);
        if (row < 0) return null;
        return diff.Blocks.FirstOrDefault(block => row >= block.RowStart && row < block.RowStart + block.RowCount);
    }

    /// <summary>差異の範囲の本物の本文。最後の表示行がゴーストでも、その行の改行は足す。</summary>
    private string SelectedDiffText(DiffBlock block, int side)
    {
        var numbers = _diffDisplay.LineNumbers[side];
        var lines = DiffEditorSide.SplitLines(_diffDisplay.Texts[side]);
        var displays = DiffDisplays(block);
        if (displays.Count == 0) return "";
        var builder = new StringBuilder();
        var lastDisplay = displays[^1];
        foreach (var display in displays)
        {
            if (display >= numbers.Length || numbers[display] is not int number || number < 1 || number > lines.Count) continue;
            var line = lines[number - 1];
            builder.Append(line.Content);
            if (display != lastDisplay) builder.Append(line.Ending);
        }
        builder.Append(EndingOfDisplayRow(side, lastDisplay, lines));
        return builder.ToString();
    }

    /// <summary>差異に含まれる表示行。二者の行の間に入る中央だけの行も含む。</summary>
    private List<int> DiffDisplays(DiffBlock block)
    {
        var start = -1;
        var end = -1;
        for (var row = block.RowStart; row < block.RowStart + block.RowCount; row++)
        {
            var display = _diffDisplay.DisplayRowOf(row);
            if (display < 0) continue;
            if (start < 0 || display < start) start = display;
            if (display > end) end = display;
        }
        var displays = new List<int>();
        if (start < 0) return displays;
        for (var display = start; display <= end; display++)
        {
            var row = _diffDisplay.TwoWayRowOf(display);
            if (row >= block.RowStart && row < block.RowStart + block.RowCount) displays.Add(display);
        }
        return displays;
    }

    /// <summary>表示行の改行。ゴースト行は直前の本物の行と同じ改行（表示を作るときと同じ）。</summary>
    private string EndingOfDisplayRow(int side, int display, List<(string Content, string Ending)> lines)
    {
        var numbers = _diffDisplay.LineNumbers[side];
        if (display >= 0 && display < numbers.Length && numbers[display] is int number && number >= 1 && number <= lines.Count)
            return lines[number - 1].Ending;
        for (var row = Math.Min(display, numbers.Length) - 1; row >= 0; row--)
            if (numbers[row] is int previous && previous >= 1 && previous <= lines.Count && lines[previous - 1].Ending.Length > 0)
                return lines[previous - 1].Ending;
        return lines.FirstOrDefault(line => line.Ending.Length > 0).Ending is { Length: > 0 } ending ? ending : "\n";
    }

    /// <summary>二者と三者で、右クリックの差異コピーの出し分けを本家に合わせる。</summary>
    private void UpdateSelectedDiffCopyMenu()
    {
        var three = _diffDisplay.ThreeWay;
        foreach (var item in _copyDiffSecondItems) item.IsVisible = !three;
        foreach (var item in _copyDiffMiddleItems) item.IsVisible = three;
        foreach (var item in _copyDiffThirdItems) item.IsVisible = three;
        foreach (var item in _copyLinesToMiddleItems) item.IsVisible = three;
    }

    /// <summary>本家の「すべて選択」: フォーカスのあるペインの本文をすべて選ぶ。</summary>
    internal void SelectAllDiffText()
    {
        if (!DiffEditCommandAvailable(out var side)) return;
        var view = _diffViews[side].View;
        view.SelectAll();
        _activeLineBookmarkSide = side;
    }

    /// <summary>
    /// エディターのキー（実行環境のコピー・切り取り・貼り付けのキー。Windows では Ctrl+C／Ctrl+Insert など）を、組込みの処理より先に受ける。
    /// 処理したら true。
    /// </summary>
    private bool HandleDiffClipboardKey(DiffEditorSide side, KeyEventArgs args)
    {
        if (Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration is not { } hotkeys) return false;
        Func<Task>? action = hotkeys.Copy.Any(gesture => gesture.Matches(args)) ? () => CopyDiffSelectionAsync(cut: false)
            : hotkeys.Cut.Any(gesture => gesture.Matches(args)) ? () => CopyDiffSelectionAsync(cut: true)
            : hotkeys.Paste.Any(gesture => gesture.Matches(args)) ? PasteDiffTextAsync
            : null;
        if (action is null) return false;
        _activeLineBookmarkSide = side.SourceSide;
        _ = GuardAsync(action);
        return true;
    }

    /// <summary>本家のブックマークのメニュー（Ctrl+F2／F2／Shift+F2／すべてクリア）。ペインの行ブックマークの操作を使う。</summary>
    internal void ExecuteBookmarkCommand(FrameCommand command)
    {
        if (!DiffEditCommandAvailable(out var side)) return;
        _activeLineBookmarkSide = side;
        switch (command)
        {
            case FrameCommand.ToggleBookmark: TogglePlainBookmark(); break;
            case FrameCommand.NextBookmark: NavigateBookmark(1); break;
            case FrameCommand.PreviousBookmark: NavigateBookmark(-1); break;
            case FrameCommand.ClearAllBookmarks: ClearAllLineBookmarks(); break;
        }
    }

    /// <summary>行番号の欄に描くブックマーク（正本の行（0始まり）と、番号付きならその番号）。</summary>
    internal IReadOnlyDictionary<int, int?> LineBookmarkMarks(int side)
    {
        var marks = new Dictionary<int, int?>();
        foreach (var line in _plainLineBookmarks[side]) marks[line] = null;
        for (var number = 0; number < 10; number++)
            if (_numberedLineBookmarks[side][number] is { } line) marks[line] = number;
        return marks;
    }

    /// <summary>本家の本文の右クリックメニュー（IDR_POPUP_MERGEVIEW のうち対応する操作）。開いたペインを操作の対象にする。</summary>
    private void AttachDiffEditorContextMenu(DiffEditorSide side)
    {
        MenuItem Item(string msgid, string? gesture, Func<Task> action)
        {
            var item = new MenuItem { Header = UiText.Menu(msgid), Tag = msgid, InputGesture = gesture is null ? null : KeyGesture.Parse(gesture) };
            item.Click += async (_, _) => { _activeLineBookmarkSide = side.SourceSide; await GuardAsync(action); };
            return item;
        }
        MenuItem Command(string header, string? gesture, FrameCommand command) => Item(header, gesture, () => ExecuteFrameCommandAsync(command));
        MenuItem MenuFilters()
        {
            var filters = new MenuItem { Header = UiText.Menu("Add to &Filters"), Tag = "Add to &Filters" };
            filters.Items.Add(Command("Add to &Display Filter", null, FrameCommand.AddToDisplayFilters));
            filters.Items.Add(Command("Add to &Line Filters", null, FrameCommand.AddToLineFilters));
            filters.Items.Add(Command("Add to Substitution &Filters", null, FrameCommand.AddToSubstitutionFilters));
            return filters;
        }
        MenuItem MenuPopupOpen()
        {
            var open = new MenuItem { Header = UiText.Menu("Op&en"), Tag = "Op&en" };
            open.Items.Add(Command("With &Registered Application", null, FrameCommand.OpenRegistered));
            open.Items.Add(Command("With &External Editor\tCtrl+Alt+E", null, FrameCommand.OpenExternalEditor));
            open.Items.Add(Command("Open &Parent Folder...", null, FrameCommand.OpenParentFolder));
            return open;
        }
        MenuItem DiffCopy(string msgid, int ordinal, List<MenuItem>? group)
        {
            var item = Item(msgid, null, () => CopySelectedDifferenceAsync(ordinal));
            group?.Add(item);
            return item;
        }
        var linesToMiddle = Command("Copy Selected Lines to Middle", null, FrameCommand.CopyLinesToMiddle);
        _copyLinesToMiddleItems.Add(linesToMiddle);
        var menu = new ContextMenu
        {
            Items =
            {
                Command("Copy Selected Lines to Right", null, FrameCommand.CopyLinesToRight),
                Command("Copy Selected Lines to Left", null, FrameCommand.CopyLinesToLeft),
                linesToMiddle,
                new Separator(),
                DiffCopy("Copy Selected Diff (Left) to Clipboard\tCtrl+&1", 0, null),
                DiffCopy("Copy Selected Diff (Right) to Clipboard\tCtrl+&2", 1, _copyDiffSecondItems),
                DiffCopy("Copy Selected Diff (Middle) to Clipboard\tCtrl+&2", 1, _copyDiffMiddleItems),
                DiffCopy("Copy Selected Diff (Right) to Clipboard\tCtrl+&3", 2, _copyDiffThirdItems),
                new Separator(),
                Command("Select Line &Difference\tF4", "F4", FrameCommand.SelectLineDifference),
                MenuFilters(),
                new Separator(),
                Command("&Undo\tCtrl+Z", null, FrameCommand.Undo),
                Command("&Redo\tCtrl+Y", null, FrameCommand.Redo),
                new Separator(),
                Item("Cu&t\tCtrl+X", null, () => CopyDiffSelectionAsync(cut: true)),
                Item("&Copy\tCtrl+C", null, () => CopyDiffSelectionAsync(cut: false)),
                Item("&Paste\tCtrl+V", null, PasteDiffTextAsync),
                new Separator(),
                new MenuItem
                {
                    Header = UiText.Menu("&Scripts"),
                    Tag = "&Scripts",
                    Items = { new MenuItem { Header = UiText.Menu("< Empty >"), IsEnabled = false, Tag = "< Empty >" } },
                },
                new Separator(),
                Command("&Go to...\tCtrl+G", "Ctrl+G", FrameCommand.GoTo),
                Command("Go to &Definition\tF12", "F12", FrameCommand.GoToDefinition),
                MenuPopupOpen(),
            },
        };
        // 右クリックしたペインを対象にする（本家は右クリックしたビューのメニューを出す）。
        menu.Opening += (_, _) => { _activeLineBookmarkSide = side.SourceSide; UpdateSelectedDiffCopyMenu(); };
        side.View.ContextMenu = menu;
        UpdateSelectedDiffCopyMenu();
    }

    /// <summary>headless検証用: その側の本文の右クリックメニュー。</summary>
    internal ContextMenu? DiffEditorContextMenu(int side) => _diffViews[side].View.ContextMenu;
}
