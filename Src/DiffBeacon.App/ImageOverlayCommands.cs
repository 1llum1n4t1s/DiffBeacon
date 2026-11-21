using System.Globalization;
using System.Text.Json;

namespace DiffBeacon.App;

// 原画・設定・clock queueだけを読む。原本expected/maskを製品へ渡さない。
internal static class ImageOverlayCommands
{
    private const int MaximumInputBytes = 8 * 1024 * 1024;
    internal static async Task<int> RunAsync(string[] args)
    {
        long maximumWork = ImageComparisonEngine.MaximumDecodeWork;
        int? cancelAfterClock = null; var seen = new HashSet<string>(StringComparer.Ordinal);
        if (args.Length < 2 || args.Length % 2 != 0) throw new ArgumentException("--image-overlay-script INPUT_JSON [--max-work N] [--cancel-after-clock N] を指定してください。");
        for (var index = 2; index < args.Length; index += 2)
        {
            if (!seen.Add(args[index])) throw new ArgumentException("overlay診断オプションが重複しています。");
            if (args[index] == "--max-work" && long.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var work)
                && work <= ImageComparisonEngine.MaximumDecodeWork) maximumWork = work;
            else if (args[index] == "--cancel-after-clock" && int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is >= 1 and <= 16) cancelAfterClock = count;
            else throw new ArgumentException("overlay診断予算/取消clockオプションが不正です。");
        }
        var path = ImagePngStore.ValidateLocal(args[1]);
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var token = cancel.Token;
            await using var file = File.OpenRead(path);
            if (file.Length > MaximumInputBytes) throw new ArgumentException("overlay入力JSONは8 MiBまでです。");
            using var inputBytes = new MemoryStream(); var buffer = new byte[65_536]; int read;
            while ((read = await file.ReadAsync(buffer, token)) != 0)
            {
                if (inputBytes.Length + read > MaximumInputBytes) throw new ArgumentException("overlay入力JSONは8 MiBまでです。");
                inputBytes.Write(buffer.AsSpan(0, read));
            }
            inputBytes.Position = 0;
            using var input = await JsonDocument.ParseAsync(inputBytes, new() { MaxDepth = 16 }, token);
            Fields(input.RootElement, "cases"); var cases = Array(input.RootElement.GetProperty("cases"), 1, 1024);
            var budget = new ImageDisplayWorkBudget(maximumWork, token);
            using var output = new BoundedOutput(token);
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject(); writer.WriteStartArray("cases");
                foreach (var item in cases.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    Fields(item, "name", "mode", "overlayAlpha", "showDifferences", "blinkDifferences", "animationPeriod", "blinkPeriod",
                        "highlightAlpha", "selectedDiffIndex", "wipeMode", "wipePosition", "blockSize", "threshold", "images", "states");
                    var name = item.GetProperty("name").GetString() ?? throw new ArgumentException("overlay nameは文字列です。");
                    var wipeMode = item.GetProperty("wipeMode").GetInt32(); var position = item.GetProperty("wipePosition").GetInt32();
                    if (wipeMode is < 0 or > 2 || position < 0) throw new ArgumentException("wipe modeは0..2、positionは非負です。");
                    var wipe = wipeMode == 0 ? null : new ImageWipeSnapshot(wipeMode == 1 ? ImageDragMode.VerticalWipe : ImageDragMode.HorizontalWipe, position);
                    var settings = new ImageOverlayRenderer.Settings(item.GetProperty("mode").GetInt32(), item.GetProperty("overlayAlpha").GetDouble(),
                        item.GetProperty("showDifferences").GetBoolean(), item.GetProperty("blinkDifferences").GetBoolean(),
                        item.GetProperty("animationPeriod").GetInt32(), item.GetProperty("blinkPeriod").GetInt32(), item.GetProperty("blockSize").GetInt32(),
                        item.GetProperty("threshold").GetDouble(), item.GetProperty("highlightAlpha").GetDouble(), item.GetProperty("selectedDiffIndex").GetInt32(), wipe);
                    settings.Validate(); var images = Array(item.GetProperty("images"), 2, 3);
                    var frames = new ImageComparisonEngine.DecodedFrame[images.GetArrayLength()]; var offsets = new ImageOffset[frames.Length];
                    for (var pane = 0; pane < frames.Length; pane++)
                    {
                        token.ThrowIfCancellationRequested(); var image = images[pane]; Fields(image, "width", "height", "offsetX", "offsetY", "bgraBase64", "bgraBytes");
                        var width = image.GetProperty("width").GetInt32(); var height = image.GetProperty("height").GetInt32();
                        var pixels = (long)width * height;
                        if (width <= 0 || height <= 0 || pixels > ImageComparisonEngine.MaximumPixels) throw new ArgumentException("overlay原画寸法が上限を超えるか不正です。");
                        offsets[pane] = new(image.GetProperty("offsetX").GetInt32(), image.GetProperty("offsetY").GetInt32());
                        var hasBase64 = image.TryGetProperty("bgraBase64", out var encoded); var hasBytes = image.TryGetProperty("bgraBytes", out var bytes);
                        if (hasBase64 == hasBytes) throw new ArgumentException("BGRAはbase64またはbyte配列の一方です。");
                        byte[] decoded;
                        if (hasBase64)
                        {
                            var text = encoded.GetString() ?? throw new ArgumentException("BGRA base64は文字列です。");
                            if (text.Length != (pixels * 4 + 2) / 3 * 4) throw new ArgumentException("overlay BGRA base64長が寸法と一致しません。");
                            budget.Reserve(pixels); decoded = Convert.FromBase64String(text);
                        }
                        else
                        {
                            Array(bytes, checked((int)(pixels * 4)), checked((int)(pixels * 4))); budget.Reserve(pixels); decoded = new byte[bytes.GetArrayLength()];
                            for (var index = 0; index < decoded.Length; index++) { if ((index & 4095) == 0) token.ThrowIfCancellationRequested(); decoded[index] = bytes[index].GetByte(); }
                        }
                        if (decoded.LongLength != pixels * 4) throw new ArgumentException("overlay BGRA長が寸法と一致しません。");
                        frames[pane] = new(1, width, height, decoded);
                    }
                    var states = Array(item.GetProperty("states"), 1, 1024);
                    ImageOverlayRenderer.Preflight(frames, offsets, settings, states.GetArrayLength(), budget);
                    var prepared = ImageOverlayRenderer.Prepare(frames, offsets, settings, budget);
                    writer.WriteStartObject(); writer.WriteString("name", name); WriteFrames("rawBefore", frames); WriteClassification("classificationBefore", prepared.Regions, frames.Length);
                    WriteFrames("baseCanvas", prepared.Baseline); writer.WriteStartArray("states");
                    foreach (var state in states.EnumerateArray())
                    {
                        token.ThrowIfCancellationRequested(); Fields(state, "epochs"); var epochsJson = Array(state.GetProperty("epochs"), 0, 16);
                        var epochs = epochsJson.EnumerateArray().Select(value => value.GetInt64()).ToArray();
                        var clock = new ScriptedClock(epochs, cancelAfterClock, cancel); var rendered = ImageOverlayRenderer.Render(prepared, clock, budget); clock.Complete();
                        writer.WriteStartObject(); WriteFrames("processed", rendered.Frames); writer.WriteStartArray("clockReads");
                        foreach (var epoch in rendered.Sample.Epochs) writer.WriteNumberValue(epoch); writer.WriteEndArray();
                        writer.WriteStartArray("blendAlphas"); foreach (var alpha in rendered.Sample.BlendAlphas) writer.WriteNumberValue(alpha); writer.WriteEndArray();
                        writer.WriteBoolean("highlightVisible", rendered.Sample.HighlightVisible); writer.WriteNumber("position", rendered.Sample.Wipe?.Position ?? position);
                        writer.WriteNumber("workUsed", budget.Used); writer.WriteEndObject(); writer.Flush();
                    }
                    writer.WriteEndArray(); WriteClassification("classificationAfter", prepared.Regions, frames.Length); WriteFrames("rawAfter", frames); writer.WriteEndObject(); writer.Flush();
                }
                writer.WriteEndArray(); writer.WriteNumber("workUsed", budget.Used); writer.WriteNumber("maximumWork", budget.Maximum); writer.WriteEndObject(); writer.Flush();

