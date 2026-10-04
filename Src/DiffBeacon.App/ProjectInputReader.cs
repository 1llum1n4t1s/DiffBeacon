using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static class ProjectInputReader
{
    internal static async Task<TextDocument> ReadTextAsync(ComparisonProject project, int side,
        CancellationToken token, string? physicalSnapshot = null, IReadOnlyList<string?>? passwords = null)
    {
        ProjectInputs.EnsureWorkingFormat(project);
        var input = ProjectInputs.Archive(project, side)?.Copy();
        if (input is null)
            return await TextDocument.LoadAsync(ArchiveActions.ValidatePath(physicalSnapshot ?? ProjectInputs.PathFor(project, side)), token).ConfigureAwait(false);
        if (physicalSnapshot is not null) input.RootPath = physicalSnapshot;
        if (input.MissingEntryChain is not null)
        {
            await Task.Run(() => ResolveMissingManifest(input, passwords, token), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return TextDocument.FromSnapshot([]);
        }
        if (input.LeafEntry is null) throw new InvalidDataException("Text比較には内包ファイルを指定してください。");
        var options = new TextLoadOptions();
        var source = input.ToSource();
        var bytes = await Task.Run(() => new ManagedArchive().ResolveEntry(source, input.LeafEntry,
            checked((int)options.MaxFileSize), passwords, token), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var working = input.WorkingDocuments?.SingleOrDefault(copy => copy.EntryChain.SequenceEqual(input.EntryChain) && copy.LeafEntry == input.LeafEntry);
        if (working?.IsBinary == true) throw new InvalidDataException("Binary作業版はBinary形式で開き直してください。");
        var original = TextDocument.FromSnapshot(bytes, options);
        if (working is null) return original;
        if (working.EncodingName != original.EncodingName || working.HasBom != original.HasBom)
            throw new InvalidDataException("作業版の文字コード／BOMが原本と一致しません。");
        return working.Document();
    }

    internal static async Task<byte[]> ReadBytesAsync(ComparisonProject project, int side, int maximumBytes,
        CancellationToken token, IReadOnlyList<string?>? passwords = null)
    {
        ProjectInputs.EnsureWorkingFormat(project);
        var input = ProjectInputs.Archive(project, side)?.Copy();
        if (input is not null)
        {
            if (input.MissingEntryChain is not null)
            {
                await Task.Run(() => ResolveMissingManifest(input, passwords, token), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); return [];
            }
            if (input.LeafEntry is null) throw new InvalidDataException("内包ファイルを指定してください。");
            var original = await Task.Run(() => new ManagedArchive().ResolveEntry(input.ToSource(), input.LeafEntry,
                maximumBytes, passwords, token), token).ConfigureAwait(false);
            var working = input.WorkingDocuments?.SingleOrDefault(copy => copy.EntryChain.SequenceEqual(input.EntryChain) && copy.LeafEntry == input.LeafEntry);
            if (working is null) return original;
            if (working.Bytes is null || working.Bytes.Length > maximumBytes) throw new InvalidDataException("作業版の保持サイズ上限を超えました。");
            return working.Bytes.ToArray();
        }
        var path = ArchiveActions.ValidatePath(ProjectInputs.PathFor(project, side));
        if (new FileInfo(path).Length > maximumBytes) throw new InvalidDataException("内包比較の保持サイズ上限を超えました。");
        var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        if (bytes.Length > maximumBytes) throw new InvalidDataException("内包比較の保持サイズ上限を超えました。");
        return bytes;
    }

    internal static void ValidateSnapshot(ArchiveProjectInput input, string physicalSnapshot, CancellationToken token)
    {
        var captured = input.Copy(); captured.RootPath = physicalSnapshot;
        if (captured.MissingEntryChain is not null) { ResolveMissingManifest(captured, null, token); return; }
        var result = new ManagedArchive().ResolveManifest(captured.ToSource(), cancellationToken: token);
        var leaf = captured.LeafEntry is null ? null : new ArchiveSource(Path.GetFullPath(captured.RootPath), [captured.LeafEntry]).EntryChain[0];
        if (leaf is not null && !result.Manifest.Entries.Any(entry => entry.Path == leaf && !entry.IsDirectory))
            throw new InvalidDataException("包装する内包ファイルが存在しないか、ディレクトリです。");
        ValidateWorkingSources(captured, null, token);
    }

    internal static void ValidateWorkingSources(ArchiveProjectInput input, IReadOnlyList<string?>? passwords, CancellationToken token)
    {
        foreach (var copy in input.WorkingDocuments ?? [])
        {
            var passwordChain = passwords is null ? null : passwords.Take(copy.EntryChain.Length + 1).ToArray();
            try { ValidateWorkingSource(input, copy, passwordChain, token); }
            finally { if (passwordChain is not null) Array.Clear(passwordChain); }
        }
    }

    internal static void ValidateWorkingSource(ArchiveProjectInput input, ArchiveWorkingSnapshot copy,
        IReadOnlyList<string?>? passwords, CancellationToken token)
    {
        copy.Validate(input.RootPath);
        // 作業版でも全CRC/SHAと元leafの存在、文字コード/BOMを完全階層で検証する。
        var source = new ArchiveSource(Path.GetFullPath(input.RootPath), copy.EntryChain, input.RootSha256);
        var bytes = new ManagedArchive().ResolveEntry(source, copy.LeafEntry, copy.MaximumFileBytes, passwords, token);
        if (copy.IsBinary) return;
        var original = TextDocument.FromSnapshot(bytes);
        if (original.EncodingName != copy.EncodingName || original.HasBom != copy.HasBom)
            throw new InvalidDataException("作業版の文字コード／BOMが原本と一致しません。");
    }

    internal static ManagedArchiveSourceManifest ResolveMissingManifest(ArchiveProjectInput input,
        IReadOnlyList<string?>? passwords, CancellationToken token)
    {
        var missing = input.MissingEntryChain;
        if (missing is null || missing.Length == 0) throw new InvalidDataException("不在入力の格納名を指定してください。");
        var anchor = new ArchiveSource(Path.GetFullPath(input.RootPath), missing).EntryChain[0];
        var result = new ManagedArchive().ResolveManifest(input.ToSource(), passwords, token);
        EnsureAbsent(result.Manifest, anchor, token);
        return result;
    }

    internal static void EnsureAbsent(ManagedArchiveManifest manifest, string anchor, CancellationToken token)
    {
        // 全containerを解決した結果でだけ不在を採用し、破損やpassword失敗を空入力へ変換しない。
        token.ThrowIfCancellationRequested();
        if (manifest.Entries.Any(entry => StringComparer.Ordinal.Equals(entry.Path, anchor)))
            throw new InvalidDataException("不在として保存した格納名が存在します。親の比較から開き直してください。");
    }
}
