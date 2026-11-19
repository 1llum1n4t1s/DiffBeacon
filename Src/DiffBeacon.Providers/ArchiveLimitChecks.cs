using System.Diagnostics;
using System.IO.Compression;

namespace DiffBeacon.Providers;

/// <summary>
/// 壊れたアーカイブと宣言サイズが上限を超えるアーカイブを、展開の前に拒否できることを確かめる。
/// </summary>
public static class ArchiveLimitChecks
{
    /// <summary>4件を実行し、失敗があれば例外にする。</summary>
    public static void Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        AcceptsSmallZip(directory);
        RejectsDeclaredOversize(directory);
        RejectsTooManyEntries(directory);
        RejectsTruncatedZipQuickly(directory);
    }

    /// <summary>小さい ZIP は読めて、辞書上限がその読み取りのエントリ上限まで狭まる。</summary>
    private static void AcceptsSmallZip(string directory)
    {
        var path = WriteZip(Path.Combine(directory, "small.zip"), "a.txt", "hi");
        var limits = new ManagedArchiveLimits(MaximumEntryBytes: 4096, MaximumDecodedBytes: 4096, MaximumInputBytes: 4096);
        var manifest = new ManagedArchive(limits).ReadManifest(path);
        if (manifest.Entries.Count != 1 || manifest.Entries[0].Size != 2)
            throw new InvalidOperationException("小さい ZIP の読取り結果が違います。");
        if (DictionarySizeLimitsAtMost(4096) is false)
            throw new InvalidOperationException("辞書上限がエントリ上限まで狭まっていません。");
    }

    /// <summary>中央ディレクトリの展開サイズだけを大きくした ZIP は、展開せず上限で拒否する。</summary>
    private static void RejectsDeclaredOversize(string directory)
    {
        var path = WriteZip(Path.Combine(directory, "oversize.zip"), "a.txt", "hello");
        InflateDeclaredSize(path, 5, 50_000_000);
        var limits = new ManagedArchiveLimits(MaximumEntryBytes: 1024, MaximumDecodedBytes: 1024, MaximumInputBytes: 1024 * 1024);
        var watch = Stopwatch.StartNew();
        try
        {
            _ = new ManagedArchive(limits).ReadManifest(path);
            throw new InvalidOperationException("宣言サイズが上限を超える ZIP を受理しました。");
        }
        catch (InvalidDataException)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("上限拒否に5秒を超えました。");
        }
    }

    /// <summary>エントリ数が上限を超える ZIP は列挙の途中で拒否する。</summary>
    private static void RejectsTooManyEntries(string directory)
    {
        var path = Path.Combine(directory, "many.zip");
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            for (var index = 0; index < 3; index++)
            {
                var entry = zip.CreateEntry("e" + index + ".txt", CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("x");
            }
        }
        var limits = new ManagedArchiveLimits(MaximumEntries: 2, MaximumEntryBytes: 4096, MaximumDecodedBytes: 4096);
        try
        {
            _ = new ManagedArchive(limits).ReadManifest(path);
            throw new InvalidOperationException("エントリ数の上限を超える ZIP を受理しました。");
        }
        catch (InvalidDataException error) when (error.Message.Contains("エントリ数", StringComparison.Ordinal))
        {
        }
    }

    /// <summary>途中で切れた ZIP は待たずに拒否する。</summary>
    private static void RejectsTruncatedZipQuickly(string directory)
    {
        var path = Path.Combine(directory, "truncated.zip");
        File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0, 0, 0, 0, 0]);
        var watch = Stopwatch.StartNew();
        try
        {
            _ = new ManagedArchive(new ManagedArchiveLimits(MaximumEntryBytes: 4096, MaximumDecodedBytes: 4096)).ReadManifest(path);
            throw new InvalidOperationException("途中で切れた ZIP を受理しました。");
        }
        catch (Exception error) when (!error.Message.Contains("受理", StringComparison.Ordinal))
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("壊れた ZIP の拒否に5秒を超えました。", error);
        }
        if (watch.Elapsed > TimeSpan.FromSeconds(5))
            throw new InvalidOperationException("壊れた ZIP の拒否に5秒を超えました。");
    }

    /// <summary>ライブラリの辞書上限が cap 以下か。</summary>
    private static bool DictionarySizeLimitsAtMost(long cap) =>
        SharpCompress.Common.DictionarySizeLimits.Lzma <= cap
        && SharpCompress.Common.DictionarySizeLimits.Lzma2 <= cap
        && SharpCompress.Common.DictionarySizeLimits.Rar5 <= cap
        && SharpCompress.Common.DictionarySizeLimits.Ppmd <= cap;

    /// <summary>無圧縮の ZIP を1エントリで書く。</summary>
    private static string WriteZip(string path, string name, string content)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
        return path;
    }

    /// <summary>無圧縮サイズのフィールドを、展開せずに大きい宣言へ書き換える。</summary>
    private static void InflateDeclaredSize(string path, int actual, int declared)
    {
        var bytes = File.ReadAllBytes(path);
        var from = BitConverter.GetBytes(actual);
        var to = BitConverter.GetBytes(declared);
        var replaced = 0;
        for (var index = 0; index + 4 <= bytes.Length; index++)
        {
            if (bytes[index] != from[0] || bytes[index + 1] != from[1] || bytes[index + 2] != from[2] || bytes[index + 3] != from[3]) continue;
            bytes[index] = to[0]; bytes[index + 1] = to[1]; bytes[index + 2] = to[2]; bytes[index + 3] = to[3];
            replaced++;
            index += 3;
        }
        if (replaced < 2) throw new InvalidOperationException("ZIP のサイズフィールドを書き換えられませんでした: " + replaced);
        File.WriteAllBytes(path, bytes);
    }
}
