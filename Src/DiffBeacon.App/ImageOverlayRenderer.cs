namespace DiffBeacon.App;

// WinIMerge v1.0.54 da639cdfaeca87aaad0eaceec509afa11ad61421、GPL v2以降。
// ImgDiffBuffer.hpp RefreshImages1166–1218 / XorImages2 / AlphaBlendImages2 2078–2124。
// 原本・license: tests/Fixtures/ImageRegions、無改変採取: ImageOverlays / ImageTemporalOverlays。
internal static class ImageOverlayRenderer
{
    internal sealed record Settings(int Mode, double Alpha, bool ShowDifferences, bool BlinkDifferences,
        int AnimationPeriod = 1000, int BlinkPeriod = 800, int BlockSize = 8, double Threshold = 0,
        double HighlightAlpha = .7, int SelectedDiffIndex = -1, ImageWipeSnapshot? Wipe = null)
    {
        internal void Validate()
        {
            if (Mode is < 0 or > 3) throw new ArgumentException("overlay modeは0..3です。");
            if (!double.IsFinite(Alpha) || Alpha is < 0 or > 1) throw new ArgumentException("overlay alphaは有限の0..1です。");
            if (AnimationPeriod is < 200 or > 8000 || BlinkPeriod is < 200 or > 8000) throw new ArgumentException("overlay/blink periodは200..8000です。");
            if (BlockSize is < 1 or > 256 || !double.IsFinite(Threshold) || Threshold < 0 || SelectedDiffIndex < -1)
                throw new ArgumentException("overlay比較設定が不正です。");
            ImageComparisonEngine.ValidateHighlightAlpha(HighlightAlpha); Wipe?.Validate();
        }
    }
    internal sealed record Prepared(IReadOnlyList<ImageComparisonEngine.DecodedFrame> Original,
        IReadOnlyList<ImageComparisonEngine.DecodedFrame> Baseline, ImageRegionDiffer.Result Regions, Settings Settings,
        ImageLineAlignment.Result? Alignment = null);
    internal sealed record Result(IReadOnlyList<ImageComparisonEngine.DecodedFrame> Frames, ImageDisplaySample Sample);

    internal static long RefreshWork(ImageComparisonEngine.FrameComparison comparison, Settings settings, bool prepare)
    {
        var regions = comparison.Regions; var canvas = (long)regions.Width * regions.Height; var count = comparison.Frames.Count;
        var source = comparison.Frames.Sum(frame => (long)frame.Width * frame.Height);
        return RefreshWorkMetadata(regions.Width, regions.Height, count, source,
            count == 3 ? (long)comparison.Frames[1].Width * comparison.Frames[1].Height : 0, settings, prepare);
    }

    internal static long RefreshWorkMetadata(int width, int height, int count, long source, long middleSource,
        Settings settings, bool prepare = true)
    {
        settings.Validate();
        var canvas = (long)width * height;
        var blend = settings.Mode == 0 ? 0 : count == 2 ? source : source + middleSource;
        var columns = (width + settings.BlockSize - 1) / settings.BlockSize;
        var rows = (height + settings.BlockSize - 1) / settings.BlockSize;
        var wipe = settings.Wipe?.Clamp(width, height);
        return checked((prepare ? canvas * count + source : 0) + canvas * count * 2 + blend
            + (long)columns * rows * (count + 2) + (settings.ShowDifferences ? canvas * count : 0)
            + (wipe is null ? 0 : ImageWipeRenderer.Work(width, height, count, wipe)));
    }

    // GUIの比較済み領域/整列を借り、raw診断のPrepareで再分類しない。
    internal static Prepared FromComparison(ImageComparisonEngine.FrameComparison comparison, Settings settings,
        ImageDisplayWorkBudget budget)
    {
        var canvas = (long)comparison.Regions.Width * comparison.Regions.Height;
        budget.Reserve(checked(canvas * comparison.Frames.Count + comparison.Frames.Sum(frame => (long)frame.Width * frame.Height)));
        var baseline = ImageRegionRenderer.Render(comparison.Frames, comparison.Regions, settings.BlockSize, settings.HighlightAlpha,
            settings.SelectedDiffIndex, budget.Token, false, comparison.Alignment);
        return new(comparison.Frames, baseline, comparison.Regions, settings, comparison.Alignment);
    }

