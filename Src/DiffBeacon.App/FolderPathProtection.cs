using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static class FolderPathProtection
{
    internal static bool SameContainer(string first, string second)
        => ArchivePaths.SameFile(ContainerPath(first), ContainerPath(second));

    internal static string ContainerPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows()) return full;
        // OS用extended表記と通常表記を、保護対象の比較では同じ絶対pathにする。
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            full = @"\\" + full[8..];
        else if (full.StartsWith(@"\\?\", StringComparison.Ordinal) && full.Length >= 7
            && char.IsAsciiLetter(full[4]) && full[5] == ':' && full[6] == '\\')
            full = full[4..];
        // basefileの置換は、そのfileを入力とする全named streamにも影響する。
        var rootLength = Path.GetPathRoot(full)!.Length;
        var streamSeparator = full.IndexOf(':', rootLength);
        return streamSeparator < 0 ? full : full[..streamSeparator];
    }
}
