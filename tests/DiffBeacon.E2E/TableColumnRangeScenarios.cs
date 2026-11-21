using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class TableColumnRangeScenarios
{
    private sealed record Snapshot(int?[][] Mapping, string Raw, string[] Paths, byte[][] Bytes);

    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var directory = Path.Combine(fixtures, "table-column-range");
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false);
        var leftText = "key,detail,group\r\nA,old-A,one\r\nB,old-B,two\r\nshort,only\r\n";
        var rightText = "key,detail,group\r\nX,new-X,extra\r\nA,new-A,one\r\nB,new-B,two\r\nshort,other\r\n";

        var defaults = await Compare("default", leftText, rightText, null, []);
        var included = await Compare("include-first-third", leftText, rightText, null, ["--table-column-range", "1,3"]);
        var excluded = await Compare("exclusion-priority", leftText, rightText, null, ["--table-column-range", "1-3, !2"]);
        var accumulated = await Compare("repeated-specifications", leftText, rightText, null,
            ["--table-column-range", "1", "--table-column-range", "3"]);
        check("table-column-range-exclusions-override-inclusions", SameMap(included.Mapping, excluded.Mapping), excluded.Raw);
        check("table-column-range-repeated-specs-append-in-order", SameMap(included.Mapping, accumulated.Mapping), accumulated.Raw);
        check("table-column-range-short-rows-are-clamped-to-real-columns", HasPair(included.Mapping, 4, 5), Describe(included.Mapping));
        check("table-column-range-preserves-all-source-rows", AllRowsOnce(included.Mapping, leftText, rightText), Describe(included.Mapping));

        var singleColumnLeft = "old-A\r\nold-B\r\n";
        var singleColumnRight = "new-X\r\nold-A\r\nold-B\r\n";
        var allPane = await Compare("all-pane", singleColumnLeft, singleColumnRight, null, ["--table-column-range", "!1:all"]);
        var leftOnly = await Compare("left-only", singleColumnLeft, singleColumnRight, null, ["--table-column-range", "!1:left"]);
        var rightOnly = await Compare("right-only", singleColumnLeft, singleColumnRight, null, ["--table-column-range", "!1:right"]);
        check("table-column-range-pane-scope-does-not-change-other-pane", !SameMap(allPane.Mapping, leftOnly.Mapping)
            && !SameMap(allPane.Mapping, rightOnly.Mapping),
            $"all={Describe(allPane.Mapping)}; left={Describe(leftOnly.Mapping)}; right={Describe(rightOnly.Mapping)}");

        var baseText = "key,detail,group\r\nB,base-B,two\r\nA,base-A,one\r\nshort,base-only\r\n";
        var threeWayAll = await Compare("three-way-all", leftText, rightText, baseText,
            ["--table-column-range", "1,3:all"]);
        var threeWayBase = await Compare("three-way-base-only", leftText, rightText, baseText,
            ["--table-column-range", "1,3:base"]);
        check("table-column-range-three-way-base-pane-is-targeted", !SameMap(threeWayBase.Mapping, threeWayAll.Mapping),
            $"all={Describe(threeWayAll.Mapping)}; base={Describe(threeWayBase.Mapping)}");
        check("table-column-range-three-way-retains-source-row-order", AllRowsOnce(threeWayAll.Mapping, leftText, baseText, rightText),
            Describe(threeWayAll.Mapping));

        var quotedLeft = "id,detail,group\r\nA,\"old,\r\nwrapped\",one\r\nB,\"old \"\"quoted\"\"\",two\r\n";
        var quotedRight = "id,detail,group\r\nB,\"new \"\"quoted\"\"\",two\r\nA,\"new,\r\nwrapped\",one\r\n";
        var quoted = await Compare("quoted-fields", quotedLeft, quotedRight, null, ["--table-column-range", "1,3"]);
        check("table-column-range-uses-decoded-csv-columns", HasPair(quoted.Mapping, 3, 2), Describe(quoted.Mapping));

        var projectPath = Path.Combine(directory, "range-project.json");
        var reportPath = Path.Combine(directory, "range-report.html");
        var projectBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            mode = "Table", leftPath = included.Paths[0], rightPath = included.Paths[1], tableDelimiter = ',',
            tableQuote = '"', tableAllowNewlinesInQuotes = true,
            tableColumnRangeRules = new[] { new { specification = "1,3", panes = 7 } }
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllBytesAsync(projectPath, projectBytes);
        var beforeReportSources = included.Paths.Select(File.ReadAllBytes).ToArray();
        var report = await run("table-column-range-report-project", 0, true, ["--report-project", projectPath, reportPath]);
        check("table-column-range-report-project-generated", report.ExitCode == 0 && File.Exists(reportPath), report.Stdout + report.Stderr);
        var reportProjectAfter = await File.ReadAllBytesAsync(projectPath);
        check("table-column-range-report-project-is-preserved", projectBytes.SequenceEqual(reportProjectAfter), projectPath);
        check("table-column-range-report-does-not-change-source-bytes",
            included.Paths.Select((path, side) => beforeReportSources[side].SequenceEqual(File.ReadAllBytes(path))).All(value => value), "All source bytes are unchanged.");
        if (File.Exists(reportPath))
        {
            var html = await File.ReadAllTextAsync(reportPath);
            check("table-column-range-report-keeps-original-cell-values", html.Contains("old-A", StringComparison.Ordinal)
                && html.Contains("new-A", StringComparison.Ordinal), "Visible cell values are not filtered out.");
            check("table-column-range-report-retains-cell-differences", Regex.IsMatch(html, "<td\\b[^>]*class=[\\\"'](?:Modified|Added|Deleted)[\\\"']"), "Cells still show their ordinary differences.");
        }

        var legacyProjectPath = Path.Combine(directory, "legacy-project.json");
        var legacyProject = JsonSerializer.SerializeToUtf8Bytes(new { mode = "Table", leftPath = included.Paths[0], rightPath = included.Paths[1], tableDelimiter = ',' });
        await File.WriteAllBytesAsync(legacyProjectPath, legacyProject);
        var legacyOutput = Path.Combine(directory, "legacy-report.html");
        var legacy = await run("table-column-range-old-project-defaults-to-all", 0, true, ["--report-project", legacyProjectPath, legacyOutput]);
        check("table-column-range-old-project-without-setting-loads", legacy.ExitCode == 0 && File.Exists(legacyOutput), legacy.Stdout + legacy.Stderr);

        foreach (var (name, specification) in new[] { ("zero", "0"), ("reversed", "3-2"), ("trailing-comma", "1,") })
        {
            var result = await run("table-column-range-invalid-" + name, 2, false,
                ["--table", included.Paths[0], included.Paths[1], "--table-column-range", specification]);
            CheckBytes(result.Name, included.Paths, included.Bytes);
            check(result.Name + "-rejects-invalid-specification", result.ExitCode == 2 && !string.IsNullOrWhiteSpace(result.Stderr), result.Stdout + result.Stderr);
        }
        var unknownPane = await run("table-column-range-invalid-pane", 2, false,
            ["--table", included.Paths[0], included.Paths[1], "--table-column-range", "1:middle"]);
        check("table-column-range-rejects-unknown-pane", unknownPane.ExitCode == 2 && !string.IsNullOrWhiteSpace(unknownPane.Stderr), unknownPane.Stdout + unknownPane.Stderr);
        var wrongCommand = await run("table-column-range-wrong-command", 2, false,
            ["--json", included.Paths[0], included.Paths[1], "--table-column-range", "1"]);
        check("table-column-range-option-rejected-outside-table", wrongCommand.ExitCode == 2 && !string.IsNullOrWhiteSpace(wrongCommand.Stderr), wrongCommand.Stdout + wrongCommand.Stderr);
        CheckBytes("table-column-range-invalid-inputs", included.Paths, included.Bytes);

        var invalidProjectPath = Path.Combine(directory, "invalid-range-project.json");
        var invalidOutput = Path.Combine(directory, "invalid-range-report.html");
        var sentinel = Encoding.UTF8.GetBytes("existing output\r\n");
        await File.WriteAllBytesAsync(invalidOutput, sentinel);
        foreach (var (name, specification) in new[] { ("bad-order", "2-1"), ("oversize", new string('1', 65_537)) })
        {
            var invalidProject = JsonSerializer.SerializeToUtf8Bytes(new
            {
                mode = "Table", leftPath = included.Paths[0], rightPath = included.Paths[1], tableDelimiter = ',',
                tableColumnRangeRules = new[] { new { specification, panes = 7 } }
            });
            await File.WriteAllBytesAsync(invalidProjectPath, invalidProject);
            var rejected = await run("table-column-range-invalid-project-" + name, 2, false,
                ["--report-project", invalidProjectPath, invalidOutput]);
            var outputAfterRejection = await File.ReadAllBytesAsync(invalidOutput);
            var projectAfterRejection = await File.ReadAllBytesAsync(invalidProjectPath);
            check(rejected.Name + "-rejects-invalid-range", rejected.ExitCode == 2, rejected.Stdout + rejected.Stderr);
            check(rejected.Name + "-preserves-existing-output", sentinel.SequenceEqual(outputAfterRejection), invalidOutput);
            check(rejected.Name + "-preserves-project-bytes", invalidProject.SequenceEqual(projectAfterRejection), invalidProjectPath);
        }
        CheckBytes("table-column-range-invalid-projects-preserve-inputs", included.Paths, included.Bytes);

        await File.WriteAllTextAsync(Path.Combine(output, "table-column-range-result.json"),
            JsonSerializer.Serialize(new { include = included.Mapping, excluded = excluded.Mapping, accumulated = accumulated.Mapping,
                allPane = allPane.Mapping, leftOnly = leftOnly.Mapping, rightOnly = rightOnly.Mapping,
                threeWayAll = threeWayAll.Mapping, threeWayBase = threeWayBase.Mapping },
                new JsonSerializerOptions { WriteIndented = true }), utf8);

        async Task<Snapshot> Compare(string name, string leftSource, string rightSource, string? baseSource, string[] flags)
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
            var result = await run("table-column-range-" + name, 1, true, arguments.ToArray());
            CheckBytes(result.Name, paths, bytes);
            try
            {
                using var document = JsonDocument.Parse(result.Stdout);
                var root = document.RootElement;
                var sides = paths.Length == 2 ? new[] { "left", "right" } : ["left", "base", "right"];
                var mapping = root.GetProperty("mapping").EnumerateArray().Select(row => sides.Select(side =>
                    row.GetProperty(side).ValueKind == JsonValueKind.Null ? (int?)null : row.GetProperty(side).GetInt32()).ToArray()).ToArray();
                check(result.Name + "-status", result.ExitCode == 1 && mapping.Length == root.GetProperty("alignedRows").GetInt32(), result.Stdout);
                CheckRows(result.Name, mapping, sources);
                return new(mapping, result.Stdout, paths, bytes);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                check(result.Name + "-json-contract", false, error.Message + ": " + result.Stdout + result.Stderr);
                return new([], result.Stdout, paths, bytes);
            }
        }

        void CheckRows(string name, int?[][] mapping, string[] sources)
        {
            var sides = mapping.Length == 0 ? 0 : mapping[0].Length;
            for (var side = 0; side < Math.Min(sides, sources.Length); side++)
            {
                var actual = mapping.Select(row => row[side]).Where(row => row.HasValue).Select(row => row!.Value).ToArray();
                var expected = Enumerable.Range(1, CountRows(sources[side])).ToArray();
                check(name + "-all-source-rows-" + side.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-once-in-order",
                    actual.SequenceEqual(expected), $"expected={string.Join(',', expected)}; actual={string.Join(',', actual)}");
            }
        }

        void CheckBytes(string name, string[] paths, byte[][] bytes)
            => check(name + "-source-bytes-preserved", paths.Select((path, side) => bytes[side].SequenceEqual(File.ReadAllBytes(path))).All(value => value), "All source bytes are unchanged.");

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

        static bool AllRowsOnce(int?[][] mapping, params string[] sources)
            => sources.Select((source, side) => mapping.Select(row => row[side]).Where(row => row.HasValue)
                .Select(row => row!.Value).SequenceEqual(Enumerable.Range(1, CountRows(source)))).All(value => value);

        static bool HasPair(int?[][] mapping, int left, int right)
            => mapping.Any(row => row[0] == left && row[^1] == right);

        static bool SameMap(int?[][] first, int?[][] second)
            => first.Length == second.Length && first.Zip(second).All(pair => pair.First.SequenceEqual(pair.Second));

        static string Describe(int?[][] mapping)
            => JsonSerializer.Serialize(mapping);
    }
}
