using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class LineTransformSearchScenarios
{
    private sealed record Inputs(string Left, string Right, byte[] LeftBytes, byte[] RightBytes);
    private sealed record Rule(string Expression, int Panes);

    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Search fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformSearch");
        var work = Path.GetFullPath(Path.Combine(fixtures, "line-transform-search"));
        RequireExternal(work, repository.FullName);
        if (File.Exists(work) || Directory.Exists(work)) throw new IOException("Search E2E requires fresh task output.");
        Directory.CreateDirectory(work);
        var sources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "manifest.json")));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            var path = Path.GetFullPath(Path.Combine(reference, file.Name));
            Within(path, reference);
            var bytes = await File.ReadAllBytesAsync(path);
            check("search-fixed-source-" + file.Name, bytes.LongLength == file.Value.GetProperty("size").GetInt64()
                && Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.Value.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Fixed input SHA and size.");
        }
        using var cases = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "cases.json")));
        var golden = File.ReadLines(Path.Combine(reference, "golden.ndjson")).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => { using var parsed = JsonDocument.Parse(line); return parsed.RootElement.Clone(); })
            .ToDictionary(value => value.GetProperty("id").GetString()!, StringComparer.Ordinal);
        check("search-fixed-expectation-input-complete", cases.RootElement.GetArrayLength() == 68 && golden.Count == 68, "Expected input corpus size; this is not a claim that CLI executes all 68 oracle contexts.");
        // 固定contextをCLIへ捏造しない。選択済みPlainText/Left/mask7だけ別のLeft-only等価比較を作る。
        var selected = 0;
        foreach (var test in cases.RootElement.EnumerateArray())
        {
            var groups = test.GetProperty("groups");
            if (test.GetProperty("table").GetBoolean() || test.GetProperty("pane").GetInt32() != 0
                || groups.GetArrayLength() != 1 || groups[0].GetProperty("mask").GetInt32() != 7) continue;
            var id = test.GetProperty("id").GetString()!;
            if (id.Contains("-arity-", StringComparison.Ordinal)) continue; // Direct AST NONE is distinct from public Compile rejection.
            var expected = golden[id];
            var keys = expected.GetProperty("lines").EnumerateArray().Select(line =>
                Encoding.UTF8.GetString(Convert.FromHexString(line.GetProperty("keyHex").GetString()!))).ToArray();
            var original = test.GetProperty("lines").EnumerateArray().Select(line => line.GetString()!).ToArray();
            // serialized keyが元の物理EOLを末尾に一度だけ保持する普通のtextであることを確かめる。
            if (keys.Length != original.Length || keys.Where((key, index) =>
                !key.EndsWith(Ending(original[index]), StringComparison.Ordinal)
                || key[..(key.Length - Ending(original[index]).Length)].IndexOfAny(['\r', '\n', '\0']) >= 0).Any()) continue;
            selected++;
            var input = await Create(id, Utf8(string.Concat(original)), Utf8(string.Concat(keys)));
            var options = groups[0].GetProperty("expressions").EnumerateArray()
                .SelectMany(expression => new[] { "--line-transform", "left", expression.GetString()! }).ToArray();
            if (expected.GetProperty("status").GetString() == "error") await Reject(id, input, options);
            else await Compare(id, input, false, false, options);
        }
        check("search-selected-cli-contexts", selected == 57,
            "Selected ordinary fixed contexts executed as separate Left-only equivalence; public Plan68 has its own diagnostics/reader.");

        var simple = await Create("source-grounded-opt", Utf8("old\r\n"), Utf8("new\r\n"));
        // 原本は@noopt。以下は旧Optimize bodyのstd::get<long>(none)と検索missに基づく別回帰。
        await Reject("defaultopt-count-empty-needle", simple, ["--line-transform", "all", "StrCount(\"abc\", \"\")"]);
        await Compare("noopt-count-empty-needle-none", simple, false, false,
            ["--line-transform", "all", "@noopt StrCount(\"abc\", \"\")"]);
        await Compare("defaultopt-find-constant-miss-none", simple, false, false,
            ["--line-transform", "all", "StrFind(\"abc\", \"missing\")"]);
        // 旧body直接ASTではNONE、公的Compileはarity拒否: 固定goldenと混同しない境界回帰。
        foreach (var expression in new[] { "StrCount()", "StrCount(Line)", "StrCount(Line, \"a\", 0, 1)",
            "StrFind()", "StrFind(Line)", "StrFind(Line, \"a\", 0, 1)" })
            await Reject("public-arity-" + Convert.ToHexString(SHA256.HashData(Utf8(expression)))[..12], simple,
                ["--line-transform", "all", expression]);
        await Reject("syntax", simple, ["--line-transform", "all", "StrFind(Line,"]);

        var table = await Create("raw-quoted-table", Utf8("A,\"あx,old\"\r\n"), Utf8("A,\"あx,new\"\r\n"));
        await Compare("raw-column2-three-byte-offset", table, true, false,
            ["--line-transform", "all", "@cs @noopt StrFind(Column2, \",\")"]);
        // RAW Column2の開始引用符1byte + あ3byte + x1byte = comma位置5。復号cellでの4ではない。
        var rawOffset = await Create("raw-column2-byte-proof", table.LeftBytes, Utf8("5\r\n"));
        await Compare("raw-column2-quote-and-byte-offset", rawOffset, true, false,
            ["--line-transform", "left", "@cs @noopt StrFind(Column2, \",\")"]);
        var pipeline = await Create("ordered-pipeline", Utf8("aaaa\r\n"), Utf8("0\r\n"));
        await Compare("same-mask-ordered-pipeline", pipeline, false, false,
            ["--line-transform", "left", "@noopt StrCount(Line, \"aa\")", "--line-transform", "left", "@noopt StrFind(Line, \"2\")"]);
        var middle = Path.Combine(work, "physical-middle.txt");
        await WriteSource(middle, Utf8("middle\r\n"));
        foreach (var (pair, scope, expression) in new[] { ("left-middle", "base", "@noopt If(StrCount(Line, \"d\") = 2, \"old\", Line)"),
            ("middle-right", "right", "@noopt If(StrFind(Line, \"new\") = 0, \"middle\", Line)"),
            ("left-right", "left", "@noopt If(StrFind(Line, \"old\") = 0, \"new\", Line)") })
        {
            var result = await run("search-physical-mask-" + pair, 0, true,
                ["--compare", simple.Left, simple.Right, "--middle", middle, "--independent-text", "--pair", pair,
                    "--line-transform", scope, expression]);
            using var parsed = Parse(result);
            if (parsed is not null) check(result.Name + "-physical-classification", !parsed.RootElement.GetProperty("different").GetBoolean()
                && parsed.RootElement.GetProperty("pair").GetString() == (pair == "left-middle" ? "LeftMiddle" : pair == "middle-right" ? "MiddleRight" : "LeftRight"),
                "Actual physical target identity and canonical pair.");
        }
        await Projects(table);
        // 大prefixは現行共有budget内の成功回帰。KMP固有上限/拒否理由は作らない。
        var prefix = new string('a', 4096) + "b";
        var large = await Create("large-prefix", Utf8(new string('a', 131072) + "b\r\n"), Utf8("126976\r\n"));
        await Compare("large-prefix-finite-search", large, false, false,
            ["--line-transform", "left", "@noopt StrFind(Line, \"" + prefix + "\")"]);
        await UnusedPatternAllocation();
        // 実B13 CLIで確認した共有上限拒否。公開stderrは処理量と生成量を区別しない。
        await SharedSearchBudgetRejection();
        foreach (var source in sources)
        {
            var current = File.Exists(source.Key) ? await File.ReadAllBytesAsync(source.Key) : [];
            check("search-source-sha-" + Path.GetFileName(source.Key), SHA256.HashData(source.Value).SequenceEqual(SHA256.HashData(current)), source.Key);
        }

        async Task UnusedPatternAllocation()
        {
            // 未使用KMP patternの生成だけでdefault64MiBを消費した旧prototypeへの実CLI回帰。
            // 256ルールはworkspace JSONで渡す。製品budgetやpublic APIを注入・変更しない。
            var physicalPattern = new string('a', 60000);
            var physicalLeft = "start\r\n" + physicalPattern + "\r\n";
            var caseCount = 0;
            foreach (var function in new[] { "StrCount", "StrFind" })
                foreach (var (sourceValue, expectedKey, suffix) in new[]
                {
                    ("None", "none", "none"), ("Array()", "[]", "empty-array"),
                    ("Array(None, None)", "[none, none]", "all-none-array")
                })
                {
                    var name = "unused-pattern-" + function.ToLowerInvariant() + "-" + suffix;
                    var input = await Create(name, Utf8(physicalLeft), Utf8(expectedKey + "\r\n" + expectedKey + "\r\n"));
                    var expression = "@noopt " + function + "(" + sourceValue + ", LineAt(2))";
                    var rules = Enumerable.Repeat(new Rule(expression, 1), 256).ToArray();
                    var workspace = Path.Combine(work, name + "-workspace.json");
                    await WriteSource(workspace, JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1,
                        entries = new[] { new { mode = "Text", leftPath = input.Left, rightPath = input.Right, lineTransformRules = rules } },
                        activeEntryIndex = 0 }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                    var htmlPath = Path.Combine(work, name + ".html");
                    var sentinel = Utf8("existing unused-pattern report\r\n");
                    await File.WriteAllBytesAsync(htmlPath, sentinel);
                    // ReportCommandsの通常public経路は--max-workを受け付けない。default予算で成功が必要。
                    var result = await run("search-" + name, 0, true, ["--report-project", workspace, htmlPath]);
                    var bytes = File.Exists(htmlPath) ? await File.ReadAllBytesAsync(htmlPath) : [];
                    var html = Encoding.UTF8.GetString(bytes);
                    check(result.Name + "-published-equal-original-report", result.ExitCode == 0 && string.IsNullOrWhiteSpace(result.Stderr)
                        && !bytes.SequenceEqual(sentinel) && html.StartsWith("<!doctype html>", StringComparison.Ordinal)
                        && html.Contains("data-different=\"false\"", StringComparison.Ordinal)
                        && html.Contains("data-ignored=\"true\"", StringComparison.Ordinal),
                        "256 rules with source " + sourceValue + " succeed under actual default budget and replace existing report with equal ignored original rows.");
                    var rows = Regex.Matches(html,
                        "<td data-side=\"(?<side>left|right)\" data-line=\"(?<line>[0-9]+)\"(?<attributes>[^>]*)>.*?<pre>(?<text>.*?)</pre></td>",
                        RegexOptions.Singleline).Cast<Match>().ToArray();
                    foreach (var (side, expectedRows) in new[] { ("left", new[] { "start", physicalPattern }),
                        ("right", new[] { expectedKey, expectedKey }) })
                    {
                        var cells = rows.Where(row => row.Groups["side"].Value == side).ToArray();
                        var texts = cells.Select(row => System.Net.WebUtility.HtmlDecode(Regex.Replace(row.Groups["text"].Value, "<[^>]+>", "")));
                        check(result.Name + "-raw-two-crlf-rows-" + side,
                            cells.Select(row => row.Groups["line"].Value).SequenceEqual(["1", "2"])
                            && texts.SequenceEqual(expectedRows)
                            && cells.All(row => row.Groups["attributes"].Value.Contains("data-ending=\"CRLF\"", StringComparison.Ordinal)
                                && row.Groups["attributes"].Value.Contains("data-missing=\"false\"", StringComparison.Ordinal)),
                            "Independently decoded original report cells, physical line numbers and both CRLF endings; no transformed key replaces raw input.");
                    }
                    foreach (var inputPath in new[] { input.Left, input.Right, workspace })
                        check(result.Name + "-source-sha-" + Path.GetFileName(inputPath), File.Exists(inputPath)
                            && SHA256.HashData(await File.ReadAllBytesAsync(inputPath)).SequenceEqual(SHA256.HashData(sources[inputPath])),
                            inputPath);
                    caseCount++;
                }
            check("search-unused-pattern-six-cases", caseCount == 6, "StrCount/StrFind x None/empty Array/all-none Array, actual default shared budget.");
        }
        async Task SharedSearchBudgetRejection()
        {
            // 非NONE scalarでも検索元1byte/needle60000bytesは通常のpublic経路で評価される。
            // 256ルールと既定予算での共有上限拒否を実測済み。個別counterの先着理由は断定しない。
            var physicalLeft = "start\r\n" + new string('a', 60000) + "\r\n";
            foreach (var function in new[] { "StrCount", "StrFind" })
            {
                var name = "shared-search-budget-" + function.ToLowerInvariant();
                var key = function == "StrCount" ? "0" : "none";
                var input = await Create(name, Utf8(physicalLeft), Utf8(key + "\r\n" + key + "\r\n"));
                var expression = "@noopt " + function + "(\"b\", LineAt(2))";
                var workspace = Path.Combine(work, name + "-workspace.json");
                await WriteSource(workspace, JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1,
                    entries = new[] { new { mode = "Text", leftPath = input.Left, rightPath = input.Right,
                        lineTransformRules = Enumerable.Repeat(new Rule(expression, 1), 256).ToArray() } },
                    activeEntryIndex = 0 }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                var html = Path.Combine(work, name + "-existing.html");
                var sentinel = Utf8("existing shared-search-budget report\r\n");
                await File.WriteAllBytesAsync(html, sentinel);
                var sentinelSha = SHA256.HashData(sentinel);
                var result = await run("search-" + name, 2, false, ["--report-project", workspace, html]);
                check(result.Name + "-shared-limit-rejected", result.ExitCode == 2 && string.IsNullOrWhiteSpace(result.Stdout)
                    && result.Stderr.Contains("行変換の共有処理量または生成文字数", StringComparison.Ordinal),
                    "Actual shared-limit public stderr; work/output counters are not individually attributed.");
                check(result.Name + "-existing-html-sha", File.Exists(html)
                    && SHA256.HashData(await File.ReadAllBytesAsync(html)).SequenceEqual(sentinelSha),
                    "Shared-limit refusal leaves the complete existing HTML sentinel unchanged.");
                foreach (var sourcePath in new[] { input.Left, input.Right, workspace })
                    check(result.Name + "-input-sha-" + Path.GetFileName(sourcePath), File.Exists(sourcePath)
                        && SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)).SequenceEqual(SHA256.HashData(sources[sourcePath])),
                        sourcePath);
            }
        }
        async Task<Inputs> Create(string name, byte[] left, byte[] right)
        {
            var lp = Path.Combine(work, name + "-left.txt"); var rp = Path.Combine(work, name + "-right.txt");
            await WriteSource(lp, left); await WriteSource(rp, right); return new(lp, rp, left, right);
        }
        async Task WriteSource(string path, byte[] bytes)
        {
            await File.WriteAllBytesAsync(path, bytes); sources.Add(path, bytes);
        }
        async Task Compare(string name, Inputs input, bool tableMode, bool different, string[] options)
        {
            var result = await run("search-" + name, different ? 1 : 0, true,
                new[] { tableMode ? "--table" : "--compare", input.Left, input.Right }.Concat(options).ToArray());
            using var parsed = Parse(result);
            if (parsed is null) return;
            var root = parsed.RootElement;
            check(result.Name + "-classification", result.ExitCode == (different ? 1 : 0) && root.GetProperty("different").GetBoolean() == different, "Expected different=" + different);
            if (!tableMode)
            {
                var rows = root.GetProperty("rows").EnumerateArray().ToArray();
                foreach (var (side, original) in new[] { ("left", input.LeftBytes), ("right", input.RightBytes) })
                {
                    var actual = rows.Where(row => row.TryGetProperty(side + "Line", out _)).ToArray();
                    var originalRows = Lines(Encoding.UTF8.GetString(original));
                    check(result.Name + "-original-" + side, actual.Select(row => row.GetProperty(side + "Line").GetInt32()).SequenceEqual(Enumerable.Range(1, originalRows.Length))
                        && actual.Select(row => row.GetProperty(side).GetString()).SequenceEqual(originalRows), "Original line numbers and contents remain unchanged.");
                }
            }
        }
        async Task Reject(string name, Inputs input, string[] options)
        {
            var result = await run("search-reject-" + name, 2, false, new[] { "--compare", input.Left, input.Right }.Concat(options).ToArray());
            check(result.Name + "-atomic", result.ExitCode == 2 && !string.IsNullOrWhiteSpace(result.Stderr) && string.IsNullOrWhiteSpace(result.Stdout), "Product rejection; no partial comparison JSON.");
            var html = Path.Combine(work, "reject-" + name + ".html"); var sentinel = Utf8("existing report\r\n");
            await File.WriteAllBytesAsync(html, sentinel);
            var rejectedRules = new List<Rule>();
            for (var index = 0; index < options.Length; index++)
                if (options[index] == "--line-transform")
                {
                    var mask = options[index + 1] switch { "left" => 1, "base" => 2, "right" => 4, "all" => 7,
                        _ => throw new IOException("Unexpected regression pane scope.") };
                    rejectedRules.Add(new Rule(options[index + 2], mask)); index += 2;
                }
            var project = Path.Combine(work, "reject-" + name + ".json");
            await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Text", leftPath = input.Left, rightPath = input.Right,
                lineTransformRules = rejectedRules }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            var report = await run("search-reject-report-" + name, 2, false, ["--report-project", project, html]);
            check(report.Name + "-preserves-existing-html", report.ExitCode == 2 && string.IsNullOrWhiteSpace(report.Stdout)
                && !string.IsNullOrWhiteSpace(report.Stderr) && File.ReadAllBytes(html).SequenceEqual(sentinel), "Rejected before replacing existing HTML.");
        }
        JsonDocument? Parse(CommandResult result)
        {
            try { return JsonDocument.Parse(result.Stdout); }
            catch (JsonException error) { check(result.Name + "-json", false, "exit=" + result.ExitCode + "; " + error.Message); return null; }
        }
        async Task Projects(Inputs input)
        {
            var rules = new[] { new Rule("@cs @noopt StrFind(Column2, \",\")", 7) };
            object Entry(Rule[] expressions) => new { mode = "Table", leftPath = input.Left, rightPath = input.Right,
                tableDelimiter = ',', lineTransformRules = expressions };
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var project = Path.Combine(work, "project.json"); var workspace = Path.Combine(work, "workspace.json");
            await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(Entry(rules), jsonOptions));
            await WriteSource(workspace, JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, entries = new[] { Entry(rules), Entry([new("@noopt StrCount(Line, \",\")", 7)]) }, activeEntryIndex = 0 }, jsonOptions));
            foreach (var source in new[] { project, workspace })
            {
                var copy = source + ".copy.json"; var again = source + ".again.json";
                await run("search-copy-" + Path.GetFileName(source), 0, true, ["--project-copy", source, copy]);
                await run("search-recopy-" + Path.GetFileName(source), 0, true, ["--project-copy", copy, again]);
                check("search-json-roundtrip-" + Path.GetFileName(source), File.Exists(copy) && File.Exists(again)
                    && File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(again)), "Stable search expression JSON.");
                if (File.Exists(copy))
                {
                    using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(copy));
                    var entry = saved.RootElement.TryGetProperty("entries", out var entries) ? entries[0] : saved.RootElement;
                    check("search-json-expression-" + Path.GetFileName(source), entry.GetProperty("lineTransformRules")[0].GetProperty("expression").GetString() == rules[0].Expression
                        && entry.GetProperty("lineTransformRules")[0].GetProperty("panes").GetInt32() == 7, "Expression and mask preserved.");
                    if (source == workspace) check("search-json-second-entry", entries.GetArrayLength() == 2 && entries[1].GetProperty("lineTransformRules")[0].GetProperty("expression").GetString() == "@noopt StrCount(Line, \",\")"
                        && saved.RootElement.GetProperty("activeEntryIndex").GetInt32() == 0, "Every workspace entry and active position.");
                }
            }
            var html = Path.Combine(work, "report.html");
            await run("search-project-report", 0, true, ["--report-project", project, html]);
            CheckHtml(html, false);
            var overridden = Path.Combine(work, "override.html");
            await run("search-project-report-override", 0, true, ["--report-project", project, overridden, "--line-transform", "all", "Line"]);
            CheckHtml(overridden, true);
            var package = Path.Combine(work, "package.zip");
            await run("search-package", 0, true, ["--package-project", project, package, "--report"]);
            if (File.Exists(package))
            {
                using var zip = ZipFile.OpenRead(package);
                var entries = new List<byte[]>();
                foreach (var entry in zip.Entries.Where(entry => entry.Name.Length > 0))
                { using var stream = entry.Open(); using var buffer = new MemoryStream(); stream.CopyTo(buffer); entries.Add(buffer.ToArray()); }
                check("search-package-original-assets", entries.Any(bytes => bytes.SequenceEqual(input.LeftBytes)) && entries.Any(bytes => bytes.SequenceEqual(input.RightBytes)), "Full original asset bytes, independently read ZIP.");
                var expanded = Path.Combine(work, "expanded");
                await run("search-package-expand", 0, true, ["--archive-extract", package, expanded]);
                var reopened = Path.Combine(work, "reopened.json");
                await run("search-package-reopen", 0, true, ["--project-copy", Path.Combine(expanded, "project.json"), reopened]);
                var reloaded = Path.Combine(work, "reloaded.html");
                await run("search-package-reloaded-report", 0, true, ["--report-project", reopened, reloaded]); CheckHtml(reloaded, false);
            }
            void CheckHtml(string path, bool different)
            {
                var html = File.Exists(path) ? File.ReadAllText(path) : "";
                check("search-html-" + Path.GetFileName(path), html.Contains("data-different=\"" + (different ? "true" : "false") + "\"", StringComparison.Ordinal)
                    && html.Contains("old", StringComparison.Ordinal) && html.Contains("new", StringComparison.Ordinal)
                    && (different || html.Contains("data-ignored=\"true\"", StringComparison.Ordinal))
                    && Regex.Matches(html, "<td\\b[^>]*><pre>").Count == 4, "Original quoted cells, classification and all cells retained.");
            }
        }
    }
    private static string Ending(string text) => text.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
        : text.EndsWith('\r') ? "\r" : text.EndsWith('\n') ? "\n" : "";
    private static byte[] Utf8(string value) => new UTF8Encoding(false).GetBytes(value);
    private static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        var rows = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return text[^1] is '\r' or '\n' ? rows[..^1] : rows;
    }
    private static void Within(string path, string root)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Search fixture/output path escapes root.");
        NoLinks(path);
    }
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Search output cannot traverse links.");
    }
    private static void RequireExternal(string path, string repository)
    {
        var full = Path.GetFullPath(path); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { repository, Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("Search E2E requires external task output.");
        }
        if (full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase))) throw new IOException("Search E2E cannot use an artifacts directory.");
        NoLinks(path);
    }
}
