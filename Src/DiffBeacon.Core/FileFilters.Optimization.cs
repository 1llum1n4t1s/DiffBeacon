using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DiffBeacon.Core;

// 固定原典: WinMerge 6780924ba35f7f55af0f6f14dd302561175830a6
// Src/FilterEngine/FilterExpressionNodes.cpp の Optimize / TryFoldConstants。
// 原典sourceと実測資格は revision-r3/post-capture-r2。公開parser全体の実測とは区別する。
public sealed partial class FileFilter
{
    // 原典のliteralクラスはruntime variantの型とは別。Size/Duration/VersionをIntとして
    // FunctionNodeのguardへ渡すとArray/At/Abs/Substrの連鎖が変わってしまう。
    private enum LiteralClass { Other, None, Bool, Int, Double, String, DateTime, Size, Duration, Version, Array, Regex }
    private static LiteralClass ClassOf(Node node) => node.Effective switch
    {
        SizeLiteral => LiteralClass.Size,
        DurationLiteral => LiteralClass.Duration,
        VersionLiteral => LiteralClass.Version,
        NumberLiteral => LiteralClass.Double,
        Literal literal => literal.Value.Kind switch
        {
            ValueKind.Null => LiteralClass.None, ValueKind.Boolean => LiteralClass.Bool,
            ValueKind.Number => LiteralClass.Int, ValueKind.Floating => LiteralClass.Double,
            ValueKind.String => LiteralClass.String, ValueKind.Date or ValueKind.Timestamp => LiteralClass.DateTime,
            ValueKind.Array => LiteralClass.Array, ValueKind.Regex => LiteralClass.Regex,
            _ => LiteralClass.Other
        },
        _ => LiteralClass.Other
    };
    private static bool IntLike(Node node) => ClassOf(node) is LiteralClass.Int or LiteralClass.Size or LiteralClass.Duration or LiteralClass.Version;
    private static bool IsNumericKindPair(ValueKind left, ValueKind right)
        => left is ValueKind.Number or ValueKind.Floating && right is ValueKind.Number or ValueKind.Floating;
    private sealed record DurationLiteral(long Microseconds) : Node(ValueKind.Number)
    {
        public override Value Evaluate(Context context) => new(ValueKind.Number, Number: Microseconds);
    }
    private sealed record VersionLiteral(long Packed) : Node(ValueKind.Number)
    {
        public override Value Evaluate(Context context) => new(ValueKind.Number, Number: Packed);
    }
    private static bool TryDurationLiteral(string text, int suffix, out long value)
    {
        var multiplier = text[suffix..].ToLowerInvariant() switch
        {
            "w" or "week" or "weeks" => 604800e6, "d" or "day" or "days" => 86400e6,
            "h" or "hr" or "hour" or "hours" => 3600e6, "m" or "min" or "minute" or "minutes" => 60e6,
            "s" or "sec" or "second" or "seconds" => 1e6,
            "ms" or "msec" or "millisecond" or "milliseconds" => 1000d, _ => 0
        };
        value = 0;
        if (multiplier == 0) return false;
        var scaled = double.Parse(text[..suffix], CultureInfo.InvariantCulture) * multiplier;
        if (!double.IsFinite(scaled) || scaled < long.MinValue || scaled >= 9223372036854775808d)
            throw new FormatException("期間リテラルが64bit整数の範囲を超えています。");
        value = (long)scaled;
        return true;
    }
    private static long ParseVersionLiteral(string text)
    {
        // sscanf("%d.%d.%d.%d")と同じく、読めた成分だけ採り、残りを0にする。
        var cursor = 0;
        Span<int> parts = stackalloc int[4];
        parts.Clear();
        for (var index = 0; index < parts.Length; index++)
        {
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            var start = cursor;
            if (cursor < text.Length && text[cursor] is '+' or '-') cursor++;
            var digits = cursor;
            while (cursor < text.Length && text[cursor] is >= '0' and <= '9') cursor++;
            if (cursor == digits) break;
            if (!int.TryParse(text.AsSpan(start, cursor - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out parts[index]))
                throw new FormatException("Versionの成分が32bit整数の範囲を超えています。");
            if (cursor == text.Length || text[cursor++] != '.') break;
        }
        return unchecked(((long)parts[0] << 48) + ((long)parts[1] << 32) + ((long)parts[2] << 16) + parts[3]);
    }
    private sealed record Negate(Node Argument) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Argument.UsesProperties;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context) => Negated(Argument.EvaluatePrepared(context));
    }
    private static Value Negated(Value value) => value.Kind switch
    {
        ValueKind.Number => new(ValueKind.Number, Number: unchecked(-(long)value.Number)),
        ValueKind.Floating => new(ValueKind.Floating, Floating: -value.Floating), _ => new(ValueKind.Null)
    };
    private sealed record ArithmeticValue(string Operator, Node Left, Node Right) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Left.UsesProperties || Right.UsesProperties;
        public override bool UsesSideMetadata => Left.UsesSideMetadata || Right.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Left.RequiresFolderComparisonContext || Right.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
            => ArithmeticCompute(Operator, Left.EvaluatePrepared(context), Right.EvaluatePrepared(context), context);
    }
    private static Value AsTimestamp(Value value) => value.Kind == ValueKind.Date
        ? new(ValueKind.Timestamp, Number: (value.Date.Ticks - DateTime.UnixEpoch.Ticks) / 10) : value;
    private static Value ArithmeticCompute(string op, Value a, Value b, Context context, bool folding = false)
    {
        context.WorkBudget.Consume(1);
        a = AsTimestamp(a); b = AsTimestamp(b);
        if (a.Kind == ValueKind.Array || b.Kind == ValueKind.Array)
        {
            var count = Math.Max(a.Items?.Count ?? 0, b.Items?.Count ?? 0);
            context.WorkBudget.Consume(count);
            var items = new Value[count];
            for (var index = 0; index < count; index++)
                items[index] = a.Kind == ValueKind.Array && index >= a.Items!.Count || b.Kind == ValueKind.Array && index >= b.Items!.Count
                    ? new(ValueKind.Null) : ArithmeticCompute(op, a.Kind == ValueKind.Array ? a.Items![index] : a,
                        b.Kind == ValueKind.Array ? b.Items![index] : b, context);
            return new(ValueKind.Array, Items: items);
        }
        if (op == "+" && (a.Kind == ValueKind.String || b.Kind == ValueKind.String))
        {
            byte[] left = a.Kind == ValueKind.Null ? [] : NativeString(a, context.WorkBudget, context.TimestampEnvironment);
            byte[] right = b.Kind == ValueKind.Null ? [] : NativeString(b, context.WorkBudget, context.TimestampEnvironment);
            context.WorkBudget.Consume(checked(left.Length + right.Length));
            return Value.Str([.. left, .. right]);
        }
        if (a.Kind == ValueKind.Number && b.Kind == ValueKind.Number)
        {
            var left = (long)a.Number; var right = (long)b.Number;
            if (op is "/" or "%" && right == 0) throw new DivideByZeroException("フィルター式で0による除算はできません。");
            return new(ValueKind.Number, Number: op switch
            {
                "+" => unchecked(left + right), "-" => unchecked(left - right), "*" => unchecked(left * right),
                "/" => left / right, "%" => left % right, _ => throw new InvalidOperationException("未対応の算術演算子です。")
            });
        }
        if (IsNumericKindPair(a.Kind, b.Kind))
        {
            var left = a.Kind == ValueKind.Floating ? a.Floating : (double)a.Number;
            var right = b.Kind == ValueKind.Floating ? b.Floating : (double)b.Number;
            if (op == "%") return new(ValueKind.Null);
            if (op == "/" && right == 0) throw new DivideByZeroException("フィルター式で0による除算はできません。");
            return new(ValueKind.Floating, Floating: op switch
            { "+" => left + right, "-" => left - right, "*" => left * right, "/" => left / right, _ => double.NaN });
        }
        if (a.Kind == ValueKind.Timestamp && b.Kind == ValueKind.Timestamp && op == "-")
            return new(ValueKind.Number, Number: unchecked((long)a.Number - (long)b.Number));
        if (a.Kind == ValueKind.Timestamp && b.Kind == ValueKind.Number && op is "+" or "-")
            return new(ValueKind.Timestamp, Number: op == "+" ? unchecked((long)a.Number + (long)b.Number) : unchecked((long)a.Number - (long)b.Number));
        if (a.Kind == ValueKind.Boolean && b.Kind == ValueKind.Boolean && op is "+" or "-")
        {
            var left = a.Boolean ? 1 : 0; var right = b.Boolean ? 1 : 0;
            // 原典はTryFoldConstantsのl-rとruntime computeのr-lが異なる。
            return new(ValueKind.Number, Number: op == "+" ? left + right : folding ? left - right : right - left);
        }
        return new(ValueKind.Null);
    }

    private static IEnumerable<Node> OptimizationChildren(Node node) => node switch
    {
        Logical n => n.Right is null ? [n.Left] : [n.Left, n.Right],
        Comparison n => [n.Left, n.Right], ArithmeticValue n => [n.Left, n.Right], Negate n => [n.Argument],
        FolderProviderUnavailable n => n.Arguments, FolderSideValue n => n.Arguments, LogValue n => n.Arguments,
        RegexCount n => [n.Source, n.Pattern],
        RegexPosition n => new Node?[] { n.Source, n.Pattern, n.Group, n.Occurrence }.OfType<Node>(),
        RegexReplace n => [n.Source, n.Pattern, n.Replacement], ReplaceList n => [n.Source, n.List],
        Len n => [n.Argument], ChangeCase n => [n.Argument], CharacterMapping n => [n.Argument],
        ToDateTime n => [n.Argument], ToDateStr n => [n.Argument], StartOf n => [n.Argument],
        NormalizeUnicode n => n.Form is null ? [n.Argument] : [n.Argument, n.Form],
        Strlen n => [n.Argument], InRange n => [n.Argument, n.Lower, n.Upper], Abs n => [n.Argument],
        NumericConversion n => [n.Argument], StringSearch n => n.Occurrence is null ? [n.Haystack, n.Needle] : [n.Haystack, n.Needle, n.Occurrence],
        Substr n => n.Length is null ? [n.Source, n.Start] : [n.Source, n.Start, n.Length], TrimValue n => [n.Argument],
        NotEach n => [n.Argument], AnyOf n => [n.Argument], AllOf n => [n.Argument], At n => [n.Array, n.Index],
        LogicalEach n => [n.Left, n.Right], Choice n => new[] { n.Index }.Concat(n.Choices),
        Conditional n => [n.Condition, n.WhenTrue, n.WhenFalse], ConditionalEach n => [n.Condition, n.WhenTrue, n.WhenFalse],
        ArrayValue n => n.Arguments, AllEqual n => n.Arguments,
        Literal or NumberLiteral or SizeLiteral or DurationLiteral or VersionLiteral or ClockValue or ShellProperty or Property => [],
        _ => throw new InvalidOperationException("最適化するノードの子定義がありません。")
    };
    private sealed class OptimizationRejectedException(string message, Exception cause) : FormatException(message, cause) { }
    private static void OptimizeTree(Node root, Context context)
    {
        try { OptimizeNode(root, context); }
        catch (DivideByZeroException error) { throw new OptimizationRejectedException(error.Message, error); }
        // 原典reductionのstd::exceptionはrootを残す。明示拒否・取消・予算超過は握り潰さない。
        catch (Exception error) when (error is ArgumentException or OverflowException or InvalidCastException
            || error is FormatException and not OptimizationRejectedException) { }
    }
    private static void OptimizeNode(Node node, Context context)
    {
        context.WorkBudget.Consume(1);
        foreach (var child in OptimizationChildren(node)) OptimizeNode(child, context);
        var folded = FoldNode(node, context);
        if (folded is not null) node.PreparedReplacement = folded;
    }
    private static Literal FoldValue(Value value) => new(AsTimestamp(value));
    private static Literal RequiredFoldValue(Value value, ValueKind expected)
        => value.Kind == expected ? FoldValue(value) : throw new InvalidCastException("原典std::getの型が一致しません。");
    private static bool IsClass(Node node, LiteralClass kind) => ClassOf(node) == kind;
    private static Node? FoldNode(Node node, Context context)
    {
        if (node is ClockValue clock) return FoldValue(clock.Value);
        if (node is Logical logical && IsClass(logical.Left, LiteralClass.Bool)
            && (logical.Right is null || IsClass(logical.Right, LiteralClass.Bool)))
            return FoldValue(logical.Evaluate(context));
        if (node is Negate negate && (IntLike(negate.Argument) || IsClass(negate.Argument, LiteralClass.Double)))
            return FoldValue(negate.Evaluate(context));
        if (node is ArithmeticValue arithmetic)
        {
            var left = ClassOf(arithmetic.Left); var right = ClassOf(arithmetic.Right); var op = arithmetic.Operator;
            var allowed = IntLike(arithmetic.Left) && IntLike(arithmetic.Right)
                || left == LiteralClass.Double && right == LiteralClass.Double && op != "%"
                || left == LiteralClass.String && right == LiteralClass.String && op == "+"
                || left == LiteralClass.Bool && right == LiteralClass.Bool && op is "+" or "-"
                || left == LiteralClass.DateTime && right == LiteralClass.DateTime && op == "-"
                || left == LiteralClass.DateTime && IntLike(arithmetic.Right) && op is "+" or "-";
            return allowed ? FoldValue(ArithmeticCompute(op, arithmetic.Left.EvaluatePrepared(context), arithmetic.Right.EvaluatePrepared(context), context, true)) : null;
        }
        if (node is Comparison comparison)
        {
            var left = ClassOf(comparison.Left); var right = ClassOf(comparison.Right);
            if (comparison.Operator is "=" or "==" or "!=" or "<" or "<=" or ">" or ">="
                && (IntLike(comparison.Left) && IntLike(comparison.Right)
                    || left == right && left is LiteralClass.Double or LiteralClass.String or LiteralClass.Bool or LiteralClass.DateTime))
                return FoldValue(comparison.Evaluate(context));
            if (comparison.Operator is "matches" or "recontains" && right == LiteralClass.String)
            {
                try
                {
                    var bytes = comparison.Right.EvaluatePrepared(context).Text!;
                    context.WorkBudget.Consume(bytes.Length);
                    comparison.PreparedBinaryPattern = MakeRegex(StrictUtf8.GetString(bytes), comparison.CaseSensitive, comparison.Operator == "matches");
                    comparison.Right.PreparedReplacement = new Literal(new(ValueKind.Regex,
                        CompiledRegex: new(comparison.PreparedBinaryPattern, false)));
                }
                catch (Exception error) when (error is ArgumentException or FormatException)
                { throw new OptimizationRejectedException(error.Message, error); }
            }
            return null;
        }
        if (node is FolderSideValue side)
        {
            try { side.PrepareLiteralRange(context); }
            catch (ArgumentException error) { throw new OptimizationRejectedException(error.Message, error); }
            return null;
        }
        // FunctionNodeの22 guard。実行時の純粋性からguardを増やさない。
        if (node is Abs abs && ClassOf(abs.Argument) is LiteralClass.Int or LiteralClass.Double)
            return RequiredFoldValue(abs.Evaluate(context), IsClass(abs.Argument, LiteralClass.Int) ? ValueKind.Number : ValueKind.Floating);
        if (node is ArrayValue array && array.Arguments.Count != 0
            && array.Arguments.All(arg => ClassOf(arg) is LiteralClass.Int or LiteralClass.Double or LiteralClass.String or LiteralClass.DateTime or LiteralClass.Bool))
        {
            context.WorkBudget.Consume(array.Arguments.Count);
            return FoldValue(new(ValueKind.Array, Items: array.Arguments.Select(arg => AsTimestamp(arg.EvaluatePrepared(context))).ToArray()));
        }
        if (node is At at && IsClass(at.Array, LiteralClass.Array) && IsClass(at.Index, LiteralClass.Int))
        {
            var values = at.Array.EvaluatePrepared(context).Items!; var index = (long)at.Index.EvaluatePrepared(context).Number;
            if (index < 0) index += values.Count;
            if (index >= 0 && index < values.Count && values[(int)index].Kind is ValueKind.String or ValueKind.Floating or ValueKind.Number or ValueKind.Boolean or ValueKind.Timestamp or ValueKind.Date)
                return FoldValue(values[(int)index]);
        }
        if (node is Strlen strlen && IsClass(strlen.Argument, LiteralClass.String)) return RequiredFoldValue(strlen.Evaluate(context), ValueKind.Number);
        if (node is Substr substr && IsClass(substr.Source, LiteralClass.String) && IsClass(substr.Start, LiteralClass.Int)
            && (substr.Length is null || IsClass(substr.Length, LiteralClass.Int))) return RequiredFoldValue(substr.Evaluate(context), ValueKind.String);
        if (node is RegexReplace replace)
        {
            if (!OptimizeRegexArgument(replace.Pattern, replace.CaseSensitive, replace.Literal, context)) return null;
            if (IsClass(replace.Source, LiteralClass.String) && IsClass(replace.Pattern, LiteralClass.Regex) && IsClass(replace.Replacement, LiteralClass.String))
                return RequiredFoldValue(replace.Evaluate(context), ValueKind.String);
        }
        if (node is StringSearch search && IsClass(search.Haystack, LiteralClass.String) && IsClass(search.Needle, LiteralClass.String)
            && (!search.Find || search.Occurrence is null || IsClass(search.Occurrence, LiteralClass.Int)))
        {
            var value = search.Evaluate(context);
            if (!search.Find) return RequiredFoldValue(value, ValueKind.Number);
            if (value.Kind == ValueKind.Number) return FoldValue(value);
        }
        if (node is RegexCount count)
        {
            OptimizeRegexArgument(count.Pattern, count.CaseSensitive, false, context);
            if (IsClass(count.Source, LiteralClass.String) && IsClass(count.Pattern, LiteralClass.Regex)) return RequiredFoldValue(count.Evaluate(context), ValueKind.Number);
        }
        if (node is RegexPosition position)
        {
            OptimizeRegexArgument(position.Pattern, position.CaseSensitive, false, context);
            if (IsClass(position.Source, LiteralClass.String) && IsClass(position.Pattern, LiteralClass.Regex)
                && (position.Group is null || IsClass(position.Group, LiteralClass.Int))
                && (position.Occurrence is null || IsClass(position.Occurrence, LiteralClass.Int)))
            {
                var value = position.Evaluate(context);
                if (value.Kind is ValueKind.Number or ValueKind.String) return FoldValue(value);
            }
        }
        if (node is ReplaceList list && IsClass(list.List, LiteralClass.String))
            list.List.PreparedReplacement = FoldValue(list.PrepareList(context));
        if (node is StartOf start && IsClass(start.Argument, LiteralClass.DateTime)) return RequiredFoldValue(start.Evaluate(context), ValueKind.Timestamp);
        if (node is NumericConversion conversion && IsClass(conversion.Argument, LiteralClass.String))
        {
            var value = conversion.Evaluate(context);
            if (value.Kind is ValueKind.Number or ValueKind.Floating) return FoldValue(value);
        }
        if (node is ToDateTime date && IsClass(date.Argument, LiteralClass.String))
        {
            var value = date.Evaluate(context);
            if (value.Kind == ValueKind.Timestamp) return FoldValue(value);
        }
        return null;
    }
    private static LineTransformExpression.RegexWorkContext OptimizationRegexWork(Context context, bool caseSensitive)
        => new(caseSensitive, context.CancellationToken, new((amount, token, output) =>
        {
            token.ThrowIfCancellationRequested();
            context.WorkBudget.Consume(amount is >= 0 and <= int.MaxValue ? (int)amount : -1);
            context.WorkBudget.Consume(output is >= 0 and <= int.MaxValue ? (int)output : -1);
        }));
    private static bool OptimizeRegexArgument(Node argument, bool caseSensitive, bool literal, Context context)
    {
        if (!IsClass(argument, LiteralClass.String)) return false;
        var bytes = argument.EvaluatePrepared(context).Text!;
        context.WorkBudget.Consume(bytes.Length);
        if (!TryDecode(bytes, out var pattern)) return false;
        if (literal)
        {
            var escaped = new StringBuilder();
            foreach (var ch in pattern)
            {
                context.WorkBudget.Consume(1);
                if (".^$*+?{}[]\\|()".Contains(ch)) escaped.Append('\\');
                escaped.Append(ch);
            }
            context.WorkBudget.Consume(escaped.Length);
            pattern = escaped.ToString();
        }
        var compiled = LineTransformExpression.PrepareRegex(pattern, OptimizationRegexWork(context, caseSensitive), carriageReturnNewline: true);
        if (compiled is null) return false;
        argument.PreparedReplacement = new Literal(new(ValueKind.Regex, CompiledRegex: compiled));
        return true;
    }
}
