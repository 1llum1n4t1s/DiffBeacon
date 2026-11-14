using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DiffBeacon.Core;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMergeのフォルダー比較一覧（DirView）に合わせた列・アイコン・行の色・比較結果の文言。
/// 列の既定順は DirViewColItems.cpp、アイコンの選択順は DirActions.cpp の GetColImage、
/// 行の色の優先順は DirView.cpp の GetColors、文言は Translations/WinMerge/Japanese.po に従う。
/// 一覧そのもの（ListBox・DirectoryEntry・複数選択・展開ボタン）は既存の契約を保つため差し替えない。
/// </summary>
public sealed partial class ComparisonPane
{
    // 列幅は見出しと全行で同じ表を使う。仮想化された行ごとに測ると列がずれるため、表の定義で揃える。
    // 名前と比較結果は一覧の幅に合わせて伸縮させ（負の値は * の比率）、他の列は固定幅にする。
    // 横スクロールにすると重ね表示のスクロールバーが最下行を覆い、行をクリックできなくなるため使わない。
    private enum FolderColumn { Name, Folder, Result, LeftDate, MiddleDate, RightDate, Extension }
    private static readonly (FolderColumn Column, string Title, double Width)[] FolderColumnsTwoWay =
    [
        (FolderColumn.Name, "名前", -1), (FolderColumn.Folder, "フォルダー", 90), (FolderColumn.Result, "比較結果", -1.3),
        (FolderColumn.LeftDate, "左更新日時", 140), (FolderColumn.RightDate, "右更新日時", 140), (FolderColumn.Extension, "拡張子", 56),
    ];
    private static readonly (FolderColumn Column, string Title, double Width)[] FolderColumnsThreeWay =
    [
        (FolderColumn.Name, "名前", -1), (FolderColumn.Folder, "フォルダー", 90), (FolderColumn.Result, "比較結果", -1.3),
        (FolderColumn.LeftDate, "左更新日時", 140), (FolderColumn.MiddleDate, "中更新日時", 140), (FolderColumn.RightDate, "右更新日時", 140),
        (FolderColumn.Extension, "拡張子", 56),
    ];
    private const double FolderRowHeight = 20;
    private const double FolderTreeIndent = 16;

    private readonly Grid _folderColumnHeader = new() { Name = "folder-column-header" };
    private readonly Border _folderColumnHeaderHost = new()
    {
        Name = "folder-column-header-host", Background = WinMergeColors.FrameFace, BorderBrush = WinMergeColors.FrameBorder,
        BorderThickness = new Thickness(0, 0, 0, 1), ClipToBounds = true,
    };
    private readonly Grid _folderRootBars = new() { Name = "folder-root-bars" };
    private readonly TextBlock[] _folderRootPaths =
    [
        new() { Name = "folder-root-left" }, new() { Name = "folder-root-middle" }, new() { Name = "folder-root-right" },
    ];
    private bool? _folderHeaderThreeWay;

    // headless検証用: 現在の列見出しと最上部のルートパス帯。
    internal IReadOnlyList<string> FolderColumnTitles => _folderColumnHeader.Children.OfType<Border>()
        .Select(cell => (cell.Child as TextBlock ?? (cell.Child as Grid)?.Children.OfType<TextBlock>().FirstOrDefault())?.Text ?? "").ToArray();
    internal IReadOnlyList<string> FolderRootPathTexts => _folderRootPaths.Where(path => path.IsVisible).Select(path => path.Text ?? "").ToArray();
    internal Control FolderColumnHeader => _folderColumnHeaderHost;

    private (FolderColumn Column, string Title, double Width)[] FolderColumns
        => _directoryComparison?.IsThreeWay == true ? FolderColumnsThreeWay : FolderColumnsTwoWay;

