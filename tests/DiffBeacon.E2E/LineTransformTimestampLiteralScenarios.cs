using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LineTransformTimestampLiteralScenarios
{
    private sealed record Rule(string Expression, int Panes);
    private sealed record Inputs(string Left, string Right, byte[] LeftBytes, byte[] RightBytes);
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Timestamp fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformTimestampLiterals");
        var work = Path.GetFullPath(Path.Combine(fixtures, "line-transform-timestamp-literals"));
        RequireExternal(work, repository.FullName);
        if (Directory.Exists(work) || File.Exists(work)) throw new IOException("Fresh timestamp E2E output required.");
        Directory.CreateDirectory(work);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var sources = new Dictionary<string, byte[]>(); var evidence = new List<object>(); var selection = new List<object>();
        foreach (var corpus in new[] { "primary31", "guarded48" })
        {
            var root = Path.Combine(reference, corpus);
            var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(root, "fixed-manifest.json"));
            var pinnedManifestSha = corpus == "primary31" ? "5c330afd8f815d5e4ea6398ec362f53ae6ba69d801331ea699b69bc5a2437f13" : "1113b1595877e55e2f98ffb2980b81f8d583d3a95fc013ae3ec9ded42d946443";
            if (Hex(manifestBytes) != pinnedManifestSha) throw new IOException("Timestamp frozen manifest differs.");
            using var manifest = JsonDocument.Parse(manifestBytes);
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                var path = Path.GetFullPath(Path.Combine(root, file.GetProperty("path").GetString()!)); Within(path, root);
                var bytes = await File.ReadAllBytesAsync(path);
                if (bytes.Length != file.GetProperty("size").GetInt64() || Hex(bytes) != file.GetProperty("sha256").GetString())
                    throw new IOException("Frozen timestamp payload differs: " + path);
            }
        }
        const string a = "d\"2000-01-01T00:00:00Z\"", b = "D\"2000-01-01T00:00:01Z\"";
        var engineering = new (string Id, string Expression, string Key)[]
        {
            ("equal", a + "==D\"2000-01-01T00:00:00Z\"", "true"),
            ("ordering", a + "<" + b, "true"), ("subtract", b + "-" + a, "1000000"),
            ("date-add", "(" + a + "+1000000)==" + b, "true"),
            ("date-subtract", "(" + b + "-1000000)==" + a, "true"),
            ("range", "InRange(" + a + "," + a + "," + b + ")", "true"),
            ("array-differences", "At(Array(" + a + "-" + a + "," + b + "-" + a + "),1)==1000000", "true"),
            ("min", "Minimum(Array(" + b + "," + a + "))==" + a, "true"),
            ("max", "Maximum(Array(" + b + "," + a + "))==" + b, "true"),
            ("count-mixed", "Len(Array(" + a + ",1,\"x\"))", "3"),
            // UTC年の妥当性はOS localtimeより先に検査される。原典source由来でNative未採取。
            ("utc-year-overflow", "d\"9999-12-31T23:59:59Z\"+1000000", "Invalid argument")
        };
        foreach (var test in engineering)
            foreach (var directive in new[] { "", "@noopt " })
            {
                var name = "engineering-" + test.Id + (directive.Length == 0 ? "-normal" : "-noopt");
                await Exercise(name, directive + test.Expression, Utf8(test.Key), new { classification = "source-derived-engineering", nativeTypedObserved = false });
            }
        using var joined = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(reference, "guarded48", "case-join.json")));
        var rawBytes = await File.ReadAllBytesAsync(Path.Combine(reference, "guarded48", "native-run-1.ndjson"));
        var raw = Encoding.UTF8.GetString(rawBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => { using var d = JsonDocument.Parse(line); return d.RootElement.Clone(); }).ToArray();
        if (raw.Length != 48 || joined.RootElement.GetProperty("cases").GetArrayLength() != 48) throw new IOException("Native guarded48 count differs.");
        bool matching = SameProfile(raw[0]); int serialized = 0, errors = 0, skipped = 0;
        foreach (var test in joined.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = test.GetProperty("id").GetString()!; var category = test.GetProperty("publicProposalCategory").GetString()!;
            var native = test.GetProperty("rawNativeObservation"); var binding = test.GetProperty("run1Ref");
            var row = raw[binding.GetProperty("line").GetInt32() - 1];
            if (row.GetProperty("id").GetString() != id || binding.GetProperty("sha256").GetString() != Hex(rawBytes))
                throw new IOException("Timestamp raw join differs.");
            foreach (var property in native.EnumerateObject())
                if (!JsonElement.DeepEquals(property.Value, row.GetProperty(property.Name))) throw new IOException("Timestamp raw record differs.");
            if (category != "safe32-serialized-compile-proposal" || !matching)
            {
                selection.Add(new { id, category, executed = false, reason = category == "safe32-serialized-compile-proposal" ? "Natural host profile differs; Native expected bytes not regenerated" : "No defined serialized public bridge", nativeTypedObserved = false });
                skipped++; continue;
            }
            var proposal = test.GetProperty("publicproposal");
            var payload = new UTF8Encoding(false, true).GetString(Convert.FromHexString(proposal.GetProperty("payloadUtf8Hex").GetString()!));
            foreach (var (prefix, prefixLabel) in new[] { ("d", "lower"), ("D", "upper") }) foreach (var directive in new[] { "", "@noopt " })
            {
                var name = "native-" + id + "-" + prefixLabel + (directive.Length == 0 ? "-normal" : "-noopt");
                var expression = directive + prefix + JsonSerializer.Serialize(payload);
                var provenance = new { classification = "same-profile-public-serialized-raw-bridge", nativeParserObserved = false, nativeTypedObserved = false, rawReference = binding.Clone(), rawRecord = row.Clone() };
                if (native.GetProperty("status").GetString() == "error") await Reject(name, expression, provenance);
                else await Exercise(name, expression, Convert.FromHexString(native.GetProperty("timestampArmSerializedUtf8Hex").GetString()!), provenance);
            }
            if (native.GetProperty("status").GetString() == "error") errors++; else serialized++;
            selection.Add(new { id, category, executed = true, nativeTypedObserved = false });
        }
        check("timestamp-native-selection-counts", matching ? serialized == 24 && errors == 8 && skipped == 16 : serialized == 0 && errors == 0 && skipped == 48, "Profile qualification never replaces Native bytes with calculated expectations.");
        foreach (var test in new (string Id, string Expression)[]
        {
            ("invalid", "d\"not-a-date\""), ("nul", "d\"1970-01-01\0T00:00:00Z\""),
            ("single-quote", "d'2000-01-01'"), ("utf16", "d\"SURROGATE_MARKER\""), ("unsafe-locale", "d\"1970年1月1日 0時0分0秒\""),
            ("unsafe-subtraction", a + "+9223372036854775807")
        }) await Reject("engineering-reject-" + test.Id, test.Expression, new { classification = "engineering-rejection", nativeSerializerGolden = false });
        foreach (var source in sources) check("timestamp-input-preserved-" + Path.GetFileName(source.Key), File.ReadAllBytes(source.Key).SequenceEqual(source.Value), "Inputs remain byte-identical.");
        await Write(Path.Combine(work, "selection.json"), JsonSerializer.SerializeToUtf8Bytes(new { matchingNativeProfile = matching, serialized, errors, skipped, nativeParserTypedQualified = false, selection }, options));
        await Write(Path.Combine(work, "expected.json"), JsonSerializer.SerializeToUtf8Bytes(evidence, options));

        async Task Write(string path, byte[] bytes)
        { Within(path, work); using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); await stream.WriteAsync(bytes); }
        async Task<Inputs> Create(string name, byte[] key)
        {
            var left = Path.Combine(work, name + "-left.txt"); var right = Path.Combine(work, name + "-right.txt");
            var lb = Utf8("physical timestamp probe\r\n"); var rb = key.Concat(Utf8("\r\n")).ToArray();
            await Write(left, lb); await Write(right, rb); sources.Add(left, lb); sources.Add(right, rb); return new(left, right, lb, rb);
        }
        async Task Receipt(CommandResult result, Inputs input, object expected)
        {
            var item = new { result.Name, result.Arguments, result.ExitCode, expected, leftSha256 = Hex(input.LeftBytes), rightSha256 = Hex(input.RightBytes), stdoutSha256 = Hex(Utf8(result.Stdout)), stderrSha256 = Hex(Utf8(result.Stderr)) };
            evidence.Add(item); await Write(Path.Combine(work, result.Name + ".receipt.json"), JsonSerializer.SerializeToUtf8Bytes(item, options));
            await Write(Path.Combine(work, result.Name + ".stdout.raw"), Utf8(result.Stdout)); await Write(Path.Combine(work, result.Name + ".stderr.raw"), Utf8(result.Stderr));
        }
        async Task Exercise(string name, string expression, byte[] key, object provenance)
        {
            var input = await Create(name, key);
            foreach (var mode in new[] { "--compare", "--table" })
            {
                var result = await run("timestamp-" + name + mode, 0, true, [mode, input.Left, input.Right, "--line-transform", "left", expression]);
                using var actual = JsonDocument.Parse(result.Stdout);
                check(result.Name + "-equal", !actual.RootElement.GetProperty("different").GetBoolean(), "Frozen serialized key comparison.");
                await Receipt(result, input, new { expectedSerializedUtf8Hex = Convert.ToHexString(key), provenance });
            }
            var project = Path.Combine(work, name + "-project.json");
            var dto = new { mode = "Table", leftPath = input.Left, rightPath = input.Right, tableDelimiter = ',', lineTransformRules = new[] { new Rule(expression, 1) } };
            var projectBytes = JsonSerializer.SerializeToUtf8Bytes(dto, options);
            await Write(project, projectBytes); sources.Add(project, projectBytes);
            var copy = Path.Combine(work, name + "-copy.json");
            await run("timestamp-project-copy-" + name, 0, true, ["--project-copy", project, copy]);
            using (var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(copy)))
                check("timestamp-rule-roundtrip-" + name, saved.RootElement.GetProperty("lineTransformRules")[0].GetProperty("expression").GetString() == expression, "Exact expression; no host DTO field.");
            var html = Path.Combine(work, name + ".html");
            await run("timestamp-report-" + name, 0, true, ["--report-project", copy, html]); Html(html, name);
            var workspace = Path.Combine(work, name + "-workspace.json");
            var workspaceBytes = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, entries = new[] { dto }, activeEntryIndex = 0 }, options);
            await Write(workspace, workspaceBytes); sources.Add(workspace, workspaceBytes);
            var workspaceCopy = Path.Combine(work, name + "-workspace-copy.json");
            await run("timestamp-workspace-copy-" + name, 0, true, ["--project-copy", workspace, workspaceCopy]);
            var workspaceHtml = Path.Combine(work, name + "-workspace.html");
            await run("timestamp-workspace-report-" + name, 0, true, ["--report-project", workspaceCopy, workspaceHtml]); Html(workspaceHtml, name + "-workspace");
            var package = Path.Combine(work, name + ".zip");
            await run("timestamp-package-" + name, 0, true, ["--package-project", project, package, "--report"]);
            using (var zip = ZipFile.OpenRead(package))
            {
                var assets = new List<byte[]>();
                foreach (var entry in zip.Entries.Where(x => x.Name.Length > 0)) { using var stream = entry.Open(); using var memory = new MemoryStream(); await stream.CopyToAsync(memory); assets.Add(memory.ToArray()); }
                check("timestamp-package-original-" + name, assets.Any(x => x.SequenceEqual(input.LeftBytes)) && assets.Any(x => x.SequenceEqual(input.RightBytes)), "ZIP CRC/read and original bytes.");
            }
            var expanded = Path.Combine(work, name + "-expanded");
            await run("timestamp-expand-" + name, 0, true, ["--archive-extract", package, expanded]);
            var reopened = Path.Combine(work, name + "-reopened.html");
            await run("timestamp-reopen-" + name, 0, true, ["--report-project", Path.Combine(expanded, "project.json"), reopened]); Html(reopened, name + "-reopened");
            await run("timestamp-input-protect-" + name, 2, false, ["--report-project", project, input.Left]);
            await run("timestamp-project-protect-" + name, 2, false, ["--report-project", project, project]);
        }
        async Task Reject(string name, string expression, object provenance)
        {
            var input = await Create(name, Utf8("sentinel")); var project = Path.Combine(work, name + "-project.json");
            var projectJson = JsonSerializer.Serialize(new { mode = "Text", leftPath = input.Left, rightPath = input.Right, lineTransformRules = new[] { new Rule(expression, 1) } }, options);
            bool malformedUtf16 = name == "engineering-reject-utf16";
            if (malformedUtf16) projectJson = projectJson.Replace("SURROGATE_MARKER", "\\uD800", StringComparison.Ordinal);
            var projectBytes = Utf8(projectJson); await Write(project, projectBytes); sources.Add(project, projectBytes);
            // OS argv cannot carry NUL; lone UTF16 is supplied through exact JSON escape bytes.
            if (!expression.Contains('\0') && !malformedUtf16)
            {
                var result = await run("timestamp-reject-cli-" + name, 2, false, ["--compare", input.Left, input.Right, "--line-transform", "left", expression]);
                check(result.Name + "-atomic", result.ExitCode == 2 && string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), "Rejected without partial JSON."); await Receipt(result, input, provenance);
            }
            var html = Path.Combine(work, name + "-existing.html"); var sentinel = Utf8("existing timestamp report\r\n"); await Write(html, sentinel);
            var report = await run("timestamp-reject-report-" + name, 2, false, ["--report-project", project, html]);
            check(report.Name + "-preserved", report.ExitCode == 2 && string.IsNullOrWhiteSpace(report.Stdout) && !string.IsNullOrWhiteSpace(report.Stderr) && File.ReadAllBytes(html).SequenceEqual(sentinel), "Existing report byte-identical after rejection; no partial output."); await Receipt(report, input, provenance);
        }
        void Html(string path, string label)
        { var html = File.ReadAllText(path); check("timestamp-html-" + label, html.Contains("data-different=\"false\"", StringComparison.Ordinal) && html.Contains("physical timestamp probe", StringComparison.Ordinal), "Original cells and frozen key classification."); }
    }
    private static byte[] Utf8(string text) => new UTF8Encoding(false, true).GetBytes(text);
    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Within(string path, string root)
    { var relative = Path.GetRelativePath(root, path); if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("Timestamp path escaped root."); NoLinks(path); }
    private static void NoLinks(string path)
    { for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current)) if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Timestamp path traverses links."); }
    private static void RequireExternal(string path, string repository)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in new[] { repository, Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
            if (Path.GetRelativePath(root, path) is var relative && !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison)) throw new IOException("External timestamp output required.");
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(x => x.Equals("artifacts", comparison))) throw new IOException("Forbidden output folder."); NoLinks(path);
    }
    private static bool SameProfile(JsonElement row)
    {
        if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TZ"))) return false;
        var locale = new StringBuilder(85); if (GetUserDefaultLocaleName(locale, locale.Capacity) == 0) return false;
        var state = GetTimeZoneInformation(out var zone); if (state == uint.MaxValue) return false;
        return GetUserDefaultLCID() == row.GetProperty("userLcid").GetUInt32()
            && locale.ToString() == Encoding.UTF8.GetString(Convert.FromHexString(row.GetProperty("userLocaleUtf8Hex").GetString()!))
            && state == row.GetProperty("timezoneState").GetUInt32()
            && zone.StandardName == Encoding.UTF8.GetString(Convert.FromHexString(row.GetProperty("timezoneStandardNameUtf8Hex").GetString()!))
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

