using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DiffBeacon.App;

/// <summary>検索・置換の設定（本家 LastSearchInfos）。前回の値として次に開いたダイアログと F3 に使う。</summary>
internal sealed record TextSearchSettings(string Pattern, bool MatchCase, bool WholeWord, bool Regex, bool NoWrap, bool NoClose)
{
    internal static TextSearchSettings Default { get; } = new("", false, false, false, false, true);
    internal TextSearchQuery Query => new(Pattern, MatchCase, WholeWord && !Regex, Regex);
}

/// <summary>ダイアログのボタンから呼ぶ操作。</summary>
internal enum TextSearchAction { FindNext, FindPrevious, Replace, ReplaceAll }

/// <summary>ボタンを押したときの入力（設定・置換後の文字列・範囲が選択範囲か）。</summary>
internal sealed record TextSearchRequest(TextSearchAction Action, TextSearchSettings Settings, string Replacement, bool SelectionOnly);

/// <summary>
/// 本家WinMergeの「検索」（IDD_EDIT_FIND、cfindtextdlg.cpp）と「置換」（IDD_EDIT_REPLACE、ceditreplacedlg.cpp）のダイアログ。
/// - 本家と同じくモードレスで、開いたまま本文を操作できる。検索は「このダイアログ ボックスを閉じない」が外れていれば、見つけた時点で閉じる。
/// - 正規表現を選ぶと単語単位は使えない（本家 UpdateRegExp）。検索する文字列が空ならボタンを押せない（本家 UpdateControls）。
/// - 置換の「範囲」は、開いたときに選択があれば選べる。複数行の選択なら初めから「選択範囲」にする（本家 OnEditReplace）。
///   選択範囲では「置換して次に」は押せない（本家と同じ）。
/// - 入力の履歴（本家の CMemComboBox）は、アプリを閉じるまでの間だけ覚える。
/// ボタンの処理はペイン（<see cref="ComparisonPane"/>）が行い、ダイアログは入力の収集と状態の表示だけを持つ。
/// </summary>
internal sealed class TextSearchDialog : Window
{
    /// <summary>headless検証用: ダイアログを表示した直後に呼ぶ。</summary>
    internal static Action<TextSearchDialog>? Shown { get; set; }

