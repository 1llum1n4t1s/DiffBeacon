using Avalonia.Platform.Storage;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    private async Task CreateArchiveAsync()
    {
        var folders = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "7zへ保存するフォルダーを選択" });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not string directory) return;
        var path = await SavePathAsync("非暗号化7zを作成", "archive.7z");
        if (path is null) return;
        _operation?.Cancel(); _operation?.Dispose(); _operation = new CancellationTokenSource();
        _status.Text = "7zを作成しています…";
        await ArchiveActions.CreateAsync(directory, path, _operation.Token);
        _status.Text = "非暗号化7zを保存しました。";
    }
}
