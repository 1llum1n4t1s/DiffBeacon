namespace DiffBeacon.App;

// 表示変換後の原画内にある半開区間。比較キャンバスの位置・整列ghostを含めない。
internal readonly record struct ImageRectangle(int Left, int Top, int Right, int Bottom);
