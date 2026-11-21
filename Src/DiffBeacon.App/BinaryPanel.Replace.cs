using Avalonia.Controls;
using Avalonia.Input;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public static partial class SpecializedViews
{
    public sealed partial class BinaryPanel
    {
        private readonly BinaryReplaceRequest?[] _replacementRequests = new BinaryReplaceRequest?[3];
        private readonly byte[]?[] _replacementPatterns = new byte[]?[3];
        private readonly List<(Button Button, int Side)> _replacementButtons = [];
        private readonly HashSet<Button> _busyReplacementButtons = [];
        private BinaryReplaceDialog? _replacementDialog;
        private CancellationTokenSource? _replacementCancellation;
        private long _replacementGeneration;
        internal Task? CurrentReplacementDialogOperation { get; private set; }
        internal Task? CurrentReplacementOperation { get; private set; }
        internal Action? ReplacementReadyForAdoption { get; set; }
        internal Action<int, BinaryReplaceAction, BinaryReplacementResult?>? ReplacementSnapshotObserved { get; set; }
        internal Exception? ReplacementObservedFailure { get; private set; }

        private void AddReplaceButton(Panel actions, int side)
        {
            var name = side switch { 0 => "左", 1 => "中央", _ => "右" };
            var button = new Button { Content = name + " 置換", Name = "binary-replace-" + side, Margin = new Avalonia.Thickness(4) };
            _replacementButtons.Add((button, side)); actions.Children.Add(button);
            button.KeyDown += (_, args) =>
            {
                var command = args.KeyModifiers.HasFlag(KeyModifiers.Control) || OperatingSystem.IsMacOS() && args.KeyModifiers.HasFlag(KeyModifiers.Meta);
                if (command && args.Key == Key.H) { args.Handled = true; _ = RunAsync(() => CurrentReplacementDialogOperation = ReplaceDialogAsync(side)); }
            };
            button.Click += async (_, _) =>
            {
                if (_disposed || !_busyReplacementButtons.Add(button)) return;
                try { await RunAsync(() => CurrentReplacementDialogOperation = ReplaceDialogAsync(side)); }
                finally { _busyReplacementButtons.Remove(button); if (!_disposed) UpdateReplaceButtons(); }
            };
            UpdateReplaceButtons();
        }
        private bool ReplacementButtonAllowed(int side)
            => !_disposed && !ProjectSides.Any(Pending) && Session.Length(LocalSide(side)) != 0
                && !ReadOnly(side) && !Session.IsReadOnly(LocalSide(side)) && EditingCurrent?.Invoke() != false;
        private void UpdateReplaceButtons()
        {
            foreach (var (button, side) in _replacementButtons)
                button.IsEnabled = ReplacementButtonAllowed(side) && !_busyReplacementButtons.Contains(button);
            _replacementDialog?.RefreshState();
        }
        internal async Task ReplaceDialogAsync(int side)
        {
            WritableSelection(side);
            if (Session.Length(LocalSide(side)) == 0) throw new InvalidOperationException("空のバイナリには置換するbytesがありません。");
            if (_replacementDialog is not null) return;
            CancelSearch(true); CancelReplacement(false);
            var selection = Selection(side);
            var initial = _replacementRequests[side] ?? new BinaryReplaceRequest("", "", false, false, false);
            var saved = _replacementPatterns[side];
            if (selection.Selected)
            {
                saved = Session.Page(LocalSide(side), selection.Start, selection.Count);
                initial = initial with { FindText = DialogBytecode(saved, true), UseSavedPattern = true };
            }
            else if (saved is not { Length: > 0 }) initial = initial with { UseSavedPattern = false };
            var owner = TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException("置換ダイアログの親ウィンドウがありません。");
            await BinaryReplaceDialog.ShowAsync(owner, initial, saved?.Length ?? 0, selection.Oem,
                (request, action, token) =>
                {
                    var operation = ExecuteReplacementAsync(side, request, action, saved, token);
                    CurrentReplacementOperation = operation; return operation;
                },
                () => ReplacementButtonAllowed(side), () => !_disposed && Selection(side).Selected,
                dialog => _replacementDialog = dialog);
        }
        internal async Task<BinaryReplaceFeedback> ExecuteReplacementAsync(int side, BinaryReplaceRequest request,
            BinaryReplaceAction action, byte[]? savedPattern, CancellationToken dialogToken = default)
        {
            if (action is not (BinaryReplaceAction.FindNext or BinaryReplaceAction.FindPrevious or BinaryReplaceAction.Selection or BinaryReplaceAction.Following or BinaryReplaceAction.Previous))
                throw new ArgumentOutOfRangeException(nameof(action));
            var finding = action is BinaryReplaceAction.FindNext or BinaryReplaceAction.FindPrevious;
            if (finding) RequireSearch(side); else WritableSelection(side);
            if (request.FindText.Length > BinarySearchDialog.MaximumTextLength) throw new FormatException("検索入力の文字数上限を超えています。");
            var oem = Selection(side).Oem;
            var pattern = action == BinaryReplaceAction.Selection ? Array.Empty<byte>() : BinaryReplaceDialog.DecodePattern(request, oem, savedPattern);
            var replacement = finding ? Array.Empty<byte>() : BinaryReplaceDialog.DecodeReplacement(request, oem);
            CancelReplacement(false); CancelSearch(false); ReplacementObservedFailure = null;
            var generation = _replacementGeneration;
            var ownerCurrent = CaptureSearchContext?.Invoke();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(dialogToken);
            _replacementCancellation = cancellation;
            var session = Session; var stamp = StateStamp; var page = _pageStart;
            var revisions = ProjectSides.Select(value => session.Revision(LocalSide(value))).ToArray();
            var readOnly = ProjectSides.Select(value => (ReadOnly(value), session.IsReadOnly(LocalSide(value)))).ToArray();
            var state = Selection(side); var start = state.Start; var count = state.Count; var caret = state.Caret;
            var snapshot = CaptureApplied(side).CopyBytes();
            bool Current() => !cancellation.IsCancellationRequested && !_disposed && generation == _replacementGeneration
                && ReferenceEquals(session, Session) && StateStamp == stamp && _pageStart == page
                && ProjectSides.Select(value => session.Revision(LocalSide(value))).SequenceEqual(revisions)
                && ProjectSides.Select(value => (ReadOnly(value), session.IsReadOnly(LocalSide(value)))).SequenceEqual(readOnly)
                && EditingCurrent?.Invoke() != false;
            try
            {
                ownerCurrent?.Invoke(); cancellation.Token.ThrowIfCancellationRequested();
                var found = -1; BinaryReplacementResult? result = null;
                if (finding)
                {
                    var backwards = action == BinaryReplaceAction.FindPrevious;
                    var searchCaret = count == 0 ? caret : backwards ? start : checked(start + count - 1);
                    found = await Task.Run(() => BinaryByteSearch.Find(snapshot, pattern, searchCaret,
                        backwards, request.MatchCase, cancellation.Token), cancellation.Token);
                }
                else
                {
                    var mode = action switch { BinaryReplaceAction.Selection => BinaryReplacementMode.Selection,
                        BinaryReplaceAction.Following => BinaryReplacementMode.Forward, _ => BinaryReplacementMode.Backward };
                    result = await Task.Run(() => BinaryByteReplacement.Transform(snapshot, pattern, replacement,
                        caret, start, count, mode, request.MatchCase, cancellation.Token), cancellation.Token);
                }
                try { ReplacementSnapshotObserved?.Invoke(side, action, result); ReplacementReadyForAdoption?.Invoke(); }
                catch (Exception error) { ReplacementObservedFailure = error; cancellation.Cancel(); return new(false, ""); }
                if (!Current()) return new(false, "");
                ownerCurrent?.Invoke(); EnsureApplied(); cancellation.Token.ThrowIfCancellationRequested();
                string message;
                if (finding)
                {
                    if (found >= 0) { SelectBytes(side, checked(found + pattern.Length - 1), found); message = $"検索結果: 0x{found:X8} · {pattern.Length:N0} bytes"; }
                    else message = "一致するバイトがありません。先頭／末尾で折り返しません。";
                }
                else if (result!.ReplacementCount == 0) message = "置換する一致がありません。現在のbytesと選択を保持しています。";
                else
                {
                    // workerは不変snapshotだけを扱う。候補と共有履歴の検査・公開はUIへ戻ってから行う。
                    var local = LocalSide(side); var before = session.Revision(local);
                    var candidate = session.PrepareReplaceRange(local, 0, session.Length(local), result.Bytes.Span);
                    cancellation.Token.ThrowIfCancellationRequested(); ownerCurrent?.Invoke();
                    if (!Current()) return new(false, "");
                    CommitBytes(side, candidate, result.Caret);
                    if (result.SelectionLength != 0) SelectBytes(side, checked(result.SelectionStart + result.SelectionLength - 1), result.SelectionStart);
                    message = session.Revision(local) == before
                        ? $"{result.ReplacementCount:N0}箇所を処理しました。内容は同じため履歴は追加していません。"
                        : $"{result.ReplacementCount:N0}箇所を置換しました。最終位置: 0x{result.Caret:X8}。一度のUndoで戻せます。";
                }
                _replacementRequests[side] = request;
                _replacementPatterns[side] = request.UseSavedPattern ? savedPattern : null;
                SetStatus(message); UpdateReplaceButtons(); return new(true, message);
            }
            catch (OperationCanceledException) { return new(false, ""); }
            finally
            {
                if (ReferenceEquals(_replacementCancellation, cancellation)) _replacementCancellation = null;
                cancellation.Dispose();
            }
        }
        private void CancelReplacement(bool closeDialog)
        {
            _replacementGeneration++; _replacementCancellation?.Cancel();
            if (closeDialog) _replacementDialog?.Close();
            if (_disposed) { Array.Clear(_replacementRequests); Array.Clear(_replacementPatterns); }
        }
    }
}
