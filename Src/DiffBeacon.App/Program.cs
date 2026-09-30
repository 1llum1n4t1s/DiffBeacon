using Avalonia;

namespace DiffBeacon.App;

public static class Program
{
    public static string[] Arguments { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Arguments = args;
        if (args.Length == 2 && args[0] == "--self-test") return HeadlessSelfTest.Run(args[1]);
        if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
            return CommandLine.RunAsync(args).GetAwaiter().GetResult();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<BeaconApplication>()
        .UsePlatformDetect().WithInterFont().LogToTrace();
}
