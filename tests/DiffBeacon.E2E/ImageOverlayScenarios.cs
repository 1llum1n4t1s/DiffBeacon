using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ImageOverlayScenarios
{
    internal static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check)
    {
        var fixedInputs = new[]
        {
            (Path: "tests/Fixtures/ImageOverlays/winimerge-image-overlay-golden.json.gz", SHA: "E100EA23C79FABAD15D4EA930C4BDF82A4231CF01FD407A4248F2225355855F5", Count: 49, States: 49, Label: "static"),
            (Path: "tests/Fixtures/ImageTemporalOverlays/golden.json.gz", SHA: "A69F8DAA4A4B69168C96AE7AAB903F590BB5C6C690E3AFAED23BD6DBA816F8F2", Count: 77, States: 83, Label: "temporal")
        };
        var observation = new List<object>();
        foreach (var source in fixedInputs)
        {
            check("overlay fixed " + source.Label + " SHA", Hash(source.Path) == source.SHA, source.Path);
            using var gzip = new GZipStream(File.OpenRead(source.Path), CompressionMode.Decompress);
            using var golden = JsonDocument.Parse(gzip); var cases = golden.RootElement.GetProperty("cases");
            var script = Path.Combine(fixtures, "overlay-" + source.Label + "-inputs.json");
            using (var file = File.Create(script))
            using (var writer = new Utf8JsonWriter(file))
            {
                writer.WriteStartObject(); writer.WriteStartArray("cases");
                foreach (var item in cases.EnumerateArray())
                {
                    writer.WriteStartObject(); writer.WriteString("name", item.GetProperty("name").GetString());
                    writer.WriteNumber("mode", item.GetProperty("mode").GetString() switch { "none" => 0, "xor" => 1, "alpha" => 2, "anim" => 3, _ => throw new InvalidDataException("fixture mode") });
                    foreach (var key in new[] { "overlayAlpha", "showDifferences", "highlightAlpha", "selectedDiffIndex", "wipeMode", "wipePosition", "blockSize", "threshold" })
                    { writer.WritePropertyName(key); item.GetProperty(key).WriteTo(writer); }
                    writer.WriteBoolean("blinkDifferences", item.TryGetProperty("blinkDifferences", out var blink) && blink.GetBoolean());
                    writer.WriteNumber("animationPeriod", item.TryGetProperty("animationPeriod", out var period) ? period.GetInt32() : 1000);
                    writer.WriteNumber("blinkPeriod", item.TryGetProperty("blinkPeriod", out var interval) ? interval.GetInt32() : 800);
                    writer.WritePropertyName("images"); item.GetProperty("images").WriteTo(writer);
                    writer.WritePropertyName("states");
                    if (item.TryGetProperty("states", out var states)) states.WriteTo(writer);
                    else { writer.WriteStartArray(); writer.WriteStartObject(); writer.WriteStartArray("epochs"); writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndArray(); }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            var inputHash = Hash(script);
            check("overlay expected absent from product input " + source.Label, !File.ReadAllText(script).Contains("\"expected\"", StringComparison.Ordinal), "only raw images/settings/epoch queues");
            var command = await run("overlay-" + source.Label + "-kernel", 0, true, ["--image-overlay-script", script]);
            using var actual = JsonDocument.Parse(command.Stdout); var foundCases = actual.RootElement.GetProperty("cases");
            var totalStates = 0; var caseIndex = 0;
            foreach (var item in cases.EnumerateArray())
            {
                var label = source.Label + "/" + item.GetProperty("name").GetString(); var expected = item.GetProperty("expected"); var found = foundCases[caseIndex++];
                foreach (var key in new[] { "rawBefore", "rawAfter", "baseCanvas" }) Frames(label + "/" + key, found.GetProperty(key), expected.GetProperty(key));
                foreach (var key in new[] { "classificationBefore", "classificationAfter" }) Classification(label + "/" + key, found.GetProperty(key), expected.GetProperty(key));
                var actualStates = found.GetProperty("states");
                if (expected.TryGetProperty("states", out var expectedStates))
                {
                    check(label + " state count", actualStates.GetArrayLength() == expectedStates.GetArrayLength(), "original consecutive Refresh");
                    var stateIndex = 0;
                    foreach (var expectedState in expectedStates.EnumerateArray()) State(label + "/" + stateIndex, actualStates[stateIndex++], expectedState);
                }
                else { check(label + " state count", actualStates.GetArrayLength() == 1, "static Refresh"); State(label, actualStates[0], expected); }
                void State(string stateLabel, JsonElement actualState, JsonElement expectedState)
                {
                    totalStates++; Frames(stateLabel + "/processed", actualState.GetProperty("processed"), expectedState.GetProperty("processed"));
                    var expectedReads = expectedState.TryGetProperty("clockReads", out var readEpochs) ? readEpochs.EnumerateArray().Select(value => value.GetInt64()).ToArray() : [];
                    check(stateLabel + " clock read order", actualState.GetProperty("clockReads").EnumerateArray().Select(value => value.GetInt64()).SequenceEqual(expectedReads), "each blend then extra show&&blink; no single-time sampling");
                    check(stateLabel + " position", actualState.GetProperty("position").GetInt32() == expectedState.GetProperty("position").GetInt32(), "wipe follows highlight and clamps on canvas");
                }
            }
            check("overlay " + source.Label + " case/state counts", caseIndex == source.Count && totalStates == source.States, caseIndex + "/" + totalStates);
            check("overlay " + source.Label + " input preserved", Hash(script) == inputHash && Hash(source.Path) == source.SHA, inputHash);
            observation.Add(new { source.Label, cases = caseIndex, states = totalStates, fixtureSHA256 = source.SHA, inputSHA256 = inputHash, work = actual.RootElement.GetProperty("workUsed").GetInt64() });
        }

        var basic = Basic(); var basicPath = await Save("basic", basic);
        var baseline = await run("overlay-basic-work", 0, true, ["--image-overlay-script", basicPath]);
        using (var result = JsonDocument.Parse(baseline.Stdout)) check("overlay derived work28", result.RootElement.GetProperty("workUsed").GetInt64() == 28, "raw decode2 + prepare16 + state10 (copy/blend/grid), shared pixel-work");
        await run("overlay-exact-work-bound", 0, true, ["--image-overlay-script", basicPath, "--max-work", "28"]);
        foreach (var work in new[] { "0", "27" })
        {
            var refused = await run("overlay-work-refused-" + work, 2, false, ["--image-overlay-script", basicPath, "--max-work", work]);
            check("overlay work no success stdout " + work, refused.Stdout.Length == 0 && refused.Stderr.Contains("256M", StringComparison.Ordinal), "reserve before classification/canvas/clock");
        }
        var largeBudget = Basic(); largeBudget["blockSize"] = 256; largeBudget["images"]![1]!["offsetX"] = 3999; largeBudget["images"]![1]!["offsetY"] = 3999;
        largeBudget["states"] = new JsonArray(Enumerable.Range(0, 4).Select(_ => (JsonNode)new JsonObject { ["epochs"] = new JsonArray() }).ToArray());
        var largeBudgetPath = await Save("cumulative-budget", largeBudget);
        var budgetFailure = await run("overlay-256M-states-preflight", 2, false, ["--image-overlay-script", largeBudgetPath]);
        check("overlay cumulative budget before output", budgetFailure.Stdout.Length == 0 && budgetFailure.Stderr.Contains("256M", StringComparison.Ordinal), "metadata reserve all4states, not output-size/canvas rejection");
        var clockCancel = Basic(); clockCancel["mode"] = 3; clockCancel["showDifferences"] = true; clockCancel["blinkDifferences"] = true;
        clockCancel["states"] = new JsonArray(new JsonObject { ["epochs"] = new JsonArray(100, 500, 400) });
        var cancellationPath = await Save("clock-cancel", clockCancel); var cancelInputHash = Hash(cancellationPath);
        foreach (var read in new[] { "1", "2", "3" })
        {
            var cancelled = await run("overlay-cancel-after-clock-" + read, 2, false, ["--image-overlay-script", cancellationPath, "--cancel-after-clock", read]);
            check("overlay cancellation publishes no JSON " + read, cancelled.Stdout.Length == 0 && cancelled.Stderr.Length > 0 && Hash(cancellationPath) == cancelInputHash, "real linked operation token cancelled at first/second blend/extra blink read");
        }
        var malformed = new (string Name, Action<JsonObject> Change)[]
        {
            ("mode-minus", x => x["mode"] = -1), ("mode-high", x => x["mode"] = 4), ("mode-type", x => x["mode"] = "anim"),
            ("alpha-minus", x => x["overlayAlpha"] = -.1), ("alpha-high", x => x["overlayAlpha"] = 1.1), ("alpha-infinity", x => x["overlayAlpha"] = "Infinity"),
            ("highlight-minus", x => x["highlightAlpha"] = -.1), ("threshold-minus", x => x["threshold"] = -1), ("block-zero", x => x["blockSize"] = 0),
            ("selected-low", x => x["selectedDiffIndex"] = -2), ("selected-missing", x => x["selectedDiffIndex"] = 1),
            ("empty-states", x => x["states"] = new JsonArray()), ("states-shape", x => x["states"] = new JsonObject()),
            ("negative-epoch", x => x["states"]![0]!["epochs"] = new JsonArray(-1)),
            ("missing-epoch", x => { x["mode"] = 3; x["states"]![0]!["epochs"] = new JsonArray(0); }),
            ("excess-epoch", x => x["states"]![0]!["epochs"] = new JsonArray(0)),
            ("showfalse-blink-epoch", x => { x["blinkDifferences"] = true; x["states"]![0]!["epochs"] = new JsonArray(0); }),
            ("pane-one", x => x["images"]!.AsArray().RemoveAt(1)), ("pane-four", x => { x["images"]!.AsArray().Add(x["images"]![0]!.DeepClone()); x["images"]!.AsArray().Add(x["images"]![0]!.DeepClone()); }),
            ("images-shape", x => x["images"] = new JsonObject()), ("dimension-zero", x => x["images"]![0]!["width"] = 0),
            ("dimension-overflow", x => { x["images"]![0]!["width"] = int.MaxValue; x["images"]![0]!["height"] = int.MaxValue; }),
            ("offset-negative", x => x["images"]![0]!["offsetX"] = -1), ("canvas-overflow", x => x["images"]![0]!["offsetY"] = int.MaxValue),
            ("byte256", x => { x["images"]![0]!.AsObject().Remove("bgraBase64"); x["images"]![0]!["bgraBytes"] = new JsonArray(256, 0, 0, 0); }),
            ("byte-negative", x => { x["images"]![0]!.AsObject().Remove("bgraBase64"); x["images"]![0]!["bgraBytes"] = new JsonArray(-1, 0, 0, 0); }),
            ("bgra-short", x => x["images"]![0]!["bgraBase64"] = "AA=="), ("bgra-both", x => x["images"]![0]!["bgraBytes"] = new JsonArray(0, 0, 0, 0)),
            ("wipe-mode", x => x["wipeMode"] = 3), ("wipe-negative", x => x["wipePosition"] = -1),
            ("unexpected-expected", x => x["expected"] = new JsonObject()), ("missing-images", x => x.Remove("images"))
        };
        foreach (var invalid in malformed)
        {
            var changed = Basic(); invalid.Change(changed); await Reject(invalid.Name, changed);
        }
        foreach (var period in new[] { 0, -1, 199, 8001 })
        foreach (var key in new[] { "animationPeriod", "blinkPeriod" })
        { var changed = Basic(); changed[key] = period; await Reject(key + period, changed); }
        var reversed = Basic(); reversed["mode"] = 3; reversed["states"]![0]!["epochs"] = new JsonArray(500, 100);
        var reversedPath = await Save("clock-backwards-valid", reversed);
        await run("overlay-positive-clock-backwards-valid", 0, true, ["--image-overlay-script", reversedPath]);
        var late = Basic(); late["mode"] = 4;
        var latePath = Path.Combine(fixtures, "overlay-late-invalid.json"); await File.WriteAllTextAsync(latePath, new JsonObject { ["cases"] = new JsonArray(Basic(), late) }.ToJsonString());
        var lateResult = await run("overlay-late-invalid-no-partial-json", 2, false, ["--image-overlay-script", latePath]);
        check("overlay late invalid stdout empty", lateResult.Stdout.Length == 0, "first completed state is buffered, not published");
        var bigName = Basic(); bigName["name"] = new string('<', 6_000_000); var bigPath = await Save("escaped-header-output-limit", bigName);
        var oversized = await run("overlay-escaped-header-output-limit", 2, false, ["--image-overlay-script", bigPath]);
        check("overlay bounded escaped header stdout empty", oversized.Stdout.Length == 0 && oversized.Stderr.Contains("32 MiB", StringComparison.Ordinal), "input under8MiB, escapedname over32MiB, cap applies before first state");
        var duplicatePath = Path.Combine(fixtures, "overlay-duplicate-key.json"); await File.WriteAllTextAsync(duplicatePath, "{\"cases\":[],\"cases\":[]}");
        var duplicate = await run("overlay-duplicate-key-refused", 2, false, ["--image-overlay-script", duplicatePath]); check("overlay duplicate stdout empty", duplicate.Stdout.Length == 0, "duplicate root property rejected");
        var emptyPath = Path.Combine(fixtures, "overlay-empty-cases.json"); await File.WriteAllTextAsync(emptyPath, "{\"cases\":[]}");
        var empty = await run("overlay-empty-cases-refused", 2, false, ["--image-overlay-script", emptyPath]); check("overlay empty cases stdout empty", empty.Stdout.Length == 0, "operations required before rendering");
        var lateState = Basic(); lateState["states"]!.AsArray().Add(new JsonObject { ["epochs"] = "not-array" });
        await Reject("late-state-shape", lateState);
        var oversizedInput = Path.Combine(fixtures, "overlay-input-over8MiB.json"); await File.WriteAllBytesAsync(oversizedInput, new byte[8 * 1024 * 1024 + 1]);
        var inputLimit = await run("overlay-input-limit-before-parse", 2, false, ["--image-overlay-script", oversizedInput]);
        check("overlay input size refusal", inputLimit.Stdout.Length == 0 && inputLimit.Stderr.Contains("8 MiB", StringComparison.Ordinal), "inputcap wins before malformed JSON parsing");
        await File.WriteAllTextAsync(Path.Combine(output, "overlay-observations.json"), JsonSerializer.Serialize(new { groups = observation, malformedCases = malformed.Length + 8, cancellationClockBoundaries = 3,
            GUI = "this Stage1 raw-kernel suite excludes GUI; integrated GUI uses separate headless artifacts", Cache = "FreeImage transparency cache not ported or proven", Timer = "this Stage1 raw-kernel suite excludes GUI timer; separate headless verifies timer", Work = "raw command shared256M metadata preflight and stage reservation" }, new JsonSerializerOptions { WriteIndented = true }));

        async Task<string> Save(string name, JsonObject item)
        { var path = Path.Combine(fixtures, "overlay-" + name + ".json"); await File.WriteAllTextAsync(path, new JsonObject { ["cases"] = new JsonArray(item) }.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })); return path; }
        async Task Reject(string name, JsonObject item)
        {
            var path = await Save(name, item); var before = Hash(path); var refused = await run("overlay-reject-" + name, 2, false, ["--image-overlay-script", path]);
            check("overlay rejection no stdout/input preserved " + name, refused.Stdout.Length == 0 && refused.Stderr.Length > 0 && Hash(path) == before, "validate independent of operations; immutable input");
        }
        void Frames(string label, JsonElement actual, JsonElement expected)
        {
            check(label + " count", actual.GetArrayLength() == expected.GetArrayLength(), "all panes");
            for (var pane = 0; pane < expected.GetArrayLength(); pane++)
            {
                var a = actual[pane]; var e = expected[pane]; var bytes = e.GetProperty("bgraBase64").GetBytesFromBase64();
                check(label + " pane" + pane + " BGRA", a.GetProperty("width").GetInt32() == e.GetProperty("width").GetInt32()
                    && a.GetProperty("height").GetInt32() == e.GetProperty("height").GetInt32() && a.GetProperty("bgraBase64").GetBytesFromBase64().AsSpan().SequenceEqual(bytes)
                    && string.Equals(a.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase), "all four channels, hiddenRGB, dimensions and hash");
            }
        }
        void Classification(string label, JsonElement actual, JsonElement expected)
        {
            foreach (var key in new[] { "pair01", "pair21", "pair02", "regionIds" })
            {
                var a = actual.GetProperty(key); var e = expected.GetProperty(key);
                check(label + "/" + key, a.GetArrayLength() == e.GetArrayLength() && a.EnumerateArray().Select(row => row.GetArrayLength()).SequenceEqual(e.EnumerateArray().Select(row => row.GetArrayLength()))
                    && a.EnumerateArray().SelectMany(row => row.EnumerateArray()).Select(value => value.GetInt32()).SequenceEqual(e.EnumerateArray().SelectMany(row => row.EnumerateArray()).Select(value => value.GetInt32())), "real classifier; no golden mask passed");
            }
            var actualRegions = actual.GetProperty("regions"); var expectedRegions = expected.GetProperty("regions");
            check(label + "/regions", actualRegions.GetArrayLength() == expectedRegions.GetArrayLength()
                && actualRegions.EnumerateArray().SelectMany(region => new[] { "id", "op", "left", "top", "right", "bottom" }.Select(key => region.GetProperty(key).GetInt32()))
                    .SequenceEqual(expectedRegions.EnumerateArray().SelectMany(region => new[] { "id", "op", "left", "top", "right", "bottom" }.Select(key => region.GetProperty(key).GetInt32()))), "region ids/classes/rectangles remain unchanged");
        }
        static JsonObject Basic() => new()
        {
            ["name"] = "basic", ["mode"] = 2, ["overlayAlpha"] = .3, ["showDifferences"] = false, ["blinkDifferences"] = false,
            ["animationPeriod"] = 1000, ["blinkPeriod"] = 800, ["highlightAlpha"] = .7, ["selectedDiffIndex"] = -1,
            ["wipeMode"] = 0, ["wipePosition"] = 0, ["blockSize"] = 1, ["threshold"] = 0,
            ["images"] = new JsonArray(new JsonObject { ["width"] = 1, ["height"] = 1, ["offsetX"] = 0, ["offsetY"] = 0, ["bgraBase64"] = "AQIDAA==" },
                new JsonObject { ["width"] = 1, ["height"] = 1, ["offsetX"] = 0, ["offsetY"] = 0, ["bgraBase64"] = "BQQD/w==" }),
            ["states"] = new JsonArray(new JsonObject { ["epochs"] = new JsonArray() })
        };
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }
}
