# input266: 実Button検証の失敗条件（コード作成前）

本書は検証草案の契約。製品実行・buildは親担当であり、以下を成功済みとは扱わない。
対象は通常GUIの「三側の入力を選択」→modal→実Button→新tab採用。private reader直接呼出し・SubmitAsync直接呼出しを入口の代用にしない。
原典はinput247/parent-adopted-contract.md、parent-source-check255.md、input259/api-contract.md、input261/api-and-adoption-spec.mdと当該草案。固定B588/D99のreader・golden・42pinsは変更しない。

| ID | 起こり得る失敗 | 必須期待結果 / 証拠 |
|---|---|---|
| N01 | 通常Physical三側のpath/kind/readonly/pair混同 | 実Pickへ注入した絶対path、全原文bytes/文字コード/BOM/改行、三側metadata、各3pairが一致し新tabを一件採用 |
| N02 | 全Untitledを空Physicalと混同 | path空・Untitled三側・空本文、pair、readonly、独立mode、新tab一件 |
| N03 | 三Archiveのchain/leaf/passwordを混同 | 実一覧行→OpenContainer→Back→再Open→実leaf選択、空通常path・各rootSHA/chain/leaf、原本readonly、編集許可一致 |
| N04 | 中央Archive mixedが旧ancestorになる | left/right Physical、中央typed Archive、全原文一致、Independent pair保持 |
| N05 | 同root別leafを一側へ束ねる | 同root/SHAでも独立した3leaf/chain本文・readonly・保存版identity |
| N06 | 初期working版を別store/外来snapshotから採用 | 同window.ArchiveTextsのCaptureだけを用い、初期編集版本文・保存点・store reference/generation、全sourcebytes保持 |
| N07 | 採用自身のSelectで自己取消 | ShowDialog<bool> true・最終新tab選択・新tab一件・parent本文/dirty/diff/stamp/store/既存出力保持 |
| N08 | 三pair/六copy/全側save/packageが変わる | 固定B588経路を変更せず別runで再使用、独立literal/元fixture全byte照合。新入口成功と既存B588成功を混同しない |
| A01 | 二重送信で二候補/二tab | 同一pendingへの連続実Compare clickで候補一件・tab一件・true一件 |
| A02 | Cancel/Close/Abort後に採用 | pending browser/candidateを解放してfalseまたは再試行可、旧parent/store/output保持、後発完了も採用なし |
| A03 | kind/root/readonly/pair変更後に旧結果採用 | generation/token無効化、古いtask終端後も新tabなし、変更後の再試行は新routeだけ |
| A04 | manifest gate中の取消/古い結果で旧一覧を確定 | 実Loadをgateで止め変更、成功済み一覧は表示保持してもCapture禁止。gate解放後旧結果採用なし |
| A05 | 一覧retryが旧失敗/旧credentialを再利用 | 実Load retryで全container再検証、成功後だけ確定可能 |
| A06 | candidate gate中の取消/古いreadが採用 | 旧parent/store/outputを保持、candidate Dispose/seed clear、retryで一件だけ採用 |
| A07 | tab移動→戻りで同じactive referenceに戻り採用 | 一度のtab移動で寿命無効化、戻っても採用なし |
| A08 | parent終了/window close後に採用 | modal false・候補解放・tab増加なし、window終了時store clearと通常失敗時保持は区別 |
| A09 | 後発本文/dirty/diff/pair/readonly変更を取り落とす | 完全stampで拒否、変更したparentの現在値を巻戻さず保持 |
| A10 | Binary未適用Hex/通知前Hexを取り落とす | format変更中のHex本文・未適用値・revisionを保持し新tab拒否 |
| A11 | 共有store保存/revision更新を取り落とす | store reference/generationで候補拒否、保存した新版を巻戻さない |
| A12 | 同size/mtime rootSHA差替えをcacheで採用 | 初read前・Attach直前の両窓で全SHA拒否、原本/出力/parent保持。差替え対象はrun内copyのみ。初回read前とreset直後に100ns UTC固定時刻を設定、case別before/after rawを保持し実stat ns一致を独立照合 |
| A13 | Physical読込みbytesと最終diskSHAが違う | decoded bytesのSHAと最後再Hashを照合、同size/mtime差替え拒否 |
| A14 | 256tab上限の取り落とし | 256既存tabのままCompare拒否・parent/state/store/output保持 |
| R01 | Archive ReadOnly=true/null/省略で編集可 | 原本ReadOnly=true固定、InheritedReadOnly true/null/省略のいずれも編集拒否。明示falseだけ許可 |
| R02 | 危険leaf/dir/不在/壊れCRC/chain上限を採用 | 実一覧/実Load/実Compareで拒否、全entry後続破損も拒否、旧表示と入力出力保持 |
| R03 | passwordをproject/receiptへ保存 | schemaにpassword欄なし、入力fieldは揮発、root変更でclear、chain共通outerのみ保持・兄弟inner破棄 |
| G01 | dialog footer/listが通常/最小で隠れる | 1000x680/850x550で各side一覧100DIP以上、Pick/Load/Open/Back/Pair/Compare/Abort/Cancel到達、PNGとbounds JSON独立照合 |
| G02 | 多数tab/成功新tabのeditor/saveに到達不可 | parent多数tabと採用tabの三側editor・全save操作にスクロールで到達、PNG/bounds JSON |
| E01 | 観測pollが開始前状態を完了と誤判定 | 実ButtonhandlerからTask observerを受け、既存headless PumpでそのTask終端を待つ。timeout/例外/欠けた最終集計は失敗 |
| E02 | readerが製品自己申告/生成outputを期待値化 | stdlib独立readerが固定synthetic fixture literal、ZIP全entryCRC/SHA、route metadata、beforeafter、出力bytesを照合 |
| E03 | 他run/AppData/clipboard/native pickerを変更 | クリーンなrun内outputのみ、picker絶対path注入、clipboard/AppData操作なし。native操作は未検証 |

実装setと欠落はcoverage.mdへ記録する。失敗caseのbeforeは必ずgate後のユーザー編集より前/後を区別する。
mtime固定値は `2024-01-01T00:00:00.1234567Z` / `1704067200123456700 ns`。DateTime Ticksだけの一致、Ticksをnsへ逆算した値、次caseで復元される共有pathをafter snapshotとして使うことは独立mtime証拠にしない。
全unit新設禁止、製品/build実行禁止、Src/tests/docs/Git変更禁止。追加所有はinput266内のみ。
