using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public sealed partial class MainWindow : Window
{
    private readonly TabControl _tabs = new();
    private readonly List<TabItem> _sessions = [];
    private bool _headerBringQueued;
    internal ScrollViewer? SessionHeaders { get; private set; }
    internal ImageApplicationOptionsStore ImageOptions { get; }
    internal ArchiveWorkingStore ArchiveTexts { get; private set; } = new();
    internal ArchiveWindowLifetime ArchiveLifetime { get; } = new();
    public ComparisonPane ActivePane => (ComparisonPane)((TabItem)_tabs.SelectedItem!).Content!;

    public MainWindow(string[]? arguments = null) : this(arguments, new ImageApplicationOptionsStore()) { }

    internal MainWindow(string[]? arguments, ImageApplicationOptionsStore imageOptions)
    {
        ImageOptions = imageOptions;
        Closed += (_, _) => { ArchiveTexts.Clear(); ArchiveLifetime.Clear(); };
        Title = "DiffBeacon";
        Width = 1280;
        Height = 850;
        MinWidth = 850;
        MinHeight = 550;
        Background = new SolidColorBrush(Color.Parse("#171B24"));
        var root = new DockPanel();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(20, 12) };
        header.Children.Add(new TextBlock { Text = "DiffBeacon", FontSize = 23, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var add = new Button { Content = "+ 新しい比較", Margin = new Thickness(8, 0) };
        add.Click += async (_, _) => await AddSessionFromUiAsync();
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        var theme = new Button { Content = "明 / 暗" };
        theme.Click += (_, _) => Application.Current!.RequestedThemeVariant = Application.Current.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
            ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        Grid.SetColumn(theme, 2);
        header.Children.Add(theme);
        var package = PackagingButton(); Grid.SetColumn(package, 3); header.Children.Add(package);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_tabs);
        // 多数の比較タブでも見出しが本文を押し出さないよう、既存の見出しpresenterをスクロールする。
        _tabs.TemplateApplied += (_, args) =>
        {
            if (args.NameScope.Find<ItemsPresenter>("PART_ItemsPresenter") is not { Parent: DockPanel dock } presenter) return;
            dock.Children.Remove(presenter);
            SessionHeaders = new ScrollViewer { Name = "SessionHeaders", MaxHeight = 110, Content = presenter,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            DockPanel.SetDock(SessionHeaders, Dock.Top); dock.Children.Insert(0, SessionHeaders);
            SessionHeaders.SizeChanged += (_, _) => QueueSelectedHeader();
            QueueSelectedHeader();
        };
        _tabs.SelectionChanged += (_, _) =>
        {
            UpdateImageDisplayVisibility();
            QueueSelectedHeader();
        };
        PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty) UpdateImageDisplayVisibility(); };
        Content = root;
        AddSession(arguments);
        if (ImageOptions.Diagnostic is { } diagnostic)
            Opened += async (_, _) => await Dialogs.MessageAsync(this, "画像操作設定", diagnostic);
        if (arguments is { Length: 1 } && Path.GetExtension(arguments[0]).ToLowerInvariant() is ".json" or ".winmerge" or ".diffbeacon")
            Opened += async (_, _) =>
            {
                try { await OpenWorkspaceAsync(arguments[0]); }
                catch (Exception ex) { await Dialogs.MessageAsync(this, "プロジェクトを開けませんでした", ex.Message); }
            };
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.F7) { ActivePane.NavigateDifference(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); e.Handled = true; }
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.N) { e.Handled = true; await AddSessionFromUiAsync(); }
        };
        Closing += async (_, e) =>
        {
            if (!_sessions.Any(x => ((ComparisonPane)x.Content!).HasUnsavedChanges)) return;
            e.Cancel = true;
            if (await Dialogs.ConfirmAsync(this, "未保存の変更", "変更を保存せずに閉じますか？"))
            {
                foreach (var session in _sessions) ((ComparisonPane)session.Content!).DiscardChanges();
                Close();
            }
        };
    }

    private void QueueSelectedHeader()
    {
        if (_headerBringQueued) return;
        _headerBringQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _headerBringQueued = false;
            if (SessionHeaders is not null && _tabs.SelectedItem is TabItem selected) selected.BringIntoView();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public ComparisonPane AddSession(string[]? arguments = null)
    {
        if (_sessions.Count >= WorkspaceStore.MaxEntries) throw new InvalidOperationException("比較タブは256件以下にしてください。");
        var pane = new ComparisonPane(this);
        AttachProjectSession(pane); _tabs.SelectedItem = _sessions[^1];
        if (arguments is { Length: >= 2 })
        {
            pane.LeftPath.Text = arguments[0];
            pane.RightPath.Text = arguments[^1];
            if (arguments.Length == 3) pane.BasePath.Text = arguments[1];
            Opened += async (_, _) => await pane.ComparePathsAsync();
        }
        return pane;
    }
    private Task AddSessionFromUiAsync()
    {
        if (_sessions.Count >= WorkspaceStore.MaxEntries) return Dialogs.MessageAsync(this, "比較タブの上限", "比較タブは256件以下にしてください。");
        AddSession(); return Task.CompletedTask;
    }
    internal void UpdateImageDisplayVisibility()
    {
        foreach (var tab in _sessions)
            ((ComparisonPane)tab.Content!).UpdateImageDisplayVisibility(IsVisible && ReferenceEquals(_tabs.SelectedItem, tab));
    }
}

