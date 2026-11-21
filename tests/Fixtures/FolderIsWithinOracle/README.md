# IsWithin の固定 native oracle

WinMerge commit `6780924ba35f7f55af0f6f14dd302561175830a6` の `IsWithinFunc` をMSVC x64の独立した2 compile／processで実行した159件の型付き期待値です。`failure-matrix.md`、typed `cases.json`、adapterの順に作成しました。InRangeの期待値を変換せず、IsWithin原典全体を直接照合して実行しています。

原本は `FolderInRangeOracle/original` の固定Git blobから無改変コピーし、ネットワークから再取得していません。単独fixture内に原本・GPL／Poco BSL-1.0・出典・Git blob・SHAを保持しています。readerと再採取はsibling fixtureに依存しません。

| 原典 | 行 | 実行する範囲 |
| --- | --- | --- |
| FilterExpressionNodes.cpp | 1693–1705 | shallow array helper |
| FilterExpressionNodes.cpp | 2157–2234 | IsWithinFunc全体 |
| FilterExpressionNodes.cpp | 3802 | iswithinの登録と引数数3..3 |
| FilterExpressionNodes.cpp | 4269–4280 | constructorのarity検証ブロック |
| Poco/String.h | 286–309 | icompare全体 |
| Poco/Ascii.h・Ascii.cpp | include-specs.json参照 | ASCII分類、lower変換と分類表 |
| Poco/Timestamp.h・Timestamp.cpp | include-specs.json参照 | 実class、value/copy/assignment/destructor、比較 |

`FilterExpressionNodes.h` の20行はadapterのValueType順序の根拠です。抽出範囲は `include-specs.json`、全原本の出典・Git blob・SHAは `provenance.json`、fixture全ファイルのinventory／SHAは `manifest.json` に固定します。includeは原本の行bytesを無改変抽出します。

入力はint64、double（16桁IEEE754 bits）、Timestamp（整数マイクロ秒）、UTF-8 string、none／bool／regex／file／arrayです。159件で両端包含・singleton・逆順、int64端点、2^53付近の8種類の数値型組合せ、±zero・subnormal・finite最大値・infinity・±quiet NaN、Timestamp同型と異型、NUL・非ASCIIとcs/ci、値の浅い配列／nestedと配列境界、arityとthrow traceを確認します。結果はbool／None／arrayの型を区別します。

原典は `lower <= value <= upper` です。int64値は比較する辺ごとに必要な場合だけdouble化し、double値では両数値境界をdouble化します。Timestampとstringは各々同型の境界だけを受け付けます。異型はNone、値配列は一段だけ適用し、nested要素はNone、境界配列は展開しません。引数の評価順はlower(1)→upper(2)→value(0)で、不正境界や空値配列でも引数評価を省略しません。不正arityは全引数の評価前に拒否します。

文字列はこの実測ABIに限定します。cs経路の `std::string::compare` はunsigned UTF-8 byte順です。ci経路の固定Poco `icompare` はASCII fold後にcharへ戻して比較します。このMSVC x64ではsigned charのため `IsWithin("é", "", "z")` はcs=false、ci=trueです。Unicode case foldingは行いません。native自身が `reference.stderr` にpointerBytes=8、charSigned=true、x64=true、msvcVersion=1951を出力し、receiptとreaderで確認します。compile flagsは `/std:c++20 /EHsc /utf-8`、`/J`は指定していません。他OS／compiler／char ABIへ順序を一般化しません。

adapterはtyped literal・throw spy・型付きJSON出力を供給します。完全なFunctionNode constructor、parser、Optimize、folder `.flt` loader／scanner、公開CLI、実ファイルcontext、共有budget、managed取消／出力保護は実行しません。throw診断は注入spyの結果です。元parserから全typed入力を表現できるとは主張しません。この159件だけでfolder公開互換件数を増やせず、InRangeやLineTransformRangesの意味範囲も流用しません。

Python 3.13+ と既存MSVC x64 developer環境を使い、未使用の外部Dディレクトリを指定します。D不在、既存run、reparse祖先、repo／Codexユーザーディレクトリ内、`artifacts`名の祖先を拒否し、Cへfallbackしません。TEMP／TMP、compiler出力、raw streams、抽出include、原本／adapter／入力snapshot、receiptは指定run内へ集約します。

```powershell
<Python3> -B tests/Fixtures/FolderIsWithinOracle/reproduce.py --output D:/Claude/DiffBeacon/<unique-run>/run1 --compiler cl.exe
<Python3> -B tests/Fixtures/FolderIsWithinOracle/reproduce.py --output D:/Claude/DiffBeacon/<unique-run>/run2 --compiler cl.exe
<Python3> -B tests/Fixtures/FolderIsWithinOracle/verify.py
<Python3> -B tests/Fixtures/FolderIsWithinOracle/verify.py --evidence D:/Claude/DiffBeacon/folder-iswithin-oracle-20261020-r1
```

`verify.py` は製品・adapter・producerをimportせず、独立したPythonの整数／明示binary64変換、UTF-8 bytes、ASCII fold後のsigned byte列、型拒否、浅い配列と評価traceから期待値を計算します。fixture inventory／SHA、原本Git blob／SHA、ID／件数、型付きgoldenを照合します。raw evidence指定時は `capture-index.json` の2receipt、異なるPID、実終了code・held handle signaled・両stream EOF、process記録、compiler／executable SHA、原本と入力snapshot、抽出include全byte、ABI、goldenとのstdout全byte一致も確認します。

初回raw evidenceは `D:/Claude/DiffBeacon/folder-iswithin-oracle-20261020-r1/run1` と `run2` に保持します。新しいreceiptはPID／時刻が異なるため固定indexと一致しませんが、producerは再採取stdoutとgoldenの全byte一致を要求します。将来の再現・監査のため今回のD領域を保持し、代替証拠の検証完了後が清掃可能条件です。今回は削除していません。製品build／E2E／UI／Native AOT／macOS／Linuxはこのfixtureでは未検証です。
