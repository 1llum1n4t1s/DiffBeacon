# ArrayFunc の固定 native oracle

## 想定失敗条件（採取前）

- 0引数やnull引数で空配列を返さない、可変引数の個数を制限する。
- boolean / integer / double / none / Timestamp / string の型が変わる。
- 配列を展開する、順序を変える、空の入れ子を失う。
- false / none / 空配列によって後続評価を短絡する、同じ引数を複数回評価する。
- 例外引数より後を評価する、例外を成功結果へ変える。
- raw Git blob / 無変更span / native stdout が再採取時に変わる。

原典は固定commit `6780924ba35f7f55af0f6f14dd302561175830a6`。採取・検証結果とadapterの範囲は初回採取完了時に追記する。

## 原典・証拠と実測

`reference-source.zip` は既存 FolderLenOracle の固定原典ZIPから6 raw Git blobsを無変更取得しました。source manifest の固定commit・blob SHA-1・SHA-256・サイズを初回取得時にも照合します。固定後のcapture / readerは他fixtureやGit履歴を必要としません。`LICENSE.md` (GPL-2.0) と `Externals/poco/LICENSE` (BSL-1.0) を原典ZIPへ同梱します。

`provenance.json` は ArrayFunc 全文、FunctionInfo、array登録行（0..unbounded）、constructorの引数数検証ブロック、full ValueType / ValueType2、ExprNode、Poco Timestampの値・copy constructor / destructor / equality / epochMicroseconds のbyte範囲・行番号・SHAを固定します。spanは無変更でincludeします。範囲の終端byteは排他的です。

`adapter.cpp` は空context、各引数の評価indexを記録するspy、型付きserializerと配列入力storageのみ提供します。Timestampクラス宣言は必要メンバーのみadapterで宣言し、値・copy・destructor・読取methodは原典実装です。文字列は明示byte長とoctal escapeでUTF-8 / NULを保持します。JSON数値は `type: integer / double / timestamp` を併記します。nativeはdouble 1.0をJSON数値1と出力することがありますが、型labelは失いません。

50件をMSVC 14.51.36231 / Windows x64 / Python 3.13で二回独立compile・別child process実行しました。終了code 0・stderr空・stdout EOF・保持process handleのsignaledをreceiptへ記録し、native stdoutの原bytesは二回一致しました。SHA-256は `31e439ea586ba539b462e5912069c01b1e904c311e1f7493ff01bc3d741948a0` です。

- evaluate / null 45件: 空・nullptr、各scalar型、int64両端、Unicode / NUL / JSON制御文字、異種順序 / 逆順、integerと同値double、nested配列、false / none / 空配列後の評価、128引数、1 / 4 / 16引数の全例外位置を確認します。例外なら結果null・後続未評価です。nested配列入力はadapter storageで構築して保持を観測し、nested Array式のparser実行証拠とはしません。
- arity 5件: 0 / 1 / 2 / 16 / 128引数を原典の引数数検証ブロックで受理し、全て未評価です。`result:true` はadapter sentinelです。constructor全体の実行や最大引数数の網羅実測ではありません。

**未検証の境界:** parser / optimizer / loader / 公開`.flt` / folder scanner / side metadata / Prop / GUI / 製品E2Eはこのfixtureでは実行しません。regex / FileContentRefは原典ValueType宣言へ保持しますが入力やserializerへ含めません。Timestampはnative値slotの保持のみで、時刻文字列のparseやtimezoneを検証しません。noneを「不在metadata」と同一視しません。非有限double・negative zeroのbit保持、不正Unicode、共有配列pointer identityは対象外です。

## 再採取と独立reader

出力は下記Dの専用rootのfresh child pathだけを受理し、repository / `.codex` / CODEX_HOME / reparse pointを拒否します。TEMP / TMPとcompile全出力もそのrunへ集約します。既存runを上書きしません。`run1` / `run2` が既存なら一意名へ変更してください。

```powershell
$python = (uv python find 3.13)
$fixture = 'tests/Fixtures/FolderArrayOracle'
$root = 'D:/Claude/DiffBeacon/array-20261019-impl02/native-array-oracle'
$vc = 'C:/Program Files/Microsoft Visual Studio/18/Community/VC'
$compiler = "$vc/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe"
$vcvars = "$vc/Auxiliary/Build/vcvars64.bat"
& $python -B "$fixture/capture.py" --output "$root/recheck1" --compiler $compiler --vcvars $vcvars
& $python -B "$fixture/capture.py" --output "$root/recheck2" --compiler $compiler --vcvars $vcvars
& $python -B "$fixture/verify.py" --observed "$root/recheck1/golden.ndjson"
& $python -B "$fixture/verify.py" --observed "$root/recheck2/golden.ndjson"
```

`verify.py` はcaptureや製品をimportしない独立Python readerです。全fixtureファイルのSHA・size、ZIP全entry CRC、Git blob、原典span、二回のcompile入力・exe・receipt・生stdoutを照合し、型付き入力から別実装で結果形状・例外・評価順を計算します。ZIPはメモリーで読みます。`--observed` は再採取のraw stdoutを固定bytesへ照合します。

`capture.py --initialize-source <reference-source.zip>` は同じdirectoryのsource-manifest.jsonに固定commitと各blob記録がある初回専用入口です。`freeze.py --run1 <fresh-run1> --run2 <fresh-run2>` も初回専用で、既存golden / native-proof / fixed manifestを上書きしません。通常再検証では両方不要です。固定`native-proof.zip`はexe・全compile入力・実行stdout / stderr・receiptを保持します（再生成可能なobjを除く）。

初回raw compile / runは `D:/Claude/DiffBeacon/array-20261019-impl02/native-array-oracle/run1` と `run2` へ保持しています。親の清掃判断まで削除しません。固定ZIPから再検証でき、不要になった展開runは通常の正規清掃対象です。
