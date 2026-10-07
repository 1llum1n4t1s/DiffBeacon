using System.Security.Cryptography;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal sealed class FolderCommandSelection(DirectoryCopySelection selection, FolderFilterInput? filter,
    IReadOnlyList<string>? protectedRoots = null)
{
    internal DirectoryCopySelection Selection { get; } = selection;
    private readonly string[] additionalProtectedRoots = protectedRoots?.ToArray() ?? [];

    internal void RequireInputsUnchanged() => filter?.RequireUnchanged();

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
        foreach (var root in additionalProtectedRoots)
            if (protection.Within(destination, root))
                throw new IOException("コピー先以外の比較フォルダーへは書き込めません。");
    }
}

internal sealed class FolderFilterInput
{
    private readonly string sha256;
    internal string Path { get; }
    internal FileFilter Filter { get; }

    internal FolderFilterInput(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        var bytes = ReadBytes();
        sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        Filter = FileFilter.ParseBytes(bytes, System.IO.Path.GetFileNameWithoutExtension(Path));
    }

    internal void RequireUnchanged()
    {
        if (Convert.ToHexString(SHA256.HashData(ReadBytes())) != sha256)
            throw new IOException("準備中に比較フィルターが変わりました。再実行してください。");
    }

    private byte[] ReadBytes()
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
            if (output.Length + count > 1_048_576)
                throw new FormatException("フィルターファイルは 1 MiB 以下にしてください。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
