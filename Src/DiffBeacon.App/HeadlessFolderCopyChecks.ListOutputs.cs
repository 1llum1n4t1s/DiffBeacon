using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static partial class HeadlessFolderCopyChecks
{
    private static void RunListOutputs(MainWindow window, string root, Func<ComparisonPane> add,
        Action<Task> wait, Action<string, bool> check, Action<string> screenshot,
        Action<ComparisonPane> activate, Action<string> close)
    {
        var rows = new List<(string Id, string Target, string? Before, string? After, long BeforeTime, long AfterTime,
            bool Rejected, bool Hook, bool Adopted, bool Passed, string[] Dependencies, string[] InitialDependencies, string[] Order,
            bool PublishedBeforeQueuedAdoption, string? Workspace, string? WorkspaceBefore, string? WorkspaceAfter)>();
        foreach (var id in new[] { "html-initial", "html-late", "html-cancel", "html-stale-candidate", "package-late",
            "workspace-initial", "workspace-late", "line-asset-missing", "working-asset-missing", "workspace-repeat", "workspace-repeat-protected", "workspace-queued-adoption" })
        {
            var location = Path.Combine(root, "output-" + id); Directory.CreateDirectory(location);
            var a = Path.Combine(location, "a.txt"); var b = Path.Combine(location, "b.txt");
            File.WriteAllText(a, "a\n", new UTF8Encoding(false)); File.WriteAllText(b, "b\n", new UTF8Encoding(false));
            var publisher = add(); publisher.ApplyProject(new() { Mode = "Text", LeftPath = a, RightPath = b }); wait(publisher.ComparePathsAsync());
            var observer = add();
            var left = Path.Combine(location, "left"); var right = Path.Combine(location, "right");
            Directory.CreateDirectory(left); Directory.CreateDirectory(right);
            File.WriteAllBytes(Path.Combine(left, "row.bin"), [1]); File.WriteAllBytes(Path.Combine(right, "row.bin"), [2]);
            var acceptedList = Path.Combine(location, "accepted.list"); File.WriteAllText(acceptedList, "a\tX\n", new UTF8Encoding(false));
            var filter = Path.Combine(location, "candidate.flt"); var initialFilter = Path.Combine(location, "accepted.flt");
            static string Filter(string path) => "def: exclude\nfe: RegexReplaceWithList('a','" + path.Replace("\\", "/") + "') != None\n";
            File.WriteAllText(initialFilter, Filter(acceptedList), new UTF8Encoding(false));
            observer.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, FileFilterPath = initialFilter }); wait(observer.ComparePathsAsync());
            var oldModel = observer.FolderListDependencyPaths.ToArray();
            var workspace = id.Contains("asset", StringComparison.Ordinal) ? Path.Combine(location, "workspace.json") : null;
            var target = workspace ?? Path.Combine(location, id == "package-late" ? "output.zip" : id.StartsWith("workspace", StringComparison.Ordinal) ? "workspace.json" : "output.html");
            if (id == "line-asset-missing")
            {
                var list = Path.Combine(location, "source.list"); File.WriteAllText(list, "a\tX\n", new UTF8Encoding(false));
                var literal = list.Replace("\\", "/");
                var expression = "ReplaceWithList(Line,\"" + literal + "\")";
                publisher.ApplyProject(new() { Mode = "Text", LeftPath = a, RightPath = b,
                    LineTransformRules = [new(expression, TablePaneMask.Left)], LineTransformListAssets = new(StringComparer.Ordinal) { [literal] = list } });
                wait(publisher.ComparePathsAsync());
                target = workspace + ".assets/" + Hash(list) + ".replace-list";
            }
            if (id == "working-asset-missing")
            {
                var archive = Path.Combine(location, "source.zip");
                using (var file = File.Create(archive)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                { using var content = zip.CreateEntry("leaf.txt").Open(); content.Write("original\n"u8); }
                ArchiveProjectInput Input() => new() { RootPath = archive, RootSha256 = Hash(archive), LeafEntry = "leaf.txt", InheritedReadOnly = false };
                publisher.ApplyProject(new() { Mode = "Text", LeftArchiveInput = Input(), RightArchiveInput = Input(), LeftReadOnly = true, RightReadOnly = true });
                wait(publisher.ComparePathsAsync()); publisher.LeftEditor.Text = "working text\n"; wait(publisher.SaveAsync(false));
                var copy = publisher.CaptureProject().LeftArchiveInput!.WorkingDocuments!.Single();
                target = workspace + ".assets/" + copy.Sha256 + copy.AssetExtension;
            }
            if (workspace is not null) File.WriteAllText(workspace, "KEEP WORKSPACE\n", new UTF8Encoding(false));
            if (!id.Contains("asset", StringComparison.Ordinal)) File.WriteAllText(target, "a\tX\n", new UTF8Encoding(false));
            if (File.Exists(target)) File.SetLastWriteTimeUtc(target, FixedTime);
            File.WriteAllText(filter, Filter(target), new UTF8Encoding(false));
            var before = Hash(target); var beforeTime = File.GetLastWriteTimeUtc(target).Ticks; var workspaceBefore = workspace is null ? null : Hash(workspace);
            var hook = false; var adopted = false; var rejected = false;
            var order = new List<string>(); var publishedBeforeQueuedAdoption = false;
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancel = new CancellationTokenSource();
            void Adopt()
            {
                if (id == "html-stale-candidate") observer.DirectoryReadyForAdoption = () => observer.LeftPath.Text = left + "-rejected";
                observer.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, FileFilterPath = filter });
                try { wait(observer.ComparePathsAsync()); } finally { observer.DirectoryReadyForAdoption = null; }
                activate(observer); adopted = observer.FolderListDependencyPaths.Contains(Path.GetFullPath(target));
                order.Add("adoption-complete");
            }
            var preflight = id.EndsWith("initial", StringComparison.Ordinal) || id == "workspace-repeat-protected";
            if (id == "workspace-repeat-protected")
            {
                wait(window.SaveWorkspaceAsync(target));
                before = Hash(target); beforeTime = File.GetLastWriteTimeUtc(target).Ticks;
            }
            if (preflight) Adopt();
            activate(publisher);
            window.OutputReadyForPublication = path =>
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), Path.GetFullPath(target))) return;
                window.OutputReadyForPublication = null; hook = true;
                order.Add("publisher-entered");
                if (id == "workspace-queued-adoption")
                {
                    order.Add("adoption-queued");
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            publishedBeforeQueuedAdoption = Hash(target) != before;
                            order.Add("atomic-visible-before-adoption"); Adopt(); queued.TrySetResult();
                        }
                        catch (Exception error) { queued.TrySetException(error); }
                    });
                }
                else if (id == "html-cancel") cancel.Cancel();
                else if (id != "workspace-repeat") Adopt();
            };
            try
            {
                if (id == "package-late") wait(window.PackageWorkspaceAsync(target,
                    [Array.IndexOf(window.SessionPanes.ToArray(), publisher)], new(IncludeDocuments: true, IncludeReport: true), cancel.Token));
                else if (id.StartsWith("workspace", StringComparison.Ordinal) || workspace is not null)
                {
                    wait(window.SaveWorkspaceAsync(workspace ?? target, cancel.Token));
                    if (id == "workspace-repeat") wait(window.SaveWorkspaceAsync(target, cancel.Token));
                }
                else wait(publisher.SaveReportAsync(target, cancel.Token));
            }
            catch (InvalidOperationException) { rejected = true; }
            catch (OperationCanceledException) when (id == "html-cancel" && cancel.IsCancellationRequested) { rejected = true; }
            finally { window.OutputReadyForPublication = null; observer.DirectoryReadyForAdoption = null; }
            if (id == "workspace-queued-adoption") wait(queued.Task);
            var after = Hash(target); var afterTime = File.GetLastWriteTimeUtc(target).Ticks; var workspaceAfter = workspace is null ? null : Hash(workspace);
            var positive = id is "html-stale-candidate" or "workspace-repeat" or "workspace-queued-adoption";
            var passed = positive ? !rejected && before != after : rejected && before == after && beforeTime == afterTime && workspaceBefore == workspaceAfter;
            passed &= preflight ? !hook && adopted : hook;
            if (id is "html-stale-candidate" or "html-cancel") passed &= !adopted && observer.FolderListDependencyPaths.SequenceEqual(oldModel);
            else if (id is not ("html-cancel" or "workspace-repeat")) passed &= adopted;
            if (id == "workspace-repeat") passed &= !File.ReadAllText(target).Contains("folderListDependencies", StringComparison.OrdinalIgnoreCase);
            if (id == "workspace-queued-adoption") passed &= publishedBeforeQueuedAdoption
                && order.SequenceEqual(new[] { "publisher-entered", "adoption-queued", "atomic-visible-before-adoption", "adoption-complete" });
            var captured = WorkspaceStore.CloneProject(observer.CaptureProject()).FolderListDependencies;
            if (adopted) passed &= captured.Contains(Path.GetFullPath(target));
            check("folder list output protection " + id, passed);
            screenshot("folder-list-output-" + id + ".png");
            rows.Add((id, target, before, after, beforeTime, afterTime, rejected, hook, adopted, passed,
                observer.FolderListDependencyPaths.ToArray(), oldModel, order.ToArray(), publishedBeforeQueuedAdoption, workspace, workspaceBefore, workspaceAfter));
            publisher.DiscardChanges(); observer.DiscardChanges(); close("output-" + id);
        }
        using var output = File.Create(Path.Combine(root, "folder-list-output-observations.json"));
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartObject(); json.WriteBoolean("complete", true); json.WriteStartArray("cases");
        foreach (var row in rows)
        {
            json.WriteStartObject(); json.WriteString("id", row.Id); json.WriteString("target", row.Target); json.WriteString("beforeSha256", row.Before); json.WriteString("afterSha256", row.After);
            json.WriteNumber("beforeWriteTicks", row.BeforeTime); json.WriteNumber("afterWriteTicks", row.AfterTime);
            json.WriteBoolean("rejected", row.Rejected); json.WriteBoolean("hook", row.Hook); json.WriteBoolean("adopted", row.Adopted); json.WriteBoolean("passed", row.Passed);
            json.WriteStartArray("dependencies"); foreach (var path in row.Dependencies) json.WriteStringValue(path); json.WriteEndArray();
            json.WriteStartArray("initialDependencies"); foreach (var path in row.InitialDependencies) json.WriteStringValue(path); json.WriteEndArray();
            json.WriteStartArray("order"); foreach (var value in row.Order) json.WriteStringValue(value); json.WriteEndArray();
            json.WriteBoolean("publishedBeforeQueuedAdoption", row.PublishedBeforeQueuedAdoption);
            json.WriteStartArray("dependencySnapshots"); foreach (var path in row.Dependencies)
            { json.WriteStartObject(); json.WriteString("path", path); json.WriteString("sha256", Hash(path)); json.WriteEndObject(); } json.WriteEndArray();
            json.WriteString("workspace", row.Workspace); json.WriteString("workspaceBeforeSha256", row.WorkspaceBefore); json.WriteString("workspaceAfterSha256", row.WorkspaceAfter); json.WriteEndObject();
        }
        json.WriteEndArray(); json.WriteEndObject();

        RunCopyPublication(window, root, add, wait, check, screenshot, activate, close);

        static string? Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
    }
}
