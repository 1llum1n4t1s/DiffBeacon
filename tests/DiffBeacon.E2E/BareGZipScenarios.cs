using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static partial class BareGZipScenarios
{
    // 失敗条件: 全memberのCRC/FHCRC/ISIZE/EOF、危険名、厳密復号、共有予算、旧schemaへの設定混入、出力保護。
    // 曖昧gzip: 固定unknown TARの全entryを保持し、非TARは裸file。FNAMEでは追加形式へ再帰しない。
    // TAR総量は単entry上限と分離しDecodedを一度だけ課金。長い無視対象名と後続破損も全検証する。
    // 零payloadは旧TAR sniffを優先するため512零は不完全TAR拒否、1024零はempty TAR（同bytesの裸fileとは区別不能）。
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, string python = "python")
    {
        var root = Path.GetFullPath("tests/Fixtures/Archives/BareGZip");
        var work = Path.Combine(fixtures, "bare-gzip"); Directory.CreateDirectory(work);
        var originals = Directory.GetFiles(root).ToDictionary(path => path, Hash);
        check("bare gzip fixed seed ZIP", Hash(Path.Combine(root, "bare-gzip-inputs.zip")) == "929A192FA6BAAD4A79CAE6789E5B3919AF66F110C2C18A7EF9B700417CF8F76D", "own CC0 originals");
        check("bare gzip fixed seed expectations", Hash(Path.Combine(root, "expected.json")) == "29092407C235051E8EBCF6F4E209A26BD9782A51987A7676981E0C321851E83A", "raw FNAME and full decoded bytes");
        check("bare gzip fixed extra ZIP", Hash(Path.Combine(root, "extra-inputs.zip")) == "0F354B3A5B1E033D9E97E49B3E6E114DECE6055D4D5F96B819A6FC6E043AD7C9", "mixed container encoding and Windows ACPs");
        check("bare gzip fixed extra expectations", Hash(Path.Combine(root, "extra-expected.json")) == "47044E7C7D706AA4194D5BAA65C18A57C9CCEF39940980DA4EC2A935F008F1C5", "independent generator");
        ZipFile.ExtractToDirectory(Path.Combine(root, "bare-gzip-inputs.zip"), work);
        ZipFile.ExtractToDirectory(Path.Combine(root, "extra-inputs.zip"), work);
        using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "expected.json")));
        var proof = new List<object>();
        foreach (var item in expected.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!; var name = item.GetProperty("input").GetString()!;
            var path = Path.Combine(work, name);
            check("bare gzip original " + id, Hash(path) == item.GetProperty("inputSHA256").GetString()
                && new FileInfo(path).Length == item.GetProperty("inputBytes").GetInt64(), "ZIP CRC read and fixed SHA/size");
            foreach (var codePage in new[] { 28591, 65001, 932 })
            {
                var metadata = item.GetProperty("firstMemberMetadata");
                var decoded = metadata.GetProperty("perCodePage").GetProperty(codePage.ToString());
                var fallback = metadata.GetProperty("fallbackRequired").GetBoolean();
                var valid = item.GetProperty("expectedGzipStructurallyValid").GetBoolean() && id != "misleading-tar-gz"
                    && (fallback || decoded.GetProperty("decodeAccepted").ValueKind == JsonValueKind.True && decoded.GetProperty("safeEntryPathAccepted").GetBoolean());
                var entry = fallback ? DefaultName(name) : decoded.TryGetProperty("normalizedEntryPath", out var normalized) ? normalized.GetString() : null;
                var tag = id + "-" + codePage;
                var options = new[] { "--gzip-name-code-page", codePage.ToString() };
                var result = await run("bare-gzip-list-" + tag, valid ? 0 : 2, valid, ["--archive-list", path, .. options]);
                if (!valid)
                {
                    check("bare gzip no partial metadata " + tag, result.Stdout.Length == 0 && result.Stderr.Length > 0, "structural failure precedes adoption");
                    if (codePage != 28591) continue;
                    var protectedOutput = Path.Combine(work, "protected-" + id + ".zip"); File.WriteAllText(protectedOutput, "protected existing output"); var before = Hash(protectedOutput);
                    var rejected = await run("bare-gzip-protected-" + id, 2, false, ["--archive-repack", path, protectedOutput]);
                    check("bare gzip reject preserves existing output " + id, Hash(protectedOutput) == before && rejected.Stdout.Length == 0, "no publication before complete validation");
                    continue;
                }
                if (result.ExitCode != 0) continue;
                using var listing = JsonDocument.Parse(result.Stdout);
                var actual = listing.RootElement.GetProperty("entries");
                check("bare gzip full manifest " + tag, listing.RootElement.GetProperty("format").GetString() == "gzip" && actual.GetArrayLength() == 1
                    && actual[0].GetProperty("path").GetString() == entry && !actual[0].GetProperty("directory").GetBoolean()
                    && actual[0].GetProperty("size").GetInt64() == item.GetProperty("expectedPayloadBytes").GetInt64()
                    && actual[0].GetProperty("sha256").GetString() == item.GetProperty("expectedPayloadSHA256").GetString(), "first-member name; all concatenated payload bytes");
                var export = Path.Combine(work, "export-" + tag + ".bin");
                await run("bare-gzip-entry-" + tag, 0, true, ["--archive-entry", path, entry!, export, .. options]);
                var bytes = Convert.FromHexString(item.GetProperty("expectedPayloadFullHex").GetString()!);
                check("bare gzip entry bytes " + tag, File.Exists(export) && File.ReadAllBytes(export).SequenceEqual(bytes), "independent Python gzip/zlib full payload");
                proof.Add(new { id, codePage, entry, sha256 = item.GetProperty("expectedPayloadSHA256").GetString() });
            }
        }
        using var extra = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "extra-expected.json")));
        foreach (var row in extra.RootElement.GetProperty("cases").EnumerateArray())
            check("bare gzip extra original " + row.GetProperty("input").GetString(), Hash(Path.Combine(work, row.GetProperty("input").GetString()!)) == row.GetProperty("sha256").GetString(), "fixed raw bytes");
        var binary = Convert.FromHexString(extra.RootElement.GetProperty("binaryHex").GetString()!);
        await RunAmbiguousGZipAsync(output, work, binary, run, check);
        foreach (var (name, entry, accepted) in new[] { ("fallback.gz", "fallback", true), ("fallback.bin", "fallback", true), ("fallback", "noname", true), (".gz", "", false) })
        {
            var path = Path.Combine(work, name); File.Copy(Path.Combine(work, "absent-first-later-name.gz"), path);
            var listed = await run("bare-gzip-fallback-" + name, accepted ? 0 : 2, accepted, ["--archive-list", path]);
            if (listed.ExitCode == 0)
            {
                using var listing = JsonDocument.Parse(listed.Stdout);
                check("bare gzip WinMerge fallback " + name, listing.RootElement.GetProperty("entries")[0].GetProperty("path").GetString() == entry, "local Merge7z caller override");
            }
        }
        foreach (var (name, codePage, entry) in new[] { ("windows-1252.gz", 1252, "Euro-€.bin"), ("windows-1251.gz", 1251, "имя.bin"), ("dbcs-backslash-trail.gz", 932, "表.bin") })
        {
            var exported = Path.Combine(work, codePage + ".bin");
            await run("bare-gzip-legacy-" + codePage, 0, true, ["--archive-entry", Path.Combine(work, name), entry, exported, "--gzip-name-code-page", codePage.ToString()]);
            check("bare gzip explicit legacy bytes " + codePage, File.Exists(exported) && File.ReadAllBytes(exported).SequenceEqual(binary), "portable numeric codepage");
        }
        var badTail = await run("bare-gzip-name-error-bad-tail", 2, false, ["--archive-list", Path.Combine(work, "encoded-bad-tail.gz")]);
        check("bare gzip corrupt tail offers no name retry", badTail.Stdout.Length == 0 && !badTail.Stderr.Contains("文字コードを選択", StringComparison.Ordinal), "CRC fails before tentative name failure");
        foreach (var (name, codePage) in new[] { ("traversal-c1.gz", 28591), ("traversal-invalid-utf8.gz", 65001), ("traversal-invalid-dbcs.gz", 932) })
        {
            var rejected = await run("bare-gzip-mixed-path-" + codePage, 2, false, ["--archive-list", Path.Combine(work, name), "--gzip-name-code-page", codePage.ToString()]);
            check("bare gzip dangerous path never offers name retry " + codePage, rejected.Stdout.Length == 0 && !rejected.Stderr.Contains("文字コードを選択", StringComparison.Ordinal), "unsafe ASCII structure is independent of name decoding");
        }
        foreach (var codePage in new[] { 1200, 1201, 12000, 65000, 50220, -1, 0 })
            await run("bare-gzip-invalid-codepage-" + codePage, 2, false, ["--archive-list", Path.Combine(work, "utf8-inner.gz"), "--gzip-name-code-page", codePage.ToString()]);
        var nested = Path.Combine(work, "nested-mixed.gz");
        await run("bare-gzip-compare-portable-options", 0, true, ["--archive-compare", Path.Combine(work, "utf8-inner.gz"), Path.Combine(work, "utf8-inner.gz"), "--gzip-name-code-page", "65001", "--right-gzip-name-code-page", "65001"]);
        JsonObject Descriptor(int[] choices) => new() { ["rootPath"] = nested, ["entryChain"] = new JsonArray("階層.gz"), ["containerNameCodePages"] = JsonSerializer.SerializeToNode(choices) };
        string Save(string name, JsonObject document) { var path = Path.Combine(work, name + ".json"); File.WriteAllText(path, document.ToJsonString()); return path; }
        var descriptor = Save("source", Descriptor([932, 65001]));
        await run("bare-gzip-mixed-source-list", 0, true, ["--archive-source-list", descriptor]);
        var textOutput = Path.Combine(work, "nested.txt");
        await run("bare-gzip-mixed-source-entry", 0, true, ["--archive-source-entry", descriptor, "café.txt", textOutput]);
        var text = Convert.FromHexString(extra.RootElement.GetProperty("textHex").GetString()!);
        check("bare gzip mixed source full bytes", File.Exists(textOutput) && File.ReadAllBytes(textOutput).SequenceEqual(text), "root932, innerUTF8, immutable WithChild identity");
        foreach (var choices in new[] { new[] { 932 }, new[] { 28591, 65001 }, new[] { 932, 1200 }, new[] { 932, 65001, 932 } })
            await run("bare-gzip-source-invalid-" + string.Join('-', choices), 2, false, ["--archive-source-list", Save("invalid-" + string.Join('-', choices), Descriptor(choices))]);
        var lowered = Descriptor([932, 65001]); lowered["limits"] = new JsonObject { ["maximumDecodedBytes"] = 1 };
        await run("bare-gzip-shared-budget", 2, false, ["--archive-source-list", Save("lowered", lowered)]);
        foreach (var isBinary in new[] { false, true })
        {
            var rootPath = isBinary ? Path.Combine(work, "nested-binary.gz") : nested;
            var leaf = isBinary ? "café.bin" : "café.txt";
            var payload = isBinary ? binary : text;
            var asset = Path.Combine(work, isBinary ? "working.bin" : "working.text"); File.WriteAllBytes(asset, payload);
            var input = new JsonObject { ["rootPath"] = rootPath, ["entryChain"] = new JsonArray("階層.gz"), ["leafEntry"] = leaf, ["rootSha256"] = Hash(rootPath),
                ["containerNameCodePages"] = new JsonArray(932, 65001), ["inheritedReadOnly"] = false,
                ["workingTexts"] = new JsonArray(new JsonObject { ["entryChain"] = new JsonArray("階層.gz"), ["leafEntry"] = leaf, ["snapshotPath"] = Path.GetFileName(asset),
                    ["containerNameCodePages"] = new JsonArray(932, 65001), ["sha256"] = Hash(asset), ["encodingName"] = isBinary ? "" : "utf-8", ["hasBom"] = !isBinary }) };
            if (isBinary) input["workingTexts"]![0]!["kind"] = "Binary";
            var project = new JsonObject { ["formatVersion"] = 7, ["activeEntryIndex"] = 0, ["entries"] = new JsonArray(new JsonObject
            { ["mode"] = isBinary ? "Binary" : "Text", ["leftArchiveInput"] = input.DeepClone(), ["rightArchiveInput"] = input.DeepClone(), ["leftReadOnly"] = true, ["rightReadOnly"] = true }) };
            var suffix = isBinary ? "binary" : "text"; var source = Save("workspace-" + suffix, project); var copy = Path.Combine(work, "copied-" + suffix + ".json");
            await run("bare-gzip-workspace-copy-" + suffix, 0, true, ["--project-copy", source, copy]);
            if (File.Exists(copy))
            {
                using var copied = JsonDocument.Parse(File.ReadAllBytes(copy));
                var stored = copied.RootElement.GetProperty("entries")[0].GetProperty("leftArchiveInput");
                check("bare gzip v7 setting preserved " + suffix, copied.RootElement.GetProperty("formatVersion").GetInt32() == 7
                    && stored.GetProperty("containerNameCodePages").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual(new[] { 932, 65001 }), "body encoding remains independent");
                var snapshot = stored.GetProperty("workingTexts")[0].GetProperty("snapshotPath").GetString()!;
                check("bare gzip copied working bytes " + suffix, File.ReadAllBytes(Path.GetFullPath(snapshot, work)).SequenceEqual(payload), "readonly raw source preserved");
            }
            var package = Path.Combine(work, "package-" + suffix + ".zip");
            await run("bare-gzip-package-" + suffix, 0, true, ["--package-project", source, package]);
            var unpack = Path.Combine(work, "unpack-" + suffix);
            await run("bare-gzip-package-extract-" + suffix, 0, true, ["--archive-extract", package, unpack]);
            var restored = Path.Combine(unpack, "project.json");
            await run("bare-gzip-package-recopy-" + suffix, 0, true, ["--project-copy", restored, Path.Combine(work, "restored-" + suffix + ".json")]);
            if (!isBinary) await run("bare-gzip-report", 0, true, ["--report-project", source, Path.Combine(work, "report.html")]);
            for (var version = 1; version <= 6; version++)
            {
                var old = (JsonObject)project.DeepClone(); old["formatVersion"] = version;
                var rejected = await run("bare-gzip-old-schema-" + suffix + "-" + version, 2, false, ["--project-copy", Save("old-" + suffix + "-" + version, old), copy]);
                check("bare gzip old schema no partial output " + suffix + "-" + version, rejected.Stdout.Length == 0, "new field cannot enter legacy schema");
            }
        }
        var repacked = Path.Combine(work, "legacy-repacked.zip");
        await run("bare-gzip-repack-existing-format", 0, true, ["--archive-repack", Path.Combine(work, "windows-1252.gz"), repacked, "--gzip-name-code-page", "1252"]);
        if (File.Exists(repacked))
        {
            using var zip = ZipFile.OpenRead(repacked); using var stream = zip.Entries.Single().Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes);
            check("bare gzip repack independent ZIP bytes", zip.Entries.Single().FullName == "Euro-€.bin" && bytes.ToArray().SequenceEqual(binary), "no .gz writer claimed");
        }
        var extraction = Path.Combine(work, "extracted");
        await run("bare-gzip-extract", 0, true, ["--archive-extract", Path.Combine(work, "windows-1252.gz"), extraction, "--gzip-name-code-page", "1252"]);
        check("bare gzip extracted bytes", File.Exists(Path.Combine(extraction, "Euro-€.bin")) && File.ReadAllBytes(Path.Combine(extraction, "Euro-€.bin")).SequenceEqual(binary), "same explicit decoder across list/export/repack/extract");
        foreach (var (path, sha) in originals) check("bare gzip fixed fixture retained " + Path.GetFileName(path), Hash(path) == sha, "read-only originals");
        var ui = Path.Combine(output, "bare-gzip-ui");
        await run("bare-gzip-ui", 0, false, ["--self-test", ui, "--bare-gzip-only"]);
        var uiAssertions = Path.Combine(ui, "ui-report.json");
        check("bare gzip UI report published", File.Exists(uiAssertions), "fixture binding is read from the completed UI report");
        using var uiReport = JsonDocument.Parse(File.ReadAllBytes(uiAssertions));
        var uiRoot = Path.GetFullPath(ui);
        var uiFixtureRoot = Path.GetFullPath(uiReport.RootElement.GetProperty("fixtures").GetString()
            ?? throw new IOException("bare gzip GUI fixture binding missing."), uiRoot);
        var allowedFixtureRoot = Path.GetFullPath(Path.Combine(uiRoot, "fixtures")) + Path.DirectorySeparatorChar;
        if (!uiFixtureRoot.StartsWith(allowedFixtureRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !Directory.Exists(uiFixtureRoot)) throw new IOException("bare gzip GUI fixture binding escapes run or is missing.");
        check("bare gzip UI proof published", File.Exists(Path.Combine(uiFixtureRoot, "bare-gzip", "ui-proof.json"))
            && File.Exists(Path.Combine(ui, "bare-gzip-confirmed.png")), "headless injection is distinct from native dialogs");
        var identityRoot = Path.Combine(uiFixtureRoot, "bare-gzip", "working-decoder-identity");
        var identityProofPath = Path.Combine(identityRoot, "identity-proof.json");
        check("bare gzip working decoder proof published", File.Exists(identityProofPath), "real Store capture and workspace save/reload");
        if (File.Exists(identityProofPath))
        {
            using var identityProof = JsonDocument.Parse(File.ReadAllBytes(identityProofPath));
            var expectedVariants = identityProof.RootElement.GetProperty("variants").EnumerateArray().ToDictionary(row => row.GetProperty("codePage").GetInt32(),
                row => Convert.FromHexString(row.GetProperty("payloadHex").GetString()!));
            var identityCases = new List<object>();
            foreach (var shared in new[] { false, true })
            {
                var tag = shared ? "shared" : "distinct";
                var source = identityProof.RootElement.GetProperty(shared ? "sharedWorkspace" : "workspace").GetString()!;
                var copy = Path.Combine(work, "identity-" + tag + ".json");
                await run("bare-gzip-identity-copy-" + tag, 0, true, ["--project-copy", source, copy]);
                VerifyIdentity(copy, shared, "copy-" + tag);
                var package = Path.Combine(work, "identity-" + tag + ".zip"); var unpack = Path.Combine(work, "identity-unpack-" + tag);
                await run("bare-gzip-identity-package-" + tag, 0, true, ["--package-project", copy, package]);
                await run("bare-gzip-identity-extract-" + tag, 0, true, ["--archive-extract", package, unpack]);
                var packedProject = Path.Combine(unpack, "project.json"); var restored = Path.Combine(work, "identity-restored-" + tag + ".json");
                VerifyIdentity(packedProject, shared, "package-" + tag);
                await run("bare-gzip-identity-reload-" + tag, 0, true, ["--project-copy", packedProject, restored]);
                VerifyIdentity(restored, shared, "restored-" + tag);
                identityCases.Add(new { tag, source, copy, package, unpack, restored });
            }
            var protectedCopy = Path.Combine(work, "identity-distinct.json"); var protectedSha = Hash(protectedCopy);
            foreach (var defaultAlias in new[] { false, true })
            {
                var invalid = JsonNode.Parse(File.ReadAllBytes(Path.Combine(identityRoot, "workspace.json")))!.AsObject();
                foreach (var side in new[] { "leftArchiveInput", "rightArchiveInput" })
                {
                    var input = invalid["entries"]![0]![side]!.AsObject(); var first = input["workingTexts"]![0]!.DeepClone(); var duplicate = first.DeepClone();
                    if (defaultAlias) { first["containerNameCodePages"] = null; duplicate["containerNameCodePages"] = new JsonArray(28591, 28591); }
                    input["workingTexts"] = new JsonArray(first, duplicate);
                }
                var path = Save("identity-duplicate-" + defaultAlias, invalid);
                var rejected = await run("bare-gzip-identity-duplicate-" + defaultAlias, 2, false, ["--project-copy", path, protectedCopy]);
                check("bare gzip same decoder duplicate preserves output " + defaultAlias, rejected.Stdout.Length == 0 && Hash(protectedCopy) == protectedSha,
                    defaultAlias ? "null/default28591 are the same identity" : "same concrete codepage is a duplicate");
            }
            File.WriteAllBytes(Path.Combine(output, "bare-gzip-working-identity-proof.json"), JsonSerializer.SerializeToUtf8Bytes(new
            { headlessProof = identityProofPath, cases = identityCases }, new JsonSerializerOptions { WriteIndented = true }));

            void VerifyIdentity(string workspace, bool shared, string stage)
            {
                check("bare gzip identity workspace exists " + stage, File.Exists(workspace), "copy/package/reload route");
                if (!File.Exists(workspace)) return;
                using var parsed = JsonDocument.Parse(File.ReadAllBytes(workspace));
                check("bare gzip identity workspace v7 " + stage, parsed.RootElement.GetProperty("formatVersion").GetInt32() == 7, "codepage-bearing working DTO");
                foreach (var side in new[] { "leftArchiveInput", "rightArchiveInput" })
                {
                    var input = parsed.RootElement.GetProperty("entries")[0].GetProperty(side); var saved = input.GetProperty("workingTexts").EnumerateArray().ToArray();
                    check("bare gzip identity both saved variants " + stage + side, saved.Length == 2, "same root/chain/leaf; two decoder identities");
                    var codePages = saved.Select(row => row.GetProperty("containerNameCodePages")[1].GetInt32()).Order().ToArray();
                    check("bare gzip identity decoder set " + stage + side, codePages.SequenceEqual(new[] { 932, 65001 }), "numeric settings survive every route");
                    foreach (var row in saved)
                    {
                        var choices = row.GetProperty("containerNameCodePages"); var codePage = choices[1].GetInt32();
                        var asset = Path.GetFullPath(row.GetProperty("snapshotPath").GetString()!, Path.GetDirectoryName(workspace)!);
                        var expectedBytes = expectedVariants[shared ? 932 : codePage];
                        check("bare gzip identity full saved bytes " + stage + side + codePage, choices.GetArrayLength() == 2 && choices[0].GetInt32() == 28591
                            && row.GetProperty("entryChain")[0].GetString() == "a.txt.gz" && row.GetProperty("leafEntry").GetString() == "a.txt"
                            && File.Exists(asset) && File.ReadAllBytes(asset).SequenceEqual(expectedBytes), "same ASCII path; independent working bytes");
                    }
                    if (shared) check("bare gzip identity shared asset " + stage + side, saved.Select(row => row.GetProperty("snapshotPath").GetString()).Distinct(StringComparer.Ordinal).Count() == 1,
                        "same content hash/kind remains safely shared across decoder choices");
                }
            }
        }
        await RunPayloadKindsAsync(output, work, run, check);
        await RunWritingAsync(output, work, python, run, check);
        File.WriteAllBytes(Path.Combine(output, "bare-gzip-independent-proof.json"), JsonSerializer.SerializeToUtf8Bytes(proof, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task RunAmbiguousGZipAsync(string output, string work, byte[] binary,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var sourceRoot = Path.GetFullPath("tests/Fixtures/Archives/Sources");
        var sourceManifest = Path.Combine(sourceRoot, "manifest.json");
        check("ambiguous gzip fixed Source golden", Hash(sourceManifest) == "07AB91DD960B1369E61182261C958AF611B03E0E44CD3387A9E50ADB324DF2FE", "existing independently decoded TAR entries");
        using var golden = JsonDocument.Parse(File.ReadAllBytes(sourceManifest));
        var expected = golden.RootElement.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("name").GetString() == "unknown-gzip");
        var original = Path.Combine(sourceRoot, expected.GetProperty("file").GetString()!);
        var originalHash = Hash(original);
        check("ambiguous gzip fixed Source input", originalHash == "0CB0AD76C0D0758470243D4C4C23CB10B4D6C1C1883DE5DE15E2FFFB048BE0BB", "unchanged gzip member");
        var gzip = File.ReadAllBytes(original);
        var observations = new List<object>();
        foreach (var suffix in new[] { ".gz", ".bin", "" })
        {
            var path = Path.Combine(work, "unknown-tar" + suffix); File.WriteAllBytes(path, gzip);
            var tag = suffix.Length == 0 ? "no-extension" : suffix[1..];
            await VerifyTar("classification-" + tag, path);
        }
        var rawTarPath = Path.Combine(sourceRoot, "small.tar");
        check("ambiguous gzip fixed raw TAR", Hash(rawTarPath) == "45C8A28CEDCA660D1965A0EB419B3DCA52999F12FF6FE5EC29FDC01565B58409", "unchanged independently generated USTAR");
        var rawTar = File.ReadAllBytes(rawTarPath);
        var splitTar = Path.Combine(work, "member-split-tar.bin");
        File.WriteAllBytes(splitTar, [.. Compress(rawTar[..173]), .. Compress(rawTar[173..])]);
        await VerifyTar("member-split-first-header", splitTar);
        // 独立goldenの全entryを書出し、payload全bytesのSHAとsizeへ照合する。
        await VerifyTar("fixed-source", original);
        foreach (var entry in expected.GetProperty("entries").EnumerateArray())
        {
            var name = entry.GetProperty("path").GetString()!;
            var exported = Path.Combine(work, "unknown-export-" + name);
            await run("ambiguous-gzip-fixed-entry-" + name, 0, true, ["--archive-entry", original, name, exported]);
            check("ambiguous gzip all fixed entry bytes " + name, File.Exists(exported)
                && new FileInfo(exported).Length == entry.GetProperty("size").GetInt64() && Hash(exported) == entry.GetProperty("sha256").GetString(), "full exported bytes against existing Source golden");
        }
        var decodedBytes = expected.GetProperty("decodedBytes").GetInt64();
        var exact = new JsonObject { ["maximumDecodedBytes"] = decodedBytes, ["maximumEntryBytes"] = 35L,
            ["maximumEntries"] = 3L, ["maximumPathCharacters"] = 34L };
        var exactPath = Descriptor("exact", original, exact);
        var exactResult = await run("ambiguous-gzip-source-exact", 0, true, ["--archive-source-list", exactPath]);
        if (exactResult.ExitCode == 0) VerifyManifest("exact-shared-budget", exactResult.Stdout);
        var splitExact = (JsonObject)exact.DeepClone(); splitExact["maximumEntries"] = 4L;
        var splitResult = await run("ambiguous-gzip-split-source-exact", 0, true,
            ["--archive-source-list", Descriptor("split-exact", splitTar, splitExact)]);
        if (splitResult.ExitCode == 0) VerifyManifest("member-split-exact-budget", splitResult.Stdout);
        var protectedOutput = Path.Combine(work, "ambiguous-protected.zip"); File.WriteAllBytes(protectedOutput, binary);
        var protectedHash = Hash(protectedOutput);
        foreach (var key in new[] { "maximumDecodedBytes", "maximumEntryBytes", "maximumEntries", "maximumPathCharacters" })
        {
            var below = (JsonObject)exact.DeepClone(); below[key] = below[key]!.GetValue<long>() - 1;
            var descriptor = Descriptor("below-" + key, original, below);
            var rejected = await run("ambiguous-gzip-source-below-" + key, 2, false, ["--archive-source-entry", descriptor, "leaf.txt", protectedOutput]);
            check("ambiguous gzip exact shared refusal " + key, rejected.Stdout.Length == 0 && Hash(protectedOutput) == protectedHash
                && Hash(original) == originalHash, "one byte/item/character below preserves input and existing output");
        }
        var longTar = Path.Combine(work, "long-ignored-tar.bin");
        File.WriteAllBytes(longTar, WithName(gzip, Enumerable.Repeat((byte)'a', 20 * 1024).ToArray()));
        await VerifyTar("long-ignored-name", longTar);
        var longExact = (JsonObject)exact.DeepClone(); longExact["maximumPathCharacters"] = 20 * 1024 + 34;
        var longResult = await run("ambiguous-gzip-long-name-exact", 0, true,
            ["--archive-source-list", Descriptor("long-name-exact", longTar, longExact)]);
        if (longResult.ExitCode == 0) VerifyManifest("long-name-exact", longResult.Stdout);
        longExact["maximumPathCharacters"] = 20 * 1024 + 33;
        var longBelow = await run("ambiguous-gzip-long-name-below", 2, false,
            ["--archive-source-entry", Descriptor("long-name-below", longTar, longExact), "leaf.txt", protectedOutput]);
        check("ambiguous gzip ignored name shares header budget", longBelow.Stdout.Length == 0 && Hash(protectedOutput) == protectedHash,
            "bounded FNAME capture does not omit ignored metadata from shared budget");
        var bare = Compress(binary);
        foreach (var (name, entry) in new[] { ("unknown-file.bin", "unknown-file"), ("unknown-file", "noname") })
        {
            var path = Path.Combine(work, name); File.WriteAllBytes(path, bare);
            var listed = await run("ambiguous-gzip-bare-" + name, 0, true, ["--archive-list", path]);
            if (listed.ExitCode == 0)
            {
                using var json = JsonDocument.Parse(listed.Stdout); var rows = json.RootElement.GetProperty("entries");
                check("ambiguous gzip nonTAR classification " + name, json.RootElement.GetProperty("format").GetString() == "gzip"
                    && rows.GetArrayLength() == 1 && rows[0].GetProperty("path").GetString() == entry
                    && rows[0].GetProperty("size").GetInt64() == binary.Length
                    && rows[0].GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(binary)), "magic-only preserves full binary as one logical file");
            }
            var exported = Path.Combine(work, name + "-export.bin");
            await run("ambiguous-gzip-bare-entry-" + name, 0, true, ["--archive-entry", path, entry, exported]);
            check("ambiguous gzip nonTAR bytes " + name, File.Exists(exported) && File.ReadAllBytes(exported).SequenceEqual(binary), "full binary input survives classification");
        }
        var largeBinary = Enumerable.Repeat(binary, 5).SelectMany(bytes => bytes).ToArray();
        var largeBare = Path.Combine(work, "large-bare.bin"); File.WriteAllBytes(largeBare, Compress(largeBinary));
        var largeExport = Path.Combine(work, "large-bare-export.bin");
        await run("ambiguous-gzip-large-bare-entry", 0, true, ["--archive-entry", largeBare, "large-bare", largeExport]);
        check("ambiguous gzip prefix and remainder preserved", File.Exists(largeExport) && File.ReadAllBytes(largeExport).SequenceEqual(largeBinary),
            "nonTAR header probe feeds exactly once into the continuing bare sink");
        var misleading = Path.Combine(work, "fname-archive.bin"); File.WriteAllBytes(misleading, WithName(bare, "inner.zip"u8.ToArray()));
        var misleadingResult = await run("ambiguous-gzip-fname-does-not-recurse", 0, true, ["--archive-list", misleading]);
        if (misleadingResult.ExitCode == 0)
        {
            using var json = JsonDocument.Parse(misleadingResult.Stdout);
            check("ambiguous gzip FNAME does not add recursion", json.RootElement.GetProperty("format").GetString() == "gzip"
                && json.RootElement.GetProperty("entries")[0].GetProperty("path").GetString() == "inner.zip", "only explicit wrapper chains select ZIP reader");
        }
        var longBare = WithName(bare, Enumerable.Repeat((byte)'a', 20 * 1024).ToArray());
        foreach (var (id, bytes) in new[] { ("tar-later-crc", gzip.Concat(File.ReadAllBytes(Path.Combine(work, "bad-crc.gz"))).ToArray()),
            ("long-tar-later-crc", File.ReadAllBytes(longTar).Concat(File.ReadAllBytes(Path.Combine(work, "bad-crc.gz"))).ToArray()),
            ("bare-long-name", longBare), ("bare-long-name-crc", CorruptCrc(longBare)) })
        {
            var path = Path.Combine(work, id + ".bin"); File.WriteAllBytes(path, bytes); var hash = Hash(path);
            var rejected = await run("ambiguous-gzip-reject-" + id, 2, false, ["--archive-list", path]);
            check("ambiguous gzip no partial listing " + id, rejected.Stdout.Length == 0 && Hash(path) == hash, "full member verification precedes publication");
            if (id.Contains("crc", StringComparison.Ordinal)) check("ambiguous gzip CRC priority " + id,
                rejected.Stderr.Contains("CRC", StringComparison.Ordinal) && !rejected.Stderr.Contains("文字コードを選択", StringComparison.Ordinal), "overflow/ignored name cannot hide subsequent CRC failure");
            var exported = await run("ambiguous-gzip-protected-" + id, 2, false, ["--archive-repack", path, protectedOutput]);
            check("ambiguous gzip corrupt export retained " + id, exported.Stdout.Length == 0 && Hash(protectedOutput) == protectedHash && Hash(path) == hash,
                "existing output and source retained on malformed tail/name");
        }
        foreach (var length in new[] { 512, 1024 })
        {
            var path = Path.Combine(work, "zero-" + length + ".bin"); File.WriteAllBytes(path, Compress(new byte[length]));
            var result = await run("ambiguous-gzip-zero-" + length, length == 512 ? 2 : 0, length == 1024, ["--archive-list", path]);
            if (length == 512) check("ambiguous gzip single zero block rejected", result.Stdout.Length == 0, "TAR priority retains two-block terminator requirement");
            else if (result.ExitCode == 0)
            {
                using var json = JsonDocument.Parse(result.Stdout);
                check("ambiguous gzip empty TAR priority", json.RootElement.GetProperty("format").GetString() == "tar.gz"
                    && json.RootElement.GetProperty("entries").GetArrayLength() == 0, "identical 1024 zero bytes cannot distinguish bare binary from empty TAR");
            }
        }
        check("ambiguous gzip fixed Source original retained", Hash(original) == originalHash, "no fixture regeneration");
        File.WriteAllBytes(Path.Combine(output, "ambiguous-gzip-proof.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { fixedInput = original, fixedSha256 = originalHash, decodedBytes, cases = observations,
            zeroPayloadPolicy = "TAR priority: 512 rejected; 1024 empty TAR", cancellation = "existing UI token checks; mid-probe cancellation injection not performed here" }, new JsonSerializerOptions { WriteIndented = true }));

        async Task VerifyTar(string id, string path)
        {
            var result = await run("ambiguous-gzip-tar-" + id, 0, true, ["--archive-list", path]);
            if (result.ExitCode == 0) VerifyManifest(id, result.Stdout);
            observations.Add(new { id, input = path, sha256 = Hash(path), result.ExitCode });
        }
        void VerifyManifest(string id, string stdout)
        {
            using var json = JsonDocument.Parse(stdout); var rows = json.RootElement.GetProperty("entries");
            var wanted = expected.GetProperty("entries");
            check("ambiguous gzip TAR manifest " + id, json.RootElement.GetProperty("format").GetString() == "tar.gz"
                && rows.GetArrayLength() == wanted.GetArrayLength() && rows.EnumerateArray().Zip(wanted.EnumerateArray()).All(pair =>
                    pair.First.GetProperty("path").GetString() == pair.Second.GetProperty("path").GetString()
                    && pair.First.GetProperty("directory").GetBoolean() == pair.Second.GetProperty("directory").GetBoolean()
                    && pair.First.GetProperty("size").GetInt64() == pair.Second.GetProperty("size").GetInt64()
                    && pair.First.GetProperty("sha256").GetString() == pair.Second.GetProperty("sha256").GetString()), "all existing Source entries; not a single gzip file");
        }
        string Descriptor(string id, string path, JsonObject limits)
        {
            var descriptor = Path.Combine(work, "ambiguous-" + id + ".json");
            File.WriteAllText(descriptor, new JsonObject { ["rootPath"] = path, ["entryChain"] = new JsonArray(), ["limits"] = limits.DeepClone() }.ToJsonString());
            return descriptor;
        }
        static byte[] Compress(byte[] bytes)
        {
            using var compressed = new MemoryStream();
            using (var stream = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) stream.Write(bytes);
            return compressed.ToArray();
        }
        static byte[] WithName(byte[] bytes, byte[] name)
        {
            if (bytes.Length < 18 || bytes[3] != 0) throw new IOException("Ambiguous gzip test requires a fixed header without optional fields.");
            var header = bytes[..10]; header[3] = 8;
            return [.. header, .. name, 0, .. bytes[10..]];
        }
        static byte[] CorruptCrc(byte[] bytes) { var copy = (byte[])bytes.Clone(); copy[^8] ^= 1; return copy; }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string DefaultName(string physical)
    {
        var dot = physical.LastIndexOf('.'); return dot >= 0 ? physical[..dot] : "noname";
    }
}
