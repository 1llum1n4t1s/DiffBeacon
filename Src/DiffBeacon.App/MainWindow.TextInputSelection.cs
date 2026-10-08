using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public sealed partial class MainWindow
{
    private bool _independentTextInputWindowClosed;
    internal Action<Task<bool>>? IndependentTextInputOperationObserved { get; set; }
    internal Action<IndependentTextInputDialog>? IndependentTextInputDialogShown { get; set; }
    internal Action<ComparisonPane>? IndependentTextInputCandidateCreated { get; set; }
    internal Func<ComparisonPane, CancellationToken, Task>? IndependentTextInputAdoptionGate { get; set; }

    internal async Task<bool> OpenIndependentTextInputsAsync(ComparisonPane parent)
    {
        if (!Dispatcher.UIThread.CheckAccess()) throw new InvalidOperationException("入力選択はUIスレッドから開始してください。");
        if (_independentTextInputWindowClosed || !SessionPanes.Contains(parent) || !ReferenceEquals(ActivePane, parent))
            throw new OperationCanceledException("入力選択の親タブが終了または変更されました。");
        if (_sessions.Count >= WorkspaceStore.MaxEntries)
            throw new InvalidOperationException("比較タブは256件以下にしてください。");

        var parentStamp = parent.CaptureTextInputSelectionStamp();
        var store = ArchiveTexts;
        var storeGeneration = store.Generation;
        using var lifetime = new CancellationTokenSource();
        var lifetimeToken = lifetime.Token;
        IndependentTextInputDialog? dialog = null;
        Task? acceptanceFinished = null;
        var adopting = false;
        EventHandler ownerClosed = (_, _) => lifetime.Cancel();
        Action parentDisposed = () => lifetime.Cancel();
        EventHandler dialogClosed = (_, _) => lifetime.Cancel();
        EventHandler<SelectionChangedEventArgs> activeChanged = (_, _) =>
        {
            if (!adopting && (!SessionPanes.Contains(parent) || !ReferenceEquals(ActivePane, parent))) lifetime.Cancel();
        };
        Closed += ownerClosed;
        parent.IndependentTextInputParentDisposed += parentDisposed;
        _tabs.SelectionChanged += activeChanged;
        var unsubscribeState = parent.SubscribeTextInputSelectionInvalidation(() => { if (!adopting) lifetime.Cancel(); });

        bool Current(IndependentTextInputSelection selection) => !_independentTextInputWindowClosed
            && !lifetimeToken.IsCancellationRequested && dialog is not null && selection.Generation == dialog.Generation
            && SessionPanes.Contains(parent) && ReferenceEquals(ActivePane, parent)
            && parent.IsTextInputSelectionCurrent(parentStamp)
            && ReferenceEquals(ArchiveTexts, store) && store.Generation == storeGeneration
            && _sessions.Count < WorkspaceStore.MaxEntries;

        async Task<bool> AcceptAsync(IndependentTextInputSelection selection, CancellationToken selectionToken)
        {
            // Closeの返却が先でも、callbackのfinallyとcredential解放まで寿命を保持する。
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            acceptanceFinished = finished.Task;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, selectionToken);
            var token = operation.Token;
            ComparisonPane? candidate = null;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!Current(selection)) return false;
                var project = WorkspaceStore.CloneProject(selection.Project);
                if (!ProjectInputs.IsIndependentText(project)) throw new InvalidDataException("独立三側Textの選択が必要です。");
                project.TextInputs!.Validate(project);
                // 外来snapshotは取り込まず、現在の共有storeが所有する保存版だけを複製する。
                ArchiveProjectInput? Capture(ArchiveProjectInput? input) => input is null ? null
                    : store.Capture(input.Copy() with { WorkingDocuments = null });
                project = project with { LeftArchiveInput = Capture(project.LeftArchiveInput),
                    BaseArchiveInput = Capture(project.BaseArchiveInput), RightArchiveInput = Capture(project.RightArchiveInput) };
                candidate = new ComparisonPane(this, store) { IsWorkspaceCandidate = true };
                candidate.ApplyProject(project);
                IndependentTextInputCandidateCreated?.Invoke(candidate);
                token.ThrowIfCancellationRequested();
                if (!Current(selection)) return false;
                await candidate.PrepareIndependentProjectAsync(token, selection.Passwords);
                if (IndependentTextInputAdoptionGate is { } gate) await gate(candidate, token);
                token.ThrowIfCancellationRequested();
                if (!Current(selection)) return false;
                // 最後のawaitは全原本の再検査。mtimeだけのキャッシュを採用根拠にしない。
                await candidate.ValidateIndependentCandidateInputsAsync(token);
                token.ThrowIfCancellationRequested();
                if (!Current(selection)) return false;

                // この区間にawaitも注入hookも置かない。世代の一致を副作用前に検査する。
                // 採用自身のSelectを、採用前のユーザーtab移動として取消さない。
                adopting = true;
                AttachProjectSession(candidate);
                candidate.ActivateWorkspaceCandidate();
                _tabs.SelectedItem = _sessions[^1];
                candidate = null;
                return true;
            }
            finally
            {
                adopting = false;
                try { candidate?.Dispose(); }
                finally { finished.TrySetResult(); }
            }
        }

        try
        {
            dialog = new IndependentTextInputDialog(AcceptAsync, AbsoluteTextInputSelectionInitial(parent.CaptureProject()), lifetimeToken);
            dialog.Closed += dialogClosed;
            var shown = dialog.ShowDialog<bool>(this);
            IndependentTextInputDialogShown?.Invoke(dialog);
            // 成功CloseでもClosedが寿命を取消すため、返却後にtoken検査を追加しない。
            return await shown;
        }
        finally
        {
            try
            {
                lifetime.Cancel();
                dialog?.Close();
                // 閉鎖通知だけでは元readerやpickerのfinallyは終わらない。
                // Submitからこの親Taskを待たないため、成功Closeでも循環しない。
                if (acceptanceFinished is not null) await acceptanceFinished;
                if (dialog is not null) await dialog.PendingOperationsCompletion;
            }
            finally
            {
                if (dialog is not null) dialog.Closed -= dialogClosed;
                unsubscribeState();
                _tabs.SelectionChanged -= activeChanged;
                parent.IndependentTextInputParentDisposed -= parentDisposed;
                Closed -= ownerClosed;
            }
        }
    }

    private static ComparisonProject AbsoluteTextInputSelectionInitial(ComparisonProject source)
    {
        var project = WorkspaceStore.CloneProject(source);
        static string Full(string path) => string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path);
        static ArchiveProjectInput? Archive(ArchiveProjectInput? input) => input is null ? null
            : input.Copy() with { RootPath = Path.GetFullPath(input.RootPath), WorkingDocuments = null };
        return project with { LeftPath = Full(project.LeftPath), BasePath = Full(project.BasePath), RightPath = Full(project.RightPath),
            LeftArchiveInput = Archive(project.LeftArchiveInput), BaseArchiveInput = Archive(project.BaseArchiveInput),
            RightArchiveInput = Archive(project.RightArchiveInput) };
    }
}

