using Avalonia.Controls;

namespace DiffBeacon.App;

/// <summary>
/// フォルダー一覧の表示する列・順序・列幅・並べ替えの状態と保存。本家WinMergeの「列の設定」（ID_TOOLS_CUSTOMIZECOLUMNS、
/// DirView.cpp の OnCustomizeColumns／OnEditColumns）に当たる。
/// - 状態は二者と三者で別に持つ（本家の DirView/ColumnOrders と DirView3/ColumnOrders、DirViewSortCol と DirViewSortCol3）。
/// - ペインはその種類の一覧を最初に作るときに保存済みの設定を読み、変えたら保存する。開いている他のペインには通知しない（本家と同じ）。
/// - 並べ替えている列を表示しなくしたら並べ替えを解除する（本家 OnEditColumns）。
/// - 既定と違う列の並びにしたときは全列を相対幅にして、列が多くても一覧に横スクロールを出さない。
/// </summary>
public sealed partial class ComparisonPane
{
    private sealed class FolderColumnState(bool threeWay, FolderColumn[] columns)
    {
        private FolderColumn[] _columns = columns;
        internal bool ThreeWay { get; } = threeWay;
        internal FolderColumn[] Columns
        {
            get => _columns;
            set { _columns = value; Infos = value.Select(column => FolderColumnCatalog.Info(column, ThreeWay)).ToArray(); }
        }
        // 見出しの並び順の列情報。行を作るたびに引かないよう、列の並びを変えたときだけ作り直す。
        internal FolderColumnInfo[] Infos { get; private set; } = columns.Select(column => FolderColumnCatalog.Info(column, threeWay)).ToArray();
        // null は既定の幅（名前・比較結果だけ伸縮、他は固定）。値があれば全列を相対幅にする（列ごとの重み、px と同じ尺度）。
        internal Dictionary<FolderColumn, double>? Weights { get; set; }
        internal FolderColumn? SortColumn { get; set; }
        internal bool SortDescending { get; set; }
    }

    private FolderColumnState? _folderStateTwoWay, _folderStateThreeWay;
    private readonly FolderColumnOptionsStore _standaloneFolderColumnOptions = new();
    private FolderColumnOptionsStore FolderColumnOptions => (_owner as MainWindow)?.FolderColumnOptions ?? _standaloneFolderColumnOptions;

    private FolderColumnState FolderState => FolderStateFor(_directoryComparison?.IsThreeWay == true);

    private FolderColumnState FolderStateFor(bool threeWay)
        => threeWay ? _folderStateThreeWay ??= LoadFolderColumnState(true) : _folderStateTwoWay ??= LoadFolderColumnState(false);

    private FolderColumnInfo[] FolderColumns => FolderState.Infos;

    /// <summary>保存済みの配置を読む。保存が無いか読めない（Store が既定へ戻した）ときは既定の列。</summary>
    private FolderColumnState LoadFolderColumnState(bool threeWay)
    {
        if (FolderColumnOptions.Diagnostic is { } diagnostic) _status.Text = diagnostic;
        if (FolderColumnOptions.Layout(threeWay) is not { } saved)
            return new FolderColumnState(threeWay, FolderColumnCatalog.Defaults(threeWay).ToArray());
        var columns = saved.Columns.Select(Enum.Parse<FolderColumn>).ToArray();
        var state = new FolderColumnState(threeWay, columns)
        {
            SortColumn = saved.SortColumn is { } sort ? Enum.Parse<FolderColumn>(sort) : null,
            SortDescending = saved.SortDescending,
        };
        if (saved.Widths is { } widths)
        {
            state.Weights = [];
            for (var i = 0; i < columns.Length; i++) state.Weights[columns[i]] = widths[i];
        }
        return state;
    }

