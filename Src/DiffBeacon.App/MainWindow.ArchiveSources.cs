using Avalonia.Controls;
using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public sealed partial class MainWindow
{
    internal async Task OpenArchiveChildAsync(ComparisonPane parent, ArchivePanel panel, ArchiveOpenRequest request)
    {
        bool Current() => SessionPanes.Contains(parent) && ReferenceEquals(ActivePane, parent)
            && parent.IsArchiveChildCurrent(panel, request);
        if (!Current()) return;
        if (_sessions.Count >= WorkspaceStore.MaxEntries) throw new InvalidOperationException("比較タブは256件以下にしてください。");
        var container = request.Mode == ArchiveEntryOpenMode.Archive
            || request.Mode == ArchiveEntryOpenMode.Auto && ArchivePanel.Supports(request.Row.Path);
        var mode = container ? "Archive" : request.Mode == ArchiveEntryOpenMode.Binary ? "Binary" : "Text";
        var parentProject = parent.CaptureProject();
        bool InheritedReadOnly(int side) => ProjectInputs.Archive(parentProject, side) is { } typed
            ? typed.InheritedReadOnly ?? true : side == 0 ? parentProject.LeftReadOnly : parentProject.RightReadOnly;
        ArchiveProjectInput Input(ArchiveSource source, IReadOnlyList<string>? missing, bool present, int side) => new()
        {
            RootPath = source.RootPath,
            EntryChain = missing is null && present && container ? source.EntryChain.Append(request.Row.Path).ToArray() : source.EntryChain.ToArray(),
            LeafEntry = missing is null && present && !container ? request.Row.Path : null, RootSha256 = source.RootSha256,
            MissingEntryChain = missing is not null ? missing.Append(request.Row.Path).ToArray() : present ? null : [request.Row.Path],
            InheritedReadOnly = InheritedReadOnly(side)
        };
        var project = new ComparisonProject
        {
            Mode = mode, LeftArchiveInput = Input(request.LeftSource, request.LeftMissingEntryChain, request.Row.Left is not null, 0),
            RightArchiveInput = Input(request.RightSource, request.RightMissingEntryChain, request.Row.Right is not null, 2),
            LeftReadOnly = true, RightReadOnly = true, LeftDescription = request.Row.Path, RightDescription = request.Row.Path
        };
        ComparisonPane? candidate = new(this);
        string?[]? left = null, right = null;
        try
        {
            candidate.ApplyProject(project);
            candidate.ArchiveSourceRetryShown = parent.ArchiveSourceRetryShown;
            string?[] Passwords(IReadOnlyList<string?> values, ArchiveProjectInput input)
                => values.Concat(Enumerable.Repeat<string?>(null, input.EntryChain.Length + 1 - values.Count)).ToArray();
            left = Passwords(request.LeftPasswords, project.LeftArchiveInput!);
            right = Passwords(request.RightPasswords, project.RightArchiveInput!);
            parent.ArchiveChildReadStarting?.Invoke(); request.Token.ThrowIfCancellationRequested();
            if (!Current()) return;
            if (!await candidate.CompareArchiveProjectAsync(request.Token, left, right, request.Mode == ArchiveEntryOpenMode.Auto && !container)) return;
            parent.ArchiveChildReadyForAdoption?.Invoke(candidate);
            request.Token.ThrowIfCancellationRequested();
            if (!Current()) return;
            if (_sessions.Count >= WorkspaceStore.MaxEntries) throw new InvalidOperationException("比較タブは256件以下にしてください。");
            AttachProjectSession(candidate); _tabs.SelectedItem = _sessions[^1]; candidate = null;
        }
        finally { candidate?.Dispose(); if (left is not null) Array.Clear(left); if (right is not null) Array.Clear(right); }
    }
}

