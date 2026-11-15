using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMerge風の「検索」「置換」「移動」ダイアログと F3 系の続けての検索（ComparisonPane.FindReplace.cs）の自己検証。
/// 期待する本文・選択・行は、実装の関数を使わずに文字列と数で書いた値と照合する。
/// - 検索はゴースト行を飛ばし、ゴースト行へ入力した行（目印の文字が残る行）でも目印を含まずに選ぶこと。
/// - 折り返し・折り返さない・前を検索・単語単位・大文字小文字・正規表現（誤った式・長さ0の一致）・閉じない設定・F3／Shift+F3／Ctrl+F3。
/// - 置換は改行（CRLF・LF・CR）と末尾改行を保ち、「すべて置換」は二重に置き換えず、一回の元に戻すで元の本文へ戻ること。
/// - 読取り専用の側・祖先は置き換えないこと。選択範囲だけの置換。
/// - 移動の範囲表示・ボタンの可否・行と差異への移動。
/// </summary>
internal static class HeadlessFindReplaceChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, string artifactOutput,
        Action<string, bool, string> check, Action<string> screenshot, Action<Task> pump)
    {
        window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs();
        var work = Path.Combine(output, "find-replace"); Directory.CreateDirectory(work);
        var menu = window.FrameMenu!.Items.OfType<MenuItem>().First(item => item.Header as string == "編集(_E)");
        var headers = menu.Items.OfType<MenuItem>().Select(item => item.Header as string ?? "").ToArray();
        check("find menu lists find, replace and go to like WinMerge", headers.Contains("検索(_I)...") && headers.Contains("置換(_C)...") && headers.Contains("移動(_G)..."),
            string.Join("|", headers));

        // 左は CRLF・末尾改行なし。右にだけ「inserted」の行があるので、左の2行目はゴースト行になる。
        var leftOriginal = "one alpha\r\nAlpha two\r\nthree alphabet\r\nalpha";
        var rightOriginal = "one alpha\ninserted\nAlpha two\nthree alphabet\nalpha";
        Compare(pane, work, "a", leftOriginal, null, rightOriginal, pump);
        var left = pane.DiffEditor(0); var right = pane.DiffEditor(2);
        check("find fixture has a ghost line on the left", pane.DiffEditorGhostLineCount(0) == 1 && left.Document.LineCount == 5, $"{pane.DiffEditorGhostLineCount(0)} {left.Document.LineCount}");

        TextSearchDialog? shown = null;
        TextSearchDialog.Shown = dialog => shown = dialog;
        try
        {
            // Ctrl+F: キャレットの位置の単語が検索する文字列になる。閉じない設定が既定（本家 FIND_NO_CLOSE）。
            Caret(pane, 0, 6);
            Press(window, left, Key.F, KeyModifiers.Control);
            var find = shown;
            check("find Ctrl+F opens the find dialog with the word at the caret", find is { ReplaceMode: false, Title: "検索" } && find.FindText.Text == "alpha"
                && find.NoClose.IsChecked == true && find.WholeWord.IsEnabled && find.FindNextButton.IsEnabled,
                $"{find?.Title} {find?.FindText.Text} noClose={find?.NoClose.IsChecked}");
            if (find is null) return;
            SaveDialog(find, artifactOutput, "find-dialog.png");

            // 次を検索: ゴースト行を飛ばし、大文字小文字を区別しないので Alpha・alphabet の alpha・最終行・折り返して1行目の順。
            var steps = new List<string>();
            for (var n = 0; n < 4; n++) { Click(find.FindNextButton); steps.Add($"{left.TextArea.Caret.Line}:{pane.DiffEditorSelectedText(0)}"); }
            check("find next skips the ghost line and wraps to the top", steps.SequenceEqual(["3:Alpha", "4:alpha", "5:alpha", "1:alpha"]), string.Join(",", steps));
            Click(find.FindPreviousButton);
            check("find previous wraps from the top to the last match", $"{left.TextArea.Caret.Line}:{pane.DiffEditorSelectedText(0)}" == "5:alpha",
                $"{left.TextArea.Caret.Line}:{pane.DiffEditorSelectedText(0)}");

            // 単語単位: alphabet の中の alpha を飛ばす。
            find.WholeWord.IsChecked = true; Caret(pane, 0, 0); steps.Clear();
            for (var n = 0; n < 4; n++) { Click(find.FindNextButton); steps.Add(left.TextArea.Caret.Line.ToString()); }
            check("find whole word skips alphabet", steps.SequenceEqual(["1", "3", "5", "1"]), string.Join(",", steps));
            // 大文字と小文字を区別する: Alpha だけ。
            find.WholeWord.IsChecked = false; find.MatchCase.IsChecked = true; find.FindText.Text = "Alpha"; Caret(pane, 0, 0); steps.Clear();
            for (var n = 0; n < 2; n++) { Click(find.FindNextButton); steps.Add($"{left.TextArea.Caret.Line}:{pane.DiffEditorSelectedText(0)}"); }
            check("find match case finds only the capitalized word", steps.SequenceEqual(["3:Alpha", "3:Alpha"]), string.Join(",", steps));
            // 最後まで検索した後ループしない: 末尾から先は見つからず、選択はそのまま。
            find.MatchCase.IsChecked = false; find.NoWrap.IsChecked = true; find.FindText.Text = "alpha";
            Caret(pane, 0, left.Document.TextLength);
            var noWrap = find.Run(TextSearchAction.FindNext);
            check("find no wrap reports not found at the end", !noWrap && pane.ComparisonStatus == "文字列 \"alpha\" が見つかりません。" && pane.DiffEditorSelectedText(0) == "",
                $"{noWrap} {pane.ComparisonStatus}");
            find.NoWrap.IsChecked = false;

            // 正規表現: 単語単位は使えなくなる。行頭からの一致、誤った式、長さ0の一致。
            find.WholeWord.IsChecked = true; find.Regex.IsChecked = true; Dispatcher.UIThread.RunJobs();
            check("find regular expression disables whole word", find.WholeWord.IsChecked == false && !find.WholeWord.IsEnabled, $"{find.WholeWord.IsChecked} {find.WholeWord.IsEnabled}");
            find.FindText.Text = @"^a\w+$"; Caret(pane, 0, 0); Click(find.FindNextButton);
            check("find regular expression matches a whole line", $"{left.TextArea.Caret.Line}:{pane.DiffEditorSelectedText(0)}" == "5:alpha",
                $"{left.TextArea.Caret.Line}:{pane.DiffEditorSelectedText(0)}");
            find.FindText.Text = "("; var invalid = find.Run(TextSearchAction.FindNext);
            check("find invalid regular expression is reported", !invalid && (pane.ComparisonStatus ?? "").StartsWith("正規表現が正しくありません", StringComparison.Ordinal), pane.ComparisonStatus ?? "");
            find.FindText.Text = "^"; Caret(pane, 0, 0); steps.Clear();
            for (var n = 0; n < 2; n++) { Click(find.FindNextButton); steps.Add($"{left.TextArea.Caret.Line}:{left.SelectionLength}"); }
            check("find zero-length match moves on instead of stopping", steps.SequenceEqual(["1:0", "3:0"]), string.Join(",", steps));

            // 閉じない設定を外すと、見つけた時点で閉じる。
            find.Regex.IsChecked = false; find.NoClose.IsChecked = false; find.FindText.Text = "two"; Caret(pane, 0, 0);
            Click(find.FindNextButton); Dispatcher.UIThread.RunJobs();
            check("find closes after a hit when keep open is off", pane.TextSearchDialogForTest is null && pane.DiffEditorSelectedText(0) == "two",
                $"{pane.TextSearchDialogForTest is null} {pane.DiffEditorSelectedText(0)}");

            // F3 は前回の条件で次、Shift+F3 は前。Ctrl+F3 はキャレットの単語を探す。
            Caret(pane, 0, 0); Press(window, left, Key.F3, KeyModifiers.None);
            var f3 = $"{left.TextArea.Caret.Line}:{pane.DiffEditorSelectedText(0)}";
            Caret(pane, 0, 0); Press(window, left, Key.F3, KeyModifiers.Shift);
            var shiftF3 = $"{left.TextArea.Caret.Line}:{pane.DiffEditorSelectedText(0)}";
            Caret(pane, 0, left.Document.GetLineByNumber(4).Offset + 1); Press(window, left, Key.F3, KeyModifiers.Control);
            var ctrlF3 = $"{left.TextArea.Caret.Line}:{pane.DiffEditorSelectedText(0)}";
            check("find F3, Shift+F3 and Ctrl+F3 repeat the search", f3 == "3:two" && shiftF3 == "3:two" && ctrlF3 == "4:three", $"{f3} {shiftF3} {ctrlF3}");

            // ゴースト行へ入力した直後（再比較の前）でも、目印を含まずに選び、置き換えた本文に目印が入らない。
            var ghost = left.Document.GetLineByNumber(2);
            // 直前の Ctrl+F3 で選んだ文字列を上書きしないよう、選択を外してからゴースト行の末尾へ入力する。
            left.TextArea.ClearSelection(); left.TextArea.Caret.Offset = ghost.EndOffset; left.TextArea.PerformTextInput("zeta alpha"); Dispatcher.UIThread.RunJobs();
            var typed = "one alpha\r\nzeta alpha\r\nAlpha two\r\nthree alphabet\r\nalpha";
            var typedOk = pane.LeftEditor.Text == typed && pane.DiffEditRecomparePending;
            Caret(pane, 0, 0);
            var zeta = new TextSearchSettings("zeta", false, false, false, false, true);
            var foundTyped = pane.FindText(zeta, backward: false, 0);
            check("find on a line typed into a ghost line selects only the match", typedOk && foundTyped && pane.DiffEditorSelectedText(0) == "zeta" && left.TextArea.Caret.Line == 2,
                $"{typedOk} {foundTyped} [{Escape(pane.DiffEditorSelectedText(0))}] {left.TextArea.Caret.Line} text={Escape(pane.LeftEditor.Text)} pending={pane.DiffEditRecomparePending} doc={Escape(left.Document.Text)}");
            var replacedTyped = pane.ReplaceAllText(zeta, "eta", 0, false);
            check("find replace all on the typed line keeps CRLF and no marks", replacedTyped && pane.LeftEditor.Text == "one alpha\r\neta alpha\r\nAlpha two\r\nthree alphabet\r\nalpha"
                && pane.ComparisonStatus == "1 個の文字列を置換しました。", $"{Escape(pane.LeftEditor.Text)} {pane.ComparisonStatus}");
            pane.UndoText(); Dispatcher.UIThread.RunJobs();
            check("find undo of replace all restores the typed text", pane.LeftEditor.Text == typed, Escape(pane.LeftEditor.Text));

            // 置換ダイアログ（右）: 一回目の「置換して次に」は一致を選ぶだけ、二回目で置き換えて次を選ぶ。
            shown = null; Caret(pane, 2, 0);
            Press(window, right, Key.H, KeyModifiers.Control);
            var replace = shown;
            check("find Ctrl+H opens the replace dialog without a selection scope", replace is { ReplaceMode: true, Title: "置換" } && !replace.ScopeSelection.IsEnabled
                && replace.ScopeWholeFile.IsChecked == true, $"{replace?.Title} {replace?.ScopeSelection.IsEnabled}");
            if (replace is null) return;
            replace.FindText.Text = "alpha"; replace.ReplaceText.Text = "beta"; Dispatcher.UIThread.RunJobs();
            SaveDialog(replace, artifactOutput, "replace-dialog.png");
            var firstRun = replace.Run(TextSearchAction.Replace);
            var firstDetail = $"run={firstRun} status={pane.ComparisonStatus} sel=[{pane.DiffEditorSelectedText(2)}] line={right.TextArea.Caret.Line} offset={right.TextArea.Caret.Offset}";
            var firstPress = pane.RightEditor.Text == rightOriginal && pane.DiffEditorSelectedText(2) == "alpha" && right.TextArea.Caret.Line == 1;
            if (!firstPress) check("find replace first press selects the first match", false, firstDetail);
            Click(replace.ReplaceButton);
            var afterOne = "one beta\ninserted\nAlpha two\nthree alphabet\nalpha";
            check("find replace selects first, then replaces and selects the next", firstPress && pane.RightEditor.Text == afterOne && pane.DiffEditorSelectedText(2) == "Alpha",
                $"{firstPress} {Escape(pane.RightEditor.Text)} [{pane.DiffEditorSelectedText(2)}] status={pane.ComparisonStatus} readOnly={right.IsReadOnly} enabled={replace.ReplaceButton.IsEnabled} find={replace.FindText.Text}");
            Click(replace.ReplaceAllButton);
            var afterAll = "one beta\ninserted\nbeta two\nthree betabet\nbeta";
            check("find replace all reports the count", pane.RightEditor.Text == afterAll && pane.ComparisonStatus == "3 個の文字列を置換しました。",
                $"{Escape(pane.RightEditor.Text)} {pane.ComparisonStatus}");
            pane.UndoText(); var undoAll = pane.RightEditor.Text; pane.UndoText(); var undoOne = pane.RightEditor.Text;
            pane.RedoText(); pane.RedoText(); Dispatcher.UIThread.RunJobs();
            check("find one undo per replace and replace all, then redo", undoAll == afterOne && undoOne == rightOriginal && pane.RightEditor.Text == afterAll,
                $"{Escape(undoAll)} | {Escape(undoOne)} | {Escape(pane.RightEditor.Text)}");
            replace.Close(); Dispatcher.UIThread.RunJobs();

            // 読取り専用の側は置換ダイアログを開かず、本文を変えない。
            pane.RightEditor.IsReadOnly = true; Dispatcher.UIThread.RunJobs(); shown = null; Caret(pane, 2, 0);
            Press(window, right, Key.H, KeyModifiers.Control);
            var readOnlyRefused = shown is null && pane.ComparisonStatus == "右は読取り専用のため置換できません。";
            var readOnlyAll = pane.ReplaceAllText(new("beta", false, false, false, false, true), "x", 2, false);
            check("find refuses replace on a read-only side", readOnlyRefused && !readOnlyAll && pane.RightEditor.Text == afterAll, $"{readOnlyRefused} {readOnlyAll} {pane.ComparisonStatus}");
            pane.RightEditor.IsReadOnly = false; Dispatcher.UIThread.RunJobs();
        }
        finally { TextSearchDialog.Shown = null; }
        pane.DiscardChanges();

        ReplaceCases(window, window.AddSession(), work, check, pump);
        ThreeWay(window, window.AddSession(), work, check, pump);
        GoTo(window, pane, work, check, screenshot, artifactOutput, pump);
    }

    // 「すべて置換」の置換規則（二重に置き換えない・正規表現の書式・長さ0の一致・CR・選択範囲）。
    private static void ReplaceCases(MainWindow window, ComparisonPane pane, string work, Action<string, bool, string> check, Action<Task> pump)
    {
        Compare(pane, work, "b", "here here\r\nthere\r\n", null, "x\r\n", pump);
        string Run(string text, string pattern, string replacement, bool regex, out bool ok, out string? status)
        {
            pane.LeftEditor.Text = text; pane.CompareEditors(); Dispatcher.UIThread.RunJobs();
            ok = pane.ReplaceAllText(new(pattern, true, false, regex, false, true), replacement, 0, false);
            status = pane.ComparisonStatus;
            return pane.LeftEditor.Text ?? "";
        }
        var there = Run("here here\r\nthere\r\n", "here", "there", false, out var thereOk, out var thereStatus);
        check("find replace all does not replace twice", thereOk && there == "there there\r\ntthere\r\n" && thereStatus == "3 個の文字列を置換しました。", $"{Escape(there)} {thereStatus}");
        var groups = Run("a=1\nb=2\n", @"(\w)=(\w)", @"\2=\U\1", true, out _, out var groupStatus);
        check("find regular expression replacement uses groups and case flags", groups == "1=A\n2=B\n" && groupStatus == "2 個の文字列を置換しました。", $"{Escape(groups)} {groupStatus}");
        var prefix = Run("a\rb\rc", "^", "// ", true, out _, out var prefixStatus);
        var prefixTrailing = Run("a\rb\r", "^", "// ", true, out _, out var trailingStatus);
        check("find zero-length replacement keeps CR line endings", prefix == "// a\r// b\r// c" && prefixStatus == "3 個の文字列を置換しました。"
            && prefixTrailing == "// a\r// b\r" && trailingStatus == "2 個の文字列を置換しました。", $"{Escape(prefix)} {prefixStatus} {Escape(prefixTrailing)} {trailingStatus}");
        var literal = Run("axb", "x", @"$1\n", false, out _, out _);
        check("find literal replacement inserts dollar and backslash as text", literal == @"a$1\nb", Escape(literal));
        var badTemplate = Run("b", "(a)|b", @"\1", true, out var badOk, out var badStatus);
        check("find replacement with an unmatched group changes nothing", !badOk && badTemplate == "b" && badStatus == "置換後の文字列の \\1 に当たるグループが一致していません。", $"{badOk} {badStatus}");

        // 複数行を選んで開くと範囲は「選択範囲」になり、「置換して次に」は押せない。選択の中だけを置き換える。
        pane.LeftEditor.Text = "a\na\na\na"; pane.RightEditor.Text = "a\na\na\na"; pane.CompareEditors(); Dispatcher.UIThread.RunJobs();
        var left = pane.DiffEditor(0);
        left.TextArea.Focus(); left.Select(2, 3); Dispatcher.UIThread.RunJobs();
        TextSearchDialog? shown = null;
        TextSearchDialog.Shown = dialog => shown = dialog;
        try
        {
            Press(window, left, Key.H, KeyModifiers.Control);
            var scoped = shown is { } dialog && dialog.ScopeSelection.IsChecked == true && dialog.ScopeSelection.IsEnabled;
            if (shown is null) { check("find selection scope dialog", false, "no dialog"); return; }
            shown.FindText.Text = "a"; shown.ReplaceText.Text = "b"; Dispatcher.UIThread.RunJobs();
            var replaceDisabled = !shown.ReplaceButton.IsEnabled;
            Click(shown.ReplaceAllButton);
            check("find replace all in the selection only", scoped && replaceDisabled && pane.LeftEditor.Text == "a\nb\nb\na" && pane.ComparisonStatus == "2 個の文字列を置換しました。",
                $"{scoped} {replaceDisabled} {Escape(pane.LeftEditor.Text)} {pane.ComparisonStatus}");
            shown.Close();
        }
        finally { TextSearchDialog.Shown = null; }
        pane.DiscardChanges();
    }

    // 三者比較: 中央（祖先）は検索できるが置き換えない。
    private static void ThreeWay(MainWindow window, ComparisonPane pane, string work, Action<string, bool, string> check, Action<Task> pump)
    {
        Compare(pane, work, "c", "m alpha\nl\n", "m alpha\n", "m alpha\nr\n", pump);
        var middle = pane.DiffEditor(1);
        Caret(pane, 1, 0);
        var settings = new TextSearchSettings("alpha", false, false, false, false, true);
        var found = pane.FindText(settings, backward: false, 1);
        var replaced = pane.ReplaceAllText(settings, "x", 1, false);
        check("find in the ancestor pane selects but refuses to replace", found && pane.DiffEditorSelectedText(1) == "alpha" && !replaced
            && pane.ComparisonStatus == "中央は読取り専用のため置換できません。" && pane.MiddleEditor.Text == "m alpha\n",
            $"{found} [{pane.DiffEditorSelectedText(1)}] {replaced} {pane.ComparisonStatus}");
        pane.DiscardChanges();
    }

    // 移動: 範囲の表示、範囲外で押せないこと、行と差異への移動。
    private static void GoTo(MainWindow window, ComparisonPane pane, string work, Action<string, bool, string> check, Action<string> screenshot, string artifactOutput, Action<Task> pump)
    {
        window.SelectSession(window.SessionPanes.ToList().IndexOf(pane)); Dispatcher.UIThread.RunJobs();
        Compare(pane, work, "d", "one\r\ntwo\r\nthree\r\nfour", null, "one\ninserted\ntwo\nthree\nfour\n", pump);
        var left = pane.DiffEditor(0); var right = pane.DiffEditor(2);
        Caret(pane, 0, left.Document.GetLineByNumber(3).Offset);
        GoToDialog? shown = null; var states = new List<string>();
        GoToDialog.Shown = dialog =>
        {
            shown = dialog;
            states.Add($"{dialog.Number.Text}{dialog.Range.Text}{dialog.GoButton.IsEnabled}{dialog.Middle.IsEnabled}");
            dialog.Right.IsChecked = true; Dispatcher.UIThread.RunJobs(); states.Add(dialog.Range.Text ?? "");
            dialog.Number.Text = "0"; Dispatcher.UIThread.RunJobs(); states.Add(dialog.GoButton.IsEnabled.ToString());
            dialog.Number.Text = "7"; Dispatcher.UIThread.RunJobs(); states.Add(dialog.GoButton.IsEnabled.ToString());
            dialog.Difference.IsChecked = true; Dispatcher.UIThread.RunJobs(); states.Add(dialog.Range.Text ?? "");
            dialog.Line.IsChecked = true; dialog.Number.Text = "4"; Dispatcher.UIThread.RunJobs(); states.Add(dialog.GoButton.IsEnabled.ToString());
            SaveDialog(dialog, artifactOutput, "goto-dialog.png");
            Click(dialog.GoButton);
        };
        try
        {
            // 左のキャレットは3行目（表示ではゴースト行の下の two）。左は4行、右は改行で終わるので6行（本家と同じ数え方）。差異は inserted の行と末尾の改行の2つ。
            Press(window, left, Key.G, KeyModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            check("go to dialog shows the range and enables the button only in range", shown is not null
                && states.SequenceEqual(["2(1-4)TrueFalse", "(1-6)", "False", "False", "(1-2)", "True"]), string.Join(",", states));
            check("go to moves to the line of the chosen side", right.TextArea.Caret.Line == 4, right.TextArea.Caret.Line.ToString());
            pane.ApplyGoTo(new GoToDialog.Choice(0, true, 1)); Dispatcher.UIThread.RunJobs();
            var block = pane.CurrentDiff!.Blocks[0];
            check("go to moves to the chosen difference", pane.DiffList.SelectedIndex == block.RowStart, $"{pane.DiffList.SelectedIndex}/{block.RowStart}");
        }
        finally { GoToDialog.Shown = null; }
        screenshot("find-goto-window.png");
        pane.DiscardChanges();
    }

    private static void Compare(ComparisonPane pane, string work, string name, string left, string? middle, string right, Action<Task> pump)
    {
        var leftPath = Path.Combine(work, name + "-left.txt"); var rightPath = Path.Combine(work, name + "-right.txt");
        File.WriteAllText(leftPath, left, new UTF8Encoding(false)); File.WriteAllText(rightPath, right, new UTF8Encoding(false));
        var basePath = "";
        if (middle is not null) { basePath = Path.Combine(work, name + "-base.txt"); File.WriteAllText(basePath, middle, new UTF8Encoding(false)); }
        pane.LeftPath.Text = leftPath; pane.BasePath.Text = basePath; pane.RightPath.Text = rightPath;
        pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
    }

    // その側を検索・置換の対象（フォーカスのある側）にして、キャレットを置く。headless では別のウィンドウ（ダイアログ）を
    // 開いた後にフォーカスの移動が届かないことがあるので、ペインの入口でその側を明示的に選ぶ。
    private static void Caret(ComparisonPane pane, int side, int offset)
    {
        var view = pane.DiffEditor(side);
        pane.FocusDiffEditorRealLine(side, 0);
        view.TextArea.Focus();
        view.TextArea.ClearSelection();
        view.TextArea.Caret.Offset = offset;
        Dispatcher.UIThread.RunJobs();
    }

    // 外枠のキー（トンネル段階）を、フォーカスのあるエディターから押したのと同じ経路で送る。
    private static void Press(MainWindow window, AvaloniaEdit.TextEditor view, Key key, KeyModifiers modifiers)
    {
        if (!view.TextArea.IsFocused) view.TextArea.Focus();
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void SaveDialog(Window dialog, string artifactOutput, string name)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("ダイアログの描画がありません。");
        frame.Save(Path.Combine(artifactOutput, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static string Escape(string? text) => (text ?? "<null>").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\u200B", "<ZW>").Replace("\u200C", "<ZWN>");
}
