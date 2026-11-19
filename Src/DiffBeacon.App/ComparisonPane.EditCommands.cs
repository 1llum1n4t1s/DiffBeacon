using Avalonia.Controls;
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
        var menu = new ContextMenu
        {
            Items =
            {
                Command("Select Line &Difference\tF4", "F4", FrameCommand.SelectLineDifference),
                new Separator(),
                Command("&Undo\tCtrl+Z", null, FrameCommand.Undo),
                Command("&Redo\tCtrl+Y", null, FrameCommand.Redo),
                new Separator(),
                Item("Cu&t\tCtrl+X", null, () => CopyDiffSelectionAsync(cut: true)),
                Item("&Copy\tCtrl+C", null, () => CopyDiffSelectionAsync(cut: false)),
                Item("&Paste\tCtrl+V", null, PasteDiffTextAsync),
                new Separator(),
                Command("&Go to...\tCtrl+G", "Ctrl+G", FrameCommand.GoTo),
            },
        };
        // 右クリックしたペインを対象にする（本家は右クリックしたビューのメニューを出す）。
        menu.Opening += (_, _) => _activeLineBookmarkSide = side.SourceSide;
        side.View.ContextMenu = menu;
    }

    /// <summary>headless検証用: その側の本文の右クリックメニュー。</summary>
    internal ContextMenu? DiffEditorContextMenu(int side) => _diffViews[side].View.ContextMenu;
}
