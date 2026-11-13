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
    Undo, Redo, Find,
    FirstDifference, PreviousDifference, CurrentDifference, NextDifference, LastDifference,
    NextConflict, CopyToRight, CopyToLeft, CopyToRightAndAdvance, CopyToLeftAndAdvance, AutoMerge,
    GeneratePatch, GenerateReport, GenerateArchive, AddExternalTool, Stop, SaveMergeResult,
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
        FrameCommand.Undo => Run(() => FocusedEditor()?.Undo()),
        FrameCommand.Redo => Run(() => FocusedEditor()?.Redo()),
        FrameCommand.Find => Run(Find),
        FrameCommand.FirstDifference => Run(() => NavigateToDifference(first: true)),
        FrameCommand.PreviousDifference => Run(() => NavigateDifference(-1)),
        FrameCommand.CurrentDifference => Run(NavigateToCurrentDifference),
        FrameCommand.NextDifference => Run(() => NavigateDifference(1)),
        FrameCommand.LastDifference => Run(() => NavigateToDifference(first: false)),
        FrameCommand.NextConflict => Run(NavigateConflict),
        FrameCommand.CopyToRight => CopySelectionAsync(true),
        FrameCommand.CopyToLeft => CopySelectionAsync(false),
        FrameCommand.CopyToRightAndAdvance => CopyAndAdvanceAsync(true),
        FrameCommand.CopyToLeftAndAdvance => CopyAndAdvanceAsync(false),
        FrameCommand.AutoMerge => MergeThreeWayAsync(),
        FrameCommand.GeneratePatch => ExportPatchAsync(),
        FrameCommand.GenerateReport => ExportReportAsync(),
        FrameCommand.GenerateArchive => ObserveCriticalTextSaveTask("archive-create", CreateArchiveAsync()),
        FrameCommand.AddExternalTool => AddExternalProviderAsync(),
        FrameCommand.Stop => Run(StopAllOperations),
        FrameCommand.SaveMergeResult => SaveResultAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    });

    private static Task Run(Action action) { action(); return Task.CompletedTask; }

    // 本家の「元に戻す」「やり直し」は直前に操作したペインに作用する。ここでは入力フォーカスのある編集欄を対象にする。
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
