using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

/// <summary>
/// フォルダー一覧の列見出し操作。本家WinMergeの DirView と同じく、見出しのクリックで並べ替え（同じ列を続けて押すと昇順／降順を切替え）、
/// 見出しの境界のドラッグで列幅を変える。
/// 並べ替えは表示順だけを変え、比較結果（DirectoryComparisonResult）・選択・コピー計画の元データには触れない。
/// ツリー表示では同じ親の兄弟どうしを並べ替え、親子の関係は保つ（本家の DirView も階層内で整列する）。
/// 列幅は、利用者が変えた後は全列を相対幅（Star）として持ち、境界の左右2列の間で幅を移すので、横スクロールを出さずウィンドウ幅へ追従する。
/// </summary>
public sealed partial class ComparisonPane
{
    private const double FolderColumnMinimumWidth = 40;
    private const double FolderColumnGripWidth = 6;
    private FolderColumn? _folderSortColumn;
    private bool _folderSortDescending;
    // 列幅の相対値（二者／三者で別）。null は既定（Name・Result だけ伸縮、他は固定）。
    private double[]? _folderWeightsTwoWay, _folderWeightsThreeWay;
    private bool _folderResizing;
    private double[]? _folderResizeStart;
    private int _folderResizeIndex;
    private double _folderResizeOrigin;

    internal string? FolderSortColumnName => _folderSortColumn?.ToString();
    internal bool FolderSortDescending => _folderSortDescending;
    internal IReadOnlyList<double> FolderColumnWidths => _folderColumnHeader.ColumnDefinitions.Select(column => column.ActualWidth).ToArray();

