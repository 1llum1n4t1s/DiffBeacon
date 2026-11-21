using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Layout;
using DiffBeacon.Core;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    /// <summary>印のダイアログ。OK か適用でウィンドウの印を置き換え、キャンセルは捨てる。</summary>
    private async Task<bool> ShowMarkerDialogAsync(Window owner, List<TextMarker> draft, bool enabled)
    {
        var accepted = false;
        var selected = draft.Count - 1;
        var syncing = false;
        var list = new ListBox { Name = "marker-list", Height = 140 };
        var find = new TextBox { Name = "marker-text" };
        var color = new ComboBox { Name = "marker-color", ItemsSource = MarkerColorNames() };
        var whole = new CheckBox { Name = "marker-whole", Content = UiText.Menu("Match &whole word only") };
        var matchCase = new CheckBox { Name = "marker-case", Content = UiText.Menu("Match &case") };
        var regex = new CheckBox { Name = "marker-regex", Content = UiText.Menu("Regular &expression") };
        var markersEnabled = new CheckBox { Name = "marker-enabled", Content = UiText.Menu("Enable &markers"), IsChecked = enabled };
        var message = new TextBlock { Name = "marker-message", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        void ReloadList()
        {
            syncing = true;
            list.Items.Clear();
            foreach (var marker in draft) list.Items.Add((marker.Visible ? "✓ " : "") + marker.Text);
            if (draft.Count == 0) selected = -1;
            else selected = Math.Clamp(selected, 0, draft.Count - 1);
            list.SelectedIndex = selected;
            syncing = false;
            LoadSelected();
        }
        void LoadSelected()
        {
            syncing = true;
            var has = selected >= 0 && selected < draft.Count;
            find.IsEnabled = color.IsEnabled = matchCase.IsEnabled = regex.IsEnabled = has;
            if (!has)
            {
                find.Text = ""; color.SelectedIndex = 0; whole.IsChecked = matchCase.IsChecked = regex.IsChecked = false;
                whole.IsEnabled = false; syncing = false; return;
            }
            var marker = draft[selected];
            find.Text = marker.Text; color.SelectedIndex = Math.Clamp(marker.ColorIndex, 0, 2);
            matchCase.IsChecked = marker.MatchCase; regex.IsChecked = marker.Regex;
            whole.IsChecked = marker.Regex ? false : marker.WholeWord; whole.IsEnabled = !marker.Regex;
            syncing = false;
        }
        void StoreSelected()
        {
            if (syncing || selected < 0 || selected >= draft.Count) return;
            var marker = draft[selected];
            var useRegex = regex.IsChecked == true;
            draft[selected] = marker with
            {
                Text = find.Text ?? "", ColorIndex = Math.Clamp(color.SelectedIndex, 0, 2),
                MatchCase = matchCase.IsChecked == true, WholeWord = useRegex ? false : whole.IsChecked == true,
                Regex = useRegex, Visible = marker.Visible,
            };
            syncing = true;
            list.Items[selected] = (draft[selected].Visible ? "✓ " : "") + draft[selected].Text;
            syncing = false;
            whole.IsEnabled = !useRegex;
            if (useRegex) whole.IsChecked = false;
        }
        list.SelectionChanged += (_, _) =>
        {
            if (syncing) return;
            StoreSelected();
            selected = list.SelectedIndex;
            LoadSelected();
        };
        find.TextChanged += (_, _) => StoreSelected();
        color.SelectionChanged += (_, _) => StoreSelected();
        whole.IsCheckedChanged += (_, _) => StoreSelected();
        matchCase.IsCheckedChanged += (_, _) => StoreSelected();
        regex.IsCheckedChanged += (_, _) => StoreSelected();
        bool TryAccept(bool close)
        {
            StoreSelected();
            if (draft.Any(marker => marker.Text.Length == 0)) { message.Text = "印の文字列が空です。"; return false; }
            if (draft.Any(marker => marker.Text.Length > 1024)) { message.Text = "印の文字列は1024文字以下にしてください。"; return false; }
            foreach (var marker in draft)
            {
                if (!marker.Regex) continue;
                try
                {
                    PcrePattern.RejectUnsupported(marker.Text);
                    _ = new Regex(PcrePattern.RewritePossessive(marker.Text), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
                }
                catch (Exception error) when (error is FormatException or ArgumentException or RegexParseException)
                {
                    message.Text = error.Message;
                    return false;
                }
            }
            message.Text = "";
            var on = markersEnabled.IsChecked == true;
            if (_owner is MainWindow window) window.ReplaceTextMarkers(on, draft);
            else ApplySharedMarkers(draft, on);
            if (close) accepted = true;
            return true;
        }
        var add = new Button { Name = "marker-new", Content = UiText.Menu("New"), MinWidth = 80 };
        add.Click += (_, _) =>
        {
            StoreSelected();
            draft.Add(new TextMarker(UiText.Menu("New Pattern"), draft.Count % 3, false, false, false, true));
            selected = draft.Count - 1;
            ReloadList();
        };
        var delete = new Button { Name = "marker-delete", Content = UiText.Menu("&Delete"), MinWidth = 80 };
        delete.Click += (_, _) =>
        {
            if (selected < 0 || selected >= draft.Count) return;
            draft.RemoveAt(selected);
            selected = Math.Min(selected, draft.Count - 1);
            ReloadList();
        };
        var dialog = new Window
        {
            Name = "marker-dialog", Title = UiText.Menu("Markers"), Width = 560, Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false,
        };
        var ok = new Button { Name = "marker-ok", Content = UiText.Menu("&Ok"), IsDefault = true, MinWidth = 80 };
        var apply = new Button { Name = "marker-apply", Content = UiText.Menu("&Apply"), MinWidth = 80 };
        var cancel = new Button { Name = "marker-cancel", Content = UiText.Menu("Cancel"), IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => { if (TryAccept(true)) dialog.Close(true); };
        apply.Click += (_, _) => TryAccept(false);
        cancel.Click += (_, _) => dialog.Close(false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { ok, apply, cancel },
        };
        var side = new StackPanel { Spacing = 8, Children = { add, delete } };
        DockPanel.SetDock(side, Dock.Right);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(markersEnabled, Dock.Top);
        var fields = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = UiText.Menu("Fi&nd what:") }, find,
                new TextBlock { Text = UiText.Menu("&Background color:") }, color,
                whole, matchCase, regex, message,
            },
        };
        var body = new DockPanel { Margin = new Avalonia.Thickness(12) };
        body.Children.Add(markersEnabled); body.Children.Add(buttons); body.Children.Add(side); body.Children.Add(list);
        DockPanel.SetDock(fields, Dock.Bottom); body.Children.Add(fields);
        dialog.Content = body;
        ReloadList();
        var closing = dialog.ShowDialog<bool>(owner);
        MarkerDialogShown?.Invoke(dialog);
        return accepted || await closing;
    }

    /// <summary>印の色の名前。本家の Marker Color 1〜3。</summary>
    private static string[] MarkerColorNames()
    {
        var format = UiText.Menu("Marker Color %d").Replace("%d", "{0}", StringComparison.Ordinal);
        return [string.Format(System.Globalization.CultureInfo.InvariantCulture, format, 1), string.Format(System.Globalization.CultureInfo.InvariantCulture, format, 2), string.Format(System.Globalization.CultureInfo.InvariantCulture, format, 3)];
    }
}
