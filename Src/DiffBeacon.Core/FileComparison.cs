using System.Security.Cryptography;

namespace DiffBeacon.Core;

public sealed record BinaryDifference(long Offset, int LeftLength, int RightLength);
public sealed record BinaryDiffResult(long LeftLength, long RightLength, IReadOnlyList<BinaryDifference> Differences)
{
    public bool HasDifferences => Differences.Count != 0;
    public bool IsApproximate { get; init; }
}

public static class BinaryDiffer
{
    public static BinaryDiffResult Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right,
        int maxDifferenceRanges = 65_536, CancellationToken cancellationToken = default)
    {
        if (maxDifferenceRanges < 1) throw new ArgumentOutOfRangeException(nameof(maxDifferenceRanges));
        var differences = new List<BinaryDifference>();
        var common = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < common)
        {
            if ((index & 0xffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (left[index] == right[index]) { index++; continue; }
            if (differences.Count == maxDifferenceRanges - 1)
            {
                differences.Add(new(index, left.Length - index, right.Length - index));
                return new(left.Length, right.Length, differences) { IsApproximate = true };
            }
            var start = index++;
            while (index < common && left[index] != right[index]) index++;
            differences.Add(new(start, index - start, index - start));
        }
        if (left.Length != right.Length) differences.Add(new(common, left.Length - common, right.Length - common));
        return new(left.Length, right.Length, differences);
    }

    public static bool IsProbablyBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe }) || bytes.StartsWith(new byte[] { 0xfe, 0xff }) ||
            bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) return false;
        var length = Math.Min(bytes.Length, 8192);
        var evenZeros = 0;
        var oddZeros = 0;
        var controls = 0;
        for (var i = 0; i < length; i++)
        {
            if (bytes[i] == 0) { if (i % 2 == 0) evenZeros++; else oddZeros++; }
            if (bytes[i] < 32 && bytes[i] is not (9 or 10 or 12 or 13)) controls++;
        }
        if (length >= 4 && bytes.Length % 2 == 0 &&
            ((oddZeros > length / 4 && evenZeros < length / 16) || (evenZeros > length / 4 && oddZeros < length / 16))) return false;
        return evenZeros + oddZeros > 0 || (length > 0 && controls > length / 10);
    }
}

public sealed record FileComparisonResult(string LeftPath, string RightPath, bool IsBinary,
    DiffResult? TextDiff, BinaryDiffResult? BinaryDiff, TextDocument? LeftDocument, TextDocument? RightDocument)
{
    public bool HasDifferences => TextDiff?.HasDifferences ?? BinaryDiff?.HasDifferences ?? false;
}

public static class FileComparer
{
    public static async Task<FileComparisonResult> CompareAsync(string leftPath, string rightPath,
        ComparisonOptions? options = null, CancellationToken cancellationToken = default)
    {
        leftPath = Path.GetFullPath(leftPath);
        rightPath = Path.GetFullPath(rightPath);
        if (new FileInfo(leftPath).Length > 256L * 1024 * 1024 || new FileInfo(rightPath).Length > 256L * 1024 * 1024)
            throw new InvalidDataException("ファイル比較の上限 256 MiB を超えています。ディレクトリのハッシュ比較を使用してください。");
        var leftBytes = await File.ReadAllBytesAsync(leftPath, cancellationToken).ConfigureAwait(false);
        var rightBytes = await File.ReadAllBytesAsync(rightPath, cancellationToken).ConfigureAwait(false);
        if (BinaryDiffer.IsProbablyBinary(leftBytes) || BinaryDiffer.IsProbablyBinary(rightBytes))
            return new(leftPath, rightPath, true, null, BinaryDiffer.Compare(leftBytes, rightBytes,
                cancellationToken: cancellationToken), null, null);
        var left = TextDocument.FromBytes(leftPath, leftBytes, new());
        var right = TextDocument.FromBytes(rightPath, rightBytes, new());
        return new(leftPath, rightPath, false, TextDiffer.Compare(left.Text, right.Text, options, cancellationToken), null, left, right);
    }
}

public enum DirectoryComparisonMode { Content, Hash, TimeAndSize }
public enum DirectoryEntryKind { File, Directory, SymbolicLink }
public enum DirectoryDifferenceKind { Equal, Modified, LeftOnly, RightOnly, TypeConflict, Error }
public sealed record DirectoryComparisonOptions
{
    public bool Recursive { get; init; } = true;
    public DirectoryComparisonMode Mode { get; init; } = DirectoryComparisonMode.Content;
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];
    public ComparisonOptions? TextOptions { get; init; }
    public FileFilter? FileFilter { get; init; }
}
public sealed record DirectoryEntry(string RelativePath, string? LeftPath, string? RightPath,
    DirectoryEntryKind EntryKind, DirectoryDifferenceKind Kind, long? LeftSize, long? RightSize, string? Error = null)
{
    public DirectoryDifferenceKind Status => Kind;
}
public sealed record DirectoryComparisonResult(string LeftPath, string RightPath, IReadOnlyList<DirectoryEntry> Entries)
{
    public bool HasDifferences => Entries.Any(entry => entry.Kind != DirectoryDifferenceKind.Equal);
}

public static class DirectoryComparer
{
    private sealed record Entry(string Path, DirectoryEntryKind Kind, long Size, DateTime WriteTime, string? LinkTarget);

