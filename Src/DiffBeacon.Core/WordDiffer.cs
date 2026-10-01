using System.Globalization;

namespace DiffBeacon.Core;

public enum WordWhitespaceMode { CompareAll, IgnoreChanges, IgnoreAll }
public enum WordEolMode { Strict, Ignore, AsSpace }

public sealed record WordDiffOptions
{
    public bool MatchCase { get; init; } = true;
    public WordWhitespaceMode Whitespace { get; init; }
    public WordEolMode Eol { get; init; }
    public bool IgnoreNumbers { get; init; }
    public bool BreakOnSeparators { get; init; } = true;
    public string Separators { get; init; } = ",.;:";
    public bool CharacterLevel { get; init; } = true;
}

public readonly record struct WordRange(int Start, int Length);
public readonly record struct WordDifference(WordRange Left, WordRange Right);
public sealed record WordDiffResult(IReadOnlyList<WordDifference> Differences, bool Fallback, string? FallbackReason)
{
    public int WorkUsed { get; init; }
}

/// <summary>原文の UTF-16 区間を保持する、予算付きの旧 WordDiff 互換比較。</summary>
public static class WordDiffer
{
    private const int MaxTextLength = 64 * 1024 * 1024;
    private const int MaxTokenVector = 20_480;
    private const int MaxTraceBytes = 32 * 1024 * 1024;
    private enum TokenKind { Word, Space, Eol, Separator, Number }
    private readonly record struct Word(int Start, int Length, TokenKind Kind, uint Hash);
    private readonly record struct Trace(char Op, int EqualCount, int Previous);

    public static WordDiffResult Compare(string left, string right, WordDiffOptions? options = null,
        int maxWork = 4_000_000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.Separators);
        if (!Enum.IsDefined(options.Whitespace) || !Enum.IsDefined(options.Eol))
            throw new ArgumentException("WordDiff options contain an unknown mode.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(maxWork);
        maxWork = Math.Min(maxWork, 8_000_000);
        cancellationToken.ThrowIfCancellationRequested();
        if (left.Length > MaxTextLength || right.Length > MaxTextLength)
            return FullDifference(left, right, "text-limit");
        var engine = new Engine(left, right, options, maxWork, cancellationToken);
        try { return engine.Run() with { WorkUsed = engine.WorkUsed }; }
        catch (LimitException limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return FullDifference(left, right, limit.Reason) with { WorkUsed = engine.WorkUsed };
        }
    }

    private static WordDiffResult FullDifference(string left, string right, string reason)
        => new(Array.AsReadOnly(new[] { new WordDifference(new(0, left.Length), new(0, right.Length)) }), true, reason);

    private sealed class LimitException(string reason) : Exception
    {
        public string Reason { get; } = reason;
    }

    // 伸長時に巨大配列を複製しない。Trace は 12 bytes 相当、合計 32 MiB 以下。
    private sealed class TraceStore
    {
        private const int ChunkLength = 4096;
        private readonly List<Trace[]> chunks = [];
        public int Count { get; private set; }
        public int Add(Trace value)
        {
            if (Count >= MaxTraceBytes / 12) throw new LimitException("trace-limit");
            if (Count % ChunkLength == 0)
            {
                if ((long)(chunks.Count + 1) * ChunkLength * 12 > MaxTraceBytes)
                    throw new LimitException("trace-limit");
                chunks.Add(new Trace[ChunkLength]);
            }
            var index = Count++;
            chunks[index / ChunkLength][index % ChunkLength] = value;
            return index;
        }
        public Trace Get(int index) => chunks[index / ChunkLength][index % ChunkLength];
    }

    private sealed class Engine(string left, string right, WordDiffOptions options, int maxWork, CancellationToken token)
    {
        private long remaining = maxWork;
        public int WorkUsed => (int)(maxWork - remaining);
        private readonly bool[] separators = new bool[256];
        private void Spend(long amount, string phase)
        {
            token.ThrowIfCancellationRequested();
            if (amount > remaining) throw new LimitException("work-limit:" + phase);
            remaining -= amount;
        }

