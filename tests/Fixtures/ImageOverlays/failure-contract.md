# 静的overlay採取前の失敗契約

原本compile実行前に定義する。実アプリ実装や旧GUIの検証ではない。

| 想定する失敗 | 期待 |
| --- | --- |
| header SHA/抜粋範囲の変化 | 採取拒否、原本を改変しない |
| MSVC依存不足 | compiler log保持。download/全header facadeへ拡大しない |
| 不正pane/dimension/offset/alpha/input | adapter拒否 |
| srcが加工済みdstになる | 常にpreprocessed原本を使う独立scalar期待と全BGRA一致 |
| 3pane中央dstの累積更新や中間丸めを欠く | 1→0,0→1,2→1,1→2の実行結果を閉じたscalar式で照合 |
| XORがalphaまで変える | BGRのみXOR、元alpha保持 |
| ALPHAでpremultiply/alpha0 hiddenRGBを消す | straight double、4channelそれぞれtruncで一致 |
| .3/.5境界で丸める、alpha0/1が不正 | truncと端値を全画素照合 |
| source offset支持矩形外を書き換える | 小寸法と正offsetを含むcanvas全BGRA照合 |
| padding未初期化 | adapter setSizeの透明ゼロcanvasを明記して全画素照合 |
| overlayにより分類や入力が変更される | raw前後/classification前後の原本観測bytes・SHA一致 |
| 強調とwipeの順序が逆 | overlay→MarkDiff→wipeの全画素期待と一致 |
| 強調alpha0の完全透明RGB置換を省く | 原本のtransparent color literalと一致 |
| animation/blink/OS操作の実測へ拡大 | ANIM対象外、blink=false、pointer/focus/capture/clipboardなし |
| FreeImage transparency cache stubが互換性を隠す | pixel非作用stubを明記。FreeImage cache未検証 |
| 2runs/gzip結果が異なる | 確定しない。rawとgzip両方で再現性を照合 |

stage1のみ。方向・ghost生成・復号・旧GUI・FreeImage・managed実アプリ・AOTは未採取。
