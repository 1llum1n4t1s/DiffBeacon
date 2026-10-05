using DiffBeacon.Core;
using System.Security.Cryptography;

namespace DiffBeacon.App;

public static partial class FolderOperations
{
    private const long WindowsMetadataStateAllowance = 512;

    private static FolderCopyWindowsMetadataSnapshot WindowsMetadataSnapshot(FolderCopyWindowsMetadata metadata,
        CopyBudget budget, ulong? expectedWriteTime = null)
    {
        budget.ReserveRetainedBytes(512);
        return new(metadata.CreationTime, expectedWriteTime ?? metadata.LastWriteTime, metadata.Size,
            metadata.ObservedAttributes, metadata.VolumeSerial, metadata.FileIndex, metadata.SecurityControl,
            Convert.ToHexString(SHA256.HashData(metadata.CanonicalSecurity)),
            Convert.ToHexString(SHA256.HashData(metadata.CanonicalExtendedAttributes)));
    }

    private static bool UsesLocalNtfsMetadata(string sourceRoot, string destinationRoot)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return LocalNtfs(sourceRoot) && LocalNtfs(destinationRoot);

        static bool LocalNtfs(string path)
        {
            // 拡張drive表記も同じlocal volumeとして扱う。UNC/device volumeはこの資格へ含めない。
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length >= 7
                && char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] == '\\')
                path = path[4..];
            var root = Path.GetPathRoot(path) ?? throw new IOException("Windowsコピーのvolumeを確認できません。");
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var drive = new DriveInfo(root);
            return drive.DriveType is DriveType.Fixed or DriveType.Removable
                && drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string WindowsMetadataQualification(bool enabled) => enabled
        ? FolderCopyWindowsMetadata.QualificationScope + ";OutsideRootAncestorWriteTimeUntracked"
        : OperatingSystem.IsWindows() ? "OrdinaryAttributesOnly;AdvancedMetadataUnverified" : "NotWindows";

    private static void CaptureWindowsMetadataChain(string path,
        Dictionary<string, FolderCopyWindowsMetadata> state, CopyBudget budget, CancellationToken token)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            Guard(token, null);
            if (state.ContainsKey(current)) continue;
            var basic = ReadSnapshot(current);
            if (basic is null) continue;
            // map key・計画のreadonly列・実行側stateを含め、保持する前に一括予約する。
            budget.ReserveRetainedBytes(checked(WindowsMetadataStateAllowance + 2L * current.Length));
            var metadata = FolderCopyWindowsMetadata.Capture(current, budget, token);
            RequireWindowsSnapshot(basic, metadata);
            state.Add(current, metadata);
        }
    }

    private static void RequireWindowsSnapshot(FolderCopySnapshot basic, FolderCopyWindowsMetadata metadata)
    {
        if ((basic.Kind == DirectoryEntryKind.Directory) != metadata.IsDirectory || basic.Size != metadata.Size
            || checked((ulong)basic.LastWriteTimeUtc.ToFileTimeUtc()) != metadata.LastWriteTime
            || basic.Attributes != metadata.ObservedAttributes)
            throw new IOException("Windowsの基本snapshotとmetadataの観測値が一致しません。");
    }

    private static void RequirePreparedWindowsMetadata(string root,
        IReadOnlyDictionary<string, FolderCopyWindowsMetadata> state, CopyBudget budget,
        CancellationToken token, Action? validateContext = null)
    {
        Guard(token, validateContext);
        foreach (var pair in state)
            RequirePreparedWindowsPath(pair.Key, root, pair.Value, budget, token, null);
        Guard(token, validateContext);
    }

    private static void RequirePreparedWindowsChain(string path, string root,
        IReadOnlyDictionary<string, FolderCopyWindowsMetadata> state, CopyBudget budget,
        CancellationToken token, Action? validateContext = null)
    {
        Guard(token, validateContext);
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (state.TryGetValue(current, out var metadata))
                RequirePreparedWindowsPath(current, root, metadata, budget, token, null);
        Guard(token, validateContext);
    }

    private static void RequirePreparedWindowsPath(string path, string root, FolderCopyWindowsMetadata metadata,
        CopyBudget budget, CancellationToken token, Action? validateContext)
    {
        Guard(token, validateContext);
        // UIの世代照合は有界な単件観測の前後で行う。Native内部の各照合はtokenを維持し、
        // 全祖先のqueryごとにUIへ同期dispatchしてコピーを滞留させない。
        if (SameOrInside(path, root))
            FolderCopyWindowsMetadata.RequireUnchanged(path, metadata, budget, token);
        else
        {
            // 比較root外の祖先では、無関係な兄弟の作成で変わるmtimeを追跡しない。
            // identity・creation・属性・security・EAは原観測値との照合を続ける。
            var current = ReadSnapshot(path) ?? throw new IOException("Windows metadataの祖先が不在になりました。");
            if (current.Kind != DirectoryEntryKind.Directory) throw new IOException("Windows metadataの祖先がdirectoryではありません。");
            FolderCopyWindowsMetadata.RequireUnchanged(path, metadata,
                checked((ulong)current.LastWriteTimeUtc.ToFileTimeUtc()), budget, token);
        }
        Guard(token, validateContext);
    }
}
