namespace DiffBeacon.App;

/// <summary>
/// 本家WinMergeの「表示」メニューのうち、ファイル比較の本文の見た目を変える設定（拡大／縮小・空白・改行・行内差異・行番号・マージン）。
///
/// 設計の要点:
/// - 本家と同じくアプリ全体で一つの設定で、すべての比較タブ（後から開くタブを含む）の全ペインへ同時に反映する。
///   <see cref="MainWindow"/> が一つ持ち、各 <see cref="ComparisonPane"/> は <see cref="Changed"/> を購読して見た目をそろえる。
/// - 本家のレジストリと同じく、起動時に <see cref="TextViewOptionsStore"/>（view-options.json）から読み、変えるたびに保存する。
///   保存は外枠が <see cref="Changed"/> を受けて行う。headless検証はメモリだけの保存先を使い、利用者の設定ファイルを書き換えない。
/// - 拡大／縮小は本家 CMergeEditView::ZoomText と同じく 1 ポイントずつ変え、2 ポイント未満と本家 OPT_VIEW_ZOOM の上限（800%）を超えない。
///   基準は差分エディターの既定の文字の大きさ（13 DIP）で、「標準に戻す」はこの大きさへ戻す。
/// - 既定値は本家と同じく空白・改行は表示しない、行内差異は表示する。行番号とマージン（ブックマークの印の欄）は、
///   本家の既定（非表示）と異なり DiffBeacon の従来どおり表示する（行番号・ブックマークの既存の表示と検証を変えないため）。
/// </summary>
internal sealed class DiffTextViewOptions
{
    /// <summary>差分エディターの既定の文字の大きさ（DIP）。</summary>
    internal const double BaseFontSize = 13;
    /// <summary>フォントを指定していないときの本文フォント（等幅の候補）。</summary>
    internal const string DefaultFontFamilyName = "Cascadia Mono, Menlo, Consolas, monospace";
    /// <summary>本家の Diff コンテキストで選べる行数。-1 は全行で、この配列には入れない。</summary>
    internal static readonly int[] DiffContextChoices = [0, 1, 3, 5, 7, 9];
    private const double DipPerPoint = 96.0 / 72.0;
    private const double BasePoints = BaseFontSize / DipPerPoint;
    /// <summary>縮小の下限（本家と同じく 2 ポイント以上）。</summary>
    internal static readonly int MinimumZoomSteps = -(int)Math.Floor(BasePoints - 2);
    /// <summary>拡大の上限（本家 OPT_VIEW_ZOOM の最大 8000‰）。</summary>
    internal static readonly int MaximumZoomSteps = (int)Math.Floor(BasePoints * 8 - BasePoints);

    private int _zoomSteps;
    private bool _whitespace;
    private bool _endOfLine;
    private bool _lineDifferences = true;
    private bool _lineNumbers = true;
    private bool _margins = true;
    private bool _wordWrap;
    private string _fontFamily = "";
    private bool _syntaxHighlight;
    private int _diffContextLines = -1;
    private int _diffContextLast;
    private bool _diffContextInvert;
    private bool _verticalSplit = true;
    private int _lockedSide = -1;

    /// <summary>設定が変わったとき。購読する側は見た目だけを変え、本文・履歴・比較結果は変えない。</summary>
    internal event Action? Changed;

    /// <summary>既定の大きさから何ポイント拡大しているか（負は縮小）。</summary>
    internal int ZoomSteps => _zoomSteps;
    /// <summary>本文に使う文字の大きさ（DIP）。</summary>
    internal double FontSize => BaseFontSize + _zoomSteps * DipPerPoint;
    /// <summary>既定の大きさに対する倍率（行番号の欄など、本文に合わせて大きさを変える部品が使う）。</summary>
    internal double Scale => FontSize / BaseFontSize;
    internal bool ViewWhitespace { get => _whitespace; set => Set(ref _whitespace, value); }
    internal bool ViewEndOfLine { get => _endOfLine; set => Set(ref _endOfLine, value); }
    internal bool ViewLineDifferences { get => _lineDifferences; set => Set(ref _lineDifferences, value); }
    internal bool ViewLineNumbers { get => _lineNumbers; set => Set(ref _lineNumbers, value); }
    internal bool ViewMargins { get => _margins; set => Set(ref _margins, value); }
    /// <summary>行を右端で折り返す。折り返し中のスクロール同期は画素ではなく表示の行番号でそろえる。</summary>
    internal bool WordWrap { get => _wordWrap; set => Set(ref _wordWrap, value); }
    /// <summary>本文のフォント名。空なら <see cref="DefaultFontFamilyName"/>。</summary>
    internal string FontFamily
    {
        get => _fontFamily;
        set
        {
            var next = (value ?? "").Trim();
            if (!IsFontFamilyName(next)) throw new ArgumentException("フォント名は200文字以下の1行です。", nameof(value));
            Set(ref _fontFamily, next);
        }
    }
    /// <summary>拡張子に合う AvaloniaEdit のシンタックスハイライトを本文へ付ける。</summary>
    internal bool SyntaxHighlight { get => _syntaxHighlight; set => Set(ref _syntaxHighlight, value); }
    /// <summary>差異の前後に残す行数。-1 は全行。</summary>
    internal int DiffContextLines => _diffContextLines;
    /// <summary>全行へ戻す前に選んでいた 0～9 行。Ctrl+D の切替先。</summary>
    internal int DiffContextLastLines => _diffContextLast;
    /// <summary>真のときはコンテキストの内側（差異行側）を隠し、外側を出す。</summary>
    internal bool DiffContextInvert { get => _diffContextInvert; set => Set(ref _diffContextInvert, value); }
    /// <summary>真のときは左右（垂直分割）、偽のときは上下に並べる。</summary>
    internal bool VerticalSplit { get => _verticalSplit; set => Set(ref _verticalSplit, value); }
    /// <summary>スクロールを同期しない側。-1 はロックなし。0=左、1=中央、2=右。</summary>
    internal int LockedSide
    {
        get => _lockedSide;
        set
        {
            if (value is < -1 or > 2) throw new ArgumentOutOfRangeException(nameof(value));
            Set(ref _lockedSide, value);
        }
    }

