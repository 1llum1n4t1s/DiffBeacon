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

        pane.LeftPath.Text = left; pane.BasePath.Text = ""; pane.RightPath.Text = right;
        pump(pane.ComparePathsAsync());
        Dispatcher.UIThread.RunJobs();
        var list = pane.FolderOptionsRawRows;
        var entries = list.Items.OfType<DirectoryEntry>().ToArray();
        check("folder view lists compared entries", entries.Length >= 6, string.Join("|", entries.Select(entry => entry.RelativePath)));

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

        // 三者比較: 中更新日時の列と、欠落側・同一側の文言とアイコン。
        var three = Path.Combine(output, "folder-view-3"); var l3 = Path.Combine(three, "left"); var m3 = Path.Combine(three, "middle"); var r3 = Path.Combine(three, "right");
        foreach (var root in new[] { l3, m3, r3 }) Directory.CreateDirectory(root);
        Write(l3, "a.txt", "left changed\n"); Write(m3, "a.txt", "base\n"); Write(r3, "a.txt", "base\n");
        Write(l3, "b.txt", "b\n"); Write(r3, "b.txt", "b\n");
        pane.LeftPath.Text = l3; pane.BasePath.Text = m3; pane.RightPath.Text = r3;
        pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
        entries = list.Items.OfType<DirectoryEntry>().ToArray();
        titles = pane.FolderColumnTitles;
        check("folder view three-way adds middle date column", titles.SequenceEqual(["名前", "フォルダー", "比較結果", "左更新日時", "中更新日時", "右更新日時", "拡張子"]),
            string.Join("|", titles));
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
