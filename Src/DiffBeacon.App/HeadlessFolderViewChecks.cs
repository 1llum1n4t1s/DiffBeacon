using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMerge風のフォルダー比較一覧（列・アイコン・行の色・比較結果の文言・ルートパス帯）の自己検証。
/// 期待値は本家の判定順（GetColImage・GetColors・ColStatusGet）と Japanese.po の訳から固定し、
/// 日時の文字列は実ファイルの時刻から同じ書式で導出する（runnerのタイムゾーンに依存させない）。
/// 通常・最小ウィンドウのPNGとbounds JSONを残す。
/// </summary>
internal static class HeadlessFolderViewChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, string artifactOutput,
        Action<string, bool, string> check, Action<string> screenshot, Action<Task> pump)
    {
        // アイコンは全種類を16pxで復号でき、透明でない画素を持つこと（ICOから16pxの画像だけを取り出している）。
        foreach (var name in FolderIcons.Names)
        {
            var bitmap = FolderIcons.Get(name);
            check($"folder view icon {name} decodes 16px frame", bitmap.PixelSize == new PixelSize(16, 16) && OpaquePixels(bitmap) > 20,
                $"{bitmap.PixelSize} opaque={OpaquePixels(bitmap)}");
        }

        var work = Path.Combine(output, "folder-view"); Directory.CreateDirectory(work);
        var left = Path.Combine(work, "left"); var right = Path.Combine(work, "right");
        Directory.CreateDirectory(Path.Combine(left, "sub")); Directory.CreateDirectory(Path.Combine(right, "sub"));
        Directory.CreateDirectory(Path.Combine(left, "left-dir"));
        Write(left, "same.txt", "same\n"); Write(right, "same.txt", "same\n");
        Write(left, "diff.txt", "left\n"); Write(right, "diff.txt", "right side\n");
        Write(left, "left-only.txt", "left only\n"); Write(right, "right-only.bin", "right only\n");
        Write(left, "sub/inner.txt", "inner left\n"); Write(right, "sub/inner.txt", "inner right\n");
        // 新しい側にだけ "* " が付くことを確かめるため、diff.txt は左を新しくする。
        File.SetLastWriteTimeUtc(Path.Combine(left, "diff.txt"), new DateTime(2026, 11, 13, 3, 4, 5, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(right, "diff.txt"), new DateTime(2026, 11, 12, 3, 4, 5, DateTimeKind.Utc));
        // 日時比較の列の "==" を確かめるため、same.txt は左右を同じ時刻にする。サイズの桁区切りと短縮形は 1234 バイトの左だけのファイルで見る。
        File.SetLastWriteTimeUtc(Path.Combine(left, "same.txt"), new DateTime(2026, 11, 10, 1, 2, 3, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(right, "same.txt"), new DateTime(2026, 11, 10, 1, 2, 3, DateTimeKind.Utc));
        Write(left, "big.dat", new string('x', 1234));

        pane.LeftPath.Text = left; pane.BasePath.Text = ""; pane.RightPath.Text = right;
        pump(pane.ComparePathsAsync());
        Dispatcher.UIThread.RunJobs();
        var list = pane.FolderOptionsRawRows;
        var entries = list.Items.OfType<DirectoryEntry>().ToArray();
        check("folder view lists compared entries", entries.Length >= 6, string.Join("|", entries.Select(entry => entry.RelativePath)));
        ClickMenu(window, "view-show-identical");
        var hiddenIdentical = pane.FolderOptionsRawRows.Items.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).ToArray();
        check("folder view hides identical items", !hiddenIdentical.Contains("same.txt") && hiddenIdentical.Contains("diff.txt") && !window.ShowIdenticalItems,
            string.Join("|", hiddenIdentical));
        ClickMenu(window, "view-show-identical");
        ClickMenu(window, "view-show-unique-left");
        var hiddenLeft = pane.FolderOptionsRawRows.Items.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).ToArray();
        check("folder view hides left-only items", hiddenLeft.Contains("same.txt") && !hiddenLeft.Contains("left-only.txt") && !hiddenLeft.Contains("left-dir") && window.ShowIdenticalItems,
            string.Join("|", hiddenLeft));
        ClickMenu(window, "view-show-unique-left");

        var titles = pane.FolderColumnTitles;
        check("folder view two-way columns follow WinMerge default order",
            titles.SequenceEqual(["名前", "フォルダー", "比較結果", "左更新日時", "右更新日時", "拡張子"]), string.Join("|", titles));
        var roots = pane.FolderRootPathTexts;
        check("folder view root bars show compared folders", roots.Count == 2 && SamePath(roots[0], left) && SamePath(roots[1], right), string.Join("|", roots));

        var separator = Path.DirectorySeparatorChar.ToString();
        Expect("same.txt", "equalfile", Brushes.White, "同一", "." + separator, "txt");
        Expect("diff.txt", "notequalfile", WinMergeColors.FolderDifference, "ファイルは異なっています", "." + separator, "txt");
        Expect("left-only.txt", "lfile", WinMergeColors.FolderMissing, "左側のみ: " + left, "." + separator, "txt");
        Expect("right-only.bin", "rfile", WinMergeColors.FolderMissing, "右側のみ: " + right, "." + separator, "bin");
        Expect("left-dir", "lfolder", WinMergeColors.FolderMissing, "左側のみ: " + left, "." + separator, "");
        Expect("sub/inner.txt", "notequalfile", WinMergeColors.FolderDifference, "ファイルは異なっています", "sub", "txt");
        var subFolder = Entry("sub");
        check("folder view directory rows use folder icons and no extension",
            subFolder is not null && pane.FolderIconName(subFolder).Contains("folder", StringComparison.Ordinal) && pane.FolderCellValue(subFolder, "Extension") == "",
            subFolder is null ? "missing" : pane.FolderIconName(subFolder));

        // 更新日時: 新しい側だけ "* "。文字列は実ファイルの時刻から同じ書式で導出する。
        var diff = Entry("diff.txt")!;
        string Local(string path) => File.GetLastWriteTimeUtc(path).ToLocalTime().ToString("yyyy/MM/dd H:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var leftDate = pane.FolderCellValue(diff, "LeftDate"); var rightDate = pane.FolderCellValue(diff, "RightDate");
        check("folder view marks only the newer date", leftDate == "* " + Local(Path.Combine(left, "diff.txt")) && rightDate == "  " + Local(Path.Combine(right, "diff.txt")),
            leftDate + " / " + rightDate);
        var leftOnly = Entry("left-only.txt")!;
        check("folder view leaves the missing side date empty", pane.FolderCellValue(leftOnly, "RightDate") == "" && pane.FolderCellValue(leftOnly, "LeftDate").StartsWith("* ", StringComparison.Ordinal),
            pane.FolderCellValue(leftOnly, "LeftDate") + " / " + pane.FolderCellValue(leftOnly, "RightDate"));

        // 実際に描画された行: 背景色・アイコン・セルの文字が上の値と一致すること。
        var diffRow = Container(diff);
        var texts = diffRow?.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray() ?? [];
        var icon = diffRow?.GetVisualDescendants().OfType<Image>().FirstOrDefault(image => image.Name == "FolderEntryIcon");
        check("folder view realized row shows name, result, dates and icon", diffRow is not null
            && texts.Contains("diff.txt") && texts.Contains("ファイルは異なっています") && texts.Contains(leftDate) && texts.Contains(rightDate)
            && icon is { Tag: "notequalfile", Source: Bitmap }, string.Join("|", texts));
        list.SelectedItems?.Clear(); Dispatcher.UIThread.RunJobs();
        // 直前の検証のポインターが行の上に残ると表示はホバー色になるため、行そのものの状態色（コンテナーの背景）を見る。
        var unselected = Container(diff)?.Background;
        check("folder view unselected row uses WinMerge difference color", ReferenceEquals(unselected, WinMergeColors.FolderDifference), Describe(unselected));
        list.SelectedItems?.Add(diff); Dispatcher.UIThread.RunJobs();
        var selected = Presenter(Container(diff))?.Background;
        check("folder view selected row is visibly distinct from its state color", selected is ISolidColorBrush { Color: var color } && (color == Color.FromRgb(204, 232, 255) || color == Color.FromRgb(184, 220, 252)),
            Describe(selected));
        list.SelectedItems?.Clear(); Dispatcher.UIThread.RunJobs();

        // 列見出しと行のセル境界が揃い、最小幅でも一覧に横スクロールが出ない（重ね表示のバーが最下行を覆わない）こと。
        window.Width = 850; window.Height = 550; Dispatcher.UIThread.RunJobs();
        var realized = Container(diff);
        var headerGrid = (pane.FolderColumnHeader as Border)?.Child as Grid;
        var headerCells = headerGrid?.Children.OfType<Control>().Select(cell => cell.TranslatePoint(new Point(0, 0), window)?.X ?? -1).ToArray() ?? [];
        var rowGrid = realized?.GetVisualDescendants().OfType<Grid>().FirstOrDefault(grid => grid.ColumnDefinitions.Count == headerCells.Length);
        var rowCells = rowGrid is null ? [] : Enumerable.Range(0, rowGrid.ColumnDefinitions.Count)
            .Select(index => rowGrid.Children.OfType<Control>().First(child => Grid.GetColumn(child) == index))
            .Select(cell => (cell.TranslatePoint(new Point(0, 0), window)?.X ?? -1) - cell.Margin.Left).ToArray();
        check("folder view header and row columns align at minimum width", rowCells.Length == headerCells.Length && headerCells.Length > 0
            && headerCells.Zip(rowCells).Skip(1).All(pair => Math.Abs(pair.First - pair.Second) < 3),
            string.Join(",", headerCells.Select(x => x.ToString("F0"))) + " / " + string.Join(",", rowCells.Select(x => x.ToString("F0"))));
        var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        check("folder view has no horizontal scrolling at minimum width", viewer is not null && viewer.Extent.Width <= viewer.Viewport.Width + .5,
            $"extent={viewer?.Extent.Width} viewport={viewer?.Viewport.Width}");
        window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs();

        // 通常・最小ウィンドウで、列見出しが一覧の直上にあり、一覧本体が100 DIP以上残ること。
        var bounds = new List<(string Layout, double Width, double Height, Rect Header, Rect List)>();
        foreach (var (layout, width, height) in new[] { ("normal", 1280d, 850d), ("minimum", 850d, 550d) })
        {
            window.Width = width; window.Height = height; Dispatcher.UIThread.RunJobs();
            screenshot($"folder-view-{layout}.png");
            var header = Within(pane.FolderColumnHeader, window); var body = Within(list, window);
            bounds.Add((layout, width, height, header, body));
            check($"folder view {layout} keeps header directly above a usable list",
                header.Height > 10 && body.Height >= 100 && Math.Abs(header.Bottom - body.Top) <= 1, $"header={header} list={body}");
        }
        window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs();

        // 見出しのクリックで並べ替え、境界のドラッグで列幅を変える。比較結果・選択は変えない。
        {
            string[] Top() => list.Items.OfType<DirectoryEntry>().Where(entry => !entry.RelativePath.Contains('/') && !entry.RelativePath.Contains((char)92)).Select(entry => entry.RelativePath).ToArray();
            var before = list.Items.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            var diffEntry = list.Items.OfType<DirectoryEntry>().First(entry => entry.RelativePath == "diff.txt");
            list.SelectedItems?.Clear(); list.SelectedItems?.Add(diffEntry); Dispatcher.UIThread.RunJobs();
            var isDir = (string path) => list.Items.OfType<DirectoryEntry>().First(entry => entry.RelativePath == path).EntryKind == DirectoryEntryKind.Directory;

            pane.SortFolderBy("Name"); Dispatcher.UIThread.RunJobs();
            var asc = Top();
            var dirsFirst = asc.SkipWhile(isDir).All(path => !isDir(path));
            var namesAsc = asc.Where(isDir).SequenceEqual(asc.Where(isDir).OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
                && asc.Where(path => !isDir(path)).SequenceEqual(asc.Where(path => !isDir(path)).OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase));
            check("folder view name sort puts folders first then ascending names", dirsFirst && namesAsc && pane.FolderSortColumnName == "Name" && !pane.FolderSortDescending, string.Join("|", asc));
            check("folder view sort marks the header with an arrow", pane.FolderColumnTitles[0] == "名前 ▲" && pane.FolderColumnTitles[1] == "フォルダー", string.Join("|", pane.FolderColumnTitles));
            check("folder view sort keeps the selection and all entries",
                list.SelectedItems?.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).SequenceEqual(["diff.txt"]) == true
                && list.Items.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).OrderBy(path => path, StringComparer.Ordinal).SequenceEqual(before), string.Join("|", asc));

            pane.SortFolderBy("Name"); Dispatcher.UIThread.RunJobs();
            var desc = Top();
            check("folder view clicking the same header again reverses within groups", pane.FolderSortDescending && pane.FolderColumnTitles[0] == "名前 ▼"
                && desc.Where(isDir).SequenceEqual(asc.Where(isDir).Reverse()) && desc.Where(path => !isDir(path)).SequenceEqual(asc.Where(path => !isDir(path)).Reverse()), string.Join("|", desc));

            pane.SortFolderBy("LeftDate"); Dispatcher.UIThread.RunJobs();
            var byLeft = list.Items.OfType<DirectoryEntry>().Where(entry => !entry.RelativePath.Contains('/') && !entry.RelativePath.Contains((char)92)).Select(entry => entry.LeftState?.LastWriteTimeUtc ?? DateTime.MinValue).ToArray();
            check("folder view date sort orders by real timestamps ascending, missing side first",
                byLeft.SequenceEqual(byLeft.OrderBy(time => time)) && pane.FolderColumnTitles[0] == "名前" && pane.FolderColumnTitles[3] == "左更新日時 ▲", string.Join("|", pane.FolderColumnTitles));

            // 列幅: 境界の左右2列の間で幅を移し、合計と最小幅を守る。見出しと表示中の行が同じ幅になる。
            pane.ClearFolderSortForTest(); Dispatcher.UIThread.RunJobs();
            window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs();
            var start = pane.FolderColumnWidths.ToArray();
            pane.ResizeFolderBoundaryForTest(1, 30); Dispatcher.UIThread.RunJobs();
            var moved = pane.FolderColumnWidths.ToArray();
            check("folder view dragging a boundary moves width between its two columns",
                Math.Abs(moved[1] - start[1] - 30) < 1.5 && Math.Abs(moved[2] - start[2] + 30) < 1.5 && Math.Abs(moved.Sum() - start.Sum()) < 1.5
                && Enumerable.Range(0, start.Length).Where(i => i != 1 && i != 2).All(i => Math.Abs(moved[i] - start[i]) < 1.5),
                string.Join(",", start.Select(x => x.ToString("F0"))) + " -> " + string.Join(",", moved.Select(x => x.ToString("F0"))));
            var rowAfter = list.GetVisualDescendants().OfType<Grid>().FirstOrDefault(grid => grid.Name == "folder-data-row");
            check("folder view row columns follow the resized header", rowAfter is not null
                && Enumerable.Range(0, moved.Length).All(i => Math.Abs(rowAfter.ColumnDefinitions[i].ActualWidth - moved[i]) < 1.5),
                string.Join(",", rowAfter?.ColumnDefinitions.Select(c => c.ActualWidth.ToString("F0")) ?? []));
            pane.ResizeFolderBoundaryForTest(1, 100000); Dispatcher.UIThread.RunJobs();
            var clamped = pane.FolderColumnWidths.ToArray();
            check("folder view column width never drops below the minimum", clamped[2] >= 39.5 && clamped.All(width => width >= 39.5) && Math.Abs(clamped.Sum() - start.Sum()) < 1.5,
                string.Join(",", clamped.Select(x => x.ToString("F0"))));
            window.Width = 850; window.Height = 550; Dispatcher.UIThread.RunJobs();
            var viewerAfter = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            check("folder view resized columns still fit without horizontal scrolling", viewerAfter is not null && viewerAfter.Extent.Width <= viewerAfter.Viewport.Width + .5,
                $"extent={viewerAfter?.Extent.Width} viewport={viewerAfter?.Viewport.Width}");
            window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs();
            list.SelectedItems?.Clear(); Dispatcher.UIThread.RunJobs();
        }

        CheckColumnCustomization(window, pane, work, list, check, screenshot, pump, entries);

        // 三者比較: 中更新日時の列と、欠落側・同一側の文言とアイコン。
        var three = Path.Combine(output, "folder-view-3"); var l3 = Path.Combine(three, "left"); var m3 = Path.Combine(three, "middle"); var r3 = Path.Combine(three, "right");
        foreach (var root in new[] { l3, m3, r3 }) Directory.CreateDirectory(root);
        Write(l3, "a.txt", "left changed\n"); Write(m3, "a.txt", "base\n"); Write(r3, "a.txt", "base\n");
        Write(l3, "b.txt", "b\n"); Write(r3, "b.txt", "b\n");
        // 三者の日時比較（新しい順に L・M・R を並べる）を確かめるため、時刻を固定する。
        File.SetLastWriteTimeUtc(Path.Combine(l3, "a.txt"), new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(m3, "a.txt"), new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(r3, "a.txt"), new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(l3, "b.txt"), new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(r3, "b.txt"), new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc));
        pane.LeftPath.Text = l3; pane.BasePath.Text = m3; pane.RightPath.Text = r3;
        pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
        entries = list.Items.OfType<DirectoryEntry>().ToArray();
        titles = pane.FolderColumnTitles;
        check("folder view three-way adds middle date column", titles.SequenceEqual(["名前", "フォルダー", "比較結果", "左更新日時", "中更新日時", "右更新日時", "拡張子"]),
            string.Join("|", titles));
        CheckThreeWayColumns(pane, list, check);
        roots = pane.FolderRootPathTexts;
        check("folder view three-way root bars show three folders", roots.Count == 3 && SamePath(roots[1], m3), string.Join("|", roots));
        var a = Entry("a.txt"); var b = Entry("b.txt");
        check("folder view three-way names the identical pair", a is not null && pane.FolderCellValue(a, "Result") == "ファイルは異なっています (中央と右側は同一です)"
            && pane.FolderIconName(a) == "notequalfile", a is null ? "missing" : pane.FolderCellValue(a, "Result"));
        check("folder view three-way reports the missing middle", b is not null && pane.FolderCellValue(b, "Result").StartsWith(m3 + " 内のみ存在しません", StringComparison.Ordinal)
            && pane.FolderIconName(b) == "mmissing" && ReferenceEquals(pane.FolderRowBrush(b), WinMergeColors.FolderMissing),
            b is null ? "missing" : pane.FolderCellValue(b, "Result") + " / " + pane.FolderIconName(b));
        screenshot("folder-view-threeway.png");

        using var file = File.Create(Path.Combine(artifactOutput, "folder-view-bounds.json"));
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        json.WriteStartArray();
        foreach (var item in bounds)
        {
            json.WriteStartObject(); json.WriteString("layout", item.Layout); json.WriteNumber("windowWidth", item.Width); json.WriteNumber("windowHeight", item.Height);
            json.WriteNumber("headerTop", item.Header.Top); json.WriteNumber("headerHeight", item.Header.Height);
            json.WriteNumber("listTop", item.List.Top); json.WriteNumber("listHeight", item.List.Height); json.WriteEndObject();
        }
        json.WriteEndArray();

        DirectoryEntry? Entry(string path) => entries.FirstOrDefault(entry => entry.RelativePath.Replace('\\', '/') == path);
        ListBoxItem? Container(DirectoryEntry entry)
        {
            list.ScrollIntoView(entry); Dispatcher.UIThread.RunJobs();
            return list.ContainerFromItem(entry) as ListBoxItem;
        }
        void Expect(string path, string iconName, IBrush brush, string result, string folder, string extension)
        {
            var entry = Entry(path);
            if (entry is null) { check($"folder view row {path}", false, "missing"); return; }
            var actual = (Icon: pane.FolderIconName(entry), Brush: pane.FolderRowBrush(entry), Result: pane.FolderCellValue(entry, "Result"),
                Folder: pane.FolderCellValue(entry, "Folder"), Extension: pane.FolderCellValue(entry, "Extension"));
            check($"folder view row {path} matches WinMerge icon, color and result",
                actual.Icon == iconName && ReferenceEquals(actual.Brush, brush) && actual.Result == result && actual.Folder == folder && actual.Extension == extension,
                $"{actual.Icon} {Describe(actual.Brush)} {actual.Result} [{actual.Folder}] [{actual.Extension}]");
        }
    }

    /// <summary>
    /// 段階9: 追加の列（サイズ・短縮サイズ・日時比較・簡易の比較結果・属性）、「列の表示」ダイアログ（ツールメニューと見出しの右クリック）、
    /// 表示する列・幅・並べ替えの保存と読み直し、最小ウィンドウで全列を出しても横スクロールが出ないこと、設定ファイルの往復と不正な設定の拒否。
    /// 終わったら二者・三者とも既定の列へ戻し、後に続く検証へ影響させない。
    /// </summary>
    private static void CheckColumnCustomization(MainWindow window, ComparisonPane pane, string work, ListBox list,
        Action<string, bool, string> check, Action<string> screenshot, Action<Task> pump, DirectoryEntry[] entries)
    {
        DirectoryEntry? Entry(string path) => list.Items.OfType<DirectoryEntry>().FirstOrDefault(entry => entry.RelativePath.Replace('\\', '/') == path)
            ?? entries.FirstOrDefault(entry => entry.RelativePath.Replace('\\', '/') == path);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // 追加の列の値（本家 ColSizeGet・ColSizeShortGet・ColNewerGet・ColStatusAbbrGet・ColAttrGet）。
            var diff = Entry("diff.txt")!; var same = Entry("same.txt")!; var leftOnly = Entry("left-only.txt")!;
            var rightOnly = Entry("right-only.bin")!; var sub = Entry("sub")!; var big = Entry("big.dat");
            string V(DirectoryEntry entry, string column) => pane.FolderCellValue(entry, column);
            check("folder columns size shows bytes with grouping and leaves folders and missing sides empty",
                big is not null && V(diff, "LeftSize") == "5" && V(diff, "RightSize") == "11" && V(big, "LeftSize") == 1234.ToString("N0", culture)
                && V(sub, "LeftSize") == "" && V(leftOnly, "RightSize") == "" && V(rightOnly, "LeftSize") == "",
                big is null ? "big.dat missing" : $"{V(diff, "LeftSize")}|{V(diff, "RightSize")}|{V(big, "LeftSize")}|{V(sub, "LeftSize")}");
            check("folder columns short size uses B below 1024 and three significant digits above",
                V(diff, "LeftSizeShort") == "5 B" && big is not null && V(big, "LeftSizeShort") == 1.20m.ToString("F2", culture) + " KB" && V(sub, "LeftSizeShort") == "",
                V(diff, "LeftSizeShort") + "|" + (big is null ? "" : V(big, "LeftSizeShort")));
            var shortCases = new (long Size, string Expected)[]
            {
                (0, "0 B"), (1023, "1023 B"), (1024, 1.00m.ToString("F2", culture) + " KB"), (1205, 1.17m.ToString("F2", culture) + " KB"),
                (12000, 11.7m.ToString("F1", culture) + " KB"), (200000, "195 KB"), (1048576, 1.00m.ToString("F2", culture) + " MB"),
                (1572864, 1.50m.ToString("F2", culture) + " MB"), (5L * 1024 * 1024 * 1024 * 1024, 5.00m.ToString("F2", culture) + " TB"),
                // 以下は Windows 11 の StrFormatByteSizeW の実測値（切り捨てと、1000 以上での次の単位への繰り上がり）。
                (1535, 1.49m.ToString("F2", culture) + " KB"), (1023999, "999 KB"), (1047552, 0.99m.ToString("F2", culture) + " MB"),
                (1048575, 0.99m.ToString("F2", culture) + " MB"), (10485759, 9.99m.ToString("F2", culture) + " MB"),
                (104857599, 99.9m.ToString("F1", culture) + " MB"), (1073741823, 0.99m.ToString("F2", culture) + " GB"),
            };
            var shortActual = shortCases.Select(item => FolderColumnCatalog.ShortSize(item.Size)).ToArray();
            check("folder columns short size truncates like StrFormatByteSize64", shortCases.Select(item => item.Expected).SequenceEqual(shortActual), string.Join("|", shortActual));
            check("folder columns newer file follows ColNewerGet arrows",
                V(diff, "Newer") == "<-" && V(same, "Newer") == "==" && V(leftOnly, "Newer") == "<--" && V(rightOnly, "Newer") == "-->",
                $"{V(diff, "Newer")}|{V(same, "Newer")}|{V(leftOnly, "Newer")}|{V(rightOnly, "Newer")}");
            check("folder columns short result uses WinMerge short wording",
                V(same, "ResultAbbr") == "同一" && V(diff, "ResultAbbr") == "差異あり" && V(leftOnly, "ResultAbbr") == "左側のみ" && V(rightOnly, "ResultAbbr") == "右側のみ",
                $"{V(same, "ResultAbbr")}|{V(diff, "ResultAbbr")}|{V(leftOnly, "ResultAbbr")}|{V(rightOnly, "ResultAbbr")}");
            var attributePattern = new System.Text.RegularExpressions.Regex("^R?H?S?A?$");
            check("folder columns attributes use R/H/S/A order and stay empty on the missing side",
                attributePattern.IsMatch(V(diff, "LeftAttributes")) && attributePattern.IsMatch(V(diff, "RightAttributes")) && V(leftOnly, "RightAttributes") == ""
                && FolderColumnCatalog.Attributes(FileAttributes.Archive | FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden) == "RHSA"
                && FolderColumnCatalog.Attributes(FileAttributes.Normal) == "",
                V(diff, "LeftAttributes") + "|" + V(diff, "RightAttributes"));

            // 列見出しの右クリックメニュー（本家 _HDR_POPUP_ の「列の設定(&C)...」）。
            var headerMenu = pane.FolderColumnHeader.ContextMenu?.ItemsSource?.OfType<MenuItem>().Select(item => item.Header as string).ToArray() ?? [];
            check("folder columns header context menu offers customize columns", headerMenu.SequenceEqual(["列の設定(_C)..."]), string.Join("|", headerMenu));

            // ダイアログの初期状態とキャンセル（ツールメニューと同じ外枠のコマンドから開く）。
            var defaultsTwoWay = FolderColumnCatalog.DefaultTwoWay.Select(column => column.ToString()).ToArray();
            (FolderColumn Column, bool Checked)[] initial = [];
            string description = ""; bool upAtTop = true, downAtTop = false, focusSelected = false;
            FolderColumnsDialog.Shown = dialog =>
            {
                initial = dialog.Items.ToArray(); description = dialog.DescriptionText;
                upAtTop = dialog.UpButton.IsEnabled; downAtTop = dialog.DownButton.IsEnabled;
                dialog.ItemBox(FolderColumn.Folder).Focus(); Dispatcher.UIThread.RunJobs();
                focusSelected = dialog.SelectedColumn == FolderColumn.Folder && dialog.UpButton.IsEnabled;
                Click(dialog.CancelButton);
            };
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.CustomizeColumns)); Dispatcher.UIThread.RunJobs();
            var expectedInitial = FolderColumnCatalog.DefaultTwoWay.Select(column => (column, true))
                .Concat(FolderColumnCatalog.TwoWay.Select(info => info.Column).Where(column => !FolderColumnCatalog.DefaultTwoWay.Contains(column)).Select(column => (column, false))).ToArray();
            check("folder columns dialog lists shown columns first then hidden ones in WinMerge order", initial.SequenceEqual(expectedInitial),
                string.Join("|", initial.Select(item => item.Column + (item.Checked ? "+" : "-"))));
            check("folder columns dialog selects the first column with its description and disables up",
                description == "ファイル名またはフォルダー名です。" && !upAtTop && downAtTop, description + " up=" + upAtTop + " down=" + downAtTop);
            check("folder columns dialog selects the row whose checkbox gets keyboard focus", focusSelected, "");
            check("folder columns cancel keeps the columns", pane.FolderVisibleColumnNames.SequenceEqual(defaultsTwoWay), string.Join("|", pane.FolderVisibleColumnNames));

            // チェックの変更・上へ・説明の追従・OK の無効化を、実際のチェックボックスとボタンで行って OK する。
            string extensionDescription = ""; bool okWithoutChecks = true, downAtBottom = true;
            FolderColumnsDialog.Shown = dialog =>
            {
                dialog.Select(FolderColumn.Extension); extensionDescription = dialog.DescriptionText;
                foreach (var (column, _) in dialog.Items) dialog.ItemBox(column).IsChecked = false;
                okWithoutChecks = dialog.OkButton.IsEnabled;
                foreach (var column in new[] { FolderColumn.Name, FolderColumn.Result, FolderColumn.LeftDate, FolderColumn.LeftSize, FolderColumn.RightSize, FolderColumn.ResultAbbr })
                    dialog.ItemBox(column).IsChecked = true;
                dialog.Select(FolderColumn.LeftSize); Click(dialog.UpButton); Click(dialog.UpButton);
                dialog.Select(dialog.Items[^1].Column); downAtBottom = dialog.DownButton.IsEnabled;
                Click(dialog.OkButton);
            };
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.CustomizeColumns)); Dispatcher.UIThread.RunJobs();
            string[] chosen = ["Name", "Result", "LeftDate", "LeftSize", "RightSize", "ResultAbbr"];
            check("folder columns dialog description follows the selection", extensionDescription == "ファイルの拡張子です。", extensionDescription);
            check("folder columns dialog disables OK without checked columns and down at the end", !okWithoutChecks && !downAtBottom, "ok=" + okWithoutChecks + " down=" + downAtBottom);
            check("folder columns OK shows the checked columns in the dialog order", pane.FolderVisibleColumnNames.SequenceEqual(chosen)
                && pane.FolderColumnTitles.SequenceEqual(["名前", "比較結果", "左更新日時", "左サイズ", "右サイズ", "比較結果(簡易)"]),
                string.Join("|", pane.FolderColumnTitles));
            var saved = window.FolderColumnOptions.Layout(false);
            check("folder columns OK saves the layout with relative widths", saved is not null && saved.Columns.SequenceEqual(chosen)
                && saved.Widths is { Length: 6 } && pane.FolderColumnsUseWeights, saved is null ? "null" : string.Join("|", saved.Columns) + " widths=" + saved.Widths?.Length);
            list.ScrollIntoView(diff); Dispatcher.UIThread.RunJobs();
            var rowTexts = (list.ContainerFromItem(diff) as ListBoxItem)?.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray() ?? [];
            var headerCount = ((pane.FolderColumnHeader as Border)?.Child as Grid)?.ColumnDefinitions.Count ?? 0;
            var rowGrid = (list.ContainerFromItem(diff) as ListBoxItem)?.GetVisualDescendants().OfType<Grid>().FirstOrDefault(grid => grid.Name == "folder-data-row");
            check("folder columns realized rows are rebuilt with the new columns", rowGrid?.ColumnDefinitions.Count == 6 && headerCount == 6
                && rowTexts.Contains("5") && rowTexts.Contains("11") && rowTexts.Contains("差異あり") && !rowTexts.Contains("txt"), string.Join("|", rowTexts));

            // 新しく開いたペイン（保存した設定を読み直す）でも同じ列になる。
            pane.ReloadFolderColumnsForTest(); Dispatcher.UIThread.RunJobs();
            check("folder columns saved layout is read back", pane.FolderVisibleColumnNames.SequenceEqual(chosen) && pane.FolderColumnsUseWeights,
                string.Join("|", pane.FolderVisibleColumnNames));

            // サイズで並べ替え（バイト数の順、無い側が先頭）、その列を表示しなくすると並べ替えを解除する（本家 OnEditColumns）。
            pane.SortFolderBy("LeftSize"); Dispatcher.UIThread.RunJobs();
            var sizes = list.Items.OfType<DirectoryEntry>().Where(entry => !entry.RelativePath.Contains('/') && !entry.RelativePath.Contains((char)92))
                .Select(entry => entry.LeftState is { Kind: not DirectoryEntryKind.Directory } state ? state.Size : long.MinValue).ToArray();
            check("folder columns size sort orders by bytes with missing sides first", sizes.SequenceEqual(sizes.OrderBy(size => size))
                && pane.FolderSortColumnName == "LeftSize" && window.FolderColumnOptions.Layout(false)?.SortColumn == "LeftSize", string.Join(",", sizes));
            FolderColumnsDialog.Shown = dialog => { dialog.ItemBox(FolderColumn.LeftSize).IsChecked = false; Click(dialog.OkButton); };
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.CustomizeColumns)); Dispatcher.UIThread.RunJobs();
            check("folder columns hiding the sorted column clears the sort", pane.FolderSortColumnName is null && !pane.FolderVisibleColumnNames.Contains("LeftSize")
                && window.FolderColumnOptions.Layout(false)?.SortColumn is null, pane.FolderSortColumnName ?? "none");

            // 全列を表示しても、最小ウィンドウで見出しと行がそろい、横スクロールが出ない。
            FolderColumnsDialog.Shown = dialog => { foreach (var (column, _) in dialog.Items) dialog.ItemBox(column).IsChecked = true; Click(dialog.OkButton); };
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.CustomizeColumns)); Dispatcher.UIThread.RunJobs();
            window.Width = 850; window.Height = 550; Dispatcher.UIThread.RunJobs();
            list.ScrollIntoView(diff); Dispatcher.UIThread.RunJobs();
            var headerGrid = (pane.FolderColumnHeader as Border)?.Child as Grid;
            var headerCells = headerGrid?.Children.OfType<Control>().Select(cell => cell.TranslatePoint(new Point(0, 0), window)?.X ?? -1).ToArray() ?? [];
            var allRow = (list.ContainerFromItem(diff) as ListBoxItem)?.GetVisualDescendants().OfType<Grid>().FirstOrDefault(grid => grid.Name == "folder-data-row");
            var rowCells = allRow is null ? [] : Enumerable.Range(0, allRow.ColumnDefinitions.Count)
                .Select(index => allRow.Children.OfType<Control>().First(child => Grid.GetColumn(child) == index))
                .Select(cell => (cell.TranslatePoint(new Point(0, 0), window)?.X ?? -1) - cell.Margin.Left).ToArray();
            var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            check("folder columns all columns fit the minimum window without horizontal scrolling",
                pane.FolderVisibleColumnNames.Count == FolderColumnCatalog.TwoWay.Length && headerCells.Length == FolderColumnCatalog.TwoWay.Length
                && viewer is not null && viewer.Extent.Width <= viewer.Viewport.Width + .5 && pane.FolderColumnWidths.All(width => width >= 39.5),
                $"columns={headerCells.Length} extent={viewer?.Extent.Width} viewport={viewer?.Viewport.Width} widths={string.Join(",", pane.FolderColumnWidths.Select(x => x.ToString("F0")))}");
            // 右寄せの列（サイズ）はセルの左端がずれるため、左端の位置ではなく列の境界（列の定義の幅）で照合する。
            var headerWidths = headerGrid?.ColumnDefinitions.Select(column => column.ActualWidth).ToArray() ?? [];
            var rowWidths = allRow?.ColumnDefinitions.Select(column => column.ActualWidth).ToArray() ?? [];
            check("folder columns all columns keep header and row boundaries aligned", rowCells.Length == headerCells.Length
                && headerWidths.Length == rowWidths.Length && headerWidths.Zip(rowWidths).All(pair => Math.Abs(pair.First - pair.Second) < 1.5),
                string.Join(",", headerWidths.Select(x => x.ToString("F0"))) + " / " + string.Join(",", rowWidths.Select(x => x.ToString("F0"))));
            screenshot("folder-view-all-columns-minimum.png");
            window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs();

            // デフォルトで既定の列と既定の幅へ戻し、保存も既定（何も書かない）へ戻す。
            FolderColumnsDialog.Shown = dialog => { Click(dialog.DefaultsButton); Click(dialog.OkButton); };
            pump(pane.ExecuteFrameCommandAsync(FrameCommand.CustomizeColumns)); Dispatcher.UIThread.RunJobs();
            check("folder columns defaults restore the default columns and widths", pane.FolderVisibleColumnNames.SequenceEqual(defaultsTwoWay)
                && !pane.FolderColumnsUseWeights && window.FolderColumnOptions.Layout(false) is null
                && pane.FolderColumnTitles.SequenceEqual(["名前", "フォルダー", "比較結果", "左更新日時", "右更新日時", "拡張子"]), string.Join("|", pane.FolderColumnTitles));

            // 出力保護に当たる保存先へは書かず、記憶している値も変えない。
            var blocked = new FolderColumnOptionsStore(Path.Combine(work, "blocked-columns.json"));
            blocked.AddOutputGuard(_ => throw new InvalidOperationException("protected"));
            check("folder columns store refuses a protected output without writing",
                !blocked.SetLayout(false, new FolderColumnLayoutOptions { Columns = ["Name"] }) && blocked.Diagnostic is not null
                && !File.Exists(Path.Combine(work, "blocked-columns.json")) && blocked.Layout(false) is null, blocked.Diagnostic ?? "");

            CheckColumnStoreFile(work, check);
            CheckStartupWindowLoadsSavedColumns(work, check, pump);
        }
        finally
        {
            FolderColumnsDialog.Shown = null;
            pane.ResetFolderColumnsForTest();
            window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>folder-columns.json の往復（二者・三者を別に保存）と、不正な設定の拒否（既定の列で表示し、ファイルは変えない）。</summary>
    private static void CheckColumnStoreFile(string work, Action<string, bool, string> check)
    {
        var path = Path.Combine(work, "folder-columns.json");
        var store = new FolderColumnOptionsStore(path);
        var two = new FolderColumnLayoutOptions { Columns = ["Name", "LeftSize", "Newer"], Widths = [210, 90.5, 70], SortColumn = "LeftSize", SortDescending = true };
        var three = new FolderColumnLayoutOptions { Columns = ["Name", "MiddleSize", "MiddleAttributes"] };
        var saved = store.SetLayout(false, two) && store.SetLayout(true, three);
        var reopened = new FolderColumnOptionsStore(path);
        var a = reopened.Layout(false); var b = reopened.Layout(true);
        check("folder columns settings file round-trips two-way and three-way layouts", saved && reopened.Diagnostic is null
            && a is not null && a.Columns.SequenceEqual(two.Columns) && a.Widths!.SequenceEqual(two.Widths!) && a.SortColumn == "LeftSize" && a.SortDescending
            && b is not null && b.Columns.SequenceEqual(three.Columns) && b.Widths is null && b.SortColumn is null,
            File.Exists(path) ? File.ReadAllText(path) : "missing");
        var invalid = new[]
        {
            "null", "{\"twoWay\":{\"columns\":[]}}", "{\"twoWay\":{\"columns\":[\"Unknown\"]}}", "{\"twoWay\":{\"columns\":[\"MiddleDate\"]}}",
            "{\"twoWay\":{\"columns\":[\"0\"]}}", "{\"twoWay\":{\"columns\":[\"Name\",\"Name\"]}}", "{\"twoWay\":{\"columns\":[\"Name\"],\"widths\":[1,2]}}",
            "{\"twoWay\":{\"columns\":[\"Name\"],\"widths\":[-1]}}", "{\"twoWay\":{\"columns\":[\"Name\"],\"sortColumn\":\"Folder\"}}",
            "{\"threeWay\":{\"columns\":[\"Name\"]},\"other\":1}", new string(' ', FolderColumnOptionsStore.MaximumBytes + 1),
        };
        var index = 0;
        foreach (var text in invalid)
        {
            var bad = Path.Combine(work, $"folder-columns-invalid-{index++}.json");
            File.WriteAllText(bad, text, new UTF8Encoding(false));
            var before = File.ReadAllBytes(bad);
            var badStore = new FolderColumnOptionsStore(bad);
            check($"folder columns invalid settings {index} fall back to defaults and keep the file",
                badStore.Diagnostic is not null && badStore.Layout(false) is null && badStore.Layout(true) is null && before.SequenceEqual(File.ReadAllBytes(bad)),
                (text.Length > 80 ? text[..80] : text) + " => " + badStore.Diagnostic);
        }
        check("folder columns store rejects invalid layouts before writing",
            !store.SetLayout(false, new FolderColumnLayoutOptions { Columns = ["Name", "MiddleSize"] }) && store.Layout(false)!.Columns.SequenceEqual(two.Columns)
            && new FolderColumnOptionsStore(path).Layout(false)!.Columns.SequenceEqual(two.Columns), store.Diagnostic ?? "");
    }

    /// <summary>
    /// 起動の経路: 保存した配置のファイルを持つ store でウィンドウを作ると、コンストラクターの中で作られる最初のタブが
    /// その配置（列・相対幅・並べ替え）で一覧を出し、並べ替えをしても保存した列を既定の列で上書きしないこと。
    /// </summary>
    private static void CheckStartupWindowLoadsSavedColumns(string work, Action<string, bool, string> check, Action<Task> pump)
    {
        var path = Path.Combine(work, "startup-columns.json");
        string[] columns = ["Name", "RightSize", "Newer", "Result"];
        var seeded = new FolderColumnOptionsStore(path).SetLayout(false,
            new FolderColumnLayoutOptions { Columns = columns, Widths = [200, 80, 60, 220], SortColumn = "RightSize", SortDescending = true });
        var startup = new MainWindow(null, new ImageApplicationOptionsStore(), new FolderApplicationOptionsStore(), new TableApplicationOptionsStore(),
            new FolderColumnOptionsStore(path)) { Width = 1280, Height = 850 };
        startup.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var first = startup.SessionPanes[0];
            first.LeftPath.Text = Path.Combine(work, "left"); first.BasePath.Text = ""; first.RightPath.Text = Path.Combine(work, "right");
            pump(first.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
            check("folder columns first tab of a new window uses the saved layout", seeded && first.FolderVisibleColumnNames.SequenceEqual(columns)
                && first.FolderColumnsUseWeights && first.FolderSortColumnName == "RightSize" && first.FolderSortDescending
                && first.FolderColumnTitles.SequenceEqual(["名前", "右サイズ ▼", "日時比較", "比較結果"]), string.Join("|", first.FolderColumnTitles));
            first.SortFolderBy("Name"); Dispatcher.UIThread.RunJobs();
            var reread = new FolderColumnOptionsStore(path).Layout(false);
            check("folder columns sorting in the first tab keeps the saved columns", reread is not null && reread.Columns.SequenceEqual(columns)
                && reread.SortColumn == "Name" && !reread.SortDescending && reread.Widths is { Length: 4 },
                reread is null ? "null" : string.Join("|", reread.Columns) + " sort=" + reread.SortColumn);
        }
        finally
        {
            foreach (var session in startup.SessionPanes) session.DiscardChanges();
            startup.Close(); Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>三者比較の列（中サイズ・中属性・三者の日時比較・簡易の比較結果）と、二者とは別の配置。</summary>
    private static void CheckThreeWayColumns(ComparisonPane pane, ListBox list, Action<string, bool, string> check)
    {
        DirectoryEntry? Entry(string path) => list.Items.OfType<DirectoryEntry>().FirstOrDefault(entry => entry.RelativePath.Replace('\\', '/') == path);
        var a = Entry("a.txt"); var b = Entry("b.txt");
        if (a is null || b is null) { check("folder columns three-way entries", false, "missing"); return; }
        check("folder columns three-way newer file orders sides like ColNewerGet",
            pane.FolderCellValue(a, "Newer") == "L<-R<-M" && pane.FolderCellValue(b, "Newer") == "L==R<-M",
            pane.FolderCellValue(a, "Newer") + "|" + pane.FolderCellValue(b, "Newer"));
        check("folder columns three-way short result and middle size",
            pane.FolderCellValue(a, "ResultAbbr") == "差異あり" && pane.FolderCellValue(b, "ResultAbbr") == "中央にない"
            && pane.FolderCellValue(a, "MiddleSize") == "5" && pane.FolderCellValue(b, "MiddleSize") == "" && pane.FolderCellValue(b, "MiddleAttributes") == "",
            pane.FolderCellValue(a, "ResultAbbr") + "|" + pane.FolderCellValue(b, "ResultAbbr") + "|" + pane.FolderCellValue(a, "MiddleSize"));
        try
        {
            var applied = pane.ApplyFolderColumnChoice(true, [FolderColumn.Name, FolderColumn.Newer, FolderColumn.MiddleSize, FolderColumn.ResultAbbr], reset: false);
            Dispatcher.UIThread.RunJobs();
            check("folder columns three-way layout is separate from two-way", applied
                && pane.FolderColumnTitles.SequenceEqual(["名前", "日時比較", "中サイズ", "比較結果(簡易)"])
                && pane.FolderColumnLayoutForTest(false).SequenceEqual(FolderColumnCatalog.DefaultTwoWay.Select(column => column.ToString())),
                string.Join("|", pane.FolderColumnTitles));
            check("folder columns three-way refuses two-way-only checks", !pane.ApplyFolderColumnChoice(false, [FolderColumn.MiddleSize], reset: false), "");
        }
        finally { pane.ResetFolderColumnsForTest(); Dispatcher.UIThread.RunJobs(); }
    }

    private static void Click(Button button) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    /// <summary>表示メニューのチェック項目を押す。</summary>
    private static void ClickMenu(MainWindow window, string name)
    {
        var view = window.FrameMenu!.Items.OfType<MenuItem>().First(item => item.Header as string == "表示(_V)");
        var item = view.Items.OfType<MenuItem>().Single(menu => menu.Name == "frame-menu-" + name);
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static Rect Within(Control control, Visual root)
        => new(control.TranslatePoint(new Point(0, 0), root) ?? default, control.Bounds.Size);

    private static void Write(string root, string relative, string text)
        => File.WriteAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), text, new UTF8Encoding(false));

    private static bool SamePath(string actual, string expected)
        => string.Equals(Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static ContentPresenter? Presenter(ListBoxItem? item)
        => item?.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(presenter => presenter.Name == "PART_ContentPresenter");

    private static string Describe(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color.ToString() : brush?.GetType().Name ?? "null";

    private static int OpaquePixels(Bitmap bitmap)
    {
        var size = bitmap.PixelSize; var stride = size.Width * 4; var buffer = Marshal.AllocHGlobal(stride * size.Height);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), buffer, stride * size.Height, stride);
            var bytes = new byte[stride * size.Height]; Marshal.Copy(buffer, bytes, 0, bytes.Length);
            var count = 0;
            for (var i = 3; i < bytes.Length; i += 4) if (bytes[i] > 0) count++;
            return count;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
