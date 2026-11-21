# WinIMerge 静的overlay原本 fixture（stage1）

WinIMerge `v1.0.54`、commit `da639cdfaeca87aaad0eaceec509afa11ad61421` の `RefreshImages` と静的 NONE/XOR/ALPHABLEND の原本核を bytes 無改変抽出して実行した49件。最終出力は全 pane BGRA 3,132 bytes。旧GUI・製品App機能の検証ではない。

| 保存物・原本 | SHA-256 |
| --- | --- |
| `winimerge-image-overlay-golden.json.gz`（17,055 bytes） | `E100EA23C79FABAD15D4EA930C4BDF82A4231CF01FD407A4248F2225355855F5` |
| gzip展開後JSON payload | `35E67215D97335233AE455B715520577A305DCC8DEF3CAAADF8D3486FD47D078` |
| canonical `../ImageRegions/reference-source/ImgDiffBuffer.hpp` | `7071EDFA31CCC138685CA7B9FD3D41EE5E84B942FB3D8028C7E3A31C76F93F28` |
| canonical `../ImageRegions/reference-source/image.hpp` | `173CE00FE65187D34A6C7D2B10C0B45443EA6CFFBAA50FE1263BCE5840149609` |
| `probe-adapter.cpp` | `F7773F700DF180DF90336873046A6378217C48D7C08C33AAF0124F22E1B87D50` |

ライセンスは GPL v2以降。出典は [ImageRegions README](../ImageRegions/README.md)、本文は [LICENSE.txt](../ImageRegions/LICENSE.txt) をcanonicalとして参照する。headerをコピーしない。gzipはfilenameなし・mtime=0・level9で保存し、展開JSONを併存させない。BCL `GZipStream` で読める。

## 原本抽出とadapter境界

| 抽出 | 行範囲 | 抜粋SHA-256 |
| --- | --- | --- |
| `original-types.inc` | ImgDiffBuffer 33–161 | `D25AA2C4DA9EEEADC2F6DC4CD47B5D107E1793E2D2E7A1ACCBD499E2BC25D0B5` |
| `original-modes.inc` | ImgDiffBuffer 479–487（overlay enum 482–484） | `D8234F7EB263BAEB0A43B7659D9211B6A134F9771A1D4FC4F259B74D17375D81` |
| `original-color.inc` | image 384–395 | `2C6A2F6ADD291118033865BA6413D1A13EB1EE1404E3CC503270D466C42361A5` |
| `original-functions.inc` | ImgDiffBuffer 1166–1218、1663–1700、1702–1964、1966–2053、2055–2125 | `CCFBB4AC89E353D53064B69772F1D37CAD3E451792B7F6E76F01DEC3EE89503A` |

範囲は `RefreshImages`、最大寸法／分類／描画初期化、比較／分類／`MarkDiff`、`WipeEffect`、`CopyPreprocessedImageToDiffImage`、`XorImages2`、`AlphaBlendImages2` を含む。AlphaBlend終端は2125行、ghost生成開始は2127行。抜粋をgoldenの `sourceExcerpts` と採取 `extraction.json` へ記録し、別Python実行でcanonical原本からSHAを再照合する。

adapter class名は `CImgDiffBuffer` とし、原本member function pointerを変更しない。最小Imageはwidth/height/scanLine/setSizeとBGRA Colorを持つ。setSizeは共通canvasを透明BGRA=0で初期化する。このpadding初期化はadapter境界で、FreeImageの新規bitmap生成を実測したものではない。

`UpdateDiffTransparencyCache` は呼出し回数だけ記録する画素非作用stub。FreeImage cacheの構築・利用・同等性は未検証。原本chrono分岐は削らずcompileするが、OVERLAY_ALPHABLEND_ANIM=3は入力対象外、blink=false固定。方向・ghost生成・復号・GUI・OS pointer/capture/focus/clipboardは実行しない。既知preprocessed BGRAを入力境界とする。

原本核で分類を生成し、NONE・強調なし・wipeなしの `RefreshImages` で初期canvasを採取した後、要求した静的overlay／強調／wipeで再度 `RefreshImages` を呼ぶ。原本 `m_imgPreprocessed` と分類の前後状態を直接出力する。`GetPixelColor` は使わない。

## ケースと観測契約

