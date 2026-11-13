using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class FolderPropertyFilterScenarios
{
    // oracleのInt64値をフィルター式のliteralへ投影する。従来どおり `…b`（Size literal）を基本にし、
    // Size literalは原典通りdoubleを経由するため、doubleで正確に表せない値（2^53超の一部やInt64端点）だけ通常の整数literalにする。
    // 通常の整数literalはInt64全域（直結の -9223372036854775808 を含む）を表せる。Size literalへの意図的な依存を変えないよう、
    // 正確に表せる値は `…b` のまま残す。
    private static string Int64Literal(string decimalText)
    {
        var value = long.Parse(decimalText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        // 単項負号はSize変換の後に適用されるため、絶対値をdouble経由で正確に表せるかで判定する（Int64最小値の絶対値2^63は不可）。
        var magnitude = value == long.MinValue ? 9223372036854775808d : Math.Abs((double)value);
        var exact = magnitude < 9223372036854775808d && (long)magnitude == (value < 0 ? -value : value);
        return exact ? decimalText + "b" : decimalText;
    }

    internal static async Task RunAsync(string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check, Action<string, string> skip, string python, bool replaceListsOnly = false,
        Action<string, Action<string>?>? observeStderr = null, bool smoke = false)
    {
        var work = Path.Combine(fixtures, "folder-properties"); Directory.CreateDirectory(work);
        var fixture = Path.GetFullPath("tests/Fixtures/FolderPropertyFilters");
        var info = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(fixture, "verify.py") }) info.ArgumentList.Add(argument);
        using (var process = Process.Start(info)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var evidence = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, "independent-fixture.json"), evidence);
            check("folder properties independent native fixture", process.ExitCode == 0, evidence);
        }
        var allEqualFixture = Path.GetFullPath("tests/Fixtures/FolderAllEqualOracle");
        var allEqualInfo = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(allEqualFixture, "verify.py") }) allEqualInfo.ArgumentList.Add(argument);
        using (var process = Process.Start(allEqualInfo)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var evidence = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, "independent-allequal-oracle.json"), evidence);
            check("folder AllEqual independent native oracle", process.ExitCode == 0, evidence);
        }
        var allOfFixture = Path.GetFullPath("tests/Fixtures/FolderAllOfOracle");
        var allOfInfo = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(allOfFixture, "verify.py") }) allOfInfo.ArgumentList.Add(argument);
        using (var process = Process.Start(allOfInfo)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var evidence = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, "independent-allof-oracle.json"), evidence);
            check("folder AllOf independent native oracle", process.ExitCode == 0, evidence);
        }
        var anyOfFixture = Path.GetFullPath("tests/Fixtures/FolderAnyOfOracle");
        var anyOfInfo = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(anyOfFixture, "verify.py") }) anyOfInfo.ArgumentList.Add(argument);
        using (var process = Process.Start(anyOfInfo)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var evidence = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, "independent-anyof-oracle.json"), evidence);
            check("folder AnyOf independent native oracle", process.ExitCode == 0, evidence);
        }
        var notEachFixture = Path.GetFullPath("tests/Fixtures/FolderNotEachOracle");
        var notEachInfo = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(notEachFixture, "verify.py") }) notEachInfo.ArgumentList.Add(argument);
        using (var process = Process.Start(notEachInfo)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var evidence = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, "independent-noteach-oracle.json"), evidence);
            check("folder NotEach independent native oracle", process.ExitCode == 0, evidence);
        }
        var lenFixture = Path.GetFullPath("tests/Fixtures/FolderLenOracle");
        var lenInfo = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(lenFixture, "verify.py") }) lenInfo.ArgumentList.Add(argument);
        using (var process = Process.Start(lenInfo)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var evidence = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, "independent-len-oracle.json"), evidence);
            check("folder Len independent native oracle", process.ExitCode == 0, evidence);
        }
        var strlenFixture = Path.GetFullPath("tests/Fixtures/FolderStrlenOracle");
        var strlenInfo = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(strlenFixture, "verify.py") }) strlenInfo.ArgumentList.Add(argument);
        using (var process = Process.Start(strlenInfo)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var evidence = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, "independent-strlen-oracle.json"), evidence);
            check("folder Strlen independent native oracle", process.ExitCode == 0, evidence);
        }
        var arrayFixture = Path.GetFullPath("tests/Fixtures/FolderArrayOracle");
        var arrayInfo = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(arrayFixture, "verify.py") }) arrayInfo.ArgumentList.Add(argument);
        using (var process = Process.Start(arrayInfo)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var evidence = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, "independent-array-oracle.json"), evidence);
            check("folder Array independent native oracle", process.ExitCode == 0, evidence);
        }
        var input = Path.Combine(work, "input"); ZipFile.ExtractToDirectory(Path.Combine(fixture, "input.zip"), input);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture, "input-manifest.json")));
        foreach (var item in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = Path.Combine(input, item.GetProperty("name").GetString()!);
            check("folder properties input SHA " + Path.GetFileName(path),
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) == item.GetProperty("sha256").GetString(), path);
            File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddTicks(17000000001234567));
        }
        string Filter(string id, string text)
        {
            var path = Path.Combine(work, id + ".flt"); File.WriteAllText(path, text, new UTF8Encoding(text.Contains('\0'))); return path;
        }
        if (replaceListsOnly) { await RegexReplaceListValues(); await FolderListDependencyCommands(); await FolderListDependencyCommands(literal: true); await FolderSupplementaryCaseFoldScenarios.RunAsync(fixtures, run, check, python); return; }
        // smoke: 式評価の網羅オラクル（OS非依存の純粋な値計算。約8,000命令）は、win-x64の全件実行と同じコードを通るため、
        // win-arm64・osx-arm64と作業ブランチのpushでは重い単位だけを省き、軽い単位（下の Len・Trim など）は全OSで実行する。
        // 省いた単位は skipped として記録し、成功扱いにしない。
        void Heavy(string unit) => skip("folder oracle " + unit, "smoke: 網羅オラクルはwin-x64の全件実行（--oracles full）で検証する。");
        await LoggingValues();
        if (smoke) Heavy("regex count/position/replace/replace-list values"); else { await RegexCountValues(); await RegexPositionValues(); await RegexReplaceValues(); await ReplaceValues(); await RegexReplaceListValues(); }
        if (smoke) Heavy("list dependency, supplementary case fold, provider null, side functions, optimizer");
        else
        {
            await FolderListDependencyCommands(); await FolderListDependencyCommands(literal: true);
            await FolderSupplementaryCaseFoldScenarios.RunAsync(fixtures, run, check, python);
            await FolderProviderNullScenarios.RunAsync(fixtures, run, check, skip, python);
            await FolderSideFunctionScenarios.RunAsync(fixtures, run, check, skip, python);
            await FolderOptimizerScenarios.RunAsync(fixtures, run, check, skip, python);
        }
        await ClockValues();
        if (smoke) Heavy("width and date conversion values"); else { await WidthValues(); await ToDateTimeValues(); await ToDateTimeMetadataDependency(); await ToDateStrValues(); await StartOfValues(); }
        await NormalizeValues();
        await SideAttributes();
        await FlagAttributes();
        await DatePrecision();
        await FileDateEpoch();
        await FullPaths();
        await AllOf();
        await AnyOf();
        await NotEach();
        await ArrayValues();
        await AtValues();
        await LogicalEachValues();
        await ConditionalValues();
        await ChoiceValues();
        await AllEqual();
        await Len();
        await Strlen();
        await TrimValues();
        await CasingValues();
        if (smoke) Heavy("string search, numeric conversion, abs, in-range, is-within, substr values");
        else { await StringSearchValues(); await NumericConversionValues(); await AbsValues(); await InRangeValues(); await IsWithinValues(); await SubstrValues(); }
        if (OperatingSystem.IsMacOS())
        {
            var left = Path.Combine(work, "actual-fifo", "left"); var right = Path.Combine(work, "actual-fifo", "right");
            Directory.CreateDirectory(left); Directory.CreateDirectory(right);
            var fifoInfo = new ProcessStartInfo("/usr/bin/mkfifo") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            fifoInfo.ArgumentList.Add(Path.Combine(left, "fifo"));
            using var fifo = Process.Start(fifoInfo)!; var stdout = fifo.StandardOutput.ReadToEndAsync(); var stderr = fifo.StandardError.ReadToEndAsync();
            await fifo.WaitForExitAsync(); var creation = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, "actual-fifo-create.txt"), creation);
            check("folder actual FIFO creation", fifo.ExitCode == 0, creation);
            if (fifo.ExitCode == 0)
            {
                File.WriteAllText(Path.Combine(right, "fifo"), "regular counterpart");
                var result = await run("folder-property-exclude-actual-fifo", 2, true, ["--directory", left, right, "--exclude", "*"]);
                using var model = JsonDocument.Parse(result.Stdout); var entry = model.RootElement.GetProperty("entries").EnumerateArray().Single();
                check("folder actual FIFO error remains visible when excluded", entry.GetProperty("path").GetString() == "fifo" && entry.GetProperty("status").GetString() == "Error"
                    && !entry.GetProperty("left").GetProperty("filtered").GetBoolean() && entry.GetProperty("right").GetProperty("filtered").GetBoolean(), result.Stdout);
            }
        }
        else skip("folder actual Mac FIFO exclusion", "macOS lstat実FIFOの検証はmacOS実機限定。Windowsではself-testのcontrolled validator injectionを別途実行。");
        if (!OperatingSystem.IsWindows())
        {
            await run("folder-property-unsupported", 2, false, ["--directory", input, input, "--filter", Filter("unsupported", "fe: Prop('System.Size') == 0")]);
            skip("folder properties Windows Shell", "Windows 専用の property service。非Windowsで明示拒否だけを検証。"); return;
        }
        foreach (var line in File.ReadLines(Path.Combine(fixture, "golden.ndjson")))
        {
            using var document = JsonDocument.Parse(line); var row = document.RootElement;
            var id = row.GetProperty("id").GetString()!;
            if (!id.StartsWith("file-", StringComparison.Ordinal) || !row.GetProperty("serviceSuccess").GetBoolean()) continue;
            var marker = id.IndexOf("-mixed-", StringComparison.Ordinal); if (marker < 0) continue;
            var name = id[5..marker]; var property = id[(marker + 7)..]; var value = row.GetProperty("value");
            var literal = value.GetProperty("type").GetString() switch
            {
                "integer" => value.GetProperty("value").GetString()!,
                "timestamp-us" => "d'" + DateTime.UnixEpoch.AddTicks(long.Parse(value.GetProperty("value").GetString()!, CultureInfo.InvariantCulture) * 10).ToString("O", CultureInfo.InvariantCulture) + "'",
                "string" => "'" + value.GetProperty("value").GetString()!.Replace("'", "\\'", StringComparison.Ordinal) + "'",
                _ => throw new InvalidDataException(id)
            };
            var filter = Filter(id, $"def: exclude\nfe: Name == '{name}' and LeftProp('{property}') == {literal}\n");
            var result = await run("folder-property-" + id, 0, true, ["--directory", input, input, "--filter", filter]);
            using var model = JsonDocument.Parse(result.Stdout);
            check("folder property native value " + id, Paths(model).SequenceEqual([name]), result.Stdout);
        }
        var oneByteLeft = Path.Combine(work, "mixed-unknown-one-byte", "left"); var oneByteRight = Path.Combine(work, "mixed-unknown-one-byte", "right");
        foreach (var (id, expression, expectedFiles) in new[]
        {
            ("double-left-string", "1.5 != LeftProp('System.FileName')", 0),
            ("double-left-string-none", "(1.5 != LeftProp('System.FileName')) == None", 4),
            ("string-left-double", "LeftProp('System.FileName') != 1.5", 4),
            ("double-integer", "1.5 != LeftProp('System.Size')", 4),
            ("double-equality", "1.5 == 1.5 and Prop('System.Size') != None", 4),
            ("exponent-left-string", "1e0 != LeftProp('System.FileName')", 0),
            ("large-exponent-none", "(1e100 != LeftProp('System.FileName')) == None", 4),
            ("large-exponent-number", "1e100 > 0 and Prop('System.Size') != None", 4),
            ("native-double-scalar-precision", "0.1000000000000000000000000001 != 0.1", 0),
            ("native-large-double-scalar", "1e100 > 0", 4),
            ("native-mixed-kind-equality", "Name == 1", 0),
            ("native-double-precision", "0.1000000000000000000000000001 != 0.1 and Prop('System.Size') != None", 0),
            ("lazy-false-and", "false and Prop('Hash.SHA256') == ''", 0),
            ("lazy-true-or", "true or Prop('Hash.SHA256') == ''", 4)
        })
        {
            var filter = Filter(id, "def: exclude\nfe: " + expression + "\n");
            var result = await run("folder-property-number-lazy-" + id, 0, true, ["--directory", input, input, "--filter", filter]);
            using var model = JsonDocument.Parse(result.Stdout);
            check("folder property numeric token and short circuit " + id, Paths(model).Length == expectedFiles, result.Stdout);
        }
        foreach (var (id, expression) in new[]
        {
            ("double-overflow", "1e9999 > 0 and Prop('System.Size') != None"),
            ("short-circuit-invalid-name", "false and Prop('Bogus.Property') == None")
        }) await run("folder-property-number-invalid-" + id, 2, false, ["--directory", input, input, "--filter", Filter(id, "fe: " + expression)]);
        Directory.CreateDirectory(oneByteLeft); Directory.CreateDirectory(oneByteRight); File.WriteAllBytes(Path.Combine(oneByteRight, "one.bin"), [1]);
        foreach (var withProperty in new[] { false, true })
        {
            var expression = "((Size > 0) == None)" + (withProperty ? " and Prop('System.Size') != None" : "");
            var filter = Filter("mixed-unknown-one-byte-" + withProperty, "def: exclude\nfe: " + expression + "\n");
            var result = await run("folder-property-mixed-unknown-one-byte-" + withProperty, 1, true,
                ["--directory", oneByteLeft, oneByteRight, "--filter", filter]);
            using var model = JsonDocument.Parse(result.Stdout);
            var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            check("folder property native unknown one-byte independent of Prop " + withProperty, entries.Length == 1 && entries[0].GetProperty("path").GetString() == "one.bin" && entries[0].GetProperty("status").GetString() == "RightOnly"
                    && entries[0].GetProperty("left").ValueKind == JsonValueKind.Null && entries[0].GetProperty("right").GetProperty("size").GetInt64() == 1, result.Stdout);
        }
        foreach (var count in new[] { 2, 3 })
        {
            var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "size-unit-" + count, "side-" + side)).ToArray();
            foreach (var root in roots) Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(roots[0], "fractional-kb.bin"), new byte[1536]);
            var filter = Filter("size-unit-prop-" + count, "def: exclude\nfe: LeftProp('System.Size') == 1.5001KB\n");
            List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter];
            if (count == 3) arguments.AddRange(["--middle", roots[1]]);
            var result = await run("folder-property-size-unit-" + count, 1, true, arguments.ToArray());
            using var model = JsonDocument.Parse(result.Stdout);
            var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            check("folder property size literal uses legacy byte truncation " + count,
                entries.Length == 1 && entries[0].GetProperty("path").GetString() == "fractional-kb.bin"
                    && entries[0].GetProperty("status").GetString() == "LeftOnly", result.Stdout);
            if (count == 2)
            {
                var legacyFilter = Filter("size-unit-without-prop", "def: exclude\nfe: Size == 1.5001KB\n");
                var legacy = await run("folder-size-unit-without-prop", 1, true,
                    ["--directory", roots[0], roots[^1], "--filter", legacyFilter]);
                using var legacyModel = JsonDocument.Parse(legacy.Stdout);
                check("folder size literal uses native Int64 without Prop", Paths(legacyModel).SequenceEqual(["fractional-kb.bin"]), legacy.Stdout);
            }
        }
        foreach (var count in new[] { 2, 3 })
        {
            var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "mask-" + count, "side-" + side)).ToArray();
            foreach (var root in roots) Directory.CreateDirectory(root);
            for (var mask = 1; mask < 1 << count; mask++)
                for (var side = 0; side < count; side++)
                    if ((mask & 1 << side) != 0) File.WriteAllBytes(Path.Combine(roots[side], "mask-" + mask + ".bin"), new byte[(side + 1) * 11]);
            foreach (var function in new[] { "Prop", "LeftProp", "MiddleProp", "RightProp" })
            {
                var index = function switch { "LeftProp" => 0, "MiddleProp" => 1, _ => count - 1 };
                var filter = Filter("mask-" + count + "-" + function, $"def: exclude\nfe: {function}('System.Size') == {(index + 1) * 11}\n");
                List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter];
                if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                var result = await run("folder-property-mask-" + count + "-" + function, 1, true, arguments.ToArray());
                using var model = JsonDocument.Parse(result.Stdout);
                var expected = Enumerable.Range(1, (1 << count) - 1).Where(mask => (mask & 1 << index) != 0).Select(mask => "mask-" + mask + ".bin").Order(StringComparer.Ordinal);
                check("folder properties full presence mask " + count + " " + function, Paths(model).Order(StringComparer.Ordinal).SequenceEqual(expected), result.Stdout);
            }
            foreach (var (id, expression, excluded) in new[]
            {
                ("array-equality", "Size != Prop('System.Size')", Enumerable.Range(1, (1 << count) - 1).ToArray()),
                ("mixed-global", $"(Size == 11) and RightProp('System.Size') == {count * 11}", Enumerable.Range(1, (1 << count) - 1).Where(mask => (mask & 1) != 0 && (mask & 1 << (count - 1)) != 0).ToArray()),
                ("actual-presence", "(Exists == false) and Prop('System.Size') == None", Enumerable.Range(1, (1 << count) - 2).ToArray())
            })
            {
                // array-equalityの!=は常にfalse。def: excludeなら全項目が除外される。
                var filter = Filter(id + "-" + count, $"def: {(id == "array-equality" ? "exclude" : "include")}\nfe: {expression}\n");
                List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                var result = await run("folder-property-" + id + "-" + count, id == "array-equality" ? 0 : 1, true, arguments.ToArray());
                using var model = JsonDocument.Parse(result.Stdout);
                var mixedEntries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                check("folder properties whole-item mixed metadata " + id + " " + count, mixedEntries.All(entry =>
                {
                    var mask = int.Parse(entry.GetProperty("path").GetString()![5..^4], CultureInfo.InvariantCulture);
                    var expectedFiltered = excluded.Contains(mask);
                    return entry.GetProperty("filtered").GetBoolean() == expectedFiltered
                        && new[] { "left", "middle", "right" }.All(side => !entry.TryGetProperty(side, out var state) || state.ValueKind == JsonValueKind.Null || state.GetProperty("filtered").GetBoolean() == expectedFiltered);
                }), result.Stdout);
            }
            foreach (var (id, expression) in new[]
            {
                ("modified-alias", "modified == Prop('System.DateModified')"),
                ("isdirectory-alias", "isdirectory == IsFolder and Prop('System.Size') != None")
            })
            {
                var filter = Filter(id + "-" + count, $"def: exclude\nfe: {expression}\n");
                List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter];
                if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                var result = await run("folder-property-" + id + "-" + count, 1, true, arguments.ToArray());
                using var model = JsonDocument.Parse(result.Stdout);
                check("folder property alias all-side " + id + " " + count, Paths(model).Length == (1 << count) - 1, result.Stdout);
            }
            foreach (var (id, expression, missingOnly) in new[]
            {
                ("not-unknown", "not (LeftProp('System.Size') > 0)", false),
                ("not-unknown-is-none", "(not (LeftProp('System.Size') > 0)) == None", true),
                ("and-left-unknown", "((LeftProp('System.Size') > 0) and false) == None", true),
                ("and-false-short-circuit", "(false and (LeftProp('System.Size') > 0)) == None", false),
                ("or-known-false", "((LeftProp('System.Size') > 0) or false) == None", false),
                ("or-both-unknown", "((LeftProp('System.Size') > 0) or (LeftProp('System.Size') > 0)) == None", true)
            })
            {
                var filter = Filter(id + "-" + count, $"def: exclude\nfe: {expression}\n");
                List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter];
                if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                var result = await run("folder-property-nullable-" + id + "-" + count, missingOnly ? 1 : 0, true, arguments.ToArray());
                using var model = JsonDocument.Parse(result.Stdout);
                var expected = missingOnly ? Enumerable.Range(1, (1 << count) - 1).Where(mask => (mask & 1) == 0).Select(mask => "mask-" + mask + ".bin").ToArray() : [];
                check("folder property nullable logic " + id + " " + count, Paths(model).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)), result.Stdout);
            }
            foreach (var (id, comparison) in new[]
            {
                ("size-lt", "Size < 0"), ("size-le", "Size <= 0"), ("size-gt", "Size > 0"), ("size-ge", "Size >= 0"),
                ("modified", "Modified > d'2000-01-01'"), ("name", "Name > 'a'"), ("contains", "Name contains 'mask-'")
            })
            {
                var filter = Filter("mixed-native-unknown-" + id + "-" + count, "def: exclude\nfe: ((" + comparison + ") == None) and Prop('System.Size') != None\n");
                List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter];
                if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                var result = await run("folder-property-mixed-native-unknown-" + id + "-" + count, 1, true, arguments.ToArray());
                using var model = JsonDocument.Parse(result.Stdout);
                var expected = Enumerable.Range(1, (1 << count) - 2).Select(mask => "mask-" + mask + ".bin").Order(StringComparer.Ordinal);
                check("folder property global metadata unknown " + id + " " + count, Paths(model).Order(StringComparer.Ordinal).SequenceEqual(expected), result.Stdout);
            }
            Directory.CreateDirectory(Path.Combine(roots[^1], "blocked", "deep"));
            File.WriteAllText(Path.Combine(roots[^1], "blocked", "deep", "never.bin"), "must not scan");
            var prune = Filter("prune-" + count, "def: include\nde: LeftProp('System.FileName') == None\nfe: Prop('System.Size') == 11\n");
            List<string> pruneArgs = ["--directory", roots[0], roots[^1], "--filter", prune, "--show-filtered"];
            if (count == 3) pruneArgs.AddRange(["--middle", roots[1]]);
            var pruned = await run("folder-property-prune-" + count, 1, true, pruneArgs.ToArray());
            using var prunedModel = JsonDocument.Parse(pruned.Stdout);
            var entries = prunedModel.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            check("folder property global directory pruning " + count,
                entries.Any(entry => entry.GetProperty("path").GetString() == "blocked" && entry.GetProperty("filtered").GetBoolean())
                && !entries.Any(entry => entry.GetProperty("path").GetString()!.StartsWith("blocked/", StringComparison.Ordinal)), pruned.Stdout);
        }
        foreach (var expression in new[] { "Prop()", "Prop('System.Size', 'System.FileName')", "Prop(Name)", "Prop('Bogus.Property')", "Prop('system.size')", "Prop('hash.sha256')" })
            await run("folder-property-invalid-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expression)))[..8], 2, false,
                ["--directory", input, input, "--filter", Filter("invalid", "fe: " + expression)]);
        foreach (var count in new[] { 2, 3 })
        {
            var filter = Filter("exclude-all-" + count, "def: include\nfe: Prop('Hash.SHA256') == '' or Prop('System.FileName') == ''\n");
            List<string> arguments = ["--directory", input, input, "--filter", filter, "--exclude", "*", "--show-filtered"];
            if (count == 3) arguments.AddRange(["--middle", input]);
            var result = await run("folder-property-exclude-all-" + count, 0, true, arguments.ToArray());
            using var model = JsonDocument.Parse(result.Stdout);
            var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            check("folder properties exclude preserves all actual sides " + count, entries.Length == 4 && entries.All(entry => entry.GetProperty("filtered").GetBoolean()
                && new[] { "left", "middle", "right" }.All(side => !entry.TryGetProperty(side, out var state) || state.GetProperty("filtered").GetBoolean())), result.Stdout);
        }
        var caseRoots = Enumerable.Range(0, 3).Select(index => Path.Combine(work, "case-sensitive", "side-" + index)).ToArray();
        var caseSensitive = true;
        for (var index = 0; index < caseRoots.Length; index++)
        {
            Directory.CreateDirectory(caseRoots[index]);
            var fsutilInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "fsutil.exe"))
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "file", "setCaseSensitiveInfo", caseRoots[index], "enable" }) fsutilInfo.ArgumentList.Add(argument);
            using var fsutil = Process.Start(fsutilInfo)!; var stdout = fsutil.StandardOutput.ReadToEndAsync(); var stderr = fsutil.StandardError.ReadToEndAsync();
            await fsutil.WaitForExitAsync(); var evidence = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, "case-sensitive-enable-" + index + ".txt"), "exit=" + fsutil.ExitCode + "\n" + evidence);
            if (fsutil.ExitCode != 0) { caseSensitive = false; skip("folder property actual Windows case-sensitive directory", "fsutilで専用fixture directoryを有効化できませんでした。代替ケースを同等扱いしません。" + evidence); break; }
            var probe = Path.Combine(caseRoots[index], "probe"); Directory.CreateDirectory(probe);
            try
            {
                using (var stream = new FileStream(Path.Combine(probe, "CaseProbe"), FileMode.CreateNew, FileAccess.Write)) stream.WriteByte(1);
                using (var stream = new FileStream(Path.Combine(probe, "caseprobe"), FileMode.CreateNew, FileAccess.Write)) stream.WriteByte(2);
                check("folder property actual case-sensitive distinct files " + index, Directory.GetFiles(probe).Length == 2, probe);
            }
            catch (IOException error) { caseSensitive = false; skip("folder property actual Windows case-sensitive distinct files", error.Message); break; }
        }
        if (caseSensitive)
        {
            string[] folders = ["Top/Inner", "TOP/INNER", "top/inner"]; string[] names = ["Foo.txt", "FOO.txt", "foo.txt"];
            for (var index = 0; index < caseRoots.Length; index++)
            { var folder = Path.Combine(caseRoots[index], folders[index]); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, names[index]), "abc", new UTF8Encoding(false)); }
            foreach (var count in new[] { 2, 3 })
                foreach (var (id, expression) in new[]
                {
                    ("Name", "Name == RightProp('System.FileName')"),
                    ("Folder", "Folder == 'top/inner' and Prop('System.Size') == 3"),
                    ("RelPath", "RelPath == 'top/inner/foo.txt' and Prop('System.Size') == 3"),
                    ("PrefixedRelPaths", "LeftRelPath == 'Top/Inner/Foo.txt' and MiddleRelPath == '" + (count == 3 ? "TOP/INNER/FOO.txt" : "top/inner/foo.txt") + "' and RightRelPath == 'top/inner/foo.txt'"),
                    ("PrefixedNames", "LeftName == 'Foo.txt' and MiddleName == '" + (count == 3 ? "FOO.txt" : "foo.txt") + "' and RightName == 'foo.txt'"),
                    ("PrefixedBaseNames", "LeftBaseName == 'Foo' and MiddleBaseName == '" + (count == 3 ? "FOO" : "foo") + "' and RightBaseName == 'foo'"),
                    ("PrefixedFolders", "LeftFolder == 'Top/Inner' and MiddleFolder == '" + (count == 3 ? "TOP/INNER" : "top/inner") + "' and RightFolder == 'top/inner'")
                })
                {
                    var filter = Filter("case-sensitive-" + id + "-" + count, "def: exclude\nde: true\nfe: @cs " + expression + "\n");
                    List<string> arguments = ["--directory", caseRoots[0], caseRoots[^1], "--filter", filter, "--exclude", "probe"];
                    if (count == 3) arguments.AddRange(["--middle", caseRoots[1]]);
                    var result = await run("folder-property-case-sensitive-" + id + "-" + count, 0, true, arguments.ToArray());
                    using var model = JsonDocument.Parse(result.Stdout);
                    var files = model.RootElement.GetProperty("entries").EnumerateArray().Where(entry => entry.GetProperty("kind").GetString() == "File").ToArray();
                    check("folder property actual side-specific " + id + " " + count, files.Length == 1
                        && files[0].GetProperty("left").GetProperty("path").GetString()!.EndsWith(Path.Combine("Top", "Inner", "Foo.txt"), StringComparison.Ordinal)
                        && files[0].GetProperty("right").GetProperty("path").GetString()!.EndsWith(Path.Combine("top", "inner", "foo.txt"), StringComparison.Ordinal), result.Stdout);
                }
            foreach (var count in new[] { 2, 3 })
            {
                var filter = Filter("case-bare-" + count, "def: exclude\nde: true\nfe: @cs LeftExists and Name == 'Foo.txt'\n");
                List<string> arguments = ["--directory", caseRoots[0], caseRoots[^1], "--filter", filter, "--exclude", "probe"];
                if (count == 3) arguments.AddRange(["--middle", caseRoots[1]]);
                var result = await run("folder-side-case-bare-" + count, 0, true, arguments.ToArray());
                using var model = JsonDocument.Parse(result.Stdout);
                var files = model.RootElement.GetProperty("entries").EnumerateArray().Where(entry => entry.GetProperty("kind").GetString() == "File").ToArray();
                check("folder side bare Name uses global native array " + count, files.Length == 1 && new[] { "left", "middle", "right" }.All(side =>
                    !files[0].TryGetProperty(side, out var state) || state.ValueKind == JsonValueKind.Null || !state.GetProperty("filtered").GetBoolean()), result.Stdout);
            }
            File.WriteAllText(Path.Combine(caseRoots[^1], "top", "inner", "right-only.txt"), "absent on left");
            foreach (var count in new[] { 2, 3 })
            {
                var filter = Filter("case-missing-folder-" + count, "def: exclude\nde: true\nfe: @cs not LeftExists and LeftFolder == 'Top/Inner' and RightFolder == 'top/inner'\n");
                List<string> arguments = ["--directory", caseRoots[0], caseRoots[^1], "--filter", filter, "--exclude", "probe"];
                if (count == 3) arguments.AddRange(["--middle", caseRoots[1]]);
                var result = await run("folder-side-case-missing-folder-" + count, 1, true, arguments.ToArray());
                using var model = JsonDocument.Parse(result.Stdout);
                var files = model.RootElement.GetProperty("entries").EnumerateArray().Where(entry => entry.GetProperty("kind").GetString() == "File").ToArray();
                check("folder side missing LeftFolder uses actual parent casing " + count, files.Length == 1
                    && files[0].GetProperty("path").GetString()!.EndsWith("right-only.txt", StringComparison.Ordinal)
                    && files[0].GetProperty("left").ValueKind == JsonValueKind.Null && !files[0].GetProperty("right").GetProperty("filtered").GetBoolean(), result.Stdout);
            }
        }

        async Task SideAttributes()
        {
            foreach (var count in new[] { 2, 3 })
            {
                var numericRoots = Enumerable.Range(0, count).Select(index => Path.Combine(work, "side-numbers-" + count, "side-" + index)).ToArray();
                for (var index = 0; index < count; index++)
                {
                    Directory.CreateDirectory(numericRoots[index]);
                    File.WriteAllBytes(Path.Combine(numericRoots[index], "one.bin"), [(byte)(index + 1)]);
                }
                foreach (var (id, expression, included) in new[]
                {
                    ("left-double", "LeftSize == 1.0000000000000001", true),
                    ("middle-double", "MiddleSize == 1.0000000000000001", true),
                    ("right-double", "RightSize == 1.0000000000000001", true),
                    ("double-left", "1.0000000000000001 == LeftSize", true),
                    ("left-unit", "LeftSize == 0.001kb", true),
                    ("middle-unit", "MiddleSize == 0.001kb", true),
                    ("right-unit", "RightSize == 0.001kb", true),
                    ("bare-double", "Size == 1.0000000000000001", true),
                    ("bare-unit", "Size == 0.001kb", true),
                    ("mixed-double", "LeftExists and Size == 1.0000000000000001", true),
                    ("mixed-literals", "LeftExists and 0.1000000000000000000000000001 == 0.1", true),
                    ("large-double", "LeftSize < 1e100", true),
                    ("separate-rules", "LeftExists and false\nfe: Size == 1.0000000000000001", true)
                })
                {
                    var filter = Filter("side-number-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n");
                    List<string> arguments = ["--directory", numericRoots[0], numericRoots[^1], "--filter", filter];
                    if (count == 3) arguments.AddRange(["--middle", numericRoots[1]]);
                    var result = await run("folder-side-number-" + count + "-" + id, included ? 1 : 0, true, arguments.ToArray());
                    using var model = JsonDocument.Parse(result.Stdout);
                    check("folder side native number " + count + " " + id, Paths(model).SequenceEqual(included ? ["one.bin"] : System.Array.Empty<string>()), result.Stdout);
                }
                var roots = Enumerable.Range(0, count).Select(index => Path.Combine(work, "side-attributes-" + count, "side-" + index)).ToArray();
                var date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                for (var side = 0; side < count; side++)
                {
                    Directory.CreateDirectory(Path.Combine(roots[side], "nested"));
                    for (var mask = 1; mask < 1 << count; mask++)
                    {
                        if ((mask & 1 << side) == 0) continue;
                        var path = Path.Combine(roots[side], "nested", "mask-" + mask + ".txt");
                        File.WriteAllBytes(path, Enumerable.Repeat((byte)(side + 1), (side + 1) * 11).ToArray());
                        File.SetLastWriteTimeUtc(path, date.AddDays(side));
                    }
                }
                async Task<JsonDocument> Compare(string id, string expression, int exit)
                {
                    var filter = Filter("side-" + count + "-" + id, "def: exclude\nde: true\nfe: " + expression + "\n");
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-side-" + count + "-" + id, exit, true, arguments.ToArray());
                    return JsonDocument.Parse(result.Stdout);
                }
                foreach (var prefix in new[] { "Left", "Middle", "Right" })
                {
                    var index = prefix switch { "Left" => 0, "Middle" => 1, _ => count - 1 };
                    foreach (var (id, expression, missing) in new[]
                    {
                        ("exists", prefix + "Exists", false),
                        ("missing-exists", "not " + prefix + "Exists", true),
                        ("size", prefix + "Size == " + (index + 1) * 11, false),
                        ("relpath", prefix + "RelPath != None", false),
                        ("missing-relpath", prefix + "RelPath == None", true),
                        ("name", prefix + "Name != None", false),
                        ("basename", prefix + "BaseName != None", false),
                        ("extension", prefix + "Extension == 'txt'", false),
                        ("attributes", prefix + "Attributes != None", false),
                        ("attrstr", prefix + "AttrStr != None", false),
                        ("missing-attributes", prefix + "Attributes == None", true),
                        ("missing-attrstr", prefix + "AttrStr == None", true),
                        ("date", prefix + "Date == d'" + date.AddDays(index).ToString("O", CultureInfo.InvariantCulture) + "'", false),
                        ("none", prefix + "Size == None", true),
                        ("unknown-not", "(not (" + prefix + "Size > 0)) == None", true),
                        ("unknown-and", "((" + prefix + "Size > 0) and false) == None", true),
                        ("missing-folder", prefix + "Folder == 'nested' and not " + prefix + "Exists", true)
                    })
                    {
                        using var model = await Compare(prefix + "-" + id, expression, 1);
                        var files = model.RootElement.GetProperty("entries").EnumerateArray().Where(entry => entry.GetProperty("kind").GetString() == "File").ToArray();
                        check("folder side attribute masks " + count + " " + prefix + " " + id,
                            files.Length == (1 << count) - 1 && files.All(entry =>
                            {
                                var mask = int.Parse(Path.GetFileNameWithoutExtension(entry.GetProperty("path").GetString()!)[5..], CultureInfo.InvariantCulture);
                                var included = ((mask & 1 << index) == 0) == missing;
                                return entry.GetProperty("filtered").GetBoolean() == !included
                                    && new[] { "left", "middle", "right" }.All(side => !entry.TryGetProperty(side, out var state) || state.ValueKind == JsonValueKind.Null || state.GetProperty("filtered").GetBoolean() == !included);
                            }), model.RootElement.GetRawText());
                    }
                }
                foreach (var prefix in new[] { "Left", "Middle", "Right" })
                {
                    var index = prefix switch { "Left" => 0, "Middle" => 1, _ => count - 1 };
                    var expectedPath = "nested/mask-" + (1 << index).ToString(CultureInfo.InvariantCulture) + ".txt";
                    using var model = await Compare("exact-relpath-" + prefix, prefix + "RelPath == '" + expectedPath + "'", 1);
                    var files = model.RootElement.GetProperty("entries").EnumerateArray().Where(entry => entry.GetProperty("kind").GetString() == "File").ToArray();
                    check("folder side exact RelPath " + count + " " + prefix, files.Length == (1 << count) - 1 && files.All(entry =>
                    {
                        var mask = int.Parse(Path.GetFileNameWithoutExtension(entry.GetProperty("path").GetString()!)[5..], CultureInfo.InvariantCulture);
                        var included = mask == (1 << index);
                        return entry.GetProperty("filtered").GetBoolean() == !included
                            && new[] { "left", "middle", "right" }.All(side => !entry.TryGetProperty(side, out var state) || state.ValueKind == JsonValueKind.Null || state.GetProperty("filtered").GetBoolean() == !included);
                    }), model.RootElement.GetRawText());
                }
                foreach (var (id, expression) in new[] { ("Size", "Size == 11"), ("Date", "Date == d'2024-01-01T00:00:00Z'"), ("MixedSize", "LeftExists and Size == 11"), ("MixedDate", "LeftExists and Date == d'2024-01-01T00:00:00Z'") })
                {
                    using var model = await Compare("bare-" + id, expression, 1);
                    check("folder side bare uses global native array " + count + " " + id, model.RootElement.GetProperty("entries").EnumerateArray()
                        .Where(entry => entry.GetProperty("kind").GetString() == "File").All(entry =>
                        {
                            var mask = int.Parse(Path.GetFileNameWithoutExtension(entry.GetProperty("path").GetString()!)[5..], CultureInfo.InvariantCulture);
                            var filtered = (mask & 1) == 0;
                            return entry.GetProperty("filtered").GetBoolean() == filtered && new[] { "left", "middle", "right" }.All(side =>
                                !entry.TryGetProperty(side, out var state) || state.ValueKind == JsonValueKind.Null || state.GetProperty("filtered").GetBoolean() == filtered);
                        }), model.RootElement.GetRawText());
                }
                foreach (var side in roots)
                {
                    Directory.CreateDirectory(Path.Combine(side, "blocked", "deep"));
                    File.WriteAllText(Path.Combine(side, "blocked", "deep", "never.txt"), "pruned");
                }
                var prune = Filter("side-prune-" + count, "def: include\nde: LeftName == 'blocked'\n");
                List<string> pruneArguments = ["--directory", roots[0], roots[^1], "--filter", prune, "--show-filtered"];
                if (count == 3) pruneArguments.AddRange(["--middle", roots[1]]);
                var pruned = await run("folder-side-prune-" + count, 1, true, pruneArguments.ToArray());
                using var prunedModel = JsonDocument.Parse(pruned.Stdout);
                check("folder side attribute directory prune " + count, prunedModel.RootElement.GetProperty("entries").EnumerateArray().Any(entry => entry.GetProperty("path").GetString() == "blocked" && entry.GetProperty("filtered").GetBoolean())
                    && !Paths(prunedModel).Any(path => path.StartsWith("blocked/", StringComparison.Ordinal)), pruned.Stdout);
                var relPathPrune = Filter("side-prune-relpath-" + count, "def: include\nde: LeftRelPath == 'blocked'\n");
                List<string> relPathPruneArguments = ["--directory", roots[0], roots[^1], "--filter", relPathPrune, "--show-filtered"];
                if (count == 3) relPathPruneArguments.AddRange(["--middle", roots[1]]);
                var relPathPruned = await run("folder-side-relpath-directory-prune-" + count, 1, true, relPathPruneArguments.ToArray());
                using var relPathPrunedModel = JsonDocument.Parse(relPathPruned.Stdout);
                check("folder side RelPath directory prune " + count, relPathPrunedModel.RootElement.GetProperty("entries").EnumerateArray().Any(entry => entry.GetProperty("path").GetString() == "blocked" && entry.GetProperty("filtered").GetBoolean())
                    && !Paths(relPathPrunedModel).Any(path => path.StartsWith("blocked/", StringComparison.Ordinal)), relPathPruned.Stdout);
                // 側prefix付きDateStrは対応済み（Docs/MIGRATION.md フォルダー式の最適化・全側評価）なので無効一覧に含めない。
                foreach (var attribute in new[] { "Modified", "Path", "IsDirectory", "Prop" })
                    await run("folder-side-invalid-" + count + "-" + attribute, 2, false, ["--directory", roots[0], roots[^1], "--filter", Filter("side-invalid", "fe: Left" + attribute + " == None")]);
            }
        }
        async Task FullPaths()
        {
            string Literal(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal) + "'";
            bool AllPresentSidesHaveFiltered(JsonElement entry, int expectedSideCount, bool expected)
            {
                var names = expectedSideCount == 1 ? new[] { "left", "middle", "right" }
                    : expectedSideCount == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                var found = 0;
                foreach (var name in names)
                {
                    if (!entry.TryGetProperty(name, out var side) || side.ValueKind == JsonValueKind.Null) continue;
                    found++;
                    if (side.GetProperty("filtered").GetBoolean() != expected) return false;
                }
                return found == expectedSideCount;
            }
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "full-path-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                    File.WriteAllBytes(Path.Combine(roots[side], "shared.bin"), new byte[(side + 1) * 11]);
                File.WriteAllText(Path.Combine(roots[0], "left-only.bin"), "left");
                File.WriteAllText(Path.Combine(roots[^1], "right-only.bin"), "right");
                var leftShared = Literal(Path.GetFullPath(Path.Combine(roots[0], "shared.bin")));
                var rightShared = Literal(Path.GetFullPath(Path.Combine(roots[^1], "shared.bin")));

                async Task<JsonDocument> Compare(string id, string expression, int expectedExit)
                {
                    var filter = Filter("full-path-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n");
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-full-path-" + count + "-" + id, expectedExit, true, arguments.ToArray());
                    return JsonDocument.Parse(result.Stdout);
                }
                JsonElement Entry(JsonDocument model, string path) => model.RootElement.GetProperty("entries").EnumerateArray()
                    .Single(entry => entry.GetProperty("path").GetString() == path);

                using (var model = await Compare("bare-left-path", "FullPath == " + leftShared, 1))
                {
                    var shared = Entry(model, "shared.bin");
                    check("folder FullPath bare value uses native absolute path " + count,
                        AllPresentSidesHaveFiltered(shared, count, false), model.RootElement.ToString());
                }
                using (var model = await Compare("right-prefixed-path", "RightFullPath == " + rightShared, 1))
                {
                    var shared = Entry(model, "shared.bin");
                    check("folder RightFullPath selects the right root " + count,
                        AllPresentSidesHaveFiltered(shared, count, false), model.RootElement.ToString());
                }
                using (var model = await Compare("middle-index", "MiddleFullPath == RightFullPath", 1))
                {
                    var shared = Entry(model, "shared.bin");
                    check("folder FullPath middle uses pane index one " + count,
                        AllPresentSidesHaveFiltered(shared, count, count != 2), model.RootElement.ToString());
                }
                using (var model = await Compare("missing-left-null", "FullPath == LeftFullPath", 1))
                {
                    var rightOnly = Entry(model, "right-only.bin");
                    check("folder FullPath preserves missing-left null equality " + count,
                        rightOnly.GetProperty("left").ValueKind == JsonValueKind.Null
                            && AllPresentSidesHaveFiltered(rightOnly, 1, false), model.RootElement.ToString());
                }
                using (var model = await Compare("crossed-side-and", "FullPath == " + leftShared + " and Size == " + (count * 11), 1))
                {
                    var shared = Entry(model, "shared.bin");
                    check("folder FullPath shares a mixed all-side predicate " + count,
                        AllPresentSidesHaveFiltered(shared, count, false), model.RootElement.ToString());
                }
                var separateFilter = Filter("full-path-" + count + "-separate-rules",
                    "def: exclude\nfe: FullPath == " + Literal(Path.GetFullPath(Path.Combine(roots[0], "not-present.bin")))
                    + "\nfe: Size == 11\n");
                List<string> separateArguments = ["--directory", roots[0], roots[^1], "--filter", separateFilter, "--show-filtered"];
                if (count == 3) separateArguments.AddRange(["--middle", roots[1]]);
                var separate = await run("folder-full-path-" + count + "-separate-rules", 1, true, separateArguments.ToArray());
                using (var model = JsonDocument.Parse(separate.Stdout))
                {
                    // 条件式は全側DIFFITEMで一度だけ評価する（Docs/MIGRATION.md フォルダー式の最適化・全側評価）。
                    // shared.binは左11・右22なので Size == 11 は [true,false] となり、原典ContainsTrueで一致として全側が同じ結果になる。
                    var shared = Entry(model, "shared.bin");
                    check("folder FullPath and Size rules share one all-side evaluation " + count,
                        AllPresentSidesHaveFiltered(shared, count, false), model.RootElement.ToString());
                }
                var blocked = Path.Combine(roots[^1], "blocked");
                Directory.CreateDirectory(Path.Combine(blocked, "deep"));
                File.WriteAllText(Path.Combine(blocked, "deep", "never.bin"), "must not scan");
                var pruneFilter = Filter("full-path-" + count + "-directory-prune",
                    "def: include\nde: FullPath == " + Literal(Path.GetFullPath(blocked)) + "\n");
                List<string> pruneArguments = ["--directory", roots[0], roots[^1], "--filter", pruneFilter, "--show-filtered"];
                if (count == 3) pruneArguments.AddRange(["--middle", roots[1]]);
                var pruned = await run("folder-full-path-" + count + "-directory-prune", 1, true, pruneArguments.ToArray());
                using (var model = JsonDocument.Parse(pruned.Stdout))
                {
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    var blockedEntry = entries.Single(entry => entry.GetProperty("path").GetString() == "blocked");
                    check("folder FullPath excludes and prunes a matching directory " + count,
                        blockedEntry.GetProperty("filtered").GetBoolean()
                            && !entries.Any(entry => entry.GetProperty("path").GetString()!.StartsWith("blocked/", StringComparison.Ordinal)),
                        model.RootElement.ToString());
                }
            }
        }
        async Task StringSearchValues()
        {
            // 失敗行列はREADMEのStrCount/StrFind節。native bodyのtraceと公開parser/scannerの証拠を分ける。
            var oracle = Path.GetFullPath("tests/Fixtures/FolderStrSearchOracle");
            var reader = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(oracle, "verify.py") }) reader.ArgumentList.Add(argument);
            using (var process = Process.Start(reader)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var evidence = await stdout + await stderr;
                await File.WriteAllTextAsync(Path.Combine(work, "independent-strsearch-oracle.json"), evidence);
                check("folder StrSearch independent native oracle", process.ExitCode == 0, evidence);
            }
            using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(oracle, "cases.json")));
            var golden = File.ReadLines(Path.Combine(oracle, "golden.ndjson")).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .ToDictionary(row => row.GetProperty("id").GetString()!);
            static string Quote(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            static string Literal(JsonElement value)
            {
                var kind = value.GetProperty("type").GetString();
                if (kind == "none") return "None";
                if (kind == "array") return "Array(" + string.Join(",", value.GetProperty("items").EnumerateArray().Select(Literal)) + ")";
                var item = value.GetProperty("value");
                if (kind == "string") return Quote(Encoding.UTF8.GetString(Convert.FromHexString(item.GetString()!)));
                if (kind is "int64" or "bool") return item.GetRawText();
                if (kind == "double")
                {
                    var number = item.GetRawText();
                    return number.Contains('.') || number.Contains('e') || number.Contains('E') ? number : number + ".0";
                }
                throw new InvalidDataException("Unprojected native StrSearch type: " + kind);
            }
            var projections = new List<object>();
            var bodyOnly = new List<object>();
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "strsearch-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3]);
                    File.WriteAllBytes(Path.Combine(roots[side], "different.bin"), [(byte)(side + 1)]);
                    if (side != count - 1) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [4]);
                }
                var hashes = roots.SelectMany(root => Directory.GetFiles(root)).ToDictionary(path => path,
                    path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                async Task Compare(string id, string expression, Func<string, bool>? included = null)
                {
                    included ??= _ => true;
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter",
                        Filter("strsearch-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"), "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var exit = included("different.bin") || included("missing.bin") ? 1 : 0;
                    var result = await run("folder-strsearch-" + count + "-" + id, exit, true, arguments.ToArray());
                    if (result.ExitCode != exit) return;
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder StrSearch " + count + " " + id, entries.Length == 3 && entries.All(entry =>
                    {
                        var filtered = !included(entry.GetProperty("path").GetString()!);
                        return entry.GetProperty("filtered").GetBoolean() == filtered
                            && (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name =>
                                !entry.TryGetProperty(name, out var side) || side.ValueKind == JsonValueKind.Null || side.GetProperty("filtered").GetBoolean() == filtered);
                    }), result.Stdout);
                }
                if (count == 2)
                {
                    foreach (var row in plan.RootElement.EnumerateArray())
                    {
                        var id = row.GetProperty("id").GetString()!;
                        var function = row.GetProperty("function").GetString()!;
                        var arguments = row.GetProperty("args").EnumerateArray().ToArray();
                        var invalidArity = arguments.Length < 2 || arguments.Length > (function == "StrFind" ? 3 : 2);
                        if (row.GetProperty("nullArgs").GetBoolean() || invalidArity || row.GetProperty("throws").GetArrayLength() != 0)
                        {
                            bodyOnly.Add(new { id, reason = "Direct-body null args / invalid arity / throw node cannot be projected as public typed .flt evaluation; native trace verified by reader. Public arity is tested separately." });
                            continue;
                        }
                        var expected = golden[id];
                        check("folder StrSearch oracle status " + id, expected.GetProperty("status").GetString() == "ok", expected.GetRawText());
                        var expression = (row.GetProperty("caseSensitive").GetBoolean() ? "@cs " : "@ci ")
                            + "AllEqual(Array(" + function + "(" + string.Join(",", arguments.Select(Literal)) + ")," + Literal(expected.GetProperty("result")) + "))";
                        await Compare("oracle-" + id, expression);
                        projections.Add(new { id, expression, expected = expected.GetProperty("result").Clone() });
                    }
                }
                await Compare("metadata", "AllOf(StrCount(Name,'.') == 1) and StrFind(LeftName,'.') >= 0", path => path != "missing.bin");
                await Compare("fullpath", "AllOf(StrCount(FullPath,'same.bin') == 1) and StrFind(LeftFullPath,'same.bin') >= 0", path => path == "same.bin");
                await Compare("needle-context", "StrCount('same.bin',LeftName) == 1", path => path == "same.bin");
                await Compare("occurrence-context", "StrFind('xxx','x',LeftSize) == None", path => path == "same.bin");
                await Compare("missing", "StrCount(RightName,'.') == None and StrFind(RightFullPath,'x') == None", path => path == "missing.bin");
                if (count == 3) await Compare("middle", "StrCount(MiddleName,'same') == 1 and StrFind(MiddleFullPath,'same.bin') >= 0", path => path == "same.bin");
                await Compare("case-name", "@cs STRCOUNT('AaA','a') == 1 and sTrFiNd('AaA','a') == 1");
                await Compare("case-ci", "@ci StrCount('AaA','a') == 3 and StrFind('AaA','a',1) == 1");
                await Compare("timestamp-occurrence", "StrFind('xxx','x',d'2024-01-01T00:00:00Z') == None");
                if (OperatingSystem.IsWindows())
                {
                    await Compare("property", "AllOf(StrCount(Prop('System.Size'),'3') == 1)", path => path == "same.bin");
                    await Compare("side-property", "StrFind(LeftProp('System.Size'),'3') == 0", path => path == "same.bin");
                    await Compare("needle-property", "StrCount('3',LeftProp('System.Size')) == None");
                    await Compare("occurrence-property", "StrFind('xxx','x',LeftProp('System.Size')) == None", path => path == "same.bin");
                    if (count == 3) await Compare("middle-property", "StrCount(MiddleProp('System.Size'),'3') == 1", path => path == "same.bin");
                }
                else skip("folder StrSearch property " + count, "Shell property is Windows only.");
                if (count == 2)
                {
                    var needle = new string('a', 6000) + "b";
                    // 公開式の16KiB上限内で8KiB sourceと6KiB needleを使い、長い共通prefixを繰り返す。
                    var longRoots = new[] { Path.Combine(work, "strsearch-long-left"), Path.Combine(work, "strsearch-long-right") };
                    foreach (var root in longRoots) { Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "same.bin"), [1]); }
                    foreach (var function in new[] { "StrCount", "StrFind" })
                    {
                        var watch = Stopwatch.StartNew();
                        var expression = "@cs " + function + "(" + Quote(new string('a', 8000) + "b") + "," + Quote(needle) + ") == " + (function == "StrCount" ? "1" : "2000");
                        var result = await run("folder-strsearch-long-prefix-" + function, 0, true, ["--directory", longRoots[0], longRoots[1], "--filter", Filter("strsearch-long-prefix-" + function, "def: exclude\nfe: " + expression + "\n"), "--show-filtered"]);
                        watch.Stop();
                        using var model = JsonDocument.Parse(result.Stdout);
                        check("folder StrSearch long common prefix " + function, result.ExitCode == 0 && !model.RootElement.GetProperty("entries")[0].GetProperty("filtered").GetBoolean(),
                            "KMP sourceBytes=8001 needleBytes=6001; elapsedMs=" + watch.ElapsedMilliseconds + "; CLI timeout remains finite; measured case is not an asymptotic proof.");
                    }
                    foreach (var function in new[] { "StrCount", "StrFind" })
                    foreach (var (id, expressionText) in new[] { ("zero", function + "()"), ("one", function + "('a')"), ("four", function + "('a','a',0,0)"),
                        ("leading", function + "(,'a')"), ("trailing", function + "('a','a',)"), ("unclosed", function + "('a','a'"), ("bare", function), ("separator", function + "('a' 'a')") })
                    {
                        var filter = Filter("strsearch-invalid-" + function + "-" + id, "fe: " + expressionText + "\n");
                        var filterHash = SHA256.HashData(File.ReadAllBytes(filter));
                        var invalid = await run("folder-strsearch-invalid-" + function + "-" + id, 2, false, ["--directory", roots[0], roots[^1], "--filter", filter]);
                        check("folder StrSearch invalid stdout " + function + " " + id, invalid.Stdout.Length == 0 && filterHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(filter))), invalid.Stdout + invalid.Stderr);
                        var sync = await run("folder-strsearch-invalid-sync-" + function + "-" + id, 2, false,
                            ["--folder-sync", roots[0], roots[^1], "--direction", "left-to-right", "--copy", "all", "--select", "different.bin", "--filter", filter]);
                        check("folder StrSearch invalid output preserved " + function + " " + id, sync.Stdout.Length == 0
                            && hashes.All(pair => pair.Value == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key)))) , sync.Stdout + sync.Stderr);
                    }
                    var three = await run("folder-strsearch-invalid-StrCount-three", 2, false, ["--directory", roots[0], roots[^1], "--filter", Filter("strsearch-invalid-three", "fe: StrCount('a','a',0)\n")]);
                    check("folder StrSearch invalid StrCount three stdout", three.Stdout.Length == 0, three.Stdout + three.Stderr);
                }
                check("folder StrSearch input hashes " + count, hashes.All(pair => File.Exists(pair.Key)
                    && pair.Value == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key)))) , "All source and existing destination bytes retained.");
            }
            await File.WriteAllTextAsync(Path.Combine(work, "strsearch-projections.json"), JsonSerializer.Serialize(new { projections, bodyOnly,
                evidenceBoundary = "Default reader verifies portable fixed native function-body values and evaluation trace. Public CLI projections verify parser/scanner typed results, not spy evaluation trace or native compilation receipts." }, new JsonSerializerOptions { WriteIndented = true }));
            check("folder StrSearch oracle projection coverage", projections.Count + bodyOnly.Count == golden.Count, "projected=" + projections.Count + "; native-body-only=" + bodyOnly.Count + "; golden=" + golden.Count);
        }

        async Task SubstrValues()
        {
            var oracle = Path.GetFullPath("tests/Fixtures/FolderSubstrOracle");
            var observers = Path.GetFullPath("tests/Fixtures/FolderStringByteObserversOracle");
            foreach (var (id, directory) in new[] { ("substr", oracle), ("string-bytes", observers), ("string-byte-consumers", Path.Combine(observers, "consumers")), ("string-byte-glob", Path.Combine(observers, "glob")) })
            {
                var reader = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(directory, "verify.py") }) reader.ArgumentList.Add(argument);
                using var process = Process.Start(reader)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var evidence = await stdout; var errors = await stderr;
                await File.WriteAllTextAsync(Path.Combine(work, "independent-" + id + "-oracle.json"), evidence);
                await File.WriteAllTextAsync(Path.Combine(work, "independent-" + id + "-oracle.stderr.txt"), errors);
                check("folder Substr independent " + id + " oracle", process.ExitCode == 0, evidence + errors);
            }
            var strict = new UTF8Encoding(false, true);
            static string Quote(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            string? LiteralBytes(string hex)
            {
                try { return Quote(strict.GetString(Convert.FromHexString(hex))); }
                catch (DecoderFallbackException) { return null; }
            }
            var slices = new Dictionary<string, string>(StringComparer.Ordinal);
            using var substrPlan = JsonDocument.Parse(File.ReadAllText(Path.Combine(oracle, "cases.json")));
            var substrGolden = File.ReadLines(Path.Combine(oracle, "golden.ndjson")).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToDictionary(row => row.GetProperty("id").GetString()!);
            // 有効な公開literalからのsliceだけをbyte bindingの代替にする。任意byteは注入しない。
            foreach (var row in substrPlan.RootElement.EnumerateArray())
            {
                var args = row.GetProperty("args").EnumerateArray().ToArray(); var expected = substrGolden[row.GetProperty("id").GetString()!];
                if (args.Length is < 2 or > 3 || args[0].GetProperty("type").GetString() != "string" || args[1].GetProperty("type").GetString() != "int64"
                    || args.Length == 3 && args[2].GetProperty("type").GetString() != "int64" || expected.GetProperty("status").GetString() != "ok"
                    || expected.GetProperty("result").GetProperty("type").GetString() != "string" || row.GetProperty("throws").GetArrayLength() != 0) continue;
                var literal = LiteralBytes(args[0].GetProperty("value").GetString()!); if (literal is null) continue;
                var call = "Substr(" + literal + "," + args[1].GetProperty("value").GetInt64().ToString(CultureInfo.InvariantCulture)
                    + (args.Length == 3 ? "," + args[2].GetProperty("value").GetInt64().ToString(CultureInfo.InvariantCulture) : "") + ")";
                slices.TryAdd(expected.GetProperty("result").GetProperty("value").GetString()!, call);
            }
            string? Bytes(string hex) => LiteralBytes(hex) ?? slices.GetValueOrDefault(hex);
            string? Input(JsonElement value, bool expected = false)
            {
                switch (value.GetProperty("type").GetString())
                {
                    case "none": return "None";
                    case "bool": return value.GetProperty("value").GetBoolean() ? "true" : "false";
                    case "int64": return value.GetProperty("value").GetInt64().ToString(CultureInfo.InvariantCulture);
                    case "string": return Bytes(value.GetProperty("value").GetString()!);
                    case "double":
                        var number = value.GetProperty("value");
                        var floating = number.ValueKind == JsonValueKind.String
                            ? BitConverter.UInt64BitsToDouble(ulong.Parse(number.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture)) : number.GetDouble();
                        if (double.IsNaN(floating)) return "ToDouble('nan')";
                        if (double.IsInfinity(floating)) return floating < 0 ? "ToDouble('-inf')" : "ToDouble('inf')";
                        var text = floating.ToString("R", CultureInfo.InvariantCulture); return text.IndexOfAny(['.', 'e', 'E']) >= 0 ? text : text + ".0";
                    case "array":
                        var items = value.GetProperty("items").EnumerateArray().Select(item => Input(item, expected)).ToArray();
                        return items.Any(item => item is null) ? null : "Array(" + string.Join(",", items) + ")";
                    default: return null;
                }
            }
            var projections = new List<(string Id, bool Sensitive, string Expression)>();
            var boundaries = new List<object>();
            void Project(string suite, JsonElement row, JsonElement expected)
            {
                var id = row.GetProperty("id").GetString()!; var op = suite == "substr" ? "Substr" : row.GetProperty("operation").GetString()!;
                var args = row.GetProperty("args").EnumerateArray().Select(argument => Input(argument)).ToArray();
                var result = expected.GetProperty("result"); var wanted = Input(result, true);
                string? call = null;
                if (expected.GetProperty("status").GetString() == "ok" && row.GetProperty("throws").GetArrayLength() == 0 && args.All(argument => argument is not null) && wanted is not null)
                {
                    var binary = op switch { "Eq" => "==", "Ne" => "!=", "Lt" => "<", "Le" => "<=", "Gt" => ">", "Ge" => ">=", "Contains" => "contains", "Matches" => "matches", "Recontains" => "recontains", _ => null };
                    if (binary is not null && args.Length == 2)
                    {
                        // regexの右辺は現行公開parserでliteralに限定される。
                        if (op is not ("Matches" or "Recontains") || row.GetProperty("args")[1].GetProperty("type").GetString() == "string"
                            && LiteralBytes(row.GetProperty("args")[1].GetProperty("value").GetString()!) is not null)
                            call = "(" + args[0] + " " + binary + " " + args[1] + ")";
                        if (call is not null && op is "Matches" or "Recontains")
                        {
                            try { _ = new System.Text.RegularExpressions.Regex(strict.GetString(Convert.FromHexString(row.GetProperty("args")[1].GetProperty("value").GetString()!))); }
                            catch (ArgumentException) { call = null; }
                        }
                    }
                    else if (op is not ("Plus" or "ToString") && (op != "Substr" || args.Length is 2 or 3)) call = op + "(" + string.Join(",", args) + ")";
                }
                if (call is null)
                {
                    boundaries.Add(new { suite, id, scope = "native-only", reason = "Injected throw/arity; arbitrary raw bytes/unsupported native type; Plus/ToString; or nonliteral/invalid-syntax regex pattern", expected });
                    return;
                }
                var expression = "AllEqual(Array(" + call + "," + wanted + "))";
                var sensitive = !row.TryGetProperty("caseSensitive", out var sensitivity) || sensitivity.GetBoolean();
                projections.Add((suite + "-" + id, sensitive, expression));
                boundaries.Add(new { suite, id, scope = "public-cli-composition", expression, sensitive, expected, limit = "Raw bytes/trace are native evidence; public malformed arguments/expectations are dynamically generated Substr values, not byte injection" });
            }
            foreach (var row in substrPlan.RootElement.EnumerateArray()) Project("substr", row, substrGolden[row.GetProperty("id").GetString()!]);
            foreach (var (suite, directory) in new[] { ("observers", observers), ("consumers", Path.Combine(observers, "consumers")) })
            {
                using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "cases.json")));
                var golden = File.ReadLines(Path.Combine(directory, "golden.ndjson")).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                    .Where(row => row.TryGetProperty("id", out _)).ToDictionary(row => row.GetProperty("id").GetString()!);
                foreach (var row in plan.RootElement.EnumerateArray()) Project(suite, row, golden[row.GetProperty("id").GetString()!]);
            }
            using (var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(observers, "glob", "glob-cases.json"))))
            {
                var golden = File.ReadLines(Path.Combine(observers, "glob", "glob-golden.ndjson")).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToDictionary(row => row.GetProperty("id").GetString()!);
                foreach (var row in plan.RootElement.EnumerateArray())
                {
                    var id = row.GetProperty("id").GetString()!; var raw = golden[id];
                    using var output = JsonDocument.Parse(Convert.FromHexString(raw.GetProperty("stdoutHex").GetString()!)); var expected = output.RootElement;
                    var subject = Bytes(row.GetProperty("subjectHex").GetString()!); var pattern = Bytes(row.GetProperty("patternHex").GetString()!);
                    if (expected.GetProperty("status").GetString() != "ok" || subject is null || pattern is null)
                    {
                        boundaries.Add(new { suite = "glob", id, scope = "native-only", reason = "Native syntax exception or arbitrary malformed binding cannot be generated by public literal/slice", raw }); continue;
                    }
                    var expression = "AllEqual(Array((" + subject + " like " + pattern + ")," + (expected.GetProperty("result").GetBoolean() ? "true" : "false") + "))";
                    var sensitive = row.GetProperty("caseSensitive").GetBoolean(); projections.Add(("glob-" + id, sensitive, expression));
                    boundaries.Add(new { suite = "glob", id, scope = "public-cli-composition", expression, sensitive, raw });
                }
            }
            await File.WriteAllTextAsync(Path.Combine(work, "substr-byte-projections.json"), JsonSerializer.Serialize(boundaries));
            check("folder Substr projection coverage", boundaries.Count == 268 + 1226 + 1042 + 756 && projections.Count > 1000 && slices.Count > 10,
                JsonSerializer.Serialize(new { native = boundaries.Count, projected = projections.Count, generatedSlices = slices.Count }));
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "substr-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3]);
                    if (side != count - 1) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [4, 5]);
                }
                var inputs = roots.SelectMany(root => Directory.GetFiles(root)).ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                var sentinel = Path.Combine(work, "substr-" + count + "-existing-output.bin"); File.WriteAllBytes(sentinel, [9, 8, 7]);
                async Task Compare(string id, string expression, bool reject = false, bool budget = false)
                {
                    List<string> args = ["--directory", roots[0], roots[^1], "--filter", Filter("substr-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"), "--show-filtered"];
                    if (count == 3) args.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-substr-" + count + "-" + id, reject ? 2 : 1, !reject, args.ToArray());
                    if (reject) { check("folder Substr rejected stdout empty " + count + " " + id, result.Stdout.Length == 0 && (!budget || result.Stderr.Contains("65,536", StringComparison.Ordinal)), result.Stdout + result.Stderr); return; }
                    if (result.ExitCode != 1) return;
                    using var model = JsonDocument.Parse(result.Stdout); var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder Substr " + count + " " + id, entries.Length == 2 && entries.All(entry => !entry.GetProperty("filtered").GetBoolean()
                        && (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name =>
                            !entry.TryGetProperty(name, out var state) || state.ValueKind == JsonValueKind.Null || !state.GetProperty("filtered").GetBoolean())), result.Stdout);
                }
                // 同じ感度の4条件を一命令へまとめ、parserの512 token以内で論理積を実scannerへ通す。
                foreach (var group in projections.GroupBy(item => item.Sensitive))
                    foreach (var batch in group.Chunk(4))
                    {
                        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";", batch.Select(item => item.Id)))))[..12];
                        await Compare("oracle-" + id, (group.Key ? "@cs " : "@ci ") + string.Join(" and ", batch.Select(item => "(" + item.Expression + ")")));
                    }
                foreach (var (id, expression) in new[]
                {
                    ("case", "sUbStR('abcdef',1,3) == 'bcd'"),
                    ("bad-start-skip-source", "Substr(At(Array(1),ToInt('bad')),'bad') == None"),
                    ("noninteger-length-suffix", "Substr('abc',1,true) == 'bc' and Substr('abc',1,Array(1)) == 'bc'"),
                    ("raw-collision", "@cs Substr('é',1,1) != Substr('雪',1,1) and not AllEqual(Array(Substr('é',1,1),Substr('雪',1,1)))"),
                    ("raw-vs-replacement", "@cs Substr('é',1,1) != '�' and Substr('雪',1,1) != '�' and Substr('😀',1,2) != '��'"),
                    ("nested-stringify", "StrFind(At(Substr(Array(Array(Substr('é',1,1))),0),0),Substr('é',1,1)) == 1 and Len(At(Substr(Array(Array(Substr('é',1,1))),0),0)) == 3"),
                    ("metadata", "AllEqual(Array(Substr(Name,0),Name)) and AllEqual(Array(Substr(FullPath,0),FullPath))"),
                    ("side-context", "Substr(LeftName,0) == LeftName and Substr(LeftFullPath,0) == LeftFullPath"),
                    ("start-context", "Substr('abcde',LeftSize,1) == If(LeftSize == 3,'d','c')"),
                    ("length-context", "Substr('abc',0,LeftSize) == If(LeftSize == 3,'abc','ab')"),
                    ("timestamp-stringify", "Substr(d'2024-06-01T12:00:00Z',0,4) == '2024'"),
                    ("nul-byte-equality", "AllEqual(Array(Substr('A\0B',1,1),'\0')) and Substr('A\0B',1,1) == '\0' and Substr('A\0B',1,1) != ''"),
                    ("nul-byte-observers", "Len(Substr('A\0B',1,1)) == 1 and StrFind('A\0B','\0') == 1 and StrCount('A\0B\0','\0') == 2 and Trim(' \0 ') == '\0'"),
                    ("glob-invalid-token", "Substr('é',1,1) like Substr('雪',1,1)"),
                    ("regex-invalid", "not (Substr('é',1,1) matches '.') and not (Substr('雪',1,1) recontains '.')"),
                    ("byte-order-cs", "@cs Substr('é',1,1) > 'z'"),
                    ("byte-order-ci", "@ci Substr('é',1,1) < 'a'")
                }) await Compare(id, expression);
                if (OperatingSystem.IsWindows()) await Compare("property-context", "AllEqual(Array(Substr(Prop('System.Size'),0),Substr(Size,0)))");
                foreach (var expression in new[] { "Substr()", "Substr('a')", "Substr('a',0,1,2)", "Substr(,0)", "Substr('a',)", "Substr('a',0,)", "Substr('a',0", "Substr" })
                {
                    var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expression)))[..8]; await Compare("invalid-" + id, expression, true);
                }
                // sourceを評価しないことは、実際に予算拒否する同一probeとの対照で確認する。
                // 短いFullPath参照を反復し、文字列literalの構文上限へ依存しないprobe。
                var workProbe = "Strlen(Substr(Array(Array(" + string.Join(",", Enumerable.Repeat("LeftFullPath", 200)) + ")),0))";
                await Compare("bad-start-skips-budget-source", "Substr(" + workProbe + ",'bad') == None");
                await Compare("bad-start-evaluates-length", "Substr('x','bad'," + workProbe + ") == None", true, true);
                await Compare("start-before-length", "Substr('x'," + workProbe + ",0) == None", true, true);
                await Compare("valid-start-evaluates-source", "Substr(" + workProbe + ",0) == None", true, true);
                check("folder Substr input SHA preserved " + count, inputs.All(item => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(item.Key))) == item.Value), JsonSerializer.Serialize(inputs));
                check("folder Substr existing sentinel preserved " + count, File.ReadAllBytes(sentinel).SequenceEqual(new byte[] { 9, 8, 7 }), sentinel);
            }
        }

        async Task IsWithinValues()
        {
            // READMEの実装前失敗行列に対応。native traceは公開CLIの評価traceと区別する。
            var oracle = Path.GetFullPath("tests/Fixtures/FolderIsWithinOracle");
            var reader = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(oracle, "verify.py") }) reader.ArgumentList.Add(argument);
            using (var process = Process.Start(reader)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var evidence = await stdout; var errors = await stderr;
                await File.WriteAllTextAsync(Path.Combine(work, "independent-iswithin-oracle.json"), evidence);
                await File.WriteAllTextAsync(Path.Combine(work, "independent-iswithin-oracle.stderr.txt"), errors);
                check("folder IsWithin independent native oracle", process.ExitCode == 0, evidence + errors);
            }
            using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(oracle, "cases.json")));
            var golden = File.ReadLines(Path.Combine(oracle, "golden.ndjson")).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToDictionary(row => row.GetProperty("id").GetString()!);
            static string Quote(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            string? Input(JsonElement value)
            {
                switch (value.GetProperty("type").GetString())
                {
                    case "int64": return value.GetProperty("value").GetInt64().ToString(CultureInfo.InvariantCulture);
                    case "double":
                        var bits = ulong.Parse(value.GetProperty("value").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        var number = BitConverter.Int64BitsToDouble(unchecked((long)bits));
                        if (double.IsNaN(number)) return "ToDouble('nan')";
                        if (double.IsInfinity(number)) return number < 0 ? "ToDouble('-inf')" : "ToDouble('inf')";
                        var literal = number.ToString("R", CultureInfo.InvariantCulture);
                        return literal.IndexOfAny(['.', 'e', 'E']) < 0 ? literal + ".0" : literal;
                    case "timestamp":
                        try { return "d'" + DateTime.UnixEpoch.AddTicks(checked(value.GetProperty("value").GetInt64() * 10)).ToString("O", CultureInfo.InvariantCulture) + "'"; }
                        catch (Exception error) when (error is OverflowException or ArgumentOutOfRangeException) { return null; }
                    case "string": var text = value.GetProperty("value").GetString()!; return text.Contains('\0') ? null : Quote(text);
                    case "none": return "None";
                    case "bool": return value.GetProperty("value").GetBoolean() ? "true" : "false";
                    case "array":
                        var items = value.GetProperty("items").EnumerateArray().Select(Input).ToArray();
                        return items.Any(item => item is null) ? null : "Array(" + string.Join(",", items) + ")";
                    default: return null;
                }
            }
            string Expected(JsonElement value) => value.GetProperty("type").GetString() switch
            {
                "bool" => value.GetProperty("value").GetBoolean() ? "true" : "false",
                "none" => "None",
                "array" => "Array(" + string.Join(",", value.GetProperty("items").EnumerateArray().Select(Expected)) + ")",
                _ => throw new InvalidDataException("Unexpected IsWithin result")
            };
            var projections = new List<(string Id, string Expression)>(); var boundaries = new List<object>();
            foreach (var row in plan.RootElement.EnumerateArray())
            {
                var id = row.GetProperty("id").GetString()!; var expected = golden[id];
                var args = row.GetProperty("args").EnumerateArray().Select(Input).ToArray();
                if (expected.GetProperty("status").GetString() != "ok" || args.Length != 3 || args.Any(argument => argument is null))
                {
                    boundaries.Add(new { id, scope = "native-only", reason = "Injected throw/constructor arity, unsupported Regex/FileContent/NUL binding, or Timestamp outside DateTime literal range", expected });
                    continue;
                }
                var call = "IsWithin(" + string.Join(",", args) + ")";
                var result = expected.GetProperty("result");
                var expression = result.GetProperty("type").GetString() == "bool"
                    ? result.GetProperty("value").GetBoolean() ? call : "not " + call
                    : "AllEqual(Array(" + call + "," + Expected(result) + "))";
                expression = (row.GetProperty("caseSensitive").GetBoolean() ? "@cs " : "@ci ") + expression;
                projections.Add((id, expression));
                boundaries.Add(new { id, scope = "public-cli-projection", expression, expected, limit = "Date literal projection, NaN/sign-zero bits and native trace are not public binding/trace evidence" });
            }
            await File.WriteAllTextAsync(Path.Combine(work, "iswithin-projections.json"), JsonSerializer.Serialize(boundaries));
            check("folder IsWithin projection coverage", boundaries.Count == 159 && projections.Count > 100, JsonSerializer.Serialize(new { native = boundaries.Count, projected = projections.Count }));
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "iswithin-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3]);
                    if (side != count - 1) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [4, 5]);
                    foreach (var file in Directory.GetFiles(roots[side])) File.SetLastWriteTimeUtc(file, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                }
                var inputs = roots.SelectMany(root => Directory.GetFiles(root)).ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                var sentinel = Path.Combine(work, "iswithin-" + count + "-existing-output.bin"); File.WriteAllBytes(sentinel, [9, 8, 7]);
                async Task Compare(string id, string expression)
                {
                    List<string> args = ["--directory", roots[0], roots[^1], "--filter", Filter("iswithin-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"), "--show-filtered"];
                    if (count == 3) args.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-iswithin-" + count + "-" + id, 1, true, args.ToArray());
                    if (result.ExitCode != 1) return;
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder IsWithin " + count + " " + id, entries.Length == 2 && entries.All(entry => !entry.GetProperty("filtered").GetBoolean()
                        && (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name =>
                            !entry.TryGetProperty(name, out var state) || state.ValueKind == JsonValueKind.Null || !state.GetProperty("filtered").GetBoolean())), result.Stdout);
                }
                foreach (var projection in projections) await Compare("oracle-" + projection.Id, projection.Expression);
                foreach (var (id, expression) in new[]
                {
                    ("case", "iSwItHiN(1,0,2)"),
                    ("upper-inclusive", "IsWithin(2,1,2) and not InRange(2,1,2)"),
                    ("equal-bounds", "IsWithin(2,2,2) and not InRange(2,2,2)"),
                    ("reversed-bounds", "not IsWithin(2,3,1)"),
                    ("string-upper-cs", "@cs IsWithin('z','a','z') and not InRange('z','a','z')"),
                    ("string-upper-ci", "@ci IsWithin('Z','a','z') and not InRange('Z','a','z')"),
                    ("timestamp-upper", "IsWithin(LeftDate,d'2023-12-31T23:59:59Z',d'2024-01-01T00:00:00Z') and not InRange(LeftDate,d'2023-12-31T23:59:59Z',d'2024-01-01T00:00:00Z')"),
                    ("standalone-double-context", "IsWithin(1.5,1,1.5)"),
                    ("value-highbit-cs", "@cs not IsWithin('é','a','z')"),
                    ("value-highbit-ci", "@ci not IsWithin('é','a','z')"),
                    ("lower-highbit-cs", "@cs not IsWithin('a','é','z')"),
                    ("lower-highbit-ci", "@ci IsWithin('a','é','z')"),
                    ("upper-highbit-cs", "@cs IsWithin('a','','é')"),
                    ("upper-highbit-ci", "@ci not IsWithin('a','','é')"),
                    // Int64範囲外の整数literalは字句解析で拒否する契約（FolderOptimizer/semantic-expectations.json）。下の拒否検査で確認する。
                    ("metadata", "AllEqual(Array(IsWithin(Size,0,4),IfEach(Exists,true,None)))"),
                    ("side-metadata", "IsWithin(LeftSize,2,4)"),
                    ("lower-metadata", "IsWithin(3,LeftSize,4)"),
                    ("upper-metadata", "IsWithin(1,0,LeftSize)"),
                    ("context-value", "AllEqual(Array(IsWithin(FullPath,'','zzzz'),IsWithin(Size,0,4)))"),
                    ("context-lower", "IsWithin(LeftFullPath,LeftFullPath,'zzzz')"),
                    ("context-upper", "IsWithin(LeftFullPath,'',LeftFullPath)"),
                    ("timestamp", "IsWithin(LeftDate,d'2024-01-01T00:00:00Z',d'2024-01-01T00:00:01Z')"),
                    ("timestamp-array", "AllEqual(Array(IsWithin(Date,d'2024-01-01T00:00:00Z',d'2024-01-01T00:00:01Z'),IsWithin(Size,0,4)))"),
                }) await Compare(id, expression);
                if (OperatingSystem.IsWindows())
                {
                    await Compare("property", "AllEqual(Array(IsWithin(Prop('System.Size'),0,4),IsWithin(Size,0,4)))");
                    await Compare("property-boundaries", "IsWithin(LeftSize,LeftProp('System.Size'),4)");
                    await Compare("property-timestamp", "IsWithin(LeftProp('System.DateModified'),d'2024-01-01T00:00:00Z',d'2024-01-01T00:00:01Z')");
                    await Compare("property-upper", "IsWithin(LeftProp('System.Size'),0,LeftSize) and not InRange(LeftProp('System.Size'),0,LeftSize)");
                }
                else skip("folder IsWithin Shell Prop " + count, "Windows Shell property system only");
                foreach (var expression in new[] { "IsWithin()", "IsWithin(1)", "IsWithin(1,2)", "IsWithin(1,2,3,4)", "IsWithin(,1,2)", "IsWithin(1,,2)", "IsWithin(1,2,)", "IsWithin(1,2,3", "IsWithin", "IsWithin(1 2,3)" })
                {
                    var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expression)))[..8];
                    List<string> invalidArguments = ["--directory", roots[0], roots[^1], "--filter", Filter("iswithin-invalid-" + count + "-" + id, "fe: " + expression)];
                    if (count == 3) invalidArguments.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-iswithin-invalid-" + count + "-" + id, 2, false, invalidArguments.ToArray());
                    check("folder IsWithin invalid stdout empty " + count + " " + id, result.Stdout.Length == 0, result.Stdout + result.Stderr);
                }
                foreach (var (id, expression) in new[] { ("out-of-int64-value", "IsWithin(9223372036854775808,0,1) == None"), ("out-of-int64-upper", "IsWithin(1,0,9223372036854775808) == None") })
                {
                    List<string> rangeArguments = ["--directory", roots[0], roots[^1], "--filter", Filter("iswithin-" + count + "-" + id, "fe: " + expression)];
                    if (count == 3) rangeArguments.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-iswithin-" + count + "-" + id, 2, false, rangeArguments.ToArray());
                    check("folder IsWithin Int64 literal rejected " + count + " " + id,
                        result.Stdout.Length == 0 && result.Stderr.Contains("64bit", StringComparison.Ordinal), result.Stdout + result.Stderr);
                }
                var overBudget = "@ci IsWithin(Array(" + string.Join(",", Enumerable.Repeat("LeftFullPath", 200)) + "),LeftFullPath,'z')";
                // basenameだけでASCII 128 bytesを保証し、短い外部runでも必ず予算を超える。
                // 200 slotの作業量下限は602*L+602。L>=128なら77,658>65,536。
                var budgetRoots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "iswithin-budget-" + count, "side-" + side)).ToArray();
                var budgetPaths = new List<string>();
                foreach (var root in budgetRoots)
                {
                    Directory.CreateDirectory(root);
                    var path = Path.Combine(root, new string('a', 124) + ".bin"); File.WriteAllBytes(path, [7]);
                    budgetPaths.Add(path); inputs.Add(path, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                }
                var minimumBytes = budgetPaths.Min(path => Encoding.UTF8.GetByteCount(Path.GetFullPath(path)));
                var minimumWork = 602L * minimumBytes + 602;
                await File.WriteAllTextAsync(Path.Combine(work, "iswithin-budget-" + count + ".json"), JsonSerializer.Serialize(new { repetitions = 200, basenameBytes = 128, minimumBytes, minimumWork, limit = 65_536, paths = budgetPaths }));
                List<string> budgetArguments = ["--directory", budgetRoots[0], budgetRoots[^1], "--filter", Filter("iswithin-budget-" + count, "fe: " + overBudget)];
                if (count == 3) budgetArguments.AddRange(["--middle", budgetRoots[1]]);
                var rejected = await run("folder-iswithin-budget-" + count, 2, false, budgetArguments.ToArray());
                check("folder IsWithin shared budget " + count, minimumBytes >= 128 && minimumWork > 65_536 && rejected.Stdout.Length == 0 && rejected.Stderr.Contains("65,536", StringComparison.Ordinal), rejected.Stdout + rejected.Stderr);
                check("folder IsWithin input SHA preserved " + count, inputs.All(item => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(item.Key))) == item.Value), JsonSerializer.Serialize(inputs));
                // --directoryには出力指定がない。sentinel保持は既存fileの非変更だけを証明する。
                check("folder IsWithin existing sentinel preserved " + count, File.ReadAllBytes(sentinel).SequenceEqual(new byte[] { 9, 8, 7 }), sentinel);
            }
        }
        async Task InRangeValues()
        {
            // READMEの実装前失敗行列に対応。native traceは公開CLIの評価traceと区別する。
            var oracle = Path.GetFullPath("tests/Fixtures/FolderInRangeOracle");
            var reader = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(oracle, "verify.py") }) reader.ArgumentList.Add(argument);
            using (var process = Process.Start(reader)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var evidence = await stdout; var errors = await stderr;
                await File.WriteAllTextAsync(Path.Combine(work, "independent-inrange-oracle.json"), evidence);
                await File.WriteAllTextAsync(Path.Combine(work, "independent-inrange-oracle.stderr.txt"), errors);
                check("folder InRange independent native oracle", process.ExitCode == 0, evidence + errors);
            }
            using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(oracle, "cases.json")));
            var golden = File.ReadLines(Path.Combine(oracle, "golden.ndjson")).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToDictionary(row => row.GetProperty("id").GetString()!);
            static string Quote(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            string? Input(JsonElement value)
            {
                switch (value.GetProperty("type").GetString())
                {
                    case "int64": return value.GetProperty("value").GetInt64().ToString(CultureInfo.InvariantCulture);
                    case "double":
                        var bits = ulong.Parse(value.GetProperty("value").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        var number = BitConverter.Int64BitsToDouble(unchecked((long)bits));
                        if (double.IsNaN(number)) return "ToDouble('nan')";
                        if (double.IsInfinity(number)) return number < 0 ? "ToDouble('-inf')" : "ToDouble('inf')";
                        var literal = number.ToString("R", CultureInfo.InvariantCulture);
                        return literal.IndexOfAny(['.', 'e', 'E']) < 0 ? literal + ".0" : literal;
                    case "timestamp":
                        try { return "d'" + DateTime.UnixEpoch.AddTicks(checked(value.GetProperty("value").GetInt64() * 10)).ToString("O", CultureInfo.InvariantCulture) + "'"; }
                        catch (Exception error) when (error is OverflowException or ArgumentOutOfRangeException) { return null; }
                    case "string": var text = value.GetProperty("value").GetString()!; return text.Contains('\0') ? null : Quote(text);
                    case "none": return "None";
                    case "bool": return value.GetProperty("value").GetBoolean() ? "true" : "false";
                    case "array":
                        var items = value.GetProperty("items").EnumerateArray().Select(Input).ToArray();
                        return items.Any(item => item is null) ? null : "Array(" + string.Join(",", items) + ")";
                    default: return null;
                }
            }
            string Expected(JsonElement value) => value.GetProperty("type").GetString() switch
            {
                "bool" => value.GetProperty("value").GetBoolean() ? "true" : "false",
                "none" => "None",
                "array" => "Array(" + string.Join(",", value.GetProperty("items").EnumerateArray().Select(Expected)) + ")",
                _ => throw new InvalidDataException("Unexpected InRange result")
            };
            var projections = new List<(string Id, string Expression)>(); var boundaries = new List<object>();
            foreach (var row in plan.RootElement.EnumerateArray())
            {
                var id = row.GetProperty("id").GetString()!; var expected = golden[id];
                var args = row.GetProperty("args").EnumerateArray().Select(Input).ToArray();
                if (expected.GetProperty("status").GetString() != "ok" || args.Length != 3 || args.Any(argument => argument is null))
                {
                    boundaries.Add(new { id, scope = "native-only", reason = "Injected throw/constructor arity, unsupported Regex/FileContent/NUL binding, or Timestamp outside DateTime literal range", expected });
                    continue;
                }
                var call = "InRange(" + string.Join(",", args) + ")";
                var result = expected.GetProperty("result");
                var expression = result.GetProperty("type").GetString() == "bool"
                    ? result.GetProperty("value").GetBoolean() ? call : "not " + call
                    : "AllEqual(Array(" + call + "," + Expected(result) + "))";
                expression = (row.GetProperty("caseSensitive").GetBoolean() ? "@cs " : "@ci ") + expression;
                projections.Add((id, expression));
                boundaries.Add(new { id, scope = "public-cli-projection", expression, expected, limit = "Date literal projection, NaN/sign-zero bits and native trace are not public binding/trace evidence" });
            }
            await File.WriteAllTextAsync(Path.Combine(work, "inrange-projections.json"), JsonSerializer.Serialize(boundaries));
            check("folder InRange projection coverage", boundaries.Count == 145 && projections.Count > 100, JsonSerializer.Serialize(new { native = boundaries.Count, projected = projections.Count }));
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "inrange-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3]);
                    if (side != count - 1) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [4, 5]);
                    foreach (var file in Directory.GetFiles(roots[side])) File.SetLastWriteTimeUtc(file, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                }
                var inputs = roots.SelectMany(root => Directory.GetFiles(root)).ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                var sentinel = Path.Combine(work, "inrange-" + count + "-existing-output.bin"); File.WriteAllBytes(sentinel, [9, 8, 7]);
                async Task Compare(string id, string expression)
                {
                    List<string> args = ["--directory", roots[0], roots[^1], "--filter", Filter("inrange-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"), "--show-filtered"];
                    if (count == 3) args.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-inrange-" + count + "-" + id, 1, true, args.ToArray());
                    if (result.ExitCode != 1) return;
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder InRange " + count + " " + id, entries.Length == 2 && entries.All(entry => !entry.GetProperty("filtered").GetBoolean()
                        && (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name =>
                            !entry.TryGetProperty(name, out var state) || state.ValueKind == JsonValueKind.Null || !state.GetProperty("filtered").GetBoolean())), result.Stdout);
                }
                foreach (var projection in projections) await Compare("oracle-" + projection.Id, projection.Expression);
                foreach (var (id, expression) in new[]
                {
                    ("case", "iNrAnGe(1,0,2)"),
                    ("standalone-double-context", "InRange(1.5,1,2)"),
                    ("value-highbit-cs", "@cs not InRange('é','a','z')"),
                    ("value-highbit-ci", "@ci not InRange('é','a','z')"),
                    ("lower-highbit-cs", "@cs not InRange('a','é','z')"),
                    ("lower-highbit-ci", "@ci InRange('a','é','z')"),
                    ("upper-highbit-cs", "@cs InRange('a','','é')"),
                    ("upper-highbit-ci", "@ci not InRange('a','','é')"),
                    // Int64範囲外の整数literalは字句解析で拒否する契約。下の拒否検査で確認する。
                    ("metadata", "AllEqual(Array(InRange(Size,0,4),IfEach(Exists,true,None)))"),
                    ("side-metadata", "InRange(LeftSize,2,4)"),
                    ("lower-metadata", "InRange(3,LeftSize,4)"),
                    ("upper-metadata", "InRange(1,0,LeftSize)"),
                    ("context-value", "AllEqual(Array(InRange(FullPath,'','zzzz'),InRange(Size,0,4)))"),
                    ("context-lower", "InRange(LeftFullPath,LeftFullPath,'zzzz')"),
                    ("context-upper", "not InRange(LeftFullPath,'',LeftFullPath)"),
                    ("timestamp", "InRange(LeftDate,d'2024-01-01T00:00:00Z',d'2024-01-01T00:00:01Z')"),
                    ("timestamp-array", "AllEqual(Array(InRange(Date,d'2024-01-01T00:00:00Z',d'2024-01-01T00:00:01Z'),InRange(Size,0,4)))"),
                }) await Compare(id, expression);
                if (OperatingSystem.IsWindows())
                {
                    await Compare("property", "AllEqual(Array(InRange(Prop('System.Size'),0,4),InRange(Size,0,4)))");
                    await Compare("property-boundaries", "InRange(LeftSize,LeftProp('System.Size'),4)");
                    await Compare("property-timestamp", "InRange(LeftProp('System.DateModified'),d'2024-01-01T00:00:00Z',d'2024-01-01T00:00:01Z')");
                }
                else skip("folder InRange Shell Prop " + count, "Windows Shell property system only");
                foreach (var expression in new[] { "InRange()", "InRange(1)", "InRange(1,2)", "InRange(1,2,3,4)", "InRange(,1,2)", "InRange(1,,2)", "InRange(1,2,)", "InRange(1,2,3", "InRange", "InRange(1 2,3)" })
                {
                    var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expression)))[..8];
                    var result = await run("folder-inrange-invalid-" + count + "-" + id, 2, false, ["--directory", roots[0], roots[^1], "--filter", Filter("inrange-invalid-" + count + "-" + id, "fe: " + expression)]);
                    check("folder InRange invalid stdout empty " + count + " " + id, result.Stdout.Length == 0, result.Stdout + result.Stderr);
                }
                foreach (var (id, expression) in new[] { ("out-of-int64-value", "InRange(9223372036854775808,0,1) == None"), ("out-of-int64-upper", "InRange(1,0,9223372036854775808) == None") })
                {
                    var result = await run("folder-inrange-" + count + "-" + id, 2, false, ["--directory", roots[0], roots[^1], "--filter", Filter("inrange-" + count + "-" + id, "fe: " + expression)]);
                    check("folder InRange Int64 literal rejected " + count + " " + id,
                        result.Stdout.Length == 0 && result.Stderr.Contains("64bit", StringComparison.Ordinal), result.Stdout + result.Stderr);
                }
                var overBudget = "@ci InRange(Array(" + string.Join(",", Enumerable.Repeat("LeftFullPath", 200)) + "),LeftFullPath,'z')";
                // basenameだけでASCII 128 bytesを保証し、短い外部runでも必ず予算を超える。
                // 200 slotの作業量下限は602*L+602。L>=128なら77,658>65,536。
                var budgetRoots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "inrange-budget-" + count, "side-" + side)).ToArray();
                var budgetPaths = new List<string>();
                foreach (var root in budgetRoots)
                {
                    Directory.CreateDirectory(root);
                    var path = Path.Combine(root, new string('a', 124) + ".bin"); File.WriteAllBytes(path, [7]);
                    budgetPaths.Add(path); inputs.Add(path, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                }
                var minimumBytes = budgetPaths.Min(path => Encoding.UTF8.GetByteCount(Path.GetFullPath(path)));
                var minimumWork = 602L * minimumBytes + 602;
                await File.WriteAllTextAsync(Path.Combine(work, "inrange-budget-" + count + ".json"), JsonSerializer.Serialize(new { repetitions = 200, basenameBytes = 128, minimumBytes, minimumWork, limit = 65_536, paths = budgetPaths }));
                List<string> budgetArguments = ["--directory", budgetRoots[0], budgetRoots[^1], "--filter", Filter("inrange-budget-" + count, "fe: " + overBudget)];
                if (count == 3) budgetArguments.AddRange(["--middle", budgetRoots[1]]);
                var rejected = await run("folder-inrange-budget-" + count, 2, false, budgetArguments.ToArray());
                check("folder InRange shared budget " + count, minimumBytes >= 128 && minimumWork > 65_536 && rejected.Stdout.Length == 0 && rejected.Stderr.Contains("65,536", StringComparison.Ordinal), rejected.Stdout + rejected.Stderr);
                check("folder InRange input SHA preserved " + count, inputs.All(item => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(item.Key))) == item.Value), JsonSerializer.Serialize(inputs));
                // --directoryには出力指定がない。sentinel保持は既存fileの非変更だけを証明する。
                check("folder InRange existing sentinel preserved " + count, File.ReadAllBytes(sentinel).SequenceEqual(new byte[] { 9, 8, 7 }), sentinel);
            }
        }
        async Task AbsValues()
        {
            // READMEの実装前失敗行列に対応。IEEE bits／typed binding／throwはnative証拠と区別する。
            var reader = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.GetFullPath("tests/Fixtures/FolderAbsOracle/verify.py") }) reader.ArgumentList.Add(argument);
            using (var process = Process.Start(reader)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var evidence = await stdout; var errors = await stderr;
                await File.WriteAllTextAsync(Path.Combine(work, "independent-abs-oracle.json"), evidence);
                await File.WriteAllTextAsync(Path.Combine(work, "independent-abs-oracle.stderr.txt"), errors);
                check("folder Abs independent native oracle", process.ExitCode == 0, evidence + errors);
            }
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "abs-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3]);
                    if (side != count - 1) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [4, 5]);
                }
                var inputs = roots.SelectMany(root => Directory.GetFiles(root)).ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                async Task Compare(string id, string expression)
                {
                    var filter = Filter("abs-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n");
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-abs-" + count + "-" + id, 1, true, arguments.ToArray());
                    if (result.ExitCode != 1) return;
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder Abs " + count + " " + id, entries.Length == 2 && entries.All(entry =>
                        !entry.GetProperty("filtered").GetBoolean()
                        && (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name =>
                            !entry.TryGetProperty(name, out var state) || state.ValueKind == JsonValueKind.Null || !state.GetProperty("filtered").GetBoolean())), result.Stdout);
                }
                foreach (var (id, expression) in new[]
                {
                    ("case", "aBs(-7) == 7"),
                    // 他関数のflagへ依存せず、Abs自身がDouble評価contextを有効にすることを検査する。
                    ("standalone-double-context", "Abs(-1.5) == 1.5"),
                    ("integer", "AllEqual(Array(Abs(-9007199254740993),9007199254740993)) and Abs(0) == 0 and Abs(7) == 7"),
                    ("int64-endpoints", "AllEqual(Array(Abs(-9223372036854775808),-9223372036854775808)) and Abs(-9223372036854775807) == 9223372036854775807 and Abs(9223372036854775807) == 9223372036854775807"),
                    // Int64範囲外の整数literalは字句解析で拒否する契約。下の拒否検査で確認する。
                    ("finite", "AllEqual(Array(Abs(-1.5),1.5)) and AllEqual(Array(Abs(1.5),1.5)) and AllEqual(Array(Abs(-1.7976931348623157e308),1.7976931348623157e308))"),
                    ("subnormal", "AllEqual(Array(Abs(-5e-324),5e-324))"),
                    ("zero", "AllEqual(Array(Abs(-0.0),0.0)) and AllEqual(Array(Abs(0.0),0.0)) and not AllEqual(Array(Abs(-0.0),0))"),
                    ("infinity", "Abs(ToDouble('-inf')) > 1.7976931348623157e308 and Abs(ToDouble('inf')) > 1.7976931348623157e308"),
                    ("nan", "not AllEqual(Array(Abs(ToDouble('-nan')),None)) and not AllEqual(Array(Abs(ToDouble('-nan')),Abs(ToDouble('-nan'))))"),
                    ("invalid-types", "Abs(None) == None and Abs(true) == None and Abs(false) == None and Abs('-7') == None and Abs(d'2024-01-01T00:00:00Z') == None"),
                    ("empty", "AllEqual(Array(Abs(Array()),Array()))"),
                    ("mixed", "AllEqual(Array(Abs(Array(-7,-1.5,None,true,'-2',Array(-1))),Array(7,1.5,None,None,None,None)))"),
                    ("nested", "AllEqual(Array(Abs(Array(Array(-1),Array())),Array(None,None)))"),
                    ("metadata", "AllEqual(Array(Abs(Size),Size))"),
                    ("side-metadata", "Abs(LeftSize) == LeftSize"),
                    ("context", "AllOf(Abs(FullPath) == None)"),
                    ("nested-call", "Abs(Abs(-7)) == 7"),
                }) await Compare(id, expression);
                if (OperatingSystem.IsWindows())
                {
                    await Compare("property", "AllEqual(Array(Abs(Prop('System.Size')),Prop('System.Size')))");
                    await Compare("side-property", "Abs(LeftProp('System.Size')) == LeftProp('System.Size')");
                }
                else skip("folder Abs Shell Prop " + count, "Windows Shell property system only");
                foreach (var expression in new[] { "Abs()", "Abs(1,2)", "Abs(1,2,3)", "Abs(,1)", "Abs(1,)", "Abs(1", "Abs", "Abs(1 2)" })
                {
                    var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expression)))[..8];
                    var result = await run("folder-abs-invalid-" + count + "-" + id, 2, false, ["--directory", roots[0], roots[^1], "--filter", Filter("abs-invalid-" + count + "-" + id, "fe: " + expression)]);
                    check("folder Abs invalid stdout empty " + count + " " + id, result.Stdout.Length == 0, result.Stdout + result.Stderr);
                }
                foreach (var (id, expression) in new[] { ("out-of-int64-positive", "Abs(9223372036854775808) == None"), ("out-of-int64-negative", "Abs(-9223372036854775809) == None") })
                {
                    var result = await run("folder-abs-" + count + "-" + id, 2, false, ["--directory", roots[0], roots[^1], "--filter", Filter("abs-" + count + "-" + id, "fe: " + expression)]);
                    check("folder Abs Int64 literal rejected " + count + " " + id,
                        result.Stdout.Length == 0 && result.Stderr.Contains("64bit", StringComparison.Ordinal), result.Stdout + result.Stderr);
                }
                check("folder Abs input SHA preserved " + count, inputs.All(item => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(item.Key))) == item.Value), JsonSerializer.Serialize(inputs));
            }
        }
        async Task NumericConversionValues()
        {
            // READMEの失敗行列に対応。固定native bodyのtrace/IEEE bitsと公開parserの観測は分ける。
            var oracle = Path.GetFullPath("tests/Fixtures/FolderNumericConversionOracle");
            var reader = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(oracle, "verify.py") }) reader.ArgumentList.Add(argument);
            using (var process = Process.Start(reader)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var evidence = await stdout; var errors = await stderr;
                await File.WriteAllTextAsync(Path.Combine(work, "independent-numeric-oracle.json"), evidence);
                check("folder numeric conversion independent native oracle", process.ExitCode == 0, evidence + errors);
            }
            using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(oracle, "cases.json")));
            var golden = File.ReadLines(Path.Combine(oracle, "golden.ndjson")).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .ToDictionary(row => row.GetProperty("id").GetString()!);
            static string Quote(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            var projections = new List<object>();
            var bodyOnly = new List<object>();
            var predicates = new List<string>();
            foreach (var row in plan.RootElement.EnumerateArray())
            {
                var id = row.GetProperty("id").GetString()!;
                var scope = row.GetProperty("scope").GetString()!;
                if (scope != "public-string-candidate")
                {
                    bodyOnly.Add(new { id, scope, reason = row.GetProperty("limit").GetString() });
                    continue;
                }
                var expected = golden[id];
                check("folder numeric conversion oracle status " + id, expected.GetProperty("status").GetString() == "ok", expected.GetRawText());
                var value = expected.GetProperty("result");
                var actual = row.GetProperty("function").GetString() + "(" + Quote(row.GetProperty("args")[0].GetProperty("value").GetString()!) + ")";
                var kind = value.GetProperty("type").GetString();
                string expression;
                var boundary = "Strict type/value comparison; public evaluation trace is not observed.";
                if (kind == "double")
                {
                    var bits = ulong.Parse(value.GetProperty("bits").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    var number = BitConverter.Int64BitsToDouble(unchecked((long)bits));
                    if (double.IsNaN(number))
                    {
                        expression = "not AllEqual(Array(" + actual + ",None)) and not AllEqual(Array(" + actual + "," + actual + "))";
                        boundary = "Non-None and non-reflexive strict equality; NaN sign/payload bits are verified only by the independent fixed oracle. Repeated calls do not prove evaluation count.";
                    }
                    else if (double.IsInfinity(number))
                    {
                        expression = "not AllEqual(Array(" + actual + ",None)) and " + actual + (number > 0 ? " > " : " < -") + "1.7976931348623157e308";
                        boundary = "Non-None and signed value beyond finite Double range; exact IEEE bits are verified only by the independent fixed oracle.";
                    }
                    else
                    {
                        var literal = number.ToString("R", CultureInfo.InvariantCulture);
                        if (literal.IndexOfAny(['.', 'e', 'E']) < 0) literal += ".0";
                        expression = "AllEqual(Array(" + actual + "," + literal + "))";
                        if (number == 0) boundary = "Strict Double type/value; signed-zero bits are verified only by the independent fixed oracle.";
                    }
                }
                else
                {
                    var literal = kind switch
                    {
                        "int" => value.GetProperty("decimal").GetString()!,
                        "none" => "None",
                        _ => throw new InvalidDataException("Unprojected numeric oracle type: " + kind)
                    };
                    expression = "AllEqual(Array(" + actual + "," + literal + "))";
                }
                predicates.Add(expression);
                projections.Add(new { id, expression, expected = value.Clone(), expectedPredicate = true, boundary });
            }
            check("folder numeric conversion oracle projection coverage", projections.Count == 198 && bodyOnly.Count == 48 && projections.Count + bodyOnly.Count == golden.Count,
                "projected=" + projections.Count + "; oracle-only=" + bodyOnly.Count + "; golden=" + golden.Count);
            foreach (var function in new[] { "ToInt", "ToDouble", "ToNumber" })
                check("folder numeric conversion public candidate count " + function, plan.RootElement.EnumerateArray().Count(row =>
                    row.GetProperty("scope").GetString() == "public-string-candidate" && row.GetProperty("function").GetString() == function) == 66, "66 public string candidates per function.");
            var hex5200Positive = "0x0." + new string('0', 1299) + "1p5200";
            var hex5200Negative = "0x1" + new string('0', 1300) + "p-5200";
            var hex5201Positive = "0x0." + new string('0', 1299) + "1p5201";
            var hex5201Negative = "0x1" + new string('0', 1301) + "p-5201";
            var decimal5200Positive = "0." + new string('0', 5199) + "1e5200";
            var decimal5200Negative = "1" + new string('0', 5200) + "e-5200";
            var decimal5201Positive = "0." + new string('0', 5200) + "1e5201";
            var decimal5201Negative = "1" + new string('0', 5201) + "e-5201";
            var decimalMidpoint = "1.00000000000000011102230246251565404236316680908203125";
            var decimalDiscardedTail = decimalMidpoint + new string('0', 714) + "1";
            var hexDiscardedTail = "0x1.00000000000008" + new string('0', 753) + "1p0";
            var hexBoundaries = "AllOf(Array(" +
                "AllEqual(Array(ToDouble(" + Quote(hex5200Positive) + "),1.0))," +
                "AllEqual(Array(ToNumber(" + Quote(hex5200Positive) + "),1.0))," +
                "AllEqual(Array(ToDouble(" + Quote(hex5200Negative) + "),1.0))," +
                "AllEqual(Array(ToDouble(" + Quote(hex5201Positive) + "),None))," +
                "AllEqual(Array(ToDouble(" + Quote(hex5201Negative) + "),None))))";
            var decimalExponent5200 = "AllOf(Array(" +
                "AllEqual(Array(ToDouble(" + Quote(decimal5200Positive) + "),1.0))," +
                "AllEqual(Array(ToDouble(" + Quote(decimal5200Negative) + "),1.0))))";
            var decimalExponent5201 = "AllOf(Array(" +
                "AllEqual(Array(ToDouble(" + Quote(decimal5201Positive) + "),None))," +
                "AllEqual(Array(ToDouble(" + Quote(decimal5201Negative) + "),None))))";
            var decimalToNumber5200 = "AllEqual(Array(ToNumber(" + Quote(decimal5200Positive) + "),1.0))";
            var decimalToNumber5201 = "AllOf(Array(" +
                "AllEqual(Array(ToNumber(" + Quote(decimal5201Positive) + "),None))," +
                "AllEqual(Array(ToNumber(" + Quote(decimal5201Negative) + "),None))))";
            var hexMantissaBoundary = "AllOf(Array(" +
                "AllEqual(Array(ToDouble(" + Quote(hexDiscardedTail) + "),1.0))," +
                "AllEqual(Array(ToNumber(" + Quote(hexDiscardedTail) + "),1.0))))";
            var decimalMantissaBoundary = "AllOf(Array(" +
                "AllEqual(Array(ToDouble(" + Quote(decimalDiscardedTail) + "),1.0))," +
                "AllEqual(Array(ToNumber(" + Quote(decimalDiscardedTail) + "),1.0))))";
            var zeroExponentBoundary = "AllOf(Array(" +
                "AllEqual(Array(ToDouble('0e5201'),0.0)),AllEqual(Array(ToDouble('0e-5201'),0.0))))";
            var negativeNaNStringification = "AllOf(Array(" +
                "AllEqual(Array(Trim(ToDouble('-nan')),'-nan')),Strlen(ToDouble('-nan')) == 4," +
                "AllEqual(Array(Trim(ToDouble('nan')),'nan')),Strlen(ToDouble('nan')) == 3))";
            var classifiedNaNStringification = "AllOf(Array(" +
                "AllEqual(Array(Trim(ToDouble('nan(ind)')),'-nan(ind)')),Strlen(ToDouble('nan(ind)')) == 9," +
                "AllEqual(Array(Trim(ToDouble('-nan(ind)')),'-nan(ind)')),Strlen(ToDouble('-nan(ind)')) == 9," +
                "AllEqual(Array(Trim(ToDouble('nan(snan)')),'nan(snan)')),Strlen(ToDouble('nan(snan)')) == 9," +
                "AllEqual(Array(Trim(ToDouble('-nan(snan)')),'-nan(snan)')),Strlen(ToDouble('-nan(snan)')) == 10))";
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "numeric-" + count, "side-" + side)).ToArray();
                for (var side = 0; side < count; side++)
                {
                    Directory.CreateDirectory(roots[side]);
                    File.WriteAllBytes(Path.Combine(roots[side], "42"), [1, 2, 3]);
                    File.WriteAllBytes(Path.Combine(roots[side], "1.5"), [(byte)(side + 1)]);
                    if (side != count - 1) File.WriteAllBytes(Path.Combine(roots[side], "bad"), [4]);
                }
                var hashes = roots.SelectMany(root => Directory.GetFiles(root)).ToDictionary(path => path,
                    path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                async Task Compare(string id, string expression, Func<string, bool>? included = null)
                {
                    included ??= _ => true;
                    check("folder numeric expression within cap " + count + " " + id, expression.Length <= 16_384, "characters=" + expression.Length);
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter",
                        Filter("numeric-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"), "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var exit = included("1.5") || included("bad") ? 1 : 0;
                    var result = await run("folder-numeric-" + count + "-" + id, exit, true, arguments.ToArray());
                    if (result.ExitCode != exit) return;
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder numeric conversion " + count + " " + id, entries.Length == 3 && entries.All(entry =>
                    {
                        var filtered = !included(entry.GetProperty("path").GetString()!);
                        return entry.GetProperty("filtered").GetBoolean() == filtered
                            && (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name =>
                                !entry.TryGetProperty(name, out var side) || side.ValueKind == JsonValueKind.Null || side.GetProperty("filtered").GetBoolean() == filtered);
                    }), result.Stdout);
                }
                var batch = 0;
                foreach (var chunk in predicates.Chunk(18))
                    await Compare("oracle-batch-" + batch++, "AllOf(Array(" + string.Join(",", chunk.Select(predicate => "(" + predicate + ")")) + "))");
                foreach (var function in new[] { "ToInt", "ToDouble", "ToNumber" })
                {
                    var expectedArray = function == "ToInt" ? "Array(1234,None,None,None,None,None,None)"
                        : function == "ToDouble" ? "Array(1234.0,1.5,None,None,None,None,None)" : "Array(1234,1.5,None,None,None,None,None)";
                    await Compare(function + "-array", "AllEqual(Array(" + function + "(Array('1,234','1.5',None,true,2,2.5,Array('9')))," + expectedArray + ")) and AllEqual(Array(" + function + "(Array()),Array()))");
                    var noneArray = "Array(" + string.Join(",", Enumerable.Repeat("None", count)) + ")";
                    await Compare(function + "-metadata", "AllOf(Array(AllEqual(Array(" + function + "(Size)," + noneArray + ")),AllEqual(Array(" + function + "(FullPath)," + noneArray + ")),AllEqual(Array(" + function + "(LeftSize),None)),AllEqual(Array(" + function + "(LeftDate),None)),AllEqual(Array(" + function + "(true),None))))");
                    await Compare(function + "-name", "AllOf(" + function + "(Name) == 42)", path => path == "42");
                    await Compare(function + "-side-name", function + "(LeftName) == 1.5", path => function != "ToInt" && path == "1.5");
                    await Compare(function + "-missing", "not RightExists and AllEqual(Array(" + function + "(RightName),None)) and AllEqual(Array(" + function + "(RightFullPath),None))", path => path == "bad");
                    if (count == 3) await Compare(function + "-middle", function + "(MiddleName) == 42", path => path == "42");
                    if (OperatingSystem.IsWindows())
                    {
                        await Compare(function + "-property", "AllOf(" + function + "(Prop('System.FileName')) == 42)", path => path == "42");
                        await Compare(function + "-typed-property", "AllEqual(Array(" + function + "(LeftProp('System.Size')),None))");
                    }
                    else skip("folder numeric conversion properties " + count + " " + function, "Shell property is Windows only.");
                }
                await Compare("case-names", "AllOf(Array(AllEqual(Array(tOiNt('42'),42)),AllEqual(Array(TODOUBLE('42'),42.0)),AllEqual(Array(tonumber('1.5'),1.5))))");
                await Compare("hex-exponent-cancellation-boundaries", hexBoundaries);
                await Compare("decimal-explicit-exponent-boundary-5200", decimalExponent5200);
                await Compare("decimal-explicit-exponent-boundary-5201", decimalExponent5201);
                await Compare("decimal-to-number-exponent-boundary-5200", decimalToNumber5200);
                await Compare("decimal-to-number-exponent-boundary-5201", decimalToNumber5201);
                await Compare("hex-mantissa-discarded-tail-boundary", hexMantissaBoundary);
                await Compare("decimal-mantissa-discarded-tail-boundary", decimalMantissaBoundary);
                await Compare("zero-exponent-range-boundary", zeroExponentBoundary);
                await Compare("negative-nan-stringification-composition", negativeNaNStringification);
                await Compare("classified-nan-stringification-composition", classifiedNaNStringification);
                if (count == 2)
                {
                    async Task Reject(string id, string expression)
                    {
                        var filter = Filter("numeric-invalid-" + id, "fe: " + expression + "\n");
                        var filterHash = SHA256.HashData(File.ReadAllBytes(filter));
                        var invalid = await run("folder-numeric-invalid-" + id, 2, false, ["--directory", roots[0], roots[^1], "--filter", filter]);
                        check("folder numeric rejection stdout " + id, invalid.Stdout.Length == 0, invalid.Stdout + invalid.Stderr);
                        var sync = await run("folder-numeric-invalid-sync-" + id, 2, false,
                            ["--folder-sync", roots[0], roots[^1], "--direction", "left-to-right", "--copy", "all", "--select", "1.5", "--filter", filter]);
                        check("folder numeric rejected input/output retained " + id, sync.Stdout.Length == 0
                            && filterHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(filter)))
                            && hashes.All(pair => File.Exists(pair.Key) && pair.Value == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key)))) , sync.Stdout + sync.Stderr);
                    }
                    foreach (var function in new[] { "ToInt", "ToDouble", "ToNumber" })
                    {
                        await Reject(function + "-zero", function + "()");
                        await Reject(function + "-two", function + "('42','1')");
                    }
                    await Reject("expression-cap", "ToInt('" + new string('1', 16_384) + "') == None");
                }
                check("folder numeric source and destination SHA " + count, hashes.All(pair => File.Exists(pair.Key)
                    && pair.Value == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key)))) , "All input and existing output bytes retained.");
            }
            await File.WriteAllTextAsync(Path.Combine(work, "numeric-conversion-projections.json"), JsonSerializer.Serialize(new
            {
                projections, bodyOnly,
                boundaries = new[]
                {
                    "198 public string candidates (66 per function) are projected against fixed typed golden results, in two- and three-way CLI comparisons.",
                    "48 oracle-only rows: 30 exact typed bindings, 9 embedded-NUL strings, 9 direct-body arity/throw cases. Public array/scalar probes are supplemental and do not reclassify these rows.",
                    "NaN payload/sign, infinity bits and signed-zero bits come only from the independent fixed oracle; public predicates test the expressible value properties.",
                    "Native direct-body trace, ignored extra arguments, exception propagation and exactly-once evaluation are not public parser evaluation-trace proof. Public zero/two arity is rejected separately."
                }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        async Task ToDateTimeValues()
        {
            var oracle = Path.GetFullPath("tests/Fixtures/LineTransformTimestampFunctions");
            var reader = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(oracle, "verify.py") }) reader.ArgumentList.Add(argument);
            using (var process = Process.Start(reader)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var evidence = await stdout + await stderr;
                await File.WriteAllTextAsync(Path.Combine(work, "independent-todatetime-oracle.json"), evidence);
                check("folder ToDateTime independent fixed native oracle", process.ExitCode == 0, evidence);
            }
            using var capture = ZipFile.OpenRead(Path.Combine(oracle, "native-functions-capture-closure.v9.zip"));
            using var rawReader = new StreamReader(capture.GetEntry("proof/native-run-1.stdout.raw")!.Open(), new UTF8Encoding(false, true));
            var rows = (await rawReader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).Where(row => row.GetProperty("function").GetString() == "todatetime" && row.GetProperty("bodyDidRun").GetBoolean()).ToArray();
            string[] excluded = ["todatetime-no-args", "todatetime-raw-int64-min", "todatetime-raw-int64-max", "todatetime-string-year-0000", "todatetime-string-locale-1900", "todatetime-string-locale-1969", "todatetime-string-locale-1970"];
            var publicRows = rows.Where(row => !excluded.Contains(row.GetProperty("id").GetString(), StringComparer.Ordinal)).ToArray();
            check("folder ToDateTime native projection selection", rows.Length == 59 && publicRows.Length == 52
                && publicRows.Count(row => row.GetProperty("input").GetProperty("kind").GetString() == "string") == 34
                && publicRows.Count(row => row.GetProperty("result").GetProperty("kind").GetString() == "timestamp") == 23, "59 measured bodies; 52 public candidates; 34 strings / 23 Timestamp results; 7 grammar-only exclusions");
            static string Quote(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            static string Literal(JsonElement value)
            {
                switch (value.GetProperty("kind").GetString())
                {
                    case "none": return "None";
                    case "bool": return value.GetProperty("value").GetBoolean() ? "true" : "false";
                    case "int64": return Int64Literal(value.GetProperty("valueDecimal").GetString()!);
                    case "double":
                        var number = value.GetProperty("value").GetDouble().ToString("R", CultureInfo.InvariantCulture);
                        return number.Contains('.') || number.Contains('E') || number.Contains('e') ? number : number + ".0";
                    case "string": return Quote(new UTF8Encoding(false, true).GetString(Convert.FromHexString(value.GetProperty("utf8Hex").GetString()!)));
                    case "timestamp":
                        var epoch = long.Parse(value.GetProperty("epochSignedInt64Decimal").GetString()!, CultureInfo.InvariantCulture);
                        return "d'" + DateTime.UnixEpoch.AddTicks(checked(epoch * 10)).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture) + "'";
                    case "array": return "Array(" + string.Join(',', value.GetProperty("items").EnumerateArray().Select(Literal)) + ")";
                    default: throw new InvalidDataException("Unprojectable native timestamp value");
                }
            }
            bool SameProfile(JsonElement row)
            {
                if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TZ"))) return false;
                var locale = new StringBuilder(85);
                if (ToDateTimeGetUserDefaultLocaleName(locale, 85) == 0) return false;
                var state = ToDateTimeGetTimeZoneInformation(out var zone);
                string Decode(string name) => Encoding.UTF8.GetString(Convert.FromHexString(row.GetProperty(name).GetString()!));
                return state != uint.MaxValue && ToDateTimeGetUserDefaultLCID() == row.GetProperty("userLcid").GetUInt32()
                    && locale.ToString() == Decode("userLocaleUtf8Hex") && state == row.GetProperty("timezoneState").GetUInt32()
                    && zone.StandardName == Decode("timezoneStandardNameUtf8Hex") && zone.Bias == row.GetProperty("timezoneBiasMinutes").GetInt32()
                    && zone.StandardBias == row.GetProperty("timezoneStandardBiasMinutes").GetInt32() && zone.DaylightBias == row.GetProperty("timezoneDaylightBiasMinutes").GetInt32()
                    && -zone.Bias * 60 - (state == 2 ? zone.DaylightBias * 60 : 0) == row.GetProperty("pocoTimezoneTzdSeconds").GetInt32();
            }
            var matching = SameProfile(rows[0]);
            if (!matching) skip("folder ToDateTime fixed native profile", "Native 52-row projections require natural ja-JP / LCID1041 / Tokyo / tzd32400; no recomputed expectations.");
            for (var count = 2; count <= 3; count++)
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "todatetime-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    foreach (var name in new[] { "same.bin", "different.bin", "missing.bin" })
                    {
                        if (name == "missing.bin" && side == 1) continue;
                        var path = Path.Combine(roots[side], name);
                        File.WriteAllBytes(path, name == "different.bin" ? Enumerable.Repeat((byte)(side + 1), side + 1).ToArray() : [1]);
                        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddSeconds(1));
                    }
                }
                var hashes = roots.SelectMany(Directory.GetFiles).ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                var sentinel = Path.Combine(work, "todatetime-sentinel-" + count); File.WriteAllText(sentinel, "existing output retained");
                var sentinelHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sentinel)));
                async Task Compare(string id, string expression, Func<string, bool>? included = null)
                {
                    included ??= _ => true;
                    List<string> args = ["--folder", roots[0], roots[^1], "--filter", Filter("todatetime-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"), "--show-filtered"];
                    if (count == 3) args.AddRange(["--middle", roots[1]]);
                    var exit = included("different.bin") || included("missing.bin") ? 1 : 0;
                    var result = await run("folder-todatetime-" + count + "-" + id, exit, true, args.ToArray());
                    if (result.ExitCode != exit) return;
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder ToDateTime " + count + " " + id, entries.Length == 3 && entries.All(entry =>
                    {
                        var filtered = !included(entry.GetProperty("path").GetString()!);
                        return entry.GetProperty("filtered").GetBoolean() == filtered
                            && (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name =>
                                !entry.TryGetProperty(name, out var side) || side.ValueKind == JsonValueKind.Null || side.GetProperty("filtered").GetBoolean() == filtered);
                    }), result.Stdout);
                }
                async Task Reject(string id, string expression, bool budget = false)
                {
                    List<string> args = ["--folder", roots[0], roots[^1], "--filter", Filter("todatetime-reject-" + count + "-" + id, "fe: " + expression + "\n")];
                    if (count == 3) args.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-todatetime-reject-" + count + "-" + id, 2, false, args.ToArray());
                    check("folder ToDateTime rejected stdout " + count + " " + id, result.Stdout.Length == 0 && (!budget || result.Stderr.Contains("65,536", StringComparison.Ordinal)), result.Stderr);
                }
                if (matching) foreach (var row in publicRows)
                    await Compare("oracle-" + row.GetProperty("id").GetString(), "AllEqual(Array(ToDateTime(" + Literal(row.GetProperty("input")) + ")," + Literal(row.GetProperty("result")) + "))");
                await Compare("case-name", "AllEqual(Array(TODATETIME('1970-01-01T00:00:00Z'),d'1970-01-01T00:00:00Z'))");
                await Compare("metadata-date", "AllEqual(Array(LeftDate,ToDateTime('1970-01-01T00:00:01Z')))");
                await Compare("timestamp-array", "AllEqual(ToDateTime(Date)) and Len(ToDateTime(Date)) == " + count + "b");
                await Compare("name-array", "AllEqual(ToDateTime(Name)) and Len(ToDateTime(Name)) == " + count + "b");
                await Compare("missing-side", "AllEqual(Array(ToDateTime(At(Size,1b)),None)) and At(Exists,0b)");
                await Compare("nested-array", "AllEqual(Array(ToDateTime(Array(Array('1970-01-01T00:00:00Z'),'invalid',false,1b,1.0)),Array(None,None,None,None,None)))");
                await Compare("not-predicate", "ToDateTime('1970-01-01T00:00:00Z')", _ => false);
                if (OperatingSystem.IsWindows()) await Compare("property-array", "AllEqual(ToDateTime(Prop('System.DateModified'))) and Len(ToDateTime(Prop('System.Size'))) == " + count + "b");
                var once = "Substr(ToLower('" + new string('A', 7500) + "'),0b,0b)";
                await Compare("argument-once-budget", "AllEqual(Array(ToDateTime(" + once + "),None))");
                var expensive = "ToDateTime('" + new string('A', 4000) + "')";
                await Reject("selected-budget", "AllEqual(Array(" + expensive + ",None))", true);
                // 実行時の短絡評価を検査するため @noopt で最適化を止める。原典 FunctionNode::Optimize は到達性に関係なく
                // ToDateTime(文字列literal) を畳み込み、managedはその作業を共有65,536予算へ計上して超過を拒否する設計である。
                await Compare("if-short-circuit", "@noopt If(true,true,AllEqual(Array(" + expensive + ",None)))");
                await Compare("and-short-circuit", "@noopt false and AllEqual(Array(" + expensive + ",None))", _ => false);
                await Compare("or-short-circuit", "@noopt true or AllEqual(Array(" + expensive + ",None))");
                // 既定（最適化あり）では、選ばれない分岐でも畳み込みが予算を超えて拒否される。
                await Reject("default-optimized-dead-branch-budget", "If(true,true,AllEqual(Array(" + expensive + ",None)))", true);
                var halfBudget = "ToDateTime('" + new string('A', 2100) + "')";
                await Reject("shared-array-budget", "AllEqual(Array(" + halfBudget + "," + halfBudget + "))", true);
                foreach (var (id, expression) in new[] { ("zero", "ToDateTime()"), ("two", "ToDateTime('a','b')"), ("bare", "ToDateTime"), ("empty", "ToDateTime(,)"), ("extra-comma", "ToDateTime('a',)"), ("missing-close", "ToDateTime('a'"), ("unselected-malformed", "If(true,true,ToDateTime())") }) await Reject(id, expression);
                check("folder ToDateTime input and sentinel SHA retained " + count, hashes.All(pair => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value)
                    && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sentinel))) == sentinelHash, "All input bytes and existing sentinel unchanged; folder CLI has no output-file option.");
            }
            await File.WriteAllTextAsync(Path.Combine(work, "todatetime-projections.json"), JsonSerializer.Serialize(new
            {
                measuredBodies = rows.Length, publicCandidates = publicRows.Length, executedPublicCandidates = matching ? publicRows.Length : 0,
                excluded = excluded.Select(id => new { id, reason = id == "todatetime-no-args" ? "public arity is one" : "Timestamp input or expected epoch is outside the filter date-literal grammar" }),
                nativeProfileMatched = matching, nativeParserAstQualified = false,
                limits = new[] { "Exact type and epoch microseconds compared with AllEqual(Array(actual,expected)); no native expectations regenerated.", "Non-Syntax host exceptions and cancellation are source-contract evidence, not public CLI injection coverage." }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        async Task ToDateTimeMetadataDependency()
        {
            for (var count = 2; count <= 3; count++)
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "todatetime-dependency-" + count, "side-" + side)).ToArray();
                var times = count == 2 ? new[] { 1, 2 } : new[] { 1, 3, 2 };
                for (var side = 0; side < count; side++)
                {
                    Directory.CreateDirectory(roots[side]);
                    var path = Path.Combine(roots[side], "probe.bin"); File.WriteAllBytes(path, [1]);
                    File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddSeconds(times[side]));
                    check("folder ToDateTime dependency fixture timestamp " + count + " " + side,
                        File.GetLastWriteTimeUtc(path) == DateTime.UnixEpoch.AddSeconds(times[side]), path);
                }
                var hashes = roots.SelectMany(Directory.GetFiles).ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                List<string> args = ["--folder", roots[0], roots[^1], "--filter", Filter("todatetime-dependency-" + count,
                    "def: exclude\nfe: ToDateTime('1970-01-01T00:00:01Z') == Date\n"), "--show-filtered"];
                if (count == 3) args.AddRange(["--middle", roots[1]]);
                var result = await run("folder-todatetime-metadata-dependency-" + count, 0, true, args.ToArray());
                if (result.ExitCode != 0) continue;
                using var model = JsonDocument.Parse(result.Stdout);
                var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                var names = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                check("folder ToDateTime all-side metadata dependency " + count, entries.Length == 1 && names.Select((name, side) =>
                {
                    var value = entries[0].GetProperty(name);
                    return !value.GetProperty("filtered").GetBoolean() && value.GetProperty("lastWriteTimeUtc").GetDateTime() == DateTime.UnixEpoch.AddSeconds(times[side]);
                }).All(value => value), result.Stdout);
                check("folder ToDateTime dependency input SHA retained " + count, hashes.All(pair => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value), "Dedicated input bytes unchanged.");
            }
        }

        // 実装前失敗行列: arity1/一度評価/短絡、StringとTimestampだけ受理、空/不正文字列とOS整形失敗はthrow。
        // 直下arrayだけmapし入れ子/他型はNone。52原典=正常33/例外19、8native-onlyは未採取のまま。
        // epoch -1us成功/-1s失敗、Date/Timestampのmicrosecond往復、共有65536と入力/sentinel SHAを実CLIで確認する。
        async Task ToDateStrValues()
        {
            var oracle = Path.GetFullPath("tests/Fixtures/LineTransformTimestampFunctions");
            var reader = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(oracle, "verify.py") }) reader.ArgumentList.Add(argument);
            using (var process = Process.Start(reader)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var evidence = await stdout + await stderr;
                await File.WriteAllTextAsync(Path.Combine(work, "independent-todatestr-oracle.json"), evidence);
                check("folder ToDateStr independent fixed native oracle", process.ExitCode == 0, evidence);
            }
            using var capture = ZipFile.OpenRead(Path.Combine(oracle, "native-functions-capture-closure.v9.zip"));
            using var rawReader = new StreamReader(capture.GetEntry("proof/native-run-1.stdout.raw")!.Open(), new UTF8Encoding(false, true));
            var allRows = (await rawReader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).Where(row => row.GetProperty("function").GetString() == "todatestr").ToArray();
            var rows = allRows.Where(row => row.GetProperty("bodyDidRun").GetBoolean()).ToArray();
            var excluded = allRows.Where(row => !row.GetProperty("bodyDidRun").GetBoolean()).Select(row => row.GetProperty("id").GetString()!).ToArray();
            var publicRows = rows;
            check("folder ToDateStr native projection selection", rows.Length == 52 && excluded.Length == 8
                && rows.Count(row => row.GetProperty("status").GetString() == "ok") == 33
                && rows.Count(row => row.GetProperty("status").GetString() == "exception") == 19,
                "52 public bodies: 33 returns / 19 exceptions; 8 native-only exclusions retain no golden");
            static string Quote(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            static string Literal(JsonElement value)
            {
                switch (value.GetProperty("kind").GetString())
                {
                    case "none": return "None";
                    case "bool": return value.GetProperty("value").GetBoolean() ? "true" : "false";
                    case "int64": return Int64Literal(value.GetProperty("valueDecimal").GetString()!);
                    case "double":
                        var number = value.GetProperty("value").GetDouble().ToString("R", CultureInfo.InvariantCulture);
                        return number.Contains('.') || number.Contains('E') || number.Contains('e') ? number : number + ".0";
                    case "string": return Quote(new UTF8Encoding(false, true).GetString(Convert.FromHexString(value.GetProperty("utf8Hex").GetString()!)));
                    case "timestamp":
                        var epoch = long.Parse(value.GetProperty("epochSignedInt64Decimal").GetString()!, CultureInfo.InvariantCulture);
                        return "d'" + DateTime.UnixEpoch.AddTicks(checked(epoch * 10)).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture) + "'";
                    case "array": return "Array(" + string.Join(',', value.GetProperty("items").EnumerateArray().Select(Literal)) + ")";
                    default: throw new InvalidDataException("Unprojectable native timestamp value");
                }
            }
            bool SameProfile(JsonElement row)
            {
                if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TZ"))) return false;
                var locale = new StringBuilder(85);
                if (ToDateTimeGetUserDefaultLocaleName(locale, 85) == 0) return false;
                var state = ToDateTimeGetTimeZoneInformation(out var zone);
                string Decode(string name) => Encoding.UTF8.GetString(Convert.FromHexString(row.GetProperty(name).GetString()!));
                return state != uint.MaxValue && ToDateTimeGetUserDefaultLCID() == row.GetProperty("userLcid").GetUInt32()
                    && locale.ToString() == Decode("userLocaleUtf8Hex") && state == row.GetProperty("timezoneState").GetUInt32()
                    && zone.StandardName == Decode("timezoneStandardNameUtf8Hex") && zone.Bias == row.GetProperty("timezoneBiasMinutes").GetInt32()
                    && zone.StandardBias == row.GetProperty("timezoneStandardBiasMinutes").GetInt32() && zone.DaylightBias == row.GetProperty("timezoneDaylightBiasMinutes").GetInt32()
                    && -zone.Bias * 60 - (state == 2 ? zone.DaylightBias * 60 : 0) == row.GetProperty("pocoTimezoneTzdSeconds").GetInt32();
            }
            var matching = SameProfile(rows[0]);
            if (!matching) skip("folder ToDateStr fixed native profile", "Native 52-row projections (33 returns / 19 exceptions) require natural ja-JP / LCID1041 / Tokyo / tzd32400; no recomputed expectations.");
            for (var count = 2; count <= 3; count++)
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "todatestr-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    foreach (var name in new[] { "same.bin", "different.bin", "missing.bin" })
                    {
                        if (name == "missing.bin" && side == 1) continue;
                        var path = Path.Combine(roots[side], name);
                        File.WriteAllBytes(path, name == "different.bin" ? Enumerable.Repeat((byte)(side + 1), side + 1).ToArray() : [1]);
                        // Windows Shellは1970年のSystem.DateModifiedをNoneと返すため、実property検証には現代の日付を使う。
                        File.SetLastWriteTimeUtc(path, new DateTime(2023, 11, 14, 22, 13, 21, DateTimeKind.Utc));
                    }
                }
                var hashes = roots.SelectMany(Directory.GetFiles).ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                var sentinel = Path.Combine(work, "todatestr-sentinel-" + count); File.WriteAllText(sentinel, "existing output retained");
                var sentinelHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sentinel)));
                async Task Compare(string id, string expression, Func<string, bool>? included = null)
                {
                    included ??= _ => true;
                    List<string> args = ["--folder", roots[0], roots[^1], "--filter", Filter("todatestr-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"), "--show-filtered"];
                    if (count == 3) args.AddRange(["--middle", roots[1]]);
                    var exit = included("different.bin") || included("missing.bin") ? 1 : 0;
                    var result = await run("folder-todatestr-" + count + "-" + id, exit, true, args.ToArray());
                    if (result.ExitCode != exit) return;
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder ToDateStr " + count + " " + id, entries.Length == 3 && entries.All(entry =>
                    {
                        var filtered = !included(entry.GetProperty("path").GetString()!);
                        return entry.GetProperty("filtered").GetBoolean() == filtered
                            && (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name =>
                                !entry.TryGetProperty(name, out var side) || side.ValueKind == JsonValueKind.Null || side.GetProperty("filtered").GetBoolean() == filtered);
                    }), result.Stdout);
                }
                async Task Reject(string id, string expression, bool budget = false)
                {
                    List<string> args = ["--folder", roots[0], roots[^1], "--filter", Filter("todatestr-reject-" + count + "-" + id, "fe: " + expression + "\n")];
                    if (count == 3) args.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-todatestr-reject-" + count + "-" + id, 2, false, args.ToArray());
                    check("folder ToDateStr rejected stdout " + count + " " + id, result.Stdout.Length == 0 && (!budget || result.Stderr.Contains("65,536", StringComparison.Ordinal)), result.Stderr);
                }
                if (matching) foreach (var row in publicRows)
                {
                    var id = "oracle-" + row.GetProperty("id").GetString();
                    var call = "ToDateStr(" + Literal(row.GetProperty("input")) + ")";
                    if (row.GetProperty("status").GetString() == "exception") await Reject(id, call);
                    else await Compare(id, "AllEqual(Array(" + call + "," + Literal(row.GetProperty("result")) + "))");
                }
                await Compare("case-name", "AllEqual(Array(TODATESTR('1970-01-01T00:00:00Z'),ToDateStr('1970-01-01T00:00:00Z')))");
                await Compare("metadata-date", "AllEqual(Array(ToDateStr(LeftDate),ToDateStr('2023-11-14T22:13:21Z')))");
                await Compare("timestamp-array", "AllEqual(ToDateStr(Date)) and Len(ToDateStr(Date)) == " + count + "b", path => path != "missing.bin");
                await Compare("modified-array", "AllEqual(Array(ToDateStr(Modified),ToDateStr(Date)))");
                await Reject("name-invalid-array", "AllEqual(ToDateStr(Name))");
                await Reject("empty-string", "ToDateStr('')");
                await Compare("missing-side", "AllEqual(Array(ToDateStr(At(Size,1b)),None)) and At(Exists,0b)");
                await Compare("nested-array", "AllEqual(Array(ToDateStr(Array(Array('1970-01-01T00:00:00Z'),None,false,1b,1.0)),Array(None,None,None,None,None)))");
                await Compare("not-predicate", "ToDateStr('1970-01-01T00:00:00Z')", _ => false);
                await Compare("timestamp-construction", "AllEqual(Array(ToDateStr(ToDateTime('1970-01-01T00:00:00.000001Z')),ToDateStr(d'1970-01-01T00:00:00.000001Z')))");
                await Compare("microsecond-roundtrip", "AllEqual(Array(ToDateTime('1969-12-31T23:59:59.999999Z'),d'1969-12-31T23:59:59.999999Z')) and AllEqual(Array(ToDateStr(ToDateTime('1969-12-31T23:59:59.999999Z')),ToDateStr(d'1969-12-31T23:59:59.999999Z')))");
                if (OperatingSystem.IsWindows()) await Compare("property-array", "AllEqual(ToDateStr(Prop('System.DateModified'))) and Len(ToDateStr(Prop('System.Size'))) == " + count + "b", path => path != "missing.bin");
                var once = "If(AllEqual(Array(Substr(ToLower('" + new string('A', 7500) + "'),0b,0b),'')),None,'invalid')";
                await Compare("argument-once-budget", "AllEqual(Array(ToDateStr(" + once + "),None))");
                var expensive = "ToDateStr('" + new string('A', 4000) + "')";
                await Reject("selected-budget", "AllEqual(Array(" + expensive + ",None))", true);
                await Compare("if-short-circuit", "If(true,true,AllEqual(Array(" + expensive + ",None)))");
                await Compare("and-short-circuit", "false and AllEqual(Array(" + expensive + ",None))", _ => false);
                await Compare("or-short-circuit", "true or AllEqual(Array(" + expensive + ",None))");
                // Lowerは4n、Substrはn+1。7500文字の各halfは約37550作業量で成功し、2halfは65536を超える。
                // 650引数の展開は512tokenに先に達するため使わず、約15200文字・100tokenの短い式を共有する。
                var halfBudget = "ToDateStr(" + once.Replace("None,'invalid'", "LeftDate,'invalid'", StringComparison.Ordinal) + ")";
                await Compare("half-budget-control", "AllEqual(Array(" + halfBudget + ",ToDateStr(LeftDate)))");
                await Reject("shared-array-budget", "AllEqual(Array(" + halfBudget + "," + halfBudget + "))", true);
                foreach (var (id, expression) in new[] { ("zero", "ToDateStr()"), ("two", "ToDateStr('a','b')"), ("bare", "ToDateStr"), ("empty", "ToDateStr(,)"), ("extra-comma", "ToDateStr('a',)"), ("missing-close", "ToDateStr('a'"), ("unselected-malformed", "If(true,true,ToDateStr())") }) await Reject(id, expression);
                check("folder ToDateStr input and sentinel SHA retained " + count, hashes.All(pair => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value)
                    && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sentinel))) == sentinelHash, "All input bytes and existing sentinel unchanged; folder CLI has no output-file option.");
            }
            await File.WriteAllTextAsync(Path.Combine(work, "todatestr-projections.json"), JsonSerializer.Serialize(new
            {
                measuredBodies = rows.Length, publicCandidates = publicRows.Length, executedPublicCandidates = matching ? publicRows.Length : 0,
                excluded = excluded.Select(id => new { id, reason = "Native static safety exclusion; no golden and no public native qualification" }),
                nativeProfileMatched = matching, nativeParserAstQualified = false,
                limits = new[] { "Exact result type, date bytes and array items compared with AllEqual(Array(actual,expected)); timestamp inputs retain microsecond precision.", "Non-Syntax host exceptions and cancellation are source-contract evidence, not public CLI injection coverage." }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        // 固定RegexCount本文を型付きpredicateへ投影し、原典外の安全境界は補足ケースに分ける。
        // 行・実在側・評価順と入力保持を、実アプリの別プロセスと独立readerで照合する。
        async Task RegexPositionValues()
        {
            foreach (var function in new[] { "RegexFind", "RegexExtract" })
            {
                var isFind = function == "RegexFind";
                var oracle = Path.GetFullPath("tests/Fixtures/LineTransformRegex");
                var directory = Path.Combine(work, function.ToLowerInvariant()); Directory.CreateDirectory(directory);
                async Task Reader(string script, params string[] arguments)
                {
                    var start = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    foreach (var argument in new[] { "-E", "-B", "-X", "utf8", script }.Concat(arguments)) start.ArgumentList.Add(argument);
                    using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync(); var evidence = await stdout + await stderr;
                    await File.WriteAllTextAsync(Path.Combine(directory, Path.GetFileNameWithoutExtension(script) + "-result.json"), evidence);
                    check("folder " + function + " independent " + Path.GetFileName(script), process.ExitCode == 0, evidence);
                }
                await Reader(Path.Combine(oracle, "verify.py"), "--verify", "--git");
                using var casesDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(oracle, "cases.json")));
                var all = casesDocument.RootElement.EnumerateArray().Where(item => item.GetProperty("function").GetString() == function.ToLowerInvariant()).ToArray();
                var golden = File.ReadAllLines(Path.Combine(oracle, "golden.ndjson")).Select(line => { using var row = JsonDocument.Parse(line); return row.RootElement.Clone(); })
                    .ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
                static bool PublicArgument(JsonElement value)
                {
                    var type = value.GetProperty("type").GetString();
                    return type == "array" ? value.GetProperty("value").EnumerateArray().All(PublicArgument)
                        : type is "integer" or "double" or "bool" or "none" || type == "string" && !value.GetProperty("value").GetString()!.Contains('\0');
                }
                static string Exclusion(JsonElement row)
                {
                    if (row.GetProperty("mode").GetString() != "function-body-noopt") return "optimizer-only";
                    if (row.GetProperty("nullArgs").GetBoolean()) return "null-args-adapter";
                    if (row.GetProperty("args").GetArrayLength() is < 2 or > 4) return "native-body-arity-public-parser-rejects";
                    foreach (var argument in row.GetProperty("args").EnumerateArray()) if (!PublicArgument(argument))
                        return argument.GetProperty("type").GetString() == "string" ? "embedded-nul-literal-projection" : "adapter-argument-" + argument.GetProperty("type").GetString();
                    return "";
                }
                var selected = all.Where(row => Exclusion(row).Length == 0).ToArray();
                var partition = all.Select(row => new { id = row.GetProperty("id").GetString(), exclusion = Exclusion(row), expected = golden[row.GetProperty("id").GetString()!] }).ToArray();
                await File.WriteAllTextAsync(Path.Combine(directory, "native-partition.json"), JsonSerializer.Serialize(partition));
                var kinds = selected.Select(row => golden[row.GetProperty("id").GetString()!].GetProperty("value"))
                    .Select(value => value.ValueKind == JsonValueKind.Null ? "exception" : value.GetProperty("kind").GetString()!).ToArray();
                check("folder " + function + " complete native partition", all.Length == 122 && selected.Length == 94
                    && kinds.Count(kind => kind == (isFind ? "integer" : "string")) == 52 && kinds.Count(kind => kind == "none") == 33
                    && kinds.Count(kind => kind == "array") == 4 && kinds.Count(kind => kind == "exception") == 5,
                    "94 public bodies: 52 scalar / 33 None / 4 Array / 5 exceptions; 28 native-only IDs retained");
                static string Quote(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                    .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
                static string Argument(JsonElement value) => value.GetProperty("type").GetString() switch
                {
                    "none" => "None", "string" => Quote(value.GetProperty("value").GetString()!),
                    "integer" => value.GetProperty("value").GetRawText(),
                    "double" => value.GetProperty("value").GetRawText().IndexOfAny(['.', 'e', 'E']) >= 0 ? value.GetProperty("value").GetRawText() : value.GetProperty("value").GetRawText() + ".0",
                    "bool" => value.GetProperty("value").GetBoolean() ? "true" : "false",
                    "array" => "Array(" + string.Join(',', value.GetProperty("value").EnumerateArray().Select(Argument)) + ")",
                    _ => throw new InvalidDataException("Unprojectable regex argument")
                };
                static string Expected(JsonElement value) => value.GetProperty("kind").GetString() switch
                {
                    "none" => "None", "integer" => value.GetProperty("value").GetRawText(),
                    "string" => Quote(new UTF8Encoding(false, true).GetString(Convert.FromHexString(value.GetProperty("utf8Hex").GetString()!))),
                    "array" => "Array(" + string.Join(',', value.GetProperty("items").EnumerateArray().Select(Expected)) + ")",
                    _ => throw new InvalidDataException("Unprojectable regex position result")
                };
                var preserved = new Dictionary<string, string>(); var observations = new List<object>(); var expectedCommands = new List<string>();
                static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
                foreach (var count in new[] { 2, 3 })
                {
                    var roots = Enumerable.Range(0, count).Select(side => Path.Combine(directory, "sides-" + count, "side-" + side)).ToArray();
                    foreach (var (root, side) in roots.Select((root, side) => (root, side)))
                    {
                        Directory.CreateDirectory(root); var input = Path.Combine(root, "row.bin"); File.WriteAllBytes(input, Enumerable.Repeat((byte)(side + 1), side + 1).ToArray());
                        File.SetLastWriteTimeUtc(input, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)); preserved[input] = Hash(input);
                    }
                    async Task Compare(string id, string expression, bool included = true, bool rejected = false, string events = "", string diagnostic = "", string? nativeId = null, bool multiple = false, bool missing = false)
                    {
                        expectedCommands.Add(count + ":" + id);
                        expression = expression.Replace("RegexPosition(", (id == "function-case" ? function.ToUpperInvariant() : function) + "(", StringComparison.Ordinal);
                        if (diagnostic == "RegexPosition") diagnostic = function;
                        if (id.StartsWith("budget-pattern-prepare-", StringComparison.Ordinal))
                        {
                            // この3件は最適化前処理ではなく、実評価の共有準備予算を検査する。
                            expression = "@noopt " + expression;
                            var standalone = id.EndsWith("standalone", StringComparison.Ordinal);
                            var expectedRejection = id.EndsWith("rejected", StringComparison.Ordinal);
                            var patternLength = id.EndsWith("accepted", StringComparison.Ordinal) ? 102 : 104;
                            var exact = "@noopt " + (standalone ? "" : "RegexCount(" + Quote(new string('a', 5000)) + ",'z') == 0 and ")
                                + "AllEqual(Array(" + function + "(''," + Quote(new string('b', patternLength)) + "),None))";
                            check("folder " + function + " exact literal preparation probe " + count + " " + id,
                                expression == exact && included && rejected == expectedRejection && events.Length == 0
                                && diagnostic == (expectedRejection ? "65,536" : ""), expression);
                        }

                        var filter = Filter(function.ToLowerInvariant() + "-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"); preserved[filter] = Hash(filter);
                        var activeRoots = roots; var sideNames = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                        var expectedRows = new Dictionary<string, (bool Included, string[] Present)>(StringComparer.Ordinal) { ["row.bin"] = (included, missing ? ["left"] : sideNames) };
                        if (multiple || missing)
                        {
                            activeRoots = Enumerable.Range(0, count).Select(side => Path.Combine(directory, (multiple ? "multiple-" : "missing-") + count, "side-" + side)).ToArray();
                            foreach (var (root, side) in activeRoots.Select((root, side) => (root, side)))
                            {
                                Directory.CreateDirectory(root);
                                foreach (var (name, size) in multiple ? new[] { ("alpha.bin", 1), ("beta.bin", 3) } : side == 0 ? new[] { ("row.bin", 1) } : Array.Empty<(string, int)>())
                                { var input = Path.Combine(root, name); File.WriteAllBytes(input, Enumerable.Repeat((byte)(side + 1), size).ToArray()); preserved[input] = Hash(input); }
                            }
                            if (multiple) { expectedRows.Clear(); expectedRows.Add("alpha.bin", (true, sideNames)); expectedRows.Add("beta.bin", (false, sideNames)); }
                        }
                        var expectedRowEvidence = expectedRows.ToDictionary(pair => pair.Key, pair => new { included = pair.Value.Included, present = pair.Value.Present });
                        await File.WriteAllTextAsync(Path.Combine(directory, count + "-" + id + "-expected-rows.json"), JsonSerializer.Serialize(expectedRowEvidence));
                        List<string> args = ["--folder", activeRoots[0], activeRoots[^1], "--filter", filter, "--show-filtered"]; if (count == 3) args.AddRange(["--middle", activeRoots[1]]);
                        var protectedBefore = activeRoots.SelectMany(root => Directory.EnumerateFiles(root)).Append(filter)
                            .ToDictionary(path => path, path => preserved[path], StringComparer.Ordinal);
                        var result = await run("folder-" + function.ToLowerInvariant() + "-" + count + "-" + id, rejected ? 2 : included ? 1 : 0, !rejected, args.ToArray());
                        var protectedAfter = protectedBefore.Keys.ToDictionary(path => path, path => Hash(path), StringComparer.Ordinal);
                        check("folder " + function + " immediate input/filter retention " + count + " " + id,
                            protectedBefore.All(pair => protectedAfter[pair.Key] == pair.Value), JsonSerializer.Serialize(protectedAfter));
                        var stdout = Path.Combine(directory, count + "-" + id + ".stdout"); var stderr = Path.Combine(directory, count + "-" + id + ".stderr");
                        await File.WriteAllTextAsync(stdout, result.Stdout, new UTF8Encoding(false)); await File.WriteAllTextAsync(stderr, result.Stderr, new UTF8Encoding(false));
                        bool EventsMatch()
                        {
                            if (!multiple) return result.Stderr == events;
                            var lines = result.Stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                            var expected = new[] { "info: alpha.bin", "warn: alpha.bin ^alpha", "info: beta.bin", "warn: beta.bin ^alpha" };
                            return lines.Length == expected.Length && lines.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal))
                                && Array.IndexOf(lines, expected[0]) < Array.IndexOf(lines, expected[1]) && Array.IndexOf(lines, expected[2]) < Array.IndexOf(lines, expected[3]);
                        }
                        var noParserEvents = !id.StartsWith("invalid-", StringComparison.Ordinal) || !result.Stderr.Split(Environment.NewLine, StringSplitOptions.None)
                            .Any(line => line.StartsWith("info: ", StringComparison.Ordinal) || line.StartsWith("warn: ", StringComparison.Ordinal) || line.StartsWith("error: ", StringComparison.Ordinal));
                        check("folder " + function + " streams/order " + count + " " + id, rejected ? result.Stdout.Length == 0 && result.Stderr.StartsWith(events, StringComparison.Ordinal)
                            && result.Stderr.Length > events.Length && result.Stderr.Contains(diagnostic, StringComparison.Ordinal) && noParserEvents : EventsMatch(), result.Stderr);
                        if (!rejected && result.ExitCode == (included ? 1 : 0))
                        {
                            using var json = JsonDocument.Parse(result.Stdout); var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                            check("folder " + function + " typed row and sides " + count + " " + id, entries.Length == expectedRows.Count
                                && entries.Select(entry => entry.GetProperty("path").GetString()!).ToHashSet(StringComparer.Ordinal).SetEquals(expectedRows.Keys)
                                && entries.All(entry =>
                                {
                                    var expected = expectedRows[entry.GetProperty("path").GetString()!];
                                    var present = new[] { "left", "middle", "right" }.Where(name => entry.TryGetProperty(name, out var side) && side.ValueKind != JsonValueKind.Null).ToHashSet(StringComparer.Ordinal);
                                    return present.SetEquals(expected.Present) && entry.GetProperty("filtered").GetBoolean() == !expected.Included
                                        && expected.Present.All(name => entry.GetProperty(name).GetProperty("filtered").GetBoolean() == !expected.Included);
                                }), result.Stdout);
                        }
                        if (id == "timeout") check("folder " + function + " bounded timeout " + count, result.DurationMilliseconds < 10_000, result.DurationMilliseconds.ToString(CultureInfo.InvariantCulture));
                        observations.Add(new { id, nativeId, count, included, rejected, multiple, missing, expectedRows = expectedRowEvidence, expression, stdout, stderr, exitCode = result.ExitCode,
                            duration = result.DurationMilliseconds, diagnostic, inputRoots = activeRoots, filter, protectedBefore, protectedAfter, eventsHex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(events)) });
                    }
                    foreach (var item in selected)
                    {
                        var id = item.GetProperty("id").GetString()!; var expected = golden[id]; var failure = expected.GetProperty("value").ValueKind == JsonValueKind.Null;
                        var call = function + "(" + string.Join(',', item.GetProperty("args").EnumerateArray().Select(Argument)) + ")";
                        await Compare("native-" + id, (item.GetProperty("caseSensitive").GetBoolean() ? "@cs " : "@ci ")
                            + (failure ? call : "AllEqual(Array(" + call + "," + Expected(expected.GetProperty("value")) + "))"), rejected: failure, diagnostic: failure ? expected.GetProperty("exceptionType").GetString() == "RegularExpressionException" ? "継続byte" : "末尾" : "", nativeId: id);
                    }
                    var nl = Environment.NewLine; var a200 = Quote(new string('a',200)); var a400 = Quote(new string('a',400));
                    var a3000 = Quote(new string('a',3000)); var a5000 = Quote(new string('a',5000)); var a5100 = Quote(new string('a',5100));
                    string Project(string call, string find, string extract) => "AllEqual(Array(" + call + "," + (isFind ? find : extract) + "))";
                    foreach (var (id, expression, events) in new[]
                    {
                        ("function-case", Project("RegexPosition('Aa','a')","0","'A'"), ""),
                        ("argument-order", Project("RegexPosition(LogInfo('source',123),LogWarn('pattern','[0-9]'),LogInfo('group',0),LogWarn('occurrence',1))","1","'2'"), "info: source 123" + nl + "warn: pattern [0-9]" + nl + "info: group 0" + nl + "warn: occurrence 1" + nl),
                        ("group-guard", Project("RegexPosition(At(Array(LogInfo('source',0),Substr('é',0,1)),1),LogWarn('pattern','['),LogInfo('group',0.5),LogWarn('unreached',0))","None","None"), "info: source 0" + nl + "warn: pattern [" + nl + "info: group 0.500000" + nl),
                        ("occurrence-guard", Project("RegexPosition(LogInfo('source','a'),LogWarn('pattern',None),LogInfo('group',0),LogWarn('occurrence',0.5))","None","None"), "info: source a" + nl + "warn: pattern none" + nl + "info: group 0" + nl + "warn: occurrence 0.500000" + nl),
                        ("pattern-type-eager", Project("RegexPosition(LogInfo('source','a'),LogWarn('pattern',false),LogInfo('group',0),LogWarn('occurrence',0))","None","None"), "info: source a" + nl + "warn: pattern false" + nl + "info: group 0" + nl + "warn: occurrence 0" + nl),
                        ("none-eager", Project("RegexPosition(LogInfo('source',None),LogWarn('pattern','a'),LogInfo('group',0),LogWarn('occurrence',0))","None","None"), "info: source none" + nl + "warn: pattern a" + nl + "info: group 0" + nl + "warn: occurrence 0" + nl),
                        ("array-eager", Project("RegexPosition(LogInfo('source',Array('aba',None)),LogWarn('pattern','(a)'),LogInfo('group',1),LogWarn('occurrence',1))","Array(2,None)","Array('a',None)"), "info: source [aba, none]" + nl + "warn: pattern (a)" + nl + "info: group 1" + nl + "warn: occurrence 1" + nl),
                        ("array-pattern-type", Project("RegexPosition(Array('a',None),Array('a'))","None","None"), ""),
                        ("array-group-type", Project("RegexPosition(Array('a',None),'a',Array(0))","None","None"), ""),
                        ("empty-array-invalid-pattern", Project("RegexPosition(Array(),'[')","Array()","Array()"), ""),
                        ("date-source", Project("RegexPosition(d'2024-01-02T03:04:05Z','^([0-9]{4})',1)","0","'2024'"), ""),
                        ("timestamp-source", Project("RegexPosition(ToDateTime('2024-01-02T03:04:05Z'),'^([0-9]{4})',1)","0","'2024'"), ""),
                        ("raw-pattern-none", Project("RegexPosition('abc',Substr('é',0,1))","None","None"), ""),
                        ("raw-source-pattern-type", Project("RegexPosition(Substr('é',0,1),None)","None","None"), ""),
                        ("none-skips-raw-pattern", Project("RegexPosition(None,Substr('é',0,1))","None","None"), ""),
                        ("raw-source-invalid-pattern", Project("RegexPosition(Substr('é',0,1),LogWarn('pattern','['))","None","None"), "warn: pattern [" + nl),
                        ("negative-occurrence-raw-source", Project("RegexPosition(Substr('é',0,1),'a',0,-1)","None","None"), ""),
                        ("negative-occurrence-invalid-pattern", Project("RegexPosition(Substr('é',0,1),'[',0,-1)","None","None"), ""),
                        ("negative-no-search", Project("RegexPosition(" + Quote(new string('a', 2000) + "!") + ",'(a+)+$',0,-1)","None","None"), ""),
                        ("utf8-byte-offset", Project("RegexPosition('é😀z','(z)',1)","6","'z'"), ""),
                        ("fourth-is-occurrence", Project("RegexPosition('xaxax','(a)',1,1)","3","'a'"), ""),
                        ("empty-capture", Project("RegexPosition('a','()a',1)","0","''"), ""),
                        ("unmatched-capture", Project("RegexPosition('a','(x)?a',1)","None","None"), ""),
                        ("negative-group", Project("RegexPosition('a','(a)',-1)","None","None"), ""),
                        ("out-of-range-group", Project("RegexPosition('a','(a)',9)","None","None"), ""),
                        ("out-of-range-occurrence", Project("RegexPosition('a','(a)',1,9)","None","None"), ""),
                        ("folder-function", Project("RegexPosition(ToUpper(LeftName),'ROW')","0","'ROW'"), ""),
                        ("bare-size-snapshot", "RegexPosition(Size,'1') == " + (isFind ? "0" : "'1'"), ""),
                        ("side-metadata", Project("RegexPosition(LeftSize,'1')","0","'1'") + " and " + Project("RegexPosition(RightSize,'[23]')","0",count == 3 ? "'3'" : "'2'") + (count == 3 ? " and " + Project("RegexPosition(MiddleSize,'2')","0","'2'") : ""), ""),
                        ("fullpath", Project("RegexPosition(FullPath,'^.*row[.]bin$')", "Array(" + string.Join(',', Enumerable.Repeat("0", count)) + ")", "FullPath"), ""),
                        ("lazy-and", "not (false and RegexPosition('é','(?:)',0,1) == None)", ""),
                        ("lazy-or", "true or RegexPosition('é','(?:)',0,1) == None", ""),
                        ("lazy-if", "If(true," + Project("RegexPosition('aa','a')","0","'a'") + ",RegexPosition('é','(?:)',0,1) == None)", ""),
                        ("logical-each", "AllEqual(Array(AndEach(RegexPosition(Array('aa',''),'a') == " + (isFind ? "0" : "'a'") + ",true),Array(true,false))) and AllEqual(Array(OrEach(RegexPosition(Array('aa',''),'a') == " + (isFind ? "0" : "'a'") + ",false),Array(true,false)))", ""),
                        ("typed-log-result", Project("LogInfo(RegexPosition('aa','a'))","0","'a'"), (isFind ? "info: 0" : "info: a") + nl),
                        ("budget-scalar-accepted", Project("RegexPosition(" + a5000 + ",'z')","None","None"), ""),
                        ("budget-pattern-prepare-accepted", "RegexCount(" + a5000 + ",'z') == 0 and " + Project("RegexPosition(''," + Quote(new string('b',102)) + ")","None","None"), ""),
                        ("budget-pattern-prepare-standalone", Project("RegexPosition(''," + Quote(new string('b',104)) + ")","None","None"), ""),
                        ("budget-pattern-expression-accepted", Project("RegexPosition('a',ToLower(" + Quote(new string('A', 2000)) + "))","None","None"), ""),
                        ("budget-array-accepted", Project("RegexPosition(ChooseEach(Array(" + string.Join(',', Enumerable.Range(0, 16).Select(value => value.ToString(CultureInfo.InvariantCulture))) + ")," + Quote(new string('a', 128)) + "),'z')", "Array(" + string.Join(',', Enumerable.Repeat("None", 16)) + ")", "Array(" + string.Join(',', Enumerable.Repeat("None", 16)) + ")"), ""),
                        ("budget-search-accepted", Project("RegexPosition(" + a200 + ",'a',0,199)","199","'a'"), ""),
                        ("output-budget-accepted", Project("RegexPosition(" + Quote(new string('a',4000)) + ",'^.*$')","0",Quote(new string('a',4000))), "")
                    }) await Compare(id, expression, events: events);
                    await Compare("budget-individual", Project("RegexPosition(" + a3000 + ",'z')","None","None"));
                    await Compare("missing-side", "RegexPosition(Size,'1') == " + (isFind ? "0" : "'1'"), missing: true);
                    await Compare("multiple-row", "RegexPosition(LogInfo(LeftName),LogWarn(LeftName,'^alpha')) == " + (isFind ? "0" : "'alpha'"), multiple: true);
                    if (OperatingSystem.IsWindows()) await Compare("property-size", Project("RegexPosition(Prop('System.Size'),'[123]')", "Array(" + string.Join(',',Enumerable.Repeat("0",count)) + ")", "Array(" + string.Join(',',Enumerable.Range(1,count).Select(value => Quote(value.ToString(CultureInfo.InvariantCulture)))) + ")"));
                    if (!isFind) await Compare("output-budget-rejected", "RegexPosition(" + Quote(new string('a',4400)) + ",'^.*$')", rejected: true, diagnostic: "65,536");
                    else await Compare("output-budget-find-control", Project("RegexPosition(" + Quote(new string('a',4400)) + ",'^.*$')","0","None"));
                    foreach (var (id, expression, diagnostic, events) in new[]
                    {
                        ("raw-source", "RegexPosition(Substr('é',0,1),'a')", "UTF-8", ""),
                        ("negative-group-raw-source", "RegexPosition(Substr('é',0,1),'a',-1,0)", "UTF-8", ""),
                        ("array-mid-failure", "RegexPosition(Array('abc','é'),'(?:)',0,1)", "継続byte", ""),
                        ("first-error-skips-pattern", "RegexPosition(ToLower(ToLower(" + Quote(new string('A',14000)) + ")),LogWarn('unreached','a'))", "65,536", ""),
                        ("pattern-error-retains-source", "RegexPosition(LogInfo('source','a'),ToLower(ToLower(" + Quote(new string('A',14000)) + ")))", "65,536", "info: source a" + nl),
                        ("group-error-retains-pattern", "RegexPosition(LogInfo('source','a'),LogWarn('pattern','a'),ToLower(ToLower(" + Quote(new string('A',14000)) + ")),LogWarn('unreached',0))", "65,536", "info: source a" + nl + "warn: pattern a" + nl),
                        ("occurrence-error-retains-group", "RegexPosition(LogInfo('source','a'),LogWarn('pattern','a'),LogInfo('group',0),ToLower(ToLower(" + Quote(new string('A',14000)) + ")))", "65,536", "info: source a" + nl + "warn: pattern a" + nl + "info: group 0" + nl),
                        ("budget-scalar-rejected", "RegexPosition(" + a5100 + ",'z')", "65,536", ""),
                        ("budget-pattern-prepare-rejected", "RegexCount(" + a5000 + ",'z') == 0 and " + Project("RegexPosition(''," + Quote(new string('b',104)) + ")","None","None"), "65,536", ""),
                        ("budget-pattern-expression-rejected", "RegexPosition('a',ToLower(ToLower(" + Quote(new string('A',14000)) + ")))", "65,536", ""),
                        ("budget-array-rejected", "RegexPosition(ChooseEach(Array(" + string.Join(',', Enumerable.Range(0, 64).Select(value => value.ToString(CultureInfo.InvariantCulture))) + ")," + Quote(new string('a',128)) + "),'z')", "65,536", ""),
                        ("budget-search-rejected", "RegexPosition(" + a400 + ",'a',0,399)", "65,536", ""),
                        ("budget-shared", Project("RegexPosition(" + a3000 + ",'z')","None","None") + " and " + Project("RegexPosition(" + a3000 + ",'z')","None","None"), "65,536", ""),
                        ("unsupported-han", "RegexPosition('𠀀'," + Quote("\\p{Han}") + ")", "Han", ""),
                        ("timeout", "RegexPosition(" + Quote(new string('a',2000) + "!") + ",'(a+)+$')", "100", "")
                    }) await Compare(id, expression, rejected: true, diagnostic: diagnostic, events: events);
                    foreach (var (id, expression) in new[]
                    {
                        ("zero", "RegexPosition()"),
                        ("one", "RegexPosition('a')"),
                        ("five", "RegexPosition('a','a',0,0,0)"),
                        ("bare", "RegexPosition"),
                        ("empty", "RegexPosition(,)"),
                        ("trailing", "RegexPosition('a','a',)"),
                        ("missing-close", "RegexPosition('a','a'"),
                        ("empty-group", "RegexPosition('a','a',,0)"),
                        ("typed-regex", "RegexPosition(LogInfo('unreached','a'),Regex('a'))"),
                        ("unselected", "If(true,LogInfo('unreached',true),RegexPosition())")
                    }) await Compare("invalid-" + id, expression == "RegexPosition" ? function : expression, rejected: true, diagnostic: "RegexPosition");
                }
                var inventory = Path.Combine(directory, "observations.json"); await File.WriteAllTextAsync(inventory, JsonSerializer.Serialize(new { observations, preserved, expectedCommands, function, windowsProperties = OperatingSystem.IsWindows(), newlineHex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(Environment.NewLine)) }));
                var reader = Path.Combine(directory, "verify-folder-regex-position.py");
                await File.WriteAllTextAsync(reader, """
import hashlib,json,pathlib,sys,collections
data=json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8')); base=pathlib.Path(sys.argv[2]); cases=json.loads((base/'cases.json').read_text(encoding='utf-8')); golden={r['id']:r for r in map(json.loads,(base/'golden.ndjson').read_text(encoding='utf-8').splitlines())}
function=data['function']; assert function in ['RegexFind','RegexExtract']; find=function=='RegexFind'
def public(a):
 t=a['type']; return all(map(public,a['value'])) if t=='array' else t in ['integer','double','bool','none'] or t=='string' and '\0' not in a['value']
def exclusion(c):
 if c['mode']!='function-body-noopt': return 'optimizer-only'
 if c['nullArgs']: return 'null-args-adapter'
 if not 2<=len(c['args'])<=4: return 'native-body-arity-public-parser-rejects'
 for a in c['args']:
  if not public(a): return 'embedded-nul-literal-projection' if a['type']=='string' else 'adapter-argument-'+a['type']
 return ''
native=[c for c in cases if c['function']==function.lower()]; selected=[c for c in native if not exclusion(c)]
def quote(s): return "'"+s.replace('\\','\\\\').replace("'","\\'").replace('\r','\\r').replace('\n','\\n').replace('\t','\\t')+"'"
def argument(a):
 t=a['type']
 if t=='none': return 'None'
 if t=='string': return quote(a['value'])
 if t=='bool': return 'true' if a['value'] else 'false'
 if t=='array': return 'Array('+','.join(map(argument,a['value']))+')'
 s=json.dumps(a['value']); return s if t=='integer' or any(c in s for c in '.eE') else s+'.0'
def expected_value(v):
 if v['kind']=='none': return 'None'
 if v['kind']=='integer': return str(v['value'])
 if v['kind']=='string': return quote(bytes.fromhex(v['utf8Hex']).decode('utf-8',errors='strict'))
 return 'Array('+','.join(map(expected_value,v['items']))+')'
assert len(native)==122 and len(selected)==94
assert collections.Counter(golden[c['id']]['value']['kind'] if golden[c['id']]['value'] is not None else 'exception' for c in selected)=={('integer' if find else 'string'):52,'none':33,'array':4,'exception':5}
partition=json.loads((pathlib.Path(sys.argv[1]).parent/'native-partition.json').read_text(encoding='utf-8')); assert len(partition)==122 and len({r['id'] for r in partition})==122
assert {r['id']:r['exclusion'] for r in partition}=={c['id']:exclusion(c) for c in native}
assert all(r['expected']==golden[r['id']] for r in partition)
seen=[]; pairs=[]; errors=0; nl=bytes.fromhex(data['newlineHex'])
markers={
 'argument-order':['info: source 123','warn: pattern [0-9]','info: group 0','warn: occurrence 1'],
 'group-guard':['info: source 0','warn: pattern [','info: group 0.500000'],
 'occurrence-guard':['info: source a','warn: pattern none','info: group 0','warn: occurrence 0.500000'],
 'pattern-type-eager':['info: source a','warn: pattern false','info: group 0','warn: occurrence 0'],
 'none-eager':['info: source none','warn: pattern a','info: group 0','warn: occurrence 0'],
 'array-eager':['info: source [aba, none]','warn: pattern (a)','info: group 1','warn: occurrence 1'],
 'first-error-skips-pattern':[], 'pattern-error-retains-source':['info: source a'],
 'group-error-retains-pattern':['info: source a','warn: pattern a'],
 'occurrence-error-retains-group':['info: source a','warn: pattern a','info: group 0']}
none_calls={
 'group-guard':"(At(Array(LogInfo('source',0),Substr('é',0,1)),1),LogWarn('pattern','['),LogInfo('group',0.5),LogWarn('unreached',0))",
 'occurrence-guard':"(LogInfo('source','a'),LogWarn('pattern',None),LogInfo('group',0),LogWarn('occurrence',0.5))",
 'negative-occurrence-raw-source':"(Substr('é',0,1),'a',0,-1)",
 'negative-occurrence-invalid-pattern':"(Substr('é',0,1),'[',0,-1)",
 'negative-no-search':"("+quote('a'*2000+'!')+",'(a+)+$',0,-1)"}
for row in data['observations']:
 seen.append(str(row['count'])+':'+row['id']); out=pathlib.Path(row['stdout']).read_bytes(); err=pathlib.Path(row['stderr']).read_bytes(); expected=bytes.fromhex(row['eventsHex'])
 if row['id'] in ['budget-pattern-prepare-accepted','budget-pattern-prepare-rejected','budget-pattern-prepare-standalone']:
  standalone=row['id']=='budget-pattern-prepare-standalone'; rejection=row['id']=='budget-pattern-prepare-rejected'; length=102 if row['id']=='budget-pattern-prepare-accepted' else 104
  exact='@noopt '+('' if standalone else 'RegexCount('+quote('a'*5000)+",'z') == 0 and ")+'AllEqual(Array('+function+"('',"+quote('b'*length)+'),None))'
  assert row['expression']==exact and row['included'] and row['rejected']==rejection and expected==b'',row['id']
  assert row['diagnostic']==('65,536' if rejection else ''),row['id']
  assert pathlib.Path(row['filter']).read_text(encoding='utf-8-sig')=='def: exclude\nfe: '+exact+'\n',row['id']
 side_names=['left','right'] if row['count']==2 else ['left','middle','right']; assert len(row['inputRoots'])==row['count']
 protected={row['filter']}|{str(pathlib.Path(root)/name) for side,root in zip(side_names,row['inputRoots']) for name,value in row['expectedRows'].items() if side in value['present']}
 assert set(row['protectedBefore'])==set(row['protectedAfter'])==protected,row['id']
 assert row['protectedBefore']==row['protectedAfter'] and all(data['preserved'][p]==sha for p,sha in row['protectedBefore'].items()),row['id']
 if row['id'] in markers: assert expected==b''.join(line.encode()+nl for line in markers[row['id']]),row['id']
 if row['id'] in none_calls: assert not row['rejected'] and row['included'] and row['expression']=='AllEqual(Array('+function+none_calls[row['id']]+',None))',row['id']
 if row['id']=='function-case': assert row['expression']=='AllEqual(Array('+function.upper()+"('Aa','a'),"+('0' if find else "'A'")+'))',row['id']
 if row['nativeId']:
  g=golden[row['nativeId']]; c=next(c for c in selected if c['id']==row['nativeId']); pairs.append((row['nativeId'],row['count'])); assert row['rejected']==(g['value'] is None)
  call=function+'('+','.join(map(argument,c['args']))+')'; projection=call if g['value'] is None else 'AllEqual(Array('+call+','+expected_value(g['value'])+'))'
  assert row['expression']==('@cs ' if c['caseSensitive'] else '@ci ')+projection,row['id']
  if g['value'] is not None: assert row['included']
  else: assert ('継続byte' if g['exceptionType']=='RegularExpressionException' else '末尾').encode() in err,row['id']
 if row['id'] in ['raw-source-invalid-pattern','negative-occurrence-raw-source','negative-occurrence-invalid-pattern','negative-no-search']:
  assert not row['rejected'] and row['included'],row['id']
  if row['id']=='raw-source-invalid-pattern': assert row['expression']=='AllEqual(Array('+function+"(Substr('é',0,1),LogWarn('pattern','[')),None))" and expected==b'warn: pattern ['+nl,row['id']
 if row['rejected']:
  errors+=1; assert row['exitCode']==2 and not out and err.startswith(expected) and len(err)>len(expected),row['id']; assert row['diagnostic'] and row['diagnostic'].encode() in err,row['id']
  assert not any(line.startswith(x) for line in err[len(expected):].split(nl) for x in [b'info: ',b'warn: ',b'error: ']),row['id']
  if row['id'].startswith('invalid-'): assert row['diagnostic']==function and function.encode() in err and expected==b'',row['id']
 else:
  assert row['exitCode']==(1 if row['included'] else 0),row['id']
  if row['multiple']:
   lines=err.split(nl); expected_lines=[b'info: alpha.bin',b'warn: alpha.bin ^alpha',b'info: beta.bin',b'warn: beta.bin ^alpha']; assert lines[-1]==b'' and sorted(lines[:-1])==sorted(expected_lines),row['id']
   assert lines.index(expected_lines[0])<lines.index(expected_lines[1]) and lines.index(expected_lines[2])<lines.index(expected_lines[3]),row['id']
  else: assert err==expected,row['id']
  entries=json.loads(out)['entries']; expected_rows=row['expectedRows']; assert len(entries)==len(expected_rows) and {e['path'] for e in entries}==set(expected_rows),row['id']
  assert set(expected_rows)==({'alpha.bin','beta.bin'} if row['multiple'] else {'row.bin'}),row['id']; names={'left','right'} if row['count']==2 else {'left','middle','right'}
  for e in entries:
   x=expected_rows[e['path']]; present={side for side in ['left','middle','right'] if e.get(side) is not None}; assert present==set(x['present'])==({'left'} if row['missing'] else names),row['id']
   decision=e['path']=='alpha.bin' if row['multiple'] else row['included']; assert x['included']==decision and e['filtered']==(not decision),row['id']
   for side in present: assert e[side]['filtered']==(not decision),row['id']
 if row['id']=='timeout': assert row['duration']<10000,row['id']
assert len(pairs)==188 and set(pairs)=={(c['id'],n) for c in selected for n in [2,3]}
assert len(seen)==len(set(seen)) and seen==data['expectedCommands']
assert len(seen)==(332 if data['windowsProperties'] else 330)
assert errors==(60 if find else 62)
required=['budget-pattern-prepare-accepted','budget-pattern-prepare-rejected','budget-pattern-prepare-standalone','function-case', 'argument-order', 'group-guard', 'occurrence-guard', 'pattern-type-eager', 'none-eager', 'array-eager', 'array-pattern-type', 'array-group-type', 'empty-array-invalid-pattern', 'date-source', 'timestamp-source', 'raw-pattern-none', 'raw-source-pattern-type', 'none-skips-raw-pattern', 'raw-source-invalid-pattern', 'negative-occurrence-raw-source', 'negative-occurrence-invalid-pattern', 'negative-no-search', 'utf8-byte-offset', 'fourth-is-occurrence', 'empty-capture', 'unmatched-capture', 'negative-group', 'out-of-range-group', 'out-of-range-occurrence', 'folder-function', 'bare-size-snapshot', 'side-metadata', 'fullpath', 'lazy-and', 'lazy-or', 'lazy-if', 'logical-each', 'typed-log-result', 'budget-scalar-accepted', 'budget-pattern-expression-accepted', 'budget-array-accepted', 'budget-search-accepted', 'output-budget-accepted', 'raw-source', 'negative-group-raw-source', 'array-mid-failure', 'first-error-skips-pattern', 'pattern-error-retains-source', 'group-error-retains-pattern', 'occurrence-error-retains-group', 'budget-scalar-rejected', 'budget-pattern-expression-rejected', 'budget-array-rejected', 'budget-search-rejected', 'budget-shared', 'unsupported-han', 'timeout', 'invalid-zero', 'invalid-one', 'invalid-five', 'invalid-bare', 'invalid-empty', 'invalid-trailing', 'invalid-missing-close', 'invalid-empty-group', 'invalid-typed-regex', 'invalid-unselected', 'budget-individual', 'missing-side', 'multiple-row']
for n in [2,3]:
 for case in required: assert str(n)+':'+case in seen,case
 assert str(n)+':'+('output-budget-find-control' if find else 'output-budget-rejected') in seen
for path,sha in data['preserved'].items(): assert hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()==sha,path
print(json.dumps(dict(passed=True,function=function,nativeCases=122,publicBodies=94,nativeOnly=28,nativeProjections=len(pairs),commands=len(seen),expectedErrors=errors,preservedFiles=len(data['preserved']),interRowOrderQualified=False,cancellationInjected=False)))
""", new UTF8Encoding(false));
                await Reader(reader, inventory, oracle);
            }
        }

        async Task FolderListDependencyCommands(bool literal = false)
        {
            var function = literal ? "ReplaceWithList" : "RegexReplaceWithList";
            var prefix = literal ? "folder-literal-list-dependencies" : "folder-list-dependencies";
            var directory = Path.Combine(work, literal ? "literal-list-dependencies" : "list-dependencies"); Directory.CreateDirectory(directory);
            var records = new List<object>(); var preserved = new Dictionary<string, object>();
            static string Quote(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
            static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            void Preserve(string path) => preserved[path] = new { sha256 = Hash(path), writeTicks = File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture) };
            foreach (var id in new[] { "existing-list-output", "missing-list-output", "dynamic-first-output", "dynamic-second-output" })
            {
                var location = Path.Combine(directory, id); var left = Path.Combine(location, "left"); var right = Path.Combine(location, "right");
                Directory.CreateDirectory(left); Directory.CreateDirectory(right);
                var dynamic = id.StartsWith("dynamic", StringComparison.Ordinal); var missing = id.StartsWith("missing", StringComparison.Ordinal);
                var names = dynamic ? new[] { "one.bin", "two.bin" } : new[] { "a.bin", missing ? "missing.list" : "rules.list" };
                foreach (var name in names)
                {
                    File.WriteAllText(Path.Combine(left, name), "a\tY\n", new UTF8Encoding(false));
                    if (!missing || name == "a.bin") File.WriteAllText(Path.Combine(right, name), "a\tX\n", new UTF8Encoding(false));
                }
                var referenced = dynamic ? names.Select(name => Path.Combine(right, name)).ToArray() : [Path.Combine(right, names[1])];
                var pathExpression = dynamic ? "If(LeftName == 'one.bin'," + Quote(referenced[0]) + "," + Quote(referenced[1]) + ")" : Quote(referenced[0]);
                var filter = Path.Combine(location, "filter.flt"); File.WriteAllText(filter, "def: exclude\nfe: " + function + "('a'," + pathExpression + ") != None\n", new UTF8Encoding(false));
                foreach (var path in Directory.EnumerateFiles(location, "*", SearchOption.AllDirectories)) Preserve(path);
                List<string> args = ["--folder-sync", left, right, "--direction", "left-to-right", "--copy", "all", "--filter", filter];
                foreach (var name in dynamic ? [id.StartsWith("dynamic-first", StringComparison.Ordinal) ? names[0] : names[1]] : names) args.AddRange(["--select", name]);
                var result = await run(prefix + "-" + id, 2, false, args.ToArray());
                check("folder list CLI protects " + id, result.Stdout.Length == 0 && result.Stderr.Contains("置換リスト", StringComparison.Ordinal), result.Stderr);
                records.Add(new { id, command = result, referenced, missing, mutated = false, expectedList = (string?)null, listPath = (string?)null, writeTicks = (string?)null });
            }
            var staleExecuted = OperatingSystem.IsWindows() && observeStderr is not null;
            if (staleExecuted)
            {
                const string id = "same-size-restored-mtime-stale"; var commandName = prefix + "-stale";
                var location = Path.Combine(directory, id); var left = Path.Combine(location, "left"); var right = Path.Combine(location, "right");
                Directory.CreateDirectory(left); Directory.CreateDirectory(right);
                for (var index = 0; index < 1000; index++)
                { File.WriteAllBytes(Path.Combine(left, index.ToString("D4", CultureInfo.InvariantCulture) + ".bin"), [1]); File.WriteAllBytes(Path.Combine(right, index.ToString("D4", CultureInfo.InvariantCulture) + ".bin"), [2]); }
                var list = Path.Combine(location, "rules.list"); File.WriteAllText(list, "a\tX\n", new UTF8Encoding(false));
                var time = new DateTime(2002, 3, 4, 5, 6, 7, DateTimeKind.Utc); File.SetLastWriteTimeUtc(list, time);
                var filter = Path.Combine(location, "filter.flt");
                File.WriteAllText(filter, "def: exclude\nfe: LogInfo('FOLDER-LIST-SNAPSHOT'," + function + "('a'," + Quote(list) + ")) != None\n", new UTF8Encoding(false));
                foreach (var path in Directory.EnumerateFiles(location, "*", SearchOption.AllDirectories).Where(path => path != list)) Preserve(path);
                var observed = new StringBuilder(); var attempted = false; var mutated = false; string? mutationError = null;
                observeStderr!(commandName, chunk =>
                {
                    observed.Append(chunk);
                    if (attempted || !observed.ToString().Contains("FOLDER-LIST-SNAPSHOT X", StringComparison.Ordinal)) return;
                    attempted = true;
                    for (var attempt = 0; attempt < 20; attempt++)
                    {
                        try { File.WriteAllText(list, "a\tY\n", new UTF8Encoding(false)); File.SetLastWriteTimeUtc(list, time); mutated = true; return; }
                        catch (IOException error) { mutationError = error.Message; Thread.Sleep(2); }
                    }
                });
                CommandResult result;
                try { result = await run(commandName, 2, false, ["--folder-sync", left, right, "--direction", "left-to-right", "--copy", "all", "--filter", filter, "--select", "0000.bin"]); }
                finally { observeStderr(commandName, null); }
                check("folder list CLI stale external snapshot rejected", attempted && mutated && result.Stdout.Length == 0
                    && result.Stderr.Contains("置換リストが変わりました", StringComparison.Ordinal) && File.GetLastWriteTimeUtc(list) == time, result.Stderr + mutationError);
                records.Add(new { id, command = result, referenced = new[] { list }, missing = false, mutated, expectedList = "6109590a", listPath = list, writeTicks = time.Ticks.ToString(CultureInfo.InvariantCulture) });
            }
            else skip("folder list CLI live stderr mutation", "Windowsの登録済み命令だけにstream observerを使用。macOS startup gateは変更せず、GUIの同期hookによるstale検査と区別する。");
            {
                const string id = "staging-list-change";
                var location = Path.Combine(directory, id); var left = Path.Combine(location, "left"); var right = Path.Combine(location, "right");
                Directory.CreateDirectory(left); Directory.CreateDirectory(right);
                var source = Path.Combine(left, "payload.bin"); var destination = Path.Combine(right, "payload.bin");
                const long sourceBytes = 128L * 1024 * 1024;
                using (var stream = File.Create(source))
                { var block = Enumerable.Repeat((byte)0x71, 1024 * 1024).ToArray(); for (var index = 0; index < 128; index++) stream.Write(block); }
                File.WriteAllBytes(destination, [0x4f, 0x4c, 0x44]);
                var list = Path.Combine(location, "rules.list"); File.WriteAllText(list, "a\tX\n", new UTF8Encoding(false));
                var time = new DateTime(2002, 3, 4, 5, 6, 7, DateTimeKind.Utc); File.SetLastWriteTimeUtc(list, time);
                var filter = Path.Combine(location, "filter.flt");
                File.WriteAllText(filter, "def: exclude\nfe: " + function + "('a'," + Quote(list) + ") != None\n", new UTF8Encoding(false));
                foreach (var path in new[] { source, destination, filter }) Preserve(path);
                string? temporary = null; long stagedBytes = 0; DateTime? stagedUtc = null; var mutated = false;
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var observer = Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        foreach (var path in Directory.EnumerateFiles(right, ".diffbeacon-copy-*.tmp"))
                        {
                            try
                            {
                                var length = new FileInfo(path).Length;
                                if (length <= 0 || length >= sourceBytes) continue;
                                File.WriteAllText(list, "a\tY\n", new UTF8Encoding(false)); File.SetLastWriteTimeUtc(list, time);
                                temporary = path; stagedBytes = length; stagedUtc = DateTime.UtcNow; mutated = true; return;
                            }
                            catch (IOException) { }
                        }
                        try { await Task.Delay(1, stop.Token); } catch (OperationCanceledException) { return; }
                    }
                });
                CommandResult result;
                try { result = await run(prefix + "-staging", 2, false, ["--folder-sync", left, right, "--direction", "left-to-right", "--copy", "all", "--filter", filter, "--select", "payload.bin"]); }
                finally { stop.Cancel(); await observer; }
                using var output = JsonDocument.Parse(result.Stdout.Length > 0 ? result.Stdout : "{}");
                var rejected = output.RootElement.TryGetProperty("succeeded", out var succeeded) && !succeeded.GetBoolean()
                    && output.RootElement.GetProperty("published").GetInt32() == 0 && output.RootElement.GetProperty("reason").GetString()!.Contains("置換リスト", StringComparison.Ordinal);
                check("folder list CLI revalidates during staging before publication", mutated && stagedBytes is > 0 and < sourceBytes && rejected
                    && File.ReadAllBytes(destination).SequenceEqual(new byte[] { 0x4f, 0x4c, 0x44 }), result.Stdout + result.Stderr);
                records.Add(new { id, command = result, referenced = new[] { list }, missing = false, mutated, expectedList = "6109590a", listPath = list,
                    writeTicks = time.Ticks.ToString(CultureInfo.InvariantCulture), temporary, stagedBytes, stagedUtc, sourceBytes, destination });
            }
            var inventory = Path.Combine(directory, "observations.json");
            await File.WriteAllTextAsync(inventory, JsonSerializer.Serialize(new { records, preserved, staleExecuted }));
            var reader = Path.Combine(directory, "verify-list-dependencies.py");
            await File.WriteAllTextAsync(reader, """
import hashlib,json,pathlib,sys
d=json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8')); rows=d['records']
assert [r['id'] for r in rows]==['existing-list-output','missing-list-output','dynamic-first-output','dynamic-second-output']+(['same-size-restored-mtime-stale'] if d['staleExecuted'] else [])+['staging-list-change']
for r in rows:
 c=r['command']; assert c['ExitCode']==2 and c['Pid']>0 and c['CreationUtc'] and c['ExitObservedUtc'],r['id']
 if r['id']=='staging-list-change':
  out=json.loads(c['Stdout']); assert not out['succeeded'] and out['published']==0 and isinstance(out['mutationOccurred'],bool) and '置換リスト' in out['reason']
  p=pathlib.Path(r['temporary']); assert p.parent==pathlib.Path(r['destination']).parent and p.name.startswith('.diffbeacon-copy-') and p.suffix=='.tmp' and not p.exists()
  assert r['stagedUtc'] and 0<r['stagedBytes']<r['sourceBytes']==134217728 and pathlib.Path(r['destination']).read_bytes()==b'OLD'
 else: assert c['Stdout']=='' and '置換リスト' in c['Stderr'],r['id']
 if r['missing']: assert len(r['referenced'])==1 and not pathlib.Path(r['referenced'][0]).exists(),r['id']
 if r['id'].startswith('dynamic'): assert len(r['referenced'])==2 and all(pathlib.Path(p).read_bytes()==b'a\tX\n' for p in r['referenced'])
 if r['mutated']:
  p=pathlib.Path(r['listPath']); assert p.read_bytes()==bytes.fromhex(r['expectedList'])==b'a\tY\n' and p.stat().st_size==4
  assert p.stat().st_mtime_ns//100==int(r['writeTicks'])-621355968000000000
  if r['id']!='staging-list-change': assert 'FOLDER-LIST-SNAPSHOT X' in c['Stderr'] and '置換リストが変わりました' in c['Stderr']
for p,expected in d['preserved'].items():
 p=pathlib.Path(p); assert hashlib.sha256(p.read_bytes()).hexdigest()==expected['sha256']
 assert p.stat().st_mtime_ns//100==int(expected['writeTicks'])-621355968000000000
print(json.dumps(dict(passed=True,commands=len(rows),preservedFiles=len(d['preserved']),liveStale=d['staleExecuted'],headlessHashOnlyCheckpointSeparate=True)))
""", new UTF8Encoding(false));
            var start = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-E", "-B", "-X", "utf8", reader, inventory }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); var evidence = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(directory, "reader-result.json"), evidence);
            check("folder list CLI independent dependency reader", process.ExitCode == 0, evidence);
        }

        async Task ReplaceValues()
        {
            var directory = Path.Combine(work, "replace"); Directory.CreateDirectory(directory);
            static string Quote(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            static byte[] Bytes(JsonElement value, string family) => family == "folder" ? Convert.FromHexString(value.GetProperty("value").GetString()!)
                : value.GetProperty("type").GetString() == "raw-string" ? value.GetProperty("value").EnumerateArray().Select(item => item.GetByte()).ToArray()
                : Encoding.UTF8.GetBytes(value.GetProperty("value").GetString()!);
            static string ByteArgument(byte[] bytes)
            {
                try { return Quote(new UTF8Encoding(false, true).GetString(bytes)); }
                catch (DecoderFallbackException)
                {
                    var carrier = Encoding.UTF8.GetBytes("é中😀Z"); var offset = carrier.AsSpan().IndexOf(bytes.AsSpan());
                    if (offset < 0) throw new InvalidDataException("公開構文で構成しないraw bytes");
                    return "Substr('é中😀Z'," + offset.ToString(CultureInfo.InvariantCulture) + "," + bytes.Length.ToString(CultureInfo.InvariantCulture) + ")";
                }
            }
            static JsonElement.ArrayEnumerator Items(JsonElement value, string family) => value.GetProperty(family == "folder" ? "items" : "value").EnumerateArray();
            static bool HasType(JsonElement value, string family, params string[] types) => types.Contains(value.GetProperty("type").GetString()!, StringComparer.Ordinal)
                || value.GetProperty("type").GetString() == "array" && Items(value, family).Any(item => HasType(item, family, types));
            static bool PublicArgument(JsonElement value, string family)
            {
                var type = value.GetProperty("type").GetString();
                if (type == "array") return Items(value, family).All(item => PublicArgument(item, family));
                if (type is "string" or "raw-string")
                { try { _ = ByteArgument(Bytes(value, family)); return true; } catch (InvalidDataException) { return false; } }
                return type is "none" or "int64" or "integer" or "double" or "bool";
            }
            static string Exclusion(JsonElement row, string family)
            {
                if (row.TryGetProperty("mode", out var mode) && mode.GetString() != "function-body-noopt") return "optimizer-selected-branch";
                if (row.TryGetProperty("nullArgs", out var nullArgs) && nullArgs.GetBoolean()) return "null-args-adapter";
                if (row.GetProperty("args").GetArrayLength() != 3) return "native-arity-public-parser-rejects";
                var args = row.GetProperty("args").EnumerateArray().ToArray();
                if (row.TryGetProperty("throws", out var throws) && throws.GetArrayLength() != 0 || args.Any(value => HasType(value, family, "throw"))) return "native-throw-node";
                if (args.Any(value => HasType(value, family, "regex", "lazy-regex"))) return "typed-regex-not-public";
                return args.All(value => PublicArgument(value, family)) ? "" : "raw-FF-not-publicly-constructible";
            }
            static string Argument(JsonElement value, string family) => value.GetProperty("type").GetString() switch
            {
                "string" or "raw-string" => ByteArgument(Bytes(value, family)), "none" => "None",
                "int64" or "integer" or "double" => value.GetProperty("value").GetRawText(), "bool" => value.GetProperty("value").GetBoolean() ? "true" : "false",
                "array" => "Array(" + string.Join(',', Items(value, family).Select(item => Argument(item, family))) + ")", _ => throw new InvalidDataException("Native-only Replace argument")
            };
            static string Expected(JsonElement value, string family) => value.GetProperty(family == "folder" ? "type" : "kind").GetString() switch
            {
                "none" => "None", "string" => ByteArgument(Convert.FromHexString(value.GetProperty(family == "folder" ? "value" : "utf8Hex").GetString()!)),
                "array" => "Array(" + string.Join(',', value.GetProperty("items").EnumerateArray().Select(item => Expected(item, family))) + ")",
                _ => throw new InvalidDataException("Native-only Replace result")
            };
            static string Same(string call, string expected) => "AllEqual(Array(" + call + "," + expected + "))";
            async Task Reader(string label, string script, params string[] arguments)
            {
                var start = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var argument in new[] { "-E", "-B", "-X", "utf8", script }.Concat(arguments)) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); var evidence = await stdout + await stderr;
                await File.WriteAllTextAsync(Path.Combine(directory, label + "-result.json"), evidence);
                check("folder Replace independent " + label, process.ExitCode == 0, evidence);
            }
            var folderOracle = Path.GetFullPath("tests/Fixtures/FolderReplaceOracle"); var lineOracle = Path.GetFullPath("tests/Fixtures/LineTransformReplace");
            await Reader("folder-native-oracle", Path.Combine(folderOracle, "verify.py"));
            await Reader("line-native-oracle", Path.Combine(lineOracle, "verify.py"), "--verify");
            var partition = new List<object>(); var selected = new List<(string Family, JsonElement Case, JsonElement Expected)>();
            foreach (var (family, oracle) in new[] { ("folder", folderOracle), ("line", lineOracle) })
            {
                using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(oracle, "cases.json")));
                var golden = File.ReadAllLines(Path.Combine(oracle, "golden.ndjson")).Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
                    .ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
                foreach (var row in cases.RootElement.EnumerateArray())
                {
                    var id = row.GetProperty("id").GetString()!; var exclusion = Exclusion(row, family); var fixedValue = golden[id];
                    if (family == "folder")
                    { using var payload = JsonDocument.Parse(Convert.FromHexString(fixedValue.GetProperty("stdoutHex").GetString()!)); fixedValue = payload.RootElement.Clone(); }
                    partition.Add(new { family, id, exclusion, scope = exclusion.Length == 0 ? "fixed-native-public-projection" : "native-only", native = fixedValue });
                    if (exclusion.Length == 0) selected.Add((family, row.Clone(), fixedValue.GetProperty(family == "folder" ? "result" : "value").Clone()));
                }
            }
            check("folder Replace fixed public partition", partition.Count == 369 && selected.Count(item => item.Family == "folder") == 103 && selected.Count(item => item.Family == "line") == 72,
                "Folder269=103 public+166 native-only; Line100=72 public+28 native-only; raw Substr projections retain exact native bytes");
            var observations = new List<object>(); var preserved = new Dictionary<string, string>();
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(directory, "sides-" + count, "side-" + side)).ToArray();
                foreach (var (side, root) in roots.Select((root, side) => (side, root)))
                { Directory.CreateDirectory(root); var path = Path.Combine(root, "row.bin"); File.WriteAllBytes(path, Enumerable.Repeat((byte)(side + 1), side + 1).ToArray()); preserved[path] = Hash(path); }
                async Task Compare(string id, string expression, string? family = null, string? nativeId = null, string events = "", bool rejected = false, string diagnostic = "", bool included = true)
                {
                    // 固定原典と補助境界は関数本体の契約。optimizer専用行は選択前に除外する。
                    expression = "@noopt " + expression;
                    var filter = Filter("replace-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"); preserved[filter] = Hash(filter);
                    // 拒否時は実syncの公開前に失敗させ、既存コピー先も保護する。
                    List<string> args = rejected ? ["--folder-sync", roots[0], roots[^1], "--direction", "left-to-right", "--copy", "all", "--select", "row.bin", "--filter", filter]
                        : ["--folder", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) args.AddRange(["--middle", roots[1]]);
                    var before = roots.Select(root => Path.Combine(root, "row.bin")).Append(filter).ToDictionary(path => path, Hash, StringComparer.Ordinal);
                    var result = await run("folder-replace-" + count + "-" + id, rejected ? 2 : included ? 1 : 0, !rejected, args.ToArray());
                    var stdout = Path.Combine(directory, count + "-" + id + ".stdout"); var stderr = Path.Combine(directory, count + "-" + id + ".stderr");
                    await File.WriteAllTextAsync(stdout, result.Stdout, new UTF8Encoding(false)); await File.WriteAllTextAsync(stderr, result.Stderr, new UTF8Encoding(false));
                    check("folder Replace diagnostics " + count + " " + id, rejected ? result.Stdout.Length == 0 && result.Stderr.StartsWith(events, StringComparison.Ordinal)
                        && result.Stderr.Contains(diagnostic, StringComparison.Ordinal) && !result.Stderr.Contains("UNREACHED", StringComparison.Ordinal) : result.Stderr == events, result.Stderr);
                    if (!rejected)
                    {
                        using var model = JsonDocument.Parse(result.Stdout); var entry = model.RootElement.GetProperty("entries").EnumerateArray().Single();
                        check("folder Replace inclusion " + count + " " + id, entry.GetProperty("path").GetString() == "row.bin" && entry.GetProperty("filtered").GetBoolean() == !included, result.Stdout);
                    }
                    check("folder Replace inputs and existing output preserved " + count + " " + id, before.All(pair => File.Exists(pair.Key) && Hash(pair.Key) == pair.Value), id);
                    observations.Add(new { id, count, expression, family, nativeId, events, rejected, diagnostic, included, arguments = args, protectedBefore = before,
                        existingOutput = Path.Combine(roots[^1], "row.bin"), scope = nativeId is null ? "derived-public-boundary" : "fixed-native-public-projection", exitCode = result.ExitCode, stdout, stderr });
                }
                foreach (var item in selected)
                {
                    var id = item.Case.GetProperty("id").GetString()!;
                    var call = "Replace(" + string.Join(',', item.Case.GetProperty("args").EnumerateArray().Select(value => Argument(value, item.Family))) + ")";
                    await Compare(item.Family + "-" + id, (item.Case.GetProperty("caseSensitive").GetBoolean() ? "@cs " : "@ci ") + Same(call, Expected(item.Expected, item.Family)), item.Family, id);
                }
                var nl = Environment.NewLine;
                foreach (var (id, expression, events) in new[]
                {
                    ("argument-order", Same("Replace(LogInfo('source','aba'),LogWarn('from','a'),LogInfo('to','X'))", "'XbX'"), "info: source aba" + nl + "warn: from a" + nl + "info: to X" + nl),
                    ("none-eager", Same("Replace(LogInfo('source',None),LogWarn('from','a'),LogInfo('to','X'))", "None"), "info: source none" + nl + "warn: from a" + nl + "info: to X" + nl),
                    ("invalid-from-eager", Same("Replace(LogInfo('source','a'),LogWarn('from',1),LogInfo('to','X'))", "None"), "info: source a" + nl + "warn: from 1" + nl + "info: to X" + nl),
                    ("invalid-to-eager", Same("Replace(LogInfo('source','a'),LogWarn('from','a'),LogInfo('to',1))", "None"), "info: source a" + nl + "warn: from a" + nl + "info: to 1" + nl),
                    ("empty-array-empty-from", Same("Replace(Array(),'','X')", "Array()"), ""),
                    ("empty-array-invalid-from", Same("Replace(Array(),1,'X')", "None"), ""),
                    ("empty-array-invalid-to", Same("Replace(Array(),'a',1)", "None"), ""),
                    ("shallow-array", Same("Replace(Array('aba',None,123,true,Array('a')),'a','X')", "Array('XbX',None,'123','true','[X]')"), ""),
                    ("raw-source-none", Same("Replace(Substr('é',0,1),'a','X')", "None"), ""),
                    ("raw-from-none", Same("Replace('a',Substr('é',0,1),'X')", "None"), ""),
                    ("raw-to-match", Same("Replace('a','a',Substr('é',0,1))", "Substr('é',0,1)"), ""),
                    ("raw-to-no-match", Same("Replace('b','a',Substr('é',0,1))", "'b'"), ""),
                    ("raw-to-empty-source", Same("Replace('','a',Substr('é',0,1))", "''"), ""),
                    ("side-context", Same("Replace(LeftSize,'1','L')", "'L'") + " and " + Same("Replace('row.bin',LeftName,'X')", "'X'") + " and " + Same("Replace('a','a',RightName)", "'row.bin'")
                        + (count == 3 ? " and " + Same("Replace(MiddleSize,'2','M')", "'M'") : ""), ""),
                    ("fullpath-source", Same("Replace(FullPath,'row.bin','ROW')", "Array(" + string.Join(',', roots.Select(root => Quote(Path.Combine(root,"ROW")))) + ")"), ""),
                    ("fullpath-to", Same("Replace('a','a',At(FullPath,0))", Quote(Path.Combine(roots[0],"row.bin"))), ""),
                    ("function-case", Same("rEpLaCe('a.a','.','X')", "'aXa'"), ""),
                    ("budget-accepted", Same("Replace(" + Quote(new string('a',1000)) + ",'z','X')", Quote(new string('a',1000))), "")
                }) await Compare(id, expression, events: events);
                await Compare("negative-control", Same("Replace('a.a','.','X')", "'XXX'"), included: false);
                if (OperatingSystem.IsWindows()) await Compare("property-context", Same("Replace(Prop('System.Size'),'1','L')", count == 2 ? "Array('L','2')" : "Array('L','2','3')")
                    + " and " + Same("Replace('a','a',LeftProp('System.ItemNameDisplay'))", "'row.bin'"));
                var expensive = "ToLower(ToLower(" + Quote(new string('A',14000)) + "))";
                await Compare("source-failure", "Replace(" + expensive + ",LogWarn('UNREACHED'),'X')", rejected: true, diagnostic: "65,536");
                await Compare("from-failure", "Replace(LogInfo('source','a')," + expensive + ",LogWarn('UNREACHED'))", events: "info: source a" + nl, rejected: true, diagnostic: "65,536");
                await Compare("to-failure", "Replace(LogInfo('source','a'),LogWarn('from','a')," + expensive + ")", events: "info: source a" + nl + "warn: from a" + nl, rejected: true, diagnostic: "65,536");
                await Compare("budget-shared", "Replace(Replace(" + Quote(new string('a',8000)) + ",'z','X'),'z','X')", rejected: true, diagnostic: "65,536");
                await Compare("budget-output", "Replace('aa','a'," + Quote(new string('X',8000)) + ")", rejected: true, diagnostic: "65,536");
                foreach (var (id, expression) in new[] { ("arity-zero", "Replace()"), ("arity-one", "Replace(LogInfo('UNREACHED'))"), ("arity-two", "Replace(LogInfo('UNREACHED'),'a')"),
                    ("arity-four", "Replace(LogInfo('UNREACHED'),'a','X',1)"), ("arity-bare", "Replace"), ("arity-lazy", "If(false,Replace(),true)") })
                    await Compare(id, expression, rejected: true, diagnostic: "Replace");
            }
            var inventory = Path.Combine(directory, "observations.json");
            await File.WriteAllTextAsync(inventory, JsonSerializer.Serialize(new { partition, observations, preserved, windowsProperties = OperatingSystem.IsWindows(), typedRegexPublicBinding = false,
                nativeOptimizer = false, evaluationPhase = "function-body-noopt", cancellationInjected = false, nativeThrowInjection = false }));
            var reader = Path.Combine(directory, "verify-folder-replace.py");
            await File.WriteAllTextAsync(reader, """
import hashlib,json,pathlib,sys,os
inventory=pathlib.Path(sys.argv[1]); d=json.loads(inventory.read_text(encoding='utf-8'))
assert d['evaluationPhase']=='function-body-noopt'
roots={'folder':pathlib.Path(sys.argv[2]),'line':pathlib.Path(sys.argv[3])}
sha={('folder','cases.json'):'16EB1C6FA43FF0C3611B3F2FA05BE73AB5230BEF7CE6F2D5B6D8EAA9137E2036',('folder','golden.ndjson'):'45CE59815FA8BF5475156B3DEFD58207B3A89D6BC43F220B4AE40195E8C6DBBC',('line','cases.json'):'A0FE11284DC6B012F8EEAD24639FDE753EAF416E1FC7E878465AB0C70C32A42E',('line','golden.ndjson'):'D7105FE61AE209C2D53833731683933EBAF4D9EFBA89359947999F96E05E9C65'}
for (family,name),expected_sha in sha.items(): assert hashlib.sha256((roots[family]/name).read_bytes()).hexdigest().upper()==expected_sha

def quote(v): return "'"+v.replace('\\','\\\\').replace("'","\\'").replace('\r','\\r').replace('\n','\\n').replace('\t','\\t')+"'"
def bytearg(data):
 try: return quote(data.decode('utf-8'))
 except UnicodeDecodeError:
  offset='é中😀Z'.encode().find(data)
  if offset<0: raise ValueError('unconstructible bytes')
  return "Substr('é中😀Z',"+str(offset)+','+str(len(data))+')'
def data(v,f): return bytes.fromhex(v['value']) if f=='folder' else bytes(v['value']) if v['type']=='raw-string' else v['value'].encode('utf-8')
def items(v,f): return v['items' if f=='folder' else 'value']
def has(v,f,types): return v['type'] in types or v['type']=='array' and any(has(x,f,types) for x in items(v,f))
def public(v,f):
 t=v['type']
 if t=='array': return all(public(x,f) for x in items(v,f))
 if t in ['string','raw-string']:
  try: bytearg(data(v,f)); return True
  except ValueError: return False
 return t in ['none','int64','integer','double','bool']
def exclusion(c,f):
 if c.get('mode','function-body-noopt')!='function-body-noopt': return 'optimizer-selected-branch'
 if c.get('nullArgs',False): return 'null-args-adapter'
 if len(c['args'])!=3: return 'native-arity-public-parser-rejects'
 if c.get('throws',[]) or any(has(a,f,['throw']) for a in c['args']): return 'native-throw-node'
 if any(has(a,f,['regex','lazy-regex']) for a in c['args']): return 'typed-regex-not-public'
 return '' if all(public(a,f) for a in c['args']) else 'raw-FF-not-publicly-constructible'
def arg(v,f):
 t=v['type']
 if t in ['string','raw-string']: return bytearg(data(v,f))
 if t=='none': return 'None'
 if t=='bool': return str(v['value']).lower()
 if t=='array': return 'Array('+','.join(arg(x,f) for x in items(v,f))+')'
 return str(v['value'])
def expected(v,f):
 t=v['type' if f=='folder' else 'kind']
 if t=='none': return 'None'
 if t=='string': return bytearg(bytes.fromhex(v['value' if f=='folder' else 'utf8Hex']))
 assert t=='array'
 return 'Array('+','.join(expected(x,f) for x in v['items'])+')'
def same(call,value): return 'AllEqual(Array('+call+','+value+'))'
all_cases={};native={};selected={};excluded={}
for family,root in roots.items():
 cases=json.loads((root/'cases.json').read_text(encoding='utf-8'))
 golden={r['id']:(json.loads(bytes.fromhex(r['stdoutHex']).decode('utf-8')) if family=='folder' else r) for r in map(json.loads,(root/'golden.ndjson').read_text(encoding='utf-8').splitlines())}
 assert len(cases)==(269 if family=='folder' else 100)
 for c in cases:
  key=(family,c['id']); all_cases[key]=c; native[key]=golden[c['id']]; reason=exclusion(c,family)
  if reason: excluded[key]=reason
  else:
   selected[key]=c
   if family=='folder': assert golden[c['id']]['status']=='ok' and golden[c['id']]['trace']==[0,1,2]
   else: assert golden[c['id']]['errorUtf8Hex']=='' and golden[c['id']]['evaluated']==[0,1,2]
assert len([k for k in selected if k[0]=='folder'])==103 and len([k for k in selected if k[0]=='line'])==72
assert len(d['partition'])==369 and len(excluded)==194
partition_seen=set()
for row in d['partition']:
 key=(row['family'],row['id']); assert key not in partition_seen; partition_seen.add(key)
 assert row['native']==native[key] and row['exclusion']==excluded.get(key,'')
 assert row['scope']==('native-only' if key in excluded else 'fixed-native-public-projection')
assert partition_seen==set(all_cases)
assert not any(d[k] for k in ['typedRegexPublicBinding','nativeOptimizer','cancellationInjected','nativeThrowInjection'])

def boundaries(count,side_roots):
 nl=os.linesep
 rows={
 'argument-order':(same("Replace(LogInfo('source','aba'),LogWarn('from','a'),LogInfo('to','X'))","'XbX'"),'info: source aba'+nl+'warn: from a'+nl+'info: to X'+nl),
 'none-eager':(same("Replace(LogInfo('source',None),LogWarn('from','a'),LogInfo('to','X'))","None"),'info: source none'+nl+'warn: from a'+nl+'info: to X'+nl),
 'invalid-from-eager':(same("Replace(LogInfo('source','a'),LogWarn('from',1),LogInfo('to','X'))","None"),'info: source a'+nl+'warn: from 1'+nl+'info: to X'+nl),
 'invalid-to-eager':(same("Replace(LogInfo('source','a'),LogWarn('from','a'),LogInfo('to',1))","None"),'info: source a'+nl+'warn: from a'+nl+'info: to 1'+nl),
 'empty-array-empty-from':(same("Replace(Array(),'','X')","Array()"),''),
 'empty-array-invalid-from':(same("Replace(Array(),1,'X')","None"),''),
 'empty-array-invalid-to':(same("Replace(Array(),'a',1)","None"),''),
 'shallow-array':(same("Replace(Array('aba',None,123,true,Array('a')),'a','X')","Array('XbX',None,'123','true','[X]')"),''),
 'raw-source-none':(same("Replace(Substr('é',0,1),'a','X')","None"),''),
 'raw-from-none':(same("Replace('a',Substr('é',0,1),'X')","None"),''),
 'raw-to-match':(same("Replace('a','a',Substr('é',0,1))","Substr('é',0,1)"),''),
 'raw-to-no-match':(same("Replace('b','a',Substr('é',0,1))","'b'"),''),
 'raw-to-empty-source':(same("Replace('','a',Substr('é',0,1))","''"),''),
 'side-context':(same("Replace(LeftSize,'1','L')","'L'")+' and '+same("Replace('row.bin',LeftName,'X')","'X'")+' and '+same("Replace('a','a',RightName)","'row.bin'")+(' and '+same("Replace(MiddleSize,'2','M')","'M'") if count==3 else ''),''),
 'fullpath-source':(same("Replace(FullPath,'row.bin','ROW')",'Array('+','.join(quote(str(root/'ROW')) for root in side_roots)+')'),''),
 'fullpath-to':(same("Replace('a','a',At(FullPath,0))",quote(str(side_roots[0]/'row.bin'))),''),
 'function-case':(same("rEpLaCe('a.a','.','X')","'aXa'"),''),
 'budget-accepted':(same('Replace('+quote('a'*1000)+",'z','X')",quote('a'*1000)),''),
 'negative-control':(same("Replace('a.a','.','X')","'XXX'"),'')}
 accepted={key:(expr,events,False,'',key!='negative-control') for key,(expr,events) in rows.items()}
 if d['windowsProperties']: accepted['property-context']=(same("Replace(Prop('System.Size'),'1','L')","Array('L','2')" if count==2 else "Array('L','2','3')")+' and '+same("Replace('a','a',LeftProp('System.ItemNameDisplay'))","'row.bin'"),'',False,'',True)
 expensive='ToLower(ToLower('+quote('A'*14000)+'))'
 rejections={
 'source-failure':('Replace('+expensive+",LogWarn('UNREACHED'),'X')",'', '65,536'),
 'from-failure':("Replace(LogInfo('source','a'),"+expensive+",LogWarn('UNREACHED'))",'info: source a'+nl, '65,536'),
 'to-failure':("Replace(LogInfo('source','a'),LogWarn('from','a'),"+expensive+')','info: source a'+nl+'warn: from a'+nl, '65,536'),
 'budget-shared':('Replace(Replace('+quote('a'*8000)+",'z','X'),'z','X')",'', '65,536'),
 'budget-output':("Replace('aa','a',"+quote('X'*8000)+')','', '65,536'),
 'arity-zero':('Replace()','', 'Replace'), 'arity-one':("Replace(LogInfo('UNREACHED'))",'', 'Replace'),
 'arity-two':("Replace(LogInfo('UNREACHED'),'a')",'', 'Replace'), 'arity-four':("Replace(LogInfo('UNREACHED'),'a','X',1)",'', 'Replace'),
 'arity-bare':('Replace','', 'Replace'), 'arity-lazy':('If(false,Replace(),true)','', 'Replace')}
 accepted.update({key:(expr,events,True,diagnostic,True) for key,(expr,events,diagnostic) in rejections.items()})
 return accepted
seen=set();projections=set();rejected=0
for row in d['observations']:
 count=row['count']; key=(count,row['id']); assert count in [2,3] and key not in seen;seen.add(key)
 side_roots=[inventory.parent/('sides-'+str(count))/('side-'+str(side)) for side in range(count)]
 if row['nativeId'] is not None:
  native_key=(row['family'],row['nativeId']); c=selected[native_key];projections.add((count,*native_key));value=native[native_key]['result' if row['family']=='folder' else 'value']
  assert row['id']==row['family']+'-'+row['nativeId'] and row['scope']=='fixed-native-public-projection'
  assert row['expression']=='@noopt '+('@cs ' if c['caseSensitive'] else '@ci ')+same('Replace('+','.join(arg(v,row['family']) for v in c['args'])+')',expected(value,row['family']))
  assert row['events']=='' and not row['rejected'] and row['included'] and row['diagnostic']==''
 else:
  assert row['family'] is None and row['scope']=='derived-public-boundary'
  wanted=boundaries(count,side_roots)[row['id']]
  assert (row['expression'],row['events'],row['rejected'],row['diagnostic'],row['included'])==('@noopt '+wanted[0],*wanted[1:]),key
 filter_path=inventory.parent.parent/('replace-'+str(count)+'-'+row['id']+'.flt')
 # BOMだけを読込み時に除き、埋込みNULを含むfilter本文全体を照合する。
 assert filter_path.read_bytes().decode('utf-8-sig')=='def: exclude\nfe: '+row['expression']+'\n'
 args=['--folder-sync',str(side_roots[0]),str(side_roots[-1]),'--direction','left-to-right','--copy','all','--select','row.bin','--filter',str(filter_path)] if row['rejected'] else ['--folder',str(side_roots[0]),str(side_roots[-1]),'--filter',str(filter_path),'--show-filtered']
 if count==3: args+=['--middle',str(side_roots[1])]
 assert row['arguments']==args
 out=pathlib.Path(row['stdout']).read_bytes();err=pathlib.Path(row['stderr']).read_bytes();events=row['events'].encode()
 if row['rejected']:
  rejected+=1;assert row['exitCode']==2 and not out and err.startswith(events) and len(err)>len(events) and row['diagnostic'].encode() in err and b'UNREACHED' not in err
 else:
  assert row['exitCode']==(1 if row['included'] else 0) and err==events
  entries=json.loads(out)['entries'];assert len(entries)==1 and entries[0]['path']=='row.bin' and entries[0]['filtered']==(not row['included'])
  for side in (['left','right'] if count==2 else ['left','middle','right']):assert entries[0][side] is not None and entries[0][side]['filtered']==(not row['included'])
 assert set(row['protectedBefore'])=={str(root/'row.bin') for root in side_roots}|{str(filter_path)}
 for path,sha in row['protectedBefore'].items():assert hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()==sha
 assert row['existingOutput']==str(side_roots[-1]/'row.bin') and pathlib.Path(row['existingOutput']).read_bytes()==bytes([count])*count
assert projections=={(count,*key) for count in [2,3] for key in selected} and len(projections)==350
required={(count,family+'-'+native_id) for count in [2,3] for family,native_id in selected}
for count in [2,3]:
 side_roots=[inventory.parent/('sides-'+str(count))/('side-'+str(side)) for side in range(count)]
 required|={(count,key) for key in boundaries(count,side_roots)}
assert seen==required
for path,sha in d['preserved'].items():assert hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()==sha
print(json.dumps(dict(passed=True,fixedNativeCases=369,publicCases=175,nativeOnly=194,resultProjections=len(projections),commands=len(seen),expectedRejections=rejected,typedRegexPublicBinding=False,nativeOptimizer=False,cancellationInjected=False,nativeThrowInjection=False)))
""", new UTF8Encoding(false));
            await Reader("folder-reader", reader, inventory, folderOracle, lineOracle);
        }

        async Task RegexReplaceListValues()
        {
            var oracle = Path.GetFullPath("tests/Fixtures/LineTransformReplaceLists");
            var directory = Path.Combine(work, "regexreplacelist"); Directory.CreateDirectory(directory);
            static string LocateFixture(string relativePath)
            {
                foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
                    for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
                    {
                        var candidate = Path.GetFullPath(Path.Combine(directory.FullName, relativePath));
                        if (File.Exists(Path.Combine(candidate, "verify.py")) && File.Exists(Path.Combine(candidate, "golden.ndjson"))) return candidate;
                    }
                throw new DirectoryNotFoundException("補足native原本が見つかりません: " + relativePath);
            }
            var supplementaryOracle = LocateFixture("tests/Fixtures/FolderReplaceListBoundaries");
            var supplementaryRepoRoot = Path.GetFullPath(Path.Combine(oracle, "..", "..", ".."));
            var supplementaryGolden = File.ReadAllLines(Path.Combine(supplementaryOracle, "golden.ndjson"))
                .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
                .ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
            string SupplementaryExpected(string id)
            {
                var value = supplementaryGolden[id].GetProperty("value");
                if (value.ValueKind == JsonValueKind.Null || value.GetProperty("kind").GetString() != "string") throw new InvalidDataException("補足原本のString結果が必要です: " + id);
                var bytes = Convert.FromHexString(value.GetProperty("utf8Hex").GetString()!);
                try { return Quote(new UTF8Encoding(false, true).GetString(bytes)); }
                catch (DecoderFallbackException) when (bytes.AsSpan().SequenceEqual(new byte[] { 0xc3 })) { return "Substr('é',0,1)"; }
            }
            bool SupplementaryFailure(string id) => supplementaryGolden[id].GetProperty("value").ValueKind == JsonValueKind.Null
                && supplementaryGolden[id].GetProperty("errorUtf8Hex").GetString()!.Length != 0;

            async Task Reader(string label, string script, params string[] arguments)
            {
                var start = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var argument in new[] { "-E", "-B", "-X", "utf8", script }.Concat(arguments)) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); var evidence = await stdout + await stderr;
                await File.WriteAllTextAsync(Path.Combine(directory, label + "-result.json"), evidence);
                check("folder RegexReplaceWithList independent " + label, process.ExitCode == 0, evidence);
            }
            await Reader("native-oracle", Path.Combine(oracle, "verify.py"), "--verify", "--git");
            using var casesDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(oracle, "cases.json")));
            var all = casesDocument.RootElement.EnumerateArray().ToArray();
            var golden = File.ReadAllLines(Path.Combine(oracle, "golden.ndjson")).Select(line => { using var row = JsonDocument.Parse(line); return row.RootElement.Clone(); })
                .ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
            static string Quote(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            static bool Projectable(JsonElement value) => value.GetProperty("type").GetString() switch
            {
                "array" => value.GetProperty("value").EnumerateArray().All(Projectable),
                "string" or "integer" or "double" or "bool" or "none" or "path" => true, _ => false
            };
            static string Argument(JsonElement value, string path) => value.GetProperty("type").GetString() switch
            {
                "path" => Quote(path), "string" => Quote(value.GetProperty("value").GetString()!), "none" => "None",
                "integer" or "double" => value.GetProperty("value").GetRawText(), "bool" => value.GetProperty("value").GetBoolean() ? "true" : "false",
                "array" => "Array(" + string.Join(',', value.GetProperty("value").EnumerateArray().Select(item => Argument(item, path))) + ")",
                _ => throw new InvalidDataException("Native-only list argument")
            };
            static string Expected(JsonElement value) => value.GetProperty("kind").GetString() switch
            {
                "none" => "None", "string" => Quote(new UTF8Encoding(false, true).GetString(Convert.FromHexString(value.GetProperty("utf8Hex").GetString()!))),
                "array" => "Array(" + string.Join(',', value.GetProperty("items").EnumerateArray().Select(Expected)) + ")", _ => throw new InvalidDataException("Native-only list result")
            };
            static string Same(string call, string expected) => "AllEqual(Array(" + call + "," + expected + "))";
            static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            var preserved = new Dictionary<string, string>(); var observations = new List<object>(); var boundaries = new List<object>(); var literalLinks = new List<object>();
            foreach (var row in all)
            {
                var id = row.GetProperty("id").GetString()!;
                var eligible = (row.GetProperty("function").GetString() is "RegexReplaceWithList" or "ReplaceWithList") && !row.GetProperty("nullArgs").GetBoolean()
                    && row.GetProperty("args").GetArrayLength() == 2 && row.GetProperty("args").EnumerateArray().All(Projectable);
                var fileAdapter = row.GetProperty("args").EnumerateArray().Any(value => value.GetProperty("type").GetString() == "path");
                boundaries.Add(new { id, scope = eligible ? fileAdapter ? "managed-file-adapter-projection" : "folder-cli-projection" : "native-only", reason = eligible ? "Typed result projection; file adapter encoding/OS equivalence is not asserted" : "Typed Regex binding, throw, null AST or wrong native arity" });
            }
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(directory, "sides-" + count, "side-" + side)).ToArray();
                foreach (var (root, side) in roots.Select((root, side) => (root, side)))
                {
                    Directory.CreateDirectory(root); var input = Path.Combine(root, "row.bin"); File.WriteAllBytes(input, Enumerable.Repeat((byte)(side + 1), side + 1).ToArray()); preserved[input] = Hash(input);
                }
                async Task Compare(string id, string expression, bool rejected = false, string events = "", string? nativeId = null, string? listPath = null, string diagnostic = "", bool included = true, bool sourceDerivedException = false, string? supplementaryNativeId = null)
                {
                    var filter = Filter("regexreplacelist-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"); preserved[filter] = Hash(filter);
                    // 例外境界は実syncの公開前で失敗させ、既存コピー先と全入力の保持を照合する。
                    var protectedPaths = roots.Select(root => Path.Combine(root, "row.bin")).Append(filter)
                        .Concat(listPath is not null && File.Exists(listPath) ? new[] { listPath } : []).Distinct(StringComparer.Ordinal).ToArray();
                    var before = protectedPaths.ToDictionary(path => path, Hash, StringComparer.Ordinal);
                    List<string> args = sourceDerivedException
                        ? ["--folder-sync", roots[0], roots[^1], "--direction", "left-to-right", "--copy", "all", "--select", "row.bin", "--filter", filter]
                        : ["--folder", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) args.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-regexreplacelist-" + count + "-" + id, rejected ? 2 : included ? 1 : 0, !rejected, args.ToArray());
                    var stdout = Path.Combine(directory, count + "-" + id + ".stdout"); var stderr = Path.Combine(directory, count + "-" + id + ".stderr");
                    await File.WriteAllTextAsync(stdout, result.Stdout, new UTF8Encoding(false)); await File.WriteAllTextAsync(stderr, result.Stderr, new UTF8Encoding(false));
                    check("folder RegexReplaceWithList result " + count + " " + id, rejected ? result.Stdout.Length == 0 && result.Stderr.StartsWith(events, StringComparison.Ordinal)
                        && result.Stderr.Length > events.Length && result.Stderr.Contains(diagnostic, StringComparison.Ordinal) : result.Stderr == events, result.Stderr);
                    if (!rejected)
                    {
                        using var model = JsonDocument.Parse(result.Stdout); var entry = model.RootElement.GetProperty("entries").EnumerateArray().Single();
                        check("folder RegexReplaceWithList inclusion " + count + " " + id, entry.GetProperty("path").GetString() == "row.bin" && entry.GetProperty("filtered").GetBoolean() == !included, result.Stdout);
                    }
                    check("folder list inputs and existing destination preserved " + count + " " + id, before.All(pair => File.Exists(pair.Key) && Hash(pair.Key) == pair.Value), id);
                    observations.Add(new { id, count, expression, rejected, included, events, nativeId, listPath, diagnostic,
                        scope = supplementaryNativeId is not null ? sourceDerivedException ? "supplementary-native-exception-boundary" : "supplementary-native-value-projection" : "public-projection-or-boundary",
                        supplementaryNativeId, sourceDerivedException, protectedBefore = before, existingOutput = Path.Combine(roots[^1], "row.bin"),
                        exitCode = result.ExitCode, stdout, stderr });
                }
                foreach (var row in all.Where(row => (row.GetProperty("function").GetString() is "RegexReplaceWithList" or "ReplaceWithList")
                    && !row.GetProperty("nullArgs").GetBoolean() && row.GetProperty("args").GetArrayLength() == 2 && row.GetProperty("args").EnumerateArray().All(Projectable)))
                {
                    var id = row.GetProperty("id").GetString()!; var path = Path.Combine(directory, count + "-" + id + ".list");
                    if (row.GetProperty("fileUtf8Hex").ValueKind == JsonValueKind.String) { File.WriteAllBytes(path, Convert.FromHexString(row.GetProperty("fileUtf8Hex").GetString()!)); preserved[path] = Hash(path); }
                    var call = row.GetProperty("function").GetString() + "(" + string.Join(',', row.GetProperty("args").EnumerateArray().Select(value => Argument(value, path))) + ")";
                    var supplementaryId = id == "literal-file-empty-from-cs" ? "literal-empty-ascii-ab" : null;
                    var expression = (row.GetProperty("caseSensitive").GetBoolean() ? "@cs " : "@ci ") + Same(call,
                        supplementaryId is null ? Expected(golden[id].GetProperty("value")) : SupplementaryExpected(supplementaryId));
                    var events = id is "regex-file-open-failure" or "literal-file-open-failure" ? "error: Failed to open regex replace list file: " + path + Environment.NewLine
                        : id == "regex-file-invalid-regex-cs" ? "error: Invalid regular expression at line 1 in file: " + path + ", pattern: [" + Environment.NewLine : "";
                    await Compare(id, expression, events: events, nativeId: id, listPath: path, supplementaryNativeId: supplementaryId);
                }
                // 35件の固定literal原本とは別の公開境界。token期待値はFolderReplaceOracle/LineTransformReplace由来。
                var literalList = Path.Combine(directory, count + "-literal-derived.list");
                File.WriteAllText(literalList, "a\t$0/$1/$10/$$\n", new UTF8Encoding(false)); preserved[literalList] = Hash(literalList);
                await Compare("literal-derived-poco-tokens", Same("ReplaceWithList('aba'," + Quote(literalList) + ")", "'a//0/$$ba//0/$$'"), listPath: literalList);
                // 固定literal35件とは別の、追加native原本で観測した直接substの例外境界。
                var literalEmpty = Path.Combine(directory, count + "-literal-exception-empty.list");
                File.WriteAllText(literalEmpty, "\tX\n", new UTF8Encoding(false)); preserved[literalEmpty] = Hash(literalEmpty);
                await Compare("literal-exception-empty-utf8", "ReplaceWithList('é'," + Quote(literalEmpty) + ")", rejected: SupplementaryFailure("literal-empty-utf8-e"), listPath: literalEmpty, diagnostic: "UTF-8", sourceDerivedException: true, supplementaryNativeId: "literal-empty-utf8-e");
                await Compare("literal-exception-empty-ascii-utf8", "ReplaceWithList('aé'," + Quote(literalEmpty) + ")", rejected: SupplementaryFailure("literal-empty-utf8-ae"), listPath: literalEmpty, diagnostic: "UTF-8", sourceDerivedException: true, supplementaryNativeId: "literal-empty-utf8-ae");
                var literalRawSource = Path.Combine(directory, count + "-literal-exception-raw.list");
                File.WriteAllText(literalRawSource, "a\tX\n", new UTF8Encoding(false)); preserved[literalRawSource] = Hash(literalRawSource);
                await Compare("literal-exception-raw-source", "ReplaceWithList(Substr('é',0,1)," + Quote(literalRawSource) + ")", rejected: SupplementaryFailure("literal-nonempty-raw-c3"), listPath: literalRawSource, diagnostic: "UTF-8", sourceDerivedException: true, supplementaryNativeId: "literal-nonempty-raw-c3");
                var literalContext = Path.Combine(directory, count + "-literal-context.list");
                File.WriteAllText(literalContext, "1\tL\n2\tM\n3\tR\n", new UTF8Encoding(false)); preserved[literalContext] = Hash(literalContext);
                await Compare("literal-side-context", Same("ReplaceWithList(LeftSize," + Quote(literalContext) + ")", "'L'") + " and "
                    + Same("ReplaceWithList(RightSize," + Quote(literalContext) + ")", count == 2 ? "'M'" : "'R'")
                    + (count == 3 ? " and " + Same("ReplaceWithList(MiddleSize," + Quote(literalContext) + ")", "'M'") : ""), listPath: literalContext);
                // 実リンクの読込みを拒否し、target bytesも保持する。作成不可は未観測として記録する。
                var literalLink = Path.Combine(directory, count + "-literal-link.list");
                var literalLinkCreated = false;
                try { File.CreateSymbolicLink(literalLink, literalContext); literalLinkCreated = true; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                { skip("folder literal list link creation", error.GetType().Name + ": link protection unmeasured"); }
                literalLinks.Add(new { count, path = literalLink, target = literalContext, created = literalLinkCreated });
                if (literalLinkCreated) await Compare("literal-link-input", Same("ReplaceWithList('1'," + Quote(literalLink) + ")", "'1'"),
                    events: "error: Failed to open regex replace list file: " + literalLink + Environment.NewLine, listPath: literalLink);
                await Compare("literal-fullpath", Same("ReplaceWithList(FullPath,Array())", "Array(" + string.Join(',', roots.Select(root => Quote(Path.Combine(root,"row.bin")))) + ")"));
                await Compare("literal-dynamic-list-context", Same("ReplaceWithList(LeftSize,If(RightSize > 1," + Quote(literalContext) + ",None))", "'L'"), listPath: literalContext);
                if (OperatingSystem.IsWindows()) await Compare("literal-property-context", Same("ReplaceWithList(Prop('System.Size')," + Quote(literalContext) + ")", count == 2 ? "Array('L','M')" : "Array('L','M','R')"), listPath: literalContext);
                foreach (var (id, expression, events) in new[]
                {
                    ("literal-argument-order", Same("ReplaceWithList(LogInfo('source','aba'),LogWarn('list',Array(Array('a','X'))))", "'aba'"), "info: source aba" + Environment.NewLine + "warn: list [[a, X]]" + Environment.NewLine),
                    ("literal-none-eager", Same("ReplaceWithList(LogInfo('source',None),LogWarn('list',Array()))", "None"), "info: source none" + Environment.NewLine + "warn: list []" + Environment.NewLine),
                    ("literal-invalid-list-eager", Same("ReplaceWithList(LogInfo('source','a'),LogWarn('list',1))", "None"), "info: source a" + Environment.NewLine + "warn: list 1" + Environment.NewLine),
                    ("literal-shallow-array", Same("ReplaceWithList(Array('a a',123,true,None,Array('a')),Array(Array('a','X')))", "Array('a a','123','true',None,'[a]')"), ""),
                    ("literal-ignored-inline", Same("ReplaceWithList('aba',Array(Array('a','b'),Array('b','c'),Array('[','X')))", "'aba'"), ""),
                    ("literal-function-case", Same("rEpLaCeWiThLiSt('a',Array(Array('a','X')))", "'a'"), ""),
                    ("literal-raw-source-retained", Same("ReplaceWithList(Substr('é',0,1),Array(Array('a','X')))", SupplementaryExpected("literal-inline-string-raw-c3")), "")
                }) await Compare(id, expression, events: events, supplementaryNativeId: id == "literal-raw-source-retained" ? "literal-inline-string-raw-c3" : null);
                await Compare("literal-negative-control", Same("ReplaceWithList('a',Array(Array('a','X')))", "'X'"), included: false);
                var nl = Environment.NewLine;
                foreach (var (id, expression, events) in new[]
                {
                    ("argument-order", Same("RegexReplaceWithList(LogInfo('source','aba'),LogWarn('list',Array(Array('a','X'))))", "'XbX'"), "info: source aba" + nl + "warn: list [[a, X]]" + nl),
                    ("none-eager", Same("RegexReplaceWithList(LogInfo('source',None),LogWarn('list',Array()))", "None"), "info: source none" + nl + "warn: list []" + nl),
                    ("invalid-list-eager", Same("RegexReplaceWithList(LogInfo('source','a'),LogWarn('list',1))", "None"), "info: source a" + nl + "warn: list 1" + nl),
                    ("shallow-array", Same("RegexReplaceWithList(Array('a',1,true,None,Array('a')),Array(Array('a','X')))", "Array('X','1','true',None,'[X]')"), ""),
                    ("reverse-order", Same("RegexReplaceWithList('aba',Array(Array('b','c'),Array('a','b')))", "'bcb'"), ""),
                    ("invalid-pairs-continue", Same("RegexReplaceWithList('a',Array(1,Array('a'),Array('a','x','y'),Array(1,'x'),Array('a',1),Array('[','x'),Array('a','Y')))", "'Y'"), ""),
                    ("capture-escape", Same("RegexReplaceWithList('a.a',Array(Array('([a])', '<$1>'),Array('\\\\.','!')))", "'<a>!<a>'"), ""),
                    ("zero-width-partial", Same("RegexReplaceWithList('aé',Array(Array('','X'),Array('X','Y')))", "RegexReplace('XXa','a',Substr('é',1,1))"), ""),
                    ("newline-inline", Same("RegexReplaceWithList('a\\nb',Array(Array('.','X')))", "'XXX'") + " and " + Same("RegexReplaceWithList('a\\rb',Array(Array('.','X')))", "'X\\rX'"), ""),
                    ("newline-direct", "RegexCount('a\\nb','.') == 3 and RegexFind('a\\nb','.',0,1) == 1 and " + Same("RegexExtract('a\\nb','.',0,1)", "'\\n'") + " and "
                        + Same("RegexReplace('a\\nb','.','X')", "'XXX'") + " and RegexCount('a\\rb','.') == 2 and " + Same("RegexReplace('a\\rb','.','X')", "'X\\rX'"), ""),
                    ("raw-source-retained", Same("RegexReplaceWithList(Substr('é',0,1),Array(Array('a','X')))", "Substr('é',0,1)"), ""),
                    ("side-metadata", Same("RegexReplaceWithList(LeftSize,Array(Array('1','L')))", "'L'") + " and " + Same("RegexReplaceWithList(RightSize,Array(Array('[23]','R')))", "'R'"), ""),
                    ("dynamic-list-context", Same("RegexReplaceWithList(LeftName,Array(Array(LeftName,'L')))", "'L'")
                        + (count == 3 ? " and " + Same("RegexReplaceWithList(MiddleSize,Array(Array('2','M')))", "'M'") : ""), ""),
                    ("fullpath", Same("RegexReplaceWithList(FullPath,Array(Array('row[.]bin$','ROW')))", "Array(" + string.Join(',', roots.Select(root => Quote(Path.Combine(root,"ROW")))) + ")"), ""),
                    ("function-case", Same("rEgExRePlAcEwItHlIsT('a',Array(Array('a','X')))", "'X'"), ""),
                    ("budget-accepted", Same("RegexReplaceWithList(" + Quote(new string('a',1000)) + ",Array(Array('z','X')))",Quote(new string('a',1000))), "")
                }) await Compare(id, expression, events: events);
                await Compare("negative-control", Same("RegexReplaceWithList('aba',Array(Array('a','b'),Array('b','c')))", "'bcb'"), included: false);
                if (OperatingSystem.IsWindows()) await Compare("property-context", Same("RegexReplaceWithList(Prop('System.Size'),Array(Array('[123]','X')))",
                    "Array(" + string.Join(',', Enumerable.Repeat("'X'", count)) + ")") + " and " + Same("RegexReplaceWithList(LeftName,Array(Array(LeftProp('System.ItemNameDisplay'),'Y')))", "'Y'"));
                var invalidUtf8 = Path.Combine(directory, count + "-invalid-utf8.list"); File.WriteAllBytes(invalidUtf8, [0xff]); preserved[invalidUtf8] = Hash(invalidUtf8);
                await Compare("file-invalid-utf8", "RegexReplaceWithList('a'," + Quote(invalidUtf8) + ")", rejected: true, listPath: invalidUtf8, diagnostic: "UTF-8");
                var oversized = Path.Combine(directory, count + "-oversized.list"); File.WriteAllBytes(oversized, new byte[1_048_577]); preserved[oversized] = Hash(oversized);
                await Compare("file-oversized", "RegexReplaceWithList('a'," + Quote(oversized) + ")", rejected: true, listPath: oversized, diagnostic: "1 MiB");
                var readBudget = Path.Combine(directory, count + "-read-budget.list"); File.WriteAllText(readBudget, new string('#', 40_000), new UTF8Encoding(false)); preserved[readBudget] = Hash(readBudget);
                await Compare("file-read-budget", "RegexReplaceWithList('a'," + Quote(readBudget) + ")", rejected: true, listPath: readBudget, diagnostic: "65,536");
                var newlineList = Path.Combine(directory, count + "-newline.list"); File.WriteAllText(newlineList, ".\tX\n", new UTF8Encoding(false)); preserved[newlineList] = Hash(newlineList);
                await Compare("newline-file", Same("RegexReplaceWithList('a\\nb'," + Quote(newlineList) + ")", "'XXX'") + " and " + Same("RegexReplaceWithList('a\\rb'," + Quote(newlineList) + ")", "'X\\rX'"), listPath: newlineList);
                await Compare("timeout", "RegexReplaceWithList(" + Quote(new string('a',100) + "!") + ",Array(Array('(a+)+$','X')))", rejected: true, diagnostic: "タイムアウト");
                var expensive = "ToLower(ToLower(" + Quote(new string('A',14000)) + "))";
                await Compare("source-failure", "RegexReplaceWithList(" + expensive + ",LogWarn('UNREACHED',Array()))", rejected: true, diagnostic: "65,536");
                await Compare("list-failure", "RegexReplaceWithList(LogInfo('source','a')," + expensive + ")", rejected: true, events: "info: source a" + nl, diagnostic: "65,536");
                await Compare("budget-pairs", "RegexReplaceWithList(" + Quote(new string('a',3000)) + ",Array(" + string.Join(',',Enumerable.Repeat("Array('z','X')",30)) + "))", rejected: true, diagnostic: "65,536");
                await Compare("budget-output", "RegexReplaceWithList('aa',Array(Array('a'," + Quote(new string('X',8000)) + ")))", rejected: true, diagnostic: "65,536");
                await Compare("literal-source-failure", "ReplaceWithList(" + expensive + ",LogWarn('UNREACHED',Array()))", rejected: true, diagnostic: "65,536");
                await Compare("literal-list-failure", "ReplaceWithList(LogInfo('source','a')," + expensive + ")", rejected: true, events: "info: source a" + nl, diagnostic: "65,536");
                await Compare("literal-file-read-budget", "ReplaceWithList('a'," + Quote(readBudget) + ")", rejected: true, listPath: readBudget, diagnostic: "65,536");
                await Compare("literal-file-invalid-utf8", "ReplaceWithList('a'," + Quote(invalidUtf8) + ")", rejected: true, listPath: invalidUtf8, diagnostic: "UTF-8");
                await Compare("literal-file-oversized", "ReplaceWithList('a'," + Quote(oversized) + ")", rejected: true, listPath: oversized, diagnostic: "1 MiB");
                var literalOutput = Path.Combine(directory, count + "-literal-output.list");
                File.WriteAllText(literalOutput, "a\t" + new string('X',8000) + "\n", new UTF8Encoding(false)); preserved[literalOutput] = Hash(literalOutput);
                await Compare("literal-budget-output", "ReplaceWithList('aa'," + Quote(literalOutput) + ")", rejected: true, listPath: literalOutput, diagnostic: "65,536");
                foreach (var (id, expression) in new[] { ("literal-arity-zero", "ReplaceWithList()"), ("literal-arity-one", "ReplaceWithList(LogInfo('UNREACHED'))"),
                    ("literal-arity-three", "ReplaceWithList(LogInfo('UNREACHED'),Array(),1)"), ("literal-arity-bare", "ReplaceWithList"), ("literal-arity-lazy", "If(false,ReplaceWithList(),true)") })
                    await Compare(id, expression, rejected: true, diagnostic: "ReplaceWithList");
                foreach (var (id, expression) in new[] { ("arity-zero", "RegexReplaceWithList()"), ("arity-one", "RegexReplaceWithList(LogInfo('UNREACHED'))"),
                    ("arity-three", "RegexReplaceWithList(LogInfo('UNREACHED'),Array(),1)"), ("arity-bare", "RegexReplaceWithList"), ("arity-lazy", "If(false,RegexReplaceWithList(),true)") })
                    await Compare(id, expression, rejected: true, diagnostic: "RegexReplaceWithList");
            }
            var inventory = Path.Combine(directory, "observations.json");
            await File.WriteAllTextAsync(inventory, JsonSerializer.Serialize(new { boundaries, observations, preserved, literalLinks, windowsProperties = OperatingSystem.IsWindows(), typedRegexPublicBinding = false, cancellationInjected = false, nativePathEncodingEquivalence = false }));
            var reader = Path.Combine(directory, "verify-folder-replacelist.py");
            await File.WriteAllTextAsync(reader, """
import json,pathlib,sys,hashlib,os,subprocess
d=json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8')); root=pathlib.Path(sys.argv[2])
cases=json.loads((root/'cases.json').read_text(encoding='utf-8')); golden={x['id']:x for x in map(json.loads,(root/'golden.ndjson').read_text(encoding='utf-8').splitlines())}
# 同じPythonで追加native proofを独立検査する。製品の失敗をnative例外全文と同一視しない。
supplementary_root=pathlib.Path(sys.argv[3]); repo_root=pathlib.Path(sys.argv[4])
verifier_args=[sys.executable,'-E','-B','-X','utf8',str(supplementary_root/'verify.py'),'--repo-root',str(repo_root),'--self-check-rejections']
proof=subprocess.run(verifier_args,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=120,check=False)
proof_receipt=dict(arguments=verifier_args,exitCode=proof.returncode,stdout=proof.stdout.decode('utf-8'),stderr=proof.stderr.decode('utf-8'))
pathlib.Path(sys.argv[1]).with_name('supplementary-proof-verifier.json').write_text(json.dumps(proof_receipt,ensure_ascii=False,indent=2),encoding='utf-8')
assert proof.returncode==0 and not proof.stderr,proof_receipt
proof_result=json.loads(proof.stdout)
assert proof_result['passed'] and proof_result['indexedProofEntries']==270 and proof_result['sourceBlobs']==61 and proof_result['sourceSpans']==41
assert proof_result['baselineCases']==70 and proof_result['supplementaryCases']==10 and proof_result['independentCompiles']==2
assert not proof_result['productExecuted'] and not proof_result['nativeRecompiled']
supplementary_cases={c['id']:c for c in json.loads((supplementary_root/'cases.json').read_text(encoding='utf-8'))}
supplementary_golden={r['id']:r for r in map(json.loads,(supplementary_root/'golden.ndjson').read_text(encoding='utf-8').splitlines())}
assert len(supplementary_cases)==10 and set(supplementary_cases)==set(supplementary_golden) and not set(supplementary_cases)&set(golden)
supplementary_mapping={'literal-exception-empty-utf8':'literal-empty-utf8-e','literal-exception-empty-ascii-utf8':'literal-empty-utf8-ae','literal-exception-raw-source':'literal-nonempty-raw-c3','literal-raw-source-retained':'literal-inline-string-raw-c3','literal-file-empty-from-cs':'literal-empty-ascii-ab'}
def quote(v): return "'"+v.replace('\\','\\\\').replace("'","\\'").replace('\r','\\r').replace('\n','\\n').replace('\t','\\t')+"'"
def arg(v,path):
 t=v['type']
 if t=='path': return quote(path)
 if t=='string': return quote(v['value'])
 if t=='none': return 'None'
 if t=='array': return 'Array('+','.join(arg(x,path) for x in v['value'])+')'
 if t=='bool': return str(v['value']).lower()
 return str(v['value'])
def expected(v):
 if v['kind']=='none': return 'None'
 if v['kind']=='string': return quote(bytes.fromhex(v['utf8Hex']).decode('utf-8'))
 return 'Array('+','.join(expected(x) for x in v['items'])+')'
def projectable(v): return all(map(projectable,v['value'])) if v['type']=='array' else v['type'] in ['string','integer','double','bool','none','path']
def same(call,value): return 'AllEqual(Array('+call+','+value+'))'
def supplementary_argument(value,path):
 if value['type']=='raw':
  assert value['hex']=='c3'
  return "Substr('é',0,1)"
 if value['type']=='array': return 'Array('+','.join(supplementary_argument(v,path) for v in value['value'])+')'
 return arg(value,path)
def supplementary_expected(native):
 value=native['value']; assert value is not None and value['kind']=='string'
 assert native['serializedUtf8Hex']==value['utf8Hex'] and native['errorUtf8Hex']==''
 data=bytes.fromhex(value['utf8Hex'])
 try: return quote(data.decode('utf-8'))
 except UnicodeDecodeError:
  assert data==b'\xc3'
  return "Substr('é',0,1)"
fixed={
 'argument-order':(same("RegexReplaceWithList(LogInfo('source','aba'),LogWarn('list',Array(Array('a','X'))))","'XbX'"),'info: source aba'+os.linesep+'warn: list [[a, X]]'+os.linesep),
 'none-eager':(same("RegexReplaceWithList(LogInfo('source',None),LogWarn('list',Array()))",'None'),'info: source none'+os.linesep+'warn: list []'+os.linesep),
 'invalid-list-eager':(same("RegexReplaceWithList(LogInfo('source','a'),LogWarn('list',1))",'None'),'info: source a'+os.linesep+'warn: list 1'+os.linesep),
 'shallow-array':(same("RegexReplaceWithList(Array('a',1,true,None,Array('a')),Array(Array('a','X')))","Array('X','1','true',None,'[X]')"),''),
 'reverse-order':(same("RegexReplaceWithList('aba',Array(Array('b','c'),Array('a','b')))","'bcb'"),''),
 'negative-control':(same("RegexReplaceWithList('aba',Array(Array('a','b'),Array('b','c')))","'bcb'"),''),
 'invalid-pairs-continue':(same("RegexReplaceWithList('a',Array(1,Array('a'),Array('a','x','y'),Array(1,'x'),Array('a',1),Array('[','x'),Array('a','Y')))","'Y'"),''),
 'capture-escape':(same("RegexReplaceWithList('a.a',Array(Array('([a])', '<$1>'),Array('\\\\.','!')))","'<a>!<a>'"),''),
 'zero-width-partial':(same("RegexReplaceWithList('aé',Array(Array('','X'),Array('X','Y')))","RegexReplace('XXa','a',Substr('é',1,1))"),''),
 'newline-inline':(same("RegexReplaceWithList('a\\nb',Array(Array('.','X')))","'XXX'")+' and '+same("RegexReplaceWithList('a\\rb',Array(Array('.','X')))","'X\\rX'"),''),
 'newline-direct':("RegexCount('a\\nb','.') == 3 and RegexFind('a\\nb','.',0,1) == 1 and "+same("RegexExtract('a\\nb','.',0,1)","'\\n'")+' and '+same("RegexReplace('a\\nb','.','X')","'XXX'")+" and RegexCount('a\\rb','.') == 2 and "+same("RegexReplace('a\\rb','.','X')","'X\\rX'"),''),
 'raw-source-retained':(same("RegexReplaceWithList(Substr('é',0,1),Array(Array('a','X')))","Substr('é',0,1)"),''),
 'function-case':(same("rEgExRePlAcEwItHlIsT('a',Array(Array('a','X')))","'X'"),''),
 'budget-accepted':(same('RegexReplaceWithList('+quote('a'*1000)+",Array(Array('z','X')))",quote('a'*1000)),''),
 'source-failure':('RegexReplaceWithList(ToLower(ToLower('+quote('A'*14000)+")),LogWarn('UNREACHED',Array()))",''),
 'list-failure':("RegexReplaceWithList(LogInfo('source','a'),ToLower(ToLower("+quote('A'*14000)+')))', 'info: source a'+os.linesep),
 'budget-pairs':('RegexReplaceWithList('+quote('a'*3000)+',Array('+','.join(["Array('z','X')"]*30)+'))',''),
 'budget-output':("RegexReplaceWithList('aa',Array(Array('a',"+quote('X'*8000)+')))', ''),
 'arity-zero':('RegexReplaceWithList()',''), 'arity-one':("RegexReplaceWithList(LogInfo('UNREACHED'))",''),
 'arity-three':("RegexReplaceWithList(LogInfo('UNREACHED'),Array(),1)",''), 'arity-bare':('RegexReplaceWithList',''), 'arity-lazy':('If(false,RegexReplaceWithList(),true)','')}
fixed.update({
 'literal-argument-order':(same("ReplaceWithList(LogInfo('source','aba'),LogWarn('list',Array(Array('a','X'))))","'aba'"),'info: source aba'+os.linesep+'warn: list [[a, X]]'+os.linesep),
 'literal-none-eager':(same("ReplaceWithList(LogInfo('source',None),LogWarn('list',Array()))","None"),'info: source none'+os.linesep+'warn: list []'+os.linesep),
 'literal-invalid-list-eager':(same("ReplaceWithList(LogInfo('source','a'),LogWarn('list',1))","None"),'info: source a'+os.linesep+'warn: list 1'+os.linesep),
 'literal-shallow-array':(same("ReplaceWithList(Array('a a',123,true,None,Array('a')),Array(Array('a','X')))","Array('a a','123','true',None,'[a]')"),''),
 'literal-ignored-inline':(same("ReplaceWithList('aba',Array(Array('a','b'),Array('b','c'),Array('[','X')))","'aba'"),''),
 'literal-function-case':(same("rEpLaCeWiThLiSt('a',Array(Array('a','X')))","'a'"),''),
 'literal-raw-source-retained':(same("ReplaceWithList(Substr('é',0,1),Array(Array('a','X')))",supplementary_expected(supplementary_golden['literal-inline-string-raw-c3'])),''),
 'literal-negative-control':(same("ReplaceWithList('a',Array(Array('a','X')))","'X'"),''),
 'literal-source-failure':('ReplaceWithList(ToLower(ToLower('+quote('A'*14000)+")),LogWarn('UNREACHED',Array()))",''),
 'literal-list-failure':("ReplaceWithList(LogInfo('source','a'),ToLower(ToLower("+quote('A'*14000)+')))', 'info: source a'+os.linesep),
 'literal-arity-zero':('ReplaceWithList()',''), 'literal-arity-one':("ReplaceWithList(LogInfo('UNREACHED'))",''),
 'literal-arity-three':("ReplaceWithList(LogInfo('UNREACHED'),Array(),1)",''), 'literal-arity-bare':('ReplaceWithList',''), 'literal-arity-lazy':('If(false,ReplaceWithList(),true)','')})
selected={c['id']:c for c in cases if c['function'] in ['RegexReplaceWithList','ReplaceWithList'] and not c['nullArgs'] and len(c['args'])==2 and all(map(projectable,c['args']))}
assert len([c for c in selected.values() if c['function']=='ReplaceWithList'])==25
assert len([c for c in selected.values() if c['function']=='ReplaceWithList' and any(a['type']=='path' for a in c['args'])])==16
assert len(cases)==70 and len(d['boundaries'])==70 and {b['id'] for b in d['boundaries']}=={c['id'] for c in cases}
assert {b['id'] for b in d['boundaries'] if b['scope'] in ['folder-cli-projection','managed-file-adapter-projection']}==set(selected)
assert {b['id'] for b in d['boundaries'] if b['scope']=='managed-file-adapter-projection'}=={c['id'] for c in selected.values() if any(a['type']=='path' for a in c['args'])}
assert not d['typedRegexPublicBinding'] and not d['cancellationInjected'] and not d['nativePathEncodingEquivalence']
seen=set(); projections=set(); supplementary_seen=set(); rejected=0
for r in d['observations']:
 key=(r['count'],r['id']); assert key not in seen; seen.add(key)
 out=pathlib.Path(r['stdout']).read_bytes(); err=pathlib.Path(r['stderr']).read_bytes(); events=r['events'].encode()
 if r['id'] in fixed: assert (r['expression'],r['events'])==fixed[r['id']],key
 if r['id']=='file-invalid-utf8': assert pathlib.Path(r['listPath']).read_bytes()==b'\xff' and r['expression']=="RegexReplaceWithList('a',"+quote(r['listPath'])+')' and r['diagnostic']=='UTF-8',key
 if r['id']=='file-oversized': assert pathlib.Path(r['listPath']).stat().st_size==1048577 and r['expression']=="RegexReplaceWithList('a',"+quote(r['listPath'])+')' and r['diagnostic']=='1 MiB',key
 if r['id']=='file-read-budget': assert pathlib.Path(r['listPath']).read_bytes()==b'#'*40000 and r['expression']=="RegexReplaceWithList('a',"+quote(r['listPath'])+')' and r['diagnostic']=='65,536',key
 if r['id']=='timeout': assert r['expression']=='RegexReplaceWithList('+quote('a'*100+'!')+",Array(Array('(a+)+$','X')))" and r['diagnostic']=='タイムアウト',key
 if r['id']=='newline-file': assert pathlib.Path(r['listPath']).read_bytes()==b'.\tX\n' and r['expression']==same("RegexReplaceWithList('a\\nb',"+quote(r['listPath'])+')',"'XXX'")+' and '+same("RegexReplaceWithList('a\\rb',"+quote(r['listPath'])+')',"'X\\rX'"),key
 if r['id']=='property-context': assert r['expression']==same("RegexReplaceWithList(Prop('System.Size'),Array(Array('[123]','X')))",'Array('+','.join(["'X'"]*r['count'])+')')+' and '+same("RegexReplaceWithList(LeftName,Array(Array(LeftProp('System.ItemNameDisplay'),'Y')))","'Y'"),key
 if r['id'].startswith('literal-file-') and not r['nativeId']:
  kind=r['id'].removeprefix('literal-file-'); p=pathlib.Path(r['listPath'])
  assert r['expression']=="ReplaceWithList('a',"+quote(str(p))+')'
  if kind=='invalid-utf8': assert p.read_bytes()==b'\xff' and r['diagnostic']=='UTF-8'
  elif kind=='oversized': assert p.stat().st_size==1048577 and r['diagnostic']=='1 MiB'
  elif kind=='read-budget': assert p.read_bytes()==b'#'*40000 and r['diagnostic']=='65,536'
  else: raise AssertionError(kind)
 if r['id']=='literal-budget-output': assert pathlib.Path(r['listPath']).read_bytes()==b'a\t'+b'X'*8000+b'\n' and r['expression']=="ReplaceWithList('aa',"+quote(r['listPath'])+')' and r['diagnostic']=='65,536'
 if r['id']=='literal-derived-poco-tokens': assert pathlib.Path(r['listPath']).read_bytes()==b'a\t$0/$1/$10/$$\n' and r['expression']==same("ReplaceWithList('aba',"+quote(r['listPath'])+')',"'a//0/$$ba//0/$$'")
 if r['id'] in ['literal-side-context','literal-dynamic-list-context','literal-property-context']:
  assert pathlib.Path(r['listPath']).read_bytes()==b'1\tL\n2\tM\n3\tR\n'
  path=quote(r['listPath'])
  if r['id']=='literal-side-context': assert r['expression']==same('ReplaceWithList(LeftSize,'+path+')',"'L'")+' and '+same('ReplaceWithList(RightSize,'+path+')',"'M'" if r['count']==2 else "'R'")+(' and '+same('ReplaceWithList(MiddleSize,'+path+')',"'M'") if r['count']==3 else '')
  if r['id']=='literal-dynamic-list-context': assert r['expression']==same('ReplaceWithList(LeftSize,If(RightSize > 1,'+path+",None))","'L'")
  if r['id']=='literal-property-context': assert d['windowsProperties'] and r['expression']==same("ReplaceWithList(Prop('System.Size'),"+path+')',"Array('L','M')" if r['count']==2 else "Array('L','M','R')")
 if r['id']=='literal-fullpath':
  roots=[pathlib.Path(r['stdout']).parent/('sides-'+str(r['count']))/('side-'+str(side)) for side in range(r['count'])]
  assert r['expression']==same('ReplaceWithList(FullPath,Array())','Array('+','.join(quote(str(root/'row.bin')) for root in roots)+')')
 if r['id']=='literal-link-input':
  assert pathlib.Path(r['listPath']).is_symlink() and r['expression']==same("ReplaceWithList('1',"+quote(r['listPath'])+')',"'1'") and r['events']=='error: Failed to open regex replace list file: '+r['listPath']+os.linesep
 if r['sourceDerivedException']:
  assert r['scope']=='supplementary-native-exception-boundary' and r['nativeId'] is None and r['rejected'] and r['diagnostic']=='UTF-8'
  source={'literal-exception-empty-utf8':"'é'",'literal-exception-empty-ascii-utf8':"'aé'",'literal-exception-raw-source':"Substr('é',0,1)"}[r['id']]
  assert r['expression']=='ReplaceWithList('+source+','+quote(r['listPath'])+')'
  assert pathlib.Path(r['listPath']).read_bytes()==(b'a\tX\n' if r['id']=='literal-exception-raw-source' else b'\tX\n')
  assert r['existingOutput'] in r['protectedBefore'] and pathlib.Path(r['existingOutput']).read_bytes()==bytes([r['count']])*r['count']
 if r['id']=='literal-raw-source-retained': assert r['scope']=='supplementary-native-value-projection' and not r['sourceDerivedException'] and r['nativeId'] is None
 for path,sha in r['protectedBefore'].items(): assert hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()==sha,path
 supplementary_id=supplementary_mapping.get(r['id'])
 assert r['supplementaryNativeId']==supplementary_id
 if supplementary_id is not None:
  supplementary_seen.add((r['count'],supplementary_id))
  case=supplementary_cases[supplementary_id]; native=supplementary_golden[supplementary_id]
  assert case['function']=='ReplaceWithList' and case['caseSensitive'] and not case['nullArgs'] and native['evaluated']==[0,1]
  assert native['loaderLogs']==[] and native['functionLogs']==[]
  call=case['function']+'('+','.join(supplementary_argument(value,r['listPath']) for value in case['args'])+')'
  if case['fileUtf8Hex'] is not None: assert pathlib.Path(r['listPath']).read_bytes()==bytes.fromhex(case['fileUtf8Hex'])
  failed=native['value'] is None
  assert failed==(native['errorUtf8Hex']!='') and failed==r['rejected'] and failed==r['sourceDerivedException']
  if failed:
   assert native['serializedUtf8Hex'] is None and r['scope']=='supplementary-native-exception-boundary' and r['expression']==call
  else:
   assert r['scope']=='supplementary-native-value-projection'
   assert r['expression']==('@cs ' if r['nativeId'] else '')+same(call,supplementary_expected(native))
  assert r['existingOutput'] in r['protectedBefore'] and pathlib.Path(r['existingOutput']).read_bytes()==bytes([r['count']])*r['count']
 if r['nativeId']:
  c=selected[r['nativeId']]; projections.add((r['count'],c['id']))
  call=c['function']+'('+','.join(arg(a,r['listPath']) for a in c['args'])+')'
  assert r['expression']==('@cs ' if c['caseSensitive'] else '@ci ')+'AllEqual(Array('+call+','+expected(golden[c['id']]['value'])+'))',key
  if c['fileUtf8Hex'] is not None: assert pathlib.Path(r['listPath']).read_bytes()==bytes.fromhex(c['fileUtf8Hex']),key
  if c['id'] in ['regex-file-open-failure','literal-file-open-failure']: assert not pathlib.Path(r['listPath']).exists() and b'Failed to open' in err
 if r['rejected']:
  rejected+=1; assert r['exitCode']==2 and not out and err.startswith(events) and len(err)>len(events) and r['diagnostic'].encode() in err,key
  assert b'UNREACHED' not in err,key
 else:
  assert r['exitCode']==(1 if r['included'] else 0) and err==events,key
  assert r['included']==(r['id'] not in ['negative-control','literal-negative-control']),key
  entries=json.loads(out)['entries']; assert len(entries)==1 and entries[0]['path']=='row.bin' and entries[0]['filtered']==(not r['included']),key
  names=['left','right'] if r['count']==2 else ['left','middle','right']
  for side in names: assert entries[0][side] is not None and entries[0][side]['filtered']==(not r['included']),key
assert projections=={(n,c) for n in [2,3] for c in selected}
assert supplementary_seen=={(n,native_id) for n in [2,3] for native_id in supplementary_mapping.values()} and len(supplementary_seen)==10
required=list(fixed)+['literal-exception-empty-utf8','literal-exception-empty-ascii-utf8','literal-exception-raw-source']+['literal-derived-poco-tokens','literal-side-context','literal-dynamic-list-context','literal-fullpath','literal-file-invalid-utf8','literal-file-oversized','literal-file-read-budget','literal-budget-output']+(['literal-property-context'] if d['windowsProperties'] else [])+['side-metadata','dynamic-list-context','fullpath','file-invalid-utf8','file-oversized','file-read-budget','timeout','newline-file']+(['property-context'] if d['windowsProperties'] else [])
assert len(d['literalLinks'])==2
def canonical_link_target(target,parent):
 target=os.fspath(target)
 if os.name=='nt':
  # Windowsのextended prefixだけを通常形へ戻し、device pathやPOSIXの文字は変えない。
  prefix='\\\\?\\'; unc=prefix+'UNC\\'
  if target[:len(unc)].upper()==unc: target='\\\\'+target[len(unc):]
  elif target.startswith(prefix): target=target[len(prefix):]
 if not os.path.isabs(target): target=os.path.join(os.fspath(parent),target)
 return os.path.normcase(os.path.normpath(os.path.abspath(target)))
for link in d['literalLinks']:
 assert link['count'] in [2,3]
 if link['created']:
  assert pathlib.Path(link['path']).is_symlink() and canonical_link_target(pathlib.Path(link['path']).readlink(),pathlib.Path(link['path']).parent)==canonical_link_target(link['target'],pathlib.Path(link['path']).parent)
  assert (link['count'],'literal-link-input') in seen
assert seen=={(n,c) for n in [2,3] for c in list(selected)+required}|{(link['count'],'literal-link-input') for link in d['literalLinks'] if link['created']}
for p,sha in d['preserved'].items(): assert hashlib.sha256(pathlib.Path(p).read_bytes()).hexdigest()==sha,p
print(json.dumps(dict(passed=True,fixedNativeCases=70,supplementaryNativeCases=10,supplementaryNativeResultProjections=len(supplementary_seen),nativeExceptionMessageEquivalence=False,projectedBodies=len(selected),managedFileAdapterBodies=sum(b['scope']=='managed-file-adapter-projection' for b in d['boundaries']),resultProjections=len(projections),commands=len(seen),expectedRejections=rejected,preservedFiles=len(d['preserved']),typedRegexPublicBinding=False,cancellationInjected=False,nativePathEncodingEquivalence=False)))
""", new UTF8Encoding(false));
            await Reader("folder-reader", reader, inventory, oracle, supplementaryOracle, supplementaryRepoRoot);
        }

        async Task RegexReplaceValues()
        {
            var oracle = Path.GetFullPath("tests/Fixtures/LineTransformRegex");
            var directory = Path.Combine(work, "regexreplace"); Directory.CreateDirectory(directory);
            async Task Reader(string label, string script, params string[] arguments)
            {
                var start = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var argument in new[] { "-E", "-B", "-X", "utf8", script }.Concat(arguments)) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); var evidence = await stdout + await stderr;
                await File.WriteAllTextAsync(Path.Combine(directory, label + "-result.json"), evidence);
                check("folder RegexReplace independent " + Path.GetFileName(script), process.ExitCode == 0, evidence);
            }
            await Reader("line-regex-oracle", Path.Combine(oracle, "verify.py"), "--verify", "--git");
            var replaceOracle = Path.GetFullPath("tests/Fixtures/FolderReplaceOracle");
            await Reader("folder-replace-oracle", Path.Combine(replaceOracle, "verify.py"));
            using var casesDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(oracle, "cases.json")));
            var all = casesDocument.RootElement.EnumerateArray().Where(item => item.GetProperty("function").GetString() == "regexreplace").ToArray();
            var golden = File.ReadAllLines(Path.Combine(oracle, "golden.ndjson")).Select(line => { using var row = JsonDocument.Parse(line); return row.RootElement.Clone(); })
                .ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
            static bool PublicArgument(JsonElement value)
            {
                var type = value.GetProperty("type").GetString();
                return type == "array" ? value.GetProperty("value").EnumerateArray().All(PublicArgument)
                    : type is "integer" or "double" or "bool" or "none" || type == "string";
            }
            static string Exclusion(JsonElement row)
            {
                if (row.GetProperty("mode").GetString() != "function-body-noopt") return "optimizer-only";
                if (row.GetProperty("nullArgs").GetBoolean()) return "null-args-adapter";
                if (row.GetProperty("args").GetArrayLength() != 3) return "native-body-arity-public-parser-rejects";
                foreach (var argument in row.GetProperty("args").EnumerateArray()) if (!PublicArgument(argument))
                    return argument.GetProperty("type").GetString() == "string" ? "embedded-nul-literal-projection" : "adapter-argument-" + argument.GetProperty("type").GetString();
                return "";
            }
            var selected = all.Where(row => Exclusion(row).Length == 0).ToArray();
            var partition = all.Select(row => new { id = row.GetProperty("id").GetString(), exclusion = Exclusion(row), expected = golden[row.GetProperty("id").GetString()!] }).ToArray();
            await File.WriteAllTextAsync(Path.Combine(directory, "native-partition.json"), JsonSerializer.Serialize(partition));
            var kinds = selected.Select(row => golden[row.GetProperty("id").GetString()!].GetProperty("value"))
                .Select(value => value.ValueKind == JsonValueKind.Null ? "exception" : value.GetProperty("kind").GetString()!).ToArray();
            check("folder RegexReplace complete native partition", all.Length == 107 && selected.Length == 74
                && kinds.Count(kind => kind == "string") == 50 && kinds.Count(kind => kind == "none") == 17
                && kinds.Count(kind => kind == "array") == 6 && kinds.Count(kind => kind == "exception") == 1,
                "74 public bodies including three NULs; 33 native-only IDs retained");
            static string Quote(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            static string Argument(JsonElement value) => value.GetProperty("type").GetString() switch
            {
                "none" => "None", "string" => Quote(value.GetProperty("value").GetString()!),
                "integer" => value.GetProperty("value").GetRawText(),
                "double" => value.GetProperty("value").GetRawText().IndexOfAny(['.', 'e', 'E']) >= 0 ? value.GetProperty("value").GetRawText() : value.GetProperty("value").GetRawText() + ".0",
                "bool" => value.GetProperty("value").GetBoolean() ? "true" : "false",
                "array" => "Array(" + string.Join(',', value.GetProperty("value").EnumerateArray().Select(Argument)) + ")",
                _ => throw new InvalidDataException("Unprojectable regex argument")
            };
            static string Expected(JsonElement value) => value.GetProperty("kind").GetString() switch
            {
                "none" => "None", "integer" => value.GetProperty("value").GetRawText(),
                "string" => Quote(new UTF8Encoding(false, true).GetString(Convert.FromHexString(value.GetProperty("utf8Hex").GetString()!))),
                "array" => "Array(" + string.Join(',', value.GetProperty("items").EnumerateArray().Select(Expected)) + ")",
                _ => throw new InvalidDataException("Unprojectable RegexReplace result")
            };
            var preserved = new Dictionary<string, string>(); var observations = new List<object>(); var expectedCommands = new List<string>();
            static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            var retained = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            static Dictionary<string, string> Metadata(string path)
            {
                var info = new FileInfo(path);
                return new() { ["sha256"] = Hash(path), ["length"] = info.Length.ToString(CultureInfo.InvariantCulture), ["writeTicks"] = info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) };
            }
            void Preserve(string path) { preserved[path] = Hash(path); retained[path] = Metadata(path); }

            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(directory, "sides-" + count, "side-" + side)).ToArray();
                foreach (var (root, side) in roots.Select((root, side) => (root, side)))
                {
                    Directory.CreateDirectory(root); var input = Path.Combine(root, "row.bin"); File.WriteAllBytes(input, Enumerable.Repeat((byte)(side + 1), side + 1).ToArray());
                    File.SetLastWriteTimeUtc(input, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)); Preserve(input);
                }
                async Task Compare(string id, string expression, bool included = true, bool rejected = false, string events = "", string diagnostic = "", string? nativeId = null, bool multiple = false, bool missing = false)
                {
                    if (id is "budget-prep-accepted" or "budget-prep-rejected" or "budget-prep-standalone")
                        expression = "@noopt " + expression;
                    expectedCommands.Add(count + ":" + id);
                    var filter = Filter("regexreplace-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"); Preserve(filter);
                    var activeRoots = roots; var sideNames = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                    var expectedRows = new Dictionary<string, (bool Included, string[] Present)>(StringComparer.Ordinal) { ["row.bin"] = (included, missing ? ["left"] : sideNames) };
                    if (multiple || missing)
                    {
                        activeRoots = Enumerable.Range(0, count).Select(side => Path.Combine(directory, (multiple ? "multiple-" : "missing-") + count, "side-" + side)).ToArray();
                        foreach (var (root, side) in activeRoots.Select((root, side) => (root, side)))
                        {
                            Directory.CreateDirectory(root);
                            foreach (var (name, size) in multiple ? new[] { ("alpha.bin", 1), ("beta.bin", 3) } : side == 0 ? new[] { ("row.bin", 1) } : Array.Empty<(string, int)>())
                            { var input = Path.Combine(root, name); File.WriteAllBytes(input, Enumerable.Repeat((byte)(side + 1), size).ToArray()); Preserve(input); }
                        }
                        if (multiple) { expectedRows.Clear(); expectedRows.Add("alpha.bin", (true, sideNames)); expectedRows.Add("beta.bin", (false, sideNames)); }
                    }
                    var expectedRowEvidence = expectedRows.ToDictionary(pair => pair.Key, pair => new { included = pair.Value.Included, present = pair.Value.Present });
                    await File.WriteAllTextAsync(Path.Combine(directory, count + "-" + id + "-expected-rows.json"), JsonSerializer.Serialize(expectedRowEvidence));
                    List<string> args = ["--folder", activeRoots[0], activeRoots[^1], "--filter", filter, "--show-filtered"]; if (count == 3) args.AddRange(["--middle", activeRoots[1]]);
                    var protectedBefore = activeRoots.SelectMany(root => Directory.EnumerateFiles(root)).Append(filter).ToDictionary(path => path, Metadata, StringComparer.Ordinal);
                    var result = await run("folder-regexreplace-" + count + "-" + id, rejected ? 2 : included ? 1 : 0, !rejected, args.ToArray());
                    var protectedAfter = protectedBefore.Keys.ToDictionary(path => path, Metadata, StringComparer.Ordinal);
                    check("folder RegexReplace immediate input/filter retention " + count + " " + id,
                        JsonSerializer.Serialize(protectedBefore) == JsonSerializer.Serialize(protectedAfter)
                        && protectedBefore.All(pair => JsonSerializer.Serialize(retained[pair.Key]) == JsonSerializer.Serialize(pair.Value)), JsonSerializer.Serialize(protectedAfter));
                    var stdout = Path.Combine(directory, count + "-" + id + ".stdout"); var stderr = Path.Combine(directory, count + "-" + id + ".stderr");
                    await File.WriteAllTextAsync(stdout, result.Stdout, new UTF8Encoding(false)); await File.WriteAllTextAsync(stderr, result.Stderr, new UTF8Encoding(false));
                    bool EventsMatch()
                    {
                        if (!multiple) return result.Stderr == events;
                        var lines = result.Stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                        var expected = new[] { "info: alpha.bin", "warn: alpha.bin ^alpha", "info: alpha.bin X", "info: beta.bin", "warn: beta.bin ^alpha", "info: beta.bin X" };
                        return lines.Length == expected.Length && lines.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal))
                            && Array.IndexOf(lines, expected[0]) < Array.IndexOf(lines, expected[1]) && Array.IndexOf(lines, expected[1]) < Array.IndexOf(lines, expected[2])
                            && Array.IndexOf(lines, expected[3]) < Array.IndexOf(lines, expected[4]) && Array.IndexOf(lines, expected[4]) < Array.IndexOf(lines, expected[5]);
                    }
                    var noParserEvents = !id.StartsWith("invalid-", StringComparison.Ordinal) || !result.Stderr.Split(Environment.NewLine, StringSplitOptions.None)
                        .Any(line => line.StartsWith("info: ", StringComparison.Ordinal) || line.StartsWith("warn: ", StringComparison.Ordinal) || line.StartsWith("error: ", StringComparison.Ordinal));
                    check("folder RegexReplace streams/order " + count + " " + id, rejected ? result.Stdout.Length == 0 && result.Stderr.StartsWith(events, StringComparison.Ordinal)
                        && result.Stderr.Length > events.Length && result.Stderr.Contains(diagnostic, StringComparison.Ordinal) && noParserEvents : EventsMatch(), result.Stderr);
                    if (!rejected && result.ExitCode == (included ? 1 : 0))
                    {
                        using var json = JsonDocument.Parse(result.Stdout); var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                        check("folder RegexReplace typed row and sides " + count + " " + id, entries.Length == expectedRows.Count
                            && entries.Select(entry => entry.GetProperty("path").GetString()!).ToHashSet(StringComparer.Ordinal).SetEquals(expectedRows.Keys)
                            && entries.All(entry =>
                            {
                                var expected = expectedRows[entry.GetProperty("path").GetString()!];
                                var present = new[] { "left", "middle", "right" }.Where(name => entry.TryGetProperty(name, out var side) && side.ValueKind != JsonValueKind.Null).ToHashSet(StringComparer.Ordinal);
                                return present.SetEquals(expected.Present) && entry.GetProperty("filtered").GetBoolean() == !expected.Included
                                    && expected.Present.All(name => entry.GetProperty(name).GetProperty("filtered").GetBoolean() == !expected.Included);
                            }), result.Stdout);
                    }
                    if (id == "timeout") check("folder RegexReplace bounded timeout " + count, result.DurationMilliseconds < 10_000, result.DurationMilliseconds.ToString(CultureInfo.InvariantCulture));
                    observations.Add(new { id, nativeId, count, included, rejected, multiple, missing, expectedRows = expectedRowEvidence, expression, stdout, stderr, exitCode = result.ExitCode,
                        duration = result.DurationMilliseconds, diagnostic, inputRoots = activeRoots, filter, protectedBefore, protectedAfter, eventsHex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(events)) });
                }
                foreach (var item in selected)
                {
                    var id = item.GetProperty("id").GetString()!; var expected = golden[id]; var failure = expected.GetProperty("value").ValueKind == JsonValueKind.Null;
                    var call = "RegexReplace(" + string.Join(',', item.GetProperty("args").EnumerateArray().Select(Argument)) + ")";
                    await Compare("native-" + id, (item.GetProperty("caseSensitive").GetBoolean() ? "@cs " : "@ci ")
                        + (failure ? call : "AllEqual(Array(" + call + "," + Expected(expected.GetProperty("value")) + "))"), rejected: failure, diagnostic: failure ? "replacement" : "", nativeId: id);
                }
                var nl = Environment.NewLine; var a5000 = Quote(new string('a',5000));
                string Same(string call, string value) => "AllEqual(Array(" + call + "," + value + "))";
                const string raw = "Substr('é',0,1)";
                var repeated = "RegexReplace('aba','a'," + raw + ")";
                var budgetArray = "ChooseEach(Array(" + string.Join(',', Enumerable.Repeat("0", 128)) + ")," + Quote(new string('a',1024)) + ")";
                var sharedCall = "RegexReplace(" + Quote(new string('a',2000)) + ",'z','X')";
                var sharedBudget = "AllEqual(Array(" + string.Join(',', Enumerable.Repeat(sharedCall, 7)) + "))";
                foreach (var (id, expression, events) in new[]
                {
                    ("function-case", Same("REGEXREPLACE('Aa','a','X')","'XX'"), ""),
                    ("argument-order", Same("RegexReplace(LogInfo('source','a'),LogWarn('pattern','a'),LogInfo('replacement','X'))","'X'"), "info: source a" + nl + "warn: pattern a" + nl + "info: replacement X" + nl),
                    ("none-eager", Same("RegexReplace(LogInfo('source',None),LogWarn('pattern','a'),LogInfo('replacement','X'))","None"), "info: source none" + nl + "warn: pattern a" + nl + "info: replacement X" + nl),
                    ("pattern-type-eager", Same("RegexReplace(LogInfo('source','a'),LogWarn('pattern',false),LogInfo('replacement','X'))","None"), "info: source a" + nl + "warn: pattern false" + nl + "info: replacement X" + nl),
                    ("replacement-type-eager", Same("RegexReplace(LogInfo('source','a'),LogWarn('pattern','a'),LogInfo('replacement',false))","None"), "info: source a" + nl + "warn: pattern a" + nl + "info: replacement false" + nl),
                    ("array-eager", Same("RegexReplace(LogInfo('source',Array('a',None)),LogWarn('pattern','a'),LogInfo('replacement','X'))","Array('X',None)"), "info: source [a, none]" + nl + "warn: pattern a" + nl + "info: replacement X" + nl),
                    ("pattern-array-type", Same("RegexReplace(Array('a',None),Array('a'),'X')","None"), ""),
                    ("replacement-array-type", Same("RegexReplace(Array('a',None),'a',Array('X'))","None"), ""),
                    ("empty-array-invalid-pattern", Same("RegexReplace(Array(),'[','X')","Array()"), ""),
                    ("date-source", Same("RegexReplace(d'2024-01-02T03:04:05Z','^([0-9]{4}).*$','$1')","'2024'"), ""),
                    ("timestamp-source", Same("RegexReplace(ToDateTime('2024-01-02T03:04:05Z'),'^([0-9]{4}).*$','$1')","'2024'"), ""),
                    ("raw-source-none", Same("RegexReplace(" + raw + ",'a','X')","None"), ""),
                    ("raw-pattern-none", Same("RegexReplace('abc'," + raw + ",'X')","None"), ""),
                    ("raw-replacement-terminal", Same("RegexReplace('a','a'," + raw + ")",raw), ""),
                    ("raw-replacement-no-match", Same("RegexReplace('a','z'," + raw + ")","'a'"), ""),
                    ("raw-unsupported-pattern-no-match", Same("RegexReplace('b','(a)'," + raw + ")","'b'"), ""),
                    ("raw-replacement-none-source", Same("RegexReplace(None,'a'," + raw + ")","None"), ""),
                    ("raw-replacement-array", Same("RegexReplace(Array('a',None),'a'," + raw + ")","Array(" + raw + ",None)"), ""),
                    ("raw-replacement-empty-source", Same("RegexReplace('',''," + raw + ")","''"), ""),
                    ("raw-replacement-invalid-pattern", Same("RegexReplace('a','['," + raw + ")","None"), ""),
                    ("raw-replacement-multiple", Same("Substr(" + repeated + ",0,1)",raw) + " and " + Same("Substr(" + repeated + ",1,1)","'b'") + " and " + Same("Substr(" + repeated + ",2,1)",raw) + " and " + Same("Substr(" + repeated + ",3,1)","''"), ""),
                    ("raw-replacement-escaped-literal", Same("Substr(RegexReplace('a.a'," + Quote("\\.") + "," + raw + "),0,1)","'a'") + " and " + Same("Substr(RegexReplace('a.a'," + Quote("\\.") + "," + raw + "),1,1)",raw) + " and " + Same("Substr(RegexReplace('a.a'," + Quote("\\.") + "," + raw + "),2,1)","'a'") + " and " + Same("Substr(RegexReplace('a.a'," + Quote("\\.") + "," + raw + "),3,1)","''"), ""),
                    ("raw-replacement-capture-gap", Same("RegexReplace('a','a',Substr('é$9',1))","Substr('é',1,1)"), ""),
                    ("bare-size-snapshot", "RegexReplace(Size,'1','X') == 'X'", ""),
                    ("side-metadata", Same("RegexReplace(LeftSize,'1','L')","'L'") + " and " + Same("RegexReplace(RightSize,'[23]','R')","'R'") + (count == 3 ? " and " + Same("RegexReplace(MiddleSize,'2','M')","'M'") : ""), ""),
                    ("fullpath", Same("RegexReplace(FullPath,'row[.]bin$','ROW')","Array(" + string.Join(',',roots.Select(root => Quote(Path.Combine(root,"ROW")))) + ")"), ""),
                    ("folder-function", Same("RegexReplace(ToUpper(LeftName),'ROW','R')","'R.BIN'"), ""),
                    ("lazy-and", "not (false and RegexReplace('b','(a)?(b)','$1') == None)", ""),
                    ("lazy-or", "true or RegexReplace('b','(a)?(b)','$1') == None", ""),
                    ("lazy-if", "If(true," + Same("RegexReplace('a','a','X')","'X'") + ",RegexReplace('b','(a)?(b)','$1') == None)", ""),
                    ("logical-each", "AllEqual(Array(AndEach(RegexReplace(Array('aa','b'),'a','X') == 'XX',true),Array(true,false))) and AllEqual(Array(OrEach(RegexReplace(Array('aa','b'),'a','X') == 'XX',false),Array(true,false)))", ""),
                    ("typed-log-result", Same("LogInfo(RegexReplace('a','a','X'))","'X'"), "info: X" + nl),
                    ("budget-source-accepted", Same("RegexReplace(" + Quote(new string('a',4000)) + ",'z','X')",Quote(new string('a',4000))), ""),
                    ("budget-array-accepted", Same("RegexReplace(Choose(16," + Quote(new string('a',128)) + "),'z','X')","Choose(16," + Quote(new string('a',128)) + ")"), ""),
                    ("budget-search-accepted", Same("RegexReplace(" + Quote(new string('a',50)) + ",'a','X')",Quote(new string('X',50))), ""),
                    ("budget-output-accepted", Same("RegexReplace('a','a'," + Quote(new string('X',1000)) + ")",Quote(new string('X',1000))), ""),
                    ("budget-prep-accepted", "RegexCount(" + a5000 + ",'z') == 0 and " + Same("RegexReplace(''," + Quote(new string('b',102)) + "," + raw + ")","''"), ""),
                    ("budget-prep-standalone", Same("RegexReplace(''," + Quote(new string('b',104)) + "," + raw + ")","''"), "")
                }) await Compare(id, expression, events: events);
                await Compare("missing-side", "RegexReplace(Size,'1','X') == 'X'", missing: true);
                await Compare("multiple-row", "RegexReplace(LogInfo(LeftName),LogWarn(LeftName,'^alpha'),LogInfo(LeftName,'X')) == 'X.bin'", multiple: true);
                if (OperatingSystem.IsWindows()) await Compare("property-size", Same("RegexReplace(Prop('System.Size'),'[123]','X')","Array(" + string.Join(',',Enumerable.Repeat("'X'",count)) + ")"));
                var individual = Same("RegexReplace(" + a5000 + ",'z','X')",a5000);
                await Compare("budget-individual", individual);
                foreach (var (id, expression, diagnostic, events) in new[]
                {
                    ("unmatched-middle", "RegexReplace('b','(a)?(b)','$1')", "replacement", ""),
                    ("raw-unsupported-pattern", "RegexReplace('aba','(a)'," + raw + ")", "literal", ""),
                    ("raw-unsupported-boundary", "RegexReplace('aba'," + Quote("\\ba") + "," + raw + ")", "literal", ""),
                    ("first-error-skips-pattern", "RegexReplace(ToLower(ToLower(" + Quote(new string('A',14000)) + ")),LogWarn('unreached','a'),LogInfo('unreached','X'))", "65,536", ""),
                    ("pattern-error-retains-source", "RegexReplace(LogInfo('source','a'),ToLower(ToLower(" + Quote(new string('A',14000)) + ")),LogInfo('unreached','X'))", "65,536", "info: source a" + nl),
                    ("replacement-error-retains-pattern", "RegexReplace(LogInfo('source','a'),LogWarn('pattern','a'),ToLower(ToLower(" + Quote(new string('A',14000)) + ")))", "65,536", "info: source a" + nl + "warn: pattern a" + nl),
                    ("budget-source-rejected", "RegexReplace(" + Quote(new string('a',12000)) + ",'z','X')", "65,536", ""),
                    ("budget-array-rejected", "RegexReplace(" + budgetArray + ",'z','X')", "65,536", ""),
                    ("budget-search-rejected", "RegexReplace(" + Quote(new string('a',150)) + ",'a','X')", "65,536", ""),
                    ("budget-output-rejected", "RegexReplace('a','a'," + Quote(new string('X',12000)) + ")", "65,536", ""),
                    ("budget-prep-rejected", "RegexCount(" + a5000 + ",'z') == 0 and " + Same("RegexReplace(''," + Quote(new string('b',104)) + "," + raw + ")","''"), "65,536", ""),
                    ("budget-shared", sharedBudget, "65,536", ""),
                    ("timeout", "RegexReplace(" + Quote(new string('a',2000) + "!") + ",'(a+)+$','X')", "100", "")
                }) await Compare(id, expression, rejected: true, diagnostic: diagnostic, events: events);
                foreach (var (id, expression) in new[]
                {
                    ("zero", "RegexReplace()"), ("one", "RegexReplace('a')"), ("two", "RegexReplace('a','a')"), ("four", "RegexReplace('a','a','X',0)"),
                    ("bare", "RegexReplace"), ("empty", "RegexReplace(,,)"), ("trailing", "RegexReplace('a','a','X',)"), ("missing-close", "RegexReplace('a','a','X'"),
                    ("typed-regex", "RegexReplace(LogInfo('unreached','a'),Regex('a'),'X')"), ("unselected", "If(true,LogInfo('unreached',true),RegexReplace())")
                }) await Compare("invalid-" + id, expression, rejected: true, diagnostic: "RegexReplace");
            }
            var inventory = Path.Combine(directory, "observations.json"); await File.WriteAllTextAsync(inventory, JsonSerializer.Serialize(new { observations, preserved, retained, expectedCommands, windowsProperties = OperatingSystem.IsWindows(), newlineHex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(Environment.NewLine)) }));
            var reader = Path.Combine(directory, "verify-folder-regexreplace.py");
            await File.WriteAllTextAsync(reader, """
import hashlib,json,pathlib,sys,collections
data=json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8')); base=pathlib.Path(sys.argv[2]); cases=json.loads((base/'cases.json').read_text(encoding='utf-8')); golden={r['id']:r for r in map(json.loads,(base/'golden.ndjson').read_text(encoding='utf-8').splitlines())}
def public(a):
 t=a['type']; return all(map(public,a['value'])) if t=='array' else t in ['integer','double','bool','none'] or t=='string' 
native=[c for c in cases if c['function']=='regexreplace']; selected=[c for c in native if c['mode']=='function-body-noopt' and not c['nullArgs'] and len(c['args'])==3 and all(map(public,c['args']))]
def quote(s): return "'"+s.replace('\\','\\\\').replace("'","\\'").replace('\r','\\r').replace('\n','\\n').replace('\t','\\t')+"'"
def argument(a):
 t=a['type']
 if t=='none': return 'None'
 if t=='string': return quote(a['value'])
 if t=='bool': return 'true' if a['value'] else 'false'
 if t=='array': return 'Array('+','.join(map(argument,a['value']))+')'
 s=json.dumps(a['value']); return s if t=='integer' or any(c in s for c in '.eE') else s+'.0'
def expected_value(v):
 if v['kind']=='none': return 'None'
 if v['kind']=='integer': return str(v['value'])
 if v['kind']=='string': return quote(bytes.fromhex(v['utf8Hex']).decode('utf-8',errors='strict'))
 return 'Array('+','.join(map(expected_value,v['items']))+')'
assert len(native)==107 and len(selected)==74
assert collections.Counter(golden[c['id']]['value']['kind'] if golden[c['id']]['value'] is not None else 'exception' for c in selected)=={'string':50,'none':17,'array':6,'exception':1}
assert all(any(c['id']==id for c in selected) for id in ['replace-nul-subject','replace-nul-pattern','replace-nul-replacement'])
partition=json.loads((pathlib.Path(sys.argv[1]).parent/'native-partition.json').read_text(encoding='utf-8')); assert len(partition)==107 and len({r['id'] for r in partition})==107 and {r['id'] for r in partition}=={c['id'] for c in native}
assert {r['id'] for r in partition if not r['exclusion']}=={c['id'] for c in selected}
def exclusion(c):
 if c['mode']!='function-body-noopt': return 'optimizer-only'
 if c['nullArgs']: return 'null-args-adapter'
 if len(c['args'])!=3: return 'native-body-arity-public-parser-rejects'
 for arg in c['args']:
  if not public(arg): return 'adapter-argument-'+arg['type']
 return ''
assert {r['id']:r['exclusion'] for r in partition}=={c['id']:exclusion(c) for c in native}

assert all(r['expected']==golden[r['id']] for r in partition)
ff={r['id']:r for r in map(json.loads,(pathlib.Path(sys.argv[3])/'golden.ndjson').read_text().splitlines())}
for id in ['replacement-12-False','replacement-12-True']:
 native_ff=json.loads(bytes.fromhex(ff[id]['stdoutHex'])); assert native_ff['result']=={'type':'string','value':'ff62ff'},id
seen=[]; pairs=[]; errors=0; nl=bytes.fromhex(data['newlineHex'])
for row in data['observations']:
 seen.append(str(row['count'])+':'+row['id']); out=pathlib.Path(row['stdout']).read_bytes(); err=pathlib.Path(row['stderr']).read_bytes(); expected=bytes.fromhex(row['eventsHex'])
 assert pathlib.Path(row['filter']).read_text(encoding='utf-8-sig')=='def: exclude\nfe: '+row['expression']+'\n',row['id']
 side_names=['left','right'] if row['count']==2 else ['left','middle','right']; assert len(row['inputRoots'])==row['count']
 protected={row['filter']}|{str(pathlib.Path(root)/name) for side,root in zip(side_names,row['inputRoots']) for name,value in row['expectedRows'].items() if side in value['present']}
 assert set(row['protectedBefore'])==set(row['protectedAfter'])==protected and row['protectedBefore']==row['protectedAfter'],row['id']
 assert all(data['retained'][p]==meta for p,meta in row['protectedBefore'].items()),row['id']
 marker_lines={
 'argument-order':['info: source a','warn: pattern a','info: replacement X'],
 'none-eager':['info: source none','warn: pattern a','info: replacement X'],
 'pattern-type-eager':['info: source a','warn: pattern false','info: replacement X'],
 'replacement-type-eager':['info: source a','warn: pattern a','info: replacement false'],
 'array-eager':['info: source [a, none]','warn: pattern a','info: replacement X'],
 'first-error-skips-pattern':[], 'pattern-error-retains-source':['info: source a'],
 'replacement-error-retains-pattern':['info: source a','warn: pattern a']}
 if row['id'] in marker_lines: assert expected==b''.join(line.encode()+nl for line in marker_lines[row['id']]),row['id']
 raw="Substr('é',0,1)"; repeated="RegexReplace('aba','a',"+raw+")"
 raw_cases={
  'raw-source-none':("RegexReplace("+raw+",'a','X')",'None'),
  'raw-pattern-none':("RegexReplace('abc',"+raw+",'X')",'None'),
  'raw-replacement-terminal':("RegexReplace('a','a',"+raw+")",raw),
  'raw-replacement-no-match':("RegexReplace('a','z',"+raw+")","'a'"),
  'raw-unsupported-pattern-no-match':("RegexReplace('b','(a)',"+raw+")","'b'"),
  'raw-replacement-none-source':("RegexReplace(None,'a',"+raw+")",'None'),
  'raw-replacement-array':("RegexReplace(Array('a',None),'a',"+raw+")",'Array('+raw+',None)'),
  'raw-replacement-empty-source':("RegexReplace('','',"+raw+")","''"),
  'raw-replacement-invalid-pattern':("RegexReplace('a','[',"+raw+")",'None')}
 if row['id'] in raw_cases:
  call,value=raw_cases[row['id']]; assert row['expression']=='AllEqual(Array('+call+','+value+'))' and row['included'] and not row['rejected'] and not expected,row['id']
 if row['id'].startswith('budget-prep-'):
  length=102 if row['id']=='budget-prep-accepted' else 104; standalone=row['id']=='budget-prep-standalone'; rejection=row['id']=='budget-prep-rejected'
  expression='@noopt '+('' if standalone else 'RegexCount('+quote('a'*5000)+",'z') == 0 and ")+"AllEqual(Array(RegexReplace('',"+quote('b'*length)+','+raw+"),''))"
  assert row['expression']==expression and row['included'] and row['rejected']==rejection and expected==b'',row['id']
  assert pathlib.Path(row['filter']).read_text(encoding='utf-8-sig')=='def: exclude\nfe: '+expression+'\n',row['id']
 if row['id']=='raw-replacement-capture-gap': assert row['expression']=="AllEqual(Array(RegexReplace('a','a',Substr('é$9',1)),Substr('é',1,1)))",row['id']
 if row['id']=='raw-replacement-multiple':
  expression=' and '.join('AllEqual(Array(Substr('+repeated+','+str(i)+',1),'+value+'))' for i,value in enumerate([raw,"'b'",raw,"''"]))
  assert row['expression']==expression and row['included'] and not row['rejected'] and not expected,row['id']
 if row['nativeId']:
  g=golden[row['nativeId']]; c=next(c for c in selected if c['id']==row['nativeId']); pairs.append((row['nativeId'],row['count'])); assert row['rejected']==(g['value'] is None)
  call='RegexReplace('+','.join(map(argument,c['args']))+')'; projection=call if g['value'] is None else 'AllEqual(Array('+call+','+expected_value(g['value'])+'))'
  assert row['expression']==('@cs ' if c['caseSensitive'] else '@ci ')+projection,row['id']
  if g['value'] is not None: assert row['included']
  else: assert b'replacement' in err,row['id']
 if row['rejected']:
  errors+=1; assert row['exitCode']==2 and not out and err.startswith(expected) and len(err)>len(expected),row['id']; assert row['diagnostic'].encode() in err,row['id']
  assert not any(line.startswith(x) for line in err[len(expected):].split(nl) for x in [b'info: ',b'warn: ',b'error: ']),row['id']
  if row['id'].startswith('invalid-'): assert row['diagnostic']=='RegexReplace' and b'RegexReplace' in err and expected==b'',row['id']
 else:
  assert row['exitCode']==(1 if row['included'] else 0),row['id']
  if row['multiple']:
   lines=err.split(nl); expected_lines=[b'info: alpha.bin',b'warn: alpha.bin ^alpha',b'info: alpha.bin X',b'info: beta.bin',b'warn: beta.bin ^alpha',b'info: beta.bin X']; assert lines[-1]==b'' and sorted(lines[:-1])==sorted(expected_lines),row['id']
   assert lines.index(expected_lines[0])<lines.index(expected_lines[1])<lines.index(expected_lines[2]) and lines.index(expected_lines[3])<lines.index(expected_lines[4])<lines.index(expected_lines[5]),row['id']
  else: assert err==expected,row['id']
  entries=json.loads(out)['entries']; expected_rows=row['expectedRows']; assert len(entries)==len(expected_rows) and {e['path'] for e in entries}==set(expected_rows),row['id']
  assert set(expected_rows)==({'alpha.bin','beta.bin'} if row['multiple'] else {'row.bin'}),row['id']; names={'left','right'} if row['count']==2 else {'left','middle','right'}
  for e in entries:
   x=expected_rows[e['path']]; present={side for side in ['left','middle','right'] if e.get(side) is not None}; assert present==set(x['present'])==({'left'} if row['missing'] else names),row['id']
   decision=e['path']=='alpha.bin' if row['multiple'] else row['included']; assert x['included']==decision and e['filtered']==(not decision),row['id']
   for side in present: assert e[side]['filtered']==(not decision),row['id']
 if row['id']=='timeout': assert row['duration']<10000,row['id']
assert len(pairs)==148 and set(pairs)=={(c['id'],n) for c in selected for n in [2,3]}
assert len(seen)==len(set(seen)) and seen==data['expectedCommands']
assert len(seen)==(278 if data['windowsProperties'] else 276)
assert errors==48
required=['raw-unsupported-pattern-no-match','function-case', 'argument-order', 'none-eager', 'pattern-type-eager', 'replacement-type-eager', 'array-eager', 'pattern-array-type', 'replacement-array-type', 'empty-array-invalid-pattern', 'date-source', 'timestamp-source', 'raw-source-none', 'raw-pattern-none', 'raw-replacement-terminal', 'raw-replacement-no-match', 'raw-replacement-none-source', 'raw-replacement-array', 'raw-replacement-empty-source', 'raw-replacement-invalid-pattern', 'raw-replacement-multiple', 'raw-replacement-escaped-literal', 'raw-replacement-capture-gap', 'bare-size-snapshot', 'side-metadata', 'fullpath', 'folder-function', 'lazy-and', 'lazy-or', 'lazy-if', 'logical-each', 'typed-log-result', 'budget-source-accepted', 'budget-array-accepted', 'budget-search-accepted', 'budget-output-accepted', 'budget-prep-accepted', 'budget-prep-standalone', 'unmatched-middle', 'raw-unsupported-pattern', 'raw-unsupported-boundary', 'first-error-skips-pattern', 'pattern-error-retains-source', 'replacement-error-retains-pattern', 'budget-source-rejected', 'budget-array-rejected', 'budget-search-rejected', 'budget-output-rejected', 'budget-prep-rejected', 'budget-shared', 'timeout', 'invalid-zero', 'invalid-one', 'invalid-two', 'invalid-four', 'invalid-bare', 'invalid-empty', 'invalid-trailing', 'invalid-missing-close', 'invalid-typed-regex', 'invalid-unselected', 'missing-side', 'multiple-row', 'budget-individual']
for n in [2,3]:
 for case in required: assert str(n)+':'+case in seen,case
for path,sha in data['preserved'].items(): assert hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()==sha,path

for path,meta in data['retained'].items():
 st=pathlib.Path(path).stat(); assert str(st.st_size)==meta['length'] and st.st_mtime_ns//100==int(meta['writeTicks'])-621355968000000000,path
 assert hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()==meta['sha256'],path
print(json.dumps(dict(passed=True,nativeCases=107,publicBodies=74,nativeOnly=33,nativeProjections=len(pairs),commands=len(seen),expectedErrors=errors,preservedFiles=len(data['preserved']),interRowOrderQualified=False,cancellationInjected=False)))
""", new UTF8Encoding(false));
            await Reader("folder-regexreplace", reader, inventory, oracle, replaceOracle);
        }

        async Task RegexCountValues()
        {
            var oracle = Path.GetFullPath("tests/Fixtures/LineTransformRegex");
            var directory = Path.Combine(work, "regexcount"); Directory.CreateDirectory(directory);
            async Task Reader(string script, params string[] arguments)
            {
                var start = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var argument in new[] { "-E", "-B", "-X", "utf8", script }.Concat(arguments)) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); var evidence = await stdout + await stderr;
                await File.WriteAllTextAsync(Path.Combine(directory, Path.GetFileNameWithoutExtension(script) + "-result.json"), evidence);
                check("folder RegexCount independent " + Path.GetFileName(script), process.ExitCode == 0, evidence);
            }
            await Reader(Path.Combine(oracle, "verify.py"), "--verify", "--git");
            using var casesDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(oracle, "cases.json")));
            var all = casesDocument.RootElement.EnumerateArray().Where(item => item.GetProperty("function").GetString() == "regexcount").ToArray();
            var golden = File.ReadAllLines(Path.Combine(oracle, "golden.ndjson")).Select(line => { using var row = JsonDocument.Parse(line); return row.RootElement.Clone(); })
                .ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
            static bool PublicArgument(JsonElement value)
            {
                var type = value.GetProperty("type").GetString();
                return type == "array" ? value.GetProperty("value").EnumerateArray().All(PublicArgument)
                    : type is "integer" or "double" or "bool" or "none" || type == "string" && !value.GetProperty("value").GetString()!.Contains('\0');
            }
            static string Exclusion(JsonElement row)
            {
                if (row.GetProperty("mode").GetString() != "function-body-noopt") return "optimizer-only";
                if (row.GetProperty("nullArgs").GetBoolean()) return "null-args-adapter";
                if (row.GetProperty("args").GetArrayLength() != 2) return "native-body-arity-public-parser-rejects";
                foreach (var argument in row.GetProperty("args").EnumerateArray()) if (!PublicArgument(argument))
                    return argument.GetProperty("type").GetString() == "string" ? "embedded-nul-literal-projection" : "adapter-argument-" + argument.GetProperty("type").GetString();
                return "";
            }
            var selected = all.Where(row => Exclusion(row).Length == 0).ToArray();
            var partition = all.Select(row => new { id = row.GetProperty("id").GetString(), exclusion = Exclusion(row), expected = golden[row.GetProperty("id").GetString()!] }).ToArray();
            await File.WriteAllTextAsync(Path.Combine(directory, "native-partition.json"), JsonSerializer.Serialize(partition));
            check("folder RegexCount complete native partition", all.Length == 81 && selected.Length == 59
                && selected.Count(row => golden[row.GetProperty("id").GetString()!].GetProperty("value").ValueKind == JsonValueKind.Null) == 2
                && selected.Count(row => golden[row.GetProperty("id").GetString()!].GetProperty("value").ValueKind != JsonValueKind.Null
                    && golden[row.GetProperty("id").GetString()!].GetProperty("value").GetProperty("kind").GetString() == "integer") == 44,
                "59 public bodies: 44 integer / 9 none / 4 array / 2 exceptions; 22 native-only IDs retained");
            static string Quote(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            static string Argument(JsonElement value) => value.GetProperty("type").GetString() switch
            {
                "none" => "None", "string" => Quote(value.GetProperty("value").GetString()!),
                "integer" => value.GetProperty("value").GetRawText(),
                "double" => value.GetProperty("value").GetRawText().IndexOfAny(['.', 'e', 'E']) >= 0 ? value.GetProperty("value").GetRawText() : value.GetProperty("value").GetRawText() + ".0",
                "bool" => value.GetProperty("value").GetBoolean() ? "true" : "false",
                "array" => "Array(" + string.Join(',', value.GetProperty("value").EnumerateArray().Select(Argument)) + ")",
                _ => throw new InvalidDataException("Unprojectable regex argument")
            };
            static string Expected(JsonElement value) => value.GetProperty("kind").GetString() switch
            {
                "none" => "None", "integer" => value.GetProperty("value").GetRawText(),
                "array" => "Array(" + string.Join(',', value.GetProperty("items").EnumerateArray().Select(Expected)) + ")",
                _ => throw new InvalidDataException("Unprojectable RegexCount result")
            };
            var preserved = new Dictionary<string, string>(); var observations = new List<object>(); var expectedCommands = new List<string>();
            static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(directory, "sides-" + count, "side-" + side)).ToArray();
                foreach (var (root, side) in roots.Select((root, side) => (root, side)))
                {
                    Directory.CreateDirectory(root); var input = Path.Combine(root, "row.bin"); File.WriteAllBytes(input, Enumerable.Repeat((byte)(side + 1), side + 1).ToArray());
                    File.SetLastWriteTimeUtc(input, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)); preserved[input] = Hash(input);
                }
                async Task Compare(string id, string expression, bool included = true, bool rejected = false, string events = "", string diagnostic = "", string? nativeId = null, bool multiple = false, bool missing = false)
                {
                    expectedCommands.Add(count + ":" + id);
                    var filter = Filter("regexcount-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"); preserved[filter] = Hash(filter);
                    var activeRoots = roots; var sideNames = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                    var expectedRows = new Dictionary<string, (bool Included, string[] Present)>(StringComparer.Ordinal) { ["row.bin"] = (included, missing ? ["left"] : sideNames) };
                    if (multiple || missing)
                    {
                        activeRoots = Enumerable.Range(0, count).Select(side => Path.Combine(directory, (multiple ? "multiple-" : "missing-") + count, "side-" + side)).ToArray();
                        foreach (var (root, side) in activeRoots.Select((root, side) => (root, side)))
                        {
                            Directory.CreateDirectory(root);
                            foreach (var (name, size) in multiple ? new[] { ("alpha.bin", 1), ("beta.bin", 3) } : side == 0 ? new[] { ("row.bin", 1) } : Array.Empty<(string, int)>())
                            { var input = Path.Combine(root, name); File.WriteAllBytes(input, Enumerable.Repeat((byte)(side + 1), size).ToArray()); preserved[input] = Hash(input); }
                        }
                        if (multiple) { expectedRows.Clear(); expectedRows.Add("alpha.bin", (true, sideNames)); expectedRows.Add("beta.bin", (false, sideNames)); }
                    }
                    var expectedRowEvidence = expectedRows.ToDictionary(pair => pair.Key, pair => new { included = pair.Value.Included, present = pair.Value.Present });
                    await File.WriteAllTextAsync(Path.Combine(directory, count + "-" + id + "-expected-rows.json"), JsonSerializer.Serialize(expectedRowEvidence));
                    List<string> args = ["--folder", activeRoots[0], activeRoots[^1], "--filter", filter, "--show-filtered"]; if (count == 3) args.AddRange(["--middle", activeRoots[1]]);
                    var result = await run("folder-regexcount-" + count + "-" + id, rejected ? 2 : included ? 1 : 0, !rejected, args.ToArray());
                    var stdout = Path.Combine(directory, count + "-" + id + ".stdout"); var stderr = Path.Combine(directory, count + "-" + id + ".stderr");
                    await File.WriteAllTextAsync(stdout, result.Stdout, new UTF8Encoding(false)); await File.WriteAllTextAsync(stderr, result.Stderr, new UTF8Encoding(false));
                    bool EventsMatch()
                    {
                        if (!multiple) return result.Stderr == events;
                        var lines = result.Stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                        var expected = new[] { "info: alpha.bin", "warn: alpha.bin a", "info: beta.bin", "warn: beta.bin a" };
                        return lines.Length == expected.Length && lines.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal))
                            && Array.IndexOf(lines, expected[0]) < Array.IndexOf(lines, expected[1]) && Array.IndexOf(lines, expected[2]) < Array.IndexOf(lines, expected[3]);
                    }
                    var noParserEvents = !id.StartsWith("invalid-", StringComparison.Ordinal) || !result.Stderr.Split(Environment.NewLine, StringSplitOptions.None)
                        .Any(line => line.StartsWith("info: ", StringComparison.Ordinal) || line.StartsWith("warn: ", StringComparison.Ordinal) || line.StartsWith("error: ", StringComparison.Ordinal));
                    check("folder RegexCount streams/order " + count + " " + id, rejected ? result.Stdout.Length == 0 && result.Stderr.StartsWith(events, StringComparison.Ordinal)
                        && result.Stderr.Length > events.Length && result.Stderr.Contains(diagnostic, StringComparison.Ordinal) && noParserEvents : EventsMatch(), result.Stderr);
                    if (!rejected && result.ExitCode == (included ? 1 : 0))
                    {
                        using var json = JsonDocument.Parse(result.Stdout); var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                        check("folder RegexCount typed row and sides " + count + " " + id, entries.Length == expectedRows.Count
                            && entries.Select(entry => entry.GetProperty("path").GetString()!).ToHashSet(StringComparer.Ordinal).SetEquals(expectedRows.Keys)
                            && entries.All(entry =>
                            {
                                var expected = expectedRows[entry.GetProperty("path").GetString()!];
                                var present = new[] { "left", "middle", "right" }.Where(name => entry.TryGetProperty(name, out var side) && side.ValueKind != JsonValueKind.Null).ToHashSet(StringComparer.Ordinal);
                                return present.SetEquals(expected.Present) && entry.GetProperty("filtered").GetBoolean() == !expected.Included
                                    && expected.Present.All(name => entry.GetProperty(name).GetProperty("filtered").GetBoolean() == !expected.Included);
                            }), result.Stdout);
                    }
                    if (id == "timeout") check("folder RegexCount bounded timeout " + count, result.DurationMilliseconds < 10_000, result.DurationMilliseconds.ToString(CultureInfo.InvariantCulture));
                    observations.Add(new { id, nativeId, count, included, rejected, multiple, missing, expectedRows = expectedRowEvidence, expression, stdout, stderr, exitCode = result.ExitCode,
                        duration = result.DurationMilliseconds, diagnostic, eventsHex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(events)) });
                }
                foreach (var item in selected)
                {
                    var id = item.GetProperty("id").GetString()!; var expected = golden[id]; var failure = expected.GetProperty("value").ValueKind == JsonValueKind.Null;
                    var call = "RegexCount(" + string.Join(',', item.GetProperty("args").EnumerateArray().Select(Argument)) + ")";
                    await Compare("native-" + id, (item.GetProperty("caseSensitive").GetBoolean() ? "@cs " : "@ci ")
                        + (failure ? call : "AllEqual(Array(" + call + "," + Expected(expected.GetProperty("value")) + "))"), rejected: failure, diagnostic: failure ? "継続byte" : "", nativeId: id);
                }
                var nl = Environment.NewLine; var a200 = Quote(new string('a', 200)); var a400 = Quote(new string('a', 400));
                var a3000 = Quote(new string('a', 3000)); var a5000 = Quote(new string('a', 5000)); var a5100 = Quote(new string('a', 5100));
                foreach (var (id, expression, events) in new[]
                {
                    ("function-case", "REGEXCOUNT('Aa','a') == 2", ""),
                    ("argument-order", "RegexCount(LogInfo('source',123),LogWarn('pattern','[0-9]')) == 3", "info: source 123" + nl + "warn: pattern [0-9]" + nl),
                    ("none-eager", "RegexCount(LogInfo('source',None),LogWarn('pattern','a')) == None", "info: source none" + nl + "warn: pattern a" + nl),
                    ("pattern-type-eager", "RegexCount(LogInfo('source','abc'),LogWarn('pattern',false)) == None", "info: source abc" + nl + "warn: pattern false" + nl),
                    ("array-eager", "AllEqual(Array(RegexCount(LogInfo('source',Array('aa',None)),LogWarn('pattern','a')),Array(2,None)))", "info: source [aa, none]" + nl + "warn: pattern a" + nl),
                    ("array-pattern-type", "RegexCount(Array('a',None),Array('a')) == None", ""),
                    ("empty-array-invalid-pattern", "AllEqual(Array(RegexCount(Array(),'['),Array()))", ""),
                    ("date-source", "RegexCount(d'2024-01-02T03:04:05Z','^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}$') == 1", ""),
                    ("timestamp-source", "RegexCount(ToDateTime('2024-01-02T03:04:05Z'),'^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}$') == 1", ""),
                    ("raw-pattern-none", "RegexCount('abc',Substr('é',0,1)) == None", ""),
                    ("raw-source-pattern-type", "RegexCount(Substr('é',0,1),None) == None", ""),
                    ("none-skips-raw-pattern", "RegexCount(None,Substr('é',0,1)) == None", ""),
                    ("raw-source-invalid-pattern", "AllEqual(Array(RegexCount(Substr('é',0,1),LogWarn('pattern','[')),None))", "warn: pattern [" + nl),
                    ("bare-size-snapshot", "RegexCount(Size,'1') == 1", ""),
                    ("side-metadata", "RegexCount(LeftSize,'1') == 1 and RegexCount(RightSize,'[23]') == 1" + (count == 3 ? " and RegexCount(MiddleSize,'2') == 1" : ""), ""),
                    ("fullpath", "AllEqual(Array(RegexCount(FullPath,'row[.]bin$'),Array(" + string.Join(',', Enumerable.Repeat("1", count)) + ")))", ""),
                    ("folder-function", "@cs RegexCount(ToUpper(LeftName),'ROW') == 1", ""),
                    ("lazy-and", "not (false and RegexCount('é','') == 0)", ""),
                    ("lazy-or", "true or RegexCount('é','') == 0", ""),
                    ("lazy-if", "If(true,RegexCount('aa','a') == 2,RegexCount('é','') == 0)", ""),
                    ("logical-each", "AllEqual(Array(AndEach(RegexCount(Array('aa',''), 'a') == 2,true),Array(true,false))) and AllEqual(Array(OrEach(RegexCount(Array('aa',''), 'a') == 2,false),Array(true,false)))", ""),
                    ("typed-log-result", "LogInfo(RegexCount('aa','a')) == 2", "info: 2" + nl),
                    ("budget-scalar-accepted", "RegexCount(" + a5000 + ",'z') == 0", ""),
                    ("budget-pattern-accepted", "RegexCount('a',ToLower(" + Quote(new string('A', 2000)) + ")) == 0", ""),
                    ("budget-array-accepted", "AllEqual(Array(RegexCount(ChooseEach(Array(" + string.Join(',', Enumerable.Repeat("0", 16)) + ")," + Quote(new string('a', 128)) + "),'z'),Array(" + string.Join(',', Enumerable.Repeat("0", 16)) + ")))", ""),
                    ("budget-search-accepted", "RegexCount(" + a200 + ",'a') == 200", "")
                }) await Compare(id, expression, events: events);
                await Compare("budget-individual", "RegexCount(" + a3000 + ",'z') == 0");
                await Compare("missing-side", "RegexCount(Size,'1') == 1", missing: true);
                await Compare("multiple-row", "RegexCount(LogInfo(LeftName),LogWarn(LeftName,'a')) == 2", multiple: true);
                if (OperatingSystem.IsWindows()) await Compare("property-size", "AllEqual(Array(RegexCount(Prop('System.Size'),'[123]'),Array(" + string.Join(',', Enumerable.Repeat("1", count)) + ")))");
                foreach (var (id, expression, diagnostic, events) in new[]
                {
                    ("raw-source", "RegexCount(Substr('é',0,1),'a')", "UTF-8", ""),
                    ("array-mid-failure", "RegexCount(Array('abc','é'),'(?:)')", "継続byte", ""),
                    ("budget-scalar-rejected", "RegexCount(" + a5100 + ",'z')", "上限", ""),
                    ("budget-pattern-rejected", "RegexCount('a',ToLower(" + Quote(new string('A', 14000)) + "))", "上限", ""),
                    ("budget-array-rejected", "RegexCount(ChooseEach(Array(" + string.Join(',', Enumerable.Repeat("0", 64)) + ")," + Quote(new string('a', 128)) + "),'z')", "上限", ""),
                    ("budget-search-rejected", "RegexCount(" + a400 + ",'a')", "上限", ""),
                    ("budget-shared", "AllEqual(Array(RegexCount(" + a3000 + ",'z'),RegexCount(" + a3000 + ",'z'),0))", "上限", ""),
                    ("first-error-skips-pattern", "RegexCount(RegexCount(" + a5100 + ",'z'),LogWarn('unreached','a'))", "上限", ""),
                    ("pattern-error-retains-source", "RegexCount(LogInfo('first','a'),ToLower(" + Quote(new string('A', 14000)) + "))", "上限", "info: first a" + nl),
                    ("unsupported-han", "RegexCount('𠀀'," + Quote("\\p{Han}") + ")", "Han", ""),
                    ("timeout", "RegexCount(" + Quote(new string('a', 2000) + "!") + ",'(a+)+$')", "100", "")
                }) await Compare(id, expression, rejected: true, diagnostic: diagnostic, events: events);
                foreach (var (id, expression) in new[]
                {
                    ("zero", "RegexCount()"), ("one", "RegexCount('a')"), ("three", "RegexCount('a','a',0)"), ("four", "RegexCount('a','a',0,0)"),
                    ("bare", "RegexCount"), ("empty", "RegexCount(,)"), ("trailing", "RegexCount('a','a',)"), ("missing-close", "RegexCount('a','a'"),
                    ("typed-regex", "RegexCount(LogInfo('unreached','a'),Regex('a'))"), ("unselected", "If(true,LogInfo('unreached',true),RegexCount())")
                }) await Compare("invalid-" + id, expression, rejected: true, diagnostic: "RegexCount");
            }
            var inventory = Path.Combine(directory, "observations.json"); await File.WriteAllTextAsync(inventory, JsonSerializer.Serialize(new { observations, preserved, expectedCommands, windowsProperties = OperatingSystem.IsWindows(), newlineHex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(Environment.NewLine)) }));
            var reader = Path.Combine(directory, "verify-folder-regexcount.py");
            await File.WriteAllTextAsync(reader, """
import hashlib,json,pathlib,sys,collections
data=json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8')); base=pathlib.Path(sys.argv[2]); cases=json.loads((base/'cases.json').read_text(encoding='utf-8')); golden={r['id']:r for r in map(json.loads,(base/'golden.ndjson').read_text(encoding='utf-8').splitlines())}
def public(a):
 t=a['type']; return all(map(public,a['value'])) if t=='array' else t in ['integer','double','bool','none'] or t=='string' and '\0' not in a['value']
native=[c for c in cases if c['function']=='regexcount']; selected=[c for c in native if c['mode']=='function-body-noopt' and not c['nullArgs'] and len(c['args'])==2 and all(map(public,c['args']))]
def quote(s): return "'"+s.replace('\\','\\\\').replace("'","\\'").replace('\r','\\r').replace('\n','\\n').replace('\t','\\t')+"'"
def argument(a):
 t=a['type']
 if t=='none': return 'None'
 if t=='string': return quote(a['value'])
 if t=='bool': return 'true' if a['value'] else 'false'
 if t=='array': return 'Array('+','.join(map(argument,a['value']))+')'
 s=json.dumps(a['value']); return s if t=='integer' or any(c in s for c in '.eE') else s+'.0'
def expected_value(v):
 if v['kind']=='none': return 'None'
 if v['kind']=='integer': return str(v['value'])
 return 'Array('+','.join(map(expected_value,v['items']))+')'
assert len(native)==81 and len(selected)==59
assert collections.Counter(golden[c['id']]['value']['kind'] if golden[c['id']]['value'] is not None else 'exception' for c in selected)=={'integer':44,'none':9,'array':4,'exception':2}
partition=json.loads((pathlib.Path(sys.argv[1]).parent/'native-partition.json').read_text(encoding='utf-8')); assert len(partition)==81 and {r['id'] for r in partition}=={c['id'] for c in native}
assert {r['id'] for r in partition if not r['exclusion']}=={c['id'] for c in selected}
seen=[]; pairs=[]; errors=0; nl=bytes.fromhex(data['newlineHex'])
for row in data['observations']:
 seen.append(str(row['count'])+':'+row['id']); out=pathlib.Path(row['stdout']).read_bytes(); err=pathlib.Path(row['stderr']).read_bytes(); expected=bytes.fromhex(row['eventsHex'])
 if row['id']=='raw-source-invalid-pattern':
  assert not row['rejected'] and row['included'] and expected==b'warn: pattern ['+nl,row['id']
  assert row['expression']=="AllEqual(Array(RegexCount(Substr('é',0,1),LogWarn('pattern','[')),None))",row['id']
 if row['nativeId']:
  g=golden[row['nativeId']]; c=next(c for c in selected if c['id']==row['nativeId']); pairs.append((row['nativeId'],row['count'])); assert row['rejected']==(g['value'] is None)
  call='RegexCount('+','.join(map(argument,c['args']))+')'; projection=call if g['value'] is None else 'AllEqual(Array('+call+','+expected_value(g['value'])+'))'
  assert row['expression']==('@cs ' if c['caseSensitive'] else '@ci ')+projection,row['id']
  if g['value'] is not None: assert row['included']
  else: assert '継続byte'.encode() in err,row['id']
 if row['rejected']:
  errors+=1; assert row['exitCode']==2 and not out and err.startswith(expected) and len(err)>len(expected),row['id']; assert row['diagnostic'].encode() in err,row['id']
  assert not any(line.startswith(x) for line in err[len(expected):].split(nl) for x in [b'info: ',b'warn: ',b'error: ']),row['id']
  if row['id'].startswith('invalid-'): assert row['diagnostic']=='RegexCount' and b'RegexCount' in err and expected==b'',row['id']
 else:
  assert row['exitCode']==(1 if row['included'] else 0),row['id']
  if row['multiple']:
   lines=err.split(nl); expected_lines=[b'info: alpha.bin',b'warn: alpha.bin a',b'info: beta.bin',b'warn: beta.bin a']; assert lines[-1]==b'' and sorted(lines[:-1])==sorted(expected_lines),row['id']
   assert lines.index(expected_lines[0])<lines.index(expected_lines[1]) and lines.index(expected_lines[2])<lines.index(expected_lines[3]),row['id']
  else: assert err==expected,row['id']
  entries=json.loads(out)['entries']; expected_rows=row['expectedRows']; assert len(entries)==len(expected_rows) and {e['path'] for e in entries}==set(expected_rows),row['id']
  assert set(expected_rows)==({'alpha.bin','beta.bin'} if row['multiple'] else {'row.bin'}),row['id']; names={'left','right'} if row['count']==2 else {'left','middle','right'}
  for e in entries:
   x=expected_rows[e['path']]; present={side for side in ['left','middle','right'] if e.get(side) is not None}; assert present==set(x['present'])==({'left'} if row['missing'] else names),row['id']
   decision=e['path']=='alpha.bin' if row['multiple'] else row['included']; assert x['included']==decision and e['filtered']==(not decision),row['id']
   for side in present: assert e[side]['filtered']==(not decision),row['id']
 if row['id']=='timeout': assert row['duration']<10000,row['id']
assert len(pairs)==118 and set(pairs)=={(c['id'],n) for c in selected for n in [2,3]}
assert len(seen)==len(set(seen)) and seen==data['expectedCommands']
assert len(seen)==(220 if data['windowsProperties'] else 218)
assert errors==46
required=['argument-order','none-eager','pattern-type-eager','array-eager','array-pattern-type','empty-array-invalid-pattern','bare-size-snapshot','side-metadata','fullpath','folder-function','multiple-row','missing-side','date-source','timestamp-source','raw-pattern-none','raw-source','raw-source-invalid-pattern','raw-source-pattern-type','none-skips-raw-pattern','budget-scalar-accepted','budget-scalar-rejected','budget-pattern-accepted','budget-pattern-rejected','budget-array-accepted','budget-array-rejected','budget-search-accepted','budget-search-rejected','budget-individual','budget-shared','timeout']
for n in [2,3]:
 for case in required: assert str(n)+':'+case in seen,case
for path,sha in data['preserved'].items(): assert hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()==sha,path
print(json.dumps(dict(passed=True,nativeCases=81,publicBodies=59,nativeOnly=22,nativeProjections=len(pairs),commands=len(seen),expectedErrors=errors,preservedFiles=len(data['preserved']),interRowOrderQualified=False,cancellationInjected=False)))
""", new UTF8Encoding(false));
            await Reader(reader, inventory, oracle);
        }

        async Task LoggingValues()
        {
            var oracle = Path.GetFullPath("tests/Fixtures/LineTransformLogging");
            var directory = Path.Combine(work, "logging"); Directory.CreateDirectory(directory);
            async Task Reader(string script, params string[] arguments)
            {
                var start = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var argument in new[] { "-E", "-B", "-X", "utf8", script }.Concat(arguments)) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); var evidence = await stdout + await stderr;
                await File.WriteAllTextAsync(Path.Combine(directory, Path.GetFileNameWithoutExtension(script) + "-result.json"), evidence);
                check("folder logging independent " + Path.GetFileName(script), process.ExitCode == 0, evidence);
            }
            await Reader(Path.Combine(oracle, "verify.py"));
            using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(oracle, "cases.json")));
            var golden = File.ReadAllLines(Path.Combine(oracle, "golden.ndjson")).Select(line => JsonDocument.Parse(line)).ToArray();
            static string Quote(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            static string Literal(JsonElement value) => value.ValueKind switch
            {
                JsonValueKind.Null => "None", JsonValueKind.True => "true", JsonValueKind.False => "false",
                JsonValueKind.String => Quote(value.GetString()!),
                JsonValueKind.Array => "Array(" + string.Join(',', value.EnumerateArray().Select(Literal)) + ")",
                JsonValueKind.Number => value.GetRawText(),
                _ => throw new InvalidDataException("Unsupported native logging value")
            };
            var failure = "Strlen(ChooseEach(Array(" + string.Join(',', Enumerable.Repeat("0", 128)) + "),'" + new string('x', 1024) + "'))";
            string Node(JsonElement node) => node.GetProperty("op").GetString() switch
            {
                "value" => Literal(node.GetProperty("value")), "throw" => failure,
                "if" => "If(" + string.Join(',', node.GetProperty("args").EnumerateArray().Select(Node)) + ")",
                "log" => new[] { "LogError", "LogWarn", "LogInfo" }[node.GetProperty("level").GetInt32()] + "(" + string.Join(',', node.GetProperty("args").EnumerateArray().Select(Node)) + ")",
                _ => throw new InvalidDataException("Unsupported native logging node")
            };
            JsonElement Last(JsonElement node)
            {
                if (node.GetProperty("op").GetString() == "value") return node.GetProperty("value");
                var args = node.GetProperty("args").EnumerateArray().ToArray();
                if (node.GetProperty("op").GetString() == "if")
                {
                    var condition = Last(args[0]); return Last(args[condition.ValueKind == JsonValueKind.True ? 1 : 2]);
                }
                return Last(args[^1]);
            }
            var observations = new List<object>(); var preserved = new Dictionary<string, string>();
            static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(directory, "sides-" + count, "side-" + side)).ToArray();
                foreach (var (root, side) in roots.Select((root, side) => (root, side)))
                {
                    Directory.CreateDirectory(root); var input = Path.Combine(root, "row.bin"); File.WriteAllBytes(input, Enumerable.Repeat((byte)(side + 1), side + 1).ToArray()); preserved[input] = Hash(input);
                }
                var sentinel = Path.Combine(directory, "sentinel-" + count); File.WriteAllText(sentinel, "existing output retained"); preserved[sentinel] = Hash(sentinel);
                async Task Compare(string id, string expression, string expectedEvents, bool included = true, bool rejected = false, string? nativeId = null, string? rules = null, bool missing = false, bool multiple = false)
                {
                    var filter = Filter("logging-" + count + "-" + id, "name: unrelated file label\ndef: exclude\n" + (rules ?? "fe: " + expression + "\n")); preserved[filter] = Hash(filter);
                    var activeRoots = roots;
                    if (missing)
                    {
                        activeRoots = Enumerable.Range(0, count).Select(side => Path.Combine(directory, "missing-" + count, "side-" + side)).ToArray();
                        foreach (var root in activeRoots) Directory.CreateDirectory(root);
                        var input = Path.Combine(activeRoots[0], "row.bin"); File.WriteAllBytes(input, [1]); preserved[input] = Hash(input);
                    }
                    var sideNames = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                    var expectedRows = new Dictionary<string, (bool Included, string[] Present)>(StringComparer.Ordinal)
                    { ["row.bin"] = (included, missing ? ["left"] : sideNames) };
                    if (multiple)
                    {
                        activeRoots = Enumerable.Range(0, count).Select(side => Path.Combine(directory, "multiple-" + count, "side-" + side)).ToArray();
                        foreach (var (root, side) in activeRoots.Select((root, side) => (root, side)))
                        {
                            Directory.CreateDirectory(root);
                            foreach (var (name, size) in new[] { ("alpha.bin", 1), ("beta.bin", 3) })
                            {
                                var input = Path.Combine(root, name); File.WriteAllBytes(input, Enumerable.Repeat((byte)(side + 1), size).ToArray()); preserved[input] = Hash(input);
                            }
                        }
                        expectedRows.Clear(); expectedRows.Add("alpha.bin", (true, sideNames)); expectedRows.Add("beta.bin", (false, sideNames));
                    }
                    var expectedRowEvidence = expectedRows.ToDictionary(pair => pair.Key, pair => new { included = pair.Value.Included, present = pair.Value.Present });
                    await File.WriteAllTextAsync(Path.Combine(directory, count + "-" + id + "-expected-rows.json"), JsonSerializer.Serialize(expectedRowEvidence));
                    List<string> args = ["--folder", activeRoots[0], activeRoots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) args.AddRange(["--middle", activeRoots[1]]);
                    var result = await run("folder-logging-" + count + "-" + id, rejected ? 2 : included ? 1 : 0, !rejected, args.ToArray());
                    var stdout = Path.Combine(directory, count + "-" + id + ".stdout"); var stderr = Path.Combine(directory, count + "-" + id + ".stderr");
                    await File.WriteAllTextAsync(stdout, result.Stdout, new UTF8Encoding(false)); await File.WriteAllTextAsync(stderr, result.Stderr, new UTF8Encoding(false));
                    check("folder logging channels and event order " + count + " " + id, rejected
                        ? result.Stdout.Length == 0 && result.Stderr.StartsWith(expectedEvents, StringComparison.Ordinal) && result.Stderr.Length > expectedEvents.Length
                        : multiple ? result.Stderr.Split(Environment.NewLine, StringSplitOptions.None).Order(StringComparer.Ordinal)
                            .SequenceEqual(expectedEvents.Split(Environment.NewLine, StringSplitOptions.None).Order(StringComparer.Ordinal))
                        : result.Stderr == expectedEvents, result.Stderr);
                    if (!rejected && result.ExitCode == (included ? 1 : 0))
                    {
                        using var json = JsonDocument.Parse(result.Stdout); var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                        check("folder logging shared row result " + count + " " + id,
                            entries.Length == expectedRows.Count && entries.Select(entry => entry.GetProperty("path").GetString()!).ToHashSet(StringComparer.Ordinal).SetEquals(expectedRows.Keys)
                            && entries.All(entry =>
                            {
                                var expected = expectedRows[entry.GetProperty("path").GetString()!];
                                var present = new[] { "left", "middle", "right" }.Where(name => entry.TryGetProperty(name, out var side) && side.ValueKind != JsonValueKind.Null).ToHashSet(StringComparer.Ordinal);
                                return present.SetEquals(expected.Present) && entry.GetProperty("filtered").GetBoolean() == !expected.Included
                                    && expected.Present.All(name => entry.GetProperty(name).GetProperty("filtered").GetBoolean() == !expected.Included);
                            }), result.Stdout);
                    }
                    observations.Add(new { id, nativeId, count, included, rejected, missing, multiple, expectedRows = expectedRowEvidence, filter, stdout, stderr, exitCode = result.ExitCode, expectedEventsHex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(expectedEvents)) });
                }
                var projected = 0;
                foreach (var item in cases.RootElement.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString()!;
                    if (id.StartsWith("zero-args", StringComparison.Ordinal) || id.StartsWith("directive-", StringComparison.Ordinal)) continue;
                    var expected = golden.Single(row => row.RootElement.GetProperty("id").GetString() == id).RootElement;
                    var rejected = !expected.GetProperty("success").GetBoolean();
                    var events = string.Concat(expected.GetProperty("logs").EnumerateArray().Select(log => new[] { "error", "warn", "info" }[log.GetProperty("level").GetInt32()] + ": "
                        + Encoding.UTF8.GetString(Convert.FromHexString(log.GetProperty("messageHex").GetString()!)) + Environment.NewLine));
                    var call = Node(item.GetProperty("node"));
                    var expression = item.GetProperty("directives").GetString()!.Replace("log-call", rejected ? call : "AllEqual(Array(" + call + "," + Literal(Last(item.GetProperty("node"))) + "))", StringComparison.Ordinal);
                    await Compare("native-" + id, expression, events, rejected: rejected, nativeId: id); projected++;
                }
                check("folder logging native public coverage " + count, projected == 28, "25 successful body projections + 3 managed-budget failure projections; 7 native-only directive/zero-arity exclusions");
                foreach (var (id, expression, events, included) in new[]
                {
                    ("empty-name", "@name=\"\" LogInfo(true)", "info: true" + Environment.NewLine, true),
                    ("typed-false", "LogInfo(false)", "info: false" + Environment.NewLine, false),
                    ("lazy-and", "false and LogInfo(true)", "", false), ("lazy-or", "true or LogError(false)", "", true),
                    ("left-right", "LogInfo(LogError('a'),LogWarn('b'),true)", "error: a" + Environment.NewLine + "warn: b" + Environment.NewLine + "info: a b true" + Environment.NewLine, true),
                    ("side-metadata", "LogInfo(LeftSize,RightSize,true)", "info: 1 " + count + " true" + Environment.NewLine, true),
                    ("bare-metadata", "AllEqual(Array(LogInfo(Size),Size))", "info: [" + string.Join(", ", Enumerable.Range(1, count)) + "]" + Environment.NewLine, true)
                }) await Compare(id, expression, events, included);
                await Compare("missing-side", "LogInfo(true)", "info: true" + Environment.NewLine, missing: true);
                await Compare("multiple-row", "LogInfo(LeftName,LeftSize < 2b)", "info: alpha.bin true" + Environment.NewLine + "info: beta.bin false" + Environment.NewLine, multiple: true);
                await Compare("lazy-rule", "", "", rules: "fe: true\nfe: LogError(false)\n");
                await Compare("rule-order", "", "info: false" + Environment.NewLine + "warn: true" + Environment.NewLine, rules: "fe: LogInfo(false)\nfe: LogWarn(true)\n");
                foreach (var function in new[] { "LogError", "LogWarn", "LogInfo" })
                    foreach (var suffix in new[] { "()", "(,)", "(true,)", "(true", "" })
                        await Compare("invalid-" + function + "-" + Array.IndexOf(new[] { "()", "(,)", "(true,)", "(true", "" }, suffix), function + suffix, "", rejected: true);
                await Compare("unselected-invalid", "If(true,true,LogInfo())", "", rejected: true);
            }
            foreach (var row in golden) row.Dispose();
            var inventory = Path.Combine(directory, "observations.json"); await File.WriteAllTextAsync(inventory, JsonSerializer.Serialize(new { observations, preserved, newlineHex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(Environment.NewLine)) }));
            var reader = Path.Combine(directory, "verify-folder-logging.py");
            await File.WriteAllTextAsync(reader, """
import hashlib,json,pathlib,sys
p=pathlib.Path(sys.argv[1]); data=json.loads(p.read_text(encoding='utf-8')); golden={r['id']:r for r in map(json.loads,pathlib.Path(sys.argv[2]).read_text(encoding='utf-8').splitlines())}; native=0; errors=0
for row in data['observations']:
 out=pathlib.Path(row['stdout']).read_bytes(); err=pathlib.Path(row['stderr']).read_bytes(); expected=bytes.fromhex(row['expectedEventsHex'])
 if row['nativeId']:
  g=golden[row['nativeId']]; expected=b''.join([['error','warn','info'][e['level']].encode()+b': '+bytes.fromhex(e['messageHex'])+bytes.fromhex(data['newlineHex']) for e in g['logs']]); native+=1
 if row['rejected']:
  assert row['exitCode']==2 and not out and err.startswith(expected) and len(err)>len(expected),row['id']; errors+=1
  # 診断をイベントとして数えず、未完了loggerが公開されないことも確認する。
  assert not any(err[len(expected):].startswith(x) for x in [b'error: ',b'warn: ',b'info: ']),row['id']
 else:
  assert row['exitCode']==(1 if row['included'] else 0),row['id']
  if row['multiple']: assert sorted(err.splitlines(keepends=True))==sorted(expected.splitlines(keepends=True)),row['id']
  else: assert err==expected,row['id']
  entries=json.loads(out)['entries']; expected_rows=row['expectedRows']; assert len(entries)==len(expected_rows) and {e['path'] for e in entries}==set(expected_rows),row['id']
  names={'left','right'} if row['count']==2 else {'left','middle','right'}
  assert set(expected_rows)==({'alpha.bin','beta.bin'} if row['multiple'] else {'row.bin'}),row['id']
  for entry in entries:
   expected_row=expected_rows[entry['path']]; present={side for side in ['left','middle','right'] if entry.get(side) is not None}
   assert set(expected_row['present'])==({'left'} if row['missing'] else names) and present==set(expected_row['present']),row['id']
   decision=entry['path']=='alpha.bin' if row['multiple'] else row['included']; assert expected_row['included']==decision and entry['filtered']==(not decision),row['id']
   for side in present: assert entry[side]['filtered']==(not decision),row['id']
for path,sha in data['preserved'].items(): assert hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()==sha,path
assert native==56 and len(data['observations'])==110,(native,len(data['observations']))
assert sum(r['multiple'] for r in data['observations'])==2
print(json.dumps(dict(passed=True,commands=len(data['observations']),nativeBodyProjections=native,expectedErrors=errors,preservedFiles=len(data['preserved']),folderInvocationCountQualified=True,interRowOrderQualified=False,nativeOuterRecoveryQualified=False)))
""", new UTF8Encoding(false));
            await Reader(reader, inventory, Path.Combine(oracle, "golden.ndjson"));
        }

        async Task ClockValues()
        {
            var oracle = Path.GetFullPath("tests/Fixtures/LineTransformTimestampClockFunctions");
            var info = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-E", "-B", Path.Combine(oracle, "verify.py"), "--archive", Path.Combine(oracle, "clock-native-portable-v1.zip"), "--fixed", Path.Combine(oracle, "fixed-manifest.json") }) info.ArgumentList.Add(argument);
            using (var process = Process.Start(info)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); var evidence = await stdout + await stderr;
                await File.WriteAllTextAsync(Path.Combine(work, "independent-folder-clock-oracle.json"), evidence);
                check("folder clock fixed native dynamic oracle", process.ExitCode == 0, evidence);
            }
            check("folder clock fixed closure SHA", Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(oracle, "clock-native-portable-v1.zip"))))
                == "23339417df345ce7332b4dfaf4146cad32d51d1fde433beb1d7a42d8a969a1d0", "Fixed Native dynamic relations; no historical clock value becomes today's golden.");
            var evidenceRows = new List<object>();
            static string Profile()
            {
                var native = "";
                if (OperatingSystem.IsWindows())
                {
                    var state = ToDateTimeGetTimeZoneInformation(out var zone);
                    native = $"|{state}|{zone.Bias}|{zone.StandardBias}|{zone.DaylightBias}|{zone.StandardName}|{ToDateTimeGetUserDefaultLCID()}";
                }
                return TimeZoneInfo.Local.Id + "|" + TimeZoneInfo.Local.BaseUtcOffset + "|" + Environment.GetEnvironmentVariable("TZ") + native;
            }
            static bool HasNativeTokyoMidnightProfile()
            {
                // 原典が未採取のDST/profileへBCLから独立goldenを作らない。
                if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TZ"))
                    || TimeZoneInfo.Local.Id != "Tokyo Standard Time" || TimeZoneInfo.Local.SupportsDaylightSavingTime) return false;
                var locale = new StringBuilder(85);
                if (ToDateTimeGetUserDefaultLocaleName(locale, 85) == 0 || locale.ToString() != "ja-JP" || ToDateTimeGetUserDefaultLCID() != 1041) return false;
                var state = ToDateTimeGetTimeZoneInformation(out var zone);
                return state == 0 && zone.Bias == -540 && zone.StandardBias == 0 && zone.DaylightBias == -60;
            }
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "clock-" + count, "side-" + side)).ToArray();
                for (var side = 0; side < count; side++)
                {
                    Directory.CreateDirectory(roots[side]);
                    foreach (var name in new[] { "same.bin", "different.bin", "missing.bin" })
                    {
                        if (name == "missing.bin" && side == 1) continue;
                        File.WriteAllBytes(Path.Combine(roots[side], name), name == "different.bin" ? Enumerable.Repeat((byte)(side + 1), side + 1).ToArray() : [1]);
                    }
                }
                var hashes = roots.SelectMany(Directory.GetFiles).ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                var sentinel = Path.Combine(work, "clock-sentinel-" + count); File.WriteAllText(sentinel, "existing output retained");
                var sentinelHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sentinel)));
                var filters = new Dictionary<string, string>();
                async Task Compare(string id, string expression, bool included = true, bool rejected = false, string? rules = null)
                {
                    var filter = Filter("clock-" + count + "-" + id, "def: exclude\n" + (rules ?? "fe: " + expression + "\n"));
                    filters[filter] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(filter)));
                    List<string> args = ["--folder", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) args.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-clock-" + count + "-" + id, rejected ? 2 : included ? 1 : 0, !rejected, args.ToArray());
                    check("folder clock streams " + count + " " + id, rejected ? result.Stdout.Length == 0 && result.Stderr.Length > 0 : result.Stderr.Length == 0, result.Stderr);
                    if (!rejected && result.ExitCode == (included ? 1 : 0))
                    {
                        using var json = JsonDocument.Parse(result.Stdout);
                        var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                        check("folder clock snapshot and side flags " + count + " " + id, entries.Length == 3 && entries.All(entry => entry.GetProperty("filtered").GetBoolean() == !included
                            && (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name => !entry.TryGetProperty(name, out var side)
                                || side.ValueKind == JsonValueKind.Null || side.GetProperty("filtered").GetBoolean() == !included)), result.Stdout);
                    }
                }
                foreach (var (id, expression) in new[]
                {
                    ("now-repeated", "AllEqual(Array(Now(),NOW(),Now(),If(true,Now(),Today())))"),
                    ("today-repeated", "AllEqual(Array(Today(),TODAY(),Today(),If(false,Now(),Today())))"),
                    ("timestamp-types", "not AllEqual(Array(Now(),None)) and not AllEqual(Array(Today(),0b)) and not AllEqual(Array(Now(),false)) and ToDateTime(Now()) == None and ToDateTime(Today()) == None"),
                    ("array-types", "Len(Array(Now(),Today())) == 2b and AllEqual(Array(At(Array(Now(),Today()),0b),Now()))"),
                    ("local-date-relation", "ToDateStr(Now()) == ToDateStr(Today()) and Now() >= Today()"),
                    ("metadata", "Now() >= LeftDate and AllEqual(Array(Now(),Now()))"),
                    ("non-clock", "LeftSize >= 1b"),
                    ("short-circuit", "If(true,true,Now()) and (true or Today())")
                }) await Compare(id, expression);
                await Compare("now-not-predicate", "Now()", false);
                await Compare("today-not-predicate", "Today()", false);
                await Compare("noopt", "@noopt AllEqual(Array(Now(),Now())) and AllEqual(Array(Today(),Today()))");
                await Compare("separate-fe-de-e", "", rules: "fe: AllEqual(Array(Now(),Now()))\nde: AllEqual(Array(Today(),Today()))\ne: Now() >= Today()\n");
                foreach (var function in new[] { "Now", "Today" }) foreach (var (id, expression) in new[]
                {
                    ("one", function + "(None)"), ("two", function + "(None,None)"), ("bare", function), ("empty-comma", function + "(,)"),
                    ("missing-close", function + "("), ("extra-close", function + "())"), ("unselected", "If(true,true," + function + "(None))")
                }) await Compare(function + "-invalid-" + id, expression, rejected: true);
                check("folder clock input filter sentinel SHA " + count, hashes.Concat(filters).All(pair => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value)
                    && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sentinel))) == sentinelHash, "Input/filter/sentinel bytes retained.");

                // 他の関数の依存flagを借りず、ClockValue自身がDateの全側配列を要求することを守る。
                var bareRoots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "clock-bare-date-" + count, "side-" + side)).ToArray();
                var bareBefore = DateTime.UtcNow;
                var past = new DateTime(bareBefore.AddDays(-2).Ticks / 10 * 10, DateTimeKind.Utc);
                var future = new DateTime(bareBefore.AddDays(2).Ticks / 10 * 10, DateTimeKind.Utc);
                var bareNames = count == 2 ? new[] { "left-past.bin", "right-past.bin", "all-future.bin" }
                    : new[] { "left-past.bin", "middle-past.bin", "right-past.bin", "all-future.bin" };
                var sideNames = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                for (var side = 0; side < count; side++)
                {
                    Directory.CreateDirectory(bareRoots[side]);
                    foreach (var name in bareNames)
                    {
                        var path = Path.Combine(bareRoots[side], name); File.WriteAllBytes(path, [1]);
                        File.SetLastWriteTimeUtc(path, name == sideNames[side] + "-past.bin" ? past : future);
                    }
                }
                var bareFilter = Filter("clock-bare-date-" + count, "def: exclude\nfe: Now() >= Date\n");
                List<string> bareArgs = ["--folder", bareRoots[0], bareRoots[^1], "--filter", bareFilter, "--show-filtered"];
                if (count == 3) bareArgs.AddRange(["--middle", bareRoots[1]]);
                var bareResult = await run("folder-clock-bare-date-" + count, 0, true, bareArgs.ToArray());
                var bareAfter = DateTime.UtcNow;
                using (var json = JsonDocument.Parse(bareResult.Stdout))
                {
                    var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder clock bare Date all-side dependency " + count, bareResult.Stderr.Length == 0 && bareAfter >= bareBefore && bareAfter < future
                        && entries.Length == bareNames.Length && entries.All(entry =>
                        {
                            var name = entry.GetProperty("path").GetString()!; var filtered = name == "all-future.bin";
                            return entry.GetProperty("filtered").GetBoolean() == filtered && sideNames.All(side =>
                                entry.GetProperty(side).GetProperty("filtered").GetBoolean() == filtered
                                && entry.GetProperty(side).GetProperty("lastWriteTimeUtc").GetDateTime() == (name == side + "-past.bin" ? past : future));
                        }), bareResult.Stdout);
                }
                check("folder clock bare Date input bytes " + count, bareRoots.SelectMany(Directory.GetFiles).All(path => File.ReadAllBytes(path).AsSpan().SequenceEqual(new byte[] { 1 })), "Past/future metadata fixture bytes retained.");
                evidenceRows.Add(new { kind = "bare-date-dependency", sides = count, before = bareBefore, after = bareAfter, past, future, expression = "Now() >= Date", result = bareResult.Name });

                var treeRoots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "clock-rules-" + count, "side-" + side)).ToArray();
                foreach (var root in treeRoots) foreach (var name in new[] { "kept", "blocked" })
                {
                    Directory.CreateDirectory(Path.Combine(root, name)); File.WriteAllBytes(Path.Combine(root, name, "leaf.bin"), [1]);
                }
                foreach (var shared in new[] { false, true })
                {
                    var treeFilter = Filter("clock-tree-" + count + "-" + shared, "def: exclude\n" + (shared
                        ? "e: AllEqual(Array(Now(),Now())) and AllEqual(Array(Today(),Today()))\n"
                        : "de: AllEqual(Array(Now(),Now())) and LeftName != 'blocked'\nfe: Now() >= Today() and AllEqual(Array(Today(),Today()))\n"));
                    List<string> treeArgs = ["--folder", treeRoots[0], treeRoots[^1], "--filter", treeFilter, "--show-filtered"];
                    if (count == 3) treeArgs.AddRange(["--middle", treeRoots[1]]);
                    var result = await run("folder-clock-tree-" + count + "-" + shared, 0, true, treeArgs.ToArray());
                    using var json = JsonDocument.Parse(result.Stdout);
                    var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder clock file directory rule snapshot " + count + " " + shared, result.Stderr.Length == 0 && entries.Length == (shared ? 4 : 3)
                        && entries.Any(entry => entry.GetProperty("path").GetString() == "kept/leaf.bin" && !entry.GetProperty("filtered").GetBoolean())
                        && entries.All(entry => entry.GetProperty("filtered").GetBoolean() == (!shared && entry.GetProperty("path").GetString() == "blocked"))
                        && entries.All(entry => (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name => entry.GetProperty(name).GetProperty("filtered").GetBoolean() == entry.GetProperty("filtered").GetBoolean())), result.Stdout);
                }
                check("folder clock tree input bytes " + count, treeRoots.SelectMany(root => Directory.GetFiles(root, "*", SearchOption.AllDirectories)).All(path => File.ReadAllBytes(path).AsSpan().SequenceEqual(new byte[] { 1 })), "Clock rules retain nested source bytes.");

                // 多数のmtime境界を同じParserで判定し、公開JSONからNowの区間を絞る。値そのものの公開APIは追加しない。
                const int thresholdCount = 2048;
                const int thresholdStepMilliseconds = 100;
                var thresholdRoots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "clock-threshold-" + count, "side-" + side)).ToArray();
                foreach (var root in thresholdRoots)
                {
                    Directory.CreateDirectory(root);
                    for (var index = 0; index < thresholdCount; index++) File.WriteAllBytes(Path.Combine(root, index.ToString("D4", CultureInfo.InvariantCulture) + ".bin"), [1]);
                }
                var beforeProfile = Profile(); var before = DateTime.UtcNow; var timer = Stopwatch.StartNew();
                var lower = new DateTime(before.Ticks / 10 * 10, DateTimeKind.Utc);
                foreach (var root in thresholdRoots) for (var index = 0; index < thresholdCount; index++)
                    File.SetLastWriteTimeUtc(Path.Combine(root, index.ToString("D4", CultureInfo.InvariantCulture) + ".bin"), lower.AddMilliseconds(index * thresholdStepMilliseconds));
                var thresholdFilter = Filter("clock-threshold-" + count, "def: exclude\nfe: Now() >= LeftDate and AllEqual(Array(Now(),Now(),Now()))\n");
                List<string> thresholdArgs = ["--folder", thresholdRoots[0], thresholdRoots[^1], "--filter", thresholdFilter, "--show-filtered"];
                if (count == 3) thresholdArgs.AddRange(["--middle", thresholdRoots[1]]);
                var commandName = "folder-clock-threshold-" + count;
                var commandTimeoutSeconds = CommandTimeoutPolicy.GetSeconds(commandName, thresholdArgs);
                var preparationSeconds = timer.Elapsed.TotalSeconds;
                var rangeSpanSeconds = (thresholdCount - 1) * thresholdStepMilliseconds / 1000d;
                var rangeCoversPreparationAndRun = rangeSpanSeconds >= preparationSeconds + commandTimeoutSeconds;
                var measured = await run(commandName, 0, true, thresholdArgs.ToArray());
                var after = DateTime.UtcNow; timer.Stop(); var afterProfile = Profile();
                var stableClock = after >= before && Math.Abs((after - before).TotalSeconds - timer.Elapsed.TotalSeconds) < 1 && beforeProfile == afterProfile;
                DateTime? inferredLower = null, inferredUpper = null;
                using (var json = JsonDocument.Parse(measured.Stdout))
                {
                    var entries = json.RootElement.GetProperty("entries").EnumerateArray().OrderBy(entry => entry.GetProperty("path").GetString(), StringComparer.Ordinal).ToArray();
                    var accepted = entries.TakeWhile(entry => !entry.GetProperty("filtered").GetBoolean()).Count();
                    check("folder clock threshold all-side snapshot " + count, measured.Stderr.Length == 0 && entries.Length == thresholdCount && entries.Skip(accepted).All(entry => entry.GetProperty("filtered").GetBoolean())
                        && entries.All(entry => (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name => entry.GetProperty(name).GetProperty("filtered").GetBoolean() == entry.GetProperty("filtered").GetBoolean())), "All threshold decisions have one cutoff and identical flags on every side.");
                    if (accepted > 0 && accepted < thresholdCount)
                    {
                        inferredLower = lower.AddMilliseconds((accepted - 1) * thresholdStepMilliseconds);
                        inferredUpper = lower.AddMilliseconds(accepted * thresholdStepMilliseconds);
                    }
                }
                var bounded = inferredLower >= before && inferredUpper <= after;
                var reasons = new List<string>();
                if (!stableClock) reasons.Add("Clock/profile changed across real process");
                if (!rangeCoversPreparationAndRun) reasons.Add("Observed grid does not cover measured preparation plus actual runner timeout");
                if (measured.DurationMilliseconds > commandTimeoutSeconds * 1000L) reasons.Add("Run exceeded its timeout budget");
                if (inferredLower is null || inferredUpper is null) reasons.Add("Both a real accepted and a real rejected threshold are required");
                var boundaryQualified = reasons.Count == 0;
                var detail = $"before={before:O}; inferred=[{inferredLower:O},{inferredUpper:O}); after={after:O}; span={rangeSpanSeconds}; preparation={preparationSeconds}; timeout={commandTimeoutSeconds}";
                if (boundaryQualified)
                {
                    check("folder clock observation span covers preparation and timeout " + count, rangeCoversPreparationAndRun, detail);
                    if (bounded || inferredLower > after || inferredUpper <= before)
                        check("folder clock Now bounded by launch endpoints " + count, bounded, detail);
                    else { boundaryQualified = false; reasons.Add("Observation resolution overlaps an endpoint without proving the strict bound"); }
                }
                if (!boundaryQualified) skip("folder clock Now boundary qualification " + count, string.Join("; ", reasons) + "; " + detail);
                check("folder clock threshold input bytes " + count, thresholdRoots.SelectMany(Directory.GetFiles).All(path => File.ReadAllBytes(path).AsSpan().SequenceEqual(new byte[] { 1 })), "All generated threshold input bytes retained.");
                evidenceRows.Add(new { kind = "now-before-after", sides = count, before, after, beforeProfile, afterProfile, stableClock, inferredLower, inferredUpper, bounded,
                    rangeSpanSeconds, preparationSeconds, commandTimeoutSeconds, rangeCoversPreparationAndRun, boundaryQualified, reasons,
                    monotonicSeconds = timer.Elapsed.TotalSeconds, result = measured.Name, fixedEpochGolden = false, exactCaptureCountQualified = false });

                if (!HasNativeTokyoMidnightProfile())
                {
                    const string reason = "Direct midnight expectations require the captured natural Windows ja-JP/Tokyo fixed-offset profile; other profiles/macOS/DST have no independent native golden";
                    skip("folder clock Today midnight profile " + count, reason);
                    evidenceRows.Add(new { kind = "today-local-midnight", sides = count, qualified = false, reason, nativeOtherProfileQualified = false, nativeTzComparisonQualified = false });
                    continue;
                }
                var dayRoots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "clock-midnight-" + count, "side-" + side)).ToArray();
                var dayProfile = Profile(); var dayBefore = DateTime.UtcNow; var localBefore = TimeZoneInfo.ConvertTimeFromUtc(dayBefore, TimeZoneInfo.Local);
                var localMidnights = Enumerable.Range(-1, 3).Select(delta => DateTime.SpecifyKind(localBefore.Date.AddDays(delta), DateTimeKind.Unspecified)).ToArray();
                if (localMidnights.Any(local => TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local)))
                {
                    const string reason = "At least one local midnight is invalid/ambiguous; no unqualified BCL conversion is attempted";
                    skip("folder clock Today ambiguous midnight " + count, reason);
                    evidenceRows.Add(new { kind = "today-local-midnight", sides = count, qualified = false, reason, nativeOtherProfileQualified = false });
                    continue;
                }
                foreach (var root in dayRoots) Directory.CreateDirectory(root);
                var midnights = new Dictionary<string, DateTime>();
                for (var delta = -1; delta <= 1; delta++)
                {
                    var local = localMidnights[delta + 1];
                    var utc = TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local); var name = "day-" + (delta + 1) + ".bin"; midnights[name] = utc;
                    foreach (var root in dayRoots) { var path = Path.Combine(root, name); File.WriteAllBytes(path, [1]); File.SetLastWriteTimeUtc(path, utc); }
                }
                var midnightFilter = Filter("clock-midnight-" + count, "def: exclude\nfe: Today() == LeftDate and ToDateStr(Now()) == ToDateStr(Today())\n");
                List<string> dayArgs = ["--folder", dayRoots[0], dayRoots[^1], "--filter", midnightFilter, "--show-filtered"];
                if (count == 3) dayArgs.AddRange(["--middle", dayRoots[1]]);
                var dayResult = await run("folder-clock-local-midnight-" + count, 0, true, dayArgs.ToArray());
                var dayAfter = DateTime.UtcNow; var localAfter = TimeZoneInfo.ConvertTimeFromUtc(dayAfter, TimeZoneInfo.Local);
                var qualified = dayAfter >= dayBefore && dayAfter - dayBefore < TimeSpan.FromSeconds(60) && dayProfile == Profile() && HasNativeTokyoMidnightProfile();
                using (var json = JsonDocument.Parse(dayResult.Stdout))
                {
                    var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    var accepted = entries.Where(entry => !entry.GetProperty("filtered").GetBoolean()).ToArray();
                    bool DateWithinEndpoints(JsonElement entry)
                    {
                        var utc = midnights[entry.GetProperty("path").GetString()!];
                        var localDate = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).Date;
                        return localDate >= localBefore.Date && localDate <= localAfter.Date;
                    }
                    if (qualified) check("folder clock Today local midnight and date endpoints " + count, dayResult.Stderr.Length == 0 && entries.Length == 3 && accepted.Length == 1 && DateWithinEndpoints(accepted[0])
                        && entries.All(entry => (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name => entry.GetProperty(name).GetProperty("filtered").GetBoolean() == entry.GetProperty("filtered").GetBoolean())), dayResult.Stdout);
                    else skip("folder clock Today unstable endpoints " + count, "Clock/local profile changed during process; no fixed date golden.");
                    evidenceRows.Add(new { kind = "today-local-midnight", sides = count, before = dayBefore, after = dayAfter, localBefore, localAfter, qualified, midnights,
                        accepted = accepted.Select(entry => entry.GetProperty("path").GetString()).ToArray(), result = dayResult.Name, nativeOtherProfileQualified = false });
                }
                check("folder clock midnight input bytes " + count, dayRoots.SelectMany(Directory.GetFiles).All(path => File.ReadAllBytes(path).AsSpan().SequenceEqual(new byte[] { 1 })), "All local midnight input bytes retained.");
            }
            await File.WriteAllTextAsync(Path.Combine(work, "folder-clock-projections.json"), JsonSerializer.Serialize(new
            {
                evidenceRows, nativeDynamicOracleReexecuted = false, publicClockFixedGolden = false,
                limits = new[] { "Capture call count, pre-lexing ordering, per-Parser separation/e-root sharing, Core no-environment and injected clock/calendar failure propagation are source-review evidence; no public CLI injection API.", "Native fixture proves the captured dynamic relation in its fixed Windows profile; runtime endpoint tests are current-host engineering evidence, not new Native profile/DST qualification." }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        // 実装前失敗行列: 原典108本体=70成功/38例外を二者/三者へ投影し、18静的除外はgoldenなし。
        // 型/epoch/直下array、負epochのWeek例外とMonth/Year成功、arity/一度評価/短絡/共有予算を検査する。
        // 固定Tokyo profileだけを採取済みとし、DST/別profileは既存calendar helperの契約に留める。
        async Task StartOfValues()
        {
            var oracle = Path.GetFullPath("tests/Fixtures/LineTransformTimestampCalendarFunctions");
            var reader = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(oracle, "verify.py") }) reader.ArgumentList.Add(argument);
            using (var process = Process.Start(reader)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var evidence = await stdout + await stderr;
                await File.WriteAllTextAsync(Path.Combine(work, "independent-startof-oracle.json"), evidence);
                check("folder StartOf independent fixed native oracle", process.ExitCode == 0, evidence);
            }
            var archive = Path.Combine(oracle, "startof-actual-capture-closure.v4.zip");
            check("folder StartOf closure SHA", Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive))) == "1965de864574bf4c00da5484ba18e3dba6d459d9ceb1ec04906aa17a64dc8d21", archive);
            using var capture = ZipFile.OpenRead(archive);
            using var rawReader = new StreamReader(capture.GetEntry("proof/native-run-1.stdout.raw")!.Open(), new UTF8Encoding(false, true));
            var allRows = (await rawReader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            var rows = allRows.Where(row => row.GetProperty("bodyDidRun").GetBoolean()).ToArray();
            var excluded = allRows.Where(row => !row.GetProperty("bodyDidRun").GetBoolean()).Select(row => row.GetProperty("id").GetString()!).ToArray();
            check("folder StartOf native projection selection", allRows.Length == 126 && rows.Length == 108 && excluded.Length == 18
                && rows.Count(row => row.GetProperty("status").GetString() == "ok") == 70
                && rows.Count(row => row.GetProperty("status").GetString() == "exception") == 38, "126 raw / 108 public / 70 success / 38 exceptions / 18 native-only exclusions");
            static string Quote(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            static string Literal(JsonElement value) => value.GetProperty("kind").GetString() switch
            {
                "none" => "None",
                "bool" => value.GetProperty("value").GetBoolean() ? "true" : "false",
                "int64" => Int64Literal(value.GetProperty("valueDecimal").GetString()!),
                "double" => value.GetProperty("value").GetDouble().ToString("R", CultureInfo.InvariantCulture) is var number
                    && (number.Contains('.') || number.Contains('E') || number.Contains('e')) ? number : number + ".0",
                "string" => Quote(new UTF8Encoding(false, true).GetString(Convert.FromHexString(value.GetProperty("utf8Hex").GetString()!))),
                "timestamp" => "d'" + DateTime.UnixEpoch.AddTicks(checked(long.Parse(value.GetProperty("epochSignedInt64Decimal").GetString()!, CultureInfo.InvariantCulture) * 10))
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture) + "'",
                "array" => "Array(" + string.Join(',', value.GetProperty("items").EnumerateArray().Select(Literal)) + ")",
                _ => throw new InvalidDataException("Unprojectable native StartOf value")
            };
            bool SameProfile(JsonElement row)
            {
                if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TZ"))) return false;
                var locale = new StringBuilder(85);
                if (ToDateTimeGetUserDefaultLocaleName(locale, 85) == 0) return false;
                var state = ToDateTimeGetTimeZoneInformation(out var zone);
                string Decode(string name) => Encoding.UTF8.GetString(Convert.FromHexString(row.GetProperty(name).GetString()!));
                return state != uint.MaxValue && ToDateTimeGetUserDefaultLCID() == row.GetProperty("userLcid").GetUInt32()
                    && locale.ToString() == Decode("userLocaleUtf8Hex") && state == row.GetProperty("timezoneState").GetUInt32()
                    && zone.StandardName == Decode("timezoneStandardNameUtf8Hex") && zone.Bias == row.GetProperty("timezoneBiasMinutes").GetInt32()
                    && zone.StandardBias == row.GetProperty("timezoneStandardBiasMinutes").GetInt32() && zone.DaylightBias == row.GetProperty("timezoneDaylightBiasMinutes").GetInt32()
                    && -zone.Bias * 60 - (state == 2 ? zone.DaylightBias * 60 : 0) == row.GetProperty("pocoTimezoneTzdSeconds").GetInt32();
            }
            var matching = SameProfile(rows[0]);
            if (!matching) skip("folder StartOf fixed native profile", "108 native bodies require natural Windows ja-JP / LCID1041 / Tokyo / tzd32400; DST and other profiles have no regenerated golden.");
            var projections = new List<object>();
            var serializers = rows.Where(row => row.GetProperty("status").GetString() == "ok" && row.GetProperty("result").GetProperty("kind").GetString() == "timestamp"
                && row.GetProperty("result").GetProperty("serializedUtf8Hex").GetString()!.StartsWith("53797374656d", StringComparison.Ordinal)).ToArray();
            check("folder StartOf negative serializer observations separated", serializers.Length == 14 && serializers.All(row => long.Parse(row.GetProperty("result").GetProperty("epochSignedInt64Decimal").GetString()!, CultureInfo.InvariantCulture) < 0), "14 successful native Timestamp returns with serializer System bytes; not body exceptions or .flt serializer golden");
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "startof-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++) foreach (var name in new[] { "same.bin", "different.bin", "missing.bin" })
                {
                    if (name == "missing.bin" && side == 1) continue;
                    var path = Path.Combine(roots[side], name);
                    File.WriteAllBytes(path, name == "different.bin" ? Enumerable.Repeat((byte)(side + 1), side + 1).ToArray() : [1]);
                    File.SetLastWriteTimeUtc(path, new DateTime(2023, 11, 14, 22, 13, 21, DateTimeKind.Utc));
                }
                var hashes = roots.SelectMany(Directory.GetFiles).ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                var sentinel = Path.Combine(work, "startof-sentinel-" + count); File.WriteAllText(sentinel, "existing output retained");
                var sentinelHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sentinel)));
                var filters = new Dictionary<string, string>();
                async Task<CommandResult> Compare(string id, string expression, Func<string, bool>? included = null, bool rejected = false, bool budget = false)
                {
                    included ??= _ => true;
                    var filter = Filter("startof-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n");
                    filters[filter] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(filter)));
                    List<string> args = ["--folder", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) args.AddRange(["--middle", roots[1]]);
                    var exit = rejected ? 2 : included("different.bin") || included("missing.bin") ? 1 : 0;
                    var result = await run("folder-startof-" + count + "-" + id, exit, !rejected, args.ToArray());
                    check("folder StartOf streams " + count + " " + id, rejected
                        ? result.Stdout.Length == 0 && result.Stderr.Length > 0 && (!budget || result.Stderr.Contains("65,536", StringComparison.Ordinal))
                        : result.Stderr.Length == 0, result.Stderr);
                    if (!rejected && result.ExitCode == exit && result.Stdout.Length != 0)
                    {
                        using var model = JsonDocument.Parse(result.Stdout);
                        var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                        check("folder StartOf values and shared side flags " + count + " " + id, entries.Length == 3 && entries.All(entry =>
                        {
                            var filtered = !included(entry.GetProperty("path").GetString()!);
                            return entry.GetProperty("filtered").GetBoolean() == filtered
                                && (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name =>
                                    !entry.TryGetProperty(name, out var side) || side.ValueKind == JsonValueKind.Null || side.GetProperty("filtered").GetBoolean() == filtered);
                        }), result.Stdout);
                    }
                    return result;
                }
                if (matching) foreach (var row in rows)
                {
                    var id = row.GetProperty("id").GetString()!;
                    var call = row.GetProperty("function").GetString() + "(" + Literal(row.GetProperty("input")) + ")";
                    var rejected = row.GetProperty("status").GetString() == "exception";
                    var result = await Compare("oracle-" + id, rejected ? call : "AllEqual(Array(" + call + "," + Literal(row.GetProperty("result")) + "))", rejected: rejected);
                    projections.Add(new { id, sides = count, nativeStatus = row.GetProperty("status").GetString(), expected = row.GetProperty("result"), result.ExitCode,
                        stdoutEmpty = result.Stdout.Length == 0, stderrEmpty = result.Stderr.Length == 0 });
                    if (!rejected && row.GetProperty("result").GetProperty("kind").GetString() == "timestamp")
                    {
                        var epoch = row.GetProperty("result").GetProperty("epochSignedInt64Decimal").GetString()!;
                        await Compare("timestamp-type-" + id, "not AllEqual(Array(" + call + "," + epoch + "b))");
                        if (long.Parse(epoch, CultureInfo.InvariantCulture) >= 0)
                            await Compare("serializer-" + id, "AllEqual(Array(Substr(" + call + ",0b)," + Quote(Encoding.UTF8.GetString(Convert.FromHexString(row.GetProperty("result").GetProperty("serializedUtf8Hex").GetString()!))) + "))");
                    }
                }
                foreach (var function in new[] { "StartOfWeek", "StartOfMonth", "StartOfYear" })
                {
                    await Compare(function + "-case", "AllEqual(Array(" + function.ToUpperInvariant() + "(LeftDate)," + function + "(LeftDate)))");
                    await Compare(function + "-metadata", "AllEqual(" + function + "(Date)) and Len(" + function + "(Date)) == " + count + "b", path => path != "missing.bin");
                    await Compare(function + "-modified", "AllEqual(Array(" + function + "(Modified)," + function + "(Date)))");
                    await Compare(function + "-missing", "AllEqual(Array(" + function + "(At(Date,1b)),None))", path => path == "missing.bin");
                    await Compare(function + "-nested", "AllEqual(Array(" + function + "(Array(Array(LeftDate),None,false,1b,1.0)),Array(None,None,None,None,None)))");
                    await Compare(function + "-not-predicate", function + "(LeftDate)", _ => false);
                    if (OperatingSystem.IsWindows()) await Compare(function + "-property", "AllEqual(" + function + "(Prop('System.DateModified')))", path => path != "missing.bin");
                    var once = "If(AllEqual(Array(Substr(ToLower('" + new string('A', 7500) + "'),0b,0b),'')),LeftDate,'invalid')";
                    var half = function + "(" + once + ")";
                    await Compare(function + "-argument-once", "AllEqual(Array(" + half + "," + function + "(LeftDate)))");
                    await Compare(function + "-shared-budget", "AllEqual(Array(" + half + "," + half + "))", rejected: true, budget: true);
                    var expensive = function + "('" + new string('A', 4000) + "')";
                    await Compare(function + "-selected-budget", expensive, rejected: true, budget: true);
                    await Compare(function + "-if-lazy", "If(true,true," + expensive + ")");
                    await Compare(function + "-and-lazy", "false and " + expensive, _ => false);
                    await Compare(function + "-or-lazy", "true or " + expensive);
                    await Compare(function + "-empty", function + "('')", rejected: true);
                    await Compare(function + "-middle-failure", function + "(Array(LeftDate,'invalid',LeftDate))", rejected: true);
                    foreach (var (id, expression) in new[] { ("zero", function + "()"), ("two", function + "('a','b')"), ("bare", function), ("empty", function + "(,)"), ("extra-comma", function + "('a',)"), ("missing-close", function + "('a'"), ("unselected-malformed", "If(true,true," + function + "())") })
                        await Compare(function + "-arity-" + id, expression, rejected: true);
                }
                check("folder StartOf input filter and sentinel SHA retained " + count, hashes.Concat(filters).All(pair => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value)
                    && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sentinel))) == sentinelHash, "Input and filter bytes and existing output sentinel unchanged; folder CLI has no output-file option.");
                if (matching) foreach (var function in new[] { "startofweek", "startofmonth", "startofyear" })
                {
                    var row = rows.Single(item => item.GetProperty("id").GetString() == function + "-sunday");
                    var epoch = long.Parse(row.GetProperty("result").GetProperty("epochSignedInt64Decimal").GetString()!, CultureInfo.InvariantCulture);
                    var dependencyRoots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "startof-dependency-" + count + "-" + function, "side-" + side)).ToArray();
                    var offsets = count == 2 ? new[] { 0, 2 } : new[] { 0, 3, 2 };
                    for (var side = 0; side < count; side++)
                    {
                        Directory.CreateDirectory(dependencyRoots[side]);
                        var path = Path.Combine(dependencyRoots[side], "probe.bin"); File.WriteAllBytes(path, [1]);
                        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddTicks(epoch * 10).AddSeconds(offsets[side]));
                    }
                    var inputHashes = dependencyRoots.SelectMany(Directory.GetFiles).ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                    List<string> args = ["--folder", dependencyRoots[0], dependencyRoots[^1], "--filter", Filter("startof-dependency-" + count + "-" + function,
                        "def: exclude\nfe: " + function + "(" + Literal(row.GetProperty("input")) + ") == Date\n"), "--show-filtered"];
                    if (count == 3) args.AddRange(["--middle", dependencyRoots[1]]);
                    var result = await run("folder-startof-metadata-dependency-" + count + "-" + function, 0, true, args.ToArray());
                    if (result.ExitCode == 0)
                    {
                        using var model = JsonDocument.Parse(result.Stdout);
                        var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                        var names = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                        check("folder StartOf all-side metadata dependency " + count + " " + function, result.Stderr.Length == 0 && entries.Length == 1 && names.Select((name, side) =>
                        {
                            var value = entries[0].GetProperty(name);
                            return !value.GetProperty("filtered").GetBoolean() && value.GetProperty("lastWriteTimeUtc").GetDateTime() == DateTime.UnixEpoch.AddTicks(epoch * 10).AddSeconds(offsets[side]);
                        }).All(value => value), result.Stdout);
                    }
                    check("folder StartOf dependency input SHA " + count + " " + function, inputHashes.All(pair => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value), "Dedicated dependency inputs unchanged.");
                }
            }
            check("folder StartOf all public CLI projections counted", projections.Count == (matching ? 216 : 0), "108 native bodies x two/three-way; no static exclusions promoted");
            await File.WriteAllTextAsync(Path.Combine(work, "startof-projections.json"), JsonSerializer.Serialize(new
            {
                measuredBodies = rows.Length, publicCandidates = rows.Length, executedProjections = projections.Count, nativeProfileMatched = matching,
                projections, excluded = excluded.Select(id => new { id, reason = "Native static safety exclusion; no golden and no public native qualification" }),
                serializerObservations = serializers.Select(row => new { id = row.GetProperty("id").GetString(), result = row.GetProperty("result") }),
                limits = new[] { "Timestamp body epoch/type are tested independently of native serializer catch bytes. Existing .flt Literal/System serialization is unchanged.", "Fixed Tokyo native profile only; DST, other profile/OS, native full AST/parser and callback failures remain unqualified." }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        async Task WidthValues()
        {
            var oracle = Path.GetFullPath("tests/Fixtures/FolderWidthOracle");
            var reader = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(oracle, "verify.py") }) reader.ArgumentList.Add(argument);
            if (OperatingSystem.IsWindows()) reader.ArgumentList.Add("--windows-api");
            using (var process = Process.Start(reader)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var evidence = await stdout + await stderr;
                await File.WriteAllTextAsync(Path.Combine(work, "independent-width-oracle.json"), evidence);
                check("folder width independent native oracle", process.ExitCode == 0, evidence);
            }
            using var plan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(oracle, "cases.json")));
            using var registrations = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(oracle, "functions.json")));
            var functions = registrations.RootElement.GetProperty("functions").EnumerateArray().Select(row => row.GetProperty("publicSpelling").GetString()!).ToArray();
            check("folder width fixed registration aliases", functions.Length == 6 && registrations.RootElement.GetProperty("functions").EnumerateArray().All(row => row.GetProperty("aliases").GetArrayLength() == 0), "exactly six names; case variants are tested through the public parser");
            var golden = File.ReadLines(Path.Combine(oracle, "golden.ndjson")).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToDictionary(row => row.GetProperty("id").GetString()!);
            static string Quote(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            static string Literal(JsonElement value, bool expected = false)
            {
                var kind = value.GetProperty("type").GetString();
                if (kind == "none") return "None";
                if (kind == "string") return Quote(expected ? Encoding.UTF8.GetString(Convert.FromHexString(value.GetProperty("hex").GetString()!)) : value.GetProperty("value").GetString()!);
                if (kind == "int") return Int64Literal(value.GetProperty("value").GetString()!);
                if (kind == "double")
                {
                    var number = value.GetProperty("value").GetString()!;
                    return number.Contains('.') || number.Contains('e') || number.Contains('E') ? number : number + ".0";
                }
                if (kind == "bool") return value.GetProperty("value").GetRawText();
                if (kind == "array") return "Array(" + string.Join(',', value.GetProperty(expected ? "items" : "value").EnumerateArray().Select(item => Literal(item, expected))) + ")";
                throw new InvalidOperationException("公開文字種fixtureに未対応の型: " + kind);
            }
            var publicRows = plan.RootElement.EnumerateArray().Where(row => row.GetProperty("projection").GetString() == "public-flt-projectable").ToArray();
            var nativeRows = plan.RootElement.EnumerateArray().Where(row => row.GetProperty("projection").GetString() == "native-only").ToArray();
            check("folder width fixed projection counts", publicRows.Length == 174 && nativeRows.Length == 135, "174 public / 135 native-only");
            var projections = new List<object>();
            foreach (var row in nativeRows) projections.Add(new { id = row.GetProperty("id").GetString(), scope = "native-only", reason = "Fixed fixture classification: raw bytes/NUL, native-only binding/nonfinite double, throw/arity, or invalid Windows flags; not a public callback/API failure injection" });
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "width-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1]);
                    File.WriteAllBytes(Path.Combine(roots[side], "different.bin"), Enumerable.Repeat((byte)(side + 1), side + 1).ToArray());
                    if (side != 1) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [1]);
                }
                var hashes = roots.SelectMany(Directory.GetFiles).ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                async Task Compare(string id, string expression, Func<string, bool>? included = null)
                {
                    included ??= _ => true;
                    List<string> args = ["--directory", roots[0], roots[^1], "--filter", Filter("width-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"), "--show-filtered"];
                    if (count == 3) args.AddRange(["--middle", roots[1]]);
                    var exit = included("different.bin") || included("missing.bin") ? 1 : 0;
                    var result = await run("folder-width-" + count + "-" + id, exit, true, args.ToArray());
                    if (result.ExitCode != exit) return;
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder width " + count + " " + id, entries.Length == 3 && entries.All(entry =>
                    {
                        var filtered = !included(entry.GetProperty("path").GetString()!);
                        return entry.GetProperty("filtered").GetBoolean() == filtered
                            && (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name =>
                                !entry.TryGetProperty(name, out var side) || side.ValueKind == JsonValueKind.Null || side.GetProperty("filtered").GetBoolean() == filtered);
                    }), result.Stdout);
                }
                async Task Reject(string id, string expression, bool budget = false)
                {
                    List<string> args = ["--directory", roots[0], roots[^1], "--filter", Filter("width-reject-" + count + "-" + id, "fe: " + expression + "\n")];
                    if (count == 3) args.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-width-reject-" + count + "-" + id, 2, false, args.ToArray());
                    check("folder width rejected stdout " + count + " " + id, result.Stdout.Length == 0 && (!budget || result.Stderr.Contains("65,536", StringComparison.Ordinal)), result.Stderr);
                }
                if (OperatingSystem.IsWindows())
                {
                    foreach (var row in publicRows)
                    {
                        var id = row.GetProperty("id").GetString()!;
                        var argument = Literal(row.GetProperty("args")[0]);
                        var expected = Literal(golden[id].GetProperty("result"), true);
                        await Compare("oracle-" + id, "AllEqual(Array(" + row.GetProperty("function").GetString() + "(" + argument + ")," + expected + "))");
                        if (count == 2) projections.Add(new { id, scope = "public-cli", sides = "two/three" });
                    }
                }
                else if (count == 2) skip("folder width Windows native golden projections", "This platform uses the existing macOS mapping environment; Windows LCMapStringEx parity is not claimed.");
                foreach (var function in functions)
                {
                    var digit = function == "ToFullWidth" ? "１" : "1";
                    await Compare(function + "-case-name", "AllEqual(Array(" + function.ToUpperInvariant() + "('Ａ')," + function + "('Ａ')))");
                    await Compare(function + "-side-value", function + "(LeftSize) == '" + digit + "'");
                    await Compare(function + "-metadata-shape", "Len(" + function + "(Name)) == " + count + "b");
                    await Compare(function + "-all-side-value", "AllEqual(" + function + "(Size))", path => path == "same.bin");
                    await Compare(function + "-missing-value", "AllEqual(Array(" + function + "(At(Size,1b)),None))", path => path == "missing.bin");
                    await Compare(function + "-folder-value", "Len(" + function + "(Folder)) == Len(Folder)");
                    await Compare(function + "-fullpath-value", "Len(" + function + "(LeftFullPath)) == Len(LeftFullPath)");
                    await Compare(function + "-scalar-not-predicate", function + "('true')", _ => false);
                    if (OperatingSystem.IsWindows())
                    {
                        var doubleCase = publicRows.First(row => row.GetProperty("function").GetString() == function && row.GetProperty("args")[0].GetProperty("type").GetString() == "double" && row.GetProperty("args")[0].GetProperty("value").GetString() == "1.25");
                        await Compare(function + "-direct-double", function + "(1.25) == " + Literal(golden[doubleCase.GetProperty("id").GetString()!].GetProperty("result"), true));
                        await Compare(function + "-property-value", function + "(LeftProp('System.Size')) == '" + digit + "'");
                        await Compare(function + "-property-array", "AllEqual(" + function + "(Prop('System.Size')))", path => path == "same.bin");
                    }
                    var once = "Substr(ToLower('" + new string('A', 7500) + "'),0b,0b)";
                    await Compare(function + "-value-once-budget", "AllEqual(Array(" + function + "(" + once + "),''))");
                    var big = function + "('" + new string('A', 8000) + "')";
                    await Reject(function + "-shared-budget", "AllEqual(Array(" + big + "," + big + "))", true);
                    foreach (var (id, expression) in new[] { ("zero", function + "()"), ("two", function + "('a','b')"), ("empty", function + "(,)"), ("bare", function), ("unknown-alias", function[2..] + "('a')") }) await Reject(function + "-" + id, expression);
                }
                check("folder width input SHA retained " + count, hashes.All(pair => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value), "all input bytes unchanged");
            }
            await File.WriteAllTextAsync(Path.Combine(work, "width-projections.json"), JsonSerializer.Serialize(projections, new JsonSerializerOptions { WriteIndented = true }));
        }

        async Task NormalizeValues()
        {
            var oracle = Path.GetFullPath("tests/Fixtures/FolderNormalizeOracle");
            var reader = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(oracle, "verify.py") }) reader.ArgumentList.Add(argument);
            using (var process = Process.Start(reader)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var evidence = await stdout + await stderr;
                await File.WriteAllTextAsync(Path.Combine(work, "independent-normalize-oracle.json"), evidence);
                check("folder NormalizeUnicode independent native oracle", process.ExitCode == 0, evidence);
            }
            using var plan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(oracle, "cases.json")));
            var golden = File.ReadLines(Path.Combine(oracle, "golden.ndjson")).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .ToDictionary(row => row.GetProperty("id").GetString()!);
            static string Quote(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            static string Literal(JsonElement value, bool expected = false)
            {
                var kind = value.GetProperty("type").GetString();
                if (kind == "none") return "None";
                if (kind == "string") return Quote(expected ? Encoding.UTF8.GetString(Convert.FromHexString(value.GetProperty("hex").GetString()!)) : value.GetProperty("value").GetString()!);
                if (kind == "int") return Int64Literal(value.GetProperty("value").GetString()!);
                if (kind == "double")
                {
                    var number = value.GetProperty("value").GetString()!;
                    return number.Contains('.') || number.Contains('e') || number.Contains('E') ? number : number + ".0";
                }
                if (kind == "bool") return value.GetProperty("value").GetRawText();
                if (kind == "array") return "Array(" + string.Join(',', value.GetProperty(expected ? "items" : "value").EnumerateArray().Select(item => Literal(item, expected))) + ")";
                throw new InvalidOperationException("公開NormalizeUnicode fixtureに未対応の型: " + kind);
            }
            var publicRows = plan.RootElement.EnumerateArray().Where(row => row.GetProperty("projection").GetString() == "public-flt-projectable").ToArray();
            var nativeRows = plan.RootElement.EnumerateArray().Where(row => row.GetProperty("projection").GetString() == "native-only").ToArray();
            check("folder NormalizeUnicode fixed projection counts", publicRows.Length == 95 && nativeRows.Length == 83, "95 public / 83 native-only");
            var projections = new List<object>();
            foreach (var row in nativeRows) projections.Add(new { id = row.GetProperty("id").GetString(), scope = "native-only", reason = "Fixed fixture classification: raw byte/NUL or native-only binding, nonfinite double, injected throw, or constructor arity; not a public CLI execution trace" });
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "normalize-" + count, "side-" + side)).ToArray();
                var missingSide = 1;
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1]);
                    File.WriteAllBytes(Path.Combine(roots[side], "different.bin"), Enumerable.Repeat((byte)(side + 1), side + 1).ToArray());
                    if (side != missingSide) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [1]);
                }
                var hashes = roots.SelectMany(Directory.GetFiles).ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                async Task Compare(string id, string expression, Func<string, bool>? included = null)
                {
                    included ??= _ => true;
                    List<string> args = ["--directory", roots[0], roots[^1], "--filter", Filter("normalize-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"), "--show-filtered"];
                    if (count == 3) args.AddRange(["--middle", roots[1]]);
                    var exit = included("different.bin") || included("missing.bin") ? 1 : 0;
                    var result = await run("folder-normalize-" + count + "-" + id, exit, true, args.ToArray());
                    if (result.ExitCode != exit) return;
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder NormalizeUnicode " + count + " " + id, entries.Length == 3 && entries.All(entry =>
                    {
                        var filtered = !included(entry.GetProperty("path").GetString()!);
                        return entry.GetProperty("filtered").GetBoolean() == filtered
                            && (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name =>
                                !entry.TryGetProperty(name, out var side) || side.ValueKind == JsonValueKind.Null || side.GetProperty("filtered").GetBoolean() == filtered);
                    }), result.Stdout);
                }
                async Task Reject(string id, string expression, bool budget = false)
                {
                    List<string> args = ["--directory", roots[0], roots[^1], "--filter", Filter("normalize-reject-" + count + "-" + id, "fe: " + expression + "\n")];
                    if (count == 3) args.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-normalize-reject-" + count + "-" + id, 2, false, args.ToArray());
                    check("folder NormalizeUnicode rejected stdout " + count + " " + id, result.Stdout.Length == 0 && (!budget || result.Stderr.Contains("65,536", StringComparison.Ordinal)), result.Stderr);
                }
                if (OperatingSystem.IsWindows())
                {
                    foreach (var row in publicRows)
                    {
                        var id = row.GetProperty("id").GetString()!;
                        var args = string.Join(',', row.GetProperty("args").EnumerateArray().Select(argument => Literal(argument)));
                        var expected = Literal(golden[id].GetProperty("result"), true);
                        await Compare("oracle-" + id, "AllEqual(Array(NormalizeUnicode(" + args + ")," + expected + "))");
                        if (count == 2) projections.Add(new { id, scope = "public-cli", sides = "two/three" });
                    }
                }
                else if (count == 2) skip("folder NormalizeUnicode Windows native golden projections", "This platform uses managed normalization; Windows NormalizeString oracle results are not claimed.");
                foreach (var (id, expression) in new[]
                {
                    ("case-name", "AllEqual(Array(NORMALIZEUNICODE('é','nFc'),'é'))"),
                    ("strict-form-kind", "AllEqual(Array(NormalizeUnicode('é',2b),'é')) and AllEqual(Array(NormalizeUnicode('é',2.0),'é')) and AllEqual(Array(NormalizeUnicode('ﬃ',5b),'ffi')) and AllEqual(Array(NormalizeUnicode('ﬃ',5.0),'ﬃ'))"),
                    ("name", "AllEqual(Array(NormalizeUnicode(Name,'NFC'),Name))"),
                    ("side-value", "NormalizeUnicode(LeftSize,'C') == '1'"),
                    ("fullpath-value", "AllEqual(Array(NormalizeUnicode(LeftFullPath,'C'),LeftFullPath))"),
                    ("fullpath-form", "AllEqual(Array(NormalizeUnicode('unused',LeftFullPath),None))"),
                    ("folder-value", "AllEqual(Array(NormalizeUnicode(Folder),Folder))"),
                    ("timestamp", "AllEqual(Array(NormalizeUnicode(d'2024-01-01T00:00:00Z'),Trim(d'2024-01-01T00:00:00Z')))"),
                    ("scalar-not-predicate", "NormalizeUnicode('true')")
                }) await Compare(id, expression, id == "scalar-not-predicate" ? _ => false : null);
                await Compare("all-side-value", "AllEqual(NormalizeUnicode(Size))", path => path == "same.bin");
                await Compare("missing-value", "AllEqual(Array(NormalizeUnicode(At(Size,1b),'KD'),None))", path => path == "missing.bin");
                await Compare("side-form", "AllEqual(Array(NormalizeUnicode('é',MiddleSize),'é'))", path => path != "different.bin");
                await Compare("array-form-default", "AllEqual(Array(NormalizeUnicode('é',Size),'é'))");
                if (OperatingSystem.IsWindows())
                {
                    await Compare("property-value", "AllEqual(NormalizeUnicode(Prop('System.Size')))", path => path == "same.bin");
                    await Compare("property-form", "AllEqual(Array(NormalizeUnicode('é',At(Prop('System.Size'),1b)),'é'))", path => path != "different.bin");
                }
                var expensive = "ToUpper(ToLower(ToUpper('" + new string('A', 6000) + "')))";
                await Compare("invalid-form-skips-value-string", "AllEqual(Array(NormalizeUnicode(" + expensive + ",'invalid'),None))");
                await Compare("invalid-form-skips-value-int", "AllEqual(Array(NormalizeUnicode(" + expensive + ",0b),None))");
                await Reject("valid-form-evaluates-value", "NormalizeUnicode(" + expensive + ",'NFC')", true);
                await Reject("wrong-type-form-evaluates-value", "NormalizeUnicode(" + expensive + ",0.0)", true);
                await Reject("form-budget-propagates", "NormalizeUnicode('unused'," + expensive + ")", true);
                var once = "Substr(ToLower('" + new string('A', 7500) + "'),0b,0b)";
                await Compare("value-once-budget", "AllEqual(Array(NormalizeUnicode(" + once + ",'NFC'),''))");
                await Compare("form-once-budget", "AllEqual(Array(NormalizeUnicode('unused'," + once + "),None))");
                foreach (var (id, expression) in new[] { ("zero", "NormalizeUnicode()"), ("three", "NormalizeUnicode('a','C','D')"), ("bare", "NormalizeUnicode"), ("empty-first", "NormalizeUnicode(,'C')"), ("empty-form", "NormalizeUnicode('a',)"), ("missing-close", "NormalizeUnicode('a','C'") })
                    await Reject(id, expression);
                check("folder NormalizeUnicode input SHA retained " + count, hashes.All(pair => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value), "all input bytes unchanged");
            }
            await File.WriteAllTextAsync(Path.Combine(work, "normalize-projections.json"), JsonSerializer.Serialize(projections, new JsonSerializerOptions { WriteIndented = true }));
        }

        async Task CasingValues()
        {
            // 製品編集前の失敗行列はD task記録へ保存。固定native結果と公開投影を分ける。
            var oracle = Path.GetFullPath("tests/Fixtures/FolderCasingOracle");
            var reader = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(oracle, "verify.py") }) reader.ArgumentList.Add(argument);
            using (var process = Process.Start(reader)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var evidence = await stdout + await stderr;
                await File.WriteAllTextAsync(Path.Combine(work, "independent-casing-oracle.json"), evidence);
                check("folder casing independent native oracle", process.ExitCode == 0, evidence);
            }
            using var plan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(oracle, "cases.json")));
            var golden = File.ReadLines(Path.Combine(oracle, "golden.ndjson")).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .ToDictionary(row => row.GetProperty("id").GetString()!);
            static string Quote(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
            static string? Literal(JsonElement value, bool expected = false)
            {
                var kind = value.GetProperty("type").GetString();
                if (kind == "none") return "None";
                if (kind == "string") return Quote(expected ? Encoding.UTF8.GetString(Convert.FromHexString(value.GetProperty("hex").GetString()!)) : value.GetProperty("value").GetString()!);
                if (kind == "int") return Int64Literal(value.GetProperty("value").GetString()!);
                if (kind == "double") return value.GetProperty("value").GetString();
                if (kind == "bool") return value.GetProperty("value").GetRawText();
                if (kind == "bytes") return value.GetProperty("value").GetString() switch
                {
                    "a9" => "Substr('é',1b,1b)", "c3" => "Substr('é',0b,1b)",
                    "e282" => "Substr('€',0b,2b)", "f09090" => "Substr('𐐀',0b,3b)",
                    "8081" => "Substr('ခ',1b,2b)", _ => null
                };
                if (kind != "array") return null;
                var items = value.GetProperty(expected ? "items" : "value").EnumerateArray().Select(item => Literal(item, expected)).ToArray();
                return items.Any(item => item is null) ? null : "Array(" + string.Join(',', items) + ")";
            }
            var boundaries = new List<object>();
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "casing-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1]);
                    File.WriteAllBytes(Path.Combine(roots[side], "different.bin"), Enumerable.Repeat((byte)(side + 1), side + 1).ToArray());
                }
                var hashes = roots.SelectMany(Directory.GetFiles).ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                async Task Compare(string id, string expression, bool expected = true)
                {
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", Filter("casing-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"), "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-casing-" + count + "-" + id, expected ? 1 : 0, true, arguments.ToArray());
                    if (result.ExitCode != (expected ? 1 : 0)) return;
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder casing " + count + " " + id, entries.Length == 2 && entries.All(entry => entry.GetProperty("filtered").GetBoolean() == !expected), result.Stdout);
                }
                foreach (var row in plan.RootElement.EnumerateArray())
                {
                    var id = row.GetProperty("id").GetString()!; var expected = golden[id];
                    var arguments = row.GetProperty("args").EnumerateArray().Select(argument => Literal(argument)).ToArray();
                    var output = expected.GetProperty("status").GetString() == "ok" ? Literal(expected.GetProperty("result"), true) : null;
                    if (!OperatingSystem.IsWindows() || arguments.Length != 1 || arguments[0] is null || output is null)
                    {
                        if (count == 2) boundaries.Add(new { id, scope = "native-only", reason = !OperatingSystem.IsWindows() ? "Fixed Windows LCMapStringEx locale result; this platform uses managed TextInfo" : "Injected throw/arity, unsupported Regex/FileContent binding, or malformed bytes not constructible by a public valid-Unicode literal/Substr slice" });
                        continue;
                    }
                    await Compare("oracle-" + id, "AllEqual(Array(" + (row.GetProperty("function").GetString() == "lower" ? "ToLower" : "ToUpper") + "(" + arguments[0] + ")," + output + "))");
                    if (count == 2) boundaries.Add(new { id, scope = "public-cli", sides = "two/three" });
                }
                foreach (var function in new[] { "ToLower", "ToUpper" })
                {
                    var lower = function == "ToLower";
                    await Compare(function + "-case-name", "AllEqual(" + function.ToUpperInvariant() + "('aBc'), '" + (lower ? "abc" : "ABC") + "')");
                    await Compare(function + "-typed", "AllEqual(" + function + "(true),'" + (lower ? "true" : "TRUE") + "') and not AllEqual(" + function + "(true),true) and " + function + "(None) == None");
                    var sameNames = "Array(" + string.Join(",", Enumerable.Repeat(lower ? "'same.bin'" : "'SAME.BIN'", count)) + ")";
                    var differentNames = "Array(" + string.Join(",", Enumerable.Repeat(lower ? "'different.bin'" : "'DIFFERENT.BIN'", count)) + ")";
                    await Compare(function + "-name", "AllEqual(Array(" + function + "(Name)," + sameNames + ")) or AllEqual(Array(" + function + "(Name)," + differentNames + "))");
                    await Compare(function + "-side", function + "(LeftSize) == '1' and " + function + "(RightSize) != None");
                    await Compare(function + "-metadata-shape", "Len(" + function + "(Size)) == " + count + "b and Len(" + function + "(Date)) == " + count + "b");
                    await Compare(function + "-timestamp-coercion", "AllEqual(" + function + "(d'2024-01-01T00:00:00Z'), Trim(d'2024-01-01T00:00:00Z'))");
                    await Compare(function + "-fullpath", "AllEqual(" + function + "(LeftFullPath)," + function + "(LeftFullPath))");
                    if (OperatingSystem.IsWindows()) await Compare(function + "-property", function + "(LeftProp('System.Size')) == '1'");
                    foreach (var (id, expression) in new[] { ("zero", function + "()"), ("two", function + "('a','b')"), ("bare", function), ("comma", function + "(,)") })
                    {
                        List<string> args = ["--directory", roots[0], roots[^1], "--filter", Filter("casing-invalid-" + count + function + id, "fe: " + expression + "\n")];
                        if (count == 3) args.AddRange(["--middle", roots[1]]);
                        var rejected = await run("folder-casing-invalid-" + count + function + id, 2, false, args.ToArray());
                        check("folder casing invalid stdout " + count + function + id, rejected.Stdout.Length == 0, rejected.Stderr);
                    }
                    var budget = function + "('" + new string('A', 8000) + "')";
                    var over = await run("folder-casing-budget-" + count + function, 2, false, ["--directory", roots[0], roots[^1], "--filter", Filter("casing-budget-" + count + function, "fe: AllEqual(Array(" + budget + "," + budget + "))\n")]);
                    check("folder casing budget rejects " + count + function, over.Stdout.Length == 0 && over.Stderr.Contains("65,536", StringComparison.Ordinal), over.Stderr);
                }
                check("folder casing input SHA retained " + count, hashes.All(pair => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value), "all input bytes unchanged");
            }
            await File.WriteAllTextAsync(Path.Combine(work, "casing-projections.json"), JsonSerializer.Serialize(boundaries, new JsonSerializerOptions { WriteIndented = true }));
        }

        async Task TrimValues()
        {
            // 想定失敗行列: case-insensitive名／arity 1／不足・余剰・区切り・括弧の拒否、引数の重複評価。
            // ASCII space/tab/CR/LFの左右境界、空／全空白／内部空白、NBSP・全角・VT・FFの誤削除。
            // Noneとscalar文字列型、整数／double／boolean／timestamp変換、直下arrayのshapeとnested非再帰文字列化。
            // 二者／三者のside metadata・property・FullPath依存、入力保持。評価順・例外spyはnative固定fixtureに限る。
            var oracle = Path.GetFullPath("tests/Fixtures/FolderTrimOracle");
            var reader = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(oracle, "verify.py") }) reader.ArgumentList.Add(argument);
            using (var process = Process.Start(reader)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var evidence = await stdout + await stderr;
                await File.WriteAllTextAsync(Path.Combine(work, "independent-trim-oracle.json"), evidence);
                check("folder Trim independent native oracle", process.ExitCode == 0, evidence);
            }
            using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(oracle, "case-plan.json")));
            var golden = File.ReadLines(Path.Combine(oracle, "golden.ndjson")).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .ToDictionary(row => row.GetProperty("id").GetString()!);
            static string? Literal(JsonElement value)
            {
                var kind = value.GetProperty("type").GetString();
                if (kind == "none") return "None";
                var item = value.GetProperty("value");
                if (kind == "string") return "'" + item.GetString()!.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
                    .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
                if (kind is "integer" or "boolean") return item.GetRawText();
                if (kind == "double")
                {
                    var number = item.GetRawText();
                    return number.Contains('.') || number.Contains('e') || number.Contains('E') ? number : number + ".0";
                }
                if (kind != "array") return null; // typed Regex/contentは公開folder式の入力境界外。
                var items = item.EnumerateArray().Select(Literal).ToArray();
                return items.Any(text => text is null) ? null : "Array(" + string.Join(",", items) + ")";
            }
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "trim-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3]);
                    File.WriteAllBytes(Path.Combine(roots[side], "different.bin"), [(byte)(side + 1)]);
                    if (side != count - 1) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [4]);
                }
                var hashes = roots.SelectMany(root => Directory.GetFiles(root)).ToDictionary(path => path,
                    path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                async Task Compare(string id, string expression, Func<string, bool>? included = null)
                {
                    included ??= _ => true;
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter",
                        Filter("trim-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n"), "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var exit = included("different.bin") || included("missing.bin") ? 1 : 0;
                    var result = await run("folder-trim-" + count + "-" + id, exit, true, arguments.ToArray());
                    if (result.ExitCode != exit) return;
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder Trim " + count + " " + id, entries.Length == 3 && entries.All(entry =>
                    {
                        var path = entry.GetProperty("path").GetString()!;
                        var filtered = !included(path);
                        return entry.GetProperty("filtered").GetBoolean() == filtered
                            && (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name =>
                                !entry.TryGetProperty(name, out var side) || side.ValueKind == JsonValueKind.Null
                                || side.GetProperty("filtered").GetBoolean() == filtered);
                    }), result.Stdout);
                }
                if (count == 2)
                {
                    foreach (var row in plan.RootElement.EnumerateArray())
                    {
                        if (row.GetProperty("mode").GetString() != "evaluate") continue;
                        var id = row.GetProperty("id").GetString()!;
                        var expected = golden[id];
                        if (expected.GetProperty("error").GetString() != "") continue;
                        var arguments = row.GetProperty("args").EnumerateArray().Select(argument => Literal(argument.GetProperty("value"))).ToArray();
                        var result = Literal(expected.GetProperty("result"));
                        if (arguments.Length != 1 || arguments[0] is null || result is null) continue;
                        await Compare("oracle-" + id, "AllEqual(Array(" + row.GetProperty("function").GetString() + "(" + arguments[0] + ")," + result + "))");
                    }
                }
                foreach (var function in new[] { "Trim", "TrimLeft", "TrimRight" })
                {
                    var left = function != "TrimRight";
                    var right = function != "TrimLeft";
                    var expected = (left ? "" : " \\t\\r\\n") + "x \\t y" + (right ? "" : "\\n\\r\\t ");
                    await Compare(function + "-boundaries", function + "(' \\t\\r\\nx \\t y\\n\\r\\t ') == '" + expected + "'");
                    await Compare(function + "-empty", function + "('') == '' and " + function + "(' \\t\\r\\n') == ''");
                    foreach (var value in new[] { "\u00a0", "\u3000", "\v", "\f" })
                        await Compare(function + "-preserve-" + ((int)value[0]).ToString(CultureInfo.InvariantCulture),
                            function + "('" + value + "x" + value + "') == '" + value + "x" + value + "'");
                    await Compare(function + "-typed", function + "(None) == None and AllEqual(" + function + "(12), '12') and not AllEqual(" + function + "(12), 12) and "
                        + function + "(-1.5) == '-1.500000' and " + function + "(true) == 'true' and " + function + "(false) == 'false'");
                    await Compare(function + "-timestamp", function + "(d'2024-01-01T00:00:00Z') == '"
                        + DateTime.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "'");
                    await Compare(function + "-array-shape", "AllEqual(Array(" + function + "(Array(None, 12, true, Array(' x ', None))), Array(None, '12', 'true', '[ x , none]'))) and Len(" + function + "(Array())) == 0");
                    await Compare(function + "-metadata", "AllOf(" + function + "(Name) == Name) and " + function + "(RightName) == RightName");
                    await Compare(function + "-context", "AllOf(" + function + "(FullPath) == FullPath) and " + function + "(LeftFullPath) == LeftFullPath");
                    await Compare(function + "-missing", function + "(RightName) == None", path => path == "missing.bin");
                    if (count == 3) await Compare(function + "-middle", function + "(MiddleName) == 'same.bin'", path => path == "same.bin");
                    if (OperatingSystem.IsWindows())
                    {
                        await Compare(function + "-property", "AllOf(" + function + "(Prop('System.Size')) == '3')", path => path == "same.bin");
                        await Compare(function + "-side-property", function + "(LeftProp('System.Size')) == '3'", path => path == "same.bin");
                        if (count == 3) await Compare(function + "-middle-property", function + "(MiddleProp('System.Size')) == '3'", path => path == "same.bin");
                    }
                    else skip("folder Trim property " + function + " " + count, "Shell property is Windows only.");
                    await Compare(function + "-case", function.ToUpperInvariant() + "('x') == 'x'");
                    await Compare(function + "-dynamic-root", function + "('x')", _ => false);
                    if (count != 2) continue;
                    foreach (var (id, expression) in new[]
                    {
                        ("zero", function + "()"), ("two", function + "(1,2)"), ("leading", function + "(,1)"),
                        ("trailing", function + "(1,)"), ("unclosed", function + "(1"), ("bare", function),
                        ("separator", function + "(1 2)")
                    })
                    {
                        var result = await run("folder-trim-invalid-" + id + "-" + function, 2, false,
                            ["--directory", roots[0], roots[^1], "--filter", Filter("trim-invalid-" + id + "-" + function, "fe: " + expression + "\n")]);
                        check("folder Trim invalid stdout " + id + " " + function, string.IsNullOrWhiteSpace(result.Stdout), result.Stdout + result.Stderr);
                    }
                }
                check("folder Trim input hashes " + count, hashes.All(pair => File.Exists(pair.Key)
                    && pair.Value == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key)))) , "All input bytes retained.");
            }
        }

        async Task Len()
        {
            // 想定失敗: UTF-16/graphemeによる誤計数、配列の平坦化、不在側の脱落、合成時の型変換、引数数の見逃し。
            // 公開CLIで表現できない空・入れ子property配列は固定native oracleとGUI自己検証の注入ケースで扱う。
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "len-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                var missingSide = count == 2 ? count - 1 : 1;
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3]);
                    File.WriteAllBytes(Path.Combine(roots[side], "different.bin"), Enumerable.Repeat((byte)(side + 1), side + 2).ToArray());
                    File.WriteAllBytes(Path.Combine(roots[side], "日本😀.txt"), [4, 5]);
                    if (side != missingSide) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [6, 7]);
                }
                async Task Compare(string id, string expression, Func<string, bool> included, bool hasDifference)
                {
                    var filter = Filter("len-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n");
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-len-" + count + "-" + id, hasDifference ? 1 : 0, true, arguments.ToArray());
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder Len " + count + " " + id, entries.Length == 4 && entries.All(entry =>
                    {
                        var path = entry.GetProperty("path").GetString()!;
                        var filtered = !included(path);
                        var present = 0;
                        foreach (var name in count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" })
                        {
                            if (!entry.TryGetProperty(name, out var side) || side.ValueKind == JsonValueKind.Null) continue;
                            present++;
                            if (side.GetProperty("filtered").GetBoolean() != filtered) return false;
                        }
                        return present == (path == "missing.bin" ? count - 1 : count)
                            && entry.GetProperty("filtered").GetBoolean() == filtered;
                    }), result.Stdout);
                }
                foreach (var name in new[] { "Name", "Size", "Exists" })
                    await Compare("array-" + name, "Len(" + name + ") == " + count, _ => true, true);
                await Compare("scalar-name", "Len(LeftName) == 8", path => path == "same.bin", false);
                await Compare("unicode-name", "Len(LeftName) == 7", path => path == "日本😀.txt", false);
                await Compare("missing-scalar", "Len(" + (count == 2 ? "RightName" : "MiddleName") + ") == 0", path => path == "missing.bin", true);
                await Compare("nested-call", "Len(Len(Name)) == 1", _ => true, true);
                await Compare("allof-composition", "AllOf(Len(Name) == " + count + ")", _ => true, true);
                await Compare("allequal-composition", "AllEqual(Len(Name), " + count + ")", _ => true, true);
                await Compare("len-allof", "Len(AllOf(Exists)) == 1", _ => true, true);
                if (OperatingSystem.IsWindows())
                {
                    await Compare("property-array", "Len(Prop('System.Size')) == " + count, _ => true, true);
                    await Compare("property-scalar", "Len(LeftProp('System.Size')) == 1", _ => true, true);
                    await Compare("property-missing", "Len(" + (count == 2 ? "RightProp" : "MiddleProp") + "('System.Size')) == 0", path => path == "missing.bin", true);
                }
                if (count != 2) continue;
                foreach (var (id, expression) in new[]
                {
                    ("none", "Len(None) == 0"),
                    ("booleans", "Len(true) == 1 and Len(false) == 1"),
                    ("numbers", "Len(0) == 1 and Len(-7) == 1 and Len(1.5) == 1"),
                    ("timestamp", "Len(d'2024-01-01T00:00:00Z') == 1"),
                    ("empty-ascii", "Len('') == 0 and Len('abcd') == 4"),
                    ("japanese", "Len('日本語') == 3"),
                    ("emoji", "Len('😀') == 1 and Len('A😀あ') == 3"),
                    ("combining", "Len('e\u0301') == 2 and Len('é') == 1 and Len('か\u3099') == 2"),
                    ("zwj-flag", "Len('👩\u200d💻') == 3 and Len('🇯🇵') == 2"),
                    ("variation", "Len('✈\ufe0f') == 2"),
                    ("controls", "Len('a\\tb') == 3 and Len('a\\nb') == 3"),
                    ("function-case", "lEn('AbC') == 3"),
                    ("len-none-result", "Len(AllOf(1)) == 0"),
                    ("allequal-strict", "AllEqual(Len('x'), 1) and not AllEqual(Len('x'), 1.0)")
                }) await Compare("literal-" + id, expression, _ => true, true);
                foreach (var (id, expression) in new[]
                {
                    ("zero", "Len() == 0"), ("two", "Len(1, 2) == 1"), ("three", "Len(1, 2, 3) == 1"),
                    ("trailing", "Len(1,) == 1"), ("leading", "Len(,1) == 1"),
                    ("unclosed", "Len(1"), ("bare", "Len == 1")
                })
                    await run("folder-len-invalid-" + id, 2, false,
                        ["--directory", roots[0], roots[^1], "--filter", Filter("len-invalid-" + id, "fe: " + expression + "\n")]);
                // Bool以外の結果は拒否せず一致なしとして扱う（原典 FilterExpression::Evaluate の ContainsTrue）。差分があるので終了1。
                var nonBoolean = await run("folder-len-nonboolean-no-match", 1, true,
                    ["--directory", roots[0], roots[^1], "--filter", Filter("len-nonboolean", "fe: Len('x')\n")]);
                check("folder Len nonboolean is no match", nonBoolean.Stderr.Length == 0
                    && JsonDocument.Parse(nonBoolean.Stdout).RootElement.GetProperty("entries").EnumerateArray().All(entry => !entry.GetProperty("filtered").GetBoolean()), nonBoolean.Stdout + nonBoolean.Stderr);
            }
        }

        async Task Strlen()
        {
            // 想定失敗: Lenとの混同、UTF-16/grapheme誤計数、scalar文字列化の相違、None脱落、再帰map、引数数の見逃し。
            // 埋込みNUL・空／入れ子property配列は公開CLIで作れないため、固定native oracleの独立readerで照合する。
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "strlen-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                var missingSide = count == 2 ? count - 1 : 1;
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3]);
                    File.WriteAllBytes(Path.Combine(roots[side], "different.bin"), Enumerable.Repeat((byte)(side + 1), side + 2).ToArray());
                    File.WriteAllBytes(Path.Combine(roots[side], "日本😀.txt"), [4, 5]);
                    if (side != missingSide) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [6, 7]);
                }
                var inputHashes = roots.SelectMany(root => Directory.GetFiles(root)).ToDictionary(path => path,
                    path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                async Task Compare(string id, string expression, Func<string, bool> included, bool hasDifference)
                {
                    var filter = Filter("strlen-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n");
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var expectedExit = hasDifference ? 1 : 0;
                    var result = await run("folder-strlen-" + count + "-" + id, expectedExit, true, arguments.ToArray());
                    if (result.ExitCode != expectedExit) return;
                    JsonDocument model;
                    try { model = JsonDocument.Parse(result.Stdout); }
                    catch (JsonException) { return; } // Run already records invalid JSON beside the process result.
                    using (model)
                    {
                        if (model.RootElement.ValueKind != JsonValueKind.Object
                            || !model.RootElement.TryGetProperty("entries", out var entryRows)
                            || entryRows.ValueKind != JsonValueKind.Array)
                        {
                            check("folder Strlen " + count + " " + id + " JSON shape", false, result.Stdout);
                            return;
                        }
                        var entries = entryRows.EnumerateArray().ToArray();
                        check("folder Strlen " + count + " " + id, entries.Length == 4 && entries.All(entry =>
                        {
                            var path = entry.GetProperty("path").GetString()!;
                            var filtered = !included(path);
                            var present = 0;
                            foreach (var name in count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" })
                            {
                                if (!entry.TryGetProperty(name, out var side) || side.ValueKind == JsonValueKind.Null) continue;
                                present++;
                                if (side.GetProperty("filtered").GetBoolean() != filtered) return false;
                            }
                            return present == (path == "missing.bin" ? count - 1 : count)
                                && entry.GetProperty("filtered").GetBoolean() == filtered;
                        }), result.Stdout);
                    }
                }
                await Compare("scalar-name", "Strlen(LeftName) == 8", path => path == "same.bin", false);
                await Compare("unicode-name", "Strlen(LeftName) == 7", path => path == "日本😀.txt", false);
                var missingName = count == 2 ? "RightName" : "MiddleName";
                await Compare("missing-scalar", "Strlen(" + missingName + ") == None", path => path == "missing.bin", true);
                await Compare("array-name", "AllOf(Strlen(Name) == 8)", path => path == "same.bin", false);
                await Compare("array-unicode", "AllOf(Strlen(Name) == 7)", path => path == "日本😀.txt", false);
                await Compare("array-size", "AllOf(Strlen(Size) == 1)", path => path != "missing.bin", true);
                await Compare("array-exists", "AllOf(Strlen(Exists) == 4)", path => path != "missing.bin", true);
                await Compare("array-missing-slot", "AllEqual(Strlen(Name))", path => path != "missing.bin", true);
                await Compare("len-array-unchanged", "Len(Strlen(Name)) == " + count + " and Len(Name) == " + count, _ => true, true);
                await Compare("nested-call", "AllOf(Strlen(Strlen(Name)) == 1)", path => path == "same.bin" || path == "日本😀.txt", false);
                if (OperatingSystem.IsWindows())
                {
                    await Compare("property-array", "AllOf(Strlen(Prop('System.Size')) == 1)", path => path != "missing.bin", true);
                    await Compare("property-scalar", "Strlen(LeftProp('System.Size')) == 1", _ => true, true);
                    await Compare("property-missing", "Strlen(" + (count == 2 ? "RightProp" : "MiddleProp") + "('System.Size')) == None", path => path == "missing.bin", true);
                }
                if (count == 2)
                {
                    foreach (var (id, expression) in new[]
                    {
                        ("none", "Strlen(None) == None and Strlen(AllOf(1)) == None"),
                        ("booleans", "Strlen(true) == 4 and Strlen(false) == 5"),
                        ("numbers", "Strlen(0) == 1 and Strlen(-12) == 3 and Strlen(1.5) == 8 and Strlen(-1.25) == 9"),
                        ("integer-boundaries", "Strlen(-9223372036854775808) == 20 and Strlen(9223372036854775807) == 19"),
                        ("double-six-decimals", "Strlen(1234567890.125) == 17 and Strlen(1.0) == 8"),
                        ("timestamp", "Strlen(d'2024-01-01T00:00:00Z') == 19"),
                        ("empty-ascii", "Strlen('') == 0 and Strlen('abcd') == 4"),
                        ("japanese", "Strlen('日本語') == 3"),
                        ("emoji", "Strlen('😀') == 1 and Strlen('A😀あ') == 3"),
                        ("combining", "Strlen('e\u0301') == 2 and Strlen('é') == 1 and Strlen('か\u3099') == 2"),
                        ("zwj-flag", "Strlen('👩\u200d💻') == 3 and Strlen('🇯🇵') == 2"),
                        ("variation", "Strlen('✈\ufe0f') == 2"),
                        ("controls", "Strlen('a\\tb') == 3 and Strlen('a\\nb') == 3"),
                        ("function-case", "sTrLeN('AbC') == 3"),
                        ("allequal-strict", "AllEqual(Strlen('x'), 1) and not AllEqual(Strlen('x'), 1.0)"),
                        ("len-scalar-unchanged", "Len(None) == 0 and Len(false) == 1 and Len(1.5) == 1 and Len('😀') == 1")
                    }) await Compare("literal-" + id, expression, _ => true, true);
                    foreach (var (id, expression) in new[]
                    {
                        ("zero", "Strlen() == 0"), ("two", "Strlen(1, 2) == 1"), ("three", "Strlen(1, 2, 3) == 1"),
                        ("trailing", "Strlen(1,) == 1"), ("leading", "Strlen(,1) == 1"),
                        ("unclosed", "Strlen(1"), ("bare", "Strlen == 1")
                    })
                    {
                        var result = await run("folder-strlen-invalid-" + id, 2, false,
                            ["--directory", roots[0], roots[^1], "--filter", Filter("strlen-invalid-" + id, "fe: " + expression + "\n")]);
                        check("folder Strlen invalid stdout empty " + id, string.IsNullOrWhiteSpace(result.Stdout), result.Stdout + result.Stderr);
                    }
                    // Bool以外（scalar・配列）の結果は拒否せず一致なしとして扱う（原典 ContainsTrue）。差分があるので終了1。
                    foreach (var (id, expression) in new[] { ("nonboolean", "Strlen('x')"), ("nonboolean-array", "Strlen(Name)") })
                    {
                        var result = await run("folder-strlen-" + id + "-no-match", 1, true,
                            ["--directory", roots[0], roots[^1], "--filter", Filter("strlen-" + id, "fe: " + expression + "\n")]);
                        check("folder Strlen " + id + " is no match", result.Stderr.Length == 0
                            && JsonDocument.Parse(result.Stdout).RootElement.GetProperty("entries").EnumerateArray().All(entry => !entry.GetProperty("filtered").GetBoolean()), result.Stdout + result.Stderr);
                    }
                }
                check("folder Strlen inputs retained " + count, inputHashes.All(item => File.Exists(item.Key)
                    && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(item.Key))) == item.Value), JsonSerializer.Serialize(inputHashes));
            }
        }

        async Task AllOf()
        {
            bool AllPresentSidesHaveFiltered(JsonElement entry, int expectedSideCount, bool expected)
            {
                var names = expectedSideCount == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                var found = 0;
                foreach (var name in names)
                {
                    if (!entry.TryGetProperty(name, out var side) || side.ValueKind == JsonValueKind.Null) continue;
                    found++;
                    if (side.GetProperty("filtered").GetBoolean() != expected) return false;
                }
                return found == expectedSideCount;
            }
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "all-of-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                const int missingSide = 1;
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3, 4]);
                    File.WriteAllBytes(Path.Combine(roots[side], "different.bin"), Enumerable.Repeat((byte)(side + 1), side + 2).ToArray());
                    File.WriteAllBytes(Path.Combine(roots[side], "zero.bin"), []);
                    File.WriteAllBytes(Path.Combine(roots[side], "partial.bin"), side == count - 1 ? [] : [7, 8]);
                    if (side != missingSide) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [9, 8, 7]);
                }

                async Task<JsonDocument> Compare(string id, string expression, int expectedExit)
                {
                    var filter = Filter("all-of-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n");
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-all-of-" + count + "-" + id, expectedExit, true, arguments.ToArray());
                    return JsonDocument.Parse(result.Stdout);
                }
                JsonElement Entry(JsonDocument model, string path) => model.RootElement.GetProperty("entries").EnumerateArray()
                    .Single(entry => entry.GetProperty("path").GetString() == path);

                using (var model = await Compare("size", "AllOf(Size > 0)", 1))
                {
                    var missing = Entry(model, "missing.bin");
                    check("folder AllOf Size requires every side to be positive " + count,
                        AllPresentSidesHaveFiltered(Entry(model, "same.bin"), count, false)
                            && AllPresentSidesHaveFiltered(Entry(model, "different.bin"), count, false)
                            && AllPresentSidesHaveFiltered(Entry(model, "zero.bin"), count, true)
                            && AllPresentSidesHaveFiltered(Entry(model, "partial.bin"), count, true)
                            && AllPresentSidesHaveFiltered(missing, count - 1, true)
                            && missing.GetProperty(count == 2 ? "right" : "middle").ValueKind == JsonValueKind.Null,
                        model.RootElement.ToString());
                }
                using (var model = await Compare("exists", "AllOf(Exists)", 1))
                {
                    var missing = Entry(model, "missing.bin");
                    check("folder AllOf Exists rejects a missing side " + count,
                        AllPresentSidesHaveFiltered(Entry(model, "same.bin"), count, false)
                            && AllPresentSidesHaveFiltered(Entry(model, "different.bin"), count, false)
                            && AllPresentSidesHaveFiltered(Entry(model, "zero.bin"), count, false)
                            && AllPresentSidesHaveFiltered(Entry(model, "partial.bin"), count, false)
                            && AllPresentSidesHaveFiltered(missing, count - 1, true)
                            && missing.GetProperty(count == 2 ? "right" : "middle").ValueKind == JsonValueKind.Null,
                        model.RootElement.ToString());
                }
                using (var model = await Compare("scalar-true", "aLlOf(true)", 1))
                {
                    var missing = Entry(model, "missing.bin");
                    check("folder AllOf function names are case-insensitive and true scalar stays true " + count,
                        AllPresentSidesHaveFiltered(Entry(model, "same.bin"), count, false)
                            && AllPresentSidesHaveFiltered(Entry(model, "partial.bin"), count, false)
                            && AllPresentSidesHaveFiltered(missing, count - 1, false), model.RootElement.ToString());
                }
                using (var model = await Compare("scalar-false", "AllOf(false)", 0))
                    check("folder AllOf false scalar filters every present side " + count,
                        new[] { "same.bin", "different.bin", "zero.bin", "partial.bin", "missing.bin" }
                            .All(path => AllPresentSidesHaveFiltered(Entry(model, path), path == "missing.bin" ? count - 1 : count, true)),
                        model.RootElement.ToString());
                using (var model = await Compare("scalar-none", "AllOf(1)", 0))
                    check("folder AllOf nonboolean scalar returns no match " + count,
                        AllPresentSidesHaveFiltered(Entry(model, "same.bin"), count, true), model.RootElement.ToString());
                using (var model = await Compare("nonboolean-array", "AllOf(Size)", 0))
                    check("folder AllOf requires boolean array elements " + count,
                        AllPresentSidesHaveFiltered(Entry(model, "same.bin"), count, true), model.RootElement.ToString());
            }
            foreach (var (id, expression) in new[]
            {
                ("zero-args", "AllOf()"), ("multiple-args", "AllOf(true, false)"),
                ("trailing-comma", "AllOf(Size,)"), ("missing-close", "AllOf(Size > 0")
            })
            {
                var filter = Filter("all-of-invalid-" + id, "def: exclude\nfe: " + expression + "\n");
                await run("folder-all-of-invalid-" + id, 2, false, ["--directory", work, work, "--filter", filter]);
            }
        }

        async Task AnyOf()
        {
            // Native oracleのarray-true/false/mixed、scalar、arityに対応する公開CLI経路。
            // nested/emptyの任意Prop配列はHeadlessFolderPropertyChecksの注入contextで照合する。
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "any-of-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3, 4]);
                    File.WriteAllBytes(Path.Combine(roots[side], "different.bin"), Enumerable.Repeat((byte)(side + 1), side + 2).ToArray());
                    File.WriteAllBytes(Path.Combine(roots[side], "zero.bin"), []);
                    File.WriteAllBytes(Path.Combine(roots[side], "partial.bin"), side == count - 1 ? [] : [7, 8]);
                    if (side != 1) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [9, 8, 7]);
                }
                foreach (var (id, expression, included) in new (string, string, string[])[]
                {
                    ("size", "AnyOf(Size > 0)", ["same.bin", "different.bin", "partial.bin", "missing.bin"]),
                    ("exists", "AnyOf(Exists)", ["same.bin", "different.bin", "zero.bin", "partial.bin", "missing.bin"]),
                    ("scalar-true", "aNyOf(true)", ["same.bin", "different.bin", "zero.bin", "partial.bin", "missing.bin"]),
                    ("scalar-false", "AnyOf(false)", []),
                    ("scalar-none", "AnyOf(1) == None", ["same.bin", "different.bin", "zero.bin", "partial.bin", "missing.bin"]),
                    ("scalar-string", "AnyOf('true') == None", ["same.bin", "different.bin", "zero.bin", "partial.bin", "missing.bin"]),
                    ("nonboolean-array", "AnyOf(Size)", []),
                    ("not", "not AnyOf(Size > 0)", ["zero.bin"]),
                    ("allof", "AllOf(AnyOf(Exists))", ["same.bin", "different.bin", "zero.bin", "partial.bin", "missing.bin"]),
                    ("allequal", "AllEqual(AnyOf(Exists), true)", ["same.bin", "different.bin", "zero.bin", "partial.bin", "missing.bin"]),
                    ("len", "Len(AnyOf(Size > 0)) == 1 and AnyOf(Size > 0)", ["same.bin", "different.bin", "partial.bin", "missing.bin"]),
                    ("shell-direct", "AnyOf(Prop('System.Size') > 0)", ["same.bin", "different.bin", "partial.bin", "missing.bin"]),
                    ("shell-nonboolean", "AnyOf(Prop('System.Size'))", [])
                })
                {
                    if (id.StartsWith("shell-", StringComparison.Ordinal) && !OperatingSystem.IsWindows()) continue;
                    var filter = Filter("any-of-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n");
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-any-of-" + count + "-" + id, included.Contains("different.bin") ? 1 : 0, true, arguments.ToArray());
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    var sides = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                    check("folder AnyOf all present side flags " + count + " " + id, entries.Length == 5 && entries.All(entry =>
                    {
                        var path = entry.GetProperty("path").GetString()!;
                        var present = sides.Where(name => entry.GetProperty(name).ValueKind != JsonValueKind.Null).ToArray();
                        return present.Length == (path == "missing.bin" ? count - 1 : count)
                            && present.All(name => entry.GetProperty(name).GetProperty("filtered").GetBoolean() == !included.Contains(path))
                            && (path != "missing.bin" || entry.GetProperty(count == 2 ? "right" : "middle").ValueKind == JsonValueKind.Null);
                    }), model.RootElement.ToString());
                }
            }
            foreach (var (id, expression) in new[]
            {
                ("zero-args", "AnyOf()"), ("two-args", "AnyOf(true, false)"), ("three-args", "AnyOf(true, false, true)"),
                ("trailing-comma", "AnyOf(Size,)"), ("missing-close", "AnyOf(Size > 0")
            })
            {
                var filter = Filter("any-of-invalid-" + id, "def: exclude\nfe: " + expression + "\n");
                await run("folder-any-of-invalid-" + id, 2, false, ["--directory", work, work, "--filter", filter]);
            }
        }

        async Task NotEach()
        {
            // 想定失敗: 全体notとの混同、再帰map、None slotや不在側の脱落、引数数と合成時の型拒否。
            // 任意の空・入れ子配列、順序と評価一回・例外伝播は固定67行とheadless注入contextで別途照合する。
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "not-each-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3, 4]);
                    File.WriteAllBytes(Path.Combine(roots[side], "different.bin"), Enumerable.Repeat((byte)(side + 1), side + 2).ToArray());
                    File.WriteAllBytes(Path.Combine(roots[side], "zero.bin"), []);
                    File.WriteAllBytes(Path.Combine(roots[side], "partial.bin"), side == count - 1 ? [] : [7, 8]);
                    if (side != 1) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [9, 8, 7]);
                }
                string[] all = ["same.bin", "different.bin", "zero.bin", "partial.bin", "missing.bin"];
                foreach (var (id, expression, included) in new (string, string, string[])[]
                {
                    ("exists", "NotEach(Exists)", ["missing.bin"]),
                    ("any-exists", "AnyOf(NotEach(Exists))", ["missing.bin"]),
                    ("size", "NotEach(Size > 0)", ["zero.bin", "partial.bin"]),
                    ("all-size", "AllOf(NotEach(Size > 0))", ["zero.bin"]),
                    ("not-result", "not NotEach(Exists)", ["same.bin", "different.bin", "zero.bin", "partial.bin"]),
                    ("not-argument", "NotEach(not Exists)", all),
                    ("nested", "AnyOf(NotEach(NotEach(Exists)))", all),
                    ("scalar-false", "nOtEaCh(false)", all),
                    ("scalar-true", "NotEach(true)", []),
                    ("scalar-none", "NotEach(1) == None", all),
                    ("scalar-string", "NotEach('true') == None", all),
                    ("nonboolean-array", "NotEach(Size)", []),
                    ("nonboolean-slots", "AllOf(NotEach(Size) == None)", all),
                    ("len", "Len(NotEach(Exists)) == " + count, all),
                    ("shell-size", "NotEach(Prop('System.Size') > 0)", ["zero.bin", "partial.bin"]),
                    ("shell-nonboolean", "NotEach(Prop('System.Size'))", [])
                })
                {
                    if (id.StartsWith("shell-", StringComparison.Ordinal) && !OperatingSystem.IsWindows()) continue;
                    var filter = Filter("not-each-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n");
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-not-each-" + count + "-" + id,
                        included.Any(path => path is "different.bin" or "partial.bin" or "missing.bin") ? 1 : 0, true, arguments.ToArray());
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    var sides = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                    check("folder NotEach all present side flags " + count + " " + id, entries.Length == 5 && entries.All(entry =>
                    {
                        var path = entry.GetProperty("path").GetString()!;
                        var present = sides.Where(name => entry.GetProperty(name).ValueKind != JsonValueKind.Null).ToArray();
                        var filtered = !included.Contains(path);
                        return present.Length == (path == "missing.bin" ? count - 1 : count)
                            && entry.GetProperty("filtered").GetBoolean() == filtered
                            && present.All(name => entry.GetProperty(name).GetProperty("filtered").GetBoolean() == filtered)
                            && (path != "missing.bin" || entry.GetProperty(count == 2 ? "right" : "middle").ValueKind == JsonValueKind.Null);
                    }), model.RootElement.ToString());
                }
            }
            foreach (var (id, expression) in new[]
            {
                ("zero-args", "NotEach()"), ("two-args", "NotEach(true, false)"), ("three-args", "NotEach(true, false, true)"),
                ("trailing-comma", "NotEach(Size,)"), ("missing-close", "NotEach(Size > 0"), ("no-parenthesis", "NotEach")
            })
            {
                var filter = Filter("not-each-invalid-" + id, "def: exclude\nfe: " + expression + "\n");
                await run("folder-not-each-invalid-" + id, 2, false, ["--directory", work, work, "--filter", filter]);
            }
        }

        async Task AtValues()
        {
            // 失敗行列: 0/末尾/負indexは同じslot、範囲外・空・非配列・非Int64 indexはNone。
            // 入れ子/None/型を保持し、rootだけ再帰boolean探索、AnyOf/Logicalは既存の浅い判定を保つ。
            // 両引数の側metadata/Prop/contextを伝播。不正arity/区切り/括弧は終了2・stdout空。
            // 評価順・一度評価・非配列時index省略・例外伝播は独立native readerのspy証拠へ照合する。
            var oracle = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.GetFullPath("tests/Fixtures/FolderAtOracle/verify.py") }) oracle.ArgumentList.Add(argument);
            using (var process = Process.Start(oracle)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var evidence = await stdout + await stderr;
                await File.WriteAllTextAsync(Path.Combine(work, "independent-at-oracle.json"), evidence);
                check("folder At independent native oracle", process.ExitCode == 0, evidence);
            }
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "at-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3]);
                    if (side != count - 1) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [4, 5]);
                }
                var inputs = roots.SelectMany(root => Directory.GetFiles(root)).ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                async Task Compare(string id, string expression, bool missingOnly = false)
                {
                    var filter = Filter("at-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n");
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-at-" + count + "-" + id, 1, true, arguments.ToArray());
                    if (result.ExitCode != 1) return;
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder At " + count + " " + id, entries.Length == 2 && entries.All(entry =>
                    {
                        var filtered = missingOnly && entry.GetProperty("path").GetString() != "missing.bin";
                        return entry.GetProperty("filtered").GetBoolean() == filtered
                            && (count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" }).All(name =>
                                !entry.TryGetProperty(name, out var state) || state.ValueKind == JsonValueKind.Null || state.GetProperty("filtered").GetBoolean() == filtered);
                    }), result.Stdout);
                }
                foreach (var (id, expression) in new[]
                {
                    ("case", "aT(Array(true), 0)"),
                    ("first", "At(Array('a', 'b'), 0) == 'a'"),
                    ("final", "At(Array('a', 'b'), 1) == 'b'"),
                    ("negative-final", "At(Array('a', 'b'), -1) == 'b'"),
                    ("negative-length", "At(Array('a', 'b'), -2) == 'a'"),
                    ("range", "At(Array(true), 1) == None and At(Array(true), -2) == None"),
                    ("extremes", "At(Array(true), -9223372036854775808) == None and At(Array(true), 9223372036854775807) == None"),
                    ("empty", "At(Array(), 0) == None"),
                    ("nonarray", "At(None, 0) == None and At(true, 0) == None and At('x', 0) == None and At(7, 0) == None"),
                    ("index-double", "At(Array(true), 0.0) == None and At(Array(true), 0.5) == None"),
                    ("index-types", "At(Array(true), true) == None and At(Array(true), '0') == None and At(Array(true), None) == None and At(Array(true), Array(0)) == None"),
                    ("nested", "AllEqual(Array(At(Array(Array(1, None, Array(true))), 0), Array(1, None, Array(true))))"),
                    ("none-slot", "At(Array(false, None, true), 1) == None"),
                    ("root-nested", "At(Array(Array(Array(true))), 0)"),
                    ("anyof-shallow", "not AnyOf(At(Array(Array(Array(true))), 0))"),
                    ("logical-shallow", "not (At(Array(Array(Array(true))), 0) and true)"),
                    ("numeric-types", "not AllEqual(Array(At(Array(1), 0), At(Array(1.0), 0)))"),
                    ("metadata", "At(Name, 0) == 'same.bin' or At(Name, 0) == 'missing.bin'"),
                    ("index-metadata", "At(Array(true, false, false, false), Len(Name)) == false"),
                    ("nested-metadata", "AllEqual(Array(At(Array(Name), 0), Name))"),
                    ("array-comparison-context", "At(FullPath, 0) == LeftFullPath"),
                    ("index-comparison-context", "At(Array(true), Len(FullPath)) == None"),
                }) await Compare(id, expression);
                await Compare("absent-side", "At(Exists, -1) == false", true);
                await Compare("absent-name", "At(Name, -1) == None", true);
                if (OperatingSystem.IsWindows())
                {
                    await Compare("property", "At(Prop('System.Size'), 0) == LeftProp('System.Size')");
                    await Compare("property-missing", "At(Prop('System.Size'), -1) == None", true);
                    await Compare("nested-property", "AllEqual(Array(At(Array(Prop('System.Size')), 0), Prop('System.Size')))");
                    await Compare("index-property", "At(Array(false, false, true, true), LeftProp('System.Size'))");
                }
                else skip("folder At Shell Prop " + count, "Windows Shell property system only");
                if (count == 2)
                    foreach (var expression in new[] { "At()", "At(Array())", "At(Array(), 0, 1)", "At(,0)", "At(Array(),)", "At(Array(),,0)", "At(Array() 0)", "At(Array(),0", "At", "At(Array(),0,)" })
                    {
                        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expression)))[..8];
                        var result = await run("folder-at-invalid-" + id, 2, false, ["--directory", roots[0], roots[^1], "--filter", Filter("at-invalid-" + id, "fe: " + expression)]);
                        check("folder At invalid stdout empty " + id, result.Stdout.Length == 0, result.Stdout + result.Stderr);
                    }
                check("folder At input SHA preserved " + count, inputs.All(item => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(item.Key))) == item.Value), JsonSerializer.Serialize(inputs));
            }
        }
        async Task LogicalEachValues()
        {
            // 想定失敗: arity/区切りの誤受理、scalar/配列の誤変換、片側broadcast、短配列の誤長さ・false化、
            // None/非boolean/入れ子の誤判定、空配列のscalar化、側metadata・FullPath contextの欠落、入力変更。
            // 両引数を左から一度ずつ評価する順序と例外伝播は固定native source/oracleの証拠と分ける。
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "logical-each-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3]);
                    if (side != count - 1) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [4, 5]);
                }
                var inputs = roots.SelectMany(root => Directory.GetFiles(root))
                    .ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                var sideNames = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                var allExist = count == 2 ? "Array(true, true)" : "Array(true, true, true)";
                var missingExist = count == 2 ? "Array(true, false)" : "Array(true, true, false)";
                var leftFullPath = count == 2 ? "Array(true, false)" : "Array(true, false, false)";

                async Task Compare(string id, string expression, Func<string, bool> included)
                {
                    var filter = Filter("logical-each-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n");
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var expectedExit = included("missing.bin") ? 1 : 0;
                    var result = await run("folder-logical-each-" + count + "-" + id, expectedExit, true, arguments.ToArray());
                    if (result.ExitCode != expectedExit)
                    {
                        check("folder LogicalEach " + id + " " + count + " sides", false, result.Stdout + result.Stderr);
                        return;
                    }
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    var passed = entries.Length == 2 && entries.All(entry =>
                    {
                        var path = entry.GetProperty("path").GetString()!;
                        var expectedFiltered = !included(path);
                        var found = 0;
                        foreach (var sideName in sideNames)
                        {
                            if (!entry.TryGetProperty(sideName, out var side) || side.ValueKind == JsonValueKind.Null) continue;
                            found++;
                            if (side.GetProperty("filtered").GetBoolean() != expectedFiltered) return false;
                        }
                        return found == count - (path == "missing.bin" ? 1 : 0)
                            && entry.GetProperty("filtered").GetBoolean() == expectedFiltered;
                    });
                    check("folder LogicalEach " + id + " " + count + " sides", passed, model.RootElement.ToString());
                }

                await Compare("scalar-case-and-or",
                    "aNdEaCh(true, false) == false and oReAcH(false, true)", _ => true);
                await Compare("scalar-none",
                    "AndEach(None, true) == None and OrEach(false, 'not-boolean') == None", _ => true);
                await Compare("left-broadcast",
                    "AllEqual(Array(AndEach(Array(true, false, true), true), Array(true, false, true)))", _ => true);
                await Compare("right-broadcast",
                    "AllEqual(Array(AndEach(true, Array(true, false)), Array(true, false)))", _ => true);
                await Compare("unequal-arrays-and-none-slots",
                    "AllEqual(Array(OrEach(Array(false, false, true), Array(true)), Array(true, None, None)))", _ => true);
                await Compare("invalid-and-nested-shallow",
                    "AllEqual(Array(AndEach(Array(true, 1, None), Array(false, true, true)), Array(false, None, None))) and AllEqual(Array(OrEach(Array(Array(true), Array(false), Array(Array(true))), false), Array(true, false, false)))",
                    _ => true);
                await Compare("empty-arrays",
                    "Len(AndEach(Array(), true)) == 0 and Len(OrEach(true, Array())) == 0", _ => true);
                await Compare("side-metadata-first-argument",
                    $"AllEqual(Array(AndEach(Exists, true), {allExist}))", path => path == "same.bin");
                await Compare("side-metadata-second-argument",
                    $"AllEqual(Array(AndEach(true, Exists), {allExist}))", path => path == "same.bin");
                await Compare("missing-side-none",
                    $"AllEqual(Array(AndEach(Exists, true), {missingExist}))", path => path == "missing.bin");
                await Compare("folder-context-first-argument",
                    $"AllEqual(Array(AndEach(FullPath == LeftFullPath, true), {leftFullPath}))", _ => true);
                await Compare("folder-context-second-argument",
                    $"AllEqual(Array(AndEach(true, FullPath == LeftFullPath), {leftFullPath}))", _ => true);

                if (OperatingSystem.IsWindows())
                {
                    var propertyAllPresent = allExist;
                    var propertyMissing = count == 2 ? "Array(true, None)" : "Array(true, true, None)";
                    await Compare("property-first-argument",
                        $"AllEqual(Array(AndEach(Prop('System.Size') > 0, true), {propertyAllPresent}))", path => path == "same.bin");
                    await Compare("property-second-argument",
                        $"AllEqual(Array(AndEach(true, Prop('System.Size') > 0), {propertyAllPresent}))", path => path == "same.bin");
                    await Compare("property-missing-side",
                        $"AllEqual(Array(AndEach(Prop('System.Size') > 0, true), {propertyMissing}))", path => path == "missing.bin");
                }
                else skip("folder LogicalEach Shell Prop " + count, "Windows Shell property system only");

                if (count == 2)
                    foreach (var function in new[] { "AndEach", "OrEach" })
                        foreach (var expression in new[]
                        {
                            function + "()", function + "(true)", function + "(true, false, true)", function + "(, true)",
                            function + "(true,)", function + "(true,,false)", function + "(true false)", function + "(true, false",
                            function, function + "(true, false,)"
                        })
                        {
                            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expression)))[..8];
                            var result = await run("folder-logical-each-invalid-" + id, 2, false,
                                ["--directory", roots[0], roots[^1], "--filter", Filter("logical-each-invalid-" + id, "fe: " + expression)]);
                            check("folder LogicalEach invalid stdout empty " + id, result.Stdout.Length == 0, result.Stdout + result.Stderr);
                        }
                check("folder LogicalEach input SHA preserved " + count,
                    inputs.All(item => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(item.Key))) == item.Value), JsonSerializer.Serialize(inputs));
            }
        }
        async Task ConditionalValues()
        {
            // 想定失敗: Ifのfalse/None分岐・未選択枝評価・型崩れ、IfEachのeager順・scalar条件index 0・
            // 条件配列のslotずれ・枝broadcast/短配列・empty/nested条件、全引数のmetadata/context欠落。
            // native oracleの評価順・例外traceは公開CLIへ注入した検証とは区別する。
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "conditional-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3]);
                    if (side != count - 1) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [4, 5]);
                }
                var inputs = roots.SelectMany(root => Directory.GetFiles(root))
                    .ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                var sideNames = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                var allNames = count == 2 ? "Array('same.bin', 'same.bin')" : "Array('same.bin', 'same.bin', 'same.bin')";
                var missingNames = count == 2 ? "Array('missing.bin', None)" : "Array('missing.bin', 'missing.bin', None)";
                var missingEachNames = count == 2 ? "Array('missing.bin', 'absent')" : "Array('missing.bin', 'missing.bin', 'absent')";
                var allFullPaths = count == 2 ? "Array(LeftFullPath, RightFullPath)" : "Array(LeftFullPath, MiddleFullPath, RightFullPath)";
                var missingFullPaths = count == 2 ? "Array(LeftFullPath, 'absent')" : "Array(LeftFullPath, MiddleFullPath, 'absent')";
                var falseConditions = count == 2 ? "Array(false, false)" : "Array(false, false, false)";

                async Task Compare(string id, string expression, Func<string, bool> included)
                {
                    var filter = Filter("conditional-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n");
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var expectedExit = included("missing.bin") ? 1 : 0;
                    var result = await run("folder-conditional-" + count + "-" + id, expectedExit, true, arguments.ToArray());
                    if (result.ExitCode != expectedExit)
                    {
                        check("folder Conditional " + id + " " + count + " sides", false, result.Stdout + result.Stderr);
                        return;
                    }
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    var passed = entries.Length == 2 && entries.All(entry =>
                    {
                        var path = entry.GetProperty("path").GetString()!;
                        var expectedFiltered = !included(path);
                        var found = 0;
                        foreach (var sideName in sideNames)
                        {
                            if (!entry.TryGetProperty(sideName, out var side) || side.ValueKind == JsonValueKind.Null) continue;
                            found++;
                            if (side.GetProperty("filtered").GetBoolean() != expectedFiltered) return false;
                        }
                        return found == count - (path == "missing.bin" ? 1 : 0)
                            && entry.GetProperty("filtered").GetBoolean() == expectedFiltered;
                    });
                    check("folder Conditional " + id + " " + count + " sides", passed, model.RootElement.ToString());
                }

                await Compare("if-scalar-branches",
                    "iF(true, 'yes', 'no') == 'yes' and If(false, 'yes', 'no') == 'no' and If(None, 'yes', 'no') == 'no' and If(1, 'yes', 'no') == 'no'", _ => true);
                await Compare("if-shallow-condition",
                    "If(Array(false, true), 'yes', 'no') == 'yes' and If(Array(Array(true)), 'no', 'yes') == 'yes'", _ => true);
                await Compare("if-selected-type-nesting",
                    "AllEqual(Array(If(true, Array(1, None, Array(true)), 'fallback'), Array(1, None, Array(true))))", _ => true);
                await Compare("ifeach-scalar-condition-index-zero",
                    "IfEach(true, Array('a', 'b'), Array('x', 'y')) == 'a' and IfEach(false, Array('a', 'b'), Array('x', 'y')) == 'x'", _ => true);
                await Compare("ifeach-array-branch-index",
                    "AllEqual(Array(IfEach(Array(true, false, true), Array('A'), Array('X', 'Y')), Array('A', 'Y', None)))", _ => true);
                await Compare("ifeach-short-false-branch",
                    "AllEqual(Array(IfEach(Array(false, true, false), Array('A', 'B'), Array('X')), Array('X', 'B', None)))", _ => true);
                await Compare("ifeach-scalar-branch-and-none-condition",
                    "AllEqual(Array(IfEach(Array(false, None, true), 'T', Array('x', 'y')), Array('x', 'y', 'T')))", _ => true);
                await Compare("ifeach-nested-shallow-condition",
                    "AllEqual(Array(IfEach(Array(Array(true), Array(false), Array(Array(true))), 'T', 'F'), Array('T', 'F', 'F')))", _ => true);
                await Compare("ifeach-empty-condition",
                    "Len(IfEach(Array(), Array('x'), 'y')) == 0", _ => true);
                await Compare("if-side-metadata",
                    $"AllEqual(Array(If(Exists, Name, 'absent'), {allNames}))", path => path == "same.bin");
                await Compare("if-side-metadata-missing",
                    $"AllEqual(Array(If(Exists, Name, 'absent'), {missingNames}))", path => path == "missing.bin");
                await Compare("ifeach-side-metadata",
                    $"AllEqual(Array(IfEach(Exists, Name, 'absent'), {missingEachNames}))", path => path == "missing.bin");
                await Compare("if-false-side-metadata",
                    "If(false, 'unused', RightName) == None", path => path == "missing.bin");
                await Compare("ifeach-false-side-metadata",
                    "IfEach(false, 'unused', RightName) == None", path => path == "missing.bin");
                await Compare("if-folder-context-condition",
                    "If(FullPath != None, 'full', 'none') == 'full'", _ => true);
                await Compare("ifeach-folder-context-branch",
                    $"AllEqual(Array(IfEach(Exists, FullPath, 'absent'), {allFullPaths}))", path => path == "same.bin");
                await Compare("ifeach-folder-context-missing",
                    $"AllEqual(Array(IfEach(Exists, FullPath, 'absent'), {missingFullPaths}))", path => path == "missing.bin");
                await Compare("if-false-folder-context",
                    "AllOf(If(false, false, FullPath != None))", path => path == "same.bin");
                await Compare("ifeach-false-folder-context",
                    $"AllOf(IfEach({falseConditions}, false, FullPath != None))", path => path == "same.bin");

                if (OperatingSystem.IsWindows())
                {
                    var allSizes = count == 2 ? "Array(3, 3)" : "Array(3, 3, 3)";
                    var ifMissingSizes = count == 2 ? "Array(2, None)" : "Array(2, 2, None)";
                    var ifEachMissingSizes = count == 2 ? "Array(2, 0)" : "Array(2, 2, 0)";
                    await Compare("if-false-property", $"AllEqual(Array(If(false, 0, Prop('System.Size')), {ifMissingSizes}))", path => path == "missing.bin");
                    await Compare("ifeach-false-property", $"AllEqual(Array(IfEach({falseConditions}, 0, Prop('System.Size')), {ifMissingSizes}))", path => path == "missing.bin");
                    await Compare("if-property-branch", $"AllEqual(Array(If(true, Prop('System.Size'), 0), {ifMissingSizes}))", path => path == "missing.bin");
                    await Compare("ifeach-property-branch", $"AllEqual(Array(IfEach(Exists, Prop('System.Size'), 0), {ifEachMissingSizes}))", path => path == "missing.bin");
                    await Compare("if-property-all-present", $"AllEqual(Array(If(true, Prop('System.Size'), 0), {allSizes}))", path => path == "same.bin");
                }
                else skip("folder Conditional Shell Prop " + count, "Windows Shell property system only");

                if (count == 2)
                    foreach (var function in new[] { "If", "IfEach" })
                        foreach (var expression in new[]
                        {
                            function + "()", function + "(true)", function + "(true, false)", function + "(true, false, true, false)",
                            function + "(, true, false)", function + "(true,, false)", function + "(true, false,)",
                            function + "(true, false, true,)", function + "(true false, false, true)", function + "(true, false, true",
                            function
                        })
                        {
                            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expression)))[..8];
                            var result = await run("folder-conditional-invalid-" + id, 2, false,
                                ["--directory", roots[0], roots[^1], "--filter", Filter("conditional-invalid-" + id, "fe: " + expression)]);
                            check("folder Conditional invalid stdout empty " + id, result.Stdout.Length == 0, result.Stdout + result.Stderr);
                        }
                check("folder Conditional input SHA preserved " + count,
                    inputs.All(item => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(item.Key))) == item.Value), JsonSerializer.Serialize(inputs));
            }
        }
        async Task ChoiceValues()
        {
            // 想定失敗: arity/区切り、int64以外の誤受理、clamp、型/入れ子/空配列の破壊、
            // 配列indexの再帰処理や枝のzip/平坦化、全枝の依存情報欠落、入力変更。
            // 深い枝再評価の指数増幅、入れ子間で予算非共有、独立predicate間の予算漏れ、上限時の結果公開。
            // 評価回数・未選択枝の遅延・例外伝播は固定native fixtureの証拠と区別する。
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "choice-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3]);
                    if (side != count - 1) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [4, 5]);
                }
                var inputs = roots.SelectMany(root => Directory.GetFiles(root))
                    .ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                var sideNames = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                var allExist = count == 2 ? "Array(true, true)" : "Array(true, true, true)";
                var missingExist = count == 2 ? "Array(true, false)" : "Array(true, true, false)";
                async Task Compare(string id, string expression, bool missingOnly = false)
                {
                    var filter = Filter("choice-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n");
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-choice-" + count + "-" + id, 1, true, arguments.ToArray());
                    if (result.ExitCode != 1)
                    {
                        check("folder Choice " + count + " " + id, false, result.Stdout + result.Stderr);
                        return;
                    }
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder Choice " + count + " " + id, entries.Length == 2 && entries.All(entry =>
                    {
                        var path = entry.GetProperty("path").GetString();
                        var filtered = missingOnly && path != "missing.bin";
                        var found = 0;
                        foreach (var name in sideNames)
                        {
                            if (!entry.TryGetProperty(name, out var state) || state.ValueKind == JsonValueKind.Null) continue;
                            found++;
                            if (state.GetProperty("filtered").GetBoolean() != filtered) return false;
                        }
                        return found == count - (path == "missing.bin" ? 1 : 0)
                            && entry.GetProperty("filtered").GetBoolean() == filtered;
                    }), result.Stdout);
                }
                foreach (var function in new[] { "Choose", "ChooseEach" })
                {
                    foreach (var (id, expression) in new[]
                    {
                        ("case-minimum", $"{function.ToLowerInvariant()}(0, true)"),
                        ("variadic", $"{function}(2, false, false, true, false)"),
                        ("clamp-negative", $"{function}(-9223372036854775808, true, false)"),
                        ("clamp-final", $"{function}(9223372036854775807, false, true)"),
                        ("double", $"{function}(0.0, true) == None and {function}(0.5, true) == None"),
                        ("invalid-types", $"{function}(true, true) == None and {function}('0', true) == None and {function}(None, true) == None"),
                        ("selected-none", $"{function}(1, true, None) == None"),
                        ("typed", $"AllEqual(Array({function}(1, 1.0, 1), 1)) and not AllEqual(Array({function}(0, 1.0, 1), 1))"),
                        ("string", $"{function}(1, None, 'chosen') == 'chosen'"),
                        ("nested-branch", $"AllEqual(Array({function}(1, false, Array(1, None, Array(true))), Array(1, None, Array(true))))"),
                        ("empty-branch", $"Len({function}(0, Array(), true)) == 0"),
                        ("nested-index", $"{function}(Choose(1, 0, 2), false, false, true)"),
                        ("metadata-choice", $"{function}(1, None, At(Name, 0)) == 'same.bin' or {function}(1, None, At(Name, 0)) == 'missing.bin'"),
                        ("metadata-index", $"{function}(Len(Name), false, false, true)"),
                        ("metadata-unselected", $"{function}(0, true, RightName == None)"),
                        ("fullpath-choice", $"At({function}(1, None, FullPath), 0) == LeftFullPath"),
                        ("fullpath-index", $"{function}(Len(FullPath), false, false, true)"),
                        ("fullpath-unselected", $"{function}(0, true, FullPath)"),
                        ("exists-array-branch", $"AllEqual(Array({function}(1, None, Exists), {allExist})) or AllEqual(Array({function}(1, None, Exists), {missingExist}))"),
                    }) await Compare(function.ToLowerInvariant() + "-" + id, expression);
                    await Compare(function.ToLowerInvariant() + "-missing-side", $"At({function}(1, None, Exists), -1) == false", true);
                    if (OperatingSystem.IsWindows())
                    {
                        await Compare(function.ToLowerInvariant() + "-property-choice", $"At({function}(1, None, Prop('System.Size')), 0) == 3 or At({function}(1, None, Prop('System.Size')), 0) == 2");
                        await Compare(function.ToLowerInvariant() + "-property-index", $"{function}(At(Prop('System.Size'), 0), false, false, true)");
                        await Compare(function.ToLowerInvariant() + "-property-unselected", $"{function}(0, true, Prop('System.Size'))");
                    }
                    else skip("folder Choice Shell Prop " + function + " " + count, "Windows Shell property system only");
                }
                foreach (var (id, expression) in new[]
                {
                    ("choose-array-index", "Choose(Array(0), true) == None"),
                    ("each-mixed", "AllEqual(Array(ChooseEach(Array(-1, 0, 1, 99, None, true, '0', 0.0, Array(0)), 'first', 'last'), Array('first', 'first', 'last', 'last', None, None, None, None, None)))"),
                    ("each-empty-index", "Len(ChooseEach(Array(), true, false)) == 0"),
                    ("each-shape", "AllEqual(Array(ChooseEach(Array(0, 1, 0), Array(1, None), Array(Array(true))), Array(Array(1, None), Array(Array(true)), Array(1, None))))"),
                    ("each-nested-index", "AllEqual(Array(ChooseEach(Array(Array(0), 1, Array(1)), false, true), Array(None, true, None)))"),
                    ("each-invalid-all", "AllEqual(Array(ChooseEach(Array(true, None, 1.0), true), Array(None, None, None)))"),
                    ("each-scalar-array", "AllEqual(Array(ChooseEach(0, Array(true, false)), Array(true, false)))"),
                    ("each-index-nested-choice", "AllEqual(Array(ChooseEach(Choose(0, Array(1, 0)), 'a', 'b'), Array('b', 'a')))"),
                    ("each-repeated-metadata", "AllEqual(Array(ChooseEach(Array(0, 0), Name), Array(Name, Name)))"),
                    ("each-repeated-context", "AllEqual(Array(ChooseEach(Array(0, 0), FullPath), Array(FullPath, FullPath)))"),
                }) await Compare(id, expression);
                string NestedChoice(int depth)
                {
                    var expression = "true";
                    for (var level = 0; level < depth; level++) expression = "ChooseEach(Array(0,0)," + expression + ")";
                    return expression;
                }
                // 4 * (2^14 - 1) = 65,532単位。二つの入力で独立した予算resetも確認する。
                await Compare("each-budget-near-limit", "Len(" + NestedChoice(14) + ") == 2");
                var exactlyAtBudget = "Len(ChooseEach(Array(0,1)," + NestedChoice(14) + ",true)) == 2";
                await Compare("each-budget-exact-limit", exactlyAtBudget);
                var oneOverBudget = "Len(Choose(0,ChooseEach(Array(0,1)," + NestedChoice(14) + ",true),true)) == 2";
                List<string> oneOverArguments = ["--directory", roots[0], roots[^1], "--filter",
                    Filter("choice-budget-one-over-" + count, "fe: " + oneOverBudget)];
                if (count == 3) oneOverArguments.AddRange(["--middle", roots[1]]);
                var oneOverFailure = await run("folder-choice-" + count + "-budget-one-over", 2, false, oneOverArguments.ToArray());
                check("folder Choice one-over budget error " + count,
                    oneOverFailure.ExitCode == 2 && oneOverFailure.Stderr.Contains("評価作業量", StringComparison.Ordinal)
                    && oneOverFailure.Stderr.Contains("65,536", StringComparison.Ordinal), oneOverFailure.Stderr);
                check("folder Choice one-over budget stdout empty " + count,
                    oneOverFailure.Stdout.Length == 0, oneOverFailure.Stdout + oneOverFailure.Stderr);
                List<string> budgetArguments = ["--directory", roots[0], roots[^1], "--filter",
                    Filter("choice-budget-rejected-" + count, "fe: " + NestedChoice(31))];
                if (count == 3) budgetArguments.AddRange(["--middle", roots[1]]);
                var boundedFailure = await run("folder-choice-" + count + "-budget-rejected", 2, false, budgetArguments.ToArray());
                check("folder Choice shared budget error " + count,
                    boundedFailure.ExitCode == 2 && boundedFailure.Stderr.Contains("ChooseEach", StringComparison.Ordinal)
                    && boundedFailure.Stderr.Contains("評価作業量", StringComparison.Ordinal)
                    && boundedFailure.Stderr.Contains("65,536", StringComparison.Ordinal), boundedFailure.Stderr);
                check("folder Choice shared budget stdout empty " + count,
                    boundedFailure.Stdout.Length == 0, boundedFailure.Stdout + boundedFailure.Stderr);
                if (count == 2)
                    foreach (var function in new[] { "Choose", "ChooseEach" })
                        foreach (var suffix in new[] { "()", "(0)", "(,true)", "(0,,true)", "(0,true,)", "(0 true)", "(0,true", "" })
                        {
                            var expression = function + suffix;
                            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expression)))[..8];
                            var result = await run("folder-choice-invalid-" + id, 2, false,
                                ["--directory", roots[0], roots[^1], "--filter", Filter("choice-invalid-" + id, "fe: " + expression)]);
                            check("folder Choice invalid stdout empty " + id, result.Stdout.Length == 0, result.Stdout + result.Stderr);
                        }
                check("folder Choice input SHA preserved " + count,
                    inputs.All(item => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(item.Key))) == item.Value), JsonSerializer.Serialize(inputs));
            }
        }
        async Task ArrayValues()
        {
            // 想定失敗: 空配列、引数の脱落・並べ替え・平坦化、型の混同、側別値や不在側の誤読。
            bool AllPresentSidesHaveFiltered(JsonElement entry, int expectedSideCount, bool expected)
            {
                var names = expectedSideCount == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                var found = 0;
                foreach (var name in names)
                {
                    if (!entry.TryGetProperty(name, out var side) || side.ValueKind == JsonValueKind.Null) continue;
                    found++;
                    if (side.GetProperty("filtered").GetBoolean() != expected) return false;
                }
                return found == expectedSideCount;
            }
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "array-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                var missingSide = count == 2 ? count - 1 : 1;
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3]);
                    File.WriteAllBytes(Path.Combine(roots[side], "different.bin"), Enumerable.Repeat((byte)(side + 1), side + 1).ToArray());
                    if (side != missingSide) File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [4, 5, 6]);
                }
                async Task Compare(string id, string expression, Func<string, bool> included)
                {
                    var filter = Filter("array-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n");
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var expectedExit = included("different.bin") || included("missing.bin") ? 1 : 0;
                    var result = await run("folder-array-" + count + "-" + id, expectedExit, true, arguments.ToArray());
                    using var model = JsonDocument.Parse(result.Stdout);
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    var passed = entries.Length == 3 && entries.All(entry =>
                    {
                        var path = entry.GetProperty("path").GetString()!;
                        var expectedFiltered = !included(path);
                        return entry.GetProperty("filtered").GetBoolean() == expectedFiltered
                            && AllPresentSidesHaveFiltered(entry, count - (path == "missing.bin" ? 1 : 0), expectedFiltered);
                    });
                    check("folder Array " + id + " " + count + " sides", passed, model.RootElement.ToString());
                }
                await Compare("empty", "Len(Array()) == 0", _ => true);
                await Compare("variadic", "Len(Array(false, true, 'x', 1.5)) == 4", _ => true);
                await Compare("nested-equal-order", "AllEqual(Array(Array('a', 'b'), Array('a', 'b')))", _ => true);
                await Compare("nested-different-order", "AllEqual(Array(Array('a', 'b'), Array('b', 'a')))", _ => false);
                await Compare("strict-numeric-kind", "AllEqual(Array(1, 1.0))", _ => false);
                await Compare("root-recursive-bool", "Array(false, Array(true))", _ => true);
                await Compare("root-empty", "Array()", _ => false);
                await Compare("root-no-boolean-true", "Array(false, 1, 'x', None)", _ => false);
                await Compare("anyof-shallow-bool", "AnyOf(Array(Array(true)))", _ => false);
                await Compare("allof-shallow-bool", "AllOf(Array(Array(true)))", _ => false);
                var sideExpression = count == 2 ? "AllEqual(Array(LeftSize, RightSize))" : "AllEqual(Array(LeftSize, MiddleSize, RightSize))";
                await Compare("side-metadata", sideExpression, path => path == "same.bin");
                var manyArguments = string.Join(", ", Enumerable.Range(0, 128));
                await Compare("128-arguments", $"Len(Array({manyArguments})) == 128", _ => true);
            }
            foreach (var (id, expression) in new[] {
                ("empty-first", "Array(,)"), ("empty-final", "Array(true,)"), ("empty-leading", "Array(,true)"),
                ("empty-middle", "Array(true,,false)"), ("unclosed", "Array(true"), ("no-parens", "Array true"), ("trailing", "Array(true))")
            })
            {
                var filter = Filter("array-invalid-" + id, "def: exclude\nfe: " + expression + "\n");
                await run("folder-array-invalid-" + id, 2, false, ["--directory", input, input, "--filter", filter]);
            }
        }
        async Task AllEqual()
        {
            bool AllPresentSidesHaveFiltered(JsonElement entry, int expectedSideCount, bool expected)
            {
                var names = expectedSideCount == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                var found = 0;
                foreach (var name in names)
                {
                    if (!entry.TryGetProperty(name, out var side) || side.ValueKind == JsonValueKind.Null) continue;
                    found++;
                    if (side.GetProperty("filtered").GetBoolean() != expected) return false;
                }
                return found == expectedSideCount;
            }
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "all-equal-" + count, "side-" + side)).ToArray();
                foreach (var root in roots) Directory.CreateDirectory(root);
                var sameTimestamp = DateTime.UnixEpoch.AddTicks(17_000_000_001_234_567);
                for (var side = 0; side < count; side++)
                {
                    File.WriteAllBytes(Path.Combine(roots[side], "same.bin"), [1, 2, 3, 4]);
                    File.SetLastWriteTimeUtc(Path.Combine(roots[side], "same.bin"), sameTimestamp);
                    File.WriteAllBytes(Path.Combine(roots[side], "different.bin"), Enumerable.Repeat((byte)(side + 1), side + 2).ToArray());
                    File.SetLastWriteTimeUtc(Path.Combine(roots[side], "different.bin"), sameTimestamp.AddMinutes(side + 1));
                }
                var missingSide = count == 2 ? count - 1 : 1;
                for (var side = 0; side < count; side++)
                    if (side != missingSide)
                    {
                        File.WriteAllBytes(Path.Combine(roots[side], "missing.bin"), [9, 8, 7]);
                        File.SetLastWriteTimeUtc(Path.Combine(roots[side], "missing.bin"), sameTimestamp);
                    }

                async Task<JsonDocument> Compare(string id, string expression, int expectedExit)
                {
                    var filter = Filter("all-equal-" + count + "-" + id, "def: exclude\nfe: " + expression + "\n");
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-all-equal-" + count + "-" + id, expectedExit, true, arguments.ToArray());
                    return JsonDocument.Parse(result.Stdout);
                }
                JsonElement Entry(JsonDocument model, string path) => model.RootElement.GetProperty("entries").EnumerateArray()
                    .Single(entry => entry.GetProperty("path").GetString() == path);

                using (var model = await Compare("size", "AllEqual(Size)", 0))
                {
                    var missing = Entry(model, "missing.bin");
                    var missingName = count == 2 ? "right" : "middle";
                    check("folder AllEqual Size includes identical sides and rejects mismatch or missing " + count,
                        AllPresentSidesHaveFiltered(Entry(model, "same.bin"), count, false)
                            && AllPresentSidesHaveFiltered(Entry(model, "different.bin"), count, true)
                            && AllPresentSidesHaveFiltered(missing, count - 1, true)
                            && missing.GetProperty(missingName).ValueKind == JsonValueKind.Null,
                        model.RootElement.ToString());
                }
                using (var model = await Compare("date", "AllEqual(Date)", 0))
                {
                    var missing = Entry(model, "missing.bin");
                    check("folder AllEqual Date compares snapshot timestamps " + count,
                        AllPresentSidesHaveFiltered(Entry(model, "same.bin"), count, false)
                            && AllPresentSidesHaveFiltered(Entry(model, "different.bin"), count, true)
                            && AllPresentSidesHaveFiltered(missing, count - 1, true)
                            && missing.GetProperty(count == 2 ? "right" : "middle").ValueKind == JsonValueKind.Null,
                        model.RootElement.ToString());
                }
                if (OperatingSystem.IsWindows())
                {
                    using (var model = await Compare("size-prop", "AllEqual(Prop('System.Size'))", 0))
                    {
                        var missingSize = Entry(model, "missing.bin");
                        check("folder AllEqual property size rejects mismatch or missing " + count,
                            AllPresentSidesHaveFiltered(Entry(model, "same.bin"), count, false)
                                && AllPresentSidesHaveFiltered(Entry(model, "different.bin"), count, true)
                                && AllPresentSidesHaveFiltered(missingSize, count - 1, true)
                                && missingSize.GetProperty(count == 2 ? "right" : "middle").ValueKind == JsonValueKind.Null,
                            model.RootElement.ToString());
                    }
                    using (var model = await Compare("date-prop", "AllEqual(Prop('System.DateModified'))", 0))
                    {
                        var missingDate = Entry(model, "missing.bin");
                        check("folder AllEqual timestamp property compares every side " + count,
                            AllPresentSidesHaveFiltered(Entry(model, "same.bin"), count, false)
                                && AllPresentSidesHaveFiltered(Entry(model, "different.bin"), count, true)
                                && AllPresentSidesHaveFiltered(missingDate, count - 1, true)
                                && missingDate.GetProperty(count == 2 ? "right" : "middle").ValueKind == JsonValueKind.Null,
                            model.RootElement.ToString());
                    }
                    using var hashModel = await Compare("hash-prop", "AllEqual(Prop('Hash.SHA256'))", 0);
                    var missingHash = Entry(hashModel, "missing.bin");
                    check("folder AllEqual property arrays compare every side " + count,
                        AllPresentSidesHaveFiltered(Entry(hashModel, "same.bin"), count, false)
                            && AllPresentSidesHaveFiltered(Entry(hashModel, "different.bin"), count, true)
                            && AllPresentSidesHaveFiltered(missingHash, count - 1, true)
                            && missingHash.GetProperty(count == 2 ? "right" : "middle").ValueKind == JsonValueKind.Null,
                        hashModel.RootElement.ToString());
                }

                using (var model = await Compare("array-first", "AllEqual(Name, 'ignored')", 1))
                    check("folder AllEqual array-first ignores later argument " + count,
                        AllPresentSidesHaveFiltered(Entry(model, "same.bin"), count, false)
                            && AllPresentSidesHaveFiltered(Entry(model, "different.bin"), count, false)
                            && AllPresentSidesHaveFiltered(Entry(model, "missing.bin"), count - 1, true), model.RootElement.ToString());
                using (var model = await Compare("scalar-variadic", "AllEqual(1, 1, 1)", 1))
                    check("folder AllEqual scalar variadic equality " + count, AllPresentSidesHaveFiltered(Entry(model, "different.bin"), count, false), model.RootElement.ToString());
                using (var model = await Compare("timestamp-normalization", "AllEqual(d'2023-11-14T22:13:20.12345699Z', d'2023-11-14T22:13:20.123456Z')", 1))
                    check("folder AllEqual date literals share timestamp precision " + count,
                        AllPresentSidesHaveFiltered(Entry(model, "different.bin"), count, false), model.RootElement.ToString());
                foreach (var (id, expression, expectedFiltered) in new[]
                {
                    ("strict-number-kind", "AllEqual(1, 1.0)", true),
                    ("legacy-size-literal", "AllEqual(1, 1.1b)", false),
                    ("strict-case", "@ci AllEqual('a', 'A')", true),
                    ("scalar-mismatch", "AllEqual(1, 2, 2)", true)
                })
                {
                    using var model = await Compare(id, expression, expectedFiltered ? 0 : 1);
                    check("folder AllEqual strict value comparison " + count + " " + id,
                        AllPresentSidesHaveFiltered(Entry(model, "same.bin"), count, expectedFiltered), model.RootElement.ToString());
                }
            }
            foreach (var (id, expression) in new[]
            {
                ("zero-args", "AllEqual()"), ("trailing-comma", "AllEqual(Size,)"), ("missing-close", "AllEqual(Size")
            })
            {
                var filter = Filter("all-equal-invalid-" + id, "def: exclude\nfe: " + expression + "\n");
                await run("folder-all-equal-invalid-" + id, 2, false, ["--directory", work, work, "--filter", filter]);
            }
        }
        async Task FlagAttributes()
        {
            const FileAttributes rhsa = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;
            string Flags(FileAttributes flags) => string.Concat(new[] { (FileAttributes.ReadOnly, "R"), (FileAttributes.Hidden, "H"), (FileAttributes.System, "S"), (FileAttributes.Archive, "A") }
                .Where(item => (flags & item.Item1) != 0).Select(item => item.Item2));
            foreach (var count in new[] { 2, 3 })
            {
                var roots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "attribute-flags-" + count, "side-" + side)).ToArray();
                var attributes = new FileAttributes[count, 16]; var directories = new FileAttributes[count];
                var before = new List<(string Path, long Ticks, FileAttributes Attributes, string Sha256)>();
                for (var side = 0; side < count; side++)
                {
                    Directory.CreateDirectory(roots[side]);
                    for (var pattern = 0; pattern < 16; pattern++)
                    {
                        var path = Path.Combine(roots[side], "flags-" + pattern + ".bin"); File.WriteAllBytes(path, [(byte)(side + 1)]);
                        var bits = (pattern + side * 5) % 16;
                        var desired = (FileAttributes)((bits & 7) | ((bits & 8) != 0 ? 32 : 0));
                        File.SetAttributes(path, OperatingSystem.IsWindows() ? desired | FileAttributes.Temporary : desired & FileAttributes.ReadOnly);
                        attributes[side, pattern] = File.GetAttributes(path);
                        if (OperatingSystem.IsWindows()) check("folder attributes Win32 RHSA bits " + count + " " + side + " " + pattern,
                            (attributes[side, pattern] & rhsa) == desired && (attributes[side, pattern] & FileAttributes.Temporary) != 0, ((long)attributes[side, pattern]).ToString(CultureInfo.InvariantCulture));
                        before.Add((path, File.GetLastWriteTimeUtc(path).Ticks, attributes[side, pattern], Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))));
                    }
                    var directory = Path.Combine(roots[side], "marker"); Directory.CreateDirectory(directory);
                    if (OperatingSystem.IsWindows()) File.SetAttributes(directory, FileAttributes.Directory | rhsa | FileAttributes.NotContentIndexed);
                    directories[side] = File.GetAttributes(directory);
                    check("folder attributes directory bit " + count + " " + side, (directories[side] & FileAttributes.Directory) != 0, directory);
                }
                string Clauses(int side) => string.Join(" or ", Enumerable.Range(0, 16).Select(pattern => "(Name == 'flags-" + pattern + ".bin' and {0}Attributes == "
                    + (long)attributes[side, pattern] + " and {0}AttrStr == '" + Flags(attributes[side, pattern]) + "')"));
                async Task<JsonDocument> Compare(string id, string fileExpression, string directoryExpression)
                {
                    var filter = Filter("attributes-" + count + "-" + id, "def: exclude\nde: " + directoryExpression + "\nfe: " + fileExpression + "\n");
                    List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter, "--show-filtered"];
                    if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                    var result = await run("folder-attributes-" + count + "-" + id, 1, true, arguments.ToArray()); return JsonDocument.Parse(result.Stdout);
                }
                foreach (var prefix in new[] { "Left", "Middle", "Right" })
                {
                    var side = prefix == "Left" ? 0 : prefix == "Middle" ? 1 : count - 1;
                    using var model = await Compare(prefix, string.Format(CultureInfo.InvariantCulture, Clauses(side), prefix), prefix + "Attributes == " + (long)directories[side] + " and " + prefix + "AttrStr == '" + Flags(directories[side]) + "'");
                    var entries = model.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                    check("folder attributes raw RHSA order unknown-bit exclusion " + count + " " + prefix,
                        entries.Length == 17 && entries.All(entry => !entry.GetProperty("filtered").GetBoolean()), model.RootElement.GetRawText());
                }
                foreach (var (id, expression) in new[] { ("bare", string.Format(CultureInfo.InvariantCulture, Clauses(0), "")), ("contains", "AttrStr contains 'R'") })
                {
                    using var model = await Compare(id, expression, "true");
                    var files = model.RootElement.GetProperty("entries").EnumerateArray().Where(entry => entry.GetProperty("kind").GetString() == "File").ToArray();
                    // 無prefixの属性は全側の配列になり、条件式は全側DIFFITEMで一度だけ評価する（Docs/MIGRATION.md）。
                    // 原典ContainsTrueにより、どこか1側でも条件を満たせば全側が同じく一致になる。
                    check("folder attributes bare side shared " + count + " " + id, files.Length == 16 && files.All(entry =>
                    {
                        var pattern = int.Parse(Path.GetFileNameWithoutExtension(entry.GetProperty("path").GetString()!)[6..], CultureInfo.InvariantCulture);
                        var names = count == 2 ? new[] { "left", "right" } : new[] { "left", "middle", "right" };
                        var anySide = Enumerable.Range(0, count).Any(side =>
                            id == "contains" ? Flags(attributes[side, pattern]).Contains('R') : attributes[side, pattern] == attributes[0, pattern]);
                        return entry.GetProperty("filtered").GetBoolean() == !anySide
                            && names.All(name => entry.GetProperty(name).GetProperty("filtered").GetBoolean() == !anySide);
                    }), model.RootElement.GetRawText());
                }
                var after = before.Select(item => new { path = item.Path, ticks = File.GetLastWriteTimeUtc(item.Path).Ticks, attributes = (long)File.GetAttributes(item.Path), sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(item.Path))) }).ToArray();
                File.WriteAllText(Path.Combine(work, "attributes-input-before-" + count + ".json"), JsonSerializer.Serialize(before.Select(item => new { path = item.Path, ticks = item.Ticks, attributes = (long)item.Attributes, sha256 = item.Sha256 })));
                File.WriteAllText(Path.Combine(work, "attributes-input-after-" + count + ".json"), JsonSerializer.Serialize(after));
                check("folder attributes input bytes flags mtime retained " + count, before.Select((item, index) => item.Ticks == after[index].ticks && (long)item.Attributes == after[index].attributes && item.Sha256 == after[index].sha256).All(value => value), "attributes-input-before/after-" + count + ".json");
            }
        }
        async Task DatePrecision()
        {
            var roots = new[] { Path.Combine(work, "date-precision", "left"), Path.Combine(work, "date-precision", "right") };
            var requested = DateTime.Parse("2024-01-01T00:00:00.1234561Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
            foreach (var root in roots) { Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "one.bin"), [1]); File.SetLastWriteTimeUtc(Path.Combine(root, "one.bin"), requested); }
            var actual = roots.Select(root => File.GetLastWriteTimeUtc(Path.Combine(root, "one.bin"))).ToArray();
            File.WriteAllText(Path.Combine(work, "date-precision-filesystem.json"), JsonSerializer.Serialize(new { requested = requested.ToString("O"), actual = actual.Select(value => value.ToString("O")).ToArray() }));
            var currentOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
            File.WriteAllText(Path.Combine(work, "date-timezone.json"), JsonSerializer.Serialize(new { zone = TimeZoneInfo.Local.Id, currentOffsetTicks = currentOffset.Ticks, currentOffsetMinutes = currentOffset.TotalMinutes, zeroOffsetMeansNoObservableDifference = currentOffset == TimeSpan.Zero }));
            if (actual.Any(value => value.Ticks != requested.Ticks)) { skip("folder Date actual 100ns precision", "FileSystemが要求した100ns刻みのmtimeを保持しないため、Poco sub-microsecond実ファイル比較を省略。"); return; }
            foreach (var field in OperatingSystem.IsWindows() ? new[] { "Date", "LeftDate", "MiddleDate", "RightDate", "Date-prop", "Prop" } : new[] { "Date", "LeftDate", "MiddleDate", "RightDate" })
                foreach (var adjacent in new[] { false, true })
                foreach (var digits in new[] { 7, 8, 18 })
                foreach (var clock in new[] { "00:00:00", "0:00:00", "00:0:00", "00:00:0", "00:0:0", "0:0:0" })
                {
                    var literal = "d'2024-01-01T" + clock + "." + (adjacent ? "123457" : "123456") + new string('9', digits - 6) + "Z'";
                    var expression = field == "Prop" ? "Prop('System.DateModified') == " + literal : (field == "Date-prop" ? "Date" : field) + " == " + literal + (field == "Date-prop" ? " and Prop('System.Size') == 1" : "");
                    var id = field + "-" + adjacent + "-" + digits + "-" + clock.Replace(':', '-');
                    var filter = Filter("date-precision-" + id, "def: exclude\nfe: " + expression + "\n");
                    var result = await run("folder-date-precision-" + id, 0, true, ["--directory", roots[0], roots[1], "--filter", filter]);
                    using var model = JsonDocument.Parse(result.Stdout);
                    check("folder Date Poco microseconds " + id, Paths(model).SequenceEqual(adjacent ? System.Array.Empty<string>() : ["one.bin"]), result.Stdout);
                }
            foreach (var clock in new[] { "00:00:00", "0:00:00", "00:0:00", "00:00:0", "00:0:0", "0:0:0" })
            {
                var id = clock.Replace(':', '-');
                var filter = Filter("date-whole-second-" + id, "def: exclude\nfe: Date >= d'2024-01-01T" + clock + "Z' and Date < d'2024-01-01T00:00:01Z'\n");
                var result = await run("folder-date-whole-second-" + id, 0, true, ["--directory", roots[0], roots[1], "--filter", filter]);
                using var model = JsonDocument.Parse(result.Stdout);
                check("folder Date no-fraction short clock " + clock, Paths(model).SequenceEqual(["one.bin"]), result.Stdout);
            }
            foreach (var field in OperatingSystem.IsWindows() ? new[] { "Date", "LeftDate", "Date-prop" } : new[] { "Date", "LeftDate" })
                foreach (var adjacent in new[] { false, true })
                {
                    var literal = requested.Add(currentOffset).AddTicks(adjacent ? 10 : 0).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
                    var expression = (field == "Date-prop" ? "Date" : field) + " == d'" + literal + "'" + (field == "Date-prop" ? " and Prop('System.Size') == 1" : "");
                    var id = "date-local-" + field + "-" + adjacent;
                    var filter = Filter(id, "def: exclude\nfe: " + expression + "\n");
                    var result = await run("folder-" + id, 0, true, ["--directory", roots[0], roots[1], "--filter", filter]);
                    using var model = JsonDocument.Parse(result.Stdout);
                    check("folder Date current local offset " + field + " adjacent=" + adjacent, Paths(model).SequenceEqual(adjacent ? System.Array.Empty<string>() : ["one.bin"]), result.Stdout);
                }
            foreach (var (id, text) in new[]
            {
                ("positive-offset", "2024-01-01T09:00:00.12345699+09:00"),
                ("negative-offset-year-boundary", "2023-12-31T19:00:00.12345699-05:00"),
                ("comma", "2024-01-01T00:00:00,12345699Z"),
                ("slash-date", "2024/01/01 00:00:00.12345699Z"),
                ("dot-date", "2024.01.01 00:00:00.12345699Z"),
                ("space", "2024-01-01 00:00:00.12345699Z")
            })
            {
                var filter = Filter("date-spelling-" + id, "def: exclude\nfe: Date == d'" + text + "'\n");
                var result = await run("folder-date-spelling-" + id, 0, true, ["--directory", roots[0], roots[1], "--filter", filter]);
                using var model = JsonDocument.Parse(result.Stdout);
                check("folder Date existing calendar zone spelling " + id, Paths(model).SequenceEqual(["one.bin"]), result.Stdout);
            }
            foreach (var (id, expression) in new[]
            {
                ("upper", "d'9999-12-31T23:00:00+01:00' == d'9999-12-31T22:00:00Z'"),
                ("lower", "d'0001-01-01T14:00:00+14:00' == d'0001-01-01T00:00:00Z'")
            })
            {
                var filter = Filter("date-explicit-range-" + id, "def: exclude\nfe: " + expression + "\n");
                var result = await run("folder-date-explicit-range-" + id, 0, true, ["--directory", roots[0], roots[1], "--filter", filter]);
                using var model = JsonDocument.Parse(result.Stdout);
                check("folder Date explicit UTC range " + id, Paths(model).SequenceEqual(["one.bin"]), result.Stdout);
            }
            foreach (var (id, text) in new[]
            {
                ("day", "2024-02-30T00:00:00.12345699Z"),
                ("month-short-clock", "2024-13-01T0:0:0.12345699Z"),
                ("minute-short-clock", "2024-01-01T0:75:0.12345699Z"),
                ("day-whole-second", "2024-02-30T0:0:0Z"),
                ("hour", "2024-01-01T25:00:00.12345699Z"),
                ("second", "2024-01-01T00:00:60.12345699Z"),
                ("offset", "2024-01-01T00:00:00.12345699+14:01"),
                ("suffix", "2024-01-01T00:00:00.12345699oopsZ"),
                ("two-fractions", "2024-01-01T00:00:00.12345699.5Z")
            })
                await run("folder-date-invalid-" + id, 2, false, ["--directory", roots[0], roots[1], "--filter", Filter("date-invalid-" + id, "fe: Date == d'" + text + "'")]);
        }
        async Task FileDateEpoch()
        {
            if (!OperatingSystem.IsWindows()) { skip("folder Date FILETIME pre-epoch Prop equality", "Windows Shell FILETIMEとDirTravelのunsigned変換の一致はWindows限定。非WindowsのPropertySystemは明示拒否。"); return; }
            var roots = Enumerable.Range(0, 3).Select(side => Path.Combine(work, "file-date-epoch", "side-" + side)).ToArray();
            foreach (var root in roots) { Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "one.bin"), [1]); }
            using var evidence = new StreamWriter(Path.Combine(work, "file-date-epoch.ndjson"), false, new UTF8Encoding(false));
            foreach (var (id, delta) in new[] { ("before-second", -10_000_000L), ("before-nine-ticks", -9L), ("before-one-tick", -1L), ("epoch", 0L), ("after-one-tick", 1L), ("after-microsecond", 10L), ("after-second", 10_000_000L), ("modern", 17_040_672_001_234_561L) })
            {
                var requested = DateTime.UnixEpoch.AddTicks(delta);
                foreach (var root in roots) File.SetLastWriteTimeUtc(Path.Combine(root, "one.bin"), requested);
                var actual = roots.Select(root => File.GetLastWriteTimeUtc(Path.Combine(root, "one.bin"))).ToArray();
                var raw = new NativeFileDateOracle.Observation[roots.Length];
                for (var side = 0; side < roots.Length; side++) raw[side] = NativeFileDateOracle.Read(Path.Combine(roots[side], "one.bin"));
                var fileMicroseconds = actual.Select(value => unchecked((long)((unchecked((ulong)value.ToFileTimeUtc()) - 116444736000000000UL) / 10))).ToArray();
                var signedLiteral = (requested.Ticks - requested.Ticks % 10 - DateTime.UnixEpoch.Ticks) / 10;
                evidence.WriteLine(JsonSerializer.Serialize(new { id, requested = requested.ToString("O"), actual = actual.Select(value => value.ToString("O")).ToArray(), nativeFileMicroseconds = fileMicroseconds.Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray(), signedLiteralMicroseconds = signedLiteral.ToString(CultureInfo.InvariantCulture), shell = raw }));
                check("folder Date raw IPropertyStore calls " + id, raw.All(value => value.Open >= 0 && value.Key >= 0 && value.Get >= 0), JsonSerializer.Serialize(raw));
                if (actual.Any(value => value.Ticks != requested.Ticks)) { skip("folder Date FILETIME precision " + id, "FileSystemが要求したmtimeを保持しないためこの境界を省略。"); continue; }
                foreach (var count in new[] { 2, 3 })
                    foreach (var (field, property) in new[] { ("Date", "Prop"), ("modified", "Prop"), ("LeftDate", "LeftProp"), ("MiddleDate", "MiddleProp"), ("RightDate", "RightProp") })
                    {
                        var filter = Filter("file-date-" + id + "-" + count + "-" + field, "def: include\nfe: " + field + " == " + property + "('System.DateModified')\n");
                        List<string> arguments = ["--directory", roots[0], roots[^1], "--filter", filter]; if (count == 3) arguments.AddRange(["--middle", roots[1]]);
                        var result = await run("folder-file-date-" + id + "-" + count + "-" + field, 0, true, arguments.ToArray());
                        using var model = JsonDocument.Parse(result.Stdout);
                        bool Equal(int side) => raw[side].Type == 64 && raw[side].Microseconds == fileMicroseconds[side];
                        var matches = field is "Date" or "modified" ? (count == 2 ? new[] { 0, 2 } : new[] { 0, 1, 2 }).All(Equal)
                            : Equal(field == "LeftDate" ? 0 : field == "MiddleDate" && count == 3 ? 1 : 2);
                        check("folder file Date Shell equality follows native VT " + id + " " + count + " " + field, Paths(model).Length == (matches ? 0 : 1), result.Stdout);
                    }
                foreach (var (field, property) in new[] { ("Date", ""), ("LeftDate", ""), ("", "LeftProp('System.DateModified')") })
                {
                    var expression = (field.Length != 0 ? field : property) + " == d'" + requested.ToString("O", CultureInfo.InvariantCulture) + "'";
                    var filter = Filter("file-date-literal-" + id + "-" + field + property.Length, "def: exclude\nfe: " + expression + "\n");
                    var result = await run("folder-file-date-literal-" + id + "-" + field + property.Length, 0, true, ["--directory", roots[0], roots[^1], "--filter", filter]);
                    using var model = JsonDocument.Parse(result.Stdout);
                    var matches = field.Length != 0 ? fileMicroseconds[0] == signedLiteral : raw[0].Type == 64 && raw[0].Microseconds == signedLiteral;
                    check("folder Date literal signed epoch versus native file or Shell " + id + " " + field + property, Paths(model).Length == (matches ? 1 : 0), result.Stdout);
                }
            }
            var signed = Filter("file-date-signed-literal", "def: exclude\nfe: d'1969-12-31T23:59:59Z' == d'1970-01-01T00:59:59+01:00'\n");
            var signedResult = await run("folder-file-date-signed-literal", 0, true, ["--directory", roots[0], roots[^1], "--filter", signed]);
            using var signedModel = JsonDocument.Parse(signedResult.Stdout);
            check("folder Date literal signed epoch offset unchanged", Paths(signedModel).SequenceEqual(["one.bin"]), signedResult.Stdout);
        }
        static string[] Paths(JsonDocument document) => document.RootElement.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("path").GetString()!).ToArray();
    }
    private static class NativeFileDateOracle
    {
        [StructLayout(LayoutKind.Sequential)] private struct Key { public Guid Id; public uint Pid; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public long Raw; }
        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface Store
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out Key key);
            [PreserveSig] int GetValue(ref Key key, out Variant value);
        }
        internal sealed record Observation(string Path, int Open, int Key, int Get, string KeyId, uint Pid, ushort Type, long Raw, long? Microseconds);
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Variant value);
        [DllImport("propsys.dll", CharSet = CharSet.Unicode)] private static extern int PSGetPropertyKeyFromName(string name, out Key key);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHGetPropertyStoreFromParsingName(string path, nint context, uint flags, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out Store? store);
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        internal static Observation Read(string path)
        {
            var initialized = CoInitializeEx(0, 0); Store? store = null; var value = new Variant();
            try
            {
                var id = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
                var opened = SHGetPropertyStoreFromParsingName(path, 0, 0, ref id, out store);
                var keyed = PSGetPropertyKeyFromName("System.DateModified", out var key);
                var got = store is not null && keyed >= 0 ? store.GetValue(ref key, out value) : -1;
                return new(path, opened, keyed, got, key.Id.ToString(), key.Pid, value.Type, value.Raw,
                    value.Type == 64 ? unchecked((long)((unchecked((ulong)value.Raw) - 116444736000000000UL) / 10)) : null);
            }
            finally { PropVariantClear(ref value); if (store is not null) Marshal.ReleaseComObject(store); if (initialized >= 0) CoUninitialize(); }
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ToDateTimeTimezoneInformation
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] DaylightDate;
        public int DaylightBias;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetUserDefaultLCID")] private static extern uint ToDateTimeGetUserDefaultLCID();
    [DllImport("kernel32.dll", EntryPoint = "GetUserDefaultLocaleName", CharSet = CharSet.Unicode)] private static extern int ToDateTimeGetUserDefaultLocaleName(StringBuilder name, int length);
    [DllImport("kernel32.dll", EntryPoint = "GetTimeZoneInformation", CharSet = CharSet.Unicode)] private static extern uint ToDateTimeGetTimeZoneInformation(out ToDateTimeTimezoneInformation zone);

}
