using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static class HeadlessTarWrapperChecks
{
    private sealed record Observation(string Stage, bool Canceled, bool SamePanel, bool Rows, bool Preview, bool Operable);
    private sealed record Result(string Name, string Path, string Sha, ManagedArchiveEntry[] Entries, Observation[] Observations, bool Latest, bool Disposed);

    internal static void Run(MainWindow window, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "tar-wrapper-gui"); Directory.CreateDirectory(folder);
        var assembly = typeof(HeadlessTarWrapperChecks).Assembly;
        byte[] Resource(string name)
        {
            using var stream = assembly.GetManifestResourceStream("DiffBeacon.SelfTest.TarWrappers." + name) ?? throw new InvalidOperationException("TAR GUI固定原本がありません: " + name);
            using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
        }
        var manifestBytes = Resource("manifest.json"); var manifestSha = Hash(manifestBytes);
        check("TAR GUI fixed manifest SHA", manifestSha == "40735306000EFE828BBA5C2348C8FCB0CB4D66215A6B03AB257547EED493ADB4", "fixed original manifest");
        using var manifest = JsonDocument.Parse(manifestBytes);
        var cases = manifest.RootElement.GetProperty("cases").EnumerateArray().Where(item => item.GetProperty("valid").GetBoolean()).ToArray();
        foreach (var item in cases)
        {
            var name = item.GetProperty("name").GetString()!; var bytes = Resource(name);
            check("TAR GUI fixed input " + name, Hash(bytes) == item.GetProperty("sha256").GetString(), "full physical bytes");
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
        }
        var pane = window.ActivePane; pane.DiscardChanges(); pane.SelectMode(7);
        var baseline = Path.Combine(folder, "payload.tar.gz.bz2"); SetPaths(baseline); pump(pane.ComparePathsAsync());
        var stop = pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止"));
        var results = new List<Result>();
        foreach (var item in cases)
        {
            var name = item.GetProperty("name").GetString()!; var path = Path.Combine(folder, name); var before = Hash(File.ReadAllBytes(path));
            var observations = new List<Observation>(); ManagedArchiveEntry[]? completed = null;
            var prior = Panel(); Preview(prior); var rows = prior.Rows; var preview = prior.PreviewText;
            SetPaths(path); pane.ArchiveReadStarting = Stop;
            var canceled = Cancel(pane.ComparePathsAsync()); pane.ArchiveReadStarting = null;
            Observe("early-stop", prior, rows, preview, canceled);
            rows = prior.Rows; preview = prior.PreviewText;
            pane.ArchiveReadyForAdoption = candidate => { completed = candidate.Rows.Select(row => row.Left!).ToArray(); Stop(); };
            canceled = Cancel(pane.ComparePathsAsync()); pane.ArchiveReadyForAdoption = null;
            Observe("late-stop", prior, rows, preview, canceled);
            check("TAR GUI completed candidate " + name, completed is not null && completed.Length == item.GetProperty("entries").GetArrayLength(), "real candidate before actual Stop");
            pump(pane.ComparePathsAsync()); var confirmed = Panel(); Preview(confirmed); rows = confirmed.Rows; preview = confirmed.PreviewText;
            confirmed.RefreshReadyForAdoption = Stop; canceled = Cancel(confirmed.RefreshAsync()); confirmed.RefreshReadyForAdoption = null;
            Observe("refresh-stop", confirmed, rows, preview, canceled);
            Task? latest = null; ArchivePanel? discarded = null;
            pane.ArchiveReadyForAdoption = candidate =>
            {
                discarded = candidate; pane.ArchiveReadyForAdoption = null; SetPaths(baseline); latest = pane.ComparePathsAsync();
            };
            try { pump(pane.ComparePathsAsync()); } catch (OperationCanceledException) { }
            if (latest is not null) pump(latest);
            var latestOnly = latest is not null && Panel().LeftSourcePath == baseline && Panel().RightSourcePath == baseline;
            var disposed = discarded?.IsDisposed == true && confirmed.IsDisposed;
            check("TAR GUI stale candidate disposed " + name, latestOnly && disposed, "actual newer comparison adopts only latest input");
            check("TAR GUI physical input preserved " + name, Hash(File.ReadAllBytes(path)) == before, "all bytes SHA before/after");
            results.Add(new(name, path, before, completed!, observations.ToArray(), latestOnly, disposed));

            void Observe(string stage, ArchivePanel owner, IReadOnlyList<ArchiveEntryDifference> expectedRows, string expectedPreview, bool wasCanceled)
            {
                var same = ReferenceEquals(Panel(), owner); var rowsSame = ReferenceEquals(owner.Rows, expectedRows); var previewSame = owner.PreviewText == expectedPreview;
                pump(owner.RefreshAsync()); Preview(owner);
                var operable = !owner.IsDisposed && owner.Rows.Count == expectedRows.Count;
                observations.Add(new(stage, wasCanceled, same, rowsSame, previewSame, operable));
                check("TAR GUI " + stage + " " + name, wasCanceled && same && rowsSame && previewSame && operable, "actual Stop, confirmed display retained and refresh/preview reusable");
            }
        }
        using (var file = File.Create(Path.Combine(folder, "facts.json")))
        using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("manifestSha256", manifestSha); writer.WriteStartArray("cases");
            foreach (var result in results)
            {
                writer.WriteStartObject(); writer.WriteString("name", result.Name); writer.WriteString("inputPath", result.Path);
                writer.WriteString("inputShaBefore", result.Sha); writer.WriteString("inputShaAfter", Hash(File.ReadAllBytes(result.Path)));
                writer.WriteStartArray("candidateEntries");
                foreach (var entry in result.Entries)
                {
                    writer.WriteStartObject(); writer.WriteString("path", entry.Path); writer.WriteBoolean("directory", entry.IsDirectory); writer.WriteNumber("size", entry.Size);
                    writer.WriteString("sha256", entry.Sha256); writer.WriteString("modifiedUtc", entry.LastModifiedTime?.ToUniversalTime().ToString("O")); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteStartArray("observations");
                foreach (var observation in result.Observations)
                {
                    writer.WriteStartObject(); writer.WriteString("stage", observation.Stage); writer.WriteBoolean("canceled", observation.Canceled); writer.WriteBoolean("samePanel", observation.SamePanel);
                    writer.WriteBoolean("rowsPreserved", observation.Rows); writer.WriteBoolean("previewPreserved", observation.Preview); writer.WriteBoolean("operable", observation.Operable); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteBoolean("staleLatestOnly", result.Latest); writer.WriteBoolean("discardedDisposed", result.Disposed); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        screenshot("tar-wrapper-gui-after.png");
        void SetPaths(string path) { pane.LeftPath.Text = pane.RightPath.Text = path; }
        ArchivePanel Panel() => pane.GetVisualDescendants().OfType<ArchivePanel>().Single();
        void Stop() => stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool Cancel(Task task) { try { pump(task); return false; } catch (OperationCanceledException) { return true; } }
        void Preview(ArchivePanel panel) { if (panel.Rows.FirstOrDefault(row => row.Left?.IsDirectory == false) is { } row) pump(panel.PreviewAsync(row)); Dispatcher.UIThread.RunJobs(); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
