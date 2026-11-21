using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class GnuTextScenarios
{
    public static async Task RunAsync(string output,
        Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check)
    {
        var goldenPath = FindGolden();
        var goldenBytes = await File.ReadAllBytesAsync(goldenPath);
        check("gnu-text-golden-sha", Convert.ToHexString(SHA256.HashData(goldenBytes)) ==
            "4D52C2A1BADC1FEBF908B7EF348CE05808F8400B64736040E7D66359D377EDCF", goldenPath);
        using var golden = JsonDocument.Parse(goldenBytes);
        var cases = golden.RootElement.GetProperty("cases").EnumerateArray().Where(c => c.GetProperty("mode").GetString() == "default").ToArray();
        check("gnu-text-default-count", cases.Length == 273, $"default cases={cases.Length}");
        var folder = Path.Combine(output, "gnu-text-inputs");
        Directory.CreateDirectory(folder);
        var matched = new List<string>(); var fallback = new List<string>(); var failed = new List<string>();
        var observations = new List<object>();
        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString()!;
            var inputs = c.GetProperty("inputs").EnumerateArray().ToArray();
            var bytes = inputs.Select(input => Convert.FromBase64String(input.GetProperty("base64").GetString()!)).ToArray();
            for (var side = 0; side < bytes.Length; side++)
                check("gnu-text-" + name + "-source-" + side, bytes[side].Length == inputs[side].GetProperty("byteLength").GetInt32()
                    && Convert.ToHexString(SHA256.HashData(bytes[side])).Equals(inputs[side].GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Original input bytes, including BOM and final EOL.");
            var expected = ReadChanges(c.GetProperty("changes"));
            var result = await Compare("gnu-text-" + name, bytes[0], bytes[1], ["--eol", "strict", "--max-work", "8000000"], expected.Length == 0 ? 0 : 1, 8_000_000);
            if (result is null) { failed.Add(name); continue; }
            var full = new[] { new Change(0, 0, Contents(bytes[0]).Length, Contents(bytes[1]).Length) };
            var allowedFallback = name is ("heuristic-reversed-4095" or "heuristic-reversed-4097") && result.Fallback
                && result.WorkUsed == 8_000_000 && !string.IsNullOrWhiteSpace(result.Reason) && result.Changes.SequenceEqual(full);
            var equal = !result.Fallback && result.Changes.SequenceEqual(expected);
            check("gnu-text-" + name + "-original-script", equal || allowedFallback,
                $"expected={JsonSerializer.Serialize(expected)}; actual={JsonSerializer.Serialize(result.Changes)}; fallback={result.Fallback}; reason={result.Reason}");
            if (equal) matched.Add(name); else if (allowedFallback) fallback.Add(name); else failed.Add(name);
            observations.Add(new { name, category = c.GetProperty("category").GetString(), expected, actual = result.Changes,
                result.WorkUsed, result.Fallback, result.Reason, status = equal ? "matched" : allowedFallback ? "budget-fallback" : "failed" });
        }

        await CompareText("gnu-text-filter-case", "a\nB\na\n", "A\nb\nA\n", ["--ignore-case"], 0);
        await CompareText("gnu-text-filter-space", "a b\na  b\na b\n", "ab\nab\nab\n", ["--whitespace", "all"], 0);
        await CompareText("gnu-text-filter-blank", "a\n\na\n\nb\n", "a\na\nb\n", ["--ignore-blank"], 0);
        await CompareText("gnu-text-filter-regex", "a\nvolatile=1\na\n", "a\nvolatile=2\na\n", ["--ignore-regex", "^volatile="], 0);
        foreach (var budget in new[] { 0, 1 })
        {
            var result = await CompareText("gnu-text-budget-" + budget, "a\nold\nz\n", "a\nnew\nz\n",
                ["--max-work", budget.ToString(System.Globalization.CultureInfo.InvariantCulture)], 1, budget);
            if (result is not null) check("gnu-text-budget-" + budget + "-fallback", result.Fallback
                && !string.IsNullOrWhiteSpace(result.Reason), JsonSerializer.Serialize(result));
        }
        var same = await CompareText("gnu-text-equal-zero-budget", "a\na\nb\n", "a\na\nb\n", ["--max-work", "0"], 0, 0);
        if (same is not null) check("gnu-text-equal-zero-budget-preserved", !same.Fallback && same.Changes.Length == 0, JsonSerializer.Serialize(same));
        var huge = await CompareText("gnu-text-large-line-budget", new string('x', 65_536), new string('y', 65_536), ["--max-work", "16"], 1, 16);
        if (huge is not null) check("gnu-text-large-line-budget-fallback", huge.Fallback && !string.IsNullOrWhiteSpace(huge.Reason)
            && huge.Changes.SequenceEqual(new[] { new Change(0, 0, 1, 1) }), JsonSerializer.Serialize(huge));
        await CompareText("gnu-text-eof-strict", "a\n", "a", ["--eol", "strict"], 1);
        await CompareText("gnu-text-eof-ignore", "a\n", "a", ["--eol", "ignore"], 1);
        await CompareText("gnu-text-eol-strict", "a\r\na\r\n", "a\na\n", ["--eol", "strict"], 1);
        await CompareText("gnu-text-eol-ignore", "a\r\na\r\n", "a\na\n", ["--eol", "ignore"], 0);

        var ancestor = Write("merge-base", Encoding.UTF8.GetBytes("one\ntwo\nthree\n"));
        var left = Write("merge-left", Encoding.UTF8.GetBytes("ONE\ntwo\nthree\n"));
        var right = Write("merge-right", Encoding.UTF8.GetBytes("one\ntwo\nTHREE\n"));
        var mergeInputs = new[] { ancestor, left, right };
        var mergeBytes = mergeInputs.Select(File.ReadAllBytes).ToArray();
        var merged = Path.Combine(folder, "merged.txt");
        await run("gnu-text-merge-independent", 0, false, ["--merge", ancestor, left, right, merged]);
        check("gnu-text-merge-independent-result", File.Exists(merged) && File.ReadAllBytes(merged).SequenceEqual(Encoding.UTF8.GetBytes("ONE\ntwo\nTHREE\n")), merged);
        var patch = Path.Combine(folder, "generated.patch");
        var applied = Path.Combine(folder, "applied.txt");
        await run("gnu-text-patch-create", 0, false, ["--patch-create", ancestor, merged, patch]);
        if (File.Exists(patch) && File.Exists(merged))
        {
            var patchBefore = await File.ReadAllBytesAsync(patch);
            var targetBefore = await File.ReadAllBytesAsync(merged);
            await run("gnu-text-patch-apply", 0, false, ["--patch-apply", ancestor, patch, applied]);
            check("gnu-text-patch-reconstruct", File.Exists(applied) && File.ReadAllBytes(applied).SequenceEqual(targetBefore), applied);
            check("gnu-text-patch-target-preserved", patchBefore.SequenceEqual(File.ReadAllBytes(patch)) && targetBefore.SequenceEqual(File.ReadAllBytes(merged)), patch);
        }
        else check("gnu-text-patch-inputs-exist", false, patch);
        check("gnu-text-merge-originals-preserved", mergeInputs.Select((path, side) => mergeBytes[side].SequenceEqual(File.ReadAllBytes(path))).All(value => value), folder);
        var goldenAfter = await File.ReadAllBytesAsync(goldenPath);
        check("gnu-text-golden-preserved", goldenBytes.SequenceEqual(goldenAfter), goldenPath);
        await File.WriteAllTextAsync(Path.Combine(output, "gnu-text-observations.json"), JsonSerializer.Serialize(new
        {
            defaultCases = cases.Length, fullBudget = 8_000_000, matched, fallback, failed, observations,
            auxiliaryComparisons = 12, mergeAndPatchCalls = 3
        }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        string Write(string name, byte[] bytes)
        {
            var path = Path.Combine(folder, name + ".txt");
            File.WriteAllBytes(path, bytes);
            return path;
        }
        Task<Snapshot?> CompareText(string name, string a, string b, string[] flags, int exit, int budget = 4_000_000)
            => Compare(name, Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b), flags, exit, budget);
        async Task<Snapshot?> Compare(string name, byte[] a, byte[] b, string[] flags, int exit, int budget)
        {
            var lp = Write(name + "-left", a); var rp = Write(name + "-right", b);
            var command = await run(name, exit, true, ["--compare", lp, rp, .. flags]);
            var afterA = await File.ReadAllBytesAsync(lp); var afterB = await File.ReadAllBytesAsync(rp);
            check(name + "-original-bytes", a.SequenceEqual(afterA) && b.SequenceEqual(afterB), "Original bytes, BOM and EOL preserved.");
            try
            {
                using var document = JsonDocument.Parse(command.Stdout);
                var root = document.RootElement;
                var rows = root.GetProperty("rows").EnumerateArray().ToArray();
                var changes = RestoreChanges(rows);
                CheckRows(name, rows, Contents(a), Contents(b), check);
                var used = root.GetProperty("lineWorkUsed").GetInt32();
                var didFallback = root.GetProperty("lineFallback").GetBoolean();
                var reason = root.GetProperty("lineFallbackReason").GetString();
                check(name + "-status", command.ExitCode == exit && root.GetProperty("different").GetBoolean() == (exit == 1)
                    && root.GetProperty("blocks").GetInt32() == changes.Length, $"exit={command.ExitCode}; blocks={changes.Length}");
                check(name + "-budget", used >= 0 && used <= budget && (didFallback ? !string.IsNullOrWhiteSpace(reason) : reason == null),
                    $"work={used}; budget={budget}; fallback={didFallback}; reason={reason}");
                return new(changes, didFallback, used, reason);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
            { check(name + "-json-contract", false, error.Message); return null; }
        }
    }

    private sealed record Change(int Line0, int Line1, int Deleted, int Inserted);
    private sealed record Snapshot(Change[] Changes, bool Fallback, int WorkUsed, string? Reason);
    private static Change[] ReadChanges(JsonElement array) => array.EnumerateArray().Select(c => new Change(c.GetProperty("line0").GetInt32(),
        c.GetProperty("line1").GetInt32(), c.GetProperty("deleted").GetInt32(), c.GetProperty("inserted").GetInt32())).ToArray();
    private static Change[] RestoreChanges(JsonElement[] rows)
    {
        var changes = new List<Change>(); var a = 0; var b = 0;
        var startA = 0; var startB = 0; var active = false;
        foreach (var row in rows)
        {
            var equal = row.GetProperty("kind").GetString() == "Equal";
            if (equal && active) { changes.Add(new(startA, startB, a - startA, b - startB)); active = false; }
            if (!equal && !active) { startA = a; startB = b; active = true; }
            if (row.TryGetProperty("leftLine", out _)) a++;
            if (row.TryGetProperty("rightLine", out _)) b++;
        }
        if (active) changes.Add(new(startA, startB, a - startA, b - startB));
        return changes.ToArray();
    }
    private static void CheckRows(string name, JsonElement[] rows, string[] a, string[] b, Action<string, bool, string> check)
    {
        foreach (var (side, contents) in new[] { ("left", a), ("right", b) })
        {
            var sourceRows = rows.Where(row => row.TryGetProperty(side + "Line", out _)).ToArray();
            var numbers = sourceRows.Select(row => row.GetProperty(side + "Line").GetInt32()).ToArray();
            check(name + "-" + side + "-source-order", numbers.SequenceEqual(Enumerable.Range(1, contents.Length)),
                $"originalRows={contents.Length}; consumed={numbers.Length}");
            check(name + "-" + side + "-source-content", sourceRows.All(row =>
            {
                var n = row.GetProperty(side + "Line").GetInt32();
                return n > 0 && n <= contents.Length && row.GetProperty(side).GetString() == contents[n - 1];
            }), "Decoded original content preserved, including empty lines.");
        }
    }
    private static string[] Contents(byte[] bytes)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        if (text.Length == 0) return [];
        var lines = Regex.Split(text, "\\r\\n|\\r|\\n");
        return text[^1] is '\r' or '\n' ? lines[..^1] : lines;
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
