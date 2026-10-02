# 挿入削除整列後の画像強調の原本観測

WinIMerge v1.0.54、revision `da639cdfaeca87aaad0eaceec509afa11ad61421` の固定配布 DLL を公開 API だけで呼び出した golden。既存の58件の整列・編集 fixture は変更しない。製品出力から期待値を生成しない。

垂直／水平の先頭・中央・末尾挿入、逆方向の削除、透明な実画素、閾値2、readonly、offset (1,1)、三者2領域を12件146状態で記録する。各件で強調 alpha 0 / .3 / .7 / 1 と未選択／先頭選択／末尾選択／選択解除を採取した。原本子プロセスを二回独立実行し、入力・操作・stdout・stderr・強調PNG・強調前整列BGRA・最終保存PNGの720ファイルが全bytes一致した。各回670検証が成功した。

失敗先行の対象は、ghostと透明実画素の混同、削除色を適用する整列intervalの欠落、alpha0のhidden RGB消失、selected/normal混同、offsetと二方向座標、三者のpane別強調、強調画素の原画保存への混入。通常ghostは RGB(192,192,192)、選択ghostは RGB(240,192,192) が観測された。透明画素のalphaは `byte(255 * highlightAlpha)` で、alpha0でもRGBは保持される。

| 固定物 | SHA-256 |
| --- | --- |
| gzip 43,427 bytes | `4636258D1C81E7D45CDBC2343334CC36FC7215E63490132B775E4338340D2C81` |
| 展開JSON 479,795 bytes | `FA89130794BD54918CF95B8D701729B61840B0105263C8D05C48710749F37B01` |
| 二回一致の観測JSON | `AC8E457D3D718B63663CCA99E70E619F7F4446C783C9DD880B3884F978833674` |
| 配布DLL | `36F2A726C34A2323D2E569903B4F1DC6C28ABF1C10447006335F2785FC511BA6` |
| 公開header | `597902D5FFBE8585524C86C41E73032C607E42E04E65F90F0441E521F84DB4F4` |

## Schema と実測の境界

root の `cases` は既存 ImageInsertions の形式を継承する。入力 `inputs` と最終原画 `exports` は寸法・全 `bgraHex`・元PNGの `pngBase64` / SHA を持つ。`states` は `mode` (0:none、1:vertical、2:horizontal)、`currentDiffIndex` (-1:未選択)、`highlightAlpha`、領域／競合数、paneごとの原画寸法・canvas寸法・offset・外周1画素を含む全座標mappingを持つ。

state pane の `bgraHex` は原本 `SaveDiffImageAs` の全canvas強調PNGを独立PNG decoderで復号した値。`alignedRawBgraHex` は同じcanvas座標の強調前 `GetPixelColor` 値。後者は原本 `ImgDiffBuffer.hpp:563-566` にある **offset付きの m_imgPreprocessed** の画素であり、原画そのものではない。原画保持は最終 `SaveImageAs` PNGを入力全BGRAと照合した。原本private領域grid・行対応intervalを公開APIで取得したとは扱わない。

`actions` は `[kind,src,dst,index]`。`mode` はindexのmode、`alpha` はindex / 100.0、`select` はindex (-2:末尾、-1:選択解除)、`offset` はpane=dstへ (src,index) 加算。通常case state1が整列直後、state2～5が未選択alpha0/.3/.7/1、state6が先頭選択alpha1、state7/8が先頭選択alpha.3/.7、state9/10が末尾選択alpha.7/1、state11が解除alpha1。`insert-end` はstate2にoffset操作が入るので以降の番号が1増える。state0はNONEの原本観測である。

## 原本・ライセンス・再生成

原本根拠は [ImgDiffBuffer.hpp](../ImageRegions/reference-source/ImgDiffBuffer.hpp) の507～510 (色)、563～566 (強調前画素)、1888～1964 (透明画素の色選択とMarkDiff)。公式配布物・固定公開header・GPL／FreeImageの由来は [DLL採取手順](../../../build/WinIMergeDllReference/README.md) を参照する。観測・採取コードは [GPL-2.0-or-later](LICENSE.txt)。入力はこの採取用に自作した極小PNGで、CC0-1.0として提供する。

Windows x64、MSVC14.51.36231、Windows SDK10.0.28000.0、Python標準ライブラリだけを使用する。`capture.py` と `state-function.cpp.txt` を `E:/DiffBeacon-artifacts/reference/image-insertion-highlight-v1.0.54/` に配置する。既存の固定 `build/WinIMergeDllReference/generate-reference.py` と公開header、固定配布DLL、`E:/DiffBeacon-artifacts/reference/image-transforms-v1.0.54/transform-probe.cpp` を再利用する。採取の二回は空のrun4/run5が必要で、既存runは上書きしない。

```powershell
python -B E:/DiffBeacon-artifacts/reference/image-insertion-highlight-v1.0.54/capture.py run4
python -B E:/DiffBeacon-artifacts/reference/image-insertion-highlight-v1.0.54/capture.py run5
python -B tests/Fixtures/ImageInsertionHighlight/extract.py E:/DiffBeacon-artifacts/reference/image-insertion-highlight-v1.0.54/run4 E:/DiffBeacon-artifacts/reference/image-insertion-highlight-v1.0.54/run5
```

gzipは時刻・ファイル名を固定し、展開SHAも照合する。最新原本run5だけを展開保持し、run1～4は各entryのサイズ・SHA照合済みZIPに保持する。run1～3は旧検証コードが `GetPixelColor` を原画と誤認した失敗記録であり、golden採用元ではない。採取用DLL・C++・MSVCを製品ビルドの依存へ追加しない。GUI/CLI/HTMLの互換性は製品側E2E・自己検証の結果で別に判断する。
