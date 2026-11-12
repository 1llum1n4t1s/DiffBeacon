# Windows folder Property 固定native原本

固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` の旧関数を無変更抽出し、MSVC x64で二回独立compile・別child実行した148件です。`golden.ndjson` SHA-256は `c2fa2b016932c823ab0e7de0fc0870e581bceebd43925832c7352b2abdcc077e`。期待値はnative stdoutそのもので、製品呼出し・期待値の手編集は行いません。

## 原典と実行範囲

`reference-source.zip` に12 raw Git blobsとGPL-2.0／Poco BSL-1.0ライセンスを保持します。`source-manifest.json` のcommit・Git blob SHA-1・SHA-256・sizeを検証し、`provenance.json` の21 spanをbyte範囲・行番号・SHA-256で固定します。

| 原典 | 無変更実行する範囲 |
| --- | --- |
| `FunctionNodeProperty.cpp:11-114` | 全変換、`prop`、`propary`、`SetPropFunc` |
| `PropertySystem.cpp:21-83,182-255` | hash key表／名前解決、ctor／dtor、canonical名の登録とindex検索、Shell store取得とhash fallback |
| `HashCalc.cpp:13-69` | BCryptアルゴリズムopen／hash作成／64KiB逐次read／finish |
| `FilterExpressionNodes.cpp:1509-1518,34-49` | side prefix解決、浅い`evalAsBool` |
| `FilterExpression.cpp:336-350` | 最終判定に使う再帰`ContainsTrue` |
| `Timestamp.cpp:58-72,171-183` | Timestamp ctor／copy／dtor／Windows FILETIME変換 |

PropertySystemとHashCalcは実Windows API（`PSGetPropertyKeyFromName`、`SHGetPropertyStoreFromParsingName`、`IPropertyStore::GetValue`、`CreateFile`、`ReadFile`、BCrypt）を呼びます。値を返す玩具property serviceやhash代替実装はありません。Shellへ渡す入力は実呼出し側と同じ絶対pathです。

`adapter.cpp` がAST storage／例外host型／DIFFITEMのpresenceとcache／比較rootとside数／UTF-8変換／path結合／JSON出力を提供します。`ValueType` はこの経路で使う型だけを宣言し、未使用regex／file-ref alternativesは省略しています。Timestampのクラス宣言は必要メンバーだけを置き、変換とctor実装は上記原関数です。typed値はserializer adapterで、旧`ToStringValue`による比較key採取ではありません。

FunctionNodeの通常constructor・lexer/parser・大小文字の正規化・optimizer・property名collector・`.flt` loader・folder scanner・GUIは実行しません。side関数名を小文字化済みとして`ParseAttributeName`と`SetPropFunc`へ直接渡します。実行した旧関数と、このcontext adapter、将来の製品bridgeは別の境界です。このoracleだけで全`.flt`互換や製品同等性を主張しません。

## ケースと観測

`failure-matrix.json` を実装前に作成し、`case-plan.json` に全IDと採取scopeを固定しています。

- 比較side数1／2／3について全presence maskとbare／Left／Middle／Rightを採取。bareは1 sideならscalar、複数なら順序を保った配列です。Middleはindex 1、Rightは最後のsideです。1 sideのMiddleは不在としてnoneになります。string vectorをside配列に入れたnested arrayも採取します。
- 全変換case：EMPTY、I4、UI4、I8、UI8、R8、LPWSTR、BOOL、FILETIME、vector LPWSTR、vector UI1。UI8最大値のsigned castは`-1`、byte vectorは小文字hex。R4／NULL／I2／BSTR／vector I4はnoneです。FILETIME 0は旧コードのunsigned減算により`1833029933770955161` microsecondsとなり、この観測を保持します。
- BOOLの0／-1／1／2を採取。`ContainsTrue`はnested array内のbool trueを再帰的に受理し、数値1・double 1・string `true`は受理しません。論理演算用`evalAsBool`は配列の直接bool要素だけを見るためnested true単独はfalseです。
- canonical名の有効／無効、System名とHash名の大小文字差、null args／0／2 args／非literal／無効property名を採取。採取環境では`system.size`、`hash.sha256`、`Hash.Sha256`は不受理です。
- 固定fileは空file、非ASCII名file、65536+9 bytesのfile。実ShellのSize／FileName／FileExtension／DateModifiedとMD5／SHA1／SHA256を取得し、Python `hashlib`が全file bytesから独立照合します。mtimeを`1700000000123456700` nsへ固定します。入力全bytesは`input.zip`、size／SHAは`input-manifest.json`です。
- mixed property storeは成功true、hash-only serviceは値が設定されても戻り値falseです。不在pathではSystem値はnone、Hash値は空hex文字列でfalse。実fileサイズ3→8の変更後、同じDIFFITEMはcached 3、cacheをresetすると8となります。

NULL LPWSTR、破損vector pointer、null shared array等の不正メモリ構造は実行していません。アクセス拒否／read中変更／BCrypt障害注入／Shell拡張固有property／network file／directory hash／非Windows環境は未検証です。全PROPVARIANT型を支援するという意味ではなく、switchの全明示branchと代表的default branchを採取しています。

## 再現と独立検証

Python 3.12以上、既存MSVC x64とWindows SDKを使います。旧Merge project、MFC、submodule、NuGet、製品buildは不要です。出力先はfreshな外部Tempだけを受理し、リンク／junction／repository／`.codex`／CODEX_HOME配下を拒否します。compilerのobj／exe／log／中間sourceはすべてそのrun配下へ置きます。既存fileを上書き・削除しません。

```powershell
$python = 'C:/Users/IMT/AppData/Roaming/uv/python/cpython-3.14.7-windows-x86_64-none/python.exe'
& $python -B tests/Fixtures/FolderPropertyFilters/verify.py
& $python -B tests/Fixtures/FolderPropertyFilters/reproduce.py `
  --output "$env:TEMP/Codex/DiffBeacon/folder-prop-oracle-new-run" `
  --compiler 'C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe' `
  --vcvars 'C:/Program Files/Microsoft Visual Studio/18/Community/VC/Auxiliary/Build/vcvars64.bat'
```

