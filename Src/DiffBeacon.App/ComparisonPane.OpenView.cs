using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMergeの「ファイルまたはフォルダーの選択」画面（Merge.rc の IDD_OPEN / OpenView.cpp）に合わせた入力欄。
/// 既存のパス欄・フィルター欄・比較ボタンのインスタンスをそのまま載せ替える（自己検証・プロジェクト復元・
/// フォルダー一覧のダブルクリックが同じインスタンスへ書き込むため、複製は作らない）。
/// 文言は Translations/WinMerge/Japanese.po の訳で、アクセスキーの「&amp;」は「_」へ置き換える。
/// 本家にある各欄の「読み取り専用」と展開／前処理プラグインの選択欄は、対応する機能が無いため置かない。
/// </summary>
public sealed partial class ComparisonPane
{
    internal Control? OpenView { get; private set; }
    // 比較結果（差分・形式別ビュー・独立テキスト）をまだ持たない空のセッションか。
    // 本家は比較前に選択画面だけを出すため、この間は選択画面の高さ制限を緩める。
    private bool HasComparisonContent => CurrentDiff is not null || _specialTab.Content is not null || IndependentText;
    // 自己検証で、選択画面がスクロールで隠れた状態から「開く」で戻ることを確かめる。
    internal ScrollViewer ComparisonToolbar => _comparisonToolbar;
    internal TextBox FileFilterBox => _fileFilter;
    internal CheckBox RecursiveBox => _recursive;

    private Control BuildOpenView()
    {
        // 本家の並びは 1番目＝左、2番目＝右（三者では中央）、3番目（オプション）。
        // DiffBeacon の三者比較は祖先／中央を BasePath に持つため、3番目の欄を BasePath に割り当てる。
        var view = new StackPanel { Name = "open-view", Spacing = 3 };
        view.Children.Add(PathGroup("_1 番目のファイルまたはフォルダー", LeftPath, "1 番目と 2 番目を交換", LeftPath, RightPath));
        view.Children.Add(PathGroup("_2 番目のファイルまたはフォルダー", RightPath, "2 番目と 3 番目を交換", RightPath, BasePath));
        view.Children.Add(PathGroup("_3 番目のファイルまたはフォルダー (オプション)", BasePath, "1 番目と 3 番目を交換", LeftPath, BasePath));

        // 「フォルダー: フィルター」とサブフォルダーの指定。本家の左下の枠に当たる。
        _recursive.Content = "サブフォルダーを含める(_I)";
        _fileFilter.Width = double.NaN;
        var filterRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Detach(_fileFilter); filterRow.Children.Add(_fileFilter);
        Detach(_recursive); Grid.SetColumn(_recursive, 1); filterRow.Children.Add(_recursive);
        view.Children.Add(Group(" フォルダー: フィルター", filterRow));

        // 下端のボタン行。本家の「キャンセル」は選択画面を閉じる操作で、ここでは画面が常設のため置かない。
        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 2, 0, 0) };
        var saveProject = new Button { Content = "プロジェクトを保存(_V)...", Padding = new Thickness(9, 4) };
        saveProject.Click += async (_, _) => await GuardAsync(() => ObserveCriticalTextSaveTask("project-save", SaveProjectAsync()));
        DockPanel.SetDock(saveProject, Dock.Left); buttons.Children.Add(saveProject);
        CompareButton.Content = "比較(_M)";
        Detach(CompareButton); DockPanel.SetDock(CompareButton, Dock.Right); buttons.Children.Add(CompareButton);
        view.Children.Add(buttons);
        return OpenView = view;
    }

    private Control PathGroup(string header, TextBox box, string swapLabel, TextBox first, TextBox second)
    {
        // 最小ウィンドウ（特にmacOSの字形）でも比較ボタンまで収まるよう、本家の3行（見出し／欄／交換と参照）を
        // 2行（見出しと交換／欄と参照）に詰める。並びと文言は本家のまま。
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        Detach(box); body.Children.Add(box);
        // 本家の交換ボタンは小さな絵のボタンと説明文の組。
        var swap = new Button { Content = "⇅", Width = 22, Height = 20, Padding = new Thickness(0), FontSize = 11,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(swap, swapLabel);
        swap.Click += (_, _) => (first.Text, second.Text) = (second.Text, first.Text);
        var swapRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        swapRow.Children.Add(swap);
        swapRow.Children.Add(new TextBlock { Text = swapLabel, FontSize = 12, Foreground = WinMergeColors.FrameText, VerticalAlignment = VerticalAlignment.Center });
        // 本家の「参照」は分割ボタンで、ファイルとフォルダーを選べる。
        var browse = new Button { Content = "参照(_B)...", Padding = new Thickness(9, 2), MinWidth = 90 };
        var flyout = new MenuFlyout();
        flyout.Items.Add(BrowseItem("ファイル(_F)...", box, folder: false));
        flyout.Items.Add(BrowseItem("フォルダー(_O)...", box, folder: true));
        browse.Flyout = flyout;
        Grid.SetColumn(browse, 1); body.Children.Add(browse);
        return Group(header, body, swapRow);
    }

    private MenuItem BrowseItem(string header, TextBox box, bool folder)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await GuardAsync(async () =>
        {
            var selected = folder
                ? (await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false })).Select(x => x.TryGetLocalPath()).FirstOrDefault()
                : (await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false })).Select(x => x.TryGetLocalPath()).FirstOrDefault();
            if (selected is not null) box.Text = selected;
        });
        return item;
    }

    // 本家の GROUPBOX に相当する、見出し付きの細い枠。
    private static Control Group(string header, Control body, Control? headerExtra = null)
    {
        var content = new StackPanel { Spacing = 3 };
        var label = new Label { Content = header, Padding = new Thickness(0), FontSize = 12, Foreground = WinMergeColors.FrameText, Target = FirstFocusable(body), VerticalAlignment = VerticalAlignment.Center };
        var headerRow = new DockPanel();
        if (headerExtra is not null) { DockPanel.SetDock(headerExtra, Dock.Right); headerRow.Children.Add(headerExtra); }
        headerRow.Children.Add(label);
        content.Children.Add(headerRow);
        content.Children.Add(body);
        return new Border { BorderBrush = WinMergeColors.FrameBorder, BorderThickness = new Thickness(1), Padding = new Thickness(6, 2, 6, 4), Child = content };
    }

    private static IInputElement? FirstFocusable(Control body) => body switch
    {
        Panel panel => panel.Children.OfType<TextBox>().FirstOrDefault() as IInputElement ?? panel.Children.FirstOrDefault(),
        _ => body,
    };

    private static void Detach(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel: panel.Children.Remove(control); break;
            case ContentControl host when ReferenceEquals(host.Content, control): host.Content = null; break;
            case Decorator decorator when ReferenceEquals(decorator.Child, control): decorator.Child = null; break;
        }
    }
}
