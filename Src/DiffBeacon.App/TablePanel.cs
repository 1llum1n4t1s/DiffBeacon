using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using DiffBeacon.Core;
using System.Globalization;

namespace DiffBeacon.App;

// セルの表示・編集は原文と共通比較モデルへ接続する。CSV 全体の再生成はしない。
public sealed partial class TablePanel : DockPanel
{
    private readonly Func<int, string> _source;
    private readonly Func<int, bool> _readOnly;
    private readonly Action<int, string> _write;
    private readonly Action<int, string> _rollback;
    private readonly Func<Task<TableComparisonResult>> _compare;
    private readonly Func<CancellationToken> _cancellation;
    private readonly TableApplicationOptionsStore _applicationOptions;
    private readonly Func<IReadOnlyList<TableColumnIgnoreRule>> _getColumnIgnoreRules;
    private readonly Action<IReadOnlyList<TableColumnIgnoreRule>>? _setColumnIgnoreRules;
    private readonly Func<IReadOnlyList<TableColumnRangeRule>> _getColumnRangeRules;
    private readonly Action<IReadOnlyList<TableColumnRangeRule>>? _setColumnRangeRules;
    private readonly TextBox _ignoreColumn = new() { Width = 75, Text = "1", Name = "table-ignore-column" };
    private readonly ComboBox _ignoreColumnScope = new() { Width = 125, Name = "table-ignore-column-scope", ItemsSource = new[] { "全ペイン", "選択ペイン" }, SelectedIndex = 0 };
    private readonly TextBlock _ignoreColumnSummary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6), Name = "table-ignore-column-summary" };
    private readonly TextBox _columnRange = new() { Width = 180, PlaceholderText = "例: 1-3, 5, !2", Name = "table-column-range" };
    private readonly ComboBox _columnRangeScope = new() { Width = 125, Name = "table-column-range-scope", ItemsSource = new[] { "全ペイン", "選択ペイン" }, SelectedIndex = 0 };
    private readonly TextBlock _columnRangeSummary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6), Name = "table-column-range-summary" };
    private readonly ListBox _rows = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };
    private readonly ComboBox _side = new() { Width = 85, Name = "table-side" };
    private readonly TextBox _row = new() { Width = 60, Text = "1", Name = "table-source-row" };
    private readonly TextBox _column = new() { Width = 60, Text = "1", Name = "table-source-column" };
    public TextBox CellEditor { get; } = new() { AcceptsReturn = true, Width = 280, Height = 65, Name = "table-cell-editor" };
    public TextBox SearchText { get; } = new() { Width = 180, PlaceholderText = "セル内を検索", Name = "table-search" };
    private readonly CheckBox _case = new() { Content = "大小文字を区別" };
    private readonly CheckBox _regex = new() { Content = "正規表現" };
    private readonly CheckBox _word = new() { Content = "単語単位" };
    private readonly CheckBox _wrap = new() { Content = "折返し", IsChecked = true };
    private readonly CheckBox _allSides = new() { Content = "全ペイン", IsChecked = true };
    private readonly CheckBox _useFirstRowAsHeaders = new() { Content = "先頭行を列見出しにする", Name = "table-use-first-row-as-headers", IsChecked = false };
    private readonly Border _columnHeaders = new() { Name = "table-column-headers", BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0, 0, 0, .5), MaxHeight = 82, IsVisible = false };
    private Stack<Edit> _undo = new();
    private readonly Stack<Edit> _redo = new();
    private bool _busy;
    private bool _syncingHeaderOption;
    private const int ColumnsPerPage = 32;
    private const double DefaultColumnWidth = 140;
    private const double MinimumColumnWidth = 48;
    private int _firstColumn;
    private double[] _columnWidths = [];
    internal Task? PendingOperation { get; private set; }
    private int _selectedSide, _selectedRow = -1, _selectedColumn;
    private sealed record Edit(int Side, string Before, string After, SearchScopeState? BeforeScope = null, SearchScopeState? AfterScope = null);
    public TableComparisonResult Comparison { get; private set; }

    internal TablePanel(TableComparisonResult comparison, Func<int, string> source, Func<int, bool> readOnly,
        Action<int, string> write, Action<int, string> rollback, Func<Task<TableComparisonResult>> compare, IReadOnlyList<string> names,
        Func<CancellationToken> cancellation, TableApplicationOptionsStore applicationOptions,
        Func<IReadOnlyList<TableColumnIgnoreRule>>? getColumnIgnoreRules = null,
        Action<IReadOnlyList<TableColumnIgnoreRule>>? setColumnIgnoreRules = null,
        Func<IReadOnlyList<TableColumnRangeRule>>? getColumnRangeRules = null,
        Action<IReadOnlyList<TableColumnRangeRule>>? setColumnRangeRules = null)
    {
        Comparison = comparison; _source = source; _readOnly = readOnly; _write = write; _rollback = rollback; _compare = compare;
        _cancellation = cancellation; _applicationOptions = applicationOptions;
        _getColumnIgnoreRules = getColumnIgnoreRules ?? (() => Array.Empty<TableColumnIgnoreRule>());
        _setColumnIgnoreRules = setColumnIgnoreRules;
        _getColumnRangeRules = getColumnRangeRules ?? (() => Array.Empty<TableColumnRangeRule>());
        _setColumnRangeRules = setColumnRangeRules;
        EnsureColumnWidths();
        _side.ItemsSource = names; _side.SelectedIndex = 0;
        _useFirstRowAsHeaders.IsChecked = _applicationOptions.Current.UseFirstRowAsHeaders;
        _useFirstRowAsHeaders.IsCheckedChanged += (_, _) =>
        {
            if (_syncingHeaderOption) return;
            UpdateColumnHeaders();
            var requested = _applicationOptions.Current with { UseFirstRowAsHeaders = _useFirstRowAsHeaders.IsChecked == true };
            if (_applicationOptions.SetOptions(requested)) return;
            SetFirstRowAsHeaders(_applicationOptions.Current.UseFirstRowAsHeaders);
            if (_applicationOptions.Diagnostic is { } saveWarning) _status.Text += " · " + saveWarning;
        };
        var top = new StackPanel { Spacing = 4 };
        var headings = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(',', names.Select(_ => "*"))) };
        for (var i = 0; i < names.Count; i++)
        {
            var caption = new TextBlock { Text = names[i], FontWeight = FontWeight.Bold, Margin = new Thickness(6) };
            Grid.SetColumn(caption, i); headings.Children.Add(caption);
        }
        top.Children.Add(headings);
        var editing = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var control in new Control[] { _side, new TextBlock { Text = "元行" }, _row, new TextBlock { Text = "列" }, _column, CellEditor, _useFirstRowAsHeaders })
        { control.Margin = new Thickness(4); editing.Children.Add(control); }
        Button(editing, "セルを選択", () => { SelectSourceCell(); return Task.CompletedTask; });
        Button(editing, "セルを変更", CommitCellAsync);
        Button(editing, "セル編集を戻す", UndoAsync);
        Button(editing, "セル編集をやり直す", RedoAsync);
        Button(editing, "列幅を内容に合わせる", AutoFitColumnsAsync);
        Button(editing, "前の列", () => { _firstColumn = Math.Max(0, _firstColumn - ColumnsPerPage); UpdateRows(); return Task.CompletedTask; });
        Button(editing, "次の列", () => { if (_firstColumn + ColumnsPerPage < Comparison.ColumnCount) _firstColumn += ColumnsPerPage; UpdateRows(); return Task.CompletedTask; });
        top.Children.Add(editing);
        var exclusions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var control in new Control[] { new TextBlock { Text = "行対応から除外する列（1から）" }, _ignoreColumn, _ignoreColumnScope })
        { control.Margin = new Thickness(4); exclusions.Children.Add(control); }
        Button(exclusions, "同じ対象の除外列を置換", () => ChangeColumnIgnoreAsync(false), "table-ignore-column-replace");
        Button(exclusions, "除外列を追加", () => ChangeColumnIgnoreAsync(true), "table-ignore-column-add");
        Button(exclusions, "除外列をすべて解除", () => ApplyColumnIgnoreRulesAsync([]), "table-ignore-column-reset");
        top.Children.Add(exclusions); top.Children.Add(_ignoreColumnSummary);
        UpdateColumnIgnoreSummary();
        var ranges = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var control in new Control[] { new TextBlock { Text = "行対応に使う列範囲" }, _columnRange, _columnRangeScope })
        { control.Margin = new Thickness(4); ranges.Children.Add(control); }
        Button(ranges, "同じ対象の範囲を置換", () => ChangeColumnRangeAsync(false), "table-column-range-replace");
        Button(ranges, "範囲を追加", () => ChangeColumnRangeAsync(true), "table-column-range-add");
        Button(ranges, "範囲指定を解除", () => ApplyColumnRangeRulesAsync([]), "table-column-range-reset");
        top.Children.Add(ranges); top.Children.Add(_columnRangeSummary);
        UpdateColumnRangeSummary();
        var searching = new WrapPanel();
        foreach (var control in new Control[] { SearchText, _case, _regex, _word, _wrap, _allSides })
        { control.Margin = new Thickness(4); searching.Children.Add(control); }
        Button(searching, "前の一致", () => FindAsync(-1)); Button(searching, "次の一致", () => FindAsync(1));
        top.Children.Add(searching); AddSearchActions(top);
        var toolbar = new ScrollViewer { Content = top, MaxHeight = 240,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        // 短いウィンドウや大きいフォントでも、一覧へ表示領域を残す。
        SizeChanged += (_, args) => toolbar.MaxHeight = Math.Clamp(args.NewSize.Height * .5, 40, 240);
        DockPanel.SetDock(toolbar, Dock.Top); Children.Add(toolbar);
        DockPanel.SetDock(_columnHeaders, Dock.Top); Children.Add(_columnHeaders);
        DockPanel.SetDock(_status, Dock.Bottom); Children.Add(_status);
        _rows.ItemTemplate = new FuncDataTemplate<int>((index, _) => RenderRow(index), false);
        Children.Add(_rows);
        UpdateRows();
        if (_applicationOptions.Diagnostic is { } diagnostic) _status.Text += " · " + diagnostic;
    }

    internal void SetFirstRowAsHeaders(bool enabled)
    {
        if (_useFirstRowAsHeaders.IsChecked != enabled)
        {
            _syncingHeaderOption = true;
            try { _useFirstRowAsHeaders.IsChecked = enabled; }
            finally { _syncingHeaderOption = false; }
        }
        UpdateColumnHeaders();
    }

    private void Button(Panel panel, string label, Func<Task> action, string? name = null)
    {
        var button = new Button { Content = label, Margin = new Thickness(4), Name = name };
        button.Click += async (_, _) =>
        {
            if (_busy) return;
            try { _busy = true; PendingOperation = action(); await PendingOperation; }
            catch (OperationCanceledException) { _status.Text = "表の操作を中止しました。"; }
            catch (Exception exception) { _status.Text = exception.Message; }
            finally { _busy = false; }
        };
        panel.Children.Add(button);
    }

    private Task ChangeColumnIgnoreAsync(bool append)
    {
        if (!int.TryParse(_ignoreColumn.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var column)
            || column is < 1 or > TableColumnIgnoreRule.MaximumColumnsAndRules)
            throw new ArgumentException($"除外列は1から{TableColumnIgnoreRule.MaximumColumnsAndRules}の整数で指定してください。");
        if (_ignoreColumnScope.SelectedIndex is not (0 or 1)) throw new ArgumentException("除外する対象ペインを選択してください。");
        var side = _side.SelectedIndex;
        if (_ignoreColumnScope.SelectedIndex == 1 && (uint)side >= (uint)Comparison.Documents.Count)
            throw new ArgumentException("除外するペインを選択してください。");
        var panes = _ignoreColumnScope.SelectedIndex == 0 ? TablePaneMask.All
            : side == 0 ? TablePaneMask.Left : Comparison.Documents.Count == 2 || side == 2 ? TablePaneMask.Right : TablePaneMask.Base;
        var requestedRule = new TableColumnIgnoreRule(column, panes);
        var rules = new List<TableColumnIgnoreRule>();
        var token = _cancellation();
        foreach (var rule in _getColumnIgnoreRules())
        {
            token.ThrowIfCancellationRequested();
            if ((append || rule.Panes != panes) && rule != requestedRule) rules.Add(rule);
        }
        rules.Add(requestedRule);
        return ApplyColumnIgnoreRulesAsync(rules);
    }

    private async Task ApplyColumnIgnoreRulesAsync(IReadOnlyList<TableColumnIgnoreRule> rules)
    {
        EnsureCurrent(); EnsureNoPendingCellEdit();
        if (_setColumnIgnoreRules is null) throw new InvalidOperationException("この表には除外列の設定先がありません。");
        TableColumnIgnoreRule.ValidateRules(rules, _cancellation());
        var before = _getColumnIgnoreRules();
        if (before.SequenceEqual(rules)) return;
        var comparison = Comparison; var scope = CaptureScope();
        _setColumnIgnoreRules(rules);
        var requested = _getColumnIgnoreRules();
        try { await RefreshAfterWriteAsync(); }
        catch
        {
            if (ReferenceEquals(_getColumnIgnoreRules(), requested)) _setColumnIgnoreRules(before);
            Comparison = comparison; UpdateRows(); RestoreScope(scope); UpdateColumnIgnoreSummary();
            throw;
        }
        UpdateColumnIgnoreSummary();
    }

    private void UpdateColumnIgnoreSummary()
    {
        var rules = _getColumnIgnoreRules();
        string Scope(TablePaneMask panes) => panes == TablePaneMask.All ? "全ペイン"
            : string.Join("・", new[] { (TablePaneMask.Left, "左"), (TablePaneMask.Base, "祖先"), (TablePaneMask.Right, "右") }
                .Where(item => (panes & item.Item1) != 0).Select(item => item.Item2));
        _ignoreColumnSummary.Text = rules.Count == 0 ? "行対応から除外する列: なし"
            : "行対応から除外する列: " + string.Join("、", rules.Take(8).Select(rule => $"{rule.Column}列 ({Scope(rule.Panes)})"))
                + (rules.Count > 8 ? $"、ほか{rules.Count - 8}件" : "") + " · セルの差分表示と元の本文は保持します。";
    }

    private Task ChangeColumnRangeAsync(bool append)
    {
        if (_columnRangeScope.SelectedIndex is not (0 or 1)) throw new ArgumentException("範囲指定の対象ペインを選択してください。");
        var side = _side.SelectedIndex;
        if (_columnRangeScope.SelectedIndex == 1 && (uint)side >= (uint)Comparison.Documents.Count)
            throw new ArgumentException("範囲指定を適用するペインを選択してください。");
        var panes = _columnRangeScope.SelectedIndex == 0 ? TablePaneMask.All
            : side == 0 ? TablePaneMask.Left : Comparison.Documents.Count == 2 || side == 2 ? TablePaneMask.Right : TablePaneMask.Base;
        var requestedRule = new TableColumnRangeRule(_columnRange.Text ?? "", panes);
        var token = _cancellation();
        TableColumnRangeRule.ValidateRules([requestedRule], token);
        var rules = new List<TableColumnRangeRule>();
        foreach (var rule in _getColumnRangeRules())
        {
            token.ThrowIfCancellationRequested();
            if (append || rule.Panes != panes) rules.Add(rule);
        }
        if (!rules.Contains(requestedRule)) rules.Add(requestedRule);
        return ApplyColumnRangeRulesAsync(rules);
    }

    private async Task ApplyColumnRangeRulesAsync(IReadOnlyList<TableColumnRangeRule> rules)
    {
        EnsureCurrent(); EnsureNoPendingCellEdit();
        if (_setColumnRangeRules is null) throw new InvalidOperationException("この表には列範囲の設定先がありません。");
        var token = _cancellation();
        TableColumnRangeRule.ValidateRules(rules, token);
        var before = _getColumnRangeRules();
        if (before.SequenceEqual(rules)) return;
        var comparison = Comparison; var scope = CaptureScope();
        _setColumnRangeRules(rules);
        var requested = _getColumnRangeRules();
        try { await RefreshAfterWriteAsync(); }
        catch
        {
            if (ReferenceEquals(_getColumnRangeRules(), requested)) _setColumnRangeRules(before);
            Comparison = comparison; UpdateRows(); RestoreScope(scope); UpdateColumnRangeSummary();
            throw;
        }
        UpdateColumnRangeSummary();
    }

    private void UpdateColumnRangeSummary()
    {
        var rules = _getColumnRangeRules();
        string Scope(TablePaneMask panes) => panes == TablePaneMask.All ? "全ペイン"
            : string.Join("・", new[] { (TablePaneMask.Left, "左"), (TablePaneMask.Base, "祖先"), (TablePaneMask.Right, "右") }
                .Where(item => (panes & item.Item1) != 0).Select(item => item.Item2));
        _columnRangeSummary.Text = rules.Count == 0 ? "行対応に使う列: 全列"
            : "行対応に使う列: " + string.Join("、", rules.Take(6).Select(rule => $"{rule.Specification} ({Scope(rule.Panes)})"))
                + (rules.Count > 6 ? $"、ほか{rules.Count - 6}件" : "") + " · 元のセルとセル差分は保持します。";
    }

    private Control RenderRow(int alignedRow)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(',', Comparison.Documents.Select(_ => "*"))) };
        for (var side = 0; side < Comparison.Documents.Count; side++)
        {
            var visibleColumns = Math.Min(ColumnsPerPage, Math.Max(0, Comparison.ColumnCount - _firstColumn));
            var cells = new Grid { Name = $"table-row-cells-{side}-{alignedRow}", ColumnDefinitions = new ColumnDefinitions(VisibleColumnDefinitions()) };
            var rowKind = Comparison.GetRowKind(side, alignedRow);
            var header = new TextBlock
            {
                Text = Comparison.GetSourceRow(side, alignedRow)?.ToString() ?? "—", Width = 35, Margin = new Thickness(4),
                Name = $"table-row-header-{side}-{alignedRow}", Tag = rowKind,
                Background = Comparison.UsesLineTransforms && rowKind != DiffKind.Equal ? WinMergeColors.Difference : null
            };
            if (Comparison.IsIgnored(side, alignedRow)) ToolTip.SetTip(header, "比較キーが一致した行です。原文には違いがあります。");
            else if (Comparison.UsesLineTransforms && rowKind != DiffKind.Equal)
                ToolTip.SetTip(header, "比較キーに差分があります。セルには元の内容を表示します。");
            cells.Children.Add(header);
            for (var visibleColumn = 0; visibleColumn < visibleColumns; visibleColumn++)
            {
                var column = _firstColumn + visibleColumn;
                var cell = Comparison.GetCell(side, alignedRow, column);
                var kind = Comparison.GetKind(side, alignedRow, column);
                var selectedSide = side; var selectedColumn = column;
                var border = new Border
                {
                    Padding = new Thickness(6), BorderThickness = new Thickness(.5), BorderBrush = Brushes.Gray,
                    Background = cell is null ? WinMergeColors.DifferenceDeleted : kind == DiffKind.Equal ? Brushes.Transparent : WinMergeColors.Difference,
                    Child = new TextBlock { Text = CellPreview(cell?.Value), TextWrapping = TextWrapping.Wrap, MaxHeight = 64, TextTrimming = TextTrimming.CharacterEllipsis }
                };
                // 差分色の上は本家どおり黒文字にし、ダークテーマでも読めるようにする。等しいセルはテーマの文字色を継承する。
                if (cell is null || kind != DiffKind.Equal) ((TextBlock)border.Child).Foreground = WinMergeColors.DifferenceText;
                border.PointerPressed += (_, _) =>
                {
                    try { SelectCell(selectedSide, alignedRow, selectedColumn); }
                    catch (Exception exception) { _status.Text = exception.Message; }
                };
                Grid.SetColumn(border, visibleColumn + 1); cells.Children.Add(border);
            }
            var scroll = new ScrollViewer { Content = cells, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            Grid.SetColumn(scroll, side); grid.Children.Add(scroll);
        }
        return grid;
    }

    private static string CellPreview(string? value)
    {
        if (value is null) return "（セルなし）";
        if (value.Length <= 512) return value;
        var length = char.IsHighSurrogate(value[511]) && char.IsLowSurrogate(value[512]) ? 511 : 512;
        return value[..length] + "…（セル選択で全文）";
    }

    private void UpdateRows()
    {
        EnsureColumnWidths();
        _rows.ItemsSource = Enumerable.Range(0, Comparison.Rows.Count).ToArray();
        _firstColumn = Math.Min(_firstColumn, Math.Max(0, (Comparison.ColumnCount - 1) / ColumnsPerPage * ColumnsPerPage));
        UpdateColumnHeaders();
        _status.Text = $"{Comparison.Rows.Count:N0} 表示行 · 列 {_firstColumn + 1}–{Math.Min(Comparison.ColumnCount, _firstColumn + ColumnsPerPage)} / {Comparison.ColumnCount} · {(Comparison.HasDifferences ? "差があります" : "一致しています")}" +
            (Comparison.AlignmentFallback ? " · 大きな区間には上限付きの行対応を使用しました。" : "");
    }

    private void UpdateColumnHeaders()
    {
        if (_useFirstRowAsHeaders.IsChecked != true || Comparison.ColumnCount == 0)
        {
            _columnHeaders.Child = null;
            _columnHeaders.IsVisible = false;
            return;
        }

        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(',', Comparison.Documents.Select(_ => "*"))) };
        var visibleColumns = Math.Min(ColumnsPerPage, Math.Max(0, Comparison.ColumnCount - _firstColumn));
        for (var side = 0; side < Comparison.Documents.Count; side++)
        {
            IReadOnlyList<TableCell> headerCells = Comparison.Documents[side].Rows.Count > 0
                ? Comparison.Documents[side].Rows[0].Cells : Array.Empty<TableCell>();
            var cells = new Grid { Name = $"table-header-cells-{side}", ColumnDefinitions = new ColumnDefinitions(VisibleColumnDefinitions()) };
            cells.Children.Add(new TextBlock { Text = "列", Width = 35, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center });
            for (var visibleColumn = 0; visibleColumn < visibleColumns; visibleColumn++)
            {
                var column = _firstColumn + visibleColumn;
                var value = column < headerCells.Count ? headerCells[column].Value : "";
                if (value.Length == 0) value = ColumnLabel(column);
                var label = new TextBlock
                {
                    Name = $"table-column-header-{side}-{column}", Text = CellPreview(value), TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 64, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = WinMergeColors.FrameText
                };
                var border = new Border
                {
                    Padding = new Thickness(6), BorderThickness = new Thickness(.5), BorderBrush = Brushes.Gray,
                    Background = WinMergeColors.FrameFace, Child = label
                };
                Grid.SetColumn(border, visibleColumn + 1); cells.Children.Add(border);
            }
            var scroll = new ScrollViewer { Content = cells, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            Grid.SetColumn(scroll, side); panes.Children.Add(scroll);
        }
        _columnHeaders.Child = panes;
        _columnHeaders.IsVisible = true;
    }

    private string VisibleColumnDefinitions()
    {
        var count = Math.Max(1, Math.Min(ColumnsPerPage, Math.Max(0, Comparison.ColumnCount - _firstColumn)));
        var widths = Enumerable.Range(0, count).Select(offset =>
        {
            var column = _firstColumn + offset;
            var width = column < _columnWidths.Length ? _columnWidths[column] : DefaultColumnWidth;
            return width.ToString("0.##", CultureInfo.InvariantCulture);
        });
        return "Auto," + string.Join(',', widths);
    }

    private void EnsureColumnWidths()
    {
        if (_columnWidths.Length == Comparison.ColumnCount) return;
        var widths = Enumerable.Repeat(DefaultColumnWidth, Comparison.ColumnCount).ToArray();
        Array.Copy(_columnWidths, widths, Math.Min(_columnWidths.Length, widths.Length));
        _columnWidths = widths;
    }

    private async Task AutoFitColumnsAsync()
    {
        EnsureCurrent();
        var comparison = Comparison;
        var token = _cancellation();
        var maximumWidth = MaximumAutoFitColumnWidth();
        var includeHeaderFallbacks = _useFirstRowAsHeaders.IsChecked == true;
        var widths = await Task.Run(() => CalculateColumnWidths(comparison, includeHeaderFallbacks, maximumWidth, token), token);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(Comparison, comparison)) throw new OperationCanceledException(token);
        _columnWidths = widths;
        UpdateRows();
    }

    private double MaximumAutoFitColumnWidth()
    {
        var paneCount = Math.Max(1, Comparison.Documents.Count);
        var paneWidth = Bounds.Width > 0 && double.IsFinite(Bounds.Width)
            ? Bounds.Width / paneCount - 50
            : 600;
        return Math.Clamp(Math.Floor(paneWidth), MinimumColumnWidth, 600);
    }

    private static double[] CalculateColumnWidths(TableComparisonResult comparison, bool includeHeaderFallbacks,
        double maximumWidth, CancellationToken token)
    {
        var cellWidths = new int[comparison.ColumnCount];
        var visited = 0;
        foreach (var document in comparison.Documents)
        foreach (var row in document.Rows)
        for (var column = 0; column < Math.Min(row.Cells.Count, cellWidths.Length); column++)
        {
            if ((visited++ & 1023) == 0) token.ThrowIfCancellationRequested();
            cellWidths[column] = Math.Max(cellWidths[column], PreviewCellWidth(row.Cells[column].Value));
        }

        if (includeHeaderFallbacks)
        {
            foreach (var document in comparison.Documents)
            for (var column = 0; column < cellWidths.Length; column++)
            {
                if ((visited++ & 1023) == 0) token.ThrowIfCancellationRequested();
                var header = document.Rows.Count > 0 && column < document.Rows[0].Cells.Count
                    ? document.Rows[0].Cells[column].Value : "";
                if (header.Length == 0) cellWidths[column] = Math.Max(cellWidths[column], PreviewCellWidth(ColumnLabel(column)));
            }
        }

        token.ThrowIfCancellationRequested();
        var widths = new double[cellWidths.Length];
        for (var column = 0; column < widths.Length; column++)
            widths[column] = Math.Clamp(cellWidths[column] * 8d + 13, MinimumColumnWidth, maximumWidth);
        return widths;
    }

    private static int PreviewCellWidth(string value)
    {
        var length = value.Length;
        var truncated = length > 512;
        if (truncated) length = char.IsHighSurrogate(value[511]) && char.IsLowSurrogate(value[512]) ? 511 : 512;

        var maxLine = 0;
        var line = 0;
        for (var index = 0; index < length; index++)
        {
            var character = value[index];
            if (character is '\r' or '\n')
            {
                maxLine = Math.Max(maxLine, line);
                line = 0;
                if (character == '\r' && index + 1 < length && value[index + 1] == '\n') index++;
                continue;
            }
            if (character == '\t') { line++; continue; }
            var codePoint = (int)character;
            if (char.IsHighSurrogate(character) && index + 1 < length && char.IsLowSurrogate(value[index + 1]))
                codePoint = char.ConvertToUtf32(character, value[++index]);
            line += IsWideCell(codePoint) ? 2 : 1;
        }
        if (truncated) line += PreviewCellWidth("…（セル選択で全文）");
        return Math.Max(maxLine, line);
    }

    private static bool IsWideCell(int codePoint)
        => codePoint is >= 0x1100 and <= 0x115F
            or 0x2329 or 0x232A
            or >= 0x2E80 and <= 0xA4CF and not 0x303F
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD;

    private static string ColumnLabel(int column)
    {
        var label = "";
        for (var value = column + 1; value > 0; value = (value - 1) / 26)
            label = (char)('A' + (value - 1) % 26) + label;
        return label;
    }

    private void EnsureCurrent()
    {
        for (var side = 0; side < Comparison.Documents.Count; side++)
            if (_source(side) != Comparison.Documents[side].SourceText)
                throw new InvalidOperationException("テキスト編集後は「再比較」を実行してからセルを操作してください。");
    }

    public bool SelectCell(int side, int alignedRow, int column)
    {
        EnsureNoPendingCellEdit();
        return SelectCellCore(side, alignedRow, column);
    }

    private bool SelectCellCore(int side, int alignedRow, int column)
    {
        EnsureCurrent();
        _lastHit = null; _resumeBoundary = null; _rawLastHit = null; _rawResumeBoundary = null;
        if ((uint)side >= (uint)Comparison.Documents.Count || (uint)alignedRow >= (uint)Comparison.Rows.Count || (uint)column >= (uint)Comparison.ColumnCount)
            throw new ArgumentOutOfRangeException(nameof(column), "存在するペイン・行・列を選択してください。");
        var cell = Comparison.GetCell(side, alignedRow, column);
        _selectedSide = side; _selectedRow = alignedRow; _selectedColumn = column;
        if (column < _firstColumn || column >= _firstColumn + ColumnsPerPage)
        { _firstColumn = column / ColumnsPerPage * ColumnsPerPage; UpdateRows(); }
        _side.SelectedIndex = side; _row.Text = Comparison.GetSourceRow(side, alignedRow)?.ToString() ?? ""; _column.Text = (column + 1).ToString();
        CellEditor.Text = cell?.Value ?? ""; CellEditor.SelectionStart = 0; CellEditor.SelectionEnd = 0; CellEditor.IsReadOnly = cell is null || _readOnly(side);
        _rows.SelectedIndex = alignedRow; _rows.ScrollIntoView(alignedRow);
        _status.Text = cell is null ? "実セルのない行・列は編集できません。" : _readOnly(side) ? "このペインは読取り専用です。検索・選択はできます。" : "セルを変更してから「セルを変更」を押してください。";
        return cell is not null;
    }

    private void SelectSourceCell()
    {
        if (!int.TryParse(_row.Text, out var sourceRow) || !int.TryParse(_column.Text, out var column) || sourceRow < 1 || column < 1)
            throw new ArgumentException("元行と列を1以上の整数で指定してください。");
        var side = _side.SelectedIndex;
        for (var index = 0; index < Comparison.Rows.Count; index++)
            if (Comparison.GetSourceRow(side, index) == sourceRow) { SelectCell(side, index, column - 1); return; }
        throw new ArgumentException("指定された元行はありません。");
    }

    public async Task CommitCellAsync()
    {
        EnsureCurrent();
        if (_selectedRow < 0 || Comparison.GetCell(_selectedSide, _selectedRow, _selectedColumn) is null)
            throw new InvalidOperationException("変更する実セルを選択してください。");
        if (_side.SelectedIndex != _selectedSide || _row.Text != Comparison.GetSourceRow(_selectedSide, _selectedRow)?.ToString()
            || !int.TryParse(_column.Text, out var column) || column != _selectedColumn + 1)
            throw new InvalidOperationException("元行・列・ペインを変更したときは先に「セルを選択」を押してください。");
        if (_readOnly(_selectedSide)) throw new InvalidOperationException("読取り専用のセルは変更できません。");
        var side = _selectedSide; var document = Comparison.Documents[side];
        var edit = StructuredComparer.ReplaceCell(document, Comparison.GetSourceRow(_selectedSide, _selectedRow)!.Value,
            _selectedColumn + 1, CellEditor.Text ?? "", _cancellation());
        var after = document.SourceText.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
        if (after == document.SourceText) return;
        var scope = CaptureScope();
        await WriteAndRefreshAsync(side, after);
        _undo.Push(new(side, document.SourceText, after, scope)); _redo.Clear(); TrimHistory();
    }

    public async Task UndoAsync()
    {
        EnsureNoPendingCellEdit();
        if (!_undo.TryPeek(out var edit)) return;
        if (_readOnly(edit.Side)) throw new InvalidOperationException("読取り専用のセルは変更できません。");
        if (_source(edit.Side) != edit.After) throw new InvalidOperationException("テキスト編集後の内容へ古いセル編集を適用できません。");
        await WriteAndRefreshAsync(edit.Side, edit.Before); _undo.Pop(); _redo.Push(edit); RestoreScope(edit.BeforeScope);
    }

    private void TrimHistory()
    {
        var newest = _undo.ToArray();
        long characters = newest.Sum(edit => (long)edit.Before.Length + edit.After.Length);
        var keep = newest.Length;
        while (keep > 1 && (keep > 100 || characters > 64 * 1024 * 1024))
        { keep--; characters -= (long)newest[keep].Before.Length + newest[keep].After.Length; }
        if (keep != newest.Length) _undo = new Stack<Edit>(newest.Take(keep).Reverse());
    }

    public async Task RedoAsync()
    {
        EnsureNoPendingCellEdit();
        if (!_redo.TryPeek(out var edit)) return;
        if (_readOnly(edit.Side)) throw new InvalidOperationException("読取り専用のセルは変更できません。");
        if (_source(edit.Side) != edit.Before) throw new InvalidOperationException("テキスト編集後の内容へ古いセル編集を適用できません。");
        await WriteAndRefreshAsync(edit.Side, edit.After); _redo.Pop(); _undo.Push(edit); RestoreScope(edit.AfterScope);
    }

    internal Task RefreshAsync()
    {
        EnsureNoPendingCellEdit();
        return RefreshAfterWriteAsync();
    }

    // 再比較が拒否・取消されたときは、この操作が書いた原文だけを戻す。
    private async Task WriteAndRefreshAsync(int side, string after)
    {
        var before = _source(side); var comparison = Comparison; var scope = CaptureScope();
        var draft = CellEditor.Text; var start = CellEditor.SelectionStart; var end = CellEditor.SelectionEnd;
        _write(side, after);
        try { await RefreshAfterWriteAsync(); }
        catch
        {
            if (_source(side) == after) _rollback(side, before);
            Comparison = comparison; UpdateRows(); RestoreScope(scope);
            CellEditor.IsReadOnly = _selectedRow < 0 || Comparison.GetCell(_selectedSide, _selectedRow, _selectedColumn) is null || _readOnly(_selectedSide);
            if (CellEditor.Text == draft) { CellEditor.SelectionStart = start; CellEditor.SelectionEnd = end; }
            throw;
        }
    }

    private async Task RefreshAfterWriteAsync()
    {
        var side = _selectedSide; var row = _selectedRow; var column = _selectedColumn; var draft = CellEditor.Text;
        var sourceRow = row >= 0 ? Comparison.GetSourceRow(side, row) : null;
        var comparison = await _compare();
        if (_selectedSide != side || _selectedRow != row || _selectedColumn != column || CellEditor.Text != draft)
            throw new InvalidOperationException("再比較中にセルの選択・編集が変更されました。操作をやり直してください。");
        ClearSearchSelection(); Comparison = comparison; UpdateRows();
        if (sourceRow is not null)
        {
            for (var index = 0; index < Comparison.Rows.Count; index++)
                if (Comparison.GetSourceRow(_selectedSide, index) == sourceRow && _selectedColumn < Comparison.ColumnCount)
                { SelectCellCore(_selectedSide, index, _selectedColumn); return; }
            // 原文置換で行・列が減った場合も、Undo/Redoに古い座標を残さない。
            var document = Comparison.Documents[side];
            if (document.Rows.Count > 0)
            {
                var target = document.Rows[Math.Min(sourceRow.Value, document.Rows.Count) - 1];
                var aligned = Enumerable.Range(0, Comparison.Rows.Count).First(index => Comparison.GetSourceRow(side, index) == target.SourceRow);
                SelectCellCore(side, aligned, Math.Min(column, target.Cells.Count - 1)); return;
            }
        }
        _selectedRow = -1; _selectedColumn = 0; CellEditor.Text = ""; CellEditor.IsReadOnly = true;
    }

}
