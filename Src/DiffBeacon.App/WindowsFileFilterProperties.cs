using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DiffBeacon.Core;

namespace DiffBeacon.App;

// IPropertyStore は生の COM vtable を使い、Native AOT の動的 COM marshalling に依存しない。
internal sealed unsafe partial class WindowsFileFilterProperties : IFileFilterPropertyService
{
    internal static readonly WindowsFileFilterProperties Instance = new();
    internal Action<CancellationToken>? HashChunkReadForVerification { get; set; }
    internal Action<string, IReadOnlyList<string>>? ReadRequestedForVerification { get; set; }
    private static readonly Guid StoreId = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    public void ValidateNames(IReadOnlyList<string> canonicalNames)
    {
        RequireWindows();
        foreach (var name in canonicalNames)
            if (!IsHash(name) && PSGetPropertyKeyFromName(name, out _) < 0)
                throw new FormatException($"未対応の canonical property 名 '{name}' です。");
    }

    public bool Read(string path, IReadOnlyList<string> canonicalNames, CancellationToken token,
        out IReadOnlyList<FileFilterPropertyValue> values)
    {
        RequireWindows();
        token.ThrowIfCancellationRequested();
        ReadRequestedForVerification?.Invoke(path, canonicalNames);
        var result = canonicalNames.Select(_ => FileFilterPropertyValue.None).ToArray();
        values = result;
        // Shell store を開く前にも全祖先を検査し、link の property を問い合わせない。
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("リンクを通る property の取得はできません。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        var initialized = CoInitializeEx(0, 0);
        nint store = 0;
        try
        {
            var id = StoreId;
            if (canonicalNames.Any(name => !IsHash(name)))
                SHGetPropertyStoreFromParsingName(path, 0, 0, in id, out store);
            for (var index = 0; index < canonicalNames.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var name = canonicalNames[index];
                if (IsHash(name)) { result[index] = Hash(path, name, token); continue; }
                if (store == 0 || PSGetPropertyKeyFromName(name, out var key) < 0) continue;
                var variant = new PropertyVariant();
                try
                {
                    var table = *(nint**)store;
                    ((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropertyVariant*, int>)table[5])(store, &key, &variant);
                    result[index] = ConvertVariant(variant);
                }
                finally { PropVariantClear(ref variant); }
            }
            return store != 0;
        }
        finally
        {
            if (store != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)store)[2])(store);
            if (initialized >= 0) CoUninitialize();
        }
    }

    private FileFilterPropertyValue Hash(string path, string name, CancellationToken token)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var hash = IncrementalHash.CreateHash(name switch
            { "Hash.MD5" => HashAlgorithmName.MD5, "Hash.SHA1" => HashAlgorithmName.SHA1, _ => HashAlgorithmName.SHA256 });
            var buffer = new byte[65_536];
            int count;
            var first = true;
            while ((count = stream.Read(buffer)) != 0)
            {
                if (first) { first = false; HashChunkReadForVerification?.Invoke(token); }
                token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count);
            }
            return new(FileFilterPropertyKind.String, Text: Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(FileFilterPropertyKind.String, Text: ""); }
    }

    internal static FileFilterPropertyValue ConvertVariant(PropertyVariant value) => value.Type switch
    {
        3 => new(FileFilterPropertyKind.Integer, Integer: unchecked((int)value.Integer)),
        19 => new(FileFilterPropertyKind.Integer, Integer: unchecked((uint)value.Integer)),
        20 or 21 => new(FileFilterPropertyKind.Integer, Integer: value.Integer),
        5 => new(FileFilterPropertyKind.Double, Double: value.Double),
        31 => new(FileFilterPropertyKind.String, Text: Marshal.PtrToStringUni(value.Pointer) ?? ""),
        11 => new(FileFilterPropertyKind.Boolean, Boolean: unchecked((short)value.Integer) != 0),
        64 => new(FileFilterPropertyKind.Timestamp, Integer: unchecked((long)((unchecked((ulong)value.Integer) - 116444736000000000UL) / 10))),
        0x101f => new(FileFilterPropertyKind.Array, Items: ReadStrings(value)),
        0x1011 => new(FileFilterPropertyKind.String, Text: Convert.ToHexStringLower(new ReadOnlySpan<byte>((void*)value.Vector, checked((int)value.Count)))),
        _ => FileFilterPropertyValue.None
    };

    private static FileFilterPropertyValue[] ReadStrings(PropertyVariant value)
    {
        var result = new FileFilterPropertyValue[checked((int)value.Count)];
        for (var index = 0; index < result.Length; index++)
            result[index] = new(FileFilterPropertyKind.String, Text: Marshal.PtrToStringUni(((nint*)value.Vector)[index]) ?? "");
        return result;
    }

    private static bool IsHash(string name) => name is "Hash.MD5" or "Hash.SHA1" or "Hash.SHA256";
    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Prop / LeftProp / MiddleProp / RightProp は Windows 専用です。");
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { internal Guid Format; internal uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropertyVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal long Integer;
        [FieldOffset(8)] internal double Double;
        [FieldOffset(8)] internal nint Pointer;
        [FieldOffset(8)] internal uint Count;
        [FieldOffset(16)] internal nint Vector;
    }
    [LibraryImport("propsys.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int PSGetPropertyKeyFromName(string name, out PropertyKey key);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int SHGetPropertyStoreFromParsingName(string path, nint context, uint flags, in Guid id, out nint store);
    [LibraryImport("ole32.dll")] private static partial int PropVariantClear(ref PropertyVariant variant);
    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
}
