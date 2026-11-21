# Folder provider-null の限定契約

通常folder呼出しは `FilterEvalContext{this,&di}` により provider=null / sharedContext=null / lineIndex=-1 となる。本fixtureは15正規関数だけを対象にする。名前とmin/max arityはproof内 `functions.json` を正本とし、MatchDistanceBefore / MatchDistanceAfter を短縮しない。実際の行providerによる走査、LineAt/Column全側family、Content/LineCount/Sublinesは含まない。

`native-folder-null-proof.zip` SHA-256は `48fdbeda29a72c4e3515ef83951e2f3400495a64d05afd375c0ddcf4f72b111a`。96 entryの全bytes・size・CRC、15固定source blob・67無変更span、独立2compile／2child、159 caseを `verify-native.py`（採取担当readerを無変更保持）で検査する。原本はFunctionNode::Evaluateと15関数本体をadapter-bound closureで実行し、三点contextと全None・引数Evaluate未到達を観測した。full constructor／parser／子Optimizeは未実行。子Optimizeは原典FunctionNode::Optimizeで先に訪問されるため、評価guardがnative最適化時の副作用や例外まで抑えるとは主張しない。

`projection-map.json` は原本159件と同順序。114件はinteger/bool/string/none/empty-array/doubleの19有効arityを公開式へ表記し、`F(...) == None` によるfolder選択を比較する。nativeのadapter ExprNode型と公開literal ASTの完全同一性や内部typed Noneを実観測したとは扱わない。controlled throw ASTの45件は公開投影なし。`public-plan.json` の残318件は明示的にderivedと分類し、二者／三者で計864製品commandを予定する。

全864件のfilterは `<rule>: @noopt <expression>` とし、native function-bodyの実測フェーズに制約する。原典FilterExpression.h:95ではoptimize=true、FilterParser.y:47-51はroot Optimizeを実行し、FunctionNode::Optimize:4297-4307は関数本体に先行して全子をOptimizeする。したがって引数未評価／Noneの原本期待を既定最適化や`@opt`へ拡張しない。これらはnativeOptimizedPhaseQualified=false／nativeDefaultOptimizationQualified=falseで未資格。`@noopt`はnativeでOptimizeを抑止するdirectiveだが、現C# Parserはopt/optimize/noopt/nooptimizeを認識して捨てており、C#に対応する最適化切替を実装したという意味ではない。full native parser／constructorが未実行という限定も維持する。無指定の旧filterは独立actual readerがbytes一致で拒否する。

property名の収集／constructorとhostの準備検査は`@noopt`でも残る。Prop子のWindows成功／非Windows準備拒否という下記のOS別期待は変更しない。

derivedは全15関数のe/de/fe、None等値・不等値、大小比較のnot、Ifと単独predicate、case-insensitive関数名、内側LogInfo 0回／外側LogInfo 1回、Prop('System.Size')子、未登録list子、例外になるlist子を扱う。例外対照は `ReplaceWithList('é',file)`、file bytes=`09 58 0a`（空search/TAB/X/LF）。同じ入力のstandaloneはexit2とUTF-8診断を要求し、関数の子に入れたときは成功Noneを要求する。これは既存literal-list原典に基づく安全な派生入力であり、native TraceThrowの例外class/messageと同一ではない。Prop/listへのOS読取り回数は計測せず、内部I/O非発生を直接観測したとは扱わない。

Prop子はruntime引数評価と準備段階を分ける。原典FilterExpression.cpp:98-103 traverseExprTreeは全FunctionNode argsへ再帰し、:106-125 collectPropertyNamesは未評価子Propも収集する。DirDoc.cpp:312-322は全名でPropertySystemを初期化し、FunctionNodeProperty.cpp:95-108 SetPropFuncもconstructorでproperty keyを検証する。本proofのguard実測とは別のsource根拠を `property-preparation-source.json` に全file SHA・行span SHA／bytesとして保持しreaderで実repoと照合する。現CoreのparserによるpropertyNames登録と事前ValidateNamesはこの境界に対応する。Windowsは準備後のNoneと子runtime副作用なし、非Windowsは既存Windows専用property契約による準備拒否（exit2／stdout空／Windows診断／入力保持）を要求し、skipしない。新関数によってPropをportable化したとは主張しない。初期に全OS成功を想定した解釈はここへ訂正する。初期外部candidateのCore SHA `40662ec4cceb5d4e9c401d09b2bbab822e647f09162f9fe3baf02b34d4f03182` は由来として保持し、今回の統合版SHAは別の統合planで固定する。

