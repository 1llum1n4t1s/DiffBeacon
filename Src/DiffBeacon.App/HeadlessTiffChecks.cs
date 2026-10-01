using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessTiffChecks
{
    internal static void Run(ComparisonPane pane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "tiff"); Directory.CreateDirectory(folder);
        byte[] Resource(string name)
        {
            using var stream = typeof(HeadlessTiffChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.Tiff." + name)
                ?? throw new InvalidDataException("TIFF fixtureがありません: " + name);
            using var data = new MemoryStream(); stream.CopyTo(data); return data.ToArray();
        }
        var manifest = Resource("expectations.json"); File.WriteAllBytes(Path.Combine(folder, "expectations.json"), manifest);
        using var json = JsonDocument.Parse(manifest); var assets = json.RootElement.GetProperty("assets");
        var observations = new List<(string Asset, int Number, string Side, string Sha)>();
        foreach (var asset in assets.EnumerateObject())
        {
            var bytes = Resource(asset.Name); var path = Path.Combine(folder, asset.Name); File.WriteAllBytes(path, bytes);
            check("TIFF UI fixture " + asset.Name, Convert.ToHexString(SHA256.HashData(bytes)) == asset.Value.GetProperty("fileSha256").GetString(), path);
            if (!asset.Value.GetProperty("valid").GetBoolean()) continue;
            pane.DiscardChanges(); pane.BasePath.Text = ""; pane.LeftPath.Text = path; pane.RightPath.Text = path; pane.SelectMode(0);
            pump(pane.ComparePathsAsync());
            var panel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
            panel.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "ImageShowDifferences").IsChecked = false;
            pump(panel.CurrentFrameOperation); var frames = asset.Value.GetProperty("frames");
            check("TIFF UI page count " + asset.Name, panel.LeftFrameCount == frames.GetArrayLength() && panel.RightFrameCount == frames.GetArrayLength(), "auto mode");
            for (var number = 1; number <= frames.GetArrayLength(); number++)
            {
                pump(panel.SetFramesAsync(number, number)); var expected = frames[number - 1];
                var raw = Convert.FromBase64String(expected.GetProperty("bgraBase64").GetString()!);
                check("TIFF UI page selection " + asset.Name + number, panel.LeftFrame == number && panel.RightFrame == number && panel.DifferentPixels == 0, "");
                foreach (var side in new[] { "Left", "Right" })
                {
                    var bitmap = (WriteableBitmap)panel.GetVisualDescendants().OfType<Image>().Single(image => image.Name == "ImagePane" + side).Source!;
                    using var locked = bitmap.Lock(); var actual = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
                    for (var row = 0; row < bitmap.PixelSize.Height; row++)
                        Marshal.Copy(IntPtr.Add(locked.Address, row * locked.RowBytes), actual, row * bitmap.PixelSize.Width * 4, bitmap.PixelSize.Width * 4);
                    check("TIFF UI raw page " + asset.Name + number + side, bitmap.PixelSize.Width == expected.GetProperty("width").GetInt32()
                        && bitmap.PixelSize.Height == expected.GetProperty("height").GetInt32() && actual.AsSpan().SequenceEqual(raw), Convert.ToHexString(SHA256.HashData(actual)));
                    File.WriteAllBytes(Path.Combine(folder, asset.Name + number + side + ".bgra"), actual);
                    observations.Add((asset.Name, number, side, Convert.ToHexString(SHA256.HashData(actual))));
                }
            }
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); var canceled = false;
            try { pump(panel.SetFramesAsync(1, 1, cancel.Token)); } catch (OperationCanceledException) { canceled = true; }
            check("TIFF UI cancel retains page " + asset.Name, canceled && panel.LeftFrame == frames.GetArrayLength() && panel.RightFrame == frames.GetArrayLength(), "");
            if (frames.GetArrayLength() > 1)
            {
                var rejected = false; try { pump(panel.CopyRegionAsync(0, 1, true)); } catch (InvalidOperationException) { rejected = true; }
                check("TIFF UI multi-page edit rejected " + asset.Name, rejected && !panel.HasUnsavedChanges, "");
            }
            if (asset.Name == "two-pages-le.tif") screenshot("tiff-variable-pages.png");
            check("TIFF UI input preserved " + asset.Name, File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes), path);
        }
        var left = Path.Combine(folder, "two-pages-le.tif"); var right = Path.Combine(folder, "same-first-right.tif");
        pane.DiscardChanges(); pane.LeftPath.Text = left; pane.RightPath.Text = right; pump(pane.ComparePathsAsync());
        var pages = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
        check("TIFF UI identical first page", pages.DifferentPixels == 0, "");
        pages.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "ImageNextBoth")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); pump(pages.CurrentFrameOperation);
        check("TIFF UI button reaches later difference", pages.LeftFrame == 2 && pages.RightFrame == 2 && pages.DifferentPixels == 1, "");
        var obsolete = pages.SetFramesAsync(1, 1); var latest = pages.SetFramesAsync(2, 2);
        try { pump(Task.WhenAll(obsolete, latest)); } catch (OperationCanceledException) { }
        check("TIFF UI latest page retained", latest.IsCompletedSuccessfully && pages.LeftFrame == 2 && pages.RightFrame == 2 && pages.DifferentPixels == 1, "");
        var report = Path.Combine(folder, "all.html"); var original = File.ReadAllBytes(left);
        try
        {
            File.WriteAllText(left, "changed after opening"); pump(pane.SaveReportAsync(report)); var html = File.ReadAllText(report);
            check("TIFF UI report uses immutable pages", html.Contains("data-left-frames=\"2\"", StringComparison.Ordinal)
                && html.Contains("data-left-frame=\"2\" data-right-frame=\"2\" data-different-pixels=\"1\"", StringComparison.Ordinal), report);
        }
        finally { File.WriteAllBytes(left, original); }
        screenshot("tiff-later-difference.png");
        pane.RightPath.Text = Path.Combine(folder, "single-page.tif"); pump(pane.ComparePathsAsync());
        var shortSide = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
        shortSide.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "ImageNextBoth")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); pump(shortSide.CurrentFrameOperation);
        check("TIFF UI short-side synchronized page", shortSide.LeftFrame == 2 && shortSide.RightFrame == 1, "");
        screenshot("tiff-short-side.png");
        using var evidence = File.Create(Path.Combine(folder, "observations.json")); using var writer = new Utf8JsonWriter(evidence, new() { Indented = true });
        writer.WriteStartArray();
        foreach (var row in observations)
        { writer.WriteStartObject(); writer.WriteString("asset", row.Asset); writer.WriteNumber("number", row.Number); writer.WriteString("side", row.Side); writer.WriteString("sha256", row.Sha); writer.WriteEndObject(); }
        writer.WriteEndArray();
    }
}
