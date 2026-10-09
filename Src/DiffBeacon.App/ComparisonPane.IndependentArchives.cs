using Avalonia.Controls;
using Avalonia.Threading;
using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    // 候補が採用されるまでcredentialをwindowへ公開しない。
    private sealed class IndependentArchiveTextRead : IDisposable
    {
        internal TextDocument[] Documents { get; } = new TextDocument[3];
        internal string?[][] Passwords { get; set; } = [[], [], []];
        internal List<(ArchiveProjectInput Input, string?[] Values)> Branches { get; } = [];
        public void Dispose()
        {
            foreach (var values in Passwords) Array.Clear(values);
            foreach (var branch in Branches) Array.Clear(branch.Values);
        }
    }

    private async Task<IndependentArchiveTextRead> ReadIndependentTextInputsAsync(ComparisonProject project,
        CancellationTokenSource operation, CancellationToken token, string?[][]? candidatePasswords = null)
    {
        var seed = candidatePasswords is null ? null : CopyIndependentCandidatePasswords(project, candidatePasswords);
        var result = new IndependentArchiveTextRead();
        try
        {
            for (var side = 0; side < 3; side++)
            {
                var input = ProjectInputs.Archive(project, side);
                var count = (input?.EntryChain.Length ?? 0) + 1;
                if (seed is not null)
                {
                    // Physical/Untitledの選択は0要素。既存retry内部の1要素null契約は保持する。
                    result.Passwords[side] = input is null ? new string?[count] : seed[side].ToArray();
                    continue;
                }
                var cached = input is null ? null : (_owner as MainWindow)?.ArchiveLifetime.Find(input);
                var current = input is not null && _lastArchiveComparison == ArchiveComparisonIdentity(project)
                    ? _archivePasswords?[side] : null;
                result.Passwords[side] = current?.ToArray() ?? cached ?? new string?[count];
                if (current is not null && cached is not null) Array.Clear(cached);
                if (result.Passwords[side].Length != count || result.Passwords[side].Any(value => value?.Length > 4096))
                    throw new InvalidDataException("内包入力のパスワード階層が不正です。");
            }
            for (var side = 0; side < 3; side++)
            {
                while (true)
                {
                    ArchiveSourceReadStarting?.Invoke(); token.ThrowIfCancellationRequested();
                    try
                    {
                        result.Documents[side] = seed is not null && ProjectInputs.Archive(project, side) is null && !ProjectInputs.IsUntitled(project, side)
                            ? await ReadIndependentCandidatePhysicalTextAsync(project, side, token)
                            : await ProjectInputReader.ReadTextAsync(project, side, token, passwords: result.Passwords[side]);
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) when (exception is not OutOfMemoryException && ProjectInputs.Archive(project, side) is not null)
                    {
                        var retry = await RetryIndependentArchivePasswordsAsync(project, result.Passwords, operation, token, exception is ArchiveNameDecodingException);
                        foreach (var values in result.Passwords) Array.Clear(values);
                        result.Passwords = retry;
                        // 別側のpassword訂正も読込み結果へ反映する。
                        side = -1; break;
                    }
                }
            }
            for (var side = 0; side < 3; side++)
            {
                var input = ProjectInputs.Archive(project, side);
                if (input is null) continue;
                foreach (var snapshot in input.WorkingDocuments ?? [])
                {
                    var route = input.Copy() with { EntryChain = snapshot.EntryChain.ToArray(), ContainerNameCodePages = snapshot.ContainerNameCodePages?.ToArray(), ContainerGZipPayloadKinds = snapshot.ContainerGZipPayloadKinds?.ToArray(), ContainerCompressionPayloadKinds = snapshot.ContainerCompressionPayloadKinds?.ToArray(), LeafEntry = snapshot.LeafEntry, WorkingDocuments = null };
                    var values = (_owner as MainWindow)?.ArchiveLifetime.Find(route) ?? new string?[route.EntryChain.Length + 1];
                    if (route.EntryChain.Take(input.EntryChain.Length).SequenceEqual(input.EntryChain))
                        for (var layer = 0; layer < Math.Min(values.Length, result.Passwords[side].Length); layer++) values[layer] ??= result.Passwords[side][layer];
                    try
                    {
                        while (true)
                        {
                            try
                            {
                                await Task.Run(() => ProjectInputReader.ValidateWorkingSource(input, snapshot, values, token), token);
                                result.Branches.Add((route, values)); values = []; break;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception exception) when (exception is not OutOfMemoryException)
                            {
                                var routeProject = side switch
                                {
                                    0 => project with { LeftArchiveInput = route },
                                    1 => project with { BaseArchiveInput = route },
                                    _ => project with { RightArchiveInput = route }
                                };
                                var supplied = result.Passwords.Select(value => value.ToArray()).ToArray();
                                Array.Clear(supplied[side]); supplied[side] = values.ToArray();
                                try
                                {
                                    var retry = await RetryIndependentArchivePasswordsAsync(routeProject, supplied, operation, token, allowPayloadRetry: false);
                                    Array.Clear(values); values = retry[side]; retry[side] = [];
                                    foreach (var other in retry) Array.Clear(other);
                                }
                                finally { foreach (var other in supplied) Array.Clear(other); }
                            }
                        }
                    }
                    finally { Array.Clear(values); }
                }
            }
            ArchiveSourceReadyForAdoption?.Invoke(); token.ThrowIfCancellationRequested();
            return result;
        }
        catch { result.Dispose(); throw; }
        finally { ClearIndependentCandidatePasswords(seed); }
    }

    private async Task<string?[][]> RetryIndependentArchivePasswordsAsync(ComparisonProject project, string?[][] passwords,
        CancellationTokenSource operation, CancellationToken token, bool allowNameRetry = false, bool allowPayloadRetry = true)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed || !ReferenceEquals(_operation, operation)) throw new OperationCanceledException(token);
        var dialog = new ArchiveSourceRetryDialog(project, passwords, allowNameRetry, allowPayloadRetry);
        string?[][]? retry = null;
        try
        {
            var task = dialog.ShowDialog<string?[][]?>(_owner);
            using var registration = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
            ArchiveSourceRetryShown?.Invoke(dialog);
            retry = await task;
            token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(_operation, operation) || retry is null) throw new OperationCanceledException(token);
            dialog.ApplyNameChoices(project);
            var adopted = retry; retry = null; return adopted;
        }
        finally
        {
            if (retry is not null) foreach (var values in retry) Array.Clear(values);
            dialog.ClearPasswords(); dialog.Close();
        }
    }

    private void AdoptIndependentArchiveInputs(ComparisonProject project, IndependentArchiveTextRead read)
    {
        ClearArchivePasswords();
        _archivePasswords = read.Passwords.Select(values => values.ToArray()).ToArray();
        for (var side = 0; side < 3; side++)
        {
            var input = ProjectInputs.Archive(project, side);
            _workingTextRevisions[side] = input is null ? 0 : _workingTexts.Revision(input);
            if (input is not null && !IsWorkspaceCandidate && _owner is MainWindow window)
                window.ArchiveLifetime.Remember(input, read.Passwords[side]);
        }
        if (!IsWorkspaceCandidate && _owner is MainWindow owner)
            foreach (var branch in read.Branches) owner.ArchiveLifetime.Remember(branch.Input, branch.Values);
    }

    private void RefreshIndependentTextCaptions()
    {
        var pair = TextPair();
        _leftCaption.Text = TextSideCaption(pair.Left); _rightCaption.Text = TextSideCaption(pair.Right);
        var panes = _editGrid.Children.OfType<DockPanel>().ToArray();
        for (var side = 0; side < Math.Min(3, panes.Length); side++)
            if (panes[side].Children.OfType<TextBlock>().FirstOrDefault() is { } label) label.Text = TextSideCaption(side);
    }

    internal TextDocument[] CaptureIndependentTextDocuments() => Enumerable.Range(0, 3)
        .Select(side => TextInputDocument(side) ?? throw new InvalidOperationException("三側のTextを読み込んでください。")).ToArray();
}
