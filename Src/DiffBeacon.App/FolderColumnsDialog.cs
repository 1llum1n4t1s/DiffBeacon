using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMergeの「列の表示」ダイアログ（Merge.rc の IDD_DIRCOLS、DirColsDlg.cpp）。
/// 列の一覧（チェックで表示）、上へ(&amp;U)／下へ(&amp;D)、選んだ列の説明、デフォルト、OK／キャンセルを置く。
/// - OK では、チェックした列を一覧の並び順で表示する（本家の SanitizeOrder と同じく、チェックの無い列は表示しない列として後ろへ回る）。
/// - チェックが一つも無いと OK は押せない。先頭の項目では「上へ」、末尾の項目では「下へ」を押せない。
/// - デフォルトは既定の列と順序に戻し、OK したときは列幅も既定に戻す（本家の m_bReset／ResetColumnWidths）。
/// 本家の「追加プロパティ」（Windows のシェルのプロパティを列にする機能）は対応する機能が無いため置かない。
/// 本家は複数の項目を選んでまとめて移動できるが、ここでは一項目ずつ移動する。
/// 一覧は ListBox ではなく行の並びを自前で持つ（項目のチェックボックスを並べ替えのたびに作り直さず、親の付け替えも起こさないため）。
/// </summary>
internal sealed class FolderColumnsDialog : Window
{
    /// <summary>headless検証用: ダイアログを表示した直後に呼ぶ（Dialogs.ConfirmationShown と同じ考え方）。</summary>
    internal static Action<FolderColumnsDialog>? Shown { get; set; }

    internal sealed record Choice(FolderColumn[] Columns, bool Reset);

