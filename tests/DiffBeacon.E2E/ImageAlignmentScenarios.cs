using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ImageAlignmentScenarios
{
    // 失敗先行: ghostを原画へ混入、三者区間順、水平回転、空区間と境界の逆座標。
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var root = FixtureRepository.FindRoot();
        if (root is null) throw new DirectoryNotFoundException("ImageInsertions fixture root");
        var zip = await File.ReadAllBytesAsync(Path.Combine(root.FullName, "tests", "Fixtures", "ImageInsertions", "winimerge-insertions-golden.json.gz"));
        var zipSha = Convert.ToHexString(SHA256.HashData(zip));
        check("image-alignment-gzip-sha", zipSha == "2B0275994E7445F8BF4A745CF7DCE8BBF531C647D43095751F12AE6278A7AF21", zipSha);
        if (zipSha != "2B0275994E7445F8BF4A745CF7DCE8BBF531C647D43095751F12AE6278A7AF21") throw new InvalidDataException("Alignment golden changed");
        using var packed = new MemoryStream(zip);
        using var gzip = new GZipStream(packed, CompressionMode.Decompress);
        using var bytes = new MemoryStream(); await gzip.CopyToAsync(bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
        check("image-alignment-source-sha", sha == "991FC3F5CBAA2B039D6320E85AB9ADE0F6BB2CE186FD5AFFC30BA43D4777EDDB", sha);
        if (sha != "991FC3F5CBAA2B039D6320E85AB9ADE0F6BB2CE186FD5AFFC30BA43D4777EDDB") throw new InvalidDataException("Alignment observations changed");
        using var golden = JsonDocument.Parse(bytes.ToArray());
        var folder = Path.Combine(fixtures, "image-alignment"); Directory.CreateDirectory(folder);
        var count = 0;
        foreach (var item in golden.RootElement.GetProperty("cases").EnumerateArray())
        {
            var first = item.GetProperty("actions")[0];
            if (first[0].GetString() != "mode") continue;
            var mode = first[3].GetInt32();
            var name = item.GetProperty("name").GetString()!;
            var input = item.GetProperty("inputs"); var paths = new string[input.GetArrayLength()]; var hashes = new string[paths.Length];
            for (var pane = 0; pane < paths.Length; pane++)
            {
                paths[pane] = Path.Combine(folder, name + "-" + pane + ".png");
                var png = Convert.FromBase64String(input[pane].GetProperty("pngBase64").GetString()!);
                await File.WriteAllBytesAsync(paths[pane], png);
                hashes[pane] = Convert.ToHexString(SHA256.HashData(png));
            }
            var arguments = new List<string> { "--image-align" }; arguments.AddRange(paths);
            if (mode == 2) arguments.Add("--horizontal");
            arguments.AddRange(["--threshold", item.GetProperty("threshold").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--block-size", item.GetProperty("blockSize").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            var result = await run("image-alignment-" + name, 0, true, arguments.ToArray());
            using var actual = JsonDocument.Parse(result.Stdout);
            var expected = item.GetProperty("states")[1];
            foreach (var property in new[] { "mode", "differenceCount", "conflictCount" })
                check("image-alignment-" + name + "-" + property, actual.RootElement.GetProperty(property).GetInt32() == expected.GetProperty(property).GetInt32(), "original state after mode selection");
            var observedPanes = actual.RootElement.GetProperty("panes");
            for (var pane = 0; pane < paths.Length; pane++)
            {
                var observed = observedPanes[pane]; var wanted = expected.GetProperty("panes")[pane];
                var label = "image-alignment-" + name + "-pane" + pane;
                foreach (var dimension in new[] { "canvasWidth", "canvasHeight" })
                    check(label + "-" + dimension, observed.GetProperty(dimension).GetInt32() == wanted.GetProperty(dimension).GetInt32(), "original canvas dimensions");
                check(label + "-bgra", Convert.FromHexString(observed.GetProperty("bgraHex").GetString()!).SequenceEqual(
                    Convert.FromHexString(wanted.GetProperty("bgraHex").GetString()!)), "all original aligned BGRA including ghost");
                static IEnumerable<string> Points(JsonElement values) => values.EnumerateArray().Select(value => string.Join(",", value.EnumerateArray().Select(part => part.ToString())));
                check(label + "-mapping", Points(observed.GetProperty("mapping")).SequenceEqual(Points(wanted.GetProperty("mapping"))), "all original coordinates and one-pixel perimeter");
                check(label + "-input-preserved", Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(paths[pane]))) == hashes[pane], "original PNG unchanged");
            }
            count++;
        }
        check("image-alignment-initial-state-count", count == 56, count.ToString());
        await File.WriteAllTextAsync(Path.Combine(output, "image-alignment-proof.json"), JsonSerializer.Serialize(new
        { cases = count, sourceSha256 = sha, gzipSha256 = zipSha, scope = "first mode state only; structural copy and transformed states remain" }));
    }
}
