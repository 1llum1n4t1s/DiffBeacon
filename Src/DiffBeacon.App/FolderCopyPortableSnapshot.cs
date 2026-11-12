using System.ComponentModel;
using System.Runtime.InteropServices;
using DiffBeacon.Core;

namespace DiffBeacon.App;

// UNC の通常 snapshot 専用。security/EA/stream の保持資格は追加しない。
internal static partial class FolderCopyPortableSnapshot
{
    internal static bool IsUncPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal)
        && !path.StartsWith(@"\\.\", StringComparison.Ordinal)
        && (!path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase));

    internal static FolderCopySnapshot ReadDirectory(string path)
    {
        // READ_ATTRIBUTES だけの open は SMB の古い属性 cache で完結する。
        // READ_EA も要求して server 上の open を行い、同じ handle の基本情報を一度だけ読む。
        // EA 自体は取得しない。権限不足や query 失敗時に cache へ退避しない。
        var handle = OpenDirectory(FolderCopyWindowsStreams.ExtendedPath(path), 0x88, 7, 0, 3, 0x02200000, 0);
        if (handle == -1) throw NativeFailure("CreateFileW(UNC directory)", Marshal.GetLastPInvokeError());
        Exception? primaryFailure = null;
        try
        {
            if (ReadInformation(handle, out var information) == 0)
                throw NativeFailure("GetFileInformationByHandle(UNC directory)", Marshal.GetLastPInvokeError());
            var attributes = (FileAttributes)information.Attributes;
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("UNC directory が通常ディレクトリではありません。");
            var write = ((long)information.WriteHigh << 32) | information.WriteLow;
            if (write < 0 || write > DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc).ToFileTimeUtc())
                throw new IOException("UNC directory の更新日時が不正です。");
            return new(DirectoryEntryKind.Directory, 0, DateTime.FromFileTimeUtc(write), attributes, null);
        }
        catch (Exception exception) { primaryFailure = exception; throw; }
        finally
        {
            if (CloseDirectory(handle) == 0)
            {
                var failure = NativeFailure("CloseHandle(UNC directory)", Marshal.GetLastPInvokeError());
                if (primaryFailure is null) throw failure;
                primaryFailure.Data[FolderCopyWindowsMetadata.CloseFailureDataKey] = failure;
            }
        }
    }

    private static IOException NativeFailure(string operation, int error)
        => new($"{operation}に失敗しました (Win32 error {error})。", new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential)]
    private struct Information
    {
        internal uint Attributes;
        internal uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint OpenDirectory(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    private static partial int ReadInformation(nint handle, out Information information);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    private static partial int CloseDirectory(nint handle);
}
