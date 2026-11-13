using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMergeのメインフレーム（メニューバー → ツールバー → MDIタブ）を再現する外枠。
/// 文言は Translations/WinMerge/Japanese.po の訳をそのまま使い、アクセスキーの「&amp;」は Avalonia の「_」へ置き換える。
/// ツールバー画像は旧ソースの Src/res/ToolbarEnabled32.png（32px × 26枚、GPL v2）を埋め込み、
/// Src/ToolbarButtons.cpp の g_toolbarImageOrder の順で切り出す。
/// </summary>
public sealed partial class MainWindow
{
    // g_toolbarImageOrder と同じ並び。ストリップ画像内の位置（インデックス）を表す。
    private enum ToolbarImage
    {
        New, Open, Save, Undo, Redo, SelectLineDiff, NextDiff, PrevDiff, NextConflict, PrevConflict,
        FirstDiff, CurrentDiff, LastDiff, L2R, R2L, L2RNext, R2LNext, AllRight, AllLeft, AutoMerge,
        FirstFile, PrevFile, NextFile, LastFile, Options, Refresh,
    }

    private const int ToolbarImageSize = 32;
    private const int ToolbarImageHeight = 31;
    private Bitmap? _toolbarStrip;
    internal Menu? FrameMenu { get; private set; }
    internal Panel? FrameToolbar { get; private set; }

    private Control BuildFrame()
    {
        var frame = new StackPanel { Background = WinMergeColors.FrameFace };
        FrameMenu = BuildMenu();
        frame.Children.Add(FrameMenu);
        frame.Children.Add(new Border { BorderBrush = WinMergeColors.FrameBorder, BorderThickness = new Thickness(0, 1, 0, 1), Child = FrameToolbar = BuildToolbar() });
        return frame;
    }

