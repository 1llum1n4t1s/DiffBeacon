using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

/// <summary>
/// 「差分」タブの本文を直接編集するエディター（ComparisonPane.DiffEditors.cs）の自己検証。
/// - ゴースト行で左右の行がそろい、正本へ戻した本文が元の本文（改行の種類・末尾改行の有無を含む）と完全に一致すること。
/// - 本物の行への入力・ゴースト行への入力が正本へ反映され、再比較で差分が更新されること。保存したバイト列（BOM・UTF-16・CRLF）が期待どおりであること。
/// - 正本が読取り専用なら入力を受け付けないこと。別の「編集」タブが無いこと。
/// 期待値は、編集操作とは別に文字列で書いた値と照合する（実装の関数で期待値を作らない）。
/// </summary>
internal static class HeadlessDiffEditChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, string artifactOutput,
        Action<string, bool, string> check, Action<string> screenshot, Action<Task> pump)
    {
        window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs();
        var work = Path.Combine(output, "diff-edit"); Directory.CreateDirectory(work);

        // 「編集」タブは置かず、差分タブの本文で編集する。
        var tabs = pane.GetVisualDescendants().OfType<TabItem>().Select(tab => tab.Header as string ?? "").ToArray();
        check("diff edit has no separate edit tab", !tabs.Any(header => header.StartsWith("編集", StringComparison.Ordinal)) && tabs.Contains("差分"), string.Join("|", tabs));

        // 左: CRLF、右: LF。どちらも末尾改行なし（改行の種類は比較では区別しない）。左だけの行 b と右だけの行 d がある。
        var leftPath = Path.Combine(work, "left.txt"); var rightPath = Path.Combine(work, "right.txt");
        var leftOriginal = "a\r\nb\r\nc\r\ne"; var rightOriginal = "a\nc\nd\ne";
        File.WriteAllText(leftPath, leftOriginal, new UnicodeEncoding(false, true));
        File.WriteAllText(rightPath, rightOriginal, new UTF8Encoding(false));
        pane.LeftPath.Text = leftPath; pane.BasePath.Text = ""; pane.RightPath.Text = rightPath;
        pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
        var (leftView, rightView) = pane.DiffEditors;
        var rows = pane.CurrentDiff?.Rows.Count ?? -1;
        check("diff edit aligns both sides to the comparison rows", rows == 5 && LineTexts(leftView, rows).SequenceEqual(["a", "b", "c", "", "e"])
            && LineTexts(rightView, rows).SequenceEqual(["a", "", "c", "d", "e"]),
            $"rows={rows} left={string.Join("|", LineTexts(leftView, rows))} right={string.Join("|", LineTexts(rightView, rows))} "
            + $"diffLeft={Escape(pane.CurrentDiff?.LeftText)} numbers={string.Join(",", pane.CurrentDiff?.Rows.Select(row => $"{row.LeftLineNumber}/{row.RightLineNumber}") ?? [])}");
        check("diff edit marks one ghost line on each side", pane.DiffEditorGhostLineCount(false) == 1 && pane.DiffEditorGhostLineCount(true) == 1,
            $"{pane.DiffEditorGhostLineCount(false)}/{pane.DiffEditorGhostLineCount(true)}");
        check("diff edit leaves the source texts unchanged after display", pane.LeftEditor.Text == leftOriginal && pane.RightEditor.Text == rightOriginal,
            Escape(pane.LeftEditor.Text) + " / " + Escape(pane.RightEditor.Text));
        check("diff edit editors are editable for writable sources", !leftView.IsReadOnly && !rightView.IsReadOnly, $"{leftView.IsReadOnly}/{rightView.IsReadOnly}");
        screenshot("diff-edit-normal.png");

        // 本物の行（左の a）の行末へ入力すると、正本は CRLF を保ったまま a だけが変わる。
        Type(leftView, 1, "!");
        check("diff edit typing in a real line updates only that line of the source", pane.LeftEditor.Text == "a!\r\nb\r\nc\r\ne" && pane.TextDirty(0),
            Escape(pane.LeftEditor.Text));
        check("diff edit schedules a recompare after typing", pane.DiffEditRecomparePending, "");
        pump(pane.FlushDiffEditRecompareAsync()); Dispatcher.UIThread.RunJobs();
        check("diff edit recompare adopts the edited text", pane.CurrentDiff?.LeftText == "a!\r\nb\r\nc\r\ne" && pane.CurrentDiff.Rows.Any(row => row.LeftText == "a!"),
            Escape(pane.CurrentDiff?.LeftText));

        // 左のゴースト行（右の d の向かい）へ入力すると、その位置に本物の行が入る。改行は周りと同じ CRLF。
        var ghostRow = pane.CurrentDiff!.Rows.Select((row, index) => (row, index)).First(item => item.row.LeftText is null && item.row.RightText == "d").index + 1;
        Type(leftView, ghostRow, "D");
        check("diff edit typing in a ghost line inserts a real line in place", pane.LeftEditor.Text == "a!\r\nb\r\nc\r\nD\r\ne", Escape(pane.LeftEditor.Text));
        pump(pane.FlushDiffEditRecompareAsync()); Dispatcher.UIThread.RunJobs();
        check("diff edit ghost lines follow the new alignment", pane.DiffEditorGhostLineCount(false) == 0 && pane.DiffEditorGhostLineCount(true) == 1,
            $"{pane.DiffEditorGhostLineCount(false)}/{pane.DiffEditorGhostLineCount(true)}");

        // 右の最終行 e を編集しても、末尾に改行は足さない。
        var eRow = pane.CurrentDiff!.Rows.Select((row, index) => (row, index)).First(item => item.row.RightText == "e").index + 1;
        Type(rightView, eRow, "2");
        check("diff edit keeps the missing trailing newline of the edited side", pane.RightEditor.Text == "a\nc\nd\ne2", Escape(pane.RightEditor.Text));
        pump(pane.FlushDiffEditRecompareAsync()); Dispatcher.UIThread.RunJobs();

        // 保存したバイト列: 左は BOM 付き UTF-16LE・CRLF・末尾改行なし、右は BOM なし UTF-8・LF。
        pump(pane.SaveAsync(false)); pump(pane.SaveAsync(true));
        var leftBytes = File.ReadAllBytes(leftPath); var rightBytes = File.ReadAllBytes(rightPath);
        var leftExpected = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("a!\r\nb\r\nc\r\nD\r\ne")).ToArray();
        var rightExpected = Encoding.UTF8.GetBytes("a\nc\nd\ne2");
        check("diff edit saves the left side with UTF-16 BOM and CRLF", leftBytes.SequenceEqual(leftExpected), Convert.ToHexString(leftBytes));
        check("diff edit saves the right side as UTF-8 without BOM and LF", rightBytes.SequenceEqual(rightExpected), Convert.ToHexString(rightBytes));
        check("diff edit clears dirty after saving", !pane.TextDirty(0) && !pane.TextDirty(2), $"{pane.TextDirty(0)}/{pane.TextDirty(2)}");

        // 正本が読取り専用なら、エディターも入力を受け付けない。
        pane.RightEditor.IsReadOnly = true; Dispatcher.UIThread.RunJobs();
        var before = pane.RightEditor.Text;
        Type(rightView, 1, "X");
        check("diff edit refuses typing into a read-only source", rightView.IsReadOnly && pane.RightEditor.Text == before && !pane.DiffEditRecomparePending,
            $"readonly={rightView.IsReadOnly} text={Escape(pane.RightEditor.Text)}");
        pane.RightEditor.IsReadOnly = false; Dispatcher.UIThread.RunJobs();

        // \r だけの改行と末尾の改行も、表示から正本へ戻すと元のまま（ゴースト行は右の w の向かい）。
        pane.LeftEditor.Text = "x\ry\rz\r"; pane.RightEditor.Text = "x\ry\rw\rz\r";
        pump(pane.FlushDiffEditRecompareAsync()); pane.CompareEditors(); Dispatcher.UIThread.RunJobs();
        Type(leftView, 1, "1");
        check("diff edit keeps lone CR line endings and the trailing newline", pane.LeftEditor.Text == "x1\ry\rz\r", Escape(pane.LeftEditor.Text));
        pump(pane.FlushDiffEditRecompareAsync()); Dispatcher.UIThread.RunJobs();

        // 元に戻す／やり直す: 行を増やす編集で再比較が並びを変えても、Ctrl+Z／Ctrl+Y は利用者の編集単位で戻り・進む。
        {
            var undoPane = window.AddSession();
            var undoLeft = Path.Combine(work, "undo-left.txt"); var undoRight = Path.Combine(work, "undo-right.txt");
            File.WriteAllText(undoLeft, "a\nb\nc\n", new UTF8Encoding(false)); File.WriteAllText(undoRight, "a\nc\n", new UTF8Encoding(false));
            undoPane.LeftPath.Text = undoLeft; undoPane.BasePath.Text = ""; undoPane.RightPath.Text = undoRight;
            pump(undoPane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
            var view = undoPane.DiffEditors.Right;
            // 右の a の行末で Enter と x を入力し、右に新しい行 x を足す（右のゴースト行の位置が変わる）。
            Type(view, 1, "\nx");
            check("diff edit typing a new line updates the source", undoPane.RightEditor.Text == "a\nx\nc\n", Escape(undoPane.RightEditor.Text));
            pump(undoPane.FlushDiffEditRecompareAsync()); Dispatcher.UIThread.RunJobs();
            var realigned = undoPane.CurrentDiff?.RightText == "a\nx\nc\n";
            // 一度の入力（改行と x）は一つの「元に戻す」単位。再比較による整列もその単位に含まれる。
            Key(view, Avalonia.Input.Key.Z, Avalonia.Input.KeyModifiers.Control);
            var afterUndo = undoPane.RightEditor.Text;
            check("diff edit Ctrl+Z after a realigning recompare restores the user's edit", realigned && afterUndo == "a\nc\n" && undoPane.DiffEditorGhostLineCount(2) == 1,
                $"undo={Escape(afterUndo)} doc={Escape(view.Document.Text)} canUndo={view.CanUndo} ghosts={undoPane.DiffEditorGhostLineCount(2)}");
            pump(undoPane.FlushDiffEditRecompareAsync()); Dispatcher.UIThread.RunJobs();
            Key(view, Avalonia.Input.Key.Y, Avalonia.Input.KeyModifiers.Control);
            check("diff edit Ctrl+Y redoes the edits after the undo recompare", undoPane.RightEditor.Text == "a\nx\nc\n", Escape(undoPane.RightEditor.Text));
            pump(undoPane.FlushDiffEditRecompareAsync()); Dispatcher.UIThread.RunJobs();
            check("diff edit realigned display pairs b with x without ghost lines", undoPane.DiffEditorGhostLineCount(0) == 0 && undoPane.DiffEditorGhostLineCount(2) == 0,
                $"{undoPane.DiffEditorGhostLineCount(0)}/{undoPane.DiffEditorGhostLineCount(2)}");
            undoPane.DiscardChanges();
        }

        // 全ペイン共有の元に戻す／やり直す（本家と同じ）。どのペインで押しても、全ペインを通して最後の変更から順に戻る。
        // 左は CRLF・末尾改行なし、右は CR だけの改行。元に戻した本文は、元の本文と改行の種類まで完全に一致する。
        {
            var shared = window.AddSession();
            var sl = Path.Combine(work, "shared-left.txt"); var sr = Path.Combine(work, "shared-right.txt");
            const string leftOriginal2 = "a\r\nb\r\nc"; const string rightOriginal2 = "a\rc\r";
            File.WriteAllText(sl, leftOriginal2, new UTF8Encoding(false)); File.WriteAllText(sr, rightOriginal2, new UTF8Encoding(false));
            shared.LeftPath.Text = sl; shared.BasePath.Text = ""; shared.RightPath.Text = sr;
            pump(shared.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
            var (lv, rv) = shared.DiffEditors;
            Type(lv, 1, "L");
            pump(shared.FlushDiffEditRecompareAsync()); Dispatcher.UIThread.RunJobs();
            var leftEdited = shared.LeftEditor.Text;
            // 右は行を増やす入力。再比較を待たずに（まとめている途中で）元に戻す。
            Type(rv, 1, "\nR");
            var rightEdited = shared.RightEditor.Text;
            check("shared undo edits change the expected sides", leftEdited == "aL\r\nb\r\nc" && rightEdited != rightOriginal2
                && DiffEditorSideLines(rightEdited) == 3 && rightEdited!.Contains('R') && shared.DiffEditRecomparePending,
                $"{Escape(leftEdited)} / {Escape(rightEdited)}");
            Key(lv, Avalonia.Input.Key.Z, Avalonia.Input.KeyModifiers.Control);
            check("shared undo in the left pane first undoes the latest right edit", shared.RightEditor.Text == rightOriginal2 && shared.LeftEditor.Text == leftEdited
                && !shared.DiffEditRecomparePending && shared.CurrentDiff?.RightText == rightOriginal2,
                $"{Escape(shared.LeftEditor.Text)} / {Escape(shared.RightEditor.Text)} pending={shared.DiffEditRecomparePending}");
            Key(lv, Avalonia.Input.Key.Z, Avalonia.Input.KeyModifiers.Control);
            check("shared undo then undoes the left edit to the exact original bytes", shared.LeftEditor.Text == leftOriginal2 && shared.RightEditor.Text == rightOriginal2
                && !shared.TextDirty(0) && !shared.TextDirty(2) && !shared.CanUndoText && shared.TextHistoryRedoCount == 2,
                $"{Escape(shared.LeftEditor.Text)} / {Escape(shared.RightEditor.Text)} redo={shared.TextHistoryRedoCount}");
            Key(rv, Avalonia.Input.Key.Y, Avalonia.Input.KeyModifiers.Control);
            var afterFirstRedo = (shared.LeftEditor.Text, shared.RightEditor.Text);
            Key(rv, Avalonia.Input.Key.Y, Avalonia.Input.KeyModifiers.Control);
            check("shared redo replays the left then the right edit exactly", afterFirstRedo == (leftEdited, rightOriginal2)
                && shared.LeftEditor.Text == leftEdited && shared.RightEditor.Text == rightEdited && !shared.CanRedoText
                && shared.CurrentDiff?.LeftText == leftEdited && shared.CurrentDiff?.RightText == rightEdited,
                $"{Escape(afterFirstRedo.Item1)}|{Escape(afterFirstRedo.Item2)} -> {Escape(shared.LeftEditor.Text)}|{Escape(shared.RightEditor.Text)}");

            // 差分のコピーも共有の履歴に入る。保存した時点（右 = rightEdited）へ戻すと未保存の印が消え、さらに戻すと付く。
            pump(shared.SaveAsync(false)); pump(shared.SaveAsync(true)); Dispatcher.UIThread.RunJobs();
            shared.NavigateDifference(1); Dispatcher.UIThread.RunJobs();
            shared.CopySelected(true); Dispatcher.UIThread.RunJobs();
            var copied = shared.RightEditor.Text;
            check("shared undo records a difference copy", copied != rightEdited && shared.TextDirty(2) && shared.CanUndoText, Escape(copied));
            Key(rv, Avalonia.Input.Key.Z, Avalonia.Input.KeyModifiers.Control);
            check("shared undo of the copy returns to the saved point and clears dirty", shared.RightEditor.Text == rightEdited && !shared.TextDirty(2), Escape(shared.RightEditor.Text));
            Key(rv, Avalonia.Input.Key.Z, Avalonia.Input.KeyModifiers.Control);
            check("shared undo past the saved point marks the side dirty", shared.RightEditor.Text == rightOriginal2 && shared.TextDirty(2), Escape(shared.RightEditor.Text));
            // メニュー・ツールバーの「やり直し」（外枠のコマンド）も同じ履歴を使う。
            pump(shared.ExecuteFrameCommandAsync(FrameCommand.Redo)); Dispatcher.UIThread.RunJobs();
            check("shared redo from the frame command uses the shared history", shared.RightEditor.Text == rightEdited && !shared.TextDirty(2) && shared.CanRedoText,
                Escape(shared.RightEditor.Text));
            // 新しい入力を始めると、やり直しは消える。
            Type(lv, 1, "Z");
            check("shared history drops redo on a new edit", !shared.CanRedoText && shared.CanUndoText, $"redo={shared.TextHistoryRedoCount}");
            pump(shared.FlushDiffEditRecompareAsync()); Dispatcher.UIThread.RunJobs();

            // 読取り専用になった側の変更は戻さない。本文と履歴はそのまま残し、書き込めるようになれば戻せる。
            Type(rv, 1, "Q");
            pump(shared.FlushDiffEditRecompareAsync()); Dispatcher.UIThread.RunJobs();
            var rightWithQ = shared.RightEditor.Text; var undoCount = shared.TextHistoryUndoCount;
            shared.RightEditor.IsReadOnly = true; Dispatcher.UIThread.RunJobs();
            Key(lv, Avalonia.Input.Key.Z, Avalonia.Input.KeyModifiers.Control);
            check("shared undo refuses a read-only side and keeps text and history", shared.RightEditor.Text == rightWithQ && shared.TextHistoryUndoCount == undoCount,
                $"{Escape(shared.RightEditor.Text)} count={shared.TextHistoryUndoCount}/{undoCount}");
            shared.RightEditor.IsReadOnly = false; Dispatcher.UIThread.RunJobs();
            Key(lv, Avalonia.Input.Key.Z, Avalonia.Input.KeyModifiers.Control);
            check("shared undo works again once the side is writable", shared.RightEditor.Text == rightEdited && shared.TextHistoryUndoCount == undoCount - 1, Escape(shared.RightEditor.Text));

            // 記録していない経路（読込みなど）で正本が変わったら、履歴の前提が崩れるので消す。
            shared.LeftEditor.Text = "external\n"; Dispatcher.UIThread.RunJobs();
            check("shared history is cleared by an unrecorded source change", !shared.CanUndoText && !shared.CanRedoText && shared.TextHistoryUndoCount == 0,
                $"undo={shared.TextHistoryUndoCount} redo={shared.TextHistoryRedoCount}");
            shared.DiscardChanges();
        }

        // 履歴の記録と上限（数・文字数）。上限を超えたら古い記録から捨て、直前の1件はそれだけで上限を超えても残す。
        {
            var edit = ComparisonPane.TextEdit.Between(0, "a\r\nb\r\nc", "a\r\nXY\r\nc")!;
            check("shared history records only the replaced range", edit.Start == 3 && edit.Removed == "b" && edit.Inserted == "XY"
                && edit.Apply("a\r\nXY\r\nc", undo: true) == "a\r\nb\r\nc" && edit.Apply("a\r\nb\r\nc", undo: false) == "a\r\nXY\r\nc"
                && edit.Matches("a\r\nXY\r\nc", applied: true) && !edit.Matches("a\r\nZZ\r\nc", applied: true),
                $"{edit.Start} {Escape(edit.Removed)} {Escape(edit.Inserted)}");
            var history = new ComparisonPane.TextEditHistory(maxEntries: 3, maxCharacters: 10);
            for (var index = 0; index < 5; index++) history.Push(new ComparisonPane.TextEdit(0, index, "a", "b"));
            var byCount = (history.UndoCount, history.PeekUndo()?.Start);
            history.Push(new ComparisonPane.TextEdit(0, 9, new string('x', 50), ""));
            check("shared history trims the oldest entries by count and size", byCount == (3, 4) && history.UndoCount == 1 && history.PeekUndo()?.Start == 9,
                $"count={byCount} after={history.UndoCount}/{history.PeekUndo()?.Start}");
        }

        // 祖先のある三者比較は、本家と同じく左・中（祖先、読取り専用）・右の3ペインで、行をそろえて表示する。
        {
            var threePane = window.AddSession();
            var l3 = Path.Combine(work, "three-left.txt"); var b3 = Path.Combine(work, "three-base.txt"); var r3 = Path.Combine(work, "three-right.txt");
            File.WriteAllText(l3, "a\nL\nc\n", new UTF8Encoding(false)); File.WriteAllText(b3, "a\nb\nc\n", new UTF8Encoding(false)); File.WriteAllText(r3, "a\nb\nc\nR\n", new UTF8Encoding(false));
            threePane.LeftPath.Text = l3; threePane.BasePath.Text = b3; threePane.RightPath.Text = r3;
            pump(threePane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
            var views = new[] { threePane.DiffEditor(0), threePane.DiffEditor(1), threePane.DiffEditor(2) };
            var counts = views.Select(view => view.Document.LineCount).ToArray();
            check("diff edit three-way shows left, ancestor and right panes", threePane.DiffEditorsShowThreePanes && views.All(view => view.IsEffectivelyVisible)
                && counts.Distinct().Count() == 1, string.Join(",", counts));
            check("diff edit three-way aligns the ancestor rows", views[1].Document.GetText(views[1].Document.GetLineByNumber(2)) == "b"
                && views[0].Document.GetText(views[0].Document.GetLineByNumber(2)) == "L" && views[2].Document.GetText(views[2].Document.GetLineByNumber(4)) == "R"
                && threePane.DiffEditorGhostLineCount(0) >= 1 && threePane.DiffEditorGhostLineCount(1) >= 1,
                string.Join(" / ", views.Select(view => Escape(view.Document.Text))));
            check("diff edit three-way ancestor pane is read-only and sides are editable", views[1].IsReadOnly && !views[0].IsReadOnly && !views[2].IsReadOnly,
                string.Join(",", views.Select(view => view.IsReadOnly)));
            screenshot("diff-edit-three-way.png");
            var middleBefore = threePane.MiddleEditor.Text;
            Type(views[1], 1, "!");
            check("diff edit refuses typing into the fixed ancestor", threePane.MiddleEditor.Text == middleBefore && !threePane.CanUndoText && threePane.TextHistoryUndoCount == 0,
                Escape(threePane.MiddleEditor.Text));
            threePane.DiscardChanges();
        }

        // 独立三者で「中央と右」を比べ、中央と右が同じ本文でも、中央のペインへの入力は中央の正本だけを変える。
        {
            var independent = window.AddSession();
            var il = Path.Combine(work, "ind-left.txt"); var im = Path.Combine(work, "ind-middle.txt"); var ir = Path.Combine(work, "ind-right.txt");
            File.WriteAllText(il, "one\n", new UTF8Encoding(false)); File.WriteAllText(im, "same\n", new UTF8Encoding(false)); File.WriteAllText(ir, "same\n", new UTF8Encoding(false));
            independent.ApplyProject(new ComparisonProject
            {
                Mode = "Text", LeftPath = il, BasePath = im, RightPath = ir,
                TextInputs = new() { Semantics = "Independent", Left = new() { Kind = "Physical" }, Middle = new() { Kind = "Physical" }, Right = new() { Kind = "Physical" } },
            });
            pump(independent.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
            independent.SelectTextPair(1); pump(independent.IndependentTextUiTask ?? Task.CompletedTask); Dispatcher.UIThread.RunJobs();
            check("diff edit independent three-way shows all three sides", independent.DiffEditorsShowThreePanes, "");
            Type(independent.DiffEditor(1), 1, "M");
            check("diff edit independent typing changes only the middle source", independent.MiddleEditor.Text == "sameM\n" && independent.RightEditor.Text == "same\n" && independent.LeftEditor.Text == "one\n",
                $"{Escape(independent.LeftEditor.Text)}|{Escape(independent.MiddleEditor.Text)}|{Escape(independent.RightEditor.Text)}");
            // 中央 → 右 → 左の順に入力し、中央のペインで元に戻すと、左 → 右 → 中央の順に一つずつ戻る（三側で一つの履歴）。
            pump(independent.FlushDiffEditRecompareAsync()); pump(independent.IndependentTextUiTask ?? Task.CompletedTask); Dispatcher.UIThread.RunJobs();
            Type(independent.DiffEditor(2), 1, "R");
            pump(independent.FlushDiffEditRecompareAsync()); pump(independent.IndependentTextUiTask ?? Task.CompletedTask); Dispatcher.UIThread.RunJobs();
            Type(independent.DiffEditor(0), 1, "L");
            pump(independent.FlushDiffEditRecompareAsync()); pump(independent.IndependentTextUiTask ?? Task.CompletedTask); Dispatcher.UIThread.RunJobs();
            string Texts() => $"{Escape(independent.LeftEditor.Text)}|{Escape(independent.MiddleEditor.Text)}|{Escape(independent.RightEditor.Text)}";
            var steps = new List<string> { Texts() };
            for (var step = 0; step < 3; step++)
            {
                Key(independent.DiffEditor(1), Avalonia.Input.Key.Z, Avalonia.Input.KeyModifiers.Control);
                pump(independent.IndependentTextUiTask ?? Task.CompletedTask); Dispatcher.UIThread.RunJobs();
                steps.Add(Texts());
            }
            check("diff edit independent three-way shares one history across the three sides", steps.SequenceEqual([
                "oneL\\n|sameM\\n|sameR\\n", "one\\n|sameM\\n|sameR\\n", "one\\n|sameM\\n|same\\n", "one\\n|same\\n|same\\n"]) && !independent.CanUndoText,
                string.Join(" -> ", steps));
            independent.DiscardChanges();
        }

        // プロジェクトで読取り専用にした側は、エディターでも入力を受け付けない。
        {
            var readOnly = window.AddSession();
            var rl = Path.Combine(work, "ro-left.txt"); var rr = Path.Combine(work, "ro-right.txt");
            File.WriteAllText(rl, "left\n", new UTF8Encoding(false)); File.WriteAllText(rr, "right\n", new UTF8Encoding(false));
            readOnly.ApplyProject(new ComparisonProject { Mode = "Text", LeftPath = rl, RightPath = rr, LeftReadOnly = true });
            pump(readOnly.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
            Type(readOnly.DiffEditors.Left, 1, "!");
            check("diff edit refuses typing into a project read-only side", readOnly.DiffEditors.Left.IsReadOnly && readOnly.LeftEditor.Text == "left\n" && !readOnly.DiffEditors.Right.IsReadOnly,
                $"{readOnly.DiffEditors.Left.IsReadOnly} {Escape(readOnly.LeftEditor.Text)}");
            readOnly.DiscardChanges();
        }

        // 通常・最小ウィンドウで左右のエディターが並び、使える高さが残る（上で開いた検査用のタブから元のタブへ戻す）。
        window.SelectSession(window.SessionPanes.ToList().IndexOf(pane)); Dispatcher.UIThread.RunJobs();
        var bounds = new List<(string Layout, Rect Left, Rect Right)>();
        foreach (var (layout, width, height) in new[] { ("normal", 1280d, 850d), ("minimum", 850d, 550d) })
        {
            window.Width = width; window.Height = height; Dispatcher.UIThread.RunJobs();
            screenshot($"diff-edit-{layout}-layout.png");
            var left = Within(leftView, window); var right = Within(rightView, window);
            bounds.Add((layout, left, right));
            check($"diff edit {layout} shows both editors side by side", left.Width > 100 && right.Width > 100 && left.Height >= 60 && Math.Abs(left.Top - right.Top) < 1 && left.Right <= right.Left + 1,
                $"left={left} right={right}");
        }
        window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs();
        using var file = File.Create(Path.Combine(artifactOutput, "diff-edit-bounds.json"));
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        json.WriteStartArray();
        foreach (var item in bounds)
        {
            json.WriteStartObject(); json.WriteString("layout", item.Layout);
            json.WriteNumber("leftX", item.Left.X); json.WriteNumber("leftY", item.Left.Y); json.WriteNumber("leftWidth", item.Left.Width); json.WriteNumber("leftHeight", item.Left.Height);
            json.WriteNumber("rightX", item.Right.X); json.WriteNumber("rightWidth", item.Right.Width); json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    // 利用者の入力と同じ経路（キャレットを置いて文字を入力）で、行の末尾へ文字を足す。
    private static void Type(AvaloniaEdit.TextEditor view, int line, string text)
    {
        var documentLine = view.Document.GetLineByNumber(line);
        view.TextArea.Caret.Offset = documentLine.EndOffset;
        view.TextArea.PerformTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    // 利用者のキー操作と同じ経路（エディターの入力欄へキーを送る）。
    private static void Key(AvaloniaEdit.TextEditor view, Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers)
    {
        view.TextArea.Focus();
        view.TextArea.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        Dispatcher.UIThread.RunJobs();
    }

    private static string[] LineTexts(AvaloniaEdit.TextEditor view, int count)
        => Enumerable.Range(1, Math.Min(count, view.Document.LineCount))
            .Select(number => view.Document.GetText(view.Document.GetLineByNumber(number)).Replace("\u200B", "").Replace("\u200C", "")).ToArray();

    // 改行（CRLF・LF・CR）で区切った行の数。
    private static int DiffEditorSideLines(string? text) => ComparisonPane.DiffEditorSide.SplitLines(text ?? "").Count;

    private static string Escape(string? text) => (text ?? "<null>").Replace("\r", "\\r").Replace("\n", "\\n");

    private static Rect Within(Control control, Visual root) => new(control.TranslatePoint(default, root) ?? default, control.Bounds.Size);
}
