# LineTransformMatchInside 固定native fixture

固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` の `LineMatchInsideFunc`、`GetOrCreateInsideRanges`、`evalAsBool`、実serializerを無変更で抽出し、MSVCで二回compile・別childで実行した59件です。goldenはstdout原本で、SHA-256 `4b8061442df10a5b0e89c1dfdbfed402a7b2b80c66bfbfc3ae72aaebcc756b95`。期待値の手編集・算術生成・製品呼出しは行いません。

原典はsibling `LineTransformReplace/reference-source.zip`（SHA-256 `18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7`）と `LineTransformAggregates/additional-original` を再利用し、重複コピーしません。`source-manifest.json` が15 raw blobのSHA・size・Git blob IDを、`provenance.json` が無変更spanのbyte範囲・行番号・SHAを固定します。原典は `Src/FilterEngine/FilterExpressionNodes.cpp` のinside helper:3256、MatchInside:3410、登録:3817です。`Testing/GoogleTest/FilterEngine/FilterExpression_test.cpp` の3982行以降の既存5 assertions（内側・両境界・開始前・終了後）は読み取り確認し、blob/span SHAを `source-test-evidence.json` に記録しました。既存GoogleTest自体は実行していません。GPL-2.0原文はZIPの `original/LICENSE.md`、Poco BSL-1.0は `original/Externals/poco/LICENSE` を参照します。

ケースは空・未開始・先行end・未終了・両境界・連続／離れた範囲・開始前の同時成立・開始中の同時成立で同じ行から再開・nested begin・scalar none/integer/double/string/Timestamp/raw bytes・直接／入れ子bool配列・begin/endの先頭／中央／末尾throwを含みます。各行を二巡し、typed値・serialized UTF-8 hex・評価順trace・外側lineIndex・cache数とnative範囲を採取します。pairのbeginだけ／endだけ／両方／順序の変更、同pairの別wrapper、cache後のpredicate変更、cache clear、実際に別のshared contextを使用する二巡目も採取します。別inner pairが完走した後にouterがthrowするprobeは、inner cacheだけが残ることを確認します。

原典は各行begin→endの順に両方を評価します。beginがthrowするとendへ進まず、endがthrowするとbeginまでのtraceを残して外へ伝播します。完走前にouter cacheは公開しません。bool変換不能はfalseで、配列内の直接boolのみを処理し、nested arrayを再帰しません。境界を含み、未終了はEOFへ伸ばし、開始中のbeginだけではnested rangeにしません。`cancel-at-begin/end` はadapterが例外を注入する取消相当のprobeであり、nativeに取消APIがあるとの主張ではありません。

`native-proof.zip` は二回のcompile入力、compile stdout/stderr、child stdout/stderr、compile/process receiptを保持し、exe/objを含みません。`proof-manifest.json` は全entryのSHA・size・CRCを固定します。readerは固定fileset、全source blobと抽出span、compile入力、PID・birth time・保持handleのsignaled状態・実終了・両EOF・timeoutと二回のstdout全bytesを照合します。receipt内の絶対パスは採取時証拠です。`fixed-manifest.json` は自身を除く全fixture fileを固定します。

`public-case-plan.json` は各項目を `{id, expression, sourceLineCount, expectedOutcome, expectedSerializedKeys}` の5 fieldsに固定します。全59件中、serialized-key-comparison=35、runtime-rejection=6、native-only=18 です。公開式は `@cs @noopt MatchInside(Choose(LineNumber-1,...),Choose(LineNumber-1,...))`、integerはToInt、配列はArray、throw相当はprovider依存の実行時ゼロ除算を使用します。expectedSerializedKeysはnative goldenのfirst passのserializedUtf8Hexを無変更で参照します。runtime-rejection/native-onlyは空配列です。製品E2Eはserialized-key-comparisonだけを期待key比較に使い、runtime-rejectionは拒否として検証してください。空source・cache/AST/context/lifetime・providerなし・取消相当・Timestamp/raw bytesはnative-onlyです。Timestamp/raw bytesを公開式で表現できないケースのexpressionは有効な `MatchInside(none,none)` placeholderで、同等性を表しません。その他のnative-onlyも式だけではadapterのmodeを再現しません。

公開planは静的です。製品build・E2E・GUIを実行しておらず、公開typed/cache/AST identity/例外classの同等性、parser・全optimizer・ApplyFilterExpressionsToLinesの実行を主張しません。sourceに含めた未使用のstatistics helperやcaller/optimizer spanは共通adapter支援・出典境界です。実取消token、同一compiled product planの複数Transform、実GUI、他OS/AOTは未検証です。

## 独立検証と再採取

PythonとMSVCは検証専用です。通常.NET buildへの依存を追加しません。再採取先はrepoとCodex user directory外のfresh directoryにしてください。

```powershell
& $python -B tests/Fixtures/LineTransformMatchInside/verify.py --verify --git
& $python -B tests/Fixtures/LineTransformMatchInside/reproduce.py `
  --repo $repository `
  --reference-zip tests/Fixtures/LineTransformReplace/reference-source.zip `
  --output "$env:TEMP/Codex/DiffBeacon/ltc1515/matchinside-fixture/fresh-reproduction" `
  --compiler 'C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe' `
  --vcvars 'C:/Program Files/Microsoft Visual Studio/18/Community/VC/Auxiliary/Build/vcvars64.bat'
& $python -B tests/Fixtures/LineTransformMatchInside/verify.py --actual $nativeStdout
```

`verify-public.py --actual <public-observations.json>` は公開scenarioの観測41件（比較35件・実行時拒否6件）をplanに照合します。planの5 fields、ID・式・期待key、baselineの終了コード・stdout・stderr、比較35件の最適化あり式とCLI JSONの `different=false` を検査し、typed/context/例外同等性のflagはfalseに固定します。公開keyは直接観測ではなく比較結果による間接照合です。reader自体は外部Tempのschema-shaped模擬観測で成功し、20件の改変を拒否しました。これを製品実測の成功としては扱いません。

`generate-public-plan.py --output <fresh external file>` はnative goldenを参照する静的planを再生成します。固定原本の再採取は `cases.json` とcontractの失敗条件を先に確認し、別compile／別childの全stdoutが一致してから更新します。Git stage/commit/pushは行っていません。
