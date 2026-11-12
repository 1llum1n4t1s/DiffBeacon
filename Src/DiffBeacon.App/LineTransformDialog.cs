using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal sealed class LineTransformDialog : Window
{
    private sealed record Scope(string Label, TablePaneMask Panes)
    {
        public override string ToString() => Label;
    }

    private static readonly Scope[] Scopes =
    [
        new("全側", TablePaneMask.All), new("左", TablePaneMask.Left),
        new("中央", TablePaneMask.Base), new("右", TablePaneMask.Right),
        new("左・中央", TablePaneMask.Left | TablePaneMask.Base),
        new("左・右", TablePaneMask.Left | TablePaneMask.Right),
        new("中央・右", TablePaneMask.Base | TablePaneMask.Right)
    ];
    private readonly List<LineTransformRule> _rules;
    private readonly ComparisonProject? _project;
    private bool _closed, _applying;
    internal static Action<LineTransformDialog>? Shown { get; set; }
    internal TextBox Expression { get; } = new()
    {
        Name = "LineTransformExpression", AcceptsReturn = true, Height = 88,
        MaxLength = LineTransformRule.MaximumExpressionLength,
        PlaceholderText = "例: ColumnRange(\"1,3\")"
    };
    internal ComboBox Target { get; } = new() { Name = "LineTransformTarget", ItemsSource = Scopes, SelectedIndex = 0 };
    internal ListBox RuleList { get; } = new() { Name = "LineTransformRules", Height = 180 };
    internal TextBlock Error { get; } = new() { Name = "LineTransformError", TextWrapping = TextWrapping.Wrap };
    internal Button Accept { get; } = new() { Name = "LineTransformApply", Content = "適用" };
    internal Button Cancel { get; } = new() { Name = "LineTransformCancel", Content = "取消" };
    internal IReadOnlyList<LineTransformRule> Rules => Array.AsReadOnly(_rules.ToArray());
    private readonly Button _change, _delete, _clear, _up, _down;

    internal LineTransformDialog(IReadOnlyList<LineTransformRule> rules, ComparisonProject? project = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.ToList(); _project = project;
        Title = "行変換"; Width = MinWidth = 720; Height = MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("*,Auto") };
        var fields = new StackPanel { Spacing = 8 };
        fields.Children.Add(new TextBlock
        {
            Text = "比較キーの行全体に、一覧の順で式を適用します。元の本文と保存内容は変えません。",
            TextWrapping = TextWrapping.Wrap
        });
        fields.Children.Add(new TextBlock { Text = "式" }); fields.Children.Add(Expression);
        fields.Children.Add(new TextBlock { Text = "適用する側" }); fields.Children.Add(Target);
        fields.Children.Add(new TextBlock
        {
            Text = "対象の側と、式の Left / Middle / Right 接頭辞は独立した指定です。入力した式は「追加」または「変更」で一覧に登録してください。",
            TextWrapping = TextWrapping.Wrap
        });
        RuleList.ItemTemplate = new FuncDataTemplate<LineTransformRule>((rule, _) => new TextBlock
        {
            Text = rule is null ? "（不正な規則）" : $"[{ScopeLabel(rule.Panes)}] {rule.Expression}",
            TextWrapping = TextWrapping.Wrap
        });
        RuleList.SelectionChanged += (_, _) =>
        {
            if (RuleList.SelectedItem is LineTransformRule rule)
            {
                Expression.Text = rule.Expression;
                Target.SelectedItem = Scopes.FirstOrDefault(scope => scope.Panes == rule.Panes);
            }
            RefreshActions();
        };
        var actions = new WrapPanel();
        AddButton(actions, "LineTransformAdd", "追加", () => Upsert(false));
        _change = AddButton(actions, "LineTransformChange", "変更", () => Upsert(true));
        _delete = AddButton(actions, "LineTransformDelete", "削除", DeleteSelected);
        _clear = AddButton(actions, "LineTransformClear", "全解除", () => { _rules.Clear(); Refresh(); });
        _up = AddButton(actions, "LineTransformUp", "↑", () => Move(-1));
        _down = AddButton(actions, "LineTransformDown", "↓", () => Move(1));
        fields.Children.Add(actions); fields.Children.Add(RuleList); fields.Children.Add(Error);
        root.Children.Add(new ScrollViewer { Name = "LineTransformScroll", Content = fields });
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Thickness(0, 12, 0, 0)
        };
        footer.Children.Add(Cancel); footer.Children.Add(Accept); Grid.SetRow(footer, 1); root.Children.Add(footer);
        Content = root;
        Cancel.Click += (_, _) => { if (!_closed) Close(null); };
        Accept.Click += (_, _) => Apply();
        Closed += (_, _) => { _closed = true; Accept.IsEnabled = false; };
        Opened += (_, _) => Shown?.Invoke(this);
        Refresh();
    }

    private static string ScopeLabel(TablePaneMask panes) => Scopes.FirstOrDefault(scope => scope.Panes == panes)?.Label ?? $"不正な対象: {panes}";

    private Button AddButton(Panel panel, string name, string label, Action action)
    {
        var button = new Button { Name = name, Content = label, Margin = new Thickness(0, 0, 8, 4) };
        button.Click += (_, _) => { if (!_closed && !_applying) action(); };
        panel.Children.Add(button); return button;
    }

    private void Upsert(bool update)
    {
        try
        {
            var index = RuleList.SelectedIndex;
            if (update && index < 0) return;
            if (Target.SelectedItem is not Scope scope) throw new ArgumentException("適用する側を選択してください。");
            var rule = new LineTransformRule(Expression.Text ?? "", scope.Panes);
            var candidate = _rules.ToList();
            if (update) candidate[index] = rule; else candidate.Add(rule);
            LineTransformPlan.Compile(candidate, new LineTransformBudget(),
                timestampEnvironment: LineTransformTimestampEnvironments.ForRules(candidate),
                stringEnvironment: LineTransformStringEnvironments.ForRules(candidate),
                listEnvironment: LineTransformListEnvironments.Create(LineTransformLogEnvironments.Trace, _project));
            if (update) _rules[index] = rule; else _rules.Add(rule);
            Refresh(update ? index : _rules.Count - 1);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or ArithmeticException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Error.Text = exception.Message;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            { if (!_closed) Error.BringIntoView(); }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private void DeleteSelected()
    {
        var index = RuleList.SelectedIndex;
        if (index < 0) return;
        _rules.RemoveAt(index); Refresh(Math.Min(index, _rules.Count - 1));
    }

    private void Move(int delta)
    {
        var index = RuleList.SelectedIndex; var destination = index + delta;
        if (index < 0 || destination < 0 || destination >= _rules.Count) return;
        (_rules[index], _rules[destination]) = (_rules[destination], _rules[index]); Refresh(destination);
    }

    private void Refresh(int index = -1)
    {
        RuleList.ItemsSource = _rules.ToArray(); RuleList.SelectedIndex = index;
        Error.Text = ""; RefreshActions();
    }

    private void RefreshActions()
    {
        var index = RuleList.SelectedIndex;
        _change.IsEnabled = _delete.IsEnabled = index >= 0;
        _clear.IsEnabled = _rules.Count > 0;
        _up.IsEnabled = index > 0;
        _down.IsEnabled = index >= 0 && index + 1 < _rules.Count;
    }

    private void Apply()
    {
        if (_closed || _applying) return;
        _applying = true; Accept.IsEnabled = false;
        try
        {
            var result = _rules.ToArray();
            LineTransformPlan.Compile(result, new LineTransformBudget(),
                timestampEnvironment: LineTransformTimestampEnvironments.ForRules(result),
                stringEnvironment: LineTransformStringEnvironments.ForRules(result),
                listEnvironment: LineTransformListEnvironments.Create(LineTransformLogEnvironments.Trace, _project));
            Close(result);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or ArithmeticException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Error.Text = exception.Message;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            { if (!_closed) Error.BringIntoView(); }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        finally
        {
            _applying = false; Accept.IsEnabled = !_closed;
        }
    }
}
