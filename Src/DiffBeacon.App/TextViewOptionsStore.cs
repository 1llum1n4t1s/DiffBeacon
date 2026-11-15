using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiffBeacon.App;

/// <summary>
/// 「表示」メニューの設定の保存形式。本家WinMergeの Settings/ViewWhitespace・ViewEOL・ViewLineDifferences・ViewLineNumbers・
/// ViewSelectionMargin・ViewZoom・ShowToolbar に当たる（本家はレジストリ、ここは view-options.json）。
/// 省略した項目は既定値（本家と同じく空白・改行は表示しない、行内差異は表示する。行番号・マージン・ツールバーは DiffBeacon の従来どおり表示する）。
/// 位置引数ではなく初期値付きのプロパティにしているのは、{} や項目の欠けた設定でも既定値が true の項目を false にしないため。
/// </summary>
internal sealed record TextViewSettings
{
    public int ZoomSteps { get; init; }
    public bool ViewWhitespace { get; init; }
    public bool ViewEndOfLine { get; init; }
    public bool ViewLineDifferences { get; init; } = true;
    public bool ViewLineNumbers { get; init; } = true;
    public bool ViewMargins { get; init; } = true;
    public bool ShowToolbar { get; init; } = true;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(TextViewSettings))]
internal partial class TextViewSettingsJsonContext : JsonSerializerContext;

/// <summary>
/// 「表示」メニューの設定を desktop 内で一つ保存する（Kagayoi/DiffBeacon/view-options.json）。
///
/// 設計の要点:
/// - 本家と同じく、起動時に読み、メニュー・キー・Ctrl+ホイールで変えるたびに保存する（本家もレジストリへ都度書く）。
/// - 読めない設定（壊れたJSON・知らない項目・範囲外の拡大・16 KiB超・フォルダー）は既定値で表示し、ファイルは上書きしない
///   （利用者が直せるよう残す）。利用者が設定を変えたときに初めて正しい内容で置き換える。
/// - 保存に失敗しても、その起動の間は変えた表示を使い続ける（見た目だけの設定なので、表示を戻すと操作が効かないように見えるため）。
///   フォルダー一覧の列の設定（失敗したら列を変えない）とはここが異なる。失敗は状態欄へ出す。
/// - path=null はメモリだけの設定（headless検証が利用者のAppDataへ書かないため）。保存は原子的（一時ファイル→置換）で、
///   比較入力・workspace を上書きしないよう出力保護をかける。
/// </summary>
internal sealed class TextViewOptionsStore
{
    internal const int MaximumBytes = 16 * 1024;
    private readonly string? _path;
    private string? _unavailableReason;
    private readonly List<Action<string>> _outputGuards = [];
    private TextViewSettings _current = new();

    internal TextViewSettings Current => _current;
    internal string? Diagnostic { get; private set; }
    internal string? Path => _path;
    internal bool? LastSaveResult { get; private set; }
    /// <summary>保存を試みた回数（読込みだけでは増えないことの検証用）。</summary>
    internal long SaveAttemptCount { get; private set; }

    internal TextViewOptionsStore(string? path = null)
    {
        _path = path;
        if (path is not null) Reload();
    }

    internal static TextViewOptionsStore ForDesktop(string? applicationDataDirectory = null)
    {
        var directory = applicationDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(directory)
            ? new() { Diagnostic = "表示メニューの設定の保存先を取得できませんでした。",
                _unavailableReason = "表示メニューの設定の保存先を取得できないため変更を保存できません。" }
            : new(System.IO.Path.Combine(directory, "Kagayoi", "DiffBeacon", "view-options.json"));
    }

    internal void AddOutputGuard(Action<string> guard) => _outputGuards.Add(guard);
    internal void RemoveOutputGuard(Action<string> guard) => _outputGuards.Remove(guard);

    /// <summary>読み込んだ設定も保存する設定も同じ規則で確かめる。拡大は本家と同じ範囲（2ポイント～800%）の中だけ。</summary>
    internal static void Validate(TextViewSettings settings)
    {
        if (settings.ZoomSteps < DiffTextViewOptions.MinimumZoomSteps || settings.ZoomSteps > DiffTextViewOptions.MaximumZoomSteps)
            throw new InvalidDataException($"表示メニューの設定の拡大（zoomSteps）は {DiffTextViewOptions.MinimumZoomSteps}～{DiffTextViewOptions.MaximumZoomSteps} にしてください: {settings.ZoomSteps}");
    }

    internal bool Reload()
    {
        if (_path is null) return true;
        try
        {
            var path = ImagePngStore.ValidateLocal(_path);
            if (Directory.Exists(path)) throw new IOException("表示メニューの設定の保存先がフォルダーです。");
            if (!File.Exists(path)) { _current = new(); Diagnostic = null; return true; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("表示メニューの設定は16 KiB以下です。");
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("読込み中に表示メニューの設定のサイズが変更されました。");
            var settings = JsonSerializer.Deserialize(bytes, TextViewSettingsJsonContext.Default.TextViewSettings)
                ?? throw new InvalidDataException("表示メニューの設定が空です。");
            Validate(settings);
            _current = settings; Diagnostic = null; return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            _current = new();
            Diagnostic = "表示メニューの設定を読み込めません。既定の表示にします: " + error.Message;
            return false;
        }
    }

    /// <summary>設定を保存する。保存できなければ false と Diagnostic を返す（記憶している値は保存できた最後の値のまま）。</summary>
    internal bool Save(TextViewSettings settings)
    {
        SaveAttemptCount++;
        LastSaveResult = null;
        string? temporary = null;
        try
        {
            Validate(settings);
            if (_unavailableReason is not null) throw new IOException(_unavailableReason);
            if (_path is not null)
            {
                var path = GuardTarget();
                var directory = System.IO.Path.GetDirectoryName(path) ?? throw new IOException("表示メニューの設定の保存先を解決できません。");
                Directory.CreateDirectory(directory);
                temporary = System.IO.Path.Combine(directory, ".diffbeacon-view-options-" + Guid.NewGuid().ToString("N") + ".tmp");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, TextViewSettingsJsonContext.Default.TextViewSettings);
                if (bytes.Length > MaximumBytes) throw new InvalidDataException("表示メニューの設定は16 KiB以下です。");
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
            _current = settings;
            Diagnostic = null;
            LastSaveResult = true;
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or JsonException or NotSupportedException)
        {
            Diagnostic = "表示メニューの設定を保存できません: " + error.Message;
            LastSaveResult = false;
            return false;
        }
        finally
        {
            // 製品の原子的保存で作った一時ファイル。保存に失敗したときだけ残っている。
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Diagnostic += " 一時設定ファイルを削除できません: " + temporary + " (" + error.Message + ")"; }
            }
        }
    }

    private string GuardTarget()
    {
        var path = ImagePngStore.ValidateLocal(_path!);
        if (Directory.Exists(path)) throw new IOException("表示メニューの設定の保存先がフォルダーです。");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException("読取り専用の表示メニューの設定は保存できません。");
        foreach (var guard in _outputGuards.ToArray()) guard(path);
        return path;
    }
}
