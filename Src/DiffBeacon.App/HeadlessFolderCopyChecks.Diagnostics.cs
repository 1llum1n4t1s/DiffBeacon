using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static partial class HeadlessFolderCopyChecks
{
    // 確認dialog待機の失敗はerror dialogを閉じず、旧taskの結果を採用せずに採取する。
    private static void RecordConfirmationWaitDiagnostic(string root, string name, string phase,
        MainWindow window, ComparisonPane pane, Task task, TimeSpan elapsed, string source, string destination,
        Entry[] beforeSource, Entry[] beforeDestination)
    {
        RecordCopyDiagnostic(root, name + "-" + phase, pane, source, destination,
            DirectoryCopyDirection.LeftToRight, DirectoryCopyMode.All, "injected-stale-confirmation-wait");
        using var stream = File.Create(Path.Combine(root, "folder-confirmation-" + name + "-" + phase + ".json"));
        using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
        writer.WriteStartObject(); writer.WriteString("name", name); writer.WriteString("phase", phase);
        writer.WriteNumber("elapsedMilliseconds", elapsed.TotalMilliseconds);
        writer.WriteString("taskStatus", task.Status.ToString()); writer.WriteBoolean("isCompleted", task.IsCompleted);
        writer.WriteBoolean("isFaulted", task.IsFaulted); writer.WriteBoolean("isCanceled", task.IsCanceled);
        writer.WriteString("comparisonStatus", pane.ComparisonStatus);
        writer.WriteString("exception", task.IsCompleted && task.IsFaulted ? task.Exception?.ToString() : null);
        writer.WriteString("sourceRoot", source); writer.WriteString("destinationRoot", destination);
        writer.WriteStartArray("dialogs");
        foreach (var dialog in window.OwnedWindows)
        {
            writer.WriteStartObject(); writer.WriteString("title", dialog.Title);
            writer.WriteStartArray("textBlocks");
            foreach (var block in dialog.GetVisualDescendants().OfType<TextBlock>()) writer.WriteStringValue(block.Text);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteEntries(writer, "beforeSource", beforeSource); WriteEntries(writer, "beforeDestination", beforeDestination);
        CaptureConfirmationSnapshot(writer, "afterSource", source);
        CaptureConfirmationSnapshot(writer, "afterDestination", destination);
        writer.WriteEndObject(); writer.Flush();
    }

    private static void CaptureConfirmationSnapshot(Utf8JsonWriter writer, string name, string root)
    {
        Entry[]? entries = null; string? failure = null;
        try { entries = Snapshot(root); }
        catch (Exception error) { failure = error.ToString(); }
        if (entries is not null) WriteEntries(writer, name, entries);
        else writer.WriteNull(name);
        writer.WriteString(name + "CaptureFailure", failure);
    }

    // 結果の判定より先に閉じる。後続の失敗でも各caseの理由を保持する。
    private static void RecordCopyDiagnostic(string root, string name, ComparisonPane pane,
        string sourceRoot, string destinationRoot, DirectoryCopyDirection direction, DirectoryCopyMode mode,
        string scenario)
    {
        using var stream = new FileStream(Path.Combine(root, "folder-copy-diagnostics.ndjson"), FileMode.Append, FileAccess.Write);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name); writer.WriteString("verificationScenario", scenario);
            writer.WriteString("sourceRoot", sourceRoot); writer.WriteString("destinationRoot", destinationRoot);
            writer.WriteString("direction", direction.ToString()); writer.WriteString("mode", mode.ToString());
            writer.WriteNumber("selectedDirection", (int)direction);
            writer.WriteString("comparisonStatus", pane.ComparisonStatus);
            var result = pane.LastFolderCopyResult;
            writer.WritePropertyName("result");
            if (result is null) writer.WriteNullValue();
            else
            {
                writer.WriteStartObject();
                writer.WriteString("reason", result.Reason); writer.WriteBoolean("cancelled", result.Cancelled);
                writer.WriteBoolean("succeeded", result.Succeeded); writer.WriteBoolean("mutationOccurred", result.MutationOccurred);
                writer.WriteNumber("publishedCount", result.PublishedCount);
                writer.WriteNumber("readBytes", result.ReadBytes); writer.WriteNumber("writeBytes", result.WriteBytes);
                writer.WriteNumber("metadataRetainedBytes", result.MetadataRetainedBytes);
                writer.WriteNumber("metadataQueryBytes", result.MetadataQueryBytes);
                writer.WriteNumber("metadataNativeOperations", result.MetadataNativeOperations);
                writer.WriteStartArray("entries");
                foreach (var entry in result.Entries)
                {
                    writer.WriteStartObject();
                    writer.WriteString("relativePath", entry.RelativePath); writer.WriteString("destinationPath", entry.DestinationPath);
                    writer.WriteString("sourcePath", Path.Combine(sourceRoot, entry.RelativePath));
                    writer.WriteString("status", entry.Status.ToString()); writer.WriteString("reason", entry.Reason);
                    writer.WriteString("cleanupFailure", entry.CleanupFailure);
                    writer.WriteBoolean("mutationOccurred", entry.MutationOccurred); writer.WriteBoolean("published", entry.Published);
                    writer.WritePropertyName("metadata");
                    if (entry.Metadata is not { } metadata) writer.WriteNullValue();
                    else
                    {
                        writer.WriteStartObject();
                        WriteCopySnapshot(writer, "source", metadata.Source);
                        WriteCopySnapshot(writer, "destination", metadata.Destination);
                        writer.WriteString("explicitWindowsAttributes", metadata.ExplicitWindowsAttributes.ToString());
                        writer.WriteString("sourceWindowsAttributesNotExplicitlySet", metadata.SourceWindowsAttributesNotExplicitlySet.ToString());
                        writer.WritePropertyName("windows");
                        if (metadata.Windows is not { } windows) writer.WriteNullValue();
                        else
                        {
                            writer.WriteStartObject(); writer.WriteBoolean("verified", windows.Verified);
                            writer.WriteString("qualificationScope", windows.QualificationScope);
                            WriteCopyWindowsSnapshot(writer, "expected", windows.Expected);
                            WriteCopyWindowsSnapshot(writer, "observed", windows.Observed);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndObject(); writer.Flush();
        }
        stream.WriteByte((byte)'\n'); stream.Flush();
    }

    private static void WriteCopySnapshot(Utf8JsonWriter writer, string name, FolderCopySnapshot? snapshot)
    {
        writer.WritePropertyName(name);
        if (snapshot is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject(); writer.WriteString("kind", snapshot.Kind.ToString()); writer.WriteNumber("size", snapshot.Size);
        writer.WriteString("lastWriteTimeUtc", snapshot.LastWriteTimeUtc); writer.WriteString("attributes", snapshot.Attributes.ToString());
        writer.WriteString("unixMode", snapshot.UnixMode?.ToString()); writer.WriteEndObject();
    }

    private static void WriteCopyWindowsSnapshot(Utf8JsonWriter writer, string name, FolderCopyWindowsMetadataSnapshot? snapshot)
    {
        writer.WritePropertyName(name);
        if (snapshot is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject(); writer.WriteNumber("creationTime", snapshot.CreationTime);
        writer.WriteNumber("lastWriteTime", snapshot.LastWriteTime); writer.WriteNumber("size", snapshot.Size);
        writer.WriteString("attributes", snapshot.Attributes.ToString()); writer.WriteNumber("volumeSerial", snapshot.VolumeSerial);
        writer.WriteNumber("fileIndex", snapshot.FileIndex); writer.WriteNumber("securityControl", snapshot.SecurityControl);
        writer.WriteString("securitySha256", snapshot.SecuritySha256); writer.WriteString("eaSha256", snapshot.EaSha256);
        writer.WriteEndObject();
    }
}
