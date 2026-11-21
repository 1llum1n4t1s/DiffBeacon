# GNU 同値行 script の fixture と実CLI契約

`legacy-gnu-golden.json` は旧WinMergeに同梱されたGNU原本を実行した279ケース。**旧GUI／配布アプリの実測ではない**。SHA-256は `4d52c2a1badc1febf908b7ef348ce05808f8400b64736040e7d66359d377edcf`。この追加担当はgoldenを変更・複製していない。

## 元ソースとadapterの境界

無変更の `analyze.c` / `io.c` を同一C翻訳単位へincludeし、無変更 `util.c` / `cmpbuf.c` を別途コンパイル。原本の `diff_2_files → read_files → discard_confusing_lines → compareseq/diag → shift_boundaries → build_script` を実行した。C++ adapterはregular file入力、globals初期化、原本配列・scriptの読取り専用JSON出力を担当し、genericdiffや独自算法で期待値を作っていない。moved-block/context stubは呼ばれたら失敗する。MFC、SEH、prediffer、moved-block、GUIは実行しない。

原本io後の `equivs` はprefix/suffixを含まない `file_data.equivs[0..buffered_lines)`。ID再分類・再採番をせず、そのままC#実CLIへ渡す。`classCount`は予約class0を含む上限で、範囲は `0 <= ID < classCount`。原文の同値分類やprefix選択をこの経路では再実装しない。`rawChanges`は配列内0-origin、`changes`は原本の外部復元座標。E2EでprefixLinesを両座標へ加えて照合する。

table-*は旧表保存のESC化に合うbytesを手書きした採取入力で、旧callerを実行した検証ではない。flagsは原本による同値分類済みequivsの出典であり、このCLIの変換設定として渡していない。元bytesのbase64/SHA/長さを検証するが、全bytes→equivs変換互換、全GUI同値、三者、別codepage、UTF-16/32、バイナリ、取消は未検証。

## 失敗条件と実測範囲

先行失敗契約は `artifacts/verification/gnu-line/e2e-contract.md`。

- 全279caseを別process CLI `--gnu-line-script INPUT_JSON --max-work 8000000` で照合。通常結果は原本rawChanges全値／順序一致とprefix復元一致。
- 予算fallbackを許容するのは `heuristic-reversed-4095` / `heuristic-reversed-4097` だけ。WorkUsed=8M、work-limit理由、全入力一blockを要求し、他caseのfallbackは失敗。matched/fallback/failedの名前と結果を別集計する。
- 変更範囲の単調性・非負・境界、equal区間のクラス同値を確認し、全元同値行がequal/deleted/insertedとして一度ずつ順序通り消費されることを検証する。期待scriptを導出する算法は追加しない。
- 追加正常5件は両空/classCount0、片側空各1、class0同値、class0混在同値。追加予算0/1では理由付き全入力fallbackを確認する。
- 拒否24件は負／範囲外class、負classCount、必須項目／型／JSON不正、各側rowcap262145、budget／引数不正を確認する。負／範囲外classは予算0でも再確認し、validationをfallbackより先に行う。終了2・診断・成功JSONなし、全case JSON bytes不変を要求する。

計310呼出（279＋正常5＋予算2＋拒否24）。生成した同値case JSON、既存commandログ/exit/stdout/stderr/assertions、`gnu-line-observations.json` の選択一覧とmatched/fallback/failed一覧を出力先へ保持する。原本fixtureはSHAと実行前後bytesで保持を確認する。

低レベルの正式実CLI E2Eでは277 raw完全一致、不一致0、8M fallback2を確認した。通常 `--compare` への入力接続はdefault 273ケースの元bytesを実ファイルへ戻して `--eol strict --max-work 8000000` で照合し、271 script完全一致・指定2件の予算退避を確認した。元byte・行番号順序・本文保持も検証する。限定実行は `--gnu-text-only`、結果は `gnu-text-observations.json`。上限・フィルター・EOF/EOL・三者マージとパッチの補助ケースも実プロセスで確認する。非default 6ケースはこの通常入力接続の一致判定に含めない。現行フィルターのUnicode・数字の正規化を旧CRTの全分岐へ変更したものではない。表caller全体・旧GUIの全互換も未検証。

## 出典・再採取・ライセンス

再採取は `pwsh -NoProfile -File build/Generate-LegacyGnuReference.ps1`。通常アプリbuildには原本GNU採取exeを組み込まない。詳細は [採取harness README](../../../build/LegacyGnuReference/README.md) と `artifacts/verification/gnu-line/reproduced/metadata.json`。

- source commit: `118f76461e58f45e4894512c71d9fcd14ed095b5`
- Windows `Microsoft Windows NT 10.0.26300.0`、win-x64、CRT locale=C
- MSVC `19.51.36260.0`、toolset `14.51.36231`、Windows SDK `10.0.28000.0`。原本Cは `/TC /Y- /utf-8 /DWIN32 /D_CRT_SECURE_NO_WARNINGS /W3 /Od /Zi`。
- metadataにはcompile環境／引数、原本・adapter SHA、入力bytes、出力、再構成確認を保存。adapter `OriginalTranslationUnit.c` SHAは `ba27bfdc0c66a49b575181e8f7e7fdada4a5f56253b901d9c045e96398bd70de`。
- 原本ヘッダーはGPL-2.0-or-later。本文は [Src/COPYING](../../../Src/COPYING)。copyright／ライセンスを維持し、原本GNUを組込む採取exeも同条件。

| 無変更原本 | SHA-256 |
| --- | --- |
| `Src/diffutils/src/analyze.c` | `d874081200325d7a24ae40c141cb106cb4bb953faffcdbf6d74ba93bac534685` |
| `Src/diffutils/src/io.c` | `1fe0a8fa9701e90ff65df8538befe228d6787712a6f2da77f456bddc91b17fde` |
| `Src/diffutils/src/util.c` | `79507d8f97c4f7718c84c422b94a3c052719d22a36df3da206b92f5b7c747600` |
| `Src/diffutils/lib/cmpbuf.c` | `db087b3d93518eb5b0fea4c1feaec008c7cbfed954757f9b6550c37e76fb8fa5` |

表の初期GNU行対応はdefault A/B・LFの225ケースから各変更が片側のみの143ケースを選び、元bytes・SHAを保持して実CLIの元行対応とHTMLの全セル・種類・ghostを照合する。限定実行は `--gnu-table-only`、選択と一致一覧は `gnu-table-observations.json`。両側変更のraw採点を原本scriptから推定せず、88件の別行対応fixtureへ分ける。引用表記の復号同値・三者の無変更側一致、共有予算・EOL/EOFを追加検証する。旧raw CSV callerの全入力変換の同値性は未確認。
