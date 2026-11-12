using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class TableColumnIgnoreScenarios
{
    private sealed record Snapshot(int?[][] Mapping, bool Different, string Raw, string[] Paths, byte[][] Bytes);

    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var directory = Path.Combine(fixtures, "table-column-ignore");
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false);
        var leftText = "old-A\r\nold-B\r\n";
        var rightText = "new-X\r\nold-A\r\nold-B\r\n";
        var left = await Compare("two-way-default", leftText, rightText, null, [], 1);
        var all = await Compare("two-way-all", leftText, rightText, null, ["--table-ignore-column", "1"], 1);
        check("table-column-ignore-all-changes-initial-row-mapping", !SameMap(left.Mapping, all.Mapping),
            $"default={Describe(left.Mapping)}; all={Describe(all.Mapping)}");

        var duplicate = await Compare("two-way-duplicate", leftText, rightText, null,
            ["--table-ignore-column", "1:all", "--table-ignore-column", "1:all"], 1);
        check("table-column-ignore-repeated-identical-rule-is-stable", SameMap(all.Mapping, duplicate.Mapping), duplicate.Raw);

        var leftOnly = await Compare("two-way-left-only", leftText, rightText, null,
            ["--table-ignore-column", "1:left"], 1);
        var rightOnly = await Compare("two-way-right-only", leftText, rightText, null,
            ["--table-ignore-column", "1:right"], 1);
        check("table-column-ignore-selected-left-is-not-all-panes", !SameMap(all.Mapping, leftOnly.Mapping), Describe(leftOnly.Mapping));
        check("table-column-ignore-selected-right-is-not-all-panes", !SameMap(all.Mapping, rightOnly.Mapping), Describe(rightOnly.Mapping));

        var twoColumnLeft = "old-A,1\r\nold-B,2\r\n";
        var twoColumnRight = "new-X,0\r\nold-A,1\r\nold-B,2\r\n";
        var ignoreFirst = await Compare("accumulation-first-column", twoColumnLeft, twoColumnRight, null,
            ["--table-ignore-column", "1:all"], 1);
        var ignoreSecond = await Compare("accumulation-second-column", twoColumnLeft, twoColumnRight, null,
            ["--table-ignore-column", "2:all"], 1);
        var accumulated = await Compare("accumulation-two-columns", twoColumnLeft, twoColumnRight, null,
            ["--table-ignore-column", "1:all", "--table-ignore-column", "2:all"], 1);
        check("table-column-ignore-repeated-distinct-rules-accumulate", !SameMap(accumulated.Mapping, ignoreFirst.Mapping)
            && !SameMap(accumulated.Mapping, ignoreSecond.Mapping),
            $"first={Describe(ignoreFirst.Mapping)}; second={Describe(ignoreSecond.Mapping)}; both={Describe(accumulated.Mapping)}");

        var quotedLeft = "key,detail\r\nx,\"old,\"\"A\"\"\"\r\nx,\"line one\r\nline two\"\r\nshort\r\n";
        var quotedRight = "key,detail\r\nx,\"new,\"\"X\"\"\"\r\nx,\"old,\"\"A\"\"\"\r\nx,\"line one\r\nline two\"\r\nshort\r\n";
        var quotedDefault = await Compare("quoted-fields-default", quotedLeft, quotedRight, null, [], 1);
        var quotedIgnored = await Compare("quoted-fields-ignore-second", quotedLeft, quotedRight, null,
            ["--table-ignore-column", "2:all"], 1);
        check("table-column-ignore-uses-parsed-field-index", !SameMap(quotedDefault.Mapping, quotedIgnored.Mapping),
            $"default={Describe(quotedDefault.Mapping)}; ignored={Describe(quotedIgnored.Mapping)}");
        var quotedReportProject = Path.Combine(directory, "quoted-fields-project.json");
        var quotedReportPath = Path.Combine(directory, "quoted-fields-report.html");
        var quotedProjectBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            mode = "Table", leftPath = quotedIgnored.Paths[0], rightPath = quotedIgnored.Paths[1], tableDelimiter = ',',
            tableQuote = '"', tableAllowNewlinesInQuotes = true,
            tableColumnIgnoreRules = new[] { new { column = 2, panes = 7 } }
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllBytesAsync(quotedReportProject, quotedProjectBytes);
        var quotedReport = await run("table-column-ignore-quoted-report", 0, true,
            ["--report-project", quotedReportProject, quotedReportPath]);
        check("table-column-ignore-quoted-report-generated", quotedReport.ExitCode == 0 && File.Exists(quotedReportPath), quotedReport.Stdout + quotedReport.Stderr);
        if (File.Exists(quotedReportPath))
        {
            var html = await File.ReadAllTextAsync(quotedReportPath);
            check("table-column-ignore-quoted-delimiters-and-newlines-remain-decoded", html.Contains("old,&quot;A&quot;", StringComparison.Ordinal)
                || html.Contains("old,\"A\"", StringComparison.Ordinal), "Quoted delimiter and escaped quote remain in their original cell.");
            check("table-column-ignore-short-row-keeps-missing-cell", Regex.IsMatch(html,
                "<td\\b[^>]*data-side=\"left\"[^>]*data-row=\"4\"[^>]*data-column=\"2\"[^>]*data-missing=\"true\""),
                "Column 2 remains absent for the short source row.");
            var quotedProjectAfter = await File.ReadAllBytesAsync(quotedReportProject);
            check("table-column-ignore-quoted-project-preserved", quotedProjectBytes.SequenceEqual(quotedProjectAfter), quotedReportProject);
        }
        else check("table-column-ignore-quoted-report-readable", false, quotedReportPath);

        var baseText = "new-X\r\nold-A\r\nold-B\r\n";
        var threeWay = await Compare("three-way-all", leftText, rightText, baseText,
            ["--table-ignore-column", "1:all"], 1);
        var baseOnly = await Compare("three-way-base-only", leftText, rightText, baseText,
            ["--table-ignore-column", "1:base"], 1);
        CheckRows("three-way-all", threeWay.Mapping, [leftText, baseText, rightText]);
        CheckRows("three-way-base-only", baseOnly.Mapping, [leftText, baseText, rightText]);

        var reportProject = Path.Combine(directory, "table-ignore-report-project.json");
        var reportHtml = Path.Combine(directory, "table-ignore-report.html");
        var reportProjectBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            mode = "Table", leftPath = left.Paths[0], rightPath = left.Paths[1], tableDelimiter = ',',
            tableColumnIgnoreRules = new[] { new { column = 1, panes = 7 } }
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllBytesAsync(reportProject, reportProjectBytes);
        var beforeReportSources = left.Paths.Select(File.ReadAllBytes).ToArray();
        var report = await run("table-column-ignore-report-project", 0, true,
            ["--report-project", reportProject, reportHtml]);
        check("table-column-ignore-report-generated", report.ExitCode == 0 && File.Exists(reportHtml), report.Stdout + report.Stderr);
        var reportProjectAfter = await File.ReadAllBytesAsync(reportProject);
        check("table-column-ignore-report-preserves-project", reportProjectBytes.SequenceEqual(reportProjectAfter), reportProject);
        CheckBytes("table-column-ignore-report", left.Paths, beforeReportSources);
        if (File.Exists(reportHtml))
        {
            var html = await File.ReadAllTextAsync(reportHtml);
            var cells = Regex.Matches(html, "<td\\b(?<attrs>[^>]*)>(?<body>.*?)</td>", RegexOptions.Singleline)
                .Cast<Match>().Where(cell => Regex.IsMatch(cell.Groups["attrs"].Value, "\\bdata-side=[\\\"'](?:left|right)[\\\"']"))
                .Select(cell => WebUtility.HtmlDecode(Regex.Replace(cell.Groups["body"].Value, "<[^>]*>", ""))).ToArray();
            check("table-column-ignore-report-displays-original-cell-values",
                cells.Contains("old-A", StringComparer.Ordinal) && cells.Contains("old-B", StringComparer.Ordinal)
                    && cells.Contains("new-X", StringComparer.Ordinal), string.Join(" | ", cells));
            check("table-column-ignore-report-retains-cell-differences",
                Regex.IsMatch(html, "<td\\b[^>]*class=[\\\"'](?:Modified|Added|Deleted)[\\\"']"), "HTML retains cell difference classifications.");
        }
        else check("table-column-ignore-report-readable", false, reportHtml);

        var errorLeft = await Write("invalid-left.csv", "keep,one\r\n");
        var errorRight = await Write("invalid-right.csv", "keep,two\r\n");
        var errorInputs = new[] { errorLeft, errorRight };
        var beforeErrors = errorInputs.Select(File.ReadAllBytes).ToArray();
        var errors = new (string Name, string[] Args)[]
        {
            ("zero", ["--table-ignore-column", "0"]), ("negative", ["--table-ignore-column", "-1"]),
            ("unknown-scope", ["--table-ignore-column", "1:unknown"]), ("excessive-column", ["--table-ignore-column", "1048577"]),
            ("missing-value", ["--table-ignore-column"])
        };
        foreach (var error in errors)
        {
            var result = await run("table-column-ignore-invalid-" + error.Name, 2, false,
                ["--table", errorLeft, errorRight, .. error.Args]);
            check("table-column-ignore-invalid-" + error.Name + "-rejected", result.ExitCode == 2
                && !string.IsNullOrWhiteSpace(result.Stderr) && !SuccessJson(result.Stdout), result.Stderr);
            CheckBytes(result.Name, errorInputs, beforeErrors);
        }
        var wrongCommand = await run("table-column-ignore-wrong-command", 2, false,
            ["--json", errorLeft, errorRight, "--table-ignore-column", "1"]);
        check("table-column-ignore-option-rejected-outside-table", wrongCommand.ExitCode == 2
            && !string.IsNullOrWhiteSpace(wrongCommand.Stderr) && !SuccessJson(wrongCommand.Stdout), wrongCommand.Stderr);
        CheckBytes("table-column-ignore-wrong-command", errorInputs, beforeErrors);

        await File.WriteAllTextAsync(Path.Combine(output, "table-column-ignore-observations.json"),
            JsonSerializer.Serialize(new
            {
                twoWayDefault = left.Mapping, twoWayAll = all.Mapping, twoWayLeftOnly = leftOnly.Mapping,
                twoWayRightOnly = rightOnly.Mapping, accumulated = accumulated.Mapping,
                threeWayAll = threeWay.Mapping, threeWayBaseOnly = baseOnly.Mapping
            }, new JsonSerializerOptions { WriteIndented = true }), utf8);

        async Task<Snapshot> Compare(string name, string leftSource, string rightSource, string? baseSource, string[] flags, int expectedExit)
        {
            var sources = new[] { leftSource, baseSource, rightSource }.Where(text => text is not null).Cast<string>().ToArray();
            var paths = new string[sources.Length];
            var bytes = new byte[sources.Length][];
            for (var side = 0; side < sources.Length; side++)
            {
                paths[side] = Path.Combine(directory, name + "-" + side.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".csv");
                bytes[side] = utf8.GetBytes(sources[side]);
                await File.WriteAllBytesAsync(paths[side], bytes[side]);
            }
            var arguments = new List<string> { "--table", paths[0], paths[^1] };
            if (paths.Length == 3) arguments.AddRange(["--base", paths[1]]);
            arguments.AddRange(flags);
            var result = await run("table-column-ignore-" + name, expectedExit, true, arguments.ToArray());
            CheckBytes(result.Name, paths, bytes);
            try
            {
                using var document = JsonDocument.Parse(result.Stdout);
                var root = document.RootElement;
                var sides = paths.Length == 2 ? new[] { "left", "right" } : ["left", "base", "right"];
                var mapping = root.GetProperty("mapping").EnumerateArray().Select(row => sides.Select(side =>
                    row.GetProperty(side).ValueKind == JsonValueKind.Null ? (int?)null : row.GetProperty(side).GetInt32()).ToArray()).ToArray();
                var different = root.GetProperty("different").GetBoolean();
                check(result.Name + "-status", result.ExitCode == expectedExit && different && mapping.Length == root.GetProperty("alignedRows").GetInt32(), result.Stdout);
                CheckRows(result.Name, mapping, sources);
                return new(mapping, different, result.Stdout, paths, bytes);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                check(result.Name + "-json-contract", false, error.Message + ": " + result.Stdout);
                return new([], true, result.Stdout, paths, bytes);
            }
        }

        async Task<string> Write(string name, string text)
        {
            var path = Path.Combine(directory, name);
            await File.WriteAllTextAsync(path, text, utf8);
            return path;
        }

        void CheckRows(string name, int?[][] mapping, string[] sources)
        {
            for (var side = 0; side < sources.Length; side++)
            {
                var actual = mapping.Select(row => row[side]).Where(row => row.HasValue).Select(row => row!.Value).ToArray();
                var expected = Enumerable.Range(1, CountRows(sources[side])).ToArray();
                check(name + "-all-source-rows-" + side.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-once-in-order",
                    actual.SequenceEqual(expected), $"expected={string.Join(',', expected)}; actual={string.Join(',', actual)}");
            }
        }

        static int CountRows(string text)
        {
            if (text.Length == 0) return 0;
            var count = 0;
            var inQuotes = false;
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '"')
                {
                    if (inQuotes && index + 1 < text.Length && text[index + 1] == '"') { index++; continue; }
                    inQuotes = !inQuotes;
                }
                else if (!inQuotes && text[index] == '\n') count++;
            }
            if (text[^1] != '\n') count++;
            return count;
        }

        void CheckBytes(string name, string[] paths, byte[][] bytes)
            => check(name + "-source-bytes-preserved", paths.Select((path, side) => bytes[side].SequenceEqual(File.ReadAllBytes(path))).All(value => value), "All source bytes are unchanged.");

        static bool SameMap(int?[][] leftMap, int?[][] rightMap)
            => leftMap.Length == rightMap.Length && leftMap.Zip(rightMap).All(pair => pair.First.SequenceEqual(pair.Second));

        static string Describe(int?[][] mapping)
            => JsonSerializer.Serialize(mapping);

        static bool SuccessJson(string text)
        {
            try { using var document = JsonDocument.Parse(text); return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("different", out _); }
            catch (JsonException) { return false; }
        }
    }
}
