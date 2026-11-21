using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiffBeacon.App;

/// <summary>
/// 二者または三者のフォルダー一覧の列の配置。本家WinMergeの DirView/ColumnOrders・DirView/ColumnWidths・Settings/DirViewSortCol(3)・
/// Settings/DirViewSortAscending に当たる（本家は二者と三者で別のキーを持つので、ここも別に持つ）。
/// Columns は表示する列を表示順に並べたもの、Widths は null なら既定の幅、値があれば Columns と同じ順の相対幅（px と同じ尺度）。
/// </summary>
internal sealed record FolderColumnLayoutOptions
{
    public string[] Columns { get; init; } = [];
    public double[]? Widths { get; init; }
    public string? SortColumn { get; init; }
    public bool SortDescending { get; init; }
}

internal sealed record FolderColumnOptions
{
    public FolderColumnLayoutOptions? TwoWay { get; init; }
    public FolderColumnLayoutOptions? ThreeWay { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(FolderColumnOptions))]
internal partial class FolderColumnOptionsJsonContext : JsonSerializerContext;

/// <summary>
/// フォルダー一覧の列の配置を、desktop 内の全ペインで共有して保存する。
/// 本家と同じく、ペインは一覧を最初に作るときに読み、変えたペインが保存する。開いている他のペインの表示は変えない。
/// フォルダー表示設定（folder-options.json）とは別のファイルにして、既存の設定ファイルの内容と検証を変えない。
/// </summary>
internal sealed class FolderColumnOptionsStore
{
    internal const int MaximumBytes = 16 * 1024;
    internal const double MaximumWidth = 100_000;
    private readonly string? _path;
    private string? _unavailableReason;
    private readonly List<Action<string>> _outputGuards = [];
    private FolderColumnOptions _current = new();

    internal FolderColumnOptions Current => _current;
    internal string? Diagnostic { get; private set; }
    internal string? Path => _path;

    // path=null はheadless用のメモリ設定（利用者のAppDataへ書かない）。
    internal FolderColumnOptionsStore(string? path = null)
    {
        _path = path;
        if (path is not null) Reload();
    }

    internal static FolderColumnOptionsStore ForDesktop(string? applicationDataDirectory = null)
    {
        var directory = applicationDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(directory)
            ? new() { Diagnostic = "フォルダー一覧の列の設定の保存先を取得できませんでした。",
                _unavailableReason = "フォルダー一覧の列の設定の保存先を取得できないため変更を保存できません。" }
            : new(System.IO.Path.Combine(directory, "Kagayoi", "DiffBeacon", "folder-columns.json"));
    }

    internal void AddOutputGuard(Action<string> guard) => _outputGuards.Add(guard);
    internal void RemoveOutputGuard(Action<string> guard) => _outputGuards.Remove(guard);

    /// <summary>その比較の種類（二者／三者）の保存済み配置。無いか使えない値なら null（既定の配置）。</summary>
    internal FolderColumnLayoutOptions? Layout(bool threeWay) => threeWay ? _current.ThreeWay : _current.TwoWay;

    /// <summary>
    /// 保存できる配置か確かめる。列は空でなく、その比較の種類で選べる列だけを重複なく並べ、幅は列と同じ数の正の有限値、
    /// 並べ替えの列は表示している列であること。読み込んだ設定も保存する設定も同じ規則で確かめる。
    /// </summary>
    internal static void Validate(FolderColumnLayoutOptions? layout, bool threeWay)
    {
        if (layout is null) return;
        var kind = threeWay ? "三者" : "二者";
        if (layout.Columns is not { Length: > 0 } columns) throw new InvalidDataException(kind + "の列の設定に表示する列がありません。");
        var seen = new HashSet<FolderColumn>();
        foreach (var name in columns)
        {
            if (!Enum.TryParse<FolderColumn>(name, ignoreCase: false, out var column) || !Enum.IsDefined(column) || column.ToString() != name
                || !FolderColumnCatalog.Supports(column, threeWay))
                throw new InvalidDataException(kind + "の列の設定に使えない列があります: " + name);
            if (!seen.Add(column)) throw new InvalidDataException(kind + "の列の設定で同じ列が重複しています: " + name);
        }
        if (layout.Widths is { } widths)
        {
            if (widths.Length != columns.Length) throw new InvalidDataException(kind + "の列の幅の数が列の数と一致しません。");
            if (widths.Any(width => !double.IsFinite(width) || width <= 0 || width > MaximumWidth))
                throw new InvalidDataException(kind + "の列の幅は0より大きく" + MaximumWidth.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "以下にしてください。");
        }
        if (layout.SortColumn is { } sort && !columns.Contains(sort, StringComparer.Ordinal))
            throw new InvalidDataException(kind + "の並べ替えの列が表示している列にありません: " + sort);
    }

    internal bool Reload()
    {
        if (_path is null) return true;
        try
        {
            var path = ImagePngStore.ValidateLocal(_path);
            if (Directory.Exists(path)) throw new IOException("フォルダー一覧の列の設定の保存先がフォルダーです。");
            if (!File.Exists(path)) { _current = new(); Diagnostic = null; return true; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("フォルダー一覧の列の設定は16 KiB以下です。");
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("読込み中にフォルダー一覧の列の設定のサイズが変更されました。");
            var options = JsonSerializer.Deserialize(bytes, FolderColumnOptionsJsonContext.Default.FolderColumnOptions)
                ?? throw new InvalidDataException("フォルダー一覧の列の設定が空です。");
            Validate(options.TwoWay, false); Validate(options.ThreeWay, true);
            _current = options; Diagnostic = null; return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            // 壊れた設定は既定の配置で表示し、ファイルは上書きしない（利用者が直せるよう残す）。
            _current = new();
            Diagnostic = "フォルダー一覧の列の設定を読み込めません。既定の列で表示します: " + error.Message;
            return false;
        }
    }

    internal bool? LastSetResult { get; private set; }
    internal long SetAttemptCount { get; private set; }

    /// <summary>その比較の種類の配置を保存する。null は既定の配置へ戻す。保存できなければ false と Diagnostic を返し、記憶している値も変えない。</summary>
    internal bool SetLayout(bool threeWay, FolderColumnLayoutOptions? layout)
    {
        SetAttemptCount++;
        LastSetResult = null;
        string? temporary = null;
        try
        {
            Validate(layout, threeWay);
            var options = threeWay ? _current with { ThreeWay = layout } : _current with { TwoWay = layout };
            if (_unavailableReason is not null) throw new IOException(_unavailableReason);
            if (_path is not null)
            {
                var path = GuardTarget();
                var directory = System.IO.Path.GetDirectoryName(path) ?? throw new IOException("フォルダー一覧の列の設定の保存先を解決できません。");
                Directory.CreateDirectory(directory);
                temporary = System.IO.Path.Combine(directory, ".diffbeacon-folder-columns-" + Guid.NewGuid().ToString("N") + ".tmp");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(options, FolderColumnOptionsJsonContext.Default.FolderColumnOptions);
                if (bytes.Length > MaximumBytes) throw new InvalidDataException("フォルダー一覧の列の設定は16 KiB以下です。");
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
            LastSetResult = true;
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or JsonException or NotSupportedException)
        {
            Diagnostic = "フォルダー一覧の列の設定を保存できません: " + error.Message;
            LastSetResult = false;
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
        if (Directory.Exists(path)) throw new IOException("フォルダー一覧の列の設定の保存先がフォルダーです。");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException("読取り専用のフォルダー一覧の列の設定は保存できません。");
        foreach (var guard in _outputGuards.ToArray()) guard(path);
        return path;
    }
}
