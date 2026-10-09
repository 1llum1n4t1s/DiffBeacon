using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiffBeacon.App;

internal sealed record FolderApplicationOptions
{
    public bool TreeMode { get; set; } = true;
    public int InitialExpansion { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(FolderApplicationOptions))]
internal partial class FolderApplicationOptionsJsonContext : JsonSerializerContext;

// 新規paneの既定値だけを共有する。既存paneと個別展開状態へは通知しない。
internal sealed class FolderApplicationOptionsStore
{
    internal const int MaximumBytes = 4096;
    private readonly string? _path;
    private readonly List<Action<string>> _outputGuards = [];
    private string? _unavailableReason;
    private FolderApplicationOptions _current;
    internal FolderApplicationOptions Current => _current with { };
    internal string? Diagnostic { get; private set; }
    internal Action? SaveBeforePublish { get; set; }

    // 既存headless入口の互換値はflat/none。path注入とdesktopはtree/none。
    internal FolderApplicationOptionsStore(string? path = null, bool desktopDefaults = false)
    {
        _path = path;
        _current = new() { TreeMode = path is not null || desktopDefaults };
        if (path is not null) Reload();
    }

    internal static FolderApplicationOptionsStore ForDesktop(string? applicationDataDirectory = null)
    {
        var directory = applicationDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(directory)
            ? new(desktopDefaults: true) { Diagnostic = "フォルダー表示設定の保存先を取得できませんでした。",
                _unavailableReason = "フォルダー表示設定の保存先を取得できないため変更を保存できません。" }
            : new(Path.Combine(directory, "Kagayoi", "DiffBeacon", "folder-options.json"));
    }

    internal void AddOutputGuard(Action<string> guard) => _outputGuards.Add(guard);
    internal void RemoveOutputGuard(Action<string> guard) => _outputGuards.Remove(guard);
    internal static void Validate(FolderApplicationOptions options)
    {
        if (options.InitialExpansion is < 0 or > 3)
            throw new InvalidDataException("フォルダーの初期展開は0～3から指定してください。");
    }

    internal bool Reload()
    {
        if (_path is null) return true;
        try
        {
            var path = ImagePngStore.ValidateLocal(_path);
            if (Directory.Exists(path)) throw new IOException("フォルダー表示設定の保存先がフォルダーです。");
            if (!File.Exists(path)) { _current = new(); Diagnostic = null; return true; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("フォルダー表示設定は4 KiB以下です。");
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("読込み中にフォルダー表示設定のサイズが変更されました。");
            var options = JsonSerializer.Deserialize(bytes, FolderApplicationOptionsJsonContext.Default.FolderApplicationOptions)
                ?? throw new InvalidDataException("フォルダー表示設定が空です。");
            Validate(options); _current = options; Diagnostic = null; return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        { Diagnostic = "フォルダー表示設定を読み込めません。既存の設定を保持しました: " + error.Message; return false; }
    }

    internal bool? LastSetOptionsResult { get; private set; }
    internal long SetOptionsAttemptCount { get; private set; }
    internal bool SetOptions(FolderApplicationOptions requested, Action? ensureCurrent = null)
    {
        SetOptionsAttemptCount++; LastSetOptionsResult = null;
        string? temporary = null;
        try
        {
            var options = requested with { }; Validate(options); ensureCurrent?.Invoke();
            if (_unavailableReason is not null) throw new IOException(_unavailableReason);
            if (_path is not null)
            {
                var path = GuardTarget(); var directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                temporary = Path.Combine(directory, ".diffbeacon-folder-options-" + Guid.NewGuid().ToString("N") + ".tmp");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(options, FolderApplicationOptionsJsonContext.Default.FolderApplicationOptions);
                if (bytes.Length > MaximumBytes) throw new InvalidDataException("フォルダー表示設定は4 KiB以下です。");
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
                SaveBeforePublish?.Invoke(); GuardTarget(); ensureCurrent?.Invoke();
                File.Move(temporary, path, overwrite: true); temporary = null;
            }
            else { SaveBeforePublish?.Invoke(); ensureCurrent?.Invoke(); }
            _current = options; Diagnostic = null; LastSetOptionsResult = true; return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException)
        { Diagnostic = "フォルダー表示設定を保存できません。表示は変更していません: " + error.Message; LastSetOptionsResult = false; return false; }
        finally
        {
            // 製品の原子的保存で生成した一時file。agent作業物の清掃経路とは別。
            if (temporary is not null && File.Exists(temporary))
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Diagnostic += " 一時設定ファイルを削除できません: " + temporary + " (" + error.Message + ")"; }
        }
    }

    private string GuardTarget()
    {
        var path = ImagePngStore.ValidateLocal(_path!);
        if (Directory.Exists(path)) throw new IOException("フォルダー表示設定の保存先がフォルダーです。");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException("読取り専用のフォルダー表示設定は保存できません。");
        foreach (var guard in _outputGuards.ToArray()) guard(path);
        return path;
    }
}