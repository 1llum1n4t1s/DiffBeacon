# Abs の固定 native oracle

WinMerge commit `6780924ba35f7f55af0f6f14dd302561175830a6` の `AbsFunc` をMSVCで実行した41件の型付き期待値です。新規取得せず、`FolderStrSearchOracle/original` の固定Git blobを無改変コピーしました。`provenance.json` に出典・Git blob・SHA-256・GPLの原本 `COPYING` を記録しています。

`failure-matrix.md` を最初に作成後、`cases.json` の型付き入力、`adapter.cpp`、二回の別プロセスによる `golden.ndjson` を採取しました。adapterは typed literal / throwing node と結果のJSON化だけを提供します。次を行番号指定で無改変抽出します。

| 原典 | 行 | 用途 |
| --- | --- | --- |
| FilterExpressionNodes.cpp | 1693–1718 | shallow array helper と AbsFunc |
| FilterExpressionNodes.cpp | 3789 | abs の登録名と引数数1..1 |
| FilterExpressionNodes.cpp | 4269–4280 | 引数数検証 |
| FilterExpressionNodes.h | 20 | adapterのValueType順序の根拠 |

int64端点、2^53超、整数±/zero、doubleの±finite・subnormal・zero・最大値・infinity・quiet NaN payload、none/bool/string/Timestamp/Regex/FileContent拒否、空/混合/入れ子配列、throw trace、null/0/2/3引数を含みます。doubleのvalueは16桁のIEEE754 bit列で、符号ゼロとNaNをJSON数値に潰しません。

実測では `abs(INT64_MIN)` はint64最小値のまま返ります。これはこのMSVCでの原典挙動の観測であり、C++での移植性保証ではありません。doubleは符号bitを除去し、quiet NaN payloadを保持します。配列は一段だけ適用され、入れ子はnoneです。引数数違反は全引数の評価前に例外、正常な一引数は一度だけ評価されます。

パーサー、FunctionNode全constructor、Optimize、`.flt` loader/scanner、managed evaluatorはこのoracleの境界外です。原典関数を未検査のnull/空vectorで直接呼び出す未定義挙動は実行せず、原典の登録と検証ブロックを通します。

Python 3.13+ と既存MSVC x64 developer環境で、未使用の外部Dディレクトリを指定します。全build出力・TEMP/TMP・stdout/stderr・receiptはその指定先です。Pythonのbytecode生成を避けるため `-B` を使います。

```powershell
python -B tests/Fixtures/FolderAbsOracle/reproduce.py --output D:/Claude/DiffBeacon/<unique-run>/run1 --compiler cl.exe
python -B tests/Fixtures/FolderAbsOracle/reproduce.py --output D:/Claude/DiffBeacon/<unique-run>/run2 --compiler cl.exe
python -B tests/Fixtures/FolderAbsOracle/verify.py
python -B tests/Fixtures/FolderAbsOracle/verify.py --evidence D:/Claude/DiffBeacon/abs-oracle-20261020-r1
```

`verify.py` は製品コードとadapterをimportせず、Pythonの整数絶対値と明示したMSVC最小値例外、IEEE符号mask、型拒否、浅い配列とtraceから独立に期待値を計算します。全fixtureファイルのSHA、原典Git blob/SHA、case ID/件数、goldenを照合します。`--evidence` では二回のraw stdout一致、receiptのSHA、実process identity・終了code・handle signaled・両stream EOF、抽出include全byte、compiler/executable SHAも確認します。

`capture-index.json` は初回二runのreceipt hashを固定します。新規再採取のreceiptはPID・時刻が異なるため、この固定indexとは一致しません。再採取のstdoutはgoldenと全byte照合してください。保持する初回raw evidenceは `D:/Claude/DiffBeacon/abs-oracle-20261020-r1/run1` と `run2` です。将来の再現・監査のため保持し、代替証拠の検証完了後にこのtask分だけ正規清掃できます。
