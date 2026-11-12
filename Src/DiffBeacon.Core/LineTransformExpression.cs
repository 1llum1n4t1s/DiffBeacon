using System.Globalization;
using System.Text;

namespace DiffBeacon.Core;

/// <summary>旧FilterParserの行値・配列演算。未移植構文は解析時に明示拒否する。</summary>
internal sealed partial class LineTransformExpression
{
    internal const int MaximumDepth = 128;
    internal const int MaximumTokens = 16_384;
    internal const int MaximumArrayElements = 16_384;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Node root;
    private readonly bool caseSensitive;
    internal bool UsesAggregates { get; }
    internal bool UsesMatchRanges { get; }
    internal bool UsesContent { get; }
    internal bool UsesSublines { get; }
    private LineTransformExpression(Node root, bool caseSensitive, bool usesAggregates, bool usesMatchRanges, bool usesContent, bool usesSublines)
    { this.root = root; this.caseSensitive = caseSensitive; UsesAggregates = usesAggregates; UsesMatchRanges = usesMatchRanges; UsesContent = usesContent; UsesSublines = usesSublines; }

    internal static LineTransformExpression Compile(string expression, LineTransformBudget budget, CancellationToken token,
        ILineTransformTimestampEnvironment? timestampEnvironment = null, ILineTransformStringEnvironment? stringEnvironment = null,
        ILineTransformLogEnvironment? logEnvironment = null, ILineTransformListEnvironment? listEnvironment = null)
    {
        var snapshot = CaptureTimestampClock(timestampEnvironment, budget, token);
        var parser = new Parser(expression, budget, token, timestampEnvironment, snapshot, stringEnvironment, logEnvironment, listEnvironment);
        var root = parser.Parse();
        if (parser.Optimize) root = root.Optimize(new(null, 0, parser.CaseSensitive, budget, token));
        return new(root, parser.CaseSensitive, parser.UsesAggregates, parser.UsesMatchRanges, parser.UsesContent, parser.UsesSublines);
    }

    internal static IEnumerable<LineTransformListDependency> InspectListDependencies(string expression, LineTransformBudget budget,
        CancellationToken token, ILineTransformTimestampEnvironment? timestampEnvironment, ILineTransformStringEnvironment? stringEnvironment)
    {
        var clock = CaptureTimestampClock(timestampEnvironment, budget, token);
        return new Parser(expression, budget, token, timestampEnvironment, clock, stringEnvironment, null, null).Parse().ListDependencies();
    }

    internal string Evaluate(LineTransformProvider provider, int lineIndex, EvaluationState? state = null)
    {
        if ((UsesAggregates || UsesMatchRanges) && state is null)
        {
            provider.Budget.Charge(1, provider.Token);
            state = new();
        }
        return StringValue(root.Evaluate(new(provider, lineIndex, caseSensitive, provider.Budget, provider.Token, state)),
            provider.Budget, provider.Token);
    }

    private sealed record Context(LineTransformProvider? Provider, int LineIndex, bool CaseSensitive,
        LineTransformBudget Budget, CancellationToken Token, EvaluationState? State = null);

    private abstract class Node(int height)
    {
        internal int Height { get; } = height <= MaximumDepth ? height
            : throw new ArgumentException("行変換の式の深さが上限を超えています。");
        internal abstract object? Evaluate(Context context);
        internal virtual string? ConstantString() => null;
        internal virtual IEnumerable<LineTransformListDependency> ListDependencies() => [];
        internal virtual Node Optimize(Context context) { context.Budget.Charge(1, context.Token); return this; }
    }

    // 数値constructorは整数値でも、原典のIntLiteral専用foldとは区別する。
    private enum LiteralOrigin { Ordinary, Size, Duration, Version, Timestamp }

    private sealed class Literal(object? value, LiteralOrigin origin = LiteralOrigin.Ordinary) : Node(1)
    {
        internal object? Value { get; } = value;
        internal LiteralOrigin Origin { get; } = origin;
        internal override string? ConstantString() => Value as string;
        internal override object? Evaluate(Context context)
        {
            context.Budget.Charge(1, context.Token);
            return Value;
        }
    }

    private sealed class Field(string name, int column = 0) : Node(1)
    {
        internal override object? Evaluate(Context c)
        {
            c.Budget.Charge(1, c.Token);
            var provider = c.Provider!;
            return name switch
            {
                "line" => provider.Line(c.LineIndex),
                "content" => provider.Content,
                "linenumber" => (long)c.LineIndex + 1,
                "linelength" => CharacterCount(provider.Line(c.LineIndex)!, c),
                "columncount" => (long)provider.ColumnCount(c.LineIndex),
                "column" => provider.Column(c.LineIndex, column),
                _ => throw new ArgumentException("未対応の行フィールドです。")
            };
        }
    }

    private sealed class Unary(string op, Node operand) : Node(operand.Height + 1)
    {
        internal override IEnumerable<LineTransformListDependency> ListDependencies() => operand.ListDependencies();
        internal override object? Evaluate(Context c)
        {
            c.Budget.Charge(1, c.Token);
            var value = operand.Evaluate(c);
            if (op == "not") return BooleanValue(value, c) is bool boolean ? !boolean : null;
            return value switch { long integer => checked(-integer), double real => Finite(-real), _ => null };
        }
        internal override Node Optimize(Context c)
        {
            c.Budget.Charge(1, c.Token);
            var child = operand.Optimize(c);
            var node = new Unary(op, child);
            return child is Literal literal && (op == "not" ? literal.Value is bool : literal.Value is long or double)
                ? new Literal(node.Evaluate(c)) : node;
        }
    }

    private sealed class Binary(string op, Node left, Node right) : Node(Math.Max(left.Height, right.Height) + 1)
    {
        internal string Operator => op;
        internal Node Left => left;
        internal Node Right => right;
        internal override string? ConstantString() => op == "+" && left.ConstantString() is { } a && right.ConstantString() is { } b ? a + b : null;
        internal override IEnumerable<LineTransformListDependency> ListDependencies() => left.ListDependencies().Concat(right.ListDependencies());
        internal override object? Evaluate(Context c)
        {
            c.Budget.Charge(1, c.Token);
            var l = left.Evaluate(c);
            if (op == "and")
            {
                if (BooleanValue(l, c) is not bool lb) return null;
                if (!lb) return false;
                return BooleanValue(right.Evaluate(c), c) is bool rb ? rb : null;
            }
            if (op == "or")
            {
                var lb = BooleanValue(l, c);
                if (lb is true) return true;
                var rb = BooleanValue(right.Evaluate(c), c);
                if (rb is true) return true;
                return lb is not null || rb is not null ? false : null;
            }
            return Compute(op, l, right.Evaluate(c), c);
        }

        internal override Node Optimize(Context c)
        {
            c.Budget.Charge(1, c.Token);
            var l = left.Optimize(c);
            var r = right.Optimize(c);
            if (op is "matches" or "recontains" && r is Literal { Value: string regexText })
            {
                // 旧BinaryOpNodeの定数patternは、左の型や実行時の到達性に先立って検査する。
                var pattern = PrepareRegex(regexText, c, nonEmptyAtStart: op == "matches", carriageReturnNewline: op == "recontains")
                    ?? throw new ArgumentException($"行変換の{op}に不正な正規表現があります。");
                return new Binary(op, l, new Literal(pattern));
            }
            var node = new Binary(op, l, r);
            if (l is not Literal ll || r is not Literal rr) return node;
            if (ll.Value is TimestampValue && ((rr.Value is TimestampValue && op is "=" or "!=" or "<" or "<=" or ">" or ">=" or "-")
                || (rr.Value is long && op is "+" or "-")))
            {
                var folded = node.Evaluate(c);
                return new Literal(folded, folded is TimestampValue ? LiteralOrigin.Timestamp : LiteralOrigin.Ordinary);
            }
            // 旧Optimizeは同じ型のliteralだけをfoldする。混在型は実行時のまま。
            var sameType = ll.Value is long && rr.Value is long || ll.Value is double && rr.Value is double
                || ll.Value is string && rr.Value is string || ll.Value is bool && rr.Value is bool;
            var canFold = op is "and" or "or" ? ll.Value is bool && rr.Value is bool
                : sameType && (op is "=" or "!=" or "<" or "<=" or ">" or ">="
                    || ll.Value is long && op is "+" or "-" or "*" or "/" or "%"
                    || ll.Value is double && op is "+" or "-" or "*" or "/"
                    || ll.Value is string && op == "+"
                    || ll.Value is bool && op is "+" or "-");
            if (canFold && op == "-" && ll.Value is bool lb && rr.Value is bool rb)
                return new Literal((lb ? 1L : 0L) - (rb ? 1L : 0L));
            return canFold ? new Literal(node.Evaluate(c)) : node;
        }
    }

