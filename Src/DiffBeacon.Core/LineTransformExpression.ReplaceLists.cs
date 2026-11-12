using System.Text;

namespace DiffBeacon.Core;

internal sealed partial class LineTransformExpression
{
    private sealed partial class Function
    {
        private object? EvaluateReplaceList(object? source, object? list, Context c)
        {
            // 原典はsourceとlistを順に評価してから型を検査する。
            if (list is string path) list = LoadReplaceList(path, c);
            if (list is not ValueArray rules) return null;
            return ApplyToScalarOrArray(source, value =>
            {
                if (value is null) return null;
                var text = StringValue(value, c.Budget, c.Token);
                for (var index = 0; index < rules.Count; index++)
                {
                    c.Budget.Charge(1, c.Token);
                    if (rules[index] is not ValueArray { Count: 2 } pair || pair[1] is not string replacement) continue;
                    var pattern = pair[0] as RegexPattern;
                    if (pattern is null && name == "regexreplacewithlist" && pair[0] is string expression)
                        pattern = PrepareRegex(expression, c);
                    if (pattern is null) continue;
                    // regexSubstのfalseを旧list関数は無視する。失敗ruleは本文を保持して後続へ進む。
                    text = ReplaceRegex(text, replacement, pattern, c, preservePartialOnFailure: name == "regexreplacewithlist")
                        ?? throw new ArgumentException("置換リストの空幅patternが不正なUTF-8位置へ進みました。");
                }
                return text;
            }, c);
        }

        private ValueArray LoadReplaceList(string path, Context c)
        {
            if (listEnvironment is null) throw new ArgumentException("置換リストのファイルには明示的な読込み環境が必要です。");
            var text = listEnvironment.ReadList(path, c.Budget, c.Token);
            var pairs = new List<object?>();
            var lineNumber = 0;
            for (var start = 0; start <= text.Length;)
            {
                c.Budget.Charge(1, c.Token);
                var end = start;
                while (end < text.Length && text[end] is not ('\r' or '\n')) { c.Budget.Charge(1, c.Token); end++; }
                var line = text.AsSpan(start, end - start);
                lineNumber++;
                var tab = line.IndexOf('\t');
                if (line.Length > 0 && line[0] != '#' && tab >= 0)
                {
                    var replacement = line[(tab + 1)..];
                    var second = replacement.IndexOf('\t');
                    if (second >= 0) replacement = replacement[..second];
                    c.Budget.Charge(line.Length * 3L + 1, c.Token, line.Length * 3L);
                    var from = line[..tab].ToString();
                    var expression = from;
                    if (name == "replacewithlist")
                    {
                        var escaped = new StringBuilder();
                        foreach (var ch in from)
                        { c.Budget.Charge(1, c.Token); if (".^$*+?{}[]\\|()".Contains(ch)) escaped.Append('\\'); escaped.Append(ch); }
                        expression = escaped.ToString();
                    }
                    var pattern = PrepareRegex(expression, c);
                    if (pattern is null)
                        logEnvironment?.Write(LineTransformLogLevel.Error, $"Invalid {(name == "regexreplacewithlist" ? "regular expression" : "pattern")} at line {lineNumber} in file: {path}, pattern: {from}");
                    else
                    {
                        if (pairs.Count >= MaximumArrayElements) throw new InvalidOperationException("置換リストのルール数が上限を超えています。");
                        var to = replacement.ToString();
                        pairs.Add(CreateArray(2, index => index == 0 ? pattern : to, c));
                    }
                }
                if (end == text.Length) break;
                start = end + 1;
                if (text[end] == '\r' && start < text.Length && text[start] == '\n') start++;
            }
            return CreateArray(pairs.Count, index => pairs[index], c);
        }
    }
}
