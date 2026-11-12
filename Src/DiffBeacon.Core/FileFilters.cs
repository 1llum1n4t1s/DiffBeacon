using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DiffBeacon.Core;

/// <summary>
/// WinMerge の .flt (def/f/d/f!/d!/fe/de/e) と限定したメタデータ式を評価する。
/// 式は and/or/not、比較、contains/like/matches/recontains、サイズ単位、日時、Array、At、Choose、ChooseEach、AndEach、OrEach、If、IfEach、NotEach、AnyOf、AllOf、AllEqual、Len、Strlen、Substr、StrCount、StrFind、NormalizeUnicode、ToLower、ToUpper、ToHalfWidth、ToFullWidth、ToHiragana、ToKatakana、ToSimplifiedChinese、ToTraditionalChinese、Trim、TrimLeft、TrimRight、ToInt、ToDouble、ToNumber、ToDateTime、ToDateStr、StartOfWeek、StartOfMonth、StartOfYear、Now、Today、Abs、InRange、IsWithin に対応する。
/// + - * / %、単項マイナス、Duration / Version literal と、既定有効の whole-tree Optimize（@noopt で無効）に対応する。
/// LogError / LogWarn / LogInfo は注入したログ環境へ出力し、最後の引数の型を保持する。
/// RegexCount / RegexFind / RegexExtract / RegexReplace / Replace / RegexReplaceWithList / ReplaceWithList は共通の有限時間Regex処理で浅い配列またはScalarを評価する。
/// Average / Maximum / Minimum / Sum、MatchCount / MatchNumber / MatchDistance / MatchDistanceBefore / MatchDistanceAfter、MatchContext / MatchInside、MatchBlockCount / MatchBlockNumber / MatchBlockOffset / MatchBlockSize はフォルダー文脈の provider 不在時だけに対応し、引数を実評価せず None を返す。
/// Windows の Prop / LeftProp / MiddleProp / RightProp は注入した property service で評価する。
/// Array/At/Choose/ChooseEach/AndEach/OrEach/If/IfEach/NotEach/AnyOf/AllOf/AllEqual/Len/Strlen/Substr/StrCount/StrFind/NormalizeUnicode/ToLower/ToUpper/ToHalfWidth/ToFullWidth/ToHiragana/ToKatakana/ToSimplifiedChinese/ToTraditionalChinese/Trim/TrimLeft/TrimRight/ToInt/ToDouble/ToNumber/ToDateTime/ToDateStr/StartOfWeek/StartOfMonth/StartOfYear/Now/Today/LogError/LogWarn/LogInfo/RegexCount/RegexFind/RegexExtract/RegexReplace/Replace/RegexReplaceWithList/ReplaceWithList/Abs/InRange/IsWithin/Average/Maximum/Minimum/Sum/MatchCount/MatchNumber/MatchDistance/MatchDistanceBefore/MatchDistanceAfter/MatchContext/MatchInside/MatchBlockCount/MatchBlockNumber/MatchBlockOffset/MatchBlockSize 以外の関数、未対応の左右ペイン属性、PCRE 専用構文には対応しない。
/// パス式は '/' 区切り、Extension は先頭 '.' なし、日時は UTC。正規表現は 250 ms で停止する。
/// </summary>
public sealed partial class FileFilter
{
    private const int MaxExpressionLength = 16_384;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly List<Rule> rules = [];
    private Node? expression;
    private ILineTransformStringEnvironment? stringEnvironment;
    private ILineTransformTimestampEnvironment? timestampEnvironment;
    private ILineTransformLogEnvironment? logEnvironment;
    private IFileFilterListEnvironment? listEnvironment;
    private readonly HashSet<string> propertyNames = new(StringComparer.Ordinal);
    public IReadOnlyList<string> PropertyNames => propertyNames.Order(StringComparer.Ordinal).ToArray();
    public bool UsesProperties => propertyNames.Count != 0;
    private bool RequiresFolderComparisonContext => expression?.RequiresFolderComparisonContext == true
        || rules.Any(rule => rule.Expression?.RequiresFolderComparisonContext == true);
    public string Name { get; private set; } = "Filter";
    public string Description { get; private set; } = "";
    public bool DefaultInclude { get; private set; } = true;

    private sealed record Rule(bool Directory, bool Exception, Regex? Pattern, bool FileNameOnly, Node? Expression)
    {
        public bool Matches(Context context)
        {
            if (Pattern is not null)
            {
                var subject = FileNameOnly ? context.Name : "\\" + context.Path.Replace('/', '\\');
                return Pattern.IsMatch(subject);
            }
            return EvaluateExpression(Expression!, context).ContainsTrue;
        }
    }

    public static FileFilter Load(string path, ILineTransformStringEnvironment? stringEnvironment = null, ILineTransformTimestampEnvironment? timestampEnvironment = null, ILineTransformLogEnvironment? logEnvironment = null, IFileFilterListEnvironment? listEnvironment = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var absolute = System.IO.Path.GetFullPath(path);
        if (new FileInfo(absolute).Length > 1_048_576) throw new FormatException("フィルターファイルは 1 MiB 以下にしてください。");
        var bytes = File.ReadAllBytes(absolute);
        return ParseBytes(bytes, System.IO.Path.GetFileNameWithoutExtension(absolute), stringEnvironment, timestampEnvironment, logEnvironment, listEnvironment, cancellationToken);
    }

