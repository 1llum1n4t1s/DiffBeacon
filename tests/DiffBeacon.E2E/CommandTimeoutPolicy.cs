internal static class CommandTimeoutPolicy
{
    // 通常起動とmacOS gate起動で、検証用selectorの制限を一致させる。
    internal static int GetSeconds(string name, IReadOnlyList<string> arguments)
    {
        // macOS x64 CIの実測で120秒を超えたため、画像既定値readerに有限の300秒を使う。
        if (name is "image-defaults-reader") return OperatingSystem.IsMacOS() ? 300 : 120;
        if (name is "folder-copy-large-stream" or "folder-copy-gui") return 180;
        // CI osx-x64の正常完走は入力選択145.6秒＋critical17.4秒。両段階に有限の余裕を持たせる。
        if (arguments.Count > 0 && arguments[0] == "--self-test-independent-text-inputs") return 240;
        return arguments.Count > 0 && arguments[0] is
            "--self-test" or
            "--self-test-independent-text" or
            "--self-test-independent-archive-text" or
            "--self-test-independent-text-input-archives" or
            "--self-test-independent-text-input-cipher" or
            "--self-test-independent-text-input-saved-archives" or
            "--self-test-independent-text-input-routes" or
            "--self-test-independent-text-input-lifetime" ? 120 : 30;
    }
}