public sealed partial class ComparisonPane : UserControl, IDisposable
{
    private readonly Window _owner;
    public TextBox LeftPath { get; } = new() { PlaceholderText = "左のファイル / フォルダー" };
    public TextBox BasePath { get; } = new() { PlaceholderText = "共通の祖先 / 中央画像・バイナリ（3方向比較）" };
    public TextBox RightPath { get; } = new() { PlaceholderText = "右のファイル / フォルダー" };
    public TextBox LeftEditor { get; } = Editor();
    public TextBox RightEditor { get; } = Editor();
    public TextBox ResultEditor { get; } = Editor();
    public Button CompareButton { get; } = new() { Content = "比較", MinWidth = 90 };
    public Button CopyRightButton { get; } = new() { Content = "選択差分 →" };
    public ListBox DiffList { get; } = new() { SelectionMode = SelectionMode.Single };
    public DiffResult? CurrentDiff { get; private set; }
    public bool HasUnsavedChanges => LeftEditor.Text != _savedLeft || RightEditor.Text != _savedRight || ResultEditor.Text != _savedResult || SpecializedViews.HasUnsavedChanges(_specialTab.Content as Control);
    private readonly CheckBox _ignoreCase = new() { Content = "大文字小文字を無視" };
    private readonly CheckBox _ignoreSpace = new() { Content = "空白を無視" };
    private readonly CheckBox _ignoreBlank = new() { Content = "空行を無視" };
    private readonly TextBox _ignoreRegex = new() { PlaceholderText = "除外行の正規表現", Width = 210 };
    private readonly TextBox _find = new() { PlaceholderText = "検索", Width = 180 };
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "自動", "テキスト", "フォルダー", "バイナリ", "画像", "JSON", "CSV / TSV", "アーカイブ", "拡張形式" }, SelectedIndex = 0, Width = 130 };
    private readonly ComparisonProviderRegistry _providers = BuiltinComparisonProviders.CreateDefault();
    private readonly ComboBox _provider = new() { Width = 130, Margin = new Thickness(4) };
    private readonly TextBox _fileFilter = new() { PlaceholderText = "ファイルフィルター (.flt)", Width = 220 };
    private readonly TextBox _externalFormat = new() { PlaceholderText = "外部ツール形式", Text = "text", Width = 120 };
    private readonly CheckBox _recursive = new() { Content = "サブフォルダー", IsChecked = true };
    private readonly TextBox _excludes = new() { PlaceholderText = "除外パス（;区切り）", Width = 180 };
    private readonly ComboBox _folderMode = new() { ItemsSource = new[] { "内容", "SHA-256", "日時とサイズ" }, SelectedIndex = 0, Width = 140 };
    private readonly TextBlock _status = new() { Text = "パスを選択するか、編集タブにテキストを貼り付けて比較できます。", Margin = new Thickness(12, 8) };
    private readonly TextBlock _leftCaption = new() { Text = "左", Margin = new Thickness(16, 8), FontWeight = FontWeight.Bold };
    private readonly TextBlock _rightCaption = new() { Text = "右", Margin = new Thickness(16, 8), FontWeight = FontWeight.Bold };
    private readonly TabControl _views = new();
    private readonly ScrollViewer _comparisonToolbar;
    private readonly TabItem _diffTab;
    private readonly TabItem _specialTab = new() { Header = "形式別ビュー" };
    private readonly TabItem _resultTab;
    private readonly Grid _editGrid = new();
    private readonly TextBox _ancestorEditor = Editor();
    private readonly TextBox _resultPreview = Editor();
    private readonly ListBox _directoryList = new();
    private TextDocument? _leftDocument, _rightDocument;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _reportOperation;
    private string? _baseText;
    private string _savedLeft = "", _savedRight = "", _savedResult = "";
    private string? _directoryLeft, _directoryRight;
    private int _diffIndex = -1;
    private bool _textSaveAllowed = true;
    private readonly EventHandler _ownerClosedHandler;
    private bool _disposed;
    internal string? ComparisonStatus => _status.Text;

    // 実GUI自己検証で、完成した変換結果の採用直前に中止操作を再現する。
    internal Action<ProviderResult>? ProviderResultReadyForAdoption { get; set; }
    internal Action<ArchivePanel>? ArchiveReadyForAdoption { get; set; }
    internal Action<SpecializedViews.ImagePanel>? ImageReadyForAdoption { get; set; }
    internal Action? DirectoryReadyForAdoption { get; set; }
    internal Action? TableReadyForAdoption { get; set; }
    // 採用を拒否したとき、前の本文・文書・保存点を実GUIから照合する。
    internal object CaptureAdoptionState() => (_leftDocument, _rightDocument, _baseDocument, CurrentDiff, CurrentMergeSession,
        _baseText, _textSaveAllowed, _lastPackageComparison, LeftEditor.Text, RightEditor.Text, ResultEditor.Text,
        LeftEditor.IsReadOnly, RightEditor.IsReadOnly, _savedLeft, _savedRight, _savedResult);
    internal Action<ArchiveRetryDialog>? ArchiveRetryShown { get; set; }
    // 実GUI自己検証で、有効な比較操作の読込み開始時に中止ボタンを押す。
    internal Action? ArchiveReadStarting { get; set; }

    public ComparisonPane(Window owner) : this(owner, (owner as MainWindow)?.ArchiveTexts ?? new()) { }

    internal ComparisonPane(Window owner, ArchiveWorkingStore workingTexts)
    {
        _workingTexts = workingTexts;
        _owner = owner;
        var root = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel { Spacing = 8 };
        var paths = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 8 };
        paths.Children.Add(PathPicker(LeftPath));
        var ancestor = PathPicker(BasePath); Grid.SetColumn(ancestor, 1); paths.Children.Add(ancestor);
        var right = PathPicker(RightPath); Grid.SetColumn(right, 2); paths.Children.Add(right);
        top.Children.Add(paths);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_mode);
        _provider.ItemsSource = _providers.Providers.Select(x => x.Id).ToArray(); _provider.SelectedIndex = 0;
        actions.Children.Add(_provider);
        actions.Children.Add(CompareButton);
        AddAction(actions, "再比較", RefreshEditorsAsync);
        AddAction(actions, "← 選択差分", () => CopySelectionAsync(false));
        actions.Children.Add(CopyRightButton);
        CopyRightButton.Click += async (_, _) => await GuardAsync(() => CopySelectionAsync(true));
        AddAction(actions, "前の差分", () => { NavigateDifference(-1); return Task.CompletedTask; });
        AddAction(actions, "次の差分", () => { NavigateDifference(1); return Task.CompletedTask; });
        AddAction(actions, "左を保存", () => SaveAsync(false));
        AddAction(actions, "右を保存", () => SaveAsync(true));
        AddAction(actions, "左を外部保存", () => SaveTextAsAsync(false));
        AddAction(actions, "右を外部保存", () => SaveTextAsAsync(true));
        AddAction(actions, "パッチ出力", ExportPatchAsync);
        AddAction(actions, "自動マージ", MergeThreeWayAsync);
        AddAction(actions, "マージ開始", () => RestartMergeAsync(false));
        AddAction(actions, "アーカイブ作成", CreateArchiveAsync);
        AddAction(actions, "結果を保存", SaveResultAsync);
        AddAction(actions, "次の競合", () => { NavigateConflict(); return Task.CompletedTask; });
        AddAction(actions, "中止", () => { _operation?.Cancel(); _reportOperation?.Cancel(); _textSaveOperation?.Cancel(); (_specialTab.Content as ArchivePanel)?.CancelOperation(); return Task.CompletedTask; });
        top.Children.Add(actions);
        var projectActions = new WrapPanel();
        AddAction(projectActions, "プロジェクトを開く", OpenProjectAsync);
        AddAction(projectActions, "プロジェクトを保存", SaveProjectAsync);
        AddAction(projectActions, "比較の設定…", EditProjectOptionsAsync);
        AddAction(projectActions, "HTMLレポート", ExportReportAsync);
        AddAction(projectActions, "外部ツールを追加", AddExternalProviderAsync);
        projectActions.Children.Add(_externalFormat);
        foreach (var item in new Control[] { _recursive, _folderMode, _excludes, _fileFilter }) { item.Margin = new Thickness(8, 4); projectActions.Children.Add(item); }
        top.Children.Add(projectActions);
        var options = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in new Control[] { _ignoreCase, _ignoreSpace, _ignoreBlank, _ignoreRegex, _find }) { control.Margin = new Thickness(8, 4); options.Children.Add(control); }
        AddAction(options, "検索", () => { Find(); return Task.CompletedTask; });
        top.Children.Add(options);
        top.Children.Add(CreateAdvancedFilters());
        top.Children.Add(CreateMergeToolbar());
        var toolbar = new ScrollViewer { Content = top, MaxHeight = 400,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        // 設定・操作欄が折り返しても、比較本文に領域を残す。
        _comparisonToolbar = toolbar;
        SizeChanged += (_, _) => UpdateComparisonToolbarHeight();
        _views.SelectionChanged += (_, _) => UpdateComparisonToolbarHeight();
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        DiffList.ItemTemplate = new FuncDataTemplate<DiffRow>((row, _) => BuildRow(row), false);
        var diffRoot = new DockPanel();
        var captions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        captions.Children.Add(_leftCaption);
        Grid.SetColumn(_rightCaption, 1); captions.Children.Add(_rightCaption);
        DockPanel.SetDock(captions, Dock.Top); diffRoot.Children.Add(captions); diffRoot.Children.Add(DiffList);
        _diffTab = new TabItem { Header = "差分", Content = diffRoot };
        _ancestorEditor.IsReadOnly = true;
        ResultEditor.TextChanged += (_, _) => { if (_resultPreview.Text != ResultEditor.Text) _resultPreview.Text = ResultEditor.Text; };
        _resultPreview.TextChanged += (_, _) => { if (ResultEditor.Text != _resultPreview.Text) ResultEditor.Text = _resultPreview.Text; };
        LeftEditor.TextChanged += (_, _) => RefreshArchiveDraftCaptions();
        RightEditor.TextChanged += (_, _) => RefreshArchiveDraftCaptions();
        UpdateEditorLayout(false);
        _resultTab = new TabItem { Header = "マージ結果", Content = CreateMergeResultView() };
        _views.ItemsSource = new[] { _diffTab, new TabItem { Header = "編集 / 4ペイン", Content = _editGrid }, _resultTab, _specialTab };
        _views.SelectedItem = _diffTab;
        _views.SelectionChanged += (_, _) => (_owner as MainWindow)?.UpdateImageDisplayVisibility();
        root.Children.Add(_views);
        Content = root;
        CompareButton.Click += async (_, _) => await GuardAsync(ComparePathsAsync);
        _directoryList.DoubleTapped += async (_, _) => await GuardAsync(async () =>
        {
            if (_directoryList.SelectedItem is not DirectoryEntry entry || _directoryLeft is null || _directoryRight is null) return;
            var l = Path.Combine(_directoryLeft, entry.RelativePath); var r = Path.Combine(_directoryRight, entry.RelativePath);
            if (File.Exists(l) && File.Exists(r)) { LeftPath.Text = l; RightPath.Text = r; _mode.SelectedIndex = 0; await ComparePathsAsync(); }
        });
        _ownerClosedHandler = (_, _) => Dispose();
        _owner.Closed += _ownerClosedHandler;
    }

    private static TextBox Editor() => new() { Text = "", AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Menlo, Consolas, monospace"), FontSize = 14, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private Control PathPicker(TextBox box)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        row.Children.Add(box);
        var file = new Button { Content = "…", Padding = new Thickness(8) };
        file.Click += async (_, _) => await GuardAsync(async () =>
        {
            var selected = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
            if (selected.Count > 0) box.Text = selected[0].TryGetLocalPath();
        });
        Grid.SetColumn(file, 1); row.Children.Add(file);
        var folder = new Button { Content = "▣", Padding = new Thickness(8) };
        folder.Click += async (_, _) => await GuardAsync(async () =>
        {
            var selected = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            if (selected.Count > 0) box.Text = selected[0].TryGetLocalPath();
        });
        Grid.SetColumn(folder, 2); row.Children.Add(folder);
        return row;
    }

    private void AddAction(Panel panel, string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(3), Padding = new Thickness(9, 6) };
        button.Click += async (_, _) => await GuardAsync(action);
        panel.Children.Add(button);
    }
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { _status.Text = "比較を中止しました。"; }
        catch (Exception ex) { _status.Text = ex.Message; await Dialogs.MessageAsync(_owner, "操作を完了できませんでした", ex.Message); }
    }
    private ComparisonOptions Options() => new() { IgnoreCase = _ignoreCase.IsChecked == true, IgnoreWhitespace = _ignoreSpace.IsChecked == true, IgnoreBlankLines = _ignoreBlank.IsChecked == true, IgnoreNumbers = _ignoreNumbers.IsChecked == true, CommentSyntax = (CommentSyntax)_comments.SelectedIndex, Whitespace = (WhitespaceMode)_whitespace.SelectedIndex, SubstitutionRules = _substitutions, IgnoreLinePattern = string.IsNullOrWhiteSpace(_ignoreRegex.Text) ? null : _ignoreRegex.Text };

    public async Task ComparePathsAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ProjectInputs.HasArchives(CaptureProject()))
        {
            if (HasUnsavedChanges && !await Dialogs.ConfirmAsync(_owner, "未保存の変更", "編集内容を破棄して内包項目を開き直しますか？")) return;
            await CompareArchiveProjectAsync(); return;
        }
        (_specialTab.Content as SpecializedViews.ImagePanel)?.EnsureNotSaving();
        if (HasUnsavedChanges && !await Dialogs.ConfirmAsync(_owner, "未保存の変更", "編集内容を破棄してファイルを開き直しますか？")) return;
        (_specialTab.Content as SpecializedViews.ImagePanel)?.EnsureNotSaving();
        InvalidateTextSave();
        _operation?.Cancel(); _operation?.Dispose(); _operation = new CancellationTokenSource();
        (_specialTab.Content as ArchivePanel)?.CancelOperation();
        var operation = _operation; var token = operation.Token;
        var left = LeftPath.Text ?? ""; var right = RightPath.Text ?? "";
        var imageSettings = CaptureImageSettings();
        var comparisonForPackaging = (left, BasePath.Text ?? "", right, _mode.SelectedIndex, _provider.SelectedItem as string);
        var previousBinary = _specialTab.Content as SpecializedViews.BinaryPanel;
        var previousBinaryStamp = previousBinary?.StateStamp;
        bool CanAdopt()
        {
            var valid = !token.IsCancellationRequested && !_disposed && ReferenceEquals(_operation, operation)
                && previousBinary?.StateStamp == previousBinaryStamp
                && comparisonForPackaging == (LeftPath.Text ?? "", BasePath.Text ?? "", RightPath.Text ?? "", _mode.SelectedIndex, _provider.SelectedItem as string);
            if (!valid && !_disposed && ReferenceEquals(_operation, operation))
                _status.Text = token.IsCancellationRequested ? "比較を中止しました。" : "比較中に入力が変更されたため、前の比較を保持しました。";
            return valid;
        }
        void PrepareAdoption()
        {
            ResetMergeSession(); _lastPackageComparison = null;
            _textSaveAllowed = false; _baseText = null; CurrentDiff = null;
        }
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
        {
            var editorLeft = LeftEditor.Text ?? ""; var editorRight = RightEditor.Text ?? ""; var editorOptions = Options();
            var editorDiff = await Task.Run(() => TextDiffer.Compare(editorLeft, editorRight, editorOptions, token), token);
            if (CanAdopt() && editorLeft == LeftEditor.Text && editorRight == RightEditor.Text) { ResetMergeSession(); _lastPackageComparison = null; ApplyDiff(editorDiff); }
            return;
        }
        _status.Text = "比較しています…";
        CompareButton.IsEnabled = false;
        try
        {
            var mode = _mode.SelectedIndex;
            if (mode == 8)
            {
                var provider = _providers.Get((string)_provider.SelectedItem!);
                var result = await provider.CompareAsync(new ComparisonRequest(left, right, provider.Formats[0]), token);
                ProviderResultReadyForAdoption?.Invoke(result);
                var providerOptions = Options();
                var providerDiff = await Task.Run(() => TextDiffer.Compare(result.LeftText, result.RightText, providerOptions, token), token);
                token.ThrowIfCancellationRequested();
                if (!CanAdopt()) return;
                PrepareAdoption();
                LeftEditor.Text = _savedLeft = result.LeftText; RightEditor.Text = _savedRight = result.RightText;
                LeftEditor.IsReadOnly = RightEditor.IsReadOnly = true;
                SetSpecialView(null); ApplyDiff(providerDiff);
                _status.Text = result.Summary + " · 変換後の内容です。元のファイルへのテキスト保存はできません。";
                _lastPackageComparison = comparisonForPackaging;
                return;
            }
            if (mode == 2 || (Directory.Exists(left) && Directory.Exists(right)))
            {
                if (await CompareDirectoryAsync(left, right, token, CanAdopt, PrepareAdoption)) _lastPackageComparison = comparisonForPackaging;
                return;
            }
            if (mode == 4 || (mode == 0 && SpecializedViews.IsImage(left) && SpecializedViews.IsImage(right)))
            {
                Control? imageView = await SpecializedViews.ImagesWithOptionsAsync(left, right, token, comparisonForPackaging.Item2, imageSettings,
                    (_owner as MainWindow)?.ImageOptions);
                try
                {
                    ImageReadyForAdoption?.Invoke((SpecializedViews.ImagePanel)imageView);
                    token.ThrowIfCancellationRequested();
                    if (!CanAdopt()) return;
                    ConfigureImageEditing((SpecializedViews.ImagePanel)imageView);
                    PrepareAdoption();
                    SetSpecialView(imageView); imageView = null;
                    _savedLeft = LeftEditor.Text ?? ""; _savedRight = RightEditor.Text ?? ""; _savedResult = ResultEditor.Text ?? "";
                    LeftEditor.IsReadOnly = RightEditor.IsReadOnly = true;
                    _views.SelectedItem = _specialTab; _status.Text = "画像を比較しました。"; _lastPackageComparison = comparisonForPackaging; return;
                }
                finally { SpecializedViews.Release(imageView); }
            }
            if (mode == 3)
            {
                var candidate = await SpecializedViews.BinaryAsync(left, right, token, _projectMetadata.LeftReadOnly, _projectMetadata.RightReadOnly, EnsureProjectOutputWritable, comparisonForPackaging.Item2, _projectMetadata.BaseReadOnly);
                if (!CanAdopt())
                { SpecializedViews.Release(candidate); return; }
                PrepareAdoption();
                SetSpecialView(candidate); _workingDocumentStale = false;
                _views.SelectedItem = _specialTab; _status.Text = "バイナリを比較しました。"; _lastPackageComparison = comparisonForPackaging; return;
            }
            if (mode == 7 || (mode == 0 && ArchivePanel.Supports(left) && ArchivePanel.Supports(right)))
            {
                ArchivePanel? candidate = null;
                string? leftPassword = null, rightPassword = null;
                try
                {
                    while (candidate is null)
                    {
                        ArchiveReadStarting?.Invoke();
                        token.ThrowIfCancellationRequested();
                        try { candidate = await ArchivePanel.CreateWithPasswordsAsync(left, right, token, EnsureArchiveOutputWritable, leftPassword, rightPassword); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception exception) when (exception is not OutOfMemoryException)
                        {
                            token.ThrowIfCancellationRequested();
                            if (operation != _operation) return;
                            var dialog = new ArchiveRetryDialog(leftPassword, rightPassword);
                            try
                            {
                                var retryTask = dialog.ShowDialog<ArchiveRetryResult?>(_owner);
                                ArchiveRetryShown?.Invoke(dialog);
                                var retry = await retryTask;
                                token.ThrowIfCancellationRequested();
                                if (operation != _operation) return;
                                if (retry is null) throw new OperationCanceledException("アーカイブ操作を中止しました。", token);
                                leftPassword = retry.LeftPassword; rightPassword = retry.RightPassword;
                            }
                            finally { dialog.LeftPassword.Text = dialog.RightPassword.Text = ""; dialog.Close(); }
                        }
                    }
                    ArchiveReadyForAdoption?.Invoke(candidate);
                    token.ThrowIfCancellationRequested();
                    if (!CanAdopt()) return;
                    PrepareAdoption();
                    BindArchivePanel(candidate);
                    SetSpecialView(candidate); candidate = null;
                }
                finally { candidate?.Dispose(); leftPassword = rightPassword = null; }
                _views.SelectedItem = _specialTab; _status.Text = "アーカイブビューを開きました。"; _lastPackageComparison = comparisonForPackaging; return;
            }
            var leftDocument = await TextDocument.LoadAsync(left, token);
            var rightDocument = await TextDocument.LoadAsync(right, token);
            var baseDocument = !string.IsNullOrWhiteSpace(comparisonForPackaging.Item2) ? await TextDocument.LoadAsync(comparisonForPackaging.Item2, token) : null;
            var textOptions = Options();
            var textDiff = mode is 5 or 6 ? null : await Task.Run(() => TextDiffer.Compare(leftDocument.Text, rightDocument.Text, textOptions, token), token);
            var tableCandidate = mode == 6 ? await CreateTableViewAsync(leftDocument, rightDocument, baseDocument, token) : default;
            token.ThrowIfCancellationRequested();
            if (!CanAdopt()) return;
            PrepareAdoption();
            _leftDocument = leftDocument; _rightDocument = rightDocument; _baseDocument = baseDocument;
            LeftEditor.IsReadOnly = _projectMetadata.LeftReadOnly;
            RightEditor.IsReadOnly = _projectMetadata.RightReadOnly;
            LeftEditor.Text = _savedLeft = _leftDocument.Text;
            RightEditor.Text = _savedRight = _rightDocument.Text;
            _baseText = _baseDocument?.Text;
            _ancestorEditor.Text = _baseText ?? "";
            UpdateEditorLayout(_baseText is not null);
            _textSaveAllowed = true;
            if (mode == 6) { _tableSyntax = tableCandidate.Syntax; SetSpecialView(tableCandidate.View); _views.SelectedItem = _specialTab; }
            else if (mode == 5) { SetSpecialView(SpecializedViews.StructuredJson(LeftEditor.Text, RightEditor.Text)); _views.SelectedItem = _specialTab; }
            else
            {
                // Text採用後に旧形式の保存・通知へdispatchしない。読込み失敗中は旧ownerを保持する。
                SetSpecialView(null); _workingDocumentStale = false; ApplyDiff(textDiff!);
            }
            if (mode is 5 or 6) _status.Text = mode == 5 ? "JSONの構造を比較しました。" : "表の区切り・引用符設定で比較しました。";
            _lastPackageComparison = comparisonForPackaging;
        }
        catch (OperationCanceledException) when (operation != _operation) { }
        catch (OperationCanceledException) { if (!_disposed && ReferenceEquals(operation, _operation)) _status.Text = "比較を中止しました。"; throw; }
        finally { if (!_disposed && ReferenceEquals(operation, _operation)) CompareButton.IsEnabled = true; }
    }

    public void CompareEditors()
    {
        ApplyDiff(TextDiffer.Compare(LeftEditor.Text ?? "", RightEditor.Text ?? "", Options()));
    }
    private async Task CompareEditorsAsync()
    {
        var left = LeftEditor.Text ?? ""; var right = RightEditor.Text ?? ""; var options = Options();
        var token = _operation?.Token ?? CancellationToken.None;
        var diff = await Task.Run(() => TextDiffer.Compare(left, right, options, token), token);
        if (left == LeftEditor.Text && right == RightEditor.Text) ApplyDiff(diff);
    }
    private Task RefreshEditorsAsync()
    {
        if (ProjectInputs.HasArchives(_projectMetadata) || _specialTab.Content is ArchivePanel) return ComparePathsAsync();
        _operation?.Cancel(); _operation?.Dispose(); _operation = new CancellationTokenSource();
        if (_mode.SelectedIndex == 6 && _specialTab.Content is TablePanel table) return table.RefreshAsync();
        return CompareEditorsAsync();
    }
    private void ApplyDiff(DiffResult diff)
    {
        CurrentDiff = diff;
        DiffList.ItemsSource = CurrentDiff.Rows;
        _diffIndex = -1;
        _status.Text = $"{CurrentDiff.Blocks.Count} 個の差分  ·  左 {_leftDocument?.EncodingName ?? "UTF-8"}  /  右 {_rightDocument?.EncodingName ?? "UTF-8"}";
        if (diff.InlineFallbackCount > 0) _status.Text += $"  ·  詳細比較を省略した {diff.InlineFallbackCount} 行は行全体を強調";
        if (diff.LineFallback) _status.Text += "  ·  行対応の処理上限に達したため、未確定区間をまとめて表示";
        _views.SelectedItem = _diffTab;
    }

    public void NavigateDifference(int direction)
    {
        if (_specialTab.Content is SpecializedViews.ImagePanel image) { _ = image.NavigateRegionAsync(direction); return; }
        if (CurrentDiff is not { Blocks.Count: > 0 }) return;
        _diffIndex = (_diffIndex + direction + CurrentDiff.Blocks.Count) % CurrentDiff.Blocks.Count;
        DiffList.SelectedIndex = CurrentDiff.Blocks[_diffIndex].RowStart;
        DiffList.ScrollIntoView(DiffList.SelectedItem!);
    }

    public void CopySelected(bool leftToRight)
    {
        EnsureSideWritable(leftToRight);
        if (CurrentDiff is null) return;
        // 編集後の古い差分座標で上書きしない。
        if (CurrentDiff.LeftText != LeftEditor.Text || CurrentDiff.RightText != RightEditor.Text) CompareEditors();
        var block = CurrentDiff!.Blocks.FirstOrDefault(x => DiffList.SelectedIndex >= x.RowStart && DiffList.SelectedIndex < x.RowStart + x.RowCount)
            ?? (_diffIndex >= 0 && _diffIndex < CurrentDiff.Blocks.Count ? CurrentDiff.Blocks[_diffIndex] : null);
        if (block is null) { _status.Text = "差分の行を選択してください。"; return; }
        if (leftToRight) RightEditor.Text = TextMerger.CopyLeftToRight(CurrentDiff, block);
        else LeftEditor.Text = TextMerger.CopyRightToLeft(CurrentDiff, block);
        CompareEditors();
    }

    public void DiscardChanges() { _savedLeft = LeftEditor.Text ?? ""; _savedRight = RightEditor.Text ?? ""; _savedResult = ResultEditor.Text ?? ""; SpecializedViews.DiscardChanges(_specialTab.Content as Control); }
    public void SelectMode(int mode) => _mode.SelectedIndex = mode;
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        InvalidateTextSave();
        _owner.Closed -= _ownerClosedHandler;
        _operation?.Cancel(); _operation?.Dispose();
        _reportOperation?.Cancel();
        ClearArchivePasswords();
        SpecializedViews.Release(_specialTab.Content as Control);
    }
    internal void UpdateImageDisplayVisibility(bool active)
    {
        if (_specialTab.Content is SpecializedViews.ImagePanel image)
            image.SetDisplayActive(active && ReferenceEquals(_views.SelectedItem, _specialTab));
    }
    private void SetSpecialView(Control? view)
    {
        SpecializedViews.Release(_specialTab.Content as Control);
        _specialTab.Content = view;
        if (view is SpecializedViews.BinaryPanel binary) BindBinaryPanel(binary);
        (_owner as MainWindow)?.UpdateImageDisplayVisibility();
        UpdateComparisonToolbarHeight();
    }
    private void UpdateComparisonToolbarHeight()
    {
        // 画像・バイナリ・アーカイブには独自の操作欄があるため、共通設定をスクロールして比較本文の領域を残す。
        var fraction = _views.SelectedItem == _specialTab && _specialTab.Content is SpecializedViews.ImagePanel or SpecializedViews.BinaryPanel or ArchivePanel ? .2 : .5;
        _comparisonToolbar.MaxHeight = Bounds.Height > 0 ? Math.Clamp(Bounds.Height * fraction, 80, 400) : 400;
    }
    private async Task CopySelectionAsync(bool toRight)
    {
        EnsureSideWritable(toRight);
        if (_views.SelectedItem == _specialTab && _specialTab.Content == _directoryList && _directoryList.SelectedItem is DirectoryEntry entry && _directoryLeft is not null && _directoryRight is not null)
        {
            var left = _directoryLeft; var right = _directoryRight; var operation = _operation;
            var inputs = (LeftPath.Text, BasePath.Text, RightPath.Text, _mode.SelectedIndex, _provider.SelectedItem);
            var binary = _specialTab.Content as SpecializedViews.BinaryPanel; var stamp = binary?.StateStamp;
            bool CanAdopt() => !_disposed && ReferenceEquals(_operation, operation) && operation?.IsCancellationRequested != true
                && ReferenceEquals(_specialTab.Content, _directoryList) && binary?.StateStamp == stamp
                && inputs == (LeftPath.Text, BasePath.Text, RightPath.Text, _mode.SelectedIndex, _provider.SelectedItem);
            if (!await Dialogs.ConfirmAsync(_owner, "フォルダー内のコピー", $"{entry.RelativePath} を{(toRight ? "右" : "左")}へコピーします。既存ファイルは上書きされます。")) return;
            if (!CanAdopt()) return;
            await FolderOperations.CopyAsync(toRight ? left : right, toRight ? right : left, entry.RelativePath);
            await CompareDirectoryAsync(left, right, operation?.Token ?? CancellationToken.None, CanAdopt, () => { });
        }
        else CopySelected(toRight);
    }
    private async Task SaveProjectAsync()
    {
        var path = await SavePathAsync("比較プロジェクトを保存", "comparison.diffbeacon.json");
        if (path is null) return;
        if (_owner is MainWindow window) await window.SaveWorkspaceAsync(path);
        else { EnsureArchiveDraftSaved(); await WorkspaceStore.SaveAsync(path, CaptureProject()); }
        _status.Text = "すべての比較タブをプロジェクトへ保存しました。編集本文は元ファイルへ別途保存してください。";
    }
    private async Task OpenProjectAsync()
    {
        var paths = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "比較プロジェクトを開く" });
        if (paths.Count == 0 || paths[0].TryGetLocalPath() is not string path) return;
        if (_owner is MainWindow window) await window.OpenWorkspaceAsync(path);
        else
        {
            var project = await WorkspaceStore.LoadAsync(path);
            if (HasUnsavedChanges && !await Dialogs.ConfirmAsync(_owner, "未保存の変更", "編集内容を破棄してプロジェクトを開きますか？")) return;
            ApplyProject(project); DiscardChanges(); await CompareProjectAsync();
        }
    }
    private async Task AddExternalProviderAsync()
    {
        var selected = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "実行する比較ツールを選択" });
        if (selected.Count == 0 || selected[0].TryGetLocalPath() is not string executable) return;
        var id = "external-" + _providers.Providers.Count();
        var format = string.IsNullOrWhiteSpace(_externalFormat.Text) ? "text" : _externalFormat.Text;
        _providers.Register(new ExecutableComparisonProvider(id, executable, [format]));
        _provider.ItemsSource = _providers.Providers.Select(x => x.Id).ToArray(); _provider.SelectedItem = id;
        _mode.SelectedIndex = 8;
        _status.Text = $"{Path.GetFileName(executable)} を選択しました。比較操作で実行します。";
    }
    private async Task ExportReportAsync()
    {
        var path = await SavePathAsync("HTMLレポートを保存", "comparison.html");
        if (path is not null) await SaveReportAsync(path);
    }

    public async Task SaveReportAsync(string path, CancellationToken token = default)
    {
        EnsureNoPendingTableEdit();
        if (_reportOperation is not null) throw new InvalidOperationException("HTMLレポートを生成しています。");
        var project = CaptureTextReportProject();
        if (ProjectInputs.HasArchives(project) || !string.IsNullOrWhiteSpace(project.LeftPath) || !string.IsNullOrWhiteSpace(project.RightPath)) EnsureComparedForPackaging();
        var workspaceWindow = _owner as MainWindow;
        var panes = workspaceWindow?.SessionPanes ?? [this];
        var sourceProject = workspaceWindow?.WorkspaceSourcePath;
        foreach (var pane in panes) pane.EnsureProjectOutputWritable(path);
        var protectedEntries = panes.Select(pane => pane.CaptureProject()).ToArray();
        var left = LeftEditor.Text ?? ""; var right = RightEditor.Text ?? ""; var ancestor = _baseText;
        ImageComparisonEngine.ReportInput? imageInput = null;
        if (ProjectReport.IsImage(project))
        {
            imageInput = (_specialTab.Content as SpecializedViews.ImagePanel
                ?? throw new InvalidOperationException("画像を比較してからレポートを生成してください。")).CaptureReport();
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _reportOperation = operation;
        _status.Text = "HTMLレポートを生成しています…（中止できます）";
        try
        {
            var cancellation = operation.Token;
            var html = await Task.Run(() => imageInput is { } image
                ? ImageReport.Create(image, string.IsNullOrWhiteSpace(project.BasePath)
                    ? [project.LeftDescription ?? project.LeftPath, project.RightDescription ?? project.RightPath]
                    : [project.LeftDescription ?? project.LeftPath, project.BaseDescription ?? project.BasePath, project.RightDescription ?? project.RightPath], cancellation)
                : project.Mode is "Provider" or "Web"
                ? HtmlReport.CreateText([new((project.LeftDescription ?? project.LeftPath) + "（変換後のテキスト）", left),
                    new((project.RightDescription ?? project.RightPath) + "（変換後のテキスト）", right)], ProjectReport.Options(project), ProjectReport.MaximumBytes, cancellation)
                : ProjectReport.Create(project, left, ancestor, right, cancellation), cancellation);
            // 生成開始時の入力と、生成中に開いた・保存した現行プロジェクトの両方を保護する。
            var currentPanes = workspaceWindow?.SessionPanes ?? [this];
            var currentSource = new ComparisonProject { LeftPath = workspaceWindow?.WorkspaceSourcePath ?? "" };
            await ProjectReport.SaveAsync(path, html, protectedEntries.Concat(currentPanes.Select(pane => pane.CaptureProject())).Append(currentSource),
                sourceProject, cancellation);
            _status.Text = "HTMLレポートを保存しました。";
        }
        finally { _reportOperation = null; }
    }
    private void Find()
    {
        if (CurrentDiff is null || string.IsNullOrEmpty(_find.Text)) return;
        var rows = CurrentDiff.Rows;
        for (var n = 1; n <= rows.Count; n++)
        {
            var i = (Math.Max(-1, DiffList.SelectedIndex) + n) % rows.Count;
            if ((rows[i].LeftText?.Contains(_find.Text, StringComparison.OrdinalIgnoreCase) ?? false) || (rows[i].RightText?.Contains(_find.Text, StringComparison.OrdinalIgnoreCase) ?? false))
            { DiffList.SelectedIndex = i; DiffList.ScrollIntoView(DiffList.SelectedItem!); return; }
        }
        _status.Text = "検索語が見つかりませんでした。";
    }

    public Task SaveAsync(bool right) => _specialTab.Content is SpecializedViews.BinaryPanel binary ? binary.SaveAsync(right) : SaveTextCoreAsync(right, null, CancellationToken.None);
    private async Task<string?> SavePathAsync(string title, string suggested) => (await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggested, ShowOverwritePrompt = true }))?.TryGetLocalPath();
    private async Task ExportPatchAsync()
    {
        var path = await SavePathAsync("Unifiedパッチを保存", "changes.patch");
        if (path is null) return;
        EnsureProjectOutputWritable(path);
        await File.WriteAllTextAsync(path, UnifiedPatch.Create(LeftEditor.Text ?? "", RightEditor.Text ?? "", LeftPath.Text ?? "left", RightPath.Text ?? "right"));
        _status.Text = "パッチを保存しました。";
    }
    private Task MergeThreeWayAsync() => RestartMergeAsync(true);
    private void UpdateEditorLayout(bool fourPanes)
    {
        foreach (var oldPane in _editGrid.Children.OfType<DockPanel>()) oldPane.Children.Clear();
        _editGrid.Children.Clear();
        _editGrid.ColumnDefinitions = new ColumnDefinitions(fourPanes ? "*,6,*,6,*,6,*" : "*,6,*");
        var editors = fourPanes ? new[] { LeftEditor, _ancestorEditor, RightEditor, _resultPreview } : new[] { LeftEditor, RightEditor };
        var labels = fourPanes ? new[] { ProjectCaption(false), _projectMetadata.BaseDescription ?? "共通の祖先", ProjectCaption(true), "マージ結果" } : new[] { ProjectCaption(false), ProjectCaption(true) };
        for (var index = 0; index < editors.Length; index++)
        {
            var pane = new DockPanel();
            var label = new TextBlock { Text = labels[index], Margin = new Thickness(8), FontWeight = FontWeight.Bold };
            DockPanel.SetDock(label, Dock.Top); pane.Children.Add(label); pane.Children.Add(editors[index]);
            Grid.SetColumn(pane, index * 2); _editGrid.Children.Add(pane);
            if (index == editors.Length - 1) continue;
            var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetColumn(splitter, index * 2 + 1); _editGrid.Children.Add(splitter);
        }
    }
    private void NavigateConflict()
    {
        if (_specialTab.Content is SpecializedViews.ImagePanel image)
        {
            _ = image.NavigateRegionAsync(1, conflictsOnly: true); return;
        }
        if (CurrentMergeSession is { } session)
        {
            var pending = session.Sections.Where(range => range.Section.IsPending).ToArray();
            if (pending.Length == 0) { _status.Text = "未解決の差分はありません。"; return; }
            var currentId = (_mergeSections.SelectedItem as SectionChoice)?.Id;
            var current = Array.FindIndex(pending, range => range.Section.Id == currentId);
            var range = pending[(current + 1) % pending.Length];
            _mergeSections.SelectedItem = _mergeSections.Items.OfType<SectionChoice>().Single(item => item.Id == range.Section.Id);
            _views.SelectedItem = _resultTab; SelectMergeRange(range); ResultEditor.Focus(); return;
        }
        var text = ResultEditor.Text ?? "";
        var position = text.IndexOf("<<<<<<<", Math.Min(text.Length, ResultEditor.SelectionEnd + 1), StringComparison.Ordinal);
        if (position < 0) position = text.IndexOf("<<<<<<<", StringComparison.Ordinal);
        if (position < 0) { _status.Text = "競合マーカーはありません。"; return; }
        _views.SelectedItem = _resultTab;
        ResultEditor.SelectionStart = position; ResultEditor.SelectionEnd = position + 7; ResultEditor.CaretIndex = position;
        ResultEditor.Focus();
    }
    private async Task SaveResultAsync()
    {
        var unresolved = CurrentMergeSession?.UnresolvedCount ?? ((ResultEditor.Text ?? "").Contains("<<<<<<<", StringComparison.Ordinal) ? 1 : 0);
        if (unresolved > 0 && !await Dialogs.ConfirmAsync(_owner, "競合が残っています", "未解決の差分を含む結果を保存しますか？")) return;
        var path = await SavePathAsync("マージ結果を保存", "merged.txt");
        if (path is null) return;
        await SaveMergeResultToAsync(path, allowUnresolved: true);
    }

    private async Task<bool> CompareDirectoryAsync(string left, string right, CancellationToken token, Func<bool> canAdopt, Action prepareAdoption)
    {
        var textOptions = Options();
        var filtered = textOptions.IgnoreCase || textOptions.IgnoreWhitespace || textOptions.IgnoreBlankLines || textOptions.IgnoreLinePattern is not null
            || textOptions.IgnoreNumbers || textOptions.CommentSyntax != CommentSyntax.None || textOptions.Whitespace != WhitespaceMode.None || textOptions.SubstitutionRules.Any(rule => rule.Enabled);
        var filter = ResolveProjectFilter();
        var result = await DirectoryComparer.CompareAsync(left, right, new DirectoryComparisonOptions { Recursive = _recursive.IsChecked == true, Mode = (DirectoryComparisonMode)_folderMode.SelectedIndex, ExcludePatterns = (_excludes.Text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), TextOptions = filtered ? textOptions : null, FileFilter = filter }, token);
        DirectoryReadyForAdoption?.Invoke();
        token.ThrowIfCancellationRequested();
        if (!canAdopt()) return false;
        prepareAdoption();
        _directoryLeft = left; _directoryRight = right;
        _directoryList.ItemsSource = result.Entries;
        _directoryList.ItemTemplate = new FuncDataTemplate<DirectoryEntry>((entry, _) => new TextBlock { Text = entry is null ? "" : $"{entry.Status,-14}  {entry.RelativePath}", FontFamily = new FontFamily("Cascadia Mono, Menlo, monospace"), Margin = new Thickness(8) }, false);
        SetSpecialView(_directoryList); _views.SelectedItem = _specialTab;
        _status.Text = $"フォルダー比較: {result.Entries.Count} 件";
        return true;
    }

    private static Control BuildRow(DiffRow? row)
    {
        if (row is null) return new Border();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*,52,*"), MinHeight = 23 };
        var background = row.Kind switch { DiffKind.Added => "#244332", DiffKind.Deleted => "#4B2933", DiffKind.Modified => "#49422A", _ => "#00000000" };
        grid.Background = new SolidColorBrush(Color.Parse(background));
        var values = new[] { row.LeftLineNumber?.ToString() ?? "", row.LeftText ?? "", row.RightLineNumber?.ToString() ?? "", row.RightText ?? "" };
        for (var i = 0; i < values.Length; i++)
        {
            var text = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Menlo, Consolas, monospace"), FontSize = 13, Margin = new Thickness(6, 2), TextTrimming = TextTrimming.CharacterEllipsis };
            if (i % 2 == 0) text.Foreground = Brushes.Gray;
            var spans = i == 1 ? row.LeftSpans : i == 3 ? row.RightSpans : [];
            if (spans.Count == 0) text.Text = values[i];
            else
            {
                var offset = 0;
                foreach (var span in spans)
                {
                    text.Inlines!.Add(new Run(values[i][offset..span.Start]));
                    text.Inlines.Add(new Run(values[i].Substring(span.Start, span.Length)) { Foreground = Brushes.Gold, FontWeight = FontWeight.Bold });
                    offset = span.Start + span.Length;
                }
                text.Inlines!.Add(new Run(values[i][offset..]));
            }
            Grid.SetColumn(text, i); grid.Children.Add(text);
        }
        return grid;
    }
}

