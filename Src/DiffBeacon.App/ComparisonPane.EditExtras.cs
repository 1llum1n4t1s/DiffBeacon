using System.Diagnostics;
using System.Text;
using DiffBeacon.Core;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace DiffBeacon.App;

/// <summary>選択した文字列を印として塗る。色の一覧や正規表現の印は持たない。</summary>
internal sealed class TextMarkerColorizer : DocumentColorizingTransformer
{
    /// <summary>塗る文字列。空なら何も塗らない。</summary>
    internal List<string> Markers { get; } = [];

    /// <summary>印の背景。本家のマーカー色の既定に近い黄。</summary>
    private static readonly IBrush Mark = new SolidColorBrush(Color.FromRgb(255, 255, 128));

    /// <summary>行の中で印の文字列を塗る。</summary>
    protected override void ColorizeLine(DocumentLine line)
    {
        if (Markers.Count == 0 || CurrentContext is null) return;
        var text = CurrentContext.Document.GetText(line);
        foreach (var marker in Markers)
        {
            if (marker.Length == 0) continue;
            var index = 0;
            while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                ChangeLinePart(line.Offset + index, line.Offset + index + marker.Length, element => element.BackgroundBrush = Mark);
                index += marker.Length;
            }
        }
    }
}

public sealed partial class ComparisonPane
{
    /// <summary>検証用。設定すると、関連付けで開く代わりにパスを受け取る。</summary>
    internal Action<string>? ExternalLauncher { get; set; }

    /// <summary>検証用。設定すると、置換フィルターの確認をこの関数で行う。</summary>
    internal Func<string, Task<bool>>? SubstitutionConfirmation { get; set; }

    /// <summary>表示フィルター。本家の「Line contains "…" AND …」。</summary>
    private string _displayFilter = "";

    /// <summary>いまの行フィルター（除外行の正規表現）。</summary>
    internal string LineFilterPattern { get => _ignoreRegex.Text ?? ""; set => _ignoreRegex.Text = value; }

    /// <summary>いまの表示フィルター。</summary>
    internal string DisplayFilter => _displayFilter;

    /// <summary>印にした文字列。</summary>
    internal IReadOnlyList<string> TextMarkers => _diffViews[0].MarkerColorizer.Markers;

    /// <summary>
    /// 選択した表示行を、フォーカスのある側から指定した側へコピーする。
    /// 選択が無ければキャレットの行だけ。コピー先の対応する行を置き換え、コピー元がゴーストならその行を消す。
    /// </summary>
    internal void CopySelectedLines(int destinationSide)
    {
        if (!DiffEditCommandAvailable(out var sourceSide)) return;
        if (sourceSide == destinationSide) { _status.Text = "コピー元とコピー先が同じです。"; return; }
        if (destinationSide == 1 && !_diffDisplay.ThreeWay) { _status.Text = "中央の側はありません。"; return; }
        if (!CanEditText(destinationSide, _diffViews[destinationSide], "コピーできません")) return;
        var editor = _diffViews[sourceSide].View;
        int startLine;
        int endLine;
        if (editor.SelectionLength > 0)
        {
            startLine = editor.Document.GetLineByOffset(editor.SelectionStart).LineNumber;
            var endOffset = editor.SelectionStart + editor.SelectionLength;
            endLine = editor.Document.GetLineByOffset(endOffset > editor.SelectionStart ? endOffset - 1 : editor.SelectionStart).LineNumber;
        }
        else
        {
            startLine = endLine = editor.TextArea.Caret.Line;
        }
        var model = _diffDisplay;
        if (model.RowCount == 0 || editor.Document.LineCount < model.RowCount) { _status.Text = "表示と本文の行がそろっていないためコピーできません。"; return; }
        var sourceLines = DiffEditorSide.SplitLines(model.Texts[sourceSide]);
        var destinationLines = DiffEditorSide.SplitLines(TextEditor(destinationSide).Text ?? "");
        var output = new List<(string Content, string Ending)>();
        var destinationIndex = 0;
        for (var row = 0; row < model.RowCount; row++)
        {
            var selected = row + 1 >= startLine && row + 1 <= endLine;
            var sourceNumber = NumberAt(model, sourceSide, row);
            var destinationNumber = NumberAt(model, destinationSide, row);
            if (!selected)
            {
                if (destinationNumber is int kept && kept - 1 == destinationIndex && destinationIndex < destinationLines.Count)
                    output.Add(destinationLines[destinationIndex++]);
                continue;
            }
            if (destinationNumber is int replaced && replaced - 1 == destinationIndex && destinationIndex < destinationLines.Count)
                destinationIndex++;
            if (sourceNumber is int source && source >= 1 && source <= sourceLines.Count)
                output.Add(sourceLines[source - 1]);
        }
        while (destinationIndex < destinationLines.Count) output.Add(destinationLines[destinationIndex++]);
        ApplyReplacedText(destinationSide, string.Concat(output.Select(line => line.Content + line.Ending)));
    }

