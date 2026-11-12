using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class LineTransformListEnvironments
{
    internal const int MaximumFileBytes = 4 * 1024 * 1024;
    internal static ILineTransformListEnvironment ValidationOnly { get; } = new ValidationEnvironment();
    internal static Action<string, string>? BeforeAssetPublish { get; set; }
    private sealed class ValidationEnvironment : ILineTransformListEnvironment
    {
        public string ReadList(string path, LineTransformBudget budget, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ""; }
    }
    internal static ILineTransformListEnvironment Create(ILineTransformLogEnvironment? logger = null, ComparisonProject? project = null)
        => new EnvironmentReader(logger, project?.LineTransformListState ?? new(), project?.LineTransformListAssets);

    internal static IReadOnlyList<LineTransformListDependency> Dependencies(ComparisonProject project)
        => LineTransformPlan.InspectListDependencies(project.LineTransformRules,
            timestampEnvironment: LineTransformTimestampEnvironments.ForRules(project.LineTransformRules),
            stringEnvironment: LineTransformStringEnvironments.ForRules(project.LineTransformRules));

    internal static IEnumerable<string> ProtectedPaths(ComparisonProject project)
        => Dependencies(project).Where(dependency => dependency.Path is not null)
            .Select(dependency => Path.GetFullPath(Environment.ExpandEnvironmentVariables(dependency.Path!)))
            .Concat(project.LineTransformListState.Paths).Concat(project.LineTransformListAssets?.Values.AsEnumerable() ?? []);

    internal static async Task<byte[]> ReadSnapshotAsync(string path, CancellationToken token)
    {
        var source = ArchiveActions.ValidatePath(path);
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("置換リストは4 MiBまでです。");
        using var bytes = new MemoryStream();
        var buffer = new byte[65_536];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) break;
            if (bytes.Length + count > MaximumFileBytes) throw new InvalidDataException("置換リストは4 MiBまでです。");
            await bytes.WriteAsync(buffer.AsMemory(0, count), token);
        }
        ArchiveActions.ValidatePath(source);
        return bytes.ToArray();
    }

    internal static async Task SaveAssetsAsync(ComparisonWorkspace snapshot, string fullPath, string? sourceProject,
        Action<string>? publishedAsset, CancellationToken token, Action<string, Action>? outputPublisher = null)
    {
        var directory = Path.GetDirectoryName(fullPath)!;
        long total = 0;
        foreach (var project in snapshot.Entries)
        {
            if (project.LineTransformListAssets is null) continue;
            var saved = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in project.LineTransformListAssets)
            {
                var bytes = await ReadSnapshotAsync(pair.Value, token);
                total += bytes.Length;
                if (total > 64L * 1024 * 1024) throw new InvalidDataException("置換リストassetの合計は64 MiBまでです。");
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                var relative = Path.GetFileName(fullPath) + ".assets/" + hash + ".replace-list";
                var asset = Path.Combine(directory, relative);
                ArchiveActions.ValidatePath(asset);
                if (File.Exists(asset))
                {
                    var existing = await ReadSnapshotAsync(asset, token);
                    if (!existing.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("置換リストassetの内容が一致しません。");
                }
                else
                {
                    ProjectInputs.EnsureOutput(asset, snapshot.Entries, sourceProject);
                    Directory.CreateDirectory(Path.GetDirectoryName(asset)!);
                    var temporary = Path.Combine(Path.GetDirectoryName(asset)!, ".replace-list-" + Guid.NewGuid().ToString("N") + ".tmp");
                    var ownsTemporary = false;
                    try
                    {
                        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                        { ownsTemporary = true; await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true); }
                        BeforeAssetPublish?.Invoke(temporary, asset);
                        void Publish()
                        {
                            token.ThrowIfCancellationRequested(); ArchiveActions.ValidatePath(asset);
                            ProjectInputs.EnsureOutput(asset, snapshot.Entries, sourceProject);
                            token.ThrowIfCancellationRequested(); File.Move(temporary, asset, overwrite: false);
                        }
                        if (outputPublisher is null) Publish(); else outputPublisher(asset, Publish);
                    }
                    finally { if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary); }
                }
                // 再利用した不変assetもJSON公開前に所有保護へ登録する。
                publishedAsset?.Invoke(asset);
                saved.Add(pair.Key, relative.Replace('\\', '/'));
            }
            project.LineTransformListAssets = saved;
        }
    }

    private sealed class EnvironmentReader(ILineTransformLogEnvironment? logger, LineTransformListState state,
        Dictionary<string, string>? assets) : ILineTransformListEnvironment
    {
        public string ReadList(string path, LineTransformBudget budget, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            if (assets is not null && !assets.ContainsKey(path))
                throw new InvalidDataException("包装した置換リストにこの動的パスはありません: " + path);
            var full = ArchiveActions.ValidatePath(assets is not null ? assets[path] : source);
            state.Register(source); state.Register(full);
            try
            {
                using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.SequentialScan);
                if (stream.Length > MaximumFileBytes) throw new InvalidDataException("置換リストは4 MiBまでです。");
                budget.ChargeListRead(65_536, cancellationToken);
                var buffer = new byte[65_536];
                using var bytes = new MemoryStream();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = stream.Read(buffer);
                    if (count == 0) break;
                    if (bytes.Length + count > MaximumFileBytes) throw new InvalidDataException("置換リストは4 MiBまでです。");
                    budget.ChargeListRead(count * 4L, cancellationToken);
                    bytes.Write(buffer, 0, count);
                }
                ArchiveActions.ValidatePath(full);
                return TextDocument.FromSnapshot(bytes.ToArray(), new TextLoadOptions { MaxFileSize = MaximumFileBytes }).Text;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger?.Write(LineTransformLogLevel.Error, $"Failed to open replace list file: {path}, error: {error.Message}");
                return "";
            }
        }
    }
}

internal sealed class LineTransformListState
{
    private readonly object gate = new();
    private readonly HashSet<string> paths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    internal IReadOnlyList<string> Paths { get { lock (gate) return paths.ToArray(); } }
    internal void Register(string path)
    {
        lock (gate)
        {
            if (!paths.Contains(path) && paths.Count >= 16_384)
                throw new InvalidOperationException("置換リストの依存パス数が上限を超えています。");
            paths.Add(path);
        }
    }
}
