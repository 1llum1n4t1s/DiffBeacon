using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DiffBeacon.Core;

internal sealed partial class LineTransformExpression
{
    // PCRE2全体の代替ではない。.NETで実行できる構文に、固定原典で必要な
    // UTF scalar、ANYCRLF、capture番号、casefoldの差を補う。実行は有限timeout。
    internal sealed record RegexPattern(Regex Regex, bool BmpHanOnly, bool RawReplacementLiteral = false);
    private sealed class InvalidLineRegexException : Exception { }
    private static readonly TimeSpan LineRegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly UTF8Encoding RegexReplacementUtf8 = new(false, true);

    // 呼出し元の予算と取消をそのまま委譲し、変換・探索本体の課金順序を共用する。
    internal readonly record struct RegexWorkBudget(Action<long, CancellationToken, long> ChargeWork)
    {
        internal void Charge(long work, CancellationToken token, long output = 0) => ChargeWork(work, token, output);
    }
    internal readonly record struct RegexWorkContext(bool CaseSensitive, CancellationToken Token, RegexWorkBudget Budget);
    private static RegexWorkContext RegexContext(Context context) => new(context.CaseSensitive, context.Token, new(context.Budget.Charge));

    private sealed partial class Function
    {
        private object? EvaluateRegex(object? first, Context c)
        {
            var patternValue = arguments[1].Evaluate(c);
            long group = 0, occurrence = 0;
            if (arguments.Length >= 3)
            {
                if (arguments[2].Evaluate(c) is not long number) return null;
                group = number;
            }
            if (arguments.Length == 4)
            {
                if (arguments[3].Evaluate(c) is not long number) return null;
                occurrence = number;
            }
            if (patternValue is not (string or RegexPattern)) return null;
            var prepared = patternValue as RegexPattern;
            var attempted = prepared is not null;
            return ApplyToScalarOrArray(first, value =>
            {
                if (value is null) return null;
                var text = StringValue(value, c.Budget, c.Token);
                if (!attempted)
                {
                    prepared = PrepareRegex((string)patternValue, c);
                    attempted = true;
                }
                return prepared is null ? null : MatchRegex(text, prepared, name, group, occurrence, c);
            }, c);
        }

        private object? EvaluateRegexReplace(object? first, object? patternValue, object? replacementValue, Context c)
        {
            if (patternValue is not (string or RegexPattern) || replacementValue is not string replacement) return null;
            var prepared = patternValue as RegexPattern;
            var attempted = prepared is not null;
            return ApplyToScalarOrArray(first, value =>
            {
                if (value is null) return null;
                var text = StringValue(value, c.Budget, c.Token);
                if (!attempted)
                {
                    prepared = PrepareRegex((string)patternValue, c);
                    attempted = true;
                }
                return prepared is null ? null : ReplaceRegex(text, replacement, prepared, c);
            }, c);
        }
    }

    private static RegexPattern? PrepareRegex(string pattern, Context c, bool nonEmptyAtStart = false,
        bool carriageReturnNewline = false)
        => PrepareRegex(pattern, RegexContext(c), nonEmptyAtStart, carriageReturnNewline);

    internal static RegexPattern? PrepareRegex(string pattern, RegexWorkContext c, bool nonEmptyAtStart = false,
        bool carriageReturnNewline = false)
    {
        c.Budget.Charge(pattern.Length + 1L, c.Token);
        if (pattern.Length > MaximumTokens)
            throw new InvalidOperationException("行変換の正規表現の長さが上限を超えています。");
        try
        {
            // recontainsのPoco既定newlineはCR。既存の関数／matches用ANYCRLFと分ける。
            var translated = TranslateRegex(pattern, c, carriageReturnNewline);
            // PocoのRE_ANCHORED | RE_NOTEMPTY。終端はregexで強制せず、最初の非空match後に検査する。
            // 終端までbacktrackさせると "ab" matches "a|ab" の結果が変わる。
            if (nonEmptyAtStart) translated = "\\A(?:" + translated + ")(?!\\A)";
            c.Budget.Charge(translated.Length + 1L, c.Token, translated.Length);
            var regex = new Regex(translated, RegexOptions.CultureInvariant
                | (c.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), LineRegexTimeout);
            c.Token.ThrowIfCancellationRequested();
            return new(regex, pattern.Contains("\\p{Han}", StringComparison.Ordinal)
                || pattern.Contains("\\P{Han}", StringComparison.Ordinal), !nonEmptyAtStart && IsRawReplacementLiteral(pattern));
        }
        catch (RegexParseException) { return null; }
        catch (InvalidLineRegexException) { return null; }
    }

