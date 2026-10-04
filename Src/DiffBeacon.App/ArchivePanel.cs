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
    private readonly ArchiveSource _requestedLeft, _requestedRight;
    private ArchiveSource _confirmedLeft, _confirmedRight;
    private readonly string?[] _leftAncestors, _rightAncestors;
    private readonly IReadOnlyList<string>? _leftMissing, _rightMissing;
    private readonly Action<string>? _guardOutput;
    private readonly CancellationTokenSource _lifetime;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _previewOperation;
    private CancellationTokenSource? _writeOperation;
    private CancellationTokenSource? _childOperation;
    private int _childGeneration;
    private int _previewVersion;
    private int _refreshVersion;
    private bool _disposed;
    private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _preview = new() { Name = "archive-preview", IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Mono, Menlo, monospace") };
    private readonly ScrollViewer _toolbar;
    private readonly ScrollViewer _statusViewport;
    public ListBox EntryList { get; } = new() { Name = "archive-entries" };
    public ComboBox EntryKind { get; } = new() { Name = "archive-entry-kind", ItemsSource = new[] { "自動", "テキスト", "バイナリ", "アーカイブ" }, SelectedIndex = 0, Margin = new Thickness(4) };
    public Button OpenEntryButton { get; } = new() { Name = "archive-open-entry", Content = "選択項目を比較", Margin = new Thickness(4), IsEnabled = false };
    public TextBox LeftPassword { get; } = new() { Name = "archive-left-password", PasswordChar = '●', MaxLength = 4096, PlaceholderText = "左のパスワード（任意）", Width = 200, Margin = new Thickness(4) };
    public TextBox RightPassword { get; } = new() { Name = "archive-right-password", PasswordChar = '●', MaxLength = 4096, PlaceholderText = "右のパスワード（任意）", Width = 200, Margin = new Thickness(4) };
    public TextBox ExtractionName { get; } = new() { Name = "archive-extraction-name", Text = "extracted", PlaceholderText = "新しい展開フォルダー名", Width = 200, Margin = new Thickness(4) };
    public IReadOnlyList<ArchiveEntryDifference> Rows { get; private set; } = [];
    public string PreviewText => _preview.Text ?? "";
    public string StatusText => _status.Text ?? "";
    internal Action? RefreshReadyForAdoption { get; set; }
    internal Action? ExportReadStarting { get; set; }
    internal Action? ExportReadyForPublication { get; set; }
    internal string LeftSourcePath => _leftPath;
    internal string RightSourcePath => _rightPath;
    internal bool IsDisposed => _disposed;
    internal ArchiveSource ConfirmedLeft => _confirmedLeft;
    internal ArchiveSource ConfirmedRight => _confirmedRight;
    internal Func<ArchivePanel, ArchiveOpenRequest, Task>? OpenEntryRequested { get; set; }

    private ArchivePanel(ArchiveSource left, ArchiveSource right, Action<string>? guardOutput,
        IReadOnlyList<string?>? leftAncestors = null, IReadOnlyList<string?>? rightAncestors = null,
        IReadOnlyList<string>? leftMissing = null, IReadOnlyList<string>? rightMissing = null)
    {
        _leftPath = left.RootPath; _rightPath = right.RootPath; _requestedLeft = _confirmedLeft = left; _requestedRight = _confirmedRight = right;
        _leftMissing = Missing(left, leftMissing); _rightMissing = Missing(right, rightMissing);
        _leftAncestors = Ancestors(left, leftAncestors);
        try { _rightAncestors = Ancestors(right, rightAncestors); }
        catch { Array.Clear(_leftAncestors); throw; }
        _guardOutput = guardOutput; _lifetime = new CancellationTokenSource();
        var panel = new DockPanel(); var actions = new WrapPanel(); actions.Children.Add(LeftPassword); actions.Children.Add(RightPassword);
        var sources = new TextBlock { Name = "archive-confirmed-sources", Text = $"左: {SourceCaption(left, _leftMissing)}\n右: {SourceCaption(right, _rightMissing)}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4) };
        Button("アーカイブを再比較", RefreshAsync);
        actions.Children.Add(EntryKind); actions.Children.Add(OpenEntryButton);
        OpenEntryButton.Click += async (_, _) => await GuardAsync(OpenSelectedAsync);
        EntryList.DoubleTapped += async (_, _) => await GuardAsync(OpenSelectedAsync);
        Button("左エントリを書き出す", () => ExportAsync(false)); Button("右エントリを書き出す", () => ExportAsync(true));
        Button("左を再梱包", () => RepackAsync(false), left.EntryChain.Count == 0 && _leftMissing is null); Button("右を再梱包", () => RepackAsync(true), right.EntryChain.Count == 0 && _rightMissing is null);
        actions.Children.Add(ExtractionName);
        Button("左をすべて展開", () => ExtractAsync(false), left.EntryChain.Count == 0 && _leftMissing is null); Button("右をすべて展開", () => ExtractAsync(true), right.EntryChain.Count == 0 && _rightMissing is null);
        EntryList.ItemTemplate = new FuncDataTemplate<ArchiveEntryDifference>((row, _) => new TextBlock
        {
            Text = row is null ? "" : $"{Label(row.Status),-7} {row.Path}   左 {row.Left?.Size.ToString("N0") ?? "—"} / 右 {row.Right?.Size.ToString("N0") ?? "—"}",
            Margin = new Thickness(6), FontFamily = new FontFamily("Cascadia Mono, Menlo, monospace")
        });
        EntryList.SelectionChanged += async (_, _) =>
        {
            OpenEntryButton.IsEnabled = EntryList.SelectedItem is ArchiveEntryDifference selected && CanOpen(selected);
            if (EntryList.SelectedItem is ArchiveEntryDifference row) await GuardAsync(() => PreviewAsync(row));
        };
        var toolbarContent = new StackPanel(); toolbarContent.Children.Add(sources); toolbarContent.Children.Add(actions);
        _toolbar = new ScrollViewer { Name = "archive-toolbar", Content = toolbarContent, MaxHeight = 160,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _statusViewport = new ScrollViewer { Content = _status, MaxHeight = 80,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        DockPanel.SetDock(_toolbar, Dock.Top); panel.Children.Add(_toolbar);
        DockPanel.SetDock(_statusViewport, Dock.Bottom); panel.Children.Add(_statusViewport);
        SizeChanged += (_, _) =>
        {
            if (Bounds.Height <= 0) return;
            _toolbar.MaxHeight = Math.Clamp(Bounds.Height * .35, 40, 160);
            _statusViewport.MaxHeight = Math.Clamp(Bounds.Height * .2, 24, 80);
        };
        var pair = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*"), Margin = new Thickness(8) };
        pair.Children.Add(EntryList); var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1); pair.Children.Add(splitter); Grid.SetColumn(_preview, 2); pair.Children.Add(_preview); panel.Children.Add(pair); Content = panel;
        void Button(string title, Func<Task> action, bool enabled = true)
        {
            var button = new Button { Content = title, Margin = new Thickness(4), IsEnabled = enabled };
            button.Click += async (_, _) => await GuardAsync(action); actions.Children.Add(button);
        }
    }
    public static bool Supports(string path) => ManagedArchive.SupportsInput(path);
    public static Task<ArchivePanel> CreateAsync(string left, string right, CancellationToken token, Action<string>? guardOutput = null)
        => CreateWithPasswordsAsync(left, right, token, guardOutput, null, null);
    internal static async Task<ArchivePanel> CreateWithPasswordsAsync(string left, string right, CancellationToken token,
        Action<string>? guardOutput, string? leftPassword, string? rightPassword)
    {
        return await CreateForSourcesAsync(new(Path.GetFullPath(left)), new(Path.GetFullPath(right)), token, guardOutput, [], [], leftPassword, rightPassword);
    }
    internal static async Task<ArchivePanel> CreateForSourcesAsync(ArchiveSource left, ArchiveSource right, CancellationToken token,
        Action<string>? guardOutput, IReadOnlyList<string?> leftAncestors, IReadOnlyList<string?> rightAncestors,
        string? leftPassword = null, string? rightPassword = null,
        IReadOnlyList<string>? leftMissing = null, IReadOnlyList<string>? rightMissing = null)
    {
        var panel = new ArchivePanel(left, right, guardOutput, leftAncestors, rightAncestors, leftMissing, rightMissing);
        try
        {
            panel.LeftPassword.Text = leftPassword; panel.RightPassword.Text = rightPassword;
            await panel.RefreshCoreAsync(token); token.ThrowIfCancellationRequested(); return panel;
        }
        catch { panel.Dispose(); throw; }
    }
    internal void CancelOperation() { if (_disposed) return; _operation?.Cancel(); _previewOperation?.Cancel(); _writeOperation?.Cancel(); _childOperation?.Cancel(); }
    internal void CancelChildOperation() { if (_disposed) return; _childGeneration++; _childOperation?.Cancel(); }
    public Task RefreshAsync() => RefreshCoreAsync(CancellationToken.None);
    private async Task RefreshCoreAsync(CancellationToken callerToken)
    {
        if (_disposed) return;
        _operation?.Cancel(); _operation?.Dispose();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, callerToken); _operation = operation;
        _previewOperation?.Cancel();
        CancelChildOperation();
        var version = ++_refreshVersion; var token = _operation.Token; var service = new ManagedArchive();
        string?[]? leftPasswords = null, rightPasswords = null;
        _previewVersion++;
        try
        {
            leftPasswords = Passwords(false); rightPasswords = Passwords(true);
            var left = await Task.Run(() => service.ResolveManifest(_requestedLeft, leftPasswords, token), token);
            var right = await Task.Run(() => service.ResolveManifest(_requestedRight, rightPasswords, token), token);
            if (_leftMissing is not null) { ProjectInputReader.EnsureAbsent(left.Manifest, _leftMissing[0], token); left = left with { Manifest = new("不在", []) }; }
            if (_rightMissing is not null) { ProjectInputReader.EnsureAbsent(right.Manifest, _rightMissing[0], token); right = right with { Manifest = new("不在", []) }; }
            var candidate = ArchiveComparison.Compare(left.Manifest, right.Manifest);
            RefreshReadyForAdoption?.Invoke();
            token.ThrowIfCancellationRequested(); if (_disposed || version != _refreshVersion) return;
            _confirmedLeft = left.Source; _confirmedRight = right.Source;
            Rows = candidate; EntryList.ItemsSource = Rows; _preview.Text = "";
            _status.Text = $"{left.Manifest.Format} / {right.Manifest.Format}: {Rows.Count} 項目、差分 {Rows.Count(row => row.Status != "Equal")} 件。格納名・型・サイズ・SHA-256で比較します。暗号化アーカイブはパスワードを入力して再比較してください。再梱包の出力は暗号化されません。全件展開は選択した親フォルダー内の新しいフォルダーへ保存します。";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && version != _refreshVersion) { }
        finally { if (leftPasswords is not null) Array.Clear(leftPasswords); if (rightPasswords is not null) Array.Clear(rightPasswords); if (ReferenceEquals(_operation, operation)) _operation = null; }
    }
    public async Task PreviewAsync(ArchiveEntryDifference row)
    {
        if (_disposed || !Rows.Any(item => ReferenceEquals(item, row))) return;
        _previewOperation?.Cancel(); _previewOperation?.Dispose();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _previewOperation = operation;
        var version = ++_previewVersion; var token = operation.Token; var text = new StringBuilder(); var service = new ManagedArchive();
        try
        {
        foreach (var side in new[] { false, true })
        {
            var entry = side ? row.Right : row.Left; text.AppendLine(side ? "右" : "左");
            if (entry is null) { text.AppendLine("エントリなし"); continue; }
            if (entry.IsDirectory) { text.AppendLine("ディレクトリ"); continue; }
            var source = side ? _confirmedRight : _confirmedLeft;
            var passwords = Passwords(side); byte[] bytes;
            try { bytes = await Task.Run(() => service.ResolveEntryPreview(source, row.Path, passwords, token), token); }
            finally { Array.Clear(passwords); }
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
        catch (Exception exception) when (exception is not OutOfMemoryException && version != _previewVersion) { }
        finally { if (ReferenceEquals(_previewOperation, operation)) _previewOperation = null; }
    }
    public Task ExportToAsync(bool rightSide, string entry, string output, CancellationToken token = default)
    {
        EnsurePresentSide(rightSide);
        EnsureNewOutput(output);
        return RunWriteAsync(cancellation => ExportSourceAsync(rightSide, entry, output, cancellation), token);
    }
    private async Task ExportSourceAsync(bool rightSide, string entry, string output, CancellationToken token)
    {
        var passwords = Passwords(rightSide);
        try
        {
            ExportReadStarting?.Invoke(); token.ThrowIfCancellationRequested();
            await ArchiveActions.ExportSourceAsync(rightSide ? _confirmedRight : _confirmedLeft, entry, output, null, new(), passwords, token, ExportReadyForPublication);
        }
        finally { Array.Clear(passwords); }
    }
    public Task RepackToAsync(bool rightSide, string output, CancellationToken token = default)
    {
        EnsurePresentSide(rightSide);
        if ((rightSide ? _requestedRight : _requestedLeft).EntryChain.Count != 0) throw new InvalidOperationException("内側アーカイブの再梱包は未対応です。");
        EnsureNewOutput(output);
        var input = rightSide ? _rightPath : _leftPath; var password = Password(rightSide);
        return RunWriteAsync(cancellation => Task.Run(() => new ManagedArchive().Repack(input, output, password, cancellation), cancellation), token);
    }
    public Task ExtractToAsync(bool rightSide, string directory, CancellationToken token = default)
    {
        EnsurePresentSide(rightSide);
        if ((rightSide ? _requestedRight : _requestedLeft).EntryChain.Count != 0) throw new InvalidOperationException("内側アーカイブの全件展開は未対応です。");
        _guardOutput?.Invoke(directory);
        var input = rightSide ? _rightPath : _leftPath; var password = Password(rightSide);
        return RunWriteAsync(cancellation => Task.Run(() => new ManagedArchive().ExtractAll(input, directory, password, cancellation), cancellation), token);
    }
    private async Task RunWriteAsync(Func<CancellationToken, Task> action, CancellationToken callerToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writeOperation?.Cancel(); _writeOperation?.Dispose();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, callerToken); _writeOperation = operation;
        try { await action(operation.Token); }
        finally { if (ReferenceEquals(_writeOperation, operation)) _writeOperation = null; }
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
    private string? Password(bool rightSide)
    {
        var value = rightSide ? RightPassword.Text : LeftPassword.Text;
        if (value?.Length > 4096) throw new InvalidDataException("パスワードの文字数上限を超えました。");
        return string.IsNullOrEmpty(value) ? null : value;
    }
    private string?[] Passwords(bool rightSide)
    {
        var ancestors = rightSide ? _rightAncestors : _leftAncestors;
        var result = new string?[ancestors.Length + 1]; Array.Copy(ancestors, result, ancestors.Length);
        try { result[^1] = Password(rightSide); return result; }
        catch { Array.Clear(result); throw; }
    }
    private static string?[] Ancestors(ArchiveSource source, IReadOnlyList<string?>? passwords)
    {
        if (passwords is not null && (passwords.Count != source.EntryChain.Count || passwords.Any(value => value?.Length > 4096)))
            throw new ArgumentException("格納階層のパスワード指定が不正です。");
        return passwords?.ToArray() ?? new string?[source.EntryChain.Count];
    }
    private static string SourceCaption(ArchiveSource source, IReadOnlyList<string>? missing = null)
        => string.Join(" / ", new[] { source.RootPath }.Concat(source.EntryChain).Concat(missing ?? [])) + (missing is null ? "" : "（存在しない）");
    private static IReadOnlyList<string>? Missing(ArchiveSource source, IReadOnlyList<string>? missing)
    {
        if (missing is null) return null;
        if (missing.Count == 0 || missing.Count + source.EntryChain.Count > 8)
            throw new InvalidDataException("不在アーカイブの格納階層が不正です。");
        return new ArchiveSource(source.RootPath, missing).EntryChain;
    }
    private void EnsurePresentSide(bool rightSide)
    {
        if ((rightSide ? _rightMissing : _leftMissing) is not null)
            throw new InvalidOperationException("存在しない側の書出し・再梱包・展開はできません。");
    }
    private static bool CanOpen(ArchiveEntryDifference row)
        => (row.Left is { IsDirectory: false } || row.Right is { IsDirectory: false })
            && row.Left is not { IsDirectory: true } && row.Right is not { IsDirectory: true };
    internal bool IsCurrent(ArchiveOpenRequest request) => !_disposed && request.Generation == _childGeneration
        && !request.Token.IsCancellationRequested && ReferenceEquals(request.LeftSource, _confirmedLeft) && ReferenceEquals(request.RightSource, _confirmedRight);
    public async Task OpenSelectedAsync()
    {
        if (_disposed || EntryList.SelectedItem is not ArchiveEntryDifference row || !Rows.Any(item => ReferenceEquals(item, row)))
            throw new InvalidOperationException("現在の一覧からファイルを選択してください。");
        if (!CanOpen(row)) throw new InvalidOperationException("比較するファイルを選択してください。ディレクトリは開けません。");
        var callback = OpenEntryRequested ?? throw new InvalidOperationException("比較タブから内包項目を開いてください。");
        _childOperation?.Cancel(); _childOperation?.Dispose();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _childOperation = operation; var generation = ++_childGeneration;
        string?[]? left = null, right = null;
        try
        {
            left = Passwords(false); right = Passwords(true);
            var mode = (ArchiveEntryOpenMode)EntryKind.SelectedIndex;
            if (!Enum.IsDefined(mode)) throw new InvalidOperationException("内包項目の比較形式を選択してください。");
            await callback(this, new(_confirmedLeft, _confirmedRight, row, mode, left, right, generation, operation.Token, _leftMissing, _rightMissing));
        }
        finally { if (left is not null) Array.Clear(left); if (right is not null) Array.Clear(right); if (ReferenceEquals(_childOperation, operation)) _childOperation = null; }
    }
    private void EnsureNewOutput(string output)
    {
        foreach (var original in new[] { _leftPath, _rightPath })
            if (ArchivePaths.SameFile(output, original)) throw new IOException("比較元アーカイブは上書きできません。");
        _guardOutput?.Invoke(output);
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
        if (_disposed) return; _disposed = true; _lifetime.Cancel(); _operation?.Cancel(); _previewOperation?.Cancel(); _writeOperation?.Cancel(); _childOperation?.Cancel(); _operation?.Dispose(); _lifetime.Dispose();
        LeftPassword.Text = RightPassword.Text = "";
        Array.Clear(_leftAncestors); Array.Clear(_rightAncestors); OpenEntryRequested = null;
    }
}
