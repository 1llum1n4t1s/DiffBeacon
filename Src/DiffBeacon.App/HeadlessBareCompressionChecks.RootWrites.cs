using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static partial class HeadlessBareCompressionChecks
{
    private static byte[] ProjectJson(ComparisonProject project) => JsonSerializer.SerializeToUtf8Bytes(project, ProjectJsonContext.Default.ComparisonProject);
    private static void WriteProject(string path, ComparisonProject project) => File.WriteAllBytes(path, ProjectJson(project));
    private sealed record RootWriteResult(string Codec, string Side, string Kind, string Source, string BeforeSha256,
        string AfterSha256, string AdoptedKind, string[] Rows, string Zip, string Extract, string[] RelativePaths);

    // 失敗条件: picker未採用・左右混線・FileをTARとして再解釈・ZIP/展開の欠落/余剰/byte差・原本変更。
    private static void CheckRootWrites(MainWindow caller, string folder, JsonElement[] cases, Action<Task> pump, Action<string,bool,string> check)
    {
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        var results = new List<RootWriteResult>();
        var owner = new MainWindow(null, new ImageApplicationOptionsStore(Path.Combine(folder,"root-write-options.json"))) { Width=1000, Height=700 };
        owner.Show(caller); Dispatcher.UIThread.RunJobs();
        try
        {
            foreach (var codec in new[] { "bz2", "Z" })
            {
                var sourceName = "tar." + codec; var source = Path.Combine(folder, sourceName);
                var before = Hash(File.ReadAllBytes(source));
                var spec = cases.Single(item => item.GetProperty("input").GetString() == sourceName);
                var raw = Convert.FromHexString(spec.GetProperty("decodedHex").GetString()!);
                var tarFiles = new Dictionary<string,byte[]>(StringComparer.Ordinal);
                using (var tar = new TarReader(new MemoryStream(raw)))
                    while (tar.GetNextEntry() is { } entry)
                        if (entry.DataStream is { } data)
                        { using var memory = new MemoryStream(); data.CopyTo(memory); tarFiles.Add(entry.Name, memory.ToArray()); }
                foreach (var right in new[] { false, true })
                foreach (var kind in new[] { CompressionPayloadKind.File, CompressionPayloadKind.Auto, CompressionPayloadKind.Tar })
                {
                    var pane = owner.ActivePane; pane.DiscardChanges();
                    pane.ApplyProject(new() { Mode="Archive", LeftPath=source, RightPath=source });
                    pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs(); var panel = Panel(pane);
                    (right ? panel.RightCompressionPayloadKind : panel.LeftCompressionPayloadKind).SelectedItem = kind;
                    Task? refresh = null; panel.ButtonTaskObserved = (_, task) => refresh = task;
                    try
                    {
                        var button = panel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content,"アーカイブを再比較"));
                        if (!button.IsEnabled) throw new InvalidDataException("root再比較buttonが無効です。");
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (refresh is null) throw new InvalidDataException("root再比較button Task未観測"); pump(refresh); Dispatcher.UIThread.RunJobs();
                    }
                    finally { panel.ButtonTaskObserved = null; }
                    var expected = kind == CompressionPayloadKind.File ? new Dictionary<string,byte[]>(StringComparer.Ordinal) { ["tar"] = raw } : tarFiles;
                    var confirmed = right ? panel.ConfirmedRight : panel.ConfirmedLeft;
                    var rows = panel.Rows.Select(row => right ? row.Right : row.Left).Where(entry => entry is { IsDirectory:false }).Select(entry => entry!.Path).Order(StringComparer.Ordinal).ToArray();
                    var side = right ? "right" : "left"; var id = codec + "-" + side + "-" + kind;
                    check("root-adopt-" + id, confirmed.RootPath == source && confirmed.EntryChain.Count == 0
                        && confirmed.ContainerCompressionPayloadKinds[0] == kind && rows.SequenceEqual(expected.Keys.Order(StringComparer.Ordinal)), "real picker/recompare adopts root kind and all side entries");
                    var zipName = "root-write-" + id + ".zip"; var zipPath = Path.Combine(folder,zipName);
                    pump(panel.RepackToAsync(right, zipPath));
                    using (var zip = ZipFile.OpenRead(zipPath))
                    {
                        var actual = new Dictionary<string,byte[]>(StringComparer.Ordinal);
                        foreach (var entry in zip.Entries)
                        { using var data = entry.Open(); using var memory = new MemoryStream(); data.CopyTo(memory); actual.Add(entry.FullName,memory.ToArray()); }
                        check("root-repack-" + id, actual.Count == expected.Count && expected.All(pair => actual.TryGetValue(pair.Key,out var bytes) && bytes.SequenceEqual(pair.Value)), "new ZIP contains exact confirmed root entries and all bytes");
                    }
                    var extractName = "root-extract-" + id; var extractPath = Path.Combine(folder,extractName);
                    pump(panel.ExtractToAsync(right,extractPath));
                    var paths = Directory.EnumerateFiles(extractPath,"*",SearchOption.AllDirectories).Select(path => Path.GetRelativePath(extractPath,path).Replace('\\','/')).Order(StringComparer.Ordinal).ToArray();
                    check("root-extract-" + id, paths.SequenceEqual(expected.Keys.Order(StringComparer.Ordinal))
                        && expected.All(pair => File.ReadAllBytes(Path.Combine(extractPath,pair.Key)).SequenceEqual(pair.Value))
                        && Hash(File.ReadAllBytes(source)) == before, "all extracted paths/bytes and source preservation");
                    results.Add(new(codec,side,kind.ToString(),sourceName,before,Hash(File.ReadAllBytes(source)),confirmed.ContainerCompressionPayloadKinds[0].ToString(),rows,zipName,extractName,paths));
                }
            }
            using var file = File.Create(Path.Combine(folder,"root-write-proof.json")); using var writer = new Utf8JsonWriter(file,new JsonWriterOptions { Indented=true });
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion",1); writer.WriteNumber("matrixCases",results.Count); writer.WriteNumber("producerChecks",results.Count * 3); writer.WriteStartArray("cases");
            foreach (var result in results)
            {
                writer.WriteStartObject(); writer.WriteString("codec",result.Codec); writer.WriteString("side",result.Side); writer.WriteString("kind",result.Kind);
                writer.WriteString("source",result.Source); writer.WriteString("beforeSha256",result.BeforeSha256); writer.WriteString("afterSha256",result.AfterSha256);
                writer.WriteString("adoptedKind",result.AdoptedKind); writer.WriteString("zip",result.Zip); writer.WriteString("extract",result.Extract);
                writer.WriteStartArray("rows"); foreach (var path in result.Rows) writer.WriteStringValue(path); writer.WriteEndArray();
                writer.WriteStartArray("relativePaths"); foreach (var path in result.RelativePaths) writer.WriteStringValue(path); writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        finally { owner.ActivePane.DiscardChanges(); owner.Close(); caller.Activate(); }
    }
}
