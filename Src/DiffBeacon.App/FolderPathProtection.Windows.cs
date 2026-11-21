using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiffBeacon.App;

internal static partial class FolderPathProtection
{
    // FILE_ID_INFO: ULONGLONG volumeとFILE_ID_128の16 bytes。SDKのFileIdInfoは18。
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct WindowsFileIdentity(ulong VolumeSerialNumber, ulong FileIdLow, ulong FileIdHigh);

    private static WindowsFileIdentity? ReadWindowsIdentity(string path)
    {
        // metadataのみ、既存file/directoryのみ。原本を読まず、共有読書き・削除を妨げない。
        // 長いpathとextended入力由来の末尾dot/spaceを、通常Win32の再解釈から保護する。
        var nativePath = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        using var handle = OpenExistingWindowsPath(nativePath, 0, 7, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 2 or 3) return null;
            throw IdentityFailure(error);
        }
        if (GetWindowsFileId(handle, 18, out var identity, 24) == 0)
            throw IdentityFailure(Marshal.GetLastPInvokeError());
        return identity;
    }

    private static IOException IdentityFailure(int error)
        => new($"出力保護のためのWindows実体IDを確認できません（Win32 error {error}）。", new Win32Exception(error));

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle OpenExistingWindowsPath(string path, uint access, uint share, nint security,
        uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static partial int GetWindowsFileId(SafeFileHandle handle, int informationClass, out WindowsFileIdentity identity, uint size);
}
