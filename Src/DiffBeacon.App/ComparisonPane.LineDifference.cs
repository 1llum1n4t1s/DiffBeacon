using DiffBeacon.Core;

namespace DiffBeacon.App;

/// <summary>
/// 本家の「行内差異を選択」（ID_SELECTLINEDIFF＝F4、ID_SELECTPREVLINEDIFF＝Shift+F4、CMergeDoc::Computelinediff）。
/// フォーカスのあるペインで、キャレットの位置から次（前）の行内差分の文字列を選択する。
/// 行内差分の無い差分の行（片側だけにある行・行内差分を省略した行）は、行全体を一つの対象にする。
/// 最後の対象の次は最初の対象へ回る（本家も差分ブロックを循環する）。
/// </summary>
public sealed partial class ComparisonPane
{
    // 直前に選択した対象。キャレットや選択がそのまま残っていれば、そこから次（前）へ進む。
    private (int Side, int Start, int Length, DiffResult Diff)? _lineDifferenceSelection;

    /// <summary>行内差異を選択する。選択できたら true。差分タブの表示が正本と食い違う（再比較の待機中）間は選ばない。</summary>
    internal bool SelectLineDifference(bool reverse)
    {
        var sideIndex = ActiveLineBookmarkSide;
        if (sideIndex == 1 && !_diffDisplay.ThreeWay) sideIndex = 0;
        var side = _diffViews[sideIndex];
        if (CurrentDiff is not { Blocks.Count: > 0 } diff || _diffDisplay.RowCount == 0)
        { _status.Text = "選択する差異がありません。"; return false; }
        // 本家は再比較の前（編集した後）は使えない（OnUpdateSelectLineDiff）。
        if (side.SourceText != TextEditor(sideIndex).Text || _diffEditTimer?.IsEnabled == true)
        { _status.Text = "編集後の再比較が終わってから行内差異を選択してください。"; return false; }

        var targets = LineDifferenceTargets(side);
        if (targets.Count == 0) { _status.Text = "選択する差異がありません。"; return false; }
        var area = side.View.TextArea;
        var selectionStart = area.Selection.IsEmpty ? area.Caret.Offset : area.Selection.SurroundingSegment.Offset;
        var selectionEnd = area.Selection.IsEmpty ? area.Caret.Offset : area.Selection.SurroundingSegment.EndOffset;

        int index;
        var current = _lineDifferenceSelection is { } last && last.Side == sideIndex && ReferenceEquals(last.Diff, diff)
            && last.Start == selectionStart && last.Start + last.Length == selectionEnd
            ? targets.FindIndex(target => target.Start == last.Start && target.Length == last.Length) : -1;
        if (current >= 0) index = (current + (reverse ? -1 : 1) + targets.Count) % targets.Count;
        else if (!reverse)
        {
            // キャレットを含む対象か、その後ろの最初の対象（本家の Computelinediff と同じ判定）。
            index = targets.FindIndex(target => target.Start + target.Length >= selectionStart);
            if (index < 0) index = 0;
        }
        else
        {
            index = targets.FindLastIndex(target => target.Start <= selectionStart);
            if (index < 0) index = targets.Count - 1;
        }

        var (start, length) = targets[index];
        side.View.Focus();
        _activeLineBookmarkSide = sideIndex;
        side.View.Select(start, length);
        var line = side.View.Document.GetLineByOffset(start).LineNumber;
        ScrollDiffEditorsToDisplayRow(line - 1);
        _lineDifferenceSelection = (sideIndex, start, length, diff);
        _status.Text = UiText.Say($"行内差異 {index + 1} / {targets.Count}");
        return true;
    }

    /// <summary>その側の選択対象（文書上の位置と長さ）を文書の順に並べる。ゴースト行は選ばない。</summary>
    private List<(int Start, int Length)> LineDifferenceTargets(DiffEditorSide side)
    {
        var result = new List<(int Start, int Length)>();
        var document = side.View.Document;
        var ghosts = side.GhostLineNumbers();
        for (var number = 1; number <= document.LineCount && number <= _diffDisplay.RowCount; number++)
        {
            if (!_diffDisplay.Difference[number - 1] || ghosts.Contains(number)) continue;
            var line = document.GetLineByNumber(number);
            var spans = SpansForLine(side, number).Where(span => span.Length > 0 && span.Start >= 0 && span.Start + span.Length <= line.Length).ToArray();
            if (spans.Length == 0) result.Add((line.Offset, line.Length));
            else foreach (var span in spans.OrderBy(span => span.Start)) result.Add((line.Offset + span.Start, span.Length));
        }
        return result;
    }

    /// <summary>headless検証用: フォーカスのあるペインで選択中の文字列。</summary>
    internal string DiffEditorSelectedText(int side) => _diffViews[side].View.SelectedText;

    /// <summary>headless検証用: マージ結果で選択中の区間の ID（コンフリクトの移動先）。</summary>
    internal int? SelectedMergeSectionId => (_mergeSections.SelectedItem as SectionChoice)?.Id;
}
