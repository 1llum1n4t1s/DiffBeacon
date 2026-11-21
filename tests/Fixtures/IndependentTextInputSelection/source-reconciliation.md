# 原典と親の二確認文書との整合

読取り正本は `input247/parent-adopted-contract.md` と `input247/parent-source-check255.md`。独立検証をあたし自身が二回実施したという意味ではない。
APIはinput259/api-contract.md、input261/api-and-adoption-spec.md、input269適用後の実Srcを読取り照合した。入力ブラウザー利用者文言の日本語化input275を含む最新源SHAはexact-edit-proposal.jsonに保持する。

| 原典 / 契約 | 草案への対応 |
|---|---|
| MainWindow.cs AddSession/Attach/Select、Projects SessionPanes/SelectSession | 実入口はparent.IndependentTextInputsButton。ShowDialog<bool>から返る元Task<bool>のtrueと新tab選択を採用証拠にする。上限だけfixture helperで選択を変えず256tabにする |
| ComparisonPane.TextInputs.cs private TextAdoptionStamp、input261完全stamp | 検証草案は新要求stampを置換しない。旧CaptureIndependentTextStateだけではpair/saveGeneration/revisionが不足するため、別読取りpartialで全三側bytes/dirty/revision/pair/saveGeneration/diff本文/件数/選択座標をreceiptへ捕捉しbeforeafter独立照合 |
| ArchiveWorkingStore.Generation/Capture/Import no-op | 同store参照/世代保持を記録。初期working版や後発store変更は未実装としてcoverage N06/A11へ残す |
| ManagedArchive.ResolveManifest、ArchiveSource/ArchiveProjectInput.Validate | private readerを入口代替にせず、実Load/実ListBox row/実Open/Back/leafを操作。元ZIPはPython zipfileで全entry/CRC/SHAを検証。危険leaf/CRC/深度/暗号化境界は未実装 |
| input259browser/dlg内部Controlsとpicker注入 | Kind/RootPath/Pick/Load/OpenContainer/Back/Entries/AllowWorkingEdit/Pair/Compare/Abort/Cancelをそのまま使用。pickerはside/archive/絶対pathのみ、native pickerを触らない |
| input261 DialogShown/CandidateCreated/AdoptionGate | 既存hookにTask observerを最小追加提案。元button起動Taskの参照を受け渡すだけでengine/取消/完了条件を差替えない |
| input261自分自身のadopting Select guard | 15成功+double/retryの実opener bool true/新tab+1で反証する草案。ShowDialog true前の自己取消があれば失敗になる |
| 最後のawait後全state再検査/全rootSHA | gateで候補全bytesを捕捉してから同size/mtime root差替え。run inputsの初回read前/reset後に100ns UTC固定時刻を設定。case別before/after rawと2path/size/SHA/provenanceを保持し、readerが原fixture全byteと実stat exact ns一致を照合。Ticksは診断だけ。初read前差替え窓と後発本文/Hex/storeは未実装 |
| HeadlessSelfTest Pump / HeadlessIndependentArchiveTextChecks CaptureRenderedFrame | Task observerと既存Pumpを組合せる。IsBusy=falseなどのpollを完了判定に使わない。ForceRenderTimerTick/PNG/boundsは既存描画手順に従う |
| E2E Program.Run/RunWithInput/RecordCommand/MacCommandLauncher | 新scenarioから既存Runへ実アプリflagを渡す。macOS startup gate/固定SHA/-textを変更しない。reader processも実exit/両stream終端/timeoutを証跡化 |
| 固定B588/D99/旧限定schema | 新flag `--self-test-independent-text-inputs` と `--independent-text-inputs-only`、新fixture/reader/receiptで分離。既存範囲の結果で新入口合格を代用しない |

実trace schemaにはroute-kind/path/root/chain/leaf/rootSHA/readOnly/inheritedReadOnly/pair、3side text/bytes/SHA/dirty/editorReadOnly/revision、parent before/after/save世代/diff状態、store世代、candidateCount/tab数/active索引/accepted、picker注入/実click履歴、gate候補本文、tamper実mtime/size/SHA、既存outputbytes、PNG/bounds/clipが入る。password/credential/secretキーを独立readerが再帰拒否する。
保存/包装outputschemaは今回追加していない。coverage.mdに残契約を全て列挙した。
時刻修正は親のmac-time276-osx全12caseの実測報告を根拠に固定UTCを採用した。親のMac原典実測と本草案の未実行を区別する。親の18proposal/7Srcは未変更、固定B588/golden/fixtureは読み取りだけ。
