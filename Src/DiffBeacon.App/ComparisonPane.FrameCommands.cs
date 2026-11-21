using Avalonia.Controls;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMergeのメニュー・ツールバー（ID_NEXTDIFF などのコマンドID）に対応する、ウィンドウ外枠からの操作。
/// 外枠は個々のペインの内部状態を知らずに、この列挙値だけでコマンドを送る。
/// </summary>
internal enum FrameCommand
{
    Compare, Refresh, Options,
    SaveLeft, SaveRight, SaveLeftAs, SaveRightAs, OpenProject, SaveProject,
    Undo, Redo, Cut, Copy, Paste, SelectAll, CopyWithLineNumbers, CopySelectedDifference1, CopySelectedDifference2, CopySelectedDifference3,
    CopyLinesToLeft, CopyLinesToRight, CopyLinesToMiddle, AddToLineFilters, AddToDisplayFilters, AddToSubstitutionFilters, AddMarker,
    OpenRegistered, OpenParentFolder, OpenExternalEditor, GoToDefinition,
    ReadOnlyLeft, ReadOnlyMiddle, ReadOnlyRight, EolToWindows, EolToUnix, EolToMac, OpenClipboard, OpenConflict,
    ToggleBookmark, NextBookmark, PreviousBookmark, ClearAllBookmarks, Find, Replace, FindNext, FindPrevious, FindSelectedNext, FindSelectedPrevious, GoTo,
    FirstDifference, PreviousDifference, CurrentDifference, NextDifference, LastDifference,
    NextConflict, PreviousConflict, SelectLineDifference, SelectPreviousLineDifference,
    CopyToRight, CopyToLeft, CopyToRightAndAdvance, CopyToLeftAndAdvance, CopyAllToRight, CopyAllToLeft, AutoMerge,
    GeneratePatch, GenerateReport, GenerateArchive, AddExternalTool, Stop, SaveMergeResult, CustomizeColumns,
}

public sealed partial class ComparisonPane
{
    /// <summary>
    /// 外枠のメニュー・ツールバーから受けたコマンドを、ペイン内の既存ボタンと同じ処理経路で実行する。
    /// 例外と取消は既存の <see cref="GuardAsync"/> に任せ、ペイン内ボタンとの挙動差を作らない。
    /// </summary>
    internal Task ExecuteFrameCommandAsync(FrameCommand command) => GuardAsync(() => command switch
    {
        FrameCommand.Compare => ObserveCriticalTextSaveTask("path-compare", ComparePathsAsync()),
        FrameCommand.Refresh => ObserveCriticalTextSaveTask("route-compare", RefreshEditorsAsync()),
        FrameCommand.Options => EditProjectOptionsAsync(),
        FrameCommand.SaveLeft => ObserveCriticalTextSaveTask("左を保存", SaveAsync(false)),
        FrameCommand.SaveRight => ObserveCriticalTextSaveTask("右を保存", SaveAsync(true)),
        FrameCommand.SaveLeftAs => ObserveCriticalTextSaveTask("左を外部保存", SaveTextAsAsync(false)),
        FrameCommand.SaveRightAs => ObserveCriticalTextSaveTask("右を外部保存", SaveTextAsAsync(true)),
        FrameCommand.OpenProject => ObserveCriticalTextSaveTask("project-open", OpenProjectAsync()),
        FrameCommand.SaveProject => ObserveCriticalTextSaveTask("project-save", SaveProjectAsync()),
        FrameCommand.Undo => Run(() => { if (FocusedEditor() is { } editor) editor.Undo(); else if (DiffTabActive()) UndoText(); }),
        FrameCommand.Redo => Run(() => { if (FocusedEditor() is { } editor) editor.Redo(); else if (DiffTabActive()) RedoText(); }),
        FrameCommand.Cut => CopyDiffSelectionAsync(cut: true),
        FrameCommand.Copy => CopyDiffSelectionAsync(cut: false),
        FrameCommand.Paste => PasteDiffTextAsync(),
        FrameCommand.SelectAll => Run(SelectAllDiffText),
        FrameCommand.CopyWithLineNumbers => CopyDiffLinesWithNumbersAsync(),
        FrameCommand.CopySelectedDifference1 => CopySelectedDifferenceAsync(0),
        FrameCommand.CopySelectedDifference2 => CopySelectedDifferenceAsync(1),
        FrameCommand.CopySelectedDifference3 => CopySelectedDifferenceAsync(2),
        FrameCommand.CopyLinesToLeft => Run(() => CopySelectedLines(0)),
        FrameCommand.CopyLinesToRight => Run(() => CopySelectedLines(2)),
        FrameCommand.CopyLinesToMiddle => Run(() => CopySelectedLines(1)),
        FrameCommand.AddToLineFilters => Run(AddSelectionToLineFilter),
        FrameCommand.AddToDisplayFilters => Run(AddSelectionToDisplayFilter),
        FrameCommand.AddToSubstitutionFilters => AddSelectionToSubstitutionFilterAsync(),
        FrameCommand.AddMarker => AddTextMarkerAsync(),
        FrameCommand.OpenRegistered => Run(OpenWithRegisteredApplication),
        FrameCommand.OpenParentFolder => Run(OpenParentFolder),
        FrameCommand.OpenExternalEditor => Run(OpenWithExternalEditor),
        FrameCommand.GoToDefinition => Run(GoToDefinition),
        FrameCommand.ReadOnlyLeft => Run(() => SetManualReadOnly(0, !_manualReadOnly[0])),
        FrameCommand.ReadOnlyMiddle => Run(() => SetManualReadOnly(1, !_manualReadOnly[1])),
        FrameCommand.ReadOnlyRight => Run(() => SetManualReadOnly(2, !_manualReadOnly[2])),
        FrameCommand.EolToWindows => Run(() => ConvertLineEndings("\r\n")),
        FrameCommand.EolToUnix => Run(() => ConvertLineEndings("\n")),
        FrameCommand.EolToMac => Run(() => ConvertLineEndings("\r")),
        FrameCommand.OpenClipboard => OpenClipboardTextAsync(),
        FrameCommand.OpenConflict => OpenConflictFromDialogAsync(),
        FrameCommand.ToggleBookmark or FrameCommand.NextBookmark or FrameCommand.PreviousBookmark or FrameCommand.ClearAllBookmarks
            => Run(() => ExecuteBookmarkCommand(command)),
        FrameCommand.Find => Run(() => OpenTextSearch(replace: false)),
        FrameCommand.Replace => Run(() => OpenTextSearch(replace: true)),
        FrameCommand.FindNext => Run(() => RepeatTextSearch(backward: false, selected: false)),
        FrameCommand.FindPrevious => Run(() => RepeatTextSearch(backward: true, selected: false)),
        FrameCommand.FindSelectedNext => Run(() => RepeatTextSearch(backward: false, selected: true)),
        FrameCommand.FindSelectedPrevious => Run(() => RepeatTextSearch(backward: true, selected: true)),
        FrameCommand.GoTo => GoToAsync(),
        FrameCommand.FirstDifference => Run(() => NavigateToDifference(first: true)),
        FrameCommand.PreviousDifference => Run(() => NavigateDifference(-1)),
        FrameCommand.CurrentDifference => Run(NavigateToCurrentDifference),
        FrameCommand.NextDifference => Run(() => NavigateDifference(1)),
        FrameCommand.LastDifference => Run(() => NavigateToDifference(first: false)),
        FrameCommand.NextConflict => Run(() => NavigateConflict(1)),
        FrameCommand.PreviousConflict => Run(() => NavigateConflict(-1)),
        FrameCommand.SelectLineDifference => Run(() => SelectLineDifference(reverse: false)),
        FrameCommand.SelectPreviousLineDifference => Run(() => SelectLineDifference(reverse: true)),
        FrameCommand.CopyToRight => CopySelectionAsync(true),
        FrameCommand.CopyToLeft => CopySelectionAsync(false),
        FrameCommand.CopyToRightAndAdvance => CopyAndAdvanceAsync(true),
        FrameCommand.CopyToLeftAndAdvance => CopyAndAdvanceAsync(false),
        FrameCommand.CopyAllToRight => CopyAllAsync(true),
        FrameCommand.CopyAllToLeft => CopyAllAsync(false),
        FrameCommand.AutoMerge => MergeThreeWayAsync(),
        FrameCommand.GeneratePatch => ExportPatchAsync(),
        FrameCommand.GenerateReport => ExportReportAsync(),
        FrameCommand.GenerateArchive => ObserveCriticalTextSaveTask("archive-create", CreateArchiveAsync()),
        FrameCommand.AddExternalTool => AddExternalProviderAsync(),
        FrameCommand.Stop => Run(StopAllOperations),
        FrameCommand.SaveMergeResult => SaveResultAsync(),
        FrameCommand.CustomizeColumns => CustomizeFolderColumnsAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    });

