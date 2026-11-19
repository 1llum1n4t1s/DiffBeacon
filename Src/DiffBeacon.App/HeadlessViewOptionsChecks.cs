using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMerge風の「表示」メニューの本文の設定（拡大／縮小・空白・改行・行内差異・行番号・マージン、DiffTextViewOptions と
/// ComparisonPane.ViewOptions.cs）の自己検証。期待値は実装の関数を使わずに書いた値（文字の大きさ、印の文字列）と照合する。
/// 設定はアプリ全体で共有されるので、最後に既定へ戻して後続の検証へ影響を残さない。
/// </summary>
internal static partial class HeadlessViewOptionsChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, string artifactOutput,
        Action<string, bool, string> check, Action<string> screenshot, Action<Task> pump)
    {
        window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs();
        var work = Path.Combine(output, "view-options"); Directory.CreateDirectory(work);
        var options = window.TextViewOptions;
        try
        {
            RunChecks(window, pane, work, options, check, screenshot, pump);
            CheckPersistence(work, check);
        }
        finally
        {
            options.Zoom(0);
            options.ViewWhitespace = false; options.ViewEndOfLine = false; options.ViewLineDifferences = true;
            options.ViewLineNumbers = true; options.ViewMargins = true;
            Dispatcher.UIThread.RunJobs();
            pane.DiscardChanges();
        }
    }

    private static void RunChecks(MainWindow window, ComparisonPane pane, string work, DiffTextViewOptions options,
        Action<string, bool, string> check, Action<string> screenshot, Action<Task> pump)
    {
        // メニューの並び（本家 IDR_MERGEDOCTYPE の「表示」のうち対応する項目）と拡大／縮小の小メニュー・キーの表示。
        var view = window.FrameMenu!.Items.OfType<MenuItem>().First(item => item.Header as string == "表示(_V)");
        var headers = view.Items.OfType<MenuItem>().Select(item => item.Header as string ?? "").ToArray();
        check("view menu lists the WinMerge text view options", headers.SequenceEqual(["拡大/縮小(_Z)", "空白を表示(_V)", "改行を表示(_O)", "行内差異を表示(_W)", "行番号を表示(_N)",
            "マージンを表示(_M)", "ツールバー(_T)", "ステータス バー(&S)", "Diff ペイン(&P)", "ロケーション ペイン(&C)", "ペインの交換(_W)", "ダーク テーマ(_D)", "最新の情報に更新"]), string.Join("|", headers));
        var zoomMenu = view.Items.OfType<MenuItem>().First(item => item.Header as string == "拡大/縮小(_Z)");
        var zoomItems = zoomMenu.Items.OfType<MenuItem>().ToArray();
        // メニューに出るキーの文字は、テーマのメニュー項目が使う変換（PlatformKeyGestureConverter）を通した文字列で確かめる。
        var converter = new Avalonia.Controls.Converters.PlatformKeyGestureConverter();
        var shown = zoomItems.Select(item => converter.Convert(item.InputGesture, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture) as string ?? "").ToArray();
        var zoomText = string.Join("|", zoomItems.Select((item, index) => $"{item.Header}={item.InputGesture}/{shown[index]}"));
        check("view zoom submenu matches WinMerge", zoomItems.Select(item => item.Header as string).SequenceEqual(["拡大(_I)", "縮小(_O)", "標準に戻す(_N)"])
            && zoomItems[0].InputGesture is { Key: Key.OemPlus, KeyModifiers: KeyModifiers.Control }
            && zoomItems[1].InputGesture is { Key: Key.OemMinus, KeyModifiers: KeyModifiers.Control }
            && zoomItems[2].InputGesture is { Key: Key.Multiply, KeyModifiers: KeyModifiers.Control }
            && shown.SequenceEqual(ExpectedZoomGestureTexts), zoomText);
        MenuItem Option(string name) => view.Items.OfType<MenuItem>().First(item => item.Name == "frame-menu-" + name);
        var checks = new[] { "view-whitespace", "view-eol", "view-line-differences", "view-line-numbers", "view-margins" }.Select(name => Option(name).IsChecked).ToArray();
        check("view options start with WinMerge defaults except line numbers and margins", checks.SequenceEqual([false, false, true, true, true])
            && options.ZoomSteps == 0 && pane.DiffEditor(0).FontSize == 13, string.Join(",", checks) + $" zoom={options.ZoomSteps} font={pane.DiffEditor(0).FontSize}");

        // 左は CRLF・末尾改行なし、右は LF・末尾改行あり。右にだけ extra と four の行があるので左の3行目はゴースト行、
        // four に合わせて左の最後にもゴースト行（直前の改行は表示のために足したもの）が入る。
        var leftOriginal = "one\r\nalpha beta\r\nthree";
        var rightOriginal = "one\nalpha gamma\nextra\nthree\nfour\n";
        Compare(pane, work, "plain", leftOriginal, rightOriginal, pump);
        var left = pane.DiffEditor(0); var right = pane.DiffEditor(2);
        check("view fixture has ghost lines on the left", pane.DiffEditorGhostLineCount(0) == 2, pane.DiffEditorGhostLineCount(0).ToString());

        // 拡大／縮小のキー: 本文にフォーカスがあるときの Ctrl++（Ctrl+テンキー+）・Ctrl+-・Ctrl+*・Ctrl+0。1回で 1 ポイント（4/3 DIP）。
        pane.FocusDiffEditorRealLine(0, 0);
        Press(window, left, Key.OemPlus, KeyModifiers.Control); var plus = left.FontSize;
        Press(window, left, Key.Add, KeyModifiers.Control); var add = right.FontSize;
        Press(window, left, Key.OemMinus, KeyModifiers.Control); var minus = left.FontSize;
        Press(window, left, Key.Multiply, KeyModifiers.Control); var normal = left.FontSize;
        Press(window, left, Key.OemPlus, KeyModifiers.Control); Press(window, left, Key.D0, KeyModifiers.Control); var zero = left.FontSize;
        check("view zoom keys change every pane by one point and reset", Near(plus, 13 + 4.0 / 3) && Near(add, 13 + 8.0 / 3) && Near(minus, 13 + 4.0 / 3) && Near(normal, 13) && Near(zero, 13),
            $"plus={plus} add={add} minus={minus} normal={normal} zero={zero}");

        // 通常の入力欄（パスの欄）にフォーカスがあるときは拡大しない（入力欄のキーを奪わない）。
        pane.LeftPath.Focus(); Dispatcher.UIThread.RunJobs();
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.OemPlus, KeyModifiers = KeyModifiers.Control }); Dispatcher.UIThread.RunJobs();
        check("view zoom keys are ignored while a path box has focus", options.ZoomSteps == 0 && Near(left.FontSize, 13), $"zoom={options.ZoomSteps}");

        // Ctrl+ホイール（本家 OnMouseWheel）。上で拡大、下で縮小し、本文はスクロールさせない。
        pane.FocusDiffEditorRealLine(0, 0);
        var wheelUp = Wheel(window, left, 1); var afterUp = options.ZoomSteps;
        var wheelDown = Wheel(window, left, -1); var afterDown = options.ZoomSteps;
        check("view ctrl wheel zooms in and out", wheelUp && wheelDown && afterUp == 1 && afterDown == 0, $"handled={wheelUp},{wheelDown} steps={afterUp},{afterDown}");

        // 下限は 2 ポイント（本家 ZoomText）、上限は本家 OPT_VIEW_ZOOM の 800%。基準 13 DIP＝9.75 ポイントなので -7 と +68 で止まる。
        for (var i = 0; i < 20; i++) options.Zoom(-1);
        var minimum = options.ZoomSteps; var minimumFont = left.FontSize;
        for (var i = 0; i < 100; i++) options.Zoom(1);
        var maximum = options.ZoomSteps;
        options.Zoom(0);
        check("view zoom stops at two points and 800 percent", minimum == -7 && Near(minimumFont, 13 - 7 * 4.0 / 3) && maximum == 68 && options.ZoomSteps == 0,
            $"min={minimum} font={minimumFont} max={maximum}");

        // 拡大中も全ペインの行の高さとスクロール位置がそろう（ゴースト行の対応が崩れない）。行番号の欄も同じ倍率で広がる。
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var baseMargin = pane.DiffEditorMarginWidth(0);
        for (var i = 0; i < 6; i++) options.Zoom(1);
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var zoomedMargin = pane.DiffEditorMarginWidth(0);
        var heights = (left.TextArea.TextView.DefaultLineHeight, right.TextArea.TextView.DefaultLineHeight);
        var alignedText = pane.LeftEditor.Text == leftOriginal && pane.RightEditor.Text == rightOriginal;
        check("view zoom keeps panes aligned and scales the line number margin", Near(heights.Item1, heights.Item2) && heights.Item1 > 0 && zoomedMargin > baseMargin * 1.3 && alignedText,
            $"heights={heights.Item1},{heights.Item2} margin={baseMargin}->{zoomedMargin}");
        screenshot("view-zoomed.png");
        options.Zoom(0);

        // 空白を表示: メニューから切り替えると全ペインで空白・タブの印（本家 ViewableWhitespace の · と »）を出す。
        Click(Option("view-whitespace"));
        check("view whitespace shows WinMerge glyphs in every pane", options.ViewWhitespace && Option("view-whitespace").IsChecked
            && new[] { 0, 1, 2 }.All(side => pane.DiffEditor(side).Options is { ShowSpaces: true, ShowTabs: true, ShowSpacesGlyph: "·", ShowTabsGlyph: "»" }),
            $"{left.Options.ShowSpaces} {left.Options.ShowSpacesGlyph} {left.Options.ShowTabsGlyph}");

        // 改行を表示: 本物の行の本物の改行にだけ印を付ける。表示の行は 左=one／alpha beta／ゴースト／three（足した改行）／ゴースト、右=one／alpha gamma／extra／three／four。
        Click(Option("view-eol"));
        var leftGlyphs = Glyphs(pane, 0, 5); var rightGlyphs = Glyphs(pane, 2, 5);
        check("view eol marks only real line breaks and skips ghost lines and the added break", options.ViewEndOfLine
            && leftGlyphs.SequenceEqual(["◄┘", "◄┘", null, null, null]) && rightGlyphs.SequenceEqual(["◄┘", "◄┘", "◄┘", "◄┘", "◄┘"]),
            $"left={Show(leftGlyphs)} right={Show(rightGlyphs)}");
        screenshot("view-whitespace-eol.png");

        // 行内差異を表示: 外すと本文と Diff ペインの行内差異の色を塗らない（比較結果は変えない）。
        pane.DiffList.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
        var highlighted = Highlighted(pane.DiffPaneTexts.Left) + Highlighted(pane.DiffPaneTexts.Right);
        Click(Option("view-line-differences"));
        pane.DiffList.SelectedIndex = 0; pane.DiffList.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
        var plain = Highlighted(pane.DiffPaneTexts.Left) + Highlighted(pane.DiffPaneTexts.Right);
        check("view line differences toggles the word highlight", highlighted > 0 && plain == 0 && !options.ViewLineDifferences && pane.CurrentDiff?.Blocks.Count > 0,
            $"on={highlighted} off={plain} text={Escape(pane.DiffPaneTexts.Left.Text)}");
        screenshot("view-no-line-differences.png");
        Click(Option("view-line-differences"));

        // 行番号を表示・マージンを表示: 外すとその欄の幅を除き、両方外すと欄と縦線を隠す。
        window.UpdateLayout(); var both = pane.DiffEditorMarginWidth(0);
        Click(Option("view-line-numbers")); window.UpdateLayout(); var marginOnly = pane.DiffEditorMarginWidth(0);
        Click(Option("view-margins")); window.UpdateLayout(); var none = pane.DiffEditorMarginWidth(0); var separator = pane.DiffEditorMarginSeparatorVisible(0);
        screenshot("view-no-margins.png");
        Click(Option("view-line-numbers")); window.UpdateLayout(); var numbersOnly = pane.DiffEditorMarginWidth(0);
        Click(Option("view-margins")); window.UpdateLayout();
        check("view line numbers and margins hide their columns", Near(marginOnly, 14) && none == 0 && !separator && Near(numbersOnly, both - 14)
            && pane.DiffEditorMarginSeparatorVisible(0), $"both={both} margin={marginOnly} none={none} numbers={numbersOnly} separator={separator}");

        // スクロールした位置で拡大／縮小しても、本家と同じく一番上に見えていた行を保ち、全ペインがその行にそろう。
        // 左にだけ無い行を混ぜてゴースト行を作り、表示の200行目付近を一番上にしてから1段ずつ拡大・縮小する。
        var longLeft = new StringBuilder(); var longRight = new StringBuilder();
        for (var i = 1; i <= 300; i++) { if (i % 7 != 0) longLeft.Append("line ").Append(i).Append('\n'); longRight.Append("line ").Append(i).Append('\n'); }
        Compare(pane, work, "long", longLeft.ToString(), longRight.ToString(), pump);
        window.UpdateLayout();
        var scroller = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(left).OfType<ScrollViewer>().First();
        scroller.Offset = new Vector(0, left.TextArea.TextView.GetVisualTopByDocumentLine(200));
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var before = (pane.DiffEditorTopDisplayLine(0), pane.DiffEditorTopDisplayLine(2));
        options.Zoom(1); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var zoomedTop = (pane.DiffEditorTopDisplayLine(0), pane.DiffEditorTopDisplayLine(2));
        options.Zoom(-3); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var shrunkTop = (pane.DiffEditorTopDisplayLine(0), pane.DiffEditorTopDisplayLine(2));
        screenshot("view-zoomed-scrolled.png");
        options.Zoom(0); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        check("view zoom keeps the top visible line while scrolled", before.Item1 == 200 && before.Item2 == 200 && zoomedTop == before && shrunkTop == before
            && pane.DiffEditorTopDisplayLine(0) == 200, $"before={before} zoomed={zoomedTop} shrunk={shrunkTop} reset={pane.DiffEditorTopDisplayLine(0)}");

        // 改行が混在する側は、本家と同じく CR・LF・CR+LF を描き分ける。
        Compare(pane, work, "mixed", "a\r\nb\r\nc\r\n", "a\nb\r\nc\r", pump);
        var mixed = Glyphs(pane, 2, 3); var uniform = Glyphs(pane, 0, 3);
        check("view eol distinguishes CR, LF and CRLF only in mixed text", mixed.SequenceEqual(["↓", "◄┘", "←"]) && uniform.SequenceEqual(["◄┘", "◄┘", "◄┘"]),
            $"mixed={Show(mixed)} uniform={Show(uniform)}");

        // 設定はアプリ全体で一つ。後から開いたタブも同じ大きさ・印で表示し、切り替えは本文・保存の状態を変えない。
        options.Zoom(2);
        using (var other = new ComparisonPane(window))
            check("view options apply to newly opened panes", Near(other.DiffEditor(2).FontSize, 13 + 8.0 / 3) && other.DiffEditor(0).Options.ShowSpaces,
                $"font={other.DiffEditor(2).FontSize} spaces={other.DiffEditor(0).Options.ShowSpaces}");
        check("view options do not change the text", pane.LeftEditor.Text == "a\r\nb\r\nc\r\n" && pane.RightEditor.Text == "a\nb\r\nc\r",
            $"{Escape(pane.LeftEditor.Text)} {Escape(pane.RightEditor.Text)}");
    }

    // 本家のメニューの表示（Ctrl++／Ctrl+-／Ctrl+*）。
    private static readonly string[] ExpectedZoomGestureTexts = ["Ctrl++", "Ctrl+-", "Ctrl+*"];

    private static void Compare(ComparisonPane pane, string work, string name, string left, string right, Action<Task> pump)
    {
        var leftPath = Path.Combine(work, name + "-left.txt"); var rightPath = Path.Combine(work, name + "-right.txt");
        File.WriteAllText(leftPath, left, new UTF8Encoding(false)); File.WriteAllText(rightPath, right, new UTF8Encoding(false));
        pane.LeftPath.Text = leftPath; pane.BasePath.Text = ""; pane.RightPath.Text = rightPath;
        pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
    }

    private static string?[] Glyphs(ComparisonPane pane, int side, int lines) =>
        Enumerable.Range(1, lines).Select(line => pane.DiffEditorEndOfLineGlyph(side, line)).ToArray();

    private static int Highlighted(TextBlock block) =>
        block.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Count(run => run.Background is not null) ?? 0;

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    // 外枠のキー（トンネル段階）を送る。
    private static void Press(MainWindow window, AvaloniaEdit.TextEditor view, Key key, KeyModifiers modifiers)
    {
        if (!view.TextArea.IsFocused) view.TextArea.Focus();
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        Dispatcher.UIThread.RunJobs();
    }

    // 本文の上で Ctrl を押したままホイールを回す。処理済みになったか（スクロールへ流れないか）を返す。
    private static bool Wheel(MainWindow window, AvaloniaEdit.TextEditor view, int delta)
    {
        var args = new PointerWheelEventArgs(view.TextArea, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), window,
            new Point(20, 20), 0, new PointerPointProperties(), KeyModifiers.Control, new Vector(0, delta));
        view.TextArea.RaiseEvent(args);
        Dispatcher.UIThread.RunJobs();
        return args.Handled;
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < .01;

    private static string Show(string?[] glyphs) => string.Join(",", glyphs.Select(glyph => glyph ?? "-"));

    private static string Escape(string? text) => (text ?? "<null>").Replace("\r", "\\r").Replace("\n", "\\n");
}
