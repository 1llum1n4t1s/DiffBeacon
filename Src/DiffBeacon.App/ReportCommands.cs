using System.Globalization;

namespace DiffBeacon.App;

internal static class ReportCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        const string usage = "--report-project INPUT_PROJECT OUTPUT_HTML [--entry N] [--left-frame N --right-frame N] [--threshold N]";
        if (args.Length < 3) throw new ArgumentException(usage);
        int? index = null;
        int? leftFrame = null, rightFrame = null, threshold = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var optionIndex = 3; optionIndex < args.Length; optionIndex += 2)
        {
            var option = args[optionIndex];
            if (option is not ("--entry" or "--left-frame" or "--right-frame" or "--threshold") || !seen.Add(option)
                || optionIndex + 1 >= args.Length
                || !int.TryParse(args[optionIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                throw new ArgumentException(usage);
            switch (option)
            {
                case "--entry" when value is >= 1 and <= 256: index = value - 1; break;
                case "--left-frame" when value >= 1: leftFrame = value; break;
                case "--right-frame" when value >= 1: rightFrame = value; break;
                case "--threshold" when value is >= 0 and <= 255: threshold = value; break;
                default: throw new ArgumentException("比較番号は1〜256、フレームは1以上、閾値は0〜255です。");
            }
        }
        if (leftFrame.HasValue != rightFrame.HasValue) throw new ArgumentException("--left-frame と --right-frame は両方指定してください。");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, ev) => { ev.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var workspace = await WorkspaceStore.LoadWorkspaceAsync(args[1], cancellation.Token);
            var entry = index ?? workspace.ActiveEntryIndex;
            await ProjectReport.ExportAsync(workspace, entry, args[2], args[1], cancellation.Token, leftFrame, rightFrame, threshold);
            CommandLine.WriteJson(writer => { writer.WriteString("output", Path.GetFullPath(args[2])); writer.WriteNumber("entry", entry + 1); });
            return 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
