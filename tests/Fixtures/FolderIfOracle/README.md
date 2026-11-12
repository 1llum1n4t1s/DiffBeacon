# IfFunc の固定 native oracle

固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` の旧WinMergeから、If本体・bool解釈・arity検証を無変更で抽出した79件です。MSVC x64で別々にcompileし、別child processで採取したstdoutが完全一致しました。`golden.ndjson` はstdoutの無編集bytes、SHA-256は `5d1f7488051a693415179b89c6d0522f89e96c16bccc1ec655e144c018a87130` です。製品コードを実行して期待値を生成していません。

## 固定原典と境界

`reference-source.zip` は固定Git commitから採取した6 raw blobsです。`source-manifest.json` にGit blob SHA-1・SHA-256・sizeを固定しています。GPL-2.0の `LICENSE.md` とPoco BSL-1.0の `Externals/poco/LICENSE` を同梱します。`provenance.json` のbyte区間を無変更で抽出し、include末尾へ改行だけを追加します。sourceの文字列置換・翻訳は行いません。

| 原典 | 実行する範囲 |
| --- | --- |
| `FilterExpressionNodes.cpp:34–49` | evalAsBool全体 |
| 同 `:2433–2441` | IfFunc全体 |
| 同 `:3778–3785` | FunctionInfo宣言 |
| 同 `:3799` | ifの3..3引数登録行 |
| 同 `:4270–4280` | constructor内のarity検証blockだけ |
| `FilterExpressionNodes.h:15–21,26–31` | 全ValueType/ValueType2・ExprNode宣言 |
| `Timestamp.cpp` / `Timestamp.h` | 値/copy constructor・destructor・equality・epochMicroseconds |

行位置の終端は末尾改行により次の行を指す場合があります。正確なbyte範囲とSHAはprovenanceに記録しています。

adapterは空context、AST spy、typed入力、serializer、最小Timestamp宣言を提供します。Timestampの処理は原典実装をincludeします。regex/FileContentRefはnull/non-nullのopaque shared_ptrの輸送だけを対象にします。adapterのFileContentRef guardコメントの「未対応」は参照先内容の比較を指し、内容比較が呼ばれるとthrowします。参照先の正規表現・ファイル内容処理を証明しません。

**FunctionNode constructor全体、findFunction、parser、optimizer、case directive、`.flt` loader、通常`.flt` UI経路、metadata、folder scanner、GUIは実行しません。** InvalidArgumentCountErrorはadapterでruntime_errorへaliasするため、arityの原典拒否条件・messageだけを対象にします。

## 失敗matrixと観測

`failure-matrix.md` と `case-plan.json` はcapture作成より先に固定しました。

- 条件23件: bool true/false、none、int/double/string/Timestamp、opaque参照、空/直下true/直下false/混在/入れ子配列。条件を一度評価し、boolの明示trueまたは配列直下のboolean trueがある場合だけtrue枝を選びます。
- 返却46件: 両枝で全型・空/入れ子配列のtyped値を保持します。arrayは同じshared storageであることも観測します。
- 例外5件: 未選択枝のthrowを抑止し、選択枝/条件のthrowを伝播します。traceは正常時 `[0,1]` または `[0,2]`、条件throw時 `[0]` です。
- arity5件: 0/1/2/4を引数未評価で拒否、3を未評価で受理。全spyにthrowを指定して `evaluated:[]` を確認します。arityのtyped boolean trueは検証通過sentinelで、If実行結果ではありません。

## 再採取

Python 3.13標準ライブラリとMSVC x64を使います。出力は以下の同じTemp task rootの**未使用の新しいrun名**へ指定します。既存pathを受理せず、ancestorのreparse、repo・`.codex`・CODEX_HOME内出力を拒否します。TEMP/TMPは各runのcompiler-tempへ注入します。製品build/E2Eを実行しません。

```powershell
$python = (uv python find 3.13).Trim()
$root = 'C:\Users\IMT\AppData\Local\Temp\Codex\DiffBeacon\if-oracle-20261017'
$compiler = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231\bin\Hostx64\x64\cl.exe'
$vcvars = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'
$stamp = [guid]::NewGuid().ToString('N')
& $python -B tests/Fixtures/FolderIfOracle/capture.py --output "$root/repro-$stamp-a" --compiler $compiler --vcvars $vcvars
& $python -B tests/Fixtures/FolderIfOracle/capture.py --output "$root/repro-$stamp-b" --compiler $compiler --vcvars $vcvars
& $python -B tests/Fixtures/FolderIfOracle/verify.py --observed "$root/repro-$stamp-a/golden.ndjson"
& $python -B tests/Fixtures/FolderIfOracle/verify.py --observed "$root/repro-$stamp-b/golden.ndjson"
```

`freeze.py --run1 <path> --run2 <path>` は初回固定専用です。既存golden/proof/manifestを上書きせず、二回の原stdout・receiptを照合して固定します。`capture.py --initialize-source` も初回原典保存専用です。

## 独立readerと保持資料

`verify.py` は製品/captureをimportせず、標準ライブラリだけで型付きcase-planから結果・例外・評価順を照合します。固定全fileのSHA/size、raw Git blobのSHA-1/SHA-256、全spanのbyte/行、二回のcompile入力、exe、stdout、stderr、終了code、held process handleの終了、stdout/stderr EOF、PIDとbirth FILETIMEによる別processを検査します。`--observed` は新しいstdoutも無編集goldenへ照合します。

`native-proof.zip` はrun1/run2のexe・無変更include・adapter・生成入力・compiler command/response・compile stdout/stderr/receipt・実行stdout/stderr/receiptを保持します。再生成可能なobjとcompiler-tempは含めません。`.gitattributes` は全file `-text`、`fixed-manifest.json` は自己参照以外の全固定物のSHA-256/sizeです。

採取原本は上のTemp rootのrun1/run2に保持します。再現・監査に必要な原本で、親の指示によりこの作業では削除・清掃しません。fixture内ZIPと各runのreader照合が再現方法です。原本の保持が不要になり、親が清掃を許可した時点が削除条件です。製品build/E2E/UI・他OSの検証は未実施です。
