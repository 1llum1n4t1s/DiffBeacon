using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

internal static class MacCommandLauncher
{
    // 実OS生成時刻をready後に取得し、stdinと異常時cleanupを有限時間で扱う。
    internal static async Task<CommandResult> RunAsync(string app, string python, string name, string? standardInput, string[] arguments, Encoding utf8)
    {
        var nativeExecutable = app.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : app;
        var nativeArguments = app.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? new[] { app }.Concat(arguments).ToArray() : arguments;
        var gatePath = Path.GetFullPath("tests/DiffBeacon.E2E/process_startup_gate.py");
        const string expectedGateSha256 = "760F968A8438F91441ACE6A7F6556B64D3CC54785A3825B9D1D98112A5CD074E";
        string? gateSha256 = null;
        var launchNonce = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false), UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-B", "-X", "utf8", gatePath, launchNonce, nativeExecutable }.Concat(nativeArguments)) start.ArgumentList.Add(argument);
        // 未終端/読込み未完了の場合にusingの暗黙Disposeへ進まない。
        var process = new Process { StartInfo = start };
        var timer = Stopwatch.StartNew();
        var timeoutSeconds = name is "folder-copy-large-stream" or "folder-copy-gui" ? 180
            : arguments.Length > 0 && (arguments[0] is "--self-test" or "--self-test-independent-text" or "--self-test-independent-archive-text") ? 120 : 30;
        const int cleanupGraceSeconds = 5;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var captureCancellation = new CancellationTokenSource();
        var exit = -1; var stderr = ""; var started = false; var gateReleased = false; var wrapperReady = false;
        var terminal = false; var pipesReleased = false; var processDisposed = false;
        var cleanupIssues = new List<string>();
        string? wrapperReadinessLine = null; int? rawExitCode = null;
        var launchUtc = DateTime.UtcNow; var pid = 0;
        DateTime? creationUtc = null; DateTime? exitObservedUtc = null; DateTime? gateReleaseObservedUtc = null; DateTime? wrapperReadyObservedUtc = null;
        Stream? inputPipe = null; Stream? outputPipe = null; Stream? errorPipe = null;
        CommandStreamCapture? stdoutCapture = null; CommandStreamCapture? stderrCapture = null;
        CommandStreamSnapshot? stdoutSnapshot = null; CommandStreamSnapshot? stderrSnapshot = null;
        try
        {
            gateSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(gatePath, timeout.Token)));
            if (!string.Equals(gateSha256, expectedGateSha256, StringComparison.Ordinal)) throw new IOException("E2E startup gate SHA-256 が固定値と一致しません。");
            timeout.Token.ThrowIfCancellationRequested();
            launchUtc = DateTime.UtcNow;
            started = process.Start();
            if (!started) throw new IOException("E2E startup gate を開始できませんでした。");
            // writerには一文字も渡さず、空のcharbufferを維持する。
            inputPipe = process.StandardInput.BaseStream;
            outputPipe = process.StandardOutput.BaseStream; errorPipe = process.StandardError.BaseStream;
            stdoutCapture = new CommandStreamCapture(process.StandardOutput, true, captureCancellation.Token);
            stderrCapture = new CommandStreamCapture(process.StandardError, false, captureCancellation.Token);
            pid = process.Id;
            wrapperReadinessLine = await stdoutCapture.Readiness.WaitAsync(timeout.Token);
            var expectedReady = $"DIFFBEACON_E2E_GATE_READY_V2\t{launchNonce}\t{pid}";
            var startupReadinessSnapshot = stdoutCapture.GetSnapshot();
            if (!startupReadinessSnapshot.ReadinessComplete || !string.Equals(wrapperReadinessLine, expectedReady, StringComparison.Ordinal)) throw new IOException("E2E startup gate readiness がnonce/PIDと一致しません。");
            wrapperReady = true; wrapperReadyObservedUtc = DateTime.UtcNow;
            creationUtc = process.StartTime.ToUniversalTime();
            await inputPipe.WriteAsync(new byte[] { 0 }, timeout.Token);
            await inputPipe.FlushAsync(timeout.Token);
            gateReleased = true; gateReleaseObservedUtc = DateTime.UtcNow;
            if (standardInput is not null) await inputPipe.WriteAsync(utf8.GetBytes(standardInput), timeout.Token);
            // EOFはraw pipeだけを閉じる。TextWriter.Closeの同期flushは使わない。
            inputPipe.Dispose(); inputPipe = null;
            await process.WaitForExitAsync(timeout.Token);
            rawExitCode = process.ExitCode; terminal = true; exitObservedUtc = DateTime.UtcNow;
            await Task.WhenAll(stdoutCapture.Completion, stderrCapture.Completion).WaitAsync(timeout.Token);
            exit = rawExitCode.Value;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            exit = -2;
            stderr += $"検証の制限時間 {timeoutSeconds} 秒を超えました。\n";
        }
        catch (Exception exception) { stderr += exception + "\n"; }
        finally
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(cleanupGraceSeconds));
            if (started && !terminal)
            {
                // 取消された入力の解放より先にKillを試す。Waitも必ず有限graceにする。
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Exception exception) { cleanupIssues.Add("Kill: " + exception); }
                try
                {
                    await process.WaitForExitAsync(grace.Token);
                    rawExitCode = process.ExitCode; terminal = true; exitObservedUtc = DateTime.UtcNow;
                }
                catch (Exception exception) { cleanupIssues.Add("termination not observed: " + exception); }
            }
            try { inputPipe?.Dispose(); inputPipe = null; }
            catch (Exception exception) { cleanupIssues.Add("stdin pipe release: " + exception); }
            var captureTasks = new[] { stdoutCapture?.Completion, stderrCapture?.Completion }.OfType<Task>().ToArray();
            try { await Task.WhenAll(captureTasks).WaitAsync(grace.Token); }
            catch (Exception exception) { cleanupIssues.Add("output capture grace: " + exception); }
            captureCancellation.Cancel();
            // pipeのraw handleを解放し、token無しのWait/ReadToEnd/reader.Closeは呼ばない。
            var releaseFailed = false;
            foreach (var pipe in new[] { outputPipe, errorPipe })
                try { pipe?.Dispose(); } catch (Exception exception) { releaseFailed = true; cleanupIssues.Add("output pipe release: " + exception); }
            pipesReleased = inputPipe is null && !releaseFailed;
            if (!terminal) { rawExitCode = null; exitObservedUtc = null; }
            // 最終本文/ready/完了/errorを同じlockの一回のsnapshotへ固定する。
            stdoutSnapshot = stdoutCapture?.GetSnapshot(); stderrSnapshot = stderrCapture?.GetSnapshot();
            if (stdoutSnapshot is { Complete: false } || stderrSnapshot is { Complete: false }) cleanupIssues.Add("native output is partial");
            if (stdoutSnapshot?.ReadError is { } stdoutError) cleanupIssues.Add("stdout capture: " + stdoutError);
            if (stderrSnapshot?.ReadError is { } stderrError) cleanupIssues.Add("stderr capture: " + stderrError);
            // 終端とcapture task完了、raw pipe解放の確認後だけProcessをDisposeする。
            if ((!started || terminal) && captureTasks.All(task => task.IsCompleted) && pipesReleased)
            {
                try { process.Dispose(); processDisposed = true; }
                catch (Exception exception) { cleanupIssues.Add("process disposal: " + exception); }
            }
            else cleanupIssues.Add("process wrapper retained: termination or capture completion not observed");
            if ((!terminal || cleanupIssues.Count > 0) && exit >= 0) exit = -1;
        }
        timer.Stop();
        var stdout = stdoutSnapshot?.Body ?? "";
        stderr += stderrSnapshot?.Body ?? "";
        if (cleanupIssues.Count > 0) stderr += "\n" + string.Join("\n", cleanupIssues);
        wrapperReadinessLine = stdoutSnapshot?.ReadinessLine;
        var launchEvidence = new CommandLaunchEvidence(app, nativeExecutable, nativeArguments, start.FileName, start.ArgumentList.ToArray(),
            gatePath, gateSha256, gateReleased, gateReleaseObservedUtc, pid, creationUtc?.ToString("O"), exitObservedUtc?.ToString("O"),
            launchNonce, wrapperReadinessLine, wrapperReady, wrapperReadyObservedUtc, rawExitCode,
            terminal, cleanupGraceSeconds, stdoutSnapshot?.Complete ?? false, stderrSnapshot?.Complete ?? false, pipesReleased, processDisposed, cleanupIssues.ToArray(),
            "macos-wrapper-readiness-before-exec; native stdout excludes ready line; OS birth preserved by exec; release observes pipe flush; Terminal/Complete=false means unobserved termination or partial capture");
        return new CommandResult(name, arguments, exit, stdout, stderr, timer.ElapsedMilliseconds, pid, creationUtc, launchUtc, exitObservedUtc, launchEvidence);
    }

    // 完全出力は通常経路で保持し、異常時にも既に読めたprefixを回収する。
    sealed class CommandStreamCapture
    {
        private readonly object sync = new();
        private readonly StringBuilder body = new();
        private readonly StringBuilder readinessPrefix = new();
        private readonly TaskCompletionSource<string?> readiness = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool splitReadiness;
        private bool readyEnded;
        private bool complete;
        private string? readError;

        public CommandStreamCapture(StreamReader reader, bool splitReadiness, CancellationToken token)
        {
            this.splitReadiness = splitReadiness;
            if (!splitReadiness) readiness.TrySetResult(null);
            Completion = CaptureAsync(reader, token);
        }

        public Task Completion { get; }
        public Task<string?> Readiness => readiness.Task;
        public CommandStreamSnapshot GetSnapshot()
        {
            lock (sync)
                return new CommandStreamSnapshot(body.ToString(), readinessPrefix.Length == 0 ? null : readinessPrefix.ToString(), complete, readError, readyEnded);
        }

        private async Task CaptureAsync(StreamReader reader, CancellationToken token)
        {
            var buffer = new char[4096];
            try
            {
                int length;
                while ((length = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
                {
                    lock (sync)
                    {
                        if (splitReadiness && !readyEnded)
                        {
                            var newline = Array.IndexOf(buffer, '\n', 0, length);
                            if (newline < 0) readinessPrefix.Append(buffer, 0, length);
                            else
                            {
                                readinessPrefix.Append(buffer, 0, newline);
                                readyEnded = true;
                                readiness.TrySetResult(readinessPrefix.ToString());
                                body.Append(buffer, newline + 1, length - newline - 1);
                            }
                        }
                        else body.Append(buffer, 0, length);
                    }
                }
                lock (sync) complete = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { lock (sync) readError = exception.ToString(); }
            finally { lock (sync) readiness.TrySetResult(readinessPrefix.Length == 0 ? null : readinessPrefix.ToString()); }
        }
    }

    sealed record CommandStreamSnapshot(string Body, string? ReadinessLine, bool Complete, string? ReadError, bool ReadinessComplete);
}
