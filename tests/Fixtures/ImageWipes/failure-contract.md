# 採取前の失敗契約

この契約は原本核のコンパイル・実行前に作成する。

| 起こり得る失敗 | 期待結果 |
| --- | --- |
| 原本header SHA・抽出行範囲の変化 | 採取を拒否し、固定goldenを更新しない |
| MSVC/SDKの不足、原本核の依存増加 | compiler logを保持して失敗。ダウンロードや原本改変をしない |
| 不正pane数・寸法・BGRA byte・入力不足 | adapterが拒否。2/3pane・正寸法・0〜255の入力だけを採取する |
| 初回INT_MAXが実scanLineへ流れる | probe失敗。原本が軸の端へ初期化することを観測する |
| 負座標・端を超える座標 | 原本が0/軸寸法へclampする |
| vertical/horizontalの軸取り違え | 非対称3×4 canvasで、verticalはy、horizontalはxの期待literalを照合する |
| 初回・同座標・前進・逆進・繰返しでpane順が壊れる | 全状態を独立した絶対領域pane順の期待literalと照合する |
| RGBだけ交換され、alpha/hiddenRGBが欠落 | 全4byte・alpha 0/128/255を照合する |
| 寸法差paddingで走査範囲が壊れる | adapterの透明共通canvasを全画素照合する |
| 原本入力が変更される | 原本SHAとfixture入力を保持する |
| 2回の再採取が不一致 | golden bytes比較を失敗とし、確定fixtureを更新しない |
| OS操作や前処理の実測へ拡大する | stage1核だけに限定。pointer/focus/capture/clipboard/FreeImage/overlayを呼ばない |

この採取は実アプリのE2E・旧GUI互換確認を代替しない。期待literalの確認と原本核の観測は別の成果物へ記録する。
