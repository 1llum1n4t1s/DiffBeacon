# 旧 WinMerge GNU 二者 script の参照採取

この harness は旧 WinMerge に同梱された GNU 原本から参照 fixture を採取する開発用ツールです。DiffBeacon の通常 build・配布・実行には組み込みません。汎用 GNU diff の出力、C# 実装、独自に計算した期待 script は使いません。

```powershell
pwsh -NoProfile -File build/Generate-LegacyGnuReference.ps1
```

既定出力は `artifacts/verification/gnu-line/reproduced`。MSVC `14.51.36231`、Windows SDK `10.0.28000.0`、PowerShell 7、`uv run --no-project python` が必要です。採取した入力 bytes と各ケースの stdout/stderr、raw change、prefix、外部復元 change、設定、コンパイル引数・環境・ログ、原本と adapter の SHA-256、入力不変・再構成検証を JSON に保存します。実行後の exe/obj/pdb/ilk は metadata の `cleanupCandidates` に列挙し、このスクリプト自身は削除しません。

## 原本と adapter の境界

- `OriginalTranslationUnit.c` が無変更の `Src/diffutils/src/analyze.c` と `io.c` を同一 C 翻訳単位に include します。`diff_2_files` → `read_files` → `discard_confusing_lines` → `compareseq`/`diag` → `shift_boundaries` → `build_script` をそのまま実行します。`too_expensive` の getter は読み取り専用で、算法・分岐・結果を変更しません。
- 無変更の `util.c` 全体と `cmpbuf.c` を別の C 翻訳単位としてコンパイルします。比較設定の行比較 `line_cmp` と `translate_line_number` も原本です。外部 change 座標は `translate_line_number - 1`（0-origin）、deleted/inserted は無変更です。
- C++ harness は regular file の `_open(_O_BINARY)`/`_fstat64`、globals 初期化、`diff_2_files(..., depth=0, &binaryStatus, moved=0, &binaryFiles)`、JSON 出力を担当します。原本 caller の MFC、SEH、画面表示、prediffer、moved-block は実行しません。`UnsupportedPaths.c` の moved-block と context-header stub は呼ばれたら失敗終了し、成功を返しません。
- `equivs[0]`/`equivs[1]` は原本 `file_data.equivs` の先頭から `buffered_lines` 個を読み取った配列です。`lengths` は左右の `buffered_lines`、`equivMax` は左右の原本 `equiv_max`、`classCount` は共有の `equiv_max` そのものです。`classCount` は予約 class 0 を含む index 範囲の上限（`0 <= class < classCount`）で、実 class の個数は `classCount - 1` です。prefix/suffix は含めず、discard 前の区間と同値 ID をそのまま記録し、再採番・再分類・行の追加を行いません。`discard_confusing_lines` は別の `undiscarded` 配列へ投影するため、この `equivs` は算法の結果に書き換えられません。原本からの読取り専用出力と配列長・ID範囲の検証だけを追加しています。
- Python は入力 bytes 作成、原本 executable 呼び出し、出力記録と検証のみです。名前が `Extract` でも原本関数の抽出や再実装はしません。既定比較では BOM を除いた原文行へ外部 script を適用し、右入力を完全再構成できることを検証します。比較無視 flags では座標・件数・単調性・入力不変を検証し、無視対象のアンカーを独自実装で再判定しません。

## 設定と実測範囲

既定値は normal/context=0/default-algorithm=0/heuristic=1/no_discards=0/always_text=0/horizon=0/no_details=0/LF、moved=0、各 ignore flag=0、locale=C。case、space-change、space-all、numbers、eol の別設定も記録します。`CompareOptions.cpp` の設定式に従い、numbers 単独は `length_varies=0`、`ignore_some_changes=0` です。そのため数字無視でも桁数が違う行は同値扱いされない原本の結果をそのまま保持します。

空入力・空行・反復・交差一意・同点・先頭末尾・prefix/suffix・頻出+孤立・provisional の頻度5/6/9・LF/CRLF/CR/混在・最終改行・UTF-8 BOM/非ASCIIを採取します。A/B の長さ0～3の全225組合せも採取します。heuristic は反転199/201/4095/4097行と40行 snakeを含む交差ブロック199/201/220行を実行します。記録する閾値は `too_expensive=4096`。原本分岐への instrumentation を入れていないため、heuristic 分岐 coverage や全入力同値の成功は主張しません。

バイナリ、UTF-16/32、別 codepage、プロセス取消・資源上限、全 GUI 同値、三者比較は今回の範囲外です。一ケース一プロセスとし、原本が保持する file buffer はプロセス終了時に OS が回収します。

## 表 caller の証拠と限界

旧 caller は `Src/MergeDoc.cpp` の `SetPaths` → `SaveBuffForDiff` → `RunFileDiff`、`Src/DiffTextBuffer.cpp` の `SaveToFile(..., bTempFile=true)` を通ります。比較用一時ファイルは元 codepage と別に UTF-8 BOM を使用し、表セルの decoded 再 serialize ではなく元行の Chars/長さを保存します。mixed EOL が無効なら既定 EOL を使用し、有効なら元 EOL を使用します。最終改行のない入力へ余計な改行は加えません。

`Externals/crystaledit/editlib/ccrystaltextbuffer.cpp` の `JoinLinesForTableEditingMode` は quote 状態を反転して引用内の次物理行を元 EOL ごと結合します。temp/table/allow-newlines 時には保存本文の ESC を ESC ESC、CR を ESC r、LF を ESC n の順に変換し、外側 EOL を付けます。GNU io は表 props を解析しません。

`table-*` ケースはこの保存結果に合う比較用 bytes を手書きしたものです。`a` 対 `"a"`、引用内 LF/CRLF の ESC 化済み行、実 ESC+n、末尾 EOL なし、片側の引用だけの変更を含みます。caller の実行で変換を確認した成果物ではありません。現行 C# の decoded セル比較の契約とも別です。caller 原本を hash 対象に含めますが、全旧 GUI 互換と称しません。

## ライセンス

GNU 原本各ヘッダーは GPL version 2 or later を指定しています。本文は [`Src/COPYING`](../../Src/COPYING) にあり、原本の copyright/ライセンスを維持します。この参照 harness の GNU 組込み executable も同条件を満たす必要があります。fixture の登録と配布の判断は親作業に委ねます。
