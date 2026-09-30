using System.Globalization;

namespace DiffBeacon.App;

internal static class PackageCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("--package-project INPUT_PROJECT OUTPUT_ARCHIVE [--entries 1,3] [--report] [--patch] [--no-documents] [--no-project]");
        var report = false; var patch = false; var documents = true; var project = true;
        int[]? indices = null; var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 3; i < args.Length; i++)
        {
            if (!flags.Add(args[i])) throw new ArgumentException("包装オプションを重複して指定できません。");
            switch (args[i])
            {
                case "--report": report = true; break;
                case "--patch": patch = true; break;
                case "--no-documents": documents = false; break;
                case "--no-project": project = false; break;
                case "--entries":
                    if (++i == args.Length) throw new ArgumentException("比較の番号を1,3のように指定してください。");
                    indices = args[i].Split(',').Select(value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                        && index is >= 1 and <= WorkspaceStore.MaxEntries ? index - 1 : throw new ArgumentException("比較の番号は1～256です。")).ToArray();
                    break;
                default: throw new ArgumentException("不明な包装オプションです。");
            }
        }
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var workspace = await WorkspaceStore.LoadWorkspaceAsync(args[1], cancel.Token);
            await ComparisonPackage.CreateAsync(workspace, args[2], new(documents, report, patch, project), indices, cancel.Token, args[1]);
            CommandLine.WriteJson(writer => { writer.WriteString("output", Path.GetFullPath(args[2])); writer.WriteNumber("entries", indices?.Length ?? workspace.Entries.Length); });
            return 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
