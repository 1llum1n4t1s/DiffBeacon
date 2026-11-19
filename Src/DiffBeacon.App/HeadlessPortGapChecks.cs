namespace DiffBeacon.App;

/// <summary>
/// シェル登録、翻訳カタログ、外部 OCR、プラグインの説明が、この OS で確かめられる範囲。
/// </summary>
internal static class HeadlessPortGapChecks
{
    /// <summary>ウィンドウを開かずに終了コードを返す。</summary>
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var failed = 0;
        void Check(string name, bool passed, string detail)
        {
            if (!passed) failed++;
            Console.WriteLine((passed ? "ok " : "FAIL ") + name + " " + detail);
        }
        var executable = OperatingSystem.IsWindows() ? @"C:\Tools\DiffBeacon.exe" : "/opt/DiffBeacon";
        var commands = ShellIntegration.WindowsCommands(executable);
        var expected = "\"" + executable + "\" \"%1\"";
        Check("shell commands quote the executable and the selected path",
            commands.Count == 4 && commands.All(item => item.Key.Contains("DiffBeacon", StringComparison.Ordinal))
            && commands[1].Value == expected && commands[3].Value == commands[1].Value,
            commands[1].Value);
        Check("shell description names this OS", ShellIntegration.Describe().Length > 0, ShellIntegration.Describe());
        var directory = PoCatalog.FindDirectory();
        Check("translation catalogs are available", directory is not null, directory ?? "null");
        if (directory is not null)
        {
            var japanese = PoCatalog.Load(Path.Combine(directory, "Japanese.po"));
            Check("Japanese catalog translates Select Font", japanese.Get("Select &Font...") == "フォントの選択(&F)...", japanese.Get("Select &Font..."));
            Check("Japanese catalog keeps a missing id", japanese.Get("not-a-winmerge-id") == "not-a-winmerge-id", japanese.Count.ToString());
        }
        Check("ocr response reads text", ExternalOcr.ReadResponse("{\"text\":\"hello\"}") == "hello", ExternalOcr.Availability);
        var possessive = DiffBeacon.Core.FileFilter.ParseExpression("name matches 'a++a'");
        var greedy = DiffBeacon.Core.FileFilter.ParseExpression("name matches 'a+a'");
        Check("possessive quantifier does not backtrack", !possessive.Matches("aaa", false, 0, DateTime.UnixEpoch) && greedy.Matches("aaa", false, 0, DateTime.UnixEpoch), "");
        var content = DiffBeacon.Core.FileFilter.ParseExpression("content = 'x'");
        Check("content field parses and is empty without a content scan", !content.Matches("a.txt", false, 1, DateTime.UnixEpoch), "");
        var vision = MacIntegration.TryRecognize("/tmp/diffbeacon-no-such-image", out _, out var visionError);
        Check("vision shim stays quiet when it is not published beside the app", !vision, visionError);
        var rejected = false;
        try { ExternalOcr.ReadResponse("{\"text\":1}"); }
        catch (InvalidDataException) { rejected = true; }
        Check("ocr response rejects a non-string", rejected, "");
        return failed == 0 ? 0 : 2;
    }
}
