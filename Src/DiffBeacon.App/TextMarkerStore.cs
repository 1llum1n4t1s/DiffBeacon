using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DiffBeacon.Core;

namespace DiffBeacon.App;

/// <summary>一つの印。色は 0 緑、1 赤、2 青。本家の利用者定義マーカー。</summary>
internal sealed record TextMarker(string Text, int ColorIndex, bool MatchCase, bool WholeWord, bool Regex, bool Visible);

/// <summary>印の保存形式。本家はレジストリの Marker。ここは markers.json。</summary>
internal sealed record TextMarkerSettings
{
    /// <summary>印を塗るか。偽でも一覧は残す。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>利用者の印。</summary>
    public TextMarkerItem[] Markers { get; init; } = [];
}

/// <summary>印の1件。省略した項目は既定（色0、区別しない、単語ではない、正規表現ではない、表示する）。</summary>
internal sealed record TextMarkerItem
{
    /// <summary>探す文字列。</summary>
    public string Text { get; init; } = "";

    /// <summary>0〜2。</summary>
    public int Color { get; init; }

    /// <summary>大文字と小文字を区別する。</summary>
    public bool MatchCase { get; init; }

    /// <summary>単語単位。正規表現のときは使わない。</summary>
    public bool WholeWord { get; init; }

    /// <summary>正規表現。</summary>
    public bool Regex { get; init; }

    /// <summary>一覧に出して塗る。</summary>
    public bool Visible { get; init; } = true;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(TextMarkerSettings))]
internal partial class TextMarkerSettingsJsonContext : JsonSerializerContext;

/// <summary>印を view-options.json の隣の markers.json へ保存する。path が null のときはメモリだけ。</summary>
internal sealed class TextMarkerStore
{
    private readonly string? _path;
    private readonly List<Action<string>> _outputGuards = [];

    internal TextMarkerStore(string? viewOptionsPath)
    {
        if (string.IsNullOrWhiteSpace(viewOptionsPath)) return;
        var directory = Path.GetDirectoryName(viewOptionsPath);
        _path = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "markers.json");
    }

    /// <summary>比較入力を上書きしないための検査。</summary>
    internal void AddOutputGuard(Action<string> guard) => _outputGuards.Add(guard);

    /// <summary>保存してある印を読む。ファイルが無いときは空。壊れていたら空にして理由を返す。</summary>
    internal (bool Enabled, TextMarker[] Markers, string? Error) Load()
    {
        if (_path is null || !File.Exists(_path)) return (true, [], null);
        try
        {
            var path = ImagePngStore.ValidateLocal(_path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 64 * 1024) throw new InvalidDataException("印の設定は64 KiB以下です。");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            var settings = JsonSerializer.Deserialize(bytes, TextMarkerSettingsJsonContext.Default.TextMarkerSettings)
                ?? throw new InvalidDataException("印の設定が空です。");
            var markers = new TextMarker[settings.Markers.Length];
            for (var index = 0; index < settings.Markers.Length; index++)
            {
                var item = settings.Markers[index];
                if (item.Text.Length is 0 or > 1024 || item.Color is < 0 or > 2)
                    throw new InvalidDataException("印の文字列は1〜1024文字、色は0〜2です。");
                if (item.Regex)
                {
                    PcrePattern.RejectUnsupported(item.Text);
                    _ = new Regex(PcrePattern.RewritePossessive(item.Text), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
                }
                markers[index] = new TextMarker(item.Text, item.Color, item.MatchCase, item.WholeWord && !item.Regex, item.Regex, item.Visible);
            }
            return (settings.Enabled, markers, null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or FormatException or RegexParseException)
        {
            return (true, [], "印の設定を読み込めません。印は無しにします: " + error.Message);
        }
    }

    /// <summary>印を保存する。path が null のときは何もしない。</summary>
    internal bool Save(bool enabled, IReadOnlyList<TextMarker> markers, out string? error)
    {
        error = null;
        if (_path is null) return true;
        string? temporary = null;
        try
        {
            var path = ImagePngStore.ValidateLocal(_path);
            foreach (var guard in _outputGuards) guard(path);
            foreach (var marker in markers)
            {
                if (marker.Text.Length is 0 or > 1024 || marker.ColorIndex is < 0 or > 2)
                    throw new InvalidDataException("印の文字列は1〜1024文字、色は0〜2です。");
                if (!marker.Regex) continue;
                PcrePattern.RejectUnsupported(marker.Text);
                _ = new Regex(PcrePattern.RewritePossessive(marker.Text), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            var settings = new TextMarkerSettings
            {
                Enabled = enabled,
                Markers = markers.Select(marker => new TextMarkerItem
                {
                    Text = marker.Text, Color = marker.ColorIndex, MatchCase = marker.MatchCase,
                    WholeWord = marker.WholeWord, Regex = marker.Regex, Visible = marker.Visible,
                }).ToArray(),
            };
            var directory = Path.GetDirectoryName(path) ?? throw new IOException("印の設定の保存先を解決できません。");
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".diffbeacon-markers-" + Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, TextMarkerSettingsJsonContext.Default.TextMarkerSettings);
            if (bytes.Length > 64 * 1024) throw new InvalidDataException("印の設定は64 KiB以下です。");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            foreach (var guard in _outputGuards) guard(path);
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or JsonException or NotSupportedException)
        {
            error = "印の設定を保存できません: " + exception.Message;
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { error += " " + exception.Message; }
            }
        }
    }
}
