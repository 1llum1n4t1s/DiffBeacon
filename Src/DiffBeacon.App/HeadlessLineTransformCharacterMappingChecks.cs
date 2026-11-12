using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessLineTransformCharacterMappingChecks
{
    // 想定失敗: 最適化による環境呼出し、noneの変換、配列への再帰、scope逸脱、予算・取消の無視。
    internal static void Run(string output, Action<string, bool, string> check)
    {
        var observations = new List<LineTransformCharacterMappingObservation>();
        void Verify(string name, bool passed)
        {
            check("文字種行変換 " + name, passed, "");
            observations.Add(new(name, passed));
        }
        var functions = new[] { "ToHalfWidth", "ToFullWidth", "ToHiragana", "ToKatakana", "ToSimplifiedChinese", "ToTraditionalChinese" };
        foreach (var function in functions)
            foreach (var noopt in new[] { false, true })
            {
                var environment = new CountingEnvironment();
                var prefix = "@cs " + (noopt ? "@noopt " : "");
                LineTransformPlan Compile(string expression, TablePaneMask scope = TablePaneMask.All, LineTransformBudget? budget = null)
                    => LineTransformPlan.Compile([new(prefix + expression, scope)], budget ?? new(), stringEnvironment: environment);
                var plan = Compile(function + "(Array(\"value\",none,Array(\"nested\",true)))");
                Verify(function + " not folded " + noopt, environment.Values.Count == 0);
                var actual = plan.Transform(["raw"], TablePaneMask.Left, false).Single();
                Verify(function + " one level " + noopt, actual == "[value, none, [nested, true]]"
                    && environment.Values.Select(value => value.Value).SequenceEqual(["value", "[nested, true]"])
                    && environment.Values.All(value => value.Mapping.ToString().Equals(function[2..], StringComparison.Ordinal)));
                environment.Values.Clear();
                actual = Compile("If(false," + function + "(Line),\"kept\")").Transform(["raw"], TablePaneMask.Left, false).Single();
                Verify(function + " lazy branch " + noopt, actual == "kept" && environment.Values.Count == 0);
                foreach (var scope in new[] { TablePaneMask.Left, TablePaneMask.Base, TablePaneMask.Right })
                    foreach (var side in new[] { TablePaneMask.Left, TablePaneMask.Base, TablePaneMask.Right })
                    {
                        environment.Values.Clear();
                        var key = Compile(function + "(Line)", scope).Transform(["raw"], side, false).Single();
                        Verify(function + " scope " + scope + "/" + side + "/" + noopt,
                            key == "raw" && environment.Values.Count == (scope == side ? 1 : 0));
                    }
                foreach (var directive in new[] { "@ci ", "@cs " })
                {
                    environment.Values.Clear();
                    plan = LineTransformPlan.Compile([new(directive + function + "(Line)", TablePaneMask.All)], new(), stringEnvironment: environment);
                    Verify(function + " directive " + directive + noopt,
                        plan.Transform(["Ab"], TablePaneMask.Left, false).Single() == "Ab" && environment.Values.Single().Value == "Ab");
                }
                var measurement = new LineTransformBudget();
                var measured = Compile(function + "(Line)", budget: measurement);
                var compileOutput = measurement.OutputCharacters;
                measured.Transform(["raw"], TablePaneMask.Left, false);
                var budget = new LineTransformBudget(maximumOutputCharacters: measurement.OutputCharacters);
                plan = Compile(function + "(Line)", budget: budget);
                string[] source = ["raw"];
                plan.Transform(source, TablePaneMask.Left, false);
                var rejected = false;
                try { plan.Transform(source, TablePaneMask.Right, false); }
                catch (InvalidOperationException) { rejected = true; }
                Verify(function + " shared output " + noopt, rejected && compileOutput < measurement.OutputCharacters && source.Single() == "raw");
                var workMeasurement = new LineTransformBudget();
                var workPlan = Compile(function + "(Line)", budget: workMeasurement);
                var compileWork = workMeasurement.WorkUsed;
                workPlan.Transform(source, TablePaneMask.Left, false);
                plan = Compile(function + "(Line)", budget: new LineTransformBudget(maximumWork: workMeasurement.WorkUsed));
                plan.Transform(source, TablePaneMask.Left, false);
                rejected = false;
                try { plan.Transform(source, TablePaneMask.Right, false); }
                catch (InvalidOperationException) { rejected = true; }
                Verify(function + " shared work " + noopt, rejected && compileWork < workMeasurement.WorkUsed && source.Single() == "raw");
                using var cancel = new CancellationTokenSource();
                environment.AfterMap = cancel.Cancel;
                plan = Compile(function + "(Line)");
                var canceled = false;
                try { plan.Transform(source, TablePaneMask.Left, false, cancellationToken: cancel.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Verify(function + " cancellation " + noopt, canceled && source.Single() == "raw");
                environment.Values.Clear();
                canceled = false;
                try { plan.Transform(source, TablePaneMask.Left, false, cancellationToken: cancel.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Verify(function + " pre-cancellation " + noopt, canceled && environment.Values.Count == 0);
            }
        var production = LineTransformStringEnvironments.ForRules([new("ToHalfWidth(Line)", TablePaneMask.All)])!;
        Verify("embedded NUL", production.MapCharacters("Ａ\0Ｂ", LineTransformCharacterMapping.HalfWidth) == "A\0B");
        var evidence = new LineTransformCharacterMappingEvidence(
            "injected environment plus system embedded-NUL check; not native WinMerge golden", observations.ToArray());
        File.WriteAllText(Path.Combine(output, "line-transform-character-mapping.json"), JsonSerializer.Serialize(
            evidence, LineTransformCharacterMappingJsonContext.Default.LineTransformCharacterMappingEvidence));
    }

    private sealed class CountingEnvironment : ILineTransformStringEnvironment
    {
        internal List<(string Value, LineTransformCharacterMapping Mapping)> Values { get; } = [];
        internal Action? AfterMap { get; set; }
        public string ToLower(string value) => value.ToLowerInvariant();
        public string ToUpper(string value) => value.ToUpperInvariant();
        public string NormalizeUnicode(string value, NormalizationForm form) => value.Normalize(form);
        public string MapCharacters(string value, LineTransformCharacterMapping mapping)
        {
            Values.Add((value, mapping));
            AfterMap?.Invoke();
            return value;
        }
    }
}

internal sealed record LineTransformCharacterMappingObservation(string Name, bool Passed);
internal sealed record LineTransformCharacterMappingEvidence(string Qualification, LineTransformCharacterMappingObservation[] Observations);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LineTransformCharacterMappingEvidence))]
internal partial class LineTransformCharacterMappingJsonContext : JsonSerializerContext;
