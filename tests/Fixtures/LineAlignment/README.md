# 表の行対応 fixture

この fixture は **旧 GUI／配布バイナリの実測ではない**。旧 C++ の10定義の本体を変更せず抽出し、raw logical 行を渡す buffer adapter から実行した結果である。ルート `LICENSE.md` の GPL v2 に従う。

| 保存物 | SHA-256 |
| --- | --- |
| `legacy-line-golden.json` | `06A370C108716F0CF250642580C8130AB7D2C5E65F0D693A4585E501605AAA37` |
| `legacy-line-pair-dummy-golden.json` | `D72E3EA26D5A0758E370195B505E1CCBEC87C5DD82B51D819942F7D771E247E0` |
| `legacy-line-expanded-golden.json` | `1C311E5806E7C4E60F06350F841F83ED6B0DEB059DDEF83966D52A98C162ABF3` |

元の20入力×4設定＝80ケースを固定snapshotとして保持する。pairdummy補正版はscores4ケースだけ異なり、全mappingは同一。E2Eは両方のSHAと80mappingの一致を確認し、scoresを製品の期待値には使わない。expanded版から `4096-both-edge` と `offset-prefix-three` の追加8件だけを合流し、計88件を実行する。既存80件は上書きしない。

設定0は文字比較・EOL無視、1は単語比較・EOL無視、2は文字比較・EOL厳密、3は文字比較・EOL無視・case無視・全空白無視。全rawブロックを明示して得た oracle と、製品の decoded prefix/suffix/patience アンカーが区切る結果は、全ケースで同一にはならない。

## 先行して固定した失敗条件と範囲

正本は `artifacts/verification/table-line-alignment/e2e-contract.md`。

- 中央共通語、逆順、零score同点、4096/4097、one-pair shortcut、wrapped、quoted logical、空側、EOL/EOFの二者入力13種×4で全mappingを照合する。中央共通語の通常文字比較は `[[null,1],[1,2]]` が期待値であり、prefix比率だけの対応を検出する。
- 全80件では各側の元logical行1..Nが一度ずつ順序通り現れることを検証する。3者20-adopt/ghost/inconsistent、offset-three、central-three、empty-base、eol-onlyは初期アンカーの影響を区別し、全体oracle一致を強制しない。central-three/empty-baseは親の統合実測後、一致が成立した場合にoracle対象へ追加できる。
- eol-onlyはdecodedセル同値。strict設定2だけexit1、他設定はexit0。それ以外はセル差分又はmissing行のためexit1。終了コードをgoldenのmappingから推測しない。
- config0/3の20入力×2で実CLIと `--report-project` 単体HTMLを比較する。HTML各側の元 `data-row`、`data-aligned-row`、ghostの空row／missingを確認する。projectにはword-level/strict/budgetの設定がないため、そのHTML同条件比較は未検証。
- 予算0/1/120は同じ二つのdecoded固有アンカーに区切られる三変更blockで検証し、総 `alignmentWorkUsed <= budget`、reason付きfallback、元全行保持とアンカー対応を確認する。4097-capは`legacy-content-limit`、4097-one-pairはfallbackなしを期待する。
- unknownflag、負／過大／不正budget、値不足、不明EOL、重複base、存在しないbaseはexit2・診断あり・成功JSONなし。全呼出で入力bytesを保持する。

88 CLI＋44 HTML＋3共有予算＋9拒否＋literal `--base` を含むsubstitute値2件＝146呼出。後者は `--substitute --base x` と `--substitute x --base` の値をbase指定と誤認せず、同値exit0を期待する。入力、project、HTML、選択policy (`line-alignment-selection.json`)、終了コード、標準出力／エラー、assertionsは既存E2Eの出力先に保持する。ビルド／実行は親の統合工程であり、追加時点では未実測。単体テストは作らない。

## 元実行の出典

