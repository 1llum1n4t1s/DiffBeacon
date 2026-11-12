namespace DiffBeacon.Core;

public enum BinaryReplacementMode { Selection, Forward, Backward }

public sealed class BinaryReplacementResult
{
    public ReadOnlyMemory<byte> Bytes { get; }
    public int ReplacementCount { get; }
    public int SelectionStart { get; }
    public int SelectionLength { get; }
    public int Caret { get; }

    internal BinaryReplacementResult(byte[] bytes, int count, int start, int length, int caret)
        => (Bytes, ReplacementCount, SelectionStart, SelectionLength, Caret) = (bytes, count, start, length, caret);
}

/// <summary>入力と履歴を変更せず、方向指定のバイト置換候補を作る。</summary>
public static class BinaryByteReplacement
{
    public const long MaximumWorkUnits = 256L * 1024 * 1024;
    private const int MaximumLength = BinaryEditSession.MaximumFileBytes;

    /// <remarks>呼出中に入力spanを変更しない。大文字小文字の折畳みは検索と同じASCIIだけ。</remarks>
    public static BinaryReplacementResult Transform(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> pattern,
        ReadOnlySpan<byte> replacement, int caret, int selectionStart, int selectionLength,
        BinaryReplacementMode mode, bool matchCase = true, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (mode is not (BinaryReplacementMode.Selection or BinaryReplacementMode.Forward or BinaryReplacementMode.Backward))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (bytes.Length > MaximumLength || pattern.Length > MaximumLength || replacement.Length > MaximumLength)
            throw new InvalidDataException("バイナリ置換は各16 MiBまでです。");
        if (caret < 0 || caret > bytes.Length) throw new ArgumentOutOfRangeException(nameof(caret));
        if (selectionStart < 0 || selectionStart > bytes.Length) throw new ArgumentOutOfRangeException(nameof(selectionStart));
        if (selectionLength < 0 || selectionLength > bytes.Length - selectionStart)
            throw new ArgumentOutOfRangeException(nameof(selectionLength));
        if (mode == BinaryReplacementMode.Selection && selectionLength == 0)
            throw new InvalidOperationException("置換する範囲を選択してください。");
        if (mode != BinaryReplacementMode.Selection && pattern.IsEmpty)
            throw new ArgumentException("検索するbytesを指定してください。", nameof(pattern));

        var budget = new WorkBudget(token);
        if (mode != BinaryReplacementMode.Selection && Equal(pattern, replacement, budget))
            throw new InvalidOperationException("検索と置換のbytesが同じです。");
        long documentLength = bytes.Length;
        if (selectionLength != 0) ChangeLength(ref documentLength, replacement.Length - selectionLength);
        if (mode == BinaryReplacementMode.Selection)
        {
            var output = new byte[(int)documentLength];
            Copy(bytes[..selectionStart], output, budget);
            Copy(replacement, output.AsSpan(selectionStart), budget);
            Copy(bytes[(selectionStart + selectionLength)..], output.AsSpan(selectionStart + replacement.Length), budget);
            budget.Complete();
            return new(output, 1, selectionStart, replacement.Length, selectionStart);
        }

        var backwards = mode == BinaryReplacementMode.Backward;
        var prefix = BuildPrefix(pattern, backwards, matchCase, budget);
        return backwards
            ? Backward(bytes, pattern, replacement, caret, selectionStart, selectionLength, matchCase, prefix, documentLength, budget)
            : Forward(bytes, pattern, replacement, caret, selectionStart, selectionLength, matchCase, prefix, documentLength, budget);
    }

