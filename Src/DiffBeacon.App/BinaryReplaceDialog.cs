using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal enum BinaryReplaceAction { FindNext, FindPrevious, Selection, Following, Previous }
internal sealed record BinaryReplaceRequest(string FindText, string ReplacementText, bool MatchCase,
    bool AsText, bool BigEndian, bool UseSavedPattern = false);
internal sealed record BinaryReplaceFeedback(bool Adopted, string Message);

internal sealed class BinaryReplaceDialog : Window
{
    internal static Action<BinaryReplaceDialog>? Shown { get; set; }
    internal TextBox FindText { get; } = new() { AcceptsReturn = true, Height = 80, MaxLength = BinarySearchDialog.MaximumTextLength };
    internal TextBox ReplacementText { get; } = new() { AcceptsReturn = true, Height = 80, MaxLength = BinaryBytecode.MaximumTextBytes };
    internal CheckBox SavedPattern { get; } = new();
    internal CheckBox MatchCase { get; } = new() { Content = "大文字と小文字を区別する（区別しない場合はASCII A–Zだけ）" };
    internal CheckBox AsText { get; } = new() { Content = "置換値を文字列のまま使う（ANSI、バイトコードは解釈しない）" };
    internal CheckBox BigEndian { get; } = new() { Content = "数値バイトコードをbig-endianにする" };
    internal Button FindNext { get; } = new() { Content = "次を検索" };
    internal Button FindPrevious { get; } = new() { Content = "前を検索" };
    internal Button ReplaceSelection { get; } = new() { Content = "選択範囲を置換" };
    internal Button ReplaceFollowing { get; } = new() { Content = "前方を全置換" };
    internal Button ReplacePrevious { get; } = new() { Content = "後方を全置換" };
    internal Button Cancel { get; } = new() { Content = "閉じる／取消" };
    internal TextBlock Status { get; } = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    internal Task CurrentOperation { get; private set; } = Task.CompletedTask;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<BinaryReplaceRequest, BinaryReplaceAction, CancellationToken, Task<BinaryReplaceFeedback>> _execute;
    private readonly Func<bool> _canEdit, _hasSelection;
    private readonly bool _hasSavedPattern;
    private bool _busy, _closed;

