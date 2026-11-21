using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DiffBeacon.App;

internal readonly record struct FolderCopyStreamDefinition(string Name, long Size);

internal sealed class FolderCopyStreamLayout
{
    internal FolderCopyStreamLayout(bool supportsNamedStreams, IEnumerable<FolderCopyStreamDefinition> streams)
    {
        SupportsNamedStreams = supportsNamedStreams;
        Streams = Array.AsReadOnly(streams.ToArray());
    }

    internal bool SupportsNamedStreams { get; }
    internal IReadOnlyList<FolderCopyStreamDefinition> Streams { get; }
}

internal static partial class FolderCopyWindowsStreams
{
    internal const string DefaultStreamName = "::$DATA";
    internal const string CloseFailureDataKey = "FolderCopyWindowsStreams.FindCloseFailure";
    private const int ErrorHandleEof = 38;
    private const int ErrorInvalidParameter = 87;
    private const int NativeNameCharacters = 296;
    private static readonly nint InvalidHandle = -1;

    internal static FolderCopyStreamLayout EnumerateFile(string path, long expectedMainSize,
        int maximumStreams, int maximumNameCharacters, CancellationToken token)
    {
        RequireWindows();
        ArgumentOutOfRangeException.ThrowIfNegative(expectedMainSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumStreams, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumNameCharacters, DefaultStreamName.Length);
        token.ThrowIfCancellationRequested();
        var nativePath = ExtendedPath(path);
        if ((File.GetAttributes(nativePath) & FileAttributes.Directory) != 0)
            throw new IOException("ファイルのストリーム列挙にディレクトリを指定できません。");
        token.ThrowIfCancellationRequested();
        var handle = FindFirstStream(nativePath, 0, out var data, 0);
        if (handle == InvalidHandle)
        {
            var error = Marshal.GetLastPInvokeError();
            token.ThrowIfCancellationRequested();
            // 非対応時の主本文は、呼出し元が固定 metadata と SHA を別に照合する。
            if (error == ErrorInvalidParameter)
                return new(false, new[] { new FolderCopyStreamDefinition(DefaultStreamName, expectedMainSize) });
            if (error == ErrorHandleEof)
                throw new InvalidDataException("ファイルの既定ストリームがありません。");
            throw NativeFailure("FindFirstStreamW", error);
        }

        Exception? failure = null;
        try
        {
            var streams = new List<FolderCopyStreamDefinition>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedNameCharacters = 0;
            var foundDefault = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var name = ReadStreamName(ref data);
                ValidateStreamName(name);
                if (data.StreamSize < 0) throw new InvalidDataException("ストリームの長さが負です。");
                if (streams.Count >= maximumStreams)
                    throw new IOException("ファイルのストリーム数が上限を超えています。");
                // 差分で検査し、加算前に総文字数の overflow と上限超過を防ぐ。
                if (name.Length > maximumNameCharacters - usedNameCharacters)
                    throw new IOException("ファイルのストリーム名の総文字数が上限を超えています。");
                usedNameCharacters += name.Length;
                if (!names.Add(name)) throw new InvalidDataException("大文字小文字を除いて重複するストリーム名があります。");
                if (name == DefaultStreamName)
                {
                    foundDefault = true;
                    if (data.StreamSize != expectedMainSize)
                        throw new IOException("ファイルの既定ストリームの長さが確認値と一致しません。");
                }
                streams.Add(new(name, data.StreamSize));
                token.ThrowIfCancellationRequested();
                if (FindNextStream(handle, out data) != 0) continue;
                var error = Marshal.GetLastPInvokeError();
                if (error != ErrorHandleEof) throw NativeFailure("FindNextStreamW", error);
                token.ThrowIfCancellationRequested();
                break;
            }
            if (!foundDefault) throw new InvalidDataException("ファイルの既定ストリームがありません。");
            return new(true, streams);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            CloseSearch(handle, failure);
        }
    }

    internal static bool SupportsNamedStreams(string existingDirectory, CancellationToken token)
    {
        RequireWindows();
        token.ThrowIfCancellationRequested();
        var nativePath = ExtendedPath(existingDirectory);
        if ((File.GetAttributes(nativePath) & FileAttributes.Directory) == 0)
            throw new IOException("ストリーム対応の確認には既存のディレクトリを指定してください。");
        token.ThrowIfCancellationRequested();
        var handle = FindFirstStream(nativePath, 0, out _, 0);
        if (handle == InvalidHandle)
        {
            var error = Marshal.GetLastPInvokeError();
            token.ThrowIfCancellationRequested();
            // ディレクトリには既定本文がない。EOF は対応済みかつ ADS なし。
            if (error == ErrorHandleEof) return true;
            if (error == ErrorInvalidParameter) return false;
            throw NativeFailure("FindFirstStreamW (directory)", error);
        }

        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            // ディレクトリ ADS は転送対象にしないため、その内容は列挙しない。
            return true;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            CloseSearch(handle, failure);
        }
    }

    internal static string PathForStream(string validatedAbsolutePath, string validatedStreamName)
    {
        RequireWindows();
        ValidateStreamName(validatedStreamName);
        var nativePath = ExtendedPath(validatedAbsolutePath);
        // ADS の colon を相対パス検査へ渡さず、検証済み suffix だけを付ける。
        return validatedStreamName == DefaultStreamName ? nativePath : nativePath + validatedStreamName;
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows のファイルストリーム API は Windows でのみ利用できます。");
    }

    private static void ValidateStreamName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        const string type = ":$DATA";
        if (name.Length < DefaultStreamName.Length || name[0] != ':' || !name.EndsWith(type, StringComparison.Ordinal))
            throw new InvalidDataException("ストリーム名は :name:$DATA 形式でなければなりません。");
        var logicalName = name.AsSpan(1, name.Length - type.Length - 1);
        foreach (var character in logicalName)
            if (character is ':' or '/' or '\\' or '\0')
                throw new InvalidDataException("ストリーム名に区切り文字または NUL が含まれています。");
        // 名前は trim しない。$DATA、末尾空白、Unicode、空本文を保持する。
    }

    private static unsafe string ReadStreamName(ref Win32FindStreamData data)
    {
        fixed (ushort* pointer = data.StreamName)
        {
            var length = 0;
            while (length < NativeNameCharacters && pointer[length] != 0) length++;
            if (length == NativeNameCharacters)
                throw new InvalidDataException("OS が返したストリーム名が終端されていません。");
            return new string((char*)pointer, 0, length);
        }
    }

    internal static string ExtendedPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.IndexOf('\0') >= 0 || path.IndexOf('/') >= 0 || path.IndexOf('*') >= 0)
            throw new ArgumentException("検証済みの Windows 絶対パスを指定してください。", nameof(path));
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("通常のデバイスパスは利用できません。", nameof(path));

        var extended = path.StartsWith(@"\\?\", StringComparison.Ordinal);
        var body = extended ? path[4..] : path;
        if (extended && body.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
        {
            ValidateUnc(body[4..]);
            return path;
        }
        if (!extended && body.StartsWith(@"\\", StringComparison.Ordinal))
        {
            ValidateUnc(body[2..]);
            return @"\\?\UNC\" + body[2..];
        }
        if (body.Length < 3 || !char.IsAsciiLetter(body[0]) || body[1] != ':' || body[2] != '\\'
            || body.AsSpan(2).IndexOf(':') >= 0 || body.AsSpan(2).IndexOf('?') >= 0)
            throw new ArgumentException("ドライブ絶対パスまたは UNC パスを指定してください。", nameof(path));
        return extended ? path : @"\\?\" + body;
    }

    private static void ValidateUnc(string body)
    {
        var separator = body.IndexOf('\\');
        if (separator <= 0 || separator == body.Length - 1 || body[separator + 1] == '\\'
            || body.AsSpan().IndexOf(':') >= 0 || body.AsSpan().IndexOf('?') >= 0
            || body.AsSpan(0, separator).SequenceEqual(".".AsSpan()))
            throw new ArgumentException("サーバー名と共有名を含む UNC 絶対パスを指定してください。");
    }

    private static IOException NativeFailure(string operation, int error) =>
        new($"{operation} に失敗しました (Win32 error {error})。", new Win32Exception(error));

    private static void CloseSearch(nint handle, Exception? primaryFailure)
    {
        // 必ず一度だけ呼び、失敗しても handle の再利用や再 close を行わない。
        if (FindClose(handle) != 0) return;
        var error = Marshal.GetLastPInvokeError();
        var closeFailure = NativeFailure("FindClose", error);
        if (primaryFailure is null) throw closeFailure;
        // 取消を含む一次例外の型を保つ。呼出し元はこのキーを cleanup 診断へ渡す。
        primaryFailure.Data[CloseFailureDataKey] = closeFailure;
    }

    [StructLayout(LayoutKind.Explicit, Size = 600)]
    private unsafe struct Win32FindStreamData
    {
        [FieldOffset(0)] internal long StreamSize;
        [FieldOffset(8)] internal fixed ushort StreamName[NativeNameCharacters];
    }

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstStreamW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint FindFirstStream(string fileName, int infoLevel, out Win32FindStreamData data, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextStreamW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int FindNextStream(nint handle, out Win32FindStreamData data);

    [LibraryImport("kernel32.dll", EntryPoint = "FindClose", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int FindClose(nint handle);
}
