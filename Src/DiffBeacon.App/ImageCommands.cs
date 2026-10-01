using System.Globalization;
using System.Text.Json;

namespace DiffBeacon.App;

internal static class ImageCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("--image LEFT RIGHT [--left-frame N --right-frame N] [--threshold N] を指定してください。");
        int? leftFrame = null, rightFrame = null;
        var threshold = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 3; index < args.Length; index += 2)
        {
            var option = args[index];
            if (option is not ("--left-frame" or "--right-frame" or "--threshold") || !seen.Add(option))
                throw new ArgumentException($"未知または重複する画像オプションです: {option}");
            if (index + 1 >= args.Length || !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                throw new ArgumentException($"{option} に整数を指定してください。");
            if (option == "--threshold")
            {
                if (value is < 0 or > 255) throw new ArgumentException("差分閾値は0..255です。");
                threshold = value;
            }
            else
            {
                if (value < 1) throw new ArgumentException("画像フレーム番号は1始まりです。");
                if (option == "--left-frame") leftFrame = value;
                else rightFrame = value;
            }
        }
        if (leftFrame.HasValue != rightFrame.HasValue) throw new ArgumentException("--left-frame と --right-frame は両方指定してください。");
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var token = cancel.Token;
            var left = await ImageComparisonEngine.OpenAsync(args[1], token);
            var right = await ImageComparisonEngine.OpenAsync(args[2], token);
            var result = await Task.Run(() => ImageComparisonEngine.Compare(left, right, leftFrame, rightFrame, threshold, token), token);
            // 完了した結果だけ標準出力へ渡し、失敗・取消時に成功JSONを残さない。
            using var content = new MemoryStream();
            using (var writer = new Utf8JsonWriter(content))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("different", result.Different);
                writer.WriteNumber("leftFrames", result.LeftFrames); writer.WriteNumber("rightFrames", result.RightFrames);
                writer.WriteNumber("threshold", result.Threshold); writer.WriteString("mode", result.Mode);
                writer.WriteStartArray("frames");
                foreach (var frame in result.Frames)
                {
                    token.ThrowIfCancellationRequested();
                    writer.WriteStartObject();
                    WriteNullable("leftFrame", frame.LeftFrame); WriteNullable("rightFrame", frame.RightFrame);
                    WriteNullable("leftWidth", frame.LeftWidth); WriteNullable("leftHeight", frame.LeftHeight);
                    WriteNullable("rightWidth", frame.RightWidth); WriteNullable("rightHeight", frame.RightHeight);
                    writer.WriteNumber("differentPixels", frame.DifferentPixels); writer.WriteNumber("totalPixels", frame.TotalPixels);
                    writer.WriteString("leftPixelSha256", frame.LeftPixelSha256); writer.WriteString("rightPixelSha256", frame.RightPixelSha256);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();

                void WriteNullable(string name, int? value)
                { if (value.HasValue) writer.WriteNumber(name, value.Value); else writer.WriteNull(name); }
            }
            token.ThrowIfCancellationRequested();
            content.Position = 0;
            await content.CopyToAsync(Console.OpenStandardOutput(), token);
            return result.Different ? 1 : 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
