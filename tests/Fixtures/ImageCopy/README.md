# WinIMerge 静止画像領域copy／履歴 fixture

公式 WinMerge/winimerge `v1.0.54`、commit `da639cdfaeca87aaad0eaceec509afa11ad61421` の copy／三者auto／UndoRecords／Undo／Redo 関数を本体bytes無改変で抽出し、明示したraw BGRA adapterで採取した143ケース・935状態。製品/C#出力から期待値を生成しない。

| 保存物 | SHA-256 |
| --- | --- |
| `winimerge-image-copy-golden.json` | `A1645415624D84B3CB4A328148151A0FAE48B05412CF2E504589CD98C289D65D` |
| `reference-source/ImgMergeBuffer.hpp` | `956B69A5D76D6918CD6D3245B5DB5BFDEF902C82221E2539FFEA1B33996AE40B` |
| canonical `../ImageRegions/reference-source/ImgDiffBuffer.hpp` | `7071EDFA31CCC138685CA7B9FD3D41EE5E84B942FB3D8028C7E3A31C76F93F28` |
| canonical `../ImageRegions/reference-source/image.hpp` | `173CE00FE65187D34A6C7D2B10C0B45443EA6CFFBAA50FE1263BCE5840149609` |
| `probe-adapter.cpp` | `0923B15AB9457722717D05A41145AC37D1FEDC03EAECC5F91E1D9D14BE6CFA36` |

