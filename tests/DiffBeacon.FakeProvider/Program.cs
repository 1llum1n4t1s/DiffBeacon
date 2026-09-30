using System.Text;
using System.Text.Json;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
try
{
    using var request = JsonDocument.Parse(await Console.In.ReadToEndAsync());
    var root = request.RootElement;
    if (root.GetProperty("protocolVersion").GetInt32() != 1) return 3;
    var format = root.GetProperty("format").GetString();
    var left = await File.ReadAllTextAsync(root.GetProperty("leftPath").GetString()!);
    var right = await File.ReadAllTextAsync(root.GetProperty("rightPath").GetString()!);
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer))
    {
        writer.WriteStartObject(); writer.WriteNumber("protocolVersion", format == "invalid" ? 99 : 1);
        // 上限検証では、JSON 自体を壊さず有効な応答のサイズだけを超過させる。
        writer.WriteString("summary", format == "flood" ? new string('x', 5 * 1024 * 1024) : "E2E external provider fixture");
        writer.WriteString("leftText", left); writer.WriteString("rightText", right);
        writer.WriteEndObject();
    }
    await Console.Out.WriteAsync(Encoding.UTF8.GetString(buffer.ToArray()));
    return 0;
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return 3;
}
