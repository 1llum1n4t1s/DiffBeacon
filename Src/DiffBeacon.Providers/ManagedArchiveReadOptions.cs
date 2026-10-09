using System.Text;

namespace DiffBeacon.Providers;

/// <summary>外側gzipの復号済み本文を扱う形式。</summary>
public enum GZipPayloadKind { Auto, File, Tar }

/// <summary>外側BZip2/Zの復号済み本文を扱う形式。</summary>
public enum CompressionPayloadKind { Auto, File, Tar }

/// <summary>本文の文字コードや処理上限とは独立した、コンテナーの格納名復号設定。</summary>
public sealed record ManagedArchiveReadOptions(int GZipNameCodePage = 28591,
    GZipPayloadKind GZipPayloadKind = global::DiffBeacon.Providers.GZipPayloadKind.Auto,
    CompressionPayloadKind CompressionPayloadKind = global::DiffBeacon.Providers.CompressionPayloadKind.Auto)
{
    private GZipPayloadKind _gzipPayloadKind = ValidatePayloadKind(GZipPayloadKind);
    private CompressionPayloadKind _compressionPayloadKind = ValidateCompressionPayloadKind(CompressionPayloadKind, GZipPayloadKind);
    public GZipPayloadKind GZipPayloadKind
    {
        get => _gzipPayloadKind;
        init
        {
            _ = ValidateCompressionPayloadKind(_compressionPayloadKind, value);
            _gzipPayloadKind = ValidatePayloadKind(value);
        }
    }

    public CompressionPayloadKind CompressionPayloadKind
    {
        get => _compressionPayloadKind;
        init => _compressionPayloadKind = ValidateCompressionPayloadKind(value, _gzipPayloadKind);
    }

    private static CompressionPayloadKind ValidateCompressionPayloadKind(CompressionPayloadKind value, GZipPayloadKind gzip)
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(CompressionPayloadKind), "BZip2/Z本文の形式が不正です。");
        if (value != global::DiffBeacon.Providers.CompressionPayloadKind.Auto && gzip != global::DiffBeacon.Providers.GZipPayloadKind.Auto)
            throw new ArgumentException("gzipとBZip2/Zの本文形式を同時に指定できません。");
        return value;
    }

    private static GZipPayloadKind ValidatePayloadKind(GZipPayloadKind value) => Enum.IsDefined(value)
        ? value : throw new ArgumentOutOfRangeException(nameof(GZipPayloadKind), "gzip本文の形式が不正です。");

    public static IReadOnlyList<int> SupportedGZipNameCodePages { get; } = SupportedCodePages();

    private static IReadOnlyList<int> SupportedCodePages()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Array.AsReadOnly(Encoding.GetEncodings().Select(info => info.CodePage)
            .Where(codePage => Compatible(Encoding.GetEncoding(codePage))).OrderBy(codePage => codePage == 28591 ? 0 : codePage == 65001 ? 1 : codePage == 932 ? 2 : codePage + 3).ToArray());
    }

    private static bool Compatible(Encoding encoding) => (encoding.IsSingleByte || encoding.CodePage is 65001 or 932 or 936 or 949 or 950 or 1361)
        && encoding.GetBytes("\0 /\\.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz").AsSpan()
            .SequenceEqual("\0 /\\.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8);

    internal Encoding NameEncoding()
    {
        if (!SupportedGZipNameCodePages.Contains(GZipNameCodePage))
            throw new ArgumentOutOfRangeException(nameof(GZipNameCodePage), "gzip格納名にはASCII互換の単一byte文字コード、Windows DBCS、UTF-8を指定してください。");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(GZipNameCodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}

/// <summary>構造や安全な相対パスの失敗と区別する、格納名の復号だけの失敗。</summary>
public sealed class ArchiveNameDecodingException : IOException
{
    internal ArchiveNameDecodingException() : base("gzip格納名を指定した文字コードで復号できません。格納名の文字コードを選択してください。") { }
}
