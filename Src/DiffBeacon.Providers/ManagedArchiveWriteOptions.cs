using System.Text;

namespace DiffBeacon.Providers;

/// <summary>読込み設定から継承しない、gzip出力の格納名文字コード。</summary>
public sealed record ManagedArchiveWriteOptions(int GZipNameCodePage = 28591)
{
    public static IReadOnlyList<int> SupportedGZipNameCodePages => ManagedArchiveReadOptions.SupportedGZipNameCodePages;

    // 読込みと同じASCII互換判定を再利用し、replacementを許可しない。
    internal Encoding NameEncoding() => new ManagedArchiveReadOptions(GZipNameCodePage).NameEncoding();
}