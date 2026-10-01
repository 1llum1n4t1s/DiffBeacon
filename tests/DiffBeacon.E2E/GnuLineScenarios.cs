using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class GnuLineScenarios
{
    private const int FullBudget = 8_000_000;
    private const string GoldenSha = "4D52C2A1BADC1FEBF908B7EF348CE05808F8400B64736040E7D66359D377EDCF";

    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var goldenPath = FindGolden();
        var original = await File.ReadAllBytesAsync(goldenPath);
        check("gnu-line-golden-sha", Convert.ToHexString(SHA256.HashData(original)) == GoldenSha, goldenPath);
        using var golden = JsonDocument.Parse(original);
        var cases = golden.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        check("gnu-line-golden-count", cases.Length == 279, $"cases={cases.Length}");
        var folder = Path.Combine(fixtures, "gnu-line");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(output);
        var observations = new List<object>();
        var matchedNames = new List<string>();
        var fallbackNames = new List<string>();
        var failedNames = new List<string>();
        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString()!;
            var category = c.GetProperty("category").GetString()!;
            var equivalents = c.GetProperty("equivs").EnumerateArray().Select(a => a.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray();
            var lengths = c.GetProperty("lengths").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            var classes = c.GetProperty("classCount").GetInt32();
            check("gnu-line-" + name + "-source-shape", equivalents.Length == 2 && lengths.SequenceEqual(equivalents.Select(a => a.Length))
                && equivalents.All(a => a.All(v => v >= 0 && v < classes)), "Original io.c equivs and lengths, without reclassification.");
            foreach (var input in c.GetProperty("inputs").EnumerateArray())
            {
                var bytes = Convert.FromBase64String(input.GetProperty("base64").GetString()!);
                check("gnu-line-" + name + "-input-" + input.GetProperty("path").GetString(),
                    bytes.Length == input.GetProperty("byteLength").GetInt32()
                    && Convert.ToHexString(SHA256.HashData(bytes)).Equals(input.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Original bytes provenance.");
            }
            var snapshot = await Compare("gnu-line-" + name, equivalents[0], equivalents[1], classes, FullBudget);
            if (snapshot is null) { observations.Add(new { name, category, status = "invalid-response" }); failedNames.Add(name); continue; }
            var expected = ReadChanges(c.GetProperty("rawChanges"));
            var prefixes = c.GetProperty("prefixLines").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            var restored = ReadChanges(c.GetProperty("changes"));
            var fullBlock = new[] { new Change(0, 0, equivalents[0].Length, equivalents[1].Length) };
            var allowedFallback = category == "heuristic-size" && name is ("heuristic-reversed-4095" or "heuristic-reversed-4097") && snapshot.Fallback
                && snapshot.WorkUsed == FullBudget && snapshot.Reason?.StartsWith("work-limit", StringComparison.Ordinal) == true
                && snapshot.Changes.SequenceEqual(fullBlock);
            var matched = !snapshot.Fallback && expected.SequenceEqual(snapshot.Changes);
            if (matched) matchedNames.Add(name);
            else if (allowedFallback) fallbackNames.Add(name);
            else failedNames.Add(name);
            check("gnu-line-" + name + "-raw-or-budget", matched || allowedFallback,
                $"category={category}; expected={JsonSerializer.Serialize(expected)}; actual={snapshot.Raw}");
            if (!snapshot.Fallback)
            {
                var translated = snapshot.Changes.Select(change => change with { Line0 = change.Line0 + prefixes[0], Line1 = change.Line1 + prefixes[1] }).ToArray();
                check("gnu-line-" + name + "-prefix", translated.SequenceEqual(restored),
                    $"prefix={string.Join(',', prefixes)}; expected={JsonSerializer.Serialize(restored)}; actual={JsonSerializer.Serialize(translated)}");
            }
            observations.Add(new { name, category, prefixes, status = matched ? "matched" : allowedFallback ? "budget-fallback" : "failed",
                snapshot.Fallback, snapshot.WorkUsed, snapshot.Reason, expected, actual = snapshot.Changes });
        }

        await Compare("gnu-line-empty-class-zero", [], [], 0, FullBudget, []);
        await Compare("gnu-line-left-empty", [], [1, 2], 3, FullBudget, [new(0, 0, 0, 2)]);
        await Compare("gnu-line-right-empty", [1, 2], [], 3, FullBudget, [new(0, 0, 2, 0)]);
        await Compare("gnu-line-class-zero", [0], [0], 1, FullBudget, []);
        await Compare("gnu-line-class-zero-mixed", [0, 1], [0, 1], 2, FullBudget, []);
        foreach (var budget in new[] { 0, 1 })
        {
            var snapshot = await Compare("gnu-line-budget-" + budget, [1, 2], [2, 1], 3, budget);
            if (snapshot is not null) check("gnu-line-budget-" + budget + "-fallback", snapshot.Fallback && snapshot.WorkUsed == budget
                && snapshot.Reason?.StartsWith("work-limit", StringComparison.Ordinal) == true
                && snapshot.Changes.SequenceEqual(new[] { new Change(0, 0, 2, 2) }), snapshot.Raw);
        }

        var invalid = new (string Name, string Json)[]
        {
            ("negative-class", "{\"left\":[-1],\"right\":[],\"classCount\":1}"),
            ("out-of-range-class", "{\"left\":[],\"right\":[1],\"classCount\":1}"),
            ("negative-class-count", "{\"left\":[],\"right\":[],\"classCount\":-1}"),
            ("zero-class-count-nonempty", "{\"left\":[0],\"right\":[],\"classCount\":0}"),
            ("missing-left", "{\"right\":[],\"classCount\":1}"),
            ("missing-right", "{\"left\":[],\"classCount\":1}"),
            ("missing-count", "{\"left\":[],\"right\":[]}"),
            ("null-left", "{\"left\":null,\"right\":[],\"classCount\":1}"),
            ("scalar-right", "{\"left\":[],\"right\":2,\"classCount\":1}"),
            ("string-class", "{\"left\":[\"0\"],\"right\":[],\"classCount\":1}"),
            ("fractional-class", "{\"left\":[0.5],\"right\":[],\"classCount\":1}"),
            ("string-count", "{\"left\":[],\"right\":[],\"classCount\":\"1\"}"),
            ("invalid-json", "{broken"),
            ("row-cap-left", JsonSerializer.Serialize(new { left = new int[262_145], right = Array.Empty<int>(), classCount = 1 })),
            ("row-cap-right", JsonSerializer.Serialize(new { left = Array.Empty<int>(), right = new int[262_145], classCount = 1 }))
        };
        foreach (var error in invalid)
        {
            var path = Write(error.Name, error.Json);
            await Reject("gnu-line-error-" + error.Name, path, ["--gnu-line-script", path]);
            if (error.Name is "negative-class" or "out-of-range-class")
                await Reject("gnu-line-error-zero-budget-" + error.Name, path, ["--gnu-line-script", path, "--max-work", "0"]);
        }
        var validPath = Write("arguments", "{\"left\":[0],\"right\":[0],\"classCount\":1}");
        var arguments = new (string Name, string[] Args)[]
        {
            ("negative-budget", ["--gnu-line-script", validPath, "--max-work", "-1"]),
            ("excessive-budget", ["--gnu-line-script", validPath, "--max-work", "8000001"]),
            ("bad-budget", ["--gnu-line-script", validPath, "--max-work", "bad"]),
            ("missing-budget", ["--gnu-line-script", validPath, "--max-work"]),
            ("unknown-flag", ["--gnu-line-script", validPath, "--unknown", "1"]),
            ("extra-argument", ["--gnu-line-script", validPath, "extra"]),
            ("missing-input", ["--gnu-line-script"])
        };
        foreach (var error in arguments) await Reject("gnu-line-error-" + error.Name, validPath, error.Args);
        var after = await File.ReadAllBytesAsync(goldenPath);
        check("gnu-line-golden-preserved", original.SequenceEqual(after), goldenPath);
        await File.WriteAllTextAsync(Path.Combine(output, "gnu-line-observations.json"),
            JsonSerializer.Serialize(new { goldenSha = GoldenSha, budget = FullBudget, cases = observations,
                matchedNames, fallbackNames, failedNames, goldenCases = cases.Length, additionalValid = 7,
                rejected = invalid.Length + arguments.Length + 2 }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        string Write(string name, string json)
        {
            var path = Path.Combine(folder, name + ".json");
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return path;
        }
        async Task Reject(string name, string path, string[] args)
        {
            var before = await File.ReadAllBytesAsync(path);
            var result = await run(name, 2, false, args);
            check(name + "-rejected", result.ExitCode == 2 && !string.IsNullOrWhiteSpace(result.Stderr) && !SuccessJson(result.Stdout), result.Stderr);
            var afterBytes = await File.ReadAllBytesAsync(path);
            check(name + "-input-preserved", before.SequenceEqual(afterBytes), path);
        }
        async Task<Snapshot?> Compare(string name, int[] left, int[] right, int count, int budget, Change[]? expected = null)
        {
            var path = Write(name, JsonSerializer.Serialize(new { left, right, classCount = count }));
            var before = await File.ReadAllBytesAsync(path);
            var result = await run(name, 0, true, ["--gnu-line-script", path, "--max-work", budget.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            var afterBytes = await File.ReadAllBytesAsync(path);
            check(name + "-input-preserved", before.SequenceEqual(afterBytes), path);
            try
            {
                using var parsed = JsonDocument.Parse(result.Stdout);
                var root = parsed.RootElement;
                var changes = ReadChanges(root.GetProperty("changes"));
                var fallback = root.GetProperty("fallback").GetBoolean();
                var used = root.GetProperty("workUsed").GetInt32();
                var reason = root.GetProperty("fallbackReason").GetString();
                check(name + "-status", result.ExitCode == 0 && used >= 0 && used <= budget
                    && (fallback ? !string.IsNullOrWhiteSpace(reason) : reason == null), result.Stdout);
                CheckConsumption(name, left, right, changes, check);
                if (expected is not null) check(name + "-expected", !fallback && changes.SequenceEqual(expected), result.Stdout);
                return new(changes, fallback, used, reason, result.Stdout);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
            { check(name + "-json-contract", false, error.Message + ": " + result.Stdout); return null; }
        }
    }

    private static void CheckConsumption(string name, int[] left, int[] right, Change[] changes, Action<string, bool, string> check)
    {
        var a = 0; var b = 0; var valid = true; var equals = true;
        var seenLeft = new int[left.Length]; var seenRight = new int[right.Length];
        foreach (var change in changes)
        {
            if (change.Line0 < a || change.Line1 < b || change.Deleted < 0 || change.Inserted < 0
                || (long)change.Line0 + change.Deleted > left.Length || (long)change.Line1 + change.Inserted > right.Length
                || (long)change.Deleted + change.Inserted == 0) { valid = false; break; }
            EqualUntil(change.Line0, change.Line1);
            for (var i = 0; i < change.Deleted; i++) seenLeft[a++]++;
            for (var i = 0; i < change.Inserted; i++) seenRight[b++]++;
        }
        if (valid) EqualUntil(left.Length, right.Length);
        check(name + "-consumption", valid && a == left.Length && b == right.Length
            && seenLeft.All(n => n == 1) && seenRight.All(n => n == 1), "Each original equivalent row is consumed once in source order.");
        check(name + "-equal-classes", valid && equals, "Unchanged script gaps have equal length and equivalent classes.");
        void EqualUntil(int endA, int endB)
        {
            if (endA - a != endB - b) equals = false;
            var common = Math.Min(endA - a, endB - b);
            for (var i = 0; i < common; i++) if (left[a + i] != right[b + i]) equals = false;
            while (a < endA) seenLeft[a++]++;
            while (b < endB) seenRight[b++]++;
        }
    }
    private sealed record Change(int Line0, int Line1, int Deleted, int Inserted);
    private sealed record Snapshot(Change[] Changes, bool Fallback, int WorkUsed, string? Reason, string Raw);
    private static Change[] ReadChanges(JsonElement array) => array.EnumerateArray().Select(c => new Change(
        c.GetProperty("line0").GetInt32(), c.GetProperty("line1").GetInt32(), c.GetProperty("deleted").GetInt32(), c.GetProperty("inserted").GetInt32())).ToArray();
    private static bool SuccessJson(string stdout)
    {
        try { using var document = JsonDocument.Parse(stdout); return document.RootElement.TryGetProperty("changes", out _); }
        catch (JsonException) { return false; }
    }
    private static string FindGolden()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "Fixtures", "GnuLines", "legacy-gnu-golden.json");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("GNU line golden fixture was not found in the repository.");
    }
}
