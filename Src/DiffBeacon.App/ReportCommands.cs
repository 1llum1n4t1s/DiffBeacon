using System.Globalization;

namespace DiffBeacon.App;

internal static class ReportCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        const string usage = "--report-project INPUT_PROJECT OUTPUT_HTML [--entry N] [--left-frame N [--middle-frame N] --right-frame N] [--threshold X]";
        if (args.Length < 3) throw new ArgumentException(usage);
        int? index = null;
        int? leftFrame = null, middleFrame = null, rightFrame = null;
        double? threshold = null;
        ImageOffset? leftOffset = null, middleOffset = null, rightOffset = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var optionIndex = 3; optionIndex < args.Length; optionIndex += 2)
        {
            var option = args[optionIndex];
            if (option is not ("--entry" or "--left-frame" or "--middle-frame" or "--right-frame" or "--threshold"
                or "--left-offset" or "--middle-offset" or "--right-offset") || !seen.Add(option)
                || optionIndex + 1 >= args.Length)
                throw new ArgumentException(usage);
            if (option.EndsWith("-offset", StringComparison.Ordinal))
            {
                var position = ImageOffset.Parse(args[optionIndex + 1]);
                if (option == "--left-offset") leftOffset = position;
                else if (option == "--middle-offset") middleOffset = position;
                else rightOffset = position;
                continue;
            }
            if (option == "--threshold")
            {
                if (!double.TryParse(args[optionIndex + 1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var distance)) throw new ArgumentException("閾値は有限の非負数です。");
                ImageComparisonEngine.ValidateThreshold(distance); threshold = distance; continue;
            }
            if (!int.TryParse(args[optionIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value)) throw new ArgumentException(usage);
            switch (option)
            {
                case "--entry" when value is >= 1 and <= 256: index = value - 1; break;
                case "--left-frame" when value >= 1: leftFrame = value; break;
                case "--right-frame" when value >= 1: rightFrame = value; break;
                case "--middle-frame" when value >= 1: middleFrame = value; break;
                default: throw new ArgumentException("比較番号は1〜256、フレームは1以上です。");
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
            await ProjectReport.ExportAsync(workspace, entry, args[2], args[1], cancellation.Token, leftFrame, rightFrame, threshold, middleFrame,
                leftOffset, middleOffset, rightOffset);
            CommandLine.WriteJson(writer => { writer.WriteString("output", Path.GetFullPath(args[2])); writer.WriteNumber("entry", entry + 1); });
            return 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
