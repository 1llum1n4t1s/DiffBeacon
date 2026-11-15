using System.Globalization;

namespace DiffBeacon.App;

/// <summary>
/// フォルダー一覧の列。本家WinMergeの DirViewColItems.cpp（f_cols／f_cols3）のうち、
/// DiffBeacon の比較結果（DirectoryEntry と各側の DirectorySideSnapshot）から値を出せる列だけを持つ。
/// 作成日時・バージョン・文字コード・改行・差異の数・バイナリ・プラグインの列は、比較モデルに値が無いため持たない（MIGRATION.md に記載）。
/// 列の識別子は保存する設定（folder-columns.json）にそのまま書くので、名前を変えない。
/// </summary>
internal enum FolderColumn
{
    Name, Folder, Result, LeftDate, MiddleDate, RightDate, Extension,
    LeftSize, MiddleSize, RightSize, LeftSizeShort, MiddleSizeShort, RightSizeShort,
    Newer, ResultAbbr, LeftAttributes, MiddleAttributes, RightAttributes,
}

/// <summary>列の見出し・説明（Japanese.po の訳）と既定の幅。Width が負の列は、既定の配置で一覧の幅に合わせて伸縮する（* の比率）。</summary>
internal sealed record FolderColumnInfo(FolderColumn Column, string Title, string Description, double Width, bool AlignRight = false);

internal static class FolderColumnCatalog
{
    private static FolderColumnInfo Name => new(FolderColumn.Name, "名前", "ファイル名またはフォルダー名です。", -1);
    private static FolderColumnInfo Folder => new(FolderColumn.Folder, "フォルダー", "サブフォルダー名です。[サブフォルダーを含める] が有効な場合に表示されます。", 90);
    private static FolderColumnInfo Result => new(FolderColumn.Result, "比較結果", "長い形式の比較結果を表示します。", -1.3);
    private static FolderColumnInfo Extension => new(FolderColumn.Extension, "拡張子", "ファイルの拡張子です。", 56);
    private static FolderColumnInfo Newer => new(FolderColumn.Newer, "日時比較", "どちらが最新の更新日時かを表示します。", 70);
    private static FolderColumnInfo ResultAbbr => new(FolderColumn.ResultAbbr, "比較結果(簡易)", "短い形式の比較結果を表示します。", 100);

    /// <summary>二者比較で選べる列（本家 f_cols の論理順）。</summary>
    internal static readonly FolderColumnInfo[] TwoWay =
    [
        Name, Folder, Result,
        new(FolderColumn.LeftDate, "左更新日時", "左側の更新日時です。", 140),
        new(FolderColumn.RightDate, "右更新日時", "右側の更新日時です。", 140),
        Extension,
        new(FolderColumn.LeftSize, "左サイズ", "左側ファイルのバイト数です。", 90, true),
        new(FolderColumn.RightSize, "右サイズ", "右側ファイルのバイト数です。", 90, true),
        new(FolderColumn.LeftSizeShort, "左サイズ(短縮)", "左側ファイルのバイト数(短縮形)です。", 80, true),
        new(FolderColumn.RightSizeShort, "右サイズ(短縮)", "右側ファイルのバイト数(短縮形)です。", 80, true),
        Newer, ResultAbbr,
        new(FolderColumn.LeftAttributes, "左属性", "左側の属性です。", 50),
        new(FolderColumn.RightAttributes, "右属性", "右側の属性です。", 50),
    ];

    /// <summary>三者比較で選べる列（本家 f_cols3 の論理順）。</summary>
    internal static readonly FolderColumnInfo[] ThreeWay =
    [
        Name, Folder, Result,
        new(FolderColumn.LeftDate, "左更新日時", "左側の更新日時です。", 140),
        new(FolderColumn.MiddleDate, "中更新日時", "中央の更新日時です。", 140),
        new(FolderColumn.RightDate, "右更新日時", "右側の更新日時です。", 140),
        Extension,
        new(FolderColumn.LeftSize, "左サイズ", "左側ファイルのバイト数です。", 90, true),
        new(FolderColumn.MiddleSize, "中サイズ", "中央ファイルのバイト数です。", 90, true),
        new(FolderColumn.RightSize, "右サイズ", "右側ファイルのバイト数です。", 90, true),
        new(FolderColumn.LeftSizeShort, "左サイズ(短縮)", "左側ファイルのバイト数(短縮形)です。", 80, true),
        new(FolderColumn.MiddleSizeShort, "中サイズ(短縮)", "中央ファイルのバイト数(短縮形)です。", 80, true),
        new(FolderColumn.RightSizeShort, "右サイズ(短縮)", "右側ファイルのバイト数(短縮形)です。", 80, true),
        Newer, ResultAbbr,
        new(FolderColumn.LeftAttributes, "左属性", "左側の属性です。", 50),
        new(FolderColumn.MiddleAttributes, "中属性", "中央の属性です。", 50),
        new(FolderColumn.RightAttributes, "右属性", "右側の属性です。", 50),
    ];

