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

// 外部draft。Native constructor原本と公開Compile/Transformのserialized観測を分離する。
internal static class HeadlessLineTransformSizeLiteralChecks
{
    private const string GoldenSha = "abe36d531ca546c5683a153b36be4e1f5ed31f971b9b3c322f535cf55e1e3fe8";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Functions = ["SizeLiteral"];

    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "line-transform-size-literals"));
        RequireExternal(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("SizeLiteral検証には新規出力が必要です。");
        Directory.CreateDirectory(directory);
        void Verify(string name, bool ok, string detail = "") => check("line transform size literal " + name, ok, detail);
        WriteNativeDiagnostics(directory, Verify);
        WritePublicProbes(directory, Verify);
        WriteEngineeringDiagnostics(directory, Verify);
        RunPlainAndScopes(pane, directory, Verify, screenshot, pump);
        RunUi(window, pane, directory, Verify, screenshot, pump);
    }

    private static void WriteNativeDiagnostics(string directory, Action<string, bool, string> verify)
    {
        var goldenBytes = ReadEmbedded("DiffBeacon.LineTransformSizeLiteralGolden");
        if (Hex(SHA256.HashData(goldenBytes)) != GoldenSha) throw new InvalidDataException("Native95 SHA不一致です。");
        using var plan = JsonDocument.Parse(ReadEmbedded("DiffBeacon.LineTransformSizeLiteralCases"));
        if (plan.RootElement.GetProperty("goldenSha256").GetString() != GoldenSha) throw new InvalidDataException("計画SHA不一致です。");
        var lines = Utf8.GetString(goldenBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\r')).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0; var bridges = 0; var constructors = 0; var rounding = 0;
        using var file = NewFile(directory, "diagnostics.ndjson");
        foreach (var item in plan.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var reference = item.GetProperty("nativeReference");
            var number = reference.GetProperty("lineNumber").GetInt32();
            if (!seen.Add(id) || number != ++count || number > lines.Length) throw new InvalidDataException("Native95順序不一致です。");
            var originalLine = lines[number - 1];
            using var original = JsonDocument.Parse(originalLine);
            var native = original.RootElement;
            if (native.GetProperty("id").GetString() != id || reference.GetProperty("fileSha256").GetString() != GoldenSha
                || reference.GetProperty("lineSha256WithoutTerminator").GetString() != Hex(SHA256.HashData(Utf8.GetBytes(originalLine)))
                || !JsonElement.DeepEquals(native, reference.GetProperty("record"))) throw new InvalidDataException("Native原本参照不一致です。");
            var classification = item.GetProperty("classification").GetString();
            var bridge = classification == "planned-public-serialized-bridge";
            if (bridge) bridges++;
            else if (classification == "constructor-only-unsupported-public") constructors++;
            else if (classification == "defined-cast-platform-rounding-observation") rounding++;
            else throw new InvalidDataException("未承認のNative95分類です: " + classification);
            var source = bridge ? Enumerable.Range(0, item.GetProperty("nativeCase").GetProperty("providerCount").GetInt32())
                .Select(n => "source RAW " + id + " " + n).ToArray() : [];
            var before = source.ToArray(); var sourceSha = SourceSha(source);
            string[]? keys = null; Exception? error = null; var compiled = false; var executed = false;
            var budget = new LineTransformBudget();
            if (bridge)
            {
                try
                {
                    var producer = LineTransformPlan.Compile([new(item.GetProperty("expression").GetString()!, TablePaneMask.All)], budget);
                    compiled = true; executed = true;
                    keys = producer.Transform(source, TablePaneMask.Left, false);
                }
                catch (Exception exception) when (ExpectedError(exception)) { error = exception; }
            }
            var expected = native.GetProperty("serializedUtf8Hex").GetString();
            var matched = bridge && error is null && compiled && executed && keys?.Length == source.Length
                && keys!.All(key => Hex(Utf8.GetBytes(key)) == expected) && source.SequenceEqual(before) && SourceSha(source) == sourceSha;
            Row(file, json =>
            {
                json.WriteString("id", id); json.WriteString("classification", classification);
                json.WriteString("qualification", "public serialized UTF8 keys only; Native parser/directive/typed AST context unexecuted");
                json.WriteBoolean("nativeParserExecuted", false); json.WriteBoolean("nativeDirectiveExecuted", false);
                json.WriteBoolean("contextEquivalent", false); json.WriteBoolean("actualTypedObserved", false);
                json.WriteBoolean("actualTypedValueObserved", false); json.WriteNull("actualNumericBitsHex");
                var value = native.GetProperty("value");
                json.WriteString("nativeValueKind", value.GetProperty("kind").GetString());
                json.WriteString("nativeValueBitsHex", value.TryGetProperty("bitsHex", out var bits) ? bits.GetString() : null);
                json.WritePropertyName("nativeRecord"); native.WriteTo(json);
                json.WriteString("nativeGoldenSha256", GoldenSha); json.WriteNumber("nativeLineNumber", number);
                json.WriteString("nativeRecordSha256", reference.GetProperty("lineSha256WithoutTerminator").GetString());
                json.WriteString("reason", item.GetProperty("reason").GetString());
                json.WriteBoolean("publicCompileExecuted", bridge); json.WriteBoolean("producerCompiled", compiled); json.WriteBoolean("executed", executed); json.WriteBoolean("skipped", !bridge);
                json.WriteBoolean("matched", matched); json.WriteString("expectedSerializedUtf8Hex", expected);
                WriteKeys(json, keys); WriteError(json, error);
                json.WriteBoolean("sourceRetained", source.SequenceEqual(before)); json.WriteString("sourceShaBefore", sourceSha);
                json.WriteString("sourceShaAfter", SourceSha(source)); json.WriteNumber("workUsed", budget.WorkUsed); json.WriteNumber("outputCharacters", budget.OutputCharacters);
            });
            verify("native " + id, bridge ? matched : !compiled && !executed && keys is null && budget.WorkUsed == 0, classification ?? "");
        }
        file.Flush();
        verify("native95 classification totals", count == 95 && lines.Length == 95 && bridges == 77 && constructors == 14 && rounding == 4,
            $"records={count};bridges={bridges};constructorOnly={constructors};roundingUnqualified={rounding}");
    }

    private static void WritePublicProbes(string directory, Action<string, bool, string> verify)
    {
        using var plan = JsonDocument.Parse(ReadEmbedded("DiffBeacon.LineTransformSizeLiteralCases"));
        using var proposals = JsonDocument.Parse(ReadEmbedded("DiffBeacon.LineTransformSizeLiteralProbes"));
        using var file = NewFile(directory, "public-probes.ndjson");
        foreach (var nativeId in new[] { "constructor-probe-10", "constructor-stod-underflow" })
        {
            var item = plan.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("id").GetString() == nativeId);
            var expression = item.GetProperty("nativeCase").GetProperty("input").GetString()!;
            var reference = item.GetProperty("nativeReference");
            var expected = reference.GetProperty("record").GetProperty("serializedUtf8Hex").GetString();
            string[]? keys = null; Exception? error = null; var budget = new LineTransformBudget(); var compiled = false;
            var source = new[] { "public direct constructor edge source RAW" }; var before = SourceSha(source);
            try { var producer = LineTransformPlan.Compile([new(expression, TablePaneMask.All)], budget); compiled = true;
                keys = producer.Transform(source, TablePaneMask.Left, false); }
            catch (Exception exception) when (ExpectedError(exception)) { error = exception; }
            var matched = expected == "30" && compiled && error is null && keys is { Length: 1 } && Hex(Utf8.GetBytes(keys[0])) == "30" && SourceSha(source) == before;
            Row(file, json => { json.WriteString("id", "additional-public-probe-" + nativeId); json.WriteString("originalNativeId", nativeId);
                json.WriteString("originalRecordSha256", reference.GetProperty("lineSha256WithoutTerminator").GetString());
                json.WriteString("expression", expression); json.WriteBoolean("nativeParserExecuted", false);
                json.WriteBoolean("nativeDirectiveExecuted", false); json.WriteBoolean("actualTypedObserved", false); json.WriteBoolean("contextEquivalent", false);
                json.WriteString("qualification", "separate CSharp public direct expression observation; main95 historical skip unchanged");
                json.WriteBoolean("producerCompiled", compiled); json.WriteBoolean("matched", matched); WriteKeys(json, keys); WriteError(json, error);
                json.WriteString("expectedSerializedUtf8Hex", expected); json.WriteNumber("workUsed", budget.WorkUsed);
                json.WriteString("sourceShaBefore", before); json.WriteString("sourceShaAfter", SourceSha(source)); });
            verify("separate public direct " + nativeId, matched, "Native constructor catch0 only; not native parser equivalence");
        }
        foreach (var proposal in proposals.RootElement.GetProperty("tokenRuleProbes").EnumerateArray())
        {
            var expression = proposal.GetProperty("text").GetString()!;
            var shouldCompile = proposal.GetProperty("sizeLexemeMatchesStaticRule").GetBoolean();
            Probe("public-token-" + expression, expression, shouldCompile, "static token proposal; CSharp Compile observation only");
        }
        var upper = proposals.RootElement.GetProperty("unsupported").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "int64-upper-cast");
        foreach (var input in upper.GetProperty("inputs").EnumerateArray())
            Probe("public-reject-" + input.GetString(), input.GetString()!, false, "Native UB cast unexecuted; no golden expected value");
        file.Flush();
        void Probe(string id, string expression, bool shouldCompile, string qualification)
        {
            var budget = new LineTransformBudget(); var compiled = false; Exception? error = null;
            try { LineTransformPlan.Compile([new(expression, TablePaneMask.All)], budget); compiled = true; }
            catch (Exception exception) when (ExpectedError(exception)) { error = exception; }
            var passed = compiled == shouldCompile && (compiled || error is not null);
            Row(file, json => { json.WriteString("id", id); json.WriteString("expression", expression); json.WriteString("qualification", qualification);
                json.WriteBoolean("nativeExecuted", false); json.WriteBoolean("actualTypedObserved", false); json.WriteBoolean("contextEquivalent", false);
                json.WriteBoolean("producerCompiled", compiled); json.WriteBoolean("expectedCompileSuccess", shouldCompile); json.WriteBoolean("passed", passed);
                json.WriteNull("nativeGoldenValue"); json.WriteNumber("workUsed", budget.WorkUsed); WriteKeys(json, null); WriteError(json, error); });
            verify(id, passed, qualification);
        }
    }

    private static string SourceSha(string[] source)
    {
        using var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartArray();
            foreach (var value in source) json.WriteStringValue(value);
            json.WriteEndArray(); json.Flush();
        }
        return Hex(SHA256.HashData(output.ToArray()));
    }
    private static void WriteEngineeringDiagnostics(string directory, Action<string, bool, string> verify)
    {
        using var file = NewFile(directory, "engineering.ndjson");
        var rules = new[] { new LineTransformRule("@noopt If(LineNumber > 0, 1.5KB, Line)", TablePaneMask.All) };
        var source = new[] { "left raw\r", "right RAW", "" }; var before = SourceSha(source);
        var budget = new LineTransformBudget(); var plan = LineTransformPlan.Compile(rules, budget);
        var compileWork = budget.WorkUsed; var keys = plan.Transform(source, TablePaneMask.Left, false); var firstWork = budget.WorkUsed;
        var second = plan.Transform(source, TablePaneMask.Right, false); var secondWork = budget.WorkUsed;
        var limited = new LineTransformBudget(maximumWork: firstWork); var limitedPlan = LineTransformPlan.Compile(rules, limited);
        limitedPlan.Transform(source, TablePaneMask.Left, false); var rejected = false;
        try { limitedPlan.Transform(source, TablePaneMask.Right, false); } catch (InvalidOperationException) { rejected = true; }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var compileCancelled = false; var transformCancelled = false;
        try { LineTransformPlan.Compile(rules, new LineTransformBudget(), cancelled.Token); } catch (OperationCanceledException) { compileCancelled = true; }
        try { plan.Transform(source, TablePaneMask.Left, false, cancellationToken: cancelled.Token); } catch (OperationCanceledException) { transformCancelled = true; }
        var ok = keys.All(key => key == "1536") && keys.SequenceEqual(second) && secondWork > firstWork && firstWork > compileWork
            && rejected && compileCancelled && transformCancelled && SourceSha(source) == before;
        Row(file, json => { json.WriteString("id", "engineering-shared-budget-cancellation"); json.WriteString("qualification", "engineering observation; not fixed Native95");
            json.WriteNumber("compileWork", compileWork); json.WriteNumber("firstWork", firstWork); json.WriteNumber("secondWork", secondWork);
            json.WriteNumber("limitedWork", limited.WorkUsed); json.WriteBoolean("limitedRejected", rejected);
            json.WriteBoolean("compileCancelled", compileCancelled); json.WriteBoolean("transformCancelled", transformCancelled);
            json.WriteBoolean("passed", ok); WriteKeys(json, keys); json.WriteString("sourceShaBefore", before); json.WriteString("sourceShaAfter", SourceSha(source)); });
        file.Flush(); verify("shared budget and cancellation", ok, "engineering.ndjson");
        // 追加engineering観測。固定Native95/public-probes18の分類・実行状態は変更しない。
        foreach (var test in new (string Id, string Adjacent, string Spaced, string ExpectedHex)[]
        {
            ("size-and", "1KB==1KBAND TRUE", "1KB==1KB AND TRUE", "74727565"),
            ("integer-and", "1KB==1024AND TRUE", "1KB==1024 AND TRUE", "74727565"),
            ("integer-or", "1B==1OR FALSE", "1B==1 OR FALSE", "74727565"),
            ("size-contains", "1KBCONTAINS 1B", "1KB CONTAINS 1B", "6e6f6e65")
        })
        {
            Observe("engineering-adjacency-" + test.Id + "-adjacent", test.Adjacent, test.ExpectedHex, true,
                "engineering-adjacency-public-serialized-observation", null);
            Observe("engineering-adjacency-" + test.Id + "-spaced", test.Spaced, test.ExpectedHex, true,
                "engineering-adjacency-public-serialized-observation", null);
        }
        foreach (var expression in new[] { "1KBB", "1KB2", "1KBANDTRUE", "1KiB", "1ns" })
            Observe("engineering-reject-" + expression, expression, null, false, "engineering-public-rejection-observation", null);
        using var roundingPlan = JsonDocument.Parse(ReadEmbedded("DiffBeacon.LineTransformSizeLiteralCases"));
        var roundingCount = 0;
        foreach (var item in roundingPlan.RootElement.GetProperty("cases").EnumerateArray()
            .Where(x => x.GetProperty("classification").GetString() == "defined-cast-platform-rounding-observation"))
        {
            roundingCount++;
            Observe("engineering-public-" + item.GetProperty("id").GetString(), item.GetProperty("nativeCase").GetProperty("input").GetString()!,
                item.GetProperty("nativeReference").GetProperty("record").GetProperty("serializedUtf8Hex").GetString(), true,
                "defined-cast-platform-rounding-observation", item);
        }
        verify("engineering separate rounding count", roundingCount == 4, "engineering observation; main95 remains unexecuted for rounding");
        file.Flush();

        void Observe(string id, string expression, string? expectedHex, bool shouldCompile, string classification, JsonElement? frozenCase)
        {
            var input = new[] { "engineering source RAW " + id }; var original = input.ToArray(); var sourceBefore = SourceSha(input);
            var observationBudget = new LineTransformBudget(); var compiled = false; var executed = false;
            string[]? actual = null; Exception? error = null;
            try
            {
                var producer = LineTransformPlan.Compile([new(expression, TablePaneMask.Left)], observationBudget);
                compiled = true; actual = producer.Transform(input, TablePaneMask.Left, false); executed = true;
            }
            catch (Exception exception) when (ExpectedError(exception)) { error = exception; }
            var retained = input.SequenceEqual(original) && SourceSha(input) == sourceBefore;
            var passed = retained && (shouldCompile
                ? compiled && executed && error is null && actual is { Length: 1 } && Hex(Utf8.GetBytes(actual[0])) == expectedHex
                : !compiled && !executed && error is not null && actual is null);
            Row(file, json =>
            {
                json.WriteString("id", id); json.WriteBoolean("engineering", true); json.WriteString("classification", classification);
                json.WriteString("qualification", "engineering public Compile/Transform serialized observation; not original Native parser/directive/typed compatibility; historical main95/public-probes18 unchanged");
                json.WriteString("expression", expression); json.WriteBoolean("expectedCompileSuccess", shouldCompile);
                json.WriteBoolean("publicCompileExecuted", true); json.WriteBoolean("producerCompiled", compiled); json.WriteBoolean("executed", executed);
                json.WriteBoolean("nativeParserExecuted", false); json.WriteBoolean("nativeDirectiveExecuted", false);
                json.WriteBoolean("actualTypedObserved", false); json.WriteBoolean("actualTypedValueObserved", false); json.WriteBoolean("contextEquivalent", false);
                json.WriteString("expectedSerializedUtf8Hex", expectedHex); json.WriteBoolean("passed", passed);
                json.WriteBoolean("sourceRetained", retained); json.WriteString("sourceShaBefore", sourceBefore); json.WriteString("sourceShaAfter", SourceSha(input));
                json.WriteNumber("workUsed", observationBudget.WorkUsed); json.WriteNumber("outputCharacters", observationBudget.OutputCharacters);
                if (frozenCase is JsonElement item)
                {
                    json.WriteString("originalNativeId", item.GetProperty("id").GetString());
                    json.WritePropertyName("nativeReference"); item.GetProperty("nativeReference").WriteTo(json);
                }
                WriteKeys(json, actual); WriteError(json, error);
            });
            verify(id, passed, "engineering.ndjson; serialized public observation only");
        }
    }

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
            var dialog = shown ?? throw new InvalidOperationException("SizeLiteral scope dialogがありません。");
            for (var index = 0; index < masks.Length; index++)
            {
                dialog.Target.SelectedIndex = index; dialog.Expression.Text = "@noopt 1.5KB";
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
                var rule = new LineTransformRule(mode == "Table" ? "@cs @noopt If(LineNumber = 1, Line, 1.5KB)" : "@cs @noopt 1.5KB", mask);
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
                    json.WriteString("qualification", "GUI-derived cases; Native95 contexts not retargeted"); json.WriteBoolean("rawRetained", rawRetained);
                    json.WriteBoolean("classificationExpected", classified); json.WriteBoolean("ignoredRowObserved", ignored); json.WriteBoolean("passed", ok);
                    json.WriteStartArray("inputSha256"); foreach (var sha in inputSha) json.WriteStringValue(sha); json.WriteEndArray(); });
                verify(prefix + " " + mask, ok, "source-scopes.ndjson"); screenshot("line-transform-size-" + prefix + "-" + mask + ".png");
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
            var rule = new LineTransformRule("@cs @noopt If(LineNumber = 1, Line, 1.5KB)", TablePaneMask.All);
            var oldShown = LineTransformDialog.Shown; LineTransformDialog? shown = null;
            try
            {
                LineTransformDialog.Shown = dialog => shown = dialog; pane.SetLineTransforms([]); Click(Caption(pane, "行変換…")); Settle();
                var dialog = shown ?? throw new InvalidOperationException("SizeLiteraldialogが開きません。");
                foreach (var expression in new[] { "", ".1KB", "1.KB", "1e2KB", "1 KB", "1PB" })
                {
                    dialog.Expression.Text = expression; Click(Named(dialog, "LineTransformAdd"));
                    verify(function + " dialog invalid " + expression, dialog.Rules.Count == 0 && !string.IsNullOrEmpty(dialog.Error.Text), "");
                }
                dialog.Expression.Text = rule.Expression; Click(Named(dialog, "LineTransformAdd"));
                Settle();
                using (var bitmap = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("SizeLiteraldialogのPNGがありません。"))
                using (var png = NewFile(directory, function + "-dialog.png")) bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                using (var bounds = NewFile(directory, "SizeLiteral-dialog-bounds.ndjson"))
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
                        verify("dialog reachable " + control.Name, reachable, "SizeLiteral-dialog-bounds.ndjson");
                    }
                    bounds.Flush();
                }
                Click(dialog.Accept); Click(dialog.Accept); Settle();
                verify(function + " duplicate apply", pane.CaptureProject().LineTransformRules.SequenceEqual([rule]), "");
                shown = null; Click(Caption(pane, "行変換…")); Settle(); dialog = shown ?? throw new InvalidOperationException("SizeLiteraldialogが再開しません。");
                dialog.Expression.Text = "2KB"; Click(Named(dialog, "LineTransformAdd")); Click(dialog.Cancel); Settle();
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
                pump(saved ?? throw new InvalidOperationException("SizeLiteralTableの実保存Taskが観測されません。")); Settle();
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
            pane.SetLineTransforms([new("@noopt 1KB / (LineNumber-LineNumber)", TablePaneMask.All)]); pump(pane.CompareProjectAsync()); Settle();
            verify(function + " runtime failure preserves view", ReferenceEquals(adopted, Table()) && Equals(state, pane.CaptureAdoptionState()) && pane.LeftEditor.Text == edited, "");
            var ready = pane.TableReadyForAdoption;
            try
            {
                pane.SetLineTransforms([rule]); pane.TableReadyForAdoption = () => Click(Caption(pane, "中止")); pump(pane.CompareProjectAsync()); Settle();
                verify(function + " cancel preserves view", ReferenceEquals(adopted, Table()) && Equals(state, pane.CaptureAdoptionState()), "");
                var later = rule with { Expression = "@noopt 2KB" };
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
            screenshot("line-transform-size-" + function + "-table.png");
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
                results.Add((label, ok)); screenshot("line-transform-size-" + name + "-" + label + ".png");
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
        using var resource = typeof(HeadlessLineTransformSizeLiteralChecks).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("SizeLiteral固定resourceがありません: " + name);
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
            if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("SizeLiteral検証は外部出力が必要です。");
        }
        if (full.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase))) throw new IOException("禁止名の出力です。");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("リンク経由の出力です。");
    }
}
