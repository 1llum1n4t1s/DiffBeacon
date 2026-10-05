using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessFolderCopyChecks
{
    private static readonly DateTime FixedTime = new(2002, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    internal static void Run(MainWindow window, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> screenshot)
    {
        var root = Path.Combine(output, "folder-copy"); Directory.CreateDirectory(root);
        var observations = new List<Observation>();
        var cancellationRows = new List<(Observation Row, string? Status, string Png, StreamMetadata[] Before, StreamMetadata[] After)>();
        var reviewRows = new List<(string Id, string Work, Entry[] Before, Entry[] After, bool Rejected, bool AdsAccepted, bool CopyAttempted, string? AdsPath, string? AdsBefore, string? AdsAfter)>();
        var extendedAccepted = false; string? extendedRequested = null; string? extendedObserved = null; string? extendedInputText = null; var extendedNormalized = false;
        var confirmations = 0;
        try
        {
            foreach (var toRight in new[] { true, false }) foreach (var all in new[] { true, false })
                Case($"normal-{(toRight ? "L" : "R")}-{(all ? "all" : "diff")}", toRight, all, ["a.bin", "same.bin", "empty"],
                    all ? ["a.bin", "same.bin"] : ["a.bin"]);
            Case("confirmation-cancel", true, true, ["a.bin"], [], confirm: false);
            Case("same-mtime-source-change", true, true, ["a.bin"], [], beforeConfirm: (pane, left, right) =>
            { File.WriteAllBytes(Path.Combine(left, "a.bin"), [0x91]); File.SetLastWriteTimeUtc(Path.Combine(left, "a.bin"), FixedTime); });
            Case("unknown-member", true, true, ["tree"], [], beforeConfirm: (pane, left, right) =>
            { File.WriteAllBytes(Path.Combine(left, "tree", "late.bin"), [0x77]); File.SetLastWriteTimeUtc(Path.Combine(left, "tree", "late.bin"), FixedTime); Directory.SetLastWriteTimeUtc(Path.Combine(left,"tree"),FixedTime); }, recursive: false);
            Case("scanned-model-unknown-member", true, true, ["tree"], ["tree/leaf.bin"], beforeConfirm: (pane, left, right) =>
            { File.WriteAllBytes(Path.Combine(left,"tree","late.bin"),[0x77]);File.SetLastWriteTimeUtc(Path.Combine(left,"tree","late.bin"),FixedTime);Directory.SetLastWriteTimeUtc(Path.Combine(left,"tree"),FixedTime); });
            foreach (var name in new[] { "readonly-revert", "input-revert", "mode-revert", "setting-revert", "tab-revert", "cancel-button" })
                Case(name, true, true, ["a.bin"], [], beforeConfirm: (pane, left, right) =>
                {
                    if (name == "readonly-revert") { Settings(pane, true); Settings(pane, false); }
                    else if (name == "input-revert") { pane.LeftPath.Text = right; pane.LeftPath.Text = left; }
                    else if (name == "mode-revert") { var mode = pane.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Items.Cast<object>().Any(v => Equals(v, "フォルダー"))); var prior = mode.SelectedIndex; mode.SelectedIndex = 0; mode.SelectedIndex = prior; }
                    else if (name == "setting-revert") { var box = pane.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "大文字小文字を無視")); box.IsChecked = true; box.IsChecked = false; }
                    else if (name == "tab-revert") { window.SelectSession(0); Activate(pane); }
                    else Click(pane, "中止");
                });
            Case("partial-output-failure", true, true, ["a.bin", "b.bin", "same.bin"], ["a.bin"], outputFault: "fail");
            Case("partial-output-cancel", true, true, ["a.bin", "b.bin", "same.bin"], ["a.bin"], outputFault: "cancel");
            Case("cancel-preflight", true, true, ["a.bin", "b.bin", "same.bin"], [], outputFault: "cancel-preflight");
            Case("cancel-refresh", true, true, ["a.bin"], ["a.bin"]);
            Case("refresh-adoption-rejected", true, true, ["a.bin"], ["a.bin"], rejectRefresh: true);
            Case("source-readonly", true, true, ["a.bin"], ["a.bin"], readonlySource: true);
            Case("legacy-single-diff", true, false, ["a.bin"], ["a.bin"], legacyButton: true);
            Case("budget-exact", true, true, ["a.bin", "b.bin"], ["a.bin", "b.bin"], limits: new() { MaximumEntries = 2, MaximumDepth = 1, MaximumLogicalBytes = 2, MaximumIoBytes = 18 });
            Case("budget-entry-over", true, true, ["a.bin", "b.bin"], [], limits: new() { MaximumEntries = 1 });
            Case("budget-depth-exact", true, true, ["tree/leaf.bin"], ["tree/leaf.bin"], limits: new() { MaximumDepth = 2 });
            Case("budget-depth-over", true, true, ["tree/leaf.bin"], [], limits: new() { MaximumDepth = 1 });
            Case("budget-logical-over", true, true, ["a.bin", "b.bin"], [], limits: new() { MaximumLogicalBytes = 1 });
            Case("budget-io-over", true, true, ["a.bin", "b.bin"], [], limits: new() { MaximumIoBytes = 17 });
            foreach (var limit in new (string Name, FolderCopyLimits Value)[]
            {
                ("entries", new() { MaximumEntries = 100_001 }), ("depth", new() { MaximumDepth = 257 }),
                ("logical", new() { MaximumLogicalBytes = (1L << 40) + 1 }), ("io", new() { MaximumIoBytes = (5L << 40) + 1 })
            }) Case("hard-limit-" + limit.Name, true, true, ["a.bin"], [], limits: limit.Value);
            Case("all-tab-file-protection", true, true, ["a.bin"], [], protection: "file");
            Case("all-tab-folder-protection", true, true, ["a.bin"], [], protection: "folder");
            Case("all-tab-filter-protection", true, true, ["a.bin"], [], protection: "filter");
            Case("all-tab-accepted-path-protection", true, true, ["a.bin"], [], protection: "accepted");
            Case("all-tab-archive-protection", true, true, ["a.bin"], [], protection: "archive");
            Case("workspace-protection", true, true, ["a.bin"], [], protection: "workspace");
            foreach (var mode in new[] { "Folder", "Binary", "Image" })
                foreach (var accept in new[] { false, true }) StaleConfirmation(mode, accept);
            foreach (var mode in new[] { "Folder", "Binary", "Image" }) StalePreparation(mode);

            foreach (var toRight in new[] { true, false }) foreach (var all in new[] { true, false }) ReviewNested(toRight, all);
            ReviewFilterChange();
            if (OperatingSystem.IsWindows()) ReviewCentralAds();
            if (OperatingSystem.IsWindows()) ReviewExtendedDirectory();

            WindowsStreamCases();
            WindowsStreamBudgetCases();

            var layout = Create("layout", out var layoutLeft, out var layoutRight);
            Select(layout, ["a.bin", "b.bin"]); window.Width = 1280; window.Height = 850;
            Dispatcher.UIThread.RunJobs(); Layout(layout, "normal"); screenshot("folder-copy-normal.png");
            window.Width = 850; window.Height = 550; Dispatcher.UIThread.RunJobs(); Layout(layout, "minimum"); screenshot("folder-copy-minimum.png");
            while(window.SessionPanes.Count>45)
            {
                var paneToClose=window.SessionPanes.First(p=>!ReferenceEquals(p,layout));
                var tab=window.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Items.OfType<TabItem>().Any(i=>ReferenceEquals(i.Content,paneToClose)));
                var item=tab.Items.OfType<TabItem>().Single(i=>ReferenceEquals(i.Content,paneToClose));
                ((StackPanel)item.Header!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
            }
            while (window.SessionPanes.Count < 45) window.AddSession(); Activate(layout); Dispatcher.UIThread.RunJobs();
            Report("minimum exact 45 tabs",window.SessionPanes.Count==45);
            Layout(layout, "minimum-many-tabs"); screenshot("folder-copy-minimum-many-tabs.png");
            window.Width = 1280; window.Height = 850;
        }
        finally
        {
            Dialogs.ConfirmationShown = null;
            foreach (var pane in window.SessionPanes) { pane.FolderPlanReady = null; pane.FolderExecutionStarting = null; pane.FolderOutputChecking = null; pane.DirectoryReadyForAdoption = null; pane.DiscardChanges(); }
            using var file = File.Create(Path.Combine(root, "folder-copy-observations.json"));
            using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
            json.WriteStartObject(); json.WriteBoolean("complete", true); json.WriteNumber("confirmations", confirmations);
            json.WriteStartArray("cases"); foreach (var value in observations) Write(json, value); json.WriteEndArray(); json.WriteEndObject();
            using var cancellationFile = File.Create(Path.Combine(root, "folder-cancellation-observations.json"));
            using var cancellationJson = new Utf8JsonWriter(cancellationFile, new JsonWriterOptions { Indented = true });
            cancellationJson.WriteStartObject(); cancellationJson.WriteBoolean("complete", true); cancellationJson.WriteStartArray("cases");
            foreach (var row in cancellationRows)
            {
                cancellationJson.WriteStartObject(); cancellationJson.WriteString("status", row.Status); cancellationJson.WriteString("png", row.Png);
                cancellationJson.WritePropertyName("observation"); Write(cancellationJson, row.Row);
                cancellationJson.WritePropertyName("beforeMetadata"); WriteStreamMetadata(cancellationJson, row.Before);
                cancellationJson.WritePropertyName("afterMetadata"); WriteStreamMetadata(cancellationJson, row.After); cancellationJson.WriteEndObject();
            }
            cancellationJson.WriteEndArray(); cancellationJson.WriteEndObject();
            using var reviewFile = File.Create(Path.Combine(root, "folder-review-observations.json"));
            using var reviewJson = new Utf8JsonWriter(reviewFile, new JsonWriterOptions { Indented = true });
            reviewJson.WriteStartObject(); reviewJson.WriteStartArray("cases");
            foreach (var row in reviewRows)
            {
                reviewJson.WriteStartObject(); reviewJson.WriteString("id", row.Id); reviewJson.WriteString("work", row.Work);
                reviewJson.WriteBoolean("rejected", row.Rejected); reviewJson.WriteBoolean("adsAccepted", row.AdsAccepted); reviewJson.WriteBoolean("copyAttempted", row.CopyAttempted);
                reviewJson.WriteString("adsPath", row.AdsPath); reviewJson.WriteString("adsBefore", row.AdsBefore); reviewJson.WriteString("adsAfter", row.AdsAfter);
                if (row.Id == "extended-directory-container")
                {
                    reviewJson.WriteBoolean("folderAccepted", extendedAccepted); reviewJson.WriteString("requestedRoot", extendedRequested);
                    reviewJson.WriteString("observedRoot", extendedObserved); reviewJson.WriteString("inputText", extendedInputText); reviewJson.WriteBoolean("extendedPrefixNormalized", extendedNormalized);
                }
                WriteEntries(reviewJson, "before", row.Before); WriteEntries(reviewJson, "after", row.After); reviewJson.WriteEndObject();
            }
            reviewJson.WriteEndArray(); reviewJson.WriteEndObject();
        }


        void WindowsStreamCases()
        {
            if (!OperatingSystem.IsWindows()) return;
            var rows = new List<StreamObservation>();
            try
            {
                foreach (var id in new[] { "overwrite", "fresh", "empty-main", "plain", "directory", "long-path", "io179", "io180", "source-sha-change", "destination-sha-change", "cancel" })
                {
                    var location = Path.GetFullPath(Path.Combine(root, "streams", id));
                    if (Directory.Exists(location)) throw new IOException("Stream GUI fixtureは新runへ作成してください。");
                    var left = Path.Combine(location, "left"); var right = Path.Combine(location, "right");
                    var relative = id == "directory" ? "tree/payload.bin" : id == "long-path" ? string.Join('/', Enumerable.Repeat(new string('l', 40), 8).Append("payload.bin")) : "payload.bin";
                    var sourcePath = Path.Combine(left, relative); var destinationPath = Path.Combine(right, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!); Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    WriteSource(sourcePath, id);
                    if (id != "fresh")
                    {
                        WriteStream(destinationPath, "", "A0A1A2A3A4A5A6"); WriteStream(destinationPath, ":named:$DATA", "B0B1"); WriteStream(destinationPath, ":old:$DATA", "C0C1C2");
                    }
                    if (id == "directory")
                    {
                        WriteStream(Path.Combine(left, "tree"), ":source-dir:$DATA", "41424344");
                        WriteStream(Path.Combine(right, "tree"), ":keep-dir:$DATA", "515253");
                    }
                    foreach (var path in Directory.EnumerateFileSystemEntries(location, "*", SearchOption.AllDirectories))
                        if (Directory.Exists(path)) Directory.SetLastWriteTimeUtc(path, FixedTime); else File.SetLastWriteTimeUtc(path, FixedTime);
                    var destinationDirectoryTime = new DateTime(2011, 4, 5, 6, 7, 8, DateTimeKind.Utc);
                    foreach (var directory in Directory.EnumerateDirectories(right, "*", SearchOption.AllDirectories).Prepend(right))
                        Directory.SetLastWriteTimeUtc(directory, destinationDirectoryTime);
                    var beforeMetadata = CaptureStreamMetadata(location);
                    WriteStreamMetadataFile(Path.Combine(location, "before-metadata.json"), beforeMetadata);
                    var sourceMtime = File.GetLastWriteTimeUtc(sourcePath).ToFileTimeUtc();
                    var destinationMtime = id == "fresh" ? 0 : File.GetLastWriteTimeUtc(destinationPath).ToFileTimeUtc();
                    var pane = window.AddSession();
                    pane.FolderCopyVerificationLimits = id == "io179" ? new() { MaximumIoBytes = 179 } : id == "io180" ? new() { MaximumIoBytes = 180 } : null;
                    pane.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, Recursive = true }); Wait(pane.ComparePathsAsync()); Activate(pane);
                    Select(pane, [id == "directory" ? "tree" : relative]);
                    var confirmed = false; FolderCopyPlan? plan = null;
                    pane.FolderPlanReady = value => plan = value;
                    Dialogs.ConfirmationShown = dialog =>
                    {
                        confirmed = true;
                        if (id == "source-sha-change") { WriteStream(sourcePath, ":日本😀:$DATA", "303132333435363738"); File.SetLastWriteTimeUtc(sourcePath, FixedTime); }
                        if (id == "destination-sha-change") { WriteStream(destinationPath, ":old:$DATA", "D0D1D2"); File.SetLastWriteTimeUtc(destinationPath, FixedTime); }
                        Answer(dialog, id != "cancel");
                    };
                    Click(pane, "選択をすべてコピー →");
                    if (pane.PendingFolderCopy is { } pending) Wait(pending); else CloseMessages();
                    var result = pane.LastFolderCopyResult;
                    var afterMetadata = CaptureStreamMetadata(location);
                    WriteStreamMetadataFile(Path.Combine(location, "after-metadata.json"), afterMetadata);
                    var success = id is not ("io179" or "source-sha-change" or "destination-sha-change" or "cancel");
                    Report("stream " + id + " actual button result", (result?.Succeeded == true) == success && confirmed == (id != "io179"));
                    if (id is "source-sha-change" or "destination-sha-change")
                        Report("stream " + id + " preflight zero mutation", result is { Succeeded: false, MutationOccurred: false, PublishedCount: 0 });
                    if (id is "io180" or "overwrite")
                        Report("stream " + id + " 24/12/180/156/24", plan is { LogicalBytes: 24 } && result is { ReadBytes: 156, WriteBytes: 24 });
                    rows.Add(new(id, location, true, confirmed, success,
                        result?.Succeeded, result?.MutationOccurred ?? false, result?.PublishedCount ?? 0,
                        plan?.LogicalBytes, result?.ReadBytes, result?.WriteBytes,
                        result?.Entries.Select(e => e.RelativePath).ToArray() ?? [], sourceMtime, destinationMtime, beforeMetadata, afterMetadata));
                    screenshot("folder-stream-" + id + ".png");
                    pane.FolderPlanReady = null; Dialogs.ConfirmationShown = null;
                }
            }
            finally
            {
                WriteStreamObservationsFile(Path.Combine(root, "folder-stream-observations.json"), rows);
            }
        }
        void WindowsStreamBudgetCases()
        {
            if (!OperatingSystem.IsWindows()) return;
            var rows = new List<StreamBudgetObservation>();
            try
            {
                // 各fileは7 descriptors/75 UTF16 units。2file共有で14/150になる。
                foreach (var (id, limits, success) in new (string, FolderCopyLimits, bool)[]
                {
                    ("descriptors14", new() { MaximumStreamDescriptors = 14 }, true),
                    ("descriptors13", new() { MaximumStreamDescriptors = 13 }, false),
                    ("names150", new() { MaximumStreamNameCharacters = 150 }, true),
                    ("names149", new() { MaximumStreamNameCharacters = 149 }, false)
                })
                {
                    var location = Path.GetFullPath(Path.Combine(root, "stream-budgets", id));
                    if (Directory.Exists(location)) throw new IOException("Stream budget GUI fixtureは新runへ作成してください。");
                    var left = Path.Combine(location, "left"); var right = Path.Combine(location, "right");
                    Directory.CreateDirectory(left); Directory.CreateDirectory(right);
                    string[] paths = ["alpha.bin", "beta.bin"];
                    foreach (var path in paths)
                    {
                        WriteSource(Path.Combine(left, path), id);
                        var destination = Path.Combine(right, path);
                        WriteStream(destination, "", "A0A1A2A3A4A5A6");
                        WriteStream(destination, ":named:$DATA", "B0B1"); WriteStream(destination, ":old:$DATA", "C0C1C2");
                    }
                    foreach (var path in Directory.EnumerateFileSystemEntries(location, "*", SearchOption.AllDirectories))
                        if (Directory.Exists(path)) Directory.SetLastWriteTimeUtc(path, FixedTime); else File.SetLastWriteTimeUtc(path, FixedTime);
                    Directory.SetLastWriteTimeUtc(right, new DateTime(2011, 4, 5, 6, 7, 8, DateTimeKind.Utc));
                    var before = CaptureStreamMetadata(location);
                    WriteStreamMetadataFile(Path.Combine(location, "before-metadata.json"), before);
                    var pane = window.AddSession(); pane.FolderCopyVerificationLimits = limits;
                    pane.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, Recursive = true });
                    Wait(pane.ComparePathsAsync()); Activate(pane); Select(pane, paths);
                    FolderCopyPlan? plan = null; var confirmed = false;
                    pane.FolderPlanReady = value => plan = value;
                    Dialogs.ConfirmationShown = dialog => { confirmed = true; Answer(dialog, true); };
                    Click(pane, "選択をすべてコピー →");
                    if (pane.PendingFolderCopy is { } pending) Wait(pending); else CloseMessages();
                    var result = pane.LastFolderCopyResult;
                    var after = CaptureStreamMetadata(location);
                    WriteStreamMetadataFile(Path.Combine(location, "after-metadata.json"), after);
                    Report("stream budget " + id + " actual button/confirmation", confirmed == success && (result?.Succeeded == true) == success);
                    if (success)
                        Report("stream budget " + id + " shared14/150/48/24/360/312/48", plan is { StreamDescriptors: 14, StreamNameCharacters: 150, LogicalBytes: 48, DestinationBytes: 24, PlannedIoBytes: 360 }
                            && result is { Succeeded: true, MutationOccurred: true, PublishedCount: 2, ReadBytes: 312, WriteBytes: 48 });
                    else
                        Report("stream budget " + id + " preparation refused before confirmation/publication", plan is null && result is null && before.SequenceEqual(after));
                    rows.Add(new(id, location, confirmed, success, limits, plan, result, before, after));
                    screenshot("folder-stream-budget-" + id + ".png");
                    pane.FolderPlanReady = null; Dialogs.ConfirmationShown = null;
                }
            }
            finally
            {
                Dialogs.ConfirmationShown = null;
                WriteStreamBudgetObservationsFile(Path.Combine(root, "folder-stream-budget-observations.json"), rows);
            }
        }
        void WriteSource(string path, string id)
        {
            WriteStream(path, "", id == "empty-main" ? "" : "0102030405");
            if (id == "plain") return;
            WriteStream(path, ":empty:$DATA", ""); WriteStream(path, ":trailing :$DATA", "10111213141516171819"); WriteStream(path, ":日本😀:$DATA", "202122232425262728");
        }
        void WriteStream(string path, string suffix, string hex)
        {
            // fixtureも末尾空白suffixを保持。productのnative helperを使用しない。
            var absolute = Path.GetFullPath(path);
            var extended = absolute.StartsWith(@"\\?\", StringComparison.Ordinal) ? absolute : absolute.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + absolute[2..] : @"\\?\" + absolute;
            File.WriteAllBytes(extended + suffix, Convert.FromHexString(hex));
        }

        void ReviewNested(bool toRight, bool all)
        {
            var id = $"nested-{(toRight ? "L" : "R")}-{(all ? "all" : "diff")}";
            var location = Path.Combine(root, "review-" + id);
            foreach (var side in new[] { "left", "right" })
            {
                Directory.CreateDirectory(Path.Combine(location, side, "Sub", "Sub"));
                File.WriteAllBytes(Path.Combine(location, side, "a.bin"), [side == "left" ? (byte)0x01 : (byte)0x11]);
                File.WriteAllBytes(Path.Combine(location, side, "Sub", "a.bin"), [side == "left" ? (byte)0x02 : (byte)0x12]);
                File.WriteAllBytes(Path.Combine(location, side, "Sub", "Sub", "a.bin"), [side == "left" ? (byte)0x03 : (byte)0x13]);
            }
            foreach (var path in Directory.EnumerateFileSystemEntries(location, "*", SearchOption.AllDirectories))
                if (Directory.Exists(path)) Directory.SetLastWriteTimeUtc(path, FixedTime); else File.SetLastWriteTimeUtc(path, FixedTime);
            var pane = window.AddSession();
            pane.ApplyProject(new() { Mode = "Folder", LeftPath = Path.Combine(location, "left"), RightPath = Path.Combine(location, "left", "Sub") });
            Wait(pane.ComparePathsAsync()); Activate(pane); Select(pane, ["a.bin", "Sub/a.bin"]);
            var before = Snapshot(location); var confirmed = false;
            Dialogs.ConfirmationShown = dialog => { confirmed = true; Answer(dialog, true); };
            Click(pane, toRight ? all ? "選択をすべてコピー →" : "選択の差分をコピー →" : all ? "← 選択をすべてコピー" : "← 選択の差分をコピー");
            if (pane.PendingFolderCopy is { } pending) Wait(pending); else CloseMessages();
            var after = Snapshot(location);
            var rejected = !confirmed && pane.LastFolderCopyResult is null && before.SequenceEqual(after);
            Report(id + " prepare rejects overlapping roots", rejected);
            reviewRows.Add((id, location, before, after, rejected, false, true, null, null, null));
        }

        void ReviewCentralAds()
        {
            const string id = "central-ads-container";
            var location = Path.Combine(root, "review-" + id); var left = Path.Combine(location, "left"); var right = Path.Combine(location, "right");
            Directory.CreateDirectory(left); Directory.CreateDirectory(right);
            File.WriteAllBytes(Path.Combine(left, "base.bin"), [0x01, 0x02]); File.WriteAllBytes(Path.Combine(left, "support.bin"), [0x11, 0x12]);
            File.WriteAllBytes(Path.Combine(right, "base.bin"), [0x7A, 0x62]);
            var ads = Path.Combine(right, "base.bin") + ":protected"; File.WriteAllBytes(ads, [0x03, 0x14, 0xFF]);
            foreach (var path in Directory.EnumerateFileSystemEntries(location, "*", SearchOption.AllDirectories))
                if (Directory.Exists(path)) Directory.SetLastWriteTimeUtc(path, FixedTime); else File.SetLastWriteTimeUtc(path, FixedTime);
            var folder = window.AddSession(); folder.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right }); Wait(folder.ComparePathsAsync());
            var binary = window.AddSession(); binary.ApplyProject(new() { Mode = "Binary", LeftPath = Path.Combine(left, "base.bin"), RightPath = Path.Combine(left, "support.bin"), BasePath = ads });
            Wait(binary.ComparePathsAsync());
            var panel = binary.GetVisualDescendants().OfType<SpecializedViews.BinaryPanel>().SingleOrDefault();
            var accepted = panel is { HasMiddle: true } && panel.Capture(1).CopyBytes().SequenceEqual(new byte[] { 0x03, 0x14, 0xFF });
            Report("central ADS actual Binary input accepted", accepted);
            var before = Snapshot(location); var adsBefore = Convert.ToHexString(File.ReadAllBytes(ads)); var attempted = false;
            if (accepted)
            {
                Activate(folder); Select(folder, ["base.bin"]); attempted = true;
                Dialogs.ConfirmationShown = dialog => Answer(dialog, true);
                Click(folder, "選択をすべてコピー →"); if (folder.PendingFolderCopy is { } pending) Wait(pending); else CloseMessages();
            }
            var after = Snapshot(location); var adsAfter = Convert.ToHexString(File.ReadAllBytes(ads));
            var rejected = accepted && attempted && before.SequenceEqual(after) && adsBefore == adsAfter && folder.LastFolderCopyResult?.Succeeded != true;
            Report("central ADS container overwrite protected", rejected);
            reviewRows.Add((id, location, before, after, rejected, accepted, attempted, ads, adsBefore, adsAfter));
        }

        void ReviewFilterChange()
        {
            const string id = "active-filter-late";
            var location = Path.Combine(root, "review-" + id); var left = Path.Combine(location, "left"); var right = Path.Combine(location, "right");
            Directory.CreateDirectory(left); Directory.CreateDirectory(right);
            File.WriteAllBytes(Path.Combine(left, "a.bin"), [0x01]); File.WriteAllBytes(Path.Combine(right, "a.bin"), [0x02]);
            var filter = Path.Combine(right, "rules.flt"); File.WriteAllText(filter, "def: include\n## original\n", new System.Text.UTF8Encoding(false));
            foreach (var path in Directory.EnumerateFileSystemEntries(location, "*", SearchOption.AllDirectories))
                if (Directory.Exists(path)) Directory.SetLastWriteTimeUtc(path, FixedTime); else File.SetLastWriteTimeUtc(path, FixedTime);
            var pane = window.AddSession(); pane.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, FileFilterPath = filter });
            Wait(pane.ComparePathsAsync()); Activate(pane); Select(pane, ["a.bin"]);
            var before = Snapshot(location); var confirmed = false;
            Dialogs.ConfirmationShown = dialog =>
            {
                confirmed = true; File.WriteAllText(filter, "def: include\n## modified\n", new System.Text.UTF8Encoding(false)); File.SetLastWriteTimeUtc(filter, FixedTime); Answer(dialog, true);
            };
            Click(pane, "選択をすべてコピー →"); if (pane.PendingFolderCopy is { } pending) Wait(pending); else CloseMessages();
            var after = Snapshot(location);
            var rejected = confirmed && before.Where(e => e.Path != "right/rules.flt").SequenceEqual(after.Where(e => e.Path != "right/rules.flt")) && pane.LastFolderCopyResult?.Succeeded != true;
            Report("active filter content changed confirmation refuses copy", rejected);
            reviewRows.Add((id, location, before, after, rejected, false, true, null, null, null));
        }

        void ReviewExtendedDirectory()
        {
            const string id = "extended-directory-container";
            var location = Path.Combine(root, "review-" + id); var left = Path.Combine(location, "left"); var right = Path.Combine(location, "right");
            Directory.CreateDirectory(left); Directory.CreateDirectory(right);
            File.WriteAllBytes(Path.Combine(left, "base.bin"), [0x01, 0x02]); File.WriteAllBytes(Path.Combine(right, "base.bin"), [0x7A, 0x62]);
            foreach (var path in Directory.EnumerateFileSystemEntries(location, "*", SearchOption.AllDirectories))
                if (Directory.Exists(path)) Directory.SetLastWriteTimeUtc(path, FixedTime); else File.SetLastWriteTimeUtc(path, FixedTime);
            var operation = window.AddSession(); operation.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right }); Wait(operation.ComparePathsAsync());
            var other = window.AddSession(); extendedRequested = @"\\?\" + Path.GetFullPath(right);
            other.ApplyProject(new() { Mode = "Folder", LeftPath = extendedRequested, RightPath = left }); Wait(other.ComparePathsAsync()); Activate(other);
            extendedInputText = other.LeftPath.Text;
            var list = other.GetVisualDescendants().OfType<ListBox>().SingleOrDefault(l => l.ItemsSource is IEnumerable<DirectoryEntry>);
            var entry = list?.Items.OfType<DirectoryEntry>().SingleOrDefault(e => e.RelativePath == "base.bin");
            extendedObserved = entry?.LeftState is { } observed ? Path.GetDirectoryName(observed.Path) : null;
            extendedAccepted = list is { IsEffectivelyVisible: true } && list.Bounds.Height > 0 && entry is { Status: DirectoryDifferenceKind.Modified }
                && entry.LeftState is { Kind: DirectoryEntryKind.File, Size: 2, Error: null } && entry.RightState is { Kind: DirectoryEntryKind.File, Size: 2, Error: null };
            extendedNormalized = extendedObserved is not null && !extendedObserved.StartsWith(@"\\?\", StringComparison.Ordinal);
            Report("extended Folder actual root accepted", extendedAccepted);
            var before = Snapshot(location); var attempted = false;
            if (extendedAccepted)
            {
                Activate(operation); Select(operation, ["base.bin"]); attempted = true; Dialogs.ConfirmationShown = dialog => Answer(dialog, true);
                Click(operation, "選択をすべてコピー →"); if (operation.PendingFolderCopy is { } pending) Wait(pending); else CloseMessages();
            }
            var after = Snapshot(location);
            var rejected = extendedAccepted && attempted && before.SequenceEqual(after) && operation.LastFolderCopyResult?.Succeeded != true;
            Report("extended Folder container descendant output protected", rejected);
            reviewRows.Add((id, location, before, after, rejected, false, attempted, null, null, null));
        }

        void Case(string name, bool toRight, bool all, string[] selected, string[] copied,
            bool confirm = true, Action<ComparisonPane, string, string>? beforeConfirm = null, string? outputFault = null,
            bool rejectRefresh = false, bool readonlySource = false, FolderCopyLimits? limits = null, string? protection = null, bool legacyButton = false, bool recursive = true)
        {
            var pane = Create(name, out var left, out var right, readonlySource, limits, recursive);
            var destination = toRight ? right : left; var source = toRight ? left : right;
            if (protection is not null)
            {
                var other = window.AddSession();
                if (protection == "folder") { other.ApplyProject(new() { Mode = "Folder", LeftPath = destination, RightPath = source }); Wait(other.ComparePathsAsync()); }
                else if (protection == "filter") other.ApplyProject(new() { Mode = "Text", LeftPath = Path.Combine(source, "same.bin"), RightPath = Path.Combine(source, "a.bin"), FileFilterPath = Path.Combine(destination, "a.bin") });
                else if (protection == "accepted")
                {
                    other.ApplyProject(new() { Mode = "Binary", LeftPath = Path.Combine(destination,"a.bin"), RightPath = Path.Combine(source,"b.bin") });
                    Wait(other.ComparePathsAsync()); other.LeftPath.Text = Path.Combine(source,"same.bin");
                }
                else if (protection == "archive")
                {
                    var archive = Path.Combine(destination,"a.bin");
                    using (var file = File.Create(archive)) using (var zip = new System.IO.Compression.ZipArchive(file,System.IO.Compression.ZipArchiveMode.Create))
                        using (var leaf = zip.CreateEntry("leaf.bin").Open()) leaf.WriteByte(0x88);
                    var input = new ArchiveProjectInput { RootPath = archive,RootSha256=Hash(archive),LeafEntry="leaf.bin",EntryChain=[],InheritedReadOnly=true };
                    other.ApplyProject(new() {Mode="Binary",LeftArchiveInput=input,LeftReadOnly=true,RightPath=Path.Combine(source,"b.bin")}); Wait(other.ComparePathsAsync());
                }
                else if (protection == "workspace") Wait(window.SaveWorkspaceAsync(Path.Combine(destination, "a.bin")));
                else other.ApplyProject(new() { Mode = "Binary", LeftPath = Path.Combine(destination, "a.bin"), RightPath = Path.Combine(source, "b.bin") });
                Activate(pane);
            }
            var before = Snapshot(source); var destinationBefore = Snapshot(destination);
            var checksCancellation = name is "cancel-button" or "cancel-preflight" or "partial-output-cancel" or "cancel-refresh";
            StreamMetadata[] cancellationBefore = checksCancellation ? CaptureStreamMetadata(Path.GetDirectoryName(source)!) : [];
            FolderCopyPlan? plan = null; var guardCalls = 0;
            pane.FolderPlanReady = value => plan = value;
            var a = Path.Combine(destination, "a.bin"); var b = Path.Combine(destination, "b.bin");
            pane.FolderOutputChecking = path =>
            {
                guardCalls++;
                if (outputFault == "cancel-preflight" && path == a) { Click(pane, "中止"); return; }
                if (outputFault is not null && path == b && File.ReadAllBytes(a).SequenceEqual([toRight ? (byte)0x11 : (byte)0x22]))
                {
                    if (outputFault == "cancel") Click(pane, "中止");
                    else throw new IOException("検証用の第二項目出力拒否");
                }
            };
            if (rejectRefresh) pane.DirectoryReadyForAdoption = () => pane.LeftPath.Text = left + "-not-adopted";
            if (name == "cancel-refresh") pane.DirectoryReadyForAdoption = () => Click(pane, "中止");
            Dialogs.ConfirmationShown = dialog =>
            {
                confirmations++; beforeConfirm?.Invoke(pane, left, right);
                Answer(dialog, confirm);
            };
            Select(pane, selected);
            if (name == "normal-L-all")
            {
                Pointer(pane, "a.bin", RawInputModifiers.None); Pointer(pane, "b.bin", RawInputModifiers.Control);
                Report("Ctrl multi selection", List(pane).SelectedItems!.Count == 2);
                Pointer(pane, "same.bin", RawInputModifiers.Shift);
                Report("Shift range selection", List(pane).SelectedItems!.Count > 2 && List(pane).SelectedItems!.OfType<DirectoryEntry>().Any(e => e.RelativePath == "same.bin"));
                Select(pane, selected);
            }
            if (legacyButton) { pane.CopyRightButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
            else Click(pane, toRight ? all ? "選択をすべてコピー →" : "選択の差分をコピー →" : all ? "← 選択をすべてコピー" : "← 選択の差分をコピー");
            if (pane.PendingFolderCopy is { } pending) Wait(pending);
            else if (legacyButton)
            {
                var clock=Stopwatch.StartNew(); while(pane.LastFolderCopyResult is null) { Dispatcher.UIThread.RunJobs();CloseMessages();if(clock.Elapsed>TimeSpan.FromSeconds(30))throw new TimeoutException("旧single copyの実buttonが完了しません。");Thread.Sleep(2); }
                Dispatcher.UIThread.RunJobs();
            }
            else CloseMessages();
            var result = pane.LastFolderCopyResult;
            var cancellationStatus = pane.ComparisonStatus;
            var cancellationPng = "folder-" + name + ".png";
            if (checksCancellation)
            {
                Report(name + " cancellation completion status", name == "cancel-button"
                    ? cancellationStatus == "フォルダーコピーを中止しました。"
                    : cancellationStatus?.StartsWith("コピー結果: 公開 " + (name == "cancel-preflight" ? "0" : "1") + " 件", StringComparison.Ordinal) == true);
                Report(name + " buttons restored", pane.GetVisualDescendants().OfType<Button>()
                    .Single(b => Equals(b.Content, "選択をすべてコピー →")).IsEnabled && List(pane).IsEnabled);
                screenshot(cancellationPng);
            }
            foreach (var path in copied)
                Report(name + " copied " + path, File.ReadAllBytes(Path.Combine(destination, path)).SequenceEqual(File.ReadAllBytes(Path.Combine(source, path))));
            if (copied.Length == 0) Report(name + " destination retained", Snapshot(destination).SequenceEqual(destinationBefore));
            if (beforeConfirm is null) Report(name + " source retained", Snapshot(source).SequenceEqual(before));
            if (outputFault is not null && outputFault != "cancel-preflight")
                Report(name + " individual partial results", result is { Succeeded: false, MutationOccurred: true }
                    && result.Entries[0].Published && !result.Entries[1].Published && (result.Entries[1].Status is FolderCopyEntryStatus.Failed or FolderCopyEntryStatus.Cancelled)
                    && result.Entries[2].Status == FolderCopyEntryStatus.NotExecuted);
            if (name == "cancel-preflight") Report(name + " no mutation result", result is { Cancelled: true, MutationOccurred: false, PublishedCount: 0 });
            if (name == "same-mtime-source-change" || name == "unknown-member") Report(name + " preflight zero mutation", result is { Succeeded: false, MutationOccurred: false, PublishedCount: 0 });
            if(name=="unknown-member")Report(name+" physical captured membership",plan?.Entries.Any(e=>e.RelativePath=="tree"&&e.Kind==DirectoryEntryKind.Directory&&e.Children.SequenceEqual(["leaf.bin"]))==true);
            if(name=="scanned-model-unknown-member")Report(name+" captured model leaf boundary",plan is {Entries.Count:1}&&plan.Entries[0].RelativePath=="tree/leaf.bin"&&!File.Exists(Path.Combine(destination,"tree","late.bin")));
            if (copied.Length > 0 && outputFault is null) Report(name + " completed result", result?.Succeeded == true);
            if (plan is not null) Report(name + " limits frozen", !ReferenceEquals(plan.Limits, limits));
            if (outputFault is not null)
            {
                pane.FolderOutputChecking=null; Wait(pane.ComparePathsAsync());
                var entries = List(pane).Items.OfType<DirectoryEntry>().ToArray();
                Report(name+" actual disk recompare",entries.Single(e=>e.RelativePath=="a.bin").Status==(name == "cancel-preflight" ? DirectoryDifferenceKind.Modified : DirectoryDifferenceKind.Equal)
                    && entries.Single(e=>e.RelativePath=="b.bin").Status==DirectoryDifferenceKind.Modified);
            }
            var observation = new Observation(name, source, destination, copied, toRight, all, confirm, guardCalls,
                result?.Succeeded, result?.MutationOccurred, result?.PublishedCount, result?.Entries.Select(e => (e.RelativePath, e.Status.ToString(), e.Published)).ToArray() ?? [],
                before, Snapshot(source), destinationBefore, Snapshot(destination));
            if (name is not ("cancel-preflight" or "cancel-refresh")) observations.Add(observation);
            if (checksCancellation) cancellationRows.Add((observation, cancellationStatus, cancellationPng, cancellationBefore, CaptureStreamMetadata(Path.GetDirectoryName(source)!)));
            pane.FolderPlanReady = null; pane.FolderOutputChecking = null; pane.DirectoryReadyForAdoption = null; Dialogs.ConfirmationShown = null;
        }

        void StaleConfirmation(string mode, bool accept)
        {
            var name="old-confirmation-new-"+mode.ToLowerInvariant()+"-"+(accept?"continue":"cancel");
            var pane=Create(name,out var left,out var right); var before=Snapshot(left); var beforeDestination=Snapshot(right);
            Select(pane,["a.bin"]); Window? confirmation=null;
            Dialogs.ConfirmationShown=dialog=>confirmation=dialog;
            Click(pane,"選択をすべてコピー →");
            var oldTask=pane.PendingFolderCopy ?? Task.CompletedTask;
            var clock=Stopwatch.StartNew();
            while(confirmation is null && !oldTask.IsCompleted) {Dispatcher.UIThread.RunJobs();if(clock.Elapsed>TimeSpan.FromSeconds(20))throw new TimeoutException("Folder confirmationが開きません。");Thread.Sleep(2);}
            Report(name+" actual dialog held",confirmation is not null);
            // 確認を保持してから中止し、新比較の採用後も旧完了が新しい表示を上書きしない。
            if (mode == "Folder" && accept && confirmation is not null) Click(pane, "中止");
            var next=Path.Combine(root,name,"new-inputs");Directory.CreateDirectory(next);
            string nextLeft,nextRight;
            if(mode=="Folder") {nextLeft=Path.Combine(next,"left");nextRight=Path.Combine(next,"right");Directory.CreateDirectory(nextLeft);Directory.CreateDirectory(nextRight);File.WriteAllBytes(Path.Combine(nextLeft,"x.bin"),[1]);File.WriteAllBytes(Path.Combine(nextRight,"x.bin"),[2]);}
            else {nextLeft=Path.Combine(next,"left."+(mode=="Image"?"png":"bin"));nextRight=Path.Combine(next,"right."+(mode=="Image"?"png":"bin"));
                if(mode=="Image") {using(var resource=typeof(HeadlessFolderCopyChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.Images.disposal-2.png")!)using(var file=File.Create(nextLeft))resource.CopyTo(file);File.Copy(nextLeft,nextRight);}
                else {File.WriteAllBytes(nextLeft,[1]);File.WriteAllBytes(nextRight,[2]);}}
            pane.ApplyProject(new(){Mode=mode,LeftPath=nextLeft,RightPath=nextRight});Wait(pane.ComparePathsAsync());
            var status=pane.ComparisonStatus; var adoption=pane.CaptureAdoptionState();
            if(confirmation is not null) Answer(confirmation,accept); Wait(oldTask);
            Report(name+" adopted state and status retained",pane.ComparisonStatus==status && Equals(adoption,pane.CaptureAdoptionState()));
            Report(name+" original output retained",Snapshot(right).SequenceEqual(beforeDestination)&&Snapshot(left).SequenceEqual(before));
            observations.Add(new(name,left,right,[],true,true,accept,0,null,null,null,[],before,Snapshot(left),beforeDestination,Snapshot(right)));
            Dialogs.ConfirmationShown=null;
        }

        void StalePreparation(string mode)
        {
            var name="old-prepare-new-"+mode.ToLowerInvariant();var pane=Create(name,out var left,out var right);var before=Snapshot(left);var beforeDestination=Snapshot(right);
            string? status=null;object? adopted=null;
            pane.FolderPlanReady=_=>
            {
                var next=Path.Combine(root,name,"new-inputs");Directory.CreateDirectory(next);string l,r;
                if(mode=="Folder"){l=Path.Combine(next,"left");r=Path.Combine(next,"right");Directory.CreateDirectory(l);Directory.CreateDirectory(r);File.WriteAllBytes(Path.Combine(l,"x.bin"),[1]);File.WriteAllBytes(Path.Combine(r,"x.bin"),[2]);}
                else {l=Path.Combine(next,"left."+(mode=="Image"?"png":"bin"));r=Path.Combine(next,"right."+(mode=="Image"?"png":"bin"));
                    if(mode=="Image"){using(var resource=typeof(HeadlessFolderCopyChecks).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.Images.disposal-2.png")!)using(var file=File.Create(l))resource.CopyTo(file);File.Copy(l,r);}else{File.WriteAllBytes(l,[1]);File.WriteAllBytes(r,[2]);}}
                pane.ApplyProject(new(){Mode=mode,LeftPath=l,RightPath=r});Wait(pane.ComparePathsAsync());status=pane.ComparisonStatus;adopted=pane.CaptureAdoptionState();
            };
            Select(pane,["a.bin"]);Click(pane,"選択をすべてコピー →");if(pane.PendingFolderCopy is {} pending)Wait(pending);
            Report(name+" completed prepare rejected after new adoption",status is not null&&status==pane.ComparisonStatus&&Equals(adopted,pane.CaptureAdoptionState()));
            Report(name+" original output retained",Snapshot(right).SequenceEqual(beforeDestination)&&Snapshot(left).SequenceEqual(before));
            observations.Add(new(name,left,right,[],true,true,false,0,null,null,null,[],before,Snapshot(left),beforeDestination,Snapshot(right)));pane.FolderPlanReady=null;
        }

        ComparisonPane Create(string name, out string left, out string right, bool readonlySource = false, FolderCopyLimits? limits = null, bool recursive = true)
        {
            var folder = Path.Combine(root, name); left = Path.Combine(folder, "left"); right = Path.Combine(folder, "right");
            foreach (var side in new[] { left, right })
            {
                Directory.CreateDirectory(Path.Combine(side, "tree")); Directory.CreateDirectory(Path.Combine(side, "empty"));
                File.WriteAllBytes(Path.Combine(side, "a.bin"), [side == left ? (byte)0x11 : (byte)0x22]);
                File.WriteAllBytes(Path.Combine(side, "b.bin"), [side == left ? (byte)0x33 : (byte)0x44]);
                File.WriteAllBytes(Path.Combine(side, "same.bin"), [0]); File.WriteAllBytes(Path.Combine(side, "tree", "leaf.bin"), [side == left ? (byte)0x55 : (byte)0x66]);
            }
            if (readonlySource) File.SetAttributes(Path.Combine(left, "a.bin"), File.GetAttributes(Path.Combine(left, "a.bin")) | FileAttributes.ReadOnly);
            foreach (var side in new[] { left, right }) foreach (var path in Directory.EnumerateFileSystemEntries(side, "*", SearchOption.AllDirectories).Prepend(side))
                if (Directory.Exists(path)) Directory.SetLastWriteTimeUtc(path, FixedTime); else File.SetLastWriteTimeUtc(path, FixedTime);
            var pane = window.AddSession(); pane.FolderCopyVerificationLimits = limits;
            pane.ApplyProject(new() { Mode = "Folder", LeftPath = left, RightPath = right, Recursive=recursive }); Wait(pane.ComparePathsAsync()); return pane;
        }
        void Wait(Task task)
        {
            var clock = Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs(); CloseMessages();
                if (clock.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("Folder copy実GUI操作が完了しませんでした。");
                Thread.Sleep(2);
            }
            task.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs(); CloseMessages();
        }
        void CloseMessages()
        {
            foreach (var dialog in window.OwnedWindows.Where(d => d.Title is "操作を完了できませんでした" or "コピーを完了できませんでした").ToArray())
                dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "閉じる")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        void Activate(ComparisonPane pane) { window.SelectSession(Array.IndexOf(window.SessionPanes.ToArray(), pane)); Dispatcher.UIThread.RunJobs(); }
        void Settings(ComparisonPane pane, bool readOnly)
        {
            Click(pane, "比較の設定…"); var dialog = window.OwnedWindows.Single(d => d.Title == "比較の設定");
            dialog.GetVisualDescendants().OfType<CheckBox>().Single(b => Equals(b.Content, "右を読取り専用にする")).IsChecked = readOnly;
            dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "適用")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        }
        void Pointer(ComparisonPane pane, string path, RawInputModifiers modifiers)
        {
            var list = List(pane); var entry = list.Items.OfType<DirectoryEntry>().Single(e => e.RelativePath == path);
            list.ScrollIntoView(entry); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var container = list.ContainerFromItem(entry)!; var point = container.TranslatePoint(new Point(12, container.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left, modifiers); window.MouseUp(point, MouseButton.Left, modifiers); Dispatcher.UIThread.RunJobs();
        }
        void Layout(ComparisonPane pane, string name)
        {
            var list = List(pane); Report(name + " actual list viewport", list.Bounds.Height >= 100);
            var first = list.Items.OfType<DirectoryEntry>().First(); list.ScrollIntoView(first); Dispatcher.UIThread.RunJobs();
            var item = list.ContainerFromItem(first)!; var itemPosition = item.TranslatePoint(default, window)!.Value;
            var listPosition = list.TranslatePoint(default, window)!.Value;
            Report(name + " actual first row visible", itemPosition.Y >= listPosition.Y && itemPosition.Y + item.Bounds.Height <= listPosition.Y + list.Bounds.Height && itemPosition.Y >= 0 && itemPosition.Y + item.Bounds.Height <= window.Bounds.Height);
            using var file=File.Create(Path.Combine(root,name+"-bounds.json"));using var json=new Utf8JsonWriter(file,new JsonWriterOptions{Indented=true});
            json.WriteStartObject();json.WriteNumber("width",window.Bounds.Width);json.WriteNumber("height",window.Bounds.Height);json.WriteNumber("tabs",window.SessionPanes.Count);
            json.WriteNumber("listY",listPosition.Y);json.WriteNumber("listHeight",list.Bounds.Height);json.WriteNumber("firstRowY",itemPosition.Y);json.WriteNumber("firstRowHeight",item.Bounds.Height);json.WriteString("firstPath",first.RelativePath);json.WriteEndObject();
            foreach (var title in new[] { "← 選択をすべてコピー", "選択をすべてコピー →", "← 選択の差分をコピー", "選択の差分をコピー →" })
            {
                var button = pane.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, title));
                var point = button.TranslatePoint(default, window)!.Value;
                Report(name + " reachable " + title, button.IsEffectivelyVisible && button.Bounds.Width > 0 && point.X>=0 && point.X+button.Bounds.Width<=window.Bounds.Width && point.Y >= 0 && point.Y + button.Bounds.Height <= window.Bounds.Height);
            }
        }
        void Report(string name, bool passed) { try { check("Folder Copy " + name, passed, "actual controls/buttons, fixed bytes and independent artifacts"); } catch (InvalidOperationException) { } }
    }
    private static ListBox List(ComparisonPane pane) => pane.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IEnumerable<DirectoryEntry>);
    private static void Select(ComparisonPane pane, string[] paths) { var list = List(pane); list.SelectedItems!.Clear(); foreach (var path in paths) list.SelectedItems.Add(list.Items.OfType<DirectoryEntry>().Single(e => e.RelativePath == path)); Dispatcher.UIThread.RunJobs(); }
    private static void Click(ComparisonPane pane, string title) { pane.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, title)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    private static void Answer(Window dialog, bool confirm) => dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, confirm ? "続行" : "キャンセル")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private sealed record Entry(string Path, bool Directory, long Size, string? Sha256, long Mtime, int Attributes, int? Mode);
    private static string Hash(string path) {using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
    private static Entry[] Snapshot(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path =>
    {
        var directory = Directory.Exists(path); using var file = directory ? null : File.OpenRead(path);
        return new Entry(Path.GetRelativePath(root, path).Replace('\\', '/'), directory, directory ? 0 : new FileInfo(path).Length,
            file is null ? null : Convert.ToHexString(SHA256.HashData(file)), File.GetLastWriteTimeUtc(path).ToFileTimeUtc(),
            (int)File.GetAttributes(path), OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(path));
    }).ToArray();
    // サイズはfileの主本文だけ。directoryのネイティブsizeやADS sizeとは比較しない。
    private sealed record StreamMetadata(string Path, bool Directory, long Size, long Mtime, int Attributes, int? Mode = null);
    private sealed record StreamBudgetObservation(string Id, string Work, bool Confirmed, bool ExpectedSuccess,
        FolderCopyLimits Limits, FolderCopyPlan? Plan, FolderCopyResult? Result, StreamMetadata[] Before, StreamMetadata[] After);
    private static void WriteStreamBudgetObservationsFile(string path, IEnumerable<StreamBudgetObservation> rows)
    {
        using var file = File.Create(path);
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        json.WriteStartArray();
        foreach (var row in rows)
        {
            json.WriteStartObject(); json.WriteString("Id", row.Id); json.WriteString("Work", row.Work);
            json.WriteBoolean("Clicked", true); json.WriteBoolean("Confirmed", row.Confirmed); json.WriteBoolean("ExpectedSuccess", row.ExpectedSuccess);
            json.WriteNumber("MaximumStreamDescriptors", row.Limits.MaximumStreamDescriptors);
            json.WriteNumber("MaximumStreamNameCharacters", row.Limits.MaximumStreamNameCharacters);
            json.WriteBoolean("PlanPresent", row.Plan is not null); json.WriteBoolean("ResultPresent", row.Result is not null);
            Number("Descriptors", row.Plan?.StreamDescriptors); Number("NameCharacters", row.Plan?.StreamNameCharacters);
            Number("Logical", row.Plan?.LogicalBytes); Number("Destination", row.Plan?.DestinationBytes); Number("Io", row.Plan?.PlannedIoBytes);
            Number("Read", row.Result?.ReadBytes); Number("Write", row.Result?.WriteBytes);
            json.WriteBoolean("Succeeded", row.Result?.Succeeded == true); json.WriteBoolean("Mutation", row.Result?.MutationOccurred ?? false);
            json.WriteNumber("Published", row.Result?.PublishedCount ?? 0);
            json.WriteStartArray("Paths"); foreach (var entry in row.Result?.Entries ?? []) json.WriteStringValue(entry.RelativePath); json.WriteEndArray();
            json.WritePropertyName("BeforeMetadata"); WriteStreamMetadata(json, row.Before);
            json.WritePropertyName("AfterMetadata"); WriteStreamMetadata(json, row.After);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        void Number(string name, long? value) { if (value is { } number) json.WriteNumber(name, number); else json.WriteNull(name); }
    }
    private sealed record StreamObservation(string Id, string Work, bool Clicked, bool Confirmed, bool ExpectedSuccess,
        bool? Succeeded, bool Mutation, int Published, long? Logical, long? Read, long? Write, string[] Paths,
        long SourceMtime, long DestinationMtime, StreamMetadata[] BeforeMetadata, StreamMetadata[] AfterMetadata);
    private static void WriteStreamMetadataFile(string path, StreamMetadata[] rows)
    {
        using var file = File.Create(path);
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        WriteStreamMetadata(json, rows);
    }
    private static void WriteStreamMetadata(Utf8JsonWriter json, StreamMetadata[] rows)
    {
        json.WriteStartArray();
        foreach (var row in rows)
        {
            json.WriteStartObject(); json.WriteString("Path", row.Path); json.WriteBoolean("Directory", row.Directory);
            json.WriteNumber("Size", row.Size); json.WriteNumber("Mtime", row.Mtime); json.WriteNumber("Attributes", row.Attributes);
            if (row.Mode is { } mode) json.WriteNumber("Mode", mode);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }
    private static void WriteStreamObservationsFile(string path, IEnumerable<StreamObservation> rows)
    {
        using var file = File.Create(path);
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        json.WriteStartArray();
        foreach (var row in rows)
        {
            json.WriteStartObject(); json.WriteString("Id", row.Id); json.WriteString("Work", row.Work);
            json.WriteBoolean("Clicked", row.Clicked); json.WriteBoolean("Confirmed", row.Confirmed);
            json.WriteBoolean("ExpectedSuccess", row.ExpectedSuccess);
            if (row.Succeeded is { } succeeded) json.WriteBoolean("Succeeded", succeeded); else json.WriteNull("Succeeded");
            json.WriteBoolean("Mutation", row.Mutation); json.WriteNumber("Published", row.Published);
            Number("Logical", row.Logical); Number("Read", row.Read); Number("Write", row.Write);
            json.WriteStartArray("Paths"); foreach (var value in row.Paths) json.WriteStringValue(value); json.WriteEndArray();
            json.WriteNumber("SourceMtime", row.SourceMtime); json.WriteNumber("DestinationMtime", row.DestinationMtime);
            json.WritePropertyName("BeforeMetadata"); WriteStreamMetadata(json, row.BeforeMetadata);
            json.WritePropertyName("AfterMetadata"); WriteStreamMetadata(json, row.AfterMetadata);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        void Number(string name, long? value) { if (value is { } number) json.WriteNumber(name, number); else json.WriteNull(name); }
    }
    private static StreamMetadata[] CaptureStreamMetadata(string location) => new[] { "left", "right" }
        .SelectMany(side => Directory.EnumerateFileSystemEntries(Path.Combine(location, side), "*", SearchOption.AllDirectories).Prepend(Path.Combine(location, side)))
        .Order(StringComparer.Ordinal).Select(path =>
        {
            var directory = Directory.Exists(path);
            return new StreamMetadata(Path.GetRelativePath(location, path).Replace('\\', '/'), directory,
                directory ? 0 : new FileInfo(path).Length, File.GetLastWriteTimeUtc(path).ToFileTimeUtc(), (int)File.GetAttributes(path),
                OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(path));
        }).ToArray();
    private sealed record Observation(string Name, string Source, string Destination, string[] Copied, bool ToRight, bool All, bool Confirm,
        int GuardCalls, bool? Succeeded, bool? Mutation, int? Published, (string Path, string Status, bool Published)[] Results,
        Entry[] BeforeSource, Entry[] AfterSource, Entry[] BeforeDestination, Entry[] AfterDestination);
    private static void Write(Utf8JsonWriter json, Observation value)
    {
        json.WriteStartObject(); json.WriteString("name", value.Name); json.WriteString("source", value.Source); json.WriteString("destination", value.Destination);
        json.WriteBoolean("toRight", value.ToRight); json.WriteBoolean("all", value.All); json.WriteBoolean("confirm", value.Confirm); json.WriteNumber("guardCalls", value.GuardCalls);
        if (value.Succeeded is { } succeeded) json.WriteBoolean("succeeded", succeeded); if (value.Mutation is { } mutation) json.WriteBoolean("mutation", mutation); if (value.Published is { } published) json.WriteNumber("published", published);
        json.WriteStartArray("copied"); foreach (var path in value.Copied) json.WriteStringValue(path); json.WriteEndArray();
        json.WriteStartArray("results"); foreach (var row in value.Results) { json.WriteStartObject(); json.WriteString("path", row.Path); json.WriteString("status", row.Status); json.WriteBoolean("published", row.Published); json.WriteEndObject(); } json.WriteEndArray();
        foreach (var (name, rows) in new[] { ("beforeSource",value.BeforeSource), ("afterSource",value.AfterSource), ("beforeDestination",value.BeforeDestination), ("afterDestination",value.AfterDestination) })
        {
            json.WriteStartArray(name); foreach (var row in rows) { json.WriteStartObject(); json.WriteString("path",row.Path); json.WriteBoolean("directory",row.Directory); json.WriteNumber("size",row.Size); json.WriteString("sha256",row.Sha256); json.WriteNumber("mtime",row.Mtime); json.WriteNumber("attributes",row.Attributes); if(row.Mode is {} mode) json.WriteNumber("mode",mode); json.WriteEndObject(); } json.WriteEndArray();
        }
        json.WriteEndObject();
    }
    private static void WriteEntries(Utf8JsonWriter json, string name, Entry[] rows)
    {
        json.WriteStartArray(name);
        foreach (var row in rows)
        {
            json.WriteStartObject(); json.WriteString("path", row.Path); json.WriteBoolean("directory", row.Directory); json.WriteNumber("size", row.Size);
            json.WriteString("sha256", row.Sha256); json.WriteNumber("mtime", row.Mtime); json.WriteNumber("attributes", row.Attributes); if (row.Mode is { } mode) json.WriteNumber("mode", mode); json.WriteEndObject();
        }
        json.WriteEndArray();
    }
}
