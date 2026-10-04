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
        if (args.Length == 2 && args[0] == "--self-test") return HeadlessSelfTest.Run(args[1]);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--archive-sources-only")
            return HeadlessSelfTest.Run(args[1], archiveSourcesOnly: true);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--archive-working-review-only")
            return HeadlessSelfTest.Run(args[1], archiveWorkingReviewOnly: true);
        if (args.Length == 3 && args[0] == "--self-test" && args[2] == "--binary-working-only")
            return HeadlessSelfTest.Run(args[1], binaryWorkingOnly: true);
        if (args.Length == 3 && args[0] == "--clipboard-self-test")
            return DesktopClipboardSelfTest.Run(args[1], args[2]);
        if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
            return CommandLine.RunAsync(args).GetAwaiter().GetResult();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<BeaconApplication>()
        .UsePlatformDetect().WithInterFont().LogToTrace();
}
