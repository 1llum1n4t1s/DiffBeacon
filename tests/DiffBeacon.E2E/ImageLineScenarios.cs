using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ImageLineScenarios
{
    // 失敗先行: ImageLines/README.md。入力と期待scriptは無改変原本からの採取。
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var root = FixtureRepository.FindRoot();
        if (root is null) throw new DirectoryNotFoundException("ImageLines fixture root");
        var source = Path.Combine(root.FullName, "tests", "Fixtures", "ImageLines", "winimerge-image-lines-golden.json.gz");
        var zipped = await File.ReadAllBytesAsync(source);
        const string zipSha = "38B5DC1CAE36478FB59D713ACF7DE19BA704B5BD94AF3D50955A6F4F56BB966B";
        var actualZipSha = Convert.ToHexString(SHA256.HashData(zipped));
        check("image-lines-gzip-sha", actualZipSha == zipSha, actualZipSha);
        if (actualZipSha != zipSha) throw new InvalidDataException("Image line golden changed");
        using var compressed = new MemoryStream(zipped);
        using var unzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var expanded = new MemoryStream();
        await unzip.CopyToAsync(expanded);
        var bytes = expanded.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        const string goldenSha = "159878A6360C7DCFC79EA092379D24EBBF90A1EA8ACF789BD50F5B8A154A8CC1";
        check("image-lines-source-sha", sha == goldenSha, sha);
        if (sha != goldenSha) throw new InvalidDataException("Image line observations changed");
        using var golden = JsonDocument.Parse(bytes);
        var wanted = golden.RootElement.GetProperty("cases");
        check("image-lines-source-count", wanted.GetArrayLength() == 14_797, wanted.GetArrayLength().ToString());
        var folder = Path.Combine(fixtures, "image-lines"); Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "rows.json");
        // 製品プロセスへ期待値を渡さず、元入力だけを渡す。
        using (var stream = File.Create(input))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteStartArray("cases");
            foreach (var item in wanted.EnumerateArray())
            {
                writer.WriteStartObject();
                foreach (var name in new[] { "name", "threshold", "left", "right" })
                { writer.WritePropertyName(name); item.GetProperty(name).WriteTo(writer); }
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        var before = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(input)));
        var result = await run("image-lines-original-batch", 0, true, ["--image-line-script", input]);
        using var observed = JsonDocument.Parse(result.Stdout);
        var actual = observed.RootElement.GetProperty("cases");
        check("image-lines-result-count", actual.GetArrayLength() == wanted.GetArrayLength(), actual.GetArrayLength().ToString());
        for (var i = 0; i < Math.Min(actual.GetArrayLength(), wanted.GetArrayLength()); i++)
        {
            var label = "image-lines-" + wanted[i].GetProperty("name").GetString();
            check(label + "-name", actual[i].GetProperty("name").GetString() == wanted[i].GetProperty("name").GetString(), "input order");
            check(label + "-script", actual[i].GetProperty("script").GetString() == wanted[i].GetProperty("script").GetString(), "original script bytes");
            foreach (var side in new[] { "leftHashes", "rightHashes" })
                check(label + "-" + side,
                    actual[i].GetProperty(side).EnumerateArray().Select(x => x.GetUInt32()).SequenceEqual(
                        wanted[i].GetProperty(side).EnumerateArray().Select(x => x.GetUInt32())), "original uint32 row hashes");
        }
        var budgetInput = Path.Combine(folder, "budget.json");
        await File.WriteAllTextAsync(budgetInput, JsonSerializer.Serialize(new
        { cases = new[] { wanted[0], wanted[1] }.Select(item => new
            { name = item.GetProperty("name").GetString(), threshold = item.GetProperty("threshold").GetDouble(),
                left = item.GetProperty("left"), right = item.GetProperty("right") }) }));
        var exactBudget = actual[0].GetProperty("work").GetInt64() + actual[1].GetProperty("work").GetInt64();
        var budgetSuccess = await run("image-lines-budget-exact", 0, true,
            ["--image-line-script", budgetInput, "--max-work", exactBudget.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        using (var exact = JsonDocument.Parse(budgetSuccess.Stdout))
            check("image-lines-budget-exact-scripts", exact.RootElement.GetProperty("cases")[0].GetProperty("script").GetString() == wanted[0].GetProperty("script").GetString()
                && exact.RootElement.GetProperty("cases")[1].GetProperty("script").GetString() == wanted[1].GetProperty("script").GetString(), "fixed original scripts at budget boundary");
        foreach (var budget in new[] { "0", (exactBudget - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "-1", "400000001" })
        {
            var rejected = await run("image-lines-budget-" + budget, 2, false,
                ["--image-line-script", budgetInput, "--max-work", budget]);
            check("image-lines-budget-" + budget + "-atomic-output", string.IsNullOrWhiteSpace(rejected.Stdout), "no partial batch at shared limit");
        }
        foreach (var (name, body) in new[]
        {
            ("bad-dimensions", "{\"cases\":[{\"name\":\"bad\",\"threshold\":0,\"left\":{\"width\":1,\"height\":1,\"bgraHex\":\"00\"},\"right\":{\"width\":1,\"height\":0,\"bgraHex\":\"\"}}]}"),
            ("negative-threshold", "{\"cases\":[{\"name\":\"bad\",\"threshold\":-1,\"left\":{\"width\":1,\"height\":0,\"bgraHex\":\"\"},\"right\":{\"width\":1,\"height\":0,\"bgraHex\":\"\"}}]}"),
            ("undefined-threshold", "{\"cases\":[{\"name\":\"bad\",\"threshold\":1e300,\"left\":{\"width\":1,\"height\":0,\"bgraHex\":\"\"},\"right\":{\"width\":1,\"height\":0,\"bgraHex\":\"\"}}]}"),
            ("late-invalid", "{\"cases\":[{\"name\":\"valid\",\"threshold\":0,\"left\":{\"width\":1,\"height\":0,\"bgraHex\":\"\"},\"right\":{\"width\":1,\"height\":0,\"bgraHex\":\"\"}},{\"name\":\"invalid\",\"threshold\":0,\"left\":{\"width\":1,\"height\":1,\"bgraHex\":\"gggggggg\"},\"right\":{\"width\":1,\"height\":0,\"bgraHex\":\"\"}}]}")
        })
        {
            var bad = Path.Combine(folder, name + ".json"); await File.WriteAllTextAsync(bad, body);
            var rejected = await run("image-lines-" + name, 2, false, ["--image-line-script", bad]);
            check("image-lines-" + name + "-atomic-output", string.IsNullOrWhiteSpace(rejected.Stdout), "no partial JSON");
        }
        check("image-lines-input-preserved", Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(input))) == before, "input SHA");
        await File.WriteAllTextAsync(Path.Combine(output, "image-lines-proof.json"), JsonSerializer.Serialize(new
        { cases = wanted.GetArrayLength(), sourceSha256 = sha, gzipSha256 = actualZipSha, inputSha256 = before,
            outputSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(result.Stdout))) }));
    }
}
