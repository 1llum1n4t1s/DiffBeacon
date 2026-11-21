internal static class FixtureRepository
{
    internal static DirectoryInfo? FindRoot()
    {
        // 外部出力の検証DLLでも、作業ディレクトリから原本fixtureを解決する。
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (DirectoryInfo? directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "DiffBeacon.slnx")))
                    return directory;
            }
        }

        return null;
    }
}
