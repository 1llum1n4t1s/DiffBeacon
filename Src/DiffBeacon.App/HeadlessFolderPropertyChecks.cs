using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessFolderPropertyChecks
{
    internal static unsafe void Run(MainWindow window, ComparisonPane pane, string output,
        Action<string, bool, string> check, Action<string> screenshot, Action<Task> pump)
    {
        var work = Path.Combine(output, "folder-properties"); Directory.CreateDirectory(work);
        var fixture = Path.GetFullPath("tests/Fixtures/FolderPropertyFilters");
        var observations = new List<(string Id, bool Passed)>();
        void Verify(string id, bool passed, string detail = "") { observations.Add((id, passed)); check("folder property " + id, passed, detail); }
        Verify("casing standalone Core current-culture ASCII fallback",
            FileFilter.ParseExpression("AllEqual(ToLower('AbC'), 'abc') and AllEqual(ToUpper('aBc'), 'ABC')").Matches("a", false, 1, DateTime.UnixEpoch));
        foreach (var lower in new[] { true, false })
        {
            var environment = new CasingEnvironment();
            var properties = new AnyOfOracleProperties(new(FileFilterPropertyKind.String, Text: "AbC"), false);
            var name = lower ? "ToLower" : "ToUpper";
            var filter = FileFilter.ParseExpression("AllEqual(" + name + "(LeftProp('fixture')), '" + (lower ? "abc" : "ABC") + "')", environment);
            Verify("casing " + name + " evaluates argument/environment once", filter.Matches("a", false, 1, DateTime.UnixEpoch, [properties])
                && properties.Reads == 1 && environment.Calls == 1);
            environment.Fail = true;
            var failed = false;
            try { filter.Matches("a", false, 1, DateTime.UnixEpoch, [properties]); }
            catch (InvalidOperationException error) when (error.Message == "casing-environment-failure") { failed = true; }
            Verify("casing " + name + " preserves explicit environment failure", failed && properties.Reads == 2 && environment.Calls == 2);
            environment.Fail = false;
            Verify("casing " + name + " does not map None", FileFilter.ParseExpression(name + "(None) == None", environment)
                .Matches("a", false, 1, DateTime.UnixEpoch) && environment.Calls == 2);
        }
        Verify("side metadata has no Shell requirement", !FileFilter.ParseExpression("LeftSize > 0 and MiddleExists and RightFolder == ''").UsesProperties);
        Verify("file attributes have no Shell requirement", !FileFilter.ParseExpression("LeftAttributes != None and MiddleAttrStr != None and AttrStr contains ''").UsesProperties);
        Verify("AllOf scalar standalone Matches", FileFilter.ParseExpression("AllOf(true)").Matches("a", false, 1, DateTime.UnixEpoch)
            && !FileFilter.ParseExpression("AllOf(false)").Matches("a", false, 1, DateTime.UnixEpoch)
            && !FileFilter.ParseExpression("AllOf(1)").Matches("a", false, 1, DateTime.UnixEpoch));
        Verify("AllEqual scalar standalone Matches", FileFilter.ParseExpression("AllEqual('same', 'same')").Matches("a", false, 1, DateTime.UnixEpoch));
        Verify("AllEqual Size standalone Matches", FileFilter.ParseExpression("AllEqual(Size)").Matches("a", false, 1, DateTime.UnixEpoch));
        Verify("AllEqual preserves strict numeric literal kinds in standalone Matches",
            !FileFilter.ParseExpression("AllEqual(1, 1.0)").Matches("a", false, 1, DateTime.UnixEpoch)
                && FileFilter.ParseExpression("AllEqual(1.0, 1.0)").Matches("a", false, 1, DateTime.UnixEpoch));
        Verify("AllEqual applies legacy byte literal conversion in standalone Matches",
            FileFilter.ParseExpression("AllEqual(1, 1.1b)").Matches("a", false, 1, DateTime.UnixEpoch));
        Verify("AllEqual overflowing floating literal standalone Matches",
            FileFilter.ParseExpression("AllEqual(1e100, 1e100)").Matches("a", false, 1, DateTime.UnixEpoch));
        Verify("AllEqual scalar mismatch standalone Matches", !FileFilter.ParseExpression("AllEqual('same', 'different')").Matches("a", false, 1, DateTime.UnixEpoch));
        Verify("AllEqual standalone composition retains native comparison None",
            !FileFilter.ParseExpression("AllEqual(1.0 == None, false)").Matches("a", false, 1, DateTime.UnixEpoch));
        Verify("AllOf standalone composition retains native comparison None",
            FileFilter.ParseExpression("AllOf(None < 0) == None").Matches("a", false, 1, DateTime.UnixEpoch));
        Verify("Len standalone composition retains native comparison None",
            FileFilter.ParseExpression("Len(None < 0) == 0").Matches("a", false, 1, DateTime.UnixEpoch));
        Verify("Array zero and variadic length",
            FileFilter.ParseExpression("Len(Array()) == 0 and Len(Array(1, 2, 3)) == 3").Matches("a", false, 1, DateTime.UnixEpoch));
        Verify("Array preserves nested order and strict numeric kinds",
            FileFilter.ParseExpression("AllEqual(Array(Array('a', 'b'), Array('a', 'b')))").Matches("a", false, 1, DateTime.UnixEpoch)
                && !FileFilter.ParseExpression("AllEqual(Array(Array('a', 'b'), Array('b', 'a')))").Matches("a", false, 1, DateTime.UnixEpoch)
                && !FileFilter.ParseExpression("AllEqual(Array(1, 1.0))").Matches("a", false, 1, DateTime.UnixEpoch));
        Verify("Array root predicate recurses through nested booleans while AnyOf and AllOf remain shallow",
            FileFilter.ParseExpression("Array(false, Array(true))").Matches("a", false, 1, DateTime.UnixEpoch)
                && !FileFilter.ParseExpression("Array()").Matches("a", false, 1, DateTime.UnixEpoch)
                && !FileFilter.ParseExpression("Array(false, 1, 'x', None)").Matches("a", false, 1, DateTime.UnixEpoch)
                && !FileFilter.ParseExpression("AnyOf(Array(Array(true)))").Matches("a", false, 1, DateTime.UnixEpoch)
                && !FileFilter.ParseExpression("AllOf(Array(Array(true)))").Matches("a", false, 1, DateTime.UnixEpoch));
        bool RejectArray(string expression)
        {
            try { FileFilter.ParseExpression(expression); return false; }
            catch (FormatException) { return true; }
        }
        Verify("Array rejects malformed commas, missing parentheses, and trailing tokens",
            RejectArray("Array(,)") && RejectArray("Array(true,)") && RejectArray("Array(, true)")
                && RejectArray("Array(true,, false)") && RejectArray("Array(true") && RejectArray("Array true") && RejectArray("Array(true))"));
        var exactArrayLength = "Array('" + new string('a', 16_375) + "')";
        var withinArrayDepth = string.Concat(Enumerable.Repeat("Array(", 64)) + "true" + new string(')', 64);
        var beyondArrayDepth = string.Concat(Enumerable.Repeat("Array(", 65)) + "true" + new string(')', 65);
        var arrayLimitsPassed = false;
        try { arrayLimitsPassed = FileFilter.ParseExpression(exactArrayLength) is not null && FileFilter.ParseExpression(withinArrayDepth).Matches("a", false, 1, DateTime.UnixEpoch) && RejectArray(exactArrayLength + " "); }
        catch (FormatException) { arrayLimitsPassed = false; }
        Verify("Array respects the 16,384-character and depth-64 parser limits", arrayLimitsPassed && RejectArray(beyondArrayDepth));
        var identicalNested = new FileFilterPropertyValue(FileFilterPropertyKind.Array, Items: [new(FileFilterPropertyKind.Integer, Integer: 1), new(FileFilterPropertyKind.String, Text: "x")]);
        var identicalNested2 = new FileFilterPropertyValue(FileFilterPropertyKind.Array, Items: [new(FileFilterPropertyKind.Integer, Integer: 1), new(FileFilterPropertyKind.String, Text: "x")]);
        var reorderedNested = new FileFilterPropertyValue(FileFilterPropertyKind.Array, Items: [new(FileFilterPropertyKind.String, Text: "x"), new(FileFilterPropertyKind.Integer, Integer: 1)]);
        var nestedProperties = new ArrayTraceProperties(new Dictionary<string, FileFilterPropertyValue> { ["first"] = identicalNested, ["second"] = identicalNested2, ["reordered"] = reorderedNested });
        var nestedMatch = FileFilter.ParseExpression("AllEqual(Array(Prop('first'), Prop('second')))").Matches("a", false, 1, DateTime.UnixEpoch, [nestedProperties]);
        var nestedOrderMismatch = !FileFilter.ParseExpression("AllEqual(Array(Prop('first'), Prop('reordered')))").Matches("a", false, 1, DateTime.UnixEpoch, [nestedProperties]);
        Verify("Array keeps nested property values grouped and ordered", nestedMatch && nestedOrderMismatch);
        var typedProperties = new ArrayTraceProperties(new Dictionary<string, FileFilterPropertyValue>
        {
            ["integer"] = new(FileFilterPropertyKind.Integer, Integer: 7),
            ["double"] = new(FileFilterPropertyKind.Double, Double: 7)
        });
        Verify("Array preserves integer and double property types",
            !FileFilter.ParseExpression("AllEqual(Array(Prop('integer'), Prop('double')))").Matches("a", false, 1, DateTime.UnixEpoch, [typedProperties]));
        var tracedValues = new Dictionary<string, FileFilterPropertyValue>
        {
            ["first"] = new(FileFilterPropertyKind.Boolean, Boolean: true),
            ["second"] = new(FileFilterPropertyKind.Boolean, Boolean: true),
            ["third"] = new(FileFilterPropertyKind.Boolean, Boolean: true)
        };
        var tracedProperties = new ArrayTraceProperties(tracedValues);
        var tracedMatch = FileFilter.ParseExpression("Array(Prop('first'), Prop('second'), Prop('third'))").Matches("a", false, 1, DateTime.UnixEpoch, [tracedProperties]);
        Verify("Array evaluates distinct properties once in source order", tracedMatch && tracedProperties.ReadTrace.SequenceEqual(["first", "second", "third"]));
        var throwingProperties = new ArrayTraceProperties(tracedValues, "second");
        var arrayThrowStopped = false;
        try { FileFilter.ParseExpression("Array(Prop('first'), Prop('second'), Prop('third'))").Matches("a", false, 1, DateTime.UnixEpoch, [throwingProperties]); }
        catch (InvalidOperationException error) { arrayThrowStopped = error.Message == "array-second"; }
        Verify("Array propagates argument errors and does not evaluate later arguments",
            arrayThrowStopped && throwingProperties.ReadTrace.SequenceEqual(["first", "second"]));
        IReadOnlyDictionary<string, FileFilterPropertyValue>?[] equalPropertySides =
        [
            new Dictionary<string, FileFilterPropertyValue> { ["System.Size"] = new(FileFilterPropertyKind.Integer, Integer: 4) },
            new Dictionary<string, FileFilterPropertyValue> { ["System.Size"] = new(FileFilterPropertyKind.Integer, Integer: 4) }
        ];
        IReadOnlyDictionary<string, FileFilterPropertyValue>?[] unequalPropertySides =
        [equalPropertySides[0], new Dictionary<string, FileFilterPropertyValue> { ["System.Size"] = new(FileFilterPropertyKind.Integer, Integer: 5) }];
        IReadOnlyDictionary<string, FileFilterPropertyValue>?[] missingPropertySide = [equalPropertySides[0], null];
        IReadOnlyDictionary<string, FileFilterPropertyValue>?[] allOfTruePropertySides =
        [
            new Dictionary<string, FileFilterPropertyValue> { ["System.Enabled"] = new(FileFilterPropertyKind.Boolean, Boolean: true) },
            new Dictionary<string, FileFilterPropertyValue> { ["System.Enabled"] = new(FileFilterPropertyKind.Boolean, Boolean: true) }
        ];
        IReadOnlyDictionary<string, FileFilterPropertyValue>?[] allOfMixedPropertySides =
        [allOfTruePropertySides[0], new Dictionary<string, FileFilterPropertyValue> { ["System.Enabled"] = new(FileFilterPropertyKind.Boolean, Boolean: false) }];
        IReadOnlyDictionary<string, FileFilterPropertyValue>?[] allOfMissingPropertySide = [allOfTruePropertySides[0], null];
        IReadOnlyDictionary<string, FileFilterPropertyValue>?[] allOfEmptyArrayPropertySide =
        [new Dictionary<string, FileFilterPropertyValue> { ["System.Enabled"] = new(FileFilterPropertyKind.Array, Items: []) }];
        IReadOnlyDictionary<string, FileFilterPropertyValue>?[] allOfNestedArrayPropertySides =
        [
            new Dictionary<string, FileFilterPropertyValue> { ["System.Enabled"] = new(FileFilterPropertyKind.Array, Items: [new(FileFilterPropertyKind.Boolean, Boolean: true)]) },
            allOfTruePropertySides[0]
        ];
        IReadOnlyDictionary<string, FileFilterPropertyValue>?[] lenEmptyArrayPropertySide =
        [new Dictionary<string, FileFilterPropertyValue> { ["System.Enabled"] = new(FileFilterPropertyKind.Array, Items: []) }];
        IReadOnlyDictionary<string, FileFilterPropertyValue>?[] lenEmptyAndBooleanPropertySides =
        [lenEmptyArrayPropertySide[0], allOfTruePropertySides[0]];
        var allOfProperty = FileFilter.ParseExpression("AllOf(Prop('System.Enabled'))");
        Verify("AllOf property context requires every boolean value", allOfProperty.Matches("a", false, 1, DateTime.UnixEpoch, allOfTruePropertySides)
            && !allOfProperty.Matches("a", false, 1, DateTime.UnixEpoch, allOfMixedPropertySides)
            && !allOfProperty.Matches("a", false, 1, DateTime.UnixEpoch, allOfMissingPropertySide));
        Verify("AllOf property context treats an empty array as true",
            allOfProperty.Matches("a", false, 1, DateTime.UnixEpoch, allOfEmptyArrayPropertySide));
        Verify("AllOf property context treats nested arrays as nonboolean items",
            !allOfProperty.Matches("a", false, 1, DateTime.UnixEpoch, allOfNestedArrayPropertySides));
        var lenPropertyArray = FileFilter.ParseExpression("Len(Prop('System.Enabled')) == 0");
        var lenTwoSidePropertyArray = FileFilter.ParseExpression("Len(Prop('System.Enabled')) == 2");
        Verify("Len property context counts an empty property array as zero",
            lenPropertyArray.Matches("a", false, 1, DateTime.UnixEpoch, lenEmptyArrayPropertySide));
        Verify("Len property context counts direct slots without flattening nested arrays",
            lenTwoSidePropertyArray.Matches("a", false, 1, DateTime.UnixEpoch, lenEmptyAndBooleanPropertySides));
        // 固定native oracleの全62行を、公開parserと既存property context注入へ対応付ける。
        var anyOfFixture = Path.GetFullPath("tests/Fixtures/FolderAnyOfOracle");
        using var anyOfPlan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(anyOfFixture, "case-plan.json")));
        var anyOfGolden = File.ReadLines(Path.Combine(anyOfFixture, "golden.ndjson"))
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToDictionary(row => row.GetProperty("id").GetString()!);
        FileFilterPropertyValue AnyOfValue(JsonElement value) => value.GetProperty("type").GetString() switch
        {
            "none" => FileFilterPropertyValue.None,
            "boolean" => new(FileFilterPropertyKind.Boolean, Boolean: value.GetProperty("value").GetBoolean()),
            "integer" => new(FileFilterPropertyKind.Integer, Integer: value.GetProperty("value").GetInt64()),
            "double" => new(FileFilterPropertyKind.Double, Double: value.GetProperty("value").GetDouble()),
            "timestamp" => new(FileFilterPropertyKind.Timestamp, Integer: value.GetProperty("value").GetInt64()),
            "string" => new(FileFilterPropertyKind.String, Text: value.GetProperty("value").GetString()),
            "array" => new(FileFilterPropertyKind.Array, Items: value.GetProperty("value").EnumerateArray().Select(AnyOfValue).ToArray()),
            _ => throw new InvalidDataException("Unknown AnyOf oracle value")
        };
        using (var writer = new StreamWriter(Path.Combine(work, "any-of-native-oracle.ndjson"), false, new System.Text.UTF8Encoding(false)))
        foreach (var row in anyOfPlan.RootElement.EnumerateArray())
        {
            var id = row.GetProperty("id").GetString()!; var golden = anyOfGolden[id];
            var args = row.GetProperty("args").EnumerateArray().ToArray();
            var arity = row.GetProperty("mode").GetString() == "arity";
            var properties = new AnyOfOracleProperties(args.Length == 0 ? FileFilterPropertyValue.None : AnyOfValue(args[0].GetProperty("value")),
                !arity && args[0].GetProperty("throws").GetBoolean());
            IReadOnlyDictionary<string, FileFilterPropertyValue>?[] context = [properties];
            var expression = "AnyOf(" + (arity ? string.Join(",", Enumerable.Repeat("Prop('fixture')", args.Length)) : "Prop('fixture')") + ")";
            var passed = false;
            try
            {
                var filter = FileFilter.ParseExpression(expression);
                if (arity) passed = golden.GetProperty("error").GetString() == "" && properties.Reads == 0;
                else
                {
                    var expected = golden.GetProperty("result");
                    var query = expected.ValueKind == JsonValueKind.Null ? expression + " == None" : expression + (expected.GetBoolean() ? " == true" : " == false");
                    passed = FileFilter.ParseExpression(query).Matches("a", false, 1, DateTime.UnixEpoch, context) && properties.Reads == 1;
                }
            }
            catch (FormatException) { passed = arity && golden.GetProperty("error").GetString() != "" && properties.Reads == 0; }
            catch (InvalidOperationException error) { passed = !arity && error.Message == golden.GetProperty("error").GetString() && properties.Reads == 1; }
            Verify("AnyOf native oracle " + id, passed);
            WriteJsonLine(writer, json =>
            {
                json.WriteStartObject(); json.WriteString("id", id); json.WriteString("expression", expression); json.WriteBoolean("passed", passed);
                json.WriteNumber("reads", properties.Reads); json.WriteString("scope", "parser and injected property context; not Shell or native exception type"); json.WriteEndObject();
            });
        }
        var anyOfProperty = FileFilter.ParseExpression("AnyOf(Prop('System.Enabled'))");
        Verify("AnyOf property direct true survives mixed nested array", anyOfProperty.Matches("a", false, 1, DateTime.UnixEpoch, allOfNestedArrayPropertySides));
        Verify("AnyOf property nested-only true is not flattened", !anyOfProperty.Matches("a", false, 1, DateTime.UnixEpoch, [allOfNestedArrayPropertySides[0], allOfMixedPropertySides[1]]));
        Verify("AnyOf property empty array is false", !anyOfProperty.Matches("a", false, 1, DateTime.UnixEpoch, allOfEmptyArrayPropertySide));
        Verify("AnyOf property missing and nonboolean is false", !anyOfProperty.Matches("a", false, 1, DateTime.UnixEpoch, equalPropertySides));
        // 原典の63 evaluate行と4 arity行を区別し、製品parserと注入contextへ対応付ける。
        var notEachFixture = Path.GetFullPath("tests/Fixtures/FolderNotEachOracle");
        using var notEachPlan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(notEachFixture, "case-plan.json")));
        var notEachGolden = File.ReadLines(Path.Combine(notEachFixture, "golden.ndjson"))
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToDictionary(row => row.GetProperty("id").GetString()!);
        FileFilterPropertyValue NotEachExpected(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Null => FileFilterPropertyValue.None,
            JsonValueKind.True or JsonValueKind.False => new(FileFilterPropertyKind.Boolean, Boolean: value.GetBoolean()),
            JsonValueKind.Array => new(FileFilterPropertyKind.Array, Items: value.EnumerateArray().Select(NotEachExpected).ToArray()),
            _ => throw new InvalidDataException("Unknown NotEach oracle result")
        };
        using (var writer = new StreamWriter(Path.Combine(work, "not-each-native-oracle.ndjson"), false, new System.Text.UTF8Encoding(false)))
        foreach (var row in notEachPlan.RootElement.EnumerateArray())
        {
            var id = row.GetProperty("id").GetString()!; var golden = notEachGolden[id];
            var args = row.GetProperty("args").EnumerateArray().ToArray();
            var arity = row.GetProperty("mode").GetString() == "arity";
            var inputValue = args.Length == 0 ? FileFilterPropertyValue.None : AnyOfValue(args[0].GetProperty("value"));
            var throws = !arity && args.Length != 0 && args[0].TryGetProperty("throws", out var throwsElement) && throwsElement.GetBoolean();
            var properties = new AnyOfOracleProperties(inputValue,
                throws, NotEachExpected(golden.GetProperty("result")));
            IReadOnlyDictionary<string, FileFilterPropertyValue>?[] context = [properties];
            var expression = "NotEach(" + (arity ? string.Join(",", Enumerable.Repeat("Prop('fixture')", args.Length)) : "Prop('fixture')") + ")";
            var passed = false;
            var lengthReads = 0; var shapeReads = 0;
            try
            {
                var filter = FileFilter.ParseExpression(expression);
                if (arity) passed = golden.GetProperty("error").GetString() == "" && properties.Reads == 0;
                else
                {
                    // 配列同士の==は長さ・順序・None slotを含む全要素の等値。最終truthへの縮約だけでは照合しない。
                    passed = FileFilter.ParseExpression(expression + " == LeftProp('expected')").Matches("a", false, 1, DateTime.UnixEpoch, context)
                        && golden.GetProperty("error").GetString() == "" && properties.Reads == 1 && properties.ExpectedReads == 1;
                    var expected = golden.GetProperty("result");
                    var array = expected.ValueKind == JsonValueKind.Array;
                    var length = array ? expected.GetArrayLength() : expected.ValueKind == JsonValueKind.Null ? 0 : 1;
                    var lengthProperties = new AnyOfOracleProperties(inputValue, false);
                    passed &= FileFilter.ParseExpression("Len(" + expression + ") == " + length).Matches("a", false, 1, DateTime.UnixEpoch, [lengthProperties]);
                    lengthReads = lengthProperties.Reads; passed &= lengthReads == 1;
                    // ==のscalar broadcastやLen(None)==0で、配列型の喪失を見逃さない。
                    // 空／1-slot配列はAllEqualの配列先頭規則でtrue、bool／None scalarと異なる文字列の比較はfalse。
                    if (length <= 1)
                    {
                        var shapeProperties = new AnyOfOracleProperties(inputValue, false);
                        var shapeQuery = (array ? "" : "not ") + "AllEqual(" + expression + ", 'shape-sentinel')";
                        passed &= FileFilter.ParseExpression(shapeQuery).Matches("a", false, 1, DateTime.UnixEpoch, [shapeProperties]);
                        shapeReads = shapeProperties.Reads; passed &= shapeReads == 1;
                    }
                }
            }
            catch (FormatException) { passed = arity && golden.GetProperty("error").GetString() != "" && properties.Reads == 0; }
            catch (InvalidOperationException error) { passed = !arity && error.Message == golden.GetProperty("error").GetString() && properties.Reads == 1 && properties.ExpectedReads == 0; }
            passed &= properties.Reads == golden.GetProperty("evaluated").GetArrayLength();
            Verify("NotEach native oracle " + id, passed);
            WriteJsonLine(writer, json =>
            {
                json.WriteStartObject(); json.WriteString("id", id); json.WriteString("expression", expression); json.WriteBoolean("passed", passed);
                json.WriteNumber("reads", properties.Reads); json.WriteNumber("expectedReads", properties.ExpectedReads); json.WriteNumber("lengthReads", lengthReads); json.WriteNumber("shapeReads", shapeReads);
                json.WritePropertyName("expected"); golden.GetProperty("result").WriteTo(json);
                json.WriteString("scope", arity ? "product parser versus native arity block; no argument evaluation" : "product parser and injected property context versus native direct function; not Shell or native exception type"); json.WriteEndObject();
            });
        }
        Verify("NotEach scalar and logical not composition", FileFilter.ParseExpression("nOtEaCh(false)").Matches("a", false, 1, DateTime.UnixEpoch)
            && FileFilter.ParseExpression("not NotEach(true)").Matches("a", false, 1, DateTime.UnixEpoch)
            && FileFilter.ParseExpression("NotEach(not false) == false").Matches("a", false, 1, DateTime.UnixEpoch)
            && FileFilter.ParseExpression("NotEach(1) == None").Matches("a", false, 1, DateTime.UnixEpoch));
        var notEachFullPathRequiresFolderContext = false;
        try { FileFilter.ParseExpression("NotEach(FullPath)").Matches("a", false, 1, DateTime.UnixEpoch); }
        catch (InvalidOperationException) { notEachFullPathRequiresFolderContext = true; }
        Verify("NotEach propagates FullPath folder context requirement", notEachFullPathRequiresFolderContext);
        // Strlenの失敗計画: Unicode/NUL、scalar文字列化、直下だけの配列map、shape、throw伝播、arityの未評価。
        // fixture読込みは製品parserの例外捕捉より前に行い、壊れたfixtureを期待されたparse failureとして扱わない。
        var strlenFixture = Path.GetFullPath("tests/Fixtures/FolderStrlenOracle");
        var strlenGoldenBytes = File.ReadAllBytes(Path.Combine(strlenFixture, "golden.ndjson"));
        if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(strlenGoldenBytes))
            .Equals("4d31dbab06e96df545b2346481b36721d7cc394d9365a74578ce127aa01ab0d6", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Strlen native oracle SHA-256 mismatch");
        using var strlenPlan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(strlenFixture, "case-plan.json")));
        var strlenRows = strlenPlan.RootElement.EnumerateArray().ToArray();
        var strlenGolden = System.Text.Encoding.UTF8.GetString(strlenGoldenBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
        if (strlenRows.Length != 45 || strlenGolden.Count != 45
            || !strlenRows.Select(row => row.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal).SetEquals(strlenGolden.Keys))
            throw new InvalidDataException("Strlen native oracle must contain the same 45 unique planned cases");
        FileFilterPropertyValue StrlenExpected(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Null => FileFilterPropertyValue.None,
            JsonValueKind.Number => new(FileFilterPropertyKind.Integer, Integer: value.GetInt64()),
            JsonValueKind.Array => new(FileFilterPropertyKind.Array, Items: value.EnumerateArray().Select(StrlenExpected).ToArray()),
            _ => throw new InvalidDataException("Unknown Strlen oracle result")
        };
        using (var writer = new StreamWriter(Path.Combine(work, "strlen-native-oracle.ndjson"), false, new System.Text.UTF8Encoding(false)))
        foreach (var row in strlenRows)
        {
            var id = row.GetProperty("id").GetString()!; var golden = strlenGolden[id];
            var args = row.GetProperty("args").EnumerateArray().ToArray();
            var arity = row.GetProperty("mode").GetString() == "arity";
            if (golden.GetProperty("mode").GetString() != row.GetProperty("mode").GetString())
                throw new InvalidDataException("Strlen native oracle mode mismatch: " + id);
            // 型付きProp注入により、NULもCLI引数や式literalへ変換せず明示長のstringとして渡す。
            var inputValue = args.Length == 0 ? FileFilterPropertyValue.None : AnyOfValue(args[0].GetProperty("value"));
            var throws = args.Length != 0 && args[0].TryGetProperty("throws", out var throwsElement) && throwsElement.GetBoolean();
            var expected = golden.GetProperty("result");
            var properties = new AnyOfOracleProperties(inputValue, throws, arity ? null : StrlenExpected(expected));
            IReadOnlyDictionary<string, FileFilterPropertyValue>?[] context = [properties];
            var expression = "Strlen(" + string.Join(",", Enumerable.Repeat("Prop('fixture')", args.Length)) + ")";
            var expectedError = golden.GetProperty("error").GetString()!;
            var expectedReads = golden.GetProperty("evaluated").GetArrayLength();
            var passed = false; var parsed = false; var lengthReads = 0; var shapeReads = 0;
            var comparisonMatched = false; var lengthMatched = false; var shapeMatched = false;
            var stage = "parse"; var diagnostic = "";
            try
            {
                FileFilter.ParseExpression(expression + " == 0"); parsed = true;
                if (arity) passed = expectedError.Length == 0 && properties.Reads == 0;
                else
                {
                    stage = "expected comparison";
                    comparisonMatched = FileFilter.ParseExpression(expression + " == LeftProp('expected')")
                        .Matches("a", false, 1, DateTime.UnixEpoch, context);
                    passed = comparisonMatched && expectedError.Length == 0 && properties.Reads == 1 && properties.ExpectedReads == 1;
                    if (expectedError.Length == 0)
                    {
                        // ==のscalar broadcastだけではshapeを保証できないため、長さと空/1-slotの型も別に照合する。
                        var array = expected.ValueKind == JsonValueKind.Array;
                        var length = array ? expected.GetArrayLength() : expected.ValueKind == JsonValueKind.Null ? 0 : 1;
                        var lengthProperties = new AnyOfOracleProperties(inputValue, false);
                        stage = "length comparison";
                        lengthMatched = FileFilter.ParseExpression("Len(" + expression + ") == " + length.ToString(CultureInfo.InvariantCulture))
                            .Matches("a", false, 1, DateTime.UnixEpoch, [lengthProperties]);
                        passed &= lengthMatched;
                        lengthReads = lengthProperties.Reads; passed &= lengthReads == 1;
                        if (length <= 1)
                        {
                            var shapeProperties = new AnyOfOracleProperties(inputValue, false);
                            var shapeQuery = (array ? "" : "not ") + "AllEqual(" + expression + ", 'shape-sentinel')";
                            stage = "shape comparison";
                            shapeMatched = FileFilter.ParseExpression(shapeQuery).Matches("a", false, 1, DateTime.UnixEpoch, [shapeProperties]);
                            passed &= shapeMatched;
                            shapeReads = shapeProperties.Reads; passed &= shapeReads == 1;
                        }
                    }
                }
            }
            catch (FormatException error)
            {
                diagnostic = error.Message;
                passed = arity && expectedError.Length != 0 && properties.Reads == 0;
            }
            catch (InvalidOperationException error)
            {
                diagnostic = error.Message;
                passed = !arity && parsed && throws && expectedError.Length != 0 && properties.Reads == 1 && properties.ExpectedReads == 0;
            }
            passed &= properties.Reads == expectedReads;
            if (!passed)
                diagnostic = $"stage={stage}; parsed={parsed}; comparison={comparisonMatched}; length={lengthMatched}; shape={shapeMatched}; reads={properties.Reads}/{expectedReads}; expectedReads={properties.ExpectedReads}; lengthReads={lengthReads}; shapeReads={shapeReads}; nativeError={expectedError}; {diagnostic}";
            Verify("Strlen native oracle " + id, passed, diagnostic);
            WriteJsonLine(writer, json =>
            {
                json.WriteStartObject(); json.WriteString("id", id); json.WriteString("expression", expression); json.WriteBoolean("passed", passed);
                json.WriteBoolean("parsed", parsed); json.WriteString("stage", stage); json.WriteString("diagnostic", diagnostic);
                json.WriteNumber("reads", properties.Reads); json.WriteNumber("expectedReads", expectedReads); json.WriteNumber("expectedValueReads", properties.ExpectedReads);
                json.WriteNumber("lengthReads", lengthReads); json.WriteNumber("shapeReads", shapeReads);
                json.WritePropertyName("expected"); expected.WriteTo(json); json.WriteString("expectedError", expectedError);
                json.WriteString("scope", arity ? "parse only; no argument evaluation; native arity sentinel is not a Strlen value"
                    : "typed Prop preserves NUL; scalar/array equality plus Len and AllEqual shape probes; native exception wording is not required");
                json.WriteEndObject();
            });
        }
        var allEqualProperty = FileFilter.ParseExpression("AllEqual(Prop('System.Size'))");
        Verify("AllEqual property context compares all values", allEqualProperty.Matches("a", false, 1, DateTime.UnixEpoch, equalPropertySides));
        Verify("AllEqual property context rejects mismatch", !allEqualProperty.Matches("a", false, 1, DateTime.UnixEpoch, unequalPropertySides));
        Verify("AllEqual property context treats missing as None", !allEqualProperty.Matches("a", false, 1, DateTime.UnixEpoch, missingPropertySide));
        var allEqualFullPathRequiresFolderContext = false;
        try { FileFilter.ParseExpression("AllEqual(FullPath)").Matches("a", false, 1, DateTime.UnixEpoch); }
        catch (InvalidOperationException) { allEqualFullPathRequiresFolderContext = true; }
        Verify("AllEqual FullPath still requires folder context", allEqualFullPathRequiresFolderContext);
        var rows = File.ReadLines(Path.Combine(fixture, "golden.ndjson")).Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var valuesPath = Path.Combine(work, "property-values.ndjson");
        using var values = new StreamWriter(valuesPath, false, new System.Text.UTF8Encoding(false));
        void Value(string id, FileFilterPropertyValue actual, bool? success = null)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject(); writer.WriteString("id", id); writer.WritePropertyName("value"); WriteValue(writer, actual);
                if (success is { } state) writer.WriteBoolean("serviceSuccess", state); writer.WriteEndObject();
            }
            var json = System.Text.Encoding.UTF8.GetString(stream.ToArray()); values.WriteLine(json);
            using var parsed = JsonDocument.Parse(json);
            Verify(id, JsonElement.DeepEquals(rows[id].GetProperty("value"), parsed.RootElement.GetProperty("value"))
                && (success is null || rows[id].GetProperty("serviceSuccess").GetBoolean() == success));
        }

        nint* pointers = stackalloc nint[3];
        byte* bytes = stackalloc byte[] { 0, 1, 15, 16, 127, 128, 254, 255 };
        foreach (var row in rows.Values.Where(row => row.GetProperty("id").GetString()!.StartsWith("convert-", StringComparison.Ordinal)))
        {
            var id = row.GetProperty("id").GetString()!;
            var variant = new WindowsFileFilterProperties.PropertyVariant { Type = row.GetProperty("vt").GetUInt16() };
            switch (id)
            {
                case "convert-i4": variant.Integer = int.MinValue; break;
                case "convert-ui4": variant.Integer = uint.MaxValue; break;
                case "convert-i8": variant.Integer = long.MinValue; break;
                case "convert-ui8-overflow": variant.Integer = -1; break;
                case "convert-r8": variant.Double = -1.25; break;
                case "convert-bool--1": variant.Integer = -1; break;
                case "convert-bool-1": variant.Integer = 1; break;
                case "convert-bool-2": variant.Integer = 2; break;
            }
            if (id.StartsWith("convert-filetime-", StringComparison.Ordinal)) variant.Integer = long.Parse(id[17..], CultureInfo.InvariantCulture);
            if (id is "convert-wide" or "convert-wide-empty")
            {
                variant.Pointer = Marshal.StringToCoTaskMemUni(id == "convert-wide" ? "日本語😀" : "");
                try { Value(id, WindowsFileFilterProperties.ConvertVariant(variant)); } finally { Marshal.FreeCoTaskMem(variant.Pointer); }
            }
            else if (id == "convert-string-vector")
            {
                for (var index = 0; index < 3; index++) pointers[index] = Marshal.StringToCoTaskMemUni(new[] { "a", "日本語", "" }[index]);
                variant.Count = 3; variant.Vector = (nint)pointers;
                try { Value(id, WindowsFileFilterProperties.ConvertVariant(variant)); } finally { for (var index = 0; index < 3; index++) Marshal.FreeCoTaskMem(pointers[index]); }
            }
            else if (id == "convert-byte-vector")
            {
                variant.Count = 8; variant.Vector = (nint)bytes;
                Value(id, WindowsFileFilterProperties.ConvertVariant(variant));
            }
            else Value(id, WindowsFileFilterProperties.ConvertVariant(variant));
        }
        // Shell が VT_EMPTY を返す環境とも区別し、VT_FILETIME が与えられた境界を固定値で照合する。
        using var typedDates = new StreamWriter(Path.Combine(work, "file-date-typed-values.ndjson"), false, new System.Text.UTF8Encoding(false));
        foreach (var (delta, expected) in new[] { (-10_000_000L, 1844674407369955161L), (-9L, 1844674407370955160L), (-1L, 1844674407370955161L), (0L, 0L), (1L, 0L), (10L, 1L), (10_000_000L, 1000000L), (17_040_672_001_234_561L, 1704067200123456L) })
        {
            var modified = DateTime.UnixEpoch.AddTicks(delta);
            var property = WindowsFileFilterProperties.ConvertVariant(new() { Type = 64, Integer = modified.ToFileTimeUtc() });
            IReadOnlyDictionary<string, FileFilterPropertyValue>?[] context = [new Dictionary<string, FileFilterPropertyValue> { ["fixture"] = property }];
            Verify("controlled FILETIME conversion " + delta, property.Kind == FileFilterPropertyKind.Timestamp && property.Integer == expected);
            Verify("controlled file Date equals FILETIME Prop " + delta, FileFilter.ParseExpression("Date == LeftProp('fixture')").Matches("a", false, 1, modified, context));
            Verify("controlled file Date versus signed literal " + delta, FileFilter.ParseExpression("Date == d'" + modified.ToString("O", CultureInfo.InvariantCulture) + "'").Matches("a", false, 1, modified) == (delta >= 0));
            typedDates.WriteLine("{\"deltaTicks\":\"" + delta.ToString(CultureInfo.InvariantCulture) + "\",\"expectedMicroseconds\":\"" + expected.ToString(CultureInfo.InvariantCulture) + "\",\"actualMicroseconds\":\"" + property.Integer.ToString(CultureInfo.InvariantCulture) + "\"}");
        }
        // 最終判定の再帰と論理演算の浅い判定は別の契約。
        FileFilterPropertyValue Bool(bool value) => new(FileFilterPropertyKind.Boolean, Boolean: value);
        FileFilterPropertyValue Array(params FileFilterPropertyValue[] items) => new(FileFilterPropertyKind.Array, Items: items);
        var allEqualNestedProperty = FileFilter.ParseExpression("AllEqual(Prop('fixture'))");
        FileFilterPropertyValue Nested(string text) => Array(new FileFilterPropertyValue(FileFilterPropertyKind.Integer, Integer: 7), Array(new FileFilterPropertyValue(FileFilterPropertyKind.String, Text: text)));
        IReadOnlyDictionary<string, FileFilterPropertyValue>?[] equalNestedPropertySides =
        [
            new Dictionary<string, FileFilterPropertyValue> { ["fixture"] = Nested("leaf") },
            new Dictionary<string, FileFilterPropertyValue> { ["fixture"] = Nested("leaf") }
        ];
        IReadOnlyDictionary<string, FileFilterPropertyValue>?[] unequalNestedTypePropertySides =
        [
            new Dictionary<string, FileFilterPropertyValue> { ["fixture"] = Nested("leaf") },
            new Dictionary<string, FileFilterPropertyValue> { ["fixture"] = Array(new FileFilterPropertyValue(FileFilterPropertyKind.Double, Double: 7), Array(new FileFilterPropertyValue(FileFilterPropertyKind.String, Text: "leaf"))) }
        ];
        IReadOnlyDictionary<string, FileFilterPropertyValue>?[] emptyArrayPropertySides =
        [new Dictionary<string, FileFilterPropertyValue> { ["fixture"] = Array() }, new Dictionary<string, FileFilterPropertyValue> { ["fixture"] = Array() }];
        IReadOnlyDictionary<string, FileFilterPropertyValue>?[] nonePropertySides =
        [new Dictionary<string, FileFilterPropertyValue> { ["fixture"] = FileFilterPropertyValue.None }, new Dictionary<string, FileFilterPropertyValue> { ["fixture"] = FileFilterPropertyValue.None }];
        Verify("AllEqual property context compares nested arrays and preserves element kinds",
            allEqualNestedProperty.Matches("a", false, 1, DateTime.UnixEpoch, equalNestedPropertySides)
                && !allEqualNestedProperty.Matches("a", false, 1, DateTime.UnixEpoch, unequalNestedTypePropertySides));
        Verify("AllEqual property context compares empty arrays and None values",
            allEqualNestedProperty.Matches("a", false, 1, DateTime.UnixEpoch, emptyArrayPropertySides)
                && allEqualNestedProperty.Matches("a", false, 1, DateTime.UnixEpoch, nonePropertySides));
        var truthCases = new Dictionary<string, FileFilterPropertyValue>
        {
            ["side-1-0-prop"] = FileFilterPropertyValue.None,
            ["convert-bool-0"] = Bool(false), ["convert-bool-1"] = Bool(true),
            ["truth-scalar-integer"] = new(FileFilterPropertyKind.Integer, Integer: 1), ["truth-scalar-string"] = new(FileFilterPropertyKind.String, Text: "true"),
            ["truth-scalar-double"] = new(FileFilterPropertyKind.Double, Double: 1), ["truth-array-integer"] = Array(new FileFilterPropertyValue(FileFilterPropertyKind.Integer, Integer: 1)),
            ["truth-array-true"] = Array(Bool(false), Bool(true)), ["truth-nested-true"] = Array(Array(Bool(true))),
            ["truth-nested-string"] = Array(Array(new FileFilterPropertyValue(FileFilterPropertyKind.String, Text: "true")))
        };
        foreach (var (id, value) in truthCases)
        {
            IReadOnlyDictionary<string, FileFilterPropertyValue>?[] context = [new Dictionary<string, FileFilterPropertyValue> { ["fixture"] = value }];
            Verify(id + " final", FileFilter.ParseExpression("Prop('fixture')").Matches("a", false, 0, DateTime.UnixEpoch, context) == rows[id].GetProperty("containsTrue").GetBoolean());
            if (rows[id].GetProperty("evalAsBool").ValueKind == JsonValueKind.True || rows[id].GetProperty("evalAsBool").ValueKind == JsonValueKind.False)
                Verify(id + " logical", FileFilter.ParseExpression("Prop('fixture') and true").Matches("a", false, 0, DateTime.UnixEpoch, context) == rows[id].GetProperty("evalAsBool").GetBoolean());
            var oracleBool = rows[id].GetProperty("evalAsBool");
            var unknown = oracleBool.ValueKind == JsonValueKind.Null;
            foreach (var (expression, expected) in new[]
            {
                ("(not Prop('fixture')) == None", unknown),
                ("(Prop('fixture') and false) == None", unknown),
                ("(false and Prop('fixture')) == None", false),
                ("(true and Prop('fixture')) == None", unknown),
                ("(Prop('fixture') or false) == None", false),
                ("(Prop('fixture') or Prop('fixture')) == None", unknown),
                ("not Prop('fixture')", !unknown && !oracleBool.GetBoolean())
            }) Verify(id + " nullable " + expression, FileFilter.ParseExpression(expression).Matches("a", false, 0, DateTime.UnixEpoch, context) == expected);
        }
        foreach (var count in new[] { 1, 2, 3 })
            for (var mask = 0; mask < 1 << count; mask++)
                foreach (var function in new[] { "prop", "leftprop", "middleprop", "rightprop" })
                {
                    var id = $"side-{count}-{mask}-{function}";
                    IReadOnlyDictionary<string, FileFilterPropertyValue>?[] context = Enumerable.Range(0, count).Select(index =>
                        (mask & 1 << index) == 0 ? null : (IReadOnlyDictionary<string, FileFilterPropertyValue>)new Dictionary<string, FileFilterPropertyValue>
                        { ["System.Size"] = new(FileFilterPropertyKind.Integer, Integer: (index + 1) * 10) }).ToArray();
                    foreach (var candidate in new[] { "None", "10", "20", "30" })
                    {
                        bool Expected(JsonElement value) => value.GetProperty("type").GetString() == "array"
                            ? value.GetProperty("value").EnumerateArray().Any(Expected)
                            : candidate == "None" ? value.GetProperty("type").GetString() == "none"
                            : value.GetProperty("type").GetString() == "integer" && value.GetProperty("value").GetString() == candidate;
                        Verify(id + " == " + candidate, FileFilter.ParseExpression(function + "('System.Size') == " + candidate)
                            .Matches("a", false, 0, DateTime.UnixEpoch, context) == Expected(rows[id].GetProperty("value")));
                    }
                }
        IReadOnlyDictionary<string, FileFilterPropertyValue>?[] nestedContext =
        [
            new Dictionary<string, FileFilterPropertyValue> { ["fixture"] = Array(Array(Bool(true))) },
            new Dictionary<string, FileFilterPropertyValue> { ["fixture"] = Array(Bool(true)) }
        ];
        Verify("whole-array equality requires scalar true", !FileFilter.ParseExpression("LeftProp('fixture') == RightProp('fixture')").Matches("a", false, 0, DateTime.UnixEpoch, nestedContext));
        foreach (var count in new[] { 2, 3 })
        {
            var roots = Enumerable.Range(0, count).Select(index => Path.Combine(work, "validator-errors-" + count, "side-" + index)).ToArray();
            foreach (var root in roots) { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "unsafe.bin"), "unsafe"); File.WriteAllText(Path.Combine(root, "safe.bin"), "safe"); }
            var options = new DirectoryComparisonOptions
            {
                ExcludePatterns = ["*"], EntryTypeValidator = (path, _) =>
                {
                    if (path == Path.Combine(roots[0], "unsafe.bin")) throw new IOException("controlled nonregular IOException");
                    if (path == Path.Combine(roots[^1], "unsafe.bin")) throw new UnauthorizedAccessException("controlled nonregular UnauthorizedAccessException");
                }
            };
            var comparison = count == 2 ? DirectoryComparer.CompareAsync(roots[0], roots[^1], options)
                : DirectoryComparer.CompareThreeWayAsync(roots[0], roots[1], roots[^1], options);
            pump(comparison); var model = comparison.Result;
            Verify("controlled validator " + count + " exclusion preserves errors", model.HasDifferences && model.Entries.Count == 1
                && model.Entries[0] is { RelativePath: "unsafe.bin", Status: DirectoryDifferenceKind.Error, IsFiltered: false,
                    LeftState: { IsFiltered: false, ScanState: DirectoryScanState.Error }, RightState: { IsFiltered: false, ScanState: DirectoryScanState.Error } }
                && model.AllEntries.Single(entry => entry.RelativePath == "safe.bin").IsFiltered);
            using var stream = File.Create(Path.Combine(work, "controlled-validator-" + count + ".json")); using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject(); writer.WriteString("qualification", "controlled EntryTypeValidator injection; not an OS FIFO"); FolderCommands.WriteModel(writer, model); writer.WriteEndObject();
        }
        HeadlessFolderOptimizerChecks.Run(output, check, pump);
        HeadlessFolderDateStrChecks.Run(output, check, pump);
        if (!OperatingSystem.IsWindows())
        {
            try { WindowsFileFilterProperties.Instance.ValidateNames(["System.Size"]); Verify("unsupported OS rejected", false); }
            catch (PlatformNotSupportedException) { Verify("unsupported OS rejected", true); }
            return;
        }
        var input = Path.Combine(work, "input"); ZipFile.ExtractToDirectory(Path.Combine(fixture, "input.zip"), input);
        foreach (var file in Directory.EnumerateFiles(input)) File.SetLastWriteTimeUtc(file, DateTime.UnixEpoch.AddTicks(17000000001234567));
        string[] mixed = ["System.Size", "System.FileName", "System.FileExtension", "System.DateModified", "Hash.MD5", "Hash.SHA1", "Hash.SHA256"];
        foreach (var name in new[] { "empty.bin", "unicode-日本語.bin", "boundary.bin", "absent.bin" })
            foreach (var names in new[] { mixed, mixed[4..] })
            {
                var success = WindowsFileFilterProperties.Instance.Read(Path.Combine(input, name), names, default, out var actual);
                for (var index = 0; index < names.Length; index++) Value("file-" + name + (names.Length == 7 ? "-mixed-" : "-hashonly-") + names[index], actual[index], success);
            }
        foreach (var count in new[] { 2, 3 })
        {
            var roots = Enumerable.Range(0, count).Select(index => Path.Combine(work, "gui-" + count, "side-" + index)).ToArray();
            foreach (var root in roots) { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "keep.bin"), "same"); }
            Directory.CreateDirectory(Path.Combine(roots[^1], "blocked", "deep")); File.WriteAllText(Path.Combine(roots[^1], "blocked", "deep", "never.bin"), "no scan");
            var filter = Path.Combine(work, "gui-" + count + ".flt"); File.WriteAllText(filter, "def: include\nde: LeftProp('System.FileName') == None\nfe: Prop('System.Size') == 99\n");
            pane.ApplyProject(new() { Mode = "Folder", LeftPath = roots[0], BasePath = count == 3 ? roots[1] : "", RightPath = roots[^1], Recursive = true, FileFilterPath = filter });
            Task? pending = null; var previous = pane.TextSaveTaskObserved;
            pane.TextSaveTaskObserved = (route, task) => { if (route == "path-compare") pending = task; previous?.Invoke(route, task); };
            try { pane.CompareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); if (pending is null) throw new InvalidOperationException("Compare button did not start comparison."); pump(pending); }
            finally { pane.TextSaveTaskObserved = previous; }
            Dispatcher.UIThread.RunJobs();
            var model = (DirectoryComparisonResult)pane.FolderOptionsAdoptedModel!;
            Verify("GUI " + count + " shared pruning", model.Entries.Select(entry => entry.RelativePath).SequenceEqual(["keep.bin"])
                && model.AllEntries.Single(entry => entry.RelativePath == "blocked") is { IsFiltered: true, RightState.ScanState: DirectoryScanState.Unscanned }
                && !model.AllEntries.Any(entry => entry.RelativePath.StartsWith("blocked/", StringComparison.Ordinal)));
            using (var stream = File.Create(Path.Combine(work, "gui-" + count + ".json")))
            using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); FolderCommands.WriteModel(writer, model); writer.WriteEndObject(); }
            screenshot("folder-properties-" + count + ".png");
            var adopted = pane.FolderOptionsAdoptedModel;
            var displayed = pane.FolderOptionsRawRows.Items.Cast<object>().ToArray();
            var previousObserver = WindowsFileFilterProperties.Instance.HashChunkReadForVerification;
            using var started = new ManualResetEventSlim();
            WindowsFileFilterProperties.Instance.HashChunkReadForVerification = token =>
            {
                started.Set();
                if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("Hash cancel gate was not released.");
                token.ThrowIfCancellationRequested();
            };
            File.WriteAllText(filter, "def: include\nfe: Prop('Hash.SHA256') == ''\n");
            pending = null; pane.TextSaveTaskObserved = (route, task) => { if (route == "path-compare") pending = task; previous?.Invoke(route, task); };
            try
            {
                pane.CompareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!started.IsSet && clock.Elapsed < TimeSpan.FromSeconds(5)) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
                Verify("GUI " + count + " hash runs outside dispatcher", started.IsSet && pending is { IsCompleted: false });
                var heartbeat = false; Dispatcher.UIThread.Post(() => heartbeat = true); Dispatcher.UIThread.RunJobs();
                Verify("GUI " + count + " dispatcher responds during hash", heartbeat);
                pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                try { pump(pending!); } catch (OperationCanceledException) { }
                Dispatcher.UIThread.RunJobs();
                Verify("GUI " + count + " cancel preserves adopted display", pending!.IsCompleted && ReferenceEquals(adopted, pane.FolderOptionsAdoptedModel)
                    && ((DirectoryComparisonResult)adopted!).Entries.Select(entry => entry.RelativePath).SequenceEqual(["keep.bin"])
                    && pane.FolderOptionsRawRows.Items.Cast<object>().SequenceEqual(displayed) && pane.CompareButton.IsEnabled);
            }
            finally { WindowsFileFilterProperties.Instance.HashChunkReadForVerification = previousObserver; pane.TextSaveTaskObserved = previous; }
            started.Reset(); using var release = new ManualResetEventSlim();
            WindowsFileFilterProperties.Instance.HashChunkReadForVerification = token =>
            {
                started.Set(); if (!release.Wait(TimeSpan.FromSeconds(10), token)) throw new TimeoutException("Hash stale gate was not released.");
            };
            pending = null; pane.TextSaveTaskObserved = (route, task) => { if (route == "path-compare") pending = task; previous?.Invoke(route, task); };
            try
            {
                pane.CompareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!started.IsSet && clock.Elapsed < TimeSpan.FromSeconds(5)) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
                Verify("GUI " + count + " stale candidate hash started", started.IsSet && pending is { IsCompleted: false });
                pane.RightPath.Text = roots[0]; release.Set(); pump(pending!); Dispatcher.UIThread.RunJobs();
                Verify("GUI " + count + " settings change during hash rejects candidate", ReferenceEquals(adopted, pane.FolderOptionsAdoptedModel)
                    && pane.FolderOptionsRawRows.Items.Cast<object>().SequenceEqual(displayed) && pane.CompareButton.IsEnabled);
            }
            finally { release.Set(); WindowsFileFilterProperties.Instance.HashChunkReadForVerification = previousObserver; pane.TextSaveTaskObserved = previous; }
            pane.RightPath.Text = roots[^1]; File.WriteAllText(filter, "def: include\nde: LeftProp('System.FileName') == None\nfe: Prop('System.Size') == 99\n");
            pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
            Verify("GUI " + count + " comparison recovers after stale candidate", !ReferenceEquals(adopted, pane.FolderOptionsAdoptedModel)
                && ((DirectoryComparisonResult)pane.FolderOptionsAdoptedModel!).Entries.Select(entry => entry.RelativePath).SequenceEqual(["keep.bin"]) && pane.CompareButton.IsEnabled);
            screenshot("folder-properties-recovered-" + count + ".png");
            var hashStarts = 0;
            WindowsFileFilterProperties.Instance.HashChunkReadForVerification = _ => Interlocked.Increment(ref hashStarts);
            try
            {
                File.WriteAllText(filter, "def: include\nfe: Prop('Hash.SHA256') == '' or Prop('System.FileName') == ''\n");
                pane.ApplyProject(new() { Mode = "Folder", LeftPath = roots[0], BasePath = count == 3 ? roots[1] : "", RightPath = roots[^1], Recursive = true, FileFilterPath = filter, ExcludedPaths = "blocked" });
                CompareByButton();
                var includedStarts = hashStarts;
                Verify("GUI " + count + " property observer sees included files", includedStarts == count);
                hashStarts = 0;
                pane.ApplyProject(new() { Mode = "Folder", LeftPath = roots[0], BasePath = count == 3 ? roots[1] : "", RightPath = roots[^1], Recursive = true, FileFilterPath = filter, ExcludedPaths = "*" });
                CompareByButton();
                var excluded = (DirectoryComparisonResult)pane.FolderOptionsAdoptedModel!;
                var excludedAll = excluded.Entries.Count == 0 && excluded.AllEntries.Count == 2 && excluded.AllEntries.All(entry => entry.IsFiltered)
                    && excluded.AllEntries.Single(entry => entry.RelativePath == "blocked").RightState?.ScanState == DirectoryScanState.Unscanned;
                using (var stream = File.Create(Path.Combine(work, "exclude-observations-" + count + ".json")))
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject(); writer.WriteNumber("sides", count); writer.WriteNumber("includedHashStarts", includedStarts);
                    writer.WriteNumber("excludedHashStarts", hashStarts); writer.WriteBoolean("allFilteredNoRecursion", excludedAll); writer.WriteEndObject();
                }
                Verify("GUI " + count + " exclude short circuits property reads", hashStarts == 0 && excludedAll && pane.CompareButton.IsEnabled);
            }
            finally { WindowsFileFilterProperties.Instance.HashChunkReadForVerification = previousObserver; }
            var previousReadObserver = WindowsFileFilterProperties.Instance.ReadRequestedForVerification;
            var calls = new List<(string Path, string[] Names)>(); var lazyHashStarts = 0;
            WindowsFileFilterProperties.Instance.ReadRequestedForVerification = (path, names) => calls.Add((path, names.ToArray()));
            WindowsFileFilterProperties.Instance.HashChunkReadForVerification = _ => Interlocked.Increment(ref lazyHashStarts);
            using var lazyEvidence = new StreamWriter(Path.Combine(work, "lazy-property-" + count + ".ndjson"), false, new System.Text.UTF8Encoding(false));
            try
            {
                const string hash = "Hash.SHA256";
                var leftAttributes = File.GetAttributes(Path.Combine(roots[0], "keep.bin"));
                var rightAttributes = File.GetAttributes(Path.Combine(roots[^1], "keep.bin"));
                var localDate = DateTime.UnixEpoch.Add(TimeZoneInfo.Local.GetUtcOffset(DateTime.Now)).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
                foreach (var (id, expression, expectedHashes, expectedNames, expectedSides, included) in new[]
                {
                    ("date-literal-long", "d'2024-01-01T00:00:00.12345699Z' == d'2024-01-01T00:00:00.123456Z'", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), true),
                    ("date-literal-short-clock", "d'2024-01-01T0:0:0.12345699Z' == d'2024-01-01T00:00:00.123456Z'", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), true),
                    ("date-literal-short-whole-second", "d'2024-01-01T0:0:0Z' == d'2024-01-01T00:00:00Z'", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), true),
                    ("date-literal-local-offset", "d'" + localDate + "' == d'1970-01-01T00:00:00Z'", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), true),
                    ("date-literal-explicit-upper-range", "d'9999-12-31T23:00:00+01:00' == d'9999-12-31T22:00:00Z'", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), true),
                    ("date-literal-short-adjacent", "d'2024-01-01T00:0:0.12345799Z' != d'2024-01-01T00:00:00.123456Z'", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), true),
                    ("date-literal-zone", "d'2024-01-01T09:00:00.12345699+09:00' == d'2024-01-01T00:00:00.123456Z'", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), true),
                    ("date-literal-year-boundary", "d'2023-12-31T23:59:59.999999999Z' == d'2023-12-31T23:59:59.999999Z'", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), true),
                    // 固定consumer oracle search-control-998/999: containsの空needleは空matchなのでfalse。
                    ("file-attributes", "LeftAttributes == " + (long)leftAttributes + " and RightAttributes == " + (long)rightAttributes + " and MiddleAttrStr != None and AttrStr contains ''", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), false),
                    ("file-attributes-short-circuit", "LeftAttrStr != None or Prop('Hash.SHA256') == ''", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), true),
                    ("file-attributes-prop", "LeftAttributes == " + (long)leftAttributes + " and RightProp('System.Size') == 4", 0, new[] { "System.Size" }, new[] { count - 1 }, true),
                    ("side-metadata-only", "LeftSize == 4 and MiddleSize == 4 and RightName == 'keep.bin'", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), true),
                    ("side-metadata-double", "LeftSize == 4.0000000000000001 and MiddleSize == 4.0000000000000001 and RightSize == 4.0000000000000001", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), true),
                    ("side-metadata-unit", "LeftSize == 0.004kb and MiddleSize == 0.004kb and RightSize == 0.004kb", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), true),
                    ("side-metadata-short-circuit", "LeftSize == 4 or Prop('Hash.SHA256') == ''", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), true),
                    ("side-metadata-prop", "LeftSize == RightProp('System.Size')", 0, new[] { "System.Size" }, new[] { count - 1 }, true),
                    ("and-short-circuit", "false and Prop('Hash.SHA256') == ''", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), false),
                    ("or-short-circuit", "true or Prop('Hash.SHA256') == ''", 0, System.Array.Empty<string>(), System.Array.Empty<int>(), true),
                    ("left-cache", "LeftProp('Hash.SHA256') != '' and LeftProp('Hash.SHA256') == LeftProp('Hash.SHA256')", 1, new[] { hash }, new[] { 0 }, true),
                    ("right-cache", "RightProp('Hash.SHA256') != '' and RightProp('Hash.SHA256') == RightProp('Hash.SHA256')", 1, new[] { hash }, new[] { count - 1 }, true),
                    ("all-cache", "Prop('Hash.SHA256') != '' and Prop('Hash.SHA256') == Prop('Hash.SHA256')", count, Enumerable.Repeat(hash, count).ToArray(), Enumerable.Range(0, count).ToArray(), true),
                    ("unused-name", "LeftProp('System.Size') == 4 and (true or RightProp('Hash.SHA256') == '')", 0, new[] { "System.Size" }, new[] { 0 }, true),
                    ("mixed-cache", "LeftProp('System.Size') == 4 and LeftProp('Hash.SHA256') == LeftProp('Hash.SHA256')", 1, new[] { "System.Size", hash }, new[] { 0, 0 }, true),
                    ("mixed-new-comparison", "LeftProp('System.Size') == 4 and LeftProp('Hash.SHA256') == LeftProp('Hash.SHA256')", 1, new[] { "System.Size", hash }, new[] { 0, 0 }, true)
                })
                {
                    calls.Clear(); lazyHashStarts = 0;
                    File.WriteAllText(filter, "def: exclude\nfe: " + expression + "\n");
                    pane.ApplyProject(new() { Mode = "Folder", LeftPath = roots[0], BasePath = count == 3 ? roots[1] : "", RightPath = roots[^1], Recursive = true, FileFilterPath = filter, ExcludedPaths = "blocked" });
                    CompareByButton();
                    var lazyModel = (DirectoryComparisonResult)pane.FolderOptionsAdoptedModel!;
                    RecordLazy(id);
                    Verify("GUI " + count + " lazy " + id, lazyHashStarts == expectedHashes && calls.Count == expectedNames.Length
                        && calls.All(call => call.Names.Length == 1) && calls.SelectMany(call => call.Names).Order(StringComparer.Ordinal).SequenceEqual(expectedNames.Order(StringComparer.Ordinal))
                        && calls.Select(call => call.Path).Order(StringComparer.Ordinal).SequenceEqual(expectedSides.Select(side => Path.Combine(roots[side], "keep.bin")).Order(StringComparer.Ordinal))
                        && lazyModel.Entries.Count == (included ? 1 : 0) && (!included || lazyModel.Entries[0].RelativePath == "keep.bin") && pane.CompareButton.IsEnabled);
                }
                File.WriteAllBytes(Path.Combine(roots[^1], "only-right.bin"), [1]); calls.Clear(); lazyHashStarts = 0;
                File.WriteAllText(filter, "def: exclude\nfe: LeftProp('Hash.SHA256') == None\n");
                pane.ApplyProject(new() { Mode = "Folder", LeftPath = roots[0], BasePath = count == 3 ? roots[1] : "", RightPath = roots[^1], Recursive = true, FileFilterPath = filter, ExcludedPaths = "blocked;keep.bin" });
                CompareByButton(); RecordLazy("absent-side");
                var absent = (DirectoryComparisonResult)pane.FolderOptionsAdoptedModel!;
                Verify("GUI " + count + " lazy absent side not read", calls.Count == 0 && lazyHashStarts == 0 && absent.Entries.Count == 1 && absent.Entries[0].RelativePath == "only-right.bin");
            }
            finally
            {
                WindowsFileFilterProperties.Instance.ReadRequestedForVerification = previousReadObserver;
                WindowsFileFilterProperties.Instance.HashChunkReadForVerification = previousObserver;
            }
            void RecordLazy(string id)
            {
                using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject(); writer.WriteString("id", id); writer.WriteNumber("hashStarts", lazyHashStarts); writer.WriteStartArray("reads");
                    foreach (var call in calls) { writer.WriteStartObject(); writer.WriteString("path", call.Path); writer.WriteStartArray("names"); foreach (var name in call.Names) writer.WriteStringValue(name); writer.WriteEndArray(); writer.WriteEndObject(); }
                    writer.WriteEndArray(); writer.WriteEndObject();
                }
                lazyEvidence.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray())); lazyEvidence.Flush();
            }
        }
        var previousAllEqualReadObserver = WindowsFileFilterProperties.Instance.ReadRequestedForVerification;
        var previousAllEqualHashObserver = WindowsFileFilterProperties.Instance.HashChunkReadForVerification;
        var allEqualCalls = new List<(string Path, string[] Names)>(); var allEqualHashStarts = 0;
        WindowsFileFilterProperties.Instance.ReadRequestedForVerification = (path, names) => allEqualCalls.Add((path, names.ToArray()));
        WindowsFileFilterProperties.Instance.HashChunkReadForVerification = _ => Interlocked.Increment(ref allEqualHashStarts);
        try
        {
            foreach (var count in new[] { 2, 3 })
            {
                var allEqualRoots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "all-equal-" + count, "side-" + side)).ToArray();
                foreach (var root in allEqualRoots) Directory.CreateDirectory(root);
                var sameTimestamp = DateTime.UnixEpoch.AddTicks(17_000_000_001_234_567);
                foreach (var root in allEqualRoots)
                {
                    var samePath = Path.Combine(root, "same.bin"); File.WriteAllBytes(samePath, [1, 2, 3, 4]); File.SetLastWriteTimeUtc(samePath, sameTimestamp);
                }
                for (var side = 0; side < count; side++)
                {
                    var differentPath = Path.Combine(allEqualRoots[side], "different.bin"); File.WriteAllBytes(differentPath, Enumerable.Repeat((byte)(side + 1), side + 2).ToArray());
                    File.SetLastWriteTimeUtc(differentPath, sameTimestamp.AddMinutes(side + 1));
                }
                var missingSide = count == 2 ? count - 1 : 1;
                for (var side = 0; side < count; side++)
                    if (side != missingSide)
                    {
                        var missingPath = Path.Combine(allEqualRoots[side], "missing.bin"); File.WriteAllBytes(missingPath, [9, 8, 7]); File.SetLastWriteTimeUtc(missingPath, sameTimestamp);
                    }
                var allEqualFilter = Path.Combine(work, "all-equal-" + count + ".flt");
                DirectoryComparisonResult CompareAllEqual(string expression)
                {
                    File.WriteAllText(allEqualFilter, "def: exclude\nfe: " + expression + "\n");
                    pane.ApplyProject(new() { Mode = "Folder", LeftPath = allEqualRoots[0], BasePath = count == 3 ? allEqualRoots[1] : "", RightPath = allEqualRoots[^1], Recursive = true, FileFilterPath = allEqualFilter });
                    CompareByButton();
                    return (DirectoryComparisonResult)pane.FolderOptionsAdoptedModel!;
                }
                void SaveAllEqualModel(string id, DirectoryComparisonResult model)
                {
                    using var stream = File.Create(Path.Combine(work, "all-equal-" + count + "-" + id + ".json"));
                    using var writer = new Utf8JsonWriter(stream); writer.WriteStartObject(); writer.WriteString("id", id);
                    writer.WriteNumber("hashStarts", allEqualHashStarts); writer.WriteStartArray("propertyReads");
                    foreach (var call in allEqualCalls)
                    {
                        writer.WriteStartObject(); writer.WriteString("path", call.Path); writer.WriteStartArray("names");
                        foreach (var name in call.Names) writer.WriteStringValue(name);
                        writer.WriteEndArray(); writer.WriteEndObject();
                    }
                    writer.WriteEndArray(); FolderCommands.WriteModel(writer, model); writer.WriteEndObject();
                }
                bool IsFiltered(DirectoryComparisonResult model, string path, bool expected)
                    => model.AllEntries.Single(entry => entry.RelativePath == path).IsFiltered == expected;

                allEqualCalls.Clear(); allEqualHashStarts = 0;
                var sizeModel = CompareAllEqual("AllEqual(Size)"); SaveAllEqualModel("size", sizeModel);
                Verify("GUI " + count + " AllEqual Size shares the side array", sizeModel.Entries.Select(entry => entry.RelativePath).SequenceEqual(["same.bin"])
                    && IsFiltered(sizeModel, "same.bin", false) && IsFiltered(sizeModel, "different.bin", true) && IsFiltered(sizeModel, "missing.bin", true)
                    && allEqualCalls.Count == 0 && allEqualHashStarts == 0);

                foreach (var function in new[] { "ToLower", "ToUpper" })
                {
                    var casingModel = CompareAllEqual("AllEqual(" + function + "(Size))"); SaveAllEqualModel("casing-" + function, casingModel);
                    Verify("GUI " + count + " " + function + " .flt metadata selection", casingModel.Entries.Select(entry => entry.RelativePath).SequenceEqual(["same.bin"])
                        && IsFiltered(casingModel, "same.bin", false) && IsFiltered(casingModel, "different.bin", true) && IsFiltered(casingModel, "missing.bin", true));
                    var casingWorkspace = Path.Combine(work, "casing-workspace-" + count + "-" + function + ".diffbeacon");
                    pump(WorkspaceStore.SaveAsync(casingWorkspace, pane.CaptureProject()));
                    var loadCasing = WorkspaceStore.LoadAsync(casingWorkspace); pump(loadCasing);
                    pane.ApplyProject(loadCasing.GetAwaiter().GetResult()); CompareByButton();
                    var restoredCasing = (DirectoryComparisonResult)pane.FolderOptionsAdoptedModel!;
                    Verify("GUI " + count + " " + function + " workspace reload .flt", restoredCasing.Entries.Select(entry => entry.RelativePath).SequenceEqual(["same.bin"])
                        && IsFiltered(restoredCasing, "different.bin", true) && IsFiltered(restoredCasing, "missing.bin", true));
                }

                allEqualCalls.Clear(); allEqualHashStarts = 0;
                var dateModel = CompareAllEqual("AllEqual(Date)"); SaveAllEqualModel("date", dateModel);
                Verify("GUI " + count + " AllEqual Date compares snapshot timestamps", dateModel.Entries.Select(entry => entry.RelativePath).SequenceEqual(["same.bin"])
                    && IsFiltered(dateModel, "same.bin", false) && IsFiltered(dateModel, "different.bin", true) && IsFiltered(dateModel, "missing.bin", true)
                    && allEqualCalls.Count == 0 && allEqualHashStarts == 0);

                allEqualCalls.Clear(); allEqualHashStarts = 0;
                var sizePropertyModel = CompareAllEqual("AllEqual(Prop('System.Size'))"); SaveAllEqualModel("system-size", sizePropertyModel);
                Verify("GUI " + count + " AllEqual System.Size array", sizePropertyModel.Entries.Select(entry => entry.RelativePath).SequenceEqual(["same.bin"])
                    && IsFiltered(sizePropertyModel, "same.bin", false) && IsFiltered(sizePropertyModel, "different.bin", true) && IsFiltered(sizePropertyModel, "missing.bin", true)
                    && allEqualCalls.Count > 0 && allEqualHashStarts == 0);

                allEqualCalls.Clear(); allEqualHashStarts = 0;
                var datePropertyModel = CompareAllEqual("AllEqual(Prop('System.DateModified'))"); SaveAllEqualModel("system-date-modified", datePropertyModel);
                Verify("GUI " + count + " AllEqual System.DateModified timestamp array", datePropertyModel.Entries.Select(entry => entry.RelativePath).SequenceEqual(["same.bin"])
                    && IsFiltered(datePropertyModel, "same.bin", false) && IsFiltered(datePropertyModel, "different.bin", true) && IsFiltered(datePropertyModel, "missing.bin", true)
                    && allEqualCalls.Any(call => call.Names.Contains("System.DateModified", StringComparer.Ordinal)) && allEqualHashStarts == 0);

                allEqualCalls.Clear(); allEqualHashStarts = 0;
                var hashModel = CompareAllEqual("AllEqual(Prop('Hash.SHA256'))"); SaveAllEqualModel("sha256", hashModel);
                Verify("GUI " + count + " AllEqual hashes every side", hashModel.Entries.Select(entry => entry.RelativePath).SequenceEqual(["same.bin"])
                    && IsFiltered(hashModel, "same.bin", false) && IsFiltered(hashModel, "different.bin", true) && IsFiltered(hashModel, "missing.bin", true)
                    && allEqualCalls.Count > 0 && allEqualHashStarts > 0);

                allEqualCalls.Clear(); allEqualHashStarts = 0;
                var arrayFirstModel = CompareAllEqual("AllEqual(Name, Prop('Hash.SHA256'))"); SaveAllEqualModel("array-first", arrayFirstModel);
                Verify("GUI " + count + " AllEqual array-first skips its hash tail", arrayFirstModel.Entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal)
                    .SequenceEqual(["different.bin", "same.bin"]) && IsFiltered(arrayFirstModel, "missing.bin", true)
                    && allEqualCalls.Count == 0 && allEqualHashStarts == 0);

                allEqualCalls.Clear(); allEqualHashStarts = 0;
                var shortCircuitModel = CompareAllEqual("AllEqual(1, 2, Prop('Hash.SHA256'))"); SaveAllEqualModel("scalar-short-circuit", shortCircuitModel);
                Verify("GUI " + count + " AllEqual mismatch short-circuits before Hash", shortCircuitModel.Entries.Count == 0
                    && allEqualHashStarts == 0 && allEqualCalls.Count == 0 && pane.CompareButton.IsEnabled);

                allEqualCalls.Clear(); allEqualHashStarts = 0;
                var strictModel = CompareAllEqual("@ci AllEqual('a', 'A')"); SaveAllEqualModel("strict-case", strictModel);
                Verify("GUI " + count + " AllEqual ignores case directive", strictModel.Entries.Count == 0 && allEqualHashStarts == 0 && pane.CompareButton.IsEnabled);
                screenshot("folder-all-equal-" + count + ".png");
            }
        }
        finally
        {
            WindowsFileFilterProperties.Instance.ReadRequestedForVerification = previousAllEqualReadObserver;
            WindowsFileFilterProperties.Instance.HashChunkReadForVerification = previousAllEqualHashObserver;
        }
        foreach (var count in new[] { 2, 3 })
        {
            var allOfRoots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "all-of-" + count, "side-" + side)).ToArray();
            foreach (var root in allOfRoots) Directory.CreateDirectory(root);
            for (var side = 0; side < count; side++)
            {
                File.WriteAllBytes(Path.Combine(allOfRoots[side], "same.bin"), [1, 2, 3, 4]);
                File.WriteAllBytes(Path.Combine(allOfRoots[side], "different.bin"), Enumerable.Repeat((byte)(side + 1), side + 2).ToArray());
                File.WriteAllBytes(Path.Combine(allOfRoots[side], "zero.bin"), []);
                File.WriteAllBytes(Path.Combine(allOfRoots[side], "partial.bin"), side == count - 1 ? [] : [7, 8]);
                if (side != 1) File.WriteAllBytes(Path.Combine(allOfRoots[side], "missing.bin"), [9, 8, 7]);
            }
            var allOfFilter = Path.Combine(work, "all-of-" + count + ".flt");
            var anyOfFilter = Path.Combine(work, "any-of-" + count + ".flt");
            var notEachFilter = Path.Combine(work, "not-each-" + count + ".flt");
            DirectoryComparisonResult CompareFolderFunction(string expression, string filterPath)
            {
                File.WriteAllText(filterPath, "def: exclude\nfe: " + expression + "\n");
                pane.ApplyProject(new() { Mode = "Folder", LeftPath = allOfRoots[0], BasePath = count == 3 ? allOfRoots[1] : "", RightPath = allOfRoots[^1], Recursive = true, FileFilterPath = filterPath });
                CompareByButton();
                return (DirectoryComparisonResult)pane.FolderOptionsAdoptedModel!;
            }
            DirectoryComparisonResult CompareAllOf(string expression) => CompareFolderFunction(expression, allOfFilter);
            DirectoryComparisonResult CompareAnyOf(string expression) => CompareFolderFunction(expression, anyOfFilter);
            DirectoryComparisonResult CompareNotEach(string expression) => CompareFolderFunction(expression, notEachFilter);
            void SaveFunctionModel(string family, string id, DirectoryComparisonResult model)
            {
                using var stream = File.Create(Path.Combine(work, family + "-" + count + "-" + id + ".json"));
                using var writer = new Utf8JsonWriter(stream); writer.WriteStartObject(); writer.WriteString("id", id); FolderCommands.WriteModel(writer, model); writer.WriteEndObject();
            }
            void SaveAllOfModel(string id, DirectoryComparisonResult model) => SaveFunctionModel("all-of", id, model);
            void SaveAnyOfModel(string id, DirectoryComparisonResult model) => SaveFunctionModel("any-of", id, model);
            void SaveNotEachModel(string id, DirectoryComparisonResult model) => SaveFunctionModel("not-each", id, model);
            bool IsFiltered(DirectoryComparisonResult model, string path, bool expected)
                => model.AllEntries.Single(entry => entry.RelativePath == path).IsFiltered == expected;

            var anySizeModel = CompareAnyOf("AnyOf(Size > 0)"); SaveAnyOfModel("size", anySizeModel);
            Verify("GUI " + count + " AnyOf Size includes partial and missing side",
                anySizeModel.Entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal)
                    .SequenceEqual(["different.bin", "missing.bin", "partial.bin", "same.bin"]) && pane.CompareButton.IsEnabled);
            var anyExistsModel = CompareAnyOf("AnyOf(Exists)"); SaveAnyOfModel("exists", anyExistsModel);
            Verify("GUI " + count + " AnyOf Exists accepts missing side", anyExistsModel.Entries.Count == 5 && IsFiltered(anyExistsModel, "missing.bin", false));
            var anyPropModel = CompareAnyOf("AnyOf(Prop('System.Size') > 0)"); SaveAnyOfModel("prop-size", anyPropModel);
            Verify("GUI " + count + " AnyOf Shell property comparison", anyPropModel.Entries.Count == 4 && IsFiltered(anyPropModel, "zero.bin", true));
            var adoptedAnyOf = pane.FolderOptionsAdoptedModel;
            var displayedAnyOf = pane.FolderOptionsRawRows.Items.Cast<object>().ToArray();
            var anyOfDialogs = window.OwnedWindows.ToArray();
            File.WriteAllText(anyOfFilter, "def: exclude\nfe: AnyOf(true, false)\n");
            var anyOfRejected = false;
            try { CompareByButton(); } catch (FormatException) { anyOfRejected = true; }
            Dispatcher.UIThread.RunJobs();
            foreach (var dialog in window.OwnedWindows.Where(dialog => !anyOfDialogs.Contains(dialog) && dialog.Title == "操作を完了できませんでした").ToArray())
                dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "閉じる")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Verify("GUI " + count + " invalid AnyOf preserves adopted model and rows", anyOfRejected
                && ReferenceEquals(adoptedAnyOf, pane.FolderOptionsAdoptedModel)
                && pane.FolderOptionsRawRows.Items.Cast<object>().SequenceEqual(displayedAnyOf) && pane.CompareButton.IsEnabled);
            SaveAnyOfModel("invalid-retained", (DirectoryComparisonResult)pane.FolderOptionsAdoptedModel!);
            screenshot("folder-any-of-retained-" + count + ".png");
            var notEachExistsModel = CompareNotEach("NotEach(Exists)"); SaveNotEachModel("exists", notEachExistsModel);
            Verify("GUI " + count + " NotEach Exists includes only missing side", notEachExistsModel.Entries.Select(entry => entry.RelativePath).SequenceEqual(["missing.bin"])
                && IsFiltered(notEachExistsModel, "missing.bin", false) && IsFiltered(notEachExistsModel, "same.bin", true));
            var notEachAnyModel = CompareNotEach("AnyOf(NotEach(Exists))"); SaveNotEachModel("any-exists", notEachAnyModel);
            Verify("GUI " + count + " AnyOf NotEach preserves missing slot", notEachAnyModel.Entries.Select(entry => entry.RelativePath).SequenceEqual(["missing.bin"]));
            var notEachSizeModel = CompareNotEach("NotEach(Size > 0)"); SaveNotEachModel("size", notEachSizeModel);
            Verify("GUI " + count + " NotEach Size inverts each present side and keeps missing None", notEachSizeModel.Entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal).SequenceEqual(["partial.bin", "zero.bin"])
                && IsFiltered(notEachSizeModel, "missing.bin", true));
            var notEachAllModel = CompareNotEach("AllOf(NotEach(Size > 0))"); SaveNotEachModel("all-size", notEachAllModel);
            Verify("GUI " + count + " AllOf NotEach requires all zero sizes", notEachAllModel.Entries.Select(entry => entry.RelativePath).SequenceEqual(["zero.bin"]));
            var notEachNotModel = CompareNotEach("not NotEach(Exists)"); SaveNotEachModel("not-result", notEachNotModel);
            Verify("GUI " + count + " logical not consumes whole NotEach result", notEachNotModel.Entries.Count == 4 && IsFiltered(notEachNotModel, "missing.bin", true));
            var notEachNotArgumentModel = CompareNotEach("NotEach(not Exists)"); SaveNotEachModel("not-argument", notEachNotArgumentModel);
            Verify("GUI " + count + " NotEach accepts nested logical not", notEachNotArgumentModel.Entries.Count == 5);
            var notEachPropModel = CompareNotEach("NotEach(Prop('System.Size') > 0)"); SaveNotEachModel("prop-size", notEachPropModel);
            Verify("GUI " + count + " NotEach actual Shell property comparison", notEachPropModel.Entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal).SequenceEqual(["partial.bin", "zero.bin"])
                && IsFiltered(notEachPropModel, "missing.bin", true) && pane.CompareButton.IsEnabled);
            var adoptedNotEach = pane.FolderOptionsAdoptedModel;
            var displayedNotEach = pane.FolderOptionsRawRows.Items.Cast<object>().ToArray();
            var notEachDialogs = window.OwnedWindows.ToArray();
            File.WriteAllText(notEachFilter, "def: exclude\nfe: NotEach(true, false)\n");
            var notEachRejected = false;
            try { CompareByButton(); } catch (FormatException) { notEachRejected = true; }
            Dispatcher.UIThread.RunJobs();
            foreach (var dialog in window.OwnedWindows.Where(dialog => !notEachDialogs.Contains(dialog) && dialog.Title == "操作を完了できませんでした").ToArray())
                dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "閉じる")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Verify("GUI " + count + " invalid NotEach preserves adopted candidate model and rows", notEachRejected
                && ReferenceEquals(adoptedNotEach, pane.FolderOptionsAdoptedModel)
                && pane.FolderOptionsRawRows.Items.Cast<object>().SequenceEqual(displayedNotEach) && pane.CompareButton.IsEnabled);
            SaveNotEachModel("invalid-retained", (DirectoryComparisonResult)pane.FolderOptionsAdoptedModel!);
            screenshot("folder-not-each-retained-" + count + ".png");
            var sizeModel = CompareAllOf("AllOf(Size > 0)"); SaveAllOfModel("size", sizeModel);
            Verify("GUI " + count + " AllOf Size requires every side to be positive",
                sizeModel.Entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal).SequenceEqual(["different.bin", "same.bin"])
                    && IsFiltered(sizeModel, "same.bin", false) && IsFiltered(sizeModel, "different.bin", false)
                    && IsFiltered(sizeModel, "zero.bin", true) && IsFiltered(sizeModel, "partial.bin", true)
                    && IsFiltered(sizeModel, "missing.bin", true) && pane.CompareButton.IsEnabled);
            screenshot("folder-all-of-" + count + ".png");

            var existsModel = CompareAllOf("AllOf(Exists)"); SaveAllOfModel("exists", existsModel);
            Verify("GUI " + count + " AllOf Exists rejects a missing side",
                existsModel.Entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal)
                    .SequenceEqual(["different.bin", "partial.bin", "same.bin", "zero.bin"])
                    && IsFiltered(existsModel, "missing.bin", true) && pane.CompareButton.IsEnabled);

            var trueModel = CompareAllOf("AllOf(true)"); SaveAllOfModel("true", trueModel);
            Verify("GUI " + count + " AllOf preserves true scalar result", trueModel.Entries.Count == 5 && pane.CompareButton.IsEnabled);
            var falseModel = CompareAllOf("AllOf(false)"); SaveAllOfModel("false", falseModel);
            Verify("GUI " + count + " AllOf preserves false scalar result", falseModel.Entries.Count == 0 && pane.CompareButton.IsEnabled);
            var numberModel = CompareAllOf("AllOf(Size)"); SaveAllOfModel("nonboolean-array", numberModel);
            Verify("GUI " + count + " AllOf rejects nonboolean array items", numberModel.Entries.Count == 0 && pane.CompareButton.IsEnabled);
        }
        foreach (var count in new[] { 2, 3 })
        {
            var lenRoots = Enumerable.Range(0, count).Select(side => Path.Combine(work, "len-" + count, "side-" + side)).ToArray();
            foreach (var root in lenRoots)
            {
                Directory.CreateDirectory(root);
                File.WriteAllBytes(Path.Combine(root, "same.bin"), [1, 2, 3, 4]);
                File.WriteAllBytes(Path.Combine(root, "zero.bin"), []);
                File.WriteAllBytes(Path.Combine(root, "long-name.bin"), [5]);
            }
            var lenFilter = Path.Combine(work, "len-" + count + ".flt");
            DirectoryComparisonResult CompareLen(string expression)
            {
                File.WriteAllText(lenFilter, "def: exclude\nfe: " + expression + "\n");
                pane.ApplyProject(new() { Mode = "Folder", LeftPath = lenRoots[0], BasePath = count == 3 ? lenRoots[1] : "", RightPath = lenRoots[^1], Recursive = true, FileFilterPath = lenFilter });
                CompareByButton();
                return (DirectoryComparisonResult)pane.FolderOptionsAdoptedModel!;
            }
            void SaveLenModel(string id, string expression, DirectoryComparisonResult model)
            {
                using var stream = File.Create(Path.Combine(work, "len-" + count + "-" + id + ".json"));
                using var writer = new Utf8JsonWriter(stream); writer.WriteStartObject(); writer.WriteString("id", id);
                writer.WriteNumber("sides", count); writer.WriteString("filterPath", lenFilter); writer.WriteString("expression", expression);
                FolderCommands.WriteModel(writer, model); writer.WriteEndObject();
            }

            var arrayExpression = "Len(Name) == " + count;
            var arrayModel = CompareLen(arrayExpression); SaveLenModel("name-array", arrayExpression, arrayModel);
            Verify("GUI " + count + " Len Name counts all sides", arrayModel.Entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal)
                .SequenceEqual(["long-name.bin", "same.bin", "zero.bin"]) && pane.CompareButton.IsEnabled);
            screenshot("folder-len-name-array-" + count + ".png");

            const string stringExpression = "Len(LeftName) == 8";
            var stringModel = CompareLen(stringExpression); SaveLenModel("left-name-string", stringExpression, stringModel);
            Verify("GUI " + count + " Len LeftName counts string characters", stringModel.Entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal)
                .SequenceEqual(["same.bin", "zero.bin"]) && stringModel.AllEntries.Single(entry => entry.RelativePath == "long-name.bin").IsFiltered
                && pane.CompareButton.IsEnabled);
            screenshot("folder-len-left-name-" + count + ".png");

            const string scalarExpression = "Len(None) == 0 and Len('') == 0 and Len(0) == 1 and Len(false) == 1 and Len(LeftSize) == 1";
            var scalarModel = CompareLen(scalarExpression); SaveLenModel("scalars", scalarExpression, scalarModel);
            Verify("GUI " + count + " Len None empty string numeric and boolean scalars", scalarModel.Entries.Count == 3
                && scalarModel.Entries.Any(entry => entry.RelativePath == "zero.bin") && pane.CompareButton.IsEnabled);

            var adopted = pane.FolderOptionsAdoptedModel;
            var displayed = pane.FolderOptionsRawRows.Items.Cast<object>().ToArray();
            var priorDialogs = window.OwnedWindows.ToArray();
            File.WriteAllText(lenFilter, "def: exclude\nfe: Len() == 0\n");
            var invalidRejected = false;
            try { CompareByButton(); }
            catch (FormatException) { invalidRejected = true; }
            Dispatcher.UIThread.RunJobs();
            var errorDialogs = window.OwnedWindows.Where(dialog => !priorDialogs.Contains(dialog) && dialog.Title == "操作を完了できませんでした").ToArray();
            var diagnostic = string.Join("\n", errorDialogs.SelectMany(dialog => dialog.GetVisualDescendants().OfType<TextBlock>()).Select(block => block.Text));
            foreach (var dialog in errorDialogs)
                dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "閉じる")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var modelRetained = ReferenceEquals(adopted, pane.FolderOptionsAdoptedModel);
            var rowsRetained = pane.FolderOptionsRawRows.Items.Cast<object>().SequenceEqual(displayed);
            Verify("GUI " + count + " invalid Len preserves adopted display", invalidRejected && modelRetained && rowsRetained && pane.CompareButton.IsEnabled);
            using (var stream = File.Create(Path.Combine(work, "len-" + count + "-invalid.json")))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject(); writer.WriteString("expression", "Len() == 0"); writer.WriteNumber("sides", count);
                writer.WriteBoolean("rejected", invalidRejected); writer.WriteBoolean("modelRetained", modelRetained); writer.WriteBoolean("rowsRetained", rowsRetained);
                writer.WriteString("diagnostic", diagnostic); FolderCommands.WriteModel(writer, (DirectoryComparisonResult)pane.FolderOptionsAdoptedModel!); writer.WriteEndObject();
            }
            screenshot("folder-len-invalid-retained-" + count + ".png");
        }
        using var evidence = File.Create(Path.Combine(work, "assertions.json")); using var evidenceWriter = new Utf8JsonWriter(evidence);
        evidenceWriter.WriteStartArray(); foreach (var item in observations) { evidenceWriter.WriteStartObject(); evidenceWriter.WriteString("id", item.Id); evidenceWriter.WriteBoolean("passed", item.Passed); evidenceWriter.WriteEndObject(); } evidenceWriter.WriteEndArray();

        void CompareByButton()
        {
            Task? task = null; var prior = pane.TextSaveTaskObserved;
            pane.TextSaveTaskObserved = (route, value) => { if (route == "path-compare") task = value; prior?.Invoke(route, value); };
            try { pane.CompareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); if (task is null) throw new InvalidOperationException("Compare button did not start comparison."); pump(task); Dispatcher.UIThread.RunJobs(); }
            finally { pane.TextSaveTaskObserved = prior; }
        }
    }

    private sealed class CasingEnvironment : ILineTransformStringEnvironment
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        private string Map(string value, bool lower)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("casing-environment-failure");
            return lower ? value.ToLowerInvariant() : value.ToUpperInvariant();
        }
        public string ToLower(string value) => Map(value, true);
        public string ToUpper(string value) => Map(value, false);
        public string NormalizeUnicode(string value, System.Text.NormalizationForm form) => throw new NotSupportedException();
    }

    private sealed class ArrayTraceProperties(IReadOnlyDictionary<string, FileFilterPropertyValue> values, string? throwOn = null)
        : IReadOnlyDictionary<string, FileFilterPropertyValue>
    {
        public List<string> ReadTrace { get; } = [];
        public FileFilterPropertyValue this[string key] => values[key];
        public IEnumerable<string> Keys => values.Keys;
        public IEnumerable<FileFilterPropertyValue> Values => values.Values;
        public int Count => values.Count;
        public bool ContainsKey(string key) => values.ContainsKey(key);
        public bool TryGetValue(string key, out FileFilterPropertyValue result)
        {
            ReadTrace.Add(key);
            if (key == throwOn) throw new InvalidOperationException("array-" + key);
            return values.TryGetValue(key, out result!);
        }
        public IEnumerator<KeyValuePair<string, FileFilterPropertyValue>> GetEnumerator() => values.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class AnyOfOracleProperties(FileFilterPropertyValue value, bool throws, FileFilterPropertyValue? expected = null) : IReadOnlyDictionary<string, FileFilterPropertyValue>
    {
        public int Reads { get; private set; }
        public int ExpectedReads { get; private set; }
        public bool TryGetValue(string key, out FileFilterPropertyValue result)
        {
            if (key == "expected" && expected is { } expectedValue) { ExpectedReads++; result = expectedValue; return true; }
            Reads++;
            if (throws) throw new InvalidOperationException("argument-0");
            result = value; return key == "fixture";
        }
        public FileFilterPropertyValue this[string key] => key == "fixture" ? value
            : key == "expected" && expected is { } result ? result : throw new KeyNotFoundException(key);
        public int Count => expected is null ? 1 : 2;
        public IEnumerable<string> Keys => expected is null ? ["fixture"] : ["fixture", "expected"];
        public IEnumerable<FileFilterPropertyValue> Values => Keys.Select(key => this[key]);
        public bool ContainsKey(string key) => key == "fixture" || key == "expected" && expected is not null;
        public IEnumerator<KeyValuePair<string, FileFilterPropertyValue>> GetEnumerator()
            => Keys.Select(key => new KeyValuePair<string, FileFilterPropertyValue>(key, this[key])).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void WriteValue(Utf8JsonWriter writer, FileFilterPropertyValue value)
    {
        writer.WriteStartObject(); writer.WriteString("type", value.Kind switch { FileFilterPropertyKind.None => "none", FileFilterPropertyKind.Integer => "integer", FileFilterPropertyKind.Double => "double", FileFilterPropertyKind.Timestamp => "timestamp-us", FileFilterPropertyKind.Boolean => "bool", FileFilterPropertyKind.Array => "array", _ => "string" });
        switch (value.Kind)
        {
            case FileFilterPropertyKind.Integer: case FileFilterPropertyKind.Timestamp: writer.WriteString("value", value.Integer.ToString(CultureInfo.InvariantCulture)); break;
            case FileFilterPropertyKind.Double: writer.WriteNumber("value", value.Double); break;
            case FileFilterPropertyKind.Boolean: writer.WriteBoolean("value", value.Boolean); break;
            case FileFilterPropertyKind.String: writer.WriteString("value", value.Text); break;
            case FileFilterPropertyKind.Array: writer.WriteStartArray("value"); foreach (var item in value.Items!) WriteValue(writer, item); writer.WriteEndArray(); break;
        }
        writer.WriteEndObject();
    }

    private static void WriteJsonLine(StreamWriter writer, Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream)) write(json);
        writer.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }
}
