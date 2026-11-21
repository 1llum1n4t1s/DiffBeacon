# WinIMerge 画像ブロック／領域 fixture

公式 `WinMerge/winimerge` tag `v1.0.54`、commit `da639cdfaeca87aaad0eaceec509afa11ad61421` の C++ 関数を**本体無改変**で抜粋し、BGRA buffer adapter で実行した164件の固定結果。旧 GUI、FreeImage の復号、回転、位置合わせ、挿入削除の実測ではない。GPL v2 以降の原本ライセンス表示を保持し、公式 `GPL.txt` を `LICENSE.txt` に同梱する。

期待値は C++ 原本だけで計算する。C# や製品の出力を使った生成は行わない。通常の solution build／E2E に C++、FreeImage、DLL、submodule を追加しない。

## 出典と固定 SHA-256

| 保存物 | SHA-256 |
| --- | --- |
| `winimerge-image-regions-golden.json` | `07BF82D5179A748A568A0B6EB0AAA37B171F04FBF4228920A38698DDD8C05965` |
| `reference-source/ImgDiffBuffer.hpp` | `7071EDFA31CCC138685CA7B9FD3D41EE5E84B942FB3D8028C7E3A31C76F93F28` |
| `reference-source/image.hpp` | `173CE00FE65187D34A6C7D2B10C0B45443EA6CFFBAA50FE1263BCE5840149609` |

- [ImgDiffBuffer.hpp 原本](https://github.com/WinMerge/winimerge/blob/da639cdfaeca87aaad0eaceec509afa11ad61421/src/WinIMergeLib/ImgDiffBuffer.hpp): Git blob `34d82abc42e5686bf94d419dbab176c64e7c8d58`。
- [image.hpp 原本](https://github.com/WinMerge/winimerge/blob/da639cdfaeca87aaad0eaceec509afa11ad61421/src/WinIMergeLib/image.hpp): Git blob `52c4c00a059dcdbb1d8ead64788b10713ae62d32`。
- [GPL.txt 原本](https://github.com/WinMerge/winimerge/blob/da639cdfaeca87aaad0eaceec509afa11ad61421/GPL.txt): Git blob `36488d58658d63f9d7a8d5e0637c530df987c4e5`。
- Toolport GitHub gateway `get_file_contents` で revision を指定して取得。保存 bytes の `git hash-object --no-filters` を上記 blob と照合した。
- 原本33–161行の型、1663–1693行の `GetMaxWidthHeight`／`InitializeDiff`、1702–1886行の `CompareImages2`／`FloodFill8Directions`／`MarkDiffIndex`／`MarkDiffIndex3way`／`Make3WayDiff` を bytes ごと抜粋。抽出範囲・SHAは `extraction.json`。163行は後続 namespace 宣言なので型の抜粋には含めない。

## 採取範囲と JSON

各 case の `images` は左・右（二者）、左・中央・右（三者）順。画像は正の `width`／`height` と上から下、左から右の raw BGRA `bgraBase64`。alpha を含む4チャンネルを原本へ渡す。`blockSize` は1／2／8、`threshold` は double。offset は全画像0、変換・回転・位置合わせ・挿入削除は無効。

`expected.pair01`／`pair21`／`pair02` は原本比較直後の0（同値）／-1（差分）の block grid。二者の21／02は null。`regionIds` は0（領域外）／1からの領域番号、上から下・左から右で最初に見つけた順。各 grid は行配列。

`regions` の `left`／`top`／`right`／`bottom` は **block 座標、右・下は排他的境界**。pixel への拡大や端画像幅への切詰めはしない。`op` は1=LeftOnly、2=MiddleOnly、3=RightOnly、4=Conflict。二者領域は常に4。`differenceCount` は領域数、`conflictCount` は原本領域 op=4 の個数を adapter で数えた値。

- threshold は BGRA ユークリッド距離の平方 `> threshold²` だけを差分とする。同値境界は同じ。
- 三者候補は01∨21。02だけ異なっても候補なし。8近傍で連結し、21同値→左、02同値→中央、01同値→右の順で各 block を分類する。領域内分類が混在すると Conflict。
- `InitializeDiff` は三画像の共通最大 block grid を確保する。第三画像だけ高い場合、比較 pair 範囲より下にも原本の unsigned `hmax-by*blockSize` 減算と範囲外行判定が適用され、双方に pixel がない block も差分になる。原本関数を補正せず、この挙動を寸法差ケースに記録する。
- 原本 `MarkDiffIndex` は flood fill 後の走査中に矩形を更新するため、矩形も再計算せず原本値を保存する。

74固定ケースは二／三者、各側のみ、全異なる、対角接触、離れた左右、同一 block 混在、端 block、単独／複合チャンネル、alpha、閾値境界と小数、非推移的同値、寸法差を含む。追加90件は Python `random.Random(1054)` の固定小～中画像。全入力は JSON に raw bytes として保持する。

## 再生成

既存の Windows MSVC `14.51.36231`、Windows SDK `10.0.28000.0`、Python 3 を使う。参照プローブ専用で、製品 build の依存ではない。

```powershell
uv run --no-project python tests/Fixtures/ImageRegions/generate-reference.py --output artifacts/verification/image-regions-reference/reproduced
Get-FileHash tests/Fixtures/ImageRegions/winimerge-image-regions-golden.json
Get-FileHash artifacts/verification/image-regions-reference/reproduced/winimerge-image-regions-golden.json
```

同一 SHA を確認後に固定版を更新する明示作業では、生成コマンドに `--golden tests/Fixtures/ImageRegions/winimerge-image-regions-golden.json` を付ける。原本SHAを生成前に検証し、本体抜粋をそのままコンパイルする。compiler arguments は `/std:c++17 /EHsc /Y- /utf-8 /W3 /Od`。

入力 `probe-inputs.json`／`probe-input.txt`、出力 `probe-output.jsonl`、`compiler.log`、`probe-stderr.log`、`metadata.json`、抽出 `.inc`／`extraction.json` と生成 golden を指定出力先に保持する。先行失敗契約は `artifacts/verification/image-regions-reference/failure-contract.md`。

2026-10-01の原本採取と別出力先への再採取は build exit=0、probe exit=0、164件、golden bytes一致、原本SHA不変。製品の managed／Native AOT E2E は親の統合工程であり、この参照採取成功に含めない。
