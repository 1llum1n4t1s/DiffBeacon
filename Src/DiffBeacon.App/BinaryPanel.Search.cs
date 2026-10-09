using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public static partial class SpecializedViews
{
    public sealed partial class BinaryPanel
    {
        private readonly BinarySearchRequest?[] _searchRequests = new BinarySearchRequest?[3];
        private readonly List<(Button Button, int Side, bool RequiresPattern)> _searchButtons = [];
        private readonly HashSet<Button> _busySearchButtons = [];
        private readonly byte[]?[] _selectedSearchPatterns = new byte[]?[3];
        private CancellationTokenSource? _searchCancellation;
        private BinarySearchDialog? _searchDialog;
        private long _searchGeneration;
        internal Task? CurrentSearchOperation { get; private set; }
        // ownerの比較世代は各検索開始時に捕捉する。workerにはvalidatorを渡さない。
        internal Func<Action>? CaptureSearchContext { get; set; }

        private void AddSearchButtons(Panel actions, int side)
        {
            var name = side switch { 0 => "左", 1 => "中央", _ => "右" };
            AddSearchButton(actions, side, name + " 検索", false, () => SearchDialogAsync(side));
            AddSearchButton(actions, side, name + " 次を検索", true, () => SearchNextAsync(side, false));
            AddSearchButton(actions, side, name + " 前を検索", true, () => SearchNextAsync(side, true));
        }
        private void AddSearchButton(Panel actions, int side, string title, bool requiresPattern, Func<Task> action)
        {
            var button = new Button { Content = title, Margin = new Avalonia.Thickness(4), IsEnabled = SearchButtonAllowed(side, requiresPattern) };
            _searchButtons.Add((button, side, requiresPattern));
            // 実buttonへfocusが移った後も、この側の検索キーを受け付ける。
            // editorの編集／clipboardキーをbuttonからdispatchしない。
            button.AddHandler(InputElement.KeyDownEvent, (_, args) =>
            {
                if (args.Handled || _disposed) return;
                if (args.Key == Key.F && (args.KeyModifiers.HasFlag(KeyModifiers.Control) || args.KeyModifiers.HasFlag(KeyModifiers.Meta)))
                { args.Handled = true; _ = RunAsync(() => SearchDialogAsync(side)); }
                else if (args.Key == Key.F3)
                { args.Handled = true; _ = RunAsync(() => SearchNextAsync(side, args.KeyModifiers.HasFlag(KeyModifiers.Shift))); }
            }, RoutingStrategies.Tunnel);

            button.Click += async (_, _) =>
            {
                if (_disposed || !_busySearchButtons.Add(button)) return;
                try { await RunAsync(action); }
                catch (Exception error)
                {
                    if (TopLevel.GetTopLevel(button) is Window owner)
                    {
                        var dialog = new Window { Title = "操作エラー", Width = 500, Height = 180,
                            Content = new TextBlock { Text = error.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(20) } };
                        await dialog.ShowDialog(owner);
                    }
                }
                finally { _busySearchButtons.Remove(button); if (!_disposed) UpdateSearchButtons(); }
            };
            actions.Children.Add(button);
        }
        private void UpdateSearchButtons()
        {
            foreach (var (button, side, requiresPattern) in _searchButtons)
                button.IsEnabled = SearchButtonAllowed(side, requiresPattern);
        }
        private bool HasValidSearchSelection(int side)
        {
            var length = Session.Length(LocalSide(side)); var selection = Selection(side);
            return selection.Selected && selection.Anchor >= 0 && selection.Caret >= 0
                && selection.Anchor < length && selection.Caret < length;
        }
        private bool HasSearchPattern(int side)
        {
            if (HasValidSearchSelection(side)) return true;
            if (_searchRequests[side] is not { } request) return false;
            // 前回語は復号と採用guardに成功したrequestだけ。選択由来は空Textでもraw cacheを保つ。
            return request.FromSelection
                ? _selectedSearchPatterns[side] is { Length: > 0 } bytes && bytes.Length <= BinaryEditSession.MaximumFileBytes
                : request.Text.Length is > 0 and <= BinarySearchDialog.MaximumTextLength;
        }
        private bool SearchButtonAllowed(int side, bool requiresPattern)
            => !_disposed && Session.Length(LocalSide(side)) != 0 && (!requiresPattern || HasSearchPattern(side));
        private void RequireSearch(int side)
        {
            ThrowDisposed(); _ = LocalSide(side); EnsureApplied();
            if (EditingCurrent?.Invoke() == false) throw new OperationCanceledException("比較元が変更されました。");
            if (Session.Length(LocalSide(side)) == 0) throw new InvalidOperationException("空のバイナリは検索できません。");
        }
        private BinarySearchRequest SelectedRequest(int side, string text = "")
            => new(text, _searchRequests[side]?.MatchCase ?? false, false, false,
                _searchRequests[side]?.BigEndian ?? false, true);
        private static string DialogBytecode(ReadOnlySpan<byte> bytes, bool omitOversized = false)
        {
            if (bytes.Length > BinarySearchDialog.MaximumTextLength) return omitOversized ? "" : throw DialogSelectionTooLong();
            // 固定原版Findの入力予算には終端NULを含む。Encode前に同じ1/2/7長を数える。
            var size = 1;
            foreach (var value in bytes)
            {
                size += value is 60 or 92 ? 2 : value is >= 32 and < 127 or 10 or 13 ? 1 : 7;
                if (size > BinarySearchDialog.MaximumTextLength) return omitOversized ? "" : throw DialogSelectionTooLong();
            }
            return BinaryBytecode.Encode(bytes);
        }
        private static InvalidOperationException DialogSelectionTooLong()
            => new("選択のバイトコードと終端の合計が32 KiBを超えています。選択を保持しました。次／前の検索は全選択bytesを使えます。");
        private BinarySearchRequest SelectedDialogSearch(int side)
        {
            var selection = Selection(side);
            // 最小でも1byte=1文字。巨大な全snapshot/文字列を作る前に拒否する。
            if (selection.Count > BinarySearchDialog.MaximumTextLength) throw DialogSelectionTooLong();
            return SelectedRequest(side, DialogBytecode(Session.Page(LocalSide(side), selection.Start, selection.Count)));
        }
        internal async Task SearchDialogAsync(int side)
        {
            RequireSearch(side);
            // 同じmodalへのCtrl+F二重送信を抑止する。
            if (_searchDialog is not null) return;
            CancelSearch(false);
            var openingCurrent = CaptureSearchContext?.Invoke(); openingCurrent?.Invoke();
            var previous = _searchRequests[side];
            var initial = Selection(side).Selected ? SelectedDialogSearch(side)
                : previous is { FromSelection: true } && _selectedSearchPatterns[side] is { } previousPattern
                    ? previous with { Text = DialogBytecode(previousPattern, true) }
                    : previous ?? new BinarySearchRequest("", false, false, false, false);
            var owner = TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException("検索ダイアログの親ウィンドウがありません。");
            var stamp = StateStamp; var readOnly = ProjectSides.Select(value => (ReadOnly(value), Session.IsReadOnly(LocalSide(value)))).ToArray(); var revisions = ProjectSides.Select(value => Session.Revision(LocalSide(value))).ToArray();
            bool Current() => !_disposed && StateStamp == stamp && ProjectSides.Select(value => (ReadOnly(value), Session.IsReadOnly(LocalSide(value)))).SequenceEqual(readOnly)
                && ProjectSides.Select(value => Session.Revision(LocalSide(value))).SequenceEqual(revisions) && EditingCurrent?.Invoke() != false;
            BinarySearchDialog? tracked = null;
            await BinarySearchDialog.ShowAsync(owner, initial, Selection(side).Oem,
                async (request, token) =>
                {
                    openingCurrent?.Invoke();
                    if (!Current()) throw new OperationCanceledException("検索を開いた後に比較・編集・選択の状態が変わりました。");
                    var pattern = BinarySearchDialog.Decode(request, Selection(side).Oem);
                    var operation = FindSnapshotAsync(side, request, pattern, token);
                    CurrentSearchOperation = operation; return await operation;
                }, dialog =>
                {
                    if (dialog is not null) { tracked = dialog; _searchDialog = dialog; }
                    else if (ReferenceEquals(_searchDialog, tracked)) _searchDialog = null;
                });
        }
        internal Task SearchNextAsync(int side, bool backwards)
        {
            RequireSearch(side);
            if (!HasSearchPattern(side)) throw new InvalidOperationException("検索する文字列を入力するか、バイト範囲を選択してください。");
            CancelSearch(true);
            var selection = Selection(side); var selected = HasValidSearchSelection(side);
            var request = selected ? SelectedRequest(side) : _searchRequests[side];
            if (request is null) throw new InvalidOperationException("前回の検索語がありません。");
            request = request with { Backwards = backwards };
            var bytes = CaptureApplied(side).CopyBytes();
            // EncodeのASCIIliteral/escape/数値byte tokenは元bytesへ戻る。
            // 選択済みbytesにはANSI/OEM/endian再変換が不要。Nextの全16MiB対応を保つ。
            var pattern = selected ? bytes.AsSpan(selection.Start, selection.Count).ToArray()
                : request.FromSelection ? _selectedSearchPatterns[side]
                    ?? throw new InvalidOperationException("前回の検索bytesがありません。検索する範囲を選択してください。")
                : BinarySearchDialog.Decode(request, selection.Oem);
            return CurrentSearchOperation = FindSnapshotAsync(side, request, pattern, default, bytes);
        }
        // 観測はheadless側だけが注入する。通常経路ではbuffer clone／ログを追加しない。
        internal Action<int, BinarySearchRequest, ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, int, int>? SearchSnapshotObserved { get; set; }
        internal Action? SearchReadyForAdoption { get; set; }
        private Exception? _searchObservedFailure;
        internal Exception? SearchObservedFailure => _searchObservedFailure;
        internal long SearchObservedStateVersion => StateVersion;
        internal int SearchObservedPageStart => _pageStart;
        internal BinarySearchRequest? SearchObservedPrevious(int side) => _searchRequests[side];
        internal byte[]? SearchObservedSelectedPattern(int side)
        {
            var bytes = _selectedSearchPatterns[side];
            if (bytes is null) return null;
            if (bytes.Length > 16 * 1024 * 1024) throw new InvalidOperationException("Search cache exceeds 16 MiB.");
            return (byte[])bytes.Clone();
        }
        private async Task<bool> FindSnapshotAsync(int side, BinarySearchRequest request, byte[] pattern,
            CancellationToken dialogToken, byte[]? captured = null)
        {
            RequireSearch(side); CancelSearch(false); _searchObservedFailure = null;
            var generation = _searchGeneration;
            var ownerCurrent = CaptureSearchContext?.Invoke();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(dialogToken);
            _searchCancellation = cancellation;
            var session = Session; var stamp = StateStamp; var page = _pageStart;
            var revisions = ProjectSides.Select(value => session.Revision(LocalSide(value))).ToArray();
            var readOnly = ProjectSides.Select(value => (ReadOnly(value), Session.IsReadOnly(LocalSide(value)))).ToArray();
            var snapshot = captured ?? CaptureApplied(side).CopyBytes(); var caret = Selection(side).Caret;
            try
            {
                ownerCurrent?.Invoke();
                // sessionとowner validatorはworkerに渡さない。不変コピーだけを16MiBまで検索する。
                var found = await Task.Run(() => BinaryByteSearch.Find(snapshot, pattern, caret,
                    request.Backwards, request.MatchCase, cancellation.Token), cancellation.Token);
                try
                {
                    SearchSnapshotObserved?.Invoke(side, request, snapshot, pattern, caret, found);
                    SearchReadyForAdoption?.Invoke();
                }
                catch (Exception error) { _searchObservedFailure = error; cancellation.Cancel(); return false; }
                if (cancellation.IsCancellationRequested || _disposed || generation != _searchGeneration
                    || !ReferenceEquals(session, Session) || StateStamp != stamp || _pageStart != page
                    || ProjectSides.Select(value => session.Revision(LocalSide(value))).SequenceEqual(revisions) == false
                    || ProjectSides.Select(value => (ReadOnly(value), Session.IsReadOnly(LocalSide(value)))).SequenceEqual(readOnly) == false || EditingCurrent?.Invoke() == false)
                    return false;
                ownerCurrent?.Invoke(); EnsureApplied();
                _searchRequests[side] = request;
                _selectedSearchPatterns[side] = request.FromSelection ? pattern : null;
                UpdateSearchButtons();
                if (found >= 0)
                {
                    SelectBytes(side, checked(found + pattern.Length - 1), found);
                    // Renderの確定表示を採用した後に結果statusを置く。
                    _status.Text = $"検索結果: 0x{found:X8} · {pattern.Length:N0} bytes";
                }
                else _status.Text = "一致するバイトがありません。先頭／末尾で折り返しません。";
                return true;
            }
            catch (OperationCanceledException) { return false; }
            finally
            {
                if (ReferenceEquals(_searchCancellation, cancellation)) _searchCancellation = null;
                // Cancelは所有権を移さず、各operationが最後に一度だけdisposeする。
                cancellation.Dispose();
            }
        }
        private void CancelSearch(bool closeDialog)
        {
            _searchGeneration++; _searchCancellation?.Cancel();
            if (closeDialog) _searchDialog?.Close();
            if (_disposed) { Array.Clear(_searchRequests); Array.Clear(_selectedSearchPatterns); }
        }
    }
}
