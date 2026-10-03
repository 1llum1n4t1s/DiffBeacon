using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static class HeadlessArchiveWrapperChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "archive-wrappers"); Directory.CreateDirectory(folder);
        var payload = Enumerable.Range(0, 73).Select(i => (byte)(i * 37)).ToArray();
        var raw = Zip(payload); var zipped = GZip(raw);
        var path = Path.Combine(folder, "payload.zip.gz"); File.WriteAllBytes(path, zipped);
        var basePath = Path.Combine(folder, "payload.zip"); File.WriteAllBytes(basePath, raw);
        var bad = Path.Combine(folder, "bad.zip.gz"); File.WriteAllBytes(bad, zipped[..^4]);
        var expectedSha = Hash(payload); var originalSha = Hash(zipped);
        var service = new ManagedArchive();
        VerifyContainerMetadata(folder, service, check);
        var manifest = service.ReadManifest(path);
        check("wrapper public manifest bytes/time", manifest.Format == "zip.gz" && manifest.Entries.Count == 1
            && manifest.Entries[0].Sha256 == expectedSha && manifest.Entries[0].LastModifiedTime == new DateTime(2001, 2, 3, 4, 5, 6), "all entries, fixed DOS local time");
        check("wrapper public entry/preview/export", service.ReadEntry(path, "a").SequenceEqual(payload)
            && service.ReadEntryPreview(path, "a").SequenceEqual(payload) && service.ReadEntryForExport(path, "a").SequenceEqual(payload), "full validated container before capture");
        var exact = new ManagedArchiveLimits(MaximumEntryBytes: raw.Length, MaximumDecodedBytes: raw.Length + payload.Length, MaximumEntries: 3, MaximumPathCharacters: 5);
        check("wrapper exact intermediate/cumulative/member/path bounds", new ManagedArchive(exact).ReadManifest(path).Entries[0].Sha256 == expectedSha, "wrapper+member+entry = 3; suffix+key+original = 5");
        Reject("intermediate next byte", exact with { MaximumEntryBytes = raw.Length - 1 }, path);
        Reject("shared decoded next byte", exact with { MaximumDecodedBytes = raw.Length + payload.Length - 1 }, path);
        Reject("shared items next entry", exact with { MaximumEntries = 2 }, path);
        Reject("shared characters next byte", exact with { MaximumPathCharacters = 4 }, path);
        var twice = Path.Combine(folder, "payload.zip.gz.gz"); File.WriteAllBytes(twice, GZip(zipped));
        check("wrapper two layers public read", service.ReadEntry(twice, "a").SequenceEqual(payload), "mixed layer counters not reset");
        Reject("depth before next allocation", exact with { MaximumWrapperDepth = 1 }, twice);
        var empty = Path.Combine(folder, "empty-member.zip.gz"); File.WriteAllBytes(empty, [.. GZip([]), .. zipped]);
        Reject("empty member shared count", exact, empty);
        check("wrapper empty member valid within shared budget", new ManagedArchive(exact with { MaximumEntries = 4 }).ReadManifest(empty).Entries[0].Sha256 == expectedSha, "empty member cannot bypass count");
        var lower = 1L; var upper = 1024L * 1024;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (Accept(exact with { MaximumWorkBytes = middle }, path)) upper = middle; else lower = middle + 1;
        }
        check("wrapper exact public work boundary", Accept(exact with { MaximumWorkBytes = lower }, path) && !Accept(exact with { MaximumWorkBytes = lower - 1 }, path), "minimum observed actual IO/output/metadata budget; not CPU instructions");
        check("wrapper integer extreme work budget", Accept(exact with { MaximumDecodedBytes = long.MaxValue, MaximumWorkBytes = long.MaxValue, MaximumWrapperDepth = int.MaxValue }, path), "subtraction without overflow");
        foreach (var limits in new[] { exact with { MaximumWrapperDepth = 0 }, exact with { MaximumWorkBytes = 0 }, exact with { MaximumWorkBytes = -1 } })
        {
            var rejected = false; try { _ = new ManagedArchive(limits); } catch (ArgumentOutOfRangeException) { rejected = true; }
            check("wrapper invalid constructor limits", rejected, "positive optional limits");
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); var rejected = false;
            try { service.ReadManifest(twice, cancellationToken: canceled.Token); } catch (OperationCanceledException) { rejected = true; }
            check("wrapper public cancellation retains inputs", rejected && Hash(File.ReadAllBytes(path)) == originalSha, "pre-cancel public contract; per-layer exact interruption is not inferred");
        }
        pane.DiscardChanges(); pane.SelectMode(0); pane.LeftPath.Text = pane.RightPath.Text = path;
        pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
        var panel = pane.GetVisualDescendants().OfType<ArchivePanel>().Single();
        pump(panel.PreviewAsync(panel.Rows.Single()));
        var rows = panel.Rows; var preview = panel.PreviewText;
        var stop = pane.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "中止"));
        var observed = new List<(string Stage, bool Rows, bool Preview, bool Source, bool Active)>();
        Observe("initial"); screenshot("archive-wrappers-initial.png");
        var canceledBeforeModal = false; var unexpectedModal = 0;
        pane.ArchiveRetryShown = dialog => { unexpectedModal++; dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        Dispatcher.UIThread.Post(() => { canceledBeforeModal = true; stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
        pane.LeftPath.Text = pane.RightPath.Text = twice;
        ExpectCanceled(pane.ComparePathsAsync()); Observe("early-stop");
        check("wrapper real Stop before modal", canceledBeforeModal && unexpectedModal == 0, "owner enabled, queued before comparison read continuation");
        pane.ArchiveRetryShown = null;
        // 破損候補の実 modal Cancel/×、旧 panel の再操作。
        pane.LeftPath.Text = pane.RightPath.Text = bad;
        var modalCount = 0;
        pane.ArchiveRetryShown = dialog =>
        {
            modalCount++; Observe("failure-modal");
            check("wrapper retry masked optional bounded fields", dialog.LeftPassword.PasswordChar == '●' && dialog.RightPassword.PasswordChar == '●'
                && dialog.LeftPassword.MaxLength == 4096 && dialog.RightPassword.MaxLength == 4096, "no password values in evidence");
            dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        ExpectCanceled(pane.ComparePathsAsync()); Observe("failure-cancel");
        pane.ArchiveRetryShown = dialog => { modalCount++; dialog.Close(); };
        ExpectCanceled(pane.ComparePathsAsync()); Observe("failure-close");
        pane.ArchiveRetryShown = null;
        // 自己検証所有の入力を外部変更した状態で再読込みし、確定表示を保持する。
        File.WriteAllBytes(path, zipped[..^4]); var changedInputHash = Hash(File.ReadAllBytes(path)); var refreshFailed = false;
        try { pump(panel.RefreshAsync()); } catch (InvalidDataException) { refreshFailed = true; }
        Observe("refresh-failure");
        check("wrapper failed refresh leaves changed input untouched", refreshFailed && Hash(File.ReadAllBytes(path)) == changedInputHash, "harness restores its own original after observation");
        File.WriteAllBytes(path, zipped);
        pump(panel.RefreshAsync()); rows = panel.Rows; pump(panel.PreviewAsync(panel.Rows.Single())); preview = panel.PreviewText;
        check("wrapper retained panel remains refresh/preview operable", panel.Rows.Count == 1 && preview.Length > 0 && panel.LeftSourcePath == path, "independent lifetime after failed next comparison");
        // 採用直前の実 Stop は owner が有効な、完成した本物の candidate で行う。
        pane.LeftPath.Text = pane.RightPath.Text = twice;
        pane.ArchiveReadyForAdoption = candidate =>
        {
            check("wrapper late cancel receives complete candidate", candidate.Rows.Count == 1 && candidate.Rows[0].Left?.Sha256 == expectedSha, "real public service result");
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        ExpectCanceled(pane.ComparePathsAsync()); pane.ArchiveReadyForAdoption = null; Observe("late-cancel");
        // Refresh 完了直前の中止も確定 rows/preview を保持する。
        panel.RefreshReadyForAdoption = () => stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ExpectCanceled(panel.RefreshAsync()); panel.RefreshReadyForAdoption = null; Observe("refresh-cancel");
        // 完成した古い候補から実際に新比較を開始し、新結果の世代だけを採用する。
        Task? latest = null;
        pane.ArchiveReadyForAdoption = _ =>
        {
            pane.ArchiveReadyForAdoption = null;
            pane.LeftPath.Text = pane.RightPath.Text = path;
            latest = pane.ComparePathsAsync();
        };
        pump(pane.ComparePathsAsync()); if (latest is not null) pump(latest);
        var latestPanel = pane.GetVisualDescendants().OfType<ArchivePanel>().Single();
        check("wrapper stale candidate adopts latest source only", latestPanel.LeftSourcePath == path && latestPanel.Rows.Count == 1, "token and operation checked after completed seam");
        check("wrapper discarded candidate previous panel disposed", panel.IsDisposed && panel.LeftPassword.Text == "" && panel.RightPassword.Text == "", "successful SetSpecialView releases old panel lifetime");
        var closePane = window.AddSession(); closePane.SelectMode(0); closePane.LeftPath.Text = closePane.RightPath.Text = path;
        pump(closePane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
        var closePanel = closePane.GetVisualDescendants().OfType<ArchivePanel>().Single();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Items.OfType<TabItem>().Any(item => ReferenceEquals(item.Content, closePane)));
        var tab = tabs.Items.OfType<TabItem>().Single(item => ReferenceEquals(item.Content, closePane));
        ((StackPanel)tab.Header!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        check("wrapper real tab close disposes archive lifetime", closePanel.IsDisposed && !window.SessionPanes.Contains(closePane), "actual header close event and released panel");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(item => ReferenceEquals(item.Content, pane));
        using (var file = File.Create(Path.Combine(folder, "ui-proof.json")))
        using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("inputSha256", originalSha); writer.WriteString("leafSha256", expectedSha);
            writer.WriteNumber("minimumObservedWorkBytes", lower); writer.WriteNumber("modalCount", modalCount); writer.WriteStartArray("observations");
            foreach (var item in observed)
            {
                writer.WriteStartObject(); writer.WriteString("stage", item.Stage); writer.WriteBoolean("rowsPreserved", item.Rows);
                writer.WriteBoolean("previewPreserved", item.Preview); writer.WriteBoolean("sourcePreserved", item.Source);
                writer.WriteBoolean("samePanel", item.Active); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        check("wrapper GUI all failure/cancel observations preserve confirmed display", observed.All(o => o.Rows && o.Preview && o.Source && o.Active), "JSON records all observations before assertion");
        check("wrapper GUI input preserved and no product temporary files", Hash(File.ReadAllBytes(path)) == originalSha
            && !Directory.EnumerateFileSystemEntries(folder, ".diffbeacon*", SearchOption.AllDirectories).Any(), "all created input/output paths in run");
        screenshot("archive-wrappers-after.png");
        pane.SelectMode(8); pane.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Items.OfType<string>().Contains("tar-metadata")).SelectedItem = "archive";
        pane.LeftPath.Text = path; pane.RightPath.Text = basePath; pump(pane.ComparePathsAsync());
        check("wrapper real standard archive provider canonical content", pane.LeftEditor.Text == ArchiveComparison.CanonicalText(manifest)
            && pane.RightEditor.Text == pane.LeftEditor.Text && pane.LeftEditor.IsReadOnly && pane.RightEditor.IsReadOnly, "shared public Read; transformed results remain read-only");
        var saveRejected = false; try { pump(pane.SaveAsync(false)); } catch (InvalidOperationException) { saveRejected = true; }
        check("wrapper provider cannot text-save into compressed input", saveRejected && Hash(File.ReadAllBytes(path)) == originalSha, "actual GUI save service");
        pane.SelectMode(0);

        void Observe(string stage) => observed.Add((stage, ReferenceEquals(panel.Rows, rows), panel.PreviewText == preview,
            panel.LeftSourcePath == path && panel.RightSourcePath == path, pane.GetVisualDescendants().OfType<ArchivePanel>().SingleOrDefault() == panel));
        void ExpectCanceled(Task task) { var canceled = false; try { pump(task); } catch (OperationCanceledException) { canceled = true; } check("wrapper real UI cancellation", canceled, "expected cancellation, actual task completed"); }
        void Reject(string label, ManagedArchiveLimits limits, string input) => check("wrapper rejects " + label, !Accept(limits, input), "public ManagedArchive ReadManifest");
    }
    private static bool Accept(ManagedArchiveLimits limits, string path)
    {
        try { new ManagedArchive(limits).ReadManifest(path); return true; }
        catch (InvalidDataException) { return false; }
    }
    private static void VerifyContainerMetadata(string folder, ManagedArchive service, Action<string, bool, string> check)
    {
        var assembly = typeof(HeadlessArchiveWrapperChecks).Assembly;
        using var resource = assembly.GetManifestResourceStream("DiffBeacon.SelfTest.Wrappers.Metadata.json") ?? throw new InvalidOperationException("wrapper metadata golden がありません。");
        using var bytes = new MemoryStream(); resource.CopyTo(bytes);
        check("wrapper fixed metadata golden resource SHA", Hash(bytes.ToArray()) == "EF933B7AF4D5E67E0F8B3D97005286EAAF8365DEB74B52B0BA28A06550C0E65C", "nearest -text/!eol; official full7Zip listing source");
        using var golden = JsonDocument.Parse(bytes.ToArray());
        var observations = new List<(string Source, string Path, string ExpectedUtc, string? ActualUtc, string? Kind, bool Match)>();
        var countsMatch = true;
        foreach (var source in golden.RootElement.GetProperty("sources").EnumerateArray())
        {
            var name = source.GetProperty("name").GetString()!;
            using var original = assembly.GetManifestResourceStream("DiffBeacon.SelfTest.Wrappers." + name) ?? throw new InvalidOperationException("wrapper metadata 入力がありません。");
            using var memory = new MemoryStream(); original.CopyTo(memory); var content = memory.ToArray();
            check("wrapper fixed metadata original SHA " + name, Hash(content) == source.GetProperty("inputSha256").GetString(), "existing fixture bytes, license/source unchanged");
            var wrapped = Path.Combine(folder, name + ".gz"); File.WriteAllBytes(wrapped, GZip(content));
            var manifest = service.ReadManifest(wrapped);
            var expected = source.GetProperty("entries").EnumerateArray().ToArray();
            countsMatch &= expected.Length == manifest.Entries.Count;
            foreach (var item in expected)
            {
                var path = item.GetProperty("path").GetString()!;
                var entry = manifest.Entries.SingleOrDefault(e => e.Path == path);
                var utc = item.GetProperty("modifiedUtc").GetDateTime();
                var match = entry is not null && entry.IsDirectory == item.GetProperty("directory").GetBoolean()
                    && entry.Size == item.GetProperty("size").GetInt64() && entry.LastModifiedTime?.Kind == DateTimeKind.Local
                    && entry.LastModifiedTime.Value.ToUniversalTime().Ticks == utc.Ticks;
                observations.Add((name, path, utc.ToString("O"), entry?.LastModifiedTime?.ToUniversalTime().ToString("O"), entry?.LastModifiedTime?.Kind.ToString(), match));
            }
        }
        using (var file = File.Create(Path.Combine(folder, "container-metadata-proof.json")))
        using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("runtimeTimezone", TimeZoneInfo.Local.Id); writer.WriteNumber("runtimeOffsetMinutes", TimeZoneInfo.Local.BaseUtcOffset.TotalMinutes);
            writer.WriteBoolean("allNodeCountsMatch", countsMatch); writer.WriteStartArray("entries");
            foreach (var row in observations)
            {
                writer.WriteStartObject(); writer.WriteString("source", row.Source); writer.WriteString("path", row.Path);
                writer.WriteString("expectedUtc", row.ExpectedUtc); writer.WriteString("actualUtc", row.ActualUtc); writer.WriteString("actualKind", row.Kind); writer.WriteBoolean("match", row.Match); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        check("wrapper 7z/RAR all node types/sizes/exact UTC metadata", countsMatch && observations.All(o => o.Match), "fixed FILETIME 100ns; no rounding; Local API dates normalized by runner timezone");
    }
    private static byte[] Zip(byte[] bytes)
    {
        using var target = new MemoryStream();
        using (var archive = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("a", CompressionLevel.Optimal); entry.LastWriteTime = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);
            using var output = entry.Open(); output.Write(bytes);
        }
        return target.ToArray();
    }
    private static byte[] GZip(byte[] bytes)
    {
        // 標準20byte empty member。Python gzip/zlibの独立fixtureと同じ形式。
        // BCLは一度も非空Writeしない場合にmemberを出力しない。
        if (bytes.Length == 0) return Convert.FromHexString("1F8B080000000000000303000000000000000000");
        using var target = new MemoryStream();
        using (var output = new GZipStream(target, CompressionLevel.Optimal, leaveOpen: true)) output.Write(bytes);
        return target.ToArray();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
