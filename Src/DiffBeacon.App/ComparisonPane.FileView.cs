using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMergeのファイル比較画面（Src/MergeEditFrm.cpp）に合わせた「差分」タブの構成。
/// 左端にロケーションペイン、各ペインの上にファイル名の帯、中央に左右並びの本文、下段に Diff ペインを置く。
/// 本文は既存の <see cref="DiffList"/>（左右を同じ行に整列した一覧）をそのまま使い、自己検証の参照先を変えない。
/// </summary>
public sealed partial class ComparisonPane
{
    private readonly LocationPane _locationPane = new() { Name = "location-pane" };
    private readonly TextBlock _diffPaneLeft = DiffPaneText("diff-pane-left");
    private readonly TextBlock _diffPaneRight = DiffPaneText("diff-pane-right");
    // ファイル名の帯に出すパス。見出し（左／右・読取り専用などの状態）は既存の文言のまま残し、その右にパスを並べる。
    private readonly TextBlock _leftHeaderPath = HeaderPathText("file-header-left");
    private readonly TextBlock _rightHeaderPath = HeaderPathText("file-header-right");
    private ScrollViewer? _diffListScroll;
    private int _selectedBlock = -1;

    internal LocationPane Location => _locationPane;
    internal (TextBlock Left, TextBlock Right) DiffPaneTexts => (_diffPaneLeft, _diffPaneRight);
    internal int SelectedDifferenceBlock => _selectedBlock;
    internal (string? Left, string? Right) FileHeaderPaths => (_leftHeaderPath.Text, _rightHeaderPath.Text);

    // 差分セルの塗り分けに使う目印。行のある側か、行の無い側（本家の削除色）かを持つ。
    internal sealed record DiffCellTag(bool HasLine);

