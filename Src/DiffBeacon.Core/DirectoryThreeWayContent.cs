namespace DiffBeacon.Core;

public static partial class DirectoryComparer
{
    private sealed record ThreeWayContent(bool MiddleLeftEqual, bool MiddleRightEqual, bool LeftRightEqual,
        bool HasSignificantDifferences);

    private static async Task<ThreeWayContent> CompareThreeWayContentAsync(DirectorySideSnapshot? left,
        DirectorySideSnapshot? middle, DirectorySideSnapshot? right, ComparisonOptions? options,
        long maximumBytes, CancellationToken token)
    {
        var states = new[] { left, middle, right };
        long declaredBytes = 0;
        foreach (var state in states)
        {
            token.ThrowIfCancellationRequested();
            if (state is null) continue;
            if (state.Size > 256L * 1024 * 1024)
                throw new InvalidDataException("ファイル比較の上限 256 MiB を超えています。ディレクトリのハッシュ比較を使用してください。");
            declaredBytes = checked(declaredBytes + state.Size);
        }
        if (declaredBytes > maximumBytes)
            throw new InvalidDataException("三者フォルダー比較の共有読込み容量上限を超えています。");
        var bytes = new byte[3][];
        long readBytes = 0;
        for (var side = 0; side < states.Length; side++)
        {
            token.ThrowIfCancellationRequested();
            var state = states[side];
            if (state is null) { bytes[side] = []; continue; }
            await using var stream = File.OpenRead(state.Path);
            var length = stream.Length;
            if (length != state.Size)
                throw new IOException("フォルダー走査後に比較元ファイルのサイズが変わりました。");
            if (length > 256L * 1024 * 1024 || length > maximumBytes - readBytes)
                throw new InvalidDataException("三者フォルダー比較の共有読込み容量上限を超えています。");
            bytes[side] = new byte[checked((int)length)];
            await stream.ReadExactlyAsync(bytes[side], token).ConfigureAwait(false);
            var tail = new byte[1];
            if (await stream.ReadAsync(tail, token).ConfigureAwait(false) != 0)
                throw new IOException("比較元ファイルの読込み中にサイズが変わりました。");
            readBytes += length;
        }
        if (bytes.Any(data => BinaryDiffer.IsProbablyBinary(data)))
        {
            token.ThrowIfCancellationRequested();
            var ml = EqualBytes(bytes[1], bytes[0], token);
            var mr = EqualBytes(bytes[1], bytes[2], token);
            var lr = EqualBytes(bytes[0], bytes[2], token);
            return new(ml, mr, lr, !ml || !mr);
        }
        // 不在は空本文としてFullへ渡す。存在補正は呼び出し元で全体statusだけに適用する。
        var texts = new string[3];
        for (var side = 0; side < states.Length; side++)
        {
            token.ThrowIfCancellationRequested();
            texts[side] = states[side] is null ? "" : TextDocument.FromBytes(states[side]!.Path, bytes[side], new()).Text;
        }
        var result = DirectoryTextComparer.CompareThreeWay(texts[0], texts[1], texts[2], options, token);
        if (result.LineFallback)
            throw new InvalidDataException("三者Full比較の共有作業上限で厳密比較を完了できませんでした: " + result.LineFallbackReason);
        return new(result.MiddleLeftEqual, result.MiddleRightEqual, result.LeftRightEqual, result.HasSignificantDifferences);
    }

    private static bool EqualBytes(byte[] first, byte[] second, CancellationToken token)
    {
        if (first.Length != second.Length) return false;
        const int chunkSize = 64 * 1024;
        for (var offset = 0; offset < first.Length; offset += chunkSize)
        {
            token.ThrowIfCancellationRequested();
            var length = Math.Min(chunkSize, first.Length - offset);
            if (!first.AsSpan(offset, length).SequenceEqual(second.AsSpan(offset, length))) return false;
        }
        return true;
    }
}
