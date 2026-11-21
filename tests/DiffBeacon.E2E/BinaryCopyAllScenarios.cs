using System.Diagnostics;
using System.Text.Json;

internal static class BinaryCopyAllScenarios
{
    internal static async Task RunAsync(string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, string python)
    {
        var work = Path.Combine(fixtures, "binary-copy-all"); Directory.CreateDirectory(work); var gui = Path.Combine(work, "gui");
        await run("binary-copy-all-gui", 0, false, ["--self-test", gui, "--binary-copy-all-only"]);
        using var facts = await Independent("gui", false); if (facts is null) return;
        var workspace = facts.RootElement.GetProperty("workspace").GetString()!; var package = facts.RootElement.GetProperty("package").GetString()!;
        await run("binary-copy-all-project", 0, true, ["--project-copy", workspace, Path.Combine(work, "copied.json")]);
        await run("binary-copy-all-package", 0, true, ["--package-project", workspace, Path.Combine(work, "cli-package.zip")]);
        await run("binary-copy-all-extract", 0, true, ["--archive-extract", package, Path.Combine(work, "extracted")]);
        await run("binary-copy-all-reload", 0, true, ["--project-copy", Path.Combine(work, "extracted", "project.json"), Path.Combine(work, "reloaded.json")]);
        await Independent("cli", true);
        async Task<JsonDocument?> Independent(string name, bool cli)
        {
            var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.GetFullPath("tests/Fixtures/BinaryCopyAll/verify.py"), gui }) info.ArgumentList.Add(argument); if (cli) info.ArgumentList.Add(work);
            using var process = Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); var text = await stdout; var errors = await stderr;
            File.WriteAllText(Path.Combine(work, name + "-independent.stdout.txt"), text); File.WriteAllText(Path.Combine(work, name + "-independent.stderr.txt"), errors);
            check("Binary Copy All independent full bytes " + name, process.ExitCode == 0, text + errors); return process.ExitCode == 0 ? JsonDocument.Parse(text) : null;
        }
    }
}
