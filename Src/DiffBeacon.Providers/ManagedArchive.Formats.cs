using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Writers.SevenZip;

namespace DiffBeacon.Providers;

public sealed partial class ManagedArchive
{
    public static bool SupportsOutput(string path) => OutputFormat(path) is not null;

    private static string? OutputFormat(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var name = path.ToLowerInvariant();
        if (name.EndsWith(".tar.z", StringComparison.Ordinal) || name.EndsWith(".taz", StringComparison.Ordinal)) return "tar.Z";
        if (name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tgz", StringComparison.Ordinal)) return "tar.gz";
        if (name.EndsWith(".tar.bz2", StringComparison.Ordinal) || name.EndsWith(".tbz2", StringComparison.Ordinal) || name.EndsWith(".tbz", StringComparison.Ordinal)) return "tar.bz2";
        return Path.GetExtension(name) switch
        {
            ".zip" or ".jar" or ".ear" or ".war" or ".xpi" => "zip",
            ".tar" => "tar", ".7z" => "7z", ".gz" or ".gzip" => "gzip", ".bz2" => "bzip2", ".z" => "Z", _ => null
        };
    }

    public void WriteArchive(string destinationPath, IEnumerable<ManagedArchiveWriteEntry> entries,
        CancellationToken cancellationToken = default, ManagedArchiveWriteOptions? writeOptions = null)
        => WriteArchiveCore(destinationPath, entries, RequireOutputFormat(destinationPath), cancellationToken, writeOptions);

    public void Repack(string sourcePath, string destinationPath, string? password = null,
        CancellationToken cancellationToken = default, ManagedArchiveWriteOptions? writeOptions = null)
        => RepackCore(sourcePath, destinationPath, RequireOutputFormat(destinationPath), password, cancellationToken, writeOptions);

    private static string RequireOutputFormat(string path) => OutputFormat(path)
        ?? throw new InvalidDataException("出力形式は 7z・ZIP 派生・TAR・tar.gz・tar.bz2・tar.Z・gzip・BZip2・Z を指定してください。");

    private void WriteArchiveCore(string destinationPath, IEnumerable<ManagedArchiveWriteEntry> entries,
        string format, CancellationToken token, ManagedArchiveWriteOptions? writeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        SaveOutput(ValidateLocalPath(destinationPath, false), format, token, write =>
        {
            var names = new EntryNames(_limits.MaximumEntries, _limits.MaximumPathCharacters);
            long total = 0;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(entry);
                var name = ValidateEntryPath(entry.Path);
                names.Add(name, entry.Content is null);
                if (entry.Content is not { } bytes) write(name, null, entry.LastModifiedTime);
                else
                {
                    if (bytes.Length > _limits.MaximumEntryBytes || bytes.Length > _limits.MaximumDecodedBytes - total)
                        throw new InvalidDataException("アーカイブ内容のサイズ上限を超えました。");
                    total += bytes.Length;
                    using var content = new MemoryStream(bytes.ToArray(), writable: false);
                    using var bounded = new CheckedStream(content, _limits.MaximumEntryBytes, token);
                    write(name, bounded, entry.LastModifiedTime);
                }
            }
        }, writeOptions);
    }

    private void RepackCore(string sourcePath, string destinationPath, string format, string? password, CancellationToken token, ManagedArchiveWriteOptions? writeOptions = null)
    {
        var source = ValidateLocalPath(sourcePath, true);
        var destination = ValidateLocalPath(destinationPath, false);
        if (ArchivePaths.SameFile(source, destination)) throw new IOException("元アーカイブと出力先には別のパスが必要です。");
        SaveOutput(destination, format, token, write =>
            Read(source, password, token, entry => !entry.IsDirectory, (entry, content) =>
            {
                token.ThrowIfCancellationRequested();
                if (content is null) write(entry.Path, null, entry.LastModifiedTime);
                else
                {
                    content.Position = 0;
                    using var bounded = new CheckedStream(content, _limits.MaximumEntryBytes, token);
                    write(entry.Path, bounded, entry.LastModifiedTime);
                }
            }), writeOptions);
    }

