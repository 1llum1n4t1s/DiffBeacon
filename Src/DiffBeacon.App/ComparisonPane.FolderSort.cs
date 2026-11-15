using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

/// <summary>
/// フォルダー一覧の列見出し操作。本家WinMergeの DirView と同じく、見出しのクリックで並べ替え（同じ列を続けて押すと昇順／降順を切替え）、
/// 見出しの境界のドラッグで列幅を変え、見出しの右クリックまたは「ツール > 列の設定」で表示する列と順序を選ぶ（ComparisonPane.FolderColumns.cs）。
/// 並べ替えは表示順だけを変え、比較結果（DirectoryComparisonResult）・選択・コピー計画の元データには触れない。
/// ツリー表示では同じ親の兄弟どうしを並べ替え、親子の関係は保つ（本家の DirView も階層内で整列する）。
/// 列幅は、利用者が変えた後（または既定と違う列を表示したとき）は全列を相対幅（Star）として持ち、境界の左右2列の間で幅を移すので、
/// 横スクロールを出さずウィンドウ幅へ追従する。
/// 表示する列・幅・並べ替えは二者と三者で別に持ち（本家も別のキー）、変えたら folder-columns.json へ保存する。
/// </summary>
public sealed partial class ComparisonPane
{
    private const double FolderColumnMinimumWidth = 40;
    private const double FolderColumnGripWidth = 6;
    private bool _folderResizing;
    private double[]? _folderResizeStart;
    private int _folderResizeIndex;
    private double _folderResizeOrigin;

    internal string? FolderSortColumnName => FolderState.SortColumn?.ToString();
    internal bool FolderSortDescending => FolderState.SortDescending;
    internal IReadOnlyList<double> FolderColumnWidths => _folderColumnHeader.ColumnDefinitions.Select(column => column.ActualWidth).ToArray();

    /// <summary>テストと操作の共通入口。見出しをクリックしたときと同じ並べ替えを行い、保存する。</summary>
    internal void SortFolderBy(string column)
    {
        var target = Enum.Parse<FolderColumn>(column);
        var state = FolderState;
        if (!state.Columns.Contains(target)) return;
        if (state.SortColumn == target) state.SortDescending = !state.SortDescending;
        else { state.SortColumn = target; state.SortDescending = false; }
        UpdateFolderSortIndicators();
        // 木の再投影と同じ経路で表示順だけを作り直す（選択・展開状態はそのまま保たれる）。
        if (_directoryComparison is not null && FolderTreeInputAllowed)
        {
            try { ReprojectFolderTree(new HashSet<string>(_folderExpanded, FolderTreeComparer)); }
            catch (OperationCanceledException) { }
        }
        SaveFolderColumnState();
    }

    /// <summary>テスト用。並べ替えを解除して元の順へ戻す（本家の操作には無い）。保存した並べ替えも解除する。</summary>
    internal void ClearFolderSortForTest()
    {
        var state = FolderState;
        state.SortColumn = null; state.SortDescending = false; UpdateFolderSortIndicators();
        if (_directoryComparison is not null && FolderTreeInputAllowed)
        {
            try { ReprojectFolderTree(new HashSet<string>(_folderExpanded, FolderTreeComparer)); }
            catch (OperationCanceledException) { }
        }
        SaveFolderColumnState();
    }

