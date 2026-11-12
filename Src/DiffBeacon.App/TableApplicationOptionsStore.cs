using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiffBeacon.App;

internal sealed record TableApplicationOptions
{
    public bool UseFirstRowAsHeaders { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(TableApplicationOptions))]
internal partial class TableApplicationOptionsJsonContext : JsonSerializerContext;

// 比較プロジェクトから独立した、desktop 内の全表パネルで共有する設定。
internal sealed class TableApplicationOptionsStore
{
    internal const int MaximumBytes = 4096;
    private readonly string? _path;
    private string? _unavailableReason;
    private readonly List<Action<string>> _outputGuards = [];
    private TableApplicationOptions _current = new();

    internal TableApplicationOptions Current => _current with { };
    internal string? Diagnostic { get; private set; }
    internal event Action? Changed;

    // path=null はheadless用のメモリ設定。
    internal TableApplicationOptionsStore(string? path = null)
    {
        _path = path;
        if (path is not null) Reload();
    }

    internal static TableApplicationOptionsStore ForDesktop(string? applicationDataDirectory = null)
    {
        var directory = applicationDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(directory)
            ? new() { Diagnostic = "表表示設定の保存先を取得できませんでした。",
                _unavailableReason = "表表示設定の保存先を取得できないため変更を保存できません。" }
            : new(Path.Combine(directory, "Kagayoi", "DiffBeacon", "table-options.json"));
    }

    internal void AddOutputGuard(Action<string> guard) => _outputGuards.Add(guard);
    internal void RemoveOutputGuard(Action<string> guard) => _outputGuards.Remove(guard);

    internal bool Reload()
    {
        if (_path is null) return true;
        try
        {
            var path = ImagePngStore.ValidateLocal(_path);
            if (Directory.Exists(path)) throw new IOException("表表示設定の保存先がフォルダーです。");
            if (!File.Exists(path)) { _current = new(); Diagnostic = null; return true; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("表表示設定は4 KiB以下です。");
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("読込み中に表表示設定のサイズが変更されました。");
            var options = JsonSerializer.Deserialize(bytes, TableApplicationOptionsJsonContext.Default.TableApplicationOptions)
                ?? throw new InvalidDataException("表表示設定が空です。");
            _current = options; Diagnostic = null; return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        { Diagnostic = "表表示設定を読み込めません。既存の設定を保持しました: " + error.Message; return false; }
    }

    internal bool? LastSetOptionsResult { get; private set; }
    internal long SetOptionsAttemptCount { get; private set; }

    internal bool SetOptions(TableApplicationOptions requested)
    {
        SetOptionsAttemptCount++;
        LastSetOptionsResult = null;
        string? temporary = null;
        try
        {
            var options = requested with { };
            if (options == _current) { LastSetOptionsResult = true; return true; }
            if (_unavailableReason is not null) throw new IOException(_unavailableReason);
            if (_path is not null)
            {
                var path = GuardTarget();
                var directory = Path.GetDirectoryName(path) ?? throw new IOException("表表示設定の保存先を解決できません。");
                Directory.CreateDirectory(directory);
                temporary = Path.Combine(directory, ".diffbeacon-table-options-" + Guid.NewGuid().ToString("N") + ".tmp");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(options, TableApplicationOptionsJsonContext.Default.TableApplicationOptions);
                if (bytes.Length > MaximumBytes) throw new InvalidDataException("表表示設定は4 KiB以下です。");
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
                GuardTarget();
                File.Move(temporary, path, overwrite: true);
                temporary = null;
            }
            _current = options;
            Diagnostic = null;
            LastSetOptionsResult = true;
            Changed?.Invoke();
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or JsonException or NotSupportedException)
        {
            Diagnostic = "表表示設定を保存できません。現在の設定を保持しました: " + error.Message;
            LastSetOptionsResult = false;
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private string GuardTarget()
    {
        var path = ImagePngStore.ValidateLocal(_path!);
        if (Directory.Exists(path)) throw new IOException("表表示設定の保存先がフォルダーです。");
        foreach (var guard in _outputGuards) guard(path);
        return path;
    }
}
