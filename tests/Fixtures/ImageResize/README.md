# 貼り付け前のサイズ変更の原本参照

WinIMerge v1.0.54の`Resize`、`PasteImageInternal`、`TemporaryTransformation`、`TransformImages`をSHA固定の既存原本から無改変抽出する。入力はCC0-1.0、抽出核はGPL-2.0-or-later。原本全体とライセンスは隣接[矩形fixture](../ImageRectangles/README.md)を参照する。

失敗条件は、raw寸法によるno-op判定をoriented寸法へ置換すること、変換前の旧画素を変換済み画素へ置換すること、inverse変換の順序を逆にすること、resizeの独立履歴・再比較を失うこと。非正方形3×2、4回転×4反転×6寸法の96件を二つの独立C++プロセスで採取する。

adapterは所有BGRA、ゼロ初期化、直角回転・反転、履歴pushと再比較の呼出し数を提供する。FreeImageの公式SVN実装では[`fipImage::setSize`](https://svn.code.sf.net/p/freeimage/svn/FreeImage/trunk/Wrapper/FreeImagePlus/src/fipImage.cpp)が`FreeImage_AllocateT`を呼び、[`FreeImage_AllocateBitmap`](https://svn.code.sf.net/p/freeimage/svn/FreeImage/trunk/Source/FreeImage/BitmapAccess.cpp)が確保領域をゼロ初期化する。配布WinIMergeのFreeImageソースrevisionは未確定のため、このfixtureを配布DLLの全画素やOSクリップボードの実測とは扱わない。任意角度、FreeImageの実回転、実Undo/Redo、GUIの浮動状態は採取範囲外。

MSVC x64 Native Tools環境で`python tests/Fixtures/ImageResize/generate-reference.py --output <空の採取先>`を実行する。観測はC++の実行出力からのみ作り、固定gzipの展開後全bytesへ照合する。`--record`は初回採取専用で、既存goldenを上書きしない。通常buildはMSVCに依存しない。