    public static FileFilter ParseBytes(byte[] bytes, string? name = null, ILineTransformStringEnvironment? stringEnvironment = null, ILineTransformTimestampEnvironment? timestampEnvironment = null, ILineTransformLogEnvironment? logEnvironment = null, IFileFilterListEnvironment? listEnvironment = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > 1_048_576) throw new FormatException("フィルターファイルは 1 MiB 以下にしてください。");
        var (encoding, preamble) = TextDocument.DetectEncoding(bytes, "windows-1252");
        return Parse(encoding.GetString(bytes, preamble, bytes.Length - preamble), name, stringEnvironment, timestampEnvironment, logEnvironment, listEnvironment, cancellationToken);
    }

    public static FileFilter Parse(string text, string? name = null, ILineTransformStringEnvironment? stringEnvironment = null, ILineTransformTimestampEnvironment? timestampEnvironment = null, ILineTransformLogEnvironment? logEnvironment = null, IFileFilterListEnvironment? listEnvironment = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (text.Length > 1_048_576) throw new FormatException("フィルターがサイズ上限を超えています。");
        var filter = new FileFilter { Name = name ?? "Filter", stringEnvironment = stringEnvironment, timestampEnvironment = timestampEnvironment, logEnvironment = logEnvironment, listEnvironment = listEnvironment };
        var lineNumber = 0;
        foreach (var raw in TextLines.Parse(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var rawSeparator = raw.Content.IndexOf(':');
            var rawKind = rawSeparator < 0 ? ReadOnlySpan<char>.Empty : raw.Content.AsSpan(0, rawSeparator).Trim();
            var nativeDoubleQuotes = rawKind.Equals("e", StringComparison.OrdinalIgnoreCase)
                || rawKind.Equals("fe", StringComparison.OrdinalIgnoreCase) || rawKind.Equals("de", StringComparison.OrdinalIgnoreCase)
                || rawKind.Equals("e!", StringComparison.OrdinalIgnoreCase) || rawKind.Equals("fe!", StringComparison.OrdinalIgnoreCase)
                || rawKind.Equals("de!", StringComparison.OrdinalIgnoreCase);
            var line = StripComment(raw.Content, nativeDoubleQuotes).Trim();
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) throw new FormatException($"フィルター {lineNumber} 行目に ':' がありません。");
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            try
            {
                if (key == "name") { filter.Name = value; continue; }
                if (key == "desc") { filter.Description = value; continue; }
                if (key == "def")
                {
                    filter.DefaultInclude = value.ToLowerInvariant() switch
                    {
                        "include" or "yes" or "1" => true,
                        "exclude" or "no" or "0" => false,
                        _ => throw new FormatException("def は include または exclude にしてください。")
                    };
                    continue;
                }
                var exception = key.EndsWith('!');
                var category = exception ? key[..^1] : key;
                if (category is not ("f" or "d" or "fe" or "de" or "e"))
                    throw new FormatException($"未対応のフィルター種別 '{key}' です。");
                if (value.Length == 0) throw new FormatException("空のフィルター規則です。");
                if (filter.rules.Count + (category == "e" ? 2 : 1) > 512) throw new FormatException("フィルター規則は 512 個以下にしてください。");
                if (category is "f" or "d")
                {
                    var pattern = MakeRegex(value, false);
                    var fileNameOnly = category == "f" && !value.Contains("\\\\", StringComparison.Ordinal) && value.IndexOfAny([':', '/']) < 0;
                    filter.rules.Add(new(category == "d", exception, pattern, fileNameOnly, null));
                }
                else
                {
                    var root = new Parser(value, filter.propertyNames, timestampEnvironment, stringEnvironment, logEnvironment, listEnvironment, cancellationToken).Parse();
                    if (category is "fe" or "e") filter.rules.Add(new(false, exception, null, false, root));
                    if (category is "de" or "e") filter.rules.Add(new(true, exception, null, false, root));
                }
            }
            catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
            { throw new FormatException($"フィルター {lineNumber} 行目: {error.Message}", error); }
        }
        return filter;
    }

    public static FileFilter ParseExpression(string expression, ILineTransformStringEnvironment? stringEnvironment = null, ILineTransformTimestampEnvironment? timestampEnvironment = null, ILineTransformLogEnvironment? logEnvironment = null, IFileFilterListEnvironment? listEnvironment = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filter = new FileFilter { stringEnvironment = stringEnvironment, timestampEnvironment = timestampEnvironment, logEnvironment = logEnvironment, listEnvironment = listEnvironment };
        filter.expression = new Parser(StripComment(expression, true), filter.propertyNames, timestampEnvironment, stringEnvironment, logEnvironment, listEnvironment, cancellationToken).Parse();
        return filter;
    }

    public bool Matches(string relativePath, bool isDirectory, long size, DateTime modified)
        => Matches(relativePath, isDirectory, size, modified, null);

    public bool Matches(string relativePath, bool isDirectory, long size, DateTime modified,
        IReadOnlyList<IReadOnlyDictionary<string, FileFilterPropertyValue>?>? properties, CancellationToken cancellationToken = default)
    {
        if (relativePath.Length > 32_768) throw new ArgumentException("フィルターパスが長すぎます。", nameof(relativePath));
        if (RequiresFolderComparisonContext) throw new InvalidOperationException("FullPath 式にはフォルダー比較側の context が必要です。");
        var utcModified = modified.Kind == DateTimeKind.Local ? modified.ToUniversalTime() : DateTime.SpecifyKind(modified, DateTimeKind.Utc);
        if (UsesProperties && properties is null) throw new InvalidOperationException("プロパティ式には比較側の property context が必要です。");
        var context = new Context(relativePath.Replace('\\', '/').TrimStart('/'), isDirectory, size, utcModified, properties) { StringEnvironment = stringEnvironment, TimestampEnvironment = timestampEnvironment, LogEnvironment = logEnvironment, ListEnvironment = listEnvironment, CancellationToken = cancellationToken };
        if (expression is not null) return EvaluateExpression(expression, context).ContainsTrue;
        var matched = rules.Any(rule => rule.Directory == isDirectory && !rule.Exception && rule.Matches(context));
        if (matched && rules.Any(rule => rule.Directory == isDirectory && rule.Exception && rule.Matches(context))) matched = false;
        return matched ? !DefaultInclude : DefaultInclude;
    }

    internal bool[] MatchesSides(string relativePath, IReadOnlyList<DirectorySideSnapshot?> sides,
        Func<int, string, FileFilterPropertyValue>? propertyReader, IReadOnlyList<string> sideRelativePaths, CancellationToken cancellationToken = default)
    {
        if (relativePath.Length > 32_768) throw new ArgumentException("フィルターパスが長すぎます。", nameof(relativePath));
        if (UsesProperties && propertyReader is null) throw new InvalidOperationException("プロパティ式には比較側の property context が必要です。");
        var paths = sideRelativePaths.Select(path => path.Replace('\\', '/').TrimStart('/')).ToArray();
        var contexts = sides.Select((side, index) => side is null ? null : new Context(paths[index], (side.Attributes & FileAttributes.Directory) != 0,
            side.Size, side.LastWriteTimeUtc, null) { PropertyReader = propertyReader, Attributes = side.Attributes, FullPath = side.Path, StringEnvironment = stringEnvironment, TimestampEnvironment = timestampEnvironment, LogEnvironment = logEnvironment, ListEnvironment = listEnvironment, CancellationToken = cancellationToken }).ToArray();
        for (var index = 0; index < contexts.Length; index++)
            if (contexts[index] is { } context) contexts[index] = context with { ComparisonSides = contexts, ComparisonPaths = paths };
        var global = contexts.First(context => context is not null)! with
        {
            Directory = contexts.Any(context => context?.Directory == true), MetadataSides = contexts, MetadataPaths = paths
        };
        var shared = new Dictionary<Node, bool>(ReferenceEqualityComparer.Instance);
        bool Evaluate(Node node)
        {
            // フォルダー式はDIFFITEM全体を評価する。Propの有無で無接頭辞属性の型を変えない。
            // f/d のregex照合と、ruleがfile/directoryのどちらを選ぶかは各側の既存経路を保つ。
            if (!shared.TryGetValue(node, out var value)) shared.Add(node, value = EvaluateExpression(node, global).ContainsTrue);
            return value;
        }
        return contexts.Select(context =>
        {
            if (context is null) return false;
            if (expression is not null) return Evaluate(expression);
            bool Matches(Rule rule) => rule.Pattern is not null ? rule.Matches(context) : Evaluate(rule.Expression!);
            var matched = rules.Any(rule => rule.Directory == context.Directory && !rule.Exception && Matches(rule));
            if (matched && rules.Any(rule => rule.Directory == context.Directory && rule.Exception && Matches(rule))) matched = false;
            return matched ? !DefaultInclude : DefaultInclude;
        }).ToArray();
    }

    private static Regex MakeRegex(string pattern, bool caseSensitive, bool whole = false)
    {
        if (pattern.Length > 4096) throw new FormatException("正規表現は 4096 文字以下にしてください。");
        return new(whole ? "\\A(?:" + pattern + ")\\z" : pattern,
            RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), RegexTimeout);
    }

    private static string StripComment(string text, bool nativeDoubleQuotes)
    {
        var quote = '\0';
        for (var index = 0; index < text.Length; index++)
        {
            if (quote != '\0')
            {
                if ((!nativeDoubleQuotes || quote == '\'') && text[index] == '\\') index++;
                else if (text[index] == quote)
                {
                    if (nativeDoubleQuotes && quote == '"' && index + 1 < text.Length && text[index + 1] == quote) index++;
                    else quote = '\0';
                }
            }
            else if (text[index] is '"' or '\'') quote = text[index];
            else if (text[index] == '#' && index + 1 < text.Length && text[index + 1] == '#' &&
                (index == 0 || char.IsWhiteSpace(text[index - 1]))) return text[..index];
        }
        return text;
    }

    private sealed class EvaluationBudget(CancellationToken cancellationToken = default)
    {
        private int remaining = 65_536;
        public void Consume(int amount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (amount < 0 || amount > remaining)
                throw new InvalidOperationException("Choose / ChooseEach の評価作業量が上限 65,536 を超えました。");
            remaining -= amount;
        }
    }
    private sealed record Context(string Path, bool Directory, long Size, DateTime Modified,
        IReadOnlyList<IReadOnlyDictionary<string, FileFilterPropertyValue>?>? Properties)
    {
        public IReadOnlyList<Context?>? MetadataSides { get; init; }
        public IReadOnlyList<string>? MetadataPaths { get; init; }
        public IReadOnlyList<Context?>? ComparisonSides { get; init; }
        public IReadOnlyList<string>? ComparisonPaths { get; init; }
        public bool SideMetadataExpression { get; init; }
        public bool AllEqualValueExpression { get; init; }
        public bool NativeValueExpression { get; init; }
        public EvaluationBudget WorkBudget { get; init; } = new();
        public ILineTransformStringEnvironment? StringEnvironment { get; init; }
        public ILineTransformTimestampEnvironment? TimestampEnvironment { get; init; }
        public ILineTransformLogEnvironment? LogEnvironment { get; init; }
        public IFileFilterListEnvironment? ListEnvironment { get; init; }
        public CancellationToken CancellationToken { get; init; }
        public FileAttributes Attributes { get; init; }
        public string? FullPath { get; init; }
        public Func<int, string, FileFilterPropertyValue>? PropertyReader { get; init; }
        public string Name => Path[(Path.LastIndexOf('/') + 1)..];
        public string Extension => System.IO.Path.GetExtension(Name).TrimStart('.');
        public string Folder => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "";
    }
    private enum ValueKind { Null, Boolean, Number, String, Date, Dynamic, Array, Floating, Timestamp, Regex }
    // Stringの型を保ったまま、Substrが分断したUTF-8も全consumerへ無改変で渡す。
    private readonly record struct Value(ValueKind Kind, bool Boolean = false, decimal Number = 0, byte[]? Text = null,
        DateTime Date = default, IReadOnlyList<Value>? Items = null, double Floating = 0,
        LineTransformExpression.RegexPattern? CompiledRegex = null)
    {
        public static Value Bool(bool value) => new(ValueKind.Boolean, Boolean: value);
        public static Value Str(string value) => Str(Encoding.UTF8.GetBytes(value));
        public static Value Str(byte[] value) => new(ValueKind.String, Text: value);
        public bool ContainsTrue => Kind == ValueKind.Boolean && Boolean || Kind == ValueKind.Array && Items!.Any(item => item.ContainsTrue);
        public bool? AsBool => Kind == ValueKind.Boolean ? Boolean : Kind == ValueKind.Array
            ? Items!.Any(item => item.Kind == ValueKind.Boolean && item.Boolean) : null;
        public static Value FromProperty(FileFilterPropertyValue value) => value.Kind switch
        {
            FileFilterPropertyKind.Integer => new(ValueKind.Number, Number: value.Integer),
            FileFilterPropertyKind.Double => new(ValueKind.Floating, Floating: value.Double),
            FileFilterPropertyKind.String => Str(value.Text ?? ""),
            FileFilterPropertyKind.Boolean => Bool(value.Boolean),
            FileFilterPropertyKind.Timestamp => new(ValueKind.Timestamp, Number: value.Integer),
            FileFilterPropertyKind.Array => new(ValueKind.Array, Items: value.Items!.Select(FromProperty).ToArray()),
            _ => new(ValueKind.Null)
        };
    }
    private abstract record Node(ValueKind Kind)
    {
        public virtual bool UsesProperties => false;
        public virtual bool UsesSideMetadata => false;
        public virtual bool UsesAllSideMetadata => false;
        public virtual bool RequiresFolderComparisonContext => false;
        // 子の置換は元ノードに残す。上位Optimizeが通常例外で中断しても、完了済みの子は保持する。
        public Node? PreparedReplacement { get; set; }
        public Node Effective => PreparedReplacement ?? this;
        public Value EvaluatePrepared(Context context) => Effective.Evaluate(context);
        public abstract Value Evaluate(Context context);
    }
    private static Value EvaluateExpression(Node node, Context context)
    {
        // 独立したpredicateごとにresetし、子のcontext withコピーでも同じ予算を共有する。
        context = context with { WorkBudget = new(context.CancellationToken) };
        return node.EvaluatePrepared(context with { NativeValueExpression = true });
    }
    // フォルダー式には行providerがない。子ASTの構文は検査し、実行時には評価しない。
    // nativeのFunctionNode.Optimizeによる子の最適化まで省略できるという契約ではない。
    private sealed record FolderProviderUnavailable(Node[] Arguments) : Node(ValueKind.Dynamic)
    {
        public override bool UsesAllSideMetadata => true;
        public override Value Evaluate(Context context)
        {
            context.WorkBudget.Consume(1);
            return new(ValueKind.Null);
        }
    }
    // 行providerのないフォルダー専用。At系は子を評価せず、Column系だけ各側で再評価する。
    private sealed record FolderSideValue(string Function, Node[] Arguments, int? Side) : Node(ValueKind.Dynamic)
    {
        public bool CachedLiteralRange { get; set; }
        private bool EvaluatesArguments => Function is "column" or "columnrange";
        public override bool UsesProperties => EvaluatesArguments && Arguments.Any(argument => argument.UsesProperties);
        public override bool UsesSideMetadata => EvaluatesArguments && Arguments.Any(argument => argument.UsesSideMetadata);
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => true;
        public void PrepareLiteralRange(Context context)
        {
            if (Function != "columnrange" || Arguments[0].Effective is not Literal { Value.Kind: ValueKind.String } literal) return;
            ValidateFolderColumnRanges(literal.Value.Text!, context.WorkBudget);
            CachedLiteralRange = true;
        }
        public override Value Evaluate(Context context)
        {
            context.WorkBudget.Consume(1);
            var sides = context.ComparisonSides ?? context.MetadataSides
                ?? throw new InvalidOperationException("行・列のフォルダー式には比較側の metadata context が必要です。");
            Value Read(int side)
            {
                context.WorkBudget.Consume(1);
                // providerの有無を引数の型検査より先に判定する原典At系の順序を保つ。
                if (!EvaluatesArguments) return new(ValueKind.Null);
                if (CachedLiteralRange) return Value.Str("");
                var argument = Arguments[0].EvaluatePrepared(context);
                if (Function == "column")
                {
                    if (argument.Kind == ValueKind.String || argument.Kind == ValueKind.Number
                        && argument.Number >= 1 && argument.Number <= int.MaxValue
                        && decimal.Truncate(argument.Number) == argument.Number)
                        return new(ValueKind.Null);
                    throw new ArgumentException("invalid column specification");
                }
                if (argument.Kind != ValueKind.String) throw new ArgumentException("ColumnRange requires a string argument");
                ValidateFolderColumnRanges(argument.Text!, context.WorkBudget);
                return Value.Str("");
            }
            return Side is null
                ? new(ValueKind.Array, Items: Enumerable.Range(0, sides.Count).Select(Read).ToArray())
                : Read(Side == -1 ? sides.Count - 1 : Side.Value);
        }
    }
    // std::isspace/isdigitの原典文法をUTF-8 byte上で検査する。列の選択自体はprovider不在で空文字。
    private static void ValidateFolderColumnRanges(byte[] text, EvaluationBudget budget)
    {
        budget.Consume(text.Length);
        var position = 0;
        void SkipSpace()
        {
            while (position < text.Length && text[position] is 32 or 9 or 10 or 11 or 12 or 13) position++;
        }
        int Number()
        {
            var begin = position;
            var value = 0;
            while (position < text.Length && text[position] is >= (byte)'0' and <= (byte)'9')
            {
                var digit = text[position++] - (byte)'0';
                if (value > (int.MaxValue - digit) / 10) throw new ArgumentException("invalid column specification");
                value = value * 10 + digit;
            }
            if (position == begin || value < 1) throw new ArgumentException("invalid column specification");
            return value;
        }
        while (true)
        {
            SkipSpace();
            if (position == text.Length) throw new ArgumentException("invalid column specification");
            if (text[position] == (byte)'!') { position++; SkipSpace(); }
            var start = Number();
            SkipSpace();
            if (position < text.Length && text[position] == (byte)'-')
            {
                position++; SkipSpace();
                if (position < text.Length && text[position] != (byte)',' && Number() < start)
                    throw new ArgumentException("invalid column specification");
            }
            SkipSpace();
            if (position == text.Length) return;
            if (text[position++] != (byte)',') throw new ArgumentException("invalid column specification");
        }
    }
    private sealed record Literal(Value Value) : Node(Value.Kind) { public override Value Evaluate(Context context) => Value; }
    // 他の日時関数と同じく、Date metadataとの比較は実行時のTimestamp規則へ渡す。
    private sealed record ClockValue(Value Value) : Node(ValueKind.Dynamic)
    {
        public override bool UsesAllSideMetadata => true;
        public override Value Evaluate(Context context) => Value;
    }
    private sealed record LogValue(Node[] Arguments, LineTransformLogLevel Level, string ExpressionName) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Arguments.Any(argument => argument.UsesProperties);
        public override bool UsesSideMetadata => Arguments.Any(argument => argument.UsesSideMetadata);
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Arguments.Any(argument => argument.RequiresFolderComparisonContext);
        public override Value Evaluate(Context context)
        {
            // 原典と同じ順序で評価・文字列化し、全引数の完了後だけ一度公開する。
            using var message = new MemoryStream();
            if (ExpressionName.Length != 0)
            {
                var prefix = Encoding.UTF8.GetBytes("[" + ExpressionName + "] ");
                context.WorkBudget.Consume(prefix.Length); message.Write(prefix);
            }
            Value result = new(ValueKind.Null);
            for (var index = 0; index < Arguments.Length; index++)
            {
                result = Arguments[index].EvaluatePrepared(context);
                var bytes = NativeString(result, context.WorkBudget, context.TimestampEnvironment);
                if (index != 0) { context.WorkBudget.Consume(1); message.WriteByte((byte)' '); }
                context.WorkBudget.Consume(bytes.Length); message.Write(bytes);
            }
            var text = NativeUtf16(message.ToArray(), context.WorkBudget);
            context.LogEnvironment?.Write(Level, text);
            return result;
        }
    }
    private sealed record RegexCount(Node Source, Node Pattern, bool CaseSensitive) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Source.UsesProperties || Pattern.UsesProperties;
        public override bool UsesSideMetadata => Source.UsesSideMetadata || Pattern.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Source.RequiresFolderComparisonContext || Pattern.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            // 型判定より前にsource→patternを一度ずつ評価する。
            var source = Source.EvaluatePrepared(context); var patternValue = Pattern.EvaluatePrepared(context);
            if (patternValue.Kind is not (ValueKind.String or ValueKind.Regex)) return new(ValueKind.Null);
            var work = new LineTransformExpression.RegexWorkContext(CaseSensitive, context.CancellationToken,
                new((amount, token, output) =>
                {
                    token.ThrowIfCancellationRequested();
                    // 共通処理のworkと生成量を、同じフォルダーpredicateの予算へ戻す。
                    context.WorkBudget.Consume(amount is >= 0 and <= int.MaxValue ? (int)amount : -1);
                    context.WorkBudget.Consume(output is >= 0 and <= int.MaxValue ? (int)output : -1);
                }));
            LineTransformExpression.RegexPattern? prepared = null;
            var attempted = false;
            Value Map(Value value)
            {
                context.WorkBudget.Consume(1);
                if (value.Kind == ValueKind.Null) return new(ValueKind.Null);
                var bytes = NativeString(value, context.WorkBudget, context.TimestampEnvironment);
                context.WorkBudget.Consume(bytes.Length);
                if (!attempted)
                {
                    attempted = true;
                    if (patternValue.Kind == ValueKind.Regex) prepared = patternValue.CompiledRegex;
                    else
                    {
                        var patternBytes = patternValue.Text!; context.WorkBudget.Consume(patternBytes.Length);
                        // 不正patternのUTF-8は原典createRegexと同じNone。sourceの失敗はここへ混ぜない。
                        if (TryDecode(patternBytes, out var pattern)) prepared = LineTransformExpression.PrepareRegex(pattern, work, carriageReturnNewline: true);
                    }
                }
                if (prepared is null) return new(ValueKind.Null);
                // 原典はStringのbyteを保持し、pattern準備に成功してから入力を照合する。
                string text;
                try { text = StrictUtf8.GetString(bytes); }
                catch (DecoderFallbackException error) { throw new ArgumentException("RegexCount の入力が不正なUTF-8です。", error); }
                try
                {
                    var count = (long)LineTransformExpression.MatchRegex(text, prepared, "regexcount", 0, 0, work)!;
                    return new(ValueKind.Number, Number: count);
                }
                catch (RegexMatchTimeoutException error)
                {
                    throw new InvalidOperationException("RegexCount の正規表現評価がタイムアウトしました（"
                        + error.MatchTimeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms）。", error);
                }
                catch (NotSupportedException error) { throw new InvalidOperationException(error.Message, error); }
            }
            if (source.Kind != ValueKind.Array) return Map(source);
            context.WorkBudget.Consume(source.Items!.Count);
            return new(ValueKind.Array, Items: source.Items.Select(Map).ToArray());
        }
    }
    private sealed record RegexPosition(string FunctionName, Node Source, Node Pattern, Node? Group, Node? Occurrence, bool CaseSensitive) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Source.UsesProperties || Pattern.UsesProperties || Group?.UsesProperties == true || Occurrence?.UsesProperties == true;
        public override bool UsesSideMetadata => Source.UsesSideMetadata || Pattern.UsesSideMetadata || Group?.UsesSideMetadata == true || Occurrence?.UsesSideMetadata == true;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Source.RequiresFolderComparisonContext || Pattern.RequiresFolderComparisonContext
            || Group?.RequiresFolderComparisonContext == true || Occurrence?.RequiresFolderComparisonContext == true;
        public override Value Evaluate(Context context)
        {
            // groupの型不一致ではoccurrenceを評価せず、patternの型判定は両guardの後に行う。
            var source = Source.EvaluatePrepared(context); var patternValue = Pattern.EvaluatePrepared(context);
            long group = 0, occurrence = 0;
            if (Group is not null)
            {
                var value = Group.EvaluatePrepared(context);
                if (!Integer(value)) return new(ValueKind.Null);
                group = (long)value.Number;
            }
            if (Occurrence is not null)
            {
                var value = Occurrence.EvaluatePrepared(context);
                if (!Integer(value)) return new(ValueKind.Null);
                occurrence = (long)value.Number;
            }
            if (patternValue.Kind is not (ValueKind.String or ValueKind.Regex)) return new(ValueKind.Null);
            var work = new LineTransformExpression.RegexWorkContext(CaseSensitive, context.CancellationToken,
                new((amount, token, output) =>
                {
                    token.ThrowIfCancellationRequested();
                    context.WorkBudget.Consume(amount is >= 0 and <= int.MaxValue ? (int)amount : -1);
                    context.WorkBudget.Consume(output is >= 0 and <= int.MaxValue ? (int)output : -1);
                }));
            LineTransformExpression.RegexPattern? prepared = null;
            var attempted = false;
            var nativeFunction = FunctionName == "RegexFind" ? "regexfind" : "regexextract";
            Value Map(Value value)
            {
                context.WorkBudget.Consume(1);
                if (value.Kind == ValueKind.Null) return new(ValueKind.Null);
                var bytes = NativeString(value, context.WorkBudget, context.TimestampEnvironment);
                context.WorkBudget.Consume(bytes.Length);
                if (!attempted)
                {
                    attempted = true;
                    if (patternValue.Kind == ValueKind.Regex) prepared = patternValue.CompiledRegex;
                    else
                    {
                        var patternBytes = patternValue.Text!; context.WorkBudget.Consume(patternBytes.Length);
                        if (TryDecode(patternBytes, out var pattern)) prepared = LineTransformExpression.PrepareRegex(pattern, work, carriageReturnNewline: true);
                    }
                }
                if (prepared is null) return new(ValueKind.Null);
                // 原典はgetAsString→createRegexの後、負occurrenceでは探索しない。Stringの不正byteも復号しない。
                if (occurrence < 0) return new(ValueKind.Null);
                string text;
                try { text = StrictUtf8.GetString(bytes); }
                catch (DecoderFallbackException error) { throw new ArgumentException(FunctionName + " の入力が不正なUTF-8です。", error); }
                try
                {
                    var result = LineTransformExpression.MatchRegex(text, prepared, nativeFunction, group, occurrence, work);
                    if (result is null) return new(ValueKind.Null);
                    // 捕捉のworkと生成量は共通探索で課金済み。Stringは正確なUTF-8 byte値へ戻す。
                    return FunctionName == "RegexFind" ? new(ValueKind.Number, Number: (long)result) : Value.Str(StrictUtf8.GetBytes((string)result));
                }
                catch (RegexMatchTimeoutException error)
                {
                    throw new InvalidOperationException(FunctionName + " の正規表現評価がタイムアウトしました（"
                        + error.MatchTimeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms）。", error);
                }
                catch (NotSupportedException error) { throw new InvalidOperationException(error.Message, error); }
            }
            if (source.Kind != ValueKind.Array) return Map(source);
            context.WorkBudget.Consume(source.Items!.Count);
            return new(ValueKind.Array, Items: source.Items.Select(Map).ToArray());
        }
        private static bool Integer(Value value) => value.Kind == ValueKind.Number && value.Number >= long.MinValue && value.Number <= long.MaxValue && decimal.Truncate(value.Number) == value.Number;
    }
    private sealed record RegexReplace(Node Source, Node Pattern, Node Replacement, bool CaseSensitive, bool Literal = false) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Source.UsesProperties || Pattern.UsesProperties || Replacement.UsesProperties;
        public override bool UsesSideMetadata => Source.UsesSideMetadata || Pattern.UsesSideMetadata || Replacement.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Source.RequiresFolderComparisonContext || Pattern.RequiresFolderComparisonContext || Replacement.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            // sourceがNoneでも、型検査より前に3引数を左から一度ずつ評価する。
            var source = Source.EvaluatePrepared(context); var patternValue = Pattern.EvaluatePrepared(context); var replacement = Replacement.EvaluatePrepared(context);
            if ((patternValue.Kind is not (ValueKind.String or ValueKind.Regex)) || replacement.Kind != ValueKind.String) return new(ValueKind.Null);
            var work = new LineTransformExpression.RegexWorkContext(CaseSensitive, context.CancellationToken,
                new((amount, token, output) =>
                {
                    token.ThrowIfCancellationRequested();
                    context.WorkBudget.Consume(amount is >= 0 and <= int.MaxValue ? (int)amount : -1);
                    context.WorkBudget.Consume(output is >= 0 and <= int.MaxValue ? (int)output : -1);
                }));
            LineTransformExpression.RegexPattern? prepared = null;
            var attempted = false;
            Value Map(Value value)
            {
                context.WorkBudget.Consume(1);
                if (value.Kind == ValueKind.Null) return new(ValueKind.Null);
                var bytes = NativeString(value, context.WorkBudget, context.TimestampEnvironment);
                context.WorkBudget.Consume(bytes.Length);
                if (!attempted)
                {
                    attempted = true;
                    if (patternValue.Kind == ValueKind.Regex) prepared = patternValue.CompiledRegex;
                    else
                    {
                        var patternBytes = patternValue.Text!; context.WorkBudget.Consume(patternBytes.Length);
                        if (TryDecode(patternBytes, out var pattern) && (!Literal || pattern.Length != 0))
                        {
                            if (Literal)
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
                            prepared = LineTransformExpression.PrepareRegex(pattern, work, carriageReturnNewline: true);
                        }
                    }
                }
                // regexSubstの不正pattern/subjectはNone。replacementは実match後にだけ解釈する。
                if (prepared is null || !TryDecode(bytes, out var text)) return new(ValueKind.Null);
                try
                {
                    var result = LineTransformExpression.ReplaceRegexUtf8(text, replacement.Text!, prepared, work);
                    return result is null ? new(ValueKind.Null) : Value.Str(result);
                }
                catch (RegexMatchTimeoutException error)
                {
                    throw new InvalidOperationException((Literal ? "Replace" : "RegexReplace") + " の正規表現評価がタイムアウトしました（"
                        + error.MatchTimeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms）。", error);
                }
                catch (NotSupportedException error) { throw new InvalidOperationException(error.Message, error); }
            }
            if (source.Kind != ValueKind.Array) return Map(source);
            context.WorkBudget.Consume(source.Items!.Count);
            return new(ValueKind.Array, Items: source.Items.Select(Map).ToArray());
        }
    }
    private sealed record ReplaceList(Node Source, Node List, bool CaseSensitive, bool Literal) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Source.UsesProperties || List.UsesProperties;
        public override bool UsesSideMetadata => Source.UsesSideMetadata || List.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Source.RequiresFolderComparisonContext || List.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            // 型検査・ファイル読込みの前に、左から一度ずつ評価する。
            var source = Source.EvaluatePrepared(context); var list = List.EvaluatePrepared(context);
            var work = new LineTransformExpression.RegexWorkContext(CaseSensitive, context.CancellationToken,
                new((amount, token, output) =>
                {
                    token.ThrowIfCancellationRequested();
                    context.WorkBudget.Consume(amount is >= 0 and <= int.MaxValue ? (int)amount : -1);
                    context.WorkBudget.Consume(output is >= 0 and <= int.MaxValue ? (int)output : -1);
                }));
            if (list.Kind == ValueKind.String) list = LoadList(list.Text!, context, work);
            if (list.Kind != ValueKind.Array) return new(ValueKind.Null);
            Value Map(Value value)
            {
                context.WorkBudget.Consume(1);
                if (value.Kind == ValueKind.Null) return value;
                var bytes = NativeString(value, context.WorkBudget, context.TimestampEnvironment);
                foreach (var rule in list.Items!)
                {
                    context.WorkBudget.Consume(1);
                    if (rule.Kind != ValueKind.Array || rule.Items!.Count != 2 || rule.Items[1].Kind != ValueKind.String) continue;
                    var patternValue = rule.Items[0]; var pattern = patternValue.CompiledRegex;
                    if (!Literal && patternValue.Kind == ValueKind.String)
                    {
                        context.WorkBudget.Consume(patternValue.Text!.Length);
                        if (TryDecode(patternValue.Text!, out var expression)) pattern = LineTransformExpression.PrepareRegex(expression, work, carriageReturnNewline: true);
                    }
                    // literalは無効pairを無視するが、実行するcompiled ruleの入力不正は原典の直接subst同様に失敗させる。
                    if (pattern is null) continue;
                    if (!TryDecode(bytes, out var text))
                    {
                        if (Literal) throw new ArgumentException("ReplaceWithList のsourceは有効なUTF-8にしてください。");
                        continue;
                    }
                    try
                    {
                        var result = LineTransformExpression.ReplaceRegexUtf8(text, rule.Items[1].Text!, pattern, work, preservePartialOnFailure: !Literal);
                        if (Literal && result is null) throw new ArgumentException("ReplaceWithList の空幅patternが不正なUTF-8位置へ進みました。");
                        bytes = result ?? bytes;
                    }
                    catch (RegexMatchTimeoutException error) { throw new InvalidOperationException((Literal ? "ReplaceWithList" : "RegexReplaceWithList") + " の正規表現評価がタイムアウトしました。", error); }
                    catch (NotSupportedException error) { throw new InvalidOperationException(error.Message, error); }
                }
                return Value.Str(bytes);
            }
            if (source.Kind != ValueKind.Array) return Map(source);
            context.WorkBudget.Consume(source.Items!.Count);
            return new(ValueKind.Array, Items: source.Items.Select(Map).ToArray());
        }
        public Value PrepareList(Context context)
        {
            var value = List.EvaluatePrepared(context);
            return value.Kind == ValueKind.String ? LoadList(value.Text!, context, OptimizationRegexWork(context, CaseSensitive)) : value;
        }
        private Value LoadList(byte[] pathBytes, Context context, LineTransformExpression.RegexWorkContext work)
        {
            context.WorkBudget.Consume(pathBytes.Length);
            if (!TryDecode(pathBytes, out var path)) throw new ArgumentException("置換リストのパスは有効なUTF-8にしてください。");
            if (context.ListEnvironment is null) throw new ArgumentException("置換リストには明示的なホスト読込み環境が必要です。");
            var text = context.ListEnvironment.ReadList(path, new(context.WorkBudget.Consume, context.CancellationToken));
            var pairs = new List<Value>(); var lineNumber = 0;
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } line)
            {
                context.WorkBudget.Consume(line.Length + 1); lineNumber++;
                var tab = line.IndexOf('\t');
                if (line.Length == 0 || line[0] == '#' || tab < 0) continue;
                var replacement = line[(tab + 1)..]; var second = replacement.IndexOf('\t');
                if (second >= 0) replacement = replacement[..second];
                var expression = line[..tab];
                // LoadListと同じmetacharacter集合だけをescapeし、置換自体はPoco互換backendへ渡す。
                if (Literal)
                {
                    var escaped = new StringBuilder();
                    foreach (var ch in expression)
                    {
                        context.WorkBudget.Consume(1);
                        if (".^$*+?{}[]\\|()".Contains(ch)) escaped.Append('\\');
                        escaped.Append(ch);
                    }
                    context.WorkBudget.Consume(escaped.Length);
                    expression = escaped.ToString();
                }
                var pattern = LineTransformExpression.PrepareRegex(expression, work, carriageReturnNewline: true);
                if (pattern is null)
                {
                    context.LogEnvironment?.Write(LineTransformLogLevel.Error, (Literal ? "Invalid pattern at line " : "Invalid regular expression at line ") + lineNumber.ToString(CultureInfo.InvariantCulture)
                        + " in file: " + path + ", pattern: " + expression);
                    continue;
                }
                context.WorkBudget.Consume(3 + replacement.Length);
                pairs.Add(new(ValueKind.Array, Items: [new(ValueKind.Regex, CompiledRegex: pattern), Value.Str(replacement)]));
            }
            return new(ValueKind.Array, Items: pairs);
        }
    }
    private sealed record NumberLiteral(double Floating) : Node(ValueKind.Number)
    {
        public override Value Evaluate(Context context) => new(ValueKind.Floating, Floating: Floating);
    }
    private sealed record SizeLiteral(decimal ExtensionValue, long? NativeBytes) : Node(ValueKind.Number)
    {
        // B/KB/MB/GB/TBは原典のInt64。従来のKiB等の構文拡張はその既存値を保持する。
        public override Value Evaluate(Context context) => new(ValueKind.Number, Number: NativeBytes ?? ExtensionValue);
    }
    private sealed record ShellProperty(string Name, int Side) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => true;
        public override Value Evaluate(Context context)
        {
            var count = context.MetadataSides?.Count ?? context.Properties!.Count;
            Value Read(int side)
            {
                if (side < 0 || side >= count) return new(ValueKind.Null);
                if (context.PropertyReader is { } reader) return Value.FromProperty(reader(side, Name));
                return context.Properties![side] is { } values && values.TryGetValue(Name, out var value) ? Value.FromProperty(value) : new(ValueKind.Null);
            }
            return Side == -2 ? count == 1 ? Read(0) : new(ValueKind.Array, Items: Enumerable.Range(0, count).Select(Read).ToArray())
                : Read(Side == -1 ? count - 1 : Side);
        }
    }
    private sealed record Property(string Name, ValueKind Type, int? Side = null) : Node(Type)
    {
        public override bool UsesSideMetadata => Side is not null;
        public override bool UsesAllSideMetadata => Name == "fullpath";
        public override bool RequiresFolderComparisonContext => Name == "fullpath";
        public override Value Evaluate(Context context)
        {
            if (Side is { } requested)
            {
                var available = context.ComparisonSides ?? context.MetadataSides
                    ?? throw new InvalidOperationException("接頭辞付き属性には比較側の metadata context が必要です。");
                var index = requested == -1 ? available.Count - 1 : requested;
                if (index < 0 || index >= available.Count) return new(ValueKind.Null);
                var selected = available[index];
                return Name switch
                {
                    "exists" or "fileexists" => Value.Bool(selected is not null),
                    "missing" or "filemissing" => Value.Bool(selected is null),
                    "isfolder" => Value.Bool(context.Directory),
                    "folder" => Value.Str(selected?.Folder ?? FolderOf((context.ComparisonPaths ?? context.MetadataPaths)![index])),
                    _ => selected is null ? new(ValueKind.Null) : Scalar(selected)
                };
            }
            if (context.MetadataSides is { } sides)
            {
                Value At(Context? side, int index) => Name switch
                {
                    "exists" or "fileexists" => Value.Bool(side is not null),
                    "missing" or "filemissing" => Value.Bool(side is null),
                    "isdirectory" or "isfolder" => Value.Bool(context.Directory),
                    "folder" => Value.Str(side?.Folder ?? FolderOf(context.MetadataPaths![index])),
                    _ => side is null ? new(ValueKind.Null) : Scalar(side)
                };
                return sides.Count == 1 ? At(sides[0], 0) : new(ValueKind.Array, Items: sides.Select(At).ToArray());
            }
            return Scalar(context);
        }
        private static string FolderOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        private Value Scalar(Context context) => Name switch
        {
            "path" or "relpath" => Value.Str(context.Path),
            "name" => Value.Str(context.Name),
            "basename" => Value.Str(System.IO.Path.GetFileNameWithoutExtension(context.Name)),
            "extension" => Value.Str(context.Extension),
            "folder" => Value.Str(context.Folder),
            "fullpath" => context.FullPath is { } fullPath ? Value.Str(fullPath) : new(ValueKind.Null),
            "size" => new(ValueKind.Number, Number: context.Size),
            "attributes" => new(ValueKind.Number, Number: (long)context.Attributes),
            "attrstr" => Value.Str(AttributeString(context.Attributes)),
            "modified" or "date" => new(ValueKind.Timestamp, Number: FileTimestamp(context.Modified)),
            // 原典DateStrはmtime当日のDSTではなく、現在のtzdをUTC値へ固定適用する。
            "datestr" => Value.Str(CoreDateStr(context)),
            "isdirectory" or "isfolder" => Value.Bool(context.Directory),
            "exists" or "fileexists" => Value.Bool(true),
            "missing" or "filemissing" => Value.Bool(false),
            _ => throw new InvalidOperationException("未対応の属性です。")
        };
    }
    private static string CoreDateStr(Context context)
    {
        // fake Matchesも符号付きUTC epochを保ち、Date属性のFILETIME変換とは分離する。
        long epoch = (Microseconds(context.Modified).Ticks - DateTime.UnixEpoch.Ticks) / 10;
        int offset = context.TimestampEnvironment?.CurrentTzdSeconds
            ?? (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalSeconds;
        return LineTransformTimestampParser.FormatLocalDate(new(epoch), new DateStrFixedOffsetEnvironment(offset));
    }
    private sealed class DateStrFixedOffsetEnvironment(int offset) : ITimestampParserEnvironment
    {
        public string ProfileId => "folder DateStr fixed current offset";
        public int CurrentTzdSeconds => offset;
        public bool TryParseWindowsLocale(string text, out TimestampEpochMicroseconds value)
        { value = default; return false; }
        public bool TryGetSerializationOffset(TimestampEpochMicroseconds value, out int seconds, out string failureText)
        { seconds = offset; failureText = ""; return true; }
    }
    private static DateTime Microseconds(DateTime value) => new(value.Ticks - value.Ticks % 10, value.Kind);
    // FILETIME の unsigned epoch減算後に /10 する Poco::Timestamp::fromFileTimeNP と同じ順序。
    private static long FileTimestamp(DateTime value) => (long)(unchecked((ulong)(value.Ticks - DateTime.UnixEpoch.Ticks)) / 10);
    private static string AttributeString(FileAttributes attributes)
        => ((attributes & FileAttributes.ReadOnly) != 0 ? "R" : "")
            + ((attributes & FileAttributes.Hidden) != 0 ? "H" : "")
            + ((attributes & FileAttributes.System) != 0 ? "S" : "")
            + ((attributes & FileAttributes.Archive) != 0 ? "A" : "");
    private sealed record Len(Node Argument) : Node(ValueKind.Number)
    {
        public override bool UsesProperties => Argument.UsesProperties;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var value = Argument.EvaluatePrepared(context);
            if (value.Kind == ValueKind.Array) return new(ValueKind.Number, Number: value.Items!.Count);
            if (value.Kind == ValueKind.Null) return new(ValueKind.Number, Number: 0);
            if (value.Kind != ValueKind.String) return new(ValueKind.Number, Number: 1);
            return new(ValueKind.Number, Number: NativeLength(value.Text!, context.WorkBudget));
        }
    }
    private sealed record ChangeCase(Node Argument, bool Lower) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Argument.UsesProperties;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var value = Argument.EvaluatePrepared(context);
            Value Map(Value item)
            {
                if (item.Kind == ValueKind.Null) return item;
                var bytes = NativeString(item, context.WorkBudget);
                var input = NativeUtf16(bytes, context.WorkBudget);
                var mapped = context.StringEnvironment is { } environment
                    ? Lower ? environment.ToLower(input) : environment.ToUpper(input)
                    : Lower ? CultureInfo.CurrentCulture.TextInfo.ToLower(input) : CultureInfo.CurrentCulture.TextInfo.ToUpper(input);
                if (mapped is null) throw new ArgumentException("大小文字変換環境が文字列を返しませんでした。");
                context.WorkBudget.Consume(mapped.Length);
                var output = Encoding.UTF8.GetBytes(mapped);
                context.WorkBudget.Consume(output.Length);
                return Value.Str(output);
            }
            // 直下だけをmapし、入れ子arrayはNativeStringで一度文字列化する。
            if (value.Kind != ValueKind.Array) return Map(value);
            context.WorkBudget.Consume(value.Items!.Count);
            return new(ValueKind.Array, Items: value.Items.Select(Map).ToArray());
        }
    }
    private sealed record CharacterMapping(Node Argument, LineTransformCharacterMapping Mapping) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Argument.UsesProperties;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var value = Argument.EvaluatePrepared(context);
            Value Map(Value item)
            {
                if (item.Kind == ValueKind.Null) return item;
                var input = NativeUtf16(NativeString(item, context.WorkBudget), context.WorkBudget);
                var environment = context.StringEnvironment ?? throw new ArgumentException("文字種変換には明示的な環境が必要です。");
                var mapped = environment.MapCharacters(input, Mapping);
                if (mapped is null) throw new ArgumentException("文字種変換環境が文字列を返しませんでした。");
                context.WorkBudget.Consume(mapped.Length);
                var output = Encoding.UTF8.GetBytes(mapped);
                context.WorkBudget.Consume(output.Length);
                return Value.Str(output);
            }
            if (value.Kind != ValueKind.Array) return Map(value);
            context.WorkBudget.Consume(value.Items!.Count);
            return new(ValueKind.Array, Items: value.Items.Select(Map).ToArray());
        }
    }
    private sealed record ToDateTime(Node Argument) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Argument.UsesProperties;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var value = Argument.EvaluatePrepared(context);
            Value Map(Value item)
            {
                if (item.Kind != ValueKind.String || item.Text!.Length == 0) return new(ValueKind.Null);
                var input = NativeUtf16(item.Text, context.WorkBudget);
                // 全形式の走査もフォルダー式の共有予算へ収める。
                context.WorkBudget.Consume(checked(input.Length * 16 + 1));
                var environment = context.TimestampEnvironment ?? throw new ArgumentException("日時関数には明示的な環境が必要です。");
                var result = LineTransformTimestampParser.Construct(input, new LineTransformExpression.TimestampEnvironmentAdapter(environment));
                return result.Success ? new(ValueKind.Timestamp, Number: result.Value.Value) : new(ValueKind.Null);
            }
            if (value.Kind != ValueKind.Array) return Map(value);
            context.WorkBudget.Consume(value.Items!.Count);
            return new(ValueKind.Array, Items: value.Items.Select(Map).ToArray());
        }
    }
    private sealed record ToDateStr(Node Argument) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Argument.UsesProperties;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var value = Argument.EvaluatePrepared(context);
            Value Map(Value item)
            {
                if (item.Kind is not (ValueKind.String or ValueKind.Date or ValueKind.Timestamp)) return new(ValueKind.Null);
                var environment = context.TimestampEnvironment ?? throw new ArgumentException("日時関数には明示的な環境が必要です。");
                var adapter = new LineTransformExpression.TimestampEnvironmentAdapter(environment);
                long epoch;
                if (item.Kind == ValueKind.String)
                {
                    var input = NativeUtf16(item.Text!, context.WorkBudget);
                    context.WorkBudget.Consume(checked(input.Length * 16 + 1));
                    var parsed = LineTransformTimestampParser.Construct(input, adapter);
                    if (!parsed.Success) throw new ArgumentException(parsed.Error);
                    epoch = parsed.Value.Value;
                }
                else epoch = item.Kind == ValueKind.Date ? (item.Date.Ticks - DateTime.UnixEpoch.Ticks) / 10 : decimal.ToInt64(item.Number);
                context.WorkBudget.Consume(32);
                var date = LineTransformTimestampParser.FormatLocalDate(new(epoch), adapter);
                context.WorkBudget.Consume(date.Length);
                var bytes = Encoding.UTF8.GetBytes(date);
                context.WorkBudget.Consume(bytes.Length);
                return Value.Str(bytes);
            }
            if (value.Kind != ValueKind.Array) return Map(value);
            context.WorkBudget.Consume(value.Items!.Count);
            return new(ValueKind.Array, Items: value.Items.Select(Map).ToArray());
        }
    }
    private sealed record StartOf(Node Argument, string Function) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Argument.UsesProperties;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var value = Argument.EvaluatePrepared(context);
            Value Map(Value item)
            {
                if (item.Kind is not (ValueKind.String or ValueKind.Date or ValueKind.Timestamp)) return new(ValueKind.Null);
                var environment = context.TimestampEnvironment ?? throw new ArgumentException("日時関数には明示的な環境が必要です。");
                var adapter = new LineTransformExpression.TimestampEnvironmentAdapter(environment);
                long epoch;
                if (item.Kind == ValueKind.String)
                {
                    var input = NativeUtf16(item.Text!, context.WorkBudget);
                    context.WorkBudget.Consume(checked(input.Length * 16 + 1));
                    var parsed = LineTransformTimestampParser.Construct(input, adapter);
                    if (!parsed.Success) throw new ArgumentException(parsed.Error);
                    epoch = parsed.Value.Value;
                }
                else epoch = item.Kind == ValueKind.Date ? (item.Date.Ticks - DateTime.UnixEpoch.Ticks) / 10 : decimal.ToInt64(item.Number);
                context.WorkBudget.Consume(64);
                // 週だけのUTC時間軸減算・再構築と、月/年の負epoch返値の差を共通原典経路で保つ。
                var result = LineTransformTimestampParser.StartOf(new(epoch), Function, adapter);
                return new(ValueKind.Timestamp, Number: result.Value);
            }
            if (value.Kind != ValueKind.Array) return Map(value);
            context.WorkBudget.Consume(value.Items!.Count);
            return new(ValueKind.Array, Items: value.Items.Select(Map).ToArray());
        }
    }
    private sealed record NormalizeUnicode(Node Argument, Node? Form) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Argument.UsesProperties || Form?.UsesProperties == true;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata || Form?.UsesSideMetadata == true;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext || Form?.RequiresFolderComparisonContext == true;
        public override Value Evaluate(Context context)
        {
            // 原典はformを先に一度読み、不正string/Int64ではvalueを評価しない。
            var form = NormalizationForm.FormC;
            if (Form is not null)
            {
                var requested = Form.EvaluatePrepared(context);
                if (requested.Kind == ValueKind.String)
                {
                    var bytes = requested.Text!;
                    context.WorkBudget.Consume(bytes.Length);
                    if (bytes.Length is < 1 or > 4 || bytes.Any(value => value > 0x7f)) return new(ValueKind.Null);
                    var resolved = Encoding.ASCII.GetString(bytes).ToUpperInvariant() switch
                    {
                        "NFC" or "C" => NormalizationForm.FormC,
                        "NFD" or "D" => NormalizationForm.FormD,
                        "NFKC" or "KC" => NormalizationForm.FormKC,
                        "NFKD" or "KD" => NormalizationForm.FormKD,
                        _ => (NormalizationForm)0
                    };
                    if (resolved == 0) return new(ValueKind.Null);
                    form = resolved;
                }
                else if (requested.Kind == ValueKind.Number)
                {
                    form = requested.Number switch
                    {
                        1 => NormalizationForm.FormC, 2 => NormalizationForm.FormD,
                        5 => NormalizationForm.FormKC, 6 => NormalizationForm.FormKD,
                        _ => (NormalizationForm)0
                    };
                    if (form == 0) return new(ValueKind.Null);
                }
                // double/bool/None/array等のwrong-typeは整数aliasへ変換せずNFC既定。
            }
            var value = Argument.EvaluatePrepared(context);
            Value Map(Value item)
            {
                if (item.Kind == ValueKind.Null) return item;
                var input = NativeUtf16(NativeString(item, context.WorkBudget), context.WorkBudget);
                var mapped = context.StringEnvironment is { } environment ? environment.NormalizeUnicode(input, form) : input.Normalize(form);
                if (mapped is null) throw new ArgumentException("Unicode正規化環境が文字列を返しませんでした。");
                context.WorkBudget.Consume(mapped.Length);
                var output = Encoding.UTF8.GetBytes(mapped);
                context.WorkBudget.Consume(output.Length);
                return Value.Str(output);
            }
            if (value.Kind != ValueKind.Array) return Map(value);
            context.WorkBudget.Consume(value.Items!.Count);
            return new(ValueKind.Array, Items: value.Items.Select(Map).ToArray());
        }
    }
    private sealed record Strlen(Node Argument) : Node(ValueKind.Number)
    {
        public override bool UsesProperties => Argument.UsesProperties;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var value = Argument.EvaluatePrepared(context);
            // 旧 applyToScalarOrArray は直下のarrayだけを写し、入れ子arrayは文字列化する。
            return value.Kind == ValueKind.Array
                ? new(ValueKind.Array, Items: value.Items!.Select(item => Measure(item, context.WorkBudget)).ToArray()) : Measure(value, context.WorkBudget);
        }
        private static Value Measure(Value value, EvaluationBudget budget)
        {
            if (value.Kind == ValueKind.Null) return new(ValueKind.Null);
            return new(ValueKind.Number, Number: NativeLength(NativeString(value, budget), budget));
        }
    }
    private sealed record InRange(Node Argument, Node Lower, Node Upper, bool CaseSensitive, bool UpperInclusive = false) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Argument.UsesProperties || Lower.UsesProperties || Upper.UsesProperties;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata || Lower.UsesSideMetadata || Upper.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext || Lower.RequiresFolderComparisonContext || Upper.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            // 原典は型検査より先に下限・上限・値の順で全引数を評価する。
            var lower = Normalize(Lower.EvaluatePrepared(context));
            var upper = Normalize(Upper.EvaluatePrepared(context));
            var value = Argument.EvaluatePrepared(context);
            byte[]? lowerBytes = null, upperBytes = null;
            byte[] Bytes(byte[] text)
            {
                var count = text.Length;
                context.WorkBudget.Consume(count);
                if (!CaseSensitive) context.WorkBudget.Consume(count);
                var bytes = (byte[])text.Clone();
                if (!CaseSensitive)
                    for (var index = 0; index < bytes.Length; index++)
                        if (bytes[index] is >= (byte)'A' and <= (byte)'Z') bytes[index] += (byte)('a' - 'A');
                return bytes;
            }
            int Order(byte[] left, byte[] right)
            {
                var count = Math.Min(left.Length, right.Length);
                for (var index = 0; index < count; index++)
                {
                    context.WorkBudget.Consume(1);
                    var a = CaseSensitive ? (int)left[index] : unchecked((sbyte)left[index]);
                    var b = CaseSensitive ? (int)right[index] : unchecked((sbyte)right[index]);
                    if (a != b) return a < b ? -1 : 1;
                }
                return left.Length.CompareTo(right.Length);
            }
            Value Scalar(Value item)
            {
                context.WorkBudget.Consume(1);
                item = Normalize(item);
                if (Integer(item))
                {
                    if (!(Integer(lower) || lower.Kind == ValueKind.Floating) || !(Integer(upper) || upper.Kind == ValueKind.Floating)) return new(ValueKind.Null);
                    // 各辺だけで昇格し、もう一方のInt64比較の精度を保つ。
                    var above = lower.Kind == ValueKind.Floating ? (double)item.Number >= lower.Floating : item.Number >= lower.Number;
                    var below = upper.Kind == ValueKind.Floating ? WithinUpper((double)item.Number, upper.Floating) : WithinUpper(item.Number, upper.Number);
                    return Value.Bool(above && below);
                }
                if (item.Kind == ValueKind.Floating)
                {
                    if (!(Integer(lower) || lower.Kind == ValueKind.Floating) || !(Integer(upper) || upper.Kind == ValueKind.Floating)) return new(ValueKind.Null);
                    return Value.Bool(item.Floating >= (lower.Kind == ValueKind.Floating ? lower.Floating : (double)lower.Number)
                        && WithinUpper(item.Floating, upper.Kind == ValueKind.Floating ? upper.Floating : (double)upper.Number));
                }
                if (item.Kind == ValueKind.Timestamp && lower.Kind == ValueKind.Timestamp && upper.Kind == ValueKind.Timestamp)
                    return Value.Bool(item.Number >= lower.Number && WithinUpper(item.Number, upper.Number));
                if (item.Kind == ValueKind.String && lower.Kind == ValueKind.String && upper.Kind == ValueKind.String)
                {
                    lowerBytes ??= Bytes(lower.Text!); upperBytes ??= Bytes(upper.Text!);
                    var bytes = Bytes(item.Text!);
                    var above = Order(bytes, lowerBytes);
                    var below = Order(bytes, upperBytes);
                    return Value.Bool(above >= 0 && (UpperInclusive ? below <= 0 : below < 0));
                }
                return new(ValueKind.Null);
            }
            if (value.Kind != ValueKind.Array) return Scalar(value);
            context.WorkBudget.Consume(value.Items!.Count);
            var items = new Value[value.Items.Count];
            for (var index = 0; index < items.Length; index++) items[index] = Scalar(value.Items[index]);
            return new(ValueKind.Array, Items: items);
        }
        private bool WithinUpper(decimal value, decimal upper) => UpperInclusive ? value <= upper : value < upper;
        private bool WithinUpper(double value, double upper) => UpperInclusive ? value <= upper : value < upper;
        private static bool Integer(Value value) => value.Kind == ValueKind.Number && value.Number >= long.MinValue && value.Number <= long.MaxValue && decimal.Truncate(value.Number) == value.Number;
        private static Value Normalize(Value value) => value.Kind == ValueKind.Date
            ? new(ValueKind.Timestamp, Number: (value.Date.Ticks - DateTime.UnixEpoch.Ticks) / 10) : value;
    }
    private sealed record Abs(Node Argument) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Argument.UsesProperties;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var value = Argument.EvaluatePrepared(context);
            return value.Kind == ValueKind.Array
                ? new(ValueKind.Array, Items: value.Items!.Select(Scalar).ToArray()) : Scalar(value);
        }
        private static Value Scalar(Value value)
        {
            if (value.Kind == ValueKind.Floating)
                return new(ValueKind.Floating, Floating: BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value.Floating) & long.MaxValue));
            if (value.Kind != ValueKind.Number || value.Number < long.MinValue || value.Number > long.MaxValue
                || decimal.Truncate(value.Number) != value.Number) return new(ValueKind.Null);
            var integer = (long)value.Number;
            // 固定MSVC原典で観測したInt64最小値の折返しを明示する。
            return new(ValueKind.Number, Number: integer < 0 ? unchecked(-integer) : integer);
        }
    }
    private sealed record NumericConversion(Node Argument, string Function) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Argument.UsesProperties;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata;
        // native関数内でもDouble literalとInt64の型を区別する。
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            // 引数評価の例外を変換失敗へ置き換えず、直下のarrayだけを一度mapする。
            var value = Argument.EvaluatePrepared(context);
            return value.Kind == ValueKind.Array
                ? new(ValueKind.Array, Items: value.Items!.Select(item => ConvertScalar(item, context.WorkBudget)).ToArray()) : ConvertScalar(value, context.WorkBudget);
        }
        private Value ConvertScalar(Value value, EvaluationBudget budget)
        {
            if (value.Kind != ValueKind.String) return new(ValueKind.Null);
            budget.Consume(value.Text!.Length);
            // CRTの全byte消費検査により非ASCII/不正UTF-8は数値にならない。置換復号しない。
            if (!TryDecode(value.Text, out var source)) return new(ValueKind.Null);
            var cleaned = source.Replace(",", "", StringComparison.Ordinal);
            var text = cleaned.AsSpan();
            var start = 0;
            while (start < text.Length && (text[start] == ' ' || text[start] is >= '\t' and <= '\r')) start++;
            text = text[start..];
            if (text.IsEmpty) return new(ValueKind.Null);
            if (Function == "todouble" || Function == "tonumber" && text.IndexOfAny('.', 'e', 'E') >= 0)
                return TryFloating(text, out var floating) ? new(ValueKind.Floating, Floating: floating) : new(ValueKind.Null);

            // TryParse自体が許す終端NULも、nativeの全消費検査では失敗する。
            var digits = text[0] is '+' or '-' ? text[1..] : text;
            if (digits.IsEmpty) return new(ValueKind.Null);
            foreach (var digit in digits) if (digit is < '0' or > '9') return new(ValueKind.Null);
            return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
                ? new(ValueKind.Number, Number: integer) : new(ValueKind.Null);
        }
        private static bool TryFloating(ReadOnlySpan<char> text, out double result)
        {
            result = 0;
            var negative = text[0] == '-';
            var unsigned = text[0] is '+' or '-' ? text[1..] : text;
            if (unsigned.IsEmpty) return false;
            if (unsigned.Equals("inf", StringComparison.OrdinalIgnoreCase) || unsigned.Equals("infinity", StringComparison.OrdinalIgnoreCase))
            {
                result = negative ? double.NegativeInfinity : double.PositiveInfinity;
                return true;
            }
            if (unsigned.StartsWith("nan", StringComparison.OrdinalIgnoreCase))
            {
                var payload = unsigned[3..];
                if (!payload.IsEmpty)
                {
                    if (payload.Length < 2 || payload[0] != '(' || payload[^1] != ')') return false;
                    foreach (var character in payload[1..^1])
                        if (!(character is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_')) return false;
                }
                // MSVCのNaNは.NETの既定NaNとはpayload/signが異なる。
                var bits = payload.Equals("(snan)", StringComparison.OrdinalIgnoreCase) ? 0x7ff0000000000001UL
                    : payload.Equals("(ind)", StringComparison.OrdinalIgnoreCase) ? 0xfff8000000000000UL : 0x7fffffffffffffffUL;
                if (negative) bits |= 0x8000000000000000UL;
                result = BitConverter.UInt64BitsToDouble(bits);
                return true;
            }
            if (unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return TryHexFloating(unsigned[2..], negative, out result);

            var index = 0;
            var digits = 0;
            var nonzero = false;
            var significantDigits = 0;
            var storedEnd = -1;
            var discardedIntegerDigits = 0;
            var truncated = false;
            var dot = false;
            while (index < unsigned.Length)
            {
                var character = unsigned[index];
                if (character is >= '0' and <= '9')
                {
                    if (!nonzero && character != '0') nonzero = true;
                    if (nonzero)
                    {
                        if (significantDigits < 768)
                        {
                            significantDigits++;
                            if (significantDigits == 768) storedEnd = index + 1;
                        }
                        else
                        {
                            truncated = true;
                            if (!dot) discardedIntegerDigits++;
                        }
                    }
                    index++;
                    digits++;
                    continue;
                }
                if (character == '.' && !dot) { dot = true; index++; continue; }
                break;
            }
            if (digits == 0) return false;
            var exponentOffset = -1;
            var exponentMagnitude = 0;
            if (index < unsigned.Length && unsigned[index] is 'e' or 'E')
            {
                exponentOffset = index;
                index++;
                if (index < unsigned.Length && unsigned[index] is '+' or '-') index++;
                var exponentStart = index;
                while (index < unsigned.Length && unsigned[index] is >= '0' and <= '9')
                    exponentMagnitude = Math.Min(5201, exponentMagnitude * 10 + unsigned[index++] - '0');
                if (index == exponentStart) return false;
                if (nonzero && exponentMagnitude > 5200) return false;
            }
            if (index != unsigned.Length) return false;
            ReadOnlySpan<char> parseText = text;
            if (truncated)
            {
                // UCRTはdecimal mantissaを768桁まで保持し、それ以降は丸め情報にも使わない。
                var signLength = text.Length - unsigned.Length;
                var parseString = text[..signLength].ToString() + unsigned[..storedEnd].ToString()
                    + new string('0', discardedIntegerDigits)
                    + (exponentOffset < 0 ? "" : unsigned[exponentOffset..].ToString());
                parseText = parseString.AsSpan();
            }
            if (!double.TryParse(parseText,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out result) || !double.IsFinite(result)) return false;
            // MSVCは非zeroの丸め結果がzeroになる場合だけERANGEとする。subnormalは保持する。
            return result != 0 || !nonzero;
        }
        private static bool TryHexFloating(ReadOnlySpan<char> text, bool negative, out double result)
        {
            result = 0;
            ulong mantissa = 0;
            var storedDigits = 0;
            long significantDigits = 0, fractionalDigits = 0;
            var firstDigitBits = 0;
            var sticky = false;
            var dot = false;
            var anyDigit = false;
            var index = 0;
            while (index < text.Length)
            {
                var character = text[index];
                if (character == '.' && !dot) { dot = true; index++; continue; }
                var digit = character is >= '0' and <= '9' ? character - '0'
                    : character is >= 'a' and <= 'f' ? character - 'a' + 10
                    : character is >= 'A' and <= 'F' ? character - 'A' + 10 : -1;
                if (digit < 0) break;
                index++; anyDigit = true;
                if (dot) fractionalDigits++;
                if (significantDigits == 0 && digit == 0) continue;
                if (significantDigits++ == 0) firstDigitBits = digit >= 8 ? 4 : digit >= 4 ? 3 : digit >= 2 ? 2 : 1;
                // binary64の端数bitは64bit内で保持し、UCRTの768桁枠内だけstickyを追跡する。
                if (storedDigits < 16) { mantissa = (mantissa << 4) | (uint)digit; storedDigits++; }
                else if (significantDigits <= 768) sticky |= digit != 0;
            }
            if (!anyDigit) return false;
            var exponent = 0;
            if (index < text.Length && text[index] is 'p' or 'P')
            {
                index++;
                var exponentNegative = index < text.Length && text[index] == '-';
                if (index < text.Length && text[index] is '+' or '-') index++;
                var exponentStart = index;
                while (index < text.Length && text[index] is >= '0' and <= '9')
                    exponent = Math.Min(5201, exponent * 10 + text[index++] - '0');
                if (index == exponentStart) return false;
                if (exponentNegative) exponent = -exponent;
            }
            if (index != text.Length) return false;
            if (significantDigits == 0) { result = Math.CopySign(0, negative ? -1 : 1); return true; }
            if (exponent is < -5200 or > 5200) return false;
            var highestExponent = exponent - 4 * fractionalDigits + 4 * (significantDigits - 1) + firstDigitBits - 1;
            if (highestExponent is < -1075 or > 1023) return false;
            var retainedBits = highestExponent >= -1022 ? 53 : (int)(highestExponent + 1075);
            var storedBits = firstDigitBits + 4 * (storedDigits - 1);
            var shift = storedBits - retainedBits;
            ulong rounded;
            if (shift <= 0) rounded = mantissa << -shift;
            else
            {
                rounded = shift == 64 ? 0 : mantissa >> shift;
                var remainder = shift == 64 ? mantissa : mantissa & ((1UL << shift) - 1);
                var halfway = 1UL << (shift - 1);
                if (remainder > halfway || remainder == halfway && (sticky || (rounded & 1) != 0)) rounded++;
            }
            result = Math.CopySign(Math.ScaleB(rounded, highestExponent >= -1022 ? (int)highestExponent - 52 : -1074), negative ? -1 : 1);
            return result != 0 && double.IsFinite(result);
        }
    }
    private sealed record StringSearch(Node Haystack, Node Needle, Node? Occurrence, bool Find, bool CaseSensitive) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Haystack.UsesProperties || Needle.UsesProperties || Occurrence?.UsesProperties == true;
        public override bool UsesSideMetadata => Haystack.UsesSideMetadata || Needle.UsesSideMetadata || Occurrence?.UsesSideMetadata == true;
        // 他のnative文字列関数と同じく、小数literalのDouble型を維持する。
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Haystack.RequiresFolderComparisonContext
            || Needle.RequiresFolderComparisonContext || Occurrence?.RequiresFolderComparisonContext == true;

        public override Value Evaluate(Context context)
        {
            var haystack = Haystack.EvaluatePrepared(context);
            var needle = Needle.EvaluatePrepared(context);
            long occurrence = 0;
            // nativeはneedle検査より前に第3引数を一度評価する。
            if (Occurrence is not null)
            {
                var requested = Occurrence.EvaluatePrepared(context);
                if (requested.Kind != ValueKind.Number || requested.Number < long.MinValue || requested.Number > long.MaxValue
                    || decimal.Truncate(requested.Number) != requested.Number) return new(ValueKind.Null);
                occurrence = (long)requested.Number;
            }
            if (needle.Kind != ValueKind.String || needle.Text!.Length == 0) return new(ValueKind.Null);
            context.WorkBudget.Consume(needle.Text.Length);
            var pattern = (byte[])needle.Text.Clone();
            if (!CaseSensitive) LowerAscii(pattern);

            // KMPのprefix表を全要素で共有し、長い共通prefixでもO(needle + 全haystack bytes)にする。
            var prefixes = new int[pattern.Length];
            for (int index = 1, matched = 0; index < pattern.Length; index++)
            {
                context.WorkBudget.Consume(1);
                while (matched > 0 && pattern[index] != pattern[matched]) { context.WorkBudget.Consume(1); matched = prefixes[matched - 1]; }
                if (pattern[index] == pattern[matched]) matched++;
                prefixes[index] = matched;
            }
            Value Search(Value value)
            {
                if (value.Kind == ValueKind.Null || Find && occurrence < 0) return new(ValueKind.Null);
                var bytes = (byte[])NativeString(value, context.WorkBudget).Clone();
                if (!CaseSensitive) LowerAscii(bytes);
                long count = 0;
                for (int index = 0, matched = 0; index < bytes.Length; index++)
                {
                    context.WorkBudget.Consume(1);
                    while (matched > 0 && bytes[index] != pattern[matched]) { context.WorkBudget.Consume(1); matched = prefixes[matched - 1]; }
                    if (bytes[index] == pattern[matched]) matched++;
                    if (matched != pattern.Length) continue;
                    if (Find && count == occurrence) return new(ValueKind.Number, Number: index - pattern.Length + 1);
                    count++;
                    // 一致した全byteを消費し、重複する開始位置には戻らない。
                    matched = 0;
                }
                return Find ? new(ValueKind.Null) : new(ValueKind.Number, Number: count);
            }
            return haystack.Kind == ValueKind.Array
                ? new(ValueKind.Array, Items: haystack.Items!.Select(Search).ToArray()) : Search(haystack);
        }

        private static void LowerAscii(byte[] bytes)
        {
            // Poco::toLowerはUTF-8のUnicode case foldingを行わずASCII byteだけを変換する。
            for (var index = 0; index < bytes.Length; index++)
                if (bytes[index] is >= (byte)'A' and <= (byte)'Z') bytes[index] += (byte)('a' - 'A');
        }
    }
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static bool TryDecode(byte[] bytes, out string text)
    {
        try { text = StrictUtf8.GetString(bytes); return true; }
        catch (DecoderFallbackException) { text = ""; return false; }
    }
    private static string NativeUtf16(byte[] bytes, EvaluationBudget budget)
    {
        budget.Consume(bytes.Length);
        var text = new StringBuilder(bytes.Length);
        // 固定Poco TextIteratorは不正な単位を-1とし、UTF16へのcastでU+FFFFにする。
        for (var index = 0; index < bytes.Length;)
        {
            var first = bytes[index];
            var width = first < 0xc0 || first >= 0xf8 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
            var part = bytes.AsSpan(index, Math.Min(width, bytes.Length - index));
            var valid = Rune.DecodeFromUtf8(part, out var rune, out var consumed) == System.Buffers.OperationStatus.Done && consumed == width;
            text.Append(valid ? rune.ToString() : "\uffff");
            index += width;
        }
        return text.ToString();
    }
    private static byte[] NativeString(Value value, EvaluationBudget budget, ILineTransformTimestampEnvironment? logTimestampEnvironment = null)
    {
        if (value.Kind == ValueKind.String) { budget.Consume(value.Text!.Length); return value.Text; }
        if (value.Kind == ValueKind.Array)
        {
            // 構造用ASCIIと内部Stringをbyteのまま連結する。入れ子も同じ共有予算を使う。
            using var stream = new MemoryStream();
            budget.Consume(2); stream.WriteByte((byte)'[');
            for (var index = 0; index < value.Items!.Count; index++)
            {
                budget.Consume(1);
                if (index != 0) { budget.Consume(2); stream.Write(", "u8); }
                var item = NativeString(value.Items[index], budget, logTimestampEnvironment);
                budget.Consume(item.Length); stream.Write(item);
            }
            stream.WriteByte((byte)']'); return stream.ToArray();
        }
        var text = value.Kind switch
        {
            ValueKind.Null => "none", ValueKind.Boolean => value.Boolean ? "true" : "false",
            ValueKind.Number => value.Number.ToString(CultureInfo.InvariantCulture),
            ValueKind.Floating => FloatingString(value.Floating),
            ValueKind.Date or ValueKind.Timestamp => logTimestampEnvironment is null ? TimestampString(value)
                : LineTransformTimestampParser.Serialize(new(value.Kind == ValueKind.Date ? (value.Date.Ticks - DateTime.UnixEpoch.Ticks) / 10 : decimal.ToInt64(value.Number)),
                    new LineTransformExpression.TimestampEnvironmentAdapter(logTimestampEnvironment)), _ => "<unknown>"
        };
        var bytes = Encoding.UTF8.GetBytes(text); budget.Consume(bytes.Length); return bytes;
    }
    private static int NativeLength(byte[] bytes, EvaluationBudget budget)
    {
        budget.Consume(bytes.Length);
        var count = 0;
        for (var index = 0; index < bytes.Length; count++)
        {
            var lead = bytes[index];
            index += lead < 0xc0 || lead >= 0xfe ? 1 : lead < 0xe0 ? 2 : lead < 0xf0 ? 3 : lead < 0xf8 ? 4 : lead < 0xfc ? 5 : 6;
        }
        return count;
    }
    private sealed record Substr(Node Source, Node Start, Node? Length) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Source.UsesProperties || Start.UsesProperties || Length?.UsesProperties == true;
        public override bool UsesSideMetadata => Source.UsesSideMetadata || Start.UsesSideMetadata || Length?.UsesSideMetadata == true;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Source.RequiresFolderComparisonContext || Start.RequiresFolderComparisonContext || Length?.RequiresFolderComparisonContext == true;
        public override Value Evaluate(Context context)
        {
            // 型検査の前にstart→lengthを評価し、startがInt64のときだけsourceへ進む。
            var start = Start.EvaluatePrepared(context);
            var length = Length?.EvaluatePrepared(context) ?? new(ValueKind.Null);
            if (!Integer(start)) return new(ValueKind.Null);
            var source = Source.EvaluatePrepared(context);
            Value Slice(Value value)
            {
                context.WorkBudget.Consume(1);
                if (value.Kind == ValueKind.Null) return value;
                var bytes = NativeString(value, context.WorkBudget);
                var position = (long)start.Number;
                if (position < 0) position += bytes.Length;
                if (position < 0 || position >= bytes.Length) return Value.Str([]);
                var remaining = bytes.Length - position;
                var count = Integer(length) ? (long)length.Number : remaining;
                // 負lengthとremainingの和はInt64範囲内。巨大な正lengthは残量へclampする。
                count = count < 0 ? Math.Max(0, remaining + count) : Math.Min(remaining, count);
                context.WorkBudget.Consume((int)count);
                return Value.Str(bytes.AsSpan((int)position, (int)count).ToArray());
            }
            if (source.Kind != ValueKind.Array) return Slice(source);
            context.WorkBudget.Consume(source.Items!.Count);
            return new(ValueKind.Array, Items: source.Items.Select(Slice).ToArray());
        }
        private static bool Integer(Value value) => value.Kind == ValueKind.Number && value.Number >= long.MinValue && value.Number <= long.MaxValue && decimal.Truncate(value.Number) == value.Number;
    }
    private static string FloatingString(double value)
    {
        if (double.IsNaN(value))
        {
            var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
            if (bits == 0xfff8000000000000UL) return "-nan(ind)";
            var sign = (bits & 0x8000000000000000UL) != 0 ? "-" : "";
            var kind = (bits & 0x0008000000000000UL) == 0 ? "nan(snan)" : "nan";
            return sign + kind;
        }
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";
        return value.ToString("F6", CultureInfo.InvariantCulture);
    }
    private static string TimestampString(Value value)
    {
        var utc = value.Kind == ValueKind.Date
            ? DateTime.SpecifyKind(value.Date, DateTimeKind.Utc)
            : new DateTime(checked(DateTime.UnixEpoch.Ticks + checked(decimal.ToInt64(value.Number) * 10)), DateTimeKind.Utc);
        return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
    private sealed record TrimValue(Node Argument, bool Left, bool Right) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Argument.UsesProperties;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var value = Argument.EvaluatePrepared(context);
            return value.Kind == ValueKind.Array
                ? new(ValueKind.Array, Items: value.Items!.Select(item => TrimScalar(item, context.WorkBudget)).ToArray()) : TrimScalar(value, context.WorkBudget);
        }
        private Value TrimScalar(Value value, EvaluationBudget budget)
        {
            if (value.Kind == ValueKind.Null) return value;
            var text = NativeString(value, budget);
            var start = 0;
            var end = text.Length;
            if (Left) while (start < end && IsTrimCharacter(text[start])) start++;
            if (Right) while (end > start && IsTrimCharacter(text[end - 1])) end--;
            return Value.Str(text[start..end]);
        }
        private static bool IsTrimCharacter(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
    }
    private sealed record NotEach(Node Argument) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Argument.UsesProperties;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var value = Argument.EvaluatePrepared(context);
            static Value Invert(Value item) => item.AsBool is { } boolean ? Value.Bool(!boolean) : new(ValueKind.Null);
            // 直下のslotだけを写す。入れ子配列の判定はAsBoolの浅いboolean探索に従う。
            return value.Kind == ValueKind.Array
                ? new(ValueKind.Array, Items: value.Items!.Select(Invert).ToArray()) : Invert(value);
        }
    }
    private sealed record AnyOf(Node Argument) : Node(ValueKind.Boolean)
    {
        public override bool UsesProperties => Argument.UsesProperties;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var value = Argument.EvaluatePrepared(context);
            if (value.Kind == ValueKind.Boolean) return value;
            if (value.Kind != ValueKind.Array) return new(ValueKind.Null);
            foreach (var item in value.Items!)
                if (item.Kind == ValueKind.Boolean && item.Boolean) return Value.Bool(true);
            return Value.Bool(false);
        }
    }
    private sealed record AllOf(Node Argument) : Node(ValueKind.Boolean)
    {
        public override bool UsesProperties => Argument.UsesProperties;
        public override bool UsesSideMetadata => Argument.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Argument.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var value = Argument.EvaluatePrepared(context);
            if (value.Kind == ValueKind.Boolean) return value;
            if (value.Kind != ValueKind.Array) return new(ValueKind.Null);
            foreach (var item in value.Items!)
                if (item.Kind != ValueKind.Boolean || !item.Boolean) return Value.Bool(false);
            return Value.Bool(true);
        }
    }
    private sealed record At(Node Array, Node Index) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Array.UsesProperties || Index.UsesProperties;
        public override bool UsesSideMetadata => Array.UsesSideMetadata || Index.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Array.RequiresFolderComparisonContext || Index.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var array = Array.EvaluatePrepared(context);
            if (array.Kind != ValueKind.Array) return new(ValueKind.Null);
            var index = Index.EvaluatePrepared(context);
            if (index.Kind != ValueKind.Number || index.Number < long.MinValue || index.Number > long.MaxValue
                || decimal.Truncate(index.Number) != index.Number) return new(ValueKind.Null);
            var offset = (long)index.Number;
            var items = array.Items!;
            // 負indexの加算は一度だけ。CountはInt32なのでInt64.MinValueでもoverflowしない。
            if (offset < 0) offset += items.Count;
            return offset >= 0 && offset < items.Count ? items[(int)offset] : new(ValueKind.Null);
        }
    }
    private sealed record LogicalEach(Node Left, Node Right, bool IsAnd) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Left.UsesProperties || Right.UsesProperties;
        public override bool UsesSideMetadata => Left.UsesSideMetadata || Right.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Left.RequiresFolderComparisonContext || Right.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var left = Left.EvaluatePrepared(context);
            var right = Right.EvaluatePrepared(context);
            Value Apply(Value first, Value second)
            {
                var firstBool = first.AsBool;
                var secondBool = second.AsBool;
                return firstBool is { } a && secondBool is { } b
                    ? Value.Bool(IsAnd ? a && b : a || b) : new(ValueKind.Null);
            }
            if (left.Kind != ValueKind.Array && right.Kind != ValueKind.Array) return Apply(left, right);
            var length = Math.Max(left.Kind == ValueKind.Array ? left.Items!.Count : 0,
                right.Kind == ValueKind.Array ? right.Items!.Count : 0);
            Value At(Value value, int index) => value.Kind != ValueKind.Array ? value
                : index < value.Items!.Count ? value.Items[index] : new(ValueKind.Null);
            return new(ValueKind.Array, Items: Enumerable.Range(0, length).Select(index => Apply(At(left, index), At(right, index))).ToArray());
        }
    }
    private sealed record Choice(Node Index, IReadOnlyList<Node> Choices, bool Each) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Index.UsesProperties || Choices.Any(choice => choice.UsesProperties);
        public override bool UsesSideMetadata => Index.UsesSideMetadata || Choices.Any(choice => choice.UsesSideMetadata);
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Index.RequiresFolderComparisonContext
            || Choices.Any(choice => choice.RequiresFolderComparisonContext);
        public override Value Evaluate(Context context)
        {
            var index = Index.EvaluatePrepared(context);
            Value Select(Value item)
            {
                // native int64だけを受理し、選んだ枝をslotごとに評価する。配列枝もそのまま保持する。
                if (item.Kind != ValueKind.Number || item.Number < long.MinValue || item.Number > long.MaxValue
                    || decimal.Truncate(item.Number) != item.Number) return new(ValueKind.Null);
                var selected = (int)Math.Clamp((long)item.Number, 0L, Choices.Count - 1L);
                context.WorkBudget.Consume(1);
                return Choices[selected].EvaluatePrepared(context);
            }
            if (!Each || index.Kind != ValueKind.Array) return Select(index);
            // 配列確保前に全slotを予約し、各slotの枝評価も同じ予算で検査する。
            context.WorkBudget.Consume(index.Items!.Count);
            var items = new Value[index.Items.Count];
            for (var slot = 0; slot < items.Length; slot++) items[slot] = Select(index.Items[slot]);
            return new(ValueKind.Array, Items: items);
        }
    }
    private sealed record Conditional(Node Condition, Node WhenTrue, Node WhenFalse) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Condition.UsesProperties || WhenTrue.UsesProperties || WhenFalse.UsesProperties;
        public override bool UsesSideMetadata => Condition.UsesSideMetadata || WhenTrue.UsesSideMetadata || WhenFalse.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Condition.RequiresFolderComparisonContext
            || WhenTrue.RequiresFolderComparisonContext || WhenFalse.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var condition = Condition.EvaluatePrepared(context).AsBool;
            return (condition is true ? WhenTrue : WhenFalse).EvaluatePrepared(context);
        }
    }
    private sealed record ConditionalEach(Node Condition, Node WhenTrue, Node WhenFalse) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Condition.UsesProperties || WhenTrue.UsesProperties || WhenFalse.UsesProperties;
        public override bool UsesSideMetadata => Condition.UsesSideMetadata || WhenTrue.UsesSideMetadata || WhenFalse.UsesSideMetadata;
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Condition.RequiresFolderComparisonContext
            || WhenTrue.RequiresFolderComparisonContext || WhenFalse.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
        {
            var condition = Condition.EvaluatePrepared(context);
            var whenTrue = WhenTrue.EvaluatePrepared(context);
            var whenFalse = WhenFalse.EvaluatePrepared(context);
            Value Select(Value item, int index)
            {
                Value trueValue = whenTrue.Kind == ValueKind.Array
                    ? index < whenTrue.Items!.Count ? whenTrue.Items[index] : new(ValueKind.Null) : whenTrue;
                Value falseValue = whenFalse.Kind == ValueKind.Array
                    ? index < whenFalse.Items!.Count ? whenFalse.Items[index] : new(ValueKind.Null) : whenFalse;
                return item.AsBool is true ? trueValue : falseValue;
            }
            if (condition.Kind != ValueKind.Array) return Select(condition, 0);
            var conditions = condition.Items!;
            return new(ValueKind.Array, Items: Enumerable.Range(0, conditions.Count).Select(index => Select(conditions[index], index)).ToArray());
        }
    }
    private sealed record ArrayValue(IReadOnlyList<Node> Arguments) : Node(ValueKind.Dynamic)
    {
        public override bool UsesProperties => Arguments.Any(argument => argument.UsesProperties);
        public override bool UsesSideMetadata => Arguments.Any(argument => argument.UsesSideMetadata);
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Arguments.Any(argument => argument.RequiresFolderComparisonContext);
        public override Value Evaluate(Context context)
        {
            var items = new Value[Arguments.Count];
            for (var index = 0; index < Arguments.Count; index++)
                items[index] = Arguments[index].EvaluatePrepared(context);
            return new(ValueKind.Array, Items: items);
        }
    }
    private sealed record AllEqual(IReadOnlyList<Node> Arguments) : Node(ValueKind.Boolean)
    {
        public override bool UsesProperties => Arguments.Any(argument => argument.UsesProperties);
        public override bool UsesSideMetadata => Arguments.Any(argument => argument.UsesSideMetadata);
        public override bool UsesAllSideMetadata => true;
        public override bool RequiresFolderComparisonContext => Arguments.Any(argument => argument.RequiresFolderComparisonContext);
        public override Value Evaluate(Context context)
        {
            // AllEqual は式評価位置に関係なく、doubleリテラルを整数と区別する。
            var valueContext = context with { AllEqualValueExpression = true };
            var first = Arguments[0].EvaluatePrepared(valueContext);
            if (first.Kind == ValueKind.Array)
            {
                var items = first.Items!;
                for (var index = 1; index < items.Count; index++)
                    if (!StrictEqual(items[0], items[index], context.WorkBudget)) return Value.Bool(false);
            }
            else
            {
                for (var index = 1; index < Arguments.Count; index++)
                    if (!StrictEqual(first, Arguments[index].EvaluatePrepared(valueContext), context.WorkBudget)) return Value.Bool(false);
            }
            return Value.Bool(true);
        }
        private static bool StrictEqual(Value left, Value right, EvaluationBudget budget)
        {
            // 配列を再帰呼出しせず、比較中の階層だけ保持する。数値の型変換や case 指定は適用しない。
            var pending = new Stack<(Value Left, Value Right, int Index)>();
            pending.Push((left, right, -1));
            while (pending.TryPop(out var pair))
            {
                var a = pair.Left;
                var b = pair.Right;
                // 現行の日時リテラルと属性日時は、旧実装では共に Poco::Timestamp。
                if (a.Kind == ValueKind.Date) a = new(ValueKind.Timestamp, Number: (a.Date.Ticks - DateTime.UnixEpoch.Ticks) / 10);
                if (b.Kind == ValueKind.Date) b = new(ValueKind.Timestamp, Number: (b.Date.Ticks - DateTime.UnixEpoch.Ticks) / 10);
                if (a.Kind != b.Kind) return false;
                if (a.Kind == ValueKind.Array)
                {
                    if (a.Items!.Count != b.Items!.Count) return false;
                    var index = Math.Max(0, pair.Index);
                    if (index < a.Items.Count)
                    {
                        pending.Push((a, b, index + 1));
                        pending.Push((a.Items[index], b.Items[index], -1));
                    }
                    continue;
                }
                if (a.Kind == ValueKind.String) budget.Consume(Math.Min(a.Text!.Length, b.Text!.Length));
                var equal = a.Kind switch
                {
                    ValueKind.Null => true,
                    ValueKind.Boolean => a.Boolean == b.Boolean,
                    ValueKind.Number or ValueKind.Timestamp => a.Number == b.Number,
                    ValueKind.Floating => a.Floating == b.Floating,
                    ValueKind.String => a.Text!.AsSpan().SequenceEqual(b.Text),
                    _ => false
                };
                if (!equal) return false;
            }
            return true;
        }
    }
    private sealed record Logical(string Operator, Node Left, Node? Right = null) : Node(ValueKind.Boolean)
    {
        public override bool UsesSideMetadata => Left.UsesSideMetadata || Right?.UsesSideMetadata == true;
        public override bool UsesAllSideMetadata => Left.UsesAllSideMetadata || Right?.UsesAllSideMetadata == true;
        public override bool UsesProperties => Left.UsesProperties || Right?.UsesProperties == true;
        public override bool RequiresFolderComparisonContext => Left.RequiresFolderComparisonContext || Right?.RequiresFolderComparisonContext == true;
        public override Value Evaluate(Context context)
        {
            var left = Left.EvaluatePrepared(context).AsBool;
            if (Operator == "not") return left is { } value ? Value.Bool(!value) : new(ValueKind.Null);
            if (Operator == "and")
            {
                if (left is null) return new(ValueKind.Null);
                if (left == false) return Value.Bool(false);
                return Right!.EvaluatePrepared(context).AsBool is { } right ? Value.Bool(right) : new(ValueKind.Null);
            }
            if (Operator == "or")
            {
                if (left == true) return Value.Bool(true);
                var right = Right!.EvaluatePrepared(context).AsBool;
                if (right == true) return Value.Bool(true);
                return left.HasValue || right.HasValue ? Value.Bool(false) : new(ValueKind.Null);
            }
            throw new InvalidOperationException("未対応の論理演算子です。");
        }
    }
    private sealed record Comparison(string Operator, Node Left, Node Right, bool CaseSensitive, Regex? Pattern = null) : Node(ValueKind.Boolean)
    {
        public Regex? PreparedBinaryPattern { get; set; }
        public override bool UsesSideMetadata => Left.UsesSideMetadata || Right.UsesSideMetadata;
        public override bool UsesAllSideMetadata => Left.UsesAllSideMetadata || Right.UsesAllSideMetadata;
        public override bool UsesProperties => Left.UsesProperties || Right.UsesProperties;
        public override bool RequiresFolderComparisonContext => Left.RequiresFolderComparisonContext || Right.RequiresFolderComparisonContext;
        public override Value Evaluate(Context context)
            => Compare(Left.EvaluatePrepared(context), Right.EvaluatePrepared(context), context.MetadataSides is not null || context.SideMetadataExpression || context.NativeValueExpression || UsesProperties || UsesSideMetadata, context.WorkBudget);
        private Value Compare(Value a, Value b, bool nativeSemantics, EvaluationBudget budget)
        {
            if (a.Kind == ValueKind.Array || b.Kind == ValueKind.Array)
            {
                if (a.Kind == ValueKind.Array && b.Kind == ValueKind.Array && Operator is "=" or "==" or "!=")
                {
                    var equal = a.Items!.Count == b.Items!.Count && a.Items.Zip(b.Items).All(pair =>
                        new Comparison("==", Left, Right, CaseSensitive).Compare(pair.First, pair.Second, nativeSemantics, budget) is { Kind: ValueKind.Boolean, Boolean: true });
                    return Value.Bool(Operator == "!=" ? !equal : equal);
                }
                var count = Math.Max(a.Items?.Count ?? 0, b.Items?.Count ?? 0);
                Value At(Value value, int index) => value.Kind != ValueKind.Array ? value
                    : index < value.Items!.Count ? value.Items[index] : new(ValueKind.Null);
                return new(ValueKind.Array, Items: Enumerable.Range(0, count).Select(index =>
                    a.Kind == ValueKind.Array && b.Kind == ValueKind.Array && (index >= a.Items!.Count || index >= b.Items!.Count)
                        ? new(ValueKind.Null) : Compare(At(a, index), At(b, index), nativeSemantics, budget)).ToArray());
            }
            if (nativeSemantics && a.Kind == ValueKind.Floating && b.Kind is not (ValueKind.Floating or ValueKind.Number)) return new(ValueKind.Null);
            if (a.Kind == ValueKind.Null || b.Kind == ValueKind.Null)
            {
                if (nativeSemantics && Operator is not ("=" or "==" or "!=")) return new(ValueKind.Null);
                return Value.Bool(Operator == "!=" ? a.Kind != b.Kind : Operator is "=" or "==" && a.Kind == b.Kind);
            }
            if (a.Kind == ValueKind.Timestamp && b.Kind == ValueKind.Date)
                b = new(ValueKind.Timestamp, Number: (b.Date.Ticks - DateTime.UnixEpoch.Ticks) / 10);
            if (b.Kind == ValueKind.Timestamp && a.Kind == ValueKind.Date)
                a = new(ValueKind.Timestamp, Number: (a.Date.Ticks - DateTime.UnixEpoch.Ticks) / 10);
            if (a.Kind is ValueKind.Number or ValueKind.Floating && b.Kind is ValueKind.Number or ValueKind.Floating
                && (a.Kind == ValueKind.Floating || b.Kind == ValueKind.Floating))
            {
                a = new(ValueKind.Floating, Floating: a.Kind == ValueKind.Floating ? a.Floating : (double)a.Number);
                b = new(ValueKind.Floating, Floating: b.Kind == ValueKind.Floating ? b.Floating : (double)b.Number);
                return Value.Bool(Operator switch
                {
                    "=" or "==" => a.Floating == b.Floating, "!=" => a.Floating != b.Floating,
                    "<" => a.Floating < b.Floating, "<=" => a.Floating <= b.Floating,
                    ">" => a.Floating > b.Floating, ">=" => a.Floating >= b.Floating, _ => false
                });
            }
            if (a.Kind != b.Kind && !(a.Kind == ValueKind.String && b.Kind == ValueKind.Regex && Operator is "matches" or "recontains"))
                return nativeSemantics && Operator is not ("=" or "==" or "!=") ? new(ValueKind.Null) : Value.Bool(Operator == "!=");
            if (Operator is "contains" or "like" or "matches" or "recontains" && a.Kind != ValueKind.String)
                return nativeSemantics ? new(ValueKind.Null) : Value.Bool(false);
            if (Operator == "contains")
            {
                if (b.Text!.Length == 0) return Value.Bool(false);
                budget.Consume(a.Text!.Length + b.Text.Length);
                var subject = a.Text; var needle = b.Text;
                if (!CaseSensitive)
                {
                    subject = (byte[])subject.Clone(); needle = (byte[])needle.Clone();
                    LowerAscii(subject); LowerAscii(needle);
                }
                return Value.Bool(subject.AsSpan().IndexOf(needle) >= 0);
            }
            if (Operator == "like") return Value.Bool(Glob(a.Text!, b.Text!, CaseSensitive, budget));
            if (Operator is "matches" or "recontains")
            {
                var pattern = Pattern ?? PreparedBinaryPattern;
                if (pattern is null)
                {
                    if (!TryDecode(b.Text!, out var expression)) return Value.Bool(false);
                    try { pattern = MakeRegex(expression, CaseSensitive, Operator == "matches"); }
                    catch (ArgumentException) { return Value.Bool(false); }
                }
                budget.Consume(a.Text!.Length);
                return Value.Bool((Operator != "matches" || a.Text.Length != 0) && TryDecode(a.Text, out var subject) && pattern.IsMatch(subject));
            }
            var order = a.Kind switch
            {
                ValueKind.Boolean => a.Boolean.CompareTo(b.Boolean),
                ValueKind.Number or ValueKind.Timestamp => a.Number.CompareTo(b.Number),
                ValueKind.Floating => a.Floating.CompareTo(b.Floating),
                ValueKind.Date => a.Date.CompareTo(b.Date),
                ValueKind.String => StringOrder(a.Text!, b.Text!, CaseSensitive, budget),
                _ => 0
            };
            return Value.Bool(Operator switch { "=" or "==" => order == 0, "!=" => order != 0, "<" => order < 0, "<=" => order <= 0, ">" => order > 0, ">=" => order >= 0, _ => false });
        }
        private static void LowerAscii(byte[] bytes)
        {
            for (var index = 0; index < bytes.Length; index++)
                if (bytes[index] is >= (byte)'A' and <= (byte)'Z') bytes[index] += (byte)('a' - 'A');
        }
        private static int StringOrder(byte[] left, byte[] right, bool sensitive, EvaluationBudget budget)
        {
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                budget.Consume(1);
                int Fold(byte value) => sensitive ? value : unchecked((sbyte)(value is >= (byte)'A' and <= (byte)'Z' ? value + ('a' - 'A') : value));
                var order = Fold(left[index]).CompareTo(Fold(right[index]));
                if (order != 0) return order;
            }
            return left.Length.CompareTo(right.Length);
        }
        private static bool Glob(byte[] value, byte[] patternBytes, bool caseSensitive, EvaluationBudget budget)
        {
            int[] Tokens(byte[] bytes)
            {
                budget.Consume(bytes.Length);
                var tokens = new List<int>();
                for (var index = 0; index < bytes.Length;)
                {
                    var first = bytes[index];
                    var width = first < 0xc0 || first >= 0xf8 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
                    var part = bytes.AsSpan(index, Math.Min(width, bytes.Length - index));
                    var valid = Rune.DecodeFromUtf8(part, out var rune, out var consumed) == System.Buffers.OperationStatus.Done && consumed == width;
                    var scalar = valid ? rune.Value : -1;
                    if (!caseSensitive)
                    {
                        scalar = LineTransformGlob.ToLower(scalar, out var comparisons);
                        budget.Consume(comparisons);
                    }
                    tokens.Add(scalar); index += width;
                }
                return tokens.ToArray();
            }
            var subject = Tokens(value); var pattern = Tokens(patternBytes);
            static ArgumentException BadRange() => new("glob集合または範囲の構文が不正です。");
            static ArgumentException BadEscape() => new("globのbackslashに続く文字がありません。");
            bool MatchSet(ref int p, int scalar)
            {
                while (p < pattern.Length)
                {
                    budget.Consume(1);
                    if (pattern[p] == ']') { p++; return false; }
                    if (pattern[p] == '\\' && ++p == pattern.Length) throw BadEscape();
                    var first = pattern[p++]; var last = first;
                    if (p < pattern.Length && pattern[p] == '-')
                    {
                        if (++p == pattern.Length) throw BadRange();
                        last = pattern[p++];
                    }
                    if (first > scalar || scalar > last) continue;
                    while (p < pattern.Length)
                    {
                        budget.Consume(1);
                        if (pattern[p++] == ']') return true;
                        if (pattern[p - 1] == '\\' && ++p == pattern.Length) break;
                    }
                    throw BadRange();
                }
                return false;
            }
            // nativeのstar再試行順をstackへ移し、深いpatternでCLR stackを消費しない。
            var alternatives = new Stack<(int Pattern, int Subject)>();
            var p = 0; var s = 0;
            while (true)
            {
                var failed = false;
                while (p < pattern.Length)
                {
                    budget.Consume(1);
                    if (s == subject.Length) { while (p < pattern.Length && pattern[p] == '*') p++; break; }
                    var ch = pattern[p++];
                    if (ch == '?') { s++; continue; }
                    if (ch == '*')
                    {
                        if (p == pattern.Length) return true;
                        if (s + 1 < subject.Length) alternatives.Push((p - 1, s + 1));
                        continue;
                    }
                    if (ch == '[')
                    {
                        if (p == pattern.Length) throw BadRange();
                        var invert = pattern[p] == '!';
                        if (invert && ++p == pattern.Length) throw BadRange();
                        var found = MatchSet(ref p, subject[s++]);
                        if (invert ? found : !found) { failed = true; break; }
                        continue;
                    }
                    if (ch == '\\') { if (p == pattern.Length) throw BadEscape(); ch = pattern[p++]; }
                    if (ch != subject[s++]) { failed = true; break; }
                }
                if (!failed && p == pattern.Length && s == subject.Length) return true;
                if (!alternatives.TryPop(out var next)) return false;
                p = next.Pattern; s = next.Subject;
            }
        }
    }

    private enum TokenKind { End, Word, String, Number, Date, Version, Operator, LeftParen, RightParen, Comma, Directive }
    private readonly record struct Token(TokenKind Kind, string Text, int Position);
    private sealed class Parser
    {
        private readonly string text;
        private int cursor;
        private int tokenCount;
        private Token current;
        private bool optimize = true;
        private bool caseSensitive;
        private string expressionName = "";
        private readonly HashSet<string> propertyNames;
        private readonly Context optimizationContext;
        private readonly Value? now;
        private readonly Value? today;
        public Parser(string text, HashSet<string> propertyNames, ILineTransformTimestampEnvironment? timestampEnvironment,
            ILineTransformStringEnvironment? stringEnvironment, ILineTransformLogEnvironment? logEnvironment,
            IFileFilterListEnvironment? listEnvironment, CancellationToken cancellationToken)
        {
            optimizationContext = new("", false, 0, DateTime.UnixEpoch, null)
            {
                NativeValueExpression = true, StringEnvironment = stringEnvironment,
                TimestampEnvironment = timestampEnvironment, LogEnvironment = logEnvironment, ListEnvironment = listEnvironment, CancellationToken = cancellationToken, WorkBudget = new(cancellationToken)
            };
            // 原典UpdateTimestampと同じく、最初の字句を読む前に一組を確定する。
            if (timestampEnvironment is ILineTransformTimestampClockEnvironment clock)
            {
                var epoch = clock.CaptureNowEpochMicroseconds();
                var midnight = LineTransformTimestampParser.Today(new(epoch), new LineTransformExpression.TimestampEnvironmentAdapter(clock));
                now = new(ValueKind.Timestamp, Number: epoch);
                today = new(ValueKind.Timestamp, Number: midnight.Value);
            }
            if (text.Length > MaxExpressionLength) throw new FormatException("式は 16,384 文字以下にしてください。");
            this.text = text;
            this.propertyNames = propertyNames;
            Advance();
        }
        public Node Parse()
        {
            while (current.Kind == TokenKind.Directive)
            {
                switch (current.Text.ToLowerInvariant())
                {
                    case "cs": case "casesensitive": caseSensitive = true; break;
                    case "ci": case "caseinsensitive": caseSensitive = false; break;
                    case "opt": case "optimize": optimize = true; break;
                    case "noopt": case "nooptimize": optimize = false; break;
                    case "name":
                        Advance();
                        if (!Take("=")) throw Error("@name の後に '=' が必要です。");
                        if (current.Kind is not (TokenKind.Word or TokenKind.String)) throw Error("@name に名前を指定してください。");
                        expressionName = current.Text;
                        Advance();
                        continue;
                    default: throw Error("未対応のディレクティブです。");
                }
                Advance();
            }
            var root = Or(0);
            if (current.Kind != TokenKind.End) throw Error("未対応の構文または余分な文字があります。");
            if (optimize) OptimizeTree(root, optimizationContext);
            return root;
        }
        private Node Or(int depth)
        {
            var result = And(depth);
            while (Take("or") || Take("||")) { var right = And(depth); result = new Logical("or", result, right); }
            return result;
        }
        private Node And(int depth)
        {
            var result = Not(depth);
            while (Take("and") || Take("&&")) { var right = Not(depth); result = new Logical("and", result, right); }
            return result;
        }
        private Node Not(int depth)
        {
            if (depth > 64) throw Error("式の入れ子は 64 以下にしてください。");
            if (Take("not") || Take("!")) return new Logical("not", Not(depth + 1));
            var left = Arithmetic(depth);
            var negate = Take("not");
            var op = current.Text.ToLowerInvariant();
            if (current.Kind is not (TokenKind.Word or TokenKind.Operator) ||
                op is not ("=" or "==" or "!=" or "<" or "<=" or ">" or ">=" or "contains" or "matches" or "recontains" or "like"))
            {
                if (negate) throw Error("not の後に文字列比較演算子が必要です。");
                return left;
            }
            Advance();
            var right = Arithmetic(depth);
            if (negate && op is not ("contains" or "matches" or "recontains" or "like")) throw Error("未対応の not 比較です。");
            Regex? regex = null;
            Node comparison = new Comparison(op, left, right, caseSensitive, regex);
            return negate ? new Logical("not", comparison) : comparison;
        }
        private Node Arithmetic(int depth, int minimumPrecedence = 0)
        {
            if (depth > 64) throw Error("式の入れ子は 64 以下にしてください。");
            Node left;
            if (current.Kind == TokenKind.Operator && current.Text == "-")
            {
                var minus = current; Advance();
                // 旧signed numericのInt64最小値だけ、正の範囲外operandを作らず保持する。
                if (current.Kind == TokenKind.Number && current.Text == "9223372036854775808" && current.Position == minus.Position + 1)
                {
                    left = new Literal(new(ValueKind.Number, Number: long.MinValue)); Advance();
                }
                else left = new Negate(Arithmetic(depth + 1, 3));
            }
            else if (current.Kind == TokenKind.Operator && current.Text == "+")
            {
                var plus = current; Advance();
                if (current.Kind != TokenKind.Number || current.Position != plus.Position + 1)
                    throw Error("先頭の+は直後の数値リテラルだけに指定してください。");
                left = Term(depth);
            }
            else left = Term(depth);
            while (current.Kind == TokenKind.Operator)
            {
                var op = current.Text;
                var precedence = op is "+" or "-" ? 1 : op is "*" or "/" or "%" ? 2 : 0;
                if (precedence == 0 || precedence < minimumPrecedence) break;
                Advance();
                left = new ArithmeticValue(op, left, Arithmetic(depth + 1, precedence + 1));
            }
            return left;
        }
        private Node Term(int depth)
        {
            var token = current;
            Advance();
            if (token.Kind == TokenKind.LeftParen)
            {
                var result = Or(depth + 1);
                if (current.Kind != TokenKind.RightParen) throw Error("閉じ括弧がありません。");
                Advance();
                return result;
            }
            if (token.Kind == TokenKind.String) return new Literal(Value.Str(token.Text));
            if (token.Kind == TokenKind.Date)
            {
                // Poco は時分秒を1〜2桁、秒小数を6桁だけ採取する。calendar/zone検証の前に正規化する。
                var dateText = Regex.Replace(token.Text, @"(?<![0-9:])([0-9]{1,2}):([0-9]{1,2}):([0-9]{1,2})(?![0-9])(?:([.,])([0-9]+))?",
                    match => match.Groups[1].Value.PadLeft(2, '0') + ":" + match.Groups[2].Value.PadLeft(2, '0') + ":" + match.Groups[3].Value.PadLeft(2, '0')
                        + match.Groups[4].Value + match.Groups[5].Value[..Math.Min(6, match.Groups[5].Value.Length)], RegexOptions.CultureInvariant, RegexTimeout);
                if (!DateTime.TryParse(dateText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) throw Error("日時リテラルが不正です。");
                if (DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out var localDate)
                    && localDate.Kind == DateTimeKind.Unspecified)
                {
                    // Poco::Timezone::tzd() は日時の過去のoffsetではなく、現在の標準時＋夏時間を使う。
                    var ticks = localDate.Ticks - TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).Ticks;
                    if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) throw Error("日時リテラルが範囲外です。");
                    date = new DateTime(ticks, DateTimeKind.Utc);
                }
                return new Literal(new(ValueKind.Date, Date: Microseconds(date)));
            }
            if (token.Kind == TokenKind.Version) return new VersionLiteral(ParseVersionLiteral(token.Text));
            if (token.Kind == TokenKind.Number)
            {
                var suffix = token.Text.Length;
                while (suffix > 0 && char.IsLetter(token.Text[suffix - 1])) suffix--;
                if (suffix < token.Text.Length)
                {
                    if (TryDurationLiteral(token.Text, suffix, out var duration)) return new DurationLiteral(duration);
                    var nativeBytes = NativeSizeBytes(token.Text, suffix);
                    if (token.Text[suffix..].ToLowerInvariant() is "b" or "kb" or "mb" or "gb" or "tb" && nativeBytes is null)
                        throw Error("サイズが64bit整数の範囲を超えています。");
                    return new SizeLiteral(Number(token.Text), nativeBytes);
                }
                if ((char.IsDigit(token.Text[^1]) || token.Text[^1] == '.') && token.Text.IndexOfAny(['.', 'e', 'E']) >= 0)
                {
                    var floating = double.Parse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (!double.IsFinite(floating)) throw Error("小数が Double の範囲を超えています。");
                    return new NumberLiteral(floating);
                }
                if (!long.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var integer))
                    throw Error("整数リテラルが64bit整数の範囲を超えています。");
                return new Literal(new(ValueKind.Number, Number: integer));
            }
            if (token.Kind != TokenKind.Word) throw Error("値が必要です。");
            var word = token.Text.ToLowerInvariant();
            var folderSide = word.StartsWith("left", StringComparison.Ordinal) ? (int?)0
                : word.StartsWith("middle", StringComparison.Ordinal) ? 1
                : word.StartsWith("right", StringComparison.Ordinal) ? -1 : null;
            var folderFunction = folderSide is null ? word : word[(folderSide == 1 ? 6 : folderSide == 0 ? 4 : 5)..];
            if (folderFunction is "lineat" or "lineoffsetat" or "column" or "columnrange" or "columnat" or "columnoffsetat")
            {
                var arity = folderFunction is "columnat" or "columnoffsetat" ? 2 : 1;
                if (current.Kind != TokenKind.LeftParen) throw Error(token.Text + " には引数が必要です。");
                Advance();
                var arguments = new List<Node>();
                while (current.Kind != TokenKind.RightParen)
                {
                    if (current.Kind is TokenKind.Comma or TokenKind.End) throw Error(token.Text + " の引数が必要です。");
                    arguments.Add(Or(depth + 1));
                    if (arguments.Count > arity) throw Error(token.Text + " の引数が多すぎます。");
                    if (current.Kind != TokenKind.Comma) break;
                    Advance();
                    if (current.Kind == TokenKind.RightParen) throw Error(token.Text + " の引数が必要です。");
                }
                if (arguments.Count != arity || current.Kind != TokenKind.RightParen)
                    throw Error(token.Text + " の引数数または閉じ括弧が不正です。");
                Advance();
                var node = new FolderSideValue(folderFunction, arguments.ToArray(), folderSide);
                return node;
            }
            if (word is "average" or "maximum" or "minimum" or "sum"
                or "matchcount" or "matchnumber" or "matchdistance" or "matchdistancebefore" or "matchdistanceafter"
                or "matchcontext" or "matchinside" or "matchblockcount" or "matchblocknumber" or "matchblockoffset" or "matchblocksize")
            {
                var (minimum, maximum) = word switch
                {
                    "average" or "maximum" or "minimum" or "sum" => (1, 2),
                    "matchcontext" => (3, 3),
                    "matchinside" => (2, 2),
                    _ => (1, 1)
                };
                if (current.Kind != TokenKind.LeftParen) throw Error(token.Text + " には引数が必要です。");
                Advance();
                var arguments = new List<Node>();
                while (current.Kind != TokenKind.RightParen)
                {
                    if (current.Kind is TokenKind.Comma or TokenKind.End) throw Error(token.Text + " の引数が必要です。");
                    arguments.Add(Or(depth + 1));
                    if (arguments.Count > maximum) throw Error(token.Text + " の引数が多すぎます。");
                    if (current.Kind != TokenKind.Comma) break;
                    Advance();
                    if (current.Kind == TokenKind.RightParen) throw Error(token.Text + " の引数が必要です。");
                }
                if (arguments.Count < minimum || current.Kind != TokenKind.RightParen)
                    throw Error(token.Text + " の引数数または閉じ括弧が不正です。");
                Advance();
                return new FolderProviderUnavailable(arguments.ToArray());
            }
            if (word == "regexcount")
            {
                try
                {
                    if (current.Kind != TokenKind.LeftParen) throw Error("RegexCount には引数を2つ指定してください。");
                    Advance();
                    if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End) throw Error("RegexCount のsourceが必要です。");
                    var source = Or(depth + 1);
                    if (current.Kind != TokenKind.Comma) throw Error("RegexCount にはカンマで区切った引数を2つ指定してください。");
                    Advance();
                    if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End) throw Error("RegexCount のpatternが必要です。");
                    var pattern = Or(depth + 1);
                    if (current.Kind != TokenKind.RightParen) throw Error("RegexCount の引数数または閉じ括弧が不正です。");
                    Advance();
                    return new RegexCount(source, pattern, caseSensitive);
                }
                catch (FormatException error) { throw Error("RegexCount の構文が不正です。" + error.Message); }
            }
            if (word is "regexfind" or "regexextract")
            {
                var function = word == "regexfind" ? "RegexFind" : "RegexExtract";
                try
                {
                    if (current.Kind != TokenKind.LeftParen) throw Error(function + " には引数を2〜4つ指定してください。");
                    Advance();
                    if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End) throw Error(function + " のsourceが必要です。");
                    var source = Or(depth + 1);
                    if (current.Kind != TokenKind.Comma) throw Error(function + " にはsourceとpatternが必要です。");
                    Advance();
                    if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End) throw Error(function + " のpatternが必要です。");
                    var pattern = Or(depth + 1);
                    Node? group = null, occurrence = null;
                    if (current.Kind == TokenKind.Comma)
                    {
                        Advance();
                        if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End) throw Error(function + " のgroupが必要です。");
                        group = Or(depth + 1);
                        if (current.Kind == TokenKind.Comma)
                        {
                            Advance();
                            if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End) throw Error(function + " のoccurrenceが必要です。");
                            occurrence = Or(depth + 1);
                        }
                    }
                    if (current.Kind != TokenKind.RightParen) throw Error(function + " の引数数または閉じ括弧が不正です。");
                    Advance();
                    return new RegexPosition(function, source, pattern, group, occurrence, caseSensitive);
                }
                catch (FormatException error) { throw Error(function + " の構文が不正です。" + error.Message); }
            }
            if (word is "regexreplacewithlist" or "replacewithlist")
            {
                var function = word == "replacewithlist" ? "ReplaceWithList" : "RegexReplaceWithList";
                try
                {
                    if (current.Kind != TokenKind.LeftParen) throw Error(function + " には引数を2つ指定してください。");
                    Advance();
                    if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End) throw Error(function + " のsourceが必要です。");
                    var source = Or(depth + 1);
                    if (current.Kind != TokenKind.Comma) throw Error(function + " には引数を2つ指定してください。");
                    Advance();
                    if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End) throw Error(function + " のlistが必要です。");
                    var list = Or(depth + 1);
                    if (current.Kind != TokenKind.RightParen) throw Error(function + " の引数数または閉じ括弧が不正です。");
                    Advance();
                    return new ReplaceList(source, list, caseSensitive, word == "replacewithlist");
                }
                catch (FormatException error) { throw Error(function + " の構文が不正です。" + error.Message); }
            }
            if (word is "regexreplace" or "replace")
            {
                var function = word == "replace" ? "Replace" : "RegexReplace";
                try
                {
                    if (current.Kind != TokenKind.LeftParen) throw Error(function + " には引数を3つ指定してください。");
                    Advance();
                    if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End) throw Error(function + " のsourceが必要です。");
                    var source = Or(depth + 1);
                    if (current.Kind != TokenKind.Comma) throw Error(function + " には引数を3つ指定してください。");
                    Advance();
                    if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End) throw Error(function + " のpatternが必要です。");
                    var pattern = Or(depth + 1);
                    if (current.Kind != TokenKind.Comma) throw Error(function + " にはreplacementが必要です。");
                    Advance();
                    if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End) throw Error(function + " のreplacementが必要です。");
                    var replacement = Or(depth + 1);
                    if (current.Kind != TokenKind.RightParen) throw Error(function + " の引数数または閉じ括弧が不正です。");
                    Advance();
                    return new RegexReplace(source, pattern, replacement, caseSensitive, word == "replace");
                }
                catch (FormatException error) { throw Error(function + " の構文が不正です。" + error.Message); }
            }
            if (word is "logerror" or "logwarn" or "loginfo")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error($"{token.Text} には引数を1つ以上指定してください。");
                Advance();
                var arguments = new List<Node>();
                while (true)
                {
                    if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End) throw Error($"{token.Text} の引数が必要です。");
                    arguments.Add(Or(depth + 1));
                    if (current.Kind != TokenKind.Comma) break;
                    Advance();
                }
                if (current.Kind != TokenKind.RightParen) throw Error($"{token.Text} の閉じ括弧がありません。");
                Advance();
                return new LogValue(arguments.ToArray(), word == "logerror" ? LineTransformLogLevel.Error : word == "logwarn" ? LineTransformLogLevel.Warning : LineTransformLogLevel.Information, expressionName);
            }
            if (word is "now" or "today")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error($"{token.Text} には引数なしの括弧を指定してください。");
                Advance();
                if (current.Kind != TokenKind.RightParen) throw Error($"{token.Text} に引数は指定できません。");
                var captured = (word == "now" ? now : today) ?? throw Error("Now/Todayには明示的な時計環境が必要です。");
                Advance();
                return new ClockValue(captured);
            }
            if (word is "inrange" or "iswithin")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error($"{token.Text} には引数を3つ指定してください。");
                Advance();
                var arguments = new Node[3];
                for (var index = 0; index < arguments.Length; index++)
                {
                    if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End) throw Error($"{token.Text} の引数が必要です。");
                    arguments[index] = Or(depth + 1);
                    if (index != arguments.Length - 1)
                    {
                        if (current.Kind != TokenKind.Comma) throw Error($"{token.Text} にはカンマで区切った引数を3つ指定してください。");
                        Advance();
                    }
                }
                if (current.Kind != TokenKind.RightParen) throw Error($"{token.Text} の引数数または閉じ括弧が不正です。");
                Advance();
                return new InRange(arguments[0], arguments[1], arguments[2], caseSensitive, UpperInclusive: word == "iswithin");
            }
            if (word == "abs")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error("Abs には引数を1つ指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error("Abs には引数を1つ指定してください。");
                var argument = Or(depth + 1);
                if (current.Kind != TokenKind.RightParen)
                    throw Error(current.Kind == TokenKind.Comma ? "Abs に指定できる引数は1つです。" : "Abs の閉じ括弧がありません。");
                Advance();
                return new Abs(argument);
            }
            if (word is "toint" or "todouble" or "tonumber")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error($"{token.Text} には引数を1つ指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error($"{token.Text} には引数を1つ指定してください。");
                var argument = Or(depth + 1);
                if (current.Kind != TokenKind.RightParen)
                    throw Error(current.Kind == TokenKind.Comma ? $"{token.Text} に指定できる引数は1つです。" : $"{token.Text} の閉じ括弧がありません。");
                Advance();
                return new NumericConversion(argument, word);
            }
            if (word is "substr" or "strcount" or "strfind")
            {
                var maximum = word is "substr" or "strfind" ? 3 : 2;
                if (current.Kind != TokenKind.LeftParen) throw Error($"{token.Text} には引数を2〜{maximum}個指定してください。");
                Advance();
                var arguments = new List<Node>();
                while (true)
                {
                    if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                        throw Error($"{token.Text} の引数が不足しています。");
                    arguments.Add(Or(depth + 1));
                    if (current.Kind != TokenKind.Comma) break;
                    if (arguments.Count == maximum) throw Error($"{token.Text} の引数が多すぎます。");
                    Advance();
                }
                if (arguments.Count < 2) throw Error($"{token.Text} には引数を2つ以上指定してください。");
                if (current.Kind != TokenKind.RightParen) throw Error($"{token.Text} の閉じ括弧がありません。");
                Advance();
                return word == "substr" ? new Substr(arguments[0], arguments[1], arguments.Count == 3 ? arguments[2] : null)
                    : new StringSearch(arguments[0], arguments[1], arguments.Count == 3 ? arguments[2] : null, word == "strfind", caseSensitive);
            }
            if (word == "normalizeunicode")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error("NormalizeUnicode には引数を1〜2個指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End) throw Error("NormalizeUnicode の値がありません。");
                var argument = Or(depth + 1);
                Node? form = null;
                if (current.Kind == TokenKind.Comma)
                {
                    Advance();
                    if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End) throw Error("NormalizeUnicode の形式がありません。");
                    form = Or(depth + 1);
                }
                if (current.Kind != TokenKind.RightParen) throw Error("NormalizeUnicode の引数が多すぎるか閉じ括弧がありません。");
                Advance();
                return new NormalizeUnicode(argument, form);
            }
            if (word is "todatetime" or "todatestr" or "startofweek" or "startofmonth" or "startofyear" or "trim" or "trimleft" or "trimright" or "tolower" or "toupper"
                or "tohalfwidth" or "tofullwidth" or "tohiragana" or "tokatakana" or "tosimplifiedchinese" or "totraditionalchinese")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error($"{token.Text} には引数を1つ指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error($"{token.Text} には引数を1つ指定してください。");
                var argument = Or(depth + 1);
                if (current.Kind != TokenKind.RightParen)
                    throw Error(current.Kind == TokenKind.Comma ? $"{token.Text} に指定できる引数は1つです。" : $"{token.Text} の閉じ括弧がありません。");
                Advance();
                return word switch
                {
                    "todatetime" => new ToDateTime(argument),
                    "todatestr" => new ToDateStr(argument),
                    "startofweek" or "startofmonth" or "startofyear" => new StartOf(argument, word),
                    "tolower" or "toupper" => new ChangeCase(argument, word == "tolower"),
                    "tohalfwidth" => new CharacterMapping(argument, LineTransformCharacterMapping.HalfWidth),
                    "tofullwidth" => new CharacterMapping(argument, LineTransformCharacterMapping.FullWidth),
                    "tohiragana" => new CharacterMapping(argument, LineTransformCharacterMapping.Hiragana),
                    "tokatakana" => new CharacterMapping(argument, LineTransformCharacterMapping.Katakana),
                    "tosimplifiedchinese" => new CharacterMapping(argument, LineTransformCharacterMapping.SimplifiedChinese),
                    "totraditionalchinese" => new CharacterMapping(argument, LineTransformCharacterMapping.TraditionalChinese),
                    _ => new TrimValue(argument, word != "trimright", word != "trimleft")
                };
            }
            if (word == "strlen")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error("Strlen には引数を1つ指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error("Strlen には引数を1つ指定してください。");
                var argument = Or(depth + 1);
                if (current.Kind != TokenKind.RightParen)
                    throw Error(current.Kind == TokenKind.Comma ? "Strlen に指定できる引数は1つです。" : "Strlen の閉じ括弧がありません。");
                Advance();
                return new Strlen(argument);
            }
            if (word == "len")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error("Len には引数を1つ指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error("Len には引数を1つ指定してください。");
                var argument = Or(depth + 1);
                if (current.Kind != TokenKind.RightParen)
                    throw Error(current.Kind == TokenKind.Comma ? "Len に指定できる引数は1つです。" : "Len の閉じ括弧がありません。");
                Advance();
                return new Len(argument);
            }
            if (word == "noteach")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error("NotEach には引数を1つ指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error("NotEach には引数を1つ指定してください。");
                var argument = Or(depth + 1);
                if (current.Kind != TokenKind.RightParen)
                    throw Error(current.Kind == TokenKind.Comma ? "NotEach に指定できる引数は1つです。" : "NotEach の閉じ括弧がありません。");
                Advance();
                return new NotEach(argument);
            }
            if (word == "anyof")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error("AnyOf には引数を1つ指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error("AnyOf には引数を1つ指定してください。");
                var argument = Or(depth + 1);
                if (current.Kind != TokenKind.RightParen)
                    throw Error(current.Kind == TokenKind.Comma ? "AnyOf に指定できる引数は1つです。" : "AnyOf の閉じ括弧がありません。");
                Advance();
                return new AnyOf(argument);
            }
            if (word == "allof")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error("AllOf には引数を1つ指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error("AllOf には引数を1つ指定してください。");
                var argument = Or(depth + 1);
                if (current.Kind != TokenKind.RightParen)
                    throw Error(current.Kind == TokenKind.Comma ? "AllOf に指定できる引数は1つです。" : "AllOf の閉じ括弧がありません。");
                Advance();
                return new AllOf(argument);
            }
            if (word == "at")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error("At には引数を2つ指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error("At の第1引数が必要です。");
                var array = Or(depth + 1);
                if (current.Kind != TokenKind.Comma) throw Error("At にはカンマで区切った引数を2つ指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error("At の第2引数が必要です。");
                var index = Or(depth + 1);
                if (current.Kind != TokenKind.RightParen)
                    throw Error(current.Kind == TokenKind.Comma ? "At に指定できる引数は2つです。" : "At の閉じ括弧がありません。");
                Advance();
                return new At(array, index);
            }
            if (word is "andeach" or "oreach")
            {
                var name = word == "andeach" ? "AndEach" : "OrEach";
                if (current.Kind != TokenKind.LeftParen) throw Error($"{name} には引数を2つ指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error($"{name} の第1引数が必要です。");
                var left = Or(depth + 1);
                if (current.Kind != TokenKind.Comma) throw Error($"{name} にはカンマで区切った引数を2つ指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error($"{name} の第2引数が必要です。");
                var right = Or(depth + 1);
                if (current.Kind != TokenKind.RightParen)
                    throw Error(current.Kind == TokenKind.Comma ? $"{name} に指定できる引数は2つです。" : $"{name} の閉じ括弧がありません。");
                Advance();
                return new LogicalEach(left, right, word == "andeach");
            }
            if (word is "choose" or "chooseeach")
            {
                var name = word == "choose" ? "Choose" : "ChooseEach";
                if (current.Kind != TokenKind.LeftParen) throw Error($"{name} には引数を2つ以上指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error($"{name} のindexが必要です。");
                var index = Or(depth + 1);
                var choices = new List<Node>();
                while (current.Kind == TokenKind.Comma)
                {
                    Advance();
                    if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                        throw Error($"{name} のカンマの後に選択肢が必要です。");
                    choices.Add(Or(depth + 1));
                }
                if (choices.Count == 0) throw Error($"{name} には選択肢を1つ以上指定してください。");
                if (current.Kind != TokenKind.RightParen) throw Error($"{name} の閉じ括弧がありません。");
                Advance();
                return new Choice(index, choices, word == "chooseeach");
            }
            if (word is "if" or "ifeach")
            {
                var name = word == "if" ? "If" : "IfEach";
                if (current.Kind != TokenKind.LeftParen) throw Error($"{name} には引数を3つ指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error($"{name} の第1引数が必要です。");
                var condition = Or(depth + 1);
                if (current.Kind != TokenKind.Comma) throw Error($"{name} にはカンマで区切った引数を3つ指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error($"{name} の第2引数が必要です。");
                var whenTrue = Or(depth + 1);
                if (current.Kind != TokenKind.Comma) throw Error($"{name} にはカンマで区切った引数を3つ指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error($"{name} の第3引数が必要です。");
                var whenFalse = Or(depth + 1);
                if (current.Kind != TokenKind.RightParen)
                    throw Error(current.Kind == TokenKind.Comma ? $"{name} に指定できる引数は3つです。" : $"{name} の閉じ括弧がありません。");
                Advance();
                return word == "if" ? new Conditional(condition, whenTrue, whenFalse) : new ConditionalEach(condition, whenTrue, whenFalse);
            }
            if (word == "array")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error("Array には括弧を指定してください。");
                Advance();
                var arguments = new List<Node>();
                if (current.Kind != TokenKind.RightParen)
                {
                    if (current.Kind is TokenKind.Comma or TokenKind.End)
                        throw Error("Array の引数が必要です。");
                    arguments.Add(Or(depth + 1));
                    while (current.Kind == TokenKind.Comma)
                    {
                        Advance();
                        if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                            throw Error("Array のカンマの後に引数が必要です。");
                        arguments.Add(Or(depth + 1));
                    }
                    if (current.Kind != TokenKind.RightParen)
                        throw Error("Array の閉じ括弧がありません。");
                }
                Advance();
                return new ArrayValue(arguments);
            }
            if (word == "allequal")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error("AllEqual には引数を1つ以上指定してください。");
                Advance();
                if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                    throw Error("AllEqual には引数を1つ以上指定してください。");
                var arguments = new List<Node> { Or(depth + 1) };
                while (current.Kind == TokenKind.Comma)
                {
                    Advance();
                    if (current.Kind is TokenKind.RightParen or TokenKind.Comma or TokenKind.End)
                        throw Error("AllEqual のカンマの後に引数が必要です。");
                    arguments.Add(Or(depth + 1));
                }
                if (current.Kind != TokenKind.RightParen) throw Error("AllEqual の閉じ括弧がありません。");
                Advance();
                return new AllEqual(arguments);
            }
            if (word is "prop" or "leftprop" or "middleprop" or "rightprop")
            {
                if (current.Kind != TokenKind.LeftParen) throw Error("Prop には文字列リテラル引数を1つ指定してください。");
                Advance();
                var argument = Or(depth + 1);
                if (argument is not Literal { Value.Kind: ValueKind.String } literal)
                    throw Error("Prop には文字列リテラル引数を1つ指定してください。");
                var propertyName = StrictUtf8.GetString(literal.Value.Text!);
                if (current.Kind != TokenKind.RightParen) throw Error("Prop には文字列リテラル引数を1つ指定してください。");
                Advance();
                propertyNames.Add(propertyName);
                return new ShellProperty(propertyName, word switch { "leftprop" => 0, "middleprop" => 1, "rightprop" => -1, _ => -2 });
            }
            var (side, prefixLength) = word.StartsWith("left", StringComparison.Ordinal) ? (0, 4)
                : word.StartsWith("middle", StringComparison.Ordinal) ? (1, 6)
                : word.StartsWith("right", StringComparison.Ordinal) ? (-1, 5) : (0, 0);
            if (prefixLength != 0)
            {
                var attribute = word[prefixLength..];
                var type = attribute switch
                {
                    "exists" or "fileexists" or "missing" or "filemissing" or "isfolder" => ValueKind.Boolean,
                    "name" or "basename" or "extension" or "folder" or "fullpath" or "relpath" or "attrstr" or "datestr" => ValueKind.String,
                    "size" or "attributes" => ValueKind.Number,
                    "date" => ValueKind.Date,
                    _ => throw Error($"未対応の属性 '{token.Text}' です。")
                };
                return new Property(attribute, type, side);
            }
            return word switch
            {
                "true" => new Literal(Value.Bool(true)), "false" => new Literal(Value.Bool(false)),
                "null" or "none" or "undefined" => new Literal(new(ValueKind.Null)),
                "path" or "relpath" or "name" or "basename" or "extension" or "folder" or "fullpath" or "datestr" or "attrstr" => new Property(word, ValueKind.String),
                "size" or "attributes" => new Property(word, ValueKind.Number),
                "date" or "modified" => new Property(word, ValueKind.Date),
                "isdirectory" or "isfolder" or "exists" or "fileexists" or "missing" or "filemissing" => new Property(word, ValueKind.Boolean),
                _ => throw Error($"未対応の属性 '{token.Text}' です。")
            };
        }
        private static long? NativeSizeBytes(string text, int suffix)
        {
            var multiplications = text[suffix..].ToLowerInvariant() switch
            {
                "b" => 0, "kb" => 1, "mb" => 2, "gb" => 3, "tb" => 4, _ => -1
            };
            if (multiplications < 0 || !double.TryParse(text[..suffix], NumberStyles.Float, CultureInfo.InvariantCulture, out var bytes)) return null;
            for (var index = 0; index < multiplications; index++) bytes *= 1024d;
            const double int64Limit = 9_223_372_036_854_775_808d;
            if (!double.IsFinite(bytes) || bytes < -int64Limit || bytes >= int64Limit) return null;
            return (long)bytes;
        }
        private static decimal Number(string text)
        {
            var suffix = text.Length;
            while (suffix > 0 && char.IsLetter(text[suffix - 1])) suffix--;
            var number = decimal.Parse(text[..suffix], NumberStyles.Float, CultureInfo.InvariantCulture);
            var multiplier = text[suffix..].ToLowerInvariant() switch { "" or "b" => 1m, "kb" or "kib" => 1024m, "mb" or "mib" => 1048576m, "gb" or "gib" => 1073741824m, "tb" or "tib" => 1099511627776m, _ => throw new FormatException("未対応の数値単位です。") };
            return checked(number * multiplier);
        }
        private bool Take(string value)
        {
            if (current.Kind is not (TokenKind.Word or TokenKind.Operator) || !current.Text.Equals(value, StringComparison.OrdinalIgnoreCase)) return false;
            Advance();
            return true;
        }
        private FormatException Error(string message) => new($"式の {current.Position + 1} 文字目: {message}");
        private void Advance()
        {
            optimizationContext.CancellationToken.ThrowIfCancellationRequested();
            if (++tokenCount > 512) throw Error("式のトークンは 512 個以下にしてください。");
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            var start = cursor;
            if (cursor == text.Length) { current = new(TokenKind.End, "", cursor); return; }
            var character = text[cursor++];
            if (character is '(' or ')') { current = new(character == '(' ? TokenKind.LeftParen : TokenKind.RightParen, character.ToString(), start); return; }
            if (character == ',') { current = new(TokenKind.Comma, ",", start); return; }
            var date = character is 'd' or 'D' && cursor < text.Length && text[cursor] is '"' or '\'';
            var version = character is 'v' or 'V' && cursor < text.Length && text[cursor] is '"' or '\'';
            if (date || version) character = text[cursor++];
            if (character is '"' or '\'')
            {
                var value = new StringBuilder();
                var closed = false;
                while (cursor < text.Length)
                {
                    var next = text[cursor++];
                    if (next == character)
                    {
                        if (character == '"' && cursor < text.Length && text[cursor] == character)
                        {
                            value.Append(character); cursor++; continue;
                        }
                        closed = true; break;
                    }
                    if (character == '\'' && next == '\\' && cursor < text.Length)
                    {
                        var escaped = text[cursor++];
                        if (escaped == character || escaped == '\\') value.Append(escaped);
                        else if (escaped is 'n' or 'r' or 't') value.Append(escaped == 'n' ? '\n' : escaped == 'r' ? '\r' : '\t');
                        else value.Append('\\').Append(escaped);
                    }
                    else value.Append(next);
                }
                if (!closed) throw new FormatException($"式の {start + 1} 文字目: 引用符が閉じられていません。");
                current = new(date ? TokenKind.Date : version ? TokenKind.Version : TokenKind.String, value.ToString(), start);
                return;
            }
            if (character == '@' || char.IsLetter(character) || character == '_')
            {
                while (cursor < text.Length && (char.IsLetterOrDigit(text[cursor]) || text[cursor] == '_')) cursor++;
                current = new(character == '@' ? TokenKind.Directive : TokenKind.Word, text[(start + (character == '@' ? 1 : 0))..cursor], start);
                return;
            }
            if (char.IsDigit(character) || character == '.')
            {
                while (cursor < text.Length && (char.IsLetterOrDigit(text[cursor]) || text[cursor] == '.' ||
                    (text[cursor] is '+' or '-' && text[cursor - 1] is 'e' or 'E'))) cursor++;
                current = new(TokenKind.Number, text[start..cursor], start);
                return;
            }
            if (character is '=' or '!' or '<' or '>' or '&' or '|' or '+' or '-' or '*' or '/' or '%')
            {
                if (cursor < text.Length && (text[cursor] == '=' || text[cursor] == character && character is '&' or '|')) cursor++;
                current = new(TokenKind.Operator, text[start..cursor], start);
                return;
            }
            throw new FormatException($"式の {start + 1} 文字目に未対応の文字があります。");
        }
    }
}
