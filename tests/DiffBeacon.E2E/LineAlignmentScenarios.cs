using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class LineAlignmentScenarios
{
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var source = FindFixtures();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(source, "legacy-line-golden.json"));
        check("line-alignment-golden-sha", Convert.ToHexString(SHA256.HashData(bytes)) ==
            "06A370C108716F0CF250642580C8130AB7D2C5E65F0D693A4585E501605AAA37", source);
        var correctedBytes = await File.ReadAllBytesAsync(Path.Combine(source, "legacy-line-pair-dummy-golden.json"));
        check("line-alignment-corrected-sha", Convert.ToHexString(SHA256.HashData(correctedBytes)) ==
            "D72E3EA26D5A0758E370195B505E1CCBEC87C5DD82B51D819942F7D771E247E0", source);
        using var document = JsonDocument.Parse(bytes);
        using var corrected = JsonDocument.Parse(correctedBytes);
        var expandedBytes = await File.ReadAllBytesAsync(Path.Combine(source, "legacy-line-expanded-golden.json"));
        check("line-alignment-expanded-sha", Convert.ToHexString(SHA256.HashData(expandedBytes)) ==
            "1C311E5806E7C4E60F06350F841F83ED6B0DEB059DDEF83966D52A98C162ABF3", source);
        using var expanded = JsonDocument.Parse(expandedBytes);
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var correctedCases = corrected.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        check("line-alignment-oracle-map-stability", cases.Length == 80 && correctedCases.Length == 80
            && cases.Zip(correctedCases).All(pair => pair.First.GetProperty("mapping").GetRawText() == pair.Second.GetProperty("mapping").GetRawText()),
            "80 mappings are identical between unchanged and pair-dummy adapters; scores are not product assertions.");
        var additions = expanded.RootElement.GetProperty("cases").EnumerateArray()
            .Where(c => c.GetProperty("name").GetString() is "4096-both-edge" or "offset-prefix-three").ToArray();
        check("line-alignment-expanded-count", additions.Length == 8, "Two additional inputs, four configs each.");
        cases = cases.Concat(additions).ToArray();
        var exact = new HashSet<string>(StringComparer.Ordinal)
        {
            "zero-tie", "zero-tie-reverse", "central", "central-reverse", "4096-edge", "4097-cap",
            "4097-one-pair", "mixed-eol", "eof-no-eol", "wrapped", "empty-left", "empty-right", "quoted", "4096-both-edge"
        };
        var folder = Path.Combine(fixtures, "line-alignment");
        var reports = Path.Combine(output, "line-alignment-reports");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(reports);
        await File.WriteAllTextAsync(Path.Combine(output, "line-alignment-selection.json"), JsonSerializer.Serialize(new
        {
            goldenCount = cases.Length, exactNames = exact.Order().ToArray(), htmlConfigs = new[] { 0, 3 },
            excludedWholeBlockNames = cases.Select(c => c.GetProperty("name").GetString()).Distinct().Where(n => !exact.Contains(n!)).ToArray(),
            reason = "Initial decoded prefix/suffix/patience anchors may split the raw block oracle."
        }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        foreach (var c in cases)
        {
            var inputName = c.GetProperty("name").GetString()!;
            var config = c.GetProperty("config").GetInt32();
            var texts = c.GetProperty("text").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var name = "line-alignment-" + inputName + "-" + config;
            var flags = new List<string> { "--eol", config == 2 ? "strict" : "ignore" };
            if (config == 1) flags.Add("--word-level");
            if (config == 3) flags.AddRange(["--ignore-case", "--whitespace", "all"]);
            // eol-only has equal decoded cells; strict row endings alone make it different.
            var different = inputName != "eol-only" || config == 2;
            var snapshot = await Compare(name, texts, flags.ToArray(), different ? 1 : 0);
            if (snapshot is null) continue;
            if (exact.Contains(inputName))
            {
                var expected = c.GetProperty("mapping").EnumerateArray().Select(row => row.EnumerateArray()
                    .Select(value => value.ValueKind == JsonValueKind.Null ? (int?)null : value.GetInt32()).ToArray()).ToArray();
                check(name + "-legacy-map", SameMap(expected, snapshot.Mapping), Describe(expected, snapshot.Mapping));
            }
            if (inputName == "4097-cap") check(name + "-legacy-cap", snapshot.Fallback && snapshot.Reason == "legacy-content-limit", snapshot.Raw);
            if (inputName == "4097-one-pair") check(name + "-one-pair-shortcut", !snapshot.Fallback, snapshot.Raw);
            if (inputName == "4096-both-edge") check(name + "-true-boundary", !snapshot.Fallback, snapshot.Raw);
            if (config is 0 or 3)
            {
                var project = Path.Combine(folder, name + ".json");
                await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new
                {
                    leftPath = snapshot.Paths[0], basePath = texts.Length == 3 ? snapshot.Paths[1] : "",
                    rightPath = snapshot.Paths[^1], mode = "Table", tableDelimiter = ',',
                    ignoreCase = config == 3, ignoreWhitespace = config == 3
                }), new UTF8Encoding(false));
                var before = await File.ReadAllBytesAsync(project);
                var htmlPath = Path.Combine(reports, name + ".html");
                await run(name + "-report", 0, false, ["--report-project", project, htmlPath]);
                if (File.Exists(htmlPath))
                {
                    var html = await File.ReadAllTextAsync(htmlPath);
                    var map = HtmlMapping(html, texts.Length, check, name);
                    check(name + "-html-cli-map", SameMap(snapshot.Mapping, map), Describe(snapshot.Mapping, map));
                    CheckRows(name + "-html", map, texts);
                }
                else check(name + "-html-exists", false, htmlPath);
                var projectAfter = await File.ReadAllBytesAsync(project);
                check(name + "-project-preserved", before.SequenceEqual(projectAfter), project);
                CheckBytes(name + "-report", snapshot.Paths, snapshot.Bytes);
            }
        }

        await Compare("line-alignment-substitute-base-pattern", ["--base,keep\n", "x,keep\n"], ["--substitute", "--base", "x"], 0);
        await Compare("line-alignment-substitute-base-replacement", ["x,keep\n", "--base,keep\n"], ["--substitute", "x", "--base"], 0);

        var budgetTexts = new[] { "left-a\nanchor-one\nleft-b\nanchor-two\nleft-c\n", "right-a\nanchor-one\nright-b\nanchor-two\nright-c\n" };
        foreach (var budget in new[] { 0, 1, 120 })
        {
            var name = "line-alignment-shared-budget-" + budget;
            var snapshot = await Compare(name, budgetTexts, ["--max-work", budget.ToString(System.Globalization.CultureInfo.InvariantCulture)], 1);
            if (snapshot is null) continue;
            check(name + "-bounded", snapshot.Fallback && snapshot.Reason?.Contains("work-limit:", StringComparison.Ordinal) == true
                && snapshot.WorkUsed >= 0 && snapshot.WorkUsed <= budget, snapshot.Raw);
            // Two decoded unique anchors split this input into three changed blocks.
            check(name + "-anchors", snapshot.Mapping.Any(row => row[0] == 2 && row[1] == 2)
                && snapshot.Mapping.Any(row => row[0] == 4 && row[1] == 4), snapshot.Raw);
        }
        var errorLeft = Write("line-alignment-errors-left", "keep,one\r\n");
        var errorRight = Write("line-alignment-errors-right", "keep,two\r\n");
        var originals = new[] { File.ReadAllBytes(errorLeft), File.ReadAllBytes(errorRight) };
        var errors = new (string Name, string[] Flags)[]
        {
            ("unknown", ["--unknown-table-flag"]), ("negative", ["--max-work", "-1"]),
            ("excessive", ["--max-work", "8000001"]), ("bad-budget", ["--max-work", "bad"]),
            ("missing-budget", ["--max-work"]), ("unknown-eol", ["--eol", "space"]),
            ("missing-base", ["--base"]), ("duplicate-base", ["--base", errorLeft, "--base", errorRight]),
            ("missing-file", ["--base", Path.Combine(folder, "does-not-exist.csv")])
        };
        foreach (var error in errors)
        {
            var result = await run("line-alignment-error-" + error.Name, 2, false, ["--table", errorLeft, errorRight, .. error.Flags]);
            check(result.Name + "-rejected", result.ExitCode == 2 && !string.IsNullOrWhiteSpace(result.Stderr) && !SuccessJson(result.Stdout), result.Stderr);
            CheckBytes(result.Name, [errorLeft, errorRight], originals);
        }

        await DecodedAnchorCases();

        async Task DecodedAnchorCases()
        {
            var anchorCases = new[]
            {
                new DecodedAnchorCase("quote-left", ["a\n\"a\"\n", "\"a\"\na\n", "z\n"], 0, [], false, false, null, [[1, 1], [2, 2]], true),
                new DecodedAnchorCase("quote-right", ["z\n", "\"a\"\na\n", "a\n\"a\"\n"], 2, [], false, false, null, [[1, 1], [2, 2]], true),
                new DecodedAnchorCase("case-left", ["a\n\"A\"\n", "\"A\"\na\n", "z\n"], 0, ["--ignore-case"], true, false, null, [[1, 1], [2, 2]], true),
                new DecodedAnchorCase("space-right", ["z\n", "\"a b\"\na b\n", "a b\n\"a  b\"\n"], 2, ["--whitespace", "all"], false, true, null, [[1, 1], [2, 2]], true),
                new DecodedAnchorCase("substitute-left", ["a\n\"a\"\n", "\"b\"\nb\n", "z\n"], 0, ["--substitute", "a|b", "k"], false, false, "a|b", [[1, 1], [2, 2]], true),
                new DecodedAnchorCase("substitute-right", ["z\n", "\"b\"\nb\n", "a\n\"a\"\n"], 2, ["--substitute", "a|b", "k"], false, false, "a|b", [[1, 1], [2, 2]], true),
                new DecodedAnchorCase("partial-left", ["H\nleft-insert\na\n\"a\"\nT\n", "H\n\"a\"\na\nT\n", "Z\n\"a\"\nright-insert\na\nT\n"], 0, [], false, false, null, [[1, 1], [3, 2], [4, 3], [5, 4]], false),
                new DecodedAnchorCase("partial-right", ["Z\n\"a\"\nright-insert\na\nT\n", "H\n\"a\"\na\nT\n", "H\nleft-insert\na\n\"a\"\nT\n"], 2, [], false, false, null, [[1, 1], [3, 2], [4, 3], [5, 4]], false)
            };
            await File.WriteAllTextAsync(Path.Combine(output, "decoded-anchors-selection.json"),
                JsonSerializer.Serialize(anchorCases, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            foreach (var c in anchorCases)
            {
                var name = "line-alignment-decoded-" + c.Name;
                var pair = await Compare(name + "-pair", [c.Texts[c.Side], c.Texts[1]], c.Flags, c.Identity ? 0 : 1);
                if (pair is not null)
                {
                    foreach (var anchor in c.Anchors)
                        check(name + "-pair-anchor-" + anchor[1], pair.Mapping.Any(row => row[0] == anchor[0] && row[1] == anchor[1]), pair.Raw);
                    if (c.Identity) check(name + "-pair-identity", pair.Mapping.All(row => row[0].HasValue && row[0] == row[1]), pair.Raw);
                }
                var triple = await Compare(name + "-three", c.Texts, c.Flags, 1);
                if (triple is null) continue;
                var protectedAnchors = c.Anchors.Select(anchor => (Side: c.Side, Source: anchor[0], Base: anchor[1])).ToList();
                if (!c.Identity)
                    protectedAnchors.AddRange(new[] { (Side: 2 - c.Side, Source: 2, Base: 2), (Side: 2 - c.Side, Source: 4, Base: 3), (Side: 2 - c.Side, Source: 5, Base: 4) });
                foreach (var anchor in protectedAnchors)
                    check(name + "-three-anchor-" + anchor.Side + "-" + anchor.Base,
                        triple.Mapping.Any(row => row[anchor.Side] == anchor.Source && row[1] == anchor.Base), triple.Raw);
                if (c.Identity) check(name + "-three-unchanged-identity", triple.Mapping
                    .Where(row => row[c.Side].HasValue || row[1].HasValue).All(row => row[c.Side].HasValue && row[c.Side] == row[1]), triple.Raw);
                var project = Path.Combine(folder, name + ".json");
                var settings = new Dictionary<string, object?>
                {
                    ["mode"] = "Table", ["leftPath"] = triple.Paths[0], ["basePath"] = triple.Paths[1], ["rightPath"] = triple.Paths[2],
                    ["tableDelimiter"] = ",", ["ignoreCase"] = c.IgnoreCase, ["ignoreWhitespace"] = c.IgnoreWhitespace
                };
                if (c.Pattern is not null) settings["substitutionRules"] = new[]
                {
                    new { pattern = c.Pattern, replacement = "k", matchCase = true, useRegex = true, wholeWord = false, enabled = true }
                };
                await File.WriteAllTextAsync(project, JsonSerializer.Serialize(settings), new UTF8Encoding(false));
                var before = await File.ReadAllBytesAsync(project);
                var report = Path.Combine(reports, name + ".html");
                await run(name + "-report", 0, false, ["--report-project", project, report]);
                if (File.Exists(report))
                {
                    var html = await File.ReadAllTextAsync(report);
                    var map = HtmlMapping(html, 3, check, name);
                    check(name + "-html-map", SameMap(triple.Mapping, map), Describe(triple.Mapping, map));
                    CheckRows(name + "-html", map, c.Texts);
                    foreach (var anchor in protectedAnchors)
                    {
                        var sideName = anchor.Side == 0 ? "left" : "right";
                        var cells = Regex.Matches(html, "<td\\b(?<attrs>[^>]*)>").Cast<Match>().Where(cell =>
                            Attribute(cell.Groups["attrs"].Value, "data-side") == sideName
                            && Attribute(cell.Groups["attrs"].Value, "data-row") == anchor.Source.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        check(name + "-html-equal-cell-" + sideName + "-" + anchor.Base, cells.Length == 1 && cells.All(cell =>
                            Attribute(cell.Groups["attrs"].Value, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("Equal")
                            && Attribute(cell.Groups["attrs"].Value, "data-missing") == "false"),
                            $"side={sideName}, source={anchor.Source}, ancestor={anchor.Base}; cells={string.Join(';', cells.Select(cell => cell.Value))}");
                        check(name + "-html-anchor-" + sideName + "-" + anchor.Base,
                            map.Any(row => row[anchor.Side] == anchor.Source && row[1] == anchor.Base), Describe(triple.Mapping, map));
                    }
                }
                else check(name + "-html-exists", false, report);
                var after = await File.ReadAllBytesAsync(project);
                check(name + "-project-preserved", before.SequenceEqual(after), project);
                CheckBytes(name + "-report", triple.Paths, triple.Bytes);
                // Fallback の保証は原行完全性のみ。通常時のanchor強度は課さない。
                var fallback = await Compare(name + "-zero-budget", c.Texts, [.. c.Flags, "--max-work", "0"], 1);
                if (fallback is not null) check(name + "-fallback", fallback.Fallback && fallback.WorkUsed == 0
                    && !string.IsNullOrWhiteSpace(fallback.Reason), fallback.Raw);
            }
        }

        string Write(string name, string text)
        {
            var path = Path.Combine(folder, name + ".csv");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        void CheckBytes(string name, string[] paths, byte[][] before)
            => check(name + "-inputs-preserved", paths.Select((path, side) => before[side].SequenceEqual(File.ReadAllBytes(path))).All(value => value), "All original bytes preserved.");

        void CheckRows(string name, int?[][] map, string[] texts)
        {
            for (var side = 0; side < texts.Length; side++)
            {
                var actual = map.Select(row => row.Length > side ? row[side] : null).Where(row => row.HasValue).Select(row => row!.Value).ToArray();
                var count = LogicalRows(texts[side]);
                check(name + "-rows-side-" + side, actual.SequenceEqual(Enumerable.Range(1, count)),
                    $"expected source rows 1..{count}; actual={string.Join(',', actual)}");
            }
        }

        async Task<Snapshot?> Compare(string name, string[] texts, string[] flags, int exit)
        {
            var paths = texts.Select((text, side) => Write(name + "-" + side, text)).ToArray();
            var before = paths.Select(File.ReadAllBytes).ToArray();
            var arguments = new List<string> { "--table", paths[0], paths[^1] };
            if (texts.Length == 3) arguments.AddRange(["--base", paths[1]]);
            arguments.AddRange(flags);
            var result = await run(name, exit, true, arguments.ToArray());
            CheckBytes(name, paths, before);
            try
            {
                using var parsed = JsonDocument.Parse(result.Stdout);
                var root = parsed.RootElement;
                var sides = texts.Length == 3 ? new[] { "left", "base", "right" } : ["left", "right"];
                var map = root.GetProperty("mapping").EnumerateArray().Select(row => sides.Select(side =>
                    row.GetProperty(side).ValueKind == JsonValueKind.Null ? (int?)null : row.GetProperty(side).GetInt32()).ToArray()).ToArray();
                check(name + "-status", result.ExitCode == exit && root.GetProperty("different").GetBoolean() == (exit == 1)
                    && root.GetProperty("alignedRows").GetInt32() == map.Length, result.Stdout);
                var fallback = root.GetProperty("alignmentFallback").GetBoolean();
                var reason = root.GetProperty("alignmentFallbackReason").GetString();
                var used = root.GetProperty("alignmentWorkUsed").GetInt32();
                check(name + "-fallback-contract", used >= 0 && (fallback ? !string.IsNullOrWhiteSpace(reason) : reason == null), result.Stdout);
                CheckRows(name, map, texts);
                return new(map, fallback, reason, used, paths, before, result.Stdout);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
            { check(name + "-json-contract", false, error.Message + ": " + result.Stdout); return null; }
        }
    }

    private sealed record Snapshot(int?[][] Mapping, bool Fallback, string? Reason, int WorkUsed, string[] Paths, byte[][] Bytes, string Raw);
    private sealed record DecodedAnchorCase(string Name, string[] Texts, int Side, string[] Flags, bool IgnoreCase,
        bool IgnoreWhitespace, string? Pattern, int[][] Anchors, bool Identity);
    private static string Attribute(string attributes, string name)
        => Regex.Match(attributes, Regex.Escape(name) + "=\"([^\"]*)\"").Groups[1].Value;

    private static int?[][] HtmlMapping(string html, int count, Action<string, bool, string> check, string name)
    {
        var sides = count == 3 ? new[] { "left", "base", "right" } : ["left", "right"];
        var rows = new List<int?[]>();
        foreach (Match row in Regex.Matches(html, "<tr data-aligned-row=\"(?<aligned>[0-9]+)\">(?<body>.*?)</tr>", RegexOptions.Singleline))
        {
            var cells = Regex.Matches(row.Groups["body"].Value, "<th\\b(?<attrs>[^>]*)>");
            var mapping = new int?[count];
            var found = new HashSet<string>();
            foreach (Match cell in cells)
            {
                string Attr(string attr) => Regex.Match(cell.Groups["attrs"].Value, attr + "=\"([^\"]*)\"").Groups[1].Value;
                var side = Attr("data-side");
                var index = Array.IndexOf(sides, side);
                if (index < 0) continue;
                found.Add(side);
                var source = Attr("data-row");
                mapping[index] = source.Length == 0 ? null : int.Parse(source, System.Globalization.CultureInfo.InvariantCulture);
                check(name + "-html-coordinate-" + rows.Count + "-" + side,
                    Attr("data-aligned-row") == row.Groups["aligned"].Value && Attr("data-missing") == (source.Length == 0 ? "true" : "false"), cell.Value);
            }
            check(name + "-html-side-count-" + rows.Count, found.Count == count && int.Parse(row.Groups["aligned"].Value) == rows.Count + 1, row.Value);
            rows.Add(mapping);
        }
        return rows.ToArray();
    }

    // Fixture の logical 行数だけを独立に数える。quoted 改行と末端 EOL を区別する。
    private static int LogicalRows(string text)
    {
        if (text.Length == 0) return 0;
        var count = 0; var quoted = false; var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                quoted = !quoted;
            }
            if (quoted || text[i] is not ('\r' or '\n')) continue;
            count++;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        return count + (start < text.Length ? 1 : 0);
    }

    private static bool SameMap(int?[][] a, int?[][] b) => a.Length == b.Length && a.Zip(b).All(pair => pair.First.SequenceEqual(pair.Second));
    private static string Describe(int?[][] a, int?[][] b) => $"expected={JsonSerializer.Serialize(a)}; actual={JsonSerializer.Serialize(b)}";
    private static bool SuccessJson(string text)
    {
        try { using var document = JsonDocument.Parse(text); return document.RootElement.TryGetProperty("mapping", out _); }
        catch (JsonException) { return false; }
    }
    private static string FindFixtures()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "Fixtures", "LineAlignment");
            if (File.Exists(Path.Combine(path, "legacy-line-golden.json"))) return path;
        }
        throw new FileNotFoundException("LineAlignment fixtures were not found in the repository.");
    }
}