        public WordDiffResult Run()
        {
            Spend(options.Separators.Length, "separators");
            for (var index = 0; index < options.Separators.Length; index++)
            {
                if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
                var ch = options.Separators[index];
                if (ch <= '\u00ff') separators[ch] = true;
            }
            var a = Tokenize(left);
            var b = Tokenize(right);
            var script = Onp(a, b);
            var differences = BuildDifferences(a, b, script);
            if (options.CharacterLevel)
                for (var index = 0; index < differences.Count; index++)
                {
                    Spend(1, "character-refinement");
                    differences[index] = Refine(differences[index]);
                }
            var combined = new List<WordDifference>(differences.Count);
            foreach (var difference in differences)
            {
                Spend(1, "coalescing");
                if (combined.Count > 0)
                {
                    var previous = combined[^1];
                    if (previous.Left.Start + previous.Left.Length == difference.Left.Start &&
                        previous.Right.Start + previous.Right.Length == difference.Right.Start)
                    {
                        combined[^1] = new(new(previous.Left.Start, previous.Left.Length + difference.Left.Length),
                            new(previous.Right.Start, previous.Right.Length + difference.Right.Length));
                        continue;
                    }
                }
                combined.Add(difference);
            }
            token.ThrowIfCancellationRequested();
            return new(combined.AsReadOnly(), false, null);
        }

        private List<Word> Tokenize(string text)
        {
            // 境界判定と妥当性確認の原文走査を先に課金する。
            Spend((long)text.Length * 2, "tokenization");
            for (var index = 0; index < text.Length; index++)
            {
                if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!char.IsSurrogate(text[index])) continue;
                if (!char.IsHighSurrogate(text[index]) || index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                    throw new LimitException("invalid-utf16");
                index++;
            }
            var words = new List<Word> { new(0, 0, TokenKind.Word, 0) };
            if (text.Length == 0) return words;
            var begin = 0;
            var indexAt = 0;
            var previous = TokenKind.Word;
            while (indexAt < text.Length)
            {
                Spend(1, "tokenization");
                var kind = Classify(text[indexAt]);
                if (indexAt > 0 && (kind != previous || kind == TokenKind.Separator ||
                    (previous == TokenKind.Eol && !(text[indexAt - 1] == '\r' && text[indexAt] == '\n'))))
                {
                    AddWord(words, text, begin, indexAt - begin, previous);
                    begin = indexAt;
                }
                if (options.Eol == WordEolMode.AsSpace && kind == TokenKind.Space)
                {
                    do { Spend(1, "tokenization"); indexAt++; }
                    while (indexAt < text.Length && (text[indexAt] is '\r' or '\n' || SafeSpace(text[indexAt])));
                }
                else indexAt += GlyphLength(text.AsSpan(indexAt));
                previous = kind;
            }
            AddWord(words, text, begin, text.Length - begin, previous);
            return words;
        }

        private void AddWord(List<Word> words, string text, int start, int length, TokenKind kind)
        {
            if (words.Count + 1 >= MaxTokenVector) throw new LimitException("token-limit");
            Spend(length, "hashing");
            uint hash = 0;
            for (var index = start; index < start + length; index++)
            {
                if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
                var ch = options.MatchCase ? text[index] : FoldCase(text[index]);
                hash = unchecked(hash + ch + ((hash << 7) | (hash >> 25)));
            }
            words.Add(new(start, length, kind, hash));
        }

        private TokenKind Classify(char ch)
        {
            if (ch is '\r' or '\n') return options.Eol == WordEolMode.AsSpace ? TokenKind.Space : TokenKind.Eol;
            if (SafeSpace(ch)) return TokenKind.Space;
            // 原本で採取した Windows CTYPE1 / CRT C の分類を全 OS で固定する。
            var separator = ch <= '\u00ff' ? options.BreakOnSeparators && separators[ch]
                : (WordCharacterProfile.Flags(ch) & 1) == 0;
            if (separator) return TokenKind.Separator;
            if (options.IgnoreNumbers && (WordCharacterProfile.Flags(ch) & 2) != 0) return TokenKind.Number;
            return TokenKind.Word;
        }