    private Control BuildFileCompareView()
    {
        // 本家のファイル名の帯（CEditorFilePathBar）。灰色の帯にパスを表示する。
        var headers = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        headers.Children.Add(HeaderBar(_leftCaption, _leftHeaderPath));
        var rightHeader = HeaderBar(_rightCaption, _rightHeaderPath); Grid.SetColumn(rightHeader, 1); headers.Children.Add(rightHeader);

        var body = new DockPanel();
        DockPanel.SetDock(headers, Dock.Top); body.Children.Add(headers);
        body.Children.Add(DiffList);

        // 左端のロケーションペイン。本家と同じく本文の左に縦長で置く。
        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var location = new Border { BorderBrush = WinMergeColors.FrameBorder, BorderThickness = new Thickness(0, 0, 1, 0), Child = _locationPane };
        main.Children.Add(location);
        Grid.SetColumn(body, 1); main.Children.Add(body);

        // 下段の Diff ペイン。選択行の左右を並べ、行内差分を本家の色で示す。高さは境界線で変えられる。
        var diffPane = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 2) };
        diffPane.Children.Add(DiffPaneRow(_diffPaneLeft));
        var rightRow = DiffPaneRow(_diffPaneRight); Grid.SetRow(rightRow, 1); diffPane.Children.Add(rightRow);
        var diffPaneHost = new Border
        {
            Name = "diff-pane", BorderBrush = WinMergeColors.FrameBorder, BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new ScrollViewer { Content = diffPane, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };
        ToolTip.SetTip(diffPaneHost, "Diff ペイン");

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        root.Children.Add(main);
        var splitter = new GridSplitter { Height = 4, Background = WinMergeColors.FrameFace, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitter, 1); root.Children.Add(splitter);
        Grid.SetRow(diffPaneHost, 2); root.Children.Add(diffPaneHost);

        DiffList.SelectionChanged += (_, _) => OnDiffSelectionChanged();
        DiffList.ContainerPrepared += (_, args) =>
        {
            // 行の中身はテンプレート適用後に作られるため、描画前の段階で色を合わせる。
            if (args.Container is ListBoxItem item) Dispatcher.UIThread.Post(() => ColorDiffRow(item), DispatcherPriority.Loaded);
        };
        DiffList.TemplateApplied += (_, args) =>
        {
            if (_diffListScroll is not null) _diffListScroll.ScrollChanged -= OnDiffListScrolled;
            _diffListScroll = args.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            if (_diffListScroll is not null) _diffListScroll.ScrollChanged += OnDiffListScrolled;
        };
        // 本家は現在行を青く塗らず、選択中の差分ブロックだけを赤系で示す。一覧の選択色は淡い灰色に抑える。
        foreach (var key in new[] { "SystemControlHighlightListAccentLowBrush", "SystemControlHighlightListAccentMediumBrush",
                     "SystemControlHighlightListAccentHighBrush", "SystemControlHighlightListLowBrush", "SystemControlHighlightListMediumBrush" })
            DiffList.Resources[key] = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
        _locationPane.RowRequested += ScrollDiffListToRow;
        return root;
    }

    private static Border HeaderBar(TextBlock caption, TextBlock path)
    {
        var bar = new DockPanel();
        DockPanel.SetDock(caption, Dock.Left); bar.Children.Add(caption); bar.Children.Add(path);
        return new Border { Background = WinMergeColors.FrameFace, BorderBrush = WinMergeColors.FrameBorder, BorderThickness = new Thickness(0, 0, 1, 1), Child = bar };
    }

    private static TextBlock HeaderPathText(string name) => new()
    {
        Name = name, Margin = new Thickness(0, 3, 6, 3), FontSize = 12, Foreground = WinMergeColors.FrameText,
        TextTrimming = TextTrimming.PrefixCharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock DiffPaneText(string name) => new()
    {
        Name = name, FontFamily = new FontFamily("Cascadia Mono, Menlo, Consolas, monospace"), FontSize = 13, Margin = new Thickness(6, 1),
        MinHeight = 18, TextWrapping = TextWrapping.NoWrap,
    };

    private static Control DiffPaneRow(TextBlock text) => new Border { Child = text };

    // 差分結果が変わったら、ロケーションペインと Diff ペインを作り直す。
    private void OnCurrentDiffChanged()
    {
        _locationPane.SetDiff(CurrentDiff);
        // 帯のパスは比較した時点の入力パスを示す。編集欄だけの比較ではパスが空なので何も出さない。
        _leftHeaderPath.Text = CurrentDiff is null ? "" : LeftPath.Text ?? "";
        _rightHeaderPath.Text = CurrentDiff is null ? "" : RightPath.Text ?? "";
        _selectedBlock = -1;
        UpdateDiffPane();
        UpdateLocationViewport();
    }

    private void OnDiffSelectionChanged()
    {
        var index = DiffList.SelectedIndex;
        var block = CurrentDiff?.Blocks.FirstOrDefault(x => index >= x.RowStart && index < x.RowStart + x.RowCount);
        var selected = block?.Index ?? -1;
        if (selected != _selectedBlock)
        {
            _selectedBlock = selected;
            _locationPane.SetCurrentBlock(selected);
            foreach (var container in DiffList.GetRealizedContainers().OfType<ListBoxItem>()) ColorDiffRow(container);
        }
        UpdateDiffPane();
    }

    /// <summary>行の左右セルを、選択中の差分ブロックなら本家の選択色、それ以外なら通常の差分色で塗る。</summary>
    private void ColorDiffRow(ListBoxItem item)
    {
        if (CurrentDiff is null) return;
        var index = DiffList.IndexFromContainer(item);
        var inSelected = _selectedBlock >= 0 && _selectedBlock < CurrentDiff.Blocks.Count
            && index >= CurrentDiff.Blocks[_selectedBlock].RowStart && index < CurrentDiff.Blocks[_selectedBlock].RowStart + CurrentDiff.Blocks[_selectedBlock].RowCount;
        foreach (var cell in item.GetVisualDescendants().OfType<Border>())
        {
            if (cell.Tag is not DiffCellTag tag) continue;
            cell.Background = tag.HasLine
                ? inSelected ? WinMergeColors.SelectedDifference : WinMergeColors.Difference
                : inSelected ? WinMergeColors.SelectedDifferenceDeleted : WinMergeColors.DifferenceDeleted;
        }
    }

    // Diff ペインには選択行の左右を表示する。未選択なら選択中の差分ブロックの先頭行を表示する。
    private void UpdateDiffPane()
    {
        var row = DiffList.SelectedItem as DiffRow;
        SetDiffPaneText(_diffPaneLeft, row?.LeftText, row?.LeftSpans ?? []);
        SetDiffPaneText(_diffPaneRight, row?.RightText, row?.RightSpans ?? []);
    }

    private static void SetDiffPaneText(TextBlock block, string? text, IReadOnlyList<InlineSpan> spans)
    {
        block.Inlines?.Clear();
        block.Text = null;
        if (text is null) { block.Text = ""; return; }
        if (spans.Count == 0) { block.Text = text; return; }
        var offset = 0;
        foreach (var span in spans)
        {
            block.Inlines!.Add(new Run(text[offset..span.Start]));
            block.Inlines.Add(new Run(text.Substring(span.Start, span.Length)) { Background = WinMergeColors.WordDifference });
            offset = span.Start + span.Length;
        }
        block.Inlines!.Add(new Run(text[offset..]));
    }

    private void OnDiffListScrolled(object? sender, ScrollChangedEventArgs e) => UpdateLocationViewport();

    // ロケーションペインの表示範囲枠を、一覧のスクロール位置に合わせる。
    private void UpdateLocationViewport()
    {
        if (_diffListScroll is not { } scroll || scroll.Extent.Height <= 0) { _locationPane.SetViewport(0, 1); return; }
        _locationPane.SetViewport(scroll.Offset.Y / scroll.Extent.Height, scroll.Viewport.Height / scroll.Extent.Height);
    }

    /// <summary>
    /// 差分の移動先を表示する。本家と同じく、ブロックを画面の端ではなく中央付近に置き、前後の文脈と後続の行も見えるようにする。
    /// 先に選択行を実体化してから位置を合わせ、仮想化された一覧でも確実に表示する。
    /// </summary>
    private void RevealDifference(DiffBlock block)
    {
        DiffList.ScrollIntoView(DiffList.SelectedItem!);
        DiffList.UpdateLayout();
        ScrollDiffListToRow(block.RowStart + block.RowCount / 2);
        DiffList.UpdateLayout();
    }

    // ロケーションペインで指定された行が本文の中央に来るようにスクロールする（本家の既定動作）。
    private void ScrollDiffListToRow(int row)
    {
        if (CurrentDiff is not { Rows.Count: > 0 } diff) return;
        if (_diffListScroll is { Extent.Height: > 0 } scroll)
        {
            var target = (double)row / diff.Rows.Count * scroll.Extent.Height - scroll.Viewport.Height / 2;
            scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(target, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        }
        else DiffList.ScrollIntoView(row);
        UpdateLocationViewport();
    }
}