public sealed partial class ComparisonPane
{
    internal Button IndependentTextInputsButton { get; } = new() { Content = "三側の入力を選択" };
    internal event Action? IndependentTextInputParentDisposed;
    private readonly Dictionary<int, (string Path, string Sha256)> _independentCandidatePhysicalInputs = [];

    internal object CaptureTextInputSelectionStamp()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var binary = _specialTab.Content as SpecializedViews.BinaryPanel;
        return (TextAdoptionStamp(), _mode.SelectedIndex, _textRole.SelectedIndex, _workingTexts,
            CurrentDiff, HasUnsavedChanges, _specialTab.Content, binary?.StateStamp,
            binary?.LeftReadOnly, binary?.HasMiddle == true ? binary.MiddleReadOnly : (bool?)null, binary?.RightReadOnly, binary?.IsDisposed,
            _savedLeft, _savedMiddle, _savedRight, _savedResult, ResultEditor.Text,
            _textSaveAllowed, _workingDocumentStale, _operation, _operation?.IsCancellationRequested, CompareButton.IsEnabled);
    }
    internal bool IsTextInputSelectionCurrent(object stamp) => !_disposed && Equals(stamp, CaptureTextInputSelectionStamp());

    internal Action SubscribeTextInputSelectionInvalidation(Action invalidate)
    {
        // 値を戻しても、入力・役割・編集状態の変更が起きた要求は復活させない。
        AvaloniaObject[] controls = [LeftPath, BasePath, RightPath, LeftEditor, MiddleEditor, RightEditor, ResultEditor,
            _mode, _textRole, _textPair, _provider, _fileFilter, _excludes, _ignoreRegex, _externalFormat,
            _ignoreCase, _ignoreSpace, _ignoreBlank, _recursive, _folderMode, _ignoreNumbers, _comments, _whitespace];
        if (_specialTab.Content is SpecializedViews.BinaryPanel binary)
            controls = [.. controls, binary.LeftHex, binary.MiddleHex, binary.RightHex];
        EventHandler<AvaloniaPropertyChangedEventArgs> changed = (_, args) =>
        {
            if (args.Property == TextBox.TextProperty || args.Property == TextBox.IsReadOnlyProperty
                || args.Property == ComboBox.SelectedIndexProperty || args.Property == ComboBox.SelectedItemProperty
                || args.Property == ToggleButton.IsCheckedProperty)
                invalidate();
        };
        foreach (var control in controls) control.PropertyChanged += changed;
        return () => { foreach (var control in controls) control.PropertyChanged -= changed; };
    }

    private string?[][] CopyIndependentCandidatePasswords(ComparisonProject project, string?[][] supplied)
    {
        if (!IsWorkspaceCandidate) throw new InvalidOperationException("候補以外に選択passwordを渡すことはできません。");
        if (supplied.Length != 3) throw new InvalidDataException("三側のpassword配列が必要です。");
        for (var side = 0; side < 3; side++)
        {
            var count = ProjectInputs.Archive(project, side) is { } input ? input.EntryChain.Length + 1 : 0;
            if (supplied[side] is null || supplied[side].Length != count || supplied[side].Any(value => value?.Length > 4096))
                throw new InvalidDataException("選択passwordの階層または長さが不正です。");
        }
        return supplied.Select(values => values.ToArray()).ToArray();
    }

    private static void ClearIndependentCandidatePasswords(string?[][]? values)
    {
        if (values is null) return;
        foreach (var chain in values) Array.Clear(chain);
        Array.Clear(values);
    }

    private async Task<TextDocument> ReadIndependentCandidatePhysicalTextAsync(ComparisonProject project, int side, CancellationToken token)
    {
        if (!IsWorkspaceCandidate) throw new InvalidOperationException("物理入力証跡は候補内にだけ保持します。");
        var path = ArchiveActions.ValidatePath(ProjectInputs.PathFor(project, side));
        var options = new TextLoadOptions();
        byte[]? bytes = null;
        try
        {
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous))
            {
                if (stream.Length > options.MaxFileSize) throw new InvalidDataException("テキストファイルがサイズ上限を超えています。");
                bytes = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(bytes, token);
                var extra = new byte[1];
                if (await stream.ReadAsync(extra, token) != 0) throw new InvalidDataException("読込み中に物理入力が変更されました。");
            }
            token.ThrowIfCancellationRequested();
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var document = TextDocument.FromSnapshot(bytes, options);
            if (!StringComparer.Ordinal.Equals(hash, await HashIndependentPhysicalInputAsync(path, token)))
                throw new InvalidDataException("読込み中に物理入力が変更されました。");
            _independentCandidatePhysicalInputs[side] = (path, hash);
            return document.SavedCopy(path, document.Text);
        }
        finally { if (bytes is not null) Array.Clear(bytes); }
    }

    private static async Task<string> HashIndependentPhysicalInputAsync(string requestedPath, CancellationToken token)
    {
        var path = ArchiveActions.ValidatePath(requestedPath);
        var maximum = new TextLoadOptions().MaxFileSize;
        static async Task<string> ReadHashAsync(string source, long limit, CancellationToken cancellation)
        {
            await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            if (stream.Length > limit) throw new InvalidDataException("テキストファイルがサイズ上限を超えています。");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long read = 0;
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellation)) != 0)
            {
                read = checked(read + count);
                if (read > limit) throw new InvalidDataException("テキストファイルがサイズ上限を超えています。");
                hash.AppendData(buffer, 0, count);
            }
            cancellation.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        var first = await ReadHashAsync(path, maximum, token);
        ArchiveActions.ValidatePath(path);
        // 開いたhandleと、再openした現pathを別々に検査する。size/mtimeによる省略はしない。
        var current = await ReadHashAsync(path, maximum, token);
        token.ThrowIfCancellationRequested();
        ArchiveActions.ValidatePath(path);
        if (!StringComparer.Ordinal.Equals(first, current)) throw new InvalidDataException("検証中に物理入力が変更されました。");
        return current;
    }

    internal async Task ValidateIndependentCandidateInputsAsync(CancellationToken token)
    {
        if (!IsWorkspaceCandidate || !IndependentText || _disposed) throw new InvalidOperationException("準備済みの独立Text候補が必要です。");
        var project = CaptureProject();
        for (var side = 0; side < 3; side++)
        {
            token.ThrowIfCancellationRequested();
            if (ProjectInputs.Archive(project, side) is not null)
            {
                var values = _archivePasswords?[side].ToArray() ?? throw new InvalidDataException("候補passwordの検証が完了していません。");
                try
                {
                    // ResolveEntryを通す既存readerで全root SHA/全entry/EOF/link/上限/取消を再検査する。
                    await ProjectInputReader.ReadTextAsync(project, side, token, passwords: values);
                }
                finally { Array.Clear(values); }
            }
            else if (!ProjectInputs.IsUntitled(project, side))
            {
                var path = ArchiveActions.ValidatePath(ProjectInputs.PathFor(project, side));
                if (!_independentCandidatePhysicalInputs.TryGetValue(side, out var evidence) || evidence.Path != path
                    || !StringComparer.Ordinal.Equals(evidence.Sha256, await HashIndependentPhysicalInputAsync(path, token)))
                    throw new InvalidDataException("候補読込み後に物理入力が変更されました。");
            }
        }
        token.ThrowIfCancellationRequested();
    }
}
