using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    public TextBox MiddleEditor => _ancestorEditor;
    private string _savedMiddle = "";
    private readonly long[] _textRevisions = new long[3];
    private readonly ComboBox _textRole = new() { ItemsSource = new[] { "祖先（読取り専用）", "比較対象" }, SelectedIndex = 0, Width = 180 };
    private readonly ComboBox _textPair = new() { ItemsSource = new[] { "左と中央", "中央と右", "左と右" }, SelectedIndex = 0, Width = 140, IsVisible = false };
    private readonly TextBlock _syncPointCount = new() { Text = "同期点 0", VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly Button _addSyncPoint = new() { Content = "同期点を追加", IsVisible = false };
    private readonly Button _clearSyncPoints = new() { Content = "同期点を消去", IsVisible = false };
    private readonly Button _middleSave = new() { Content = "中央を保存", IsVisible = false };
    private readonly Button _middleSaveAs = new() { Content = "中央を外部保存", IsVisible = false };
    private readonly Dictionary<(int Left, int Right), List<TextSyncPoint>> _manualSyncPoints = [];
    private long _syncPointRevision;
    private object? _independentInputIdentity;
    private int _comparedTextPair = -1;
    private string? _comparedTextConfiguration;
    internal Action? IndependentTextReadyForAdoption { get; set; }
    internal Action? LegacyTextReadyForAdoption { get; set; }
    internal string? IndependentTextAdoptionDiagnostic { get; private set; }
    internal Task IndependentTextUiTask { get; private set; } = Task.CompletedTask;
    internal Func<CancellationToken, Task>? IndependentEditorCompareGate { get; set; }
    internal Action? IndependentEditorDiffReadyForAdoption { get; set; }
    internal Action? IndependentEditorCopyReadyForAdoption { get; set; }
    internal long[] CaptureIndependentTextRevisions() => _textRevisions.ToArray();
    internal Func<int, Task<string?>>? TextSideSavePathPicker { get; set; }
    internal bool IsWorkspaceCandidate { get; set; }
    internal void ActivateWorkspaceCandidate()
    {
        IsWorkspaceCandidate = false;
        if (_owner is MainWindow window)
            for (var side = 0; side < 3; side++)
                if (ProjectInputs.Archive(_projectMetadata, side) is { } input && _archivePasswords is not null)
                    window.ArchiveLifetime.Remember(input, _archivePasswords[side]);
        if (_owner is MainWindow owner)
            foreach (var copy in Enumerable.Range(0, 3).SelectMany(side => ProjectInputs.Archive(_projectMetadata, side)?.WorkingDocuments ?? []))
                if (copy.SnapshotPath is { } asset && Path.IsPathFullyQualified(asset)) owner.ArchiveLifetime.RegisterAsset(asset);
    }
    private bool IndependentText => ProjectInputs.IsIndependentText(_projectMetadata);

    public TextBox TextEditor(int side) => side switch
    { 0 => LeftEditor, 1 => MiddleEditor, 2 => RightEditor, _ => throw new ArgumentOutOfRangeException(nameof(side)) };
    private TextBox TextPath(int side) => side switch
    { 0 => LeftPath, 1 => BasePath, 2 => RightPath, _ => throw new ArgumentOutOfRangeException(nameof(side)) };
    private TextDocument? TextInputDocument(int side) => side switch
    { 0 => _leftDocument, 1 => _baseDocument, 2 => _rightDocument, _ => throw new ArgumentOutOfRangeException(nameof(side)) };
    private string TextSaved(int side) => side switch
    { 0 => _savedLeft, 1 => _savedMiddle, 2 => _savedRight, _ => throw new ArgumentOutOfRangeException(nameof(side)) };
    public bool TextDirty(int side) => (TextEditor(side).Text ?? "") != TextSaved(side);
    private bool TextReadOnly(int side) => side switch
    { 0 => !CanEditArchiveText(0) && _projectMetadata.LeftReadOnly, 1 => !CanEditArchiveText(1) && _projectMetadata.BaseReadOnly, 2 => !CanEditArchiveText(2) && _projectMetadata.RightReadOnly, _ => throw new ArgumentOutOfRangeException(nameof(side)) };
    private void SetSavedTextInput(int side, TextDocument document, string saved)
    {
        switch (side)
        {
            case 0: _leftDocument = document; _savedLeft = saved; break;
            case 1: _baseDocument = document; _savedMiddle = saved; _baseText = document.Text; break;
            case 2: _rightDocument = document; _savedRight = saved; break;
            default: throw new ArgumentOutOfRangeException(nameof(side));
        }
    }
    public void SelectIndependentText(bool independent = true)
    { if (independent) _mode.SelectedIndex = 1; _textRole.SelectedIndex = independent ? 1 : 0; }
    public void SelectTextPair(int pair)
    { if (pair is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(pair)); _textPair.SelectedIndex = pair; }

    private void InitializeTextInputs(WrapPanel actions)
    {
        actions.Children.Add(new TextBlock { Text = "中央の役割", VerticalAlignment = VerticalAlignment.Center });
        actions.Children.Add(_textRole); actions.Children.Add(_textPair);
        actions.Children.Add(_middleSave); actions.Children.Add(_middleSaveAs);
        actions.Children.Add(_syncPointCount); actions.Children.Add(_addSyncPoint); actions.Children.Add(_clearSyncPoints);
        actions.Children.Add(IndependentTextInputsButton);
        IndependentTextInputsButton.Click += async (_, _) => await GuardAsync(async () =>
        {
            if (_owner is MainWindow window)
            {
                var opening = window.OpenIndependentTextInputsAsync(this);
                window.IndependentTextInputOperationObserved?.Invoke(opening); await opening;
            }
        });
        _middleSave.Click += async (_, _) => await GuardAsync(() => ObserveCriticalTextSaveTask((string)_middleSave.Content!, SaveWorkingTextAsync(1)));
        _middleSaveAs.Click += async (_, _) => await GuardAsync(() => ObserveCriticalTextSaveTask("中央を外部保存", SaveTextAsAsync(1)));
        _textRole.SelectionChanged += (_, _) => { InvalidateTextSave(); if (_textRole.SelectedIndex == 1) _mode.SelectedIndex = 1; RefreshIndependentControls(); };
        _textPair.SelectionChanged += async (_, _) =>
        {
            RefreshSyncPointControls();
            if (IndependentText && _leftDocument is not null && _baseDocument is not null && _rightDocument is not null)
                await GuardAsync(() => StartIndependentEditorComparisonAsync());
        };
        _mode.PropertyChanged += (_, change) => { if (change.Property == ComboBox.SelectedIndexProperty) RefreshSyncPointControls(); };
        _provider.PropertyChanged += (_, change) => { if (change.Property == ComboBox.SelectedItemProperty) RefreshSyncPointControls(); };
        _addSyncPoint.Click += async (_, _) => await GuardAsync(AddTextSyncPointAsync);
        _clearSyncPoints.Click += async (_, _) => await GuardAsync(ClearTextSyncPointsAsync);
        foreach (var side in Enumerable.Range(0, 3))
        {
            var capturedSide = side;
            // TextChangedの遅延通知で初期値を後発編集として数えず、実際のproperty変更を同期追跡する。
            TextEditor(side).PropertyChanged += (_, change) =>
            {
                if (change.Property != TextBox.TextProperty) return;
                _textRevisions[capturedSide]++;
                if (ClearTextSyncPointsForSide(capturedSide))
                    _status.Text = "本文を編集したため、この側を含む同期点を消去しました。再比較してください。";
                RefreshArchiveDraftCaptions();
            };
        }
        RefreshSyncPointControls();
    }
    private void RefreshIndependentControls()
    {
        _textPair.IsVisible = _middleSave.IsVisible = _middleSaveAs.IsVisible = _textRole.SelectedIndex == 1;
        MiddleEditor.IsReadOnly = !IndependentText || !_textSaveAllowed || TextReadOnly(1);
        _middleSave.Content = ProjectInputs.Archive(_projectMetadata, 1) is not null ? "中央の作業版を保存" : "中央を保存";
        RefreshSyncPointControls();
    }
    private void AdoptLegacyTextRole()
    {
        if (_textRole.SelectedIndex == 1) return;
        if (IndependentText) _projectMetadata = _projectMetadata with { TextInputs = null, TextComparisonPair = null };
        _independentInputIdentity = null; MiddleEditor.IsReadOnly = true;
        RefreshIndependentControls();
        UpdateComparisonToolbarHeight();
    }
    private TextInputDescriptor? CaptureTextInputDescriptor()
    {
        if (_textRole.SelectedIndex != 1) return _projectMetadata.TextInputs?.Copy();
        TextInputSide Side(int side) => new()
        {
            Kind = ProjectInputs.Archive(_projectMetadata, side) is not null ? "Archive"
                : !string.IsNullOrEmpty(TextPath(side).Text) ? "Physical"
                : _projectMetadata.TextInputs?.Side(side).Kind == "Absent" ? "Absent" : "Untitled"
        };
        return new() { Semantics = "Independent", Left = Side(0), Middle = Side(1), Right = Side(2) };
    }
    private string TextInputIdentity() => JsonSerializer.Serialize(CaptureProject(), ProjectJsonContext.Default.ComparisonProject);
    private object TextSourceIdentity()
    {
        var project = CaptureProject();
        return (project.LeftPath, project.BasePath, project.RightPath, project.Mode, project.ProviderId, project.TextInputs?.Semantics,
        project.TextInputs?.Left?.Kind, project.TextInputs?.Middle?.Kind, project.TextInputs?.Right?.Kind, ArchiveComparisonIdentity(project));
    }
    private object TextAdoptionStamp() => (TextInputIdentity(), _projectMetadata.TextInputs, _textSaveGeneration, _workingTexts.Generation, ArchiveComparisonIdentity(CaptureProject()), _leftDocument, _baseDocument, _rightDocument,
        _textPair.SelectedIndex, _textRevisions[0], _textRevisions[1], _textRevisions[2], _syncPointRevision, LeftEditor.Text, MiddleEditor.Text, RightEditor.Text,
        LeftEditor.IsReadOnly, MiddleEditor.IsReadOnly, RightEditor.IsReadOnly, (_owner as MainWindow)?.ActivePane,
        _resultHost, _resultHost?.Session.Current.Version, MergeHasPendingComposition);
    private string DescribeTextAdoptionState() => $"generation={_textSaveGeneration}; revisions={string.Join(",", _textRevisions)}; role={_textRole.SelectedIndex}; pair={_textPair.SelectedIndex}; "
        + $"documents={_leftDocument is not null}/{_baseDocument is not null}/{_rightDocument is not null}; textLengths={LeftEditor.Text?.Length}/{MiddleEditor.Text?.Length}/{RightEditor.Text?.Length}; "
        + $"readonly={LeftEditor.IsReadOnly}/{MiddleEditor.IsReadOnly}/{RightEditor.IsReadOnly}; active={ReferenceEquals((_owner as MainWindow)?.ActivePane, this)}; identity={TextInputIdentity()}";
    internal object CaptureIndependentTextState() => (_leftDocument, _baseDocument, _rightDocument, CurrentDiff,
        LeftEditor.Text, MiddleEditor.Text, RightEditor.Text, LeftEditor.IsReadOnly, MiddleEditor.IsReadOnly, RightEditor.IsReadOnly,
        _savedLeft, _savedMiddle, _savedRight, _independentInputIdentity);

    internal async Task PrepareIndependentProjectAsync(CancellationToken token, string?[][]? candidatePasswords = null)
    {
        var seed = candidatePasswords is null ? null : CopyIndependentCandidatePasswords(CaptureProject(), candidatePasswords);
        try
        {
            if (!await CompareIndependentTextAsync(token, confirmDiscard: false, candidatePasswords: seed))
                throw new OperationCanceledException("独立Textの候補を採用できませんでした。既存タブを保持します。", token);
        }
        finally { ClearIndependentCandidatePasswords(seed); }
    }
    private async Task<bool> CompareIndependentTextAsync(CancellationToken callerToken = default, bool confirmDiscard = true, string?[][]? candidatePasswords = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var project = CaptureProject();
        if (_mode.SelectedIndex != 1 || project.TextInputs?.Semantics != "Independent")
            throw new InvalidOperationException("独立三者入力はText形式で比較してください。提供元変換にはまだ対応していません。");
        ProjectInputs.EnsureWorkingFormat(project);
        project.TextInputs.Validate(project);
        var confirmationIdentity = TextInputIdentity();
        if (confirmDiscard && HasUnsavedChanges && !await Dialogs.ConfirmAsync(_owner, "未保存の変更", "三側の編集内容を破棄してファイルを開き直しますか？")) return false;
        if (confirmationIdentity != TextInputIdentity()) throw new OperationCanceledException("確認中に入力が変更されました。三側の表示を保持します。");
        (_specialTab.Content as SpecializedViews.ImagePanel)?.EnsureNotSaving();
        InvalidateFolderCopy(); InvalidateTextSave();
        _operation?.Cancel(); _operation?.Dispose(); _operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        var operation = _operation; var token = operation.Token; var stamp = TextAdoptionStamp();
        var diagnosticBefore = DescribeTextAdoptionState(); IndependentTextAdoptionDiagnostic = null;
        var oldBinary = _specialTab.Content as SpecializedViews.BinaryPanel; var oldBinaryStamp = oldBinary?.StateStamp;
        var options = Options(); var pair = TextPair();
        CompareButton.IsEnabled = false; _status.Text = "三側のTextを読み込んでいます…";
        try
        {
            using var read = await ReadIndependentTextInputsAsync(project, operation, token, candidatePasswords);
            var documents = read.Documents;
            var useCurrentSyncPoints = TextEditor(pair.Left).Text == documents[pair.Left].Text
                && TextEditor(pair.Right).Text == documents[pair.Right].Text;
            var syncPoints = useCurrentSyncPoints ? TextSyncPointsFor(pair.Left, pair.Right).ToArray() : Array.Empty<TextSyncPoint>();
            var diff = await Task.Run(() => CompareTextWithSyncPoints(documents[pair.Left].Text, documents[pair.Right].Text, syncPoints, options, token), token);
            IndependentTextReadyForAdoption?.Invoke();
            token.ThrowIfCancellationRequested();
            // 同size/mtimeの差替えも、候補を採用する前に原本SHAで拒否する。
            foreach (var input in Enumerable.Range(0, 3).Select(side => ProjectInputs.Archive(project, side)).OfType<ArchiveProjectInput>())
                await Task.Run(() => EnsureArchiveRootUnchanged(input), token);
            if (_disposed || !ReferenceEquals(_operation, operation) || !Equals(stamp, TextAdoptionStamp()) || oldBinary?.StateStamp != oldBinaryStamp)
            {
                IndependentTextAdoptionDiagnostic = $"before: {diagnosticBefore}; after: {DescribeTextAdoptionState()}; disposed={_disposed}; sameOperation={ReferenceEquals(_operation, operation)}; sameBinaryStamp={oldBinary?.StateStamp == oldBinaryStamp}";
                _status.Text = "読込み中に入力または編集状態が変わりました。三側の表示を保持しました。"; return false;
            }
            ResetMergeSession(); _lastPackageComparison = null; SetSpecialView(null);
            _projectMetadata = WorkspaceStore.CloneProject(project);
            _textSaveAllowed = true; _workingDocumentStale = false;
            _lastArchiveComparison = ProjectInputs.HasArchives(project) ? ArchiveComparisonIdentity(project) : null;
            for (var side = 0; side < 3; side++)
            {
                SetSavedTextInput(side, documents[side], documents[side].Text);
                TextEditor(side).Text = documents[side].Text; TextEditor(side).IsReadOnly = TextReadOnly(side);
            }
            AdoptIndependentArchiveInputs(project, read);
            RefreshTextReadOnly(); ConfigureArchiveInputControls();
            _provider.IsEnabled = !ProjectInputs.HasArchives(project);
            RefreshIndependentControls(); UpdateIndependentEditorLayout();
            _independentInputIdentity = TextSourceIdentity();
            _lastPackageComparison = (LeftPath.Text ?? "", BasePath.Text ?? "", RightPath.Text ?? "", _mode.SelectedIndex, _provider.SelectedItem as string);
            ApplyIndependentDiff(diff); return true;
        }
        finally
        {
            if (!_disposed && ReferenceEquals(_operation, operation)) CompareButton.IsEnabled = true;
        }
    }
    private (int Left, int Right) TextPair() => _textPair.SelectedIndex switch
    { 0 => (0, 1), 1 => (1, 2), 2 => (0, 2), _ => throw new InvalidOperationException("比較する二側を選択してください。") };

    private (int Left, int Right) CurrentTextSyncPair()
        => IndependentText && _textRole.SelectedIndex == 1 ? TextPair() : (0, 2);

    private IReadOnlyList<TextSyncPoint> TextSyncPointsFor(int left, int right)
        => _manualSyncPoints.TryGetValue((left, right), out var points) ? points : Array.Empty<TextSyncPoint>();

    private DiffResult CompareTextWithSyncPoints(string left, string right, int leftSide, int rightSide,
        ComparisonOptions options, CancellationToken token)
    {
        return CompareTextWithSyncPoints(left, right, TextSyncPointsFor(leftSide, rightSide), options, token);
    }

    private static DiffResult CompareTextWithSyncPoints(string left, string right,
        IReadOnlyList<TextSyncPoint> points, ComparisonOptions options, CancellationToken token)
    {
        return points.Count == 0 ? TextDiffer.Compare(left, right, options, token)
            : TextDiffer.CompareWithSyncPoints(left, right, points, options, token);
    }

    private void RefreshSyncPointControls()
    {
        var visible = _mode.SelectedIndex is 0 or 1;
        _syncPointCount.IsVisible = _addSyncPoint.IsVisible = _clearSyncPoints.IsVisible = visible;
        if (!visible) return;
        var pair = CurrentTextSyncPair();
        var count = TextSyncPointsFor(pair.Left, pair.Right).Count;
        _syncPointCount.Text = $"同期点 {count}";
        _addSyncPoint.IsEnabled = count < TextDiffer.MaximumSyncPoints;
        _clearSyncPoints.IsEnabled = count > 0;
    }

    private async Task AddTextSyncPointAsync()
    {
        if (_mode.SelectedIndex is not (0 or 1))
        { _status.Text = "同期点はテキスト比較で使用できます。"; return; }
        var pair = CurrentTextSyncPair();
        var leftText = TextEditor(pair.Left).Text ?? "";
        var rightText = TextEditor(pair.Right).Text ?? "";
        var point = new TextSyncPoint(CaretLineNumber(TextEditor(pair.Left), leftText), CaretLineNumber(TextEditor(pair.Right), rightText));
        if (point.LeftLineNumber > TextLineCount(leftText) || point.RightLineNumber > TextLineCount(rightText))
        { _status.Text = "同期点は本文内の行に置いてください。末尾の改行後は選択できません。"; return; }

        if (!_manualSyncPoints.TryGetValue(pair, out var points)) _manualSyncPoints[pair] = points = [];
        if (points.Count >= TextDiffer.MaximumSyncPoints)
        { _status.Text = $"同期点は{TextDiffer.MaximumSyncPoints}個までです。"; return; }
        if (points.Any(existing => existing.LeftLineNumber == point.LeftLineNumber || existing.RightLineNumber == point.RightLineNumber))
        { _status.Text = "同じ行を使う同期点が既にあります。"; return; }
        var insert = points.FindIndex(existing => existing.LeftLineNumber > point.LeftLineNumber);
        if (insert < 0) insert = points.Count;
        if (insert > 0 && points[insert - 1].RightLineNumber >= point.RightLineNumber
            || insert < points.Count && points[insert].RightLineNumber <= point.RightLineNumber)
        { _status.Text = "同期点は両側で同じ順序になる行を選んでください。"; return; }

        points.Insert(insert, point);
        _syncPointRevision++;
        RefreshSyncPointControls();
        await CompareEditorsAsync();
    }

    private async Task ClearTextSyncPointsAsync()
    {
        var pair = CurrentTextSyncPair();
        if (!_manualSyncPoints.Remove(pair)) return;
        _syncPointRevision++;
        RefreshSyncPointControls();
        await CompareEditorsAsync();
    }

    private bool ClearTextSyncPointsForSide(int side)
    {
        var keys = _manualSyncPoints.Keys.Where(pair => pair.Left == side || pair.Right == side).ToArray();
        if (keys.Length == 0) return false;
        foreach (var key in keys) _manualSyncPoints.Remove(key);
        _syncPointRevision++;
        RefreshSyncPointControls();
        return true;
    }

    private static int CaretLineNumber(TextBox editor, string text)
    {
        var end = Math.Clamp(editor.CaretIndex, 0, text.Length);
        var line = 1;
        for (var index = 0; index < end; index++)
        {
            if (text[index] == '\r')
            {
                line++;
                if (index + 1 < end && text[index + 1] == '\n') index++;
            }
            else if (text[index] == '\n') line++;
        }
        return line;
    }

    private static int TextLineCount(string text)
    {
        if (text.Length == 0) return 0;
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                count++;
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
            }
            else if (text[index] == '\n') count++;
        }
        return text[^1] is '\r' or '\n' ? count : count + 1;
    }

    private void ApplyIndependentDiff(DiffResult diff)
    {
        ApplyDiff(diff); _comparedTextPair = _textPair.SelectedIndex;
        _comparedTextConfiguration = TextInputIdentity();
        var pair = TextPair();
        _leftCaption.Text = TextSideCaption(pair.Left); _rightCaption.Text = TextSideCaption(pair.Right);
        DiffList.SelectedIndex = -1;
        _status.Text = $"{_textPair.SelectedItem}: {diff.Blocks.Count} 個の差分" + (diff.LineFallback ? " · 行対応の処理上限に達したため、未確定区間をまとめて表示" : "")
            + (diff.InlineFallbackCount > 0 ? $" · 詳細比較を省略した {diff.InlineFallbackCount} 行は行全体を強調" : "");
    }
    private void CompareIndependentEditors()
    {
        var pair = TextPair();
        ApplyIndependentDiff(CompareTextWithSyncPoints(TextEditor(pair.Left).Text ?? "", TextEditor(pair.Right).Text ?? "", pair.Left, pair.Right, Options(), CancellationToken.None));
    }
    private Task<bool> StartIndependentEditorComparisonAsync()
    {
        var task = CompareIndependentEditorsAsync(); IndependentTextUiTask = task; return task;
    }
    private async Task<bool> CompareIndependentEditorsAsync()
    {
        EnsureIndependentTextSource();
        _operation?.Cancel(); _operation?.Dispose(); _operation = new CancellationTokenSource();
        var operation = _operation; var token = operation.Token;
        var stamp = TextAdoptionStamp(); var texts = Enumerable.Range(0, 3).Select(side => TextEditor(side).Text ?? "").ToArray();
        var pair = TextPair(); var syncPoints = TextSyncPointsFor(pair.Left, pair.Right).ToArray();
        var options = Options(); options = options with { SubstitutionRules = options.SubstitutionRules.ToArray() };
        var gate = IndependentEditorCompareGate;
        CompareButton.IsEnabled = false; _status.Text = "選択ペアのTextを比較しています…（中止できます）";
        try
        {
            var diff = await Task.Run(async () =>
            {
                if (gate is not null) await gate(token).ConfigureAwait(false);
                return CompareTextWithSyncPoints(texts[pair.Left], texts[pair.Right], syncPoints, options, token);
            }, token);
            IndependentEditorDiffReadyForAdoption?.Invoke(); token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(operation, _operation) || !Equals(stamp, TextAdoptionStamp()))
            {
                if (!_disposed && ReferenceEquals(operation, _operation)) _status.Text = "比較中に入力・役割または編集状態が変わりました。前の差分と三側本文を保持しました。";
                return false;
            }
            ApplyIndependentDiff(diff); return true;
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && ReferenceEquals(operation, _operation)) _status.Text = "比較を中止しました。前の差分と三側本文を保持しました。";
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_disposed || !ReferenceEquals(operation, _operation)) return false;
            throw new InvalidOperationException("Textの比較に失敗しました。前の差分と三側本文を保持しています。 " + exception.Message, exception);
        }
        finally { if (!_disposed && ReferenceEquals(operation, _operation)) CompareButton.IsEnabled = true; }
    }
    private void EnsureIndependentTextSource()
    {
        if (_disposed || _workingDocumentStale || !IndependentText || _textRole.SelectedIndex != 1 || _mode.SelectedIndex != 1 || !Equals(_independentInputIdentity, TextSourceIdentity()))
            throw new InvalidOperationException("入力が変更されています。独立Textを比較し直してください。前の差分と三側本文を保持しています。");
    }
    private bool IndependentDiffIsCurrent((int Left, int Right) pair) => CurrentDiff is not null && _comparedTextPair == _textPair.SelectedIndex
        && _comparedTextConfiguration == TextInputIdentity() && CurrentDiff.LeftText == TextEditor(pair.Left).Text && CurrentDiff.RightText == TextEditor(pair.Right).Text;
    private Task StartIndependentTextCopyAsync(bool forward)
    {
        var task = CopyIndependentSelectedAsync(forward); IndependentTextUiTask = task; return task;
    }
    private async Task CopyIndependentSelectedAsync(bool forward)
    {
        EnsureIndependentTextSource(); var pair = TextPair(); var destination = forward ? pair.Right : pair.Left;
        EnsureTextSideWritable(destination);
        if (CurrentDiff is null) return;
        if (!IndependentDiffIsCurrent(pair))
        {
            if (await StartIndependentEditorComparisonAsync()) _status.Text = "本文または設定が変更されています。新しい差分行を選択してからコピーしてください。";
            return;
        }
        var comparison = CurrentDiff; var row = DiffList.SelectedItem as DiffRow;
        if (row is null || !comparison.Rows.Any(current => ReferenceEquals(current, row)))
        { _status.Text = "現在の差分行を選択してください。"; return; }
        var block = comparison.Blocks.FirstOrDefault(value => DiffList.SelectedIndex >= value.RowStart && DiffList.SelectedIndex < value.RowStart + value.RowCount);
        if (block is null) return;
        _operation?.Cancel(); _operation?.Dispose(); _operation = new CancellationTokenSource();
        var operation = _operation; var token = operation.Token; var stamp = TextAdoptionStamp();
        CompareButton.IsEnabled = false;
        try
        {
            // 現行Coreのコピーは同期APIなので、本文構築もUIから外し、取消時は完成候補を採用しない。
            var copied = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var text = forward ? TextMerger.CopyLeftToRight(comparison, block) : TextMerger.CopyRightToLeft(comparison, block);
                token.ThrowIfCancellationRequested(); return text;
            }, token);
            IndependentEditorCopyReadyForAdoption?.Invoke();
            token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(operation, _operation) || !Equals(stamp, TextAdoptionStamp())
                || !ReferenceEquals(comparison, CurrentDiff) || !ReferenceEquals(row, DiffList.SelectedItem))
            {
                if (!_disposed && ReferenceEquals(operation, _operation)) _status.Text = "コピー中に状態が変わりました。前の差分と三側本文を保持しました。";
                return;
            }
            EnsureTextSideWritable(destination); TextEditor(destination).Text = copied;
            await StartIndependentEditorComparisonAsync();
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && ReferenceEquals(operation, _operation)) _status.Text = "コピーを中止しました。前の差分と三側本文を保持しました。";
        }
        finally { if (!_disposed && ReferenceEquals(operation, _operation)) CompareButton.IsEnabled = true; }
    }
    private void EnsureTextSideWritable(int side)
    {
        if (!IndependentText) { if (side == 1) throw new InvalidOperationException("祖先は読取り専用です。"); EnsureSideWritable(side == 2); return; }
        if (_workingDocumentStale || TextReadOnly(side) || TextEditor(side).IsReadOnly) throw new InvalidOperationException($"{TextSideCaption(side)}は読取り専用です。");
    }
    private void CopyIndependentSelected(bool forward)
    {
        if (_textRole.SelectedIndex != 1 || _mode.SelectedIndex != 1 || !Equals(_independentInputIdentity, TextSourceIdentity()))
            throw new InvalidOperationException("入力が変更されています。独立Textを比較し直してからコピーしてください。");
        var pair = TextPair(); var destination = forward ? pair.Right : pair.Left;
        EnsureTextSideWritable(destination);
        if (CurrentDiff is null) return;
        if (_comparedTextPair != _textPair.SelectedIndex || _comparedTextConfiguration != TextInputIdentity()
            || CurrentDiff.LeftText != TextEditor(pair.Left).Text || CurrentDiff.RightText != TextEditor(pair.Right).Text)
        { CompareIndependentEditors(); _status.Text = "本文が変更されています。新しい差分行を選択してからコピーしてください。"; return; }
        var row = DiffList.SelectedItem as DiffRow;
        if (row is null || !CurrentDiff.Rows.Any(current => ReferenceEquals(current, row)))
        { _status.Text = "現在の差分行を選択してください。"; return; }
        var block = CurrentDiff.Blocks.FirstOrDefault(value => DiffList.SelectedIndex >= value.RowStart && DiffList.SelectedIndex < value.RowStart + value.RowCount);
        if (block is null) return;
        TextEditor(destination).Text = forward ? TextMerger.CopyLeftToRight(CurrentDiff, block) : TextMerger.CopyRightToLeft(CurrentDiff, block);
        CompareIndependentEditors();
    }
    private string TextSideCaption(int side)
    {
        var description = side switch { 0 => _projectMetadata.LeftDescription, 1 => _projectMetadata.BaseDescription, _ => _projectMetadata.RightDescription };
        return (string.IsNullOrWhiteSpace(description) ? new[] { "左", "中央", "右" }[side] : description)
            + (ProjectInputs.IsUntitled(_projectMetadata, side) ? "（無題）" : "") + (HasArchiveDraft(side) ? "（未保存）" : "") + (TextReadOnly(side) ? "（読取り専用）" : "");
    }
    private void UpdateIndependentEditorLayout()
    {
        foreach (var pane in _editGrid.Children.OfType<DockPanel>()) pane.Children.Clear();
        var count = _resultHost is null ? 3 : 4;
        _editGrid.Children.Clear(); _editGrid.ColumnDefinitions = new ColumnDefinitions(count == 3 ? "*,6,*,6,*" : "*,6,*,6,*,6,*");
        for (var side = 0; side < count; side++)
        {
            var pane = new DockPanel(); var label = new TextBlock { Text = side == 3 ? "マージ結果" : TextSideCaption(side), Margin = new Thickness(8), FontWeight = FontWeight.Bold };
            DockPanel.SetDock(label, Dock.Top); pane.Children.Add(label); pane.Children.Add(side == 3 ? _resultPreview : TextEditor(side));
            Grid.SetColumn(pane, side * 2); _editGrid.Children.Add(pane);
            if (side == count - 1) continue;
            var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetColumn(splitter, side * 2 + 1); _editGrid.Children.Add(splitter);
        }
        if (_views.ItemsSource is IEnumerable<TabItem> tabs)
            foreach (var tab in tabs.Where(tab => ReferenceEquals(tab.Content, _editGrid))) tab.Header = count == 3 ? "編集 / 3ペイン" : "編集 / 4ペイン";
        UpdateComparisonToolbarHeight();
    }
    private void EnsureUntitledTextSaved()
    {
        if (IndependentText && _textRole.SelectedIndex != 1)
            throw new InvalidOperationException("中央の役割変更はまだ採用されていません。比較を完了するか、比較対象へ戻してからプロジェクトを保存・包装してください。");
        var project = CaptureProject();
        if (project.TextInputs is null) return;
        for (var side = 0; side < 3; side++)
            if ((ProjectInputs.IsUntitled(project, side) || ProjectInputs.IsUntitled(_projectMetadata, side))
                && (TextDirty(side) || !string.IsNullOrEmpty(TextEditor(side).Text)))
                throw new InvalidOperationException("未保存の無題Textがあります。該当する側を外部保存してからプロジェクトを保存・包装してください。");
    }

    public Task SaveTextToAsync(int side, string path, CancellationToken token = default)
        => side is < 0 or > 2 ? Task.FromException(new ArgumentOutOfRangeException(nameof(side)))
            : IndependentText ? SaveIndependentTextCoreAsync(side, path, false, token) : side == 1
                ? Task.FromException(new InvalidOperationException("祖先は保存できません。")) : SaveTextCoreAsync(side == 2, path, token);
    public Task SaveWorkingTextAsync(int side, CancellationToken token = default)
        => side is < 0 or > 2 ? Task.FromException(new ArgumentOutOfRangeException(nameof(side)))
            : IndependentText ? SaveIndependentTextCoreAsync(side, null, false, token) : side == 1
                ? Task.FromException(new InvalidOperationException("祖先は保存できません。")) : SaveTextCoreAsync(side == 2, null, token);
    public async Task SaveTextAsAsync(int side)
    {
        if (side is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(side));
        if (!IndependentText) { if (side == 1) throw new InvalidOperationException("祖先は保存できません。"); await SaveTextAsAsync(side == 2); return; }
        await SaveIndependentTextCoreAsync(side, null, true, CancellationToken.None);
    }
    private Task<string?> PickTextSidePathAsync(int side) => TextSideSavePathPicker is { } picker ? picker(side)
        : side != 1 && TextSavePathPicker is { } oldPicker ? oldPicker(side == 2) : SavePathAsync("テキストを外部保存", "untitled.txt");

    private async Task SaveIndependentTextCoreAsync(int side, string? selectedPath, bool explicitSaveAs, CancellationToken callerToken)
    {
        EnsureNoPendingTableEdit();
        if (!explicitSaveAs) EnsureTextSideWritable(side);
        if (_disposed || _workingDocumentStale || !_textSaveAllowed || !IndependentText || _mode.SelectedIndex != 1 || _textRole.SelectedIndex != 1)
            throw new InvalidOperationException("独立Textを比較してから保存してください。");
        if (_textSaveOperation is not null) throw new InvalidOperationException("テキストを保存しています。");
        var source = TextInputDocument(side) ?? throw new InvalidOperationException("Textを読み込んでください。");
        if (!Equals(_independentInputIdentity, TextSourceIdentity())) throw new InvalidOperationException("入力が変更されています。比較してから保存してください。");
        var archive = ProjectInputs.Archive(_projectMetadata, side)?.Copy();
        if (archive is not null) EnsureTextSideWritable(side);
        if (archive is not null && _lastArchiveComparison != ArchiveComparisonIdentity(CaptureProject()))
            throw new InvalidOperationException("内包入力が変更されています。比較してから保存してください。");
        if (archive is not null && selectedPath is null && !explicitSaveAs)
        { await SaveArchiveWorkingTextAsync(side, archive, callerToken); return; }
        var originalPath = TextPath(side).Text ?? "";
        if (archive is null && !string.IsNullOrEmpty(originalPath) && !ArchivePaths.SameFile(originalPath, source.Path))
            throw new InvalidOperationException("保存元の物理pathが読込み後に変更されています。");
        var identity = TextInputIdentity(); var descriptor = _projectMetadata.TextInputs; var generation = _textSaveGeneration;
        var readonlyAtStart = TextReadOnly(side); var editorReadonlyAtStart = TextEditor(side).IsReadOnly;
        var activeAtStart = (_owner as MainWindow)?.ActivePane; var revision = _workingTextRevisions[side];
        var text = TextEditor(side).Text ?? ""; var published = false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken); _textSaveOperation = operation; var token = operation.Token;
        void Current()
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _textSaveGeneration || identity != TextInputIdentity() || !ReferenceEquals(descriptor, _projectMetadata.TextInputs)
                || !ReferenceEquals(source, TextInputDocument(side)) || readonlyAtStart != TextReadOnly(side) || editorReadonlyAtStart != TextEditor(side).IsReadOnly
                || !ReferenceEquals(activeAtStart, (_owner as MainWindow)?.ActivePane))
                throw new OperationCanceledException("保存元の比較または権限が変更されました。", token);
            if (!explicitSaveAs || archive is not null) EnsureTextSideWritable(side);
            if (archive is not null) { _workingTexts.EnsureCurrent(archive, revision); EnsureArchiveRootUnchanged(archive); }
        }
        try
        {
            var target = selectedPath ?? (explicitSaveAs || string.IsNullOrEmpty(originalPath) ? await PickTextSidePathAsync(side) : originalPath);
            Current(); if (target is null) return;
            target = ArchiveActions.ValidatePath(target);
            var ownOriginal = !explicitSaveAs && !string.IsNullOrEmpty(source.Path) && ArchivePaths.SameFile(source.Path, target);
            void Guard(string output)
            {
                Current(); ArchiveActions.ValidatePath(output);
                var window = _owner as MainWindow; var panes = window?.SessionPanes ?? [this]; var own = CaptureProject();
                if (ownOriginal) own = side switch { 0 => own with { LeftPath = "" }, 1 => own with { BasePath = "" }, _ => own with { RightPath = "" } };
                ProjectInputs.EnsureOutput(output, panes.Where(pane => !ReferenceEquals(pane, this)).Select(pane => pane.CaptureProject()).Append(own), window?.WorkspaceSourcePath);
                foreach (var pane in panes) pane.EnsureProjectOutputWritable(output);
            }
            Guard(target);
            async Task Publish(string temporary, string output, CancellationToken cancellation)
            {
                await Dispatcher.UIThread.InvokeAsync(() => TextSaveBeforePublish?.Invoke() ?? Task.CompletedTask);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Guard(output); cancellation.ThrowIfCancellationRequested();
                    if (File.Exists(output) && (File.GetAttributes(output) & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("読み取り専用のファイルは保存できません。");
                    File.Move(temporary, output, overwrite: true); published = true;
                });
            }
            await source.SaveCopyAsync(target, text, token, Publish);
            ArchiveActions.ValidatePath(target); var bytes = await File.ReadAllBytesAsync(target, token);
            if (!source.CaptureBytes(text).SequenceEqual(bytes)) throw new IOException("保存したテキストbytesが一致しません。");
            TextSaveReadyForAdoption?.Invoke(); Guard(target);
            var captured = CaptureProject(); var updated = captured.TextInputs!.Copy(); updated.Side(side).Kind = "Physical";
            captured = side switch { 0 => captured with { LeftPath = target }, 1 => captured with { BasePath = target }, _ => captured with { RightPath = target } };
            if (archive is not null)
            {
                _detachedArchiveRoots.Add(archive.RootPath);
                if (_archivePasswords is not null) { Array.Clear(_archivePasswords[side]); _archivePasswords[side] = [null]; }
                captured = side switch
                {
                    0 => captured with { LeftArchiveInput = null, LeftReadOnly = false, LeftDescription = Path.GetFileName(target) },
                    1 => captured with { BaseArchiveInput = null, BaseReadOnly = false, BaseDescription = Path.GetFileName(target) },
                    _ => captured with { RightArchiveInput = null, RightReadOnly = false, RightDescription = Path.GetFileName(target) }
                };
                _workingTextRevisions[side] = 0;
            }
            _projectMetadata = captured with { TextInputs = updated };
            TextPath(side).Text = target; SetSavedTextInput(side, source.SavedCopy(target, text), text);
            _independentInputIdentity = TextSourceIdentity();
            _lastPackageComparison = (LeftPath.Text ?? "", BasePath.Text ?? "", RightPath.Text ?? "", _mode.SelectedIndex, _provider.SelectedItem as string);
            _lastArchiveComparison = ProjectInputs.HasArchives(_projectMetadata) ? ArchiveComparisonIdentity(CaptureProject()) : null;
            ConfigureArchiveInputControls(); _provider.IsEnabled = !ProjectInputs.HasArchives(_projectMetadata);
            RefreshTextReadOnly(); RefreshIndependentControls(); UpdateIndependentEditorLayout(); _status.Text = $"保存しました: {target}";
        }
        catch (Exception exception) when (published && exception is not OutOfMemoryException)
        { _status.Text = "ファイルは公開済みですが、表示の保存点と入力は採用できませんでした。"; throw new IOException(_status.Text, exception); }
        finally { if (ReferenceEquals(_textSaveOperation, operation)) _textSaveOperation = null; }
    }
}
