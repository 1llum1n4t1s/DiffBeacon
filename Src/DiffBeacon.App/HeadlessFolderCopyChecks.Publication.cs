using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;

namespace DiffBeacon.App;

internal static partial class HeadlessFolderCopyChecks
{
    private static void RunCopyPublication(MainWindow window, string root, Func<ComparisonPane> add,
        Action<Task> wait, Action<string, bool> check, Action<string> screenshot,
        Action<ComparisonPane> activate, Action<string> close)
    {
        using var output = File.Create(Path.Combine(root, "folder-copy-publication-observations.json"));
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartObject(); json.WriteBoolean("complete", true); json.WriteBoolean("windows", OperatingSystem.IsWindows());
        json.WriteStartArray("cases");
        foreach (var id in new[] { "late-list", "queued-list", "cancel", "failure", "new-parent", "empty-directory" })
        {
            var location = Path.Combine(root, "publication-" + id); Directory.CreateDirectory(location);
            var left = Path.Combine(location, "left"); var right = Path.Combine(location, "right");
            Directory.CreateDirectory(left); Directory.CreateDirectory(right);
            var directoryCase = id is "new-parent" or "empty-directory";
            var relative = id == "new-parent" ? "tree/leaf.list" : id == "empty-directory" ? "empty" : "payload.list";
            var source = Path.Combine(left, relative); var target = Path.Combine(right, relative);
            if (id == "empty-directory")
            {
                Directory.CreateDirectory(source); Directory.CreateDirectory(target);
                Directory.SetLastWriteTimeUtc(source, FixedTime); Directory.SetLastWriteTimeUtc(target, FixedTime.AddDays(-1));
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                File.WriteAllText(source, "a\tNEW\n", new UTF8Encoding(false)); File.SetLastWriteTimeUtc(source, FixedTime);
                if (!directoryCase) { File.WriteAllText(target, "a\tOLD\n", new UTF8Encoding(false)); File.SetLastWriteTimeUtc(target, FixedTime); }
            }
            var publisher = add(); publisher.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, Recursive = true }); wait(publisher.ComparePathsAsync());
            var observer = add();
            var observerLeft = Path.Combine(location, "observer-left"); var observerRight = Path.Combine(location, "observer-right");
            Directory.CreateDirectory(observerLeft); Directory.CreateDirectory(observerRight);
            File.WriteAllBytes(Path.Combine(observerLeft, "row.bin"), [1]); File.WriteAllBytes(Path.Combine(observerRight, "row.bin"), [2]);
            var accepted = Path.Combine(location, "accepted.list"); File.WriteAllText(accepted, "a\tOLD\n", new UTF8Encoding(false));
            static string Filter(string path) => "def: exclude\nfe: RegexReplaceWithList('a','" + path.Replace("\\", "/") + "') != None\n";
            var filter = Path.Combine(location, "accepted.flt"); File.WriteAllText(filter, Filter(accepted), new UTF8Encoding(false));
            observer.ApplyProject(new() { Mode = "Folder", LeftPath = observerLeft, RightPath = observerRight, FileFilterPath = filter }); wait(observer.ComparePathsAsync());
            var initialDependencies = observer.FolderListDependencyPaths.ToArray();
            var before = Hash(target); var sourceBefore = Hash(source); var acceptedBefore = Hash(accepted);
            var beforeTicks = File.GetLastWriteTimeUtc(target).Ticks;
            var guardedPath = id == "new-parent" ? Path.Combine(right, "tree") : target;
            var guardedBeforeTicks = File.GetLastWriteTimeUtc(guardedPath).Ticks;
            var hooked = false; var staged = false; var adopted = false; string? adoptedSha = null;
            var order = new List<string>(); var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Adopt()
            {
                var candidate = Path.Combine(location, "candidate.flt"); File.WriteAllText(candidate, Filter(target), new UTF8Encoding(false));
                observer.ApplyProject(new() { Mode = "Folder", LeftPath = observerLeft, RightPath = observerRight, FileFilterPath = candidate });
                wait(observer.ComparePathsAsync());
                adopted = observer.FolderListDependencyPaths.Contains(Path.GetFullPath(target)); adoptedSha = Hash(target);
                order.Add("adoption-complete");
            }
            activate(publisher); Select(publisher, [relative]);
            publisher.FolderPublicationStarting = path =>
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(path, guardedPath) || hooked) return;
                hooked = true; order.Add("publisher-entered");
                staged = Directory.EnumerateFiles(right, ".diffbeacon-copy-*", SearchOption.AllDirectories).Any();
                if (id == "late-list") Adopt();
                else if (id == "cancel") Click(publisher, "中止");
                else if (id == "failure") throw new IOException("publication regression final failure");
                else if (directoryCase) observer.LeftPath.Text = guardedPath;
            };
            publisher.FolderPublicationCriticalSection = (path, publish) =>
            {
                if (id == "queued-list" && StringComparer.OrdinalIgnoreCase.Equals(path, target))
                {
                    order.Add("adoption-queued");
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { Adopt(); queued.TrySetResult(); } catch (Exception error) { queued.TrySetException(error); }
                    });
                    publish(); order.Add("atomic-published");
                }
                else publish();
            };
            Dialogs.ConfirmationShown = dialog => Answer(dialog, true);
            try
            {
                Click(publisher, "選択をすべてコピー →");
                if (publisher.PendingFolderCopy is { } pending) wait(pending); else CloseFolderMessages(window);
                if (id == "queued-list") wait(queued.Task);
            }
            finally { publisher.FolderPublicationStarting = null; publisher.FolderPublicationCriticalSection = null; Dialogs.ConfirmationShown = null; }
            var result = publisher.LastFolderCopyResult; var after = Hash(target); var afterTicks = File.GetLastWriteTimeUtc(target).Ticks;
            var clean = !Directory.EnumerateFiles(right, ".diffbeacon-copy-*", SearchOption.AllDirectories).Any();
            var positive = id == "queued-list";
            var passed = hooked && clean && Hash(source) == sourceBefore && Hash(accepted) == acceptedBefore;
            passed &= positive ? result is { Succeeded: true, PublishedCount: 1 } && after == sourceBefore && adopted && adoptedSha == after
                && order.SequenceEqual(new[] { "publisher-entered", "adoption-queued", "atomic-published", "adoption-complete" })
                : result is { Succeeded: false, PublishedCount: 0 } && before == after && beforeTicks == afterTicks
                    && guardedBeforeTicks == File.GetLastWriteTimeUtc(guardedPath).Ticks;
            if (id == "late-list") passed &= staged && adopted && adoptedSha == before;
            if (id is "cancel" or "failure") passed &= staged && !adopted && observer.FolderListDependencyPaths.SequenceEqual(initialDependencies);
            if (id == "cancel") passed &= result?.Cancelled == true;
            if (id == "new-parent") passed &= !Directory.Exists(guardedPath) && !File.Exists(target);
            if (OperatingSystem.IsWindows() && positive) passed &= result!.Entries.Single().Metadata?.Windows?.Verified == true;
            check("folder atomic publication " + id, passed); screenshot("folder-publication-" + id + ".png");
            json.WriteStartObject(); json.WriteString("id", id); json.WriteBoolean("passed", passed); json.WriteBoolean("hooked", hooked);
            json.WriteString("source", source); json.WriteString("target", target); json.WriteString("sourceBeforeSha256", sourceBefore); json.WriteString("sourceAfterSha256", Hash(source));
            json.WriteString("beforeSha256", before); json.WriteString("afterSha256", after); json.WriteNumber("beforeWriteTicks", beforeTicks); json.WriteNumber("afterWriteTicks", afterTicks);
            json.WriteString("accepted", accepted); json.WriteString("acceptedBeforeSha256", acceptedBefore); json.WriteString("acceptedAfterSha256", Hash(accepted));
            json.WriteBoolean("staged", staged); json.WriteBoolean("clean", clean); json.WriteBoolean("adopted", adopted); json.WriteString("adoptedSha256", adoptedSha);
            json.WriteBoolean("succeeded", result?.Succeeded == true); json.WriteBoolean("cancelled", result?.Cancelled == true); json.WriteNumber("publishedCount", result?.PublishedCount ?? -1);
            json.WriteBoolean("windowsMetadataVerified", result?.Entries.SingleOrDefault()?.Metadata?.Windows?.Verified == true);
            json.WriteString("guardedPath", guardedPath); json.WriteNumber("guardedBeforeWriteTicks", guardedBeforeTicks); json.WriteNumber("guardedAfterWriteTicks", File.GetLastWriteTimeUtc(guardedPath).Ticks);
            json.WriteStartArray("dependencies"); foreach (var path in observer.FolderListDependencyPaths) json.WriteStringValue(path); json.WriteEndArray();
            json.WriteStartArray("initialDependencies"); foreach (var path in initialDependencies) json.WriteStringValue(path); json.WriteEndArray();
            json.WriteStartArray("order"); foreach (var value in order) json.WriteStringValue(value); json.WriteEndArray(); json.WriteEndObject();
            publisher.DiscardChanges(); observer.DiscardChanges(); close("publication-" + id);
        }
        json.WriteEndArray(); json.WriteEndObject();
        static string? Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
    }
}