using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    private readonly DockPanel _folderView = new();
    private readonly CheckBox _showFilteredDirectories = new() { Content = "除外項目を表示" };
    private readonly TextBlock _folderCopySummary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly List<Button> _folderCopyButtons = [];
    private readonly List<Control> _folderTwoWayControls = [];
    private readonly ComboBox _folderDirection = new()
    {
        Name = "FolderCopyDirection", ItemsSource = new[] { "左 → 右", "右 → 左", "左 → 中央", "中央 → 左", "中央 → 右", "右 → 中央" },
        SelectedIndex = 0, MinWidth = 160, Margin = new Thickness(4), IsVisible = false
    };
    private readonly List<Control> _folderThreeWayControls = [];
    private DirectoryComparisonResult? _directoryComparison;
    // headless raw証拠: 比較モデルの実referenceだけを読み出す。
    internal object? FolderOptionsAdoptedModel => _directoryComparison;
    internal ListBox FolderOptionsRawRows => _directoryList;
    internal CheckBox FolderOptionsRawMode => _folderTreeMode;
    internal ComboBox FolderOptionsRawPolicy => _folderInitialExpansion;
    private CancellationTokenSource? _folderCopyOperation, _folderRefreshOperation;
    private long _folderContextGeneration;
    private bool _folderCopyBusy, _folderModelStale;
    private FolderConfiguration? _directoryConfiguration;
    private string? _directoryFilterStamp;
    private FolderFilterListState? _folderFilterListState;
    internal Action<string, int>? FolderListReadProgress { get; set; }
    internal IReadOnlyList<string> FolderListDependencyPaths => _folderFilterListState?.Paths ?? [];
    internal Task? PendingFolderCopy { get; private set; }
    internal Task? PendingFolderMove { get; private set; }
    internal Task? PendingFolderTrash { get; private set; }
    internal FolderCopyResult? LastFolderCopyResult { get; private set; }
    internal FolderMoveResult? LastFolderMoveResult { get; private set; }
    internal FolderTrashResult? LastFolderTrashResult { get; private set; }
    internal string FolderCopySummaryText => _folderCopySummary.Text ?? "";
    internal string FolderCopyStatusText => _status.Text ?? "";
    internal Action<FolderCopyPlan>? FolderPlanReady { get; set; }
    internal Action<FolderCopyPlan>? FolderExecutionStarting { get; set; }
    internal Action<string>? FolderOutputChecking { get; set; }
    internal Action<string>? FolderPublicationStarting { get; set; }
    internal Action<string, Action>? FolderPublicationCriticalSection { get; set; }
    internal Action<FolderMovePlan>? FolderMovePlanReady { get; set; }
    internal Action<FolderMovePlan>? FolderMoveExecutionStarting { get; set; }
    internal Action<string>? FolderMoveSourceChecking { get; set; }
    internal Action<string>? FolderMoveDestinationChecking { get; set; }
    internal Action<FolderTrashPlan>? FolderTrashPlanReady { get; set; }
    internal Action<FolderTrashPlan>? FolderTrashExecutionStarting { get; set; }
    internal Action<string>? FolderTrashSourceChecking { get; set; }
    private FolderCopyLimits? _folderCopyVerificationLimits;
    internal FolderCopyLimits? FolderCopyVerificationLimits
    {
        get => _folderCopyVerificationLimits;
        set
        {
            if (Equals(_folderCopyVerificationLimits, value)) return;
            _folderCopyVerificationLimits = value is null ? null : value with { };
            InvalidateFolderCopy(false);
        }
    }

    private sealed record FolderConfiguration(object Values, SubstitutionRule[] Rules);

    private FolderConfiguration CaptureFolderConfiguration()
    {
        var options = Options();
        return new((LeftPath.Text, BasePath.Text, RightPath.Text, _mode.SelectedIndex, _provider.SelectedItem,
            _recursive.IsChecked, _folderMode.SelectedIndex, _excludes.Text, _fileFilter.Text, _showFilteredDirectories.IsChecked,
            options.IgnoreCase, options.IgnoreWhitespace, options.IgnoreBlankLines, options.IgnoreNumbers,
            options.CommentSyntax, options.Whitespace, options.IgnoreLinePattern, _projectMetadata.LegacyFilter,
            _projectMetadata.LeftReadOnly, _projectMetadata.BaseReadOnly, _projectMetadata.RightReadOnly),
            options.SubstitutionRules.ToArray());
    }

    private bool FolderConfigurationMatches(FolderConfiguration captured)
    {
        var current = CaptureFolderConfiguration();
        return captured.Values.Equals(current.Values) && captured.Rules.SequenceEqual(current.Rules);
    }

    private string? FolderFilterDocumentStamp()
    {
        if (string.IsNullOrWhiteSpace(_fileFilter.Text)) return null;
        if (new FileInfo(_fileFilter.Text).Length > 1_048_576) throw new InvalidDataException("フィルターは1 MiB以下にしてください。");
        var bytes = File.ReadAllBytes(_fileFilter.Text);
        if (bytes.Length > 1_048_576) throw new InvalidDataException("フィルターは1 MiB以下にしてください。");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private string? FolderFilterStamp(FolderFilterListState? dependencies = null, CancellationToken token = default)
    {
        var filter = FolderFilterDocumentStamp();
        var state = dependencies ?? _folderFilterListState;
        return state is null || state.Paths.Count == 0 ? filter : (filter ?? "") + ":" + state.CurrentStamp(token);
    }

    private void InitializeFolderView()
    {
        _directoryList.SelectionMode = SelectionMode.Multiple;
        _directoryList.SelectionChanged += (_, _) => InvalidateFolderCopy(false);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
        Add("← 選択をすべてコピー", false, DirectoryCopyMode.All);
        Add("選択をすべてコピー →", true, DirectoryCopyMode.All);
        Add("← 選択の差分をコピー", false, DirectoryCopyMode.DifferencesOnly);
        Add("選択の差分をコピー →", true, DirectoryCopyMode.DifferencesOnly);
        AddMove("← 選択ファイルを移動", false);
        AddMove("選択ファイルを移動 →", true);
        AddTrash("左側の差分ファイルをゴミ箱へ", DirectoryCopyDirection.LeftToRight);
        AddTrash("右側の差分ファイルをゴミ箱へ", DirectoryCopyDirection.RightToLeft);
        actions.Children.Add(_folderDirection); _folderThreeWayControls.Add(_folderDirection);
        AddThreeWay("選択をすべてコピー", DirectoryCopyMode.All);
        AddThreeWay("選択の差分をコピー", DirectoryCopyMode.DifferencesOnly);
        AddThreeWayMove("選択ファイルを移動");
        AddThreeWayTrash("選択側の差分ファイルをゴミ箱へ");
        _folderDirection.SelectionChanged += (_, _) => InvalidateFolderCopy(false);
        InitializeFolderTree(actions);
        var folderActionsScroll = new ScrollViewer { Content = actions, MaxHeight = 144,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        DockPanel.SetDock(folderActionsScroll, Dock.Top); _folderView.Children.Add(folderActionsScroll);
        DockPanel.SetDock(_folderCopySummary, Dock.Bottom); _folderView.Children.Add(_folderCopySummary);
        // 本家の DirView と同じく、一覧の直上にルートパス帯と列見出しを置く。
        InitializeFolderListChrome();
        DockPanel.SetDock(_folderRootBars, Dock.Top); _folderView.Children.Add(_folderRootBars);
        DockPanel.SetDock(_folderColumnHeaderHost, Dock.Top); _folderView.Children.Add(_folderColumnHeaderHost);
        _folderView.Children.Add(_directoryList);
        void UpdateFolderActionsHeight()
        {
            if (_folderView.Bounds.Height <= 0) return;
            // 一覧の最小高さ100 DIPは、パス帯と列見出しを除いた本体で確保する。
            var reservedHeight = _directoryList.MinHeight + _directoryList.Margin.Top + _directoryList.Margin.Bottom
                + _folderRootBars.Bounds.Height + _folderColumnHeaderHost.Bounds.Height
                + _folderCopySummary.Bounds.Height + _folderCopySummary.Margin.Top + _folderCopySummary.Margin.Bottom;
            var maxHeight = Math.Min(144, Math.Max(0, _folderView.Bounds.Height - reservedHeight));
            if (Math.Abs(folderActionsScroll.MaxHeight - maxHeight) > .1) folderActionsScroll.MaxHeight = maxHeight;
        }
        _folderView.SizeChanged += (_, _) => UpdateFolderActionsHeight();
        _folderCopySummary.SizeChanged += (_, _) => UpdateFolderActionsHeight();
        foreach (var control in new Control[] { LeftPath, BasePath, RightPath, _mode, _provider, _recursive, _folderMode,
            _excludes, _fileFilter, _showFilteredDirectories, _ignoreCase, _ignoreSpace, _ignoreBlank, _ignoreRegex,
            _ignoreNumbers, _comments, _whitespace })
            control.PropertyChanged += (_, args) =>
            {
                if (args.Property == TextBox.TextProperty || args.Property == ComboBox.SelectedIndexProperty
                    || args.Property == ComboBox.SelectedItemProperty || args.Property == CheckBox.IsCheckedProperty)
                    InvalidateFolderCopy(true);
            };

        void Add(string title, bool toRight, DirectoryCopyMode mode)
        {
            var button = new Button { Content = title, Margin = new Thickness(4) };
            button.Click += async (_, _) =>
            {
                var task = CopyFolderSelectionAsync(toRight, mode);
                PendingFolderCopy = task;
                try { await task; } finally { if (ReferenceEquals(PendingFolderCopy, task)) PendingFolderCopy = null; }
            };
            _folderCopyButtons.Add(button); _folderTwoWayControls.Add(button); actions.Children.Add(button);
        }

        void AddThreeWay(string title, DirectoryCopyMode mode)
        {
            var button = new Button { Content = title, Margin = new Thickness(4), IsVisible = false };
            button.Click += async (_, _) =>
            {
                if (_folderDirection.SelectedIndex is < 0 or > 5) return;
                var task = CopyFolderDirectionAsync((DirectoryCopyDirection)_folderDirection.SelectedIndex, mode);
                PendingFolderCopy = task;
                try { await task; } finally { if (ReferenceEquals(PendingFolderCopy, task)) PendingFolderCopy = null; }
            };
            _folderCopyButtons.Add(button); _folderThreeWayControls.Add(button); actions.Children.Add(button);
        }

        void AddMove(string title, bool toRight)
        {
            var button = new Button { Content = title, Margin = new Thickness(4) };
            button.Click += async (_, _) =>
            {
                var task = MoveFolderDirectionAsync(toRight ? DirectoryCopyDirection.LeftToRight : DirectoryCopyDirection.RightToLeft);
                PendingFolderMove = task;
                try { await task; } finally { if (ReferenceEquals(PendingFolderMove, task)) PendingFolderMove = null; }
            };
            _folderCopyButtons.Add(button); _folderTwoWayControls.Add(button); actions.Children.Add(button);
        }

        void AddThreeWayMove(string title)
        {
            var button = new Button { Content = title, Margin = new Thickness(4), IsVisible = false };
            button.Click += async (_, _) =>
            {
                if (_folderDirection.SelectedIndex is < 0 or > 5) return;
                var task = MoveFolderDirectionAsync((DirectoryCopyDirection)_folderDirection.SelectedIndex);
                PendingFolderMove = task;
                try { await task; } finally { if (ReferenceEquals(PendingFolderMove, task)) PendingFolderMove = null; }
            };
            _folderCopyButtons.Add(button); _folderThreeWayControls.Add(button); actions.Children.Add(button);
        }

        void AddTrash(string title, DirectoryCopyDirection direction)
        {
            var button = new Button { Content = title, Margin = new Thickness(4) };
            button.Click += async (_, _) =>
            {
                var task = TrashFolderDirectionAsync(direction);
                PendingFolderTrash = task;
                try { await task; } finally { if (ReferenceEquals(PendingFolderTrash, task)) PendingFolderTrash = null; }
            };
            _folderCopyButtons.Add(button); _folderTwoWayControls.Add(button); actions.Children.Add(button);
        }

        void AddThreeWayTrash(string title)
        {
            var button = new Button { Content = title, Margin = new Thickness(4), IsVisible = false };
            button.Click += async (_, _) =>
            {
                if (_folderDirection.SelectedIndex is < 0 or > 5) return;
                var task = TrashFolderDirectionAsync((DirectoryCopyDirection)_folderDirection.SelectedIndex);
                PendingFolderTrash = task;
                try { await task; } finally { if (ReferenceEquals(PendingFolderTrash, task)) PendingFolderTrash = null; }
            };
            _folderCopyButtons.Add(button); _folderThreeWayControls.Add(button); actions.Children.Add(button);
        }
    }

    internal void InvalidateFolderCopy(bool markStale = true)
    {
        _folderContextGeneration++;
        _folderCopyOperation?.Cancel(); _folderRefreshOperation?.Cancel();
        if (markStale && _directoryComparison is not null) _folderModelStale = true;
        UpdateFolderTreeControls();
    }

    internal void CancelInactiveFolderCopy(bool active)
    {
        if (!active && (_folderCopyOperation is not null || _folderRefreshOperation is not null)) InvalidateFolderCopy(false);
    }

    private void StopFolderCopy()
    {
        var running = _folderCopyOperation is not null || _folderRefreshOperation is not null;
        // 同じ比較での中止完了は表示する。比較・選択・設定変更の世代更新は別途維持する。
        _folderCopyOperation?.Cancel(); _folderRefreshOperation?.Cancel();
        if (running && FolderOwnerCurrent() && ReferenceEquals(_specialTab.Content, _folderView))
            _status.Text = "フォルダーコピーを中止しています…";
    }

    internal void EnsureFolderOptionsOutputWritable(string path)
    {
        EnsureProjectOutputWritable(path);
        foreach (var source in CurrentImageProtectedPaths().Concat(new[] { _directoryLeft, _directoryMiddle, _directoryRight }))
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            if (ArchivePaths.SameFile(source, path)) throw new InvalidOperationException("フォルダー表示設定で比較入力やworkspaceを上書きできません。");
            if (!FolderComparisons.IsDirectory(source)) continue;
            for (var current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current))
                if (ArchivePaths.SameFile(source, current)) throw new InvalidOperationException("比較入力フォルダー内へ表示設定を保存できません。");
        }
    }
    private void BindDirectoryModel(FolderDirectoryCandidate candidate)
    {
        var result = candidate.Model;
        InvalidateFolderCopy();
        _directoryLeft = result.LeftPath; _directoryMiddle = result.MiddlePath; _directoryRight = result.RightPath;
        _directoryComparison = result; _directoryConfiguration = candidate.Configuration; _directoryFilterStamp = candidate.FilterStamp;
        foreach (var control in _folderTwoWayControls) control.IsVisible = !result.IsThreeWay;
        foreach (var control in _folderThreeWayControls) control.IsVisible = result.IsThreeWay;
        AdoptFolderTreeProjection(candidate.Projection, candidate.Selection);
        _folderModelStale = false; _folderCopySummary.Text = "Ctrl / Shiftで複数の項目を選択できます。";
        SetSpecialView(_folderView); _views.SelectedItem = _specialTab;
        UpdateFolderTreeControls();
        _status.Text = $"フォルダー比較: {result.Entries.Count} 件";
        // 投影・表示の採用が完了するまで、前モデルの依存保護を保持する。
        _folderFilterListState = candidate.ListDependencies;
    }

    private bool FolderOwnerCurrent() => !_disposed && (_owner is not MainWindow window
        || window.SessionPanes.Contains(this) && ReferenceEquals(window.ActivePane, this));

    private Task CopyFolderSelectionAsync(bool toRight, DirectoryCopyMode mode)
        => CopyFolderDirectionAsync(toRight ? DirectoryCopyDirection.LeftToRight : DirectoryCopyDirection.RightToLeft, mode);

    private async Task CopyFolderDirectionAsync(DirectoryCopyDirection direction, DirectoryCopyMode mode)
    {
        var model = _directoryComparison;
        var generation = _folderContextGeneration;
        try { await CopyFolderSelectionCoreAsync(direction, mode); }
        catch (OperationCanceledException error)
        {
            if (CanReport())
            {
                var detail = FolderOperations.DescribeFailure(error);
                _status.Text = "フォルダーコピーを中止しました。" + (detail == error.Message ? "" : " · " + detail);
            }
        }
        catch (Exception error)
        {
            if (CanReport())
            {
                var detail = FolderOperations.DescribeFailure(error);
                _status.Text = detail;
                await Dialogs.MessageAsync(_owner, "コピーを完了できませんでした", detail);
            }
        }

        bool CanReport() => FolderOwnerCurrent() && ReferenceEquals(_specialTab.Content, _folderView)
            && ReferenceEquals(_directoryComparison, model) && _folderContextGeneration == generation;
    }

    private async Task MoveFolderDirectionAsync(DirectoryCopyDirection direction)
    {
        var model = _directoryComparison;
        var generation = _folderContextGeneration;
        try { await MoveFolderSelectionCoreAsync(direction); }
        catch (OperationCanceledException error)
        {
            if (CanReport())
            {
                var detail = FolderOperations.DescribeFailure(error);
                _status.Text = "フォルダー移動を中止しました。" + (detail == error.Message ? "" : " · " + detail);
            }
        }
        catch (Exception error)
        {
            if (CanReport())
            {
                var detail = FolderOperations.DescribeFailure(error);
                _status.Text = detail;
                await Dialogs.MessageAsync(_owner, "移動を完了できませんでした", detail);
            }
        }

        bool CanReport() => FolderOwnerCurrent() && ReferenceEquals(_specialTab.Content, _folderView)
            && ReferenceEquals(_directoryComparison, model) && _folderContextGeneration == generation;
    }

    private async Task TrashFolderDirectionAsync(DirectoryCopyDirection direction)
    {
        var model = _directoryComparison;
        var generation = _folderContextGeneration;
        try { await TrashFolderSelectionCoreAsync(direction); }
        catch (OperationCanceledException error)
        {
            if (CanReport())
            {
                var detail = FolderOperations.DescribeFailure(error);
                _status.Text = "ごみ箱への移動を中止しました。" + (detail == error.Message ? "" : " · " + detail);
            }
        }
        catch (Exception error)
        {
            if (CanReport())
            {
                var detail = FolderOperations.DescribeFailure(error);
                _status.Text = detail;
                await Dialogs.MessageAsync(_owner, "ごみ箱へ移せませんでした", detail);
            }
        }

        bool CanReport() => FolderOwnerCurrent() && ReferenceEquals(_specialTab.Content, _folderView)
            && ReferenceEquals(_directoryComparison, model) && _folderContextGeneration == generation;
    }

    private async Task MoveFolderSelectionCoreAsync(DirectoryCopyDirection direction)
    {
        if (_folderCopyBusy) throw new InvalidOperationException("フォルダー操作を実行中です。");
        var (sourceSide, destinationSide) = DirectorySideMapping.GetSides(direction);
        EnsureFolderSideWritable(sourceSide);
        EnsureFolderSideWritable(destinationSide);
        var model = _directoryComparison ?? throw new InvalidOperationException("先にフォルダーを比較してください。");
        var configuration = _directoryConfiguration!;
        if (_folderModelStale || !FolderConfigurationMatches(configuration) || FolderFilterStamp() != _directoryFilterStamp)
            throw new InvalidOperationException("比較条件や入力が変わっています。フォルダーを再比較してください。");
        var selected = _directoryList.SelectedItems?.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).ToArray() ?? [];
        var selection = DirectoryCopyPlanner.Create(model, selected, direction, DirectoryCopyMode.All);
        using var operation = new CancellationTokenSource();
        _folderCopyOperation = operation;
        LastFolderMoveResult = null;
        var generation = _folderContextGeneration;
        SetBusy(true);
        try
        {
            ValidateFilter();
            _status.Text = "移動対象の通常ファイルを確認しています…";
            var plan = await FolderOperations.PrepareMoveAsync(selection, FolderCopyVerificationLimits, operation.Token);
            ValidateFilter(); FolderMovePlanReady?.Invoke(plan); ValidateFilter();
            foreach (var entry in plan.CopyPlan.Entries)
            {
                operation.Token.ThrowIfCancellationRequested();
                EnsureFolderSourceMovable(entry.SourcePath, plan.CopyPlan.SourceRoot, sourceSide);
                EnsureFolderDestinationWritable(entry.DestinationPath, plan.CopyPlan.DestinationRoot, destinationSide);
            }
            ValidateFilter();
            if (plan.CopyPlan.Entries.Count == 0) { _status.Text = "移動条件に合う項目がありません。"; return; }
            var names = string.Join("\n", selected.Take(8));
            if (!await Dialogs.ConfirmAsync(_owner, "フォルダー内の移動",
                $"{names}{(selected.Length > 8 ? "\n…" : "")}\n\n{plan.CopyPlan.Entries.Count}件・{plan.CopyPlan.LogicalBytes:N0} bytesを{FolderSideName(destinationSide)}へ移動します。\n通常ファイルのみ・同一ボリューム限定です。既存ファイルは上書きしません。"))
            { ValidateFilter(); _status.Text = "移動を取り消しました。"; return; }
            ValidateFilter(); FolderMoveExecutionStarting?.Invoke(plan); ValidateFilter();
            _status.Text = "フォルダーを移動しています…";
            var result = await FolderOperations.ExecuteMoveAsync(plan, path => OnUi(() =>
            {
                ValidateFilter(); FolderMoveSourceChecking?.Invoke(path); ValidateFilter();
                EnsureFolderSourceMovable(path, plan.CopyPlan.SourceRoot, sourceSide);
            }), path => OnUi(() =>
            {
                ValidateFilter(); FolderMoveDestinationChecking?.Invoke(path); ValidateFilter();
                EnsureFolderDestinationWritable(path, plan.CopyPlan.DestinationRoot, destinationSide);
            }), () => OnUi(ValidateFilter), operation.Token);
            LastFolderMoveResult = result;
            DirectoryComparisonResult? resultModel = model;
            var resultGeneration = generation;
            var summary = $"移動結果: 移動 {result.MovedCount} 件 / 全 {result.Entries.Count} 件"
                + (result.Reason is { } reason ? " · " + reason : "");
            if (result.MutationOccurred)
            {
                if (ReferenceEquals(_directoryComparison, model)) _folderModelStale = true;
                var refreshGeneration = _folderContextGeneration;
                using var refresh = new CancellationTokenSource(); _folderRefreshOperation = refresh;
                bool CanRefresh() => FolderOwnerCurrent() && ReferenceEquals(_directoryComparison, model)
                    && ReferenceEquals(_specialTab.Content, _folderView) && _folderContextGeneration == refreshGeneration
                    && FolderConfigurationMatches(configuration) && FolderFilterStamp() == _directoryFilterStamp;
                try
                {
                    if (CanRefresh() && await CompareDirectoryAsync(model.LeftPath, model.RightPath, refresh.Token, CanRefresh, () => { }, model.MiddlePath))
                    { resultModel = _directoryComparison; resultGeneration = _folderContextGeneration; }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException)
                { summary += " · 再比較を完了できませんでした: " + error.Message; }
                finally { if (ReferenceEquals(_folderRefreshOperation, refresh)) _folderRefreshOperation = null; }
            }
            if (FolderOwnerCurrent() && ReferenceEquals(_specialTab.Content, _folderView)
                && ReferenceEquals(_directoryComparison, resultModel) && _folderContextGeneration == resultGeneration
                && FolderConfigurationMatches(configuration))
            { _folderCopySummary.Text = summary; _status.Text = summary; }
        }
        finally
        {
            if (ReferenceEquals(_folderCopyOperation, operation)) _folderCopyOperation = null;
            SetBusy(false);
        }

        void Validate()
        {
            operation.Token.ThrowIfCancellationRequested();
            if (!FolderOwnerCurrent() || _folderModelStale || _folderContextGeneration != generation
                || !ReferenceEquals(_directoryComparison, model) || !ReferenceEquals(_specialTab.Content, _folderView)
                || !FolderConfigurationMatches(configuration))
                throw new OperationCanceledException("移動中に比較・選択・設定が変わったため中止しました。", operation.Token);
            EnsureFolderSideWritable(sourceSide);
            EnsureFolderSideWritable(destinationSide);
        }
        void ValidateFilter()
        {
            Validate();
            if (FolderFilterStamp() != _directoryFilterStamp)
                throw new OperationCanceledException("移動中にフィルターが変わったため中止しました。", operation.Token);
        }
        void SetBusy(bool busy)
        {
            _folderCopyBusy = busy; _directoryList.IsEnabled = !busy;
            foreach (var button in _folderCopyButtons) button.IsEnabled = !busy;
            _folderDirection.IsEnabled = !busy;
            UpdateFolderTreeControls();
        }
        static void OnUi(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess()) action();
            else Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult();
        }
    }

    private async Task TrashFolderSelectionCoreAsync(DirectoryCopyDirection direction)
    {
        if (_folderCopyBusy) throw new InvalidOperationException("フォルダー操作を実行中です。");
        var (sourceSide, _) = DirectorySideMapping.GetSides(direction);
        EnsureFolderSideWritable(sourceSide);
        var model = _directoryComparison ?? throw new InvalidOperationException("先にフォルダーを比較してください。");
        var configuration = _directoryConfiguration!;
        if (_folderModelStale || !FolderConfigurationMatches(configuration) || FolderFilterStamp() != _directoryFilterStamp)
            throw new InvalidOperationException("比較条件や入力が変わっています。フォルダーを再比較してください。");
        var selected = _directoryList.SelectedItems?.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).ToArray() ?? [];
        var selection = DirectoryCopyPlanner.Create(model, selected, direction, DirectoryCopyMode.DifferencesOnly);
        using var operation = new CancellationTokenSource();
        _folderCopyOperation = operation;
        LastFolderTrashResult = null;
        var generation = _folderContextGeneration;
        SetBusy(true);
        try
        {
            ValidateFilter();
            _status.Text = "差分ファイルの内容を確認しています…";
            var plan = await FolderOperations.PrepareTrashAsync(selection, FolderCopyVerificationLimits, operation.Token);
            ValidateFilter(); FolderTrashPlanReady?.Invoke(plan); ValidateFilter();
            foreach (var entry in plan.Entries.Where(entry => entry.IsSupported))
            {
                operation.Token.ThrowIfCancellationRequested();
                EnsureFolderSourceMovable(entry.SourcePath, plan.SourceRoot, sourceSide);
            }
            ValidateFilter();
            if (plan.SupportedCount == 0)
            {
                _status.Text = plan.UnsupportedCount == 0
                    ? "選択範囲に削除できる差分ファイルはありません。"
                    : $"通常ファイルの差分はありません。ファイル以外 {plan.UnsupportedCount} 件は対象外のまま残しました。";
                return;
            }
            var names = string.Join("\n", selected.Take(8));
            if (!await Dialogs.ConfirmAsync(_owner, "差分ファイルをゴミ箱へ移動",
                $"{names}{(selected.Length > 8 ? "\n…" : "")}\n\n{plan.SupportedCount}件・{plan.LogicalBytes:N0} bytesを{FolderSideName(sourceSide)}側からOSのごみ箱へ移します。\n通常ファイルだけが対象です。ディレクトリとリンクは対象外です。\nごみ箱へ移せない場合、完全削除へ切り替えずに中止します。"))
            { ValidateFilter(); _status.Text = "ごみ箱への移動を取り消しました。"; return; }
            ValidateFilter(); FolderTrashExecutionStarting?.Invoke(plan); ValidateFilter();
            _status.Text = "差分ファイルをごみ箱へ移しています…";
            var result = await FolderOperations.ExecuteTrashAsync(plan, path => OnUi(() =>
            {
                ValidateFilter(); FolderTrashSourceChecking?.Invoke(path); ValidateFilter();
                EnsureFolderSourceMovable(path, plan.SourceRoot, sourceSide);
            }), () => OnUi(ValidateFilter), operation.Token);
            LastFolderTrashResult = result;
            DirectoryComparisonResult? resultModel = model;
            var resultGeneration = generation;
            var summary = $"ごみ箱への移動結果: 移動 {result.TrashedCount} 件 / 対象 {plan.SupportedCount} 件"
                + (result.UnsupportedCount == 0 ? "" : $" / 対象外 {result.UnsupportedCount} 件")
                + (result.Reason is { } reason ? " · " + reason : "");
            if (result.MutationOccurred)
            {
                if (ReferenceEquals(_directoryComparison, model)) _folderModelStale = true;
                var refreshGeneration = _folderContextGeneration;
                using var refresh = new CancellationTokenSource(); _folderRefreshOperation = refresh;
                bool CanRefresh() => FolderOwnerCurrent() && ReferenceEquals(_directoryComparison, model)
                    && ReferenceEquals(_specialTab.Content, _folderView) && _folderContextGeneration == refreshGeneration
                    && FolderConfigurationMatches(configuration) && FolderFilterStamp() == _directoryFilterStamp;
                try
                {
                    if (CanRefresh() && await CompareDirectoryAsync(model.LeftPath, model.RightPath, refresh.Token, CanRefresh, () => { }, model.MiddlePath))
                    { resultModel = _directoryComparison; resultGeneration = _folderContextGeneration; }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException)
                { summary += " · 再比較を完了できませんでした: " + error.Message; }
                finally { if (ReferenceEquals(_folderRefreshOperation, refresh)) _folderRefreshOperation = null; }
            }
            if (FolderOwnerCurrent() && ReferenceEquals(_specialTab.Content, _folderView)
                && ReferenceEquals(_directoryComparison, resultModel) && _folderContextGeneration == resultGeneration
                && FolderConfigurationMatches(configuration))
            { _folderCopySummary.Text = summary; _status.Text = summary; }
        }
        finally
        {
            if (ReferenceEquals(_folderCopyOperation, operation)) _folderCopyOperation = null;
            SetBusy(false);
        }

        void Validate()
        {
            operation.Token.ThrowIfCancellationRequested();
            if (!FolderOwnerCurrent() || _folderModelStale || _folderContextGeneration != generation
                || !ReferenceEquals(_directoryComparison, model) || !ReferenceEquals(_specialTab.Content, _folderView)
                || !FolderConfigurationMatches(configuration))
                throw new OperationCanceledException("ごみ箱への移動中に比較・選択・設定が変わったため中止しました。", operation.Token);
            EnsureFolderSideWritable(sourceSide);
        }
        void ValidateFilter()
        {
            Validate();
            if (FolderFilterStamp() != _directoryFilterStamp)
                throw new OperationCanceledException("ごみ箱への移動中にフィルターが変わったため中止しました。", operation.Token);
        }
        void SetBusy(bool busy)
        {
            _folderCopyBusy = busy; _directoryList.IsEnabled = !busy;
            foreach (var button in _folderCopyButtons) button.IsEnabled = !busy;
            _folderDirection.IsEnabled = !busy;
            UpdateFolderTreeControls();
        }
        static void OnUi(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess()) action();
            else Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult();
        }
    }

    private async Task CopyFolderSelectionCoreAsync(DirectoryCopyDirection direction, DirectoryCopyMode mode)
    {
        if (_folderCopyBusy) throw new InvalidOperationException("フォルダーコピーを実行中です。");
        var (_, destinationSide) = DirectorySideMapping.GetSides(direction);
        EnsureFolderSideWritable(destinationSide);
        var model = _directoryComparison ?? throw new InvalidOperationException("先にフォルダーを比較してください。");
        var configuration = _directoryConfiguration!;
        if (_folderModelStale || !FolderConfigurationMatches(configuration) || FolderFilterStamp() != _directoryFilterStamp)
            throw new InvalidOperationException("比較条件や入力が変わっています。フォルダーを再比較してください。");
        var selected = _directoryList.SelectedItems?.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).ToArray() ?? [];
        var selection = DirectoryCopyPlanner.Create(model, selected, direction, mode);
        using var operation = new CancellationTokenSource();
        _folderCopyOperation = operation;
        LastFolderCopyResult = null;
        var generation = _folderContextGeneration;
        SetBusy(true);
        try
        {
            ValidateFilter();
            _status.Text = "コピー対象を確認しています…";
            var plan = await FolderOperations.PrepareAsync(selection, FolderCopyVerificationLimits, operation.Token);
            ValidateFilter(); FolderPlanReady?.Invoke(plan); ValidateFilter();
            if (model.IsThreeWay)
            {
                // 第三入力とコピー元の保護は確認前にも判定し、公開直前の全タブ検査も維持する。
                var protectedRoots = Enum.GetValues<DirectorySide>().Where(side => side != destinationSide)
                    .Select(side => DirectorySideMapping.GetRoot(model, side)).ToArray();
                foreach (var entry in plan.Entries)
                {
                    operation.Token.ThrowIfCancellationRequested();
                    var protection = FolderPathProtection.CreateContext();
                    if (protectedRoots.Any(root => protection.Within(entry.DestinationPath, root)))
                        throw new InvalidOperationException("コピー先以外の比較フォルダーへは書き込めません。");
                }
                ValidateFilter();
            }
            if (plan.Entries.Count == 0) { _status.Text = "コピー条件に合う項目がありません。"; return; }
            var names = string.Join("\n", selected.Take(8));
            if (!await Dialogs.ConfirmAsync(_owner, "フォルダー内のコピー",
                $"{names}{(selected.Length > 8 ? "\n…" : "")}\n\n{plan.Entries.Count}項目・{plan.LogicalBytes:N0} bytesを{FolderSideName(destinationSide)}へコピーします。\n既存ファイルは上書きされます。"))
            { ValidateFilter(); _status.Text = "コピーを取り消しました。"; return; }
            ValidateFilter(); FolderExecutionStarting?.Invoke(plan); ValidateFilter();
            _status.Text = "フォルダーをコピーしています…";
            var result = await FolderOperations.ExecuteAsync(plan, path => OnUi(() =>
            {
                ValidateFilter(); FolderOutputChecking?.Invoke(path); ValidateFilter();
                EnsureFolderDestinationWritable(path, plan.DestinationRoot, destinationSide);
            }), () => OnUi(Validate), operation.Token, (path, publish) => OnUi(() =>
            {
                FolderPublicationStarting?.Invoke(path);
                ValidateFilter();
                EnsureFolderDestinationWritable(path, plan.DestinationRoot, destinationSide);
                if (FolderPublicationCriticalSection is { } critical) critical(path, publish);
                else publish();
            }));
            LastFolderCopyResult = result;
            DirectoryComparisonResult? resultModel = model;
            var resultGeneration = generation;
            var summary = $"コピー結果: 公開 {result.PublishedCount} 件 / 全 {result.Entries.Count} 件"
                + (result.Reason is { } reason ? " · " + reason : "");
            foreach (var entry in result.Entries)
                if (!string.IsNullOrEmpty(entry.CleanupFailure))
                    summary += $" · {entry.RelativePath}: 一時ファイルの清掃またはハンドル終了を確認できませんでした: {entry.CleanupFailure}";
            if (result.MutationOccurred)
            {
                if (ReferenceEquals(_directoryComparison, model)) _folderModelStale = true;
                var refreshGeneration = _folderContextGeneration;
                using var refresh = new CancellationTokenSource(); _folderRefreshOperation = refresh;
                bool CanRefresh() => FolderOwnerCurrent() && ReferenceEquals(_directoryComparison, model)
                    && ReferenceEquals(_specialTab.Content, _folderView) && _folderContextGeneration == refreshGeneration
                    && FolderConfigurationMatches(configuration) && FolderFilterStamp() == _directoryFilterStamp;
                try
                {
                    if (CanRefresh() && await CompareDirectoryAsync(model.LeftPath, model.RightPath, refresh.Token, CanRefresh, () => { }, model.MiddlePath))
                    { resultModel = _directoryComparison; resultGeneration = _folderContextGeneration; }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException)
                { summary += " · 再比較を完了できませんでした: " + error.Message; }
                finally { if (ReferenceEquals(_folderRefreshOperation, refresh)) _folderRefreshOperation = null; }
            }
            if (FolderOwnerCurrent() && ReferenceEquals(_specialTab.Content, _folderView)
                && ReferenceEquals(_directoryComparison, resultModel) && _folderContextGeneration == resultGeneration
                && FolderConfigurationMatches(configuration))
            { _folderCopySummary.Text = summary; _status.Text = summary; }
        }
        finally
        {
            if (ReferenceEquals(_folderCopyOperation, operation)) _folderCopyOperation = null;
            SetBusy(false);
        }

        void Validate()
        {
            operation.Token.ThrowIfCancellationRequested();
            if (!FolderOwnerCurrent() || _folderModelStale || _folderContextGeneration != generation
                || !ReferenceEquals(_directoryComparison, model) || !ReferenceEquals(_specialTab.Content, _folderView)
                || !FolderConfigurationMatches(configuration))
                throw new OperationCanceledException("コピー中に比較・選択・設定が変わったため中止しました。", operation.Token);
            EnsureFolderSideWritable(destinationSide);
        }
        void ValidateFilter()
        {
            Validate();
            if (FolderFilterStamp() != _directoryFilterStamp)
                throw new OperationCanceledException("コピー中にフィルターが変わったため中止しました。", operation.Token);
        }
        void SetBusy(bool busy)
        {
            _folderCopyBusy = busy; _directoryList.IsEnabled = !busy;
            foreach (var button in _folderCopyButtons) button.IsEnabled = !busy;
            _folderDirection.IsEnabled = !busy;
            UpdateFolderTreeControls();
        }
        static void OnUi(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess()) action();
            else Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult();
        }
    }

    private void EnsureFolderDestinationWritable(string output, string destinationRoot, DirectorySide destinationSide)
    {
        var absolute = Path.GetFullPath(output);
        var protection = FolderPathProtection.CreateContext();
        if (!protection.Within(absolute, destinationRoot)) throw new InvalidOperationException("指定したコピー先フォルダーの外へは書き込めません。");
        EnsureFolderSideWritable(destinationSide);
        var window = _owner as MainWindow;
        // 各出力の公開直前に全タブ共通の保護を一度検査し、各タブ固有の保護を続ける。
        EnsureWindowProjectOutputWritable(absolute, protection);
        foreach (var pane in window?.SessionPanes ?? [this])
        {
            pane.EnsurePaneProjectOutputWritable(absolute, protection);
            var project = pane.CaptureProject();
            foreach (var side in Enumerable.Range(0, 3))
            {
                var source = ProjectInputs.PathFor(project, side);
                if (!(ReferenceEquals(pane, this) && side == (int)destinationSide && Same(source, destinationRoot))) Protect(source);
                if (pane._lastPackageComparison is { } compared)
                {
                    var accepted = side == 0 ? compared.Left : side == 1 ? compared.Base : compared.Right;
                    if (!(ReferenceEquals(pane, this) && side == (int)destinationSide && Same(accepted, destinationRoot))) Protect(accepted);
                }
            }
            foreach (var source in ProjectInputs.PhysicalPaths(project).Skip(3)) Protect(source);
            foreach (var source in (pane._specialTab.Content as SpecializedViews.ImagePanel)?.SourceProtectionPaths ?? []) Protect(source);
            if (pane._directoryComparison is { } folder)
            {
                if (!(ReferenceEquals(pane, this) && destinationSide == DirectorySide.Left && Same(folder.LeftPath, destinationRoot))) Protect(folder.LeftPath, true);
                if (!(ReferenceEquals(pane, this) && destinationSide == DirectorySide.Middle && Same(folder.MiddlePath, destinationRoot))) Protect(folder.MiddlePath, true);
                if (!(ReferenceEquals(pane, this) && destinationSide == DirectorySide.Right && Same(folder.RightPath, destinationRoot))) Protect(folder.RightPath, true);
            }
        }
        Protect(window?.WorkspaceSourcePath);

        void Protect(string? source, bool directory = false)
        {
            if (string.IsNullOrWhiteSpace(source)) return;
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
            { if (!uri.IsFile) return; source = uri.LocalPath; }
            if (Same(source, absolute) || (directory || Directory.Exists(source)) && protection.Within(absolute, source))
                throw new InvalidOperationException("別の比較入力・アーカイブ原本・フィルター・プロジェクトを上書きできません。");
        }
        bool Same(string? source, string path) => !string.IsNullOrWhiteSpace(source) && protection.SameContainer(source, path);
    }

    private void EnsureFolderSourceMovable(string input, string sourceRoot, DirectorySide sourceSide)
    {
        var absolute = Path.GetFullPath(input);
        var protection = FolderPathProtection.CreateContext();
        if (!protection.Within(absolute, sourceRoot)) throw new InvalidOperationException("指定した入力フォルダーの外からは移動できません。");
        EnsureFolderSideWritable(sourceSide);
        var window = _owner as MainWindow;
        EnsureWindowProjectOutputWritable(absolute, protection);
        foreach (var pane in window?.SessionPanes ?? [this])
        {
            pane.EnsurePaneProjectOutputWritable(absolute, protection);
            var project = pane.CaptureProject();
            foreach (var side in Enumerable.Range(0, 3))
            {
                var source = ProjectInputs.PathFor(project, side);
                if (!(ReferenceEquals(pane, this) && side == (int)sourceSide && Same(source, sourceRoot))) Protect(source);
                if (pane._lastPackageComparison is { } compared)
                {
                    var accepted = side == 0 ? compared.Left : side == 1 ? compared.Base : compared.Right;
                    if (!(ReferenceEquals(pane, this) && side == (int)sourceSide && Same(accepted, sourceRoot))) Protect(accepted);
                }
            }
            foreach (var source in ProjectInputs.PhysicalPaths(project).Skip(3)) Protect(source);
            foreach (var source in (pane._specialTab.Content as SpecializedViews.ImagePanel)?.SourceProtectionPaths ?? []) Protect(source);
            if (pane._directoryComparison is { } folder)
            {
                if (!(ReferenceEquals(pane, this) && sourceSide == DirectorySide.Left && Same(folder.LeftPath, sourceRoot))) Protect(folder.LeftPath, true);
                if (!(ReferenceEquals(pane, this) && sourceSide == DirectorySide.Middle && Same(folder.MiddlePath, sourceRoot))) Protect(folder.MiddlePath, true);
                if (!(ReferenceEquals(pane, this) && sourceSide == DirectorySide.Right && Same(folder.RightPath, sourceRoot))) Protect(folder.RightPath, true);
            }
        }
        Protect(window?.WorkspaceSourcePath);
        Protect(_fileFilter.Text);

        void Protect(string? source, bool directory = false)
        {
            if (string.IsNullOrWhiteSpace(source)) return;
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
            { if (!uri.IsFile) return; source = uri.LocalPath; }
            if (Same(source, absolute) || (directory || Directory.Exists(source)) && protection.Within(absolute, source))
                throw new InvalidOperationException("別の比較入力・アーカイブ原本・フィルター・プロジェクトを移動できません。");
        }
        bool Same(string? source, string path) => !string.IsNullOrWhiteSpace(source) && protection.SameContainer(source, path);
    }

    private void EnsureFolderSideWritable(DirectorySide side)
    {
        if (side != DirectorySide.Middle) { EnsureSideWritable(side == DirectorySide.Right); return; }
        if (_projectMetadata.BaseArchiveInput is not null || _projectMetadata.BaseReadOnly)
            throw new InvalidOperationException("中央フォルダーは読取り専用に指定されています。");
    }

    private static string FolderSideName(DirectorySide side) => side switch
    { DirectorySide.Left => "左", DirectorySide.Middle => "中央", DirectorySide.Right => "右", _ => throw new ArgumentOutOfRangeException(nameof(side)) };

}