                void WriteFrames(string name, IReadOnlyList<ImageComparisonEngine.DecodedFrame> frames)
                {
                    writer.WriteStartArray(name);
                    foreach (var frame in frames)
                    {
                        token.ThrowIfCancellationRequested(); writer.WriteStartObject(); writer.WriteNumber("width", frame.Width); writer.WriteNumber("height", frame.Height);
                        writer.WriteBase64String("bgraBase64", frame.Pixels); writer.WriteString("sha256", ImageComparisonEngine.PixelHash(frame.Pixels, token)); writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                void WriteClassification(string name, ImageRegionDiffer.Result regions, int count)
                {
                    writer.WriteStartObject(name); Grid("pair01", count == 2 ? null : regions.Pair01, count == 2 ? regions.RegionIds : null);
                    Grid("pair21", regions.Pair21, null); Grid("pair02", regions.Pair02, null); Grid("regionIds", null, regions.RegionIds);
                    writer.WriteStartArray("regions");
                    foreach (var region in regions.Regions)
                    {
                        token.ThrowIfCancellationRequested(); writer.WriteStartObject(); writer.WriteNumber("id", region.Id); writer.WriteNumber("op", region.Op);
                        writer.WriteNumber("left", region.Left); writer.WriteNumber("top", region.Top); writer.WriteNumber("right", region.Right); writer.WriteNumber("bottom", region.Bottom); writer.WriteEndObject();
                    }
                    writer.WriteEndArray(); writer.WriteEndObject();
                    void Grid(string key, byte[]? pair, int[]? ids)
                    {
                        writer.WriteStartArray(key);
                        if (pair is not null || ids is not null)
                        for (var y = 0; y < regions.Rows; y++)
                        {
                            token.ThrowIfCancellationRequested(); writer.WriteStartArray();
                            for (var x = 0; x < regions.Columns; x++)
                            { if ((x & 4095) == 0) token.ThrowIfCancellationRequested(); var index = y * regions.Columns + x; writer.WriteNumberValue(ids is null ? pair![index] == 0 ? 0 : -1 : ids[index]); }
                            writer.WriteEndArray();
                        }
                        writer.WriteEndArray();
                    }
                }
            }
            token.ThrowIfCancellationRequested(); output.Position = 0; await output.CopyToAsync(Console.OpenStandardOutput(), token); return 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
    private static void Fields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("overlay JSONはobjectです。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new ArgumentException("overlay JSONの未知/重複propertyです: " + property.Name);
    }
    private static JsonElement Array(JsonElement value, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < minimum || value.GetArrayLength() > maximum)
            throw new ArgumentException("overlay JSONの配列shape/件数が不正です。");
        return value;
    }
    private sealed class ScriptedClock(long[] epochs, int? cancelAfterClock, CancellationTokenSource cancel) : IImageDisplayClock
    {
        private readonly long[] _epochs = epochs.All(epoch => epoch >= 0) ? epochs.ToArray() : throw new ArgumentException("overlay epochは非負です。");
        private int _index;
        public long ReadEpochMilliseconds()
        {
            if (_index == _epochs.Length) throw new ArgumentException("overlay clock queueが不足しています。");
            var epoch = _epochs[_index++]; if (_index == cancelAfterClock) cancel.Cancel(); return epoch;
        }
        internal void Complete() { if (_index != _epochs.Length) throw new ArgumentException("overlay clock queueが余っています。"); }
    }
    private sealed class BoundedOutput(CancellationToken token) : MemoryStream
    {
        private void Check(int count)
        { token.ThrowIfCancellationRequested(); if (count > ProjectReport.MaximumBytes - Length) throw new InvalidOperationException("overlay診断JSONは32 MiBまでです。"); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
}
