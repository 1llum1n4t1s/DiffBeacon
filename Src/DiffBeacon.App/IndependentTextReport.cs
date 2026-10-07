using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class IndependentTextReport
{
    internal static string Pair(ComparisonProject project) => project.TextComparisonPair ?? "LeftMiddle";

    internal static (int First, int Second) PairSides(string pair) => pair switch
    {
        "LeftMiddle" => (0, 1), "MiddleRight" => (1, 2), "LeftRight" => (0, 2),
        _ => throw new InvalidDataException("独立三者Textの比較ペアが不正です。")
    };

    internal static string Role(int side) => side switch
    {
        0 => "left", 1 => "middle", 2 => "right", _ => throw new ArgumentOutOfRangeException(nameof(side))
    };

    internal static string Create(ComparisonProject project, IReadOnlyList<ReportDocument> documents, CancellationToken token,
        IReadOnlyList<TextDocument>? snapshots = null)
    {
        project.TextInputs?.Validate(project);
        if (!ProjectInputs.IsIndependentText(project)) throw new InvalidDataException("独立三者Textの明示入力が必要です。");
        if (snapshots is not null && snapshots.Count != 3)
            throw new InvalidDataException("独立三者Textの文字コードsnapshotには三側が必要です。");
        return HtmlReport.CreateIndependentText(documents, Pair(project), ProjectReport.Options(project), ProjectReport.MaximumBytes, token,
            snapshots, Enumerable.Range(0, 3).Select(side => ProjectInputs.Caption(project, side)).ToArray());
    }
}
