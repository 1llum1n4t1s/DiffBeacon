using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal enum BinaryRangeKind { Insert, Overwrite, Delete }
internal sealed record BinaryRangeRequest(BinaryRangeKind Kind, long Start, long Count, string Hex);
internal sealed class BinaryRangeDialog : Window
{
    internal static Action<BinaryRangeDialog>? Shown { get; set; }
    internal TextBox Offset { get; } = new() { Text = "0" };
    internal TextBox Count { get; } = new() { Text = "1" };
    internal TextBox Hex { get; } = new() { AcceptsReturn = true, Height = 120, MaxLength = BinaryEditSession.MaximumFileBytes * 3 };
    internal TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap };
    internal Button Accept { get; } = new() { Content = "適用" };
    internal Button Cancel { get; } = new() { Content = "キャンセル" };
    internal static string Label(BinaryRangeKind kind) => kind switch { BinaryRangeKind.Insert => "バイト挿入", BinaryRangeKind.Overwrite => "バイト上書き", _ => "バイト削除" };
    private BinaryRangeDialog(BinaryRangeKind kind, int length)
    {
        Title = Label(kind); Width = 500; SizeToContent = SizeToContent.Height; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        content.Children.Add(new TextBlock { Text = $"開始位置（10進、0〜{length}）" }); content.Children.Add(Offset);
        content.Children.Add(new TextBlock { Text = kind == BinaryRangeKind.Delete ? "削除バイト数（10進）" : "16進値（2桁で1バイト、空白可）" });
        content.Children.Add(kind == BinaryRangeKind.Delete ? Count : Hex); content.Children.Add(Status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(Cancel); buttons.Children.Add(Accept); content.Children.Add(buttons); Content = content;
        Cancel.Click += (_, _) => Close();
        Accept.Click += (_, _) =>
        {
            try
            {
                if (!long.TryParse(Offset.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
                    || !long.TryParse(Count.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)) throw new FormatException("位置とバイト数を10進整数で入力してください。");
                var hex = Hex.Text ?? "";
                _ = BinaryEditSession.ValidateRange(length, start, count, hex, kind == BinaryRangeKind.Insert, kind == BinaryRangeKind.Delete);
                Close(new BinaryRangeRequest(kind, start, count, hex));
            }
            catch (Exception error) when (error is FormatException or ArgumentException or InvalidDataException) { Status.Text = error is ArgumentOutOfRangeException ? "開始位置または削除バイト数が範囲外です。" : error.Message; }
        };
    }
    internal static Task<BinaryRangeRequest?> ShowAsync(Window owner, BinaryRangeKind kind, int length)
    {
        var dialog = new BinaryRangeDialog(kind, length); var result = dialog.ShowDialog<BinaryRangeRequest?>(owner); Shown?.Invoke(dialog); return result;
    }
}