    private static FolderColumnLayoutOptions? FolderLayoutOptions(FolderColumnState state)
    {
        var defaults = FolderColumnCatalog.Defaults(state.ThreeWay);
        // 既定の列・既定の幅・並べ替えなしなら何も保存しない（設定ファイルを既定の値で埋めない）。
        if (state.Columns.SequenceEqual(defaults) && state.Weights is null && state.SortColumn is null) return null;
        return new FolderColumnLayoutOptions
        {
            Columns = state.Columns.Select(column => column.ToString()).ToArray(),
            Widths = state.Weights is { } weights ? state.Infos.Select(info => FolderWeight(weights, info)).ToArray() : null,
            SortColumn = state.SortColumn?.ToString(),
            SortDescending = state.SortColumn is not null && state.SortDescending,
        };
    }

    /// <summary>現在の種類の配置を保存する。保存できなくても表示は変えたまま（並べ替え・幅の操作は取り消さない）にし、理由を状態欄へ出す。</summary>
    private void SaveFolderColumnState()
    {
        var state = FolderState;
        if (!FolderColumnOptions.SetLayout(state.ThreeWay, FolderLayoutOptions(state))) _status.Text = FolderColumnOptions.Diagnostic;
    }

    // headless検証用。
    internal IReadOnlyList<string> FolderVisibleColumnNames => FolderState.Columns.Select(column => column.ToString()).ToArray();
    internal bool FolderColumnsUseWeights => FolderState.Weights is not null;
    internal IReadOnlyList<string> FolderColumnLayoutForTest(bool threeWay) => FolderStateFor(threeWay).Columns.Select(column => column.ToString()).ToArray();

    /// <summary>headless検証用: 新しく開いたペインと同じく、保存済みの設定から読み直す。</summary>
    internal void ReloadFolderColumnsForTest()
    {
        _folderStateTwoWay = null; _folderStateThreeWay = null;
        _folderHeaderSignature = null;
        UpdateFolderListChrome();
        RebuildFolderRows();
    }

    /// <summary>
    /// 表示中の行は前の列で作られているので、表示順を作り直して行を作り直す（選択・展開状態は保つ）。
    /// 並べ替えが無いと表示順は比較結果の一覧そのもの（同じ参照）になり、一覧が変化に気づかず行を作り直さないため、新しい配列にして渡す。
    /// </summary>
    private void RebuildFolderRows()
    {
        if (_directoryComparison is not { } model || !FolderTreeInputAllowed) return;
        if (FolderFilterStamp() != _directoryFilterStamp) return;
        var projection = PrepareFolderTree(model, new HashSet<string>(_folderExpanded, FolderTreeComparer), FolderTreeActive);
        var selected = _directoryList.SelectedItems?.OfType<Core.DirectoryEntry>().Select(entry => entry.RelativePath).ToArray() ?? [];
        InvalidateFolderCopy(false);
        AdoptFolderTreeProjection(projection with { Rows = projection.Rows.ToArray() }, selected);
    }

    /// <summary>headless検証用: 二者・三者とも既定の列・幅・並べ替えなしへ戻し、保存した設定も消す（後に続く検証へ影響させない）。</summary>
    internal void ResetFolderColumnsForTest()
    {
        FolderColumnOptions.SetLayout(false, null); FolderColumnOptions.SetLayout(true, null);
        ReloadFolderColumnsForTest();
    }

    /// <summary>
    /// 「ツール > 列の設定」と列見出しの右クリック。フォルダー比較の一覧を表示していて、他の操作の実行中でないときだけ開く。
    /// OK したら、先に保存してから表示を変える（保存できなければ表示も変えない）。
    /// </summary>
    internal async Task CustomizeFolderColumnsAsync()
    {
        if (_directoryComparison is not { } model || !ReferenceEquals(_specialTab.Content, _folderView) || !FolderTreeInputAllowed)
        {
            _status.Text = "列の設定は、フォルダー比較の一覧を表示しているときに使えます。";
            return;
        }
        var threeWay = model.IsThreeWay;
        var state = FolderStateFor(threeWay);
        var dialog = new FolderColumnsDialog(FolderColumnCatalog.For(threeWay), state.Columns, FolderColumnCatalog.Defaults(threeWay));
        var choice = await dialog.ShowAsync(_owner);
        if (choice is null || choice.Columns.Length == 0) return;
        // ダイアログの間に比較し直された・別の種類になったときは、古い選択を当てはめない。
        if (!ReferenceEquals(model, _directoryComparison) || !FolderTreeInputAllowed)
        {
            _status.Text = "列の設定の間に比較の状態が変わったため、列は変更していません。";
            return;
        }
        ApplyFolderColumnChoice(threeWay, choice.Columns, choice.Reset);
    }

