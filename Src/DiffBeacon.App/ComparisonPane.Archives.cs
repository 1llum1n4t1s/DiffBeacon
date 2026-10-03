using Avalonia.Platform.Storage;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    private async Task CreateArchiveAsync()
    {
        var folders = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "アーカイブへ保存するフォルダーを選択" });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not string directory) return;
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "非暗号化アーカイブを作成", SuggestedFileName = "archive.7z", ShowOverwritePrompt = true, FileTypeChoices = ArchivePickers.FileTypes });
        if (file?.TryGetLocalPath() is not string path) return;
        EnsureProjectOutputWritable(path);
        _operation?.Cancel(); _operation?.Dispose(); _operation = new CancellationTokenSource();
        _status.Text = "アーカイブを作成しています…";
        await ArchiveActions.CreateAsync(directory, path, _operation.Token);
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
        new("TAR + compress") { Patterns = ["*.tar.Z", "*.tar.z", "*.taz"] }
    ];
}
