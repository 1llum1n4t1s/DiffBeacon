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
    private readonly ComboBox _folderDirection = new()
    {
        Name = "FolderCopyDirection", ItemsSource = new[] { "左 → 右", "右 → 左", "左 → 中央", "中央 → 左", "中央 → 右", "右 → 中央" },
        SelectedIndex = 0, MinWidth = 160, Margin = new Thickness(4), IsVisible = false
    };
    private readonly List<Control> _folderThreeWayControls = [];
    private DirectoryComparisonResult? _directoryComparison;
    private CancellationTokenSource? _folderCopyOperation, _folderRefreshOperation;
    private long _folderContextGeneration;
    private bool _folderCopyBusy, _folderModelStale;
    private FolderConfiguration? _directoryConfiguration;
    private string? _directoryFilterStamp;
    internal Task? PendingFolderCopy { get; private set; }
    internal FolderCopyResult? LastFolderCopyResult { get; private set; }
    internal string FolderCopySummaryText => _folderCopySummary.Text ?? "";
    internal string FolderCopyStatusText => _status.Text ?? "";
    internal Action<FolderCopyPlan>? FolderPlanReady { get; set; }
    internal Action<FolderCopyPlan>? FolderExecutionStarting { get; set; }
    internal Action<string>? FolderOutputChecking { get; set; }
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

    private string? FolderFilterStamp()
    {
        if (string.IsNullOrWhiteSpace(_fileFilter.Text)) return null;
        if (new FileInfo(_fileFilter.Text).Length > 1_048_576) throw new InvalidDataException("フィルターは1 MiB以下にしてください。");
        var bytes = File.ReadAllBytes(_fileFilter.Text);
        if (bytes.Length > 1_048_576) throw new InvalidDataException("フィルターは1 MiB以下にしてください。");
        return Convert.ToHexString(SHA256.HashData(bytes));
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
        actions.Children.Add(_folderDirection); _folderThreeWayControls.Add(_folderDirection);
        AddThreeWay("選択をすべてコピー", DirectoryCopyMode.All);
        AddThreeWay("選択の差分をコピー", DirectoryCopyMode.DifferencesOnly);
        _folderDirection.SelectionChanged += (_, _) => InvalidateFolderCopy(false);
        DockPanel.SetDock(actions, Dock.Top); _folderView.Children.Add(actions);
        DockPanel.SetDock(_folderCopySummary, Dock.Bottom); _folderView.Children.Add(_folderCopySummary);
        _folderView.Children.Add(_directoryList);
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
            _folderCopyButtons.Add(button); actions.Children.Add(button);
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
    }

    internal void InvalidateFolderCopy(bool markStale = true)
    {
        _folderContextGeneration++;
        _folderCopyOperation?.Cancel(); _folderRefreshOperation?.Cancel();
        if (markStale && _directoryComparison is not null) _folderModelStale = true;
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

    private void BindDirectoryModel(DirectoryComparisonResult result, FolderConfiguration configuration, string? filterStamp)
    {
        InvalidateFolderCopy();
        _directoryLeft = result.LeftPath; _directoryMiddle = result.MiddlePath; _directoryRight = result.RightPath;
        _directoryComparison = result; _directoryConfiguration = configuration; _directoryFilterStamp = filterStamp;
        foreach (var button in _folderCopyButtons.Take(4)) button.IsVisible = !result.IsThreeWay;
        foreach (var control in _folderThreeWayControls) control.IsVisible = result.IsThreeWay;
        _directoryList.ItemsSource = result.Entries;
        _directoryList.ItemTemplate = new FuncDataTemplate<DirectoryEntry>((entry, _) => new TextBlock
        {
            Text = entry is null ? "" : $"{entry.Status,-14}  {ThreeWayCaption(entry)}{(entry.IsFiltered ? "[除外] " : "")}{entry.RelativePath}",
            FontFamily = new FontFamily("Cascadia Mono, Menlo, monospace"), Margin = new Thickness(8),
            TextTrimming = TextTrimming.CharacterEllipsis
        }, false);
        _folderModelStale = false; _folderCopySummary.Text = "Ctrl / Shiftで複数の項目を選択できます。";
        SetSpecialView(_folderView); _views.SelectedItem = _specialTab;
        _status.Text = $"フォルダー比較: {result.Entries.Count} 件";

        static string ThreeWayCaption(DirectoryEntry entry) => entry.ThreeWay?.Classification switch
        {
            DirectoryThreeWayClassification.OnlyLeft => "[左のみ異なる] ",
            DirectoryThreeWayClassification.OnlyMiddle => "[中央のみ異なる] ",
            DirectoryThreeWayClassification.OnlyRight => "[右のみ異なる] ",
            DirectoryThreeWayClassification.AllChanged => "[三者差分] ",
            _ => ""
        };
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
            }), () => OnUi(Validate), operation.Token);
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

    private void EnsureFolderSideWritable(DirectorySide side)
    {
        if (side != DirectorySide.Middle) { EnsureSideWritable(side == DirectorySide.Right); return; }
        if (_projectMetadata.BaseArchiveInput is not null || _projectMetadata.BaseReadOnly)
            throw new InvalidOperationException("中央フォルダーは読取り専用に指定されています。");
    }

    private static string FolderSideName(DirectorySide side) => side switch
    { DirectorySide.Left => "左", DirectorySide.Middle => "中央", DirectorySide.Right => "右", _ => throw new ArgumentOutOfRangeException(nameof(side)) };

}
