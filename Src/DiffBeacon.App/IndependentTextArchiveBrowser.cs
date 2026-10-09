using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

/// <summary>単側の要求経路と検証済み一覧を分離する。格納名はOS pathへ変換しない。</summary>
internal sealed class IndependentTextArchiveBrowser : UserControl, IDisposable
{
    private readonly int _side;
    private readonly Action _changed;
    private readonly CancellationToken _lifetime;
    private readonly HashSet<Task> _pendingOperations = [];
    private readonly StackPanel _passwordPanel = new() { Spacing = 4 };
    private readonly List<TextBox> _passwordBoxes = [];
    private readonly TextBlock _route = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private ArchiveSource? _requested;
    private ManagedArchiveSourceManifest? _confirmed;
    private string?[] _confirmedPasswords = [];
    private string? _leaf;
    private string? _initialLeaf;
    private CancellationTokenSource? _readCts;
    private CancellationTokenSource? _pickerCts;
    private long _generation;
    private bool _verified, _disposed, _updating, _reading;

    internal ComboBox Kind { get; } = new() { ItemsSource = new[] { "通常ファイル", "無題", "アーカイブ内のファイル" }, SelectedIndex = 1 };
    internal TextBox RootPath { get; } = new() { PlaceholderText = "通常ファイルまたはアーカイブの絶対パス" };
    internal Button Pick { get; } = new() { Content = "ファイルを選択" };
    internal CheckBox ReadOnly { get; } = new() { Content = "読取り専用" };
    internal CheckBox AllowWorkingEdit { get; } = new() { Content = "内包ファイルの作業編集を許可", IsChecked = false };
    internal Button Load { get; } = new() { Content = "一覧を検証 / 再試行" };
    internal ComboBox NameCodePage { get; } = ArchiveNameSettings.Picker();
    internal ComboBox GZipPayloadKindPicker { get; } = ArchivePayloadSettings.Picker();
    internal ComboBox CompressionPayloadKindPicker { get; } = ArchivePayloadSettings.CompressionPicker();
    internal Button OpenContainer { get; } = new() { Content = "選択したアーカイブを開く" };
    internal Button Back { get; } = new() { Content = "戻る" };
    internal ListBox Entries { get; } = new() { MinHeight = 100 };
    internal string KindName => Kind.SelectedIndex switch { 0 => "Physical", 1 => "Untitled", 2 => "Archive", _ => throw new InvalidDataException("入力種類が不正です。") };
    internal string Summary => KindName switch
    {
        "Untitled" => "無題" + (ReadOnly.IsChecked == true ? "（読取り専用）" : ""),
        "Physical" => (RootPath.Text ?? "") + (ReadOnly.IsChecked == true ? "（読取り専用）" : ""),
        _ => (_requested?.RootPath ?? RootPath.Text ?? "") + " / " + string.Join(" / ", _requested?.EntryChain ?? Array.Empty<string>())
            + " / " + (_leaf ?? "葉未選択") + (_verified ? "（検証済み）" : "（未検証）")
    };
    // pickerは実buttonの経路だけに注入する。manifest gateは下層readerを置換しない。
    internal Func<int, bool, CancellationToken, Task<string?>>? PickerPathProvider { get; set; }
    internal Func<int, ArchiveSource, CancellationToken, Task>? ManifestReadGate { get; set; }
    internal Action<string, Task>? OperationTaskObserved { get; set; }
    internal Action<string?[]>? PendingReadStateObserved { get; set; }
    internal ManagedArchiveLimits? ReadLimits { get; set; }
    private Task ObserveOperation(string operation, Task task) { OperationTaskObserved?.Invoke(operation, task); return task; }
    // 開始前に登録し、元処理のfinallyが終わってから集合から除く。
    // Closeは新規開始を止めた後、同じTask集合を非同期で待つ。
    private Task TrackOperation(Func<Task> action)
    {
        if (_disposed) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingOperations.Add(completion.Task);
        async Task Run()
        {
            try { await action(); completion.TrySetResult(); }
            catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); throw; }
            catch (Exception error) { completion.TrySetException(error); throw; }
            finally { _pendingOperations.Remove(completion.Task); }
        }
        return Run();
    }
    internal Task PendingOperationsCompletion => Task.WhenAll(_pendingOperations.ToArray());
    internal IReadOnlyList<TextBox> PasswordFields => _passwordBoxes.AsReadOnly();

    internal IndependentTextArchiveBrowser(int side, ComparisonProject? initial, Action changed, CancellationToken lifetime)
    {
        _side = side; _changed = changed; _lifetime = lifetime;
        var source = initial is null ? null : ProjectInputs.Archive(initial, side);
        var path = initial is null ? "" : ProjectInputs.PathFor(initial, side);
        var kind = source is not null ? 2 : string.IsNullOrWhiteSpace(path) ? 1 : 0;
        _updating = true;
        Kind.SelectedIndex = kind; RootPath.Text = source?.RootPath ?? path;
        ReadOnly.IsChecked = initial is not null && (side switch { 0 => initial.LeftReadOnly, 1 => initial.BaseReadOnly, _ => initial.RightReadOnly });
        // 初期projectの編集許可も明示falseだけ復元する。初期本文/working asset/passwordは持ち込まない。
        AllowWorkingEdit.IsChecked = source?.InheritedReadOnly == false;
        if (source is not null)
        {
            // 親の正規化済み比較形式に従う。containerには葉を要求せず、一覧の再検証後に選択する。
            source.Validate(container: initial!.Mode == "Archive", readOnly: true);
            _requested = source.ToSource(); _initialLeaf = source.LeafEntry;
        }
        RebuildPasswords([]); _updating = false;

        var fields = new StackPanel { Spacing = 6 };
        fields.Children.Add(Kind);
        fields.Children.Add(new TextBlock { Text = "現在のコンテナーのgzip格納名文字コード (28591 Latin1 / 65001 UTF-8 / 932 日本語)" });
        fields.Children.Add(NameCodePage);
        fields.Children.Add(new TextBlock { Text = "現在のコンテナーのgzip本文の形式" }); fields.Children.Add(GZipPayloadKindPicker);
        fields.Children.Add(new TextBlock { Text = "現在のコンテナーのBZip2／Z本文の形式" }); fields.Children.Add(CompressionPayloadKindPicker);
        CompressionPayloadKindPicker.SelectedItem = _requested?.ContainerCompressionPayloadKinds[^1] ?? CompressionPayloadKind.Auto;
        CompressionPayloadKindPicker.SelectionChanged += (_, _) => { if (_updating || _disposed) return; Mutation(); _verified = false; _leaf = null; Refresh(); };
        GZipPayloadKindPicker.SelectedItem = _requested?.ContainerGZipPayloadKinds[^1] ?? GZipPayloadKind.Auto;
        GZipPayloadKindPicker.SelectionChanged += (_, _) => { if (_updating || _disposed) return; Mutation(); _verified = false; _leaf = null; Refresh(); };
        NameCodePage.SelectedItem = _requested?.ContainerNameCodePages[^1] ?? 28591;
        GZipPayloadKindPicker.SelectedItem = _requested?.ContainerGZipPayloadKinds[^1] ?? GZipPayloadKind.Auto;
        NameCodePage.SelectionChanged += (_, _) => { if (_updating || _disposed) return; Mutation(); _verified = false; _leaf = null; Refresh(); };
        var paths = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        paths.Children.Add(RootPath); Grid.SetColumn(Pick, 1); paths.Children.Add(Pick); fields.Children.Add(paths);
        fields.Children.Add(ReadOnly); fields.Children.Add(AllowWorkingEdit);
        fields.Children.Add(_route); fields.Children.Add(_passwordPanel);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        commands.Children.Add(Load); commands.Children.Add(Back); commands.Children.Add(OpenContainer); fields.Children.Add(commands);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(8) };
        var scroll = new ScrollViewer { Content = fields, MaxHeight = 170,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        body.Children.Add(scroll); Grid.SetRow(Entries, 1); body.Children.Add(Entries); Grid.SetRow(_status, 2); body.Children.Add(_status); Content = body;

        Kind.SelectionChanged += (_, _) => ResetInput();
        // 遅延TextChangedは初期root設定も通知するため、同期の値変更だけで経路を破棄する。
        RootPath.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) ResetInput(); };
        ReadOnly.IsCheckedChanged += (_, _) => OptionsChanged();
        AllowWorkingEdit.IsCheckedChanged += (_, _) => OptionsChanged();
        Entries.SelectionChanged += (_, _) => SelectLeaf();
        Pick.Click += async (_, _) => await ObserveOperation("pick", PickAsync());
        Load.Click += async (_, _) => await ObserveOperation("load", ReadManifestAsync());
        Back.Click += async (_, _) => await ObserveOperation("back", GuardRouteAsync(BackAsync));
        OpenContainer.Click += async (_, _) => await ObserveOperation("open", GuardRouteAsync(OpenAsync));
        Refresh();
    }

    private void Mutation()
    {
        _generation++; _readCts?.Cancel(); _pickerCts?.Cancel(); _changed();
    }

    private void ResetInput()
    {
        if (_updating || _disposed) return;
        Mutation(); _requested = null; _verified = false; _leaf = _initialLeaf = null;
        Array.Clear(_confirmedPasswords); _confirmedPasswords = [];
        _updating = true; Entries.SelectedItem = null; RebuildPasswords([]); _updating = false;
        // 前一覧は残すが、新rootを検証するまでは選択を確定できない。
        _status.Text = "入力が変更されました。アーカイブは一覧を検証してください。"; Refresh();
    }

    private void OptionsChanged()
    {
        if (_updating || _disposed) return;
        Mutation(); Refresh();
    }

    private void SelectLeaf()
    {
        if (_updating || _disposed) return;
        Mutation();
        _leaf = _verified && RouteMatches() && Entries.SelectedItem is EntryRow row && !row.Entry.IsDirectory ? row.Entry.Path : null;
        Refresh();
    }

    private void RebuildPasswords(IReadOnlyList<string?> retained, int retainedCount = int.MaxValue)
    {
        foreach (var box in _passwordBoxes) { box.PropertyChanged -= PasswordChanged; box.Text = ""; }
        _passwordBoxes.Clear(); _passwordPanel.Children.Clear();
        NameCodePage.SelectedItem = _requested?.ContainerNameCodePages[^1] ?? 28591;
        GZipPayloadKindPicker.SelectedItem = _requested?.ContainerGZipPayloadKinds[^1] ?? GZipPayloadKind.Auto;
        CompressionPayloadKindPicker.SelectedItem = _requested?.ContainerCompressionPayloadKinds[^1] ?? CompressionPayloadKind.Auto;
        var count = (_requested?.EntryChain.Count ?? 0) + 1;
        for (var index = 0; index < count; index++)
        {
            var label = index == 0 ? "アーカイブのパスワード（任意）" : $"階層{index}のパスワード（任意）";
            var box = new TextBox { PasswordChar = '●', MaxLength = 4096, PlaceholderText = label, Text = index < retained.Count && index < retainedCount ? retained[index] : null };
            box.PropertyChanged += PasswordChanged;
            _passwordBoxes.Add(box); _passwordPanel.Children.Add(box);
        }
    }

    private void PasswordChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        // 撤去欄と初期化の通知は新しい経路を取消さない。利用者の現役欄への変更だけを監視する。
        if (_updating || _disposed || args.Property != TextBox.TextProperty
            || sender is not TextBox box || !_passwordBoxes.Contains(box)) return;
        Mutation(); _verified = false; _leaf = null;
        _status.Text = "パスワードを変更しました。一覧を再検証してください。"; Refresh();
    }
    private string?[] PasswordCopy()
    {
        if (_passwordBoxes.Any(box => box.Text?.Length > 4096)) throw new InvalidDataException("パスワードは各4096文字以下で入力してください。");
        return _passwordBoxes.Select(box => string.IsNullOrEmpty(box.Text) ? null : box.Text).ToArray();
    }

    private bool RouteMatches() => _requested is not null && _confirmed is not null
        && StringComparer.Ordinal.Equals(_requested.RootPath, _confirmed.Source.RootPath)
        && _requested.EntryChain.SequenceEqual(_confirmed.Source.EntryChain)
        && _requested.ContainerNameCodePages.SequenceEqual(_confirmed.Source.ContainerNameCodePages)
        && _requested.ContainerGZipPayloadKinds.SequenceEqual(_confirmed.Source.ContainerGZipPayloadKinds)
        && _requested.ContainerCompressionPayloadKinds.SequenceEqual(_confirmed.Source.ContainerCompressionPayloadKinds)
        && (_requested.RootSha256 is null || _requested.RootSha256 == _confirmed.Source.RootSha256);

    private void ChangeRoute(ArchiveSource source, int retainedCount)
    {
        // 未検証の入力欄は持ち越さず、同じ確定root/SHA/共通prefixの検証済み外側だけ保持する。
        var common = 0;
        if (_confirmed is not null && source.RootSha256 is not null
            && source.RootPath == _confirmed.Source.RootPath && source.RootSha256 == _confirmed.Source.RootSha256)
        {
            while (common < source.EntryChain.Count && common < _confirmed.Source.EntryChain.Count
                && source.EntryChain[common] == _confirmed.Source.EntryChain[common]) common++;
            retainedCount = Math.Min(retainedCount, common + 1);
        }
        else retainedCount = 0;
        retainedCount = Math.Min(retainedCount, _confirmedPasswords.Length);
        var passwords = new string?[retainedCount];
        Array.Copy(_confirmedPasswords, passwords, retainedCount);
        for (var index = retainedCount; index < _confirmedPasswords.Length; index++) _confirmedPasswords[index] = null;
        try
        {
            Mutation(); _requested = source; _verified = false; _leaf = _initialLeaf = null;
            _updating = true;
            Entries.SelectedItem = null; RebuildPasswords(passwords, retainedCount);
        }
        finally { _updating = false; Array.Clear(passwords); }
        Refresh();
    }

    internal Task ReadManifestAsync() => TrackOperation(ReadManifestCoreAsync);

    private async Task ReadManifestCoreAsync()
    {
        if (_disposed || _reading || KindName != "Archive") return;
        string?[]? passwords = null;
        CancellationTokenSource? read = null;
        var generation = _generation;
        try
        {
            var root = AbsolutePath();
            _requested ??= new ArchiveSource(root);
            _requested = ArchivePayloadSettings.WithChoices(_requested, _requested.EntryChain.Count,
                ArchivePayloadSettings.Selected(GZipPayloadKindPicker), ArchivePayloadSettings.SelectedCompression(CompressionPayloadKindPicker))
                .WithNameCodePage(_requested.EntryChain.Count, ArchiveNameSettings.Selected(NameCodePage));
            if (_requested.EntryChain.Count > 8) throw new InvalidDataException("内包アーカイブの階層は8段までです。");
            var request = _requested;
            passwords = PasswordCopy(); PendingReadStateObserved?.Invoke(passwords); Mutation(); _verified = false; _leaf = null;
            generation = _generation;
            read = CancellationTokenSource.CreateLinkedTokenSource(_lifetime); _readCts = read; _reading = true;
            _status.Text = "アーカイブと格納項目を検証しています。"; Refresh();
            if (ManifestReadGate is { } gate) await gate(_side, request, read.Token);
            var result = await Task.Run(() => new ManagedArchive(ReadLimits).ResolveManifest(request, passwords, read.Token), read.Token);
            read.Token.ThrowIfCancellationRequested();
            if (_disposed || generation != _generation || !ReferenceEquals(_requested, request)) return;
            if (result.Source.RootSha256 is null || result.Source.EntryChain.Count > 8
                || result.Source.RootPath != request.RootPath || !result.Source.EntryChain.SequenceEqual(request.EntryChain))
                throw new InvalidDataException("検証結果の経路が要求と一致しません。");
            _confirmed = result; _requested = result.Source; _verified = true;
            Array.Clear(_confirmedPasswords); _confirmedPasswords = passwords.ToArray();
            _updating = true;
            var rows = result.Manifest.Entries.Select(entry => new EntryRow(entry)).ToArray();
            Entries.ItemsSource = rows;
            var restored = rows.SingleOrDefault(row => !row.Entry.IsDirectory && row.Entry.Path == _initialLeaf);
            Entries.SelectedItem = restored; _leaf = restored?.Entry.Path; _initialLeaf = null;
            _updating = false; _status.Text = "一覧を検証しました。比較するファイルを選択してください。";
            _changed();
        }
        catch (OperationCanceledException) { if (!_disposed && generation == _generation && (read is null || ReferenceEquals(_readCts, read))) _status.Text = "一覧の読込みを中止しました。"; }
        catch (Exception)
        {
            // 下層例外は成功へ変換しない。passwordが例外に混ざらない固定文だけを表示する。
            if (!_disposed && generation == _generation && (read is null || ReferenceEquals(_readCts, read)))
                _status.Text = "一覧を検証できませんでした。入力、全階層のパスワード、破損、形式、上限を確認してください。";
        }
        finally
        {
            if (passwords is not null) Array.Clear(passwords);
            if (ReferenceEquals(_readCts, read)) { _readCts = null; _reading = false; }
            read?.Dispose(); _updating = false; if (!_disposed) Refresh();
        }
    }

    private async Task OpenAsync()
    {
        if (_disposed || _reading || !_verified || !RouteMatches() || Entries.SelectedItem is not EntryRow row || row.Entry.IsDirectory) return;
        // 明示的な開封では拡張子で拒否せず、共通読込みで形式と全格納項目を検証する。
        if (_confirmed!.Source.EntryChain.Count >= 8) { _status.Text = "内包アーカイブの階層は8段までです。"; return; }
        ChangeRoute(_confirmed.Source.WithChild(row.Entry.Path), _passwordBoxes.Count);
        await ReadManifestAsync();
    }

    private Task GuardRouteAsync(Func<Task> action) => TrackOperation(() => GuardRouteCoreAsync(action));

    private async Task GuardRouteCoreAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) _status.Text = "格納階層へ移動できませんでした。経路と入力条件を確認してください。"; }
    }

    private async Task BackAsync()
    {
        if (_disposed || _reading || _requested is null || _requested.EntryChain.Count == 0) return;
        var chain = _requested.EntryChain.Take(_requested.EntryChain.Count - 1).ToArray();
        ChangeRoute(new ArchiveSource(_requested.RootPath, chain, _requested.RootSha256, _requested.ContainerNameCodePages.Take(chain.Length + 1).ToArray(), _requested.ContainerGZipPayloadKinds.Take(chain.Length + 1).ToArray(), _requested.ContainerCompressionPayloadKinds.Take(chain.Length + 1).ToArray()), chain.Length + 1);
        await ReadManifestAsync();
    }

    private Task PickAsync() => TrackOperation(PickCoreAsync);

    private async Task PickCoreAsync()
    {
        if (_disposed || KindName == "Untitled") return;
        Mutation(); var generation = _generation;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _pickerCts = request;
        // 通常pickerも世代で採用を抑止する。StorageProviderは取消tokenを受けない。
        try
        {
            var path = PickerPathProvider is { } provider ? await provider(_side, KindName == "Archive", request.Token)
                : (await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                { Title = KindName == "Archive" ? "アーカイブを選択" : "比較するテキストファイルを選択", AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
            request.Token.ThrowIfCancellationRequested();
            if (!_disposed && generation == _generation && path is not null) RootPath.Text = path;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed && generation == _generation) _status.Text = "ファイルを選択できませんでした。"; }
        finally { if (ReferenceEquals(_pickerCts, request)) _pickerCts = null; }
    }

    private string AbsolutePath()
    {
        var value = RootPath.Text ?? "";
        if (!Path.IsPathFullyQualified(value) || Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            throw new InvalidDataException("物理入力には絶対ファイルパスを指定してください。");
        return Path.GetFullPath(value);
    }

    internal (string Path, ArchiveProjectInput? Archive, bool ReadOnly, string?[] Passwords) Capture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reading) throw new InvalidOperationException("一覧の検証が終わるまで待ってください。");
        if (KindName == "Untitled") return ("", null, ReadOnly.IsChecked == true, []);
        if (KindName == "Physical") return (AbsolutePath(), null, ReadOnly.IsChecked == true, []);
        if (!_verified || !RouteMatches() || _leaf is null || _confirmed!.Manifest.Entries.Count(entry => entry.Path == _leaf && !entry.IsDirectory) != 1)
            throw new InvalidOperationException("現在の経路を検証して実在ファイルを選択してください。");
        var input = new ArchiveProjectInput { RootPath = _confirmed.Source.RootPath, EntryChain = _confirmed.Source.EntryChain.ToArray(),
            ContainerNameCodePages = ArchiveNameSettings.Capture(_confirmed.Source.ContainerNameCodePages), ContainerGZipPayloadKinds = ArchivePayloadSettings.Capture(_confirmed.Source.ContainerGZipPayloadKinds), ContainerCompressionPayloadKinds = ArchivePayloadSettings.Capture(_confirmed.Source.ContainerCompressionPayloadKinds),
            LeafEntry = _leaf, RootSha256 = _confirmed.Source.RootSha256, InheritedReadOnly = AllowWorkingEdit.IsChecked != true };
        input.Validate(container: false, readOnly: true);
        return ("", input, true, PasswordCopy());
    }

    internal void AbortPendingRead()
    {
        if (_disposed) return;
        _generation++; _readCts?.Cancel(); _pickerCts?.Cancel();
        if (_reading) { _verified = false; _leaf = null; }
        Refresh();
    }

    private void Refresh()
    {
        var archive = KindName == "Archive";
        RootPath.IsEnabled = Pick.IsEnabled = KindName != "Untitled";
        ReadOnly.IsVisible = !archive; AllowWorkingEdit.IsVisible = archive;
        _passwordPanel.IsVisible = Load.IsVisible = Back.IsVisible = OpenContainer.IsVisible = Entries.IsVisible = _route.IsVisible = archive;
        Load.IsEnabled = !_reading; Back.IsEnabled = !_reading && _requested?.EntryChain.Count > 0;
        OpenContainer.IsEnabled = !_reading && _verified && RouteMatches() && Entries.SelectedItem is EntryRow row && !row.Entry.IsDirectory;
        _route.Text = _requested is null ? "アーカイブは未検証です" : string.Join(" / ", new[] { _requested.RootPath }.Concat(_requested.EntryChain));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++; _readCts?.Cancel(); _pickerCts?.Cancel();
        _updating = true; foreach (var box in _passwordBoxes) { box.PropertyChanged -= PasswordChanged; box.Text = ""; }
        Array.Clear(_confirmedPasswords); _confirmedPasswords = [];
        _passwordBoxes.Clear(); _passwordPanel.Children.Clear(); _leaf = _initialLeaf = null; _requested = null; _confirmed = null;
    }

    private sealed record EntryRow(ManagedArchiveEntry Entry)
    {
        public override string ToString() => Entry.Path + (Entry.IsDirectory ? " （フォルダー）" : $" （{Entry.Size} bytes）");
    }
}
