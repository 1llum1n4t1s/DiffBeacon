using System.Text.Json;

namespace DiffBeacon.App;

// raw共通canvasと操作列だけを読む。golden期待値は実アプリへ渡さない。
internal static class ImageWipeCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("--image-wipe-script INPUT_JSON を指定してください。");
        var path = ImagePngStore.ValidateLocal(args[1]);
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
        var token = cancel.Token;
        await using var file = File.OpenRead(path);
        if (file.Length > 16 * 1024 * 1024) throw new ArgumentException("ワイプ入力JSONは16 MiBまでです。");
        using var content = new MemoryStream(); var buffer = new byte[65_536]; int read;
        while ((read = await file.ReadAsync(buffer, token)) != 0)
        { if (content.Length + read > 16 * 1024 * 1024) throw new ArgumentException("ワイプ入力JSONは16 MiBまでです。"); content.Write(buffer.AsSpan(0, read)); }
        content.Position = 0;
        using var input = await JsonDocument.ParseAsync(content, new() { MaxDepth = 16 }, token);
        using var output = new BoundedOutput();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteStartArray("cases");
            var cases = input.RootElement.GetProperty("cases");
            if (cases.GetArrayLength() > 1024) throw new ArgumentException("ワイプ診断は1024ケースまでです。");
            foreach (var item in cases.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var mode = item.GetProperty("mode").GetString();
                ImageWipeSnapshot.Parse(mode, "0")!.Validate();
                var images = item.GetProperty("images").EnumerateArray().Select(image => new ImageComparisonEngine.DecodedFrame(1,
                    image.GetProperty("width").GetInt32(), image.GetProperty("height").GetInt32(),
                    image.GetProperty("bgraBase64").GetBytesFromBase64())).ToArray();
                ImageWipeRenderer.ValidateBaseline(images);
                var actions = item.GetProperty("positions");
                if (actions.GetArrayLength() is < 1 or > 1024) throw new ArgumentException("ワイプ診断は1～1024操作です。");
                writer.WriteStartObject(); writer.WriteString("name", item.GetProperty("name").GetString()); writer.WriteStartArray("states");
                foreach (var position in actions.EnumerateArray())
                {
                    var wipe = ImageWipeSnapshot.Parse(mode, Math.Max(0, position.GetInt32()).ToString(System.Globalization.CultureInfo.InvariantCulture))!;
                    var rendered = ImageWipeRenderer.Render(images, wipe, token);
                    writer.WriteStartObject(); writer.WriteNumber("position", wipe.Clamp(images[0].Width, images[0].Height).Position);
                    writer.WriteStartArray("processed");
                    foreach (var frame in rendered)
                    {
                        writer.WriteStartObject(); writer.WriteNumber("width", frame.Width); writer.WriteNumber("height", frame.Height);
                        writer.WriteBase64String("bgraBase64", frame.Pixels); writer.WriteString("sha256", ImageComparisonEngine.PixelHash(frame.Pixels, token)); writer.WriteEndObject();
                    }
                    writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
                    if (output.Length > ProjectReport.MaximumBytes) throw new ArgumentException("ワイプ診断JSONが32 MiBを超えます。");
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        token.ThrowIfCancellationRequested(); output.Position = 0; await output.CopyToAsync(Console.OpenStandardOutput(), token); return 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
    private sealed class BoundedOutput : MemoryStream
    {
        private void Check(int count)
        { if (count > ProjectReport.MaximumBytes - Length) throw new InvalidOperationException("ワイプ診断JSONは32 MiBまでです。"); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
}