    private Menu BuildMenu()
    {
        var menu = new Menu { Name = "frame-menu", Background = WinMergeColors.FrameFace, Foreground = WinMergeColors.FrameText };
        menu.Items.Add(Popup("ファイル(_F)",
            Item("新規(_N)", "Ctrl+N", () => AddSessionFromUiAsync()),
            Item("開く(_O)...", "Ctrl+O", () => { FocusPaths(); return Task.CompletedTask; }),
            Separator(),
            Pane("プロジェクトを開く(_J)...", "Ctrl+J", FrameCommand.OpenProject),
            Pane("プロジェクトを保存(_V)...", null, FrameCommand.SaveProject),
            Separator(),
            Popup("保存(_E)",
                Pane("左側のファイルを保存(_L)", null, FrameCommand.SaveLeft),
                Pane("右側のファイルを保存(_R)", null, FrameCommand.SaveRight)),
            Popup("名前を付けて保存(_A)",
                Pane("名前を付けて左側のファイルを保存(_L)...", null, FrameCommand.SaveLeftAs),
                Pane("名前を付けて右側のファイルを保存(_R)...", null, FrameCommand.SaveRightAs)),
            Separator(),
            Item("比較を包装(_K)...", null, ShowPackagingDialogAsync),
            Separator(),
            Item("終了(_X)", "Ctrl+Q", () => { Close(); return Task.CompletedTask; })));
        menu.Items.Add(Popup("編集(_E)",
            Pane("元に戻す(_U)", "Ctrl+Z", FrameCommand.Undo),
            Pane("やり直し(_R)", "Ctrl+Y", FrameCommand.Redo),
            Separator(),
            Pane("検索(_I)...", "Ctrl+F", FrameCommand.Find),
            Separator(),
            Pane("設定(_O)...", "Ctrl+,", FrameCommand.Options)));
        var darkTheme = new MenuItem { Header = "ダーク テーマ(_D)", ToggleType = MenuItemToggleType.CheckBox };
        darkTheme.Click += (_, _) =>
        {
            var dark = Application.Current!.ActualThemeVariant != Avalonia.Styling.ThemeVariant.Dark;
            Application.Current.RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
            darkTheme.IsChecked = dark;
        };
        var toolbarVisible = new MenuItem { Header = "ツールバー(_T)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = true };
        toolbarVisible.Click += (_, _) => { if (FrameToolbar?.Parent is Control host) host.IsVisible = toolbarVisible.IsChecked = !host.IsVisible; };
        menu.Items.Add(Popup("表示(_V)",
            toolbarVisible,
            darkTheme,
            Separator(),
            Pane("最新の情報に更新", "F5", FrameCommand.Refresh)));
        menu.Items.Add(Popup("マージ(_M)",
            Pane("次の差異(_N)", "Alt+Down", FrameCommand.NextDifference),
            Pane("前の差異(_P)", "Alt+Up", FrameCommand.PreviousDifference),
            Pane("次のコンフリクト(_X)", "Alt+Shift+Down", FrameCommand.NextConflict),
            Pane("最初の差異(_F)", "Alt+Home", FrameCommand.FirstDifference),
            Pane("現在の差異(_C)", "Alt+Enter", FrameCommand.CurrentDifference),
            Pane("最後の差異(_L)", "Alt+End", FrameCommand.LastDifference),
            Separator(),
            Pane("右側にコピー(_R)", "Alt+Right", FrameCommand.CopyToRight),
            Pane("左側にコピー(_E)", "Alt+Left", FrameCommand.CopyToLeft),
            Pane("右側にコピーして次に進む(_O)", "Ctrl+Alt+Right", FrameCommand.CopyToRightAndAdvance),
            Pane("左側にコピーして次に進む(_T)", "Ctrl+Alt+Left", FrameCommand.CopyToLeftAndAdvance),
            Separator(),
            Pane("自動マージ(_U)", "Ctrl+Alt+M", FrameCommand.AutoMerge)));
        menu.Items.Add(Popup("ツール(_T)",
            Pane("パッチの生成(_G)...", null, FrameCommand.GeneratePatch),
            Pane("レポートの生成(_R)...", null, FrameCommand.GenerateReport),
            Pane("アーカイブの生成(_A)...", null, FrameCommand.GenerateArchive)));
        menu.Items.Add(Popup("プラグイン(_P)",
            Pane("外部ツールを追加(_L)...", null, FrameCommand.AddExternalTool)));
        menu.Items.Add(Popup("ウィンドウ(_W)",
            Item("閉じる(_O)", "Ctrl+W", CloseActiveSessionAsync),
            Item("すべて閉じる(_S)", null, CloseAllSessionsAsync)));
        menu.Items.Add(Popup("ヘルプ(_H)",
            Item("DiffBeacon について(_A)...", null, () => Dialogs.MessageAsync(this, "DiffBeacon について",
                "DiffBeacon\n\nWinMerge をもとにした .NET / Avalonia 版の比較・マージツールです。\nGNU General Public License v2 で配布しています。"))));
        return menu;
    }

    // 本家 g_toolbarFileCmp3 の並びと区切り（0）を写したもの。null は区切り。
    private Panel BuildToolbar()
    {
        var bar = new StackPanel { Name = "frame-toolbar", Orientation = Orientation.Horizontal, Margin = new Thickness(2, 1), Spacing = 0 };
        var layout = new (ToolbarImage Image, string Tip, Func<Task>? Action)?[]
        {
            (ToolbarImage.New, "新規 (Ctrl+N)", () => AddSessionFromUiAsync()),
            (ToolbarImage.Open, "開く (Ctrl+O)", () => { FocusPaths(); return Task.CompletedTask; }),
            (ToolbarImage.Save, "左側のファイルを保存", PaneAction(FrameCommand.SaveLeft)),
            null,
            (ToolbarImage.Undo, "元に戻す (Ctrl+Z)", PaneAction(FrameCommand.Undo)),
            (ToolbarImage.Redo, "やり直し (Ctrl+Y)", PaneAction(FrameCommand.Redo)),
            null,
            (ToolbarImage.SelectLineDiff, "行内差異を選択 (F4)", null),
            null,
            (ToolbarImage.NextDiff, "次の差異 (Alt+Down)", PaneAction(FrameCommand.NextDifference)),
            (ToolbarImage.PrevDiff, "前の差異 (Alt+Up)", PaneAction(FrameCommand.PreviousDifference)),
            null,
            (ToolbarImage.NextConflict, "次のコンフリクト (Alt+Shift+Down)", PaneAction(FrameCommand.NextConflict)),
            (ToolbarImage.PrevConflict, "前のコンフリクト (Alt+Shift+Up)", null),
            null,
            (ToolbarImage.FirstDiff, "最初の差異 (Alt+Home)", PaneAction(FrameCommand.FirstDifference)),
            (ToolbarImage.CurrentDiff, "現在の差異 (Alt+Enter)", PaneAction(FrameCommand.CurrentDifference)),
            (ToolbarImage.LastDiff, "最後の差異 (Alt+End)", PaneAction(FrameCommand.LastDifference)),
            null,
            (ToolbarImage.L2R, "右側にコピー (Alt+Right)", PaneAction(FrameCommand.CopyToRight)),
            (ToolbarImage.R2L, "左側にコピー (Alt+Left)", PaneAction(FrameCommand.CopyToLeft)),
            null,
            (ToolbarImage.L2RNext, "右側にコピーして次に進む (Ctrl+Alt+Right)", PaneAction(FrameCommand.CopyToRightAndAdvance)),
            (ToolbarImage.R2LNext, "左側にコピーして次に進む (Ctrl+Alt+Left)", PaneAction(FrameCommand.CopyToLeftAndAdvance)),
            null,
            (ToolbarImage.AllRight, "すべてを右側にコピー", null),
            (ToolbarImage.AllLeft, "すべてを左側にコピー", null),
            null,
            (ToolbarImage.AutoMerge, "自動マージ (Ctrl+Alt+M)", PaneAction(FrameCommand.AutoMerge)),
            null,
            (ToolbarImage.Options, "設定", PaneAction(FrameCommand.Options)),
            null,
            (ToolbarImage.Refresh, "最新の情報に更新 (F5)", PaneAction(FrameCommand.Refresh)),
        };
        foreach (var entry in layout)
        {
            if (entry is not { } button) { bar.Children.Add(ToolbarSeparator()); continue; }
            bar.Children.Add(ToolbarButton(button.Image, button.Tip, button.Action));
        }
        // 包装は本家に無い DiffBeacon 独自の機能。既存の自己検証が Name で探すため、ボタンとして右端に残す。
        bar.Children.Add(ToolbarSeparator());
        var package = PackagingButton();
        package.Margin = new Thickness(2, 0); package.Padding = new Thickness(6, 2); package.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(package);
        return bar;
    }

    private Button ToolbarButton(ToolbarImage image, string tip, Func<Task>? action)
    {
        var icon = new Image { Source = ToolbarIcon(image), Width = 16, Height = 16, Stretch = Stretch.Uniform };
        var button = new Button
        {
            Name = "frame-toolbar-" + image, Content = icon, Width = 24, Height = 24, Padding = new Thickness(3),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), IsEnabled = action is not null,
        };
        // 本家は未対応・使用不可のボタンを灰色で表示する。Avalonia の既定では画像が薄くならないので明示する。
        // Fluent の無効ボタンは背景が灰色で塗られるが、本家のフラットなツールバーはアイコンだけが薄くなる。
        if (action is null) icon.Opacity = .35;
        button.Resources["ButtonBackgroundDisabled"] = Brushes.Transparent;
        button.Resources["ButtonBorderBrushDisabled"] = Brushes.Transparent;
        ToolTip.SetTip(button, tip);
        if (action is not null) button.Click += async (_, _) => await action();
        return button;
    }

