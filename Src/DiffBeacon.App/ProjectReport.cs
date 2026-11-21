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
        && !(FolderComparisons.IsDirectory(project.LeftPath) && FolderComparisons.IsDirectory(project.RightPath));

    internal static ComparisonOptions Options(ComparisonProject project, ILineTransformLogEnvironment? logEnvironment = null) => new()
    {
        IgnoreCase = project.IgnoreCase, IgnoreWhitespace = project.IgnoreWhitespace,
        IgnoreBlankLines = project.IgnoreBlankLines, IgnoreLinePattern = project.IgnoreLinePattern,
        IgnoreNumbers = project.IgnoreNumbers, CommentSyntax = project.CommentSyntax,
        Whitespace = project.Whitespace, InlineCharacterLevel = project.InlineCharacterLevel,
        BreakOnSeparators = project.BreakOnSeparators,
        WordBreakSeparators = project.WordBreakSeparators, SubstitutionRules = project.SubstitutionRules,
        LineTransformRules = Array.AsReadOnly(WorkspaceStore.CloneLineTransformRules(project.LineTransformRules, project)),
        LineTransformTimestampEnvironment = LineTransformTimestampEnvironments.ForRules(project.LineTransformRules),
        LineTransformStringEnvironment = LineTransformStringEnvironments.ForRules(project.LineTransformRules),
        LineTransformLogEnvironment = logEnvironment ?? LineTransformLogEnvironments.Trace,
        LineTransformListEnvironment = LineTransformListEnvironments.Create(logEnvironment ?? LineTransformLogEnvironments.Trace, project),
            TableColumnIgnoreRules = Array.AsReadOnly(WorkspaceStore.CloneTableColumnIgnoreRules(project.TableColumnIgnoreRules)),
            TableColumnRangeRules = Array.AsReadOnly(WorkspaceStore.CloneTableColumnRangeRules(project.TableColumnRangeRules))
    };

    public static string Create(ComparisonProject project, string left, string? ancestor, string right,
        CancellationToken token = default, string? leftName = null, string? baseName = null, string? rightName = null,
        IReadOnlyList<TextDocument>? snapshots = null, TextComparisonSourcePaths? sourcePaths = null,
        ILineTransformLogEnvironment? logEnvironment = null)
    {
        if (!IsTextual(project)) throw new InvalidOperationException("この形式の単体HTMLレポートは未対応です。");
        static string Title(string? description, string? name, string path, string fallback) =>
            !string.IsNullOrWhiteSpace(description) ? description : name ?? (string.IsNullOrWhiteSpace(path) ? fallback : path);
        string InputTitle(int side, string? description, string? name, string fallback)
        {
            var title = Title(description, name, ProjectInputs.Caption(project, side), fallback);
            return ProjectInputs.Archive(project, side)?.MissingEntryChain is not null && !title.EndsWith("（存在しない）", StringComparison.Ordinal)
                ? title + "（存在しない）" : title;
        }
        sourcePaths ??= snapshots is { Count: 3 }
            ? TextComparisonSources.ForProject(project, snapshots[0], snapshots[1], snapshots[2]) : new();
        if (ProjectInputs.IsIndependentText(project))
        {
            if (ancestor is null) throw new InvalidDataException("独立三者Textの中央本文がありません。");
            var independent = IndependentTextReport.Create(project,
                [new(InputTitle(0, project.LeftDescription, leftName, "左"), left) { SourcePath = sourcePaths.Left, SourceEncodingName = sourcePaths.LeftEncodingName },
                 new(InputTitle(1, project.BaseDescription, baseName, "中央"), ancestor) { SourcePath = sourcePaths.Base, SourceEncodingName = sourcePaths.BaseEncodingName },
                 new(InputTitle(2, project.RightDescription, rightName, "右"), right) { SourcePath = sourcePaths.Right, SourceEncodingName = sourcePaths.RightEncodingName }], token, snapshots, logEnvironment);
            ValidateSize(independent);
            return independent;
        }
        var documents = new List<ReportDocument> { new(InputTitle(0, project.LeftDescription, leftName, "左"), left)
            { SourcePath = sourcePaths.Left, SourceEncodingName = sourcePaths.LeftEncodingName } };
        if (ancestor is not null) documents.Add(new(InputTitle(1, project.BaseDescription, baseName, "共通の祖先"), ancestor)
            { SourcePath = sourcePaths.Base, SourceEncodingName = sourcePaths.BaseEncodingName });
        documents.Add(new(InputTitle(2, project.RightDescription, rightName, "右"), right)
            { SourcePath = sourcePaths.Right, SourceEncodingName = sourcePaths.RightEncodingName });
        var settings = Options(project, logEnvironment);
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

    public static Task ExportAsync(ComparisonWorkspace workspace, int entryIndex, string output,
        string? sourceProject = null, CancellationToken token = default,
        int? leftFrame = null, int? rightFrame = null, double? imageThreshold = null, int? middleFrame = null,
        ImageOffset? leftOffset = null, ImageOffset? middleOffset = null, ImageOffset? rightOffset = null,
        int? insertionDeletionMode = null, double? highlightAlpha = null)
        => ExportWithWipeAsync(workspace, entryIndex, output, sourceProject, token, leftFrame, rightFrame, imageThreshold, middleFrame,
            leftOffset, middleOffset, rightOffset, insertionDeletionMode, highlightAlpha, null);

    internal static async Task ExportWithWipeAsync(ComparisonWorkspace workspace, int entryIndex, string output,
        string? sourceProject = null, CancellationToken token = default,
        int? leftFrame = null, int? rightFrame = null, double? imageThreshold = null, int? middleFrame = null,
        ImageOffset? leftOffset = null, ImageOffset? middleOffset = null, ImageOffset? rightOffset = null,
        int? insertionDeletionMode = null, double? highlightAlpha = null, ImageWipeSnapshot? wipe = null,
        ImageDisplayOptions? displayOptions = null, ILineTransformLogEnvironment? logEnvironment = null)
    {
        _ = WorkspaceStore.SerializeWorkspace(workspace);
        if ((uint)entryIndex >= (uint)workspace.Entries.Length) throw new ArgumentOutOfRangeException(nameof(entryIndex), "比較の番号が範囲外です。");
        var entries = workspace.Entries.Select(WorkspaceStore.CloneProject).ToArray();
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
            if (insertionDeletionMode.HasValue) settings = settings with { InsertionDeletionMode = insertionDeletionMode.Value };
            if (middleImage is null && middleOffset.HasValue) throw new ArgumentException("中央の画像位置は三者比較だけです。");
            settings = settings with { LeftOffset = leftOffset ?? settings.LeftOffset,
                MiddleOffset = middleOffset ?? settings.MiddleOffset, RightOffset = rightOffset ?? settings.RightOffset };
            ImageViewSettings.Validate(settings);
            if (!selected && !settings.ReportAllFrames) numbers = settings.FrameNumbers(middleImage is not null);
            string[] titles = middleImage is null ? [project.LeftDescription ?? project.LeftPath, project.RightDescription ?? project.RightPath]
                : [project.LeftDescription ?? project.LeftPath, project.BaseDescription ?? project.BasePath, project.RightDescription ?? project.RightPath];
            var imageHtml = await Task.Run(() => ImageReport.Create(new(images, imageThreshold ?? settings.Threshold, numbers,
                ShowDifferences: settings.ShowDifferences, Orientations: settings.Orientations(middleImage is not null), BlockSize: settings.BlockSize,
                Offsets: settings.Offsets(middleImage is not null), InsertionDeletionMode: settings.InsertionDeletionMode,
                HighlightAlpha: highlightAlpha ?? settings.HighlightAlpha, Wipe: wipe,
                DisplaySettings: displayOptions?.Settings(settings.OverlayOpacity, settings.ShowDifferences, settings.BlockSize,
                    imageThreshold ?? settings.Threshold, highlightAlpha ?? settings.HighlightAlpha, wipe: wipe)), titles, token), token).ConfigureAwait(false);
            await SaveAsync(target, imageHtml, entries, sourceProject, token).ConfigureAwait(false);
            return;
        }
        if (leftFrame.HasValue || rightFrame.HasValue || imageThreshold.HasValue || middleFrame.HasValue
            || leftOffset.HasValue || middleOffset.HasValue || rightOffset.HasValue || insertionDeletionMode.HasValue || highlightAlpha.HasValue || wipe is not null || displayOptions?.Specified == true)
            throw new ArgumentException("フレーム・閾値のレポート指定は画像比較にだけ使用できます。");
        if (!IsTextual(project)) throw new InvalidOperationException("この形式の単体HTMLレポートは未対応です。");
        var left = await ProjectInputReader.ReadTextAsync(project, 0, token).ConfigureAwait(false);
        var ancestor = !ProjectInputs.HasBase(project) ? null : await ProjectInputReader.ReadTextAsync(project, 1, token).ConfigureAwait(false);
        var right = await ProjectInputReader.ReadTextAsync(project, 2, token).ConfigureAwait(false);
        var snapshots = ProjectInputs.IsIndependentText(project) ? new[] { left, ancestor!, right } : null;
        var html = await Task.Run(() => Create(project, left.Text, ancestor?.Text, right.Text, token, snapshots: snapshots,
            sourcePaths: TextComparisonSources.ForProject(project, left, ancestor, right), logEnvironment: logEnvironment), token).ConfigureAwait(false);
        await SaveAsync(target, html, entries, sourceProject, token).ConfigureAwait(false);
    }

    internal static async Task SaveAsync(string output, string html, IEnumerable<ComparisonProject> protectedEntries,
        string? sourceProject = null, CancellationToken token = default, Action<string, Action>? outputPublisher = null)
    {
        ValidateSize(html);
        var entries = protectedEntries.ToArray();
        var target = ValidateTarget(output, entries, sourceProject);
        token.ThrowIfCancellationRequested();
        await TextDocument.Create().SaveAsync(target, html, token, (temporary, destination, cancellation) =>
        {
            void Publish()
            {
                cancellation.ThrowIfCancellationRequested();
                ValidateTarget(destination, entries, sourceProject);
                if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
                    throw new UnauthorizedAccessException("読み取り専用のファイルは保存できません。");
                cancellation.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: true);
            }
            if (outputPublisher is null) Publish(); else outputPublisher(destination, Publish);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    private static void ValidateSize(string html)
    {
        if (Encoding.UTF8.GetByteCount(html) > MaximumBytes) throw new InvalidDataException("HTMLレポートは32 MiBまでです。");
    }

    private static string ValidateTarget(string output, IEnumerable<ComparisonProject> entries, string? sourceProject)
    {
        var target = ValidateLocal(output);
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("レポートの保存先フォルダーがありません。");
        var projects = entries.ToArray();
        ProjectInputs.EnsureOutput(target, projects, sourceProject);
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