        private static bool SafeSpace(char ch) => (WordCharacterProfile.Flags(ch) & 4) != 0;

        // 原本プローブの CRT locale=C は非 ASCII を小文字化しない。
        private static char FoldCase(char ch) => ch is >= 'A' and <= 'Z' ? (char)(ch + ('a' - 'A')) : ch;

        private int GlyphLength(ReadOnlySpan<char> text)
        {
            token.ThrowIfCancellationRequested();
            var length = StringInfo.GetNextTextElementLength(text);
            // ICU63 由来の旧規則では半角濁点・半濁点を直前の grapheme に結合しない。
            for (var index = 1; index < length; index++)
            {
                if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
                if (text[index] is '\uff9e' or '\uff9f') return index;
            }
            token.ThrowIfCancellationRequested();
            return length;
        }

        private bool SameWord(Word a, Word b)
        {
            Spend(1, "token-comparison");
            if (options.Whitespace != WordWhitespaceMode.CompareAll && a.Kind == TokenKind.Space && b.Kind == TokenKind.Space)
                return true;
            if (options.IgnoreNumbers && (WordCharacterProfile.Flags(left[a.Start]) & 2) != 0
                && (WordCharacterProfile.Flags(right[b.Start]) & 2) != 0) return true;
            if (options.Eol == WordEolMode.Ignore && a.Kind == TokenKind.Eol && b.Kind == TokenKind.Eol) return true;
            if (options.Eol == WordEolMode.AsSpace && a.Kind == TokenKind.Space && b.Kind == TokenKind.Space)
                return SameSpace(left.AsSpan(a.Start, a.Length), right.AsSpan(b.Start, b.Length));
            if (a.Hash != b.Hash || a.Length != b.Length) return false;
            return SameCharacters(left.AsSpan(a.Start, a.Length), right.AsSpan(b.Start, b.Length));
        }

