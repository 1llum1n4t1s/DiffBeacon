# 自作APNG fixture（CC0）

PNG第三版の[固定Recommendation](https://www.w3.org/TR/2025/REC-png-3-20250624/)の§4.9、§11.3.6、§6.2に沿うPNG8 RGBAの小画像。generator・画像・手書き期待値はCC0-1.0（[LICENSE.txt](LICENSE.txt)）。他の画像・decoder/compositorの出力を期待値に使わない。

有効APNG8個、単一frame PNG1個、壊れたAPNG/過大宣言7個。canvasは2×2または3×2、valid animationは2～4frame。無効入力は期待画素を持たず拒否専用に分離する。`expectations.json`は各fileのSHA/size、animation frameCountと各frameの全canvas BGRA8888 Unpremul/base64/SHAを持つ。default-excludedの白いIDAT画像はanimationの枚数・期待画素に含めない。

| 有効入力 | 手書き期待値の観点 |
| --- | --- |
| same-first-left/right | 赤い先頭は同一、後続は緑/青で差分。2frame |
| control-before-actl | same-first-leftの先頭fcTL/acTLだけ交換。seq0と赤→緑の期待値を保持 |
| default-excluded | animationに含まれない白default、青animation→右下緑 |
| offset-disposals | 3×2、offset矩形、NONE→BACKGROUND→PREVIOUS→次frame |
| source-over | 青にalpha128赤OVERはBGRA[127,0,128,255]、後続透明SOURCE |
| first-previous | 先頭PREVIOUSをBACKGROUNDとして処分、次の矩形外は透明黒 |
| hidden-source-first | 最初のSOURCE alpha0でもBGRA[13,37,71,0]を保持 |
| static-red | 赤単一frame、短い側のrepeat-last/枚数差用 |

acTLはIDATより前。§5.6 Table 7ではacTLとfcTLの相対順序は固定されず、先頭fcTL→acTL→IDATも合法。含まれるdefaultはfcTL→IDATでcanvas全域、含まれないdefaultはIDAT→fcTL→fdAT。fcTL/fdATは共有sequenceを0から連続増加する。SOURCEはRGBAを上書きし、OVERはalpha合成。BACKGROUNDは当該矩形を透明黒にし、PREVIOUSは描画前の内容へ戻す。出力は描画後・dispose前の全canvasを期待値として明示する。PNGは非premultiplied alphaを格納するため、透明SOURCEのencoded RGBを保持することも既存PNG原画契約として照合する。OVERの出力alpha0のRGBなど意味が曖昧な丸めの期待値は含めない。ICC/gAMA/EXIF/palette/interlace/16bit/color管理はこのfixture範囲外。

control-before-actl.pngは既存chunkのbytesとCRCを無変更で交換した合法変形。SHA256は `79CB5122CF11ED52BAA6A7D0CF527B6A66B8FB95EB172415E16D2EC94908107B`。再生成 `E:/DiffBeacon-artifacts/local/apng-port/reproduced-order-run4` と `reproduced-order-run5` の17ファイル・23,069bytesは全bytes一致し、元15画像のSHAと全asset metadataも変更前と一致した。新manifest SHAは `DDA56A7F2EE200D47F76FC5C96313A7A3E98EE2EEF3E12EE70E40108EE68FAE9`、generator SHAは `0C96723C855E762DE3362B51A7380D02914833351B2948182CBA92C40AE2D6AC`。実アプリの修正前exit2と修正後exit0・両フレームSHA一致は `E:/DiffBeacon-artifacts/local/apng-port/control-order` に分けて保持し、以前のbaseline summaryは変更しない。最終検証の範囲は[移行記録](../../../../Docs/MIGRATION.md#apngの合成フレーム比較)を参照する。

無効入力はsequence gap、fdAT CRC、canvas外rect、acTL枚数不一致、truncated fdAT、canvas16000001画素宣言、frame1025宣言。APNGを認識した後で静止PNGへfallbackせず、成功JSONなし・exit2で拒否する製品契約を期待する。過大宣言は数百bytesのままで、対応する巨大画素データは生成しない。

## 再生成

Python標準ライブラリだけを使い、新しい空の出力先を指定する。既存結果の上書きとlink/reparse経由を拒否する。

```powershell
& 'C:/Users/IMT/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' `
  tests/Fixtures/Images/Apng/generate.py --output E:/DiffBeacon-artifacts/local/apng-port/reproduced
```

再生成画像とJSONの全bytesを固定fixtureへ照合する。入力PNG/APNGは生成器の圧縮・chunk組立て、期待canvasは同じファイル内の別のliteral配列であり、復号・合成関数は生成器に存在しない。SHAは`expectations.json`に集約する。

2026-10-02の現Skia baselineではsame-first-left/rightを通常`--image`で比較するとexit0、両側frameCount1、different=falseで後続差分を見落とした。これはAPNG接続前のmanaged DLLの観測であり、後続の製品修正結果ではない。指定frame・malformedを含む実行command/exit/stdout/stderr、アプリSHA、全照合と再生成proofは `E:/DiffBeacon-artifacts/local/apng-port/baseline` に保持する。

初回反証時のDLL SHAは未記録。その後、親のAPNG接続とbuildを挟んで33回のCLIを観測し、実DLL SHA `26F79D9C7FDD3D61FF098F21D8F221871DA0BF3C8B74D7FD7D6250A6678A7859` の128検査が一致、7無効入力もexit2となった。この後続観測は旧Skiaの能力ではなく、APNG接続後の別段階としてsummaryに分離する。親の最終build後の全fixture検証は別に実施する。

fixture-run2/run3の画像15個と期待JSON（16ファイル・21,179bytes）が全bytes一致。固定19ファイルは計32KB程度で、生成器は既存出力への再実行を変更前に拒否した。link/reparse拒否はコードで確認し、fixture生成時は通常の絶対directoryを使用した（link経路の実測はしていない）。
