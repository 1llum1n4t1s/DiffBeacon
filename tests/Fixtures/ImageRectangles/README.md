# 矩形画素編集の原本契約

WinIMerge v1.0.54 / revision `da639cdfaeca87aaad0eaceec509afa11ad61421` の `ImgMergeBuffer.hpp` から `DeleteRectangle`、`PasteImage`、`PasteImageInternal` を無改変抽出した核を、独立した二つのC++プロセスで実行した32件の観測。入力はCC0-1.0、抽出核・probeはGPL-2.0-or-later（`GPL-LICENSE.txt`）。原本全体は隣接 `ImageCopy/reference-source/ImgMergeBuffer.hpp` を共用する。

`source-manifest.json` のbyte範囲・SHA、`compiler.json`、`processes.json` と `summary.json` は初回採取の記録。原本全体SHAは `956B69A5D76D6918CD6D3245B5DB5BFDEF902C82221E2539FFEA1B33996AE40B`、無圧縮golden SHAは `E32876DC786381285E6FDA5363073A1C09881934AB1B9B8C9645FA5A132A14E2`。`cases.json` SHAは `371961F9AE6E88689C76DDFB76CB995F3E5239E1E1450DDE58FA3F7F588CFCE1`。gzip SHAは `1EC081CE7158F9D17C4A59E1218AA6D8EFF8D33210EF5A48B6D5463748D8D6AA`。

採取adapterはtop-to-bottom BGRA、identity方向、所有snapshotの履歴push数、再比較呼出し数を提供するshim。FreeImageの復号、実際のUndo/Redo、方向変換、領域分類、GUI/clipboard/floating pasteの動作はこのgoldenの証明範囲に含まれない。詳細は `contract.md`。

## 実装前の失敗条件と期待結果

- 削除は表示変換後の原画内の半開矩形を全BGRA=0にする。範囲外・反転矩形は拒否。空幅/空高さでも有効なら1履歴。
- 貼付けは所有した操作時の表示原画をBGRA memcpyし、負の座標・端の交差をclip。透明RGBもそのままコピー。同一画素・重なりなしでも1履歴。
- 無効pane・読取り専用先はAPIではfalseで変更なし。CLIの新規pane指定は全script検査で拒否。
- 原本の直接PasteImageは読取り専用を無視する。現アプリは安全境界を保ちfalseを返す明示差異。`paste-readonly`を原本一致と扱わない。
- 空source/targetは原本観測に含まれるが現アプリの画像寸法契約では不正。現行E2Eの原本画素照合から除外する。
- 正の完全右/下範囲外は原本が負source indexを作り得る未定義動作。goldenを作らず、現アプリでは安全な空重なりと1履歴にする。整数極値はlong交差で安全に処理する。
- 同pane貼付けは操作前の所有snapshotを使う。方向はoriented画素を変更しinverseでrawへ戻す。offsetと挿入削除のghostは編集座標に含めない。
- 共有履歴・dirty・保存点・全pane再比較・候補work/確保量・128履歴/256MiBを既存経路と共有。失敗/取消は確定前に止める。exportは全script/全stateが成功するまで公開しない。
- 新規actionは厳密schema。未知・重複属性、数値型不正、入力/scriptへの出力は拒否し既存出力を保持。

## 再採取と実行

MSVC x64の開発環境で `python tests/Fixtures/ImageRectangles/extract.py --output <空の採取先>`。既存の核/原本SHAを検査して同じC++probeを二回実行する。観測はC++出力のみから構築し、C#計算でgoldenを生成しない。コンパイラ環境は実行時の `cl` を使用する。原本採取は通常ビルドの依存ではない。

`dotnet run --project tests/DiffBeacon.E2E -c Release --no-build -- --image-rectangles-only --output <検証先>` で実アプリ別プロセスの全画素、入力/script保持、Undo/Redo、PNG独立再読込み、安全差異、schemaと予算拒否を検証する。限定実行は全体E2Eの代替ではない。

CLI actionは `{"kind":"delete-rectangle","destination":0,"rectangle":[0,0,4,3]}` または `{"kind":"paste-image","source":1,"destination":0,"x":-1,"y":0}`。新規actionResultは成功1、安全拒否0。旧actionと既定JSON構造は維持する。crop APIは現アプリ独自の所有BGRA切出しであり、FreeImage原本crop一致は未検証。
