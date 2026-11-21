using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DiffBeacon.Core;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace DiffBeacon.App;

/// <summary>印の文字列を、本家の利用者定義マーカー色（緑・赤・青）で塗る。</summary>
internal sealed class TextMarkerColorizer : DocumentColorizingTransformer
{
    /// <summary>塗る印。空なら何も塗らない。</summary>
    internal List<TextMarker> Markers { get; } = [];

    /// <summary>偽のときは印を記憶したまま塗らない。本家の Enable markers。</summary>
    internal bool Enabled { get; set; } = true;

    /// <summary>本家 COLORINDEX_MARKERBKGND1〜3。検索用の黄（BKGND0）は利用者の印には使わない。</summary>
    private static readonly IBrush[] Colors =
    [
        new SolidColorBrush(Color.FromRgb(127, 255, 127)),
        new SolidColorBrush(Color.FromRgb(255, 127, 127)),
        new SolidColorBrush(Color.FromRgb(127, 127, 255)),
    ];

    private Regex?[] _patterns = [];

    /// <summary>正規表現の印をコンパイルし直す。不正な式は、設定を入れたときに拒否済みである。</summary>
    internal void InvalidatePatterns()
    {
        _patterns = new Regex?[Markers.Count];
        for (var index = 0; index < Markers.Count; index++)
        {
            var marker = Markers[index];
            if (!marker.Regex || marker.Text.Length == 0) continue;
            try
            {
                PcrePattern.RejectUnsupported(marker.Text);
                var options = RegexOptions.CultureInvariant;
                if (!marker.MatchCase) options |= RegexOptions.IgnoreCase;
                _patterns[index] = new Regex(PcrePattern.RewritePossessive(marker.Text), options, TimeSpan.FromMilliseconds(250));
            }
            catch (Exception error) when (error is FormatException or ArgumentException or RegexParseException)
            {
                _patterns[index] = null;
            }
        }
    }

    /// <summary>行の中で印の文字列を塗る。</summary>
    protected override void ColorizeLine(DocumentLine line)
    {
        if (!Enabled || Markers.Count == 0 || CurrentContext is null) return;
        var text = CurrentContext.Document.GetText(line);
        for (var index = 0; index < Markers.Count; index++)
        {
            var marker = Markers[index];
            if (!marker.Visible || marker.Text.Length == 0) continue;
            var color = Colors[Math.Clamp(marker.ColorIndex, 0, Colors.Length - 1)];
            if (marker.Regex) PaintRegex(line, text, index, color);
            else PaintLiteral(line, text, marker, color);
        }
    }

    /// <summary>正規表現ではない印を塗る。</summary>
    private void PaintLiteral(DocumentLine line, string text, TextMarker marker, IBrush color)
    {
        var comparison = marker.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var index = 0;
        while ((index = text.IndexOf(marker.Text, index, comparison)) >= 0)
        {
            var end = index + marker.Text.Length;
            if (!marker.WholeWord || WholeWord(text, index, end))
                ChangeLinePart(line.Offset + index, line.Offset + end, element => element.BackgroundBrush = color);
            index = end;
        }
    }

    /// <summary>正規表現の印を塗る。空の一致は1文字進めて繰り返さない。</summary>
    private void PaintRegex(DocumentLine line, string text, int markerIndex, IBrush color)
    {
        if (markerIndex >= _patterns.Length || _patterns[markerIndex] is not { } pattern) return;
        try
        {
            foreach (Match match in pattern.Matches(text))
            {
                if (match.Length == 0) continue;
                ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length, element => element.BackgroundBrush = color);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // 制限時間を超えた印はこの行だけ塗らない。式自体は残す。
        }
    }

    /// <summary>印の両隣が単語の文字でなければ、単語単位とみなす。</summary>
    private static bool WholeWord(string text, int start, int end)
        => (start == 0 || !IsWord(text[start - 1])) && (end >= text.Length || !IsWord(text[end]));

