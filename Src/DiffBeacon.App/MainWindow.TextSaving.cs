using Avalonia.Threading;
using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    private CancellationTokenSource? _textSaveOperation;
    private long _textSaveGeneration;
    private readonly HashSet<string> _detachedArchiveRoots = new(StringComparer.Ordinal);
    internal Func<bool, Task<string?>>? TextSavePathPicker { get; set; }
    internal Func<Task>? TextSaveBeforePublish { get; set; }
    internal Action? TextSaveReadyForAdoption { get; set; }

    private bool CanEditMissingText(bool right) => !_disposed && _textSaveAllowed && _projectMetadata.Mode == "Text"
        && ProjectInputs.Archive(_projectMetadata, right ? 2 : 0) is { MissingEntryChain: not null, InheritedReadOnly: false }
        && (right ? _rightDocument : _leftDocument) is not null
        && _lastArchiveComparison == ArchiveComparisonIdentity(CaptureProject());

    private void RefreshTextReadOnly()
    {
        LeftEditor.IsReadOnly = !_textSaveAllowed || !CanEditMissingText(false)
            && (_projectMetadata.LeftReadOnly || _projectMetadata.LeftArchiveInput is not null);
        RightEditor.IsReadOnly = !_textSaveAllowed || !CanEditMissingText(true)
            && (_projectMetadata.RightReadOnly || _projectMetadata.RightArchiveInput is not null);
    }

    private void InvalidateTextSave() { _textSaveGeneration++; _textSaveOperation?.Cancel(); }
    private bool HasArchiveDraft(bool right) => ProjectInputs.Archive(_projectMetadata, right ? 2 : 0)?.MissingEntryChain is not null
        && (right ? RightEditor.Text != _savedRight : LeftEditor.Text != _savedLeft);

    private void RefreshArchiveDraftCaptions()
    {
        if (!ProjectInputs.HasMissing(_projectMetadata)) return;
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
        var project = CaptureProject();
        if (HasArchiveDraft(false)) project = project with { LeftDescription = (project.LeftDescription ?? "左") + "（未保存の編集）" };
        if (HasArchiveDraft(true)) project = project with { RightDescription = (project.RightDescription ?? "右") + "（未保存の編集）" };
        return project;
    }

    internal void EnsureArchiveDraftSaved()
    {
        if (HasArchiveDraft(false) || HasArchiveDraft(true))
            throw new InvalidOperationException("未保存の内包文書があります。編集した側を外部ファイルへ保存してからプロジェクトを保存してください。");
    }

    public Task SaveTextToAsync(bool right, string path, CancellationToken token = default)
        => SaveTextCoreAsync(right, path, token);

    private async Task SaveTextCoreAsync(bool right, string? selectedPath, CancellationToken callerToken)
    {
        EnsureNoPendingTableEdit(); EnsureSideWritable(right);
        if (!_textSaveAllowed || _disposed) throw new InvalidOperationException("この比較はテキスト保存の対象ではありません。");
        if (_textSaveOperation is not null) throw new InvalidOperationException("テキストを保存しています。");
        var missing = CanEditMissingText(right);
        var sourceDocument = right ? _rightDocument : _leftDocument;
        var originalPath = right ? RightPath.Text : LeftPath.Text;
        if (!missing && !string.IsNullOrWhiteSpace(originalPath)
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
        }
        try
        {
            var target = selectedPath ?? (missing || string.IsNullOrWhiteSpace(originalPath)
                ? TextSavePathPicker is { } picker ? await picker(right) : await SavePathAsync("テキストを保存", "untitled.txt")
                : originalPath);
            Current(); if (target is null) return;
            target = ArchiveActions.ValidatePath(target);
            var ownOriginal = !missing && sourceDocument is not null && !string.IsNullOrWhiteSpace(sourceDocument.Path)
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
            var loaded = await TextDocument.LoadAsync(target, token);
            TextSaveReadyForAdoption?.Invoke(); Guard(target);
            if (loaded.Text != text) throw new IOException("保存したテキストが開始時の本文と一致しません。");
            if (missing && ProjectInputs.Archive(_projectMetadata, right ? 2 : 0) is { } origin)
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
                if (missing) _projectMetadata = _projectMetadata with { RightArchiveInput = null, RightReadOnly = false, RightDescription = Path.GetFileName(target) };
                RightPath.Text = target; _savedRight = text; _rightDocument = loaded;
            }
            else
            {
                if (missing) _projectMetadata = _projectMetadata with { LeftArchiveInput = null, LeftReadOnly = false, LeftDescription = Path.GetFileName(target) };
                LeftPath.Text = target; _savedLeft = text; _leftDocument = loaded;
            }
            ConfigureArchiveInputControls(); _mode.IsEnabled = _provider.IsEnabled = !ProjectInputs.HasArchives(_projectMetadata);
            if (ProjectInputs.HasArchives(_projectMetadata)) _lastArchiveComparison = ArchiveComparisonIdentity(CaptureProject());
            else _lastPackageComparison = (LeftPath.Text ?? "", BasePath.Text ?? "", RightPath.Text ?? "", _mode.SelectedIndex, _provider.SelectedItem as string);
            RefreshTextReadOnly(); _leftCaption.Text = ProjectCaption(false); _rightCaption.Text = ProjectCaption(true);
            UpdateEditorLayout(ProjectInputs.HasBase(_projectMetadata));
            _status.Text = $"保存しました: {target}";
        }
        finally { if (ReferenceEquals(_textSaveOperation, operation)) _textSaveOperation = null; }
    }
}
