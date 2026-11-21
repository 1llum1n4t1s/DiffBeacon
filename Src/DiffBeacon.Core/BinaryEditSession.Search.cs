namespace DiffBeacon.Core;

public sealed partial class BinaryEditSession
{
    /// <summary>caretより先／前に開始する最も近いbyte一致を返す。不一致は-1、折畳みはASCIIだけ。</summary>
    /// <remarks>同期read操作。呼出中にsessionまたはpatternを変更しない。wrapと入力文字の変換は行わない。</remarks>
    public int FindBytes(int side, ReadOnlySpan<byte> pattern, int caret, bool backwards = false,
        bool matchCase = true, CancellationToken token = default)
    {
        ValidateSide(side);
        token.ThrowIfCancellationRequested();
        return BinaryByteSearch.Find(_bytes[side], pattern, caret, backwards, matchCase, token);
    }
}

/// <summary>不変snapshotなど、呼出中に変更されないbytesを検索する。</summary>
public static class BinaryByteSearch
{
    /// <summary>caretを除いた指定方向の最も近い一致の開始位置を返す。不一致は-1。</summary>
    /// <remarks>入力を複製・変更しない同期read操作。呼出中にbytesまたはpatternを変更しない。</remarks>
    public static int Find(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> pattern, int caret,
        bool backwards = false, bool matchCase = true, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > BinaryEditSession.MaximumFileBytes)
            throw new InvalidDataException("バイナリ検索の入力は16 MiBまでです。");
        if (caret < 0 || caret > bytes.Length) throw new ArgumentOutOfRangeException(nameof(caret));
        if (pattern.IsEmpty) throw new ArgumentException("検索するbytesを指定してください。", nameof(pattern));
        if (pattern.Length > BinaryEditSession.MaximumFileBytes) throw new InvalidDataException("バイナリ検索は16 MiBまでです。");

        var lastStart = bytes.Length - pattern.Length;
        if (lastStart < 0 || (backwards ? caret == 0 : caret >= lastStart))
        {
            token.ThrowIfCancellationRequested();
            return -1;
        }

        // patternのcloneは作らない。prefixは16 MiBのpatternに対し最大64 MiB。
        // 後方は逆patternと逆scanで最も近い開始位置を直接取得する。
        token.ThrowIfCancellationRequested();
        var prefix = new int[pattern.Length];
        var work = 0;
        for (var at = 1; at < pattern.Length; at++)
        {
            CheckSearchCancellation(ref work, token);
            var value = SearchPatternByte(pattern, at, backwards, matchCase);
            var matched = prefix[at - 1];
            while (matched > 0 && value != SearchPatternByte(pattern, matched, backwards, matchCase))
            {
                CheckSearchCancellation(ref work, token);
                matched = prefix[matched - 1];
            }
            if (value == SearchPatternByte(pattern, matched, backwards, matchCase)) matched++;
            prefix[at] = matched;
        }

        token.ThrowIfCancellationRequested();
        var start = backwards ? Math.Min(caret - 1, lastStart) + pattern.Length - 1 : caret + 1;
        var step = backwards ? -1 : 1;
        var length = 0;
        for (var at = start; backwards ? at >= 0 : at < bytes.Length; at += step)
        {
            CheckSearchCancellation(ref work, token);
            var value = FoldSearchByte(bytes[at], matchCase);
            while (length > 0 && value != SearchPatternByte(pattern, length, backwards, matchCase))
            {
                CheckSearchCancellation(ref work, token);
                length = prefix[length - 1];
            }
            if (value == SearchPatternByte(pattern, length, backwards, matchCase)) length++;
            if (length == pattern.Length)
            {
                token.ThrowIfCancellationRequested();
                return backwards ? at : at - pattern.Length + 1;
            }
        }
        token.ThrowIfCancellationRequested();
        return -1;
    }

    private static byte SearchPatternByte(ReadOnlySpan<byte> pattern, int index, bool backwards, bool matchCase)
        => FoldSearchByte(pattern[backwards ? pattern.Length - index - 1 : index], matchCase);

    private static byte FoldSearchByte(byte value, bool matchCase)
        => !matchCase && value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + ('a' - 'A')) : value;

    private static void CheckSearchCancellation(ref int work, CancellationToken token)
    {
        // outer反復だけでなく失敗リンクの走査も数え、長い反復patternでも取消に戻る。
        if (++work == 4096) { work = 0; token.ThrowIfCancellationRequested(); }
    }
}