using System.Text;

namespace DiffBeacon.Core;

// CLIの保存契約だけを適用する。pane順序・採用・差分判定はFourPaneのまま扱う。
public static class FourPaneCliSerialization
{
    public static string ExpandedText(FourPaneMaterializedResult result, ResultLineBuffer buffer,
        string ancestor, string left, string right, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(ancestor); ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right); token.ThrowIfCancellationRequested();
        if (!result.HasAncestor) throw new ArgumentException("CLIマージには祖先が必要です。");
        buffer.ValidateTextRanges();
        // 全文で合意した枝は、比較で無視されたEOLも含めて原文を保存する。
        if (!buffer.Segments.Any(s => s.IsPlaceholder))
        {
            if (left == right || right == ancestor) return left;
            if (left == ancestor) return right;
        }
        const int maximumCharacters = 64 * 1024 * 1024;
        var output = new StringBuilder();
        foreach (var segment in buffer.Segments)
        {
            token.ThrowIfCancellationRequested();
            string text;
            if (segment.IsPlaceholder)
            {
                var descriptor = result.Choices.At(segment.OriginalDiffId);
                text = Markers(descriptor.Source(0).RawText, descriptor.Source(1).RawText,
                    descriptor.Source(2).RawText, result.DefaultEol, token);
            }
            else text = buffer.Text.Substring(segment.TextStart, segment.TextLength);
            long size = (long)output.Length + text.Length;
            if (text.Length > 0 && output.Length > 0 && output[^1] is not ('\r' or '\n'))
                size += result.DefaultEol.Length;
            if (size > maximumCharacters) throw new ArgumentException("expanded CLI result capacity exceeded");
            TextLines.AppendSeparated(output, text, result.DefaultEol);
        }
        return output.ToString();
    }

    private static string Markers(string left, string ancestor, string right, string newline,
        CancellationToken token)
    {
        long maximumLength = (long)left.Length + ancestor.Length + right.Length + 44 + 7L * newline.Length;
        if (maximumLength > 64 * 1024 * 1024) throw new ArgumentException("CLI conflict marker capacity exceeded");
        var output = new StringBuilder("<<<<<<< LEFT" + newline);
        void Part(string text)
        {
            token.ThrowIfCancellationRequested(); output.Append(text);
            if (text.Length > 0 && !TextLines.HasFinalNewLine(text)) output.Append(newline);
        }
        Part(left); output.Append("||||||| BASE").Append(newline); Part(ancestor);
        output.Append("=======").Append(newline); Part(right);
        output.Append(">>>>>>> RIGHT").Append(newline);
        return output.ToString();
    }
}
