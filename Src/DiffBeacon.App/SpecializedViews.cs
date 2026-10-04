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

    public static async Task<Control> ImagesAsync(string left, string right, CancellationToken cancellationToken, string? middle = null,
        ImageViewSettings? settings = null)
        => await ImagesWithOptionsAsync(left, right, cancellationToken, middle, settings, null);

    internal static async Task<Control> ImagesWithOptionsAsync(string left, string right, CancellationToken cancellationToken, string? middle,
        ImageViewSettings? settings, ImageApplicationOptionsStore? applicationOptions)
    {
        var leftSnapshot = await ImageComparisonEngine.OpenAsync(left, cancellationToken);
        var rightSnapshot = await ImageComparisonEngine.OpenAsync(right, cancellationToken);
        var middleSnapshot = string.IsNullOrWhiteSpace(middle) ? null : await ImageComparisonEngine.OpenAsync(middle, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var panel = new ImagePanel(leftSnapshot, rightSnapshot, middleSnapshot, applicationOptions);
        panel.ConfigureEditing(middleSnapshot is null ? [left, right] : [left, middle!, right],
            new bool[middleSnapshot is null ? 2 : 3]);
        try
        {
            await panel.ApplySettingsAsync(settings ?? new(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return panel;
        }
        catch { panel.Dispose(); throw; }
    }
    public static async Task<Control> BinaryAsync(string left, string right, CancellationToken cancellationToken, bool leftReadOnly = false, bool rightReadOnly = false, Action<string>? guardOutput = null, string? middle = null, bool middleReadOnly = false)
    {
        const int limit = 16 * 1024 * 1024;
        if (new FileInfo(left).Length > limit || new FileInfo(right).Length > limit) throw new InvalidOperationException("16進比較の上限は各16 MiBです。");
        var a = await ReadBinaryAsync(left, limit, cancellationToken);
        var b = await ReadBinaryAsync(right, limit, cancellationToken);
        var c = string.IsNullOrWhiteSpace(middle) ? null : await ReadBinaryAsync(middle, limit, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return BinarySnapshot(a, b, left, right, leftReadOnly, rightReadOnly, guardOutput, middle: c, middleReadOnly: middleReadOnly);
    }
    internal static Control BinarySnapshot(byte[] a, byte[] b, string left, string right,
        bool leftReadOnly, bool rightReadOnly, Action<string>? guardOutput = null,
        string? leftProtectedPath = null, string? rightProtectedPath = null, bool fixedLeftReadOnly = false, bool fixedRightReadOnly = false, byte[]? middle = null, bool middleReadOnly = false)
    {
        return new BinaryPanel(a, b, leftReadOnly, rightReadOnly, middle, middleReadOnly) { FixedLeftReadOnly = fixedLeftReadOnly, FixedRightReadOnly = fixedRightReadOnly };
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
        if (control is BinaryPanel panel) { panel.LeftReadOnly = left; panel.RightReadOnly = right; if (panel.HasMiddle) panel.MiddleReadOnly = middle; panel.ApplyReadOnly?.Invoke(); }
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
    private static async Task PickBinaryOutputAsync(BinaryPanel control, int side, string name)
    {
        if (control.SavePathPicker is { } picker)
        { var chosen = await picker(side); if (chosen is not null) await control.SaveToAsync(side, chosen); return; }
        var top = TopLevel.GetTopLevel(control); if (top is null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = name, Title = "バイナリを別名保存" });
        if (file is null) return;
        if (file.TryGetLocalPath() is not string path) throw new IOException("ローカルの保存先を選択してください。");
        await control.SaveToAsync(side, path);
    }
}
