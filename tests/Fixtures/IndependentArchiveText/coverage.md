# 追加実装の検証範囲

失敗条件原本は `failure-spec.md`。これを変更せず、実装した検証と残りを区別する。

| 条件 | 追加した経路 |
| --- | --- |
| 3側実在leaf／中央非祖先 | Headless読み込み・編集・merge拒否 |
| 全6方向コピー／BOM／1252／混在改行 | 実pair/hunkボタン、全hunk適用、保存全byte＋literal独立reader |
| 三側working／中央保存／外部Physical | 中央実saveボタン、三側SaveWorkingTextAsync、中央SaveTextToAsync、root保護 |
| v6／3側相対asset／HTML／包装／展開 | CLI別プロセスと全ZIP/CRC、全asset、HTML snapshot、製品再読込み |
| clean/dirty中央兄弟 | 同route別tab、clean保存点更新／dirty保持／revision競合拒否 |
| Undo/Redo | 全OS headless実Controlキーイベント |
| true/null/省略readonly | 固定JSON原bytesを実DTO読込み、各3側editor／working-save／copy-to拒否 |
| 取消／保存後発編集／比較古い採用 | CancellationTokenと既存hookで本文／dirty／前diff保持 |
| 通常／最小／多数tab | 三者100DIP本文・中央save到達、PNG＋bounds独立reader |
| 全三側external SaveAs | 注入pickerの実SaveAs API、当該側だけPhysical、三側literal bytes、元root保護、v6復元／CLI包装・展開再読込み |
| 全三側root SHA差替え | 有効ZIPのcomment一byteを変更し同size／同mtimeを維持、比較／working／外部save拒否、旧本文／doc／dirty／revision／出力保持 |
| 全三側後続entry破損 | 既存CC0 late-bad-sibling固定原本、破損rootSHA一致、先頭leaf正常でも採用拒否、stdlibで先頭literalと後続CRC拒否 |
| 保存中tab移動 | working／external保存の実hook中に別tabへ移動、取消理由と三側本文／store／snapshot／既存出力保持 |
| 採用済み子の親close | 実一覧で子を開き、保存hook中に非active親close。中央保存成功と他側dirty／doc保持、working／externalの保存bytes |
| shared store／Text容量 | 128MiB／256文書の実inventoryと全SHA、64MiB+1文字の拒否、実Control Undo/Redoの範囲、三側state／保存版保持 |
| 全三側output guard | physical／root／workspace／filter／asset／readonly／linkとlate追加input／readonly／link、全30caseの理由・raw sentinel・三側state保持 |

上表のcritical195・boundary196を含むWindows managed検証は実測済み。Release `whole200/build-20261007T1534081404032` は実終了0・警告0／エラー0、限定 `limited208` はE2E87成功／0失敗・42命令、GUI163成功／0失敗、B588 stdlib reader2,295条件。全体E2E161,942成功／0失敗／5skip・7,112命令（実PID15808・実終了0）、全体headless UI11,532成功／0失敗・新Archive GUI163成功・217 root PNGを確認した。旧D99固定reader160条件、新B588関数のGUI専用helper4,146条件と全217PNG全画素を独立照合した。証拠は `artifacts/local/folder-threeway-next/text-next73` の `limited208-parent-accept209.json`、`accept215/acceptance-e2e200.json`・`facts.json`、`whole-ui-parent-accept217.json`、`accept212/archive-whole-gui-final-retry2-receipt.json`。

Textは文書64MiB、作業storeは共有128MiB／256文書を境界として拒否し、拒否前後の本文・保存版・stateと実Control Undo/Redoで履歴保持を確認した。Textに共有編集履歴の容量engineはないため、Binaryの64MiB／256操作の共有履歴上限をTextへ転用せず、その独立容量検証を実装済みとは扱わない。

readonlyのJSON nullと省略を分けたCLIcase、ancestor-not-mergeの親一覧での競合分類等は未実測。元の失敗仕様はfailure-spec.md、追加前の契約はcritical-failure-spec.mdに保持する。この追加機能の4RID Native AOT・CI／CodeQL、通常desktop、ネイティブ保存dialog、実OSのMeta／pointerは未実測。Physical／Untitled基準版cb666の4RID合格は旧scopeのみ。全体E2Eのsource2,050行／2,049固有pathとGUIのsource1,992行／runtime551件は別の記録であり、元記録にない古いauxiliary／decoder／compiler PIDは補完しない。
