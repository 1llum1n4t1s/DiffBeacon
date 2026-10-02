using System.Net;
using System.Security.Cryptography;
using System.Text;
using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public sealed record ComparisonPackageOptions(bool IncludeDocuments = true, bool IncludeReport = false,
    bool IncludePatch = false, bool IncludeProject = true);

public static class ComparisonPackage
{
    private const long MaximumFileBytes = 256L * 1024 * 1024;
    private const long MaximumInputBytes = 1024L * 1024 * 1024;
    private const int MaximumGeneratedBytes = 32 * 1024 * 1024;
    private sealed record Input(string Source, string Name, string Snapshot, DateTime Modified, long Size, string Hash);

    public static Task CreateAsync(ComparisonWorkspace workspace, string output, ComparisonPackageOptions options,
        IReadOnlyList<int>? selectedIndices = null, CancellationToken token = default, string? sourceProject = null)
    {
        // UI の値を await 前に確定し、編集中の配列・辞書をバックグラウンドで共有しない。
        WorkspaceStore.SerializeWorkspace(workspace);
        var clone = workspace with { Entries = workspace.Entries.Select(project => project with
        { SubstitutionRules = project.SubstitutionRules.ToArray(), LegacySettings = new(project.LegacySettings), ImageSettings = project.ImageSettings with { } }).ToArray() };
        var indices = selectedIndices?.ToArray() ?? Enumerable.Range(0, clone.Entries.Length).ToArray();
        if (indices.Length == 0 || indices.Distinct().Count() != indices.Length || indices.Any(index => index < 0 || index >= clone.Entries.Length))
            throw new ArgumentException("包装する比較を重複なく1件以上選択してください。");
        if (!options.IncludeDocuments && !options.IncludeReport && !options.IncludePatch && !options.IncludeProject)
            throw new ArgumentException("文書・レポート・パッチ・プロジェクトのいずれかを含めてください。");
        return Task.Run(() => Create(clone, output, options, indices, token, sourceProject), token);
    }

