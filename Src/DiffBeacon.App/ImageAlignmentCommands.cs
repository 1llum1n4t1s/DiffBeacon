using System.Globalization;

namespace DiffBeacon.App;

// 小さい原本入力でghost画素・領域・逆座標を照合する。保存や履歴は変更しない。
internal static class ImageAlignmentCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var paths = new List<string>(); var option = 1;
        while (option < args.Length && !args[option].StartsWith("--", StringComparison.Ordinal))
            paths.Add(ImagePngStore.ValidateLocal(args[option++]));
        if (paths.Count is not (2 or 3)) throw new ArgumentException("画像整列は二者または三者です。");
        var horizontal = false; var threshold = 0.0; var blockSize = 1;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (option < args.Length)
        {
            var name = args[option++];
            if (!seen.Add(name)) throw new ArgumentException("画像整列オプションが重複しています。");
            if (name == "--horizontal") { horizontal = true; continue; }
            if (option >= args.Length) throw new ArgumentException("画像整列オプションの値が必要です。");
            var value = args[option++];
            if (name == "--threshold" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold)) continue;
            if (name == "--block-size" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out blockSize)) continue;
            throw new ArgumentException("画像整列オプションまたは値が不正です。");
        }
        ImageComparisonEngine.ValidateThreshold(threshold);
        if (blockSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(blockSize));
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var token = cancel.Token;
            var frames = new List<ImageComparisonEngine.DecodedFrame>();
            foreach (var path in paths)
            {
                var source = await ImageComparisonEngine.OpenAsync(path, token);
                if (source.FrameCount != 1 || source.Pixels > 4096)
                    throw new ArgumentException("整列診断は単一フレーム・4096画素までです。");
                frames.Add(source.Decode(1, token));
            }
            var aligned = ImageLineAlignment.Align(frames, horizontal, threshold, token);
            var width = aligned.Frames.Max(frame => frame.Width);
            var height = aligned.Frames.Max(frame => frame.Height);
            if ((long)width * height > 4096) throw new ArgumentException("整列診断canvasは4096画素までです。");
            var regions = ImageRegionDiffer.Compare(aligned.Frames, blockSize, threshold, token);
            CommandLine.WriteJson(writer =>
            {
                writer.WriteNumber("mode", horizontal ? 2 : 1);
                writer.WriteNumber("work", aligned.Work);
                writer.WriteNumber("differenceCount", regions.Regions.Count);
                writer.WriteNumber("conflictCount", regions.ConflictCount);
                writer.WriteStartArray("panes");
                for (var pane = 0; pane < aligned.Frames.Count; pane++)
                {
                    var frame = aligned.Frames[pane];
                    var canvas = new byte[checked(width * height * 4)];
                    for (var y = 0; y < frame.Height; y++)
                    {
                        token.ThrowIfCancellationRequested();
                        frame.Pixels.AsSpan(y * frame.Width * 4, frame.Width * 4).CopyTo(canvas.AsSpan(y * width * 4));
                    }
                    writer.WriteStartObject(); writer.WriteNumber("canvasWidth", width); writer.WriteNumber("canvasHeight", height);
                    writer.WriteString("bgraHex", Convert.ToHexString(canvas));
                    writer.WriteStartArray("mapping");
                    for (var y = -1; y <= height; y++)
                    for (var x = -1; x <= width; x++)
                    {
                        token.ThrowIfCancellationRequested();
                        var real = aligned.ConvertToRealPosition(pane, x, y);
                        writer.WriteStartArray(); writer.WriteNumberValue(x); writer.WriteNumberValue(y);
                        writer.WriteBooleanValue(real.Inside); writer.WriteNumberValue(real.X); writer.WriteNumberValue(real.Y); writer.WriteEndArray();
                    }
                    writer.WriteEndArray(); writer.WriteEndObject();
                }
                writer.WriteEndArray();
                token.ThrowIfCancellationRequested();
            });
            return 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
