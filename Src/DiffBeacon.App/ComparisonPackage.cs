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
        IReadOnlyList<int>? selectedIndices = null, CancellationToken token = default, string? sourceProject = null,
        IReadOnlyDictionary<int, IReadOnlyList<string>>? textSnapshots = null)
        => CreateWithImageDisplaysAsync(workspace, output, options, selectedIndices, token, sourceProject, null, textSnapshots);

    internal static Task CreateWithImageDisplaysAsync(ComparisonWorkspace workspace, string output, ComparisonPackageOptions options,
        IReadOnlyList<int>? selectedIndices, CancellationToken token, string? sourceProject, IReadOnlyDictionary<int, ImageReportDisplayCapture>? displays,
        IReadOnlyDictionary<int, IReadOnlyList<string>>? textSnapshots = null)
    {
        // UI の値を await 前に確定し、編集中の配列・辞書をバックグラウンドで共有しない。
        WorkspaceStore.SerializeWorkspace(workspace);
        // DTOは本文を持たない。捕捉本文を渡すcallerの未保存無題を空入力として包装しない。
        foreach (var capture in textSnapshots ?? new Dictionary<int, IReadOnlyList<string>>())
        {
            if (capture.Key < 0 || capture.Key >= workspace.Entries.Length || capture.Value is null || capture.Value.Count != 3)
                throw new InvalidDataException("包装のText本文snapshotが不正です。");
            for (var side = 0; side < 3; side++)
                if (capture.Value[side] is null || ProjectInputs.IsUntitled(workspace.Entries[capture.Key], side) && capture.Value[side].Length != 0)
                    throw new InvalidOperationException("未保存の無題Textはファイルへ保存してから包装してください。");
        }
        var clone = workspace with { Entries = workspace.Entries.Select(WorkspaceStore.CloneProject).ToArray() };
        var indices = selectedIndices?.ToArray() ?? Enumerable.Range(0, clone.Entries.Length).ToArray();
        if (indices.Length == 0 || indices.Distinct().Count() != indices.Length || indices.Any(index => index < 0 || index >= clone.Entries.Length))
            throw new ArgumentException("包装する比較を重複なく1件以上選択してください。");
        if (!options.IncludeDocuments && !options.IncludeReport && !options.IncludePatch && !options.IncludeProject)
            throw new ArgumentException("文書・レポート・パッチ・プロジェクトのいずれかを含めてください。");
        var capturedDisplays = displays?.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (capturedDisplays is not null) foreach (var display in capturedDisplays.Values) display.Settings.Validate();
        return Task.Run(() => Create(clone, output, options, indices, token, sourceProject, capturedDisplays), token);
    }

    private static void Create(ComparisonWorkspace workspace, string output, ComparisonPackageOptions options,
        int[] indices, CancellationToken token, string? sourceProject, IReadOnlyDictionary<int, ImageReportDisplayCapture>? displays)
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
        ProjectInputs.EnsureOutput(target, workspace.Entries, sourceProject);
        var sources = new string[selected.Length][];
        for (var i = 0; i < selected.Length; i++)
        {
            var project = selected[i];
            if (options.IncludePatch && (ProjectInputs.IsIndependentText(project) || Enumerable.Range(0, 3).Any(side => ProjectInputs.IsUntitled(project, side))))
                throw new InvalidOperationException("独立三者Textのパッチ包装は未対応です。");
            if (!ProjectInputs.IsUntitled(project, 0) && string.IsNullOrWhiteSpace(SidePath(project, 0))
                || !ProjectInputs.IsUntitled(project, 2) && string.IsNullOrWhiteSpace(SidePath(project, 2)))
                throw new InvalidOperationException("包装前に左右の文書をファイルへ保存してください。");
            if (ProjectInputs.HasArchives(project) && !ProjectReport.IsTextual(project) && (options.IncludeReport || options.IncludePatch))
                throw new InvalidOperationException("内包Binary／ArchiveのHTML・パッチ包装は未対応です。");
            sources[i] = Paths(project).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
            foreach (var source in sources[i].Where(path => !IsUrl(path)))
            {
                ValidateLocal(source);
                if (FolderComparisons.IsDirectory(source)) throw new InvalidOperationException("フォルダー比較の包装は未対応です。フォルダーのアーカイブ作成を使用してください。");
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
        var ownedDirectories = new List<string>();
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
                // 裸圧縮の合成格納名も保つため、入力ごとの所有ディレクトリへ元basenameのまま確定する。
                var snapshotDirectory = Path.Combine(stage, inputs.Count.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
                ValidateLocal(snapshotDirectory);
                if (Directory.Exists(snapshotDirectory) || File.Exists(snapshotDirectory))
                    throw new IOException("包装の入力snapshotディレクトリが既に存在します。");
                Directory.CreateDirectory(snapshotDirectory); ownedDirectories.Add(snapshotDirectory);
                var snapshot = Path.Combine(snapshotDirectory, Path.GetFileName(source));
                ValidateLocal(snapshot);
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
                var project = selected[i]; var three = ProjectInputs.HasBase(project);
                var paths = new string[3]; var files = new Input?[3];
                for (var side = 0; side < 3; side++)
                {
                    var source = SidePath(project, side);
                    if (string.IsNullOrWhiteSpace(source) || IsUrl(source)) { paths[side] = source; continue; }
                    var prefix = three ? (side + 1).ToString() : side == 0 ? "original" : "altered";
                    var relative = CommonRelative(common[side], source);
                    var input = Snapshot(source, prefix + "/" + relative); files[side] = input;
                    if (ProjectInputs.Archive(project, side) is { } archiveInput)
                    {
                        if (!StringComparer.Ordinal.Equals(archiveInput.RootSha256?.ToUpperInvariant(), input.Hash))
                            throw new InvalidDataException("確定したアーカイブ原本と包装snapshotのSHAが一致しません。");
                        ProjectInputReader.ValidateSnapshot(archiveInput, input.Snapshot, token);
                    }
                    paths[side] = options.IncludeDocuments ? input.Name : Path.GetFullPath(source);
                }
                var filter = project.FileFilterPath;
                if (!string.IsNullOrWhiteSpace(filter) && options.IncludeProject)
                {
                    if (IsUrl(filter)) throw new InvalidDataException("ファイルフィルターはローカルのファイルを指定してください。");
                    filter = Snapshot(filter, $"filters/{i + 1}-" + Path.GetFileName(filter)).Name;
                }
                ArchiveProjectInput? PackedInput(int side)
                {
                    if (ProjectInputs.Archive(project, side) is not { } input) return null;
                    var savedInput = input.Copy(); savedInput.RootPath = paths[side];
                    foreach (var copy in savedInput.WorkingDocuments ?? [])
                    {
                        var bytes = copy.Bytes ?? throw new InvalidDataException("作業文書のsnapshotを読み込んでください。");
                        var name = "working/" + copy.Sha256 + copy.AssetExtension;
                        if (!names.ContainsKey(name))
                        {
                            if (bytes.Length > MaximumInputBytes - total) throw new InvalidDataException("包装する入力の合計は1 GiBまでです。");
                            var savedPath = Path.Combine(stage, "working-" + copy.Sha256 + ".tmp"); owned.Add(savedPath);
                            using (var stream = new FileStream(savedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes);
                            var workingInput = new Input("", name, savedPath, DateTime.UnixEpoch, bytes.Length, copy.Sha256);
                            names.Add(name, workingInput); inputs.Add(workingInput); total += bytes.Length;
                        }
                        copy.SnapshotPath = name;
                    }
                    return savedInput;
                }
                packed[i] = project with
                {
                    LeftPath = project.LeftArchiveInput is null ? paths[0] : "", BasePath = project.BaseArchiveInput is null ? paths[1] : "",
                    RightPath = project.RightArchiveInput is null ? paths[2] : "", FileFilterPath = filter,
                    LeftArchiveInput = PackedInput(0), BaseArchiveInput = PackedInput(1), RightArchiveInput = PackedInput(2)
                };
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
                TextDocument? leftText = null, rightText = null;
                if (textMode && (options.IncludePatch || options.IncludeReport))
                {
                    leftText = ProjectInputReader.ReadTextAsync(project, 0, token, left?.Snapshot).GetAwaiter().GetResult();
                    rightText = ProjectInputReader.ReadTextAsync(project, 2, token, right?.Snapshot).GetAwaiter().GetResult();
                    a = leftText.Text; b = rightText.Text;
                }
                if (options.IncludePatch && textMode && project.Mode.ToLowerInvariant() is not ("json" or "5"))
                {
                    string PatchName(int side, Input input) => ProjectInputs.Archive(project, side)?.MissingEntryChain is not null
                        ? "/dev/null" : ProjectInputs.Archive(project, side)?.LeafEntry is { } leaf
                            ? (side == 0 ? "original/" : "altered/") + leaf : input.Name;
                    patches.Append(UnifiedPatch.Create(a!, b!, PatchName(0, left!), PatchName(2, right!),
                        leftExists: ProjectInputs.Archive(project, 0)?.MissingEntryChain is null,
                        rightExists: ProjectInputs.Archive(project, 2)?.MissingEntryChain is null));
                    if (patches.Length > MaximumGeneratedBytes) throw new InvalidDataException("パッチの上限を超えました。");
                }
                if (options.IncludeReport)
                {
                    var title = project.LeftDescription ?? (project.LeftArchiveInput is null
                        ? Path.GetFileName(project.LeftPath) : ProjectInputs.Caption(project, 0));
                    indexReport.Append("<li><a href=\"report.files/").Append(i + 1).Append(".html\">").Append(WebUtility.HtmlEncode(title)).Append("</a></li>");
                    var ancestorDocument = textMode && ProjectInputs.HasBase(project)
                        ? ProjectInputReader.ReadTextAsync(project, 1, token, pairInputs[i][1]?.Snapshot).GetAwaiter().GetResult() : null;
                    var ancestor = ancestorDocument?.Text;
                    string report;
                    if (ProjectReport.IsImage(project))
                    {
                        var display = displays?.GetValueOrDefault(indices[i]);
                        var staged = pairInputs[i][1] is null ? new[] { left!, right! } : new[] { left!, pairInputs[i][1]!, right! };
                        if (display is not null && (staged.Length != display.InputHashes.Count || staged.Where((input, side) =>
                            !StringComparer.OrdinalIgnoreCase.Equals(input.Hash, display.InputHashes[side])).Any()))
                            throw new InvalidDataException("表示した画像原本と包装stagingのSHAが一致しません。");
                        // 包装する原本と同じ確定内容を使い、元パスの再読込みを避ける。
                        var leftImage = ImageComparisonEngine.OpenAsync(left!.Snapshot, token).GetAwaiter().GetResult();
                        var rightImage = ImageComparisonEngine.OpenAsync(right!.Snapshot, token).GetAwaiter().GetResult();
                        var middleInput = pairInputs[i][1];
                        var middleImage = middleInput is null ? null : ImageComparisonEngine.OpenAsync(middleInput.Snapshot, token).GetAwaiter().GetResult();
                        var settings = project.ImageSettings;
                        display?.ValidateSettings(settings, staged.Length);
                        report = ImageReport.Create(new(middleImage is null ? [leftImage, rightImage] : [leftImage, middleImage, rightImage],
                            settings.Threshold, settings.ReportAllFrames ? null : settings.FrameNumbers(middleImage is not null),
                            ShowDifferences: settings.ShowDifferences, Orientations: settings.Orientations(middleImage is not null), BlockSize: settings.BlockSize,
                            Offsets: settings.Offsets(middleImage is not null), InsertionDeletionMode: settings.InsertionDeletionMode,
                            HighlightAlpha: settings.HighlightAlpha, Wipe: display?.Sample.Wipe,
                            SelectedDiffIndex: display?.Settings.SelectedDiffIndex ?? -1, DisplayCapture: display),
                            middleImage is null ? [project.LeftDescription ?? left.Name, project.RightDescription ?? right.Name]
                                : [project.LeftDescription ?? left.Name, project.BaseDescription ?? middleInput!.Name, project.RightDescription ?? right.Name], token);
                    }
                    else report = textMode ? ProjectReport.Create(project, a!, ancestor, b!, token, left?.Name, pairInputs[i][1]?.Name, right?.Name,
                        ProjectInputs.IsIndependentText(project) ? new[] { leftText!, ancestorDocument!, rightText! } : null)
                        : MetadataReport(project, pairInputs[i]);
                    Generated($"report.files/{i + 1}.html", report);
                }
            }
            if (options.IncludeReport) Generated("report.html", indexReport.Append("</ol>").ToString());
            if (options.IncludePatch) Generated("patch.diff", patches.ToString());
            if (options.IncludeProject)
            {
                var active = Array.IndexOf(indices, workspace.ActiveEntryIndex);
                GeneratedBytes("project.json", WorkspaceStore.SerializeWorkspace(new ComparisonWorkspace { FormatVersion = workspace.FormatVersion is 8 or 9 ? workspace.FormatVersion : 1, Entries = packed, ActiveEntryIndex = Math.Max(0, active) }));
            }
            IEnumerable<ManagedArchiveWriteEntry> Content()
            {
                foreach (var input in inputs)
                {
                    token.ThrowIfCancellationRequested();
                    if (!options.IncludeDocuments && !input.Name.StartsWith("filters/", StringComparison.Ordinal)
                        && !(options.IncludeProject && input.Name.StartsWith("working/", StringComparison.Ordinal))) continue;
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
            for (var index = ownedDirectories.Count - 1; index >= 0; index--)
            {
                var directory = ownedDirectories[index]; ValidateLocal(directory);
                Directory.Delete(directory, recursive: false);
            }
            Directory.Delete(stage, recursive: false);
        }
    }

    private static IEnumerable<string> Paths(ComparisonProject project)
    { yield return SidePath(project, 0); if (ProjectInputs.HasBase(project)) yield return SidePath(project, 1); yield return SidePath(project, 2); }
    private static string SidePath(ComparisonProject project, int side) => ProjectInputs.PathFor(project, side);
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
