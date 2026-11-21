using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformContentScenarios
{
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var work = Path.GetFullPath(Path.Combine(fixtures, "line-transform-content"));
        RequireExternal(work);
        if (Directory.Exists(work) || File.Exists(work)) throw new IOException("Fresh content E2E output required.");
        Directory.CreateDirectory(work);
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Repository missing.");
        var fixture = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformContent");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(fixture, "contract.json"));
        var digest = (await File.ReadAllTextAsync(Path.Combine(fixture, "contract.sha256"))).Split(' ')[0];
        check("content fixed contract SHA", Convert.ToHexString(SHA256.HashData(bytes)) == digest, digest);
        using var contract = JsonDocument.Parse(bytes);
        var observations = new List<object>();
        var pathObservations = new List<object>();
        var rejections = new List<object>();
        var originals = new Dictionary<string, byte[]>();
        foreach (var item in contract.RootElement.GetProperty("cases").EnumerateArray())
            foreach (var noopt in new[] { false, true })
            {
                var id = item.GetProperty("id").GetString()! + (noopt ? "-noopt" : "-optimized");
                var source = item.GetProperty("source").GetString()!;
                var expression = item.GetProperty("expression").GetString()!;
                var expected = item.GetProperty("expectedKey").GetString()!;
                var left = await Source(id + "-left", source); var right = await Source(id + "-right", source);
                expected = expected.Replace("<Content>", "<FileContent:" + Path.GetFullPath(left) + ">", StringComparison.Ordinal);
                var table = item.TryGetProperty("mode", out var mode) && mode.GetString() == "Table";
                var arguments = new List<string> { table ? "--table" : "--compare", left, right };
                if (item.TryGetProperty("preExpression", out var pre)) arguments.AddRange(["--line-transform", "left", pre.GetString()!]);
                var caseSensitive = !item.TryGetProperty("caseSensitive", out var caseSensitiveProperty) || caseSensitiveProperty.GetBoolean();
                var caseDirective = caseSensitive ? "@cs " : "@ci ";
                arguments.AddRange(["--line-transform", "left", caseDirective + (noopt ? "@noopt " : "") + expression]);
                if (item.TryGetProperty("postExpression", out var post)) arguments.AddRange(["--line-transform", "left", post.GetString()!]);
                arguments.AddRange(["--line-transform", "right", "If(true," + Quote(expected) + ",Sublines(Content,0))"]);
                var result = await run("content-" + id, 0, true, arguments.ToArray());
                using var json = JsonDocument.Parse(result.Stdout);
                var passed = result.ExitCode == 0 && !json.RootElement.GetProperty("different").GetBoolean();
                check(result.Name + "-key", passed, expression + " => " + JsonSerializer.Serialize(expected));
                if (!table)
                {
                    var rows = json.RootElement.GetProperty("rows").EnumerateArray().ToArray();
                    var physical = PhysicalRows(source);
                    foreach (var side in new[] { "left", "right" })
                    {
                        var actual = rows.Where(row => row.TryGetProperty(side + "Line", out _)).ToArray();
                        check(result.Name + "-physical-" + side, actual.Length == physical.Count
                            && actual.Select(row => row.GetProperty(side).GetString()).SequenceEqual(physical)
                            && actual.Select(row => row.GetProperty(side + "Line").GetInt32()).SequenceEqual(Enumerable.Range(1, physical.Count)),
                            "Opaque keys preserve every original physical row and its number.");
                    }
                }
                observations.Add(new { id, expression, expectedKey = expected, passed, result.ExitCode, stdout = result.Stdout, arguments });
            }
        var rejectLeft = await Source("reject-left", "source\r\n"); var rejectRight = await Source("reject-right", "source\r\n");
        foreach (var item in contract.RootElement.GetProperty("errors").EnumerateArray())
            foreach (var noopt in new[] { false, true })
            {
                var id = item.GetProperty("id").GetString()! + (noopt ? "-noopt" : "-optimized");
                var expression = (noopt ? "@noopt " : "") + item.GetProperty("expression").GetString();
                var result = await run("content-reject-" + id, 2, false,
                    ["--compare", rejectLeft, rejectRight, "--line-transform", "left", expression]);
                check(result.Name + "-atomic", result.ExitCode == 2 && string.IsNullOrWhiteSpace(result.Stdout)
                    && !string.IsNullOrWhiteSpace(result.Stderr), "Evaluation/arity failures emit no partial comparison.");
                rejections.Add(new { id, expression, result.ExitCode, stdout = result.Stdout, stderr = result.Stderr });
            }
        // 三者の各Contentは独立した原文。隣のpaneや変換済みキーへ読みに行かない。
        var threeLeft = await Source("three-left", "LEFT\r\nleft\r\n");
        var threeBase = await Source("three-base", "BASE\r\nbase\r\n");
        var threeRight = await Source("three-right", "RIGHT\r\nright\r\n");
        var three = await run("content-three-pane", 0, true, ["--table", threeLeft, threeRight, "--base", threeBase,
            "--line-transform", "left", "If(Sublines(Content,0,1)=" + Quote("LEFT\r\n") + ",\"same\",\"bad-left\")",
            "--line-transform", "base", "If(Sublines(Content,0,1)=" + Quote("BASE\r\n") + ",\"same\",\"bad-base\")",
            "--line-transform", "right", "If(Sublines(Content,0,1)=" + Quote("RIGHT\r\n") + ",\"same\",\"bad-right\")"]);
        using (var json = JsonDocument.Parse(three.Stdout))
            check(three.Name + "-key", three.ExitCode == 0 && !json.RootElement.GetProperty("different").GetBoolean(), "Three content snapshots agree.");
        var threePathLeft = await Source("three-path-left", "same\r\n");
        var threePathBase = await Source("three-path-base", "same\r\n");
        var threePathRight = await Source("three-path-right", "same\r\n");
        var threePathRules = new List<string>();
        foreach (var (side, sourcePath) in new[] { ("left", threePathLeft), ("base", threePathBase), ("right", threePathRight) })
        {
            var marker = "<FileContent:" + Path.GetFullPath(sourcePath) + ">";
            threePathRules.AddRange(["--line-transform", side,
                "If(\"\"+Content=" + Quote(marker) + ",\"same\",\"bad-" + side + "\")"]);
        }
        foreach (var (id, pair) in new[] { ("left-middle", "left-middle"), ("middle-right", "middle-right") })
        {
            var threePathArguments = new List<string>
            {
                "--compare", threePathLeft, threePathRight, "--independent-text", "--middle", threePathBase, "--pair", pair
            };
            threePathArguments.AddRange(threePathRules);
            var threePaths = await run("content-three-pane-paths-" + id, 0, true, threePathArguments.ToArray());
            var paths = new { left = Path.GetFullPath(threePathLeft), @base = Path.GetFullPath(threePathBase), right = Path.GetFullPath(threePathRight) };
            var threePathPassed = false;
            using (var json = JsonDocument.Parse(threePaths.Stdout))
                threePathPassed = threePaths.ExitCode == 0
                    && !json.RootElement.GetProperty("different").GetBoolean()
                    && json.RootElement.GetProperty("pair").GetString() == (id == "left-middle" ? "LeftMiddle" : "MiddleRight")
                    && json.RootElement.GetProperty("leftPath").GetString() == paths.left
                    && json.RootElement.GetProperty("middlePath").GetString() == paths.@base
                    && json.RootElement.GetProperty("rightPath").GetString() == paths.right;
            check(threePaths.Name + "-exact", threePathPassed, "Independent Text pair uses the exact left/base/right source paths.");
            pathObservations.Add(new
            {
                id = "legacy-three-way-" + id, pair, sourcePaths = paths,
                passed = threePathPassed, threePaths.ExitCode, stdout = threePaths.Stdout,
                arguments = threePathArguments.ToArray()
            });
        }
        var pathLeft = await Source("legacy-marker-left", "path probe\r\n");
        var pathRight = await Source("legacy-marker-right", "path probe\r\n");
        var leftMarker = Quote("<FileContent:" + Path.GetFullPath(pathLeft) + ">");
        var rightMarker = Quote("<FileContent:" + Path.GetFullPath(pathRight) + ">");
        var leftPath = await run("content-legacy-left-path-marker", 0, true,
            ["--compare", pathLeft, pathRight, "--line-transform", "left", "Content",
                "--line-transform", "right", leftMarker]);
        var leftPathPassed = false;
        using (var json = JsonDocument.Parse(leftPath.Stdout))
            leftPathPassed = leftPath.ExitCode == 0 && !json.RootElement.GetProperty("different").GetBoolean();
        check(leftPath.Name + "-exact", leftPathPassed, "Content stringifies to the exact absolute source path for the left pane.");
        pathObservations.Add(new { id = "legacy-left", sourcePath = Path.GetFullPath(pathLeft), expectedMarker = "<FileContent:" + Path.GetFullPath(pathLeft) + ">",
            passed = leftPathPassed, leftPath.ExitCode, stdout = leftPath.Stdout,
            arguments = new[] { "--compare", pathLeft, pathRight, "--line-transform", "left", "Content", "--line-transform", "right", leftMarker } });
        var rightPath = await run("content-legacy-right-path-marker", 0, true,
            ["--compare", pathLeft, pathRight, "--line-transform", "left", rightMarker,
                "--line-transform", "right", "Content"]);
        var rightPathPassed = false;
        using (var json = JsonDocument.Parse(rightPath.Stdout))
            rightPathPassed = rightPath.ExitCode == 0 && !json.RootElement.GetProperty("different").GetBoolean();
        check(rightPath.Name + "-exact", rightPathPassed, "Content stringifies to the exact absolute source path for the right pane.");
        pathObservations.Add(new { id = "legacy-right", sourcePath = Path.GetFullPath(pathRight), expectedMarker = "<FileContent:" + Path.GetFullPath(pathRight) + ">",
            passed = rightPathPassed, rightPath.ExitCode, stdout = rightPath.Stdout,
            arguments = new[] { "--compare", pathLeft, pathRight, "--line-transform", "left", rightMarker, "--line-transform", "right", "Content" } });
        var cachedLeft = await Source("aggregate-cache-left", "a\nb");
        var cachedRight = await Source("aggregate-cache-right", "a\nb");
        var cachedResult = await run("content-all-mask-aggregate-cache", 0, true,
            ["--compare", cachedLeft, cachedRight, "--line-transform", "all", "Sublines(Content,0,1)",
                "--line-transform", "all", "Minimum(Line)"]);
        using (var json = JsonDocument.Parse(cachedResult.Stdout))
            check(cachedResult.Name + "-keys-and-rows", cachedResult.ExitCode == 0
                && !json.RootElement.GetProperty("different").GetBoolean()
                && json.RootElement.GetProperty("rows").GetArrayLength() == 2,
                "Second-row aggregate cache hits preserve the opaque multiline origin for both All panes.");
        var project = Path.Combine(work, "project.json");
        var reportLeft = await Source("report-left", "a\r\nleft-original\r\n");
        var reportRight = await Source("report-right", "a\r\nright-original\r\n");
        var reportLeftRule = "If(\"\"+Content=" + Quote("<FileContent:" + Path.GetFullPath(reportLeft) + ">") + ",\"same\",\"bad-left\")";
        var reportRightRule = "If(\"\"+Content=" + Quote("<FileContent:" + Path.GetFullPath(reportRight) + ">") + ",\"same\",\"bad-right\")";
        await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new
        {
            mode = "Text", leftPath = reportLeft, rightPath = reportRight,
            lineTransformRules = new[] { new { expression = reportLeftRule, panes = 1 }, new { expression = reportRightRule, panes = 4 } }
        }));
        var copied = Path.Combine(work, "copied.json");
        await run("content-project-copy", 0, true, ["--project-copy", project, copied]);
        var report = Path.Combine(work, "report.html");
        await run("content-report-project", 0, true, ["--report-project", copied, report]);
        var html = File.Exists(report) ? System.Net.WebUtility.HtmlDecode(await File.ReadAllTextAsync(report)) : "";
        check("content HTML keeps original source and ignored classification", html.Contains("data-ignored=\"true\"", StringComparison.Ordinal)
            && html.Contains("left-original", StringComparison.Ordinal) && html.Contains("right-original", StringComparison.Ordinal), report);
        var package = Path.Combine(work, "package.zip");
        await run("content-package", 0, true, ["--package-project", copied, package, "--report"]);
        if (File.Exists(package))
        {
            using var zip = ZipFile.OpenRead(package);
            var entry = zip.Entries.SingleOrDefault(entry => entry.FullName.Equals("report.files/1.html", StringComparison.OrdinalIgnoreCase));
            var packaged = "";
            if (entry is not null) { using var reader = new StreamReader(entry.Open()); packaged = System.Net.WebUtility.HtmlDecode(await reader.ReadToEndAsync()); }
            check("content package keeps original source and ignored classification", packaged.Contains("data-ignored=\"true\"", StringComparison.Ordinal)
                && packaged.Contains("left-original", StringComparison.Ordinal) && packaged.Contains("right-original", StringComparison.Ordinal), package);
        }
        foreach (var original in originals)
            check("content source " + Path.GetFileName(original.Key), (await File.ReadAllBytesAsync(original.Key)).SequenceEqual(original.Value), "Original bytes/EOL preserved.");
        await File.WriteAllTextAsync(Path.Combine(work, "observations.json"), JsonSerializer.Serialize(new
        { qualification = "hand-authored snapshot contract; nativeGolden=false", observations, pathObservations, rejections }, new JsonSerializerOptions { WriteIndented = true }));

        async Task<string> Source(string name, string text)
        {
            var path = Path.Combine(work, name + ".txt"); var content = new UTF8Encoding(false, true).GetBytes(text);
            await File.WriteAllBytesAsync(path, content); originals.Add(path, content); return path;
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static List<string> PhysicalRows(string source)
        => source.Length == 0 ? [] : System.Text.RegularExpressions.Regex.Split(source, "\r\n|\r|\n")
            .Take(System.Text.RegularExpressions.Regex.Matches(source, "\r\n|\r|\n").Count + (source.EndsWith('\r') || source.EndsWith('\n') ? 0 : 1)).ToList();

    private static void RequireExternal(string path)
    {
        var repository = FixtureRepository.FindRoot()?.FullName ?? throw new IOException("Repository missing.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in new[] { repository, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), Environment.GetEnvironmentVariable("CODEX_HOME") })
            if (!string.IsNullOrEmpty(root) && (path.Equals(Path.GetFullPath(root), comparison)
                || path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, comparison)))
                throw new IOException("Content E2E requires external output.");
        for (DirectoryInfo? parent = new(path); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked content output refused.");
    }
}