    private static BinaryReplacementResult Forward(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> pattern,
        ReadOnlySpan<byte> replacement, int caret, int selectionStart, int selectionLength, bool matchCase,
        int[] prefix, long documentLength, WorkBudget budget)
    {
        var output = new ByteStack(false, budget);
        var count = selectionLength == 0 ? 0 : 1;
        var lastStart = selectionStart;
        var start = selectionLength == 0 ? Math.Min((long)caret + 1, bytes.Length) : selectionStart + selectionLength;
        if (selectionLength != 0)
        {
            output.Append(bytes[..selectionStart]);
            output.Append(replacement);
            // 空置換後は選択が解除され、旧仕様の次の検索はcaretの次byteから始まる。
            if (replacement.IsEmpty && start < bytes.Length) output.Push(bytes[(int)start++], 0);
        }
        else output.Append(bytes[..(int)start]);

        var pending = (int)start;
        var matched = 0;
        for (var at = pending; at < bytes.Length; at++)
        {
            matched = Advance(pattern, prefix, matched, bytes[at], false, matchCase, budget);
            if (matched != pattern.Length) continue;
            var found = at - pattern.Length + 1;
            ChangeLength(ref documentLength, replacement.Length - pattern.Length);
            output.Append(bytes.Slice(pending, found - pending));
            lastStart = output.Length;
            output.Append(replacement);
            count++;
            pending = at + 1;
            if (replacement.IsEmpty && pending < bytes.Length)
            {
                output.Push(bytes[pending++], 0);
                at++;
            }
            // 置換内と一致範囲の重なりは前方で再検索しない。
            matched = 0;
        }
        output.Append(bytes[pending..]);
        var result = output.Finish(false);
        budget.Complete();
        return count == 0 ? new(result, 0, selectionStart, selectionLength, caret)
            : new(result, count, lastStart, replacement.Length, lastStart);
    }

    private static BinaryReplacementResult Backward(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> pattern,
        ReadOnlySpan<byte> replacement, int caret, int selectionStart, int selectionLength, bool matchCase,
        int[] prefix, long documentLength, WorkBudget budget)
    {
        var output = new ByteStack(true, budget);
        var count = selectionLength == 0 ? 0 : 1;
        var lastStart = selectionStart;
        var boundary = selectionLength == 0 ? caret : selectionStart;
        var suffix = selectionLength == 0 ? caret : selectionStart + selectionLength;
        // suffixと置換bytesは状態だけ記録する。次の元prefixbyteとの境界一致は検出する。
        for (var at = bytes.Length - 1; at >= suffix; at--)
            PushWithoutDetection(output, pattern, prefix, bytes[at], matchCase, budget);
        if (selectionLength != 0)
            for (var at = replacement.Length - 1; at >= 0; at--)
                PushWithoutDetection(output, pattern, prefix, replacement[at], matchCase, budget);
        for (var at = boundary - 1; at >= 0; at--)
        {
            var state = Advance(pattern, prefix, output.State, bytes[at], true, matchCase, budget);
            output.Push(bytes[at], state);
            if (state != pattern.Length) continue;
            ChangeLength(ref documentLength, replacement.Length - pattern.Length);
            output.Pop(pattern.Length);
            for (var index = replacement.Length - 1; index >= 0; index--)
                PushWithoutDetection(output, pattern, prefix, replacement[index], matchCase, budget);
            lastStart = at;
            count++;
        }
        var result = output.Finish(true);
        budget.Complete();
        return count == 0 ? new(result, 0, selectionStart, selectionLength, caret)
            : new(result, count, lastStart, replacement.Length, lastStart);

    }

    private static void PushWithoutDetection(ByteStack output, ReadOnlySpan<byte> pattern, int[] prefix,
        byte value, bool matchCase, WorkBudget budget)
        => output.Push(value, Advance(pattern, prefix, output.State, value, true, matchCase, budget));

    private static int[] BuildPrefix(ReadOnlySpan<byte> pattern, bool backwards, bool matchCase, WorkBudget budget)
    {
        budget.Add(pattern.Length);
        var prefix = new int[pattern.Length];
        for (var at = 1; at < pattern.Length; at++)
        {
            var state = prefix[at - 1];
            var value = PatternByte(pattern, at, backwards, matchCase);
            while (state > 0 && !Same(value, PatternByte(pattern, state, backwards, matchCase), budget)) state = prefix[state - 1];
            if (Same(value, PatternByte(pattern, state, backwards, matchCase), budget)) state++;
            prefix[at] = state;
        }
        return prefix;
    }

