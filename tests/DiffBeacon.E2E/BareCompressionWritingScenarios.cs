using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class BareCompressionWritingScenarios
{
    // 期待原文は固定fixture expected.jsonまたは独立に作ったbytes。製品の復号出力を期待値にしない。
    internal static async Task RunAsync(string output, string work, JsonElement fixtureCases,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check,
        string python, string reference, object referenceQualification,
        Func<string, string, Task>? verifyIndependent = null)
    {
        var directory = Path.Combine(work, "writing");
        if (Directory.Exists(directory)) throw new IOException("裸圧縮writer検証には未使用の出力directoryが必要です。");
        Directory.CreateDirectory(directory);
        var observations = new List<object>(); var cases = new List<object>(); var tarCases = new List<object>(); var rejections = new List<object>(); var linkEvidence = new List<object>();
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        var unqualified = new List<string>
        {
            "Independent Python bz2 / qualified official ncompress reader pending; manifest creation is not verification",
            "Deterministic cancellation and small entry/decoded/output/work budgets: next App headless real Providers checks",
            "Primary/Dispose double failure and normal Dispose failure: next App headless real Providers checks",
            "GUI writer/native dialogs and AOT"
        };
        var payload = Enumerable.Range(0, 8193).Select(i => (byte)((i * 31 + i / 256) % 256)).ToArray();
        var single = Zip("single.zip", [("nested/日本.bin", payload)]);
        var empty = Zip("empty.zip", [("empty.bin", Array.Empty<byte>())]);
        var multiple = Zip("multiple.zip", [("a.bin", payload), ("b.bin", new byte[] { 1, 2, 3 })]);
        var zero = Zip("zero.zip", []);
        var dirOnly = Zip("dir-only.zip", [("folder/", null)]);
        var dirFirst = Zip("dir-first.zip", [("folder/", null), ("folder/payload.bin", payload)]);
        var dirLast = Zip("dir-last.zip", [("folder/payload.bin", payload), ("folder/", null)]);
        var japanese = Input("日本の入力.bin", payload); var createEmpty = Input("empty-input.bin", []);
        var gzip = Path.Combine(directory, "source.gz");
        using (var file = File.Create(gzip)) using (var sink = new GZipStream(file, CompressionLevel.Optimal)) sink.Write(payload);
        Remember(gzip);
        var brokenGzipBytes = File.ReadAllBytes(gzip); brokenGzipBytes[^8] ^= 1;
        var brokenGzip = Input("late-crc.gz", brokenGzipBytes);
        var tarRow = fixtureCases.EnumerateArray().Single(row => row.GetProperty("input").GetString() == "tar.bz2");
        var tarPayload = Convert.FromHexString(tarRow.GetProperty("decodedHex").GetString()!);
        var tarInput = Input("tar-body.bin", tarPayload);
        var oversized = Zip("declared-oversized.zip", [("oversized.bin", Array.Empty<byte>())]);
        // 数百MiBの実体を作らず既定MaximumEntryBytesを越える宣言をlocal/central双方に入れる。
        var oversizedBytes = File.ReadAllBytes(oversized);
        BinaryPrimitives.WriteUInt32LittleEndian(oversizedBytes.AsSpan(22, 4), 256u * 1024 * 1024 + 1);
        var central = -1;
        for (var offset = 30; offset <= oversizedBytes.Length - 28; offset++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(oversizedBytes.AsSpan(offset, 4)) == 0x02014b50) { central = offset; break; }
        if (central < 0) throw new InvalidDataException("合成ZIPのcentral headerがありません。");
        BinaryPrimitives.WriteUInt32LittleEndian(oversizedBytes.AsSpan(central + 24, 4), 256u * 1024 * 1024 + 1);
        // 最初に作ったZIPもrawとして残し、別pathへ拒否用inputを作る。
        oversized = Input("declared-oversized-input.zip", oversizedBytes);
        // SetLengthはsparseを保証しない。1つのlength-only inputを両format/既存有無で共用する。
        // 製品はmetadataで256MiB超を拒否し、内容byte配列を割り当てない。
        var drive = new DriveInfo(Path.GetPathRoot(directory)!);
        var freeBeforeCapacity = drive.AvailableFreeSpace;
        if (freeBeforeCapacity < 512L * 1024 * 1024) throw new IOException("writer容量検証には512MiB以上の空きが必要です。");
        var createCapacity = Path.Combine(directory, "create-capacity-input.bin");
        using (var file = new FileStream(createCapacity, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.SetLength(256L * 1024 * 1024 + 1);
        Remember(createCapacity);
        var capacityAllocation = new { input = createCapacity, logicalBytes = new FileInfo(createCapacity).Length,
            freeBefore = freeBeforeCapacity, freeAfter = drive.AvailableFreeSpace, sparseClaim = false,
            allocatedBytes = (long?)null, allocationMeasurement = "Parent must measure actual allocated bytes; drive free-space delta is concurrent and not equivalent" };
        foreach (var extension in new[] { ".bz2", ".BZ2", ".Z", ".z" })
        {
            var tag = extension switch { ".bz2" => "bzip2-lower", ".BZ2" => "bzip2-upper", ".Z" => "z-upper", _ => "z-lower" };
            await Success("create-japanese-" + tag, japanese, payload, extension, "--archive-create", []);
            await Success("create-empty-" + tag, createEmpty, [], extension, "--archive-create", []);
            await Success("create-tar-body-" + tag, tarInput, tarPayload, extension, "--archive-create", []);
        }
        var createZero = Path.Combine(directory, "create-zero"); Directory.CreateDirectory(createZero);
        var createDir = Path.Combine(directory, "create-directory"); Directory.CreateDirectory(Path.Combine(createDir, "folder"));
        var createMultiple = Path.Combine(directory, "create-multiple"); Directory.CreateDirectory(createMultiple);
        foreach (var name in new[] { "a.bin", "b.bin" }) { var path = Path.Combine(createMultiple, name); File.WriteAllBytes(path, payload); Remember(path); }
        foreach (var extension in new[] { ".bz2", ".Z" })
        {
            var tag = extension switch { ".bz2" => "bzip2-lower", ".BZ2" => "bzip2-upper", ".Z" => "z-upper", _ => "z-lower" };
            await Success("zip-" + tag, single, payload, extension, "--archive-repack", []);
            await Success("empty-zip-" + tag, empty, [], extension, "--archive-repack", []);
            await Success("gzip-" + tag, gzip, payload, extension, "--archive-repack", ["--gzip-payload-kind", "file"]);
            foreach (var name in new[] { "short.bz2", "short.Z", "empty.bz2", "empty.Z", "tar.bz2", "tar.Z" })
            {
                var row = fixtureCases.EnumerateArray().Single(item => item.GetProperty("input").GetString() == name);
                var input = Path.Combine(work, name); Remember(input);
                await Success("from-" + name.Replace('.', '-') + "-" + tag, input,
                    Convert.FromHexString(row.GetProperty("decodedHex").GetString()!), extension,
                    "--archive-repack", ["--compression-payload-kind", "file"]);
            }
            foreach (var (id, input, reason, options) in new (string, string, string, string[])[]
            {
                ("zero", zero, "単一ファイル", []), ("multiple", multiple, "単一ファイル", []),
                ("directory-only", dirOnly, "単一ファイル", []), ("directory-first", dirFirst, "単一ファイル", []),
                ("directory-last", dirLast, "単一ファイル", []),
                ("late-gzip-crc", brokenGzip, "CRC", ["--gzip-payload-kind", "file"]),
                ("entry-limit", oversized, "サイズ上限", [])
            }) foreach (var exists in new[] { false, true })
                await Reject(id + "-" + tag + "-" + exists, input, Path.Combine(directory, id + "-" + tag + "-" + exists + extension),
                    exists, reason, "--archive-repack", options);
            foreach (var (id, input) in new[] { ("create-zero", createZero), ("create-directory", createDir), ("create-multiple", createMultiple) })
                foreach (var exists in new[] { false, true })
                    await Reject(id + "-" + tag + "-" + exists, input, Path.Combine(directory, id + "-" + tag + "-" + exists + extension),
                        exists, "単一ファイル", "--archive-create", []);
            foreach (var exists in new[] { false, true })
                await Reject("create-entry-limit-" + tag + "-" + exists, createCapacity,
                    Path.Combine(directory, "create-entry-limit-" + tag + "-" + exists + extension), exists,
                    "256 MiBまで", "--archive-create", []);
            var badTail = Path.Combine(work, "bad-tail.bz2"); Remember(badTail);
            foreach (var exists in new[] { false, true })
                await Reject("bad-bzip-tail-" + tag + "-" + exists, badTail,
                    Path.Combine(directory, "bad-bzip-tail-" + tag + "-" + exists + extension), exists, null,
                    "--archive-repack", ["--compression-payload-kind", "file"]);
            foreach (var codepage in new[] { "28591", "65001", "932" }) foreach (var command in new[] { "--archive-create", "--archive-repack" })
                await Reject("gzip-option-" + tag + "-" + codepage + "-" + command[10..], command == "--archive-create" ? japanese : single,
                    Path.Combine(directory, "option-" + tag + "-" + codepage + "-" + command[10..] + extension), true,
                    "単一ファイルgzip出力", command, ["--output-gzip-name-code-page", codepage]);
            var same = Path.Combine(work, extension == ".bz2" ? "short.bz2" : "short.Z"); Remember(same);
            var sameHash = Hash(same);
            await Invoke("same-repack-" + tag, 2, false, ["--archive-repack", same, same]);
            await Invoke("same-create-" + tag, 2, false, ["--archive-create", same, same]);
            check("bare writer same input/output retained " + tag, Hash(same) == sameHash, "all input bytes/SHA");
            var readOnly = Path.Combine(directory, "readonly" + extension); File.WriteAllText(readOnly, "protected readonly", new UTF8Encoding(false));
            var attrs = File.GetAttributes(readOnly); var before = Hash(readOnly);
            try
            {
                File.SetAttributes(readOnly, attrs | FileAttributes.ReadOnly);
                await Reject("readonly-" + tag, single, readOnly, true, null, "--archive-repack", [], prepareOutput: false);
                check("bare writer readonly attribute retained " + tag, Hash(readOnly) == before && File.GetAttributes(readOnly).HasFlag(FileAttributes.ReadOnly), "existing bytes and attribute");
            }
            finally { File.SetAttributes(readOnly, attrs); }
            await Links(tag, extension);
        }
        var tarSource = Zip("tar-priority-source.zip", [("folder/", null), ("folder/a.bin", payload), ("b.bin", new byte[] { 4, 5, 6 })]);
        var tarRows = new[] { new { name = "folder", directory = true, expectedHex = "" },
            new { name = "folder/a.bin", directory = false, expectedHex = Convert.ToHexString(payload) },
            new { name = "b.bin", directory = false, expectedHex = "040506" } };
        foreach (var extension in new[] { ".tar.bz2", ".tbz2", ".tbz", ".tar.Z", ".taz" })
        {
            var destination = Path.Combine(directory, "priority" + extension); File.WriteAllText(destination, "old priority output");
            await Invoke("tar-priority" + extension, 0, true, ["--archive-repack", tarSource, destination]);
            var manifest = await Invoke("tar-priority-list" + extension, 0, true, ["--archive-list", destination]);
            using var doc = JsonDocument.Parse(manifest.Stdout);
            check("bare writer TAR priority " + extension, doc.RootElement.GetProperty("entries").GetArrayLength() == 3, "directory and two files retained");
            tarCases.Add(new { id = "priority" + extension, archive = destination, archiveSha256 = Hash(destination), rows = tarRows,
                decoder = extension is ".tar.Z" or ".taz" ? "official-ncompress-then-python-tarfile" : "python-bz2-then-tarfile" });
        }
        foreach (var (path, sha) in originals) check("bare writer input retained " + Path.GetFileName(path), Hash(path) == sha, "raw all bytes/SHA");
        var specification = Path.Combine(directory, "writer-proof-manifest.json");
        File.WriteAllText(specification, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, kind = "bare-compression-writer-cli", independentValidation = "pending",
            python, zReference = Path.GetFullPath(reference), referenceQualification,
            cases, tarCases, observations, rejections, linkEvidence, originals, unqualified, capacityAllocation,
            expectedReaderOutput = Path.Combine(directory, "independent-writer-proof.json"),
            rawRetention = "Every input/output/expected raw file remains in this owned run until approved cleanup"
        }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        var independentProof = Path.Combine(directory, "independent-writer-proof.json");
        if (verifyIndependent is null)
            check("bare writer independent reader not connected", false, "manifest retained; no independent pass inferred");
        else
        {
            await verifyIndependent(specification, independentProof);
            check("bare writer independent proof exists", File.Exists(independentProof), "reader proof required");
            if (File.Exists(independentProof))
            {
                using var proof = JsonDocument.Parse(File.ReadAllBytes(independentProof));
                var result = proof.RootElement;
                check("bare writer independent proof qualified", result.GetProperty("allChecksPassed").GetBoolean()
                    && result.GetProperty("manifestSha256").GetString()!.Equals(Hash(specification), StringComparison.OrdinalIgnoreCase)
                    && result.GetProperty("verifiedCaseIds").GetArrayLength() == cases.Count
                    && result.GetProperty("verifiedTarCaseIds").GetArrayLength() == tarCases.Count,
                    "external reader must verify full bytes, exact case IDs, strict BZip2 end and qualified official Z decode");
            }
        }

        void Remember(string path) => originals.TryAdd(path, Hash(path));
        string Input(string name, byte[] bytes)
        { var path = Path.Combine(directory, name); File.WriteAllBytes(path, bytes); Remember(path); return path; }
        string Zip(string name, (string path, byte[]? bytes)[] entries)
        {
            var path = Path.Combine(directory, name);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) foreach (var row in entries)
            { var entry = zip.CreateEntry(row.path, CompressionLevel.NoCompression); if (row.bytes is not null) { using var sink = entry.Open(); sink.Write(row.bytes); } }
            Remember(path); return path;
        }
        async Task<CommandResult> Invoke(string id, int exit, bool json, string[] arguments)
        {
            var result = await run("bare-write-" + id, exit, json, arguments);
            observations.Add(new { id, arguments, expectedExit = exit, actualExit = result.ExitCode, result.Stdout, result.Stderr,
                result.DurationMilliseconds, result.Pid, result.CreationUtc, result.LaunchUtc, result.ExitObservedUtc, result.LaunchEvidence });
            if (exit == 2) check("bare writer rejection diagnostic " + id, result.Stdout.Length == 0 && result.Stderr.Length > 0, "no partial JSON; failure reason present");
            return result;
        }
        async Task Success(string id, string input, byte[] expected, string extension, string command, string[] options)
        {
            // 大文字小文字でidが同じNTFS fileへ衝突しないようunique directoryを使う。
            var caseDirectory = Path.Combine(directory, "success-" + cases.Count); Directory.CreateDirectory(caseDirectory);
            var destination = Path.Combine(caseDirectory, "出力." + id + extension); File.WriteAllText(destination, "old output");
            var result = await Invoke(id, 0, true, [command, input, destination, .. options]);
            check("bare writer complete output " + id, result.ExitCode == 0 && File.Exists(destination), "existing destination replaced only on success");
            var leaf = Path.GetFileNameWithoutExtension(destination);
            var listed = await Invoke(id + "-list", 0, true, ["--archive-list", destination, "--compression-payload-kind", "file"]);
            using (var doc = JsonDocument.Parse(listed.Stdout))
            {
                var rows = doc.RootElement.GetProperty("entries");
                check("bare writer output basename leaf " + id, rows.GetArrayLength() == 1 && rows[0].GetProperty("path").GetString() == leaf
                    && rows[0].GetProperty("size").GetInt64() == expected.Length
                    && rows[0].GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(expected)), "raw File mode; FNAME/mtime not stored");
            }
            var export = Path.Combine(caseDirectory, "export.bin");
            await Invoke(id + "-entry", 0, true, ["--archive-entry", destination, leaf, export, "--compression-payload-kind", "file"]);
            var extraction = Path.Combine(caseDirectory, "extract");
            await Invoke(id + "-extract", 0, true, ["--archive-extract", destination, extraction, "--compression-payload-kind", "file"]);
            var extracted = Path.Combine(extraction, leaf);
            check("bare writer full-byte export/extract " + id, File.ReadAllBytes(export).SequenceEqual(expected)
                && File.ReadAllBytes(extracted).SequenceEqual(expected), "input-independent all bytes");
            var expectedPath = Path.Combine(caseDirectory, "expected.bin"); File.WriteAllBytes(expectedPath, expected);
            cases.Add(new { id, archive = destination, archiveSha256 = Hash(destination), expectedPath,
                expectedBytes = expected.LongLength, expectedSha256 = Hash(expectedPath), input, inputSha256 = Hash(input),
                leaf, export, extracted, decoder = extension.Equals(".bz2", StringComparison.OrdinalIgnoreCase) ? "python-bz2" : "official-ncompress" });
        }
        async Task Reject(string id, string input, string destination, bool exists, string? reason,
            string command, string[] options, bool prepareOutput = true)
        {
            if (exists && prepareOutput) File.WriteAllText(destination, "protected output", new UTF8Encoding(false));
            var inputSha = Fingerprint(input); var before = exists ? Hash(destination) : null;
            var result = await Invoke(id, 2, false, [command, input, destination, .. options]);
            check("bare writer output protected " + id, Fingerprint(input) == inputSha
                && (exists ? File.Exists(destination) && Hash(destination) == before : !File.Exists(destination)), "input/existing output SHA; no publication");
            rejections.Add(new { id, input, inputIsDirectory = Directory.Exists(input), inputFingerprintBefore = inputSha,
                inputFingerprintAfter = Fingerprint(input), destination, existedBefore = exists, outputSha256Before = before,
                existsAfter = File.Exists(destination), outputSha256After = File.Exists(destination) ? Hash(destination) : null,
                expectedReasonContains = reason, actualReason = result.Stderr });
            if (reason is not null) check("bare writer rejection reason " + id, result.Stderr.Contains(reason, StringComparison.Ordinal), reason);
            check("bare writer no partial temporary " + id, Directory.GetFiles(Path.GetDirectoryName(destination)!, ".diffbeacon-*.archive.tmp").Length == 0, "owned run only");
        }
        async Task Links(string tag, string extension)
        {
            var link = Path.Combine(directory, "output-link" + extension);
            var inputLink = Path.Combine(directory, "input-link-" + tag + ".zip");
            var targetDirectory = Path.Combine(directory, "link-target-" + tag); Directory.CreateDirectory(targetDirectory);
            var directoryLink = Path.Combine(directory, "parent-link-" + tag);
            var target = Input("link-target-" + tag + ".bin", [7, 8, 9]);
            var sentinel = Path.Combine(targetDirectory, "protected" + extension);
            File.WriteAllText(sentinel, "parent link protected output"); Remember(sentinel);
            var absentCanonical = Path.Combine(targetDirectory, "absent" + extension);
            if (File.Exists(absentCanonical) || Directory.Exists(absentCanonical)) throw new IOException("新規parent-link childが既に存在します。");
            var targetHashBefore = Hash(target); var inputHashBefore = Hash(single);
            var directoryBefore = Fingerprint(targetDirectory); var directoryEntriesBefore = DirectorySnapshot(targetDirectory);
            var sentinelBefore = Hash(sentinel);
            var linkRows = new[] { (role: "output", path: link, target, directory: false),
                (role: "input", path: inputLink, target: single, directory: false),
                (role: "parent", path: directoryLink, target: targetDirectory, directory: true) };
            string?[] targetsBefore = new string?[3]; var commandsExecuted = false;
            try
            {
                try
                {
                    File.CreateSymbolicLink(link, target); targetsBefore[0] = new FileInfo(link).LinkTarget;
                    File.CreateSymbolicLink(inputLink, single); targetsBefore[1] = new FileInfo(inputLink).LinkTarget;
                    Directory.CreateSymbolicLink(directoryLink, targetDirectory); targetsBefore[2] = new DirectoryInfo(directoryLink).LinkTarget;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                { unqualified.Add("link creation " + tag + ": " + error.GetType().Name); return; }
                await Reject("output-link-" + tag, single, link, true, "リンク", "--archive-repack", [], prepareOutput: false);
                await Reject("input-link-" + tag, inputLink, Path.Combine(directory, "from-link" + extension), true, "リンク", "--archive-repack", []);
                await Reject("parent-link-" + tag, single, Path.Combine(directoryLink, Path.GetFileName(sentinel)), true,
                    "リンク", "--archive-repack", [], prepareOutput: false);
                await Reject("parent-link-absent-" + tag, single, Path.Combine(directoryLink, Path.GetFileName(absentCanonical)), false,
                    "リンク", "--archive-repack", [], prepareOutput: false);
                commandsExecuted = true;
            }
            finally
            {
                var directoryAfter = Fingerprint(targetDirectory); var sentinelAfter = Hash(sentinel);
                check("bare writer link canonical targets retained " + tag, Hash(target) == targetHashBefore && Hash(single) == inputHashBefore
                    && directoryAfter == directoryBefore && sentinelAfter == sentinelBefore
                    && !File.Exists(absentCanonical) && !Directory.Exists(absentCanonical), "canonical target bytes/tree unchanged; new parent child absent");
                for (var index = 0; index < linkRows.Length; index++)
                {
                    var row = linkRows[index];
                    var after = row.directory ? new DirectoryInfo(row.path).LinkTarget : new FileInfo(row.path).LinkTarget;
                    if (targetsBefore[index] is null && after is null) continue; // 作成されなかったlinkは未資格、存在したpartial linkは必ず記録する。
                    check("bare writer actual link retained " + tag + " " + row.role, targetsBefore[index] is not null
                        && after == targetsBefore[index] && File.GetAttributes(row.path).HasFlag(FileAttributes.ReparsePoint)
                        && Path.GetFullPath(after!, Path.GetDirectoryName(row.path)!).Equals(Path.GetFullPath(row.target),
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "metadata target before/after; no replacement");
                    var childDestinations = new List<object>();
                    if (row.directory)
                    {
                        childDestinations.Add(new { caseId = "parent-link-" + tag, linkChildPath = Path.Combine(row.path, Path.GetFileName(sentinel)),
                            canonicalChildPath = sentinel, existedBefore = true, existsAfter = File.Exists(sentinel),
                            sha256Before = sentinelBefore, sha256After = sentinelAfter });
                        childDestinations.Add(new { caseId = "parent-link-absent-" + tag, linkChildPath = Path.Combine(row.path, Path.GetFileName(absentCanonical)),
                            canonicalChildPath = absentCanonical, existedBefore = false, existsAfter = File.Exists(absentCanonical) || Directory.Exists(absentCanonical),
                            sha256Before = (string?)null, sha256After = File.Exists(absentCanonical) ? Hash(absentCanonical) : null });
                    }
                    linkEvidence.Add(new { role = row.role, linkPath = row.path, expectedTargetPath = Path.GetFullPath(row.target),
                        linkTargetBefore = targetsBefore[index], linkTargetAfter = after, targetKind = row.directory ? "directory" : "file", commandsExecuted,
                        targetFileSha256Before = row.directory ? null : index == 0 ? targetHashBefore : inputHashBefore,
                        targetFileSha256After = row.directory ? null : Hash(row.target),
                        targetDirectoryFingerprintBefore = row.directory ? directoryBefore : null,
                        targetDirectoryFingerprintAfter = row.directory ? directoryAfter : null,
                        targetDirectoryEntriesBefore = row.directory ? directoryEntriesBefore : null,
                        targetDirectoryEntriesAfter = row.directory ? DirectorySnapshot(row.target) : null, childDestinations });
                }
            }
        }
    }
    private static string Fingerprint(string path)
    {
        if (!Directory.Exists(path)) return Hash(path);
        var entries = Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(item => new { relativePath = Path.GetRelativePath(path, item), directory = Directory.Exists(item),
                sha256 = Directory.Exists(item) ? null : Hash(item) });
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entries)));
    }
    private static object[] DirectorySnapshot(string path)
        => Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(item => (object)new { relativePath = Path.GetRelativePath(path, item), directory = Directory.Exists(item),
                sha256 = Directory.Exists(item) ? null : Hash(item) }).ToArray();
    private static string Hash(string path)
    { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
}
