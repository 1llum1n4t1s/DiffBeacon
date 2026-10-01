using Avalonia;
using Avalonia.Controls;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public sealed partial class TablePanel
{
    public TextBox ReplacementText { get; } = new() { Width = 280, PlaceholderText = "置換後のセル内文字列", Name = "table-replacement" };
    private readonly CheckBox _selectionOnly = new() { Content = "選択範囲だけ" };
    private readonly TextBlock _scopeCaption = new() { Text = "範囲: 未指定", Margin = new Thickness(4) };
    private sealed record SearchHit(int Side, int SourceRow, int Column, int Start, int Length);
    private sealed record SearchScopeState(int Side, TableSearchRange Range, bool Enabled);
    private SearchHit? _lastHit;
    private SearchHit? _resumeBoundary;
    private TableSearchOptions? _lastQuery;
    private TableSearchRange? _searchRange;
    private int _searchRangeSide;
    private int _lastDirection = 1;
    private bool _applyingOperation;

    private void AddSearchActions(Panel top)
    {
        var actions = new WrapPanel();
        ReplacementText.Margin = new Thickness(4); actions.Children.Add(ReplacementText);
        Button(actions, "この一致を置換", ReplaceCurrentAsync); Button(actions, "ペイン内を全置換", ReplaceAllAsync);
        Button(actions, "未反映セルを破棄", () => { DiscardCellDraft(); return Task.CompletedTask; });
        top.Children.Add(actions);
        var scope = new WrapPanel(); scope.Children.Add(_selectionOnly);
        Button(scope, "検索範囲を固定", () => { PinSearchSelection(); return Task.CompletedTask; });
        Button(scope, "検索範囲を解除", () => { ClearSearchSelection(); return Task.CompletedTask; });
        scope.Children.Add(_scopeCaption); top.Children.Add(scope);
    }

    private TableSearchOptions SearchOptions() => new(SearchText.Text ?? "", _case.IsChecked == true, _regex.IsChecked == true, _word.IsChecked == true);

    public bool HasPendingCellEdit => _selectedRow >= 0 && _selectedRow < Comparison.Rows.Count && _selectedColumn < Comparison.ColumnCount
        && CellEditor.Text != (Comparison.GetCell(_selectedSide, _selectedRow, _selectedColumn)?.Value ?? "");

    internal void EnsureNoPendingCellEdit()
    {
        if (HasPendingCellEdit) throw new InvalidOperationException("セル編集が未反映です。「セルを変更」または「未反映セルを破棄」を押してください。");
    }

    public void DiscardCellDraft()
    {
        if (_selectedRow >= 0 && _selectedRow < Comparison.Rows.Count && _selectedColumn < Comparison.ColumnCount)
            CellEditor.Text = Comparison.GetCell(_selectedSide, _selectedRow, _selectedColumn)?.Value ?? "";
        _lastHit = null; _resumeBoundary = null;
    }

    public void PinSearchSelection()
    {
        EnsureCurrent(); EnsureNoPendingCellEdit();
        if (_selectedRow < 0 || Comparison.GetCell(_selectedSide, _selectedRow, _selectedColumn) is null)
            throw new InvalidOperationException("実セルの文字範囲を選択してください。");
        var start = Math.Min(CellEditor.SelectionStart, CellEditor.SelectionEnd); var end = Math.Max(CellEditor.SelectionStart, CellEditor.SelectionEnd);
        if (start == end) throw new InvalidOperationException("固定する文字範囲を選択してください。");
        var value = CellEditor.Text ?? "";
        if (!Boundary(value, start) || !Boundary(value, end)) throw new InvalidOperationException("Unicode文字またはCRLFの途中を範囲端にできません。");
        _searchRange = new(Comparison.GetSourceRow(_selectedSide, _selectedRow)!.Value, _selectedColumn + 1, start, end - start);
        _searchRangeSide = _selectedSide; _selectionOnly.IsChecked = true; _lastHit = null; _resumeBoundary = null;
        UpdateScopeCaption();
    }

    public void ClearSearchSelection()
    {
        _searchRange = null; _selectionOnly.IsChecked = false; _lastHit = null; _resumeBoundary = null;
        UpdateScopeCaption();
    }

    private void UpdateScopeCaption() => _scopeCaption.Text = _searchRange is null ? "範囲: 未指定" :
        $"範囲: {_side.Items[_searchRangeSide]} · 元行 {_searchRange.SourceRow} / 列 {_searchRange.Column} · 文字 {_searchRange.Start + 1}–{_searchRange.Start + _searchRange.Length}";
    private SearchScopeState? CaptureScope() => _searchRange is null ? null : new(_searchRangeSide, _searchRange, _selectionOnly.IsChecked == true);
    private void RestoreScope(SearchScopeState? state)
    {
        if (state is null) return;
        _searchRangeSide = state.Side; _searchRange = state.Range; _selectionOnly.IsChecked = state.Enabled; UpdateScopeCaption();
    }

    private TableSearchRange? ActiveRange(int side)
    {
        if (_selectionOnly.IsChecked != true) return null;
        if (_searchRange is null) throw new InvalidOperationException("先に文字を選択し「検索範囲を固定」を押してください。");
        if (_searchRangeSide != side) throw new InvalidOperationException("固定範囲と同じペインを選択してください。");
        return _searchRange;
    }

    private static bool Boundary(string value, int index) => index >= 0 && index <= value.Length &&
        !(index > 0 && index < value.Length && (char.IsHighSurrogate(value[index - 1]) && char.IsLowSurrogate(value[index]) || value[index - 1] == '\r' && value[index] == '\n'));
    private static int AfterScalar(string value, int index)
    {
        if (index >= value.Length) return value.Length + 1;
        return index + (index + 1 < value.Length && (char.IsHighSurrogate(value[index]) && char.IsLowSurrogate(value[index + 1]) || value[index] == '\r' && value[index + 1] == '\n') ? 2 : 1);
    }

    public async Task FindAsync(int direction)
    {
        EnsureCurrent(); EnsureNoPendingCellEdit();
        var options = SearchOptions(); if (options.Pattern.Length == 0) { _lastHit = null; return; }
        var engine = new TableSearchEngine(options); var comparison = Comparison; var token = _cancellation();
        var side = _side.SelectedIndex; var scope = ActiveRange(side); var allSides = scope is null && _allSides.IsChecked == true;
        var scopeState = CaptureScope(); var rangeSide = _searchRangeSide;
        var selectionStart = CellEditor.SelectionStart; var selectionEnd = CellEditor.SelectionEnd;
        var wrap = _wrap.IsChecked == true; var cursor = options == _lastQuery && _lastHit?.Side == side &&
            _lastHit.SourceRow == (_selectedRow >= 0 ? Comparison.GetSourceRow(_selectedSide, _selectedRow) : null) &&
            _lastHit.Column == _selectedColumn + 1 && Math.Min(selectionStart, selectionEnd) == _lastHit.Start &&
            Math.Max(selectionStart, selectionEnd) == _lastHit.Start + _lastHit.Length ? _lastHit : null;
        var resume = options == _lastQuery && _resumeBoundary?.Side == side && _selectedRow >= 0 &&
            Comparison.GetSourceRow(_selectedSide, _selectedRow) == _resumeBoundary.SourceRow &&
            _selectedColumn + 1 == _resumeBoundary.Column ? _resumeBoundary : null;
        var currentRow = _selectedRow; var currentColumn = _selectedColumn; var selectedSide = _selectedSide;
        var columns = Math.Max(1, comparison.ColumnCount); var sides = comparison.Documents.Count;
        var currentRank = currentRow < 0 ? (direction >= 0 ? -1L : long.MaxValue) : ((long)currentRow * sides + side) * columns + currentColumn;
        var boundary = direction >= 0 ? Math.Min(CellEditor.SelectionStart, CellEditor.SelectionEnd) : Math.Max(CellEditor.SelectionStart, CellEditor.SelectionEnd);
        if (side != selectedSide) boundary = direction >= 0 ? 0 : int.MaxValue;
        if (cursor is not null)
        {
            boundary = direction >= 0 ? cursor.Start + cursor.Length : cursor.Start;
            if (direction >= 0 && cursor.Length == 0) boundary = AfterScalar(CellEditor.Text ?? "", cursor.Start);
        }
        else if (resume is not null) boundary = resume.Start;
        // 変更した検索語は選択セルの先頭／末尾から調べる。
        if (options != _lastQuery) boundary = direction >= 0 ? scope?.Start ?? 0 : scope is null ? int.MaxValue : scope.Start + scope.Length;
        var result = await Task.Run(() =>
        {
            for (var pass = 0; pass < (wrap ? 2 : 1); pass++)
            {
                for (var rowIndex = 0; rowIndex < comparison.Rows.Count; rowIndex++)
                {
                    token.ThrowIfCancellationRequested();
                    var row = direction >= 0 ? rowIndex : comparison.Rows.Count - rowIndex - 1;
                    for (var sideIndex = 0; sideIndex < sides; sideIndex++)
                    {
                        var candidateSide = direction >= 0 ? sideIndex : sides - sideIndex - 1;
                        if ((!allSides && candidateSide != side) || scope is not null && candidateSide != rangeSide) continue;
                        var sourceRow = comparison.GetSourceRow(candidateSide, row); if (sourceRow is null) continue;
                        if (scope is not null && sourceRow != scope.SourceRow) continue;
                        var cells = comparison.Documents[candidateSide].Rows[sourceRow.Value - 1].Cells;
                        for (var cellIndex = 0; cellIndex < cells.Count; cellIndex++)
                        {
                            token.ThrowIfCancellationRequested();
                            var column = direction >= 0 ? cellIndex : cells.Count - cellIndex - 1;
                            if (scope is not null && column + 1 != scope.Column) continue;
                            var rank = ((long)row * sides + candidateSide) * columns + column;
                            if (pass == 0 && (direction >= 0 ? rank < currentRank : rank > currentRank) ||
                                pass == 1 && (direction >= 0 ? rank > currentRank : rank < currentRank)) continue;
                            SearchHit? found = null;
                            foreach (var match in engine.Matches(cells[column].Value, scope?.Start ?? 0, scope?.Length, token))
                            {
                                if (rank == currentRank)
                                {
                                    var follows = direction >= 0 ? match.Index >= boundary : match.Index + match.Length <= boundary &&
                                        (cursor is null || match.Index < cursor.Start);
                                    if (follows != (pass == 0)) continue;
                                }
                                found = new(candidateSide, sourceRow.Value, column + 1, match.Index, match.Length);
                                if (direction >= 0) break;
                            }
                            if (found is not null) return found;
                        }
                    }
                }
            }
            return null;
        }, token);
        token.ThrowIfCancellationRequested(); EnsureCurrent(); EnsureNoPendingCellEdit();
        if (!ReferenceEquals(Comparison, comparison) || SearchOptions() != options || CaptureScope() != scopeState || _side.SelectedIndex != side ||
            _selectedRow != currentRow || _selectedColumn != currentColumn || _selectedSide != selectedSide ||
            CellEditor.SelectionStart != selectionStart || CellEditor.SelectionEnd != selectionEnd ||
            (scope is null && _allSides.IsChecked == true) != allSides || (_wrap.IsChecked == true) != wrap)
            throw new InvalidOperationException("検索中に本文・設定・範囲が変更されました。検索をやり直してください。");
        _lastQuery = options; _resumeBoundary = null; _lastDirection = direction;
        if (result is null) { _status.Text = "一致する文字列はありません。"; return; }
        var aligned = Enumerable.Range(0, comparison.Rows.Count).First(index => comparison.GetSourceRow(result.Side, index) == result.SourceRow);
        SelectCellCore(result.Side, aligned, result.Column - 1); CellEditor.Focus();
        CellEditor.SelectionStart = result.Start; CellEditor.SelectionEnd = result.Start + result.Length;
        _lastHit = result;
        _status.Text = $"元行 {result.SourceRow} / 列 {result.Column} · 文字 {result.Start + 1} · {result.Length}文字の一致";
    }

    public Task ReplaceCurrentAsync() => ReplaceAsync(false);
    public Task ReplaceAllAsync() => ReplaceAsync(true);

    private async Task ReplaceAsync(bool all)
    {
        if (_applyingOperation) throw new InvalidOperationException("置換が進行中です。");
        EnsureCurrent(); EnsureNoPendingCellEdit();
        var side = _side.SelectedIndex; if (_readOnly(side)) throw new InvalidOperationException("読取り専用のペインは置換できません。");
        var scope = ActiveRange(side); var options = SearchOptions(); var engine = new TableSearchEngine(options);
        if (!all && (_lastHit is null || _lastHit.Side != side || _lastQuery != options || _selectedSide != side ||
            _selectedRow < 0 || Comparison.GetSourceRow(side, _selectedRow) != _lastHit.SourceRow || _selectedColumn + 1 != _lastHit.Column ||
            Math.Min(CellEditor.SelectionStart, CellEditor.SelectionEnd) != _lastHit.Start ||
            Math.Max(CellEditor.SelectionStart, CellEditor.SelectionEnd) != _lastHit.Start + _lastHit.Length))
            throw new InvalidOperationException("現在の検索条件で一致を選択してから置換してください。");
        var hit = _lastHit; var selectedRow = _selectedRow; var selectedColumn = _selectedColumn; var selectedSide = _selectedSide;
        var selectionStart = CellEditor.SelectionStart; var selectionEnd = CellEditor.SelectionEnd;
        var comparison = Comparison; var document = comparison.Documents[side]; var replacement = ReplacementText.Text ?? "";
        var token = _cancellation(); var beforeScope = CaptureScope();
        try
        {
            _applyingOperation = true;
            var plan = await Task.Run(() => engine.Replace(document, replacement, scope,
                all ? null : new(hit!.SourceRow, hit.Column, hit.Start, hit.Length), token), token);
            token.ThrowIfCancellationRequested(); EnsureCurrent(); EnsureNoPendingCellEdit();
            if (!ReferenceEquals(Comparison, comparison) || SearchOptions() != options || ReplacementText.Text != replacement ||
                _side.SelectedIndex != side || _readOnly(side) || CaptureScope() != beforeScope ||
                !all && (_lastHit != hit || _selectedRow != selectedRow || _selectedColumn != selectedColumn ||
                    _selectedSide != selectedSide || CellEditor.SelectionStart != selectionStart || CellEditor.SelectionEnd != selectionEnd))
                throw new InvalidOperationException("置換中に本文・設定・範囲が変更されました。原文は更新していません。");
            var afterScope = beforeScope;
            if (scope is not null && beforeScope is not null)
                afterScope = beforeScope with { Range = scope with { Length = checked(scope.Length + plan.Changes.Sum(change => change.NewLength - change.Length)) } };
            if (plan.Text != document.SourceText)
            {
                await WriteAndRefreshAsync(side, plan.Text);
                _undo.Push(new(side, document.SourceText, plan.Text, beforeScope, afterScope)); _redo.Clear(); TrimHistory(); RestoreScope(afterScope);
            }
            _lastQuery = options; _lastHit = null; _resumeBoundary = null;
            _status.Text = $"選択ペインで {plan.Count:N0} 件を置換しました。";
            if (!all && plan.Changes.Count != 0)
            {
                var change = plan.Changes[0];
                var aligned = Enumerable.Range(0, Comparison.Rows.Count).First(index => Comparison.GetSourceRow(side, index) == change.SourceRow);
                SelectCellCore(side, aligned, change.Column - 1);
                var position = _lastDirection >= 0 ? change.Start + change.NewLength : change.Start;
                if (_lastDirection >= 0 && change.Length == 0) position = AfterScalar(CellEditor.Text ?? "", position);
                _resumeBoundary = new(side, change.SourceRow, change.Column, position, 0);
                try { await FindAsync(_lastDirection); }
                catch (OperationCanceledException) { _status.Text = "置換は完了しました。次の一致の検索を中止しました。"; }
            }
        }
        finally { _applyingOperation = false; }
    }
}
