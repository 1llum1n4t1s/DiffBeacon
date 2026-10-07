using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class FolderThreeWayScenarios
{
    internal static async Task RunAsync(string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check, Action<string, string> skip, string python)
    {
        var fixture = Path.GetFullPath("tests/Fixtures/FolderThreeWay");
        var work = Path.Combine(fixtures, "folder-threeway");
        if (Directory.Exists(work)) throw new IOException("三者folder成果物には新しいrunを使用してください。");
        Directory.CreateDirectory(work);
        var records = new List<object>();
        var inputs = Path.Combine(work, "model");
        var roots = Roots(inputs);
        foreach (var line in await File.ReadAllLinesAsync(Path.Combine(fixture, "original", "transport.ndjson")))
        {
            using var item = JsonDocument.Parse(line);
            var row = item.RootElement;
            var id = row.GetProperty("id").GetString()!;
            var presence = row.GetProperty("presenceMask").GetInt32();
            var sides = row.GetProperty("sides").EnumerateArray().ToArray();
            for (var side = 0; side < 3; side++)
                if ((presence & (1 << side)) != 0)
                    await File.WriteAllBytesAsync(Path.Combine(roots[side], id + ".txt"), Convert.FromBase64String(sides[side].GetProperty("bytesBase64").GetString()!));
        }
        await Observe("original63", inputs, 1, true, ["--directory", roots[0], roots[2], "--middle", roots[1]]);

        var directions = new[] { ("left-to-right", 0, 2), ("right-to-left", 2, 0), ("left-to-middle", 0, 1),
            ("middle-to-left", 1, 0), ("middle-to-right", 1, 2), ("right-to-middle", 2, 1) };
        foreach (var (direction, source, target) in directions)
        {
            foreach (var mode in new[] { "all", "diff" })
            {
                var id = mode + "-" + direction;
                var location = Path.Combine(work, id);
                var sides = await Create(location, ["left\n", "middle\n", "right\n"]);
                await Observe(id, location, 0, true, Sync(sides, direction, mode), source, target);
            }
            // 旧gateは全体のModifiedを使う。選択pairのEqualだけで候補を落とさない。
            var equalId = "equal-pair-" + direction;
            var equalLocation = Path.Combine(work, equalId);
            var texts = new[] { "left\n", "middle\n", "right\n" }; texts[target] = texts[source];
            var equalSides = await Create(equalLocation, texts);
            await Observe(equalId, equalLocation, 0, true, Sync(equalSides, direction, "diff"), source, target);
        }

        var absentLocation = Path.Combine(work, "source-absent");
        var absent = Roots(absentLocation);
        await File.WriteAllTextAsync(Path.Combine(absent[1], "entry.txt"), "middle\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(absent[2], "entry.txt"), "right\n", new UTF8Encoding(false));
        await Observe("source-absent", absentLocation, 0, true, Sync(absent, "left-to-middle", "diff"));

        var filteredLocation = Path.Combine(work, "filtered");
        var filtered = await Create(filteredLocation, ["left\n", "middle\n", "right\n"]);
        await Observe("filtered", filteredLocation, 0, true, [.. Sync(filtered, "left-to-middle", "diff"), "--exclude", "entry.txt", "--show-filtered"]);

        var protectionLocation = Path.Combine(work, "third-root-protection");
        var protection = Roots(protectionLocation);
        var nestedMiddle = Path.Combine(protection[2], "third"); Directory.CreateDirectory(nestedMiddle);
        Directory.CreateDirectory(Path.Combine(protection[0], "third"));
        await File.WriteAllTextAsync(Path.Combine(protection[0], "third", "entry.txt"), "new\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(nestedMiddle, "entry.txt"), "protected\n", new UTF8Encoding(false));
        await Observe("third-root-protection", protectionLocation, 2, false,
            ["--folder-sync", protection[0], protection[2], "--middle", nestedMiddle, "--direction", "left-to-right", "--copy", "all", "--select", "third/entry.txt"]);

        foreach (var alias in OperatingSystem.IsWindows()
            ? new[] { "trailing", "extended-double-trailing", "device-double-trailing", "localhost-share" } : new[] { "trailing" })
        {
            var id = "third-root-" + alias;
            var location = Path.Combine(work, id);
            var sides = Roots(location);
            var third = Path.Combine(sides[2], "third");
            Directory.CreateDirectory(third); Directory.CreateDirectory(Path.Combine(sides[0], "third"));
            await File.WriteAllTextAsync(Path.Combine(sides[0], "third", "entry.txt"), "new\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(third, "entry.txt"), "protected\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(sides[2], "entry.txt"), "right\n", new UTF8Encoding(false));
            var middleInput = alias switch
            {
                "extended-double-trailing" => @"\\?\" + third + @"\\",
                "device-double-trailing" => @"\\.\" + third + @"\\",
                "localhost-share" => @"\\localhost\" + third[0] + "$" + third[2..],
                _ => third + Path.DirectorySeparatorChar
            };
            if (alias == "localhost-share")
            {
                var eligible = Directory.Exists(middleInput);
                await File.WriteAllBytesAsync(Path.Combine(work, "physical-alias-environment.json"),
                    JsonSerializer.SerializeToUtf8Bytes(new { canonicalRoot = third, middleInput, eligible }));
                if (!eligible)
                {
                    skip("Folder threeway existing localhost share alias", "既存のローカル管理共有からfixtureを読めないため、UNC別名の実測を省略します。共有やOS設定は変更しません。");
                    continue;
                }
            }
            await Observe(id, location, 2, false,
                ["--folder-sync", sides[0], sides[2], "--middle", middleInput, "--direction", "left-to-right", "--copy", "all", "--select", "third/entry.txt"]);
        }

        if (OperatingSystem.IsWindows())
        {
            var probeLocation = LongLocation(Path.Combine(work, "long-alias-share-probe"));
            var probe = Roots(probeLocation)[0];
            var share = @"\\localhost\" + probe[0] + "$" + probe[2..];
            var eligible = Directory.Exists(@"\\?\UNC\" + share[2..]);
            await Json(work, "long-alias-environment.json", new { canonicalRoot = probe, shareInput = share, eligible });
            if (!eligible) skip("Folder long existing localhost DOS UNC alias", "既存管理共有を読めないため長いDOS UNC別名だけ省略します。共有やOS設定は変更しません。");
            foreach (var alias in eligible ? new[] { "device", "device-unc" } : new[] { "device" })
            {
                foreach (var side in new[] { 0, 2 })
                {
                    var id = $"long-{alias}-two-read-{side}";
                    var location = LongLocation(Path.Combine(work, id));
                    var sides = await Create(location, ["left\n", "middle\n", "right\n"]);
                    var arguments = new[] { "--directory", sides[0], sides[2] };
                    arguments[side == 0 ? 1 : 2] = LongAlias(sides[side], alias);
                    await Observe(id, location, 1, true, arguments);
                }
                for (var side = 0; side < 3; side++)
                {
                    var id = $"long-{alias}-three-read-{side}";
                    var location = LongLocation(Path.Combine(work, id));
                    var sides = await Create(location, ["left\n", "middle\n", "right\n"]);
                    sides[side] = LongAlias(sides[side], alias);
                    await Observe(id, location, 1, true, ["--directory", sides[0], sides[2], "--middle", sides[1]]);
                }
                foreach (var (direction, source, target) in directions.Take(2))
                {
                    var id = $"long-{alias}-two-copy-{direction}";
                    var location = LongLocation(Path.Combine(work, id));
                    var sides = await Create(location, ["left\n", "middle\n", "right\n"]);
                    await Observe(id, location, 0, true,
                        ["--folder-sync", LongAlias(sides[0], alias), LongAlias(sides[2], alias), "--direction", direction, "--copy", "all", "--select", "entry.txt"], source, target);
                }
            }
            var protectedId = "third-root-long-device-double-trailing";
            var protectedLocation = LongLocation(Path.Combine(work, protectedId));
            var protectedSides = Roots(protectedLocation);
            var protectedThird = Path.Combine(protectedSides[2], "third");
            Directory.CreateDirectory(protectedThird); Directory.CreateDirectory(Path.Combine(protectedSides[0], "third"));
            await File.WriteAllTextAsync(Path.Combine(protectedSides[0], "third", "entry.txt"), "new\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(protectedThird, "entry.txt"), "protected\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(protectedSides[2], "entry.txt"), "right\n", new UTF8Encoding(false));
            await Observe(protectedId, protectedLocation, 2, false,
                ["--folder-sync", protectedSides[0], protectedSides[2], "--middle", LongAlias(protectedThird, "device") + @"\\", "--direction", "left-to-right", "--copy", "all", "--select", "third/entry.txt"]);
        }

        var overlapLocation = Path.Combine(work, "identical-roots");
        var overlap = await Create(overlapLocation, ["left\n", "middle\n", "right\n"]);
        await Observe("identical-roots", overlapLocation, 2, false,
            ["--folder-sync", overlap[0], overlap[2], "--middle", overlap[0], "--direction", "left-to-middle", "--copy", "all", "--select", "entry.txt"]);

        var budgetLocation = Path.Combine(work, "content-budget");
        var budget = await Create(budgetLocation, ["left\n", "middle\n", "right\n"]);
        await Observe("content-budget", budgetLocation, 2, true,
            ["--directory", budget[0], budget[2], "--middle", budget[1], "--max-content-bytes", "1"]);
        await Observe("error-skip", budgetLocation, 0, true,
            [.. Sync(budget, "left-to-middle", "diff"), "--max-content-bytes", "1"]);

        var workspaceLocation = Path.Combine(work, "workspace");
        await Create(workspaceLocation, ["left\n", "middle\n", "right\n"]);
        var project = Path.Combine(workspaceLocation, "input.json");
        await Json(workspaceLocation, "input.json", new { leftPath = "left", basePath = "middle", rightPath = "right", mode = "Folder",
            baseReadOnly = true, recursive = false, folderMode = "Content", folderShowFiltered = true });
        var saved = Path.Combine(workspaceLocation, "saved.json");
        var reopened = Path.Combine(workspaceLocation, "reopened.json");
        await Observe("workspace-save", workspaceLocation, 0, true, ["--project-copy", project, saved]);
        await Observe("workspace-reopen", workspaceLocation, 0, true, ["--project-copy", saved, reopened]);

        var gui = await run("folder-threeway-gui", 0, false, ["--self-test", Path.Combine(work, "gui"), "--folder-threeway-only"]);
        await Json(work, "gui-process.json", gui);
        await File.WriteAllTextAsync(Path.Combine(work, "gui.stdout.txt"), gui.Stdout, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(work, "gui.stderr.txt"), gui.Stderr, new UTF8Encoding(false));
        await Json(work, "commands.json", records);

        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(fixture, "verify.py"), fixture, work }) start.ArgumentList.Add(argument);
        var launch = DateTime.UtcNow;
        using var reader = Process.Start(start)!;
        var creation = reader.StartTime.ToUniversalTime();
        var stdout = reader.StandardOutput.ReadToEndAsync(); var stderr = reader.StandardError.ReadToEndAsync();
        await reader.WaitForExitAsync(); var text = await stdout; var error = await stderr;
        await File.WriteAllTextAsync(Path.Combine(work, "independent.stdout.json"), text, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(work, "independent.stderr.txt"), error, new UTF8Encoding(false));
        await Json(work, "independent-process.json", new { pid = reader.Id, creationUtc = creation, launchUtc = launch, exitObservedUtc = DateTime.UtcNow,
            actualExit = reader.ExitCode, waitCompleted = true, stdoutComplete = true, stderrComplete = true, readerSha256 = Hash(Path.Combine(fixture, "verify.py")) });
        check("Folder threeway independent original63 copies protection workspace GUI", reader.ExitCode == 0, text + error);
        skip("Folder threeway actual desktop and all input compatibility", "既定GNU UTF-8原本63とheadless GUIの限定契約。実OS pointer/dialog、任意plugins/encoding/binaryは範囲外。");

        async Task Observe(string id, string location, int exit, bool json, string[] arguments, int? source = null, int? target = null)
        {
            var before = Snapshot(location);
            var command = await run("folder-threeway-" + id, exit, json, arguments);
            var after = Snapshot(location);
            records.Add(new { id, work = location, command, before, after, source, target });
        }
    }

    // 各componentを80文字以下に保ち、標準パスで270文字超の実在根を作る。
    private static string LongLocation(string location)
    {
        location = Path.GetFullPath(location);
        while (location.Length < 270) location = Path.Combine(location, new string('l', Math.Min(60, Math.Max(1, 270 - location.Length))));
        if (location.Split(Path.DirectorySeparatorChar).Any(part => part.Length > 80)) throw new IOException("長い別名fixtureのcomponentが80文字を超えています。");
        return location;
    }
    private static string LongAlias(string canonical, string alias) => alias == "device-unc"
        ? @"\\.\UNC\localhost\" + canonical[0] + "$" + canonical[2..] : @"\\.\" + canonical;

    private static string[] Roots(string location)
    {
        var roots = new[] { "left", "middle", "right" }.Select(name => Path.Combine(location, name)).ToArray();
        foreach (var root in roots) Directory.CreateDirectory(root);
        return roots;
    }
    private static async Task<string[]> Create(string location, string[] text)
    {
        var roots = Roots(location);
        for (var side = 0; side < 3; side++) await File.WriteAllTextAsync(Path.Combine(roots[side], "entry.txt"), text[side], new UTF8Encoding(false));
        return roots;
    }
    private static string[] Sync(string[] roots, string direction, string mode) =>
        ["--folder-sync", roots[0], roots[2], "--middle", roots[1], "--direction", direction, "--copy", mode, "--select", "entry.txt"];
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private sealed record Entry(string Path, long Bytes, string Sha256, long Mtime, int Attributes);
    private static Entry[] Snapshot(string location) => Directory.EnumerateFiles(location, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetRelativePath(location, path).Split(Path.DirectorySeparatorChar)[0] is "left" or "middle" or "right")
        .Order(StringComparer.Ordinal).Select(path => new Entry(Path.GetRelativePath(location, path).Replace('\\', '/'), new FileInfo(path).Length,
            Hash(path), File.GetLastWriteTimeUtc(path).ToFileTimeUtc(), (int)File.GetAttributes(path))).ToArray();
    private static Task Json<T>(string location, string name, T value) => File.WriteAllTextAsync(Path.Combine(location, name),
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
}
