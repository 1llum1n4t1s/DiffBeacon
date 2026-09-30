using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public sealed record ComparisonProject
{
    public string LeftPath { get; init; } = "";
    public string BasePath { get; init; } = "";
    public string RightPath { get; init; } = "";
    public string Mode { get; init; } = "Text";
    public string? ProviderId { get; init; }
    public string? FileFilterPath { get; init; }
    public bool IgnoreCase { get; init; }
    public bool IgnoreWhitespace { get; init; }
    public bool IgnoreBlankLines { get; init; }
    public string? IgnoreLinePattern { get; init; }
    public bool IgnoreNumbers { get; init; }
    public CommentSyntax CommentSyntax { get; init; }
    public WhitespaceMode Whitespace { get; init; }
    public SubstitutionRule[] SubstitutionRules { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ComparisonProject))]
internal partial class ProjectJsonContext : JsonSerializerContext { }

public static class WorkspaceStore
{
    public static async Task SaveAsync(string path, ComparisonProject project, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, ".diffbeacon-project-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, project, ProjectJsonContext.Default.ComparisonProject, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    public static async Task<ComparisonProject> LoadAsync(string path, CancellationToken token = default)
    {
        if (Path.GetExtension(path).Equals(".WinMerge", StringComparison.OrdinalIgnoreCase))
            return await ImportLegacyAsync(path, token);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync(stream, ProjectJsonContext.Default.ComparisonProject, token)
            ?? throw new InvalidDataException("比較プロジェクトが空です。");
    }

    public static async Task<ComparisonProject> ImportLegacyAsync(string path, CancellationToken token = default)
    {
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 };
        using var reader = XmlReader.Create(path, settings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, token);
        if (document.Root?.Name != "project") throw new InvalidDataException("WinMerge プロジェクトのルート要素が不正です。");
        var entries = document.Root.Elements("paths").ToArray();
        if (entries.Length != 1) throw new InvalidDataException("一組の paths を含む WinMerge プロジェクトだけを読み込めます。");
        var entry = entries[0];
        string Value(string name) => entry.Element(name)?.Value ?? "";
        bool Flag(string name) => Value(name).Trim() is "1" or "true";
        var ignoreComments = entry.Element("ignore-comment-diff") is not null ? Flag("ignore-comment-diff") : Flag("ignore-comments");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string Resolve(string name)
        {
            var value = Value(name);
            if (string.IsNullOrWhiteSpace(value)) return "";
            // 別 OS の絶対パスは書き換えず、利用者が移行先で選び直せるよう保持する。
            if (Path.IsPathRooted(value) || (value.Length > 1 && value[1] == ':') || value.StartsWith("\\\\", StringComparison.Ordinal)) return value;
            return Path.GetFullPath(Path.Combine(directory, value.Replace('\\', Path.DirectorySeparatorChar)));
        }
        return new ComparisonProject
        {
            LeftPath = Resolve("left"), BasePath = Resolve("middle"), RightPath = Resolve("right"),
            Mode = Value("window-type").Trim() switch { "2" => "Table", "3" => "Binary", "4" => "Image", "5" => "Web", "6" => "Folder", _ => "Text" },
            IgnoreCase = Flag("ignore-case"), IgnoreWhitespace = Value("white-spaces").Trim() == "2",
            Whitespace = Value("white-spaces").Trim() switch { "1" => WhitespaceMode.IgnoreChanges, "2" => WhitespaceMode.IgnoreAll, _ => WhitespaceMode.None },
            IgnoreNumbers = Flag("ignore-numbers"),
            CommentSyntax = ignoreComments ? InferCommentSyntax(Value("left"), Value("middle"), Value("right")) : CommentSyntax.None,
            IgnoreBlankLines = Flag("ignore-blank-lines")
        };
    }

    private static CommentSyntax InferCommentSyntax(params string[] paths)
    {
        // 旧経路と同様に既知の構文を共有し、未知の形式では本文を除去しない。
        foreach (var path in paths)
        {
            var syntax = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".py" or ".pyw" => CommentSyntax.Python,
                ".cs" => CommentSyntax.CSharp,
                ".xml" or ".xaml" or ".axaml" or ".svg" or ".html" or ".htm" => CommentSyntax.Xml,
                ".c" or ".cc" or ".cpp" or ".cxx" or ".c++" or ".h" or ".hh" or ".hpp" or ".hxx" or ".h++" or ".m" or ".mm" => CommentSyntax.CStyle,
                _ => CommentSyntax.None
            };
            if (syntax != CommentSyntax.None) return syntax;
        }
        return CommentSyntax.None;
    }
}

public static class HtmlReport
{
    public static string Create(DiffResult result, string leftName, string rightName)
    {
        ArgumentNullException.ThrowIfNull(result);
        string Escape(string? value) => WebUtility.HtmlEncode(value ?? "");
        var html = new StringBuilder("<!doctype html><html lang=\"ja\"><meta charset=\"utf-8\"><title>DiffBeacon 比較レポート</title><style>body{font-family:system-ui;margin:2rem}table{border-collapse:collapse;width:100%;table-layout:fixed}td,th{border:1px solid #aaa;padding:.4rem;vertical-align:top}pre{margin:0;white-space:pre-wrap;overflow-wrap:anywhere}.Added{background:#dff5df}.Deleted{background:#f9dddd}.Modified{background:#fff0c5}</style><h1>DiffBeacon 比較レポート</h1>");
        html.Append("<p>差分ブロック数: ").Append(result.Blocks.Count).Append("</p><table><thead><tr><th>").Append(Escape(leftName)).Append("</th><th>").Append(Escape(rightName)).Append("</th></tr></thead><tbody>");
        foreach (var row in result.Rows)
        {
            html.Append("<tr class=\"").Append(row.Kind).Append("\"><td><pre>").Append(row.LeftLineNumber?.ToString() ?? "").Append("  ").Append(Escape(row.LeftText));
            html.Append("</pre></td><td><pre>").Append(row.RightLineNumber?.ToString() ?? "").Append("  ").Append(Escape(row.RightText)).Append("</pre></td></tr>");
        }
        return html.Append("</tbody></table></html>").ToString();
    }

    public static string CreateJson(DiffResult result, string leftName, string rightName)
    {
        ArgumentNullException.ThrowIfNull(result);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("leftName", leftName); writer.WriteString("rightName", rightName);
            writer.WriteBoolean("different", result.HasDifferences); writer.WriteNumber("blocks", result.Blocks.Count);
            writer.WriteStartArray("rows");
            foreach (var row in result.Rows)
            {
                writer.WriteStartObject(); writer.WriteString("kind", row.Kind.ToString());
                if (row.LeftLineNumber is int left) writer.WriteNumber("leftLine", left); else writer.WriteNull("leftLine");
                if (row.RightLineNumber is int right) writer.WriteNumber("rightLine", right); else writer.WriteNull("rightLine");
                writer.WriteString("left", row.LeftText); writer.WriteString("right", row.RightText); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
