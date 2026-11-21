# input301: 実Archive browserの失敗仕様（実装前）

製品build/runは親のsource freeze後に実行する。これは合格記録ではない。

| ID | 起こり得る失敗 | 期待結果と独立証拠 |
|---|---|---|
| P01 | 初回password任意欄がない／未入力・誤passwordを受理する | root load失敗、同じ欄の訂正後に全entry確認、二階層leaf literalで採用 |
| P02 | inner誤password／retryでouterを失う | 同一routeで実Load retry、outer保持、inner訂正後literal採用 |
| P03 | Back／兄弟変更でinnerを持ち越す | 検証済み共通outerだけ保持、撤去した欄の値はclear、兄弟innerは空 |
| P04 | root変更時credential持越し | 全現欄・撤去欄clear、新requestedと旧一覧を混在してCompareできない |
| P05 | 4096上限／Close後の漏出 | 4096のMaxLength、4097設定拒否または切詰め、元Compare／Close task終端後に全保持欄clear。JSON/projectに秘密値やcredential項目なし |
| R01 | 後続entry CRC破損を見逃す | 固定late-bad-siblingを標準zipfileも拒否、旧確定一覧保持・Compare拒否 |
| R02 | 危険entryを普通leafとして出す | 最小 ../leaf.txt ZIPの一覧拒否・旧一覧保持 |
| R03 | directory／不在選択をleafにする | 実ListBox directory／選択解除と実Compareから候補ゼロ |
| R04 | chain8／9境界 | depth8 leaf採用、depth9の第9container Open拒否、routeと一覧不変 |
| R05 | linkをrootに使う | run内file symlinkからLoad拒否。OS作成不能は未検証として報告し成功にしない |
| R06 | root作業／入力予算を見逃す | 製品既定値を変えず、browserの検証専用limits注入で最小正常rootを境界+1としてLoad拒否。default1GiB超fileの作成はしない |
| A01 | 取消されたold manifestが新rootへ採用される | gate後root変更→元read task終端、旧確定一覧保持、Compare候補ゼロ |

全caseはMainWindowの実「三側の入力を選択」buttonから開始し、実Load/Open/Back/Compareと元Task observerを使用する。gateのTCSは到達と解放だけ。期待literalと全root/entry SHA・ZIP CRCはPython標準libraryで確認し、製品decoderを期待値生成に使わない。PNGはmasked欄だけ、JSONには認証値・欄Textを保存しない。

現APIではR06用のlimits注入がないためexact-old-new proposalを親へ返す。未適用ならR06は実行不能であり合格扱いしない。実OSpointer／native picker／保存dialogは対象外。
