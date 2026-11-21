using System.Globalization;
using System.Text.Json;

namespace DiffBeacon.App;

// 通常CLIはAppDataを読まず、指定された表示値だけを上書きする。
internal sealed class ImageDisplayOptions
{
    private int? _mode, _period, _blinkPeriod;
    private double? _alpha;
    private bool? _blink;
    internal bool Specified => _mode.HasValue || _alpha.HasValue || _blink.HasValue || _period.HasValue || _blinkPeriod.HasValue;
    internal static bool IsOption(string name) => name is "--overlay-mode" or "--overlay-alpha" or "--overlay-blink" or "--overlay-period" or "--blink-period";
    internal void Parse(string name, string value)
    {
        switch (name)
        {
            case "--overlay-mode":
                _mode = value switch { "none" => 0, "xor" => 1, "alpha" => 2, "anim" => 3,
                    _ => throw new ArgumentException("overlay modeはnone|xor|alpha|animです。") }; break;
            case "--overlay-alpha":
                if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var alpha)
                    || !double.IsFinite(alpha) || alpha is < 0 or > 1) throw new ArgumentException("overlay alphaは有限の0..1です。");
                _alpha = alpha; break;
            case "--overlay-blink":
                _blink = value switch { "true" => true, "false" => false,
                    _ => throw new ArgumentException("overlay blinkはtrue|falseです。") }; break;
            case "--overlay-period": _period = Period(value); break;
            case "--blink-period": _blinkPeriod = Period(value); break;
            default: throw new ArgumentException("未知のoverlayオプションです。");
        }
    }
    private static int Period(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var period)
        && period is >= 200 and <= 8000 ? period : throw new ArgumentException("overlay/blink periodは200..8000です。");
    internal ImageOverlayRenderer.Settings? Settings(double fallbackAlpha = .3, bool show = true, int blockSize = 8,
        double threshold = 0, double highlightAlpha = .7, int selected = -1, ImageWipeSnapshot? wipe = null)
        => !Specified ? null : new(_mode ?? 0, _alpha ?? fallbackAlpha, show, _blink ?? false, _period ?? 1000,
            _blinkPeriod ?? 800, blockSize, threshold, highlightAlpha, selected, wipe);
    internal static void WriteMetadata(Utf8JsonWriter writer, ImageOverlayRenderer.Settings settings)
    {
        writer.WriteNumber("overlayMode", settings.Mode); writer.WriteNumber("overlayAlpha", settings.Alpha);
        writer.WriteBoolean("overlayBlink", settings.BlinkDifferences); writer.WriteNumber("overlayPeriod", settings.AnimationPeriod);
        writer.WriteNumber("blinkPeriod", settings.BlinkPeriod);
    }
}
