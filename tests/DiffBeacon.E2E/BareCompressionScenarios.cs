using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class BareCompressionScenarios
{
    // 失敗条件: 全BZip2 member/CRC/footer、Z構造、不正名、TAR曖昧判定、各層指定、共有予算、旧schema混入、出力保護。
    // Zはchecksum/宣言長/明示EOFがないため、全semantic破損検出やpadding zeroを契約にしない。
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check,
        string python, string? reference, string? fixtureRoot = null)
    {
        if (reference is null) throw new ArgumentException("裸圧縮検証には固定公式 --z-reference が必要です。");
        if (fixtureRoot is not null && !Path.IsPathFullyQualified(fixtureRoot)) throw new ArgumentException("裸圧縮fixture overrideは絶対ディレクトリで指定してください。");
        var root = Path.GetFullPath(fixtureRoot ?? "tests/Fixtures/Archives/BareCompression");
        RequireUnlinked(root);
        var work = Path.GetFullPath(Path.Combine(fixtures, "bare-compression"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (work.Equals(root, comparison) || work.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || root.StartsWith(work + Path.DirectorySeparatorChar, comparison)) throw new ArgumentException("fixture原本とrun出力を重ねないでください。");
        var allowedFiles = new HashSet<string>(StringComparer.Ordinal) { "inputs.zip", "expected.json", "verify.py", "generate.py", "README.md", ".gitattributes", "verify-ui.py", "UI-CONTRACT.md", "verify-writer.py" };
        if (Directory.GetDirectories(root).Length != 0 || Directory.GetFiles(root).Any(path => !allowedFiles.Contains(Path.GetFileName(path))))
            throw new InvalidDataException("固定fixture以外のfile／directoryを含むoverrideは使用できません。");
        foreach (var path in Directory.GetFiles(root)) RequireUnlinked(path);
        var originals = Directory.GetFiles(root).ToDictionary(p => p, Hash);
        RequireFixed("inputs.zip", "CA8BABE9D94D1584BBEAE908516870EF17BCD5DB8A26941193D4323CEC4A91FC");
        RequireFixed("expected.json", "C606FD0E63C6D62AFD3AC220CC023D1E2D8ECC07ECF4E741AF55EF13E4022DA6");
        // override配下の外部scriptも固定原本以外は実行しない。verify.pyがimportするgenerate.pyも同じ境界。
        RequireFixed("verify.py", "5037F45198BF18562476A239BFD1E240443D517FB0EBDA0E9AE3EB76C683FBEF");
        RequireFixed("generate.py", "D68BB5DDE8469F6781741BBD1D2CAC151403AF68FDE2CC8690E7C18528499A18");
        RequireFixed("verify-writer.py", "530AF37CB57287BF8A85ACE8F03995135B20612572F79E5FF31092CF8AB95DFC");
        RequireUnlinked(Path.GetFullPath(reference));
        var referenceSource = Path.GetFullPath("tests/Fixtures/Archives/TarZ/reference-source");
        // 固定scriptの資格のみモードはdecoder／compilerを実行せず、原典とbuild-proofを実bytesへ照合する。
        var referenceQualification = await QualifyReferenceAsync();
        Directory.CreateDirectory(work);
        File.WriteAllBytes(Path.Combine(work, "fixture-input-proof.json"), JsonSerializer.SerializeToUtf8Bytes(new { fixtureRoot = root, outputRoot = work, python, verifyScript = Path.Combine(root, "verify.py"), importedGenerator = Path.Combine(root, "generate.py"), zReference = Path.GetFullPath(reference), referenceQualification, writerVerifyScript = Path.Combine(root, "verify-writer.py"), writerVerifyScriptSha256 = Hash(Path.Combine(root, "verify-writer.py")), originalSha256 = originals }, new JsonSerializerOptions { WriteIndented = true }));
        ZipFile.ExtractToDirectory(Path.Combine(root, "inputs.zip"), work);
        using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "expected.json")));
        foreach (var row in expected.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = row.GetProperty("input").GetString()!; var path = Path.Combine(work, name);
            var valid = row.GetProperty("valid").GetBoolean(); var bytes = Convert.FromHexString(row.GetProperty("decodedHex").GetString()!);
            check("bare compression input " + name, Hash(path) == row.GetProperty("inputSHA256").GetString(), "fixed input SHA");
            var auto = row.GetProperty("autoExit").GetInt32();
            await run("bare-compression-auto-" + name, auto, auto == 0, ["--archive-list", path]);
            var listed = await run("bare-compression-file-" + name, valid ? 0 : 2, valid, ["--archive-list", path, "--compression-payload-kind", "file"]);
            var protectedOutput = Path.Combine(work, "export-" + name + ".bin"); File.WriteAllText(protectedOutput, "protected existing output"); var before = Hash(protectedOutput);
            var entry = name.Contains('.') ? name[..name.LastIndexOf('.')] : "noname";
            if (valid)
            {
                if (listed.ExitCode == 0)
                {
                    using var manifest = JsonDocument.Parse(listed.Stdout); var entries = manifest.RootElement.GetProperty("entries");
                    check("bare compression one arbitrary file " + name, entries.GetArrayLength() == 1 && entries[0].GetProperty("path").GetString() == entry
                        && entries[0].GetProperty("size").GetInt64() == bytes.Length && entries[0].GetProperty("sha256").GetString() == row.GetProperty("decodedSHA256").GetString(), "full payload even TAR/ZIP/zero data");
                }
                await run("bare-compression-export-" + name, 0, true, ["--archive-entry", path, entry, protectedOutput, "--compression-payload-kind", "file"]);
                check("bare compression exported all bytes " + name, File.ReadAllBytes(protectedOutput).SequenceEqual(bytes), "input-independent expected bytes");
            }
            else
            {
                check("bare compression no partial manifest " + name, listed.Stdout.Length == 0 && listed.Stderr.Length > 0, "failure before adoption");
                await run("bare-compression-rejected-export-" + name, 2, false, ["--archive-entry", path, entry, protectedOutput, "--compression-payload-kind", "file"]);
                check("bare compression protected output " + name, Hash(protectedOutput) == before, "no partial publication");
            }
            // TAR strict: normal TAR and two zero blocks accepted, arbitrary file/ZIP/512 zero rejected.
            var tarExit = valid && (name.StartsWith("tar.", StringComparison.Ordinal) || name.StartsWith("zero1024.", StringComparison.Ordinal)) ? 0 : 2;
            await run("bare-compression-tar-" + name, tarExit, tarExit == 0, ["--archive-list", path, "--compression-payload-kind", "tar"]);
        }
        foreach (var choice in new[] { "", "unknown", "FILE", "1" })
            await run("bare-compression-invalid-choice-" + choice, 2, false, ["--archive-list", Path.Combine(work, "short.bz2"), "--compression-payload-kind", choice]);
        await run("bare-compression-gzip-choice-conflict", 2, false, ["--archive-list", Path.Combine(work, "short.bz2"), "--gzip-payload-kind", "file", "--compression-payload-kind", "file"]);
        await run("bare-compression-right-choice-conflict", 2, false, ["--archive-compare", Path.Combine(work, "short.bz2"), Path.Combine(work, "short.bz2"), "--right-gzip-payload-kind", "file", "--right-compression-payload-kind", "file"]);
        await run("bare-compression-independent-right", 0, true, ["--archive-compare", Path.Combine(work, "short.bz2"), Path.Combine(work, "short.bz2"), "--compression-payload-kind", "file", "--right-compression-payload-kind", "file"]);
        // 非Autoの専用flagは異形式入力へ適用できない。正常本文の採用とJSON公開を期待しない。
        var crossKindBZip = Path.Combine(work, "short.bz2"); var crossKindGZip = Path.GetFullPath("tests/Fixtures/Archives/Sources/unknown.gz");
        var crossKindBZipHash = Hash(crossKindBZip); var crossKindGZipHash = Hash(crossKindGZip);
        var crossKindExisting = Path.Combine(work, "export-short.bz2.bin"); var crossKindExistingHash = Hash(crossKindExisting);
        var crossGZip = await run("bare-compression-gzip-setting-independent", 2, false, ["--archive-list", crossKindBZip, "--gzip-payload-kind", "file"]);
        check("bare compression gzip flag rejects unrelated input", crossGZip.Stdout.Length == 0 && crossGZip.Stderr.Length > 0
            && Hash(crossKindBZip) == crossKindBZipHash && Hash(crossKindExisting) == crossKindExistingHash, "expected rejection retains original input and existing exported bytes");
        var crossCompression = await run("bare-compression-preserves-gzip", 2, false, ["--archive-list", crossKindGZip, "--compression-payload-kind", "file"]);
        check("bare compression flag rejects gzip input", crossCompression.Stdout.Length == 0 && crossCompression.Stderr.Length > 0
            && Hash(crossKindGZip) == crossKindGZipHash && Hash(crossKindExisting) == crossKindExistingHash, "gzip original and existing output retained without JSON adoption");
        string Save(string id, JsonObject value) { var path = Path.Combine(work, id + ".json"); File.WriteAllText(path, value.ToJsonString()); return path; }
        JsonObject Source(string[] chain, string[] choices) => new() { ["rootPath"] = Path.Combine(work, "chain.bz2"), ["rootSha256"] = expected.RootElement.GetProperty("chainSHA256").GetString(), ["entryChain"] = JsonSerializer.SerializeToNode(chain), ["containerCompressionPayloadKinds"] = JsonSerializer.SerializeToNode(choices) };
        var route = Source(["chain", "noname"], ["File", "File", "File"]);
        await run("bare-compression-typed-root", 0, true, ["--archive-source-list", Save("typed-root", Source([], ["File"]))]);
        await run("bare-compression-three-layer-list", 0, true, ["--archive-source-list", Save("chain", route)]);
        var chainOutput = Path.Combine(work, "chain-leaf.bin");
        await run("bare-compression-three-layer-entry", 0, true, ["--archive-source-entry", Save("chain", route), "noname", chainOutput]);
        check("bare compression each layer full bytes", File.Exists(chainOutput) && File.ReadAllBytes(chainOutput).SequenceEqual("hello\n"u8.ToArray()), "root and two child choices independent");
        var mixedPath = Path.Combine(work, "mixed.bz2");
        JsonObject Mixed(string[] choices) => new() { ["rootPath"] = mixedPath, ["rootSha256"] = expected.RootElement.GetProperty("mixedSHA256").GetString(), ["entryChain"] = new JsonArray("mixed", "inner.bz2"), ["containerCompressionPayloadKinds"] = JsonSerializer.SerializeToNode(choices) };
        foreach (var middle in new[] { "Auto", "Tar", "File" })
        {
            var target = Path.Combine(work, "mixed-" + middle + ".bin"); var accept = middle != "File";
            await run("bare-compression-mixed-noninheritance-" + middle, accept ? 0 : 2, accept, ["--archive-source-entry", Save("mixed-" + middle, Mixed(["File", middle, "File"])), "inner", target]);
            if (accept) check("bare compression mixed all bytes " + middle, File.ReadAllBytes(target).SequenceEqual("hello\n"u8.ToArray()), "File root never implicitly overrides the child Auto/Tar selection");
        }
        foreach (var choices in new[] { new[] { "File" }, new[] { "File", "File", "File", "File" }, new[] { "File", "unknown", "File" }, new[] { "File", "Tar", "File" } })
            await run("bare-compression-invalid-route-" + string.Join('-', choices), 2, false, ["--archive-source-list", Save("invalid-route-" + string.Join('-', choices), Source(["chain", "noname"], choices))]);
        foreach (var limit in new[] { "maximumEntryBytes", "maximumDecodedBytes", "maximumWorkBytes", "maximumWrapperDepth" })
        {
            var lowered = route.DeepClone().AsObject(); lowered["limits"] = new JsonObject { [limit] = 1 };
            await run("bare-compression-limit-" + limit, 2, false, ["--archive-source-list", Save("limit-" + limit, lowered)]);
        }
        var duplicate = Path.Combine(work, "duplicate.json"); File.WriteAllText(duplicate, route.ToJsonString().TrimEnd('}') + ",\"containerCompressionPayloadKinds\":[\"File\",\"File\",\"File\"]}");
        await run("bare-compression-duplicate-property", 2, false, ["--archive-source-list", duplicate]);
        var unknown = route.DeepClone().AsObject(); unknown["unknown"] = true;
        await run("bare-compression-unknown-property", 2, false, ["--archive-source-list", Save("unknown", unknown)]);
        foreach (var variant in new[] { "null", "allAuto", "omitted" })
        {
            var source = Source(["chain", "noname"], ["Auto", "Auto", "Auto"]);
            if (variant == "omitted") source.Remove("containerCompressionPayloadKinds"); else if (variant == "null") source["containerCompressionPayloadKinds"] = null;
            await run("bare-compression-descriptor-default-" + variant, 0, true, ["--archive-source-list", Save("descriptor-default-" + variant, source)]);
        }
        // CLI全entry検証。preview APIは公開CLIに存在せず、親のheadless担当へ引き継ぐ。
        var corrupt = new JsonObject { ["rootPath"] = Path.Combine(work, "bad-tail.bz2"), ["entryChain"] = new JsonArray(), ["containerCompressionPayloadKinds"] = new JsonArray("File"), ["limits"] = new JsonObject { ["maximumPreviewBytes"] = 1 } };
        var corruptOut = Path.Combine(work, "corrupt-source.bin"); File.WriteAllText(corruptOut, "keep corrupt output"); var corruptHash = Hash(corruptOut);
        var corruptResult = await run("bare-compression-later-member-source-entry", 2, false, ["--archive-source-entry", Save("corrupt-source", corrupt), "bad-tail", corruptOut]);
        check("bare compression later corruption protected output", corruptResult.Stdout.Length == 0 && Hash(corruptOut) == corruptHash, "whole-entry CRC failure retains output; this does not measure preview");
        var input = route.DeepClone().AsObject(); input["leafEntry"] = "noname";
        JsonObject Project(int version) => new() { ["formatVersion"] = version, ["activeEntryIndex"] = 0, ["entries"] = new JsonArray(new JsonObject { ["mode"] = "Text", ["leftArchiveInput"] = input.DeepClone(), ["rightArchiveInput"] = input.DeepClone(), ["leftReadOnly"] = true, ["rightReadOnly"] = true }) };
        var protectedProject = Path.Combine(work, "protected-project.json"); File.WriteAllText(protectedProject, "keep project"); var protectedHash = Hash(protectedProject);
        for (var version = 1; version <= 8; version++)
        {
            var old = Project(version); old["entries"]![0]!["leftArchiveInput"]!["containerCompressionPayloadKinds"] = null;
            await run("bare-compression-old-schema-null-" + version, 2, false, ["--project-copy", Save("old-" + version, old), protectedProject]);
            check("bare compression old schema output preserved " + version, Hash(protectedProject) == protectedHash, "presence of new field including null rejected");
        }
        var sourceProject = Save("project-v9", Project(9)); var cloned = Path.Combine(work, "cloned-v9.json");
        await run("bare-compression-v9-clone", 0, true, ["--project-copy", sourceProject, cloned]);
        if (File.Exists(cloned))
        {
            using var clone = JsonDocument.Parse(File.ReadAllBytes(cloned));
            check("bare compression v9 choices retained", clone.RootElement.GetProperty("formatVersion").GetInt32() == 9 && clone.RootElement.GetProperty("entries")[0].GetProperty("leftArchiveInput").GetProperty("containerCompressionPayloadKinds").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "File", "File", "File" }), "explicit v9 clone identity");
        }
        await run("bare-compression-v9-report", 0, true, ["--report-project", sourceProject, Path.Combine(work, "v9.html")]);
        await run("bare-compression-v9-package", 0, true, ["--package-project", sourceProject, Path.Combine(work, "v9.zip")]);
        await run("bare-compression-v9-unpack", 0, true, ["--archive-extract", Path.Combine(work, "v9.zip"), Path.Combine(work, "unpacked")]);
        await run("bare-compression-v9-reopened-report", 0, true, ["--report-project", Path.Combine(work, "unpacked", "project.json"), Path.Combine(work, "reopened-v9.html")]);
        foreach (var variant in new (string Name, JsonNode? Value)[] { ("empty", new JsonArray()), ("short", new JsonArray("File")), ("long", new JsonArray("File", "File", "File", "File")), ("lowercase", new JsonArray("file", "File", "File")), ("unknown", new JsonArray("Unknown", "File", "File")), ("number", new JsonArray(1, "File", "File")), ("null-element", new JsonArray((JsonNode?)null, "File", "File")), ("object", new JsonObject()) })
        {
            var invalid = Project(9); invalid["entries"]![0]!["leftArchiveInput"]!["containerCompressionPayloadKinds"] = variant.Value?.DeepClone();
            await run("bare-compression-v9-strict-" + variant.Name, 2, false, ["--project-copy", Save("v9-strict-" + variant.Name, invalid), protectedProject]);
            check("bare compression v9 strict output retained " + variant.Name, Hash(protectedProject) == protectedHash, "field kind/enum/count validated before publishing");
        }
        foreach (var variant in new[] { "null", "allAuto", "omitted" })
        {
            var accepted = Project(9);
            foreach (var side in new[] { "leftArchiveInput", "rightArchiveInput" })
            {
                var archive = accepted["entries"]![0]![side]!.AsObject();
                if (variant == "omitted") archive.Remove("containerCompressionPayloadKinds");
                else archive["containerCompressionPayloadKinds"] = variant == "null" ? null : new JsonArray("Auto", "Auto", "Auto");
            }
            var target = Path.Combine(work, "v9-default-" + variant + "-copy.json");
            await run("bare-compression-v9-default-" + variant, 0, true, ["--project-copy", Save("v9-default-" + variant, accepted), target]);
            if (File.Exists(target)) { using var clone = JsonDocument.Parse(File.ReadAllBytes(target)); check("bare compression explicit v9 default retained " + variant, clone.RootElement.GetProperty("formatVersion").GetInt32() == 9, "explicit v9 remains v9, default choices canonicalize"); }
        }
        var unknownProject = Project(9); unknownProject["entries"]![0]!["leftArchiveInput"]!["unknownCompressionField"] = true;
        await run("bare-compression-v9-unknown-property", 2, false, ["--project-copy", Save("v9-unknown", unknownProject), protectedProject]);
        var duplicateProject = Path.Combine(work, "v9-duplicate.json"); var duplicated = Project(9).ToJsonString().Replace("\"containerCompressionPayloadKinds\":[\"File\",\"File\",\"File\"]", "\"containerCompressionPayloadKinds\":[\"File\",\"File\",\"File\"],\"containerCompressionPayloadKinds\":null", StringComparison.Ordinal); File.WriteAllText(duplicateProject, duplicated);
        await run("bare-compression-v9-duplicate-property", 2, false, ["--project-copy", duplicateProject, protectedProject]);
        await WorkingIdentityAsync();
        await AnonymousGZipPackageAsync();
        // 全Autoは新fieldを保存せず、v8の既存identity/schemaへ正規化する。
        var allAuto = Project(8);
        foreach (var side in new[] { "leftArchiveInput", "rightArchiveInput" }) allAuto["entries"]![0]![side]!.AsObject().Remove("containerCompressionPayloadKinds");
        var oldClone = Path.Combine(work, "cloned-v8.json");
        await run("bare-compression-v8-compatibility", 0, true, ["--project-copy", Save("project-v8", allAuto), oldClone]);
        if (File.Exists(oldClone))
        {
            using var clone = JsonDocument.Parse(File.ReadAllBytes(oldClone));
            check("bare compression all Auto legacy schema", clone.RootElement.GetProperty("formatVersion").GetInt32() <= 8 && !clone.RootElement.GetProperty("entries")[0].GetProperty("leftArchiveInput").TryGetProperty("containerCompressionPayloadKinds", out _), "legacy canonical identity retained");
        }
        var sourcePath = Path.Combine(work, "short.bz2"); var sourceHash = Hash(sourcePath);
        await run("bare-compression-input-output-overlap", 2, false, ["--archive-entry", sourcePath, "short", sourcePath, "--compression-payload-kind", "file"]);
        check("bare compression raw input protected", Hash(sourcePath) == sourceHash, "all bytes retained");
        var readOnlyOutput = Path.Combine(work, "readonly.bin"); File.WriteAllText(readOnlyOutput, "readonly output"); var attributes = File.GetAttributes(readOnlyOutput); var readOnlyHash = Hash(readOnlyOutput);
        try
        {
            File.SetAttributes(readOnlyOutput, attributes | FileAttributes.ReadOnly);
            await run("bare-compression-readonly-output", 2, false, ["--archive-entry", sourcePath, "short", readOnlyOutput, "--compression-payload-kind", "file"]);
            check("bare compression readonly bytes retained", Hash(readOnlyOutput) == readOnlyHash, "existing output protection");
        }
        finally { File.SetAttributes(readOnlyOutput, attributes); }
        // 独立run内にのみlinkを作り、削除は親のごみ箱helperへ委ねる。
        var link = Path.Combine(work, "output-link.bin"); var inputLink = Path.Combine(work, "input-link.bz2");
        try { File.CreateSymbolicLink(link, readOnlyOutput); File.CreateSymbolicLink(inputLink, sourcePath); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { check("bare compression links unavailable unmeasured", true, ex.GetType().Name + ": no protection success inferred"); }
        if (File.Exists(link) && File.Exists(inputLink))
        {
            await run("bare-compression-link-output", 2, false, ["--archive-entry", sourcePath, "short", link, "--compression-payload-kind", "file"]);
            await run("bare-compression-link-input", 2, false, ["--archive-list", inputLink, "--compression-payload-kind", "file"]);
            check("bare compression link target bytes retained", Hash(sourcePath) == sourceHash && Hash(readOnlyOutput) == readOnlyHash && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "actual links retained only in owned run for approved cleanup");
        }
        var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(root, "verify.py"), "--root", root, "--z-reference", reference, "--reference-source", referenceSource, "--run", work }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        var text = await stdout; var errors = await stderr; File.WriteAllText(Path.Combine(work, "independent.stdout.json"), text); File.WriteAllText(Path.Combine(work, "independent.stderr.txt"), errors);
        check("bare compression independent full bytes", process.ExitCode == 0, text + errors);
        foreach (var row in expected.RootElement.GetProperty("cases").EnumerateArray()) check("bare compression extracted input retained " + row.GetProperty("input").GetString(), Hash(Path.Combine(work, row.GetProperty("input").GetString()!)) == row.GetProperty("inputSHA256").GetString(), "product commands never modify inputs");
        foreach (var original in originals) check("bare compression fixture retained " + Path.GetFileName(original.Key), Hash(original.Key) == original.Value, "original bytes immutable");

        await BareCompressionWritingScenarios.RunAsync(output, work, expected.RootElement.GetProperty("cases"),
            run, check, python, reference, referenceQualification, RunIndependentWriterReaderAsync);

        async Task RunIndependentWriterReaderAsync(string manifestPath, string proofPath)
        {
            var writingRoot = Path.GetDirectoryName(manifestPath)!;
            var runRoot = Path.GetFullPath(Path.GetDirectoryName(writingRoot)!);
            var reader = Path.Combine(root, "verify-writer.py");
            var generator = Path.Combine(root, "generate.py");
            var processPath = Path.Combine(runRoot, "independent-reader-process.json");
            var stdoutPath = Path.Combine(runRoot, "independent-reader.stdout.raw");
            var stderrPath = Path.Combine(runRoot, "independent-reader.stderr.raw");
            if (File.Exists(processPath) || File.Exists(stdoutPath) || File.Exists(stderrPath))
                throw new IOException("独立readerの実行証拠が既に存在します。");
            var arguments = new[] { "-B", "-X", "utf8", reader,
                "--manifest", Path.GetFullPath(manifestPath), "--proof", Path.GetFullPath(proofPath),
                "--run-root", runRoot, "--z-reference", Path.GetFullPath(reference!),
                "--reference-source", referenceSource, "--generator", generator };
            var start = new ProcessStartInfo(python)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            var launchUtc = DateTime.UtcNow;
            using var process = new Process { StartInfo = start };
            var pid = 0;
            string? birthUtc = null;
            var exitCode = -1;
            var timedOut = false;
            var hasExited = false;
            var stdoutEof = false;
            var stderrEof = false;
            string? failure = null;
            try
            {
                if (!process.Start()) throw new IOException("独立readerを起動できません。");
                pid = process.Id;
                birthUtc = process.StartTime.ToUniversalTime().ToString("O");
                using var stdout = new FileStream(stdoutPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var stderr = new FileStream(stderrPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                var stdoutCopy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
                var stderrCopy = process.StandardError.BaseStream.CopyToAsync(stderr);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try { await process.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                await Task.WhenAll(stdoutCopy, stderrCopy).WaitAsync(TimeSpan.FromSeconds(10));
                stdoutEof = stdoutCopy.IsCompletedSuccessfully;
                stderrEof = stderrCopy.IsCompletedSuccessfully;
                await stdout.FlushAsync();
                await stderr.FlushAsync();
                process.Refresh();
                hasExited = process.HasExited;
                exitCode = timedOut ? -2 : process.ExitCode;
            }
            catch (Exception exception)
            {
                failure = exception.GetType().Name + ": " + exception.Message;
                if (pid != 0)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); hasExited = process.HasExited; } catch { }
                }
            }
            var stdoutBytes = File.Exists(stdoutPath) ? new FileInfo(stdoutPath).Length : (long?)null;
            var stderrBytes = File.Exists(stderrPath) ? new FileInfo(stderrPath).Length : (long?)null;
            string? HashPath(string path)
            {
                if (!File.Exists(path)) return null;
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            var receipt = new
            {
                schemaVersion = 1, pid, birthUtc, launchUtc = launchUtc.ToString("O"),
                exitObservedUtc = hasExited ? DateTime.UtcNow.ToString("O") : null,
                hasExited, exitCode, timedOut, stdoutEof, stderrEof, stdoutBytes, stderrBytes,
                stdoutSha256 = HashPath(stdoutPath), stderrSha256 = HashPath(stderrPath),
                processPath = process.StartInfo.FileName, arguments, failure
            };
            File.WriteAllText(processPath, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            check("bare writer independent reader process", pid > 0 && birthUtc is not null && hasExited
                && exitCode == 0 && !timedOut && stdoutEof && stderrEof && failure is null,
                $"pid={pid}, exit={exitCode}, stdoutEOF={stdoutEof}, stderrEOF={stderrEof}, timeout={timedOut}; {failure}");
        }
        async Task<JsonObject> QualifyReferenceAsync()
        {
            var qualificationStart = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(root, "generate.py"), "--z-reference", Path.GetFullPath(reference), "--reference-source", referenceSource, "--validate-reference-only" }) qualificationStart.ArgumentList.Add(argument);
            using var qualificationProcess = Process.Start(qualificationStart) ?? throw new IOException("Z reference資格確認を起動できません。");
            var processId = qualificationProcess.Id; var startedUtc = qualificationProcess.StartTime.ToUniversalTime();
            var qualificationStdout = qualificationProcess.StandardOutput.ReadToEndAsync(); var qualificationStderr = qualificationProcess.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await qualificationProcess.WaitForExitAsync(deadline.Token);
                await Task.WhenAll(qualificationStdout, qualificationStderr).WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                if (!qualificationProcess.HasExited) qualificationProcess.Kill(entireProcessTree: true);
                await qualificationProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                throw new IOException($"Z reference資格確認が60秒上限を超えました: PID={processId}, startedUtc={startedUtc:O}");
            }
            var qualificationText = await qualificationStdout; var qualificationErrors = await qualificationStderr;
            if (qualificationProcess.ExitCode != 0) throw new InvalidDataException($"Z reference資格確認に失敗しました: PID={processId}, startedUtc={startedUtc:O}, exit={qualificationProcess.ExitCode}, stdout/stderr EOF=true\n{qualificationText}{qualificationErrors}");
            return new JsonObject { ["processId"] = processId, ["startedUtc"] = startedUtc.ToString("O"), ["exitCode"] = qualificationProcess.ExitCode, ["stdoutEof"] = true, ["stderrEof"] = true, ["stdout"] = qualificationText, ["stderr"] = qualificationErrors, ["reference"] = JsonNode.Parse(qualificationText) };
        }

        void RequireFixed(string name, string sha)
        {
            var actual = Hash(Path.Combine(root, name));
            check("bare compression fixed " + name, actual == sha, "mandatory complete SHA before product command or external script");
            if (actual != sha) throw new InvalidDataException("裸圧縮fixtureの固定SHAが一致しません: " + name);
        }
        static void RequireUnlinked(string path)
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("fixture原本／script／referenceのリンクは使用できません: " + path);
                current = Path.GetDirectoryName(current);
            }
        }

        async Task AnonymousGZipPackageAsync()
        {
            // 失敗条件: 包装の物理snapshot名でFNAMEなしrootのfallback leafが変わると、元leafを再読込みできない。
            var payload = "hello\n"u8.ToArray(); var anonymousRoot = Path.Combine(work, "anonymous-root.gz");
            using (var file = File.Create(anonymousRoot))
            using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize)) gzip.Write(payload);
            var originalBytes = File.ReadAllBytes(anonymousRoot); var originalHash = Hash(anonymousRoot); var originalSize = originalBytes.Length;
            check("anonymous gzip fixture no FNAME", originalBytes.Length >= 18 && (originalBytes[3] & 8) == 0, "run-local .NET gzip with known independent hello payload");
            var archiveInput = new JsonObject { ["rootPath"] = anonymousRoot, ["rootSha256"] = originalHash, ["entryChain"] = new JsonArray(),
                ["leafEntry"] = "anonymous-root", ["containerGZipPayloadKinds"] = new JsonArray("File"), ["inheritedReadOnly"] = true };
            var project = new JsonObject { ["formatVersion"] = 8, ["activeEntryIndex"] = 0, ["entries"] = new JsonArray(new JsonObject
            { ["mode"] = "Text", ["leftArchiveInput"] = archiveInput.DeepClone(), ["rightArchiveInput"] = archiveInput.DeepClone(), ["leftReadOnly"] = true, ["rightReadOnly"] = true }) };
            var package = Path.Combine(work, "anonymous-gzip.zip"); var unpack = Path.Combine(work, "anonymous-gzip-unpack");
            await run("anonymous-gzip-fallback-package", 0, true, ["--package-project", Save("anonymous-gzip-v8", project), package]);
            if (File.Exists(package))
            {
                using var archive = ZipFile.OpenRead(package); using var projectStream = archive.GetEntry("project.json")!.Open(); using var packed = JsonDocument.Parse(projectStream);
                VerifyDto(packed.RootElement, "package");
                foreach (var side in new[] { "leftArchiveInput", "rightArchiveInput" })
                {
                    var stored = packed.RootElement.GetProperty("entries")[0].GetProperty(side); var rootName = stored.GetProperty("rootPath").GetString()!.Replace('\\', '/');
                    var entry = archive.GetEntry(rootName); check("anonymous gzip package root exists " + side, entry is not null, "standard ZIP reader locates saved DTO root");
                    if (entry is null) continue;
                    using var input = entry.Open(); using var content = new MemoryStream(); input.CopyTo(content);
                    check("anonymous gzip packaged root full bytes " + side, content.ToArray().SequenceEqual(originalBytes) && entry.Length == originalSize, "compressed root complete bytes and size unchanged");
                }
            }
            await run("anonymous-gzip-fallback-unpack", 0, true, ["--archive-extract", package, unpack]);
            var restoredProject = Path.Combine(unpack, "project.json");
            if (File.Exists(restoredProject))
            {
                using var restored = JsonDocument.Parse(File.ReadAllBytes(restoredProject)); VerifyDto(restored.RootElement, "unpack");
                foreach (var side in new[] { "leftArchiveInput", "rightArchiveInput" })
                {
                    var stored = restored.RootElement.GetProperty("entries")[0].GetProperty(side); var rootPath = Path.GetFullPath(stored.GetProperty("rootPath").GetString()!, unpack);
                    check("anonymous gzip unpacked root SHA and size " + side, Hash(rootPath) == originalHash && new FileInfo(rootPath).Length == originalSize && File.ReadAllBytes(rootPath).SequenceEqual(originalBytes), "full compressed original restored");
                    using var file = File.OpenRead(rootPath); using var gzip = new GZipStream(file, CompressionMode.Decompress); using var decoded = new MemoryStream(); gzip.CopyTo(decoded);
                    check("anonymous gzip independent decoded all bytes " + side, decoded.ToArray().SequenceEqual(payload), "independent .NET GZipStream reads every byte");
                }
            }
            await run("anonymous-gzip-fallback-reopened-report", 0, true, ["--report-project", restoredProject, Path.Combine(work, "anonymous-gzip-reopened.html")]);
            check("anonymous gzip input SHA and size retained", Hash(anonymousRoot) == originalHash && new FileInfo(anonymousRoot).Length == originalSize, "readonly source remains the original compressed bytes");
            void VerifyDto(JsonElement document, string stage)
            {
                check("anonymous gzip literal v8 retained " + stage, document.GetProperty("formatVersion").GetInt32() == 8, "existing explicit gzip schema is preserved");
                foreach (var side in new[] { "leftArchiveInput", "rightArchiveInput" })
                {
                    var stored = document.GetProperty("entries")[0].GetProperty(side);
                    check("anonymous gzip literal leaf and File retained " + stage + side, stored.GetProperty("leafEntry").GetString() == "anonymous-root"
                        && stored.GetProperty("containerGZipPayloadKinds").GetArrayLength() == 1 && stored.GetProperty("containerGZipPayloadKinds")[0].GetString() == "File"
                        && stored.GetProperty("rootSha256").GetString() == originalHash, "temporary staging basename never rewrites the selected leaf or gzip choice");
                }
            }
        }

        async Task WorkingIdentityAsync()
        {
            var expectedBodies = new[] { "working Auto\n"u8.ToArray(), "working Tar\n"u8.ToArray() }; var modes = new[] { "Auto", "Tar" };
            var snapshots = new JsonArray();
            for (var index = 0; index < modes.Length; index++)
            {
                var asset = Path.Combine(work, "working-" + modes[index] + ".text"); File.WriteAllBytes(asset, expectedBodies[index]);
                snapshots.Add(new JsonObject { ["entryChain"] = new JsonArray("mixed", "inner.bz2"), ["leafEntry"] = "inner", ["containerCompressionPayloadKinds"] = new JsonArray("File", modes[index], "File"), ["snapshotPath"] = Path.GetFileName(asset), ["sha256"] = Hash(asset), ["encodingName"] = "utf-8", ["hasBom"] = false });
            }
            var ancestor = new JsonObject { ["rootPath"] = mixedPath, ["rootSha256"] = Hash(mixedPath), ["entryChain"] = new JsonArray(), ["containerCompressionPayloadKinds"] = new JsonArray("File"), ["inheritedReadOnly"] = false, ["workingTexts"] = snapshots };
            var project = new JsonObject { ["formatVersion"] = 9, ["activeEntryIndex"] = 0, ["entries"] = new JsonArray(new JsonObject { ["mode"] = "Archive", ["leftArchiveInput"] = ancestor.DeepClone(), ["rightArchiveInput"] = ancestor.DeepClone(), ["leftReadOnly"] = true, ["rightReadOnly"] = true }) };
            var target = Path.Combine(work, "ancestor-copy.json");
            await run("bare-compression-ancestor-working-copy", 0, true, ["--project-copy", Save("ancestor", project), target]);
            VerifyWorking(target, "clone");
            var package = Path.Combine(work, "ancestor.zip");
            await run("bare-compression-ancestor-working-package", 0, true, ["--package-project", Save("ancestor", project), package]);
            var unpack = Path.Combine(work, "ancestor-unpack");
            await run("bare-compression-ancestor-working-unpack", 0, true, ["--archive-extract", package, unpack]);
            VerifyWorking(Path.Combine(unpack, "project.json"), "package");
            var ancestorReportProject = Path.Combine(unpack, "project.json"); var ancestorReportOutput = Path.Combine(work, "ancestor.html");
            var ancestorReportSentinel = "keep existing ancestor report\n"u8.ToArray(); File.WriteAllBytes(ancestorReportOutput, ancestorReportSentinel);
            var ancestorReportOutputHash = Hash(ancestorReportOutput); var ancestorReportProjectHash = Hash(ancestorReportProject);
            using var ancestorReportDocument = JsonDocument.Parse(File.ReadAllBytes(ancestorReportProject));
            var ancestorReportIsArchive = ancestorReportDocument.RootElement.GetProperty("entries")[0].GetProperty("mode").GetString() == "Archive";
            var ancestorReportRejected = await run("bare-compression-ancestor-working-report", 2, false, ["--report-project", Path.Combine(unpack, "project.json"), Path.Combine(work, "ancestor.html")]);
            check("bare compression Archive report expected unsupported", ancestorReportIsArchive && ancestorReportRejected.Stdout.Length == 0
                && ancestorReportRejected.Stderr.Contains("この形式の単体HTMLレポートは未対応です。", StringComparison.Ordinal)
                && Hash(ancestorReportProject) == ancestorReportProjectHash && Hash(ancestorReportOutput) == ancestorReportOutputHash
                && File.ReadAllBytes(ancestorReportOutput).SequenceEqual(ancestorReportSentinel), "actual Archive mode rejects HTML before publication and retains project and existing output bytes");
            var wrongPrefix = project.DeepClone().AsObject(); wrongPrefix["entries"]![0]!["leftArchiveInput"]!["containerCompressionPayloadKinds"] = new JsonArray("Auto");
            await run("bare-compression-working-prefix-mismatch", 2, false, ["--project-copy", Save("ancestor-mismatch", wrongPrefix), protectedProject]);
            var badSnapshot = project.DeepClone().AsObject(); badSnapshot["entries"]![0]!["leftArchiveInput"]!["workingTexts"]![0]!["containerCompressionPayloadKinds"] = new JsonArray("File");
            await run("bare-compression-working-strict-array", 2, false, ["--project-copy", Save("ancestor-bad-snapshot", badSnapshot), protectedProject]);
            foreach (var variant in new (string Name, JsonNode? Value)[] { ("empty", new JsonArray()), ("long", new JsonArray("File", "Auto", "File", "Auto")), ("unknown", new JsonArray("File", "Unknown", "File")), ("number", new JsonArray("File", 1, "File")), ("null-element", new JsonArray("File", (JsonNode?)null, "File")), ("object", new JsonObject()) })
            {
                var invalid = project.DeepClone().AsObject(); invalid["entries"]![0]!["leftArchiveInput"]!["workingTexts"]![0]!["containerCompressionPayloadKinds"] = variant.Value?.DeepClone();
                await run("bare-compression-working-strict-" + variant.Name, 2, false, ["--project-copy", Save("ancestor-strict-" + variant.Name, invalid), protectedProject]);
                check("bare compression snapshot rejected output retained " + variant.Name, Hash(protectedProject) == protectedHash, "v9 snapshot field validated before output");
            }
            var unknownSnapshot = project.DeepClone().AsObject(); unknownSnapshot["entries"]![0]!["leftArchiveInput"]!["workingTexts"]![0]!["unknownCompressionField"] = true;
            await run("bare-compression-working-unknown-property", 2, false, ["--project-copy", Save("ancestor-snapshot-unknown", unknownSnapshot), protectedProject]);
            var duplicateSnapshot = Path.Combine(work, "ancestor-snapshot-duplicate.json");
            File.WriteAllText(duplicateSnapshot, project.ToJsonString().Replace("\"containerCompressionPayloadKinds\":[\"File\",\"Auto\",\"File\"]", "\"containerCompressionPayloadKinds\":[\"File\",\"Auto\",\"File\"],\"containerCompressionPayloadKinds\":null", StringComparison.Ordinal));
            await run("bare-compression-working-duplicate-property", 2, false, ["--project-copy", duplicateSnapshot, protectedProject]);
            // v9 Binary snapshots share the same prefix boundary and retain independently saved bytes.
            var binary = project.DeepClone().AsObject();
            foreach (var side in new[] { "leftArchiveInput", "rightArchiveInput" })
            foreach (var snapshot in binary["entries"]![0]![side]!["workingTexts"]!.AsArray())
            { snapshot!["kind"] = "Binary"; snapshot["encodingName"] = ""; }
            var binaryCopy = Path.Combine(work, "ancestor-binary-copy.json");
            await run("bare-compression-ancestor-binary-copy", 0, true, ["--project-copy", Save("ancestor-binary", binary), binaryCopy]); VerifyWorking(binaryCopy, "binary-clone");
            var binaryPackage = Path.Combine(work, "ancestor-binary.zip");
            await run("bare-compression-ancestor-binary-package", 0, true, ["--package-project", Save("ancestor-binary", binary), binaryPackage]);
            var binaryUnpack = Path.Combine(work, "ancestor-binary-unpack");
            await run("bare-compression-ancestor-binary-unpack", 0, true, ["--archive-extract", binaryPackage, binaryUnpack]); VerifyWorking(Path.Combine(binaryUnpack, "project.json"), "binary-package");
            void VerifyWorking(string path, string stage)
            {
                check("bare compression ancestor workspace exists " + stage, File.Exists(path), "public clone/package result"); if (!File.Exists(path)) return;
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path)); var stored = doc.RootElement.GetProperty("entries")[0].GetProperty("leftArchiveInput"); var rows = stored.GetProperty("workingTexts");
                check("bare compression ancestor prefix identities " + stage, rows.GetArrayLength() == 2 && stored.GetProperty("containerCompressionPayloadKinds")[0].GetString() == "File", "root prefix matches while descendant Auto/Tar identities stay separate");
                foreach (var row in rows.EnumerateArray())
                {
                    var index = Array.IndexOf(modes, row.GetProperty("containerCompressionPayloadKinds")[1].GetString());
                    var asset = Path.GetFullPath(row.GetProperty("snapshotPath").GetString()!, Path.GetDirectoryName(path)!);
                    check("bare compression working full bytes " + stage + index, index >= 0 && File.ReadAllBytes(asset).SequenceEqual(expectedBodies[index]) && Hash(asset) == row.GetProperty("sha256").GetString(), "separate immutable snapshot bytes and SHA");
                }
            }
        }
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
