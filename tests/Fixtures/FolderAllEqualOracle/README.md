# AllEqualFunc の固定 native oracle

固定 commit `6780924ba35f7f55af0f6f14dd302561175830a6` の旧 `valueEquals` / `AllEqualFunc` を無変更抽出し、MSVC x64 で二回独立 compile・別 child process 実行した90件です。`golden.ndjson` は native stdout の原 bytes で、SHA-256 は `7cd9031f43f539f388ec505f228ba8047e9d2b22f1869df8249882e2694c2faa`。製品を呼んで期待値を作成せず、期待値の手編集もしません。既存 `FolderPropertyFilters` の148件は変更しません。

## 原典と adapter 境界

`reference-source.zip` に6 raw Git blobs（ソース4件、GPL-2.0 / Poco BSL-1.0 ライセンス2件）を保持します。初回は既存 `FolderPropertyFilters/reference-source.zip` を読み取り専用で利用し、その固定 source manifest の commit・Git blob SHA-1・全 bytes の SHA-256 / size を検証しました。この fixture は必要な原典を自身で保持するので、再採取・独立検証に既存 fixture は不要です。

| 原典 | 無変更で利用する範囲 |
| --- | --- |
| `Src/FilterEngine/FilterExpressionNodes.cpp:1752–1773` | `valueEquals` 全文。型のvariant indexと再帰配列比較 |
| 同 `:1775–1801` | `AllEqualFunc` 全文。先頭配列とscalarの分岐・短絡・評価順 |
| 同 `:3790` | `allequal` の登録行。原典の関連付けを示す証拠のみで、実行しない |
| `Src/FilterEngine/FilterExpressionNodes.h:15–21,26–31` | full `ValueType` / `ValueType2` 宣言、`ExprNode` 宣言 |
| `Externals/poco/Foundation/src/Timestamp.cpp:58–72` | Timestamp の値constructor・copy constructor・destructor |
| `Externals/poco/Foundation/include/Poco/Timestamp.h:156–159` | Timestamp の `operator ==` |

`source-manifest.json` は raw blob の commit / blob SHA-1 / SHA-256 / size、`provenance.json` は9 spanのbyte範囲・行番号・SHA-256を固定します。spanは文字列置換や翻訳なしで `.inc` へ連結し、compile 入力として保存します。

`adapter.cpp` は空の `FilterEvalContext`、入力を返す `ExprNode` spy、配列storageの作成、JSON出力を提供します。spy は各評価のindexを記録し、指定した引数で例外を投げます。Poco Timestamp のクラス宣言は必要メンバーを adapter で宣言し、constructor / copy / destructor / equality の実装は原典を使用します。full ValueType の regex / FileContentRef alternatives は宣言上保持しますが、入力には含めません。FileContentRef の adapter equality は呼ばれた場合に例外を投げる未対応guardです。

合法arity（1引数以上）の `AllEqualFunc` を直接呼びます。**FunctionNode constructor、parser、arity検証、optimizer、case directive、`.flt` loader / 公開動作、side metadataの組立て、Prop取得、folder scanner、GUIは実行しません。** 登録行を保存しても constructor / parser の実行証拠にはなりません。このoracleは製品全体の `.flt` 互換性を証明するものではありません。

## 固定ケースと観測

`failure-matrix.json` を採取前に作成し、`case-plan.json` に入力の型・値・throw指定を固定します。

- none / bool / integer / double / string / Timestamp の単独引数6件と全36型組合せを採取します。integer 7 と double 7 は不一致、Timestamp と同値integerも不一致です。
- scalar の全引数一致・最初／後半の不一致と短絡・先頭／途中引数の例外、文字列の大小文字差・非ASCIIを確認します。
- 空／1要素／同値／異値／None／型差／大小文字差／Timestamp／nested 配列の順序・長さ・型・空配列・深い配列を採取します。各array caseには、後続引数でthrowする対も置きます。先頭が配列なら後続引数は一切評価されません。
- scalarの次に配列が現れる場合は型不一致となり、その後のthrow引数は評価しません。

最終90件すべてで結果・例外・評価index列を固定しています。例えば scalar 不一致の評価列は `[0,1]`、先頭配列では一致／不一致にかかわらず `[0]`、途中例外は `result:null` と `argument-N` です。空／1要素配列はtrueです。

## 再採取と独立検証

Python 3.12以降、既存MSVC x64を使用します。PythonのWindows Store aliasではなく実体の実行ファイルを指定してください。fixture内へ作業物を出力しません。`--output` はユーザーTemp配下 `Codex/DiffBeacon/` の未作成pathのみを受理し、repository / `.codex` / `CODEX_HOME`、reparse point、`artifacts` というpath componentを拒否します。

```powershell
$python = (uv python find 3.13)
$vc = 'C:/Program Files/Microsoft Visual Studio/18/Community/VC'
$compiler = "$vc/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe"
$vcvars = "$vc/Auxiliary/Build/vcvars64.bat"
$fixture = 'tests/Fixtures/FolderAllEqualOracle'
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('Codex/DiffBeacon/allequal-' + [guid]::NewGuid().ToString('N'))
& $python -B "$fixture/capture.py" --output "$runRoot/run1" --compiler $compiler --vcvars $vcvars
& $python -B "$fixture/capture.py" --output "$runRoot/run2" --compiler $compiler --vcvars $vcvars
& $python -B "$fixture/verify.py" --observed "$runRoot/run1/golden.ndjson"
& $python -B "$fixture/verify.py" --observed "$runRoot/run2/golden.ndjson"
```

採取時は `TEMP` / `TMP` を各run内の `compiler-temp` へ注入します。compile入出力・全抽出span・生成case入力・実行stdout/stderr・compile receipt・実行receipt・exeを各runへ保存します。実行receiptはPID / birth FILETIME、保持したprocess handleの終了状態、stdout/stderr EOF、終了code、入力・exe・stdoutのSHAを記録します。異なる二つのchild processが終了code 0、空stderr、stdout完全一致でした。

`freeze.py --run1 <path> --run2 <path>` は初回固定専用です。既存golden/proof/manifestを上書きせず、二回の原stdoutを照合して `golden.ndjson` / `native-proof.zip` / `fixed-manifest.json` を作成します。通常の再検証では実行しません。`capture.py --initialize-source` も初回原典保存専用です。

`verify.py` は製品・capture実装をimportしない独立readerです。固定全fileのSHAとsize、ZIP全entryのCRC、raw Git blob、spanのbyte範囲・行番号・SHA、二回のcompile入力・実行receipt・exe・stdoutを検証し、型付きJSON入力から別実装で結果・例外・評価順を再計算してnative stdoutと照合します。`--observed` で新しい採取stdoutも固定原本へbyte一致照合できます。ZIPはメモリー内で読み、展開しません。

初回実測環境は Windows x64 / MSVC 14.51.36231 / Python 3.13。90件、独立2実行、同一stdout SHAで全照合に成功しました。製品build、製品E2E、`.flt` parserや公開経路はこのfixture採取の検証対象外です。