    private sealed partial class Function(string name, Node[] arguments, ColumnRangeSegment[]? cachedRanges = null, bool optimizedReplaceNeedle = false,
        ILineTransformTimestampEnvironment? timestampEnvironment = null, TimestampClockSnapshot? timestampClock = null,
        ILineTransformStringEnvironment? stringEnvironment = null, ILineTransformLogEnvironment? logEnvironment = null,
        string filterName = "", ILineTransformListEnvironment? listEnvironment = null)
        : Node(1 + (arguments.Length == 0 ? 0 : arguments.Max(argument => argument.Height)))
    {
        private string FunctionName => name;
        private Node[] Arguments => arguments;
        internal override IEnumerable<LineTransformListDependency> ListDependencies()
        {
            foreach (var dependency in arguments.SelectMany(argument => argument.ListDependencies())) yield return dependency;
            if (name is not ("replacewithlist" or "regexreplacewithlist")) yield break;
            foreach (var dependency in ListCandidates(arguments[1])) yield return dependency;
        }

        private static IEnumerable<LineTransformListDependency> ListCandidates(Node value, bool stringConcatenation = false)
        {
            if (value.ConstantString() is { } constant) { yield return new(constant, false); yield break; }
            if (value is Literal literal)
            {
                if (literal.Value is string path) yield return new(path, false);
                else if (stringConcatenation) yield return new(null, true);
                yield break;
            }
            if (value is Binary { Operator: "+" } binary)
            {
                var left = ListCandidates(binary.Left, true).ToArray(); var right = ListCandidates(binary.Right, true).ToArray();
                // 非stringのliteralは単独ではpathでないが、+では文字列化され得る。候補欠落を許さない。
                if (left.Length == 0 || right.Length == 0) { yield return new(null, true); yield break; }
                if (left.Any(item => item.Dynamic) || right.Any(item => item.Dynamic)) { yield return new(null, true); yield break; }
                if ((long)left.Length * right.Length > MaximumArrayElements)
                    throw new InvalidOperationException("置換リストの候補パス数が上限を超えています。");
                foreach (var a in left) foreach (var b in right) yield return new(a.Path + b.Path, false);
                yield break;
            }
            if (value is Function function)
            {
                if (function.FunctionName == "array")
                { if (stringConcatenation) yield return new(null, true); yield break; }
                if (function.FunctionName == "at" && function.Arguments[0] is Function { FunctionName: "array" } array)
                {
                    foreach (var element in array.Arguments)
                        foreach (var dependency in ListCandidates(element, stringConcatenation)) yield return dependency;
                    // Atがnoneを返す場合、文字列連結では空のpath区間として扱う。
                    var hasKnownInRangeIndex = function.Arguments[1] is Literal { Value: long index }
                        && index >= -(long)array.Arguments.Length && index < array.Arguments.Length;
                    if (stringConcatenation && !hasKnownInRangeIndex) yield return new(string.Empty, false);
                    yield break;
                }
                if (function.FunctionName == "if")
                {
                    foreach (var branch in function.Arguments.Skip(1))
                        foreach (var dependency in ListCandidates(branch, stringConcatenation)) yield return dependency;
                    yield break;
                }
                if (function.FunctionName == "choose")
                {
                    foreach (var branch in function.Arguments.Skip(1))
                        foreach (var dependency in ListCandidates(branch, stringConcatenation)) yield return dependency;
                    yield break;
                }
            }
            yield return new(null, true);
        }

        internal override object? Evaluate(Context c)
        {
            c.Budget.Charge(1, c.Token);
            if (name is "now" or "today")
            {
                var snapshot = timestampClock ?? throw new ArgumentException("Now/Todayには明示的な時計環境が必要です。");
                return name == "now" ? snapshot.Now : snapshot.Today;
            }
            if (name is "logerror" or "logwarn" or "loginfo") return EvaluateLog(c);
            // 選択は旧関数ごとのlazy/eager境界を保つ。Optimizeは全子を先に処理する。
            if (name is "average" or "sum" or "maximum" or "minimum")
                return EvaluateAggregate(name, arguments[0], arguments.Length == 2 ? arguments[1] : null, c);
            if (name == "matchcontext") return EvaluateMatchContext(arguments[0], arguments[1], arguments[2], c);
            if (name == "matchinside") return EvaluateMatchInside(arguments[0], arguments[1], c);
            if (name is "matchblockcount" or "matchblocknumber" or "matchblocksize" or "matchblockoffset")
                return EvaluateMatchBlock(name, arguments[0], c);
            if (name is "matchcount" or "matchnumber" or "matchdistance" or "matchdistancebefore" or "matchdistanceafter")
                return EvaluateMatchMetric(name, arguments[0], c);
            if (name == "if") return arguments[BooleanValue(arguments[0].Evaluate(c), c) is true ? 1 : 2].Evaluate(c);
            if (name == "array") return CreateArray(arguments.Length, index => arguments[index].Evaluate(c), c);
            if (name == "substr")
            {
                // 原典はstart/lengthを先に評価し、不正startなら文字列の式を評価しない。
                var startValue = arguments[1].Evaluate(c);
                var lengthValue = arguments.Length == 3 ? arguments[2].Evaluate(c) : null;
                if (startValue is not long begin) return null;
                return ApplyToScalarOrArray(arguments[0].Evaluate(c),
                    value => value is null ? null : ByteSubstring(StringValue(value, c.Budget, c.Token), begin, lengthValue as long?, c), c);
            }
            if (name is "inrange" or "iswithin")
            {
                // 原典は下限・上限・値の順で、型検査より先に全引数を評価する。
                var lower = arguments[1].Evaluate(c);
                var upper = arguments[2].Evaluate(c);
                return ApplyToScalarOrArray(arguments[0].Evaluate(c), value => InRange(value, lower, upper, c, name == "iswithin"), c);
            }
            if (name == "normalizeunicode")
            {
                // 原典はformを先に評価。不正string/intは本文を評価せずnoneを返す。
                var formValue = arguments.Length == 2 ? arguments[1].Evaluate(c) : null;
                var form = UnicodeNormalizationForm(formValue);
                if (form is null) return null;
                return ApplyToScalarOrArray(arguments[0].Evaluate(c),
                    value => NormalizeUnicode(value, form.Value, stringEnvironment, c), c);
            }
            var first = arguments[0].Evaluate(c);
            if (name is "replacewithlist" or "regexreplacewithlist")
                return EvaluateReplaceList(first, arguments[1].Evaluate(c), c);
            if (name is "regexfind" or "regexcount" or "regexextract") return EvaluateRegex(first, c);
            if (name == "regexreplace")
            {
                // 原典は本文・pattern・replacementを左からすべて評価してから型を検査する。
                var pattern = arguments[1].Evaluate(c);
                var replacement = arguments[2].Evaluate(c);
                return EvaluateRegexReplace(first, pattern, replacement, c);
            }
            if (name == "linecount")
                return ApplyToScalarOrArray(first, value => value is LineTransformContent content ? (long)content.LineCount : null, c);
            if (name == "sublines")
            {
                // 原典はcontent/start/lenを全て評価してからstartの型を検査する。
                var start = arguments[1].Evaluate(c);
                var length = arguments.Length == 3 ? arguments[2].Evaluate(c) : null;
                if (start is not long begin) return null;
                return ApplyToScalarOrArray(first, value => value is LineTransformContent content
                    ? content.Sublines(begin, length is long size ? size : -1, c.Budget, c.Token) : null, c);
            }
            if (name is "strcount" or "strfind")
            {
                var needleValue = arguments[1].Evaluate(c);
                // 原典は不正needleでも第三引数まで先に評価する。
                var occurrenceValue = name == "strfind" && arguments.Length == 3 ? arguments[2].Evaluate(c) : 0L;
                if (name == "strfind" && occurrenceValue is not long) return null;
                if (needleValue is not string needle || needle.Length == 0) return null;
                (byte[] Bytes, int[] Prefix)? pattern = null;
                return ApplyToScalarOrArray(first, value =>
                {
                    if (value is null) return null;
                    var text = StringValue(value, c.Budget, c.Token);
                    // 原典はnoneの文字列化に失敗したら検索準備を行わない。
                    // array内では最初の検索可能な値だけが共有patternを準備する。
                    var prepared = pattern ?? CreateSearchPattern(needle, c);
                    pattern = prepared;
                    return SearchBytes(text, prepared.Bytes, prepared.Prefix,
                        name == "strcount", (long)occurrenceValue!, c);
                }, c);
            }
            if (name == "replace")
            {
                // 原典は型検査より先に全三引数を一度ずつ評価する。
                var needleValue = arguments[1].Evaluate(c);
                var replacementValue = arguments[2].Evaluate(c);
                if (needleValue is not string needle || replacementValue is not string replacement) return null;
                ReplacePattern? pattern = null;
                return ApplyToScalarOrArray(first, value =>
                {
                    if (value is null) return null;
                    var text = StringValue(value, c.Budget, c.Token);
                    if (needle.Length == 0 && !optimizedReplaceNeedle) return null;
                    pattern ??= PrepareReplace(needle, replacement, c);
                    return ReplaceLiteral(text, replacement, pattern, c);
                }, c);
            }
            var provider = c.Provider;
            switch (name)
            {
                case "at":
                    if (first is not ValueArray array) return null;
                    var indexValue = arguments[1].Evaluate(c);
                    return indexValue is long at ? ArrayAt(array, at) : null;
                case "anyof": return BooleanValue(first, c);
                case "allof":
                    if (first is not ValueArray all) return first is bool boolean ? boolean : null;
                    for (var index = 0; index < all.Count; index++)
                    {
                        c.Budget.Charge(1, c.Token);
                        if (all[index] is not true) return false;
                    }
                    return true;
                case "allequal":
                    if (first is ValueArray equals)
                    {
                        for (var index = 1; index < equals.Count; index++)
                            if (!StrictEqual(equals[0], equals[index], c)) return false;
                    }
                    else
                        for (var index = 1; index < arguments.Length; index++)
                            if (!StrictEqual(first, arguments[index].Evaluate(c), c)) return false;
                    return true;
                case "andeach":
                case "oreach": return LogicalEach(first, arguments[1].Evaluate(c), name == "andeach", c);
                case "noteach": return ApplyToScalarOrArray(first,
                    value => BooleanValue(value, c) is bool condition ? !condition : null, c);
                case "ifeach":
                    // 両枝は常に一度ずつ評価。候補arrayの対応位置がなければnone。
                    var whenTrue = arguments[1].Evaluate(c);
                    var whenFalse = arguments[2].Evaluate(c);
                    var position = 0;
                    return ApplyToScalarOrArray(first, value =>
                    {
                        var chosen = BooleanValue(value, c) is true ? whenTrue : whenFalse;
                        c.Budget.Charge(1, c.Token);
                        var result = chosen is ValueArray choices ? choices.AtOrNone(position) : chosen;
                        position++;
                        return result;
                    }, c);
                case "choose":
                    return first is long choice ? arguments[ChoiceIndex(choice, arguments.Length)].Evaluate(c) : null;
                case "chooseeach":
                    return ApplyToScalarOrArray(first,
                        value => value is long choice ? arguments[ChoiceIndex(choice, arguments.Length)].Evaluate(c) : null, c);
                case "columnrange":
                    if (cachedRanges is not null) return provider!.SelectColumns(c.LineIndex, cachedRanges);
                    if (first is not string specification) throw new ArgumentException("ColumnRangeには文字列が必要です。");
                    return provider!.SelectColumns(c.LineIndex, ParseRanges(specification, c));
                case "column":
                    if (first is long column)
                    {
                        if (column is < 1 or > int.MaxValue) throw new ArgumentException("Columnの列番号が不正です。");
                        return provider!.Column(c.LineIndex, column);
                    }
                    if (first is string header) return provider!.ColumnByName(c.LineIndex, header, c.CaseSensitive);
                    throw new ArgumentException("Columnには列番号または列名が必要です。");
                case "lineat": return first is long line && line > 0 ? provider!.Line(line - 1) : null;
                case "lineoffsetat": return first is long offset ? provider!.Line(checked(c.LineIndex + offset)) : null;
                case "columnat":
                case "columnoffsetat":
                    if (first is not long row) return null;
                    if (name == "columnat" && row <= 0) return null;
                    var rowIndex = name == "columnat" ? row - 1 : checked(c.LineIndex + row);
                    if (rowIndex < 0 || rowIndex >= provider!.LineCount) return null;
                    var selection = arguments[1].Evaluate(c);
                    return selection is long selected ? provider.Column(rowIndex, selected)
                        : selection is string title ? provider.ColumnByName(rowIndex, title, c.CaseSensitive) : null;
                case "len": return first switch
                {
                    ValueArray lengthArray => (long)lengthArray.Count,
                    null => 0L, string lengthText => CharacterCount(lengthText, c), _ => 1L
                };
                case "abs": return ApplyToScalarOrArray(first,
                    value => value switch { long number => checked(Math.Abs(number)), double number => Finite(Math.Abs(number)), _ => null }, c);
                case "strlen": return ApplyToScalarOrArray(first,
                    value => value is null ? null : CharacterCount(StringValue(value, c.Budget, c.Token), c), c);
                case "trim":
                case "trimleft":
                case "trimright": return ApplyToScalarOrArray(first, value => TrimValue(value, name, c), c);
                case "tolower": return ApplyToScalarOrArray(first, value => ConvertCase(value, true, stringEnvironment, c), c);
                case "toupper": return ApplyToScalarOrArray(first, value => ConvertCase(value, false, stringEnvironment, c), c);
                case "tohalfwidth":
                case "tofullwidth":
                case "tohiragana":
                case "tokatakana":
                case "tosimplifiedchinese":
                case "totraditionalchinese": return ApplyToScalarOrArray(first, value => MapCharacters(value, name, stringEnvironment, c), c);
                case "todatetime": return ApplyToScalarOrArray(first, value => ConvertToTimestamp(value, timestampEnvironment, c), c);
                case "todatestr": return ApplyToScalarOrArray(first, value => ConvertToDateString(value, timestampEnvironment, c), c);
                case "startofweek":
                case "startofmonth":
                case "startofyear": return ApplyToScalarOrArray(first, value => ConvertStartOf(value, name, timestampEnvironment, c), c);
                case "toint":
                case "todouble":
                case "tonumber": return ApplyToScalarOrArray(first, value => NumberConversion(value, name, c), c);
                default: throw new ArgumentException("未対応の行関数です。");
            }
        }

        private object? EvaluateLog(Context c)
        {
            var values = new string[arguments.Length];
            object? result = null;
            long messageLength = filterName.Length == 0 ? 0 : checked((long)filterName.Length + 3);
            for (var index = 0; index < arguments.Length; index++)
            {
                c.Token.ThrowIfCancellationRequested();
                result = arguments[index].Evaluate(c);
                values[index] = StringValue(result, c.Budget, c.Token);
                messageLength = checked(messageLength + values[index].Length + (index == 0 ? 0 : 1));
            }
            c.Budget.Charge(messageLength, c.Token, messageLength);
            if (messageLength > int.MaxValue) throw new InvalidOperationException("行変換ログの文字数が上限を超えています。");
            var message = string.Create((int)messageLength, (Values: values, Name: filterName), static (destination, state) =>
            {
                var position = 0;
                if (state.Name.Length > 0)
                {
                    destination[position++] = '[';
                    state.Name.AsSpan().CopyTo(destination[position..]);
                    position += state.Name.Length;
                    destination[position++] = ']';
                    destination[position++] = ' ';
                }
                for (var index = 0; index < state.Values.Length; index++)
                {
                    if (index > 0) destination[position++] = ' ';
                    state.Values[index].AsSpan().CopyTo(destination[position..]);
                    position += state.Values[index].Length;
                }
            });
            var level = name switch
            {
                "logerror" => LineTransformLogLevel.Error,
                "logwarn" => LineTransformLogLevel.Warning,
                _ => LineTransformLogLevel.Information
            };
            logEnvironment?.Write(level, message);
            return result;
        }

        internal override Node Optimize(Context c)
        {
            c.Budget.Charge(arguments.Length + 1L, c.Token);
            var optimized = new Node[arguments.Length];
            for (var index = 0; index < arguments.Length; index++) optimized[index] = arguments[index].Optimize(c);
            if (name is "replacewithlist" or "regexreplacewithlist" && optimized[1] is Literal { Value: string listPath })
                optimized[1] = new Literal(LoadReplaceList(listPath, c));
            if (name is "regexfind" or "regexcount" or "regexextract" or "regexreplace"
                && optimized[1] is Literal { Value: string regexText })
            {
                var pattern = PrepareRegex(regexText, c);
                if (pattern is not null) optimized[1] = new Literal(pattern);
            }
            ColumnRangeSegment[]? ranges = null;
            if (name == "columnrange" && optimized[0] is Literal { Value: string specification })
                ranges = ParseRanges(specification, c);
            var node = new Function(name, optimized, ranges,
                name == "replace" && optimized[1] is Literal { Value: string }, timestampEnvironment, timestampClock, stringEnvironment,
                logEnvironment, filterName, listEnvironment);
            // 原典のconstant-time functionsはDateTimeLiteralへ置換する。
            if (name is "now" or "today")
            {
                if (node.Evaluate(c) is not TimestampValue timestamp) throw new ArgumentException("時計関数の定数評価結果が日時ではありません。");
                return new Literal(timestamp, LiteralOrigin.Timestamp);
            }
            // 原典は子のOptimize後、StringLiteralから成功したTimestampだけをDateTimeLiteralへ置換する。
            if (name == "todatetime" && optimized[0] is Literal { Value: string })
            {
                if (node.Evaluate(c) is TimestampValue timestamp) return new Literal(timestamp, LiteralOrigin.Timestamp);
                return node;
            }
            // 原典DateTimeLiteral専用。StringLiteralのstartofは実行時のまま。
            if (name is "startofweek" or "startofmonth" or "startofyear"
                && optimized[0] is Literal { Value: TimestampValue, Origin: LiteralOrigin.Timestamp })
            {
                if (node.Evaluate(c) is not TimestampValue timestamp) throw new ArgumentException("日時関数の定数評価結果が日時ではありません。");
                return new Literal(timestamp, LiteralOrigin.Timestamp);
            }
            // 集計とmatch範囲自体は定数引数でも全行・providerへ依存する。子の最適化だけを保つ。
            if (name is "average" or "sum" or "maximum" or "minimum"
                or "matchinside" or "matchcontext" or "matchblockcount" or "matchblocknumber" or "matchblocksize" or "matchblockoffset"
                or "matchcount" or "matchnumber" or "matchdistance" or "matchdistancebefore" or "matchdistanceafter") return node;
            if (name is "regexfind" or "regexcount" or "regexextract"
                && optimized[0] is Literal { Value: string } && optimized[1] is Literal { Value: RegexPattern }
                && (name == "regexcount" || optimized.Length == 2
                    || optimized[2] is Literal { Value: long, Origin: LiteralOrigin.Ordinary })
                && (name != "regexfind" || optimized.Length <= 3
                    || optimized[3] is Literal { Value: long, Origin: LiteralOrigin.Ordinary }))
            {
                var result = node.Evaluate(c);
                if (result is long or string) return new Literal(result);
                return node;
            }
            if (name == "regexreplace" && optimized[0] is Literal { Value: string }
                && optimized[1] is Literal { Value: RegexPattern } && optimized[2] is Literal { Value: string })
            {
                var result = node.Evaluate(c);
                if (result is string) return new Literal(result);
                throw new ArgumentException("RegexReplaceの定数評価結果が文字列ではありません。");
            }
            if (name == "replace" && optimized[0] is Literal { Value: string }
                && optimized[1] is Literal { Value: string } && optimized[2] is Literal { Value: string })
            {
                var result = node.Evaluate(c);
                if (result is not string) throw new ArgumentException("Replaceの定数評価結果が文字列ではありません。");
                return new Literal(result);
            }
            if (name is "strcount" or "strfind" && optimized[0] is Literal { Value: string }
                && optimized[1] is Literal { Value: string }
                && (name == "strcount" || optimized.Length == 2 || optimized[2] is Literal { Value: long, Origin: LiteralOrigin.Ordinary }))
            {
                var result = node.Evaluate(c);
                // StrCountの原典std::get<int64_t>はnoneを定数化しようとすると例外。
                if (name == "strcount" && result is not long)
                    throw new ArgumentException("StrCountの定数評価結果が整数ではありません。");
                if (result is long) return new Literal(result);
            }
            // 原典IsArrayConstはnone/arrayを含まない。空Arrayは定数化する。
            if (name == "array" && optimized.All(argument => argument is Literal { Value: long or double or string or bool, Origin: LiteralOrigin.Ordinary }))
                return new Literal(node.Evaluate(c));
            if (name == "at" && optimized[0] is Literal { Value: ValueArray array }
                && optimized[1] is Literal { Value: long atIndex, Origin: LiteralOrigin.Ordinary })
            {
                var adjusted = atIndex < 0 ? atIndex + array.Count : atIndex;
                if (adjusted >= 0 && adjusted < array.Count && array[(int)adjusted] is long or double or string or bool)
                    return new Literal(ArrayAt(array, atIndex));
            }
            var fold = name == "abs" && optimized[0] is Literal { Value: long or double, Origin: LiteralOrigin.Ordinary }
                || name == "strlen" && optimized[0] is Literal { Value: string }
                || name == "substr" && optimized[0] is Literal { Value: string }
                    && optimized[1] is Literal { Value: long, Origin: LiteralOrigin.Ordinary }
                    && (optimized.Length == 2 || optimized[2] is Literal { Value: long, Origin: LiteralOrigin.Ordinary });
            return fold ? new Literal(node.Evaluate(c)) : node;
        }
    }

