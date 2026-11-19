using System.Text;

namespace DiffBeacon.Core;

/// <summary>一回のフィルタ評価で共有する、内容と再帰の読取り上限。</summary>
internal sealed class FilterReadBudget
{
    /// <summary>内容として読む残りのバイト。ファイルあたりの上限より先に尽きたら、それ以上は読まない。</summary>
    internal int RemainingContentBytes { get; private set; } = 1024 * 1024;
    /// <summary>再帰で触れる残りの項目数。</summary>
    internal int RemainingEntries { get; private set; } = 20_000;

    internal int TakeContent(int requested)
    {
        var amount = Math.Min(requested, RemainingContentBytes);
        RemainingContentBytes -= amount;
        return amount;
    }

    internal bool TakeEntry()
    {
        if (RemainingEntries <= 0) return false;
        RemainingEntries--;
        return true;
    }
}

/// <summary>一つの項目について、遅延で読んだ内容と木の合計。</summary>
internal sealed class FilterFileCache
{
    internal bool ContentLoaded { get; set; }
    internal string Content { get; set; } = "";
    internal string Encoding { get; set; } = "";
    internal int CodePage { get; set; }
    internal bool HasBom { get; set; }
    internal string Eol { get; set; } = "";
    internal bool TreeLoaded { get; set; }
    internal long Files { get; set; }
    internal long Items { get; set; }
    internal long Bytes { get; set; }
}

/// <summary>
/// フィルタが content / encoding / 再帰合計を参照したときだけ、パスから読む。
/// 1ファイル 256 KiB、評価全体で 1 MiB と 20,000 項目まで。超えた分は読まず、読めた範囲だけを返す。
/// </summary>
/// <summary>遅延読取りに必要な、その項目の場所と共有予算。</summary>
internal readonly record struct FilterFactSource(string? FullPath, bool Directory, long Size, DateTime Modified, FilterReadBudget ReadBudget, FilterFileCache Cache);

internal static class FilterFileFacts
{
    private const int PerFileCap = 256 * 1024;

    internal static string Content(FilterFactSource context) => Load(context).Content;
    internal static string EncodingName(FilterFactSource context) => Load(context).Encoding;
    internal static int CodePage(FilterFactSource context) => Load(context).CodePage;
    internal static bool HasBom(FilterFactSource context) => Load(context).HasBom;
    internal static string Eol(FilterFactSource context) => Load(context).Eol;
    internal static DateTime CreationTime(FilterFactSource context)
    {
        var path = context.FullPath;
        if (string.IsNullOrEmpty(path)) return context.Modified;
        try { return File.GetCreationTimeUtc(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return context.Modified; }
    }

    internal static long Immediate(FilterFactSource context, bool filesOnly)
    {
        if (!context.Directory || context.FullPath is null || !Directory.Exists(context.FullPath)) return context.Directory ? 0 : 1;
        long count = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(context.FullPath))
        {
            if (!context.ReadBudget.TakeEntry()) break;
            if (!filesOnly || File.Exists(entry)) count++;
        }
        return count;
    }

    internal static long ImmediateBytes(FilterFactSource context)
    {
        if (context.FullPath is null || !Directory.Exists(context.FullPath)) return 0;
        long total = 0;
        foreach (var entry in Directory.EnumerateFiles(context.FullPath))
        {
            if (!context.ReadBudget.TakeEntry()) break;
            try { total += new FileInfo(entry).Length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    internal static (long Files, long Items, long Bytes) Tree(FilterFactSource context)
    {
        var cache = context.Cache;
        if (cache.TreeLoaded) return (cache.Files, cache.Items, cache.Bytes);
        cache.TreeLoaded = true;
        var path = context.FullPath;
        if (path is null) return (0, 0, 0);
        if (File.Exists(path) && !Directory.Exists(path))
        {
            cache.Files = 1; cache.Items = 1; cache.Bytes = context.Size;
            return (1, 1, context.Size);
        }
        if (!Directory.Exists(path)) return (0, 0, 0);
        var pending = new Queue<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        pending.Enqueue(path);
        seen.Add(path);
        while (pending.TryDequeue(out var directory))
        {
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(directory); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var child in children)
            {
                if (!context.ReadBudget.TakeEntry()) { pending.Clear(); break; }
                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0 || !seen.Add(child)) continue;
                cache.Items++;
                if ((attributes & FileAttributes.Directory) != 0) pending.Enqueue(child);
                else
                {
                    cache.Files++;
                    try { cache.Bytes += new FileInfo(child).Length; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        return (cache.Files, cache.Items, cache.Bytes);
    }

    private static FilterFileCache Load(FilterFactSource context)
    {
        var cache = context.Cache;
        if (cache.ContentLoaded) return cache;
        cache.ContentLoaded = true;
        var path = context.FullPath;
        if (path is null || context.Directory || !File.Exists(path)) return cache;
        var cap = context.ReadBudget.TakeContent(PerFileCap);
        if (cap <= 0) return cache;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(cap, stream.Length);
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            Decode(bytes, cache);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return cache;
    }

    private static void Decode(byte[] bytes, FilterFileCache cache)
    {
        var offset = 0;
        Encoding encoding;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        { encoding = new UTF8Encoding(true); offset = 3; cache.HasBom = true; cache.CodePage = 65001; cache.Encoding = "UTF-8"; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        { encoding = Encoding.Unicode; offset = 2; cache.HasBom = true; cache.CodePage = 1200; cache.Encoding = "UTF-16 LE"; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        { encoding = Encoding.BigEndianUnicode; offset = 2; cache.HasBom = true; cache.CodePage = 1201; cache.Encoding = "UTF-16 BE"; }
        else
        {
            encoding = new UTF8Encoding(false, false);
            cache.CodePage = 65001;
            cache.Encoding = "UTF-8";
        }
        cache.Content = encoding.GetString(bytes, offset, bytes.Length - offset);
        var withoutCrlf = cache.Content.Replace("\r\n", "", StringComparison.Ordinal);
        var cr = cache.Content.Contains("\r\n", StringComparison.Ordinal);
        var lf = withoutCrlf.Contains('\n');
        var bareCr = withoutCrlf.Contains('\r');
        cache.Eol = (cr, lf, bareCr) switch
        {
            (true, false, false) => "CRLF",
            (false, true, false) => "LF",
            (false, false, true) => "CR",
            (false, false, false) => "",
            _ => "mixed",
        };
    }
}
