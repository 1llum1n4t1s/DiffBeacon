# input306: 実暗号outerと別passwordのinner兄弟（実装前仕様）

製品build/runは担当禁止のため未実行。独立fixture検証をGUI合格と扱わない。

| ID | 起こり得る失敗 | 期待結果 |
|---|---|---|
| C01 | 実暗号outerをpasswordなし／誤値で受理 | Python zipfileと実Loadが拒否、正しいouterで全3entry一覧を確認 |
| C02 | left inner初回任意欄／retryを壊す | left Openでouter保持・inner空、missing／wrong拒否で前回outer一覧を保持、left正値retryでliteral一覧 |
| C03 | Backでcredentialを失う／撤去欄に残す | 実Backでouter保持、撤去されたleft inner TextBoxはclear、全outer一覧を再確認 |
| C04 | rightへleft passwordを持ち越す／cross passwordを受理 | right Openでinner空、missing・left値を明示入力したLoadを拒否、前回outer一覧保持、right正値retryでliteral一覧 |
| C05 | Compareが別route／旧stateを確定 | 実Compare→元Task終端、right typed route＋固定rootSHA＋独立literal本文、候補一件・新tab一件、旧parent全文・履歴・保存状態保持 |
| C06 | credential漏出／Closed時欄の寿命破損 | 閉鎖後に現欄・保持した撤去欄がclear、source-generated project JSONとreceiptに公開fixture password値やcredential keyなし |
| C07 | fixture自体が誤り／decoder由来期待 | CC0手動ZipCrypto encoderをmulti-entry化。Python標準zipfileで全層・全entryのSHA/CRC/暗号flag/literalを確認し、missing/wrong/cross値を各層で拒否 |

三つのpasswordは検証専用の公開literalで、user secret／AppData／clipboardは使わない。製品decoderを期待値生成に使わず、実buttonからobserverで通知された元TaskのみをPumpする。TCSによる合格代用やprivate API直接Submit/readは使わない。