    /// <summary>選んだ列を当てはめる。テストからも同じ経路で呼ぶ。</summary>
    internal bool ApplyFolderColumnChoice(bool threeWay, FolderColumn[] columns, bool reset)
    {
        if (columns.Length == 0 || columns.Distinct().Count() != columns.Length || columns.Any(column => !FolderColumnCatalog.Supports(column, threeWay)))
        {
            _status.Text = "その比較の種類では表示できない列が含まれています。列は変更していません。";
            return false;
        }
        var state = FolderStateFor(threeWay);
        var defaults = FolderColumnCatalog.Defaults(threeWay);
        Dictionary<FolderColumn, double>? weights;
        if (reset && columns.SequenceEqual(defaults)) weights = null;
        else if (state.Weights is { } existing && !reset) weights = new(existing);
        else if (columns.SequenceEqual(defaults)) weights = null;
        else
        {
            // 既定の幅のまま列を変えたら、今の見た目の幅を相対幅の起点にし、新しく出す列は既定の幅から始める。
            // デフォルトを押した後なら今の幅は使わず、全列を既定の幅から始める（本家の ResetColumnWidths）。
            weights = [];
            if (!reset && threeWay == (_directoryComparison?.IsThreeWay == true) && _folderColumnHeader.ColumnDefinitions.Count == state.Infos.Length)
                for (var i = 0; i < state.Infos.Length; i++)
                    if (_folderColumnHeader.ColumnDefinitions[i].ActualWidth > 0)
                        weights[state.Infos[i].Column] = Math.Max(FolderColumnMinimumWidth, _folderColumnHeader.ColumnDefinitions[i].ActualWidth);
        }
        // 表示しない列の重みは残さない（保存する幅は表示中の列の分だけ）。
        if (weights is not null)
            foreach (var hidden in weights.Keys.Where(column => !columns.Contains(column)).ToArray()) weights.Remove(hidden);
        var sortColumn = state.SortColumn is { } sort && columns.Contains(sort) ? state.SortColumn : null;
        var candidate = new FolderColumnState(threeWay, columns.ToArray())
        {
            Weights = weights, SortColumn = sortColumn, SortDescending = sortColumn is not null && state.SortDescending,
        };
        if (!FolderColumnOptions.SetLayout(threeWay, FolderLayoutOptions(candidate)))
        {
            _status.Text = FolderColumnOptions.Diagnostic + " 列は変更していません。";
            return false;
        }
        state.Columns = candidate.Columns; state.Weights = candidate.Weights;
        state.SortColumn = candidate.SortColumn; state.SortDescending = candidate.SortDescending;
        if (threeWay == (_directoryComparison?.IsThreeWay == true))
        {
            // 列が同じでも幅（既定／相対）が変わることがあるので、見出しは必ず作り直す。
            _folderHeaderSignature = null;
            UpdateFolderListChrome();
            RebuildFolderRows();
        }
        _status.Text = "表示する列を変更しました。";
        return true;
    }

    /// <summary>列見出しの右クリックメニュー（本家 IDR_POPUP_DIRVIEW の _HDR_POPUP_）。</summary>
    private void InitializeFolderColumnMenu()
    {
        var item = new MenuItem { Header = "列の設定(_C)...", Name = "FolderCustomizeColumns" };
        item.Click += async (_, _) => await GuardAsync(CustomizeFolderColumnsAsync);
        _folderColumnHeaderHost.ContextMenu = new ContextMenu { ItemsSource = new[] { item } };
    }
}
