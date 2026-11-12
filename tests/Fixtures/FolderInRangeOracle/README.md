# InRange の固定 native oracle

WinMerge commit `6780924ba35f7f55af0f6f14dd302561175830a6` の `InRangeFunc` を、MSVC x64 の別々にコンパイルした実行ファイル・独立プロセスで2回採取した145件の型付き期待値です。最初に `failure-matrix.md` を作成・確認してからadapterと入力を作りました。ネットワークから再取得せず、既存fixtureの固定Git blobを無改変コピーしています。出典・Git blob・SHA-256は `provenance.json`、全ファイルのSHAは `manifest.json`、GPLとPoco BSL-1.0は `original` 内に保持します。

`include-specs.json` は固定原典からの無改変行抽出を指定します。

| 原典 | 行 | 範囲 |
| --- | --- | --- |
| FilterExpressionNodes.cpp | 1693–1705 | shallow array helper |
| FilterExpressionNodes.cpp | 2236–2313 | InRangeFunc全体 |
| FilterExpressionNodes.cpp | 3801 | 登録名と引数数3..3 |
| FilterExpressionNodes.cpp | 4269–4280 | constructorの引数数検証ブロック |
| FilterExpressionNodes.h | 20 | ValueTypeの型と順序の根拠 |
| Poco/String.h | 286–309 | icompareの全定義 |
| Poco/Ascii.h・Ascii.cpp | include-specs.json参照 | ASCII分類・lower変換・固定分類表 |
| Poco/Timestamp.h・Timestamp.cpp | include-specs.json参照 | 実class・value/copy/代入/destructor・比較 |

型付き入力はint64、double（16桁IEEE754 bit列）、Timestamp（整数マイクロ秒）、UTF-8 string、none/bool/regex/file/arrayです。int64端点、2^53近辺の8種類の数値型組合せ、半開区間、等しい／逆順境界、NaN・infinity・符号zero・subnormal、Timestamp同型比較と異型拒否、NUL入り文字列、ASCII foldと非ASCII、配列境界と値の浅い配列・入れ子、arity、throw traceを含みます。出力はbool／none／arrayを型付きで区別します。

混在数値は比較する各辺だけでint64をdouble化し、他方がint64なら整数比較を保持します。double値では両境界をdouble化します。値配列は一段だけ適用し、配列境界は展開しません。正常arityの評価順は下限1→上限2→値0で、不正境界でも値は評価されます。不正arityは全引数の評価前に例外です。throwはadapterの注入例外で、パーサー診断ではありません。

文字列順序の実測はこのABIに限定します。`@cs`に相当する経路は `std::string::compare` のunsigned byte順です。`@ci`に相当する固定Poco経路はASCIIをfold後、`char`へ戻して比較します。実行したMSVC x64はsigned charで、`InRange("é", "", "z")` はcs=false、ci=trueでした。非ASCIIのUnicode case foldingはしません。`reference.stderr` はnative自身が出すABI JSON（pointerBytes=8、charSigned=true、x64=true、msvcVersion=1951）です。compile commandは `/std:c++20 /EHsc /utf-8` を記録し、`/J`は指定していません。他OS・compiler・char ABIへ同じ順序を一般化しません。

adapterはtyped literal・throwing node・型付き出力を供給し、関数・helper・登録行・arity検証とPoco実定義を抽出して実行します。完全なFunctionNode constructor、parser、Optimize、folder `.flt` loader/scanner、managed evaluator、公開CLIは実行しません。この145件だけでfolder公開経路の互換件数を増やせません。`LineTransformRanges` の行変換serialized-key証拠も別の意味範囲で、folder公開互換件数への流用はしません。

Python 3.13+ と既存MSVC x64 developer環境で、未使用の外部Dディレクトリを指定します。全compiler出力・TEMP/TMP・raw streams・receiptはその指定先へ集約します。D不在・既存出力・reparse祖先・repo/Codexユーザーディレクトリ内・`artifacts`名の祖先は拒否し、Cへfallbackしません。

```powershell
<Python3> -B tests/Fixtures/FolderInRangeOracle/reproduce.py --output D:/Claude/DiffBeacon/<unique-run>/run1 --compiler cl.exe
<Python3> -B tests/Fixtures/FolderInRangeOracle/reproduce.py --output D:/Claude/DiffBeacon/<unique-run>/run2 --compiler cl.exe
<Python3> -B tests/Fixtures/FolderInRangeOracle/verify.py
<Python3> -B tests/Fixtures/FolderInRangeOracle/verify.py --evidence D:/Claude/DiffBeacon/folder-inrange-oracle-20261020-r1
```

`verify.py` は製品コード・adapter・producerをimportせず、Pythonの整数／明示binary64変換、独立したUTF-8 byte処理、ASCII fold後のsigned byte列、型拒否、浅い配列、評価traceから全期待値を計算します。fixture inventory／SHA、原本のGit blob／SHA、case ID、型付きgoldenを照合します。`--evidence` は初回固定 `capture-index.json` の2receipt、異なるprocess identity、実終了code・held handle signaled・両stream EOF、compiler/executable SHA、抽出include全byte、stdoutとgoldenの全byte一致、ABI JSONを確認します。新しいreceiptは時刻とPIDが変わるため固定indexと一致しませんが、`reproduce.py` 自体がgoldenとの全byte一致を要求します。

保持する正規raw evidenceは `D:/Claude/DiffBeacon/folder-inrange-oracle-20261020-r1/run5` と `run6`。run1は初回のTimestamp代入定義不足によるlink失敗、run2はABI出力追加前、run3/run4はhighbit順序の明示case追加前で、失敗・採取履歴として同じtask領域に保持しています。これらは145件の証拠数に含めません。将来の再現・監査のため保持し、代替証拠検証完了後にこのtask分だけ正規清掃できます。製品build・E2E・App/UI・Native AOT・macOS/Linuxはこのfixtureでは未検証です。
