using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    public MergeSession? CurrentMergeSession { get; private set; }
    private TextDocument? _baseDocument;
    private bool _updatingMerge;
    private readonly ComboBox _mergeSections = new() { Width = 260, Margin = new Thickness(4) };
    private readonly TextBlock _mergeState = new() { Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _mergeProvenance = new() { Margin = new Thickness(8), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private WrapPanel? _mergeToolbar;
    private sealed record SectionChoice(int Id, string Label) { public override string ToString() => Label; }

    private Control CreateMergeToolbar()
    {
        _mergeToolbar = new WrapPanel { IsVisible = false };
        _mergeSections.ItemTemplate = new FuncDataTemplate<SectionChoice>((choice, _) => new TextBlock { Text = choice?.Label ?? "" });
        _mergeSections.SelectionChanged += (_, _) =>
        {
            if (_updatingMerge || CurrentMergeSession is null || _mergeSections.SelectedItem is not SectionChoice choice) return;
            var range = CurrentMergeSession.Sections.Single(section => section.Section.Id == choice.Id);
            SelectMergeRange(range);
        };
        _mergeToolbar.Children.Add(_mergeSections);
        AddAction(_mergeToolbar, "左を採用", () => { ChooseMergeSources(MergeSource.Left); return Task.CompletedTask; });
        AddAction(_mergeToolbar, "祖先を採用", () => { ChooseMergeSources(MergeSource.Base); return Task.CompletedTask; });
        AddAction(_mergeToolbar, "右を採用", () => { ChooseMergeSources(MergeSource.Right); return Task.CompletedTask; });
        AddAction(_mergeToolbar, "左 → 右を採用", () => { ChooseMergeSources(MergeSource.Left, MergeSource.Right); return Task.CompletedTask; });
        AddAction(_mergeToolbar, "右 → 左を採用", () => { ChooseMergeSources(MergeSource.Right, MergeSource.Left); return Task.CompletedTask; });
        AddAction(_mergeToolbar, "マージを元に戻す", () => { UndoMerge(); return Task.CompletedTask; });
        AddAction(_mergeToolbar, "マージをやり直す", () => { RedoMerge(); return Task.CompletedTask; });
        _mergeToolbar.Children.Add(_mergeState);
        ResultEditor.TextChanged += (_, _) =>
        {
            if (_updatingMerge || CurrentMergeSession is null || ResultEditor.Text == CurrentMergeSession.Text) return;
            try { CurrentMergeSession.UpdateText(ResultEditor.Text ?? ""); UpdateMergeView(); }
            catch (Exception exception) { UpdateMergeView(); _status.Text = exception.Message; }
        };
        foreach (var editor in new[] { ResultEditor, _resultPreview })
        {
            editor.AddHandler(KeyDownEvent, (_, e) =>
            {
                if (CurrentMergeSession is null || !(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) return;
                if (e.Key == Key.Z) { if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) RedoMerge(); else UndoMerge(); e.Handled = true; }
                else if (e.Key == Key.Y) { RedoMerge(); e.Handled = true; }
            }, RoutingStrategies.Tunnel);
        }
        return _mergeToolbar;
    }

    private Control CreateMergeResultView()
    {
        var panel = new DockPanel();
        DockPanel.SetDock(_mergeProvenance, Dock.Bottom); panel.Children.Add(_mergeProvenance); panel.Children.Add(ResultEditor);
        return panel;
    }

    public void StartMergeSession(bool autoResolve = true)
    {
        CurrentMergeSession = _baseText is null
            ? MergeSession.CreateTwoWay(LeftEditor.Text ?? "", RightEditor.Text ?? "", Options())
            : MergeSession.CreateThreeWay(_baseText, LeftEditor.Text ?? "", RightEditor.Text ?? "", Options(), autoResolve);
        UpdateMergeView(); _views.SelectedItem = _resultTab;
    }

    private async Task RestartMergeAsync(bool autoResolve)
    {
        if (ResultEditor.Text != _savedResult && !await Dialogs.ConfirmAsync(_owner, "未保存のマージ結果", "現在の結果を破棄してマージを開始し直しますか？")) return;
        StartMergeSession(autoResolve);
    }

    public void ChooseMergeSources(params MergeSource[] sources)
    {
        SynchronizeMergeText();
        var session = CurrentMergeSession ?? throw new InvalidOperationException("先にマージを開始してください。");
        var id = (_mergeSections.SelectedItem as SectionChoice)?.Id ?? session.Sections.FirstOrDefault(section => section.Section.IsPending)?.Section.Id
            ?? throw new InvalidOperationException("採用する差分を選択してください。");
        session.Choose(id, sources); UpdateMergeView();
    }

    private void SynchronizeMergeText()
    {
        if (!_updatingMerge && CurrentMergeSession is { } session && session.Text != ResultEditor.Text) session.UpdateText(ResultEditor.Text ?? "");
    }
    public void UndoMerge() { SynchronizeMergeText(); if (CurrentMergeSession?.Undo() == true) UpdateMergeView(); }
    public void RedoMerge() { SynchronizeMergeText(); if (CurrentMergeSession?.Redo() == true) UpdateMergeView(); }
    private void UpdateMergeView()
    {
        if (CurrentMergeSession is not { } session) return;
        var selectedId = (_mergeSections.SelectedItem as SectionChoice)?.Id;
        _updatingMerge = true;
        try
        {
            ResultEditor.Text = session.Text; _resultPreview.Text = session.Text;
            var items = session.Sections.Where(range => range.Section.IsDifference).Select((range, index) =>
                new SectionChoice(range.Section.Id, $"差分 {index + 1}: {SectionLabel(range.Section)}")).ToArray();
            _mergeSections.ItemsSource = items;
            _mergeSections.SelectedItem = items.FirstOrDefault(item => item.Id == selectedId)
                ?? items.FirstOrDefault(item => session.Sections.Single(range => range.Section.Id == item.Id).Section.IsPending) ?? items.FirstOrDefault();
            _mergeState.Text = $"未解決 {session.UnresolvedCount} · 競合 {session.ConflictCount}";
            var provenance = session.LineProvenance;
            _mergeProvenance.Text = "行の採用元（1=左、2=祖先、3=右、?=未解決、m=手編集）: "
                + string.Join(" ", provenance.Take(120).Select(line => $"{line.Line}:{line.Source}"))
                + (provenance.Count > 120 ? " …" : "");
            if (_mergeToolbar is not null) _mergeToolbar.IsVisible = true;
            _status.Text = $"マージ結果: 未解決 {session.UnresolvedCount} 個、競合 {session.ConflictCount} 個。";
        }
        finally { _updatingMerge = false; }
    }
    private static string SectionLabel(MergeSection section) => section.State switch
    {
        MergeSectionState.Conflict => section.IsWhitespaceOnly ? "競合（空白のみ）" : "競合",
        MergeSectionState.Unresolved => "未解決", MergeSectionState.Edited => "手編集",
        _ => string.Join(" → ", section.Sources.Select(source => source switch { MergeSource.Left => "左", MergeSource.Base => "祖先", _ => "右" }))
    };
    private void SelectMergeRange(MergeSectionRange range)
    {
        ResultEditor.SelectionStart = _resultPreview.SelectionStart = range.Start;
        ResultEditor.SelectionEnd = _resultPreview.SelectionEnd = Math.Min(ResultEditor.Text?.Length ?? 0, range.Start + range.Length);
        ResultEditor.CaretIndex = _resultPreview.CaretIndex = range.Start;
    }
    private void ResetMergeSession()
    {
        _updatingMerge = true;
        try { CurrentMergeSession = null; ResultEditor.Text = _savedResult = ""; _resultPreview.Text = ""; _baseDocument = null; if (_mergeToolbar is not null) _mergeToolbar.IsVisible = false; }
        finally { _updatingMerge = false; }
    }
    public async Task SaveMergeResultToAsync(string path, bool allowUnresolved = false, CancellationToken token = default)
    {
        SynchronizeMergeText();
        if (CurrentMergeSession is { UnresolvedCount: > 0 } && !allowUnresolved) throw new InvalidOperationException("未解決の差分が残っています。");
        var output = Path.GetFullPath(path);
        EnsureProjectOutputWritable(output);
        if (!_textSaveAllowed)
        {
            foreach (var source in new[] { LeftPath.Text, BasePath.Text, RightPath.Text })
                if (!string.IsNullOrWhiteSpace(source) && File.Exists(source) && Path.GetFullPath(source).Equals(output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("変換後のマージ結果で元の文書を上書きできません。別の保存先を選んでください。");
        }
        var document = (_textSaveAllowed ? _baseDocument ?? _leftDocument : null) ?? TextDocument.Create();
        var text = ResultEditor.Text ?? "";
        await document.SaveCopyAsync(output, text, token);
        _savedResult = text; _status.Text = $"保存しました: {output}";
    }
}
