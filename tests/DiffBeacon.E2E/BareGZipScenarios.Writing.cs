using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

internal static partial class BareGZipScenarios
{
    // 実CLIのrepack/createを候補化する。取消/任意limit/GUIはproofの未資格項目。
    private static async Task RunWritingAsync(string output, string work, string python,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var directory = Path.Combine(work, "writing"); Directory.CreateDirectory(directory);
        var observations = new List<object>(); var originals = new Dictionary<string, string>();
        var cases = new List<object>();
        var bytes = Enumerable.Range(0, 8193).Select(i => (byte)((i * 31 + i / 256) % 256)).ToArray();
        var single = Zip("single.zip", [("folder/payload.bin", bytes)]);
        var empty = Zip("empty.zip", [("folder/empty.bin", Array.Empty<byte>())]);
        var multiple = Zip("multiple.zip", [("a.bin", bytes), ("b.bin", new byte[] { 1, 2, 3 })]);
        var visibleDirectory = Zip("directory.zip", [("visible/", null), ("visible/payload.bin", bytes)]);
        var zero = Zip("zero.zip", []);
        foreach (var extension in new[] { ".gz", ".gzip" })
        {
            await Success("single" + extension, single, bytes, "payload.bin", "7061796C6F61642E62696E", extension, []);
            await Success("empty" + extension, empty, [], "empty.bin", "656D7074792E62696E", extension, []);
            foreach (var isEmpty in new[] { false, true })
            {
                var payload = isEmpty ? Array.Empty<byte>() : bytes;
                var name = isEmpty ? "create-empty.bin" : "create-payload.bin";
                var input = Path.Combine(directory, name);
                if (!File.Exists(input)) { File.WriteAllBytes(input, payload); originals[input] = Hash(input); }
                await Success("create-" + isEmpty + extension, input, payload, name, Convert.ToHexString(Encoding.ASCII.GetBytes(name)), extension, [], "--archive-create");
            }
        }
        var latin = Zip("latin.zip", [("nested/café.bin", bytes)]);
        await Success("latin-default", latin, bytes, "café.bin", "636166E92E62696E", ".gz", []);
        var japanese = Zip("japanese.zip", [("nested/日本.bin", bytes)]);
        await Success("japanese-utf8", japanese, bytes, "日本.bin", "E697A5E69CAC2E62696E", ".gzip", ["--output-gzip-name-code-page", "65001"]);
        await Success("japanese-932", japanese, bytes, "日本.bin", "93FA967B2E62696E", ".gz", ["--output-gzip-name-code-page", "932"]);
        var euro = Zip("euro.zip", [("nested/Euro-€.bin", bytes)]);
        await Success("euro-1252", euro, bytes, "Euro-€.bin", "4575726F2D802E62696E", ".gz", ["--output-gzip-name-code-page", "1252"]);
        // read=1252でもwriteは既定Latin1。€を置換せず拒否し、明示write=1252だけを許可する。
        var legacy = Path.Combine(work, "windows-1252.gz"); originals[legacy] = Hash(legacy);
        var legacyPayload = Path.Combine(directory, "legacy-expected.bin");
        await Invoke("legacy-export", 0, false, ["--archive-entry", legacy, "Euro-€.bin", legacyPayload, "--gzip-name-code-page", "1252"]);
        await Success("legacy-independent-options", legacy, File.ReadAllBytes(legacyPayload), "Euro-€.bin", "4575726F2D802E62696E", ".gzip",
            ["--gzip-name-code-page", "1252", "--output-gzip-name-code-page", "1252"]);
        foreach (var (id, input, options) in new (string, string, string[])[]
        {
            ("multiple", multiple, []), ("visible-directory", visibleDirectory, []), ("no-entry", zero, []),
            ("unencodable-default", japanese, []), ("unencodable-932", euro, ["--output-gzip-name-code-page", "932"]),
            ("read-not-inherited", legacy, ["--gzip-name-code-page", "1252"]),
            ("codepage-0", single, ["--output-gzip-name-code-page", "0"]),
            ("codepage-utf16", single, ["--output-gzip-name-code-page", "1200"]),
            ("codepage-invalid", single, ["--output-gzip-name-code-page", "999999"]),
            ("codepage-negative", single, ["--output-gzip-name-code-page", "-1"]),
            ("codepage-text", single, ["--output-gzip-name-code-page", "bad"]),
            ("codepage-missing", single, ["--output-gzip-name-code-page"]),
            ("codepage-duplicate", single, ["--output-gzip-name-code-page", "28591", "--output-gzip-name-code-page", "28591"])
        })
        {
            foreach (var exists in new[] { false, true })
            {
                var destination = Path.Combine(directory, id + "-" + exists + ".gz");
                if (exists) File.WriteAllText(destination, "existing output must survive", new UTF8Encoding(false));
                var before = exists ? Hash(destination) : null;
                var result = await Invoke("reject-" + id + "-" + exists, 2, false, ["--archive-repack", input, destination, .. options]);
                check("gzip writer rejected output " + id + " " + exists, result.Stdout.Length == 0 && result.Stderr.Length > 0
                    && (exists ? Hash(destination) == before : !File.Exists(destination)), "input/output retained; no partial publication");
            }
        }
        foreach (var extension in new[] { ".zip", ".tar", ".tar.gz", ".7z" })
        {
            var destination = Path.Combine(directory, "wrong-option" + extension); File.WriteAllText(destination, "protected"); var before = Hash(destination);
            var rejected = await Invoke("wrong-output-option" + extension, 2, false,
                ["--archive-repack", single, destination, "--output-gzip-name-code-page", "65001"]);
            check("gzip writer option only bare output " + extension, rejected.Stdout.Length == 0 && Hash(destination) == before, "write option rejected for non-gzip format");
        }
        await Invoke("read-command-write-option", 2, false, ["--archive-list", single, "--output-gzip-name-code-page", "28591"]);
        var sameHash = Hash(single);
        await Invoke("same-input-output", 2, false, ["--archive-repack", single, single]);
        check("gzip writer same-file input retained", Hash(single) == sameHash, "output protection is checked before format-dependent writing");
        var tarGzip = Path.Combine(directory, "priority.tar.gz");
        await Invoke("tar-priority", 0, true, ["--archive-repack", visibleDirectory, tarGzip]);
        var tarRows = new[] { new { name = "visible", directory = true, expectedHex = "" },
            new { name = "visible/payload.bin", directory = false, expectedHex = Convert.ToHexString(bytes) } };
        var specification = Path.Combine(directory, "reader-input.json");
        File.WriteAllText(specification, JsonSerializer.Serialize(new { cases, tarGzip, tarRows }, new JsonSerializerOptions { WriteIndented = true }));
        var script = Path.Combine(directory, "verify_gzip_writer.py"); File.WriteAllText(script, WriterReader, new UTF8Encoding(false));
        var proof = Path.Combine(directory, "independent-reader.json");
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-B", "-X", "utf8", script, specification, proof }) start.ArgumentList.Add(argument);
        var launch = DateTime.UtcNow; using var process = Process.Start(start) ?? throw new IOException("gzip writer独立readerを起動できません。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(); var stderrTask = process.StandardError.ReadToEndAsync();
        var timedOut = false; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { timedOut = true; if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        var stdout = await stdoutTask; var stderr = await stderrTask;
        File.WriteAllText(Path.Combine(directory, "reader.stdout.json"), stdout); File.WriteAllText(Path.Combine(directory, "reader.stderr.txt"), stderr);
        File.WriteAllText(Path.Combine(directory, "reader-process.json"), JsonSerializer.Serialize(new { launch, actualExit = process.ExitCode, timedOut,
            waitCompleted = true, stdoutComplete = true, stderrComplete = true, arguments = start.ArgumentList.ToArray(), readerSha256 = Hash(script) }));
        check("gzip writer independent gzip/zlib/header/trailer/TAR", !timedOut && process.ExitCode == 0 && File.Exists(proof), stdout + stderr);
        foreach (var (path, sha) in originals) check("gzip writer original retained " + Path.GetFileName(path), Hash(path) == sha, "all synthetic ZIP and fixed gzip inputs");
        File.WriteAllText(Path.Combine(output, "gzip-writer-proof.json"), JsonSerializer.Serialize(new
        { observations, originals, specification, independentReader = proof,
            unqualified = new[] { "GUI writer/native dialogs", "deterministic cancellation", "entry/decoded/output/path/work limits", "all candidate cases pending real C# build/run" },
            retainedReadScenarios = "BareGZipScenarios + PayloadKinds unchanged; fixed valid empty DEFLATE 03 00 retained" }, new JsonSerializerOptions { WriteIndented = true }));

        string Zip(string name, (string path, byte[]? content)[] entries)
        {
            var path = Path.Combine(directory, name);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) foreach (var entry in entries)
            {
                var row = zip.CreateEntry(entry.path); row.LastWriteTime = new DateTimeOffset(2024, 1, 2, 3, 4, 6, TimeSpan.Zero);
                if (entry.content is not null) { using var stream = row.Open(); stream.Write(entry.content); }
            }
            originals[path] = Hash(path); return path;
        }
        async Task<CommandResult> Invoke(string id, int exit, bool json, string[] args)
        {
            var result = await run("gzip-write-" + id, exit, json, args);
            observations.Add(new { id, args, expectedExit = exit, result.ExitCode }); return result;
        }
        async Task Success(string id, string input, byte[] expected, string name, string rawNameHex, string extension, string[] options, string command = "--archive-repack")
        {
            // 既存outputの置換も実CLIで確認。成功後のみ独立readerへ登録する。
            var destination = Path.Combine(directory, id + extension); File.WriteAllText(destination, "old output");
            var result = await Invoke(id, 0, true, [command, input, destination, .. options]);
            check("gzip writer output exists " + id, result.ExitCode == 0 && File.Exists(destination), "existing destination replaced by complete member");
            cases.Add(new { id, archive = destination, expectedHex = Convert.ToHexString(expected), name, rawNameHex });
        }
    }

    private const string WriterReader = """
import gzip, hashlib, json, pathlib, struct, sys, tarfile, zlib
spec = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
proof = []
for case in spec['cases']:
    raw = pathlib.Path(case['archive']).read_bytes()
    expected = bytes.fromhex(case['expectedHex'])
    assert raw[:4] == bytes([31, 139, 8, 8]), (case['id'], 'header/flags')
    assert raw[8:10] == bytes([0, 255]), (case['id'], 'XFL/OS')
    end = raw.index(b'\0', 10)
    name = raw[10:end]
    assert name == bytes.fromhex(case['rawNameHex']), (case['id'], 'FNAME')
    assert b'/' not in name and b'\\' not in name, (case['id'], 'basename')
    deflated = raw[end + 1:-8]
    decoder = zlib.decompressobj(-15)
    decoded = decoder.decompress(deflated) + decoder.flush()
    assert decoder.eof and not decoder.unused_data and not decoder.unconsumed_tail, (case['id'], 'raw DEFLATE EOF')
    assert decoded == expected and gzip.decompress(raw) == expected, (case['id'], 'all bytes')
    crc, size = struct.unpack('<II', raw[-8:])
    assert crc == zlib.crc32(expected) and size == len(expected) % (2 ** 32), (case['id'], 'CRC/ISIZE')
    if not expected:
        assert deflated == bytes.fromhex('0300'), (case['id'], 'valid empty final block')
    proof.append(dict(id=case['id'], bytes=len(expected), sha256=hashlib.sha256(decoded).hexdigest(),
                      headerHex=raw[:10].hex(), fnameHex=name.hex(), crc32=crc, isize=size, eof=True))
with tarfile.open(spec['tarGzip'], 'r:gz') as archive:
    actual = archive.getmembers()
    wanted = spec['tarRows']
    assert len(actual) == len(wanted), 'TAR priority entry count'
    for row, entry in zip(wanted, actual):
        assert entry.name.rstrip('/') == row['name'] and entry.isdir() == row['directory'], 'TAR metadata'
        if not entry.isdir():
            assert archive.extractfile(entry).read() == bytes.fromhex(row['expectedHex']), 'TAR all bytes'
result = dict(cases=proof, tarPriority=True, allChecksPassed=True)
pathlib.Path(sys.argv[2]).write_text(json.dumps(result, indent=2), encoding='utf-8')
print(json.dumps(result))
""";
}
