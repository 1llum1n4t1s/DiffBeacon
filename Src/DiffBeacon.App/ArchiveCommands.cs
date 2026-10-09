using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static class ArchiveCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var required = args[0] switch { "--archive-list" => 2, "--archive-entry" => 4, "--archive-compare" or "--archive-repack" or "--archive-create" or "--archive-extract" => 3, _ => throw new ArgumentException("不明なアーカイブコマンドです。") };
        if (args.Length < required) throw new ArgumentException("アーカイブコマンドの引数が不正です。--help を参照してください。");
        var passwordInput = false; var leftCodePage = 28591; int? rightCodePage = null;
        var leftPayloadKind = GZipPayloadKind.Auto; GZipPayloadKind? rightPayloadKind = null;
        var leftCompressionPayloadKind = CompressionPayloadKind.Auto; CompressionPayloadKind? rightCompressionPayloadKind = null;
        var outputCodePage = 28591; var outputCodePageSpecified = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = required; index < args.Length; index++)
        {
            var option = args[index];
            if (!seen.Add(option)) throw new ArgumentException("アーカイブオプションが重複しています。");
            if (option == "--output-gzip-name-code-page")
            {
                if (args[0] is not ("--archive-create" or "--archive-repack") || ++index >= args.Length
                    || !int.TryParse(args[index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out outputCodePage)
                    || !ManagedArchiveWriteOptions.SupportedGZipNameCodePages.Contains(outputCodePage))
                    throw new ArgumentException("gzip出力の格納名文字コード指定が不正です。");
                outputCodePageSpecified = true;
                continue;
            }
            if (args[0] == "--archive-create") throw new ArgumentException("作成にはアーカイブ読込みオプションを指定できません。");
            if (option == "--password-stdin") { passwordInput = true; continue; }
            if (option is "--compression-payload-kind" or "--right-compression-payload-kind")
            {
                if (++index >= args.Length || option == "--right-compression-payload-kind" && args[0] != "--archive-compare")
                    throw new ArgumentException("BZip2／Z本文の形式指定が不正です。");
                var value = ArchivePayloadSettings.ParseCompressionCommand(args[index]);
                if (option == "--compression-payload-kind") leftCompressionPayloadKind = value; else rightCompressionPayloadKind = value;
                continue;
            }
            if (option is "--gzip-payload-kind" or "--right-gzip-payload-kind")
            {
                if (++index >= args.Length || option == "--right-gzip-payload-kind" && args[0] != "--archive-compare")
                    throw new ArgumentException("gzip本文の形式指定が不正です。");
                var value = ArchivePayloadSettings.ParseCommand(args[index]);
                if (option == "--gzip-payload-kind") leftPayloadKind = value; else rightPayloadKind = value;
                continue;
            }
            if (option is not ("--gzip-name-code-page" or "--right-gzip-name-code-page") || ++index >= args.Length
                || !int.TryParse(args[index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var codePage)
                || !ManagedArchiveReadOptions.SupportedGZipNameCodePages.Contains(codePage)
                || option == "--right-gzip-name-code-page" && args[0] != "--archive-compare")
                throw new ArgumentException("gzip格納名の文字コード指定が不正です。");
            if (option == "--gzip-name-code-page") leftCodePage = codePage; else rightCodePage = codePage;
        }
        if (outputCodePageSpecified && (args[2].EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || !(args[2].EndsWith(".gz", StringComparison.OrdinalIgnoreCase) || args[2].EndsWith(".gzip", StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("出力格納名の文字コードは単一ファイルgzip出力だけに指定できます。");
        var writeOptions = new ManagedArchiveWriteOptions(outputCodePage);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        var token = cancel.Token;
        string? password = null, rightPassword = null;
        if (passwordInput)
        {
            if (!Console.IsInputRedirected) throw new ArgumentException("パスワードはリダイレクトした標準入力から送ってください。");
            password = await ReadPasswordAsync(token);
            if (args[0] == "--archive-compare") rightPassword = await ReadPasswordAsync(token);
        }
        var options = new ManagedArchiveReadOptions(leftCodePage, leftPayloadKind, leftCompressionPayloadKind);
        var service = new ManagedArchive(readOptions: options);
        if (args[0] == "--archive-create")
            await ArchiveActions.CreateAsync(args[1], args[2], token, writeOptions);
        else if (args[0] == "--archive-entry")
            await ArchiveActions.ExportAsync(args[1], args[2], args[3], password, token, options);
        else if (args[0] == "--archive-repack")
            await Task.Run(() => service.Repack(args[1], args[2], password, token, writeOptions), token);
        else if (args[0] == "--archive-extract")
            await Task.Run(() => service.ExtractAll(args[1], args[2], password, token), token);
        else
        {
            var left = await Task.Run(() => service.ReadManifest(args[1], password, token), token);
            if (args[0] == "--archive-list")
            {
                CommandLine.WriteJson(writer =>
                {
                    writer.WriteString("format", left.Format); writer.WriteStartArray("entries");
                    foreach (var entry in left.Entries)
                    {
                        writer.WriteStartObject(); writer.WriteString("path", entry.Path); writer.WriteBoolean("directory", entry.IsDirectory);
                        writer.WriteNumber("size", entry.Size); writer.WriteString("sha256", entry.Sha256); writer.WriteBoolean("encrypted", entry.IsEncrypted); writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                });
                return 0;
            }
                var right = await Task.Run(() => new ManagedArchive(readOptions: new(rightCodePage ?? leftCodePage, rightPayloadKind ?? leftPayloadKind, rightCompressionPayloadKind ?? leftCompressionPayloadKind)).ReadManifest(args[2], rightPassword, token), token);
            var rows = ArchiveComparison.Compare(left, right); var different = rows.Any(row => row.Status != "Equal");
            CommandLine.WriteJson(writer =>
            {
                writer.WriteBoolean("different", different); writer.WriteStartArray("entries");
                foreach (var row in rows) { writer.WriteStartObject(); writer.WriteString("path", row.Path); writer.WriteString("status", row.Status); writer.WriteEndObject(); }
                writer.WriteEndArray();
            });
            return different ? 1 : 0;
        }
        CommandLine.WriteJson(writer => writer.WriteString("output", Path.GetFullPath(args[0] == "--archive-entry" ? args[3] : args[2]))); return 0;
    }

    internal static async Task<string> ReadPasswordAsync(CancellationToken token)
    {
        // 引数・環境変数・応答へ転記せず、過大な標準入力も制限する。
        var password = new System.Text.StringBuilder(); var buffer = new char[1];
        while (await Console.In.ReadAsync(buffer.AsMemory(), token) != 0)
        {
            if (buffer[0] == '\n') return password.ToString().TrimEnd('\r');
            if (password.Length == 4096) throw new ArgumentException("パスワード入力が長すぎます。");
            password.Append(buffer[0]);
        }
        if (password.Length == 0) throw new ArgumentException("パスワードの標準入力が不足しています。");
        return password.ToString().TrimEnd('\r');
    }
}

internal static class ArchiveActions
{
    internal static Task ExportAsync(string archive, string entry, string output, string? password, CancellationToken token,
        ManagedArchiveReadOptions? readOptions = null)
        => ExportCoreAsync(archive, output,
            cancellation => Task.Run(() => new ManagedArchive(readOptions: readOptions).ReadEntryForExport(archive, entry, password, cancellation), cancellation), token);

    internal static Task ExportSourceAsync(ArchiveSource source, string entry, string output, string? descriptor,
        ManagedArchiveLimits limits, IReadOnlyList<string?> passwords, CancellationToken token, Action? beforePublication = null)
        => ExportCoreAsync(source.RootPath, output,
            cancellation => Task.Run(() => new ManagedArchive(limits).ResolveEntry(source, entry,
                checked((int)Math.Min(limits.MaximumEntryBytes, limits.MaximumOutputBytes)), passwords, cancellation), cancellation), token, descriptor, beforePublication);

    private static async Task ExportCoreAsync(string archive, string output, Func<CancellationToken, Task<byte[]>> read,
        CancellationToken token, string? descriptor = null, Action? beforePublication = null)
    {
        var target = ValidatePath(output); var original = Path.GetFullPath(archive);
        CheckInputs();
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("出力先のディレクトリがありません。");
        ValidateWritable(target);
        var bytes = await read(token);
        beforePublication?.Invoke(); token.ThrowIfCancellationRequested();
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".diffbeacon-entry-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); }
            token.ThrowIfCancellationRequested(); ValidatePath(target); ValidateWritable(target); CheckInputs();
            if (!OperatingSystem.IsWindows() && File.Exists(target)) File.SetUnixFileMode(temporary, File.GetUnixFileMode(target));
            if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        void CheckInputs()
        {
            if (ArchivePaths.SameFile(target, original) || descriptor is not null && ArchivePaths.SameFile(target, descriptor))
                throw new IOException("元アーカイブや入力descriptorへエントリを上書きできません。");
        }
    }
    internal static async Task CreateAsync(string directory, string output, CancellationToken token, ManagedArchiveWriteOptions? writeOptions = null)
    {
        var root = ValidatePath(directory); var target = ValidatePath(output);
        if (File.Exists(root))
        {
            if (ArchivePaths.SameFile(root, target)) throw new IOException("入力ファイルへアーカイブを上書きできません。");
            await Task.Run(() => new ManagedArchive().WriteArchive(target, SingleFile(), token, writeOptions), token);
            return;
        }
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("アーカイブ化するファイルまたはフォルダーがありません。");
        var entries = new List<(string Path, string Name, bool Directory)>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()).Order(StringComparer.Ordinal))
            {
                ValidatePath(path); if (ArchivePaths.SameFile(path, target)) continue;
                if (entries.Count == 100_000) throw new InvalidDataException("アーカイブ化する項目数の上限を超えました。");
                var isDirectory = Directory.Exists(path); entries.Add((path, Path.GetRelativePath(root, path).Replace('\\', '/'), isDirectory));
                if (isDirectory) pending.Push(path);
            }
        }
        await Task.Run(() => new ManagedArchive().WriteArchive(target, Content(), token, writeOptions), token);
        IEnumerable<ManagedArchiveWriteEntry> SingleFile()
        {
            token.ThrowIfCancellationRequested(); ValidatePath(root);
            using var file = new FileStream(root, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 256L * 1024 * 1024) throw new InvalidDataException("アーカイブ化するファイルは256 MiBまでです。");
            var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            if (file.ReadByte() >= 0) throw new IOException("読込み中にファイルサイズが変わりました。");
            yield return new(Path.GetFileName(root), bytes, File.GetLastWriteTime(root));
        }
        IEnumerable<ManagedArchiveWriteEntry> Content()
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested(); ValidatePath(entry.Path);
                if (entry.Directory) yield return new(entry.Name, null, File.GetLastWriteTime(entry.Path));
                else
                {
                    using var file = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (file.Length > 256L * 1024 * 1024) throw new InvalidDataException("アーカイブ化するファイルは256 MiBまでです。");
                    var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
                    if (file.ReadByte() >= 0) throw new IOException("読込み中にファイルサイズが変わりました。");
                    yield return new(entry.Name, bytes, File.GetLastWriteTime(entry.Path));
                }
            }
        }
    }
    internal static string ValidatePath(string path)
    {
        var full = Path.GetFullPath(path); string? current = full;
        while (current is not null)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null || info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("アーカイブ操作のパスにリンクを使用できません。");
            current = Path.GetDirectoryName(current);
        }
        return full;
    }
    private static void ValidateWritable(string path)
    {
        if (Directory.Exists(path)) throw new IOException("出力先はファイルである必要があります。");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)) throw new UnauthorizedAccessException("読み取り専用ファイルを上書きできません。");
    }
}
