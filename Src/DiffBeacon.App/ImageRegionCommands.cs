using System.Globalization;
using System.Text.Json;

namespace DiffBeacon.App;

// 原本との照合用。通常GUI/HTMLへの統合前に、復号を含む実行経路を検証する。
internal static class ImageRegionCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var optionStart = 1;
        while (optionStart < args.Length && !args[optionStart].StartsWith("--", StringComparison.Ordinal)) optionStart++;
        var count = optionStart - 1;
        if (count is not (2 or 3)) throw new ArgumentException("--image-regions LEFT [MIDDLE] RIGHT を指定してください。");
        var blockSize = 8; double threshold = 0; double highlightAlpha = .7;
        var selectedDiffIndex = -1; var render = false;
        var insertionDeletionMode = 0;
        var offsets = new ImageOffset[count];
        int? leftFrame = null, middleFrame = null, rightFrame = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = optionStart; index < args.Length; index += 2)
        {
            var option = args[index];
            if (option is not ("--block-size" or "--threshold" or "--left-frame" or "--middle-frame" or "--right-frame"
                or "--highlight-alpha" or "--selected-region" or "--insertion-deletion-mode"
                or "--left-offset" or "--middle-offset" or "--right-offset")
                || !seen.Add(option) || index + 1 >= args.Length)
                throw new ArgumentException($"未知・重複または値が不足した画像領域オプションです: {option}");
            if (option == "--insertion-deletion-mode")
            { insertionDeletionMode = ImageComparisonEngine.ParseInsertionDeletionMode(args[index + 1]); continue; }
            if (option.EndsWith("-offset", StringComparison.Ordinal))
            {
                if (option == "--middle-offset" && count != 3) throw new ArgumentException("中央の画像位置は三者比較だけです。");
                offsets[option == "--left-offset" ? 0 : option == "--middle-offset" ? 1 : count - 1] = ImageOffset.Parse(args[index + 1]);
                continue;
            }
            if (option == "--threshold")
            {
                if (!double.TryParse(args[index + 1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out threshold)
                    || !double.IsFinite(threshold) || threshold < 0)
                    throw new ArgumentException("差分閾値は有限の非負数です（小数点は .）。");
                continue;
            }
            if (option == "--highlight-alpha")
            {
                if (!double.TryParse(args[index + 1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out highlightAlpha)
                    || !double.IsFinite(highlightAlpha) || highlightAlpha is < 0 or > 1)
                    throw new ArgumentException("強調alphaは有限の0..1です（小数点は .）。");
                render = true;
                continue;
            }
            if (option == "--selected-region")
            {
                if (!int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var regionNumber))
                    throw new ArgumentException("選択領域は0（未選択）または1以上の整数です。");
                selectedDiffIndex = regionNumber - 1; render = true;
                continue;
            }
            if (!int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
                throw new ArgumentException($"{option} は1以上の整数です。");
            if (option == "--block-size")
            {
                if (value > 256) throw new ArgumentException("ブロックサイズは1..256です。");
                blockSize = value;
            }
            else if (option == "--left-frame") leftFrame = value;
            else if (option == "--middle-frame") middleFrame = value;
            else rightFrame = value;
        }
        var selected = leftFrame.HasValue || middleFrame.HasValue || rightFrame.HasValue;
        if (count == 2 && middleFrame.HasValue || selected && (!leftFrame.HasValue || !rightFrame.HasValue || count == 3 && !middleFrame.HasValue))
            throw new ArgumentException("選択するフレーム番号は各入力すべてに指定してください。中央は三者比較だけです。");
        var numbers = count == 2 ? new[] { leftFrame ?? 1, rightFrame ?? 1 } : [leftFrame ?? 1, middleFrame ?? 1, rightFrame ?? 1];
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var token = cancel.Token;
            var snapshots = new ImageComparisonEngine.Snapshot[count];
            var width = 0; var height = 0;
            for (var index = 0; index < count; index++)
            {
                var image = snapshots[index] = await ImageComparisonEngine.OpenAsync(args[index + 1], token);
                if (numbers[index] > image.FrameCount) throw new ArgumentException($"画像フレームは1..{image.FrameCount}です。");
                var size = image.GetDimensions(numbers[index]);
                width = Math.Max(width, checked(size.Width + offsets[index].X)); height = Math.Max(height, checked(size.Height + offsets[index].Y));
            }
            ImageComparisonEngine.ValidateSelection(snapshots, numbers, offsets: offsets);
            var columns = (width + blockSize - 1) / blockSize;
            var rows = (height + blockSize - 1) / blockSize;
            // 診断JSONの全grid出力だけを制限。通常比較へこの出力制約を持ち込まない。
            if ((long)columns * rows > 262_144) throw new InvalidOperationException("開発用の領域JSONは262,144ブロックまでです。");
            var comparison = await Task.Run(() =>
            {
                var frames = new ImageComparisonEngine.DecodedFrame[count];
                for (var index = 0; index < count; index++) frames[index] = snapshots[index].Decode(numbers[index], token);
                ImageLineAlignment.Result? alignment = insertionDeletionMode == 0 ? null
                    : ImageLineAlignment.Align(frames, insertionDeletionMode == 2, threshold, token);
                IReadOnlyList<ImageComparisonEngine.DecodedFrame> views = alignment?.Frames ?? frames;
                var canvas = ImageOffset.Canvas(views, offsets);
                if ((long)((canvas.Width + blockSize - 1) / blockSize) * ((canvas.Height + blockSize - 1) / blockSize) > 262_144)
                    throw new InvalidOperationException("開発用の領域JSONは262,144ブロックまでです。");
                var regionResult = ImageRegionDiffer.Compare(views, blockSize, threshold, token, offsets);
                var rendered = render ? ImageRegionRenderer.Render(views, regionResult, blockSize, highlightAlpha, selectedDiffIndex, token,
                    alignment: alignment) : null;
                return (Result: regionResult, Rendered: rendered);
            }, token);
            var result = comparison.Result;
            using var content = new MemoryStream();
            using (var writer = new Utf8JsonWriter(content))
            {
                writer.WriteStartObject();
                writer.WriteNumber("width", result.Width); writer.WriteNumber("height", result.Height);
                writer.WriteNumber("columns", result.Columns); writer.WriteNumber("rows", result.Rows);
                writer.WriteNumber("blockSize", blockSize); writer.WriteNumber("threshold", threshold);
                writer.WriteStartArray("frameNumbers"); foreach (var number in numbers) writer.WriteNumberValue(number); writer.WriteEndArray();
                WriteGrid("pair01", result.Pair01, null); WriteGrid("pair21", result.Pair21, null);
                WriteGrid("pair02", result.Pair02, null); WriteGrid("regionIds", null, result.RegionIds);
                writer.WriteNumber("differenceCount", result.Regions.Count);
                writer.WriteStartArray("regions");
                foreach (var region in result.Regions)
                {
                    token.ThrowIfCancellationRequested(); writer.WriteStartObject();
                    writer.WriteNumber("id", region.Id); writer.WriteNumber("op", region.Op);
                    writer.WriteNumber("left", region.Left); writer.WriteNumber("top", region.Top);
                    writer.WriteNumber("right", region.Right); writer.WriteNumber("bottom", region.Bottom); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteNumber("conflictCount", result.ConflictCount);
                if (comparison.Rendered is not null)
                {
                    writer.WriteStartArray("renderedFrames");
                    foreach (var frame in comparison.Rendered)
                    {
                        token.ThrowIfCancellationRequested(); writer.WriteStartObject();
                        writer.WriteNumber("frame", frame.Number); writer.WriteNumber("width", frame.Width); writer.WriteNumber("height", frame.Height);
                        writer.WriteString("pixelSha256", ImageComparisonEngine.PixelHash(frame.Pixels, token)); writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject(); writer.Flush();

                void WriteGrid(string name, byte[]? pair, int[]? ids)
                {
                    if (pair is null && ids is null) { writer.WriteNull(name); return; }
                    writer.WriteStartArray(name);
                    for (var y = 0; y < result.Rows; y++)
                    {
                        token.ThrowIfCancellationRequested(); writer.WriteStartArray();
                        for (var x = 0; x < result.Columns; x++)
                        {
                            if ((x & 4095) == 0) token.ThrowIfCancellationRequested();
                            var index = y * result.Columns + x;
                            writer.WriteNumberValue(ids is not null ? ids[index] : pair![index] == 0 ? 0 : -1);
                        }
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                }
            }
            token.ThrowIfCancellationRequested(); content.Position = 0;
            await content.CopyToAsync(Console.OpenStandardOutput(), token);
            return result.Regions.Count == 0 ? 0 : 1;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
