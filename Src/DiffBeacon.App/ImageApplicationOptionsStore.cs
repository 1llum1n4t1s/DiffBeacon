using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiffBeacon.App;

internal sealed record ImageApplicationOptions
{
    public ImageDragMode DragMode { get; init; } = ImageDragMode.Move;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ImageApplicationOptions))]
internal partial class ImageApplicationOptionsJsonContext : JsonSerializerContext;

// プロジェクト設定から独立した、desktop 内の全画像パネルで共有する設定。
internal sealed class ImageApplicationOptionsStore
{
    internal const int MaximumBytes = 4096;
    private readonly string? _path;
    private string? _unavailableReason;
    private readonly List<Action<string>> _outputGuards = [];
    internal ImageDragMode Mode { get; private set; } = ImageDragMode.Move;
    internal string? Diagnostic { get; private set; }
    internal event Action? Changed;

    // path=null は headless 用メモリ設定。OS の設定パスにはアクセスしない。
    internal ImageApplicationOptionsStore(string? path = null)
    {
        _path = path;
        if (path is not null) Reload();
    }

    internal static ImageApplicationOptionsStore ForDesktop(string? applicationDataDirectory = null)
    {
        var directory = applicationDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(directory)
            ? new() { Diagnostic = "画像操作設定の保存先を取得できませんでした。今回は表示を移動モードで使用します。",
                _unavailableReason = "画像操作設定の保存先を取得できないため変更を保存できません。" }
            : new(Path.Combine(directory, "Kagayoi", "DiffBeacon", "options.json"));
    }

    internal void AddOutputGuard(Action<string> guard) => _outputGuards.Add(guard);
    internal void RemoveOutputGuard(Action<string> guard) => _outputGuards.Remove(guard);
    internal static void ValidateMode(ImageDragMode mode)
    {
        if (mode is not (ImageDragMode.None or ImageDragMode.Move or ImageDragMode.AdjustOffset or ImageDragMode.VerticalWipe or ImageDragMode.HorizontalWipe or ImageDragMode.RectangleSelect))
            throw new InvalidDataException("画像ドラッグモードは0～5から指定してください。");
    }

    internal bool Reload()
    {
        if (_path is null) return true;
        try
        {
            var path = ImagePngStore.ValidateLocal(_path);
            if (!File.Exists(path)) { Diagnostic = null; return true; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("画像操作設定は4 KiB以下です。");
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("読込み中に画像操作設定のサイズが変更されました。");
            var options = JsonSerializer.Deserialize(bytes, ImageApplicationOptionsJsonContext.Default.ImageApplicationOptions)
                ?? throw new InvalidDataException("画像操作設定が空です。");
            ValidateMode(options.DragMode);
            Mode = options.DragMode; Diagnostic = null; NotifyChanged(); return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        { Diagnostic = "画像操作設定を読み込めません。既存の設定ファイルを保持しました: " + error.Message; return false; }
    }

    internal bool SetMode(ImageDragMode mode)
    {
        string? temporary = null;
        try
        {
            ValidateMode(mode);
            if (_unavailableReason is not null) throw new IOException(_unavailableReason);
            if (_path is not null)
            {
                var path = GuardTarget();
                var directory = Path.GetDirectoryName(path)!; Directory.CreateDirectory(directory);
                temporary = Path.Combine(directory, ".diffbeacon-options-" + Guid.NewGuid().ToString("N") + ".tmp");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new ImageApplicationOptions { DragMode = mode }, ImageApplicationOptionsJsonContext.Default.ImageApplicationOptions);
                var attributes = File.Exists(path) ? File.GetAttributes(path) : FileAttributes.Normal;
                UnixFileMode? unixMode = !OperatingSystem.IsWindows() && File.Exists(path) ? File.GetUnixFileMode(path) : null;
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(bytes); stream.Flush(true); }
                if (unixMode.HasValue && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, unixMode.Value);
                if (OperatingSystem.IsWindows())
                {
                    var preserved = attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed);
                    File.SetAttributes(temporary, preserved == 0 ? FileAttributes.Normal : preserved);
                }
                GuardTarget(); File.Move(temporary, path, overwrite: true); temporary = null;
            }
            Mode = mode; Diagnostic = null; NotifyChanged(); return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Diagnostic = "画像操作設定を保存できません。操作モードは変更していません: " + error.Message; return false; }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Diagnostic += " 一時設定ファイルを削除できません: " + temporary + " (" + error.Message + ")"; }
        }
    }

    private void NotifyChanged()
    {
        foreach (var listener in Changed?.GetInvocationList() ?? [])
            try { ((Action)listener)(); }
            catch (Exception error)
            { Diagnostic = "画像操作モードは確定しましたが、表示への通知を完了できません: " + error.Message; }
    }

    private string GuardTarget()
    {
        var path = ImagePngStore.ValidateLocal(_path!);
        if (Directory.Exists(path)) throw new IOException("画像操作設定の保存先がフォルダーです。");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException("読取り専用の画像操作設定は保存できません。");
        foreach (var guard in _outputGuards.ToArray()) guard(path);
        return path;
    }
}