    /// <summary>既定で表示する列と順序（本家の phy_col が 0 以上の列）。追加した列は既定では表示しない。</summary>
    internal static readonly FolderColumn[] DefaultTwoWay =
        [FolderColumn.Name, FolderColumn.Folder, FolderColumn.Result, FolderColumn.LeftDate, FolderColumn.RightDate, FolderColumn.Extension];
    internal static readonly FolderColumn[] DefaultThreeWay =
        [FolderColumn.Name, FolderColumn.Folder, FolderColumn.Result, FolderColumn.LeftDate, FolderColumn.MiddleDate, FolderColumn.RightDate, FolderColumn.Extension];

    internal static FolderColumnInfo[] For(bool threeWay) => threeWay ? ThreeWay : TwoWay;
    internal static FolderColumn[] Defaults(bool threeWay) => threeWay ? DefaultThreeWay : DefaultTwoWay;
    internal static FolderColumnInfo Info(FolderColumn column, bool threeWay) => For(threeWay).First(info => info.Column == column);
    internal static bool Supports(FolderColumn column, bool threeWay) => For(threeWay).Any(info => info.Column == column);

    /// <summary>
    /// 相対幅（全列を * で持つ配置）にするときの、その列の初めの重み。固定幅の列はそのpx、伸縮する列は既定の配置で取りやすい幅にする。
    /// 重みは実際の幅（px）と同じ尺度で持つので、利用者が幅を変えた列の重みとそのまま並べられる。
    /// </summary>
    internal static double InitialWeight(FolderColumnInfo info) => info.Width < 0 ? -info.Width * 200 : info.Width;

    /// <summary>
    /// 本家の短縮サイズ（DirViewColItems.cpp の MakeShortSize）。1024 未満は "%d B"、それ以上は Windows シェルの StrFormatByteSize64 と同じく
    /// KB・MB…の単位で有効数字3桁に切り捨てて表す（例: 1024 → "1.00 KB"、12000 → "11.7 KB"、200000 → "195 KB"）。
    /// StrFormatByteSize64 は Windows の API なので、macOS でも同じ結果になるよう管理コードで書き直している。
    /// </summary>
    internal static string ShortSize(long size)
    {
        if (size < 1024) return size.ToString(CultureInfo.InvariantCulture) + " B";
        string[] units = ["KB", "MB", "GB", "TB", "PB", "EB"];
        // 2進の浮動小数点では 1.17×100 が 116.999… になり切り捨てで桁が落ちるため、10進の decimal で計算する。
        var value = size / 1024m; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        // 有効数字3桁（整数部が3桁以上なら小数なし、2桁なら1桁、1桁なら2桁）。四捨五入せず切り捨てる。
        var decimals = value >= 100 ? 0 : value >= 10 ? 1 : 2;
        var scale = decimals == 0 ? 1m : decimals == 1 ? 10m : 100m;
        var truncated = Math.Floor(value * scale) / scale;
        return truncated.ToString("F" + decimals, CultureInfo.CurrentCulture) + " " + units[unit];
    }

    /// <summary>本家の属性列（FileFlags::ToString）。読取り専用・隠し・システム・アーカイブを R・H・S・A の順に並べる。</summary>
    internal static string Attributes(FileAttributes attributes)
        => (attributes.HasFlag(FileAttributes.ReadOnly) ? "R" : "") + (attributes.HasFlag(FileAttributes.Hidden) ? "H" : "")
            + (attributes.HasFlag(FileAttributes.System) ? "S" : "") + (attributes.HasFlag(FileAttributes.Archive) ? "A" : "");

    /// <summary>属性列の並べ替えに使う値（本家 ColAttrSort と同じく、属性の値そのものを比べる）。対象の4属性だけを見る。</summary>
    internal static long AttributeKey(FileAttributes attributes)
        => (long)(attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive));
}