public sealed partial class ComparisonPane
{
    private string?[][]? _archivePasswords;
    private string? _lastArchiveComparison;
    internal Action? ArchiveChildReadStarting { get; set; }
    internal Action<ComparisonPane>? ArchiveChildReadyForAdoption { get; set; }
    internal Action? ArchiveSourceReadStarting { get; set; }
    internal Action? ArchiveSourceReadyForAdoption { get; set; }
    internal Action<ArchiveSourceRetryDialog>? ArchiveSourceRetryShown { get; set; }
    internal bool IsArchiveChildCurrent(ArchivePanel panel, ArchiveOpenRequest request)
        => !_disposed && ReferenceEquals(_specialTab.Content, panel) && CompareButton.IsEnabled && panel.IsCurrent(request);
    internal bool IsDisposed => _disposed;

    private void EnsureArchiveOutputWritable(string output)
    {
        var window = _owner as MainWindow;
        var projects = window?.SessionPanes.Select(pane => pane.CaptureProject()) ?? [CaptureProject()];
        ProjectInputs.EnsureOutput(output, projects, window?.WorkspaceSourcePath);
        EnsureProjectOutputWritable(output);
    }

    private void BindArchivePanel(ArchivePanel panel)
    {
        panel.SetWorkingTexts(_workingTexts);
        panel.OpenEntryRequested = (source, request) => _owner is MainWindow window
            ? window.OpenArchiveChildAsync(this, source, request)
            : throw new InvalidOperationException("内包比較を開くウィンドウがありません。");
    }

    private static string ArchiveComparisonIdentity(ComparisonProject project)
    {
        // 区切り文字を含む格納名も曖昧にせず、captionを物理入力として判定しない。
        var result = new System.Text.StringBuilder();
        void Part(string? value) { result.Append(value?.Length ?? -1).Append(':').Append(value); }
        Part(project.Mode);
        foreach (var side in Enumerable.Range(0, 3))
        {
            var input = ProjectInputs.Archive(project, side);
            Part(ProjectInputs.PathFor(project, side)); Part(input?.RootSha256); Part(input?.LeafEntry);
            Part(input?.InheritedReadOnly?.ToString());
            result.Append(input?.EntryChain.Length ?? -1).Append(':');
            if (input is not null) foreach (var hop in input.EntryChain) Part(hop);
            result.Append(input?.MissingEntryChain?.Length ?? -1).Append(':');
            if (input?.MissingEntryChain is { } missing) foreach (var hop in missing) Part(hop);
        }
        return result.ToString();
    }

    private void ClearArchivePasswords()
    {
        if (_archivePasswords is not null) foreach (var side in _archivePasswords) Array.Clear(side);
        _archivePasswords = null;
    }

    private void ConfigureArchiveInputControls()
    {
        var hasArchives = ProjectInputs.HasArchives(_projectMetadata);
        foreach (var (box, input) in new[] { (LeftPath, _projectMetadata.LeftArchiveInput), (BasePath, _projectMetadata.BaseArchiveInput), (RightPath, _projectMetadata.RightArchiveInput) })
        {
            var fixedInput = input is not null || ReferenceEquals(box, BasePath) && hasArchives && _projectMetadata.Mode != "Text";
            box.IsReadOnly = fixedInput;
            if (box.Parent is Panel row) foreach (var button in row.Children.OfType<Button>()) button.IsEnabled = !fixedInput;
            if (input is not null) ToolTip.SetTip(box, input.RootPath + "\n" + string.Join(" / ", input.EntryChain.Concat(input.MissingEntryChain ?? (input.LeafEntry is null ? [] : new[] { input.LeafEntry }))));
            else ToolTip.SetTip(box, null);
        }
    }

