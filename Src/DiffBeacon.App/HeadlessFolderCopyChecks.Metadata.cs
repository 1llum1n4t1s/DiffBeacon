using System.Text.Json;
using System.Security.Cryptography;
using Avalonia.Threading;
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
        RunWindowsMetadataTiming(root, create, wait, report);
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

    private sealed record MetadataTimingProbe(FolderCopyWindowsMetadata.VerificationPoint Point,
        long BeforeWrite, long AfterWrite, long BeforeCreation, long AfterCreation,
        int BeforeAttributes, int AfterAttributes, int MutatorProcessId, int MutatorThreadId);

    private static void RunWindowsMetadataTiming(string root,
        Func<string, FolderCopyLimits?, (ComparisonPane Pane, string Left, string Right)> create,
        Action<Task> wait, Action<string, bool> report)
    {
        if (FolderCopyWindowsMetadata.VerificationObserver is not null)
            throw new InvalidOperationException("metadata観測検証のcallbackが既に使用されています。");
        // 8操作、1byte原本だけ。root外の操作はcase専用containerに限定する。
        foreach (var (id, point, targetKind, mutation, copied) in new (string, string, string, string, bool)[]
        {
            ("outer-basic", "afterBasicSnapshot", "outer", "write", true),
            ("outer-observe", "before", "outer", "write", true),
            ("outer-reopen", "beforePathRecheck", "outer", "write", true),
            ("inner-observe", "before", "inner", "write", false),
            ("file-observe", "before", "file", "write", false),
            ("outer-creation", "before", "outer", "creation", false),
            ("outer-attributes", "before", "outer", "attributes", false),
            ("outer-cancel", "before", "outer", "cancel", false)
        })
        {
            var name = "metadata-timing-" + id;
            var (pane, left, right) = create(name, null);
            var source = Path.Combine(left, "a.bin");
            var target = targetKind == "outer" ? Path.GetDirectoryName(left)! : targetKind == "inner" ? left : source;
            var nativeTarget = FolderCopyWindowsStreams.ExtendedPath(target);
            var beforeSource = Snapshot(left); var beforeDestination = Snapshot(right);
            var probes = new List<MetadataTimingProbe>(); var matched = 0; var confirmed = false;
            FolderCopyPlan? plan = null;
            pane.FolderPlanReady = value => plan = value;
            Dialogs.ConfirmationShown = dialog => { confirmed = true; Answer(dialog, true); };
            FolderCopyWindowsMetadata.VerificationObserver = observed =>
            {
                if (observed.Point != point || !FolderCopyWindowsStreams.ExtendedPath(observed.Path).Equals(nativeTarget, StringComparison.OrdinalIgnoreCase)
                    || Interlocked.CompareExchange(ref matched, 1, 0) != 0) return;
                var beforeWrite = (targetKind == "file" ? File.GetLastWriteTimeUtc(target) : Directory.GetLastWriteTimeUtc(target)).ToFileTimeUtc();
                var beforeCreation = (targetKind == "file" ? File.GetCreationTimeUtc(target) : Directory.GetCreationTimeUtc(target)).ToFileTimeUtc();
                var beforeAttributes = (int)File.GetAttributes(target);
                if (mutation == "write")
                {
                    var changed = DateTime.FromFileTimeUtc(beforeWrite).AddSeconds(1);
                    if (targetKind == "file") File.SetLastWriteTimeUtc(target, changed); else Directory.SetLastWriteTimeUtc(target, changed);
                }
                else if (mutation == "creation") Directory.SetCreationTimeUtc(target, DateTime.FromFileTimeUtc(beforeCreation).AddSeconds(1));
                else if (mutation == "attributes") File.SetAttributes(target, (FileAttributes)beforeAttributes ^ FileAttributes.ReadOnly);
                probes.Add(new(observed, beforeWrite, (targetKind == "file" ? File.GetLastWriteTimeUtc(target) : Directory.GetLastWriteTimeUtc(target)).ToFileTimeUtc(),
                    beforeCreation, (targetKind == "file" ? File.GetCreationTimeUtc(target) : Directory.GetCreationTimeUtc(target)).ToFileTimeUtc(), beforeAttributes, (int)File.GetAttributes(target),
                    Environment.ProcessId, Environment.CurrentManagedThreadId));
                if (mutation == "cancel")
                {
                    if (Dispatcher.UIThread.CheckAccess()) Click(pane, "中止");
                    else Dispatcher.UIThread.InvokeAsync(() => Click(pane, "中止")).GetAwaiter().GetResult();
                }
            };
            try
            {
                Select(pane, ["a.bin"]); Click(pane, "選択をすべてコピー →");
                if (pane.PendingFolderCopy is { } pending) wait(pending);
                var result = pane.LastFolderCopyResult;
                RecordCopyDiagnostic(root, name, pane, left, right, DirectoryCopyDirection.LeftToRight, DirectoryCopyMode.All,
                    "injected-bounded-metadata-" + point + "-" + mutation);
                report(name + " exact observation phase and actor", matched == 1 && probes.Count == 1
                    && probes[0].Point.TrackWriteTime == (targetKind != "outer")
                    && probes[0].Point.ProcessId == Environment.ProcessId
                    && probes[0].MutatorProcessId == probes[0].Point.ProcessId
                    && probes[0].MutatorThreadId == probes[0].Point.ThreadId);
                if (probes.Count == 1)
                {
                    var probe = probes[0];
                    report(name + " fixed timestamp boundary", point == "afterBasicSnapshot"
                        ? probe.Point.MetadataWriteTime is null && probe.Point.BasicWriteTime == checked((ulong)probe.BeforeWrite)
                        : probe.Point.MetadataWriteTime == checked((ulong)probe.BeforeWrite) && probe.Point.BasicWriteTime is null);
                    report(name + " only declared metadata field changed", mutation switch
                    {
                        "write" => probe.AfterWrite == probe.BeforeWrite + TimeSpan.TicksPerSecond
                            && probe.AfterCreation == probe.BeforeCreation && probe.AfterAttributes == probe.BeforeAttributes,
                        "creation" => probe.AfterCreation == probe.BeforeCreation + TimeSpan.TicksPerSecond
                            && probe.AfterWrite == probe.BeforeWrite && probe.AfterAttributes == probe.BeforeAttributes,
                        "attributes" => probe.AfterAttributes == (probe.BeforeAttributes ^ (int)FileAttributes.ReadOnly)
                            && probe.AfterWrite == probe.BeforeWrite && probe.AfterCreation == probe.BeforeCreation,
                        _ => probe.AfterWrite == probe.BeforeWrite && probe.AfterCreation == probe.BeforeCreation
                            && probe.AfterAttributes == probe.BeforeAttributes
                    });
                }
                report(name + " actual button policy result", copied
                    ? confirmed && plan is { UsesWindowsMetadata: true } && result is { Succeeded: true, MutationOccurred: true, PublishedCount: 1 }
                        && result.Entries.Count == 1 && result.Entries[0].Metadata?.Windows?.Verified == true && result.Entries[0].CleanupFailure is null
                    : !confirmed && plan is null && result is null && (mutation != "cancel" || pane.FolderCopyStatusText.Contains("中止", StringComparison.Ordinal)));
                report(name + " fixed DATA bytes and copy boundary", FixedMetadataTimingBytes(left, true)
                    && FixedMetadataTimingBytes(right, false, copied));
                report(name + " rejected output snapshot retained", copied || beforeDestination.SequenceEqual(Snapshot(right)));
                report(name + " declared file mtime only", File.GetLastWriteTimeUtc(source) ==
                    (targetKind == "file" && mutation == "write" ? FixedTime.AddSeconds(1) : FixedTime));
            }
            finally
            {
                FolderCopyWindowsMetadata.VerificationObserver = null;
                pane.FolderPlanReady = null; Dialogs.ConfirmationShown = null;
                WriteMetadataTimingCase(Path.Combine(root, "folder-" + name + ".json"), name, point, target, mutation, copied,
                    pane, confirmed, probes, beforeSource, Snapshot(left), beforeDestination, Snapshot(right));
            }
        }
    }

    private static bool FixedMetadataTimingBytes(string root, bool source, bool copied = false) =>
        File.ReadAllBytes(Path.Combine(root, "a.bin")).SequenceEqual(new byte[] { source || copied ? (byte)0x11 : (byte)0x22 })
        && File.ReadAllBytes(Path.Combine(root, "b.bin")).SequenceEqual(new byte[] { source ? (byte)0x33 : (byte)0x44 })
        && File.ReadAllBytes(Path.Combine(root, "same.bin")).SequenceEqual(new byte[] { 0 })
        && File.ReadAllBytes(Path.Combine(root, "tree", "leaf.bin")).SequenceEqual(new byte[] { source ? (byte)0x55 : (byte)0x66 });

    private static void WriteMetadataTimingCase(string path, string name, string point, string target, string mutation, bool copied,
        ComparisonPane pane, bool confirmed, List<MetadataTimingProbe> probes,
        Entry[] beforeSource, Entry[] afterSource, Entry[] beforeDestination, Entry[] afterDestination)
    {
        using var stream = File.Create(path); using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
        writer.WriteStartObject(); writer.WriteString("name", name); writer.WriteString("point", point); writer.WriteString("target", target);
        writer.WriteString("mutation", mutation); writer.WriteBoolean("expectedCopied", copied); writer.WriteBoolean("confirmed", confirmed);
        writer.WriteString("status", pane.FolderCopyStatusText); writer.WriteString("summary", pane.FolderCopySummaryText);
        writer.WriteString("sourceExpectedASha256", Convert.ToHexString(SHA256.HashData(new byte[] { 0x11 })));
        writer.WriteString("destinationExpectedASha256", Convert.ToHexString(SHA256.HashData(new byte[] { copied ? (byte)0x11 : (byte)0x22 })));
        writer.WriteNumber("published", pane.LastFolderCopyResult?.PublishedCount ?? 0);
        writer.WriteStartArray("probes");
        foreach (var probe in probes)
        {
            writer.WriteStartObject(); writer.WriteString("path", probe.Point.Path); writer.WriteString("point", probe.Point.Point);
            writer.WriteBoolean("trackWriteTime", probe.Point.TrackWriteTime);
            if (probe.Point.MetadataWriteTime is { } metadataWrite) writer.WriteNumber("metadataWriteBefore", metadataWrite); else writer.WriteNull("metadataWriteBefore");
            if (probe.Point.BasicWriteTime is { } basicWrite) writer.WriteNumber("basicWriteBefore", basicWrite); else writer.WriteNull("basicWriteBefore");
            writer.WriteNumber("beforeWrite", probe.BeforeWrite); writer.WriteNumber("afterWrite", probe.AfterWrite);
            writer.WriteNumber("beforeCreation", probe.BeforeCreation); writer.WriteNumber("afterCreation", probe.AfterCreation);
            writer.WriteNumber("beforeAttributes", probe.BeforeAttributes); writer.WriteNumber("afterAttributes", probe.AfterAttributes);
            writer.WriteNumber("observerPid", probe.Point.ProcessId); writer.WriteNumber("observerThread", probe.Point.ThreadId);
            writer.WriteNumber("mutatorPid", probe.MutatorProcessId); writer.WriteNumber("mutatorThread", probe.MutatorThreadId);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteEntries(writer, "beforeSource", beforeSource); WriteEntries(writer, "afterSource", afterSource);
        WriteEntries(writer, "beforeDestination", beforeDestination); WriteEntries(writer, "afterDestination", afterDestination);
        writer.WriteEndObject(); writer.Flush();
    }
}
