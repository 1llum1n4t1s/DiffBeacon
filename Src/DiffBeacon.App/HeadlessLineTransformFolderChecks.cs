using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessLineTransformFolderChecks
{
    internal static void Run(string output, Action<string, bool, string> check, Action<Task> pump)
    {
        var directory = Path.Combine(output, "line-transform-folder");
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false);
        var evidence = new List<(string Name, bool Passed, string Detail)>();
        var inputs = new Dictionary<string, byte[]>();
        void Verify(string name, bool passed, string detail)
        {
            evidence.Add((name, passed, detail));
            check("line transform folder " + name, passed, detail);
        }
        var masks = new[] { TablePaneMask.Left, TablePaneMask.Base, TablePaneMask.Right };
        var pairs = new[] { (First: 1, Second: 0), (First: 1, Second: 2), (First: 0, Second: 2) };
        DirectoryThreeWayComparison Compare(string name, string?[] texts, LineTransformRule[] rules)
        {
            var roots = Enumerable.Range(0, 3).Select(side => Path.Combine(directory, name, side.ToString())).ToArray();
            for (var side = 0; side < 3; side++)
            {
                Directory.CreateDirectory(roots[side]);
                var path = Path.Combine(roots[side], "item");
                if (texts[side] is null) Directory.CreateDirectory(path);
                else
                {
                    File.WriteAllText(path, texts[side], utf8);
                    inputs[path] = File.ReadAllBytes(path);
                }
            }
            var task = DirectoryComparer.CompareThreeWayAsync(roots[0], roots[1], roots[2],
                new() { Mode = DirectoryComparisonMode.Content, TextOptions = new() { LineTransformRules = rules } });
            pump(task);
            var result = task.GetAwaiter().GetResult();
            var entry = result.Entries.Single(entry => entry.RelativePath == "item");
            if (texts.Any(text => text is null))
                Verify(name + " type conflict", entry.Status == DirectoryDifferenceKind.TypeConflict
                    && entry.ThreeWay!.Classification is not null,
                    $"status={entry.Status};classification={entry.ThreeWay!.Classification}");
            return entry.ThreeWay!;
        }
        static DirectoryPairComparison Select(DirectoryThreeWayComparison result, int pair) => pair switch
        {
            0 => result.MiddleLeft, 1 => result.MiddleRight, _ => result.LeftRight
        };
        for (var pair = 0; pair < pairs.Length; pair++)
        {
            var (first, second) = pairs[pair];
            var excluded = 3 - first - second;
            foreach (var transformed in new[] { first, second })
            {
                var name = $"pair-{pair}-scope-{transformed}";
                string?[] texts = new string?[3];
                texts[first] = transformed == first ? "old\n" : "new\n";
                texts[second] = transformed == second ? "old\n" : "new\n";
                var result = Compare(name, texts, [new("\"new\"", masks[transformed])]);
                var selected = Select(result, pair);
                Verify(name, selected.Status == DirectoryDifferenceKind.Equal
                    && Enumerable.Range(0, 3).Where(index => index != pair)
                        .All(index => Select(result, index).Status == DirectoryDifferenceKind.TypeConflict),
                    $"target={transformed};excluded={excluded};ML={result.MiddleLeft.Status};MR={result.MiddleRight.Status};LR={result.LeftRight.Status}");
            }
            // 衝突側の限定式を、残る二つの空ファイルへ適用してはならない。
            string?[] empty = new string?[3];
            empty[first] = ""; empty[second] = "";
            var ignored = Compare($"pair-{pair}-excluded-empty", empty, [new("\"changed\"", masks[excluded])]);
            Verify($"pair-{pair}-excluded-empty", Select(ignored, pair).Status == DirectoryDifferenceKind.Equal,
                $"ML={ignored.MiddleLeft.Status};MR={ignored.MiddleRight.Status};LR={ignored.LeftRight.Status}");
            var plain = Compare($"pair-{pair}-no-rules", new string?[] { null, null, null }
                .Select((_, side) => side == excluded ? null : side == first ? "old\n" : "new\n").ToArray(), []);
            Verify($"pair-{pair}-no-rules", Select(plain, pair).Status == DirectoryDifferenceKind.Modified,
                $"ML={plain.MiddleLeft.Status};MR={plain.MiddleRight.Status};LR={plain.LeftRight.Status}");
        }
        var equal = Compare("three-files-default", ["same\n", "same\n", "same\n"], []);
        Verify("three-files-default", equal.MiddleLeft.Status == DirectoryDifferenceKind.Equal
            && equal.MiddleRight.Status == DirectoryDifferenceKind.Equal && equal.LeftRight.Status == DirectoryDifferenceKind.Equal,
            $"ML={equal.MiddleLeft.Status};MR={equal.MiddleRight.Status};LR={equal.LeftRight.Status}");
        var transformedEqual = Compare("three-files-left", ["old\n", "new\n", "new\n"], [new("\"new\"", TablePaneMask.Left)]);
        Verify("three-files-left", transformedEqual.MiddleLeft.Status == DirectoryDifferenceKind.Equal
            && transformedEqual.MiddleRight.Status == DirectoryDifferenceKind.Equal && transformedEqual.LeftRight.Status == DirectoryDifferenceKind.Equal,
            $"ML={transformedEqual.MiddleLeft.Status};MR={transformedEqual.MiddleRight.Status};LR={transformedEqual.LeftRight.Status}");
        foreach (var (path, original) in inputs)
            Verify("input preserved " + Path.GetRelativePath(directory, path),
                File.ReadAllBytes(path).AsSpan().SequenceEqual(original), Convert.ToHexString(SHA256.HashData(original)));
        using var stream = File.Create(Path.Combine(directory, "assertions.json"));
        using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
        writer.WriteStartArray();
        foreach (var item in evidence)
        {
            writer.WriteStartObject();
            writer.WriteString("name", item.Name); writer.WriteBoolean("passed", item.Passed);
            writer.WriteString("detail", item.Detail); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
