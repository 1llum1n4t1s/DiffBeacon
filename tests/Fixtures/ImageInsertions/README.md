# 画像の行・列挿入削除の原本観測

固定 WinIMerge DLL v1.0.54、revision `da639cdfaeca87aaad0eaceec509afa11ad61421`、SHA-256 `36F2A726C34A2323D2E569903B4F1DC6C28ABF1C10447006335F2785FC511BA6`。既定Myers一種の58ケース304状態を3回別プロセスで採取し、観測JSONの全bytes一致を確認した。これは移植の期待値であり、現行アプリの挿入削除検出の対応済み宣言ではない。

入力は自作CC0の極小RGBA。`SaveDiffImageAs`の全canvas PNG、`ConvertToRealPos`のcanvasと外周1画素の全座標、方向・offset・領域／競合数・dirty/savepoint・共有Undo/Redo、最終原画PNGを含む。DLLのprivate領域gridや行対応表を公開APIから取得したと称しない。垂直／水平の先頭・中央・末尾挿入、削除・置換・反復・一致・全変更、両方向copy、readonly、方向・mode・offset変更、三者autoと閾値・透明RGBを記録する。

失敗先行契約は、ghostをrawに焼き込む誤り、透明実画素との意味混同、逆座標と構造copy、Undoが表示modeを戻す誤り、readonly、左上拡張後offset、閾値equalsとhashの不整合。製品の将来検証は実CLI／GUI／設定／HTML／包装へ接続し、全画素と入力・既存出力の保持、処理量・確保上限、取消を確認する。

| 固定物 | SHA-256 |
| --- | --- |
| gzip | `2B0275994E7445F8BF4A745CF7DCE8BBF531C647D43095751F12AE6278A7AF21` |
| 展開JSON 824,480 bytes | `991FC3F5CBAA2B039D6320E85AB9ADE0F6BB2CE186FD5AFFC30BA43D4777EDDB` |
| 原本観測JSON | `BA3295C6880776C6663FE2AC3B75135A3902589D6754DAE24C12FBE10E441360` |

採取プログラムは `E:/DiffBeacon-artifacts/reference/image-insertions-v1.0.54/capture.py` と `state-function.cpp.txt`、最新原本は `run3`。以前の2回は全entryのSHA・サイズ確認済みZIPに保持する。[行算法の最新採取](../ImageLines/README.md)とともに `python -B tests/Fixtures/ImageInsertions/extract.py <DLL run3> <line-script run2>` で再生成する。gzipの時刻・ファイル名を固定し、展開後SHAも照合する。採取用DLL・C++・Pythonを製品依存へ加えない。原本に由来する観測・処理のライセンスは[GPL v2以降](../ImageRegions/LICENSE.txt)を参照する。
