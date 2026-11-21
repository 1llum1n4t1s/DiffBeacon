using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformRegexScenarios
{
    private const string GoldenSha256 = "3e4dbfa2e88eace81f62bc41ae2d8eb58bff6f2a7c98dfd224ec708241a708f7";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, string python)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Regex fixture repository missing.");
        var fixture = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformRegex");
        var directory = Path.GetFullPath(Path.Combine(fixtures, "line-transform-regex"));
        RequireExternal(directory, repository.FullName);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Regex E2E requires fresh task output.");
        Directory.CreateDirectory(directory);

        var reader = await RunReferenceReaderAsync(python, Path.Combine(fixture, "verify.py"), directory);
        check("line-transform-regex-reference-reader", reader.ExitCode == 0
            && reader.Summary.GetProperty("cases").GetInt32() == 432
            && reader.Summary.GetProperty("gitChecked").GetBoolean()
            && reader.Summary.GetProperty("goldenSha256").GetString() == GoldenSha256, reader.Stdout + reader.Stderr);

        var goldenBytes = await File.ReadAllBytesAsync(Path.Combine(fixture, "golden.ndjson"));
        var goldenSha = Convert.ToHexString(SHA256.HashData(goldenBytes)).ToLowerInvariant();
        check("line-transform-regex-golden-sha256", goldenSha == GoldenSha256, goldenSha);
        var golden = File.ReadLines(Path.Combine(fixture, "golden.ndjson"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);
        using var casesDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(fixture, "cases.json")));
        var cases = casesDocument.RootElement.EnumerateArray().ToArray();
        var left = Path.Combine(directory, "left.txt");
        var right = Path.Combine(directory, "right.txt");
        await File.WriteAllTextAsync(left, "probe\r\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(right, "probe\r\n", new UTF8Encoding(false));

        var publicCases = cases.Where(IsPublicCase).ToArray();
        var arityRejections = publicCases.Where(item => !HasPublicArity(item)).ToArray();
        var selected = publicCases.Where(HasPublicArity)
            .Where(item => golden.TryGetValue(item.GetProperty("id").GetString()!, out var expected)
                && IsRenderableValue(expected.GetProperty("value"))).ToArray();
        check("line-transform-regex-public-case-count", publicCases.Length == 358,
            $"public={publicCases.Length}; fixture={cases.Length}; adapter-only inputs are excluded.");
        check("line-transform-regex-golden-case-count", selected.Length == 343,
            $"golden-comparable={selected.Length}; wrong-arity parser cases are checked separately.");
        check("line-transform-regex-public-arity-count", arityRejections.Length == 15,
            $"wrong-arity parser cases={arityRejections.Length}.");
        var replaceCases = publicCases.Count(item => item.GetProperty("function").GetString() == "regexreplace");
        var replaceSelected = selected.Count(item => item.GetProperty("function").GetString() == "regexreplace");
        var replaceArityRejections = arityRejections.Count(item => item.GetProperty("function").GetString() == "regexreplace");
        check("line-transform-regexreplace-public-case-count", replaceCases == 83,
            $"public RegexReplace cases={replaceCases}.");
        check("line-transform-regexreplace-golden-case-count", replaceSelected == 79,
            $"golden-comparable RegexReplace cases={replaceSelected}.");
        check("line-transform-regexreplace-public-arity-count", replaceArityRejections == 4,
            $"wrong-arity RegexReplace parser cases={replaceArityRejections}.");

        var runtimeErrors = 0;
        var observations = new List<object>(publicCases.Length);
        foreach (var item in arityRejections)
        {
            var id = item.GetProperty("id").GetString()!;
            var function = item.GetProperty("function").GetString()!;
            var expression = BuildExpression(item);
            var result = await run("line-transform-regex-" + id, 2, false,
                ["--compare", left, right, "--line-transform", "left", expression]);
            var passed = result.ExitCode == 2 && result.Stdout.Length == 0
                && result.Stderr.Contains(function, StringComparison.Ordinal)
                && result.Stderr.Contains("引数数が不正", StringComparison.Ordinal);
            check(result.Name + " public arity rejection", passed, result.Stderr);
            observations.Add(new { id, expression, expected = "public parser arity rejection", passed,
                exitCode = result.ExitCode, stdoutLength = result.Stdout.Length, stderr = result.Stderr });
        }

        foreach (var item in selected)
        {
            var id = item.GetProperty("id").GetString()!;
            var expected = golden[id].GetProperty("value");
            var function = item.GetProperty("function").GetString()!;
            var arguments = item.GetProperty("args").EnumerateArray().Select(RenderArgument).ToArray();
            var mode = item.GetProperty("mode").GetString();
            var expression = BuildExpression(item);
            var expectedError = expected.ValueKind == JsonValueKind.Null;
            var expectedExpression = expectedError ? "none" : RenderValue(expected);
            var result = await run("line-transform-regex-" + id, expectedError ? 2 : 0, !expectedError,
                ["--compare", left, right,
                    "--line-transform", "left", expression,
                    "--line-transform", "right", expectedExpression]);

            var passed = false;
            if (expectedError)
            {
                runtimeErrors++;
                passed = result.ExitCode == 2 && result.Stdout.Length == 0 && !string.IsNullOrWhiteSpace(result.Stderr);
                if (id == "replace-optimizer-zero-unicode")
                    passed &= result.Stderr.Contains("RegexReplaceの定数評価結果が文字列ではありません。", StringComparison.Ordinal);
                check(result.Name + " runtime rejection", passed, "Expected a bounded evaluation error with no partial comparison JSON.");
            }
            else
            {
                try
                {
                    using var json = JsonDocument.Parse(result.Stdout);
                    passed = result.ExitCode == 0 && !json.RootElement.GetProperty("different").GetBoolean();
                    check(result.Name + " golden value", passed,
                        "expression=" + expression + "; expected=" + expectedExpression + "; actual=" + result.Stdout);
                }
                catch (JsonException exception)
                {
                    check(result.Name + " golden value", false, exception.Message + "; " + result.Stderr);
                }
            }
            observations.Add(new { id, mode, expression, expected = expectedExpression, expectedError, passed,
                exitCode = result.ExitCode, stdoutLength = result.Stdout.Length, stderr = result.Stderr });
        }

        var budgetInput = Path.Combine(directory, "budget-left.txt");
        var budgetRight = Path.Combine(directory, "budget-right.txt");
        await File.WriteAllTextAsync(budgetInput, new string('a', 128), new UTF8Encoding(false));
        await File.WriteAllTextAsync(budgetRight, "probe\r\n", new UTF8Encoding(false));
        var budgetExpression = "@cs @noopt regexreplace(\"" + new string('a', 128)
            + "\", \"a\", \"" + new string('x', 16_384) + "\")";
        var budgetResult = await run("line-transform-regexreplace-output-budget", 2, false,
            ["--compare", budgetInput, budgetRight, "--line-transform", "left", budgetExpression]);
        var budgetPassed = budgetResult.ExitCode == 2 && budgetResult.Stdout.Length == 0
            && budgetResult.Stderr.Contains("上限を超えています", StringComparison.Ordinal);
        check(budgetResult.Name + " output budget rejection", budgetPassed,
            "Expected an output-budget error without partial comparison JSON; " + budgetResult.Stderr);

        await File.WriteAllBytesAsync(Path.Combine(directory, "observations.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            fixtureCases = cases.Length, publicCases = publicCases.Length, goldenComparableCases = selected.Length,
            arityRejections = arityRejections.Length, runtimeErrors,
            outputBudgetProbe = new { expression = budgetExpression, passed = budgetPassed,
                exitCode = budgetResult.ExitCode, stdoutLength = budgetResult.Stdout.Length, stderr = budgetResult.Stderr },
            goldenSha256 = goldenSha, observations
        }, new JsonSerializerOptions { WriteIndented = true }));
        check("line-transform-regex-observation-count", observations.Count == 358 && runtimeErrors == 15
            && arityRejections.Length == 15,
            $"observations={observations.Count}; runtimeErrors={runtimeErrors}; arityRejections={arityRejections.Length}.");
    }

    private static bool IsPublicCase(JsonElement item)
    {
        if (item.GetProperty("nullArgs").GetBoolean()) return false;
        foreach (var argument in item.GetProperty("args").EnumerateArray())
            if (!IsPublicArgument(argument)) return false;
        return true;
    }

    private static bool IsPublicArgument(JsonElement argument)
    {
        var type = argument.GetProperty("type").GetString();
        if (type == "array")
        {
            foreach (var value in argument.GetProperty("value").EnumerateArray())
                if (!IsPublicArgument(value)) return false;
            return true;
        }
        if (type is not ("string" or "integer" or "double" or "bool" or "none")) return false;
        return type != "string" || !argument.GetProperty("value").GetString()!.Contains('\0');
    }

    private static bool HasPublicArity(JsonElement item)
    {
        var count = item.GetProperty("args").GetArrayLength();
        return item.GetProperty("function").GetString() switch
        {
            "regexcount" => count == 2,
            "regexreplace" => count == 3,
            "regexfind" or "regexextract" => count is >= 2 and <= 4,
            _ => false
        };
    }

    private static string BuildExpression(JsonElement item)
    {
        var caseSensitive = !item.TryGetProperty("caseSensitive", out var sensitive) || sensitive.GetBoolean();
        var noOptimize = item.GetProperty("mode").GetString() == "function-body-noopt";
        var arguments = item.GetProperty("args").EnumerateArray().Select(RenderArgument);
        return (caseSensitive ? "@cs " : "@ci ") + (noOptimize ? "@noopt " : "")
            + item.GetProperty("function").GetString() + "(" + string.Join(", ", arguments) + ")";
    }

    private static bool IsRenderableValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return true;
        var kind = value.GetProperty("kind").GetString();
        if (kind == "array")
            return value.GetProperty("items").EnumerateArray().All(IsRenderableValue);
        if (kind == "string")
        {
            try { return !StrictUtf8.GetString(Convert.FromHexString(value.GetProperty("utf8Hex").GetString()!)).Contains('\0'); }
            catch (DecoderFallbackException) { return false; }
        }
        return kind is "integer" or "double" or "bool" or "none";
    }

    private static string RenderArgument(JsonElement argument)
    {
        var type = argument.GetProperty("type").GetString();
        if (type == "array")
            return "Array(" + string.Join(", ", argument.GetProperty("value").EnumerateArray().Select(RenderArgument)) + ")";
        if (type == "string") return Quote(argument.GetProperty("value").GetString()!);
        if (type == "integer")
        {
            var integer = argument.GetProperty("value").GetInt64();
            return integer is < int.MinValue or > int.MaxValue
                ? "ToInt(" + Quote(integer.ToString(CultureInfo.InvariantCulture)) + ")"
                : integer.ToString(CultureInfo.InvariantCulture);
        }
        if (type == "double") return RenderDouble(argument.GetProperty("value").GetDouble());
        if (type == "bool") return argument.GetProperty("value").GetBoolean() ? "true" : "false";
        if (type == "none") return "none";
        throw new InvalidDataException("Regex fixture argument is outside the public expression surface: " + type);
    }

    private static string RenderValue(JsonElement value)
    {
        var kind = value.GetProperty("kind").GetString();
        return kind switch
        {
            "array" => "Array(" + string.Join(", ", value.GetProperty("items").EnumerateArray().Select(RenderValue)) + ")",
            "string" => Quote(StrictUtf8.GetString(Convert.FromHexString(value.GetProperty("utf8Hex").GetString()!))),
            "integer" => value.GetProperty("value").GetInt64().ToString(CultureInfo.InvariantCulture),
            "double" => RenderDouble(value.GetProperty("value").GetDouble()),
            "bool" => value.GetProperty("value").GetBoolean() ? "true" : "false",
            "none" => "none",
            _ => throw new InvalidDataException("Regex fixture result is outside the expression value surface: " + kind)
        };
    }

    private static string RenderDouble(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') || text.Contains('e') ? text : text + ".0";
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task<(int ExitCode, string Stdout, string Stderr, JsonElement Summary)> RunReferenceReaderAsync(
        string python, string script, string output)
    {
        var start = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--verify");
        start.ArgumentList.Add("--git");
        using var process = Process.Start(start) ?? throw new IOException("Could not start the regex fixture reader.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Regex fixture reader exceeded 60 seconds.");
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        await File.WriteAllTextAsync(Path.Combine(output, "reference-reader.stdout.txt"), stdout, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(output, "reference-reader.stderr.txt"), stderr, new UTF8Encoding(false));
        using var document = JsonDocument.Parse(stdout);
        return (process.ExitCode, stdout, stderr, document.RootElement.Clone());
    }

    private static void RequireExternal(string path, string repository)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { repository, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Environment.GetEnvironmentVariable("CODEX_HOME") })
        {
            if (string.IsNullOrWhiteSpace(forbidden)) continue;
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new IOException("Regex E2E requires external task output.");
        }
        for (var parent = new DirectoryInfo(full); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Regex E2E output cannot traverse linked directories.");
    }
}
