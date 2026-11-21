using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static partial class BareGZipScenarios
{
    // 失敗モデルと未検証境界はgzip-mode-proof.jsonへ記録する。旧Autoケースは変更しない。
    private static async Task RunPayloadKindsAsync(string output, string work,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var modeWork = Path.Combine(work, "payload-kinds"); Directory.CreateDirectory(modeWork);
        var invoked = new List<object>();
        var originals = new Dictionary<string, string>();
        async Task<CommandResult> Invoke(string id, int exit, bool json, params string[] args)
        {
            var result = await run("gzip-mode-" + id, exit, json, args);
            invoked.Add(new { name = "gzip-mode-" + id, expectedExitCode = exit, actualExitCode = result.ExitCode, args });
            return result;
        }
        string Save(string id, JsonObject value)
        {
            var path = Path.Combine(modeWork, id + ".json"); File.WriteAllText(path, value.ToJsonString()); return path;
        }
        JsonObject Descriptor(string path, string[] chain, string[] modes, JsonObject? limits = null)
        {
            var result = new JsonObject { ["rootPath"] = path, ["entryChain"] = JsonSerializer.SerializeToNode(chain),
                ["containerGZipPayloadKinds"] = JsonSerializer.SerializeToNode(modes) };
            if (limits is not null) result["limits"] = limits.DeepClone();
            return result;
        }
        var protectedOutput = Path.Combine(modeWork, "protected.zip");
        var sentinel = "existing output retained"u8.ToArray(); File.WriteAllBytes(protectedOutput, sentinel);
        async Task Reject(string id, string input, params string[] args)
        {
            var before = Hash(input); var result = await Invoke(id, 2, false, args);
            check("gzip mode rejection preserves " + id, result.Stdout.Length == 0 && result.Stderr.Length > 0
                && Hash(input) == before && File.ReadAllBytes(protectedOutput).SequenceEqual(sentinel), "source and sentinel output unchanged");
        }
        void FileManifest(string id, string stdout, string entry, byte[] bytes)
        {
            using var doc = JsonDocument.Parse(stdout); var rows = doc.RootElement.GetProperty("entries");
            check("gzip mode complete file manifest " + id, doc.RootElement.GetProperty("format").GetString() == "gzip" && rows.GetArrayLength() == 1
                && rows[0].GetProperty("path").GetString() == entry && !rows[0].GetProperty("directory").GetBoolean()
                && rows[0].GetProperty("size").GetInt64() == bytes.Length
                && rows[0].GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(bytes)), "independent raw full bytes, including TAR-looking data");
        }
        using var seed = JsonDocument.Parse(File.ReadAllBytes(Path.GetFullPath("tests/Fixtures/Archives/BareGZip/expected.json")));
        foreach (var row in seed.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = row.GetProperty("id").GetString()!; var name = row.GetProperty("input").GetString()!;
            var input = Path.Combine(work, name); originals[input] = Hash(input);
            var metadata = row.GetProperty("firstMemberMetadata"); var nameInfo = metadata.GetProperty("perCodePage").GetProperty("28591");
            var fallback = metadata.GetProperty("fallbackRequired").GetBoolean();
            var validFile = row.GetProperty("expectedGzipStructurallyValid").GetBoolean()
                && (fallback || nameInfo.GetProperty("decodeAccepted").GetBoolean() && nameInfo.GetProperty("safeEntryPathAccepted").GetBoolean());
            var entry = fallback ? DefaultName(name) : nameInfo.TryGetProperty("normalizedEntryPath", out var normalized) ? normalized.GetString()! : "unused";
            var bytes = Convert.FromHexString(row.GetProperty("expectedPayloadFullHex").GetString()!);
            foreach (var mode in new[] { "auto", "file", "tar" })
            {
                var valid = mode != "tar" && validFile && (mode == "file" || id != "misleading-tar-gz");
                var result = await Invoke("seed-list-" + id + "-" + mode, valid ? 0 : 2, valid,
                    "--archive-list", input, "--gzip-payload-kind", mode);
                if (valid && result.ExitCode == 0)
                {
                    FileManifest(id + mode, result.Stdout, entry, bytes);
                    var export = Path.Combine(modeWork, "seed-" + id + "-" + mode + ".bin");
                    await Invoke("seed-entry-" + id + "-" + mode, 0, true, "--archive-entry", input, entry, export, "--gzip-payload-kind", mode);
                    check("gzip mode full exported seed " + id + mode, File.Exists(export) && File.ReadAllBytes(export).SequenceEqual(bytes), "full independent payload hex");
                }
                else if (!valid)
                    await Reject("seed-protected-" + id + "-" + mode, input, "--archive-repack", input, protectedOutput, "--gzip-payload-kind", mode);
            }
        }
        var sourceRoot = Path.GetFullPath("tests/Fixtures/Archives/Sources");
        var rawTarPath = Path.Combine(sourceRoot, "small.tar"); var fixedGzip = Path.Combine(sourceRoot, "unknown.gz");
        originals[rawTarPath] = Hash(rawTarPath); originals[fixedGzip] = Hash(fixedGzip);
        check("gzip mode fixed raw TAR SHA", originals[rawTarPath] == "45C8A28CEDCA660D1965A0EB419B3DCA52999F12FF6FE5EC29FDC01565B58409", "independent TAR raw original");
        check("gzip mode fixed gzip SHA", originals[fixedGzip] == "0CB0AD76C0D0758470243D4C4C23CB10B4D6C1C1883DE5DE15E2FFFB048BE0BB", "unchanged Source original");
        var rawTar = File.ReadAllBytes(rawTarPath); var gzip = File.ReadAllBytes(fixedGzip);
        using var sourceGolden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sourceRoot, "manifest.json")));
        var tarGolden = sourceGolden.RootElement.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("name").GetString() == "unknown-gzip");
        void TarManifest(string id, string stdout, bool empty = false)
        {
            using var doc = JsonDocument.Parse(stdout); var rows = doc.RootElement.GetProperty("entries");
            var expectedRows = tarGolden.GetProperty("entries");
            check("gzip mode TAR manifest " + id, doc.RootElement.GetProperty("format").GetString() == "tar.gz"
                && rows.GetArrayLength() == (empty ? 0 : expectedRows.GetArrayLength())
                && (empty || rows.EnumerateArray().Zip(expectedRows.EnumerateArray()).All(pair =>
                    pair.First.GetProperty("path").GetString() == pair.Second.GetProperty("path").GetString()
                    && pair.First.GetProperty("directory").GetBoolean() == pair.Second.GetProperty("directory").GetBoolean()
                    && pair.First.GetProperty("size").GetInt64() == pair.Second.GetProperty("size").GetInt64()
                    && pair.First.GetProperty("sha256").GetString() == pair.Second.GetProperty("sha256").GetString())), "fixed independently decoded entries");
        }
        foreach (var (id, bytes) in new[] { ("tar", rawTar), ("empty", Array.Empty<byte>()), ("zero512", new byte[512]),
            ("zero1024", new byte[1024]), ("tar-header-only", rawTar[..512]) })
        foreach (var suffix in new[] { ".gz", ".tar.gz", ".zip.gz" })
        {
            var input = Path.Combine(modeWork, id + suffix); File.WriteAllBytes(input, ModeCompress(bytes)); originals[input] = Hash(input);
            foreach (var mode in new[] { "auto", "file", "tar" })
            {
                // Autoの.zip.gzは明示ZIP終端を要求するため、この非ZIP行列では拒否する。
                var isTar = id is "tar" or "zero1024";
                var valid = mode == "file" || mode == "tar" && isTar || mode == "auto" && suffix != ".zip.gz" && (isTar || id == "empty" && suffix == ".gz");
                var tag = id + suffix + "-" + mode;
                var result = await Invoke("matrix-list-" + tag, valid ? 0 : 2, valid, "--archive-list", input, "--gzip-payload-kind", mode);
                if (valid && result.ExitCode == 0)
                {
                    if (mode == "file" || !isTar)
                    {
                        var entry = DefaultName(Path.GetFileName(input)); FileManifest(tag, result.Stdout, entry, bytes);
                        var exported = Path.Combine(modeWork, "matrix-export-" + tag + ".bin");
                        await Invoke("matrix-entry-" + tag, 0, true, "--archive-entry", input, entry, exported, "--gzip-payload-kind", mode);
                        check("gzip mode matrix raw bytes " + tag, File.Exists(exported) && File.ReadAllBytes(exported).SequenceEqual(bytes), "no TAR sniff/wrapper in File");
                    }
                    else TarManifest(tag, result.Stdout, id == "zero1024");
                }
                else if (!valid) await Reject("matrix-protected-" + tag, input, "--archive-repack", input, protectedOutput, "--gzip-payload-kind", mode);
            }
        }
        foreach (var mode in new[] { "file", "tar" })
        {
            var repacked = Path.Combine(modeWork, "explicit-repacked-" + mode + ".zip");
            await Invoke("repack-success-" + mode, 0, true, "--archive-repack", fixedGzip, repacked, "--gzip-payload-kind", mode);
            if (File.Exists(repacked))
            {
                using var zip = ZipFile.OpenRead(repacked);
                if (mode == "file")
                {
                    using var stream = zip.Entries.Single().Open(); using var body = new MemoryStream(); stream.CopyTo(body);
                    check("gzip mode independent File repack", zip.Entries.Single().FullName == "unknown" && body.ToArray().SequenceEqual(rawTar), "standard ZIP reader and fixed raw TAR bytes");
                }
                else
                {
                    var expectedRows = tarGolden.GetProperty("entries").EnumerateArray().ToArray();
                    check("gzip mode independent Tar repack count", zip.Entries.Count == expectedRows.Length, "all fixed TAR entries");
                    foreach (var row in expectedRows)
                    {
                        var z = zip.GetEntry(row.GetProperty("path").GetString()!) ?? throw new IOException("Expected TAR entry absent in ZIP.");
                        using var stream = z.Open(); using var body = new MemoryStream(); stream.CopyTo(body);
                        check("gzip mode independent Tar repack bytes " + z.FullName, z.Length == row.GetProperty("size").GetInt64()
                            && Convert.ToHexString(SHA256.HashData(body.ToArray())) == row.GetProperty("sha256").GetString(), "full fixed manifest SHA and size");
                    }
                }
            }
            var extracted = Path.Combine(modeWork, "explicit-extracted-" + mode);
            await Invoke("extract-success-" + mode, 0, true, "--archive-extract", fixedGzip, extracted, "--gzip-payload-kind", mode);
            if (mode == "file") check("gzip mode File extraction raw bytes", File.Exists(Path.Combine(extracted, "unknown"))
                && File.ReadAllBytes(Path.Combine(extracted, "unknown")).SequenceEqual(rawTar), "all TAR payload retained as one file");
            else foreach (var row in tarGolden.GetProperty("entries").EnumerateArray())
            {
                var leaf = Path.Combine(extracted, row.GetProperty("path").GetString()!);
                check("gzip mode Tar extraction bytes " + Path.GetFileName(leaf), File.Exists(leaf) && new FileInfo(leaf).Length == row.GetProperty("size").GetInt64()
                    && Hash(leaf) == row.GetProperty("sha256").GetString(), "independent fixed full-entry golden");
            }
        }
        foreach (var mode in new[] { "Auto", "File", "Tar" })
        {
            var limits = new JsonObject { ["maximumDecodedBytes"] = rawTar.Length, ["maximumEntryBytes"] = mode == "File" ? rawTar.Length : 35,
                ["maximumEntries"] = mode == "File" ? 2 : 3, ["maximumPathCharacters"] = mode == "File" ? 14 : 34 };
            var exact = await Invoke("root-budget-exact-" + mode, 0, true, "--archive-source-list", Save("root-exact-" + mode, Descriptor(fixedGzip, [], [mode], limits)));
            if (exact.ExitCode == 0) { if (mode == "File") FileManifest("exactFile", exact.Stdout, "unknown", rawTar); else TarManifest("exact" + mode, exact.Stdout); }
            foreach (var key in new[] { "maximumDecodedBytes", "maximumEntryBytes", "maximumEntries", "maximumPathCharacters" })
            {
                var below = (JsonObject)limits.DeepClone(); below[key] = below[key]!.GetValue<int>() - 1;
                await Reject("root-budget-below-" + mode + "-" + key, fixedGzip, "--archive-source-entry",
                    Save("root-below-" + mode + "-" + key, Descriptor(fixedGzip, [], [mode], below)), mode == "File" ? "unknown" : "leaf.txt", protectedOutput);
            }
        }
        var badCrc = (byte[])gzip.Clone(); badCrc[^8] ^= 1;
        foreach (var (id, bytes) in new[] { ("tar-crc", badCrc), ("tar-footer", gzip[..^1]), ("tar-late-member", gzip.Concat(badCrc).ToArray()) })
        {
            var path = Path.Combine(modeWork, id + ".gz"); File.WriteAllBytes(path, bytes); originals[path] = Hash(path);
            foreach (var mode in new[] { "auto", "file", "tar" })
                await Reject("corrupt-" + id + "-" + mode, path, "--archive-repack", path, protectedOutput, "--gzip-payload-kind", mode);
        }
        var invalidNameTar = Path.Combine(modeWork, "tar-invalid-utf8-name.gz"); File.WriteAllBytes(invalidNameTar, ModeWithName(gzip, [0xc3, 0x28]));
        foreach (var mode in new[] { "auto", "file", "tar" })
        {
            var result = await Invoke("tar-name-decoding-" + mode, mode == "file" ? 2 : 0, mode != "file", "--archive-list", invalidNameTar,
                "--gzip-payload-kind", mode, "--gzip-name-code-page", "65001");
            if (mode != "file" && result.ExitCode == 0) TarManifest("tar-name-decoding" + mode, result.Stdout);
            if (mode == "file") check("gzip mode file name decoding rejects", result.Stdout.Length == 0 && result.Stderr.Length > 0, "Tar ignores name identity; File keeps strict decoding");
        }
        foreach (var mode in new[] { "auto", "file", "tar" })
        {
            var named = Path.Combine(modeWork, "long-name-" + mode + ".gz");
            File.WriteAllBytes(named, ModeWithName(gzip, Enumerable.Repeat((byte)'a', 20 * 1024).ToArray()));
            var result = await Invoke("long-name-" + mode, mode == "file" ? 2 : 0, mode != "file", "--archive-list", named, "--gzip-payload-kind", mode);
            if (mode != "file" && result.ExitCode == 0) TarManifest("long-name-" + mode, result.Stdout);
        }
        var utf8 = Path.Combine(work, "first-utf8.gz");
        foreach (var mode in new[] { "auto", "file" })
        {
            var utf8row = seed.RootElement.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("id").GetString() == "first-utf8");
            var result = await Invoke("utf8-name-" + mode, 0, true, "--archive-list", utf8, "--gzip-payload-kind", mode, "--gzip-name-code-page", "65001");
            if (result.ExitCode == 0) FileManifest("utf8-name-" + mode, result.Stdout,
                utf8row.GetProperty("firstMemberMetadata").GetProperty("perCodePage").GetProperty("65001").GetProperty("normalizedEntryPath").GetString()!,
                Convert.FromHexString(utf8row.GetProperty("expectedPayloadFullHex").GetString()!));
        }
        foreach (var bad in new[] { "File", "TAR", "unknown", "1", "", "file,tar" })
            await Reject("invalid-option-" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(bad)), fixedGzip,
                "--archive-repack", fixedGzip, protectedOutput, "--gzip-payload-kind", bad);
        await Reject("duplicate-option", fixedGzip, "--archive-repack", fixedGzip, protectedOutput, "--gzip-payload-kind", "file", "--gzip-payload-kind", "tar");
        await Reject("right-option-noncompare", fixedGzip, "--archive-repack", fixedGzip, protectedOutput, "--right-gzip-payload-kind", "file");
        await Invoke("compare-independent", 1, true, "--archive-compare", fixedGzip, fixedGzip, "--gzip-payload-kind", "file", "--right-gzip-payload-kind", "tar");
        await Invoke("compare-inherited", 0, true, "--archive-compare", fixedGzip, fixedGzip, "--gzip-payload-kind", "file");
        await Invoke("compare-explicit-equal", 0, true, "--archive-compare", fixedGzip, fixedGzip, "--gzip-payload-kind", "tar", "--right-gzip-payload-kind", "tar");
        foreach (var mode in new[] { "File", "Tar" })
        {
            await Reject("nongzip-" + mode, rawTarPath, "--archive-source-entry", Save("nongzip-" + mode, Descriptor(rawTarPath, [], [mode])), "leaf.txt", protectedOutput);
            await Reject("nongzip-cli-" + mode, rawTarPath, "--archive-repack", rawTarPath, protectedOutput, "--gzip-payload-kind", mode.ToLowerInvariant());
        }
        var nestedZip = Path.Combine(modeWork, "nested.zip");
        using (var zip = ZipFile.Open(nestedZip, ZipArchiveMode.Create)) { using var stream = zip.CreateEntry("inner.gz").Open(); stream.Write(gzip); }
        var nestedOuter = Path.Combine(modeWork, "outer.gz"); File.WriteAllBytes(nestedOuter, ModeCompress(gzip)); originals[nestedZip] = Hash(nestedZip); originals[nestedOuter] = Hash(nestedOuter);
        foreach (var root in new[] { ("zip", nestedZip, "Auto", "inner.gz", "inner"), ("gzip", nestedOuter, "File", "outer", "noname") })
        foreach (var inner in new[] { "Auto", "File", "Tar" })
        {
            var descriptor = Save("nested-" + root.Item1 + "-" + inner, Descriptor(root.Item2, [root.Item4], [root.Item3, inner]));
            var result = await Invoke("nested-list-" + root.Item1 + "-" + inner, 0, true, "--archive-source-list", descriptor);
            if (result.ExitCode == 0) { if (inner == "File") FileManifest(root.Item1 + inner, result.Stdout, root.Item5, rawTar); else TarManifest(root.Item1 + inner, result.Stdout); }
            if (inner == "File")
            {
                var exported = Path.Combine(modeWork, "nested-export-" + root.Item1 + ".tar");
                await Invoke("nested-entry-" + root.Item1, 0, true, "--archive-source-entry", descriptor, root.Item5, exported);
                check("gzip mode independent nested raw " + root.Item1, File.Exists(exported) && File.ReadAllBytes(exported).SequenceEqual(rawTar), "each addressed gzip selects independently");
            }
            foreach (var limit in new[] { "maximumDecodedBytes", "maximumEntryBytes", "maximumEntries", "maximumPathCharacters", "maximumWrapperDepth", "maximumWorkBytes", "maximumInputBytes", "maximumOutputBytes" })
                await Reject("nested-budget-" + root.Item1 + "-" + inner + "-" + limit, root.Item2, "--archive-source-entry",
                    Save("budget-" + root.Item1 + "-" + inner + "-" + limit, Descriptor(root.Item2, [root.Item4], [root.Item3, inner], new JsonObject { [limit] = 1 })),
                    inner == "File" ? root.Item5 : "leaf.txt", protectedOutput);
        }
        foreach (var modes in new[] { Array.Empty<string>(), new[] { "Auto" }, new[] { "Auto", "Auto", "Auto" }, new[] { "Auto", "File", "Tar" } })
            await Reject("nested-descriptor-length-" + modes.Length + "-" + string.Join("-", modes), nestedZip, "--archive-source-entry",
                Save("nested-descriptor-length-" + modes.Length + "-" + string.Join("-", modes), Descriptor(nestedZip, ["inner.gz"], modes)), "inner", protectedOutput);
        var variants = new (string Id, JsonNode? Value)[] { ("empty", new JsonArray()), ("too-long", new JsonArray("Auto", "File")), ("too-long-allAuto", new JsonArray("Auto", "Auto")),
            ("lowercase", new JsonArray("file")), ("unknown", new JsonArray("Unknown")), ("integer", new JsonArray(1)), ("object", new JsonObject()), ("null-element", new JsonArray((JsonNode?)null)) };
        foreach (var (id, value) in variants)
        {
            var desc = Descriptor(fixedGzip, [], ["File"]); desc["containerGZipPayloadKinds"] = value?.DeepClone();
            await Reject("descriptor-invalid-" + id, fixedGzip, "--archive-source-entry", Save("descriptor-" + id, desc), "unknown", protectedOutput);
        }
        foreach (var choice in new[] { "null", "allAuto", "omitted" })
        {
            var descriptor = Descriptor(fixedGzip, [], ["Auto"]);
            if (choice == "null") descriptor["containerGZipPayloadKinds"] = null;
            if (choice == "omitted") descriptor.Remove("containerGZipPayloadKinds");
            var result = await Invoke("descriptor-default-" + choice, 0, true, "--archive-source-list", Save("descriptor-default-" + choice, descriptor));
            if (result.ExitCode == 0) TarManifest("default" + choice, result.Stdout);
        }
        var unknown = Descriptor(fixedGzip, [], ["File"]); unknown["unknownModeField"] = true;
        await Reject("descriptor-unknown-property", fixedGzip, "--archive-source-entry", Save("descriptor-unknown", unknown), "unknown", protectedOutput);
        var duplicate = Path.Combine(modeWork, "descriptor-duplicate.json");
        var clean = Descriptor(fixedGzip, [], ["File"]).ToJsonString();
        File.WriteAllText(duplicate, clean[..^1] + ",\"containerGZipPayloadKinds\":[\"Tar\"]}");
        await Reject("descriptor-duplicate-property", fixedGzip, "--archive-source-entry", duplicate, "unknown", protectedOutput);
        await VerifyModeWorkspaceAsync();
        foreach (var (path, sha) in originals) check("gzip mode original retained " + Path.GetFileName(path), Hash(path) == sha, "no fixed input regenerated");
        File.WriteAllBytes(Path.Combine(output, "gzip-mode-proof.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            commandCount = invoked.Count, commands = invoked, originals,
            notExecuted = new[] { "deterministic mid-probe cancellation (run delegate has no injection)", "native GUI/dialogs" },
            golden = "fixed payload hex, Sources small.tar SHA and fixed full TAR entry manifest; never product stdout"
        }, new JsonSerializerOptions { WriteIndented = true }));

        async Task VerifyModeWorkspaceAsync()
        {
            var original = seed.RootElement.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("id").GetString() == "first-name-differs");
            var input = Path.Combine(work, original.GetProperty("input").GetString()!);
            var bytes = Convert.FromHexString(original.GetProperty("expectedPayloadFullHex").GetString()!);
            var expectedAssets = new Dictionary<string, byte[]> { ["Auto"] = bytes, ["File"] = [.. bytes, 0x42] };
            var working = new Dictionary<string, JsonObject>();
            var sides = new[] { "leftArchiveInput", "baseArchiveInput", "rightArchiveInput" };
            var expectedModes = new[] { "Auto", "File" };
            foreach (var (mode, body) in expectedAssets)
            {
                var asset = Path.Combine(modeWork, "asset-" + mode + ".bin"); File.WriteAllBytes(asset, body);
                working[mode] = new JsonObject { ["entryChain"] = new JsonArray(), ["leafEntry"] = "ChosenName.TXT", ["containerGZipPayloadKinds"] = new JsonArray(mode),
                    ["snapshotPath"] = Path.GetFileName(asset), ["sha256"] = Hash(asset), ["encodingName"] = "", ["hasBom"] = false, ["kind"] = "Binary" };
            }
            JsonObject Entry(string mode)
            {
                var archive = Descriptor(input, [], [mode]); archive["leafEntry"] = "ChosenName.TXT";
                archive["rootSha256"] = Hash(input); archive["inheritedReadOnly"] = false;
                archive["workingTexts"] = new JsonArray(working[mode].DeepClone());
                return new JsonObject { ["mode"] = "Binary", ["leftArchiveInput"] = archive.DeepClone(), ["baseArchiveInput"] = archive.DeepClone(),
                    ["rightArchiveInput"] = archive.DeepClone(), ["leftReadOnly"] = true, ["baseReadOnly"] = true, ["rightReadOnly"] = true };
            }
            var project = new JsonObject { ["formatVersion"] = 8, ["activeEntryIndex"] = 0, ["entries"] = new JsonArray(Entry("Auto"), Entry("File")) };
            // 各保存版は親inputのmode prefixと一致必須。異なるidentityは別entryで保持する。
            var mismatch = (JsonObject)project.DeepClone(); var mismatchedEntry = Entry("File");
            foreach (var side in sides) mismatchedEntry[side]!["workingTexts"] = new JsonArray(working["Auto"].DeepClone(), working["File"].DeepClone());
            mismatch["entries"] = new JsonArray(mismatchedEntry);
            await Reject("workspace8-parent-mode-mismatch", input, "--project-copy", Save("workspace8-parent-mode-mismatch", mismatch), protectedOutput);
            var path = Save("workspace8", project); var copied = Path.Combine(modeWork, "workspace8-copy.json");
            await Invoke("workspace8-copy", 0, true, "--project-copy", path, copied); Verify(copied, "copy");
            var cloned = Path.Combine(modeWork, "workspace8-clone.json");
            await Invoke("workspace8-recopy", 0, true, "--project-copy", copied, cloned); Verify(cloned, "recopy");
            var package = Path.Combine(modeWork, "workspace8.zip");
            await Invoke("workspace8-package", 0, true, "--package-project", path, package);
            if (File.Exists(package))
            {
                using var zip = ZipFile.OpenRead(package);
                var projectEntry = zip.GetEntry("project.json") ?? throw new IOException("Packaged project.json missing.");
                using var projectStream = projectEntry.Open(); using var packageProject = JsonDocument.Parse(projectStream);
                var entries = packageProject.RootElement.GetProperty("entries");
                check("gzip mode independent ZIP entry count", entries.GetArrayLength() == 2, "both independently selected input identities retained");
                for (var index = 0; index < entries.GetArrayLength(); index++)
                foreach (var side in sides)
                {
                    var expectedMode = expectedModes[index]; var archive = entries[index].GetProperty(side); var rows = archive.GetProperty("workingTexts");
                    check("gzip mode independent ZIP input identity " + index + side, ModeOf(archive) == expectedMode && rows.GetArrayLength() == 1
                        && ModeOf(rows[0]) == expectedMode, "parent and one snapshot share the same mode prefix");
                    foreach (var row in rows.EnumerateArray())
                    {
                        var assetPath = row.GetProperty("snapshotPath").GetString()!.Replace('\\', '/');
                        var assetEntry = zip.GetEntry(assetPath) ?? throw new IOException("Packaged mode asset missing.");
                        using var assetStream = assetEntry.Open(); using var buffer = new MemoryStream(); assetStream.CopyTo(buffer);
                        check("gzip mode independent ZIP saved asset " + index + side + expectedMode,
                            buffer.ToArray().SequenceEqual(expectedAssets[expectedMode]) && assetEntry.Length == expectedAssets[expectedMode].Length,
                            "standard ZIP reader, independent complete expected bytes");
                    }
                }
            }
            var unpack = Path.Combine(modeWork, "workspace8-unpack");
            await Invoke("workspace8-extract", 0, true, "--archive-extract", package, unpack); Verify(Path.Combine(unpack, "project.json"), "package");
            var restored = Path.Combine(modeWork, "workspace8-restored.json");
            await Invoke("workspace8-restored-copy", 0, true, "--project-copy", Path.Combine(unpack, "project.json"), restored); Verify(restored, "restored");
            foreach (var defaultMode in new[] { "null", "allAuto" })
            {
                var defaults = new JsonObject { ["formatVersion"] = 8, ["activeEntryIndex"] = 0, ["entries"] = new JsonArray(Entry("Auto")) };
                foreach (var side in sides)
                {
                    var inputNode = defaults["entries"]![0]![side]!.AsObject();
                    inputNode["containerGZipPayloadKinds"] = defaultMode == "null" ? null : new JsonArray("Auto");
                    inputNode["workingTexts"]![0]!["containerGZipPayloadKinds"] = defaultMode == "null" ? null : new JsonArray("Auto");
                }
                var oldDefaults = (JsonObject)defaults.DeepClone(); oldDefaults["formatVersion"] = 7;
                await Reject("workspace7-default-property-" + defaultMode, input, "--project-copy", Save("workspace7-default-invalid-" + defaultMode, oldDefaults), protectedOutput);
                var defaultCopy = Path.Combine(modeWork, "workspace8-default-copy-" + defaultMode + ".json");
                var defaultPath = Save("workspace8-default-" + defaultMode, defaults);
                await Invoke("workspace8-default-copy-" + defaultMode, 0, true, "--project-copy", defaultPath, defaultCopy);
                var defaultPackage = Path.Combine(modeWork, "workspace8-default-" + defaultMode + ".zip");
                await Invoke("workspace8-default-package-" + defaultMode, 0, true, "--package-project", defaultPath, defaultPackage);
                check("gzip mode default package exists " + defaultMode, File.Exists(defaultPackage), "explicit v8 input with Auto identity");
                if (File.Exists(defaultPackage))
                {
                    using var zip = ZipFile.OpenRead(defaultPackage);
                    var packageProjectEntry = zip.GetEntry("project.json") ?? throw new IOException("Default packaged project missing.");
                    using var stream = packageProjectEntry.Open(); using var packaged = JsonDocument.Parse(stream);
                    var packageEntries = packaged.RootElement.GetProperty("entries");
                    check("gzip mode defaults packaged same version " + defaultMode, packaged.RootElement.GetProperty("formatVersion").GetInt32() == 8
                        && packageEntries.GetArrayLength() == 1, "independent ZIP reader preserves explicit v8 envelope");
                    foreach (var side in sides)
                    {
                        var archive = packageEntries[0].GetProperty(side); var snapshots = archive.GetProperty("workingTexts");
                        check("gzip mode default package identity " + defaultMode + side, ModeOf(archive) == "Auto" && snapshots.GetArrayLength() == 1
                            && ModeOf(snapshots[0]) == "Auto", "null/allAuto have the same parent and snapshot identity");
                        foreach (var row in snapshots.EnumerateArray())
                        {
                            var assetName = row.GetProperty("snapshotPath").GetString()!.Replace('\\', '/');
                            var assetEntry = zip.GetEntry(assetName) ?? throw new IOException("Default packaged asset missing.");
                            using var assetStream = assetEntry.Open(); using var buffer = new MemoryStream(); assetStream.CopyTo(buffer);
                            check("gzip mode default package independent bytes " + defaultMode + side, assetEntry.Length == bytes.Length
                                && buffer.ToArray().SequenceEqual(bytes) && row.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(bytes)),
                                "standard ZIP reader, independent fixed full raw bytes and SHA");
                        }
                    }
                }
                check("gzip mode default copy exists " + defaultMode, File.Exists(defaultCopy), "single Auto entry, parent/snapshot mode prefix match");
                if (!File.Exists(defaultCopy)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllBytes(defaultCopy));
                check("gzip mode defaults same version " + defaultMode, doc.RootElement.GetProperty("formatVersion").GetInt32() == 8
                    && doc.RootElement.GetProperty("entries").GetArrayLength() == 1, "null/allAuto normalize without changing explicit v8 input version");
                foreach (var side in sides)
                {
                    var stored = doc.RootElement.GetProperty("entries")[0].GetProperty(side); var only = stored.GetProperty("workingTexts");
                    check("gzip mode default identity normalized " + defaultMode + side, ModeOf(stored) == "Auto" && only.GetArrayLength() == 1 && ModeOf(only[0]) == "Auto", "root and snapshot have the same Auto identity");
                    var asset = Path.GetFullPath(only[0].GetProperty("snapshotPath").GetString()!, Path.GetDirectoryName(defaultCopy)!);
                    check("gzip mode default identity bytes " + defaultMode + side, File.Exists(asset) && File.ReadAllBytes(asset).SequenceEqual(bytes), "same independent raw fixture bytes");
                }
            }
            var legacy = (JsonObject)project.DeepClone(); legacy["formatVersion"] = 7;
            await Reject("workspace7-modes", input, "--project-copy", Save("workspace7-invalid", legacy), protectedOutput);
            foreach (var alias in new[] { false, true })
            {
                var invalid = new JsonObject { ["formatVersion"] = 8, ["activeEntryIndex"] = 0, ["entries"] = new JsonArray(Entry("Auto")) };
                foreach (var side in sides)
                {
                    var inputNode = invalid["entries"]![0]![side]!.AsObject(); var first = inputNode["workingTexts"]![0]!.DeepClone(); var second = first.DeepClone();
                    if (alias) first.AsObject().Remove("containerGZipPayloadKinds");
                    inputNode["workingTexts"] = new JsonArray(first, second);
                }
                await Reject("workspace-duplicate-" + alias, input, "--project-copy", Save("workspace-duplicate-" + alias, invalid), protectedOutput);
            }
            static string ModeOf(JsonElement value) => !value.TryGetProperty("containerGZipPayloadKinds", out var modes) || modes.ValueKind == JsonValueKind.Null
                ? "Auto" : modes.ValueKind == JsonValueKind.Array && modes.GetArrayLength() == 1 ? modes[0].GetString()! : "invalid-mode-array";
            void Verify(string workspace, string stage)
            {
                check("gzip mode workspace exists " + stage, File.Exists(workspace), "public clone/package/reload route"); if (!File.Exists(workspace)) return;
                using var doc = JsonDocument.Parse(File.ReadAllBytes(workspace)); var entries = doc.RootElement.GetProperty("entries");
                check("gzip mode workspace v8 " + stage, doc.RootElement.GetProperty("formatVersion").GetInt32() == 8, "nonAuto source retained");
                check("gzip mode both input identities " + stage, entries.GetArrayLength() == 2, "same root/leaf/codepage with separate Auto and File entries");
                for (var index = 0; index < entries.GetArrayLength(); index++)
                foreach (var side in sides)
                {
                    var expectedMode = expectedModes[index]; var stored = entries[index].GetProperty(side); var rows = stored.GetProperty("workingTexts");
                    check("gzip mode root selection saved " + stage + index + side, ModeOf(stored) == expectedMode, "source identity copied in entry order");
                    check("gzip mode one matching saved identity " + stage + index + side, rows.GetArrayLength() == 1 && ModeOf(rows[0]) == expectedMode,
                        "snapshot mode prefix matches its parent container selection");
                    foreach (var row in rows.EnumerateArray())
                    {
                        var asset = Path.GetFullPath(row.GetProperty("snapshotPath").GetString()!, Path.GetDirectoryName(workspace)!);
                        check("gzip mode saved asset full bytes " + stage + index + side + expectedMode, File.Exists(asset)
                            && File.ReadAllBytes(asset).SequenceEqual(expectedAssets[expectedMode])
                            && row.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(expectedAssets[expectedMode])),
                            "separate mode asset bytes and SHA independent of stdout");
                    }
                }
            }
        }
    }
    private static byte[] ModeCompress(byte[] bytes)
    {
        // 空入力もRFC 1952の完全なmemberにする（raw DEFLATE 03 00、CRC32/ISIZEは0）。
        if (bytes.Length == 0) return Convert.FromHexString("1F8B080000000000000303000000000000000000");
        using var buffer = new MemoryStream(); using (var stream = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true)) stream.Write(bytes); return buffer.ToArray();
    }
    private static byte[] ModeWithName(byte[] gzip, byte[] name)
    {
        if (gzip.Length < 18 || gzip[3] != 0) throw new IOException("Fixed gzip header must have no optional fields.");
        var header = gzip[..10]; header[3] = 8; return [.. header, .. name, 0, .. gzip[10..]];
    }
}