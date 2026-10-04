namespace DiffBeacon.Core;

public sealed partial class BinaryEditSession
{
    // 公開前に本文と共有履歴の容量を検査し、非同期完了時は全側の変更も検出する。
    public sealed class Candidate
    {
        internal readonly BinaryEditSession owner;
        internal readonly int side;
        internal readonly long mutation;
        internal readonly byte[] bytes;
        internal readonly Change? change;
        internal bool consumed;
        internal Candidate(BinaryEditSession owner, int side, long mutation, byte[] bytes, Change? change)
        { this.owner = owner; this.side = side; this.mutation = mutation; this.bytes = bytes; this.change = change; }
    }
    private Candidate PrepareCandidate(int side, byte[] bytes)
    {
        Writable(side);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("バイナリ編集の上限は各16 MiBです。");
        return new(this, side, _mutation, bytes, DescribeChange(side, bytes));
    }
    public bool CommitPrepared(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!ReferenceEquals(candidate.owner, this)) throw new InvalidOperationException("編集元が異なります。");
        Writable(candidate.side);
        if (candidate.consumed || candidate.mutation != _mutation) throw new InvalidOperationException("編集中にバイナリまたは履歴が変更されました。");
        candidate.consumed = true;
        if (candidate.change is not { } change) return false;
        for (var index = _history.Count - 1; index >= _cursor; index--) { _historyBytes -= Cost(_history[index]); _history.RemoveAt(index); }
        _history.Add(change); _cursor++; _historyBytes += Cost(change); SetBytes(candidate.side, candidate.bytes);
        return true;
    }
    public Candidate PrepareReplaceRange(int side, int start, int removeCount, ReadOnlySpan<byte> replacement)
    {
        Writable(side); var current = _bytes[side];
        if (start < 0 || start > current.Length || removeCount < 0 || removeCount > current.Length - start) throw new ArgumentOutOfRangeException(nameof(start));
        var length = (long)current.Length - removeCount + replacement.Length;
        if (length > MaximumFileBytes) throw new InvalidDataException("バイナリ編集の上限は各16 MiBです。");
        var result = new byte[(int)length]; current.AsSpan(0, start).CopyTo(result); replacement.CopyTo(result.AsSpan(start));
        current.AsSpan(start + removeCount).CopyTo(result.AsSpan(start + replacement.Length));
        return PrepareCandidate(side, result);
    }
    public void ReplaceRange(int side, int start, int removeCount, ReadOnlySpan<byte> replacement)
        => CommitPrepared(PrepareReplaceRange(side, start, removeCount, replacement));

    public Candidate PreparePaste(int side, int start, int selectionLength, ReadOnlySpan<byte> payload, bool insert, long repeat = 1, long skip = 0)
    {
        Writable(side); var current = _bytes[side];
        var resultLength = ValidatePaste(current.Length, start, selectionLength, payload.Length, insert, repeat, skip);
        insert |= selectionLength != 0;
        var result = new byte[(int)resultLength];
        if (!insert)
        {
            current.CopyTo(result, 0);
            for (long step = 0; step < repeat; step++) payload.CopyTo(result.AsSpan((int)(start + step * (payload.Length + skip))));
        }
        else
        {
            current.AsSpan(0, start).CopyTo(result); var read = start + selectionLength; var write = start;
            for (long step = 0; step < repeat; step++)
            {
                payload.CopyTo(result.AsSpan(write)); write += payload.Length;
                if (step + 1 == repeat) break;
                current.AsSpan(read, (int)skip).CopyTo(result.AsSpan(write)); read += (int)skip; write += (int)skip;
            }
            current.AsSpan(read).CopyTo(result.AsSpan(write));
        }
        return PrepareCandidate(side, result);
    }
    public static int ValidatePaste(int length, int start, int selectionLength, int payloadLength, bool insert, long repeat, long skip)
    {
        if (length < 0 || length > MaximumFileBytes || start < 0 || start > length || selectionLength < 0 || selectionLength > length - start) throw new ArgumentOutOfRangeException(nameof(start));
        if (payloadLength <= 0) throw new InvalidDataException("貼り付けるバイトがありません。");
        if (payloadLength > MaximumFileBytes || repeat < 1 || repeat > MaximumFileBytes || skip < 0 || skip > MaximumFileBytes) throw new ArgumentOutOfRangeException(nameof(repeat));
        insert |= selectionLength != 0;
        var added = repeat * payloadLength; var suffix = length - start - selectionLength;
        var resultLength = insert ? length - selectionLength + added : length;
        if (resultLength > MaximumFileBytes || added > MaximumFileBytes) throw new InvalidDataException("貼り付けは各16 MiBまでです。");
        if (insert ? (repeat - 1) * skip > suffix : repeat * (payloadLength + skip) > suffix) throw new InvalidDataException("貼り付け回数とスキップが末尾を超えています。");
        return (int)resultLength;
    }
}
