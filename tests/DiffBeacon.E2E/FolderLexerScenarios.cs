using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class FolderLexerScenarios
{
    internal static async Task RunAsync(string fixtures, string oracle,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        using var plan = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(oracle, "lexer-plan.json")));
        var cases = plan.RootElement.GetProperty("cases");
        var work = Path.Combine(fixtures, "folder-properties", "lexer");
        Directory.CreateDirectory(work);
        var utf8 = new UTF8Encoding(false, true);
        static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var left = Path.Combine(work, "left"); var right = Path.Combine(work, "right");
        Directory.CreateDirectory(left); Directory.CreateDirectory(right);
        var leftFile = Path.Combine(left, "row.bin"); var rightFile = Path.Combine(right, "row.bin");
        await File.WriteAllBytesAsync(leftFile, new byte[] { 1 });
        await File.WriteAllBytesAsync(rightFile, new byte[] { 2 });
        var sentinel = Path.Combine(work, "existing-output.sentinel");
        await File.WriteAllTextAsync(sentinel, "lexer existing output remains unchanged\n", utf8);
        var observations = new List<object>();
        var identities = new HashSet<(int, DateTime)>();
        foreach (var item in cases.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var expression = item.GetProperty("expression").GetString()!;
            var rejected = item.GetProperty("rejected").GetBoolean();
            var selected = item.GetProperty("selected").GetBoolean();
            var filter = Path.Combine(work, id + ".flt");
            var rule = item.TryGetProperty("rule", out var ruleValue) ? ruleValue.GetString() : "fe";
            if (rule is not ("fe" or "f")) throw new InvalidDataException("未対応の字句検証規則です。");
            var filterBytes = utf8.GetBytes("def: exclude\n" + rule + ": " + expression + "\n");
            await File.WriteAllBytesAsync(filter, filterBytes);
            var protectedPaths = new[] { leftFile, rightFile, filter, sentinel };
            var before = protectedPaths.ToDictionary(path => path, Hash, StringComparer.Ordinal);
            string[] arguments = ["--directory", left, right, "--filter", filter, "--show-filtered"];
            var expectedExit = rejected ? 2 : selected ? 1 : 0;
            // 既存run delegateが実App別processと実終了・出力回収を担う。
            var result = await run("folder-lexer-" + id, expectedExit, !rejected, arguments);
            var after = protectedPaths.ToDictionary(path => path, Hash, StringComparer.Ordinal);
            var stdout = Path.Combine(work, id + ".stdout"); var stderr = Path.Combine(work, id + ".stderr");
            await File.WriteAllTextAsync(stdout, result.Stdout, utf8);
            await File.WriteAllTextAsync(stderr, result.Stderr, utf8);
            check("folder lexer preservation " + id,
                before.All(pair => after[pair.Key] == pair.Value) && File.ReadAllBytes(filter).SequenceEqual(filterBytes), filter);
            check("folder lexer actual exit " + id, result.ExitCode == expectedExit, result.ExitCode.ToString());
            check("folder lexer real distinct process " + id,
                result.Pid > 0 && result.CreationUtc.HasValue && identities.Add((result.Pid, result.CreationUtc.Value)), result.CreationUtc?.ToString("o") ?? "missing OS birth");
            check("folder lexer streams " + id,
                rejected ? result.Stdout.Length == 0 && result.Stderr.Length > 0 : result.Stderr.Length == 0, result.Stderr);
            if (!rejected)
            {
                using var output = JsonDocument.Parse(result.Stdout);
                var entries = output.RootElement.GetProperty("entries");
                check("folder lexer whole row " + id,
                    output.RootElement.GetProperty("allEntryCount").GetInt32() == 1 && entries.GetArrayLength() == 1 &&
                    entries[0].GetProperty("path").GetString() == "row.bin" &&
                    entries[0].GetProperty("filtered").GetBoolean() == !selected &&
                    entries[0].GetProperty("status").GetString() == (selected ? "Modified" : "Uncompared") &&
                    entries[0].GetProperty("differenceKind").GetString() == (selected ? "Modified" : "Uncompared"), result.Stdout);
            }
            observations.Add(new
            {
                id, expression, rejected, selected, expectedExit, filter, arguments,
                exitCode = result.ExitCode, stdout, stderr, stdoutSha256 = Hash(stdout), stderrSha256 = Hash(stderr),
                protectedBefore = before, protectedAfter = after,
                pid = result.Pid, creationUtc = result.CreationUtc, launchUtc = result.LaunchUtc,
                exitObservedUtc = result.ExitObservedUtc, durationMilliseconds = result.DurationMilliseconds,
                launchEvidence = result.LaunchEvidence,
                nativeFullParserQualified = false, nativeTypedValueObserved = false
            });
        }
        // optimizerの5304計画・命令とは別のscopeと件数で保存する。
        await File.WriteAllBytesAsync(Path.Combine(work, "lexical-observations.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            scope = "folder-lexer-public-process-observations", publicOptimizerPlanCount = 5304,
            lexicalPlannedCommands = cases.GetArrayLength(), lexicalExecutedCommands = observations.Count,
            nativeLexerSourceExpected = 125, nativeLexerNulObservation = 1, nativeFullParserQualified = false,
            observations
        }));
    }
}
