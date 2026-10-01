# WinIMerge 画像強調 fixture

公式 WinMerge/winimerge `v1.0.54`、commit `da639cdfaeca87aaad0eaceec509afa11ad61421` の原本核と `GetDiffColorFromPosition`／`MarkDiff` を本体 bytes 無改変で実行した72件の固定結果。期待値は描画後の raw BGRA 全画素で、PNG 化や C# 出力は使わない。

| 保存物 | SHA-256 |
| --- | --- |
| `winimerge-image-highlight-golden.json` | `853A08102656CD1F726E39647D98AF47CF4C8918C7F3EB051D078FEA87BC057A` |
| canonical `../ImageRegions/reference-source/ImgDiffBuffer.hpp` | `7071EDFA31CCC138685CA7B9FD3D41EE5E84B942FB3D8028C7E3A31C76F93F28` |
| canonical `../ImageRegions/reference-source/image.hpp` | `173CE00FE65187D34A6C7D2B10C0B45443EA6CFFBAA50FE1263BCE5840149609` |
| `probe-adapter.cpp` | `E04CC8719DD65CF6284FEC2F4BFF6AF0F693BDFE6BC6DBD0888811EDA77A5F57` |

原本・GPL は既存 [ImageRegions の出典](../ImageRegions/README.md) と [GPL v2 本文](../ImageRegions/LICENSE.txt) を canonical として参照する。原本ライセンスは GPL v2 以降。元ファイルと164件の分類 golden は変更しない。

## 採取範囲

`ImgDiffBuffer.hpp` 33–161行の型、479–481行の挿入削除 enum、1663–1693行の初期化、1702–1886行の比較／領域分類、1888–1916行の `GetDiffColorFromPosition`、1918–1964行の `MarkDiff` を bytes ごと抜粋する。色 adapter では `image.hpp` 384–395行の `valueR/G/B/A` と `Rgb` も無改変で抜粋する。範囲と SHA は `extraction.json`。

adapter の `Color` は原本 `RGBQUAD` と同じ Blue／Green／Red／Reserved の各 unsigned byte を持つ。入力の scanLine は上から下で、offset は全画像0。各画像を共通最大 width／height の canvas 左上へコピーし、残りは BGRA=0 の透明 padding にする。その canvas に原本 `MarkDiff` を各 pane 一度だけ呼ぶ。

通常色は原本 constructor と同じ RGB=(255,255,64)、選択色は(255,64,64)。原本の選択削除色・削除色も初期化するが、挿入削除 mode NONE のため通常／選択色が返る。変換・回転・位置合わせ・overlay・wipe・blink は実行しない。FreeImage 復号・旧 GUI・ディスプレイ compositing の実測ではない。

- 非透明画素は RGB を `byte(original*(1-alpha)+color*alpha)` にして元 alpha を保持する。半透明も同じ経路。
- 完全透明画素は RGB を色へ置換し、alpha は `byte(255*alpha)` にする。強調 alpha=0 でも RGB は置換する。
- 左 pane は RightOnly 領域を、右 pane は LeftOnly 領域を強調しない。中央 pane は全差分領域を強調する。二者は両 pane を強調する。
- 左／中央／右のみ、Conflict、隣接分類混在、離れた二者領域、alpha=0/128/255、共通 canvas padding、block8 の9／17画素端を採取する。
- 8種入力に highlight alpha=0/.3/.7/1 と選択=-1/0 を掛けた64件、各入力の alpha=.7・最終領域選択8件で計72件。選択されない色・最初と最終領域の色を区別する。

## JSON と再生成

各 case は `blockSize`、比較の `threshold`、強調の `highlightAlpha`、0始まりの `selectedDiffIndex`（-1 または0、`"last"`）、上から下の raw BGRA `images` を持つ。`expected` は分類 fixture と同じ pair grid／regionIds／regions／counts に、解決後の `currentDiffIndex` と pane 順 `processed` を追加する。processed は共通 canvas 寸法、全画素 `bgraBase64`、その bytes の `sha256`。矩形は block 座標・右／下排他的境界。

```powershell
uv run --no-project python tests/Fixtures/ImageHighlight/generate-reference.py --output artifacts/verification/image-highlight-reference/reproduced
Get-FileHash tests/Fixtures/ImageHighlight/winimerge-image-highlight-golden.json
Get-FileHash artifacts/verification/image-highlight-reference/reproduced/winimerge-image-highlight-golden.json
```

既存 MSVC `14.51.36231` と Windows SDK `10.0.28000.0`、Python 3 を採取時だけ使う。通常 build／E2E に C++、FreeImage、DLL、submodule を追加しない。固定版の明示更新時だけ `--golden tests/Fixtures/ImageHighlight/winimerge-image-highlight-golden.json` を付ける。

入力 JSON／plain probe-input、原本出力 JSONL、compiler log、stderr log、抽出 inc／SHA、metadata、golden を指定出力先に保持する。先行失敗契約は `artifacts/verification/image-highlight-reference/failure-contract.md`。2026-10-01の native 採取と再採取は compiler/probe exit=0、72件の golden bytes 一致。原本抜粋一致・画素SHA・寸法・選択・未強調画素・非透明alpha保持など745検証成功・0失敗を同ディレクトリ `assertions.json` に保持した。

製品 GUI／HTML／managed／Native AOT との一致、復号後の表示、macOS は親の統合工程であり、この採取成功には含めない。
