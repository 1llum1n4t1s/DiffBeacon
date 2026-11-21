using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessLineTransformVersionLiteralChecks
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Functions = ["VersionLiteral"];
    internal static void Run(MainWindow window, ComparisonPane pane, string output,
        Action<string, bool, string> check, Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "line-transform-version-literals"));
        RequireExternal(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Version検証には新規出力が必要です。");
        Directory.CreateDirectory(directory);
        void Verify(string name, bool ok, string detail = "") => check("line transform version literal " + name, ok, detail);
        using var plan = JsonDocument.Parse(ReadEmbedded("DiffBeacon.LineTransformVersionLiteralCases"));
        if (plan.RootElement.GetProperty("schema").GetString() != "diffbeacon.version-public-plan.v1")
            throw new InvalidDataException("Version計画schema不一致です。");
        var cases = plan.RootElement.GetProperty("records").EnumerateArray().Select(x => x.Clone()).ToArray();
        ValidateCases(cases);
        var actual = cases.Select(item => Observe(item, Verify)).ToArray();
        using (var file = NewFile(directory, "public-observations.json"))
            WriteObservations(file, "diffbeacon.version-public-observations.v1", actual);
        WriteProbes(directory, Verify);
        WriteEngineering(directory, Verify);
        RunPlainAndScopes(pane, directory, Verify, screenshot, pump);
        RunUi(window, pane, directory, Verify, screenshot, pump);
    }

    // long/constructor-originをserialized stringから逆算しない。Native int64は期待値だけ。
    private static Action<Utf8JsonWriter> Observe(JsonElement item, Action<string, bool, string> verify)
    {
        var id = item.GetProperty("id").GetString()!;
        var bytes = Convert.FromHexString(item.GetProperty("expressionUtf8Hex").GetString()!);
        var expression = Utf8.GetString(bytes);
        var success = item.GetProperty("classification").GetString() == "public-success-proposal";
        var source = new[] { "Version physical RAW" }; var before = SourceSha(source);
        string? serialized = null; Exception? error = null; bool compiled = false;
        try
        {
            var plan = LineTransformPlan.Compile([new(expression, TablePaneMask.All)], new LineTransformBudget());
            compiled = true;
            var keys = plan.Transform(source, TablePaneMask.Left, false);
            if (keys.Length != 1) throw new InvalidDataException("Version key count differs.");
            serialized = Hex(Utf8.GetBytes(keys[0]));
        }
        catch (Exception caught) when (ExpectedError(caught)) { error = caught; }
        var passed = success
            ? compiled && error is null && serialized == item.GetProperty("serializedByteHex").GetString()!.ToLowerInvariant()
            : !compiled && serialized is null && error is not null;
        verify(id, passed && SourceSha(source) == before, "public-observations.json; serialized public observation only");
        return json =>
            {
                json.WriteString("id", id);
                json.WriteString("expressionUtf8Hex", item.GetProperty("expressionUtf8Hex").GetString());
                json.WriteBoolean("publicExecuted", true);
                json.WriteBoolean("compileSucceeded", compiled);
                json.WriteNull("actualValueKind");
                json.WriteNull("actualInt64");
                json.WriteNull("constructorOrigin");
                json.WriteBoolean("publicTypedObserved", false);
                json.WriteBoolean("constructorOriginObserved", false);
                json.WriteString("expectedNativeKind", "int64");
                json.WriteString("expectedNativeInt64", item.GetProperty("rawValue").GetString());
                json.WriteString("expectedConstructorOrigin", "Version");
                json.WriteString("expectedConstructorOriginEvidence", "static-source");
                json.WriteString("serializedByteHex", serialized);
                json.WriteString("actualSerializedValue", serialized is null ? null : Utf8.GetString(Convert.FromHexString(serialized)));
                json.WritePropertyName("nativeIndex"); item.GetProperty("nativeIndex").Clone().WriteTo(json);
                json.WritePropertyName("provenance"); item.GetProperty("provenance").Clone().WriteTo(json);
                json.WriteBoolean("nativeFullParserExecuted", false);
                json.WriteBoolean("nativeTypedContextQualified", false);
                json.WriteBoolean("nativeFullAstQualified", false);
                json.WriteString("errorCode", error is null ? null : "compile-rejected");
                json.WriteString("errorType", error?.GetType().FullName);
                json.WriteString("errorMessage", error?.Message);
                json.WriteBoolean("passed", passed);
                json.WriteString("sourceShaBefore", before);
                json.WriteString("sourceShaAfter", SourceSha(source));
            };
    }

    private static void ValidateCases(JsonElement[] cases)
    {
        if (cases.Length != 156 || cases.Count(x => x.GetProperty("classification").GetString() == "public-success-proposal") != 154
            || cases.Count(x => x.GetProperty("classification").GetString() == "constructor-only-nul-rejected") != 2
            || cases.Select(x => x.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count() != 156)
            throw new InvalidDataException("Version154+NUL2計画不一致です。");
        foreach (var group in cases.GroupBy(x => x.GetProperty("id").GetString()![..x.GetProperty("id").GetString()!.LastIndexOf(':')]))
            if (group.Count() != 2 || !group.Select(x => x.GetProperty("prefix").GetString()).Order(StringComparer.Ordinal).SequenceEqual(new[] { "V", "v" }))
                throw new InvalidDataException("Version v/V pair differs.");
        foreach (var item in cases)
        {
            foreach (var field in new[] { "nativeFullParserExecuted", "nativeTypedContextQualified", "nativeFullAstQualified" })
                if (item.GetProperty(field).GetBoolean()) throw new InvalidDataException("Unmeasured Native qualification.");
            // UTF8 bytesと原本quote bytesが同じ公開入力へ結合されていることを検査。
            var prefix = item.GetProperty("prefix").GetString()!;
            var expected = Utf8.GetBytes(prefix + "\"");
            var body = Convert.FromHexString(item.GetProperty("quotedBodyHex").GetString()!);
            var expression = Convert.FromHexString(item.GetProperty("expressionUtf8Hex").GetString()!);
            if (!expression.SequenceEqual(expected.Concat(body)))
                throw new InvalidDataException("Native quoted body/public expression join differs.");
        }
    }

    private static void WriteProbes(string directory, Action<string, bool, string> verify)
    {
        using var plan = JsonDocument.Parse(ReadEmbedded("DiffBeacon.LineTransformVersionLiteralProbes"));
        var proposals = plan.RootElement.GetProperty("records").EnumerateArray().ToArray();
        if (proposals.Length != 12) throw new InvalidDataException("Version separate compile probes require12 cases.");
        var rows = new List<Action<Utf8JsonWriter>>();
        foreach (var proposal in proposals)
        {
            var expression = Utf8.GetString(Convert.FromHexString(proposal.GetProperty("expressionUtf8Hex").GetString()!));
            bool compiled = false; Exception? error = null;
            try { LineTransformPlan.Compile([new(expression, TablePaneMask.All)], new LineTransformBudget()); compiled = true; }
            catch (Exception caught) when (ExpectedError(caught)) { error = caught; }
            var passed = compiled == proposal.GetProperty("expectCompile").GetBoolean()
                && (compiled ? error is null : error is not null);
            rows.Add(json =>
                {
                    json.WriteString("id", proposal.GetProperty("id").GetString());
                    json.WriteString("expressionUtf8Hex", proposal.GetProperty("expressionUtf8Hex").GetString());
                    json.WriteString("expressionUtf16Hex", string.Concat(expression.Select(ch => ((int)ch).ToString("x4", CultureInfo.InvariantCulture))));
                    json.WriteBoolean("publicExecuted", true);
                    json.WriteBoolean("publicCompileExecuted", true);
                    json.WriteBoolean("compileSucceeded", compiled);
                    json.WriteBoolean("executed", false);
                    json.WriteNull("actualValueKind");
                    json.WriteNull("actualInt64");
                    json.WriteNull("constructorOrigin");
                    json.WriteBoolean("publicTypedObserved", false);
                    json.WriteNull("actualSerializedValue");
                    json.WriteNull("serializedByteHex");
                    json.WritePropertyName("nativeIndex"); proposal.GetProperty("nativeIndex").Clone().WriteTo(json);
                    json.WritePropertyName("provenance"); proposal.GetProperty("provenance").Clone().WriteTo(json);
                    json.WriteBoolean("nativeFullParserExecuted", false);
                    json.WriteBoolean("nativeLexerExecuted", false);
                    json.WriteBoolean("actualNativelexerExecuted", false);
                    json.WriteBoolean("nativeTypedContextQualified", false);
                    json.WriteBoolean("nativeFullAstQualified", false);
                    json.WriteString("errorCode", error is null ? null : "compile-rejected");
                    json.WriteString("errorType", error?.GetType().FullName);
                    json.WriteString("errorMessage", error?.Message);
                    json.WriteBoolean("passed", passed);
                });
            verify("separate compile " + proposal.GetProperty("id").GetString(), passed, "public-probes.json; static proposals only, no Native parser qualification");
        }
        using var output = NewFile(directory, "public-probes.json");
        WriteObservations(output, "diffbeacon.version-probes-observations.v1", rows);
    }
    private static void WriteEngineering(string directory, Action<string, bool, string> verify)
    {
        using var file = NewFile(directory, "engineering.ndjson");
        var records = new List<Action<Utf8JsonWriter>>();
        foreach (var (id, expression, expected) in new (string, string, string?)[]
        {
            ("array-constructor", "Array(v\"0.0.0.1\",v\"0.0.0.2\")", "[1, 2]"),
            ("array-noopt", "@noopt Array(v\"0.0.0.1\",v\"0.0.0.2\")", "[1, 2]"),
            ("abs-constructor", "Abs(v\"0.0.0.-1\")", "1"),
            ("abs-noopt", "@noopt Abs(v\"0.0.0.-1\")", "1"),
            ("at-constructor", "At(Array(10,20),v\"0.0.0.1\")", "20"),
            ("at-noopt", "@noopt At(Array(10,20),v\"0.0.0.1\")", "20"),
            ("substr-constructor", "Substr(\"abc\",v\"0.0.0.1\",v\"0.0.0.1\")", "b"),
            ("substr-noopt", "@noopt Substr(\"abc\",v\"0.0.0.1\",v\"0.0.0.1\")", "b"),
            ("strfind-constructor", "StrFind(\"abc\",\"b\",v\"0\")", "1"),
            ("strfind-noopt", "@noopt StrFind(\"abc\",\"b\",v\"0\")", "1"),
            ("signbit", "v\"32768\"", "-9223372036854775808"),
            ("signbit-binary-ordinary", "v\"32768\" + 0", "-9223372036854775808"),
            ("zero", "v\"0.0.0.0\"", "0"),
            ("ignored-doubled-quote", "v\"0.0.0.1\"\"tail\"", "1"),
            ("ignored-backslash", "v\"0.0.0.1\\tail\"", "1"),
            ("ignored-newline", "v\"0.0.0.1\nTail\"", "1"),
            ("partial-first", "v\"1.tail\"", "281474976710656"),
            ("partial-second", "v\"0.1.tail\"", "4294967296"),
            ("partial-third", "v\"0.0.1.tail\"", "65536"),
            ("partial-fourth", "v\"0.0.0.1tail\"", "1"),
            ("plus", "v\"+0.+0.+0.+1\"", "1"),
            ("revision-min", "v\"0.0.0.-2147483648\"", "-2147483648"),
            ("ignored-unicode", "v\"0.0.0.1雪\"", "1"),
            ("major-overflow", "v\"65536\"", null),
            ("negative-major", "v\"-1\"", null),
            ("negative-minor", "v\"0.-1\"", null),
            ("minor-int32-overflow", "v\"0.2147483648\"", null),
            ("build-int32-overflow", "v\"0.0.2147483648\"", null),
            ("revision-int32-overflow", "v\"0.0.0.2147483648\"", null),
            ("sum-overflow", "v\"32767.2147483647.2147483647.2147483647\"", null),
            ("binary-overflow", "v\"32768\" - 1", null),
            ("unary-overflow", "-v\"32768\"", null),
            ("nul", "v\"0\0\"", null),
            ("unpaired-surrogate", "v\"0\ud800\"", null)
        })
        {
            var source = new[] { "engineering RAW" }; var before = SourceSha(source);
            string[]? actual = null; Exception? error = null; bool compiled = false;
            try { var plan = LineTransformPlan.Compile([new(expression, TablePaneMask.All)], new LineTransformBudget()); compiled = true; actual = plan.Transform(source, TablePaneMask.Left, false); }
            catch (Exception caught) when (ExpectedError(caught)) { error = caught; }
            var ok = expected is null ? error is not null && actual is null : error is null && actual is { Length: 1 } && actual[0] == expected;
            string? expressionHex = null;
            try { expressionHex = Hex(Utf8.GetBytes(expression)); } catch (EncoderFallbackException) { }
            records.Add(json =>
                {
                    json.WriteString("id", id);
                    json.WriteString("expressionUtf8Hex", expressionHex);
                    json.WriteString("expressionUtf16Hex", string.Concat(expression.Select(ch => ((int)ch).ToString("x4", CultureInfo.InvariantCulture))));
                    json.WriteBoolean("publicExecuted", true);
                    json.WriteBoolean("compileSucceeded", compiled);
                    json.WriteNull("actualValueKind");
                    json.WriteNull("actualInt64");
                    json.WriteNull("constructorOrigin");
                    json.WriteBoolean("publicTypedObserved", false);
                    json.WriteBoolean("constructorOriginObserved", false);
                    json.WriteString("serializedByteHex", actual is { Length: 1 } ? Hex(Utf8.GetBytes(actual[0])) : null);
                    json.WriteString("actualSerializedValue", actual is { Length: 1 } ? actual[0] : null);
                    json.WriteNull("nativeIndex");
                    json.WriteNull("provenance");
                    json.WriteBoolean("nativeFullParserExecuted", false);
                    json.WriteBoolean("nativeTypedContextQualified", false);
                    json.WriteBoolean("nativeFullAstQualified", false);
                    json.WriteString("errorCode", error is null ? null : compiled ? "evaluate-rejected" : "compile-rejected");
                    json.WriteString("errorType", error?.GetType().FullName);
                    json.WriteString("errorMessage", error?.Message);
                    json.WriteBoolean("passed", ok && before == SourceSha(source));
                });
            Row(file, json =>
            {
                json.WriteString("id", id); json.WriteString("expression", expression);
                json.WriteBoolean("engineering", true); json.WriteBoolean("publicExecuted", true);
                json.WriteBoolean("compileSucceeded", compiled); json.WriteNull("actualValueKind"); json.WriteNull("actualInt64");
                json.WriteNull("constructorOrigin"); json.WriteBoolean("publicTypedObserved", false);
                json.WriteBoolean("nativeFullParserExecuted", false); json.WriteBoolean("nativeTypedContextQualified", false); json.WriteBoolean("nativeFullAstQualified", false);
                json.WriteString("expectedSerializedText", expected); WriteKeys(json, actual); WriteError(json, error);
                json.WriteBoolean("passed", ok && before == SourceSha(source));
            });
            verify("engineering " + id, ok && before == SourceSha(source), "engineering.ndjson; independent constant contract, not Native AST");
        }
        using (var observations = NewFile(directory, "engineering-observations.json"))
            WriteObservations(observations, "diffbeacon.version-engineering-observations.v1", records);
        var rules = new[] { new LineTransformRule("v\"0.0.0.1\"", TablePaneMask.All) };
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool cancelled = false, transformCancelled = false, limited = false, rulesRejected = false;
        try { LineTransformPlan.Compile(rules, new LineTransformBudget(), cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        var unlimited = LineTransformPlan.Compile(rules, new LineTransformBudget());
        try { unlimited.Transform(["RAW"], TablePaneMask.Left, false, cancellationToken: cancellation.Token); } catch (OperationCanceledException) { transformCancelled = true; }
        try { var p = LineTransformPlan.Compile(rules, new LineTransformBudget(maximumWork: 1)); p.Transform(["RAW"], TablePaneMask.Left, false); } catch (InvalidOperationException) { limited = true; }
        try { LineTransformPlan.Compile(Enumerable.Repeat(rules[0], 257).ToArray(), new LineTransformBudget()); } catch (ArgumentException) { rulesRejected = true; }
        Row(file, json => { json.WriteString("id", "budget-cancel"); json.WriteBoolean("compileCancelled", cancelled); json.WriteBoolean("transformCancelled", transformCancelled); json.WriteBoolean("limitedRejected", limited); json.WriteBoolean("rules257Rejected", rulesRejected); });
        verify("budget cancel and 257 rules", cancelled && transformCancelled && limited && rulesRejected, "engineering.ndjson");
    }
    private static void WriteObservations(Stream output, string schema, IEnumerable<Action<Utf8JsonWriter>> records)
    {
        using var json = new Utf8JsonWriter(output);
        json.WriteStartObject();
        json.WriteString("schema", schema);
        json.WriteBoolean("producerExecuted", true);
        json.WriteStartArray("records");
        foreach (var record in records)
        {
            json.WriteStartObject();
            record(json);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
    }
    private static string SourceSha(string[] source) => Hex(SHA256.HashData(Utf8.GetBytes(string.Join("\0", source))));
    private static void RunPlainAndScopes(ComparisonPane pane, string directory, Action<string, bool, string> verify,
        Action<string> screenshot, Action<Task> pump)
    {
        using var evidence = NewFile(directory, "source-scopes.ndjson");
        var masks = new[] { TablePaneMask.All, TablePaneMask.Left, TablePaneMask.Base, TablePaneMask.Right,
            TablePaneMask.Left | TablePaneMask.Base, TablePaneMask.Left | TablePaneMask.Right, TablePaneMask.Base | TablePaneMask.Right };
        var previousRules = pane.CaptureProject().LineTransformRules.ToArray();
        var oldShown = LineTransformDialog.Shown; LineTransformDialog? shown = null;
        try
        {
            pane.SetLineTransforms([]); LineTransformDialog.Shown = dialog => shown = dialog;
            Click(Caption(pane, "行変換…")); Settle();
            var dialog = shown ?? throw new InvalidOperationException("VersionLiteral scope dialogがありません。");
            for (var index = 0; index < masks.Length; index++)
            {
                dialog.Target.SelectedIndex = index; dialog.Expression.Text = "@noopt v\"0.0.0.1500\"";
                Click(Named(dialog, "LineTransformAdd"));
                var ok = dialog.Rules.Count == index + 1 && dialog.Rules[index].Panes == masks[index];
                Row(evidence, json => { json.WriteString("id", "dialog-scope-" + index); json.WriteNumber("mask", (int)masks[index]); json.WriteBoolean("passed", ok); });
                verify("actual dialog scope " + masks[index], ok, "source-scopes.ndjson");
            }
            Click(dialog.Cancel); Settle(); verify("scope dialog cancel", pane.CaptureProject().LineTransformRules.Length == 0, "");
        }
        finally { LineTransformDialog.Shown = oldShown; pane.SetLineTransforms(previousRules); }
        foreach (var mode in new[] { "Text", "Table" })
        foreach (var three in new[] { false, true })
        {
            var raw = mode == "Table" ? new[] { "id,value\r\nA,\"left,raw\"\r\n", "id,value\r\nA,\"middle,raw\"\r\n", "id,value\r\nA,\"right,raw\"\r\n" }
                : new[] { "left RAW\r\nsecond left\r\n", "middle RAW\r\nsecond middle\r\n", "right RAW\r\nsecond right\r\n" };
            var prefix = "scopes-" + mode + "-" + (three ? "three" : "two");
            var paths = raw.Select((text, side) => Path.Combine(directory, prefix + "-" + side + (mode == "Table" ? ".csv" : ".txt"))).ToArray();
            for (var side = 0; side < 3; side++) { using var file = new FileStream(paths[side], FileMode.CreateNew, FileAccess.Write); file.Write(Utf8.GetBytes(raw[side])); }
            var inputSha = paths.Select(path => Hex(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
            foreach (var mask in mode == "Text" ? new[] { TablePaneMask.All } : masks)
            {
                var rule = new LineTransformRule(mode == "Table" ? "@cs @noopt If(LineNumber = 1, Line, v\"0.0.0.1500\")" : "@cs @noopt v\"0.0.0.1500\"", mask);
                var project = new ComparisonProject { Mode = mode, LeftPath = paths[0], BasePath = three ? paths[1] : "", RightPath = paths[2],
                    TableDelimiter = ',', TableQuote = '"', LineTransformRules = [rule] };
                pane.DiscardChanges(); pane.ApplyProject(project); pump(pane.CompareProjectAsync()); Settle();
                var full = three ? mask == TablePaneMask.All : (mask & (TablePaneMask.Left | TablePaneMask.Right)) == (TablePaneMask.Left | TablePaneMask.Right);
                var rawRetained = pane.LeftEditor.Text == raw[0] && pane.RightEditor.Text == raw[2]
                    && paths.Select(path => Hex(SHA256.HashData(File.ReadAllBytes(path)))).SequenceEqual(inputSha);
                var classified = false; var ignored = false;
                if (mode == "Table")
                {
                    var table = pane.GetVisualDescendants().OfType<TablePanel>().Single();
                    classified = table.Comparison.HasDifferences != full && table.Comparison.Documents.Count == (three ? 3 : 2);
                    rawRetained &= table.Comparison.Documents[0].SourceText == raw[0] && table.Comparison.Documents[^1].SourceText == raw[2];
                    ignored = table.Comparison.IsIgnored(0, 1);
                }
                else { classified = pane.CurrentDiff is { HasDifferences: false }; ignored = pane.CurrentDiff?.Rows.Any(row => row.IsIgnored) == true; }
                var ok = rawRetained && classified && (!full || ignored);
                Row(evidence, json => { json.WriteString("id", prefix + "-" + mask); json.WriteString("expression", rule.Expression); json.WriteNumber("mask", (int)mask);
                    json.WriteString("qualification", "GUI-derived cases; Native106 contexts not retargeted"); json.WriteBoolean("rawRetained", rawRetained);
                    json.WriteBoolean("classificationExpected", classified); json.WriteBoolean("ignoredRowObserved", ignored); json.WriteBoolean("passed", ok);
                    json.WriteStartArray("inputSha256"); foreach (var sha in inputSha) json.WriteStringValue(sha); json.WriteEndArray(); });
                verify(prefix + " " + mask, ok, "source-scopes.ndjson"); screenshot("line-transform-version-" + prefix + "-" + mask + ".png");
            }
        }
        evidence.Flush();
    }
    private static void RunUi(MainWindow window, ComparisonPane pane, string directory,
        Action<string, bool, string> verify, Action<string> screenshot, Action<Task> pump)
    {
        using var uiEvidence = NewFile(directory, "ui-assertions.ndjson");
        var report = verify;
        verify = (name, ok, detail) => { Row(uiEvidence, json => { json.WriteString("name", name); json.WriteBoolean("passed", ok);
            json.WriteString("detail", detail); json.WriteString("qualification", "headless routed buttons; OS dialogs/pointer not observed"); }); uiEvidence.Flush(); report(name, ok, detail); };
        T Wait<T>(Task<T> task) { pump(task); return task.GetAwaiter().GetResult(); }
        TablePanel Table(ComparisonPane? target = null) => (target ?? pane).GetVisualDescendants().OfType<TablePanel>().Single();
        void TableClick(TablePanel table, string caption) { Click(Caption(table, caption)); if (table.PendingOperation is { } task) pump(task); Settle(); }
        string Write(string name, string value) { var path = Path.Combine(directory, name); using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write); file.Write(Utf8.GetBytes(value)); return path; }
        ComparisonProject? finalProject = null;
        foreach (var function in Functions)
        {
            var rule = new LineTransformRule("@cs @noopt If(LineNumber = 1, Line, v\"0.0.0.1500\")", TablePaneMask.All);
            var oldShown = LineTransformDialog.Shown; LineTransformDialog? shown = null;
            try
            {
                LineTransformDialog.Shown = dialog => shown = dialog; pane.SetLineTransforms([]); Click(Caption(pane, "行変換…")); Settle();
                var dialog = shown ?? throw new InvalidOperationException("VersionLiteraldialogが開きません。");
                foreach (var expression in new[] { "", ".1ms", "1.ms", "1eV\"0.0.0.2000\"", "1 ms", "1xs" })
                {
                    dialog.Expression.Text = expression; Click(Named(dialog, "LineTransformAdd"));
                    verify(function + " dialog invalid " + expression, dialog.Rules.Count == 0 && !string.IsNullOrEmpty(dialog.Error.Text), "");
                }
                dialog.Expression.Text = rule.Expression; Click(Named(dialog, "LineTransformAdd"));
                Settle();
                using (var bitmap = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("VersionLiteraldialogのPNGがありません。"))
                using (var png = NewFile(directory, function + "-dialog.png")) bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                using (var bounds = NewFile(directory, "VersionLiteral-dialog-bounds.ndjson"))
                {
                    foreach (var control in new Control[] { dialog.Expression, dialog.Target, dialog.RuleList, Named(dialog, "LineTransformAdd"), dialog.Accept, dialog.Cancel })
                    {
                        control.BringIntoView(); Settle(); var point = control.TranslatePoint(default, dialog);
                        var reachable = point is { } p && control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0
                            && p.X >= -.01 && p.X + control.Bounds.Width <= dialog.ClientSize.Width + .01
                            && p.Y >= -.01 && p.Y + control.Bounds.Height <= dialog.ClientSize.Height + .01;
                        Row(bounds, json => { json.WriteString("control", control.Name); json.WriteNumber("x", point?.X ?? -1); json.WriteNumber("y", point?.Y ?? -1);
                            json.WriteNumber("width", control.Bounds.Width); json.WriteNumber("height", control.Bounds.Height);
                            json.WriteNumber("clientWidth", dialog.ClientSize.Width); json.WriteNumber("clientHeight", dialog.ClientSize.Height); json.WriteBoolean("reachable", reachable); });
                        verify("dialog reachable " + control.Name, reachable, "VersionLiteral-dialog-bounds.ndjson");
                    }
                    bounds.Flush();
                }
                Click(dialog.Accept); Click(dialog.Accept); Settle();
                verify(function + " duplicate apply", pane.CaptureProject().LineTransformRules.SequenceEqual([rule]), "");
                shown = null; Click(Caption(pane, "行変換…")); Settle(); dialog = shown ?? throw new InvalidOperationException("VersionLiteraldialogが再開しません。");
                dialog.Expression.Text = "V\"0.0.0.2000\""; Click(Named(dialog, "LineTransformAdd")); Click(dialog.Cancel); Settle();
                verify(function + " dialog cancel", pane.CaptureProject().LineTransformRules.SequenceEqual([rule]), "");
            }
            finally { LineTransformDialog.Shown = oldShown; }
            var raw = new[] { "id,value\r\nA,\"left,raw\"\r\nB,keep\r\n", "id,value\r\nA,\"middle,raw\"\r\nB,keep\r\n", "id,value\r\nA,\"right,raw\"\r\nB,keep\r\n" };
            var paths = raw.Select((text, side) => Write(function + "-" + side + ".csv", text)).ToArray();
            // 通常保存で上書きする入力には、既存bytesの復元可能な原本コピーを先に置く。
            Write(function + "-left-before-save.csv", raw[0]);
            var project = new ComparisonProject { Mode = "Table", LeftPath = paths[0], BasePath = paths[1], RightPath = paths[2], TableDelimiter = ',', TableQuote = '"', LineTransformRules = [rule] };
            pane.DiscardChanges(); pane.ApplyProject(project); pump(pane.CompareProjectAsync()); Settle(); var table = Table();
            verify(function + " three raw sources and ignored difference", table.Comparison.Documents.Count == 3 && !table.Comparison.HasDifferences
                && table.Comparison.GetCell(0, 1, 1)?.Value == "left,raw" && table.Comparison.GetCell(1, 1, 1)?.Value == "middle,raw"
                && table.Comparison.GetCell(2, 1, 1)?.Value == "right,raw" && pane.LeftEditor.Text == raw[0], "");
            table.SelectCell(0, 1, 1); table.CellEditor.Text = "edited,raw"; TableClick(table, "セルを変更");
            var edited = pane.LeftEditor.Text!; verify(function + " edit original cell", edited == raw[0].Replace("left,raw", "edited,raw", StringComparison.Ordinal), "");
            TableClick(table, "セル編集を戻す"); verify(function + " undo", pane.LeftEditor.Text == raw[0], "");
            TableClick(table, "セル編集をやり直す"); verify(function + " redo", pane.LeftEditor.Text == edited, "");
            var oldSaveObserved = pane.TextSaveTaskObserved; Task? saved = null;
            try
            {
                pane.TextSaveTaskObserved = (operation, task) => { oldSaveObserved?.Invoke(operation, task); if (operation == "左を保存") saved = task; };
                Click(Caption(pane, "左を保存"));
                pump(saved ?? throw new InvalidOperationException("VersionLiteralTableの実保存Taskが観測されません。")); Settle();
            }
            finally { pane.TextSaveTaskObserved = oldSaveObserved; }
            verify(function + " save raw bytes", File.ReadAllBytes(paths[0]).SequenceEqual(Utf8.GetBytes(edited)), "");
            var workspace = Path.Combine(directory, function + "-workspace.json");
            pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new ComparisonWorkspace { Entries = [pane.CaptureProject()], ActiveEntryIndex = 0 }));
            var restored = Wait(WorkspaceStore.LoadWorkspaceAsync(workspace)); pane.DiscardChanges(); pane.ApplyProject(restored.Entries.Single()); pump(pane.CompareProjectAsync()); Settle();
            verify(function + " workspace restoration", pane.CaptureProject().LineTransformRules.SequenceEqual([rule]) && pane.LeftEditor.Text == edited, "");
            var reportPath = Path.Combine(directory, function + "-report.html"); pump(pane.SaveReportAsync(reportPath));
            var html = File.ReadAllText(reportPath); verify(function + " report original cells", html.Contains("edited,raw", StringComparison.Ordinal) && html.Contains("middle,raw", StringComparison.Ordinal) && html.Contains("right,raw", StringComparison.Ordinal), "");
            var package = Path.Combine(directory, function + "-package.zip");
            pump(ComparisonPackage.CreateAsync(restored, package, new ComparisonPackageOptions(IncludeReport: true), sourceProject: workspace));
            var unpack = Path.Combine(directory, function + "-unpacked"); RequireExternal(unpack); Directory.CreateDirectory(unpack);
            ZipFile.ExtractToDirectory(package, unpack); var packaged = Wait(WorkspaceStore.LoadWorkspaceAsync(Path.Combine(unpack, "project.json")));
            pane.ApplyProject(packaged.Entries.Single()); pump(pane.CompareProjectAsync()); Settle();
            verify(function + " package reread", pane.CaptureProject().LineTransformRules.SequenceEqual([rule]) && pane.LeftEditor.Text == edited
                && Table().Comparison.Documents[1].SourceText == raw[1] && pane.RightEditor.Text == raw[2], "");
            pane.ApplyProject(restored.Entries.Single()); pump(pane.CompareProjectAsync()); Settle();
            pane.SetLineTransforms([new(rule.Expression.Replace("@noopt ", "", StringComparison.Ordinal), TablePaneMask.All)]); pump(pane.CompareProjectAsync()); Settle();
            verify(function + " defaultopt", !Table().Comparison.HasDifferences && pane.LeftEditor.Text == edited, "");
            pane.SetLineTransforms([rule]); pump(pane.CompareProjectAsync()); Settle(); var adopted = Table(); var state = pane.CaptureAdoptionState();
            pane.SetLineTransforms([new("@noopt 1ms / (LineNumber-LineNumber)", TablePaneMask.All)]); pump(pane.CompareProjectAsync()); Settle();
            verify(function + " runtime failure preserves view", ReferenceEquals(adopted, Table()) && Equals(state, pane.CaptureAdoptionState()) && pane.LeftEditor.Text == edited, "");
            var ready = pane.TableReadyForAdoption;
            try
            {
                pane.SetLineTransforms([rule]); pane.TableReadyForAdoption = () => Click(Caption(pane, "中止")); pump(pane.CompareProjectAsync()); Settle();
                verify(function + " cancel preserves view", ReferenceEquals(adopted, Table()) && Equals(state, pane.CaptureAdoptionState()), "");
                var later = rule with { Expression = "@noopt V\"0.0.0.2000\"" };
                pane.TableReadyForAdoption = () => pane.SetLineTransforms([later]); pump(pane.CompareProjectAsync()); Settle();
                verify(function + " stale completion preserves view", ReferenceEquals(adopted, Table()) && Equals(state, pane.CaptureAdoptionState()) && pane.CaptureProject().LineTransformRules.SequenceEqual([later]), "");
            }
            finally { pane.TableReadyForAdoption = ready; }
            var readOnly = restored.Entries.Single(); readOnly.LeftReadOnly = true; pane.DiscardChanges(); pane.ApplyProject(readOnly); pump(pane.CompareProjectAsync()); Settle();
            table = Table(); table.SelectCell(0, 1, 1); var cell = table.CellEditor.Text; var rejected = false;
            try { table.CellEditor.Text = "forbidden"; TableClick(table, "セルを変更"); }
            catch (InvalidOperationException error) when (error.Message == "読取り専用のセルは変更できません。") { rejected = true; }
            finally { table.CellEditor.Text = cell; }
            verify(function + " readonly real button", rejected && table.CellEditor.IsReadOnly && pane.LeftEditor.Text == edited, "");
            readOnly.LeftReadOnly = false; pane.ApplyProject(readOnly); pump(pane.CompareProjectAsync()); Settle(); finalProject = readOnly;
            verify(function + " original file bytes", File.ReadAllBytes(paths[0]).SequenceEqual(Utf8.GetBytes(edited))
                && File.ReadAllBytes(paths[1]).SequenceEqual(Utf8.GetBytes(raw[1])) && File.ReadAllBytes(paths[2]).SequenceEqual(Utf8.GetBytes(raw[2])), "");
            screenshot("line-transform-version-" + function + "-table.png");
        }
        var width = window.Width; var height = window.Height;
        try
        {
            window.Width = 1280; window.Height = 850; Layout("normal", pane);
            window.Width = 850; window.Height = 550; Layout("minimum", pane);
            if (window.SessionPanes.Count > 37) throw new InvalidOperationException("37tab検証の新規window条件を満たしません。");
            while (window.SessionPanes.Count < 37) window.AddSession();
            var extra = window.ActivePane; extra.ApplyProject(finalProject!); pump(extra.CompareProjectAsync()); Settle();
            verify("37 comparison tabs", window.SessionPanes.Count == 37, ""); Layout("minimum-37-tabs", extra);
        }
        finally { window.Width = width; window.Height = height; Settle(); }

        void Layout(string name, ComparisonPane target)
        {
            Settle(); var table = Table(target); table.SelectCell(0, 1, 1);
            var toolbar = target.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Content is Control content && content.GetVisualDescendants().Contains(target.CompareButton));
            var tableToolbar = table.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Content is Control content && content.GetVisualDescendants().Contains(table.CellEditor));
            var results = new List<(string Label, bool Ok)>(); using var file = NewFile(directory, name + "-bounds.ndjson");
            foreach (var (label, control, scroll) in new (string, Control, ScrollViewer)[] {
                ("transform", Caption(target, "行変換…"), toolbar), ("left", target.LeftPath, toolbar), ("base", target.BasePath, toolbar), ("right", target.RightPath, toolbar),
                ("compare", target.CompareButton, toolbar), ("editor", table.CellEditor, tableToolbar), ("edit", Caption(table, "セルを変更"), tableToolbar),
                ("undo", Caption(table, "セル編集を戻す"), tableToolbar), ("redo", Caption(table, "セル編集をやり直す"), tableToolbar), ("save", Caption(target, "左を保存"), toolbar) })
            {
                control.BringIntoView(); Settle(); var point = control.TranslatePoint(default, scroll);
                var ok = point is { } p && control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0
                    && p.X >= -.01 && p.X + control.Bounds.Width <= scroll.Viewport.Width + .01
                    && p.Y >= -.01 && p.Y + control.Bounds.Height <= scroll.Viewport.Height + .01;
                Row(file, json => { json.WriteString("control", label); json.WriteNumber("windowWidth", window.Width); json.WriteNumber("windowHeight", window.Height);
                    json.WriteNumber("x", point?.X ?? -1); json.WriteNumber("y", point?.Y ?? -1); json.WriteNumber("width", control.Bounds.Width); json.WriteNumber("height", control.Bounds.Height);
                    json.WriteNumber("viewportWidth", scroll.Viewport.Width); json.WriteNumber("viewportHeight", scroll.Viewport.Height);
                    json.WriteNumber("offsetX", scroll.Offset.X); json.WriteNumber("offsetY", scroll.Offset.Y); json.WriteBoolean("reachable", ok); json.WriteBoolean("enabled", control.IsEnabled); });
                results.Add((label, ok)); screenshot("line-transform-version-" + name + "-" + label + ".png");
            }
            file.Flush(); toolbar.Offset = default; tableToolbar.Offset = default; Settle(); foreach (var row in results) verify(name + " reachable " + row.Label, row.Ok, "bounds.ndjson");
        }
    }

    private static bool ExpectedError(Exception error) => error is ArgumentException or InvalidOperationException or FormatException or ArithmeticException or OperationCanceledException;
    private static void WriteError(Utf8JsonWriter json, Exception? error) { json.WriteString("actualExceptionType", error?.GetType().FullName); json.WriteString("actualErrorUtf8Hex", error is null ? null : Hex(Utf8.GetBytes(error.Message))); }
    private static void WriteKeys(Utf8JsonWriter json, string[]? keys)
    {
        json.WriteStartArray("actualKeys"); foreach (var key in keys ?? []) { json.WriteStartObject(); json.WriteString("text", key); json.WriteNumber("utf16Length", key.Length);
            var bytes = Utf8.GetBytes(key); json.WriteNumber("utf8Length", bytes.Length); json.WriteString("utf8Hex", Hex(bytes)); json.WriteString("sha256", Hex(SHA256.HashData(bytes))); json.WriteEndObject(); } json.WriteEndArray();
    }
    private static FileStream NewFile(string directory, string name) => new(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write);
    private static void Row(Stream file, Action<Utf8JsonWriter> write) { using (var json = new Utf8JsonWriter(file)) { json.WriteStartObject(); write(json); json.WriteEndObject(); json.Flush(); } file.WriteByte((byte)'\n'); }
    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
    private static byte[] ReadEmbedded(string name)
    {
        using var resource = typeof(HeadlessLineTransformVersionLiteralChecks).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("VersionLiteral固定resourceがありません: " + name);
        using var buffer = new MemoryStream(); resource.CopyTo(buffer); return buffer.ToArray();
    }
    private static void Settle() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Button Caption(Control owner, string caption) => owner.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, caption));
    private static Button Named(LineTransformDialog dialog, string name) => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
    private static void RequireExternal(string path)
    {
        var full = Path.GetFullPath(path); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("VersionLiteral検証は外部出力が必要です。");
        }
        if (full.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase))) throw new IOException("禁止名の出力です。");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("リンク経由の出力です。");
    }
}
