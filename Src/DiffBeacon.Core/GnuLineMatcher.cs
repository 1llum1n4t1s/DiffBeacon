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
        return MatchCore(left, 0, left.Length, right, 0, right.Length,
            options.MaxFallbackComparisons, token, BoundaryEqual);

        bool BoundaryEqual(int ai, int bi)
        {
            token.ThrowIfCancellationRequested();
            var a = sourceLeft[activeLeft[ai]];
            var b = sourceRight[activeRight[bi]];
            if (!EqualText(a.Content, b.Content, token) ||
                (options.CompareLineEndings ? !EqualText(a.Ending, b.Ending, token)
                    : (a.Ending.Length == 0) != (b.Ending.Length == 0))) return false;
            // 同じ原文でも前の行のコメント状態などで比較キーが異なる場合は本文へ残す。
            return EqualKey(left[ai], right[bi], token);
        }
    }

    // 復号セル等の意味キーを原配列の範囲で比較し、絶対座標の一致組を返す。
    internal static GnuLineMatches MatchSemantic(GnuLineKey[] left, int leftStart, int leftEnd,
        GnuLineKey[] right, int rightStart, int rightEnd, int maxWork, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (leftStart < 0 || leftEnd < leftStart || leftEnd > left.Length)
            throw new ArgumentOutOfRangeException(nameof(leftStart));
        if (rightStart < 0 || rightEnd < rightStart || rightEnd > right.Length)
            throw new ArgumentOutOfRangeException(nameof(rightStart));
        return MatchCore(left, leftStart, leftEnd, right, rightStart, rightEnd, maxWork, token,
            (ai, bi) => EqualKey(left[ai], right[bi], token));
    }

    private static GnuLineMatches MatchCore(GnuLineKey[] left, int leftStart, int leftEnd,
        GnuLineKey[] right, int rightStart, int rightEnd, int maxWork, CancellationToken token,
        Func<int, int, bool> boundaryEqual)
    {
        token.ThrowIfCancellationRequested();
        var budget = new Budget(Math.Clamp(maxWork, 0, GnuLineDiffer.MaximumWork), token);
        var pairs = new List<(int A, int B)>();
        if (leftEnd - leftStart == rightEnd - rightStart && FullyEqual())
        {
            for (var index = 0; index < leftEnd - leftStart; index++)
            {
                token.ThrowIfCancellationRequested();
                pairs.Add((leftStart + index, rightStart + index));
            }
            return new(pairs.AsReadOnly(), 0, false, null);
        }
        var beginLeft = leftStart;
        var beginRight = rightStart;
        var endLeft = leftEnd;
        var endRight = rightEnd;
        // Textはio.cのhorizon=0に沿う原文境界、意味キーは復号一致境界を使う。
        // 線形の境界走査は従来どおり予算0でも行い、取消を文字単位で確認する。
        while (beginLeft < endLeft && beginRight < endRight && boundaryEqual(beginLeft, beginRight))
        {
            pairs.Add((beginLeft++, beginRight++));
        }
        while (endLeft > beginLeft && endRight > beginRight && boundaryEqual(endLeft - 1, endRight - 1))
        {
            endLeft--;
            endRight--;
        }
        var middle = new List<(int A, int B)>();
        var fallback = false;
        string? reason = null;
        try
        {
            var countLeft = endLeft - beginLeft;
            var countRight = endRight - beginRight;
            if (countLeft != 0 || countRight != 0)
            {
                if (countLeft > 262_144 || countRight > 262_144)
                    throw new LimitException("row-limit:gnu-input");
                budget.Spend((long)countLeft + countRight);
                var a = new int[countLeft];
                var b = new int[countRight];
                var classes = new Dictionary<GnuLineKey, int>(new KeyComparer(budget));
                Classify(left, beginLeft, a);
                Classify(right, beginRight, b);
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
                            middle.Add((beginLeft + cursorLeft++, beginRight + cursorRight++));
                        }
                    }
                }

                void Classify(GnuLineKey[] keys, int first, int[] equivalents)
                {
                    for (var index = 0; index < equivalents.Length; index++)
                    {
                        budget.Spend();
                        var key = keys[first + index];
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
        while (endLeft < leftEnd && endRight < rightEnd)
        {
            token.ThrowIfCancellationRequested();
            pairs.Add((endLeft++, endRight++));
        }
        return new(pairs.AsReadOnly(), budget.Used, fallback, reason);

        bool FullyEqual()
        {
            for (var index = 0; index < leftEnd - leftStart; index++)
            {
                token.ThrowIfCancellationRequested();
                if (!EqualKey(left[leftStart + index], right[rightStart + index], token)) return false;
            }
            return true;
        }
    }

    private static bool EqualKey(GnuLineKey left, GnuLineKey right, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return left.MissingFinalNewline == right.MissingFinalNewline &&
            EqualText(left.Content, right.Content, token) && EqualText(left.Ending, right.Ending, token);
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
