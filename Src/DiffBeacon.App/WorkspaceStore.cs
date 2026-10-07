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
    [JsonIgnore]
    internal string[] ProtectedArchiveAssets { get; set; } = [];
    // set を使い、source generation が省略された init 項目を default で上書きすることを防ぐ。
    public string LeftPath { get; set; } = "";
    public string BasePath { get; set; } = "";
    public string RightPath { get; set; } = "";
    public ArchiveProjectInput? LeftArchiveInput { get; set; }
    public ArchiveProjectInput? BaseArchiveInput { get; set; }
    public ArchiveProjectInput? RightArchiveInput { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextInputDescriptor? TextInputs { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextComparisonPair { get; set; }
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
    public bool FolderShowFiltered { get; set; }
    public string? ExcludedPaths { get; set; }
    public string? LegacyFilter { get; set; }
    public char? TableDelimiter { get; set; }
    public char? TableQuote { get; set; }
    public bool? TableAllowNewlinesInQuotes { get; set; }
    public ImageViewSettings ImageSettings { get; set; } = new();
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
[JsonSerializable(typeof(ArchiveProjectInput))]
[JsonSerializable(typeof(ArchiveWorkingSnapshot))]
[JsonSerializable(typeof(TextInputDescriptor))]
[JsonSerializable(typeof(TextInputSide))]
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

    public static Task SaveAsync(string path, ComparisonProject project, CancellationToken token = default, string? sourceProject = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.TextInputs is not null || ProjectInputs.HasArchives(project))
            return SaveWorkspaceAsync(path, new ComparisonWorkspace { Entries = [project] }, token, sourceProject);
        Validate(new ComparisonWorkspace { Entries = [project] });
        token.ThrowIfCancellationRequested();
        var snapshot = CloneProject(project);
        return SaveBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(snapshot, ProjectJsonContext.Default.ComparisonProject), token, [snapshot], sourceProject);
    }

    public static async Task SaveWorkspaceAsync(string path, ComparisonWorkspace workspace, CancellationToken token = default, string? sourceProject = null, Action<string>? publishedAsset = null)
    {
        token.ThrowIfCancellationRequested();
        _ = SerializeWorkspace(workspace);
        var snapshot = workspace with { Entries = workspace.Entries.Select(CloneProject).ToArray() };
        var fullPath = Path.GetFullPath(path);
        ProjectInputs.EnsureOutput(fullPath, snapshot.Entries, sourceProject); EnsureNoLinks(fullPath);
        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("読み取り専用の比較プロジェクトは保存できません。");
        var directory = Path.GetDirectoryName(fullPath)!;
        foreach (var input in snapshot.Entries.SelectMany(project => Enumerable.Range(0, 3).Select(side => ProjectInputs.Archive(project, side))).OfType<ArchiveProjectInput>())
            foreach (var copy in input.WorkingDocuments ?? [])
            {
                var bytes = copy.Bytes ?? throw new InvalidDataException("作業文書のsnapshotを読み込んでください。");
                var asset = Path.Combine(directory, Path.GetFileName(fullPath) + ".assets", copy.Sha256 + copy.AssetExtension);
                EnsureNoLinks(asset);
                if (File.Exists(asset))
                {
                    var existing = await ReadWorkingSnapshotAsync(asset, copy, token);
                    if (!existing.SequenceEqual(bytes)) throw new InvalidDataException("保存先の既存作業snapshotが一致しません。");
                }
                else
                {
                    ProjectInputs.EnsureOutput(asset, snapshot.Entries, sourceProject);
                    Directory.CreateDirectory(Path.GetDirectoryName(asset)!);
                    var temporary = Path.Combine(Path.GetDirectoryName(asset)!, ".working-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                        { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true); }
                        token.ThrowIfCancellationRequested(); EnsureNoLinks(asset);
                        ProjectInputs.EnsureOutput(asset, snapshot.Entries, sourceProject);
                        File.Move(temporary, asset, overwrite: false);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                // JSON公開の成功を待たず、不変assetの所有保護を呼び出し元へ渡す。
                publishedAsset?.Invoke(asset);
                copy.SnapshotPath = Path.GetRelativePath(directory, asset);
            }
        // 不変snapshotを先に公開し、既存projectは最後のatomic置換まで保持する。
        // project公開前に取消／失敗したassetは同じ保存先に保持し、次回SHA照合して再利用する。
        await SaveBytesAsync(path, SerializeWorkspace(snapshot), token, snapshot.Entries, sourceProject);
    }

    internal static async Task<byte[]> ReadWorkingSnapshotAsync(string path, ArchiveWorkingSnapshot copy, CancellationToken token)
    {
        var absolute = ArchiveActions.ValidatePath(path);
        if (new FileInfo(absolute).Length > copy.MaximumFileBytes) throw new InvalidDataException("作業文書がサイズ上限を超えています。");
        var bytes = await File.ReadAllBytesAsync(absolute, token);
        (copy with { Bytes = bytes }).Validate(absolute);
        return bytes;
    }

    internal static byte[] SerializeWorkspace(ComparisonWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.FormatVersion is not (1 or 2 or 3 or 4 or 5 or 6)) throw new InvalidDataException("比較ワークスペースの形式バージョンに対応していません。");
        if (workspace.Entries?.Any(project => project?.TextInputs is not null) == true)
            workspace = workspace with { FormatVersion = 6 };
        else if (workspace.FormatVersion != 6 && workspace.Entries?.Any(project => project is not null && ProjectInputs.HasArchives(project)) == true)
            workspace = workspace with { FormatVersion = workspace.Entries.Any(project => project is not null && Enumerable.Range(0, 3).Any(side => ProjectInputs.Archive(project, side)?.WorkingDocuments is not null))
                ? workspace.Entries.Any(project => Enumerable.Range(0, 3).Any(side => ProjectInputs.Archive(project, side)?.WorkingDocuments?.Any(copy => copy.IsBinary) == true)) ? 5 : 4 : workspace.Entries.Any(project => project is not null && ProjectInputs.HasMissing(project)) ? 3 : 2 };
        Validate(workspace);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(workspace, ProjectJsonContext.Default.ComparisonWorkspace);
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("比較プロジェクトは 4 MiB 以下にしてください。");
        return bytes;
    }

    internal static ComparisonProject CloneProject(ComparisonProject project) => project with
    {
        SubstitutionRules = project.SubstitutionRules.ToArray(), LegacySettings = new(project.LegacySettings),
        ImageSettings = project.ImageSettings with { }, LeftArchiveInput = project.LeftArchiveInput?.Copy(),
        BaseArchiveInput = project.BaseArchiveInput?.Copy(), RightArchiveInput = project.RightArchiveInput?.Copy(),
        ProtectedArchiveAssets = project.ProtectedArchiveAssets.ToArray(), TextInputs = project.TextInputs?.Copy()
    };

    private static async Task SaveBytesAsync(string path, byte[] bytes, CancellationToken token,
        IReadOnlyList<ComparisonProject> protectedProjects, string? sourceProject = null)
    {
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("比較プロジェクトは 4 MiB 以下にしてください。");
        var fullPath = Path.GetFullPath(path);
        ProjectInputs.EnsureOutput(fullPath, protectedProjects, sourceProject);
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
            ProjectInputs.EnsureOutput(fullPath, protectedProjects, sourceProject);
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
        // v6のenvelopeを先に確認し、重複versionの最後の値でschemaを切り替えない。
        if (wrapper && root.EnumerateObject().Any(field => field.Name == "formatVersion" && field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out var value) && value == 6))
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (field.Name is not ("formatVersion" or "entries" or "activeEntryIndex") || !fields.Add(field.Name))
                    throw new InvalidDataException("v6ワークスペースに未対応または重複した項目があります。");
            if (fields.Count != 3) throw new InvalidDataException("v6ワークスペースの必須項目がありません。");
        }
        if (wrapper && root.TryGetProperty("entries", out var projects) && projects.ValueKind == JsonValueKind.Array)
            foreach (var project in projects.EnumerateArray())
            {
                var version = root.TryGetProperty("formatVersion", out var schema) && schema.TryGetInt32(out var number) ? number : 1;
                ValidateTextJson(project, version); ValidateArchiveJson(project, version);
            }
        else if (!wrapper) { ValidateTextJson(root, 1); ValidateArchiveJson(root, 1); }
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
                LeftArchiveInput = ResolveArchive(project.LeftArchiveInput, directory),
                BaseArchiveInput = ResolveArchive(project.BaseArchiveInput, directory),
                RightArchiveInput = ResolveArchive(project.RightArchiveInput, directory),
                FileFilterPath = ResolveJsonPath(project.FileFilterPath, directory)
            }).ToArray()
        };
        var readAssets = new Dictionary<string, byte[]>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        long retainedAssetBytes = 0;
        foreach (var project in workspace.Entries)
        foreach (var input in Enumerable.Range(0, 3).Select(side => ProjectInputs.Archive(project, side)).OfType<ArchiveProjectInput>())
            foreach (var copy in input.WorkingDocuments ?? [])
            {
                var maximum = project.Mode.Equals("Binary", StringComparison.OrdinalIgnoreCase) || project.Mode == "3" ? Math.Min(copy.MaximumFileBytes, BinaryEditSession.MaximumFileBytes) : copy.MaximumFileBytes;
                if (string.IsNullOrWhiteSpace(copy.SnapshotPath) || Path.IsPathRooted(copy.SnapshotPath)
                    || copy.SnapshotPath.StartsWith('/') || copy.SnapshotPath.StartsWith('\\') || copy.SnapshotPath.Contains(':'))
                    throw new InvalidDataException("作業snapshotはプロジェクト内の相対pathで指定してください。");
                var relativeAsset = copy.SnapshotPath.Replace('\\', Path.DirectorySeparatorChar);
                var resolvedAsset = Path.GetFullPath(relativeAsset, directory);
                var boundary = Path.GetRelativePath(directory, resolvedAsset);
                if (boundary == ".." || boundary.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(boundary))
                    throw new InvalidDataException("作業snapshotをプロジェクトの外へ参照できません。");
                copy.SnapshotPath = ResolveJsonPath(copy.SnapshotPath, directory)!;
                if (!readAssets.TryGetValue(copy.SnapshotPath, out var assetBytes))
                {
                    var assetPath = ArchiveActions.ValidatePath(copy.SnapshotPath);
                    var length = new FileInfo(assetPath).Length;
                    if (length > maximum || length > ArchiveWorkingStore.MaximumBytes - retainedAssetBytes)
                        throw new InvalidDataException("復元する作業snapshotはText64 MiB／Binary16 MiB、合計128 MiBまでです。");
                    assetBytes = await ReadWorkingSnapshotAsync(assetPath, copy, token);
                    if (assetBytes.Length > ArchiveWorkingStore.MaximumBytes - retainedAssetBytes)
                        throw new InvalidDataException("復元する作業snapshotの合計上限を超えました。");
                    retainedAssetBytes += assetBytes.Length; readAssets.Add(copy.SnapshotPath, assetBytes);
                }
                if (assetBytes.Length > maximum) throw new InvalidDataException("作業版の形式別上限を超えています。");
                // 共有byte配列はruntime内部だけに公開し、すべての利用者が不変snapshotとして扱う。
                copy.Bytes = assetBytes; copy.Validate(input.RootPath);
            }
        var restoredStore = new ArchiveWorkingStore();
        foreach (var input in workspace.Entries.SelectMany(project => Enumerable.Range(0, 3).Select(side => ProjectInputs.Archive(project, side))).OfType<ArchiveProjectInput>()) restoredStore.Import(input);
        token.ThrowIfCancellationRequested();
        return workspace;
    }

    private static ArchiveProjectInput? ResolveArchive(ArchiveProjectInput? input, string directory)
        => input is null ? null : input with { RootPath = ResolveJsonPath(input.RootPath, directory)!, EntryChain = input.EntryChain.ToArray() };

    private static void ValidateTextJson(JsonElement project, int version)
    {
        if (project.ValueKind != JsonValueKind.Object) return;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var knownFields = version == 6 ? ProjectJsonContext.Default.ComparisonProject.Properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal) : null;
        var descriptorSeen = false;
        foreach (var field in project.EnumerateObject())
        {
            if (version == 6 && (!knownFields!.Contains(field.Name) || !fields.Add(field.Name)))
                throw new InvalidDataException("v6比較プロジェクトに未対応または重複した項目があります。");
            if (field.Name == "textComparisonPair" && field.Value.ValueKind != JsonValueKind.Null && version != 6)
                throw new InvalidDataException("Textの比較ペアには形式バージョン6が必要です。");
            if (field.Name != "textInputs") continue;
            if (descriptorSeen) throw new InvalidDataException("Text入力のdescriptorが重複しています。");
            descriptorSeen = true;
            if (field.Value.ValueKind == JsonValueKind.Null) continue;
            if (version != 6) throw new InvalidDataException("typed Text入力には形式バージョン6が必要です。");
            TextInputDescriptor.ValidateJson(field.Value);
            if (!project.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String || !string.Equals(mode.GetString(), "Text", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("typed Text入力にはTextの明示形式が必要です。");
            foreach (var path in new[] { "leftPath", "basePath", "rightPath" })
                if (!project.TryGetProperty(path, out var value) || value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("typed Text入力の各側には既存pathの文字列payloadが必要です。");
        }
    }

    private static void ValidateArchiveJson(JsonElement project, int version)
    {
        if (project.ValueKind != JsonValueKind.Object) return;
        var sides = new HashSet<string>(StringComparer.Ordinal);
        foreach (var side in project.EnumerateObject())
        {
            if (side.Name is not ("leftArchiveInput" or "baseArchiveInput" or "rightArchiveInput")) continue;
            if (!sides.Add(side.Name)) throw new InvalidDataException("内包入力の指定が重複しています。");
            if (side.Value.ValueKind == JsonValueKind.Null) continue;
            if (side.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("内包入力にはobjectを指定してください。");
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in side.Value.EnumerateObject())
            {
                if (field.Name is not ("rootPath" or "entryChain" or "leafEntry" or "rootSha256" or "missingEntryChain" or "inheritedReadOnly" or "workingTexts") || !fields.Add(field.Name))
                    throw new InvalidDataException("内包入力に未対応または重複した項目があります。");
                if (field.Name == "inheritedReadOnly" && field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                    throw new InvalidDataException("内包入力の読取り専用継承には真偽値を指定してください。");
                if (field.Name == "entryChain" && (field.Value.ValueKind != JsonValueKind.Array || field.Value.GetArrayLength() > 8))
                    throw new InvalidDataException("内包入力の格納階層が不正です。");
                if (field.Name == "missingEntryChain" && (field.Value.ValueKind != JsonValueKind.Array ||
                    field.Value.GetArrayLength() is < 1 or > 9))
                    throw new InvalidDataException("不在入力の格納階層が不正です。");
                if (field.Name == "workingTexts" && field.Value.ValueKind != JsonValueKind.Null)
                {
                    if (field.Value.ValueKind != JsonValueKind.Array || field.Value.GetArrayLength() is < 1 or > ArchiveWorkingStore.MaximumDocuments)
                        throw new InvalidDataException("作業文書の配列が不正です。");
                    foreach (var snapshot in field.Value.EnumerateArray())
                    {
                        if (snapshot.ValueKind != JsonValueKind.Object) throw new InvalidDataException("作業文書はobjectで指定してください。");
                        var snapshotFields = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var item in snapshot.EnumerateObject())
                            if (item.Name is not ("entryChain" or "leafEntry" or "snapshotPath" or "sha256" or "encodingName" or "hasBom" or "kind") || !snapshotFields.Add(item.Name) || item.Name == "kind" && (version is not (5 or 6) || item.Value.ValueKind != JsonValueKind.String || item.Value.GetString() != "Binary"))
                                throw new InvalidDataException("作業文書に未対応または重複した項目があります。");
                        if (snapshotFields.Count != (snapshotFields.Contains("kind") ? 7 : 6)) throw new InvalidDataException("作業文書の必須項目がありません。");
                    }
                }
            }
        }
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
        if (workspace.FormatVersion is not (1 or 2 or 3 or 4 or 5 or 6)) throw new InvalidDataException("比較ワークスペースの形式バージョンに対応していません。");
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
            if (project.TextInputs is { } textInputs)
            {
                if (workspace.FormatVersion != 6) throw new InvalidDataException("typed Text入力には形式バージョン6が必要です。");
                textInputs.Validate(project);
            }
            if (project.TextComparisonPair is not null && (workspace.FormatVersion != 6 || !ProjectInputs.IsIndependentText(project)
                || project.TextComparisonPair is not ("LeftMiddle" or "MiddleRight" or "LeftRight")))
                throw new InvalidDataException("Textの比較ペアはv6の独立Textで明示してください。");
            if (ProjectInputs.HasArchives(project))
            {
                if (workspace.FormatVersion is not (2 or 3 or 4 or 5 or 6)) throw new InvalidDataException("内包入力には形式バージョン2以降のワークスペースが必要です。");
                if (ProjectInputs.HasMissing(project) && workspace.FormatVersion is not (3 or 4 or 5 or 6))
                    throw new InvalidDataException("不在入力には形式バージョン3のワークスペースが必要です。");
                var mode = project.Mode.ToLowerInvariant();
                if (mode is not ("text" or "1" or "binary" or "3" or "archive" or "7"))
                    throw new InvalidDataException("内包入力はText／Binary／Archiveの明示形式で開いてください。");
                if (mode is "archive" or "7" && ProjectInputs.HasBase(project))
                    throw new InvalidDataException("内包入力の中央指定はText／Binary比較だけで使用できます。");
                for (var side = 0; side < 3; side++)
                {
                    var input = ProjectInputs.Archive(project, side);
                    if (input is null) continue;
                    if (input.WorkingDocuments is not null && (workspace.FormatVersion is not (4 or 5 or 6) || mode is not ("text" or "1" or "binary" or "3" or "archive" or "7")))
                        throw new InvalidDataException("作業版には形式バージョン4以降とText／Binary／Archive比較が必要です。");
                    var oldPath = side switch { 0 => project.LeftPath, 1 => project.BasePath, _ => project.RightPath };
                    if (!string.IsNullOrEmpty(oldPath)) throw new InvalidDataException("物理pathと内包入力を同じ側へ指定できません。");
                    if (side == 1 && mode is not ("text" or "1" or "binary" or "3")) throw new InvalidDataException("中央の内包入力はText／Binary比較だけで使用できます。");
                    // v6でもkind省略はv4のText、明示Binaryだけはv5のBinary／Archive制約を維持する。
                    if (input.WorkingDocuments?.Any(copy => copy.IsBinary) == true && (workspace.FormatVersion is not (5 or 6) || mode is "text" or "1"))
                        throw new InvalidDataException("Binary作業版には形式バージョン5とBinary／Archive比較が必要です。");
                    input.Validate(mode is "archive" or "7", side switch { 0 => project.LeftReadOnly, 1 => project.BaseReadOnly, _ => project.RightReadOnly });
                }
            }
            if (!Enum.IsDefined(project.CommentSyntax) || !Enum.IsDefined(project.Whitespace))
                throw new InvalidDataException("比較プロジェクトのコメント構文または空白の比較方式が不正です。");
            if (project.SubstitutionRules.Any(rule => rule is null || rule.Pattern is null || rule.Replacement is null))
                throw new InvalidDataException("置換ルールの必須項目が null です。");
            if (project.LegacySettings.Values.Any(value => value is null))
                throw new InvalidDataException("旧プロジェクト設定の値が null です。");
            ImageViewSettings.Validate(project.ImageSettings);
            ProjectInputs.EnsureWorkingFormat(project);
            if (!ProjectInputs.HasBase(project) && (project.ImageSettings.MiddleFrame != 1 || !project.ImageSettings.MiddleOrientation.IsIdentity || project.ImageSettings.MiddleOffset != default))
                throw new InvalidDataException("中央入力のない比較では中央の画像ページ番号を1、回転・反転を無効にしてください。");
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
