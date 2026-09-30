using System.Text;

namespace DiffBeacon.Providers;

public static class ArchivePaths
{
    public static bool SameFile(string first, string second)
    {
        var a = Path.GetFullPath(first); var b = Path.GetFullPath(second);
        if (a.Equals(b, StringComparison.Ordinal)) return true;
        if (OperatingSystem.IsWindows()) return a.Equals(b, StringComparison.OrdinalIgnoreCase);
        if (!OperatingSystem.IsMacOS()) return false;
        if (!a.Normalize(NormalizationForm.FormD).Equals(b.Normalize(NormalizationForm.FormD), StringComparison.OrdinalIgnoreCase)) return false;
        // 既存ディレクトリ項目の表記を使い、APFS の大小文字・Unicode別表記を解決する。
        // case-sensitive APFS の別ファイルは別名のまま保持する。
        if (!(File.Exists(a) || Directory.Exists(a)) || !(File.Exists(b) || Directory.Exists(b))) return false;
        return ExistingPath(a).Equals(ExistingPath(b), StringComparison.Ordinal);
    }
    private static string ExistingPath(string full)
    {
        var current = Path.GetPathRoot(full)!;
        foreach (var component in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var entries = Directory.EnumerateFileSystemEntries(current).ToArray();
            var match = entries.FirstOrDefault(entry => Path.GetFileName(entry).Equals(component, StringComparison.Ordinal))
                ?? entries.SingleOrDefault(entry => Path.GetFileName(entry).Normalize(NormalizationForm.FormD)
                    .Equals(component.Normalize(NormalizationForm.FormD), StringComparison.OrdinalIgnoreCase));
            current = match ?? throw new IOException("既存ファイルのパスを確認できません。");
        }
        return current;
    }
}