    /// <summary>
    /// 本家のMDIタブ（小さな文字の見出し）に合わせ、Fluent の大きなタブ見出しを詰める。比較タブと、ペイン内の表示切替タブの両方に使う。
    /// </summary>
    internal static void CompactTab(TabItem item)
    {
        item.FontSize = 13;
        item.MinHeight = 0;
        item.Padding = new Thickness(10, 3);
    }

    private static Control ToolbarSeparator() =>
        new Border { Width = 1, Height = 18, Margin = new Thickness(4, 3), Background = WinMergeColors.FrameBorder, VerticalAlignment = VerticalAlignment.Center };

    private CroppedBitmap ToolbarIcon(ToolbarImage image)
    {
        if (_toolbarStrip is null)
        {
            using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("DiffBeacon.Frame.ToolbarEnabled32.png")
                ?? throw new InvalidOperationException("ツールバー画像が埋め込まれていません。");
            _toolbarStrip = new Bitmap(stream);
        }
        return new CroppedBitmap(_toolbarStrip, new PixelRect((int)image * ToolbarImageSize, 0, ToolbarImageSize, ToolbarImageHeight));
    }

    private Func<Task> PaneAction(FrameCommand command) => () => ActivePane.ExecuteFrameCommandAsync(command);

    private static MenuItem Popup(string header, params Control[] items)
    {
        var menu = new MenuItem { Header = header };
        foreach (var item in items) menu.Items.Add(item);
        return menu;
    }