    internal async Task<bool> CompareArchiveProjectAsync(CancellationToken callerToken = default,
        IReadOnlyList<string?>? leftPasswords = null, IReadOnlyList<string?>? rightPasswords = null, bool autoLeaf = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var project = CaptureProject();
        if (!ProjectInputs.HasArchives(project)) throw new InvalidOperationException("内包入力を指定してください。");
        if (project.Mode is not ("Text" or "Binary" or "Archive") || project.Mode != "Text" && ProjectInputs.HasBase(project))
            throw new InvalidDataException("内包入力の比較形式または祖先指定が不正です。");
        var options = ProjectReport.Options(project);
        var requestedIdentity = ArchiveComparisonIdentity(project);
        var workingGeneration = _workingTexts.Generation;
        var initialText = (LeftEditor.Text, RightEditor.Text, ResultEditor.Text);
        InvalidateTextSave();
        _operation?.Cancel(); _operation?.Dispose();
        (_specialTab.Content as ArchivePanel)?.CancelOperation();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _operation = operation; var token = operation.Token;
        CompareButton.IsEnabled = false; _status.Text = "内包項目を比較しています…";
        string?[][]? passwords = null;
        Control? candidate = null;
        try
        {
            passwords = [[], [], []];
            foreach (var side in Enumerable.Range(0, 3))
            {
                var count = (ProjectInputs.Archive(project, side)?.EntryChain.Length ?? 0) + 1;
                var provided = side == 0 ? leftPasswords : side == 2 ? rightPasswords : null;
                var cached = _archivePasswords?[side];
                var copied = (provided ?? (IReadOnlyList<string?>?)cached)?.ToArray()
                    ?? (ProjectInputs.Archive(project, side) is { } input ? (_owner as MainWindow)?.ArchiveLifetime.Find(input) : null) ?? new string?[count];
                if (copied.Length != count || copied.Any(value => value?.Length > 4096))
                { Array.Clear(copied); throw new ArgumentException("内包入力のパスワード階層が不正です。"); }
                passwords[side] = copied;
            }
            TextDocument? left = null, right = null, ancestor = null;
            DiffResult? diff = null;
            while (true)
            {
                ArchiveSourceReadStarting?.Invoke(); token.ThrowIfCancellationRequested();
                try
                {
                    if (project.Mode == "Archive")
                    {
                        foreach (var side in new[] { 0, 2 })
                            if (ProjectInputs.Archive(project, side) is { WorkingTexts: not null } savedInput)
                                await ValidateWorkingRoutesAsync(savedInput, passwords[side], side);
                        ArchiveSource Source(int side) => ProjectInputs.Archive(project, side)?.ToSource()
                            ?? new(Path.GetFullPath(ProjectInputs.PathFor(project, side)));
                        candidate = await ArchivePanel.CreateForSourcesAsync(Source(0), Source(2), token, EnsureArchiveOutputWritable,
                            passwords[0][..^1], passwords[2][..^1], passwords[0][^1], passwords[2][^1],
                            project.LeftArchiveInput?.MissingEntryChain, project.RightArchiveInput?.MissingEntryChain);
                        BindArchivePanel((ArchivePanel)candidate);
                    }
                    else if (project.Mode == "Binary" || autoLeaf)
                    {
                        var maximum = autoLeaf ? 64 * 1024 * 1024 : 16 * 1024 * 1024;
                        var a = await ProjectInputReader.ReadBytesAsync(project, 0, maximum, token, passwords[0]);
                        var b = await ProjectInputReader.ReadBytesAsync(project, 2, maximum, token, passwords[2]);
                        if (autoLeaf)
                        {
                            try
                            {
                                left = await ProjectInputReader.ReadTextAsync(project, 0, token, passwords: passwords[0]);
                                right = await ProjectInputReader.ReadTextAsync(project, 2, token, passwords: passwords[2]);
                            }
                            catch (Exception exception) when (exception is InvalidDataException or System.Text.DecoderFallbackException)
                            { left = right = null; project.Mode = "Binary"; }
                        }
                        if (project.Mode == "Binary") candidate = SpecializedViews.BinarySnapshot(a, b,
                            ProjectInputs.Caption(project, 0), ProjectInputs.Caption(project, 2),
                            project.LeftReadOnly || project.LeftArchiveInput is not null,
                            project.RightReadOnly || project.RightArchiveInput is not null, EnsureArchiveOutputWritable,
                            ProjectInputs.PathFor(project, 0), ProjectInputs.PathFor(project, 2),
                            project.LeftArchiveInput is not null, project.RightArchiveInput is not null);
                    }
                    else if (project.Mode == "Text")
                    {
                        left = await ProjectInputReader.ReadTextAsync(project, 0, token, passwords: passwords[0]);
                        right = await ProjectInputReader.ReadTextAsync(project, 2, token, passwords: passwords[2]);
                    }
                    else throw new InvalidDataException("内包入力はText、Binary、Archive形式で開いてください。");
                    if (project.Mode == "Text")
                    {
                        if (ProjectInputs.HasBase(project)) ancestor = await ProjectInputReader.ReadTextAsync(project, 1, token, passwords: passwords[1]);
                        diff = await Task.Run(() => TextDiffer.Compare(left!.Text, right!.Text, options, token), token);
                    }
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    token.ThrowIfCancellationRequested(); if (_disposed || !ReferenceEquals(_operation, operation)) return false;
                    var dialog = new ArchiveSourceRetryDialog(project, passwords);
                    string?[][]? retry = null;
                    try
                    {
                        var retryTask = dialog.ShowDialog<string?[][]?>(_owner);
                        using var registration = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(dialog.Close));
                        ArchiveSourceRetryShown?.Invoke(dialog);
                        retry = await retryTask;
                        token.ThrowIfCancellationRequested(); if (_disposed || !ReferenceEquals(_operation, operation)) return false;
                        if (retry is null) throw new OperationCanceledException("内包比較を中止しました。", token);
                        foreach (var side in passwords) Array.Clear(side); passwords = retry; retry = null;
                    }
                    finally { if (retry is not null) foreach (var side in retry) Array.Clear(side); dialog.ClearPasswords(); dialog.Close(); }
                }
            }
            ArchiveSourceReadyForAdoption?.Invoke(); token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(_operation, operation) || ArchiveComparisonIdentity(CaptureProject()) != requestedIdentity
                || workingGeneration != _workingTexts.Generation || initialText != (LeftEditor.Text, RightEditor.Text, ResultEditor.Text)) return false;
            ResetMergeSession();
            if (candidate is not null)
            {
                SetSpecialView(candidate); candidate = null;
                _views.SelectedItem = _specialTab; CurrentDiff = null; _textSaveAllowed = false;
                _status.Text = project.Mode == "Archive" ? "内包アーカイブを開きました。" : "内包バイナリを比較しました。";
            }
            else
            {
                SpecializedViews.Release(_specialTab.Content as Control); _specialTab.Content = null;
                _leftDocument = left; _rightDocument = right; _baseDocument = ancestor; _baseText = ancestor?.Text;
                LeftEditor.Text = _savedLeft = left!.Text; RightEditor.Text = _savedRight = right!.Text;
                _ancestorEditor.Text = _baseText ?? ""; UpdateEditorLayout(ancestor is not null); _textSaveAllowed = true;
                _lastArchiveComparison = ArchiveComparisonIdentity(project);
                ApplyDiff(diff!); _status.Text += " · 実在する内包Textは作業版へ保存し、原本アーカイブは保持します。";
                if (ProjectInputs.HasMissing(project)) _status.Text += " · 不在側は未作成の文書です。編集できる側は外部ファイルへ保存してください。";
            }
            _projectMetadata.Mode = project.Mode; _mode.SelectedIndex = project.Mode switch { "Text" => 1, "Binary" => 3, _ => 7 };
            ConfigureArchiveInputControls();
            LeftEditor.IsReadOnly = project.LeftReadOnly || project.LeftArchiveInput is not null || !_textSaveAllowed;
            RightEditor.IsReadOnly = project.RightReadOnly || project.RightArchiveInput is not null || !_textSaveAllowed;
            _savedLeft = LeftEditor.Text ?? ""; _savedRight = RightEditor.Text ?? ""; _savedResult = ResultEditor.Text ?? "";
            ClearArchivePasswords(); _archivePasswords = passwords.Select(side => side.ToArray()).ToArray();
            if (_owner is MainWindow window)
                foreach (var side in Enumerable.Range(0, 3))
                    if (ProjectInputs.Archive(project, side) is { } input) window.ArchiveLifetime.Remember(input, passwords[side]);
            _lastArchiveComparison = ArchiveComparisonIdentity(CaptureProject());
            for (var side = 0; side < 3; side++)
                _workingTextRevisions[side] = ProjectInputs.Archive(project, side) is { } input ? _workingTexts.Revision(input) : 0;
            RefreshTextReadOnly(); UpdateEditorLayout(ancestor is not null);
            RefreshArchiveDraftCaptions();
            return true;

