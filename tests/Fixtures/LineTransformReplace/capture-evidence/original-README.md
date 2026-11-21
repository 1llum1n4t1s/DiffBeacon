# Replace 原典 fixture 候補（製品未変更）

固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` の旧C++原典を外部Tempでcompileし、100件の出力を採取した。`golden.ndjson` は実行stdoutそのもの。手書きgolden、std::regex、.NET Regexによる代用はない。

## 実行境界

- `function-body-noopt` は無改変 `ReplaceFunc`、`getAsString`、`ToStringValue`、`applyToScalarOrArray`、`escapeRegex`、`getRegexFlags`、二つの `regexSubst` を実行。
- `optimizer-selected-branch` は原典4375行のReplace分岐、`OptimizeRegexArg`、`ReplaceFunctionWithLiteral`、`RegularExpressionLiteral` constructorを無改変で実行。外側optimizerの制御文脈だけadapterで供給。旧parserとwhole optimizerは未実行。
- Pocoの無改変 `RegularExpression` constructor/destructor、inline subst forwarding、subst/substOne、compileOptions/matchOptions と MatchData を実行。matcherは同commitのPCRE2 10.47 `pcre2.c` amalgamation、既存config、Unicode表を実compileした。
- adapterは同じValueType alternatives、FilterEvalContextのcaseSensitive、FunctionNode所有権、Poco class宣言/Exception継承、AST構築、値JSON化、noopt引数評価traceだけを接続。Timestamp／FileContentは構築せず、その未使用型をshimで接続。Poco Exceptionのwhat表示はruntime_error接続で、旧Poco全例外階層の表示同等性は主張しない。
- optimizerケースはliteral型のdynamic_castを保つためtrace wrapperなし。dynamic nodeは非literal文字列を明示。array constructorは入力値だけを構築し、Replace内の配列処理は原典本体。
- 予算と取消は旧oracleとの同等性対象外。全体LineTransform/provider/CSV/旧parserの実行もこの候補の対象外。

## 原典で観測した差

空needle: nooptはnone。selected optimizerはliteral空needleをempty regexにするため、`abc`→`XXX`、空subject→空string、empty replacement→空string。Unicode subject `あい` とempty regexはPocoが1byte進める経路でUTF-8不正になり、regexSubstがnoneを返した後に原典constant foldのstd::get<string>がbad_variant_accessを投げる（例外を保存）。

literal needleの置換 `$0` は一致文字、`$1` は空、`$10` は`$1`＋`0`、`$$` は二つの$、末尾$と$xはそのまま。Regex型needleの捕捉 `$1` も別ケースで採取した。Unicode ciはPCRE2 UTF/UCPを使い、Kelvin／sigma等の実出力を保存。arity、型不正、none、配列/nested配列、eagerThrow、noMatch、overlap、物理改行、regexメタのliteral escape、NUL、invalid UTF-8とinvalid regexの原典拒否を含む。

## 原本・ライセンス

`reference-source.zip` は原本Git blob bytesと無改変抽出inc、最終compile command/logを圧縮保持。全entryは`source-archive-manifest.json`でsize/SHA-256を独立照合。`provenance.json`にGit SHA1 blob、SHA-256、byte/line範囲、worktreeのexact比較とCRLF→LF比較を分けて記録。GPL-2.0（原本LICENSE.md）、Poco BSL-1.0（原本Externals/poco/LICENSE）、PCRE2 BSD条件・Unicodeの同梱通知（各原本source/header先頭）をそのまま保持。

## 再現・復元

Windows x64の既存MSVC14.51とPython3で、固定Git commitが存在するDiffBeacon checkoutを使う。製品.NETにはC/PCRE依存を追加しない。必要なら`capture.py`のREPOとvcvars/compiler絶対pathを環境に合わせたコピーで変更する。出力は新しい、repo/.codex外の作業別Tempに指定する（名前artifactsやreparse ancestor不可）。

```powershell
& C:/Users/IMT/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe <この保存先>/capture.py --output <新規の外部Temp>/reference-run
```

cases.jsonとadapter.cppはcapture.pyと同じ場所に置く。scriptは固定Git blobをbytesで取得して新規出力にcompileし、既存fileは上書きしない。復元原本はreference-source.zipのoriginal/から取り出せる。2回独立compile/実processのstdout bytes一致、全ID順序、実PID/birth/held-handle終了/EOF/timeout falseはproof/receiptsに保持。

保持理由: Replace移植判断と採取の再現に必要な最小証拠。削除条件: 移植の採否が決定して同等の原典fixtureを正式採用し、この候補の再開・復元が不要になった時。ビルドobject/exeと途中試行は保持せず、外部Tempを共通ExactTrashで清掃する。cleanup結果は別台帳参照。

## 抽出範囲metadataの訂正

構造体・enumの10範囲は原典の閉brace直後にあるsemicolonまで `endByte`（排他的上限）に含める。semicolonはadapterの創作・追加ではなく原典の実byte。全31範囲で `SHA256(rawGit[startByte:endByte]) == provenance.sha256` を直接照合する。各incの末尾に付く1個のLFはinclude wrapperの外側区切りであり、原典範囲とSHAに含めない。したがってZIP内inc全bytesは `rawGit[startByte:endByte] + b"\n"`。

この修正は範囲metadataと採取ruleだけを訂正し、原典ZIP・inc・golden・cases・実process receipts・adapterのbyteは変えていない。旧metadataをmetadata-backupに限定保存し、復元先・理由・削除条件は同folderのrestore.jsonに記録。metadata-reader.pyは別のreaderで53原本のrawGit/SHA1/SHA256、31範囲、ZIP全entry size/SHAを照合する。実compiled codeのbytesは変わらないため再compileは不要。
