# LineTransformRanges 固定 fixture

正式familyは tests/Fixtures/LineTransformRanges。固定原本・公開plan・診断契約を製品の限定／全体検証から使用する。唯一の原典 archive は正式 sibling ../LineTransformReplace/reference-source.zip（SHA256 18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7）。追加6原本を additional-original に同梱し、Git object や旧開発PCへ依存せず10原本のGit blob SHA-1・SHA256・size、31抽出範囲と全optimizer範囲を確認する。全ファイルは近い .gitattributes の * -text で保護する。

cases.json / golden.ndjson / public-case-plan.json / provenance.json は採取済み原本 bytes のまま。golden SHA256 は 43c68c5c42f7bcce3fa508727759d5f43e7e938fe722a4108ff3151e9a3055d6、public plan は f201d12937d34271ec05e851830c641110b941ae469542ec442a0ad6a8083f1d。actor metadata、過去の絶対Cパス・command history・compiler provenance は変更せず履歴として保持する。新 producer は portable project root と sibling ZIP、明示された出力を使う。

主診断72IDは59 native ToStringValue serialized-key 一致、3 public constructor arity compile/ArgumentException 拒否、10 expression=null unsupported/unrepresentable/UnrepresentableAdapterType。別 public-arity-4 probe は独立ファイルで管理し、native ThrowNode AST 一致を主張しない。id/context/expression/classification/sourceCommit/sourceSha256/originalHex/keyHex/sourceLineCount/rowIndex/status/errorPhase/exceptionType は必須。context/count/index は凍結plan EXACT、空EOFの source は string[1]（count=1,index=0）。12件 ToInt decimal bridge は最終value/key一致だけで、typed AST・全parser・ToIntのnative互換ではない。

contract-actual.ndjson / contract-probes.ndjson は frozen core-preview ZIP の実stdoutを元に context 内 count/index をトップレベルへ投影した契約入力。raw stdout は captured-legacy-* に保持し、contract-derivation.json に元ZIP/entry/SHAと投影を記録した。数学や独自formatterで期待キーを生成しない。fixed-manifest.json は family 全ファイル（自身を除く）をsize/SHAで固定し、欠落/追加/生成物改変も拒否する。

```powershell
& <Python3> tests/Fixtures/LineTransformRanges/verify.py --actual <diagnostic.ndjson> --public-plan tests/Fixtures/LineTransformRanges/public-case-plan.json --probes <separate-probes.ndjson>
# familyを外部へコピーした再採取では --repo <repo> と --reference-zip <repo>/tests/Fixtures/LineTransformReplace/reference-source.zip を明示
# 旧実stdoutを読む場合だけ --legacy-proof を追加（上の3入力は必須）
& <Python3> tests/Fixtures/LineTransformRanges/reproduce.py --output <fresh-external-task-Temp>/native-run --compiler <absolute-cl.exe> --vcvars <absolute-vcvars64.bat>
```

引数なしverifyは固定実stdout投影のpositive契約と原本を検証する。capture/reproduce は書込み先fresh-only、repo/fixture/.codex/CODEX_HOME内部、artifactsという名前とlink/reparse祖先を拒否する。追加パッケージ・Git取得は不要。再採取は固定golden/public planへ一致必須。captureは原本から抽出するが全parser/wholeoptimizerを実行しない。MSVCは二回独立compile/processの保存証拠がある。今回はcompiler/processを再実行していない。macOS/他compiler/nativeAOT/App/UI/E2Eは未検証。Timestamp value/copy/dtor/比較は実定義、Timestamp文字列formatへ到達するadapterは拒否し成功を主張しない。optimizerは実子訪問prefixのみ。

GPL-2.0条件は sibling ZIP original/LICENSE.md、Poco BSL-1.0は original/Externals/poco/LICENSE。追加原本のcopyright/SPDX headerを保持する。native-proof.zip/manifest は抽出inc/adapter/compile入力/logと初回失敗ログを保持。固定データの変更は新採取・証拠・manifest再検討が必要であり、readerで不一致を隠さない。

一時再採取出力は外部の作業別Tempへ保存する。必要な最小証拠と復元資料を保持し、用途を終えた一時物は共通ExactTrashで清掃する。外部コピーで再採取する際は --repo と --reference-zip を明示して実repoの出力拒否境界を保つ。
