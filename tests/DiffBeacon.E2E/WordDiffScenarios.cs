using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class WordDiffScenarios
{
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var goldenPath = FindGolden();
        var goldenBytes = await File.ReadAllBytesAsync(goldenPath);
        check("word-diff-golden-source", Convert.ToHexString(SHA256.HashData(goldenBytes)) ==
            "458337D4F51C4F89D4E131F5DFBB7F0BA08956B0E31146D87B986BC541816121", goldenPath);
        using var golden = JsonDocument.Parse(goldenBytes);
        var all = golden.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var valid = Enumerable.Range(0, all.Length).Where(i => all[i].GetProperty("name").GetString() != "unpaired").ToArray();
        var selected = new SortedSet<int>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in valid)
        {
            var c = all[i];
            if (unique.Add(c.GetProperty("name").GetString() + ":" + c.GetProperty("ranges").GetRawText())) selected.Add(i);
            if (c.GetProperty("matchCase").GetBoolean() && !c.GetProperty("ignoreNumbers").GetBoolean()
                && c.GetProperty("whitespace").GetInt32() == 0 && c.GetProperty("eol").GetInt32() == 0
                && c.GetProperty("breakType").GetInt32() == 1 && c.GetProperty("separators").GetString() == ",.;:") selected.Add(i);
        }
        var names = valid.Select(i => all[i].GetProperty("name").GetString()!).Distinct().ToArray();
        // 全 EOL/空白/区切り設定を、case と numbers を同時に指定して確認する。
        for (var white = 0; white < 3; white++)
        for (var eol = 0; eol < 3; eol++)
        for (var breaks = 0; breaks < 3; breaks++)
        {
            var ordinal = white * 9 + eol * 3 + breaks;
            var name = names[ordinal % names.Length];
            var i = valid.First(index =>
            {
                var c = all[index];
                return c.GetProperty("name").GetString() == name && !c.GetProperty("matchCase").GetBoolean()
                    && c.GetProperty("ignoreNumbers").GetBoolean() && c.GetProperty("whitespace").GetInt32() == white
                    && c.GetProperty("eol").GetInt32() == eol && c.GetProperty("characterLevel").GetBoolean() == (ordinal % 2 == 0)
                    && c.GetProperty("breakType").GetInt32() == (breaks == 0 ? 0 : 1)
                    && c.GetProperty("separators").GetString() == (breaks == 2 ? "=_,;" : ",.;:");
            });
            selected.Add(i);
        }
        check("word-diff-golden-coverage", all.Length == 6048 && valid.Length == 5832 && unique.Count == 55 && selected.Count == 135,
            $"all={all.Length}; valid={valid.Length}; distinct={unique.Count}; selected={selected.Count}");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "word-diff-selection.json"),
            JsonSerializer.Serialize(selected.Select(i => new { index = i, fixture = all[i] }), new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        var folder = Path.Combine(fixtures, "word-diff");
        Directory.CreateDirectory(folder);
        foreach (var i in selected)
        {
            var c = all[i];
            var name = "word-diff-golden-" + i.ToString("D4") + "-" + c.GetProperty("name").GetString();
            var expected = c.GetProperty("ranges").EnumerateArray().Select(r =>
            {
                var p = r.EnumerateArray().Select(n => n.GetInt32()).ToArray();
                return new[] { p[0], p[1] - p[0] + 1, p[2], p[3] - p[2] + 1 };
            }).ToArray();
            var flags = new List<string>();
            if (!c.GetProperty("characterLevel").GetBoolean()) flags.Add("--word-level");
            if (!c.GetProperty("matchCase").GetBoolean()) flags.Add("--ignore-case");
            if (c.GetProperty("ignoreNumbers").GetBoolean()) flags.Add("--ignore-numbers");
            flags.AddRange(["--whitespace", new[] { "none", "changes", "all" }[c.GetProperty("whitespace").GetInt32()],
                "--eol", new[] { "strict", "ignore", "space" }[c.GetProperty("eol").GetInt32()]]);
            if (c.GetProperty("breakType").GetInt32() == 0) flags.Add("--no-separators");
            else if (c.GetProperty("separators").GetString() != ",.;:") flags.AddRange(["--separators", c.GetProperty("separators").GetString()!]);
            // 全入力の通常文字比較は全オプションを省略して既定値も実測する。
            if (c.GetProperty("characterLevel").GetBoolean() && c.GetProperty("matchCase").GetBoolean()
                && !c.GetProperty("ignoreNumbers").GetBoolean() && c.GetProperty("whitespace").GetInt32() == 0
                && c.GetProperty("eol").GetInt32() == 0 && c.GetProperty("breakType").GetInt32() == 1
                && c.GetProperty("separators").GetString() == ",.;:") flags.Clear();
            await Compare(name, c.GetProperty("left").GetString()!, c.GetProperty("right").GetString()!, flags.ToArray(), expected, null);
        }
        var inlineLeft = Write("word-diff-inline-left", "alpha old omega old end\n");
        var inlineRight = Write("word-diff-inline-right", "alpha new omega new end\n");
        var inline = await run("word-diff-inline-text", 1, true, ["--compare", inlineLeft, inlineRight]);
        using (var parsed = JsonDocument.Parse(inline.Stdout))
        {
            var root = parsed.RootElement;
            var row = root.GetProperty("rows")[0];
            var expectedSpans = "[[6,3],[16,3]]";
            check("word-diff-inline-text-spans", row.GetProperty("leftSpans").GetRawText().Replace(" ", "").Replace("\r", "").Replace("\n", "") == expectedSpans
                && row.GetProperty("rightSpans").GetRawText().Replace(" ", "").Replace("\r", "").Replace("\n", "") == expectedSpans, inline.Stdout);
            check("word-diff-inline-text-work", root.GetProperty("inlineWorkUsed").GetInt32() is > 0 and <= 4_000_000
                && root.GetProperty("inlineFallbackCount").GetInt32() == 0, inline.Stdout);
        }
        var inlineHtml = Path.Combine(folder, "word-diff-inline.html");
        await run("word-diff-inline-report", 0, false, ["--report", inlineLeft, inlineRight, inlineHtml]);
        var html = await File.ReadAllTextAsync(inlineHtml);
        check("word-diff-inline-report-spans", html.Split("<span class=\"inline-diff\">old</span>").Length == 3
            && html.Split("<span class=\"inline-diff\">new</span>").Length == 3, inlineHtml);
        // 原本のブロック呼出を実行した追加probeの inclusive区間をUTF-16長へ変換。
        await Compare("word-diff-wrapped-primitive", "one two\nthree", "one\ntwo three", [],
            [[3, 0, 3, 4], [4, 4, 8, 0]], null);
        var wrappedLeft = Write("word-diff-wrapped-left", "one two\nthree");
        var wrappedRight = Write("word-diff-wrapped-right", "one\ntwo three");
        var wrapped = await run("word-diff-wrapped-text", 1, true, ["--compare", wrappedLeft, wrappedRight]);
        using (var parsed = JsonDocument.Parse(wrapped.Stdout))
        {
            var rows = parsed.RootElement.GetProperty("rows");
            static string Spans(JsonElement row, string side) => row.GetProperty(side).GetRawText()
                .Replace(" ", "").Replace("\r", "").Replace("\n", "");
            check("word-diff-wrapped-projection", rows.GetArrayLength() == 2
                && Spans(rows[0], "leftSpans") == "[[4,3]]" && Spans(rows[0], "rightSpans") == "[]"
                && Spans(rows[1], "leftSpans") == "[]" && Spans(rows[1], "rightSpans") == "[[0,3]]", wrapped.Stdout);
        }
        var budgetLeft = Write("word-diff-budget-left", string.Concat(Enumerable.Repeat("alpha old omega old end\n", 8)));
        var budgetRight = Write("word-diff-budget-right", string.Concat(Enumerable.Repeat("alpha new omega new end\n", 8)));
        var budget = await run("word-diff-inline-shared-budget", 1, true, ["--compare", budgetLeft, budgetRight, "--max-work", "80"]);
        using (var parsed = JsonDocument.Parse(budget.Stdout))
        {
            var root = parsed.RootElement;
            check("word-diff-inline-budget-shared-between-rows", root.GetProperty("inlineWorkUsed").GetInt32() is >= 0 and <= 80
                && root.GetProperty("inlineFallbackCount").GetInt32() > 0 && root.GetProperty("rows").GetArrayLength() == 8, budget.Stdout);
        }
        check("word-diff-inline-inputs-preserved", File.ReadAllText(inlineLeft) == "alpha old omega old end\n"
            && File.ReadAllText(inlineRight) == "alpha new omega new end\n", "CLI/report retain source content.");
        await Compare("word-diff-zero-budget", "alpha", "beta", ["--max-work", "0"], [[0, 5, 0, 4]], "work-limit:separators");
        var tokens = string.Concat(Enumerable.Repeat("a ", 11000));
        await Compare("word-diff-token-limit", tokens, "b", [], [[0, tokens.Length, 0, 1]], "token-limit");
        var word = new string('a', 1_100_000);
        await Compare("word-diff-large-word", word, "b", [], [[0, word.Length, 0, 1]], "work-limit:");
        var left = Write("word-diff-errors-left", "keep left\r\n😀");
        var right = Write("word-diff-errors-right", "keep right\r\n😀");
        var beforeLeft = await File.ReadAllBytesAsync(left);
        var beforeRight = await File.ReadAllBytesAsync(right);
        var nulInput = Write("word-diff-nul-rejected", "a\0b");
        var nulBefore = await File.ReadAllBytesAsync(nulInput);
        var errors = new (string Name, string[] Arguments)[]
        {
            ("unknown-flag", ["--word-diff", left, right, "--unknown-word-option"]),
            ("missing-right", ["--word-diff", left]),
            ("missing-budget", ["--word-diff", left, right, "--max-work"]),
            ("missing-separators", ["--word-diff", left, right, "--separators"]),
            ("negative-budget", ["--word-diff", left, right, "--max-work", "-1"]),
            ("excessive-budget", ["--word-diff", left, right, "--max-work", "8000001"]),
            ("unknown-eol", ["--word-diff", left, right, "--eol", "unknown"]),
            ("unknown-whitespace", ["--word-diff", left, right, "--whitespace", "unknown"]),
            ("nul-text", ["--word-diff", nulInput, right])
        };
        foreach (var error in errors)
        {
            var result = await run("word-diff-error-" + error.Name, 2, false, error.Arguments);
            check(result.Name + "-rejected", result.ExitCode == 2 && !string.IsNullOrWhiteSpace(result.Stderr)
                && !HasSuccessJson(result.Stdout), result.Stderr);
            var afterLeft = await File.ReadAllBytesAsync(left);
            var afterRight = await File.ReadAllBytesAsync(right);
            check(result.Name + "-inputs-preserved", beforeLeft.SequenceEqual(afterLeft)
                && beforeRight.SequenceEqual(afterRight), "Original input bytes remain unchanged.");
        }
        var nulAfter = await File.ReadAllBytesAsync(nulInput);
        check("word-diff-nul-input-preserved", nulBefore.SequenceEqual(nulAfter), nulInput);

        string Write(string name, string text)
        {
            var path = Path.Combine(folder, name + ".txt");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        async Task Compare(string name, string l, string r, string[] flags, int[][] expected, string? reason)
        {
            var lp = Write(name + "-left", l);
            var rp = Write(name + "-right", r);
            var lb = await File.ReadAllBytesAsync(lp);
            var rb = await File.ReadAllBytesAsync(rp);
            var result = await run(name, expected.Length == 0 ? 0 : 1, true, ["--word-diff", lp, rp, .. flags]);
            try
            {
                using var parsed = JsonDocument.Parse(result.Stdout);
                var root = parsed.RootElement;
                var actual = root.GetProperty("ranges").EnumerateArray().Select(range => range.EnumerateArray().Select(value => value.GetInt32()).ToArray()).ToArray();
                var equal = expected.Length == actual.Length && expected.Zip(actual).All(pair => pair.First.SequenceEqual(pair.Second));
                check(name + "-ranges", equal, $"expected={JsonSerializer.Serialize(expected)}; actual={JsonSerializer.Serialize(actual)}");
                check(name + "-status", result.ExitCode == (expected.Length == 0 ? 0 : 1)
                    && root.GetProperty("different").GetBoolean() == (expected.Length != 0)
                    && root.GetProperty("fallback").GetBoolean() == (reason != null)
                    && (reason == "work-limit:" ? root.GetProperty("fallbackReason").GetString()?.StartsWith(reason, StringComparison.Ordinal) == true
                        : root.GetProperty("fallbackReason").GetString() == reason), result.Stdout);
                check(name + "-encoding", root.GetProperty("leftEncoding").GetString() == "utf-8"
                    && root.GetProperty("rightEncoding").GetString() == "utf-8", result.Stdout);
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                check(name + "-json-contract", false, error.Message + ": " + result.Stdout);
            }
            var la = await File.ReadAllBytesAsync(lp);
            var ra = await File.ReadAllBytesAsync(rp);
            check(name + "-inputs-preserved", lb.SequenceEqual(la)
                && rb.SequenceEqual(ra), "Original input bytes remain unchanged.");
        }
    }

    private static bool HasSuccessJson(string stdout)
    {
        try { using var document = JsonDocument.Parse(stdout); return document.RootElement.TryGetProperty("different", out _); }
        catch (JsonException) { return false; }
    }

    private static string FindGolden()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "Fixtures", "WordDiffs", "legacy-worddiff-golden.json");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("WordDiff golden fixture was not found in the repository.");
    }
}
