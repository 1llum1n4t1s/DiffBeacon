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
            || _lineDifferences != settings.ViewLineDifferences || _lineNumbers != settings.ViewLineNumbers || _margins != settings.ViewMargins;
        _zoomSteps = Math.Clamp(settings.ZoomSteps, MinimumZoomSteps, MaximumZoomSteps);
        _whitespace = settings.ViewWhitespace; _endOfLine = settings.ViewEndOfLine; _lineDifferences = settings.ViewLineDifferences;
        _lineNumbers = settings.ViewLineNumbers; _margins = settings.ViewMargins;
        if (changed) Changed?.Invoke();
    }

    /// <summary>保存する形へ写す。ツールバーの表示は本文の設定ではないので外枠から受け取る。</summary>
    internal TextViewSettings ToSettings(bool showToolbar, bool showStatusBar, bool showLocationPane, bool showDiffPane) => new()
    {
        ZoomSteps = _zoomSteps, ViewWhitespace = _whitespace, ViewEndOfLine = _endOfLine, ViewLineDifferences = _lineDifferences,
        ViewLineNumbers = _lineNumbers, ViewMargins = _margins, ShowToolbar = showToolbar,
        ShowStatusBar = showStatusBar, ShowLocationPane = showLocationPane, ShowDiffPane = showDiffPane,
    };

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed?.Invoke();
    }
}