    /// <summary>表示行の本物の行番号（1始まり）。無ければ null。</summary>
    private static int? NumberAt(DiffDisplayModel model, int side, int row)
    {
        var numbers = model.LineNumbers[side];
        return row >= 0 && row < numbers.Length ? numbers[row] : null;
    }

    /// <summary>選択を行フィルターへ足す。正規表現の記号は逃がし、既存の式とは OR でつなぐ。</summary>
    internal void AddSelectionToLineFilter()
    {
        if (SelectedLiteral() is not { Length: > 0 } text) { _status.Text = "行フィルターに追加する選択がありません。"; return; }
        var pattern = RegexEscape(text);
        var current = _ignoreRegex.Text ?? "";
        if (current.Length == 0) _ignoreRegex.Text = pattern;
        else if (!current.Split('|').Contains(pattern)) _ignoreRegex.Text = current + "|" + pattern;
        CompareEditors();
        _status.Text = "行フィルターに追加しました。";
    }

    /// <summary>選択、無ければキャレットの単語を、表示フィルター「Line contains」へ AND で足して折りたたむ。</summary>
    internal void AddSelectionToDisplayFilter()
    {
        if (!DiffEditCommandAvailable(out var side)) return;
        var text = SelectedLiteral();
        if (string.IsNullOrEmpty(text)) text = WordAtCaret(side);
        if (string.IsNullOrEmpty(text)) { _status.Text = "表示フィルターに追加する文字列がありません。"; return; }
        var clause = "Line contains \"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        _displayFilter = _displayFilter.Length == 0 ? clause : _displayFilter + " AND " + clause;
        ApplyDiffContext();
        _status.Text = "表示フィルターに追加しました。";
    }

    /// <summary>選択中の差異の左右の本文を、正規表現ではない置換規則として足す。三者比較では足さない。</summary>
    internal async Task AddSelectionToSubstitutionFilterAsync()
    {
        if (!DiffEditCommandAvailable(out _)) return;
        if (_diffDisplay.ThreeWay) { _status.Text = "置換フィルターへの追加は二者比較だけです。"; return; }
        if (CurrentCopyBlock() is not { } block) { _status.Text = "置換フィルターに追加する差異がありません。"; return; }
        var left = RealDiffContents(block, _diffDisplay.PairLeft);
        var right = RealDiffContents(block, _diffDisplay.PairRight);
        if (left.Length == 0) { _status.Text = "置換フィルターに追加する差異がありません。"; return; }
        if (_substitutions.Any(rule => rule.Pattern == left && rule.Replacement == right))
        { _status.Text = "その組は既に置換フィルターにあります。"; return; }
        var message = $"\"{left}\" <-> \"{right}\"";
        var confirmed = SubstitutionConfirmation is { } confirm
            ? await confirm(message)
            : await Dialogs.ConfirmAsync(_owner, "この差異を置換フィルターに追加する", message);
        if (!confirmed) return;
        var rules = _substitutions.ToList();
        rules.Add(new SubstitutionRule(left, right, true) { UseRegex = false, Enabled = true });
        SetAdvancedFilters(_ignoreNumbers.IsChecked == true, (CommentSyntax)_comments.SelectedIndex, (WhitespaceMode)_whitespace.SelectedIndex, rules.ToArray());
        CompareEditors();
        _status.Text = "置換フィルターに追加しました。";
    }

    /// <summary>差異の範囲にある本物の行の本文だけをつなぐ。ゴースト行の改行は含めない。</summary>
    private string RealDiffContents(DiffBlock block, int side)
    {
        var lines = DiffEditorSide.SplitLines(_diffDisplay.Texts[side]);
        var builder = new StringBuilder();
        for (var row = block.RowStart; row < block.RowStart + block.RowCount; row++)
        {
            if (NumberAt(_diffDisplay, side, row) is not int number || number < 1 || number > lines.Count) continue;
            if (builder.Length > 0) builder.Append(lines[number - 1].Ending.Length > 0 ? lines[number - 1].Ending : "\n");
            builder.Append(lines[number - 1].Content);
        }
        return builder.ToString();
    }

