# AtFunc の固定 native oracle

## 想定失敗条件（採取前）

`failure-matrix.json` をcapture実装より先に定義しました。

- 2引数以外の受理、arity検証中の引数評価。
- 第1引数の重複評価、非配列でもindexを評価、indexの重複評価。
- Arrayの要素を左から全評価しない、選択されない要素の例外を隠す、例外後の要素やindexを評価する。
- double 0.0 / 小数 / boolean / string / none / array / Timestamp / regex / ContentRefをint64 indexへ変換する。
- 0-based・負indexの1回加算・空配列・範囲外・int64両端を誤る。
- noneや再帰配列を含む要素の型・順序・値を変える。
- 論理式の浅い配列truthと直接filterの再帰truthを同一視する。
- raw Git blob・無変更span・二回のnative stdoutが変わる。

## 原典と実測

原典commitは `6780924ba35f7f55af0f6f14dd302561175830a6`。`reference-source.zip` は7 raw Git blobsとライセンス（GPL-2.0 / Poco BSL-1.0）を含みます。6 blobsは既存FolderArrayOracleの固定ZIPから無変更コピーし、`Src/FilterEngine/FilterExpression.cpp` は固定commitの `git show` から取得しました。`source-manifest.json` は各blobのサイズ・Git SHA-1・SHA-256を固定します。他fixtureやGit履歴は再採取時には不要です。

`provenance.json` はAtFunc全文（1817–1834行）、ArrayFunc全文（1803–1815行）、`evalAsBool`、`ContainsTrue`、FunctionInfo、at登録（2..2）、constructorのarity検査、大小文字正規化のinitializer、full ValueType / ValueType2、ExprNode、必要なPoco Timestamp実装のbyte span・行番号・SHAを固定します。終端byteは排他的です。実行対象はraw spanを無変更includeします。case normalization spanは証拠でありcompileしません。

`adapter.cpp` は空context・spy AST・serializer・入力storageのみ提供します。AtとArray、浅い論理truth `evalAsBool`、直接filterの再帰truth `ContainsTrue` は原典そのものです。第1引数を一度評価し、配列の場合だけindexを一度評価します。indexはint64のみ受理し、負なら配列長を一度加算します。At自身にcatchはなく、adapterの外側で例外とtraceを記録します。`array-expr` caseはArrayFuncを実際の子ASTとして実行し、要素の左からの評価をtrace `10,11,12`、第1引数を `0`、indexを `1` で記録します。

95件をMSVC 14.51.36231 / Windows x64 / Python 3.13で二回別compile・別child process実行しました。両方で終了code 0、stderr空、stdout EOF、保持process handleのsignaledを確認しています。raw stdout SHA-256は `08fa8e60aa3bd53d795fa183fa4043cb7181ab495043c53957ef506ac585d3b0`。型付きgoldenには戻り値の型・値、`logicalTruth`（非対応型はnull）、`directTruth`、例外、評価traceを別々に保存します。int64と同値doubleを区別し、UTF-8 / NULを保持します。arity成功時のresult:trueはadapter sentinelで、truth列はnullです。

| case群 | 件数 | 確認内容 |
| --- | ---: | --- |
| selected-type | 19 | none / boolean / int64両端 / double / Timestamp / string / opaque regex・ContentRef / 再帰配列 |
| boundary / empty | 16 | 先頭・末尾・負の長さ・範囲外・int64両端・空配列 |
| invalid-index | 15 | int64以外を変換せずnone |
| non-array / skip-throw | 30 | 非配列ではindex未評価、indexの例外も未評価 |
| first/index throws | 2 | 例外伝播、後続未評価 |
| arity | 5 | 0 / 1 / 2 / 3 / 16引数、全引数未評価 |
| array-eager / element-throws | 8 | 選択前の全要素評価と全例外位置、例外後の停止 |

## 再採取と独立reader

captureはDの専用root配下のfresh childだけを受理します。repository / `.codex` / CODEX_HOME / reparse point / 既存runは拒否し、TEMP / TMPとcompile全出力もrunへ集約します。`recheck1` / `recheck2` が既存なら一意名へ変更します。

```powershell
$python = (uv python find 3.13)
$fixture = 'tests/Fixtures/FolderAtOracle'
$root = 'D:/Claude/DiffBeacon/at-oracle-20261019-r1'
$vc = 'C:/Program Files/Microsoft Visual Studio/18/Community/VC'
$compiler = "$vc/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe"
$vcvars = "$vc/Auxiliary/Build/vcvars64.bat"
& $python -B "$fixture/capture.py" --output "$root/recheck1" --compiler $compiler --vcvars $vcvars
& $python -B "$fixture/capture.py" --output "$root/recheck2" --compiler $compiler --vcvars $vcvars
& $python -B "$fixture/verify.py" --observed "$root/recheck1/golden.ndjson"
& $python -B "$fixture/verify.py" --observed "$root/recheck2/golden.ndjson"
```

`verify.py` はcaptureや製品をimportしない独立readerです。全fixtureファイルのSHA・サイズ、ZIP全entry CRC、Git blob、原典span、二回のcompile入力・exe・receipt・raw stdoutを照合し、型付きcase planから結果・例外・評価順・二種類のtruthを独立計算します。`--observed` は新しい採取stdoutを固定原bytesと照合します。`freeze.py --run1 <run1> --run2 <run2>` は初回固定専用で既存golden / native-proof / manifestを上書きしません。

`native-proof.zip` は二回分のexe・全compile入力・stdout / stderr・receiptを保持し、再生成可能なobjは除きます。初回raw runは `D:/Claude/DiffBeacon/at-oracle-20261019-r1/run1` と `run2` です。親の清掃判断まで削除しません。固定ZIPから再検証できるため、展開runは不要になった時点で正規清掃できます。

## 検証の境界

parser / optimizer / 公開`.flt` / loader / folder scanner / metadata / GUI / 製品E2Eはこのfixtureでは実行しません。大小文字無視はconstructorの `Poco::toLower(name)` 原典証拠で固定し、parserやconstructor全体の実行とはしません。arityもconstructor内の検査blockだけです。

Timestampは原典の値・copy・destructor・epoch読取を使用し、時刻parseを行いません。regex / ContentRefは原典のshared_ptr variant slotを保持しますが、adapterのopaque tokenを使うためregex実行・content読取り・実製品objectの構築を検証しません。pointer identity・非有限double・negative zeroのbit保持・不正Unicode・null array pointerは対象外です。noneを不在metadataと同一視しません。truth helperの直接実行はfilter loader全体や論理nodeのshort circuitを検証したものではありません。
