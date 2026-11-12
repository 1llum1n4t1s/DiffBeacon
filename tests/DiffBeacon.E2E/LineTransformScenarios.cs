using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class LineTransformScenarios
{
    private sealed record Rule(string Expression, int Panes);
    private sealed record Inputs(string[] Paths, string[] Texts, byte[][] Bytes);

    internal static async Task RunGuiAsync(string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check, string python)
    {
        var repository = FixtureRepository.FindRoot() ?? throw new IOException("Line transform fixture repository missing.");
        var gui = Path.GetFullPath(Path.Combine(fixtures, "line-transform-gui"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var codex = Path.GetFullPath(Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        foreach (var forbidden in new[] { repository.FullName, codex, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (gui.Equals(root, comparison) || gui.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new IOException("Line transform GUI verification requires external task output.");
        }
        if (gui.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Line transform GUI output cannot use an artifacts directory.");
        NoLinks(gui);
        if (File.Exists(gui) || Directory.Exists(gui)) throw new IOException("Line transform GUI requires a fresh output directory.");
        var result = await run("line-transform-real-controls", 0, false, ["--self-test", gui, "--line-transform-only"]);
        var reportPath = Path.Combine(gui, "ui-report.json");
        check("line-transform-gui-report-exists", result.ExitCode == 0 && File.Exists(reportPath), reportPath);
        if (result.ExitCode != 0 || !File.Exists(reportPath)) throw new IOException("Line transform GUI did not complete successfully.");
        NoLinks(reportPath);
        using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(reportPath));
        var assertions = report.RootElement.GetProperty("assertions").EnumerateArray().ToArray();
        var qualifiedGui = report.RootElement.GetProperty("scope").GetString() == "line-transform-only"
            && assertions.Length > 0 && assertions.All(assertion => assertion.GetProperty("passed").GetBoolean());
        check("line-transform-gui-scope-all-assertions", qualifiedGui, "Scope line-transform-only; assertions=" + assertions.Length);
        if (!qualifiedGui) throw new IOException("Line transform GUI scope or assertions failed.");
        var fixtureRoot = Path.GetFullPath(report.RootElement.GetProperty("fixtures").GetString()
            ?? throw new IOException("Line transform GUI fixture root missing."));
        Within(fixtureRoot, gui);
        foreach (var corpus in new[] { "LineTransforms", "LineTransformArrays", "LineTransformSearch", "LineTransformReplace", "LineTransformRanges", "LineTransformWithin", "LineTransformAggregatesNative", "LineTransformAggregates", "LineTransformSizeLiteralsNative", "LineTransformSizeLiterals", "LineTransformDurationLiteralsNative", "LineTransformDurationLiterals", "LineTransformVersionLiteralsNative", "LineTransformVersionLiterals" })
        {
            var arrays = corpus == "LineTransformArrays";
            var search = corpus == "LineTransformSearch";
            var replace = corpus == "LineTransformReplace";
            var ranges = corpus == "LineTransformRanges";
            var within = corpus == "LineTransformWithin";
            var nativeVersions = corpus == "LineTransformVersionLiteralsNative";
            var versions = nativeVersions || corpus == "LineTransformVersionLiterals";
            var nativeDurations = corpus == "LineTransformDurationLiteralsNative";
            var durations = nativeDurations || corpus == "LineTransformDurationLiterals";
            var nativeSizes = corpus == "LineTransformSizeLiteralsNative";
            var sizes = nativeSizes || corpus == "LineTransformSizeLiterals";
            var nativeAggregates = corpus == "LineTransformAggregatesNative";
            var aggregates = nativeAggregates || corpus == "LineTransformAggregates";
            var fixtureCorpus = versions ? "LineTransformVersionLiterals" : nativeDurations ? "LineTransformDurationLiterals" : nativeSizes ? "LineTransformSizeLiterals" : nativeAggregates ? "LineTransformAggregates" : corpus;
            var prefix = nativeVersions ? "version-literal-native-" : versions ? "version-literal-" : nativeDurations ? "duration-literal-native-" : durations ? "duration-literal-" : nativeSizes ? "size-literal-native-" : sizes ? "size-literal-" : nativeAggregates ? "aggregate-native-" : aggregates ? "aggregate-" : within ? "iswithin-" : ranges ? "inrange-" : replace ? "replace-" : search ? "search-" : arrays ? "array-" : "";
            var actual = versions ? Path.Combine(fixtureRoot, "line-transform-version-literals", "public-observations.json") : Path.Combine(fixtureRoot, durations ? "line-transform-duration-literals" : sizes ? "line-transform-size-literals" : aggregates ? "line-transform-aggregates" : within ? "line-transform-within" : ranges ? "line-transform-ranges" : replace ? "line-transform-replace" : search ? "line-transform-search" : arrays ? "line-transform-arrays" : "line-transforms", "diagnostics.ndjson");
            Within(actual, gui);
            if (!File.Exists(actual)) throw new IOException("Line transform product diagnostics missing.");
            var readerArguments = new List<string> { "-X", "utf8", "-B", Path.Combine(repository.FullName, "tests", "Fixtures", fixtureCorpus, (durations && !nativeDurations) || (sizes && !nativeSizes) || (aggregates && !nativeAggregates) ? "verify-public.py" : "verify.py") };
            if (versions)
            {
                if (!nativeVersions)
                {
                    var versionProbes = Path.Combine(fixtureRoot, "line-transform-version-literals", "public-probes.json");
                    var versionEngineering = Path.Combine(fixtureRoot, "line-transform-version-literals", "engineering-observations.json");
                    Within(versionProbes, gui); Within(versionEngineering, gui); NoLinks(versionProbes); NoLinks(versionEngineering);
                    if (!File.Exists(versionProbes) || !File.Exists(versionEngineering)) throw new IOException("Version product probes/engineering missing.");
                    readerArguments.AddRange(["--public", actual, "--probes", versionProbes, "--engineering", versionEngineering]);
                }
            }
            else if (nativeDurations || nativeAggregates || nativeSizes)
                readerArguments.AddRange(["--reference-zip", Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformReplace", "reference-source.zip")]);
            else readerArguments.AddRange(["--actual", actual]);
            if (arrays || search) readerArguments.Add("--public-plan");
            if (replace || ranges || within || aggregates && !nativeAggregates)
            {
                readerArguments.Add("--public-plan");
                readerArguments.Add(Path.Combine(repository.FullName, "tests", "Fixtures", fixtureCorpus, "public-case-plan.json"));
            }
            if (ranges || within)
            {
                var probes = Path.Combine(fixtureRoot, within ? "line-transform-within" : "line-transform-ranges", "probes.ndjson");
                Within(probes, gui);
                if (!File.Exists(probes)) throw new IOException("Line transform product constructor probes missing.");
                NoLinks(probes);
                readerArguments.Add("--probes");
                readerArguments.Add(probes);
            }
            if (aggregates && !nativeAggregates)
            {
                var probes = Path.Combine(fixtureRoot, "line-transform-aggregates", "arity-probes.ndjson");
                Within(probes, gui); NoLinks(probes);
                if (!File.Exists(probes)) throw new IOException("Aggregate product arity probes missing.");
                readerArguments.AddRange(["--probes", probes]);
            }
            if (sizes && !nativeSizes)
            {
                var probes = Path.Combine(fixtureRoot, "line-transform-size-literals", "public-probes.ndjson");
                Within(probes, gui); NoLinks(probes);
                if (!File.Exists(probes)) throw new IOException("Size literal public probes missing.");
                readerArguments.AddRange(["--probes", probes, "--fixture", Path.Combine(repository.FullName, "tests", "Fixtures", fixtureCorpus)]);
            }
            if (durations && !nativeDurations)
            {
                var durationFixture = Path.Combine(repository.FullName, "tests", "Fixtures", fixtureCorpus);
                var probes = Path.Combine(fixtureRoot, "line-transform-duration-literals", "public-probes.ndjson");
                var engineering = Path.Combine(fixtureRoot, "line-transform-duration-literals", "engineering.ndjson");
                foreach (var path in new[] { probes, engineering })
                {
                    Within(path, gui); NoLinks(path);
                    if (!File.Exists(path)) throw new IOException("Duration literal public diagnostics missing.");
                }
                readerArguments.AddRange(["--probes", probes, "--engineering", engineering, "--fixture", durationFixture,
                    "--native-proof", Path.Combine(durationFixture, "native-evidence", "native-proof.zip")]);
            }
            var arguments = readerArguments.ToArray();
            string stdoutText, stderrText;
            int pid;
            object evidence;
            bool qualified;
            if (OperatingSystem.IsMacOS())
            {
                // 既存の固定gateを通し、同PIDの実birth・終了・EOFの証拠を維持する。
                var reader = await MacCommandLauncher.RunAsync(python, python, "line-transform-reference-reader", null, arguments, new UTF8Encoding(false));
                var launch = reader.LaunchEvidence;
                qualified = reader.ExitCode == 0 && launch is
                    { Terminal: true, StdoutComplete: true, StderrComplete: true, PipesReleased: true, ProcessDisposed: true }
                    && launch.ActualOsBirthTimeUtc is not null;
                evidence = new { reader.Pid, reader.CreationUtc, reader.ExitCode, qualified, launchEvidence = launch };
                pid = reader.Pid; stdoutText = reader.Stdout; stderrText = reader.Stderr;
            }
            else
            {
                var start = new ProcessStartInfo(python)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
                };
                start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                var process = Process.Start(start) ?? throw new IOException("Line transform independent reader did not start.");
                pid = process.Id; var handle = process.SafeHandle;
                DateTime? birth = null; string? error = null;
                using var capture = new CancellationTokenSource();
                var stdout = process.StandardOutput.ReadToEndAsync(capture.Token);
                var stderr = process.StandardError.ReadToEndAsync(capture.Token);
                var terminal = false; var timedOut = false; var heldAtTerminal = false; int? exit = null;
                try
                {
                    birth = process.StartTime.ToUniversalTime();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                    terminal = process.HasExited; heldAtTerminal = !handle.IsInvalid && !handle.IsClosed; exit = process.ExitCode;
                    await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception failure)
                {
                    error = failure.ToString(); timedOut = failure is TimeoutException;
                    try
                    {
                        if (!process.HasExited) process.Kill(true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        terminal = process.HasExited; heldAtTerminal = !handle.IsInvalid && !handle.IsClosed; exit = process.ExitCode;
                        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception cleanup) { error += "\n" + cleanup; capture.Cancel(); }
                }
                var stdoutComplete = stdout.IsCompletedSuccessfully; var stderrComplete = stderr.IsCompletedSuccessfully;
                stdoutText = stdoutComplete ? stdout.Result : ""; stderrText = (stderrComplete ? stderr.Result : "") + error;
                var disposed = terminal && stdoutComplete && stderrComplete;
                if (disposed) process.Dispose();
                qualified = birth.HasValue && terminal && heldAtTerminal && disposed && exit == 0 && error is null;
                evidence = new { pid, birthUtc = birth, exitCode = exit, terminal, processHandleHeldAtTerminal = heldAtTerminal,
                    stdoutComplete, stderrComplete, pipesReleased = disposed, processDisposed = disposed, timedOut, error, qualified };
            }
            Fresh(prefix + "reference-reader-process.json", JsonSerializer.Serialize(new { executable = python, arguments, evidence }));
            Fresh(prefix + "reference-reader.stdout.raw", stdoutText);
            Fresh(prefix + "reference-reader.stderr.raw", stderrText);
            check(nativeVersions ? "line-transform-version-literal-native-reference-reader-os-terminal-eof" : versions ? "line-transform-version-literal-public-reference-reader-os-terminal-eof" : nativeDurations ? "line-transform-duration-literal-native-reference-reader-os-terminal-eof" : durations ? "line-transform-duration-literal-public-reference-reader-os-terminal-eof" : nativeSizes ? "line-transform-size-literal-native-reference-reader-os-terminal-eof" : sizes ? "line-transform-size-literal-public-reference-reader-os-terminal-eof" : nativeAggregates ? "line-transform-aggregate-native-reference-reader-os-terminal-eof" : aggregates ? "line-transform-aggregate-public-reference-reader-os-terminal-eof" : within ? "line-transform-within-reference-reader-os-terminal-eof" : ranges ? "line-transform-range-reference-reader-os-terminal-eof" : replace ? "line-transform-replace-reference-reader-os-terminal-eof" : search ? "line-transform-search-reference-reader-os-terminal-eof" : arrays ? "line-transform-array-reference-reader-os-terminal-eof" : "line-transform-reference-reader-os-terminal-eof", qualified, "Reader PID=" + pid);
            if (!qualified) throw new IOException("Line transform independent reader terminal/EOF qualification failed.");
            if (versions)
            {
                var summaries = stdoutText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (summaries.Length != 1) throw new IOException("Version reader requires one complete JSON summary.");
                using var versionSummaryDocument = JsonDocument.Parse(summaries[0]);
                var versionSummary = versionSummaryDocument.RootElement;
                var versionFixture = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformVersionLiterals");
                string VersionHash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
                var valid = versionSummary.GetProperty("immutableFormalCandidate").GetBoolean()
                    && versionSummary.GetProperty("selfContainedNativeSourceChecks").GetArrayLength() == 3
                    && versionSummary.GetProperty("publicPlanRecords").GetInt32() == 156
                    && versionSummary.GetProperty("publicSerializedOnly").GetBoolean()
                    && !versionSummary.GetProperty("publicTypedObserved").GetBoolean()
                    && !versionSummary.GetProperty("nativeFullAstQualified").GetBoolean()
                    && versionSummary.GetProperty("publicPlanSha256").GetString() == VersionHash(Path.Combine(versionFixture, "public-case-plan.json"))
                    && versionSummary.GetProperty("fixedManifestSha256").GetString() == VersionHash(Path.Combine(versionFixture, "fixed-manifest.json"))
                    && versionSummary.GetProperty("immutableStagesSha256").GetString() == VersionHash(Path.Combine(versionFixture, "immutable-stages.json"));
                if (!nativeVersions)
                {
                    var actualPublic = versionSummary.GetProperty("actualPublic");
                    valid &= actualPublic.GetProperty("executedRecords").GetInt32() == 156
                        && actualPublic.GetProperty("serializedSuccesses").GetInt32() == 154
                        && actualPublic.GetProperty("compileRejections").GetInt32() == 2
                        && !actualPublic.GetProperty("publicTypedObserved").GetBoolean()
                        && !actualPublic.GetProperty("actualConstructorOriginObserved").GetBoolean()
                        && versionSummary.GetProperty("actualProbes").GetProperty("executedRecords").GetInt32() == 12
                        && versionSummary.GetProperty("actualEngineering").GetProperty("executedRecords").GetInt32() == 34
                        && versionSummary.GetProperty("actualPublicDiagnosticsSha256").GetString() == VersionHash(actual)
                        && versionSummary.GetProperty("actualProbesDiagnosticsSha256").GetString() == VersionHash(Path.Combine(fixtureRoot, "line-transform-version-literals", "public-probes.json"))
                        && versionSummary.GetProperty("actualEngineeringDiagnosticsSha256").GetString() == VersionHash(Path.Combine(fixtureRoot, "line-transform-version-literals", "engineering-observations.json"));
                }
                check(nativeVersions ? "line-transform-version-native-source-proof" : "line-transform-version-public154-nul2-probes12-engineering34", valid, stdoutText);
                Fresh(prefix + "reference-reader-receipt.json", JsonSerializer.Serialize(new {
                    readerPid = pid, qualified, valid, nativeSourceOnly = nativeVersions, publicPlanRecords = 156,
                    actualPublicRecords = nativeVersions ? 0 : 156, publicTypedObserved = false,
                    nativeFullParserExecuted = false, nativeTypedContextQualified = false, nativeFullAstQualified = false,
                    actual, processMetadata = prefix + "reference-reader-process.json", scope = "line-transform-only"
                }));
                if (!valid) throw new IOException("Version reader did not qualify source/public scope.");
                continue;
            }
            if (durations)
            {
                var summaries = stdoutText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (summaries.Length != 1) throw new IOException("Duration literal reader requires one complete JSON summary.");
                using var durationSummary = JsonDocument.Parse(summaries[0]);
                var durationValue = durationSummary.RootElement;
                var durationFixture = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformDurationLiterals");
                var goldenPath = Path.Combine(durationFixture, "golden.ndjson");
                var proofPath = Path.Combine(durationFixture, "native-evidence", "native-proof.zip");
                var planPath = Path.Combine(durationFixture, "public-case-plan.json");
                var proposalsPath = Path.Combine(durationFixture, "public-probes-and-boundaries.json");
                const string goldenSha = "bbd10685c14154365a905239292b0fcd3024d31ab70cd7eaf96b7d5453693aec";
                const string proofSha = "faf6aade2043c2845ea4ec9b268f523426cb7712913bde699a38a9054c377d8e";
                const string planSha = "e891e218b68d8501d31fa38ff84a16806f6913df60a28b8b48a96b0dadd70ab9";
                const string proposalsSha = "fc7b7ebd5d1146f1c7f95660199ee8c87f181ed7299b74af67b0620132484f18";
                string DurationHash(string path)
                {
                    NoLinks(path);
                    return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                }
                var valid = durationValue.GetProperty("passed").GetBoolean()
                    && DurationHash(goldenPath) == goldenSha && DurationHash(proofPath) == proofSha
                    && DurationHash(planPath) == planSha && DurationHash(proposalsPath) == proposalsSha;
                if (nativeDurations)
                    valid = valid && durationValue.GetProperty("nativeCases").GetInt32() == 106
                        && durationValue.GetProperty("publicPlanned").GetInt32() == 92
                        && durationValue.GetProperty("constructorOnly").GetInt32() == 10
                        && durationValue.GetProperty("platformRounding").GetInt32() == 4
                        && durationValue.GetProperty("sourceBlobs").GetInt32() == 17
                        && durationValue.GetProperty("exactSourceSpans").GetInt32() == 62
                        && durationValue.GetProperty("nativeProcesses").GetInt32() == 2
                        && durationValue.GetProperty("compileRuns").GetInt32() == 2
                        && !durationValue.GetProperty("parserExecuted").GetBoolean()
                        && !durationValue.GetProperty("directiveExecuted").GetBoolean()
                        && !durationValue.GetProperty("publicProducerCompiled").GetBoolean()
                        && durationValue.GetProperty("nativeStdoutSha256").GetString() == goldenSha
                        && durationValue.GetProperty("proofZipSha256").GetString() == proofSha;
                else
                {
                    var probesPath = Path.Combine(fixtureRoot, "line-transform-duration-literals", "public-probes.ndjson");
                    var engineeringPath = Path.Combine(fixtureRoot, "line-transform-duration-literals", "engineering.ndjson");
                    valid = valid && durationValue.GetProperty("mainCases").GetInt32() == 106
                        && durationValue.GetProperty("publicBridgeCases").GetInt32() == 92
                        && durationValue.GetProperty("arrayFunctionBridgeObservations").GetInt32() == 6
                        && durationValue.GetProperty("unexecutedCases").GetInt32() == 14
                        && durationValue.GetProperty("constructorOnlyCases").GetInt32() == 10
                        && durationValue.GetProperty("roundingUnexecutedCases").GetInt32() == 4
                        && durationValue.GetProperty("proposalCompileObservations").GetInt32() == 21
                        && durationValue.GetProperty("engineeringObservations").GetInt32() == 24
                        && durationValue.GetProperty("sharedBudgetObservations").GetInt32() == 1
                        && !durationValue.GetProperty("nativeParserExecuted").GetBoolean()
                        && !durationValue.GetProperty("nativeDirectiveExecuted").GetBoolean()
                        && !durationValue.GetProperty("actualTypedObserved").GetBoolean()
                        && !durationValue.GetProperty("actualTypedValueObserved").GetBoolean()
                        && !durationValue.GetProperty("contextEquivalent").GetBoolean()
                        && !durationValue.GetProperty("normalizerOnlyQualified").GetBoolean()
                        && !durationValue.GetProperty("cacheEquivalent").GetBoolean()
                        && !durationValue.GetProperty("nativeReaderExecutedImplicitly").GetBoolean()
                        && durationValue.GetProperty("nativeGoldenSha256").GetString() == goldenSha
                        && durationValue.GetProperty("nativeProofSha256").GetString() == proofSha
                        && durationValue.GetProperty("publicPlanSha256").GetString() == planSha
                        && durationValue.GetProperty("staticProposalsSha256").GetString() == proposalsSha
                        && durationValue.GetProperty("diagnosticsSha256").GetString() == DurationHash(actual)
                        && durationValue.GetProperty("probesSha256").GetString() == DurationHash(probesPath)
                        && durationValue.GetProperty("engineeringSha256").GetString() == DurationHash(engineeringPath);
                }
                check(nativeDurations ? "line-transform-duration-literal-native-source-proof" : "line-transform-duration-literal-public-106-key-scope", valid, stdoutText);
                // 原本採取とpublic serialized観測の件数を互換成功数へ合算しない。
                Fresh(prefix + "reference-reader-receipt.json", JsonSerializer.Serialize(new { readerPid = pid, qualified, valid,
                    nativeSourceOnly = nativeDurations, mainCases = 106, publicBridges = 92, unexecutedCases = 14,
                    separateProposalCompileObservations = 21, engineeringObservations = 24, sharedBudgetObservations = 1,
                    nativeParserExecuted = false, nativeDirectiveExecuted = false, nativeTypedValueEquivalent = false,
                    normalizerOnlyQualified = false, cacheEquivalent = false, sourceCounterRowCountEquivalent = false,
                    actual, processMetadata = prefix + "reference-reader-process.json", scope = "line-transform-only" }));
                if (!valid) throw new IOException("Duration literal reader did not qualify source/public scope.");
                continue;
            }
            if (sizes)
            {
                var summaries = stdoutText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (summaries.Length != 1) throw new IOException("Size literal reader requires one complete JSON summary.");
                using var sizeSummary = JsonDocument.Parse(summaries[0]);
                var sizeValue = sizeSummary.RootElement;
                var valid = sizeValue.GetProperty("passed").GetBoolean();
                if (nativeSizes)
                    valid = valid && sizeValue.GetProperty("nativeCases").GetInt32() == 95
                        && sizeValue.GetProperty("publicPlanned").GetInt32() == 77
                        && sizeValue.GetProperty("constructorOnly").GetInt32() == 14
                        && sizeValue.GetProperty("platformRounding").GetInt32() == 4
                        && sizeValue.GetProperty("sourceBlobs").GetInt32() == 17
                        && sizeValue.GetProperty("exactSourceSpans").GetInt32() == 62
                        && sizeValue.GetProperty("nativeProcesses").GetInt32() == 2
                        && sizeValue.GetProperty("compileRuns").GetInt32() == 2
                        && !sizeValue.GetProperty("parserExecuted").GetBoolean()
                        && !sizeValue.GetProperty("directiveExecuted").GetBoolean()
                        && !sizeValue.GetProperty("publicProducerCompiled").GetBoolean()
                        && sizeValue.GetProperty("nativeStdoutSha256").GetString() == "abe36d531ca546c5683a153b36be4e1f5ed31f971b9b3c322f535cf55e1e3fe8"
                        && sizeValue.GetProperty("proofZipSha256").GetString() == "e8f149d4e038ce36ea1e425d6c69c7862e0dee2bb0cb52990547d545f7588b0a";
                else
                {
                    var probesPath = Path.Combine(fixtureRoot, "line-transform-size-literals", "public-probes.ndjson");
                    var planPath = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformSizeLiterals", "public-case-plan.json");
                    var probePlanPath = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformSizeLiterals", "public-probes-and-boundaries.json");
                    valid = valid && sizeValue.GetProperty("mainCases").GetInt32() == 95
                        && sizeValue.GetProperty("bridgeCases").GetInt32() == 77
                        && sizeValue.GetProperty("unexecutedCases").GetInt32() == 18
                        && sizeValue.GetProperty("constructorOnlyCases").GetInt32() == 14
                        && sizeValue.GetProperty("platformRoundingCases").GetInt32() == 4
                        && sizeValue.GetProperty("additionalDirectCases").GetInt32() == 2
                        && sizeValue.GetProperty("tokenProbeCases").GetInt32() == 14
                        && sizeValue.GetProperty("tokenCompileSuccessCases").GetInt32() == 3
                        && sizeValue.GetProperty("tokenRejectCases").GetInt32() == 11
                        && sizeValue.GetProperty("ubRejectCases").GetInt32() == 2
                        && !sizeValue.GetProperty("actualTypedValueObserved").GetBoolean()
                        && !sizeValue.GetProperty("contextEquivalent").GetBoolean()
                        && !sizeValue.GetProperty("nativeParserExecuted").GetBoolean()
                        && !sizeValue.GetProperty("nativeDirectiveExecuted").GetBoolean()
                        && sizeValue.GetProperty("nativeGoldenSha256").GetString() == "abe36d531ca546c5683a153b36be4e1f5ed31f971b9b3c322f535cf55e1e3fe8"
                        && sizeValue.GetProperty("actualSha256").GetString() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(actual))).ToLowerInvariant()
                        && sizeValue.GetProperty("probesSha256").GetString() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(probesPath))).ToLowerInvariant()
                        && sizeValue.GetProperty("publicPlanSha256").GetString() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(planPath))).ToLowerInvariant()
                        && sizeValue.GetProperty("probePlanSha256").GetString() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(probePlanPath))).ToLowerInvariant();
                }
                check(nativeSizes ? "line-transform-size-literal-native-source-proof" : "line-transform-size-literal-public-95-key-scope", valid, stdoutText);
                Fresh(prefix + "reference-reader-receipt.json", JsonSerializer.Serialize(new { readerPid = pid, qualified, valid, nativeSourceOnly = nativeSizes,
                    mainCases = 95, publicBridges = 77, unexecutedCases = 18, separatePublicProbes = 18, nativeTypedValueEquivalent = false,
                    actual, processMetadata = prefix + "reference-reader-process.json", scope = "line-transform-only" }));
                if (!valid) throw new IOException("Size literal reader did not qualify source/public scope.");
                continue;
            }
            if (aggregates)
            {
                var summaries = stdoutText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (summaries.Length != 1) throw new IOException("Aggregate reader requires one complete JSON summary per process.");
                using var aggregateSummary = JsonDocument.Parse(summaries[0]);
                var aggregateValue = aggregateSummary.RootElement;
                var valid = aggregateValue.GetProperty("passed").GetBoolean();
                if (nativeAggregates)
                    valid = valid && aggregateValue.GetProperty("caseCount").GetInt32() == 141
                        && aggregateValue.GetProperty("sourceGitBlobs").GetInt32() == 15
                        && aggregateValue.GetProperty("exactSourceSpans").GetInt32() == 53
                        && aggregateValue.GetProperty("nativeProcesses").GetInt32() == 2
                        && aggregateValue.GetProperty("nativeCompileRuns").GetInt32() == 2
                        && aggregateValue.GetProperty("actualComparisonCount").GetInt32() == 0
                        && aggregateValue.GetProperty("nativeStdoutSha256").GetString() == "74e1f5b6bb58af0433cb0d198ee3360da1ebcd1f8845da9b5ce2fd0e2f549e91"
                        && aggregateValue.GetProperty("proofZipSha256").GetString() == "039e9b4e77d8164b4e97bb726350675a4d8c6de2d73fe8daa1a3de03cdca7d4f";
                else
                {
                    var planPath = Path.Combine(repository.FullName, "tests", "Fixtures", "LineTransformAggregates", "public-case-plan.json");
                    var probesPath = Path.Combine(fixtureRoot, "line-transform-aggregates", "arity-probes.ndjson");
                    valid = valid && aggregateValue.GetProperty("mainCases").GetInt32() == 141
                        && aggregateValue.GetProperty("bridgeCases").GetInt32() == 104
                        && aggregateValue.GetProperty("unrepresentedCases").GetInt32() == 37
                        && aggregateValue.GetProperty("separateProbes").GetInt32() == 8
                        && !aggregateValue.GetProperty("actualTypedValueObserved").GetBoolean()
                        && !aggregateValue.GetProperty("contextEquivalent").GetBoolean()
                        && aggregateValue.GetProperty("originalNativeBitsReferenceOnly").GetBoolean()
                        && aggregateValue.GetProperty("nativeGoldenSha256").GetString() == "74e1f5b6bb58af0433cb0d198ee3360da1ebcd1f8845da9b5ce2fd0e2f549e91"
                        && aggregateValue.GetProperty("actualSha256").GetString() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(actual))).ToLowerInvariant()
                        && aggregateValue.GetProperty("publicPlanSha256").GetString() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(planPath))).ToLowerInvariant()
                        && aggregateValue.GetProperty("probesSha256").GetString() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(probesPath))).ToLowerInvariant();
                }
                check(nativeAggregates ? "line-transform-aggregate-native-source-proof" : "line-transform-aggregate-public-141-key-scope", valid, stdoutText);
                Fresh(prefix + "reference-reader-receipt.json", JsonSerializer.Serialize(new { readerPid = pid, qualified, valid, nativeSourceOnly = nativeAggregates,
                    mainCases = 141, publicBridges = 104, unrepresentedNotExecuted = 37, separateArityProbes = 8, nativeTypedValueEquivalent = false,
                    actual, processMetadata = prefix + "reference-reader-process.json", scope = "line-transform-only" }));
                if (!valid) throw new IOException("Aggregate reader did not qualify its source/public scope.");
                continue;
            }
            if (within)
            {
                var summaries = stdoutText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (summaries.Length != 2) throw new IOException("IsWithin reader requires both complete JSON summaries.");
                using var sourceSummary = JsonDocument.Parse(summaries[0]);
                using var actualSummary = JsonDocument.Parse(summaries[1]);
                var sourceValue = sourceSummary.RootElement; var actualValue = actualSummary.RootElement;
                var counts = actualValue.GetProperty("counts");
                var fixedWithin = sourceValue.GetProperty("passed").GetBoolean()
                    && sourceValue.GetProperty("checks").GetInt32() == 200
                    && sourceValue.GetProperty("caseCount").GetInt32() == 72
                    && sourceValue.GetProperty("nativeAndActualSerializedSha256").GetString() == "c7348fc20d2e5611451a96c2f76c884702917bbfbf15948c1bf80a8f7b892d5d"
                    && sourceValue.GetProperty("publicPlanSha256").GetString() == "3325a94aae0cd10210ba30a7e422e59848a41078596757262b7f583e0d19a00d"
                    && sourceValue.GetProperty("publicSerializedEquivalenceCandidates").GetInt32() == 59
                    && sourceValue.GetProperty("publicConstructorRejections").GetInt32() == 4
                    && actualValue.GetProperty("passed").GetBoolean()
                    && counts.EnumerateObject().Count() == 4
                    && counts.GetProperty("serialized-value-equivalence").GetInt32() == 59
                    && counts.GetProperty("compile-constructor-rejection").GetInt32() == 3
                    && counts.GetProperty("unrepresentable-not-executed").GetInt32() == 10
                    && counts.GetProperty("value-only-bridge").GetInt32() == 12
                    && actualValue.GetProperty("separateProbeCount").GetInt32() == 1
                    && actualValue.GetProperty("actualSha256").GetString() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(actual))).ToLowerInvariant()
                    && actualValue.GetProperty("publicPlanSha256").GetString() == "3325a94aae0cd10210ba30a7e422e59848a41078596757262b7f583e0d19a00d";
                check("line-transform-within-independent-fixed-72-public-plan", fixedWithin, stdoutText);
                Fresh("iswithin-reference-reader-receipt.json", JsonSerializer.Serialize(new { readerPid = pid, qualified,
                    fixedCases = fixedWithin, originalSourceChecks = 200, matched = 59, rejected = 3,
                    unrepresentableNotExecuted = 10, valueOnlyBridges = 12, separateConstructorProbes = 1,
                    actual, processMetadata = "iswithin-reference-reader-process.json", scope = "line-transform-only",
                    executionQualification = "Real App self-test diagnostics and strict reader executed/publicExecution/compatibilityScope guards; no legacy-projection adapter used" }));
                if (!fixedWithin) throw new IOException("IsWithin reader did not qualify original source and public diagnostics.");
                continue;
            }
            if (ranges)
            {
                // 原本readerと公開診断readerは、それぞれ一行のJSON集計を出す。
                var summaries = stdoutText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (summaries.Length != 2) throw new IOException("InRange reader requires both complete JSON summaries.");
                using var sourceSummary = JsonDocument.Parse(summaries[0]);
                using var actualSummary = JsonDocument.Parse(summaries[1]);
                var sourceValue = sourceSummary.RootElement; var actualValue = actualSummary.RootElement;
                var fixedRange = sourceValue.GetProperty("passed").GetBoolean() && sourceValue.GetProperty("checks").GetInt32() == 162
                    && sourceValue.GetProperty("caseCount").GetInt32() == 72
                    && sourceValue.GetProperty("nativeAndActualSerializedSha256").GetString() == "43c68c5c42f7bcce3fa508727759d5f43e7e938fe722a4108ff3151e9a3055d6"
                    && sourceValue.GetProperty("publicPlanSha256").GetString() == "f201d12937d34271ec05e851830c641110b941ae469542ec442a0ad6a8083f1d"
                    && sourceValue.GetProperty("publicSerializedEquivalenceCandidates").GetInt32() == 59
                    && sourceValue.GetProperty("publicConstructorRejections").GetInt32() == 4
                    && actualValue.GetProperty("passed").GetBoolean() && actualValue.GetProperty("normativeMainCases").GetInt32() == 72
                    && actualValue.GetProperty("separateConstructorProbes").GetInt32() == 1
                    && actualValue.GetProperty("serializedKeys").GetInt32() == 59
                    && actualValue.GetProperty("compileArityRejections").GetInt32() == 3
                    && actualValue.GetProperty("unrepresentable").GetInt32() == 10 && !actualValue.GetProperty("legacyProof").GetBoolean();
                check("line-transform-range-independent-fixed-72-public-plan", fixedRange, stdoutText);
                Fresh("inrange-reference-reader-receipt.json", JsonSerializer.Serialize(new { readerPid = pid, qualified, fixedCases = fixedRange,
                    matched = 59, rejected = 3, unrepresentableNotExecuted = 10, separateConstructorProbes = 1, actual,
                    processMetadata = "inrange-reference-reader-process.json", scope = "line-transform-only" }));
                if (!fixedRange) throw new IOException("InRange reader did not qualify both original and public diagnostic contracts.");
                continue;
            }
            using var summary = JsonDocument.Parse(stdoutText);
            var value = summary.RootElement;
            if (replace)
            {
                var qualification = value.GetProperty("actual");
                var fixedReplace = value.GetProperty("caseCount").GetInt32() == 100
                    && value.GetProperty("sourceGitBlobs").GetInt32() == 53 && value.GetProperty("exactExtractions").GetInt32() == 31
                    && value.GetProperty("zipEntries").GetInt32() == 90
                    && qualification.GetProperty("matched").GetInt32() == 78
                    && qualification.GetProperty("rejected").GetInt32() == 10
                    && qualification.GetProperty("unrepresentableNotExecuted").GetInt32() == 12;
                check("line-transform-replace-independent-fixed-100-public-plan", fixedReplace, stdoutText);
                Fresh("replace-reference-reader-receipt.json", JsonSerializer.Serialize(new { readerPid = pid, qualified, fixedCases = fixedReplace,
                    matched = 78, rejected = 10, unrepresentableNotExecuted = 12, actual,
                    processMetadata = "replace-reference-reader-process.json", scope = "line-transform-only" }));
                if (!fixedReplace) throw new IOException("Replace reader did not qualify fixed public contexts.");
                continue;
            }
            var fixedCases = search
                ? value.GetProperty("fixtureCases").GetInt32() == 68 && value.GetProperty("publicPlan").GetBoolean()
                    && value.GetProperty("oracleMatchedCases").GetInt32() == 62 && value.GetProperty("publicConstructorCases").GetInt32() == 6
                    && value.GetProperty("publicBoundaryCases").GetInt32() == 6 && value.GetProperty("actualChecks").GetInt32() == 370
                : arrays
                ? value.GetProperty("fixtureCases").GetInt32() == 90 && value.GetProperty("publicPlan").GetBoolean()
                    && value.GetProperty("oracleMatchedCases").GetInt32() == 89 && value.GetProperty("publicBoundaryCases").GetInt32() == 1
                    && value.GetProperty("actualChecks").GetInt32() > 0
                : value.GetProperty("fixtureCases").GetInt32() == 86 && value.GetProperty("okCases").GetInt32() == 73
                    && value.GetProperty("errorCases").GetInt32() == 13 && value.GetProperty("actualChecks").GetInt32() > 0;
            check(search ? "line-transform-search-independent-fixed-68-public-plan" : arrays ? "line-transform-array-independent-fixed-90-public-plan" : "line-transform-independent-fixed-86-cases", fixedCases, stdoutText);
            Fresh((search ? "search-" : arrays ? "array-" : "") + "reference-reader-receipt.json", JsonSerializer.Serialize(new { readerPid = pid, qualified, fixedCases,
                fixtureCases = value.GetProperty("fixtureCases").GetInt32(), okCases = value.GetProperty("okCases").GetInt32(),
                errorCases = value.GetProperty("errorCases").GetInt32(), actualChecks = value.GetProperty("actualChecks").GetInt32(),
                actual, processMetadata = (search ? "search-" : arrays ? "array-" : "") + "reference-reader-process.json", scope = "line-transform-only" }));
            if (!fixedCases) throw new IOException("Line transform reader did not qualify all fixed product cases.");
        }

        void Within(string path, string root)
        {
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
                throw new IOException("Line transform GUI path escapes task output.");
            NoLinks(path);
        }
        static void NoLinks(string path)
        {
            for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Line transform output cannot traverse links: " + current);
        }
        void Fresh(string name, string content)
        {
            var path = Path.Combine(gui, name); Within(path, gui);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(content);
        }
    }

    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var directory = Path.Combine(fixtures, "line-transform");
        Directory.CreateDirectory(directory);
        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var plain = await Create("plain", "old\r\n日本語\nlast\r", "new\r\n日本語\nlast\r");
        await Compare("plain-whole-line", plain, false, true, ["--line-transform", "all", "ColumnRange(\"1\")"]);
        await Compare("plain-empty-key", plain, false, false, ["--line-transform", "all", "ColumnRange(\"!1\")"], ignored: true);
        await Compare("identity-unicode-bom", plain, false, true, ["--line-transform", "all", "Line"]);
        var empty = await Create("empty", "", "");
        await Compare("empty", empty, false, false, ["--line-transform", "all", "Line"]);

        // 固定86件の採取範囲外。旧FilterExpressionNodes.cpp:586-604の型分岐と
        // boolだけを真偽条件にするIfの原典契約に基づく、実CLIの独立した回帰。
        foreach (var (name, expression, different) in new[]
        {
            ("double-string-inequality-none", "If(1.0 != \"x\", \"same\", Line)", true),
            ("double-bool-inequality-none", "If(1.0 != true, \"same\", Line)", true),
            ("double-missing-line-inequality-none", "If(1.0 != LineAt(0), \"same\", Line)", true),
            ("double-integer-equality", "If(1.0 = 1, \"same\", Line)", false),
            ("double-integer-inequality", "If(1.0 != 2, \"same\", Line)", false),
            ("double-string-plus-fixed-six", "If(1.0 + \"x\" = \"1.000000x\", \"same\", Line)", false),
            ("double-string-inequality-noopt", "@noopt If(1.0 != \"x\", \"same\", Line)", true)
        })
            await Compare("numeric-" + name, plain, false, different, ["--line-transform", "all", expression], ignored: !different);

        var columns = await Create("columns", "A,old,one\r\nB,old,two\r\n", "A,new,one\r\nB,new,two\r\n");
        foreach (var (name, range) in new[] { ("include", "1,3"), ("exclude", "!2"), ("order", "3,1"),
            ("deduplicate", "1,1,3,1"), ("priority", "1-3,!2"), ("open-end", "3-,1") })
            await Compare(name, columns, true, false, ["--line-transform", "all", $"ColumnRange(\"{range}\")"]);
        var ordered = await Create("ordered", "a,b,c\r\n", "c,a\r\n");
        await Compare("selection-order-and-dedup-exact", ordered, true, false,
            ["--line-transform", "left", "ColumnRange(\"3,1,3,1\")"]);
        var excludedExact = await Create("excluded-exact", "a,b,c\r\n", "c\r\n");
        await Compare("exclusion-wins-exact", excludedExact, true, false,
            ["--line-transform", "left", "ColumnRange(\"3,1,!1\")"]);
        var dynamicColumn = await Create("dynamic-column", "2,b,c\r\n", "b\r\n");
        await Compare("dynamic-column-value", dynamicColumn, true, false,
            ["--line-transform", "left", "ColumnRange(Column1)"]);
        await Compare("dynamic-line-number", columns, true, false,
            ["--line-transform", "all", "ColumnRange(If(LineNumber = 1, \"1,3\", \"!2\"))"]);
        await Compare("dynamic-column-count", columns, true, false,
            ["--line-transform", "all", "ColumnRange(If(ColumnCount = 3, \"1,3\", \"2\"))"]);
        await Compare("dynamic-untaken-invalid", columns, true, false,
            ["--line-transform", "all", "ColumnRange(If(true, \"1,3\", \"0\"))"]);
        var quoted = await Create("quoted", "\"A,\"\"quoted\"\"\",old,\"one\r\nwrapped\"\r\n", "\"A,\"\"quoted\"\"\",new,\"one\r\nwrapped\"\r\n");
        await Compare("quoted-raw-and-crlf", quoted, true, false, ["--line-transform", "all", "ColumnRange(\"3,1\")"]);
        var quoteOnly = await Create("quote-only", "\"a\",b\r\n", "a,b\r\n");
        await Compare("two-pane-base-rule-is-inactive", quoteOnly, true, false, ["--line-transform", "base", "Line"]);
        await Compare("two-pane-base-rule-preserves-static-key", quoteOnly, true, false,
            ["--line-transform", "base", "\"constant\"", "--table-column-range", "1"]);
        await Reject("inactive-mask-invalid-expression", quoteOnly, ["--line-transform", "base", "ColumnRange(\"0\")"]);
        await Compare("raw-quote-only-meaningful", quoteOnly, true, true, ["--line-transform", "all", "Line"]);
        await Compare("static-range-keeps-raw-differences", columns, true, true, ["--table-column-range", "1,3"]);
        await StaticPaneAndRowClassification();
        await GeneralRowIgnoreCases();
        await Compare("general-static-residual", columns, true, true,
            ["--line-transform", "all", "ColumnRange(\"2\")", "--table-column-range", "1,3"]);
        var residual = await Create("residual", "a,old\r\nb,old\r\n", "a,new\r\nc,new\r\n");
        await Compare("residual-block-remains-meaningful", residual, true, true, ["--line-transform", "all", "ColumnRange(\"1\")"]);
        await Compare("left-scope", columns, true, true, ["--line-transform", "left", "ColumnRange(\"1\")"]);
        await Compare("right-scope", columns, true, true, ["--line-transform", "right", "ColumnRange(\"1\")"]);
        var prefix = await Create("prefix", "A,old\r\n", "A\r\n");
        await Compare("single-provider-prefix-is-not-mask", prefix, true, false, ["--line-transform", "left", "RightColumnRange(\"1\")"]);

        var group = await Create("groups", "a\nb\n", "z\nb\n");
        const string first = "If(LineNumber = 1, \"changed1\", \"changed2\")";
        const string second = "If(LineNumber = 1, LineAt(2), LineAt(1))";
        await Compare("same-mask-line-outer", group, false, false,
            ["--line-transform", "all", first, "--line-transform", "all", second], ignored: true);
        await Compare("mask-boundary-group-outer", group, false, true,
            ["--line-transform", "left", first, "--line-transform", "all", second]);
        var previous = await Create("previous", "a\nb\nc\n", "changed\nchanged\nchanged\n");
        await Compare("previous-sees-transformed-line", previous, false, false,
            ["--line-transform", "left", "If(LineNumber = 1, \"changed\", LineAt(1))"], ignored: true);
        var next = await Create("next", "a\nb\nc\n", "b\nc\nnone\n");
        await Compare("next-sees-original-line", next, false, false,
            ["--line-transform", "left", "LineOffsetAt(1)"], ignored: true);

        var three = await Create("three", "A,left\r\n", "A,base\r\n", "A,right\r\n");
        await Compare("three-all", three, true, false, ["--line-transform", "all", "ColumnRange(\"1\")"]);
        await Compare("three-base-scope", three, true, true, ["--line-transform", "base", "ColumnRange(\"1\")"]);
        foreach (var (pair, firstText, secondText, scope, expression, different) in new[]
        {
            ("left-middle", "A,left\r\n", "A,base\r\n", "right", "\"unused\"", true),
            ("middle-right", "A,base\r\n", "A,right\r\n", "base", "\"A,right\"", false),
            ("left-right", "A,left\r\n", "A,right\r\n", "left", "\"A,right\"", false)
        })
        {
            var result = await run("line-transform-independent-" + pair, different ? 1 : 0, true,
                ["--compare", three.Paths[0], three.Paths[2], "--middle", three.Paths[1], "--independent-text", "--pair", pair,
                    "--line-transform", scope, expression]);
            using var json = Parse(result);
            var expectedPair = pair switch { "left-middle" => "LeftMiddle", "middle-right" => "MiddleRight", "left-right" => "LeftRight", _ => throw new InvalidOperationException() };
            if (json is not null)
                check(result.Name + "-physical-panes-and-originals", json.RootElement.GetProperty("pair").GetString() == expectedPair
                    && json.RootElement.GetProperty("different").GetBoolean() == different
                    && json.RootElement.GetProperty("firstText").GetString() == firstText
                    && json.RootElement.GetProperty("secondText").GetString() == secondText, "Actual pair, original text and classification.");
        }
        var normalize = await Create("normalize", "old // left\n", "new // right\n");
        await Compare("comments-substitute-before-general", normalize, false, false,
            ["--comments", "cstyle", "--substitute", "old|new", "same", "--line-transform", "all", "Line"], ignored: true);
        await Compare("ignore-before-general", normalize, false, false,
            ["--ignore-regex", ".*", "--line-transform", "all", "Line"], ignored: true);

        foreach (var (name, expression) in new[]
        {
            ("non-string-range", "ColumnRange(1)"), ("literal-bad-untaken", "If(true, Line, ColumnRange(\"0\"))"),
            ("dynamic-bad-taken", "ColumnRange(If(false, \"1\", \"0\"))"), ("unknown-function", "Unknown(Line)"),
            ("unknown-field", "UnknownField"), ("non-ascii-identifier", "列番号"), ("empty-expression", ""),
            ("generated-crlf", "\"a\r\nb\""), ("depth-limit", new string('-', 140) + "1")
        })
            await Reject(name, columns, ["--line-transform", "all", expression]);
        foreach (var mask in new[] { "0", "8", "middle", "unknown" })
            await Reject("invalid-mask-" + mask, columns, ["--line-transform", mask, "Line"]);
        await Reject("rule-count-limit", columns, Enumerable.Range(0, 257).SelectMany(_ => new[] { "--line-transform", "all", "Line" }).ToArray());

        await ProjectPaths(columns);
        foreach (var original in originals)
        {
            var exists = File.Exists(original.Key);
            var currentBytes = exists ? await File.ReadAllBytesAsync(original.Key) : [];
            check("line-transform-final-source-sha-" + Path.GetFileName(original.Key),
                exists && SHA256.HashData(original.Value).SequenceEqual(SHA256.HashData(currentBytes)),
                original.Key);
        }

        async Task<Inputs> Create(string name, params string[] text)
        {
            var paths = text.Length == 3 ? new[] { "left", "base", "right" } : ["left", "right"];
            var bytes = new byte[text.Length][];
            for (var side = 0; side < text.Length; side++)
            {
                paths[side] = Path.Combine(directory, name + "-" + paths[side] + ".txt");
                bytes[side] = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text[side])).ToArray();
                await File.WriteAllBytesAsync(paths[side], bytes[side]);
                originals.Add(paths[side], bytes[side]);
            }
            return new(paths, text, bytes);
        }

        async Task Compare(string name, Inputs input, bool table, bool different, string[] options, bool ignored = false)
        {
            var args = new List<string> { table ? "--table" : "--compare", input.Paths[0], input.Paths[^1] };
            if (input.Paths.Length == 3) args.AddRange(["--base", input.Paths[1]]);
            args.AddRange(options);
            var result = await run("line-transform-" + name, different ? 1 : 0, true, args.ToArray());
            using var json = Parse(result);
            if (json is null) return;
            var root = json.RootElement;
            check(result.Name + "-classification", result.ExitCode == (different ? 1 : 0)
                && root.GetProperty("different").GetBoolean() == different, "Expected different=" + different);
            if (table)
            {
                var sides = input.Paths.Length == 3 ? new[] { "left", "base", "right" } : ["left", "right"];
                for (var side = 0; side < sides.Length; side++)
                {
                    var actual = root.GetProperty("mapping").EnumerateArray().Select(row => row.GetProperty(sides[side]))
                        .Where(value => value.ValueKind != JsonValueKind.Null).Select(value => value.GetInt32()).ToArray();
                    // 物理改行を含む引用符内フィールドを除外して、論理行の全対応を検査する。
                    var expected = Enumerable.Range(1, LogicalRows(input.Texts[side])).ToArray();
                    check(result.Name + "-source-row-order-" + sides[side], actual.SequenceEqual(expected), string.Join(',', actual));
                }
            }
            else
            {
                var rows = root.GetProperty("rows").EnumerateArray().ToArray();
                foreach (var (side, source) in new[] { ("left", input.Texts[0]), ("right", input.Texts[^1]) })
                {
                    var originalLines = PhysicalLines(source);
                    var present = rows.Where(row => row.TryGetProperty(side + "Line", out _)).ToArray();
                    check(result.Name + "-original-rows-" + side, present.Select(row => row.GetProperty(side + "Line").GetInt32())
                        .SequenceEqual(Enumerable.Range(1, originalLines.Length)) && present.Select(row => row.GetProperty(side).GetString())
                        .SequenceEqual(originalLines), "All original contents and line numbers, once in order.");
                }
                if (ignored) check(result.Name + "-ignored-row", rows.Any(row => row.GetProperty("ignored").GetBoolean()), "Raw differences remain visible as ignored rows.");
            }
        }

        async Task Reject(string name, Inputs input, string[] options)
        {
            var result = await run("line-transform-reject-" + name, 2, false,
                new[] { "--table", input.Paths[0], input.Paths[^1] }.Concat(options).ToArray());
            check(result.Name + "-explicit-error", result.ExitCode == 2 && !string.IsNullOrWhiteSpace(result.Stderr)
                && string.IsNullOrWhiteSpace(result.Stdout), "Rejected atomically, without comparison JSON.");
        }

        async Task GeneralRowIgnoreCases()
        {
            var regexInput = await Create("table-ignore-regex", "A,old\r\n", "A,new\r\n");
            await Compare("table-ignore-regex-baseline", regexInput, true, false, ["--ignore-regex", ".*"]);
            await Compare("table-ignore-regex-noop-general", regexInput, true, false,
                ["--ignore-regex", ".*", "--line-transform", "all", "Line"]);
            var blankInput = await Create("table-ignore-blank", " \r\n", "\t\r\n");
            await Compare("table-ignore-blank-baseline", blankInput, true, false, ["--ignore-blank"]);
            await Compare("table-ignore-blank-noop-general", blankInput, true, false,
                ["--ignore-blank", "--line-transform", "all", "Line"]);
            await Compare("table-regex-transform-boundary-control", regexInput, true, true,
                ["--ignore-regex", "^mask:", "--line-transform", "all", "Line"]);
            await Compare("table-regex-matches-transformed-raw", regexInput, true, false,
                ["--ignore-regex", "^mask:", "--line-transform", "all", "\"mask:\" + Line"]);

            foreach (var (name, input, expression, pattern, ignoreBlank, expectedCells) in new[]
            {
                ("ignore-regex-noop", regexInput, "Line", (string?)".*", false, new[] { "A", "old", "A", "new" }),
                ("ignore-blank-noop", blankInput, "Line", (string?)null, true, new[] { " ", "\t" }),
                ("ignore-regex-transformed-raw", regexInput, "\"mask:\" + Line", (string?)"^mask:", false, new[] { "A", "old", "A", "new" })
            })
            {
                var project = Path.Combine(directory, name + "-project.json");
                await File.WriteAllBytesAsync(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Table",
                    leftPath = input.Paths[0], rightPath = input.Paths[1], tableDelimiter = ',', ignoreLinePattern = pattern,
                    ignoreBlankLines = ignoreBlank, lineTransformRules = new[] { new { expression, panes = 7 } } }));
                originals.Add(project, await File.ReadAllBytesAsync(project));
                var htmlPath = Path.Combine(directory, name + "-report.html");
                await run("line-transform-table-" + name + "-report", 0, true, ["--report-project", project, htmlPath]);
                if (!File.Exists(htmlPath)) { check(name + "-html-exists", false, htmlPath); continue; }
                var html = await File.ReadAllTextAsync(htmlPath);
                var cells = Regex.Matches(html, "<td\\b[^>]*><pre>(.*?)</pre></td>", RegexOptions.Singleline).ToArray();
                check("line-transform-table-" + name + "-original-cells-and-classification", html.Contains("data-different=\"false\"", StringComparison.Ordinal)
                    && cells.Length == expectedCells.Length && cells.All(cell => cell.Value.Contains("class=\"Equal\"", StringComparison.Ordinal))
                    && cells.Select(cell => cell.Groups[1].Value).SequenceEqual(expectedCells), "Ignored rows retain every original cell and Equal cell classification.");
            }
        }

        async Task StaticPaneAndRowClassification()
        {
            // 静的列選択は現行のalignment契約。原典86件のC++採取とは別の回帰。
            var rightScope = await Create("static-right-scope", "A\r\nB\r\n", "A,x\r\nB,y\r\n");
            foreach (var (name, options, fallback) in new[]
            {
                ("static-right-zero-budget", new[] { "--table-column-range", "1:right", "--max-work", "0" }, false),
                // 一般キーは原rawのまま異なるため予算0の退避を記録し、静的列対応はその残差だけを補う。
                ("general-static-right-residual", new[] { "--table-column-range", "1:right", "--max-work", "0", "--line-transform", "all", "Line" }, true)
            })
            {
                await Compare(name, rightScope, true, true, options);
                await AlignmentEvidence(name, rightScope, options, fallback);
            }
            var baseScope = await Create("static-base-unused", "A,q\r\nB,r\r\n", "A,x\r\nB,y\r\n");
            foreach (var (name, options) in new[]
            {
                ("static-left-no-right-rule", new[] { "--table-column-range", "1:left", "--max-work", "0" }),
                ("static-unused-base-does-not-target-right", new[] { "--table-column-range", "1:left", "--table-column-range", "1:base", "--max-work", "0" })
            })
                await AlignmentEvidence(name, baseScope, options, true);
            var staticProject = Path.Combine(directory, "static-right-project.json");
            await File.WriteAllBytesAsync(staticProject, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Table",
                leftPath = rightScope.Paths[0], rightPath = rightScope.Paths[1], tableDelimiter = ',',
                tableColumnRangeRules = new[] { new { specification = "1", panes = 4 } } }));
            originals.Add(staticProject, await File.ReadAllBytesAsync(staticProject));
            var staticHtml = Path.Combine(directory, "static-right-report.html");
            await run("line-transform-static-right-original-cells", 0, true, ["--report-project", staticProject, staticHtml]);
            check("line-transform-static-right-report-original-cells", File.Exists(staticHtml)
                && new[] { "A", "B", "x", "y" }.All(text => File.ReadAllText(staticHtml).Contains("<pre>" + text + "</pre>", StringComparison.Ordinal)),
                "Static alignment preserves every original cell.");

            var rowOnly = await Create("row-only", "a,b\r\n", "a,b\r\n");
            var ignoredRows = await Create("row-ignored", "a,old\r\n", "a,new\r\n");
            foreach (var (name, input, expression, scope, different, rowKind, ignored) in new[]
            {
                ("row-only-modified", rowOnly, "\"x\"", "left", true, "Modified", false),
                ("row-key-equal-ignored", ignoredRows, "ColumnRange(\"1\")", "all", false, "Equal", true)
            })
            {
                var result = await run("line-transform-" + name, different ? 1 : 0, true,
                    ["--table", input.Paths[0], input.Paths[1], "--line-transform", scope, expression]);
                using var json = Parse(result);
                if (json is not null)
                {
                    var root = json.RootElement; var mapping = root.GetProperty("mapping").EnumerateArray().ToArray();
                    check(result.Name + "-row-kind", root.GetProperty("different").GetBoolean() == different && mapping.Length == 1
                        && mapping[0].GetProperty("left").GetInt32() == 1 && mapping[0].GetProperty("right").GetInt32() == 1
                        && mapping[0].GetProperty("leftKind").GetString() == rowKind && mapping[0].GetProperty("rightKind").GetString() == rowKind,
                        "Row classification is independent of unchanged decoded cells.");
                }
                var project = Path.Combine(directory, name + "-project.json");
                await File.WriteAllBytesAsync(project, JsonSerializer.SerializeToUtf8Bytes(new { mode = "Table", leftPath = input.Paths[0],
                    rightPath = input.Paths[1], tableDelimiter = ',', lineTransformRules = new[] { new { expression, panes = scope == "left" ? 1 : 7 } } }));
                originals.Add(project, await File.ReadAllBytesAsync(project));
                var htmlPath = Path.Combine(directory, name + ".html");
                await run("line-transform-" + name + "-report", 0, true, ["--report-project", project, htmlPath]);
                if (!File.Exists(htmlPath)) { check(name + "-html-exists", false, htmlPath); continue; }
                var html = await File.ReadAllTextAsync(htmlPath);
                var headers = Regex.Matches(html, "<th\\b[^>]*scope=\"row\"[^>]*>").Select(match => match.Value).ToArray();
                var cells = Regex.Matches(html, "<td\\b[^>]*><pre>(.*?)</pre></td>", RegexOptions.Singleline).ToArray();
                check(name + "-html-row-header-classification", headers.Length == 2 && headers.All(header =>
                    header.Contains("class=\"row-number " + rowKind + "\"", StringComparison.Ordinal)
                    && header.Contains("data-row-kind=\"" + rowKind + "\"", StringComparison.Ordinal)
                    && header.Contains("data-ignored=\"" + (ignored ? "true" : "false") + "\"", StringComparison.Ordinal))
                    && html.Contains("data-different=\"" + (different ? "true" : "false") + "\"", StringComparison.Ordinal),
                    "Both row headers carry meaningful/ignored classification.");
                check(name + "-html-cells-remain-original-and-equal", cells.Length == 4
                    && cells.All(cell => cell.Value.Contains("class=\"Equal\"", StringComparison.Ordinal))
                    && cells.Select(cell => cell.Groups[1].Value).SequenceEqual(ignored ? new[] { "a", "old", "a", "new" } : ["a", "b", "a", "b"]),
                    "Cell colors stay Equal and original raw values remain visible.");
            }

            async Task AlignmentEvidence(string name, Inputs input, string[] options, bool fallback)
            {
                var result = await run("line-transform-" + name + "-alignment", 1, true,
                    new[] { "--table", input.Paths[0], input.Paths[1] }.Concat(options).ToArray());
                using var json = Parse(result);
                if (json is null) return;
                var root = json.RootElement; var rows = root.GetProperty("mapping").EnumerateArray().ToArray();
                check(result.Name + "-alignment-contract", root.GetProperty("different").GetBoolean()
                    && root.GetProperty("alignmentFallback").GetBoolean() == fallback && root.GetProperty("alignmentWorkUsed").GetInt64() == 0
                    && (!fallback || root.GetProperty("alignmentFallbackReason").GetString() == "work-limit:gnu-input")
                    && rows.Length == 2 && rows.Select(row => row.GetProperty("left").GetInt32()).SequenceEqual(new[] { 1, 2 })
                    && rows.Select(row => row.GetProperty("right").GetInt32()).SequenceEqual(new[] { 1, 2 }),
                    "Zero work; expected fallback=" + fallback + "; full original row mapping.");
            }
        }

        JsonDocument? Parse(CommandResult result)
        {
            try { return JsonDocument.Parse(result.Stdout); }
            catch (JsonException error)
            {
                check(result.Name + "-json-contract", false, error.Message + "; exit=" + result.ExitCode);
                return null;
            }
        }

        async Task ProjectPaths(Inputs input)
        {
            object Entry(string mode, Rule[] rules) => new { mode, leftPath = input.Paths[0], rightPath = input.Paths[^1],
                tableDelimiter = ',', tableQuote = '"', tableAllowNewlinesInQuotes = true, lineTransformRules = rules };
            var rules = new[] { new Rule("ColumnRange(\"1,3\")", 7), new Rule("Line", 7) };
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var project = Path.Combine(directory, "project.json");
            var workspace = Path.Combine(directory, "workspace.json");
            await File.WriteAllBytesAsync(project, JsonSerializer.SerializeToUtf8Bytes(Entry("Table", rules), jsonOptions));
            await File.WriteAllBytesAsync(workspace, JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1,
                entries = new[] { Entry("Table", rules), Entry("Text", [new("\"same\"", 7)]) }, activeEntryIndex = 1 }, jsonOptions));
            foreach (var source in new[] { project, workspace })
            {
                originals.Add(source, await File.ReadAllBytesAsync(source));
                var copy = source + ".copy.json";
                var again = source + ".again.json";
                await run("line-transform-project-copy-" + Path.GetFileName(source), 0, true, ["--project-copy", source, copy]);
                await run("line-transform-project-recopy-" + Path.GetFileName(source), 0, true, ["--project-copy", copy, again]);
                check("line-transform-json-stable-" + Path.GetFileName(source), File.Exists(copy) && File.Exists(again)
                    && File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(again)), "Stable JSON roundtrip.");
                if (File.Exists(copy))
                {
                    using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(copy));
                    var entry = saved.RootElement.TryGetProperty("entries", out var entries) ? entries[0] : saved.RootElement;
                    check("line-transform-json-rules-" + Path.GetFileName(source), entry.GetProperty("lineTransformRules").EnumerateArray()
                        .Select(rule => (rule.GetProperty("expression").GetString(), rule.GetProperty("panes").GetInt32()))
                        .SequenceEqual(rules.Select(rule => ((string?)rule.Expression, rule.Panes))), "Expression, mask and order survive JSON.");
                    if (source == workspace) check("line-transform-workspace-active", saved.RootElement.GetProperty("activeEntryIndex").GetInt32() == 1
                        && saved.RootElement.GetProperty("entries")[1].GetProperty("lineTransformRules")[0].GetProperty("expression").GetString() == "\"same\"", "Active entry and second entry rule.");
                }
            }
            var html = Path.Combine(directory, "report.html");
            await run("line-transform-report-project", 0, true, ["--report-project", project, html]);
            CheckHtml("line-transform-report", html, "data-ignored=\"true\"");
            var textHtml = Path.Combine(directory, "text-report.html");
            await run("line-transform-report-text", 0, true, ["--report-project", workspace, textHtml]);
            CheckHtml("line-transform-text-report", textHtml, "ignored");
            var overrideHtml = Path.Combine(directory, "override-report.html");
            await run("line-transform-report-override", 0, true, ["--report-project", project, overrideHtml, "--line-transform", "all", "Line"]);
            if (File.Exists(overrideHtml)) check("line-transform-report-override-meaningful", !File.ReadAllText(overrideHtml).Contains("data-ignored=\"true\"", StringComparison.Ordinal), "Override restores raw classification.");
            var package = Path.Combine(directory, "package.zip");
            await run("line-transform-package", 0, true, ["--package-project", project, package, "--report"]);
            if (File.Exists(package))
            {
                using var zip = ZipFile.OpenRead(package);
                foreach (var side in Enumerable.Range(0, input.Paths.Length))
                {
                    var matched = false;
                    foreach (var entry in zip.Entries.Where(entry => entry.Name.Length > 0))
                    {
                        using var stream = entry.Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes);
                        matched |= bytes.ToArray().SequenceEqual(input.Bytes[side]);
                    }
                    check("line-transform-package-original-" + side, matched, "Exact source bytes present in package.");
                }
                var report = zip.GetEntry("report.files/1.html");
                check("line-transform-package-report-present", report is not null, "Packaged report.");
                if (report is not null)
                {
                    using var reader = new StreamReader(report.Open()); var content = await reader.ReadToEndAsync();
                    check("line-transform-package-report-classification", content.Contains("old", StringComparison.Ordinal)
                        && content.Contains("new", StringComparison.Ordinal) && content.Contains("data-ignored=\"true\"", StringComparison.Ordinal), "Original cells and ignored classification.");
                }
                var expanded = Path.Combine(directory, "expanded");
                await run("line-transform-package-expand", 0, true, ["--archive-extract", package, expanded]);
                var reopened = Path.Combine(directory, "reopened.json");
                await run("line-transform-package-reopen", 0, true, ["--project-copy", Path.Combine(expanded, "project.json"), reopened]);
                var expandedHtml = Path.Combine(directory, "expanded-report.html");
                await run("line-transform-package-reloaded-report", 0, true, ["--report-project", reopened, expandedHtml]);
                CheckHtml("line-transform-reloaded-report", expandedHtml, "data-ignored=\"true\"");
            }
            var sentinel = Encoding.UTF8.GetBytes("existing output\r\n");
            var protectedOutput = Path.Combine(directory, "invalid-report.html");
            await File.WriteAllBytesAsync(protectedOutput, sentinel);
            foreach (var (name, badRules) in new[] { ("mask-zero", new[] { new Rule("Line", 0) }),
                ("mask-bits", new[] { new Rule("Line", 8) }), ("bad-literal", new[] { new Rule("ColumnRange(\"0\")", 7) }),
                ("expression-limit", new[] { new Rule(new string('x', 65_537), 7) }) })
            {
                var bad = Path.Combine(directory, "invalid-" + name + ".json");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(Entry("Table", badRules), jsonOptions);
                await File.WriteAllBytesAsync(bad, bytes); originals.Add(bad, bytes);
                var result = await run("line-transform-project-reject-" + name, 2, false, ["--report-project", bad, protectedOutput]);
                check(result.Name + "-preserves-output", result.ExitCode == 2 && File.ReadAllBytes(protectedOutput).SequenceEqual(sentinel), "Existing output bytes survive rejected project.");
                check(result.Name + "-explicit-product-error", result.ExitCode == 2 && !string.IsNullOrWhiteSpace(result.Stderr)
                    && string.IsNullOrWhiteSpace(result.Stdout), "Actual product rejection, without report JSON.");
                if (name == "expression-limit")
                {
                    var protectedCopy = Path.Combine(directory, "invalid-expression-copy.json");
                    await File.WriteAllBytesAsync(protectedCopy, sentinel);
                    var rejectedCopy = await run("line-transform-project-copy-reject-expression-limit", 2, false,
                        ["--project-copy", bad, protectedCopy]);
                    check(rejectedCopy.Name + "-explicit-product-error", rejectedCopy.ExitCode == 2 && !string.IsNullOrWhiteSpace(rejectedCopy.Stderr)
                        && string.IsNullOrWhiteSpace(rejectedCopy.Stdout), "Long expression read from JSON and rejected by product.");
                    check(rejectedCopy.Name + "-preserves-output", File.ReadAllBytes(protectedCopy).SequenceEqual(sentinel), "Existing project copy bytes survive rejection.");
                }
            }
        }

        void CheckHtml(string name, string path, string classification)
            => check(name + "-original-and-classification", File.Exists(path) && File.ReadAllText(path).Contains("old", StringComparison.Ordinal)
                && File.ReadAllText(path).Contains("new", StringComparison.Ordinal) && File.ReadAllText(path).Contains(classification, StringComparison.Ordinal),
                "Original visible values and ignored classification.");
    }

    private static string[] PhysicalLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return text[^1] is '\r' or '\n' ? lines[..^1] : lines;
    }

    private static int LogicalRows(string text)
    {
        if (text.Length == 0) return 0;
        var count = 0; var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"') { index++; continue; }
                quoted = !quoted;
            }
            else if (!quoted && text[index] is '\r' or '\n')
            {
                count++;
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            }
        }
        return text[^1] is '\r' or '\n' ? count : count + 1;
    }
}
