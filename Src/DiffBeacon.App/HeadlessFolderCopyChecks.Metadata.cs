using System.Text.Json;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static partial class HeadlessFolderCopyChecks
{
    private static void RunWindowsMetadata(string root,
        Func<string, FolderCopyLimits?, (ComparisonPane Pane, string Left, string Right)> create,
        Action<Task> wait, Action<string, bool> report, Action<string> screenshot)
    {
        if (!OperatingSystem.IsWindows() || !new DriveInfo(Path.GetPathRoot(root)!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return;
        using var file = File.Create(Path.Combine(root, "folder-windows-metadata-observations.json"));
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        json.WriteStartObject(); json.WriteStartArray("cases");
        foreach (var (name, limits, changedSide) in new (string, FolderCopyLimits?, int)[]
        {
            ("metadata-retained-zero", new() { MaximumMetadataRetainedBytes = 0 }, -1),
            ("metadata-query-zero", new() { MaximumMetadataQueryBytes = 0 }, -1),
            ("metadata-native-zero", new() { MaximumMetadataNativeOperations = 0 }, -1),
            ("metadata-source-creation-change", null, 0),
            ("metadata-target-creation-change", null, 1)
        })
        {
            var (pane, left, right) = create(name, limits);
            var source = Path.Combine(left, "a.bin"); var destination = Path.Combine(right, "a.bin");
            var sourceBefore = Snapshot(left); var destinationBefore = Snapshot(right);
            var sourceCreation = File.GetCreationTimeUtc(source).ToFileTimeUtc();
            var targetCreation = File.GetCreationTimeUtc(destination).ToFileTimeUtc();
            FolderCopyPlan? plan = null; var confirmed = false;
            pane.FolderPlanReady = value => plan = value;
            Dialogs.ConfirmationShown = dialog =>
            {
                confirmed = true;
                if (changedSide >= 0) File.SetCreationTimeUtc(changedSide == 0 ? source : destination, FixedTime.AddSeconds(1));
                Answer(dialog, true);
            };
            try
            {
                Select(pane, ["a.bin"]); Click(pane, "選択をすべてコピー →");
                if (pane.PendingFolderCopy is { } pending) wait(pending);
                var result = pane.LastFolderCopyResult;
                report(name + " actual button rejected without publication", changedSide < 0
                    ? !confirmed && plan is null && result is null
                    : confirmed && plan is { UsesWindowsMetadata: true } && result is { Succeeded: false, MutationOccurred: false, PublishedCount: 0 });
                report(name + " all DATA/mtime/attributes retained", sourceBefore.SequenceEqual(Snapshot(left)) && destinationBefore.SequenceEqual(Snapshot(right)));
                var sourceAfter = File.GetCreationTimeUtc(source).ToFileTimeUtc();
                var targetAfter = File.GetCreationTimeUtc(destination).ToFileTimeUtc();
                report(name + " only requested creation changed", sourceAfter == (changedSide == 0 ? FixedTime.AddSeconds(1).ToFileTimeUtc() : sourceCreation)
                    && targetAfter == (changedSide == 1 ? FixedTime.AddSeconds(1).ToFileTimeUtc() : targetCreation));
                json.WriteStartObject(); json.WriteString("id", name); json.WriteString("source", source); json.WriteString("destination", destination);
                json.WriteBoolean("confirmed", confirmed); json.WriteBoolean("planPresent", plan is not null);
                json.WriteBoolean("usesWindowsMetadata", plan?.UsesWindowsMetadata == true);
                json.WriteBoolean("resultPresent", result is not null); json.WriteBoolean("succeeded", result?.Succeeded == true);
                json.WriteBoolean("mutation", result?.MutationOccurred == true); json.WriteNumber("published", result?.PublishedCount ?? 0);
                if (result?.Reason is { } reason) json.WriteString("reason", reason);
                json.WriteNumber("changedSide", changedSide);
                json.WriteString("sourceCreationBefore", sourceCreation.ToString(System.Globalization.CultureInfo.InvariantCulture));
                json.WriteString("sourceCreationAfter", sourceAfter.ToString(System.Globalization.CultureInfo.InvariantCulture));
                json.WriteString("targetCreationBefore", targetCreation.ToString(System.Globalization.CultureInfo.InvariantCulture));
                json.WriteString("targetCreationAfter", targetAfter.ToString(System.Globalization.CultureInfo.InvariantCulture));
                WriteEntries(json, "beforeSource", sourceBefore); WriteEntries(json, "afterSource", Snapshot(left));
                WriteEntries(json, "beforeDestination", destinationBefore); WriteEntries(json, "afterDestination", Snapshot(right));
                json.WriteEndObject();
            }
            finally { pane.FolderPlanReady = null; Dialogs.ConfirmationShown = null; }
        }
        json.WriteEndArray(); json.WriteBoolean("complete", true); json.WriteEndObject();
        RunWindowsMetadataDiagnostics(root, create, wait, report, screenshot);
    }

    private static void RunWindowsMetadataDiagnostics(string root,
        Func<string, FolderCopyLimits?, (ComparisonPane Pane, string Left, string Right)> create,
        Action<Task> wait, Action<string, bool> report, Action<string> screenshot)
    {
        using var file = File.Create(Path.Combine(root, "folder-windows-metadata-diagnostic-observations.json"));
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        json.WriteStartObject(); json.WriteStartArray("cases");
        foreach (var preparing in new[] { false, true })
        {
            var name = preparing ? "metadata-prepare-cancel-close-diagnostic" : "metadata-execute-cleanup-diagnostic";
            var marker = preparing ? "検証: metadataハンドル終了失敗" : "検証: 未公開一時ファイルの清掃失敗";
            var (pane, left, right) = create(name, null);
            var source = Path.Combine(left, "a.bin"); var destination = Path.Combine(right, "a.bin");
            var sourceBefore = Snapshot(left); var targetBefore = Snapshot(right);
            var sourceCreation = File.GetCreationTimeUtc(source).ToFileTimeUtc();
            var targetCreation = File.GetCreationTimeUtc(destination).ToFileTimeUtc();
            Exception failure = preparing ? new OperationCanceledException("検証: 準備を中止") : new IOException("検証: コピー拒否");
            // 診断の注入は実OSのclose/disposition失敗を再現したこととは区別する。
            failure.Data[preparing ? FolderCopyWindowsMetadata.CloseFailureDataKey : FolderCopyWindowsMetadata.StagingCleanupFailureDataKey] = new IOException(marker);
            if (preparing) pane.FolderPlanReady = _ => throw failure;
            else pane.FolderOutputChecking = _ => throw failure;
            var confirmed = false;
            Dialogs.ConfirmationShown = dialog => { confirmed = true; Answer(dialog, true); };
            try
            {
                Select(pane, ["a.bin"]); Click(pane, "選択をすべてコピー →");
                if (pane.PendingFolderCopy is { } pending) wait(pending);
                var result = pane.LastFolderCopyResult;
                report(name + " actual failure path without publication", preparing
                    ? !confirmed && result is null && pane.FolderCopyStatusText.Contains("中止", StringComparison.Ordinal)
                    : confirmed && result is { Succeeded: false, MutationOccurred: false, PublishedCount: 0 }
                        && result.Entries.Any(entry => entry.CleanupFailure?.Contains(marker, StringComparison.Ordinal) == true));
                report(name + " actual GUI keeps cleanup or close diagnostic", pane.FolderCopyStatusText.Contains(marker, StringComparison.Ordinal)
                    && (preparing || pane.FolderCopySummaryText.Contains(marker, StringComparison.Ordinal)));
                var sourceAfter = File.GetCreationTimeUtc(source).ToFileTimeUtc();
                var targetAfter = File.GetCreationTimeUtc(destination).ToFileTimeUtc();
                report(name + " all input metadata and DATA retained", sourceBefore.SequenceEqual(Snapshot(left)) && targetBefore.SequenceEqual(Snapshot(right))
                    && sourceCreation == sourceAfter && targetCreation == targetAfter);
                screenshot(name + ".png");
                json.WriteStartObject(); json.WriteString("id", name); json.WriteString("source", source); json.WriteString("destination", destination);
                json.WriteString("marker", marker); json.WriteBoolean("injectedDiagnostic", true); json.WriteBoolean("confirmed", confirmed);
                json.WriteBoolean("resultPresent", result is not null); json.WriteBoolean("mutation", result?.MutationOccurred == true);
                json.WriteNumber("published", result?.PublishedCount ?? 0); json.WriteString("status", pane.FolderCopyStatusText);
                json.WriteString("summary", pane.FolderCopySummaryText);
                json.WriteString("sourceCreationBefore", sourceCreation.ToString(System.Globalization.CultureInfo.InvariantCulture));
                json.WriteString("sourceCreationAfter", sourceAfter.ToString(System.Globalization.CultureInfo.InvariantCulture));
                json.WriteString("targetCreationBefore", targetCreation.ToString(System.Globalization.CultureInfo.InvariantCulture));
                json.WriteString("targetCreationAfter", targetAfter.ToString(System.Globalization.CultureInfo.InvariantCulture));
                WriteEntries(json, "beforeSource", sourceBefore); WriteEntries(json, "afterSource", Snapshot(left));
                WriteEntries(json, "beforeDestination", targetBefore); WriteEntries(json, "afterDestination", Snapshot(right));
                json.WriteEndObject();
            }
            finally { pane.FolderPlanReady = null; pane.FolderOutputChecking = null; Dialogs.ConfirmationShown = null; }
        }
        json.WriteEndArray(); json.WriteBoolean("complete", true); json.WriteEndObject();
    }
}
