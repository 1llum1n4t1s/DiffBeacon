using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class IndependentTextInputSelectionArchiveScenarios
{
    internal static async Task RunAsync(string fixtures, string output, string python,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var work = Path.Combine(fixtures, "independent-text-input-selection-archives");
        if (Directory.Exists(work)) throw new IOException("独立入力選択は新しいrunを使用してください。");
        Directory.CreateDirectory(work);
        var gui = Path.Combine(work, "gui");
        // 親のRunを使うため実appは別process、macOSは既存MacCommandLauncher/startup gateのまま。
        var result = await run("independent-text-input-selection-archives-gui", 0, false,
            ["--self-test-independent-text-input-archives", gui]);
        check("Input selection actual process exits and captures both streams", result.ExitCode == 0
            && result.Pid > 0 && result.CreationUtc is not null && result.ExitObservedUtc is not null
            && (!OperatingSystem.IsMacOS() || result.LaunchEvidence is not null), result.Stderr);
        var source = Path.GetFullPath("tests/Fixtures/IndependentTextInputSelectionArchives");
        var readerPath = Path.Combine(source, "verify.py");
        var proof = Path.Combine(work, "reader-receipt.json");
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-B", "-X", "utf8", readerPath, "--fixture", source,
            "--repo", Path.GetFullPath("."), "--gui-report", Path.Combine(gui, "ui-report.json"), "--output", proof }) start.ArgumentList.Add(argument);
        var launch = DateTime.UtcNow;
        using var process = Process.Start(start) ?? throw new IOException("新独立readerを起動できません。");
        var creation = process.StartTime.ToUniversalTime();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var timedOut = false;
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            timedOut = true;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        var stdout = await stdoutTask; var stderr = await stderrTask;
        await File.WriteAllTextAsync(Path.Combine(work, "reader.stdout.json"), stdout, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(work, "reader.stderr.txt"), stderr, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(work, "reader-process.json"), JsonSerializer.Serialize(new
        {
            process.Id, creationUtc = creation, launchUtc = launch, exitObservedUtc = DateTime.UtcNow, actualExit = process.ExitCode, timedOut,
            waitCompleted = true, stdoutComplete = true, stderrComplete = true,
            readerSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(readerPath))), arguments = start.ArgumentList.ToArray(),
            scope = "input301-archive-browser-boundaries; all original pins unchanged; coverage.md remains required"
        }));
        check("New input selection independent literal/ZIP/route/state/PNG reader", !timedOut && process.ExitCode == 0 && File.Exists(proof), stdout + stderr);
    }
}
