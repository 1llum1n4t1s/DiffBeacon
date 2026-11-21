using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessLineTransformReplaceListChecks
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "line-transform-replace-lists"));
        if (Directory.Exists(directory)) throw new IOException("ReplaceLists UI verification requires fresh output.");
        Directory.CreateDirectory(directory);
        var observations = new List<(string Name, bool Passed)>();
        void Verify(string name, bool passed)
        {
            check("line transform replace lists " + name, passed, ""); observations.Add((name, passed));
        }
        var list = Path.Combine(directory, "list.tsv");
        var left = Path.Combine(directory, "left.txt"); var right = Path.Combine(directory, "right.txt");
        const string original = "#comment\r\na\tb\textra\nb\tX\n";
        File.WriteAllText(list, original, new UTF8Encoding(false));
        var quoted = Quote(list);
        foreach (var noopt in new[] { false, true })
        {
            var rules = new[] { new LineTransformRule((noopt ? "@noopt " : "") + "ReplaceWithList(Line," + quoted + ")", TablePaneMask.All) };
            var counting = new CountingEnvironment(LineTransformListEnvironments.Create());
            var plan = LineTransformPlan.Compile(rules, new(), listEnvironment: counting);
            Verify("compile load lifetime " + noopt, counting.Reads == (noopt ? 0 : 1));
            File.WriteAllText(list, "a\tY\n", new UTF8Encoding(false));
            Verify("row loading lifetime " + noopt, plan.Transform(["a", "a"], TablePaneMask.Left, false).SequenceEqual(noopt ? new[] { "Y", "Y" } : new[] { "X", "X" })
                && counting.Reads == (noopt ? 2 : 1));
            File.WriteAllText(list, original, new UTF8Encoding(false));
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var rejected = false;
            try { LineTransformPlan.Compile([new("ReplaceWithList(Line," + quoted + ")", TablePaneMask.All)], new(), cancelled.Token, listEnvironment: LineTransformListEnvironments.Create()); }
            catch (OperationCanceledException) { rejected = true; }
            Verify("cancel before load", rejected);
        }
        var limited = false;
        try { LineTransformPlan.Compile([new("ReplaceWithList(Line," + quoted + ")", TablePaneMask.All)], new(maximumWork: 100), listEnvironment: LineTransformListEnvironments.Create()); }
        catch (InvalidOperationException) { limited = true; }
        Verify("shared read budget rejects", limited);
        File.WriteAllText(left, "a\r\na", new UTF8Encoding(false)); File.WriteAllText(right, "X\r\nX", new UTF8Encoding(false));
        pane.ApplyProject(new ComparisonProject { Mode = "Text", LeftPath = left, RightPath = right });
        pane.DiscardChanges(); pump(pane.CompareProjectAsync()); Settle();
        Verify("original comparison differs", pane.CurrentDiff is { HasDifferences: true });
        var oldShown = LineTransformDialog.Shown; LineTransformDialog? shown = null;
        LineTransformDialog.Shown = dialog => shown = dialog;
        try
        {
            Click(pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "行変換…"))); Settle();
            var dialog = shown ?? throw new InvalidOperationException("Line transform dialog missing.");
            dialog.Target.SelectedIndex = 1;
            dialog.Expression.Text = "ReplaceWithList(Line," + quoted + ")";
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "LineTransformAdd"));
            Verify("dialog accepts path-backed rule", dialog.Rules.Count == 1 && string.IsNullOrEmpty(dialog.Error.Text));
            Click(dialog.Accept); Settle(); pump(pane.CompareProjectAsync()); Settle();
            Verify("adopted key retains original editors", pane.CurrentDiff is { HasDifferences: false } && pane.LeftEditor.Text == "a\r\na" && pane.RightEditor.Text == "X\r\nX");
            screenshot("line-transform-replace-lists.png");
        }
        finally { LineTransformDialog.Shown = oldShown; }
        var rejectedOutput = false;
        try { pane.EnsureProjectOutputWritable(list); } catch (InvalidOperationException) { rejectedOutput = true; }
        Verify("loaded list is protected", rejectedOutput && File.ReadAllText(list) == original);
        var workspace = Path.Combine(directory, "workspace.json");
        pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new ComparisonWorkspace { Entries = [pane.CaptureProject()] }));
        var loading = WorkspaceStore.LoadWorkspaceAsync(workspace); pump(loading);
        pane.ApplyProject(loading.Result.Entries.Single()); pane.DiscardChanges(); pump(pane.CompareProjectAsync()); Settle();
        Verify("workspace restores path rule", pane.CurrentDiff is { HasDifferences: false });
        var previous = pane.CurrentDiff;
        var invalid = Path.Combine(directory, "oversize.tsv");
        using (var bytes = new FileStream(invalid, FileMode.CreateNew, FileAccess.Write)) bytes.SetLength(LineTransformListEnvironments.MaximumFileBytes + 1L);
        // AddとApplyの双方でliteral読込みの失敗を表示し、訂正後に同じdialogを操作できる。
        var errorDialog = new LineTransformDialog([]); var dialogClosed = false;
        errorDialog.Closed += (_, _) => dialogClosed = true;
        var errorResult = errorDialog.ShowDialog<LineTransformRule[]?>(window); Settle();
        errorDialog.Expression.Text = "ReplaceWithList(Line," + Quote(invalid) + ")";
        Click(errorDialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "LineTransformAdd")); Settle();
        Verify("oversized literal Add keeps dialog usable", !dialogClosed && errorDialog.Rules.Count == 0
            && errorDialog.Accept.IsEnabled && errorDialog.Error.Text?.Contains("4 MiB", StringComparison.Ordinal) == true && ErrorVisible(errorDialog));
        CaptureDialog(errorDialog, "dialog-add-error.png");
        File.WriteAllText(invalid, original, new UTF8Encoding(false));
        Click(errorDialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "LineTransformAdd"));
        using (var bytes = new FileStream(invalid, FileMode.Open, FileAccess.Write)) bytes.SetLength(LineTransformListEnvironments.MaximumFileBytes + 1L);
        Click(errorDialog.Accept); Settle();
        Verify("oversized literal Apply keeps dialog usable", !dialogClosed && errorDialog.Rules.Count == 1
            && errorDialog.Accept.IsEnabled && errorDialog.Error.Text?.Contains("4 MiB", StringComparison.Ordinal) == true && ErrorVisible(errorDialog));
        CaptureDialog(errorDialog, "dialog-apply-error.png");
        File.WriteAllText(invalid, original, new UTF8Encoding(false)); Click(errorDialog.Accept); pump(errorResult); Settle();
        Verify("list error dialog accepts corrected file", dialogClosed && errorResult.Result is { Length: 1 });
        using (var bytes = new FileStream(invalid, FileMode.Open, FileAccess.Write)) bytes.SetLength(LineTransformListEnvironments.MaximumFileBytes + 1L);

        // 動的pathはSetLineTransforms時に読み込まず、比較失敗時にも確定表示を保つ。
        pane.SetLineTransforms([new("@noopt ReplaceWithList(Line,If(LineNumber>0," + Quote(invalid) + "," + quoted + "))", TablePaneMask.Left)]);
        pump(pane.CompareProjectAsync()); Settle();
        Verify("failed candidate retains display", ReferenceEquals(previous, pane.CurrentDiff) && pane.LeftEditor.Text == "a\r\na");
        pane.SetLineTransforms([new("ReplaceWithList(Line," + quoted + ")", TablePaneMask.Left)]);
        pump(pane.CompareProjectAsync()); Settle();
        // 公開直前の衝突ではtempだけを除去し、既存JSONと他のassetを保持する。
        var assetProject = pane.CaptureProject() with { LineTransformListAssets = new(StringComparer.Ordinal) { [list] = list } };
        var assetWorkspace = new ComparisonWorkspace { FormatVersion = 10, Entries = [assetProject] };
        // 相対DTOの構造コピーがCWDの同名pathを読まず、絶対snapshotだけを使用する。
        var cwdSpoof = Path.Combine(directory, "cwd-spoof"); var cwdActual = Path.Combine(directory, "cwd-actual");
        Directory.CreateDirectory(cwdSpoof); Directory.CreateDirectory(cwdActual);
        var originalListBytes = File.ReadAllBytes(list);
        var cwdHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(originalListBytes));
        var cwdWorkspace = Path.Combine(cwdActual, "workspace.json"); var cwdPackage = Path.Combine(cwdActual, "package.zip");
        var spoofWorkspaceAsset = Path.Combine(cwdSpoof, "workspace.json.assets", cwdHash + ".replace-list");
        var spoofPackageAsset = Path.Combine(cwdSpoof, "replace-lists", "1", "1-" + Path.GetFileName(list));
        foreach (var spoof in new[] { spoofWorkspaceAsset, spoofPackageAsset })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(spoof)!);
            using var stream = new FileStream(spoof, FileMode.CreateNew, FileAccess.Write); stream.SetLength(LineTransformListEnvironments.MaximumFileBytes + 1L);
        }
        File.WriteAllText(cwdWorkspace, "previous workspace"); File.WriteAllText(cwdPackage, "previous package");
        var originalCwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = cwdSpoof;
            pump(WorkspaceStore.SaveWorkspaceAsync(cwdWorkspace, assetWorkspace));
            pump(ComparisonPackage.CreateAsync(assetWorkspace, cwdPackage, new()));
        }
        finally { Environment.CurrentDirectory = originalCwd; }
        var cwdRestored = WorkspaceStore.LoadWorkspaceAsync(cwdWorkspace); pump(cwdRestored);
        Verify("workspace relative DTO ignores oversized CWD asset", cwdRestored.Result.Entries.Single().LineTransformListAssets is { } cwdMap
            && cwdMap.Values.All(path => Path.IsPathRooted(path) && File.ReadAllBytes(path).SequenceEqual(originalListBytes))
            && new FileInfo(spoofWorkspaceAsset).Length == LineTransformListEnvironments.MaximumFileBytes + 1L);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(cwdPackage))
        {
            var entry = zip.GetEntry("replace-lists/1/1-" + Path.GetFileName(list)) ?? throw new InvalidDataException("Packaged list missing.");
            using var stream = entry.Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes);
            Verify("package relative DTO ignores oversized CWD asset", bytes.ToArray().SequenceEqual(originalListBytes)
                && new FileInfo(spoofPackageAsset).Length == LineTransformListEnvironments.MaximumFileBytes + 1L
                && File.ReadAllBytes(list).SequenceEqual(originalListBytes));
        }
        var protectedInput = cwdRestored.Result.Entries.Single().LineTransformListAssets!.Values.Single();
        var protectedBytes = File.ReadAllBytes(protectedInput); var protectedWorkspaceBytes = File.ReadAllBytes(cwdWorkspace);
        var inputRejected = false;
        try { pump(WorkspaceStore.SaveWorkspaceAsync(protectedInput, cwdRestored.Result)); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { inputRejected = true; }
        Verify("relative DTO keeps embedded input output protection", inputRejected && File.ReadAllBytes(protectedInput).SequenceEqual(protectedBytes)
            && File.ReadAllBytes(cwdWorkspace).SequenceEqual(protectedWorkspaceBytes));

        var faultWorkspace = Path.Combine(directory, "fault.json");
        pump(WorkspaceStore.SaveWorkspaceAsync(faultWorkspace, assetWorkspace));
        var sentinel = File.ReadAllBytes(faultWorkspace);
        var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(list)));
        var existingAsset = Path.Combine(faultWorkspace + ".assets", originalHash + ".replace-list"); var existingBytes = File.ReadAllBytes(existingAsset);
        var faultList = Path.Combine(directory, "publish-new.tsv"); File.WriteAllText(faultList, "a\tZ\n", new UTF8Encoding(false));
        var faultCandidate = assetWorkspace with { Entries = [assetProject with { LineTransformListAssets = new(StringComparer.Ordinal) { [list] = list, [faultList] = faultList } }] };
        string? collidedAsset = null; string? stagedTemporary = null; var collisionRejected = false;
        var oldBeforePublish = LineTransformListEnvironments.BeforeAssetPublish;
        try
        {
            LineTransformListEnvironments.BeforeAssetPublish = (temporary, asset) =>
            { stagedTemporary = temporary; collidedAsset = asset; Directory.CreateDirectory(asset); };
            try { pump(WorkspaceStore.SaveWorkspaceAsync(faultWorkspace, faultCandidate)); }
            catch (IOException) { collisionRejected = true; }
        }
        finally { LineTransformListEnvironments.BeforeAssetPublish = oldBeforePublish; }
        Verify("asset publish collision is atomic", collisionRejected && collidedAsset is not null && Directory.Exists(collidedAsset)
            && !File.Exists(collidedAsset) && stagedTemporary is not null && !File.Exists(stagedTemporary)
            && File.ReadAllBytes(faultWorkspace).SequenceEqual(sentinel) && File.ReadAllBytes(existingAsset).SequenceEqual(existingBytes)
            && !Directory.EnumerateFiles(faultWorkspace + ".assets", ".replace-list-*.tmp").Any());
        if (collidedAsset is not null) Directory.Move(collidedAsset, Path.Combine(directory, "publish-collision"));
        pump(WorkspaceStore.SaveWorkspaceAsync(faultWorkspace, faultCandidate));
        Verify("asset publish retry succeeds", collidedAsset is not null && File.ReadAllBytes(collidedAsset).SequenceEqual(File.ReadAllBytes(faultList))
            && File.ReadAllBytes(existingAsset).SequenceEqual(existingBytes));

        // 包装Aを開いた別windowから既存Bへ保存し、再利用assetもText SaveAsから保護する。
        var packageA = Path.Combine(directory, "package-a.zip"); var extractedA = Path.Combine(directory, "package-a");
        pump(ComparisonPackage.CreateAsync(assetWorkspace, packageA, new()));
        System.IO.Compression.ZipFile.ExtractToDirectory(packageA, extractedA);
        var loadedA = WorkspaceStore.LoadWorkspaceAsync(Path.Combine(extractedA, "project.json")); pump(loadedA);
        var workspaceB = Path.Combine(directory, "workspace-b.json"); pump(WorkspaceStore.SaveWorkspaceAsync(workspaceB, loadedA.Result));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(list)));
        var assetB = Path.Combine(workspaceB + ".assets", hash + ".replace-list"); var beforeAsset = File.ReadAllBytes(assetB);
        using (var bytes = new FileStream(list, FileMode.Open, FileAccess.Write)) bytes.SetLength(LineTransformListEnvironments.MaximumFileBytes + 1L);
        var reuseWindow = new MainWindow(null, new ImageApplicationOptionsStore(Path.Combine(directory, "reuse-options.json"))); reuseWindow.Show(); Settle();
        try
        {
            pump(reuseWindow.OpenWorkspaceAsync(Path.Combine(extractedA, "project.json"), discardChanges: true));
            pump(reuseWindow.SaveWorkspaceAsync(workspaceB));
            var reusedPane = reuseWindow.ActivePane;
            var snapshotShown = LineTransformDialog.Shown; LineTransformDialog? snapshotDialog = null;
            LineTransformDialog.Shown = dialog => snapshotDialog = dialog;
            try
            {
                Click(reusedPane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "行変換…"))); Settle();
                var dialog = snapshotDialog ?? throw new InvalidOperationException("Snapshot dialog missing.");
                Click(dialog.Accept); Settle(); pump(reusedPane.CompareProjectAsync()); Settle();
                Verify("packaged dialog applies embedded literal with oversized original", !dialog.IsVisible && reusedPane.CurrentDiff is { HasDifferences: false });
            }
            finally { LineTransformDialog.Shown = snapshotShown; snapshotDialog?.Close(); }
            var alias = Path.Combine(directory, ".", "workspace-b.json.assets", Path.GetFileName(assetB));
            var saveRejected = false;
            try { pump(reusedPane.SaveTextToAsync(true, alias)); }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException) { saveRejected = true; }
            Verify("reused workspace asset rejects alias Text SaveAs", saveRejected && beforeAsset.SequenceEqual(File.ReadAllBytes(assetB)));
        }
        finally { reuseWindow.Close(); File.WriteAllText(list, original, new UTF8Encoding(false)); }

        using (var file = File.Create(Path.Combine(directory, "observations.json")))
        using (var writer = new Utf8JsonWriter(file))
        {
            writer.WriteStartArray();
            foreach (var observation in observations)
            { writer.WriteStartObject(); writer.WriteString("name", observation.Name); writer.WriteBoolean("passed", observation.Passed); writer.WriteEndObject(); }
            writer.WriteEndArray();
        }
        static bool ErrorVisible(LineTransformDialog dialog)
        {
            var scroll = dialog.Error.GetVisualAncestors().OfType<ScrollViewer>().First();
            var point = dialog.Error.TranslatePoint(default, scroll);
            return point is { } position && position.Y >= 0 && position.Y + dialog.Error.Bounds.Height <= scroll.Bounds.Height;
        }
        void CaptureDialog(Window dialog, string name)
        {
            using var frame = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("Dialog frame missing.");
            frame.Save(Path.Combine(directory, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void Settle() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private sealed class CountingEnvironment(ILineTransformListEnvironment inner) : ILineTransformListEnvironment
    {
        internal int Reads { get; private set; }
        public string ReadList(string path, LineTransformBudget budget, CancellationToken token)
        { Reads++; return inner.ReadList(path, budget, token); }
    }
}
