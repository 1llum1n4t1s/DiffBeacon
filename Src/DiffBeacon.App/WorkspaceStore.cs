using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public sealed record ComparisonProject
{
    // set を使い、source generation が省略された init 項目を default で上書きすることを防ぐ。
    public string LeftPath { get; set; } = "";
    public string BasePath { get; set; } = "";
    public string RightPath { get; set; } = "";
    public string Mode { get; set; } = "Text";
    public string? ProviderId { get; set; }
    public string? FileFilterPath { get; set; }
    public bool IgnoreCase { get; set; }
    public bool IgnoreWhitespace { get; set; }
    public bool IgnoreBlankLines { get; set; }
    public string? IgnoreLinePattern { get; set; }
    public bool IgnoreNumbers { get; set; }
    public CommentSyntax CommentSyntax { get; set; }
    public WhitespaceMode Whitespace { get; set; }
    public SubstitutionRule[] SubstitutionRules { get; set; } = [];
    public string? LeftDescription { get; set; }
    public string? BaseDescription { get; set; }
    public string? RightDescription { get; set; }
    public bool LeftReadOnly { get; set; }
    public bool BaseReadOnly { get; set; }
    public bool RightReadOnly { get; set; }
    public bool Recursive { get; set; } = true;
    public string FolderMode { get; set; } = "Content";
    public string? ExcludedPaths { get; set; }
    public string? LegacyFilter { get; set; }
    public char? TableDelimiter { get; set; }
    public char? TableQuote { get; set; }
    public bool? TableAllowNewlinesInQuotes { get; set; }
    public Dictionary<string, string> LegacySettings { get; set; } = [];
}

public sealed record ComparisonWorkspace
{
    public int FormatVersion { get; set; } = 1;
    public ComparisonProject[] Entries { get; set; } = [];
    public int ActiveEntryIndex { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    Converters = new[] { typeof(SubstitutionRuleJsonConverter) })]
[JsonSerializable(typeof(ComparisonProject))]
[JsonSerializable(typeof(ComparisonWorkspace))]
internal partial class ProjectJsonContext : JsonSerializerContext { }

internal sealed class SubstitutionRuleJsonConverter : JsonConverter<SubstitutionRule>
{
    public override SubstitutionRule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("置換ルールがオブジェクトではありません。");
        string RequiredString(string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                throw new JsonException($"置換ルールの {name} には文字列が必要です。");
            return value.GetString()!;
        }
        bool Boolean(string name, bool fallback)
        {
            if (!root.TryGetProperty(name, out var value)) return fallback;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new JsonException($"置換ルールの {name} には真偽値が必要です。")
            };
        }
        // Core の不変な型を維持し、省略項目にもコンストラクターと同じ既定値を渡す。
        return new SubstitutionRule(RequiredString("pattern"), RequiredString("replacement"), Boolean("matchCase", true))
        {
            UseRegex = Boolean("useRegex", true), WholeWord = Boolean("wholeWord", false), Enabled = Boolean("enabled", true)
        };
    }

    public override void Write(Utf8JsonWriter writer, SubstitutionRule value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("pattern", value.Pattern);
        writer.WriteString("replacement", value.Replacement);
        writer.WriteBoolean("matchCase", value.MatchCase);
        writer.WriteBoolean("useRegex", value.UseRegex);
        writer.WriteBoolean("wholeWord", value.WholeWord);
        writer.WriteBoolean("enabled", value.Enabled);
        writer.WriteEndObject();
    }
}

public static class WorkspaceStore
{
    public const int MaxEntries = 256;
    public const int MaxFileBytes = 4 * 1024 * 1024;
    private static readonly string[] KnownModes = ["Auto", "Text", "Folder", "Binary", "Image", "Json", "Table", "Archive", "Web", "Provider"];

