# LineTransformReplaceLists 固定native原本と公開bridge

固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` の `ReplaceWithListFunc`／`RegexReplaceWithListFunc`、`ReplaceList.cpp` の `GuessEncoding`／`LoadListImpl`／`LoadList`／`LoadRegexList`、Poco regex、PCRE2を使った70件のnative採取です。無変更の関数spanをMSVCで二回独立compile・別child実行し、stdout全bytesを一致確認しました。`golden.ndjson` SHA-256は `ce461a0988472d98d45c78485d33d9def6bdd22cf34cdce32ccfddcce3776d3a`。期待値の手編集・算術生成・製品呼出しは行いません。

## 出典と実行境界

原典61 raw blobsは既存 `LineTransformRegex/reference-source.zip` と、このfixtureの `additional-original` の4 blobsを使います。既存ZIPの重複コピーはしません。新規4 blobsは `Src/FilterEngine/ReplaceList.cpp`／`.h` と `Src/Common/UniFile.cpp`／`.h` です。全sourceのcommit・Git blob ID・SHA・sizeは `source-manifest.json`、無変更spanのbyte範囲・行番号・SHAは `provenance.json` に固定します。主関数は `FilterExpressionNodes.cpp` の1996／2046行、登録は3829／3831行、リストの最適化読込みは4437行以降です。GPL-2.0とPoco BSL-1.0、PCRE2のライセンス原文は再利用ZIP内の `original/LICENSE.md`、`original/Externals/poco/LICENSE`、PCRE2 sourceのheaderに保持されています。

nativeのリスト分割・選別・escape・regex構築・逐次置換・配列lifting・serializerは原関数を実行します。typed compiled regexの構築も元Poco/PCRE2です。Exceptionのhost型・context・AST保管・loggerはadapterです。

**UniMemFile・encoding推定・環境変数展開はnative実装を実行していません。** `adapter.cpp` のファイル層はUTF-8 bytesとUTF-8 BOM、CR/LF/CRLFだけを扱う明示adapterで、encoding設定と推定は固定UTF-8、env展開はidentity、open errorは固定adapter文言です。`GuessEncoding` と `LoadListImpl` は原関数ですが、その呼出し先のOS/encoding層にはこの境界があります。UniMemFileの原典ReadBom／ReadStringは無変更source spanとして固定するだけで、原典OS I/O・BOM判定・任意codepage・推定・lossy処理・path正規化の同等性は主張しません。BOM/file/open failureの全ケースはnative-onlyです。

## 採取内容と観測

- literalとregexの全一致置換、大小文字設定、複数ruleの逐次適用、literal metachar escape、capture replacement、空検索／空置換。
- 空行、先頭`#`、tabなし、先頭空白付き`#`、最初のtabによる分割、3列目以降の無視、末尾改行なし、混在改行、空file、UTF-8 BOM／非ASCII。
- invalid regexを診断して当該ruleを飛ばし後続ruleを残す読込み、open failure時の診断／空list／本文保持。literal loaderの`[`はescapeされるため有効です。
- string pair、compiled regex pair、両者の混在、非pair、pair要素数不一致、非string replacement／非string pattern、空list。
- 配列sourceの直接要素lifting、integer／boolの文字列化、none保持、nested arrayのserializer経由文字列化。
- arity 0／1／3とnull args、両引数のeager評価と先頭／後続throw。

`ReplaceWithList` はcompiled regex＋string replacementの2要素pairだけを適用し、string pattern pairは無視します。`RegexReplaceWithList` はcompiled regexとstring patternの両方を受理します。後者のinvalid string patternはregexSubstのfalseを呼出し側が無視するため、そのruleの置換を行わず後続へ進みます。各結果はtyped value、実ToStringValueのUTF-8 hex、引数評価順、loaded listのpair形状、loaderと関数のlogger出力として採取します。compiled regexのtyped表示はopaqueで、公開typed regex identityや内部compiled object同等性を表しません。file caseではloaderを単独採取した後、実関数のpath branchをもう一度実行し、それぞれの診断を分けて保持します。