`verify.py`は製品とcaptureをimportせず、固定file SHA、source raw blob SHA／Git blob ID、21 spanの範囲／行番号／SHA、二回のnative stdout全bytes、compile入力全SHA、終了code／保持process handle signaled／stdout・stderr EOF、入力ZIP CRC／全bytes／全hashを照合します。`--actual <ndjson>`で追加採取も固定stdoutへ比較できます。JSON中のinteger／timestampは精度損失を防ぐため10進stringで保持します。

`native-proof.zip` は成功した初回run3／run4をrun1／run2名で収録し、source抽出物・adapter・compiler command／response file・compile log／receipt・実行receipt・stdoutを保持します。exe／obj等のcompiler出力はZIPにも入れません。実exeのSHAはreceipt、実体は指定外部Tempのrun3／run4に保持します。初回run1はadapterのVARTYPE initializerのcompile失敗、run2はrelative pathによるShell取得失敗を識別する試行で、正式golden／proofには含めません。試行分を含む外部Tempは親の保持指示に従って残します。

`capture.py --initialize-source` と`freeze.py`は初回固定用で、既存fixtureを更新する操作ではありません。`fixed-manifest.json`は自身以外の全fixture fileを封印します。新しい原本へ移行する場合は別fixtureとして採取し、既存期待値を上書きしないでください。

## 残る統合範囲

製品のproperty service、folder比較context、`.flt` parser／loader、Side prefixの通常登録、canonical validation、実際のfilter評価と除外結果へbridgeを接続し、既存E2E／UI検証で比較する作業は未実施です。Shell serviceのWindows依存性、取得不能値、hash fallback戻り値、cache寿命を製品仕様として採用する判断もこのfixtureだけでは確定しません。
