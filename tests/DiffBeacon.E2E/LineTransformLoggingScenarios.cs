using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal static class LineTransformLoggingScenarios
{
    private sealed record GoldenEvent(int Level, string Message);
    private sealed record GoldenCase(bool Success, string Result, GoldenEvent[] Events);
    private sealed record Probe(string Id, string Expression, bool ExpectRuntimeFailure = false, bool ExpectCompileFailure = false);

    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, string python)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Line transform logging fixture repository missing.");
        var fixture = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformLogging");
        var directory = Path.Combine(output, "line-transform-logging");
        Directory.CreateDirectory(directory);

        var verifier = await RunReferenceReaderAsync(python, Path.Combine(fixture, "verify.py"), directory);
        check("line-transform-logging-reference-reader",
            verifier.ExitCode == 0 && verifier.Summary.GetProperty("passed").GetBoolean()
                && verifier.Summary.GetProperty("cases").GetInt32() == 35,
            verifier.Stdout + verifier.Stderr);

        var golden = ReadGolden(Path.Combine(fixture, "golden.ndjson"));
        var probes = new Probe[]
        {
            new("level-0-plain", "LogError(\"message\")"),
            new("level-1-named", "@name=filter LogWarn(\"message\")"),
            new("level-2-unicode", "@name=\" 名前 空白 \" LogInfo(\"message\")"),
            new("string-none", "LogInfo(none)"),
            new("string-false", "LogInfo(false)"),
            new("string-true", "LogInfo(true)"),
            new("string-double", "LogInfo(1.25)"),
            new("string-empty", "LogInfo(\"\")"),
            new("string-array", "LogInfo(Array(none,true,false,7,\"雪\",Array(),Array(\"a\",none)))"),
            new("multiple", "LogWarn(\"first\",none,Array(true,\"last\"))"),
            new("duplicate-name", "@name=first @name=\"last name\" LogInfo(\"message\")"),
            new("flags-name", "@ci @noopt @name=sample LogInfo(\"message\")"),
            new("nested-order", "LogInfo(LogError(\"inner-a\"),LogWarn(\"inner-b\"))"),
            new("short-false", "If(false,LogError(1/(LineNumber-LineNumber)),LogInfo(\"chosen\"))"),
            new("short-true", "If(true,LogWarn(\"chosen\"),LogError(1/(LineNumber-LineNumber)))"),
            new("short-none", "If(none,LogError(1/(LineNumber-LineNumber)),LogInfo(\"chosen\"))"),
            new("throw-first", "LogInfo(1/(LineNumber-LineNumber),LogError(\"unreached\"))", true),
            new("throw-after-inner", "@name=before LogInfo(LogError(\"completed\"),1/(LineNumber-LineNumber),LogWarn(\"unreached\"))", true),
            new("nested-throw", "LogInfo(LogError(\"first\"),LogWarn(\"partial\",1/(LineNumber-LineNumber)))", true),
            new("zero-args", "LogError()", ExpectCompileFailure: true),
            new("directive-missing", "@name= LogInfo(\"message\")"),
            new("directive-unclosed", "@name=\"bad LogInfo(\"message\")"),
            new("directive-unknown", "@mystery LogInfo(\"message\")"),
            new("directive-trailing", "@name=\"ok\"tail LogInfo(\"message\")"),
            new("directive-space-around-equals", "@name =x LogInfo(\"message\")")
        };

        var left = Path.Combine(directory, "left.txt");
        var right = Path.Combine(directory, "right.txt");
        foreach (var probe in probes)
        {
            var expected = golden[probe.Id];
            await File.WriteAllTextAsync(left, "source", new UTF8Encoding(false));
            var compileFailure = probe.ExpectCompileFailure || !expected.Success && !probe.ExpectRuntimeFailure;
            var compareEqual = expected.Success && !compileFailure && expected.Result.Length > 0
                && !expected.Result.Contains('\n') && !expected.Result.Contains('\r');
            await File.WriteAllTextAsync(right, compareEqual ? expected.Result : "expected-different", new UTF8Encoding(false));
            var expectedExit = compileFailure ? 2 : expected.Success ? compareEqual ? 0 : 1 : 2;
            var result = await run("line-transform-logging-" + probe.Id, expectedExit, expected.Success && !compileFailure,
                ["--compare", left, right, "--line-transform", "left", probe.Expression]);

            var eventText = string.Concat(expected.Events.Select(item => FormatEvent(item.Level, item.Message)));
            var logsMatch = compileFailure
                ? !HasLogEventPrefix(result.Stderr) && result.Stdout.Length == 0
                : probe.ExpectRuntimeFailure
                    ? (eventText.Length == 0 ? !HasLogEventPrefix(result.Stderr) : result.Stderr.StartsWith(eventText, StringComparison.Ordinal))
                        && result.Stdout.Length == 0
                    : result.Stderr == eventText;
            check(result.Name + " reference events", logsMatch,
                "expected=" + Convert.ToHexString(Encoding.UTF8.GetBytes(eventText))
                + "; actual=" + Convert.ToHexString(Encoding.UTF8.GetBytes(result.Stderr)));

            if (expected.Success && !compileFailure)
            {
                try
                {
                    using var json = JsonDocument.Parse(result.Stdout);
                    check(result.Name + " stdout remains comparison JSON",
                        json.RootElement.GetProperty("different").GetBoolean() == !compareEqual,
                        result.Stdout);
                }
                catch (JsonException exception)
                {
                    check(result.Name + " stdout remains comparison JSON", false, exception.Message);
                }
            }
            else
            {
                check(result.Name + " rejected before comparison JSON", result.ExitCode == 2 && result.Stdout.Length == 0,
                    result.Stderr);
            }
        }

        var routeCase = golden["level-2-named"];
        var routeExpression = "@name=filter LogInfo(\"message\")";
        var routeEvent = string.Concat(routeCase.Events.Select(item => FormatEvent(item.Level, item.Message)));
        var routeLeft = Path.Combine(directory, "route-left.csv");
        var routeRight = Path.Combine(directory, "route-right.csv");
        await File.WriteAllTextAsync(routeLeft, "source\r\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(routeRight, "message\r\n", new UTF8Encoding(false));

        var table = await run("line-transform-logging-route-table", 0, true,
            ["--table", routeLeft, routeRight, "--line-transform", "left", routeExpression]);
        check(table.Name + " stderr sink", table.Stderr == routeEvent, "expected=" + routeEvent + "; actual=" + table.Stderr);
        try
        {
            using var json = JsonDocument.Parse(table.Stdout);
            check(table.Name + " transformed comparison", !json.RootElement.GetProperty("different").GetBoolean(), table.Stdout);
        }
        catch (JsonException exception)
        {
            check(table.Name + " transformed comparison", false, exception.Message);
        }

        var project = Path.Combine(directory, "route-project.json");
        await File.WriteAllBytesAsync(project, JsonSerializer.SerializeToUtf8Bytes(new
        {
            mode = "Table",
            leftPath = routeLeft,
            rightPath = routeRight,
            tableDelimiter = ',',
            lineTransformRules = new[] { new { expression = routeExpression, panes = 1 } }
        }));

        var reportPath = Path.Combine(directory, "route-report.html");
        var report = await run("line-transform-logging-route-report", 0, true, ["--report-project", project, reportPath]);
        check(report.Name + " stderr sink", report.Stderr == routeEvent, "expected=" + routeEvent + "; actual=" + report.Stderr);
        check(report.Name + " report output", File.Exists(reportPath) && new FileInfo(reportPath).Length > 0, reportPath);

        var packagePath = Path.Combine(directory, "route-package.zip");
        var package = await run("line-transform-logging-route-package", 0, true,
            ["--package-project", project, packagePath, "--report"]);
        check(package.Name + " stderr sink", package.Stderr == routeEvent, "expected=" + routeEvent + "; actual=" + package.Stderr);
        check(package.Name + " package output", File.Exists(packagePath) && new FileInfo(packagePath).Length > 0, packagePath);
    }

    private static Dictionary<string, GoldenCase> ReadGolden(string path)
    {
        var result = new Dictionary<string, GoldenCase>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var events = root.GetProperty("logs").EnumerateArray().Select(item => new GoldenEvent(
                item.GetProperty("level").GetInt32(), Encoding.UTF8.GetString(Convert.FromHexString(item.GetProperty("messageHex").GetString()!)))).ToArray();
            result.Add(root.GetProperty("id").GetString()!, new(root.GetProperty("success").GetBoolean(),
                Encoding.UTF8.GetString(Convert.FromHexString(root.GetProperty("resultHex").GetString()!)), events));
        }
        return result;
    }

    private static string FormatEvent(int level, string message)
    {
        var prefix = level switch { 0 => "error", 1 => "warn", 2 => "info", _ => throw new InvalidDataException("Unknown fixed logger level.") };
        return prefix + ": " + message + Environment.NewLine;
    }

    private static bool HasLogEventPrefix(string stderr) => stderr.Split('\n')
        .Select(line => line.TrimEnd('\r'))
        .Any(line => line.StartsWith("error: ", StringComparison.Ordinal)
            || line.StartsWith("warn: ", StringComparison.Ordinal)
            || line.StartsWith("info: ", StringComparison.Ordinal));

    private static async Task<(int ExitCode, string Stdout, string Stderr, JsonElement Summary)> RunReferenceReaderAsync(
        string python, string script, string output)
    {
        var start = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the line-transform logging reference reader.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new TimeoutException("Line-transform logging reference reader exceeded 60 seconds."); }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        await File.WriteAllTextAsync(Path.Combine(output, "reference-reader.stdout.txt"), stdout, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(output, "reference-reader.stderr.txt"), stderr, new UTF8Encoding(false));
        using var document = JsonDocument.Parse(stdout);
        return (process.ExitCode, stdout, stderr, document.RootElement.Clone());
    }
}