    private sealed record Row(FolderColumn Column, Border Host, CheckBox Box);

    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromRgb(204, 232, 255));
    private readonly FolderColumnInfo[] _catalog;
    private readonly FolderColumn[] _defaults;
    private readonly StackPanel _list = new() { Name = "FolderColumnsList" };
    private readonly Button _up = new() { Content = "上へ(_U)", Name = "FolderColumnsUp", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _down = new() { Content = "下へ(_D)", Name = "FolderColumnsDown", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _description = new() { Name = "FolderColumnsDescription", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };
    private readonly Button _defaultsButton = new() { Content = "デフォルト", Name = "FolderColumnsDefaults", MinWidth = 90 };
    private readonly Button _ok = new() { Content = "OK", Name = "FolderColumnsOk", MinWidth = 70, IsDefault = true };
    private readonly Button _cancel = new() { Content = "キャンセル", Name = "FolderColumnsCancel", MinWidth = 70, IsCancel = true };
    private readonly Dictionary<FolderColumn, Row> _rows = [];
    private readonly List<FolderColumn> _order = [];
    private int _selected = -1;
    private bool _reset;

    internal FolderColumnsDialog(FolderColumnInfo[] catalog, IReadOnlyList<FolderColumn> visible, FolderColumn[] defaults)
    {
        _catalog = catalog; _defaults = defaults;
        Title = "列の表示"; Width = 560; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        foreach (var info in catalog) _rows[info.Column] = CreateRow(info);
        Load(visible.ToHashSet(), visible);

        var buttons = new StackPanel { Spacing = 6, Width = 200 };
        buttons.Children.Add(_up); buttons.Children.Add(_down);
        buttons.Children.Add(new Border
        {
            Margin = new Thickness(0, 24, 0, 0), BorderBrush = WinMergeColors.FrameBorder, BorderThickness = new Thickness(1),
            MinHeight = 80, Child = _description,
        });
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 12, 12, 0) };
        var listHost = new Border
        {
            BorderBrush = WinMergeColors.FrameBorder, BorderThickness = new Thickness(1), Background = Brushes.White, Margin = new Thickness(0, 0, 12, 0),
            Child = new ScrollViewer { Content = _list, Height = 300 },
        };
        body.Children.Add(listHost); Grid.SetColumn(buttons, 1); body.Children.Add(buttons);

        var footer = new DockPanel { Margin = new Thickness(12), LastChildFill = false };
        DockPanel.SetDock(_defaultsButton, Dock.Left); footer.Children.Add(_defaultsButton);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(_ok); right.Children.Add(_cancel);
        DockPanel.SetDock(right, Dock.Right); footer.Children.Add(right);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(body);
        Content = root;

        _up.Click += (_, _) => MoveSelected(up: true);
        _down.Click += (_, _) => MoveSelected(up: false);
        _defaultsButton.Click += (_, _) => ResetToDefaults();
        _ok.Click += (_, _) => { if (CanAccept) Close(Result()); };
        _cancel.Click += (_, _) => Close(null);
        Select(0);
    }

    private Row CreateRow(FolderColumnInfo info)
    {
        var box = new CheckBox { Content = info.Title, Name = "FolderColumnsItem-" + info.Column, Margin = new Thickness(4, 0), MinHeight = 0, Padding = new Thickness(4, 0) };
        var host = new Border { Child = box, Padding = new Thickness(2, 1), Background = Brushes.Transparent };
        var row = new Row(info.Column, host, box);
        box.IsCheckedChanged += (_, _) => UpdateState();
        // 行のどこを押してもその列を選ぶ（本家の一覧と同じく、チェックの付け外しとは別に選択がある）。
        host.AddHandler(PointerPressedEvent, (_, _) => Select(_order.IndexOf(info.Column)), handledEventsToo: true);
        return row;
    }

    /// <summary>表示中の列を表示順に、その後ろへ表示していない列を本家の論理順で並べる。</summary>
    private void Load(HashSet<FolderColumn> checkedColumns, IReadOnlyList<FolderColumn> first)
    {
        _order.Clear(); _order.AddRange(first);
        _order.AddRange(_catalog.Select(info => info.Column).Where(column => !first.Contains(column)));
        foreach (var (column, row) in _rows) row.Box.IsChecked = checkedColumns.Contains(column);
        Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        foreach (var column in _order) _list.Children.Add(_rows[column].Host);
    }

    // headless検証用の読み取りと操作。実際のチェックボックス・ボタンを通す。
    internal IReadOnlyList<(FolderColumn Column, bool Checked)> Items => _order.Select(column => (column, _rows[column].Box.IsChecked == true)).ToArray();
    internal string DescriptionText => _description.Text ?? "";
    internal FolderColumn? SelectedColumn => _selected >= 0 ? _order[_selected] : null;
    internal Button OkButton => _ok;
    internal Button CancelButton => _cancel;
    internal Button UpButton => _up;
    internal Button DownButton => _down;
    internal Button DefaultsButton => _defaultsButton;
    internal CheckBox ItemBox(FolderColumn column) => _rows[column].Box;
    internal void Select(FolderColumn column) => Select(_order.IndexOf(column));

    private bool CanAccept => _order.Any(column => _rows[column].Box.IsChecked == true);

    private void Select(int index)
    {
        _selected = index >= 0 && index < _order.Count ? index : -1;
        UpdateState();
    }

    /// <summary>選んだ項目を一つ上（下）へ移す。チェックと選択はそのまま保つ。</summary>
    private void MoveSelected(bool up)
    {
        var target = up ? _selected - 1 : _selected + 1;
        if (_selected < 0 || target < 0 || target >= _order.Count) return;
        (_order[_selected], _order[target]) = (_order[target], _order[_selected]);
        Rebuild();
        Select(target);
    }

    /// <summary>既定の列（チェックあり、既定の順）と、その他の列（チェックなし、論理順）に戻す。</summary>
    private void ResetToDefaults()
    {
        _reset = true;
        Load(_defaults.ToHashSet(), _defaults);
        Select(0);
    }

    /// <summary>本家 OnLvnItemchangedColdlgList と同じく、説明・上へ／下へ・OK の状態を選択とチェックに合わせる。</summary>
    private void UpdateState()
    {
        for (var i = 0; i < _order.Count; i++) _rows[_order[i]].Host.Background = i == _selected ? SelectedBrush : Brushes.Transparent;
        if (_selected >= 0)
        {
            _description.Text = _catalog.First(info => info.Column == _order[_selected]).Description;
            _up.IsEnabled = _selected != 0;
            _down.IsEnabled = _selected != _order.Count - 1;
        }
        else
        {
            _description.Text = "";
            _up.IsEnabled = false; _down.IsEnabled = false;
        }
        _ok.IsEnabled = CanAccept;
    }

    /// <summary>OK の結果。チェックした列を一覧の並び順で返す。</summary>
    internal Choice Result() => new(_order.Where(column => _rows[column].Box.IsChecked == true).ToArray(), _reset);

    internal Task<Choice?> ShowAsync(Window owner)
    {
        var result = ShowDialog<Choice?>(owner);
        Shown?.Invoke(this);
        return result;
    }
}
