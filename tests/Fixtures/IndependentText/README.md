# 独立三者TextのE2E契約

この入力はテストrunごとに生成する固定literalで、旧WinMergeの原本を再生成したものではありません。既存のGNU／WordDiff原本と固定祖先の検証は全体E2Eに残します。

実装前に固定した失敗と期待結果:

| 刺激 | 期待結果 |
| --- | --- |
| 三つの文字コード／改行を含む物理入力、三選択pair | 指定pairの全文・役割・入力三側を保持、通常終了0／差分1 |
| 後段の不在物理入力、未知pair、非独立pair | 終了2、stdout空、全原本を保持 |
| v6 Physical／pristine Untitledの保存・再読込み | descriptor・中央の存在・選択pair・readonlyを保持、旧v1–5を変質させない |
| 旧versionへのdescriptor注入、未知／欠落／null／重複／payload矛盾 | 拒否、入力と既存出力sentinelを保持 |
| 独立HTML | 三側の原文を正しい役割で含め、選択pairを祖先mergeへ変換しない |
| pristine Untitled包装 | 空側のkindと存在を保持、ZIP全entry／CRC、展開・再読込みを確認 |
| GUI六方向コピー、中央編集・保存・Undo、readonly・取消・古い完了 | 指定先だけ変更、全側保存点・履歴・元入力と前の表示を保持 |
| dirty Untitledのworkspace／包装 | 本文を落として保存せず拒否、捕捉HTMLは現在本文を保持 |

`--independent-text-only` は実アプリのCLIとheadless GUIを別プロセスで呼び、入力・JSON・HTML・ZIP・PNG・終了コード・生stdout/stderr・`assertions.json`をrun内へ保持します。限定実行は全体E2E／全体UI／4RID Native AOTの代替ではありません。通常デスクトップpointerとネイティブ保存ダイアログはheadless注入検証へ含めません。

## 別Pythonプロセスの独立reader

`verify.py` はPython標準ライブラリだけを使います。製品assembly・比較結果・合格フラグから期待値を生成せず、このファイル内の固定literalから三側の原文とUTF-8／UTF-16 LEの保存bytes・BOM・改行を組み立てます。

```powershell
python tests/Fixtures/IndependentText/verify.py --run <E2E出力root> --receipt <新規receipt.json>
```

`--run` は `fixtures/*/independent-text` がちょうど一つあるE2E出力rootです。他scenarioのfixtureが併存する全体E2Eにも使えます。通常は外側の `assertions.json` と実CLI終了コードの記録も照合します。E2Eの `RunAsync` 内で最終集計の保存前に呼ぶ場合は、明示的に `--products-only` を追加します。この指定で省略するのは外側の集計JSONだけで、三組のCLI全文・全コマンドの生stdout/stderr・project・HTML・ZIP・GUI成果物は引き続き必須です。親driverはreaderの実終了コード0、stdout、stderrを回収して最終集計に接続します。

現在の通常scopeは38製品命令です。`commands.json` の全label・引数・期待終了値・実終了値・PID・creation／launch／exit UTC・全stdout／stderrを固定契約と生streamへ照合し、命令の欠落・重複・未知命令や不完全なprocess receiptを拒否します。`FixedAncestor` と中央 `Untitled` の矛盾はproject-copy・report-project・package-projectの三入口で終了2・stdout空・診断stderr・既存UTF-8 no-BOM `KEEP` 出力保持を照合します。同一UTF-8 no-BOM `制`×3×1024×1024文字を三側へ渡すJSON予算ケースは終了2・stdout空・`32 MiB` 診断・9 MiB原本の全bytes／SHA保持を照合します。両ケースの期待入力はreaderのliteralから構成します。

これに加え、`legacy-payload-proof.json` の対応済み通常compare／tableの二実プロセスを別scopeで必須照合します。UTF-8 no-BOMの固定原文 `tag --pair --independent-text\n` の入力を全bytesで確認し、`--substitute` の値に含まれるフラグ文字列が独立Textのoptionと誤認されないことを確認します。compareはdifferent=false・textSemanticsなし、tableはdifferent=false・rows=1・cols=1・alignedRows=1・alignmentFallback=false・alignmentWorkUsed=0・alignmentFallbackReason=null・mapping=[{left:1,right:1}]の固定JSON全体に照合します。両命令の実終了0・引数・PID／UTC・生stdout／stderr一致を確認し、未対応merge置換オプションの出力fileが存在しないことも確認します。38命令の `commands.json` へこの互換scopeを混ぜません。

現在のGUI scopeは `async-proof.json` の固定16名（FixedAncestor側0／1／2の3件、後発編集側1／2の2件、既存単発10件、コピー採用直前の実Cancelによる保持1件）を全passedで必須確認します。readerは各名をliteralで列挙し、未知名・欠落・重複を拒否します。三側のFixedAncestor Untitled拒否sentinelとraw v6拒否入力もliteralへ照合します。非同期／取消／古い完了の観測はGUI自身のproofであり、readerがOS pointerや製品内部状態を再実行した検証とは区別します。

旧 `limited88` の34命令scopeを検証する場合だけ `--legacy88` を追加してください。この指定では新四命令・legacy payload二命令・GUI async proofと当時未採取だった `commands.json` のprocess照合を行いません。receiptにも `legacy88=true` を記録します。新scopeで欠落した成果物を旧互換として自動許容することはありません。

全体headless UIの独立三者Text部分だけを別プロセスで確認する入口もあります。

```powershell
python tests/Fixtures/IndependentText/verify.py --gui <UI出力root> --receipt <新規GUIreceipt.json>
```

`--gui` のrootは `fixtures/*/independent-text`、二枚の `independent-text-*.png`、`ui-report.json` を含みます。全体UIの他stageのaggregate成否は三者Textのoracleにはしません。三者Text自身の保存bytes・観測JSON・bounds・PNGを直接照合し、対応する観測成否を別確認します。`--products-only` と `--gui` の併用は拒否します。

検証範囲:

- CLIの三組の全文・選択pair・役割・kind・path・encoding・空stdoutを伴う拒否と原本の全bytes／SHA、v6コピーの三側readonly・存在・path。
- HTMLの三側のescaped原文をHTML parserで復元。包装ZIPは固定entry集合・重複／異常entry拒否、全entryのstream読込み・サイズ・CRC32・SHA、両snapshotの全bytes、project・report・展開全file・reopen後の全設定を照合。
- GUIの六方向コピーと中央保存・後発編集・直接入力保存・readonly外部保存・Untitled外部保存・採用拒否後の公開bytes、全原本のbytes／BOM／改行、全側の観測本文・dirty・path・pair。
- 通常1280×850／最小850×550と17以上のタブで三editorのbounds、スクロール後の中央保存と三入力pathのtoolbar到達。PNG全chunkのCRC／length、zlib完全終端、全scanline filterの逆変換、JSONと画像の寸法一致、各editor矩形内に背景だけではない複数画素色があることを確認。

成功は終了0とstdoutの `passed=true` JSON、失敗は終了1とstderrの具体的な照合名です。引数エラーは終了2です。`--receipt` は省略可能ですが、指定時は新規fileに全照合名・ZIP全entryのsize／CRC／SHA・PNGの寸法／SHA・実タブ数を保存し、既存receiptの上書きを拒否します。通常デスクトップpointer、ネイティブ保存dialog、UI文字のpixel golden、別OS／RIDはこのreaderだけで検証済みにはしません。
