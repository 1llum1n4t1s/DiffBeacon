using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMerge風の「ファイルまたはフォルダーの選択」欄（ComparisonPane.OpenView.cs）の自己検証。
/// 欄の並び（1番目＝左、2番目＝右、3番目＝祖先／中央）、Japanese.po の見出し、交換ボタン、参照メニュー、
/// 「開く」で選択欄がスクロール位置から戻ること、通常・最小ウィンドウで比較ボタンまで横に収まることを確かめる。
/// 既存のパス欄・比較ボタンのインスタンスを載せ替えていること（複製が無いこと）も確認する。
/// </summary>
internal static class HeadlessOpenViewChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string artifactOutput,
        Action<string, bool, string> check, Action<string> screenshot, Action<Task> pump)
    {
        window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs();
        var view = pane.OpenView;
        check("open view is built in the pane", view is not null && view.GetVisualAncestors().Contains(pane), view?.Name ?? "missing");
        if (view is null) return;

        // 既存のインスタンスがそのまま選択欄に載っていること。
        var boxes = new Control[] { pane.LeftPath, pane.RightPath, pane.BasePath, pane.FileFilterBox, pane.RecursiveBox, pane.CompareButton };
        check("open view hosts the existing path, filter and compare controls",
            boxes.All(box => box.GetVisualAncestors().Contains(view)), string.Join("|", boxes.Select(box => box.GetType().Name + ":" + box.GetVisualAncestors().Contains(view))));
        var tops = new[] { pane.LeftPath, pane.RightPath, pane.BasePath }.Select(box => box.TranslatePoint(default, view)?.Y ?? -1).ToArray();
        check("open view orders 1st left, 2nd right, 3rd ancestor", tops[0] >= 0 && tops[0] < tops[1] && tops[1] < tops[2], string.Join(",", tops));
        check("pane has a single compare button", pane.GetVisualDescendants().OfType<Button>().Count(button => ReferenceEquals(button, pane.CompareButton)) == 1
            && Equals(pane.CompareButton.Content, "比較(_M)"), pane.CompareButton.Content?.ToString() ?? "");

        // 見出しは Japanese.po の訳（アクセスキーは「_」）。
        var labels = view.GetVisualDescendants().OfType<Label>().Select(label => label.Content as string).ToArray();
        string[] expectedLabels = ["_1 番目のファイルまたはフォルダー", "_2 番目のファイルまたはフォルダー", "_3 番目のファイルまたはフォルダー (オプション)", " フォルダー: フィルター"];
        check("open view group headers follow Japanese.po", labels.SequenceEqual(expectedLabels), string.Join("|", labels));
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToHashSet();
        check("open view swap captions follow Japanese.po", new[] { "1 番目と 2 番目を交換", "2 番目と 3 番目を交換", "1 番目と 3 番目を交換" }.All(texts.Contains), string.Join("|", texts));
        check("open view include subfolders follows Japanese.po", Equals(pane.RecursiveBox.Content, "サブフォルダーを含める(_I)"), pane.RecursiveBox.Content?.ToString() ?? "");

        // 交換ボタンは対応する二つの欄だけを入れ替える。
        var swaps = view.GetVisualDescendants().OfType<Button>().Where(button => Equals(button.Content, "⇅")).ToArray();
        check("open view has three swap buttons", swaps.Length == 3, swaps.Length.ToString());
        if (swaps.Length == 3)
        {
            foreach (var (index, expected) in new[] { (0, "R|L|B"), (1, "L|B|R"), (2, "B|R|L") })
            {
                pane.LeftPath.Text = "L"; pane.RightPath.Text = "R"; pane.BasePath.Text = "B";
                swaps[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
                var actual = $"{pane.LeftPath.Text}|{pane.RightPath.Text}|{pane.BasePath.Text}";
                check($"open view swap {index} exchanges only its pair", actual == expected, actual);
            }
        }
        pane.LeftPath.Text = ""; pane.RightPath.Text = ""; pane.BasePath.Text = "";

        // 参照ボタンは本家の分割ボタンと同じく、ファイルとフォルダーを選べる。
        var browses = view.GetVisualDescendants().OfType<Button>().Where(button => Equals(button.Content, "参照(_B)...")).ToArray();
        check("open view browse buttons offer file and folder", browses.Length == 3 && browses.All(button => button.Flyout is MenuFlyout menu
            && menu.Items.OfType<MenuItem>().Select(item => item.Header as string).SequenceEqual(["ファイル(_F)...", "フォルダー(_O)..."])), browses.Length.ToString());

        // 「開く」は、スクロールで隠れた選択欄を見える位置へ戻し、1番目の欄へ移動する。
        window.Width = 850; window.Height = 550; Dispatcher.UIThread.RunJobs();
        var toolbar = pane.ComparisonToolbar;
        toolbar.Offset = new Vector(0, Math.Max(0, toolbar.Extent.Height - toolbar.Viewport.Height)); Dispatcher.UIThread.RunJobs();
        var scrolled = toolbar.Offset.Y;
        window.FocusPaths(); Dispatcher.UIThread.RunJobs();
        var leftTop = pane.LeftPath.TranslatePoint(default, toolbar)?.Y ?? -1;
        check("open command scrolls the open view back into view", scrolled > 0 && leftTop >= 0 && leftTop + pane.LeftPath.Bounds.Height <= toolbar.Viewport.Height + 1,
            $"scrolled={scrolled} offset={toolbar.Offset.Y} leftTop={leftTop} viewport={toolbar.Viewport.Height}");
        check("open command focuses the 1st path", pane.LeftPath.IsFocused || pane.LeftPath.IsKeyboardFocusWithin, $"focused={pane.LeftPath.IsFocused}");

        // 通常・最小ウィンドウで、パス欄が十分な幅を持ち、比較ボタンまで横に収まる。
        var bounds = new List<(string Layout, double Width, double Height, Rect Left, Rect Compare)>();
        foreach (var (layout, width, height) in new[] { ("normal", 1280d, 850d), ("minimum", 850d, 550d) })
        {
            window.Width = width; window.Height = height; Dispatcher.UIThread.RunJobs();
            toolbar.Offset = default; Dispatcher.UIThread.RunJobs();
            screenshot($"open-view-{layout}.png");
            var left = Within(pane.LeftPath, window); var compare = Within(pane.CompareButton, window);
            bounds.Add((layout, width, height, left, compare));
            check($"open view {layout} keeps wide paths and a reachable compare button",
                left.Width >= 300 && compare.Width > 0 && compare.Right <= width + .5 && compare.Left >= 0, $"left={left} compare={compare}");
            // 比較前の空のセッションは、本家と同じく選択画面を比較ボタンまでスクロールなしで見せる。
            var compareInToolbar = pane.CompareButton.TranslatePoint(default, toolbar)?.Y ?? -1;
            check($"open view {layout} shows the compare button without scrolling before comparing",
                compareInToolbar >= 0 && compareInToolbar + pane.CompareButton.Bounds.Height <= toolbar.Viewport.Height + 1,
                $"compareTop={compareInToolbar} viewport={toolbar.Viewport.Height} max={toolbar.MaxHeight}");
        }

        // 比較した後は本文が主役なので、選択画面の高さは従来どおり本文の3割までに戻る。
        var compared = Path.Combine(artifactOutput, "open-view-inputs"); Directory.CreateDirectory(compared);
        var leftFile = Path.Combine(compared, "left.txt"); var rightFile = Path.Combine(compared, "right.txt");
        File.WriteAllText(leftFile, "a\nb\n"); File.WriteAllText(rightFile, "a\nc\n");
        pane.LeftPath.Text = leftFile; pane.RightPath.Text = rightFile;
        pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
        check("open view returns to the capped height after comparing", pane.CurrentDiff is not null && toolbar.MaxHeight <= pane.Bounds.Height * .3 + 1,
            $"max={toolbar.MaxHeight} pane={pane.Bounds.Height}");
        window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs();

        using var file = File.Create(Path.Combine(artifactOutput, "open-view-bounds.json"));
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        json.WriteStartArray();
        foreach (var item in bounds)
        {
            json.WriteStartObject(); json.WriteString("layout", item.Layout); json.WriteNumber("windowWidth", item.Width); json.WriteNumber("windowHeight", item.Height);
            json.WriteNumber("leftPathX", item.Left.X); json.WriteNumber("leftPathWidth", item.Left.Width);
            json.WriteNumber("compareX", item.Compare.X); json.WriteNumber("compareY", item.Compare.Y); json.WriteNumber("compareWidth", item.Compare.Width); json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    private static Rect Within(Control control, Visual root)
        => new(control.TranslatePoint(default, root) ?? default, control.Bounds.Size);
}
