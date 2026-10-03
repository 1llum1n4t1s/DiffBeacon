namespace DiffBeacon.App;

// 原本 WinIMerge の公開値。wipe の予約値は実装完了まで選択・保存しない。
internal enum ImageDragMode
{
    None = 0,
    Move = 1,
    AdjustOffset = 2,
    VerticalWipe = 3,
    HorizontalWipe = 4,
    RectangleSelect = 5
}
