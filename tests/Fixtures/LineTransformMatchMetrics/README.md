# LineTransformMatchMetrics 固定原本と公開bridge

固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` の `MatchCount`・`MatchNumber`・`MatchDistance`・`MatchDistanceBefore`・`MatchDistanceAfter`、共通helper、serializer、providerを無変更で抽出し、MSVCで二回compile・別childで実行した160件です。`golden.ndjson` は実stdout原本で、SHA-256は `6caf46dfc9c137a5f390932754bd4cc6a9fb32e180a74a15e093bd0541e98da7`。期待値の算術生成やgoldenの手編集はしません。既存LineTransformMatchesの132件とは別のfixtureです。

原典はsibling `LineTransformReplace/reference-source.zip`（SHA-256 `18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7`）と `LineTransformAggregates/additional-original` を再利用します。`source-manifest.json` に15 raw blobのSHA・サイズ・Git blob ID、`provenance.json` に63 spanの無変更byte範囲・行番号・SHAを固定しています。原典関数は `Src/FilterEngine/FilterExpressionNodes.cpp` のMatchCount:3334、MatchNumber:3348、Distance共通:3366、Distance:3395、Before:3400、After:3405、登録:3813-3818です。共通GetOrCreateMatchRanges／ExtractRanges／GetDistancesToRangesと実ToStringValueを使います。GPL-2.0原文はZIP `original/LICENSE.md`、Poco BSL-1.0原文は `original/Externals/poco/LICENSE`。既存のZIP・fixtureは変更・複製しません。

`native-proof.zip` は二回のcompile入力、compile stdout/stderr、実child stdout/stderr、compile/process receiptを保持し、binary/objectは含みません。`proof-manifest.json` は136 entryのSHA・サイズ・CRC、`fixed-manifest.json` は自身を除く全fixture fileのSHA・サイズを固定します。readerは固定fileset、全raw blob、全抽出span、compile入力SHA、各processのPID・birth time・保持handleのsignaled状態・実終了・両EOF・timeoutと二回のstdout全bytesを検査します。receiptの絶対パスは採取時の証拠です。

各関数32件は、空・単一・無一致・全一致・先頭／末尾／中央・距離の同点・交互・複数block、none/integer/double/string/直接bool配列/入れ子配列/混在/Timestamp/不正UTF-8文字列のpredicate、走査の先頭／中央／末尾throw、別AST・match cacheクリア・cache済predicate変更・別関数による同AST再利用、入れ子MatchCount／Sum・内側成功後の外側throw、providerなし、負／末尾外／int最大のlineIndexを含みます。全行を二巡してtyped結果、serializer bytes、走査trace、cache個数、外側lineIndexを採取します。

nativeで分離2blockの一致行4件はMatchCount=4、MatchNumberは一致行だけ1,2,3,4でした。距離は一致行で0、該当方向の一致がない場合はnoneです。predicateはscalar boolと配列内の直接boolだけを判定し、numeric/stringはtruthyにせず、入れ子配列を再帰しません。throw時の外側cache未公開と、内側cacheのみ残る境界も採取します。

`public-case-plan.json` はnative goldenの同順序160件から95件の全行serialized-key比較、15件の公開runtime拒否、50件のnative専用probeを分離します。`generate-public-plan.py` はnative結果を参照して静的planを作るだけで期待値を計算せず、製品を実行しません。公開typed integerにはToInt、行別predicateにはChoose(LineNumber-1,...)、ThrowNodeにはprovider依存の実行時ゼロ除算を使います。基準比較は `@noopt` で定数最適化を除外します。限定E2Eは同じ95ケースで製品側だけ `@noopt` を外し、native由来の固定serialized keysを期待値にして最適化ありの結果も照合します。

公開CLIの比較JSON `different=false` は全行serialized keyの間接比較です。actual keyを直接取得したことやnative型・AST identity・cache・例外classの同等性は主張しません。空provider、Timestamp、raw不正UTF-8、providerなし、nativeのcache/AST/lifetime/範囲外index probeは公開実測へ数えません。native `new-context` は同じshared objectのmatch mapをclearするadapter probeで、新しい製品Transform呼出しではありません。native `shared-functions` は同じpredicate ASTに対する別関数のcache再利用を実行します。原典parser・whole optimizer・ApplyFilterExpressionsToLinesを実行したものではなく、provenance内の登録・caller・optimizer spanは出典境界の記録です。取消、実GUI/headless、同一compiled planの複数Transform寿命、他OS/AOTはこのfixtureの未検証範囲です。

限定E2E selectorは `--line-transform-match-metrics-only`。新規 `LineTransformMatchMetricScenarios` は未最適化と最適化ありの公開bridge、引数数拒否、pane分離、複数行の凍結、表経路、別AST、5関数を組み合わせた式、project JSON往復・HTMLのignored分類、共有生成予算と既存HTML保持を準備します。fixture作成時点では製品build／E2Eを実行していません。限定成功を全体E2E・UI・AOT・他OSの成功へ読み替えません。

## 再現

Pythonと既存MSVCは検証専用です。通常.NET製品buildへの依存を追加しません。外部Tempの新しい出力先を指定し、既存出力を上書きしません。

```powershell
& $python -B tests/Fixtures/LineTransformMatchMetrics/verify.py --verify --git
& $python -B tests/Fixtures/LineTransformMatchMetrics/reproduce.py `
  --repo $repository `
  --reference-zip tests/Fixtures/LineTransformReplace/reference-source.zip `
  --output "$env:TEMP/Codex/DiffBeacon/ltc1515/match-metrics-fixture/fresh-reproduction" `
  --compiler 'C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe' `
  --vcvars 'C:/Program Files/Microsoft Visual Studio/18/Community/VC/Auxiliary/Build/vcvars64.bat'
& $python -B tests/Fixtures/LineTransformMatchMetrics/verify.py --actual $nativeStdout
& $python -B tests/Fixtures/LineTransformMatchMetrics/verify-public.py --actual $publicObservations
```

`generate-cases.py --output <fresh external file>` は入力計画だけを再生成し、`generate-public-plan.py --output <fresh external file>` はgoldenに対する静的公開planだけを再生成します。再現用compiler出力・展開・ログは外部Tempへ置きます。固定原本を更新する手順では、必ず失敗条件を先に整理し、無変更原典の別compile／別child結果が一致してから正式fixtureを更新します。
