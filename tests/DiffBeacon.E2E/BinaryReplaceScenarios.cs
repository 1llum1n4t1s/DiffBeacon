using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal static class BinaryReplaceScenarios
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal static async Task RunAsync(string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check, string python)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Binary replacement fixture repository not found.");
        var work = Path.GetFullPath(Path.Combine(fixtures, "binary-replace"));
        RequireExternal(work, repository.FullName);
        if (Directory.Exists(work)) throw new IOException("Binary replacement verification requires a fresh output directory.");
        Directory.CreateDirectory(work);
        var gui = Path.Combine(work, "gui");
        var guiResult = await run("binary-replace-real-controls", 0, false, ["--self-test", gui, "--binary-replace-only"]);
        if (guiResult.ExitCode != 0) throw new IOException("Binary replacement real controls did not succeed.");
        var reference = ExactlyOne(gui, "reference-product.ndjson");
        var proofPath = ExactlyOne(gui, "proof.json");
        var proofRoot = Path.GetDirectoryName(proofPath)!;
        RequireWithin(reference, proofRoot);
        var scriptRoot = Path.Combine(repository.FullName, "tests", "Fixtures", "BinaryReplace");

        // 固定readerはreceiptを書かないため、実終了と全51件の実行結果を確認して台帳を作る。
        var referenceResult = await ReaderAsync("reference-reader", ["-X", "utf8", "-B", Path.Combine(scriptRoot, "verify.py"), reference]);
        using (var summary = JsonDocument.Parse(referenceResult.Stdout))
        {
            var root = summary.RootElement;
            var passed = root.GetProperty("success").GetBoolean()
                && root.GetProperty("referenceCases").GetInt32() == 51 && root.GetProperty("productExecuted").GetBoolean();
            check("Binary replacement fixed 51 independent cases", passed, referenceResult.Stdout);
            if (!passed) throw new IOException("Binary replacement fixed reader did not execute all 51 product cases.");
        }
        WriteFresh(Path.Combine(work, "reference-reader-receipt.json"), new
        {
            readerPid = referenceResult.Pid, referenceCases = 51, productExecuted = true,
            artifactConsistencyPassed = true, osTerminalQualified = true, qualified = true,
            reference, processMetadata = "reference-reader-process.json"
        });

        var receipt = Path.Combine(work, "reader-receipt.json");
        await ReaderAsync("gui-reader", ["-X", "utf8", "-B", Path.Combine(scriptRoot, "verify_gui.py"), "--gui", gui, "--receipt", receipt]);
        check("Binary replacement independent GUI reader receipt", File.Exists(receipt), receipt);
        if (!File.Exists(receipt)) throw new IOException("Binary replacement GUI reader did not write its receipt.");
        using (var guiReceipt = JsonDocument.Parse(await File.ReadAllBytesAsync(receipt)))
        {
            var passed = guiReceipt.RootElement.GetProperty("success").GetBoolean()
                && guiReceipt.RootElement.GetProperty("referenceCases").GetInt32() == 51;
            check("Binary replacement GUI receipt full fixed case set", passed, receipt);
            if (!passed) throw new IOException("Binary replacement GUI reader receipt did not pass all fixed cases.");
        }

        using var proof = JsonDocument.Parse(await File.ReadAllBytesAsync(proofPath));
        var projects = proof.RootElement.GetProperty("cliProjects").EnumerateArray().ToArray();
        if (projects.Length == 0) throw new IOException("Binary replacement proof has no CLI projects.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var metadata = new List<object>();
        foreach (var project in projects)
        {
            var id = project.GetProperty("id").GetString() ?? throw new IOException("CLI project ID missing.");
            if (id.Length == 0 || !id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') || !ids.Add(id))
                throw new IOException("CLI project ID is unsafe or duplicated.");
            var relative = project.GetProperty("projectFile").GetString() ?? throw new IOException("CLI project file missing.");
            if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0'))
                throw new IOException("CLI project file must be a physical relative file.");
            var input = Path.GetFullPath(Path.Combine(proofRoot, relative));
            RequireWithin(input, proofRoot); RequireExternal(input, repository.FullName);
            if (!File.Exists(input)) throw new IOException("CLI project file does not exist: " + input);
            var copy = Path.Combine(work, "cli-" + id + "-copy.json");
            var package = Path.Combine(work, "cli-" + id + "-package.zip");
            RequireWithin(copy, work); RequireWithin(package, work);
            var copyResult = await run("binary-replace-" + id + "-project-copy", 0, true, ["--project-copy", input, copy]);
            var packageResult = await run("binary-replace-" + id + "-package-project", 0, true, ["--package-project", input, package]);
            metadata.Add(new { id, kind = project.GetProperty("kind").GetString(), projectFile = input, proofPath,
                sourceMetadata = project.Clone(), copyFile = copy, packageFile = package, copyResult, packageResult });
            // 後発の失敗でも、完了した要求の実行情報を再開可能な形で保持する。
            if (copyResult.ExitCode != 0 || packageResult.ExitCode != 0)
            {
                WriteFresh(Path.Combine(work, "cli-projects.json"), metadata);
                throw new IOException("Binary replacement project copy/package failed: " + id);
            }
        }
        WriteFresh(Path.Combine(work, "cli-projects.json"), metadata);
        var cliReceiptPath = Path.Combine(work, "cli-reader-receipt.json");
        await ReaderAsync("cli-reader", ["-X", "utf8", "-B", Path.Combine(scriptRoot, "verify_cli.py"), "--work", work, "--receipt", cliReceiptPath]);
        using (var cliReceipt = JsonDocument.Parse(await File.ReadAllBytesAsync(cliReceiptPath)))
        {
            var value = cliReceipt.RootElement;
            var passed = value.GetProperty("success").GetBoolean() && value.GetProperty("projects").GetInt32() == 2
                && value.GetProperty("fullBytes").GetBoolean() && value.GetProperty("zipCrc").GetBoolean();
            check("Binary replacement CLI independent full bytes/ZIP CRC", passed, cliReceiptPath);
            if (!passed) throw new IOException("Binary replacement CLI reader did not qualify both fixed projects.");
        }

        async Task<ReaderResult> ReaderAsync(string name, string[] arguments)
        {
            ReaderResult result;
            object evidence;
            if (OperatingSystem.IsMacOS())
            {
                // 固定gateのready/birth/release/EOF/終了証拠を既存launcherから引き継ぐ。
                var launched = await MacCommandLauncher.RunAsync(python, python, "binary-replace-" + name, null, arguments, new UTF8Encoding(false));
                var launch = launched.LaunchEvidence;
                var qualified = launched.ExitCode == 0 && launch is
                    { Terminal: true, StdoutComplete: true, StderrComplete: true, PipesReleased: true, ProcessDisposed: true }
                    && launch.ActualOsBirthTimeUtc is not null;
                evidence = new { launched.Pid, launched.CreationUtc, launched.ExitCode, qualified, launchEvidence = launch };
                result = new(launched.Pid, launched.ExitCode, launched.Stdout, launched.Stderr, qualified);
            }
            else
            {
                var info = new ProcessStartInfo(python)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
                };
                foreach (var argument in arguments) info.ArgumentList.Add(argument);
                var process = Process.Start(info) ?? throw new IOException("Binary replacement independent reader did not start.");
                var pid = process.Id; var handle = process.SafeHandle;
                DateTime? birth = null; string? error = null;
                using var capture = new CancellationTokenSource();
                var stdout = process.StandardOutput.ReadToEndAsync(capture.Token);
                var stderr = process.StandardError.ReadToEndAsync(capture.Token);
                var terminal = false; var timedOut = false; var heldAtTerminal = false; int? exit = null;
                try
                {
                    birth = process.StartTime.ToUniversalTime();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                    terminal = process.HasExited; heldAtTerminal = !handle.IsInvalid && !handle.IsClosed; exit = process.ExitCode;
                    await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception failure)
                {
                    error = failure.ToString(); timedOut = failure is TimeoutException;
                    try
                    {
                        if (!process.HasExited) process.Kill(true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        terminal = process.HasExited; heldAtTerminal = !handle.IsInvalid && !handle.IsClosed; exit = process.ExitCode;
                        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception cleanup) { error += "\n" + cleanup; capture.Cancel(); }
                }
                var stdoutComplete = stdout.IsCompletedSuccessfully; var stderrComplete = stderr.IsCompletedSuccessfully;
                var outText = stdoutComplete ? stdout.Result : ""; var errorText = stderrComplete ? stderr.Result : "";
                var disposed = terminal && stdoutComplete && stderrComplete;
                if (disposed) process.Dispose();
                var qualified = birth.HasValue && terminal && heldAtTerminal && disposed && exit == 0 && error is null;
                evidence = new { pid, birthUtc = birth, exitCode = exit, terminal, processHandleHeldAtTerminal = heldAtTerminal,
                    stdoutComplete, stderrComplete, pipesReleased = disposed, processDisposed = disposed, timedOut, error, qualified };
                result = new(pid, exit ?? -1, outText, errorText + error, qualified);
            }
            WriteFresh(Path.Combine(work, name + "-process.json"), new { executable = python, arguments, evidence });
            WriteFreshText(Path.Combine(work, name + ".stdout.raw"), result.Stdout);
            WriteFreshText(Path.Combine(work, name + ".stderr.raw"), result.Stderr);
            check("Binary replacement " + name + " OS terminal/EOF", result.Qualified, result.Stdout + result.Stderr);
            if (!result.Qualified) throw new IOException("Binary replacement independent reader launch/terminal/EOF failed: " + name);
            return result;
        }
    }

    private sealed record ReaderResult(int Pid, int ExitCode, string Stdout, string Stderr, bool Qualified);

    private static string ExactlyOne(string root, string name)
    {
        if (!Directory.Exists(root)) throw new IOException("Binary replacement GUI output missing.");
        var files = Directory.GetFiles(root, name, SearchOption.AllDirectories);
        if (files.Length != 1) throw new IOException("Binary replacement requires exactly one " + name);
        return Path.GetFullPath(files[0]);
    }

    private static void RequireWithin(string path, string root)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Binary replacement path escapes its physical output root.");
        RequireNoLinks(path);
    }

    private static void RequireExternal(string path, string repository)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var codex = Path.GetFullPath(Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        foreach (var forbidden in new[] { repository, codex, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
            if (full.Equals(Path.GetFullPath(forbidden), comparison) || full.StartsWith(Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
                throw new IOException("Binary replacement verification requires external task output.");
        if (full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Binary replacement output cannot use an artifacts directory.");
        RequireNoLinks(full);
    }

    private static void RequireNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Binary replacement output cannot traverse links: " + current);
    }

    private static void WriteFresh(string path, object value) => WriteFreshText(path, JsonSerializer.Serialize(value, Json));
    private static void WriteFreshText(string path, string value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(value);
    }
}
