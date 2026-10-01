namespace DiffBeacon.Core;

// 原文と比較キーを分離し、終端情報が本文の制御文字と衝突しないよう構造化する。
internal readonly record struct GnuLineKey(string Content, string Ending, bool MissingFinalNewline);
internal sealed record GnuLineMatches(IReadOnlyList<(int A, int B)> Pairs, int WorkUsed,
    bool Fallback, string? FallbackReason);

internal static class GnuLineMatcher
{
    internal static GnuLineMatches Match(GnuLineKey[] left, GnuLineKey[] right,
        IReadOnlyList<TextLine> sourceLeft, IReadOnlyList<TextLine> sourceRight,
        int[] activeLeft, int[] activeRight, ComparisonOptions options, CancellationToken token)
    {
        var budget = new Budget(Math.Clamp(options.MaxFallbackComparisons, 0, GnuLineDiffer.MaximumWork), token);
        var pairs = new List<(int A, int B)>();
        if (left.Length == right.Length && FullyEqual())
        {
            for (var index = 0; index < left.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                pairs.Add((index, index));
            }
            return new(pairs.AsReadOnly(), 0, false, null);
        }
        var prefix = 0;
        var endLeft = left.Length;
        var endRight = right.Length;
        // io.cのhorizon=0と同様に原文一致の完全行だけを本文の外へ出す。
        // 線形の境界走査は従来どおり予算0でも行い、取消を文字単位で確認する。
        while (prefix < endLeft && prefix < endRight && BoundaryEqual(prefix, prefix))
        {
            pairs.Add((prefix, prefix));
            prefix++;
        }
        while (endLeft > prefix && endRight > prefix && BoundaryEqual(endLeft - 1, endRight - 1))
        {
            endLeft--;
            endRight--;
        }
        var middle = new List<(int A, int B)>();
        var fallback = false;
        string? reason = null;
        try
        {
            var countLeft = endLeft - prefix;
            var countRight = endRight - prefix;
            if (countLeft != 0 || countRight != 0)
            {
                if (countLeft > 262_144 || countRight > 262_144)
                    throw new LimitException("row-limit:gnu-input");
                budget.Spend((long)countLeft + countRight);
                var a = new int[countLeft];
                var b = new int[countRight];
                var classes = new Dictionary<GnuLineKey, int>(new KeyComparer(budget));
                Classify(left, a);
                Classify(right, b);
                var script = GnuLineDiffer.Compare(a, b, classes.Count + 1, budget.Remaining, token);
                budget.Spend(script.WorkUsed);
                fallback = script.Fallback;
                reason = script.FallbackReason;
                if (!fallback)
                {
                    var cursorLeft = 0;
                    var cursorRight = 0;
                    foreach (var change in script.Changes)
                    {
                        token.ThrowIfCancellationRequested();
                        AddEqual(change.LeftStart, change.RightStart);
                        cursorLeft += change.LeftCount;
                        cursorRight += change.RightCount;
                    }
                    AddEqual(a.Length, b.Length);

                    void AddEqual(int stopLeft, int stopRight)
                    {
                        if (stopLeft < cursorLeft || stopRight < cursorRight ||
                            stopLeft > a.Length || stopRight > b.Length ||
                            stopLeft - cursorLeft != stopRight - cursorRight)
                            throw new InvalidOperationException("GNU scriptの一致区間が不正です。");
                        while (cursorLeft < stopLeft)
                        {
                            token.ThrowIfCancellationRequested();
                            if (a[cursorLeft] != b[cursorRight])
                                throw new InvalidOperationException("GNU scriptの一致行が異なります。");
                            middle.Add((prefix + cursorLeft++, prefix + cursorRight++));
                        }
                    }
                }

                void Classify(GnuLineKey[] keys, int[] equivalents)
                {
                    for (var index = 0; index < equivalents.Length; index++)
                    {
                        budget.Spend();
                        var key = keys[prefix + index];
                        if (!classes.TryGetValue(key, out var equivalent))
                        {
                            equivalent = classes.Count + 1; // 原本の予約class 0を使わない。
                            classes.Add(key, equivalent);
                        }
                        equivalents[index] = equivalent;
                    }
                }
            }
        }
        catch (LimitException exception)
        {
            fallback = true;
            reason = exception.Message;
            middle.Clear();
        }
        pairs.AddRange(middle);
        while (endLeft < left.Length && endRight < right.Length)
        {
            token.ThrowIfCancellationRequested();
            pairs.Add((endLeft++, endRight++));
        }
        return new(pairs.AsReadOnly(), budget.Used, fallback, reason);

        bool FullyEqual()
        {
            for (var index = 0; index < left.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                if (left[index].MissingFinalNewline != right[index].MissingFinalNewline ||
                    !EqualText(left[index].Content, right[index].Content, token) ||
                    !EqualText(left[index].Ending, right[index].Ending, token)) return false;
            }
            return true;
        }

        bool BoundaryEqual(int ai, int bi)
        {
            token.ThrowIfCancellationRequested();
            var a = sourceLeft[activeLeft[ai]];
            var b = sourceRight[activeRight[bi]];
            if (!EqualText(a.Content, b.Content, token) ||
                (options.CompareLineEndings ? !EqualText(a.Ending, b.Ending, token)
                    : (a.Ending.Length == 0) != (b.Ending.Length == 0))) return false;
            // 同じ原文でも前の行のコメント状態などで比較キーが異なる場合は本文へ残す。
            return left[ai].MissingFinalNewline == right[bi].MissingFinalNewline &&
                EqualText(left[ai].Content, right[bi].Content, token) &&
                EqualText(left[ai].Ending, right[bi].Ending, token);
        }
    }

    private static bool EqualText(string left, string right, CancellationToken token)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
        {
            if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
            if (left[index] != right[index]) return false;
        }
        return true;
    }

    private sealed class LimitException(string reason) : Exception(reason);
    private sealed class Budget(int initial, CancellationToken token)
    {
        private readonly int limit = initial;
        internal int Remaining { get; private set; } = initial;
        internal int Used => limit - Remaining;
        internal void Spend(long amount = 1)
        {
            token.ThrowIfCancellationRequested();
            if (amount > Remaining)
            {
                Remaining = 0;
                throw new LimitException("work-limit:gnu-input");
            }
            Remaining -= (int)amount;
        }
    }

    // ハッシュ衝突時の文字比較にも同じ予算を適用し、巨大な一行を上限外にしない。
    private sealed class KeyComparer(Budget budget) : IEqualityComparer<GnuLineKey>
    {
        public bool Equals(GnuLineKey a, GnuLineKey b)
            => a.MissingFinalNewline == b.MissingFinalNewline && Same(a.Content, b.Content) && Same(a.Ending, b.Ending);
        private bool Same(string a, string b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Length != b.Length) return false;
            for (var index = 0; index < a.Length; index++)
            {
                budget.Spend();
                if (a[index] != b[index]) return false;
            }
            return true;
        }
        public int GetHashCode(GnuLineKey key)
        {
            var hash = 2166136261u;
            Hash(key.Content);
            hash = unchecked((hash ^ 0xFFFFFFFFu) * 16777619);
            Hash(key.Ending);
            return unchecked((int)((hash ^ (key.MissingFinalNewline ? 1u : 0u)) * 16777619));
            void Hash(string text)
            {
                foreach (var character in text)
                {
                    budget.Spend();
                    hash = unchecked((hash ^ character) * 16777619);
                }
            }
        }
    }
}
