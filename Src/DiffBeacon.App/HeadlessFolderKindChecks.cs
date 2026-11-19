using DiffBeacon.Core;

namespace DiffBeacon.App;

/// <summary>
/// フォルダー同期で、ファイルをディレクトリの位置へ、ディレクトリをファイルの位置へコピーする自己検証。
/// 削除そのものはこの OS でも確かめ、コピーの実行はフォルダーコピーが対応する Windows と macOS だけで行う。
/// </summary>
internal static partial class HeadlessFolderCopyChecks
{
    /// <summary>種別の置換だけを実行して終了コードを返す。</summary>
    internal static int RunKindReplacementOnly(string output)
    {
        Directory.CreateDirectory(output);
        var failed = 0;
        RunKindReplacement(output, (name, passed, detail) =>
        {
            if (!passed) failed++;
            Console.WriteLine((passed ? "ok " : "FAIL ") + name + " " + detail);
        });
        return failed == 0 ? 0 : 2;
    }

    /// <summary>ファイルとディレクトリの入れ替え。コピー先の中身は残さない。</summary>
    internal static void RunKindReplacement(string output, Action<string, bool, string> check)
    {
        var root = Path.Combine(output, "kind-replace");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "gone.txt");
        File.WriteAllBytes(file, [1]);
        FolderOperations.DeleteReplacedDestination(file, DirectoryEntryKind.File, 10);
        check("kind replacement deletes a file", !File.Exists(file), file);
        var directory = Path.Combine(root, "gone-dir");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "child.txt"), [2]);
        FolderOperations.DeleteReplacedDestination(directory, DirectoryEntryKind.Directory, 10);
        check("kind replacement deletes a directory tree", !Directory.Exists(directory) && !File.Exists(Path.Combine(directory, "child.txt")), directory);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            check("kind replacement copy runs on Windows and macOS", true, "この OS のフォルダーコピーは未対応");
            return;
        }
        try { CopyFileOverDirectory(root, check); }
        catch (Exception error) { check("kind replacement copies a file over a directory", false, error.Message); }
        try { CopyDirectoryOverFile(root, check); }
        catch (Exception error) { check("kind replacement copies a directory over a file", false, error.Message); }
    }

    /// <summary>ファイルを、子を持つディレクトリの位置へコピーする。</summary>
    private static void CopyFileOverDirectory(string root, Action<string, bool, string> check)
    {
        var sourceRoot = Path.Combine(root, "file-source");
        var destinationRoot = Path.Combine(root, "file-destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);
        var source = Path.Combine(sourceRoot, "item");
        File.WriteAllBytes(source, [9, 8, 7]);
        var destination = Path.Combine(destinationRoot, "item");
        Directory.CreateDirectory(destination);
        File.WriteAllBytes(Path.Combine(destination, "nested.txt"), [1]);
        var result = CopyOne(sourceRoot, destinationRoot, "item", KindSnapshot(source), KindSnapshot(destination), expand: false);
        var bytes = File.Exists(destination) && !Directory.Exists(destination) ? File.ReadAllBytes(destination) : [];
        check("kind replacement copies a file over a directory", result.Succeeded && bytes.AsSpan().SequenceEqual(new byte[] { 9, 8, 7 }),
            result.Reason ?? result.Entries.FirstOrDefault()?.Reason ?? "ok");
    }

    /// <summary>ディレクトリを、ファイルの位置へコピーする。</summary>
    private static void CopyDirectoryOverFile(string root, Action<string, bool, string> check)
    {
        var sourceRoot = Path.Combine(root, "dir-source");
        var destinationRoot = Path.Combine(root, "dir-destination");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "tree"));
        File.WriteAllBytes(Path.Combine(sourceRoot, "tree", "leaf.txt"), [4, 5]);
        Directory.CreateDirectory(destinationRoot);
        var destination = Path.Combine(destinationRoot, "tree");
        File.WriteAllBytes(destination, [1]);
        var source = Path.Combine(sourceRoot, "tree");
        var result = CopyOne(sourceRoot, destinationRoot, "tree", KindSnapshot(source), KindSnapshot(destination), expand: true);
        var leaf = Path.Combine(destination, "leaf.txt");
        var bytes = File.Exists(leaf) ? File.ReadAllBytes(leaf) : [];
        check("kind replacement copies a directory over a file", result.Succeeded && Directory.Exists(destination) && bytes.AsSpan().SequenceEqual(new byte[] { 4, 5 }),
            result.Reason ?? result.Entries.FirstOrDefault()?.Reason ?? "ok");
    }

    /// <summary>ディスク上の項目から、コピー計画が読むスナップショットを作る。</summary>
    private static DirectorySideSnapshot KindSnapshot(string path)
    {
        var attributes = File.GetAttributes(path);
        var directory = (attributes & FileAttributes.Directory) != 0;
        var size = directory ? 0 : new FileInfo(path).Length;
        return new(path, directory ? DirectoryEntryKind.Directory : DirectoryEntryKind.File, size,
            File.GetLastWriteTimeUtc(path), attributes, false, directory ? DirectoryScanState.Scanned : DirectoryScanState.NotApplicable);
    }

    /// <summary>一つの項目を準備して実行する。</summary>
    private static FolderCopyResult CopyOne(string sourceRoot, string destinationRoot, string relative,
        DirectorySideSnapshot source, DirectorySideSnapshot destination, bool expand)
    {
        var selection = new DirectoryCopySelection(sourceRoot, destinationRoot, DirectoryCopyDirection.LeftToRight, DirectoryCopyMode.All,
            [relative], [new DirectoryCopyCandidate(relative, source, destination, expand)]);
        var plan = FolderOperations.PrepareAsync(selection).GetAwaiter().GetResult();
        if (plan.Entries.Count == 0 || !plan.Entries[0].ReplacesDifferentKind)
            throw new InvalidOperationException("種別の違うコピー先を置換する計画になりませんでした。");
        return FolderOperations.ExecuteAsync(plan).GetAwaiter().GetResult();
    }
}