    public static Task SaveAsync(string path, ComparisonProject project, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        Validate(new ComparisonWorkspace { Entries = [project] });
        token.ThrowIfCancellationRequested();
        return SaveBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(project, ProjectJsonContext.Default.ComparisonProject), token);
    }

    public static Task SaveWorkspaceAsync(string path, ComparisonWorkspace workspace, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return SaveBytesAsync(path, SerializeWorkspace(workspace), token);
    }

    internal static byte[] SerializeWorkspace(ComparisonWorkspace workspace)
    {
        Validate(workspace);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(workspace, ProjectJsonContext.Default.ComparisonWorkspace);
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("比較プロジェクトは 4 MiB 以下にしてください。");
        return bytes;
    }

    private static async Task SaveBytesAsync(string path, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("比較プロジェクトは 4 MiB 以下にしてください。");
        var fullPath = Path.GetFullPath(path);
        EnsureNoLinks(fullPath);
        var targetExists = File.Exists(fullPath);
        var attributes = targetExists ? File.GetAttributes(fullPath) : FileAttributes.Normal;
        if ((attributes & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("読み取り専用の比較プロジェクトは保存できません。");
        UnixFileMode? unixMode = null;
        if (!OperatingSystem.IsWindows() && targetExists) unixMode = File.GetUnixFileMode(fullPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, ".diffbeacon-project-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token);
                await stream.FlushAsync(token);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (unixMode.HasValue && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporaryPath, unixMode.Value);
            if (OperatingSystem.IsWindows() && targetExists)
            {
                var preserved = attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed);
                File.SetAttributes(temporaryPath, preserved == 0 ? FileAttributes.Normal : preserved);
            }
            EnsureNoLinks(fullPath);
            if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("読み取り専用の比較プロジェクトは保存できません。");
            token.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    public static async Task<ComparisonProject> LoadAsync(string path, CancellationToken token = default)
    {
        var workspace = await LoadWorkspaceAsync(path, token);
        return SingleEntry(workspace);
    }

    public static async Task<ComparisonWorkspace> LoadWorkspaceAsync(string path, CancellationToken token = default)
    {
        if (Path.GetExtension(path).Equals(".WinMerge", StringComparison.OrdinalIgnoreCase))
            return await ImportLegacyWorkspaceAsync(path, token);
        var bytes = await ReadBoundedAsync(path, token);
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        using var document = JsonDocument.Parse(bytes.AsMemory(offset));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("比較プロジェクトがオブジェクトではありません。");
        var wrapper = root.TryGetProperty("entries", out _) || root.TryGetProperty("formatVersion", out _) || root.TryGetProperty("activeEntryIndex", out _);
        var workspace = wrapper
            ? root.Deserialize(ProjectJsonContext.Default.ComparisonWorkspace) ?? throw new InvalidDataException("比較ワークスペースが空です。")
            : new ComparisonWorkspace { Entries = [root.Deserialize(ProjectJsonContext.Default.ComparisonProject) ?? throw new InvalidDataException("比較プロジェクトが空です。")] };
        Validate(workspace);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        workspace = workspace with
        {
            Entries = workspace.Entries.Select(project => project with
            {
                LeftPath = ResolveJsonPath(project.LeftPath, directory)!,
                BasePath = ResolveJsonPath(project.BasePath, directory)!,
                RightPath = ResolveJsonPath(project.RightPath, directory)!,
                FileFilterPath = ResolveJsonPath(project.FileFilterPath, directory)
            }).ToArray()
        };
        token.ThrowIfCancellationRequested();
        return workspace;
    }

    private static string? ResolveJsonPath(string? value, string directory)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return value;
        // JSON 内の参照はプロジェクト基準で解決し、別 OS の絶対表記は保持する。
        if (Path.IsPathRooted(value) || value.StartsWith('/') || (value.Length > 1 && value[1] == ':') || value.StartsWith("\\\\", StringComparison.Ordinal)) return value;
        return Path.GetFullPath(Path.Combine(directory, value.Replace('\\', Path.DirectorySeparatorChar)));
    }

    public static async Task<ComparisonProject> ImportLegacyAsync(string path, CancellationToken token = default)
    {
        return SingleEntry(await ImportLegacyWorkspaceAsync(path, token));
    }

    private static ComparisonProject SingleEntry(ComparisonWorkspace workspace)
    {
        if (workspace.Entries.Length != 1)
            throw new InvalidDataException("複数の比較を含むプロジェクトです。ワークスペースとして読み込んでください。");
        return workspace.Entries[0];
    }

    private static void Validate(ComparisonWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.FormatVersion != 1) throw new InvalidDataException("比較ワークスペースの形式バージョンに対応していません。");
        if (workspace.Entries is null || workspace.Entries.Length is < 1 or > MaxEntries)
            throw new InvalidDataException($"比較は 1 ～ {MaxEntries} 件を指定してください。");
        if (workspace.ActiveEntryIndex < 0 || workspace.ActiveEntryIndex >= workspace.Entries.Length)
            throw new InvalidDataException("選択中の比較インデックスが範囲外です。");
        foreach (var project in workspace.Entries)
        {
            if (project is null || project.LeftPath is null || project.BasePath is null || project.RightPath is null ||
                project.Mode is null || project.FolderMode is null || project.SubstitutionRules is null || project.LegacySettings is null)
                throw new InvalidDataException("比較プロジェクトの必須項目が null です。");
            var knownMode = KnownModes.Contains(project.Mode, StringComparer.OrdinalIgnoreCase);
            if (!knownMode && !(int.TryParse(project.Mode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var oldMode) && oldMode is >= 0 and <= 8))
                throw new InvalidDataException($"未対応の比較形式です: {project.Mode}");
            if (project.FolderMode is not ("Content" or "Hash" or "TimestampAndSize"))
                throw new InvalidDataException("フォルダー比較方式が不正です。");
            if (!Enum.IsDefined(project.CommentSyntax) || !Enum.IsDefined(project.Whitespace))
                throw new InvalidDataException("比較プロジェクトのコメント構文または空白の比較方式が不正です。");
            if (project.SubstitutionRules.Any(rule => rule is null || rule.Pattern is null || rule.Replacement is null))
                throw new InvalidDataException("置換ルールの必須項目が null です。");
            if (project.LegacySettings.Values.Any(value => value is null))
                throw new InvalidDataException("旧プロジェクト設定の値が null です。");
        }
    }

    private static void EnsureNoLinks(string fullPath)
    {
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("リンクを経由した比較プロジェクトの保存はできません。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("比較プロジェクトは 4 MiB 以下にしてください。");
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > MaxFileBytes) throw new InvalidDataException("比較プロジェクトは 4 MiB 以下にしてください。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static async Task<ComparisonWorkspace> ImportLegacyWorkspaceAsync(string path, CancellationToken token)
    {
        var bytes = await ReadBoundedAsync(path, token);
        using var stream = new MemoryStream(bytes, writable: false);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxFileBytes };
        using var reader = XmlReader.Create(stream, settings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, token);
        if (document.Root?.Name != "project") throw new InvalidDataException("WinMerge プロジェクトのルート要素が不正です。");
        var entries = document.Root.Elements("paths").ToArray();
        if (entries.Length is < 1 or > MaxEntries) throw new InvalidDataException($"WinMerge プロジェクトには 1 ～ {MaxEntries} 件の paths が必要です。");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var workspace = new ComparisonWorkspace { Entries = entries.Select(entry => ImportLegacyEntry(entry, directory)).ToArray() };
        // paths 外の拡張設定も元の値を保持し、実行設定にはしない。
        var rootSettings = new Dictionary<string, string>();
        foreach (var element in document.Root.Elements().Where(element => element.Name != "paths"))
            PreserveSetting(rootSettings, "project/" + element.Name, element);
        // ルートの拡張内容を比較件数分の文字列へ複製しない。
        foreach (var project in workspace.Entries)
            foreach (var (name, value) in rootSettings) project.LegacySettings[name] = value;
        Validate(workspace);
        token.ThrowIfCancellationRequested();
        return workspace;
    }

    private static ComparisonProject ImportLegacyEntry(XElement entry, string directory)
    {
        string Value(string name) => entry.Element(name)?.Value ?? "";
        string? Optional(string name) => entry.Element(name)?.Value;
        bool Flag(string name) => bool.TryParse(Value(name).Trim(), out var flag) ? flag :
            int.TryParse(Value(name).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number != 0;
        var ignoreComments = entry.Element("ignore-comment-diff") is not null ? Flag("ignore-comment-diff") : Flag("ignore-comments");
        string Resolve(string name)
        {
            var value = Environment.ExpandEnvironmentVariables(Value(name));
            if (string.IsNullOrWhiteSpace(value)) return "";
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return value;
            // 別 OS の絶対パスは書き換えず、利用者が移行先で選び直せるよう保持する。
            if (Path.IsPathRooted(value) || value.StartsWith('/') || (value.Length > 1 && value[1] == ':') || value.StartsWith("\\\\", StringComparison.Ordinal)) return value;
            return Path.GetFullPath(Path.Combine(directory, value.Replace('\\', Path.DirectorySeparatorChar)));
        }
        var legacy = new Dictionary<string, string>();
        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "left", "middle", "right", "left-desc", "middle-desc", "right-desc", "left-readonly", "middle-readonly", "right-readonly",
            "window-type", "subfolders", "white-spaces", "ignore-case", "ignore-numbers", "ignore-comment-diff", "ignore-comments", "ignore-blank-lines",
            "table-delimiter", "table-quote", "table-allownewlinesinquotes", "compare-method", "filter"
        };
        foreach (var element in entry.Elements())
            if (!supported.Contains(element.Name.ToString())) PreserveSetting(legacy, element.Name.ToString(), element);
        var folderMode = Value("compare-method").Trim() switch { "2" => "Hash", "4" => "TimestampAndSize", _ => "Content" };
        if (entry.Element("compare-method") is not null && Value("compare-method").Trim() is not ("0" or "2" or "4"))
            legacy["compare-method"] = Value("compare-method");
        char? Character(string name)
        {
            var value = Optional(name);
            if (value is { Length: 1 }) return value[0];
            if (value is not null)
                legacy[name] = value;
            return null;
        }
        if (entry.Element("window-type") is not null && Value("window-type").Trim() is not ("1" or "2" or "3" or "4" or "5" or "6"))
            legacy["window-type"] = Value("window-type");
        if (entry.Element("white-spaces") is not null && Value("white-spaces").Trim() is not ("0" or "1" or "2"))
            legacy["white-spaces"] = Value("white-spaces");
        var commentSyntax = ignoreComments ? InferCommentSyntax(Value("left"), Value("middle"), Value("right")) : CommentSyntax.None;
        if (ignoreComments && commentSyntax == CommentSyntax.None)
        {
            var commentName = entry.Element("ignore-comment-diff") is not null ? "ignore-comment-diff" : "ignore-comments";
            legacy[commentName] = Value(commentName);
        }
        return new ComparisonProject
        {
            LeftPath = Resolve("left"), BasePath = Resolve("middle"), RightPath = Resolve("right"),
            LeftDescription = Optional("left-desc"), BaseDescription = Optional("middle-desc"), RightDescription = Optional("right-desc"),
            LeftReadOnly = Flag("left-readonly"), BaseReadOnly = Flag("middle-readonly"), RightReadOnly = Flag("right-readonly"),
            Recursive = entry.Element("subfolders") is null || Flag("subfolders"), FolderMode = folderMode,
            LegacyFilter = Optional("filter"), LegacySettings = legacy,
            TableDelimiter = Character("table-delimiter"), TableQuote = Character("table-quote"),
            TableAllowNewlinesInQuotes = entry.Element("table-allownewlinesinquotes") is null ? null : Flag("table-allownewlinesinquotes"),
            Mode = Value("window-type").Trim() switch { "2" => "Table", "3" => "Binary", "4" => "Image", "5" => "Web", "6" => "Folder", _ => "Text" },
            IgnoreCase = Flag("ignore-case"), IgnoreWhitespace = Value("white-spaces").Trim() == "2",
            Whitespace = Value("white-spaces").Trim() switch { "1" => WhitespaceMode.IgnoreChanges, "2" => WhitespaceMode.IgnoreAll, _ => WhitespaceMode.None },
            IgnoreNumbers = Flag("ignore-numbers"),
            CommentSyntax = commentSyntax,
            IgnoreBlankLines = Flag("ignore-blank-lines")
        };
    }

    private static void PreserveSetting(Dictionary<string, string> settings, string name, XElement element)
    {
        var key = name;
        for (var duplicate = 2; settings.ContainsKey(key); duplicate++) key = name + "[" + duplicate + "]";
        settings[key] = element.HasElements || element.HasAttributes ? element.ToString(SaveOptions.DisableFormatting) : element.Value;
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
