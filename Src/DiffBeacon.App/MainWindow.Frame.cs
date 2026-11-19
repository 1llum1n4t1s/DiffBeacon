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
        // 保存したツールバーの表示は、帯（Border）を作った後に採用する（メニューを作る時点では帯がまだ無い）。
        SetToolbarVisible(_showToolbar, save: false);
        SetMenuBarVisible(_showMenuBar, save: false);
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
                Pane("右側のファイルを保存(_R)", null, FrameCommand.SaveRight),
                // マージ結果の保存は本家に無い DiffBeacon 独自の操作。ペイン内のボタンからメニューへ移した。
                Pane("マージ結果を保存(_M)", null, FrameCommand.SaveMergeResult)),
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
            Pane("切り取り(_T)", "Ctrl+X", FrameCommand.Cut),
            Pane("コピー(_C)", "Ctrl+C", FrameCommand.Copy),
            Pane("貼り付け(_P)", "Ctrl+V", FrameCommand.Paste),
            Separator(),
            Pane("行内差異を選択(_D)", "F4", FrameCommand.SelectLineDifference),
            Pane("すべて選択(_A)", "Ctrl+A", FrameCommand.SelectAll),
            Separator(),
            Pane("検索(_I)...", "Ctrl+F", FrameCommand.Find),
            Pane("置換(_C)...", "Ctrl+H", FrameCommand.Replace),
            Separator(),
            Popup("ブックマーク(_B)",
                Pane("ブックマークの作成・削除(_T)", "Ctrl+F2", FrameCommand.ToggleBookmark),
                Pane("次のブックマークへ移動(_N)", "F2", FrameCommand.NextBookmark),
                Pane("前のブックマークへ移動(_P)", "Shift+F2", FrameCommand.PreviousBookmark),
                Pane("すべてのブックマークをクリア(_C)", null, FrameCommand.ClearAllBookmarks)),
            Pane("移動(_G)...", "Ctrl+G", FrameCommand.GoTo),
            Separator(),
            Pane("設定(_O)...", "Ctrl+,", FrameCommand.Options)));
        var darkTheme = new MenuItem { Header = "ダーク テーマ(_D)", ToggleType = MenuItemToggleType.CheckBox };
        darkTheme.Click += (_, _) =>
        {
            var dark = Application.Current!.ActualThemeVariant != Avalonia.Styling.ThemeVariant.Dark;
            Application.Current.RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
            darkTheme.IsChecked = dark;
        };
        var toolbarVisible = _toolbarMenuItem = new MenuItem { Name = "frame-menu-view-toolbar", Header = "ツールバー(_T)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _showToolbar };
        toolbarVisible.Click += (_, _) => SetToolbarVisible(!_showToolbar, save: true);
        var statusVisible = ChromeItem("ステータス バー(&S)", "view-status", () => _showStatusBar, value => { _showStatusBar = value; ApplyChrome(); });
        var diffPaneVisible = ChromeItem("Diff ペイン(&P)", "view-diff-pane", () => _showDiffPane, value => { _showDiffPane = value; ApplyChrome(); });
        var locationVisible = ChromeItem("ロケーション ペイン(&C)", "view-location", () => _showLocationPane, value => { _showLocationPane = value; ApplyChrome(); });
        var tabBarVisible = ChromeItem("タブ バーを表示(&V)", "view-tab-bar", () => _showTabBar, value => SetTabBarVisible(value, save: true));
        var menuBarVisible = ChromeItem("メニューバー(&U)", "view-menu-bar", () => _showMenuBar, value => SetMenuBarVisible(value, save: true));
        // 本家 IDR_MERGEDOCTYPE の「表示」のうち、本文の見た目の設定（DiffTextViewOptions）。全タブ共通で、チェックは設定から付ける。
        var options = TextViewOptions;
        menu.Items.Add(Popup("表示(_V)",
            Popup("拡大/縮小(_Z)",
                GestureItem("拡大(_I)", new KeyGesture(Key.OemPlus, KeyModifiers.Control), () => Zoom(1)),
                GestureItem("縮小(_O)", new KeyGesture(Key.OemMinus, KeyModifiers.Control), () => Zoom(-1)),
                Separator(),
                GestureItem("標準に戻す(_N)", new KeyGesture(Key.Multiply, KeyModifiers.Control), () => Zoom(0))),
            Separator(),
            ViewOption("空白を表示(_V)", "view-whitespace", () => options.ViewWhitespace, value => options.ViewWhitespace = value),
            ViewOption("改行を表示(_O)", "view-eol", () => options.ViewEndOfLine, value => options.ViewEndOfLine = value),
            ViewOption("行内差異を表示(_W)", "view-line-differences", () => options.ViewLineDifferences, value => options.ViewLineDifferences = value),
            ViewOption("行番号を表示(_N)", "view-line-numbers", () => options.ViewLineNumbers, value => options.ViewLineNumbers = value),
            ViewOption("マージンを表示(_M)", "view-margins", () => options.ViewMargins, value => options.ViewMargins = value),
            ViewOption("行を右端で折返す(&R)", "view-wrap", () => options.WordWrap, value => options.WordWrap = value),
            Popup("フォント(_F)",
                Item("フォントの選択(&F)...", null, ChooseFontAsync),
                Item("デフォルト フォントを使用", null, () => { options.FontFamily = ""; return Task.CompletedTask; })),
            ViewOption("シンタックス ハイライト", "view-syntax", () => options.SyntaxHighlight, value => options.SyntaxHighlight = value),
            BuildDiffContextMenu(options),
            LockPanesItem(options),
            ViewOption("垂直分割(&E)", "view-split", () => options.VerticalSplit, value => options.VerticalSplit = value),
            tabBarVisible,
            menuBarVisible,
            Separator(),
            toolbarVisible,
            statusVisible,
            diffPaneVisible,
            locationVisible,
            Popup("ペインの交換(_W)",
                Item("1 番目と 2 番目を交換", null, () => ActivePane.SwapInputsAsync(ActivePane.LeftPath, ActivePane.RightPath)),
                Item("2 番目と 3 番目を交換", null, () => ActivePane.SwapInputsAsync(ActivePane.RightPath, ActivePane.BasePath)),
                Item("1 番目と 3 番目を交換", null, () => ActivePane.SwapInputsAsync(ActivePane.LeftPath, ActivePane.BasePath))),
            darkTheme,
            Separator(),
            Pane("最新の情報に更新", "F5", FrameCommand.Refresh)));
        menu.Items.Add(Popup("マージ(_M)",
            Pane("次の差異(_N)", "Alt+Down", FrameCommand.NextDifference),
            Pane("前の差異(_P)", "Alt+Up", FrameCommand.PreviousDifference),
            Pane("次のコンフリクト(_X)", "Alt+Shift+Down", FrameCommand.NextConflict),
            Pane("前のコンフリクト(_V)", "Alt+Shift+Up", FrameCommand.PreviousConflict),
            Pane("最初の差異(_F)", "Alt+Home", FrameCommand.FirstDifference),
            Pane("現在の差異(_C)", "Alt+Enter", FrameCommand.CurrentDifference),
            Pane("最後の差異(_L)", "Alt+End", FrameCommand.LastDifference),
            Separator(),
            Pane("右側にコピー(_R)", "Alt+Right", FrameCommand.CopyToRight),
            Pane("左側にコピー(_E)", "Alt+Left", FrameCommand.CopyToLeft),
            Pane("右側にコピーして次に進む(_O)", "Ctrl+Alt+Right", FrameCommand.CopyToRightAndAdvance),
            Pane("左側にコピーして次に進む(_T)", "Ctrl+Alt+Left", FrameCommand.CopyToLeftAndAdvance),
            Separator(),
            Pane("すべてを右側にコピー(_A)", null, FrameCommand.CopyAllToRight),
            Pane("すべてを左側にコピー(_Y)", null, FrameCommand.CopyAllToLeft),
            Separator(),
            Pane("自動マージ(_U)", "Ctrl+Alt+M", FrameCommand.AutoMerge)));
        menu.Items.Add(Popup("ツール(_T)",
            // 本家はフォルダー比較の「ツール」メニューの先頭に置く（IDR_DIRDOCTYPE）。ファイル比較の画面では状態欄へ使えない理由を出す。
            Pane("列の設定(_C)...", null, FrameCommand.CustomizeColumns),
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
            (ToolbarImage.SelectLineDiff, "行内差異を選択 (F4)", PaneAction(FrameCommand.SelectLineDifference)),
            null,
            (ToolbarImage.NextDiff, "次の差異 (Alt+Down)", PaneAction(FrameCommand.NextDifference)),
            (ToolbarImage.PrevDiff, "前の差異 (Alt+Up)", PaneAction(FrameCommand.PreviousDifference)),
            null,
            (ToolbarImage.NextConflict, "次のコンフリクト (Alt+Shift+Down)", PaneAction(FrameCommand.NextConflict)),
            (ToolbarImage.PrevConflict, "前のコンフリクト (Alt+Shift+Up)", PaneAction(FrameCommand.PreviousConflict)),
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
            (ToolbarImage.AllRight, "すべてを右側にコピー", PaneAction(FrameCommand.CopyAllToRight)),
            (ToolbarImage.AllLeft, "すべてを左側にコピー", PaneAction(FrameCommand.CopyAllToLeft)),
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

    private static MenuItem Item(string header, string? gesture, Func<Task> action) =>
        GestureItem(header, gesture is null ? null : KeyGesture.Parse(gesture), action);

    private static MenuItem GestureItem(string header, KeyGesture? gesture, Func<Task> action)
    {
        // InputGesture は表示用。実際のキー処理は HandleFrameShortcut で行い、編集欄の標準キーと二重に動かさない。
        var item = new MenuItem { Header = header, InputGesture = gesture };
        item.Click += async (_, _) => await action();
        return item;
    }

    /// <summary>本家の「表示」の本文の設定（DiffTextViewOptions）。全タブで一つなので、どのタブから切り替えてもチェックを設定に合わせる。</summary>
    internal DiffTextViewOptions TextViewOptions { get; } = new();

    /// <summary>表示メニューの設定の保存先（view-options.json）。省略したウィンドウはメモリだけ（headless検証が利用者のAppDataへ書かないため）。</summary>
    internal TextViewOptionsStore TextViewOptionsStorage { get; private set; } = new();

    // ツールバーの表示（本家 Settings/ShowToolbar）。本文の設定ではないので DiffTextViewOptions に入れず、ペインを描き直さない。
    private bool _showToolbar = true;
    private bool _showStatusBar = true;
    private bool _showLocationPane = true;
    private bool _showDiffPane = true;
    private bool _showTabBar = true;
    private bool _showMenuBar = true;
    private MenuItem? _toolbarMenuItem;

    /// <summary>自己検証用: メニューバーが表示されているか。</summary>
    internal bool FrameMenuVisible => FrameMenu?.IsVisible != false;
    /// <summary>自己検証用: 比較タブの見出しが表示されているか。テンプレート適用前は保存した値。</summary>
    internal bool FrameTabBarVisible => SessionHeaders is { } headers ? headers.IsVisible : _showTabBar;

    /// <summary>自己検証用: ツールバーの帯が表示されているか。</summary>
    internal bool FrameToolbarVisible => FrameToolbar?.Parent is Control { IsVisible: true };

    private void SetToolbarVisible(bool visible, bool save)
    {
        _showToolbar = visible;
        if (FrameToolbar?.Parent is Control host) host.IsVisible = visible;
        if (_toolbarMenuItem is not null) _toolbarMenuItem.IsChecked = visible;
        if (save) SaveViewMenuOptions();
    }

    /// <summary>比較タブの見出しを出し入れする。テンプレート適用前は値だけ覚え、適用時に帯へ写す。</summary>
    private void SetTabBarVisible(bool visible, bool save)
    {
        _showTabBar = visible;
        if (SessionHeaders is not null) SessionHeaders.IsVisible = visible;
        if (save) SaveViewMenuOptions();
    }

    /// <summary>メニューバーを出し入れする。隠したあとは Alt または F10 で戻す。</summary>
    private void SetMenuBarVisible(bool visible, bool save)
    {
        _showMenuBar = visible;
        if (FrameMenu is not null) FrameMenu.IsVisible = visible;
        if (save) SaveViewMenuOptions();
    }

    /// <summary>本家の Diff コンテキスト。全行、0/1/3/5/7/9 行、Ctrl+D の切替、反転。</summary>
    private MenuItem BuildDiffContextMenu(DiffTextViewOptions options)
    {
        var all = ContextItem("全行(&A)", "view-context-all", options, () => options.DiffContextLines < 0, () => options.SetDiffContext(-1));
        var numbers = DiffTextViewOptions.DiffContextChoices.Select(lines =>
            ContextItem(lines == 1 ? "&1 行" : $"&{lines} 行", "view-context-" + lines, options, () => options.DiffContextLines == lines, () => options.SetDiffContext(lines))).ToArray();
        var toggle = GestureItem("全行/0 ～ 9 行切替(&T)", new KeyGesture(Key.D, KeyModifiers.Control), () => { options.ToggleDiffContext(); return Task.CompletedTask; });
        toggle.Name = "frame-menu-view-context-toggle";
        var invert = ViewOption("反転 (差異行を非表示にする)(&I)", "view-context-invert", () => options.DiffContextInvert, value => options.DiffContextInvert = value);
        return Popup("Diff コンテキスト(&D)", [all, .. numbers, toggle, invert]);
    }

    /// <summary>コンテキストの行数。選ばれている項目だけチェックを付ける。</summary>
    private MenuItem ContextItem(string header, string name, DiffTextViewOptions options, Func<bool> selected, Action choose)
    {
        var item = new MenuItem { Header = header, Name = "frame-menu-" + name, ToggleType = MenuItemToggleType.CheckBox, IsChecked = selected() };
        item.Click += (_, _) => choose();
        options.Changed += () => item.IsChecked = selected();
        return item;
    }

    /// <summary>フォーカスのある側のスクロールを、他のペインと同期しない。</summary>
    private MenuItem LockPanesItem(DiffTextViewOptions options)
    {
        var item = new MenuItem { Header = "ペインをロック(&L)", Name = "frame-menu-view-lock", ToggleType = MenuItemToggleType.CheckBox, IsChecked = options.LockedSide >= 0 };
        item.Click += (_, _) =>
        {
            var side = ActivePane.ActiveDiffSide;
            options.LockedSide = options.LockedSide == side ? -1 : side;
        };
        options.Changed += () => item.IsChecked = options.LockedSide >= 0;
        return item;
    }

    /// <summary>インストール済みフォントから本文フォントを選ぶ。空のまま確定すると既定の等幅に戻る。</summary>
    private async Task ChooseFontAsync()
    {
        var box = new TextBox { Text = TextViewOptions.FontFamily, PlaceholderText = DiffTextViewOptions.DefaultFontFamilyName };
        var list = new ListBox { MaxHeight = 280 };
        foreach (var font in FontManager.Current.SystemFonts.Select(font => font.Name).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
            list.Items.Add(font);
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is string name) box.Text = name; };
        string? chosen = null;
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(new StackPanel { Spacing = 8, Children = { box, list } });
        var dialog = new Window
        {
            Title = "フォントの選択", Width = 420, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel,
        };
        ok.Click += (_, _) => { chosen = box.Text; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        if (chosen is not null) TextViewOptions.FontFamily = chosen;
    }

    // 本家と同じく、表示メニューの設定は変えるたびに保存する。失敗しても表示はそのまま使い、理由を状態欄へ出す。
    private void SaveViewMenuOptions()
    {
        if (TextViewOptionsStorage.Save(TextViewOptions.ToSettings(_showToolbar, _showStatusBar, _showLocationPane, _showDiffPane, _showTabBar, _showMenuBar))) return;
        if (_sessions.Count > 0) ActivePane.ShowViewOptionsStatus(TextViewOptionsStorage.Diagnostic + " 表示はこの起動の間だけ変えています。");
    }

    /// <summary>開いている比較タブ全部へ、枠の表示切替を渡す。</summary>
    private void ApplyChrome()
    {
        foreach (var pane in SessionPanes) pane.ApplyViewChrome(_showStatusBar, _showLocationPane, _showDiffPane);
        SaveViewMenuOptions();
    }

    /// <summary>枠の表示切替。本文の設定とは別に、変えたら保存する。</summary>
    private MenuItem ChromeItem(string header, string name, Func<bool> get, Action<bool> set)
    {
        var item = new MenuItem { Header = header, Name = "frame-menu-" + name, ToggleType = MenuItemToggleType.CheckBox, IsChecked = get() };
        item.Click += (_, _) => set(!get());
        return item;
    }

    private MenuItem ViewOption(string header, string name, Func<bool> get, Action<bool> set)
    {
        var item = new MenuItem { Header = header, Name = "frame-menu-" + name, ToggleType = MenuItemToggleType.CheckBox, IsChecked = get() };
        item.Click += (_, _) => set(!get());
        TextViewOptions.Changed += () => item.IsChecked = get();
        return item;
    }

    // ID_VIEW_ZOOMIN／ID_VIEW_ZOOMOUT／ID_VIEW_ZOOMNORMAL。本家と同じく全ペインの本文の文字の大きさを変える。
    private Task Zoom(int amount) { TextViewOptions.Zoom(amount); return Task.CompletedTask; }

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
    /// Ctrl+Z / Ctrl+Y は編集欄が自前で扱うため、ここでは処理しない。
    /// 検索・置換・移動（Ctrl+F／Ctrl+H／Ctrl+G／F3）は、差分タブを表示していてフォーカスが通常の入力欄に無いときだけ受ける
    /// （<see cref="ComparisonPane.AcceptsTextSearchKeys"/>）。バイナリの比較など形式別ビューの Ctrl+F／F3 はそのビュー自身が扱う。
    /// エディター（AvaloniaEdit）の組込みの検索欄より先にトンネル段階で受けるので、二つの検索が競合しない。
    /// </summary>
    private async Task<bool> HandleFrameShortcutAsync(KeyEventArgs e)
    {
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var textSearch = ActivePane.AcceptsTextSearchKeys(FocusManager?.GetFocusedElement());
        // メニューバーを隠したあとは、Alt または F10 だけで戻す。Alt+矢印などの操作はそのまま使う。
        if (!_showMenuBar && e.Key is Key.LeftAlt or Key.RightAlt or Key.F10 && !control && !shift)
        {
            e.Handled = true;
            SetMenuBarVisible(true, save: true);
            return true;
        }
        if (e.Key == Key.D && control && !alt && !shift && textSearch)
        {
            e.Handled = true;
            TextViewOptions.ToggleDiffContext();
            return true;
        }
        // 拡大／縮小（Ctrl++／Ctrl+-／Ctrl+*・Ctrl+0、本家 IDR_MERGEDOCTYPE のアクセラレータ）はペインではなく全タブ共通の設定を変える。
        // 検索と同じく差分タブを表示していて通常の入力欄にフォーカスが無いときだけ受ける（画像・バイナリなどの形式別ビューのキーを奪わない）。
        int? zoom = (e.Key, alt, control, shift) switch
        {
            (Key.OemPlus or Key.Add, false, true, false) => 1,
            (Key.OemMinus or Key.Subtract, false, true, false) => -1,
            (Key.Multiply or Key.D0, false, true, false) => 0,
            _ => null,
        };
        if (zoom is { } amount && textSearch) { e.Handled = true; TextViewOptions.Zoom(amount); return true; }
        FrameCommand? command = (e.Key, alt, control, shift) switch
        {
            (Key.F, false, true, false) when textSearch => FrameCommand.Find,
            (Key.H, false, true, false) when textSearch => FrameCommand.Replace,
            (Key.G, false, true, false) when textSearch => FrameCommand.GoTo,
            (Key.F3, false, false, false) when textSearch => FrameCommand.FindNext,
            (Key.F3, false, false, true) when textSearch => FrameCommand.FindPrevious,
            (Key.F3, false, true, false) when textSearch => FrameCommand.FindSelectedNext,
            (Key.F3, false, true, true) when textSearch => FrameCommand.FindSelectedPrevious,
            (Key.F2, false, true, false) when textSearch => FrameCommand.ToggleBookmark,
            (Key.F2, false, false, false) when textSearch => FrameCommand.NextBookmark,
            (Key.F2, false, false, true) when textSearch => FrameCommand.PreviousBookmark,
            (Key.Down, true, false, false) => FrameCommand.NextDifference,
            (Key.Up, true, false, false) => FrameCommand.PreviousDifference,
            (Key.Down, true, false, true) => FrameCommand.NextConflict,
            (Key.Up, true, false, true) => FrameCommand.PreviousConflict,
            (Key.F4, false, false, false) => FrameCommand.SelectLineDifference,
            (Key.F4, false, false, true) => FrameCommand.SelectPreviousLineDifference,
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