    private BinaryReplaceDialog(BinaryReplaceRequest initial, int savedPatternLength, bool oem,
        Func<BinaryReplaceRequest, BinaryReplaceAction, CancellationToken, Task<BinaryReplaceFeedback>> execute,
        Func<bool> canEdit, Func<bool> hasSelection)
    {
        _execute = execute; _canEdit = canEdit; _hasSelection = hasSelection; _hasSavedPattern = savedPatternLength > 0;
        Title = "バイナリ置換"; Width = 680; MaxHeight = 700; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FindText.Text = initial.FindText; ReplacementText.Text = initial.ReplacementText;
        SavedPattern.Content = $"選択／前回の検索bytesを使う（{savedPatternLength:N0} bytes）";
        SavedPattern.IsChecked = _hasSavedPattern && initial.UseSavedPattern;
        MatchCase.IsChecked = initial.MatchCase; AsText.IsChecked = initial.AsText; BigEndian.IsChecked = initial.BigEndian;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = "検索する文字列／バイトコード（最大32 Ki文字、例: A<bh:00>）" });
        body.Children.Add(SavedPattern); body.Children.Add(FindText);
        body.Children.Add(new TextBlock { Text = oem ? "検索とバイトコードの文字集合: OEM（Windowsの既定 / macOSは437）" : "検索とバイトコードの文字集合: ANSI（Windowsの既定 / macOSは1252）", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        body.Children.Add(MatchCase);
        var findButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        Add(findButtons, FindNext, BinaryReplaceAction.FindNext); Add(findButtons, FindPrevious, BinaryReplaceAction.FindPrevious);
        body.Children.Add(findButtons);
        body.Children.Add(new TextBlock { Text = "置換する文字列／バイトコード（空欄は削除、生成bytesは16 MiBまで）", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        body.Children.Add(ReplacementText); body.Children.Add(AsText); body.Children.Add(BigEndian);
        body.Children.Add(new TextBlock { Text = "全置換は現在の選択範囲を最初に置換し、その方向の先頭／末尾まで続行します。選択範囲は検索語との一致にかかわらず置換します。折り返しません。操作は一度のUndoで戻せます。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "前方の空置換は、削除位置の次のbyteから再検索します。隣接する一致が残る場合があります。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var replaceButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        Add(replaceButtons, ReplaceSelection, BinaryReplaceAction.Selection); Add(replaceButtons, ReplaceFollowing, BinaryReplaceAction.Following); Add(replaceButtons, ReplacePrevious, BinaryReplaceAction.Previous);
        body.Children.Add(replaceButtons);
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(Status); Cancel.HorizontalAlignment = HorizontalAlignment.Right; footer.Children.Add(Cancel);
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(new ScrollViewer { Content = body }); Content = root;
        FindText.TextChanged += (_, _) => RefreshState(); SavedPattern.IsCheckedChanged += (_, _) => RefreshState();
        Cancel.Click += (_, _) => Close(); Closed += (_, _) => { _closed = true; _lifetime.Cancel(); };
        RefreshState();
    }

    private void Add(Panel parent, Button button, BinaryReplaceAction action)
    {
        button.Margin = new Thickness(0, 0, 8, 4); parent.Children.Add(button);
        button.Click += (_, _) => { if (!_closed && !_busy) CurrentOperation = ExecuteAsync(action); };
    }
    internal void RefreshState()
    {
        if (_closed) return;
        var query = SavedPattern.IsChecked == true && _hasSavedPattern || !string.IsNullOrEmpty(FindText.Text);
        bool allowed, selected;
        try { allowed = _canEdit(); selected = _hasSelection(); }
        catch (ObjectDisposedException) { allowed = selected = false; }
        FindText.IsEnabled = !_busy && SavedPattern.IsChecked != true;
        SavedPattern.IsEnabled = !_busy && _hasSavedPattern;
        ReplacementText.IsEnabled = MatchCase.IsEnabled = AsText.IsEnabled = BigEndian.IsEnabled = !_busy;
        FindNext.IsEnabled = FindPrevious.IsEnabled = !_busy && query;
        ReplaceSelection.IsEnabled = !_busy && allowed && selected;
        ReplaceFollowing.IsEnabled = ReplacePrevious.IsEnabled = !_busy && allowed && query;
    }
    private async Task ExecuteAsync(BinaryReplaceAction action)
    {
        _busy = true; RefreshState(); Status.Text = "処理中… 閉じる／取消で中断できます。";
        try
        {
            var request = new BinaryReplaceRequest(FindText.Text ?? "", ReplacementText.Text ?? "", MatchCase.IsChecked == true,
                AsText.IsChecked == true, BigEndian.IsChecked == true, SavedPattern.IsChecked == true);
            var result = await _execute(request, action, _lifetime.Token);
            if (!_closed) Status.Text = result.Adopted ? result.Message : "比較・編集・選択の状態が変わったか、処理を取消しました。現在のbytesを保持しています。";
        }
        catch (OperationCanceledException) { if (!_closed) Status.Text = "処理を取消しました。現在のbytesを保持しています。"; }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidDataException or InvalidOperationException or IOException)
        { if (!_closed) Status.Text = error.Message; }
        finally { _busy = false; if (!_closed) RefreshState(); }
    }
    internal static byte[] DecodePattern(BinaryReplaceRequest request, bool oem, byte[]? savedPattern)
    {
        if (request.UseSavedPattern)
            return savedPattern is { Length: > 0 } ? savedPattern : throw new FormatException("保存した検索bytesがありません。");
        if (request.FindText.Length is 0 or > BinarySearchDialog.MaximumTextLength) throw new FormatException("検索する文字列は1～32768文字で入力してください。");
        var result = BinaryTextEncoding.DecodeBytecode(BinaryTextEncoding.Encode(request.FindText, false), request.BigEndian, oem);
        if (result.Length == 0) throw new FormatException("検索するバイトがありません。");
        return result;
    }
    internal static byte[] DecodeReplacement(BinaryReplaceRequest request, bool oem)
    {
        if (request.ReplacementText.Length > BinaryBytecode.MaximumTextBytes) throw new FormatException("置換入力の文字数上限を超えています。");
        var bytes = BinaryTextEncoding.Encode(request.ReplacementText, false);
        var result = request.AsText ? bytes : BinaryTextEncoding.DecodeBytecode(bytes, request.BigEndian, oem);
        if (result.Length > BinaryEditSession.MaximumFileBytes) throw new InvalidDataException("置換するbytesは16 MiBまでです。");
        return result;
    }
    internal static async Task ShowAsync(Window owner, BinaryReplaceRequest initial, int savedPatternLength, bool oem,
        Func<BinaryReplaceRequest, BinaryReplaceAction, CancellationToken, Task<BinaryReplaceFeedback>> execute,
        Func<bool> canEdit, Func<bool> hasSelection, Action<BinaryReplaceDialog?> track)
    {
        var dialog = new BinaryReplaceDialog(initial, savedPatternLength, oem, execute, canEdit, hasSelection);
        if (owner.Bounds.Height > 0) dialog.MaxHeight = Math.Max(300, Math.Min(700, owner.Bounds.Height - 40));
        track(dialog);
        try { var shown = dialog.ShowDialog(owner); Shown?.Invoke(dialog); await shown; }
        finally
        {
            dialog._closed = true; dialog._lifetime.Cancel();
            try { await dialog.CurrentOperation; }
            finally { dialog._lifetime.Dispose(); track(null); }
        }
    }
}
