using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace DiffBeacon.App;

/// <summary>callback完了までdialogが所有する選択。passwordを永続化しない。</summary>
internal sealed class IndependentTextInputSelection : IDisposable
{
    public ComparisonProject Project { get; }
    public string?[][] Passwords { get; }
    public long Generation { get; }
    private bool _disposed;

    internal IndependentTextInputSelection(ComparisonProject project, IReadOnlyList<string?[]> passwords, long generation)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (passwords.Count != 3) throw new ArgumentException("三側のパスワード配列が必要です。", nameof(passwords));
        project.TextInputs?.Validate(project);
        if (!ProjectInputs.IsIndependentText(project)) throw new InvalidDataException("独立三側Textのdescriptorが必要です。");
        for (var side = 0; side < 3; side++)
        {
            var input = ProjectInputs.Archive(project, side);
            input?.Validate(container: false, readOnly: true);
            if (passwords[side].Length != (input is null ? 0 : input.EntryChain.Length + 1)
                || passwords[side].Any(password => password?.Length > 4096))
                throw new InvalidDataException("選択のパスワード階層または長さが不正です。");
        }
        Project = project with { TextInputs = project.TextInputs!.Copy(), LeftArchiveInput = project.LeftArchiveInput?.Copy(),
            BaseArchiveInput = project.BaseArchiveInput?.Copy(), RightArchiveInput = project.RightArchiveInput?.Copy() };
        Passwords = passwords.Select(chain => chain.ToArray()).ToArray(); Generation = generation;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var chain in Passwords) Array.Clear(chain);
        Array.Clear(Passwords);
    }
}

/// <summary>三側をまとめて候補へ渡す。trueを返した同世代callbackだけ成功closeする。</summary>
internal sealed class IndependentTextInputDialog : Window
{
    private readonly Func<IndependentTextInputSelection, CancellationToken, Task<bool>> _accept;
    private readonly ComparisonProject _options;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _lifetimeRegistration;
    private readonly IndependentTextArchiveBrowser[] _sides;
    private readonly TextBlock[] _summaries = new TextBlock[3];
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? _submitCts;
    private Task _submissionCompletion = Task.CompletedTask;
    private long _generation;
    private bool _submitting, _closed, _ready;

    public long Generation => _generation;
    internal Button Compare { get; } = new() { Content = "比較", MinWidth = 90 };
    internal Button Abort { get; } = new() { Content = "中止", MinWidth = 90 };
    internal Button Cancel { get; } = new() { Content = "取消", MinWidth = 90 };
    internal ComboBox Pair { get; } = new() { ItemsSource = new[] { "左と中央", "中央と右", "左と右" }, SelectedIndex = 0, MinWidth = 170 };
    internal TabControl SideTabs { get; } = new();
    internal IndependentTextArchiveBrowser Side(int side) => _sides[side];

    internal IndependentTextInputDialog(Func<IndependentTextInputSelection, CancellationToken, Task<bool>> accept,
        ComparisonProject? initial = null, CancellationToken lifetime = default)
    {
        _accept = accept ?? throw new ArgumentNullException(nameof(accept));
        // 比較設定だけを保持し、旧内包入力の作業本文byteをdialogへ残さない。
        _options = initial is null ? new ComparisonProject() : initial with
        {
            LeftArchiveInput = null, BaseArchiveInput = null, RightArchiveInput = null
        };
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Title = "三側のText入力を選択"; Width = 1000; Height = 680; MinWidth = 850; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _sides = Enumerable.Range(0, 3).Select(side => new IndependentTextArchiveBrowser(side, initial, Changed, _lifetime.Token)).ToArray();
        Pair.SelectedIndex = initial?.TextComparisonPair switch { "MiddleRight" => 1, "LeftRight" => 2, _ => 0 };

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(12) };
        var summaries = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
        var names = new[] { "左", "中央", "右" };
        for (var side = 0; side < 3; side++)
        {
            // 長いrootでもsummaryが一覧領域を奪わない。全文は各側scrollbodyに表示する。
            var line = new TextBlock { MaxHeight = 24, TextTrimming = TextTrimming.CharacterEllipsis };
            _summaries[side] = line; summaries.Children.Add(line);
        }
        layout.Children.Add(summaries);
        SideTabs.ItemsSource = Enumerable.Range(0, 3).Select(side => new TabItem { Header = names[side], Content = _sides[side] }).ToArray();
        SideTabs.SelectedIndex = 0; Grid.SetRow(SideTabs, 1); layout.Children.Add(SideTabs);
        var footer = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(_status);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        commands.Children.Add(new TextBlock { Text = "比較する側", VerticalAlignment = VerticalAlignment.Center });
        commands.Children.Add(Pair); commands.Children.Add(Compare); commands.Children.Add(Abort); commands.Children.Add(Cancel);
        footer.Children.Add(commands); Grid.SetRow(footer, 2); layout.Children.Add(footer); Content = layout;

