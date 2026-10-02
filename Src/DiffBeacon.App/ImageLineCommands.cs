using System.Text.Json;

namespace DiffBeacon.App;

// 原本から固定した行比較を実アプリで照合する入口。期待scriptは読み取らない。
internal static class ImageLineCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is not (2 or 4) || args.Length == 4 && args[2] != "--max-work")
            throw new ArgumentException("--image-line-script INPUT_JSON [--max-work N] を指定してください。");
        long maximumWork = 400_000_000;
        if (args.Length == 4 && (!long.TryParse(args[3], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out maximumWork) || maximumWork < 0 || maximumWork > 400_000_000))
            throw new ArgumentException("画像行の累積作業予算は0..400,000,000です。");
        var path = ImagePngStore.ValidateLocal(args[1]);
        const int maximumInput = 16 * 1024 * 1024;
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var token = cancel.Token;
            await using var stream = File.OpenRead(path);
            using var content = new MemoryStream();
            var buffer = new byte[65_536];
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) != 0)
            {
                if (content.Length + read > maximumInput) throw new ArgumentException("画像行JSONは16 MiBまでです。");
                content.Write(buffer.AsSpan(0, read));
            }
            content.Position = 0;
            using var input = await JsonDocument.ParseAsync(content, new() { MaxDepth = 16 }, token);
            var cases = input.RootElement.GetProperty("cases");
            if (cases.ValueKind != JsonValueKind.Array || cases.GetArrayLength() > 20_000)
                throw new ArgumentException("画像行比較は20,000ケースまでです。");
            using var resultStream = new BoundedOutput();
            using (var writer = new Utf8JsonWriter(resultStream))
            {
                writer.WriteStartObject(); writer.WriteStartArray("cases");
                long totalWork = 0;
                foreach (var item in cases.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    var left = ReadRows(item.GetProperty("left"));
                    var right = ReadRows(item.GetProperty("right"));
                    var result = ImageLineDiffer.Compare(left.Width, left.Pixels, right.Width, right.Pixels,
                        item.GetProperty("threshold").GetDouble(), token, Math.Min(ImageLineDiffer.MaximumWork, maximumWork - totalWork));
                    totalWork = checked(totalWork + result.Work);
                    writer.WriteStartObject(); writer.WriteString("name", item.GetProperty("name").GetString());
                    writer.WriteString("script", result.Script); writer.WriteNumber("work", result.Work);
                    WriteHashes(writer, "leftHashes", result.LeftHashes);
                    WriteHashes(writer, "rightHashes", result.RightHashes);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
            }
            token.ThrowIfCancellationRequested();
            resultStream.Position = 0;
            await resultStream.CopyToAsync(Console.OpenStandardOutput(), token);
            return 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static (int Width, byte[] Pixels) ReadRows(JsonElement item)
    {
        var width = item.GetProperty("width").GetInt32();
        var height = item.GetProperty("height").GetInt32();
        var hex = item.GetProperty("bgraHex").GetString() ?? throw new ArgumentException("BGRA hexが必要です。");
        if (width <= 0 || height < 0 || (long)width * height > ImageComparisonEngine.MaximumPixels
            || hex.Length != (long)width * height * 8)
            throw new ArgumentException("画像行の寸法またはBGRA長が不正です。");
        return (width, Convert.FromHexString(hex));
    }

    private static void WriteHashes(Utf8JsonWriter writer, string name, uint[] hashes)
    {
        writer.WriteStartArray(name);
        foreach (var hash in hashes) writer.WriteNumberValue(hash);
        writer.WriteEndArray();
    }

    private sealed class BoundedOutput : MemoryStream
    {
        private void Check(int count)
        {
            if (count > 32 * 1024 * 1024 - Length) throw new InvalidOperationException("画像行の結果は32 MiBまでです。");
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
}
