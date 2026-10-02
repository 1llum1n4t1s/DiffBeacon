using System.Globalization;
using System.Text.Json;

namespace DiffBeacon.App;

internal static class ImageCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var optionStart = 1;
        while (optionStart < args.Length && !args[optionStart].StartsWith("--", StringComparison.Ordinal)) optionStart++;
        var inputCount = optionStart - 1;
        if (inputCount is not (2 or 3)) throw new ArgumentException("--image LEFT [MIDDLE] RIGHT を指定してください。");
        int? leftFrame = null, middleFrame = null, rightFrame = null;
        double threshold = 0;
        var blockSize = 8;
        var insertionDeletionMode = 0;
        var orientations = Enumerable.Range(0, inputCount).Select(_ => new ImageOrientation()).ToArray();
        var offsets = new ImageOffset[inputCount];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = optionStart; index < args.Length; index += 2)
        {
            var option = args[index];
            if (option is not ("--left-frame" or "--middle-frame" or "--right-frame" or "--threshold"
                or "--left-orientation" or "--middle-orientation" or "--right-orientation" or "--block-size"
                or "--left-offset" or "--middle-offset" or "--right-offset" or "--insertion-deletion-mode") || !seen.Add(option))
                throw new ArgumentException($"未知または重複する画像オプションです: {option}");
            if (index + 1 >= args.Length) throw new ArgumentException($"{option} に値を指定してください。");
            if (option == "--insertion-deletion-mode")
                insertionDeletionMode = ImageComparisonEngine.ParseInsertionDeletionMode(args[index + 1]);
            else if (option.EndsWith("-offset", StringComparison.Ordinal))
            {
                if (option == "--middle-offset" && inputCount != 3) throw new ArgumentException("中央の画像位置は三者比較だけです。");
                offsets[option == "--left-offset" ? 0 : option == "--middle-offset" ? 1 : inputCount - 1] = ImageOffset.Parse(args[index + 1]);
            }
            else if (option == "--block-size")
            {
                if (!int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out blockSize) || blockSize is < 1 or > 256)
                    throw new ArgumentException("画像差分のブロックサイズは1～256です。");
            }
            else if (option.EndsWith("-orientation", StringComparison.Ordinal))
            {
                if (option == "--middle-orientation" && inputCount != 3) throw new ArgumentException("中央の画像変換は三者比較だけです。");
                var parts = args[index + 1].Split(',');
                if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rotation)
                    || parts[1] is not ("0" or "1") || parts[2] is not ("0" or "1"))
                    throw new ArgumentException("画像変換は回転度,水平反転0/1,垂直反転0/1です（例:90,1,0）。");
                var value = new ImageOrientation { Rotation = rotation, FlipHorizontal = parts[1] == "1", FlipVertical = parts[2] == "1" };
                ImageOrientation.Validate(value);
                orientations[option == "--left-orientation" ? 0 : option == "--middle-orientation" ? 1 : inputCount - 1] = value;
            }
            else if (option == "--threshold")
            {
                if (!double.TryParse(args[index + 1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out threshold))
                    throw new ArgumentException("差分閾値は有限の非負数です（小数点は .）。");
                ImageComparisonEngine.ValidateThreshold(threshold);
            }
            else
            {
                if (!int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
                    throw new ArgumentException("画像フレーム番号は1始まりです。");
                if (option == "--left-frame") leftFrame = value;
                else if (option == "--middle-frame") middleFrame = value;
                else rightFrame = value;
            }
        }
        var selected = leftFrame.HasValue || middleFrame.HasValue || rightFrame.HasValue;
        if (inputCount == 2 && middleFrame.HasValue || selected && (!leftFrame.HasValue || !rightFrame.HasValue || inputCount == 3 && !middleFrame.HasValue))
            throw new ArgumentException("選択フレーム番号は全入力分を指定してください。中央は三者比較だけです。");
        int[]? numbers = selected ? inputCount == 3 ? [leftFrame!.Value, middleFrame!.Value, rightFrame!.Value] : [leftFrame!.Value, rightFrame!.Value] : null;
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var token = cancel.Token;
            var images = new ImageComparisonEngine.Snapshot[inputCount];
            for (var i = 0; i < inputCount; i++) images[i] = await ImageComparisonEngine.OpenAsync(args[i + 1], token);
            var result = await Task.Run(() => ImageComparisonEngine.Compare(images, numbers, threshold, token, orientations, blockSize, offsets,
                insertionDeletionMode), token);
            // 完了した結果だけ標準出力へ渡し、失敗・取消時に成功JSONを残さない。
            using var content = new MemoryStream();
            using (var writer = new Utf8JsonWriter(content))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("different", result.Different);
                writer.WriteNumber("leftFrames", result.LeftFrames); writer.WriteNumber("rightFrames", result.RightFrames);
                if (result.MiddleFrames.HasValue) writer.WriteNumber("middleFrames", result.MiddleFrames.Value);
                writer.WriteNumber("threshold", result.Threshold); writer.WriteString("mode", result.Mode);
                if (insertionDeletionMode != 0) writer.WriteNumber("insertionDeletionMode", insertionDeletionMode);
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
                    if (result.MiddleFrames.HasValue)
                    {
                        WriteNullable("middleFrame", frame.MiddleFrame); WriteNullable("middleWidth", frame.MiddleWidth); WriteNullable("middleHeight", frame.MiddleHeight);
                        writer.WriteString("middlePixelSha256", frame.MiddlePixelSha256);
                    }
                    writer.WriteNumber("differenceCount", frame.Regions.Count); writer.WriteNumber("conflictCount", frame.ConflictCount);
                    writer.WriteStartArray("regions");
                    foreach (var region in frame.Regions)
                    {
                        token.ThrowIfCancellationRequested(); writer.WriteStartObject(); writer.WriteNumber("id", region.Id); writer.WriteNumber("op", region.Op);
                        writer.WriteNumber("left", region.Left); writer.WriteNumber("top", region.Top); writer.WriteNumber("right", region.Right); writer.WriteNumber("bottom", region.Bottom); writer.WriteEndObject();
                    }
                    writer.WriteEndArray(); writer.WriteStartArray("highlightPixelSha256");
                    foreach (var hash in frame.HighlightPixelSha256) writer.WriteStringValue(hash);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();

                void WriteNullable(string name, int? value)
                { if (value.HasValue) writer.WriteNumber(name, value.Value); else writer.WriteNull(name); }
            }
            if (content.Length > ProjectReport.MaximumBytes) throw new InvalidDataException("画像比較のJSONは32 MiBまでです。");
            token.ThrowIfCancellationRequested();
            content.Position = 0;
            await content.CopyToAsync(Console.OpenStandardOutput(), token);
            return result.Different ? 1 : 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
