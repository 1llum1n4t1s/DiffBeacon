using System.Text;
using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public static class ProjectReport
{
    public const int MaximumBytes = 32 * 1024 * 1024;

    internal static bool IsImage(ComparisonProject project) => project.Mode.ToLowerInvariant() is "image" or "4"
        || project.Mode.ToLowerInvariant() is "auto" or "0"
        && SpecializedViews.IsImage(project.LeftPath) && SpecializedViews.IsImage(project.RightPath);

    internal static bool IsTextual(ComparisonProject project) => project.Mode.ToLowerInvariant() is "text" or "table" or "json" or "1" or "5" or "6"
        || project.Mode.ToLowerInvariant() is "auto" or "0"
        && !(SpecializedViews.IsImage(project.LeftPath) && SpecializedViews.IsImage(project.RightPath))
        && !(ArchivePanel.Supports(project.LeftPath) && ArchivePanel.Supports(project.RightPath))
        && !(Directory.Exists(project.LeftPath) && Directory.Exists(project.RightPath));

    internal static ComparisonOptions Options(ComparisonProject project) => new()
    {
        IgnoreCase = project.IgnoreCase, IgnoreWhitespace = project.IgnoreWhitespace,
        IgnoreBlankLines = project.IgnoreBlankLines, IgnoreLinePattern = project.IgnoreLinePattern,
        IgnoreNumbers = project.IgnoreNumbers, CommentSyntax = project.CommentSyntax,
        Whitespace = project.Whitespace, SubstitutionRules = project.SubstitutionRules
    };

    public static string Create(ComparisonProject project, string left, string? ancestor, string right,
        CancellationToken token = default, string? leftName = null, string? baseName = null, string? rightName = null)
    {
        if (!IsTextual(project)) throw new InvalidOperationException("この形式の単体HTMLレポートは未対応です。");
        static string Title(string? description, string? name, string path, string fallback) =>
            !string.IsNullOrWhiteSpace(description) ? description : name ?? (string.IsNullOrWhiteSpace(path) ? fallback : path);
        var documents = new List<ReportDocument> { new(Title(project.LeftDescription, leftName, project.LeftPath, "左"), left) };
        if (ancestor is not null) documents.Add(new(Title(project.BaseDescription, baseName, project.BasePath, "共通の祖先"), ancestor));
        documents.Add(new(Title(project.RightDescription, rightName, project.RightPath, "右"), right));
        var settings = Options(project);
        var html = project.Mode.ToLowerInvariant() switch
        {
            "table" or "6" => HtmlReport.CreateDelimited(documents,
                project.TableDelimiter ?? SpecializedViews.DetectSeparator(left, right),
                project.TableQuote ?? '"', project.TableAllowNewlinesInQuotes ?? true, settings, MaximumBytes, token),
            "json" or "5" => HtmlReport.CreateJsonComparison(documents, settings, MaximumBytes, token),
            _ => HtmlReport.CreateText(documents, settings, MaximumBytes, token)
        };
        ValidateSize(html);
        return html;
    }

    public static async Task ExportAsync(ComparisonWorkspace workspace, int entryIndex, string output,
        string? sourceProject = null, CancellationToken token = default,
        int? leftFrame = null, int? rightFrame = null, double? imageThreshold = null, int? middleFrame = null)
    {
        _ = WorkspaceStore.SerializeWorkspace(workspace);
        if ((uint)entryIndex >= (uint)workspace.Entries.Length) throw new ArgumentOutOfRangeException(nameof(entryIndex), "比較の番号が範囲外です。");
        var entries = workspace.Entries.Select(entry => entry with
        { SubstitutionRules = entry.SubstitutionRules.ToArray(), LegacySettings = new(entry.LegacySettings), ImageSettings = entry.ImageSettings with { } }).ToArray();
        var target = ValidateTarget(output, entries, sourceProject);
        var project = entries[entryIndex];
        if (IsImage(project))
        {
            var leftImagePath = ValidateLocal(project.LeftPath); var rightImagePath = ValidateLocal(project.RightPath);
            var leftImage = await ImageComparisonEngine.OpenAsync(leftImagePath, token).ConfigureAwait(false);
            var rightImage = await ImageComparisonEngine.OpenAsync(rightImagePath, token).ConfigureAwait(false);
            var middleImage = string.IsNullOrWhiteSpace(project.BasePath) ? null : await ImageComparisonEngine.OpenAsync(ValidateLocal(project.BasePath), token).ConfigureAwait(false);
            var selected = leftFrame.HasValue || middleFrame.HasValue || rightFrame.HasValue;
            if (middleImage is null && middleFrame.HasValue || selected && (!leftFrame.HasValue || !rightFrame.HasValue || middleImage is not null && !middleFrame.HasValue))
                throw new ArgumentException("選択フレームは画像の全入力分を指定してください。");
            ImageComparisonEngine.Snapshot[] images = middleImage is null ? [leftImage, rightImage] : [leftImage, middleImage, rightImage];
            int[]? numbers = selected ? middleImage is null ? [leftFrame!.Value, rightFrame!.Value] : [leftFrame!.Value, middleFrame!.Value, rightFrame!.Value] : null;
            var settings = project.ImageSettings;
            if (!selected && !settings.ReportAllFrames) numbers = settings.FrameNumbers(middleImage is not null);
            string[] titles = middleImage is null ? [project.LeftDescription ?? project.LeftPath, project.RightDescription ?? project.RightPath]
                : [project.LeftDescription ?? project.LeftPath, project.BaseDescription ?? project.BasePath, project.RightDescription ?? project.RightPath];
            var imageHtml = await Task.Run(() => ImageReport.Create(new(images, imageThreshold ?? settings.Threshold, numbers,
                ShowDifferences: settings.ShowDifferences, Orientations: settings.Orientations(middleImage is not null), BlockSize: settings.BlockSize), titles, token), token).ConfigureAwait(false);
            await SaveAsync(target, imageHtml, entries, sourceProject, token).ConfigureAwait(false);
            return;
        }
        if (leftFrame.HasValue || rightFrame.HasValue || imageThreshold.HasValue || middleFrame.HasValue)
            throw new ArgumentException("フレーム・閾値のレポート指定は画像比較にだけ使用できます。");
        if (!IsTextual(project)) throw new InvalidOperationException("この形式の単体HTMLレポートは未対応です。");
        async Task<string> Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || IsUrl(path)) throw new InvalidOperationException("単体レポートにはローカルの比較文書を指定してください。");
            ValidateLocal(path);
            return (await TextDocument.LoadAsync(path, token).ConfigureAwait(false)).Text;
        }
        var left = await Read(project.LeftPath).ConfigureAwait(false);
        var ancestor = string.IsNullOrWhiteSpace(project.BasePath) ? null : await Read(project.BasePath).ConfigureAwait(false);
        var right = await Read(project.RightPath).ConfigureAwait(false);
        var html = await Task.Run(() => Create(project, left, ancestor, right, token), token).ConfigureAwait(false);
        await SaveAsync(target, html, entries, sourceProject, token).ConfigureAwait(false);
    }

    internal static async Task SaveAsync(string output, string html, IEnumerable<ComparisonProject> protectedEntries,
        string? sourceProject = null, CancellationToken token = default)
    {
        ValidateSize(html);
        var target = ValidateTarget(output, protectedEntries, sourceProject);
        token.ThrowIfCancellationRequested();
        await TextDocument.Create().SaveAsync(target, html, token).ConfigureAwait(false);
    }

    private static void ValidateSize(string html)
    {
        if (Encoding.UTF8.GetByteCount(html) > MaximumBytes) throw new InvalidDataException("HTMLレポートは32 MiBまでです。");
    }

    private static string ValidateTarget(string output, IEnumerable<ComparisonProject> entries, string? sourceProject)
    {
        var target = ValidateLocal(output);
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("レポートの保存先フォルダーがありません。");
        IEnumerable<string?> Paths(ComparisonProject project) => [project.LeftPath, project.BasePath, project.RightPath, project.FileFilterPath];
        var projects = entries.ToArray();
        foreach (var source in projects.SelectMany(Paths).Append(sourceProject))
            if (!string.IsNullOrWhiteSpace(source) && !IsUrl(source) && ArchivePaths.SameFile(source, target))
                throw new InvalidOperationException("レポートで比較文書・フィルター・プロジェクトを上書きできません。");
        EnsureReadOnlyDirectories(target, projects);
        return target;
    }

    internal static void EnsureReadOnlyDirectories(string output, IEnumerable<ComparisonProject> projects)
    {
        foreach (var project in projects)
            foreach (var (source, readOnly) in new[] { (project.LeftPath, project.LeftReadOnly), (project.BasePath, project.BaseReadOnly), (project.RightPath, project.RightReadOnly) })
            {
                if (!readOnly || string.IsNullOrWhiteSpace(source) || IsUrl(source) || !Directory.Exists(source)) continue;
                for (var current = Path.GetDirectoryName(Path.GetFullPath(output)); current is not null; current = Path.GetDirectoryName(current))
                    if (ArchivePaths.SameFile(source, current)) throw new InvalidOperationException("読取り専用に指定されたフォルダー内へ保存できません。");
            }
    }

    private static bool IsUrl(string path) => Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string ValidateLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsUrl(path)) throw new ArgumentException("ローカルのファイルを指定してください。");
        var full = Path.GetFullPath(path);
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            FileSystemInfo item = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (item.LinkTarget is not null || item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("レポートのパスにリンクを使用できません。");
        }
        return full;
    }
}
