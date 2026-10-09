using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    private static readonly StringComparer FolderTreeComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly CheckBox _folderTreeMode = new() { Content = "ツリー表示", Name = "FolderTreeMode", Margin = new Thickness(4) };
    private readonly ComboBox _folderInitialExpansion = new()
    {
        Name = "FolderInitialExpansion", Margin = new Thickness(4),
        ItemsSource = new[] { "初期: 折り畳み", "初期: すべて展開", "初期: 異なるフォルダー", "初期: 同じフォルダー" }
    };
    private FolderApplicationOptionsStore FolderOptions => (_owner as MainWindow)?.FolderOptions ?? _standaloneFolderOptions;
    private readonly FolderApplicationOptionsStore _standaloneFolderOptions = new();
    private bool _folderTreeRequested;
    private int _folderInitialPolicy;
    private bool _updatingFolderOptions;
    private readonly List<Control> _folderTreeControls = [];
    private bool _folderTreeDisplayed;
    private HashSet<string> _folderExpanded = new(FolderTreeComparer);
    private Dictionary<string, int> _folderTreeDepth = new(FolderTreeComparer);
    private Dictionary<string, string> _folderTreeParents = new(FolderTreeComparer);

    private sealed record FolderTreeProjection(IReadOnlyList<DirectoryEntry> Rows,
        Dictionary<string, int> Depths, Dictionary<string, string> Parents, HashSet<string> Expanded);

    private bool FolderTreeActive => _folderTreeMode.IsChecked == true && _directoryComparison?.Options.Recursive == true;
    private bool FolderTreeInputAllowed => !_folderCopyBusy && !_folderModelStale && !_disposed
        && CompareButton.IsEnabled && FolderOwnerCurrent() && ReferenceEquals(_specialTab.Content, _folderView)
        && _directoryComparison?.Options.Recursive == true && _recursive.IsChecked == true
        && _directoryConfiguration is { } configuration && FolderConfigurationMatches(configuration);

    private static bool FolderCanExpand(DirectoryEntry entry) => entry.Children.Count > 0
        && (entry.LeftState?.Kind == DirectoryEntryKind.Directory || entry.MiddleState?.Kind == DirectoryEntryKind.Directory
            || entry.RightState?.Kind == DirectoryEntryKind.Directory);

    private void InitializeFolderTree(WrapPanel actions)
    {
        var options = FolderOptions.Current;
        _folderTreeRequested = options.TreeMode; _folderInitialPolicy = options.InitialExpansion;
        _folderTreeMode.IsChecked = _folderTreeRequested;
        _folderInitialExpansion.SelectedIndex = _folderInitialPolicy;
        actions.Children.Add(_folderTreeMode); _folderTreeControls.Add(_folderTreeMode);
        actions.Children.Add(_folderInitialExpansion); _folderTreeControls.Add(_folderInitialExpansion);
        _folderInitialExpansion.SelectionChanged += async (_, _) =>
        {
            if (_updatingFolderOptions) return;
            await GuardAsync(() => { SaveFolderDisplayOptions(false); return Task.CompletedTask; });
        };
        Add("すべて展開", "FolderExpandAll", 0);
        Add("異なるフォルダーを展開", "FolderExpandDifferent", 1);
        Add("同じフォルダーを展開", "FolderExpandIdentical", 2);
        Add("すべて折り畳み", "FolderCollapseAll", 3);
        _folderTreeMode.Click += async (_, _) => await GuardAsync(() =>
        {
            SaveFolderDisplayOptions(true);
            return Task.CompletedTask;
        });
        // ListBoxの標準左右移動より先に、ツリーの無修飾キーだけを処理する。
        _directoryList.AddHandler(InputElement.KeyDownEvent, async (_, args) =>
        {
            if (args.KeyModifiers != KeyModifiers.None || args.Key is not (Key.Left or Key.Right)
                || !FolderTreeInputAllowed || !FolderTreeActive || _directoryList.SelectedItem is not DirectoryEntry entry) return;
            args.Handled = true;
            await GuardAsync(() =>
            {
                if (args.Key == Key.Right)
                {
                    if (FolderCanExpand(entry) && !_folderExpanded.Contains(entry.RelativePath)) ToggleFolderNode(entry);
                    else if (FolderCanExpand(entry))
                    {
                        var child = entry.Children.FirstOrDefault(item => _folderTreeDepth.ContainsKey(item.RelativePath));
                        if (child is not null) _directoryList.SelectedItem = child;
                    }
                }
                else if (_folderExpanded.Contains(entry.RelativePath)) ToggleFolderNode(entry);
                else if (_folderTreeParents.TryGetValue(entry.RelativePath, out var parent))
                    _directoryList.SelectedItem = _directoryList.ItemsSource?.OfType<DirectoryEntry>()
                        .FirstOrDefault(item => FolderTreeComparer.Equals(item.RelativePath, parent));
                return Task.CompletedTask;
            });
        }, RoutingStrategies.Tunnel);
        CompareButton.PropertyChanged += (_, args) =>
        { if (args.Property == IsEnabledProperty) UpdateFolderTreeControls(); };
        _directoryList.MinHeight = 100;
        _directoryList.ItemTemplate = new FuncDataTemplate<DirectoryEntry>((entry, _) => BuildFolderTreeRow(entry), false);
        UpdateFolderTreeControls();

        void Add(string title, string name, int operation)
        {
            var button = new Button { Content = title, Name = name, Margin = new Thickness(4) };
            button.Click += async (_, _) => await GuardAsync(() =>
            { ApplyFolderTreeOperation(operation); return Task.CompletedTask; });
            actions.Children.Add(button); _folderTreeControls.Add(button);
        }
    }

    private void UpdateFolderTreeControls()
    {
        foreach (var control in _folderTreeControls)
            control.IsEnabled = FolderTreeInputAllowed && (ReferenceEquals(control, _folderTreeMode) || ReferenceEquals(control, _folderInitialExpansion) || FolderTreeActive);
        foreach (var button in _directoryList.GetVisualDescendants().OfType<Button>().Where(button => button.Name == "FolderTreeExpander"))
            button.IsEnabled = FolderTreeInputAllowed && FolderTreeActive;
    }

    private Control BuildFolderTreeRow(DirectoryEntry? entry)
    {
        if (entry is null) return new TextBlock();
        var row = new DockPanel { Margin = new Thickness(8, 4), LastChildFill = true };
        if (FolderTreeActive)
        {
            var depth = _folderTreeDepth.GetValueOrDefault(entry.RelativePath);
            row.Margin = new Thickness(8 + depth * 16, 4, 8, 4);
            var toggle = new Button { Name = "FolderTreeExpander", Content = _folderExpanded.Contains(entry.RelativePath) ? "−" : "+",
                Width = 24, Height = 24, Padding = new Thickness(0), Focusable = false,
                IsVisible = FolderCanExpand(entry), IsEnabled = FolderTreeInputAllowed };
            toggle.Click += async (_, args) =>
            {
                args.Handled = true;
                await GuardAsync(() => { ToggleFolderNode(entry); return Task.CompletedTask; });
            };
            toggle.AddHandler(InputElement.DoubleTappedEvent, (_, args) => args.Handled = true, RoutingStrategies.Bubble);
            DockPanel.SetDock(toggle, Dock.Left); row.Children.Add(toggle);
        }
        row.Children.Add(new TextBlock
        {
            Text = $"{entry.Status,-14}  {FolderThreeWayCaption(entry)}{(entry.IsFiltered ? "[除外] " : "")}{entry.RelativePath}",
            FontFamily = new FontFamily("Cascadia Mono, Menlo, monospace"), TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        return row;
    }

    private static string FolderThreeWayCaption(DirectoryEntry entry) => entry.ThreeWay?.Classification switch
    {
        DirectoryThreeWayClassification.OnlyLeft => "[左のみ異なる] ",
        DirectoryThreeWayClassification.OnlyMiddle => "[中央のみ異なる] ",
        DirectoryThreeWayClassification.OnlyRight => "[右のみ異なる] ",
        DirectoryThreeWayClassification.AllChanged => "[三者差分] ", _ => ""
    };

    private sealed record FolderDirectoryCandidate(DirectoryComparisonResult Model, FolderConfiguration Configuration,
        string? FilterStamp, FolderTreeProjection Projection, string[] Selection,
        DirectoryComparisonResult? PreviousModel, long ContextGeneration, bool TreeMode, int InitialPolicy);

    private FolderDirectoryCandidate PrepareFolderCandidate(DirectoryComparisonResult result,
        FolderConfiguration configuration, string? filterStamp, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var previousModel = _directoryComparison;
        var contextGeneration = _folderContextGeneration;
        var treeMode = _folderTreeMode.IsChecked == true;
        var retain = CanRetainFolderExpansion(result, configuration, filterStamp);
        var expanded = retain ? new HashSet<string>(_folderExpanded, FolderTreeComparer) : InitialFolderExpansion(result, token);
        var selection = retain
            ? _directoryList.SelectedItems?.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).ToArray() ?? [] : [];
        var projection = PrepareFolderTree(result, expanded, treeMode && result.Options.Recursive, token);
        token.ThrowIfCancellationRequested();
        return new(result, configuration, filterStamp, projection, selection, previousModel, contextGeneration, treeMode, _folderInitialPolicy);
    }

    private bool FolderCandidateContextCurrent(FolderDirectoryCandidate candidate)
        => ReferenceEquals(_directoryComparison, candidate.PreviousModel)
            && _folderContextGeneration == candidate.ContextGeneration
            && (_folderTreeMode.IsChecked == true) == candidate.TreeMode
            && _folderInitialPolicy == candidate.InitialPolicy
            && FolderConfigurationMatches(candidate.Configuration) && FolderFilterStamp() == candidate.FilterStamp;

    private void SaveFolderDisplayOptions(bool changeMode)
    {
        // 各操作の一項目だけを共有既定値へ保存し、既存paneの別項目は変更しない。
        var requested = changeMode ? FolderOptions.Current with { TreeMode = _folderTreeMode.IsChecked == true }
            : FolderOptions.Current with { InitialExpansion = _folderInitialExpansion.SelectedIndex };
        var generation = _folderContextGeneration; var model = _directoryComparison;
        FolderTreeProjection? projection = null;
        var selection = _directoryList.SelectedItems?.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).ToArray() ?? [];
        void Current()
        {
            if (!FolderTreeInputAllowed || _folderContextGeneration != generation || !ReferenceEquals(model, _directoryComparison)
                || FolderFilterStamp() != _directoryFilterStamp)
                throw new OperationCanceledException("フォルダー設定の保存中に比較状態が変わりました。");
        }
        try
        {
            Current(); FolderApplicationOptionsStore.Validate(requested);
            if (changeMode)
                projection = PrepareFolderTree(model!, new HashSet<string>(_folderExpanded, FolderTreeComparer), requested.TreeMode && model!.Options.Recursive);
            Current();
            if (!FolderOptions.SetOptions(requested, Current)) { _status.Text = FolderOptions.Diagnostic; return; }
            if (changeMode) _folderTreeRequested = requested.TreeMode; else _folderInitialPolicy = requested.InitialExpansion;
            if (projection is not null) { InvalidateFolderCopy(false); AdoptFolderTreeProjection(projection, selection); }
            else UpdateFolderTreeControls();
        }
        finally
        {
            _updatingFolderOptions = true;
            try { _folderTreeMode.IsChecked = _folderTreeRequested; _folderInitialExpansion.SelectedIndex = _folderInitialPolicy; }
            finally { _updatingFolderOptions = false; }
        }
    }

    private HashSet<string> InitialFolderExpansion(DirectoryComparisonResult model, CancellationToken token)
    {
        var expanded = new HashSet<string>(FolderTreeComparer);
        if (_folderInitialPolicy == 0 || !model.Options.Recursive) return expanded;
        var pending = new Stack<DirectoryEntry>(model.RootNodes.Reverse()); var visited = 0;
        while (pending.TryPop(out var entry))
        {
            token.ThrowIfCancellationRequested();
            if (++visited > model.Options.MaximumEntries) throw new InvalidDataException("初期展開の項目数が上限を超えています。");
            var different = entry.Status is DirectoryDifferenceKind.Modified or DirectoryDifferenceKind.LeftOnly
                or DirectoryDifferenceKind.RightOnly or DirectoryDifferenceKind.MiddleOnly or DirectoryDifferenceKind.TypeConflict
                || entry.LeftState is null || entry.RightState is null || model.IsThreeWay && entry.MiddleState is null;
            if (FolderCanExpand(entry) && (_folderInitialPolicy == 1 || _folderInitialPolicy == 2 && different
                || _folderInitialPolicy == 3 && entry.Status == DirectoryDifferenceKind.Equal)) expanded.Add(entry.RelativePath);
            for (var i = entry.Children.Count - 1; i >= 0; i--) pending.Push(entry.Children[i]);
        }
        return expanded;
    }
    private bool CanRetainFolderExpansion(DirectoryComparisonResult result, FolderConfiguration configuration, string? stamp)
    {
        if (_directoryComparison is not { } old || _directoryConfiguration is not { } previous) return false;
        return FolderTreeComparer.Equals(old.LeftPath, result.LeftPath)
            && FolderTreeComparer.Equals(old.MiddlePath, result.MiddlePath)
            && FolderTreeComparer.Equals(old.RightPath, result.RightPath)
            && previous.Values.Equals(configuration.Values) && previous.Rules.SequenceEqual(configuration.Rules)
            && FolderConfigurationMatches(configuration) && _directoryFilterStamp == stamp;
    }

    private FolderTreeProjection PrepareFolderTree(DirectoryComparisonResult model, HashSet<string> expanded,
        bool tree, CancellationToken token = default)
    {
        if (!model.Options.Recursive) return new(model.Entries, new(FolderTreeComparer), new(FolderTreeComparer), new(FolderTreeComparer));
        var visible = new HashSet<string>(model.Entries.Select(entry => entry.RelativePath), FolderTreeComparer);
        var rows = new List<DirectoryEntry>();
        var depths = new Dictionary<string, int>(FolderTreeComparer);
        var parents = new Dictionary<string, string>(FolderTreeComparer);
        var present = new HashSet<string>(FolderTreeComparer);
        var seen = new HashSet<string>(FolderTreeComparer);
        var pending = new Stack<(DirectoryEntry Entry, int Depth, string? Parent, bool Reachable)>();
        for (var i = model.RootNodes.Count - 1; i >= 0; i--) pending.Push((model.RootNodes[i], 0, null, true));
        while (pending.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested();
            if (node.Depth >= Math.Min(256, model.Options.MaximumDepth) || seen.Count >= model.Options.MaximumEntries
                || !seen.Add(node.Entry.RelativePath)) throw new InvalidDataException("ツリー表示の項目数または深度が上限を超えています。");
            var show = visible.Contains(node.Entry.RelativePath);
            if (FolderCanExpand(node.Entry)) present.Add(node.Entry.RelativePath);
            if (tree && show && node.Reachable)
            {
                rows.Add(node.Entry); depths.Add(node.Entry.RelativePath, node.Depth);
                if (node.Parent is not null) parents.Add(node.Entry.RelativePath, node.Parent);
            }
            var reachable = node.Reachable && (!show || expanded.Contains(node.Entry.RelativePath));
            for (var i = node.Entry.Children.Count - 1; i >= 0; i--)
                pending.Push((node.Entry.Children[i], node.Depth + 1, show ? node.Entry.RelativePath : node.Parent, reachable));
        }
        token.ThrowIfCancellationRequested();
        expanded.IntersectWith(present);
        return new(tree ? rows.ToArray() : model.Entries, depths, parents, expanded);
    }

    private void AdoptFolderTreeProjection(FolderTreeProjection projection, string[] selection)
    {
        _folderTreeDisplayed = FolderTreeActive;
        _folderExpanded = projection.Expanded; _folderTreeDepth = projection.Depths; _folderTreeParents = projection.Parents;
        _directoryList.SelectedItems?.Clear();
        _directoryList.ItemsSource = projection.Rows;
        var selected = new HashSet<string>(selection, FolderTreeComparer);
        foreach (var entry in projection.Rows)
            if (selected.Contains(entry.RelativePath)) _directoryList.SelectedItems?.Add(entry);
        UpdateFolderTreeControls();
    }

    private void ReprojectFolderTree(HashSet<string> expanded)
    {
        if (!FolderTreeInputAllowed || _directoryComparison is not { } model) return;
        if (FolderFilterStamp() != _directoryFilterStamp) throw new OperationCanceledException("ツリー操作前にフィルターが変わりました。再比較してください。");
        var projection = PrepareFolderTree(model, expanded, FolderTreeActive);
        var selected = _directoryList.SelectedItems?.OfType<DirectoryEntry>().Select(entry => entry.RelativePath).ToArray() ?? [];
        InvalidateFolderCopy(false);
        AdoptFolderTreeProjection(projection, selected);
    }

    private void ToggleFolderNode(DirectoryEntry entry)
    {
        if (!FolderTreeInputAllowed || !FolderTreeActive || !FolderCanExpand(entry)
            || !_folderTreeDepth.ContainsKey(entry.RelativePath)) return;
        var expanded = new HashSet<string>(_folderExpanded, FolderTreeComparer);
        if (!expanded.Add(entry.RelativePath)) expanded.Remove(entry.RelativePath);
        ReprojectFolderTree(expanded);
    }

    private void ApplyFolderTreeOperation(int operation)
    {
        if (!FolderTreeInputAllowed || !FolderTreeActive || _directoryComparison is not { } model) return;
        var expanded = new HashSet<string>(_folderExpanded, FolderTreeComparer);
        if (operation == 3) expanded.Clear();
        else
        {
            // 既存展開を維持し、必要なフォルダーだけを追加する。
            var pending = new Stack<DirectoryEntry>(model.RootNodes.Reverse());
            var visited = 0;
            while (pending.TryPop(out var entry))
            {
                if (++visited > model.Options.MaximumEntries) throw new InvalidDataException("ツリー表示の項目数が上限を超えています。");
                var different = entry.Status is DirectoryDifferenceKind.Modified or DirectoryDifferenceKind.LeftOnly
                    or DirectoryDifferenceKind.RightOnly or DirectoryDifferenceKind.MiddleOnly or DirectoryDifferenceKind.TypeConflict
                    || entry.LeftState is null || entry.RightState is null || model.IsThreeWay && entry.MiddleState is null;
                if (FolderCanExpand(entry) && (operation == 0 || operation == 1 && different
                    || operation == 2 && entry.Status == DirectoryDifferenceKind.Equal)) expanded.Add(entry.RelativePath);
                for (var i = entry.Children.Count - 1; i >= 0; i--) pending.Push(entry.Children[i]);
            }
        }
        ReprojectFolderTree(expanded);
    }
}
