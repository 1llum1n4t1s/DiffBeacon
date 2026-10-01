using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
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
            pane.LeftEditor.Text = "alpha old omega old end\n"; pane.RightEditor.Text = "alpha new omega new end\n";
            pane.CompareEditors(); Screenshot("word-diff-inline.png");
            var highlightedWords = pane.DiffList.GetVisualDescendants().OfType<TextBlock>()
                .SelectMany(block => block.Inlines?.OfType<Avalonia.Controls.Documents.Run>() ?? [])
                .Where(run => Equals(run.Foreground, Avalonia.Media.Brushes.Gold)).Select(run => run.Text).ToArray();
            Check("word diff renders separate changed words and retains equal middle", highlightedWords.Count(word => word == "old") == 2
                && highlightedWords.Count(word => word == "new") == 2 && highlightedWords.Length == 4,
                string.Join("|", highlightedWords));
            var inlineTextBlocks = pane.DiffList.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any() == true).ToArray();
            Check("word diff keeps full text visible with inherited foreground", inlineTextBlocks.Length == 2
                && inlineTextBlocks.All(block => block.Foreground is not null)
                && inlineTextBlocks.Select(block => string.Concat(block.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text)))
                    .Order().SequenceEqual(new[] { "alpha new omega new end", "alpha old omega old end" }.Order()));
            var wordReport = Path.Combine(output, "word-diff-inline.html"); Pump(pane.SaveReportAsync(wordReport));
            var wordHtml = File.ReadAllText(wordReport);
            Check("word diff GUI report shares all four inline spans", System.Text.RegularExpressions.Regex.Matches(wordHtml, "<span class=\"inline-diff\">old</span>").Count == 2
                && System.Text.RegularExpressions.Regex.Matches(wordHtml, "<span class=\"inline-diff\">new</span>").Count == 2);
            Check("word diff preview and report retain original source files", File.ReadAllText(left) == "title\r\nleft value\r\ntail\r\n"
                && File.ReadAllText(right) == "title\r\nright value\r\ntail\r\n");
            pane.DiscardChanges(); Pump(pane.ComparePathsAsync()); pane.NavigateDifference(1);
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
            var workspaceLeft = Path.Combine(output, "workspace-left.txt"); var workspaceRight = Path.Combine(output, "workspace-right.txt");
            File.WriteAllText(workspaceLeft, "same\nleft\n", new UTF8Encoding(false)); File.WriteAllText(workspaceRight, "same\nright\n", new UTF8Encoding(false));
            var tableLeft = Path.Combine(output, "workspace-left.csv"); var tableRight = Path.Combine(output, "workspace-right.csv");
            File.WriteAllText(tableLeft, "id;value\n1;'semi;colon'\n"); File.WriteAllText(tableRight, "id;value\n1;'changed'\n");
            var foldersLeft = Path.Combine(output, "workspace-left-dir"); var foldersRight = Path.Combine(output, "workspace-right-dir");
            foreach (var folder in new[] { foldersLeft, foldersRight })
            {
                Directory.CreateDirectory(Path.Combine(folder, "nested"));
                File.WriteAllText(Path.Combine(folder, "a.txt"), "same"); File.WriteAllText(Path.Combine(folder, "skip.bin"), "different");
                File.WriteAllText(Path.Combine(folder, "ignore.txt"), "excluded"); File.WriteAllText(Path.Combine(folder, "nested", "deeper.txt"), "nested");
            }
            var projectPath = Path.Combine(output, "workspace.diffbeacon.json");
            var project = new ComparisonWorkspace { Entries = [
                new() { LeftPath = workspaceLeft, RightPath = workspaceRight, Mode = "Text", LeftDescription = "原本（編集禁止）", RightDescription = "変更後", LeftReadOnly = true, LegacySettings = new() { ["prediffer"] = "unregistered-tool.exe" } },
                new() { LeftPath = tableLeft, RightPath = tableRight, Mode = "Table", TableDelimiter = ';', TableQuote = '\'', TableAllowNewlinesInQuotes = false },
                new() { LeftPath = foldersLeft, RightPath = foldersRight, Mode = "Folder", Recursive = false, FolderMode = "Hash", ExcludedPaths = "ignore.txt", LegacyFilter = "*.txt", RightReadOnly = true }
            ], ActiveEntryIndex = 1 };
            Pump(WorkspaceStore.SaveWorkspaceAsync(projectPath, project)); Pump(window.OpenWorkspaceAsync(projectPath, discardChanges: true));
            Check("workspace opens all comparisons in order and restores active tab", window.SessionPanes.Count == 3 && window.ActivePane == window.SessionPanes[1] && window.SessionPanes.Select(x => x.LeftPath.Text).SequenceEqual(project.Entries.Select(x => x.LeftPath)));
            window.Width = 960; window.Height = 700; Dispatcher.UIThread.RunJobs();
            var compactTable = window.ActivePane.GetVisualDescendants().OfType<TablePanel>().Single();
            compactTable.SelectCell(0, 1, 1); Screenshot("workspace-table-compact.png");
            var compactList = compactTable.GetVisualDescendants().OfType<ListBox>().Single();
            var compactCell = compactTable.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Text == "semi;colon");
            var compactPosition = compactCell?.TranslatePoint(new Point(0, 0), compactList);
            Check("compact table retains rendered cells below scrollable toolbar", compactList.Bounds.Height > 0 && compactPosition is { } position
                && position.Y >= 0 && compactCell!.Bounds.Height > 0 && position.Y + compactCell.Bounds.Height <= compactList.Bounds.Height,
                $"tableHeight={compactTable.Bounds.Height};listHeight={compactList.Bounds.Height};cellY={compactPosition?.Y};cellHeight={compactCell?.Bounds.Height};windowHeight={window.Bounds.Height};windowWidth={window.Bounds.Width}");
            window.Width = 1280; window.Height = 850; Dispatcher.UIThread.RunJobs();
            Check("workspace table applies custom separator and quote", window.ActivePane.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "semi;colon"));
            Screenshot("workspace-table.png");
            window.SelectSession(2); Dispatcher.UIThread.RunJobs();
            var folderPane = window.SessionPanes[2];
            var projectDirectoryRows = folderPane.GetVisualDescendants().OfType<ListBox>().SelectMany(list => list.Items.OfType<DiffBeacon.Core.DirectoryEntry>()).ToArray();
            Check("workspace folder applies mask exclusion and nonrecursive comparison", projectDirectoryRows.Any(row => row.RelativePath == "a.txt") && !projectDirectoryRows.Any(row => row.RelativePath is "skip.bin" or "ignore.txt" || row.RelativePath.Contains("deeper", StringComparison.Ordinal)));
            Screenshot("workspace-folder.png");
            var textPane = window.SessionPanes[0]; window.SelectSession(0); Dispatcher.UIThread.RunJobs();
            Check("workspace retains unsupported plugin without registering or running it", textPane.CaptureProject().LegacySettings["prediffer"] == "unregistered-tool.exe" && textPane.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text?.Contains("未適用の旧設定", StringComparison.Ordinal) == true));
            Check("workspace readonly is applied to actual text editor", textPane.LeftEditor.IsReadOnly && !textPane.RightEditor.IsReadOnly);
            textPane.NavigateDifference(1); var beforeReadOnly = textPane.LeftEditor.Text;
            rejected = false; try { textPane.CopySelected(false); } catch (InvalidOperationException) { rejected = true; }
            Check("workspace readonly refuses diff copy into protected side", rejected && textPane.LeftEditor.Text == beforeReadOnly);
            rejected = false; try { Pump(textPane.SaveAsync(false)); } catch (InvalidOperationException) { rejected = true; }
            Check("workspace readonly refuses original save", rejected && File.ReadAllText(workspaceLeft) == "same\nleft\n");
            textPane.ResultEditor.Text = "replacement";
            rejected = false; try { Pump(textPane.SaveMergeResultToAsync(workspaceLeft)); } catch (InvalidOperationException) { rejected = true; }
            Check("workspace readonly refuses merge output targeting protected input", rejected && File.ReadAllText(workspaceLeft) == "same\nleft\n");
            rejected = false; try { Pump(window.SaveWorkspaceAsync(workspaceLeft)); } catch (InvalidOperationException) { rejected = true; }
            Check("workspace save cannot overwrite protected comparison input", rejected && File.ReadAllText(workspaceLeft) == "same\nleft\n");
            textPane.DiscardChanges(); Screenshot("workspace-readonly.png");
            var invalidProject = Path.Combine(output, "workspace-invalid.json"); File.WriteAllText(invalidProject, "{\"entries\":[null],\"formatVersion\":1}");
            var currentPanes = window.SessionPanes.ToArray(); textPane.RightEditor.Text += "pending edit";
            rejected = false; try { Pump(window.OpenWorkspaceAsync(invalidProject)); } catch (InvalidDataException) { rejected = true; }
            Check("invalid workspace retains existing tabs and unsaved text", rejected && window.SessionPanes.SequenceEqual(currentPanes) && textPane.RightEditor.Text!.EndsWith("pending edit", StringComparison.Ordinal));
            var opening = window.OpenWorkspaceAsync(projectPath);
            var dialogWait = Stopwatch.StartNew();
            while (!window.OwnedWindows.Any(dialog => dialog.Title == "未保存の変更") && !opening.IsCompleted)
            { Dispatcher.UIThread.RunJobs(); if (dialogWait.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("プロジェクト確認が表示されません。"); Thread.Sleep(5); }
            var projectDialog = window.OwnedWindows.Single(dialog => dialog.Title == "未保存の変更");
            projectDialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "キャンセル")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(opening);
            Check("canceling workspace replacement preserves all tabs and pending edits", !opening.Result && window.SessionPanes.SequenceEqual(currentPanes) && textPane.HasUnsavedChanges);
            textPane.DiscardChanges(); window.SelectSession(2);
            var savedWorkspace = Path.Combine(output, "workspace-saved.json"); Pump(window.SaveWorkspaceAsync(savedWorkspace));
            Pump(window.OpenWorkspaceAsync(savedWorkspace, discardChanges: true));
            Check("workspace GUI save reload preserves all tab settings and selection", window.SessionPanes.Count == 3 && window.ActivePane == window.SessionPanes[2] && window.SessionPanes[0].CaptureProject().LeftReadOnly && window.SessionPanes[1].CaptureProject().TableQuote == '\'' && !window.SessionPanes[2].CaptureProject().Recursive && window.SessionPanes[2].CaptureProject().ExcludedPaths == "ignore.txt");
            window.SelectSession(0); Dispatcher.UIThread.RunJobs(); textPane = window.ActivePane;
            textPane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "比較の設定…")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            var settingsDialog = window.OwnedWindows.Single(dialog => dialog.Title == "比較の設定");
            settingsDialog.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, "右を読取り専用にする")).IsChecked = true;
            settingsDialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "適用")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Check("project options dialog applies readonly to live editor and captured settings", textPane.RightEditor.IsReadOnly && textPane.CaptureProject().RightReadOnly);
            var projectBinaryLeft = Path.Combine(output, "workspace-left.bin"); var projectBinaryRight = Path.Combine(output, "workspace-right.bin");
            File.WriteAllBytes(projectBinaryLeft, [1, 2, 3]); File.WriteAllBytes(projectBinaryRight, [1, 9, 3]);
            var readOnlyAncestor = Path.Combine(output, "workspace-readonly-ancestor.7z"); File.WriteAllText(readOnlyAncestor, "keep ancestor");
            textPane.ApplyProject(new() { LeftPath = projectBinaryLeft, RightPath = projectBinaryRight, BasePath = readOnlyAncestor, BaseReadOnly = true, Mode = "Binary", LeftReadOnly = true }); textPane.DiscardChanges(); Pump(textPane.CompareProjectAsync());
            var binaryEditors = textPane.GetVisualDescendants().OfType<TextBox>().Where(box => box.AcceptsReturn).ToArray();
            Check("project readonly reaches binary editor", binaryEditors.Any(editor => editor.IsReadOnly && (editor.Text ?? "").Contains("01 02 03", StringComparison.Ordinal)));
            var binaryRanges = textPane.GetVisualDescendants().OfType<ListBox>().Single(list => list.Items.Count > 0 && list.Items[0]?.ToString()?.StartsWith("0x", StringComparison.Ordinal) == true);
            binaryRanges.SelectedIndex = 0;
            textPane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "← 選択範囲")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            var binaryPanel = textPane.GetVisualDescendants().OfType<SpecializedViews.BinaryPanel>().Single();
            var binarySaved = Path.Combine(output, "workspace-binary-copy.bin"); Pump(binaryPanel.SaveToAsync(false, binarySaved));
            Check("project readonly refuses binary merge into protected side", File.ReadAllBytes(binarySaved).SequenceEqual(new byte[] { 1, 2, 3 }) && !textPane.HasUnsavedChanges);
            rejected = false; try { Pump(binaryPanel.SaveToAsync(true, readOnlyAncestor)); } catch (InvalidOperationException) { rejected = true; }
            Check("binary save protects readonly ancestor through actual save path", rejected && File.ReadAllText(readOnlyAncestor) == "keep ancestor");
            rejected = false; try { Pump(binaryPanel.SaveToAsync(true, projectBinaryLeft)); } catch (InvalidOperationException) { rejected = true; }
            Check("binary save protects readonly opposite input", rejected && File.ReadAllBytes(projectBinaryLeft).SequenceEqual(new byte[] { 1, 2, 3 }));
            textPane.ApplyProject(new() { LeftPath = sevenPath, RightPath = zipPath, BasePath = readOnlyAncestor, BaseReadOnly = true, Mode = "Archive" }); textPane.DiscardChanges(); Pump(textPane.CompareProjectAsync()); Dispatcher.UIThread.RunJobs();
            var projectArchivePanel = textPane.GetVisualDescendants().OfType<ArchivePanel>().Single();
            rejected = false; try { Pump(projectArchivePanel.ExportToAsync(false, "folder/value.txt", readOnlyAncestor)); } catch (InvalidOperationException) { rejected = true; }
            Check("archive export protects readonly ancestor", rejected && File.ReadAllText(readOnlyAncestor) == "keep ancestor");
            rejected = false; try { Pump(projectArchivePanel.RepackToAsync(false, readOnlyAncestor)); } catch (InvalidOperationException) { rejected = true; }
            Check("archive repack protects readonly ancestor", rejected && File.ReadAllText(readOnlyAncestor) == "keep ancestor");
            textPane.ApplyProject(new() { LeftPath = sevenPath, RightPath = zipPath, BasePath = foldersLeft, BaseReadOnly = true, Mode = "Archive" }); textPane.DiscardChanges(); Pump(textPane.CompareProjectAsync()); Dispatcher.UIThread.RunJobs();
            projectArchivePanel = textPane.GetVisualDescendants().OfType<ArchivePanel>().Single();
            var forbiddenExtract = Path.Combine(foldersLeft, "protected-extract");
            rejected = false; try { Pump(projectArchivePanel.ExtractToAsync(false, forbiddenExtract)); } catch (InvalidOperationException) { rejected = true; }
            Check("archive extraction cannot write inside readonly project directory", rejected && !Directory.Exists(forbiddenExtract));
            textPane.ApplyProject(new() { LeftPath = "https://example.invalid/left", RightPath = "https://example.invalid/right", Mode = "Web", ProviderId = "external-not-registered" }); textPane.DiscardChanges(); Pump(textPane.CompareProjectAsync()); Dispatcher.UIThread.RunJobs();
            Check("project unknown provider is preserved and requires explicit registration", textPane.CaptureProject().ProviderId == "external-not-registered" && textPane.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text?.Contains("未登録", StringComparison.Ordinal) == true));
            var multilineLeft = Path.Combine(output, "workspace-multiline-left.csv"); var multilineRight = Path.Combine(output, "workspace-multiline-right.csv");
            File.WriteAllText(multilineLeft, "id;value\n1;'two\nlines'\n"); File.WriteAllText(multilineRight, "id;value\n1;'two\nlines'\n");
            textPane.ApplyProject(new() { LeftPath = multilineLeft, RightPath = multilineRight, Mode = "Table", TableDelimiter = ';', TableQuote = '\'', TableAllowNewlinesInQuotes = false }); textPane.DiscardChanges(); Pump(textPane.CompareProjectAsync()); Dispatcher.UIThread.RunJobs();
            Check("project table forbids quoted newlines when disabled", textPane.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text?.Contains("引用符内の改行は許可されていません", StringComparison.Ordinal) == true));
            textPane.ApplyProject(textPane.CaptureProject() with { TableAllowNewlinesInQuotes = true }); Pump(textPane.CompareProjectAsync()); Dispatcher.UIThread.RunJobs();
            Check("project table accepts quoted newlines when enabled", textPane.GetVisualDescendants().OfType<TextBlock>().Count(block => block.Text == "two\nlines") == 2);
            var multiXml = Path.Combine(output, "workspace-legacy.WinMerge");
            File.WriteAllText(multiXml, "<project><paths><left>workspace-left.txt</left><right>workspace-right.txt</right><left-desc>旧プロジェクト</left-desc><left-readonly>1</left-readonly></paths><paths><left>workspace-left.csv</left><right>workspace-right.csv</right><window-type>2</window-type><table-delimiter>;</table-delimiter><table-quote>'</table-quote><table-allownewlinesinquotes>0</table-allownewlinesinquotes></paths></project>");
            Pump(window.OpenWorkspaceAsync(multiXml, discardChanges: true));
            Check("legacy multi-project opens separate text and table tabs", window.SessionPanes.Count == 2 && window.SessionPanes[0].LeftEditor.IsReadOnly && window.SessionPanes[1].CaptureProject().Mode == "Table");
            window.SelectSession(1); Screenshot("workspace-legacy.png");
            var packageDialog = window.CreatePackagingDialog(); packageDialog.Show(window); Dispatcher.UIThread.RunJobs();
            var packageBoxes = packageDialog.GetVisualDescendants().OfType<CheckBox>().ToArray();
            Check("packaging dialog selects active comparison and default documents/project", packageBoxes.Single(box => box.Name == "package-entry-1").IsChecked == true && packageBoxes.Single(box => box.Name == "package-entry-0").IsChecked == false
                && packageBoxes.Single(box => box.Name == "package-documents").IsChecked == true && packageBoxes.Single(box => box.Name == "package-project").IsChecked == true);
            packageBoxes.Single(box => box.Name == "package-entry-0").IsChecked = true;
            packageBoxes.Single(box => box.Name == "package-report").IsChecked = true;
            packageBoxes.Single(box => box.Name == "package-patch").IsChecked = true;
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using (var frame = packageDialog.CaptureRenderedFrame()!) frame.Save(Path.Combine(artifactOutput, "package-dialog.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            var packageOutput = Path.Combine(output, "comparisons.zip"); Pump(window.PackageFromDialogAsync(packageDialog, packageOutput)); packageDialog.Close();
            var packageArchive = new DiffBeacon.Providers.ManagedArchive(); var packageManifest = packageArchive.ReadManifest(packageOutput);
            Check("packaging actual dialog settings includes report/patch/project and four source files", packageManifest.Entries.Count(entry => !entry.IsDirectory) == 9
                && packageManifest.Entries.Any(entry => entry.Path == "report.files/2.html") && packageManifest.Entries.Any(entry => entry.Path == "patch.diff"));
            var unpacked = Path.Combine(output, "package-unpacked"); packageArchive.ExtractAll(packageOutput, unpacked);
            var unpackedProject = Path.Combine(unpacked, "project.json"); Pump(window.OpenWorkspaceAsync(unpackedProject, discardChanges: true));
            Check("unpacked package reopens all actual tabs relative to extraction and active selection", window.SessionPanes.Count == 2 && window.ActivePane == window.SessionPanes[1]
                && window.SessionPanes[0].LeftPath.Text!.StartsWith(unpacked, StringComparison.Ordinal) && window.SessionPanes[0].LeftEditor.IsReadOnly && window.SessionPanes[1].CaptureProject().TableQuote == '\'');
            Screenshot("package-reopened.png");
            var packageStartup = new MainWindow([unpackedProject]);
            try
            {
                packageStartup.Show(); var startupWait = Stopwatch.StartNew();
                while (true)
                {
                    Dispatcher.UIThread.RunJobs();
                    if (packageStartup.SessionPanes.Count == 2)
                    {
                        try { packageStartup.SessionPanes[1].EnsureComparedForPackaging(); break; }
                        catch (InvalidOperationException) { }
                    }
                    if (startupWait.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("同梱プロジェクトの起動時読込みが完了しません。"); Thread.Sleep(5);
                }
                Check("GUI startup project argument opens packaged tabs and active selection", packageStartup.ActivePane == packageStartup.SessionPanes[1]
                    && packageStartup.SessionPanes[0].LeftPath.Text!.StartsWith(unpacked, StringComparison.Ordinal));
            }
            finally { foreach (var startupPane in packageStartup.SessionPanes) startupPane.DiscardChanges(); packageStartup.Close(); }
            var packageBefore = File.ReadAllBytes(packageOutput); window.SelectSession(0); window.ActivePane.RightEditor.Text += "unsaved package edit";
            rejected = false; try { Pump(window.PackageWorkspaceAsync(packageOutput, [0])); } catch (InvalidOperationException) { rejected = true; }
            Check("packaging unsaved rejection preserves original archive and edited content", rejected && File.ReadAllBytes(packageOutput).SequenceEqual(packageBefore) && window.ActivePane.HasUnsavedChanges);
            window.ActivePane.DiscardChanges();
            rejected = false; try { Pump(window.PackageWorkspaceAsync(window.ActivePane.LeftPath.Text!, [0])); } catch (InvalidOperationException) { rejected = true; }
            Check("packaging cannot overwrite readonly comparison document", rejected);
            var oldRightPath = window.ActivePane.RightPath.Text; window.ActivePane.RightPath.Text = right;
            rejected = false; try { Pump(window.PackageWorkspaceAsync(packageOutput, [0])); } catch (InvalidOperationException) { rejected = true; }
            Check("packaging rejects paths changed after comparison and preserves archive", rejected && File.ReadAllBytes(packageOutput).SequenceEqual(packageBefore));
            window.ActivePane.RightPath.Text = oldRightPath;
            var freshPackagePane = window.AddSession(); freshPackagePane.LeftPath.Text = left; freshPackagePane.RightPath.Text = right;
            rejected = false; try { Pump(window.PackageWorkspaceAsync(packageOutput, [2])); } catch (InvalidOperationException) { rejected = true; }
            Check("packaging rejects un-compared saved paths", rejected);
            window.SelectSession(0);
            Pump(window.CopyPackageToClipboardAsync(packageOutput));
            var copiedPackages = window.Clipboard!.TryGetFilesAsync().GetAwaiter().GetResult();
            Check("packaging uses actual file clipboard format", copiedPackages is { Length: 1 } && copiedPackages[0].TryGetLocalPath() == packageOutput);
            var packageLargeLeft = Path.Combine(output, "package-large-left.bin"); var packageLargeRight = Path.Combine(output, "package-large-right.bin");
            using (var file = File.Create(packageLargeLeft)) file.SetLength(16 * 1024 * 1024);
            using (var file = File.Create(packageLargeRight)) file.SetLength(16 * 1024 * 1024);
            window.ActivePane.ApplyProject(new() { LeftPath = packageLargeLeft, RightPath = packageLargeRight, Mode = "Binary" }); window.ActivePane.DiscardChanges();
            Pump(window.ActivePane.CompareProjectAsync());
            var packagingTask = window.PackageWorkspaceAsync(packageOutput, [0]); var packageWait = Stopwatch.StartNew(); var packageInProgress = false;
            while (!packagingTask.IsCompleted && packageWait.Elapsed < TimeSpan.FromSeconds(10))
            {
                Dispatcher.UIThread.RunJobs();
                packageInProgress = Directory.EnumerateDirectories(output, ".diffbeacon-package-*").Any(dir => Directory.EnumerateFiles(dir).Any());
                if (packageInProgress) { window.CancelPackaging(); break; }
                Thread.Sleep(1);
            }
            rejected = false; try { Pump(packagingTask); } catch (OperationCanceledException) { rejected = true; }
            Check("canceling packaging after snapshot starts preserves old output and removes owned stage", packageInProgress && rejected && File.ReadAllBytes(packageOutput).SequenceEqual(packageBefore)
                && !Directory.EnumerateDirectories(output, ".diffbeacon-package-*").Any());
            window.ActivePane.DiscardChanges();
            var reportPane = window.ActivePane; var reportPath = Path.Combine(output, "pane-report.html");
            File.WriteAllText(reportPath, "old report");
            rejected = false; try { Pump(reportPane.SaveReportAsync(reportPath)); } catch (InvalidOperationException) { rejected = true; }
            Check("binary pane rejects empty text report and preserves output", rejected && File.ReadAllText(reportPath) == "old report");
            var reportBase = Path.Combine(output, "report-base.txt"); File.WriteAllText(reportBase, "title\nancestor only\ntail\n");
            reportPane.ApplyProject(new() { LeftPath = left, BasePath = reportBase, RightPath = right, Mode = "Text",
                LeftDescription = "左<script>", BaseDescription = "共通祖先", RightDescription = "右" });
            Pump(reportPane.CompareProjectAsync()); reportPane.RightEditor.Text += "edited report content\n";
            Pump(reportPane.SaveReportAsync(reportPath)); var reportText = File.ReadAllText(reportPath);
            var reportVisibleText = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(reportText, "<[^>]*>", ""));
            Check("GUI report uses all three panes and live unsaved edits with safe descriptions", reportText.Contains("data-side=\"base\"")
                && reportVisibleText.Contains("ancestor only") && reportVisibleText.Contains("edited report content") && reportText.Contains("左&lt;script&gt;")
                && !reportText.Contains("<script>") && !File.ReadAllText(right).Contains("edited report content"));
            var reportViews = reportPane.GetVisualDescendants().OfType<TabControl>().First();
            reportViews.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); Screenshot("report-three-pane.png");
            reportViews.SelectedIndex = 0;
            var oldReport = File.ReadAllBytes(reportPath); reportPane.RightPath.Text = left;
            rejected = false; try { Pump(reportPane.SaveReportAsync(reportPath)); } catch (InvalidOperationException) { rejected = true; }
            Check("GUI report rejects stale paths and preserves output", rejected && File.ReadAllBytes(reportPath).SequenceEqual(oldReport));
            reportPane.RightPath.Text = right;
            var sourceBeforeReport = File.ReadAllBytes(left);
            rejected = false; try { Pump(reportPane.SaveReportAsync(left)); } catch (InvalidOperationException) { rejected = true; }
            Check("GUI report protects writable input too", rejected && File.ReadAllBytes(left).SequenceEqual(sourceBeforeReport));
            using (var canceledReport = new CancellationTokenSource())
            {
                canceledReport.Cancel(); rejected = false;
                try { Pump(reportPane.SaveReportAsync(reportPath, canceledReport.Token)); } catch (OperationCanceledException) { rejected = true; }
                Check("GUI canceled report keeps old output", rejected && File.ReadAllBytes(reportPath).SequenceEqual(oldReport)
                    && !Directory.EnumerateFiles(output, ".pane-report.html.*.tmp").Any());
            }
            var smallEditorText = reportPane.RightEditor.Text; reportPane.RightEditor.Text = new string('<', 8 * 1024 * 1024);
            var cancelFromUi = reportPane.SaveReportAsync(reportPath); var reportWasActive = !cancelFromUi.IsCompleted;
            reportPane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            rejected = false; try { Pump(cancelFromUi); } catch (OperationCanceledException) { rejected = true; }
            Check("actual stop button cancels active HTML report and preserves old output", reportWasActive && rejected
                && File.ReadAllBytes(reportPath).SequenceEqual(oldReport) && !Directory.EnumerateFiles(output, ".pane-report.html.*.tmp").Any());
            reportPane.RightEditor.Text = smallEditorText;
            reportPane.DiscardChanges();
            reportPane.ApplyProject(new() { LeftPath = multilineLeft, BasePath = multilineLeft, RightPath = multilineRight, Mode = "Table",
                TableDelimiter = ';', TableQuote = '\'', TableAllowNewlinesInQuotes = true });
            Pump(reportPane.CompareProjectAsync()); reportPane.RightEditor.Text = "id;value\n1;'new\nvalue'\n";
            Pump(reportPane.SaveReportAsync(reportPath)); reportText = File.ReadAllText(reportPath);
            Check("GUI table report uses parsed multiline cells and ancestor instead of raw text diff", reportText.Contains("data-mode=\"Table\"")
                && reportText.Contains("data-side=\"base\"") && reportText.Contains("data-row=\"2\"") && reportText.Contains("data-column=\"2\"")
                && reportText.Contains("two\nlines") && !reportText.Contains("'two"));
            reportPane.DiscardChanges();
            var reportJsonLeft = Path.Combine(output, "report-left.json"); var reportJsonRight = Path.Combine(output, "report-right.json");
            File.WriteAllText(reportJsonLeft, "{\"a\":1,\"b\":2}"); File.WriteAllText(reportJsonRight, "{\"b\":2.0,\"a\":1.0}");
            reportPane.ApplyProject(new() { LeftPath = reportJsonLeft, RightPath = reportJsonRight, Mode = "Json" });
            Pump(reportPane.CompareProjectAsync()); Pump(reportPane.SaveReportAsync(reportPath)); reportText = File.ReadAllText(reportPath);
            Check("GUI JSON report follows structural normalization", reportText.Contains("data-mode=\"Json\"") && reportText.Contains("data-different=\"false\""));

            var alignedLeft = Path.Combine(output, "table-edit-left.csv"); var alignedRight = Path.Combine(output, "table-edit-right.csv");
            var alignedBase = Path.Combine(output, "table-edit-base.csv");
            const string rawTable = "id;value;keep\r\n1;'two\r\nlines';'raw''quote'\r\n2;tail;\r\n";
            var tableEncoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
            File.WriteAllText(alignedLeft, rawTable, tableEncoding); File.WriteAllText(alignedBase, rawTable, tableEncoding);
            File.WriteAllText(alignedRight, rawTable.Replace("1;", "0;insert;\r\n1;", StringComparison.Ordinal), tableEncoding);
            var originalRightBytes = File.ReadAllBytes(alignedRight); var originalBaseBytes = File.ReadAllBytes(alignedBase);
            var originalTableAttributes = File.GetAttributes(alignedLeft);
            window.Height = 1100;
            reportPane.DiscardChanges(); reportPane.ApplyProject(new() { LeftPath = alignedLeft, BasePath = alignedBase, RightPath = alignedRight,
                Mode = "Table", TableDelimiter = ';', TableQuote = '\'', TableAllowNewlinesInQuotes = true, RightReadOnly = true });
            Pump(reportPane.CompareProjectAsync()); Dispatcher.UIThread.RunJobs();
            var tablePanel = reportPane.GetVisualDescendants().OfType<TablePanel>().Single();
            Check("table GUI three panes align insertion and retain original logical rows", tablePanel.Comparison.Documents.Count == 3
                && tablePanel.Comparison.Rows.Count == 4 && tablePanel.Comparison.Rows[1] is { LeftRow: null, BaseRow: null, RightRow: 2 }
                && tablePanel.Comparison.Rows[2] is { LeftRow: 2, BaseRow: 2, RightRow: 3 });
            Check("table GUI keeps quote multiline in one logical cell", tablePanel.Comparison.GetCell(0, 2, 1)?.Value == "two\r\nlines"
                && tablePanel.Comparison.Documents[0].Rows.Count == 3);
            Screenshot("table-three-aligned.png");
            Check("table GUI ghost cannot become editable empty cell", !tablePanel.SelectCell(0, 1, 1) && tablePanel.CellEditor.IsReadOnly);
            rejected = false; try { Pump(tablePanel.CommitCellAsync()); } catch (InvalidOperationException) { rejected = true; }
            Check("table ghost edit is refused without changing source", rejected && reportPane.LeftEditor.Text == rawTable);
            tablePanel.SelectCell(1, 2, 1); rejected = false;
            try { Pump(tablePanel.CommitCellAsync()); } catch (InvalidOperationException) { rejected = true; }
            Check("table ancestor stays readonly regardless project base flag", rejected && tablePanel.CellEditor.IsReadOnly && File.ReadAllBytes(alignedBase).SequenceEqual(originalBaseBytes));
            tablePanel.SelectCell(2, 2, 1); rejected = false;
            try { Pump(tablePanel.CommitCellAsync()); } catch (InvalidOperationException) { rejected = true; }
            Check("table readonly reaches decoded editor and commit", rejected && tablePanel.CellEditor.IsReadOnly && File.ReadAllBytes(alignedRight).SequenceEqual(originalRightBytes));
            tablePanel.SelectCell(0, 0, 0); tablePanel.SearchText.Text = "raw'quote";
            Pump(tablePanel.FindAsync(1));
            Check("table search finds decoded escaped quote and selects its text", tablePanel.CellEditor.Text == "raw'quote"
                && tablePanel.CellEditor.SelectionEnd - tablePanel.CellEditor.SelectionStart == "raw'quote".Length);
            Pump(tablePanel.FindAsync(1));
            Check("table next search includes readonly ancestor", tablePanel.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex == 1 && tablePanel.CellEditor.IsReadOnly);
            Pump(tablePanel.FindAsync(-1));
            Check("table previous search returns to writable left cell", tablePanel.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex == 0 && !tablePanel.CellEditor.IsReadOnly);
            tablePanel.SelectCell(2, 3, 2); Pump(tablePanel.FindAsync(1));
            Check("table wrap search preserves display side column order", tablePanel.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex == 0 && tablePanel.CellEditor.Text == "raw'quote");
            tablePanel.SelectCell(0, 0, 0); tablePanel.SearchText.Text = "RAW'QUOTE";
            tablePanel.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, "大小文字を区別")).IsChecked = true;
            Pump(tablePanel.FindAsync(1));
            Check("table case-sensitive search leaves selection on no match", tablePanel.CellEditor.Text == "id");
            tablePanel.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, "大小文字を区別")).IsChecked = false;
            tablePanel.SearchText.Text = "raw.*quote";
            tablePanel.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, "正規表現")).IsChecked = true;
            tablePanel.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, "単語単位")).IsChecked = true;
            Pump(tablePanel.FindAsync(1));
            Check("table successful regex whole-word search selects decoded match", tablePanel.CellEditor.Text == "raw'quote" && tablePanel.CellEditor.SelectionStart == 0);
            tablePanel.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, "正規表現")).IsChecked = false;
            tablePanel.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, "単語単位")).IsChecked = false;
            tablePanel.SelectCell(0, 2, 1);
            tablePanel.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "table-source-column").Text = "3";
            rejected = false; try { Pump(tablePanel.CommitCellAsync()); } catch (InvalidOperationException) { rejected = true; }
            Check("table changed coordinate fields cannot edit previous cell", rejected && reportPane.LeftEditor.Text == rawTable);
            tablePanel.SelectCell(0, 2, 1);
            const string editedCell = "new;value 'quoted'\r\nnext";
            tablePanel.CellEditor.Focus(); tablePanel.CellEditor.SelectAll(); window.KeyTextInput(editedCell); Dispatcher.UIThread.RunJobs();
            Check("table decoded cell accepts actual headless text input", tablePanel.CellEditor.Text == editedCell);
            tablePanel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "セルを変更")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(tablePanel.PendingOperation!); Dispatcher.UIThread.RunJobs();
            var editedRaw = rawTable.Replace("'two\r\nlines'", "'new;value ''quoted''\r\nnext'", StringComparison.Ordinal);
            Check("table real commit quotes one raw interval and preserves remaining text", reportPane.LeftEditor.Text == editedRaw && reportPane.HasUnsavedChanges
                && tablePanel.Comparison.Documents[0].SourceText == editedRaw && tablePanel.Comparison.GetCell(0, 2, 1)?.Value == editedCell);
            tablePanel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "セル編集を戻す")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(tablePanel.PendingOperation!); Dispatcher.UIThread.RunJobs();
            Check("table real undo restores full raw source and shared model", reportPane.LeftEditor.Text == rawTable && tablePanel.Comparison.Documents[0].SourceText == rawTable);
            tablePanel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "セル編集をやり直す")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(tablePanel.PendingOperation!); Dispatcher.UIThread.RunJobs();
            Check("table real redo restores decoded and raw edit", reportPane.LeftEditor.Text == editedRaw && tablePanel.CellEditor.Text == editedCell);
            Pump(reportPane.SaveAsync(false));
            var expectedSavedBytes = tableEncoding.GetPreamble().Concat(tableEncoding.GetBytes(editedRaw)).ToArray();
            Check("table save preserves UTF16BE BOM CRLF untouched bytes and attributes", File.ReadAllBytes(alignedLeft).SequenceEqual(expectedSavedBytes)
                && File.GetAttributes(alignedLeft) == originalTableAttributes && File.ReadAllBytes(alignedRight).SequenceEqual(originalRightBytes)
                && File.ReadAllBytes(alignedBase).SequenceEqual(originalBaseBytes));
            Pump(reportPane.SaveReportAsync(reportPath)); reportText = File.ReadAllText(reportPath);
            Check("table GUI report uses same source alignment after cell edit", reportText.Contains("data-aligned-row=\"3\"")
                && reportText.Contains("new;value &#39;quoted&#39;\r\nnext") && reportText.Contains("raw&#39;quote"));
            Screenshot("table-edited.png");
            Pump(reportPane.CompareProjectAsync()); Dispatcher.UIThread.RunJobs();
            tablePanel = reportPane.GetVisualDescendants().OfType<TablePanel>().Single();
            Check("table save reload uses original encoding and new decoded cell", reportPane.LeftEditor.Text == editedRaw
                && tablePanel.Comparison.GetCell(0, 2, 1)?.Value == editedCell);
            tablePanel.SelectCell(0, 2, 1); reportPane.LeftEditor.Text += "9;external;\r\n";
            var externallyEdited = reportPane.LeftEditor.Text; rejected = false;
            try { Pump(tablePanel.CommitCellAsync()); } catch (InvalidOperationException) { rejected = true; }
            Check("table stale cell interval cannot overwrite text editor changes", rejected && reportPane.LeftEditor.Text == externallyEdited);
            Pump(tablePanel.RefreshAsync());
            Check("table explicit refresh picks up external text edits", tablePanel.Comparison.Documents[0].SourceText == externallyEdited
                && tablePanel.Comparison.Documents[0].Rows.Count == 4);
            tablePanel.SelectCell(0, 0, 0); tablePanel.SearchText.Text = "[";
            tablePanel.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, "正規表現")).IsChecked = true;
            rejected = false; try { Pump(tablePanel.FindAsync(1)); } catch (ArgumentException) { rejected = true; }
            Check("table malformed regex is refused without changing original source", rejected && reportPane.LeftEditor.Text == externallyEdited);
            reportPane.DiscardChanges();
            var flatTable = Path.Combine(output, "table-flat.csv"); File.WriteAllText(flatTable, "a");
            reportPane.ApplyProject(new() { LeftPath = flatTable, RightPath = flatTable, Mode = "Table", TableAllowNewlinesInQuotes = false });
            Pump(reportPane.CompareProjectAsync()); Dispatcher.UIThread.RunJobs();
            tablePanel = reportPane.GetVisualDescendants().OfType<TablePanel>().Single(); tablePanel.SelectCell(0, 0, 0);
            tablePanel.CellEditor.Text = "forbidden\nnewline"; rejected = false;
            try { Pump(tablePanel.CommitCellAsync()); } catch (FormatException) { rejected = true; }
            Check("table edit refuses newline disabled by syntax without changing raw text", rejected && reportPane.LeftEditor.Text == "a" && File.ReadAllText(flatTable) == "a");
            tablePanel.CellEditor.Text = ""; Pump(tablePanel.CommitCellAsync());
            Check("table empty edit keeps final single logical row instead of deleting it", reportPane.LeftEditor.Text == "\"\""
                && tablePanel.Comparison.Documents[0].Rows.Count == 1 && tablePanel.Comparison.GetCell(0, 0, 0)?.Value == "");
            reportPane.DiscardChanges();
            var sparsePath = Path.Combine(output, "table-sparse.csv");
            File.WriteAllText(sparsePath, string.Join(',', Enumerable.Repeat("x", 10_000)) + "\n" + string.Concat(Enumerable.Repeat("x\n", 10_000)));
            reportPane.ApplyProject(new() { LeftPath = sparsePath, RightPath = sparsePath, Mode = "Table" });
            Pump(reportPane.CompareProjectAsync()); Dispatcher.UIThread.RunJobs();
            tablePanel = reportPane.GetVisualDescendants().OfType<TablePanel>().Single();
            Check("table sparse wide rows render bounded column page", tablePanel.Comparison.ColumnCount == 10_000
                && tablePanel.Comparison.Rows.Count == 10_001 && tablePanel.GetVisualDescendants().OfType<Border>().Count() < 2_000);
            tablePanel.SelectCell(0, 0, 9_999); Dispatcher.UIThread.RunJobs();
            Check("table column paging keeps final source column accessible", tablePanel.CellEditor.Text == "x"
                && tablePanel.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "table-source-column").Text == "10000"
                && tablePanel.GetVisualDescendants().OfType<Border>().Count() < 2_000);
            tablePanel.SearchText.Text = "absent-sparse-value";
            var sparseTimer = Stopwatch.StartNew(); Pump(tablePanel.FindAsync(1)); sparseTimer.Stop();
            Check("table sparse search visits actual cells and keeps source intact", tablePanel.Comparison.Documents[0].SourceText == File.ReadAllText(sparsePath),
                $"elapsedMilliseconds={sparseTimer.ElapsedMilliseconds};actualCellsPerPane=20000;rectangularCoordinates=200020000");
            Screenshot("table-sparse-paged.png");
            HeadlessTableSearchTests.Run(window, reportPane, output, Check, Screenshot, Pump);
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
        void Check(string name, bool passed, string detail = "") { assertions.Add((name, passed, detail)); if (!passed) throw new InvalidOperationException(name + (detail.Length > 0 ? ": " + detail : "")); }
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
