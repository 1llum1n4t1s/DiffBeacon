using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class LineTransformReplaceScenarios
{
    private sealed record Inputs(string Left, string Right, byte[] LeftBytes, byte[] RightBytes);
    private sealed record Rule(string Expression, int Panes);

    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Replace fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformReplace");
        var work = Path.GetFullPath(Path.Combine(fixtures, "line-transform-replace"));
        RequireExternal(work, repository.FullName);
        if (File.Exists(work) || Directory.Exists(work)) throw new IOException("Replace E2E requires fresh task output.");
        Directory.CreateDirectory(work);
        var sources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        // 失敗想定を実行前に保持する。正本文脈と物理fileの派生検証を混同しない。
        await WriteSource(Path.Combine(work, "failure-plan.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            failures = new[] { "typed coercion/array serialization differs", "Unicode CI differs", "literal empty optimized branch differs from dynamic/noopt",
                "overlapping replacement or dollar expansion differs", "generated CR/LF or NUL accepted", "arity accepted",
                "original lines/bytes overwritten", "shared budget resets per rule", "report/project/package loses rule or original assets" },
            limits = "256 rules; 65536 characters per expression; 1048576 total expression characters; default shared 128MiB work/64MiB output",
            ruleCountRejection = "257-rule project import: generic invalid line-transform project error; 257 explicit CLI flags: public 256-rule count error. Both preserve inputs and produce no partial stdout.",
            cancellation = "CLI runner has no cancellation injection; cancellation is not verified by this family",
            scope = "CLI context subset plus separately named literal-derived probes; not the 100-row App diagnostics contract"
        }, jsonOptions));
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "fixed-manifest.json")));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = file.GetProperty("path").GetString()!;
            var path = Path.GetFullPath(Path.Combine(reference, name)); Within(path, reference);
            var bytes = await File.ReadAllBytesAsync(path);
            check("replace-fixed-source-" + name, bytes.LongLength == file.GetProperty("size").GetInt64()
                && Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Fixed raw SHA and size.");
        }
        check("replace-fixed-source-commit", manifest.RootElement.GetProperty("sourceCommit").GetString() == "6780924ba35f7f55af0f6f14dd302561175830a6", "Fixed original Git commit.");
        using var plan = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "public-case-plan.json")));
        check("replace-fixed-original-nodes-sha", plan.RootElement.GetProperty("originalNodesSha256").GetString()
            == "A2FCCA950BD5E5CFA48F3FF43E4436AE0F5FD55EFF9661DA1D5A979CC9051135", "Fixed original node metadata, independently included in manifest checks.");
        var golden = File.ReadLines(Path.Combine(reference, "serialized-golden.ndjson"))
            .Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => { using var parsed = JsonDocument.Parse(line); return parsed.RootElement.Clone(); })
            .ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
        check("replace-fixed-100-inputs", plan.RootElement.GetProperty("cases").GetArrayLength() == 100 && golden.Count == 100,
            "Corpus completeness only; does not claim 100 physical CLI contexts.");
        var selection = new List<object>();
        var faithful = 0; var derived = 0;
        foreach (var test in plan.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = test.GetProperty("id").GetString()!;
            var context = test.GetProperty("context");
            var classification = test.GetProperty("oracleMatchedOrBoundary").GetString()!;
            var expression = test.GetProperty("expression").ValueKind == JsonValueKind.String ? test.GetProperty("expression").GetString() : null;
            var original = Convert.FromHexString(context.GetProperty("sourceLineHex").GetString()!);
            var exact = expression is not null && classification == "oracle-match-candidate" && original.Length > 0
                && context.GetProperty("sourceLineCount").GetInt32() == 1 && context.GetProperty("rowIndex").GetInt32() == 0
                && context.GetProperty("outerEol").GetInt32() == 0 && !context.GetProperty("tableMode").GetBoolean()
                && context.GetProperty("pane").GetString() == "Left" && context.GetProperty("targetMask").GetString() == "Left"
                && !original.Any(value => value is 0 or 10 or 13);
            var status = exact ? "cli-context-selected" : expression is null ? "unrepresentable-nonexecution" : "cli-context-unavailable";
            var reason = exact ? "Nonempty EOF line preserves fixed count/row/EOL/getter/pane context."
                : expression is null ? test.GetProperty("reason").GetString()!
                : "Fixed EOF empty string[1] is not a zero-byte file (zero physical rows); no retargeting of this original ID.";
            selection.Add(new { id, classification, expression, context = context.Clone(), status, reason });
            if (exact)
            {
                faithful++;
                var expected = Convert.FromHexString(golden[id].GetProperty("serializedUtf8Hex").GetString()!);
                var input = await Create("fixed-" + id, original, expected);
                await Compare("fixed-" + id, input, false, expression!);
            }
            // getterのない固定literal式だけを別文脈へ投影。正本IDの実行とは主張しない。
            if (expression is null || context.GetProperty("kind").GetString() != "literal-no-getter" || classification != "oracle-match-candidate") continue;
            var key = Convert.FromHexString(golden[id].GetProperty("serializedUtf8Hex").GetString()!);
            if (key.Any(value => value is 0 or 10 or 13)) continue;
            var probe = await Create("derived-" + id, Utf8("physical probe\r\n"), key.Concat(Utf8("\r\n")).ToArray());
            await Compare("derived-" + id, probe, false, expression);
            derived++;
            await WriteSource(Path.Combine(work, "derived-" + id + "-context.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                probeId = "derived-" + id, expectedSerializedBytesFrom = id, expression, originalContext = context.Clone(),
                actualContext = new { sourceLineHex = Convert.ToHexString(probe.LeftBytes), sourceLineCount = 1, rowIndex = 0, outerEol = 2, tableMode = false, pane = "Left", targetMask = "Left" },
                reason = "Literal expression has no getter. CRLF physical probe is a separate context; C++ serializer bytes are copied directly, with the actual outer CRLF appended."
            }, jsonOptions));
        }
        await WriteSource(Path.Combine(work, "cli-context-selection.ndjson"), Utf8(string.Join("\n", selection.Select(value => JsonSerializer.Serialize(value, jsonOptions))) + "\n"));
        check("replace-cli-context-subset", faithful == 1 && derived > 60, "Actual faithful contexts=" + faithful + "; separate literal-derived probes=" + derived);

        var simple = await Create("physical", Utf8("old\r\n"), Utf8("new\r\n"));
        await Compare("physical-line", simple, false, "@noopt Replace(Line,\"old\",\"new\")");
        var dynamicEmpty = await Create("dynamic-empty-noopt", Utf8("abc\r\n"),
            Convert.FromHexString(golden["empty-needle-cs"].GetProperty("serializedUtf8Hex").GetString()!).Concat(Utf8("\r\n")).ToArray());
        await Compare("dynamic-empty-noopt", dynamicEmpty, false, "@cs @noopt Replace(Line,\"\",\"X\")");
        // 公開境界は既存の実在行で実測する。原典のEOF空行contextの代替ではない。
        foreach (var test in plan.RootElement.GetProperty("cases").EnumerateArray().Where(test =>
            test.GetProperty("expression").ValueKind == JsonValueKind.String && test.GetProperty("oracleMatchedOrBoundary").GetString()
                is "constructor-boundary" or "generated-crlf-boundary" or "nul-literal-boundary" or "optimizer-error-boundary-pending"))
            await Reject("derived-boundary-" + test.GetProperty("id").GetString(), simple, [new(test.GetProperty("expression").GetString()!, 1)]);
        await Reject("arity-four-public", simple, [new("@noopt Replace(\"a\",\"a\",\"X\",\"extra\")", 1)]);

        var table = await Create("table", Utf8("\"old,part\",keep\r\n"), Utf8("\"new,part\",keep\r\n"));
        const string tableExpression = "@cs @noopt Replace(Column1,\"old\",\"new\")";
        await Compare("table-column", table, true, tableExpression);
        await Projects(table, tableExpression);
        // 256は正式rule上限ちょうど。257拒否と実共有budget拒否を別々に検証する。
        await Reject("rule-count-257", simple, Enumerable.Repeat(new Rule("Replace(Line,\"old\",\"new\")", 1), 257).ToArray(), "比較プロジェクトの行変換が不正です。");
        var large = Utf8(new string('a', 262144) + "\r\n" + new string('a', 262144) + "\r\n");
        var budgetInput = await Create("shared-budget", large, large);
        await Reject("shared-budget-256", budgetInput, Enumerable.Repeat(new Rule("@noopt Replace(Line,\"a\",\"a\")", 1), 256).ToArray(), "行変換の共有処理量または生成文字数");
        foreach (var source in sources)
            check("replace-source-sha-" + Path.GetFileName(source.Key), File.Exists(source.Key)
                && SHA256.HashData(await File.ReadAllBytesAsync(source.Key)).SequenceEqual(SHA256.HashData(source.Value)), "Complete original bytes preserved: " + source.Key);

        async Task WriteSource(string path, byte[] bytes)
        {
            Within(path, work);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await stream.WriteAsync(bytes);
            sources.Add(path, bytes);
        }
        async Task<Inputs> Create(string name, byte[] left, byte[] right)
        {
            var lp = Path.Combine(work, name + "-left.txt"); var rp = Path.Combine(work, name + "-right.txt");
            await WriteSource(lp, left); await WriteSource(rp, right); return new(lp, rp, left, right);
        }
        async Task Compare(string name, Inputs input, bool tableMode, string expression)
        {
            var result = await run("replace-" + name, 0, true,
                [tableMode ? "--table" : "--compare", input.Left, input.Right, "--line-transform", tableMode ? "all" : "left", expression]);
            using var parsed = Parse(result); if (parsed is null) return;
            check(result.Name + "-classification", result.ExitCode == 0 && !parsed.RootElement.GetProperty("different").GetBoolean(), "Golden serialized bytes produce equal comparison keys.");
            if (tableMode) return;
            var rows = parsed.RootElement.GetProperty("rows").EnumerateArray().ToArray();
            foreach (var (side, bytes) in new[] { ("left", input.LeftBytes), ("right", input.RightBytes) })
            {
                var actual = rows.Where(row => row.TryGetProperty(side + "Line", out _)).ToArray();
                var expected = Lines(Encoding.UTF8.GetString(bytes));
                check(result.Name + "-original-" + side, actual.Select(row => row.GetProperty(side + "Line").GetInt32()).SequenceEqual(Enumerable.Range(1, expected.Length))
                    && actual.Select(row => row.GetProperty(side).GetString()).SequenceEqual(expected), "Original row text and indexes retained.");
            }
        }
        async Task Reject(string name, Inputs input, Rule[] rules, string? reason = null)
        {
            var project = Path.Combine(work, name + "-project.json");
            await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Text", leftPath = input.Left, rightPath = input.Right, lineTransformRules = rules }, jsonOptions));
            var html = Path.Combine(work, name + "-existing.html"); var sentinel = Utf8("existing Replace report\r\n");
            await WriteSource(html, sentinel);
            var result = await run("replace-reject-report-" + name, 2, false, ["--report-project", project, html]);
            check(result.Name + "-atomic", result.ExitCode == 2 && string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr)
                && (reason is null || result.Stderr.Contains(reason, StringComparison.Ordinal)), "No partial stdout; public rejection reason=" + (reason ?? "nonempty stderr"));
            check(result.Name + "-output-sha", File.Exists(html) && (await File.ReadAllBytesAsync(html)).SequenceEqual(sentinel), "Complete existing HTML unchanged.");
            // NULをOS argvへ渡さない。project JSON経路では正確な式を渡せる。
            if (rules.Length == 1 && !rules[0].Expression.Contains('\0'))
            {
                var cli = await run("replace-reject-cli-" + name, 2, false, ["--compare", input.Left, input.Right, "--line-transform", "left", rules[0].Expression]);
                check(cli.Name + "-atomic", cli.ExitCode == 2 && string.IsNullOrWhiteSpace(cli.Stdout) && !string.IsNullOrWhiteSpace(cli.Stderr), "Public CLI rejection without partial JSON.");
            }
            // project importの例外包装と公開CLIの件数検証を区別する。この境界だけ多重flagsで実行する。
            if (name == "rule-count-257")
            {
                var arguments = new List<string> { "--compare", input.Left, input.Right };
                foreach (var rule in rules) arguments.AddRange(["--line-transform", "left", rule.Expression]);
                var cli = await run("replace-reject-cli-" + name, 2, false, arguments.ToArray());
                check(cli.Name + "-atomic", cli.ExitCode == 2 && string.IsNullOrWhiteSpace(cli.Stdout)
                    && cli.Stderr.Contains("行変換は256件まで指定できます。", StringComparison.Ordinal),
                    "257 explicit CLI rules reject at the public count boundary, with no partial JSON.");
            }
        }
        JsonDocument? Parse(CommandResult result)
        {
            try { return JsonDocument.Parse(result.Stdout); }
            catch (JsonException error) { check(result.Name + "-json", false, error.Message); return null; }
        }
        async Task Projects(Inputs input, string expression)
        {
            var project = Path.Combine(work, "table-project.json");
            await WriteSource(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Table", leftPath = input.Left, rightPath = input.Right,
                tableDelimiter = ',', lineTransformRules = new[] { new Rule(expression, 7) } }, jsonOptions));
            var copy = Path.Combine(work, "table-project-copy.json"); var again = Path.Combine(work, "table-project-again.json");
            await run("replace-project-copy", 0, true, ["--project-copy", project, copy]);
            await run("replace-project-recopy", 0, true, ["--project-copy", copy, again]);
            check("replace-project-stable-roundtrip", File.Exists(copy) && File.Exists(again) && File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(again)), "Stable project JSON.");
            if (File.Exists(copy))
            {
                using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(copy));
                check("replace-project-expression-mask", saved.RootElement.GetProperty("lineTransformRules")[0].GetProperty("expression").GetString() == expression
                    && saved.RootElement.GetProperty("lineTransformRules")[0].GetProperty("panes").GetInt32() == 7, "Expression and physical mask persisted.");
            }
            var html = Path.Combine(work, "table-report.html");
            await run("replace-project-report", 0, true, ["--report-project", project, html]); CheckHtml(html, false);
            var overridden = Path.Combine(work, "table-override.html");
            await run("replace-project-report-override", 0, true, ["--report-project", project, overridden, "--line-transform", "all", "Line"]); CheckHtml(overridden, true);
            var package = Path.Combine(work, "table-package.zip");
            await run("replace-package", 0, true, ["--package-project", project, package, "--report"]);
            if (!File.Exists(package)) return;
            using (var zip = ZipFile.OpenRead(package))
            {
                var assets = new List<byte[]>();
                foreach (var entry in zip.Entries.Where(entry => entry.Name.Length > 0))
                { using var stream = entry.Open(); using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer); assets.Add(buffer.ToArray()); }
                check("replace-package-original-assets", assets.Any(bytes => bytes.SequenceEqual(input.LeftBytes)) && assets.Any(bytes => bytes.SequenceEqual(input.RightBytes)), "ZIP independently read; original full assets preserved.");
            }
            var expanded = Path.Combine(work, "expanded");
            await run("replace-package-expand", 0, true, ["--archive-extract", package, expanded]);
            var reopened = Path.Combine(work, "reopened.json");
            await run("replace-package-reopen", 0, true, ["--project-copy", Path.Combine(expanded, "project.json"), reopened]);
            var reloaded = Path.Combine(work, "reloaded.html");
            await run("replace-package-reloaded-report", 0, true, ["--report-project", reopened, reloaded]); CheckHtml(reloaded, false);
            void CheckHtml(string path, bool different)
            {
                var htmlText = File.Exists(path) ? File.ReadAllText(path) : "";
                check("replace-html-" + Path.GetFileName(path), htmlText.Contains("data-different=\"" + (different ? "true" : "false") + "\"", StringComparison.Ordinal)
                    && htmlText.Contains("old", StringComparison.Ordinal) && htmlText.Contains("new", StringComparison.Ordinal)
                    && (different || htmlText.Contains("data-ignored=\"true\"", StringComparison.Ordinal))
                    && Regex.Matches(htmlText, "<td\\b[^>]*><pre>").Count == 4, "Original cells, ignored classification and all rows retained.");
            }
        }
    }
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
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Replace path escapes root.");
        NoLinks(path);
    }
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Replace paths cannot traverse links.");
    }
    private static void RequireExternal(string path, string repository)
    {
        var full = Path.GetFullPath(path); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { repository, Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("Replace E2E requires external output.");
        }
        if (full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase))) throw new IOException("Replace output cannot use an artifacts folder.");
        NoLinks(path);
    }
}
