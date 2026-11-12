using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class TextComparisonSources
{
    internal static string? PathOf(TextDocument? document)
        => document is { Path.Length: > 0 } ? document.Path : null;

    internal static TextComparisonSourcePaths FromDocuments(TextDocument? left, TextDocument? ancestor, TextDocument? right)
        => new(PathOf(left), PathOf(ancestor), PathOf(right), EncodingOf(left), EncodingOf(ancestor), EncodingOf(right));

    internal static TextComparisonSourcePaths ForProject(ComparisonProject project,
        TextDocument? left, TextDocument? ancestor, TextDocument? right)
    {
        var leftPath = !ProjectInputs.IsUntitled(project, 0) && ProjectInputs.Archive(project, 0) is null ? PathOf(left) : null;
        var basePath = !ProjectInputs.IsUntitled(project, 1) && ProjectInputs.Archive(project, 1) is null ? PathOf(ancestor) : null;
        var rightPath = !ProjectInputs.IsUntitled(project, 2) && ProjectInputs.Archive(project, 2) is null ? PathOf(right) : null;
        return new(leftPath, basePath, rightPath,
            leftPath is null ? null : left?.EncodingName,
            basePath is null ? null : ancestor?.EncodingName,
            rightPath is null ? null : right?.EncodingName);
    }

    private static string? EncodingOf(TextDocument? document)
        => PathOf(document) is null ? null : document!.EncodingName;
}
