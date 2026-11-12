using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class LineTransformLogEnvironments
{
    internal static ILineTransformLogEnvironment Trace { get; } = new DelegateEnvironment((level, message) =>
        System.Diagnostics.Trace.WriteLine($"[{Label(level)}] {message}"));

    internal static ILineTransformLogEnvironment StandardError { get; } = new DelegateEnvironment((level, message) =>
        Console.Error.WriteLine($"{Label(level)}: {message}"));

    private static string Label(LineTransformLogLevel level) => level switch
    {
        LineTransformLogLevel.Error => "error",
        LineTransformLogLevel.Warning => "warn",
        LineTransformLogLevel.Information => "info",
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    private sealed class DelegateEnvironment(Action<LineTransformLogLevel, string> write) : ILineTransformLogEnvironment
    {
        public void Write(LineTransformLogLevel level, string message) => write(level, message);
    }
}