- source revision: `ec7d5614fd2f8edaf5b0f84595d42c27fa69c97a`
- Windows: `Microsoft Windows NT 10.0.26300.0`
- MSVC compiler: `19.51.36260.0`、toolset `14.51.36231`。`/std:c++17 /EHsc /Y- /utf-8 /DUNICODE /D_UNICODE /W3`
- CRT locale: `C`。WordDiffと同じWindows ICU adapterを使用。
- 再現手順: `artifacts/verification/table-line-alignment/Run-Original.ps1`、`Extract-Original.py`、`legacy-line-probe.cpp`。
- 保存済みの再現ツールは `pwsh -NoProfile -File build/Generate-LegacyLineReference.ps1`。22入力×4設定を生成し、同梱expanded JSONとSHAが一致することを確認した。出力は `artifacts/verification/table-line-alignment/reproduced`、C++/MSVCは元関数採取だけに使用し通常アプリbuildに含めない。
- 抽出10定義のsource/body SHA: 同ディレクトリ `extraction.json`。コンパイラ／adapter条件は `metadata.json`、補正版は `corrected-metadata.json`。
- 空側では旧投影アクセスをskipしAdjustのone-sided経路を実行するadapter。offsetは元oracleの開始位置を保持しているが、実CLIは全文入力を比較する。

| 主要な原本 | SHA-256 |
| --- | --- |
| `Src/MergeDocDiffSync.cpp` | `A3D0FBDA9F9D01706D357F16C1D074E02CB032F551FD22F622FA78D963C883E9` |
| `Src/MergeDocLineDiffs.cpp` | `EA60BF2AA4BE63A2E96DCA7046B3DC12445F0A2DF2A756EBE070C02AEF85736C` |
| `Src/MergeDoc.h` | `A24E5267F528BC287A45995EBA342EBED7507F9510EA955EFB0754FD20BF2769` |
| `Src/DiffList.h` | `436099FB13A1B7C5708CA4F78F01C51C353E19B1E46BEBF5CE003E99320B6AF4` |
| `Src/DiffList.cpp` | `ED86F1271214BC261E1A77B888ACE22253B0FAB0E539CBE460E965DBC1884803` |
| `Src/stringdiffs.cpp` | `5D9593CF5A96C0964DBE9ED998CB47476A75A0392845334807AEDB542F7BF74D` |

`4096-edge` の既存名は片側総contentが4096超になるため、厳密な両側content合計4096の `4096-both-edge` をexpandedから追加した。後者は元mapping `[[null,1],[1,2]]`、fallbackなしを確認する。`offset-prefix-three` は開始位置が異なる全体oracleなので全行保持／HTML一致を確認する。元snapshotのSHAは変更しない。

2026-10-01の一時停止前に、実App DLLの限定E2Eは1494成功・0失敗・0スキップ。全体E2E・Native AOT・Macはこの追加に対して未実行。実結果は `artifacts/verification/table-line-alignment/e2e-managed/assertions.json`、再開範囲は同ディレクトリ `pause-note.md`。

## 三者 decoded 一致アンカーの追加反証

失敗契約は `artifacts/verification/table-line-alignment/decoded-anchors-e2e-contract.md`。独立reviewが実アセンブリで、left=`a\n"a"\n`、base=`"a"\na\n`、right=`z\n` のdecoded無変更左が三者でghost/Deleted/Addedになることを再現した。二者identityが三者でも保持されることを新しい製品契約として検証する。旧raw oracle全体のmappingへの一致とは区別する。

- 引用形式だけ違う無変更側と左右反転、ignore-case、ignore-whitespace、substitutionの計6入力で、無変更branch/ancestorの全source番号identityとHTML全セルclass `Equal` を確認する。
- 共通引用セルと中間挿入を持つ2入力（左右反転）では、既知branch/sourceとancestor/source anchorを固定する。自由な変更側へのraw best-pairは固定しない。
- 8入力それぞれに二者CLI、三者CLI、単体HTML、予算0三者CLIを実行する。全呼出で全元logical行を順序通り一度ずつ保持し、入力/project bytes不変を確認する。予算0でのanchor強度は要求しない。
- HTMLの`td` class token、missing、data-row/data-aligned-rowとCLI mappingを照合する。追加fixture・CLIログ・HTMLと `decoded-anchors-selection.json` を再現成果物として保持する。

追加32呼出で計178呼出。今回の追加はコード実装のみで、ビルド／実行は親の統合工程を待つ。既存1494成功の実測を今回の追加成功として扱わない。
