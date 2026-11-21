using System.Text;

namespace DiffBeacon.Core;

/// <summary>競合ファイルを、自分・相手・祖先の本文へ分ける。本家 ConflictFileParser と同じ印。</summary>
public static class ConflictFileParser
{
    /// <summary>自分の側の開始。</summary>
    private const string MineBegin = "<<<<<<< ";

    /// <summary>祖先の側の開始。</summary>
    private const string BaseBegin = "||||||| ";

    /// <summary>相手の側の開始。</summary>
    private const string Separator = "=======";

    /// <summary>相手の側の終了。</summary>
    private const string TheirsEnd = ">>>>>>> ";

    /// <summary>分けた三つの本文。二者のときは祖先は空で、<see cref="ConflictSides.ThreeWay"/> は false。</summary>
    public readonly record struct ConflictSides(string Theirs, string Mine, string Base, bool ThreeWay, bool Nested);

    /// <summary>行頭に自分の側の開始があるか。</summary>
    public static bool IsConflictFile(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var (line, _) in Lines(text))
            if (line.StartsWith(MineBegin, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>競合ファイルの本文を分ける。印が無ければ false。</summary>
    public static bool TryParse(string text, out ConflictSides sides)
    {
        ArgumentNullException.ThrowIfNull(text);
        var mine = new StringBuilder();
        var theirs = new StringBuilder();
        var baseText = new StringBuilder();
        var state = 0;
        var nesting = 0;
        var found = false;
        var threeWay = false;
        var nested = false;
        foreach (var (line, eol) in Lines(text))
        {
            switch (state)
            {
                case 0:
                    if (line.StartsWith(MineBegin, StringComparison.Ordinal)) { state = 1; found = true; }
                    else { theirs.Append(line).Append(eol); baseText.Append(line).Append(eol); mine.Append(line).Append(eol); }
                    break;
                case 1:
                    if (line.StartsWith(MineBegin, StringComparison.Ordinal)) { state = 3; mine.Append(line).Append(eol); }
                    else if (line.IndexOf(BaseBegin, StringComparison.Ordinal) is var baseAt && baseAt >= 0)
                    {
                        var head = line[..baseAt];
                        if (head.Length > 0) baseText.Append(head).Append(eol);
                        state = 5;
                        threeWay = true;
                    }
                    else if (line.IndexOf(Separator, StringComparison.Ordinal) is var split && split >= 0 && split == line.Length - Separator.Length)
                    {
                        var head = line[..split];
                        if (head.Length > 0) mine.Append(head).Append(eol);
                        state = 2;
                    }
                    else mine.Append(line).Append(eol);
                    break;
                case 2:
                    if (line.StartsWith(MineBegin, StringComparison.Ordinal)) { state = 4; theirs.Append(line).Append(eol); }
                    else if (line.IndexOf(TheirsEnd, StringComparison.Ordinal) is var end && end >= 0)
                    {
                        var head = line[..end];
                        if (head.Length > 0) theirs.Append(head).Append(eol);
                        state = 0;
                    }
                    else theirs.Append(line).Append(eol);
                    break;
                case 3:
                    nested = true;
                    if (line.StartsWith(MineBegin, StringComparison.Ordinal)) nesting++;
                    else if (line.Contains(TheirsEnd, StringComparison.Ordinal))
                    {
                        if (nesting == 0) state = 1;
                        else nesting--;
                    }
                    mine.Append(line).Append(eol);
                    break;
                case 4:
                    nested = true;
                    if (line.StartsWith(MineBegin, StringComparison.Ordinal)) nesting++;
                    else if (line.Contains(TheirsEnd, StringComparison.Ordinal))
                    {
                        if (nesting == 0) state = 2;
                        else nesting--;
                    }
                    theirs.Append(line).Append(eol);
                    break;
                case 5:
                    if (line.IndexOf(Separator, StringComparison.Ordinal) is var baseSplit && baseSplit >= 0 && baseSplit == line.Length - Separator.Length)
                    {
                        var head = line[..baseSplit];
                        if (head.Length > 0) baseText.Append(head).Append(eol);
                        state = 2;
                    }
                    else baseText.Append(line).Append(eol);
                    break;
            }
        }
        sides = new ConflictSides(theirs.ToString(), mine.ToString(), threeWay ? baseText.ToString() : "", threeWay, nested);
        return found;
    }

    /// <summary>改行を残して行に分ける。末尾の改行の後には空行を足さない。</summary>
    private static IEnumerable<(string Line, string Eol)> Lines(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var start = index;
            while (index < text.Length && text[index] is not ('\r' or '\n')) index++;
            var line = text[start..index];
            var eol = "";
            if (index < text.Length && text[index] == '\r')
            {
                eol = index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
                index += eol.Length;
            }
            else if (index < text.Length && text[index] == '\n') { eol = "\n"; index++; }
            yield return (line, eol);
        }
    }
}
