# AllOfFunc の固定 native oracle

固定 commit `6780924ba35f7f55af0f6f14dd302561175830a6` の旧 WinMerge `AllOfFunc` を無変更抽出し、MSVC x64 で二回独立 compile・別 child process 実行した53件です。`golden.ndjson` は native stdout の原 bytes で、SHA-256 は `eb8d4f74a0621a6ef9193c9530eb66be8690eba1fd39bcd23f0816dda9de5bb3`。製品を呼んで期待値を作成せず、期待値を手編集しません。

## 原典と adapter 境界

`reference-source.zip` に6 raw Git blobs（ソース4件、GPL-2.0 / Poco BSL-1.0 ライセンス2件）を保持します。初回のみ既存 `FolderAllEqualOracle/reference-source.zip` を読み取り専用で利用し、固定 source manifest の commit・Git blob SHA-1・全 bytes の SHA-256 / size を検証しました。再採取と独立検証はこの fixture だけで行えます。

| 原典 | 無変更で compile する範囲 |
| --- | --- |
| `Src/FilterEngine/FilterExpressionNodes.cpp:1736–1750` | `AllOfFunc` 全文 |
| 同 `:3778–3785` | `FunctionInfo` 宣言 |
| 同 `:3791` | `allof` 登録行（1..1）。adapter の一要素登録表へ include |
| 同 `:4270–4280` | `FunctionNode` constructor の引数数検証ブロック |
| `Src/FilterEngine/FilterExpressionNodes.h:15–21,26–31` | full `ValueType` / `ValueType2` 宣言、`ExprNode` 宣言 |
| `Externals/poco/Foundation/src/Timestamp.cpp:58–72` | Timestamp の値constructor・copy constructor・destructor |
| `Externals/poco/Foundation/include/Poco/Timestamp.h:156–159` | Timestamp の `operator ==` |

`source-manifest.json` は raw blob の commit / blob SHA-1 / SHA-256 / size、`provenance.json` は10 span の byte範囲・行番号・SHA-256を固定します。行番号はspanの排他的終端を含む位置情報なので、末尾改行を含むspanのendLineは次の行になる場合があります。spanは文字列置換や翻訳なしで `.inc` へ連結します。

`adapter.cpp` は空の `FilterEvalContext`、入力を返す `ExprNode` spy、配列storage、JSON出力を提供します。spy は各評価のindexを記録し、指定引数で例外を投げます。Poco Timestamp のクラス宣言は必要メンバーを adapter で宣言し、constructor / copy / destructor / equality の実装は原典を使用します。full ValueType の regex / FileContentRef alternatives は宣言上保持しますが、入力には含めません。FileContentRef の equality は呼ばれた場合に例外を投げる未対応guardです。

観測は `mode` で分けます。

- `evaluate` 49件は合法の1引数で `AllOfFunc` 本体を直接実行します。`result` は boolean または原典の `std::monostate` を表すJSON nullです。例外は別の `error` に記録します。
- `arity` 4件は原典の引数数検証ブロックだけをadapter関数内で実行します。0 / 2 / 3引数の拒否と1引数の受理を確認します。全引数をthrow指定し、`evaluated:[]` を確認します。このmodeの `result:true` は検証通過のadapter sentinelであり、AllOfの評価結果ではありません。`InvalidArgumentCountError` は adapter 内で `std::runtime_error` に alias し、原典の拒否条件とmessageを観測します。例外の実クラスは対象外です。

**FunctionNode constructor 全体、findFunction、parser、optimizer、case directive、`.flt` loader / 公開動作、side metadata、Prop取得、folder scanner、GUIは実行しません。** arityブロックの観測を constructor / parser 全体の実行証拠として扱いません。このoracleは製品全体の `.flt` 互換性を証明しません。

## 固定ケース

採取前に `failure-matrix.json` と型付き入力 `case-plan.json` を固定しました。

- 空配列、1要素、全true、falseの先頭／中央／末尾、空／true／false／深い入れ子配列を確認します。直下の要素だけをbooleanとして判定し、nested arrayはfalseです。
- none、integerの0／1／負数、doubleの0／1、空文字列／true文字列／非ASCII文字列、Timestamp、boolean true／falseを、それぞれscalar・単要素配列・trueの間の要素で確認します。非boolean scalarはnull、非boolean配列要素はfalseです。
- 合法の引数評価は一度だけ `[0]` で、指定例外は `argument-0` として伝播します。
- arity 0 / 1 / 2 / 3は別modeで、式を一度も評価しません。

## 再採取と独立検証

独立readerはPython 3.12以降のみで動作し、compilerや製品、既存fixtureを必要としません。製品やcapture実装をimportせず、型付きJSONから結果・例外・評価列を別実装で再計算します。固定全fileのSHA / size、両ZIP全entryのCRC、raw Git blob、spanのbyte範囲・行番号・SHA、二回のcompile入力・実行receipt・exe・stdoutをメモリー内で検証し、ZIPを展開しません。

```powershell
$python = (uv python find 3.13)
& $python -B tests/Fixtures/FolderAllOfOracle/verify.py
```

再採取は既存MSVC x64を使います。現在の `capture.py` は割当済み外部作業root `E:/Codex/DiffBeacon/allequal-20261017/process-temp/Codex/DiffBeacon/allof-oracle` の下だけを受理します。新しいrun名を指定してください。repository / `.codex` / `CODEX_HOME`、reparse point、`artifacts` というpath componentは拒否します。初回固定は次のrun1 / run2で実測済みです。

```powershell
$fixture = 'tests/Fixtures/FolderAllOfOracle'
$vc = 'C:/Program Files/Microsoft Visual Studio/18/Community/VC'
$compiler = "$vc/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe"
$vcvars = "$vc/Auxiliary/Build/vcvars64.bat"
$runRoot = 'E:/Codex/DiffBeacon/allequal-20261017/process-temp/Codex/DiffBeacon/allof-oracle'
& $python -B "$fixture/capture.py" --output "$runRoot/new-run1" --compiler $compiler --vcvars $vcvars
& $python -B "$fixture/capture.py" --output "$runRoot/new-run2" --compiler $compiler --vcvars $vcvars
& $python -B "$fixture/verify.py" --observed "$runRoot/new-run1/golden.ndjson"
& $python -B "$fixture/verify.py" --observed "$runRoot/new-run2/golden.ndjson"
```

採取時は `TEMP` / `TMP` を各run内の `compiler-temp` へ注入します。compile入出力・全抽出span・生成case入力・stdout/stderr・compile receipt・実行receipt・exeを各runへ保存します。実行receiptは PID / birth FILETIME、保持したprocess handleの終了状態、stdout/stderr EOF、終了code、入力・exe・stdoutのSHAを記録します。異なる二つのchild processは終了code 0、空stderr、stdout完全一致でした。

`freeze.py --run1 <path> --run2 <path>` は初回固定専用で、既存golden / proof / manifestを上書きしません。二回の原stdoutを照合し `golden.ndjson` / `native-proof.zip` / `fixed-manifest.json` を作成します。通常の再検証では実行しません。`capture.py --initialize-source` も初回原典保存専用です。

初回実測環境は Windows x64 / MSVC 14.51.36231 / Python 3.13。53件、独立2実行の全照合に成功しました。製品build、製品E2E、`.flt` parserと公開経路、非Windowsでのnative再採取は対象外です。固定fixtureは `.gitattributes` の `* -text` で原bytesを保持します。