    /// <summary>単語の文字。文字、数字、アンダースコア。</summary>
    private static bool IsWord(char value) => char.IsLetterOrDigit(value) || value == '_';
}

public sealed partial class ComparisonPane
{
    /// <summary>検証用。設定すると、関連付けで開く代わりにパスを受け取る。</summary>
    internal Action<string>? ExternalLauncher { get; set; }

    /// <summary>検証用。設定すると、置換フィルターの確認をこの関数で行う。</summary>
    internal Func<string, Task<bool>>? SubstitutionConfirmation { get; set; }

    /// <summary>表示フィルター。本家の「Line contains "…" AND …」。</summary>
    private string _displayFilter = "";

    /// <summary>競合ファイルを開いたときに作った一時ファイル。ペインを閉じるときに消す。</summary>
    private readonly List<string> _conflictTemps = [];
    private readonly List<string> _conflictTempDirectories = [];

    /// <summary>いまの行フィルター（除外行の正規表現）。</summary>
    internal string LineFilterPattern { get => _ignoreRegex.Text ?? ""; set => _ignoreRegex.Text = value; }

    /// <summary>いまの表示フィルター。</summary>
    internal string DisplayFilter => _displayFilter;

    /// <summary>表示フィルターを外す。</summary>
    internal void ClearDisplayFilter()
    {
        _displayFilter = "";
        ApplyDiffContext();
    }

    /// <summary>印にした文字列。</summary>
    internal IReadOnlyList<string> TextMarkers => _diffViews[0].MarkerColorizer.Markers.Select(marker => marker.Text).ToArray();

    /// <summary>印の色番号。0 が緑、1 が赤、2 が青。</summary>
    internal IReadOnlyList<int> TextMarkerColors => _diffViews[0].MarkerColorizer.Markers.Select(marker => marker.ColorIndex).ToArray();

    /// <summary>印を塗るか。一覧のチェックを外した印は偽。</summary>
    internal IReadOnlyList<bool> TextMarkerVisible => _diffViews[0].MarkerColorizer.Markers.Select(marker => marker.Visible).ToArray();

    /// <summary>自己検証が、印のダイアログが出たときにボタンを押す。</summary>
    internal Action<Window>? MarkerDialogShown { get; set; }

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
        var newline = "\n";
        foreach (var line in destinationLines)
            if (line.Ending is { Length: > 0 } ending) { newline = ending; break; }
        var joined = new StringBuilder();
        for (var index = 0; index < output.Count; index++)
        {
            joined.Append(output[index].Content);
            var ending = output[index].Ending ?? "";
            if (ending.Length > 0) joined.Append(ending);
            else if (index < output.Count - 1) joined.Append(newline);
        }
        ApplyReplacedText(destinationSide, joined.ToString());
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
        if (string.IsNullOrEmpty(text) && PrepareTextSearch(side, out var view, out var real))
            text = SelectedOrWordAtCaret(view, real, TextSearcher.Lines(real));
        if (string.IsNullOrEmpty(text)) { _status.Text = "表示フィルターに追加する文字列がありません。"; return; }
        var clause = "Line contains \"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        _displayFilter = _displayFilter.Length == 0 ? clause : _displayFilter + " AND " + clause;
        _displayFilterBox.Text = _displayFilter;
        SetDisplayFilterBarVisible(true);
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
    private bool DisplayFilterHides(DiffDisplayModel model, int row, List<(string Content, string Ending)>?[] lines, IReadOnlyList<string> phrases)
    {
        if (phrases.Count == 0) return false;
        for (var side = 0; side < 3; side++)
        {
            if (side == 1 && !model.ThreeWay) continue;
            if (NumberAt(model, side, row) is not int number) continue;
            var sideLines = lines[side];
            if (sideLines is null || number < 1 || number > sideLines.Count) continue;
            if (phrases.All(phrase => sideLines[number - 1].Content.Contains(phrase, StringComparison.Ordinal))) return false;
        }
        return true;
    }