// 認証必須と断定せず、候補の検証失敗から明示再試行へ進む。秘密値は保存しない。
internal sealed record ArchiveRetryResult(string? LeftPassword, string? RightPassword);
internal sealed class ArchiveRetryDialog : Window
{
    internal TextBox LeftPassword { get; } = new() { PasswordChar = '●', MaxLength = 4096, PlaceholderText = "左のパスワード（任意）", Margin = new Thickness(0, 4) };
    internal TextBox RightPassword { get; } = new() { PasswordChar = '●', MaxLength = 4096, PlaceholderText = "右のパスワード（任意）", Margin = new Thickness(0, 4) };
    internal Button Retry { get; } = new() { Content = "再試行", Margin = new Thickness(4) };
    internal Button Cancel { get; } = new() { Content = "キャンセル", Margin = new Thickness(4) };
    internal ArchiveRetryDialog(string? leftPassword, string? rightPassword)
    {
        Title = "アーカイブを開けませんでした"; Width = 480; SizeToContent = SizeToContent.Height;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        LeftPassword.Text = leftPassword; RightPassword.Text = rightPassword;
        var panel = new StackPanel { Margin = new Thickness(16) };
        var status = new TextBlock { Text = "読込みを検証できませんでした。パスワード、破損、形式を確認して再試行してください。", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(status); panel.Children.Add(LeftPassword); panel.Children.Add(RightPassword);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Cancel); buttons.Children.Add(Retry); panel.Children.Add(buttons); Content = panel;
        Cancel.Click += (_, _) => Close();
        Retry.Click += (_, _) =>
        {
            if (LeftPassword.Text?.Length > 4096 || RightPassword.Text?.Length > 4096) { status.Text = "パスワードは左右それぞれ4096文字以下で入力してください。"; return; }
            Close(new ArchiveRetryResult(string.IsNullOrEmpty(LeftPassword.Text) ? null : LeftPassword.Text,
                string.IsNullOrEmpty(RightPassword.Text) ? null : RightPassword.Text));
        };
        Closed += (_, _) => LeftPassword.Text = RightPassword.Text = "";
    }
}

internal static class Dialogs
{
    internal static Action<Window>? ConfirmationShown { get; set; }
    public static Task MessageAsync(Window owner, string title, string message) => ShowAsync(owner, title, message, false);
    public static Task<bool> ConfirmAsync(Window owner, string title, string message) => ShowAsync(owner, title, message, true);
    private static Task<bool> ShowAsync(Window owner, string title, string message, bool confirm)
    {
        var dialog = new Window { Title = title, Width = 470, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 20 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        if (confirm) { var cancel = new Button { Content = "キャンセル" }; cancel.Click += (_, _) => dialog.Close(false); buttons.Children.Add(cancel); }
        var ok = new Button { Content = confirm ? "続行" : "閉じる" }; ok.Click += (_, _) => dialog.Close(true); buttons.Children.Add(ok);
        panel.Children.Add(buttons); dialog.Content = panel;
        var result = dialog.ShowDialog<bool>(owner);
        if (confirm) ConfirmationShown?.Invoke(dialog);
        return result;
    }
}
