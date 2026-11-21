using System.Runtime.InteropServices;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static partial class FolderCopyMacFileKind
{
    internal static void Require(string path, DirectoryEntryKind expected)
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        var result = architecture switch
        {
            Architecture.X64 => LStatX64(path, out var x64State) == 0 ? x64State.Mode : Failure(path),
            Architecture.Arm64 => LStatArm64(path, out var armState) == 0 ? armState.Mode : Failure(path),
            _ => throw new PlatformNotSupportedException("macOSの対応アーキテクチャはx64とARM64です。")
        };
        var kind = result & 0xF000;
        if (kind != (expected == DirectoryEntryKind.Directory ? 0x4000 : 0x8000))
            throw new IOException($"通常ファイルとディレクトリ以外はコピーできません: {path}");
    }

    private static ushort Failure(string path)
        => throw new IOException($"macOSで項目の種別を確認できません (errno {Marshal.GetLastPInvokeError()}): {path}");

    // Darwinの64bit inode版はdev_t(int32)の次にmode_t(uint16)を置く。
    // ABIの残りをC#で再定義せず、SDKのstruct stat全体より大きい固定bufferを渡す。
    [StructLayout(LayoutKind.Explicit, Size = 512)]
    private struct StatBuffer
    {
        [FieldOffset(4)] public ushort Mode;
    }

    [LibraryImport("libSystem.B.dylib", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LStatX64(string path, out StatBuffer result);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LStatArm64(string path, out StatBuffer result);
}
