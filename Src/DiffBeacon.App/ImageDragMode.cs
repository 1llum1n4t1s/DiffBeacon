namespace DiffBeacon.App;

// 原本 WinIMerge の公開値。縦/横ワイプは表示だけを変更する。
internal enum ImageDragMode
{
    None = 0,
    Move = 1,
    AdjustOffset = 2,
    VerticalWipe = 3,
    HorizontalWipe = 4,
    RectangleSelect = 5
}
