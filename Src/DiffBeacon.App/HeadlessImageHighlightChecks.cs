using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessImageHighlightChecks
{
    // 原本が採取したBGRAを入力と期待値にし、実画面のbitmapを全バイト照合する。
    internal static void Run(ComparisonPane pane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        using var resource = typeof(HeadlessImageHighlightChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.ImageHighlight.json")
            ?? throw new InvalidOperationException("画像強調fixtureがありません。");
        using var bytes = new MemoryStream(); resource.CopyTo(bytes);
        check("image highlight original fixture SHA", Convert.ToHexString(SHA256.HashData(bytes.ToArray())) ==
            "853A08102656CD1F726E39647D98AF47CF4C8918C7F3EB051D078FEA87BC057A", "72 original cases");
        using var document = JsonDocument.Parse(bytes.ToArray());
        File.WriteAllBytes(Path.Combine(output, "image-highlight-golden.json"), bytes.ToArray());
        var observations = new List<Observation>();
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray().Where(item => item.GetProperty("highlightAlpha").GetDouble() == .7))
        {
            var name = item.GetProperty("name").GetString()!;
            var folder = Path.Combine(output, "image-highlight", name); Directory.CreateDirectory(folder);
            var inputs = item.GetProperty("images").EnumerateArray().ToArray();
            var paths = inputs.Select((_, index) => Path.Combine(folder, "pane" + index + ".png")).ToArray();
            for (var i = 0; i < inputs.Length; i++) WritePng(paths[i], inputs[i].GetProperty("width").GetInt32(), inputs[i].GetProperty("height").GetInt32(),
                Convert.FromBase64String(inputs[i].GetProperty("bgraBase64").GetString()!));
            var hashes = paths.Select(path => SHA256.HashData(File.ReadAllBytes(path))).ToArray();
            pane.DiscardChanges(); pane.SelectMode(4);
            pane.LeftPath.Text = paths[0]; pane.RightPath.Text = paths[^1]; pane.BasePath.Text = paths.Length == 3 ? paths[1] : "";
            pump(pane.ComparePathsAsync());
            var panel = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
            panel.GetVisualDescendants().OfType<NumericUpDown>().Single(control => control.Name == "ImageThreshold").Value = (decimal)item.GetProperty("threshold").GetDouble();
            pump(panel.CurrentFrameOperation);
            var expected = item.GetProperty("expected"); var selected = expected.GetProperty("currentDiffIndex").GetInt32();
            for (var i = 0; i <= selected; i++) pump(panel.NavigateRegionAsync(1));
            check(name + " GUI classification", panel.DifferenceCount == expected.GetProperty("differenceCount").GetInt32()
                && panel.ConflictCount == (inputs.Length == 3 ? expected.GetProperty("conflictCount").GetInt32() : 0)
                && panel.SelectedDiffIndex == selected, "regions/conflicts/selection");
            var names = inputs.Length == 3 ? new[] { "Left", "Middle", "Right" } : ["Left", "Right"];
            for (var i = 0; i < inputs.Length; i++)
            {
                var bitmap = (WriteableBitmap)panel.GetVisualDescendants().OfType<Image>().Single(image => image.Name == "ImagePane" + names[i]).Source!;
                var oracle = expected.GetProperty("processed")[i];
                var pixels = ReadPixels(bitmap);
                check(name + " GUI pane " + i, bitmap.PixelSize.Width == oracle.GetProperty("width").GetInt32()
                    && bitmap.PixelSize.Height == oracle.GetProperty("height").GetInt32()
                    && pixels.SequenceEqual(Convert.FromBase64String(oracle.GetProperty("bgraBase64").GetString()!)), Convert.ToHexString(SHA256.HashData(pixels)));
                File.WriteAllBytes(Path.Combine(folder, "display" + i + ".bgra"), pixels);
                bitmap.Save(Path.Combine(folder, "display" + i + ".png"), new PngBitmapEncoderOptions());
                observations.Add(new(name, i, bitmap.PixelSize.Width, bitmap.PixelSize.Height, Convert.ToHexString(SHA256.HashData(pixels)), selected));
            }
            panel.ReportAllFrames = false; var report = Path.Combine(folder, "selected.html"); pump(pane.SaveReportAsync(report));
            var html = File.ReadAllText(report);
            check(name + " GUI report retains highlight", expected.GetProperty("processed").EnumerateArray()
                .All(frame => html.Contains(frame.GetProperty("sha256").GetString()!, StringComparison.OrdinalIgnoreCase)), report);
            if (name is "all-conflict-alpha0.7-selected0" or "two-separated-alpha0.7-selectedLast") screenshot(name + ".png");
            if (name == "all-conflict-alpha0.7-selected-1")
            {
                pump(panel.NavigateRegionAsync(1, conflictsOnly: true));
                check(name + " GUI conflict navigation", panel.SelectedDiffIndex == 0 && panel.ConflictCount == 1, "first conflict");
            }
            var show = panel.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "ImageShowDifferences"); show.IsChecked = false; pump(panel.CurrentFrameOperation);
            for (var i = 0; i < inputs.Length; i++)
            {
                var bitmap = (WriteableBitmap)panel.GetVisualDescendants().OfType<Image>().Single(image => image.Name == "ImagePane" + names[i]).Source!;
                var raw = Convert.FromBase64String(inputs[i].GetProperty("bgraBase64").GetString()!);
                var w = inputs[i].GetProperty("width").GetInt32(); var h = inputs[i].GetProperty("height").GetInt32();
                var padded = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
                for (var y = 0; y < h; y++) raw.AsSpan(y * w * 4, w * 4).CopyTo(padded.AsSpan(y * bitmap.PixelSize.Width * 4));
                check(name + " GUI unhighlight " + i, ReadPixels(bitmap).SequenceEqual(padded), "raw canvas");
            }
            check(name + " GUI input preserved", paths.Select((path, index) => SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(hashes[index])).All(value => value), folder);
        }
        pane.DiscardChanges(); pane.LeftPath.Text = Path.Combine(output, "disposal.gif");
        pane.BasePath.Text = Path.Combine(output, "same-first-left.gif"); pane.RightPath.Text = Path.Combine(output, "disposal-2.png");
        pump(pane.ComparePathsAsync());
        var pages = pane.GetVisualDescendants().OfType<SpecializedViews.ImagePanel>().Single();
        pump(pages.SetFramesAsync(2, 1, 1));
        pages.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ImageNextBoth")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        pump(pages.CurrentFrameOperation);
        check("three image synchronized pages retain earlier short selection", pages.LeftFrame == 3 && pages.MiddleFrame == 1 && pages.RightFrame == 1, "4/2/1 pages, target 3");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var rejected = false;
            try { pump(pages.SetFramesAsync(4, 2, 1, cancelled.Token)); } catch (OperationCanceledException) { rejected = true; }
            check("three image cancelled pages retain display", rejected && pages.LeftFrame == 3 && pages.MiddleFrame == 1 && pages.RightFrame == 1, "cancel before decode");
        }
        var old = pages.SetFramesAsync(1, 1, 1); var latest = pages.SetFramesAsync(4, 2, 1); pump(latest);
        try { pump(old); } catch (OperationCanceledException) { }
        check("three image latest pages win", pages.LeftFrame == 4 && pages.MiddleFrame == 2 && pages.RightFrame == 1, "old completion discarded");
        screenshot("image-three-pages.png");
        using var evidence = File.Create(Path.Combine(output, "image-highlight-observations.json"));
        using var writer = new Utf8JsonWriter(evidence, new() { Indented = true });
        writer.WriteStartArray();
        foreach (var row in observations)
        {
            writer.WriteStartObject(); writer.WriteString("name", row.Name); writer.WriteNumber("pane", row.Pane);
            writer.WriteNumber("width", row.Width); writer.WriteNumber("height", row.Height); writer.WriteString("sha256", row.Sha256);
            writer.WriteNumber("selectedDiffIndex", row.Selected); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
    private sealed record Observation(string Name, int Pane, int Width, int Height, string Sha256, int Selected);
    private static byte[] ReadPixels(WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock(); var pixels = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
        for (var y = 0; y < bitmap.PixelSize.Height; y++) Marshal.Copy(IntPtr.Add(buffer.Address, y * buffer.RowBytes), pixels, y * bitmap.PixelSize.Width * 4, bitmap.PixelSize.Width * 4);
        return pixels;
    }
    private static void WritePng(string path, int width, int height, byte[] bgra)
    {
        using var file = File.Create(path); file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = 6; Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
        {
            var row = new byte[width * 4 + 1];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++) { var source = (y * width + x) * 4; var target = x * 4 + 1; row[target] = bgra[source + 2]; row[target + 1] = bgra[source + 1]; row[target + 2] = bgra[source]; row[target + 3] = bgra[source + 3]; }
                zlib.Write(row);
            }
        }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        void Chunk(string name, byte[] data)
        {
            var type = Encoding.ASCII.GetBytes(name); Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(word, data.Length); file.Write(word); file.Write(type); file.Write(data);
            uint crc = uint.MaxValue;
            foreach (var value in type.Concat(data)) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) == 0 ? 0u : 0xedb88320u); }
            BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); file.Write(word);
        }
    }
}
