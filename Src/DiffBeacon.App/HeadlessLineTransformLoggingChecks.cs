using System.Text.Json.Serialization;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessLineTransformLoggingChecks
{
    private sealed class Capture : ILineTransformLogEnvironment
    {
        internal List<(LineTransformLogLevel Level, string Message)> Events { get; } = [];
        public void Write(LineTransformLogLevel level, string message) => Events.Add((level, message));
    }

    internal static void Run(Action<string, bool, string> check)
    {
        void Verify(string name, bool passed, string detail = "") => check("line transform logging " + name, passed, detail);

        var capture = new Capture();
        var named = LineTransformPlan.Compile(
            [new("@name=\"Rule Alpha Ω\" LogWarn(\"value\",Array(1,2),true)", TablePaneMask.Left)],
            new LineTransformBudget(), logEnvironment: capture);
        var namedResult = named.Transform(["source"], TablePaneMask.Left, false).Single();
        Verify("quoted name, severity, argument serialization and typed result",
            namedResult == "true" && capture.Events.SequenceEqual([(LineTransformLogLevel.Warning, "[Rule Alpha Ω] value [1, 2] true")]),
            $"result={namedResult}; events={string.Join(" | ", capture.Events.Select(item => $"{item.Level}:{item.Message}"))}");

        capture.Events.Clear();
        var nested = LineTransformPlan.Compile(
            [new("@name=old @NAME=\"Outer Name\" LogInfo(\"outer\",LogError(\"inner\"))", TablePaneMask.Left)],
            new LineTransformBudget(), logEnvironment: capture);
        var nestedResult = nested.Transform(["source"], TablePaneMask.Left, false).Single();
        Verify("nested calls are left-to-right and last name wins",
            nestedResult == "inner" && capture.Events.SequenceEqual([
                (LineTransformLogLevel.Error, "[Outer Name] inner"),
                (LineTransformLogLevel.Information, "[Outer Name] outer inner")]),
            $"result={nestedResult}; events={string.Join(" | ", capture.Events.Select(item => $"{item.Level}:{item.Message}"))}");

        capture.Events.Clear();
        var typed = LineTransformPlan.Compile(
            [new("LogInfo(7)+1", TablePaneMask.Left)], new LineTransformBudget(), logEnvironment: capture);
        var typedResult = typed.Transform(["source"], TablePaneMask.Left, false).Single();
        Verify("last argument retains its numeric type for later operations",
            typedResult == "8" && capture.Events.SequenceEqual([(LineTransformLogLevel.Information, "7")]),
            $"result={typedResult}; events={string.Join(" | ", capture.Events.Select(item => $"{item.Level}:{item.Message}"))}");

        capture.Events.Clear();
        var lazy = LineTransformPlan.Compile(
            [new("If(false,LogError(\"skipped\"),LogInfo(\"selected\"))", TablePaneMask.Left)],
            new LineTransformBudget(), logEnvironment: capture);
        Verify("lazy branch only logs selected expression",
            lazy.Transform(["source"], TablePaneMask.Left, false).Single() == "selected"
                && capture.Events.SequenceEqual([(LineTransformLogLevel.Information, "selected")]));

        capture.Events.Clear();
        var failure = LineTransformPlan.Compile(
            [new("LogInfo(LogWarn(\"inner\"),1/(LineNumber-LineNumber))", TablePaneMask.Left)], new LineTransformBudget(), logEnvironment: capture);
        var failed = false;
        try { _ = failure.Transform(["source"], TablePaneMask.Left, false); }
        catch (DivideByZeroException) { failed = true; }
        Verify("argument failure suppresses outer log but keeps completed inner log",
            failed && capture.Events.SequenceEqual([(LineTransformLogLevel.Warning, "inner")]));

        capture.Events.Clear();
        var compileOnly = LineTransformPlan.Compile(
            [new("LogInfo(\"constant\")", TablePaneMask.Left)], new LineTransformBudget(), logEnvironment: capture);
        Verify("compile does not execute side effects", capture.Events.Count == 0);
        _ = compileOnly.Transform(["source"], TablePaneMask.Left, false);
        Verify("optimized evaluation emits one event", capture.Events.Count == 1);
        capture.Events.Clear();
        _ = LineTransformPlan.Compile(
            [new("@noopt LogInfo(\"constant\")", TablePaneMask.Left)], new LineTransformBudget(), logEnvironment: capture)
            .Transform(["source"], TablePaneMask.Left, false);
        Verify("noopt has the same single runtime event",
            capture.Events.SequenceEqual([(LineTransformLogLevel.Information, "constant")]));

        var invalidArity = false;
        var invalidName = false;
        try { _ = LineTransformPlan.Compile([new("LogError()", TablePaneMask.Left)], new LineTransformBudget()); }
        catch (ArgumentException) { invalidArity = true; }
        try { _ = LineTransformPlan.Compile([new("@name=\"missing LogInfo(1)", TablePaneMask.Left)], new LineTransformBudget()); }
        catch (ArgumentException) { invalidName = true; }
        Verify("zero arguments and unterminated quoted directive reject", invalidArity && invalidName);

        var withoutSink = LineTransformPlan.Compile(
            [new("LogInfo(\"no sink\")", TablePaneMask.Left)], new LineTransformBudget());
        Verify("missing sink is an explicit no-op", withoutSink.Transform(["source"], TablePaneMask.Left, false).Single() == "no sink");

        Verify("runtime sink is excluded from comparison-options JSON",
            typeof(ComparisonOptions).GetProperty(nameof(ComparisonOptions.LineTransformLogEnvironment))?
                .IsDefined(typeof(JsonIgnoreAttribute), inherit: true) == true);
    }
}
