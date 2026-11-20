using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

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
        menu.Items.Add(MenuPopup("&File",
            Labeled("&New", "Ctrl+N", () => AddSessionFromUiAsync()),
            Labeled("&Open...\tCtrl+O", "Ctrl+O", () => { FocusPaths(); return Task.CompletedTask; }),
            Separator(),
            Labeled("Open Pro&ject...\tCtrl+J", "Ctrl+J", PaneAction(FrameCommand.OpenProject)),
            Labeled("Sa&ve Project...", null, PaneAction(FrameCommand.SaveProject)),
            Separator(),
            MenuPopup("Sav&e",
                Labeled("Save &Left", null, PaneAction(FrameCommand.SaveLeft)),
                Labeled("Save &Right", null, PaneAction(FrameCommand.SaveRight)),
                // マージ結果の保存は本家に無い DiffBeacon 独自の操作。ペイン内のボタンからメニューへ移した。
                Pane("マージ結果を保存(_M)", null, FrameCommand.SaveMergeResult)),
            MenuPopup("Save &As",
                Labeled("Save &Left As...", null, PaneAction(FrameCommand.SaveLeftAs)),
                Labeled("Save &Right As...", null, PaneAction(FrameCommand.SaveRightAs))),
            Separator(),
            Item("比較を包装(_K)...", null, ShowPackagingDialogAsync),
            Separator(),
            Labeled("E&xit\tCtrl+Q", "Ctrl+Q", () => { Close(); return Task.CompletedTask; })));
        menu.Items.Add(MenuPopup("&Edit",
            Labeled("&Undo\tCtrl+Z", "Ctrl+Z", PaneAction(FrameCommand.Undo)),
            Labeled("&Redo\tCtrl+Y", "Ctrl+Y", PaneAction(FrameCommand.Redo)),
            Separator(),
            Labeled("Cu&t\tCtrl+X", "Ctrl+X", PaneAction(FrameCommand.Cut)),
            Labeled("&Copy\tCtrl+C", "Ctrl+C", PaneAction(FrameCommand.Copy)),
            Labeled("&Paste\tCtrl+V", "Ctrl+V", PaneAction(FrameCommand.Paste)),
            Separator(),
            Labeled("Select Line &Difference\tF4", "F4", PaneAction(FrameCommand.SelectLineDifference)),
            Labeled("Select &All\tCtrl+A", "Ctrl+A", PaneAction(FrameCommand.SelectAll)),
            Separator(),
            Labeled("F&ind...\tCtrl+F", "Ctrl+F", PaneAction(FrameCommand.Find)),
            Labeled("Repla&ce...\tCtrl+H", "Ctrl+H", PaneAction(FrameCommand.Replace)),
            Separator(),
            MenuPopup("&Bookmarks",
                Labeled("&Toggle Bookmark\tCtrl+F2", "Ctrl+F2", PaneAction(FrameCommand.ToggleBookmark)),
                Labeled("&Next Bookmark\tF2", "F2", PaneAction(FrameCommand.NextBookmark)),
                Labeled("&Previous Bookmark\tShift+F2", "Shift+F2", PaneAction(FrameCommand.PreviousBookmark)),
                Labeled("&Clear All Bookmarks", null, PaneAction(FrameCommand.ClearAllBookmarks))),
            Labeled("&Go to...\tCtrl+G", "Ctrl+G", PaneAction(FrameCommand.GoTo)),
            Separator(),
            Labeled("&Options...\tCtrl+,", "Ctrl+,", PaneAction(FrameCommand.Options))));
        var darkTheme = new MenuItem { Header = "ダーク テーマ(_D)", ToggleType = MenuItemToggleType.CheckBox };
        darkTheme.Click += (_, _) =>
        {
            var dark = Application.Current!.ActualThemeVariant != Avalonia.Styling.ThemeVariant.Dark;
            Application.Current.RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
            darkTheme.IsChecked = dark;
        };
        var toolbarVisible = _toolbarMenuItem = new MenuItem { Name = "frame-menu-view-toolbar", Header = UiText.Menu("&Toolbar"), Tag = "&Toolbar", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _showToolbar };
        toolbarVisible.Click += (_, _) => SetToolbarVisible(!_showToolbar, save: true);
        var statusVisible = ChromeItem("&Status Bar", "view-status", () => _showStatusBar, value => { _showStatusBar = value; ApplyChrome(); });
        var diffPaneVisible = ChromeItem("Diff &Pane", "view-diff-pane", () => _showDiffPane, value => { _showDiffPane = value; ApplyChrome(); });
        var locationVisible = ChromeItem("Lo&cation Pane", "view-location", () => _showLocationPane, value => { _showLocationPane = value; ApplyChrome(); });
        var tabBarVisible = ChromeItem("&View Tab Bar", "view-tab-bar", () => _showTabBar, value => SetTabBarVisible(value, save: true));
        var menuBarVisible = ChromeItem("Men&u Bar", "view-menu-bar", () => _showMenuBar, value => SetMenuBarVisible(value, save: true));
        // 本家 IDR_MERGEDOCTYPE の「表示」のうち、本文の見た目の設定（DiffTextViewOptions）。全タブ共通で、チェックは設定から付ける。
        var options = TextViewOptions;
        menu.Items.Add(MenuPopup("&View",
            MenuPopup("&Zoom",
                Labeled("Zoom &In\tCtrl++", null, () => Zoom(1), new KeyGesture(Key.OemPlus, KeyModifiers.Control)),
                Labeled("Zoom &Out\tCtrl+-", null, () => Zoom(-1), new KeyGesture(Key.OemMinus, KeyModifiers.Control)),
                Separator(),
                Labeled("&Normal\tCtrl+*", null, () => Zoom(0), new KeyGesture(Key.Multiply, KeyModifiers.Control))),
            Separator(),
            ViewOption("&View Whitespace", "view-whitespace", () => options.ViewWhitespace, value => options.ViewWhitespace = value),
            ViewOption("View E&OL", "view-eol", () => options.ViewEndOfLine, value => options.ViewEndOfLine = value),
            ViewOption("Vie&w Line Differences", "view-line-differences", () => options.ViewLineDifferences, value => options.ViewLineDifferences = value),
            ViewOption("View Line &Numbers", "view-line-numbers", () => options.ViewLineNumbers, value => options.ViewLineNumbers = value),
            ViewOption("View &Margins", "view-margins", () => options.ViewMargins, value => options.ViewMargins = value),
            ViewOption("View To&p Margins", "view-top-margin", () => options.ViewTopMargin, value => options.ViewTopMargin = value),
            ViewOption("W&rap Lines", "view-wrap", () => options.WordWrap, value => options.WordWrap = value),
            MenuPopup("Font",
                Labeled("Select &Font...", null, ChooseFontAsync),
                Labeled("Use Default Font", null, () => { options.FontFamily = ""; return Task.CompletedTask; })),
            ViewOption("Syntax Highlight", "view-syntax", () => options.SyntaxHighlight, value => options.SyntaxHighlight = value),
            BuildDiffContextMenu(options),
            LockPanesItem(options),
            ViewOption("Split V&ertically", "view-split", () => options.VerticalSplit, value => options.VerticalSplit = value),
            tabBarVisible,
            menuBarVisible,
            Separator(),
            toolbarVisible,
            statusVisible,
            diffPaneVisible,
            locationVisible,
            MenuPopup("Sw&ap Panes",
                Labeled("Swap &1st | 2nd", null, () => ActivePane.SwapInputsAsync(ActivePane.LeftPath, ActivePane.RightPath)),
                Labeled("Swap &2nd | 3rd", null, () => ActivePane.SwapInputsAsync(ActivePane.RightPath, ActivePane.BasePath)),
                Labeled("Swap 1st | &3rd", null, () => ActivePane.SwapInputsAsync(ActivePane.LeftPath, ActivePane.BasePath))),
            darkTheme,
            LanguageMenu(),
            Separator(),
            Labeled("Refresh\tF5", "F5", PaneAction(FrameCommand.Refresh))));
        menu.Items.Add(MenuPopup("&Merge",
            Labeled("&Next Difference\tAlt+Down", "Alt+Down", PaneAction(FrameCommand.NextDifference)),
            Labeled("&Previous Difference\tAlt+Up", "Alt+Up", PaneAction(FrameCommand.PreviousDifference)),
            Labeled("Ne&xt Conflict\tAlt+Shift+Down", "Alt+Shift+Down", PaneAction(FrameCommand.NextConflict)),
            Labeled("Pre&vious Conflict\tAlt+Shift+Up", "Alt+Shift+Up", PaneAction(FrameCommand.PreviousConflict)),
            Labeled("&First Difference\tAlt+Home", "Alt+Home", PaneAction(FrameCommand.FirstDifference)),
            Labeled("&Current Difference\tAlt+Enter", "Alt+Enter", PaneAction(FrameCommand.CurrentDifference)),
            Labeled("&Last Difference\tAlt+End", "Alt+End", PaneAction(FrameCommand.LastDifference)),
            Separator(),
            Labeled("Copy to &Right\tAlt+Right", "Alt+Right", PaneAction(FrameCommand.CopyToRight)),
            Labeled("Copy to L&eft\tAlt+Left", "Alt+Left", PaneAction(FrameCommand.CopyToLeft)),
            Labeled("C&opy to Right and Advance\tCtrl+Alt+Right", "Ctrl+Alt+Right", PaneAction(FrameCommand.CopyToRightAndAdvance)),
            Labeled("Copy &to Left and Advance\tCtrl+Alt+Left", "Ctrl+Alt+Left", PaneAction(FrameCommand.CopyToLeftAndAdvance)),
            Separator(),
            Labeled("Copy &All to Right", null, PaneAction(FrameCommand.CopyAllToRight)),
            Labeled("Cop&y All to Left", null, PaneAction(FrameCommand.CopyAllToLeft)),
            Separator(),
            Labeled("A&uto Merge\tCtrl+Alt+M", "Ctrl+Alt+M", PaneAction(FrameCommand.AutoMerge))));
        menu.Items.Add(MenuPopup("&Tools",
            // 本家はフォルダー比較の「ツール」メニューの先頭に置く（IDR_DIRDOCTYPE）。ファイル比較の画面では状態欄へ使えない理由を出す。
            Labeled("&Customize Columns...", null, PaneAction(FrameCommand.CustomizeColumns)),
            Labeled("&Generate Patch...", null, PaneAction(FrameCommand.GeneratePatch)),
            Labeled("Generate &Report...", null, PaneAction(FrameCommand.GenerateReport)),
            Labeled("Generate &Archive...", null, PaneAction(FrameCommand.GenerateArchive))));
        menu.Items.Add(MenuPopup("&Plugins",
            Pane("外部ツールを追加(_L)...", null, FrameCommand.AddExternalTool)));
        menu.Items.Add(MenuPopup("&Window",
            Labeled("&Close", "Ctrl+W", CloseActiveSessionAsync),
            Item("すべて閉じる(_S)", null, CloseAllSessionsAsync)));
        menu.Items.Add(MenuPopup("&Help",
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
        // 包装は本家のツールバーに無い。自己検証が Name で探すので子としては残し、帯の見た目には出さない。メニューの「比較を包装」から開く。
        var package = PackagingButton();
        package.IsVisible = false;
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

    /// <summary>タブの文字。Windows は Yu Gothic UI、Mac はヒラギノ。無い環境は Noto Sans CJK JP に落ちる。</summary>
    internal static readonly FontFamily TabFont = new("Yu Gothic UI, Yu Gothic, Meiryo, Hiragino Sans, Hiragino Kaku Gothic ProN, Noto Sans CJK JP, WenQuanYi Micro Hei, sans-serif");

    /// <summary>
    /// 本家のMDIタブ（小さな文字の見出し）に合わせ、Fluent の大きなタブ見出しを詰める。比較タブと、ペイン内の表示切替タブの両方に使う。
    /// Fluent の選択下線（PART_SelectedPipe）は見出しの下ではなく見出しの上に重なるので、テーマごと外す。
    /// </summary>
    internal static void CompactTab(TabItem item)
    {
        item.FontFamily = TabFont;
        item.FontSize = 12;
        item.Padding = new Thickness(8, 4);
        item.MinHeight = 28;
        item.VerticalContentAlignment = VerticalAlignment.Center;
        ApplySessionTabChrome(item);
    }

    /// <summary>
    /// 比較タブの見出し。本家 <c>CMyTabCtrl::DrawItem</c>（MDITabBar.cpp）と同じく、
    /// 種類のアイコン、ファイル名（同じ名前は「名前 x N」）、閉じるボタンを横に並べる。
    /// 閉じるボタンはポインタが乗ったタブと選択中のタブに出す（本家は乗っているタブだけを描く）。
    /// </summary>
    internal TabItem CreateSessionTab(ComparisonPane pane, string title, ComparisonProject project)
    {
        var titleText = new TextBlock
        {
            Text = title, MaxWidth = 280, FontFamily = TabFont, FontSize = 12, Foreground = Brushes.Black,
            TextDecorations = null, TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Button
        {
            Content = "×", Width = 16, Height = 16, Padding = new Thickness(0), FontSize = 11,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(SessionKindIcon(project));
        header.Children.Add(titleText);
        header.Children.Add(close);
        var item = new TabItem
        {
            Header = header, Content = pane, FontFamily = TabFont, FontSize = 12,
            MinHeight = 28, Padding = new Thickness(0), Foreground = Brushes.Black,
            VerticalContentAlignment = VerticalAlignment.Center, FocusAdorner = null,
        };
        close.Click += async (_, _) => await CloseSessionAsync(item);
        ApplySessionTabChrome(item);
        return item;
    }

    /// <summary>
    /// Fluent の TabItem テーマを外す。既定テンプレートの PART_SelectedPipe は高さ 2 の下線で、
    /// MinHeight を詰めると文字（特に游ゴシックの下端）に重なる。下線の部品は置かない。
    /// </summary>
    private static void ApplySessionTabChrome(TabItem item)
    {
        item.Theme = SessionTabTheme;
        item.Styles.Add(new Style(selector => selector.OfType<Control>().Name("PART_SelectedPipe"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, false) },
        });
    }

    private static readonly ControlTheme SessionTabTheme = new(typeof(TabItem))
    {
        Setters =
        {
            new Setter(TabItem.FontFamilyProperty, TabFont),
            new Setter(TabItem.FontSizeProperty, 12d),
            new Setter(TabItem.ForegroundProperty, Brushes.Black),
            new Setter(TabItem.BackgroundProperty, Brushes.Transparent),
            new Setter(TabItem.BorderThicknessProperty, new Thickness(0)),
            new Setter(TabItem.PaddingProperty, new Thickness(0)),
            new Setter(TabItem.MinHeightProperty, 28d),
            new Setter(TabItem.VerticalContentAlignmentProperty, VerticalAlignment.Center),
            new Setter(TabItem.FocusAdornerProperty, null),
            new Setter(TabItem.TemplateProperty, new FuncControlTemplate<TabItem>((tab, _) =>
            {
                var presenter = new ContentPresenter
                {
                    Name = "PART_ContentPresenter",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    RecognizesAccessKey = false,
                    [!ContentPresenter.ContentProperty] = tab[!TabItem.HeaderProperty],
                };
                var border = new Border
                {
                    Name = "PART_LayoutRoot",
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(3, 3, 3, 1),
                    Padding = new Thickness(8, 5, 6, 5),
                    MinHeight = 28,
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = presenter,
                };
                void Paint()
                {
                    var selected = tab.IsSelected;
                    border.Background = selected ? Brushes.White : Brushes.Transparent;
                    border.BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(176, 176, 176)) : Brushes.Transparent;
                    var close = (tab.Header as Panel)?.Children.OfType<Button>().FirstOrDefault();
                    if (close is not null) close.IsVisible = selected || tab.IsPointerOver;
                }
                tab.PropertyChanged += (_, args) =>
                {
                    if (args.Property == TabItem.IsSelectedProperty || args.Property == InputElement.IsPointerOverProperty) Paint();
                };
                Paint();
                return border;
            })),
        },
    };

    /// <summary>文書タブの小さなアイコン。フォルダー比較は黄色、それ以外は白い紙。</summary>
    private static Control SessionKindIcon(ComparisonProject project)
    {
        var icon = new Border
        {
            Width = 14, Height = 12, Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(1),
        };
        ApplySessionIcon(icon, project);
        return icon;
    }

    private static void ApplySessionIcon(Border icon, ComparisonProject project)
    {
        var folder = string.Equals(project.Mode, "Folder", StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(project.LeftPath) || Directory.Exists(project.RightPath);
        icon.Background = folder ? new SolidColorBrush(Color.FromRgb(255, 205, 66)) : Brushes.White;
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

    /// <summary>msgid を見出しにし、言語を変えたときに引き直す。</summary>
    private static MenuItem MenuPopup(string msgid, params Control[] items)
    {
        var menu = Popup(UiText.Menu(msgid), items);
        menu.Tag = msgid;
        return menu;
    }

    private static Separator Separator() => new();

    private MenuItem Pane(string header, string? gesture, FrameCommand command) => Item(header, gesture, PaneAction(command));

    private static MenuItem Item(string header, string? gesture, Func<Task> action) =>
        GestureItem(header, gesture is null ? null : KeyGesture.Parse(gesture), action);

    /// <summary>カタログの msgid から見出しを作り、言語の切替で引き直せるようにする。</summary>
    private MenuItem Labeled(string msgid, string? gesture, Func<Task> action, KeyGesture? key = null)
    {
        var item = GestureItem(UiText.Menu(msgid), key ?? (gesture is null ? null : KeyGesture.Parse(gesture)), action);
        item.Tag = msgid;
        return item;
    }

    /// <summary>日本語が既定。OS の言語は利用者が選んだときだけ使う。</summary>
    private MenuItem LanguageMenu()
    {
        MenuItem Choice(string header, string language)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = UiText.Language == language, Tag = "lang:" + language };
            item.Click += (_, _) => ApplyUiLanguage(language);
            return item;
        }
        return Popup("言語(_L)", Choice("日本語", "Japanese"), Choice("English", "English"), Choice("OS の言語", "os"));
    }

    /// <summary>開いているメニューの見出しを、選んだカタログで引き直す。</summary>
    internal void ApplyUiLanguage(string language)
    {
        UiText.Use(language);
        if (FrameMenu is not null) Retitle(FrameMenu.Items);
        SaveViewMenuOptions();
    }

    private static void Retitle(ItemCollection items)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            if (item.Tag is string msgid && !msgid.StartsWith("lang:", StringComparison.Ordinal)) item.Header = UiText.Menu(msgid);
            if (item.Tag is string lang && lang.StartsWith("lang:", StringComparison.Ordinal))
                item.IsChecked = UiText.Language == lang["lang:".Length..];
            Retitle(item.Items);
        }
    }

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
        var all = ContextItem("&All Lines", "view-context-all", options, () => options.DiffContextLines < 0, () => options.SetDiffContext(-1));
        var numbers = DiffTextViewOptions.DiffContextChoices.Select(lines =>
            ContextItem(lines == 1 ? "&1 Line" : $"&{lines} Lines", "view-context-" + lines, options, () => options.DiffContextLines == lines, () => options.SetDiffContext(lines))).ToArray();
        var toggle = Labeled("&Toggle All and 0-9 Lines\tCtrl+D", null, () => { options.ToggleDiffContext(); return Task.CompletedTask; }, new KeyGesture(Key.D, KeyModifiers.Control));
        toggle.Name = "frame-menu-view-context-toggle";
        var invert = ViewOption("&Invert (Hide Different Lines)", "view-context-invert", () => options.DiffContextInvert, value => options.DiffContextInvert = value);
        return MenuPopup("&Diff Context", [all, .. numbers, toggle, invert]);
    }

    /// <summary>コンテキストの行数。選ばれている項目だけチェックを付ける。</summary>
    private MenuItem ContextItem(string header, string name, DiffTextViewOptions options, Func<bool> selected, Action choose)
    {
        var item = new MenuItem { Header = UiText.Menu(header), Name = "frame-menu-" + name, Tag = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = selected() };
        item.Click += (_, _) => choose();
        options.Changed += () => item.IsChecked = selected();
        return item;
    }

    /// <summary>フォーカスのある側のスクロールを、他のペインと同期しない。</summary>
    private MenuItem LockPanesItem(DiffTextViewOptions options)
    {
        var item = new MenuItem { Header = UiText.Menu("&Lock Panes"), Name = "frame-menu-view-lock", Tag = "&Lock Panes", ToggleType = MenuItemToggleType.CheckBox, IsChecked = options.LockedSide >= 0 };
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
        var ok = new Button { Content = UiText.Phrase("OK"), IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = UiText.Phrase("Cancel"), IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(new StackPanel { Spacing = 8, Children = { box, list } });
        var dialog = new Window
        {
            Title = UiText.Phrase("Select &Font..."), Width = 420, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel,
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
        var item = new MenuItem { Header = UiText.Menu(header), Name = "frame-menu-" + name, Tag = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = get() };
        item.Click += (_, _) => set(!get());
        return item;
    }

    private MenuItem ViewOption(string header, string name, Func<bool> get, Action<bool> set)
    {
        var item = new MenuItem { Header = UiText.Menu(header), Name = "frame-menu-" + name, Tag = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = get() };
        item.Click += (_, _) => set(!get());
        TextViewOptions.Changed += () => item.IsChecked = get();
        return item;
    }

    // ID_VIEW_ZOOMIN／ID_VIEW_ZOOMOUT／ID_VIEW_ZOOMNORMAL。本家と同じく全ペインの本文の文字の大きさを変える。
    private Task Zoom(int amount) { TextViewOptions.Zoom(amount); return Task.CompletedTask; }

    // 本家の「開く」は比較対象の選択画面を出す。ペイン上部の選択画面（ComparisonPane.OpenView.cs）を
    // スクロールで見える位置へ戻し、1 番目の欄へ移動する。
    internal void FocusPaths() => ActivePane.ShowSelectFiles();

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
