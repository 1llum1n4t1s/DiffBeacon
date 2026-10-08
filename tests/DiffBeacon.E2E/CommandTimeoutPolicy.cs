internal static class CommandTimeoutPolicy
{
    // 通常起動とmacOS gate起動で、検証用selectorの制限を一致させる。
    internal static int GetSeconds(string name, IReadOnlyList<string> arguments)
    {
        if (name is "folder-copy-large-stream" or "folder-copy-gui") return 180;
        return arguments.Count > 0 && arguments[0] is
            "--self-test" or
            "--self-test-independent-text" or
            "--self-test-independent-archive-text" or
            "--self-test-independent-text-inputs" or
            "--self-test-independent-text-input-archives" or
            "--self-test-independent-text-input-cipher" or
            "--self-test-independent-text-input-saved-archives" or
            "--self-test-independent-text-input-routes" or
            "--self-test-independent-text-input-lifetime" ? 120 : 30;
    }
}
