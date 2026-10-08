# input266の実装範囲と残契約

**32件を実行できる草案であり、32件合格の記録ではない。build/実アプリ/PNG採取/E2E/AOTは担当指示で未実行。**
入力fixtureだけをPython独立readerで実測した。既存B588/D99の製品検証結果は、この新入口の証拠へ加算しない。

## 草案に含む最小set

| 条件 | 草案case / 成果物 |
|---|---|
| Physical / 全Untitled / 三Archive / 中央Archive mixed / 同root別leaf | 各LeftMiddle / MiddleRight / LeftRight、計15正常case。実入口/Pick/Load/一覧row/Open/Back/leaf/編集許可/pair/Compare |
| 採用自身のSelectによる自己取消 | 各正常caseの実opener Task<bool> true、candidateCount=1、tab+1、active新tab、旧parent/store/output保持 |
| 二重送信 | physical-0:double-submit。gate中の実Compare二回、元一回目Task終端、候補/tab一件 |
| 取消/Close/中止 | cancel-before、close-before、cancel-gate、close-gate、abort-gate。CloseはWindow.Closeによる閉鎖イベントの検証でOSタイトルバーpointer操作ではない |
| kind/root/readonly/pair変更、tab移動→戻り | candidate gate中に変更し、元Taskを遅れて完了させても不採用 |
| manifest gate / read retry | archives-0:manifest-cancel、manifest-root-change、manifest-retry。実Loadの元Taskを待ち、古いmanifestでの実Compare拒否と実Load再試行 |
| 最終SHA再照合 | physical-sha-change / archive-sha-change。読込み済みcandidateの全bytes捕捉後、run内copyを同size/mtime別SHAへ差替え。全run入力は初回read前/reset直後に100ns UTC固定時刻、case別before.raw/after.rawは保持、独立readerが全bytes/固定SHA/実stat ns一致を照合。不採用・旧parent保持・最後に共有入力だけ原fixtureへ復元 |
| 256tab上限 | 255tabからmodalを開き、headless fixture setupで選択を動かさず256へ到達、実Compareで拒否。初回entry時点で既に256の経路は別未実装 |
| dialog通常/最小 | 1000x680 / 850x550、各sideの一覧100DIP+footer四操作、入力/Load/Open/Back/編集許可をscroll後にPNG/bounds capture |
| 多数tabと成功tab | 20tabの親/採用tab、通常/最小、三editor・全「保存」label buttonをscroll後capture。件数証跡は採用直後、20tabはlayout専用setupで増やす |
| 原本/bytes/metadata/出力 | 全3side原文bytes/文字コードBOM/混在改行、ZIP全entryCRC/SHA、typed route、readonly、pair、parent beforeafter、store世代、既存output literal |

草案実装32件 = 正常15 + Physical派生13 + Archive派生4。各caseの元実opener / Compare / browser taskをhookで取得し、既存Pumpに渡す。
OperationTaskObserved / SubmissionTaskObserved / IndependentTextInputOperationObserved は元Taskをそのまま通知する。Task.FromResultは明示許可されたpicker注入だけ。TaskCompletionSourceはgateの到達通知/解放だけで、操作合格や完了の代用にしない。
固定mtimeは `2024-01-01T00:00:00.1234567Z` / `1704067200123456700 ns`。Ticksは診断欄だけで独立証拠に使用しない。Mac原典について親からmac-time276-osxの全12caseで実測成功と通知されたが、この新草案のMac製品実行は未実行。

## 親が追加する必須未実装・未検証

| failure-spec ID | 残契約 / なぜ最小setで代替できないか |
|---|---|
| N06 | 初期working版・同store・初期metadata復元、保存点、外来snapshot拒否、同SHAのImport no-op。32caseは原本だけ |
| N08 | 新入口採用tabからの全3pair/全6copy・全側working/external保存、workspace複製、包装展開/再読込み、literalと元fixtureの照合。固定B588経路を別runで再使用する接続も未実装。新readerは保存/包装の証拠を受けない |
| A02/A08 | parent pane終了/window終了の候補解放、Closed→callback finally/credential clearとsubscription解除の実測。最小setはdialog終了のみ |
| A03/A04/A06 | 旧確定manifest表示と新requestedを混在させない、manifest成功後の後発read/候補のread retry、取消直後に次要求を作る競合、別tab/別sideの同時gate。最小setは初manifestのgateと一candidateだけ |
| A09 | gate中の後発本文/dirty/diff/selectedDiff/pair/readonly/保存世代変更による完全stamp拒否。通常ケースでparent状態を保持することとは別条件 |
| A10 | 未適用Hex（通知前を含む）/Binary revision、Archive/Image/Folder/Tableへの形式変更と採用拒否後の操作復帰 |
| A11 | gate中の共有store保存/revision更新・兄弟競合。最小setはstoreが変わらないことだけ |
| A12/A13 | 初read前の同size/mtime root差替え、browser初期SHA→最初candidate read間の差替え、Physical decoded bytes SHAとdiskの別内容を読込み窓で作るケース。最小setは読込み済みcandidate→最後再Hash窓だけ |
| A14 | 実入口時点で既に256tabのmessage/拒否、cap解除後の実入口再試行。最小setは選択非変更のfixture setupによる採用直前capだけ |
| R01 | Archive ReadOnly true/null/省略の全三側編集拒否、InheritedReadOnly null/省略、Physical ReadOnly=trueで成功採用後の拒否。最小setのreadonly-changeはcandidate無効化だけ |
| R02 | 危険leaf/dir/不在、壊れCRC（後続entry含む）、chain最大8/超過、limit/容量/リンク/全tab/filter/workspace/asset出力保護、旧確定一覧保持。alternate ZIPはSHA拒否用で、正常rootCRC拒否の代用ではない |
| R03 | password再入力/兄弟階層/共通outerだけ保持/root変更clear/4096文字上限/Dispose後配列clear、project/receipt流出検査。新schemaはpassword項目禁止だが暗号化fixtureなし |
| G01/G02 | 実PNG/boundsの採取と確認、footer到達、viewport100DIP、全save達、native picker/OS pointer/titlebar/保存dialog。新readerPNG独立CRC/scanline復号は図形の意味の目視検証を代替しない |
| E01/E02/E03 | 親がSrcへ適用してRelease build、限定32case＋reader、新flag不正引数/省略拒否、全体E2E/UI、4RID AOT、macOS実起動/出力回収、clean runner。今回のPython静的成功から製品合格を推論しない |

32caseのreceiptはschema `input-selection-observations-v1`、status `completed-minimum-set`。reader成功も `completed-minimum-set-only` とし、上表を未解決のまま残す。
critical全契約の完成前に新入口の全機能合格・出荷可と報告しない。
