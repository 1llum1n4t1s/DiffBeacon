using System.Text;

namespace DiffBeacon.Core;

public enum FourPaneTextProfile { LegacyRawUtf8, DecodedBody }

internal sealed record PreparedUtf8(byte[] Original, byte[] Buffered, byte[][] Lines, bool MissingNewline, bool BomRemoved)
{
    // 変換キーでは本文中のCR/LFと元行のEOLを別々に保持する。
    internal TextLine[]? StructuredRows { get; init; }

    internal static PreparedUtf8 PrepareRows(IReadOnlyList<TextLine> rows, bool ignoreEol,
        Action<long> spend, Action<long> reserve, FourPaneTextProfile textProfile)
    {
        var encoding = new UTF8Encoding(false, true);
        reserve(checked(24L * rows.Count));
        var structured = new TextLine[rows.Count];
        var lines = new byte[rows.Count][];
        var bom = textProfile == FourPaneTextProfile.LegacyRawUtf8 && rows.Count != 0
            && rows[0].Content.StartsWith('\ufeff');
        var missing = rows.Count != 0 && rows[^1].Ending.Length == 0;
        long originalLength = 0, bufferedLength = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            spend(checked(1L + row.Content.Length + row.Ending.Length));
            var start = index == 0 && bom ? 1 : 0;
            var content = row.Content;
            if (start != 0) { reserve(content.Length - 1L); content = content[1..]; }
            var ending = ignoreEol && row.Ending.Length != 0 ? "\n" : row.Ending;
            structured[index] = new(content, ending);
            var synthetic = index == rows.Count - 1 && missing;
            var contentLength = encoding.GetByteCount(content);
            var endingLength = synthetic ? 1 : encoding.GetByteCount(ending);
            var length = checked(contentLength + endingLength);
            // 行byte列・全文buffer・原文byte列を全て同じ保持予算へ予約する。
            reserve(length);
            var bytes = new byte[length];
            encoding.GetBytes(content, bytes);
            if (synthetic) bytes[^1] = 10;
            else encoding.GetBytes(ending, bytes.AsSpan(contentLength));
            lines[index] = bytes;
            originalLength = checked(originalLength + encoding.GetByteCount(row.Content) + encoding.GetByteCount(row.Ending));
            bufferedLength = checked(bufferedLength + length);
        }
        reserve(checked(originalLength + bufferedLength));
        if (originalLength > int.MaxValue || bufferedLength > int.MaxValue)
            throw new FourPaneGenerationLimit("capacity-limit:fourpane-structured-bytes");
        var original = new byte[(int)originalLength];
        var buffered = new byte[(int)bufferedLength];
        var originalPosition = 0; var bufferedPosition = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            spend(checked(1L + row.Content.Length + row.Ending.Length + lines[index].Length));
            originalPosition += encoding.GetBytes(row.Content, original.AsSpan(originalPosition));
            originalPosition += encoding.GetBytes(row.Ending, original.AsSpan(originalPosition));
            lines[index].CopyTo(buffered, bufferedPosition); bufferedPosition += lines[index].Length;
        }
        return new(original, buffered, lines, missing, bom) { StructuredRows = structured };
    }

    internal GnuLineKey ProductKey(int index, ComparisonOptions options, Action<long> spend,
        Action<long> reserve, CancellationToken token)
    {
        var missing = index == Lines.Length - 1 && MissingNewline;
        if (StructuredRows is null)
            return LegacyLineComparator.ProductKey(Lines[index], missing, options, spend, reserve, token);
        token.ThrowIfCancellationRequested();
        var row = StructuredRows[index];
        spend(checked(1L + row.Content.Length + row.Ending.Length));
        reserve(checked(2L * row.Content.Length));
        var key = options.NormalizeKey(row.Content, token);
        return new(key, options.CompareLineEndings ? row.Ending : "", !options.IgnoreFinalNewLine && missing);
    }

    // 74c2ae io.c prepare_text: UTF8だけを対象とする。UTF16/codepage変換は対象外。
    internal static PreparedUtf8 Prepare(string text, bool ignoreEol, Action<long> spend, Action<long> reserve,
        FourPaneTextProfile textProfile = FourPaneTextProfile.LegacyRawUtf8)
    {
        var encoding = new UTF8Encoding(false, true);
        spend(text.Length);
        var length = encoding.GetByteCount(text);
        reserve(checked(4L * length + 3));
        var original = encoding.GetBytes(text);
        // 製品本文はTextDocumentで復号済み。先頭FEFFも本文として保持する。
        var bom = textProfile == FourPaneTextProfile.LegacyRawUtf8 &&
            original.AsSpan().StartsWith(new byte[] { 239, 187, 191 });
        var start = bom ? 3 : 0;
        var missing = original.Length > start && original[^1] is not (10 or 13);
        var output = new List<byte>(length + 1);
        for (var i = start; i < original.Length; i++)
        {
            spend(1);
            var c = original[i];
            if (ignoreEol && c == 13)
            {
                if (i + 1 < original.Length && original[i + 1] == 10) { spend(1); i++; }
                c = 10;
            }
            output.Add(c);
        }
        if (missing) { spend(1); output.Add(10); }
        var buffered = output.ToArray();
        var lines = new List<byte[]>(); var first = 0;
        for (var i = 0; i < buffered.Length; i++)
        {
            spend(1);
            if (buffered[i] is not (10 or 13)) continue;
            if (buffered[i] == 13 && i + 1 < buffered.Length && buffered[i + 1] == 10) { spend(1); i++; }
            lines.Add(buffered[first..(i + 1)]); first = i + 1;
        }
        if (first != buffered.Length) throw new InvalidOperationException("prepared UTF8 must end in EOL");
        reserve(checked(8L * lines.Count));
        return new(original, buffered, lines.ToArray(), missing, bom);
    }
}