    private static void Create(ComparisonWorkspace workspace, string output, ComparisonPackageOptions options,
        int[] indices, CancellationToken token, string? sourceProject)
    {
        token.ThrowIfCancellationRequested();
        var target = ValidateLocal(output);
        ProjectReport.EnsureReadOnlyDirectories(target, workspace.Entries);
        if (!ManagedArchive.SupportsOutput(target)) throw new InvalidDataException("包装先には対応するアーカイブ拡張子を指定してください。");
        var parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("包装先のディレクトリがありません。");
        if (sourceProject is not null && ArchivePaths.SameFile(target, sourceProject)) throw new IOException("元プロジェクトを包装先に指定できません。");
        var selected = indices.Select(index => workspace.Entries[index]).ToArray();
        if (selected.Any(project => project.Mode.ToLowerInvariant() is "folder" or "2"))
            throw new InvalidOperationException("フォルダー比較の包装は未対応です。フォルダーのアーカイブ作成を使用してください。");
        foreach (var project in workspace.Entries)
            foreach (var path in Paths(project).Append(project.FileFilterPath ?? "").Where(path => !string.IsNullOrWhiteSpace(path) && !IsUrl(path)))
                if (ArchivePaths.SameFile(target, path)) throw new IOException("比較元を包装先に指定できません。");
        var sources = new string[selected.Length][];
        for (var i = 0; i < selected.Length; i++)
        {
            var project = selected[i];
            if (string.IsNullOrWhiteSpace(project.LeftPath) || string.IsNullOrWhiteSpace(project.RightPath))
                throw new InvalidOperationException("包装前に左右の文書をファイルへ保存してください。");
            sources[i] = Paths(project).ToArray();
            foreach (var source in sources[i].Where(path => !IsUrl(path)))
            {
                ValidateLocal(source);
                if (Directory.Exists(source)) throw new InvalidOperationException("フォルダー比較の包装は未対応です。フォルダーのアーカイブ作成を使用してください。");
                if (!File.Exists(source)) throw new FileNotFoundException("包装する文書がありません。", source);
                if (new FileInfo(source).Length > MaximumFileBytes) throw new InvalidDataException("包装するファイルは256 MiBまでです。");
            }
            if ((options.IncludeReport || options.IncludePatch) && sources[i].Any(IsUrl))
                throw new InvalidOperationException("URL文書のレポート・パッチはローカルへ保存してから包装してください。");
        }
        // 所有する一時ディレクトリだけに文書を確定し、同じバイト列で各成果物を作る。
        var stage = Path.Combine(parent, ".diffbeacon-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var owned = new List<string>();
        try
        {
            var inputs = new List<Input>();
            var names = new Dictionary<string, Input>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            Input Snapshot(string source, string name)
            {
                token.ThrowIfCancellationRequested(); ValidateLocal(source);
                var key = name.Normalize(NormalizationForm.FormC);
                if (names.TryGetValue(key, out var previous))
                {
                    if (ArchivePaths.SameFile(previous.Source, source)) return previous;
                    throw new InvalidDataException("別の文書が同じ格納名になります。選択した文書名を確認してください。");
                }
                var info = new FileInfo(source);
                var initialSize = info.Length; var initialModified = info.LastWriteTimeUtc;
                var snapshot = Path.Combine(stage, inputs.Count.ToString("D4") + Path.GetExtension(source));
                owned.Add(snapshot);
                using (var original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var copy = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (original.Length > MaximumFileBytes || original.Length > MaximumInputBytes - total)
                        throw new InvalidDataException("包装する入力の合計は1 GiBまでです。");
                    var buffer = new byte[65536]; long copied = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested(); var count = original.Read(buffer);
                        if (count == 0) break;
                        copied += count;
                        if (copied > MaximumFileBytes || copied > MaximumInputBytes - total) throw new InvalidDataException("包装する入力のサイズ上限を超えました。");
                        copy.Write(buffer, 0, count);
                    }
                    if (copied != initialSize || File.GetLastWriteTimeUtc(source) != initialModified)
                        throw new IOException("包装の読込み中に文書が変更されました。もう一度実行してください。");
                    total += copied;
                }
                using var read = File.OpenRead(snapshot);
                var input = new Input(source, name, snapshot, info.LastWriteTimeUtc, read.Length, Convert.ToHexString(SHA256.HashData(read)));
                names.Add(key, input); inputs.Add(input); return input;
            }
            var common = Enumerable.Range(0, 3).Select(side => CommonParent(selected.Select(project => SidePath(project, side)).Where(path => !string.IsNullOrWhiteSpace(path) && !IsUrl(path)))).ToArray();
            var packed = new ComparisonProject[selected.Length];
            var pairInputs = new Input?[selected.Length][];
            for (var i = 0; i < selected.Length; i++)
            {
                var project = selected[i]; var three = !string.IsNullOrWhiteSpace(project.BasePath);
                var paths = new string[3]; var files = new Input?[3];
                for (var side = 0; side < 3; side++)
                {
                    var source = SidePath(project, side);
                    if (string.IsNullOrWhiteSpace(source) || IsUrl(source)) { paths[side] = source; continue; }
                    var prefix = three ? (side + 1).ToString() : side == 0 ? "original" : "altered";
                    var relative = CommonRelative(common[side], source);
                    var input = Snapshot(source, prefix + "/" + relative); files[side] = input;
                    paths[side] = options.IncludeDocuments ? input.Name : Path.GetFullPath(source);
                }
                var filter = project.FileFilterPath;
                if (!string.IsNullOrWhiteSpace(filter) && options.IncludeProject)
                {
                    if (IsUrl(filter)) throw new InvalidDataException("ファイルフィルターはローカルのファイルを指定してください。");
                    filter = Snapshot(filter, $"filters/{i + 1}-" + Path.GetFileName(filter)).Name;
                }
                packed[i] = project with { LeftPath = paths[0], BasePath = paths[1], RightPath = paths[2], FileFilterPath = filter };
                pairInputs[i] = files;
            }
            var generated = new List<(string Name, string Snapshot)>(); long generatedBytes = 0;
            void GeneratedBytes(string name, byte[] bytes)
            {
                token.ThrowIfCancellationRequested();
                if (bytes.Length > MaximumGeneratedBytes || bytes.Length > MaximumInputBytes - total - generatedBytes)
                    throw new InvalidDataException("生成物は各32 MiB、入力と生成物の合計は1 GiBまでです。");
                var snapshot = Path.Combine(stage, "generated-" + generated.Count.ToString("D4") + ".tmp"); owned.Add(snapshot);
                using (var file = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(bytes);
                generated.Add((name, snapshot)); generatedBytes += bytes.Length;
            }
            void Generated(string name, string text)
            {
                if (Encoding.UTF8.GetByteCount(text) > MaximumGeneratedBytes) throw new InvalidDataException("生成するレポート・パッチは各32 MiBまでです。");
                GeneratedBytes(name, new UTF8Encoding(false).GetBytes(text));
            }
            var patches = new StringBuilder(); var indexReport = new StringBuilder("<!doctype html><meta charset=\"utf-8\"><title>比較レポート</title><h1>比較レポート</h1><ol>");
            for (var i = 0; i < selected.Length; i++)
            {
                token.ThrowIfCancellationRequested(); var project = selected[i];
                var left = pairInputs[i][0]; var right = pairInputs[i][2];
                var textMode = ProjectReport.IsTextual(project);
                string? a = null, b = null;
                if (textMode && (options.IncludePatch || options.IncludeReport))
                {
                    a = TextDocument.LoadAsync(left!.Snapshot, token).GetAwaiter().GetResult().Text;
                    b = TextDocument.LoadAsync(right!.Snapshot, token).GetAwaiter().GetResult().Text;
                }
                if (options.IncludePatch && textMode && project.Mode.ToLowerInvariant() is not ("json" or "5"))
                {
                    patches.Append(UnifiedPatch.Create(a!, b!, left!.Name, right!.Name));
                    if (patches.Length > MaximumGeneratedBytes) throw new InvalidDataException("パッチの上限を超えました。");
                }
                if (options.IncludeReport)
                {
                    var title = project.LeftDescription ?? Path.GetFileName(project.LeftPath);
                    indexReport.Append("<li><a href=\"report.files/").Append(i + 1).Append(".html\">").Append(WebUtility.HtmlEncode(title)).Append("</a></li>");
                    var ancestor = textMode && pairInputs[i][1] is { } middle
                        ? TextDocument.LoadAsync(middle.Snapshot, token).GetAwaiter().GetResult().Text : null;
                    string report;
                    if (ProjectReport.IsImage(project))
                    {
                        // 包装する原本と同じ確定内容を使い、元パスの再読込みを避ける。
                        var leftImage = ImageComparisonEngine.OpenAsync(left!.Snapshot, token).GetAwaiter().GetResult();
                        var rightImage = ImageComparisonEngine.OpenAsync(right!.Snapshot, token).GetAwaiter().GetResult();
                        var middleInput = pairInputs[i][1];
                        var middleImage = middleInput is null ? null : ImageComparisonEngine.OpenAsync(middleInput.Snapshot, token).GetAwaiter().GetResult();
                        var settings = project.ImageSettings;
                        report = ImageReport.Create(new(middleImage is null ? [leftImage, rightImage] : [leftImage, middleImage, rightImage],
                            settings.Threshold, settings.ReportAllFrames ? null : settings.FrameNumbers(middleImage is not null),
                            ShowDifferences: settings.ShowDifferences, Orientations: settings.Orientations(middleImage is not null), BlockSize: settings.BlockSize,
                            Offsets: settings.Offsets(middleImage is not null), InsertionDeletionMode: settings.InsertionDeletionMode),
                            middleImage is null ? [project.LeftDescription ?? left.Name, project.RightDescription ?? right.Name]
                                : [project.LeftDescription ?? left.Name, project.BaseDescription ?? middleInput!.Name, project.RightDescription ?? right.Name], token);
                    }
                    else report = textMode ? ProjectReport.Create(project, a!, ancestor, b!, token, left!.Name, pairInputs[i][1]?.Name, right!.Name)
                        : MetadataReport(project, pairInputs[i]);
                    Generated($"report.files/{i + 1}.html", report);
                }
            }
            if (options.IncludeReport) Generated("report.html", indexReport.Append("</ol>").ToString());
            if (options.IncludePatch) Generated("patch.diff", patches.ToString());
            if (options.IncludeProject)
            {
                var active = Array.IndexOf(indices, workspace.ActiveEntryIndex);
                GeneratedBytes("project.json", WorkspaceStore.SerializeWorkspace(new ComparisonWorkspace { Entries = packed, ActiveEntryIndex = Math.Max(0, active) }));
            }
            IEnumerable<ManagedArchiveWriteEntry> Content()
            {
                foreach (var input in inputs)
                {
                    token.ThrowIfCancellationRequested();
                    if (!options.IncludeDocuments && !input.Name.StartsWith("filters/", StringComparison.Ordinal)) continue;
                    yield return new(input.Name, File.ReadAllBytes(input.Snapshot), input.Modified);
                }
                foreach (var entry in generated) { token.ThrowIfCancellationRequested(); yield return new(entry.Name, File.ReadAllBytes(entry.Snapshot)); }
            }
            new ManagedArchive().WriteArchive(target, Content(), token);
        }
        finally
        {
            // 生成したファイル名だけを解除する。他の一時物・利用者のディレクトリへは触れない。
            ValidateLocal(stage);
            foreach (var path in owned) { ValidateLocal(path); if (File.Exists(path)) File.Delete(path); }
            Directory.Delete(stage, recursive: false);
        }
    }

    private static IEnumerable<string> Paths(ComparisonProject project)
    { yield return project.LeftPath; if (!string.IsNullOrWhiteSpace(project.BasePath)) yield return project.BasePath; yield return project.RightPath; }
    private static string SidePath(ComparisonProject project, int side) => side switch { 0 => project.LeftPath, 1 => project.BasePath, _ => project.RightPath };
    private static bool IsUrl(string path) => Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    private static string ValidateLocal(string path)
    {
        var full = Path.GetFullPath(path); string? current = full;
        while (current is not null)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null || info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("包装のパスにリンクを使用できません。");
            current = Path.GetDirectoryName(current);
        }
        return full;
    }
    private static string? CommonParent(IEnumerable<string> paths)
    {
        string? common = null;
        foreach (var path in paths)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
            if (common is null) { common = parent; continue; }
            while (common is not null)
            {
                var relative = Path.GetRelativePath(common, parent);
                if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) break;
                common = Path.GetDirectoryName(common);
            }
            if (common is null) return null;
        }
        return common;
    }
    private static string CommonRelative(string? common, string source)
    {
        var full = Path.GetFullPath(source);
        // 別ボリュームの場合は旧方式と同じくドライブを名前に含め、絶対名を格納しない。
        var relative = common is null ? full.Replace(':', '_').TrimStart('/', '\\') : Path.GetRelativePath(common, full);
        return relative.Replace('\\', '/');
    }
    private static string MetadataReport(ComparisonProject project, Input?[] inputs)
    {
        var report = new StringBuilder("<!doctype html><meta charset=\"utf-8\"><title>文書レポート</title><h1>文書レポート</h1><p>形式: ")
            .Append(WebUtility.HtmlEncode(project.Mode)).Append("</p><p>元バイト列: ").Append(inputs[0]?.Hash == inputs[2]?.Hash ? "一致" : "相違")
            .Append("</p><table><tr><th>側</th><th>文書</th><th>バイト</th><th>SHA-256</th></tr>");
        for (var side = 0; side < inputs.Length; side++) if (inputs[side] is { } input)
            report.Append("<tr><td>").Append(side + 1).Append("</td><td>").Append(WebUtility.HtmlEncode(input.Name)).Append("</td><td>").Append(input.Size)
                .Append("</td><td>").Append(input.Hash).Append("</td></tr>");
        return report.Append("</table><p>この形式の詳細な比較レポートは未対応です。</p>").ToString();
    }
}
