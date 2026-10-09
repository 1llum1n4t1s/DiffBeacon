using Avalonia;

namespace DiffBeacon.App;

public static class Program
{
    public static string[] Arguments { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (Console.IsInputRedirected) Console.InputEncoding = new System.Text.UTF8Encoding(false, true);
        Arguments = args;
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--bare-compression-only")
            return HeadlessSelfTest.Run(args[1], bareCompressionOnly: true);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--bare-gzip-only")
            return HeadlessSelfTest.Run(args[1], bareGZipOnly: true);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--image-defaults-only")
            return HeadlessSelfTest.Run(args[1], imageDefaultsOnly: true);
        if (args.Length == 2 && args[0] == "--self-test-independent-text-input-saved-archives")
            return HeadlessSelfTest.Run(args[1], independentTextInputSavedArchivesOnly: true);
        if (args.Length == 2 && args[0] == "--self-test-independent-text-input-routes")
            return HeadlessSelfTest.Run(args[1], independentTextInputRoutesOnly: true);
        if (args.Length == 2 && args[0] == "--self-test-independent-text-input-lifetime")
            return HeadlessSelfTest.Run(args[1], independentTextInputLifetimeOnly: true);
        if (args.Length == 2 && args[0] == "--self-test-independent-text-input-cipher")
            return HeadlessSelfTest.Run(args[1], independentTextInputCipherOnly: true);
        if (args.Length == 2 && args[0] == "--self-test-independent-text-input-archives")
            return HeadlessSelfTest.Run(args[1], independentTextInputArchivesOnly: true);
        if (args.Length == 2 && args[0] == "--self-test-independent-text-inputs")
            return HeadlessSelfTest.Run(args[1], independentTextInputsOnly: true);
        if (args.Length == 2 && args[0] == "--self-test-independent-archive-text")
            return HeadlessSelfTest.Run(args[1], independentArchiveTextOnly: true);
        if (args.Length == 2 && args[0] == "--self-test-independent-text")
            return HeadlessSelfTest.Run(args[1], independentTextOnly: true);
        if (args.Length == 5 && args[0] == "--binary-bytecode-self-test") return BinaryBytecodeSelfTest(args);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--binary-search-only")
            return HeadlessSelfTest.Run(args[1], binarySearchOnly: true);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--binary-clipboard-only")
            return HeadlessSelfTest.Run(args[1], binaryClipboardOnly: true);
        if (args.Length == 2 && args[0] == "--self-test") return HeadlessSelfTest.Run(args[1]);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--folder-copy-only")
            return HeadlessSelfTest.Run(args[1], folderCopyOnly: true);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--folder-threeway-only")
            return HeadlessSelfTest.Run(args[1], folderThreeWayOnly: true);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--binary-range-edits-only")
            return HeadlessSelfTest.Run(args[1], binaryRangeEditsOnly: true);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--binary-copy-all-only")
            return HeadlessSelfTest.Run(args[1], binaryCopyAllOnly: true);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--archive-sources-only")
            return HeadlessSelfTest.Run(args[1], archiveSourcesOnly: true);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--archive-working-review-only")
            return HeadlessSelfTest.Run(args[1], archiveWorkingReviewOnly: true);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--binary-working-only")
            return HeadlessSelfTest.Run(args[1], binaryWorkingOnly: true);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--tar-wrapper-gui-only")
            return HeadlessSelfTest.Run(args[1], tarWrapperGuiOnly: true);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--binary-threeway-only")
            return HeadlessSelfTest.Run(args[1], binaryThreeWayOnly: true);
        if (args.Length == 3 && args[0] == "--clipboard-self-test")
            return DesktopClipboardSelfTest.Run(args[1], args[2]);
        if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
            return CommandLine.RunAsync(args).GetAwaiter().GetResult();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<BeaconApplication>()
        .UsePlatformDetect().WithInterFont().LogToTrace();

    private static int BinaryBytecodeSelfTest(string[] args)
    {
        try
        {
            if (args[1] is not ("encode" or "decode" or "decode-oem") || args[2] is not ("little" or "big")) throw new ArgumentException("encode|decode|decode-oem little|big INPUT OUTPUT を指定してください。");
            var input = Path.GetFullPath(args[3]); var output = Path.GetFullPath(args[4]);
            if (DiffBeacon.Providers.ArchivePaths.SameFile(input, output)) throw new IOException("入力を出力へ指定できません。");
            for (var path = input; path is not null; path = Path.GetDirectoryName(path))
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("リンクを入力へ指定できません。");
            var limit = args[1] == "encode" ? DiffBeacon.Core.BinaryEditSession.MaximumFileBytes : DiffBeacon.Core.BinaryBytecode.MaximumTextBytes;
            using var stream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > limit) throw new InvalidDataException("検証入力が上限を超えています。");
            var source = new byte[stream.Length]; stream.ReadExactly(source);
            var bytes = args[1] == "encode" ? System.Text.Encoding.ASCII.GetBytes(DiffBeacon.Core.BinaryBytecode.Encode(source)) : BinaryTextEncoding.DecodeBytecode(source, args[2] == "big", args[1] == "decode-oem");
            // 独立検証用出力も通常保存と同じリンク/readonly保護を使用する。
            using var session = new DiffBeacon.Core.BinaryEditSession(bytes, []);
            DiffBeacon.Core.BinaryFileStore.SaveCopyAsync(output, session.Capture(0)).GetAwaiter().GetResult();
            Console.WriteLine($"{{\"length\":{bytes.Length},\"pid\":{Environment.ProcessId}}}"); return 0;
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or FormatException or UnauthorizedAccessException)
        { Console.Error.WriteLine(error.Message); return 2; }
    }
}
