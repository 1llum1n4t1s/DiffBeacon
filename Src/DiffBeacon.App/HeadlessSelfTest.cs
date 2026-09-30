using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

internal static class HeadlessSelfTest
{
    // 同じ画面とイベント経路を操作し、再現入力と描画結果を成果物へ残す。
    internal static int Run(string output)
    {
        var artifactOutput = Path.GetFullPath(output); Directory.CreateDirectory(artifactOutput);
        // 前回の入力・出力を残したまま再実行し、CreateNewや新規展開先と衝突させない。
        output = Path.Combine(artifactOutput, "fixtures", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(output);
        var assertions = new List<(string Name, bool Passed, string Detail)>();
        MainWindow? window = null;
        try
        {
            AppBuilder.Configure<BeaconApplication>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
            var left = Path.Combine(output, "left.txt"); var right = Path.Combine(output, "right.txt");
            File.WriteAllText(left, "title\r\nleft value\r\ntail\r\n", new UTF8Encoding(false));
            File.WriteAllText(right, "title\r\nright value\r\ntail\r\n", new UTF8Encoding(false));
            window = new MainWindow { Width = 1280, Height = 850 };
            window.Show();
            var pane = window.ActivePane;
            pane.LeftPath.Text = left; pane.RightPath.Text = right;
            Pump(pane.ComparePathsAsync());
            Check("file comparison shows difference", pane.CurrentDiff is { Blocks.Count: 1 });
            pane.NavigateDifference(1);
            Check("next difference selects changed row", pane.DiffList.SelectedIndex == pane.CurrentDiff!.Blocks[0].RowStart);
            Screenshot("comparison.png");
            pane.CopyRightButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("merge button copies selected block", pane.LeftEditor.Text == pane.RightEditor.Text && pane.CurrentDiff is { HasDifferences: false });
            Check("merge is marked unsaved", pane.HasUnsavedChanges);
            pane.LeftEditor.Text = "new line\n" + pane.LeftEditor.Text;
            pane.CompareEditors();
            Check("editor changes are compared", pane.CurrentDiff is { HasDifferences: true });
            pane.DiscardChanges();
            Screenshot("merged.png");
            var ancestor = Path.Combine(output, "base.txt");
            File.WriteAllText(ancestor, "title\r\nbase value\r\ntail\r\n", new UTF8Encoding(false));
            pane.BasePath.Text = ancestor;
            Pump(pane.ComparePathsAsync());
            pane.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "自動マージ")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("three-way merge exposes conflicting result", (pane.ResultEditor.Text ?? "").Contains("<<<<<<<", StringComparison.Ordinal));
            var views = pane.GetVisualDescendants().OfType<TabControl>().Single();
            views.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Check("four-pane editing view renders ancestor and result", pane.GetVisualDescendants().OfType<TextBox>().Count(x => x.AcceptsReturn) == 4);
            Screenshot("four-panes.png");
            pane.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "右を採用")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("choosing right resolves only the selected conflict", pane.CurrentMergeSession is { UnresolvedCount: 0 } && pane.ResultEditor.Text == pane.RightEditor.Text);
            Check("result provenance identifies chosen source", pane.CurrentMergeSession!.LineProvenance.Any(line => line.Source == "3"));
            pane.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "マージを元に戻す")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("undo restores conflict and resolution state", pane.CurrentMergeSession is { UnresolvedCount: 1 } && (pane.ResultEditor.Text ?? "").Contains("<<<<<<< LEFT", StringComparison.Ordinal));
            pane.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "マージをやり直す")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("redo restores chosen source", pane.CurrentMergeSession is { UnresolvedCount: 0 } && pane.ResultEditor.Text == pane.RightEditor.Text);
            var chosenMerge = Path.Combine(output, "selected-merge.txt");
            Pump(pane.SaveMergeResultToAsync(chosenMerge));
            Check("merge result save preserves ancestor encoding and source bytes", File.ReadAllBytes(chosenMerge).SequenceEqual(File.ReadAllBytes(right)));
            Pump(pane.SaveAsync(false));
            Check("saving merge output does not retarget the source document", pane.LeftPath.Text == left && File.ReadAllText(left).Contains("left value", StringComparison.Ordinal));
            pane.ChooseMergeSources(DiffBeacon.Core.MergeSource.Left, DiffBeacon.Core.MergeSource.Right);
            Check("ordered multiple-source selection preserves both contributions", (pane.ResultEditor.Text ?? "").Contains("left value\r\nright value\r\n", StringComparison.Ordinal));
            pane.UndoMerge();
            pane.ResultEditor.Text = (pane.ResultEditor.Text ?? "").Replace("right value", "manual value", StringComparison.Ordinal);
            Dispatcher.UIThread.RunJobs();
            Check("manual editing marks only touched result lines", pane.CurrentMergeSession!.LineProvenance.Any(line => line.Source == "m") && pane.CurrentMergeSession.LineProvenance.First().Source == "2");
            pane.UndoMerge();
            Check("undo manual edit restores source provenance", pane.ResultEditor.Text == pane.RightEditor.Text && !pane.CurrentMergeSession!.LineProvenance.Any(line => line.Source == "m"));
            pane.RedoMerge();
            Check("redo manual edit retains untouched provenance", pane.CurrentMergeSession!.LineProvenance.Any(line => line.Source == "m") && pane.CurrentMergeSession.LineProvenance.First().Source == "2");
            Screenshot("chosen-merge.png");
            views.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            pane.BasePath.Text = ""; pane.DiscardChanges();
            Pump(pane.ComparePathsAsync()); pane.StartMergeSession(false);
            var ancestorRejected = false;
            try { pane.ChooseMergeSources(DiffBeacon.Core.MergeSource.Base); } catch (ArgumentException) { ancestorRejected = true; }
            Check("two-way session rejects nonexistent ancestor without resolving", ancestorRejected && pane.CurrentMergeSession is { UnresolvedCount: 1 });
            pane.ChooseMergeSources(DiffBeacon.Core.MergeSource.Right);
            Check("two-way result can choose right", pane.ResultEditor.Text == pane.RightEditor.Text && pane.CurrentMergeSession is { UnresolvedCount: 0 });

            TwoWay("one\ntwo\nLEFT\ntail\n", "one\ntwo\nRIGHT\ntail\n");
            EditResult(pane.ResultEditor.Text!.Replace("tail", "TAIL", StringComparison.Ordinal));
            Check("editing common tail preserves preceding unresolved conflict", pane.CurrentMergeSession is { UnresolvedCount: 1 });
            var unresolvedOutput = Path.Combine(output, "unresolved-rejected.txt");
            var pendingRejected = false;
            try { Pump(pane.SaveMergeResultToAsync(unresolvedOutput)); } catch (InvalidOperationException) { pendingRejected = true; }
            Check("saving a pending edited result fails without creating output", pendingRejected && !File.Exists(unresolvedOutput));
            EditResult(pane.ResultEditor.Text!.Replace("one", "ONE", StringComparison.Ordinal));
            Check("separated common edits retain intervening conflict", pane.CurrentMergeSession is { UnresolvedCount: 1 });
            pane.ChooseMergeSources(DiffBeacon.Core.MergeSource.Right);
            Check("source choice preserves unrelated edited and untouched line origins", pane.CurrentMergeSession!.LineProvenance.Take(2).Select(line => line.Source).SequenceEqual(new[] { "m", "1" }));
            TwoWay("LEFT\n", "RIGHT\n");
            EditResult(pane.ResultEditor.Text!.Replace("LEFT\n", "MANUAL\n", StringComparison.Ordinal));
            Check("editing conflict payload retains generated pending markers", pane.CurrentMergeSession is { UnresolvedCount: 1 });
            EditResult("MANUAL\n");
            Check("removing generated conflict markers resolves manual text", pane.CurrentMergeSession is { UnresolvedCount: 0 } && pane.CurrentMergeSession.LineProvenance.All(line => line.Source == "m"));
            TwoWay("=======\n", "right\n");
            EditResult("=======\n");
            Check("literal source separator is not an unresolved generated marker", pane.CurrentMergeSession is { UnresolvedCount: 0 });
            pane.UndoMerge();
            Check("undo restores generated marker tracking", pane.CurrentMergeSession is { UnresolvedCount: 1 });
            pane.RedoMerge();
            Check("redo restores resolution and manual provenance", pane.CurrentMergeSession is { UnresolvedCount: 0 } && pane.CurrentMergeSession.LineProvenance.All(line => line.Source == "m"));
            var priorSession = pane.CurrentMergeSession; var priorResult = pane.ResultEditor.Text;
            pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "マージ開始")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var restartDialog = window.OwnedWindows.Single(dialog => dialog.Title == "未保存のマージ結果");
            restartDialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "キャンセル")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Check("canceling merge restart retains unsaved result and session", ReferenceEquals(pane.CurrentMergeSession, priorSession) && pane.ResultEditor.Text == priorResult);
            pane.DiscardChanges();
            File.WriteAllText(ancestor, "A\nB\n"); File.WriteAllText(left, "a"); File.WriteAllText(right, "A\nB\nC\n");
            pane.BasePath.Text = ancestor; Pump(pane.ComparePathsAsync()); pane.StartMergeSession();
            EditResult("a\nD\n");
            Check("editing after synthesized boundary newline preserves that newline", pane.CurrentMergeSession!.Text == "a\nD\n");
            pane.UndoMerge(); Check("undo preserves original synthesized boundary", pane.ResultEditor.Text == "a\nC\n");
            pane.RedoMerge(); Check("redo preserves edited synthesized boundary", pane.ResultEditor.Text == "a\nD\n");
            var sectionSelector = pane.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Items.Count > 0 && box.Items[0]?.ToString()?.StartsWith("差分 ", StringComparison.Ordinal) == true);
            sectionSelector.SelectedIndex = 0; pane.ChooseMergeSources(DiffBeacon.Core.MergeSource.Left);
            pane.UndoMerge(); pane.RedoMerge();
            Check("reselecting source before manual section preserves boundary newline", pane.ResultEditor.Text == "a\nD\n");
            sectionSelector.SelectedIndex = sectionSelector.ItemCount - 1; pane.ChooseMergeSources(DiffBeacon.Core.MergeSource.Base);
            pane.UndoMerge(); pane.RedoMerge();
            Check("removing final contribution restores original no-final-newline source", pane.ResultEditor.Text == "a");
            pane.UndoMerge();
            EditResult("a\n");
            Check("manual deletion of final line retains untouched boundary newline", pane.ResultEditor.Text == "a\n");
            pane.UndoMerge(); Check("undo final-line deletion restores manual contribution", pane.ResultEditor.Text == "a\nD\n");
            Screenshot("merge-regressions.png");
            pane.DiscardChanges();
            pane.SetAdvancedFilters(true, DiffBeacon.Core.CommentSyntax.CStyle, DiffBeacon.Core.WhitespaceMode.None, new DiffBeacon.Core.SubstitutionRule("build=[A-Z]", "build=*"));
            pane.LeftEditor.Text = "int value = 12; /* old */\nbuild=A\n"; pane.RightEditor.Text = "int value = 34; /* new */\nbuild=B\n";
            pane.CompareEditors();
            Check("GUI forwards number comment and substitution filters", pane.CurrentDiff is { HasDifferences: false } && pane.CurrentDiff.LeftText.Contains("12", StringComparison.Ordinal));
            pane.SetAdvancedFilters(false, DiffBeacon.Core.CommentSyntax.None, DiffBeacon.Core.WhitespaceMode.None); pane.DiscardChanges();
            var ignore = pane.GetVisualDescendants().OfType<TextBox>().Single(x => x.PlaceholderText == "除外行の正規表現");
            ignore.Text = "^#";
            pane.LeftEditor.Text = "NEW"; pane.RightEditor.Text = "old\n#keep\n";
            pane.CompareEditors(); pane.NavigateDifference(1);
            pane.CopyRightButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("unterminated merge keeps ignored line separate", pane.RightEditor.Text == "NEW\n#keep\n");
            ignore.Text = ""; pane.DiscardChanges();
            var original = File.ReadAllBytes(left);
            pane.LeftPath.Text = right;
            var rejected = false;
            try { Pump(pane.SaveAsync(false)); } catch (InvalidOperationException) { rejected = true; }
            Check("changed path cannot overwrite unrelated file", rejected && original.SequenceEqual(File.ReadAllBytes(left)));
            var imageLeft = Path.Combine(output, "image-left.png"); var imageRight = Path.Combine(output, "image-right.png");
            File.Copy(Path.Combine(artifactOutput, "comparison.png"), imageLeft, true); File.Copy(imageLeft, imageRight, true);
            pane.LeftPath.Text = imageLeft; pane.RightPath.Text = imageRight; pane.SelectMode(4);
            Pump(pane.ComparePathsAsync());
            var imageBytes = File.ReadAllBytes(imageLeft);
            rejected = false;
            try { Pump(pane.SaveAsync(false)); } catch (InvalidOperationException) { rejected = true; }
            Check("image comparison cannot be saved as text", rejected && imageBytes.SequenceEqual(File.ReadAllBytes(imageLeft)));
            Screenshot("images.png");
            pane.DiscardChanges();
            var xmlLeft = Path.Combine(output, "left.xml"); var xmlRight = Path.Combine(output, "right.xml");
            File.WriteAllText(xmlLeft, "<root a=\"1\" b=\"2\">text</root>");
            File.WriteAllText(xmlRight, "<root b=\"2\" a=\"1\">text</root>");
            pane.LeftPath.Text = xmlLeft; pane.RightPath.Text = xmlRight; pane.SelectMode(8);
            pane.GetVisualDescendants().OfType<ComboBox>().Single(x => x.Items.OfType<string>().Contains("xml")).SelectedItem = "xml";
            Pump(pane.ComparePathsAsync());
            Check("provider comparison is shown in diff view", pane.CurrentDiff is { HasDifferences: false } && pane.LeftEditor.IsReadOnly && pane.RightEditor.IsReadOnly);
            rejected = false;
            try { Pump(pane.SaveAsync(false)); } catch (InvalidOperationException) { rejected = true; }
            Check("normalized XML cannot overwrite original", rejected && File.ReadAllText(xmlLeft) == "<root a=\"1\" b=\"2\">text</root>");
            Screenshot("xml.png");
            pane.DiscardChanges();
            var binaryLeft = Path.Combine(output, "left.bin"); var binaryRight = Path.Combine(output, "right.bin");
            File.WriteAllBytes(binaryLeft, [0, 1, 2, 3]); File.WriteAllBytes(binaryRight, [0, 4, 2, 3]);
            pane.LeftPath.Text = binaryLeft; pane.RightPath.Text = binaryRight; pane.SelectMode(3);
            Pump(pane.ComparePathsAsync());
            Check("binary comparison view renders", pane.CurrentDiff is null);
            Screenshot("binary.png");
            pane.DiscardChanges();
            pane.LeftPath.Text = left; pane.RightPath.Text = right; pane.SelectMode(1);
            Pump(pane.ComparePathsAsync());
            var leftBeforeSave = File.ReadAllBytes(left);
            var attributes = File.GetAttributes(left);
            try
            {
                File.SetAttributes(left, attributes | FileAttributes.ReadOnly);
                pane.LeftEditor.Text += "edit\n";
                rejected = false;
                try { Pump(pane.SaveAsync(false)); } catch (UnauthorizedAccessException) { rejected = true; }
                Check("read-only text save preserves original bytes", rejected && leftBeforeSave.SequenceEqual(File.ReadAllBytes(left)));
            }
            finally { File.SetAttributes(left, attributes); pane.DiscardChanges(); }
            var archiveInput = Path.Combine(output, "archive-source"); Directory.CreateDirectory(Path.Combine(archiveInput, "folder"));
            var archiveValue = Path.Combine(archiveInput, "folder/value.txt"); File.WriteAllText(archiveValue, "RIGHT\n", new UTF8Encoding(false));
            var zipPath = Path.Combine(output, "left.zip"); var sevenPath = Path.Combine(output, "right.7z");
            using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                zip.CreateEntry("folder/");
                using (var writer = new StreamWriter(zip.CreateEntry("folder/value.txt").Open(), new UTF8Encoding(false))) writer.Write("LEFT\n");
                using var large = zip.CreateEntry("large.bin", System.IO.Compression.CompressionLevel.Fastest).Open();
                var chunk = Enumerable.Repeat((byte)0x61, 64 * 1024).ToArray();
                for (var index = 0; index < 272; index++) large.Write(chunk);
            }
            Pump(ArchiveActions.CreateAsync(archiveInput, sevenPath, CancellationToken.None));
            pane.BasePath.Text = ""; pane.LeftPath.Text = zipPath; pane.RightPath.Text = sevenPath; pane.SelectMode(0);
            Pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
            var archivePanel = pane.GetVisualDescendants().OfType<ArchivePanel>().Single();
            Check("automatic archive view compares ZIP and 7z by contents", archivePanel.Rows.Any(row => row.Path == "folder/value.txt" && row.Status == "Modified") && archivePanel.Rows.Any(row => row.Path == "folder" && row.Status == "Equal"));
            var archiveRow = archivePanel.Rows.Single(row => row.Path == "folder/value.txt"); Pump(archivePanel.PreviewAsync(archiveRow));
            Check("archive preview displays decoded entry bytes from both sides", archivePanel.PreviewText.Contains("4C 45 46 54", StringComparison.Ordinal) && archivePanel.PreviewText.Contains("52 49 47 48 54", StringComparison.Ordinal));
            Pump(archivePanel.PreviewAsync(archivePanel.Rows.Single(row => row.Path == "large.bin")));
            Check("archive preview captures only prefix of entry larger than preview buffer", archivePanel.PreviewText.Contains("61 61 61", StringComparison.Ordinal) && archivePanel.PreviewText.Length < 20_000);
            var largeExport = Path.Combine(output, "large-archive-export.bin"); Pump(archivePanel.ExportToAsync(false, "large.bin", largeExport));
            Check("archive export supports entries larger than preview limit", new FileInfo(largeExport).Length == 17L * 1024 * 1024 && File.ReadAllBytes(largeExport).All(value => value == 0x61));
            var exportedEntry = Path.Combine(output, "archive-export.txt"); Pump(archivePanel.ExportToAsync(true, archiveRow.Path, exportedEntry));
            Check("archive entry export preserves exact file contents", File.ReadAllBytes(exportedEntry).SequenceEqual(File.ReadAllBytes(archiveValue)));
            var archiveOriginal = File.ReadAllBytes(zipPath); rejected = false;
            try { Pump(archivePanel.ExportToAsync(true, archiveRow.Path, zipPath)); } catch (IOException) { rejected = true; }
            Check("archive export cannot overwrite either compared archive", rejected && archiveOriginal.SequenceEqual(File.ReadAllBytes(zipPath)));
            var repackedArchive = Path.Combine(output, "repacked.7z"); Pump(archivePanel.RepackToAsync(false, repackedArchive));
            var archiveService = new DiffBeacon.Providers.ManagedArchive();
            Check("GUI archive repack preserves all names types and hashes", DiffBeacon.Providers.ArchiveComparison.Compare(archiveService.ReadManifest(zipPath), archiveService.ReadManifest(repackedArchive)).All(row => row.Status == "Equal"));
            var guiZip = Path.Combine(output, "gui-repacked.zip"); Pump(archivePanel.RepackToAsync(true, guiZip));
            Check("GUI archive repack selects ZIP from output extension", archiveService.ReadManifest(guiZip).Format == "zip" && DiffBeacon.Providers.ArchiveComparison.Compare(archiveService.ReadManifest(sevenPath), archiveService.ReadManifest(guiZip)).All(row => row.Status == "Equal"));
            var guiExtracted = Path.Combine(output, "gui-extracted"); Pump(archivePanel.ExtractToAsync(true, guiExtracted));
            Check("GUI archive full extraction preserves content", Directory.Exists(Path.Combine(guiExtracted, "folder")) && File.ReadAllText(Path.Combine(guiExtracted, "folder/value.txt")) == "RIGHT\n");
            rejected = false; try { Pump(archivePanel.ExtractToAsync(true, guiExtracted)); } catch (IOException) { rejected = true; }
            Check("GUI archive extraction refuses existing destination", rejected && File.ReadAllText(Path.Combine(guiExtracted, "folder/value.txt")) == "RIGHT\n");
            var cancelledExtraction = Path.Combine(output, "gui-cancelled-extraction");
            using (var extractionCancellation = new CancellationTokenSource())
            {
                var extracting = archivePanel.ExtractToAsync(false, cancelledExtraction, extractionCancellation.Token);
                var started = false; var cancelled = false;
                Pump(Task.Run(async () =>
                {
                    for (var attempt = 0; attempt < 2000 && !extracting.IsCompleted; attempt++)
                    {
                        if (Directory.EnumerateDirectories(output, ".diffbeacon-*.extract.tmp").Any()) { started = true; break; }
                        await Task.Delay(1);
                    }
                    extractionCancellation.Cancel();
                    try { await extracting; } catch (OperationCanceledException) { cancelled = true; }
                }));
                Check("GUI archive cancellation during extraction removes staging output", started && cancelled && !Directory.Exists(cancelledExtraction) && !Directory.EnumerateDirectories(output, ".diffbeacon-*.extract.tmp").Any());
            }
            Screenshot("archives.png");
            var guiTar = Path.Combine(output, "gui-repacked.tar.gz"); Pump(archivePanel.RepackToAsync(true, guiTar));
            pane.LeftPath.Text = sevenPath; pane.RightPath.Text = guiTar; Pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
            var formatsPanel = pane.GetVisualDescendants().OfType<ArchivePanel>().Single();
            Check("GUI automatically compares 7z with compressed TAR", formatsPanel.Rows.Count > 0 && formatsPanel.Rows.All(row => row.Status == "Equal"));
            Screenshot("archives-formats.png");
            var encryptedPath = Path.Combine(output, "encrypted.zip");
            using (var resource = typeof(HeadlessSelfTest).Assembly.GetManifestResourceStream("DiffBeacon.SelfTest.Encrypted.zip") ?? throw new InvalidOperationException("暗号化検証用入力がありません。"))
            using (var encryptedFile = File.Create(encryptedPath)) resource.CopyTo(encryptedFile);
            pane.LeftPath.Text = pane.RightPath.Text = encryptedPath; Pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
            var encryptedPanel = pane.GetVisualDescendants().OfType<ArchivePanel>().Single();
            Check("encrypted archive shows error rather than false equality without password", encryptedPanel.Rows.Count == 0 && !string.IsNullOrEmpty(encryptedPanel.StatusText));
            encryptedPanel.LeftPassword.Text = encryptedPanel.RightPassword.Text = "test"; Pump(encryptedPanel.RefreshAsync());
            Check("masked GUI passwords unlock encrypted archive contents", encryptedPanel.Rows.Count > 0 && encryptedPanel.Rows.All(row => row.Status == "Equal") && encryptedPanel.LeftPassword.PasswordChar == '●');
            Screenshot("encrypted-archives.png");
            pane.DiscardChanges();
            var directoryLeft = Path.Combine(output, "directory-left"); var directoryRight = Path.Combine(output, "directory-right");
            Directory.CreateDirectory(directoryLeft); Directory.CreateDirectory(directoryRight);
            File.WriteAllText(Path.Combine(directoryLeft, "normal.txt"), "same"); File.WriteAllText(Path.Combine(directoryRight, "normal.txt"), "same");
            File.WriteAllText(Path.Combine(directoryLeft, "number.txt"), "build=123\n"); File.WriteAllText(Path.Combine(directoryRight, "number.txt"), "build=456\n");
            foreach (var checkbox in pane.GetVisualDescendants().OfType<CheckBox>().Where(box => Equals(box.Content, "大文字小文字を無視") || Equals(box.Content, "空白を無視") || Equals(box.Content, "空行を無視"))) checkbox.IsChecked = false;
            pane.SetAdvancedFilters(true, DiffBeacon.Core.CommentSyntax.None, DiffBeacon.Core.WhitespaceMode.None);
            pane.LeftPath.Text = directoryLeft; pane.RightPath.Text = directoryRight; pane.SelectMode(2);
            Pump(pane.ComparePathsAsync()); Dispatcher.UIThread.RunJobs();
            Check("number-only filter reaches folder comparison without older filters", pane.GetVisualDescendants().OfType<ListBox>().SelectMany(list => list.Items.OfType<DiffBeacon.Core.DirectoryEntry>()).Any(entry => entry.RelativePath == "number.txt" && entry.Status == DiffBeacon.Core.DirectoryDifferenceKind.Equal));
            pane.SetAdvancedFilters(false, DiffBeacon.Core.CommentSyntax.None, DiffBeacon.Core.WhitespaceMode.None);
            var largeLeft = Path.Combine(directoryLeft, "oversize.bin"); var largeRight = Path.Combine(directoryRight, "oversize.bin");
            try
            {
                using (var file = File.Create(largeLeft)) file.SetLength(257L * 1024 * 1024);
                using (var file = File.Create(largeRight)) file.SetLength(257L * 1024 * 1024);
                pane.GetVisualDescendants().OfType<CheckBox>().Single(x => Equals(x.Content, "大文字小文字を無視")).IsChecked = true;
                pane.LeftPath.Text = directoryLeft; pane.RightPath.Text = directoryRight; pane.SelectMode(2);
                Pump(pane.ComparePathsAsync());
                Dispatcher.UIThread.RunJobs();
                var directoryRows = pane.GetVisualDescendants().OfType<ListBox>().SelectMany(x => x.Items.OfType<DiffBeacon.Core.DirectoryEntry>()).ToArray();
                Check("oversized filtered directory entry is an error row", directoryRows.Any(x => x.RelativePath == "oversize.bin" && x.Status == DiffBeacon.Core.DirectoryDifferenceKind.Error));
                Check("other directory entries still compare", directoryRows.Any(x => x.RelativePath == "normal.txt" && x.Status == DiffBeacon.Core.DirectoryDifferenceKind.Equal));
            }
            finally
            {
                // この検証だけで作った上限超過入力は、サイズと作成手順をソースに残して清掃する。
                foreach (var createdPath in new[] { largeLeft, largeRight })
                    if (Path.GetFullPath(createdPath).StartsWith(output + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(createdPath)) File.Delete(createdPath);
            }
            window.AddSession();
            Check("new comparison tab is independent", window.ActivePane != pane && window.ActivePane.LeftEditor.Text == "");
            return assertions.All(x => x.Passed) ? 0 : 2;
        }
        catch (Exception ex) { assertions.Add(("unexpected failure", false, ex.ToString())); return 2; }
        finally
        {
            using var stream = File.Create(Path.Combine(artifactOutput, "ui-report.json"));
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject(); writer.WriteString("runtime", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier); writer.WriteString("fixtures", output);
            writer.WriteString("framework", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            writer.WriteStartArray("assertions");
            foreach (var assertion in assertions) { writer.WriteStartObject(); writer.WriteString("name", assertion.Name); writer.WriteBoolean("passed", assertion.Passed); writer.WriteString("detail", assertion.Detail); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteEndObject();
            window?.Close();
        }
        void Check(string name, bool passed) { assertions.Add((name, passed, "")); if (!passed) throw new InvalidOperationException(name); }
        void TwoWay(string leftText, string rightText)
        {
            var currentPane = paneForTests(); currentPane.BasePath.Text = ""; currentPane.DiscardChanges();
            // 前の祖先状態は再読込みで解除し、実際のエディター入力から開始する。
            Pump(currentPane.ComparePathsAsync());
            currentPane.LeftEditor.Text = leftText; currentPane.RightEditor.Text = rightText;
            currentPane.StartMergeSession(false); Dispatcher.UIThread.RunJobs();
        }
        ComparisonPane paneForTests() => window!.ActivePane;
        void EditResult(string text)
        {
            var currentPane = paneForTests(); currentPane.ResultEditor.Text = text; Dispatcher.UIThread.RunJobs();
            Check("result editor synchronizes exact manual text", currentPane.CurrentMergeSession!.Text == text && currentPane.ResultEditor.Text == text);
        }
        void Screenshot(string name)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window!.CaptureRenderedFrame() ?? throw new InvalidOperationException("描画フレームがありません。");
            frame.Save(Path.Combine(artifactOutput, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }

    private static void Pump(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            if (timeout.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("UI操作が完了しませんでした。");
            Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
    }
}