    /// <summary>テストと操作の共通入口。見出しをクリックしたときと同じ並べ替えを行う。</summary>
    internal void SortFolderBy(string column)
    {
        var target = Enum.Parse<FolderColumn>(column);
        if (_folderSortColumn == target) _folderSortDescending = !_folderSortDescending;
        else { _folderSortColumn = target; _folderSortDescending = false; }
        UpdateFolderSortIndicators();
        // 木の再投影と同じ経路で表示順だけを作り直す（選択・展開状態はそのまま保たれる）。
        if (_directoryComparison is not null && FolderTreeInputAllowed)
        {
            try { ReprojectFolderTree(new HashSet<string>(_folderExpanded, FolderTreeComparer)); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>テスト用。並べ替えを解除して元の順へ戻す（本家の操作には無い）。</summary>
    internal void ClearFolderSortForTest()
    {
        _folderSortColumn = null; _folderSortDescending = false; UpdateFolderSortIndicators();
        if (_directoryComparison is not null && FolderTreeInputAllowed)
        {
            try { ReprojectFolderTree(new HashSet<string>(_folderExpanded, FolderTreeComparer)); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>項目を現在の並べ替えで整列する。並べ替えなしなら元の順を返す。名前の列は本家と同じくフォルダーを先に置く。</summary>
    private IReadOnlyList<DirectoryEntry> SortFolderEntries(IReadOnlyList<DirectoryEntry> entries)
    {
        if (_folderSortColumn is not { } column || entries.Count < 2) return entries;
        var descending = _folderSortDescending;
        var keyed = entries.Select((entry, index) => (Entry: entry, Index: index, Key: FolderSortKey(entry, column))).ToList();
        keyed.Sort((a, b) =>
        {
            if (column == FolderColumn.Name)
            {
                var aDir = FolderIsDirectory(a.Entry); var bDir = FolderIsDirectory(b.Entry);
                if (aDir != bDir) return aDir ? -1 : 1;
            }
            var order = a.Key.CompareTo(b.Key);
            if (descending) order = -order;
            // 同順位は元の順を保つ（安定整列）。
            return order != 0 ? order : a.Index.CompareTo(b.Index);
        });
        return keyed.Select(item => item.Entry).ToArray();
    }

    /// <summary>整列キー。日時は表示文字列ではなく実際の時刻で比べる（印の「* 」に左右されない）。</summary>
    private FolderSortValue FolderSortKey(DirectoryEntry entry, FolderColumn column) => column switch
    {
        FolderColumn.LeftDate => FolderSortValue.OfTime(entry.LeftState?.LastWriteTimeUtc),
        FolderColumn.MiddleDate => FolderSortValue.OfTime(entry.MiddleState?.LastWriteTimeUtc),
        FolderColumn.RightDate => FolderSortValue.OfTime(entry.RightState?.LastWriteTimeUtc),
        _ => FolderSortValue.OfText(FolderCellValue(entry, column)),
    };

    private readonly struct FolderSortValue : IComparable<FolderSortValue>
    {
        private readonly string? _text; private readonly long _ticks; private readonly bool _isTime;
        private FolderSortValue(string? text, long ticks, bool isTime) { _text = text; _ticks = ticks; _isTime = isTime; }
        internal static FolderSortValue OfText(string text) => new(text, 0, false);
        // 側が無い行は最小として扱う（昇順で先頭）。
        internal static FolderSortValue OfTime(DateTime? time) => new(null, time?.ToUniversalTime().Ticks ?? long.MinValue, true);
        public int CompareTo(FolderSortValue other)
            => _isTime ? _ticks.CompareTo(other._ticks) : string.Compare(_text, other._text, StringComparison.CurrentCultureIgnoreCase);
    }

    private double[] FolderWeightsOrEmpty(int count)
    {
        var current = _directoryComparison?.IsThreeWay == true ? _folderWeightsThreeWay : _folderWeightsTwoWay;
        return current is not null && current.Length == count ? current : [];
    }

    /// <summary>列indexの幅定義。利用者が幅を変えた後は全列が相対幅になる。</summary>
    private ColumnDefinition FolderColumnDefinitionAt(int index)
    {
        var columns = FolderColumns;
        var weights = FolderWeightsOrEmpty(columns.Length);
        return weights.Length == 0 ? FolderColumnDefinition(columns[index].Width)
            : new ColumnDefinition(weights[index], GridUnitType.Star) { MinWidth = FolderColumnMinimumWidth };
    }

    private void UpdateFolderSortIndicators()
    {
        var columns = FolderColumns;
        foreach (var cell in _folderColumnHeader.Children.OfType<Border>())
        {
            var index = Grid.GetColumn(cell);
            if (index < 0 || index >= columns.Length || cell.Child is not Grid content || content.Children.OfType<TextBlock>().FirstOrDefault() is not { } title) continue;
            var sorted = _folderSortColumn == columns[index].Column;
            title.Text = columns[index].Title + (sorted ? (_folderSortDescending ? " ▼" : " ▲") : "");
        }
    }

    /// <summary>見出し1列分の操作部品を作る（クリックで並べ替え、右端のつかみでドラッグ幅変更）。</summary>
    private Control BuildFolderHeaderCell(int index, (FolderColumn Column, string Title, double Width)[] columns)
    {
        var title = new TextBlock
        {
            Text = columns[index].Title, FontSize = 12, Foreground = WinMergeColors.FrameText, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 3, FolderColumnGripWidth, 3),
        };
        var content = new Grid(); content.Children.Add(title);
        var cell = new Border
        {
            BorderBrush = WinMergeColors.FrameBorder, BorderThickness = new Thickness(0, 0, 1, 0), Background = Brushes.Transparent,
            Child = content, Name = "folder-header-" + columns[index].Column,
        };
        cell.PointerReleased += (_, args) =>
        {
            if (args.InitialPressMouseButton == MouseButton.Left && !_folderResizing) SortFolderBy(columns[index].Column.ToString());
        };
        // 最後の列の右にはドラッグできる隣接列が無いので、つかみは置かない。
        if (index < columns.Length - 1)
        {
            var grip = new Border
            {
                Width = FolderColumnGripWidth, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeWestEast), Name = "folder-grip-" + index,
            };
            grip.PointerPressed += (_, args) => BeginFolderResize(grip, args, index);
            grip.PointerMoved += (_, args) => { if (_folderResizing) ResizeFolderColumn(args.GetPosition(_folderColumnHeader).X - _folderResizeOrigin); };
            grip.PointerReleased += (_, args) => EndFolderResize(args);
            grip.PointerCaptureLost += (_, _) => _folderResizing = false;
            content.Children.Add(grip);
        }
        return cell;
    }

    private void BeginFolderResize(Control grip, PointerPressedEventArgs args, int index)
    {
        if (!args.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
        var widths = CurrentFolderWidths();
        _folderResizeStart = widths; _folderResizeIndex = index; _folderResizing = true;
        _folderResizeOrigin = args.GetPosition(_folderColumnHeader).X;
        args.Pointer.Capture(grip); args.Handled = true;
        SetFolderWeights(widths);
    }

    private void EndFolderResize(PointerReleasedEventArgs args)
    {
        if (!_folderResizing) return;
        ResizeFolderColumn(args.GetPosition(_folderColumnHeader).X - _folderResizeOrigin);
        args.Pointer.Capture(null); args.Handled = true;
        // 同じ操作が見出しのクリック（並べ替え）として発火しないよう、解除は処理の後に行う。
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _folderResizing = false);
    }

    private double[] CurrentFolderWidths()
        => _folderColumnHeader.ColumnDefinitions.Select(column => Math.Max(FolderColumnMinimumWidth, column.ActualWidth)).ToArray();

    /// <summary>境界の左右2列の間で幅を移す。合計は変えず、各列は最小幅を下回らない。</summary>
    internal void ResizeFolderColumn(double delta)
    {
        if (!_folderResizing || _folderResizeStart is not { } start) return;
        var i = _folderResizeIndex;
        var applied = Math.Clamp(delta, FolderColumnMinimumWidth - start[i], start[i + 1] - FolderColumnMinimumWidth);
        var widths = (double[])start.Clone(); widths[i] += applied; widths[i + 1] -= applied;
        SetFolderWeights(widths);
    }

    /// <summary>テスト用。index 番目と index+1 番目の境界を delta だけ動かす（ドラッグ操作と同じ経路）。</summary>
    internal void ResizeFolderBoundaryForTest(int index, double delta)
    {
        _folderResizeStart = CurrentFolderWidths(); _folderResizeIndex = index; _folderResizing = true;
        ResizeFolderColumn(delta); _folderResizing = false;
    }

    private void SetFolderWeights(double[] widths)
    {
        if (_directoryComparison?.IsThreeWay == true) _folderWeightsThreeWay = widths; else _folderWeightsTwoWay = widths;
        ApplyFolderWeights(_folderColumnHeader, widths);
        // 画面に出ている行にも同じ幅を反映する（新しく作られる行は FolderColumnDefinitionAt が同じ値を使う）。
        foreach (var row in _directoryList.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Name == "folder-data-row"))
            ApplyFolderWeights(row, widths);
    }

    private static void ApplyFolderWeights(Grid grid, double[] widths)
    {
        if (grid.ColumnDefinitions.Count != widths.Length) return;
        for (var i = 0; i < widths.Length; i++)
        {
            grid.ColumnDefinitions[i].MinWidth = FolderColumnMinimumWidth;
            grid.ColumnDefinitions[i].Width = new GridLength(widths[i], GridUnitType.Star);
        }
    }
}
