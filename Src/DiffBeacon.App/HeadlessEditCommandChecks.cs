using System.Text;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Editing;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMerge風の「編集」メニューの切り取り・コピー・貼り付け・すべて選択、ブックマークの小メニューと行番号の欄の印、
/// 本文の右クリックメニュー（ComparisonPane.EditCommands.cs）の自己検証。
/// クリップボードは検証用の差し替え（<see cref="ComparisonPane.DiffClipboardBridge"/>）を使い、利用者のクリップボードを置き換えない。
/// 期待する本文・クリップボードの文字列は、実装の関数を使わずに文字列で書いた値と照合する。
/// </summary>
internal static class HeadlessEditCommandChecks
{
    private sealed class MemoryClipboard : IEditorClipboard
    {
        internal string? Text { get; set; }
        internal TaskCompletionSource<string?>? PendingRead { get; set; }
        public Task<string?> ReadTextAsync() => PendingRead?.Task ?? Task.FromResult(Text);
        public Task WriteTextAsync(string text) { Text = text; return Task.CompletedTask; }
    }

    internal static void Run(MainWindow window, ComparisonPane pane, string output, string artifactOutput,
        Action<string, bool, string> check, Action<string> screenshot, Action<Task> pump)
    {
        window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs();
        var work = Path.Combine(output, "edit-commands"); Directory.CreateDirectory(work);
        var menu = window.FrameMenu!.Items.OfType<MenuItem>().First(item => item.Header as string == "編集(_E)");
        var headers = menu.Items.OfType<MenuItem>().Select(item => item.Header as string ?? "").ToArray();
        check("edit menu lists the WinMerge edit commands", headers.SequenceEqual(["元に戻す(_U)", "やり直し(_R)", "切り取り(_T)", "コピー(_C)", "貼り付け(_P)", "行内差異を選択(_D)",
            "すべて選択(_A)", "検索(_I)...", "置換(_C)...", "マーカー(_M)...", "高度な操作", "ブックマーク(_B)", "移動(_G)...", "設定(_O)..."]), string.Join("|", headers));
        var advanced = menu.Items.OfType<MenuItem>().First(item => item.Header as string == "高度な操作");
        var advancedHeaders = advanced.Items.OfType<MenuItem>().Select(item => item.Header as string ?? "").ToArray();
        check("edit advanced submenu copies with line numbers", advancedHeaders.SequenceEqual(["行番号付きでコピー(_C)"]), string.Join("|", advancedHeaders));
        var bookmarkMenu = menu.Items.OfType<MenuItem>().First(item => item.Header as string == "ブックマーク(_B)");
        var bookmarkHeaders = bookmarkMenu.Items.OfType<MenuItem>().Select(item => item.Header as string ?? "").ToArray();
        check("edit bookmark submenu matches WinMerge", bookmarkHeaders.SequenceEqual(["ブックマークの作成・削除(_T)", "次のブックマークへ移動(_N)", "前のブックマークへ移動(_P)", "すべてのブックマークをクリア(_C)"]),
            string.Join("|", bookmarkHeaders));

        // 左は CRLF・末尾改行なし、右は LF・末尾改行あり。右にだけ extra の行があるので、左の2行目はゴースト行。
        var leftOriginal = "one\r\ntwo\r\nthree";
        var rightOriginal = "one\nextra\ntwo\nthree\n";
        Compare(pane, work, leftOriginal, rightOriginal, pump);
        var left = pane.DiffEditor(0); var right = pane.DiffEditor(2);
        var clipboard = new MemoryClipboard();
        pane.DiffClipboardBridge = clipboard;
        try
        {
            check("edit fixture has a ghost line on the left", pane.DiffEditorGhostLineCount(0) == 1, pane.DiffEditorGhostLineCount(0).ToString());

            // コピー: ゴースト行をまたいで選んでも、本物の本文だけを渡す（本家 GetTextWithoutEmptys）。
            Select(pane, 0, 0, left.Document.GetLineByNumber(3).EndOffset);
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.Copy));
            var menuCopy = clipboard.Text;
            clipboard.Text = null;
            Select(pane, 0, 0, left.Document.GetLineByNumber(3).EndOffset);
            EditorKey(left, Key.C, KeyModifiers.Control);
            check("edit copy across a ghost line copies only the real text", menuCopy == "one\r\ntwo" && clipboard.Text == "one\r\ntwo" && pane.LeftEditor.Text == leftOriginal,
                $"menu={Escape(menuCopy)} key={Escape(clipboard.Text)}");

