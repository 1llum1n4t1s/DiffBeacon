using System.Text;

namespace DiffBeacon.Core;

// fixed GetResultConflictBlockText: two-way native pane1 Right precedes pane0 Left。
public static class FourPaneTwoWayMaterialization
{
    public static string InitialUnresolvedText(RawDescriptor descriptor, string leftLabel, string rightLabel,
        string eol, bool whitespaceOnly = false, CancellationToken token = default,
        long maximumCharacters = 64 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(leftLabel);
        ArgumentNullException.ThrowIfNull(rightLabel);
        if (eol is not ("\n" or "\r" or "\r\n")) throw new ArgumentException("結果の改行指定が不正です。", nameof(eol));
        if (descriptor.Kind is not (FourPaneOriginalKind.Conflict or FourPaneOriginalKind.Trivial))
            throw new ArgumentException("初期未解決本文は二者差分だけを対象にします。", nameof(descriptor));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        token.ThrowIfCancellationRequested();
        var suffix = whitespaceOnly && descriptor.Kind == FourPaneOriginalKind.Conflict ? " (whitespace only)" : "";
        var extraRight = NeedsEol(descriptor.RightText) ? eol : "";
        var extraLeft = NeedsEol(descriptor.LeftText) ? eol : "";
        var length = 8L + rightLabel.Length + suffix.Length + eol.Length + descriptor.RightText.Length + extraRight.Length
            + 7 + eol.Length + descriptor.LeftText.Length + extraLeft.Length + 8 + leftLabel.Length + eol.Length;
        if (length > maximumCharacters || length > int.MaxValue)
            throw new ArgumentException("二者未解決本文の保持上限を超えます。");
        var output = new StringBuilder((int)length);
        output.Append("<<<<<<< ").Append(rightLabel).Append(suffix).Append(eol);
        Append(descriptor.RightText); output.Append(extraRight).Append("=======").Append(eol);
        Append(descriptor.LeftText); output.Append(extraLeft).Append(">>>>>>> ").Append(leftLabel).Append(eol);
        token.ThrowIfCancellationRequested();
        return output.ToString();

        // empty pane already follows a marker EOL; original text.back() adds no extra EOL。
        static bool NeedsEol(string text) => text.Length > 0 && text[^1] is not ('\n' or '\r');
        void Append(string text)
        {
            for (var offset = 0; offset < text.Length; offset += 4096)
            { token.ThrowIfCancellationRequested(); output.Append(text.AsSpan(offset, Math.Min(4096, text.Length - offset))); }
        }
    }
}