    /// <summary>フォーカスのある側の選択。無ければ空。</summary>
    private string SelectedLiteral()
    {
        if (!DiffEditCommandAvailable(out var side)) return "";
        var editor = _diffViews[side].View;
        if (editor.SelectionLength <= 0) return "";
        if (!PrepareTextSearch(side, out var view, out var real)) return "";
        var lines = TextSearcher.Lines(real);
        var (start, end) = RealSelection(view, real, lines);
        return end > start ? real[start..end] : "";
    }

    /// <summary>キャレットの位置の単語。英数字と下線。</summary>
    private string WordAtCaret(int side)
    {
        if (!PrepareTextSearch(side, out var view, out var real)) return "";
        var lines = TextSearcher.Lines(real);
        var offset = Math.Clamp(view.View.CaretOffset, 0, real.Length);
        var start = offset;
        var end = offset;
        while (start > 0 && IsWord(real[start - 1])) start--;
        while (end < real.Length && IsWord(real[end])) end++;
        return end > start ? real[start..end] : "";
    }

    /// <summary>単語の文字か。</summary>
    private static bool IsWord(char value) => value is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';

    /// <summary>正規表現の記号を逃がす。本家 to_regex と同じ対象。</summary>
    private static string RegexEscape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var value in text)
        {
            if (value is '\\' or '*' or '+' or '?' or '|' or '.' or '^' or '$' or '(' or ')' or '[' or ']' or '{' or '}')
                builder.Append('\\');
            builder.Append(value switch { '\t' => "\\t", '\n' => "\\n", '\r' => "\\r", _ => value.ToString() });
        }
        return builder.ToString();
    }

    /// <summary>表示フィルターが行を隠すか。</summary>
    private bool DisplayFilterHidesAny(DiffDisplayModel model) => DisplayFilterPhrases().Count > 0 && model.RowCount > 0;

    /// <summary>その表示行を表示フィルターで隠すか。どの側の本文にも含まれなければ隠す。</summary>
    private bool DisplayFilterHides(DiffDisplayModel model, int row)
    {
        var phrases = DisplayFilterPhrases();
        if (phrases.Count == 0) return false;
        for (var side = 0; side < 3; side++)
        {
            if (side == 1 && !model.ThreeWay) continue;
            if (NumberAt(model, side, row) is not int number) continue;
            var lines = DiffEditorSide.SplitLines(model.Texts[side]);
            if (number < 1 || number > lines.Count) continue;
            if (phrases.All(phrase => lines[number - 1].Content.Contains(phrase, StringComparison.Ordinal))) return false;
        }
        return true;
    }

    /// <summary>「Line contains "…"」の中身。</summary>
    private List<string> DisplayFilterPhrases()
    {
        var phrases = new List<string>();
        const string marker = "Line contains \"";
        var index = 0;
        while ((index = _displayFilter.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            index += marker.Length;
            var end = _displayFilter.IndexOf('"', index);
            if (end < 0) break;
            phrases.Add(_displayFilter[index..end].Replace("\\\"", "\"").Replace("\\\\", "\\"));
            index = end + 1;
        }
        return phrases;
    }

    /// <summary>選択を印にして、全ペインで塗る。</summary>
    internal void AddTextMarker()
    {
        if (SelectedLiteral() is not { Length: > 0 } text) { _status.Text = "印にする選択がありません。"; return; }
        foreach (var side in _diffViews)
        {
            if (!side.MarkerColorizer.Markers.Contains(text)) side.MarkerColorizer.Markers.Add(text);
            side.View.TextArea.TextView.Redraw();
        }
        _status.Text = "印を付けました。";
    }

    /// <summary>印を全部消す。</summary>
    internal void ClearTextMarkers()
    {
        foreach (var side in _diffViews)
        {
            side.MarkerColorizer.Markers.Clear();
            side.View.TextArea.TextView.Redraw();
        }
    }

    /// <summary>フォーカスのある側のファイルを、OS の関連付けで開く。</summary>
    internal void OpenWithRegisteredApplication()
    {
        if (FocusedPath() is not { } path) return;
        LaunchExternal(path);
    }

    /// <summary>フォーカスのある側のファイルがあるフォルダーを開く。</summary>
    internal void OpenParentFolder()
    {
        if (FocusedPath() is not { } path) return;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) { _status.Text = "フォルダーがありません。"; return; }
        LaunchExternal(directory);
    }

    /// <summary>外部エディターは設定画面が無いので開かない。</summary>
    internal void OpenWithExternalEditor() => _status.Text = "外部エディターは設定されていません。";

    /// <summary>定義へ移動する記号索引は無い。</summary>
    internal void GoToDefinition() => _status.Text = "定義へ移動する言語サービスはありません。";

    /// <summary>比較した実在ファイルを、外枠の最近使った一覧へ足す。</summary>
    private void NoteOpenedPaths()
    {
        if (_owner is MainWindow window) window.RememberRecent(LeftPath.Text, BasePath.Text, RightPath.Text);
    }

    /// <summary>状態欄へ理由を出す。</summary>
    internal void SetStatus(string text) => _status.Text = text;

    /// <summary>形式を指定して比較し直す。0 自動、1 テキスト、3 バイナリ、4 画像、6 表、7 アーカイブ。</summary>
    internal Task RecompareAsAsync(int mode)
    {
        if (mode is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(mode));
        SelectMode(mode);
        return ComparePathsAsync();
    }

    /// <summary>フォーカスのある側の保存文字コードを変える。本文は変えない。</summary>
    internal void SetSideEncoding(int side, string name)
    {
        var text = TextEditor(side).Text ?? "";
        var next = (TextInputDocument(side) ?? DiffBeacon.Core.TextDocument.Create(text)).WithEncoding(name, text);
        switch (side)
        {
            case 0: _leftDocument = next; break;
            case 1: _baseDocument = next; break;
            case 2: _rightDocument = next; break;
            default: throw new ArgumentOutOfRangeException(nameof(side));
        }
        _status.Text = $"文字コードを {next.EncodingName}{(next.HasBom ? " BOM" : "")} にしました。";
    }

    /// <summary>フォーカスのある側の保存文字コード。</summary>
    internal (string Name, bool Bom) SideEncoding(int side)
    {
        var document = TextInputDocument(side);
        return document is null ? ("", false) : (document.EncodingName, document.HasBom);
    }

    /// <summary>クリップボードの文字を、フォーカスのある側へ入れて比較し直す。</summary>
    internal async Task OpenClipboardTextAsync()
    {
        if (DiffClipboard() is not { } clipboard) { _status.Text = "クリップボードを利用できません。"; return; }
        var text = await clipboard.ReadTextAsync();
        if (string.IsNullOrEmpty(text)) { _status.Text = "クリップボードに文字がありません。"; return; }
        if (text.Contains('\0')) { _status.Text = "クリップボードの NUL は本文にできません。"; return; }
        var side = SearchSideIndex();
        TextEditor(side).Text = text;
        CompareEditors();
    }

    /// <summary>フォーカスのある側の改行を、指定した CR/LF/CRLF にそろえる。</summary>
    internal void ConvertLineEndings(string ending)
    {
        if (ending is not ("\r\n" or "\n" or "\r")) throw new ArgumentOutOfRangeException(nameof(ending));
        if (!DiffEditCommandAvailable(out var side)) return;
        if (!CanEditText(side, _diffViews[side], "改行を変換できません")) return;
        var text = TextEditor(side).Text ?? "";
        var converted = NormalizeLineBreaks(text, ending);
        if (converted == text) { _status.Text = "改行は既にその形式です。"; return; }
        ApplyReplacedText(side, converted);
        _status.Text = "改行を変換しました。";
    }

    /// <summary>フォーカスのある側の、実在するファイルのパス。</summary>
    private string? FocusedPath()
    {
        var side = SearchSideIndex();
        var path = side switch { 0 => LeftPath.Text, 1 => BasePath.Text, _ => RightPath.Text };
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { _status.Text = "開くファイルがありません。"; return null; }
        return path;
    }

    /// <summary>パスを開く。検証用の差し替えが無ければ、Windows は関連付け、macOS は open、それ以外は xdg-open。</summary>
    private void LaunchExternal(string path)
    {
        if (ExternalLauncher is { } launch) { launch(path); return; }
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }
        var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        Process.Start(start);
    }
}
