# IsWithin 固定原典と公開診断

正式配置は tests/Fixtures/LineTransformWithin。Native原本 cases/golden/public-case-plan/provenance/native-proof/receipt と追加6Poco原本、actor/古いcompile絶対path/公開未実行statusを採取履歴としてbytes不変で保持。native-capture-history.md と native-original-fixed-manifest.json は旧原本記録。golden SHA C7348FC20D2E5611451A96C2F76C884702917BBFBF15948C1BF80A8F7B892D5D、plan SHA3325A94AAE0CD10210BA30A7E422E59848A41078596757262B7F583E0D19A00D。

唯一の原本ZIPは正式sibling ../LineTransformReplace/reference-source.zip（SHA18ED25D74EBA5E151BF5FD1A2E811B2490AF2CE4C3E39B273A2D563ABE8DA4E7）。追加6rawとあわせGit objects無しで全10sourceのGitblobSHA1/size/SHA256・31原典範囲・registration・全optimizer範囲を独立検証する。GPL-2.0はZIP original/LICENSE.md、Poco BSL-1.0は original/Externals/poco/LICENSE。追加rawのcopyright/SPDXを維持する。MSVC二回の実原典採取は履歴として保持し、今回再compile/runしない。

captured-public-actual.ndjson / captured-public-probes.ndjson は採用前のPureCore候補の実stdoutを frozen-proof.zip SHA1B32E5F7C2DD4119D75DECAFEB69A1A772CB2D92D4550B3F2BE3A68D24B10670 から直接bytes引用したもの。投影/legacy/独自formatterを使わず、sourceLineCount/rowIndexも実processが直接出力した値。元entry・streamSHA・二回process/probe receiptsは public-evidence に保持する。Appは自己検証のline-transform-withinへ同じ契約の診断を直接出力し、別processのこのreaderへ渡す。採用前の保存stdoutをApp/E2E/UI/NativeAOT資格の証拠には使わない。

主72診断は59実C++ToStringValue serializedキー一致、3arity0/1/2 compile/ArgumentException、10expression=nullで未表現。別arity4 probe1は独立診断fileとしnative第四ThrowNode AST同値を主張しない。id/context/expression/classification/sourceCommit/sourceSha256/originalHex/keyHex/sourceLineCount/rowIndex/status/errorPhase/exceptionTypeに加えexecuted/publicExecution/compatibilityScopeを必須とする。contextは凍結EOF string[1] count1,index0 EXACT。未表現はexecuted=false/NONEXECUTED、59成功と3compile拒否はexecuted=true/executed。

12 ToInt decimal bridgeは厳密scope serialized-value/key-only; no native typed-AST/full-parser compatibility を要求する。typed Timestamp/rawbyte/rawthrowは未表現。全parser/wholeoptimizer/ToInt AST/Timestamp format互換は主張しない。privateproposal実績とNative履歴を混同しない。全family source/data/scripts/docsは近い .gitattributes の * -text とfixed-manifest size/SHAで固定し、余分/欠落/generated改変も拒否する。

```powershell
& <Python3.12以上> <fixture>/verify.py --actual <public-diagnostic.ndjson> --public-plan <fixture>/public-case-plan.json --probes <separate-probes.ndjson> --reference-zip <repo>/tests/Fixtures/LineTransformReplace/reference-source.zip
& <Python3.12以上> <fixture>/verify_immutable_source.py --reference-zip <repo>/tests/Fixtures/LineTransformReplace/reference-source.zip
& <Python3.12以上> <fixture>/reproduce.py --repo <repo> --reference-zip <repo>/tests/Fixtures/LineTransformReplace/reference-source.zip --output <fresh-external-task-Temp>/native-run --compiler <absolute-cl.exe> --vcvars <absolute-vcvars64.bat>
```

正式配置ではsibling ZIP defaultを使用できる。外部stageでは --reference-zip を明示する。capture/reproduceはportable projectRoot/siblingZIP/defaultと明示repo/outputを使い、固定C既定値・Gitobjects不要。fresh-only outputはrepo/fixture/.codex/resolvedCODEX_HOME内部、dotwork祖先、artifacts名、reparse/linkを拒否する。compiler/vcvarsは明示が必須。通常の検証は固定原本を読み、nativeを再採取しない。製品での実測範囲は[移植状況](../../../Docs/MIGRATION.md)と[E2Eの手順](../../DiffBeacon.E2E/README.md)を参照する。macOS/他compilerでの再採取は未検証。

再採取は新しい作業別の外部Tempへ出力し、固定原本を上書きしない。検証証拠と復元資料は必要最小限を外部へ保持し、用途を終えた一時物は共通ExactTrashで清掃する。正式fixtureの近い .gitattributes とfixed-manifestは全fileの原本bytesを保護する。