    private static int Advance(ReadOnlySpan<byte> pattern, int[] prefix, int state, byte value,
        bool backwards, bool matchCase, WorkBudget budget)
    {
        budget.Add(1);
        value = Fold(value, matchCase);
        if (state == pattern.Length) state = prefix[state - 1];
        while (state > 0 && !Same(value, PatternByte(pattern, state, backwards, matchCase), budget)) state = prefix[state - 1];
        if (Same(value, PatternByte(pattern, state, backwards, matchCase), budget)) state++;
        return state;
    }

    private static byte PatternByte(ReadOnlySpan<byte> pattern, int index, bool backwards, bool matchCase)
        => Fold(pattern[backwards ? pattern.Length - index - 1 : index], matchCase);
    private static byte Fold(byte value, bool matchCase)
        => !matchCase && value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + ('a' - 'A')) : value;
    private static bool Same(byte left, byte right, WorkBudget budget) { budget.Add(1); return left == right; }
    private static bool Equal(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, WorkBudget budget)
    {
        if (left.Length != right.Length) return false;
        for (var at = 0; at < left.Length; at++) if (!Same(left[at], right[at], budget)) return false;
        return true;
    }
    private static void ChangeLength(ref long length, int delta)
    {
        length += delta;
        if (length < 0 || length > MaximumLength) throw new InvalidDataException("バイナリ置換の結果は各16 MiBまでです。");
    }
    private static void Copy(ReadOnlySpan<byte> source, Span<byte> target, WorkBudget budget)
    {
        for (var at = 0; at < source.Length; at += 4096)
        {
            var length = Math.Min(4096, source.Length - at);
            budget.Add(length);
            source.Slice(at, length).CopyTo(target.Slice(at, length));
        }
    }

    private sealed class WorkBudget(CancellationToken token)
    {
        private long _work;
        private long _nextCheck = 4096;
        public void Add(int units)
        {
            _work += units;
            if (_work > MaximumWorkUnits) throw new InvalidDataException("バイナリ置換の作業量上限を超えました。");
            if (_work < _nextCheck) return;
            token.ThrowIfCancellationRequested();
            _nextCheck = _work + 4096;
        }
        public void Complete() => token.ThrowIfCancellationRequested();
    }

    // 逆走査ではbyteごとのKMP状態を保持し、置換でpopした後の状態を定数時間で復元する。
    private sealed class ByteStack(bool states, WorkBudget budget)
    {
        private byte[] _bytes = [];
        private int[] _states = [];
        public int Length { get; private set; }
        public int State => Length == 0 ? 0 : _states[Length - 1];
        public void Push(byte value, int state)
        {
            Ensure(Length + 1);
            budget.Add(1);
            _bytes[Length] = value;
            if (states) _states[Length] = state;
            Length++;
        }
        public void Append(ReadOnlySpan<byte> bytes)
        {
            Ensure(Length + bytes.Length);
            Copy(bytes, _bytes.AsSpan(Length), budget);
            Length += bytes.Length;
        }
        public void Pop(int length) { budget.Add(length); Length -= length; }
        private void Ensure(int required)
        {
            if (required > MaximumLength) throw new InvalidDataException("バイナリ置換の結果は各16 MiBまでです。");
            if (required <= _bytes.Length) return;
            var capacity = (int)Math.Min(MaximumLength, Math.Max((long)required, Math.Max(256L, _bytes.Length * 2L)));
            budget.Add(capacity);
            var next = new byte[capacity];
            Copy(_bytes.AsSpan(0, Length), next, budget);
            _bytes = next;
            if (!states) return;
            budget.Add(capacity);
            var nextStates = new int[capacity];
            for (var at = 0; at < Length; at += 4096)
            {
                var count = Math.Min(4096, Length - at);
                budget.Add(count);
                _states.AsSpan(at, count).CopyTo(nextStates.AsSpan(at, count));
            }
            _states = nextStates;
        }
        public byte[] Finish(bool reverse)
        {
            budget.Add(Length);
            var result = new byte[Length];
            if (!reverse) Copy(_bytes.AsSpan(0, Length), result, budget);
            else for (var at = 0; at < Length; at++) { budget.Add(1); result[at] = _bytes[Length - at - 1]; }
            return result;
        }
    }
}