    // backing storageは生成helperだけが所有し、評価後の値を変更する経路を持たない。
    private sealed class ValueArray(object?[] values, int depth)
    {
        private readonly object?[] values = values;
        internal int Count => values.Length;
        internal int Depth { get; } = depth;
        internal object? this[int index] => values[index];
        internal object? AtOrNone(int index) => index < Count ? values[index] : null;
    }

    private static ValueArray CreateArray(int count, Func<int, object?> getValue, Context c)
    {
        if (count > MaximumArrayElements) throw new InvalidOperationException("行変換の配列要素数が上限を超えています。");
        c.Budget.ChargeArray(count, c.Token);
        var values = new object?[count];
        var depth = 1;
        for (var index = 0; index < count; index++)
        {
            c.Budget.Charge(1, c.Token);
            var value = getValue(index);
            if (value is ValueArray child) depth = Math.Max(depth, child.Depth + 1);
            if (depth > MaximumDepth) throw new InvalidOperationException("行変換の配列値の深さが上限を超えています。");
            values[index] = value;
        }
        return new(values, depth);
    }

    private static object? ApplyToScalarOrArray(object? value, Func<object?, object?> apply, Context c)
    {
        c.Budget.Charge(1, c.Token);
        return value is ValueArray array ? CreateArray(array.Count, index => apply(array[index]), c) : apply(value);
    }

