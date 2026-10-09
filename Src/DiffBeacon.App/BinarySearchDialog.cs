using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal sealed record BinarySearchRequest(string Text, bool MatchCase, bool Backwards, bool Utf16, bool BigEndian, bool FromSelection = false);

internal sealed class BinarySearchDialog : Window
{
    internal const int MaximumTextLength = 32 * 1024;
    internal static Action<BinarySearchDialog>? Shown { get; set; }
    internal TextBox Text { get; } = new() { AcceptsReturn = true, Height = 120, MaxLength = MaximumTextLength };
    internal CheckBox MatchCase { get; } = new() { Content = "大文字と小文字を区別する（区別しない場合はASCII A–Zだけ）" };
    internal CheckBox Utf16 { get; } = new() { Content = "UTF-16LEの文字列（バイトコード・ANSI/OEM・数値のバイト順は使わない）" };
    internal CheckBox BigEndian { get; } = new() { Content = "数値バイトコードをbig-endianにする" };
    internal ComboBox Direction { get; } = new() { ItemsSource = new[] { "次を検索", "前を検索" } };
    internal Button Accept { get; } = new() { Content = "検索" };
    internal Button Cancel { get; } = new() { Content = "取消" };
    internal TextBlock Status { get; } = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<BinarySearchRequest, CancellationToken, Task<bool>> _search;
    private bool _closed, _busy;
    internal Task CurrentOperation { get; private set; } = Task.CompletedTask;

    private BinarySearchDialog(BinarySearchRequest initial, bool oem, Func<BinarySearchRequest, CancellationToken, Task<bool>> search)
    {
        _search = search;
        if (initial.FromSelection && initial.Text.Length == 0) Status.Text = "前回の検索bytesは入力欄の上限を超えています。新しい検索を入力できます。取消後の次／前の検索は前回のbytesを保持します。";
        Title = "バイナリ検索"; Width = 590; MaxHeight = 620; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Text.Text = initial.Text; MatchCase.IsChecked = initial.MatchCase; Utf16.IsChecked = initial.Utf16; BigEndian.IsChecked = initial.BigEndian; Direction.SelectedIndex = initial.Backwards ? 1 : 0;
        var content = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "検索する文字列／バイトコード（最大32 Ki文字、例: A<bh:00>）" }); content.Children.Add(Text);
        content.Children.Add(new TextBlock { Text = oem ? "この側の文字集合: OEM（Windowsの既定 / macOSは437）" : "この側の文字集合: ANSI（Windowsの既定 / macOSは1252）" });
        content.Children.Add(MatchCase); content.Children.Add(Utf16); content.Children.Add(BigEndian); content.Children.Add(Direction);
        content.Children.Add(new TextBlock { Text = "現在のカーソル開始位置を除いて検索します。末尾／先頭で折り返しません。", TextWrapping = Avalonia.Media.TextWrapping.Wrap }); content.Children.Add(Status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 }; buttons.Children.Add(Cancel); buttons.Children.Add(Accept); content.Children.Add(buttons);
        Content = new ScrollViewer { Content = content };
        Utf16.IsCheckedChanged += (_, _) => BigEndian.IsEnabled = !_busy && Utf16.IsChecked != true;
        BigEndian.IsEnabled = !initial.Utf16;
        Cancel.Click += (_, _) => Close();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); };
        Accept.Click += (_, _) => { if (!_closed && !_busy) CurrentOperation = SearchAsync(); };
    }
    private async Task SearchAsync()
    {
        _busy = true; Accept.IsEnabled = Text.IsEnabled = MatchCase.IsEnabled = Utf16.IsEnabled = Direction.IsEnabled = BigEndian.IsEnabled = false;
        Status.Text = "検索中… 取消で中断できます。";
        try
        {
            var request = new BinarySearchRequest(Text.Text ?? "", MatchCase.IsChecked == true, Direction.SelectedIndex == 1, Utf16.IsChecked == true, BigEndian.IsChecked == true);
            var adopted = await _search(request, _lifetime.Token);
            if (!_closed) { if (adopted) Close(); else Status.Text = "検索を取消したか、比較・編集・選択の状態が変わりました。選択を保持しています。"; }
        }
        catch (OperationCanceledException) { if (!_closed) Status.Text = "検索を取消しました。"; }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidDataException or InvalidOperationException or IOException)
        { if (!_closed) Status.Text = error.Message; }
        finally
        {
            _busy = false;
            if (!_closed) { Accept.IsEnabled = Text.IsEnabled = MatchCase.IsEnabled = Utf16.IsEnabled = Direction.IsEnabled = true; BigEndian.IsEnabled = Utf16.IsChecked != true; }
        }
    }
    internal static byte[] Decode(BinarySearchRequest request, bool oem)
    {
        if (request.Text.Length == 0 || request.Text.Length > MaximumTextLength) throw new FormatException("検索する文字列は1～32768文字で入力してください。");
        // UTF16はliteralだけ。孤立surrogateを置換して別のbytesとして検索しない。
        var pattern = request.Utf16 ? new UnicodeEncoding(false, false, true).GetBytes(request.Text)
            : BinaryTextEncoding.DecodeBytecode(BinaryTextEncoding.Encode(request.Text, false), request.BigEndian, oem);
        if (pattern.Length == 0) throw new FormatException("検索するバイトがありません。");
        return pattern;
    }
    internal static async Task ShowAsync(Window owner, BinarySearchRequest initial, bool oem,
        Func<BinarySearchRequest, CancellationToken, Task<bool>> search, Action<BinarySearchDialog?> track)
    {
        var dialog = new BinarySearchDialog(initial, oem, search); track(dialog);
        try { var shown = dialog.ShowDialog(owner); Shown?.Invoke(dialog); await shown; }
        finally
        {
            // close後にもAcceptの非同期処理が戻る。最後の利用者が戻ってから一度だけdisposeする。
            dialog._closed = true; dialog._lifetime.Cancel();
            await dialog.CurrentOperation; dialog._lifetime.Dispose(); track(null);
        }
    }
}