    // 複数state全体をmetadataで確認してからcanvasを確保する。各stageでも同じ予算を予約する。
    internal static void Preflight(IReadOnlyList<ImageComparisonEngine.DecodedFrame> frames, IReadOnlyList<ImageOffset> offsets,
        Settings settings, int states, ImageDisplayWorkBudget budget)
    {
        settings.Validate();
        if (frames.Count is not (2 or 3) || states is < 1 or > 1024) throw new ArgumentException("overlay pane/state件数が不正です。");
        var positions = ImageOffset.Validate(offsets, frames.Count); var (width, height) = ImageOffset.Canvas(frames, positions);
        var canvas = (long)width * height; var count = frames.Count; var pairs = count == 2 ? 1 : 3;
        var grid = (long)((width + settings.BlockSize - 1) / settings.BlockSize) * ((height + settings.BlockSize - 1) / settings.BlockSize);
        var source = frames.Sum(frame => (long)frame.Width * frame.Height);
        var blend = settings.Mode == 0 ? 0 : count == 2 ? source : source + (long)frames[1].Width * frames[1].Height;
        var wipe = settings.Wipe?.Clamp(width, height);
        var preparation = checked(canvas * pairs + grid * (pairs + 10) + canvas * count + source);
        var state = checked(canvas * count * 2 + blend + grid * (count + 2) + (settings.ShowDifferences ? canvas * count : 0)
            + (wipe is null ? 0 : ImageWipeRenderer.Work(width, height, count, wipe)));
        budget.EnsureAvailable(checked(preparation + state * states));
    }

    internal static Prepared Prepare(IReadOnlyList<ImageComparisonEngine.DecodedFrame> frames, IReadOnlyList<ImageOffset> offsets,
        Settings settings, ImageDisplayWorkBudget budget)
    {
        settings.Validate(); budget.Token.ThrowIfCancellationRequested();
        if (frames.Count is not (2 or 3)) throw new ArgumentException("overlayは二者または三者です。");
        long source = 0;
        foreach (var frame in frames)
        {
            budget.Token.ThrowIfCancellationRequested();
            if (frame.Width <= 0 || frame.Height <= 0 || (long)frame.Width * frame.Height > ImageComparisonEngine.MaximumPixels
                || frame.Pixels.LongLength != (long)frame.Width * frame.Height * 4) throw new ArgumentException("overlayの寸法/BGRA長が不正です。");
            source = checked(source + (long)frame.Width * frame.Height);
        }
        var positions = ImageOffset.Validate(offsets, frames.Count); var (width, height) = ImageOffset.Canvas(frames, positions);
        var canvas = (long)width * height; var pairs = frames.Count == 2 ? 1 : 3;
        var grid = (long)((width + settings.BlockSize - 1) / settings.BlockSize) * ((height + settings.BlockSize - 1) / settings.BlockSize);
        // 分類のpair走査、grid分類/近傍、canvas zero-fillと原画copyを先に予約。
        budget.Reserve(checked(canvas * pairs + grid * (pairs + 10) + canvas * frames.Count + source));
        var regions = ImageRegionDiffer.Compare(frames, settings.BlockSize, settings.Threshold, budget.Token, positions);
        var baseline = ImageRegionRenderer.Render(frames, regions, settings.BlockSize, settings.HighlightAlpha,
            settings.SelectedDiffIndex, budget.Token, showDifferences: false);
        return new(frames, baseline, regions, settings);
    }

