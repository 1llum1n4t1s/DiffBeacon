using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public static partial class SpecializedViews
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Menlo, Consolas, monospace");
    public static void Release(Control? control) { if (control is IDisposable disposable) disposable.Dispose(); }
    public static bool HasUnsavedChanges(Control? control) => control is BinaryPanel binary && binary.IsDirty?.Invoke() == true || control is TablePanel table && table.HasPendingCellEdit
        || control is ImagePanel image && image.HasUnsavedChanges;
    public static void DiscardChanges(Control? control) { if (control is BinaryPanel binary) binary.MarkClean?.Invoke(); if (control is TablePanel table) table.DiscardCellDraft(); if (control is ImagePanel image) image.DiscardChanges(); }
    public static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".apng" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp";

    public static async Task<Control> ImagesAsync(string left, string right, CancellationToken cancellationToken, string? middle = null)
    {
        var leftSnapshot = await ImageComparisonEngine.OpenAsync(left, cancellationToken);
        var rightSnapshot = await ImageComparisonEngine.OpenAsync(right, cancellationToken);
        var middleSnapshot = string.IsNullOrWhiteSpace(middle) ? null : await ImageComparisonEngine.OpenAsync(middle, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var panel = new ImagePanel(leftSnapshot, rightSnapshot, middleSnapshot);
        panel.ConfigureEditing(middleSnapshot is null ? [left, right] : [left, middle!, right],
            new bool[middleSnapshot is null ? 2 : 3]);
        try
        {
            await panel.SetFramesAsync(1, 1, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return panel;
        }
        catch { panel.Dispose(); throw; }
    }
    public static async Task<Control> BinaryAsync(string left, string right, CancellationToken cancellationToken, bool leftReadOnly = false, bool rightReadOnly = false, Action<string>? guardOutput = null)
    {
        const int limit = 16 * 1024 * 1024;
        if (new FileInfo(left).Length > limit || new FileInfo(right).Length > limit) throw new InvalidOperationException("16進比較の上限は各16 MiBです。");
        var a = await ReadBinaryAsync(left, limit, cancellationToken);
        var b = await ReadBinaryAsync(right, limit, cancellationToken);
        var root = new BinaryPanel { LeftReadOnly = leftReadOnly, RightReadOnly = rightReadOnly };
        var actions = new WrapPanel();
        var offset = new NumericUpDown { Minimum = 0, Maximum = Math.Max(a.Length, b.Length), Increment = 4096, Value = 0, Width = 140 };
        var status = new TextBlock { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        var leftEditor = HexEditor(); var rightEditor = HexEditor();
        root.ApplyReadOnly = () => { leftEditor.IsReadOnly = root.LeftReadOnly; rightEditor.IsReadOnly = root.RightReadOnly; };
        root.ApplyReadOnly();
        var differences = new ListBox { MaxHeight = 160, FontFamily = Mono };
        var pageStart = 0; var updating = false;
        var leftDirty = false; var rightDirty = false;
        var leftBaseline = ""; var rightBaseline = "";
        bool PendingEdits() => leftEditor.Text != leftBaseline || rightEditor.Text != rightBaseline;
        root.IsDirty = () => leftDirty || rightDirty || PendingEdits();
        root.MarkClean = () => { leftDirty = false; rightDirty = false; leftBaseline = leftEditor.Text ?? ""; rightBaseline = rightEditor.Text ?? ""; };
        var ranges = new List<(int Start, int Length)>();
        var totalRanges = 0;
        void Refresh(bool rebuildRanges = true)
        {
            updating = true;
            pageStart = (int)(offset.Value ?? 0);
            leftEditor.Text = Hex(a.AsSpan(Math.Min(pageStart, a.Length), Math.Min(4096, Math.Max(0, a.Length - pageStart))));
            rightEditor.Text = Hex(b.AsSpan(Math.Min(pageStart, b.Length), Math.Min(4096, Math.Max(0, b.Length - pageStart))));
            leftBaseline = leftEditor.Text ?? ""; rightBaseline = rightEditor.Text ?? "";
            if (rebuildRanges)
            {
                ranges.Clear();
                totalRanges = 0;
                var n = Math.Max(a.Length, b.Length);
                for (var i = 0; i < n; i++)
                {
                    if (i < a.Length && i < b.Length && a[i] == b[i]) continue;
                    var start = i;
                    while (i + 1 < n && (i + 1 >= a.Length || i + 1 >= b.Length || a[i + 1] != b[i + 1])) i++;
                    totalRanges++;
                    if (ranges.Count < 10_000) ranges.Add((start, i - start + 1));
                }
                differences.ItemsSource = ranges.Select(x => $"0x{x.Start:X8} · {x.Length:N0} bytes").ToArray();
            }
            status.Text = $"左 {a.Length:N0} bytes / 右 {b.Length:N0} bytes · 差分範囲 {totalRanges:N0}（一覧は先頭1万範囲まで）· 表示先頭 0x{pageStart:X8}（最大4096 bytes）。編集は同じ長さの16進値を入力し適用します。保存は別名保存です。";
            updating = false;
        }
        void Apply(bool toRight)
        {
            if (toRight ? root.RightReadOnly : root.LeftReadOnly) { status.Text = "この側はプロジェクトで読取り専用に指定されています。"; return; }
            var editor = toRight ? rightEditor : leftEditor;
            var bytes = toRight ? b : a;
            try
            {
                var compact = string.Concat((editor.Text ?? "").Where(c => !char.IsWhiteSpace(c)));
                var replacement = Convert.FromHexString(compact);
                var length = Math.Min(4096, Math.Max(0, bytes.Length - pageStart));
                if (replacement.Length != length) throw new FormatException("編集前と同じバイト数を入力してください。");
                var start = Math.Min(pageStart, bytes.Length);
                if (toRight) { rightDirty |= !replacement.AsSpan().SequenceEqual(bytes.AsSpan(start, length)); rightBaseline = editor.Text ?? ""; }
                else { leftDirty |= !replacement.AsSpan().SequenceEqual(bytes.AsSpan(start, length)); leftBaseline = editor.Text ?? ""; }
                replacement.CopyTo(bytes, start);
                if (!PendingEdits()) Refresh(); else status.Text = "適用しました。もう一方の編集中の値も適用してください。";
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { status.Text = ex.Message; }
        }
        void Merge(bool toRight)
        {
            if (toRight ? root.RightReadOnly : root.LeftReadOnly) { status.Text = "この側はプロジェクトで読取り専用に指定されています。"; return; }
            if (PendingEdits()) { status.Text = "先に16進編集を適用してください。"; return; }
            if (differences.SelectedIndex < 0 || differences.SelectedIndex >= ranges.Count) return;
            var range = ranges[differences.SelectedIndex];
            var source = toRight ? a : b; var target = toRight ? b : a;
            // オフセット整列を保つため、末尾の長さ差以外は挿入・削除しない。
            var sourceLength = Math.Min(range.Length, Math.Max(0, source.Length - range.Start));
            if (range.Start + range.Length >= target.Length && range.Start + range.Length >= source.Length)
                Array.Resize(ref target, range.Start + sourceLength);
            source.AsSpan(Math.Min(range.Start, source.Length), sourceLength).CopyTo(target.AsSpan(Math.Min(range.Start, target.Length)));
            if (toRight) { b = target; rightDirty = true; } else { a = target; leftDirty = true; }
            offset.Maximum = Math.Max(a.Length, b.Length); Refresh();
        }
        actions.Children.Add(new TextBlock { Text = "表示オフセット", Margin = new Thickness(8) }); actions.Children.Add(offset);
        AddButton(actions, "左編集を適用", () => { Apply(false); return Task.CompletedTask; });
        AddButton(actions, "右編集を適用", () => { Apply(true); return Task.CompletedTask; });
        AddButton(actions, "選択範囲 →", () => { Merge(true); return Task.CompletedTask; });
        AddButton(actions, "← 選択範囲", () => { Merge(false); return Task.CompletedTask; });
        root.SaveContent = async (rightSide, path, token) =>
        {
            if (PendingEdits()) throw new InvalidOperationException("先に16進編集を適用してください。");
            GuardOutput(path);
            await WriteBinaryCopyAsync(path, rightSide ? b : a, token);
            if (rightSide) rightDirty = false; else leftDirty = false;
            status.Text = "バイナリを保存しました。";
        };
        AddButton(actions, "左を別名保存", () => PickBinaryOutputAsync(root, false, Path.GetFileName(left)));
        AddButton(actions, "右を別名保存", () => PickBinaryOutputAsync(root, true, Path.GetFileName(right)));
        void GuardOutput(string path)
        {
            guardOutput?.Invoke(path);
            if ((root.LeftReadOnly && DiffBeacon.Providers.ArchivePaths.SameFile(left, path)) || (root.RightReadOnly && DiffBeacon.Providers.ArchivePaths.SameFile(right, path)))
                throw new InvalidOperationException("読取り専用に指定された入力を上書きできません。");
        }
        offset.ValueChanged += (_, _) =>
        {
            if (updating) return;
            if (PendingEdits()) { updating = true; offset.Value = pageStart; updating = false; status.Text = "先に16進編集を適用してください。"; return; }
            Refresh(false);
        };
        differences.SelectionChanged += (_, _) => { if (!updating && differences.SelectedIndex >= 0 && differences.SelectedIndex < ranges.Count) offset.Value = ranges[differences.SelectedIndex].Start; };
        DockPanel.SetDock(actions, Dock.Top); root.Children.Add(actions);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        DockPanel.SetDock(differences, Dock.Bottom); root.Children.Add(differences);
        root.Children.Add(Pair(leftEditor, rightEditor)); Refresh(); return root;
    }

    public static Control StructuredJson(string leftText, string rightText)
    {
        var a = StructuredComparer.NormalizeJson(leftText); var b = StructuredComparer.NormalizeJson(rightText);
        var root = new DockPanel(); var status = new TextBlock { Text = a == b ? "JSONの構造と値は一致しています（キー順と同値な数値表現は無視）。" : "JSONの構造または値が異なります。オブジェクトキー順と同値な数値表現は無視し、配列順は保持します。", Margin = new Thickness(8) };
        DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        var x = HexEditor(); x.Text = a; x.IsReadOnly = true; var y = HexEditor(); y.Text = b; y.IsReadOnly = true;
        root.Children.Add(Pair(x, y)); return root;
    }

    internal static char DetectSeparator(string a, string b)
    {
        // 引用符内のタブは区切りとして数えない。
        foreach (var text in new[] { a, b })
        { var quoted = false; foreach (var c in text) { if (c == '"') quoted = !quoted; if (!quoted && c == '\t') return '\t'; if (!quoted && c is '\r' or '\n') break; } }
        return ',';
    }
    public sealed class BinaryPanel : DockPanel
    {
        public bool LeftReadOnly { get; set; }
        public bool RightReadOnly { get; set; }
        public Action? ApplyReadOnly { get; set; }
        internal Func<bool, string, CancellationToken, Task>? SaveContent { get; set; }
        public Task SaveToAsync(bool rightSide, string path, CancellationToken token = default) => SaveContent?.Invoke(rightSide, path, token) ?? throw new InvalidOperationException("バイナリを読み込んでいません。");
        public Func<bool>? IsDirty { get; set; }
        public Action? MarkClean { get; set; }
    }
    private static async Task<byte[]> ReadBinaryAsync(string path, int limit, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        using var output = new MemoryStream(); await CopyBoundedAsync(input, output, limit, token); return output.ToArray();
    }
    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        var buffer = new byte[64 * 1024]; long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, token); if (count == 0) return;
            total += count; if (total > limit) throw new InvalidDataException("ファイルの読込サイズが上限を超えました。");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }
    public static void SetProjectReadOnly(Control? control, bool left, bool right, bool middle = false)
    {
        if (control is BinaryPanel panel) { panel.LeftReadOnly = left; panel.RightReadOnly = right; panel.ApplyReadOnly?.Invoke(); }
        if (control is ImagePanel image) image.SetReadOnly(image.MiddleFrameCount.HasValue ? [left, middle, right] : [left, right]);
    }
    private static TextBox HexEditor() => new() { AcceptsReturn = true, AcceptsTab = true, FontFamily = Mono, TextWrapping = TextWrapping.NoWrap, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private static string Hex(ReadOnlySpan<byte> bytes)
    { var text = new StringBuilder(); for (var i = 0; i < bytes.Length; i++) { text.Append(bytes[i].ToString("X2")); text.Append((i + 1) % 16 == 0 ? '\n' : ' '); } return text.ToString(); }
    private static ScrollViewer Scroll(Control control) => new() { Content = control, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    private static Grid Pair(Control left, Control right)
    { var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 }; grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right); return grid; }
    private static void AddButton(Panel panel, string title, Func<Task> action)
    {
        var button = new Button { Content = title, Margin = new Thickness(4) };
        button.Click += async (_, _) =>
        {
            try { button.IsEnabled = false; await action(); }
            catch (Exception ex)
            {
                if (TopLevel.GetTopLevel(button) is Window owner)
                { var dialog = new Window { Title = "操作エラー", Width = 500, Height = 180, Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20) } }; await dialog.ShowDialog(owner); }
            }
            finally { button.IsEnabled = true; }
        };
        panel.Children.Add(button);
    }
    private static async Task PickBinaryOutputAsync(BinaryPanel control, bool rightSide, string name)
    {
        var top = TopLevel.GetTopLevel(control); if (top is null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = name, Title = "バイナリを別名保存" });
        if (file is null) return;
        if (file.TryGetLocalPath() is not string path) throw new IOException("ローカルの保存先を選択してください。");
        await control.SaveToAsync(rightSide, path);
    }
    private static async Task WriteBinaryCopyAsync(string path, byte[] bytes, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(path);
        void ValidateOutput()
        {
            for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
            {
                try
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("リンクを経由して保存できません。");
                    if (current == fullPath && (attributes & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("読取り専用ファイルへ保存できません。");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        token.ThrowIfCancellationRequested(); ValidateOutput();
        var exists = File.Exists(fullPath);
        var attributes = exists ? File.GetAttributes(fullPath) : FileAttributes.Normal;
        UnixFileMode? mode = exists && !OperatingSystem.IsWindows() ? File.GetUnixFileMode(fullPath) : null;
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, ".diffbeacon-binary-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true); }
            if (mode.HasValue && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, mode.Value);
            if (OperatingSystem.IsWindows() && exists)
            { var preserved = attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed); File.SetAttributes(temporary, preserved == 0 ? FileAttributes.Normal : preserved); }
            ValidateOutput(); token.ThrowIfCancellationRequested();
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null); else File.Move(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
