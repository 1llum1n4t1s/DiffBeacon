using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using DiffBeacon.Core;

namespace DiffBeacon.App;

// セルの表示・編集は原文と共通比較モデルへ接続する。CSV 全体の再生成はしない。
public sealed partial class TablePanel : DockPanel
{
    private readonly Func<int, string> _source;
    private readonly Func<int, bool> _readOnly;
    private readonly Action<int, string> _write;
    private readonly Func<Task<TableComparisonResult>> _compare;
    private readonly Func<CancellationToken> _cancellation;
    private readonly ListBox _rows = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };
    private readonly ComboBox _side = new() { Width = 85 };
    private readonly TextBox _row = new() { Width = 60, Text = "1", Name = "table-source-row" };
    private readonly TextBox _column = new() { Width = 60, Text = "1", Name = "table-source-column" };
    public TextBox CellEditor { get; } = new() { AcceptsReturn = true, Width = 280, Height = 65, Name = "table-cell-editor" };
    public TextBox SearchText { get; } = new() { Width = 180, PlaceholderText = "セル内を検索", Name = "table-search" };
    private readonly CheckBox _case = new() { Content = "大小文字を区別" };
    private readonly CheckBox _regex = new() { Content = "正規表現" };
    private readonly CheckBox _word = new() { Content = "単語単位" };
    private readonly CheckBox _wrap = new() { Content = "折返し", IsChecked = true };
    private readonly CheckBox _allSides = new() { Content = "全ペイン", IsChecked = true };
    private Stack<Edit> _undo = new();
    private readonly Stack<Edit> _redo = new();
    private bool _busy;
    private const int ColumnsPerPage = 32;
    private int _firstColumn;
    internal Task? PendingOperation { get; private set; }
    private int _selectedSide, _selectedRow = -1, _selectedColumn;
    private sealed record Edit(int Side, string Before, string After, SearchScopeState? BeforeScope = null, SearchScopeState? AfterScope = null);
    public TableComparisonResult Comparison { get; private set; }

    internal TablePanel(TableComparisonResult comparison, Func<int, string> source, Func<int, bool> readOnly,
        Action<int, string> write, Func<Task<TableComparisonResult>> compare, IReadOnlyList<string> names, Func<CancellationToken> cancellation)
    {
        Comparison = comparison; _source = source; _readOnly = readOnly; _write = write; _compare = compare; _cancellation = cancellation;
        _side.ItemsSource = names; _side.SelectedIndex = 0;
        var top = new StackPanel { Spacing = 4 };
        var headings = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(',', names.Select(_ => "*"))) };
        for (var i = 0; i < names.Count; i++)
        {
            var caption = new TextBlock { Text = names[i], FontWeight = FontWeight.Bold, Margin = new Thickness(6) };
            Grid.SetColumn(caption, i); headings.Children.Add(caption);
        }
        top.Children.Add(headings);
        var editing = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var control in new Control[] { _side, new TextBlock { Text = "元行" }, _row, new TextBlock { Text = "列" }, _column, CellEditor })
        { control.Margin = new Thickness(4); editing.Children.Add(control); }
        Button(editing, "セルを選択", () => { SelectSourceCell(); return Task.CompletedTask; });
        Button(editing, "セルを変更", CommitCellAsync);
        Button(editing, "セル編集を戻す", UndoAsync);
        Button(editing, "セル編集をやり直す", RedoAsync);
        Button(editing, "前の列", () => { _firstColumn = Math.Max(0, _firstColumn - ColumnsPerPage); UpdateRows(); return Task.CompletedTask; });
        Button(editing, "次の列", () => { if (_firstColumn + ColumnsPerPage < Comparison.ColumnCount) _firstColumn += ColumnsPerPage; UpdateRows(); return Task.CompletedTask; });
        top.Children.Add(editing);
        var searching = new WrapPanel();
        foreach (var control in new Control[] { SearchText, _case, _regex, _word, _wrap, _allSides })
        { control.Margin = new Thickness(4); searching.Children.Add(control); }
        Button(searching, "前の一致", () => FindAsync(-1)); Button(searching, "次の一致", () => FindAsync(1));
        top.Children.Add(searching); AddSearchActions(top); top.Children.Add(_status);
        DockPanel.SetDock(top, Dock.Top); Children.Add(top);
        _rows.ItemTemplate = new FuncDataTemplate<int>((index, _) => RenderRow(index), false);
        Children.Add(_rows);
        UpdateRows();
    }

    private void Button(Panel panel, string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(4) };
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

    private Control RenderRow(int alignedRow)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(',', Comparison.Documents.Select(_ => "*"))) };
        for (var side = 0; side < Comparison.Documents.Count; side++)
        {
            var visibleColumns = Math.Min(ColumnsPerPage, Math.Max(0, Comparison.ColumnCount - _firstColumn));
            var cells = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto," + string.Join(',', Enumerable.Repeat("140", Math.Max(1, visibleColumns)))) };
            cells.Children.Add(new TextBlock { Text = Comparison.GetSourceRow(side, alignedRow)?.ToString() ?? "—", Width = 35, Margin = new Thickness(4) });
            for (var visibleColumn = 0; visibleColumn < visibleColumns; visibleColumn++)
            {
                var column = _firstColumn + visibleColumn;
                var cell = Comparison.GetCell(side, alignedRow, column);
                var kind = Comparison.GetKind(side, alignedRow, column);
                var selectedSide = side; var selectedColumn = column;
                var border = new Border
                {
                    Padding = new Thickness(6), BorderThickness = new Thickness(.5), BorderBrush = Brushes.Gray,
                    Background = cell is null ? new SolidColorBrush(Color.Parse("#303030")) : kind == DiffKind.Equal ? Brushes.Transparent : new SolidColorBrush(Color.Parse("#553D2847")),
                    Child = new TextBlock { Text = CellPreview(cell?.Value), TextWrapping = TextWrapping.Wrap, MaxHeight = 64, TextTrimming = TextTrimming.CharacterEllipsis }
                };
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
        _rows.ItemsSource = Enumerable.Range(0, Comparison.Rows.Count).ToArray();
        _firstColumn = Math.Min(_firstColumn, Math.Max(0, (Comparison.ColumnCount - 1) / ColumnsPerPage * ColumnsPerPage));
        _status.Text = $"{Comparison.Rows.Count:N0} 表示行 · 列 {_firstColumn + 1}–{Math.Min(Comparison.ColumnCount, _firstColumn + ColumnsPerPage)} / {Comparison.ColumnCount} · {(Comparison.HasDifferences ? "差があります" : "一致しています")}" +
            (Comparison.AlignmentFallback ? " · 大きな区間には上限付きの行対応を使用しました。" : "");
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
        _lastHit = null; _resumeBoundary = null;
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
            if (_source(side) == after) _write(side, before);
            Comparison = comparison; UpdateRows(); RestoreScope(scope);
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
            for (var index = 0; index < Comparison.Rows.Count; index++)
                if (Comparison.GetSourceRow(_selectedSide, index) == sourceRow) { if (_selectedColumn < Comparison.ColumnCount) SelectCellCore(_selectedSide, index, _selectedColumn); break; }
    }

}