2/3pane、非対称2×3のBGRA字句入力。alpha0/128/255、完全透明hiddenRGB、.3/.5の中間切捨て、0/255の端を含む。

- NONE/XOR/ALPHA既定alpha=.3を、同寸法、1×2・2×3・2×1の寸法差padding、正offset `(0,0),(1,0),(0,1)` の各配置で採取。
- ALPHAのalpha0/.5/1、通常強調alpha0/.7/1、選択領域強調alpha0/.7/1、三者LeftOnlyの非対象pane保持を採取。
- active wipeはvertical y=0/1/端、horizontal x=0/1/端。原本Refresh内のoverlay→MarkDiff→wipe順を確認。
- 原本sourceは常にpreprocessed。2paneは1→0、0→1、3paneは1→0、0→1、2→1、1→2。中央dstは累積し、各ALPHA段階で整数byteへ切捨てる。
- XORはBGRのみ交換演算し、dst alphaを保持。ALPHAは全4channelをstraight doubleで補間し、hiddenRGBも計算。source支持矩形外は変更しない。

`cases.images` は寸法・offset・BGRA字句、設定はmode/overlayAlpha/showDifferences/highlightAlpha/selectedDiffIndex/wipeMode/wipePosition/blockSize/threshold。`expected` は原本観測のrawBefore/rawAfter/baseCanvas/processed（寸法・全bytes/base64/SHA）、classificationBefore/Afterと各SHA、position/oldPosition、cacheCallsを保持する。

## 独立検証と再現

先行失敗契約は [failure-contract.md](failure-contract.md)。採取処理は期待値を計算しない。別 `verify-reference.py` が標準libだけで画素ごとの閉じたscalar式を計算する。三者中央paneは `mix(mix(originalMiddle,originalLeft),originalRight)`、他paneは原本相手との単一mixで表し、原本scanline・incremental pane loopを移植しない。強調は原本観測の分類maskを使用するため、分類算法自体の独立検証とは主張しない。分類は前後の同一性と観測SHAを照合する。

2026-10-03にMSVC build/probe各exit=0、2採取のgzip bytes／payload bytes一致。独立検証1,483項目成功・0失敗。全出力BGRA、raw input保持、分類保持、元canvas、画素SHA、抜粋SHA、cache stub呼出し、wipe順を照合した。

```powershell
Import-Module 'C:/Program Files/Microsoft Visual Studio/18/Community/Common7/Tools/Microsoft.VisualStudio.DevShell.dll'
Enter-VsDevShell -VsInstallPath 'C:/Program Files/Microsoft Visual Studio/18/Community' -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null
python tests/Fixtures/ImageOverlays/generate-reference.py --output artifacts/local/image-overlay-fixture/reproduced1
python tests/Fixtures/ImageOverlays/generate-reference.py --output artifacts/local/image-overlay-fixture/reproduced2
python tests/Fixtures/ImageOverlays/verify-reference.py artifacts/local/image-overlay-fixture/reproduced1/winimerge-image-overlay-golden.json.gz artifacts/local/image-overlay-fixture/reproduced2/winimerge-image-overlay-golden.json.gz --output artifacts/local/image-overlay-fixture/reproduced-checks
Get-FileHash tests/Fixtures/ImageOverlays/winimerge-image-overlay-golden.json.gz
Get-FileHash artifacts/local/image-overlay-fixture/reproduced1/winimerge-image-overlay-golden.json.gz
```

MSVC14.51.36231と既存Python3だけを使う。compiler path／引数／実INCLUDE/LIBをmetadataに保存する。採取時の原本入力JSON／plain probe入力／出力JSONL／compiler・stderr logs／metadata／extraction SHA／goldenは `artifacts/local/image-overlay-fixture/{run1,run2}/`、独立scalar全画素は `independent-scalar-expectations.json`、照合結果は `independent-assertions.json` に保持する。使用済exe/obj/incはpath確認後削除し、清掃記録を同じ場所の `cleanup.json` に保持する。

固定版の明示更新時だけgeneratorに `--golden tests/Fixtures/ImageOverlays/winimerge-image-overlay-golden.json.gz` を付ける。通常build/E2EへMSVC/C++/FreeImageを追加しない。managed製品・GUI・HTML・AOTとの統合検証はこのfixture工程の外。
