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
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
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
            views.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            pane.BasePath.Text = ""; pane.DiscardChanges();
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
            File.Copy(Path.Combine(output, "comparison.png"), imageLeft, true); File.Copy(imageLeft, imageRight, true);
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
            var directoryLeft = Path.Combine(output, "directory-left"); var directoryRight = Path.Combine(output, "directory-right");
            Directory.CreateDirectory(directoryLeft); Directory.CreateDirectory(directoryRight);
            File.WriteAllText(Path.Combine(directoryLeft, "normal.txt"), "same"); File.WriteAllText(Path.Combine(directoryRight, "normal.txt"), "same");
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
            using var stream = File.Create(Path.Combine(output, "ui-report.json"));
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject(); writer.WriteString("runtime", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
            writer.WriteString("framework", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            writer.WriteStartArray("assertions");
            foreach (var assertion in assertions) { writer.WriteStartObject(); writer.WriteString("name", assertion.Name); writer.WriteBoolean("passed", assertion.Passed); writer.WriteString("detail", assertion.Detail); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteEndObject();
            window?.Close();
        }
        void Check(string name, bool passed) { assertions.Add((name, passed, "")); if (!passed) throw new InvalidOperationException(name); }
        void Screenshot(string name)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window!.CaptureRenderedFrame() ?? throw new InvalidOperationException("描画フレームがありません。");
            frame.Save(Path.Combine(output, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
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
