# NotEachFunc の固定 native oracle

固定 commit `6780924ba35f7f55af0f6f14dd302561175830a6` の旧 WinMerge を使用した67件です。MSVC x64 で二回独立 compile し、それぞれ別 child process で採取しました。`golden.ndjson` は native stdout の無編集 bytes で、SHA-256 は `10ec86c8ed458a46ae1fe5369b5f45f9d2d98b793a3f9ed2da80a3177086c613` です。製品コードから期待値を生成しません。

## 固定原典と adapter

`reference-source.zip` は `FolderAnyOfOracle` の同一固定commitの6 raw Git blobsをそのまま保持しています。各blobのGit SHA-1、SHA-256、sizeは `source-manifest.json` に固定しています。GPL-2.0の `LICENSE.md` と Poco BSL-1.0の `Externals/poco/LICENSE` も含みます。`provenance.json` は次の12 spanのbyte範囲・行位置・SHA-256を記録し、captureは各spanを置換せずincludeファイルへ連結します。

| 原典 | 無変更で実行する範囲 |
| --- | --- |
| `Src/FilterEngine/FilterExpressionNodes.cpp:34–49` | `evalAsBool` 全体 |
| 同 `:1693–1704` | template宣言を含む `applyToScalarOrArray` 全体 |
| 同 `:2738–2751` | `NotEachFunc` 全体 |
| 同 `:3778–3785` | `FunctionInfo` 宣言 |
| 同 `:3822` | `noteach` の1..1引数登録行 |
| 同 `:4270–4280` | constructor内の引数数検証ブロックだけ |
| `Src/FilterEngine/FilterExpressionNodes.h:15–21,26–31` | full `ValueType` / `ValueType2` と `ExprNode` 宣言 |
| `Externals/poco/Foundation/src/Timestamp.cpp:58–72` | 値constructor、copy constructor、destructor |
| `Externals/poco/Foundation/include/Poco/Timestamp.h:156–159` | equality実装 |

行番号はbyte spanの終端位置を表すため、末尾改行で次の行を指す場合があります。raw blobsのSHAを検証し、原典spanそのものをcompileします。ヘルパーの再実装による代用はありません。

`adapter.cpp` の担当は空context、入力を返す `ExprNode` spy、配列storage、結果serializerです。Poco Timestampの宣言は必要メンバーだけadapterで宣言し、実装は固定原典を使用します。regexとFileContentRefはfull ValueType内に保持しますが入力対象外です。FileContentRef equalityは誤用時にthrowするguardです。配列結果は順番・スロット・長さを保持したJSON array、monostateはJSON nullで観測します。

## 観測範囲と失敗条件

`failure-matrix.json` と型付き `case-plan.json` を captureコードの作成・compileより先に固定しました。63件の `evaluate` は合法1引数で本体を直接呼び、別の4件の `arity` は原典の検証ブロックだけを実行します。

- scalar true/falseを反転し、none、integer、double、string、Timestampは未定義にします。各型の単要素配列と混在配列も照合します。
- 配列の直下要素を一度ずつ変換します。`[true,false,1]` は `[false,true,null]`、`[[true],[],[[true]]]` は `[false,true,true]` です。nested array要素のboolean解釈は「直下にtrue booleanを含むか」で、再帰mapや再帰true探索をしません。
- 空配列、trueの位置、繰返し値、全未定義スロットで順番・件数・nullスロットを確認します。
- 合法引数の評価traceは `[0]`、throwは `argument-0` として伝播します。
- arity 0/2/3は評価前に拒否、1は評価せず受理します。全引数をthrow指定して `evaluated:[]` を確認します。arityの `result:true` はadapterの通過sentinelです。例外型は `InvalidArgumentCountError` をruntime_errorへaliasするため、原典の拒否条件・messageだけが対象です。

**FunctionNode constructor全体、findFunction、parser、optimizer、case directive、`.flt` loader、公開 `.flt` 経路、metadata、Prop取得、folder scanner、GUIは実行しません。** arity blockとdirect function-bodyを区別し、このoracleを製品全体の互換性証明として扱いません。

## 再採取

Python 3.13標準ライブラリ、MSVC x64、vcvars64.batを使用します。実Pythonの絶対パスで `-B` を指定してください。`capture.py` は次のroot内に限りfreshな出力先を受理し、既存ディレクトリへ上書きしません。TEMP/TMPは各runのcompiler-tempへ向け、製品build/E2Eは行いません。

```powershell
$python = (uv python find 3.13).Trim()
$root = 'E:\Codex\DiffBeacon\allequal-20261017\process-temp\Codex\DiffBeacon\noteach-oracle'
$compiler = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231\bin\Hostx64\x64\cl.exe'
$vcvars = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'
& $python -B tests/Fixtures/FolderNotEachOracle/capture.py --output "$root/repro1" --compiler $compiler --vcvars $vcvars
& $python -B tests/Fixtures/FolderNotEachOracle/capture.py --output "$root/repro2" --compiler $compiler --vcvars $vcvars
& $python -B tests/Fixtures/FolderNotEachOracle/verify.py --observed "$root/repro1/golden.ndjson"
& $python -B tests/Fixtures/FolderNotEachOracle/verify.py --observed "$root/repro2/golden.ndjson"
```

`freeze.py --run1 <fresh-run1> --run2 <fresh-run2>` は初回の固定作業専用です。既存golden/proof/manifestを上書きしません。raw Gitから取り直す `capture.py --initialize-source` も空の新規fixture向けで、既存固定物を上書きしません。

## 独立readerと保持資料

`verify.py` は製品やcaptureをimportせず、標準ライブラリだけで型付き入力から期待結果を計算します。固定全ファイルのSHA/size、raw blobのSHA-1/SHA-256/size、全spanのbyte/行位置、二回のcompile入力、exe、stdout、stderr、終了コード、held process handleの終了、両pipe EOF、PIDとcreation FILETIMEによる別processを照合します。

`native-proof.zip` はrun1/run2のexe、無変更include、生成入力、compiler stdout/stderrとreceipt、実行stdout/stderrとreceiptを保持します。再生成可能なobjとcompiler-tempだけはzip対象外です。`.gitattributes` は全ファイルを `-text` とし、`fixed-manifest.json` は自己参照を除く全固定物のSHA/sizeを記録します。

採取原本は上記rootのrun1/run2に保持しています。再現・監査のための保持で、削除・清掃はこの作業では行いません。compileとnative実行以外の製品検証、parser・通常GUI・他OSは未検証です。
