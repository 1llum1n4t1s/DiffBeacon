# フォルダーフィルター StrCount / StrFind 原典oracle

固定WinMerge commit `6780924ba35f7f55af0f6f14dd302561175830a6` のraw Git blob 8件を `original/` に無改変で保存する。出典・Git blob SHA-1・SHA-256・byte数は `provenance.json`、GPLは `original/COPYING`、Pocoライセンスは `original/Poco-LICENSE`。既存 `LineTransformSearch` の固定原本をコピーし、全blobを再照合した。`.gitattributes` は全fileのcheckout改行変換を禁止する。

`golden.ndjson` は93ケース。関数本体、`getAsString`、`applyToScalarOrArray`、`ToStringValue`、Poco ASCII class/property表/`toLower` templateを原典から行単位で無改変抽出してC++20で実行する。変更したのは型・明示literal/throw node・JSON出力の採取adapterだけ。Poco Timestamp/Regex/FileContent依存は未使用のfail-fast bindingであり、それらの型の実値構築は対象外。doubleはnative `std::to_string` の小数6桁を実行する。

先に保存した `failure-matrix.md` に失敗条件を列挙した。型付き引数、空/不正needle、None、bool/Int64/double文字列化、直接配列とnested配列、非重複一致、ASCIIのみの大小文字無視、UTF-8 byte位置、埋込みNUL、Int64 occurrenceの省略/負値/極値/型違いを固定する。引数nodeの評価traceと各位置の例外を記録し、無効needleでもFind第3引数が先に評価されることを確認する。エラーはadapterの `probe-N` のみで、旧parserの診断ではない。

関数名の大小文字無視と公開arityは**原典の静的証拠**。`FilterExpressionNodes.cpp:3835`–3836はCount 2引数・Find 2〜3引数、4221–4282のconstructorは名前の `Poco::toLower` と個数拒否を行い、`FilterParser.y:139`–190が例外をcompile errorへ変換する。本oracleはfunction bodyへ直接接続し、誤ったarity/null argsは評価traceなしでNoneとなる。この結果を公開 `.flt` の許容arityへ投影しない。

旧FunctionNode constructor、parser、Optimize、フォルダー変数、ファイルloader、scanner、managed実装、公開 `.flt` は実行していない。mixed-case関数名の受理はconstructorのソース証拠でありnative body実行結果ではない。特にOptimizeのNone結果の型アクセスや最適化有無の同等性は検証していない。フォルダー選別・scan件数・public filter受理の証拠として使わない。

## 再採取と独立照合

Windows x64 MSVC developer shellとPython 3.13以降を使う。通常.NET buildの依存ではない。未使用の外部Dドライブ出力先だけを受け付け、repo/CODEX_HOME配下・linked ancestor・既存出力を拒否し、子processのTEMP/TMPもrun内へ設定する。

```powershell
python -B -X utf8 tests/Fixtures/FolderStrSearchOracle/reproduce.py --output <作業フォルダー>\DiffBeacon\<unique>\run1 --compiler cl.exe
python -B -X utf8 tests/Fixtures/FolderStrSearchOracle/reproduce.py --output <作業フォルダー>\DiffBeacon\<unique>\run2 --compiler cl.exe
python -B -X utf8 tests/Fixtures/FolderStrSearchOracle/verify.py
python -B -X utf8 tests/Fixtures/FolderStrSearchOracle/verify.py --run <作業フォルダー>\DiffBeacon\<unique>\run1 --run <作業フォルダー>\DiffBeacon\<unique>\run2
```

`verify.py` はmanaged codeと採取scriptをimportしない。入力型、出力型、全93 ID、値、評価traceを独立したbyte検索modelで照合する。原本SHA/Git blob、固定manifestを常に検査する。`--run` は2run一組で、原典includeのbytes、include hash、adapter/case hash、compiler/EXE hash、compile/run command、PID/birth、保持handle終了、exit code、stdout/stderr EOFとhash、goldenとの全bytes一致を確認する。native実行時receiptは観測記録であり、暗号署名された環境証明ではない。

初回2runのreceipt hashを `capture-index.json` に固定する。元の外部run以外で再採取する場合、raw出力をgoldenに照合し、capture-indexは新規2runのreceipt hashへ明示更新してから完全readerを実行する。既定readerだけでは外部compile/process証拠を検証した扱いにしない。raw source/executable/log/receiptを含む初回証拠の保存先は `<作業フォルダー>\DiffBeacon\strsearch-oracle-20261019-r1\run1` と `run2`。