            async Task ValidateWorkingRoutesAsync(ArchiveProjectInput input, string?[] parentPasswords, int side)
            {
                foreach (var snapshot in input.WorkingTexts ?? [])
                {
                    var route = input.Copy() with { EntryChain = snapshot.EntryChain.ToArray(), LeafEntry = snapshot.LeafEntry, WorkingTexts = null };
                    var values = (_owner as MainWindow)?.ArchiveLifetime.Find(route) ?? new string?[route.EntryChain.Length + 1];
                    // 親と同一routeのprefixだけ継承し、別枝の同じ深さへpasswordを流用しない。
                    if (route.EntryChain.Take(input.EntryChain.Length).SequenceEqual(input.EntryChain))
                        for (var layer = 0; layer < parentPasswords.Length; layer++) values[layer] ??= parentPasswords[layer];
                    try
                    {
                        while (true)
                        {
                            try
                            {
                                await Task.Run(() => ProjectInputReader.ValidateWorkingSource(input, snapshot, values, token), token);
                                if (_owner is MainWindow owner) owner.ArchiveLifetime.Remember(route, values);
                                // 成功した完全routeの同一親prefixだけを戻す。
                                for (var layer = 0; layer < parentPasswords.Length; layer++) parentPasswords[layer] = values[layer];
                                break;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception exception) when (exception is not OutOfMemoryException)
                            {
                                token.ThrowIfCancellationRequested();
                                if (_disposed || !ReferenceEquals(_operation, operation)) throw new OperationCanceledException(token);
                                var request = new ComparisonProject { Mode = "Text", LeftReadOnly = true, BaseReadOnly = true, RightReadOnly = true };
                                if (side == 0) request.LeftArchiveInput = route;
                                else if (side == 1) request.BaseArchiveInput = route;
                                else request.RightArchiveInput = route;
                                string?[][] fields = [new string?[1], new string?[1], new string?[1]]; fields[side] = values.ToArray();
                                var dialog = new ArchiveSourceRetryDialog(request, fields); string?[][]? retry = null;
                                try
                                {
                                    var task = dialog.ShowDialog<string?[][]?>(_owner);
                                    using var registration = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(dialog.Close));
                                    ArchiveSourceRetryShown?.Invoke(dialog); retry = await task;
                                    token.ThrowIfCancellationRequested();
                                    if (_disposed || !ReferenceEquals(_operation, operation)) throw new OperationCanceledException(token);
                                    if (retry is null) throw new OperationCanceledException("内包作業版の検証を中止しました。", token);
                                    Array.Clear(values); values = retry[side].ToArray();
                                }
                                finally
                                {
                                    foreach (var field in fields) Array.Clear(field);
                                    if (retry is not null) foreach (var field in retry) Array.Clear(field);
                                    dialog.ClearPasswords(); dialog.Close();
                                }
                            }
                        }
                    }
                    finally { Array.Clear(values); }
                }
            }
        }
        catch (OperationCanceledException) { if (!_disposed && ReferenceEquals(_operation, operation)) _status.Text = "内包比較を中止しました。"; throw; }
        finally
        {
            SpecializedViews.Release(candidate);
            if (passwords is not null) foreach (var side in passwords) Array.Clear(side);
            if (ReferenceEquals(_operation, operation)) { _operation = null; if (!_disposed) CompareButton.IsEnabled = true; }
        }
    }
}
