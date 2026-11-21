using System.Collections.ObjectModel;

namespace DiffBeacon.App;

internal interface IImageDisplayClock
{
    long ReadEpochMilliseconds();
}

internal sealed class ImageSystemDisplayClock : IImageDisplayClock
{
    public long ReadEpochMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

// 保存時に時計を再読せず、採用した一回のRefreshを再現するための所有sample。
internal sealed class ImageDisplaySample
{
    internal ReadOnlyCollection<long> Epochs { get; }
    internal ReadOnlyCollection<double> BlendAlphas { get; }
    internal bool HighlightVisible { get; }
    internal ImageWipeSnapshot? Wipe { get; }
    internal ImageDisplaySample(IEnumerable<long> epochs, IEnumerable<double> alphas, bool visible, ImageWipeSnapshot? wipe)
    { Epochs = Array.AsReadOnly(epochs.ToArray()); BlendAlphas = Array.AsReadOnly(alphas.ToArray()); HighlightVisible = visible; Wipe = wipe; }
}

// 復号・分類・表示の追加走査は、独立の上限を作らず既存256Mから予約する。
internal sealed class ImageDisplayWorkBudget
{
    internal long Used { get; private set; }
    internal long Maximum { get; }
    internal CancellationToken Token { get; }
    internal ImageDisplayWorkBudget(long maximum, CancellationToken token)
    {
        if (maximum < 0 || maximum > ImageComparisonEngine.MaximumDecodeWork) throw new ArgumentOutOfRangeException(nameof(maximum));
        Maximum = maximum; Token = token;
    }
    internal void Reserve(long work)
    {
        EnsureAvailable(work);
        Used += work;
    }
    internal void EnsureAvailable(long work)
    {
        Token.ThrowIfCancellationRequested();
        if (work < 0 || work > Maximum - Used) throw new InvalidOperationException("overlayを含む描画作業量が共有256Mピクセル予算を超えます。");
    }
}