    private static object? ArrayAt(ValueArray array, long index)
    {
        if (index < 0) index += array.Count;
        return index >= 0 && index < array.Count ? array[(int)index] : null;
    }

    private static int ChoiceIndex(long index, int arguments)
        => index <= 0 ? 1 : index >= arguments - 2 ? arguments - 1 : (int)index + 1;

    private static bool? BooleanValue(object? value, Context c)
    {
        c.Budget.Charge(1, c.Token);
        if (value is bool boolean) return boolean;
        if (value is not ValueArray array) return null;
        // 真になるのは外側のbool trueだけ。入れ子のtrueや数値はtruthyにしない。
        for (var index = 0; index < array.Count; index++)
        {
            c.Budget.Charge(1, c.Token);
            if (array[index] is true) return true;
        }
        return false;
    }

    private static object? InRange(object? value, object? lower, object? upper, Context c, bool upperInclusive = false)
    {
        c.Budget.Charge(1, c.Token);
        if (value is TimestampValue timestamp)
            return lower is TimestampValue lowerTimestamp && upper is TimestampValue upperTimestamp
                ? timestamp.EpochMicroseconds >= lowerTimestamp.EpochMicroseconds && (upperInclusive ? timestamp.EpochMicroseconds <= upperTimestamp.EpochMicroseconds : timestamp.EpochMicroseconds < upperTimestamp.EpochMicroseconds)
                : null;
        if (value is long integer)
        {
            // 整数の辺はexact比較を維持し、doubleの辺だけ値を変換する。
            if (lower is long lowerInteger)
            {
                if (upper is long upperInteger) return integer >= lowerInteger && (upperInclusive ? integer <= upperInteger : integer < upperInteger);
                if (upper is double upperReal) return integer >= lowerInteger && (upperInclusive ? (double)integer <= upperReal : (double)integer < upperReal);
            }
            else if (lower is double lowerReal)
            {
                if (upper is double upperReal) return (double)integer >= lowerReal && (upperInclusive ? (double)integer <= upperReal : (double)integer < upperReal);
                if (upper is long upperInteger) return (double)integer >= lowerReal && (upperInclusive ? integer <= upperInteger : integer < upperInteger);
            }
        }
        else if (value is double real)
        {
            if (lower is not (long or double) || upper is not (long or double)) return null;
            var minimum = lower is long lowerInteger ? (double)lowerInteger : (double)lower;
            var maximum = upper is long upperInteger ? (double)upperInteger : (double)upper;
            return real >= minimum && (upperInclusive ? real <= maximum : real < maximum);
        }
        else if (value is string text && lower is string lowerText && upper is string upperText)
        {
            // 原典は両辺の文字列比較を実行する。UTF-8順とPocoのASCII foldは既存経路を使う。
            var minimum = CompareStrings(text, lowerText, c.CaseSensitive, c.Budget, c.Token);
            var maximum = CompareStrings(text, upperText, c.CaseSensitive, c.Budget, c.Token);
            return minimum >= 0 && (upperInclusive ? maximum <= 0 : maximum < 0);
        }
        return null;
    }
    private static bool StrictEqual(object? left, object? right, Context c, int depth = 0)
    {
        c.Budget.Charge(1, c.Token);
        if (depth > MaximumDepth) throw new InvalidOperationException("行変換の配列値の深さが上限を超えています。");
        if (left is ValueArray l)
        {
            if (right is not ValueArray r || l.Count != r.Count) return false;
            for (var index = 0; index < l.Count; index++)
                if (!StrictEqual(l[index], r[index], c, depth + 1)) return false;
            return true;
        }
        if (left is string ls && right is string rs)
            return CompareStrings(ls, rs, true, c.Budget, c.Token) == 0;
        // AllEqualのvariant等値はcase directiveやint/doubleの数値同値を採用しない。
        return left is null ? right is null : left.GetType() == right?.GetType() && left.Equals(right);
    }

    private static object? LogicalEach(object? left, object? right, bool and, Context c)
    {
        c.Budget.Charge(1, c.Token);
        var l = left as ValueArray;
        var r = right as ValueArray;
        object? Select(object? a, object? b)
        {
            var ab = BooleanValue(a, c);
            var bb = BooleanValue(b, c);
            return ab is bool av && bb is bool bv ? and ? av && bv : av || bv : null;
        }
        if (l is null && r is null) return Select(left, right);
        var count = Math.Max(l?.Count ?? 0, r?.Count ?? 0);
        return CreateArray(count, index => Select(l is null ? left : l.AtOrNone(index), r is null ? right : r.AtOrNone(index)), c);
    }

    private static object? ConvertCase(object? value, bool lower, ILineTransformStringEnvironment? environment, Context c)
    {
        if (value is null) return null;
        var text = StringValue(value, c.Budget, c.Token);
        c.Budget.Charge(text.Length, c.Token);
        c.Token.ThrowIfCancellationRequested();
        var active = environment ?? throw new ArgumentException("文字列の大小文字変換には明示的な環境が必要です。");
        var mapped = lower ? active.ToLower(text) : active.ToUpper(text);
        c.Token.ThrowIfCancellationRequested();
        c.Budget.Charge(mapped.Length, c.Token, mapped.Length);
        return mapped;
    }

    private static object? MapCharacters(object? value, string name, ILineTransformStringEnvironment? environment, Context c)
    {
        if (value is null) return null;
        var text = StringValue(value, c.Budget, c.Token);
        c.Budget.Charge(text.Length, c.Token);
        var mapping = name switch
        {
            "tohalfwidth" => LineTransformCharacterMapping.HalfWidth,
            "tofullwidth" => LineTransformCharacterMapping.FullWidth,
            "tohiragana" => LineTransformCharacterMapping.Hiragana,
            "tokatakana" => LineTransformCharacterMapping.Katakana,
            "tosimplifiedchinese" => LineTransformCharacterMapping.SimplifiedChinese,
            "totraditionalchinese" => LineTransformCharacterMapping.TraditionalChinese,
            _ => throw new ArgumentException("未対応の文字種変換です。")
        };
        var active = environment ?? throw new ArgumentException("文字種変換には明示的な環境が必要です。");
        var mapped = active.MapCharacters(text, mapping);
        c.Token.ThrowIfCancellationRequested();
        c.Budget.Charge(mapped.Length, c.Token, mapped.Length);
        return mapped;
    }

    private static NormalizationForm? UnicodeNormalizationForm(object? value)
    {
        if (value is long number) return number switch
        {
            1 => NormalizationForm.FormC, 2 => NormalizationForm.FormD,
            5 => NormalizationForm.FormKC, 6 => NormalizationForm.FormKD, _ => null
        };
        if (value is not string text) return NormalizationForm.FormC;
        // 受理するASCII名だけを比較し、空白除去・文化依存の大小変換をしない。
        if (text.Length is < 1 or > 4) return null;
        foreach (var character in text) if (character > 127) return null;
        if (text.Equals("NFC", StringComparison.OrdinalIgnoreCase) || text.Equals("C", StringComparison.OrdinalIgnoreCase)) return NormalizationForm.FormC;
        if (text.Equals("NFD", StringComparison.OrdinalIgnoreCase) || text.Equals("D", StringComparison.OrdinalIgnoreCase)) return NormalizationForm.FormD;
        if (text.Equals("NFKC", StringComparison.OrdinalIgnoreCase) || text.Equals("KC", StringComparison.OrdinalIgnoreCase)) return NormalizationForm.FormKC;
        if (text.Equals("NFKD", StringComparison.OrdinalIgnoreCase) || text.Equals("KD", StringComparison.OrdinalIgnoreCase)) return NormalizationForm.FormKD;
        return null;
    }

    private static object? NormalizeUnicode(object? value, NormalizationForm form, ILineTransformStringEnvironment? environment, Context c)
    {
        if (value is null) return null;
        var text = StringValue(value, c.Budget, c.Token);
        c.Budget.Charge(text.Length, c.Token);
        var active = environment ?? throw new ArgumentException("Unicode正規化には明示的な環境が必要です。");
        var normalized = active.NormalizeUnicode(text, form);
        c.Token.ThrowIfCancellationRequested();
        c.Budget.Charge(normalized.Length, c.Token, normalized.Length);
        return normalized;
    }

    private static object? TrimValue(object? value, string name, Context c)
    {
        if (value is null) return null;
        var text = StringValue(value, c.Budget, c.Token);
        var start = 0;
        var end = text.Length;
        if (name != "trimright")
            while (start < end && text[start] is ' ' or '\t' or '\r' or '\n') { c.Budget.Charge(1, c.Token); start++; }
        if (name != "trimleft")
            while (end > start && text[end - 1] is ' ' or '\t' or '\r' or '\n') { c.Budget.Charge(1, c.Token); end--; }
        return Slice(text, start, end - start, c);
    }

