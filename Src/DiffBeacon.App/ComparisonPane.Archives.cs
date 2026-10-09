using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    private async Task CreateArchiveAsync()
    {
        var path = await ArchiveWritePickerInjection.SaveAsync(_owner, new FilePickerSaveOptions { Title = "非暗号化アーカイブを作成", SuggestedFileName = "archive.7z", ShowOverwritePrompt = true, FileTypeChoices = ArchivePickers.FileTypes }, ArchiveWritePickers);
        if (_disposed) throw new OperationCanceledException("アーカイブ作成の比較タブは閉じています。");
        if (path is null) return;
        string source; ManagedArchiveWriteOptions? writeOptions = null;
        var singleGZip = !path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            && (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gzip", StringComparison.OrdinalIgnoreCase));
        var singleBZip2 = !path.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase);
        var singleZ = !path.EndsWith(".tar.Z", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(".Z", StringComparison.OrdinalIgnoreCase);
        if (singleGZip || singleBZip2 || singleZ)
        {
            var selected = await ArchiveWritePickerInjection.FileAsync(_owner, new FilePickerOpenOptions { Title = "圧縮して保存するファイルを選択", AllowMultiple = false }, ArchiveWritePickers);
            if (_disposed) throw new OperationCanceledException("アーカイブ作成の比較タブは閉じています。");
            if (selected is null) return;
            source = selected;
            if (singleGZip)
            {
                writeOptions = await GZipWriteOptionsDialog.ShowAsync(_owner);
                if (_disposed) throw new OperationCanceledException("アーカイブ作成の比較タブは閉じています。");
                if (writeOptions is null) return;
            }
        }
        else
        {
            var selected = await ArchiveWritePickerInjection.FolderAsync(_owner, new FolderPickerOpenOptions { Title = "アーカイブへ保存するフォルダーを選択" }, ArchiveWritePickers);
            if (_disposed) throw new OperationCanceledException("アーカイブ作成の比較タブは閉じています。");
            if (selected is null) return;
            source = selected;
        }
        if (_disposed) throw new OperationCanceledException("アーカイブ作成の比較タブは閉じています。");
        EnsureProjectOutputWritable(path);
        _operation?.Cancel(); _operation?.Dispose(); _operation = new CancellationTokenSource();
        _status.Text = "アーカイブを作成しています…";
        await ArchiveActions.CreateAsync(source, path, _operation.Token, writeOptions);
        if (_disposed) return;
        _status.Text = $"非暗号化アーカイブを保存しました: {path}";
    }
}

internal static class ArchivePickers
{
    internal static IReadOnlyList<FilePickerFileType> FileTypes { get; } =
    [
        new("7z") { Patterns = ["*.7z"] },
        new("ZIP / JAR / EAR / WAR / XPI") { Patterns = ["*.zip", "*.jar", "*.ear", "*.war", "*.xpi"] },
        new("TAR") { Patterns = ["*.tar"] },
        new("TAR + GZip") { Patterns = ["*.tar.gz", "*.tgz"] },
        new("TAR + BZip2") { Patterns = ["*.tar.bz2", "*.tbz2", "*.tbz"] },
        new("TAR + compress") { Patterns = ["*.tar.Z", "*.tar.z", "*.taz"] },
        new("GZip（単一ファイル）") { Patterns = ["*.gz", "*.gzip"] },
        new("BZip2（単一ファイル）") { Patterns = ["*.bz2"] },
        new("compress（単一ファイル）") { Patterns = ["*.Z", "*.z"] }
    ];
}

internal sealed class GZipWriteOptionsDialog : Window
{
    internal static Action<GZipWriteOptionsDialog>? Shown { get; set; }
    internal ComboBox NameCodePage { get; } = ArchiveNameSettings.Picker();
    internal Button Accept { get; } = new() { Content = "作成" };
    internal Button Cancel { get; } = new() { Content = "キャンセル" };
    private GZipWriteOptionsDialog()
    {
        Title = "gzipの格納名"; Width = 420; SizeToContent = SizeToContent.Height;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "出力ファイルに保存する格納名の文字コード" });
        panel.Children.Add(NameCodePage);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(Cancel); buttons.Children.Add(Accept); panel.Children.Add(buttons); Content = panel;
        Cancel.Click += (_, _) => Close();
        Accept.Click += (_, _) => Close(new ManagedArchiveWriteOptions(ArchiveNameSettings.Selected(NameCodePage)));
    }
    internal static Task<ManagedArchiveWriteOptions?> ShowAsync(Window owner)
    {
        var dialog = new GZipWriteOptionsDialog(); var result = dialog.ShowDialog<ManagedArchiveWriteOptions?>(owner);
        Shown?.Invoke(dialog); return result;
    }
}