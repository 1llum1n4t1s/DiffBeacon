using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMerge風の外枠（メニューバー・ツールバー・ステータスバー・差分色）の自己検証。
/// 構成の一致だけでなく、ツールバーとショートカットが実際に比較ペインを操作することと、
/// 最小ウィンドウでも外枠が本文を押し出さないことを確認する。結果は PNG と bounds JSON で残す。
/// </summary>
internal static class HeadlessFrameChecks
{
    // 本家 Merge.rc の IDR_MERGEDOCTYPE のトップレベル（Japanese.po の訳）。
    private static readonly string[] ExpectedMenus =
        ["ファイル(_F)", "編集(_E)", "表示(_V)", "マージ(_M)", "ツール(_T)", "プラグイン(_P)", "ウィンドウ(_W)", "ヘルプ(_H)"];

    internal static void Run(MainWindow window, ComparisonPane pane, string output, string artifactOutput,
        Action<string, bool, string> check, Action<string> screenshot, Action<Task> pump)
    {
        var menu = window.FrameMenu!;
        var headers = menu.Items.OfType<MenuItem>().Select(item => item.Header as string).ToArray();
        check("frame menu follows WinMerge top-level order", headers.SequenceEqual(ExpectedMenus), string.Join("|", headers));

        var toolbar = window.FrameToolbar!;
        var buttons = toolbar.Children.OfType<Button>().Where(button => button.Name?.StartsWith("frame-toolbar-", StringComparison.Ordinal) == true).ToArray();
        // g_toolbarFileCmp3 のボタン数は 22（区切りを除く）。
        check("frame toolbar has WinMerge three-pane buttons", buttons.Length == 22, buttons.Length.ToString());
        var icons = buttons.Select(button => (button.Content as Image)?.Source as CroppedBitmap).ToArray();
        check("frame toolbar icons are cropped 32px cells of the embedded strip", icons.All(icon => icon is { SourceRect.Width: 32, SourceRect.Height: 31 })
            && icons.Select(icon => icon!.SourceRect.X).Distinct().Count() == icons.Length,
            string.Join(",", icons.Select(icon => icon?.SourceRect.X)));
        var disabled = buttons.Where(button => !button.IsEnabled).Select(button => button.Name).Order().ToArray();
        check("frame toolbar disables only unported commands", disabled.SequenceEqual(new[]
            { "frame-toolbar-AllLeft", "frame-toolbar-AllRight", "frame-toolbar-PrevConflict", "frame-toolbar-SelectLineDiff" }), string.Join("|", disabled));
        check("frame toolbar keeps packaging entry by name", toolbar.Children.OfType<Button>().Count(button => button.Name == "package-comparisons") == 1, "");

        // ツールバーで差分を移動する。差分ブロックが複数ある専用の入力を使う。
        var left = Path.Combine(output, "frame-left.txt"); var right = Path.Combine(output, "frame-right.txt");
        // 先頭付近に変更2件、末尾付近に左だけの行と「変更＋右だけの追加行」を置き、一覧がスクロールする長さにする。
        var middle = Enumerable.Range(6, 180).Select(i => "line" + i).ToArray();
        var tail = Enumerable.Range(186, 3).Select(i => "line" + i).ToArray();
        File.WriteAllText(left, string.Join("\n", new[] { "a", "same", "b", "same", "c" }.Concat(middle).Append("removed-only-left").Concat(tail).Append("tail-left")) + "\n", new UTF8Encoding(false));
        File.WriteAllText(right, string.Join("\n", new[] { "a", "same", "B", "same", "C" }.Concat(middle).Concat(tail).Append("tail-right").Append("added")) + "\n", new UTF8Encoding(false));
        pane.LeftPath.Text = left; pane.RightPath.Text = right;
        pump(pane.ComparePathsAsync());
        var blocks = pane.CurrentDiff?.Blocks;
        check("frame fixture produces multiple difference blocks", blocks is { Count: >= 2 }, blocks?.Count.ToString() ?? "null");
        Click(Button("LastDiff"));
        check("frame toolbar last difference selects final block", pane.DiffList.SelectedIndex == blocks![^1].RowStart, pane.DiffList.SelectedIndex.ToString());
        Click(Button("FirstDiff"));
        check("frame toolbar first difference selects first block", pane.DiffList.SelectedIndex == blocks[0].RowStart, pane.DiffList.SelectedIndex.ToString());
        Click(Button("NextDiff"));
        check("frame toolbar next difference advances", pane.DiffList.SelectedIndex == blocks[1].RowStart, pane.DiffList.SelectedIndex.ToString());
        Click(Button("PrevDiff"));
        check("frame toolbar previous difference returns", pane.DiffList.SelectedIndex == blocks[0].RowStart, pane.DiffList.SelectedIndex.ToString());

        // 本家のアクセラレータ（Alt+End / Alt+Home / Alt+Down）。
        Key(Avalonia.Input.Key.End, KeyModifiers.Alt);
        check("frame Alt+End selects final block", pane.DiffList.SelectedIndex == blocks[^1].RowStart, pane.DiffList.SelectedIndex.ToString());
        Key(Avalonia.Input.Key.Home, KeyModifiers.Alt);
        check("frame Alt+Home selects first block", pane.DiffList.SelectedIndex == blocks[0].RowStart, pane.DiffList.SelectedIndex.ToString());
        Key(Avalonia.Input.Key.Down, KeyModifiers.Alt);
        check("frame Alt+Down advances", pane.DiffList.SelectedIndex == blocks[1].RowStart, pane.DiffList.SelectedIndex.ToString());

        // 本家の差分色: 行のある側は黄（239,203,5）、行の無い側は灰（192,192,192）、選択中の差分ブロックは赤系（239,119,116 / 240,192,192）。
        var yellow = Avalonia.Media.Color.FromRgb(239, 203, 5); var gray = Avalonia.Media.Color.FromRgb(192, 192, 192);
        var red = Avalonia.Media.Color.FromRgb(239, 119, 116); var redMissing = Avalonia.Media.Color.FromRgb(240, 192, 192);
        var rowBrushes = RowColors();
        check("frame unselected difference uses WinMerge yellow", rowBrushes.Contains(yellow), Describe(rowBrushes));
        check("frame selected difference uses WinMerge selected red", rowBrushes.Contains(red) && !rowBrushes.Contains(redMissing), Describe(rowBrushes));
        check("frame default theme is light like WinMerge", Application.Current!.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light, "");

        // Diff ペインは選択行の左右（c / C）を表示する。
        var (diffLeft, diffRight) = pane.DiffPaneTexts;
        check("frame diff pane shows selected row of both sides", DiffPaneText(diffLeft) == "c" && DiffPaneText(diffRight) == "C",
            DiffPaneText(diffLeft) + "|" + DiffPaneText(diffRight));
        // ロケーションペインは全行と選択中のブロックを持つ。
        check("frame location pane covers all aligned rows and current block", pane.Location.RowCount == pane.CurrentDiff!.Rows.Count
            && pane.Location.CurrentBlock == blocks[1].Index, $"{pane.Location.RowCount}/{pane.CurrentDiff.Rows.Count} block={pane.Location.CurrentBlock}");
        // ファイル名の帯には比較したファイルが出る。
        var (leftHeader, rightHeader) = pane.FileHeaderPaths;
        check("frame file header bars show both compared paths", leftHeader == left && rightHeader == right, leftHeader + "|" + rightHeader);

        // 末尾のブロックを選ぶと、行の無い左側は選択時の削除色、一つ前の（左だけの行の）ブロックの右側は通常の灰になる。
        Key(Avalonia.Input.Key.End, KeyModifiers.Alt);
        rowBrushes = RowColors();
        check("frame selected block paints missing side with selected deleted color", rowBrushes.Contains(redMissing), Describe(rowBrushes));
        check("frame unselected block paints missing side with WinMerge deleted gray", rowBrushes.Contains(gray), Describe(rowBrushes));
        check("frame location pane follows last block", pane.Location.CurrentBlock == blocks[^1].Index, pane.Location.CurrentBlock.ToString());

        // ロケーションペインの下端をクリックすると、本文が末尾付近へスクロールし表示範囲の枠も下へ移る。
        Key(Avalonia.Input.Key.Home, KeyModifiers.Alt);
        var viewportBefore = pane.Location.Viewport.Top;
        pane.Location.RequestRowAt(pane.Location.Bounds.Height - 6);
        Dispatcher.UIThread.RunJobs();
        check("frame location pane click scrolls comparison to the clicked area", pane.Location.Viewport.Top > viewportBefore + .5,
            $"{viewportBefore:F3}->{pane.Location.Viewport.Top:F3} height={pane.Location.Viewport.Height:F3}");
        Key(Avalonia.Input.Key.Down, KeyModifiers.Alt);

        // 通常・最小ウィンドウで外枠の高さを記録し、比較本文（タブ本体）が残ることを確認する。
        var bounds = new List<(string Layout, double Width, double Height, double Menu, double Toolbar, double Pane)>();
        foreach (var (layout, width, height) in new[] { ("normal", 1280d, 850d), ("minimum", 850d, 550d) })
        {
            window.Width = width; window.Height = height;
            Dispatcher.UIThread.RunJobs();
            screenshot($"frame-{layout}.png");
            var menuHeight = menu.Bounds.Height; var toolbarHeight = toolbar.Bounds.Height; var paneHeight = pane.Bounds.Height;
            bounds.Add((layout, width, height, menuHeight, toolbarHeight, paneHeight));
            check($"frame {layout} keeps menu and toolbar compact", menuHeight is > 0 and <= 40 && toolbarHeight is > 0 and <= 40,
                $"menu={menuHeight}, toolbar={toolbarHeight}");
            check($"frame {layout} leaves comparison pane most of the window", paneHeight >= height - 160, $"pane={paneHeight}");
        }
        window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs();
        using var file = File.Create(Path.Combine(artifactOutput, "frame-bounds.json"));
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        json.WriteStartArray();
        foreach (var item in bounds)
        {
            json.WriteStartObject(); json.WriteString("layout", item.Layout); json.WriteNumber("windowWidth", item.Width); json.WriteNumber("windowHeight", item.Height);
            json.WriteNumber("menuHeight", item.Menu); json.WriteNumber("toolbarHeight", item.Toolbar); json.WriteNumber("paneHeight", item.Pane); json.WriteEndObject();
        }
        json.WriteEndArray();

        Button Button(string image) => buttons.Single(button => button.Name == "frame-toolbar-" + image);
        Avalonia.Media.Color[] RowColors()
        {
            Dispatcher.UIThread.RunJobs();
            return pane.DiffList.GetVisualDescendants().OfType<Border>().Select(border => border.Background)
                .OfType<Avalonia.Media.ISolidColorBrush>().Select(brush => brush.Color).Distinct().ToArray();
        }
        static string Describe(IEnumerable<Avalonia.Media.Color> colors) => string.Join(",", colors.Select(color => color.ToString()));
        static string DiffPaneText(TextBlock block) => block.Inlines is { Count: > 0 } inlines
            ? string.Concat(inlines.OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text)) : block.Text ?? "";
        static void Click(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
        void Key(Key key, KeyModifiers modifiers)
        {
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
            Dispatcher.UIThread.RunJobs();
        }
    }
}