    private static bool SearchRegex(string subject, RegexPattern pattern, Context c)
    {
        ValidateRegexReplacementText(subject, pattern, c);
        c.Budget.Charge(subject.Length + 1L, c.Token);
        var match = pattern.Regex.Match(subject);
        c.Token.ThrowIfCancellationRequested();
        // 原典のmatch > 0はcapture数の判定。長さ0の成功も含める。
        if (match.Success && (!IsRegexScalarBoundary(subject, match.Index)
            || !IsRegexScalarBoundary(subject, match.Index + match.Length)))
            throw new NotSupportedException("この正規表現はUnicode scalarの途中をmatchします。対応する構文へ変更してください。");
        return match.Success;
    }

    private static bool MatchesWholeRegex(string subject, RegexPattern pattern, Context c)
    {
        ValidateRegexReplacementText(subject, pattern, c);
        c.Budget.Charge(subject.Length + 1L, c.Token);
        var match = pattern.Regex.Match(subject);
        c.Token.ThrowIfCancellationRequested();
        return match.Success && match.Index == 0 && match.Length == subject.Length;
    }

    private static object? MatchRegex(string text, RegexPattern pattern, string function, long group, long occurrence, Context c)
        => MatchRegex(text, pattern, function, group, occurrence, RegexContext(c));

