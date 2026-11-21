using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal static class BinarySearchScenarios
{
    internal static async Task RunAsync(string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, string python)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Binary search fixture repository not found.");
        var work = Path.GetFullPath(Path.Combine(fixtures, "binary-search"));
        if (work.StartsWith(repository.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || work.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.None).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Binary search verification requires an external task output.");
        Directory.CreateDirectory(work); var gui = Path.Combine(work, "gui");
        await run("binary-search-real-controls", 0, false, ["--self-test", gui, "--binary-search-only"]);
        var script = Path.Combine(repository.FullName, "tests", "Fixtures", "BinarySearch", "verify.py");
        var receipt = Path.Combine(work, "reader-receipt.json"); var arguments = new[] { "-X", "utf8", "-B", script, "--gui", gui, "--receipt", receipt };
        if (OperatingSystem.IsMacOS())
        {
            // 既存gateがready後の実OS birthを取得し、同PID execをreleaseする。
            var result = await MacCommandLauncher.RunAsync(python, python, "binary-search-reader", null, arguments, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(work, "reader.stdout.raw"), result.Stdout); File.WriteAllText(Path.Combine(work, "reader.stderr.raw"), result.Stderr);
            using var file = File.Create(Path.Combine(work, "reader-process.json")); using var json = new Utf8JsonWriter(file);
            var evidence = result.LaunchEvidence; json.WriteStartObject(); json.WriteNumber("pid", result.Pid); json.WriteString("actualOsBirthTimeUtc", evidence?.ActualOsBirthTimeUtc); json.WriteBoolean("terminal", evidence?.Terminal == true); json.WriteBoolean("stdoutComplete", evidence?.StdoutComplete == true); json.WriteBoolean("stderrComplete", evidence?.StderrComplete == true); json.WriteBoolean("pipesReleased", evidence?.PipesReleased == true); json.WriteBoolean("processDisposed", evidence?.ProcessDisposed == true); json.WriteNumber("exitCode", result.ExitCode); json.WriteEndObject();
            var qualified = result.ExitCode == 0 && evidence is { Terminal: true, StdoutComplete: true, StderrComplete: true, PipesReleased: true, ProcessDisposed: true } && evidence.ActualOsBirthTimeUtc is not null && File.Exists(receipt);
            check("Binary search independent reader", qualified, result.Stdout + result.Stderr);
            if (!qualified) throw new IOException("Independent reader launch/terminal/bytes not qualified."); return;
        }
        var info = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in arguments) info.ArgumentList.Add(value);
        using var process = Process.Start(info) ?? throw new IOException("Binary search independent reader did not start.");
        var pid = process.Id; var handle = process.SafeHandle; DateTime? birth = null; string? birthError = null;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { birth = process.StartTime.ToUniversalTime(); } catch (Exception error) { birthError = error.ToString(); }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); throw; }
        var outText = await stdout; var errorText = await stderr;
        File.WriteAllText(Path.Combine(work, "reader.stdout.raw"), outText); File.WriteAllText(Path.Combine(work, "reader.stderr.raw"), errorText);
        using (var file = File.Create(Path.Combine(work, "reader-process.json"))) using (var json = new Utf8JsonWriter(file))
        { json.WriteStartObject(); json.WriteNumber("pid", pid); if (birth.HasValue) json.WriteString("birthUtc", birth.Value); else json.WriteNull("birthUtc"); json.WriteString("birthError", birthError); json.WriteBoolean("heldUntilExit", !handle.IsInvalid); json.WriteBoolean("exited", process.HasExited); json.WriteNumber("exitCode", process.ExitCode); json.WriteBoolean("stdoutComplete", stdout.IsCompletedSuccessfully); json.WriteBoolean("stderrComplete", stderr.IsCompletedSuccessfully); json.WriteEndObject(); }
        var passed = birth.HasValue && process.ExitCode == 0 && File.Exists(receipt);
        check("Binary search independent literals, control gates and saved bytes", passed, outText + errorText + birthError);
        if (!passed) throw new InvalidOperationException("Independent reader failed or OS birth unavailable; keep original run.");
    }
}
