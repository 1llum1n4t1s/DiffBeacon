using System.Globalization;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class WordDiffCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("--word-diff LEFT RIGHT と比較オプションが必要です。");
        var options = new WordDiffOptions();
        var maxWork = 4_000_000;
        for (var index = 3; index < args.Length; index++)
        {
            string Value()
            {
                if (++index >= args.Length) throw new ArgumentException("オプションの値が不足しています。");
                return args[index];
            }
            switch (args[index])
            {
                case "--word-level": options = options with { CharacterLevel = false }; break;
                case "--ignore-case": options = options with { MatchCase = false }; break;
                case "--ignore-numbers": options = options with { IgnoreNumbers = true }; break;
                case "--no-separators": options = options with { BreakOnSeparators = false }; break;
                case "--separators": options = options with { Separators = Value() }; break;
                case "--whitespace": options = options with { Whitespace = Value() switch
                {
                    "none" => WordWhitespaceMode.CompareAll, "changes" => WordWhitespaceMode.IgnoreChanges,
                    "all" => WordWhitespaceMode.IgnoreAll, _ => throw new ArgumentException("空白モードはnone、changes、allです。")
                } }; break;
                case "--eol": options = options with { Eol = Value() switch
                {
                    "strict" => WordEolMode.Strict, "ignore" => WordEolMode.Ignore,
                    "space" => WordEolMode.AsSpace, _ => throw new ArgumentException("改行モードはstrict、ignore、spaceです。")
                } }; break;
                case "--max-work":
                    if (!int.TryParse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out maxWork) || maxWork is < 0 or > 8_000_000)
                        throw new ArgumentException("比較予算は0から8000000です。");
                    break;
                default: throw new ArgumentException($"不明なWordDiffオプション: {args[index]}");
            }
        }
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var left = await TextDocument.LoadAsync(args[1], cancel.Token);
            var right = await TextDocument.LoadAsync(args[2], cancel.Token);
            var result = WordDiffer.Compare(left.Text, right.Text, options, maxWork, cancel.Token);
            var different = result.Differences.Any(range => range.Left.Length > 0 || range.Right.Length > 0);
            CommandLine.WriteJson(writer =>
            {
                writer.WriteBoolean("different", different);
                writer.WriteBoolean("fallback", result.Fallback);
                writer.WriteString("fallbackReason", result.FallbackReason);
                writer.WriteNumber("workUsed", result.WorkUsed);
                writer.WriteString("leftEncoding", left.EncodingName);
                writer.WriteString("rightEncoding", right.EncodingName);
                writer.WriteStartArray("ranges");
                foreach (var range in result.Differences)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(range.Left.Start); writer.WriteNumberValue(range.Left.Length);
                    writer.WriteNumberValue(range.Right.Start); writer.WriteNumberValue(range.Right.Length);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            });
            return different ? 1 : 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
