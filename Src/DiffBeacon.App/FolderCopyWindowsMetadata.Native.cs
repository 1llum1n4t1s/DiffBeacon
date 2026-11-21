using System.Runtime.InteropServices;

namespace DiffBeacon.App;

internal sealed unsafe partial class FolderCopyWindowsMetadata
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint Low, High;
        internal readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        internal uint Attributes;
        internal NativeFileTime Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        internal readonly ulong Size => ((ulong)SizeHigh << 32) | SizeLow;
        internal readonly ulong Index => ((ulong)IndexHigh << 32) | IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIoStatus
    {
        internal nint Status;
        internal nuint Information;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint OpenFile(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint ReadAttributes(string path);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ReadDriveType(string root);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true)]
    private static partial int ReadVolumeInformation(nint handle, ushort* volumeName, uint volumeNameSize,
        out uint serial, out uint maximumComponentLength, out uint flags, ushort* fileSystemName, uint fileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    private static partial int ReadFileInformation(nint handle, out NativeFileInformation information);

    [LibraryImport("advapi32.dll", EntryPoint = "GetKernelObjectSecurity", SetLastError = true)]
    private static partial int ReadSecurity(nint handle, uint information, byte* descriptor,
        uint length, out uint needed);

    [LibraryImport("ntdll.dll", EntryPoint = "NtQueryEaFile")]
    private static partial int ReadExtendedAttributes(nint handle, ref NativeIoStatus status,
        byte* buffer, uint length, byte singleEntry, nint list, uint listLength, nint index, byte restart);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    private static partial int CloseFile(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBasicInformation
    {
        internal long Creation, Access, Write, Change;
        internal uint Attributes;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    private static partial int SetFileInformation(nint handle, int informationClass, byte* buffer, uint length);

    [LibraryImport("ntdll.dll", EntryPoint = "NtSetEaFile")]
    private static partial int SetExtendedAttributes(nint handle, ref NativeIoStatus status, byte* buffer, uint length);

    [LibraryImport("ntdll.dll", EntryPoint = "NtSetSecurityObject")]
    private static partial int SetSecurityObject(nint handle, uint information, byte* descriptor);

    [LibraryImport("advapi32.dll", EntryPoint = "SetSecurityDescriptorControl", SetLastError = true)]
    private static partial int SetDescriptorControl(byte* descriptor, ushort interest, ushort value);
}
