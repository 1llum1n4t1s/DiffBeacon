using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessApngChecks
{
    internal static void Run(ComparisonPane pane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "apng"); Directory.CreateDirectory(folder);
        byte[] Resource(string name)
        {
            using var stream = typeof(HeadlessApngChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.Apng." + name)
                ?? throw new InvalidDataException("APNG fixtureがありません: " + name);
            using var content = new MemoryStream(); stream.CopyTo(content); return content.ToArray();
        }
        var manifest = Resource("expectations.json"); File.WriteAllBytes(Path.Combine(folder, "expectations.json"), manifest);
        using var json = JsonDocument.Parse(manifest);
        var assets = json.RootElement.GetProperty("assets"); var observations = new List<(string Asset, int Number, string Side, string Sha)>();
        foreach (var asset in assets.EnumerateObject())
        {
            var bytes = Resource(asset.Name); var path = Path.Combine(folder, asset.Name);
            File.WriteAllBytes(path, bytes);
            check("APNG UI fixture " + asset.Name, Convert.ToHexString(SHA256.HashData(bytes)) == asset.Value.GetProperty("fileSha256").GetString(), path);
            if (!asset.Value.GetProperty("valid").GetBoolean()) continue;
            if (asset.Name == "same-first-left.png") { path = Path.ChangeExtension(path, ".apng"); File.WriteAllBytes(path, bytes); }
            pane.DiscardChanges(); pane.BasePath.Text = ""; pane.LeftPath.Text = path; pane.RightPath.Text = path; pane.SelectMode(0);
            pump(pane.ComparePathsAsync());
            var panel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
            panel.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "ImageShowDifferences").IsChecked = false;
            pump(panel.CurrentFrameOperation);
            var frames = asset.Value.GetProperty("frames");
            check("APNG UI count " + asset.Name, panel.LeftFrameCount == frames.GetArrayLength() && panel.RightFrameCount == frames.GetArrayLength(), "auto mode");
            for (var number = 1; number <= frames.GetArrayLength(); number++)
            {
                pump(panel.SetFramesAsync(number, number));
                var expected = frames[number - 1]; var raw = Convert.FromBase64String(expected.GetProperty("bgraBase64").GetString()!);
                check("APNG UI selection " + asset.Name + number, panel.LeftFrame == number && panel.RightFrame == number && panel.DifferentPixels == 0, "");
                foreach (var side in new[] { "Left", "Right" })
                {
                    var bitmap = (WriteableBitmap)panel.GetVisualDescendants().OfType<Image>().Single(image => image.Name == "ImagePane" + side).Source!;
                    using var locked = bitmap.Lock(); var actual = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
                    for (var row = 0; row < bitmap.PixelSize.Height; row++)
                        Marshal.Copy(IntPtr.Add(locked.Address, row * locked.RowBytes), actual, row * bitmap.PixelSize.Width * 4, bitmap.PixelSize.Width * 4);
                    check("APNG UI bitmap " + asset.Name + number + side, bitmap.PixelSize.Width == expected.GetProperty("width").GetInt32()
                        && bitmap.PixelSize.Height == expected.GetProperty("height").GetInt32() && actual.AsSpan().SequenceEqual(raw), Convert.ToHexString(SHA256.HashData(actual)));
                    File.WriteAllBytes(Path.Combine(folder, asset.Name + number + side + ".bgra"), actual);
                    observations.Add((asset.Name, number, side, Convert.ToHexString(SHA256.HashData(actual))));
                }
            }
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            var cancellationObserved = false;
            try { pump(panel.SetFramesAsync(1, 1, canceled.Token)); } catch (OperationCanceledException) { cancellationObserved = true; }
            check("APNG UI cancel retains frame " + asset.Name, cancellationObserved && panel.LeftFrame == frames.GetArrayLength() && panel.RightFrame == frames.GetArrayLength(), "");
            if (frames.GetArrayLength() > 1)
            {
                var rejected = false; try { pump(panel.CopyRegionAsync(0, 1, true)); } catch (InvalidOperationException) { rejected = true; }
                check("APNG UI multi-frame edit rejected " + asset.Name, rejected && !panel.HasUnsavedChanges, "");
            }
            if (asset.Name == "offset-disposals.png") screenshot("apng-disposal.png");
            if (asset.Name == "source-over.png") screenshot("apng-over.png");
            check("APNG UI input preserved " + asset.Name, File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes), path);
        }
        pane.DiscardChanges(); pane.SelectMode(0);
        var left = Path.Combine(folder, "same-first-left.apng"); var right = Path.Combine(folder, "same-first-right.png");
        pane.LeftPath.Text = left; pane.RightPath.Text = right; pump(pane.ComparePathsAsync());
        var pages = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
        check("APNG UI identical first frame", pages.LeftFrameCount == 2 && pages.RightFrameCount == 2 && pages.DifferentPixels == 0, "");
        pages.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "ImageNextBoth")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        pump(pages.CurrentFrameOperation);
        check("APNG UI button reaches later difference", pages.LeftFrame == 2 && pages.RightFrame == 2 && pages.DifferentPixels == 4, "");
        var obsolete = pages.SetFramesAsync(1, 1); var latest = pages.SetFramesAsync(2, 2);
        try { pump(Task.WhenAll(obsolete, latest)); } catch (OperationCanceledException) { }
        check("APNG UI obsolete completion rejected", latest.IsCompletedSuccessfully && pages.LeftFrame == 2 && pages.RightFrame == 2 && pages.DifferentPixels == 4, "");
        var report = Path.Combine(folder, "all.html"); var original = File.ReadAllBytes(left);
        try
        {
            File.WriteAllText(left, "changed after opening"); pump(pane.SaveReportAsync(report));
            var html = File.ReadAllText(report);
            check("APNG UI report uses immutable all-frame snapshot", html.Contains("data-left-frames=\"2\"", StringComparison.Ordinal)
                && html.Contains("data-left-frame=\"2\" data-right-frame=\"2\" data-different-pixels=\"4\"", StringComparison.Ordinal), report);
        }
        finally { File.WriteAllBytes(left, original); }
        screenshot("apng-later-difference.png");
        using var evidence = File.Create(Path.Combine(folder, "observations.json"));
        using var writer = new Utf8JsonWriter(evidence, new() { Indented = true }); writer.WriteStartArray();
        foreach (var row in observations)
        {
            writer.WriteStartObject(); writer.WriteString("asset", row.Asset); writer.WriteNumber("number", row.Number);
            writer.WriteString("side", row.Side); writer.WriteString("sha256", row.Sha); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