    internal static Result Render(Prepared prepared, IImageDisplayClock clock, ImageDisplayWorkBudget budget)
    {
        ArgumentNullException.ThrowIfNull(clock); var settings = prepared.Settings; settings.Validate();
        var raw = prepared.Original; var baseline = prepared.Baseline; var regions = prepared.Regions; var count = raw.Count;
        var canvas = (long)regions.Width * regions.Height;
        var pairs = count == 2 ? new[] { (Source: 1, Destination: 0), (Source: 0, Destination: 1) }
            : [(Source: 1, Destination: 0), (Source: 0, Destination: 1), (Source: 2, Destination: 1), (Source: 1, Destination: 2)];
        var blendWork = settings.Mode == 0 ? 0 : pairs.Sum(pair => (long)raw[pair.Source].Width * raw[pair.Source].Height);
        var wipe = settings.Wipe?.Clamp(regions.Width, regions.Height);
        var wipeWork = wipe is null ? 0 : ImageWipeRenderer.Work(regions.Width, regions.Height, count, wipe);
        var grid = (long)regions.Columns * regions.Rows;
        // clone baseline + sharedhighlight clone/検査 + 最大highlight走査 + wipe clone/copy。
        budget.Reserve(checked(canvas * count * 2 + blendWork + grid * (count + 2)
            + (settings.ShowDifferences ? canvas * count : 0) + wipeWork));
        var token = budget.Token;
        var output = baseline.Select(frame => { token.ThrowIfCancellationRequested(); return frame with { Pixels = frame.Pixels.ToArray() }; }).ToArray();
        var epochs = new List<long>(); var alphas = new List<double>();
        if (settings.Mode != 0)
        foreach (var pair in pairs)
        {
            token.ThrowIfCancellationRequested(); var alpha = settings.Alpha;
            if (settings.Mode == 3)
            {
                var t = ReadClock() % settings.AnimationPeriod; var a = settings.AnimationPeriod * 2 / 10;
                var b = settings.AnimationPeriod * 5 / 10; var c = settings.AnimationPeriod * 7 / 10;
                alpha = t < a ? (double)t / a : t < b ? 1 : t < c ? (double)(a - (t - b)) / a : 0;
            }
            if (settings.Mode is 2 or 3) alphas.Add(alpha);
            var source = raw[pair.Source]; var destination = output[pair.Destination].Pixels; var offset = regions.Offsets![pair.Source];
            for (var y = 0; y < source.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < source.Width; x++)
                {
                    if ((x & 4095) == 0) token.ThrowIfCancellationRequested();
                    var src = (y * source.Width + x) * 4; var dst = ((y + offset.Y) * regions.Width + x + offset.X) * 4;
                    for (var channel = 0; channel < (settings.Mode == 1 ? 3 : 4); channel++)
                        destination[dst + channel] = settings.Mode == 1 ? (byte)(destination[dst + channel] ^ source.Pixels[src + channel])
                            : (byte)(destination[dst + channel] * (1 - alpha) + source.Pixels[src + channel] * alpha);
                }
            }
        }
        var visible = settings.ShowDifferences;
        if (visible && settings.BlinkDifferences && ReadClock() % settings.BlinkPeriod < settings.BlinkPeriod / 2) visible = false;
        IReadOnlyList<ImageComparisonEngine.DecodedFrame> highlighted = ImageRegionRenderer.Render(raw, regions, settings.BlockSize,
            settings.HighlightAlpha, settings.SelectedDiffIndex, token, visible, prepared.Alignment, displayCanvas: output);
        if (wipe is not null) highlighted = ImageWipeRenderer.Render(highlighted, wipe, token);
        token.ThrowIfCancellationRequested(); return new(highlighted, new(epochs, alphas, visible, wipe));

        long ReadClock()
        {
            token.ThrowIfCancellationRequested(); var epoch = clock.ReadEpochMilliseconds();
            if (epoch < 0) throw new ArgumentException("overlay時計は非負epoch millisecondsです。");
            epochs.Add(epoch); token.ThrowIfCancellationRequested(); return epoch;
        }
    }
}
