using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DiffBeacon.Core;

/// <summary>
/// WinMerge の .flt (def/f/d/f!/d!/fe/de/e) と限定したメタデータ式を評価する。
/// 式は and/or/not、比較、contains/like/matches/recontains、サイズ単位、日時に対応する。
/// Content、左右ペイン属性、関数、算術、PCRE 専用構文には対応しない。
/// パス式は '/' 区切り、Extension は先頭 '.' なし、日時は UTC。正規表現は 250 ms で停止する。
/// </summary>
public sealed class FileFilter
{
    private const int MaxExpressionLength = 16_384;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly List<Rule> rules = [];
    private Node? expression;
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
            return Expression!.Evaluate(context).Boolean;
        }
    }

    public static FileFilter Load(string path)
    {
        var absolute = System.IO.Path.GetFullPath(path);
        if (new FileInfo(absolute).Length > 1_048_576) throw new FormatException("フィルターファイルは 1 MiB 以下にしてください。");
        var bytes = File.ReadAllBytes(absolute);
        if (bytes.Length > 1_048_576) throw new FormatException("フィルターファイルは 1 MiB 以下にしてください。");
        var (encoding, preamble) = TextDocument.DetectEncoding(bytes, "windows-1252");
        return Parse(encoding.GetString(bytes, preamble, bytes.Length - preamble), System.IO.Path.GetFileNameWithoutExtension(absolute));
    }

    public static FileFilter Parse(string text, string? name = null)
    {
        if (text.Length > 1_048_576) throw new FormatException("フィルターがサイズ上限を超えています。");
        var filter = new FileFilter { Name = name ?? "Filter" };
        var lineNumber = 0;
        foreach (var raw in TextLines.Parse(text))
        {
            lineNumber++;
            var line = StripComment(raw.Content).Trim();
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
                    var root = new Parser(value).Parse();
                    if (category is "fe" or "e") filter.rules.Add(new(false, exception, null, false, root));
                    if (category is "de" or "e") filter.rules.Add(new(true, exception, null, false, root));
                }
            }
            catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
            { throw new FormatException($"フィルター {lineNumber} 行目: {error.Message}", error); }
        }
        return filter;
    }

    public static FileFilter ParseExpression(string expression) => new() { expression = new Parser(StripComment(expression)).Parse() };

    public bool Matches(string relativePath, bool isDirectory, long size, DateTime modified)
    {
        if (relativePath.Length > 32_768) throw new ArgumentException("フィルターパスが長すぎます。", nameof(relativePath));
        var utcModified = modified.Kind == DateTimeKind.Local ? modified.ToUniversalTime() : DateTime.SpecifyKind(modified, DateTimeKind.Utc);
        var context = new Context(relativePath.Replace('\\', '/').TrimStart('/'), isDirectory, size, utcModified);
        if (expression is not null) return expression.Evaluate(context).Boolean;
        var matched = rules.Any(rule => rule.Directory == isDirectory && !rule.Exception && rule.Matches(context));
        if (matched && rules.Any(rule => rule.Directory == isDirectory && rule.Exception && rule.Matches(context))) matched = false;
        return matched ? !DefaultInclude : DefaultInclude;
    }

    private static Regex MakeRegex(string pattern, bool caseSensitive, bool whole = false)
    {
        if (pattern.Length > 4096) throw new FormatException("正規表現は 4096 文字以下にしてください。");
        return new(whole ? "\\A(?:" + pattern + ")\\z" : pattern,
            RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), RegexTimeout);
    }

    private static string StripComment(string text)
    {
        var quote = '\0';
        for (var index = 0; index < text.Length; index++)
        {
            if (quote != '\0')
            {
                if (text[index] == '\\') index++;
                else if (text[index] == quote) quote = '\0';
            }
            else if (text[index] is '"' or '\'') quote = text[index];
            else if (text[index] == '#' && index + 1 < text.Length && text[index + 1] == '#' &&
                (index == 0 || char.IsWhiteSpace(text[index - 1]))) return text[..index];
        }
        return text;
    }

    private sealed record Context(string Path, bool Directory, long Size, DateTime Modified)
    {
        public string Name => Path[(Path.LastIndexOf('/') + 1)..];
        public string Extension => System.IO.Path.GetExtension(Name).TrimStart('.');
        public string Folder => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "";
    }
    private enum ValueKind { Null, Boolean, Number, String, Date }
    private readonly record struct Value(ValueKind Kind, bool Boolean = false, decimal Number = 0, string? Text = null, DateTime Date = default)
    {
        public static Value Bool(bool value) => new(ValueKind.Boolean, Boolean: value);
        public static Value Str(string value) => new(ValueKind.String, Text: value);
    }
    private abstract record Node(ValueKind Kind) { public abstract Value Evaluate(Context context); }
    private sealed record Literal(Value Value) : Node(Value.Kind) { public override Value Evaluate(Context context) => Value; }
    private sealed record Property(string Name, ValueKind Type) : Node(Type)
    {
        public override Value Evaluate(Context context) => Name switch
        {
            "path" or "relpath" => Value.Str(context.Path),
            "name" => Value.Str(context.Name),
            "basename" => Value.Str(System.IO.Path.GetFileNameWithoutExtension(context.Name)),
            "extension" => Value.Str(context.Extension),
            "folder" => Value.Str(context.Folder),
            "size" => new(ValueKind.Number, Number: context.Size),
            "modified" or "date" => new(ValueKind.Date, Date: context.Modified),
            "datestr" => Value.Str(context.Modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            "isdirectory" or "isfolder" => Value.Bool(context.Directory),
            "exists" => Value.Bool(true),
            _ => throw new InvalidOperationException("未対応の属性です。")
        };
    }
    private sealed record Logical(string Operator, Node Left, Node? Right = null) : Node(ValueKind.Boolean)
    {
        public override Value Evaluate(Context context) => Value.Bool(Operator switch
        {
            "not" => !Left.Evaluate(context).Boolean,
            "and" => Left.Evaluate(context).Boolean && Right!.Evaluate(context).Boolean,
            "or" => Left.Evaluate(context).Boolean || Right!.Evaluate(context).Boolean,
            _ => false
        });
    }
    private sealed record Comparison(string Operator, Node Left, Node Right, bool CaseSensitive, Regex? Pattern = null) : Node(ValueKind.Boolean)
    {
        public override Value Evaluate(Context context)
        {
            var a = Left.Evaluate(context);
            var b = Right.Evaluate(context);
            if (a.Kind == ValueKind.Null || b.Kind == ValueKind.Null)
                return Value.Bool(Operator == "!=" ? a.Kind != b.Kind : Operator is "=" or "==" && a.Kind == b.Kind);
            if (Operator == "contains") return Value.Bool(a.Text!.Contains(b.Text!, CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
            if (Operator == "like") return Value.Bool(Glob(a.Text!, b.Text!, CaseSensitive));
            if (Pattern is not null) return Value.Bool(Pattern.IsMatch(a.Text!));
            var order = a.Kind switch
            {
                ValueKind.Boolean => a.Boolean.CompareTo(b.Boolean),
                ValueKind.Number => a.Number.CompareTo(b.Number),
                ValueKind.Date => a.Date.CompareTo(b.Date),
                ValueKind.String => (CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase).Compare(a.Text, b.Text),
                _ => 0
            };
            return Value.Bool(Operator switch { "=" or "==" => order == 0, "!=" => order != 0, "<" => order < 0, "<=" => order <= 0, ">" => order > 0, ">=" => order >= 0, _ => false });
        }
        private static bool Glob(string value, string pattern, bool caseSensitive)
        {
            var p = 0;
            var s = 0;
            var star = -1;
            var retry = 0;
            while (s < value.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || (caseSensitive ? pattern[p] == value[s] : char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(value[s])))) { p++; s++; }
                else if (p < pattern.Length && pattern[p] == '*') { star = p++; retry = s; }
                else if (star >= 0) { p = star + 1; s = ++retry; }
                else return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }
    }

    private enum TokenKind { End, Word, String, Number, Date, Operator, LeftParen, RightParen, Directive }
    private readonly record struct Token(TokenKind Kind, string Text, int Position);
    private sealed class Parser
    {
        private readonly string text;
        private int cursor;
        private int tokenCount;
        private Token current;
        private bool caseSensitive;
        public Parser(string text)
        {
            if (text.Length > MaxExpressionLength) throw new FormatException("式は 16,384 文字以下にしてください。");
            this.text = text;
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
                    case "opt": case "optimize": case "noopt": case "nooptimize": break;
                    case "name":
                        Advance();
                        if (!Take("=")) throw Error("@name の後に '=' が必要です。");
                        if (current.Kind is not (TokenKind.Word or TokenKind.String)) throw Error("@name に名前を指定してください。");
                        Advance();
                        continue;
                    default: throw Error("未対応のディレクティブです。");
                }
                Advance();
            }
            var root = Or(0);
            if (current.Kind != TokenKind.End) throw Error("未対応の構文または余分な文字があります。");
            RequireBoolean(root);
            return root;
        }
        private Node Or(int depth)
        {
            var result = And(depth);
            while (Take("or") || Take("||")) { RequireBoolean(result); var right = And(depth); RequireBoolean(right); result = new Logical("or", result, right); }
            return result;
        }
        private Node And(int depth)
        {
            var result = Not(depth);
            while (Take("and") || Take("&&")) { RequireBoolean(result); var right = Not(depth); RequireBoolean(right); result = new Logical("and", result, right); }
            return result;
        }
        private Node Not(int depth)
        {
            if (depth > 64) throw Error("式の入れ子は 64 以下にしてください。");
            if (Take("not") || Take("!")) { var operand = Not(depth + 1); RequireBoolean(operand); return new Logical("not", operand); }
            var left = Term(depth);
            var negate = Take("not");
            var op = current.Text.ToLowerInvariant();
            if (current.Kind is not (TokenKind.Word or TokenKind.Operator) ||
                op is not ("=" or "==" or "!=" or "<" or "<=" or ">" or ">=" or "contains" or "matches" or "recontains" or "like"))
            {
                if (negate) throw Error("not の後に文字列比較演算子が必要です。");
                return left;
            }
            Advance();
            var right = Term(depth);
            if (negate && op is not ("contains" or "matches" or "recontains" or "like")) throw Error("未対応の not 比較です。");
            if (left.Kind != right.Kind && left.Kind != ValueKind.Null && right.Kind != ValueKind.Null) throw Error("比較する値の型が異なります。");
            Regex? regex = null;
            if (op is "contains" or "matches" or "recontains" or "like")
            {
                if (left.Kind != ValueKind.String || right.Kind != ValueKind.String) throw Error("文字列値が必要です。");
                if (op is "matches" or "recontains")
                {
                    if (right is not Literal literal) throw Error("正規表現には文字列リテラルを指定してください。");
                    var pattern = literal.Value.Text!;
                    regex = MakeRegex(pattern, caseSensitive, op == "matches");
                }
            }
            Node comparison = new Comparison(op, left, right, caseSensitive, regex);
            return negate ? new Logical("not", comparison) : comparison;
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
                if (!DateTime.TryParse(token.Text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) throw Error("日時リテラルが不正です。");
                return new Literal(new(ValueKind.Date, Date: date));
            }
            if (token.Kind == TokenKind.Number) return new Literal(new(ValueKind.Number, Number: Number(token.Text)));
            if (token.Kind != TokenKind.Word) throw Error("値が必要です。");
            var word = token.Text.ToLowerInvariant();
            return word switch
            {
                "true" => new Literal(Value.Bool(true)), "false" => new Literal(Value.Bool(false)),
                "null" or "none" or "undefined" => new Literal(new(ValueKind.Null)),
                "path" or "relpath" or "name" or "basename" or "extension" or "folder" or "datestr" => new Property(word, ValueKind.String),
                "size" => new Property(word, ValueKind.Number),
                "date" or "modified" => new Property(word, ValueKind.Date),
                "isdirectory" or "isfolder" or "exists" => new Property(word, ValueKind.Boolean),
                _ => throw Error($"未対応の属性 '{token.Text}' です。")
            };
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
        private void RequireBoolean(Node node) { if (node.Kind != ValueKind.Boolean) throw Error("論理式には boolean 値が必要です。"); }
        private FormatException Error(string message) => new($"式の {current.Position + 1} 文字目: {message}");
        private void Advance()
        {
            if (++tokenCount > 512) throw Error("式のトークンは 512 個以下にしてください。");
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            var start = cursor;
            if (cursor == text.Length) { current = new(TokenKind.End, "", cursor); return; }
            var character = text[cursor++];
            if (character is '(' or ')') { current = new(character == '(' ? TokenKind.LeftParen : TokenKind.RightParen, character.ToString(), start); return; }
            var date = character is 'd' or 'D' && cursor < text.Length && text[cursor] is '"' or '\'';
            if (date) character = text[cursor++];
            if (character is '"' or '\'')
            {
                var value = new StringBuilder();
                var closed = false;
                while (cursor < text.Length)
                {
                    var next = text[cursor++];
                    if (next == character) { closed = true; break; }
                    if (next == '\\' && cursor < text.Length)
                    {
                        var escaped = text[cursor++];
                        if (escaped == character || escaped == '\\') value.Append(escaped);
                        else if (escaped is 'n' or 'r' or 't') value.Append(escaped == 'n' ? '\n' : escaped == 'r' ? '\r' : '\t');
                        else value.Append('\\').Append(escaped);
                    }
                    else value.Append(next);
                }
                if (!closed) throw new FormatException($"式の {start + 1} 文字目: 引用符が閉じられていません。");
                current = new(date ? TokenKind.Date : TokenKind.String, value.ToString(), start);
                return;
            }
            if (character == '@' || char.IsLetter(character) || character == '_')
            {
                while (cursor < text.Length && (char.IsLetterOrDigit(text[cursor]) || text[cursor] == '_')) cursor++;
                current = new(character == '@' ? TokenKind.Directive : TokenKind.Word, text[(start + (character == '@' ? 1 : 0))..cursor], start);
                return;
            }
            if (char.IsDigit(character) || character is '.' or '-' or '+')
            {
                while (cursor < text.Length && (char.IsLetterOrDigit(text[cursor]) || text[cursor] == '.' ||
                    (text[cursor] is '+' or '-' && text[cursor - 1] is 'e' or 'E'))) cursor++;
                current = new(TokenKind.Number, text[start..cursor], start);
                return;
            }
            if (character is '=' or '!' or '<' or '>' or '&' or '|')
            {
                if (cursor < text.Length && (text[cursor] == '=' || text[cursor] == character && character is '&' or '|')) cursor++;
                current = new(TokenKind.Operator, text[start..cursor], start);
                return;
            }
            throw new FormatException($"式の {start + 1} 文字目に未対応の文字があります。");
        }
    }
}
