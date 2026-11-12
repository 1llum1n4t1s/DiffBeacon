using System.Reflection;

namespace DiffBeacon.Development;

internal static class DevelopmentOutputGuard
{
    internal static string? WorkRoot => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "DiffBeaconDevelopmentWorkRoot")?.Value;

    internal static void Validate(string output)
    {
        var root = WorkRoot;
        if (string.IsNullOrEmpty(root)) return;
        Check(output, root);
        foreach (var name in new[] { "TEMP", "TMP" })
            Check(Environment.GetEnvironmentVariable(name) ?? throw new IOException($"{name} が未設定です。build/Dev.ps1 を使用してください。"), root);
    }

    private static void Check(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Directory.Exists(Path.GetPathRoot(root)) || !full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison))
            throw new IOException($"開発検証の出力は {root} 配下に限定します: {full}");
        for (var directory = new DirectoryInfo(full); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException($"開発検証出力にリンクは使用できません: {directory.FullName}");
    }
}
