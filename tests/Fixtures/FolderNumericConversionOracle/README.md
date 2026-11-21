# ToInt / ToDouble / ToNumber の固定C++原典

`cases.json` の246件（各関数82件）を固定commit
`6780924ba35f7f55af0f6f14dd302561175830a6` の関数本体で採取した。
`FAILURE-MATRIX.md` はadapter実装前に作成した失敗条件。
原典は既存 `../LineTransformSearch/original/` からraw bytesを複製し、既存provenanceの
SHA-256・sizeとGit blob SHA-1（`blob <size>\0` prefixを含む）を独立計算して一致確認した。
新規のリモート取得・Gitコマンドによる取得はしていない。
`provenance.json` に原典パス・commit・blob・SHA、`original/COPYING` と
`original/Poco-LICENSE` にライセンスを保持する。近い `.gitattributes` は全fileを `-text` に固定する。

`reproduce.py` は原典 `FilterExpressionNodes.cpp` の1693–1704（shallow map）、
2315–2357（文字列化）、2520–2640（comma除去と3変換）を無改変で切り出す。
明示typed ExprNode adapterと接続し、MSVC x64 19.51.36260.0 / C++20 / Odで実行した。
Pocoの日時・FileContent・Regexは未使用の型bindingで、日時とFileContentの実行は例外にする。
旧parser・FunctionNode constructor・optimizer・公開`.flt`経路は実行していない。
登録は3844/3848/3851行で全て1引数。引数0・余分な引数は関数本体だけの結果であり、公開APIの期待値へ転用しない。

## 固定範囲と注意点

- 入力stringだけを変換する。commaは位置や桁区切りの妥当性を問わず全除去し、先頭ASCII空白は許すが末尾空白・suffix・NULは全消費にならずNone。
- int64の両端を含み、境界外はNone。整数への小数・指数・hexはNone。doubleのNaN/inf/hex/min subnormal/最大有限/overflow/underflowを型とIEEE-754の16桁bitsで固定する。
- `ToDouble("0x10")` は16、`ToNumber("0x10")` はNone。`ToNumber` はdot/e/Eを含む場合だけdoubleを選び、`"0xDEAD"` は57005のdouble、`"0x1.8p2"` は6のdoubleになる。整数overflow時のfallbackはない。
- `ToDouble("nan")` はこのMSVCでbits `7fffffffffffffff`、`"inf"` は正無限。`ToNumber` はどちらもNone。最小subnormal `4.9406564584124654e-324` はbits 1で成功し、`1e-9999` はNone。CRT差・別OS差は未検証。
- arrayは一段だけmapし、内側のarrayはNoneに変わる。nested inputと結果の原典文字列化を `inputStringHex` / `stringHex` で固定する。double文字列化はこのMSVCの `std::to_string` による小数点以下6桁で、巨大有限値も省略しない。
- 引数評価は先頭1回。extraのthrow nodeは評価されず、firstのthrowは変換catchの外へ伝播する。全件に評価traceを記録し、3件だけ注入評価例外、他243件は正常（Noneも正常値）となる。

`scope` はpublic-string-candidate 198件、oracle-only-typed-binding 30件、
oracle-only-embedded-nul 9件、oracle-only-function-body 9件。
public candidateも公開構文の成功を実測した意味ではない。embedded NUL、typed double/None/nested arrayの直接注入、
不正引数数、throw nodeは公開`.flt`から表現できると主張しないoracle専用の境界として区別する。

## 再現と独立照合

新規の通常Dフォルダーを指定する。既存output・link祖先・repo内outputを拒否し、
compilerのobj/exe/log・generated include・TEMP/TMP・stdout/stderrはoutput内へ置く。
親フォルダーの存在・空き容量・reparse不在は起動前にも確認する。

```powershell
$python = 'C:\Users\IMT\AppData\Local\Python\bin\python.exe'
& $python -B -X utf8 tests/Fixtures/FolderNumericConversionOracle/reproduce.py --output '<作業フォルダー>\DiffBeacon\<unused-run>' --vcvars 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'
& $python -B -X utf8 tests/Fixtures/FolderNumericConversionOracle/verify.py
```

固定captureの外部証拠も確認する場合は `verify.py --capture <作業フォルダー>\DiffBeacon\numeric-oracle-20261019-r1\capture-certified`。
別runのreceiptはPID・creation time・exe SHAが変わるため、固定capture.jsonとの同一性検査は固定run専用。
再採取stdoutの照合は `verify.py --actual <new-output>/run-1.ndjson` を使う。
独立readerはadapter/reproduceをimportせず、manifest・原典blob・全typed input・trace・件数・代表境界・nested文字列化を検査する。

`capture.json` は別process 2回のPID・Windows creation FILETIME・保持handle・終了code・EOF・stdout/stderr/exe SHAと
compiler command/log SHA・採取入力SHA・切出しinclude SHAを持つ。stdoutは両方51711 bytes、SHA-256は
`52d07df1d5b0da74350a3d946c344ea8e6fc5aada7513fdedebccb852c2e125a`。
`golden.ndjson` はその無改変stdout。compiler/runtime logは外部capture-certifiedに保持する。

親の指示によりD workrootの中間capture/capture-finalも削除していない。
正式採取はcapture-certified、以前の2folderは採取準備の証拠でgoldenの正本ではない。
通常.NET buildへの依存は追加しておらず、製品コード変更・E2E・UI検証はこのfixture作業の範囲外。
