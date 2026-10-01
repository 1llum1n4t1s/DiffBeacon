using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal sealed record TableSearchOptions(string Pattern, bool MatchCase = false, bool UseRegex = false, bool WholeWord = false);
internal sealed record TableSearchRange(int SourceRow, int Column, int Start, int Length);
internal sealed record TableReplacementChange(int SourceRow, int Column, int Start, int Length, int NewLength);
internal sealed record TableReplacementPlan(string Text, int Count, IReadOnlyList<TableReplacementChange> Changes);

internal sealed class TableSearchEngine
{
    private const int MaxPattern = 65_536;
    private const int MaxReplacement = 1_048_576;
    private const int MaxMatches = 1_048_576;
    private const int MaxOutput = 64 * 1024 * 1024;
    private readonly TableSearchOptions options;
    private readonly Regex? regex;
    private readonly int lastGroup;

    public TableSearchEngine(TableSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Pattern);
        if (options.Pattern.Length > MaxPattern) throw new ArgumentException("検索式は 65,536 文字までです。", nameof(options));
        this.options = options;
        if (options.Pattern.Length == 0) return;
        var pattern = options.UseRegex ? options.Pattern : Regex.Escape(options.Pattern);
        if (options.WholeWord) pattern = @"(?<![\p{L}\p{N}_])(?:" + pattern + @")(?![\p{L}\p{N}_])";
        regex = new(pattern, RegexOptions.CultureInvariant | (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase),
            TimeSpan.FromMilliseconds(250));
        lastGroup = regex.GetGroupNumbers()[^1];
    }

    public IEnumerable<Match> Matches(string value, int start = 0, int? length = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        var count = length ?? value.Length - start;
        ValidateRange(value, start, count);
        return Enumerate();

        IEnumerable<Match> Enumerate()
        {
            if (regex is null) yield break;
            var end = start + count;
            var inspected = 0;
            // セル原文を保持し、lookbehind / anchors / prefix / suffix の意味を変えない。
            for (var match = regex.Match(value, start); match.Success && match.Index <= end; match = match.NextMatch())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++inspected > MaxMatches) throw new InvalidOperationException("一致は 1,048,576 件までです。");
                if (match.Index + match.Length > end) continue;
                // surrogate を割る非空一致、surrogate/CRLF 内部の空一致は採用しない。
                if (SplitsSurrogate(value, match.Index) || SplitsSurrogate(value, match.Index + match.Length)) continue;
                if (match.Length == 0 && match.Index > 0 && match.Index < value.Length &&
                    value[match.Index - 1] == '\r' && value[match.Index] == '\n') continue;
                yield return match;
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static bool SplitsSurrogate(string text, int at)
        => at > 0 && at < text.Length && char.IsHighSurrogate(text[at - 1]) && char.IsLowSurrogate(text[at]);

    private static void ValidateRange(string value, int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start > value.Length || length > value.Length - start) throw new ArgumentOutOfRangeException(nameof(length));
    }

    public TableReplacementPlan Replace(TableDocument document, string replacement, TableSearchRange? scope = null,
        TableSearchRange? onlyMatch = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();
        if (regex is null) throw new InvalidOperationException("置換する検索式を入力してください。");
        if (replacement.Length > MaxReplacement) throw new ArgumentException("置換式は 1,048,576 文字までです。", nameof(replacement));
        if (document.SourceText.Length > MaxOutput) throw new InvalidOperationException("表は 67,108,864 文字までです。");
        cancellationToken.ThrowIfCancellationRequested();
        if (scope is not null) ValidateCellRange(document, scope);
        if (onlyMatch is not null) ValidateCellRange(document, onlyMatch);
        if (onlyMatch is not null && scope is not null && (onlyMatch.SourceRow != scope.SourceRow || onlyMatch.Column != scope.Column ||
            onlyMatch.Start < scope.Start || (long)onlyMatch.Start + onlyMatch.Length > (long)scope.Start + scope.Length))
            throw new InvalidOperationException("選択した一致が固定検索範囲に含まれていません。");
        var template = ParseReplacement(replacement, cancellationToken);
        var changes = new List<TableReplacementChange>();
        var edits = new List<TableTextEdit>();
        long outputLength = document.SourceText.Length;
        long expansionWork = 0;
        for (var rowIndex = 0; rowIndex < document.Rows.Count; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = document.Rows[rowIndex];
            if (scope is not null && scope.SourceRow != row.SourceRow || onlyMatch is not null && onlyMatch.SourceRow != row.SourceRow) continue;
            for (var columnIndex = 0; columnIndex < row.Cells.Count; columnIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var column = columnIndex + 1;
                if (scope is not null && scope.Column != column || onlyMatch is not null && onlyMatch.Column != column) continue;
                var cell = row.Cells[columnIndex];
                // 一件置換は選択位置から再照合する。以前の一致と重なる有効な一致も再検証できる。
                var start = onlyMatch?.Start ?? scope?.Start ?? 0;
                var length = onlyMatch?.Length ?? scope?.Length ?? cell.Value.Length;
                StringBuilder? value = null;
                var consumed = 0;
                var selectedFound = false;
                foreach (var match in Matches(cell.Value, start, length, cancellationToken))
                {
                    if (onlyMatch is not null && (match.Index != onlyMatch.Start || match.Length != onlyMatch.Length)) continue;
                    if (changes.Count >= MaxMatches) throw new InvalidOperationException("置換は 1,048,576 件までです。");
                    if (match.Index < consumed) throw new InvalidOperationException("一致区間が重なっています。");
                    value ??= new();
                    Append(value, cell.Value.AsSpan(consumed, match.Index - consumed), cancellationToken);
                    var before = value.Length;
                    Expand(value, cell.Value, match, replacement, template, ref expansionWork, cancellationToken);
                    changes.Add(new(row.SourceRow, column, match.Index, match.Length, value.Length - before));
                    consumed = match.Index + match.Length;
                    selectedFound = true;
                    if (onlyMatch is not null) break;
                }
                if (onlyMatch is not null && !selectedFound) throw new InvalidOperationException("選択した一致が現在の検索式で再確認できません。");
                if (value is null) continue;
                Append(value, cell.Value.AsSpan(consumed), cancellationToken);
                var edit = StructuredComparer.ReplaceCell(document, row.SourceRow, column, value.ToString(), cancellationToken);
                if (edit.Start < 0 || edit.Length < 0 || edit.Start > document.SourceText.Length - edit.Length ||
                    edits.Count > 0 && edit.Start < (long)edits[^1].Start + edits[^1].Length)
                    throw new InvalidOperationException("置換する原文区間が不正または重複しています。");
                outputLength += (long)edit.Replacement.Length - edit.Length;
                if (outputLength > MaxOutput) throw new InvalidOperationException("置換後の表が 67,108,864 文字を超えます。");
                edits.Add(edit);
            }
        }
        if (onlyMatch is not null && changes.Count != 1) throw new InvalidOperationException("選択した一致を再確認できません。");
        if (changes.Count == 0) return new(document.SourceText, 0, changes.AsReadOnly());
        var text = new StringBuilder();
        var sourceStart = 0;
        foreach (var edit in edits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(text, document.SourceText.AsSpan(sourceStart, edit.Start - sourceStart), cancellationToken);
            Append(text, edit.Replacement.AsSpan(), cancellationToken);
            sourceStart = edit.Start + edit.Length;
        }
        Append(text, document.SourceText.AsSpan(sourceStart), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(text.ToString(), changes.Count, changes.AsReadOnly());
    }

    private static void ValidateCellRange(TableDocument document, TableSearchRange range)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(range.SourceRow, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(range.SourceRow, document.Rows.Count);
        var cells = document.Rows[range.SourceRow - 1].Cells;
        ArgumentOutOfRangeException.ThrowIfLessThan(range.Column, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(range.Column, cells.Count);
        ValidateRange(cells[range.Column - 1].Value, range.Start, range.Length);
    }

    private static void Append(StringBuilder builder, ReadOnlySpan<char> value, CancellationToken token)
    {
        if ((long)builder.Length + value.Length > MaxOutput) throw new InvalidOperationException("置換結果が 67,108,864 文字を超えます。");
        for (var start = 0; start < value.Length; start += 4096)
        {
            token.ThrowIfCancellationRequested();
            builder.Append(value.Slice(start, Math.Min(4096, value.Length - start)));
        }
    }

    private enum ReplacementKind { Literal, Group, Prefix, Suffix, LastGroup, Input }
    private readonly record struct ReplacementPart(ReplacementKind Kind, int Start, int Length, int Group = 0);

    private List<ReplacementPart> ParseReplacement(string replacement, CancellationToken token)
    {
        if (!options.UseRegex) return [new(ReplacementKind.Literal, 0, replacement.Length)];
        var parts = new List<ReplacementPart>();
        var literalStart = 0;
        for (var index = 0; index < replacement.Length; index++)
        {
            if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
            if (replacement[index] != '$' || index + 1 == replacement.Length) continue;
            var next = index + 1;
            var end = next + 1;
            var group = 0;
            ReplacementKind? kind = replacement[next] switch
            {
                '$' => ReplacementKind.Literal,
                '&' => ReplacementKind.Group,
                '`' => ReplacementKind.Prefix,
                '\'' => ReplacementKind.Suffix,
                '+' => ReplacementKind.LastGroup,
                '_' => ReplacementKind.Input,
                _ => null
            };
            if (replacement[next] == '{')
            {
                // 正規表現 group 名の文字だけを一度走査する。未閉鎖 ${ の連続を二乗探索しない。
                var close = next + 1;
                while (close < replacement.Length && GroupNameCharacter(replacement[close]))
                {
                    if ((close & 4095) == 0) token.ThrowIfCancellationRequested();
                    close++;
                }
                if (next + 1 < replacement.Length && replacement[next + 1] is >= '0' and <= '9')
                    ValidateNumericGroup(replacement.AsSpan(next + 1, close - next - 1), token);
                if (close > next + 1 && close < replacement.Length && replacement[close] == '}')
                {
                    var name = replacement[(next + 1)..close];
                    group = regex!.GroupNumberFromName(name);
                    if (group >= 0) { kind = ReplacementKind.Group; end = close + 1; }
                }
            }
            else if (replacement[next] is >= '0' and <= '9')
            {
                while (end < replacement.Length && replacement[end] is >= '0' and <= '9')
                {
                    if ((end & 4095) == 0) token.ThrowIfCancellationRequested();
                    end++;
                }
                group = ValidateNumericGroup(replacement.AsSpan(next, end - next), token);
                if (regex!.GroupNameFromNumber(group).Length != 0) kind = ReplacementKind.Group;
            }
            if (kind is null) continue;
            if (index > literalStart) parts.Add(new(ReplacementKind.Literal, literalStart, index - literalStart));
            parts.Add(new(kind.Value, next, kind == ReplacementKind.Literal ? 1 : 0, group));
            index = end - 1;
            literalStart = end;
        }
        if (literalStart < replacement.Length) parts.Add(new(ReplacementKind.Literal, literalStart, replacement.Length - literalStart));
        return parts;
    }

    private static bool GroupNameCharacter(char character)
        => char.IsLetterOrDigit(character) || char.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark or UnicodeCategory.ConnectorPunctuation;

    private static int ValidateNumericGroup(ReadOnlySpan<char> number, CancellationToken token)
    {
        var count = 0;
        while (count < number.Length && number[count] is >= '0' and <= '9')
        {
            if ((count & 4095) == 0) token.ThrowIfCancellationRequested();
            count++;
        }
        if (!int.TryParse(number[..count], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException("置換式のキャプチャ番号が Int32 の範囲を超えています。");
        return value;
    }

    private void Expand(StringBuilder output, string input, Match match, string replacement,
        IReadOnlyList<ReplacementPart> parts, ref long work, CancellationToken token)
    {
        foreach (var part in parts)
        {
            token.ThrowIfCancellationRequested();
            // 空 group の反復でも出力サイズと独立に処理量を制限する。
            if (++work > MaxOutput) throw new InvalidOperationException("置換式の展開処理が上限を超えました。");
            switch (part.Kind)
            {
                case ReplacementKind.Literal: Append(output, replacement.AsSpan(part.Start, part.Length), token); break;
                case ReplacementKind.Group: Append(output, match.Groups[part.Group].ValueSpan, token); break;
                case ReplacementKind.Prefix: Append(output, input.AsSpan(0, match.Index), token); break;
                case ReplacementKind.Suffix: Append(output, input.AsSpan(match.Index + match.Length), token); break;
                case ReplacementKind.LastGroup: Append(output, match.Groups[lastGroup].ValueSpan, token); break;
                case ReplacementKind.Input: Append(output, input.AsSpan(), token); break;
            }
        }
    }
}
