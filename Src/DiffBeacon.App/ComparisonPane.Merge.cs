using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    public MergeResultModel? CurrentMergeSession { get; private set; }
    private SessionEditorHost? _resultHost;
    private FourPaneMaterializedResult? _mergeMaterialization;
    private TextDocument? _mergeSaveDocument;
    private TextDocument? _baseDocument;
    private long _mergeSaveGeneration;
    private bool _mergeResultSaved;
    private bool _updatingMerge;
    private bool _mergeSourcesStale;
    internal bool MergeSourcesStale => _mergeSourcesStale;
    private bool MergeHasPendingComposition => ResultEditor.HasPendingComposition || _resultPreview.HasPendingComposition;
    private void CommitMergeInput()
    {
        if (MergeHasPendingComposition) throw new InvalidOperationException("未確定の文字入力があります。確定または取消してから操作してください。");
        ResultEditor.CommitPendingInput(); _resultPreview.CommitPendingInput();
    }
    private void NotifyMergeContextChanged() { ResultEditor.NotifyEditContextChanged(); _resultPreview.NotifyEditContextChanged(); }
    private void RenderMergeResult() { ResultEditor.RenderFromModel(); _resultPreview.RenderFromModel(); }
    private bool MergeResultDirty => _resultHost is { } host && (!_mergeResultSaved || host.Session.IsModified);
    private readonly ComboBox _mergeSections = new() { Width = 260, Margin = new Thickness(4) };
    private readonly TextBlock _mergeState = new() { Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _mergeProvenance = new() { Margin = new Thickness(8), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private WrapPanel? _mergeToolbar;
    private Button? _middleMergeChoice;
    private sealed record SectionChoice(int Id, string Label) { public override string ToString() => Label; }

    private Control CreateMergeToolbar()
    {
        _mergeToolbar = new WrapPanel { IsVisible = false };
        _mergeSections.ItemTemplate = new FuncDataTemplate<SectionChoice>((choice, _) => new TextBlock { Text = choice?.Label ?? "" });
        _mergeSections.SelectionChanged += (_, _) =>
        {
            if (_updatingMerge || CurrentMergeSession is null || _mergeSections.SelectedItem is not SectionChoice choice) return;
            NotifyMergeContextChanged();
            _resultHost!.ActiveOriginalDiffIndex = choice.Id;
            SelectMergeRange(CurrentMergeSession.Sections.Single(range => range.Section.Id == choice.Id));
        };
        _mergeToolbar.Children.Add(_mergeSections);
        AddAction(_mergeToolbar, "左を採用", () => { ChooseMergeSources(MergeSource.Left); return Task.CompletedTask; });
        AddAction(_mergeToolbar, "祖先を採用", () => { ChooseMergeSources(MergeSource.Base); return Task.CompletedTask; });
        _middleMergeChoice = (Button)_mergeToolbar.Children[^1];
        AddAction(_mergeToolbar, "右を採用", () => { ChooseMergeSources(MergeSource.Right); return Task.CompletedTask; });
        AddAction(_mergeToolbar, "左 → 右を採用", () => { ChooseMergeSources(MergeSource.Left, MergeSource.Right); return Task.CompletedTask; });
        AddAction(_mergeToolbar, "右 → 左を採用", () => { ChooseMergeSources(MergeSource.Right, MergeSource.Left); return Task.CompletedTask; });
        AddAction(_mergeToolbar, "マージを元に戻す", () => { UndoMerge(); return Task.CompletedTask; });
        AddAction(_mergeToolbar, "マージをやり直す", () => { RedoMerge(); return Task.CompletedTask; });
        _mergeToolbar.Children.Add(_mergeState);
        ResultEditor.ModelAdopted += () => { _resultPreview.RenderFromModel(); UpdateMergeView(); };
        _resultPreview.ModelAdopted += () => { ResultEditor.RenderFromModel(); UpdateMergeView(); };
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
        if (_disposed) throw new ObjectDisposedException(nameof(ComparisonPane));
        CommitMergeInput();
        var options = Options();
        var settings = new FourPaneMaterializationSettings { DefaultEol = TextDocument.Create(IndependentText ? LeftEditor.Text ?? "" : _baseText ?? LeftEditor.Text ?? "").NewLine, LeftLabel = "LEFT", MiddleLabel = IndependentText ? "MIDDLE" : "BASE", RightLabel = "RIGHT" };
        var candidate = IndependentText
            ? FourPaneMaterialization.CreateIndependentThreeWay(LeftEditor.Text ?? "", MiddleEditor.Text ?? "", RightEditor.Text ?? "", options, settings)
            : _baseText is null
                ? FourPaneMaterialization.CreateTwoWay(LeftEditor.Text ?? "", RightEditor.Text ?? "", options, settings)
                : FourPaneMaterialization.CreateThreeWay(_baseText, LeftEditor.Text ?? "", RightEditor.Text ?? "", options, autoResolve, settings);
        var host = new SessionEditorHost(candidate.InitialSession) { Choices = candidate.Choices };
        var model = new MergeResultModel(host, candidate.InputCount, candidate.HasAncestor);
        // 候補完成まで現行host・履歴・dirty・staleを保持する。
        ResultEditor.AttachHost(host); _resultPreview.AttachHost(host);
        InvalidateMergeSave(); _resultHost = host; _mergeMaterialization = candidate; _mergeResultSaved = false;
        _mergeSaveDocument = (_textSaveAllowed ? !candidate.HasAncestor ? _leftDocument : _baseDocument ?? _leftDocument : null) ?? TextDocument.Create();
        CurrentMergeSession = model; _mergeSourcesStale = false;
        UpdateEditorLayout(true); UpdateMergeView(); _views.SelectedItem = _resultTab;
    }

    private async Task RestartMergeAsync(bool autoResolve)
    {
        CommitMergeInput();
        var host = _resultHost; var version = host?.Session.Current.Version;
        if (MergeResultDirty && !await Dialogs.ConfirmAsync(_owner, "未保存のマージ結果", "現在の結果を破棄してマージを開始し直しますか？")) return;
        if (!ReferenceEquals(host, _resultHost) || version != _resultHost?.Session.Current.Version) throw new InvalidOperationException("確認中にマージ結果が変更されました。");
        StartMergeSession(autoResolve);
    }

    public void ChooseMergeSources(params MergeSource[] sources)
    {
        if (_mergeSourcesStale) throw new InvalidOperationException("入力が更新されました。旧入力のマージ結果を保存するか、マージを開始し直してください。");
        CommitMergeInput();
        var host = _resultHost ?? throw new InvalidOperationException("先にマージを開始してください。");
        if (sources.Contains(MergeSource.Base) && _mergeMaterialization!.InputCount == 2) throw new ArgumentException("二者マージには祖先がありません。", nameof(sources));
        var id = (_mergeSections.SelectedItem as SectionChoice)?.Id ?? CurrentMergeSession!.Sections.FirstOrDefault(range => range.Section.IsPending)?.Section.Id
            ?? throw new InvalidOperationException("採用する差分を選択してください。");
        NotifyMergeContextChanged(); host.ActiveOriginalDiffIndex = id;
        if (!host.TryChoose(new(host.Session.Current.Version, id, sources.Select(source => (int)source).ToArray()), out _, out var reason)) throw new InvalidOperationException(reason);
        RenderMergeResult(); UpdateMergeView();
    }

    public void UndoMerge() { CommitMergeInput(); if (_resultHost is { } host && host.TryUndo(host.Session.Current.Version, out _, out var reason)) { RenderMergeResult(); UpdateMergeView(); } }
    public void RedoMerge() { CommitMergeInput(); if (_resultHost is { } host && host.TryRedo(host.Session.Current.Version, out _, out var reason)) { RenderMergeResult(); UpdateMergeView(); } }
    private void UpdateMergeView()
    {
        if (CurrentMergeSession is not { } session) return;
        var selectedId = (_mergeSections.SelectedItem as SectionChoice)?.Id;
        _updatingMerge = true;
        try
        {
            var items = session.Sections.Select((range, index) => new SectionChoice(range.Section.Id, $"差分 {index + 1}: {range.Section.State}")).ToArray();
            _mergeSections.ItemsSource = items;
            _mergeSections.SelectedItem = items.FirstOrDefault(item => item.Id == selectedId)
                ?? items.FirstOrDefault(item => session.Sections.Single(range => range.Section.Id == item.Id).Section.IsPending) ?? items.FirstOrDefault();
            var newId = (_mergeSections.SelectedItem as SectionChoice)?.Id;
            if (_resultHost!.ActiveOriginalDiffIndex != newId) { NotifyMergeContextChanged(); _resultHost.ActiveOriginalDiffIndex = newId; }
            _mergeState.Text = (_mergeSourcesStale ? "入力更新 · 旧入力の結果 · 保存またはマージ再開始 / " : "") + $"未解決 {session.UnresolvedCount} · 競合 {session.ConflictCount}";
            var provenance = session.LineProvenance;
            _mergeProvenance.Text = $"行の採用元（1=左、2={(_mergeMaterialization!.HasAncestor ? "祖先" : "中央")},3=右、?=未解決、m=手編集）: "
                + string.Join(" ", provenance.Take(120).Select(line => $"{line.Line}:{line.Source}")) + (provenance.Count > 120 ? " …" : "");
            if (_middleMergeChoice is not null) { _middleMergeChoice.IsVisible = _mergeMaterialization.InputCount == 3; _middleMergeChoice.Content = _mergeMaterialization.HasAncestor ? "祖先を採用" : "中央を採用"; }
            if (_mergeToolbar is not null) _mergeToolbar.IsVisible = true;
            _status.Text = $"マージ結果: 未解決 {session.UnresolvedCount} 個、競合 {session.ConflictCount} 個。";
        }
        finally { _updatingMerge = false; }
    }
    private void SelectMergeRange(MergeResultRange range)
    {
        ResultEditor.CaretIndex = _resultPreview.CaretIndex = range.Start;
        ResultEditor.SelectionStart = _resultPreview.SelectionStart = range.Start;
        ResultEditor.SelectionEnd = _resultPreview.SelectionEnd = checked(range.Start + range.Length);
    }
    private void InvalidateMergeSave() => _mergeSaveGeneration = checked(_mergeSaveGeneration + 1);
    private void DiscardMergeChanges()
    {
        CommitMergeInput();
        if (_resultHost is { } host && host.TryCaptureSave(host.Session.Current.Version, out var capture, out _) && host.TryCompleteSave(capture!, true, out _)) _mergeResultSaved = true;
    }
    private void ResetMergeSession()
    {
        CommitMergeInput(); ResultEditor.DetachHost(); _resultPreview.DetachHost(); InvalidateMergeSave();
        _mergeSourcesStale = false; CurrentMergeSession = null; _resultHost = null; _mergeMaterialization = null; _mergeSaveDocument = null; _mergeResultSaved = false;
        _baseDocument = null; if (_mergeToolbar is not null) _mergeToolbar.IsVisible = false;
    }
    internal Func<Task>? MergeSaveBeforePublish { get; set; }
    internal bool HasSharedResultEditorInLayout => _editGrid.Children.OfType<DockPanel>().Any(panel => panel.Children.Contains(_resultPreview));
    internal string CaptureResultPaneLayoutDiagnostic() => $"selectedTab={_views.SelectedIndex}; host={_resultHost is not null}; disposed={_disposed}; metadataHasBase={ProjectInputs.HasBase(_projectMetadata)}; baseText={_baseText is not null}; columns={_editGrid.ColumnDefinitions.Count}; gridChildren={_editGrid.Children.Count}; logicalPreview={HasSharedResultEditorInLayout}; previewParent={_resultPreview.Parent?.GetType().Name}; visualPreview={this.GetVisualDescendants().Contains(_resultPreview)}; previewBounds={_resultPreview.Bounds}; resultBounds={ResultEditor.Bounds}; previewReadOnly={_resultPreview.IsReadOnly}";
    internal string CaptureMergeDiagnostic()
    {
        if (_resultHost is not { } host) return "host=null";
        var buffer = host.Session.Current;
        return $"version={buffer.Version}; undo={host.Session.UndoCount}; redo={host.Session.RedoCount}; modified={host.Session.IsModified}; saved={_mergeResultSaved}; editorFailure={ResultEditor.LastFailure}; previewFailure={_resultPreview.LastFailure}; text64={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(buffer.Text))}; "
            + "segments=" + string.Join("|", buffer.Segments.Select(segment => $"{segment.Id}/{segment.OriginalDiffId}/{segment.State}/base{segment.BaselineRevision}/raw{segment.TextStart}+{segment.TextLength}/panes{string.Join(',', segment.SourcePanes)}"))
            + "; lines=" + string.Join("|", buffer.Lines.Select((line, index) => $"{index}/rev{line.Revision}/marker{buffer.Marker(index)}/origins{string.Join(',', line.Origins.Select(origin => $"{origin.OwnerId}:{origin.SourcePane}:{origin.SourceLine}"))}"))
            + "; provenance=" + string.Join("|", CurrentMergeSession!.LineProvenance.Select(line => $"{line.Line}:{line.Source}"));
    }
    public async Task SaveMergeResultToAsync(string path, bool allowUnresolved = false, CancellationToken token = default)
    {
        CommitMergeInput();
        var host = _resultHost ?? throw new InvalidOperationException("先にマージを開始してください。");
        var materialized = _mergeMaterialization!; var generation = _mergeSaveGeneration; var textGeneration = _textSaveGeneration;
        var sourceIdentity = TextSourceIdentity(); var sourceDocuments = new[] { _leftDocument, _baseDocument, _rightDocument }; var saveAllowed = _textSaveAllowed;
        if (CurrentMergeSession is { UnresolvedCount: > 0 } && !allowUnresolved) throw new InvalidOperationException("未解決の差分が残っています。");
        if (!host.TryCaptureSave(host.Session.Current.Version, out var capture, out var reason)) throw new InvalidOperationException(reason);
        var text = materialized.ExpandedText(capture!.Buffer); var document = _mergeSaveDocument!;
        void Current()
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _mergeSaveGeneration || textGeneration != _textSaveGeneration || !ReferenceEquals(host, _resultHost) || MergeHasPendingComposition
                || !Equals(sourceIdentity, TextSourceIdentity()) || saveAllowed != _textSaveAllowed
                || !ReferenceEquals(sourceDocuments[0], _leftDocument) || !ReferenceEquals(sourceDocuments[1], _baseDocument) || !ReferenceEquals(sourceDocuments[2], _rightDocument))
                throw new InvalidOperationException("保存中にマージ結果の世代が変更されました。");
        }
        void Guard(string output)
        {
            Current(); ArchiveActions.ValidatePath(output);
            var window = _owner as MainWindow; var panes = window?.SessionPanes ?? [this];
            var own = CaptureProject();
            foreach (var side in Enumerable.Range(0, 3))
            {
                var source = sourceDocuments[side];
                var readOnly = side switch { 0 => own.LeftReadOnly, 1 => own.BaseReadOnly, _ => own.RightReadOnly };
                // 読込み済みの通常入力と同じ出力だけを許可する。新しい入力や原本containerは除外しない。
                if (!saveAllowed || readOnly || source is null || string.IsNullOrWhiteSpace(source.Path) || ProjectInputs.Archive(own, side) is not null
                    || !ArchivePaths.SameFile(source.Path, output) || string.IsNullOrWhiteSpace(TextPath(side).Text) || !ArchivePaths.SameFile(source.Path, TextPath(side).Text!)) continue;
                own = side switch { 0 => own with { LeftPath = "" }, 1 => own with { BasePath = "" }, _ => own with { RightPath = "" } };
            }
            ProjectInputs.EnsureOutput(output, panes.Where(pane => !ReferenceEquals(pane, this)).Select(pane => pane.CaptureProject()).Append(own), window?.WorkspaceSourcePath);
            foreach (var pane in panes) pane.EnsureProjectOutputWritable(output);
        }
        var output = ArchiveActions.ValidatePath(path); Guard(output);
        async Task Publish(string temporary, string target, CancellationToken cancellation)
        {
            await Dispatcher.UIThread.InvokeAsync(() => MergeSaveBeforePublish?.Invoke() ?? Task.CompletedTask);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Guard(target); cancellation.ThrowIfCancellationRequested();
                if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("読み取り専用のファイルは保存できません。");
                File.Move(temporary, target, overwrite: true);
            });
        }
        await document.SaveCopyAsync(output, text, token, Publish);
        Current(); if (host.TryCompleteSave(capture, true, out _)) _mergeResultSaved = true; UpdateMergeView(); _status.Text = $"保存しました: {output}";
    }
}