## 公開planとreader

`public-case-plan.json` は各項目を `{id, expression, sourceLineCount, expectedOutcome, expectedSerializedKeys}` の5 fieldsで固定します。全70件中、`serialized-key-comparison` 18、`runtime-rejection` 4、`compile-rejection` 6、`native-only` 42です。期待keyはnative goldenの実serializer結果を参照するだけで計算しません。

比較18件は公開で表せるstring pair等の式です。compiled regex pairとfile API／encoding／OS readerはnative-onlyに分離します。native-onlyのexpressionは安全な `Function(none,Array())` placeholderで、native adapter modeの再現や同等性を表しません。native関数の不正arityはnoneを返しますが、公開parserでのarity拒否は別契約なのでcompile-rejectionとして分離します。throw相当は公開式のprovider依存ゼロ除算を使い、例外class同等性を主張しません。

`verify-public.py --actual <public-observations.json>` はnative-onlyを除く28項目をplanの順に照合します。観測schemaはMatchInside fixtureの公開readerと同じ18 fieldsです。ID・expression・expectedSerializedKeys・expectedExpression・expectedError・passed、baselineのexit/stdout/stderr、最適化あり式とCLI JSONの `different=false` を検査します。比較時のreference式は `@noopt Choose(LineNumber-1,"<native serialized key>")`、拒否時は `Line`。拒否のexitは2／stdout空／stderr非空、最適化観測はnull／空です。`actualTypedValueObserved`・`contextEquivalent`・`nativeExceptionEquivalent` はfalseを要求し、summaryの `typedPublicEquivalence` もfalseです。公開keyはCLI比較による間接観測で、typed pairの直接観測ではありません。

public readerはschema-shaped模擬観測28件で成功し、改変20件をすべて拒否しました。模擬観測成功を製品の実測成功として数えません。製品build／E2E／GUIはこのfixture作成では実行していません。whole parser／optimizer、最適化時のfile寿命、実UniMemFile／encoding推定／env、任意引数型のunsafe native pointer dereference、AOT／他OSも未検証です。

## 独立検証と再採取

`native-proof.zip` は二回のcompile入力、compile出力、実child stdout/stderr、process receipt、UTF-8 list入力を保持します。exe/obj、PCRE2 raw sourceの重複コピーは含めません。compile receiptのPCRE2全source SHAを再利用原典へ照合します。readerは全fixed fileset・source blob・抽出span・proof entry SHA/size/CRC・compiler入力・PID/birth time/保持handle・exit/EOF/timeout・二回のstdout全bytesを検査します。`--git` は全source blobと固定fixtureの`-text`属性も照合します。

```powershell
& $python -B tests/Fixtures/LineTransformReplaceLists/verify.py --verify --git
& $python -B tests/Fixtures/LineTransformReplaceLists/reproduce.py `
  --output "$env:TEMP/Codex/DiffBeacon/replace-lists-r1/fresh-reproduction" `
  --compiler 'C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe' `
  --vcvars 'C:/Program Files/Microsoft Visual Studio/18/Community/VC/Auxiliary/Build/vcvars64.bat'
& $python -B tests/Fixtures/LineTransformReplaceLists/verify.py --actual $nativeStdout
& $python -B tests/Fixtures/LineTransformReplaceLists/verify-public.py --actual $publicObservations
```

Python/MSVC/PCRE2 C compileは検証専用で、通常.NET buildの依存に追加しません。再採取はrepoとCodex user directory外のfresh Temp出力を指定します。固定原本を更新する際は先に `contract.json` の失敗条件を確認し、二回の独立compile／childの全stdout一致を確認します。再開・再検証の原本はこのfixtureで保持し、外部Tempの採取一式は親による最終検証の引継ぎまで保持します。