        Pair.SelectionChanged += (_, _) =>
        {
            if (!_ready || _closed) return;
            foreach (var side in _sides) side.AbortPendingRead();
            Changed();
        };
        Compare.Click += async (_, _) =>
        {
            var submission = SubmitAsync(); SubmissionTaskObserved?.Invoke(submission); await submission;
        };
        Abort.Click += (_, _) => AbortOperations();
        Cancel.Click += (_, _) => { AbortOperations(); Close(false); };
        Closed += (_, _) => EndLifetime();
        _ready = true; Refresh();
        // 登録callbackは他threadからも来るためUIの閉鎖はdispatchする。
        _lifetimeRegistration = lifetime.Register(() => Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            AbortOperations(); Close(false);
        }));
    }

    private void Changed()
    {
        if (!_ready || _closed) return;
        _generation++; _submitCts?.Cancel(); Refresh();
    }

    private void Refresh()
    {
        var names = new[] { "左", "中央", "右" };
        for (var side = 0; side < 3; side++) _summaries[side].Text = names[side] + ": " + _sides[side].Summary;
        Compare.IsEnabled = !_submitting && !_closed;
        Abort.IsEnabled = !_closed;
    }

    private void AbortOperations()
    {
        if (_closed) return;
        foreach (var side in _sides) side.AbortPendingRead();
        Changed(); _status.Text = "処理を中止しました。入力を確認して再試行できます。";
    }

    internal Action<Task>? SubmissionTaskObserved { get; set; }
    internal Task PendingOperationsCompletion => Task.WhenAll(new[] { _submissionCompletion }.Concat(_sides.Select(side => side.PendingOperationsCompletion)));
    internal Action<IndependentTextInputSelection, string?[][]>? SubmissionStateObserved { get; set; }

    internal async Task SubmitAsync()
    {
        if (_closed || _submitting) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _submissionCompletion = completion.Task;
        IndependentTextInputSelection? selection = null;
        var passwords = new string?[3][];
        CancellationTokenSource? submit = null;
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            var left = _sides[0].Capture(); passwords[0] = left.Passwords;
            var middle = _sides[1].Capture(); passwords[1] = middle.Passwords;
            var right = _sides[2].Capture(); passwords[2] = right.Passwords;
            var project = _options with
            {
                Mode = "Text", ProviderId = null,
                LeftPath = left.Path, BasePath = middle.Path, RightPath = right.Path,
                LeftReadOnly = left.ReadOnly, BaseReadOnly = middle.ReadOnly, RightReadOnly = right.ReadOnly,
                LeftArchiveInput = left.Archive, BaseArchiveInput = middle.Archive, RightArchiveInput = right.Archive,
                LeftDescription = null, BaseDescription = null, RightDescription = null,
                TextInputs = new TextInputDescriptor { Semantics = "Independent", Left = new TextInputSide { Kind = _sides[0].KindName },
                    Middle = new TextInputSide { Kind = _sides[1].KindName }, Right = new TextInputSide { Kind = _sides[2].KindName } },
                TextComparisonPair = Pair.SelectedIndex switch { 0 => "LeftMiddle", 1 => "MiddleRight", 2 => "LeftRight", _ => throw new InvalidDataException("比較する側が不正です。") }
            };
            var generation = _generation;
            selection = new IndependentTextInputSelection(project, passwords, generation);
            SubmissionStateObserved?.Invoke(selection, passwords);
            submit = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _submitCts = submit;
            _submitting = true; _status.Text = "三側の候補を検証しています。"; Refresh();
            // 新tabへの同期採用直前にも親がGeneration/token/親stampを確認する。
            var accepted = await _accept(selection, submit.Token);
            submit.Token.ThrowIfCancellationRequested();
            if (_closed || generation != _generation) return;
            if (accepted) Close(true);
            else _status.Text = "候補を採用できませんでした。入力を確認して再試行してください。";
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = "比較を中止しました。"; }
        catch (Exception) { if (!_closed) _status.Text = "三側の入力を検証できませんでした。入力と読取り条件を確認してください。"; }
        finally
        {
            foreach (var chain in passwords) if (chain is not null) Array.Clear(chain);
            Array.Clear(passwords); selection?.Dispose();
            if (ReferenceEquals(_submitCts, submit)) _submitCts = null;
            try { submit?.Dispose(); _submitting = false; if (!_closed) Refresh(); }
            finally { completion.TrySetResult(); }
        }
    }

    private void EndLifetime()
    {
        if (_closed) return;
        _closed = true; _generation++; _submitCts?.Cancel(); _lifetime.Cancel();
        _lifetimeRegistration.Dispose(); foreach (var side in _sides) side.Dispose(); _lifetime.Dispose();
    }
}
