using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

// 注入はowned windowのinstanceだけ。通常経路は実StorageProviderへ戻す。
internal sealed class ArchiveWritePickerInjection
{
    internal Func<FilePickerSaveOptions, Task<string?>>? Save { get; set; }
    internal Func<FilePickerOpenOptions, Task<string?>>? File { get; set; }
    internal Func<FolderPickerOpenOptions, Task<string?>>? Folder { get; set; }
    internal static async Task<string?> SaveAsync(TopLevel owner, FilePickerSaveOptions options, ArchiveWritePickerInjection? probe)
        => probe?.Save is { } pick ? await pick(options) : (await owner.StorageProvider.SaveFilePickerAsync(options))?.TryGetLocalPath();
    internal static async Task<string?> FileAsync(TopLevel owner, FilePickerOpenOptions options, ArchiveWritePickerInjection? probe)
    {
        if (probe?.File is { } pick) return await pick(options);
        var files = await owner.StorageProvider.OpenFilePickerAsync(options);
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
    internal static async Task<string?> FolderAsync(TopLevel owner, FolderPickerOpenOptions options, ArchiveWritePickerInjection? probe)
    {
        if (probe?.Folder is { } pick) return await pick(options);
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(options);
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}
public sealed partial class ComparisonPane
{
    internal ArchiveWritePickerInjection? ArchiveWritePickers { get; set; }
}

internal static class HeadlessBareCompressionWritingChecks
{
    internal static void Run(MainWindow caller, ComparisonPane callerPane, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var folder = Path.Combine(output, "bare-compression-writing"); Directory.CreateDirectory(folder);
        var sharedBefore = WorkspaceStore.SerializeWorkspace(new() { Entries = [callerPane.CaptureProject()] });
        var sharedTabs = caller.SessionPanes.ToArray(); var sharedActive = caller.ActivePane;
        var sharedDirty = callerPane.HasUnsavedChanges; var sharedCompare = callerPane.CompareButton.IsEnabled;
        var sharedWidth = caller.Width; var sharedHeight = caller.Height;
        var oldGZip = GZipWriteOptionsDialog.Shown;
        var observations = new List<Action<Utf8JsonWriter>>(); var pickerRows = new List<Action<Utf8JsonWriter>>();
        var pngs = new List<string>(); var modalCount = 0; Func<bool> preserved = () => false;
        var owner = new MainWindow(null, new ImageApplicationOptionsStore(Path.Combine(folder, "owned-settings.json"))) { Width = 1100, Height = 800 };
        var pane = owner.ActivePane;
        var source = Path.Combine(folder, "日本-source.bin");
        var expected = Path.Combine(folder, "payload.expected.bin");
        var payload = Enumerable.Range(0, 257).Select(i => (byte)(i * 31)).ToArray();
        File.WriteAllBytes(source, payload); File.WriteAllBytes(expected, payload);
        var empty = Path.Combine(folder, "empty.bin"); File.WriteAllBytes(empty, []);
        var emptyExpected = Path.Combine(folder, "empty.expected.bin"); File.WriteAllBytes(emptyExpected, []);
        var root = Path.Combine(folder, "one.zip");
        new ManagedArchive().WriteArchive(root, [new("payload.bin", payload)]);
        var rootSha = Hash(root); var sourceSha = Hash(source);
        var tarFolder = Path.Combine(folder, "tar-source"); Directory.CreateDirectory(tarFolder);
        File.WriteAllBytes(Path.Combine(tarFolder, "payload.bin"), payload);
        owner.Show(caller); Jobs();
        Exception? primary = null; Exception? cleanupFailure = null;
        try
        {
            GZipWriteOptionsDialog.Shown = dialog => { modalCount++; Hit(dialog, dialog.Accept); };
            foreach (var extension in new[] { ".bz2", ".BZ2", ".Z", ".z" })
            foreach (var isEmpty in new[] { false, true })
            {
                var id = "create-" + extension[1..] + (extension == extension.ToUpperInvariant() ? "-upper-" : "-lower-") + isEmpty;
                var target = Path.Combine(folder, id + extension);
                var beforeModals = modalCount; var files = 0; var folders = 0;
                pane.ArchiveWritePickers = new()
                {
                    Save = options => { Picker("save", options.Title, false, options.ShowOverwritePrompt, options.FileTypeChoices); return Task.FromResult<string?>(target); },
                    File = options => { files++; Picker("file", options.Title, options.AllowMultiple, false, null); return Task.FromResult<string?>(isEmpty ? empty : source); },
                    Folder = options => { folders++; return Task.FromResult<string?>(tarFolder); }
                };
                Task? task = null; pane.TextSaveTaskObserved = (name, observed) => { if (name == "archive-create") task = observed; };
                Hit(owner, Button(pane, "アーカイブ作成"));
                if (task is null) throw new InvalidDataException("create button Task was not observed");
                pump(task); Jobs();
                Verify(id, files == 1 && folders == 0 && modalCount == beforeModals && File.Exists(target)
                    && Hash(source) == sourceSha && Hash(root) == rootSha, "headless pointer + injected single-file picker, no gzip modal",
                    isEmpty ? empty : source, target, isEmpty ? emptyExpected : expected);
                Frame(id);
            }
            // gzip modalの既存経路を一件保持する。
            var gzipSource = Path.Combine(folder, "gzip-source.bin"); File.WriteAllBytes(gzipSource, payload);
            var gzipTarget = Path.Combine(folder, "existing-gzip.gz"); var beforeGZip = modalCount;
            pane.ArchiveWritePickers = new() { Save = _ => Task.FromResult<string?>(gzipTarget), File = _ => Task.FromResult<string?>(gzipSource) };
            Create();
            Verify("gzip-modal-retained", modalCount == beforeGZip + 1 && File.Exists(gzipTarget), "actual owned GZip dialog pointer Accept", gzipSource, gzipTarget, expected);
            // TARの長名と全短縮別名はフォルダーpickerを使う。
            foreach (var extension in new[] { ".tar.gz", ".tgz", ".tar.bz2", ".TAR.BZ2", ".tbz2", ".tbz", ".tar.Z", ".tar.z", ".taz" })
            {
                var files = 0; var folders = 0; var before = modalCount;
                var target = Path.Combine(folder, "tar-" + extension.Replace(".", "-") + (extension == extension.ToUpperInvariant() ? "-upper" : "-lower") + extension);
                pane.ArchiveWritePickers = new()
                {
                    Save = _ => Task.FromResult<string?>(target),
                    File = _ => { files++; return Task.FromResult<string?>(source); },
                    Folder = options => { folders++; Picker("folder", options.Title, options.AllowMultiple, false, null); return Task.FromResult<string?>(tarFolder); }
                };
                Create();
                Verify("tar-picker-" + extension, files == 0 && folders == 1 && before == modalCount && File.Exists(target),
                    "TAR suffix priority retained", source, target, expected);
            }
            var cancelTarget = Path.Combine(folder, "cancel.bz2"); File.WriteAllBytes(cancelTarget, "old output"u8.ToArray()); var cancelSha = Hash(cancelTarget);
            pane.ArchiveWritePickers = new() { Save = _ => Task.FromResult<string?>(null), File = _ => throw new InvalidDataException("file picker after save cancel") };
            Create(); Verify("save-cancel", Hash(cancelTarget) == cancelSha, "save cancel before source picker", source, cancelTarget, "", cancelSha);
            pane.ArchiveWritePickers = new() { Save = _ => Task.FromResult<string?>(cancelTarget), File = _ => Task.FromResult<string?>(null) };
            Create(); Verify("file-cancel", Hash(cancelTarget) == cancelSha, "file cancel before writer", source, cancelTarget, "", cancelSha);
            pane.ApplyProject(new() { Mode = "Archive", LeftPath = root, RightPath = root });
            pump(pane.ComparePathsAsync()); Jobs();
            var panel = pane.GetVisualDescendants().OfType<ArchivePanel>().Single();
            var project = WorkspaceStore.SerializeWorkspace(new() { Entries = [pane.CaptureProject()] });
            var rows = panel.Rows; var preview = panel.PreviewText; var left = panel.ConfirmedLeft; var right = panel.ConfirmedRight;
            preserved = () => ReferenceEquals(panel.Rows, rows) && panel.PreviewText == preview
                && ReferenceEquals(panel.ConfirmedLeft, left) && ReferenceEquals(panel.ConfirmedRight, right)
                && project.SequenceEqual(WorkspaceStore.SerializeWorkspace(new() { Entries = [pane.CaptureProject()] }));
            foreach (var extension in new[] { ".bz2", ".BZ2", ".Z", ".z" })
            foreach (var rightSide in new[] { false, true })
            {
                var id = "repack-" + extension[1..] + (extension == extension.ToUpperInvariant() ? "-upper-" : "-lower-") + rightSide;
                var target = Path.Combine(folder, id + extension); var before = modalCount;
                panel.ArchiveWritePickers = new() { Save = options => { Picker("save", options.Title, false, options.ShowOverwritePrompt, options.FileTypeChoices); return Task.FromResult<string?>(target); } };
                Task? task = null; panel.ButtonTaskObserved = (_, t) => task = t;
                Hit(owner, Button(panel, rightSide ? "右を再梱包" : "左を再梱包"));
                if (task is null) throw new InvalidDataException("repack button Task was not observed");
                pump(task); Jobs();
                Verify(id, File.Exists(target) && before == modalCount && preserved(), "real side button and root read settings/display retained", root, target, expected);
            }
            // picker完了が古い確定入力へ戻る経路。実providerを走らせず既存adoption boundaryで拒否する。
            var stale = Path.Combine(folder, "stale.Z"); File.WriteAllBytes(stale, "keep stale output"u8.ToArray()); var staleSha = Hash(stale);
            var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            panel.ArchiveWritePickers = new() { Save = _ => pending.Task };
            Task? staleTask = null; panel.ButtonTaskObserved = (_, t) => staleTask = t;
            Hit(owner, Button(panel, "左を再梱包"));
            pump(panel.RefreshAsync()); pending.SetResult(stale);
            if (staleTask is null) throw new InvalidDataException("stale repack Task absent");
            Exception? staleError = null; try { pump(staleTask); } catch (OperationCanceledException e) { staleError = e; }
            Verify("old-repack-picker", staleError is OperationCanceledException && Hash(stale) == staleSha,
                "confirmed source identity replaced during actual picker await", root, stale, "", staleSha, staleError);
            panel.ButtonTaskObserved = null; panel.ArchiveWritePickers = null;
            Rejection("input-output-alias", root, () => pump(panel.RepackToAsync(false, root)));
            var readOnly = Path.Combine(folder, "readonly.bz2"); File.WriteAllBytes(readOnly, "readonly existing"u8.ToArray());
            var attributes = File.GetAttributes(readOnly);
            try { File.SetAttributes(readOnly, attributes | FileAttributes.ReadOnly); Rejection("readonly-output", readOnly, () => pump(panel.RepackToAsync(false, readOnly))); }
            finally { File.SetAttributes(readOnly, attributes); }
            var outer = Path.Combine(folder, "outer.zip"); new ManagedArchive().WriteArchive(outer, [new("inner.zip", File.ReadAllBytes(root))]);
            var outerSha = Hash(outer);
            pane.ApplyProject(new() { Mode = "Archive", LeftReadOnly = true, RightReadOnly = true,
                LeftArchiveInput = new() { RootPath = outer, RootSha256 = outerSha, EntryChain = ["inner.zip"] },
                RightArchiveInput = new() { RootPath = outer, RootSha256 = outerSha, EntryChain = ["inner.zip"] } });
            pump(pane.ComparePathsAsync()); Jobs();
            var child = pane.GetVisualDescendants().OfType<ArchivePanel>().Single();
            Verify("nested-button-disabled", !Button(child, "左を再梱包").IsEnabled && !Button(child, "右を再梱包").IsEnabled,
                "actual nested root buttons disabled", outer, "", "");
            var nestedTarget = Path.Combine(folder, "nested.Z"); File.WriteAllBytes(nestedTarget, "keep nested"u8.ToArray());
            Rejection("nested-api-refusal", nestedTarget, () => pump(child.RepackToAsync(false, nestedTarget)));
            LifetimeChecks();
            ProviderChecks();
            Verify("all-originals-retained", Hash(source) == sourceSha && Hash(root) == rootSha, "original SHA after all writer routes", source, "", expected);
        }
        catch (Exception e) { primary = e; throw; }
        finally
        {
            try { pane.ArchiveWritePickers = null; pane.TextSaveTaskObserved = null; GZipWriteOptionsDialog.Shown = oldGZip;
                pane.DiscardChanges(); foreach (var modal in owner.OwnedWindows.ToArray()) modal.Close(); owner.Close(); caller.Activate(); Jobs(); }
            catch (Exception cleanup) { cleanupFailure = cleanup; if (primary is not null) primary.Data["writer-owned-cleanup"] = cleanup.ToString(); }
            var sharedAfter = WorkspaceStore.SerializeWorkspace(new() { Entries = [callerPane.CaptureProject()] });
            var restored = sharedBefore.SequenceEqual(sharedAfter) && sharedTabs.SequenceEqual(caller.SessionPanes)
                && ReferenceEquals(sharedActive, caller.ActivePane) && callerPane.HasUnsavedChanges == sharedDirty
                && callerPane.CompareButton.IsEnabled == sharedCompare && caller.Width == sharedWidth && caller.Height == sharedHeight;
            using var file = File.Create(Path.Combine(folder, "writer-proof.json")); using var json = new Utf8JsonWriter(file, new() { Indented = true });
            json.WriteStartObject(); json.WriteNumber("schemaVersion", 1);
            json.WriteString("scope", "headless pointer + injected local picker paths; native picker/OS pointer/full-PNG pixels unverified");
            json.WriteBase64String("parentBefore", sharedBefore); json.WriteBase64String("parentAfter", sharedAfter); json.WriteBoolean("sharedRestored", restored);
            json.WriteString("cleanupFailure", cleanupFailure?.ToString());
            json.WriteString("primaryFailure", primary?.ToString()); json.WriteStartArray("cases"); foreach (var item in observations) item(json); json.WriteEndArray();
            json.WriteStartArray("pickers"); foreach (var item in pickerRows) item(json); json.WriteEndArray();
            json.WriteStartArray("pngs"); foreach (var name in pngs) json.WriteStringValue(name); json.WriteEndArray(); json.WriteEndObject();
            check("bare writer shared window restored", restored, "caller never modified; owned settings only");
            if (primary is null && cleanupFailure is not null) throw new InvalidDataException("writer owned window cleanup failed; proof retained", cleanupFailure);
        }

        void Create()
        {
            Task? task = null; pane.TextSaveTaskObserved = (name, t) => { if (name == "archive-create") task = t; };
            Hit(owner, Button(pane, "アーカイブ作成"));
            if (task is null) throw new InvalidDataException("create button Task absent");
            pump(task); Jobs();
        }
        void LifetimeChecks()
        {
            foreach (var stage in new[] { "save", "file", "folder" })
            foreach (var closeOwner in new[] { false, true })
            {
                var id = "lifetime-" + stage + "-" + closeOwner;
                var target = Path.Combine(folder, id + (stage == "folder" ? ".tar.bz2" : ".bz2"));
                File.WriteAllBytes(target, "lifetime output sentinel"u8.ToArray()); var before = Hash(target);
                var testOwner = new MainWindow(null, new ImageApplicationOptionsStore(Path.Combine(folder, id + "-settings.json"))) { Width = 1100, Height = 800 };
                var testPane = testOwner.ActivePane; var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var requested = false; Task? task = null;
                testOwner.Show(caller); Jobs();
                try
                {
                    testPane.ArchiveWritePickers = new()
                    {
                        Save = _ => { if (stage == "save") { requested = true; return pending.Task; } return Task.FromResult<string?>(target); },
                        File = _ => { if (stage == "file") { requested = true; return pending.Task; } return Task.FromResult<string?>(source); },
                        Folder = _ => { requested = true; return pending.Task; }
                    };
                    testPane.TextSaveTaskObserved = (name, observed) => { if (name == "archive-create") task = observed; };
                    Hit(testOwner, Button(testPane, "アーカイブ作成"));
                    if (!requested || task is null) throw new InvalidDataException("lifetime picker/task not reached");
                    if (closeOwner) testOwner.Close(); else testPane.Dispose();
                    pending.SetResult(stage == "save" ? target : stage == "file" ? source : tarFolder);
                    Exception? error = null; try { pump(task); } catch (OperationCanceledException e) { error = e; }
                    Verify(id, error is OperationCanceledException && Hash(target) == before && Hash(source) == sourceSha,
                        "actual create button, delayed injected picker, pane Dispose or owner Closed, no writer publication",
                        source, target, "", before, error);
                }
                finally
                {
                    testPane.ArchiveWritePickers = null; testPane.TextSaveTaskObserved = null;
                    if (!pending.Task.IsCompleted) pending.TrySetCanceled();
                    foreach (var dialog in testOwner.OwnedWindows.ToArray()) dialog.Close();
                    testPane.DiscardChanges(); testOwner.Close(); owner.Activate(); Jobs();
                }
            }
        }
        void ProviderChecks()
        {
            foreach (var extension in new[] { ".bz2", ".Z" })
            {
                foreach (var (id, entries) in new (string, ManagedArchiveWriteEntry[])[]
                {
                    ("zero", []), ("dir-only", [new("dir", null)]), ("dir-before", [new("dir", null), new("file", payload)]),
                    ("dir-after", [new("file", payload), new("dir", null)]), ("multiple", [new("a", payload), new("b", payload)])
                }) RejectWrite(id, new ManagedArchive(), entries, CancellationToken.None);
                RejectWrite("entry-budget", new ManagedArchive(new(MaximumEntryBytes: 8)), [new("file", payload)], CancellationToken.None);
                RejectWrite("decoded-budget", new ManagedArchive(new(MaximumDecodedBytes: 8)), [new("file", payload)], CancellationToken.None);
                RejectWrite("output-budget", new ManagedArchive(new(MaximumOutputBytes: 1)), [new("file", payload)], CancellationToken.None,
                    allowAggregateInvalidData: true);
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                RejectWrite("pre-cancel", new ManagedArchive(), [new("file", payload)], canceled.Token, typeof(OperationCanceledException));
                using var during = new CancellationTokenSource();
                RejectWrite("enumeration-cancel", new ManagedArchive(), CanceledEntries(during), during.Token, typeof(OperationCanceledException));
                void RejectWrite(string id, ManagedArchive service, IEnumerable<ManagedArchiveWriteEntry> entries,
                    CancellationToken token, Type? required = null, bool allowAggregateInvalidData = false)
                {
                    var target = Path.Combine(folder, "reject-" + id + extension); File.WriteAllBytes(target, "old output sentinel"u8.ToArray());
                    Rejection(id + extension, target, () => service.WriteArchive(target, entries, token),
                        required ?? typeof(InvalidDataException), allowAggregateInvalidData);
                }
            }
            var finishTarget = Path.Combine(folder, "finish-primary.bz2"); File.WriteAllBytes(finishTarget, "old finish sentinel"u8.ToArray());
            var before = Hash(finishTarget); Exception? error = null;
            try { new ManagedArchive(new(MaximumOutputBytes: 4)).WriteArchive(finishTarget, FaultingEntries()); } catch (Exception e) { error = e; }
            var failures = (error as AggregateException)?.InnerExceptions;
            var bothFailuresAttached = failures is { Count: 2 }
                && failures[0] is IOException { Message: "enumeration-primary-sentinel" }
                && !ReferenceEquals(failures[0], failures[1]);
            Verify("finish-dispose-secondary", bothFailuresAttached && Hash(finishTarget) == before,
                "real bounded BZip2 output: enumeration primary and distinct Finish/Dispose secondary remain attached",
                empty, finishTarget, "", before, error);
        }
        void Rejection(string id, string target, Action operation, Type? required = null, bool allowAggregateInvalidData = false)
        {
            var before = Hash(target); Exception? error = null; try { operation(); } catch (Exception e) { error = e; }
            var expectedError = error is not null && (required is null || error.GetType() == required);
            if (allowAggregateInvalidData && error is AggregateException aggregate)
                expectedError = aggregate.Flatten().InnerExceptions.Any(exception => exception is InvalidDataException);
            Verify(id, expectedError && Hash(target) == before,
                "real API refusal, exact old output SHA retained", source, target, "", before, error);
        }
        void Verify(string id, bool passed, string detail, string input, string target, string expectedPath, string? before = null, Exception? error = null)
        {
            var inputSha = File.Exists(input) ? Hash(input) : null; var after = File.Exists(target) ? Hash(target) : null;
            var projectState = WorkspaceStore.SerializeWorkspace(new() { Entries = [pane.CaptureProject()] });
            var visible = owner.IsVisible; var compareEnabled = pane.CompareButton.IsEnabled; var tabs = owner.SessionPanes.Count();
            var modalSnapshot = modalCount;
            var headlessButton = id.StartsWith("create-", StringComparison.Ordinal) || id.StartsWith("repack-", StringComparison.Ordinal)
                || id.StartsWith("tar-picker-", StringComparison.Ordinal) || id.StartsWith("lifetime-", StringComparison.Ordinal)
                || id is "gzip-modal-retained" or "save-cancel" or "file-cancel" or "old-repack-picker";
            observations.Add(j => { j.WriteStartObject(); j.WriteString("id", id); j.WriteBoolean("passed", passed); j.WriteString("detail", detail);
                j.WriteBoolean("headlessButton", headlessButton); j.WriteString("buttonInput", headlessButton ? "headless MouseDown/MouseUp" : "direct public API");
                j.WriteBoolean("ownedVisible", visible); j.WriteBoolean("compareEnabled", compareEnabled); j.WriteNumber("ownedTabs", tabs); j.WriteNumber("gzipModalCount", modalSnapshot);
                j.WriteBase64String("ownedProject", projectState);
                j.WriteString("input", input); j.WriteString("inputSha256", inputSha); j.WriteString("output", target); j.WriteString("expected", expectedPath);
                j.WriteString("beforeSha256", before); j.WriteString("afterSha256", after); j.WriteString("exceptionType", error?.GetType().FullName); j.WriteString("message", error?.Message);
                j.WritePropertyName("exceptionTree"); WriteExceptionTree(j, error);
                j.WriteStartArray("secondary"); if (error is not null) foreach (System.Collections.DictionaryEntry pair in error.Data) { j.WriteStartObject(); j.WriteString("key", pair.Key.ToString()); j.WriteString("value", pair.Value?.ToString()); j.WriteEndObject(); } j.WriteEndArray(); j.WriteEndObject(); });
            check("bare writer " + id, passed, detail);
        }
        void Picker(string kind, string? title, bool multiple, bool? overwrite, IReadOnlyList<FilePickerFileType>? types)
        {
            var patterns = types?.SelectMany(t => t.Patterns ?? []).ToArray() ?? [];
            pickerRows.Add(j => { j.WriteStartObject(); j.WriteString("kind", kind); j.WriteString("title", title); j.WriteBoolean("allowMultiple", multiple);
                if (overwrite is { } value) j.WriteBoolean("overwrite", value); else j.WriteNull("overwrite"); j.WriteStartArray("patterns"); foreach (var pattern in patterns) j.WriteStringValue(pattern); j.WriteEndArray(); j.WriteEndObject(); });
        }
        void Frame(string id) { Jobs(); using var bitmap = owner.CaptureRenderedFrame() ?? throw new InvalidDataException("owned frame absent");
            var name = id + ".png"; bitmap.Save(Path.Combine(folder, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); pngs.Add(name); }
    }
    private static IEnumerable<ManagedArchiveWriteEntry> CanceledEntries(CancellationTokenSource cancel)
    { yield return new("file", "payload"u8.ToArray()); cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); }
    private static IEnumerable<ManagedArchiveWriteEntry> FaultingEntries()
    { yield return new("empty", ReadOnlyMemory<byte>.Empty); throw new IOException("enumeration-primary-sentinel"); }
    // 上限は一つのtree全体で共有する。Dataの文字列だけでは一次/二次失敗を資格化しない。
    private static void WriteExceptionTree(Utf8JsonWriter json, Exception? exception)
    {
        const int maximumDepth = 8, maximumNodes = 64, maximumMessageCharacters = 4096;
        var nodes = 0;
        Write(exception, 0);
        void Write(Exception? error, int depth)
        {
            if (error is null) { json.WriteNullValue(); return; }
            if (depth > maximumDepth || nodes >= maximumNodes)
            {
                json.WriteStartObject(); json.WriteBoolean("truncated", true);
                json.WriteString("reason", depth > maximumDepth ? "maximumDepth" : "maximumNodes"); json.WriteEndObject();
                return;
            }
            nodes++;
            json.WriteStartObject(); json.WriteString("type", error.GetType().FullName);
            var message = error.Message;
            json.WriteString("message", message.Length <= maximumMessageCharacters ? message : message[..maximumMessageCharacters]);
            json.WriteBoolean("messageTruncated", message.Length > maximumMessageCharacters);
            if (error is OperationCanceledException canceled)
            {
                var token = canceled.CancellationToken;
                json.WriteBoolean("cancellationRequested", token.IsCancellationRequested);
                json.WriteBoolean("tokenCanBeCanceled", token.CanBeCanceled);
                json.WriteString("tokenClassification", !token.CanBeCanceled ? "none"
                    : token.IsCancellationRequested ? "cancelable-requested" : "cancelable-not-requested");
            }
            // Aggregate.InnerExceptionを省略せず、childrenとの重複も原構造通りに残す。
            json.WritePropertyName("inner"); Write(error.InnerException, depth + 1);
            if (error is AggregateException aggregate)
            {
                json.WriteNumber("aggregateChildCount", aggregate.InnerExceptions.Count);
                json.WriteStartArray("children");
                var recorded = 0;
                foreach (var child in aggregate.InnerExceptions)
                {
                    if (depth >= maximumDepth || nodes >= maximumNodes || recorded >= maximumNodes) break;
                    Write(child, depth + 1); recorded++;
                }
                json.WriteEndArray(); json.WriteNumber("recordedAggregateChildren", recorded);
                json.WriteBoolean("childrenTruncated", recorded != aggregate.InnerExceptions.Count);
            }
            json.WriteEndObject();
        }
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static Button Button(Control root, string title) => root.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, title));
    private static void Jobs() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Hit(Window window, Control control)
    {
        control.BringIntoView(); Jobs(); window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window) ?? throw new InvalidDataException("pointer bounds absent");
        if (!control.IsEnabled || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) throw new InvalidDataException("actual button unreachable");
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Jobs();
    }
}