    private static Task Run(Action action) { action(); return Task.CompletedTask; }

    // 差分タブの本文は全ペイン共有の履歴（本家と同じ）。ツールバーのボタンへフォーカスが移っていても、差分タブを表示していれば対象にする。
    private bool DiffTabActive() => ReferenceEquals(_views.SelectedItem, _diffTab);

    // 差分タブ以外の編集欄（マージ結果など）は、その欄自身の履歴を使う。入力フォーカスのある編集欄を対象にする。
    private TextBox? FocusedEditor()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return focused is TextBox box && (ReferenceEquals(box, LeftEditor) || ReferenceEquals(box, RightEditor)
            || ReferenceEquals(box, MiddleEditor) || ReferenceEquals(box, ResultEditor)) ? box : null;
    }

    // ID_FIRSTDIFF / ID_LASTDIFF: 差分一覧の先頭または末尾のブロックへ移動する。
    private void NavigateToDifference(bool first)
    {
        if (CurrentDiff is not { Blocks.Count: > 0 }) return;
        _diffIndex = first ? 0 : CurrentDiff.Blocks.Count - 1;
        DiffList.SelectedIndex = CurrentDiff.Blocks[_diffIndex].RowStart;
        RevealDifference(CurrentDiff.Blocks[_diffIndex]);
    }

    // ID_CURDIFF: 選択中の差分を再表示する。未選択なら最初の差分へ移動する。
    private void NavigateToCurrentDifference()
    {
        if (CurrentDiff is not { Blocks.Count: > 0 }) return;
        if (_diffIndex < 0) { NavigateToDifference(first: true); return; }
        DiffList.SelectedIndex = CurrentDiff.Blocks[_diffIndex].RowStart;
        RevealDifference(CurrentDiff.Blocks[_diffIndex]);
    }

    // ID_L2RNEXT / ID_R2LNEXT: 現在の差分をコピーしてから次の差分へ進む。
    private async Task CopyAndAdvanceAsync(bool toRight)
    {
        await CopySelectionAsync(toRight);
        NavigateDifference(1);
    }

    // ペイン内の「中止」ボタンと同じ取消範囲。
    private void StopAllOperations()
    {
        StopFolderCopy(); _operation?.Cancel(); _reportOperation?.Cancel(); _textSaveOperation?.Cancel();
        (_specialTab.Content as ArchivePanel)?.CancelOperation();
    }
}
