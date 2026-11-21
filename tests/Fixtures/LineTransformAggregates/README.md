# LineTransformAggregates 固定原本と public bridge

正式位置は `tests/Fixtures/LineTransformAggregates` です。`golden.ndjson` は固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` の無改変 C++ 関数・serializer・context・provider・FilterErrorCode enumを実MSVCで二回compileし、別childで二回実行した141件のstdout原本です。SHA-256は `74e1f5b6bb58af0433cb0d198ee3360da1ebcd1f8845da9b5ce2fd0e2f549e91`、`native-proof.zip` は `039e9b4e77d8164b4e97bb726350675a4d8c6de2d73fe8daa1a3de03cdca7d4f`。数学による期待値生成、goldenの手編集、controlfieldsの正規化は行いません。

`verify.py` は元の独立Native source/data readerを無変更で保持します。固定fileset/SHA、15 Git blob／53原典span、原ZIP全entry/CRC、二回compile入力、二回stdout全bytes、PID／birth／保持OS handle／終了／両EOF／timeout、値・型・none・例外・条件先行trace・cache・provider変更・nested/error・実callerを照合します。原採取receiptの絶対パスは当時の証拠として改変しません。原典の共有ZIPは sibling `LineTransformReplace/reference-source.zip`（SHA `18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7`）を再利用し、複製しません。GPL-2.0原文は同ZIP `original/LICENSE.md`、Poco BSL-1.0原文は `original/Externals/poco/LICENSE`。追加11 raw sourceのcopyright／SPDX／Git SHA1／SHA256／size／commitを保持しています。

`public-case-plan.json` はNative141と1対1・同順序の静的planです。104件はpublic `Compile`→`Transform(source,Left,false)`でserialized key bytesを比較する予定、37件はexpression=null／未実行です。別 `probes.json` の8件は四関数の0／3argsに対するpublic引数数拒否契約で、Native141へ追加しません。generatorはpublic producerをcompileせず、製品を実行しません。planの `executed`／`producerCompiled`／`contextEquivalent` はfalse。static PASSは原bytes参照と派生producerの校正であり、Appの実行資格ではありません。

public providerはNative rowsの個数だけを合わせた空文字行、rootLineIndexは0です。`Choose(LineNumber-1,...)`、`ToInt`、有限double literal、bool／none／Array／strict UTF-8 quoted stringを使います。Native outerLineIndex=7、contextSentinel、controlled SequenceNode trace、mutable states、shared AST identity／cache、caller lifetimeはpublic expressionと同等ではありません。Timestamp、NUL raw bytes、zero-row／null provider、controlled state/cache、native exception classの37件を公開互換PASSへ数えません。範囲外double→int64、NaN／Infinity、null sharedContext、同一node循環／並列再入、macOS／他compiler、fullAST／全parserは元の `unsupported-boundaries.json` の境界です。

全false条件の通常value四件とthrow value四件はNative原本で条件だけが全行評価され、value trace空・count0／none・成功none cacheを観測しています。public throw sentinelはprovider依存runtime-only `1/(LineNumber-LineNumber)`へ代替します。定数`1/0`は全子Optimizeへ到達し得るため使いません。派生C#例外をNative exceptionType／std bytesと同等扱いせず、`typedSourceValuesReproduced=false`／`originalExceptionEquivalent=false`を保持します。

`verify-public.py` はApp actual `diagnostics.ndjson` 141行と `arity-probes.ndjson` 8行をplanへ厳密照合し、単一JSON summaryを出します。104件はCompile成功／Transform成功／全keyのstrict UTF-8 bytesとSHA／UTF16・UTF8長／出力数／source保持／matched=trueを要求します。37件は未実行／matched=false／空actual keys／予算0を検査し、互換成功へ数えません。未表現callerの原first stepではscalar `value`／`serializedUtf8Hex` が存在しない場合があり、参照用fieldはnullとして照合します。104 bridge内のserialized hexは引き続き必須です。未知・欠落・重複field、matched=false bridge、実行失敗を拒否します。`originalNativeKind`／`originalNumericBitsHex` は原first step参照だけ、`actualTypedValueObserved=false`／`actualNumericBitsHex=null`です。内部typed valueやnumeric bitsを実観測したとは主張しません。workUsedは公開予算domain、outputCharactersはCompile中のliteral生成、scalar／array変換、source保持、生成結果などに共有される累積公開BudgetCounterです。64 MiB domainと全actual keyのUTF16総長以上という下限を検査し、実出力総長との等値やNative trace／counter同等性は主張しません。engineering／UI診断はこのreaderの対象外です。138行など未完了actualは141行必須のため資格対象外です。

```powershell
python -B tests/Fixtures/LineTransformAggregates/verify.py --reference-zip tests/Fixtures/LineTransformReplace/reference-source.zip
python -B tests/Fixtures/LineTransformAggregates/verify-public-plan.py
python -B tests/Fixtures/LineTransformAggregates/verify-public.py --actual <output>/line-transform-aggregates/diagnostics.ndjson --public-plan tests/Fixtures/LineTransformAggregates/public-case-plan.json --probes <output>/line-transform-aggregates/arity-probes.ndjson
```

public summaryのguardは `passed=true`、`mainCases=141`、`bridgeCases=104`、`unrepresentedCases=37`、`separateProbes=8`、`nativeGoldenSha256`、actual NDJSON原bytesの `actualSha256`／`probesSha256`、plan原bytesの `publicPlanSha256`です。Native reader summaryは別です。public readerはNative readerを暗黙実行せず、複数JSONも出しません。

再生成は明示した新規外部Tempだけで行います。`capture.ps1` は元 `reproduce.py` の二回native compile/runと固定stdout全bytes照合後、正式版と同じ `generate-public-plan.py`→`verify-public-plan.py` を実行し、三つの生成JSONの固定SHAも比較します。入力／golden／native proof／manifestを上書きしません。compilerのTEMP／TMPは元producerがrun内へ指定し、通常.NET／CoreへpackageやDLL依存を追加しません。

```powershell
pwsh -NoProfile -File tests/Fixtures/LineTransformAggregates/capture.ps1 -Python <absolute-python.exe> -Repository <absolute-repo> -ReferenceZip <absolute-sibling-reference-source.zip> -Output <fresh-external-task-Temp>/aggregate-reproduction -Compiler <absolute-cl.exe> -VcVars <absolute-vcvars64.bat>
```

public planだけ再生成するときも新規の外部outputへ `generate-public-plan.py --fixture <fixture-base> --output <empty-external-directory>` を使い、`verify-public-plan.py --fixture <fixture-base> --plan-dir <generated-directory>` で検査します。`nativeFirstStepReference.file=golden.ndjson` と metadata.fixture=`.` は明示fixture BASE相対です。旧draft絶対パスを必要としません。historical parser evidenceの相対repo pathとSHA／lineは採取時点の証拠であり、現在のCore変更に追随して期待値を作り直しません。

原 `EvaluationFailed=11` を実caller失敗で観測し、`Regex=8` は原enumのsource証拠です。初期draftのhost誤定数（Regex1／EvaluationFailed2）は正式goldenへ流用していません。旧133件・修正後133件・初期125件の復元履歴と採取時の保護proofは親の外部KEEPに分離保持され、通常fixture経路には入りません。採取時scope文書／provenance／receiptは歴史的原本を無変更で保存しています。