    internal static object? MatchRegex(string text, RegexPattern pattern, string function, long group, long occurrence, RegexWorkContext c)
    {
        // 全UTF-16境界から原文UTF-8のbyte位置へ写像する。pair途中は探索・captureに使わない。
        c.Budget.Charge(checked(text.Length * 5L + 1), c.Token, checked((text.Length + 1L) * 4));
        var bytes = new int[text.Length + 1];
        var byteLength = 0;
        for (var index = 0; index < text.Length;)
        {
            c.Budget.Charge(1, c.Token);
            bytes[index] = byteLength;
            var status = Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed);
            if (status != System.Buffers.OperationStatus.Done)
                throw new ArgumentException("行変換の正規表現入力に不正なUnicodeがあります。");
            if (pattern.BmpHanOnly && rune.Value is >= 0x20000 and <= 0x323af or >= 0x16fe2 and <= 0x16ff1)
                throw new NotSupportedException("補助面のHan script文字の照合には対応していません。");
            if (consumed == 2) bytes[index + 1] = -1;
            byteLength = checked(byteLength + rune.Utf8SequenceLength);
            index += consumed;
        }
        bytes[text.Length] = byteLength;
        if (function != "regexcount" && occurrence < 0) return null;
        long count = 0;
        var search = 0;
        while (true)
        {
            // Regexの内部反復は100msの上限。探索ごとの全文走査量も共有予算に含める。
            c.Budget.Charge(text.Length - (long)search + 1, c.Token);
            var match = pattern.Regex.Match(text, search);
            c.Token.ThrowIfCancellationRequested();
            if (!match.Success) return function == "regexcount" ? count : null;
            if (bytes[match.Index] < 0 || bytes[match.Index + match.Length] < 0)
                throw new NotSupportedException("この正規表現はUnicode scalarの途中をcaptureします。対応する構文へ変更してください。");
            if (function != "regexcount" && count == occurrence)
            {
                if (group < 0 || group >= match.Groups.Count) return null;
                var capture = match.Groups[(int)group];
                if (!capture.Success) return null;
                if (bytes[capture.Index] < 0 || bytes[capture.Index + capture.Length] < 0)
                    throw new NotSupportedException("この正規表現はUnicode scalarの途中をcaptureします。");
                if (function == "regexfind") return (long)bytes[capture.Index];
                c.Budget.Charge(capture.Length, c.Token, capture.Length);
                return text.Substring(capture.Index, capture.Length);
            }
            count++;
            search = match.Index + match.Length;
            if (match.Length != 0) continue;
            // 原典はzero-lengthの直後を1 UTF-8 byte進める。scalar単位へ修正しない。
            if (search == text.Length)
            {
                if (function == "regexcount") return count;
                throw new ArgumentException("正規表現の探索位置が入力の末尾を超えています。");
            }
            if (text[search] > 0x7f)
                throw new ArgumentException("正規表現の探索位置がUTF-8の継続byteを指しています。");
            search++;
        }
    }

    private static string? ReplaceRegex(string subject, string replacement, RegexPattern pattern, Context c, bool preservePartialOnFailure = false)
        => ReplaceRegex(subject, replacement, pattern, RegexContext(c), preservePartialOnFailure);

    private static string? ReplaceRegex(string subject, string? replacement, RegexPattern pattern, RegexWorkContext c,
        bool preservePartialOnFailure = false, Func<(string Text, bool Raw)>? replacementFactory = null)
    {
        var rawReplacement = false;
        if (replacement is not null) _ = RegexReplacementCharacterCount(replacement, c, false);
        var text = subject;
        var search = 0;
        while (search < text.Length)
        {
            ValidateRegexReplacementText(text, pattern, c, rawReplacement ? search : 0);
            c.Budget.Charge(text.Length - (long)search + 1, c.Token);
            var match = pattern.Regex.Match(text, search);
            c.Token.ThrowIfCancellationRequested();
            if (!match.Success || match.Index >= text.Length) return text;
            if (!IsRegexScalarBoundary(text, match.Index)
                || !IsRegexScalarBoundary(text, match.Index + match.Length))
                throw new NotSupportedException("この正規表現はUnicode scalarの途中をmatchします。対応する構文へ変更してください。");

            if (replacement is null)
            {
                var supplied = replacementFactory!(); replacement = supplied.Text; rawReplacement = supplied.Raw;
                _ = RegexReplacementCharacterCount(replacement, c, rawReplacement);
            }
            var captureLimit = 1;
            for (var index = 1; index < match.Groups.Count; index++)
            {
                c.Budget.Charge(1, c.Token);
                if (match.Groups[index].Success) captureLimit = index + 1;
            }
            var replacementLength = RegexReplacementLength(replacement, text, match, captureLimit, c);
            var zeroWidth = match.Length == 0;
            if (zeroWidth && text[match.Index] > 0x7f)
            {
                // PocoはUTF-8 byte単位で1byteを消費し、次のPCRE呼出しで不正UTF-8としてnone化する。
                if (preservePartialOnFailure && replacementFactory is not null)
                {
                    // byteを保持するbridgeではsubstOneが公開済みの部分本文を再現する。
                    // 次のruleも不正UTF-8 subjectを拒否するため、残った継続byteを修復しない。
                    var prefixLength = checked(match.Index + replacementLength);
                    c.Budget.Charge(prefixLength + 1L, c.Token, prefixLength);
                    var prefix = string.Create(prefixLength, (text, replacement, match, captureLimit, c),
                        static (destination, state) =>
                        {
                            var position = 0;
                            CopyRegexReplacementSpan(state.text.AsSpan(0, state.match.Index), destination, ref position, state.c);
                            WriteRegexReplacement(state.replacement, state.text, state.match, state.captureLimit, destination, ref position, state.c);
                        });
                    var prefixBytes = EncodeRegexReplacementResult(prefix, c);
                    c.Budget.Charge(text.Length - (long)match.Index, c.Token, text.Length - (long)match.Index);
                    var suffixBytes = EncodeRegexReplacementResult(text[match.Index..], c);
                    var partialLength = checked(prefixBytes.Length + suffixBytes.Length - 1);
                    c.Budget.Charge(partialLength + 1L, c.Token, partialLength);
                    var partial = new byte[partialLength]; prefixBytes.CopyTo(partial, 0); suffixBytes.AsSpan(1).CopyTo(partial.AsSpan(prefixBytes.Length));
                    return DecodeRegexReplacement(partial, c).Text;
                }
                return preservePartialOnFailure ? text : null;
            }
            var skipped = zeroWidth ? 1 : 0;
            var suffixStart = checked(match.Index + match.Length + skipped);
            var outputLength = checked((long)match.Index + replacementLength + text.Length - suffixStart);
            if (outputLength > int.MaxValue)
                throw new InvalidOperationException("行変換の正規表現置換結果が文字数上限を超えています。");

            // 共有予算と出力上限は確定長が判明してから、stringを割り当てる前に消費する。
            c.Budget.Charge(checked(outputLength + 1), c.Token, outputLength);
            var nextText = string.Create((int)outputLength,
                (text, replacement, match, captureLimit, suffixStart, c),
                static (destination, state) =>
                {
                    var position = 0;
                    CopyRegexReplacementSpan(state.text.AsSpan(0, state.match.Index), destination, ref position, state.c);
                    WriteRegexReplacement(state.replacement, state.text, state.match, state.captureLimit, destination, ref position, state.c);
                    CopyRegexReplacementSpan(state.text.AsSpan(state.suffixStart), destination, ref position, state.c);
                });
            search = checked(match.Index + replacementLength);
            text = nextText;
        }
        return text;
    }

    private static void ValidateRegexReplacementText(string text, RegexPattern pattern, Context c)
        => ValidateRegexReplacementText(text, pattern, RegexContext(c));

    private static void ValidateRegexReplacementText(string text, RegexPattern pattern, RegexWorkContext c, int start = 0)
    {
        for (var index = start; index < text.Length;)
        {
            c.Budget.Charge(1, c.Token);
            var scalar = ReadScalar(text, ref index);
            if (pattern.BmpHanOnly && scalar is >= 0x20000 and <= 0x323af or >= 0x16fe2 and <= 0x16ff1)
                throw new NotSupportedException("補助面のHan script文字の照合には対応していません。");
        }
    }

    private static int RegexReplacementLength(string replacement, string subject, Match match, int captureLimit, RegexWorkContext c)
    {
        long length = 0;
        for (var index = 0; index < replacement.Length; index++)
        {
            c.Budget.Charge(1, c.Token);
            if (replacement[index] != '$') { length++; continue; }
            if (index + 1 >= replacement.Length) { length++; continue; }
            var following = replacement[index + 1];
            if (following is < '0' or > '9') { length += 2; index++; continue; }
            var captureIndex = following - '0';
            index++;
            if (captureIndex >= captureLimit) continue;
            var capture = match.Groups[captureIndex];
            if (!capture.Success)
                throw new ArgumentOutOfRangeException(nameof(replacement), "正規表現置換のcapture位置が現在の入力範囲外です。");
            if (!IsRegexScalarBoundary(subject, capture.Index)
                || !IsRegexScalarBoundary(subject, capture.Index + capture.Length))
                throw new NotSupportedException("この正規表現はUnicode scalarの途中をcaptureします。");
            length = checked(length + capture.Length);
            if (length > int.MaxValue)
                throw new InvalidOperationException("行変換の正規表現置換結果が文字数上限を超えています。");
        }
        return (int)length;
    }

    private static bool IsRegexScalarBoundary(string text, int index)
        => index >= 0 && index <= text.Length
            && (index == 0 || index == text.Length
                || !char.IsHighSurrogate(text[index - 1]) || !char.IsLowSurrogate(text[index]));

    private static void WriteRegexReplacement(string replacement, string subject, Match match, int captureLimit,
        Span<char> destination, ref int position, RegexWorkContext c)
    {
        for (var index = 0; index < replacement.Length; index++)
        {
            c.Budget.Charge(0, c.Token);
            if (replacement[index] != '$')
            {
                destination[position++] = replacement[index];
                continue;
            }
            if (index + 1 >= replacement.Length)
            {
                destination[position++] = '$';
                continue;
            }
            var following = replacement[index + 1];
            if (following is < '0' or > '9')
            {
                destination[position++] = '$';
                destination[position++] = following;
                index++;
                continue;
            }
            var captureIndex = following - '0';
            index++;
            if (captureIndex >= captureLimit) continue;
            var capture = match.Groups[captureIndex];
            if (!capture.Success) throw new ArgumentOutOfRangeException(nameof(replacement));
            CopyRegexReplacementSpan(subject.AsSpan(capture.Index, capture.Length), destination, ref position, c);
        }
    }

    private static void CopyRegexReplacementSpan(ReadOnlySpan<char> source, Span<char> destination, ref int position, RegexWorkContext c)
    {
        for (var offset = 0; offset < source.Length; offset += 256)
        {
            c.Budget.Charge(0, c.Token);
            var length = Math.Min(256, source.Length - offset);
            source.Slice(offset, length).CopyTo(destination[position..]);
            position += length;
        }
    }

    // raw replacementは最初の実matchでだけ解釈する。通常String経路のloopとtoken処理を共用する。
    internal static byte[]? ReplaceRegexUtf8(string subject, byte[] replacement, RegexPattern pattern, RegexWorkContext c, bool preservePartialOnFailure = false)
    {
        (string Text, bool Raw) PrepareReplacement()
        {
            var value = DecodeRegexReplacement(replacement, c);
            if (value.Raw && !pattern.RawReplacementLiteral)
                throw new NotSupportedException("不正UTF-8のreplacementはliteral pattern（空またはescaped punctuationを含む）に限ります。");
            return value;
        }
        var result = ReplaceRegex(subject, null, pattern, c, preservePartialOnFailure, replacementFactory: PrepareReplacement);
        return result is null ? null : EncodeRegexReplacementResult(result, c);
    }

    // assertion不在という推測ではなく、元patternがliteralである場合だけmax-lookbehind=0と証明する。
    private static bool IsRawReplacementLiteral(string pattern)
    {
        const string escaped = @"\.^$|?*+(){}[]-# ";
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == (char)92)
            {
                if (++index >= pattern.Length || !escaped.Contains(pattern[index])) return false;
            }
            else if (".^$|?*+(){}[]".Contains(character)) return false;
        }
        return true;
    }

    private static (string Text, bool Raw) DecodeRegexReplacement(byte[] bytes, RegexWorkContext c)
    {
        c.Budget.Charge(bytes.Length + 1L, c.Token);
        try
        {
            var length = RegexReplacementUtf8.GetCharCount(bytes);
            c.Budget.Charge(0, c.Token, length);
            return (RegexReplacementUtf8.GetString(bytes), false);
        }
        catch (DecoderFallbackException)
        {
            // 不正byteだけを孤立low-surrogateへ可逆に退避する。正規のUTF-8 scalarとは衝突しない。
            c.Budget.Charge(0, c.Token, bytes.Length);
            var text = new StringBuilder(bytes.Length);
            for (var index = 0; index < bytes.Length;)
            {
                if (Rune.DecodeFromUtf8(bytes.AsSpan(index), out var rune, out var consumed) == System.Buffers.OperationStatus.Done)
                { text.Append(rune.ToString()); index += consumed; }
                else { text.Append((char)(0xdc00 + bytes[index++])); }
            }
            c.Budget.Charge(text.Length + 1L, c.Token, text.Length);
            return (text.ToString(), true);
        }
    }

    private static byte[] EncodeRegexReplacementResult(string text, RegexWorkContext c)
    {
        c.Budget.Charge(text.Length, c.Token);
        var length = 0;
        for (var index = 0; index < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed) == System.Buffers.OperationStatus.Done)
            { length = checked(length + rune.Utf8SequenceLength); index += consumed; }
            else if (text[index] is >= '\udc80' and <= '\udcff') { length = checked(length + 1); index++; }
            else throw InvalidUnicode();
        }
        c.Budget.Charge(length, c.Token, length);
        var bytes = new byte[length]; var position = 0;
        for (var index = 0; index < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed) == System.Buffers.OperationStatus.Done)
            { position += rune.EncodeToUtf8(bytes.AsSpan(position)); index += consumed; }
            else { bytes[position++] = (byte)(text[index++] - 0xdc00); }
        }
        return bytes;
    }

    private static long RegexReplacementCharacterCount(string text, RegexWorkContext c, bool raw)
    {
        long count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            c.Budget.Charge(1, c.Token);
            var character = text[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1])) throw InvalidUnicode();
                index++;
            }
            else if (char.IsLowSurrogate(character) && !(raw && character is >= '\udc80' and <= '\udcff')) throw InvalidUnicode();
            count++;
        }
        return count;
    }

    private readonly record struct RegexFlags(bool Multiline, bool Singleline, bool IgnoreCase, bool IgnoreSpace);

    // 固定PCRE2のUnicode 16.0.0 UCD_OTHERCASE。補助面に複数文字caseless setはない。
    // Externals/poco/dependencies/pcre2/src/pcre2_ucd.c のSHA-256:
    // 0fcaf2440147c666a747b3fa3b108012ac30db84cc554d852dfb76b98893716b
    // 原典のLF版: 6ebdc9043dcb13832189a45818f767afe9c4d6df078fb37db6d55d80126dce92
    // RuneのOS/.NET依存の大小文字表ではなく、原典の全564方向を22区間で固定する。
    private static int SupplementaryRegexOtherCase(int scalar) => scalar switch
    {
        >= 0x10400 and <= 0x10427 or >= 0x104B0 and <= 0x104D3 => scalar + 40,
        >= 0x10428 and <= 0x1044F or >= 0x104D8 and <= 0x104FB => scalar - 40,
        >= 0x10570 and <= 0x1057A or >= 0x1057C and <= 0x1058A
            or >= 0x1058C and <= 0x10592 or >= 0x10594 and <= 0x10595 => scalar + 39,
        >= 0x10597 and <= 0x105A1 or >= 0x105A3 and <= 0x105B1
            or >= 0x105B3 and <= 0x105B9 or >= 0x105BB and <= 0x105BC => scalar - 39,
        >= 0x10C80 and <= 0x10CB2 => scalar + 64,
        >= 0x10CC0 and <= 0x10CF2 => scalar - 64,
        >= 0x10D50 and <= 0x10D65 or >= 0x118A0 and <= 0x118BF
            or >= 0x16E40 and <= 0x16E5F => scalar + 32,
        >= 0x10D70 and <= 0x10D85 or >= 0x118C0 and <= 0x118DF
            or >= 0x16E60 and <= 0x16E7F => scalar - 32,
        >= 0x1E900 and <= 0x1E921 => scalar + 34,
        >= 0x1E922 and <= 0x1E943 => scalar - 34,
        _ => scalar
    };

    private static string TranslateRegex(string source, RegexWorkContext c, bool carriageReturnNewline = false)
    {
        var result = new StringBuilder();
        var flags = new RegexFlags(false, false, !c.CaseSensitive, false);
        var stack = new Stack<RegexFlags>();
        var capture = 0;
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        // 全captureに明示番号を付け、.NETの「namedはunnamedの後」の番号付けを避ける。
        for (var index = 0; index < source.Length; index++)
        {
            c.Budget.Charge(1, c.Token);
            var ch = source[index];
            if (flags.IgnoreSpace && ch == '#')
            {
                while (index < source.Length && source[index] != (carriageReturnNewline ? '\r' : '\n')) { c.Budget.Charge(1, c.Token); index++; }
                result.Append('\n');
                continue;
            }
            if (ch == '\\')
            {
                if (++index == source.Length) { result.Append('\\'); break; }
                var escaped = source[index];
                if (carriageReturnNewline && escaped == 'Z')
                {
                    result.Append("(?:\\z|(?=\\r\\z))");
                    continue;
                }
                if (escaped is 'p' or 'P' && index + 1 < source.Length && source[index + 1] == '{')
                {
                    var end = source.IndexOf('}', index + 2);
                    if (end >= 0 && source.AsSpan(index + 2, end - index - 2).SequenceEqual("Han"))
                    {
                        // 固定原典のHan文字種。補助面scriptは.NETの文字種表と異なるため明示拒否。
                        result.Append(escaped == 'p' ? "[\\u2E80-\\u2E99\\u2E9B-\\u2EF3\\u2F00-\\u2FD5\\u3005\\u3007\\u3021-\\u3029\\u3038-\\u303B\\u3400-\\u4DBF\\u4E00-\\u9FFF\\uF900-\\uFA6D\\uFA70-\\uFAD9]"
                            : "[^\\u2E80-\\u2E99\\u2E9B-\\u2EF3\\u2F00-\\u2FD5\\u3005\\u3007\\u3021-\\u3029\\u3038-\\u303B\\u3400-\\u4DBF\\u4E00-\\u9FFF\\uF900-\\uFA6D\\uFA70-\\uFAD9]");
                        index = end;
                        continue;
                    }
                    if (end >= 0)
                    {
                        result.Append('\\').Append(source.AsSpan(index, end - index + 1));
                        index = end;
                        continue;
                    }
                }
                if (escaped == 'k' && index + 1 < source.Length && source[index + 1] is '<' or '\'')
                {
                    var end = source.IndexOf(source[index + 1] == '<' ? '>' : '\'', index + 2);
                    if (end >= 0)
                    {
                        result.Append('\\').Append(source.AsSpan(index, end - index + 1));
                        index = end;
                        continue;
                    }
                }
                result.Append('\\').Append(escaped);
                continue;
            }
            if (ch == '[')
            {
                var begin = index;
                var escaped = false;
                for (index++; index < source.Length; index++)
                {
                    c.Budget.Charge(1, c.Token);
                    if (!escaped && source[index] == ']' && index > begin + 1) break;
                    if (!escaped && source[index] == '\\') escaped = true;
                    else escaped = false;
                }
                if (index == source.Length) { result.Append(source.AsSpan(begin)); break; }
                var body = source.Substring(begin + 1, index - begin - 1);
                if (body.Contains("\\p{Han}", StringComparison.Ordinal) || body.Contains("\\P{Han}", StringComparison.Ordinal))
                    throw new NotSupportedException("文字class内のHan script正規表現には対応していません。");
                result.Append('[').Append(body);
                if (flags.IgnoreCase)
                {
                    // escape・range・否定classを文字列Containsで判定すると、[\s]まで
                    // sのliteralとして広げてしまう。否定の内側の集合に対して閉包を取る。
                    var members = body.StartsWith('^') ? body[1..] : body;
                    c.Budget.Charge(members.Length + 1L, c.Token);
                    var set = new Regex("\\A[" + members + "]\\z", RegexOptions.CultureInvariant
                        | RegexOptions.IgnoreCase, LineRegexTimeout);
                    if (set.IsMatch("s") || set.IsMatch("ſ")) result.Append('s').Append('ſ');
                    if (set.IsMatch("σ") || set.IsMatch("ς")) result.Append('σ').Append('ς');
                    c.Token.ThrowIfCancellationRequested();
                }
                result.Append(']');
                continue;
            }
            if (ch == '(')
            {
                stack.Push(flags);
                if (stack.Count > MaximumDepth)
                    throw new InvalidOperationException("行変換の正規表現の深さが上限を超えています。");
                if (index + 1 >= source.Length || source[index + 1] != '?')
                {
                    result.Append("(?<").Append(++capture).Append('>');
                    continue;
                }
                var start = index + 2;
                if (start < source.Length && source[start] == '#')
                {
                    var end = source.IndexOf(')', start);
                    if (end < 0) { result.Append(source.AsSpan(index)); break; }
                    result.Append(source.AsSpan(index, end - index + 1));
                    index = end;
                    flags = stack.Pop();
                    continue;
                }
                if (start < source.Length && source[start] is '<' or '\''
                    && start + 1 < source.Length && source[start + 1] is not ('=' or '!'))
                {
                    var end = source.IndexOf(source[start] == '<' ? '>' : '\'', start + 1);
                    if (end >= 0)
                    {
                        var name = source.Substring(start + 1, end - start - 1);
                        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_')
                            || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
                            throw new InvalidLineRegexException();
                        if (!names.TryAdd(name, ++capture)) throw new InvalidLineRegexException();
                        result.Append("(?<").Append(capture).Append('>');
                        index = end;
                        continue;
                    }
                }
                var cursor = start;
                var enable = true;
                while (cursor < source.Length && source[cursor] is 'i' or 'm' or 's' or 'x' or '-')
                {
                    var option = source[cursor++];
                    if (option == '-') { enable = false; continue; }
                    flags = option switch
                    {
                        'i' => flags with { IgnoreCase = enable }, 'm' => flags with { Multiline = enable },
                        's' => flags with { Singleline = enable }, _ => flags with { IgnoreSpace = enable }
                    };
                }
                if (cursor > start && cursor < source.Length && source[cursor] is ':' or ')')
                {
                    result.Append(source.AsSpan(index, cursor - index + 1));
                    index = cursor;
                    if (source[cursor] == ')') stack.Pop();
                    continue;
                }
                result.Append('(');
                continue;
            }
            if (ch == ')')
            {
                if (stack.Count > 0) flags = stack.Pop();
                result.Append(ch);
            }
            else if (ch == '.') result.Append(flags.Singleline
                ? "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\uD800-\\uDFFF])"
                : carriageReturnNewline ? "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\r\\uD800-\\uDFFF])"
                : "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\r\\n\\uD800-\\uDFFF])");
            else if (ch == '^') result.Append(!flags.Multiline ? "\\A"
                : carriageReturnNewline ? "(?:\\A|(?<=\\r)(?!\\z))" : "(?:\\A|(?<=\\n)|(?<=\\r)(?!\\n))");
            else if (ch == '$') result.Append(carriageReturnNewline
                ? flags.Multiline ? "(?:\\z|(?=\\r))" : "(?:\\z|(?=\\r\\z))"
                : flags.Multiline ? "(?:\\z|(?=\\r)|(?<!\\r)(?=\\n))" : "(?:\\z|(?=\\r?\\n\\z)|(?=\\r\\z))");
            else if (flags.IgnoreCase && ch is 's' or 'S' or 'ſ') result.Append("[sſ]");
            else if (flags.IgnoreCase && ch is 'Σ' or 'σ' or 'ς') result.Append("[σς]");
            else if (char.IsHighSurrogate(ch) && index + 1 < source.Length && char.IsLowSurrogate(source[index + 1]))
            {
                var low = source[++index];
                c.Budget.Charge(1, c.Token);
                var scalar = char.ConvertToUtf32(ch, low);
                var other = flags.IgnoreCase ? SupplementaryRegexOtherCase(scalar) : scalar;
                // .NET IgnoreCaseはsurrogate pairをfoldしない。両側をscalarのまま
                // 非捕捉groupへ入れ、後続quantifierと元のcapture番号を維持する。
                result.Append("(?:").Append(ch).Append(low);
                if (other != scalar) result.Append('|').Append(char.ConvertFromUtf32(other));
                result.Append(')');
            }
            else result.Append(ch);
        }
        // 名前付きbackreferenceも、元の出現順のcapture番号へ戻す。
        var translated = result.ToString();
        foreach (var pair in names)
        {
            c.Budget.Charge(translated.Length, c.Token);
            translated = translated.Replace("\\k<" + pair.Key + ">", "\\k<" + pair.Value.ToString(CultureInfo.InvariantCulture) + ">", StringComparison.Ordinal)
                .Replace("\\k'" + pair.Key + "'", "\\k<" + pair.Value.ToString(CultureInfo.InvariantCulture) + ">", StringComparison.Ordinal);
        }
        return translated;
    }
}
