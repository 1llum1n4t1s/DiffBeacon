using System.Text.Json;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class GnuLineCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is not (2 or 4) || (args.Length == 4 && args[2] != "--max-work"))
            throw new ArgumentException("--gnu-line-script INPUT_JSON [--max-work N] を指定してください。");
        var budget = 4_000_000;
        if (args.Length == 4 && (!int.TryParse(args[3], out budget) || budget < 0 || budget > 8_000_000))
            throw new ArgumentException("作業予算は0以上8,000,000以下です。");
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        var token = cancel.Token;
        var info = new FileInfo(args[1]);
        if (info.Length > 64 * 1024 * 1024) throw new ArgumentException("同値クラスJSONは64 MiBまでです。");
        await using var stream = File.OpenRead(args[1]);
        using var content = new MemoryStream();
        var buffer = new byte[65_536];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (content.Length + read > 64 * 1024 * 1024) throw new ArgumentException("同値クラスJSONは64 MiBまでです。");
            content.Write(buffer.AsSpan(0, read));
        }
        content.Position = 0;
        using var input = await JsonDocument.ParseAsync(content, new() { MaxDepth = 16 }, token);
        var root = input.RootElement;
        var left = Read("left");
        var right = Read("right");
        var result = GnuLineScript.Compare(left, right, root.GetProperty("classCount").GetInt32(), budget, token);
        using var output = new Utf8JsonWriter(Console.OpenStandardOutput());
        output.WriteStartObject();
        output.WriteBoolean("fallback", result.Fallback);
        output.WriteNumber("workUsed", result.WorkUsed);
        output.WriteString("fallbackReason", result.FallbackReason);
        output.WriteStartArray("changes");
        foreach (var change in result.Changes)
        {
            token.ThrowIfCancellationRequested();
            output.WriteStartObject();
            output.WriteNumber("line0", change.LeftStart); output.WriteNumber("line1", change.RightStart);
            output.WriteNumber("deleted", change.LeftCount); output.WriteNumber("inserted", change.RightCount);
            output.WriteEndObject();
        }
        output.WriteEndArray(); output.WriteEndObject(); output.Flush();
        return 0;

        int[] Read(string name)
        {
            var elements = root.GetProperty(name);
            if (elements.GetArrayLength() > 262_144) throw new ArgumentException("同値クラスは片側262,144行までです。");
            var values = new int[elements.GetArrayLength()];
            for (var index = 0; index < values.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                values[index] = elements[index].GetInt32();
            }
            return values;
        }
    }
}