    private readonly Func<TextSearchRequest, bool> _execute;
    internal bool ReplaceMode { get; }
    internal ComboBox FindText { get; } = new() { Name = "TextSearchFindText", IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    internal ComboBox ReplaceText { get; } = new() { Name = "TextSearchReplaceText", IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    internal CheckBox WholeWord { get; } = new() { Content = "単語単位で探す(_W)", Name = "TextSearchWholeWord" };
    internal CheckBox MatchCase { get; } = new() { Content = "大文字と小文字を区別する(_C)", Name = "TextSearchMatchCase" };
    internal CheckBox Regex { get; } = new() { Content = "正規表現(_E)", Name = "TextSearchRegex" };
    internal CheckBox NoWrap { get; }
    internal CheckBox NoClose { get; } = new() { Content = "このダイアログ ボックスを閉じない(_D)", Name = "TextSearchNoClose" };
    internal RadioButton ScopeSelection { get; } = new() { Content = "選択範囲(_S)", GroupName = "TextSearchScope", Name = "TextSearchScopeSelection" };
    internal RadioButton ScopeWholeFile { get; } = new() { Content = "ファイル全体(_O)", GroupName = "TextSearchScope", Name = "TextSearchScopeWholeFile" };
    internal Button FindNextButton { get; } = new() { Content = "次を検索(_F)", Name = "TextSearchFindNext", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    internal Button FindPreviousButton { get; }
    internal Button ReplaceButton { get; } = new() { Content = "置換して次に(_R)", Name = "TextSearchReplace", HorizontalAlignment = HorizontalAlignment.Stretch };
    internal Button ReplaceAllButton { get; } = new() { Content = "すべて置換(_A)", Name = "TextSearchReplaceAll", HorizontalAlignment = HorizontalAlignment.Stretch };
    internal Button CancelButton { get; } = new() { Content = "キャンセル", Name = "TextSearchCancel", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Stretch };

    internal TextSearchDialog(bool replace, TextSearchSettings initial, string? seed, IReadOnlyList<string> findHistory, IReadOnlyList<string> replaceHistory,
        string replacement, bool selectionAvailable, bool selectionScope, Func<TextSearchRequest, bool> execute)
    {
        ReplaceMode = replace; _execute = execute;
        Title = replace ? "置換" : "検索";
        Width = 560; SizeToContent = SizeToContent.Height; CanResize = false; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        NoWrap = new CheckBox
        {
            // 本家は二つのダイアログで文言が違う（検索は IDC_FINDDLG_DONTWRAP、置換は IDC_EDIT_SCOPE_DONT_WRAP）。
            Content = replace ? "ファイルの終端で折り返さない(_D)" : "最後まで検索した後ループしない(_O)", Name = "TextSearchNoWrap",
        };
        FindPreviousButton = new Button { Content = replace ? "前を検索(_V)" : "前を検索(_P)", Name = "TextSearchFindPrevious", HorizontalAlignment = HorizontalAlignment.Stretch };
        FindText.ItemsSource = findHistory.ToArray(); ReplaceText.ItemsSource = replaceHistory.ToArray();
        FindText.Text = seed ?? initial.Pattern; ReplaceText.Text = replacement;
        MatchCase.IsChecked = initial.MatchCase; WholeWord.IsChecked = initial.WholeWord && !initial.Regex; Regex.IsChecked = initial.Regex;
        NoWrap.IsChecked = initial.NoWrap; NoClose.IsChecked = initial.NoClose;
        ScopeSelection.IsEnabled = selectionAvailable;
        ScopeSelection.IsChecked = selectionAvailable && selectionScope; ScopeWholeFile.IsChecked = !(selectionAvailable && selectionScope);

        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions(replace ? "Auto,Auto" : "Auto"), RowSpacing = 6, ColumnSpacing = 8 };
        AddField(fields, 0, "検索する文字列(_N):", FindText);
        if (replace) AddField(fields, 1, "置換後の文字列(_P):", ReplaceText);
        var checks = new StackPanel { Spacing = 2 };
        checks.Children.Add(WholeWord); checks.Children.Add(MatchCase); checks.Children.Add(Regex); checks.Children.Add(NoWrap);
        if (!replace) checks.Children.Add(NoClose);
        var options = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        options.Children.Add(checks);
        if (replace)
        {
            var scope = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
            scope.Children.Add(ScopeSelection); scope.Children.Add(ScopeWholeFile);
            // 本家のグループボックス「範囲」に当たる枠。
            var frame = new Border { BorderBrush = WinMergeColors.FrameBorder, BorderThickness = new Thickness(1), Padding = new Thickness(4), Child = new StackPanel { Children = { new TextBlock { Text = "範囲" }, scope } } };
            Grid.SetColumn(frame, 1); options.Children.Add(frame);
        }
        var left = new StackPanel(); left.Children.Add(fields); left.Children.Add(options);
        var buttons = new StackPanel { Spacing = 6, Width = 130, Margin = new Thickness(12, 0, 0, 0) };
        buttons.Children.Add(FindNextButton); buttons.Children.Add(FindPreviousButton);
        if (replace) { buttons.Children.Add(ReplaceButton); buttons.Children.Add(ReplaceAllButton); }
        buttons.Children.Add(CancelButton);
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Right); root.Children.Add(buttons); root.Children.Add(left);
        Content = root;

        FindText.PropertyChanged += (_, args) => { if (args.Property == ComboBox.TextProperty) UpdateControls(); };
        Regex.IsCheckedChanged += (_, _) => UpdateControls();
        ScopeSelection.IsCheckedChanged += (_, _) => UpdateControls();
        FindNextButton.Click += (_, _) => Run(TextSearchAction.FindNext);
        FindPreviousButton.Click += (_, _) => Run(TextSearchAction.FindPrevious);
        ReplaceButton.Click += (_, _) => Run(TextSearchAction.Replace);
        ReplaceAllButton.Click += (_, _) => Run(TextSearchAction.ReplaceAll);
        CancelButton.Click += (_, _) => Close();
        UpdateControls();
    }

    private static void AddField(Grid grid, int row, string label, Control input)
    {
        var text = new Label { Content = label, Target = input, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
        Grid.SetRow(text, row); grid.Children.Add(text);
        Grid.SetRow(input, row); Grid.SetColumn(input, 1); grid.Children.Add(input);
    }

    /// <summary>現在の入力の設定。</summary>
    internal TextSearchSettings Settings => new(FindText.Text ?? "", MatchCase.IsChecked == true, WholeWord.IsChecked == true && Regex.IsChecked != true,
        Regex.IsChecked == true, NoWrap.IsChecked == true, NoClose.IsChecked == true);

    /// <summary>本家 UpdateControls／UpdateRegExp と同じく、入力に合わせてボタンと単語単位の可否を変える。</summary>
    private void UpdateControls()
    {
        var any = !string.IsNullOrEmpty(FindText.Text);
        FindNextButton.IsEnabled = FindPreviousButton.IsEnabled = ReplaceAllButton.IsEnabled = any;
        ReplaceButton.IsEnabled = any && ScopeSelection.IsChecked != true;
        if (Regex.IsChecked == true) { WholeWord.IsChecked = false; WholeWord.IsEnabled = false; }
        else WholeWord.IsEnabled = true;
    }

    /// <summary>ボタンの操作をペインへ渡す。検索で見つかり、閉じない設定でなければ閉じる（本家 CFindTextDlg::FindText）。</summary>
    internal bool Run(TextSearchAction action)
    {
        if (string.IsNullOrEmpty(FindText.Text)) return false;
        var request = new TextSearchRequest(action, Settings, ReplaceText.Text ?? "", ReplaceMode && ScopeSelection.IsChecked == true);
        var done = _execute(request);
        if (done && !ReplaceMode && request.Settings.NoClose == false) Close();
        return done;
    }

    /// <summary>既に開いているダイアログを、新しい検索する文字列で前へ出す（本家は同じダイアログを再表示する）。</summary>
    internal void Reuse(string? seed)
    {
        if (seed is not null) FindText.Text = seed;
        Activate();
        FindText.Focus();
    }

    internal void ShowFor(Window owner)
    {
        Show(owner);
        FindText.Focus();
        Shown?.Invoke(this);
    }
}
