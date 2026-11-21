using Avalonia.Controls;
using Avalonia.Layout;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    /// <summary>自己検証が、比較統計のダイアログが出たときに読む。</summary>
    internal Action<Window>? FolderStatisticsShown { get; set; }

    /// <summary>フォルダー比較の統計。バイナリかどうかは行が持たないので、ファイル数に含める。</summary>
    internal static IReadOnlyList<(string Label, int Folders, int Files)> CountFolderStatistics(DirectoryComparisonResult model)
    {
        int Of(DirectoryEntryKind kind, params DirectoryDifferenceKind[] statuses) => model.AllEntries.Count(entry => entry.EntryKind == kind && !entry.IsFiltered && entry.Error is null && statuses.Contains(entry.Status));
        int Skipped(DirectoryEntryKind kind) => model.AllEntries.Count(entry => entry.IsFiltered && entry.EntryKind == kind);
        return
        [
            (UiText.Menu("Identical"), Of(DirectoryEntryKind.Directory, DirectoryDifferenceKind.Equal), Of(DirectoryEntryKind.File, DirectoryDifferenceKind.Equal)),
            (UiText.Menu("Different"), Of(DirectoryEntryKind.Directory, DirectoryDifferenceKind.Modified, DirectoryDifferenceKind.TypeConflict), Of(DirectoryEntryKind.File, DirectoryDifferenceKind.Modified, DirectoryDifferenceKind.TypeConflict)),
            (UiText.Menu("Left only"), Of(DirectoryEntryKind.Directory, DirectoryDifferenceKind.LeftOnly), Of(DirectoryEntryKind.File, DirectoryDifferenceKind.LeftOnly)),
            (UiText.Menu("Middle only"), Of(DirectoryEntryKind.Directory, DirectoryDifferenceKind.MiddleOnly), Of(DirectoryEntryKind.File, DirectoryDifferenceKind.MiddleOnly)),
            (UiText.Menu("Right only"), Of(DirectoryEntryKind.Directory, DirectoryDifferenceKind.RightOnly), Of(DirectoryEntryKind.File, DirectoryDifferenceKind.RightOnly)),
            (UiText.Menu("Skipped"), Skipped(DirectoryEntryKind.Directory), Skipped(DirectoryEntryKind.File)),
        ];
    }

    /// <summary>比較統計を出す。フォルダー比較が無いときは状態欄に理由を出す。</summary>
    internal async Task ShowFolderStatisticsAsync()
    {
        if (_directoryComparison is not { } model) { _status.Text = "フォルダー比較がありません。"; return; }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var lines = CountFolderStatistics(model).Select(row => row.Label + "  " + UiText.Menu("Folders:") + " " + row.Folders + "  " + UiText.Menu("Files:") + " " + row.Files);
        var text = new TextBlock { Name = "folder-statistics", Text = string.Join("\n", lines), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var dialog = new Window
        {
            Name = "folder-statistics-dialog", Title = UiText.Menu("Compare Statistics"), Width = 420, Height = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false,
        };
        var ok = new Button { Name = "folder-statistics-ok", Content = UiText.Menu("&Ok"), IsDefault = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.Close();
        var body = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 12, Children = { text, ok } };
        dialog.Content = body;
        var closing = dialog.ShowDialog(owner);
        FolderStatisticsShown?.Invoke(dialog);
        await closing;
    }
}
