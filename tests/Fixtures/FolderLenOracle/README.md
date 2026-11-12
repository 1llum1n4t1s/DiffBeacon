# LenFunc の固定 native oracle

固定 commit `6780924ba35f7f55af0f6f14dd302561175830a6` の旧 WinMerge `LenFunc` とUTF-8文字数helperを無変更抽出し、MSVC x64で二回独立compile・別child process実行した38件です。`golden.ndjson` はnative stdoutの原bytesで、SHA-256は `e8ff790146f3f0dc298b5e9bd63fc8a8aa92e4c6cad58261cbdf462c85879daf`。製品を呼んで期待値を作成せず、期待値を手編集しません。

## 原典と adapter 境界

`reference-source.zip` に7 raw Git blobs（ソース5件、GPL-2.0 / Poco BSL-1.0のライセンス2件）を保持します。`Src/Common/unicoder.cpp` の先頭にはPerry RappのMITライセンス全文も含まれます。初回は固定commitに対する `git rev-parse <commit>:<path>` と `git cat-file blob <id>` で採り、working treeの変更や改行変換を混入させません。再採取・独立検証はこのfixture内の固定ZIPを使い、既存fixtureやGit履歴を必要としません。

| 原典 | 無変更でcompileする範囲 |
| --- | --- |
| `Src/FilterEngine/FilterExpressionNodes.cpp:1851–1869` | `LenFunc` 全文 |
| `Src/Common/unicoder.cpp:112–122,145–156` | `Utf8len_fromLeadByte` / `stringlen_of_utf8` 全文 |
| `Src/FilterEngine/FilterExpressionNodes.cpp:3778–3785` | `FunctionInfo` 宣言 |
| 同 `:3803` | `len` 登録行（1..1）をadapterの一要素登録表へinclude |
| 同 `:4270–4280` | constructorの引数数検証ブロック |
| `Src/FilterEngine/FilterExpressionNodes.h:15–21,26–31` | full `ValueType` / `ValueType2` 宣言、`ExprNode` 宣言 |
| `Externals/poco/Foundation/src/Timestamp.cpp:58–72` | Timestampの値constructor・copy constructor・destructor |
| `Externals/poco/Foundation/include/Poco/Timestamp.h:156–159` | Timestampの `operator ==` |

`source-manifest.json` はcommit / blob SHA-1 / SHA-256 / size、`provenance.json` は12 spanのbyte範囲・行番号・SHA-256を固定します。行位置は排他的終端を使うため、末尾改行を含むspanのendLineが次の行になる場合があります。spanは文字列置換や翻訳なしで `.inc` へ連結します。

`adapter.cpp` は空の `FilterEvalContext`、型付き入力を返す `ExprNode` spy、配列storage、JSON出力を提供します。spyは各評価indexを記録し、指定引数で例外を投げます。Poco Timestampのクラス宣言は必要メンバーをadapterで宣言し、constructor / copy / destructor / equalityは原典を使います。full ValueTypeのregex / FileContentRef alternativesは宣言上保持しますが、入力には含めません。FileContentRef equalityは呼ばれると例外になるguardです。文字列入力はPythonでstrict UTF-8へencodeし、各byteを固定長octal escapeと明示長の `std::string` に変換するため、補助planeと埋込みNULも保持します。

観測は `mode` で分けます。

- `evaluate` 34件は合法1引数で `LenFunc` とUTF-8 helperを直接実行します。成功結果はJSON整数、例外時は `result:null` と `error` です。直下配列件数、stringのcode point数、none=0、他scalar=1を確認します。
- `arity` 4件は原典の引数数検証ブロックだけをadapter関数内で実行します。0 / 2 / 3引数拒否と1引数受理を確認し、全引数throw指定でも `evaluated:[]` です。`result:true` は検証通過のadapter sentinelで、Lenの戻り値ではありません。`InvalidArgumentCountError` は `std::runtime_error` にaliasするため、拒否条件とmessageだけを観測し、例外の実クラスは対象外です。

**FunctionNode constructor全体、findFunction、parser、optimizer、case directive、`.flt` loader / 公開動作、side metadata、Prop取得、folder scanner、GUI、製品E2Eは実行しません。** arityブロックの観測をconstructor / parser全体の実行証拠として扱いません。**無効Unicode（不正UTF-8・孤立surrogate）の挙動は対象外**です。旧helperの5 / 6 byte lead処理や不正byteのfallbackから、無効Unicodeに対する製品互換性を主張しません。

