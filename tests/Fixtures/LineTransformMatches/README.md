# LineTransformMatches 固定原本と公開bridge

`golden.ndjson` は固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` の無変更C++関数・serializer・providerをMSVCで二回compileし、別childで二回実行した132件のstdout原本です。SHA-256は `9b4bb8a2c856658165618c5f3df0a537027956d5439686546b6b5e1a2c9fabf6`。期待結果を計算して生成したり、goldenを手編集したりしません。

原本はsibling `LineTransformReplace/reference-source.zip`（SHA-256 `18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7`）と `LineTransformAggregates/additional-original`、同 `source-manifest.json` を再利用します。原本ZIPと既存fixtureを変更・複製しません。GPL-2.0原文はZIP `original/LICENSE.md`、Poco BSL-1.0原文は `original/Externals/poco/LICENSE`。provenanceにsource SHA/Git blob、抽出byte/line範囲を記録しています。

`native-proof.zip` は二回のcompile入力・stdout・stderr・compile/process receiptを保存し、binary/objectは含みません。PID・birth time・保持Windows handleのsignaled状態・実終了・両EOF・timeoutを記録します。receipt内の絶対パスは採取当時の証拠です。readerは全ZIP entryのCRC、二回の無変更原本span、compiler入力SHA、stdout全bytes、固定filesetを検査します。native-cache/traceはこのadapterでの実測であり、公開製品の内部観測ではありません。

132件はboolと配列内の直接bool、none/number/stringと入れ子配列の非一致、空/no/all/交互/端/中央の範囲、5関数の全行と再呼出し、負/0/正のbefore-after組合せ36件、bounds型、before→afterの評価順、invalid bounds時のscan抑制、走査throw後の未公開cache、同AST/別AST/変更済predicate/新shared context、入れ子match/aggregate、内側成功後の外側throw、providerなしを含みます。`new-context` は同じshared objectのmatch mapだけをクリアして寿命を模擬するadapter probeで、新しい製品Transform呼出しやネイティブcaller全体を実行した証拠ではありません。`nested-aggregate` は統計値がscalar数になるためpredicateではfalseです。

`public-case-plan.json` は同順序132件の静的planです。91件は公開式と全行serialized keyの比較、11件は公開実行時の拒否、30件は未表現・未実行とします。`generate-public-plan.py` はnative結果を参照してplanを作るだけで、期待値の算術や製品実行をしません。公開typed integer入力にはToInt、行別predicateにはChoose(LineNumber-1,...)を使い、ThrowNodeはprovider依存の実行時ゼロ除算に代替します。`@noopt` で定数最適化の別段階を混入させません。公開CLIは全行の比較結果`different=false`を観測します。`verify-public.py` はこの間接観測を検査し、actual keyを直接取得した、native型/bits/AST identity/cache/例外classを実測した、とは主張しません。

限定実行flagは `--line-transform-match-only` です。

E2Eの独立readerは `verify.py`（native source/data）と `verify-public.py --actual <public-observations.json>`（公開bridge）です。E2Eは引数数、pane分離、複数行の凍結、別AST、mask境界、JSON project往復、HTMLのignored分類、共有生成予算拒否と既存HTML保持も別の公開契約probeとして実行します。CLIに取消注入はなく、取消・同一compiled planの複数Transform寿命・GUIの内部cache計測はUI/headlessの担当です。限定E2E成功を全体E2E・UI・AOT・他OSの成功へ読み替えません。

再現は新しい外部Temp出力を指定します。Pythonと既存MSVCは検証専用で、製品・通常.NET buildの依存へ追加しません。

```powershell
& $python -B tests/Fixtures/LineTransformMatches/verify.py
& $python -B tests/Fixtures/LineTransformMatches/reproduce.py `
  --repo $repository `
  --reference-zip tests/Fixtures/LineTransformReplace/reference-source.zip `
  --output $newExternalTemp `
  --compiler $cl --vcvars $vcvars64
& $python -B tests/Fixtures/LineTransformMatches/generate-public-plan.py --output $newExternalPlan
```

`capture.py --initial-observation` は初回採取だけの経路で、固定goldenがある通常再現では使用しません。`fixed-manifest.json` はfixture全filesetのSHA/sizeを固定します。`.gitattributes` の `* -text` を保ち、原本/採取proof/goldenの改行変換をしません。raw UTF-8/NaN/Infinity/範囲外double→int64/null sharedContext/同一node再入循環/並列走査/allocation failure/旧parserとwhole optimizer/macOS compilerはこのfixtureの未検証境界です。
