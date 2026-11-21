using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    private readonly HashSet<int>[] _plainLineBookmarks = [new(), new(), new()];
    private readonly int?[][] _numberedLineBookmarks = Enumerable.Range(0, 3).Select(_ => new int?[10]).ToArray();
    private readonly string[] _bookmarkTextSnapshots = ["", "", ""];
    private readonly ComboBox _lineBookmarkNumber = new()
    {
        Name = "line-bookmark-number",
        ItemsSource = Enumerable.Range(0, 10).Select(number => number.ToString()).ToArray(),
        SelectedIndex = 0,
        Width = 72,
        Margin = new Thickness(4)
    };
    private readonly TextBlock _lineBookmarkSummary = new()
    {
        Name = "line-bookmark-summary",
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        Margin = new Thickness(8, 4),
        MinWidth = 220
    };
    private Button? _lineBookmarkNext;
    private Button? _lineBookmarkPrevious;
    private Button? _lineBookmarkGoNumber;
    private Button? _lineBookmarkClearNumbered;
    private Button? _lineBookmarkClearAll;
    private int _activeLineBookmarkSide;
    private bool _lineBookmarksInitialized;

    private Control CreateLineBookmarkToolbar()
    {
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2) };
        toolbar.Children.Add(new TextBlock
        {
            Text = "行ブックマーク",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(8, 4),
            FontWeight = Avalonia.Media.FontWeight.Bold
        });
        toolbar.Children.Add(CreateLineBookmarkButton("line-bookmark-toggle", "行を切替", TogglePlainBookmark));
        _lineBookmarkPrevious = CreateLineBookmarkButton("line-bookmark-previous", "前へ", () => NavigateBookmark(-1));
        _lineBookmarkNext = CreateLineBookmarkButton("line-bookmark-next", "次へ", () => NavigateBookmark(1));
        toolbar.Children.Add(_lineBookmarkPrevious);
        toolbar.Children.Add(_lineBookmarkNext);
        toolbar.Children.Add(_lineBookmarkNumber);
        toolbar.Children.Add(CreateLineBookmarkButton("line-bookmark-number-toggle", "番号を切替", ToggleNumberedBookmark));
        _lineBookmarkGoNumber = CreateLineBookmarkButton("line-bookmark-number-go", "番号へ移動", GoToNumberedBookmark);
        toolbar.Children.Add(_lineBookmarkGoNumber);
        _lineBookmarkClearNumbered = CreateLineBookmarkButton("line-bookmark-clear-numbered", "番号を消去", ClearNumberedBookmarks);
        _lineBookmarkClearAll = CreateLineBookmarkButton("line-bookmark-clear-all", "全て消去", ClearAllLineBookmarks);
        toolbar.Children.Add(_lineBookmarkClearNumbered);
        toolbar.Children.Add(_lineBookmarkClearAll);
        toolbar.Children.Add(_lineBookmarkSummary);
        _lineBookmarkNumber.SelectionChanged += (_, _) => RefreshLineBookmarkToolbar();
        RefreshLineBookmarkToolbar();
        return toolbar;
    }

    private static Button CreateLineBookmarkButton(string name, string label, Action action)
    {
        var button = new Button { Name = name, Content = label, Padding = new Thickness(8, 4), Margin = new Thickness(4) };
        button.Click += (_, _) => action();
        return button;
    }

    private void InitializeLineBookmarks()
    {
        if (_lineBookmarksInitialized) return;
        _lineBookmarksInitialized = true;
        for (var side = 0; side < _bookmarkTextSnapshots.Length; side++)
        {
            var currentSide = side;
            var editor = TextEditor(side);
            _bookmarkTextSnapshots[side] = editor.Text ?? "";
            editor.GotFocus += (_, _) =>
            {
                _activeLineBookmarkSide = currentSide;
                RefreshLineBookmarkToolbar();
            };
            editor.PropertyChanged += (_, args) =>
            {
                if (args.Property == TextBox.TextProperty)
                    OnLineBookmarkTextChanged(currentSide, editor.Text ?? "");
            };
        }
    }

    private void ResetLineBookmarks()
    {
        for (var side = 0; side < _bookmarkTextSnapshots.Length; side++)
        {
            _plainLineBookmarks[side].Clear();
            Array.Clear(_numberedLineBookmarks[side]);
            _bookmarkTextSnapshots[side] = TextEditor(side).Text ?? "";
        }
        RefreshLineBookmarkToolbar();
    }

    private void OnLineBookmarkTextChanged(int side, string text)
    {
        var previous = _bookmarkTextSnapshots[side];
        if (previous == text) return;
        if (_plainLineBookmarks[side].Count != 0 || _numberedLineBookmarks[side].Any(line => line.HasValue))
            RemapLineBookmarks(side, previous, text);
        _bookmarkTextSnapshots[side] = text;
        RefreshLineBookmarkToolbar();
    }

    private void RemapLineBookmarks(int side, string previous, string current)
    {
        var prefix = 0;
        var sharedLength = Math.Min(previous.Length, current.Length);
        while (prefix < sharedLength && previous[prefix] == current[prefix]) prefix++;

        var suffix = 0;
        while (suffix < previous.Length - prefix && suffix < current.Length - prefix
            && previous[previous.Length - suffix - 1] == current[current.Length - suffix - 1])
            suffix++;

        var oldEnd = previous.Length - suffix;
        var newEnd = current.Length - suffix;
        var removedBreaks = CountLineBreaks(previous, prefix, oldEnd);
        var addedBreaks = CountLineBreaks(current, prefix, newEnd);
        if (removedBreaks == 0 && addedBreaks == 0) return;

        var startLine = CountLineBreaks(previous, 0, prefix);
        var startColumn = prefix - PreviousLineStart(previous, prefix);
        var endLine = startLine + removedBreaks;
        var delta = addedBreaks - removedBreaks;
        // 行頭から一行を削除すると、次の行がそのまま開始行へ移る。共通する先頭文字があっても
        // 削除された行のブックマークを残さないよう、移動した行を照合して列位置を補正する。
        if (removedBreaks > 0 && addedBreaks == 0 && startColumn > 0
            && SameLineContent(previous, startLine + 1, current, startLine))
            startColumn = 0;

        var remappedPlain = new HashSet<int>();
        foreach (var line in _plainLineBookmarks[side])
            if (MapBookmarkLine(line, startLine, startColumn, endLine, removedBreaks, delta) is { } mapped)
                remappedPlain.Add(mapped);
        _plainLineBookmarks[side].Clear();
        _plainLineBookmarks[side].UnionWith(remappedPlain);

        for (var number = 0; number < _numberedLineBookmarks[side].Length; number++)
            if (_numberedLineBookmarks[side][number] is { } line)
                _numberedLineBookmarks[side][number] = MapBookmarkLine(line, startLine, startColumn, endLine, removedBreaks, delta);
    }

    /// <summary>prefix がある行の先頭。CRLF・LF・CR の直後を行頭にする。</summary>
    private static int PreviousLineStart(string text, int prefix)
    {
        for (var index = prefix - 1; index >= 0; index--)
            if (text[index] == '\n' || text[index] == '\r') return index + 1;
        return 0;
    }

    /// <summary>CRLF・LF・CR をそれぞれ1つの改行として数える（LFだけを数えると CR だけの本文で行がずれる）。</summary>
    private static int CountLineBreaks(string text, int start, int end)
    {
        var count = 0;
        for (var index = start; index < end; index++)
        {
            if (text[index] != '\r' && text[index] != '\n') continue;
            count++;
            if (text[index] == '\r' && index + 1 < end && text[index + 1] == '\n') index++;
        }
        return count;
    }

    /// <summary>start 以降の次の改行位置と、その改行が占める文字数（CRLF は 2）。無いときは -1。</summary>
    private static int NextLineBreak(string text, int start, out int length)
    {
        length = 0;
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] != '\r' && text[index] != '\n') continue;
            length = text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
            return index;
        }
        return -1;
    }

    private static bool SameLineContent(string left, int leftLine, string right, int rightLine)
    {
        var leftBounds = LineBounds(left, leftLine);
        var rightBounds = LineBounds(right, rightLine);
        return leftBounds is { } leftRange && rightBounds is { } rightRange
            && left.AsSpan(leftRange.Start, leftRange.End - leftRange.Start)
                .SequenceEqual(right.AsSpan(rightRange.Start, rightRange.End - rightRange.Start));
    }

    private static (int Start, int End)? LineBounds(string text, int line)
    {
        if (line < 0) return null;
        var start = 0;
        for (var current = 0; current < line; current++)
        {
            var lineBreak = NextLineBreak(text, start, out var length);
            if (lineBreak < 0) return null;
            start = lineBreak + length;
        }
        var end = NextLineBreak(text, start, out _);
        if (end < 0) end = text.Length;
        return (start, end);
    }

    private static int? MapBookmarkLine(int line, int startLine, int startColumn, int endLine, int removedBreaks, int delta)
    {
        if (line < startLine) return line;
        if (removedBreaks == 0)
            return line > startLine ? line + delta : line;

        if (startColumn == 0)
        {
            if (line == endLine) return startLine;
            if (line == startLine || line < endLine) return null;
            return line + delta;
        }

        if (line == startLine) return startLine;
        if (line > endLine) return line + delta;
        return null;
    }

    /// <summary>フォーカスのある差分ペインの側（0=左、1=中央、2=右）。ペインロックの対象に使う。</summary>
    internal int ActiveDiffSide => ActiveLineBookmarkSide;

    private int ActiveLineBookmarkSide => Math.Clamp(_activeLineBookmarkSide, 0, 2);

    private void TogglePlainBookmark()
    {
        var side = ActiveLineBookmarkSide;
        var line = CurrentCaretLine(side);
        if (!_plainLineBookmarks[side].Add(line)) _plainLineBookmarks[side].Remove(line);
        RefreshLineBookmarkToolbar();
    }

    private void ToggleNumberedBookmark()
    {
        var number = _lineBookmarkNumber.SelectedIndex;
        if (number is < 0 or > 9) return;
        var side = ActiveLineBookmarkSide;
        var line = CurrentCaretLine(side);
        _numberedLineBookmarks[side][number] = _numberedLineBookmarks[side][number] == line ? null : line;
        RefreshLineBookmarkToolbar();
    }

    private void GoToNumberedBookmark()
    {
        var number = _lineBookmarkNumber.SelectedIndex;
        if (number is < 0 or > 9) return;
        if (_numberedLineBookmarks[ActiveLineBookmarkSide][number] is { } line)
            GoToBookmarkLine(ActiveLineBookmarkSide, line);
    }

    private void NavigateBookmark(int direction)
    {
        var side = ActiveLineBookmarkSide;
        var text = TextEditor(side).Text ?? "";
        var lines = _plainLineBookmarks[side]
            .Concat(_numberedLineBookmarks[side].Where(line => line.HasValue).Select(line => line!.Value))
            .Where(line => line >= 0 && line < CountTextLines(text))
            .Distinct()
            .OrderBy(line => line)
            .ToArray();
        if (lines.Length == 0) return;

        var current = CurrentCaretLine(side);
        var target = direction < 0
            ? lines.LastOrDefault(line => line < current, -1)
            : lines.FirstOrDefault(line => line > current, -1);
        if (target < 0) target = direction < 0 ? lines[^1] : lines[0];
        GoToBookmarkLine(side, target);
    }

    private int CurrentCaretLine(int side)
    {
        var editor = TextEditor(side);
        var text = editor.Text ?? "";
        var caret = Math.Clamp(editor.CaretIndex, 0, text.Length);
        return CountLineBreaks(text, 0, caret);
    }

    private static int CountTextLines(string text) => CountLineBreaks(text, 0, text.Length) + 1;

    private void GoToBookmarkLine(int side, int line)
    {
        var editor = TextEditor(side);
        var text = editor.Text ?? "";
        if (line < 0 || line >= CountTextLines(text)) return;
        var start = 0;
        for (var currentLine = 0; currentLine < line; currentLine++)
        {
            var nextBreak = NextLineBreak(text, start, out var length);
            if (nextBreak < 0) return;
            start = nextBreak + length;
        }
        _activeLineBookmarkSide = side;
        editor.Focus();
        editor.SelectionStart = start;
        editor.SelectionEnd = start;
        editor.CaretIndex = start;
        // 本文は差分タブのエディターで表示しているので、その行へキャレットを移して見える位置へスクロールする。
        FocusDiffEditorRealLine(side, line);
        _activeLineBookmarkSide = side;
        RefreshLineBookmarkToolbar();
    }

    private void ClearNumberedBookmarks()
    {
        Array.Clear(_numberedLineBookmarks[ActiveLineBookmarkSide]);
        RefreshLineBookmarkToolbar();
    }

    private void ClearAllLineBookmarks()
    {
        var side = ActiveLineBookmarkSide;
        _plainLineBookmarks[side].Clear();
        Array.Clear(_numberedLineBookmarks[side]);
        RefreshLineBookmarkToolbar();
    }

    private void RefreshLineBookmarkToolbar()
    {
        var side = ActiveLineBookmarkSide;
        var plain = _plainLineBookmarks[side].OrderBy(line => line).Select(line => (line + 1).ToString()).ToArray();
        var numbered = Enumerable.Range(0, 10)
            .Where(number => _numberedLineBookmarks[side][number].HasValue)
            .Select(number => $"#{number}={_numberedLineBookmarks[side][number]!.Value + 1}")
            .ToArray();
        var positions = new List<string>();
        if (plain.Length != 0) positions.Add("行 " + string.Join(", ", plain));
        if (numbered.Length != 0) positions.Add("番号 " + string.Join(", ", numbered));
        _lineBookmarkSummary.Text = $"{side switch { 0 => "左", 1 => "中央", _ => "右" }}: {(positions.Count == 0 ? "位置なし" : string.Join(" / ", positions))}";
        // 差分タブの行番号の欄にある印を描き直す。
        foreach (var view in _diffViews) view.Margin.InvalidateVisual();

        var any = plain.Length != 0 || numbered.Length != 0;
        if (_lineBookmarkNext is not null) _lineBookmarkNext.IsEnabled = any;
        if (_lineBookmarkPrevious is not null) _lineBookmarkPrevious.IsEnabled = any;
        if (_lineBookmarkClearNumbered is not null) _lineBookmarkClearNumbered.IsEnabled = numbered.Length != 0;
        if (_lineBookmarkClearAll is not null) _lineBookmarkClearAll.IsEnabled = any;
        if (_lineBookmarkGoNumber is not null)
        {
            var number = _lineBookmarkNumber.SelectedIndex;
            _lineBookmarkGoNumber.IsEnabled = number is >= 0 and < 10 && _numberedLineBookmarks[side][number].HasValue;
        }
    }
}
