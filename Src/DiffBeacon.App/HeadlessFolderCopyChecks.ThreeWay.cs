using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static partial class HeadlessFolderCopyChecks
{
    internal static void RunThreeWayOnly(MainWindow window, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var root = Path.Combine(output, "folder-copy"); Directory.CreateDirectory(root);
        try
        {
            RunThreeWay(window, root, task => WaitFolderOperation(window, task), check, screenshot,
                pane => { window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), pane)); Dispatcher.UIThread.RunJobs(); });
        }
        finally { foreach (var pane in window.SessionPanes) pane.DiscardChanges(); }
    }

    private static void WaitFolderOperation(MainWindow window, Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs(); CloseFolderMessages(window);
            if (clock.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("Folder copy実GUI操作が完了しませんでした。");
            Thread.Sleep(2);
        }
        task.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs(); CloseFolderMessages(window);
    }

    private static void CloseFolderMessages(MainWindow window)
    {
        foreach (var dialog in window.OwnedWindows.Where(d => d.Title is "操作を完了できませんでした" or "コピーを完了できませんでした").ToArray())
            dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "閉じる")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void RunThreeWay(MainWindow window, string root, Action<Task> wait,
        Action<string, bool, string> check, Action<string> screenshot, Action<ComparisonPane> activate)
    {
        var observations = new List<ThreeWayCopyObservation>();
        var protectedRoots = new List<ThreeWayProtectionObservation>();
        ComparisonPane? layout = null;
        try
        {
            foreach (var direction in Enum.GetValues<DirectoryCopyDirection>())
                foreach (var mode in Enum.GetValues<DirectoryCopyMode>())
                    Case(direction + "-" + mode, direction, mode);
            Case("cancel", DirectoryCopyDirection.LeftToMiddle, DirectoryCopyMode.All, confirm: false);
            Case("middle-readonly", DirectoryCopyDirection.LeftToMiddle, DirectoryCopyMode.All, middleReadOnly: true);
            Case("middle-readonly-source", DirectoryCopyDirection.MiddleToRight, DirectoryCopyMode.All, middleReadOnly: true);
            Case("middle-change-during-confirm", DirectoryCopyDirection.LeftToMiddle, DirectoryCopyMode.All, stale: true);
            ProtectedRootCase("third-root-trailing");
            if (OperatingSystem.IsWindows())
            {
                ProtectedRootCase("third-root-extended-double-trailing", @"\\?\");
                ProtectedRootCase("third-root-device-double-trailing", @"\\.\");
                ProtectedRootCase("third-root-long-device-double-trailing", @"\\.\", longRoot: true);
                LongReadonlyOtherTabCase();
                ProtectedRootCase("third-root-localhost-share", localShare: true);
            }
            if (layout is not null)
            {
                activate(layout);
                var beforeWidth = window.Width; var beforeHeight = window.Height;
                foreach (var minimum in new[] { false, true })
                {
                    window.Width = minimum ? 850 : 1280; window.Height = minimum ? 550 : 850;
                    Dispatcher.UIThread.RunJobs();
                    var direction = layout.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "FolderCopyDirection");
                    var copy = layout.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "選択をすべてコピー"));
                    check("Folder Three Way controls visible " + minimum,
                        direction.IsEffectivelyVisible && copy.IsEffectivelyVisible && direction.Bounds.Width > 0 && List(layout).Bounds.Height >= 100,
                        $"direction={direction.Bounds};copy={copy.Bounds};list={List(layout).Bounds}");
                    screenshot("folder-threeway-" + (minimum ? "minimum" : "normal") + ".png");
                }
                window.Width = beforeWidth; window.Height = beforeHeight; Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            Dialogs.ConfirmationShown = null;
            using var stream = File.Create(Path.Combine(root, "folder-threeway-observations.json"));
            using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
            writer.WriteStartArray();
            foreach (var row in observations)
            {
                writer.WriteStartObject(); writer.WriteString("name", row.Name); writer.WriteString("direction", row.Direction.ToString());
                writer.WriteString("mode", row.Mode.ToString()); writer.WriteBoolean("confirmed", row.Confirmed);
                writer.WriteNumber("expectedPublished", row.ExpectedPublished); writer.WriteNumber("published", row.Published);
                writer.WriteStartArray("roots"); foreach (var path in row.Roots) writer.WriteStringValue(path); writer.WriteEndArray();
                writer.WriteStartArray("before"); foreach (var text in row.Before) writer.WriteStringValue(text); writer.WriteEndArray();
                writer.WriteStartArray("after"); foreach (var text in row.After) writer.WriteStringValue(text); writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            using var protectedStream = File.Create(Path.Combine(root, "folder-threeway-protection-observations.json"));
            using var protectedWriter = new Utf8JsonWriter(protectedStream, new() { Indented = true });
            protectedWriter.WriteStartArray();
            foreach (var row in protectedRoots)
            {
                protectedWriter.WriteStartObject(); protectedWriter.WriteString("name", row.Name); protectedWriter.WriteString("middleInput", row.MiddleInput);
                protectedWriter.WriteNumber("confirms", row.Confirms); protectedWriter.WriteNumber("published", row.Published);
                protectedWriter.WriteStartArray("roots"); foreach (var path in row.Roots) protectedWriter.WriteStringValue(path); protectedWriter.WriteEndArray();
                protectedWriter.WriteStartArray("before"); foreach (var text in row.Before) protectedWriter.WriteStringValue(text); protectedWriter.WriteEndArray();
                protectedWriter.WriteStartArray("after"); foreach (var text in row.After) protectedWriter.WriteStringValue(text); protectedWriter.WriteEndArray();
                protectedWriter.WriteEndObject();
            }
            protectedWriter.WriteEndArray();
        }

        void LongReadonlyOtherTabCase()
        {
            var work = Path.Combine(root, "threeway", "readonly-other-tab-long-device");
            while (Path.Combine(work, "right").Length < 270) work = Path.Combine(work, "long-path-component-0123456789");
            var roots = new[] { Path.Combine(work, "left"), Path.Combine(work, "middle"), Path.Combine(work, "right") };
            var before = new[] { "left\n", "middle\n", "right\n" };
            for (var side = 0; side < 3; side++)
            {
                Directory.CreateDirectory(roots[side]);
                File.WriteAllText(Path.Combine(roots[side], "entry.txt"), before[side], new UTF8Encoding(false));
            }
            var pane = window.AddSession();
            pane.ApplyProject(new() { Mode = "Folder", LeftPath = roots[0], BasePath = roots[1], RightPath = roots[2] });
            wait(pane.ComparePathsAsync());
            var raw = @"\\.\" + roots[2] + @"\\";
            var readOnlyPane = window.AddSession();
            readOnlyPane.ApplyProject(new() { Mode = "Folder", LeftPath = raw, LeftReadOnly = true });
            var captured = readOnlyPane.CaptureProject();
            activate(pane); Select(pane, ["entry.txt"]);
            pane.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "FolderCopyDirection").SelectedIndex = (int)DirectoryCopyDirection.LeftToRight;
            var confirms = 0;
            Dialogs.ConfirmationShown = dialog => { confirms++; Answer(dialog, true); };
            Click(pane, "選択をすべてコピー");
            if (pane.PendingFolderCopy is { } operation) wait(operation);
            Dialogs.ConfirmationShown = null; CloseFolderMessages(window);
            var after = roots.Select(path => File.ReadAllText(Path.Combine(path, "entry.txt"))).ToArray();
            var published = pane.LastFolderCopyResult?.PublishedCount ?? 0;
            RecordCopyDiagnostic(root, "threeway-readonly-other-tab-long-device", pane, roots[0], roots[2], DirectoryCopyDirection.LeftToRight, DirectoryCopyMode.All, "readonly-output-protection");
            check("Folder Three Way long readonly raw input", captured.LeftPath == raw && captured.LeftReadOnly, "uncompared other tab retains DOS long readonly root");
            check("Folder Three Way long readonly confirmation", confirms == 1, "readonly descendants refused by the existing pre-publication check after confirmation");
            check("Folder Three Way long readonly no publication", published == 0, "no overwrite");
            check("Folder Three Way long readonly all bytes", after.SequenceEqual(before), "all three fixed inputs retained");
            using var stream = File.Create(Path.Combine(root, "folder-threeway-long-readonly.json"));
            using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
            writer.WriteStartObject(); writer.WriteString("rawReadOnlyRoot", raw); writer.WriteBoolean("capturedReadOnly", captured.LeftReadOnly);
            writer.WriteString("capturedPath", captured.LeftPath); writer.WriteNumber("confirms", confirms); writer.WriteNumber("published", published);
            writer.WriteStartArray("roots"); foreach (var path in roots) writer.WriteStringValue(path); writer.WriteEndArray();
            writer.WriteStartArray("before"); foreach (var text in before) writer.WriteStringValue(text); writer.WriteEndArray();
            writer.WriteStartArray("after"); foreach (var text in after) writer.WriteStringValue(text); writer.WriteEndArray(); writer.WriteEndObject();
        }

        void ProtectedRootCase(string name, string? devicePrefix = null, bool localShare = false, bool longRoot = false)
        {
            var work = Path.Combine(root, "threeway", name);
            if (longRoot)
                while (Path.Combine(work, "right", "third").Length < 270) work = Path.Combine(work, "long-path-component-0123456789");
            var left = Path.Combine(work, "left"); var right = Path.Combine(work, "right"); var middle = Path.Combine(right, "third");
            Directory.CreateDirectory(Path.Combine(left, "third")); Directory.CreateDirectory(middle);
            var files = new[] { Path.Combine(left, "third", "entry.txt"), Path.Combine(middle, "entry.txt"), Path.Combine(right, "entry.txt") };
            var before = new[] { "new\n", "protected\n", "right\n" };
            for (var side = 0; side < files.Length; side++) File.WriteAllText(files[side], before[side], new UTF8Encoding(false));
            var middleInput = localShare ? @"\\localhost\" + middle[0] + "$" + middle[2..]
                : devicePrefix is not null ? devicePrefix + middle + @"\\" : middle + Path.DirectorySeparatorChar;
            if (localShare)
            {
                var eligible = Directory.Exists(middleInput);
                using var file = File.Create(Path.Combine(root, "folder-threeway-physical-alias-environment.json"));
                using var json = new Utf8JsonWriter(file);
                json.WriteStartObject(); json.WriteString("canonicalRoot", middle); json.WriteString("middleInput", middleInput);
                json.WriteBoolean("eligible", eligible); json.WriteEndObject(); json.Flush();
                if (!eligible) return;
            }
            var pane = window.AddSession();
            pane.ApplyProject(new() { Mode = "Folder", LeftPath = left, BasePath = middleInput, RightPath = right });
            wait(pane.ComparePathsAsync()); activate(pane); Select(pane, ["third/entry.txt"]);
            check("Folder Three Way protection middle path " + name, pane.CaptureProject().BasePath == middleInput, "actual input alias retained");
            pane.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "FolderCopyDirection").SelectedIndex = (int)DirectoryCopyDirection.LeftToRight;
            var confirms = 0;
            Dialogs.ConfirmationShown = dialog => { confirms++; Answer(dialog, true); };
            Click(pane, "選択をすべてコピー");
            if (pane.PendingFolderCopy is { } task) wait(task);
            CloseFolderMessages(window); Dialogs.ConfirmationShown = null;
            var after = files.Select(File.ReadAllText).ToArray();
            var published = pane.LastFolderCopyResult?.PublishedCount ?? 0;
            RecordCopyDiagnostic(root, "threeway-" + name, pane, left, right, DirectoryCopyDirection.LeftToRight, DirectoryCopyMode.All, "third-root-protection");
            check("Folder Three Way protection no confirmation " + name, confirms == 0, "reject before confirmation");
            check("Folder Three Way protection no publication " + name, published == 0, "third root is not a destination");
            check("Folder Three Way protection all bytes " + name, after.SequenceEqual(before), "new/protected/right fixed bytes retained");
            protectedRoots.Add(new(name, [left, middle, right], middleInput, confirms, published, before, after));
        }

        void Case(string name, DirectoryCopyDirection direction, DirectoryCopyMode mode, bool confirm = true,
            bool middleReadOnly = false, bool stale = false)
        {
            var work = Path.Combine(root, "threeway", name);
            var roots = new[] { Path.Combine(work, "left"), Path.Combine(work, "middle"), Path.Combine(work, "right") };
            var before = new[] { "left\n", "middle\n", "right\n" };
            for (var side = 0; side < 3; side++)
            {
                Directory.CreateDirectory(roots[side]);
                File.WriteAllText(Path.Combine(roots[side], "entry.txt"), before[side], new UTF8Encoding(false));
            }
            var pane = window.AddSession();
            pane.ApplyProject(new() { Mode = "Folder", LeftPath = roots[0], BasePath = roots[1], RightPath = roots[2], BaseReadOnly = middleReadOnly });
            wait(pane.ComparePathsAsync()); activate(pane); Select(pane, ["entry.txt"]);
            var entry = List(pane).Items.OfType<DirectoryEntry>().Single();
            check("Folder Three Way model " + name, entry.ThreeWay?.Presence == DirectoryPresence.All
                && entry.ThreeWay.Classification == DirectoryThreeWayClassification.AllChanged && entry.MiddlePath == Path.Combine(roots[1], "entry.txt"), "three physical sides, original Full classification");
            var captured = pane.CaptureProject();
            check("Folder Three Way project middle " + name, captured.BasePath == roots[1] && captured.BaseReadOnly == middleReadOnly, "existing project fields retain middle and readonly");
            pane.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "FolderCopyDirection").SelectedIndex = (int)direction;
            var didConfirm = false;
            Dialogs.ConfirmationShown = dialog =>
            {
                didConfirm = true;
                if (stale) { pane.BasePath.Text = roots[0]; pane.BasePath.Text = roots[1]; }
                Answer(dialog, confirm);
            };
            Click(pane, mode == DirectoryCopyMode.All ? "選択をすべてコピー" : "選択の差分をコピー");
            if (pane.PendingFolderCopy is { } task) wait(task);
            Dialogs.ConfirmationShown = null;
            var (source, destination) = DirectorySideMapping.GetSides(direction);
            RecordCopyDiagnostic(root, "threeway-" + name, pane, roots[(int)source], roots[(int)destination], direction, mode, stale ? "injected-confirmation-change" : !confirm ? "confirmation-cancel" : middleReadOnly ? "readonly-copy" : "copy");
            var copied = confirm && !stale && !(destination == DirectorySide.Middle && middleReadOnly);
            var after = roots.Select(path => File.ReadAllText(Path.Combine(path, "entry.txt"))).ToArray();
            check("Folder Three Way copy bytes " + name, after.Select((text, side) => text ==
                (copied && side == (int)destination ? before[(int)source] : before[side])).All(value => value), "all three fixed bytes, target only");
            check("Folder Three Way published " + name, (pane.LastFolderCopyResult?.PublishedCount ?? 0) == (copied ? 1 : 0), "actual button/dialog result");
            if (copied)
            {
                check("Folder Three Way refresh middle " + name, List(pane).Items.OfType<DirectoryEntry>().Single().MiddlePath == Path.Combine(roots[1], "entry.txt"), "refresh retains third root");
                layout = pane;
            }
            observations.Add(new(name, direction, mode, roots, before, after, didConfirm, copied ? 1 : 0, pane.LastFolderCopyResult?.PublishedCount ?? 0));
        }
    }

    private sealed record ThreeWayCopyObservation(string Name, DirectoryCopyDirection Direction, DirectoryCopyMode Mode,
        string[] Roots, string[] Before, string[] After, bool Confirmed, int ExpectedPublished, int Published);
    private sealed record ThreeWayProtectionObservation(string Name, string[] Roots, string MiddleInput,
        int Confirms, int Published, string[] Before, string[] After);
}
