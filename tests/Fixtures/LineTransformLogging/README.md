# LineTransformLogging 固定C++原本

固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` の原典から無改変spanを抽出し、MSVC C++20で独立に二回compile・別childで実行した35件のstdoutを `golden.ndjson` に保持する。goldenをPythonや現行Coreから生成していない。SHA-256は `cd9d2700315908a9bd7e5de5ffdd7aca671c3376340486f5e2af0e31fa50d156`。`native-proof.zip` のSHA-256は `73385de54e6681ecf05316921ada2affd108f0c47b21a7ea9bbc4d07a2bbb974`。

`original/` の3ファイルは既存LineTransformReplaceの固定reference ZIPとLineTransformAggregatesのadditional-originalに保存済みの原典bytesを再利用した。Git commit、blob ID、全bytes SHAとサイズは `source-manifest.json`。原ライセンスはGPL-2.0（リポジトリのCOPYING参照）。追加変更は加えていない。抽出した10 spanの開始・終了byte、行、SHAはproof内の各 `provenance.json` に保持する。

検証前に想定した失敗はseverity取り違え、空白結合・配列/none/boolの誤文字列化、最後の戻り値の脱落、名前の引用/Unicode/上書き/不正directive処理の相違、内外ログ順の逆転、非選択枝の評価、例外前の完了ログ消失と未完成ログの誤記録、原典・compile入力・stdoutの差替えである。

| 範囲 | 実行する原実装と観測 |
| --- | --- |
| ログ | `LogFunc`、`LogErrorFunc`、`LogWarnFunc`、`LogInfoFunc`。level 0/1/2、0/1/複数引数、全引数成功後のlogger呼出し、最後のtyped値のkindと文字列化 |
| 文字列化 | `ToStringValue`。none、bool、int64、double、空/Unicode/改行文字列、空配列と入れ子配列。nullはValueTypeのmonostateで、原表記は`none` |
| directive | `ParseDirective`、`ParseAllDirectives`。引用名、空名、空白、Unicode、大文字key、重複name、ci/noopt、不正指定、未閉鎖引用、未知key、引用後tailの受理も原挙動として記録 |
| 評価順 | 原`IfFunc`/`evalAsBool`による選択枝だけの評価、入れ子ログ順、途中throw前に完了した内側ログの保持と外側ログの不記録 |

adapterは検証専用ASTを組み立てる。式lexer/parser、関数登録のarity検査、`FunctionNode::Optimize`、AND/OR演算子、現行Core/App/CLIの経路を実行したとは扱わない。`actualExpression` の `log-call` はdirective除去の確認用opaque markerであり解析されない。0引数は原LogFuncへの直接呼出しで、製品parserが受理する証拠ではない。throw nodeは検証専用の`std::runtime_error("fixture failure")`であり、製品の例外型やエラー回復との同等性を主張しない。Poco Timestamp/Regex/FileContentは型成立用hostで、値を入力しない。日時整形、Regex/FileContentの文字列化、実ファイル読込み、null shared_ptr、logger callback自体の例外は範囲外。

`cases.json` のASTとdirective文字列が固定入力。NDJSONの全文字列値はUTF-8 hexとし、JSON escapeや出力コードページによる正規化を避ける。`resultKind` はadapterが観測したvariant kind、`resultHex` は原`ToStringValue`の結果。失敗では空で、完了済み`logs`は保持する。directive失敗時には評価しない。

`verify.py` はcompileせず、固定fileset/SHA、原典Git blob、原ZIP全entryのサイズ/SHA/CRC、二回のcompile/run receipt・保持OS handleの終了観測・PID/birth・実行終了/EOF、全compile入力、無改変原典rangeと行、stdout全bytesを照合する。さらにcapture.pyをimportしない独立Python readerが全35件のdirectiveとASTを評価し、全fieldとログ順を比較する。native proof内に実行exe・obj・抽出inc・生成cases.inc・compile stdout/stderr・run stdout/stderr・receiptを保持する。historical receiptは採取環境の証拠で、将来の再compileのexe SHA一致は要求しない。

```powershell
python -B tests/Fixtures/LineTransformLogging/verify.py
```

再採取はWindows/MSVCとPython 3を使い、リンクを含まない新規外部Tempを指定する。compiler TEMP/TMPもrun内へ注入する。次をそれぞれ異なる新規出力（例`run1`/`run2`）へ実行する。既存goldenがあればstdout全bytes一致を必須とし、正式fixtureを上書きしない。ソース・golden・proofの再生成を通常buildやCore依存へ追加しない。

```powershell
python -B tests/Fixtures/LineTransformLogging/capture.py --output <fresh-external-Temp>/run1 --compiler <absolute-cl.exe> --vcvars <absolute-vcvars64.bat>
python -B tests/Fixtures/LineTransformLogging/capture.py --output <fresh-external-Temp>/run2 --compiler <absolute-cl.exe> --vcvars <absolute-vcvars64.bat>
```

二つのrunの`golden.ndjson`が正式goldenと一致し、両receiptでexitCode=0、timeout=false、stdoutEof/stderrEof=true、heldProcessHandleSignaled=trueを確認する。異なるchildのPID/birthと同一compile入力も比較する。正式fixtureは約0.82 MiBで、再検証に必要な圧縮native証拠だけを残し、通常のビルド出力directoryを保持しない。