    /// <summary>
    /// 項目を現在の並べ替えで整列する。並べ替えなしなら元の順を返す。名前の列は本家と同じくフォルダーを先に置く。
    /// 並べ替えの状態は、整列する比較結果の種類（二者／三者）のものを使う。
    /// </summary>
    private IReadOnlyList<DirectoryEntry> SortFolderEntries(IReadOnlyList<DirectoryEntry> entries, bool threeWay)
    {
        var state = FolderStateFor(threeWay);
        if (state.SortColumn is not { } column || entries.Count < 2) return entries;
        var descending = state.SortDescending;
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

    /// <summary>
    /// 整列キー。日時は表示文字列ではなく実際の時刻、サイズはバイト数、属性は属性の値で比べる（本家 ColTimeSort・ColSizeSort・ColAttrSort）。
    /// 日時比較の列は本家 ColNewerSort と同じく表示文字列を序数で比べる。簡易の比較結果は長い比較結果と同じ順（本家はどちらも ColStatusSort）。
    /// </summary>
    private FolderSortValue FolderSortKey(DirectoryEntry entry, FolderColumn column) => column switch
    {
        FolderColumn.LeftDate => FolderSortValue.OfTime(entry.LeftState?.LastWriteTimeUtc),
        FolderColumn.MiddleDate => FolderSortValue.OfTime(entry.MiddleState?.LastWriteTimeUtc),
        FolderColumn.RightDate => FolderSortValue.OfTime(entry.RightState?.LastWriteTimeUtc),
        FolderColumn.LeftSize or FolderColumn.LeftSizeShort => FolderSortValue.OfNumber(FolderFileSize(entry.LeftState)),
        FolderColumn.MiddleSize or FolderColumn.MiddleSizeShort => FolderSortValue.OfNumber(FolderFileSize(entry.MiddleState)),
        FolderColumn.RightSize or FolderColumn.RightSizeShort => FolderSortValue.OfNumber(FolderFileSize(entry.RightState)),
        FolderColumn.LeftAttributes => FolderSortValue.OfNumber(entry.LeftState is { } left ? FolderColumnCatalog.AttributeKey(left.Attributes) : null),
        FolderColumn.MiddleAttributes => FolderSortValue.OfNumber(entry.MiddleState is { } middle ? FolderColumnCatalog.AttributeKey(middle.Attributes) : null),
        FolderColumn.RightAttributes => FolderSortValue.OfNumber(entry.RightState is { } right ? FolderColumnCatalog.AttributeKey(right.Attributes) : null),
        FolderColumn.Newer => FolderSortValue.OfOrdinal(FolderCellValue(entry, column)),
        FolderColumn.ResultAbbr => FolderSortValue.OfText(FolderCellValue(entry, FolderColumn.Result)),
        _ => FolderSortValue.OfText(FolderCellValue(entry, column)),
    };

    private readonly struct FolderSortValue : IComparable<FolderSortValue>
    {
        private enum Kind { Text, Ordinal, Number }
        private readonly string? _text; private readonly long _number; private readonly Kind _kind;
        private FolderSortValue(string? text, long number, Kind kind) { _text = text; _number = number; _kind = kind; }
        internal static FolderSortValue OfText(string text) => new(text, 0, Kind.Text);
        internal static FolderSortValue OfOrdinal(string text) => new(text, 0, Kind.Ordinal);
        // 側が無い行は最小として扱う（昇順で先頭。本家もサイズ -1・時刻 0 の項目が先頭になる）。
        internal static FolderSortValue OfTime(DateTime? time) => new(null, time?.ToUniversalTime().Ticks ?? long.MinValue, Kind.Number);
        internal static FolderSortValue OfNumber(long? number) => new(null, number ?? long.MinValue, Kind.Number);
        public int CompareTo(FolderSortValue other) => _kind switch
        {
            Kind.Number => _number.CompareTo(other._number),
            Kind.Ordinal => string.CompareOrdinal(_text, other._text),
            _ => string.Compare(_text, other._text, StringComparison.CurrentCultureIgnoreCase),
        };
    }

    /// <summary>列indexの幅定義。相対幅を持つ配置では全列が相対幅になる。</summary>
    private ColumnDefinition FolderColumnDefinitionAt(int index)
    {
        var state = FolderState;
        var info = state.Infos[index];
        return state.Weights is not { } weights ? FolderColumnDefinition(info.Width)
            : new ColumnDefinition(FolderWeight(weights, info), GridUnitType.Star) { MinWidth = FolderColumnMinimumWidth };
    }

    private static double FolderWeight(Dictionary<FolderColumn, double> weights, FolderColumnInfo info)
        => weights.TryGetValue(info.Column, out var weight) ? weight : FolderColumnCatalog.InitialWeight(info);

    private void UpdateFolderSortIndicators()
    {
        var state = FolderState;
        foreach (var cell in _folderColumnHeader.Children.OfType<Border>())
        {
            var index = Grid.GetColumn(cell);
            if (index < 0 || index >= state.Infos.Length || cell.Child is not Grid content || content.Children.OfType<TextBlock>().FirstOrDefault() is not { } title) continue;
            var sorted = state.SortColumn == state.Infos[index].Column;
            title.Text = state.Infos[index].Title + (sorted ? (state.SortDescending ? " ▼" : " ▲") : "");
        }
    }

    /// <summary>見出し1列分の操作部品を作る（クリックで並べ替え、右端のつかみでドラッグ幅変更、右クリックで列の設定）。</summary>
    private Control BuildFolderHeaderCell(int index, FolderColumnInfo[] columns)
    {
        var title = new TextBlock
        {
            Text = columns[index].Title, FontSize = 12, Foreground = WinMergeColors.FrameText, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 3, FolderColumnGripWidth, 3),
            HorizontalAlignment = columns[index].AlignRight ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left,
        };
        var content = new Grid(); content.Children.Add(title);
        var cell = new Border
        {
            BorderBrush = WinMergeColors.FrameBorder, BorderThickness = new Thickness(0, 0, 1, 0), Background = Brushes.Transparent,
            Child = content, Name = "folder-header-" + columns[index].Column,
        };
        var column = columns[index].Column;
        cell.PointerReleased += (_, args) =>
        {
            if (args.InitialPressMouseButton == MouseButton.Left && !_folderResizing) SortFolderBy(column.ToString());
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
        // ドラッグの途中ではなく、離したときに一度だけ保存する（本家も列幅の変更の後に保存する）。
        SaveFolderColumnState();
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
        if (i < 0 || i + 1 >= start.Length) return;
        var applied = Math.Clamp(delta, FolderColumnMinimumWidth - start[i], start[i + 1] - FolderColumnMinimumWidth);
        var widths = (double[])start.Clone(); widths[i] += applied; widths[i + 1] -= applied;
        SetFolderWeights(widths);
    }

    /// <summary>テスト用。index 番目と index+1 番目の境界を delta だけ動かす（ドラッグ操作と同じ経路で、離したときと同じく保存する）。</summary>
    internal void ResizeFolderBoundaryForTest(int index, double delta)
    {
        _folderResizeStart = CurrentFolderWidths(); _folderResizeIndex = index; _folderResizing = true;
        ResizeFolderColumn(delta); _folderResizing = false;
        SaveFolderColumnState();
    }

    /// <summary>表示中の列の幅（見出しの並び順）を相対幅として記録し、見出しと表示中の行へ反映する。</summary>
    private void SetFolderWeights(double[] widths)
    {
        var state = FolderState;
        if (widths.Length != state.Infos.Length) return;
        state.Weights = new Dictionary<FolderColumn, double>();
        for (var i = 0; i < widths.Length; i++) state.Weights[state.Infos[i].Column] = widths[i];
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
