using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    private readonly CheckBox _ignoreNumbers = new() { Content = "数字を無視" };
    private readonly ComboBox _whitespace = new() { ItemsSource = new[] { "空白: 無視なし", "前後だけ無視", "空白の増減を無視", "すべての空白を無視" }, SelectedIndex = 0, Width = 195 };
    private readonly ComboBox _comments = new() { ItemsSource = new[] { "コメント: 無視なし", "C / C++ / Java", "C#", "Python", "XML / HTML" }, SelectedIndex = 0, Width = 190 };
    private SubstitutionRule[] _substitutions = [];
    private readonly TextBlock _substitutionCount = new() { Text = "置換 0件", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };

    private Control CreateAdvancedFilters()
    {
        var panel = new WrapPanel();
        foreach (var control in new Control[] { _ignoreNumbers, _whitespace, _comments }) { control.Margin = new Thickness(8, 4); panel.Children.Add(control); }
        AddAction(panel, "置換フィルター…", EditSubstitutionsAsync); panel.Children.Add(_substitutionCount);
        return panel;
    }
    public void SetAdvancedFilters(bool ignoreNumbers, CommentSyntax syntax, WhitespaceMode whitespace, params SubstitutionRule[] substitutions)
    {
        _ignoreNumbers.IsChecked = ignoreNumbers; _comments.SelectedIndex = (int)syntax; _whitespace.SelectedIndex = (int)whitespace;
        _substitutions = substitutions.ToArray(); _substitutionCount.Text = $"置換 {_substitutions.Count(rule => rule.Enabled)}件";
    }
    private async Task EditSubstitutionsAsync()
    {
        var rules = _substitutions.ToList();
        var dialog = new Window { Title = "置換フィルター", Width = 720, Height = 550, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var fields = new StackPanel { Spacing = 8 };
        fields.Children.Add(new TextBlock { Text = "比較するキーだけを順に置換します。元のテキストと保存内容は変えません。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var pattern = new TextBox { PlaceholderText = "検索文字列 / 正規表現" }; var replacement = new TextBox { PlaceholderText = "置換後の文字列" };
        fields.Children.Add(pattern); fields.Children.Add(replacement);
        var flags = new WrapPanel(); var enabled = new CheckBox { Content = "有効", IsChecked = true }; var regex = new CheckBox { Content = "正規表現", IsChecked = true };
        var matchCase = new CheckBox { Content = "大文字小文字を区別", IsChecked = true }; var wholeWord = new CheckBox { Content = "単語単位" };
        foreach (var flag in new[] { enabled, regex, matchCase, wholeWord }) { flag.Margin = new Thickness(4); flags.Children.Add(flag); } fields.Children.Add(flags);
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var list = new ListBox { ItemTemplate = new FuncDataTemplate<SubstitutionRule>((rule, _) => new TextBlock { Text = rule is null ? "" : $"{(rule.Enabled ? "✓" : "—")} {rule.Pattern} → {rule.Replacement}" }) };
        void Refresh(int index = -1) { list.ItemsSource = rules.ToArray(); list.SelectedIndex = index; }
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is SubstitutionRule rule) { pattern.Text = rule.Pattern; replacement.Text = rule.Replacement; enabled.IsChecked = rule.Enabled; regex.IsChecked = rule.UseRegex; matchCase.IsChecked = rule.MatchCase; wholeWord.IsChecked = rule.WholeWord; } };
        var actions = new WrapPanel();
        void Upsert(bool update)
        {
            try
            {
                var rule = new SubstitutionRule(pattern.Text ?? "", replacement.Text ?? "", matchCase.IsChecked == true) { Enabled = enabled.IsChecked == true, UseRegex = regex.IsChecked == true, WholeWord = wholeWord.IsChecked == true };
                TextDiffer.Compare("", "", new ComparisonOptions { SubstitutionRules = [rule] });
                if (update && list.SelectedIndex >= 0) { var index = list.SelectedIndex; rules[index] = rule; Refresh(index); }
                else { rules.Add(rule); Refresh(rules.Count - 1); }
                error.Text = "";
            }
            catch (Exception exception) { error.Text = exception.Message; }
        }
        void Button(string label, Action action) { var button = new Button { Content = label, Margin = new Thickness(4) }; button.Click += (_, _) => action(); actions.Children.Add(button); }
        Button("追加", () => Upsert(false)); Button("変更", () => Upsert(true));
        Button("削除", () => { if (list.SelectedIndex >= 0) { rules.RemoveAt(list.SelectedIndex); Refresh(); } });
        Button("↑", () => { var index = list.SelectedIndex; if (index > 0) { (rules[index - 1], rules[index]) = (rules[index], rules[index - 1]); Refresh(index - 1); } });
        Button("↓", () => { var index = list.SelectedIndex; if (index >= 0 && index + 1 < rules.Count) { (rules[index + 1], rules[index]) = (rules[index], rules[index + 1]); Refresh(index + 1); } });
        Button("キャンセル", () => dialog.Close(false)); Button("適用", () => dialog.Close(true));
        fields.Children.Add(actions); fields.Children.Add(error); DockPanel.SetDock(fields, Dock.Top); panel.Children.Add(fields); panel.Children.Add(list); dialog.Content = panel; Refresh();
        if (await dialog.ShowDialog<bool>(_owner)) { _substitutions = rules.ToArray(); _substitutionCount.Text = $"置換 {_substitutions.Count(rule => rule.Enabled)}件"; }
    }
}
