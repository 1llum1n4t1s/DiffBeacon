using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ArchiveSourceScenarios
{
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run,
        Func<string, int, bool, string?, string[], Task<CommandResult>> runInput,
        Action<string, bool, string> check, Action<string, string> skip, string python)
    {
        // 起こり得る失敗と期待結果は実装前にnested-archive-source/e2e-failure-plan.mdへ固定。
        var root = Path.GetFullPath("tests/Fixtures/Archives/Sources");
        var work = Path.Combine(fixtures, "archive-sources"); Directory.CreateDirectory(work);
        var originals = Directory.GetFiles(root).ToDictionary(path => path, Hash);
        check("source fixed manifest SHA", Hash(Path.Combine(root, "manifest.json")) == "07AB91DD960B1369E61182261C958AF611B03E0E44CD3387A9E50ADB324DF2FE", "");
        using var golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "manifest.json")));
        var products = new List<object>();
        var cases = golden.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        foreach (var item in golden.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = Path.Combine(root, item.GetProperty("name").GetString()!);
            check("source fixed original " + Path.GetFileName(path), Hash(path) == item.GetProperty("sha256").GetString()
                && new FileInfo(path).Length == item.GetProperty("bytes").GetInt64(), "");
        }
        foreach (var item in cases)
        {
            var name = item.GetProperty("name").GetString()!;
            var path = Path.Combine(root, item.GetProperty("file").GetString()!);
            var chain = item.GetProperty("chain").EnumerateArray().Select(entry => entry.GetString()!).ToArray();
            var descriptor = Descriptor(name, path, chain);
            var passwords = item.TryGetProperty("passwords", out var passwordJson)
                ? string.Join('\n', passwordJson.EnumerateArray().Select(entry => entry.GetString())) + "\n" : null;
            var result = await Execute("source-list-" + name, 0, true, passwords, ["--archive-source-list", descriptor]);
            if (result.ExitCode != 0) continue;
            using var list = JsonDocument.Parse(result.Stdout);
            VerifyManifest(name, list.RootElement, item.GetProperty("entries"));
            var source = list.RootElement.GetProperty("source");
            check("source root and chain " + name, source.GetProperty("rootPath").GetString() == path
                && source.GetProperty("rootSha256").GetString() == Hash(path)
                && source.GetProperty("entryChain").EnumerateArray().Select(entry => entry.GetString()).SequenceEqual(chain), "physical root and virtual chain retained separately");
            if (item.TryGetProperty("decodedBytes", out var decodedJson))
            {
                var decoded = decodedJson.GetInt64();
                var exact = Descriptor(name + "-decoded-exact", path, chain, new() { ["maximumDecodedBytes"] = decoded });
                var boundary = await Execute("source-decoded-exact-" + name, 0, true, passwords, ["--archive-source-list", exact]);
                if (boundary.ExitCode == 0)
                {
                    using var boundaryJson = JsonDocument.Parse(boundary.Stdout);
                    VerifyManifest(name + "-decoded-exact", boundaryJson.RootElement, item.GetProperty("entries"));
                }
                var below = Descriptor(name + "-decoded-below", path, chain, new() { ["maximumDecodedBytes"] = decoded - 1 });
                await Reject(name + "-decoded-below", below, "leaf.txt", passwords);
            }
            if (item.TryGetProperty("maximumEntries", out var countJson))
            {
                var count = countJson.GetInt64();
                var exact = Descriptor(name + "-items-exact", path, chain, new() { ["maximumEntries"] = count });
                await Execute("source-items-exact-" + name, 0, true, passwords, ["--archive-source-list", exact]);
                await Reject(name + "-items-below", Descriptor(name + "-items-below", path, chain, new() { ["maximumEntries"] = count - 1 }), "leaf.txt");
            }
            if (item.TryGetProperty("listOnly", out var listOnly) && listOnly.GetBoolean()) continue;
            foreach (var entry in item.GetProperty("entries").EnumerateArray().Where(entry => !entry.GetProperty("directory").GetBoolean()))
            {
                var entryName = entry.GetProperty("path").GetString()!;
                var target = Path.Combine(work, name + "-" + Guid.NewGuid().ToString("N") + ".bin");
                await Execute("source-entry-" + name + "-" + entryName, 0, true, passwords, ["--archive-source-entry", descriptor, entryName, target]);
                check("source all entry bytes " + name + "/" + entryName, File.Exists(target)
                    && new FileInfo(target).Length == entry.GetProperty("size").GetInt64() && Hash(target) == entry.GetProperty("sha256").GetString(), "");
                products.Add(new { caseName = name, entry = entryName, path = target });
            }
            if (passwords is not null)
            {
                await Reject(name + "-no-password", descriptor, "leaf.txt");
                if (!passwords.StartsWith('\n'))
                    await Reject(name + "-wrong-first", descriptor, "leaf.txt", "wrong-password\ninner-source-fixture\n");
                await Reject(name + "-wrong-second", descriptor, "leaf.txt", "outer-source-fixture\nwrong-password\n");
                await Reject(name + "-password-eof", descriptor, "leaf.txt", "outer-source-fixture\n");
                await Reject(name + "-password-limit", descriptor, "leaf.txt", new string('x', 4097) + "\ninner-source-fixture\n");
            }
        }
        foreach (var name in new[] { "late-bad-sibling.zip", "bad-inner.zip", "unsupported-inner.zip", "depth-9.zip" })
            await Reject(name, Descriptor(name, Path.Combine(root, name), name == "depth-9.zip" ? Enumerable.Repeat("inner.zip", 9).ToArray() : ["inner.zip"]), "leaf.txt");

        var nestedPath = Path.Combine(root, "nested.zip");
        var normal = Descriptor("normal", nestedPath, ["inner.zip"]);
        await Reject("missing-container", Descriptor("missing-container", nestedPath, ["missing.zip"]), "leaf.txt");
        await Reject("directory-container", Descriptor("directory-container", Path.Combine(root, "leaf.zip"), ["folder"]), "leaf.txt");
        await Reject("missing-leaf", normal, "missing.txt", listFails: false);
        await Reject("directory-leaf", normal, "folder", listFails: false);
        var wrappedPath = Path.Combine(root, "wrapped-inner.zip");
        await Execute("source-shared-depth-exact", 0, true, null, ["--archive-source-list", Descriptor("depth-exact", wrappedPath, ["inner.zip.gz.bz2"], new() { ["maximumWrapperDepth"] = 3 })]);
        await Reject("shared-depth-below", Descriptor("depth-below", wrappedPath, ["inner.zip.gz.bz2"], new() { ["maximumWrapperDepth"] = 2 }), "leaf.txt");
        foreach (var limit in new[] { "maximumWorkBytes", "maximumPathCharacters", "maximumEntryBytes", "maximumInputBytes" })
            await Reject(limit, Descriptor(limit, nestedPath, ["inner.zip"], new() { [limit] = 1 }), "leaf.txt");
        await Reject("maximumOutputBytes", Descriptor("output-budget", nestedPath, ["inner.zip"], new() { ["maximumOutputBytes"] = 1 }), "leaf.txt", listFails: false);

        // 同サイズ・同mtimeの差替えでも、確定root SHAを使って拒否する。
        var swapped = Path.Combine(work, "swapped.zip"); File.Copy(Path.Combine(root, "root-a.zip"), swapped);
        var originalTime = File.GetLastWriteTimeUtc(swapped); var originalSize = new FileInfo(swapped).Length;
        var stale = Descriptor("stale-root", swapped, [], expectedSha: Hash(swapped));
        File.WriteAllBytes(swapped, File.ReadAllBytes(Path.Combine(root, "root-b.zip"))); File.SetLastWriteTimeUtc(swapped, originalTime);
        check("source root replacement keeps size and mtime", new FileInfo(swapped).Length == originalSize && File.GetLastWriteTimeUtc(swapped) == originalTime, "");
        await Reject("stale-root", stale, "same.txt");

        // descriptorのJSON・上限・chainを曖昧に解釈しない。
        var pathJson = JsonSerializer.Serialize(nestedPath);
        var invalid = new Dictionary<string, string>
        {
            ["unknown-property"] = "{\"rootPath\":" + pathJson + ",\"password\":\"must-not-execute\"}",
            ["duplicate-property"] = "{\"rootPath\":" + pathJson + ",\"rootPath\":" + pathJson + "}",
            ["bad-sha"] = "{\"rootPath\":" + pathJson + ",\"rootSha256\":\"not-sha\"}",
            ["unsafe-chain"] = "{\"rootPath\":" + pathJson + ",\"entryChain\":[\"../inner.zip\"]}",
            ["zero-limit"] = "{\"rootPath\":" + pathJson + ",\"limits\":{\"maximumEntries\":0}}",
            ["increase-limit"] = "{\"rootPath\":" + pathJson + ",\"limits\":{\"maximumWrapperDepth\":9}}",
            ["fractional-limit"] = "{\"rootPath\":" + pathJson + ",\"limits\":{\"maximumEntries\":1.5}}",
            ["unknown-limit"] = "{\"rootPath\":" + pathJson + ",\"limits\":{\"unknown\":1}}",
            ["duplicate-limit"] = "{\"rootPath\":" + pathJson + ",\"limits\":{\"maximumEntries\":2,\"maximumEntries\":3}}",
            ["not-object"] = "[]",
            ["oversized"] = new string(' ', 1024 * 1024 + 1),
        };
        foreach (var item in invalid)
        {
            var path = Path.Combine(work, item.Key + ".json"); File.WriteAllText(path, item.Value);
            await Reject(item.Key, path, "leaf.txt");
        }
        await VerifyProtectedOutputs();
        check("source originals preserved", originals.All(item => Hash(item.Key) == item.Value), "");
        check("source no output stage remains", !Directory.EnumerateFileSystemEntries(work, ".diffbeacon*", SearchOption.AllDirectories).Any(), "");
        var productsPath = Path.Combine(work, "products.json"); File.WriteAllText(productsPath, JsonSerializer.Serialize(products));
        var proofPath = Path.Combine(work, "independent-proof.json");
        var start = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-X", "utf8", Path.Combine(root, "regenerate.py"), "--proof", proofPath, "--products", productsPath }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync(); var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        File.WriteAllText(Path.Combine(work, "independent.stdout.txt"), await stdoutTask);
        File.WriteAllText(Path.Combine(work, "independent.stderr.txt"), await stderrTask);
        File.WriteAllText(Path.Combine(work, "independent.actual-exit.txt"), process.ExitCode.ToString());
        check("source independent fixed containers and all products", process.ExitCode == 0 && File.Exists(proofPath), await stderrTask);

        string Descriptor(string name, string rootPath, string[] chain, Dictionary<string, long>? limits = null, string? expectedSha = null)
        {
            var descriptor = Path.Combine(work, name + ".json");
            File.WriteAllText(descriptor, JsonSerializer.Serialize(new { rootPath = Path.GetRelativePath(work, rootPath).Replace('\\', '/'), entryChain = chain, rootSha256 = expectedSha ?? Hash(rootPath), limits = limits ?? new() }));
            return descriptor;
        }
        Task<CommandResult> Execute(string name, int exit, bool json, string? passwords, string[] arguments) => passwords is null
            ? run(name, exit, json, arguments) : runInput(name, exit, json, passwords, [.. arguments, "--password-stdin"]);
        async Task Reject(string name, string descriptor, string entry, string? passwords = null, bool listFails = true)
        {
            if (listFails)
            {
                var result = await Execute("source-reject-list-" + name, 2, false, passwords, ["--archive-source-list", descriptor]);
                check("source rejected list has no partial stdout " + name, result.Stdout.Length == 0 && result.Stderr.Length > 0, "");
            }
            var target = Path.Combine(work, "protected-" + Guid.NewGuid().ToString("N") + ".bin"); File.WriteAllText(target, "protected source output\r\n"); var before = Hash(target);
            var exported = await Execute("source-reject-entry-" + name, 2, false, passwords, ["--archive-source-entry", descriptor, entry, target]);
            check("source rejection preserves output " + name, Hash(target) == before && exported.Stdout.Length == 0, "");
        }
        void VerifyManifest(string name, JsonElement actual, JsonElement expected)
        {
            var entries = actual.GetProperty("entries").EnumerateArray().ToArray();
            check("source all manifest content " + name, entries.Length == expected.GetArrayLength() && expected.EnumerateArray().All(entry => entries.Any(item =>
                item.GetProperty("path").GetString() == entry.GetProperty("path").GetString()
                && item.GetProperty("directory").GetBoolean() == entry.GetProperty("directory").GetBoolean()
                && item.GetProperty("size").GetInt64() == entry.GetProperty("size").GetInt64()
                && item.GetProperty("sha256").GetString() == entry.GetProperty("sha256").GetString())), "independent Python fixed golden; no extra or lost entries");
        }
        async Task VerifyProtectedOutputs()
        {
            var ownedRoot = Path.Combine(work, "protected-root.zip"); File.Copy(nestedPath, ownedRoot);
            var descriptor = Descriptor("protected-input", ownedRoot, ["inner.zip"]);
            foreach (var target in new[] { ownedRoot, descriptor })
            {
                var before = Hash(target);
                await run("source-input-output-protection-" + Path.GetFileName(target), 2, false, ["--archive-source-entry", descriptor, "leaf.txt", target]);
                check("source physical input preserved " + Path.GetFileName(target), Hash(target) == before, "");
            }
            var readonlyPath = Path.Combine(work, "readonly.bin"); File.WriteAllText(readonlyPath, "readonly output"); var readonlyBefore = Hash(readonlyPath);
            var attributes = File.GetAttributes(readonlyPath); File.SetAttributes(readonlyPath, attributes | FileAttributes.ReadOnly);
            try
            {
                await run("source-readonly-output", 2, false, ["--archive-source-entry", normal, "leaf.txt", readonlyPath]);
                check("source readonly preserved", Hash(readonlyPath) == readonlyBefore && File.GetAttributes(readonlyPath).HasFlag(FileAttributes.ReadOnly), "");
            }
            finally { File.SetAttributes(readonlyPath, attributes); }
            // linkだけを小さいsibling rootへ分離し、通常のfull runを正規圧縮／清掃可能にする。
            var linkRoot = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + "-source-links-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(linkRoot);
            var linkedRoot = Path.Combine(linkRoot, "protected.zip"); File.Copy(nestedPath, linkedRoot);
            var linkedDescriptor = Path.Combine(linkRoot, "descriptor.json");
            File.WriteAllText(linkedDescriptor, JsonSerializer.Serialize(new { rootPath = linkedRoot, entryChain = new[] { "inner.zip" } }));
            var link = Path.Combine(linkRoot, "output-link.bin"); var linkTarget = Path.Combine(linkRoot, "link-target.bin"); File.WriteAllText(linkTarget, "protected link target");
            try
            {
                File.CreateSymbolicLink(link, linkTarget); var before = Hash(linkTarget);
                await run("source-link-output", 2, false, ["--archive-source-entry", normal, "leaf.txt", link]);
                check("source linked output target preserved", Hash(linkTarget) == before && new FileInfo(link).LinkTarget == linkTarget, "owned run link retained for prescribed cleanup");
                var sourceLink = Path.Combine(linkRoot, "source-link.zip"); File.CreateSymbolicLink(sourceLink, linkedRoot);
                await Reject("linked-root", Descriptor("linked-root", sourceLink, ["inner.zip"]), "leaf.txt");
                var descriptorLink = Path.Combine(linkRoot, "descriptor-link.json"); File.CreateSymbolicLink(descriptorLink, linkedDescriptor);
                await Reject("linked-descriptor", descriptorLink, "leaf.txt");
                var ledger = JsonSerializer.Serialize(new { linkRoot, items = new[] { new { path = link, target = linkTarget }, new { path = sourceLink, target = linkedRoot }, new { path = descriptorLink, target = linkedDescriptor } }, mainRunContainsNoSourceLinks = true });
                File.WriteAllText(Path.Combine(work, "retained-links.json"), ledger); File.WriteAllText(Path.Combine(linkRoot, "retained-links.json"), ledger);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
            { skip("source links require platform permission", exception.Message); }
            catch (IOException exception) when (exception.HResult == unchecked((int)0x80070522))
            { skip("source links require Windows symbolic-link privilege", exception.Message); }
        }
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
}