構文拒否は全関数の無引数・過剰・comma欠落／末尾・閉じ括弧欠落・未知子・不正nested・bare名、誤短縮aliasを扱い、実終了2・stdout空・入力／filter／listと既存sentinelのSHA保持を要求する。folder CLIにoutput-file optionはなく、sentinelは公開先に渡さない対照である。実ファイル公開の失敗保護を検証したとは扱わない。lexer/depth予算と取消注入は未実施としてskipし、共有65,536評価予算は変更しない。期待値は失敗後に緩和しない。

`verify.py --repo-root <real repo>` は固定fileset/SHAと無変更native readerと静的対応表を検査する。`--actual <work>/public-observations.json` は864件すべての実引数／filter bytes／入力全SHA／stdout・stderr／二三者の全行と全側／実child終了・EOF・cleanupを別に検査する。静的合格は製品検証済みを意味しない。

helperは実OSを`--platform windows|macos`でreaderへ渡し、reader自身のhostと各row.platformの一致を要求する。WindowsはCommandResultの実Pid／creationUtc／launchUtc／exitObservedUtc／durationMs／終了コードと回収streamを検査し、LaunchEvidenceはnullのまま必須とする。UTC時刻の形式・launch<=exit・creation<=exit・creation>=launch-2秒・duration>=0・elapsedとの差2秒以内を要求し、欠落値・不正時刻・timeout終了-2を拒否する。2秒はOS時刻精度と計時経路の差の整合許容であり、終了待ちの代替ではない。Program.csのWindows経路がWaitForExitAsyncとstdout/stderr両taskをawaitしてから終了時刻を採取するsource spanをrunner-source.jsonへ固定し、改行をLFへ正規化した実repoの本文にちょうど1回あることを照合する。全file SHAと行番号は採取時点の記録として残し、runnerの他の変更で資格が失われないよう照合しない。これは同host・固定runner source・JSON／実streamの整合検査であり署名付き終了証明ではない。macOSは従来のLaunchEvidence／Terminal／raw exit／EOF／pipe解放／process dispose／cleanup空を引き続き必須とし、WindowsにmacOS用証拠を捏造しない。別rootのsynthetic reader mutant校正は製品実行と数えない。

main `FolderPropertyFilterScenarios.cs` は全体経路でsupplementary、provider-null、side-functionsの順に呼び出します。helperはSDKの既定Compile対象で、fixture全fileはE2EプロジェクトへContent/PreserveNewestとして登録します。list限定selectorはsupplementaryの呼出しを含み、provider-nullとside-functionsは含みません。helperはbin/tests fixtureを優先し、原典repoは実cwdの既存LineTransformAggregates/source-manifest.jsonとDiffBeacon.slnxで別途導出します。

固定source・spanの由来／SHA／Git blob／licenseはproofのprovenance.jsonと、SHA固定の既存LineTransformAggregates/source-manifest.json、LineTransformReplace/reference-source.zipへつながる。GPL原文をGPL-2.0.txt、Poco BSL原文をPoco-BSL-1.0.txtへbytesのまま収録した。probe・reader・公開planは検証adapterであり、製品へnative C++／compiler依存を追加しない。

Current canonical source-only refresh: Program CFD2; the two retained span bytes/SHA are unchanged (1794–1826 and 2614). Native proof ZIP and reader identity checks are unchanged. This metadata is not actual runner binary qualification; a fresh outer build/runtime chain is required. Since 2026-11-12 the reader no longer compares the whole-file SHA or line numbers; it requires each retained span (line endings normalized to LF) to occur exactly once in the live runner, so unrelated runner edits do not require re-pinning.
