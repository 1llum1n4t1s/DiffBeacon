using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class ArchiveZScenarios
{
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check,
        string python, string? reference, string? sevenzip, Action<string, string> skip)
    {
        var root = Path.GetFullPath("tests/Fixtures/Archives/TarZ");
        var work = Path.Combine(fixtures, "tar-z"); Directory.CreateDirectory(work);
        check("tar.Z fixed manifest SHA", Hash(Path.Combine(root, "manifest.json")) == "66472C0B267B7FA8AF6EC766528AF13EFE9C9EDFFE2042F9D1933EF7816658C3", "");
        using var golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "manifest.json")));
        var inputs = golden.RootElement.GetProperty("inputs").EnumerateArray().ToDictionary(x => x.GetProperty("kind").GetString()!);
        var originalHashes = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
        foreach (var input in inputs.Values)
            check("tar.Z raw TAR fixed SHA " + input.GetProperty("kind").GetString(),
                Hash(Path.Combine(root, input.GetProperty("tarName").GetString()!)) == input.GetProperty("tarSha256").GetString(), "");
        foreach (var item in golden.RootElement.GetProperty("normalCases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var path = Path.Combine(root, name); var expected = inputs[item.GetProperty("kind").GetString()!];
            check("tar.Z fixed original SHA " + name, Hash(path) == item.GetProperty("sha256").GetString(), "");
            var result = await run("tar-z-list-" + name, 0, true, ["--archive-list", path]);
            if (result.ExitCode == 0)
            {
                using var list = JsonDocument.Parse(result.Stdout);
                var entries = list.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                var originals = expected.GetProperty("entries").EnumerateArray().ToArray();
                check("tar.Z all manifest rows " + name, list.RootElement.GetProperty("format").GetString() == "tar.Z"
                    && entries.Length == originals.Length && originals.All(e => entries.Any(a =>
                        a.GetProperty("path").GetString() == e.GetProperty("name").GetString()
                        && a.GetProperty("directory").GetBoolean() == (e.GetProperty("type").GetString() == "directory")
                        && a.GetProperty("size").GetInt64() == e.GetProperty("bytes").GetInt64()
                        && a.GetProperty("sha256").GetString() == e.GetProperty("sha256").GetString())), "");
            }
            var raw = Path.Combine(root, expected.GetProperty("tarName").GetString()!);
            await run("tar-z-compare-" + name, 0, true, ["--archive-compare", raw, path]);
            await run("tar-z-provider-" + name, 0, true, ["--provider", "archive", raw, path]);
            await run("tar-z-tar-" + name, 0, true, ["--provider", "tar", raw, path]);
            await run("tar-z-metadata-" + name, 0, true, ["--provider", "tar-metadata", raw, path]);
            var extracted = Path.Combine(work, name + ".extracted");
            await run("tar-z-extract-" + name, 0, true, ["--archive-extract", path, extracted]);
            check("tar.Z all extracted bytes " + name, Directory.Exists(extracted)
                && Directory.GetFiles(extracted, "*", SearchOption.AllDirectories).Length == 2
                && expected.GetProperty("entries").EnumerateArray().All(e => e.GetProperty("type").GetString() == "directory"
                    ? Directory.Exists(Path.Combine(extracted, e.GetProperty("name").GetString()!))
                    : File.Exists(Path.Combine(extracted, e.GetProperty("name").GetString()!))
                        && Hash(Path.Combine(extracted, e.GetProperty("name").GetString()!)) == e.GetProperty("sha256").GetString()), "");
        }
        check("tar.Z independent reference explicitly supplied", reference is not null && File.Exists(reference), reference ?? "--z-reference is required for writer verification");
        foreach (var input in inputs.Values)
        {
            var kind = input.GetProperty("kind").GetString()!;
            var raw = Path.Combine(root, input.GetProperty("tarName").GetString()!);
            var source = Path.Combine(work, "create-" + kind);
            await run("tar-z-source-" + kind, 0, true, ["--archive-extract", raw, source]);
            foreach (var extension in new[] { "tar.Z", "taz" })
            {
                foreach (var action in new[] { "create", "repack" })
                {
                    var product = Path.Combine(work, action + "-" + kind + "." + extension);
                    await run("tar-z-" + action + "-" + kind + "-" + extension, 0, true,
                        ["--archive-" + action, action == "create" ? source : raw, product]);
                    await run("tar-z-written-compare-" + action + "-" + kind + "-" + extension, 0, true, ["--archive-compare", raw, product]);
                    var entry = Path.Combine(work, action + "-" + kind + "-" + extension + ".bin");
                    await run("tar-z-written-entry-" + action + "-" + kind + "-" + extension, 0, true, ["--archive-entry", product, "data.bin", entry]);
                    check("tar.Z writer entry all bytes " + action + kind + extension, File.Exists(entry)
                        && Hash(entry) == input.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "data.bin").GetProperty("sha256").GetString(), "");
                    if (reference is not null && File.Exists(reference))
                        await VerifyWriter(action + "-" + kind + "-" + extension, product, raw);
                }
            }
        }

        // 不正code列と内側TARは独立literal包装で作り、製品writerをoracleにしない。
        var rawBytes = File.ReadAllBytes(Path.Combine(root, "small_repeated.tar"));
        var checksum = rawBytes.ToArray(); checksum[148] = (byte)'9';
        var nonzero = new byte[rawBytes.Length + 512]; rawBytes.CopyTo(nonzero, 0); nonzero[^1] = 1;
        var linked = MakeTar(TarEntryType.SymbolicLink, "link", "../outside");
        var hardlink = MakeTar(TarEntryType.HardLink, "hardlink", "absent");
        var special = MakeTar(TarEntryType.Fifo, "fifo", null);
        var unsafeTar = MakeTar(TarEntryType.RegularFile, "../outside", null);
        var paxHeader = rawBytes[..512]; paxHeader[156] = (byte)'x';
        SetTarLength(paxHeader, 1024 * 1024 + 1);
        var declared = rawBytes[..512]; declared[156] = (byte)'0'; SetTarLength(declared, 256L * 1024 * 1024 + 1);
        var badCases = new Dictionary<string, byte[]>
        {
            ["short-0"] = [], ["short-1"] = [0x1f], ["short-2"] = [0x1f, 0x9d],
            ["wrong-magic"] = [0, 0, 9], ["reserved-flags"] = [0x1f, 0x9d, 0x29],
            ["bits8"] = [0x1f, 0x9d, 8], ["bits17"] = [0x1f, 0x9d, 17],
            ["first-code"] = Codes([511]), ["future-code"] = Codes([65, 400]),
            ["bare-Z"] = Literal("not an archive"u8), ["bad-checksum"] = Literal(checksum),
            ["partial-header"] = Literal(rawBytes.AsSpan(0, 511)),
            ["partial-content"] = Literal(rawBytes.AsSpan(0, 1025)),
            ["missing-end"] = Literal(rawBytes.AsSpan(0, 9728)),
            ["nonzero-trailing"] = Literal(nonzero), ["link"] = Literal(linked),
            ["hardlink"] = Literal(hardlink), ["special"] = Literal(special), ["unsafe"] = Literal(unsafeTar),
            ["metadata-limit"] = Literal(paxHeader), ["declared-entry-limit"] = Literal(declared),
            ["pax-invalid-size"] = Literal(PaxSize(rawBytes, "invalid")),
            ["pax-large-size"] = Literal(PaxSize(rawBytes, "9999999999999")),
            ["pax-size-mismatch"] = Literal(PaxSize(rawBytes, "12")),
            ["plain-tar-disguised"] = rawBytes
        };
        using (var encoded = new MemoryStream())
        {
            using (var gzip = new GZipStream(encoded, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(rawBytes);
            badCases.Add("gzip-disguised", encoded.ToArray());
        }
        foreach (var (name, bytes) in badCases)
        {
            var path = Path.Combine(work, "bad-" + name + ".tar.Z"); File.WriteAllBytes(path, bytes); var before = Hash(path);
            var bad = await run("tar-z-reject-" + name, 2, false, ["--archive-list", path]);
            check("tar.Z rejection diagnostic " + name, string.IsNullOrWhiteSpace(bad.Stdout) && !string.IsNullOrWhiteSpace(bad.Stderr), "");
            var keep = Path.Combine(work, "keep-" + name + ".tar.Z"); File.WriteAllBytes(keep, "existing output"u8.ToArray()); var keepHash = Hash(keep);
            await run("tar-z-repack-reject-" + name, 2, false, ["--archive-repack", path, keep]);
            var target = Path.Combine(work, "bad-extract-" + name);
            await run("tar-z-extract-reject-" + name, 2, false, ["--archive-extract", path, target]);
            check("tar.Z failure preserves all inputs outputs " + name, before == Hash(path) && keepHash == Hash(keep)
                && !Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(work, ".diffbeacon-*").Any(), "");
            if (name is "link" or "hardlink")
            {
                var uncompressed = Path.Combine(work, name + ".tar"); File.WriteAllBytes(uncompressed, name == "link" ? linked : hardlink);
                await run("tar-z-metadata-retains-" + name, 0, true, ["--provider", "tar-metadata", uncompressed, path]);
                await run("tar-z-tar-retains-" + name, 0, true, ["--provider", "tar", uncompressed, path]);
            }
        }
        var original = Path.Combine(root, "small_repeated-b9.tar.Z");
        var renamed = Path.Combine(work, "renamed.data"); File.Copy(original, renamed);
        await run("tar-z-magic-recognition-without-extension", 0, true, ["--archive-compare", original, renamed]);
        await run("tar-z-repack-same-path", 2, false, ["--archive-repack", original, original]);
        await run("tar-z-entry-input-protection", 2, false, ["--archive-entry", original, "data.bin", original]);
        var readonlyOutput = Path.Combine(work, "readonly.tar.Z"); File.WriteAllBytes(readonlyOutput, "protected"u8.ToArray());
        var readonlyHash = Hash(readonlyOutput); var attributes = File.GetAttributes(readonlyOutput);
        try
        {
            File.SetAttributes(readonlyOutput, attributes | FileAttributes.ReadOnly);
            await run("tar-z-readonly-repack", 2, false, ["--archive-repack", original, readonlyOutput]);
            check("tar.Z readonly output bytes retained", Hash(readonlyOutput) == readonlyHash, "");
        }
        finally { File.SetAttributes(readonlyOutput, attributes); }
        var linkOutput = Path.Combine(work, "output-link.tar.Z");
        try
        {
            File.CreateSymbolicLink(linkOutput, readonlyOutput);
            await run("tar-z-output-link-protection", 2, false, ["--archive-repack", original, linkOutput]);
            check("tar.Z linked output target bytes retained", Hash(readonlyOutput) == readonlyHash, "");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException
            || exception is IOException && (exception.HResult & 0xffff) == 1314)
        { skip("tar.Z output link fixture unavailable", exception.Message); }
        finally
        {
            if (new FileInfo(linkOutput).LinkTarget is not null) File.Delete(linkOutput);
        }

        // Archiveの詳細HTMLは既存の未対応境界。包装metadataと相対入力再読込みを確認。
        var project = Path.Combine(work, "archive-project.json");
        File.WriteAllText(project, JsonSerializer.Serialize(new { formatVersion = 1, activeEntryIndex = 0,
            entries = new[] { new { leftPath = original, rightPath = original, basePath = "", mode = "Archive" } } }));
        var html = Path.Combine(work, "archive.html"); File.WriteAllText(html, "protected HTML");
        await run("tar-z-report-existing-boundary", 2, false, ["--report-project", project, html]);
        check("tar.Z unsupported report retains existing HTML", File.ReadAllText(html) == "protected HTML", "");
        var package = Path.Combine(work, "archive-package.zip");
        await run("tar-z-package-project", 0, true, ["--package-project", project, package, "--report"]);
        using (var zip = ZipFile.OpenRead(package))
        {
            var report = zip.Entries.Single(e => e.FullName == "report.files/1.html");
            using var reader = new StreamReader(report.Open()); var text = reader.ReadToEnd();
            check("tar.Z packaged HTML metadata boundary", text.Contains("詳細な比較レポートは未対応", StringComparison.Ordinal)
                && text.Contains("SHA-256", StringComparison.Ordinal), "");
        }
        var expanded = Path.Combine(work, "package-expanded"); ZipFile.ExtractToDirectory(package, expanded);
        var savedProject = Directory.GetFiles(expanded, "*.json", SearchOption.AllDirectories).Single(p => Path.GetFileName(p) == "project.json");
        using (var restored = JsonDocument.Parse(File.ReadAllBytes(savedProject)))
        {
            var entry = restored.RootElement.GetProperty("entries")[0];
            var left = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(savedProject)!, entry.GetProperty("leftPath").GetString()!));
            var right = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(savedProject)!, entry.GetProperty("rightPath").GetString()!));
            await run("tar-z-packaged-relative-input-reload", 0, true, ["--archive-compare", left, right]);
            check("tar.Z package original compressed bytes retained", Hash(left) == Hash(original) && Hash(right) == Hash(original), "");
        }
        check("tar.Z fixed inputs unchanged", originalHashes.All(p => Hash(p.Key) == p.Value), "");
        File.WriteAllText(Path.Combine(output, "tar-z-input-binding.json"), JsonSerializer.Serialize(originalHashes, new JsonSerializerOptions { WriteIndented = true }));

        async Task VerifyWriter(string name, string product, string raw)
        {
            var proofDirectory = Path.Combine(work, "independent-" + name);
            var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { Path.GetFullPath("tests/DiffBeacon.E2E/z_archive_verifier.py"), "--archive", product,
                "--expected-tar", raw, "--decoder", Path.GetFullPath(reference!), "--output", proofDirectory }) start.ArgumentList.Add(argument);
            if (sevenzip is not null) { start.ArgumentList.Add("--sevenzip"); start.ArgumentList.Add(Path.GetFullPath(sevenzip)); }
            using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); throw; }
            var response = await stdout; var diagnostic = await stderr;
            File.WriteAllText(Path.Combine(output, "tar-z-independent-" + name + ".stdout.json"), response);
            File.WriteAllText(Path.Combine(output, "tar-z-independent-" + name + ".stderr.txt"), diagnostic);
            check("tar.Z independent writer decoder and metadata " + name, process.ExitCode == 0 && string.IsNullOrWhiteSpace(diagnostic), diagnostic);
        }
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static byte[] MakeTar(TarEntryType type, string name, string? link)
    {
        using var bytes = new MemoryStream();
        using (var tar = new TarWriter(bytes, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(type, name);
            if (link is not null) entry.LinkName = link;
            if (type == TarEntryType.RegularFile) entry.DataStream = new MemoryStream("fixture bytes"u8.ToArray());
            tar.WriteEntry(entry);
        }
        return bytes.ToArray();
    }
    private static byte[] Literal(ReadOnlySpan<byte> bytes) => Codes(bytes.ToArray().Select(b => (int)b));
    private static void SetTarLength(byte[] header, long size)
    {
        Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0') + "\0").CopyTo(header, 124);
        header.AsSpan(148, 8).Fill((byte)' ');
        var checksum = header.Sum(value => (int)value);
        Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
    }
    private static byte[] PaxSize(byte[] raw, string value)
    {
        var body = " size=" + value + "\n"; var length = body.Length + 1;
        while (length != body.Length + length.ToString(System.Globalization.CultureInfo.InvariantCulture).Length)
            length = body.Length + length.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        var record = Encoding.ASCII.GetBytes(length.ToString(System.Globalization.CultureInfo.InvariantCulture) + body);
        var metadata = raw[..512]; metadata[156] = (byte)'x'; SetTarLength(metadata, record.Length);
        using var result = new MemoryStream(); result.Write(metadata); result.Write(record);
        result.Write(new byte[(512 - record.Length % 512) % 512]); result.Write(raw);
        return result.ToArray();
    }
    private static byte[] Codes(IEnumerable<int> codes)
    {
        using var bytes = new MemoryStream(); bytes.Write([0x1f, 0x9d, 9]);
        uint bits = 0; var length = 0;
        foreach (var code in codes)
        {
            bits |= (uint)code << length; length += 9;
            while (length >= 8) { bytes.WriteByte((byte)bits); bits >>= 8; length -= 8; }
        }
        if (length != 0) bytes.WriteByte((byte)bits);
        return bytes.ToArray();
    }
}