    /// <summary>一覧の上に置くルートパス帯と列見出しを用意し、一覧の横スクロールに見出しを追従させる。</summary>
    private void InitializeFolderListChrome()
    {
        foreach (var path in _folderRootPaths)
        {
            path.Margin = new Thickness(6, 3); path.FontSize = 12; path.Foreground = WinMergeColors.FrameText;
            path.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        _folderColumnHeaderHost.Child = _folderColumnHeader;
        ScrollViewer.SetHorizontalScrollBarVisibility(_directoryList, ScrollBarVisibility.Disabled);
        _directoryList.Background = Brushes.White;
        _directoryList.ContainerPrepared += (_, args) =>
        {
            // 本家と同じく行全体を状態の色で塗る。選択色はFluentの選択スタイルが優先して上書きする。
            if (args.Container is not ListBoxItem item) return;
            item.Padding = new Thickness(0); item.MinHeight = 0;
            item.Background = item.DataContext is DirectoryEntry entry ? FolderRowBrush(entry) : Brushes.Transparent;
        };
        // 本家（エクスプローラー風の一覧）に近い淡い青の選択色。行の状態色の上でも選択が分かるよう不透明にする。
        _directoryList.Resources["SystemControlHighlightListAccentLowBrush"] = new SolidColorBrush(Color.FromRgb(204, 232, 255));
        _directoryList.Resources["SystemControlHighlightListAccentMediumBrush"] = new SolidColorBrush(Color.FromRgb(184, 220, 252));
        _directoryList.Resources["SystemControlHighlightListAccentHighBrush"] = new SolidColorBrush(Color.FromRgb(164, 208, 250));
        _directoryList.Resources["SystemControlHighlightListLowBrush"] = new SolidColorBrush(Color.FromRgb(229, 243, 255));
        _directoryList.Resources["SystemControlHighlightListMediumBrush"] = new SolidColorBrush(Color.FromRgb(204, 232, 255));
        UpdateFolderListChrome();
    }

    /// <summary>採用したモデルに合わせて、列（二者／三者）とルートパス帯を更新する。</summary>
    private void UpdateFolderListChrome()
    {
        var model = _directoryComparison;
        var threeWay = model?.IsThreeWay == true;
        _folderRootPaths[0].Text = model?.LeftPath ?? "";
        _folderRootPaths[1].Text = model?.MiddlePath ?? "";
        _folderRootPaths[2].Text = model?.RightPath ?? "";
        _folderRootPaths[1].IsVisible = threeWay;
        if (_folderHeaderThreeWay == threeWay) return;
        _folderHeaderThreeWay = threeWay;

        // パス表示のTextBlockは使い回すため、古い帯から外してから並べ直す。
        foreach (var old in _folderRootBars.Children.OfType<Border>()) old.Child = null;
        _folderRootBars.Children.Clear(); _folderRootBars.ColumnDefinitions.Clear();
        var visible = _folderRootPaths.Where(path => path.IsVisible).ToArray();
        for (var i = 0; i < visible.Length; i++)
        {
            _folderRootBars.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            var bar = new Border
            {
                Background = WinMergeColors.FrameFace, BorderBrush = WinMergeColors.FrameBorder,
                BorderThickness = new Thickness(0, 0, i == visible.Length - 1 ? 0 : 1, 1), Child = visible[i],
            };
            Grid.SetColumn(bar, i); _folderRootBars.Children.Add(bar);
        }

        _folderColumnHeader.Children.Clear(); _folderColumnHeader.ColumnDefinitions.Clear();
        var columns = FolderColumns;
        for (var i = 0; i < columns.Length; i++)
        {
            _folderColumnHeader.ColumnDefinitions.Add(FolderColumnDefinitionAt(i));
            var cell = BuildFolderHeaderCell(i, columns);
            Grid.SetColumn(cell, i); _folderColumnHeader.Children.Add(cell);
        }
        UpdateFolderSortIndicators();
    }

    /// <summary>一行分のセルを作る。名前の列にはツリーの字下げ・展開ボタン・状態アイコンを置く。</summary>
    private Control BuildFolderRow(DirectoryEntry entry)
    {
        var columns = FolderColumns;
        var row = new Grid { Height = FolderRowHeight, Name = "folder-data-row" };
        for (var i = 0; i < columns.Length; i++) row.ColumnDefinitions.Add(FolderColumnDefinitionAt(i));
        for (var i = 0; i < columns.Length; i++)
        {
            Control cell = columns[i].Column == FolderColumn.Name ? BuildFolderNameCell(entry) : FolderCellText(FolderCellValue(entry, columns[i].Column));
            if (columns[i].Column == FolderColumn.Result && entry.Error is { } error) ToolTip.SetTip(cell, error);
            Grid.SetColumn(cell, i); row.Children.Add(cell);
        }
        return row;
    }

    private static ColumnDefinition FolderColumnDefinition(double width)
        => width < 0 ? new ColumnDefinition(-width, GridUnitType.Star) { MinWidth = 80 } : new ColumnDefinition(width, GridUnitType.Pixel);

    private Control BuildFolderNameCell(DirectoryEntry entry)
    {
        var cell = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 0, 0) };
        if (FolderTreeActive)
        {
            cell.Margin = new Thickness(2 + _folderTreeDepth.GetValueOrDefault(entry.RelativePath) * FolderTreeIndent, 0, 0, 0);
            // 既存の検証は名前と "+"／"−" の文字で展開状態を読むため、文字の内容は変えずに本家の小さな枠付きボタンに寄せる。
            var toggle = new Button
            {
                Name = "FolderTreeExpander", Content = _folderExpanded.Contains(entry.RelativePath) ? "−" : "+",
                Width = 14, Height = 14, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), FontSize = 10,
                Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(150, 150, 150)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0), Foreground = WinMergeColors.FrameText,
                Focusable = false, IsVisible = FolderCanExpand(entry), IsEnabled = FolderTreeInputAllowed,
            };
            toggle.Click += async (_, args) =>
            {
                args.Handled = true;
                await GuardAsync(() => { ToggleFolderNode(entry); return Task.CompletedTask; });
            };
            toggle.AddHandler(InputElement.DoubleTappedEvent, (_, args) => args.Handled = true, RoutingStrategies.Bubble);
            // 展開できない行も同じ幅を空け、アイコンの縦位置を揃える。
            var slot = new Panel { Width = 17, Children = { toggle } };
            DockPanel.SetDock(slot, Dock.Left); cell.Children.Add(slot);
        }
        var icon = new Image
        {
            Name = "FolderEntryIcon", Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            Source = FolderIcons.Get(FolderIconName(entry)), Tag = FolderIconName(entry),
        };
        RenderOptions.SetBitmapInterpolationMode(icon, BitmapInterpolationMode.None);
        DockPanel.SetDock(icon, Dock.Left); cell.Children.Add(icon);
        cell.Children.Add(FolderCellText(FolderCellValue(entry, FolderColumn.Name), 0));
        return cell;
    }

    private static TextBlock FolderCellText(string text, double left = 6) => new()
    {
        Text = text, FontSize = 12, Foreground = WinMergeColors.FrameText, Margin = new Thickness(left, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    // headless検証からも同じ値を読むため、列の文字列は一か所で決める。
    internal string FolderCellValue(DirectoryEntry entry, string column) => FolderCellValue(entry, Enum.Parse<FolderColumn>(column));

    private string FolderCellValue(DirectoryEntry entry, FolderColumn column)
    {
        var relative = entry.RelativePath.Replace('\\', '/');
        var slash = relative.LastIndexOf('/');
        return column switch
        {
            FolderColumn.Name => slash < 0 ? relative : relative[(slash + 1)..],
            // 本家の ColPathGet と同じく、ルート直下は空にせず ".\" と表示する。
            FolderColumn.Folder => slash < 0 ? "." + Path.DirectorySeparatorChar : relative[..slash].Replace('/', Path.DirectorySeparatorChar),
            FolderColumn.Result => FolderResultText(entry),
            FolderColumn.LeftDate => FolderDateText(entry, entry.LeftState),
            FolderColumn.MiddleDate => FolderDateText(entry, entry.MiddleState),
            FolderColumn.RightDate => FolderDateText(entry, entry.RightState),
            FolderColumn.Extension => FolderExtensionText(entry),
            _ => "",
        };
    }

    /// <summary>本家の更新日時列。最も新しい側だけに "* " を付け、他は2文字の空白で揃える（DirViewColItems::ColGetTextToDisplay）。</summary>
    private string FolderDateText(DirectoryEntry entry, DirectorySideSnapshot? side)
    {
        if (side is null) return "";
        var text = side.LastWriteTimeUtc.ToLocalTime().ToString("yyyy/MM/dd H:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var others = new[] { entry.LeftState, _directoryComparison?.IsThreeWay == true ? entry.MiddleState : null, entry.RightState }
            .Where(state => state is not null && !ReferenceEquals(state, side)).Select(state => state!.LastWriteTimeUtc).ToArray();
        // 片側にしか無い項目でも、本家は比較相手の時刻0より新しいので印を付ける。
        var newest = others.Length == 0 || others.All(time => side.LastWriteTimeUtc > time);
        return (newest ? "* " : "  ") + text;
    }

    private static string FolderExtensionText(DirectoryEntry entry)
    {
        if (entry.EntryKind == DirectoryEntryKind.Directory) return "";
        var paths = new[] { entry.LeftState?.Path, entry.MiddleState?.Path, entry.RightState?.Path }.Where(path => path is not null)
            .Select(path => Path.GetExtension(path!).TrimStart('.')).ToArray();
        if (paths.Length == 0) return Path.GetExtension(entry.RelativePath).TrimStart('.');
        return paths.All(ext => string.Equals(ext, paths[0], StringComparison.OrdinalIgnoreCase)) ? paths[0] : string.Join("|", paths);
    }

    private static bool FolderIsDirectory(DirectoryEntry entry) => entry.EntryKind == DirectoryEntryKind.Directory;

    private (bool Left, bool Middle, bool Right) FolderPresence(DirectoryEntry entry)
        => (entry.LeftState is not null, _directoryComparison?.IsThreeWay == true && entry.MiddleState is not null, entry.RightState is not null);

    private bool FolderExistsOnAllSides(DirectoryEntry entry)
    {
        var (left, middle, right) = FolderPresence(entry);
        return left && right && (_directoryComparison?.IsThreeWay != true || middle);
    }

    /// <summary>本家 ColStatusGet の判定順（エラー→除外→片側のみ→三者の欠落→同一→相違）で比較結果の文言を作る。</summary>
    private string FolderResultText(DirectoryEntry entry)
    {
        var threeWay = _directoryComparison?.IsThreeWay == true;
        var (left, middle, right) = FolderPresence(entry);
        var count = (left ? 1 : 0) + (middle ? 1 : 0) + (right ? 1 : 0);
        var directory = FolderIsDirectory(entry);
        string text; var addThreeWay = false;
        if (entry.Status == DirectoryDifferenceKind.Error) text = "ファイルを比較できません";
        else if (entry.IsFiltered) text = directory ? "スキップされたフォルダー" : "スキップされたファイル";
        else if (count == 1 && left) text = "左側のみ: " + FolderSideDirectory(entry.LeftState!.Path);
        else if (count == 1 && middle) text = "中央のみ: " + FolderSideDirectory(entry.MiddleState!.Path);
        else if (count == 1 && right) text = "右側のみ: " + FolderSideDirectory(entry.RightState!.Path);
        else if (threeWay && !left) { text = _directoryComparison!.LeftPath + " 内のみ存在しません"; addThreeWay = true; }
        else if (threeWay && !middle) { text = _directoryComparison!.MiddlePath + " 内のみ存在しません"; addThreeWay = true; }
        else if (threeWay && !right) { text = _directoryComparison!.RightPath + " 内のみ存在しません"; addThreeWay = true; }
        else if (entry.Status == DirectoryDifferenceKind.Equal) text = "同一";
        else if (entry.Status == DirectoryDifferenceKind.Modified)
        { text = directory ? "フォルダーは異なっています" : "ファイルは異なっています"; addThreeWay = threeWay; }
        // 本家に無い状態は DiffBeacon 独自の文言にする（MIGRATION.md に記載）。
        else if (entry.Status == DirectoryDifferenceKind.TypeConflict) { text = "種類が異なります（ファイルとフォルダー）"; addThreeWay = threeWay; }
        else if (entry.Status == DirectoryDifferenceKind.Uncompared) text = "未比較";
        else text = "";
        if (addThreeWay)
            text += entry.ThreeWay?.Classification switch
            {
                DirectoryThreeWayClassification.OnlyLeft => " (中央と右側は同一です)",
                DirectoryThreeWayClassification.OnlyMiddle => " (左側と右側は同一です)",
                DirectoryThreeWayClassification.OnlyRight => " (左側と中央は同一です)",
                _ => "",
            };
        return text;
    }

    private static string FolderSideDirectory(string path) => Path.GetDirectoryName(path) ?? path;

    /// <summary>本家 GetColImage の判定順でアイコンを選ぶ。名前は Src/res の .ico（Merge2.rc の IDI_* 対応）。</summary>
    internal string FolderIconName(DirectoryEntry entry)
    {
        var threeWay = _directoryComparison?.IsThreeWay == true;
        var (left, middle, right) = FolderPresence(entry);
        var count = (left ? 1 : 0) + (middle ? 1 : 0) + (right ? 1 : 0);
        var directory = FolderIsDirectory(entry);
        if (entry.Status == DirectoryDifferenceKind.Error) return "error";
        if (entry.IsFiltered) return directory ? "folderskip" : "fileskip";
        if (count == 1 && left) return directory ? "lfolder" : "lfile";
        if (count == 1 && middle) return directory ? "mfolder" : "mfile";
        if (count == 1 && right) return directory ? "rfolder" : "rfile";
        if (threeWay && !left) return directory ? "lmissingfolder" : "lmissing";
        if (threeWay && !middle) return directory ? "mmissingfolder" : "mmissing";
        if (threeWay && !right) return directory ? "rmissingfolder" : "rmissing";
        if (entry.Status == DirectoryDifferenceKind.Equal) return directory ? "equalfolder" : "equalfile";
        if (entry.Status is DirectoryDifferenceKind.Modified or DirectoryDifferenceKind.TypeConflict) return directory ? "notequalfolder" : "notequalfile";
        return directory ? "folder" : "fileunknown";
    }

    /// <summary>本家 GetColors の優先順（除外→いずれかの側に無い→相違→同一）で行の背景を選ぶ。</summary>
    internal IBrush FolderRowBrush(DirectoryEntry entry)
    {
        if (entry.IsFiltered) return WinMergeColors.FolderFiltered;
        if (!FolderExistsOnAllSides(entry)) return WinMergeColors.FolderMissing;
        if (entry.Status is DirectoryDifferenceKind.Modified or DirectoryDifferenceKind.TypeConflict) return WinMergeColors.FolderDifference;
        return Brushes.White;
    }
}

/// <summary>
/// 本家WinMergeのフォルダー一覧アイコン（Src/res/*.ico、GPL v2）を読み込む。
/// ICOには16〜48pxの複数画像が入っており、そのまま渡すとデコーダーが最大の48pxを縮小してしまうため、
/// 16pxのうち色数が最も多い画像だけを含む単一画像のICOを組み直してから読み込む。
/// </summary>
internal static class FolderIcons
{
    internal static readonly string[] Names =
    [
        "lfile", "mfile", "rfile", "lmissing", "mmissing", "rmissing", "notequalfile", "equalfile", "fileunknown",
        "lfolder", "mfolder", "rfolder", "lmissingfolder", "mmissingfolder", "rmissingfolder",
        "fileskip", "folderskip", "notequalfolder", "equalfolder", "folder", "error",
    ];
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);

    internal static Bitmap Get(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;
        using var stream = typeof(FolderIcons).Assembly.GetManifestResourceStream("DiffBeacon.FolderIcons." + name + ".ico")
            ?? throw new InvalidOperationException("フォルダー一覧のアイコンが見つかりません: " + name);
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        var bitmap = new Bitmap(new MemoryStream(SingleImageIcon(buffer.ToArray(), 16)));
        Cache[name] = bitmap;
        return bitmap;
    }

    private static byte[] SingleImageIcon(byte[] icon, int size)
    {
        if (icon.Length < 6 || BitConverter.ToUInt16(icon, 0) != 0 || BitConverter.ToUInt16(icon, 2) != 1)
            throw new InvalidDataException("ICO形式ではありません。");
        var count = BitConverter.ToUInt16(icon, 4);
        var best = -1; var bestBits = -1;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            if (entry + 16 > icon.Length) throw new InvalidDataException("ICOの目次が壊れています。");
            var width = icon[entry] == 0 ? 256 : icon[entry];
            var bits = BitConverter.ToUInt16(icon, entry + 6);
            if (width == size && bits > bestBits) { best = entry; bestBits = bits; }
        }
        if (best < 0) return icon;
        var length = BitConverter.ToInt32(icon, best + 8); var offset = BitConverter.ToInt32(icon, best + 12);
        if (length <= 0 || offset < 0 || offset + (long)length > icon.Length) throw new InvalidDataException("ICOの画像範囲が壊れています。");
        var result = new byte[22 + length];
        BitConverter.GetBytes((ushort)0).CopyTo(result, 0); BitConverter.GetBytes((ushort)1).CopyTo(result, 2); BitConverter.GetBytes((ushort)1).CopyTo(result, 4);
        Array.Copy(icon, best, result, 6, 12);
        BitConverter.GetBytes(22).CopyTo(result, 18);
        Array.Copy(icon, offset, result, 22, length);
        return result;
    }
}
