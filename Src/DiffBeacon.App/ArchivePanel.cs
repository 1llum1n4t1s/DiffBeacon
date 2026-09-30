using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public sealed class ArchivePanel : UserControl, IDisposable
{
    private readonly string _leftPath, _rightPath;
    private readonly Action<string>? _guardOutput;
    private readonly CancellationTokenSource _lifetime;
    private CancellationTokenSource? _operation;
    private int _previewVersion;
    private int _refreshVersion;
    private bool _disposed;
    private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _preview = new() { Name = "archive-preview", IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Mono, Menlo, monospace") };
    public ListBox EntryList { get; } = new() { Name = "archive-entries" };
    public TextBox LeftPassword { get; } = new() { Name = "archive-left-password", PasswordChar = '●', PlaceholderText = "左のパスワード（任意）", Width = 200, Margin = new Thickness(4) };
    public TextBox RightPassword { get; } = new() { Name = "archive-right-password", PasswordChar = '●', PlaceholderText = "右のパスワード（任意）", Width = 200, Margin = new Thickness(4) };
    public TextBox ExtractionName { get; } = new() { Name = "archive-extraction-name", Text = "extracted", PlaceholderText = "新しい展開フォルダー名", Width = 200, Margin = new Thickness(4) };
    public IReadOnlyList<ArchiveEntryDifference> Rows { get; private set; } = [];
    public string PreviewText => _preview.Text ?? "";
    public string StatusText => _status.Text ?? "";

    private ArchivePanel(string left, string right, CancellationToken token, Action<string>? guardOutput)
    {
        _leftPath = left; _rightPath = right; _guardOutput = guardOutput; _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var panel = new DockPanel(); var actions = new WrapPanel(); actions.Children.Add(LeftPassword); actions.Children.Add(RightPassword);
        Button("アーカイブを再比較", RefreshAsync);
        Button("左エントリを書き出す", () => ExportAsync(false)); Button("右エントリを書き出す", () => ExportAsync(true));
        Button("左を再梱包", () => RepackAsync(false)); Button("右を再梱包", () => RepackAsync(true));
        actions.Children.Add(ExtractionName);
        Button("左をすべて展開", () => ExtractAsync(false)); Button("右をすべて展開", () => ExtractAsync(true));
        EntryList.ItemTemplate = new FuncDataTemplate<ArchiveEntryDifference>((row, _) => new TextBlock
        {
            Text = row is null ? "" : $"{Label(row.Status),-7} {row.Path}   左 {row.Left?.Size.ToString("N0") ?? "—"} / 右 {row.Right?.Size.ToString("N0") ?? "—"}",
            Margin = new Thickness(6), FontFamily = new FontFamily("Cascadia Mono, Menlo, monospace")
        });
        EntryList.SelectionChanged += async (_, _) =>
        {
            if (EntryList.SelectedItem is ArchiveEntryDifference row) await GuardAsync(() => PreviewAsync(row));
        };
        DockPanel.SetDock(actions, Dock.Top); panel.Children.Add(actions);
        DockPanel.SetDock(_status, Dock.Bottom); panel.Children.Add(_status);
        var pair = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*"), Margin = new Thickness(8) };
        pair.Children.Add(EntryList); var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1); pair.Children.Add(splitter); Grid.SetColumn(_preview, 2); pair.Children.Add(_preview); panel.Children.Add(pair); Content = panel;
        void Button(string title, Func<Task> action)
        {
            var button = new Button { Content = title, Margin = new Thickness(4) };
            button.Click += async (_, _) => await GuardAsync(action); actions.Children.Add(button);
        }
    }
    public static bool Supports(string path) => ManagedArchive.SupportsOutput(path) || Path.GetExtension(path).Equals(".rar", StringComparison.OrdinalIgnoreCase);
    public static async Task<ArchivePanel> CreateAsync(string left, string right, CancellationToken token, Action<string>? guardOutput = null)
    {
        var panel = new ArchivePanel(left, right, token, guardOutput); await panel.GuardAsync(panel.RefreshAsync); return panel;
    }
    public async Task RefreshAsync()
    {
        if (_disposed) return;
        _operation?.Cancel(); _operation?.Dispose(); _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var version = ++_refreshVersion; var token = _operation.Token; var service = new ManagedArchive(); var leftPassword = Password(false); var rightPassword = Password(true);
        _previewVersion++; Rows = []; EntryList.ItemsSource = null; _preview.Text = ""; _status.Text = "アーカイブを比較しています…";
        try
        {
            var left = await Task.Run(() => service.ReadManifest(_leftPath, leftPassword, token), token);
            var right = await Task.Run(() => service.ReadManifest(_rightPath, rightPassword, token), token);
            token.ThrowIfCancellationRequested(); if (_disposed) return;
            Rows = ArchiveComparison.Compare(left, right); EntryList.ItemsSource = Rows;
            _status.Text = $"{left.Format} / {right.Format}: {Rows.Count} 項目、差分 {Rows.Count(row => row.Status != "Equal")} 件。格納名・型・サイズ・SHA-256で比較します。暗号化アーカイブはパスワードを入力して再比較してください。再梱包の出力は暗号化されません。全件展開は選択した親フォルダー内の新しいフォルダーへ保存します。";
        }
        catch (Exception) when (version != _refreshVersion) { }
        catch { Rows = []; EntryList.ItemsSource = null; throw; }
    }
    public async Task PreviewAsync(ArchiveEntryDifference row)
    {
        var version = ++_previewVersion; var token = _operation?.Token ?? _lifetime.Token; var text = new StringBuilder(); var service = new ManagedArchive();
        foreach (var side in new[] { false, true })
        {
            var entry = side ? row.Right : row.Left; text.AppendLine(side ? "右" : "左");
            if (entry is null) { text.AppendLine("エントリなし"); continue; }
            if (entry.IsDirectory) { text.AppendLine("ディレクトリ"); continue; }
            var path = side ? _rightPath : _leftPath; var password = Password(side);
            var bytes = await Task.Run(() => service.ReadEntryPreview(path, row.Path, password, token), token);
            for (var index = 0; index < bytes.Length; index += 16)
            {
                text.Append(index.ToString("X8")).Append("  ");
                for (var column = index; column < Math.Min(index + 16, bytes.Length); column++) text.Append(bytes[column].ToString("X2")).Append(' ');
                text.AppendLine();
            }
            text.AppendLine("先頭4096 bytesまで。全エントリを検証してから表示します。");
        }
        if (!_disposed && version == _previewVersion && !token.IsCancellationRequested) _preview.Text = text.ToString();
    }
    public Task ExportToAsync(bool rightSide, string entry, string output, CancellationToken token = default)
    {
        EnsureNewOutput(output);
        return ArchiveActions.ExportAsync(rightSide ? _rightPath : _leftPath, entry, output, Password(rightSide), token);
    }
    public Task RepackToAsync(bool rightSide, string output, CancellationToken token = default)
    {
        EnsureNewOutput(output);
        var input = rightSide ? _rightPath : _leftPath; var password = Password(rightSide);
        return Task.Run(() => new ManagedArchive().Repack(input, output, password, token), token);
    }
    public Task ExtractToAsync(bool rightSide, string directory, CancellationToken token = default)
    {
        _guardOutput?.Invoke(directory);
        var input = rightSide ? _rightPath : _leftPath; var password = Password(rightSide);
        return Task.Run(() => new ManagedArchive().ExtractAll(input, directory, password, token), token);
    }
    private async Task ExportAsync(bool rightSide)
    {
        if (EntryList.SelectedItem is not ArchiveEntryDifference row || (rightSide ? row.Right : row.Left) is not { IsDirectory: false })
        { _status.Text = "選択した側のファイルを選んでください。"; return; }
        var top = TopLevel.GetTopLevel(this); if (top is null) return;
        var output = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "アーカイブ内ファイルを別名保存", SuggestedFileName = Path.GetFileName(row.Path), ShowOverwritePrompt = true });
        if (output?.TryGetLocalPath() is not string path) return;
        await ExportToAsync(rightSide, row.Path, path, _lifetime.Token); _status.Text = "エントリを保存しました。";
    }
    private async Task RepackAsync(bool rightSide)
    {
        var top = TopLevel.GetTopLevel(this); if (top is null) return;
        var output = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "非暗号化アーカイブへ再梱包", SuggestedFileName = "repacked.7z", ShowOverwritePrompt = true, FileTypeChoices = ArchivePickers.FileTypes });
        if (output?.TryGetLocalPath() is not string path) return;
        await RepackToAsync(rightSide, path, _lifetime.Token); _status.Text = $"非暗号化アーカイブを保存しました: {path}";
    }
    private async Task ExtractAsync(bool rightSide)
    {
        var name = ExtractionName.Text?.Trim();
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0 || Path.IsPathRooted(name))
            throw new ArgumentException("展開フォルダー名には一つの名前を指定してください。");
        var top = TopLevel.GetTopLevel(this); if (top is null) return;
        var parents = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "新しい展開フォルダーを作る親フォルダーを選択" });
        if (parents.Count == 0 || parents[0].TryGetLocalPath() is not string parent) return;
        var output = Path.Combine(parent, name);
        await ExtractToAsync(rightSide, output, _lifetime.Token); _status.Text = $"すべてのエントリを展開しました: {output}";
    }
    private string? Password(bool rightSide) => string.IsNullOrEmpty(rightSide ? RightPassword.Text : LeftPassword.Text) ? null : rightSide ? RightPassword.Text : LeftPassword.Text;
    private void EnsureNewOutput(string output)
    {
        _guardOutput?.Invoke(output);
        foreach (var original in new[] { _leftPath, _rightPath })
            if (ArchivePaths.SameFile(output, original)) throw new IOException("比較元アーカイブは上書きできません。");
    }
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = "アーカイブ操作を中止しました。"; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { if (!_disposed) _status.Text = exception.Message; }
    }
    private static string Label(string status) => status switch { "Equal" => "一致", "OnlyLeft" => "左のみ", "OnlyRight" => "右のみ", "TypeChanged" => "型変更", _ => "変更" };
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _lifetime.Cancel(); _operation?.Cancel(); _operation?.Dispose(); _lifetime.Dispose();
        LeftPassword.Text = RightPassword.Text = "";
    }
}