    public static async Task<DirectoryComparisonResult> CompareAsync(string leftPath, string rightPath,
        DirectoryComparisonOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        leftPath = Path.GetFullPath(leftPath);
        rightPath = Path.GetFullPath(rightPath);
        if (!Directory.Exists(leftPath)) throw new DirectoryNotFoundException(leftPath);
        if (!Directory.Exists(rightPath)) throw new DirectoryNotFoundException(rightPath);
        // macOS/Linux のパスは大文字・小文字を区別する。リンクは再帰対象にしない。
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var results = new List<DirectoryEntry>();
        Dictionary<string, Entry> Scan(string root, bool isLeft)
        {
            var entries = new Dictionary<string, Entry>(comparer);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                try
                {
                    foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(root, item.FullName).Replace('\\', '/');
                        if (options.ExcludePatterns.Any(pattern => GlobMatches(relative, pattern, comparer == StringComparer.OrdinalIgnoreCase))) continue;
                        var link = (item.Attributes & FileAttributes.ReparsePoint) != 0;
                        var kind = link ? DirectoryEntryKind.SymbolicLink : item is DirectoryInfo ? DirectoryEntryKind.Directory : DirectoryEntryKind.File;
                        var size = item is FileInfo file && !link ? file.Length : 0;
                        if (options.FileFilter is not null && !options.FileFilter.Matches(relative, item is DirectoryInfo, size, item.LastWriteTimeUtc)) continue;
                        entries[relative] = new(item.FullName, kind, size, item.LastWriteTimeUtc, link ? item.LinkTarget : null);
                        if (kind == DirectoryEntryKind.Directory && options.Recursive) pending.Push(item.FullName);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    var relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
                    results.Add(new(relative, isLeft ? directory : null, isLeft ? null : directory,
                        DirectoryEntryKind.Directory, DirectoryDifferenceKind.Error, null, null, error.Message));
                }
            }
            return entries;
        }
        var left = Scan(leftPath, true);
        var right = Scan(rightPath, false);
        foreach (var relative in left.Keys.Union(right.Keys, comparer).Order(comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            left.TryGetValue(relative, out var l);
            right.TryGetValue(relative, out var r);
            var kind = l?.Kind ?? r!.Kind;
            var status = l is null ? DirectoryDifferenceKind.RightOnly : r is null ? DirectoryDifferenceKind.LeftOnly : DirectoryDifferenceKind.Equal;
            string? errorMessage = null;
            if (l is not null && r is not null)
            {
                try
                {
                    if (l.Kind != r.Kind) status = DirectoryDifferenceKind.TypeConflict;
                    else if (kind == DirectoryEntryKind.SymbolicLink)
                        status = l.LinkTarget == r.LinkTarget ? DirectoryDifferenceKind.Equal : DirectoryDifferenceKind.Modified;
                    else if (kind == DirectoryEntryKind.File)
                    {
                        bool equal;
                        if (options.Mode == DirectoryComparisonMode.TimeAndSize) equal = l.Size == r.Size && l.WriteTime == r.WriteTime;
                        else if (options.TextOptions is not null && options.Mode == DirectoryComparisonMode.Content)
                            equal = !(await FileComparer.CompareAsync(l.Path, r.Path, options.TextOptions, cancellationToken).ConfigureAwait(false)).HasDifferences;
                        else if (l.Size != r.Size) equal = false;
                        else if (options.Mode == DirectoryComparisonMode.Hash)
                        {
                            var lh = await HashAsync(l.Path, cancellationToken).ConfigureAwait(false);
                            var rh = await HashAsync(r.Path, cancellationToken).ConfigureAwait(false);
                            equal = lh.AsSpan().SequenceEqual(rh);
                        }
                        else equal = await ContentEqualsAsync(l.Path, r.Path, cancellationToken).ConfigureAwait(false);
                        status = equal ? DirectoryDifferenceKind.Equal : DirectoryDifferenceKind.Modified;
                    }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
                {
                    status = DirectoryDifferenceKind.Error;
                    errorMessage = error.Message;
                }
            }
            results.Add(new(relative, l?.Path, r?.Path, kind, status, l?.Size, r?.Size, errorMessage));
        }
        return new(leftPath, rightPath, results.OrderBy(entry => entry.RelativePath, comparer).ToArray());
    }

    public static bool GlobMatches(string path, string pattern, bool ignoreCase = false)
    {
        path = path.Replace('\\', '/');
        pattern = pattern.Replace('\\', '/');
        // 区切りなしのパターンは任意階層のファイル名にも適用する。
        if (!pattern.Contains('/')) path = path[(path.LastIndexOf('/') + 1)..];
        var p = 0;
        var s = 0;
        var star = -1;
        var retry = 0;
        while (s < path.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || (ignoreCase ? char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(path[s]) : pattern[p] == path[s]))) { p++; s++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; retry = s; }
            else if (star >= 0) { p = star + 1; s = ++retry; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
    }

    private static async Task<bool> ContentEqualsAsync(string left, string right, CancellationToken token)
    {
        await using var a = File.OpenRead(left);
        await using var b = File.OpenRead(right);
        var ab = new byte[64 * 1024];
        var bb = new byte[64 * 1024];
        while (true)
        {
            var al = await a.ReadAtLeastAsync(ab, ab.Length, false, token).ConfigureAwait(false);
            var bl = await b.ReadAtLeastAsync(bb, bb.Length, false, token).ConfigureAwait(false);
            if (al != bl || !ab.AsSpan(0, al).SequenceEqual(bb.AsSpan(0, bl))) return false;
            if (al == 0) return true;
        }
    }
}
