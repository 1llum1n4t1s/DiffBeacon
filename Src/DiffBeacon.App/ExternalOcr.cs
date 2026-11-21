using System.Text.Json;

namespace DiffBeacon.App;

/// <summary>
/// 画像からの文字認識。Windows.Media.Ocr と Apple Vision は、単一の net11.0 Native AOT バイナリから
/// WinRT 射影にも Apple のフレームワークにもリンクできない。代わりに、利用者が指定した実行ファイルへ
/// 画像パスを渡し、1 行の JSON（{"text":"..."}）を受け取る。
/// </summary>
internal static class ExternalOcr
{
    /// <summary>このビルドでプロセス内 OCR が無い理由。</summary>
    internal static string Availability { get; } = OperatingSystem.IsWindows()
        ? "Windows.Media.Ocr は WinRT 射影がこの net11.0 バイナリに無いため、外部 OCR コマンドを使います。"
        : OperatingSystem.IsMacOS()
            ? "Apple Vision はこの net11.0 バイナリにリンクできないため、外部 OCR コマンドを使います。"
            : "この OS に組み込み OCR は無く、外部 OCR コマンドを使います。";

    /// <summary>外部コマンドの標準出力（1 行の JSON）から本文を読む。text が文字列でなければ拒否する。</summary>
    internal static string ReadResponse(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        if (!document.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("OCR 応答の text は文字列である必要があります。");
        return text.GetString() ?? "";
    }
}
