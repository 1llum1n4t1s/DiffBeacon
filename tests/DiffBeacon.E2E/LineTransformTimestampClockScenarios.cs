using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// 原典のdynamic生epochはportable reader、公開CLIは分類へのpredicate投影で分離する。
internal static class LineTransformTimestampClockScenarios
{
    private const string Epoch = "d\"1970-01-01T00:00:00Z\"";
    private const string ManifestSha = "acf0580ecddd0cdbff89aae61e2bfcdbbec3c5bf3ab10258e831f34a8d174142";
    private const string CaptureSha = "23339417df345ce7332b4dfaf4146cad32d51d1fde433beb1d7a42d8a969a1d0";
    private const string ReaderSha = "861486a3b9525b695dee74159f4e25ce8dc2cc01e1b59c52d2d73dd50cc30828";
    private sealed record Rule(string Expression, int Panes);
    private sealed record Profile(string Platform, string? Tz, string Culture, string ZoneId, uint? Lcid,
        string? Locale, uint? State, string? StandardName, int? Bias, int? StandardBias, int? DaylightBias, int? Tzd);

    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check,
        string python = "python")
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Clock fixture repository missing.");
        var reference = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformTimestampClockFunctions");
        var work = Path.GetFullPath(Path.Combine(fixtures, "line-transform-clock-functions"));
        RequireExternal(work, repository.FullName);
        if (Directory.Exists(work) || File.Exists(work)) throw new IOException("Fresh clock E2E output required.");
        Directory.CreateDirectory(work);
        foreach (var (name, sha) in new[] { ("fixed-manifest.json", ManifestSha), ("clock-native-portable-v1.zip", CaptureSha), ("verify.py", ReaderSha) })
            if (Hex(await File.ReadAllBytesAsync(Path.Combine(reference, name))) != sha) throw new IOException("Fixed clock bytes differ: " + name);
        if (new FileInfo(Path.Combine(reference, "clock-native-portable-v1.zip")).Length != 2_765_601) throw new IOException("Clock ZIP size differs.");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        var inputs = new Dictionary<string, byte[]>(); var receipts = new List<object>(); var qualification = new List<object>();
        await VerifyReference();
        // unsupportedはWindows/Mac以外だけ。Macは原典same-profile実測の対象外だが製品hostである。
        var supported = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        if (!supported)
        {
            await Exercise("missing-host", "Now()", "clock", "", true);
            qualification.Add(new { scope = "public-clock", qualified = false, reason = "No supported Windows/macOS host" });
        }
        else
        {
            foreach (var prefix in new[] { "", "@noopt " })
            {
                var label = prefix.Length == 0 ? "normal" : "noopt";
                foreach (var (name, expression, expected) in new[]
                {
                    ("now-self", "Now()-Now()", "0"), ("today-self", "Today()-Today()", "0"),
                    ("timestamp-not-date-input", "Array(ToDateTime(Now()),ToDateTime(Today()))", "[none, none]"),
                    ("mixed", "Array(Now()-Now(),Len(Line),ToDateTime(Now()),ToDateTime(Today()))", "[0, 5, none, none]"),
                    ("non-clock", "Len(Line)", "5")
                }) await Exercise(label + "-" + name, prefix + expression, "clock", expected, false);
                // 異なる原文をall側に投影。ルール一つの全pane・全row snapshot関係を分類で観測する。
                foreach (var function in new[] { "Now", "Today" })
                    await AllRows(label + "-" + function.ToLowerInvariant(), prefix + function + "()-" + Epoch);
                await Dynamic(label + "-now-range", prefix, false, false);
                await Dynamic(label + "-today-midnight", prefix, true, false);
                await Dynamic(label + "-serial-date", prefix, false, true);
            }
            foreach (var function in new[] { "Now", "Today" })
                foreach (var prefix in new[] { "", "@noopt " })
                    foreach (var args in new[] { "none", "none,none" })
                        await Exercise("arity-" + function.ToLowerInvariant() + "-" + (prefix.Length == 0 ? "normal" : "noopt") + "-" + args.Count(c => c == ','), prefix + function + "(" + args + ")", "clock", "", true);
            await Roundtrip();
        }
        foreach (var input in inputs)
            check("clock-input-" + Path.GetFileName(input.Key), File.ReadAllBytes(input.Key).SequenceEqual(input.Value), "Input/project/workspace bytes retained.");
        await Write("expected.json", JsonSerializer.SerializeToUtf8Bytes(new { receipts, qualification,
            provenance = "source-derived-public-engineering; native body dynamic relation separately verified by portable reader",
            exactPublicEpochBeforeAfterQualified = false, perRuleAndRecompileCaptureCountsQualified = false,
            cancellationQualified = false, cancellationEvidence = "Separate public Core/GUI injected harness; no CLI cancel injection API",
            fixedEpochGolden = false, twoRunEpochEqualityRequired = false }, options));

        async Task<string> Input(string name, string text)
        {
            var path = Path.Combine(work, name); var bytes = Utf8(text);
            await Write(name, bytes); inputs.Add(path, bytes); return path;
        }
        async Task Write(string name, byte[] bytes)
        {
            var path = Path.Combine(work, name); Within(path, work);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await stream.WriteAsync(bytes);
        }
        async Task Receipt(CommandResult result, object expected)
        {
            var item = new { result.Name, result.Arguments, result.ExitCode, result.Pid, result.CreationUtc,
                result.LaunchUtc, result.ExitObservedUtc, result.LaunchEvidence, expected,
                classification = "source-derived-public-engineering", stdoutSha256 = Hex(Utf8(result.Stdout)), stderrSha256 = Hex(Utf8(result.Stderr)) };
            receipts.Add(item); await Write(result.Name + ".receipt.json", JsonSerializer.SerializeToUtf8Bytes(item, options));
            await Write(result.Name + ".stdout.raw", Utf8(result.Stdout)); await Write(result.Name + ".stderr.raw", Utf8(result.Stderr));
        }
        async Task Exercise(string name, string expression, string source, string key, bool rejected)
        {
            var left = await Input(name + "-left.txt", source); var right = await Input(name + "-right.txt", key);
            foreach (var mode in new[] { "--compare", "--table" })
            {
                var result = await run("clock-" + name + (mode == "--compare" ? "-text" : "-table"), rejected ? 2 : 0, !rejected,
                    [mode, left, right, "--line-transform", "left", expression]);
                if (rejected) Reject(result);
                else { using var json = JsonDocument.Parse(result.Stdout); check(result.Name + "-equal", !json.RootElement.GetProperty("different").GetBoolean(), "Source-derived public relation, not fixed native epoch."); }
                await Receipt(result, new { expression, expected = key, rejected });
            }
            if (rejected)
            {
                var project = await Project(name + "-project.json", left, right, expression, "Text");
                var html = Path.Combine(work, name + "-existing.html"); var sentinel = Utf8("existing clock report\r\n"); await Write(Path.GetFileName(html), sentinel);
                var result = await run("clock-" + name + "-report", 2, false, ["--report-project", project, html]); Reject(result);
                check(result.Name + "-preserved", File.ReadAllBytes(html).SequenceEqual(sentinel), "Invalid arity retains existing report.");
                await Receipt(result, new { expression, rejected = true, sentinelSha256 = Hex(sentinel) });
                var protectedResult = await run("clock-" + name + "-protected", 2, false, ["--report-project", project, left]); Reject(protectedResult);
                check(protectedResult.Name + "-input-protected", File.ReadAllBytes(left).SequenceEqual(inputs[left]), "Report output cannot overwrite input.");
                await Receipt(protectedResult, new { protectedInput = left });
            }
            else
            {
                var project = await Project(name + "-project.json", left, right, expression, "Text");
                var html = Path.Combine(work, name + "-report.html");
                var result = await run("clock-" + name + "-report", 0, true, ["--report-project", project, html]);
                var text = File.ReadAllText(html);
                check(result.Name + "-equal-originals", text.Contains("data-different=\"false\"", StringComparison.Ordinal)
                    && text.Contains(System.Net.WebUtility.HtmlEncode(source), StringComparison.Ordinal)
                    && text.Contains(System.Net.WebUtility.HtmlEncode(key), StringComparison.Ordinal), "Equal predicate classification retains original source bodies.");
                await Receipt(result, new { expression, expected = key, transformedSourceWritten = false });
            }
        }
        void Reject(CommandResult result) => check(result.Name + "-atomic", result.ExitCode == 2 && string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), "Rejected without partial JSON.");
        async Task<string> Project(string name, string left, string right, string expression, string mode) => await Input(name,
            JsonSerializer.Serialize(new { mode, leftPath = left, rightPath = right, lineTransformRules = new[] { new Rule(expression, 1) } }, options));
        async Task AllRows(string name, string expression)
        {
            var left = await Input(name + "-left.txt", string.Join('\n', Enumerable.Range(0, 2048).Select(n => "left-" + n)));
            var right = await Input(name + "-right.txt", string.Join('\n', Enumerable.Range(0, 2048).Select(n => "right-" + n)));
            foreach (var mode in new[] { "--compare", "--table" })
            {
                var result = await run("clock-" + name + (mode == "--compare" ? "-text" : "-table"), 0, true,
                    [mode, left, right, "--line-transform", "all", expression]);
                using var json = JsonDocument.Parse(result.Stdout);
                check(result.Name + "-same-plan-snapshot", !json.RootElement.GetProperty("different").GetBoolean(), "2048 rows across both panes use one rule snapshot; CLI does not expose host call count.");
                await Receipt(result, new { expression, rows = 2048, perRuleCaptureCountQualified = false });
            }
        }
        async Task Dynamic(string name, string prefix, bool today, bool serialDate)
        {
            var beforeProfile = CurrentProfile(); var before = DateTime.UtcNow; var timer = Stopwatch.StartNew();
            var beforeUs = EpochUs(before); var deadline = before.AddSeconds(60); var upperUs = EpochUs(deadline);
            var localDay = before.AddHours(9).Date;
            var expectedMidnight = EpochUs(DateTime.SpecifyKind(localDay.AddHours(-9), DateTimeKind.Utc));
            var expression = serialDate ? "ToDateStr(Now())=" + Quote(localDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                : today ? "(Today()-" + Epoch + ")=" + Int(expectedMidnight)
                : "((Now()-" + Epoch + ")>=" + Int(beforeUs) + ") and ((Now()-" + Epoch + ")<=" + Int(upperUs) + ")";
            expression = prefix + expression;
            var left = await Input(name + "-left.txt", "clock\nclock\nclock"); var right = await Input(name + "-right.txt", "true\ntrue\ntrue");
            var project = await Project(name + "-project.json", left, right, expression, "Text"); var html = Path.Combine(work, name + ".html");
            // Reportは分類がfalseでもexit0。期限切れ等をRunのexit期待失敗へ混ぜず資格falseで記録する。
            var result = await run("clock-" + name + "-report", 0, true, ["--report-project", project, html]);
            var after = DateTime.UtcNow; timer.Stop(); var afterProfile = CurrentProfile();
            var reasons = new List<string>();
            if (after < before) reasons.Add("observed-backward-clock");
            if (after >= deadline) reasons.Add("precomputed-upper-expired");
            if (Math.Abs((after - before).TotalSeconds - timer.Elapsed.TotalSeconds) > 1) reasons.Add("suspected-clock-jump-vs-monotonic-elapsed");
            if (beforeProfile != afterProfile) reasons.Add("parent-profile-changed");
            if (before.ToLocalTime().Date != after.ToLocalTime().Date) reasons.Add("parent-local-midnight-crossed");
            if ((today || serialDate) && (!MatchesRecordedNativeProfile(beforeProfile) || !MatchesRecordedNativeProfile(afterProfile))) reasons.Add("native-recorded-Tokyo-profile-mismatch");
            // 原典profileにTZの記録はない。これは現在の工程の保守的条件で、原典との一致証拠ではない。
            if ((today || serialDate) && (!string.IsNullOrEmpty(beforeProfile.Tz) || !string.IsNullOrEmpty(afterProfile.Tz))) reasons.Add("current-TZ-override-unqualified-native-TZ-unrecorded");
            if ((today || serialDate) && before.AddHours(9).Date != after.AddHours(9).Date) reasons.Add("Tokyo-midnight-crossed");
            var qualified = reasons.Count == 0; var text = File.Exists(html) ? File.ReadAllText(html) : "";
            check(result.Name + "-published", text.Contains("data-different=\"", StringComparison.Ordinal), "Existing HTML classification is present.");
            if (qualified) check(result.Name + "-predicate", text.Contains("data-different=\"false\"", StringComparison.Ordinal), "Source-derived bounded predicate projection; not actual emitted epoch / exact after bound.");
            var evidence = new { name, qualified, reasons, before, after, beforeUs, upperUs, deadline, monotonicElapsedSeconds = timer.Elapsed.TotalSeconds,
                beforeProfile, afterProfile, expression, expectedMidnight, today, serialDate,
                predicateProjection = "source-derived bounded predicate projection", actualEpochOutput = false,
                exactAfterBoundProven = false, nativeProfileQualificationRequired = today || serialDate,
                nativeCurrentProfileQualification = qualified && (today || serialDate), nativeTzComparisonQualified = false,
                hostClockJumpDetection = "parent endpoint and >1s wall/monotonic discrepancy only; subthreshold jumps unqualified by no claim" };
            qualification.Add(evidence); await Receipt(result, evidence);
        }
        async Task Roundtrip()
        {
            const string expression = "@noopt Now()-Now()";
            var left = await Input("roundtrip-left.txt", "clock"); var right = await Input("roundtrip-right.txt", "0");
            var project = await Project("roundtrip-project.json", left, right, expression, "Text");
            var workspace = await Input("roundtrip-workspace.json", JsonSerializer.Serialize(new { formatVersion = 1,
                entries = new[] { new { mode = "Text", leftPath = left, rightPath = right, lineTransformRules = new[] { new Rule(expression, 1) } },
                    new { mode = "Table", leftPath = left, rightPath = right, lineTransformRules = new[] { new Rule(expression, 1) } } }, activeEntryIndex = 1 }, options));
            foreach (var original in new[] { project, workspace })
            {
                var label = Path.GetFileNameWithoutExtension(original); var copy = original + ".copy.json"; var again = original + ".again.json";
                var copied = await run("clock-" + label + "-copy", 0, true, ["--project-copy", original, copy]);
                var recopied = await run("clock-" + label + "-recopy", 0, true, ["--project-copy", copy, again]);
                check(recopied.Name + "-stable", File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(again)), "Project/workspace JSON stable after roundtrip.");
                using var saved = JsonDocument.Parse(File.ReadAllBytes(again));
                var entries = saved.RootElement.TryGetProperty("entries", out var values) ? values.EnumerateArray().ToArray() : [saved.RootElement];
                check(recopied.Name + "-rules", entries.All(e => e.GetProperty("lineTransformRules")[0].GetProperty("expression").GetString() == expression), "Text/Table clock rules retained.");
                var html = again + ".html"; var reloaded = await run("clock-" + label + "-reload", 0, true, ["--report-project", again, html]);
                var text = File.ReadAllText(html); check(reloaded.Name + "-html", text.Contains("data-different=\"false\"", StringComparison.Ordinal)
                    && text.Contains("clock", StringComparison.Ordinal), "Recompiled current plan retains equality and original source; no cross-run clock equality claim.");
                await Receipt(copied, new { expression }); await Receipt(recopied, new { expression }); await Receipt(reloaded, new { expression });
            }
            var protect = await run("clock-roundtrip-project-output-protected", 2, false, ["--report-project", project, project]); Reject(protect);
            check(protect.Name + "-unchanged", File.ReadAllBytes(project).SequenceEqual(inputs[project]), "Workspace/project input cannot become report output."); await Receipt(protect, new { protectedInput = project });
            var leafProtect = await run("clock-roundtrip-leaf-output-protected", 2, false, ["--report-project", project, left]); Reject(leafProtect);
            check(leafProtect.Name + "-unchanged", File.ReadAllBytes(left).SequenceEqual(inputs[left]), "A valid clock report cannot overwrite input source."); await Receipt(leafProtect, new { protectedInput = left });
        }
        async Task VerifyReference()
        {
            // -Eはreader interpreterにだけ適用し、assertを消すPYTHONOPTIMIZE継承を抑止する。Mac gateもこのargvを転送する。
            string[] arguments = ["-E", "-B", Path.Combine(reference, "verify.py"), "--archive", Path.Combine(reference, "clock-native-portable-v1.zip"), "--fixed", Path.Combine(reference, "fixed-manifest.json")];
            string stdout, stderr; object processEvidence; bool qualified;
            if (OperatingSystem.IsMacOS())
            {
                var result = await MacCommandLauncher.RunAsync(python, python, "clock-portable-reader", null, arguments, new UTF8Encoding(false));
                qualified = result.ExitCode == 0 && result.LaunchEvidence is { Terminal: true, StdoutComplete: true, StderrComplete: true, PipesReleased: true, ProcessDisposed: true, ActualOsBirthTimeUtc: not null };
                stdout = result.Stdout; stderr = result.Stderr; processEvidence = result;
            }
            else
            {
                var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
                start.Environment["PYTHONDONTWRITEBYTECODE"] = "1"; foreach (var argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new IOException("Clock portable reader did not start.");
                var birth = process.StartTime.ToUniversalTime(); var handle = process.SafeHandle;
                var outTask = process.StandardOutput.ReadToEndAsync(); var errTask = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); throw; }
                stdout = await outTask; stderr = await errTask; qualified = process.ExitCode == 0 && process.HasExited && !handle.IsClosed && !handle.IsInvalid;
                processEvidence = new { process.Id, birth, process.ExitCode, handleHeldThroughExitBothEof = qualified, qualified, arguments };
            }
            await Write("reader.stdout.raw", Utf8(stdout)); await Write("reader.stderr.raw", Utf8(stderr));
            await Write("reader.process.json", JsonSerializer.SerializeToUtf8Bytes(processEvidence, options));
            check("clock-native-portable-reader-terminal", qualified, "Reader exited0 with full stdout/stderr and held process identity; Mac uses fixed launch gate.");
            if (!qualified) throw new IOException("Clock portable reader unqualified.");
            using var json = JsonDocument.Parse(stdout); var root = json.RootElement;
            check("clock-native-dynamic-proof", root.GetProperty("status").GetString() == "portable-clock-closure-qualified"
                && root.GetProperty("actualUpdateCalls").GetInt32() == 4 && root.GetProperty("actualNowCalls").GetInt32() == 12
                && root.GetProperty("actualTodayCalls").GetInt32() == 12 && root.GetProperty("sameSnapshotAndDynamicRangeAndLocalMidnight").GetBoolean()
                && !root.GetProperty("twoRunExactEpochEqualityRequired").GetBoolean() && root.GetProperty("actorPathsOpened").GetInt32() == 0,
                "Original dynamic raw/source/receipt joins only; public AST/Optimize and other profiles unmeasured.");
        }
    }
    private static string Int(long value) => "ToInt(" + Quote(value.ToString(CultureInfo.InvariantCulture)) + ")";
    private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static long EpochUs(DateTime utc) => checked((utc.Ticks - DateTime.UnixEpoch.Ticks) / 10);
    private static byte[] Utf8(string text) => new UTF8Encoding(false, true).GetBytes(text);
    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static Profile CurrentProfile()
    {
        if (!OperatingSystem.IsWindows()) return new(OperatingSystem.IsMacOS() ? "macOS" : "other", Environment.GetEnvironmentVariable("TZ"), CultureInfo.CurrentCulture.Name, TimeZoneInfo.Local.Id, null, null, null, null, null, null, null, null);
        var locale = new StringBuilder(85); var length = GetUserDefaultLocaleName(locale, 85); var state = GetTimeZoneInformation(out var zone);
        return new("Windows", Environment.GetEnvironmentVariable("TZ"), CultureInfo.CurrentCulture.Name, TimeZoneInfo.Local.Id,
            GetUserDefaultLCID(), length == 0 ? null : locale.ToString(), state, zone.StandardName, zone.Bias, zone.StandardBias, zone.DaylightBias,
            state == uint.MaxValue ? null : -zone.Bias * 60 - (state == 2 ? zone.DaylightBias * 60 : 0));
    }
    private static bool MatchesRecordedNativeProfile(Profile p) => p.Platform == "Windows"
        && p.Lcid == 1041 && p.Locale == "ja-JP" && p.State == 0 && p.StandardName == "東京 (標準時)"
        && p.Bias == -540 && p.StandardBias == 0 && p.DaylightBias == -60 && p.Tzd == 32400;
    private static void Within(string path, string root)
    { var relative = Path.GetRelativePath(root, path); if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("Clock output escaped root."); NoLinks(path); }
    private static void RequireExternal(string path, string repository)
    {
        foreach (var root in new[] { repository, Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        { var relative = Path.GetRelativePath(root, path); if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("External clock E2E output required."); }
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.Equals("artifacts", StringComparison.OrdinalIgnoreCase))) throw new IOException("Forbidden output folder."); NoLinks(path);
    }
    private static void NoLinks(string path)
    { for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current)) if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Clock output traverses link."); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TimezoneInformation
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