            // 切り取り: 右の extra の行を切り取り、一回の元に戻すで戻る。
            var extra = right.Document.GetLineByNumber(2);
            Select(pane, 2, extra.Offset, extra.TotalLength);
            EditorKey(right, Key.X, KeyModifiers.Control);
            var afterCut = pane.RightEditor.Text;
            pane.UndoText(); Dispatcher.UIThread.RunJobs();
            check("edit cut removes the selection and one undo restores it", clipboard.Text == "extra\n" && afterCut == "one\ntwo\nthree\n" && pane.RightEditor.Text == rightOriginal,
                $"clip={Escape(clipboard.Text)} cut={Escape(afterCut)} undo={Escape(pane.RightEditor.Text)}");

            // 貼り付け: 左（CRLF）の one の行末へ LF の文字列を貼ると、改行はその行の CRLF にそろう。目印の文字は入れない。
            clipboard.Text = "A\nB\u200B";
            Select(pane, 0, left.Document.GetLineByNumber(1).EndOffset, 0);
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.Paste));
            var afterPaste = pane.LeftEditor.Text;
            pane.UndoText(); Dispatcher.UIThread.RunJobs();
            check("edit paste converts line breaks to the line's ending and strips marks", afterPaste == "oneA\r\nB\r\ntwo\r\nthree" && pane.LeftEditor.Text == leftOriginal,
                $"{Escape(afterPaste)} undo={Escape(pane.LeftEditor.Text)}");

            // クリップボードを待つ間に本文が変わったら、貼り付けない。
            var pending = new TaskCompletionSource<string?>();
            clipboard.PendingRead = pending;
            Select(pane, 0, 0, 0);
            var paste = pane.PasteDiffTextAsync();
            pane.LeftEditor.Text = "changed"; Dispatcher.UIThread.RunJobs();
            pending.SetResult("X"); pump(paste); clipboard.PendingRead = null;
            check("edit paste is dropped when the text changed while reading the clipboard", pane.LeftEditor.Text == "changed"
                && pane.ComparisonStatus == "本文または選択が変わったため貼り付けませんでした。", $"{Escape(pane.LeftEditor.Text)} {pane.ComparisonStatus}");
            pane.LeftEditor.Text = leftOriginal; pane.CompareEditors(); Dispatcher.UIThread.RunJobs();

            // 読取り専用の側は切り取り・貼り付けを断り、コピーはできる。
            pane.RightEditor.IsReadOnly = true; Dispatcher.UIThread.RunJobs();
            extra = right.Document.GetLineByNumber(2);
            Select(pane, 2, extra.Offset, extra.Length);
            clipboard.Text = null;
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.Cut));
            var cutStatus = pane.ComparisonStatus;
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.Copy));
            check("edit read-only side refuses cut but allows copy", cutStatus == "右は読取り専用のため切り取れません。" && clipboard.Text == "extra" && pane.RightEditor.Text == rightOriginal,
                $"{cutStatus} clip={Escape(clipboard.Text)}");
            pane.RightEditor.IsReadOnly = false; Dispatcher.UIThread.RunJobs();

            // すべて選択。
            Select(pane, 2, 0, 0);
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.SelectAll));
            check("edit select all selects the whole pane", right.SelectionStart == 0 && right.SelectionLength == right.Document.TextLength, $"{right.SelectionStart}+{right.SelectionLength}/{right.Document.TextLength}");

            // 右クリックメニュー: 本家 IDR_POPUP_MERGEVIEW の対応する項目。コピーは開いたペインの選択を写す。
            var context = pane.DiffEditorContextMenu(2);
            var contextHeaders = context?.Items.OfType<MenuItem>().Where(item => item.IsVisible).Select(item => item.Header as string ?? "").ToArray() ?? [];
            Select(pane, 2, right.Document.GetLineByNumber(4).Offset, 5);
            clipboard.Text = null;
            var copyItem = context?.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header as string == "コピー(_C)");
            copyItem?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
            check("edit context menu lists WinMerge items and copies", contextHeaders.SequenceEqual(["選択した行を右側にコピー", "選択した行を左側にコピー", "選択した差異(左側)をクリップボードへコピー", "選択した差異(右側)をクリップボードへコピー", "行内差異を選択(_D)", "フィルターに追加(_F)", "元に戻す(_U)", "やり直し(_R)", "切り取り(_T)", "コピー(_C)", "貼り付け(_P)", "スクリプト(_S)", "移動(_G)...", "定義へ移動(_D)", "開く(_E)"])
                && clipboard.Text == "three", $"{string.Join("|", contextHeaders)} clip={Escape(clipboard.Text)}");

            // 行番号付きコピー: 左のゴースト行を飛ばし、本物の行番号と改行を残す。
            Select(pane, 0, 0, left.Document.TextLength);
            clipboard.Text = null;
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.CopyWithLineNumbers));
            var numbered = clipboard.Text;
            clipboard.Text = null;
            Press(window, left, Key.C, KeyModifiers.Control | KeyModifiers.Shift);
            check("edit copy with line numbers skips ghost lines", numbered == "1: one\r\n2: two\r\n3: three" && clipboard.Text == numbered,
                $"menu={Escape(numbered)} key={Escape(clipboard.Text)}");
            Select(pane, 0, 0, 0);
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.CopyWithLineNumbers));
            check("edit copy with line numbers needs a selection", pane.ComparisonStatus == "選択がありません。", pane.ComparisonStatus ?? "");

            // 選択した差異: 右だけの extra を、左は改行だけ、右は本文と改行でコピーする。
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.NextDifference));
            clipboard.Text = null;
            Press(window, left, Key.D1, KeyModifiers.Control);
            var leftDiff = clipboard.Text;
            clipboard.Text = null;
            Press(window, right, Key.D2, KeyModifiers.Control);
            check("edit copy selected difference uses ctrl+1 and ctrl+2", leftDiff == "\r\n" && clipboard.Text == "extra\n",
                $"left={Escape(leftDiff)} right={Escape(clipboard.Text)}");
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.CopySelectedDifference3));
            check("edit copy selected difference ctrl+3 is only for three panes", pane.ComparisonStatus == "その側は比較にありません。", pane.ComparisonStatus ?? "");

            // 右の extra の行を左へコピーする。
            var extraLine = right.Document.GetLineByNumber(2);
            Select(pane, 2, extraLine.Offset, extraLine.Length);
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.CopyLinesToLeft));
            check("edit copy selected line inserts the right-only line on the left", pane.LeftEditor.Text == "one\r\nextra\ntwo\r\nthree", Escape(pane.LeftEditor.Text));
            pane.UndoText(); Dispatcher.UIThread.RunJobs();

            Select(pane, 2, right.Document.GetLineByNumber(2).Offset, 5);
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.AddToLineFilters));
            check("edit add to line filter escapes and stores the selection", pane.LineFilterPattern == "extra", pane.LineFilterPattern);
            pane.LineFilterPattern = "";
            pane.CompareEditors();

            Select(pane, 2, right.Document.GetLineByNumber(2).Offset, 5);
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.AddToDisplayFilters));
            check("edit display filter keeps only lines containing the selection", pane.DisplayFilter == "Line contains \"extra\"", pane.DisplayFilter);

            var launched = new List<string>();
            pane.ExternalLauncher = launched.Add;
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.OpenRegistered));
            check("edit open registered application launches the focused file", launched.Count == 1 && launched[0] == pane.RightPath.Text, string.Join("|", launched));
            window.ExternalEditorCommand = "editor $file --line $linenum";
            launched.Clear();
            pane.FocusDiffEditorRealLine(0, 1);
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.OpenExternalEditor));
            var editorPath = pane.LeftPath.Text ?? "";
            var editorCommand = launched.FirstOrDefault() ?? "";
            check("edit external editor uses the real line after a ghost row", launched.Count == 1 && editorCommand.Contains("\"" + editorPath + "\"", StringComparison.Ordinal) && editorCommand.Contains("--line 2", StringComparison.Ordinal), editorCommand);
            check("edit external editor keeps a suffix inside the quotes", ComparisonPane.ExpandEditorCommand("code -g $file:$linenum", editorPath, 12) == "code -g \"" + editorPath + ":12\"", ComparisonPane.ExpandEditorCommand("code -g $file:$linenum", editorPath, 12));
            pane.ExternalLauncher = null;
            window.ExternalEditorCommand = "";

            Select(pane, 2, right.Document.GetLineByNumber(2).Offset, 5);
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.AddMarker));
            check("edit marker records the selection", pane.TextMarkers.Count == 1 && pane.TextMarkers[0] == "extra" && pane.DiffEditor(0).Options.EnableRectangularSelection,
                string.Join("|", pane.TextMarkers));
            pane.ClearTextMarkers();

            pane.FocusDiffEditorRealLine(0, 0);
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.EolToUnix));
            check("edit convert line endings to LF", pane.LeftEditor.Text == "one\ntwo\nthree", Escape(pane.LeftEditor.Text));
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.EolToWindows));
            check("edit convert line endings back to CRLF", pane.LeftEditor.Text == "one\r\ntwo\r\nthree", Escape(pane.LeftEditor.Text));
            pane.SetManualReadOnly(2, true);
            check("edit manual read-only blocks the right side", pane.RightEditor.IsReadOnly, pane.RightEditor.IsReadOnly.ToString());
            pane.SetManualReadOnly(2, false);

            // ブックマーク: Ctrl+F2 で作成・削除、F2／Shift+F2 で次・前（端で回り込む）、すべてクリア。印は行番号の欄に出る。
            pane.FocusDiffEditorRealLine(0, 1); Press(window, left, Key.F2, KeyModifiers.Control);
            pane.FocusDiffEditorRealLine(0, 2); Press(window, left, Key.F2, KeyModifiers.Control);
            var marks = string.Join(",", pane.LineBookmarkMarks(0).Keys.Order());
            pane.FocusDiffEditorRealLine(0, 0);
            Press(window, left, Key.F2, KeyModifiers.None); var next1 = left.TextArea.Caret.Line;
            Press(window, left, Key.F2, KeyModifiers.None); var next2 = left.TextArea.Caret.Line;
            Press(window, left, Key.F2, KeyModifiers.None); var wrapped = left.TextArea.Caret.Line;
            Press(window, left, Key.F2, KeyModifiers.Shift); var previous = left.TextArea.Caret.Line;
            // 表示の行: 1=one、2=ゴースト行、3=two、4=three。
            check("edit bookmarks toggle, move next and previous with wrap", marks == "1,2" && next1 == 3 && next2 == 4 && wrapped == 3 && previous == 4,
                $"marks={marks} next={next1},{next2} wrap={wrapped} prev={previous}");
            screenshot("edit-bookmark-margin.png");
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.ClearAllBookmarks));
            check("edit clear all bookmarks removes the marks", pane.LineBookmarkMarks(0).Count == 0, pane.LineBookmarkMarks(0).Count.ToString());

            window.ToggleMergingModeForTest();
            var before = pane.DiffList.SelectedIndex;
            Press(window, left, Key.Down, KeyModifiers.None);
            check("edit merge mode down arrow moves to the next difference", window.MergingMode && pane.DiffList.SelectedIndex != before, $"{before}->{pane.DiffList.SelectedIndex}");
            window.ToggleMergingModeForTest();
            check("edit merge mode can be turned off", !window.MergingMode, window.MergingMode.ToString());

            pane.SetSideEncoding(0, "utf-8-bom");
            var encoding = pane.SideEncoding(0);
            check("edit encoding keeps the text and records a BOM", encoding.Name == "utf-8" && encoding.Bom && pane.LeftEditor.Text == "one\r\ntwo\r\nthree" && pane.HasUnsavedChanges, $"{encoding.Name} bom={encoding.Bom}");
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.SaveLeft));
            check("edit saved encoding is no longer unsaved", !pane.EncodingUnsaved && pane.SideEncoding(0).Bom, pane.EncodingUnsaved.ToString());
            pane.DiscardChanges();

            clipboard.Text = "from-clipboard";
            pane.FocusDiffEditorRealLine(2, 0);
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.OpenClipboard));
            check("edit open clipboard replaces the focused side", pane.RightEditor.Text == "from-clipboard", Escape(pane.RightEditor.Text));
            pane.SetManualReadOnly(2, true);
            clipboard.Text = "blocked";
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.OpenClipboard));
            check("edit open clipboard refuses a read-only side", pane.RightEditor.Text == "from-clipboard", Escape(pane.RightEditor.Text));
            pane.SetManualReadOnly(2, false);

            var leftFull = Path.GetFullPath(pane.LeftPath.Text ?? "");
            var rightFull = Path.GetFullPath(pane.RightPath.Text ?? "");
            window.RememberRecent(pane.LeftPath.Text, pane.RightPath.Text);
            var recentFolder = Path.Combine(work, "recent-folder");
            Directory.CreateDirectory(recentFolder);
            window.RememberRecent(recentFolder);
            var recent = window.RecentFiles;
            check("edit recent files keeps existing paths", recent.Count is >= 2 and <= 9 && recent[0] == Path.GetFullPath(recentFolder) && recent.Contains(rightFull) && recent.Contains(leftFull), string.Join("|", recent));
            var cancel = pane.OpenView?.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => Equals(button.Content, "キャンセル"));
            var statusLine = pane.OpenView?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Name == "open-view-status");
            check("open view has cancel and a status line", cancel is not null && statusLine?.Text == "ステータス:", statusLine?.Text ?? "");

            pane.DiscardChanges();
            Compare(pane, work, "one\r\ntwo\r\nthree", "one\nextra\ntwo\nthree\nMORE\n", pump);
            var threeLine = pane.DiffEditor(0).Document.GetLineByNumber(4);
            Select(pane, 0, threeLine.Offset, threeLine.Length);
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.CopyLinesToRight));
            var copiedRight = pane.RightEditor.Text ?? "";
            check("edit copy keeps a separator after a line that has no newline", copiedRight.Contains("three\nMORE"), Escape(copiedRight));
            pane.FocusDiffEditorRealLine(0, 1);
            var wordEditor = pane.DiffEditor(0);
            wordEditor.Select(wordEditor.CaretOffset, 0);
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.AddToDisplayFilters));
            check("edit display filter uses the real word at the caret", pane.DisplayFilter.Contains("two", StringComparison.Ordinal), pane.DisplayFilter);
            pane.DiscardChanges();
            Compare(pane, work, "say \"hi\"\n", "say \"hi\"\nx\n", pump);
            var quoted = pane.DiffEditor(0).Document.Text.IndexOf('"');
            Select(pane, 0, quoted, 4);
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.AddToDisplayFilters));
            check("edit display filter keeps a quote inside the phrase", pane.DisplayFilter.Contains("\\\"hi\\\"", StringComparison.Ordinal), pane.DisplayFilter);

            pane.DiscardChanges();
            Compare(pane, work, "alpha\n", "", pump);
            Select(pane, 0, 0, 5);
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.CopyLinesToRight));
            check("edit copy into an empty side inserts the line", pane.RightEditor.Text == "alpha\n", Escape(pane.RightEditor.Text));
            pane.ClearDisplayFilter();
            pane.DiscardChanges();
            Compare(pane, work, "abcd\nefgh\nijkl\n", "abcd\nefgh\nijkl\n", pump);
            var boxed = pane.DiffEditor(0);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            boxed.TextArea.Focus();
            boxed.TextArea.Selection = new RectangleSelection(boxed.TextArea, new TextViewPosition(1, 2), new TextViewPosition(2, 4));
            Dispatcher.UIThread.RunJobs();
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.Cut));
            var clipped = (clipboard.Text ?? "").Replace("\r\n", "\n");
            check("edit rectangle cut removes the columns and copies them", pane.LeftEditor.Text == "ad\neh\nijkl\n" && clipped == "bc\nfg",
                Escape(pane.LeftEditor.Text) + " clip=" + Escape(clipboard.Text));

            var conflict = Path.Combine(work, "sample.conflict");
            File.WriteAllText(conflict, "common\n<<<<<<< mine\nmine-line\n=======\ntheirs-line\n>>>>>>> theirs\ntail\n", new UTF8Encoding(false));
            var fileMenu = window.FrameMenu!.Items.OfType<MenuItem>().First(item => item.Header as string == "ファイル(_F)");
            var fileHeaders = fileMenu.Items.OfType<MenuItem>().Select(item => item.Header as string ?? "").ToArray();
            check("edit file menu opens a conflict file", fileHeaders.Contains("コンフリクト ファイルを開く(_T)..."), string.Join("|", fileHeaders));
            pane.DiscardChanges();
            pump(pane.OpenConflictFileAsync(conflict));
            var opened = File.ReadAllText(conflict);
            check("edit conflict file opens theirs and mine without rewriting the file",
                pane.LeftEditor.Text == "common\ntheirs-line\ntail\n" && pane.RightEditor.Text == "common\nmine-line\ntail\n"
                && pane.LeftEditor.IsReadOnly && !pane.RightEditor.IsReadOnly && pane.RightPath.Text != conflict && pane.ConflictSavePath == conflict && opened.Contains("<<<<<<< mine", StringComparison.Ordinal),
                Escape(pane.LeftEditor.Text) + " / " + Escape(pane.RightEditor.Text));
            pane.RightEditor.Text = "common\nresolved\ntail\n";
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.SaveRight));
            check("edit conflict save overwrites the original file", File.ReadAllText(conflict) == "common\nresolved\ntail\n" && pane.ConflictSavePath is null, Escape(File.ReadAllText(conflict)));

            var three = Path.Combine(work, "three.conflict");
            File.WriteAllText(three, "common\n<<<<<<< mine\nmine-line\n||||||| base\nbase-line\n=======\ntheirs-line\n>>>>>>> theirs\n", new UTF8Encoding(false));
            pane.DiscardChanges();
            pump(pane.OpenConflictFileAsync(three));
            check("edit three-way conflict opens base, theirs, and mine",
                pane.ArchiveAncestorText == "common\nbase-line\n" && pane.LeftEditor.Text == "common\ntheirs-line\n" && pane.RightEditor.Text == "common\nmine-line\n" && pane.LeftEditor.IsReadOnly,
                Escape(pane.ArchiveAncestorText) + " / " + Escape(pane.LeftEditor.Text) + " / " + Escape(pane.RightEditor.Text));
            var readOnlyBoxes = pane.OpenView!.GetVisualDescendants().OfType<CheckBox>().Where(box => box.Name is "open-readonly-left" or "open-readonly-right" or "open-readonly-base").ToArray();
            var leftReadOnly = readOnlyBoxes.First(box => box.Name == "open-readonly-left");
            check("open view has a read-only box for each path", readOnlyBoxes.Length == 3 && leftReadOnly.IsChecked == true && pane.LeftEditor.IsReadOnly, $"{leftReadOnly.IsChecked}");
            leftReadOnly.IsChecked = false;
            check("open view read-only box clears the left side", pane.LeftEditor.IsReadOnly == false && leftReadOnly.IsChecked == false, pane.LeftEditor.IsReadOnly.ToString());
            var bound = pane.RightPath.Text;
            check("edit conflict save stays on the parsed file", pane.ConflictSavePath == three, pane.ConflictSavePath ?? "");
            pane.RightPath.Text = conflict;
            check("edit conflict save target drops when the right path changes", pane.ConflictSavePath is null, pane.ConflictSavePath ?? "");
            pane.RightPath.Text = bound;
        }
        finally { pane.DiffClipboardBridge = null; }
        pane.DiscardChanges();
    }

    private static void Compare(ComparisonPane pane, string work, string left, string right, Action<Task> pump)
    {
        var leftPath = Path.Combine(work, "left.txt"); var rightPath = Path.Combine(work, "right.txt");
        File.WriteAllText(leftPath, left, new UTF8Encoding(false)); File.WriteAllText(rightPath, right, new UTF8Encoding(false));
        pane.LeftPath.Text = leftPath; pane.BasePath.Text = ""; pane.RightPath.Text = rightPath;
        pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
    }

    // その側を操作の対象にして、画面の文書の位置で選ぶ。
    private static void Select(ComparisonPane pane, int side, int start, int length)
    {
        var view = pane.DiffEditor(side);
        pane.FocusDiffEditorRealLine(side, 0);
        view.TextArea.Focus();
        view.Select(start, length);
        Dispatcher.UIThread.RunJobs();
    }

    // エディターの入力欄へキーを送る（実行環境のコピー・切り取りのキーと同じ経路）。
    private static void EditorKey(AvaloniaEdit.TextEditor view, Key key, KeyModifiers modifiers)
    {
        view.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        Dispatcher.UIThread.RunJobs();
    }

    // 外枠のキー（トンネル段階）を送る。
    private static void Press(MainWindow window, AvaloniaEdit.TextEditor view, Key key, KeyModifiers modifiers)
    {
        if (!view.TextArea.IsFocused) view.TextArea.Focus();
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        Dispatcher.UIThread.RunJobs();
    }

    private static string Escape(string? text) => (text ?? "<null>").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\u200B", "<ZW>").Replace("\u200C", "<ZWN>");
}
