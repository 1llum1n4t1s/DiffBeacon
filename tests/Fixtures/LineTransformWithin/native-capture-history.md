# IsWithin 固定原典の外部候補

Canonical は未変更。正式配置候補は tests/Fixtures/LineTransformWithin。原典 commit 6780924ba35f7f55af0f6f14dd302561175830a6。唯一の原本ZIPは正式sibling ../LineTransformReplace/reference-source.zip、SHA256 18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7。追加Poco6原本は additional-original に固定Git blob bytesで同梱。10 source のsize/SHA256/Git blob SHA1と31抽出範囲、registrationと全optimizer byte/line/SHAを独立照合する。現在のlegacy Srcとは別bytesなので固定ZIPだけを正本とする。

固定ZIPの FilterExpressionNodes.cpp:2157-2234 実 IsWithinFunc を無改変抽出する。upper は <=。int64両edgeはexactで、double側のedgeだけcastする。文字列はPoco ASCII icompareを両edgeへ適用し、Timestampは実Poco比較定義を使う。評価順はlower/upper/value。direct array liftingは実定義、nested array はその実結果を記録する。

72 native typed AST primary は InRange の入力ASTだけを再利用。golden は IsWithin 実MSVCプロセスstdoutそのものを二回独立compile/runで取得し、一致を確認した。以前のgoldenの置換・数学・独自formatterでserialized keyを生成しない。ToStringValueは同じ評価resultへ実原典を適用する。Error UTF8・評価trace・実optimizer子訪問prefix traceを保持する。全optimizer bodyにはiswithin専用foldがないことを確認したが、wholeoptimizer/fullparserは実行していない。

public-case-plan.json は公開式をまだ実行していない候補。59 serialized-value同値候補、3 arity0/1/2 compile拒否候補、10 null-expression未表現を分ける。public-constructor-probes.json の別public-arity-4は第四引数をnoneへ変えた独立候補で、native ThrowNode AST同値ではない。12件のtyped int64→ToInt decimal bridgeは最終value/key同値候補だけで全parser/ToInt/native AST互換を主張しない。contextはliteral-no-getterの空EOF string[1] count1/index0を固定。rawthrow/rawbyte/typedTimestampは公開式null。Timestamp formattingの到達adapterは拒否し、nativeTimestamp文字列生成成功を主張しない。nullargsはUBを避け未実行。

```powershell
& <Python3.12以上> <fixture>/verify.py --reference-zip <repo>/tests/Fixtures/LineTransformReplace/reference-source.zip
& <Python3.12以上> <fixture>/reproduce.py --repo <repo> --reference-zip <repo>/tests/Fixtures/LineTransformReplace/reference-source.zip --output <fresh-external-task-Temp>/native --compiler <absolute-cl.exe> --vcvars <absolute-vcvars64.bat>
```

producer はportable project root/sibling ZIPのdefaultと明示fresh出力を使い、Gitobjectsや固定Cパスへ依存しない。履歴の実command/actor/compiler絶対pathはreceipt/provenanceで保持する。出力はrepo/fixture/.codex/CODEX_HOME内部、artifacts名、reparse/link祖先を拒否する。再採取は固定cases/plan/golden一致が必須。MSVC Windowsのみ実測済み、macOS/他compiler/Core/App/E2E/UI/AOTは未検証。

GPL-2.0はsiblingZIP original/LICENSE.md、Poco BSL-1.0は original/Externals/poco/LICENSE。追加原本copyright/SPDX header保持。native-proof.zip と全entry size/SHA manifestには両compile/runのinc/adapter/inputs/logs/receiptsを保持し、exe/objを含めない。固定familyは近い .gitattributes の * -text とfixed-manifestで保護する。初回compile/runも成功し、bad-first-runは発生していない。

外部KEEPは親の採否判断と独立再採取のため保持。削除条件は採否確定と同等証拠の正式保存後。外部Tempの展開runとbackupは共通ExactTrashで清掃し、拒否時はそのまま保持して報告する。
