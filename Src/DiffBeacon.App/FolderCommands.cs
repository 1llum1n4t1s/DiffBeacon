using System.Globalization;
using System.Text.Json;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class FolderCommands
{
    internal static DirectoryComparisonOptions ParseOptions(string[] args, int start,
        List<string>? selections = null, Action<string, string>? planOption = null,
        Func<string, FileFilter>? filterOption = null, Action<string>? middleOption = null)
    {
        var options = new DirectoryComparisonOptions();
        var excludes = new List<string>();
        var textFlags = new List<string>();
        var singleton = new HashSet<string>(StringComparer.Ordinal);
        for (var index = start; index < args.Length; index++)
        {
            var flag = args[index];
            if (flag is not ("--exclude" or "--select" or "--substitute") && !singleton.Add(flag))
                throw new ArgumentException($"重複した引数です: {flag}");
            var textArity = flag switch
            {
                "--ignore-case" or "--ignore-space" or "--ignore-blank" or "--ignore-numbers" or "--word-level" => 0,
                "--comments" or "--whitespace" or "--ignore-regex" or "--max-work" or "--eol" => 1,
                "--substitute" => 2,
                _ => -1
            };
            if (textArity >= 0)
            {
                if (index + textArity >= args.Length) throw new ArgumentException($"値が必要です: {flag}");
                textFlags.Add(flag);
                for (var operand = 0; operand < textArity; operand++) textFlags.Add(args[++index]);
                continue;
            }
            if (flag == "--no-recursive") { options = options with { Recursive = false }; continue; }
            if (flag == "--show-filtered") { options = options with { ShowFiltered = true }; continue; }
            if (++index >= args.Length) throw new ArgumentException($"値が必要です: {flag}");
            var value = args[index];
            switch (flag)
            {
                case "--filter":
                    options = options with { FileFilter = filterOption is null ? FileFilter.Load(value) : filterOption(value) };
                    break;
                case "--exclude": excludes.Add(value); break;
                case "--max-entries": options = options with { MaximumEntries = Positive(value) }; break;
                case "--max-depth": options = options with { MaximumDepth = Positive(value) }; break;
                case "--max-content-bytes": options = options with { MaximumContentBytes =
                    long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) && bytes > 0
                        ? bytes : throw new ArgumentException("共有読込上限は正数で指定してください。") }; break;
                case "--middle" when middleOption is not null:
                    if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("中央フォルダーのパスが必要です。");
                    middleOption(Path.GetFullPath(value)); break;
                case "--mode": options = options with { Mode = value switch
                {
                    "content" => DirectoryComparisonMode.Content,
                    "hash" => DirectoryComparisonMode.Hash,
                    "timestamp" => DirectoryComparisonMode.TimeAndSize,
                    _ => throw new ArgumentException("比較方式はcontent、hash、timestampです。")
                } }; break;
                case "--select" when selections is not null: selections.Add(value); break;
                case "--direction" or "--copy" when planOption is not null: planOption(flag, value); break;
                default: throw new ArgumentException($"未対応のフォルダー引数です: {flag}");
            }
        }
        return options with { ExcludePatterns = excludes.AsReadOnly(),
            TextOptions = textFlags.Count == 0 ? null : CommandLine.ParseOptions(textFlags.ToArray()) };

        static int Positive(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number : throw new ArgumentException("項目数・深度の上限は正数で指定してください。");
    }

    internal static Task<DirectoryComparisonResult> CompareAsync(string[] args, CancellationToken token)
    {
        if (args.Length < 3) throw new ArgumentException("--directory LEFT RIGHT [--middle MIDDLE] [folder options]");
        string? middle = null;
        var options = ParseOptions(args, 3, middleOption: value => middle = value);
        RequireContentLimitScope(args, middle, options);
        return middle is null ? FolderComparisons.CompareAsync(args[1], args[2], options, token)
            : FolderComparisons.CompareAsync(args[1], middle, args[2], options, token);
    }

    private static void RequireContentLimitScope(string[] args, string? middle, DirectoryComparisonOptions options)
    {
        if (args.Contains("--max-content-bytes", StringComparer.Ordinal)
            && (middle is null || options.Mode != DirectoryComparisonMode.Content))
            throw new ArgumentException("共有読込上限は中央フォルダーを指定した三者の内容比較で使用してください。");
    }

    internal static async Task<FolderCommandSelection> PrepareSelectionAsync(string[] args, CancellationToken token)
    {
        if (args.Length < 3) throw new ArgumentException("--folder-plan / --folder-sync LEFT RIGHT [--middle MIDDLE] --direction DIRECTION --copy all|diff --select RELATIVE [...]");
        DirectoryCopyDirection? direction = null;
        DirectoryCopyMode? mode = null;
        var selected = new List<string>();
        FolderFilterInput? filter = null;
        string? middle = null;
        var options = ParseOptions(args, 3, selected, (flag, value) =>
        {
            if (flag == "--direction") direction = value switch
            {
                "left-to-right" => DirectoryCopyDirection.LeftToRight, "right-to-left" => DirectoryCopyDirection.RightToLeft,
                "left-to-middle" => DirectoryCopyDirection.LeftToMiddle, "middle-to-left" => DirectoryCopyDirection.MiddleToLeft,
                "middle-to-right" => DirectoryCopyDirection.MiddleToRight, "right-to-middle" => DirectoryCopyDirection.RightToMiddle,
                _ => throw new ArgumentException("コピー方向は左・中央・右の異なる二側を指定してください。")
            };
            else mode = value switch
            {
                "all" => DirectoryCopyMode.All, "diff" => DirectoryCopyMode.DifferencesOnly,
                _ => throw new ArgumentException("コピー条件はallまたはdiffです。")
            };
        }, path => { token.ThrowIfCancellationRequested(); filter = new(path); return filter.Filter; }, value => middle = value);
        if (direction is null || mode is null) throw new ArgumentException("コピー方向とall/diffを明示してください。");
        filter?.RequireUnchanged();
        RequireContentLimitScope(args, middle, options);
        var model = middle is null ? await FolderComparisons.CompareAsync(args[1], args[2], options, token)
            : await FolderComparisons.CompareAsync(args[1], middle, args[2], options, token);
        filter?.RequireUnchanged();
        var selection = DirectoryCopyPlanner.Create(model, selected, direction.Value, mode.Value, token);
        var (_, destinationSide) = DirectorySideMapping.GetSides(direction.Value);
        var roots = model.IsThreeWay ? new[] { DirectorySide.Left, DirectorySide.Middle, DirectorySide.Right }
            : [DirectorySide.Left, DirectorySide.Right];
        return new(selection, filter, roots.Where(side => side != destinationSide)
            .Select(side => DirectorySideMapping.GetRoot(model, side)).ToArray());
    }

    internal static void WriteModel(Utf8JsonWriter writer, DirectoryComparisonResult model)
    {
        writer.WriteNumber("allEntryCount", model.AllEntries.Count);
        if (model.IsThreeWay) { writer.WriteBoolean("threeWay", true); writer.WriteString("middleRoot", model.MiddlePath); }
        writer.WriteStartArray("entries");
        foreach (var entry in model.Entries)
        {
            writer.WriteStartObject(); writer.WriteString("path", entry.RelativePath); writer.WriteString("status", entry.Status.ToString());
            writer.WriteString("kind", entry.EntryKind.ToString()); writer.WriteString("differenceKind", entry.Kind.ToString());
            writer.WriteBoolean("filtered", entry.IsFiltered);
            WriteSide(writer, "left", entry.LeftState); WriteSide(writer, "right", entry.RightState);
            if (entry.ThreeWay is { } three)
            {
                WriteSide(writer, "middle", entry.MiddleState);
                writer.WriteNumber("presence", (int)three.Presence);
                if (three.Classification is { } classification) writer.WriteString("classification", classification.ToString());
                else writer.WriteNull("classification");
                WritePair("middleLeft", three.MiddleLeft); WritePair("middleRight", three.MiddleRight); WritePair("leftRight", three.LeftRight);
            }
            writer.WriteStartArray("children"); foreach (var child in entry.Children) writer.WriteStringValue(child.RelativePath); writer.WriteEndArray();
            if (entry.Error is not null) writer.WriteString("error", entry.Error);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        void WritePair(string name, DirectoryPairComparison pair)
        {
            writer.WriteStartObject(name); writer.WriteString("status", pair.Status.ToString());
            if (pair.Error is not null) writer.WriteString("error", pair.Error);
            writer.WriteEndObject();
        }
    }

    internal static void WriteSelection(Utf8JsonWriter writer, DirectoryCopySelection selection)
    {
        writer.WriteString("sourceRoot", selection.SourceRoot); writer.WriteString("destinationRoot", selection.DestinationRoot);
        writer.WriteString("direction", selection.Direction.ToString()); writer.WriteString("copy", selection.Mode.ToString());
        writer.WriteStartArray("selected"); foreach (var selected in selection.SelectedPaths) writer.WriteStringValue(selected); writer.WriteEndArray();
        writer.WriteStartArray("candidates");
        foreach (var candidate in selection.Candidates)
        {
            writer.WriteStartObject(); writer.WriteString("path", candidate.RelativePath); writer.WriteBoolean("physicalDirectory", candidate.ExpandPhysicalDirectory);
            WriteSide(writer, "source", candidate.Source); WriteSide(writer, "destination", candidate.Destination); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteSide(Utf8JsonWriter writer, string name, DirectorySideSnapshot? side)
    {
        if (side is null) { writer.WriteNull(name); return; }
        writer.WriteStartObject(name); writer.WriteString("path", side.Path); writer.WriteString("kind", side.Kind.ToString());
        writer.WriteNumber("size", side.Size); writer.WriteString("lastWriteTimeUtc", side.LastWriteTimeUtc);
        writer.WriteBoolean("filtered", side.IsFiltered); writer.WriteString("scan", side.ScanState.ToString()); writer.WriteEndObject();
    }

    internal static void WriteExecution(Utf8JsonWriter writer, FolderCopyPlan plan, FolderCopyResult result)
    {
        writer.WriteString("sourceRoot", plan.SourceRoot); writer.WriteString("destinationRoot", plan.DestinationRoot);
        writer.WriteBoolean("succeeded", result.Succeeded); writer.WriteBoolean("cancelled", result.Cancelled);
        writer.WriteBoolean("mutationOccurred", result.MutationOccurred); writer.WriteNumber("published", result.PublishedCount);
        writer.WriteNumber("logicalBytes", plan.LogicalBytes); writer.WriteNumber("readBytes", result.ReadBytes); writer.WriteNumber("writeBytes", result.WriteBytes);
        writer.WriteNumber("destinationBytes", plan.DestinationBytes); writer.WriteNumber("plannedIoBytes", plan.PlannedIoBytes);
        writer.WriteNumber("streamDescriptors", plan.StreamDescriptors); writer.WriteNumber("streamNameCharacters", plan.StreamNameCharacters);
        writer.WriteBoolean("usesWindowsMetadata", plan.UsesWindowsMetadata);
        writer.WriteString("windowsMetadataQualification", plan.WindowsMetadataQualification);
        writer.WriteNumber("metadataRetainedBytes", result.MetadataRetainedBytes);
        writer.WriteNumber("metadataQueryBytes", result.MetadataQueryBytes);
        writer.WriteNumber("metadataNativeOperations", result.MetadataNativeOperations);
        if (result.Reason is { } reason) writer.WriteString("reason", reason);
        writer.WriteStartArray("entries");
        foreach (var entry in result.Entries)
        {
            writer.WriteStartObject(); writer.WriteString("path", entry.RelativePath); writer.WriteString("destination", entry.DestinationPath);
            writer.WriteString("status", entry.Status.ToString()); writer.WriteBoolean("published", entry.Published);
            writer.WriteBoolean("mutationOccurred", entry.MutationOccurred);
            if (entry.Reason is { } failure) writer.WriteString("reason", failure);
            if (entry.CleanupFailure is { } cleanup) writer.WriteString("cleanupFailure", cleanup);
            if (entry.Metadata?.Windows is { } windows)
            {
                writer.WriteStartObject("windowsMetadata");
                writer.WriteBoolean("verified", windows.Verified);
                writer.WriteString("qualificationScope", windows.QualificationScope);
                WriteWindowsMetadataSnapshot(writer, "expected", windows.Expected);
                if (windows.Observed is { } observed) WriteWindowsMetadataSnapshot(writer, "observed", observed);
                else writer.WriteNull("observed");
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteWindowsMetadataSnapshot(Utf8JsonWriter writer, string name, FolderCopyWindowsMetadataSnapshot value)
    {
        writer.WriteStartObject(name);
        // FILETIMEと64bit IDは、JSONの浮動小数点readerでも丸めない十進文字列にする。
        writer.WriteString("creationFileTime", value.CreationTime.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("lastWriteFileTime", value.LastWriteTime.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("fileIndex", value.FileIndex.ToString(CultureInfo.InvariantCulture));
        writer.WriteNumber("volumeSerial", value.VolumeSerial);
        writer.WriteNumber("size", value.Size); writer.WriteNumber("attributes", (uint)value.Attributes);
        writer.WriteNumber("securityControl", value.SecurityControl);
        writer.WriteString("securitySha256", value.SecuritySha256); writer.WriteString("eaSha256", value.EaSha256);
        writer.WriteEndObject();
    }
}
