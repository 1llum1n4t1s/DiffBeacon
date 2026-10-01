using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class GnuTableScenarios
{
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var goldenPath = FindGolden();
        var goldenBytes = await File.ReadAllBytesAsync(goldenPath);
        check("gnu-table-golden-sha", Convert.ToHexString(SHA256.HashData(goldenBytes)) ==
            "4D52C2A1BADC1FEBF908B7EF348CE05808F8400B64736040E7D66359D377EDCF", goldenPath);
        using var golden = JsonDocument.Parse(goldenBytes);
        var all = golden.RootElement.GetProperty("cases").EnumerateArray().Where(c =>
            c.GetProperty("mode").GetString() == "default" && c.GetProperty("name").GetString()!.StartsWith("ab-exhaustive-", StringComparison.Ordinal)).ToArray();
        var cases = all.Where(c => c.GetProperty("changes").EnumerateArray().All(change =>
            change.GetProperty("deleted").GetInt32() == 0 || change.GetProperty("inserted").GetInt32() == 0)).ToArray();
        check("gnu-table-selection-count", all.Length == 225 && cases.Length == 143, $"exhaustive={all.Length}; selected={cases.Length}");
        var folder = Path.Combine(fixtures, "gnu-table");
        var reports = Path.Combine(output, "gnu-table-reports");
        Directory.CreateDirectory(folder); Directory.CreateDirectory(reports);
        var matched = new List<string>(); var fallback = new List<string>(); var failed = new List<string>();
        var observations = new List<object>();
        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString()!;
            var source = c.GetProperty("inputs").EnumerateArray().ToArray();
            var bytes = source.Select(input => Convert.FromBase64String(input.GetProperty("base64").GetString()!)).ToArray();
            for (var side = 0; side < 2; side++)
                check("gnu-table-" + name + "-source-" + side, bytes[side].All(b => b == (byte)'A' || b == (byte)'B' || b == 10)
                    && bytes[side].Length == source[side].GetProperty("byteLength").GetInt32()
                    && Convert.ToHexString(SHA256.HashData(bytes[side])).Equals(source[side].GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase),
                    "Unmodified original ASCII A/B LF bytes; no decoded CSV caller compatibility claim.");
            var texts = bytes.Select(Encoding.UTF8.GetString).ToArray();
            var expected = FromOriginal(c.GetProperty("changes"), Cells(texts[0]).Length, Cells(texts[1]).Length);
            var exit = c.GetProperty("changes").GetArrayLength() == 0 ? 0 : 1;
            var snapshot = await Compare("gnu-table-" + name, bytes, ["--eol", "strict", "--max-work", "8000000"], exit, 8_000_000);
            if (snapshot is null) { failed.Add(name); continue; }
            var same = SameMap(expected, snapshot.Mapping) && !snapshot.Fallback;
            check("gnu-table-" + name + "-original-mapping", same, $"expected={JsonSerializer.Serialize(expected)}; actual={snapshot.Raw}");
            var htmlSame = await Report("gnu-table-" + name, snapshot, texts, expected, (side, row) =>
                expected[row][0] is null ? "Added" : expected[row][1] is null ? "Deleted" : "Equal");
            if (snapshot.Fallback) fallback.Add(name);
            if (same && htmlSame) matched.Add(name); else failed.Add(name);
            observations.Add(new { name, originalChanges = c.GetProperty("changes"), expected, actual = snapshot.Mapping,
                snapshot.Fallback, snapshot.WorkUsed, snapshot.Reason, htmlSame });
        }

        var alias = await CompareText("gnu-table-quote-alias-zero", ["a\n\"a\"\n", "\"a\"\na\n"], ["--max-work", "0"], 0, 0);
        if (alias is not null) check("gnu-table-quote-alias-identity", SameMap(alias.Mapping, [[1, 1], [2, 2]]) && !alias.Fallback, alias.Raw);
        var p2Texts = new[] { "a\n\"a\"\n", "\"a\"\na\n", "z\n" };
        var p2 = await CompareText("gnu-table-p2-three", p2Texts, [], 1, 4_000_000);
        if (p2 is not null)
        {
            check("gnu-table-p2-unchanged-identity", p2.Mapping.Where(row => row[0].HasValue || row[1].HasValue)
                .All(row => row[0].HasValue && row[0] == row[1]), p2.Raw);
            await Report("gnu-table-p2-three", p2, p2Texts, p2.Mapping, (side, row) => side == 0 && p2.Mapping[row][0].HasValue ? "Equal" : null);
        }
        var anchors = Enumerable.Range(0, 40).Select(i => "anchor-" + i).ToArray();
        var ancestor = string.Join('\n', anchors) + "\n";
        var shiftLeft = "left-prefix\n" + string.Join('\n', anchors.Take(20)) + "\nleft-middle\n" + string.Join('\n', anchors.Skip(20)) + "\n";
        var shiftRight = "right-prefix\n" + string.Join('\n', anchors.Take(25)) + "\nright-middle\n" + string.Join('\n', anchors.Skip(25)) + "\n";
        foreach (var budget in new[] { 0, 1, 120 })
        foreach (var texts in new[] { new[] { shiftLeft, shiftRight }, new[] { shiftLeft, ancestor, shiftRight } })
        {
            var name = "gnu-table-shared-budget-" + texts.Length + "-" + budget;
            var snapshot = await CompareText(name, texts, ["--max-work", budget.ToString(System.Globalization.CultureInfo.InvariantCulture)], 1, budget);
            if (snapshot is not null) check(name + "-initial-fallback", snapshot.Fallback && !string.IsNullOrWhiteSpace(snapshot.Reason)
                && snapshot.Reason.Contains("gnu", StringComparison.OrdinalIgnoreCase), snapshot.Raw);
        }
        await CompareText("gnu-table-eol-strict", ["a\r\na\r\n", "a\na\n"], ["--eol", "strict"], 1, 4_000_000);
        await CompareText("gnu-table-eol-ignore", ["a\r\na\r\n", "a\na\n"], ["--eol", "ignore"], 0, 4_000_000);
        await CompareText("gnu-table-eof-strict", ["a\n", "a"], ["--eol", "strict"], 1, 4_000_000);
        await CompareText("gnu-table-eof-ignore", ["a\n", "a"], ["--eol", "ignore"], 0, 4_000_000);
        var after = await File.ReadAllBytesAsync(goldenPath);
        check("gnu-table-golden-preserved", goldenBytes.SequenceEqual(after), goldenPath);
        await File.WriteAllTextAsync(Path.Combine(output, "gnu-table-observations.json"), JsonSerializer.Serialize(new
        {
            selection = "default ab-exhaustive, ASCII A/B LF, each original change one-sided or zero changes; no raw two-sided score oracle",
            sourceCount = all.Length, selected = cases.Length, names = cases.Select(c => c.GetProperty("name").GetString()),
            matched, fallback, failed, observations, auxiliaryCli = 12, auxiliaryHtml = 1
        }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        Task<Snapshot?> CompareText(string name, string[] texts, string[] flags, int exit, int budget)
            => Compare(name, texts.Select(Encoding.UTF8.GetBytes).ToArray(), flags, exit, budget);
        async Task<Snapshot?> Compare(string name, byte[][] bytes, string[] flags, int exit, int budget)
        {
            var paths = bytes.Select((data, side) =>
            {
                var path = Path.Combine(folder, name + "-" + side + ".csv"); File.WriteAllBytes(path, data); return path;
            }).ToArray();
            var args = new List<string> { "--table", paths[0], paths[^1] };
            if (paths.Length == 3) args.AddRange(["--base", paths[1]]);
            args.AddRange(flags);
            var result = await run(name, exit, true, args.ToArray());
            CheckBytes(name, paths, bytes);
            try
            {
                using var document = JsonDocument.Parse(result.Stdout);
                var root = document.RootElement;
                var sides = paths.Length == 2 ? new[] { "left", "right" } : ["left", "base", "right"];
                var map = root.GetProperty("mapping").EnumerateArray().Select(row => sides.Select(side =>
                    row.GetProperty(side).ValueKind == JsonValueKind.Null ? (int?)null : row.GetProperty(side).GetInt32()).ToArray()).ToArray();
                var used = root.GetProperty("alignmentWorkUsed").GetInt32();
                var didFallback = root.GetProperty("alignmentFallback").GetBoolean();
                var reason = root.GetProperty("alignmentFallbackReason").GetString();
                var texts = bytes.Select(Encoding.UTF8.GetString).ToArray();
                check(name + "-status", result.ExitCode == exit && root.GetProperty("different").GetBoolean() == (exit == 1)
                    && root.GetProperty("alignedRows").GetInt32() == map.Length && root.GetProperty("rows").GetInt32() == texts.Max(text => Cells(text).Length)
                    && root.GetProperty("cols").GetInt32() == (texts.All(text => text.Length == 0) ? 0 : 1), result.Stdout);
                check(name + "-budget", used >= 0 && used <= budget && (didFallback ? !string.IsNullOrWhiteSpace(reason) : reason == null), result.Stdout);
                CheckRows(name, map, texts);
                return new(map, didFallback, used, reason, paths, bytes, result.Stdout);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
            { check(name + "-json-contract", false, error.Message + ": " + result.Stdout); return null; }
        }
        void CheckRows(string name, int?[][] map, string[] texts)
        {
            for (var side = 0; side < texts.Length; side++)
                check(name + "-source-rows-" + side, map.Select(row => row[side]).Where(n => n.HasValue).Select(n => n!.Value)
                    .SequenceEqual(Enumerable.Range(1, Cells(texts[side]).Length)), "All source rows once in original order.");
        }
        void CheckBytes(string name, string[] paths, byte[][] bytes)
            => check(name + "-bytes-preserved", paths.Select((path, side) => bytes[side].SequenceEqual(File.ReadAllBytes(path))).All(value => value), "Original file bytes preserved.");
        async Task<bool> Report(string name, Snapshot snapshot, string[] texts, int?[][] expected, Func<int, int, string?> expectedKind)
        {
            var project = Path.Combine(folder, name + ".json");
            await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new { mode = "Table", leftPath = snapshot.Paths[0],
                basePath = snapshot.Paths.Length == 3 ? snapshot.Paths[1] : "", rightPath = snapshot.Paths[^1], tableDelimiter = "," }), new UTF8Encoding(false));
            var projectBytes = await File.ReadAllBytesAsync(project);
            var htmlPath = Path.Combine(reports, name + ".html");
            var command = await run(name + "-report", 0, false, ["--report-project", project, htmlPath]);
            var afterBytes = await File.ReadAllBytesAsync(project);
            check(name + "-project-preserved", projectBytes.SequenceEqual(afterBytes), project);
            CheckBytes(name + "-report", snapshot.Paths, snapshot.Bytes);
            if (!File.Exists(htmlPath)) { check(name + "-html-exists", false, htmlPath); return false; }
            var html = await File.ReadAllTextAsync(htmlPath);
            var sides = texts.Length == 2 ? new[] { "left", "right" } : ["left", "base", "right"];
            var contents = texts.Select(Cells).ToArray();
            var cells = Regex.Matches(html, "<td\\b(?<attrs>[^>]*)>(?<body>.*?)</td>", RegexOptions.Singleline).Cast<Match>()
                .Where(cell => Attr(cell, "data-side").Length != 0).ToArray();
            var columns = contents.All(c => c.Length == 0) ? 0 : 1;
            var valid = command.ExitCode == 0 && cells.Length == expected.Length * sides.Length * columns;
            var seen = new HashSet<(int Side, int Row)>();
            foreach (var cell in cells)
            {
                var side = Array.IndexOf(sides, Attr(cell, "data-side"));
                var aligned = int.TryParse(Attr(cell, "data-aligned-row"), out var number) ? number - 1 : -1;
                if (side < 0 || aligned < 0 || aligned >= expected.Length) { valid = false; continue; }
                var source = expected[aligned][side];
                var body = WebUtility.HtmlDecode(Regex.Replace(cell.Groups["body"].Value, "<[^>]*>", ""));
                var wanted = expectedKind(side, aligned);
                var okay = seen.Add((side, aligned))
                    && Attr(cell, "data-row") == (source?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "")
                    && Attr(cell, "data-column") == "1" && Attr(cell, "data-missing") == (source is null ? "true" : "false")
                    && (source is null ? body.Length == 0 : source.Value > 0 && source.Value <= contents[side].Length && body == contents[side][source.Value - 1])
                    && (wanted is null || Attr(cell, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(wanted));
                valid &= okay;
                check(name + "-html-cell-" + aligned + "-" + side, okay, cell.Value);
            }
            check(name + "-html-all-cells", valid, $"actualCells={cells.Length}; expectedRows={expected.Length}; sides={sides.Length}");
            return valid;
        }
    }
    private sealed record Snapshot(int?[][] Mapping, bool Fallback, int WorkUsed, string? Reason, string[] Paths, byte[][] Bytes, string Raw);
    private static string Attr(Match cell, string name) => Regex.Match(cell.Groups["attrs"].Value, Regex.Escape(name) + "=\"([^\"]*)\"").Groups[1].Value;
    private static bool SameMap(int?[][] a, int?[][] b) => a.Length == b.Length && a.Zip(b).All(pair => pair.First.SequenceEqual(pair.Second));
    private static string[] Cells(string text)
    {
        if (text.Length == 0) return [];
        var lines = Regex.Split(text, "\\r\\n|\\r|\\n");
        if (text[^1] is '\r' or '\n') lines = lines[..^1];
        return lines.Select(line => line.StartsWith('"') && line.EndsWith('"') ? line[1..^1].Replace("\"\"", "\"") : line).ToArray();
    }
    // 固定原本scriptを展開するだけ。両側変更の行対応算法は計算しない。
    private static int?[][] FromOriginal(JsonElement changes, int leftRows, int rightRows)
    {
        var map = new List<int?[]>(); var a = 0; var b = 0;
        foreach (var change in changes.EnumerateArray())
        {
            var endA = change.GetProperty("line0").GetInt32(); var endB = change.GetProperty("line1").GetInt32();
            while (a < endA && b < endB) map.Add([++a, ++b]);
            if (a != endA || b != endB) throw new InvalidDataException("Original script has unequal unchanged gap lengths.");
            var deleted = change.GetProperty("deleted").GetInt32(); var inserted = change.GetProperty("inserted").GetInt32();
            if (deleted != 0 && inserted != 0) throw new InvalidDataException("A two-sided raw-score block must not be used as a mapping oracle.");
            for (var i = 0; i < deleted; i++) map.Add([++a, null]);
            for (var i = 0; i < inserted; i++) map.Add([null, ++b]);
        }
        while (a < leftRows && b < rightRows) map.Add([++a, ++b]);
        if (a != leftRows || b != rightRows) throw new InvalidDataException("Original script did not consume all rows.");
        return map.ToArray();
    }
    private static string FindGolden()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "Fixtures", "GnuLines", "legacy-gnu-golden.json");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("GNU golden fixture was not found in the repository.");
    }
}