    private static Separator Separator() => new();

    private MenuItem Pane(string header, string? gesture, FrameCommand command) => Item(header, gesture, PaneAction(command));

    private static MenuItem Item(string header, string? gesture, Func<Task> action)
    {
        // InputGesture は表示用。実際のキー処理は HandleFrameShortcut で行い、編集欄の標準キーと二重に動かさない。
        var item = new MenuItem { Header = header, InputGesture = gesture is null ? null : KeyGesture.Parse(gesture) };
        item.Click += async (_, _) => await action();
        return item;
    }

    // 本家の「開く」は比較対象の選択画面を出す。ペイン上部の選択画面（ComparisonPane.OpenView.cs）を
    // スクロールで見える位置へ戻し、1 番目の欄へ移動する。
    internal void FocusPaths()
    {
        ActivePane.OpenView?.BringIntoView();
        ActivePane.LeftPath.Focus();
        ActivePane.LeftPath.SelectAll();
    }

    private Task CloseActiveSessionAsync() =>
        _tabs.SelectedItem is TabItem item ? CloseSessionAsync(item) : Task.CompletedTask;

    private async Task CloseAllSessionsAsync()
    {
        foreach (var item in _sessions.ToArray())
            if (!await CloseSessionAsync(item)) return;
    }

    /// <summary>
    /// 本家のアクセラレータ（Merge.rc の IDR_MERGEDOCTYPE / IDR_MAINFRAME）のうち、編集欄の標準操作と衝突しないものを処理する。
    /// Ctrl+Z / Ctrl+Y / Ctrl+F は編集欄やバイナリ検索が自前で扱うため、ここでは処理しない。
    /// </summary>
    private async Task<bool> HandleFrameShortcutAsync(KeyEventArgs e)
    {
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        FrameCommand? command = (e.Key, alt, control, shift) switch
        {
            (Key.Down, true, false, false) => FrameCommand.NextDifference,
            (Key.Up, true, false, false) => FrameCommand.PreviousDifference,
            (Key.Down, true, false, true) => FrameCommand.NextConflict,
            (Key.Home, true, false, false) => FrameCommand.FirstDifference,
            (Key.End, true, false, false) => FrameCommand.LastDifference,
            (Key.Enter, true, false, false) => FrameCommand.CurrentDifference,
            (Key.Right, true, false, false) => FrameCommand.CopyToRight,
            (Key.Left, true, false, false) => FrameCommand.CopyToLeft,
            (Key.Right, true, true, false) => FrameCommand.CopyToRightAndAdvance,
            (Key.Left, true, true, false) => FrameCommand.CopyToLeftAndAdvance,
            (Key.M, true, true, false) => FrameCommand.AutoMerge,
            (Key.F5, false, false, false) => FrameCommand.Refresh,
            (Key.J, false, true, false) => FrameCommand.OpenProject,
            _ => null,
        };
        if (command is { } paneCommand)
        {
            e.Handled = true;
            await ActivePane.ExecuteFrameCommandAsync(paneCommand);
            return true;
        }
        if (control && !alt && !shift && e.Key == Key.O) { e.Handled = true; FocusPaths(); return true; }
        if (control && !alt && !shift && e.Key == Key.W) { e.Handled = true; await CloseActiveSessionAsync(); return true; }
        if (control && !alt && !shift && e.Key == Key.Q) { e.Handled = true; Close(); return true; }
        return false;
    }
}
