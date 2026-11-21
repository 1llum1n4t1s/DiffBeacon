using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public sealed partial class MainWindow : Window
{
    /// <summary>起動時に選択ダイアログを出す。自己検証は先に偽にして、入力欄を比較タブに残す。</summary>
    internal static bool PreferSelectDialog { get; set; } = true;
    private readonly TabControl _tabs = new();
    private readonly List<TabItem> _sessions = [];
    private bool _headerBringQueued;
    internal ScrollViewer? SessionHeaders { get; private set; }
    internal ImageApplicationOptionsStore ImageOptions { get; }
    internal FolderApplicationOptionsStore FolderOptions { get; }
    internal TableApplicationOptionsStore TableOptions { get; }

    /// <summary>
    /// フォルダー一覧の列の配置（folder-columns.json）。他の設定と同じくコンストラクターで受け取る。
    /// 最初のタブはコンストラクターの中で作られ、その一覧が作られるときに配置を読むので、後から差し替えると保存した配置が届かない。
    /// 省略したときはメモリだけの設定（headless検証が利用者のAppDataへ書かないため）。
    /// </summary>
    internal FolderColumnOptionsStore FolderColumnOptions { get; }
    private readonly TextMarkerStore _textMarkersStore;
    private readonly List<TextMarker> _textMarkers = [];
    private bool _markersEnabled = true;
    /// <summary>全比較タブで共有する印。本家のメインマーカー。</summary>
    internal IReadOnlyList<TextMarker> TextMarkers => _textMarkers;
    /// <summary>印を塗るか。</summary>
    internal bool MarkersEnabled => _markersEnabled;
    internal long SessionSelectionGeneration { get; private set; }
    internal ArchiveWorkingStore ArchiveTexts { get; private set; } = new();
    internal ArchiveWindowLifetime ArchiveLifetime { get; } = new();
    public ComparisonPane ActivePane => (ComparisonPane)((TabItem)_tabs.SelectedItem!).Content!;

    public MainWindow(string[]? arguments = null) : this(arguments, new ImageApplicationOptionsStore()) { }

    internal MainWindow(string[]? arguments, ImageApplicationOptionsStore imageOptions)
        : this(arguments, imageOptions, new FolderApplicationOptionsStore()) { }

    internal MainWindow(string[]? arguments, ImageApplicationOptionsStore imageOptions, FolderApplicationOptionsStore folderOptions)
        : this(arguments, imageOptions, folderOptions, new TableApplicationOptionsStore()) { }

    internal MainWindow(string[]? arguments, ImageApplicationOptionsStore imageOptions, FolderApplicationOptionsStore folderOptions,
        TableApplicationOptionsStore tableOptions)
        : this(arguments, imageOptions, folderOptions, tableOptions, new FolderColumnOptionsStore()) { }

    internal MainWindow(string[]? arguments, ImageApplicationOptionsStore imageOptions, FolderApplicationOptionsStore folderOptions,
        TableApplicationOptionsStore tableOptions, FolderColumnOptionsStore folderColumnOptions)
        : this(arguments, imageOptions, folderOptions, tableOptions, folderColumnOptions, new TextViewOptionsStore()) { }

    internal MainWindow(string[]? arguments, ImageApplicationOptionsStore imageOptions, FolderApplicationOptionsStore folderOptions,
        TableApplicationOptionsStore tableOptions, FolderColumnOptionsStore folderColumnOptions, TextViewOptionsStore textViewOptionsStore)
    {
        ImageOptions = imageOptions; FolderOptions = folderOptions; TableOptions = tableOptions; FolderColumnOptions = folderColumnOptions;
        // 表示メニューの設定は、最初のタブとメニューのチェックを作る前（BuildFrame・AddSession より前）に採用する。
        // 保存の購読は採用の後にして、読み込んだだけで書き戻さない。
        TextViewOptionsStorage = textViewOptionsStore;
        TextViewOptions.Load(textViewOptionsStore.Current);
        _showToolbar = textViewOptionsStore.Current.ShowToolbar;
        _showStatusBar = textViewOptionsStore.Current.ShowStatusBar;
        _showLocationPane = textViewOptionsStore.Current.ShowLocationPane;
        _showDiffPane = textViewOptionsStore.Current.ShowDiffPane;
        _showTabBar = textViewOptionsStore.Current.ShowTabBar;
        _showMenuBar = textViewOptionsStore.Current.ShowMenuBar;
        _toolbarIconSize = textViewOptionsStore.Current.ToolbarIconSize;
        _darkTheme = textViewOptionsStore.Current.DarkTheme;
        _showOutputPane = textViewOptionsStore.Current.ShowOutputPane;
        _showIdenticalItems = textViewOptionsStore.Current.ShowIdenticalItems;
        _showDifferentItems = textViewOptionsStore.Current.ShowDifferentItems;
        _showUniqueLeftItems = textViewOptionsStore.Current.ShowUniqueLeftItems;
        _showUniqueMiddleItems = textViewOptionsStore.Current.ShowUniqueMiddleItems;
        _showUniqueRightItems = textViewOptionsStore.Current.ShowUniqueRightItems;
        _showSkippedItems = textViewOptionsStore.Current.ShowSkippedItems;
        _tabBarOnTitleBar = textViewOptionsStore.Current.TabBarOnTitleBar;
        _externalEditorCommand = textViewOptionsStore.Current.ExternalEditorCommand;
        if (Application.Current is { } application)
            application.RequestedThemeVariant = _darkTheme ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        TextViewOptions.Changed += SaveViewMenuOptions;
        Closed += (_, _) => TextViewOptions.Changed -= SaveViewMenuOptions;
        Action<string> textViewGuard = path => { foreach (var pane in SessionPanes) pane.EnsureFolderOptionsOutputWritable(path); };
        textViewOptionsStore.AddOutputGuard(textViewGuard);
        Closed += (_, _) => textViewOptionsStore.RemoveOutputGuard(textViewGuard);
        _textMarkersStore = new TextMarkerStore(textViewOptionsStore.Path);
        _textMarkersStore.AddOutputGuard(textViewGuard);
        var loadedMarkers = _textMarkersStore.Load();
        _markersEnabled = loadedMarkers.Enabled;
        _textMarkers.AddRange(loadedMarkers.Markers);
        Action<string> folderGuard = path => { foreach (var pane in SessionPanes) pane.EnsureFolderOptionsOutputWritable(path); };
        Action<string> tableGuard = path => { foreach (var pane in SessionPanes) pane.EnsureTableOptionsOutputWritable(path); };
        FolderOptions.AddOutputGuard(folderGuard);
        TableOptions.AddOutputGuard(tableGuard);
        // 列の配置の保存先も、比較入力・workspaceを上書きしないよう同じ出力保護をかける。
        Action<string> folderColumnGuard = path => { foreach (var pane in SessionPanes) pane.EnsureFolderOptionsOutputWritable(path); };
        FolderColumnOptions.AddOutputGuard(folderColumnGuard);
        Closed += (_, _) => FolderColumnOptions.RemoveOutputGuard(folderColumnGuard);
        TableOptions.Changed += OnTableApplicationOptionsChanged;
        Closed += (_, _) => { FolderOptions.RemoveOutputGuard(folderGuard); TableOptions.RemoveOutputGuard(tableGuard); };
        Closed += (_, _) => TableOptions.Changed -= OnTableApplicationOptionsChanged;
        Closed += (_, _) => { _independentTextInputWindowClosed = true; ArchiveTexts.Clear(); ArchiveLifetime.Clear(); };
        Title = "DiffBeacon";
        Width = 1280;
        Height = 850;
        MinWidth = 850;
        MinHeight = 550;
        var root = new DockPanel();
        // 本家WinMergeのメインフレームと同じく、メニューバーとツールバーを最上部に置く（MainWindow.Frame.cs）。
        UiText.Use(textViewOptionsStore.Current.UiLanguage);
        var header = BuildFrame();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        _outputText = new TextBox { Name = "output-pane", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 72, MaxHeight = 160, FontFamily = new FontFamily("Cascadia Mono, Menlo, Consolas, monospace"), FontSize = 12 };
        _outputHost = new Border { Name = "output-pane-host", IsVisible = _showOutputPane, Background = WinMergeColors.FrameFace, BorderBrush = WinMergeColors.FrameBorder, BorderThickness = new Thickness(0, 1, 0, 0), Child = _outputText };
        DockPanel.SetDock(_outputHost, Dock.Bottom);
        root.Children.Add(_outputHost);
        _tabs.Background = WinMergeColors.FrameFace;
        _tabs.Padding = new Thickness(0);
        root.Children.Add(_tabs);
        // 多数の比較タブでも見出しが本文を押し出さないよう、既存の見出しpresenterをスクロールする。
        _tabs.TemplateApplied += (_, args) =>
        {
            if (args.NameScope.Find<ItemsPresenter>("PART_ItemsPresenter") is not { Parent: DockPanel dock } presenter) return;
            dock.Children.Remove(presenter);
            SessionHeaders = new ScrollViewer { Name = "SessionHeaders", MaxHeight = 110, Background = WinMergeColors.FrameFace, Content = presenter,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            DockPanel.SetDock(SessionHeaders, Dock.Top); dock.Children.Insert(0, SessionHeaders);
            SessionHeaders.IsVisible = _showTabBar;
            ApplyTabBarOnTitleBar();
            SessionHeaders.SizeChanged += (_, _) => QueueSelectedHeader();
            QueueSelectedHeader();
        };
        _tabs.SelectionChanged += (_, args) =>
        {
            if (!ReferenceEquals(args.Source, _tabs)) return;
            SessionSelectionGeneration++;
            if (_tabs.SelectedItem is not TabItem) return;
            ActivePane.PublishFolderActions();
            SyncDisplayFilterMenu(ActivePane.DisplayFilterBarVisible);
            UpdateImageDisplayVisibility();
            foreach (var pane in SessionPanes) pane.CancelInactiveFolderCopy(ReferenceEquals(pane, ActivePane));
            QueueSelectedHeader();
        };
        PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty) UpdateImageDisplayVisibility(); };
        Content = root;
        AddSession(arguments);
        var optionDiagnostics = new List<string>();
        if (ImageOptions.Diagnostic is { } imageDiagnostic) optionDiagnostics.Add("画像操作設定: " + imageDiagnostic);
        if (FolderOptions.Diagnostic is { } folderDiagnostic) optionDiagnostics.Add("フォルダー表示設定: " + folderDiagnostic);
        if (FolderColumnOptions.Diagnostic is { } columnDiagnostic) optionDiagnostics.Add(columnDiagnostic);
        if (textViewOptionsStore.Diagnostic is { } viewDiagnostic) optionDiagnostics.Add(viewDiagnostic);
        if (loadedMarkers.Error is { } markerDiagnostic) optionDiagnostics.Add(markerDiagnostic);
        // 二種類の読込失敗でもOpenedから一つのdialogだけを開く。
        Opened += (_, _) =>
        {
            try { MacIntegration.TryInstallServices(); }
            catch (Exception) { }
            if (PreferSelectDialog && arguments is not { Length: >= 2 } && !ActivePane.ShowsComparison) ActivePane.ShowSelectFiles();
        };
        if (optionDiagnostics.Count > 0)
            Opened += async (_, _) => await Dialogs.MessageAsync(this, "表示・操作設定", string.Join("\n\n", optionDiagnostics));
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
        // 本家のアクセラレータ（Alt+矢印など）は、キーボードナビゲーションや編集欄が矢印キーを処理する前に受ける必要がある。
        // バブル段階では Alt+Down がフォーカス移動に使われて届かないため、トンネル段階で処理する。
        AddHandler(KeyDownEvent, async (_, e) => { if (!e.Handled) await HandleFrameShortcutAsync(e); }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
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

    private void OnTableApplicationOptionsChanged()
    {
        var enabled = TableOptions.Current.UseFirstRowAsHeaders;
        foreach (var pane in SessionPanes) pane.SetTableHeaderPreference(enabled);
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

    /// <summary>印を全部の比較タブへ写し、markers.json へ保存する。</summary>
    internal void ReplaceTextMarkers(bool enabled, IReadOnlyList<TextMarker> markers)
    {
        _textMarkers.Clear();
        _textMarkers.AddRange(markers);
        _markersEnabled = enabled;
        foreach (var pane in SessionPanes) pane.ApplySharedMarkers(_textMarkers, _markersEnabled);
        if (_textMarkersStore.Save(enabled, _textMarkers, out var error) || error is null || _sessions.Count == 0) return;
        ActivePane.ShowViewOptionsStatus(error);
    }

    public ComparisonPane AddSession(string[]? arguments = null)
    {
        if (_sessions.Count >= WorkspaceStore.MaxEntries) throw new InvalidOperationException("比較タブは256件以下にしてください。");
        var pane = new ComparisonPane(this);
        pane.ApplyViewChrome(_showStatusBar, _showLocationPane, _showDiffPane);
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
    public TextBox BasePath { get; } = new() { PlaceholderText = "祖先 / 中央フォルダー・画像・バイナリ（3方向比較）" };
    public TextBox RightPath { get; } = new() { PlaceholderText = "右のファイル / フォルダー" };
    public TextBox LeftEditor { get; } = Editor();
    public TextBox RightEditor { get; } = Editor();
    public TransactionalResultEditor ResultEditor { get; } = new() { Text = "", TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Menlo, Consolas, monospace"), FontSize = 14, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    public Button CompareButton { get; } = new() { Content = "比較", MinWidth = 90 };
    public Button CopyRightButton { get; } = new() { Content = "選択差分 →" };
    public ListBox DiffList { get; } = new() { SelectionMode = SelectionMode.Single };
    // 差分結果が変わるたびに、ロケーションペインと Diff ペインを追従させる。
    public DiffResult? CurrentDiff
    {
        get => _currentDiff;
        private set { _currentDiff = value; OnCurrentDiffChanged(); }
    }
    private DiffResult? _currentDiff;
    public bool HasUnsavedChanges => LeftEditor.Text != _savedLeft || RightEditor.Text != _savedRight || IndependentText && MiddleEditor.Text != _savedMiddle || EncodingUnsaved || MergeResultDirty || SpecializedViews.HasUnsavedChanges(_specialTab.Content as Control);
    private readonly CheckBox _ignoreCase = new() { Content = "大文字小文字を無視" };
    private readonly CheckBox _ignoreSpace = new() { Content = "空白を無視" };
    private readonly CheckBox _ignoreBlank = new() { Content = "空行を無視" };
    private readonly TextBox _ignoreRegex = new() { PlaceholderText = "除外行の正規表現", Width = 210 };
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "自動", "テキスト", "フォルダー", "バイナリ", "画像", "JSON", "CSV / TSV", "アーカイブ", "拡張形式" }, SelectedIndex = 0, Width = 130 };
    private readonly ComparisonProviderRegistry _providers = BuiltinComparisonProviders.CreateDefault();
    private readonly ComboBox _provider = new() { Width = 130, Margin = new Thickness(4) };
    private readonly TextBox _fileFilter = new() { PlaceholderText = "ファイルフィルター (.flt)", Width = 220 };
    private readonly TextBox _externalFormat = new() { PlaceholderText = "外部ツール形式", Text = "text", Width = 120 };
    private readonly CheckBox _recursive = new() { Content = "サブフォルダー", IsChecked = true };
    private readonly TextBox _excludes = new() { PlaceholderText = "除外パス（;区切り）", Width = 180 };
    private readonly ComboBox _folderMode = new() { ItemsSource = new[] { "内容", "SHA-256", "日時とサイズ" }, SelectedIndex = 0, Width = 140 };
    private readonly TextBlock _status = new() { Text = "パスを選択するか、編集タブにテキストを貼り付けて比較できます。", Margin = new Thickness(8, 3), Foreground = WinMergeColors.FrameText, FontSize = 12 };
    private readonly TextBlock[] _paneInfo = [StatusCell("status-info"), StatusCell("status-info"), StatusCell("status-info")];
    private readonly TextBlock[] _paneEncoding = [StatusCell("status-encoding"), StatusCell("status-encoding"), StatusCell("status-encoding")];
    private readonly TextBlock[] _paneEol = [StatusCell("status-eol"), StatusCell("status-eol"), StatusCell("status-eol")];
    private readonly TextBlock[] _paneReadOnly = [StatusCell("status-ro"), StatusCell("status-ro"), StatusCell("status-ro")];
    private static TextBlock StatusCell(string name) => new() { Name = name, FontSize = 12, Foreground = WinMergeColors.FrameText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 2) };
    private Border? _statusBarHost;
    private readonly TextBlock _leftCaption = new() { Text = "左", Margin = new Thickness(6, 3), FontSize = 12, Foreground = WinMergeColors.FrameText, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _rightCaption = new() { Text = "右", Margin = new Thickness(6, 3), FontSize = 12, Foreground = WinMergeColors.FrameText, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TabControl _views = new();
    private readonly ScrollViewer _comparisonToolbar;
    private readonly TabItem _diffTab;
    private readonly TabItem _specialTab = new() { Header = "形式別ビュー" };
    private readonly TabItem _resultTab;
    private readonly Grid _editGrid = new();
    private readonly TextBox _ancestorEditor = Editor();
    private readonly TransactionalResultEditor _resultPreview = new() { Text = "", TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Menlo, Consolas, monospace"), FontSize = 14, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _directoryList = new();
    private TextDocument? _leftDocument, _rightDocument;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _reportOperation;
    private string? _baseText;
    private string _savedLeft = "", _savedRight = "";
    private string? _directoryLeft, _directoryMiddle, _directoryRight;
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
        LeftEditor.IsReadOnly, RightEditor.IsReadOnly, _savedLeft, _savedRight, MergeResultDirty);
    internal Action<ArchiveRetryDialog>? ArchiveRetryShown { get; set; }
    // 実GUI自己検証で、有効な比較操作の読込み開始時に中止ボタンを押す。
    internal Action? ArchiveReadStarting { get; set; }

    public ComparisonPane(Window owner) : this(owner, (owner as MainWindow)?.ArchiveTexts ?? new()) { }

    internal ComparisonPane(Window owner, ArchiveWorkingStore workingTexts)
    {
        _workingTexts = workingTexts;
        _owner = owner;
        // 本家のMDI子ウィンドウと同様に枠いっぱいへ広げ、下端をステータスバーにする。
        var root = new DockPanel();
        var top = new StackPanel { Spacing = 6, Margin = new Thickness(8, 6) };
        // 本家の「ファイルまたはフォルダーの選択」画面に合わせた入力欄（ComparisonPane.OpenView.cs）。
        _openViewHost = top;
        top.Children.Add(BuildOpenView());
        // 本家と同じくメニュー・ツールバーにある操作（前／次の差分、次の競合、パッチ・レポートの生成、外部ツールの追加、
        // マージ結果の保存）はペインから外し、外枠の FrameCommand だけで実行する。残すボタンは自己検証が名前で操作する。
        // 選択画面に出すのは本家の入力欄だけ。残りのボタンは自己検証が名前で押すため、画面には出さない。
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_mode);
        _provider.ItemsSource = _providers.Providers.Select(x => x.Id).ToArray(); _provider.SelectedIndex = 0;
        actions.Children.Add(_provider);
        InitializeTextInputs(actions);
        AddAction(actions, "再比較", () => ObserveCriticalTextSaveTask("route-compare", RefreshEditorsAsync()));
        AddAction(actions, "← 選択差分", () => CopySelectionAsync(false));
        actions.Children.Add(CopyRightButton);
        CopyRightButton.Click += async (_, _) => await GuardAsync(() => CopySelectionAsync(true));
        AddAction(actions, "左を保存", () => ObserveCriticalTextSaveTask("左を保存", SaveAsync(false)));
        AddAction(actions, "右を保存", () => ObserveCriticalTextSaveTask("右を保存", SaveAsync(true)));
        AddAction(actions, "左を外部保存", () => ObserveCriticalTextSaveTask("左を外部保存", SaveTextAsAsync(false)));
        AddAction(actions, "右を外部保存", () => ObserveCriticalTextSaveTask("右を外部保存", SaveTextAsAsync(true)));
        AddAction(actions, "自動マージ", MergeThreeWayAsync);
        AddAction(actions, "マージ開始", () => RestartMergeAsync(false));
        AddAction(actions, "アーカイブ作成", () => ObserveCriticalTextSaveTask("archive-create", CreateArchiveAsync()));
        AddAction(actions, "中止", () => { StopAllOperations(); return Task.CompletedTask; });
        top.Children.Add(actions);
        var projectActions = new WrapPanel();
        AddAction(projectActions, "プロジェクトを開く", () => ObserveCriticalTextSaveTask("project-open", OpenProjectAsync()));
        AddAction(projectActions, "プロジェクトを保存", () => ObserveCriticalTextSaveTask("project-save", SaveProjectAsync()));
        AddAction(projectActions, "比較の設定…", EditProjectOptionsAsync);
        projectActions.Children.Add(_externalFormat);
        foreach (var item in new Control[] { _folderMode, _excludes, _showFilteredDirectories }) { item.Margin = new Thickness(8, 4); projectActions.Children.Add(item); }
        top.Children.Add(projectActions);
        var options = new WrapPanel { Orientation = Orientation.Horizontal };
        // 本文の検索は本家と同じく「編集」メニューの検索・置換ダイアログ（ComparisonPane.FindReplace.cs）で行う。
        foreach (var control in new Control[] { _ignoreCase, _ignoreSpace, _ignoreBlank, _ignoreRegex }) { control.Margin = new Thickness(8, 4); options.Children.Add(control); }
        top.Children.Add(options);
        top.Children.Add(CreateAdvancedFilters());
        top.Children.Add(CreateLineBookmarkToolbar());
        top.Children.Add(CreateMergeToolbar());
        var toolbar = new ScrollViewer { Content = top, MaxHeight = 400,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        // 設定・操作欄が折り返しても、比較本文に領域を残す。
        _comparisonToolbar = toolbar;
        // 比較中は選択画面を畳む。ボタンを表示へ戻す要求が来たら広げ、レイアウトの後でもう一度合わせる。
        AddHandler(RequestBringIntoViewEvent, (_, args) =>
        {
            if (toolbar.MaxHeight > 1 || !HasComparisonContent) return;
            if (args.TargetObject is not Control target || !toolbar.IsVisualAncestorOf(target)) return;
            _selectFilesPinned = true;
            UpdateComparisonToolbarHeight();
            Dispatcher.UIThread.Post(() => target.BringIntoView(), DispatcherPriority.Loaded);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        SizeChanged += (_, _) => UpdateComparisonToolbarHeight();
        _views.SelectionChanged += (_, _) => UpdateComparisonToolbarHeight();
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        // 本家のステータスバー（MergeStatusBar）に合わせ、灰色の帯に状態文を出す。
        var statusRow = new Grid();
        statusRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        statusRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        statusRow.Children.Add(_status);
        var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4, 0) };
        for (var side = 0; side < 3; side++)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Name = "status-pane-" + side };
            group.Children.Add(_paneInfo[side]); group.Children.Add(_paneEncoding[side]); group.Children.Add(_paneEol[side]); group.Children.Add(_paneReadOnly[side]);
            details.Children.Add(group);
        }
        Grid.SetColumn(details, 1); statusRow.Children.Add(details);
        var statusBar = _statusBarHost = new Border { Name = "comparison-status-bar", Background = WinMergeColors.FrameFace, BorderBrush = WinMergeColors.FrameBorder, BorderThickness = new Thickness(0, 1, 0, 0), Child = statusRow };
        DockPanel.SetDock(statusBar, Dock.Bottom); root.Children.Add(statusBar);
        DiffList.ItemTemplate = new FuncDataTemplate<DiffRow>((row, _) => BuildRow(row), false);
        // 本家のエディターと同じく行を詰めて並べる。Fluent の ListBoxItem は余白が大きく、行の間に隙間ができるため打ち消す。
        DiffList.Background = Brushes.Transparent;
        DiffList.ContainerPrepared += (_, args) =>
        {
            if (args.Container is not ListBoxItem item) return;
            item.Padding = new Thickness(0); item.MinHeight = 0;
        };
        // 本家のファイル比較画面（ロケーションペイン・ファイル名の帯・Diff ペイン）は ComparisonPane.FileView.cs で組む。
        _diffTab = new TabItem { Header = "差分", Content = BuildFileCompareView() };
        _ancestorEditor.IsReadOnly = true;
        LeftEditor.TextChanged += (_, _) => RefreshArchiveDraftCaptions();
        RightEditor.TextChanged += (_, _) => RefreshArchiveDraftCaptions();
        UpdateEditorLayout(false);
        InitializeLineBookmarks();
        _resultTab = new TabItem { Header = "マージ結果", Content = CreateMergeResultView() };
        // 本家と同じく「差分」タブの本文で直接編集するため、別の「編集」タブは置かない。
        // 各側の正本（TextBox）と _editGrid は保存・マージ・既存の契約のため内部に残す（画面には出さない）。
        _views.ItemsSource = new[] { _diffTab, _resultTab, _specialTab };
        foreach (var view in _views.Items.OfType<TabItem>()) MainWindow.CompactTab(view);
        // 本家のファイル比較に「差分 / マージ結果 / 形式別ビュー」のタブ列は無い。見出しはテストと表示メニューが使うので残し、帯だけ畳む。
        _views.TemplateApplied += (_, args) =>
        {
            if (args.NameScope.Find<Control>("PART_ItemsPresenter") is not { } strip) return;
            strip.Height = 0; strip.MinHeight = 0; strip.MaxHeight = 0; strip.Opacity = 0; strip.IsHitTestVisible = false;
        };
        _views.SelectedItem = _diffTab;
        _views.SelectionChanged += (_, _) => (_owner as MainWindow)?.UpdateImageDisplayVisibility();
        root.Children.Add(_views);
        Content = root;
        InitializeFolderView();
        CompareButton.Click += async (_, _) => await GuardAsync(async () =>
        {
            try { await ObserveCriticalTextSaveTask("path-compare", ComparePathsAsync()); }
            finally { _selectFilesPinned = false; UpdateComparisonToolbarHeight(); }
        });
        _directoryList.DoubleTapped += async (_, _) => await GuardAsync(async () =>
        {
            if (_directoryList.SelectedItem is not DirectoryEntry entry || _directoryLeft is null || _directoryRight is null) return;
            var l = Path.Combine(_directoryLeft, entry.RelativePath); var r = Path.Combine(_directoryRight, entry.RelativePath);
            var middle = _directoryMiddle is null ? null : Path.Combine(_directoryMiddle, entry.RelativePath);
            if (File.Exists(l) && File.Exists(r) && (middle is null || File.Exists(middle)))
            {
                LeftPath.Text = l; BasePath.Text = middle ?? ""; RightPath.Text = r;
                _mode.SelectedIndex = 0; await ComparePathsAsync();
            }
        });
        _ownerClosedHandler = (_, _) => Dispose();
        _owner.Closed += _ownerClosedHandler;
        // 本家の「表示」メニューの設定（拡大／縮小・空白・改行など）はアプリ全体で一つ。外枠の設定に合わせて見た目をそろえる。
        AttachTextViewOptions((owner as MainWindow)?.TextViewOptions ?? new DiffTextViewOptions());
    }

    private static TextBox Editor() => new() { Text = "", AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Menlo, Consolas, monospace"), FontSize = 14, HorizontalContentAlignment = HorizontalAlignment.Stretch };
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
    private ComparisonOptions Options() => new() { IgnoreCase = _ignoreCase.IsChecked == true, IgnoreWhitespace = _ignoreSpace.IsChecked == true, IgnoreBlankLines = _ignoreBlank.IsChecked == true, IgnoreNumbers = _ignoreNumbers.IsChecked == true, CommentSyntax = (CommentSyntax)_comments.SelectedIndex, Whitespace = (WhitespaceMode)_whitespace.SelectedIndex, InlineCharacterLevel = _inlineDiffLevel.SelectedIndex != 1, BreakOnSeparators = _breakOnSeparators.IsChecked == true, WordBreakSeparators = _wordBreakSeparators.Text ?? "", SubstitutionRules = _substitutions, LineTransformRules = Array.AsReadOnly(_projectMetadata.LineTransformRules.ToArray()), LineTransformTimestampEnvironment = LineTransformTimestampEnvironments.ForRules(_projectMetadata.LineTransformRules), LineTransformStringEnvironment = LineTransformStringEnvironments.ForRules(_projectMetadata.LineTransformRules), LineTransformLogEnvironment = LineTransformLogEnvironments.Trace, LineTransformListEnvironment = LineTransformListEnvironments.Create(LineTransformLogEnvironments.Trace, _projectMetadata), TableColumnIgnoreRules = Array.AsReadOnly(_projectMetadata.TableColumnIgnoreRules.ToArray()), TableColumnRangeRules = Array.AsReadOnly(_projectMetadata.TableColumnRangeRules.ToArray()), IgnoreLinePattern = string.IsNullOrWhiteSpace(_ignoreRegex.Text) ? null : _ignoreRegex.Text };

    public async Task ComparePathsAsync()
    {
        try { await ComparePathsAsyncCore(); }
        finally { _selectFilesPinned = false; HideSelectFiles(); UpdateComparisonToolbarHeight(); (_owner as MainWindow)?.RefreshSessionHeaders(); }
    }

    private async Task ComparePathsAsyncCore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_textRole.SelectedIndex == 1)
        { await CompareIndependentTextAsync(); return; }
        InvalidateFolderCopy();
        if (ProjectInputs.HasArchives(CaptureProject()))
        {
            var requestedTextRole = _textRole.SelectedIndex;
            var confirmationStamp = IndependentText ? TextAdoptionStamp() : null;
            if (HasUnsavedChanges && !await Dialogs.ConfirmAsync(_owner, "未保存の変更", "編集内容を破棄して内包項目を開き直しますか？")) return;
            if (requestedTextRole != _textRole.SelectedIndex || confirmationStamp is not null && !Equals(confirmationStamp, TextAdoptionStamp()))
            { _status.Text = "確認中に役割または三側の状態が変更されました。前の比較と本文を保持しました。"; return; }
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
        var imageSettings = CaptureImageSettings(useApplicationDefaults: true);
        var comparisonForPackaging = (left, BasePath.Text ?? "", right, _mode.SelectedIndex, _provider.SelectedItem as string);
        var previousBinary = _specialTab.Content as SpecializedViews.BinaryPanel;
        var previousBinaryStamp = previousBinary?.StateStamp;
        var previousTable = _specialTab.Content as TablePanel;
        var previousTableComparison = previousTable?.Comparison;
        var previousTableText = (LeftEditor.Text, RightEditor.Text, _baseText);
        var lineTransformsAtStart = _projectMetadata.LineTransformRules.ToArray();
        var candidateTextRole = _textRole.SelectedIndex;
        var syncPointRevisionAtStart = _syncPointRevision;
        var resultHost = _resultHost; var resultVersion = resultHost?.Session.Current.Version;
        var inheritedIndependentStamp = IndependentText ? TextAdoptionStamp() : null;
        bool CanAdopt()
        {
            var valid = !token.IsCancellationRequested && !_disposed && ReferenceEquals(_operation, operation)
                && previousBinary?.StateStamp == previousBinaryStamp
                && (previousTable is null || ReferenceEquals(previousTable.Comparison, previousTableComparison)
                    && previousTableText == (LeftEditor.Text, RightEditor.Text, _baseText))
                && lineTransformsAtStart.SequenceEqual(_projectMetadata.LineTransformRules)
                && !MergeHasPendingComposition && ReferenceEquals(resultHost, _resultHost) && resultVersion == _resultHost?.Session.Current.Version
                && candidateTextRole == _textRole.SelectedIndex
                && syncPointRevisionAtStart == _syncPointRevision
                && (inheritedIndependentStamp is null || Equals(inheritedIndependentStamp, TextAdoptionStamp()))
                && comparisonForPackaging == (LeftPath.Text ?? "", BasePath.Text ?? "", RightPath.Text ?? "", _mode.SelectedIndex, _provider.SelectedItem as string);
            if (!valid && !_disposed && ReferenceEquals(_operation, operation))
                _status.Text = UiText.Say(token.IsCancellationRequested ? "比較を中止しました。" : "比較中に入力が変更されたため、前の比較を保持しました。");
            return valid;
        }
        void PrepareAdoption()
        {
            AdoptLegacyTextRole();
            ResetMergeSession(); ClearEncodingUnsaved(); ReleaseConflictSaveUnlessCurrent(); _lastPackageComparison = null;
            ResetLineBookmarks();
            _textSaveAllowed = false; _baseText = null; CurrentDiff = null;
        }
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
        {
            var editorLeft = LeftEditor.Text ?? ""; var editorRight = RightEditor.Text ?? ""; var editorOptions = Options();
            var editorSyncPoints = _mode.SelectedIndex is 0 or 1 ? TextSyncPointsFor(0, 2).ToArray() : Array.Empty<TextSyncPoint>();
            var editorDiff = await Task.Run(() => CompareTextWithSyncPoints(editorLeft, editorRight, editorSyncPoints, editorOptions, token), token);
            if (CanAdopt() && editorLeft == LeftEditor.Text && editorRight == RightEditor.Text) { AdoptLegacyTextRole(); ResetMergeSession(); _lastPackageComparison = null; ApplyDiff(editorDiff); }
            return;
        }
        _status.Text = UiText.Say(UiText.Phrase("Comparing..."));
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
                _status.Text = UiText.Say(result.Summary + " · 変換後の内容です。元のファイルへのテキスト保存はできません。");
                _lastPackageComparison = comparisonForPackaging;
                return;
            }
            if (mode == 2 || (FolderComparisons.IsDirectory(left) && FolderComparisons.IsDirectory(right)))
            {
                if (await CompareDirectoryAsync(left, right, token, CanAdopt, PrepareAdoption, comparisonForPackaging.Item2)) _lastPackageComparison = comparisonForPackaging;
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
                    SetSpecialView(imageView);
                    ((SpecializedViews.ImagePanel)imageView).EnableApplicationDefaultsPersistence();
                    imageView = null;
                    _savedLeft = LeftEditor.Text ?? ""; _savedRight = RightEditor.Text ?? ""; DiscardMergeChanges();
                    LeftEditor.IsReadOnly = RightEditor.IsReadOnly = true;
                    _views.SelectedItem = _specialTab; _status.Text = "画像を比較しました。"; _lastPackageComparison = comparisonForPackaging; _pendingRestoredImageSettings = false;
                    UpdateLayout();
                    return;
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
                var leftNameCodePage = 28591; var rightNameCodePage = 28591;
                var leftPayloadKind = GZipPayloadKind.Auto; var rightPayloadKind = GZipPayloadKind.Auto;
                var leftCompressionPayloadKind = CompressionPayloadKind.Auto; var rightCompressionPayloadKind = CompressionPayloadKind.Auto;
                try
                {
                    while (candidate is null)
                    {
                        ArchiveReadStarting?.Invoke();
                        token.ThrowIfCancellationRequested();
                        try { candidate = await ArchivePanel.CreateWithPasswordsAsync(left, right, token, EnsureArchiveOutputWritable, leftPassword, rightPassword, leftNameCodePage, rightNameCodePage, leftPayloadKind, rightPayloadKind, leftCompressionPayloadKind, rightCompressionPayloadKind); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception exception) when (exception is not OutOfMemoryException)
                        {
                            token.ThrowIfCancellationRequested();
                            if (operation != _operation) return;
                            var dialog = new ArchiveRetryDialog(leftPassword, rightPassword, exception is DiffBeacon.Providers.ArchiveNameDecodingException, leftNameCodePage, rightNameCodePage, leftPayloadKind, rightPayloadKind, leftCompressionPayloadKind, rightCompressionPayloadKind);
                            try
                            {
                                var retryTask = dialog.ShowDialog<ArchiveRetryResult?>(_owner);
                                ArchiveRetryShown?.Invoke(dialog);
                                var retry = await retryTask;
                                token.ThrowIfCancellationRequested();
                                if (operation != _operation) return;
                                if (retry is null) throw new OperationCanceledException("アーカイブ操作を中止しました。", token);
                                leftPassword = retry.LeftPassword; rightPassword = retry.RightPassword;
                                leftNameCodePage = retry.LeftNameCodePage; rightNameCodePage = retry.RightNameCodePage;
                                leftPayloadKind = retry.LeftPayloadKind; rightPayloadKind = retry.RightPayloadKind;
                                leftCompressionPayloadKind = retry.LeftCompressionPayloadKind; rightCompressionPayloadKind = retry.RightCompressionPayloadKind;
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
            var useCurrentSyncPoints = (mode is 0 or 1) && LeftEditor.Text == leftDocument.Text && RightEditor.Text == rightDocument.Text;
            var syncPoints = useCurrentSyncPoints ? TextSyncPointsFor(0, 2).ToArray() : Array.Empty<TextSyncPoint>();
            var textDiff = mode is 5 or 6 ? null : await Task.Run(() => CompareTextWithSyncPoints(leftDocument.Text, rightDocument.Text, syncPoints, textOptions, token, sourcePaths: TextComparisonSources.FromDocuments(leftDocument, baseDocument, rightDocument)), token);
            var tableCandidate = mode == 6 ? await CreateTableViewAsync(leftDocument, rightDocument, baseDocument, token) : default;
            LegacyTextReadyForAdoption?.Invoke();
            token.ThrowIfCancellationRequested();
            if (!CanAdopt()) return;
            PrepareAdoption();
            _leftDocument = leftDocument; _rightDocument = rightDocument; _baseDocument = baseDocument;
            LeftEditor.IsReadOnly = _projectMetadata.LeftReadOnly;
            RightEditor.IsReadOnly = _projectMetadata.RightReadOnly;
            LeftEditor.Text = _savedLeft = _leftDocument.Text;
            RightEditor.Text = _savedRight = _rightDocument.Text;
            ClearEncodingUnsaved();
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
        if (IndependentText) { CompareIndependentEditors(); return; }
        ApplyDiff(CompareTextWithSyncPoints(LeftEditor.Text ?? "", RightEditor.Text ?? "", 0, 2, Options(), CancellationToken.None));
    }
    private async Task CompareEditorsAsync()
    {
        if (IndependentText) { await StartIndependentEditorComparisonAsync(); return; }
        var left = LeftEditor.Text ?? ""; var right = RightEditor.Text ?? ""; var options = Options();
        var token = _operation?.Token ?? CancellationToken.None;
        var syncPointRevision = _syncPointRevision;
        var syncPoints = _mode.SelectedIndex is 0 or 1 ? TextSyncPointsFor(0, 2).ToArray() : Array.Empty<TextSyncPoint>();
        var sourcePaths = CurrentTextSourcePaths();
        var diff = await Task.Run(() => CompareTextWithSyncPoints(left, right, syncPoints, options, token, sourcePaths: sourcePaths), token);
        if (left == LeftEditor.Text && right == RightEditor.Text && syncPointRevision == _syncPointRevision) ApplyDiff(diff);
    }
    private Task RefreshEditorsAsync()
    {
        InvalidateFolderCopy();
        if (IndependentText) return CompareEditorsAsync();
        if (ProjectInputs.HasArchives(_projectMetadata) || _specialTab.Content is ArchivePanel
            || ReferenceEquals(_specialTab.Content, _folderView)) return ComparePathsAsync();
        _operation?.Cancel(); _operation?.Dispose(); _operation = new CancellationTokenSource();
        if (_mode.SelectedIndex == 6 && _specialTab.Content is TablePanel table) return table.RefreshAsync();
        return CompareEditorsAsync();
    }
    private void ApplyDiff(DiffResult diff)
    {
        CurrentDiff = diff;
        DiffList.ItemsSource = CurrentDiff.Rows;
        _diffIndex = -1;
        _status.Text = UiText.Say($"{CurrentDiff.Blocks.Count} 個の差分  ·  左 {_leftDocument?.EncodingName ?? "UTF-8"}  /  右 {_rightDocument?.EncodingName ?? "UTF-8"}");
        NoteOpenedPaths();
        if (diff.InlineFallbackCount > 0) _status.Text += $"  ·  詳細比較を省略した {diff.InlineFallbackCount} 行は行全体を強調";
        if (diff.LineFallback) _status.Text += "  ·  行対応の処理上限に達したため、未確定区間をまとめて表示";
        _views.SelectedItem = _diffTab;
        RefreshStatusPanes();
        PublishFolderActions();
    }

    public void NavigateDifference(int direction)
    {
        if (_specialTab.Content is SpecializedViews.ImagePanel image) { _ = image.NavigateRegionAsync(direction); return; }
        if (CurrentDiff is not { Blocks.Count: > 0 }) return;
        _diffIndex = (_diffIndex + direction + CurrentDiff.Blocks.Count) % CurrentDiff.Blocks.Count;
        DiffList.SelectedIndex = CurrentDiff.Blocks[_diffIndex].RowStart;
        RevealDifference(CurrentDiff.Blocks[_diffIndex]);
    }

    public void CopySelected(bool leftToRight)
    {
        if (IndependentText) { CopyIndependentSelected(leftToRight); return; }
        EnsureSideWritable(leftToRight);
        if (CurrentDiff is null) return;
        // 編集後の古い差分座標で上書きしない。
        if (CurrentDiff.LeftText != LeftEditor.Text || CurrentDiff.RightText != RightEditor.Text) CompareEditors();
        var block = CurrentDiff!.Blocks.FirstOrDefault(x => DiffList.SelectedIndex >= x.RowStart && DiffList.SelectedIndex < x.RowStart + x.RowCount)
            ?? (_diffIndex >= 0 && _diffIndex < CurrentDiff.Blocks.Count ? CurrentDiff.Blocks[_diffIndex] : null);
        if (block is null) { _status.Text = "差分の行を選択してください。"; return; }
        // コピーは全ペイン共有の履歴へ一つの変更として残す（元に戻せる）。
        if (leftToRight) WriteRecordedText(2, TextMerger.CopyLeftToRight(CurrentDiff, block));
        else WriteRecordedText(0, TextMerger.CopyRightToLeft(CurrentDiff, block));
        CompareEditors();
    }

    /// <summary>
    /// 本家の ID_ALL_RIGHT / ID_ALL_LEFT。すべての差分ブロックを一度にコピーし、全ペイン共有の履歴へ一つの変更として残す。
    /// 確認は呼び出し元（<see cref="CopyAllAsync"/>）で行う。
    /// </summary>
    public void CopyAll(bool leftToRight)
    {
        if (IndependentText) { CopyIndependentSelected(leftToRight, all: true); return; }
        EnsureSideWritable(leftToRight);
        if (CurrentDiff is null) return;
        // 編集後の古い差分座標で上書きしない。
        if (CurrentDiff.LeftText != LeftEditor.Text || CurrentDiff.RightText != RightEditor.Text) CompareEditors();
        if (CurrentDiff!.Blocks.Count == 0) { _status.Text = "コピーする差異はありません。"; return; }
        if (leftToRight) WriteRecordedText(2, TextMerger.CopyAllLeftToRight(CurrentDiff));
        else WriteRecordedText(0, TextMerger.CopyAllRightToLeft(CurrentDiff));
        CompareEditors();
    }

    /// <summary>headless検証用: 「すべてコピー」の確認の代わりに呼ぶ。null なら確認のダイアログを出す。</summary>
    internal Func<string, Task<bool>>? CopyAllConfirmation { get; set; }

    // 本家と同じく、コピー先が書き込めることを確かめてから確認し、承諾したときだけコピーする。
    private async Task CopyAllAsync(bool toRight)
    {
        if (_views.SelectedItem == _specialTab && _specialTab.Content is not null)
        { _status.Text = "すべてのコピーはテキストの比較で使えます。"; return; }
        if (CurrentDiff is not { Blocks.Count: > 0 }) { _status.Text = "コピーする差異はありません。"; return; }
        if (IndependentText) { EnsureIndependentTextSource(); var pair = TextPair(); EnsureTextSideWritable(toRight ? pair.Right : pair.Left); }
        else EnsureSideWritable(toRight);
        const string message = "本当にすべての差異を他のファイルにコピーしますか?";
        if (!await (CopyAllConfirmation is { } confirm ? confirm(message) : Dialogs.ConfirmAsync(_owner, "すべてコピー", message))) return;
        if (IndependentText)
        {
            var task = CopyIndependentSelectedAsync(toRight, all: true); IndependentTextUiTask = task; await task; return;
        }
        CopyAll(toRight);
    }

    public void DiscardChanges() { _savedLeft = LeftEditor.Text ?? ""; _savedMiddle = MiddleEditor.Text ?? ""; _savedRight = RightEditor.Text ?? ""; ClearEncodingUnsaved(); DiscardMergeChanges(); SpecializedViews.DiscardChanges(_specialTab.Content as Control); }
    public void SelectMode(int mode) => _mode.SelectedIndex = mode;
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        CloseTextSearchDialog();
        DetachTextViewOptions();
        InvalidateMergeSave(); ResultEditor.CancelComposition("pane-disposed"); _resultPreview.CancelComposition("pane-disposed"); ResultEditor.DetachHost(); _resultPreview.DetachHost();
        IndependentTextInputParentDisposed?.Invoke(); IndependentTextInputParentDisposed = null;
        InvalidateFolderCopy();
        InvalidateTextSave();
        _owner.Closed -= _ownerClosedHandler;
        _operation?.Cancel(); _operation?.Dispose();
        _reportOperation?.Cancel();
        ClearArchivePasswords();
        SpecializedViews.Release(_specialTab.Content as Control);
        DeleteConflictTemps();
    }
    internal void UpdateImageDisplayVisibility(bool active)
    {
        if (_specialTab.Content is SpecializedViews.ImagePanel image)
            image.SetDisplayActive(active && ReferenceEquals(_views.SelectedItem, _specialTab));
    }
    private void SetSpecialView(Control? view)
    {
        if (ReferenceEquals(_specialTab.Content, _folderView) && !ReferenceEquals(view, _folderView)) InvalidateFolderCopy();
        SpecializedViews.Release(_specialTab.Content as Control);
        _specialTab.Content = view;
        _archivePanelRequestGeneration = view is ArchivePanel ? _archiveRequestGeneration : -1;
        if (view is SpecializedViews.BinaryPanel binary) BindBinaryPanel(binary);
        (_owner as MainWindow)?.UpdateImageDisplayVisibility();
        UpdateComparisonToolbarHeight();
    }
    // 「開く」で、比較中でも選択画面を手前に戻す。比較が始まると本家と同じく選択画面は隠す。
    private bool _selectFilesPinned;
    private bool _sawComparisonContent;

    private StackPanel? _openViewHost;
    private Window? _selectDialog;

    /// <summary>本家の「開く」と同じく、選択画面を別ダイアログで出して 1 番目の欄へ移る。</summary>
    internal void ShowSelectFiles()
    {
        if (OpenView is null || _openViewHost is null) return;
        _selectDialog ??= new Window
        {
            Title = "ファイルまたはフォルダーの選択",
            Width = 780, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Name = "select-dialog-scroll" },
        };
        if (OpenView.Parent is Panel parent) parent.Children.Remove(OpenView);
        ((ScrollViewer)_selectDialog.Content!).Content = OpenView;
        if (!_selectDialog.IsVisible) _selectDialog.Show(_owner);
        LeftPath.Focus();
        LeftPath.SelectAll();
    }

    /// <summary>選択ダイアログを閉じ、入力欄を比較タブへ戻す。比較が始まったあとは帯を畳む。</summary>
    internal void HideSelectFiles()
    {
        if (OpenView is not null && _openViewHost is not null && !ReferenceEquals(OpenView.Parent, _openViewHost))
        {
            if (_selectDialog?.Content is ScrollViewer scroll && ReferenceEquals(scroll.Content, OpenView)) scroll.Content = null;
            if (OpenView.Parent is Panel parent) parent.Children.Remove(OpenView);
            _openViewHost.Children.Insert(0, OpenView);
        }
        if (_selectDialog?.IsVisible == true) _selectDialog.Hide();
    }

    private void UpdateComparisonToolbarHeight()
    {
        // 比較が始まった直後は、選択画面を閉じて本文（またはフォルダー一覧）を全面に出す。
        if (HasComparisonContent && !_sawComparisonContent) _selectFilesPinned = false;
        _sawComparisonContent = HasComparisonContent;
        if (!HasComparisonContent) _selectFilesPinned = false;
        var showSelect = !HasComparisonContent || _selectFilesPinned || IndependentText;
        // 非表示でも IsVisible は真のままにし、中身はスクロール領域として配置する。自己検証はボタンの大きさを見る。
        _comparisonToolbar.IsVisible = true;
        _comparisonToolbar.Opacity = showSelect ? 1 : 0;
        _comparisonToolbar.IsHitTestVisible = showSelect;
        if (!showSelect)
        {
            _comparisonToolbar.MaxHeight = 1;
            return;
        }
        // 比較前は選択画面（入力欄と比較ボタン）だけが見える高さにする。その下の操作列はスクロールの外。
        // 比較中に「開く」で戻したとき、または独立三者テキストでは、本文を残すため上限を付ける。
        if (!HasComparisonContent)
        {
            var openHeight = OpenView?.Bounds.Height > 40 ? OpenView.Bounds.Height + 16 : 420;
            _comparisonToolbar.MaxHeight = openHeight;
        }
        else if (IndependentText)
            _comparisonToolbar.MaxHeight = Bounds.Height > 0 ? Math.Clamp(Bounds.Height * .25, 100, 400) : 400;
        else
            _comparisonToolbar.MaxHeight = Bounds.Height > 0 ? Math.Clamp(Bounds.Height * .45, 180, 420) : 420;
    }
    private async Task CopySelectionAsync(bool toRight)
    {
        if (IndependentText) { await StartIndependentTextCopyAsync(toRight); return; }
        EnsureSideWritable(toRight);
        if (_views.SelectedItem == _specialTab && ReferenceEquals(_specialTab.Content, _folderView))
        {
            await CopyFolderSelectionAsync(toRight, DirectoryCopyMode.DifferencesOnly);
        }
        else CopySelected(toRight);
    }
    private async Task SaveProjectAsync()
    {
        var path = ProjectPathPicker is { } picker ? await picker(false) : await SavePathAsync("比較プロジェクトを保存", "comparison.diffbeacon.json");
        if (path is null) return;
        if (_owner is MainWindow window) await window.SaveWorkspaceAsync(path);
        else { EnsureArchiveDraftSaved(); await WorkspaceStore.SaveAsync(path, CaptureProject()); }
        _status.Text = UiText.Say("すべての比較タブをプロジェクトへ保存しました。編集本文は元ファイルへ別途保存してください。");
    }
    private async Task OpenProjectAsync()
    {
        string? path;
        if (ProjectPathPicker is { } picker) path = await picker(true);
        else
        {
            var paths = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "比較プロジェクトを開く" });
            path = paths.Count > 0 ? paths[0].TryGetLocalPath() : null;
        }
        if (path is null) return;
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
        _status.Text = UiText.Say($"{Path.GetFileName(executable)} を選択しました。比較操作で実行します。");
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
        Action<string, Action> outputPublisher = (destination, publish) =>
        {
            void Publish() { EnsureProjectOutputWritable(destination); publish(); }
            if (Dispatcher.UIThread.CheckAccess()) Publish();
            else Dispatcher.UIThread.InvokeAsync(Publish).GetAwaiter().GetResult();
        };
        if (workspaceWindow is not null) outputPublisher = workspaceWindow.CaptureOutputPublisher();
        var left = LeftEditor.Text ?? ""; var right = RightEditor.Text ?? ""; var ancestor = IndependentText ? MiddleEditor.Text ?? "" : _baseText;
        var textSnapshots = IndependentText ? CaptureIndependentTextDocuments() : null;
        var textSourcePaths = CurrentTextSourcePaths();
        ImageComparisonEngine.ReportInput? imageInput = null;
        if (ProjectReport.IsImage(project))
        {
            imageInput = (_specialTab.Content as SpecializedViews.ImagePanel
                ?? throw new InvalidOperationException("画像を比較してからレポートを生成してください。")).CaptureReport();
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _reportOperation = operation;
        _status.Text = UiText.Say("HTMLレポートを生成しています…（中止できます）");
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
                : ProjectReport.Create(project, left, ancestor, right, cancellation, snapshots: textSnapshots, sourcePaths: textSourcePaths), cancellation);
            // 生成開始時の入力と、生成中に開いた・保存した現行プロジェクトの両方を保護する。
            var currentPanes = workspaceWindow?.SessionPanes ?? [this];
            var currentSource = new ComparisonProject { LeftPath = workspaceWindow?.WorkspaceSourcePath ?? "" };
            await ProjectReport.SaveAsync(path, html, protectedEntries.Concat(currentPanes.Select(pane => pane.CaptureProject())).Append(currentSource),
                sourceProject, cancellation, outputPublisher);
            _status.Text = UiText.Say("HTMLレポートを保存しました。");
        }
        finally { _reportOperation = null; }
    }
    public Task SaveAsync(bool right) => _specialTab.Content is SpecializedViews.BinaryPanel binary ? binary.SaveAsync(right) : SaveTextCoreAsync(right, null, CancellationToken.None);
    private async Task<string?> SavePathAsync(string title, string suggested) => (await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggested, ShowOverwritePrompt = true }))?.TryGetLocalPath();
    private async Task ExportPatchAsync()
    {
        if (IndependentText) throw new InvalidOperationException("独立三者Textのパッチ出力にはまだ対応していません。");
        var path = await SavePathAsync("Unifiedパッチを保存", "changes.patch");
        if (path is null) return;
        EnsureProjectOutputWritable(path);
        await File.WriteAllTextAsync(path, UnifiedPatch.Create(LeftEditor.Text ?? "", RightEditor.Text ?? "", LeftPath.Text ?? "left", RightPath.Text ?? "right"));
        _status.Text = UiText.Say("パッチを保存しました。");
    }
    private Task MergeThreeWayAsync() => RestartMergeAsync(true);
    private void UpdateEditorLayout(bool fourPanes)
    {
        // ソース保存などの再配置でも、採用済みの結果編集ペインを保持する。
        fourPanes |= !_disposed && _resultHost is not null;
        if (IndependentText) { UpdateIndependentEditorLayout(); return; }
        if (_views.ItemsSource is IEnumerable<TabItem> tabs)
            foreach (var tab in tabs.Where(tab => ReferenceEquals(tab.Content, _editGrid))) tab.Header = "編集 / 4ペイン";
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
    // ID_NEXTCONFLICT / ID_PREVCONFLICT: direction は 1 で次、-1 で前。端まで行ったら反対の端へ回る（差異の移動と同じ）。
    private void NavigateConflict(int direction)
    {
        if (_specialTab.Content is SpecializedViews.ImagePanel image)
        {
            _ = image.NavigateRegionAsync(direction, conflictsOnly: true); return;
        }
        if (CurrentMergeSession is { } session)
        {
            var pending = session.Sections.Where(range => range.Section.IsPending).ToArray();
            if (pending.Length == 0) { _status.Text = "未解決の差分はありません。"; return; }
            var currentId = (_mergeSections.SelectedItem as SectionChoice)?.Id;
            var current = Array.FindIndex(pending, range => range.Section.Id == currentId);
            var range = pending[direction >= 0 ? (current + 1) % pending.Length
                : current < 0 ? pending.Length - 1 : (current - 1 + pending.Length) % pending.Length];
            _mergeSections.SelectedItem = _mergeSections.Items.OfType<SectionChoice>().Single(item => item.Id == range.Section.Id);
            _views.SelectedItem = _resultTab; SelectMergeRange(range); ResultEditor.Focus(); return;
        }
        var text = ResultEditor.Text ?? "";
        const string marker = "<<<<<<<";
        int position;
        if (direction >= 0)
        {
            position = text.IndexOf(marker, Math.Min(text.Length, ResultEditor.SelectionEnd + 1), StringComparison.Ordinal);
            if (position < 0) position = text.IndexOf(marker, StringComparison.Ordinal);
        }
        else
        {
            // 選択の先頭より前から始まる目印を探す（LastIndexOf の開始位置は目印の末尾の文字の位置）。無ければ末尾から回る。
            var before = Math.Min(ResultEditor.SelectionStart, ResultEditor.SelectionEnd);
            position = text.Length == 0 || before <= 0 ? -1 : text.LastIndexOf(marker, Math.Min(text.Length - 1, before - 1 + marker.Length - 1), StringComparison.Ordinal);
            if (position < 0) position = text.LastIndexOf(marker, StringComparison.Ordinal);
        }
        if (position < 0) { _status.Text = "競合マーカーはありません。"; return; }
        _views.SelectedItem = _resultTab;
        ResultEditor.SelectionStart = position; ResultEditor.SelectionEnd = position + marker.Length; ResultEditor.CaretIndex = position;
        ResultEditor.Focus();
    }
    private async Task SaveResultAsync()
    {
        CommitMergeInput();
        var host = _resultHost ?? throw new InvalidOperationException("先にマージを開始してください。");
        var generation = _mergeSaveGeneration; var version = host.Session.Current.Version;
        void Current() { if (_disposed || generation != _mergeSaveGeneration || !ReferenceEquals(host, _resultHost) || version != host.Session.Current.Version || MergeHasPendingComposition) throw new InvalidOperationException("保存の選択中にマージ結果が変更されました。"); }
        var unresolved = CurrentMergeSession?.UnresolvedCount ?? 0;
        if (unresolved > 0 && !await Dialogs.ConfirmAsync(_owner, "競合が残っています", "未解決の差分を含む結果を保存しますか？")) return;
        var path = await SavePathAsync("マージ結果を保存", "merged.txt");
        Current();
        if (path is null) return;
        await SaveMergeResultToAsync(path, allowUnresolved: true);
    }

    private async Task<bool> CompareDirectoryAsync(string left, string right, CancellationToken token, Func<bool> canAdopt, Action prepareAdoption,
        string? middle = null)
    {
        var folderGeneration = _folderContextGeneration;
        var previousFolderModel = _directoryComparison;
        var configuration = CaptureFolderConfiguration();
        var filterDocumentStamp = FolderFilterDocumentStamp();
        var listDependencies = new FolderFilterListState(LineTransformLogEnvironments.Trace, FolderListReadProgress);
        var textOptions = Options();
        var filtered = textOptions.IgnoreCase || textOptions.IgnoreWhitespace || textOptions.IgnoreBlankLines || textOptions.IgnoreLinePattern is not null
            || textOptions.IgnoreNumbers || textOptions.CommentSyntax != CommentSyntax.None || textOptions.Whitespace != WhitespaceMode.None || textOptions.SubstitutionRules.Any(rule => rule.Enabled) || textOptions.LineTransformRules.Count > 0;
        // UI値を先に固定する。最適化のlist I/O中も中止と世代変更を受け付ける。
        var filterPath = _fileFilter.Text;
        var legacyFilter = _projectMetadata.LegacyFilter;
        var options = new DirectoryComparisonOptions { Recursive = _recursive.IsChecked == true, Mode = (DirectoryComparisonMode)_folderMode.SelectedIndex, ExcludePatterns = (_excludes.Text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), TextOptions = filtered ? textOptions : null, ShowFiltered = _showFilteredDirectories.IsChecked == true };
        var filter = await Task.Run(() => ResolveProjectFilter(filterPath, legacyFilter, listDependencies, token), token);
        options = options with { FileFilter = filter };
        var result = string.IsNullOrWhiteSpace(middle) ? await FolderComparisons.CompareAsync(left, right, options, token)
            : await FolderComparisons.CompareAsync(left, middle, right, options, token);
        DirectoryReadyForAdoption?.Invoke();
        token.ThrowIfCancellationRequested();
        if (!canAdopt() || _folderContextGeneration != folderGeneration
            || !ReferenceEquals(_directoryComparison, previousFolderModel)
            || !FolderConfigurationMatches(configuration) || FolderFilterDocumentStamp() != filterDocumentStamp) return false;
        var filterStamp = FolderFilterStamp(listDependencies, token);
        // 表示を破棄する前に投影と継承状態を準備し、取消と採用条件を最後に再確認する。
        var candidate = PrepareFolderCandidate(result, configuration, filterStamp, listDependencies, token);
        token.ThrowIfCancellationRequested();
        if (!canAdopt() || !FolderCandidateContextCurrent(candidate)) return false;
        prepareAdoption();
        BindDirectoryModel(candidate);
        return true;
    }

    private static Control BuildRow(DiffRow? row)
    {
        if (row is null) return new Border();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*,52,*"), MinHeight = 20 };
        // 本家WinMergeと同じく、行のある側を差分色（黄）、行の無い側を削除色（灰）で塗る。等しい行は塗らない。
        IBrush? SideBackground(bool hasLine) => row.Kind == DiffKind.Equal ? null
            : hasLine ? WinMergeColors.Difference : WinMergeColors.DifferenceDeleted;
        var sideBackgrounds = new[] { SideBackground(row.LeftText is not null), SideBackground(row.RightText is not null) };
        var values = new[] { row.LeftLineNumber?.ToString() ?? "", row.LeftText ?? "", row.RightLineNumber?.ToString() ?? "", row.RightText ?? "" };
        for (var i = 0; i < values.Length; i++)
        {
            var text = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Menlo, Consolas, monospace"), FontSize = 13, Margin = new Thickness(6, 1), TextTrimming = TextTrimming.CharacterEllipsis };
            if (i % 2 == 0) text.Foreground = Brushes.Gray;
            else if (sideBackgrounds[i / 2] is { } sideBackground)
            {
                // 差分の文字色は本家の既定（OPT_CLR_DIFF_TEXT）どおり黒。ダークテーマでも黄の上で読めるようにする。
                // Tag は選択中の差分ブロックを赤系へ塗り替えるときの目印（ComparisonPane.FileView.cs の ColorDiffRow）。
                var cell = new Border { Background = sideBackground, Tag = new DiffCellTag(sideBackground == WinMergeColors.Difference) };
                Grid.SetColumn(cell, i); grid.Children.Add(cell);
                text.Foreground = WinMergeColors.DifferenceText;
            }
            // 本家の左右ペインの境界線。右側の行番号欄の左端に縦線を引く。
            if (i == 2) { var divider = new Border { BorderBrush = WinMergeColors.FrameBorder, BorderThickness = new Thickness(1, 0, 0, 0) }; Grid.SetColumn(divider, 2); grid.Children.Add(divider); }
            var spans = i == 1 ? row.LeftSpans : i == 3 ? row.RightSpans : [];
            if (spans.Count == 0) text.Text = values[i];
            else
            {
                var offset = 0;
                foreach (var span in spans)
                {
                    text.Inlines!.Add(new Run(values[i][offset..span.Start]));
                    // 行内差分は本家と同じく背景色（OPT_CLR_WORDDIFF）で示し、文字色は行と同じにする。
                    text.Inlines.Add(new Run(values[i].Substring(span.Start, span.Length)) { Background = WinMergeColors.WordDifference });
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
internal sealed record ArchiveRetryResult(string? LeftPassword, string? RightPassword, int LeftNameCodePage = 28591, int RightNameCodePage = 28591,
    GZipPayloadKind LeftPayloadKind = GZipPayloadKind.Auto, GZipPayloadKind RightPayloadKind = GZipPayloadKind.Auto,
    CompressionPayloadKind LeftCompressionPayloadKind = CompressionPayloadKind.Auto, CompressionPayloadKind RightCompressionPayloadKind = CompressionPayloadKind.Auto);
internal sealed class ArchiveRetryDialog : Window
{
    internal TextBox LeftPassword { get; } = new() { PasswordChar = '●', MaxLength = 4096, PlaceholderText = "左のパスワード（任意）", Margin = new Thickness(0, 4) };
    internal TextBox RightPassword { get; } = new() { PasswordChar = '●', MaxLength = 4096, PlaceholderText = "右のパスワード（任意）", Margin = new Thickness(0, 4) };
    internal Button Retry { get; } = new() { Content = "再試行", Margin = new Thickness(4) };
    internal Button Cancel { get; } = new() { Content = "キャンセル", Margin = new Thickness(4) };
    internal ComboBox LeftNameCodePage { get; } = ArchiveNameSettings.Picker();
    internal ComboBox RightNameCodePage { get; } = ArchiveNameSettings.Picker();
    internal ComboBox LeftGZipPayloadKind { get; } = ArchivePayloadSettings.Picker();
    internal ComboBox RightGZipPayloadKind { get; } = ArchivePayloadSettings.Picker();
    internal ComboBox LeftCompressionPayloadKind { get; } = ArchivePayloadSettings.CompressionPicker();
    internal ComboBox RightCompressionPayloadKind { get; } = ArchivePayloadSettings.CompressionPicker();
    internal ArchiveRetryDialog(string? leftPassword, string? rightPassword, bool allowNameRetry = false, int leftNameCodePage = 28591, int rightNameCodePage = 28591,
        GZipPayloadKind leftPayloadKind = GZipPayloadKind.Auto, GZipPayloadKind rightPayloadKind = GZipPayloadKind.Auto,
        CompressionPayloadKind leftCompressionPayloadKind = CompressionPayloadKind.Auto, CompressionPayloadKind rightCompressionPayloadKind = CompressionPayloadKind.Auto)
    {
        Title = "アーカイブを開けませんでした"; Width = 480; Height = 520;
        CanResize = true; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        LeftPassword.Text = leftPassword; RightPassword.Text = rightPassword;
        var panel = new StackPanel { Margin = new Thickness(16) };
        var status = new TextBlock { Text = "読込みを検証できませんでした。パスワード、破損、形式を確認して再試行してください。", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(status); panel.Children.Add(LeftPassword); panel.Children.Add(RightPassword);
        LeftNameCodePage.SelectedItem = leftNameCodePage; RightNameCodePage.SelectedItem = rightNameCodePage;
        LeftGZipPayloadKind.SelectedItem = leftPayloadKind; RightGZipPayloadKind.SelectedItem = rightPayloadKind;
        panel.Children.Add(new TextBlock { Text = "左 gzip本文の形式" }); panel.Children.Add(LeftGZipPayloadKind);
        panel.Children.Add(new TextBlock { Text = "右 gzip本文の形式" }); panel.Children.Add(RightGZipPayloadKind);
        LeftCompressionPayloadKind.SelectedItem = leftCompressionPayloadKind; RightCompressionPayloadKind.SelectedItem = rightCompressionPayloadKind;
        panel.Children.Add(new TextBlock { Text = "左 BZip2／Z本文の形式" }); panel.Children.Add(LeftCompressionPayloadKind);
        panel.Children.Add(new TextBlock { Text = "右 BZip2／Z本文の形式" }); panel.Children.Add(RightCompressionPayloadKind);
        if (allowNameRetry)
        {
            status.Text = "gzip格納名を復号できません。28591はRFC1952 Latin1、65001はUTF-8、932は日本語です。左右の格納名文字コードを選択してください。";
            panel.Children.Add(new TextBlock { Text = "左 gzip格納名の文字コード" }); panel.Children.Add(LeftNameCodePage);
            panel.Children.Add(new TextBlock { Text = "右 gzip格納名の文字コード" }); panel.Children.Add(RightNameCodePage);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Cancel); buttons.Children.Add(Retry);
        var root = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = panel }); Content = root;
        Cancel.Click += (_, _) => Close();
        Retry.Click += (_, _) =>
        {
            if (LeftPassword.Text?.Length > 4096 || RightPassword.Text?.Length > 4096) { status.Text = "パスワードは左右それぞれ4096文字以下で入力してください。"; return; }
            Close(new ArchiveRetryResult(string.IsNullOrEmpty(LeftPassword.Text) ? null : LeftPassword.Text,
                string.IsNullOrEmpty(RightPassword.Text) ? null : RightPassword.Text,
                ArchiveNameSettings.Selected(LeftNameCodePage), ArchiveNameSettings.Selected(RightNameCodePage),
                ArchivePayloadSettings.Selected(LeftGZipPayloadKind), ArchivePayloadSettings.Selected(RightGZipPayloadKind),
                ArchivePayloadSettings.SelectedCompression(LeftCompressionPayloadKind), ArchivePayloadSettings.SelectedCompression(RightCompressionPayloadKind)));
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
        var dialog = new Window { Title = UiText.Say(title), Width = 470, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 20 };
        panel.Children.Add(new TextBlock { Text = UiText.Say(message), TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        if (confirm) { var cancel = new Button { Content = UiText.Phrase("Cancel") }; cancel.Click += (_, _) => dialog.Close(false); buttons.Children.Add(cancel); }
        var ok = new Button { Content = confirm ? UiText.Say("続行") : UiText.Phrase("&Close") }; ok.Click += (_, _) => dialog.Close(true); buttons.Children.Add(ok);
        panel.Children.Add(buttons); dialog.Content = panel;
        var result = dialog.ShowDialog<bool>(owner);
        if (confirm) ConfirmationShown?.Invoke(dialog);
        return result;
    }
}