新しい原本は親が pinned GitHub gateway で取得した [ImgMergeBuffer.hpp](https://github.com/WinMerge/winimerge/blob/da639cdfaeca87aaad0eaceec509afa11ad61421/src/WinIMergeLib/ImgMergeBuffer.hpp) の18610bytesをそのまま保存した。Git blobは `fa2f0e71f28a54167c0462545c617f90c133ca82`。取得記録は `source.json`、採取SHA・compiler条件は `provenance.json`。原本・GPLは [ImageRegions出典](../ImageRegions/README.md) と [GPL v2本文](../ImageRegions/LICENSE.txt) を canonical とする。原本ライセンスは GPL v2以降。既存canonicalやgoldenは変更していない。

## 無改変抽出とadapter境界

- ImgMergeBuffer:22–135 `UndoRecord/UndoRecords`、216–317 `CopyDiff/CopyDiffAll/CopyDiff3Way`、342–389 `IsModified/IsUndoable/IsRedoable/Undo/Redo/GetSavePoint/SetSavePoint`、566–689 `CopyDiffInternal`。
- ImgDiffBuffer:33–161型、479–481挿入削除enum、1440–1562 `ConvertToRealPos`、1663–1693初期化、1702–1886比較／分類、2264–2279 `TemporaryTransformation`。
- 本体は行末を含む原本bytesをそのまま抽出する。抽出範囲/SHAは `extraction.json`。範囲指定にあった317／635／1474直前は関数途中なので、閉じ括弧まで拡張した。
- 静止画像、offset0、回転/変換なし、挿入削除NONE、overlay/wipe/強調なし。adapterの再比較はraw原画を前処理画像へコピーし、原本の比較・分類関数を呼ぶ。TemporaryTransformationの本体は無改変で、TransformImages接続先は変換フラグだけを切り替える。
- 未実行のInsert/Delete rows/columnsは、呼ばれた場合に例外を投げるstub。ConvertToRealPosの挿入削除分岐も除外する。NONEで全probe成功したことを確認する。

**FreeImageの新規canvas初期値・paste alpha処理は未実測。** `image.hpp` 311行のsetSizeはFreeImage wrapper、325–328行のpasteSubImageもFreeImageへ委譲している。本oracleのadapterは `setSize=新規zero-filled BGRA`、`pasteSubImage=raw BGRA矩形copy` と定義する。寸法拡張のgoldenはこのadapter契約下の原本copy処理であり、FreeImage完全互換や実GUIの画素初期値を証明しない。拡張領域の製品仕様採否は親の判断対象。

Save encoderは除外する。`save` actionは原本UndoRecords.saveの履歴markだけで、ファイル保存・保存失敗のdirty保持・BPP/palette/animation・元format互換の検証ではない。

## 入力・結果

各caseは2/3画像のraw上から下・左から右BGRA `images`、`readOnly`、`blockSize=8`、`threshold=0`、操作列 `actions` を持つ。src/dstとdiff indexは**0始まり**。kindは `copy`（選択領域）、`all`、`auto`、`undo`、`redo`、`save`（savepoint mark）、`set-savepoint`。

`expected.states[0]` は初期状態、その後は `{actionResult,state}`。copy/all/save/set-savepointのvoid戻り値はadapterのsentinel -1、autoは原本nMerged、undo/redoは原本boolを0/1で保存する。stateに各原画の寸法・rawBGRA base64／SHA、再比較region grid／分類／矩形／counts、全pane共有のhistory index/count/undoable/redoableとpaneごとのmodified/modcount/savepointを保持する。

二者2方向・三者6方向、選択first/last・全copy、三者各dst auto、透明RGB/alpha128、L字・ringの別領域の穴、block8の端、大小両方向、複数領域の全copy、逐次二回の寸法拡張、無効pane/ID/同pane/readonly、共有undo/redo、分岐・savepointを固定採取する。期待値をbbox全域copyへ置き換えない。

実測した原本挙動:

- CopyDiffAllは差分0でも履歴1件・dirty=true。CopyDiff3Wayもmerged=0で履歴1件・dirty=true。無効/readonly/同paneのguardによるreturnとは異なる。
- CopyDiffInternalは拡張後も事前preprocessed寸法でConvertToRealPosを判定する。17→9のcopyではdst17x17へ拡張しても、元9x9外にsource pixelを書かず差分が残る。adapter新領域はzero。
- all/autoの複数領域を一件の履歴にまとめる。Undo後新しいcopyを行うと旧redoを破棄する。リングbbox内の別領域は選択copyで保持する。

## 再生成と実測範囲

```powershell
uv run --no-project python tests/Fixtures/ImageCopy/generate-reference.py --output artifacts/verification/image-copy-reference/reproduced
Get-FileHash tests/Fixtures/ImageCopy/winimerge-image-copy-golden.json
Get-FileHash artifacts/verification/image-copy-reference/reproduced/winimerge-image-copy-golden.json
```

固定版更新を明示して行う場合だけ `--golden tests/Fixtures/ImageCopy/winimerge-image-copy-golden.json` を付ける。MSVC14.51.36231・Windows SDK10.0.28000.0を使用し、C++17 stdlibのみでcompileする。通常buildへC++/FreeImage/.NET/submodule依存を追加しない。

2026-10-01 native compile/probe exit=0、143ケース935状態、別出力先の再採取golden bytes一致。原本抽出一致・SHA・履歴・undo/redo・穴保持・拒否不変など5142検証成功/0失敗。入力plain/JSON、出力JSONL、compiler/stderr log、抽出、metadata、assertionsは `artifacts/verification/image-copy-reference/`。中間objectだけを清掃する。失敗契約は同ディレクトリ `failure-contract.md` と親の `artifacts/verification/image-three-way/next-static-edit-contract.md`。

2026-10-02、C#コピー核を実アプリの限定E2Eと全体E2Eへ照合した。原本全143ケース935状態が一致し、PNG保存・独立復号・再読込み、Undo直後savepoint、既存出力属性、入力／script／readonly／リンク保護と各上限拒否を実測した。Windows x64 Native AOT全体とheadless UI回帰も成功。実測値は[移行一覧](../../../Docs/MIGRATION.md#静止画像の領域コピーと共有履歴)に集約する。GUI編集接続、別OS、FreeImage初期値とpaste、元format encoder／多ページ保存、履歴128件境界と処理中取消は未検証または未完了。
