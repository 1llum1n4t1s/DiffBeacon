using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformTimestampFunctionScenarios
{
    private const string Epoch = "d\"1970-01-01T00:00:00Z\"";
    private const string ManifestSha = "0d0c57cb90449124bf0ac8f42765930cc8b3e6f079255d34de52168bc04e53f5";
    private const string CaptureSha = "10cccbccdb0fc0e47b2c19b2d18307b3c20793d255c7ef47640de4505a31c522";
    private sealed record Rule(string Expression, int Panes);
    private sealed record Inputs(string Left, string Right, byte[] LeftBytes, byte[] RightBytes);

    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Timestamp function fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformTimestampFunctions");
        var work = Path.GetFullPath(Path.Combine(fixtures, "line-transform-timestamp-functions"));
        RequireExternal(work, repository.FullName);
        if (Directory.Exists(work) || File.Exists(work)) throw new IOException("Fresh timestamp function E2E output required.");
        Directory.CreateDirectory(work);
        var manifest = await File.ReadAllBytesAsync(Path.Combine(reference, "fixed-manifest.json"));
        if (Hex(manifest) != ManifestSha) throw new IOException("Timestamp function manifest differs.");
        var capture = await File.ReadAllBytesAsync(Path.Combine(reference, "native-functions-capture-closure.v9.zip"));
        if (capture.Length != 1_008_350 || Hex(capture) != CaptureSha) throw new IOException("Timestamp function capture differs.");
        using var archive = new ZipArchive(new MemoryStream(capture), ZipArchiveMode.Read);
        if (archive.Entries.Count != 52) throw new IOException("Timestamp function closure entry count differs.");
        using var golden = JsonDocument.Parse(Read("native-golden-candidate.v9.json"));
        var raw = new UTF8Encoding(false, true).GetString(Read("proof/native-run-1.stdout.raw"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line))
            .ToDictionary(doc => doc.RootElement.GetProperty("id").GetString()!, doc => doc);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var sources = new Dictionary<string, byte[]>(); var evidence = new List<object>(); var selection = new List<object>();
        var matching = SameProfile(raw.Values.First().RootElement);
        var measured = 0; var nativeSkipped = 0; var ordinal = 0;
        try
        {
            foreach (var entry in golden.RootElement.GetProperty("entries").EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString()!;
                var row = raw[id].RootElement;
                if (!row.GetProperty("bodyDidRun").GetBoolean()) throw new IOException("Unmeasured body must not become a golden.");
                if (id == "todatetime-no-args" || !matching)
                {
                    nativeSkipped++;
                    selection.Add(new { id, executed = false, reason = id == "todatetime-no-args"
                        ? "Measured zero-argument body is not public arity1" : "Natural Windows profile differs; Native bytes not regenerated" });
                    continue;
                }
                var name = "native-" + (++ordinal).ToString("D3", CultureInfo.InvariantCulture);
                var function = row.GetProperty("function").GetString()!;
                var call = function + "(" + InputExpression(row.GetProperty("input")) + ")";
                var provenance = new { classification = "same-profile-native-body-public-projection", nativeId = id,
                    nativeParserAstOptimizeQualified = false, binding = entry.GetProperty("rawReferences").Clone(), nativeRecord = row.Clone() };
                bool failure = row.GetProperty("status").GetString() == "exception";
                var (expression, expected) = failure ? (call, "") : Projection(call, row.GetProperty("result"));
                // 全110本体をTextで照合し、形式差の確認は代表ケースへ絞る。
                bool table = row.GetProperty("input").GetProperty("kind").GetString() == "array"
                    || id is "todatetime-string-date-dash" or "todatestr-string-date-dash"
                        or "todatetime-raw-epoch-zero" or "todatestr-raw-epoch-zero" or "todatestr-unrecognized";
                var source = "physical timestamp function probe";
                await Exercise(name, "@noopt " + expression, source, expected, failure, table, provenance, row);
                // 公開normal/@noopt/dynamicLineの接続を有限のアンカーで照合する。
                if (id is "todatetime-string-fraction-6-one" or "todatestr-string-fraction-6-one"
                    or "todatetime-string-date-dash" or "todatestr-string-date-dash"
                    or "todatetime-unrecognized" or "todatestr-unrecognized")
                {
                    await Exercise(name + "-normal", expression, source, expected, failure, table, provenance, row);
                    var dynamicCall = function + "(Line)";
                    var dynamicExpression = failure ? dynamicCall : Projection(dynamicCall, row.GetProperty("result")).Expression;
                    var text = Decode(row.GetProperty("input").GetProperty("utf8Hex").GetString()!);
                    await Exercise(name + "-line", dynamicExpression, text, expected, failure, table, provenance, row);
                    await Exercise(name + "-line-noopt", "@noopt " + dynamicExpression, text, expected, failure, false, provenance, row);
                }
                // serializerのcatch文字列はToDateStr本体の例外と別契約。
                if (id is "todatetime-string-year-0000" or "todatetime-string-fraction-6-one")
                {
                    var value = row.GetProperty("result");
                    if (value.GetProperty("serializerDidRun").GetBoolean())
                        await Exercise(name + "-serializer", "@noopt " + call, source,
                            Decode(value.GetProperty("serializedUtf8Hex").GetString()!), false, false, provenance, row);
                }
                measured++; selection.Add(new { id, executed = true, publicProjection = expression, nativeParserAstOptimizeQualified = false });
            }
            check("timestamp-functions-native-counts", matching ? measured == 110 && nativeSkipped == 1 : measured == 0 && nativeSkipped == 111,
                "Only same-profile measured bodies; zero-arity body is separate from public grammar.");
            foreach (var function in new[] { "ToDateTime", "ToDateStr" })
            {
                var engineering = new { classification = "source-derived-public-engineering", nativeParserAstOptimizeQualified = false };
                foreach (var (suffix, expression) in new[] { ("arity0", function + "()"), ("arity2", function + "(none,none)"),
                    ("literal-nul", function + "(\"a\0b\")") })
                    await Exercise("engineering-" + function + "-" + suffix, expression, "unchanged input", "", true, true, engineering);
                await Exercise("engineering-" + function + "-unsupported", "@noopt " + function + "(Array(none,true,7,1.25,Array(\"x\")))",
                    "unchanged input", "[none, none, none, none, none]", false, true, engineering);
                // 二つの動的配列が全行で累積1,048,576slotsを共有。日付解析やOSへ依存しない。
                var array = "Array(" + string.Join(',', Enumerable.Repeat("Len(Line)", 128)) + ")";
                await Exercise("engineering-" + function + "-shared-budget", "@noopt " + function + "(" + array + ")",
                    "keep", "", true, false, engineering, lineCount: 4097, sharedBudget: true);
                if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
                    await Exercise("engineering-" + function + "-missing-host", "@noopt " + function + "(Line)",
                        "2000-01-02", "", true, false, engineering);
            }
            foreach (var source in sources)
                check("timestamp-functions-input-" + Path.GetFileName(source.Key), File.ReadAllBytes(source.Key).SequenceEqual(source.Value), "Source and project bytes retained.");
            await Write(Path.Combine(work, "selection.json"), JsonSerializer.SerializeToUtf8Bytes(new { matchingNativeProfile = matching, measured,
                nativeSkipped, excludedNoGolden = golden.RootElement.GetProperty("excludedNoGolden").Clone(),
                originalPublicAstArityOptimizeQualified = false, cancellationAndInjectedHost = "Separate external public-plan engineering evidence; CLI has no injection hook",
                toDateTimeNonSyntaxNativeObserved = false, selection }, options));
            await Write(Path.Combine(work, "expected.json"), JsonSerializer.SerializeToUtf8Bytes(evidence, options));
        }
        finally { foreach (var doc in raw.Values) doc.Dispose(); }

        byte[] Read(string name)
        {
            using var stream = (archive.GetEntry(name) ?? throw new IOException("Missing closure member: " + name)).Open();
            using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
        }
        async Task Write(string path, byte[] bytes)
        { Within(path, work); using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); await stream.WriteAsync(bytes); }
        async Task Exercise(string name, string expression, string source, string key, bool failure, bool table, object provenance,
            JsonElement? native = null, int lineCount = 1, bool sharedBudget = false)
        {
            var input = new Inputs(Path.Combine(work, name + "-left.txt"), Path.Combine(work, name + "-right.txt"),
                Utf8(string.Concat(Enumerable.Repeat(source + "\r\n", lineCount))), Utf8(string.Concat(Enumerable.Repeat(key + "\r\n", lineCount))));
            await Write(input.Left, input.LeftBytes); await Write(input.Right, input.RightBytes);
            sources.Add(input.Left, input.LeftBytes); sources.Add(input.Right, input.RightBytes);
            foreach (var mode in table ? new[] { "--compare", "--table" } : new[] { "--compare" })
            {
                // argvはNULを運べないので、その拒否だけproject JSONで実アプリへ渡す。
                if (expression.Contains('\0')) continue;
                var result = await run("timestamp-functions-" + name + mode, failure ? 2 : 0, !failure,
                    [mode, input.Left, input.Right, "--line-transform", "left", expression]);
                if (failure) Rejected(result, native, sharedBudget);
                else
                {
                    using var actual = JsonDocument.Parse(result.Stdout);
                    check(result.Name + "-equal", !actual.RootElement.GetProperty("different").GetBoolean(), "Native raw bytes or exact epoch/order public projection.");
                }
                await Receipt(result, input, new { expression, expectedUtf8Hex = Convert.ToHexString(Utf8(key)), provenance });
            }
            if (failure)
            {
                var project = Path.Combine(work, name + "-project.json");
                var projectBytes = JsonSerializer.SerializeToUtf8Bytes(new { mode = "Text", leftPath = input.Left, rightPath = input.Right,
                    lineTransformRules = new[] { new Rule(expression, 1) } }, options);
                await Write(project, projectBytes); sources.Add(project, projectBytes);
                var html = Path.Combine(work, name + "-existing.html"); var sentinel = Utf8("existing timestamp function report\r\n"); await Write(html, sentinel);
                var report = await run("timestamp-functions-" + name + "-report", 2, false, ["--report-project", project, html]);
                Rejected(report, native, sharedBudget);
                check(report.Name + "-existing-output", File.ReadAllBytes(html).SequenceEqual(sentinel), "Failure retained prior output; no partial publication.");
                await Receipt(report, input, new { expression, provenance, existingOutputSha256 = Hex(sentinel) });
            }
            check("timestamp-functions-" + name + "-source-preserved", File.ReadAllBytes(input.Left).SequenceEqual(input.LeftBytes)
                && File.ReadAllBytes(input.Right).SequenceEqual(input.RightBytes), "Every CLI path retained both input bytes.");
        }
        void Rejected(CommandResult result, JsonElement? native, bool sharedBudget)
        {
            check(result.Name + "-atomic", result.ExitCode == 2 && string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), "Rejected without partial JSON.");
            if (native is { } row)
            {
                var message = Decode(row.GetProperty("exceptionMessageHex").GetString()!);
                var semantic = message.StartsWith("System exception", StringComparison.Ordinal) ? "System exception" : message;
                check(result.Name + "-body-meaning", result.Stderr.Contains(semantic, StringComparison.Ordinal), "Native body rejection meaning; not C++class or public phase identity.");
            }
            if (sharedBudget) check(result.Name + "-shared-budget", result.Stderr.Contains("共有処理量", StringComparison.Ordinal), "Shared cumulative budget rejection, not parser/format failure.");
        }
        async Task Receipt(CommandResult result, Inputs input, object expected)
        {
            var item = new { result.Name, result.Arguments, result.ExitCode, expected, leftSha256 = Hex(input.LeftBytes), rightSha256 = Hex(input.RightBytes),
                stdoutSha256 = Hex(Utf8(result.Stdout)), stderrSha256 = Hex(Utf8(result.Stderr)) };
            evidence.Add(item);
            // result.Nameは長いargv由来を含まない固定ラベル、全出力を再現証拠として保持。
            await Write(Path.Combine(work, result.Name + ".receipt.json"), JsonSerializer.SerializeToUtf8Bytes(item, options));
            await Write(Path.Combine(work, result.Name + ".stdout.raw"), Utf8(result.Stdout));
            await Write(Path.Combine(work, result.Name + ".stderr.raw"), Utf8(result.Stderr));
        }
    }

    private static string InputExpression(JsonElement value) => value.GetProperty("kind").GetString() switch
    {
        "none" => "none", "bool" => value.GetProperty("value").GetBoolean() ? "true" : "false",
        "int64" => "ToInt(" + Quote(value.GetProperty("valueDecimal").GetString()!) + ")",
        "double" => value.GetProperty("value").GetDouble().ToString("R", CultureInfo.InvariantCulture),
        "string" => Quote(Decode(value.GetProperty("utf8Hex").GetString()!)),
        "timestamp" => "(" + Epoch + "+ToInt(" + Quote(value.GetProperty("epochSignedInt64Decimal").GetString()!) + "))",
        "array" => value.GetProperty("nullPointer").GetBoolean() ? throw new IOException("Excluded null array")
            : "Array(" + string.Join(',', value.GetProperty("items").EnumerateArray().Select(InputExpression)) + ")",
        _ => throw new IOException("Unsupported measured input kind")
    };
    private static (string Expression, string Expected) Projection(string call, JsonElement result)
    {
        if (result.GetProperty("kind").GetString() == "timestamp") return ("(" + call + ")-" + Epoch, result.GetProperty("epochSignedInt64Decimal").GetString()!);
        if (result.GetProperty("kind").GetString() != "array") return (call, Scalar(result));
        var items = result.GetProperty("items").EnumerateArray().ToArray();
        if (items.Length == 0) return (call, "[]");
        var projected = items.Select((value, index) => Projection("At(" + call + "," + index.ToString(CultureInfo.InvariantCulture) + ")", value)).ToArray();
        return ("Array(" + string.Join(',', projected.Select(item => item.Expression)) + ")", "[" + string.Join(", ", projected.Select(item => item.Expected)) + "]");
    }
    private static string Scalar(JsonElement value) => value.GetProperty("kind").GetString() switch
    { "none" => "none", "string" => Decode(value.GetProperty("utf8Hex").GetString()!), _ => throw new IOException("Unexpected function scalar result") };
    private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string Decode(string hex) => new UTF8Encoding(false, true).GetString(Convert.FromHexString(hex));
    private static byte[] Utf8(string text) => new UTF8Encoding(false, true).GetBytes(text);
    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Within(string path, string root)
    { var relative = Path.GetRelativePath(root, path); if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("Timestamp function path escaped root."); NoLinks(path); }
    private static void NoLinks(string path)
    { for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current)) if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Timestamp function path traverses links."); }
    private static void RequireExternal(string path, string repository)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in new[] { repository, Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
            if (Path.GetRelativePath(root, path) is var relative && !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison)) throw new IOException("External timestamp function output required.");
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(x => x.Equals("artifacts", comparison))) throw new IOException("Forbidden output folder."); NoLinks(path);
    }
    private static bool SameProfile(JsonElement row)
    {
        if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TZ"))) return false;
        var locale = new StringBuilder(85);
        if (GetUserDefaultLocaleName(locale, 85) == 0) return false;
        uint state = GetTimeZoneInformation(out var zone);
        return state != uint.MaxValue && GetUserDefaultLCID() == row.GetProperty("userLcid").GetUInt32()
            && locale.ToString() == Decode(row.GetProperty("userLocaleUtf8Hex").GetString()!)
            && state == row.GetProperty("timezoneState").GetUInt32()
            && zone.StandardName == Decode(row.GetProperty("timezoneStandardNameUtf8Hex").GetString()!)
            && zone.Bias == row.GetProperty("timezoneBiasMinutes").GetInt32()
            && zone.StandardBias == row.GetProperty("timezoneStandardBiasMinutes").GetInt32()
            && zone.DaylightBias == row.GetProperty("timezoneDaylightBiasMinutes").GetInt32()
            && -zone.Bias * 60 - (state == 2 ? zone.DaylightBias * 60 : 0) == row.GetProperty("pocoTimezoneTzdSeconds").GetInt32();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TimezoneInformation
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] DaylightDate;
        public int DaylightBias;
    }
    [DllImport("kernel32.dll")] private static extern uint GetUserDefaultLCID();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetUserDefaultLocaleName(StringBuilder name, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetTimeZoneInformation(out TimezoneInformation zone);
}