    private void SaveOutput(string destination, string format, CancellationToken token,
        Action<Action<string, Stream?, DateTime?>> populate, ManagedArchiveWriteOptions? writeOptions = null)
    {
        token.ThrowIfCancellationRequested();
        ValidateOutput(destination);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".diffbeacon-{Guid.NewGuid():N}.archive.tmp");
        var created = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                created = true;
                using var output = new CheckedStream(file, _limits.MaximumOutputBytes, token);
                if (format == "gzip")
                    WriteGZipOutput(output, populate, writeOptions ?? new ManagedArchiveWriteOptions(), token);
                else if (format is "bzip2" or "Z")
                    WriteCompressionOutput(output, format, populate, token);
                else if (format == "7z")
                {
                    using var writer = new SevenZipWriter(output, new SevenZipWriterOptions(CompressionType.LZMA2));
                    populate((name, content, time) =>
                    {
                        if (content is null) writer.WriteDirectory(name, time);
                        else writer.Write(name, content, time);
                    });
                }
                else if (format == "zip")
                {
                    using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                    populate((name, content, time) =>
                    {
                        token.ThrowIfCancellationRequested();
                        var entry = zip.CreateEntry(content is null ? name + "/" : name, CompressionLevel.Optimal);
                        if (time is { } modified)
                        {
                            // ZIP のDOS日時は1980〜2107年、2秒精度に制限される。
                            var stamp = new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Unspecified), TimeSpan.Zero);
                            entry.LastWriteTime = stamp.Year < 1980 ? new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
                                : stamp.Year > 2107 ? new(2107, 12, 31, 23, 59, 58, TimeSpan.Zero) : stamp;
                        }
                        if (content is not null) { using var sink = entry.Open(); content.CopyTo(sink); }
                    });
                }
                else
                {
                    using Stream? compression = format switch
                    {
                        "tar.gz" => new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true),
                        "tar.bz2" => BZip2Stream.Create(output, SharpCompress.Compressors.CompressionMode.Compress, false, leaveOpen: true),
                        "tar.Z" => new ZWriteStream(output, token, leaveOpen: true),
                        _ => null
                    };
                    using var tar = new TarWriter(compression ?? output, TarEntryFormat.Pax, leaveOpen: true);
                    populate((name, content, time) =>
                    {
                        token.ThrowIfCancellationRequested();
                        var entry = new PaxTarEntry(content is null ? TarEntryType.Directory : TarEntryType.RegularFile, name)
                        {
                            Mode = content is null ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                : UnixFileMode.UserRead | UnixFileMode.UserWrite
                        };
                        if (time is { } modified) entry.ModificationTime = new DateTimeOffset(modified.ToUniversalTime());
                        if (content is not null) entry.DataStream = content;
                        tar.WriteEntry(entry);
                    });
                    // 正常なTAR終端を書き出した後だけZの残codeを完了する。
                    tar.Dispose();
                    if (compression is ZWriteStream z) z.Complete();
                }
                token.ThrowIfCancellationRequested();
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            ValidateOutput(destination);
            if (!OperatingSystem.IsWindows() && File.Exists(destination)) File.SetUnixFileMode(temporary, File.GetUnixFileMode(destination));
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
            created = false;
        }
        finally { if (created) File.Delete(temporary); }
    }

    // 親の明示ディレクトリと暗黙ディレクトリを分け、順序に依存しない衝突検証を行う。
    private sealed class EntryNames(int maximum, int maximumCharacters, ArchiveReadBudget? budget = null)
    {
        private readonly Dictionary<string, (string Name, bool Directory, bool Explicit)> _names = new(StringComparer.OrdinalIgnoreCase);
        private int _count;
        private long _characters;
        private void AddName(string key, string name, bool directory, bool explicitlyStored)
        {
            // 辞書キーと元表記を両方計上し、深いパスの累積prefix確保を抑える。
            var characters = (long)key.Length + name.Length;
            if (_names.Count >= maximum || characters > maximumCharacters - _characters)
                throw new InvalidDataException("暗黙の親を含む格納パス数または保持文字数の上限を超えました。");
            budget?.Item();
            budget?.PathCharacters(characters);
            _names.Add(key, (name, directory, explicitlyStored));
            _characters += characters;
        }
        public void Add(string name, bool directory)
        {
            if (++_count > maximum) throw new InvalidDataException("エントリ数の上限を超えました。");
            var key = name.Normalize(NormalizationForm.FormC);
            if (_names.TryGetValue(key, out var prior))
            {
                if (prior.Explicit || !directory || !prior.Directory || prior.Name != name)
                    throw new InvalidDataException("重複・別表記・ファイルとディレクトリの衝突した格納名です。");
            }
            var parent = name;
            while (parent.LastIndexOf('/') is var slash && slash >= 0)
            {
                parent = parent[..slash];
                var parentKey = parent.Normalize(NormalizationForm.FormC);
                if (_names.TryGetValue(parentKey, out var existing))
                {
                    if (!existing.Directory || existing.Name != parent)
                        throw new InvalidDataException("格納名の親パスがファイルまたは別表記と衝突しています。");
                }
                else AddName(parentKey, parent, true, false);
            }
            if (_names.ContainsKey(key)) _names[key] = (name, directory, true);
            else AddName(key, name, directory, true);
        }
    }

    public void ExtractAll(string sourcePath, string destinationDirectory, string? password = null,
        CancellationToken cancellationToken = default)
    {
        var source = ValidateLocalPath(sourcePath, true);
        var destination = ValidateExtractionDestination(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".diffbeacon-{Guid.NewGuid():N}.extract.tmp");
        var created = false;
        try
        {
            if (File.Exists(temporary) || Directory.Exists(temporary)) throw new IOException("一時展開先が既に存在します。");
            Directory.CreateDirectory(temporary);
            created = true;
            var directoryTimes = new List<(string Path, DateTime Time)>();
            Read(source, password, cancellationToken, entry => !entry.IsDirectory, (entry, content) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(temporary, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                ValidateLocalPath(target, false);
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(target);
                    if (entry.LastModifiedTime is { } time) directoryTimes.Add((target, time));
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        content!.Position = 0;
                        using var bounded = new CheckedStream(output, _limits.MaximumEntryBytes, cancellationToken);
                        content.CopyTo(bounded);
                        output.Flush(flushToDisk: true);
                    }
                    if (entry.LastModifiedTime is { } time) File.SetLastWriteTimeUtc(target, time.ToUniversalTime());
                }
            });
            foreach (var (path, time) in directoryTimes.OrderByDescending(item => item.Path.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.SetLastWriteTimeUtc(path, time.ToUniversalTime());
            }
            cancellationToken.ThrowIfCancellationRequested();
            ValidateExtractionDestination(destination);
            Directory.Move(temporary, destination);
            created = false;
        }
        finally { if (created) Directory.Delete(temporary, recursive: true); }
    }

    private static string ValidateExtractionDestination(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(ValidateLocalPath(path, false));
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException("展開先には存在しないディレクトリを指定してください。");
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !Directory.Exists(parent)) throw new DirectoryNotFoundException("展開先の親ディレクトリがありません。");
        return full;
    }
}
