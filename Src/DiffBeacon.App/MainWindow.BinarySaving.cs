using System.Security.Cryptography;
using Avalonia.Threading;
using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    private CancellationTokenSource? _binarySaveOperation;
    private long _binarySaveGeneration;
    private string? _binaryLoadedIdentity;
    private bool _workingDocumentStale;
    internal Func<Task>? BinarySaveBeforePublish { get; set; }
    internal Action? BinarySaveReadyForAdoption { get; set; }
    internal Func<bool, Task<string?>>? BinarySavePathPicker { get; set; }
    private void InvalidateBinarySave() { _binarySaveGeneration++; _binarySaveOperation?.Cancel(); }
    private bool BinaryReadOnly(bool right)
        => ProjectInputs.Archive(_projectMetadata, right ? 2 : 0) is { } input ? input.InheritedReadOnly != false
            : right ? _projectMetadata.RightReadOnly : _projectMetadata.LeftReadOnly;
    private void BindBinaryPanel(SpecializedViews.BinaryPanel panel)
    {
        _binaryLoadedIdentity = ArchiveComparisonIdentity(CaptureProject());
        panel.RequiredReadOnly = BinaryReadOnly;
        panel.SaveContent = (right, path, token) => SaveBinaryAsync(panel, right, path, token);
        panel.SavePathPicker = right => BinarySavePathPicker is { } picker ? picker(right) : SavePathAsync("バイナリを別名保存", right ? "right.bin" : "left.bin");
        panel.ApplyReadOnly?.Invoke();
    }
    private async Task SaveBinaryAsync(SpecializedViews.BinaryPanel panel, bool right, string? selectedPath, CancellationToken callerToken)
    {
        if (_binarySaveOperation is not null) throw new InvalidOperationException("バイナリを保存しています。");
        if (_disposed || panel.IsDisposed || !ReferenceEquals(_specialTab.Content, panel)) throw new InvalidOperationException("保存元の比較が変更されています。");
        panel.EnsureApplied(); panel.ApplyReadOnly?.Invoke();
        if (_workingDocumentStale) throw new InvalidOperationException("別tabの作業版が現在の形式に対応していません。表示bytesを退避して開き直してください。");
        var readOnly = right ? panel.RightReadOnly : panel.LeftReadOnly;
        var capture = panel.Capture(right); var side = right ? 2 : 0;
        var archive = ProjectInputs.Archive(_projectMetadata, side)?.Copy();
        var identity = ArchiveComparisonIdentity(CaptureProject()); var generation = _binarySaveGeneration;
        if (identity != _binaryLoadedIdentity) throw new InvalidOperationException("入力が変更されています。比較して開き直してから保存してください。");
        if (readOnly && selectedPath is null) throw new InvalidOperationException("この側は読取り専用です。変更しない複製は別名保存してください。");
        var revision = archive is null ? 0 : _workingTextRevisions[side];
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _binarySaveOperation = operation; var token = operation.Token;
        void Current()
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _binarySaveGeneration || !ReferenceEquals(_specialTab.Content, panel) || panel.IsDisposed
                || identity != ArchiveComparisonIdentity(CaptureProject())) throw new OperationCanceledException("保存元の比較が変更されました。", token);
            if ((right ? panel.RightReadOnly : panel.LeftReadOnly) != readOnly) throw new OperationCanceledException("読取り専用の指定が変更されました。", token);
            if (archive is not null) { EnsureArchiveRootUnchanged(archive); _workingTexts.EnsureCurrent(archive, revision); }
        }
        try
        {
            Current();
            if (archive is not null)
            {
                var passwords = _archivePasswords?[side].ToArray();
                try
                {
                    await Task.Run(() =>
                    {
                        if (archive.MissingEntryChain is null) _ = new ManagedArchive().ResolveEntry(archive.ToSource(), archive.LeafEntry!, BinaryEditSession.MaximumFileBytes, passwords, token);
                        else _ = ProjectInputReader.ResolveMissingManifest(archive, passwords, token);
                    }, token);
                }
                finally { if (passwords is not null) Array.Clear(passwords); }
            }
            if (archive is { MissingEntryChain: null } && selectedPath is null)
            {
                var bytes = capture.CopyBytes();
                await Dispatcher.UIThread.InvokeAsync(() => BinarySaveBeforePublish?.Invoke() ?? Task.CompletedTask);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Current();
                    if ((File.GetAttributes(archive.RootPath) & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("読取り専用の原本から作業保存はできません。");
                    BinarySaveReadyForAdoption?.Invoke(); Current();
                    _workingTexts.Save(archive, revision, new() { EntryChain = archive.EntryChain.ToArray(), LeafEntry = archive.LeafEntry!, Kind = "Binary", Bytes = bytes, Sha256 = capture.Sha256 });
                    panel.MarkSaved(right, capture); _workingTextRevisions[side] = _workingTexts.Revision(archive);
                    foreach (var pane in (_owner as MainWindow)?.SessionPanes ?? [this]) pane.WorkingTextSaved();
                    panel.SetStatus("バイナリの作業版を保存しました。原本アーカイブは保持しています。");
                });
                return;
            }
            var original = right ? RightPath.Text : LeftPath.Text;
            var target = selectedPath ?? (archive is not null || string.IsNullOrWhiteSpace(original)
                ? BinarySavePathPicker is { } picker ? await picker(right) : await SavePathAsync("バイナリを保存", "untitled.bin") : original);
            Current(); if (target is null) return; target = ArchiveActions.ValidatePath(target);
            var ownOriginal = archive is null && !string.IsNullOrWhiteSpace(original) && ArchivePaths.SameFile(original, target);
            void Guard(string path)
            {
                Current(); BinaryFileStore.ValidateOutput(path);
                var window = _owner as MainWindow; var panes = window?.SessionPanes ?? [this]; var own = CaptureProject();
                if (ownOriginal) own = right ? own with { RightPath = "" } : own with { LeftPath = "" };
                ProjectInputs.EnsureOutput(path, panes.Where(pane => !ReferenceEquals(pane, this)).Select(pane => pane.CaptureProject()).Append(own), window?.WorkspaceSourcePath);
                foreach (var pane in panes) pane.EnsureProjectOutputWritable(path);
            }
            Guard(target);
            await BinaryFileStore.SaveCopyAsync(target, capture, token, async (temporary, path, cancellation) =>
            {
                await Dispatcher.UIThread.InvokeAsync(() => BinarySaveBeforePublish?.Invoke() ?? Task.CompletedTask);
                await Dispatcher.UIThread.InvokeAsync(() => { Guard(path); BinaryFileStore.Publish(temporary, path, cancellation); });
            });
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                BinarySaveReadyForAdoption?.Invoke(); Guard(target);
                using (var file = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                    if (file.Length != capture.Length || Convert.ToHexString(SHA256.HashData(file)) != capture.Sha256) throw new IOException("保存したバイナリbytesが一致しません。");
                if (readOnly) { panel.SetStatus($"複製を保存しました: {target}"); return; }
                if (archive is not null)
                {
                    _detachedArchiveRoots.Add(archive.RootPath);
                    if (_archivePasswords is not null) { Array.Clear(_archivePasswords[side]); _archivePasswords[side] = [null]; }
                }
                if (right) { _projectMetadata = _projectMetadata with { RightArchiveInput = null, RightReadOnly = false, RightDescription = Path.GetFileName(target) }; RightPath.Text = target; }
                else { _projectMetadata = _projectMetadata with { LeftArchiveInput = null, LeftReadOnly = false, LeftDescription = Path.GetFileName(target) }; LeftPath.Text = target; }
                panel.MarkSaved(right, capture); _workingTextRevisions[side] = 0;
                ConfigureArchiveInputControls(); _provider.IsEnabled = !ProjectInputs.HasArchives(_projectMetadata);
                _binaryLoadedIdentity = ArchiveComparisonIdentity(CaptureProject());
                if (ProjectInputs.HasArchives(_projectMetadata)) _lastArchiveComparison = _binaryLoadedIdentity;
                else _lastPackageComparison = (LeftPath.Text ?? "", BasePath.Text ?? "", RightPath.Text ?? "", _mode.SelectedIndex, _provider.SelectedItem as string);
                panel.ApplyReadOnly?.Invoke(); panel.SetStatus($"保存しました: {target}"); (_owner as MainWindow)?.RefreshSessionHeaders();
            });
        }
        finally { if (ReferenceEquals(_binarySaveOperation, operation)) _binarySaveOperation = null; }
    }
}
