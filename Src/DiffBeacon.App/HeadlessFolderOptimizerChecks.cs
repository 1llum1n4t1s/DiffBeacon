using System.Security.Cryptography;
using System.Text.Json;
using DiffBeacon.Core;

namespace DiffBeacon.App;

// 通常Appの --self-test / --folder-properties-only から呼ぶ。Core複製やprivate probeは用いない。
internal static class HeadlessFolderOptimizerChecks
{
    internal static void Run(string output, Action<string, bool, string> check, Action<Task> pump)
    {
        var work = Path.Combine(output, "folder-properties", "optimizer");
        Directory.CreateDirectory(work);
        using var stream = File.Create(Path.Combine(work, "host-observations.json"));
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("scope", "real App self-test; public ParseExpression / Matches and injected environments");
        json.WriteBoolean("typedAstObserved", false); json.WriteBoolean("privateProbesObserved", false);
        json.WriteBoolean("nativeFullParserExecuted", false); json.WriteBoolean("nativeExceptionEquivalent", false);
        const string expectedPlanSha = "9924CB05FFE49E3BF0608E780DA18B023389B2FB0349E8E65073CD74BFE15381";
        const string expectedProofSha = "F2955CD07049D8F6318BA3DC301E52B5A008D548F2EAD8D5B5C2AB027CF1766E";
        var fixture = ResolveFixture(expectedPlanSha, expectedProofSha);
        var planBytes = File.ReadAllBytes(Path.Combine(fixture, "public-plan.json"));
        var planSha = Convert.ToHexString(SHA256.HashData(planBytes));
        json.WriteString("fixtureDirectory", fixture);
        json.WriteBoolean("dateStrLocalTimezoneQualified", false);
        json.WriteString("publicPlanSha256", planSha);
        json.WriteString("nativeProofSha256", HashFile(Path.Combine(fixture, "native-proof.zip")));
        check("folder optimizer fixed public-plan binding", planSha == expectedPlanSha, planSha);
        if (planSha != expectedPlanSha) throw new InvalidDataException("Folder optimizer fixture public-plan SHA mismatch.");
        json.WriteStartArray("cases");
        void Record(string id, string expression, bool passed, bool parsed, Exception? compileError = null,
            Exception? runtimeError = null, int compileReads = 0, int reads = 0, bool? first = null, bool? second = null,
            string scope = "source-derived", string nativeId = "", bool folderComparisonObserved = false,
            IReadOnlyList<string>? compilePaths = null, IReadOnlyList<string>? paths = null)
        {
            json.WriteStartObject(); json.WriteString("id", id); json.WriteString("expression", expression);
            json.WriteBoolean("passed", passed); json.WriteBoolean("parseSucceeded", parsed);
            json.WriteBoolean("matchesObserved", first.HasValue || runtimeError is not null);
            json.WriteBoolean("folderComparisonObserved", folderComparisonObserved);
            json.WriteString("scope", scope); json.WriteString("nativeId", nativeId);
            WriteException(json, "compileException", compileError); WriteException(json, "matchesException", runtimeError);
            json.WriteNumber("compileReads", compileReads); json.WriteNumber("totalReads", reads);
            if (compilePaths is not null)
            { json.WriteStartArray("compileReadPaths"); foreach (var path in compilePaths) json.WriteStringValue(path); json.WriteEndArray(); }
            if (paths is not null)
            { json.WriteStartArray("totalReadPaths"); foreach (var path in paths) json.WriteStringValue(path); json.WriteEndArray(); }
            if (first is { } a) json.WriteBoolean("first", a); if (second is { } b) json.WriteBoolean("second", b);
            json.WriteEndObject(); json.Flush();
            check("folder optimizer " + id, passed, expression);
        }
        try
        {
        // 原典parserの実測ではなく、y:203 / SetPropFunc:100 から導く公開parser回帰。
        foreach (var count in new[] { 2, 3 })
        foreach (var function in new[] { "Prop", "LeftProp", "MiddleProp", "RightProp" })
        {
            IReadOnlyDictionary<string, FileFilterPropertyValue>?[] properties = Enumerable.Range(0, count)
                .Select(index => (IReadOnlyDictionary<string, FileFilterPropertyValue>?)new Dictionary<string, FileFilterPropertyValue>
                { ["System.Size"] = new(FileFilterPropertyKind.Integer, Integer: (index + 1) * 11) }).ToArray();
            var value = function == "RightProp" ? count * 11 : function == "MiddleProp" ? 22 : 11;
            var expression = function == "Prop" ? "AllEqual(Prop((('System.Size'))))"
                : "AllEqual(" + function + "((('System.Size')))," + value + ")";
            FileFilter? filter = null; Exception? compile = null; Exception? runtime = null; bool? result = null;
            try { filter = FileFilter.ParseExpression(expression); } catch (Exception error) { compile = error; }
            if (filter is not null)
                try { result = filter.Matches("rules", false, 1, DateTime.UnixEpoch, properties); } catch (Exception error) { runtime = error; }
            Record("parenthesized " + function + " " + count, expression, filter is not null && compile is null && runtime is null
                && result == (function != "Prop") && filter.PropertyNames.SequenceEqual(new[] { "System.Size" }),
                filter is not null, compile, runtime, first: result,
                scope: "source-derived FilterParser.y:203 / FunctionNodeProperty.cpp:100; public injected property dictionary; not native parser measurement");
        }
        foreach (var function in new[] { "Prop", "LeftProp", "MiddleProp", "RightProp" })
        foreach (var argument in new[] { "'System.'+'Size'", "Name", "", "'System.Size','System.Size'", "((('System.Size'))" })
        foreach (var directive in new[] { "@opt", "@noopt" })
        {
            var expression = directive + " " + function + "(" + argument + ")";
            FileFilter? filter = null; Exception? compile = null;
            try { filter = FileFilter.ParseExpression(expression); } catch (Exception error) { compile = error; }
            Record("property literal rejection " + function + " " + directive + " " + argument, expression,
                filter is null && compile is FormatException, filter is not null, compile,
                scope: "source-derived parser literal class guard before Optimize; not native parser measurement");
        }
        foreach (var count in new[] { 2, 3 })
        {
            var roots = Enumerable.Range(0, count).Select(index => Path.Combine(work, "parser-aliases-" + count, "side-" + index)).ToArray();
            for (var side = 0; side < count; side++)
            {
                Directory.CreateDirectory(roots[side]);
                foreach (var name in new[] { "shared.bin", "only-" + side + ".bin" }) File.WriteAllText(Path.Combine(roots[side], name), "optimizer-host02");
                foreach (var name in new[] { "shared.dir", "only-" + side + ".dir" }) Directory.CreateDirectory(Path.Combine(roots[side], name));
                var date = new DateTime(2024, 1, side + 2, 12, 0, 0, DateTimeKind.Utc);
                foreach (var path in Directory.EnumerateFiles(roots[side])) File.SetLastWriteTimeUtc(path, date);
                foreach (var path in Directory.EnumerateDirectories(roots[side])) Directory.SetLastWriteTimeUtc(path, date);
            }
            var files = new[] { "shared.bin" }.Concat(Enumerable.Range(0, count).Select(side => "only-" + side + ".bin")).ToArray();
            var directories = new[] { "shared.dir" }.Concat(Enumerable.Range(0, count).Select(side => "only-" + side + ".dir")).ToArray();
            var prefixes = count == 2 ? new[] { "Left", "Right" } : new[] { "Left", "Middle", "Right" };
            for (var side = 0; side < count; side++)
            {
                var prefix = prefixes[side];
                var present = new[] { "shared.bin", "shared.dir", "only-" + side + ".bin", "only-" + side + ".dir" };
                var absent = files.Concat(directories).Except(present, StringComparer.Ordinal).ToArray();
                CompareAlias("folder", prefix + "IsFolder == true", directories);
                CompareAlias("file", prefix + "IsFolder == false", files);
                CompareAlias("date-present", prefix + "DateStr != None", present);
                CompareAlias("date-missing", prefix + "DateStr == None", absent);
                CompareAlias("date-value", "AllEqual(" + prefix + "DateStr,'2024-01-" + (side + 2).ToString("00", System.Globalization.CultureInfo.InvariantCulture) + "')", present);

                void CompareAlias(string mode, string expression, string[] expected)
                {
                    FileFilter? filter = null; Exception? compile = null; Exception? runtime = null;
                    DirectoryComparisonResult? model = null;
                    try { filter = FileFilter.ParseExpression(expression); } catch (Exception error) { compile = error; }
                    if (filter is not null)
                    {
                        try
                        {
                            var options = new DirectoryComparisonOptions { FileFilter = filter, ShowFiltered = true };
                            var task = count == 2 ? FolderComparisons.CompareAsync(roots[0], roots[1], options, CancellationToken.None)
                                : FolderComparisons.CompareAsync(roots[0], roots[1], roots[2], options, CancellationToken.None);
                            pump(task); model = task.GetAwaiter().GetResult();
                        }
                        catch (Exception error) { runtime = error; }
                    }
                    var actual = model?.AllEntries.Where(entry => !entry.IsFiltered).Select(entry => entry.RelativePath).Order(StringComparer.Ordinal).ToArray() ?? [];
                    var passed = model is not null && compile is null && runtime is null
                        && !model.AllEntries.Any(entry => entry.Error is not null)
                        && actual.SequenceEqual(expected.Order(StringComparer.Ordinal));
                    var id = "alias-" + count + "-" + prefix + "-" + mode;
                    using var evidence = File.Create(Path.Combine(work, id + ".json"));
                    using var writer = new Utf8JsonWriter(evidence, new JsonWriterOptions { Indented = true });
                    writer.WriteStartObject(); writer.WriteString("expression", expression); writer.WriteBoolean("passed", passed);
                    writer.WriteString("scope", "source-derived App FolderComparisons actual roots; local timezone compatibility unqualified");
                    writer.WriteBoolean("localTimezoneQualified", false); writer.WriteStartArray("expectedVisible");
                    foreach (var name in expected.Order(StringComparer.Ordinal)) writer.WriteStringValue(name); writer.WriteEndArray();
                    writer.WriteStartArray("actualVisible"); foreach (var name in actual) writer.WriteStringValue(name); writer.WriteEndArray();
                    if (model is not null) { writer.WriteStartObject("model"); FolderCommands.WriteModel(writer, model); writer.WriteEndObject(); }
                    writer.WriteEndObject();
                    Record(id, expression, passed, filter is not null, compile, runtime,
                        scope: "source-derived Nodes:895,1068,1543,1569,1643; actual App folder compare; see per-case model; not native parser measurement",
                        folderComparisonObserved: model is not null);
                }
            }
        }
        // folder-only FileExists/FileMissing/Missing。LineMissingField(provider, lineIndex)とは別資格。
        foreach (var count in new[] { 2, 3 })
        {
            var existenceWork = Path.Combine(work, "existence-" + count); Directory.CreateDirectory(existenceWork);
            var roots = Enumerable.Range(0, count).Select(index => Path.Combine(existenceWork, "side-" + index)).ToArray();
            var presence = new Dictionary<string, bool[]>(StringComparer.Ordinal);
            presence.Add("shared.bin", Enumerable.Repeat(true, count).ToArray());
            presence.Add("shared.dir", Enumerable.Repeat(true, count).ToArray());
            presence.Add("collision-all", Enumerable.Repeat(true, count).ToArray());
            presence.Add("collision-gap", Enumerable.Range(0, count).Select(index => index == 0 || index == count - 1).ToArray());
            for (var index = 0; index < count; index++)
            {
                presence.Add("only-" + index + ".bin", Enumerable.Range(0, count).Select(side => side == index).ToArray());
                presence.Add("only-" + index + ".dir", Enumerable.Range(0, count).Select(side => side == index).ToArray());
            }
            var originalBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (var side = 0; side < count; side++)
            {
                Directory.CreateDirectory(roots[side]);
                foreach (var row in presence)
                {
                    if (!row.Value[side]) continue;
                    var path = Path.Combine(roots[side], row.Key);
                    var directory = row.Key.EndsWith(".dir", StringComparison.Ordinal)
                        || row.Key == "collision-all" && side % 2 == 1
                        || row.Key == "collision-gap" && side == count - 1;
                    if (directory) Directory.CreateDirectory(path);
                    else
                    {
                        var bytes = System.Text.Encoding.UTF8.GetBytes("folder-existence-original " + count + "/" + side + "/" + row.Key + "\n");
                        File.WriteAllBytes(path, bytes); originalBytes.Add(path, bytes);
                    }
                }
            }
            var originalDirectories = roots.SelectMany(root => Directory.EnumerateDirectories(root)).Order(StringComparer.Ordinal).ToArray();
            using (var inputStream = File.Create(Path.Combine(existenceWork, "input-originals.json")))
            using (var inputWriter = new Utf8JsonWriter(inputStream, new JsonWriterOptions { Indented = true }))
            {
                inputWriter.WriteStartObject(); inputWriter.WriteString("scope", "actual fixture bytes; verification reads are not product filter content reads");
                inputWriter.WriteStartArray("files");
                foreach (var pair in originalBytes)
                {
                    inputWriter.WriteStartObject(); inputWriter.WriteString("path", pair.Key); inputWriter.WriteString("originalBytesHex", Convert.ToHexString(pair.Value));
                    inputWriter.WriteString("sha256", Convert.ToHexString(SHA256.HashData(pair.Value))); inputWriter.WriteNumber("length", pair.Value.Length); inputWriter.WriteEndObject();
                }
                inputWriter.WriteEndArray(); inputWriter.WriteStartArray("directories");
                foreach (var path in originalDirectories) inputWriter.WriteStringValue(path); inputWriter.WriteEndArray(); inputWriter.WriteEndObject();
            }
            var originalOptions = new DirectoryComparisonOptions { ShowFiltered = true, Mode = DirectoryComparisonMode.TimeAndSize };
            var originalTask = count == 2 ? FolderComparisons.CompareAsync(roots[0], roots[1], originalOptions, CancellationToken.None)
                : FolderComparisons.CompareAsync(roots[0], roots[1], roots[2], originalOptions, CancellationToken.None);
            pump(originalTask); var original = originalTask.GetAwaiter().GetResult();
            var originalMap = original.AllEntries.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
            bool PresenceMatches(DirectoryComparisonResult model) => model.AllEntries.Count == presence.Count
                && model.AllEntries.All(entry => presence.TryGetValue(entry.RelativePath, out var expected)
                    && ExistenceStates(entry, count).Select(state => state is not null).SequenceEqual(expected));
            var originalValid = PresenceMatches(original) && original.AllEntries.All(entry => entry.Error is null)
                && originalMap["collision-all"].Status == DirectoryDifferenceKind.TypeConflict
                && originalMap["collision-gap"].Status == DirectoryDifferenceKind.TypeConflict;
            using (var originalStream = File.Create(Path.Combine(existenceWork, "original-model.json")))
            using (var originalWriter = new Utf8JsonWriter(originalStream)) { originalWriter.WriteStartObject(); FolderCommands.WriteModel(originalWriter, original); originalWriter.WriteEndObject(); }
            var prefixes = count == 2 ? new[] { "Left", "Right" } : new[] { "Left", "Middle", "Right" };
            foreach (var field in new[] { "FileExists", "FileMissing", "Missing" })
            foreach (var directive in new[] { "@opt", "@noopt" })
            for (var side = 0; side < count; side++)
            {
                var prefix = prefixes[side]; var exists = field == "FileExists";
                var selected = presence.Where(row => row.Value[side] == exists).Select(row => row.Key).ToArray();
                var rejected = presence.Keys.Except(selected, StringComparer.Ordinal).ToArray();
                ObserveExistence("true", prefix + field + " == true", selected);
                ObserveExistence("false", prefix + field + " == false", rejected);
                ObserveExistence("none", prefix + field + " == None", []);
                ObserveExistence("not-none", prefix + field + " != None", presence.Keys.ToArray());
                var oldField = exists ? prefix + "Exists" : "(not " + prefix + "Exists)";
                ObserveExistence("array-slot", "At(" + field + "," + side + ") == " + oldField
                    + " and At(" + field + "," + side + ") == " + prefix + field + " and Len(" + field + ") == " + count, presence.Keys.ToArray());

                void ObserveExistence(string mode, string body, string[] expectedVisible)
                {
                    var expression = directive + " " + body;
                    FileFilter? filter = null; Exception? compile = null; Exception? runtime = null; DirectoryComparisonResult? model = null;
                    try { filter = FileFilter.ParseExpression(expression); } catch (Exception error) { compile = error; }
                    if (filter is not null)
                    {
                        try
                        {
                            var options = originalOptions with { FileFilter = filter };
                            var task = count == 2 ? FolderComparisons.CompareAsync(roots[0], roots[1], options, CancellationToken.None)
                                : FolderComparisons.CompareAsync(roots[0], roots[1], roots[2], options, CancellationToken.None);
                            pump(task); model = task.GetAwaiter().GetResult();
                        }
                        catch (Exception error) { runtime = error; }
                    }
                    var actual = model?.AllEntries.Where(entry => !entry.IsFiltered).Select(entry => entry.RelativePath).Order(StringComparer.Ordinal).ToArray() ?? [];
                    var originalsRetained = model is not null && PresenceMatches(model) && model.AllEntries.All(entry => originalMap.TryGetValue(entry.RelativePath, out var before)
                        && ExistenceStates(entry, count).Zip(ExistenceStates(before, count)).All(pair => SameOriginalSide(pair.First, pair.Second)));
                    var passed = originalValid && filter is { UsesProperties: false } && filter.PropertyNames.Count == 0
                        && compile is null && runtime is null && originalsRetained
                        && model!.AllEntries.All(entry => entry.Error is null)
                        && actual.SequenceEqual(expectedVisible.Order(StringComparer.Ordinal));
                    var id = "existence-" + count + "-" + field + "-" + directive.TrimStart('@') + "-" + prefix + "-" + mode;
                    using var evidence = File.Create(Path.Combine(existenceWork, id + ".json"));
                    using var writer = new Utf8JsonWriter(evidence, new JsonWriterOptions { Indented = true });
                    writer.WriteStartObject(); writer.WriteString("expression", expression); writer.WriteBoolean("passed", passed);
                    writer.WriteString("scope", "source-derived folder FileExistsField/FileMissingField only; line-provider Missing unqualified");
                    writer.WriteBoolean("nativeFullParserExecuted", false); writer.WriteBoolean("lineProviderMissingQualified", false);
                    writer.WriteBoolean("otherUnsupportedFieldsQualified", false); writer.WriteBoolean("originalSidesRetained", originalsRetained);
                    writer.WriteString("originalModel", "original-model.json"); writer.WriteString("inputOriginals", "input-originals.json");
                    writer.WriteString("comparisonMode", "TimeAndSize; no product content comparison requested");
                    writer.WriteBoolean("usesProperties", filter?.UsesProperties ?? false); writer.WriteNumber("propertyNamesCount", filter?.PropertyNames.Count ?? 0);
                    writer.WriteStartArray("expectedVisible"); foreach (var name in expectedVisible.Order(StringComparer.Ordinal)) writer.WriteStringValue(name); writer.WriteEndArray();
                    writer.WriteStartArray("actualVisible"); foreach (var name in actual) writer.WriteStringValue(name); writer.WriteEndArray();
                    writer.WriteStartArray("originalPresence");
                    foreach (var row in presence.OrderBy(row => row.Key, StringComparer.Ordinal))
                    {
                        writer.WriteStartObject(); writer.WriteString("path", row.Key); writer.WriteStartArray("sides");
                        foreach (var value in row.Value) writer.WriteBooleanValue(value); writer.WriteEndArray(); writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    if (model is not null) { writer.WriteStartObject("model"); FolderCommands.WriteModel(writer, model); writer.WriteEndObject(); }
                    writer.WriteEndObject();
                    Record(id, expression, passed, filter is not null, compile, runtime,
                        scope: "source-derived Nodes885-893/table1559,1561,1574 folder only; actual App FolderComparisons; not line provider or full parser measurement",
                        folderComparisonObserved: model is not null);
                }
            }
            var bytesRetained = originalBytes.All(pair => File.ReadAllBytes(pair.Key).AsSpan().SequenceEqual(pair.Value))
                && roots.Sum(root => Directory.EnumerateFiles(root).Count()) == originalBytes.Count
                && roots.SelectMany(root => Directory.EnumerateDirectories(root)).Order(StringComparer.Ordinal).SequenceEqual(originalDirectories);
            Record("existence original input and sides " + count, "", originalValid && bytesRetained, false,
                scope: "actual App original model/presence plus all original fixture bytes and directory names retained", folderComparisonObserved: true);
        }
        foreach (var field in new[] { "FileExists", "FileMissing", "Missing" })
        foreach (var directive in new[] { "@opt", "@noopt" })
        {
            var expression = directive + " " + field + " == " + (field == "FileExists" ? "true" : "false") + " and " + field + " != None";
            FileFilter? filter = null; Exception? compile = null; Exception? runtime = null; bool? result = null;
            try { filter = FileFilter.ParseExpression(expression); } catch (Exception error) { compile = error; }
            if (filter is not null) try { result = Match(filter); } catch (Exception error) { runtime = error; }
            Record("standalone existence extension " + field + " " + directive, expression,
                filter is { UsesProperties: false } && filter.PropertyNames.Count == 0 && compile is null && runtime is null && result == true,
                filter is not null, compile, runtime, first: result,
                scope: "standalone synthetic present-file extension only; not native folder/line provider equivalence");
        }
        foreach (var item in new[]
        {
            ("c03879", "AllEqual((true or (1 / 0)),None)"),
            ("c03915", "AllEqual(lineat(columnrange(substr(\"0\",0))),None)"),
            ("c03939", "AllEqual(if(true,\"chosen\",columnrange(substr(\"0\",0))),None)")
        })
        {
            foreach (var directive in new[] { "@opt", "@noopt" })
            {
                var expression = directive + " @cs " + item.Item2;
                FileFilter? filter = null; Exception? compile = null;
                try { filter = FileFilter.ParseExpression(expression); } catch (Exception error) { compile = error; }
                // Column/Line系のnoopt実行には実フォルダーcontextが必要。欠落contextの例外を原典互換として合格にしない。
                var runtimeObserved = item.Item1 == "c03879";
                bool? result = null; Exception? runtime = null;
                if (filter is not null && runtimeObserved)
                    try { result = Match(filter); } catch (Exception error) { runtime = error; }
                var passed = directive == "@opt" ? compile is FormatException && filter is null
                    : filter is not null && compile is null && (!runtimeObserved || runtime is null && result == false);
                Record(item.Item1 + " " + directive, expression, passed, filter is not null, compile, runtime, first: result,
                    scope: runtimeObserved ? "native-bound phase; public boolean consumer" : "native-bound compile phase only; noopt folder runtime unobserved", nativeId: item.Item1);
            }
        }
        foreach (var directive in new[] { "@opt", "@noopt" })
        {
            var expression = directive + " 1 / 0";
            FileFilter? filter = null; Exception? compile = null; Exception? runtime = null;
            try { filter = FileFilter.ParseExpression(expression); } catch (Exception error) { compile = error; }
            if (filter is not null) try { _ = Match(filter); } catch (Exception error) { runtime = error; }
            Record("division phase " + directive, expression,
                directive == "@opt" ? compile is FormatException && compile.InnerException is DivideByZeroException
                    : compile is null && runtime is DivideByZeroException, filter is not null, compile, runtime);
        }
        foreach (var directive in new[] { "@opt", "@noopt" })
        {
            var clock = new Clock();
            var expression = directive + " AllEqual(Now(), ToDateTime('2024-01-02T03:04:05Z')) and AllEqual(Today(), ToDateTime('2024-01-02T00:00:00Z'))";
            var filter = FileFilter.ParseExpression(expression, timestampEnvironment: clock);
            var captures = clock.Captures; clock.Now += 10L * 86_400_000_000;
            var first = Match(filter); clock.Now += 10L * 86_400_000_000; var second = Match(filter);
            Record("clock capture " + directive, expression, captures == 1 && clock.Captures == 1 && first && second,
                true, compileReads: captures, reads: clock.Captures, first: first, second: second,
                scope: "source-derived UpdateTimestamp; opt/noopt parse capture; zero-offset injected calendar");
        }
        foreach (var function in new[] { "ReplaceWithList", "RegexReplaceWithList" })
        foreach (var mode in new[] { "literal", "noopt", "dynamic" })
        {
            var environment = new Lists();
            var expression = (mode == "noopt" ? "@noopt " : "@opt ") + "@cs AllEqual(" + function + "('a'," + (mode == "dynamic" ? "Name" : "'rules'") + "),'first')";
            var filter = FileFilter.ParseExpression(expression, listEnvironment: environment);
            var compiledReads = environment.Reads;
            var first = Match(filter); environment.Text = "a\tsecond\n"; var second = Match(filter);
            var literal = mode == "literal";
            Record(function + " " + mode, expression, first && second == literal && compiledReads == (literal ? 1 : 0)
                && environment.Reads == (literal ? 1 : 2), true, compileReads: compiledReads, reads: environment.Reads, first: first, second: second,
                scope: literal ? "native-bound list lifecycle; injected reader; public boolean consumer" : "source-derived runtime reader lifecycle",
                nativeId: literal ? function == "ReplaceWithList" ? "c06123" : "c06147" : "");
        }
        {
            var lists = new Lists();
            const string expression = "@opt AllEqual(ReplaceWithList('a','rules'),'first') and AllEqual(ToDateTime('2024-01-02T03:04:05Z'),None)";
            FileFilter? filter = null; Exception? compile = null; Exception? runtime = null;
            try { filter = FileFilter.ParseExpression(expression, listEnvironment: lists); }
            catch (Exception error) { compile = error; }
            var compileReads = lists.Reads; lists.Text = "a\tsecond\n";
            if (filter is not null) try { _ = Match(filter); } catch (Exception error) { runtime = error; }
            Record("ordinary exception retains earlier list fold", expression,
                filter is not null && compile is null && runtime is ArgumentException { Message: "日時関数には明示的な環境が必要です。" }
                && compileReads == 1 && lists.Reads == 1,
                filter is not null, compile, runtime, compileReads, lists.Reads,
                scope: "source-derived ordered traversal; ordinary ArgumentException only; not every native std::exception");
        }
        {
            var lists = new Lists();
            const string expression = "@opt AllEqual(ReplaceWithList('a','rules'),'first') and AllEqual(ToDateTime('2024-01-02T03:04:05Z'),None) and AllEqual(ReplaceWithList('a','later'),'first')";
            FileFilter? filter = null; Exception? compile = null; Exception? runtime = null;
            try { filter = FileFilter.ParseExpression(expression, listEnvironment: lists); }
            catch (Exception error) { compile = error; }
            var compileReads = lists.Reads; var compilePaths = lists.Paths.ToArray();
            lists.Text = "a\tsecond\n";
            if (filter is not null) try { _ = Match(filter); } catch (Exception error) { runtime = error; }
            Record("ordinary exception stops later child preparation", expression,
                filter is not null && compile is null
                && runtime is ArgumentException { Message: "日時関数には明示的な環境が必要です。" }
                && compileReads == 1 && lists.Reads == 1
                && compilePaths.SequenceEqual(new[] { "rules" }) && lists.Paths.SequenceEqual(new[] { "rules" }),
                filter is not null, compile, runtime, compileReads, lists.Reads,
                scope: "source-derived left-to-right traversal stops at ordinary ArgumentException; later literal-list side effect unobserved; not private AST proof",
                compilePaths: compilePaths, paths: lists.Paths);
        }
        {            var single = new Lists { Charge = 40_000 };
            const string singleExpression = "@opt AllEqual(ReplaceWithList('a','one'),'first')";
            FileFilter? singleFilter = null; Exception? singleError = null; bool? singleResult = null;
            try { singleFilter = FileFilter.ParseExpression(singleExpression, listEnvironment: single); }
            catch (Exception error) { singleError = error; }
            if (singleFilter is not null) singleResult = Match(singleFilter);
            Record("single compile budget control", singleExpression, singleFilter is not null && singleError is null && singleResult == true && single.Reads == 1,
                singleFilter is not null, singleError, compileReads: single.Reads, reads: single.Reads, first: singleResult);
            var lists = new Lists { Charge = 40_000 };
            const string expression = "@opt Array(ReplaceWithList('a','one'),ReplaceWithList('a','two'))";
            FileFilter? filter = null; Exception? compile = null;
            try { filter = FileFilter.ParseExpression(expression, listEnvironment: lists); } catch (Exception error) { compile = error; }
            Record("shared compile budget", expression, filter is null && lists.Reads == 2 && compile is InvalidOperationException
                && compile.Message.Contains("65,536", StringComparison.Ordinal), filter is not null, compile, reads: lists.Reads);
        }
        foreach (var mode in new[] { "pre", "read-progress", "unselected-read-progress" })
        {
            using var cancellation = new CancellationTokenSource();
            var lists = new Lists { Cancel = mode == "pre" ? null : cancellation };
            var expression = mode == "unselected-read-progress" ? "@opt If(true,true,ReplaceWithList('a','rules'))" : "@opt ReplaceWithList('a','rules')";
            if (mode == "pre") cancellation.Cancel();
            FileFilter? filter = null; Exception? compile = null;
            try { filter = FileFilter.ParseExpression(expression, listEnvironment: lists, cancellationToken: cancellation.Token); }
            catch (Exception error) { compile = error; }
            Record("compile cancellation " + mode, expression, filter is null && compile is OperationCanceledException
                && lists.Reads == (mode == "pre" ? 0 : 1), filter is not null, compile, reads: lists.Reads,
                scope: "source-derived injected list read progress and shared CancellationToken");
        }
        {
            const string expression = "@opt If(true,true,Prop('System.Size'))";
            var filter = FileFilter.ParseExpression(expression);
            Record("unselected property collection", expression, filter.UsesProperties && filter.PropertyNames.Contains("System.Size", StringComparer.Ordinal), true,
                scope: "source-derived compile collector; property value evaluation not claimed");
        }
        }
        catch (Exception error)
        {
            Record("unexpected host check exception", "", false, false, compileError: error,
                scope: "unexpected host-check failure; phase not inferred");
        }
        json.WriteEndArray();
        json.WriteStartObject("guiEvidence"); json.WriteBoolean("executedByThisHelper", false);
        json.WriteBoolean("passed", false);
        json.WriteString("acceptedDisplayCancellationAndStale", "parent folder-properties GUI 2/3-side real compare/cancel buttons and PNG/JSON");
        json.WriteString("outputProtection", "full self-test folder-copy/folder-copy-observations.json and folder-cancellation-observations.json; actual copy buttons");
        json.WriteString("limitedRunScope", "--folder-properties-only includes parent GUI compare checks; full folder-copy checks require all self-test");
        json.WriteEndObject(); json.WriteEndObject();
    }

    private static string ResolveFixture(string expectedSha, string expectedProofSha)
    {
        var candidates = new[] { Path.Combine(AppContext.BaseDirectory, "tests", "Fixtures", "FolderOptimizer"),
            Path.GetFullPath("tests/Fixtures/FolderOptimizer") };
        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            var plan = Path.Combine(candidate, "public-plan.json");
            var proof = Path.Combine(candidate, "native-proof.zip");
            if (File.Exists(plan) && File.Exists(proof) && HashFile(plan) == expectedSha && HashFile(proof) == expectedProofSha)
                return candidate;
        }
        throw new InvalidDataException("固定SHAと一致するFolderOptimizer public-plan/native-proof fixtureがありません: " + string.Join("; ", candidates));
    }
    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    private static DirectorySideSnapshot?[] ExistenceStates(DirectoryEntry entry, int count)
        => count == 2 ? [entry.LeftState, entry.RightState] : [entry.LeftState, entry.MiddleState, entry.RightState];
    private static bool SameOriginalSide(DirectorySideSnapshot? after, DirectorySideSnapshot? before)
        => after is null ? before is null : before is not null && after.Path == before.Path && after.Kind == before.Kind
            && after.Size == before.Size && after.LastWriteTimeUtc == before.LastWriteTimeUtc && after.Attributes == before.Attributes
            && after.LinkTarget == before.LinkTarget && after.Error == before.Error;
    private static bool Match(FileFilter filter) => filter.Matches("rules", false, 1, DateTime.UnixEpoch);
    private static void WriteException(Utf8JsonWriter json, string name, Exception? error)
    {
        json.WritePropertyName(name);
        if (error is null) { json.WriteNullValue(); return; }
        json.WriteStartObject(); json.WriteString("type", error.GetType().FullName); json.WriteString("message", error.Message);
        WriteException(json, "inner", error.InnerException); json.WriteEndObject();
    }
    private sealed class Lists : IFileFilterListEnvironment
    {
        internal List<string> Paths { get; } = [];
        internal int Reads; internal string Text = "a\tfirst\n"; internal long Charge;
        internal CancellationTokenSource? Cancel;
        public string ReadList(string path, FileFilterListReadContext context)
        {
            Reads++; Paths.Add(path); Cancel?.Cancel(); context.Charge(Charge); return Text;
        }
    }
    private sealed class Clock : ILineTransformTimestampClockEnvironment
    {
        internal long Now = (new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc).Ticks - DateTime.UnixEpoch.Ticks) / 10;
        internal int Captures;
        public string ProfileId => "folder-optimizer-controlled-utc";
        public int CurrentTzdSeconds => 0;
        public long CaptureNowEpochMicroseconds() { Captures++; return Now; }
        public int GetCalendarMidnightOffset(int year, int month, int day, long naiveEpochMicroseconds) => 0;
        public bool TryGetSerializationOffset(long epochMicroseconds, out int seconds, out string failureText)
        { seconds = 0; failureText = ""; return true; }
        public bool TryParseWindowsLocale(string text, out long epochMicroseconds) { epochMicroseconds = 0; return false; }
    }
}