internal sealed class FourPaneByteBridge
{
    private readonly GnuLineMatcher.LineBudget budget;
    private readonly CancellationToken token;
    private readonly ComparisonOptions options;
    private readonly Action<long> reserve;
    private readonly FourPaneTextProfile textProfile;
    internal PreparedUtf8 Left { get; }
    internal PreparedUtf8 Right { get; }
    internal NativeLineFlags Flags { get; }

    internal FourPaneByteBridge(string left, string right, int rawLeftCount, int rawRightCount,
        ComparisonOptions options, GnuLineMatcher.LineBudget budget, CancellationToken token,
        Action<long> reserve, FourPaneTextProfile textProfile = FourPaneTextProfile.LegacyRawUtf8)
    {
        this.budget = budget; this.token = token;
        this.options = options; this.reserve = reserve; this.textProfile = textProfile;
        Flags = NativeFlags(options, textProfile);
        Left = PreparedUtf8.Prepare(left, Flags.HasFlag(NativeLineFlags.IgnoreEol), Spend, reserve, textProfile);
        Right = PreparedUtf8.Prepare(right, Flags.HasFlag(NativeLineFlags.IgnoreEol), Spend, reserve, textProfile);
        if (Left.Lines.Length != rawLeftCount || Right.Lines.Length != rawRightCount)
            throw new ArgumentException("BOM-only raw/prepared line mapping is outside this bridge");
    }

    internal FourPaneByteBridge(IReadOnlyList<TextLine> left, IReadOnlyList<TextLine> right,
        ComparisonOptions options, GnuLineMatcher.LineBudget budget, CancellationToken token,
        Action<long> reserve, FourPaneTextProfile textProfile)
    {
        this.budget = budget; this.token = token;
        this.options = options; this.reserve = reserve; this.textProfile = textProfile;
        Flags = NativeFlags(options, textProfile);
        Left = PreparedUtf8.PrepareRows(left, Flags.HasFlag(NativeLineFlags.IgnoreEol), Spend, reserve, textProfile);
        Right = PreparedUtf8.PrepareRows(right, Flags.HasFlag(NativeLineFlags.IgnoreEol), Spend, reserve, textProfile);
    }

    internal static NativeLineFlags NativeFlags(ComparisonOptions options,
        FourPaneTextProfile textProfile = FourPaneTextProfile.LegacyRawUtf8)
    {
        var mode = options.IgnoreWhitespace ? WhitespaceMode.IgnoreAll : options.Whitespace;
        if (mode == WhitespaceMode.Trim && textProfile == FourPaneTextProfile.LegacyRawUtf8)
            throw new ArgumentException("byte bridge has no native Trim flag");
        return (options.IgnoreCase ? NativeLineFlags.IgnoreCase : 0)
            | (options.IgnoreNumbers ? NativeLineFlags.IgnoreNumbers : 0)
            | (!options.CompareLineEndings ? NativeLineFlags.IgnoreEol : 0)
            | (mode == WhitespaceMode.IgnoreAll ? NativeLineFlags.IgnoreAllSpace : 0)
            | (mode == WhitespaceMode.IgnoreChanges ? NativeLineFlags.IgnoreSpaceChange : 0);
    }
    internal bool EqualBranches(FourPaneThreeSpan span)
    {
        token.ThrowIfCancellationRequested();
        var leftCount = span.LeftEnd - span.LeftBegin + 1;
        var rightCount = span.RightEnd - span.RightBegin + 1;
        // Comp02Functorのraw行数gate。postfilterや全文等価を代用しない。
        if (leftCount != rightCount) return false;
        for (var i = 0; i < leftCount; i++)
        {
            Spend(1);
            if (textProfile == FourPaneTextProfile.DecodedBody)
            {
                var li = span.LeftBegin + i; var ri = span.RightBegin + i;
                var lk = Left.ProductKey(li, options, Spend, reserve, token);
                var rk = Right.ProductKey(ri, options, Spend, reserve, token);
                Spend((long)lk.Content.Length + rk.Content.Length + lk.Ending.Length + rk.Ending.Length);
                if (lk != rk) return false;
                continue;
            }
            if (LegacyLineComparator.Compare(Left.Lines[span.LeftBegin + i], Right.Lines[span.RightBegin + i],
                Flags, ByteSpend) != 0) return false;
        }
        return true;
    }

    private void ByteSpend()
    {
        Spend(1); token.ThrowIfCancellationRequested();
    }

    private void Spend(long count)
    {
        token.ThrowIfCancellationRequested();
        if (count > budget.Remaining) throw new FourPaneGenerationLimit("work-limit:fourpane-byte-bridge");
        budget.Spend(count);
    }
}