    private static ColumnRangeSegment[] ParseRanges(string specification, Context c)
    {
        c.Budget.Charge(specification.Length, c.Token);
        var ranges = TableColumnRangePlan.Parse(specification);
        c.Budget.Charge(ranges.Length, c.Token);
        return ranges;
    }

    private static double Finite(double value) => double.IsFinite(value) ? value
        : throw new OverflowException("行変換の実数が有限範囲を超えています。");

    private static object? Compute(string op, object? left, object? right, Context c, int depth = 0)
    {
        c.Budget.Charge(1, c.Token);
        if (depth > MaximumDepth) throw new InvalidOperationException("行変換の配列演算の深さが上限を超えています。");
        var leftArray = left as ValueArray;
        var rightArray = right as ValueArray;
        if (leftArray is not null || rightArray is not null)
        {
            if (leftArray is not null && rightArray is not null)
            {
                if (op is "=" or "!=")
                {
                    if (leftArray.Count != rightArray.Count) return op == "!=";
                    for (var index = 0; index < leftArray.Count; index++)
                        if (Compute("=", leftArray[index], rightArray[index], c, depth + 1) is not true)
                            return op == "!=";
                    return op == "=";
                }
                var count = Math.Max(leftArray.Count, rightArray.Count);
                var paired = Math.Min(leftArray.Count, rightArray.Count);
                // 余りはcomputeへnoneを渡すのではなく、そのままnoneを置く。
                return CreateArray(count, index => index < paired
                    ? Compute(op, leftArray[index], rightArray[index], c, depth + 1) : null, c);
            }
            if (leftArray is not null)
                return CreateArray(leftArray.Count, index => Compute(op, leftArray[index], right, c, depth + 1), c);
            return CreateArray(rightArray!.Count, index => Compute(op, left, rightArray[index], c, depth + 1), c);
        }
        if (op == "+" && (left is string || right is string))
        {
            if (left is null) return right;
            if (right is null) return left;
            var l = StringValue(left, c.Budget, c.Token);
            var r = StringValue(right, c.Budget, c.Token);
            c.Budget.Charge(l.Length + (long)r.Length, c.Token, l.Length + (long)r.Length);
            return string.Concat(l, r);
        }
        if (left is TimestampValue timestamp)
        {
            if (right is TimestampValue other)
            {
                if (op == "-") return checked(timestamp.EpochMicroseconds - other.EpochMicroseconds);
                var order = timestamp.EpochMicroseconds.CompareTo(other.EpochMicroseconds);
                return op switch { "=" => order == 0, "!=" => order != 0, "<" => order < 0, "<=" => order <= 0, ">" => order > 0, ">=" => order >= 0, _ => null };
            }
            if (right is long amount && op is "+" or "-")
                return new TimestampValue(op == "+" ? checked(timestamp.EpochMicroseconds + amount) : checked(timestamp.EpochMicroseconds - amount), timestamp.Environment);
        }
        if (op == "contains" && left is LineTransformContent content && right is string needle)
            return content.Contains(needle, c.CaseSensitive, c.Budget, c.Token);
        if (op == "like" && left is string globSubject && right is string globPattern)
            return LineTransformGlob.IsMatch(globSubject, globPattern, c.CaseSensitive, c.Budget, c.Token);
        if (op == "recontains" && left is string or LineTransformContent && right is string or RegexPattern)
        {
            var pattern = right as RegexPattern ?? PrepareRegex((string)right, c, carriageReturnNewline: true);
            if (pattern is null) return false;
            if (left is string text) return SearchRegex(text, pattern, c);
            var source = (LineTransformContent)left;
            return source.REContains(body => SearchRegex(body, pattern, c), c.Budget, c.Token, source.IncludeFinalEofCandidate);
        }
        if (op == "matches" && left is string subject && right is string or RegexPattern)
        {
            var pattern = right as RegexPattern ?? PrepareRegex((string)right, c, nonEmptyAtStart: true);
            return pattern is not null && MatchesWholeRegex(subject, pattern, c);
        }
        // 原典のdouble分岐は、右が数値以外なら比較fallbackへ進まずnoneを返す。
        if (left is double && right is not (long or double)) return null;
        int? compare = null;
        if (left is long li && right is long ri)
        {
            if (op == "+") return checked(li + ri);
            if (op == "-") return checked(li - ri);
            if (op == "*") return checked(li * ri);
            if (op is "/" or "%")
            {
                if (ri == 0) throw new DivideByZeroException("行変換でゼロ除算が発生しました。");
                return op == "/" ? checked(li / ri) : li % ri;
            }
            compare = li.CompareTo(ri);
        }
        else if (left is long or double && right is long or double)
        {
            var l = left is long integer ? integer : (double)left;
            var r = right is long number ? number : (double)right;
            if (op == "+") return Finite(l + r);
            if (op == "-") return Finite(l - r);
            if (op == "*") return Finite(l * r);
            if (op == "/")
            {
                if (r == 0) throw new DivideByZeroException("行変換でゼロ除算が発生しました。");
                return Finite(l / r);
            }
            compare = l.CompareTo(r);
        }
        else if (left is string ls && right is string rs)
        {
            if (op == "contains") return Contains(ls, rs, c);
            compare = CompareStrings(ls, rs, c.CaseSensitive, c.Budget, c.Token);
        }
        else if (left is bool lb && right is bool rb)
        {
            if (op == "+") return (lb ? 1L : 0L) + (rb ? 1L : 0L);
            // 旧computeのbool減算は右-左（literal foldは左-右）。
            if (op == "-") return (rb ? 1L : 0L) - (lb ? 1L : 0L);
            compare = lb.CompareTo(rb);
        }
        else if (left is null && right is null && op is "=" or "!=") compare = 0;
        return op switch
        {
            "=" => compare == 0,
            "!=" => compare != 0,
            "<" => compare is int less ? less < 0 : null,
            "<=" => compare is int lessEqual ? lessEqual <= 0 : null,
            ">" => compare is int greater ? greater > 0 : null,
            ">=" => compare is int greaterEqual ? greaterEqual >= 0 : null,
            _ => null
        };
    }

    private static string StringValue(object? value, LineTransformBudget budget, CancellationToken token)
    {
        budget.Charge(1, token);
        if (value is string text) return text;
        if (value is LineTransformContent content) return content.Serialize(budget, token);
        if (value is TimestampValue timestamp) return SerializeTimestamp(timestamp, budget, token);
        if (value is ValueArray array)
        {
            // 同一StringValue操作だけでTimestamp表示を準備。variant等値ではなく参照identityで保持する。
            Dictionary<TimestampValue, string>? preparedTimestamps = null;
            var length = StringLength(array, budget, token, 0, ref preparedTimestamps);
            budget.Charge(length, token, length);
            return string.Create(checked((int)length), (array, budget, token, preparedTimestamps), static (destination, state) =>
            {
                var position = 0;
                WriteValue(state.array, destination, ref position, state.budget, state.token, 0, state.preparedTimestamps);
            });
        }
        Span<char> buffer = stackalloc char[320];
        var count = FormatScalar(value, buffer);
        budget.Charge(count, token, count);
        return new string(buffer[..count]);
    }

    private static long StringLength(object? value, LineTransformBudget budget, CancellationToken token, int depth,
        ref Dictionary<TimestampValue, string>? preparedTimestamps)
    {
        budget.Charge(1, token);
        if (depth > MaximumDepth) throw new InvalidOperationException("行変換の文字列化の深さが上限を超えています。");
        if (value is string text) { budget.Charge(text.Length, token); return text.Length; }
        if (value is LineTransformContent content) { budget.Charge(content.DisplayLength, token); return content.DisplayLength; }
        if (value is TimestampValue timestamp)
        {
            if (preparedTimestamps is null)
            {
                budget.Charge(32, token);
                preparedTimestamps = new Dictionary<TimestampValue, string>(ReferenceEqualityComparer.Instance);
            }
            if (!preparedTimestamps.TryGetValue(timestamp, out var serialized))
            {
                serialized = SerializeTimestamp(timestamp, budget, token);
                budget.Charge(32, token);
                preparedTimestamps.Add(timestamp, serialized);
            }
            return serialized.Length;
        }
        if (value is ValueArray array)
        {
            long length = 2 + Math.Max(0, array.Count - 1) * 2L;
            for (var index = 0; index < array.Count; index++)
            {
                length = checked(length + StringLength(array[index], budget, token, depth + 1, ref preparedTimestamps));
                if (length > budget.MaximumOutputCharacters || length > int.MaxValue)
                    throw new InvalidOperationException("行変換の配列文字列が生成上限を超えています。");
            }
            return length;
        }
        Span<char> buffer = stackalloc char[320];
        return FormatScalar(value, buffer);
    }

    private static void WriteValue(object? value, Span<char> destination, ref int position,
        LineTransformBudget budget, CancellationToken token, int depth,
        Dictionary<TimestampValue, string>? preparedTimestamps)
    {
        budget.Charge(1, token);
        if (depth > MaximumDepth) throw new InvalidOperationException("行変換の文字列化の深さが上限を超えています。");
        if (value is ValueArray array)
        {
            destination[position++] = '[';
            for (var index = 0; index < array.Count; index++)
            {
                if (index != 0) { destination[position++] = ','; destination[position++] = ' '; }
                WriteValue(array[index], destination, ref position, budget, token, depth + 1, preparedTimestamps);
            }
            destination[position++] = ']';
        }
        else if (value is LineTransformContent content)
        {
            budget.Charge(content.DisplayLength, token);
            content.WriteDisplay(destination[position..]);
            position += content.DisplayLength;
        }
        else if (value is string text)
        {
            budget.Charge(text.Length, token);
            text.AsSpan().CopyTo(destination[position..]);
            position += text.Length;
        }
        else if (value is TimestampValue timestamp)
        {
            if (preparedTimestamps is null || !preparedTimestamps.TryGetValue(timestamp, out var serialized))
                throw new InvalidOperationException("配列の日時表示準備がありません。");
            budget.Charge(serialized.Length, token);
            serialized.AsSpan().CopyTo(destination[position..]); position += serialized.Length;
        }
        else position += FormatScalar(value, destination[position..]);
    }

