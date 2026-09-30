using System.Globalization;

namespace DiffBeacon.App;

internal static class ReportCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("--report-project INPUT_PROJECT OUTPUT_HTML [--entry N]");
        int? index = null;
        if (args.Length != 3)
        {
            if (args.Length != 5 || args[3] != "--entry"
                || !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 1 or > 256)
                throw new ArgumentException("--report-project INPUT_PROJECT OUTPUT_HTML [--entry N] （Nは1〜256）");
            index = value - 1;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, ev) => { ev.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var workspace = await WorkspaceStore.LoadWorkspaceAsync(args[1], cancellation.Token);
            var entry = index ?? workspace.ActiveEntryIndex;
            await ProjectReport.ExportAsync(workspace, entry, args[2], args[1], cancellation.Token);
            CommandLine.WriteJson(writer => { writer.WriteString("output", Path.GetFullPath(args[2])); writer.WriteNumber("entry", entry + 1); });
            return 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