    /// <summary>フォント名として保存できる文字列か。</summary>
    internal static bool IsFontFamilyName(string name) => name.Length <= 200 && !name.Contains('\r') && !name.Contains('\n');

    /// <summary>Diff コンテキストの行数を採用する。0～9 を選ぶと、全行からの切替先もその行数になる。</summary>
    internal void SetDiffContext(int lines)
    {
        if (lines != -1 && !DiffContextChoices.Contains(lines)) throw new ArgumentOutOfRangeException(nameof(lines));
        if (lines >= 0) _diffContextLast = lines;
        Set(ref _diffContextLines, lines);
    }

    /// <summary>本家 Ctrl+D。全行と、直前に選んだ 0～9 行を交互に切り替える。</summary>
    internal void ToggleDiffContext()
    {
        if (_diffContextLines < 0) SetDiffContext(_diffContextLast);
        else SetDiffContext(-1);
    }

    /// <summary>
    /// 本家 ZoomText(amount) と同じ。正は拡大、負は縮小、0 は標準に戻す。上限・下限で止まったときは何も変えない。
    /// </summary>
    internal void Zoom(int amount)
    {
        var next = amount == 0 ? 0 : Math.Clamp(_zoomSteps + amount, MinimumZoomSteps, MaximumZoomSteps);
        Set(ref _zoomSteps, next);
    }

    /// <summary>保存した設定をまとめて採用する（変わったときだけ <see cref="Changed"/> を一度だけ出す）。値は保存先で確かめ済みのもの。</summary>
    internal void Load(TextViewSettings settings)
    {
        var changed = _zoomSteps != settings.ZoomSteps || _whitespace != settings.ViewWhitespace || _endOfLine != settings.ViewEndOfLine
            || _lineDifferences != settings.ViewLineDifferences || _lineNumbers != settings.ViewLineNumbers || _margins != settings.ViewMargins
            || _wordWrap != settings.WordWrap || _fontFamily != settings.FontFamily || _syntaxHighlight != settings.SyntaxHighlight
            || _diffContextLines != settings.DiffContextLines || _diffContextLast != settings.DiffContextLastLines
            || _diffContextInvert != settings.DiffContextInvert || _verticalSplit != settings.VerticalSplit || _lockedSide != settings.LockedSide;
        _zoomSteps = Math.Clamp(settings.ZoomSteps, MinimumZoomSteps, MaximumZoomSteps);
        _whitespace = settings.ViewWhitespace; _endOfLine = settings.ViewEndOfLine; _lineDifferences = settings.ViewLineDifferences;
        _lineNumbers = settings.ViewLineNumbers; _margins = settings.ViewMargins;
        _wordWrap = settings.WordWrap; _fontFamily = settings.FontFamily; _syntaxHighlight = settings.SyntaxHighlight;
        _diffContextLines = settings.DiffContextLines; _diffContextLast = settings.DiffContextLastLines;
        _diffContextInvert = settings.DiffContextInvert; _verticalSplit = settings.VerticalSplit; _lockedSide = settings.LockedSide;
        if (changed) Changed?.Invoke();
    }

    /// <summary>保存する形へ写す。ツールバーの表示は本文の設定ではないので外枠から受け取る。</summary>
    internal TextViewSettings ToSettings(bool showToolbar, bool showStatusBar, bool showLocationPane, bool showDiffPane, bool showTabBar, bool showMenuBar) => new()
    {
        ZoomSteps = _zoomSteps, ViewWhitespace = _whitespace, ViewEndOfLine = _endOfLine, ViewLineDifferences = _lineDifferences,
        ViewLineNumbers = _lineNumbers, ViewMargins = _margins, ShowToolbar = showToolbar,
        ShowStatusBar = showStatusBar, ShowLocationPane = showLocationPane, ShowDiffPane = showDiffPane,
        WordWrap = _wordWrap, FontFamily = _fontFamily, SyntaxHighlight = _syntaxHighlight,
        DiffContextLines = _diffContextLines, DiffContextLastLines = _diffContextLast, DiffContextInvert = _diffContextInvert,
        VerticalSplit = _verticalSplit, LockedSide = _lockedSide, ShowTabBar = showTabBar, ShowMenuBar = showMenuBar,
    };

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed?.Invoke();
    }
}