    /// <summary>表示フィルター用に、各側の行を一度だけ分ける。</summary>
    private List<(string Content, string Ending)>?[] DisplayFilterLines(DiffDisplayModel model)
    {
        var lines = new List<(string Content, string Ending)>[3];
        for (var side = 0; side < 3; side++)
        {
            if (side == 1 && !model.ThreeWay) continue;
            lines[side] = DiffEditorSide.SplitLines(model.Texts[side]);
        }
        return lines;
    }

    /// <summary>「Line contains "…"」の中身。\" と \\ を戻す。</summary>
    private List<string> DisplayFilterPhrases()
    {
        var phrases = new List<string>();
        const string marker = "Line contains \"";
        var index = 0;
        while ((index = _displayFilter.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            index += marker.Length;
            var phrase = ReadQuoted(_displayFilter, index, out var end);
            if (end < 0) break;
            phrases.Add(phrase);
            index = end + 1;
        }
        return phrases;
    }

    /// <summary>引用符の中を、バックスラッシュの次の文字をそのまま読んで取り出す。</summary>
    private static string ReadQuoted(string text, int start, out int end)
    {
        var builder = new StringBuilder();
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] == '\\' && index + 1 < text.Length) { builder.Append(text[index + 1]); index++; continue; }
            if (text[index] == '"') { end = index; return builder.ToString(); }
            builder.Append(text[index]);
        }
        end = -1;
        return "";
    }

    /// <summary>印のダイアログを出す。選択があれば、本家と同じく新しい印として入れてから出す。</summary>
    internal async Task AddTextMarkerAsync()
    {
        var source = _owner is MainWindow window ? window.TextMarkers : _diffViews[0].MarkerColorizer.Markers;
        var enabled = _owner is not MainWindow host || host.MarkersEnabled;
        var draft = source.Select(marker => marker with { }).ToList();
        var selection = SelectedLiteral();
        if (selection.Length > 0) draft.Add(new TextMarker(selection, draft.Count % 3, false, false, false, true));
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) { _status.Text = "印の画面を出せません。"; return; }
        var accepted = await ShowMarkerDialogAsync(owner, draft, enabled);
        if (!accepted) return;
        _status.Text = "印を付けました。";
    }

    /// <summary>印を全部消す。</summary>
    internal void ClearTextMarkers()
    {
        if (_owner is MainWindow window) window.ReplaceTextMarkers(window.MarkersEnabled, []);
        else ApplySharedMarkers([], true);
    }

    /// <summary>ウィンドウが覚えている印を、この比較の全ペインへ写す。</summary>
    internal void ApplySharedMarkers(IReadOnlyList<TextMarker> markers, bool enabled)
    {
        foreach (var side in _diffViews)
        {
            side.MarkerColorizer.Markers.Clear();
            side.MarkerColorizer.Markers.AddRange(markers);
            side.MarkerColorizer.Enabled = enabled;
            side.MarkerColorizer.InvalidatePatterns();
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

    /// <summary>フォーカスのある側のファイルを、設定した外部エディターで開く。$file と $linenum を置き換える。</summary>
    internal void OpenWithExternalEditor()
    {
        if (FocusedPath() is not { } path) return;
        var line = 1;
        if (DiffTabActive() && CurrentDiff is not null && _diffDisplay.RowCount > 0)
        {
            var view = _diffViews[SearchSideIndex()];
            line = Math.Max(1, view.RealLineOfDisplay(view.View.TextArea.Caret.Line) + 1);
        }
        var command = (_owner as MainWindow)?.ExternalEditorCommand ?? "";
        if (command.Length == 0)
        {
            if (!OperatingSystem.IsWindows()) { _status.Text = "外部エディターは設定されていません。"; return; }
            command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        }
        var expanded = ExpandEditorCommand(command, path, line);
        (_owner as MainWindow)?.AppendOutput(expanded);
        if (ExternalLauncher is { } launch) { launch(expanded); _status.Text = "外部エディターを起動しました。"; return; }
        try
        {
            var (file, arguments) = SplitCommand(expanded);
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = false, Arguments = arguments });
            _status.Text = "外部エディターを起動しました。";
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            _status.Text = "外部エディターを起動できませんでした: " + ex.Message;
        }
    }

    /// <summary>外部エディターのコマンドの $file と $linenum を、開くファイルと行へ置き換える。引用符は $file から次の空白まで。</summary>
    internal static string ExpandEditorCommand(string command, string file, int line)
    {
        var expanded = Environment.ExpandEnvironmentVariables(command).Replace("$linenum", line.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var clean = file.Replace("\"", "");
        var index = expanded.IndexOf("$file", StringComparison.Ordinal);
        if (index < 0) return expanded + " \"" + clean + "\"";
        expanded = expanded.Replace("$file", "\"" + clean, StringComparison.Ordinal);
        var closeAt = expanded.IndexOf(' ', index + clean.Length + 1);
        return closeAt < 0 ? expanded + "\"" : expanded.Insert(closeAt, "\"");
    }

    /// <summary>コマンド文字列を、実行ファイルと引数へ分ける。</summary>
    private static (string FileName, string Arguments) SplitCommand(string command)
    {
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return (text.Trim('"'), "");
            return (text[1..end], text[(end + 1)..].Trim());
        }
        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }

    /// <summary>定義へ移動する記号索引は無い。</summary>
    internal void GoToDefinition() => _status.Text = "定義へ移動する言語サービスはありません。";

    /// <summary>比較した実在ファイルを、外枠の最近使った一覧へ足す。</summary>
    private void NoteOpenedPaths()
    {
        if (_owner is MainWindow window) window.RememberRecent(LeftPath.Text, BasePath.Text, RightPath.Text);
    }

    /// <summary>状態欄へ理由を出す。</summary>
    internal void SetStatus(string text) => _status.Text = text;

    /// <summary>形式を指定して比較し直す。0 自動、1 テキスト、3 バイナリ、4 画像、6 表、7 アーカイブ。取消のときは形式を変えない。</summary>
    internal async Task RecompareAsAsync(int mode)
    {
        if (mode is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(mode));
        if (HasUnsavedChanges && !await Dialogs.ConfirmAsync(_owner, "未保存の変更", "編集内容を破棄してファイルを開き直しますか？")) return;
        if (HasUnsavedChanges) DiscardChanges();
        SelectMode(mode);
        await ComparePathsAsync();
    }

    /// <summary>フォーカスのある側。二者比較で中央が選ばれていても左か右へ寄せる。</summary>
    internal int FocusedTextSide => SearchSideIndex();

    /// <summary>フォーカスのある側の保存文字コードを変える。本文は変えない。保存するまで未保存にする。</summary>
    internal void SetSideEncoding(int side, string name)
    {
        if (TextEditor(side).IsReadOnly) { _status.Text = "読取り専用のため文字コードを変えられません。"; return; }
        var text = TextEditor(side).Text ?? "";
        var previous = TextInputDocument(side);
        var next = (previous ?? DiffBeacon.Core.TextDocument.Create(text)).WithEncoding(name, text);
        switch (side)
        {
            case 0: _leftDocument = next; break;
            case 1: _baseDocument = next; break;
            case 2: _rightDocument = next; break;
            default: throw new ArgumentOutOfRangeException(nameof(side));
        }
        if (previous is null || previous.EncodingName != next.EncodingName || previous.HasBom != next.HasBom)
            _encodingUnsaved[side] = true;
        _status.Text = $"文字コードを {next.EncodingName}{(next.HasBom ? " BOM" : "")} にしました。";
    }

    /// <summary>フォーカスのある側の保存文字コード。</summary>
    internal (string Name, bool Bom) SideEncoding(int side)
    {
        var document = TextInputDocument(side);
        return document is null ? ("", false) : (document.EncodingName, document.HasBom);
    }

    /// <summary>クリップボードの文字を、フォーカスのある側へ入れて比較し直す。読取り専用は拒み、未保存なら確認する。</summary>
    internal async Task OpenClipboardTextAsync()
    {
        if (!DiffEditCommandAvailable(out var side)) return;
        if (!CanEditText(side, _diffViews[side], "クリップボードを開けません")) return;
        if (DiffClipboard() is not { } clipboard) { _status.Text = "クリップボードを利用できません。"; return; }
        var text = await clipboard.ReadTextAsync();
        if (_disposed) return;
        if (string.IsNullOrEmpty(text)) { _status.Text = "クリップボードに文字がありません。"; return; }
        if (text.Contains('\0')) { _status.Text = "クリップボードの NUL は本文にできません。"; return; }
        if (!CanEditText(side, _diffViews[side], "クリップボードを開けません")) return;
        var saved = side switch { 0 => _savedLeft, 1 => _savedMiddle, _ => _savedRight };
        if ((TextEditor(side).Text ?? "") != saved && !await Dialogs.ConfirmAsync(_owner, "未保存の変更", "編集内容を破棄してクリップボードの文字で置き換えますか？")) return;
        if (!CanEditText(side, _diffViews[side], "クリップボードを開けません")) return;
        ApplyReplacedText(side, text);
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

    /// <summary>競合ファイルを選んで開く。</summary>
    internal async Task OpenConflictFromDialogAsync()
    {
        var selected = (await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "コンフリクト ファイルを開く", AllowMultiple = false }))
            .Select(file => file.TryGetLocalPath()).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(selected)) return;
        await OpenConflictFileAsync(selected);
    }

    /// <summary>
    /// 競合ファイルを、相手を左、自分を右へ開く。三者の印があれば祖先を中央へ置く。
    /// 左と祖先は読取り専用。右は未保存にし、保存すると元の競合ファイルを上書きする。
    /// </summary>
    internal async Task OpenConflictFileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) { _status.Text = "開くファイルがありません。"; return; }
        var document = await DiffBeacon.Core.TextDocument.LoadAsync(full);
        if (!ConflictFileParser.TryParse(document.Text, out var sides))
        {
            _status.Text = $"ファイル\n{full}\nはコンフリクト ファイルではありません。";
            return;
        }
        if (HasUnsavedChanges && !await Dialogs.ConfirmAsync(_owner, "未保存の変更", "編集内容を破棄してファイルを開き直しますか？")) return;
        DiscardChanges();
        DeleteConflictTemps();
        var folder = Path.Combine(Path.GetTempPath(), "DiffBeacon-conflict-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _conflictTempDirectories.Add(folder);
        var extension = Path.GetExtension(full);
        var theirsPath = Path.Combine(folder, "confv_" + Guid.NewGuid().ToString("N") + extension);
        var minePath = Path.Combine(folder, "confw_" + Guid.NewGuid().ToString("N") + extension);
        var basePath = Path.Combine(folder, "confb_" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            _conflictTemps.Add(theirsPath);
            await WriteConflictSideAsync(theirsPath, document.CaptureBytes(sides.Theirs));
            _conflictTemps.Add(minePath);
            await WriteConflictSideAsync(minePath, document.CaptureBytes(sides.Mine));
            if (sides.ThreeWay)
            {
                _conflictTemps.Add(basePath);
                await WriteConflictSideAsync(basePath, document.CaptureBytes(sides.Base));
            }
        }
        catch
        {
            DeleteConflictTemps();
            throw;
        }
        SelectIndependentText(false);
        SelectMode(1);
        LeftPath.Text = theirsPath;
        BasePath.Text = sides.ThreeWay ? basePath : "";
        RightPath.Text = minePath;
        _projectMetadata = _projectMetadata with
        {
            LeftDescription = "Theirs File",
            RightDescription = "Mine File",
            BaseDescription = sides.ThreeWay ? "Base File" : null,
        };
        await ComparePathsAsync();
        if ((_rightDocument?.Text ?? "") != sides.Mine || (_leftDocument?.Text ?? "") != sides.Theirs)
        { _status.Text = "競合ファイルを比較に採用できませんでした。"; return; }
        _conflictSavePath = full;
        _conflictSourcePath = RightPath.Text;
        SetManualReadOnly(0, true);
        SetManualReadOnly(2, false);
        _leftCaption.Text = "Theirs File（読取り専用）";
        _rightCaption.Text = "Mine File";
        if (_owner is MainWindow window)
        {
            window.DropRecent(theirsPath);
            window.DropRecent(minePath);
            if (sides.ThreeWay) window.DropRecent(basePath);
            window.RememberRecent(full);
        }
        _status.Text = "競合ファイルを開きました。保存すると元のファイルを上書きします。";
    }

    /// <summary>保存文字コードを変えた側。本文が同じでも閉じる前に確認する。</summary>
    private readonly bool[] _encodingUnsaved = [false, false, false];

    /// <summary>文字コードの変更がまだ保存されていないか。</summary>
    internal bool EncodingUnsaved => _encodingUnsaved[0] || _encodingUnsaved[1] || _encodingUnsaved[2];

    /// <summary>文字コードの未保存を消す。比較を開き直したときと破棄したとき。</summary>
    internal void ClearEncodingUnsaved() => Array.Clear(_encodingUnsaved);

    /// <summary>その側の文字コードを保存済みにする。</summary>
    internal void ClearEncodingUnsaved(int side) => _encodingUnsaved[side] = false;

    /// <summary>競合ファイルの右を保存するとき、解析した一時ファイルではなく元のファイルへ書く。</summary>
    private string? _conflictSavePath;

    /// <summary>その保存先が有効な、右に開いている解析済みファイル。</summary>
    private string? _conflictSourcePath;

    /// <summary>右の保存先。右のパスが解析済みファイルのままのときだけ、元の競合ファイルを返す。</summary>
    internal string? ConflictSavePath
    {
        get
        {
            if (_conflictSavePath is null || _conflictSourcePath is null) return null;
            if (SamePath(RightPath.Text, _conflictSourcePath)) return _conflictSavePath;
            _conflictSavePath = null;
            _conflictSourcePath = null;
            return null;
        }
    }

    /// <summary>右のパスが解析済みファイルでなくなったら、競合ファイルへの保存先を消す。</summary>
    internal void ReleaseConflictSaveUnlessCurrent()
    {
        if (_conflictSourcePath is null) return;
        if (!SamePath(RightPath.Text, _conflictSourcePath)) ClearConflictSavePath();
    }

    /// <summary>競合ファイルの保存先を消す。別のファイルを開いたときと、保存が終わったあと。</summary>
    internal void ClearConflictSavePath()
    {
        _conflictSavePath = null;
        _conflictSourcePath = null;
    }

    /// <summary>同じファイルを指すパスか。</summary>
    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>競合の作業ファイルを、本人だけが読める新規ファイルとして書く。</summary>
    private static async Task WriteConflictSideAsync(string path, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(bytes);
    }

    /// <summary>競合ファイル用の一時ファイルを消す。消せないものは覚え直し、次の削除でやり直す。</summary>
    private void DeleteConflictTemps()
    {
        var failed = new List<string>();
        var keptFiles = new List<string>();
        foreach (var path in _conflictTemps)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException ex) { failed.Add(path + ": " + ex.Message); keptFiles.Add(path); }
            catch (UnauthorizedAccessException ex) { failed.Add(path + ": " + ex.Message); keptFiles.Add(path); }
        }
        _conflictTemps.Clear();
        _conflictTemps.AddRange(keptFiles);
        var keptFolders = new List<string>();
        foreach (var folder in _conflictTempDirectories)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: false);
            }
            catch (IOException ex) { failed.Add(folder + ": " + ex.Message); keptFolders.Add(folder); }
            catch (UnauthorizedAccessException ex) { failed.Add(folder + ": " + ex.Message); keptFolders.Add(folder); }
        }
        _conflictTempDirectories.Clear();
        _conflictTempDirectories.AddRange(keptFolders);
        if (failed.Count > 0 && !_disposed) _status.Text = "一時ファイルを消せませんでした。 " + string.Join(" ", failed);
    }
}
