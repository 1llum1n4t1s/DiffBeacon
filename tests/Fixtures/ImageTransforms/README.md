# WinIMerge 表示変換・領域コピー観測

正本は WinMerge/winimerge v1.0.54、revision `da639cdfaeca87aaad0eaceec509afa11ad61421` の配布x64 DLL。DLL SHA256 `36F2A726C34A2323D2E569903B4F1DC6C28ABF1C10447006335F2785FC511BA6`。Windowless public APIで原本のrotate/flip→CopyDiffAll→Undo/Redo→SaveImageAsを呼び、GetPixelColorで全BGRAを取得した観測を要約する。DLL・exe・大量の原本artifactはこのfixtureへ複製しない。

入力PNGと抽出utilityは自作CC0（[LICENSE.txt](LICENSE.txt)）。WinIMerge由来の原本はGPLv2、canonical licenseは [ImageRegions/LICENSE.txt](../ImageRegions/LICENSE.txt)（SHA `D3533C10B656BEC2782600B05B471ABE3AC916E228B27929CDA1F83C49D7E7A5`）。public header blob `d5c1a0c0bcf463fd7ef3237b18e4b7a60b4853fa`、header SHA `597902D5FFBE8585524C86C41E73032C607E42E04E65F90F0441E521F84DB4F4`。採取環境の `build/WinIMergeDllReference/WinIMergeLib.h` のbytesへblob検証を行い、新コピーは作らない。このDLL採取helperは通常build・固定fixture E2Eに不要で、今回の製品変更へ同梱しない。

原本範囲はoffset0、挿入削除NONE、静止3×2 BGRAの6pixels、角度0/90/180/270、horizontal/vertical flip。各pixel alphaは0/1/127/128/254/255、alpha0 hiddenRGBも含む。rawへ水平flip→垂直flip→反時計回り回転の表示順。raw保存・共有Undo/Redo・dirtyを維持する。内部regiongrid/op/rect/historycounterは公開観測がないためgoldenに作らない。

| 正本 | ケース／状態 | 観測 |
| --- | --- | --- |
| `E:/DiffBeacon-artifacts/reference/image-transforms-v1.0.54/run2` | 32／256 | 16 orientations、readonly true/false、2/3pane、表示→save→reset |
| 同 `copy-run1` | 256／2560 | source16×destination16、copy-all→Undo→Redo→raw保存 |

run2原本2320checks、copy-run1原本3584checksは各summaryで全成功。copy画素は原本DLLの観測そのものであり、抽出utilityは変換・コピー算法から期待値を生成しない。原本finalPNGは独立Python標準ライブラリPNG復号で全rawBGRAへ要約し、元PNGbytes/base64/SHAも保持する。E2Eでは別実装のBCL PNG復号を使う。readOnlyでも原本SaveImageAsが成功する観測と、CLIのreadonly export拒否は別契約。roケースは表示全状態・入力不変だけを照合し、export拒否を別に検査する。

goldenはcase actions、全state viewBGRA/base64/SHA/寸法/orientation/diff/conflict/undoable/redoable/modified/savepoint、各pane finalRaw PNG/BGRAを保持する。入力PNGは1個だけ共用。公式DLL APIから採取したBGRAを再配置せず読む。原本JSON、capture-source.py、probe.cpp、compiler.json、probe.exeのSHAはgolden.sourcesに集約する。

## 再生成

GUI自己検証へ同梱する `ui-golden.json.gz` は同じgoldenの可逆圧縮で、期待値を変更しない。`python build/Generate-ImageTransformUiFixture.py` で再生成する。71,270 bytes、SHA `8E346A734B17CBA97D76D037E2E6BB4F37D0EB175990AC0D76EDB9302C004352`。生成器は自作で入力・抽出utilityと同じCC0。UI読込み時にも展開上限と正本SHAを確認する。

配布ZIP/DLLを再取得せず既存原本採取artifactを使用する。既存`capture.py`と`capture-copy.py`が原本生成経路で、MSVC14.51とWindowsSDKのcompiler.jsonはrun2に保持。通常buildへのnative依存追加はない。抽出だけならPython標準ライブラリで実行できる。

```powershell
python tests/Fixtures/ImageTransforms/extract.py `
  --reference E:/DiffBeacon-artifacts/reference/image-transforms-v1.0.54 `
  --output E:/DiffBeacon-artifacts/local/image-transforms/fresh
```

fresh output必須、既存非空directory・link/reparseを拒否。extracted-run3/run4の2ファイル計2,458,803 bytesは全bytes一致、proofは `E:/DiffBeacon-artifacts/local/image-transforms/reproducibility.json`。同じ既存原本からの再抽出であり、今回native再実行はしていない。最初の原本case00のみ0操作となった試作root/case-00は利用しない。

- golden SHA `5383447EA3FF48F5C1F1A99F6CFFF8438568ED68445F5BB59FD4C2BC31FEFD82`（2,458,712 bytes）
- extractor SHA `52D9F9AD1F07A2E6CC6D5225278D1DCF72964B585E26FE97B001EE356638AA26`
- inputPNG SHA `FA80C8FC3A248A495BD0E099BF33E0859A7E2AA63F17FB96F7DF59491B0B6F0F`（91 bytes）
- inputBGRA SHA `92BB49540CB773FF1565F51BD541C41732BBF56A3C755A2121EDE142FC9E3E9B`
- CC0/license-reference file SHA `26E16FE3D2BF883D18D80E3B4708AA018F11C16CE40858C31AF62F6DF7DFCD20`

新E2Eは288ケース2,816原本状態を実`--image-copy`別プロセスへ照合する。正常exportはrawPNG全bytes、表示・コピー・Undo/Redo・dirty・orientation保持、readonly export/不正角度/flip値/pane/required field拒否も検査。通常GUIは代表14操作列と実ボタン、TIFFページ、設定保存・復元を自己検証する。通常CLI・単体／包装HTMLは同じ限定E2Eに含め、強調なしの主画像を共通canvasへ透明paddingした観測画素、原画の詳細を変換前の画素へ照合する。