        private bool SameSpace(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
        {
            var x = 0;
            var y = 0;
            while (x < a.Length && y < b.Length)
            {
                Spend(1, "character-comparison");
                var ac = a[x++];
                var bc = b[y++];
                if (ac == '\r' && x < a.Length && a[x] == '\n') x++;
                if (bc == '\r' && y < b.Length && b[y] == '\n') y++;
                if (ac is '\r' or '\n') ac = ' ';
                if (bc is '\r' or '\n') bc = ' ';
                if (ac != bc) return false;
            }
            return x == a.Length && y == b.Length;
        }

        private bool SameCharacters(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
        {
            if (a.Length != b.Length) return false;
            for (var index = 0; index < a.Length; index++)
            {
                Spend(1, "character-comparison");
                if (options.MatchCase ? a[index] != b[index] : FoldCase(a[index]) != FoldCase(b[index]))
                    return false;
            }
            return true;
        }

        private List<char> Onp(List<Word> a, List<Word> b)
        {
            var m = a.Count - 1;
            var n = b.Count - 1;
            var exchanged = m > n;
            if (exchanged) (m, n) = (n, m);
            var offset = m + 1;
            var frontier = new int[m + n + 3];
            var heads = new int[frontier.Length];
            Array.Fill(frontier, -1);
            Array.Fill(heads, -1);
            var traces = new TraceStore();
            var delta = n - m;
            void Visit(int k)
            {
                Spend(1, "frontier");
                var pos = k + offset;
                var plus = frontier[pos - 1] + 1;
                var minus = frontier[pos + 1];
                var isPlus = plus > minus;
                var predecessor = isPlus ? pos - 1 : pos + 1;
                var startY = Math.Max(plus, minus);
                var y = startY;
                var x = y - k;
                while (x < m && y < n && (exchanged ? SameWord(a[y + 1], b[x + 1]) : SameWord(a[x + 1], b[y + 1])))
                { x++; y++; }
                frontier[pos] = y;
                heads[pos] = traces.Add(new(isPlus ? '+' : '-', y - startY, heads[predecessor]));
            }
            for (var p = 0; ; p++)
            {
                for (var k = -p; k < delta; k++) Visit(k);
                for (var k = delta + p; k > delta; k--) Visit(k);
                Visit(delta);
                if (frontier[delta + offset] == n) break;
            }
            var reversed = new List<char>(m + n + 1);
            for (var head = heads[delta + offset]; head >= 0;)
            {
                Spend(1, "trace");
                var trace = traces.Get(head);
                for (var count = 0; count < trace.EqualCount; count++)
                { Spend(1, "trace"); reversed.Add('='); }
                reversed.Add(trace.Op);
                head = trace.Previous;
            }
            reversed.Reverse();
            var script = new List<char>(m + n);
            // 最初の仮想編集を除き、隣接する逆符号の編集を置換へ結合する。
            for (var index = 1; index < reversed.Count; index++)
            {
                Spend(1, "script");
                var ch = reversed[index];
                if (ch == '=') { script.Add(ch); continue; }
                if (index + 1 < reversed.Count && reversed[index + 1] == (ch == '+' ? '-' : '+'))
                { script.Add('!'); index++; }
                else script.Add(exchanged ? (ch == '+' ? '-' : '+') : ch);
            }
            return script;
        }

        private List<WordDifference> BuildDifferences(List<Word> a, List<Word> b, List<char> script)
        {
            var differences = new List<WordDifference>();
            var x = 1;
            var y = 1;
            foreach (var operation in script)
            {
                Spend(1, "range-projection");
                if (operation == '-')
                {
                    var word = a[x++];
                    if (IgnoredEdit(word)) continue;
                    differences.Add(new(new(word.Start, word.Length), new(b[y - 1].Start + b[y - 1].Length, 0)));
                }
                else if (operation == '+')
                {
                    var word = b[y++];
                    if (IgnoredEdit(word)) continue;
                    differences.Add(new(new(a[x - 1].Start + a[x - 1].Length, 0), new(word.Start, word.Length)));
                }
                else if (operation == '!')
                {
                    var aw = a[x++];
                    var bw = b[y++];
                    if ((options.Whitespace != WordWhitespaceMode.CompareAll && aw.Kind == TokenKind.Space && bw.Kind == TokenKind.Space) ||
                        (options.IgnoreNumbers && aw.Kind == TokenKind.Number && bw.Kind == TokenKind.Number)) continue;
                    differences.Add(new(new(aw.Start, aw.Length), new(bw.Start, bw.Length)));
                }
                else { x++; y++; }
            }
            return differences;
        }

        private bool IgnoredEdit(Word word)
            => (options.Whitespace == WordWhitespaceMode.IgnoreAll && word.Kind == TokenKind.Space) ||
               (options.IgnoreNumbers && word.Kind == TokenKind.Number);

        private WordDifference Refine(WordDifference difference)
        {
            // 本体は原文 span の境界だけを動かし、変換済み文字列を生成しない。
            var a = left.AsSpan(difference.Left.Start, difference.Left.Length);
            var b = right.AsSpan(difference.Right.Start, difference.Right.Length);
            if (a.IsEmpty || b.IsEmpty) return difference;
            var ab = Boundaries(a);
            var bb = Boundaries(b);
            var ax = 0;
            var bx = 0;
            var az = ab.Count - 2;
            var bz = bb.Count - 2;
            if (options.Whitespace != WordWhitespaceMode.CompareAll)
            {
                while (ax < az && SafeSpace(a[ab[ax]])) { Spend(1, "character-refinement"); ax++; }
                while (bx < bz && SafeSpace(b[bb[bx]])) { Spend(1, "character-refinement"); bx++; }
                while (az > ax && SafeSpace(a[ab[az]])) { Spend(1, "character-refinement"); az--; }
                while (bz > bx && SafeSpace(b[bb[bz]])) { Spend(1, "character-refinement"); bz--; }
            }
            // 旧 equal=false の全空白片側例外を保持する。
            if ((ax == az && SafeSpace(a[ab[az]])) || (bx == bz && SafeSpace(b[bb[bz]])))
                return difference;
            while (ax <= az && bx <= bz)
            {
                Spend(1, "character-refinement");
                if (options.Whitespace != WordWhitespaceMode.CompareAll && ax < az && SafeSpace(a[ab[ax]]))
                {
                    if (options.Whitespace == WordWhitespaceMode.IgnoreChanges && !SafeSpace(b[bb[bx]])) break;
                    while (ax <= az && SafeSpace(a[ab[ax]])) { Spend(1, "character-refinement"); ax++; }
                    while (bx <= bz && SafeSpace(b[bb[bx]])) { Spend(1, "character-refinement"); bx++; }
                    continue;
                }
                if (options.Whitespace != WordWhitespaceMode.CompareAll && bx < bz && SafeSpace(b[bb[bx]]))
                {
                    if (options.Whitespace == WordWhitespaceMode.IgnoreChanges && !SafeSpace(a[ab[ax]])) break;
                    while (ax <= az && SafeSpace(a[ab[ax]])) { Spend(1, "character-refinement"); ax++; }
                    while (bx <= bz && SafeSpace(b[bb[bx]])) { Spend(1, "character-refinement"); bx++; }
                    continue;
                }
                if (!SameCharacters(a[ab[ax]..ab[ax + 1]], b[bb[bx]..bb[bx + 1]])) break;
                ax++; bx++;
            }
            while (az >= ax && bz >= bx)
            {
                Spend(1, "character-refinement");
                if (options.Whitespace != WordWhitespaceMode.CompareAll && az > ax && SafeSpace(a[ab[az]]))
                {
                    if (options.Whitespace == WordWhitespaceMode.IgnoreChanges && !SafeSpace(b[bb[bz]])) break;
                    while (az > ax && SafeSpace(a[ab[az]])) { Spend(1, "character-refinement"); az--; }
                    while (bz > bx && SafeSpace(b[bb[bz]])) { Spend(1, "character-refinement"); bz--; }
                    continue;
                }
                if (options.Whitespace != WordWhitespaceMode.CompareAll && bz > bx && SafeSpace(b[bb[bz]]))
                {
                    if (options.Whitespace == WordWhitespaceMode.IgnoreChanges) break;
                    while (bz > bx && SafeSpace(b[bb[bz]])) { Spend(1, "character-refinement"); bz--; }
                    continue;
                }
                if (!SameCharacters(a[ab[az]..ab[az + 1]], b[bb[bz]..bb[bz + 1]])) break;
                az--; bz--;
            }
            // 旧両側空区間は消去せず、元の差分開始位置に保持する。
            if (ax > az && bx > bz)
                return new(new(difference.Left.Start, 0), new(difference.Right.Start, 0));
            return new(new(difference.Left.Start + ab[ax], Math.Max(0, ab[az + 1] - ab[ax])),
                new(difference.Right.Start + bb[bx], Math.Max(0, bb[bz + 1] - bb[bx])));
        }

        private List<int> Boundaries(ReadOnlySpan<char> text)
        {
            // 字素配列も同じ決定的予算へ課金し、32 MiB を超える境界表を拒否する。
            var boundaries = new List<int> { 0 };
            for (var index = 0; index < text.Length;)
            {
                Spend(1, "character-boundaries");
                if (boundaries.Count >= MaxTraceBytes / sizeof(int) / 2)
                    throw new LimitException("boundary-limit");
                index += GlyphLength(text[index..]);
                boundaries.Add(index);
            }
            return boundaries;
        }
    }
}
