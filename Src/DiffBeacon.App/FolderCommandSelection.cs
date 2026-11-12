using System.Security.Cryptography;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal sealed class FolderCommandSelection(DirectoryCopySelection selection, FolderFilterInput? filter,
    IReadOnlyList<string>? protectedRoots = null, CancellationToken cancellationToken = default)
{
    internal DirectoryCopySelection Selection { get; } = selection;
    private readonly string[] additionalProtectedRoots = protectedRoots?.ToArray() ?? [];

    internal void RequireInputsUnchanged() => filter?.RequireUnchanged(cancellationToken);

    internal void ValidateOutputs(FolderCopyPlan plan)
    {
        // 利用中filterを含む計画は、一件も書く前に拒否する。
        foreach (var entry in plan.Entries) ValidateDestination(entry.DestinationPath);
    }

    internal void ValidateDestination(string destination)
    {
        var protection = FolderPathProtection.CreateContext();
        if (filter is not null && protection.SameContainer(filter.Path, destination))
            throw new IOException("比較に使用しているフィルターファイルを上書きできません。");
        foreach (var path in filter?.ListPaths ?? [])
            if (protection.SameContainer(path, destination))
                throw new IOException("比較に使用している置換リストを上書きできません。");
        foreach (var root in additionalProtectedRoots)
            if (protection.Within(destination, root))
                throw new IOException("コピー先以外の比較フォルダーへは書き込めません。");
    }
}

internal sealed class FolderFilterInput
{
    private readonly string sha256;
    private readonly FolderFilterListState listState = new(LineTransformLogEnvironments.StandardError);
    internal string Path { get; }
    internal FileFilter Filter { get; }
    internal IReadOnlyList<string> ListPaths => listState.Paths;

    internal FolderFilterInput(string path, CancellationToken cancellationToken = default)
    {
        Path = System.IO.Path.GetFullPath(path);
        var bytes = ReadBytes(cancellationToken);
        sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        Filter = FileFilter.ParseBytes(bytes, System.IO.Path.GetFileNameWithoutExtension(Path), LineTransformStringEnvironments.ForFolderFilters(), timestampEnvironment: LineTransformTimestampEnvironments.ForFolderFilters(), logEnvironment: LineTransformLogEnvironments.StandardError, listEnvironment: listState, cancellationToken: cancellationToken);
    }

    internal void RequireUnchanged(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Convert.ToHexString(SHA256.HashData(ReadBytes(cancellationToken))) != sha256)
            throw new IOException("準備中に比較フィルターが変わりました。再実行してください。");
        listState.RequireUnchanged(cancellationToken);
    }

    private byte[] ReadBytes(CancellationToken cancellationToken = default)
    {
        ArchiveActions.ValidatePath(Path);
        if (OperatingSystem.IsMacOS()) FolderCopyMacFileKind.Require(Path, DirectoryEntryKind.File);
        using var input = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65_536, FileOptions.SequentialScan);
        if (input.Length > 1_048_576)
            throw new FormatException("フィルターファイルは 1 MiB 以下にしてください。");
        // 開いたhandleを上限付きで読み、解析とSHAに同じ全bytesを使う。
        using var output = new MemoryStream();
        var buffer = new byte[65_536];
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Length + count > 1_048_576)
                throw new FormatException("フィルターファイルは 1 MiB 以下にしてください。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
