using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _packagingOperation;
    private bool _packaging;

    private Button PackagingButton()
    {
        var button = new Button { Content = "比較を包装…", Margin = new Thickness(8, 0), Name = "package-comparisons" };
        button.Click += async (_, _) => await CreatePackagingDialog().ShowDialog(this);
        Closed += (_, _) => { _packagingOperation?.Cancel(); };
        return button;
    }

    public Window CreatePackagingDialog()
    {
        var panes = SessionPanes.ToArray();
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "包装する比較を選択してください。未保存の編集は先に保存してください。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var choices = new StackPanel { Spacing = 6 };
        for (var i = 0; i < panes.Length; i++)
        {
            var project = panes[i].CaptureProject();
            choices.Children.Add(new CheckBox { Name = "package-entry-" + i, Content = ProjectTitle(project, i), IsChecked = ReferenceEquals(panes[i], ActivePane), Tag = panes[i] });
        }
        content.Children.Add(new ScrollViewer { Content = choices, MaxHeight = 220 });
        foreach (var (name, label, value) in new[] { ("documents", "比較元の文書", true), ("report", "HTMLレポート", false), ("patch", "Unifiedパッチ", false), ("project", "比較プロジェクト", true), ("clipboard", "保存したアーカイブをクリップボードへコピー", false) })
            content.Children.Add(new CheckBox { Name = "package-" + name, Content = label, IsChecked = value });
        var status = new TextBlock { Name = "package-status", TextWrapping = Avalonia.Media.TextWrapping.Wrap }; content.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "包装して保存", Name = "package-save" }; var cancel = new Button { Content = "閉じる" };
        actions.Children.Add(save); actions.Children.Add(cancel); content.Children.Add(actions);
        var dialog = new Window { Title = "比較文書のアーカイブ包装", Width = 650, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content };
        save.Click += async (_, _) =>
        {
            if (_packaging) return;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "比較文書の包装先", SuggestedFileName = "comparison.zip", FileTypeChoices = ArchivePickers.FileTypes, ShowOverwritePrompt = true });
            if (file?.TryGetLocalPath() is not string path) return;
            save.IsEnabled = false; cancel.Content = "中止"; status.Text = "比較文書を包装しています…";
            try { await PackageFromDialogAsync(dialog, path); status.Text = $"保存しました: {path}"; }
            catch (Exception ex) { status.Text = ex is OperationCanceledException ? "包装を中止しました。" : ex.Message; }
            finally { save.IsEnabled = true; cancel.Content = "閉じる"; }
        };
        cancel.Click += (_, _) => { if (_packaging) CancelPackaging(); else dialog.Close(); };
        dialog.Closing += (_, e) => { if (_packaging) { e.Cancel = true; CancelPackaging(); } };
        return dialog;
    }

    public async Task PackageFromDialogAsync(Window dialog, string output, CancellationToken token = default)
    {
        var controls = dialog.GetVisualDescendants().OfType<CheckBox>().ToArray();
        bool Checked(string name) => controls.Single(control => control.Name == "package-" + name).IsChecked == true;
        var selected = controls.Where(control => control.Name?.StartsWith("package-entry-", StringComparison.Ordinal) == true && control.IsChecked == true)
            .Select(control => Array.IndexOf(SessionPanes.ToArray(), (ComparisonPane)control.Tag!)).ToArray();
        await PackageWorkspaceAsync(output, selected, new(Checked("documents"), Checked("report"), Checked("patch"), Checked("project")), token);
        if (Checked("clipboard")) await CopyPackageToClipboardAsync(output);
    }

    public async Task PackageWorkspaceAsync(string output, IReadOnlyList<int>? selectedIndices = null,
        ComparisonPackageOptions? options = null, CancellationToken token = default)
    {
        if (_packaging) throw new InvalidOperationException("別の包装を実行しています。");
        var panes = SessionPanes.ToArray(); var indices = selectedIndices?.ToArray() ?? Enumerable.Range(0, panes.Length).ToArray();
        if (indices.Length == 0 || indices.Distinct().Count() != indices.Length || indices.Any(index => index < 0 || index >= panes.Length))
            throw new ArgumentException("包装する比較を選択してください。");
        foreach (var pane in panes) pane.EnsureProjectOutputWritable(output);
        if (indices.Any(index => panes[index].HasUnsavedChanges)) throw new InvalidOperationException("未保存の文書があります。編集内容を保存してから包装してください。");
        foreach (var index in indices) panes[index].EnsureComparedForPackaging();
        var workspace = new ComparisonWorkspace { Entries = panes.Select(pane => pane.CaptureProject()).ToArray(), ActiveEntryIndex = Array.IndexOf(panes, ActivePane) };
        var sourceProject = WorkspaceSourcePath;
        var packageOptions = options ?? new();
        var displays = (packageOptions.IncludeReport ? indices : []).Select(index => (Index: index, Display: panes[index].CapturePackagingImageDisplay()))
            .Where(item => item.Display is not null).ToDictionary(item => item.Index, item => item.Display!);
        _packaging = true;
        _packagingOperation = CancellationTokenSource.CreateLinkedTokenSource(token);
        try { await ComparisonPackage.CreateWithImageDisplaysAsync(workspace, output, packageOptions, indices, _packagingOperation.Token, sourceProject, displays); }
        finally { _packagingOperation.Dispose(); _packagingOperation = null; _packaging = false; }
    }

    public void CancelPackaging() => _packagingOperation?.Cancel();

    public async Task CopyPackageToClipboardAsync(string path)
    {
        if (Clipboard is not { } clipboard) throw new InvalidOperationException("クリップボードを利用できません。アーカイブは保存済みです。");
        var file = await StorageProvider.TryGetFileFromPathAsync(Path.GetFullPath(path))
            ?? throw new FileNotFoundException("保存したアーカイブがありません。", path);
        await clipboard.SetFileAsync(file);
        await clipboard.FlushAsync();
    }
}

public sealed partial class ComparisonPane
{
    private (string Left, string Base, string Right, int Mode, string? Provider)? _lastPackageComparison;
    internal ImageReportDisplayCapture? CapturePackagingImageDisplay()
    {
        var report = (_specialTab.Content as SpecializedViews.ImagePanel)?.CaptureReport();
        return report?.DisplayCapture;
    }
    public void EnsureComparedForPackaging()
    {
        EnsureNoPendingTableEdit();
        var current = (LeftPath.Text ?? "", BasePath.Text ?? "", RightPath.Text ?? "", _mode.SelectedIndex, _provider.SelectedItem as string);
        if (_lastPackageComparison != current || !CompareButton.IsEnabled)
            throw new InvalidOperationException("包装前に現在のパスと形式で比較してください。");
    }
}