## 固定ケース

採取前に `failure-matrix.json` と型付き入力 `case-plan.json` を固定しました。

- 配列は空、単要素、異種混合、空／複数／深いnested array、nestedとscalarの混合、64要素を確認します。入れ子を展開せず直下だけを数えます。
- none、boolean true／false、integerの0／負値／int64両端、doubleの0／負値、Timestampを確認します。
- 空／ASCII／日本語／補助plane絵文字／混合、結合列と合成済み文字、ZWJ、国旗、variation selector、埋込みNUL、CR／LF／tab、有効UTF-8の1–4byte境界、反復列を確認します。絵文字は1、`e`＋結合アクセントは2、ZWJ絵文字列は3です。graphemeやUTF-16 code unitを数えません。
- 各合法入力は `[0]` として一度だけ評価し、指定例外は `argument-0` として伝播します。arityは別modeで式を評価しません。

## 再採取と独立検証

独立readerはPython 3.12以降のみで動作し、compilerや製品、既存fixtureは不要です。製品やcapture実装をimportせず、型付きJSONから結果・例外・評価列を別実装で計算します。文字列はstrict UTF-8にencode可能なことを確認してPythonのcode point長で照合し、成功時のJSON整数型も検査します。固定全fileのSHA / size、両ZIP全entryのCRC、raw Git blob、spanのbyte範囲・行番号・SHA、二回のcompile入力・実行receipt・exe・stdoutをメモリー内で検証し、ZIPを展開しません。

```powershell
$python = (uv python find 3.13)
& $python -B tests/Fixtures/FolderLenOracle/verify.py
```

再採取は既存MSVC x64を使います。`capture.py` は割当済み外部作業root `E:/Codex/DiffBeacon/allequal-20261017/process-temp/Codex/DiffBeacon/len-oracle` の下だけを受理します。新しいrun名を指定してください。repository / `.codex` / `CODEX_HOME`、reparse point、`artifacts` というpath componentは拒否します。初回固定は同rootのrun1 / run2で実測済みです。

```powershell
$fixture = 'tests/Fixtures/FolderLenOracle'
$vc = 'C:/Program Files/Microsoft Visual Studio/18/Community/VC'
$compiler = "$vc/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe"
$vcvars = "$vc/Auxiliary/Build/vcvars64.bat"
$runRoot = 'E:/Codex/DiffBeacon/allequal-20261017/process-temp/Codex/DiffBeacon/len-oracle'
& $python -B "$fixture/capture.py" --output "$runRoot/new-run1" --compiler $compiler --vcvars $vcvars
& $python -B "$fixture/capture.py" --output "$runRoot/new-run2" --compiler $compiler --vcvars $vcvars
& $python -B "$fixture/verify.py" --observed "$runRoot/new-run1/golden.ndjson"
& $python -B "$fixture/verify.py" --observed "$runRoot/new-run2/golden.ndjson"
```

採取時は `TEMP` / `TMP` を各run内の `compiler-temp` へ注入し、compile入出力・全抽出span・生成case入力・stdout/stderr・compile receipt・実行receipt・exeを保存します。実行receiptはPID / birth FILETIME、保持したprocess handleの終了状態、stdout/stderr EOF、終了code、入力・exe・stdoutのSHAを記録します。異なる二つのchild processが終了code 0、空stderr、stdout完全一致でした。

`freeze.py --run1 <path> --run2 <path>` は初回固定専用で、既存golden / proof / manifestを上書きしません。二回の原stdoutを照合し `golden.ndjson` / `native-proof.zip` / `fixed-manifest.json` を作成します。通常の再検証では実行しません。`capture.py --initialize-source` も初回専用で、そのときだけ固定commitを保持したGit repositoryが必要です。

初回実測環境はWindows x64 / MSVC 14.51.36231 / Python 3.13。38件、独立2実行の全照合に成功しました。製品build、製品E2E、公開 `.flt`、非Windowsのnative再採取は対象外です。`.gitattributes` の `* -text` で固定原bytesを保持します。
