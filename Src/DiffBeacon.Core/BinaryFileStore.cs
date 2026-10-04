namespace DiffBeacon.Core;

public static class BinaryFileStore
{
    public static async Task SaveCopyAsync(string path, BinaryCapture capture, CancellationToken token = default,
        Func<string, string, CancellationToken, Task>? publish = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Length > BinaryEditSession.MaximumFileBytes) throw new InvalidDataException("バイナリ保存の上限は各16 MiBです。");
        var absolute = Path.GetFullPath(path); ValidateOutput(absolute);
        var exists = File.Exists(absolute); var attributes = exists ? File.GetAttributes(absolute) : FileAttributes.Normal;
        UnixFileMode? mode = exists && !OperatingSystem.IsWindows() ? File.GetUnixFileMode(absolute) : null;
        var temporary = Path.Combine(Path.GetDirectoryName(absolute)!, ".diffbeacon-binary-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            token.ThrowIfCancellationRequested();
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await file.WriteAsync(capture.Bytes, token); await file.FlushAsync(token); file.Flush(true); }
            if (mode.HasValue && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, mode.Value);
            if (exists && OperatingSystem.IsWindows())
            { var preserved = attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed); File.SetAttributes(temporary, preserved == 0 ? FileAttributes.Normal : preserved); }
            if (publish is null) Publish(temporary, absolute, token);
            else await publish(temporary, absolute, token);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void Publish(string temporary, string absolute, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); ValidateOutput(absolute); File.Move(temporary, absolute, true);
    }
    public static void ValidateOutput(string path)
    {
        var absolute = Path.GetFullPath(path);
        for (var current = absolute; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if (current == absolute && (attributes & FileAttributes.Directory) != 0) throw new IOException("ディレクトリを保存先に指定できません。");
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("リンクを経由して保存できません。");
                if (current == absolute && (attributes & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("読取り専用ファイルへ保存できません。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
