namespace DiffBeacon.App;

/// <summary>
/// シェルからの起動。Windows はエクスプローラーの右クリック（HKCU）。macOS はアプリバンドルの NSServices。
/// WinMerge のシェル拡張 DLL（COM）は、Native AOT の単一実行ファイルにも macOS にも載らない。
/// </summary>
internal static class ShellIntegration
{
    /// <summary>エクスプローラーのコマンドが書くレジストリ値。書き込みは Windows でだけ行う。</summary>
    internal static IReadOnlyList<(string Key, string Value)> WindowsCommands(string executable)
    {
        if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("シェル登録には実行ファイルの絶対パスが必要です。", nameof(executable));
        var command = "\"" + executable + "\" \"%1\"";
        return
        [
            (@"HKEY_CURRENT_USER\Software\Classes\*\shell\DiffBeacon", "DiffBeacon で比較"),
            (@"HKEY_CURRENT_USER\Software\Classes\*\shell\DiffBeacon\command", command),
            (@"HKEY_CURRENT_USER\Software\Classes\Directory\shell\DiffBeacon", "DiffBeacon で比較"),
            (@"HKEY_CURRENT_USER\Software\Classes\Directory\shell\DiffBeacon\command", command),
        ];
    }

    /// <summary>この OS でシェル登録が何をするかの説明。</summary>
    internal static string Describe() => OperatingSystem.IsWindows()
        ? "HKCU のファイルとディレクトリの右クリックに、この実行ファイルを登録します。"
        : OperatingSystem.IsMacOS()
            ? "DiffBeacon.app の Info.plist に NSServices を宣言します。サービス要求を Avalonia が受け取らないため、選んだパスを引数にして実行する操作が実働の代替です。"
            : "この OS のシェルメニューは未対応です。実行ファイルに比較するパスを渡してください。";

    /// <summary>現在のユーザーの右クリックを登録する。Windows 以外では拒否する。</summary>
    internal static void RegisterCurrentUser(string executable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(Describe());
        foreach (var (key, value) in WindowsCommands(executable))
            Microsoft.Win32.Registry.SetValue(key, "", value);
    }
}
