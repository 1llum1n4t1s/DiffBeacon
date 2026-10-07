using Avalonia.Threading;
using DiffBeacon.Core;
using DiffBeacon.Providers;
using System.Security.Cryptography;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    private readonly ArchiveWorkingStore _workingTexts;
    private readonly long[] _workingTextRevisions = new long[3];
    private CancellationTokenSource? _textSaveOperation;
    private long _textSaveGeneration;
    private readonly HashSet<string> _detachedArchiveRoots = new(StringComparer.Ordinal);
    internal Func<bool, Task<string?>>? TextSavePathPicker { get; set; }
    internal Func<Task>? TextSaveBeforePublish { get; set; }
    internal Action? TextSaveReadyForAdoption { get; set; }
    internal (string? Left, string? Right) ArchiveDiffCaptions => (_leftCaption.Text, _rightCaption.Text);
    internal string? ArchiveAncestorText => _ancestorEditor.Text;

    private bool CanEditArchiveText(bool right) => !_disposed && _textSaveAllowed && _projectMetadata.Mode == "Text"
        && ProjectInputs.Archive(_projectMetadata, right ? 2 : 0) is { InheritedReadOnly: false }
        && (right ? _rightDocument : _leftDocument) is not null
        && _lastArchiveComparison == ArchiveComparisonIdentity(CaptureProject());

    private bool CanEditMissingText(bool right) => CanEditArchiveText(right)
        && ProjectInputs.Archive(_projectMetadata, right ? 2 : 0)?.MissingEntryChain is not null;

    private ComparisonProject CaptureWorkingProject(ComparisonProject project)
    {
        ArchiveProjectInput? Capture(int side)
        {
            var input = ProjectInputs.Archive(project, side);
            if (input is null && project.Mode == "Archive" && _specialTab.Content is ArchivePanel panel && side != 1)
            {
                var source = side == 0 ? panel.ConfirmedLeft : panel.ConfirmedRight;
                var captured = _workingTexts.Capture(new() { RootPath = source.RootPath, RootSha256 = source.RootSha256,
                    EntryChain = source.EntryChain.ToArray(), InheritedReadOnly = side == 0 ? project.LeftReadOnly : project.RightReadOnly });
                if (captured.WorkingDocuments is not null) input = captured;
            }
            return input is null ? null : _workingTexts.Capture(input);
        }
        var left = Capture(0); var middle = Capture(1); var right = Capture(2);
        return project with { LeftArchiveInput = left, BaseArchiveInput = middle, RightArchiveInput = right,
            ProtectedArchiveAssets = (_owner as MainWindow)?.ArchiveLifetime.Assets ?? project.ProtectedArchiveAssets,
            LeftPath = left is null ? project.LeftPath : "", BasePath = middle is null ? project.BasePath : "", RightPath = right is null ? project.RightPath : "",
            LeftReadOnly = left is not null || project.LeftReadOnly, BaseReadOnly = middle is not null || project.BaseReadOnly, RightReadOnly = right is not null || project.RightReadOnly };
    }

    internal void WorkingTextSaved()
    {
        var changed = false;
        var selectedView = _views.SelectedItem;
        for (var side = 0; side <= 2; side++)
        {
            var right = side == 2; var input = ProjectInputs.Archive(_projectMetadata, side);
            if (input?.LeafEntry is null || _workingTexts.Revision(input) == _workingTextRevisions[side]) continue;
            var snapshot = _workingTexts.Find(input.ToSource(), input.LeafEntry);
            if (snapshot is null) continue;
            if (_specialTab.Content is SpecializedViews.BinaryPanel binary && (side != 1 || binary.HasMiddle))
            {
                if (snapshot.Bytes!.Length > BinaryEditSession.MaximumFileBytes)
                { _workingDocumentStale = true; binary.SetStatus("新しい作業版はBinaryの16MiB上限を超えています。表示bytesは保持しています。形式を選んで開き直してください。"); continue; }
                if (binary.Dirty(side)) { binary.SetStatus("別tabに新しい作業版があります。編集中のbytesは保持しています。比較し直してください。"); continue; }
                binary.AdoptSavedBytes(side, snapshot.Bytes!); _workingTextRevisions[side] = _workingTexts.Revision(input); continue;
            }
            if (_specialTab.Content is ArchivePanel) continue;
            if (snapshot.IsBinary)
            {
                _workingDocumentStale = true;
                _status.Text = "別tabでBinary作業版が保存されています。Text本文と未保存の編集は保持しています。形式を選んで開き直してください。";
                continue;
            }
            if (side != 1 && HasArchiveDraft(right)) continue;
            var document = snapshot.Document();
            if (right) { _rightDocument = document; RightEditor.Text = _savedRight = document.Text; }
            else if (side == 1) { _baseDocument = document; _baseText = document.Text; _ancestorEditor.Text = document.Text; }
            else { _leftDocument = document; LeftEditor.Text = _savedLeft = document.Text; }
            _workingTextRevisions[side] = _workingTexts.Revision(input);
            changed = true;
        }
        if (changed)
        {
            if (CurrentMergeSession is not null) { _mergeSourcesStale = true; UpdateMergeView(); }
            if (CurrentDiff is not null) CompareEditors();
            _views.SelectedItem = selectedView;
        }
        if (_specialTab.Content is ArchivePanel panel) panel.RefreshWorkingRows();
    }

    private void RefreshTextReadOnly()
    {
        MiddleEditor.IsReadOnly = !IndependentText || !_textSaveAllowed || _projectMetadata.BaseReadOnly;
        LeftEditor.IsReadOnly = !_textSaveAllowed || !CanEditArchiveText(false)
            && (_projectMetadata.LeftReadOnly || _projectMetadata.LeftArchiveInput is not null);
        RightEditor.IsReadOnly = !_textSaveAllowed || !CanEditArchiveText(true)
            && (_projectMetadata.RightReadOnly || _projectMetadata.RightArchiveInput is not null);
    }

    private void InvalidateTextSave() { _textSaveGeneration++; _textSaveOperation?.Cancel(); InvalidateBinarySave(); }
    private bool HasArchiveDraft(bool right) => ProjectInputs.Archive(_projectMetadata, right ? 2 : 0) is not null
        && (right ? RightEditor.Text != _savedRight : LeftEditor.Text != _savedLeft);

    private void RefreshArchiveDraftCaptions()
    {
        if (!ProjectInputs.HasArchives(_projectMetadata)) return;
        _leftCaption.Text = ProjectCaption(false); _rightCaption.Text = ProjectCaption(true);
        var labels = ProjectInputs.HasBase(_projectMetadata)
            ? new[] { ProjectCaption(false), _projectMetadata.BaseDescription ?? "共通の祖先", ProjectCaption(true), "マージ結果" }
            : new[] { ProjectCaption(false), ProjectCaption(true) };
        var panes = _editGrid.Children.OfType<Avalonia.Controls.DockPanel>().ToArray();
        for (var index = 0; index < panes.Length && index < labels.Length; index++)
            if (panes[index].Children.OfType<Avalonia.Controls.TextBlock>().FirstOrDefault() is { } label) label.Text = labels[index];
    }

    private ComparisonProject CaptureTextReportProject()
    {
        if (_workingDocumentStale) throw new InvalidOperationException("作業版の形式が変更されています。比較し直してください。");
        var project = CaptureProject();
        if (HasArchiveDraft(false)) project = project with { LeftDescription = (project.LeftDescription ?? "左") + "（未保存の編集）" };
        if (HasArchiveDraft(true)) project = project with { RightDescription = (project.RightDescription ?? "右") + "（未保存の編集）" };
        return project;
    }

    internal void EnsureArchiveDraftSaved()
    {
        EnsureUntitledTextSaved();
        if (_workingDocumentStale) throw new InvalidOperationException("別tabで作業版の形式が変更されています。本文を退避して開き直してください。");
        if (_specialTab.Content is SpecializedViews.BinaryPanel binary && binary.IsDirty?.Invoke() == true)
            throw new InvalidOperationException("未保存／未適用のバイナリ編集があります。保存してからプロジェクトを保存してください。");
        if (HasArchiveDraft(false) || HasArchiveDraft(true))
            throw new InvalidOperationException("未保存の内包文書があります。実在する側は通常保存、不在の側は外部保存してからプロジェクトを保存してください。");
    }

    public Task SaveTextToAsync(bool right, string path, CancellationToken token = default)
        => SaveTextToAsync(right ? 2 : 0, path, token);
    public Task SaveWorkingTextAsync(bool right, CancellationToken token = default) => SaveWorkingTextAsync(right ? 2 : 0, token);

    public async Task SaveTextAsAsync(bool right)
    {
        if (IndependentText) { await SaveTextAsAsync(right ? 2 : 0); return; }
        if (_specialTab.Content is SpecializedViews.BinaryPanel binary)
        { var chosen = await binary.SavePathPicker!(right ? 2 : 0); if (chosen is not null) await binary.SaveToAsync(right, chosen); return; }
        EnsureSideWritable(right);
        var target = TextSavePathPicker is { } picker ? await picker(right) : await SavePathAsync("テキストを外部保存", "untitled.txt");
        if (target is not null) await SaveTextCoreAsync(right, target, CancellationToken.None);
    }

    private async Task SaveTextCoreAsync(bool right, string? selectedPath, CancellationToken callerToken)
    {
        if (IndependentText) { await SaveIndependentTextCoreAsync(right ? 2 : 0, selectedPath, false, callerToken); return; }
        if (_workingDocumentStale) throw new InvalidOperationException("別tabで作業版の形式が変更されています。本文を退避して開き直してください。");
        EnsureNoPendingTableEdit(); EnsureSideWritable(right);
        if (!_textSaveAllowed || _disposed) throw new InvalidOperationException("この比較はテキスト保存の対象ではありません。");
        if (_textSaveOperation is not null) throw new InvalidOperationException("テキストを保存しています。");
        var missing = CanEditMissingText(right);
        var archive = ProjectInputs.Archive(_projectMetadata, right ? 2 : 0)?.Copy();
        if (archive is not null && _lastArchiveComparison != ArchiveComparisonIdentity(CaptureProject()))
            throw new InvalidOperationException("内包入力の形式が変更されています。比較して開き直してから保存してください。");
        if (archive is { MissingEntryChain: null } && selectedPath is null)
        { await SaveArchiveWorkingTextAsync(right, archive, callerToken); return; }
        var sourceDocument = right ? _rightDocument : _leftDocument;
        var originalPath = right ? RightPath.Text : LeftPath.Text;
        if (archive is null && !string.IsNullOrWhiteSpace(originalPath)
            && (sourceDocument is null || string.IsNullOrWhiteSpace(sourceDocument.Path) || !ArchivePaths.SameFile(originalPath, sourceDocument.Path)))
            throw new InvalidOperationException("パスが読込み後に変更されています。比較して文書を開き直してから保存してください。");
        var generation = _textSaveGeneration;
        var identity = ArchiveComparisonIdentity(CaptureProject());
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _textSaveOperation = operation;
        var token = operation.Token;
        void Current()
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _textSaveGeneration || identity != ArchiveComparisonIdentity(CaptureProject())
                || !ReferenceEquals(sourceDocument, right ? _rightDocument : _leftDocument))
                throw new OperationCanceledException("保存元の比較が変更されました。", token);
            EnsureSideWritable(right);
            if (archive is not null) EnsureArchiveRootUnchanged(archive);
        }
        try
        {
            var target = selectedPath ?? (missing || string.IsNullOrWhiteSpace(originalPath)
                ? TextSavePathPicker is { } picker ? await picker(right) : await SavePathAsync("テキストを保存", "untitled.txt")
                : originalPath);
            Current(); if (target is null) return;
            target = ArchiveActions.ValidatePath(target);
            var ownOriginal = archive is null && sourceDocument is not null && !string.IsNullOrWhiteSpace(sourceDocument.Path)
                && ArchivePaths.SameFile(sourceDocument.Path, target);
            void Guard(string path)
            {
                Current(); ArchiveActions.ValidatePath(path);
                var window = _owner as MainWindow;
                var panes = window?.SessionPanes ?? [this];
                var own = CaptureProject();
                if (ownOriginal) own = right ? own with { RightPath = "" } : own with { LeftPath = "" };
                ProjectInputs.EnsureOutput(path, panes.Where(pane => !ReferenceEquals(pane, this))
                    .Select(pane => pane.CaptureProject()).Append(own), window?.WorkspaceSourcePath);
                foreach (var pane in panes) pane.EnsureProjectOutputWritable(path);
            }
            Guard(target);
            var text = (right ? RightEditor.Text : LeftEditor.Text) ?? "";
            async Task Publish(string temporary, string path, CancellationToken cancellation)
            {
                await Dispatcher.UIThread.InvokeAsync(() => TextSaveBeforePublish?.Invoke() ?? Task.CompletedTask);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Guard(path); cancellation.ThrowIfCancellationRequested();
                    if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                        throw new UnauthorizedAccessException("読み取り専用のファイルは保存できません。");
                    File.Move(temporary, path, true);
                });
            }
            await (sourceDocument ?? TextDocument.Create()).SaveCopyAsync(target, text, token, Publish);
            ArchiveActions.ValidatePath(target);
            var savedBytes = await File.ReadAllBytesAsync(target, token);
            if (!(sourceDocument ?? TextDocument.Create()).CaptureBytes(text).SequenceEqual(savedBytes)) throw new IOException("保存したテキストbytesが一致しません。");
            var loaded = (sourceDocument ?? TextDocument.Create()).SavedCopy(target, text);
            TextSaveReadyForAdoption?.Invoke(); Guard(target);
            if (loaded.Text != text) throw new IOException("保存したテキストが開始時の本文と一致しません。");
            if (archive is not null && ProjectInputs.Archive(_projectMetadata, right ? 2 : 0) is { } origin)
            {
                _detachedArchiveRoots.Add(origin.RootPath);
                if (_archivePasswords is not null)
                {
                    var side = right ? 2 : 0;
                    Array.Clear(_archivePasswords[side]);
                    _archivePasswords[side] = [null];
                }
            }
            if (right)
            {
                if (archive is not null) _projectMetadata = _projectMetadata with { RightArchiveInput = null, RightReadOnly = false, RightDescription = Path.GetFileName(target) };
                RightPath.Text = target; _savedRight = text; _rightDocument = loaded;
            }
            else
            {
                if (archive is not null) _projectMetadata = _projectMetadata with { LeftArchiveInput = null, LeftReadOnly = false, LeftDescription = Path.GetFileName(target) };
                LeftPath.Text = target; _savedLeft = text; _leftDocument = loaded;
            }
            ConfigureArchiveInputControls(); _provider.IsEnabled = !ProjectInputs.HasArchives(_projectMetadata);
            if (ProjectInputs.HasArchives(_projectMetadata)) _lastArchiveComparison = ArchiveComparisonIdentity(CaptureProject());
            else _lastPackageComparison = (LeftPath.Text ?? "", BasePath.Text ?? "", RightPath.Text ?? "", _mode.SelectedIndex, _provider.SelectedItem as string);
            RefreshTextReadOnly(); _leftCaption.Text = ProjectCaption(false); _rightCaption.Text = ProjectCaption(true);
            UpdateEditorLayout(ProjectInputs.HasBase(_projectMetadata));
            _status.Text = $"保存しました: {target}";
        }
        finally { if (ReferenceEquals(_textSaveOperation, operation)) _textSaveOperation = null; }
    }

    private async Task SaveArchiveWorkingTextAsync(bool right, ArchiveProjectInput input, CancellationToken callerToken)
    {
        var side = right ? 2 : 0; var document = right ? _rightDocument! : _leftDocument!;
        var text = (right ? RightEditor.Text : LeftEditor.Text) ?? "";
        var generation = _textSaveGeneration; var identity = ArchiveComparisonIdentity(CaptureProject());
        var revision = _workingTextRevisions[side];
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _textSaveOperation = operation; var token = operation.Token;
        void Guard()
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _textSaveGeneration || identity != ArchiveComparisonIdentity(CaptureProject())
                || !ReferenceEquals(document, right ? _rightDocument : _leftDocument)) throw new OperationCanceledException("保存元の比較が変更されました。", token);
            EnsureSideWritable(right); _workingTexts.EnsureCurrent(input, revision);
            ArchiveActions.ValidatePath(input.RootPath);
            if ((File.GetAttributes(input.RootPath) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("読み取り専用の原本から作業保存はできません。");
            EnsureArchiveRootUnchanged(input);
        }
        try
        {
            Guard();
            var bytes = await Task.Run(() => document.CaptureBytes(text), token);
            // 保存直前にも原本の全階層と全entryを検証し、作業版で原本変更を隠さない。
            var passwords = _archivePasswords?[side].ToArray();
            try { await Task.Run(() => new ManagedArchive().ResolveEntry(input.ToSource(), input.LeafEntry!, 64 * 1024 * 1024, passwords, token), token); }
            finally { if (passwords is not null) Array.Clear(passwords); }
            await Dispatcher.UIThread.InvokeAsync(() => TextSaveBeforePublish?.Invoke() ?? Task.CompletedTask);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Guard(); TextSaveReadyForAdoption?.Invoke(); Guard();
                var snapshot = new ArchiveWorkingSnapshot { EntryChain = input.EntryChain.ToArray(), LeafEntry = input.LeafEntry!, Bytes = bytes,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), EncodingName = document.EncodingName, HasBom = document.HasBom };
                _workingTexts.Save(input, revision, snapshot);
                if (right) { _rightDocument = snapshot.Document(); _savedRight = text; }
                else { _leftDocument = snapshot.Document(); _savedLeft = text; }
                _workingTextRevisions[side] = _workingTexts.Revision(input);
                foreach (var pane in (_owner as MainWindow)?.SessionPanes ?? [this]) pane.WorkingTextSaved();
                RefreshArchiveDraftCaptions(); _status.Text = "内包文書の作業版を保存しました。原本アーカイブは保持しています。";
            });
        }
        finally { if (ReferenceEquals(_textSaveOperation, operation)) _textSaveOperation = null; }
    }

    private static void EnsureArchiveRootUnchanged(ArchiveProjectInput input)
    {
        ArchiveActions.ValidatePath(input.RootPath);
        using var stream = new FileStream(input.RootPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!StringComparer.OrdinalIgnoreCase.Equals(input.RootSha256, Convert.ToHexString(SHA256.HashData(stream))))
            throw new InvalidDataException("内包文書の原本アーカイブが変更されています。");
    }
}