    private static int FormatScalar(object? value, Span<char> destination)
    {
        // std::to_string(double)は固定小数点6桁。最長finite doubleも320charに収まる。
        if (value is long integer && integer.TryFormat(destination, out var integerLength, provider: CultureInfo.InvariantCulture)) return integerLength;
        if (value is double real && real.TryFormat(destination, out var realLength, "F6", CultureInfo.InvariantCulture)) return realLength;
        var text = value switch { null => "none", bool boolean => boolean ? "true" : "false", _ => throw new ArgumentException("未対応の行変換の値型です。") };
        text.AsSpan().CopyTo(destination);
        return text.Length;
    }

    private static string Slice(string source, int start, int length, Context c)
    {
        c.Budget.Charge(length, c.Token, length);
        return source.Substring(start, length);
    }

    private static long CharacterCount(string text, Context c)
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
            else if (char.IsLowSurrogate(character)) throw InvalidUnicode();
            count++;
        }
        return count;
    }

    internal static int CompareStrings(string left, string right, bool caseSensitive,
        LineTransformBudget budget, CancellationToken token)
    {
        // UTF-8のバイト順は有効なUnicode scalarの順。ASCIIだけをcase foldする。
        var l = 0;
        var r = 0;
        while (l < left.Length && r < right.Length)
        {
            budget.Charge(2, token);
            var lc = ReadScalar(left, ref l);
            var rc = ReadScalar(right, ref r);
            if (!caseSensitive) { lc = FoldAscii(lc); rc = FoldAscii(rc); }
            if (lc != rc) return lc.CompareTo(rc);
        }
        return (l < left.Length ? 1 : 0) - (r < right.Length ? 1 : 0);
    }

    private static int ReadScalar(string text, ref int index)
    {
        var character = text[index++];
        if (!char.IsSurrogate(character)) return character;
        if (!char.IsHighSurrogate(character) || index == text.Length || !char.IsLowSurrogate(text[index])) throw InvalidUnicode();
        return char.ConvertToUtf32(character, text[index++]);
    }
    private static int FoldAscii(int character) => character is >= 'A' and <= 'Z' ? character + 32 : character;
    private static ArgumentException InvalidUnicode() => new("行変換のUnicode文字列が不正か、UTF-8文字の途中で切断されています。");

    private static bool Contains(string source, string needle, Context c)
    {
        if (needle.Length == 0) return false;
        for (var start = 0; start <= source.Length - needle.Length; start++)
        {
            var same = true;
            for (var index = 0; index < needle.Length; index++)
            {
                c.Budget.Charge(1, c.Token);
                var a = source[start + index];
                var b = needle[index];
                if (c.CaseSensitive ? a != b : FoldAscii(a) != FoldAscii(b)) { same = false; break; }
            }
            if (same) return true;
        }
        return false;
    }

    private static string ByteSubstring(string text, long begin, long? length, Context c)
    {
        c.Budget.Charge(text.Length * 3L, c.Token);
        byte[] bytes;
        try { bytes = StrictUtf8.GetBytes(text); }
        catch (EncoderFallbackException) { throw InvalidUnicode(); }
        if (begin < 0) begin = checked(begin + bytes.Length);
        if (begin < 0 || begin >= bytes.Length) return string.Empty;
        var actual = length is null ? bytes.Length - begin : length >= 0 ? Math.Min(length.Value, bytes.Length - begin)
            : Math.Max(0, checked(bytes.Length - begin + length.Value));
        c.Budget.Charge(actual, c.Token, actual);
        try { return StrictUtf8.GetString(bytes, (int)begin, (int)actual); }
        catch (DecoderFallbackException) { throw InvalidUnicode(); }
    }

    private static int SearchByteLength(string text, Context c)
    {
        long bytes = 0;
        for (var index = 0; index < text.Length;)
        {
            c.Budget.Charge(1, c.Token);
            var scalar = ReadScalar(text, ref index);
            bytes += scalar <= 0x7f ? 1 : scalar <= 0x7ff ? 2 : scalar <= 0xffff ? 3 : 4;
            if (bytes > int.MaxValue) throw new InvalidOperationException("行検索のUTF-8生成量が上限を超えています。");
        }
        return (int)bytes;
    }

    private static byte[] EncodeSearchBytes(string text, int length, Context c)
    {
        // 呼出し元が生成量を予約済み。ASCIIだけをfoldし、UTF-8のbyte位置を保つ。
        c.Token.ThrowIfCancellationRequested();
        var bytes = new byte[length];
        var position = 0;
        for (var index = 0; index < text.Length;)
        {
            c.Budget.Charge(1, c.Token);
            var scalar = ReadScalar(text, ref index);
            if (!c.CaseSensitive) scalar = FoldAscii(scalar);
            if (scalar <= 0x7f) bytes[position++] = (byte)scalar;
            else if (scalar <= 0x7ff)
            {
                bytes[position++] = (byte)(0xc0 | scalar >> 6);
                bytes[position++] = (byte)(0x80 | scalar & 0x3f);
            }
            else if (scalar <= 0xffff)
            {
                bytes[position++] = (byte)(0xe0 | scalar >> 12);
                bytes[position++] = (byte)(0x80 | scalar >> 6 & 0x3f);
                bytes[position++] = (byte)(0x80 | scalar & 0x3f);
            }
            else
            {
                bytes[position++] = (byte)(0xf0 | scalar >> 18);
                bytes[position++] = (byte)(0x80 | scalar >> 12 & 0x3f);
                bytes[position++] = (byte)(0x80 | scalar >> 6 & 0x3f);
                bytes[position++] = (byte)(0x80 | scalar & 0x3f);
            }
        }
        c.Token.ThrowIfCancellationRequested();
        return bytes;
    }

    private static (byte[] Bytes, int[] Prefix) CreateSearchPattern(string text, Context c)
    {
        var length = SearchByteLength(text, c);
        // int[]は式のValueArrayではない。byte[]とintセルの全容量を共有生成予算へ先に予約。
        var allocation = checked(length + length * (long)sizeof(int));
        c.Budget.Charge(allocation, c.Token, allocation);
        var bytes = EncodeSearchBytes(text, length, c);
        c.Token.ThrowIfCancellationRequested();
        var prefix = new int[length];
        for (var index = 1; index < length; index++)
        {
            c.Budget.Charge(1, c.Token);
            var matched = prefix[index - 1];
            while (matched > 0)
            {
                c.Budget.Charge(1, c.Token);
                if (bytes[index] == bytes[matched]) break;
                matched = prefix[matched - 1];
            }
            c.Budget.Charge(1, c.Token);
            if (bytes[index] == bytes[matched]) matched++;
            prefix[index] = matched;
        }
        c.Token.ThrowIfCancellationRequested();
        return (bytes, prefix);
    }

    private static object? SearchBytes(string text, byte[] needle, int[] prefix, bool count, long occurrence, Context c)
    {
        var length = SearchByteLength(text, c);
        c.Budget.Charge(length, c.Token, length);
        var bytes = EncodeSearchBytes(text, length, c);
        if (!count && occurrence < 0) return null;
        var matched = 0;
        long found = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            c.Budget.Charge(1, c.Token);
            while (matched > 0)
            {
                c.Budget.Charge(1, c.Token);
                if (bytes[index] == needle[matched]) break;
                matched = prefix[matched - 1];
            }
            c.Budget.Charge(1, c.Token);
            if (bytes[index] == needle[matched]) matched++;
            if (matched != needle.Length) continue;
            if (!count && found == occurrence) return (long)index - needle.Length + 1;
            found++;
            // 原典findの次位置は一致末尾。重なった一致は数えない。
            matched = 0;
        }
        c.Token.ThrowIfCancellationRequested();
        return count ? found : null;
    }

    private static object? NumberConversion(object? value, string name, Context c)
    {
        if (value is not string text) return null;
        c.Budget.Charge(text.Length * 2L, c.Token, text.Length * 2L);
        var cleaned = text.Replace(",", "", StringComparison.Ordinal).TrimStart(' ', '\t', '\r', '\n', '\f', '\v');
        // stoll/stodは先頭空白を許すが末尾空白はpos!=sizeになる。
        if (cleaned.Length == 0 || cleaned[^1] is ' ' or '\t' or '\r' or '\n' or '\f' or '\v') return null;
        if (name == "todouble" || name == "tonumber" && cleaned.IndexOfAny(['.', 'e', 'E']) >= 0)
        {
            var unsigned = cleaned.AsSpan();
            if (unsigned.Length > 0 && unsigned[0] is '+' or '-') unsigned = unsigned[1..];
            if (unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || unsigned.StartsWith("nan", StringComparison.OrdinalIgnoreCase)
                || unsigned.StartsWith("inf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("非有限または16進実数の文字列変換には未対応です。");
            return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real) ? real : null;
        }
        return long.TryParse(cleaned, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) ? integer : null;
    }

    private sealed class Parser(string text, LineTransformBudget budget, CancellationToken token,
        ILineTransformTimestampEnvironment? timestampEnvironment, TimestampClockSnapshot? timestampClock,
        ILineTransformStringEnvironment? stringEnvironment, ILineTransformLogEnvironment? logEnvironment,
        ILineTransformListEnvironment? listEnvironment)
    {
        private int position;
        private int tokens;
        private int depth;
        private string current = "";
        private object? literal;
        private LiteralOrigin literalOrigin;
        internal bool CaseSensitive { get; private set; }
        internal bool Optimize { get; private set; } = true;
        internal bool UsesAggregates { get; private set; }
        internal bool UsesMatchRanges { get; private set; }
        internal bool UsesContent { get; private set; }
        internal bool UsesSublines { get; private set; }
        internal string FilterName { get; private set; } = "";

        internal Node Parse()
        {
            budget.Charge(text.Length, token);
            while (true)
            {
                SkipSpace();
                if (position >= text.Length || text[position] != '@') break;
                var begin = ++position;
                var inQuote = false;
                while (position < text.Length)
                {
                    var character = text[position];
                    if (character == '"') inQuote = !inQuote;
                    else if (!inQuote && IsSpace(character)) break;
                    position++;
                }
                budget.Charge((position - begin) * 2L, token, (position - begin) * 2L);
                var directive = text[begin..position];
                var equals = directive.IndexOf('=');
                var key = (equals < 0 ? directive : directive[..equals]).ToLowerInvariant();
                var value = equals < 0 ? "" : directive[(equals + 1)..];
                if (key == "name")
                {
                    if (value.Length == 0) throw new ArgumentException($"未対応または不正な行変換directive: @{directive}");
                    if (value[0] == '"')
                    {
                        var endQuote = value.IndexOf('"', 1);
                        if (endQuote < 0) throw new ArgumentException($"未対応または不正な行変換directive: @{directive}");
                        FilterName = value[1..endQuote];
                    }
                    else FilterName = value;
                    continue;
                }
                if (value.Length != 0) throw new ArgumentException($"未対応または不正な行変換directive: @{directive}");
                switch (key)
                {
                    case "cs": case "casesensitive": CaseSensitive = true; break;
                    case "ci": case "caseinsensitive": CaseSensitive = false; break;
                    case "opt": case "optimize": Optimize = true; break;
                    case "noopt": case "nooptimize": Optimize = false; break;
                    default: throw new ArgumentException($"未対応または不正な行変換directive: @{directive}");
                }
            }
            Next();
            var node = ParseOr();
            if (current != "end") throw Error();
            return node;
        }

        private Node ParseOr()
        {
            if (++depth > MaximumDepth) throw Error();
            var node = ParseAnd();
            while (current == "or") { Next(); node = new Binary("or", node, ParseAnd()); }
            depth--;
            return node;
        }
        private Node ParseAnd()
        {
            var node = ParseNot();
            while (current == "and") { Next(); node = new Binary("and", node, ParseNot()); }
            return node;
        }
        private Node ParseNot()
        {
            if (current != "not") return ParseCompare();
            if (++depth > MaximumDepth) throw Error();
            Next();
            var node = new Unary("not", ParseNot());
            depth--;
            return node;
        }
        private Node ParseCompare()
        {
            var node = ParseAdd();
            var negate = current == "not";
            if (negate) Next();
            if (current is "=" or "!=" or "<" or "<=" or ">" or ">=" or "contains" or "matches" or "recontains" or "like")
            {
                var op = current;
                if (negate && op is not ("contains" or "matches" or "recontains" or "like")) throw Error();
                Next();
                node = new Binary(op, node, ParseAdd());
                return negate ? new Unary("not", node) : node;
            }
            if (negate) throw Error();
            return node;
        }
        private Node ParseAdd()
        {
            var node = ParseMultiply();
            while (current is "+" or "-") { var op = current; Next(); node = new Binary(op, node, ParseMultiply()); }
            return node;
        }
        private Node ParseMultiply()
        {
            var node = ParseUnary();
            while (current is "*" or "/" or "%") { var op = current; Next(); node = new Binary(op, node, ParseUnary()); }
            return node;
        }
        private Node ParseUnary()
        {
            if (current != "-") return ParseTerm();
            if (++depth > MaximumDepth) throw Error();
            Next();
            var node = new Unary("-", ParseUnary());
            depth--;
            return node;
        }
        private Node ParseTerm()
        {
            if (current == "literal") { var node = new Literal(literal, literalOrigin); Next(); return node; }
            if (current == "(") { Next(); var node = ParseOr(); Expect(")"); return node; }
            if (current != "identifier") throw Error();
            var name = (string)literal!;
            Next();
            if (current == "(")
            {
                Next();
                var arguments = new List<Node>();
                if (current != ")")
                    while (true)
                    {
                        budget.Charge(1, token);
                        arguments.Add(ParseOr());
                        if (current != ",") break;
                        Next();
                    }
                Expect(")");
                var unprefixed = Unprefix(name);
                var providerFunction = unprefixed is "column" or "columnrange" or "lineat" or "lineoffsetat" or "columnat" or "columnoffsetat";
                if (name != unprefixed && !providerFunction) throw new ArgumentException($"未対応の行変換関数: {name}");
                if (providerFunction) name = unprefixed;
                var (minimum, maximum) = name switch
                {
                    "logerror" or "loginfo" or "logwarn" => (1, MaximumTokens),
                    "if" or "ifeach" or "replace" or "regexreplace" or "inrange" or "iswithin" => (3, 3), "columnat" or "columnoffsetat" or "at" or "andeach" or "oreach" or "strcount" or "regexcount" => (2, 2), "substr" or "strfind" or "sublines" => (2, 3),
                    "regexfind" or "regexextract" => (2, 4),
                    "now" or "today" => (0, 0), "array" => (0, MaximumTokens), "allequal" => (1, MaximumTokens), "choose" or "chooseeach" => (2, MaximumTokens),
                    "average" or "sum" or "maximum" or "minimum" or "normalizeunicode" => (1, 2),
                    "matchcontext" => (3, 3),
                    "matchinside" or "replacewithlist" or "regexreplacewithlist" => (2, 2),
                    "column" or "columnrange" or "lineat" or "lineoffsetat" or "abs" or "len" or "strlen" or "tolower" or "toupper" or "linecount"
                        or "tohalfwidth" or "tofullwidth" or "tohiragana" or "tokatakana" or "tosimplifiedchinese" or "totraditionalchinese"
                        or "anyof" or "allof" or "noteach" or "trim" or "trimleft" or "trimright" or "toint" or "todouble" or "tonumber" or "todatetime" or "todatestr" or "startofweek" or "startofmonth" or "startofyear" => (1, 1),
                    "matchblockcount" or "matchblocknumber" or "matchblocksize" or "matchblockoffset"
                        or "matchcount" or "matchnumber" or "matchdistance" or "matchdistancebefore" or "matchdistanceafter" => (1, 1),
                    _ => throw new ArgumentException($"未対応の行変換関数: {name}")
                };
                if (arguments.Count < minimum || arguments.Count > maximum) throw new ArgumentException($"行変換関数{name}の引数数が不正です。");
                if (name is "average" or "sum" or "maximum" or "minimum") UsesAggregates = true;
                if (name is "matchinside" or "matchcontext" or "matchblockcount" or "matchblocknumber" or "matchblocksize" or "matchblockoffset"
                    or "matchcount" or "matchnumber" or "matchdistance" or "matchdistancebefore" or "matchdistanceafter") UsesMatchRanges = true;
                if (name == "sublines") UsesSublines = true;
                budget.Charge(arguments.Count, token);
                if (name is "now" or "today" && timestampClock is null)
                    throw new ArgumentException("Now/Todayには明示的な時計環境が必要です。");
                if (name is "tolower" or "toupper" && stringEnvironment is null)
                    throw new ArgumentException("文字列の大小文字変換には明示的な環境が必要です。");
                if (name == "normalizeunicode" && stringEnvironment is null)
                    throw new ArgumentException("Unicode正規化には明示的な環境が必要です。");
                if (name is "tohalfwidth" or "tofullwidth" or "tohiragana" or "tokatakana" or "tosimplifiedchinese" or "totraditionalchinese"
                    && stringEnvironment is null)
                    throw new ArgumentException("文字種変換には明示的な環境が必要です。");
                return new Function(name, arguments.ToArray(), timestampEnvironment: timestampEnvironment,
                    timestampClock: timestampClock, stringEnvironment: stringEnvironment,
                    logEnvironment: logEnvironment, filterName: FilterName, listEnvironment: listEnvironment);
            }
            name = Unprefix(name);
            if (name == "content") { UsesContent = true; return new Field(name); }
            if (name is "line" or "linenumber" or "linelength" or "columncount") return new Field(name);
            if (name.StartsWith("column", StringComparison.Ordinal)
                && int.TryParse(name.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var column) && column >= 1)
                return new Field("column", column);
            throw new ArgumentException($"未対応の行変換フィールド: {name}");
        }

        private static string Unprefix(string name) => name.StartsWith("left", StringComparison.Ordinal) ? name[4..]
            : name.StartsWith("middle", StringComparison.Ordinal) ? name[6..]
            : name.StartsWith("right", StringComparison.Ordinal) ? name[5..] : name;

        private void Expect(string expected) { if (current != expected) throw Error(); Next(); }
        private ArgumentException Error() => new($"行変換の構文が不正か未対応です（文字位置{position}）。");
        private static bool IsSpace(char character) => character is ' ' or '\t' or '\r' or '\n';
        private void SkipSpace() { while (position < text.Length && IsSpace(text[position])) { budget.Charge(1, token); position++; } }

        private long ReadSizeLiteral(ReadOnlySpan<char> number, bool hasExponent)
        {
            // 容量は旧lexerの整数／小数だけ。通常の実数literalの文法は変えない。
            if (hasExponent || !char.IsAsciiDigit(number[0]) || !char.IsAsciiDigit(number[^1])) throw Error();
            // 旧lexerは既知unitだけを消費し、隣接するANDなどは次のtokenに渡す。
            var remaining = text.AsSpan(position);
            var rank = remaining.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ? 1
                : remaining.StartsWith("MB", StringComparison.OrdinalIgnoreCase) ? 2
                : remaining.StartsWith("GB", StringComparison.OrdinalIgnoreCase) ? 3
                : remaining.StartsWith("TB", StringComparison.OrdinalIgnoreCase) ? 4
                : remaining.StartsWith("B", StringComparison.OrdinalIgnoreCase) ? 0 : -1;
            if (rank < 0) throw new ArgumentException("未対応の型付きliteralです。");
            var unitLength = rank == 0 ? 1 : 2;
            position += unitLength;
            budget.Charge(unitLength, token);
            // 原典のstd::stod失敗はcatch後の0を容量値に使う。
            if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var scaled)
                || !double.IsFinite(scaled)) scaled = 0;
            // 原典のdoubleによる左結合乗算とint64への切捨てを保ち、範囲外castは拒否する。
            for (var index = 0; index < rank; index++)
            {
                budget.Charge(1, token);
                scaled *= 1024;
            }
            if (!double.IsFinite(scaled) || scaled < 0 || scaled >= 9223372036854775808d) throw Error();
            return (long)scaled;
        }
        private static readonly IReadOnlyList<(string Unit, int Factor, bool Milliseconds)> DurationUnits =
            Array.AsReadOnly(new (string Unit, int Factor, bool Milliseconds)[]
            {
                ("milliseconds", 1000, true), ("millisecond", 1000, true),
                ("minutes", 60, false), ("seconds", 1, false),
                ("minute", 60, false), ("second", 1, false),
                ("weeks", 604800, false), ("hours", 3600, false),
                ("week", 604800, false), ("days", 86400, false),
                ("hour", 3600, false), ("msec", 1000, true),
                ("day", 86400, false), ("min", 60, false), ("sec", 1, false),
                ("hr", 3600, false), ("ms", 1000, true),
                ("w", 604800, false), ("d", 86400, false), ("h", 3600, false),
                ("m", 60, false), ("s", 1, false)
            });

        private static bool StartsWithAsciiUnit(ReadOnlySpan<char> remaining, string unit)
        {
            if (remaining.Length < unit.Length) return false;
            for (var index = 0; index < unit.Length; index++)
            {
                var character = remaining[index];
                if (character is >= 'A' and <= 'Z') character = (char)(character + ('a' - 'A'));
                if (character != unit[index]) return false;
            }
            return true;
        }

        private bool TryReadDurationLiteral(ReadOnlySpan<char> number, bool hasExponent, out long value)
        {
            value = 0;
            var remaining = text.AsSpan(position);
            // 長い既知unitから選び、後続のANDなどは既存parserへ渡す。
            foreach (var unit in DurationUnits)
            {
                if (!StartsWithAsciiUnit(remaining, unit.Unit)) continue;
                if (hasExponent || !char.IsAsciiDigit(number[0]) || !char.IsAsciiDigit(number[^1])) throw Error();
                position += unit.Unit.Length;
                budget.Charge(unit.Unit.Length, token);
                // 原典のstd::stod失敗はcatch後の0を期間値に使う。
                if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var scaled)
                    || !double.IsFinite(scaled)) scaled = 0;
                budget.Charge(1, token);
                scaled *= unit.Factor;
                if (!unit.Milliseconds)
                {
                    // 秒への乗算とmicrosecondへの乗算を結合せず、原典の丸めを保つ。
                    budget.Charge(1, token);
                    scaled *= 1000000;
                }
                if (!double.IsFinite(scaled) || scaled < 0 || scaled >= 9223372036854775808d) throw Error();
                value = (long)scaled;
                return true;
            }
            return false;
        }
        private string ReadQuotedLiteral()
        {
            var output = new StringBuilder();
            var closed = false;
            while (position < text.Length)
            {
                budget.Charge(1, token, 1);
                var character = text[position++];
                if (character == '\0') throw Error();
                if (character == '"')
                {
                    if (position < text.Length && text[position] == '"') position++;
                    else { closed = true; break; }
                }
                output.Append(character);
            }
            if (!closed) throw Error();
            budget.Charge(output.Length, token, output.Length);
            return output.ToString();
        }

        private long ReadVersionLiteral(string content)
        {
            // 数値への変換でtailが消える前に有効なUTF-16を確認する。ASCII以外を数字や空白にはしない。
            // 有効なUnicodeは保持し、scanfの非ASCII locale同等性までは主張しない。
            for (var index = 0; index < content.Length;)
            {
                budget.Charge(1, token);
                var previous = index;
                _ = ReadScalar(content, ref index);
                if (index - previous == 2) budget.Charge(1, token);
            }
            // sscanf_s("%d.%d.%d.%d")は前方の変換だけを採用し、未変換の部品は0。
            Span<int> components = stackalloc int[4];
            components.Clear();
            var cursor = 0;
            for (var component = 0; component < components.Length; component++)
            {
                while (cursor < content.Length && content[cursor] is ' ' or '\t' or '\r' or '\n' or '\v' or '\f')
                {
                    budget.Charge(1, token);
                    cursor++;
                }
                var negative = false;
                if (cursor < content.Length && content[cursor] is '+' or '-')
                {
                    budget.Charge(1, token);
                    negative = content[cursor++] == '-';
                }
                var firstDigit = cursor;
                var magnitude = 0L;
                var limit = negative ? 2147483648L : int.MaxValue;
                while (cursor < content.Length && char.IsAsciiDigit(content[cursor]))
                {
                    budget.Charge(1, token);
                    var digit = content[cursor++] - '0';
                    if (magnitude > (limit - digit) / 10)
                        throw new ArgumentException("version literalの部品がInt32の範囲を超えています。");
                    magnitude = magnitude * 10 + digit;
                }
                if (cursor == firstDigit) break;
                components[component] = (int)(negative ? -magnitude : magnitude);
                if (component == 3 || cursor == content.Length || content[cursor] != '.') break;
                budget.Charge(1, token);
                cursor++;
            }
            // 原典C++17は非負のshift積がunsigned64に収まる範囲を許可し、signed64へ変換する。
            budget.Charge(4, token);
            if (components[0] < 0 || components[0] > 65535 || components[1] < 0 || components[2] < 0)
                throw new ArgumentException("version literalの上位部品はC++17のshiftの範囲外です。");
            try
            {
                // マスク／ORに変えず、部品の重なりと負のrevisionを含め左結合の加算を保つ。
                // MSVCのsigned変換に対応する符号bitを保持する。以降の加算は各段階で検査する。
                var value = (long)components[0] << 48;
                budget.Charge(1, token);
                value = checked(value + ((long)components[1] << 32));
                budget.Charge(1, token);
                value = checked(value + ((long)components[2] << 16));
                budget.Charge(1, token);
                return checked(value + components[3]);
            }
            catch (OverflowException)
            {
                throw new ArgumentException("version literalの部品の加算がInt64の範囲を超えています。");
            }
        }
        private void Next()
        {
            budget.Charge(1, token);
            if (++tokens > MaximumTokens) throw new ArgumentException("行変換のtoken数が上限を超えています。");
            SkipSpace();
            literal = null;
            literalOrigin = LiteralOrigin.Ordinary;
            if (position == text.Length) { current = "end"; return; }
            var begin = position;
            var character = text[position++];
            if (character is 'd' or 'D' && position < text.Length && text[position] == '"')
            {
                budget.Charge(1, token); position++;
                var content = ReadQuotedLiteral();
                if (timestampEnvironment is null) throw new ArgumentException("日時リテラルには明示的な環境が必要です。");
                if (content.Contains('\0')) throw Error();
                for (var at = 0; at < content.Length;) { budget.Charge(1, token); _ = ReadScalar(content, ref at); }
                budget.Charge(content.Length * 16L + 1, token, content.Length * 2L);
                token.ThrowIfCancellationRequested();
                var constructed = LineTransformTimestampParser.Construct(content, new TimestampEnvironmentAdapter(timestampEnvironment));
                token.ThrowIfCancellationRequested();
                if (!constructed.Success) throw new ArgumentException(constructed.Error);
                literal = new TimestampValue(constructed.Value.Value, timestampEnvironment);
                literalOrigin = LiteralOrigin.Timestamp; current = "literal"; return;
            }
            if (character is 'v' or 'V' && position < text.Length && text[position] == '"')
            {
                budget.Charge(1, token);
                position++;
                literal = ReadVersionLiteral(ReadQuotedLiteral());
                literalOrigin = LiteralOrigin.Version;
                current = "literal";
                return;
            }
            if (character == '"')
            {
                literal = ReadQuotedLiteral();
                current = "literal";
                return;
            }
            if (char.IsAsciiDigit(character) || character == '.' && position < text.Length && char.IsAsciiDigit(text[position]))
            {
                while (position < text.Length && char.IsAsciiDigit(text[position])) position++;
                var real = character == '.';
                var exponentAllowed = !real;
                var hasExponent = false;
                if (position < text.Length && text[position] == '.')
                {
                    real = true;
                    position++;
                    var fraction = position;
                    while (position < text.Length && char.IsAsciiDigit(text[position])) position++;
                    exponentAllowed = position > fraction;
                }
                // .1e2 / 1.e2 は旧re2c grammarでは一つの実数tokenにならない。
                if (exponentAllowed && position < text.Length && text[position] is 'e' or 'E')
                {
                    real = true;
                    hasExponent = true;
                    position++;
                    if (position < text.Length && text[position] is '+' or '-') position++;
                    var exponent = position;
                    while (position < text.Length && char.IsAsciiDigit(text[position])) position++;
                    if (exponent == position) throw Error();
                }
                budget.Charge(position - begin, token);
                var number = text.AsSpan(begin, position - begin);
                var remaining = text.AsSpan(position);
                if (remaining.StartsWith("B", StringComparison.OrdinalIgnoreCase)
                    || remaining.StartsWith("KB", StringComparison.OrdinalIgnoreCase)
                    || remaining.StartsWith("MB", StringComparison.OrdinalIgnoreCase)
                    || remaining.StartsWith("GB", StringComparison.OrdinalIgnoreCase)
                    || remaining.StartsWith("TB", StringComparison.OrdinalIgnoreCase))
                {
                    literal = ReadSizeLiteral(number, hasExponent);
                    literalOrigin = LiteralOrigin.Size;
                    current = "literal";
                    return;
                }
                if (TryReadDurationLiteral(number, hasExponent, out var duration))
                {
                    literal = duration;
                    literalOrigin = LiteralOrigin.Duration;
                    current = "literal";
                    return;
                }
                if (real)
                {
                    if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed)) throw Error();
                    literal = parsed;
                }
                else
                {
                    // 旧lexerのstd::stoi範囲を保つ。64bit値はToInt(string)で得る。
                    if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) throw Error();
                    literal = (long)parsed;
                }
                current = "literal";
                return;
            }
            if (char.IsAsciiLetter(character) || character == '_')
            {
                while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] == '_')) position++;
                budget.Charge((position - begin) * 2L, token, (position - begin) * 2L);
                var word = text[begin..position].ToLowerInvariant();
                if (position < text.Length && text[position] == '"' && word == "d")
                    throw new ArgumentException("datetime literalには未対応です。");
                if (word is "true" or "false" or "none")
                {
                    literal = word == "none" ? null : word == "true";
                    current = "literal";
                }
                else if (word is "and" or "or" or "not" or "contains" or "matches" or "recontains" or "like") current = word;
                else { current = "identifier"; literal = word; }
                return;
            }
            if (character is '=' or '!' or '<' or '>')
            {
                if (position < text.Length && text[position] == '=') position++;
                current = text[begin..position];
                if (current == "==") current = "=";
                if (current == "!") throw Error();
                return;
            }
            if (character is '(' or ')' or '+' or '-' or '*' or '/' or '%' or ',') { current = character.ToString(); return; }
            throw Error();
        }
    }
}
