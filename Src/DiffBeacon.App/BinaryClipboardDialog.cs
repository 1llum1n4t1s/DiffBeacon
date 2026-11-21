using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal enum BinaryClipboardCommand { Select, Copy, Cut, Paste, FastPaste }
internal sealed record BinaryClipboardRequest(int Start, int Count, byte[] Payload, bool Insert, long Repeat, long Skip);

internal sealed class BinaryClipboardDialog : Window
{
    internal static Action<BinaryClipboardDialog>? Shown { get; set; }
    internal TextBox Start { get; } = new();
    internal TextBox End { get; } = new();
    internal TextBox Count { get; } = new();
    internal CheckBox UseCount { get; } = new() { Content = "終了位置の代わりにバイト数を指定" };
    internal TextBox Text { get; } = new() { AcceptsReturn = true, Height = 140, MaxLength = BinaryBytecode.MaximumTextBytes };
    internal TextBox Repeat { get; } = new() { Text = "1" };
    internal TextBox Skip { get; } = new() { Text = "0" };
    internal CheckBox Insert { get; } = new() { Content = "挿入（選択範囲がある場合は常に置換）" };
    internal CheckBox AsText { get; } = new() { Content = "文字として貼り付け（バイトコードを解釈しない）" };
    internal CheckBox BigEndian { get; } = new() { Content = "ビッグエンディアン" };
    internal ComboBox CharacterSet { get; } = new() { ItemsSource = new[] { "ANSI（Windowsの既定 / macOSは1252）", "OEM（Windowsの既定 / macOSは437）" }, SelectedIndex = 0 };
    internal ComboBox Formats { get; } = new();
    internal TextBlock Status { get; } = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    internal Button Accept { get; } = new() { Content = "適用" };
    internal Button Cancel { get; } = new() { Content = "キャンセル" };
    internal static string Label(BinaryClipboardCommand command) => command switch { BinaryClipboardCommand.Select => "バイト選択", BinaryClipboardCommand.Copy => "コピー", BinaryClipboardCommand.Cut => "切り取り", BinaryClipboardCommand.Paste => "貼り付け", _ => "形式を選んで貼り付け" };
    private BinaryClipboardDialog(BinaryClipboardCommand command, int length, int start, int count, bool insert, IReadOnlyList<BinaryClipboardValue>? formats)
    {
        Title = Label(command); Width = 570; MaxHeight = 680; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 8 }; var paste = command is BinaryClipboardCommand.Paste or BinaryClipboardCommand.FastPaste;
        Start.Text = start.ToString(CultureInfo.InvariantCulture); End.Text = (start + Math.Max(1, count) - 1).ToString(CultureInfo.InvariantCulture); Count.Text = Math.Max(1, count).ToString(CultureInfo.InvariantCulture);
        if (!paste)
        {
            content.Children.Add(new TextBlock { Text = "開始位置（10進または0x16進）" }); content.Children.Add(Start);
            content.Children.Add(new TextBlock { Text = "終了位置（このバイトを含む）" }); content.Children.Add(End);
            content.Children.Add(UseCount); content.Children.Add(Count);
        }
        else
        {
            if (command == BinaryClipboardCommand.FastPaste)
            {
                Formats.ItemsSource = formats?.Select(value => value.Name).ToArray(); Formats.SelectedIndex = formats is { Count: > 0 } ? Math.Max(0, formats.ToList().FindIndex(value => value.Name == "CF_TEXT" || value.Kind == BinaryClipboardKind.PlatformText)) : -1;
                content.Children.Add(new TextBlock { Text = "クリップボード形式" }); content.Children.Add(Formats);
            }
            else
            {
                var initial = formats?.FirstOrDefault(value => value.Name == "CF_TEXT") ?? formats?.FirstOrDefault(value => value.Kind != BinaryClipboardKind.Raw);
                if (initial is not null)
                {
                    try { Text.Text = initial.PlainText(); }
                    catch (Exception exception) when (exception is InvalidDataException or System.Text.DecoderFallbackException)
                    { Status.Text = "初期文字列を読み込めません: " + exception.Message; }
                }
                content.Children.Add(new TextBlock { Text = "本文（例: A<bh:00>、41 00 は文字列）" }); content.Children.Add(Text);
            }
            content.Children.Add(AsText); content.Children.Add(CharacterSet); content.Children.Add(BigEndian);
            content.Children.Add(new TextBlock { Text = "繰り返し回数" }); content.Children.Add(Repeat);
            content.Children.Add(new TextBlock { Text = "繰り返し間で跨ぐ元バイト数（最後のskipも上書き容量に含む）" }); content.Children.Add(Skip);
            Insert.IsChecked = insert || count != 0; Insert.IsEnabled = count == 0; content.Children.Add(Insert);
        }
        content.Children.Add(Status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 }; buttons.Children.Add(Cancel); buttons.Children.Add(Accept); content.Children.Add(buttons);
        Content = new ScrollViewer { Content = content }; Cancel.Click += (_, _) => Close();
        Accept.Click += (_, _) =>
        {
            try
            {
                if (!paste)
                {
                    var offset = Offset(Start.Text); var amount = UseCount.IsChecked == true ? Offset(Count.Text) : checked(Offset(End.Text) - offset + 1);
                    if (offset < 0 || amount <= 0 || amount > length - offset) throw new FormatException("選択位置またはバイト数が範囲外です。");
                    Close(new BinaryClipboardRequest(offset, amount, [], false, 1, 0)); return;
                }
                if (!long.TryParse(Repeat.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var repeat) || !long.TryParse(Skip.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var skip) || repeat < 1 || repeat > BinaryEditSession.MaximumFileBytes || skip < 0 || skip > BinaryEditSession.MaximumFileBytes) throw new FormatException("回数は1以上、skipは0以上の範囲内整数で入力してください。");
                var oem = CharacterSet.SelectedIndex == 1;
                var payload = command == BinaryClipboardCommand.FastPaste ? Formats.SelectedIndex >= 0 && formats is not null ? formats[Formats.SelectedIndex].Decode(AsText.IsChecked == true, BigEndian.IsChecked == true, oem) : throw new FormatException("形式を選んでください。")
                    : DecodeText(Text.Text ?? "", AsText.IsChecked == true, BigEndian.IsChecked == true, oem);
                if (payload.Length == 0) throw new FormatException("貼り付けるバイトがありません。");
                _ = BinaryEditSession.ValidatePaste(length, start, count, payload.Length, Insert.IsChecked == true, repeat, skip);
                Close(new BinaryClipboardRequest(start, count, payload, Insert.IsChecked == true, repeat, skip));
            }
            catch (Exception error) when (error is FormatException or ArgumentException or OverflowException or InvalidDataException or InvalidOperationException) { Status.Text = error.Message; }
        };
    }
    internal static byte[] DecodeText(string text, bool asText, bool bigEndian, bool oem)
    {
        var bytes = BinaryTextEncoding.Encode(text, asText && oem);
        return asText ? bytes : BinaryTextEncoding.DecodeBytecode(bytes, bigEndian, oem);
    }
    private static int Offset(string? text) => int.TryParse(text?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true ? text[2..] : text,
        text?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new FormatException("位置は10進または0x16進整数で入力してください。");
    internal static Task<BinaryClipboardRequest?> ShowAsync(Window owner, BinaryClipboardCommand command, int length, int start, int count, bool insert, IReadOnlyList<BinaryClipboardValue>? formats = null)
    { var dialog = new BinaryClipboardDialog(command, length, start, count, insert, formats); var task = dialog.ShowDialog<BinaryClipboardRequest?>(owner); Shown?.Invoke(dialog); return task; }
}
