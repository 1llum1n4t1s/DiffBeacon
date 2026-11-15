using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMergeの「移動」ダイアログ（IDD_WMGOTO、WMGotoDlg.cpp）。番号・ファイル（左側／中央／右側）・移動種別（行／差異）を選ぶ。
/// - 番号の横に、選んだファイルと種別で受け付ける範囲「(1-N)」を出す。範囲の外・数でない番号では「移動」を押せない（本家 UpdateGoToButton）。
/// - 二者比較では中央を、差異が無ければ「差異」を選べない（本家 OnInitDialog）。
/// </summary>
internal sealed class GoToDialog : Window
{
    /// <summary>headless検証用: ダイアログを表示した直後に呼ぶ。</summary>
    internal static Action<GoToDialog>? Shown { get; set; }

    /// <summary>OK の結果。Side は 0=左、1=中央、2=右。Difference が false なら行。Number は1始まり。</summary>
    internal sealed record Choice(int Side, bool Difference, int Number);

    private readonly int[] _lastLines;
    private readonly int _lastDifference;
    internal TextBox Number { get; } = new() { Name = "GoToNumber", Width = 110 };
    internal TextBlock Range { get; } = new() { Name = "GoToRange", VerticalAlignment = VerticalAlignment.Center, MinWidth = 90 };
    internal RadioButton Left { get; } = new() { Content = "左側(_L)", GroupName = "GoToFile", Name = "GoToLeft" };
    internal RadioButton Middle { get; } = new() { Content = "中央(_M)", GroupName = "GoToFile", Name = "GoToMiddle" };
    internal RadioButton Right { get; } = new() { Content = "右側(_R)", GroupName = "GoToFile", Name = "GoToRight" };
    internal RadioButton Line { get; } = new() { Content = "行(_N)", GroupName = "GoToWhat", Name = "GoToLine" };
    internal RadioButton Difference { get; } = new() { Content = "差異(_D)", GroupName = "GoToWhat", Name = "GoToDifference" };
    internal Button GoButton { get; } = new() { Content = "移動(_G)", Name = "GoToOk", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    internal Button CancelButton { get; } = new() { Content = "キャンセル", Name = "GoToCancel", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Stretch };

    /// <param name="lastLines">各側（左・中央・右）の最後の行番号。表示していない側は -1。</param>
    /// <param name="lastDifference">差異の数。</param>
    internal GoToDialog(int[] lastLines, int lastDifference, int side, int line)
    {
        _lastLines = lastLines; _lastDifference = lastDifference;
        Title = "移動"; SizeToContent = SizeToContent.WidthAndHeight; CanResize = false; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Middle.IsEnabled = lastLines[1] > 0;
        Difference.IsEnabled = lastDifference > 0;
        (side switch { 1 when Middle.IsEnabled => Middle, 2 => Right, _ => Left }).IsChecked = true;
        Line.IsChecked = true;
        Number.Text = line.ToString(CultureInfo.InvariantCulture);

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.Add(new Label { Content = "番号(_O):", Target = Number, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0) });
        top.Children.Add(Range); top.Children.Add(Number);
        var files = Group("ファイル", Left, Middle, Right);
        var what = Group("移動種別", Line, Difference);
        var buttons = new StackPanel { Spacing = 6, Width = 100, VerticalAlignment = VerticalAlignment.Bottom };
        buttons.Children.Add(GoButton); buttons.Children.Add(CancelButton);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(files); row.Children.Add(what); row.Children.Add(buttons);
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(top); root.Children.Add(row);
        Content = root;

        foreach (var radio in new[] { Left, Middle, Right, Line, Difference }) radio.IsCheckedChanged += (_, _) => UpdateState();
        Number.TextChanged += (_, _) => UpdateState();
        GoButton.Click += (_, _) => { if (Result() is { } choice) Close(choice); };
        CancelButton.Click += (_, _) => Close(null);
        UpdateState();
    }

    private static Border Group(string title, params RadioButton[] items)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = title });
        foreach (var item in items) panel.Children.Add(item);
        return new Border { BorderBrush = WinMergeColors.FrameBorder, BorderThickness = new Thickness(1), Padding = new Thickness(6), Child = panel, MinWidth = 110 };
    }

    private int SelectedSide => Middle.IsChecked == true ? 1 : Right.IsChecked == true ? 2 : 0;

    /// <summary>選んだファイルと種別で受け付ける最大の番号（本家 GetRangeMax）。</summary>
    internal int RangeMax => Difference.IsChecked == true ? _lastDifference : _lastLines[SelectedSide];

    /// <summary>本家 UpdateRange と UpdateGoToButton。</summary>
    private void UpdateState()
    {
        var max = RangeMax;
        Range.Text = max > 0 ? $"(1-{max})" : "";
        GoButton.IsEnabled = Result() is not null;
    }

    /// <summary>入力が範囲内の番号なら、その結果。</summary>
    internal Choice? Result()
    {
        // 本家は std::stoi で先頭の数字を読む。ここでは数字だけの入力を受け付ける（前後の空白は除く）。
        var text = (Number.Text ?? "").Trim();
        if (text.Length == 0 || text.Any(c => c is < '0' or > '9') || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
        return number > 0 && number <= RangeMax ? new Choice(SelectedSide, Difference.IsChecked == true, number) : null;
    }

    internal Task<Choice?> ShowAsync(Window owner)
    {
        var result = ShowDialog<Choice?>(owner);
        Number.Focus(); Number.SelectAll();
        Shown?.Invoke(this);
        return result;
    }
}
